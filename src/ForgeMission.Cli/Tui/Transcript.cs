using ForgeMission.Conversations.Contracts;

namespace ForgeMission.Cli.Tui;

/// <summary>One visible piece of the chat transcript.</summary>
public abstract record TranscriptBlock;

/// <summary>What the person sent, shown as the right-aligned pill.</summary>
public sealed record YouBlock(string Text) : TranscriptBlock;

/// <summary>A participant's reply card. <paramref name="Text"/> is null while the reply is pending;
/// <paramref name="Mission"/> titles a final result that differs from the last step's text.</summary>
public sealed record ParticipantCard(string Title, string? Text, string Mission) : TranscriptBlock;

/// <summary>A message shown the moment it is sent, before Forge echoes it back as a
/// <c>UserMessage</c> whose event id is <paramref name="CommandId"/>.</summary>
public sealed record PendingYouBlock(Guid CommandId, string Text) : TranscriptBlock;

/// <summary>The reply card shown the moment a message is sent, before the first participant
/// starts; it has no title until then.</summary>
public sealed record PendingReplyBlock(Guid CommandId) : TranscriptBlock;

/// <summary>A muted one-line notice: a run that did not complete.</summary>
public sealed record NoticeLine(string Text) : TranscriptBlock;

/// <summary>An error from the run or the connection.</summary>
public sealed record ErrorLine(string Text) : TranscriptBlock;

// forge chat TUI (53.5, 53.7): the one mapping from conversation events to transcript blocks, used
// for both the replay of a reopened conversation and a live turn. A sent message is shown at once
// as pending blocks keyed by its command id; Forge's own events then take their place. Replay only
// applies Forge's events, so it never shows a pending block. Pure: no terminal, no Client.
public static class Transcript
{
    /// <summary>Returns the transcript after <paramref name="item"/>; unchanged when the event has
    /// no visible block.</summary>
    public static IReadOnlyList<TranscriptBlock> Apply(IReadOnlyList<TranscriptBlock> blocks, ConversationEvent item) => item.Kind switch
    {
        ConversationEventKind.UserMessage => AddUserMessage(blocks, item.EventId, item.Text ?? ""),
        ConversationEventKind.ParticipantStarted => AddStartedCard(blocks, StartedCard(item.Text ?? "")),
        ConversationEventKind.ParticipantMessage when item.Attempt is not null => FillLatestCard(blocks, item.Text ?? ""),
        ConversationEventKind.ParticipantMessage => AddFinalResult(blocks, item.Text ?? ""),
        ConversationEventKind.Error => AddError(blocks, item),
        ConversationEventKind.RunStatus when item.RunStatus is { } status && IsTerminal(status) => EndTurn(blocks, status),
        _ => blocks,
    };

    /// <summary>A message was just sent with <paramref name="commandId"/>: show it and a pending
    /// reply now, without waiting for Forge.</summary>
    public static IReadOnlyList<TranscriptBlock> Submit(IReadOnlyList<TranscriptBlock> blocks, Guid commandId, string text) =>
        [.. blocks, new PendingYouBlock(commandId, text), new PendingReplyBlock(commandId)];

    /// <summary>Forge did not accept the message: its pending blocks become an error line.</summary>
    public static IReadOnlyList<TranscriptBlock> SubmitFailed(IReadOnlyList<TranscriptBlock> blocks, Guid commandId, string message) =>
        Append(WithoutPending(blocks, commandId), new ErrorLine($"error: {message}"));

    /// <summary>The turn failed after Forge accepted the message: the message stays (as sent), the
    /// pending reply goes, and an error line follows.</summary>
    public static IReadOnlyList<TranscriptBlock> TurnFailed(IReadOnlyList<TranscriptBlock> blocks, Guid commandId, string message)
    {
        var kept = blocks
            .Where(block => block is not PendingReplyBlock reply || reply.CommandId != commandId)
            .Select(block => block is PendingYouBlock you && you.CommandId == commandId ? new YouBlock(you.Text) : block)
            .ToList();
        return Append(kept, new ErrorLine($"error: {message}"));
    }

