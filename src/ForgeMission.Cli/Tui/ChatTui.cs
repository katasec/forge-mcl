using ForgeMission.Application;
using ForgeMission.Conversations.Contracts;
using ForgeMission.Cli.Tui.Graphics;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (53.5): the full-screen chat on a terminal. It runs the same conversation calls
// as the line mode (ForgeChat.ReplayAsync / StreamAsync, SubmitAsync, CancelAsync) inside XenoAtom's
// single-threaded UI loop: awaited calls resume on the UI thread, so input and rendering keep running
// while a turn streams. This file owns the session lifecycle and the keys.
// One live stream (53.9 L1): after the replay, one stream with reply deltas runs from open to exit,
// so every turn streams live whichever window sent it; submitting only posts the message. One turn
// runs at a time. Ctrl-C cancels this window's own turn. With hands (Phase 55), only this window's
// own turn drives the attachment; another window's tool use is shown, not executed.
// Idle sleep (Phase 57 S5): ChatLink owns the connection. A stream that ends with no turn in flight
// is not reopened; any key except Ctrl-D wakes it (a catch-up read, then the live stream), and an
// Enter that wakes it sends only after the catch-up, under the normal send rule.
// Ctrl-D (53.9 L2) replaces the app's quit command: it cancels the session, and the loop stops only
// after the stream, the busy work and the hands cancel have ended on the live UI thread, so no await
// resumes after the app has stopped.
// Shape tiles (Phase 56): on the first tick (on the alternate screen, with XenoAtom owning input) the
// TUI asks for the cell size, draws every tile set at that size and sends it once; every shape names
// those images. Without a cell size the app stops before opening the conversation (G8).
internal sealed class ChatTui
{
    private static readonly KeyGesture QuitGesture = new(TerminalChar.CtrlD, TerminalModifiers.Ctrl);

    private readonly IMissionConversationService _conversations;
    private readonly Guid _conversationId;
    private readonly ChatScreen _screen;
    private readonly CancellationTokenSource _sessionSource;
    private readonly CancellationToken _session;
    private readonly ChatHandsAttachment? _hands;
    private readonly ForgeStyles _styles;
    private bool _noCellSize;
    private IReadOnlyList<TranscriptBlock> _blocks = [];
    private bool _opened;
    // Opening, submitting, or cancelling: one UI step is awaiting a call.
    private bool _busy;
    private bool _submitting;
    // A turn from any window is running, as the stream shows it.
    private bool _turnRunning;
    private SentMessage? _pendingMessage;
    // The turn this window submitted, until the stream shows its end.
    private SubmitMissionTurnResponse? _ownTurn;
    private bool _stopOwnTurn;
    // An Enter pressed while the link was asleep or catching up: send once it is ready.
    private bool _sendOnWake;
    private readonly ChatLink _link;

    private ChatTui(IMissionConversationService conversations, Guid conversationId, ChatHeader header, ForgeTheme theme,
        ChatHandsAttachment? hands, CancellationTokenSource session)
    {
        _conversations = conversations;
        _conversationId = conversationId;
        _hands = hands;
        _sessionSource = session;
        _session = session.Token;
        _styles = new ForgeStyles(theme);
        _screen = new ChatScreen(header, _styles);
        _link = new ChatLink(conversations, conversationId, InFlight, ShowLive, ShowNotice, _session);
        _screen.Composer.Accepted((_, e) => Send(e.Text));
        WakeOnInput();
        AddKey(new KeyGesture(TerminalChar.CtrlC, TerminalModifiers.Ctrl), "Forge.StopRun", StopRun);
        AddKey(new KeyGesture(TerminalKey.PageUp), "Forge.PageUp", _screen.PageUp);
        AddKey(new KeyGesture(TerminalKey.PageDown), "Forge.PageDown", _screen.PageDown);
    }

