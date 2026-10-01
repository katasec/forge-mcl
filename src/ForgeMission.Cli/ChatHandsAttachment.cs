using ForgeMission.Application;
using ForgeMission.Application.Transport;
using ForgeMission.Conversations.Contracts;

namespace ForgeMission.Cli;

// forge chat --hands (Phase 55, H5/H7): the one live hands attachment of a chat. The followed turn's
// events drive it (in the TUI only the window's own turn, 53.9 L1): each MissionHandsRequested starts one Execute (claim → read → run in the project
// folder → submit) on the thread pool, off the stream loop, serialized behind the previous one; Begin
// checks once for a request already waiting. Exit or Ctrl-C while a tool runs cancels the hands
// attempt before the Client is disposed. Failures are reported, never thrown into the stream loop.
// Reports are posted to the context Begin ran on (the TUI's UI thread); the work itself never needs
// that context, so waiting for it after the UI loop has stopped cannot hang.
internal sealed class ChatHandsAttachment(
    IMissionHandsConversationService hands, string sessionId, Guid conversationId, Guid attachmentId) : IAsyncDisposable
{
    private const string ExitReason = "forge chat stopped while a file operation was running.";
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task _executing = Task.CompletedTask;
    private Action<string> _report = _ => { };

    /// <summary>Starts reporting to <paramref name="report"/> and executes a request that was
    /// already waiting when this attachment was made.</summary>
    public void Begin(Action<string> report)
    {
        var context = SynchronizationContext.Current;
        _report = context is null ? report : message => context.Post(_ => report(message), null);
        Chain(ExecuteIfWaitingAsync);
    }

    /// <summary>Feeds one live event of the followed turn; only a new hands request acts.</summary>
    public void OnEvent(ConversationEvent item)
    {
        if (item.Kind == ConversationEventKind.MissionHandsRequested)
            Chain(ExecuteOnceAsync);
    }

    /// <summary>Cancels the hands attempt when a file operation is still running, then waits for
    /// it to end. Nothing happens when idle.</summary>
    public async Task CancelInFlightAsync()
    {
        Task running;
        lock (_gate) running = _executing;
        if (running.IsCompleted) return;

        await CancelAttemptAsync();
        await _stopping.CancelAsync();
        await running;
    }

    public async ValueTask DisposeAsync()
    {
        await CancelInFlightAsync();
        _stopping.Dispose();
    }

    /// <summary>Asks Host to cancel the in-flight attempt; Application then cancels Bob's local
    /// operation. A refusal or transport failure is reported; the local stop still follows.</summary>
    private async Task CancelAttemptAsync()
    {
        try
        {
            var cancelled = await hands.CancelAsync(
                new CancelMissionHandsRequest(sessionId, conversationId, attachmentId, ExitReason), CancellationToken.None);
            if (!cancelled.Cancelled)
                _report($"file operation could not be cancelled: {cancelled.Error}");
        }
        catch (HttpRequestException failure)
        {
            _report($"file operation could not be cancelled: {failure.Message}");
        }
    }

    // ── Execute ─────────────────────────────────────────────────────────────────────────────

    private void Chain(Func<Task> step)
    {
        lock (_gate)
        {
            var previous = _executing;
            _executing = Task.Run(async () =>
            {
                await previous;
                await step();
            });
        }
    }

    /// <summary>A request is waiting for this attachment when Host reports it attached with an
    /// unclaimed request (<see cref="MissionHandsStatus.Attached"/>).</summary>
    private Task ExecuteIfWaitingAsync() => GuardAsync(async ct =>
    {
        var status = await hands.GetStatusAsync(new GetMissionHandsStatusRequest(sessionId, conversationId, attachmentId), ct);
        if (status.Error is { } error)
            _report($"file access status: {error}");
        else if (status.Status == MissionHandsStatus.Attached)
            await ExecuteCoreAsync(ct);
    });

    private Task ExecuteOnceAsync() => GuardAsync(ExecuteCoreAsync);

    private async Task ExecuteCoreAsync(CancellationToken ct)
    {
        var executed = await hands.ExecuteAsync(new ExecuteMissionHandsRequest(sessionId, conversationId, attachmentId), ct);
        if (executed.Error is { } error)
            _report($"file operation: {error}{(executed.Reason is { } reason ? $" ({reason})" : "")}");
        else if (executed.AcceptedSequence is null)
            _report($"file operation result was not accepted: {executed.Reason}");
    }

    /// <summary>Runs one step so that a failure becomes a reported line: the chat keeps running and
    /// the turn can still be stopped with Ctrl-C. A step stopped by exit ends quietly.</summary>
    private async Task GuardAsync(Func<CancellationToken, Task> step)
    {
        try
        {
            await step(_stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (HttpRequestException failure)
        {
            _report($"file operation failed: {failure.Message}; press Ctrl-C to stop the turn.");
        }
    }
}
