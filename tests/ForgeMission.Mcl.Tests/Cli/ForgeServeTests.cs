using System.Net;
using System.Net.Sockets;
using System.Text;
using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge serve's optional aux (housekeeping) client: when its provider can't be built, serving still
// starts on a null aux client (the server answers housekeeping with its canned replies), stdout
// stays empty, and the host logger reports the failure as a Warning on stderr. The mission runners
// use forge.toml's [providers.default]; the aux client layers the mission's own provider
// (MCL_PROVIDER) over it, so an unknown one fails only the aux client.
public sealed class ForgeServeTests
{
    private static readonly string NoopMission = Path.Combine(AppContext.BaseDirectory, "Missions", "noop");

    [Fact]
    public async Task Serve_starts_without_the_aux_client_when_its_provider_cannot_be_built()
    {
        await using var harness = ForgeCliHarness.Start();
        var port = FreePort();
        harness.WorkingDirectory = WriteAgent(harness.Home, port);
        harness.Variables["MCL_PROVIDER"] = "unknown-provider";
        var healthy = false;

        var result = await harness.RunWhileAsync(async () => healthy = await WaitUntilHealthyAsync(port), "serve");

        Assert.True(healthy);
        Assert.Equal("", result.StandardOutput);
        Assert.Contains($"forge serve — agent 'pin' listening on http://0.0.0.0:{port}", result.StandardError);
        Assert.Contains("warn: Program", result.StandardError);
        Assert.Contains("The aux chat client for provider 'unknown-provider' could not be built; housekeeping requests get canned replies.",
            result.StandardError);
        Assert.Contains("Unknown provider 'unknown-provider'", result.StandardError);
        Assert.Empty(harness.Proxy.Attempts);
    }

    // Ctrl+C stops a serving command cleanly: exit 0, its banner on stderr, nothing on stdout.
    [SkippableTheory]
    [InlineData("forge serve — agent 'pin' listening on http://0.0.0.0:{port}", "serve")]
    [InlineData("✓ endpoint  /v1/messages    http://127.0.0.1:{port}", "connect", "vscode", "mission.mcl", "--port")]
    public async Task Ctrl_C_stops_a_serving_command_with_exit_0(string banner, params string[] args)
    {
        Skip.If(OperatingSystem.IsWindows(), "SIGINT is POSIX.");
        await using var harness = ForgeCliHarness.Start();
        var port = FreePort();
        harness.WorkingDirectory = WriteAgent(harness.Home, port);
        string[] command = args[^1] == "--port" ? [.. args, port.ToString()] : args;

        var result = await harness.InterruptWhileAsync(async _ => Assert.True(await WaitUntilHealthyAsync(port)), command);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.Contains(banner.Replace("{port}", port.ToString()), result.StandardError);
    }

    // A serving command whose port is taken reports it on stderr and exits 1.
    [Theory]
    [InlineData("Port {port} is already in use. Stop the existing process or change the port in agent.yaml.", "serve")]
    [InlineData("Port {port} is already in use. Pass a different --port (and rerun so the settings match).", "connect", "vscode", "mission.mcl", "--port")]
    public async Task A_taken_port_is_reported_and_exits_1(string error, params string[] args)
    {
        await using var harness = ForgeCliHarness.Start();
        using var taken = new TcpListener(IPAddress.Any, 0);
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;
        harness.WorkingDirectory = WriteAgent(harness.Home, port);
        string[] command = args[^1] == "--port" ? [.. args, port.ToString()] : args;

        var result = await harness.RunAsync(command);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.Contains(error.Replace("{port}", port.ToString()), result.StandardError);
    }

    // forge connect vscode writes .vscode/settings.json before it serves: it creates the file when
    // absent and merges its key into a comment-free one. A taken port makes the run exit right after.
    [Theory]
    [InlineData(null, """
        {
          "claudeCode.environmentVariables": {
            "ANTHROPIC_BASE_URL": "http://127.0.0.1:{port}",
            "ANTHROPIC_API_KEY": "forge-local"
          }
        }
        """)]
    [InlineData("""{ "editor.tabSize": 4, "claudeCode.environmentVariables": { "OLD": "x" }, "files.eol": "\n", }""", """
        {
          "editor.tabSize": 4,
          "claudeCode.environmentVariables": {
            "ANTHROPIC_BASE_URL": "http://127.0.0.1:{port}",
            "ANTHROPIC_API_KEY": "forge-local"
          },
          "files.eol": "\n"
        }
        """)]
    public async Task Connect_vscode_writes_the_extension_settings(string? existing, string expected)
    {
        await using var harness = ForgeCliHarness.Start();
        using var taken = new TcpListener(IPAddress.Any, 0);
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;
        harness.WorkingDirectory = WriteAgent(harness.Home, port);
        var settingsDir = Path.Combine(harness.WorkingDirectory, ".vscode");
        if (existing is not null)
        {
            Directory.CreateDirectory(settingsDir);
            File.WriteAllText(Path.Combine(settingsDir, "settings.json"), existing);
        }

        var result = await harness.RunAsync("connect", "vscode", "mission.mcl", "--port", port.ToString());

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(expected.Replace("{port}", port.ToString()).ReplaceLineEndings()),
            File.ReadAllBytes(Path.Combine(settingsDir, "settings.json")));
        Assert.Equal(["settings.json"], Directory.GetFiles(settingsDir).Select(Path.GetFileName));
    }

    // forge claude --print-env waits for its in-process endpoint to answer, then prints the wiring on
    // stdout and keeps serving until Ctrl+C, which stops it cleanly with exit 0. Needs no claude CLI.
    [SkippableFact]
    public async Task Claude_print_env_prints_the_wiring_once_the_endpoint_is_healthy()
    {
        Skip.If(OperatingSystem.IsWindows(), "SIGINT is POSIX.");
        await using var harness = ForgeCliHarness.Start();
        var port = FreePort();
        harness.WorkingDirectory = WriteAgent(harness.Home, port);
        var printed = new List<string>();

        var result = await harness.InterruptWhileAsync(async stdout =>
        {
            printed.Add((await stdout.ReadLineAsync())!);
            printed.Add((await stdout.ReadLineAsync())!);
        }, "claude", "--print-env", "--port", port.ToString());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"export ANTHROPIC_BASE_URL=http://127.0.0.1:{port}", "export ANTHROPIC_API_KEY=forge-local"], printed);
        Assert.Equal("", result.StandardOutput);
        Assert.Contains($"✓ endpoint  /v1/messages    http://127.0.0.1:{port}   (in-process)", result.StandardError);
        Assert.DoesNotContain("did not become healthy", result.StandardError);
    }

    private static string WriteAgent(string home, int port)
    {
        var dir = Path.Combine(home, "agent");
        Directory.CreateDirectory(Path.Combine(dir, "experts", "PassThrough"));
        foreach (var file in new[] { "mission.mcl", "mcl.lock", Path.Combine("experts", "PassThrough", "expert.md") })
            File.Copy(Path.Combine(NoopMission, file), Path.Combine(dir, file));
        File.WriteAllText(Path.Combine(dir, "forge.toml"), """
            [providers.default]
            provider = "openai"
            model = "gpt-4o-mini"
            apiKey = "stub"
            """);
        File.WriteAllText(Path.Combine(dir, "agent.yaml"), $"mission: mission.mcl\nport: {port}\nid: pin\n");
        return dir;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task<bool> WaitUntilHealthyAsync(int port)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"http://127.0.0.1:{port}/"));
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { } // not listening yet
            await Task.Delay(200);
        }
        return false;
    }
}
