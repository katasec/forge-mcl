using ForgeMission.Application;
using System.Collections.Concurrent;
using ForgeMission.Conversations.Contracts;
using ForgeMission.Cli.Tui.Graphics;
using ForgeMission.Cli.Commands;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (53.5): the full-screen chat on a terminal, inside XenoAtom's single-threaded UI
// loop: awaited calls resume on the UI thread, so input and rendering keep running while a turn
// streams. This file owns the session lifecycle and the keys.
// One live stream (53.9 L1): after the replay, one display stream with reply deltas runs from open
// to exit, so every turn streams live whichever window sent it. A turn itself is the same shared
// call as the line mode and forge run (ProjectConversation.RunTurnAsync, Phase 76 decision 2c): it
// runs off the UI step with nothing to show (the display stream shows it) and feeds Hands. One turn
// runs at a time. Ctrl-C cancels this window's own turn; quitting stops following it and the turn
// keeps running on Forge. A turn already running at open is followed (ResumeAsync) for Hands.
// Idle sleep (Phase 57 S5): ChatLink owns the connection. A stream that ends with no turn in flight
// is not reopened; any key except Ctrl-D wakes it (a catch-up read, then the live stream), and an
// Enter that wakes it sends only after the catch-up, under the normal send rule.
// Ctrl-D (53.9 L2) replaces the app's quit command: it cancels the session, and the loop stops only
// after the stream and the busy work have ended on the live UI thread, so no await resumes after
// the app has stopped. The caller then disposes the conversation, which stops Hands and any follow.
// Shape tiles (Phase 56): on the first tick (on the alternate screen, with XenoAtom owning input) the
// TUI asks for the cell size, draws every tile set at that size and sends it once; every shape names
// those images. Text images (Task 4) are drawn at the same cell size and sent when first needed, on
// the UI thread. Without a cell size the app stops before opening the conversation (G8).
// Motion (Task 5): the pointer's moves go to the screen's link pointer, which is also rechecked on
// every UI tick; the default pointer comes back on every exit, inside the caret colour's restore.
// /edit (spike): a composer line "/edit <path>" is never sent; it opens the file in a FileEditor in
// place of the transcript. While it is open, Ctrl-D, Ctrl-C and PgUp/PgDn do nothing here (Ctrl-C
// copies in the editor); Esc, with the unsaved-changes guard, is the only way back to the chat.
internal sealed class ChatTui
{
    private static readonly KeyGesture QuitGesture = new(TerminalChar.CtrlD, TerminalModifiers.Ctrl);

    private readonly IMissionConversationService _conversations;
    private readonly ProjectConversation _conversation;
    private readonly Guid _conversationId;
    private readonly ChatScreen _screen;
    private readonly CancellationTokenSource _sessionSource;
    private readonly CancellationToken _session;
    private readonly ForgeStyles _styles;
    private readonly TextFonts _fonts;
    private bool _noCellSize;
    private IReadOnlyList<TranscriptBlock> _blocks = [];
    private bool _opened;
    // Opening, submitting, or cancelling: one UI step is awaiting a call.
    private bool _busy;
    // A turn from any window is running, as the stream shows it.
    private bool _turnRunning;
    private SentMessage? _pendingMessage;
    // The turn this window submitted, until its RunTurnAsync ends; Ctrl-C cancels its source.
    private CancellationTokenSource? _ownTurn;
    // Its first event arrived, so Forge has the turn and a Ctrl-C can cancel it.
    private bool _ownTurnAccepted;
    private bool _stopOwnTurn;
    // The turn follows run off the UI step; an unexpected failure ends the chat like the stream's.
    private Task _following = Task.CompletedTask;
    // An Enter pressed while the link was asleep or catching up: send once it is ready.
    private bool _sendOnWake;
    private readonly ChatLink _link;
    private readonly StartPage _start;
    private readonly ConcurrentQueue<string> _notices;

