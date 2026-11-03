using System.CommandLine;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using ForgeMission.Application;
using ForgeMission.Application.Transport;
using ForgeMission.Conversations.Contracts;
using ForgeMission.Core.Resolution;
using ForgeMission.Cli.Tui;
using ForgeMission.Cli.Tui.Graphics;
using ForgeMission.Cli.Services;
using Microsoft.Extensions.Options;

namespace ForgeMission.Cli.Commands;

// forge chat opens the current folder's portable declaration and continues its hosted chat
// through Katasec.Forge.Client's shared path (open with Hands attached, one turn followed to its
// end), the same one forge run uses. Projects, packages and history stay with their owners; this
// surface handles startup and terminal/line presentation.
public static class ForgeChat
{
    private const string ProjectFlag = "--project";
    internal static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(250);
    private const string NeedsImagesMessage = "forge chat needs a terminal that can show images, such as Ghostty or Kitty " +
        "(not inside tmux). Open forge chat again from one of those.";

    /// <summary>The <c>forge chat</c> command and its flags.</summary>
    internal static Command BuildCommand(IOptions<ForgeSettings> settings, IHttpClientFactory clients)
    {
        var cmd = new Command("chat", "Chat with a hosted mission in the current Forge project");
        cmd.Add(new Option<string?>(ProjectFlag)
        {
            Description = "Path to a project file (default: ./forge.project.json)",
        });
        cmd.SetCancellableAction((result, cancellationToken) => MacChatWindow.RunCommandAsync(
            () => RunAsync(result.GetValue<string?>(ProjectFlag), settings, clients, cancellationToken),
            MacChatWindow.ChildMarker(OperatingSystem.IsMacOS(),
                UsesTui(Console.IsInputRedirected, Console.IsOutputRedirected),
                Environment.GetEnvironmentVariable(MacChatWindow.Marker)),
            context => MacChatWindow.RestoreContext(context, Directory.SetCurrentDirectory),
            () => { Console.ReadKey(intercept: true); }, Console.Error, cancellationToken));
        return cmd;
    }

