using System.Globalization;
using System.Text.Json;
using ForgeMission.Conversations.Contracts;

namespace ForgeMission.Cli.Tui;

/// <summary>One visible piece of the chat transcript.</summary>
public abstract record TranscriptBlock;

/// <summary>What the person sent, shown as the right-aligned pill, and when (Phase 59).</summary>
public sealed record YouBlock(string Text, DateTimeOffset Sent) : TranscriptBlock;

/// <summary>A participant's reply card. <paramref name="Text"/> is null while the reply is pending;
/// <paramref name="Mission"/> titles a final result that differs from the last step's text.
/// <paramref name="Streaming"/> is true while live deltas grow the text, until the step's own
/// message or the turn's end (Phase 56 Task 4: a heading at its end may still be incomplete).
/// <paramref name="Sent"/> is the time of the event that started the card (Phase 59).</summary>
public sealed record ParticipantCard(string Title, string? Text, string Mission, DateTimeOffset Sent, bool Streaming = false) : TranscriptBlock;

/// <summary>A message shown the moment it is sent, before Forge echoes it back as a
/// <c>UserMessage</c> whose event id is <paramref name="CommandId"/>; <paramref name="Sent"/> is when
/// it was sent.</summary>
public sealed record PendingYouBlock(Guid CommandId, string Text, DateTimeOffset Sent) : TranscriptBlock;

/// <summary>The reply card shown the moment a message is sent, before the first participant
/// starts; it has no title until then.</summary>
public sealed record PendingReplyBlock(Guid CommandId) : TranscriptBlock;

/// <summary>A muted one-line notice: a run that did not complete, or the state of the connection.</summary>
public sealed record NoticeLine(string Text) : TranscriptBlock;

/// <summary>An error from the run or the connection.</summary>
public sealed record ErrorLine(string Text) : TranscriptBlock;

/// <summary>One tool use through hands (Phase 55, H8), e.g. <c>Read notes.txt</c>;
/// <paramref name="Outcome"/> is null while it runs.</summary>
public sealed record HandsLine(string Label, string? Outcome) : TranscriptBlock;

// forge chat TUI (53.5, 53.7, 53.8): the one mapping from conversation events to transcript blocks,
// used for both the replay of a reopened conversation and a live turn (including live reply deltas). A sent message is shown at once
// as pending blocks keyed by its command id; Forge's own events then take their place. Replay only
// applies Forge's events, so it never shows a pending block. Pure: no terminal, no Client.
public static class Transcript
{
    // The connection notices (Phase 57 S5): each shows only while its state lasts.
    private static readonly NoticeLine ReconnectingNotice = new("connection lost; reconnecting");
    private static readonly NoticeLine IdleNotice = new("idle — reconnects when you type");

    /// <summary>Returns the transcript after <paramref name="item"/>; unchanged when the event has
    /// no visible block.</summary>
    public static IReadOnlyList<TranscriptBlock> Apply(IReadOnlyList<TranscriptBlock> blocks, ConversationEvent item) => item.Kind switch
    {
        ConversationEventKind.UserMessage => AddUserMessage(blocks, item.EventId, new YouBlock(item.Text ?? "", item.OccurredAtUtc)),
        ConversationEventKind.ParticipantStarted => AddStartedCard(blocks, StartedCard(item.Text ?? "", item.OccurredAtUtc)),
        ConversationEventKind.ParticipantDelta => AppendToLatestCard(blocks, item.Text ?? ""),
        ConversationEventKind.ParticipantMessage when item.Attempt is not null => FillLatestCard(blocks, item.Text ?? "", item.OccurredAtUtc),
        ConversationEventKind.ParticipantMessage => AddFinalResult(blocks, item),
        ConversationEventKind.Error => AddError(blocks, item),
        ConversationEventKind.RunStatus when item.RunStatus is { } status && IsTerminal(status) => EndTurn(blocks, status),
        ConversationEventKind.MissionHandsRequested => AddHandsLine(blocks, new HandsLine(HandsLabel(item), null)),
        ConversationEventKind.MissionHandsResult or ConversationEventKind.MissionHandsCancelled or
            ConversationEventKind.MissionHandsInterrupted => EndHandsLine(blocks, HandsOutcome(item)),
        _ => blocks,
    };