    /// <summary>Who is replying at the end of the transcript: the expert, <c>""</c> while no
    /// participant has started yet, or null when nothing is pending.</summary>
    public static string? Replying(IReadOnlyList<TranscriptBlock> blocks) => blocks.Count == 0 ? null : blocks[^1] switch
    {
        ParticipantCard { Text: null } card => card.Title,
        PendingReplyBlock => "",
        _ => null,
    };

    /// <summary>Forge's echo of a sent message takes the place of its pending pill.</summary>
    private static IReadOnlyList<TranscriptBlock> AddUserMessage(IReadOnlyList<TranscriptBlock> blocks, Guid eventId, string text)
    {
        var index = IndexOf(blocks, block => block is PendingYouBlock you && you.CommandId == eventId);
        return index < 0 ? Append(blocks, new YouBlock(text)) : ReplaceAt(blocks, index, new YouBlock(text));
    }

    /// <summary>The first participant to start takes the place of the pending reply (one turn
    /// runs at a time, so there is at most one).</summary>
    private static IReadOnlyList<TranscriptBlock> AddStartedCard(IReadOnlyList<TranscriptBlock> blocks, ParticipantCard card)
    {
        var index = IndexOf(blocks, block => block is PendingReplyBlock);
        return index < 0 ? Append(blocks, card) : ReplaceAt(blocks, index, card);
    }

    /// <summary>The runner titles a step <c>Mission:Expert</c>; the card shows the expert.</summary>
    private static ParticipantCard StartedCard(string step)
    {
        var colon = step.LastIndexOf(':');
        return colon < 0
            ? new ParticipantCard(step, null, step)
            : new ParticipantCard(step[(colon + 1)..], null, step[..colon]);
    }

    private static IReadOnlyList<TranscriptBlock> FillLatestCard(IReadOnlyList<TranscriptBlock> blocks, string text)
    {
        var index = LatestCardIndex(blocks);
        if (index < 0) return Append(blocks, new ParticipantCard("Forge", text, "Forge"));

        var updated = blocks.ToList();
        updated[index] = ((ParticipantCard)blocks[index]) with { Text = text };
        return updated;
    }

    /// <summary>The runner sends the mission's final result after the last step's message. It is
    /// shown, as its own card titled with the mission, only when its text differs.</summary>
    private static IReadOnlyList<TranscriptBlock> AddFinalResult(IReadOnlyList<TranscriptBlock> blocks, string text)
    {
        var index = LatestCardIndex(blocks);
        if (index < 0) return Append(blocks, new ParticipantCard("Forge", text, "Forge"));

        var last = (ParticipantCard)blocks[index];
        if (last.Text == text) return blocks;
        return Append(blocks, new ParticipantCard(last.Mission, text, last.Mission));
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
    /// shown only while the turn runs), and a run that did not complete adds a notice.</summary>
    private static IReadOnlyList<TranscriptBlock> EndTurn(IReadOnlyList<TranscriptBlock> blocks, ConversationRunStatus status)
    {
        IReadOnlyList<TranscriptBlock> ended = blocks
            .Where(block => block is not (ParticipantCard { Text: null } or PendingReplyBlock))
            .Select(block => block is PendingYouBlock you ? new YouBlock(you.Text) : block)
            .ToList();
        return status == ConversationRunStatus.Completed
            ? ended
            : Append(ended, new NoticeLine($"(run {status.ToString().ToLowerInvariant()})"));
    }

    private static IReadOnlyList<TranscriptBlock> WithoutPending(IReadOnlyList<TranscriptBlock> blocks, Guid commandId) =>
        blocks.Where(block => block switch
        {
            PendingYouBlock you => you.CommandId != commandId,
            PendingReplyBlock reply => reply.CommandId != commandId,
            _ => true,
        }).ToList();

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
