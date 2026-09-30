using System.Reflection;
using ForgeMission.Conversations.Contracts;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (53.5): the one event→block mapping used for replay and live turns, including the
// duplicate final reply, and the switch between the TUI and the line mode. Read through reflection
// like the other CLI tests (the test project does not reference the forge executable's assembly);
// blocks are compared by their record text.
public sealed class ChatTranscriptTests
{
    private static readonly Assembly Forge = LoadForge();
    private static readonly MethodInfo ApplyMethod = Forge.GetType("ForgeMission.Cli.Tui.Transcript", throwOnError: true)!
        .GetMethod("Apply", BindingFlags.Static | BindingFlags.Public)!;
    private static readonly MethodInfo ReplyingMethod = Forge.GetType("ForgeMission.Cli.Tui.Transcript", throwOnError: true)!
        .GetMethod("Replying", BindingFlags.Static | BindingFlags.Public)!;
    private static readonly MethodInfo UsesTuiMethod = Forge.GetType("ForgeMission.Cli.ForgeChat", throwOnError: true)!
        .GetMethod("UsesTui", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public void A_turn_maps_to_a_You_pill_and_a_card_titled_with_the_expert()
    {
        var blocks = Map(User("my name is Ameer"), Started("Chat:Answerer"), Step("Nice to meet you, Ameer!"));

        Assert.Equal([
            "YouBlock { Text = my name is Ameer }",
            "ParticipantCard { Title = Answerer, Text = Nice to meet you, Ameer!, Mission = Chat }",
        ], blocks);
    }

    [Fact]
    public void A_step_title_without_a_mission_is_the_card_title()
    {
        Assert.Equal(["ParticipantCard { Title = Answerer, Text = , Mission = Answerer }"], Map(Started("Answerer")));
    }

    [Fact]
    public void A_final_result_equal_to_the_last_step_message_is_shown_once()
    {
        var blocks = Map(Started("Chat:Answerer"), Step("Hello"), Final("Hello"), Status(ConversationRunStatus.Completed));

        Assert.Equal(["ParticipantCard { Title = Answerer, Text = Hello, Mission = Chat }"], blocks);
    }

    [Fact]
    public void A_final_result_that_differs_gets_its_own_card_titled_with_the_mission()
    {
        var blocks = Map(Started("Chat:Answerer"), Step("Draft"), Final("Summary"));

        Assert.Equal([
            "ParticipantCard { Title = Answerer, Text = Draft, Mission = Chat }",
            "ParticipantCard { Title = Chat, Text = Summary, Mission = Chat }",
        ], blocks);
    }

    [Fact]
    public void A_step_message_fills_the_latest_card()
    {
        var blocks = Map(Started("Plan:Planner"), Step("plan"), Started("Plan:Writer"), Step("text"));

        Assert.Equal([
            "ParticipantCard { Title = Planner, Text = plan, Mission = Plan }",
            "ParticipantCard { Title = Writer, Text = text, Mission = Plan }",
        ], blocks);
    }

    [Fact]
    public void A_mission_error_repeating_the_step_error_is_shown_once()
    {
        var blocks = Map(Started("Chat:Answerer"), Error("rate limited", attempt: 1), Error("rate limited", attempt: null),
            Status(ConversationRunStatus.Failed));

        Assert.Equal([
            "ErrorLine { Text = error: rate limited }",
            "NoticeLine { Text = (run failed) }",
        ], blocks);
    }

    [Fact]
    public void A_card_without_text_is_kept_only_while_its_turn_runs()
    {
        Assert.Equal([
            "YouBlock { Text = hi }",
            "ParticipantCard { Title = Answerer, Text = , Mission = Chat }",
        ], Map(User("hi"), Started("Chat:Answerer")));

        Assert.Equal([
            "YouBlock { Text = hi }",
            "NoticeLine { Text = (run interrupted) }",
        ], Map(User("hi"), Started("Chat:Answerer"), Status(ConversationRunStatus.Interrupted)));
    }

    [Fact]
    public void A_completed_turn_keeps_its_answered_cards()
    {
        var blocks = Map(Started("Chat:Answerer"), Step("Hello"), Status(ConversationRunStatus.Completed));

        Assert.Equal(["ParticipantCard { Title = Answerer, Text = Hello, Mission = Chat }"], blocks);
    }

    [Theory]
    [InlineData(ConversationRunStatus.Failed, "(run failed)")]
    [InlineData(ConversationRunStatus.Interrupted, "(run interrupted)")]
    [InlineData(ConversationRunStatus.Rejected, "(run rejected)")]
    public void A_run_that_ends_unfinished_adds_a_notice(ConversationRunStatus status, string notice)
    {
        Assert.Equal([$"NoticeLine {{ Text = {notice} }}"], Map(Status(status)));
    }

    [Theory]
    [InlineData(ConversationRunStatus.Completed)]
    [InlineData(ConversationRunStatus.Running)]
    [InlineData(ConversationRunStatus.Queued)]
    public void A_completed_or_live_status_adds_nothing(ConversationRunStatus status)
    {
        Assert.Empty(Map(Status(status)));
    }

    [Fact]
    public void Events_without_a_block_add_nothing()
    {
        Assert.Empty(Map(Event(ConversationEventKind.ToolRequested, null, "tool"), Event(ConversationEventKind.Artifact, null, null)));
    }

    [Fact]
    public void The_expert_is_replying_while_its_card_is_pending_and_last()
    {
        Assert.Equal("Answerer", Replying(Started("Chat:Answerer")));
        Assert.Null(Replying(Started("Chat:Answerer"), Step("done")));
        Assert.Null(Replying(Started("Chat:Answerer"), Status(ConversationRunStatus.Interrupted)));
        Assert.Null(Replying());
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Only_a_terminal_on_both_ends_opens_the_TUI(bool inputRedirected, bool outputRedirected, bool expected)
    {
        Assert.Equal(expected, (bool)UsesTuiMethod.Invoke(null, [inputRedirected, outputRedirected])!);
    }

    // ── Sent messages (53.7): shown at once, replaced by Forge's own events ─────────────────

    private static readonly Guid Sent = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void A_sent_message_shows_a_pending_pill_and_reply_at_once()
    {
        Assert.Equal([
            $"PendingYouBlock {{ CommandId = {Sent}, Text = hi }}",
            $"PendingReplyBlock {{ CommandId = {Sent} }}",
        ], Strings(Submitted("hi")));
        Assert.Equal("", (string?)ReplyingMethod.Invoke(null, [Submitted("hi")]));
    }

    [Fact]
    public void Forges_echo_replaces_the_pending_pill_and_the_first_participant_the_pending_reply()
    {
        var echoed = ApplyAll(Submitted("hi"), User("hi") with { EventId = Sent });
        Assert.Equal(["YouBlock { Text = hi }", $"PendingReplyBlock {{ CommandId = {Sent} }}"], Strings(echoed));

        var started = ApplyAll(echoed, Started("Chat:Answerer"));
        Assert.Equal(["YouBlock { Text = hi }", "ParticipantCard { Title = Answerer, Text = , Mission = Chat }"], Strings(started));
        Assert.Equal("Answerer", (string?)ReplyingMethod.Invoke(null, [started]));
    }

    [Fact]
    public void Another_commands_echo_is_appended_not_merged()
    {
        var blocks = ApplyAll(Submitted("hi"), User("from elsewhere"));

        Assert.Equal([
            $"PendingYouBlock {{ CommandId = {Sent}, Text = hi }}",
            $"PendingReplyBlock {{ CommandId = {Sent} }}",
            "YouBlock { Text = from elsewhere }",
        ], Strings(blocks));
    }

    [Fact]
    public void A_failed_submit_leaves_only_an_error_line()
    {
        var failed = TranscriptMethod("SubmitFailed").Invoke(null, [Submitted("hi"), Sent, "connection refused"])!;

        Assert.Equal(["ErrorLine { Text = error: connection refused }"], Strings(failed));
    }

    [Fact]
    public void A_turn_that_fails_after_acceptance_keeps_the_message()
    {
        var failed = TranscriptMethod("TurnFailed").Invoke(null, [Submitted("hi"), Sent, "stream closed"])!;

        Assert.Equal(["YouBlock { Text = hi }", "ErrorLine { Text = error: stream closed }"], Strings(failed));
    }

    [Fact]
    public void A_turn_end_clears_pending_blocks()
    {
        var ended = ApplyAll(Submitted("hi"), User("hi") with { EventId = Sent }, Status(ConversationRunStatus.Failed));

        Assert.Equal(["YouBlock { Text = hi }", "NoticeLine { Text = (run failed) }"], Strings(ended));
    }

    [Fact]
    public void Replay_never_shows_a_pending_block()
    {
        var replay = Map(User("hi"), Started("Chat:Answerer"), Step("Hello"), Final("Hello"),
            Status(ConversationRunStatus.Completed), User("again"), Status(ConversationRunStatus.Interrupted));

        Assert.DoesNotContain(replay, block => block.StartsWith("Pending", StringComparison.Ordinal));
    }

    private static object Submitted(string text) =>
        TranscriptMethod("Submit").Invoke(null, [Blocks([]), Sent, text])!;

    private static object ApplyAll(object blocks, params ConversationEvent[] events)
    {
        foreach (var item in events)
            blocks = ApplyMethod.Invoke(null, [blocks, item])!;
        return blocks;
    }

    private static List<string> Strings(object blocks) =>
        ((System.Collections.IEnumerable)blocks).Cast<object>().Select(block => block.ToString()!).ToList();

    private static MethodInfo TranscriptMethod(string name) =>
        Forge.GetType("ForgeMission.Cli.Tui.Transcript", throwOnError: true)!.GetMethod(name, BindingFlags.Static | BindingFlags.Public)!;

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static List<string> Map(params ConversationEvent[] events) =>
        ((System.Collections.IEnumerable)Blocks(events)).Cast<object>().Select(block => block.ToString()!).ToList();

    private static string? Replying(params ConversationEvent[] events) =>
        (string?)ReplyingMethod.Invoke(null, [Blocks(events)]);

    private static object Blocks(ConversationEvent[] events)
    {
        var blockType = Forge.GetType("ForgeMission.Cli.Tui.TranscriptBlock", throwOnError: true)!;
        object blocks = Array.CreateInstance(blockType, 0);
        foreach (var item in events)
            blocks = ApplyMethod.Invoke(null, [blocks, item])!;
        return blocks;
    }

    private static ConversationEvent User(string text) => Event(ConversationEventKind.UserMessage, null, text);
    private static ConversationEvent Started(string step) => Event(ConversationEventKind.ParticipantStarted, 1, step);
    private static ConversationEvent Step(string text) => Event(ConversationEventKind.ParticipantMessage, 1, text);
    private static ConversationEvent Final(string text) => Event(ConversationEventKind.ParticipantMessage, null, text);

    private static ConversationEvent Error(string reason, int? attempt) =>
        Event(ConversationEventKind.Error, attempt, null) with { Reason = reason };

    private static ConversationEvent Status(ConversationRunStatus status) =>
        Event(ConversationEventKind.RunStatus, null, null) with { RunStatus = status };

    private static ConversationEvent Event(ConversationEventKind kind, int? attempt, string? text) => new(
        Guid.NewGuid(), 1, Guid.Empty, Guid.Empty, 1, kind, ConversationParticipant.Forge, attempt, text,
        null, null, null, null, null, null, DateTimeOffset.UtcNow);

    private static Assembly LoadForge()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ForgeMission.Cli", "bin", "Debug", "net10.0", "forge.dll");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate built forge.dll for CLI reflection tests.");
    }
}
