using ForgeMission.Application;
using ForgeMission.Conversations.Contracts;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (53.5): the full-screen chat on a terminal. It runs the same conversation calls
// as the line mode (ForgeChat.ReplayAsync / FollowTurnAsync, SubmitAsync, CancelAsync) inside
// XenoAtom's single-threaded UI loop: awaited calls resume on the UI thread, so input and rendering
// keep running while a turn streams. This file owns the turn lifecycle and the keys.
internal sealed class ChatTui
{
    private readonly IMissionConversationService _conversations;
    private readonly Guid _conversationId;
    private readonly ChatScreen _screen;
    private readonly CancellationToken _session;
    private IReadOnlyList<TranscriptBlock> _blocks = [];
    // The last event shown. Kept per event, so following again after Ctrl-C resumes where the
    // cancelled stream stopped.
    private long _cursor;
    private bool _opened;
    private bool _busy;
    private string? _pendingMessage;
    private CancellationTokenSource? _turn;

    private ChatTui(IMissionConversationService conversations, Guid conversationId, ChatHeader header, CancellationToken session)
    {
        _conversations = conversations;
        _conversationId = conversationId;
        _session = session;
        _screen = new ChatScreen(header);
        _screen.Composer.Accepted((_, e) => Send(e.Text));
        AddKey(new KeyGesture(TerminalChar.CtrlC, TerminalModifiers.Ctrl), "Forge.StopRun", StopRun);
        AddKey(new KeyGesture(TerminalKey.PageUp), "Forge.PageUp", _screen.PageUp);
        AddKey(new KeyGesture(TerminalKey.PageDown), "Forge.PageDown", _screen.PageDown);
    }

    /// <summary>Runs the TUI until Ctrl-D. A turn still running on quit keeps running on Forge;
    /// only this process stops following it.</summary>
    public static async Task<int> RunAsync(IMissionConversationService conversations, Guid conversationId, ChatHeader header)
    {
        using var session = new CancellationTokenSource();
        var tui = new ChatTui(conversations, conversationId, header, session.Token);
        try
        {
            await Terminal.RunAsync(tui._screen.Root, tui.UpdateAsync,
                new TerminalRunOptions { ExitGesture = new KeyGesture(TerminalChar.CtrlD, TerminalModifiers.Ctrl) });
        }
        finally
        {
            await session.CancelAsync();
        }
        return 0;
    }

    /// <summary>The UI loop's one async step: open the conversation on the first tick, then run a
    /// turn whenever a message is waiting.</summary>
    private async ValueTask<TerminalLoopResult> UpdateAsync(TerminalRunningContext context)
    {
        if (!_opened)
        {
            _opened = true;
            context.App.Focus(_screen.Composer);
            await WhileBusyAsync(OpenAsync);
        }
        else if (_pendingMessage is { } message)
        {
            _pendingMessage = null;
            await WhileBusyAsync(() => RunTurnAsync(message));
        }
        return TerminalLoopResult.Continue;
    }

    // ── Conversation ────────────────────────────────────────────────────────────────────────

    /// <summary>Replays the conversation, then follows a turn that is still running. That turn
    /// cannot be stopped from here: the snapshot carries no turn id.</summary>
    private async Task OpenAsync()
    {
        var snapshot = (await _conversations.GetConversationAsync(_conversationId, _session)).Snapshot;
        await ForgeChat.ReplayAsync(_conversations, _conversationId, snapshot.LastSequence, Show, _session);
        if (snapshot.ActiveRunId is { } running && !ForgeChat.IsTerminal(snapshot.Status))
            await ForgeChat.FollowTurnAsync(_conversations, _conversationId, _cursor, running, Show, _session);
    }

    /// <summary>Submits one message and follows its turn. Ctrl-C cancels the turn on Forge, and the
    /// turn is still followed to its end so the transcript shows how it ended. A transport failure
    /// becomes an error line; the chat stays open.</summary>
    private async Task RunTurnAsync(string message)
    {
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(_session);
        _turn = turn;
        try
        {
            var submitted = await _conversations.SubmitAsync(_conversationId, Guid.NewGuid(), message, _session);
            await FollowOrCancelAsync(submitted, turn.Token);
        }
        catch (HttpRequestException failure)
        {
            ShowNotice($"error: {failure.Message}");
        }
        finally
        {
            _turn = null;
        }
    }

    private async Task FollowOrCancelAsync(SubmitMissionTurnResponse submitted, CancellationToken turn)
    {
        try
        {
            await ForgeChat.FollowTurnAsync(_conversations, _conversationId, _cursor, submitted.TurnAttemptId, Show, turn);
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested && !_session.IsCancellationRequested)
        {
            await _conversations.CancelAsync(_conversationId, submitted.TurnId, submitted.TurnAttemptId, Guid.NewGuid(), _session);
            await ForgeChat.FollowTurnAsync(_conversations, _conversationId, _cursor, submitted.TurnAttemptId, Show, _session);
        }
    }

    private async Task WhileBusyAsync(Func<Task> work)
    {
        _busy = true;
        try { await work(); }
        finally { _busy = false; }
    }

    // ── Input ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Enter: queue the message for the next UI step. While the conversation is opening
    /// or a turn runs, Enter does nothing and the text stays in the composer.</summary>
    private void Send(string text)
    {
        if (_busy || string.IsNullOrWhiteSpace(text)) return;
        _pendingMessage = text;
        _screen.Composer.Text = "";
    }

    /// <summary>Ctrl-C: stop the turn this session started; nothing when idle.</summary>
    private void StopRun() => _turn?.Cancel();

    private void AddKey(KeyGesture gesture, string id, Action action) =>
        _screen.Root.AddCommand(new Command { Id = id, LabelMarkup = string.Empty, Gesture = gesture, Execute = _ => action() });

    // ── Output ──────────────────────────────────────────────────────────────────────────────

    private void Show(ConversationEvent item)
    {
        _cursor = item.Sequence;
        _blocks = Transcript.Apply(_blocks, item);
        _screen.Show(_blocks);
    }

    private void ShowNotice(string text)
    {
        _blocks = [.. _blocks, new NoticeLine(text)];
        _screen.Show(_blocks);
    }
}
