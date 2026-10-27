using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Manifest;
using ForgeMission.Core.Runtime;
using MclProgram = ForgeMission.Parser.Program;
using McpTextContent = ModelContextProtocol.Protocol.TextContentBlock;

namespace ForgeMission.Cli;

// Owns the forge mcp protocol surface: it turns an already-loaded mission into a stdio MCP server
// exposing that mission as a single tool. Mission resolution, validation and runner construction
// stay with the shared CLI helpers in Program and reach this surface through ServeAsync's inputs
// and runner factory. The JSON schema is hand-built (AOT-safe, no STJ reflection).
internal static class McpCommand
{
    // Serves one validated mission over stdio until the transport closes. Runners are built lazily
    // through buildRunners at each tool call so startup never needs the provider API key. The
    // server logs through the CLI host's logger factory (stderr only; stdout is the protocol).
    internal static async Task ServeAsync(
        ILoggerFactory loggerFactory,
        ForgeMission.Parser.MissionDeclaration firstMission,
        MclProgram ast,
        Dictionary<string, ExpertDefinition> expertDefs,
        ForgeManifest? manifest,
        string missionPath,
        Func<Dictionary<string, object>, IReadOnlyDictionary<string, IExpertRunner>?> buildRunners)
    {
        // Build the JSON schema for this mission's parameters
        var toolName   = firstMission.Name;
        var properties = new Dictionary<string, JsonSchemaProperty>(StringComparer.Ordinal);
        foreach (var param in firstMission.Params)
            properties[param] = new JsonSchemaProperty("string", $"Value for {param}");

        var inputSchema = new JsonSchema(properties, firstMission.Params);

        Console.Error.WriteLine($"forge mcp — serving mission '{toolName}' over stdio");
        Console.Error.WriteLine($"  mission: {missionPath}");

        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddMcpServer(options => { options.ServerInfo = new Implementation { Name = "forge", Version = "1.0" }; })
            .WithStdioServerTransport()
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools =
                [
                    new Tool
                    {
                        Name        = toolName,
                        Description = $"Run the '{toolName}' MCL mission",
                        InputSchema = inputSchema.ToJsonElement()
                    }
                ]
            }))
            .WithCallToolHandler(async (request, ct) =>
            {
                if (request.Params?.Name != toolName)
                    return new CallToolResult { Content = [new McpTextContent { Text = $"Unknown tool: {request.Params?.Name}" }], IsError = true };

                // Extract call-time arguments as string overrides
                var callVars = new Dictionary<string, string>(StringComparer.Ordinal);
                if (request.Params.Arguments is { } callArgs)
                {
                    foreach (var kv in callArgs)
                    {
                        var strVal = kv.Value.ValueKind == System.Text.Json.JsonValueKind.String
                            ? kv.Value.GetString() ?? string.Empty
                            : kv.Value.ToString();
                        callVars[kv.Key] = strVal;
                    }
                }

                // Build runners lazily at call time so startup never needs the API key
                Dictionary<string, object> callSeedContext;
                try { callSeedContext = ContextBuilder.Seed(ast, callVars); }
                catch (InvalidOperationException ex) { return new CallToolResult { Content = [new McpTextContent { Text = ex.Message }], IsError = true }; }

                var callRunners = buildRunners(callSeedContext);
                if (callRunners is null)
                    return new CallToolResult { Content = [new McpTextContent { Text = "Cannot initialise provider — check API key env vars in claude_desktop_config.json" }], IsError = true };

                var runOptions = new PipelineRunOptions(toolName, callVars);

                MissionResult missionResult;
                try { missionResult = await new PipelineRunner(callRunners, manifest?.Execution, ProviderClientBuilder.BuildWebSearch()).RunAsync(ast, expertDefs, runOptions); }
                catch (Exception ex) { return new CallToolResult { Content = [new McpTextContent { Text = ex.Message }], IsError = true }; }

                if (missionResult.Status == MissionStatus.Fail)
                    return new CallToolResult { Content = [new McpTextContent { Text = $"Mission failed: {missionResult.FailReason}" }], IsError = true };

                return new CallToolResult { Content = [new McpTextContent { Text = missionResult.Text }] };
            });

        var sp = services.BuildServiceProvider();
        var server = sp.GetRequiredService<McpServer>();
        await server.RunAsync(CancellationToken.None);
    }
}

// Minimal JSON schema helpers for MCP tool registration (AOT-safe, no STJ reflection)
file sealed class JsonSchemaProperty(string type, string description)
{
    public string Type        { get; } = type;
    public string Description { get; } = description;
}

file sealed class JsonSchema(Dictionary<string, JsonSchemaProperty> properties, IReadOnlyList<string> required)
{
    public System.Text.Json.JsonElement ToJsonElement()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"type\":\"object\",\"properties\":{");
        var first = true;
        foreach (var (name, prop) in properties)
        {
            if (!first) sb.Append(',');
            sb.Append($"\"{EscapeJson(name)}\":{{\"type\":\"{EscapeJson(prop.Type)}\",\"description\":\"{EscapeJson(prop.Description)}\"}}");
            first = false;
        }
        sb.Append("},\"required\":[");
        for (var i = 0; i < required.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"\"{EscapeJson(required[i])}\"");
        }
        sb.Append("]}");
        return System.Text.Json.JsonDocument.Parse(sb.ToString()).RootElement;
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
