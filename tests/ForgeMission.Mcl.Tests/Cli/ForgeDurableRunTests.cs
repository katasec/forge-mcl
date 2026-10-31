using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ForgeMission.Conversations.Contracts;
using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge run --project (Phase 76, Hands by default): the durable run opens the project, attaches
// Hands rooted at the project folder before it submits the turn, and feeds the followed events
// to the attachment. The CLI is loaded through reflection like the other CLI tests.
public sealed class ForgeDurableRunTests : IDisposable
{
    private const string Source = "mission Review(task) = {\n  Researcher\n}\n";
    private const string Expert = "---\nname: Researcher\nkind: llm\ninput: task\noutput: answer\n---\n{{task}}";
    private static readonly Type ChatType = ForgeText.Type("ForgeMission.Cli.Commands.ForgeChat");
    private static readonly MethodInfo RunTurn = ForgeText.Type("ForgeMission.Cli.Commands.ForgeDurableRun")
        .GetMethod("RunTurnAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo RunProject = ForgeText.Type("ForgeMission.Cli.Commands.HostedMissionCommands")
        .GetMethod("RunProject", BindingFlags.Static | BindingFlags.NonPublic)!;

    private readonly string _home = Path.Combine(Path.GetTempPath(), "forge-durable-run-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _projectId = Guid.NewGuid();

    public ForgeDurableRunTests() => Directory.CreateDirectory(_home);

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public async Task Hands_attaches_before_the_turn_is_submitted_and_receives_the_followed_events()
    {
        var projectFile = await WriteProjectAsync("Review@1");
        using var host = new RunHost(_projectId);
        await using var app = CreateApp(host);

        var exit = await (Task<int>)RunTurn.Invoke(null,
            [app, projectFile, Path.Combine(_home, "mission.mcl"), "write a file", CancellationToken.None])!;

        Assert.Equal(0, exit);
        var routes = host.Requests;
        Assert.True(routes.IndexOf("/api/AttachMissionHands") is >= 0 and var attach &&
            attach < routes.IndexOf("/api/SubmitMissionTurn"), string.Join(", ", routes));
        Assert.Contains("/api/ClaimMissionHandsWork", routes);
        Assert.Equal(MissionHandsProfile.ProjectWorkspace, host.Launch!.Profile);
    }

    [Fact]
    public async Task A_mission_not_declared_in_the_project_stops_before_any_conversation_is_created()
    {
        var projectFile = await WriteProjectAsync("Other@1");
        using var host = new RunHost(_projectId);
        await using var app = CreateApp(host);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => (Task<int>)RunTurn.Invoke(null,
            [app, projectFile, Path.Combine(_home, "mission.mcl"), "write a file", CancellationToken.None])!);

        Assert.Contains("No declared mission reference exists for Review.", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"Declare Review@1 in {projectFile}.", failure.Message, StringComparison.Ordinal);
        Assert.Empty(host.Requests);
    }

    [Fact]
    public void An_explicit_project_option_is_used_as_given()
    {
        Assert.Equal("elsewhere.json", RunProject.Invoke(null, ["elsewhere.json", _home]));
    }

    [Fact]
    public void Without_the_option_the_project_file_in_the_directory_is_used()
    {
        Assert.Equal(Path.Combine(_home, "forge.project.json"), RunProject.Invoke(null, [null, _home]));
    }

    // Every run needs a project file: without one, forge run stops before sign-in or any network call.
    [Fact]
    public async Task A_run_without_a_project_file_stops_with_a_clear_message()
    {
        await using var harness = ForgeCliHarness.Start();
        harness.SignIn("https://example.invalid");
        // The process sees the temp folder through resolved symlinks (/private/var on macOS), so the
        // message is pinned by its folder-relative end rather than the full path.
        var projectFile = Path.Combine(Path.GetFileName(harness.WorkingDirectory), "forge.project.json");

        var result = await harness.RunAsync("run", "./m.mcl", "hello");

        Assert.Equal((1, ""), (result.ExitCode, result.StandardOutput));
        var error = result.StandardError.Trim();
        Assert.StartsWith("Project file not found: /", error);
        Assert.EndsWith($"/{projectFile}. Run `forge project create`.", error);
        Assert.Empty(harness.Proxy.Attempts);
    }

    private async Task<string> WriteProjectAsync(string mission)
    {
        Directory.CreateDirectory(Path.Combine(_home, "experts", "Researcher"));
        await File.WriteAllTextAsync(Path.Combine(_home, "mission.mcl"), Source);
        await File.WriteAllTextAsync(Path.Combine(_home, "experts", "Researcher", "expert.md"), Expert);
        await File.WriteAllTextAsync(Path.Combine(_home, "forge.toml"), "[experts]\nResearcher = \"https://invalid.example/Researcher\"\n");
        var projectFile = Path.Combine(_home, "forge.project.json");
        await File.WriteAllTextAsync(projectFile,
            "{\"projectId\":\"" + _projectId + "\",\"missions\":[\"" + mission + "\"],\"folders\":[]}");
        return projectFile;
    }

    private static IAsyncDisposable CreateApp(IHttpClientFactory host)
    {
        var appType = RunTurn.GetParameters()[0].ParameterType;
        var create = appType.GetMethod("Create")!;
        var publishType = create.GetParameters()[3].ParameterType;
        var publish = typeof(ForgeDurableRunTests).GetMethod(nameof(IgnoreEvents), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(publishType.GetGenericArguments()[0]).CreateDelegate(publishType);
        var policy = ChatType.GetMethod("PolicyFor", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [true]);
        return (IAsyncDisposable)create.Invoke(null, [host, null, policy, publish, CancellationToken.None])!;
    }

    private static void IgnoreEvents<T>(T item) { }

    // A controlled Host for one run. The event stream sends a Hands request, then holds the turn
    // open until the attachment claims it, so the claim proves the event reached the attachment.
    private sealed class RunHost(Guid projectId) : HttpMessageHandler, IHttpClientFactory
    {
        private readonly TaskCompletionSource _claimed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private readonly List<string> _requests = [];
        private Guid _conversationId;
        private Guid _attemptId;

        public DurableMissionLaunch? Launch { get; private set; }

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
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return route switch
            {
                "/api/CreateMissionConversation" => Create(body),
                "/api/GetConversation" => Json(new GetConversationResponse(Snapshot())),
                "/api/SubmitMissionTurn" => Submit(),
                "/api/StreamConversationEvents" => Stream(),
                "/api/ClaimMissionHandsWork" => Claim(),
                // Every other Hands route: the attachment waits for work.
                _ => Raw("{\"status\":\"awaitingHands\"}"),
            };
        }

        private HttpResponseMessage Create(string body)
        {
            var created = JsonSerializer.Deserialize(body, ConversationContractsJsonContext.Default.CreateMissionConversationRequest)!;
            Launch = created.Launch;
            _conversationId = ConversationDeterministicIds.MissionConversation(created.CommandId);
            return Json(new CreateMissionConversationResponse(_conversationId, 1, created.Launch));
        }

        private HttpResponseMessage Submit()
        {
            _attemptId = Guid.NewGuid();
            return Json(new SubmitMissionTurnResponse(_conversationId, Guid.NewGuid(), _attemptId, 2, ConversationRunStatus.Running));
        }

        private HttpResponseMessage Claim()
        {
            _claimed.TrySetResult();
            return Raw("{\"status\":\"awaitingHands\",\"reason\":\"nothing to claim in this test\"}");
        }

        private HttpResponseMessage Stream()
        {
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                await WriteEventAsync(pipe.Writer, 3, ConversationEventKind.MissionHandsRequested, null, null);
                // Without a claim the turn still ends, so the test fails on its assertion, not a hang.
                await Task.WhenAny(_claimed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                await WriteEventAsync(pipe.Writer, 4, ConversationEventKind.ParticipantMessage, "done", null);
                await WriteEventAsync(pipe.Writer, 5, ConversationEventKind.RunStatus, null, ConversationRunStatus.Completed);
                await pipe.Writer.CompleteAsync();
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
        }

        private async Task WriteEventAsync(PipeWriter writer, long sequence, ConversationEventKind kind, string? text,
            ConversationRunStatus? status)
        {
            var item = new ConversationEvent(Guid.NewGuid(), 1, _conversationId, _attemptId, sequence, kind,
                ConversationParticipant.User, null, text, null, null, null, null, null, status, DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(item, ConversationContractsJsonContext.Default.ConversationEvent);
            await writer.WriteAsync(Encoding.UTF8.GetBytes($"data: {json}\n\n"));
        }

        private ConversationSnapshot Snapshot() => new(_conversationId, null, null, 1, ConversationRunStatus.Completed,
            null, DateTimeOffset.UtcNow, Purpose: ConversationPurpose.MissionConversation, ProjectId: projectId, PinnedLaunch: Launch);

        private static HttpResponseMessage Json<T>(T value) =>
            Raw(JsonSerializer.Serialize(value, typeof(T), ConversationContractsJsonContext.Default));

        private static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
