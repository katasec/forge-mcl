using System.Text.Json;
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

/// <summary>One tool use through hands (Phase 55, H8), e.g. <c>Read notes.txt</c>;
/// <paramref name="Outcome"/> is null while it runs.</summary>
public sealed record HandsLine(string Label, string? Outcome) : TranscriptBlock;

// forge chat TUI (53.5, 53.7, 53.8): the one mapping from conversation events to transcript blocks,
// used for both the replay of a reopened conversation and a live turn (including live reply deltas). A sent message is shown at once
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
        ConversationEventKind.ParticipantDelta => AppendToLatestCard(blocks, item.Text ?? ""),
        ConversationEventKind.ParticipantMessage when item.Attempt is not null => FillLatestCard(blocks, item.Text ?? ""),
        ConversationEventKind.ParticipantMessage => AddFinalResult(blocks, item.Text ?? ""),
        ConversationEventKind.Error => AddError(blocks, item),
        ConversationEventKind.RunStatus when item.RunStatus is { } status && IsTerminal(status) => EndTurn(blocks, status),
        ConversationEventKind.MissionHandsRequested => AddHandsLine(blocks, new HandsLine(HandsLabel(item), null)),
        ConversationEventKind.MissionHandsResult or ConversationEventKind.MissionHandsCancelled or
            ConversationEventKind.MissionHandsInterrupted => EndHandsLine(blocks, HandsOutcome(item)),
        _ => blocks,
    };

    /// <summary>Applies a live event (53.9 L1): as <see cref="Apply"/>, and a message sent from
    /// another window (an echo with no pending pill of this window) also gets a pending reply, so it
    /// shows as replying until its first participant starts.</summary>
    public static IReadOnlyList<TranscriptBlock> ApplyLive(IReadOnlyList<TranscriptBlock> blocks, ConversationEvent item)
    {
        var applied = Apply(blocks, item);
        if (item.Kind != ConversationEventKind.UserMessage) return applied;
        var sentHere = IndexOf(blocks, block => block is PendingYouBlock you && you.CommandId == item.EventId) >= 0;
        return sentHere ? applied : Append(applied, new PendingReplyBlock(item.EventId));
    }

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

    /// <summary>A message was just sent with <paramref name="commandId"/>: show it and a pending
    /// reply now, without waiting for Forge.</summary>
    public static IReadOnlyList<TranscriptBlock> Submit(IReadOnlyList<TranscriptBlock> blocks, Guid commandId, string text) =>
        [.. blocks, new PendingYouBlock(commandId, text), new PendingReplyBlock(commandId)];

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

    /// <summary>A live reply delta (53.8) grows the latest card; the step's own message then replaces
    /// the card's text with the final reply.</summary>
    private static IReadOnlyList<TranscriptBlock> AppendToLatestCard(IReadOnlyList<TranscriptBlock> blocks, string text)
    {
        var index = LatestCardIndex(blocks);
        if (index < 0) return blocks;

        var card = (ParticipantCard)blocks[index];
        return ReplaceAt(blocks, index, card with { Text = (card.Text ?? "") + text });
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
