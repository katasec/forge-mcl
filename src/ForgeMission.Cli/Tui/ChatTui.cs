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
    private SentMessage? _pendingMessage;
    private CancellationTokenSource? _turn;

    private ChatTui(IMissionConversationService conversations, Guid conversationId, ChatHeader header, ForgeTheme theme,
        CancellationToken session)
    {
        _conversations = conversations;
        _conversationId = conversationId;
        _session = session;
        _screen = new ChatScreen(header, new ForgeStyles(theme));
        _screen.Composer.Accepted((_, e) => Send(e.Text));
        AddKey(new KeyGesture(TerminalChar.CtrlC, TerminalModifiers.Ctrl), "Forge.StopRun", StopRun);
        AddKey(new KeyGesture(TerminalKey.PageUp), "Forge.PageUp", _screen.PageUp);
        AddKey(new KeyGesture(TerminalKey.PageDown), "Forge.PageDown", _screen.PageDown);
    }

    /// <summary>Runs the TUI until Ctrl-D. A turn still running on quit keeps running on Forge;
    /// only this process stops following it.</summary>
    public static async Task<int> RunAsync(IMissionConversationService conversations, Guid conversationId, ChatHeader header,
        ForgeTheme theme)
    {
        using var session = new CancellationTokenSource();
        var tui = new ChatTui(conversations, conversationId, header, theme, session.Token);
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
        else if (_pendingMessage is { } sent)
        {
            _pendingMessage = null;
            await WhileBusyAsync(() => RunTurnAsync(sent));
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

    /// <summary>Submits one message (already shown as pending) with its command id and follows its
    /// turn. Ctrl-C cancels the turn on Forge, and the turn is still followed to its end so the
    /// transcript shows how it ended. A transport failure becomes an error line; a message Forge
    /// did not accept goes back into the composer. The chat stays open.</summary>
    private async Task RunTurnAsync(SentMessage sent)
    {
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(_session);
        _turn = turn;
        try
        {
            if (await SubmitAsync(sent) is { } submitted)
                await FollowOrCancelAsync(submitted, turn.Token);
        }
        catch (HttpRequestException failure)
        {
            ShowBlocks(Transcript.TurnFailed(_blocks, sent.CommandId, failure.Message));
        }
        finally
        {
            _turn = null;
        }
    }

    /// <summary>Submits the message; on a transport failure shows the error, restores the text to
    /// the composer, and returns null.</summary>
    private async Task<SubmitMissionTurnResponse?> SubmitAsync(SentMessage sent)
    {
        try
        {
            return await _conversations.SubmitAsync(_conversationId, sent.CommandId, sent.Text, _session);
        }
        catch (HttpRequestException failure)
        {
            ShowBlocks(Transcript.SubmitFailed(_blocks, sent.CommandId, failure.Message));
            _screen.Composer.Text = sent.Text;
            return null;
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

    /// <summary>Enter: show the message and a pending reply at once, and queue the submit for the
    /// next UI step. While the conversation is opening or a turn runs, Enter does nothing and the
    /// text stays in the composer.</summary>
    private void Send(string text)
    {
        if (_busy || _pendingMessage is not null || string.IsNullOrWhiteSpace(text)) return;
        var sent = new SentMessage(Guid.NewGuid(), text);
        _pendingMessage = sent;
        _screen.Composer.Text = "";
        ShowBlocks(Transcript.Submit(_blocks, sent.CommandId, sent.Text));
    }

    /// <summary>Ctrl-C: stop the turn this session started; nothing when idle.</summary>
    private void StopRun() => _turn?.Cancel();

    private void AddKey(KeyGesture gesture, string id, Action action) =>
        _screen.Root.AddCommand(new Command { Id = id, LabelMarkup = string.Empty, Gesture = gesture, Execute = _ => action() });

    // ── Output ──────────────────────────────────────────────────────────────────────────────

    private void Show(ConversationEvent item)
    {
        _cursor = item.Sequence;
        ShowBlocks(Transcript.Apply(_blocks, item));
    }

    private void ShowBlocks(IReadOnlyList<TranscriptBlock> blocks)
    {
        _blocks = blocks;
        _screen.Show(_blocks);
    }

    /// <summary>A message sent from the composer; <see cref="CommandId"/> is the id Forge echoes as
    /// the <c>UserMessage</c> event id.</summary>
    private sealed record SentMessage(Guid CommandId, string Text);
}