    /// <summary>Runs the TUI until Ctrl-D, or until the terminal gives no cell size on the first
    /// tick (<see cref="TuiExit.NoCellSize"/>; the caller reports it). A turn still running on quit
    /// keeps running on Forge; only this process stops following it. A file operation still
    /// running is cancelled.</summary>
    public static async Task<TuiExit> RunAsync(IMissionConversationService conversations, Guid conversationId, ChatHeader header,
        ForgeTheme theme, ChatHandsAttachment? hands)
    {
        using var session = new CancellationTokenSource();
        var tui = new ChatTui(conversations, conversationId, header, theme, hands, session);
        TypeAhead.Discard();
        await TerminalCaret.WhileRunning(tui._styles.Caret, () =>
            Terminal.RunAsync(tui._screen.Root, tui.UpdateAsync, new TerminalRunOptions { ExitGesture = QuitGesture }).AsTask());
        return tui._noCellSize ? TuiExit.NoCellSize : TuiExit.Quit;
    }

    /// <summary>The UI loop's one async step: on the first tick send the tile images (or stop when
    /// the terminal gives no cell size) and open the conversation; then submit a waiting message
    /// or cancel this window's turn, or send an Enter that woke the link once it has caught up.
    /// After Ctrl-D it stops the app; the loop calls this only once the previous step, with its busy
    /// work, has ended. A live stream that failed unexpectedly ends the chat with its error.</summary>
    private async ValueTask<TerminalLoopResult> UpdateAsync(TerminalRunningContext context)
    {
        if (_link.Live.IsFaulted) await _link.Live;
        if (_session.IsCancellationRequested)
            return await StopAsync();
        if (!_opened)
        {
            _opened = true;
            context.App.AddGlobalCommand(QuitCommand());
            if (!await ShowTilesAsync())
                return TerminalLoopResult.Stop;
            // The composer joins the screen inside its frame once the tiles have arrived.
            context.App.Focus(_screen.Composer);
            await WhileBusyAsync(OpenAsync);
        }
        else if (_pendingMessage is { } sent)
        {
            _pendingMessage = null;
            await WhileBusyAsync(() => SubmitAsync(sent));
        }
        else if (_stopOwnTurn && _ownTurn is { } own)
        {
            _stopOwnTurn = false;
            await WhileBusyAsync(() => CancelOwnTurnAsync(own));
        }
        else if (_sendOnWake)
        {
            _sendOnWake = false;
            await WhileBusyAsync(_link.WakeAsync);
            if (_link.Ready) Send(_screen.Composer.Text ?? "");
        }
        return TerminalLoopResult.Continue;
    }

    // ── Tile images ──────────────────────────────────────────────────────────────────────────

    /// <summary>Asks the terminal for its cell size; with one, draws every tile set at that size,
    /// sends them, and lets the screen show its shapes. Without one (G8), nothing is drawn.</summary>
    private async Task<bool> ShowTilesAsync()
    {
        if (TerminalFacts.ImageCell(await TerminalFacts.QueryCellAsync()) is not { } cell)
        {
            _noCellSize = true;
            return false;
        }
        var tiles = ScreenTiles.Create(_styles, cell);
        tiles.Transmit();
        _screen.UseTiles(tiles);
        return true;
    }

    // ── Conversation ────────────────────────────────────────────────────────────────────────

    /// <summary>Replays the conversation, then starts the live stream after it. A turn already
    /// running is shown by that stream; it cannot be stopped from here (it is not this window's).</summary>
    private async Task OpenAsync()
    {
        var snapshot = (await _conversations.GetConversationAsync(_conversationId, _session)).Snapshot;
        var cursor = await ForgeChat.ReplayAsync(_conversations, _conversationId, 0, snapshot.LastSequence, Show, _session);
        _turnRunning = snapshot.ActiveRunId is not null && !ForgeChat.IsTerminal(snapshot.Status);
        _hands?.Begin(ShowError);
        _link.Start(cursor);
    }

