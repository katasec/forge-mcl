using System.Diagnostics;
using System.Reflection;
using ForgeMission.Application.Transport;
using ForgeMission.Core.Resolution;

namespace ForgeMission.Tests.Cli;

public sealed class ForgeProjectTests
{
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
        Assert.Contains(explicitFolder ? $"Run `forge chat --project '{expectedHome}'`." : "Run `forge chat`.", output.ToString());
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
        var quotedHome = Path.Combine(Directory.GetCurrentDirectory(), "project $value''s `folder");
        Assert.Contains($"Run `forge chat --project '{quotedHome}'`.", output.ToString());
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