    /// <summary>Applies a live event (53.9 L1): as <see cref="Apply"/>, after removing a reconnect
    /// notice (events flow again), and a message sent from
    /// another window (an echo with no pending pill of this window) also gets a pending reply, so it
    /// shows as replying until its first participant starts.</summary>
    public static IReadOnlyList<TranscriptBlock> ApplyLive(IReadOnlyList<TranscriptBlock> blocks, ConversationEvent item)
    {
        var applied = Apply(WithoutLast(blocks, ReconnectingNotice), item);
        if (item.Kind != ConversationEventKind.UserMessage) return applied;
        var sentHere = IndexOf(blocks, block => block is PendingYouBlock you && you.CommandId == item.EventId) >= 0;
        return sentHere ? applied : Append(applied, new PendingReplyBlock(item.EventId));
    }

    /// <summary>A transport failure during a turn: shown until the next live event.</summary>
    public static IReadOnlyList<TranscriptBlock> Reconnecting(IReadOnlyList<TranscriptBlock> blocks) =>
        Append(blocks, ReconnectingNotice);

    /// <summary>The live stream ended with nothing in flight and was not reopened: shown until a
    /// key wakes it.</summary>
    public static IReadOnlyList<TranscriptBlock> Idle(IReadOnlyList<TranscriptBlock> blocks) => Append(blocks, IdleNotice);

    /// <summary>A key woke the stream: the idle notice goes, wherever it is.</summary>
    public static IReadOnlyList<TranscriptBlock> Awake(IReadOnlyList<TranscriptBlock> blocks) => WithoutLast(blocks, IdleNotice);

    /// <summary>The tool and the file it names: <c>Read notes.txt</c>, or the tool alone.</summary>
    public static string HandsLabel(ConversationEvent item)
    {
        if (item.MissionHandsRequest is not { } request) return "Tool";
        return FilePath(request.Arguments) is { } path ? $"{request.ToolName} {path}" : request.ToolName;
    }

    /// <summary>How a tool use ended, in words.</summary>
    public static string HandsOutcome(ConversationEvent item) => item.Kind switch
    {
        ConversationEventKind.MissionHandsCancelled => "cancelled",
        ConversationEventKind.MissionHandsInterrupted => "interrupted",
        _ => item.MissionHandsOutcome switch
        {
            MissionToolOutcome.Succeeded => "succeeded",
            MissionToolOutcome.DeniedOutOfProfile => "denied: outside the allowed tools",
            MissionToolOutcome.DeniedByPolicy => "denied by policy",
            MissionToolOutcome.DeniedByOperator => "denied",
            MissionToolOutcome.Cancelled => "cancelled",
            MissionToolOutcome.Interrupted => "interrupted",
            MissionToolOutcome.Failed => "failed",
            _ => "finished",
        },
    };

    /// <summary>The one line a <see cref="HandsLine"/> shows.</summary>
    public static string HandsText(HandsLine line) =>
        line.Outcome is null ? $"{line.Label} …" : $"{line.Label} → {line.Outcome}";

    /// <summary>A message was just sent with <paramref name="commandId"/> at <paramref name="sent"/>:
    /// show it and a pending reply now, without waiting for Forge.</summary>
    public static IReadOnlyList<TranscriptBlock> Submit(IReadOnlyList<TranscriptBlock> blocks, Guid commandId, string text,
        DateTimeOffset sent) =>
        [.. blocks, new PendingYouBlock(commandId, text, sent), new PendingReplyBlock(commandId)];

    /// <summary>Forge did not accept the message: its pending blocks become an error line.</summary>
    public static IReadOnlyList<TranscriptBlock> SubmitFailed(IReadOnlyList<TranscriptBlock> blocks, Guid commandId, string message) =>
        Append(WithoutPending(blocks, commandId), new ErrorLine($"error: {message}"));

    /// <summary>Who is replying at the end of the transcript: the expert, <c>""</c> while no
    /// participant has started yet, or null when nothing is pending.</summary>
    public static string? Replying(IReadOnlyList<TranscriptBlock> blocks) => blocks.Count == 0 ? null : blocks[^1] switch
    {
        ParticipantCard { Text: null } card => card.Title,
        PendingReplyBlock => "",
        _ => null,
    };