    private ChatTui(IMissionConversationService conversations, ProjectConversation conversation, ChatHeader header, ForgeTheme theme,
        TextFonts fonts, ConcurrentQueue<string> notices, CancellationTokenSource session)
    {
        _fonts = fonts;
        _conversations = conversations;
        _conversation = conversation;
        _conversationId = conversation.ConversationId;
        _notices = notices;
        _sessionSource = session;
        _session = session.Token;
        _styles = new ForgeStyles(theme);
        _screen = new ChatScreen(header, _styles);
        _start = new StartPage(_styles, OpenChat);
        _screen.ShowEditor(_start.View, editor: false);
        _link = new ChatLink(conversations, _conversationId, InFlight, ShowLive, ShowNotice, _session);
        _screen.Composer.Accepted((_, e) => Send(e.Text));
        WakeOnInput();
        // The pointer's shape over links (Task 5). Moving the pointer never wakes the link.
        _screen.Root.PointerMovedRouted += (_, e) => _screen.Links.PointerAt(e.UiX, e.UiY);
        AddKey(new KeyGesture(TerminalChar.CtrlC, TerminalModifiers.Ctrl), "Forge.StopRun", () =>
        {
            if (!_screen.Interaction.CopySelection()) StopRun();
        });
        AddKey(new KeyGesture(TerminalKey.PageUp), "Forge.PageUp", _screen.PageUp);
        AddKey(new KeyGesture(TerminalKey.PageDown), "Forge.PageDown", _screen.PageDown);
    }

    /// <summary>Runs the TUI until Ctrl-D, or until the terminal gives no cell size on the first
    /// tick (<see cref="TuiExit.NoCellSize"/>; the caller reports it). A turn still running on quit
    /// keeps running on Forge; only this process stops following it once the caller disposes
    /// <paramref name="conversation"/>, which also cancels a running file operation. The session
    /// also ends when <paramref name="cancellationToken"/> is cancelled.</summary>
    public static async Task<TuiExit> RunAsync(IMissionConversationService conversations, ProjectConversation conversation,
        ChatHeader header, ForgeTheme theme, TextFonts fonts, ConcurrentQueue<string> notices,
        CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tui = new ChatTui(conversations, conversation, header, theme, fonts, notices, session);
        TypeAhead.Discard();
        await TerminalCaret.WhileRunning(tui._styles.Caret, () => TerminalPointer.WhileRunning(() =>
            Terminal.RunAsync(tui._screen.Root, tui.UpdateAsync, new TerminalRunOptions { ExitGesture = QuitGesture }).AsTask()));
        return tui._noCellSize ? TuiExit.NoCellSize : TuiExit.Quit;
    }

