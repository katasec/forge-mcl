using System.CommandLine;
using System.Net.Http.Headers;
using ForgeMission.Application;
using ForgeMission.Application.Transport;
using ForgeMission.Conversations.Contracts;
using ForgeMission.Core.Resolution;
using ForgeMission.Core.Tools;
using ForgeMission.Cli.Tui;
using ForgeMission.Cli.Tui.Graphics;
using Microsoft.Extensions.DependencyInjection;
// Transport and Contracts both name these; forge chat uses the surface (Transport) side.
using CreateMissionConversationRequest = ForgeMission.Application.Transport.CreateMissionConversationRequest;
using ListMissionConversationsRequest = ForgeMission.Application.Transport.ListMissionConversationsRequest;

namespace ForgeMission.Cli;

// forge chat (53.2, 53.4, 53.5): a chat with the naked Chat mission (one expert on Claude) in the
// default Project — a full-screen TUI on a terminal (Tui/ChatTui), otherwise a plain type-and-print
// loop (acceptance scripts pipe it). Everything below the loop is an existing Katasec.Forge.Client call
// through ApplicationComposition: Project create/open, mission authoring, and the mission-conversation
// messages on ForgeAPI. This file owns only the order of those calls and what is printed.
// `forge chat --hands` (Phase 55) runs the separate ChatHands mission instead: the model may read,
// write and edit files in the project folder through Bob, after a one-time approval per project.
// The TUI needs a terminal that shows kitty images (Phase 56 G8), checked in two stages with one
// message and no plain fallback: the environment before sign-in or any network call, and the cell
// size on the TUI's first tick (ChatTui), where XenoAtom already owns terminal input.
public static class ForgeChat
{
    private const string ProjectTitle = "Chat";
    // Unchanged since 53.2: an existing default Project must keep resolving and opening as before.
    private const string ProjectGoal = "Chat with Janus from the forge CLI.";
    private const string HandsFlag = "--hands";
    private static readonly TimeSpan EvaluationPollDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(250);
    private const string NeedsImagesMessage = "forge chat needs a terminal that can show images, such as Ghostty or Kitty " +
        "(not inside tmux). Open forge chat again from one of those.";

    /// <summary>The <c>forge chat</c> command and its one flag.</summary>
    internal static Command BuildCommand()
    {
        var cmd = new Command("chat", "Chat in your default Forge project (--hands: let it read and edit files there)");
        cmd.Add(new Option<bool>(HandsFlag)
        {
            Description = "Let the model read, write and edit files in the chat project folder (asks once per project)",
        });
        cmd.SetAction(async result => await RunAsync(Hands(result)));
        return cmd;
    }

    public static async Task<int> RunAsync(bool hands)
    {
        // Read before any network call, on both paths: a bad config stops here either way.
        ForgeTheme theme;
        try { theme = ForgeConfig.ReadTheme(ForgeConfig.DefaultPath); }
        catch (ForgeConfigException bad)
        {
            Console.Error.WriteLine($"forge chat: {bad.Message}");
            return 1;
        }

        var interactive = UsesTui(Console.IsInputRedirected, Console.IsOutputRedirected);
        if (interactive && !TerminalFacts.ShowsImages(TerminalFacts.Environment()))
        {
            Console.Error.WriteLine(NeedsImagesMessage);
            return 1;
        }

        // The TUI's embedded fonts (Phase 56 Task 4), before any network call: one that is missing
        // or unreadable stops here. The line mode draws no images and loads none.
        TextFonts? fonts = null;
        if (interactive && (fonts = LoadFonts(Console.Error)) is null)
            return 1;

        var platform = CredentialStore.GetPlatform();
        if (platform is null || string.IsNullOrEmpty(platform.Key))
        {
            Console.Error.WriteLine("Not signed in. Run `forge login`.");
            return 1;
        }

        var services = new ServiceCollection();
        services.AddHttpClient("conversation-host", client =>
        {
            client.BaseAddress = new Uri(ForgeExec.ApiEndpoint + "/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", platform.Key);
        });
        await using var provider = services.BuildServiceProvider();
        await using var app = ApplicationComposition.Create(provider.GetRequiredService<IHttpClientFactory>(),
            null, PolicyFor(hands), _ => { }, CancellationToken.None);

        try
        {
            return await ChatInDefaultProjectAsync(app, ModeFor(hands), interactive, theme, fonts);
        }
        catch (ChatStoppedException stopped)
        {
            Console.Error.WriteLine($"chat stopped: {stopped.Message}");
            return 1;
        }
        catch (HttpRequestException failure)
        {
            Console.Error.WriteLine($"chat failed: {failure.Message}");
            return 1;
        }
        // S2b: the line mode does not reconnect, so a lost stream stops it. Every call outside a turn
        // passes CancellationToken.None and a turn's own Ctrl-C ends in ChatAsync, so a cancellation
        // that reaches here is never the user's.
        catch (Exception failure) when (failure is OperationCanceledException or IOException)
        {
            return ReportConnectionLost(failure, Console.Error);
        }
    }

