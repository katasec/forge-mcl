using System.CommandLine;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Net.Http.Headers;
using ForgeMission.Application;
using ForgeMission.Application.Transport;
using ForgeMission.Conversations.Contracts;
using ForgeMission.Core.Resolution;
using ForgeMission.Core.Tools;
using ForgeMission.Cli.Tui;
using ForgeMission.Cli.Tui.Graphics;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeMission.Cli;

// forge chat opens the current folder's portable declaration and reconnects to its hosted
// mission through Katasec.Forge.Client. Projects, packages and history stay with their existing
// owners; this surface handles startup, fresh hands consent and terminal/line presentation.
public static class ForgeChat
{
    private const string HandsFlag = "--hands";
    private const string ProjectFlag = "--project";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(250);
    private const string NeedsImagesMessage = "forge chat needs a terminal that can show images, such as Ghostty or Kitty " +
        "(not inside tmux). Open forge chat again from one of those.";

    /// <summary>The <c>forge chat</c> command and its flags.</summary>
    internal static Command BuildCommand()
    {
        var cmd = new Command("chat", "Chat with a hosted mission in the current Forge project");
        cmd.Add(new Option<bool>(HandsFlag)
        {
            Description = "Let the model read, write and edit project files (asks every launch)",
        });
        cmd.Add(new Option<string?>(ProjectFlag)
        {
            Description = "Open the Forge project in this folder instead of the current directory",
        });
        cmd.SetAction(async result => await RunAsync(Hands(result), result.GetValue<string?>(ProjectFlag)));
        return cmd;
    }

