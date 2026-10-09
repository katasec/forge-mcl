using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json;
using ForgeMission.Application.Transport;
using ForgeMission.Core.Resolution;
using ForgeMission.Core.Tools;
using ForgeMission.Conversations.Contracts;

namespace ForgeMission.Tests.Cli;

public sealed class ForgeProjectTests
{
    [Fact]
    public async Task Published_client_creates_opens_and_reconnects_chat_against_current_core()
    {
        var root = Directory.CreateTempSubdirectory("forge-published-client-").FullName;
        using var host = new ChatProjectHost();
        try
        {
            var application = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(RunCore.DeclaringType!.Assembly.Location)!, "ForgeMission.Application.dll"));
            Assert.Equal(new Version(0, 9, 3, 0), application.GetName().Version);
            var composition = application.GetType("ForgeMission.Application.ApplicationComposition", true)!;
            await using var app = (IAsyncDisposable)composition.GetMethod("Create")!.Invoke(null,
                [host, null, new CapabilityAuthorizationPolicy([], null), (Action<ApplicationEvent>)(_ => { }), CancellationToken.None])!;
            var conversations = composition.GetProperty("MissionConversations")!.GetValue(app)!;
            var created = await ClientAction<CreateChatProjectResponse>(conversations, "CreateChatProjectAsync", new CreateChatProjectRequest(root));
            Assert.Null(created.Error);
            Assert.NotNull(created.Created);
            var projects = composition.GetProperty("Projects")!.GetValue(app)!;
            var opened = await ClientAction<ProjectOperationResponse>(projects, "OpenChatAsync", new ProjectOpenRequest(root, "Chat"));
            Assert.Equal(ProjectOperationOutcome.Opened, opened.Outcome);
            Assert.Empty(opened.Session!.AvailableCapabilities);
            var connected = await ClientAction<ReconnectMissionConversationResponse>(conversations, "ReconnectAsync",
                new ReconnectMissionConversationRequest(opened.Session.SessionId, "Chat"));
            Assert.Null(connected.Error);
            Assert.Equal(created.Created.ConversationId, connected.Conversation!.ConversationId);
            Assert.Equal(["/api/CreateMissionConversation", "/api/ListMissionConversations", "/api/GetConversation"], host.Requests);
            Assert.Equal(["forge.project.json"], Directory.GetFiles(root).Select(Path.GetFileName));
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally
        {
            Directory.Delete(root, true);
            if (host.Created is { } created)
            {
                var projection = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge", "sessions",
                    created.ProjectId.ToString("N"), ConversationDeterministicIds.MissionConversation(created.CommandId).ToString("N"));
                if (Directory.Exists(projection)) Directory.Delete(projection, true);
            }
        }
    }

    private static Task<T> ClientAction<T>(object owner, string method, object request) =>
        (Task<T>)owner.GetType().GetMethod(method)!.Invoke(owner, [request, CancellationToken.None])!;

    private sealed class ChatProjectHost : HttpMessageHandler, IHttpClientFactory
    {
        public ForgeMission.Conversations.Contracts.CreateMissionConversationRequest? Created { get; private set; }
        public List<string> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://controlled-host.invalid/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var route = request.RequestUri!.AbsolutePath;
            Requests.Add(route);
            if (route == "/api/CreateMissionConversation")
                Created = JsonSerializer.Deserialize(await request.Content!.ReadAsStringAsync(ct), ConversationContractsJsonContext.Default.CreateMissionConversationRequest)!;
            var created = Created ?? throw new InvalidOperationException("Creation must precede chat startup.");
            var conversationId = ConversationDeterministicIds.MissionConversation(created.CommandId);
            var snapshot = new ConversationSnapshot(conversationId, null, null, 0, ConversationRunStatus.Completed,
                null, DateTimeOffset.UtcNow, Purpose: ConversationPurpose.MissionConversation, ProjectId: created.ProjectId, PinnedLaunch: created.Launch);
            var json = route switch
            {
                "/api/CreateMissionConversation" => JsonSerializer.Serialize(new ForgeMission.Conversations.Contracts.CreateMissionConversationResponse(conversationId, 1, created.Launch),
                    ConversationContractsJsonContext.Default.CreateMissionConversationResponse),
                "/api/ListMissionConversations" => JsonSerializer.Serialize(new ForgeMission.Conversations.Contracts.ListMissionConversationsResponse(
                    [new MissionConversationSummary(conversationId, created.ProjectId, created.Launch, snapshot.Status, 0, snapshot.UpdatedAtUtc)]),
                    ConversationContractsJsonContext.Default.ListMissionConversationsResponse),
                "/api/GetConversation" => JsonSerializer.Serialize(new GetConversationResponse(snapshot), ConversationContractsJsonContext.Default.GetConversationResponse),
                _ => throw new InvalidOperationException($"Unexpected ABI regression request: {route}"),
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly MethodInfo RunCore = LoadRunCore();
    private static readonly PlatformCredential SignedIn = new() { Key = "controlled-platform-key" };

    [Theory]
    [InlineData("root")]
    [InlineData("project")]
    [InlineData("create")]
    public async Task Command_help_exposes_project_creation(string level)
    {
        var args = level switch
        {
            "root" => new[] { "--help" },
            "project" => new[] { "project", "--help" },
            _ => new[] { "project", "create", "--help" },
        };
        var result = await InvokeAsync(args);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(level == "root" ? "project" : level == "project" ? "create" : "folder", result.Output);
        Assert.Equal("", result.Error);
    }

    [Fact]
    public async Task Extra_folder_is_rejected_before_the_action()
    {
        var result = await InvokeAsync(["project", "create", "first-folder", "second-folder"]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("second-folder", result.Error);
        Assert.DoesNotContain("Not signed in", result.Error);
        Assert.DoesNotContain("Project:", result.Output);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Missing_login_stops_before_shared_creation(string? key)
    {
        var calls = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var platform = key is null ? null : new PlatformCredential { Key = key };
        var result = await RunAsync(null, platform, (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Must not create without login.");
        }, output, error);
        Assert.Equal(1, result);
        Assert.Equal(0, calls);
        Assert.Equal("", output.ToString());
        Assert.Equal("Not signed in. Run `forge login`.", error.ToString().Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Success_forwards_home_and_login_once_and_prints_the_next_command(bool explicitFolder)
    {
        var folder = explicitFolder ? "project folder" : null;
        var expectedHome = Path.GetFullPath(folder ?? Directory.GetCurrentDirectory());
        var calls = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await RunAsync(folder, SignedIn, (request, platform) =>
        {
            calls++;
            Assert.Equal(expectedHome, request.HomePath);
            Assert.Same(SignedIn, platform);
            return Task.FromResult(new CreateChatProjectResponse(
                new CreatedChatProject(Guid.NewGuid(), Guid.NewGuid(), request.HomePath), null));
        }, output, error);
        Assert.Equal(0, result);
        Assert.Equal(1, calls);
        Assert.Equal("", error.ToString());
        Assert.Contains($"Project: {Path.Combine(expectedHome, "forge.project.json")}", output.ToString());
        Assert.Contains(explicitFolder ? $"Run `forge chat --project '{Path.Combine(expectedHome, "forge.project.json")}'`." : "Run `forge chat`.", output.ToString());
    }

    [Fact]
    public async Task Explicit_folder_guidance_preserves_powershell_metacharacters_and_apostrophes()
    {
        const string folder = "project $value's `folder";
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await RunAsync(folder, SignedIn, (request, _) => Task.FromResult(
            new CreateChatProjectResponse(new CreatedChatProject(Guid.NewGuid(), Guid.NewGuid(), request.HomePath), null)), output, error);
        Assert.Equal(0, result);
        Assert.Equal("", error.ToString());
        var quotedFile = Path.Combine(Directory.GetCurrentDirectory(), "project $value''s `folder", "forge.project.json");
        Assert.Contains($"Run `forge chat --project '{quotedFile}'`.", output.ToString());
    }

    [Theory]
    [InlineData(ProjectOperationErrorCode.InvalidManifest, "This folder already contains another Project.")]
    [InlineData(ProjectOperationErrorCode.SubmissionUncertain, "The project file remains. Rerun the same create safely.")]
    public async Task Owner_failure_is_reported_verbatim_without_success(ProjectOperationErrorCode code, string message)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await RunAsync(null, SignedIn, (_, _) => Task.FromResult(
            new CreateChatProjectResponse(null, new ProjectOperationError(code, message))), output, error);
        Assert.Equal(1, result);
        Assert.Equal("", output.ToString());
        Assert.Equal(message, error.ToString().Trim());
    }

    private static Task<int> RunAsync(string? folder, PlatformCredential? platform,
        Func<CreateChatProjectRequest, PlatformCredential, Task<CreateChatProjectResponse>> create,
        TextWriter output, TextWriter error) =>
        (Task<int>)RunCore.Invoke(null, [folder, platform, create, output, error])!;

    private static async Task<(int ExitCode, string Output, string Error)> InvokeAsync(string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(RunCore.DeclaringType!.Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // Controlled parser-only check; no remote endpoint is used.
        start.Environment["FORGE_API_ENDPOINT"] = "invalid-endpoint";
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await output, await error);
    }

    private static MethodInfo LoadRunCore()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "ForgeMission.Cli", "bin", configuration, "net10.0", "forge.dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path).GetType("ForgeMission.Cli.ForgeProject", throwOnError: true)!
                .GetMethod("RunCoreAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        }
        throw new FileNotFoundException("Could not locate built forge.dll for CLI tests.");
    }
}