    /// <summary>Loads the TUI's embedded fonts; a missing or unreadable one is reported on
    /// <paramref name="error"/> as <c>forge chat: …</c> and gives null (exit 1).</summary>
    internal static TextFonts? LoadFonts(TextWriter error, Func<TextFonts>? load = null)
    {
        try { return (load ?? TextFonts.LoadEmbedded)(); }
        catch (Exception bad) when (bad is FontMissingException or InvalidDataException)
        {
            error.WriteLine($"forge chat: {bad.Message}");
            return null;
        }
    }

    /// <summary>The line mode's lost connection (S2b): one error line, exit 1.</summary>
    internal static int ReportConnectionLost(Exception failure, TextWriter error)
    {
        error.WriteLine($"chat failed: connection lost ({failure.Message})");
        return 1;
    }

    /// <summary>Opens the default Project, gates hands on the one-time approval, makes sure the
    /// mode's mission is published, opens its conversation, attaches hands, and runs the chat:
    /// the TUI on a terminal, otherwise the line mode.</summary>
    private static async Task<int> ChatInDefaultProjectAsync(ApplicationComposition app, ChatMode mode, bool interactive,
        ForgeTheme theme, TextFonts? fonts)
    {
        var session = await OpenDefaultProjectAsync(app.Projects);
        if (mode.HasHands && !await HandsAllowedAsync(app.MissionConversations, session, interactive))
            return 1;

        var mission = await EnsureMissionAsync(app, session.SessionId, mode);
        var (conversationId, version) = await OpenConversationAsync(app.MissionConversations, session.SessionId, mission, mode);
        await using var hands = mode.HasHands
            ? await AttachHandsAsync(app.MissionHands, session.SessionId, conversationId, mission)
            : null;
        if (!interactive || fonts is null)
            return await ChatAsync(app.MissionConversations, conversationId, hands);

        var header = new ChatHeader(Path.GetFileName(session.Project.Home), mode.MissionName, version, ChatProfile(mode),
            Environment.UserName);
        if (await ChatTui.RunAsync(app.MissionConversations, conversationId, header, theme, fonts, hands) == TuiExit.Quit)
            return 0;
        Console.Error.WriteLine(NeedsImagesMessage);
        return 1;
    }

    // ── Project ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The default Project lives at the home a draft proposes for its title, under Forge's
    /// own projects root. Open it; create it there only when that directory does not exist.</summary>
    private static async Task<ProjectSession> OpenDefaultProjectAsync(IProjectService projects)
    {
        var draft = await projects.DraftAsync(new ProjectDraftRequest(ProjectGoal, ProjectTitle), CancellationToken.None);
        var home = draft.Draft?.HomePath ?? throw Stopped(draft.Error);

        var opened = await projects.OpenAsync(new ProjectOpenRequest(home), CancellationToken.None);
        if (opened.Error?.Code == ProjectOperationErrorCode.HomeNotFound)
            opened = await projects.CreateAsync(new ProjectCreateRequest(ProjectGoal, ProjectTitle, home), CancellationToken.None);

        if (opened.Outcome == ProjectOperationOutcome.GoalRequired)
            throw new ChatStoppedException($"{home} exists but is not a Forge project.");
        var session = opened.Session ?? throw Stopped(opened.Error);
        Console.WriteLine($"Project: {session.Project.Home}");
        return session;
    }