    public static async Task<int> RunAsync(bool hands, string? projectFolder)
    {
        var home = Path.GetFullPath(projectFolder ?? Directory.GetCurrentDirectory());
        if (!File.Exists(Path.Combine(home, "forge.project.json")))
        {
            Console.Error.WriteLine(projectFolder is null
                ? "No forge.project.json found in the current directory."
                : $"No forge.project.json found in {home}.");
            return 1;
        }

        // Configuration and terminal prerequisites still precede login/network work.
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
        try
        {
            return await RunApplicationAsync(provider.GetRequiredService<IHttpClientFactory>(), ModeFor(hands), home, theme, fonts);
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

    /// <summary>Owns the client application's joined lifetime and delivers its final notices
    /// after disposal, while preserving the original chat failure.</summary>
    private static async Task<int> RunApplicationAsync(IHttpClientFactory clients, ChatMode mode, string home,
        ForgeTheme theme, TextFonts? fonts)
    {
        var notices = new ConcurrentQueue<string>();
        var app = ApplicationComposition.Create(clients,
            null, PolicyFor(mode.HasHands), item =>
            {
                if (item.Kind == ApplicationEventKind.Error && item.Error is { } message)
                    notices.Enqueue(message);
            }, CancellationToken.None);

        // Dispose explicitly: the final projection flush can report a notice, and a cleanup
        // failure must not replace the original chat failure.
        ExceptionDispatchInfo? chatFailure = null;
        ExceptionDispatchInfo? cleanupFailure = null;
        var exitCode = 0;
        try { exitCode = await ChatInProjectAsync(app, mode, home, theme, fonts, notices); }
        catch (Exception failure) { chatFailure = ExceptionDispatchInfo.Capture(failure); }
        try { await app.DisposeAsync(); }
        catch (Exception failure) { cleanupFailure = ExceptionDispatchInfo.Capture(failure); }
        finally { DrainNotices(notices, Console.Error.WriteLine); }

        if (chatFailure is not null)
        {
            if (cleanupFailure is not null)
                Console.Error.WriteLine($"Chat cleanup also failed: {cleanupFailure.SourceException.Message}");
            chatFailure.Throw();
        }
        cleanupFailure?.Throw();
        return exitCode;
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

    /// <summary>Opens the portable Project, reconnects to its hosted mission, asks fresh hands
    /// consent and presents its complete history in the TUI or line mode.</summary>
    private static async Task<int> ChatInProjectAsync(ApplicationComposition app, ChatMode mode, string home,
        ForgeTheme theme, TextFonts? tuiFonts, ConcurrentQueue<string> notices)
    {
        var session = await OpenProjectAsync(app.Projects, home, mode.MissionName);
        var reconnected = await app.MissionConversations.ReconnectAsync(
            new ReconnectMissionConversationRequest(session.SessionId, mode.MissionName), CancellationToken.None);
        var conversation = reconnected.Conversation ?? throw Stopped(reconnected.Error);
        if (conversation.Approval.Profile != mode.ExpectedProfile)
            throw new ChatStoppedException($"The hosted {mode.MissionName} profile {conversation.Approval.Profile} " +
                $"does not match this chat mode's {mode.ExpectedProfile} profile.");
        if (mode.HasHands && !HandsAllowed(session.Project.Home, tuiFonts is not null))
            return 1;

        await using var hands = mode.HasHands
            ? await AttachHandsAsync(app.MissionHands, session.SessionId, conversation.ConversationId, conversation.Approval)
            : null;
        if (tuiFonts is null)
            return await ChatAsync(app.MissionConversations, conversation.ConversationId, hands, notices);

        var header = new ChatHeader(session.Project.Title, conversation.MissionName, conversation.Approval.VersionNumber,
            conversation.ProviderProfile, Environment.UserName);
        if (await ChatTui.RunAsync(app.MissionConversations, conversation.ConversationId, header, theme, tuiFonts, hands, notices) == TuiExit.Quit)
            return 0;
        Console.Error.WriteLine(NeedsImagesMessage);
        return 1;
    }

    // ── Project ─────────────────────────────────────────────────────────────────────────────

    private static async Task<ProjectSession> OpenProjectAsync(IProjectService projects, string home, string mission)
    {
        var opened = await projects.OpenChatAsync(new ProjectOpenRequest(home, Mission: mission), CancellationToken.None);
        var session = opened.Session ?? throw Stopped(opened.Error);
        Console.WriteLine($"Project: {session.Project.Home}");
        return session;
    }

    // ── Hands (Phase 55) ────────────────────────────────────────────────────────────────────

    /// <summary>Every hands launch asks again; redirected input never grants file access.</summary>
    private static bool HandsAllowed(string folder, bool interactive)
    {
        switch (AskApproval(folder, interactive, Console.In, Console.Out))
        {
            case HandsApproval.Approved:
                return true;
            case HandsApproval.NotInteractive:
                Console.Error.WriteLine("forge chat --hands requires fresh file access approval in a terminal.");
                return false;
            default:
                Console.Error.WriteLine("File access not allowed.");
                return false;
        }
    }

    /// <summary>Acknowledges the authenticated hosted pin after this launch's scoped approval.</summary>
    private static async Task<ChatHandsAttachment> AttachHandsAsync(IMissionHandsConversationService hands, string sessionId,
        Guid conversationId, MissionAccessApproval mission)
    {
        var acknowledged = await hands.AcknowledgeAsync(new AcknowledgeMissionHandsRequest(sessionId, conversationId,
            mission.MissionVersionId, mission.VersionNumber, mission.DefinitionHash, ProfileAccepted: true), CancellationToken.None);
        var attachmentId = acknowledged.AttachmentId
            ?? throw new ChatStoppedException($"file access could not start: {acknowledged.Error ?? "Forge returned no attachment."}");
        return new ChatHandsAttachment(hands, sessionId, conversationId, attachmentId);
    }

    // ── Conversation ────────────────────────────────────────────────────────────────────────

    /// <summary>Prints the history, follows a turn that is still running, then reads a line,
    /// submits it, and prints the reply until the turn ends. Ctrl-D exits; Ctrl-C during a turn
    /// cancels it (and a running file operation) and exits. With hands, each live hands request is
    /// executed off the follow loop.</summary>
    private static async Task<int> ChatAsync(IMissionConversationService conversations, Guid conversationId, ChatHandsAttachment? hands,
        ConcurrentQueue<string> notices)
    {
        var snapshot = (await conversations.GetConversationAsync(conversationId, CancellationToken.None)).Snapshot;
        string? lastReply = null;
        Action<ConversationEvent> print = item => lastReply = Print(item, lastReply);
        var cursor = await ReplayAsync(conversations, conversationId, 0, snapshot.LastSequence, print, CancellationToken.None);
        hands?.Begin(message => Console.WriteLine($"error: {message}"));
        Action<ConversationEvent> live = item =>
        {
            hands?.OnEvent(item);
            print(item);
        };
        if (snapshot.ActiveRunId is { } running && !IsTerminal(snapshot.Status))
            cursor = await FollowTurnAsync(conversations, conversationId, cursor, running, includeDeltas: false, live, CancellationToken.None);

        while (true)
        {
            DrainNotices(notices, Console.Error.WriteLine);
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

    /// <summary>Prints one event; returns the last reply text printed, so a final result that
    /// repeats it (<see cref="Transcript.RepeatsLastReply"/>) is printed once.</summary>
    private static string? Print(ConversationEvent item, string? lastReply)
    {
        if (Transcript.RepeatsLastReply(lastReply, item)) return lastReply;
        switch (item.Kind)
        {
            // Phase 59: every message shows when it was sent, a live echo of a typed line too.
            case ConversationEventKind.UserMessage:
                Console.WriteLine($"you · {Transcript.TimeOf(item.OccurredAtUtc)}> {item.Text}");
                break;
            case ConversationEventKind.ParticipantStarted:
                Console.WriteLine($"[{item.Text} · {Transcript.TimeOf(item.OccurredAtUtc)}]");
                break;
            case ConversationEventKind.ParticipantMessage:
                Console.WriteLine(item.Text);
                Console.WriteLine();
                return item.Text ?? "";
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
        return lastReply;
    }

    /// <summary>The TUI needs a terminal on both ends; piped input or output keeps the line mode.</summary>
    internal static bool UsesTui(bool inputRedirected, bool outputRedirected) => !inputRedirected && !outputRedirected;

    private static bool Hands(ParseResult result) => result.GetValue<bool>(HandsFlag);

    /// <summary>Whether <paramref name="args"/> (after <c>chat</c>) ask for hands.</summary>
    internal static bool ParseHands(string[] args) => Hands(BuildCommand().Parse(args));

    internal static ChatMode ModeFor(bool hands) => hands ? ChatMode.Hands : ChatMode.Plain;

    /// <summary>Portable admission is NoHands. After fresh consent and authenticated pin
    /// acknowledgement, Bob may use files; terminal capability stays denied.</summary>
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

    /// <summary>Called by the presentation owner, never by the shared notification callback.</summary>
    internal static void DrainNotices(ConcurrentQueue<string> notices, Action<string> show)
    {
        while (notices.TryDequeue(out var message)) show(message);
    }

    private static ChatStoppedException Stopped(ProjectOperationError? error) =>
        new(error?.Message ?? "Forge returned no result.");

    private sealed class ChatStoppedException(string message) : Exception(message);
}

internal enum HandsApproval { Approved, Declined, NotInteractive }

/// <summary>The command's mission selection; the hosted response owns its actual pin/profile.</summary>
internal sealed record ChatMode(string MissionName, MissionHandsProfile ExpectedProfile)
{
    public static ChatMode Plain { get; } = new(StarterMissions.Chat, MissionHandsProfile.NoHands);
    public static ChatMode Hands { get; } = new(StarterMissions.ChatHands, MissionHandsProfile.ProjectWorkspace);
    public bool HasHands => ExpectedProfile != MissionHandsProfile.NoHands;
}
