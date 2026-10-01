using System.Reflection;
using System.Text.Json;
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

    // ── Hands activity (Phase 55, H8) ───────────────────────────────────────────────────────

    [Fact]
    public void A_tool_use_shows_one_line_that_its_outcome_completes()
    {
        Assert.Equal(["HandsLine { Label = Read notes.txt, Outcome =  }"], Map(HandsRequested("Read", "notes.txt")));
        Assert.Equal(["HandsLine { Label = Read notes.txt, Outcome = succeeded }"],
            Map(HandsRequested("Read", "notes.txt"), HandsResult(MissionToolOutcome.Succeeded)));
    }

    [Fact]
    public void A_tool_use_goes_above_the_reply_still_waiting_and_the_reply_stays_last()
    {
        var blocks = Map(User("read it"), Started("ChatHands:Answerer"), HandsRequested("Read", "notes.txt"),
            HandsResult(MissionToolOutcome.Succeeded), Step("The codeword is kiwi."), Status(ConversationRunStatus.Completed));

        Assert.Equal([
            "YouBlock { Text = read it }",
            "HandsLine { Label = Read notes.txt, Outcome = succeeded }",
            "ParticipantCard { Title = Answerer, Text = The codeword is kiwi., Mission = ChatHands }",
        ], blocks);
        Assert.Equal("Answerer", Replying(Started("ChatHands:Answerer"), HandsRequested("Read", "notes.txt")));
    }

    [Theory]
    [InlineData(MissionToolOutcome.Failed, "failed")]
    [InlineData(MissionToolOutcome.DeniedByPolicy, "denied by policy")]
    [InlineData(MissionToolOutcome.DeniedOutOfProfile, "denied: outside the allowed tools")]
    [InlineData(MissionToolOutcome.Cancelled, "cancelled")]
    public void A_tool_use_that_did_not_succeed_shows_how_it_ended(MissionToolOutcome outcome, string shown)
    {
        Assert.Equal([$"HandsLine {{ Label = Write out.txt, Outcome = {shown} }}"],
            Map(HandsRequested("Write", "out.txt"), HandsResult(outcome)));
    }

    [Theory]
    [InlineData(ConversationEventKind.MissionHandsCancelled, "cancelled")]
    [InlineData(ConversationEventKind.MissionHandsInterrupted, "interrupted")]
    public void A_cancelled_or_interrupted_attempt_ends_the_line(ConversationEventKind kind, string shown)
    {
        Assert.Equal([$"HandsLine {{ Label = Edit a.md, Outcome = {shown} }}"],
            Map(HandsRequested("Edit", "a.md"), Event(kind, null, null)));
    }

    [Fact]
    public void A_tool_without_a_file_path_shows_its_name()
    {
        var noPath = HandsRequested("Read", null);

        Assert.Equal(["HandsLine { Label = Read, Outcome =  }"], Map(noPath));
    }

    [Fact]
    public void The_line_text_reads_running_then_finished()
    {
        var text = TranscriptMethod("HandsText");
        var lineType = Forge.GetType("ForgeMission.Cli.Tui.HandsLine", throwOnError: true)!;

        Assert.Equal("Read notes.txt …", text.Invoke(null, [Activator.CreateInstance(lineType, "Read notes.txt", null)]));
        Assert.Equal("Read notes.txt → succeeded", text.Invoke(null, [Activator.CreateInstance(lineType, "Read notes.txt", "succeeded")]));
    }

    [Fact]
    public void An_outcome_without_a_running_tool_use_changes_nothing()
    {
        Assert.Equal(["YouBlock { Text = hi }"], Map(User("hi"), HandsResult(MissionToolOutcome.Succeeded)));
    }

    private static ConversationEvent HandsRequested(string tool, string? path)
    {
        var arguments = JsonSerializer.SerializeToElement(path is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["file_path"] = path });
        var request = new MissionToolRequest(Guid.NewGuid(), Guid.Empty, Guid.Empty, "", "", "call-1", tool, arguments);
        return Event(ConversationEventKind.MissionHandsRequested, null, null) with { MissionHandsRequest = request };
    }

    private static ConversationEvent HandsResult(MissionToolOutcome outcome) =>
        Event(ConversationEventKind.MissionHandsResult, null, null) with { MissionHandsOutcome = outcome };

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

    // ── Live reply deltas (53.8) ────────────────────────────────────────────────

    [Fact]
    public void Deltas_grow_the_started_card_and_the_step_message_replaces_it()
    {
        var growing = Map(Started("Chat:Answerer"), Delta("Hel"), Delta("lo"));
        Assert.Equal(["ParticipantCard { Title = Answerer, Text = Hello, Mission = Chat }"], growing);

        var final = Map(Started("Chat:Answerer"), Delta("Hel"), Delta("lo"), Step("Hello!"));
        Assert.Equal(["ParticipantCard { Title = Answerer, Text = Hello!, Mission = Chat }"], final);
    }

    [Fact]
    public void A_delta_with_no_card_changes_nothing()
    {
        Assert.Equal(["YouBlock { Text = hi }"], Map(User("hi"), Delta("orphan")));
    }

    [Fact]
    public async Task Following_shows_a_delta_only_after_a_step_starts_on_the_same_connection_and_never_moves_the_cursor()
    {
        var attempt = Guid.NewGuid();
        var connections = new Queue<ConversationEvent[]>([
            // Joined mid-reply: the fragment before this connection saw a step start is hidden.
            [Delta("fragment", 5), Seq(Started("Chat:Answerer"), 6), Delta("Hel", 6), Delta("lo", 6)],
            // Reconnected mid-step: hidden until the final message.
            [Delta("mid", 6), Seq(Step("Hello!"), 7), Seq(Status(ConversationRunStatus.Completed), 8) with { RunId = attempt }],
        ]);
        var (service, requests) = FakeConversations(connections);
        var shown = new List<ConversationEvent>();

        var cursor = await (Task<long>)FollowTurnAsyncMethod.Invoke(null,
            [service, Guid.NewGuid(), 5L, attempt, true, (Action<ConversationEvent>)shown.Add, CancellationToken.None])!;

        Assert.Equal(8, cursor);
        Assert.Equal([(5L, true), (6L, true)], requests);
        Assert.Equal(["participantStarted:Chat:Answerer", "participantDelta:Hel", "participantDelta:lo", "participantMessage:Hello!", "runStatus:"],
            shown.Select(e => $"{char.ToLowerInvariant(e.Kind.ToString()[0])}{e.Kind.ToString()[1..]}:{e.Text}"));
    }

    // ── One live stream (53.9 L1) ───────────────────────────────────────────────

    [Fact]
    public void Another_windows_message_shows_as_replying_until_its_first_participant_starts()
    {
        var other = User("from elsewhere");
        var waiting = ApplyLive(Blocks([]), other);
        Assert.Equal(["YouBlock { Text = from elsewhere }", $"PendingReplyBlock {{ CommandId = {other.EventId} }}"], Strings(waiting));
        Assert.Equal("", (string?)ReplyingMethod.Invoke(null, [waiting]));

        var started = ApplyLive(waiting, Started("Chat:Answerer"));
        Assert.Equal(["YouBlock { Text = from elsewhere }", "ParticipantCard { Title = Answerer, Text = , Mission = Chat }"], Strings(started));

        var ended = ApplyLive(waiting, Status(ConversationRunStatus.Failed));
        Assert.Equal(["YouBlock { Text = from elsewhere }", "NoticeLine { Text = (run failed) }"], Strings(ended));
    }

    [Fact]
    public void This_windows_echo_keeps_its_one_pending_reply()
    {
        var echoed = ApplyLive(Submitted("hi"), User("hi") with { EventId = Sent });
        Assert.Equal(["YouBlock { Text = hi }", $"PendingReplyBlock {{ CommandId = {Sent} }}"], Strings(echoed));
    }

    [Fact]
    public void Hands_act_only_on_this_windows_own_turn_attempt()
    {
        var own = Guid.NewGuid();
        var request = HandsRequested("Read", "notes.txt");

        Assert.True(BelongsToOwnTurn(request with { RunId = own }, own));
        Assert.False(BelongsToOwnTurn(request with { RunId = Guid.NewGuid() }, own));
        Assert.False(BelongsToOwnTurn(request with { RunId = own }, null));
        Assert.False(BelongsToOwnTurn(request with { RunId = null }, own));
    }

    [Fact]
    public void A_message_from_any_window_starts_a_turn_and_a_terminal_status_ends_it()
    {
        Assert.True(TurnRunning(false, User("hi")));
        Assert.True(TurnRunning(true, Started("Chat:Answerer")));
        Assert.True(TurnRunning(true, Status(ConversationRunStatus.Running)));
        Assert.False(TurnRunning(true, Status(ConversationRunStatus.Completed)));
        Assert.False(TurnRunning(false, Delta("x")));
    }

    [Fact]
    public void A_stop_pressed_during_a_submit_survives_only_when_the_submit_started_a_turn()
    {
        Assert.True(KeepsStop(true, true));
        Assert.False(KeepsStop(true, false));
        Assert.False(KeepsStop(false, true));
        Assert.False(KeepsStop(false, false));
    }

    [Fact]
    public async Task A_stream_that_fails_in_transport_is_reported_once_and_reopened_from_the_cursor()
    {
        var attempt = Guid.NewGuid();
        var (service, requests) = FakeConversations(new Queue<ConversationEvent[]>([
            [Seq(Started("Chat:Answerer"), 6)],
            StreamingConversations.Broken,
            [Seq(Step("Hello!"), 7), Seq(Status(ConversationRunStatus.Completed), 8) with { RunId = attempt }],
        ]));
        var lost = new List<Exception>();
        var shown = new List<ConversationEvent>();

        var cursor = await (Task<long>)StreamAsyncMethod.Invoke(null, [service, Guid.NewGuid(), 5L, true,
            (Action<ConversationEvent>)shown.Add, (Func<ConversationEvent, bool>)(e => e.RunId == attempt),
            (Action<Exception>)lost.Add, CancellationToken.None])!;

        Assert.Equal(8, cursor);
        Assert.Equal([(5L, true), (6L, true), (6L, true)], requests);
        Assert.IsType<IOException>(Assert.Single(lost));
        Assert.Equal(3, shown.Count);
    }

    [Fact]
    public async Task Without_a_lost_handler_a_transport_failure_is_thrown()
    {
        var (service, _) = FakeConversations(new Queue<ConversationEvent[]>([StreamingConversations.Broken]));

        await Assert.ThrowsAsync<IOException>(() => (Task<long>)FollowTurnAsyncMethod.Invoke(null,
            [service, Guid.NewGuid(), 5L, Guid.NewGuid(), true, (Action<ConversationEvent>)(_ => { }), CancellationToken.None])!);
    }

    private static readonly MethodInfo StreamAsyncMethod = Forge.GetType("ForgeMission.Cli.ForgeChat", throwOnError: true)!
        .GetMethod("StreamAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly Type ChatTuiType = Forge.GetType("ForgeMission.Cli.Tui.ChatTui", throwOnError: true)!;

    private static object ApplyLive(object blocks, ConversationEvent item) =>
        TranscriptMethod("ApplyLive").Invoke(null, [blocks, item])!;

    private static bool BelongsToOwnTurn(ConversationEvent item, Guid? own) =>
        (bool)ChatTuiType.GetMethod("BelongsToOwnTurn", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [item, own])!;

    private static bool TurnRunning(bool running, ConversationEvent item) =>
        (bool)ChatTuiType.GetMethod("TurnRunning", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [running, item])!;

    private static bool KeepsStop(bool stopRequested, bool hasOwnTurn) =>
        (bool)ChatTuiType.GetMethod("KeepsStop", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [stopRequested, hasOwnTurn])!;

    private static readonly MethodInfo FollowTurnAsyncMethod = Forge.GetType("ForgeMission.Cli.ForgeChat", throwOnError: true)!
        .GetMethod("FollowTurnAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static (object Service, List<(long After, bool IncludeDeltas)> Requests) FakeConversations(Queue<ConversationEvent[]> connections)
    {
        var serviceType = FollowTurnAsyncMethod.GetParameters()[0].ParameterType;
        var proxy = (StreamingConversations)DispatchProxy.Create(serviceType, typeof(StreamingConversations));
        proxy.Connections = connections;
        return (proxy, proxy.Requests);
    }

    /// <summary>Answers only <c>StreamEventsAsync</c>: each call is one connection, serving the next
    /// queued events and then closing.</summary>
    public class StreamingConversations : DispatchProxy
    {
        public Queue<ConversationEvent[]> Connections { get; set; } = new();
        public List<(long After, bool IncludeDeltas)> Requests { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "StreamEventsAsync") throw new NotSupportedException(targetMethod?.Name);
            Requests.Add(((long)args![1]!, (bool)args[2]!));
            return Serve(Connections.Dequeue());
        }

        /// <summary>A connection that fails in transport (an SSE body cut mid-read).</summary>
        public static readonly ConversationEvent[] Broken = [];

        private static async IAsyncEnumerable<ConversationEvent> Serve(ConversationEvent[] events)
        {
            await Task.Yield();
            if (ReferenceEquals(events, Broken)) throw new IOException("connection reset");
            foreach (var item in events)
            {
                await Task.Yield();
                yield return item;
            }
        }
    }

    private static ConversationEvent Delta(string text, long sequence = 1) =>
        Event(ConversationEventKind.ParticipantDelta, 1, text) with { Sequence = sequence };

    private static ConversationEvent Seq(ConversationEvent item, long sequence) => item with { Sequence = sequence };

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
