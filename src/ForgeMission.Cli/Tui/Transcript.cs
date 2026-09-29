using ForgeMission.Conversations.Contracts;

namespace ForgeMission.Cli.Tui;

/// <summary>One visible piece of the chat transcript.</summary>
public abstract record TranscriptBlock;

/// <summary>What the person sent, shown as the right-aligned pill.</summary>
public sealed record YouBlock(string Text) : TranscriptBlock;

/// <summary>A participant's reply card. <paramref name="Text"/> is null while the reply is pending;
/// <paramref name="Mission"/> titles a final result that differs from the last step's text.</summary>
public sealed record ParticipantCard(string Title, string? Text, string Mission) : TranscriptBlock;

/// <summary>A muted one-line notice: a run that did not complete.</summary>
public sealed record NoticeLine(string Text) : TranscriptBlock;

/// <summary>An error from the run or the connection.</summary>
public sealed record ErrorLine(string Text) : TranscriptBlock;

// forge chat TUI (53.5): the one mapping from conversation events to transcript blocks, used for
// both the replay of a reopened conversation and a live turn. Pure: no terminal, no Client.
public static class Transcript
{
    /// <summary>Returns the transcript after <paramref name="item"/>; unchanged when the event has
    /// no visible block.</summary>
    public static IReadOnlyList<TranscriptBlock> Apply(IReadOnlyList<TranscriptBlock> blocks, ConversationEvent item) => item.Kind switch
    {
        ConversationEventKind.UserMessage => Append(blocks, new YouBlock(item.Text ?? "")),
        ConversationEventKind.ParticipantStarted => Append(blocks, StartedCard(item.Text ?? "")),
        ConversationEventKind.ParticipantMessage when item.Attempt is not null => FillLatestCard(blocks, item.Text ?? ""),
        ConversationEventKind.ParticipantMessage => AddFinalResult(blocks, item.Text ?? ""),
        ConversationEventKind.Error => AddError(blocks, item),
        ConversationEventKind.RunStatus when item.RunStatus is { } status && IsTerminal(status) => EndTurn(blocks, status),
        _ => blocks,
    };

    /// <summary>The expert whose reply is pending at the end of the transcript, or null.</summary>
    public static string? Replying(IReadOnlyList<TranscriptBlock> blocks) =>
        blocks.Count > 0 && blocks[^1] is ParticipantCard { Text: null } card ? card.Title : null;

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
        IReadOnlyList<TranscriptBlock> ended = blocks.Where(block => block is not ParticipantCard { Text: null }).ToList();
        return status == ConversationRunStatus.Completed
            ? ended
            : Append(ended, new NoticeLine($"(run {status.ToString().ToLowerInvariant()})"));
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
