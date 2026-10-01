using ForgeMission.Application;
using ForgeMission.Conversations.Contracts;

namespace ForgeMission.Cli.Tui;

/// <summary>A change of the connection the transcript shows.</summary>
internal enum LinkNotice { Idle, Awake, Reconnecting }

// forge chat TUI (Phase 57 S5): the one owner of the live connection, with no terminal. It follows
// the conversation from a cursor. When the stream ends with no turn in flight it sleeps instead of
// reconnecting, so a forgotten window does not keep ForgeAPI awake. A wake first reads what was
// missed from the saved cursor, then reopens the live stream, so nothing is missed. During a turn a
// stream that ends reconnects at once; only a transport failure is reported, once until the next
// event. Every call and continuation runs on the TUI's UI thread, so a wake and a stream end never
// interleave; a wake waits for the previous stream before reading, so two never run at once.
internal sealed class ChatLink(IMissionConversationService conversations, Guid conversationId, Func<bool> inFlight,
    Action<ConversationEvent> show, Action<LinkNotice> notice, CancellationToken session)
{
    private long _cursor;
    private bool _lostReported;
    private Task _catchUp = Task.CompletedTask;

    /// <summary>The live stream (or the catch-up before it); its result is the last cursor.</summary>
    public Task<long> Live { get; private set; } = Task.FromResult(0L);

    /// <summary>The stream ended with nothing in flight and was not reopened.</summary>
    public bool Asleep { get; private set; }

    /// <summary>Awake and caught up: a send now sees the conversation as it is.</summary>
    public bool Ready => !Asleep && _catchUp.IsCompleted;

    /// <summary>Starts the live stream after <paramref name="cursor"/> (the end of the replay).</summary>
    public void Start(long cursor)
    {
        _cursor = cursor;
        Live = FollowAsync();
    }

    /// <summary>Wakes a sleeping link: the catch-up, then the live stream. Returns the catch-up in
    /// progress, completed when there is none; after the session ends it does nothing.</summary>
    public Task WakeAsync()
    {
        if (!Asleep || session.IsCancellationRequested) return _catchUp;
        Asleep = false;
        notice(LinkNotice.Awake);
        var catchUp = CatchUpAsync(Live);
        _catchUp = catchUp;
        Live = ResumeAsync(catchUp);
        return catchUp;
    }

    private async Task<long> FollowAsync()
    {
        try
        {
            return await ForgeChat.StreamAsync(conversations, conversationId, _cursor, includeDeltas: true, Show,
                ends: _ => false, Ended, session);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            return _cursor;
        }
    }

    /// <summary>Reads the events stored after the saved cursor. A transport failure sends it back
    /// to sleep (the next key retries); the session's cancel ends it quietly.</summary>
    private async Task CatchUpAsync(Task<long> previous)
    {
        await previous;
        try
        {
            var last = (await conversations.GetConversationAsync(conversationId, session)).Snapshot.LastSequence;
            await ForgeChat.ReplayAsync(conversations, conversationId, _cursor, last, Show, session);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
        }
        catch (Exception failure) when (ForgeChat.IsTransportFailure(failure, session))
        {
            Sleep();
        }
    }

    private async Task<long> ResumeAsync(Task catchUp)
    {
        await catchUp;
        return Asleep || session.IsCancellationRequested ? _cursor : await FollowAsync();
    }

    /// <summary>The stream ended: sleep with nothing in flight, else reopen (reporting a transport
    /// failure once).</summary>
    private bool Ended(Exception? failure)
    {
        if (!inFlight())
        {
            Sleep();
            return false;
        }
        if (failure is not null && !_lostReported)
        {
            _lostReported = true;
            notice(LinkNotice.Reconnecting);
        }
        return true;
    }

    private void Sleep()
    {
        Asleep = true;
        notice(LinkNotice.Idle);
    }

    /// <summary>An event: the connection works again, and the cursor follows it (a delta never moves it).</summary>
    private void Show(ConversationEvent item)
    {
        _lostReported = false;
        if (item.Kind != ConversationEventKind.ParticipantDelta) _cursor = item.Sequence;
        show(item);
    }
}