    /// <summary>Who is streaming a reply (Phase 56 Task 5): the expert whose latest card live deltas
    /// still grow, or null. With <see cref="Replying"/> it keeps the progress row and its spinner
    /// until the reply ends; <see cref="Replying"/> alone stays the idle-sleep rule.</summary>
    public static string? Streaming(IReadOnlyList<TranscriptBlock> blocks)
    {
        var index = LatestCardIndex(blocks);
        return index >= 0 && blocks[index] is ParticipantCard { Streaming: true } card ? card.Title : null;
    }

    /// <summary>When a message was sent, as every message shows it (Phase 59): local time in the
    /// system's short time format.</summary>
    public static string TimeOf(DateTimeOffset sent) => sent.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    /// <summary>Forge's echo of a sent message takes the place of its pending pill.</summary>
    private static IReadOnlyList<TranscriptBlock> AddUserMessage(IReadOnlyList<TranscriptBlock> blocks, Guid eventId, YouBlock you)
    {
        var index = IndexOf(blocks, block => block is PendingYouBlock pending && pending.CommandId == eventId);
        return index < 0 ? Append(blocks, you) : ReplaceAt(blocks, index, you);
    }

    /// <summary>The first participant to start takes the place of the pending reply (one turn
    /// runs at a time, so there is at most one).</summary>
    private static IReadOnlyList<TranscriptBlock> AddStartedCard(IReadOnlyList<TranscriptBlock> blocks, ParticipantCard card)
    {
        var index = IndexOf(blocks, block => block is PendingReplyBlock);
        return index < 0 ? Append(blocks, card) : ReplaceAt(blocks, index, card);
    }

    /// <summary>The runner titles a step <c>Mission:Expert</c>; the card shows the expert.</summary>
    private static ParticipantCard StartedCard(string step, DateTimeOffset sent)
    {
        var colon = step.LastIndexOf(':');
        return colon < 0
            ? new ParticipantCard(step, null, step, sent)
            : new ParticipantCard(step[(colon + 1)..], null, step[..colon], sent);
    }

    /// <summary>A live reply delta (53.8) grows the latest card; the step's own message then replaces
    /// the card's text with the final reply.</summary>
    private static IReadOnlyList<TranscriptBlock> AppendToLatestCard(IReadOnlyList<TranscriptBlock> blocks, string text)
    {
        var index = LatestCardIndex(blocks);
        if (index < 0) return blocks;

        var card = (ParticipantCard)blocks[index];
        return ReplaceAt(blocks, index, card with { Text = (card.Text ?? "") + text, Streaming = true });
    }

    private static IReadOnlyList<TranscriptBlock> FillLatestCard(IReadOnlyList<TranscriptBlock> blocks, string text, DateTimeOffset sent)
    {
        var index = LatestCardIndex(blocks);
        if (index < 0) return Append(blocks, new ParticipantCard("Forge", text, "Forge", sent));

        var updated = blocks.ToList();
        updated[index] = ((ParticipantCard)blocks[index]) with { Text = text, Streaming = false };
        return updated;
    }

    /// <summary>The runner sends the mission's final result (a message with no attempt) after the
    /// last step's message; it repeats the reply when its text is the same. The TUI and the line
    /// mode both show a repeat once.</summary>
    public static bool RepeatsLastReply(string? lastReplyText, ConversationEvent item) =>
        item is { Kind: ConversationEventKind.ParticipantMessage, Attempt: null } && (item.Text ?? "") == lastReplyText;

    /// <summary>The mission's final result is shown, as its own card titled with the mission, only
    /// when it does not repeat the last reply.</summary>
    private static IReadOnlyList<TranscriptBlock> AddFinalResult(IReadOnlyList<TranscriptBlock> blocks, ConversationEvent item)
    {
        var text = item.Text ?? "";
        var index = LatestCardIndex(blocks);
        if (index < 0) return Append(blocks, new ParticipantCard("Forge", text, "Forge", item.OccurredAtUtc));

        var last = (ParticipantCard)blocks[index];
        if (RepeatsLastReply(last.Text, item)) return blocks;
        return Append(blocks, new ParticipantCard(last.Mission, text, last.Mission, item.OccurredAtUtc));
    }

