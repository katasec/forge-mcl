using System.Globalization;
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
            $"YouBlock {{ Text = my name is Ameer, Sent = {At} }}",
            $"ParticipantCard {{ Title = Answerer, Text = Nice to meet you, Ameer!, Mission = Chat, Sent = {At}, Streaming = False }}",
        ], blocks);
    }

    [Fact]
    public void A_step_title_without_a_mission_is_the_card_title()
    {
        Assert.Equal([$"ParticipantCard {{ Title = Answerer, Text = , Mission = Answerer, Sent = {At}, Streaming = False }}"], Map(Started("Answerer")));
    }

    [Fact]
    public void A_final_result_equal_to_the_last_step_message_is_shown_once()
    {
        var blocks = Map(Started("Chat:Answerer"), Step("Hello"), Final("Hello"), Status(ConversationRunStatus.Completed));

        Assert.Equal([$"ParticipantCard {{ Title = Answerer, Text = Hello, Mission = Chat, Sent = {At}, Streaming = False }}"], blocks);
    }

    [Fact]
    public void A_final_result_that_differs_gets_its_own_card_titled_with_the_mission()
    {
        var blocks = Map(Started("Chat:Answerer"), Step("Draft"), Final("Summary"));

        Assert.Equal([
            $"ParticipantCard {{ Title = Answerer, Text = Draft, Mission = Chat, Sent = {At}, Streaming = False }}",
            $"ParticipantCard {{ Title = Chat, Text = Summary, Mission = Chat, Sent = {At}, Streaming = False }}",
        ], blocks);
    }

    [Fact]
    public void A_step_message_fills_the_latest_card()
    {
        var blocks = Map(Started("Plan:Planner"), Step("plan"), Started("Plan:Writer"), Step("text"));

        Assert.Equal([
            $"ParticipantCard {{ Title = Planner, Text = plan, Mission = Plan, Sent = {At}, Streaming = False }}",
            $"ParticipantCard {{ Title = Writer, Text = text, Mission = Plan, Sent = {At}, Streaming = False }}",
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
            $"YouBlock {{ Text = hi, Sent = {At} }}",
            $"ParticipantCard {{ Title = Answerer, Text = , Mission = Chat, Sent = {At}, Streaming = False }}",
        ], Map(User("hi"), Started("Chat:Answerer")));

        Assert.Equal([
            $"YouBlock {{ Text = hi, Sent = {At} }}",
            "NoticeLine { Text = (run interrupted) }",
        ], Map(User("hi"), Started("Chat:Answerer"), Status(ConversationRunStatus.Interrupted)));
    }

    [Fact]
    public void A_completed_turn_keeps_its_answered_cards()
    {
        var blocks = Map(Started("Chat:Answerer"), Step("Hello"), Status(ConversationRunStatus.Completed));

        Assert.Equal([$"ParticipantCard {{ Title = Answerer, Text = Hello, Mission = Chat, Sent = {At}, Streaming = False }}"], blocks);
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

    /// <summary>Phase 56 Task 5: the progress row and its spinner stay while the reply streams;
    /// Replying (the idle-sleep rule) is unchanged.</summary>
    [Fact]
    public void The_expert_is_streaming_while_deltas_grow_its_latest_card()
    {
        Assert.Null(Streaming(Started("Chat:Answerer")));
        Assert.Equal("Answerer", Streaming(Started("Chat:Answerer"), Delta("Hel")));
        Assert.Null(Replying(Started("Chat:Answerer"), Delta("Hel")));
        Assert.Equal("Answerer", Streaming(Started("ChatHands:Answerer"), Delta("Reading"), HandsRequested("Read", "notes.txt")));
        Assert.Null(Streaming(Started("Chat:Answerer"), Delta("Hel"), Step("Hello")));
        Assert.Null(Streaming(Started("Chat:Answerer"), Delta("Hel"), Status(ConversationRunStatus.Interrupted)));
        Assert.Null(Streaming());
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
            $"YouBlock {{ Text = read it, Sent = {At} }}",
            "HandsLine { Label = Read notes.txt, Outcome = succeeded }",
            $"ParticipantCard {{ Title = Answerer, Text = The codeword is kiwi., Mission = ChatHands, Sent = {At}, Streaming = False }}",
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
        Assert.Equal([$"YouBlock {{ Text = hi, Sent = {At} }}"], Map(User("hi"), HandsResult(MissionToolOutcome.Succeeded)));
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

    // Every test event and sent message happens at this one time (Phase 59).
    private static readonly DateTimeOffset At = new(2026, 10, 3, 22, 34, 0, TimeSpan.Zero);

    [Fact]
    public void A_sent_message_shows_a_pending_pill_and_reply_at_once()
    {
        Assert.Equal([
            $"PendingYouBlock {{ CommandId = {Sent}, Text = hi, Sent = {At} }}",
            $"PendingReplyBlock {{ CommandId = {Sent} }}",
        ], Strings(Submitted("hi")));
        Assert.Equal("", (string?)ReplyingMethod.Invoke(null, [Submitted("hi")]));
    }

    [Fact]
    public void Forges_echo_replaces_the_pending_pill_and_the_first_participant_the_pending_reply()
    {
        var echoed = ApplyAll(Submitted("hi"), User("hi") with { EventId = Sent });
        Assert.Equal([$"YouBlock {{ Text = hi, Sent = {At} }}", $"PendingReplyBlock {{ CommandId = {Sent} }}"], Strings(echoed));

        var started = ApplyAll(echoed, Started("Chat:Answerer"));
        Assert.Equal([$"YouBlock {{ Text = hi, Sent = {At} }}", $"ParticipantCard {{ Title = Answerer, Text = , Mission = Chat, Sent = {At}, Streaming = False }}"], Strings(started));
        Assert.Equal("Answerer", (string?)ReplyingMethod.Invoke(null, [started]));
    }

    [Fact]
    public void Another_commands_echo_is_appended_not_merged()
    {
        var blocks = ApplyAll(Submitted("hi"), User("from elsewhere"));

        Assert.Equal([
            $"PendingYouBlock {{ CommandId = {Sent}, Text = hi, Sent = {At} }}",
            $"PendingReplyBlock {{ CommandId = {Sent} }}",
            $"YouBlock {{ Text = from elsewhere, Sent = {At} }}",
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

        Assert.Equal([$"YouBlock {{ Text = hi, Sent = {At} }}", "NoticeLine { Text = (run failed) }"], Strings(ended));
    }

    // ── Phase 59: every message shows when it was sent ─────────────────────────────────────────

    [Fact]
    public void A_message_and_a_reply_card_carry_the_time_of_the_event_that_made_them()
    {
        var asked = At.AddMinutes(1);
        var started = At.AddMinutes(2);
        var blocks = Map(User("hi") with { OccurredAtUtc = asked }, Started("Chat:Answerer") with { OccurredAtUtc = started },
            Delta("Hel") with { OccurredAtUtc = At.AddMinutes(3) }, Step("Hello") with { OccurredAtUtc = At.AddMinutes(4) });

        Assert.Equal([
            $"YouBlock {{ Text = hi, Sent = {asked} }}",
            $"ParticipantCard {{ Title = Answerer, Text = Hello, Mission = Chat, Sent = {started}, Streaming = False }}",
        ], blocks);
    }

    [Fact]
    public void A_pending_pill_keeps_its_send_time_when_the_turn_ends_without_an_echo()
    {
        var ended = ApplyAll(Submitted("hi"), Status(ConversationRunStatus.Failed) with { OccurredAtUtc = At.AddMinutes(5) });

        Assert.Equal([$"YouBlock {{ Text = hi, Sent = {At} }}", "NoticeLine { Text = (run failed) }"], Strings(ended));
    }

    [Theory]
    [InlineData("en-US", @"^\d{1,2}:\d{2}\s(AM|PM)$")]
    [InlineData("de-DE", @"^\d{2}:\d{2}$")]
    public void A_time_is_local_in_the_system_short_time_format(string culture, string pattern)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var shown = (string)TranscriptMethod("TimeOf").Invoke(null, [At])!;

            Assert.Equal(At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture), shown);
            Assert.Matches(pattern, shown);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
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
        Assert.Equal([$"ParticipantCard {{ Title = Answerer, Text = Hello, Mission = Chat, Sent = {At}, Streaming = True }}"], growing);

        var final = Map(Started("Chat:Answerer"), Delta("Hel"), Delta("lo"), Step("Hello!"));
        Assert.Equal([$"ParticipantCard {{ Title = Answerer, Text = Hello!, Mission = Chat, Sent = {At}, Streaming = False }}"], final);
    }

    [Fact]
    public void A_card_cut_off_mid_stream_stops_streaming_when_its_turn_ends()
    {
        var ended = Map(Started("Chat:Answerer"), Delta("## Hel"), Status(ConversationRunStatus.Interrupted));

        Assert.Equal($"ParticipantCard {{ Title = Answerer, Text = ## Hel, Mission = Chat, Sent = {At}, Streaming = False }}", ended[0]);
    }

    [Fact]
    public void A_delta_with_no_card_changes_nothing()
    {
        Assert.Equal([$"YouBlock {{ Text = hi, Sent = {At} }}"], Map(User("hi"), Delta("orphan")));
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
        Assert.Equal([$"YouBlock {{ Text = from elsewhere, Sent = {At} }}", $"PendingReplyBlock {{ CommandId = {other.EventId} }}"], Strings(waiting));
        Assert.Equal("", (string?)ReplyingMethod.Invoke(null, [waiting]));

        var started = ApplyLive(waiting, Started("Chat:Answerer"));
        Assert.Equal([$"YouBlock {{ Text = from elsewhere, Sent = {At} }}", $"ParticipantCard {{ Title = Answerer, Text = , Mission = Chat, Sent = {At}, Streaming = False }}"], Strings(started));

        var ended = ApplyLive(waiting, Status(ConversationRunStatus.Failed));
        Assert.Equal([$"YouBlock {{ Text = from elsewhere, Sent = {At} }}", "NoticeLine { Text = (run failed) }"], Strings(ended));
    }

    [Fact]
    public void This_windows_echo_keeps_its_one_pending_reply()
    {
        var echoed = ApplyLive(Submitted("hi"), User("hi") with { EventId = Sent });
        Assert.Equal([$"YouBlock {{ Text = hi, Sent = {At} }}", $"PendingReplyBlock {{ CommandId = {Sent} }}"], Strings(echoed));
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
            (Func<Exception?, bool>)(failure =>
            {
                if (failure is not null) lost.Add(failure);
                return true;
            }), CancellationToken.None])!;

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

    [Fact]
    public async Task A_stream_that_times_out_waiting_is_reported_once_and_reopened_from_the_cursor()
    {
        var attempt = Guid.NewGuid();
        var (service, requests) = FakeConversations(new Queue<ConversationEvent[]>([
            [Seq(Started("Chat:Answerer"), 6)],
            StreamingConversations.TimedOut,
            [Seq(Step("Hello!"), 7), Seq(Status(ConversationRunStatus.Completed), 8) with { RunId = attempt }],
        ]));
        var lost = new List<Exception>();

        var cursor = await Stream(service, 5L, e => e.RunId == attempt, lost.Add, CancellationToken.None);

        Assert.Equal(8, cursor);
        Assert.Equal([(5L, true), (6L, true), (6L, true)], requests);
        var timeout = Assert.IsType<TaskCanceledException>(Assert.Single(lost));
        Assert.IsType<TimeoutException>(timeout.InnerException);
    }

    [Fact]
    public async Task Repeated_timeouts_each_reconnect()
    {
        var attempt = Guid.NewGuid();
        var (service, requests) = FakeConversations(new Queue<ConversationEvent[]>([
            StreamingConversations.TimedOut,
            StreamingConversations.TimedOut,
            [Seq(Status(ConversationRunStatus.Completed), 6) with { RunId = attempt }],
        ]));
        var lost = new List<Exception>();

        var cursor = await Stream(service, 5L, e => e.RunId == attempt, lost.Add, CancellationToken.None);

        Assert.Equal(6, cursor);
        Assert.Equal([(5L, true), (5L, true), (5L, true)], requests);
        Assert.Equal(2, lost.Count);
    }

    [Fact]
    public async Task A_session_cancel_ends_the_stream_quietly()
    {
        var (service, requests) = FakeConversations(new Queue<ConversationEvent[]>([StreamingConversations.Cancelled]));
        var lost = new List<Exception>();
        using var session = new CancellationTokenSource();
        session.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stream(service, 5L, _ => false, lost.Add, session.Token));

        Assert.Empty(lost);
        Assert.Single(requests);
    }

    [Fact]
    public async Task Without_a_lost_handler_a_timeout_stops_the_chat_with_connection_lost()
    {
        var (service, _) = FakeConversations(new Queue<ConversationEvent[]>([StreamingConversations.TimedOut]));

        var failure = await Assert.ThrowsAsync<TaskCanceledException>(() => FollowTurn(service));

        var (exit, error) = ReportConnectionLost(failure);
        Assert.Equal(1, exit);
        Assert.Equal("chat failed: connection lost (The request timed out.)", error);
    }

    [Fact]
    public async Task Without_a_lost_handler_a_cut_connection_stops_the_chat_with_connection_lost()
    {
        var (service, _) = FakeConversations(new Queue<ConversationEvent[]>([StreamingConversations.Broken]));

        var failure = await Assert.ThrowsAsync<IOException>(() => FollowTurn(service));

        var (exit, error) = ReportConnectionLost(failure);
        Assert.Equal(1, exit);
        Assert.Equal("chat failed: connection lost (connection reset)", error);
    }

    // ── Idle sleep (Phase 57 S5) ───────────────────────────────────────────────

    private const string ReconnectingText = "connection lost; reconnecting";
    private const string IdleText = "idle — reconnects when you type";

    [Fact]
    public async Task A_stream_whose_handler_declines_returns_its_cursor_without_reopening()
    {
        var (service, requests) = FakeConversations(new Queue<ConversationEvent[]>([[Seq(Step("Hello!"), 6)]]));
        var ends = new List<Exception?>();

        var cursor = await (Task<long>)StreamAsyncMethod.Invoke(null, [service, Guid.NewGuid(), 5L, true,
            (Action<ConversationEvent>)(_ => { }), (Func<ConversationEvent, bool>)(_ => false),
            (Func<Exception?, bool>)(failure =>
            {
                ends.Add(failure);
                return false;
            }), CancellationToken.None])!;

        Assert.Equal(6, cursor);
        Assert.Single(requests);
        Assert.Null(Assert.Single(ends));
    }

    [Fact]
    public void The_reconnect_notice_is_removed_by_the_first_live_event()
    {
        var lost = ApplyAll(Submitted("hi"), Seq(User("hi") with { EventId = Sent }, 6));
        var noticeType = Forge.GetType("ForgeMission.Cli.Tui.NoticeLine", throwOnError: true)!;
        lost = Append(lost, Activator.CreateInstance(noticeType, ReconnectingText)!);

        Assert.DoesNotContain($"NoticeLine {{ Text = {ReconnectingText} }}", Strings(ApplyLive(lost, Seq(Started("Chat:Answerer"), 7))));
        Assert.DoesNotContain($"NoticeLine {{ Text = {ReconnectingText} }}", Strings(ApplyLive(lost, Delta("x", 6))));
    }

    [Fact]
    public void The_idle_notice_shows_until_wake_and_blocks_after_it_stay()
    {
        var idle = TranscriptMethod("Idle").Invoke(null, [Blocks([User("hi")])])!;
        Assert.Equal([$"YouBlock {{ Text = hi, Sent = {At} }}", $"NoticeLine {{ Text = {IdleText} }}"], Strings(idle));

        var typed = TranscriptMethod("Submit").Invoke(null, [idle, Sent, "next", At])!;
        Assert.Equal([$"YouBlock {{ Text = hi, Sent = {At} }}", $"PendingYouBlock {{ CommandId = 11111111-1111-1111-1111-111111111111, Text = next, Sent = {At} }}",
            "PendingReplyBlock { CommandId = 11111111-1111-1111-1111-111111111111 }"],
            Strings(TranscriptMethod("Awake").Invoke(null, [typed])!));
    }

    [Fact]
    public async Task A_stream_that_ends_with_no_turn_in_flight_sleeps_and_shows_the_idle_notice()
    {
        var (service, requests) = FakeConversations(new Queue<ConversationEvent[]>([[Seq(Step("Hello!"), 6)]]));
        var link = Link(service, inFlight: () => false, out var notices, out _);

        Start(link, 5);

        Assert.Equal(6, await Live(link));
        Assert.Equal([(5L, true)], requests);
        Assert.Equal(["Idle"], notices);
        Assert.True(Asleep(link));
    }

    [Fact]
    public async Task A_wake_while_asleep_catches_up_from_the_saved_cursor_then_reopens_the_live_stream()
    {
        var (service, requests) = FakeConversations(new Queue<ConversationEvent[]>([
            [Seq(Step("Hello!"), 6)],
            [Seq(Step("missed"), 7)],
            StreamingConversations.Held,
        ]));
        var fake = (StreamingConversations)service;
        fake.LastSequence = 7;
        var link = Link(service, inFlight: () => false, out var notices, out var shown);
        Start(link, 5);
        await Live(link);

        await Wake(link);

        Assert.False(Asleep(link));
        fake.Release.SetResult();
        Assert.Equal(7, await Live(link));
        Assert.Equal([(5L, true), (6L, false), (7L, true)], requests);
        Assert.Equal(["Hello!", "missed"], shown.Select(e => e.Text));
        Assert.Equal(["Idle", "Awake", "Idle"], notices);
    }

    [Fact]
    public async Task A_stream_that_ends_during_a_turn_reconnects_at_once_and_reports_only_a_transport_failure()
    {
        var running = true;
        var (service, requests) = FakeConversations(new Queue<ConversationEvent[]>([
            [Seq(Started("Chat:Answerer"), 6)],
            [],
            StreamingConversations.Broken,
            StreamingConversations.Broken,
            [Seq(Status(ConversationRunStatus.Completed), 7)],
        ]));
        var link = Link(service, inFlight: () => running, out var notices, out var shown, show: e => running = TurnRunning(running, e));

        Start(link, 5);

        Assert.Equal(7, await Live(link));
        Assert.Equal([(5L, true), (6L, true), (6L, true), (6L, true), (6L, true)], requests);
        Assert.Equal(["Reconnecting", "Idle"], notices);
    }

    [Fact]
    public async Task A_wake_while_awake_does_nothing()
    {
        var (service, requests) = FakeConversations(new Queue<ConversationEvent[]>([]));
        var link = Link(service, inFlight: () => false, out var notices, out _);

        await Wake(link);

        Assert.Empty(requests);
        Assert.Empty(notices);
    }

    [Fact]
    public async Task Cancelling_the_session_while_asleep_ends_quietly_and_a_later_wake_does_nothing()
    {
        var (service, requests) = FakeConversations(new Queue<ConversationEvent[]>([[]]));
        using var session = new CancellationTokenSource();
        var link = Link(service, inFlight: () => false, out var notices, out _, session: session.Token);
        Start(link, 5);
        await Live(link);

        session.Cancel();
        await Wake(link);

        Assert.True(Asleep(link));
        Assert.Equal(5, await Live(link));
        Assert.Single(requests);
        Assert.Equal(["Idle"], notices);
    }

    [Fact]
    public async Task A_catch_up_that_fails_in_transport_goes_back_to_sleep()
    {
        var (service, requests) = FakeConversations(new Queue<ConversationEvent[]>([[], StreamingConversations.Broken]));
        var link = Link(service, inFlight: () => false, out var notices, out _);
        Start(link, 5);
        await Live(link);

        await Wake(link);

        Assert.True(Asleep(link));
        Assert.Equal(5, await Live(link));
        Assert.Single(requests);
        Assert.Equal(["Idle", "Awake", "Idle"], notices);
    }

    [Fact]
    public async Task An_Enter_that_wakes_the_window_sees_another_windows_turn_before_it_can_send()
    {
        var running = false;
        var (service, _) = FakeConversations(new Queue<ConversationEvent[]>([
            [],
            // While asleep another window sent a message; its turn is running on the server.
            [Seq(User("from elsewhere"), 6)],
            StreamingConversations.Held,
            [Seq(Status(ConversationRunStatus.Completed), 7)],
        ]));
        var fake = (StreamingConversations)service;
        fake.LastSequence = 6;
        object? link = null;
        bool? readyDuringCatchUp = null;
        link = Link(service, inFlight: () => running, out _, out _, show: e =>
        {
            running = TurnRunning(running, e);
            if (e.Sequence == 6) readyDuringCatchUp = Ready(link!);
        });
        Start(link, 5);
        await Live(link);
        Assert.False(Ready(link));

        // Send waits for the wake (the catch-up) before it applies the send rule.
        await Wake(link);

        Assert.False(readyDuringCatchUp);
        Assert.True(Ready(link));
        Assert.True(running);
        fake.Release.SetResult();
        Assert.Equal(7, await Live(link));
        Assert.False(running);
    }

    private static Type ChatLinkType => Forge.GetType("ForgeMission.Cli.Tui.ChatLink", throwOnError: true)!;

    private static object Link(object service, Func<bool> inFlight, out List<string> notices, out List<ConversationEvent> shown,
        Action<ConversationEvent>? show = null, CancellationToken session = default)
    {
        var seen = new List<ConversationEvent>();
        var said = new List<string>();
        var noticeType = Forge.GetType("ForgeMission.Cli.Tui.LinkNotice", throwOnError: true)!;
        var notice = typeof(ChatTranscriptTests).GetMethod(nameof(Collect), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(noticeType).Invoke(null, [said])!;
        var link = Activator.CreateInstance(ChatLinkType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [service, Guid.NewGuid(), inFlight, (Action<ConversationEvent>)(e =>
            {
                seen.Add(e);
                show?.Invoke(e);
            }), notice, session], null)!;
        notices = said;
        shown = seen;
        return link;
    }

    private static Action<T> Collect<T>(List<string> sink) => item => sink.Add(item!.ToString()!);

    private static void Start(object link, long cursor) => ChatLinkType.GetMethod("Start")!.Invoke(link, [cursor]);

    private static Task Wake(object link) => (Task)ChatLinkType.GetMethod("WakeAsync")!.Invoke(link, [])!;

    private static Task<long> Live(object link) => (Task<long>)ChatLinkType.GetProperty("Live")!.GetValue(link)!;

    private static bool Asleep(object link) => (bool)ChatLinkType.GetProperty("Asleep")!.GetValue(link)!;

    private static bool Ready(object link) => (bool)ChatLinkType.GetProperty("Ready")!.GetValue(link)!;

    private static object Append(object blocks, object block)
    {
        var blockType = Forge.GetType("ForgeMission.Cli.Tui.TranscriptBlock", throwOnError: true)!;
        var items = ((System.Collections.IEnumerable)blocks).Cast<object>().Append(block).ToArray();
        var array = Array.CreateInstance(blockType, items.Length);
        Array.Copy(items, array, items.Length);
        return array;
    }

    // The TUI's handler: report a transport failure, always reopen (a turn in flight).
    private static Task<long> Stream(object service, long cursor, Func<ConversationEvent, bool> ends, Action<Exception> lost,
        CancellationToken ct) =>
        (Task<long>)StreamAsyncMethod.Invoke(null, [service, Guid.NewGuid(), cursor, true,
            (Action<ConversationEvent>)(_ => { }), ends, (Func<Exception?, bool>)(failure =>
            {
                if (failure is not null) lost(failure);
                return true;
            }), ct])!;

    private static Task<long> FollowTurn(object service) => (Task<long>)FollowTurnAsyncMethod.Invoke(null,
        [service, Guid.NewGuid(), 5L, Guid.NewGuid(), false, (Action<ConversationEvent>)(_ => { }), CancellationToken.None])!;

    private static (int Exit, string Error) ReportConnectionLost(Exception failure)
    {
        var error = new StringWriter();
        var exit = (int)Forge.GetType("ForgeMission.Cli.ForgeChat", throwOnError: true)!
            .GetMethod("ReportConnectionLost", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [failure, error])!;
        return (exit, error.ToString().TrimEnd());
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
            if (targetMethod?.Name == "GetConversationAsync") return Snapshot();
            if (targetMethod?.Name != "StreamEventsAsync") throw new NotSupportedException(targetMethod?.Name);
            Requests.Add(((long)args![1]!, (bool)args[2]!));
            return Serve(Connections.Dequeue());
        }

        /// <summary>The conversation's last stored sequence, as <c>GetConversationAsync</c> reports it;
        /// <see cref="Broken"/> as the first queued connection makes that read fail in transport.</summary>
        public long LastSequence { get; set; }

        private Task<GetConversationResponse> Snapshot()
        {
            if (Connections.Count > 0 && ReferenceEquals(Connections.Peek(), Broken))
            {
                Connections.Dequeue();
                return Task.FromException<GetConversationResponse>(new HttpRequestException("connection refused"));
            }
            return Task.FromResult(new GetConversationResponse(new ConversationSnapshot(Guid.Empty, null, null, LastSequence,
                ConversationRunStatus.Completed, null, DateTimeOffset.UtcNow)));
        }

        // Each marker is its own instance (an empty collection expression is one shared array).
        /// <summary>A connection that fails in transport (an SSE body cut mid-read).</summary>
        public static readonly ConversationEvent[] Broken = new ConversationEvent[0];

        /// <summary>A connection that never gets headers: HttpClient's timeout, a cancellation that is not the session's.</summary>
        public static readonly ConversationEvent[] TimedOut = new ConversationEvent[0];

        /// <summary>A connection ended by the session's own cancellation (Ctrl-D).</summary>
        public static readonly ConversationEvent[] Cancelled = new ConversationEvent[0];

        /// <summary>A connection that stays open, with no events, until <see cref="Release"/> completes.</summary>
        public static readonly ConversationEvent[] Held = new ConversationEvent[0];

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async IAsyncEnumerable<ConversationEvent> Serve(ConversationEvent[] events)
        {
            await Task.Yield();
            if (ReferenceEquals(events, Held)) await Release.Task;
            if (ReferenceEquals(events, Broken)) throw new IOException("connection reset");
            if (ReferenceEquals(events, TimedOut)) throw new TaskCanceledException("The request timed out.", new TimeoutException());
            if (ReferenceEquals(events, Cancelled)) throw new OperationCanceledException("session cancelled");
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
        TranscriptMethod("Submit").Invoke(null, [Blocks([]), Sent, text, At])!;

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

    private static string? Streaming(params ConversationEvent[] events) =>
        (string?)Forge.GetType("ForgeMission.Cli.Tui.Transcript", throwOnError: true)!
            .GetMethod("Streaming", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [Blocks(events)]);

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
        null, null, null, null, null, null, At);

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
