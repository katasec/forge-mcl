using System.Net.Http.Headers;
using ForgeMission.Application;
using ForgeMission.Application.Transport;
using ForgeMission.Conversations.Contracts;
using ForgeMission.Core.Resolution;
using ForgeMission.Core.Tools;
using ForgeMission.Cli.Tui;
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
public static class ForgeChat
{
    private const string ProjectTitle = "Chat";
    // Unchanged since 53.2: an existing default Project must keep resolving and opening as before.
    private const string ProjectGoal = "Chat with Janus from the forge CLI.";
    // The definition pins the model (`using anthropic`), so a conversation stays on the mission it
    // was created on.
    private const string MissionName = StarterMissions.Chat;
    private static readonly TimeSpan EvaluationPollDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(250);

    public static async Task<int> RunAsync()
    {
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
            null, new CapabilityAuthorizationPolicy([], null), _ => { }, CancellationToken.None);

        try
        {
            var session = await OpenDefaultProjectAsync(app.Projects);
            var mission = await EnsureMissionAsync(app, session.SessionId);
            var (conversationId, version) = await OpenConversationAsync(app.MissionConversations, session.SessionId, mission);
            if (!UsesTui(Console.IsInputRedirected, Console.IsOutputRedirected))
                return await ChatAsync(app.MissionConversations, conversationId);

            var header = new ChatHeader(Path.GetFileName(session.Project.Home), MissionName, version, ChatProfile());
            return await ChatTui.RunAsync(app.MissionConversations, conversationId, header);
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

    /// <summary>Returns the approved Chat version. On first use, runs the Desktop's authoring
    /// sequence (draft → promote → add a case → evaluate → publish), each step chosen from the
    /// Project's current authoring state, so an interrupted first use resumes where it stopped.
    /// A case that failed on an earlier launch is re-run once; a failure in this launch stops.</summary>
    private static async Task<ApprovedMissionVersionOption> EnsureMissionAsync(ApplicationComposition app, string sessionId)
    {
        var ranThisLaunch = false;
        while (true)
        {
            if (await FindApprovedAsync(app.MissionConversations, sessionId) is { } approved)
                return approved;

            var missions = await ReadAuthoringAsync(app.MissionAuthoring, sessionId, null);
            var summary = missions.Missions.FirstOrDefault(item => item.Name == MissionName);
            if (summary is null)
            {
                Console.WriteLine($"First use: publishing {MissionName} in this project (one evaluation run).");
                Check(await app.MissionAuthoring.CreateDraftAsync(
                    new CreateMissionDraftRequest(sessionId, MissionName, StarterMissions.ChatDefinition, MissionHandsProfile.NoHands), CancellationToken.None));
                continue;
            }

            var document = (await ReadAuthoringAsync(app.MissionAuthoring, sessionId, summary.MissionId)).Open
                ?? throw new ChatStoppedException($"{MissionName} could not be opened for authoring.");
            ranThisLaunch = await AdvanceAsync(app.MissionAuthoring, sessionId, document, ranThisLaunch);
        }
    }

    /// <summary>Takes the one next authoring step for <paramref name="document"/>. Returns whether
    /// an evaluation has been started in this launch.</summary>
    private static async Task<bool> AdvanceAsync(
        IMissionAuthoringService authoring, string sessionId, MissionAuthoringDocument document, bool ranThisLaunch)
    {
        if (document.Editable == MissionEditableKind.Draft)
        {
            Check(await authoring.PromoteCandidateAsync(
                new PromoteMissionCandidateRequest(sessionId, document.MissionId, document.DraftId!.Value, document.Revision), CancellationToken.None));
            return ranThisLaunch;
        }

        if (document.MissionVersionId is not { } versionId)
            throw new ChatStoppedException($"{MissionName} has no candidate version to publish.");

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
            Console.WriteLine($"{MissionName} published.");
            return ranThisLaunch;
        }

        switch (open.ResultState)
        {
            case EvaluationResultStateView.Pending:
                await Task.Delay(EvaluationPollDelay);
                return ranThisLaunch;
            case EvaluationResultStateView.Failed when ranThisLaunch:
                throw new ChatStoppedException($"{MissionName} evaluation failed: {open.ObservedSummary}");
            case EvaluationResultStateView.Failed:
                Console.WriteLine($"The previous {MissionName} evaluation failed: {open.ObservedSummary}");
                Console.WriteLine("Running it again.");
                break;
            default:
                Console.WriteLine($"Evaluating {MissionName}…");
                break;
        }

        Check(await authoring.RunCaseAsync(
            new RunEvaluationCaseRequest(sessionId, document.MissionId, versionId, open.EvaluationCaseId), CancellationToken.None));
        return true;
    }

    private static async Task<ApprovedMissionVersionOption?> FindApprovedAsync(IMissionConversationService conversations, string sessionId)
    {
        var approved = await conversations.ListApprovedVersionsAsync(new ListApprovedMissionVersionsRequest(sessionId), CancellationToken.None);
        return (approved.Options ?? throw Stopped(approved.Error)).FirstOrDefault(item => item.MissionName == MissionName);
    }