    // ── Mission ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Returns the approved version of the mode's mission. On first use, runs the Desktop's authoring
    /// sequence (draft → promote → add a case → evaluate → publish), each step chosen from the
    /// Project's current authoring state, so an interrupted first use resumes where it stopped.
    /// A case that failed on an earlier launch is re-run once; a failure in this launch stops.</summary>
    private static async Task<ApprovedMissionVersionOption> EnsureMissionAsync(ApplicationComposition app, string sessionId, ChatMode mode)
    {
        var ranThisLaunch = false;
        while (true)
        {
            if (await FindApprovedAsync(app.MissionConversations, sessionId, mode) is { } approved)
                return approved;

            var missions = await ReadAuthoringAsync(app.MissionAuthoring, sessionId, null);
            var summary = missions.Missions.FirstOrDefault(item => item.Name == mode.MissionName);
            if (summary is null)
            {
                Console.WriteLine($"First use: publishing {mode.MissionName} in this project (one evaluation run).");
                Check(await app.MissionAuthoring.CreateDraftAsync(
                    new CreateMissionDraftRequest(sessionId, mode.MissionName, mode.Definition, mode.Profile), CancellationToken.None));
                continue;
            }

            var document = (await ReadAuthoringAsync(app.MissionAuthoring, sessionId, summary.MissionId)).Open
                ?? throw new ChatStoppedException($"{mode.MissionName} could not be opened for authoring.");
            ranThisLaunch = await AdvanceAsync(app.MissionAuthoring, sessionId, document, mode.MissionName, ranThisLaunch);
        }
    }

    /// <summary>Takes the one next authoring step for <paramref name="document"/>. Returns whether
    /// an evaluation has been started in this launch.</summary>
    private static async Task<bool> AdvanceAsync(IMissionAuthoringService authoring, string sessionId,
        MissionAuthoringDocument document, string missionName, bool ranThisLaunch)
    {
        if (document.Editable == MissionEditableKind.Draft)
        {
            Check(await authoring.PromoteCandidateAsync(
                new PromoteMissionCandidateRequest(sessionId, document.MissionId, document.DraftId!.Value, document.Revision), CancellationToken.None));
            return ranThisLaunch;
        }

        if (document.MissionVersionId is not { } versionId)
            throw new ChatStoppedException($"{missionName} has no candidate version to publish.");

        if (document.Cases.Count == 0)
        {
            Check(await authoring.AddCaseAsync(new AddEvaluationCaseRequest(sessionId, document.MissionId, versionId,
                new EvaluationCaseInput("Say hello.", null, null, EvaluationOutcomeView.Succeeded, null)), CancellationToken.None));
            return ranThisLaunch;
        }

        var open = document.Cases.FirstOrDefault(item => item.ResultState != EvaluationResultStateView.Passed);
        if (open is null)
        {
            Check(await authoring.PublishAsync(new PublishMissionVersionRequest(sessionId, document.MissionId, versionId), CancellationToken.None));
            Console.WriteLine($"{missionName} published.");
            return ranThisLaunch;
        }

        switch (open.ResultState)
        {
            case EvaluationResultStateView.Pending:
                await Task.Delay(EvaluationPollDelay);
                return ranThisLaunch;
            case EvaluationResultStateView.Failed when ranThisLaunch:
                throw new ChatStoppedException($"{missionName} evaluation failed: {open.ObservedSummary}");
            case EvaluationResultStateView.Failed:
                Console.WriteLine($"The previous {missionName} evaluation failed: {open.ObservedSummary}");
                Console.WriteLine("Running it again.");
                break;
            default:
                Console.WriteLine($"Evaluating {missionName}…");
                break;
        }

        Check(await authoring.RunCaseAsync(
            new RunEvaluationCaseRequest(sessionId, document.MissionId, versionId, open.EvaluationCaseId), CancellationToken.None));
        return true;
    }

