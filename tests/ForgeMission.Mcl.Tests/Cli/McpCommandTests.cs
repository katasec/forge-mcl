using System.Text.Json;
using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge mcp serves one mission over stdio: stdout carries only JSON-RPC, stderr only the banner,
// and closing stdin ends the server with exit 0. No tool is called, so no provider is reached.
public sealed class McpCommandTests
{
    [Fact]
    public async Task Serves_the_mission_tool_on_stdout_and_only_the_banner_on_stderr()
    {
        await using var harness = ForgeCliHarness.Start();
        var mission = Path.Combine(AppContext.BaseDirectory, "Missions", "noop", "mission.mcl");
        JsonElement tools = default;

        var result = await harness.ConverseAsync(async (stdin, stdout) =>
        {
            await RequestAsync(stdin, stdout, 1, "initialize",
                """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"pin","version":"1"}}""");
            await stdin.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            tools = (await RequestAsync(stdin, stdout, 2, "tools/list", "{}")).GetProperty("tools");
        }, "mcp", mission);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"forge mcp — serving mission 'Noop' over stdio\n  mission: {mission}\n",
            result.StandardError.ReplaceLineEndings("\n"));
        Assert.Equal("", result.StandardOutput);
        Assert.Equal("Noop", Assert.Single(tools.EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty(harness.Proxy.Attempts);
    }

    // Sends one request; the next stdout line must be its JSON-RPC response. Returns its result.
    private static async Task<JsonElement> RequestAsync(StreamWriter stdin, StreamReader stdout, int id,
        string method, string parameters)
    {
        await stdin.WriteLineAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""");
        await stdin.FlushAsync();
        var response = JsonDocument.Parse(await stdout.ReadLineAsync() ?? "null").RootElement;
        Assert.Equal(id, response.GetProperty("id").GetInt32());
        return response.GetProperty("result").Clone();
    }
}