    private static async Task<MissionAuthoringProjection> ReadAuthoringAsync(IMissionAuthoringService authoring, string sessionId, Guid? missionId)
    {
        var response = await authoring.GetAsync(new GetMissionAuthoringRequest(sessionId, missionId), CancellationToken.None);
        return response.Authoring ?? throw Stopped(response.Error);
    }

    // ── Conversation ────────────────────────────────────────────────────────────────────────

    /// <summary>Reopens this Project's most recent mission conversation when it is on Chat;
    /// otherwise (none yet, or the latest is on another mission such as Janus) creates one on Chat.
    /// Returns the conversation and the Chat version it runs on.</summary>
    private static async Task<(Guid ConversationId, int Version)> OpenConversationAsync(
        IMissionConversationService conversations, string sessionId, ApprovedMissionVersionOption mission)
    {
        var listed = await conversations.ListAsync(new ListMissionConversationsRequest(sessionId), CancellationToken.None);
        var latest = (listed.Conversations ?? throw Stopped(listed.Error)).FirstOrDefault();
        if (ReusesLatest(latest?.MissionName))
            return (latest!.ConversationId, latest.VersionNumber);

        var created = await conversations.CreateAsync(
            new CreateMissionConversationRequest(sessionId, mission.MissionId, Guid.NewGuid(), mission.MissionVersionId), CancellationToken.None);
        return ((created.Created ?? throw Stopped(created.Error)).ConversationId, mission.VersionNumber);
    }

    /// <summary>The provider profile the Chat definition pins (<c>using anthropic</c>), read from the
    /// definition itself so the header never names a model the deployment may change.</summary>
    private static string ChatProfile()
    {
        var program = ForgeMission.Parser.MclParser.Parse(StarterMissions.ChatDefinition);
        var chat = program.Declarations.OfType<ForgeMission.Parser.MissionDeclaration>().Single(item => item.Name == MissionName);
        var step = chat.Pipeline.Elements.OfType<ForgeMission.Parser.StepElement>().First().Step;
        return step.Using ?? "default";
    }

    /// <summary>Prints the history, follows a turn that is still running, then reads a line,
    /// submits it, and prints the reply until the turn ends. Ctrl-D exits; Ctrl-C during a turn
    /// cancels it and exits.</summary>
    private static async Task<int> ChatAsync(IMissionConversationService conversations, Guid conversationId)
    {
        var snapshot = (await conversations.GetConversationAsync(conversationId, CancellationToken.None)).Snapshot;
        var cursor = await ReplayAsync(conversations, conversationId, snapshot.LastSequence, item => Print(item, replay: true), CancellationToken.None);
        if (snapshot.ActiveRunId is { } running && !IsTerminal(snapshot.Status))
            cursor = await FollowTurnAsync(conversations, conversationId, cursor, running, LivePrint, CancellationToken.None);

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
                cursor = await FollowTurnAsync(conversations, conversationId, cursor, turn.TurnAttemptId, LivePrint, cancel.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
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

    /// <summary>Shows every stored event up to <paramref name="lastSequence"/>; returns the cursor.</summary>
    internal static async Task<long> ReplayAsync(IMissionConversationService conversations, Guid conversationId, long lastSequence,
        Action<ConversationEvent> show, CancellationToken ct)
    {
        var cursor = 0L;
        if (lastSequence == 0) return cursor;
        await foreach (var item in conversations.StreamEventsAsync(conversationId, 0, ct))
        {
            show(item);
            cursor = item.Sequence;
            if (cursor >= lastSequence) break;
        }
        return cursor;
    }

    /// <summary>Shows events after <paramref name="cursor"/> until the run for
    /// <paramref name="attemptId"/> ends. A stream that closes first is reopened from the cursor.</summary>
    internal static async Task<long> FollowTurnAsync(IMissionConversationService conversations, Guid conversationId, long cursor,
        Guid attemptId, Action<ConversationEvent> show, CancellationToken ct)
    {
        while (true)
        {
            await foreach (var item in conversations.StreamEventsAsync(conversationId, cursor, ct))
            {
                if (item.Sequence <= cursor) continue;
                cursor = item.Sequence;
                show(item);
                if (EndsTurn(item.Kind, item.RunId, item.RunStatus, attemptId)) return cursor;
            }
            await Task.Delay(ReconnectDelay, ct);
        }
    }

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
            case ConversationEventKind.RunStatus when item.RunStatus is { } status && IsTerminal(status) && status != ConversationRunStatus.Completed:
                Console.WriteLine($"(run {status.ToString().ToLowerInvariant()})");
                break;
        }
    }

    /// <summary>The TUI needs a terminal on both ends; piped input or output keeps the line mode.</summary>
    internal static bool UsesTui(bool inputRedirected, bool outputRedirected) => !inputRedirected && !outputRedirected;

    /// <summary>The latest conversation is reopened only when it is on the default mission; a model
    /// is pinned at create, so a conversation on another mission is never continued on Chat.</summary>
    internal static bool ReusesLatest(string? latestMissionName) =>
        string.Equals(latestMissionName, MissionName, StringComparison.Ordinal);

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
