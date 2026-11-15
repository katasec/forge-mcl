using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ForgeMission.Conversations.Contracts;
using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// What forge run --verbose prints on stderr for each event of the turn (Phase 76 M0 pins, moved
// from the removed chat line mode in Phase 79). ForgeDurableRunTests proves --verbose routes the turn
// through this printer; these pins fix each event's line.
public sealed class RunTranscriptTests
{
    private static readonly MethodInfo Print = Assembly.LoadFrom(ForgeCliHarness.ForgeDllPath)
        .GetType("ForgeMission.Cli.Commands.RunTranscript", throwOnError: true)!
        .GetMethod("Print", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly DateTimeOffset Sent = new(2026, 11, 3, 14, 5, 0, TimeSpan.Zero);
    private static readonly string Time = Sent.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    [Fact]
    public void A_user_message_prints_the_sender_time_and_text()
    {
        Assert.Equal($"you · {Time}> hello\n", Render(Event(ConversationEventKind.UserMessage, "hello")));
    }

    [Fact]
    public void A_participant_start_prints_its_name_and_time_in_brackets()
    {
        Assert.Equal($"[Researcher · {Time}]\n", Render(Event(ConversationEventKind.ParticipantStarted, "Researcher")));
    }

    [Fact]
    public void A_reply_prints_its_text_and_a_blank_line()
    {
        Assert.Equal("the answer\n\n", Render(Event(ConversationEventKind.ParticipantMessage, "the answer", attempt: 1)));
    }

    // Only a final result (no attempt) with the same text is dropped; a step that repeats it is not.
    [Fact]
    public void A_final_result_that_repeats_the_last_reply_prints_once()
    {
        var printed = Render(
            Event(ConversationEventKind.ParticipantMessage, "the answer", attempt: 1),
            Event(ConversationEventKind.ParticipantMessage, "the answer"),
            Event(ConversationEventKind.ParticipantMessage, "a new result"),
            Event(ConversationEventKind.ParticipantMessage, "a new result", attempt: 2));

        Assert.Equal("the answer\n\na new result\n\na new result\n\n", printed);
    }

    [Fact]
    public void An_error_prints_its_reason_or_else_its_text()
    {
        var printed = Render(
            Event(ConversationEventKind.Error, "error text", reason: "error reason"),
            Event(ConversationEventKind.Error, "text only"));

        Assert.Equal("error: error reason\nerror: text only\n", printed);
    }

    // One line per tool use: the request starts it and its outcome completes it.
    [Fact]
    public void A_tool_use_prints_one_line_its_outcome_completes()
    {
        var printed = Render(
            Event(ConversationEventKind.MissionHandsRequested, request: Tool("write_file", "{\"file_path\":\"notes.md\"}")),
            Event(ConversationEventKind.MissionHandsResult, outcome: MissionToolOutcome.Succeeded),
            Event(ConversationEventKind.MissionHandsRequested, request: Tool("list_files", "{}")),
            Event(ConversationEventKind.MissionHandsResult, outcome: MissionToolOutcome.DeniedByPolicy),
            Event(ConversationEventKind.MissionHandsRequested),
            Event(ConversationEventKind.MissionHandsCancelled),
            Event(ConversationEventKind.MissionHandsRequested, request: Tool("read_file", "{\"file_path\":\"a.txt\"}")),
            Event(ConversationEventKind.MissionHandsInterrupted));

        Assert.Equal("write_file notes.md → succeeded\nlist_files → denied by policy\nTool → cancelled\n" +
            "read_file a.txt → interrupted\n", printed);
    }

    [Fact]
    public void Only_a_run_that_ended_without_completing_prints_its_status()
    {
        var printed = Render(
            Event(ConversationEventKind.RunStatus, status: ConversationRunStatus.Running),
            Event(ConversationEventKind.RunStatus, status: ConversationRunStatus.Completed),
            Event(ConversationEventKind.RunStatus, status: ConversationRunStatus.Failed),
            Event(ConversationEventKind.RunStatus, status: ConversationRunStatus.Rejected),
            Event(ConversationEventKind.RunStatus, status: ConversationRunStatus.Interrupted));

        Assert.Equal("(run failed)\n(run rejected)\n(run interrupted)\n", printed);
    }

    /// <summary>Prints <paramref name="events"/> in order, as the verbose observer does, and
    /// returns what was written.</summary>
    private static string Render(params ConversationEvent[] events)
    {
        using var output = new StringWriter { NewLine = "\n" };
        string? lastReply = null;
        foreach (var item in events)
            lastReply = (string?)Print.Invoke(null, [item, lastReply, output]);
        return output.ToString();
    }

    private static ConversationEvent Event(ConversationEventKind kind, string? text = null, string? reason = null, int? attempt = null,
        ConversationRunStatus? status = null, MissionToolRequest? request = null, MissionToolOutcome? outcome = null) =>
        new(Guid.NewGuid(), 1, Guid.Empty, null, 0, kind, ConversationParticipant.User, attempt, text, reason, null, null, null,
            null, status, Sent, request, outcome);

    private static MissionToolRequest Tool(string name, string arguments) => new(Guid.NewGuid(), Guid.Empty, Guid.Empty,
        "mission", "agent", "call-1", name, JsonDocument.Parse(arguments).RootElement.Clone());
}
