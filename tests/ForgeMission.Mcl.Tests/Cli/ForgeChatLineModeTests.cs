using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ForgeMission.Conversations.Contracts;

namespace ForgeMission.Tests.Cli;

// Phase 76 M0: what forge chat's line mode prints for each event, pinned on the console so the
// pins hold while chat moves to the shared client path. The chat continues the project's hosted
// conversation, whose stored history is replayed, then end of input at the prompt exits.
[Collection(ConsoleCollection.Name)]
public sealed class ForgeChatLineModeTests : IDisposable
{
    private static readonly Type ChatType = ForgeText.Type("ForgeMission.Cli.Commands.ForgeChat");
    private static readonly MethodInfo ChatInProject = ChatMethod("ChatInProjectAsync");
    private static readonly DateTimeOffset Sent = new(2026, 11, 3, 14, 5, 0, TimeSpan.Zero);
    private static readonly string Time = Sent.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    private readonly string _home = Path.Combine(Path.GetTempPath(), "forge-chat-lines-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _projectId = Guid.NewGuid();

    public ForgeChatLineModeTests() => Directory.CreateDirectory(_home);

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public async Task A_user_message_prints_the_sender_time_and_text()
    {
        Assert.Equal($"you · {Time}> hello\n", await ReplayAsync(Event(ConversationEventKind.UserMessage, "hello")));
    }

    [Fact]
    public async Task A_participant_start_prints_its_name_and_time_in_brackets()
    {
        Assert.Equal($"[Researcher · {Time}]\n", await ReplayAsync(Event(ConversationEventKind.ParticipantStarted, "Researcher")));
    }

    [Fact]
    public async Task A_reply_prints_its_text_and_a_blank_line()
    {
        Assert.Equal("the answer\n\n", await ReplayAsync(Event(ConversationEventKind.ParticipantMessage, "the answer", attempt: 1)));
    }

    // Only a final result (no attempt) with the same text is dropped; a step that repeats it is not.
    [Fact]
    public async Task A_final_result_that_repeats_the_last_reply_prints_once()
    {
        var printed = await ReplayAsync(
            Event(ConversationEventKind.ParticipantMessage, "the answer", attempt: 1),
            Event(ConversationEventKind.ParticipantMessage, "the answer"),
            Event(ConversationEventKind.ParticipantMessage, "a new result"),
            Event(ConversationEventKind.ParticipantMessage, "a new result", attempt: 2));

        Assert.Equal("the answer\n\na new result\n\na new result\n\n", printed);
    }

    [Fact]
    public async Task An_error_prints_its_reason_or_else_its_text()
    {
        var printed = await ReplayAsync(
            Event(ConversationEventKind.Error, "error text", reason: "error reason"),
            Event(ConversationEventKind.Error, "text only"));

        Assert.Equal("error: error reason\nerror: text only\n", printed);
    }

    // One line per tool use: the request starts it and its outcome completes it.
    [Fact]
    public async Task A_tool_use_prints_one_line_its_outcome_completes()
    {
        var printed = await ReplayAsync(
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
    public async Task Only_a_run_that_ended_without_completing_prints_its_status()
    {
        var printed = await ReplayAsync(
            Event(ConversationEventKind.RunStatus, status: ConversationRunStatus.Running),
            Event(ConversationEventKind.RunStatus, status: ConversationRunStatus.Completed),
            Event(ConversationEventKind.RunStatus, status: ConversationRunStatus.Failed),
            Event(ConversationEventKind.RunStatus, status: ConversationRunStatus.Rejected),
            Event(ConversationEventKind.RunStatus, status: ConversationRunStatus.Interrupted));

        Assert.Equal("(run failed)\n(run rejected)\n(run interrupted)\n", printed);
    }

    /// <summary>Runs a plain line-mode chat over <paramref name="history"/> and returns what it
    /// printed between the project line and the prompt, after checking both and the exit.</summary>
    private async Task<string> ReplayAsync(params ConversationEvent[] history)
    {
        var projectFile = Path.Combine(_home, "forge1.project.json");
        await File.WriteAllTextAsync(projectFile,
            "{\"projectId\":\"" + _projectId + "\",\"missions\":[\"Chat@1\"],\"folders\":[\".\"]}");
        using var host = new HistoryHost(_projectId, history);
        await using var app = CreateApp(host);
        using var console = new ConsoleCapture();

        var exit = await (Task<int>)ChatInProject.Invoke(null, [app, projectFile,
            ForgeText.Theme("Dark"), null, new ConcurrentQueue<string>(), CancellationToken.None])!;

        Assert.Equal((0, ""), (exit, console.Error));
        var start = $"Project: {_home}\n";
        Assert.StartsWith(start, console.Out);
        Assert.EndsWith("you> ", console.Out);
        return console.Out[start.Length..^"you> ".Length];
    }

    private static ConversationEvent Event(ConversationEventKind kind, string? text = null, string? reason = null, int? attempt = null,
        ConversationRunStatus? status = null, MissionToolRequest? request = null, MissionToolOutcome? outcome = null) =>
        new(Guid.NewGuid(), 1, Guid.Empty, null, 0, kind, ConversationParticipant.User, attempt, text, reason, null, null, null,
            null, status, Sent, request, outcome);

    private static MissionToolRequest Tool(string name, string arguments) => new(Guid.NewGuid(), Guid.Empty, Guid.Empty,
        "mission", "agent", "call-1", name, JsonDocument.Parse(arguments).RootElement.Clone());

    private static IAsyncDisposable CreateApp(IHttpClientFactory host)
    {
        var appType = ChatInProject.GetParameters()[0].ParameterType;
        var create = appType.GetMethod("Create")!;
        var publishType = create.GetParameters()[1].ParameterType;
        var publish = typeof(ForgeChatLineModeTests).GetMethod(nameof(IgnoreEvents), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(publishType.GetGenericArguments()[0]).CreateDelegate(publishType);
        return (IAsyncDisposable)create.Invoke(null, [host, publish, CancellationToken.None])!;
    }

    private static MethodInfo ChatMethod(string name) => ChatType.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static void IgnoreEvents<T>(T item) { }

    // A controlled Host holding the project's one finished chat conversation, at the id the open
    // asks for: its stored events are numbered from 1 and replayed on the event stream. Every Hands
    // route of the open waits for work.
    private sealed class HistoryHost(Guid projectId, IReadOnlyList<ConversationEvent> history) : HttpMessageHandler, IHttpClientFactory
    {
        private const string Definition = "mission Chat(message) = {\n    Assistant using anthropic\n}\n";
        private readonly DurableMissionLaunch _launch = new(Guid.NewGuid(), 1, "sha256:test", Definition,
            MissionHandsProfile.ProjectWorkspace, new DurableMissionPackage(1, "sha256:package", Definition, "Chat", "message", []));

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false)
        {
            BaseAddress = new Uri("https://controlled-host.invalid/"),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var route = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            var json = route switch
            {
                "/api/GetConversation" => JsonSerializer.Serialize(new GetConversationResponse(Snapshot(ConversationOf(body))),
                    ConversationContractsJsonContext.Default.GetConversationResponse),
                "/api/StreamConversationEvents" => string.Concat(History(ConversationOf(body)).Select(item =>
                    $"data: {JsonSerializer.Serialize(item, ConversationContractsJsonContext.Default.ConversationEvent)}\n\n")),
                _ => "{\"status\":\"awaitingHands\"}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private ConversationSnapshot Snapshot(Guid conversationId) => new(conversationId, null, null, history.Count,
            ConversationRunStatus.Completed, null, Sent, Purpose: ConversationPurpose.MissionConversation, ProjectId: projectId,
            PinnedLaunch: _launch);

        private IEnumerable<ConversationEvent> History(Guid conversationId) =>
            history.Select((item, index) => item with { ConversationId = conversationId, Sequence = index + 1 });

        private static Guid ConversationOf(string body)
        {
            using var request = JsonDocument.Parse(body);
            return request.RootElement.GetProperty("conversationId").GetGuid();
        }
    }
}