    /// <summary>A mission-level error (no attempt) that repeats the step error just shown is
    /// shown once.</summary>
    private static IReadOnlyList<TranscriptBlock> AddError(IReadOnlyList<TranscriptBlock> blocks, ConversationEvent item)
    {
        var notice = new ErrorLine($"error: {item.Reason ?? item.Text}");
        if (item.Attempt is null && blocks.Count > 0 && blocks[^1] == notice) return blocks;
        return Append(blocks, notice);
    }

    /// <summary>A turn has ended: a card that never received text is dropped (its pending body is
    /// shown only while the turn runs), a card cut off mid-stream stops streaming, and a run that
    /// did not complete adds a notice.</summary>
    private static IReadOnlyList<TranscriptBlock> EndTurn(IReadOnlyList<TranscriptBlock> blocks, ConversationRunStatus status)
    {
        IReadOnlyList<TranscriptBlock> ended = blocks
            .Where(block => block is not (ParticipantCard { Text: null } or PendingReplyBlock))
            .Select(block => block switch
            {
                PendingYouBlock you => new YouBlock(you.Text, you.Sent),
                ParticipantCard { Streaming: true } card => card with { Streaming = false },
                _ => block,
            })
            .ToList();
        return status == ConversationRunStatus.Completed
            ? ended
            : Append(ended, new NoticeLine($"(run {status.ToString().ToLowerInvariant()})"));
    }

    /// <summary>A tool use goes above the reply card still waiting for text, so the reply that
    /// follows it stays last; otherwise it is appended.</summary>
    private static IReadOnlyList<TranscriptBlock> AddHandsLine(IReadOnlyList<TranscriptBlock> blocks, HandsLine line)
    {
        if (blocks.Count == 0 || blocks[^1] is not (ParticipantCard { Text: null } or PendingReplyBlock))
            return Append(blocks, line);

        var updated = blocks.ToList();
        updated.Insert(blocks.Count - 1, line);
        return updated;
    }

    /// <summary>The outcome completes the latest tool use still running (one runs at a time).</summary>
    private static IReadOnlyList<TranscriptBlock> EndHandsLine(IReadOnlyList<TranscriptBlock> blocks, string outcome)
    {
        var index = -1;
        for (var i = blocks.Count - 1; i >= 0 && index < 0; i--)
            if (blocks[i] is HandsLine { Outcome: null }) index = i;
        return index < 0 ? blocks : ReplaceAt(blocks, index, ((HandsLine)blocks[index]) with { Outcome = outcome });
    }

    private static string? FilePath(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("file_path", out var path) &&
        path.ValueKind == JsonValueKind.String
            ? path.GetString()
            : null;

    private static IReadOnlyList<TranscriptBlock> WithoutPending(IReadOnlyList<TranscriptBlock> blocks, Guid commandId) =>
        blocks.Where(block => block switch
        {
            PendingYouBlock you => you.CommandId != commandId,
            PendingReplyBlock reply => reply.CommandId != commandId,
            _ => true,
        }).ToList();

    private static IReadOnlyList<TranscriptBlock> WithoutLast(IReadOnlyList<TranscriptBlock> blocks, TranscriptBlock block)
    {
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i] != block) continue;
            var updated = blocks.ToList();
            updated.RemoveAt(i);
            return updated;
        }
        return blocks;
    }

    private static int IndexOf(IReadOnlyList<TranscriptBlock> blocks, Func<TranscriptBlock, bool> match)
    {
        for (var i = 0; i < blocks.Count; i++)
            if (match(blocks[i])) return i;
        return -1;
    }

    private static IReadOnlyList<TranscriptBlock> ReplaceAt(IReadOnlyList<TranscriptBlock> blocks, int index, TranscriptBlock block)
    {
        var updated = blocks.ToList();
        updated[index] = block;
        return updated;
    }

    private static int LatestCardIndex(IReadOnlyList<TranscriptBlock> blocks)
    {
        for (var i = blocks.Count - 1; i >= 0; i--)
            if (blocks[i] is ParticipantCard) return i;
        return -1;
    }

    private static bool IsTerminal(ConversationRunStatus status) => status is ConversationRunStatus.Completed or
        ConversationRunStatus.Rejected or ConversationRunStatus.Interrupted or ConversationRunStatus.Failed;

    private static IReadOnlyList<TranscriptBlock> Append(IReadOnlyList<TranscriptBlock> blocks, TranscriptBlock block) =>
        [.. blocks, block];
}
