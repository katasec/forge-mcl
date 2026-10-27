using System.Text.Json;
using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge mcp serves one mission over stdio: stdout carries only JSON-RPC, stderr only the banner,
// and closing stdin ends the server with exit 0. No provider is called; the one tool call (a
// kind:search step) reaches only the harness's non-forwarding proxy.
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

    // tools/list advertises each mission param once, in declaration order, as a required string.
    [Fact]
    public async Task Lists_each_mission_param_once_in_order_as_a_required_string()
    {
        await using var harness = ForgeCliHarness.Start();
        harness.WorkingDirectory = WriteSearchMission(harness.Home, "goal, topic, goal");
        Assert.Equal(0, (await harness.RunAsync("init")).ExitCode);
        JsonElement tools = default;

        await harness.ConverseAsync(async (stdin, stdout) =>
        {
            await RequestAsync(stdin, stdout, 1, "initialize",
                """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"pin","version":"1"}}""");
            await stdin.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            tools = (await RequestAsync(stdin, stdout, 2, "tools/list", "{}")).GetProperty("tools");
        }, "mcp");

        Assert.Equal(
            """{"type":"object","properties":{"goal":{"type":"string","description":"Value for goal"},"topic":{"type":"string","description":"Value for topic"}},"required":["goal","topic","goal"]}""",
            Assert.Single(tools.EnumerateArray()).GetProperty("inputSchema").GetRawText());
    }

    // A kind:search step uses the web-search client only when an xAI key is set (XAI_API_KEY, else
    // GROK_API_KEY); with one it calls xAI (here it reaches only the non-forwarding proxy), without
    // one — or with a blank one — the step fails clearly.
    [Theory]
    [InlineData("XAI_API_KEY", "xai-test-key", "")]
    [InlineData("GROK_API_KEY", "grok-test-key", "")]
    [InlineData("XAI_API_KEY", " ", "kind: search requires a configured IWebSearch")]
    [InlineData("XAI_API_KEY", null, "kind: search requires a configured IWebSearch")]
    public async Task A_search_step_uses_the_web_search_client_only_with_an_xai_key(string variable, string? key, string error)
    {
        await using var harness = ForgeCliHarness.Start();
        harness.WorkingDirectory = WriteSearchMission(harness.Home);
        Assert.Equal(0, (await harness.RunAsync("init")).ExitCode);
        harness.Variables[variable] = key;
        JsonElement call = default;

        await harness.ConverseAsync(async (stdin, stdout) =>
        {
            await RequestAsync(stdin, stdout, 1, "initialize",
                """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"pin","version":"1"}}""");
            await stdin.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            call = await RequestAsync(stdin, stdout, 2, "tools/call", """{"name":"Search","arguments":{"goal":"q"}}""");
        }, "mcp");

        Assert.True(call.GetProperty("isError").GetBoolean());
        Assert.Contains(error, call.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(!string.IsNullOrWhiteSpace(key), harness.Proxy.Attempts.Contains(new ProxyAttempt("CONNECT", "api.x.ai:443")));
    }

    private static string WriteSearchMission(string home, string parameters = "goal")
    {
        var dir = Path.Combine(home, "search");
        Directory.CreateDirectory(Path.Combine(dir, "experts", "WebSearch"));
        File.WriteAllText(Path.Combine(dir, "mission.mcl"), $$"""
            let provider = env("MCL_PROVIDER", "openai")
            let apiKey   = env("MCL_API_KEY", "stub")
            let model    = env("MCL_MODEL", "gpt-4o-mini")

            mission Search({{parameters}}) = {
                WebSearch(query: goal)
            }

            output(Search)
            """);
        File.WriteAllText(Path.Combine(dir, "experts", "WebSearch", "expert.md"), """
            ---
            name: WebSearch
            version: 0.1.0
            kind: search
            description: Live web search
            input: query
            output: results
            ---
            """);
        return dir;
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