    /// <summary>Posts one message (already shown as pending) with its command id; the live stream
    /// shows its turn. A message Forge did not accept goes back into the composer with an error, and
    /// a Ctrl-C pressed while it was in flight is dropped, so it cannot stop the next turn.</summary>
    private async Task SubmitAsync(SentMessage sent)
    {
        _submitting = true;
        try
        {
            _ownTurn = await _conversations.SubmitAsync(_conversationId, sent.CommandId, sent.Text, _session);
        }
        catch (HttpRequestException failure)
        {
            ShowBlocks(Transcript.SubmitFailed(_blocks, sent.CommandId, failure.Message));
            _screen.Composer.Text = sent.Text;
        }
        finally
        {
            _submitting = false;
            _stopOwnTurn = KeepsStop(_stopOwnTurn, _ownTurn is not null);
        }
    }

    /// <summary>Ctrl-C on this window's turn: cancels a running file operation, then the turn on
    /// Forge. The live stream shows how the turn ended.</summary>
    private async Task CancelOwnTurnAsync(SubmitMissionTurnResponse own)
    {
        if (_hands is not null) await _hands.CancelInFlightAsync();
        try
        {
            await _conversations.CancelAsync(_conversationId, own.TurnId, own.TurnAttemptId, Guid.NewGuid(), _session);
        }
        catch (HttpRequestException failure)
        {
            ShowError(failure.Message);
        }
    }

    /// <summary>Runs one busy step. Work stopped by Ctrl-D ends here: the app is quitting.</summary>
    private async Task WhileBusyAsync(Func<Task> work)
    {
        _busy = true;
        try { await work(); }
        catch (OperationCanceledException) when (_session.IsCancellationRequested) { }
        finally { _busy = false; }
    }

    /// <summary>Waits for the live stream to end, cancels a running file operation while the UI
    /// thread still runs (its report can still be shown), then stops the app.</summary>
    private async Task<TerminalLoopResult> StopAsync()
    {
        await _link.Live;
        if (_hands is not null) await _hands.CancelInFlightAsync();
        return TerminalLoopResult.Stop;
    }

    // ── Input ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Enter: show the message and a pending reply at once, and queue the submit for the
    /// next UI step. While the link sleeps or catches up, Enter wakes it and sends once it is ready.
    /// Before the tile images are sent, while the conversation is opening, a call is in flight, or a
    /// turn from any window runs, Enter does nothing and the text stays in the composer.</summary>
    private void Send(string text)
    {
        if (_busy || _sendOnWake) return;
        if (!_link.Ready)
        {
            _sendOnWake = true;
            Wake();
            return;
        }
        if (!_screen.HasTiles || _turnRunning || _pendingMessage is not null || _session.IsCancellationRequested ||
            string.IsNullOrWhiteSpace(text)) return;
        var sent = new SentMessage(Guid.NewGuid(), text);
        _pendingMessage = sent;
        _screen.Composer.Text = "";
        ShowBlocks(Transcript.Submit(_blocks, sent.CommandId, sent.Text));
    }

    /// <summary>Ctrl-C: stop the turn this window submitted (once its submit returns); nothing
    /// when it has none.</summary>
    private void StopRun()
    {
        if (_ownTurn is not null || _submitting) _stopOwnTurn = true;
    }

    /// <summary>Ctrl-D: replaces the app's own quit command, which would stop the app at once.
    /// Cancelling the session ends the stream and the busy work; the next UI step then stops the app.</summary>
    private Command QuitCommand() => new()
    {
        Id = TerminalApp.DefaultQuitCommandId,
        LabelMarkup = "Quit",
        DescriptionMarkup = "Quit the application.",
        Gesture = QuitGesture,
        Importance = CommandImportance.Primary,
        Presentation = CommandPresentation.CommandBar,
        Execute = _ => _sessionSource.Cancel(),
    };

    /// <summary>A key of this window's own: it wakes the link first (commands never reach KeyDown).</summary>
    private void AddKey(KeyGesture gesture, string id, Action action) =>
        _screen.Root.AddCommand(new Command
        {
            Id = id, LabelMarkup = string.Empty, Gesture = gesture, Execute = _ =>
            {
                Wake();
                action();
            },
        });

