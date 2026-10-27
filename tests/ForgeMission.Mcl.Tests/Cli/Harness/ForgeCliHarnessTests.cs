namespace ForgeMission.Tests.Cli.Harness;

// Proves the shared harness: a public host only ever reaches the non-forwarding proxy, a loopback
// stub is reached directly, and the child never inherits operator credentials or a real browser. `forge whoami`
// is the probe: it GETs {endpoint}/me with the CLI's default HttpClient (default proxy from env).
public sealed class ForgeCliHarnessTests
{
    [Theory]
    [InlineData("http://example.invalid", "GET", "http://example.invalid/me")]
    [InlineData("https://example.invalid", "CONNECT", "example.invalid:443")]
    public async Task A_public_host_reaches_only_the_non_forwarding_proxy(string endpoint, string method, string target)
    {
        await using var harness = ForgeCliHarness.Start();
        harness.SignIn(endpoint);

        var result = await harness.RunAsync("whoami");

        Assert.Contains(new ProxyAttempt(method, target), harness.Proxy.Attempts);
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Signed in as", result.StandardOutput);
    }

    [Fact]
    public async Task A_loopback_stub_is_reached_directly_without_the_proxy()
    {
        await using var harness = ForgeCliHarness.Start();
        var stub = await harness.StartStubAsync(_ =>
            new StubResponse(200, """{"email":"harness@test","balanceMicroUsd":0}"""));
        harness.SignIn(stub.Address.ToString());

        var result = await harness.RunAsync("whoami");

        Assert.Equal((0, $"Signed in as harness@test{Environment.NewLine}  $0.00 credit · {stub.Address}{Environment.NewLine}"),
            (result.ExitCode, result.StandardOutput));
        var request = Assert.Single(stub.Requests);
        Assert.Equal(("GET", "/me", "Bearer fg_test_key"), (request.Method, request.PathAndQuery, request.Headers["Authorization"]));
        Assert.Empty(harness.Proxy.Attempts);
    }

    [Fact]
    public async Task The_child_environment_drops_operator_credentials_and_keeps_explicit_settings()
    {
        await using var harness = ForgeCliHarness.Start();
        harness.Variables["FORGE_API_ENDPOINT"] = "set-by-test";

        var environment = harness.BuildStartInfo(["--version"]).Environment;

        string[] scrubbed = ["OPENAI_API_KEY", "ANTHROPIC_API_KEY", "XAI_API_KEY", "GROK_API_KEY",
            "GITHUB_TOKEN", "GH_TOKEN", "AZURE_OPENAI_API_KEY"];
        Assert.All(scrubbed, name => Assert.False(environment.ContainsKey(name), name));
        Assert.Equal(["FORGE_API_ENDPOINT"], environment.Keys.Where(k => k.StartsWith("FORGE_", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("set-by-test", environment["FORGE_API_ENDPOINT"]);
        Assert.Equal(harness.Home, environment["HOME"]);
        Assert.Equal(harness.Proxy.Address.ToString(), environment["https_proxy"]);
        Assert.Equal(Path.Combine(harness.Home, "no-op-browser"), environment["PATH"]!.Split(Path.PathSeparator)[0]);
    }
}
