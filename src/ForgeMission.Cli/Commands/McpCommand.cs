using System.Diagnostics;
using System.Buffers;
using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Manifest;
using ForgeMission.Core.Resolution;
using ForgeMission.Core.Runtime;
using ForgeMission.Cli.Services;
using MclProgram = ForgeMission.Parser.Program;
using McpTextContent = ModelContextProtocol.Protocol.TextContentBlock;

namespace ForgeMission.Cli.Commands;

// forge mcp: serves one mission as a stdio MCP server with that mission as its single tool.
// Mission loading and validation use CliHelpers; runners are built per tool call by
// MissionServerBuilder.BuildRunners and reach ServeAsync as a factory. The input schema is written
// with Utf8JsonWriter (AOT-safe, no STJ reflection).
internal static class McpCommand
{
    // Thin shell: loads and validates the mission with the shared CLI helpers, then hands the prepared
    // mission to ServeAsync, which owns the stdio MCP server. Runners are built lazily per tool call;
    // the server runs until stdin closes or the command's Ctrl+C/SIGTERM token is cancelled.
    internal static Command BuildMcpCommand(ILoggerFactory loggerFactory, IHttpClientFactory clients, IOptions<WebSearchOptions> webSearch)
    {
        var missionArg = new Argument<FileInfo>("mission")
        {
            Description = "Path to the .mcl mission file",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => new FileInfo("mission.mcl")
        };

        var cmd = new Command("mcp", "Expose a mission as a stdio MCP server (for Claude Desktop and MCP-aware tools)");
        cmd.Add(missionArg);

        cmd.SetCancellableAction(async (result, ct) =>
        {
            var mission    = result.GetValue(missionArg)!;
            var missionDir = mission.DirectoryName!;
            var lockPath   = CliHelpers.MclLockPath(missionDir);

            if (!File.Exists(lockPath)) CliDiagnostics.Die($"{MclErrorCode.MissionNotInitialised.Code()} Mission not initialised — run 'forge init' first.");

            var (ast, manifest) = await CliHelpers.LoadMissionAsync(mission.FullName, ct);

            LockFile lockFile;
            try { lockFile = LockFileIO.Read(lockPath); }
            catch (Exception ex) { CliDiagnostics.Die($"Cannot read mcl.lock: {ex.Message}"); throw new UnreachableException(); }

            Dictionary<string, ExpertDefinition> expertDefs;
            try { expertDefs = ExpertResolver.ResolveAll(lockFile, missionDir, verbose: null, warnings: Console.Error); }
            catch (Exception ex) when (ex is ExpertLoadException or AggregateExpertLoadException)
            { CliDiagnostics.ReportExpertLoadFailure(ex); throw new ForgeExitException(1); }

            if (!CliHelpers.TryValidate(ast, expertDefs, contractErrorsAreFatal: true, mission.FullName))
                throw new ForgeExitException(1);

            var firstMission = ast.Missions.FirstOrDefault();
            if (firstMission is null) CliDiagnostics.Die("No mission declaration found in mission file.");

            await ServeAsync(loggerFactory, clients, webSearch, firstMission, ast, expertDefs, manifest, mission.FullName,
                callSeedContext => MissionServerBuilder.BuildRunners(manifest, callSeedContext), ct);
        });

        return cmd;
    }

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
        Func<Dictionary<string, object>, IReadOnlyDictionary<string, IExpertRunner>> buildRunners,
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
        var tool = new MissionTool(toolName, ast, expertDefs, manifest, buildRunners, webSearch, clients);
        options.Handlers.CallToolHandler = (request, ct) => tool.CallAsync(request.Params, ct);

        await using var server = McpServer.Create(new StdioServerTransport("forge", loggerFactory), options, loggerFactory);
        await server.RunAsync(cancellationToken);
    }

    // The mission exposed as the server's one tool. Each call maps its arguments to string
    // variables, seeds the context, builds runners lazily (so startup never needs the API key),
    // runs the mission and maps the outcome to a tool result. A provider that cannot be built is a
    // tool error, so the server keeps serving.
    private sealed class MissionTool(
        string toolName,
        MclProgram ast,
        Dictionary<string, ExpertDefinition> expertDefs,
        ForgeManifest? manifest,
        Func<Dictionary<string, object>, IReadOnlyDictionary<string, IExpertRunner>> buildRunners,
        IOptions<WebSearchOptions> webSearch,
        IHttpClientFactory clients)
    {
        internal async ValueTask<CallToolResult> CallAsync(CallToolRequestParams? call, CancellationToken ct)
        {
            if (call?.Name != toolName)
                return ErrorResult($"Unknown tool: {call?.Name}");

            var callVars = ToCallVariables(call.Arguments);

            Dictionary<string, object> callSeedContext;
            try { callSeedContext = ContextBuilder.Seed(ast, callVars); }
            catch (InvalidOperationException ex) { return ErrorResult(ex.Message); }

            IReadOnlyDictionary<string, IExpertRunner> callRunners;
            try { callRunners = buildRunners(callSeedContext); }
            catch (ForgeExitException) { return ErrorResult(ProviderInitError); }
            catch (Exception ex) { return ErrorResult($"{ProviderInitError} {ex.Message}"); }

            var runOptions  = new PipelineRunOptions(toolName, callVars);

            MissionResult missionResult;
            try { missionResult = await new PipelineRunner(callRunners, manifest?.Execution, WebSearchBuilder.Build(webSearch, clients)).RunAsync(ast, expertDefs, runOptions, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { return ErrorResult(ex.Message); }

            return ToToolResult(missionResult);
        }
    }

    // BuildRunners reports the specific cause on stderr (Die) before it throws.
    private const string ProviderInitError = "Cannot initialise provider — check API key env vars and forge.toml.";

    // Call-time arguments become string overrides: JSON strings by value, anything else as raw JSON text.
    private static Dictionary<string, string> ToCallVariables(IDictionary<string, JsonElement>? arguments)
    {
        var callVars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments is null) return callVars;

        foreach (var kv in arguments)
        {
            callVars[kv.Key] = kv.Value.ValueKind == JsonValueKind.String
                ? kv.Value.GetString() ?? string.Empty
                : kv.Value.ToString();
        }
        return callVars;
    }

    private static CallToolResult ToToolResult(MissionResult missionResult) =>
        missionResult.Status == MissionStatus.Fail
            ? ErrorResult($"Mission failed: {missionResult.FailReason}")
            : new CallToolResult { Content = [new McpTextContent { Text = missionResult.Text }] };

    private static CallToolResult ErrorResult(string text) =>
        new() { Content = [new McpTextContent { Text = text }], IsError = true };

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
