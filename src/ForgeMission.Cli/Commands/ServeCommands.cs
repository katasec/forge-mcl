using System.CommandLine;
using System.Diagnostics;
using ForgeMission.Cli.Services;
using ForgeMission.Core.Resolution;
using ForgeMission.Docker;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace ForgeMission.Cli.Commands;

// Local serving commands: `forge serve` (serve a mission as an OpenAI-compatible endpoint) and
// `forge claude` (serve the mission over the Anthropic wire + launch Claude Code wired to it + tear
// down on exit, 42.2). The wire server is built by MissionServerBuilder; container orchestration
// lives in ForgeMission.Docker and the shared CLI prereq helpers. This surface owns the command
// wiring plus the claude-specific target resolution, health polling and process launch.
internal static class ServeCommands
{
    // ---------------------------------------------------------------------------
    // forge serve

    internal static Command BuildServeCommand(IOptions<WebSearchOptions> webSearch, IHttpClientFactory clients, ILogger logger)
    {
        var agentArg = new Argument<FileInfo>("agent") { Description = "Path to agent.yaml", Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => new FileInfo("agent.yaml") };

        var cmd = new Command("serve", "Serve a mission as an OpenAI-compatible endpoint");
        cmd.Add(agentArg);

        cmd.SetCancellableAction(async (result, cancellationToken) =>
        {
            var agentFile = result.GetValue(agentArg)!;

            if (!agentFile.Exists) { CliDiagnostics.Die($"Agent config not found: {agentFile.FullName}\nCreate an agent.yaml next to your mission.mcl."); return; }

            AgentConfig config;
            try { config = AgentConfigLoader.Load(agentFile.FullName); }
            catch (Exception ex) { CliDiagnostics.Die($"Cannot read agent.yaml: {ex.Message}"); return; }

            var built = await MissionServerBuilder.TryBuildMissionServerAsync(webSearch, clients, logger, config, agentFile.DirectoryName!, cancellationToken);
            if (built is not { } server) return;
            var (app, missionPath, anthropicDoor, openAiDoor) = server;

            Console.Error.WriteLine($"forge serve — agent '{config.Id}' listening on http://0.0.0.0:{config.Port}");
            Console.Error.WriteLine($"  mission  : {missionPath}");
            Console.Error.WriteLine($"  wire     : {(anthropicDoor && openAiDoor ? "anthropic + openai" : anthropicDoor ? "anthropic" : "openai")}");
            Console.Error.WriteLine($"  endpoints:");
            if (anthropicDoor)
                Console.Error.WriteLine($"    POST /v1/messages          (messages API, streaming + non-streaming)");
            if (openAiDoor)
            {
                Console.Error.WriteLine($"    POST /v1/chat/completions  (chat, streaming)");
                Console.Error.WriteLine($"    POST /v1/responses         (responses API, streaming)");
                Console.Error.WriteLine($"    GET  /v1/models");
            }
            Console.Error.WriteLine($"    GET  /health");

            // The command's Ctrl+C/SIGTERM token stops the server (IHost.RunAsync(token)).
            try
            {
                await app.RunAsync(cancellationToken);
            }
            catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
            {
                CliDiagnostics.Die($"Port {config.Port} is already in use. Stop the existing process or change the port in agent.yaml.");
            }
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // forge claude — one command: serve the mission (Anthropic wire) + launch Claude Code
    // wired to it + tear down on exit (Phase 42.2). In-process by default for speed;
    // --container runs the same image the cloud runs for exact parity.

    internal static Command BuildClaudeCommand(IOptions<WebSearchOptions> webSearch, IHttpClientFactory clients, ILogger logger)
    {
        var targetArg    = new Argument<string?>("target") { Description = "Mission: a .mcl path, or empty (agent.yaml / lone .mcl in cwd)", Arity = ArgumentArity.ZeroOrOne };
        var containerOpt = new Option<bool>("--container")   { Description = "Run the mission as a Docker container (cloud parity) instead of in-process" };
        var portOpt      = new Option<int?>("--port")        { Description = "Pin the port instead of picking an ephemeral one" };
        PortOption.RejectOutOfRange(portOpt);
        var printEnvOpt  = new Option<bool>("--print-env")   { Description = "Print the export lines and keep serving (wire other tools by hand); Ctrl-C stops" };
        var promptOpt    = new Option<string?>("-p", "--prompt") { Description = "One-shot prompt passed straight through to claude" };

        var cmd = new Command("claude", "Launch Claude Code talking to a forge mission");
        cmd.Add(targetArg);
        cmd.Add(containerOpt);
        cmd.Add(portOpt);
        cmd.Add(printEnvOpt);
        cmd.Add(promptOpt);
        cmd.TreatUnmatchedTokensAsErrors = false;   // anything after the options is forwarded to claude

        cmd.SetCancellableAction(async (result, cancellationToken) =>
        {
            var printEnv = result.GetValue(printEnvOpt);
            if (!printEnv && !CliPrereqs.IsOnPath("claude"))
            {
                CliDiagnostics.Die("claude CLI not found on PATH.\nInstall it first: npm install -g @anthropic-ai/claude-code  (https://claude.com/claude-code)");
                return 0;
            }

            var resolved = ResolveClaudeTarget(result.GetValue(targetArg));
            if (resolved is not { } target) return 0;
            var (missionPath, missionName) = target;

            if (!await EnsureInitializedAsync(missionPath, cancellationToken)) return 0;

            var port = result.GetValue(portOpt) ?? FreePort.Find();
            var config = new AgentConfig
            {
                Mission = missionPath,
                Port    = result.GetValue(containerOpt) ? 8080 : port,   // container listens on 8080 inside
                Id      = missionName,
                Wire    = "anthropic",
            };

            Func<Task> teardown;
            string mode;
            if (result.GetValue(containerOpt))
            {
                var started = await StartMissionContainerAsync(config, missionPath, hostPort: port);
                if (started is null) return 0;
                teardown = started;
                mode     = "container";
            }
            else
            {
                var built = await MissionServerBuilder.TryBuildMissionServerAsync(webSearch, clients, logger, config, Path.GetDirectoryName(missionPath)!, cancellationToken);
                if (built is not { } server) return 0;
                var app = server.App;
                await app.StartAsync(cancellationToken);
                teardown = async () => { await app.StopAsync(); await app.DisposeAsync(); };
                mode     = "in-process";
            }

            var baseUrl = $"http://127.0.0.1:{port}";
            try
            {
                if (!await WaitUntilHealthyAsync(clients, baseUrl, timeoutMs: 60_000, cancellationToken))
                {
                    CliDiagnostics.Die($"Mission endpoint did not become healthy at {baseUrl}.");
                    return 0;
                }

                Console.Error.WriteLine($"✓ mission   {missionName,-15} {missionPath}");
                Console.Error.WriteLine($"✓ endpoint  /v1/messages    {baseUrl}   ({mode})");
                Console.Error.WriteLine($"✓ wired     ANTHROPIC_BASE_URL → forge");

                if (printEnv)
                {
                    Console.WriteLine($"export ANTHROPIC_BASE_URL={baseUrl}");
                    Console.WriteLine("export ANTHROPIC_API_KEY=forge-local");
                    Console.Error.WriteLine("↳ serving until Ctrl-C…");
                    // Ctrl-C/SIGTERM cancels the action's token: stop waiting and fall through to teardown.
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    return 0;
                }

                Console.Error.WriteLine("↳ launching claude…");
                return await RunWiredClaudeAsync(
                    baseUrl, result.GetValue(promptOpt), result.UnmatchedTokens);
            }
            finally
            {
                await teardown();
            }
        });

        return cmd;
    }

    // target → (absolute mission path, mission name). A path uses that file; empty means agent.yaml
    // or the lone .mcl in the current directory. Mission catalog selection and baked fallbacks belong
    // to the Runner, not this generic CLI distribution.
    internal static (string MissionPath, string MissionName)? ResolveClaudeTarget(string? target)
    {
        if (target is { } t && t.StartsWith('@'))
        {
            CliDiagnostics.Die($"Mission handle '{t}' is a Runner catalog feature. Pass a local .mcl mission path.");
            return null;
        }

        if (target is { } path)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) { CliDiagnostics.Die($"Mission file not found: {full}"); return null; }
            return (full, Path.GetFileNameWithoutExtension(full) is "mission" or ""
                ? new DirectoryInfo(Path.GetDirectoryName(full)!).Name
                : Path.GetFileNameWithoutExtension(full));
        }

        var agentYaml = Path.GetFullPath("agent.yaml");
        if (File.Exists(agentYaml))
        {
            try
            {
                var config = AgentConfigLoader.Load(agentYaml);
                return (Path.GetFullPath(Path.Combine(Path.GetDirectoryName(agentYaml)!, config.Mission)), config.Id);
            }
            catch (Exception ex) { CliDiagnostics.Die($"Cannot read agent.yaml: {ex.Message}"); return null; }
        }

        var candidates = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.mcl").Take(2).ToArray();
        if (candidates.Length == 1)
            return (candidates[0], new DirectoryInfo(Directory.GetCurrentDirectory()).Name);

        CliDiagnostics.Die(candidates.Length == 0
            ? "No mission found. Pass a .mcl path or run from a directory with a mission."
            : "Multiple .mcl files here — pass the one you mean: forge claude ./<mission>.mcl");
        return null;
    }

    // Missing mcl.lock ⇒ run `forge init` silently (local experts resolve without network).
    internal static async Task<bool> EnsureInitializedAsync(string missionPath, CancellationToken cancellationToken)
    {
        var lockPath = CliHelpers.MclLockPath(Path.GetDirectoryName(missionPath)!);
        if (File.Exists(lockPath)) return true;

        Console.Error.WriteLine("↳ initialising mission (forge init)…");
        try { await MissionDevCommands.RunInitAsync(new FileInfo(missionPath), refresh: false, cancellationToken); }
        catch (ForgeExitException) { return false; }
        return File.Exists(lockPath);
    }

    // --container: the EXACT image the cloud runs (the converged /v1 runner, 42.4) as an EPHEMERAL
    // auto-named container — started and torn down within this command (unlike the persistent
    // `forge agent start`). MissionFile env selects the runner's local-mission mode (serve exactly
    // the mounted mission, no built-ins); no agent.yaml is synthesized.
    static async Task<Func<Task>?> StartMissionContainerAsync(AgentConfig config, string missionPath, int hostPort)
    {
        const string runnerImage = "ghcr.io/katasec/forge-runner:latest";

        var docker = await DockerPrereqChecker.CheckDockerAsync();
        if (!CliPrereqs.RunAndPrintPrereqs([docker])) return null;

        if (!await DockerCli.IsImagePresentAsync(runnerImage))
        {
            AnsiConsole.MarkupLine($"[yellow]Pulling {runnerImage}...[/]");
            await DockerCli.PullImageAsync(runnerImage);
        }
        await DockerCli.EnsureNetworkAsync("forge-net");

        var missionDir         = Path.GetDirectoryName(missionPath)!;
        var workspaceRoot      = CliPrereqs.FindGitRoot(missionDir) ?? missionDir;
        var missionInWorkspace = "/workspace/" + Path.GetRelativePath(workspaceRoot, missionPath).Replace('\\', '/');

        var containerName = $"forge-claude-{Guid.NewGuid():N}"[..20];
        await DockerCli.RunContainerAsync(
            name:          containerName,
            image:         runnerImage,
            cmd:           [],   // the image's entrypoint IS the runner; config rides in env
            // Forward the full provider-key table (docs/design/deploy.md) — the mounted mission's
            // forge.toml names whichever env(...) keys it wants, exactly as the in-process path
            // sees them.
            env:           [.. CliPrereqs.BuildEnvArray("MCL_API_KEY", "MCL_MODEL", "MCL_PROVIDER", "MCL_ENDPOINT",
                                             "OPENAI_API_KEY", "CLAUDE_API_KEY", "XAI_API_KEY",
                                             "GROK_API_KEY", "GOOGLE_SEARCH_API_KEY"),
                            $"MissionFile={missionInWorkspace}"],
            binds:         [$"{workspaceRoot}:/workspace"],
            hostPort:      hostPort,
            containerPort: config.Port,
            network:       "forge-net");

        return async () => await DockerCli.StopAndRemoveAsync(containerName);
    }

    static async Task<bool> WaitUntilHealthyAsync(IHttpClientFactory clients, string baseUrl, int timeoutMs, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(CliHttpClients.Health);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        try
        {
            while (!await IsHealthyAsync(http, baseUrl, deadline.Token))
                await Task.Delay(250, deadline.Token);
            return true;
        }
        // The deadline passing is the poll's own timeout ("never became healthy"); the action's
        // token being cancelled propagates.
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    static async Task<bool> IsHealthyAsync(HttpClient http, string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, baseUrl + "/"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // Contract: swallow by design. While polling, a refused connection or
            // unresolved host means the server has not finished binding its port yet
            // — an expected transient state, not a failure. We retry until the
            // deadline; the owning boundary is the timeout, which returns false and
            // surfaces the "never became healthy" result to the caller. Only
            // HttpRequestException (transport-level "not up yet") is caught here;
            // any other exception propagates.
            return false;
        }
    }

    // Launch claude with the redirect env, stdio inherited (fully interactive), and
    // forward -p plus any unparsed tokens. Returns claude's exit code.
    static async Task<int> RunWiredClaudeAsync(string baseUrl, string? prompt, IReadOnlyList<string> passthrough)
    {
        var psi = new ProcessStartInfo("claude") { UseShellExecute = false };
        if (prompt is not null) { psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(prompt); }
        foreach (var token in passthrough) psi.ArgumentList.Add(token);

        psi.Environment["ANTHROPIC_BASE_URL"] = baseUrl;
        psi.Environment["ANTHROPIC_API_KEY"]  = "forge-local";   // forge ignores the value

        // Ctrl-C goes to the whole foreground group: claude handles it and exits; forge
        // must survive the signal so teardown still runs.
        Console.CancelKeyPress += Survive;
        try
        {
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= Survive;
        }

        static void Survive(object? _, ConsoleCancelEventArgs e) => e.Cancel = true;
    }
}