    /// <summary>Typed text, editing keys and pastes wake the link. XenoAtom runs a visual's own
    /// handlers even after its class handler marked the event handled only on the source (the
    /// focused composer), so the composer covers what it handles and the root what bubbles up
    /// unhandled. XenoAtom raises Ctrl-D as a KeyDown before quitting, so it is skipped: Ctrl-D quits
    /// without waking.</summary>
    private void WakeOnInput()
    {
        foreach (var visual in new Visual[] { _screen.Composer, _screen.Root })
        {
            visual.KeyDownRouted += (_, e) =>
            {
                if (!QuitGesture.Matches(e.RawEvent)) Wake();
            };
            visual.TextInputRouted += (_, _) => Wake();
            visual.PasteRouted += (_, _) => Wake();
        }
    }

    /// <summary>Starts a wake; a failed catch-up surfaces through the link's live task.</summary>
    private void Wake() => _ = _link.WakeAsync();

    // ── Output ──────────────────────────────────────────────────────────────────────────────

    private void Show(ConversationEvent item) => ShowBlocks(Transcript.Apply(_blocks, item));

    /// <summary>A live event: hands act on this window's own turn only (never on replay), the turn
    /// state follows it, then it is shown.</summary>
    private void ShowLive(ConversationEvent item)
    {
        if (BelongsToOwnTurn(item, _ownTurn?.TurnAttemptId))
            _hands?.OnEvent(item);
        _turnRunning = TurnRunning(_turnRunning, item);
        if (_ownTurn is { } own && ForgeChat.EndsTurn(item.Kind, item.RunId, item.RunStatus, own.TurnAttemptId))
        {
            _ownTurn = null;
            _stopOwnTurn = false;
        }
        ShowBlocks(Transcript.ApplyLive(_blocks, item));
    }

    /// <summary>The link's state as one notice line (the live events remove the reconnect line).</summary>
    private void ShowNotice(LinkNotice notice) => ShowBlocks(notice switch
    {
        LinkNotice.Idle => Transcript.Idle(_blocks),
        LinkNotice.Awake => Transcript.Awake(_blocks),
        _ => Transcript.Reconnecting(_blocks),
    });

    private void ShowError(string message) => ShowBlocks([.. _blocks, new ErrorLine($"error: {message}")]);

    private void ShowBlocks(IReadOnlyList<TranscriptBlock> blocks)
    {
        _blocks = blocks;
        _screen.Show(_blocks);
    }

    // ── Rules ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A turn is in flight (S5): one runs in the conversation, from any window, or this
    /// window waits for a reply.</summary>
    private bool InFlight() => _turnRunning || Transcript.Replying(_blocks) is not null;

    /// <summary>An event of this window's own turn: the Host stamps each turn's events with its
    /// attempt id as <c>RunId</c>. Only these drive hands, so a second window never executes (or is
    /// refused) another window's tool request.</summary>
    internal static bool BelongsToOwnTurn(ConversationEvent item, Guid? ownAttemptId) =>
        ownAttemptId is { } own && item.RunId == own;

    /// <summary>Whether a turn from any window is running after <paramref name="item"/>: a message
    /// starts one, a terminal run status ends it.</summary>
    internal static bool TurnRunning(bool running, ConversationEvent item) => item.Kind switch
    {
        ConversationEventKind.UserMessage => true,
        ConversationEventKind.RunStatus when item.RunStatus is { } status && ForgeChat.IsTerminal(status) => false,
        _ => running,
    };

    /// <summary>After a submit ends: a Ctrl-C pressed while it was in flight stops the turn only
    /// when the submit started one; a failed or refused submit drops it.</summary>
    internal static bool KeepsStop(bool stopRequested, bool hasOwnTurn) => stopRequested && hasOwnTurn;

    /// <summary>A message sent from the composer; <see cref="CommandId"/> is the id Forge echoes as
    /// the <c>UserMessage</c> event id.</summary>
    private sealed record SentMessage(Guid CommandId, string Text);
}

/// <summary>How the TUI ended: quit by the user, or stopped because the terminal gave no cell
/// size.</summary>
internal enum TuiExit { Quit, NoCellSize }
