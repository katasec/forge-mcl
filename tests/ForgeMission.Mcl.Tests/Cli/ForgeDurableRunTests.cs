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
// to the attachment. The CLI is loaded through reflection like the other CLI tests. The output
// pins capture the console, so the class runs in the console collection.
[Collection(ConsoleCollection.Name)]
public sealed class ForgeDurableRunTests : IDisposable
{
    private const string Source = "mission Review(task) = {\n  Researcher\n}\n";
    private const string Expert = "---\nname: Researcher\nkind: llm\ninput: task\noutput: answer\n---\n{{task}}";
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
            [app, projectFile, Path.Combine(_home, "mission.mcl"), "write a file", "ghcr.io/katasec", CancellationToken.None])!;

        Assert.Equal(0, exit);
        var routes = host.Requests;
        Assert.True(routes.IndexOf("/api/AttachMissionHands") is >= 0 and var attach &&
            attach < routes.IndexOf("/api/SubmitMissionTurn"), string.Join(", ", routes));
        Assert.Contains("/api/ClaimMissionHandsWork", routes);
        Assert.Equal(MissionHandsProfile.ProjectWorkspace, host.Launch!.Profile);
    }

    [Fact]
    public async Task Any_mission_runs_and_an_old_missions_key_is_ignored()
    {
        var projectFile = await WriteProjectAsync("Other@1");
        using var host = new RunHost(_projectId);
        await using var app = CreateApp(host);

        var exit = await (Task<int>)RunTurn.Invoke(null,
            [app, projectFile, Path.Combine(_home, "mission.mcl"), "write a file", "ghcr.io/katasec", CancellationToken.None])!;

        Assert.Equal(0, exit);
        Assert.Contains("/api/CreateMissionConversation", host.Requests);
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

    // Every run needs a project file. Deliberate (Phase 76 decision 2b): forge-client's shared open
    // reports it with its one message, after sign-in and package preparation, before any network call.
    [Fact]
    public async Task A_run_without_a_project_file_stops_with_a_clear_message()
    {
        await using var harness = ForgeCliHarness.Start();
        harness.SignIn("https://example.invalid");
        var directory = harness.WorkingDirectory;
        Directory.CreateDirectory(Path.Combine(directory, "experts", "Researcher"));
        await File.WriteAllTextAsync(Path.Combine(directory, "m.mcl"), Source);
        await File.WriteAllTextAsync(Path.Combine(directory, "experts", "Researcher", "expert.md"), Expert);

        var result = await harness.RunAsync("run", "./m.mcl", "hello");

        Assert.Equal((1, ""), (result.ExitCode, result.StandardOutput));
        Assert.Equal("run failed: No forge.project.json found in the current directory. Run `forge project create` to create one.",
            result.StandardError.Trim());
        Assert.Empty(harness.Proxy.Attempts);
    }

    // Output pins (Phase 76 M0): what forge run prints, kept while it moves to the shared client path.
    [Fact]
    public async Task A_reply_is_printed_on_stdout_and_the_conversation_on_stderr()
    {
        using var host = new RunHost(_projectId);
        using var console = new ConsoleCapture();

        var exit = await RunTurnAsync(host);

        Assert.Equal((0, "done\n"), (exit, console.Out));
        Assert.Equal($"conversation: {host.ConversationId}\n", WithoutHandsNotice(console.Error));
    }

    [Fact]
    public async Task A_turn_without_a_reply_prints_only_the_conversation_and_exits_1()
    {
        using var host = new RunHost(_projectId, turnEvents: []);
        using var console = new ConsoleCapture();

        var exit = await RunTurnAsync(host);

        Assert.Equal((1, "", $"conversation: {host.ConversationId}\n"), (exit, console.Out, WithoutHandsNotice(console.Error)));
    }

    [Fact]
    public async Task An_error_event_prints_its_reason_or_else_its_text_on_stderr()
    {
        using var host = new RunHost(_projectId, turnEvents:
        [
            (ConversationEventKind.Error, "error text", "error reason"),
            (ConversationEventKind.Error, "text only", null),
            (ConversationEventKind.ParticipantMessage, "done", null),
        ]);
        using var console = new ConsoleCapture();

        var exit = await RunTurnAsync(host);

        Assert.Equal(0, exit);
        Assert.Equal("done\n", console.Out);
        Assert.Equal($"error: error reason\nerror: text only\nconversation: {host.ConversationId}\n", WithoutHandsNotice(console.Error));
    }

    /// <summary>The controlled Host's claim finds no work, and the attachment reports that on
    /// stderr at a time of its own; the pins compare the rest.</summary>
    private static string WithoutHandsNotice(string error) =>
        string.Join("", error.Split('\n').Where(line => !line.StartsWith("error: file operation", StringComparison.Ordinal))
            .Select(line => line.Length == 0 ? "" : line + "\n"));

    private async Task<int> RunTurnAsync(RunHost host)
    {
        var projectFile = await WriteProjectAsync("Review@1");
        await using var app = CreateApp(host);
        return await (Task<int>)RunTurn.Invoke(null,
            [app, projectFile, Path.Combine(_home, "mission.mcl"), "write a file", "ghcr.io/katasec", CancellationToken.None])!;
    }

    private async Task<string> WriteProjectAsync(string mission)
    {
        Directory.CreateDirectory(Path.Combine(_home, "experts", "Researcher"));
        await File.WriteAllTextAsync(Path.Combine(_home, "mission.mcl"), Source);
        await File.WriteAllTextAsync(Path.Combine(_home, "experts", "Researcher", "expert.md"), Expert);
        await File.WriteAllTextAsync(Path.Combine(_home, "forge.toml"), "[experts]\nResearcher = \"https://invalid.example/Researcher\"\n");
        var projectFile = Path.Combine(_home, "forge.project.json");
        await File.WriteAllTextAsync(projectFile,
            "{\"projectId\":\"" + _projectId + "\",\"missions\":[\"" + mission + "\"],\"folders\":[\".\"]}");
        return projectFile;
    }

    private static IAsyncDisposable CreateApp(IHttpClientFactory host)
    {
        var appType = RunTurn.GetParameters()[0].ParameterType;
        var create = appType.GetMethod("Create")!;
        var publishType = create.GetParameters()[1].ParameterType;
        var publish = typeof(ForgeDurableRunTests).GetMethod(nameof(IgnoreEvents), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(publishType.GetGenericArguments()[0]).CreateDelegate(publishType);
        return (IAsyncDisposable)create.Invoke(null, [host, publish, CancellationToken.None])!;
    }

    private static void IgnoreEvents<T>(T item) { }

    // A controlled Host for one run. The event stream sends a Hands request, then holds the turn
    // open until the attachment claims it, so the claim proves the event reached the attachment.
    // The turn's events (by default one reply, "done") follow, then the run completes.
    private sealed class RunHost(Guid projectId,
        IReadOnlyList<(ConversationEventKind Kind, string? Text, string? Reason)>? turnEvents = null)
        : HttpMessageHandler, IHttpClientFactory
    {
        private readonly IReadOnlyList<(ConversationEventKind Kind, string? Text, string? Reason)> _turnEvents =
            turnEvents ?? [(ConversationEventKind.ParticipantMessage, "done", null)];
        private readonly TaskCompletionSource _claimed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private readonly List<string> _requests = [];
        private Guid _conversationId;
        private Guid _attemptId;

        public DurableMissionLaunch? Launch { get; private set; }

        public Guid ConversationId => _conversationId;

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
                await WriteEventAsync(pipe.Writer, 3, ConversationEventKind.MissionHandsRequested, null, null, null);
                // Without a claim the turn still ends, so the test fails on its assertion, not a hang.
                await Task.WhenAny(_claimed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                var sequence = 4L;
                foreach (var (kind, text, reason) in _turnEvents)
                    await WriteEventAsync(pipe.Writer, sequence++, kind, text, reason, null);
                await WriteEventAsync(pipe.Writer, sequence, ConversationEventKind.RunStatus, null, null, ConversationRunStatus.Completed);
                await pipe.Writer.CompleteAsync();
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
        }

        private async Task WriteEventAsync(PipeWriter writer, long sequence, ConversationEventKind kind, string? text,
            string? reason, ConversationRunStatus? status)
        {
            var item = new ConversationEvent(Guid.NewGuid(), 1, _conversationId, _attemptId, sequence, kind,
                ConversationParticipant.User, null, text, reason, null, null, null, null, status, DateTimeOffset.UtcNow);
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
