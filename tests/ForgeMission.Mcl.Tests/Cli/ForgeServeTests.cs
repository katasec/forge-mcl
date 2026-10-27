using System.Net;
using System.Net.Sockets;
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
