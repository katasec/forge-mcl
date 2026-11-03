using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
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
    // The theme is forge chat's alone: an unknown one does not stop a hosted command.
    [Fact]
    public async Task The_configured_api_endpoint_is_where_a_hosted_command_is_sent()
    {
        await using var harness = ForgeCliHarness.Start();
        var stub = await harness.StartStubAsync(_ => new StubResponse(500, "boom"));
        harness.SignIn("https://example.invalid");
        harness.WriteConfig($$"""{ "theme": "solarized", "api": { "endpoint": "  {{stub.Address}}/ " } }""");

        var result = await harness.RunAsync("project", "create");

        Assert.Equal((1, ""), (result.ExitCode, result.StandardOutput));
        Assert.Equal(("POST", "/api/CreateMissionConversation"), (Assert.Single(stub.Requests).Method, stub.Requests[0].PathAndQuery));
        Assert.Empty(harness.Proxy.Attempts);
    }

    [Fact]
    public async Task A_blank_api_endpoint_is_the_hosted_default()
    {
        await using var harness = ForgeCliHarness.Start();
        harness.SignIn("https://example.invalid");
        harness.WriteConfig("""{ "api": { "endpoint": " " } }""");

        var result = await harness.RunAsync("project", "create");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(new ProxyAttempt("CONNECT", "api.forge.katasec.com:443"), harness.Proxy.Attempts);
    }

    // forge project create and forge chat reach the hosted conversations at the configured endpoint.
    [Theory]
    [InlineData("/api/CreateMissionConversation", "project", "create")]
    [InlineData("/api/GetConversation", "chat")]
    public async Task Conversation_commands_go_to_the_configured_api_endpoint(string firstCall, params string[] args)
    {
        await using var harness = ForgeCliHarness.Start();
        var stub = await harness.StartStubAsync(_ => new StubResponse(500, "boom"));
        harness.SignIn("https://example.invalid");
        harness.WriteConfig($$"""{ "api": { "endpoint": "{{stub.Address}}" } }""");
        File.WriteAllText(Path.Combine(harness.WorkingDirectory, "forge.project.json"),
            "{\"projectId\":\"" + Guid.NewGuid() + "\",\"missions\":[\"Chat@1\"],\"folders\":[\".\"]}");

        var result = await harness.RunAsync(args);

        Assert.Equal(1, result.ExitCode);
        var request = Assert.Single(stub.Requests);
        Assert.Equal(("POST", firstCall, "Bearer fg_test_key"), (request.Method, request.PathAndQuery, request.Headers["Authorization"]));
        Assert.Empty(harness.Proxy.Attempts);
    }

    // oci.endpoint is the base a short mission selector resolves against; absent or blank means
    // ghcr.io/katasec. The pull is the run's first network call, seen as a CONNECT to the registry.
    [Theory]
    [InlineData("""{ "oci": { "endpoint": " registry.example.com/team " } }""", "registry.example.com:443")]
    [InlineData("{}", "ghcr.io:443")]
    [InlineData("""{ "oci": { "endpoint": " " } }""", "ghcr.io:443")]
    public async Task A_short_selector_is_pulled_from_the_configured_oci_endpoint(string config, string registry)
    {
        await using var harness = ForgeCliHarness.Start();
        harness.SignIn("https://example.invalid");
        harness.WriteConfig(config);

        var result = await harness.RunAsync("run", "ocr", "hello");

        Assert.Equal((1, ""), (result.ExitCode, result.StandardOutput));
        Assert.Equal(new ProxyAttempt("CONNECT", registry), harness.Proxy.Attempts[0]);
    }

    // Decision 8: on first use forge writes its defaults to ~/.forge/config.json and keeps what is
    // already there. Every command gets this; --help is the cheapest one that runs no network call.
    [Fact]
    public async Task A_missing_config_file_is_written_with_the_defaults()
    {
        await using var harness = ForgeCliHarness.Start();

        var result = await harness.RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        AssertConfig(harness, """{ "api": { "endpoint": "https://api.forge.katasec.com" }, "oci": { "endpoint": "ghcr.io/katasec" } }""");
    }

    // A present value is never overwritten, even blank; a section in another case is the same
    // section; a section that is not an object is left alone. Only a missing endpoint is added.
    [Theory]
    [InlineData("""{ "theme": "light", "api": { "endpoint": "https://custom.example" } }""",
        """{ "theme": "light", "api": { "endpoint": "https://custom.example" }, "oci": { "endpoint": "ghcr.io/katasec" } }""")]
    [InlineData("""{ "Api": { "Endpoint": "https://custom.example" } }""",
        """{ "Api": { "Endpoint": "https://custom.example" }, "oci": { "endpoint": "ghcr.io/katasec" } }""")]
    [InlineData("""{ "api": { "endpoint": " " }, "oci": {} }""",
        """{ "api": { "endpoint": " " }, "oci": { "endpoint": "ghcr.io/katasec" } }""")]
    [InlineData("""{ "api": "text" }""",
        """{ "api": "text", "oci": { "endpoint": "ghcr.io/katasec" } }""")]
    public async Task An_existing_config_file_keeps_its_settings_and_gains_the_missing_defaults(string before, string after)
    {
        await using var harness = ForgeCliHarness.Start();
        harness.WriteConfig(before);

        await harness.RunAsync("--help");

        AssertConfig(harness, after);
    }

    // Nothing to add, or nothing that parses: the file is not rewritten.
    [Theory]
    [InlineData("""{"api":{"endpoint":"a"},"oci":{"endpoint":"b"}}""")]
    [InlineData("{ theme: ")]
    public async Task A_complete_or_malformed_config_file_is_left_byte_for_byte(string contents)
    {
        await using var harness = ForgeCliHarness.Start();
        harness.WriteConfig(contents);

        await harness.RunAsync("--help");

        Assert.Equal(contents, File.ReadAllText(ConfigPath(harness)));
    }

    // forge needs its config file: when it cannot be written, forge stops before any command runs.
    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unwritable_config_file_stops_forge_at_startup()
    {
        Skip.If(OperatingSystem.IsWindows(), "The read-only directory is POSIX file modes.");
        await using var harness = ForgeCliHarness.Start();
        var directory = Path.Combine(harness.Home, ".forge");
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await harness.RunAsync("project", "create");

            Assert.Equal((1, ""), (result.ExitCode, result.StandardOutput));
            Assert.StartsWith($"error: Cannot update {ConfigPath(harness)}: ", result.StandardError.Trim());
            Assert.Single(result.StandardError.Trim().Split('\n'));
            Assert.Empty(harness.Proxy.Attempts);
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void AssertConfig(ForgeCliHarness harness, string expected)
    {
        var actual = JsonNode.Parse(File.ReadAllText(ConfigPath(harness)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual?.ToJsonString());
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
