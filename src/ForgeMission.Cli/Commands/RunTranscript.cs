using System.Globalization;
using System.Text.Json;
using ForgeMission.Conversations.Contracts;

namespace ForgeMission.Cli.Commands;

// forge run --verbose: one line-mode rendering of each conversation event, written to stderr.
internal static class RunTranscript
{
    /// <summary>Prints one event to <paramref name="output"/>; returns the last reply text printed, so a
    /// final result that repeats it (<see cref="RepeatsLastReply"/>) is printed once.</summary>
    internal static string? Print(ConversationEvent item, string? lastReply, TextWriter output)
    {
        if (RepeatsLastReply(lastReply, item)) return lastReply;
        switch (item.Kind)
        {
            case ConversationEventKind.UserMessage:
                output.WriteLine($"you · {TimeOf(item.OccurredAtUtc)}> {item.Text}");
                break;
            case ConversationEventKind.ParticipantStarted:
                output.WriteLine($"[{item.Text} · {TimeOf(item.OccurredAtUtc)}]");
                break;
            case ConversationEventKind.ParticipantMessage:
                output.WriteLine(item.Text);
                output.WriteLine();
                return item.Text ?? "";
            case ConversationEventKind.Error:
                output.WriteLine($"error: {item.Reason ?? item.Text}");
                break;
            // One line per tool use; the outcome completes the line its request started.
            case ConversationEventKind.MissionHandsRequested:
                output.Write(HandsLabel(item));
                break;
            case ConversationEventKind.MissionHandsResult or ConversationEventKind.MissionHandsCancelled or
                ConversationEventKind.MissionHandsInterrupted:
                output.WriteLine($" → {HandsOutcome(item)}");
                break;
            case ConversationEventKind.RunStatus when item.RunStatus is { } status && EndedWithoutCompleting(status):
                output.WriteLine($"(run {status.ToString().ToLowerInvariant()})");
                break;
        }
        return lastReply;
    }

    /// <summary>The runner sends the mission's final result (a message with no attempt) after the
    /// last step's message; it repeats the reply when its text is the same.</summary>
    private static bool RepeatsLastReply(string? lastReplyText, ConversationEvent item) =>
        item is { Kind: ConversationEventKind.ParticipantMessage, Attempt: null } && (item.Text ?? "") == lastReplyText;

    /// <summary>When a message was sent: local time in the system's short time format.</summary>
    private static string TimeOf(DateTimeOffset sent) => sent.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    /// <summary>The tool and the file it names: <c>Read notes.txt</c>, or the tool alone.</summary>
    private static string HandsLabel(ConversationEvent item)
    {
        if (item.MissionHandsRequest is not { } request) return "Tool";
        return FilePath(request.Arguments) is { } path ? $"{request.ToolName} {path}" : request.ToolName;
    }

    /// <summary>How a tool use ended, in words.</summary>
    private static string HandsOutcome(ConversationEvent item) => item.Kind switch
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

    private static string? FilePath(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("file_path", out var path) &&
        path.ValueKind == JsonValueKind.String
            ? path.GetString()
            : null;

    private static bool EndedWithoutCompleting(ConversationRunStatus status) => status is
        ConversationRunStatus.Rejected or ConversationRunStatus.Interrupted or ConversationRunStatus.Failed;
}