    internal static async Task<int> RunAsync(string? projectPath, IOptions<ForgeSettings> settings,
        IHttpClientFactory clients, CancellationToken cancellationToken = default)
    {
        var projectFile = Path.GetFullPath(projectPath
            ?? Path.Combine(Directory.GetCurrentDirectory(), "forge.project.json"));

        if (Directory.Exists(projectFile))
        {
            Console.Error.WriteLine("Expected a project file path, not a directory.");
            return 1;
        }

        // Configuration and terminal prerequisites still precede login/network work.
        ForgeTheme theme;
        try { theme = settings.Value.SelectedTheme(); }
        catch (OptionsValidationException bad)
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

        if (MacChatWindow.ShouldLaunch(OperatingSystem.IsMacOS(), interactive,
            Environment.GetEnvironmentVariable(MacChatWindow.Marker)))
            return await MacChatWindow.LaunchAsync(Environment.ProcessPath, projectFile,
                Directory.GetCurrentDirectory(), Console.Out, Console.Error, cancellationToken: cancellationToken);

        try
        {
            return await RunApplicationAsync(clients, projectFile, theme, fonts, cancellationToken);
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
        // R10: the conversation host returned a body the client could not read as a mission reply
        // (e.g. a non-JSON 500). The Application layer reports this as ConversationHostProtocolException,
        // which is internal to ForgeMission.Application — a `catch (ConversationHostProtocolException)`
        // will not compile (CS0122), and its only public base is the too-broad InvalidOperationException,
        // so it is matched by exact type name to stay narrow without swallowing unrelated failures.
        catch (Exception failure) when (failure.GetType().Name == "ConversationHostProtocolException")
        {
            Console.Error.WriteLine($"chat failed: {failure.Message}");
            return 1;
        }
        // S2b: the line mode does not reconnect, so a lost stream stops it. A turn's own Ctrl-C ends in
        // ChatAsync; the caller's own cancellation propagates rather than reading as a lost connection.
        catch (Exception failure) when (IsTransportFailure(failure, cancellationToken))
        {
            return ReportConnectionLost(failure, Console.Error);
        }
    }

    /// <summary>Owns the client application's joined lifetime and delivers its final notices
    /// after disposal, while preserving the original chat failure.</summary>
    private static async Task<int> RunApplicationAsync(IHttpClientFactory clients, string projectFile,
        ForgeTheme theme, TextFonts? fonts, CancellationToken cancellationToken)
    {
        var notices = new ConcurrentQueue<string>();
        var app = ApplicationComposition.Create(clients, item =>
            {
                if (item.Kind == ApplicationEventKind.Error && item.Error is { } message)
                    notices.Enqueue(message);
            }, cancellationToken);

        // Dispose explicitly: the final projection flush can report a notice, and a cleanup
        // failure must not replace the original chat failure.
        ExceptionDispatchInfo? chatFailure = null;
        ExceptionDispatchInfo? cleanupFailure = null;
        var exitCode = 0;
        try { exitCode = await ChatInProjectAsync(app, projectFile, theme, fonts, notices, cancellationToken); }
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

    /// <summary>Continues the Project's hosted chat (session, history copy and Hands attached by
    /// forge-client) and presents its complete history in the TUI or line mode. Hands reports go
    /// to the line mode's output, or to the TUI's notices.</summary>
    private static async Task<int> ChatInProjectAsync(ApplicationComposition app, string projectFile,
        ForgeTheme theme, TextFonts? tuiFonts, ConcurrentQueue<string> notices, CancellationToken cancellationToken)
    {
        var report = tuiFonts is null
            ? new ImmediateProgress(message => Console.WriteLine($"error: {message}"))
            : new ImmediateProgress(notices.Enqueue);
        var opened = await app.MissionConversations.ContinueChatAsync(projectFile, report, cancellationToken);
        await using var conversation = opened.Conversation ?? throw Stopped(opened.Error);
        Console.WriteLine($"Project: {conversation.ProjectHome}");
        if (tuiFonts is null)
            return await ChatAsync(app.MissionConversations, conversation, notices, cancellationToken);

        var header = new ChatHeader(conversation.ProjectTitle, conversation.MissionName, conversation.VersionNumber,
            conversation.ProviderProfile, Environment.UserName);
        if (await ChatTui.RunAsync(app.MissionConversations, conversation, header, theme, tuiFonts, notices,
            cancellationToken) == TuiExit.Quit)
            return 0;
        Console.Error.WriteLine(NeedsImagesMessage);
        return 1;
    }

    // ── Conversation ────────────────────────────────────────────────────────────────────────

    /// <summary>Prints the history, follows a turn that is still running, then reads a line,
    /// runs it as one turn, and prints its events until it ends. Ctrl-D exits; Ctrl-C (the command's
    /// <paramref name="cancellationToken"/>) during a turn cancels it (a running file operation
    /// first) and exits 130. An event already printed by the history is not printed again.</summary>
    private static async Task<int> ChatAsync(IMissionConversationService conversations, ProjectConversation conversation,
        ConcurrentQueue<string> notices, CancellationToken cancellationToken)
    {
        var snapshot = (await conversations.GetConversationAsync(conversation.ConversationId, cancellationToken)).Snapshot;
        string? lastReply = null;
        var printed = 0L;
        Action<ConversationEvent> print = item =>
        {
            if (item.Kind != ConversationEventKind.ParticipantDelta && item.Sequence <= printed) return;
            printed = Math.Max(printed, item.Sequence);
            lastReply = Print(item, lastReply);
        };
        await ReplayAsync(conversations, conversation.ConversationId, 0, snapshot.LastSequence, print, cancellationToken);
        await conversation.ResumeAsync(print, cancellationToken);

        while (true)
        {
            DrainNotices(notices, Console.Error.WriteLine);
            Console.Write("you> ");
            if (Console.ReadLine() is not { } line) return 0;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                await conversation.RunTurnAsync(Guid.NewGuid(), line, print, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("Cancelled.");
                return CommandActions.Interrupted;
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

    /// <summary>The TUI's live display stream (53.9 L1; a turn's own follow is forge-client's):
    /// shows events after <paramref name="cursor"/> until <paramref name="ends"/> accepts one, and
    /// returns the cursor. Every end is passed to
    /// <paramref name="ended"/> (null for a close, else the transport failure) and it decides: true
    /// reopens after <see cref="ReconnectDelay"/>, false returns the cursor (idle sleep, Phase 57
    /// S5). A cancellation that is not <paramref name="ct"/>'s (HttpClient's timeout waiting for
    /// headers, Phase 57 S2) is a transport failure; the session's own cancellation always ends the loop. With
    /// <paramref name="includeDeltas"/> (the TUI, Phase 53.8), live reply deltas are shown too; a
    /// delta never moves the cursor, and it is shown only once a step has started on the same
    /// connection — a step joined mid-reply (reopened or reconnected) shows no partial text until
    /// its final message.</summary>
    internal static async Task<long> StreamAsync(IMissionConversationService conversations, Guid conversationId, long cursor,
        bool includeDeltas, Action<ConversationEvent> show, Func<ConversationEvent, bool> ends, Func<Exception?, bool> ended,
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
            catch (Exception caught) when (IsTransportFailure(caught, ct))
            {
                failure = caught;
            }
            if (!ended(failure)) return cursor;
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
