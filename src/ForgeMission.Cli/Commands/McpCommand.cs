using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Manifest;
using ForgeMission.Core.Runtime;
using ForgeMission.Cli.Services;
using MclProgram = ForgeMission.Parser.Program;
using McpTextContent = ModelContextProtocol.Protocol.TextContentBlock;

namespace ForgeMission.Cli.Commands;

// Owns the forge mcp protocol surface: it turns an already-loaded mission into a stdio MCP server
// exposing that mission as a single tool. Mission resolution, validation and runner construction
// stay with the shared CLI helpers in Program and reach this surface through ServeAsync's inputs
// and runner factory. The input schema is written with Utf8JsonWriter (AOT-safe, no STJ reflection).
internal static class McpCommand
{
    // Serves one validated mission over stdio until the transport closes or cancellationToken
    // (the command's Ctrl+C/SIGTERM token) is cancelled. Runners are built lazily through
    // buildRunners at each tool call so startup never needs the provider API key. The server logs
    // through the CLI host's logger factory (stderr only; stdout is the protocol).
    internal static async Task ServeAsync(
        ILoggerFactory loggerFactory,
        IHttpClientFactory clients,
        IOptions<WebSearchOptions> webSearch,
        ForgeMission.Parser.MissionDeclaration firstMission,
        MclProgram ast,
        Dictionary<string, ExpertDefinition> expertDefs,
        ForgeManifest? manifest,
        string missionPath,
        Func<Dictionary<string, object>, IReadOnlyDictionary<string, IExpertRunner>?> buildRunners,
        CancellationToken cancellationToken)
    {
        var toolName    = firstMission.Name;
        var inputSchema = BuildInputSchema(firstMission.Params);

        Console.Error.WriteLine($"forge mcp — serving mission '{toolName}' over stdio");
        Console.Error.WriteLine($"  mission: {missionPath}");

        var options = new McpServerOptions { ServerInfo = new Implementation { Name = "forge", Version = "1.0" } };
        options.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
        {
            Tools =
            [
                new Tool
                {
                    Name        = toolName,
                    Description = $"Run the '{toolName}' MCL mission",
                    InputSchema = inputSchema
                }
            ]
        });
        options.Handlers.CallToolHandler = async (request, ct) =>
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
            try { missionResult = await new PipelineRunner(callRunners, manifest?.Execution, ProviderClientBuilder.BuildWebSearch(webSearch, clients)).RunAsync(ast, expertDefs, runOptions, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { return new CallToolResult { Content = [new McpTextContent { Text = ex.Message }], IsError = true }; }

            if (missionResult.Status == MissionStatus.Fail)
                return new CallToolResult { Content = [new McpTextContent { Text = $"Mission failed: {missionResult.FailReason}" }], IsError = true };

            return new CallToolResult { Content = [new McpTextContent { Text = missionResult.Text }] };
        };

        await using var server = McpServer.Create(new StdioServerTransport("forge", loggerFactory), options, loggerFactory);
        await server.RunAsync(cancellationToken);
    }

    // The tool's input schema: an object with one string property per distinct mission param, in
    // declaration order, and every param (as declared) required. Utf8JsonWriter does the escaping.
    private static JsonElement BuildInputSchema(IReadOnlyList<string> parameters)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            foreach (var param in parameters.Distinct(StringComparer.Ordinal))
            {
                writer.WriteStartObject(param);
                writer.WriteString("type", "string");
                writer.WriteString("description", $"Value for {param}");
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            foreach (var param in parameters)
                writer.WriteStringValue(param);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }
}
