using System.Reflection;
using ForgeMission.Tests.Cli.Harness;
using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// ~/.forge/config.json. The theme (53.6): missing file or key → dark (56 G10); "light" and "dark"
// pick those themes — checked on ForgeSettings through reflection like the other CLI tests (the test
// project does not reference the forge assembly). An unknown theme, the file itself and api.endpoint
// are pinned through the process with the shared harness.
public sealed class ForgeConfigTests
{
    private static readonly Type SettingsType = Forge.GetType("ForgeMission.Cli.Services.ForgeSettings", throwOnError: true)!;

    [Fact]
    public void A_missing_theme_is_the_dark_theme()
    {
        Assert.Same(Theme("Dark"), Read(null));
    }

    [Theory]
    [InlineData("light", "Light")]
    [InlineData("dark", "Dark")]
    public void A_named_theme_is_that_theme(string name, string theme)
    {
        Assert.Same(Theme(theme), Read(name));
    }

    [Theory]
    [InlineData("solarized")]
    [InlineData("Dark")]
    public async Task An_unknown_theme_stops_with_the_valid_names(string name)
    {
        await using var harness = StartInProject();
        harness.WriteConfig($$"""{ "theme": "{{name}}" }""");

        var result = await harness.RunAsync("chat");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.Equal($"forge chat: Unknown theme \"{name}\" in {ConfigPath(harness)}. Valid themes: light, dark.",
            result.StandardError.Trim());
    }

    // Pins through the process: a malformed ~/.forge/config.json stops the command with exit 1,
    // nothing on stdout, and an error naming the file.
    [Fact]
    public async Task Invalid_json_stops_with_a_clear_error()
    {
        await using var harness = ForgeCliHarness.Start();
        harness.WriteConfig("{ theme: ");

        var result = await harness.RunAsync("chat");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.Contains(ConfigPath(harness), result.StandardError);
        Assert.Empty(harness.Proxy.Attempts);
    }

    [Fact]
    public async Task A_valid_theme_in_the_config_file_lets_chat_continue_to_sign_in()
    {
        await using var harness = StartInProject();
        harness.WriteConfig("""{ "theme": "light" }""");

        var result = await harness.RunAsync("chat");

        Assert.Equal((1, "", "Not signed in. Run `forge login`."),
            (result.ExitCode, result.StandardOutput, result.StandardError.Trim()));
    }

    // api.endpoint is trimmed of whitespace and trailing slashes; blank means the hosted default.
    // The theme is forge chat's alone: an unknown one does not stop a hosted run.
    [Fact]
    public async Task The_configured_api_endpoint_is_where_a_hosted_run_is_sent()
    {
        await using var harness = ForgeCliHarness.Start();
        var stub = await harness.StartStubAsync(_ => new StubResponse(500, "boom"));
        harness.SignIn(stub.Address.ToString());
        harness.WriteConfig($$"""{ "theme": "solarized", "api": { "endpoint": "  {{stub.Address}}/ " } }""");

        var result = await harness.RunAsync("run", "Noop", "hello");

        Assert.Equal((1, "", "exec failed (500): boom"),
            (result.ExitCode, result.StandardOutput, result.StandardError.Trim()));
        Assert.Equal(("POST", "/api/ExecuteMission"), (Assert.Single(stub.Requests).Method, stub.Requests[0].PathAndQuery));
    }

    [Fact]
    public async Task A_blank_api_endpoint_is_the_hosted_default()
    {
        await using var harness = ForgeCliHarness.Start();
        harness.SignIn("https://example.invalid");
        harness.WriteConfig("""{ "api": { "endpoint": " " } }""");

        var result = await harness.RunAsync("run", "Noop", "hello");

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("Could not reach https://api.forge.katasec.com: ", result.StandardError);
        Assert.Contains(new ProxyAttempt("CONNECT", "api.forge.katasec.com:443"), harness.Proxy.Attempts);
    }

    // Every hosted call of forge exec — input upload, the run, artifact download — goes to the
    // configured endpoint; a URL input is fetched from its own host first.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_exec_call_goes_to_the_configured_api_endpoint(bool urlInput)
    {
        await using var harness = ForgeCliHarness.Start();
        var stub = await harness.StartStubAsync(request => request.PathAndQuery switch
        {
            "/api/UploadArtifact" => new StubResponse(200, """{"artifact":{"id":"in-1","name":"in.txt"}}"""),
            "/api/ExecuteMission" => new StubResponse(200,
                """{"type":"result","result":{"answer":"done","artifacts":[{"id":"out-1","name":"out.txt"}]}}"""),
            "/in.txt" => new StubResponse(200, "source", "text/plain"),
            _ => new StubResponse(200, "converted", "application/octet-stream"),
        });
        harness.SignIn("https://example.invalid");
        harness.WriteConfig($$"""{ "api": { "endpoint": "{{stub.Address}}" } }""");
        File.WriteAllText(Path.Combine(harness.WorkingDirectory, "in.txt"), "source");
        var input = urlInput ? new Uri(stub.Address, "in.txt").ToString() : "in.txt";

        var result = await harness.RunAsync("exec", "Noop", "--input", input, "--out", "out.txt");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Created: out.txt", result.StandardOutput);
        string[] calls = ["/api/UploadArtifact", "/api/ExecuteMission", "/api/GetArtifact"];
        Assert.Equal(urlInput ? ["/in.txt", .. calls] : calls, stub.Requests.Select(r => r.PathAndQuery));
        Assert.Equal("converted", File.ReadAllText(Path.Combine(harness.WorkingDirectory, "out.txt")));
    }

    // forge project create and forge chat reach the hosted conversations at the configured endpoint.
    [Theory]
    [InlineData("/api/CreateMissionConversation", "project", "create")]
    [InlineData("/api/ListMissionConversations", "chat")]
    public async Task Conversation_commands_go_to_the_configured_api_endpoint(string firstCall, params string[] args)
    {
        await using var harness = ForgeCliHarness.Start();
        var stub = await harness.StartStubAsync(_ => new StubResponse(500, "boom"));
        harness.SignIn("https://example.invalid");
        harness.WriteConfig($$"""{ "api": { "endpoint": "{{stub.Address}}" } }""");
        File.WriteAllText(Path.Combine(harness.WorkingDirectory, "forge.project.json"),
            "{\"projectId\":\"" + Guid.NewGuid() + "\",\"missions\":[\"Chat@1\"],\"folders\":[]}");

        var result = await harness.RunAsync(args);

        Assert.Equal(1, result.ExitCode);
        var request = Assert.Single(stub.Requests);
        Assert.Equal(("POST", firstCall, "Bearer fg_test_key"), (request.Method, request.PathAndQuery, request.Headers["Authorization"]));
        Assert.Empty(harness.Proxy.Attempts);
    }

    private static ForgeCliHarness StartInProject()
    {
        var harness = ForgeCliHarness.Start();
        File.WriteAllText(Path.Combine(harness.WorkingDirectory, "forge.project.json"), "{}");
        return harness;
    }

    private static string ConfigPath(ForgeCliHarness harness) => Path.Combine(harness.Home, ".forge", "config.json");

    // The theme a bound ForgeSettings selects when config.json names <paramref name="theme"/>.
    private static object Read(string? theme) =>
        SettingsType.GetMethod("SelectedTheme")!.Invoke(Settings(theme), null)!;

    private static object Settings(string? theme)
    {
        var settings = Activator.CreateInstance(SettingsType)!;
        SettingsType.GetProperty("Theme")!.SetValue(settings, theme);
        return settings;
    }
}