    private static async Task<ApprovedMissionVersionOption?> FindApprovedAsync(
        IMissionConversationService conversations, string sessionId, ChatMode mode)
    {
        var approved = await conversations.ListApprovedVersionsAsync(new ListApprovedMissionVersionsRequest(sessionId), CancellationToken.None);
        return (approved.Options ?? throw Stopped(approved.Error)).FirstOrDefault(item => item.MissionName == mode.MissionName);
    }

    // ── Hands (Phase 55) ────────────────────────────────────────────────────────────────────

    /// <summary>H3: hands need the published ChatHands version, which is the approval. Without it,
    /// an interactive run asks once in plain text before the TUI starts; a piped run stops.</summary>
    private static async Task<bool> HandsAllowedAsync(IMissionConversationService conversations, ProjectSession session, bool interactive)
    {
        if (await FindApprovedAsync(conversations, session.SessionId, ChatMode.Hands) is not null)
            return true;

        var folder = session.Project.Home;
        switch (AskApproval(folder, interactive, Console.In, Console.Out))
        {
            case HandsApproval.Approved:
                return true;
            case HandsApproval.NotInteractive:
                Console.Error.WriteLine($"forge chat --hands: file access in {folder} is not allowed yet. " +
                    "Run `forge chat --hands` in a terminal once to allow it.");
                return false;
            default:
                Console.Error.WriteLine("File access not allowed; nothing changed.");
                return false;
        }
    }

    /// <summary>H5: acknowledges the approved ChatHands launch for this conversation, which attaches
    /// Bob in the project folder. A refusal stops the chat.</summary>
    private static async Task<ChatHandsAttachment> AttachHandsAsync(IMissionHandsConversationService hands, string sessionId,
        Guid conversationId, ApprovedMissionVersionOption mission)
    {
        var acknowledged = await hands.AcknowledgeAsync(new AcknowledgeMissionHandsRequest(sessionId, conversationId,
            mission.MissionVersionId, mission.VersionNumber, mission.DefinitionHash, ProfileAccepted: true), CancellationToken.None);
        var attachmentId = acknowledged.AttachmentId
            ?? throw new ChatStoppedException($"file access could not start: {acknowledged.Error ?? "Forge returned no attachment."}");
        return new ChatHandsAttachment(hands, sessionId, conversationId, attachmentId);
    }

    private static async Task<MissionAuthoringProjection> ReadAuthoringAsync(IMissionAuthoringService authoring, string sessionId, Guid? missionId)
    {
        var response = await authoring.GetAsync(new GetMissionAuthoringRequest(sessionId, missionId), CancellationToken.None);
        return response.Authoring ?? throw Stopped(response.Error);
    }

    // ── Conversation ────────────────────────────────────────────────────────────────────────

    /// <summary>Reopens this Project's most recent conversation on the mode's mission, so Chat and
    /// ChatHands each keep their own history; creates one on it only when the mode has none yet
    /// (conversations on other missions such as Janus are left stored). Returns the conversation and
    /// the version it runs on.</summary>
    private static async Task<(Guid ConversationId, int Version)> OpenConversationAsync(
        IMissionConversationService conversations, string sessionId, ApprovedMissionVersionOption mission, ChatMode mode)
    {
        var listed = await conversations.ListAsync(new ListMissionConversationsRequest(sessionId), CancellationToken.None);
        var latest = LatestFor(listed.Conversations ?? throw Stopped(listed.Error), item => item.MissionName, mode.MissionName);
        if (latest is not null)
            return (latest.ConversationId, latest.VersionNumber);

        var created = await conversations.CreateAsync(
            new CreateMissionConversationRequest(sessionId, mission.MissionId, Guid.NewGuid(), mission.MissionVersionId), CancellationToken.None);
        return ((created.Created ?? throw Stopped(created.Error)).ConversationId, mission.VersionNumber);
    }

