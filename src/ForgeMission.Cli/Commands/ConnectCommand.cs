using System.CommandLine;
using ForgeMission.Core.Resolution;
using Microsoft.AspNetCore.Builder;
using System.Text.Json;

namespace ForgeMission.Cli;

// forge connect vscode — wire the Claude Code EXTENSION (VS Code / VSCodium) to a
// mission. The extension is a GUI process that never sees shell env, so the sanctioned
// gesture is the claudeCode.environmentVariables key in the workspace settings — which
// BOTH editors read, so one write covers VS Code and VSCodium alike. A pinned port
// (default 8787) because the extension cannot chase ephemeral ones.
//
// Mission resolution, initialisation and server construction stay with the shared CLI
// helpers in Program and reach this surface through Build's seam parameters; this module
// owns only the connect command wiring and the .vscode/settings.json JSONC merge.
internal static class ConnectCommand
{
    internal static Command Build(
        Func<string?, (string MissionPath, string MissionName)?> resolveTarget,
        Func<string, Task<bool>> ensureInitialized,
        Func<AgentConfig, string, Task<(WebApplication App, string MissionPath, bool AnthropicDoor, bool OpenAiDoor)?>> buildMissionServer,
        Action<string> die)
    {
        var connectCmd = new Command("connect", "Wire an editor's Claude Code extension to a forge mission");
        connectCmd.Add(BuildVsCodeCommand(resolveTarget, ensureInitialized, buildMissionServer, die));
        return connectCmd;
    }

    static Command BuildVsCodeCommand(
        Func<string?, (string MissionPath, string MissionName)?> resolveTarget,
        Func<string, Task<bool>> ensureInitialized,
        Func<AgentConfig, string, Task<(WebApplication App, string MissionPath, bool AnthropicDoor, bool OpenAiDoor)?>> buildMissionServer,
        Action<string> die)
    {
        var targetArg = new Argument<string?>("target") { Description = "Mission: a .mcl path, or empty (agent.yaml / lone .mcl in cwd)", Arity = ArgumentArity.ZeroOrOne };
        var portOpt   = new Option<int>("--port") { Description = "Port to serve on and write into the settings (default 8787)", DefaultValueFactory = _ => 8787 };

        var cmd = new Command("vscode", "Write .vscode/settings.json and serve the mission (also covers VSCodium)");
        cmd.Add(targetArg);
        cmd.Add(portOpt);

        cmd.SetAction(async result =>
        {
            var resolved = resolveTarget(result.GetValue(targetArg));
            if (resolved is not { } target) return;
            var (missionPath, missionName) = target;

            if (!await ensureInitialized(missionPath)) return;

            var port    = result.GetValue(portOpt);
            var baseUrl = $"http://127.0.0.1:{port}";
            if (!TryWriteVsCodeSettings(Directory.GetCurrentDirectory(), baseUrl, die)) return;

            var config = new AgentConfig { Mission = missionPath, Port = port, Id = missionName, Wire = "anthropic" };
            var built  = await buildMissionServer(config, Path.GetDirectoryName(missionPath)!);
            if (built is not { } server) return;
            var app = server.App;

            Console.Error.WriteLine($"✓ mission   {missionName,-15} {missionPath}");
            Console.Error.WriteLine($"✓ endpoint  /v1/messages    {baseUrl}");
            Console.Error.WriteLine($"✓ settings  .vscode/settings.json → claudeCode.environmentVariables");
            Console.Error.WriteLine($"↳ open VS Code / VSCodium in this folder — the Claude Code extension now talks to the mission.");
            Console.Error.WriteLine($"  Serving until Ctrl-C (settings persist; rerun this command to reconnect).");

            try { await app.RunAsync(); }
            catch (IOException ex) when (ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                                      || ex.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse })
            {
                die($"Port {port} is already in use. Pass a different --port (and rerun so the settings match).");
            }
        });

        return cmd;
    }

    // Merge claudeCode.environmentVariables into the workspace settings. settings.json is
    // JSONC (comments allowed) and a DOM rewrite would destroy comments — so: create freely
    // when absent; merge when comment-free; otherwise print the snippet instead of clobbering.
    static bool TryWriteVsCodeSettings(string workspaceDir, string baseUrl, Action<string> die)
    {
        var settingsDir  = Path.Combine(workspaceDir, ".vscode");
        var settingsPath = Path.Combine(settingsDir, "settings.json");

        var envNode = new System.Text.Json.Nodes.JsonObject
        {
            ["ANTHROPIC_BASE_URL"] = baseUrl,
            ["ANTHROPIC_API_KEY"]  = "forge-local",
        };

        System.Text.Json.Nodes.JsonObject root;
        if (!File.Exists(settingsPath))
        {
            root = [];
        }
        else
        {
            var raw = File.ReadAllText(settingsPath);
            if (HasJsoncComments(raw))
            {
                Console.Error.WriteLine($"{settingsPath} contains comments, which a rewrite would lose.");
                Console.Error.WriteLine("Add this key yourself, then rerun with the same --port:\n");
                Console.Error.WriteLine($$"""
                    "claudeCode.environmentVariables": {
                      "ANTHROPIC_BASE_URL": "{{baseUrl}}",
                      "ANTHROPIC_API_KEY": "forge-local"
                    }
                    """);
                return false;
            }
            try
            {
                root = System.Text.Json.Nodes.JsonNode.Parse(raw,
                    documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true })
                    as System.Text.Json.Nodes.JsonObject ?? [];
            }
            catch (JsonException ex) { die($"Cannot parse {settingsPath}: {ex.Message}"); return false; }
        }

        root["claudeCode.environmentVariables"] = envNode;

        Directory.CreateDirectory(settingsDir);
        using var stream = File.Create(settingsPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        root.WriteTo(writer);
        return true;
    }

    // Cheap comment detection outside string literals — good enough to protect user JSONC.
    static bool HasJsoncComments(string raw)
    {
        var inString = false;
        for (var i = 0; i < raw.Length - 1; i++)
        {
            var c = raw[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '/' && (raw[i + 1] == '/' || raw[i + 1] == '*')) return true;
        }
        return false;
    }
}