    /// <summary>The UI loop's one async step: on the first tick send the tile images (or stop when
    /// the terminal gives no cell size) and open the conversation; then submit a waiting message
    /// or cancel this window's turn, or send an Enter that woke the link once it has caught up.
    /// After Ctrl-D it stops the app; the loop calls this only once the previous step, with its busy
    /// work, has ended. A live stream that failed unexpectedly ends the chat with its error.</summary>
    private async ValueTask<TerminalLoopResult> UpdateAsync(TerminalRunningContext context)
    {
        if (_screen.HasTiles) ForgeChat.DrainNotices(_notices, ShowError);
        if (_link.Live.IsFaulted) await _link.Live;
        if (_following.IsFaulted) await _following;
        _screen.Links.Recheck();
        _screen.Interaction.CheckFeedback();
        if (_session.IsCancellationRequested)
            return await StopAsync();
        if (!_opened)
        {
            _opened = true;
            context.App.AddGlobalCommand(QuitCommand());
            if (!await ShowImagesAsync())
                return TerminalLoopResult.Stop;
            // The TUI opens on the start page (Phase 60); Chat with a mission swaps in the chat.
            context.App.Focus(_start.List);
            await WhileBusyAsync(OpenAsync);
        }
        else if (_pendingMessage is { } sent)
        {
            _pendingMessage = null;
            Submit(sent);
        }
        else if (_stopOwnTurn && _ownTurnAccepted && _ownTurn is { } own)
        {
            _stopOwnTurn = false;
            await own.CancelAsync();
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
    /// sends them, and lets the screen show its shapes and text images (the start-up ones are sent
    /// now). Without one (G8), nothing is drawn.</summary>
    private async Task<bool> ShowImagesAsync()
    {
        if (TerminalFacts.ImageCell(await TerminalFacts.QueryCellAsync()) is not { } cell)
        {
            _noCellSize = true;
            return false;
        }
        var tiles = ScreenTiles.Create(_styles, cell);
        tiles.Transmit();
        var text = new TextImages(new TextArt(_styles.TextArt, _fonts, cell), _styles.ImageIdSlot, cell, KittyImages.Transmit);
        _screen.UseImages(tiles, text);
        return true;
    }

    // ── Conversation ────────────────────────────────────────────────────────────────────────

    /// <summary>Replays the conversation, then starts the live stream after it. A turn already
    /// running is shown by that stream and followed for Hands; it cannot be stopped from here (it
    /// is not this window's).</summary>
    private async Task OpenAsync()
    {
        var snapshot = (await _conversations.GetConversationAsync(_conversationId, _session)).Snapshot;
        var cursor = await ForgeChat.ReplayAsync(_conversations, _conversationId, 0, snapshot.LastSequence, Show, _session);
        _turnRunning = snapshot.ActiveRunId is not null && !ForgeChat.IsTerminal(snapshot.Status);
        _link.Start(cursor);
        if (_turnRunning) _following = FollowRunningTurnAsync();
    }

    /// <summary>Runs one message (already shown as pending) as this window's turn, off the UI step,
    /// with the pending pill's command id, so Forge's echo replaces the pill.</summary>
    private void Submit(SentMessage sent)
    {
        var own = new CancellationTokenSource();
        _ownTurn = own;
        _ownTurnAccepted = false;
        _following = RunOwnTurnAsync(sent, own);
    }

    /// <summary>The turn's own follow, with nothing to show; the live stream shows how it ended.
    /// Ctrl-C (a running file operation first, then the turn on Forge) and quit (the follow stops,
    /// the turn runs on) end it quietly. A lost connection runs the turn again with the same command
    /// id after <see cref="ForgeChat.ReconnectDelay"/> (the same submit, so no second turn), so Hands
    /// keeps getting its events and Ctrl-C keeps its cancel on Forge. Before Forge has the turn (no
    /// event seen) it is retried once; a second failure puts the message back into the composer
    /// with an error, since a failed submit and a failed first stream cannot be told apart.</summary>
    private async Task RunOwnTurnAsync(SentMessage sent, CancellationTokenSource own)
    {
        try
        {
            using var retry = CancellationTokenSource.CreateLinkedTokenSource(own.Token, _session);
            var failuresBeforeAccepted = 0;
            while (await RunTurnOnceAsync(sent, own) is { } failure)
            {
                if (!_ownTurnAccepted && ++failuresBeforeAccepted > 1)
                {
                    ShowBlocks(Transcript.SubmitFailed(_blocks, sent.CommandId, failure.Message));
                    _screen.Composer.Text = sent.Text;
                    return;
                }
                await Task.Delay(ForgeChat.ReconnectDelay, retry.Token);
            }
        }
        catch (OperationCanceledException) when (own.IsCancellationRequested || _session.IsCancellationRequested)
        {
        }
        finally
        {
            _ownTurn = null;
            _stopOwnTurn = false;
            own.Dispose();
        }
    }

    /// <summary>One run of this window's turn: null when it ended, else the transport failure that
    /// lost its connection. A failed cancel on Forge after Ctrl-C is shown as an error.</summary>
    private async Task<Exception?> RunTurnOnceAsync(SentMessage sent, CancellationTokenSource own)
    {
        try
        {
            await _conversation.RunTurnAsync(sent.CommandId, sent.Text, _ => _ownTurnAccepted = true, own.Token);
            return null;
        }
        catch (HttpRequestException failure) when (own.IsCancellationRequested)
        {
            ShowError(failure.Message);
            return null;
        }
        catch (Exception failure) when (IsTransportFailure(failure, own.Token))
        {
            return failure;
        }
    }

    /// <summary>Follows the turn that was running at open, for Hands, until none is running. A
    /// follow that loses its connection follows again after <see cref="ForgeChat.ReconnectDelay"/>
    /// (the display stream reconnects and shows the turn on its own); quit ends it.</summary>
    private async Task FollowRunningTurnAsync()
    {
        while (await LosesConnectionAsync())
            await Task.Delay(ForgeChat.ReconnectDelay, _session);
    }

    /// <summary>One follow of the running turn; true when it lost its connection.</summary>
    private async Task<bool> LosesConnectionAsync()
    {
        try
        {
            await _conversation.ResumeAsync(_ => { }, CancellationToken.None);
            return false;
        }
        catch (OperationCanceledException) when (_session.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception failure) when (IsTransportFailure(failure, CancellationToken.None))
        {
            return true;
        }
    }

    /// <summary>A lost connection (an HttpClient header timeout included, Phase 57 S2), not
    /// <paramref name="ct"/>'s cancel or quit's.</summary>
    private bool IsTransportFailure(Exception failure, CancellationToken ct) =>
        !_session.IsCancellationRequested && ForgeChat.IsTransportFailure(failure, ct);

    /// <summary>Runs one busy step. Work stopped by Ctrl-D ends here: the app is quitting.</summary>
    private async Task WhileBusyAsync(Func<Task> work)
    {
        _busy = true;
        try { await work(); }
        catch (OperationCanceledException) when (_session.IsCancellationRequested) { }
        finally { _busy = false; }
    }

    /// <summary>Waits for the live stream to end, then closes the conversation while the UI thread
    /// still runs: a running file operation is cancelled and its report shown, and any turn follow
    /// stops and is waited for (the turn keeps running on Forge). Then it stops the app. The caller's later dispose of
    /// the same conversation does nothing more.</summary>
    private async Task<TerminalLoopResult> StopAsync()
    {
        await _link.Live;
        await _conversation.DisposeAsync();
        try { await _following; }
        catch (OperationCanceledException) { }
        ForgeChat.DrainNotices(_notices, ShowError);
        return TerminalLoopResult.Stop;
    }

    // ── Input ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Enter: show the message and a pending reply at once, and queue the submit for the
    /// next UI step. While the link sleeps or catches up, Enter wakes it and sends once it is ready.
    /// Before the tile images are sent, while the conversation is opening, a call is in flight, or a
    /// turn from any window runs, Enter does nothing and the text stays in the composer.</summary>
    private void Send(string text)
    {
        if (EditFile.Parse(text) is { } edit)
        {
            OpenEditor(edit);
            return;
        }
        if (_busy || _sendOnWake) return;
        if (!_link.Ready)
        {
            _sendOnWake = true;
            Wake();
            return;
        }
        if (!_screen.HasTiles || _turnRunning || _pendingMessage is not null || !_following.IsCompleted ||
            _session.IsCancellationRequested || string.IsNullOrWhiteSpace(text)) return;
        var sent = new SentMessage(Guid.NewGuid(), text);
        _pendingMessage = sent;
        _screen.Composer.Text = "";
        ShowBlocks(Transcript.Submit(_blocks, sent.CommandId, sent.Text, DateTimeOffset.Now));
    }

    // ── /edit ───────────────────────────────────────────────────────────────────────────────

    /// <summary>/edit: never sent. Without a path it shows the usage; a file that cannot be opened
    /// shows why; otherwise the editor replaces the transcript and takes focus. Nothing happens
    /// before the images arrive (the screen is not up yet).</summary>
    private void OpenEditor(EditCommand edit)
    {
        if (!_screen.HasTiles || _screen.Editing) return;
        _screen.Composer.Text = "";
        if (edit.Path is not { } path)
        {
            ShowLine(EditCommand.Usage);
            return;
        }
        if (OpenFile(path) is not { } file) return;
        var editor = new FileEditor(file, _styles, CloseEditor, _screen.Interaction);
        _screen.ShowEditor(editor.View);
        _screen.Root.App?.Focus(editor.Editor);
    }

    /// <summary>The file at <paramref name="path"/>, relative to the folder forge chat was started
    /// in; null, with a notice, when it cannot be opened.</summary>
    private EditFile? OpenFile(string path)
    {
        try
        {
            return EditFile.Open(path, Directory.GetCurrentDirectory(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            ShowLine($"cannot open {path}: {failure.Message}");
            return null;
        }
    }

    private void CloseEditor()
    {
        _screen.ShowChat();
        _screen.Root.App?.Focus(_screen.Composer);
    }

    /// <summary>Chat with a mission on the start page: today's chat, once the images are there.</summary>
    private void OpenChat()
    {
        if (_screen.HasTiles) CloseEditor();
    }

    /// <summary>Ctrl-C: stop the turn this window submitted (once Forge has it); nothing when it
    /// has none.</summary>
    private void StopRun()
    {
        if (_ownTurn is not null) _stopOwnTurn = true;
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
        CanExecute = _ => !_screen.Editing,
        Execute = _ => _sessionSource.Cancel(),
    };

    /// <summary>A key of this window's own: it wakes the link first (commands never reach KeyDown).
    /// While the file editor is open the key goes on to the editor (the command stands aside and
    /// does not consume it).</summary>
    private void AddKey(KeyGesture gesture, string id, Action action) =>
        _screen.Root.AddCommand(new Command
        {
            Id = id, LabelMarkup = string.Empty, Gesture = gesture,
            CanExecute = _ => !_screen.Editing, ConsumesGestureWhenUnavailable = false, Execute = _ =>
            {
                _screen.Interaction.Reset();
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

    /// <summary>A live event: the turn state follows it, then it is shown.</summary>
    private void ShowLive(ConversationEvent item)
    {
        _turnRunning = TurnRunning(_turnRunning, item);
        ShowBlocks(Transcript.ApplyLive(_blocks, item));
    }

    /// <summary>The link's state as one notice line (the live events remove the reconnect line).</summary>
    private void ShowNotice(LinkNotice notice) => ShowBlocks(notice switch
    {
        LinkNotice.Idle => Transcript.Idle(_blocks),
        LinkNotice.Awake => Transcript.Awake(_blocks),
        _ => Transcript.Reconnecting(_blocks),
    });

    private void ShowLine(string text) => ShowBlocks([.. _blocks, new NoticeLine(text)]);

    private void ShowError(string message) => ShowBlocks([.. _blocks, new ErrorLine($"error: {message}")]);

    private void ShowBlocks(IReadOnlyList<TranscriptBlock> blocks)
    {
        _blocks = blocks;
        _screen.TurnRunning = _turnRunning;
        _screen.Show(_blocks);
    }

    // ── Rules ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A turn is in flight (S5): one runs in the conversation, from any window, or this
    /// window waits for a reply.</summary>
    private bool InFlight() => _turnRunning || Transcript.Replying(_blocks) is not null;

    /// <summary>Whether a turn from any window is running after <paramref name="item"/>: a message
    /// starts one, a terminal run status ends it.</summary>
    internal static bool TurnRunning(bool running, ConversationEvent item) => item.Kind switch
    {
        ConversationEventKind.UserMessage => true,
        ConversationEventKind.RunStatus when item.RunStatus is { } status && ForgeChat.IsTerminal(status) => false,
        _ => running,
    };

    /// <summary>A message sent from the composer; <see cref="CommandId"/> is the id Forge echoes as
    /// the <c>UserMessage</c> event id.</summary>
    private sealed record SentMessage(Guid CommandId, string Text);
}

/// <summary>How the TUI ended: quit by the user, or stopped because the terminal gave no cell
/// size.</summary>
internal enum TuiExit { Quit, NoCellSize }