    /// <summary>The provider profile the mode's definition pins (<c>using anthropic</c>), read from the
    /// definition itself so the header never names a model the deployment may change.</summary>
    private static string ChatProfile(ChatMode mode)
    {
        var program = ForgeMission.Parser.MclParser.Parse(mode.Definition);
        var chat = program.Declarations.OfType<ForgeMission.Parser.MissionDeclaration>().Single(item => item.Name == mode.MissionName);
        var step = chat.Pipeline.Elements.OfType<ForgeMission.Parser.StepElement>().First().Step;
        return step.Using ?? "default";
    }

    /// <summary>Prints the history, follows a turn that is still running, then reads a line,
    /// submits it, and prints the reply until the turn ends. Ctrl-D exits; Ctrl-C during a turn
    /// cancels it (and a running file operation) and exits. With hands, each live hands request is
    /// executed off the follow loop.</summary>
    private static async Task<int> ChatAsync(IMissionConversationService conversations, Guid conversationId, ChatHandsAttachment? hands)
    {
        var snapshot = (await conversations.GetConversationAsync(conversationId, CancellationToken.None)).Snapshot;
        var cursor = await ReplayAsync(conversations, conversationId, 0, snapshot.LastSequence, item => Print(item, replay: true), CancellationToken.None);
        hands?.Begin(message => Console.WriteLine($"error: {message}"));
        Action<ConversationEvent> live = item =>
        {
            hands?.OnEvent(item);
            LivePrint(item);
        };
        if (snapshot.ActiveRunId is { } running && !IsTerminal(snapshot.Status))
            cursor = await FollowTurnAsync(conversations, conversationId, cursor, running, includeDeltas: false, live, CancellationToken.None);

        while (true)
        {
            Console.Write("you> ");
            if (Console.ReadLine() is not { } line) return 0;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var turn = await conversations.SubmitAsync(conversationId, Guid.NewGuid(), line, CancellationToken.None);
            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler stop = (_, e) => { e.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += stop;
            try
            {
                cursor = await FollowTurnAsync(conversations, conversationId, cursor, turn.TurnAttemptId, includeDeltas: false, live, cancel.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                if (hands is not null) await hands.CancelInFlightAsync();
                await conversations.CancelAsync(conversationId, turn.TurnId, turn.TurnAttemptId, Guid.NewGuid(), CancellationToken.None);
                Console.WriteLine("Cancelled.");
                return 130;
            }
            finally
            {
                Console.CancelKeyPress -= stop;
            }
        }
    }

    /// <summary>Shows the stored events after <paramref name="after"/> up to <paramref name="lastSequence"/>
    /// (all of them from 0, or a catch-up from a saved cursor); returns the cursor.</summary>
    internal static async Task<long> ReplayAsync(IMissionConversationService conversations, Guid conversationId, long after,
        long lastSequence, Action<ConversationEvent> show, CancellationToken ct)
    {
        var cursor = after;
        if (lastSequence <= after) return cursor;
        await foreach (var item in conversations.StreamEventsAsync(conversationId, after, includeDeltas: false, ct))
        {
            if (item.Sequence <= cursor) continue;
            show(item);
            cursor = item.Sequence;
            if (cursor >= lastSequence) break;
        }
        return cursor;
    }

    /// <summary>Shows events after <paramref name="cursor"/> until the run for
    /// <paramref name="attemptId"/> ends (see <see cref="StreamAsync"/>); a transport failure is thrown.</summary>
    internal static Task<long> FollowTurnAsync(IMissionConversationService conversations, Guid conversationId, long cursor,
        Guid attemptId, bool includeDeltas, Action<ConversationEvent> show, CancellationToken ct) =>
        StreamAsync(conversations, conversationId, cursor, includeDeltas, show,
            item => EndsTurn(item.Kind, item.RunId, item.RunStatus, attemptId), ended: null, ct);

    /// <summary>The one stream loop (the line mode's turn follow and the TUI's live stream, 53.9 L1):
    /// shows events after <paramref name="cursor"/> until <paramref name="ends"/> accepts one, and
    /// returns the cursor. Without <paramref name="ended"/> (the line mode), a stream that closes is
    /// reopened from the cursor after <see cref="ReconnectDelay"/> and a transport failure is thrown.
    /// With it (the TUI), every end is passed to it (null for a close, else the transport failure)
    /// and it decides: true reopens after the delay, false returns the cursor (idle sleep, Phase 57
    /// S5). A cancellation that is not <paramref name="ct"/>'s (HttpClient's timeout waiting for
    /// headers, Phase 57 S2) is a transport failure; the session's own cancellation always ends the loop. With
    /// <paramref name="includeDeltas"/> (the TUI, Phase 53.8), live reply deltas are shown too; a
    /// delta never moves the cursor, and it is shown only once a step has started on the same
    /// connection — a step joined mid-reply (reopened or reconnected) shows no partial text until
    /// its final message.</summary>
    internal static async Task<long> StreamAsync(IMissionConversationService conversations, Guid conversationId, long cursor,
        bool includeDeltas, Action<ConversationEvent> show, Func<ConversationEvent, bool> ends, Func<Exception?, bool>? ended,
        CancellationToken ct)
    {
        while (true)
        {
            var stepStartedHere = false;
            Exception? failure = null;
            try
            {
                await foreach (var item in conversations.StreamEventsAsync(conversationId, cursor, includeDeltas, ct))
                {
                    if (item.Kind == ConversationEventKind.ParticipantDelta)
                    {
                        if (stepStartedHere) show(item);
                        continue;
                    }

                    if (item.Sequence <= cursor) continue;
                    stepStartedHere |= item.Kind == ConversationEventKind.ParticipantStarted;
                    cursor = item.Sequence;
                    show(item);
                    if (ends(item)) return cursor;
                }
            }
            catch (Exception caught) when (ended is not null && IsTransportFailure(caught, ct))
            {
                failure = caught;
            }
            if (ended is not null && !ended(failure)) return cursor;
            await Task.Delay(ReconnectDelay, ct);
        }
    }

    /// <summary>A failure of the connection itself, not of <paramref name="ct"/>'s session.</summary>
    internal static bool IsTransportFailure(Exception failure, CancellationToken ct) =>
        !ct.IsCancellationRequested && failure is HttpRequestException or IOException or OperationCanceledException;

    // ── Output and rules ────────────────────────────────────────────────────────────────────

    private static void LivePrint(ConversationEvent item) => Print(item, replay: false);

    private static void Print(ConversationEvent item, bool replay)
    {
        switch (item.Kind)
        {
            case ConversationEventKind.UserMessage when replay:
                Console.WriteLine($"you> {item.Text}");
                break;
            case ConversationEventKind.ParticipantStarted:
                Console.WriteLine($"[{item.Text}]");
                break;
            case ConversationEventKind.ParticipantMessage:
                Console.WriteLine(item.Text);
                Console.WriteLine();
                break;
            case ConversationEventKind.Error:
                Console.WriteLine($"error: {item.Reason ?? item.Text}");
                break;
            // H8: one line per tool use; the outcome completes the line its request started.
            case ConversationEventKind.MissionHandsRequested:
                Console.Write(Transcript.HandsLabel(item));
                break;
            case ConversationEventKind.MissionHandsResult or ConversationEventKind.MissionHandsCancelled or
                ConversationEventKind.MissionHandsInterrupted:
                Console.WriteLine($" → {Transcript.HandsOutcome(item)}");
                break;
            case ConversationEventKind.RunStatus when item.RunStatus is { } status && IsTerminal(status) && status != ConversationRunStatus.Completed:
                Console.WriteLine($"(run {status.ToString().ToLowerInvariant()})");
                break;
        }
    }

    /// <summary>The TUI needs a terminal on both ends; piped input or output keeps the line mode.</summary>
    internal static bool UsesTui(bool inputRedirected, bool outputRedirected) => !inputRedirected && !outputRedirected;

    /// <summary>The latest conversation is reopened only when it is on the mode's mission; a model
    /// and a hands profile are pinned at create, so a conversation on another mission (Janus, or
    /// the other chat mode) is never continued.</summary>
    /// <summary>The first conversation in <paramref name="listed"/> (listed newest-first) on
    /// <paramref name="mission"/>, or null when there is none.</summary>
    internal static T? LatestFor<T>(IEnumerable<T> listed, Func<T, string?> missionName, string mission) where T : class =>
        listed.FirstOrDefault(item => string.Equals(missionName(item), mission, StringComparison.Ordinal));

    private static bool Hands(ParseResult result) => result.GetValue<bool>(HandsFlag);

    /// <summary>Whether <paramref name="args"/> (after <c>chat</c>) ask for hands.</summary>
    internal static bool ParseHands(string[] args) => Hands(BuildCommand().Parse(args));

    internal static ChatMode ModeFor(bool hands) => hands ? ChatMode.Hands : ChatMode.Plain;

    /// <summary>Plain chat denies every capability. Hands auto-approves the file capability only
    /// (H3: the one-time approval already covered it); the terminal stays denied.</summary>
    internal static CapabilityAuthorizationPolicy PolicyFor(bool hands) => hands
        ? new([new KeyValuePair<string, CapabilityAuthorizationRule>("file", new CapabilityAuthorizationRule(AuthorizationOutcome.AutoApproved))], null)
        : new([], null);

    /// <summary>H3's question, asked only on a terminal. Only <c>y</c> or <c>yes</c> approve;
    /// anything else, including end of input, declines.</summary>
    internal static HandsApproval AskApproval(string folder, bool interactive, TextReader input, TextWriter output)
    {
        if (!interactive) return HandsApproval.NotInteractive;
        output.Write($"Allow Forge to read, write and edit files in {folder}? [y/N] ");
        var answer = input.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
            ? HandsApproval.Approved
            : HandsApproval.Declined;
    }

    /// <summary>A turn ends at a terminal run status for its own attempt; the Host stamps each
    /// turn's events with the attempt ID as <c>RunId</c>.</summary>
    internal static bool EndsTurn(ConversationEventKind kind, Guid? runId, ConversationRunStatus? status, Guid attemptId) =>
        kind == ConversationEventKind.RunStatus && runId == attemptId && status is { } value && IsTerminal(value);

    internal static bool IsTerminal(ConversationRunStatus status) => status is
        ConversationRunStatus.Completed or ConversationRunStatus.Rejected or
        ConversationRunStatus.Interrupted or ConversationRunStatus.Failed;

    private static void Check(MissionAuthoringMutationResponse response)
    {
        if (response.Error is { } error) throw Stopped(error);
    }

    private static ChatStoppedException Stopped(ProjectOperationError? error) =>
        new(error?.Message ?? "Forge returned no result.");

    private sealed class ChatStoppedException(string message) : Exception(message);
}

internal enum HandsApproval { Approved, Declined, NotInteractive }

/// <summary>The two chat missions (H4). A conversation's hands profile is pinned at create, so each
/// mode has its own mission: plain <c>Chat</c> (no hands) and <c>ChatHands</c> (project files).</summary>
internal sealed record ChatMode(string MissionName, string Definition, MissionHandsProfile Profile)
{
    public static ChatMode Plain { get; } = new(StarterMissions.Chat, StarterMissions.ChatDefinition, MissionHandsProfile.NoHands);

    public static ChatMode Hands { get; } =
        new(StarterMissions.ChatHands, StarterMissions.ChatHandsDefinition, MissionHandsProfile.ProjectWorkspace);

    public bool HasHands => Profile != MissionHandsProfile.NoHands;
}
