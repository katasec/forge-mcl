using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ForgeMission.Conversations.Contracts;

namespace ForgeMission.Tests.Cli;

// Phase 76: the TUI's own turn is forge-client's RunTurnAsync, which also feeds Hands. When its
// follow loses the connection after Forge has the turn (a cut stream, or HttpClient's timeout, which
// is not the session's cancel), the TUI runs the turn again with the same command id, so a Hands
// request that arrives after the drop still reaches Hands (as it did when the reconnecting display
// stream fed Hands). The TUI builds visuals, so it runs in the XenoAtom UI collection.
[Collection(XenoAtomUiCollection.Name)]
public sealed class ChatTuiRefollowTests : IDisposable
{
    private static readonly Type TuiType = ForgeText.Type("ForgeMission.Cli.Tui.ChatTui");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _home = Path.Combine(Path.GetTempPath(), "forge-tui-refollow-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _projectId = Guid.NewGuid();

    public ChatTuiRefollowTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        var projection = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge", "sessions",
            _projectId.ToString("N"));
        if (Directory.Exists(projection)) Directory.Delete(projection, recursive: true);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("timeout")]
    public async Task A_lost_turn_follow_runs_the_turn_again_with_its_command_id_and_its_hands_request_reaches_hands(string loss)
    {
        var projectFile = Path.Combine(_home, "forge.project.json");
        await File.WriteAllTextAsync(projectFile, "{\"projectId\":\"" + _projectId + "\",\"missions\":[\"Chat@1\"],\"folders\":[\".\"]}");
        using var host = new DroppingHost(_projectId, loss == "timeout"
            ? new TaskCanceledException("The request timed out.", new TimeoutException())
            : new IOException("connection reset"));
        await using var app = CreateApp(host);
        var conversations = app.GetType().GetProperty("MissionConversations")!.GetValue(app)!;
        var opened = await Result(conversations.GetType().GetMethod("ContinueChatAsync")!
            .Invoke(conversations, [projectFile, new Progress<string>(), CancellationToken.None])!);
        await using var conversation = (IAsyncDisposable)opened.GetType().GetProperty("Conversation")!.GetValue(opened)!;
        using var session = new CancellationTokenSource();
        var tui = Activator.CreateInstance(TuiType, Private, null, [conversations, conversation,
            Activator.CreateInstance(ForgeText.Type("ForgeMission.Cli.Tui.ChatHeader"), "chat", "Chat", 1, "anthropic", "ameer"),
            ForgeText.Theme("Dark"), ForgeText.Fonts(), new ConcurrentQueue<string>(), session], null)!;

        var commandId = Guid.NewGuid();
        await RunOwnTurnAsync(tui, commandId, "hello").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(host.HandsClaimedAfterDrop, string.Join(", ", host.Requests));
        Assert.Equal(2, host.Requests.Count(route => route == "/api/StreamConversationEvents"));
        Assert.Equal([commandId, commandId], host.SubmittedCommands);
    }

    private static Task RunOwnTurnAsync(object tui, Guid commandId, string text)
    {
        var sentType = TuiType.GetNestedType("SentMessage", BindingFlags.NonPublic)!;
        var sent = Activator.CreateInstance(sentType, commandId, text)!;
        var own = new CancellationTokenSource();
        TuiType.GetField("_ownTurn", Private)!.SetValue(tui, own);
        return (Task)TuiType.GetMethod("RunOwnTurnAsync", Private)!.Invoke(tui, [sent, own])!;
    }

    private static async Task<object> Result(object task)
    {
        await (Task)task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static IAsyncDisposable CreateApp(IHttpClientFactory host)
    {
        var appType = ForgeText.Type("ForgeMission.Cli.Commands.ForgeChat")
            .GetMethod("ChatInProjectAsync", BindingFlags.Static | BindingFlags.NonPublic)!.GetParameters()[0].ParameterType;
        var create = appType.GetMethod("Create")!;
        var publishType = create.GetParameters()[1].ParameterType;
        var publish = typeof(ChatTuiRefollowTests).GetMethod(nameof(IgnoreEvents), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(publishType.GetGenericArguments()[0]).CreateDelegate(publishType);
        return (IAsyncDisposable)create.Invoke(null, [host, publish, CancellationToken.None])!;
    }

    private static void IgnoreEvents<T>(T item) { }

    // A controlled Host with the project's chat at the id the open asks for. A submit of the same
    // command id is the same turn. The first event stream of the turn sends its echo, then fails
    // with <loss>. The next one sends a Hands request, holds the turn open until the attachment
    // claims work after it (or 5 s pass), then completes.
    private sealed class DroppingHost(Guid projectId, Exception loss) : HttpMessageHandler, IHttpClientFactory
    {
        private const string Definition = "mission Chat(message) = {\n    Assistant using anthropic\n}\n";
        private readonly DurableMissionLaunch _launch = new(Guid.NewGuid(), 1, "sha256:test", Definition,
            MissionHandsProfile.ProjectWorkspace, new DurableMissionPackage(1, "sha256:package", Definition, "Chat", "message", []));
        private readonly TaskCompletionSource _claimedAfterDrop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private readonly List<string> _requests = [];
        private readonly List<Guid> _submitted = [];
        private Guid _attemptId;
        private int _streams;
        private volatile bool _handsRequested;

        public bool HandsClaimedAfterDrop => _claimedAfterDrop.Task.IsCompletedSuccessfully;

        public List<Guid> SubmittedCommands
        {
            get { lock (_gate) return [.. _submitted]; }
        }

        public List<string> Requests
        {
            get { lock (_gate) return [.. _requests]; }
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false)
        {
            BaseAddress = new Uri("https://controlled-host.invalid/"),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var route = request.RequestUri!.AbsolutePath;
            lock (_gate) _requests.Add(route);
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            return route switch
            {
                "/api/GetConversation" => Json(new GetConversationResponse(Snapshot(ConversationOf(body)))),
                "/api/SubmitMissionTurn" => Submit(ConversationOf(body), CommandOf(body)),
                "/api/StreamConversationEvents" => Stream(ConversationOf(body)),
                "/api/ClaimMissionHandsWork" => Claim(),
                _ => Raw("{\"status\":\"awaitingHands\"}"),
            };
        }

        private ConversationSnapshot Snapshot(Guid conversationId) => new(conversationId, null,
            _attemptId == Guid.Empty ? null : _attemptId, 0,
            _attemptId == Guid.Empty ? ConversationRunStatus.Completed : ConversationRunStatus.Running, null, DateTimeOffset.UtcNow,
            Purpose: ConversationPurpose.MissionConversation, ProjectId: projectId, PinnedLaunch: _launch);

        private HttpResponseMessage Submit(Guid conversationId, Guid commandId)
        {
            lock (_gate) _submitted.Add(commandId);
            if (_attemptId == Guid.Empty) _attemptId = Guid.NewGuid();
            return Json(new SubmitMissionTurnResponse(conversationId, Guid.NewGuid(), _attemptId, 1, ConversationRunStatus.Running));
        }

        private HttpResponseMessage Claim()
        {
            if (_handsRequested) _claimedAfterDrop.TrySetResult();
            return Raw("{\"status\":\"awaitingHands\",\"reason\":\"nothing to claim in this test\"}");
        }

        private HttpResponseMessage Stream(Guid conversationId)
        {
            var pipe = new Pipe();
            var first = Interlocked.Increment(ref _streams) == 1;
            _ = Task.Run(async () =>
            {
                if (first)
                {
                    await WriteAsync(pipe.Writer, Event(conversationId, 1, ConversationEventKind.UserMessage, null));
                    // The echo is read before the connection drops.
                    await Task.Delay(TimeSpan.FromMilliseconds(200));
                    await pipe.Writer.CompleteAsync(loss);
                    return;
                }
                await WriteAsync(pipe.Writer, Event(conversationId, 2, ConversationEventKind.MissionHandsRequested, null));
                _handsRequested = true;
                await Task.WhenAny(_claimedAfterDrop.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                await WriteAsync(pipe.Writer, Event(conversationId, 3, ConversationEventKind.RunStatus, ConversationRunStatus.Completed));
                await pipe.Writer.CompleteAsync();
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
        }

        private ConversationEvent Event(Guid conversationId, long sequence, ConversationEventKind kind, ConversationRunStatus? status) =>
            new(Guid.NewGuid(), 1, conversationId, _attemptId, sequence, kind, ConversationParticipant.User, null, "hello", null,
                null, null, null, null, status, DateTimeOffset.UtcNow);

        private static async Task WriteAsync(PipeWriter writer, ConversationEvent item)
        {
            var json = JsonSerializer.Serialize(item, ConversationContractsJsonContext.Default.ConversationEvent);
            await writer.WriteAsync(Encoding.UTF8.GetBytes($"data: {json}\n\n"));
        }

        private static Guid ConversationOf(string body) => GuidOf(body, "conversationId");

        private static Guid CommandOf(string body) => GuidOf(body, "commandId");

        private static Guid GuidOf(string body, string property)
        {
            using var request = JsonDocument.Parse(body);
            return request.RootElement.GetProperty(property).GetGuid();
        }

        private static HttpResponseMessage Json<T>(T value) =>
            Raw(JsonSerializer.Serialize(value, typeof(T), ConversationContractsJsonContext.Default));

        private static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
