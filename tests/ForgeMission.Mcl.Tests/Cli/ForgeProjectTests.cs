using System.Reflection;
using System.Text.Json;
using ForgeMission.Application.Transport;
using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

public sealed class ForgeProjectTests
{
    // Phase 79 (D2): project create only writes forge.project.json and calls no server.
    [Fact]
    public async Task Published_client_writes_only_the_declaration()
    {
        var root = Directory.CreateTempSubdirectory("forge-published-client-").FullName;
        using var host = new NoServer();
        try
        {
            var application = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(RunCore.DeclaringType!.Assembly.Location)!, "ForgeMission.Application.dll"));
            Assert.Equal(new Version(0, 17, 0, 0), application.GetName().Version);
            var composition = application.GetType("ForgeMission.Application.ApplicationComposition", true)!;
            await using var app = (IAsyncDisposable)composition.GetMethod("Create")!.Invoke(null, [host, CancellationToken.None])!;
            var conversations = composition.GetProperty("MissionConversations")!.GetValue(app)!;
            var created = await ClientAction<CreateProjectResponse>(conversations, "CreateProjectAsync",
                new CreateProjectRequest(root), CancellationToken.None);
            Assert.Null(created.Error);
            var projectFile = Path.Combine(root, "forge.project.json");
            using (var declaration = JsonDocument.Parse(await File.ReadAllTextAsync(projectFile)))
            {
                Assert.Equal(["."], declaration.RootElement.GetProperty("folders").EnumerateArray().Select(folder => folder.GetString()));
                Assert.Equal(created.Created!.ProjectId, declaration.RootElement.GetProperty("projectId").GetGuid());
            }
            Assert.Equal(["forge.project.json"], Directory.GetFiles(root).Select(Path.GetFileName));
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<T> ClientAction<T>(object owner, string method, params object[] arguments) =>
        (T)await ClientResult(owner, method, arguments);

    /// <summary>Calls a client action and returns its task's result (the result type may be one the
    /// tests do not reference).</summary>
    private static async Task<object> ClientResult(object owner, string method, params object[] arguments)
    {
        var task = (Task)owner.GetType().GetMethod(method)!.Invoke(owner, arguments)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private sealed class NoServer : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://controlled-host.invalid/") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException($"project create must not call {request.RequestUri}.");
    }

    private static readonly MethodInfo RunCore = LoadRunCore();

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
        Assert.DoesNotContain("Project:", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Success_forwards_home_once_and_prints_the_next_command(bool explicitFolder)
    {
        var folder = explicitFolder ? "project folder" : null;
        var expectedHome = Path.GetFullPath(folder ?? Directory.GetCurrentDirectory());
        var calls = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await RunAsync(folder, request =>
        {
            calls++;
            Assert.Equal(expectedHome, request.HomePath);
            return Task.FromResult(new CreateProjectResponse(new CreatedProject(Guid.NewGuid(), request.HomePath), null));
        }, output, error);
        Assert.Equal(0, result);
        Assert.Equal(1, calls);
        Assert.Equal("", error.ToString());
        Assert.Contains($"Project: {Path.Combine(expectedHome, "forge.project.json")}", output.ToString());
        Assert.Contains(explicitFolder ? $"Run `forge run --project '{Path.Combine(expectedHome, "forge.project.json")}' <mission> \"<prompt>\"`." : "Run `forge run <mission> \"<prompt>\"`.", output.ToString());
    }

    [Fact]
    public async Task Explicit_folder_guidance_preserves_powershell_metacharacters_and_apostrophes()
    {
        const string folder = "project $value's `folder";
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await RunAsync(folder, request => Task.FromResult(
            new CreateProjectResponse(new CreatedProject(Guid.NewGuid(), request.HomePath), null)), output, error);
        Assert.Equal(0, result);
        Assert.Equal("", error.ToString());
        var quotedFile = Path.Combine(Directory.GetCurrentDirectory(), "project $value''s `folder", "forge.project.json");
        Assert.Contains($"Run `forge run --project '{quotedFile}' <mission> \"<prompt>\"`.", output.ToString());
    }

    [Theory]
    [InlineData(ProjectOperationErrorCode.InvalidManifest, "This folder already contains another Project.")]
    [InlineData(ProjectOperationErrorCode.ManifestWriteFailed, "Could not update forge.project.json.")]
    public async Task Owner_failure_is_reported_verbatim_without_success(ProjectOperationErrorCode code, string message)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await RunAsync(null, _ => Task.FromResult(
            new CreateProjectResponse(null, new ProjectOperationError(code, message))), output, error);
        Assert.Equal(1, result);
        Assert.Equal("", output.ToString());
        Assert.Equal(message, error.ToString().Trim());
    }

    private static Task<int> RunAsync(string? folder, Func<CreateProjectRequest, Task<CreateProjectResponse>> create,
        TextWriter output, TextWriter error) =>
        (Task<int>)RunCore.Invoke(null, [folder, create, output, error])!;

    private static async Task<(int ExitCode, string Output, string Error)> InvokeAsync(string[] args)
    {
        await using var harness = ForgeCliHarness.Start();
        // Controlled parser-only check; no remote endpoint is used.
        harness.Variables["FORGE_API_ENDPOINT"] = "invalid-endpoint";
        var result = await harness.RunAsync(args);
        return (result.ExitCode, result.StandardOutput, result.StandardError);
    }

    private static MethodInfo LoadRunCore() =>
        Assembly.LoadFrom(ForgeCliHarness.ForgeDllPath).GetType("ForgeMission.Cli.Commands.ForgeProject", throwOnError: true)!
            .GetMethod("RunCoreAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
}
