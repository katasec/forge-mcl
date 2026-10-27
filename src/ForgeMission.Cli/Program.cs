using System.CommandLine;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Manifest;
using ForgeMission.Core.Resolution;
using ForgeMission.Parser;
using ForgeMission.Core.Runtime;
using static ForgeMission.Core.Runtime.MissionStatus;
using Microsoft.Extensions.AI;
using ForgeMission.ChatClients;
using Katasec.OciClient;
using ForgeMission.Serve;
using Microsoft.AspNetCore.Builder;
using Spectre.Console;
using ForgeMission.Cli;
using ForgeMission.Docker;
using ForgeMission.MissionRegistry;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MclProgram = ForgeMission.Parser.Program;

// No @file response-file expansion — @handles (forge claude @websearch) are arguments.
// One top-level exit/exception handler owns every exit code: helpers signal a deliberate exit by
// throwing ForgeExitException (see Die) rather than calling Environment.Exit deep in the stack, so
// teardown/finally runs on the way out. System.CommandLine's own exception handler is disabled so
// those exceptions reach this handler; it also keeps the library's prior behaviour for any
// unexpected exception (print it, exit 1). Ctrl+C/SIGTERM reaches every action as its token and
// exits 130 there (CommandActions); a cancellation that reaches this handler is not the command's own.
var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false };
try
{
    using var host = BuildHost();
    return await BuildRootCommand(host.Services)
        .Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null })
        .InvokeAsync(invocation);
}
catch (ForgeExitException exit)
{
    return exit.ExitCode;
}
catch (OperationCanceledException)
{
    return 1;
}
catch (Exception ex)
{
    Console.Error.Write("Unhandled exception: ");
    Console.Error.WriteLine(ex);
    return 1;
}

// One generic host is the CLI's composition root: commands resolve shared services from its
// Services. System.CommandLine owns the arguments, so the host gets none; its console logger writes
// to stderr only (stdout stays the commands' output and forge mcp's JSON-RPC) at Warning and above.
static IHost BuildHost()
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Configuration.AddJsonFile(ForgeSettings.DefaultPath, optional: true);
    builder.Services.AddOptions<ForgeSettings>().Bind(builder.Configuration);
    builder.Services.AddSingleton<IValidateOptions<ForgeSettings>, ForgeSettingsValidator>();
    builder.Services.AddCliHttpClients();
    builder.Services.AddOptions<ForgeApiSettings>()
        .Bind(builder.Configuration.GetSection(ForgeApiSettings.Section))
        .PostConfigure(ForgeApiSettings.NormalizeEndpoint);
    builder.Services.AddOptions<PlatformLoginOptions>()
        .Bind(builder.Configuration)
        .PostConfigure(PlatformLoginOptions.TrimPlatformEndpoint);
    builder.Services.AddOptions<WebSearchOptions>()
        .Configure<IConfiguration>((options, configuration) =>
            options.ApiKey = configuration["XAI_API_KEY"] ?? configuration["GROK_API_KEY"]);
    return builder.Build();
}

static RootCommand BuildRootCommand(IServiceProvider services)
{
    var api = services.GetRequiredService<IOptions<ForgeApiSettings>>();
    var clients = services.GetRequiredService<IHttpClientFactory>();
    var webSearch = services.GetRequiredService<IOptions<WebSearchOptions>>();
    var logger = services.GetRequiredService<ILogger<Program>>();
    var rootCommand = new RootCommand("forge — Mission Control Language runtime");
    rootCommand.Add(BuildInitCommand());
    rootCommand.Add(BuildRunCommand(api, clients));
    rootCommand.Add(BuildValidateCommand());
    rootCommand.Add(BuildListCommand());
    rootCommand.Add(BuildExpertCommand());
    rootCommand.Add(BuildRegistryCommand());
    rootCommand.Add(BuildLoginCommand(services.GetRequiredService<IOptions<PlatformLoginOptions>>(), clients));
    rootCommand.Add(BuildWhoamiCommand(clients));
    rootCommand.Add(BuildLogoutCommand());
    rootCommand.Add(BuildExecCommand(api, clients));
    rootCommand.Add(ForgeChat.BuildCommand(services.GetRequiredService<IOptions<ForgeSettings>>(), clients)); // forge chat (53.2; --hands, Phase 55)
    rootCommand.Add(ForgeProject.BuildCommand(clients));
    rootCommand.Add(BuildPublishCommand());
    rootCommand.Add(CleanCommand.Build());
    rootCommand.Add(BuildServeCommand(webSearch, clients, logger));
    rootCommand.Add(BuildClaudeCommand(webSearch, clients, logger));
    rootCommand.Add(ConnectCommand.Build(ResolveClaudeTarget, EnsureInitializedAsync,
        (config, agentDir) => TryBuildMissionServerAsync(webSearch, clients, logger, config, agentDir), Die));
    rootCommand.Add(AgentCommand.Build(Die, RunAndPrintPrereqs, BuildEnvArray, FindGitRoot));
    rootCommand.Add(WebuiCommand.Build(Die, RunAndPrintPrereqs));
    rootCommand.Add(BuildProviderCommand());
    rootCommand.Add(BuildMcpCommand(services.GetRequiredService<ILoggerFactory>(), clients, webSearch));
    return rootCommand;
}

// ---------------------------------------------------------------------------
// fms init

static Command BuildInitCommand()
{
    var missionArg  = new Argument<FileInfo?>("mission") { Description = "Path to the .mcl mission file (default: mission.mcl)", Arity = ArgumentArity.ZeroOrOne };
    var refreshOpt  = new Option<bool>("--refresh") { Description = "Re-pull OCI experts even if already present in ~/.forge/experts" };

    var cmd = new Command("init", "Resolve expert sources and generate mcl.lock");
    cmd.Add(missionArg);
    cmd.Add(refreshOpt);

    cmd.SetCancellableAction((result, cancellationToken) =>
        RunInitAsync(result.GetValue(missionArg), result.GetValue(refreshOpt), cancellationToken));

    return cmd;
}

// Resolve local experts, pull any OCI experts, and write mcl.lock. Each helper reports its own
// failure through Die (which throws ForgeExitException), so the orchestration reads top-down.
static async Task RunInitAsync(FileInfo? missionArg, bool refresh, CancellationToken cancellationToken)
{
    var mission    = ResolveMission(missionArg);
    var missionDir = mission.DirectoryName!;

    var source = await TryReadFile(mission.FullName, cancellationToken);
    if (source is null) return;

    var ast = TryParse(source, mission.FullName);
    if (ast is null) return;

    ForgeManifest? manifest = null;
    try { manifest = ForgeTomlReader.TryRead(mission.FullName); }
    catch (ForgeTomlException ex) { Die(ex.Message); return; }

    Console.WriteLine("Resolving experts...\n");

    var lockFile = BuildLocalLockFile(missionDir);
    foreach (var (name, entry) in lockFile.Experts.OrderBy(k => k.Key))
        Console.WriteLine($"  ✓ {name,-30} local    {entry.Path}");

    await PullOciExpertsAsync(manifest, refresh, lockFile, cancellationToken);

    var lockPath = Path.Combine(missionDir, "mcl.lock");
    LockFileIO.Write(lockPath, lockFile);

    var total = lockFile.Experts.Count;
    Console.WriteLine($"\nmcl.lock written ({total} expert{(total == 1 ? "" : "s")}). Run 'forge run' to execute the mission.");
}

// Local experts: discover from ./experts and seed the lock file.
static LockFile BuildLocalLockFile(string missionDir)
{
    var localCatalog    = new Dictionary<string, ResolvedExpert>(StringComparer.Ordinal);
    var localExpertsDir = Path.Combine(missionDir, SourceResolver.DefaultExpertsDir);
    if (Directory.Exists(localExpertsDir))
    {
        try { localCatalog = new SourceResolver().Resolve(missionDir); }
        catch (MclException ex) { Die(ex.Message); }
    }
    return LockFileIO.Build(localCatalog, missionDir);
}

// OCI experts: pull each manifest expert from its registry into the lock file, in name order.
// The first failure stops init (PullOciExpertAsync calls Die, which throws).
static async Task PullOciExpertsAsync(ForgeManifest? manifest, bool refresh, LockFile lockFile, CancellationToken cancellationToken)
{
    if (manifest is null) return;
    foreach (var (name, ociRef) in manifest.Experts.OrderBy(k => k.Key))
        await PullOciExpertAsync(name, ociRef, refresh, lockFile, cancellationToken);
}

// The caller's own cancellation propagates; any other pull failure stops init with MCL011.
static async Task PullOciExpertAsync(string name, string ociRef, bool refresh, LockFile lockFile, CancellationToken cancellationToken)
{
    try
    {
        var (cachePath, status) = await OciExpertPuller.PullAsync(ociRef, refresh, cancellationToken);
        lockFile.Experts[name] = new LockFileExpert
        {
            Source = "oci",
            Path   = OciExpertPuller.ToLockPath(cachePath),
            Hash   = LockFileIO.ComputeHash(cachePath),
        };
        Console.WriteLine($"  ✓ {name,-30} {status,-8} {ociRef}");
    }
    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine($"  ✗ {name,-30} failed   {ociRef}");
        Console.Error.WriteLine($"    {ex.Message}");
        Console.Error.WriteLine("    Run 'forge registry login <registry> --token <PAT>' if this is an auth error.");
        Die($"MCL011 OCI pull failed for '{name}'.");
    }
}

// ---------------------------------------------------------------------------
// fms run

static Command BuildRunCommand(IOptions<ForgeApiSettings> api, IHttpClientFactory clients)
{
    var missionArg = new Argument<string>("mission") { Description = "Hosted mission handle, e.g. vanilla" };
    var promptArg = new Argument<string>("prompt") { Description = "The mission's free-text input" };
    var cmd = new Command("run", "Run a hosted mission and print the answer");
    cmd.Add(missionArg);
    cmd.Add(promptArg);
    cmd.SetCancellableAction((result, cancellationToken) => ForgeExec.RunAsync(api, clients,
        result.GetValue(missionArg)!, result.GetValue(promptArg), cancellationToken: cancellationToken));
    return cmd;
}

// ---------------------------------------------------------------------------
// fms validate

static Command BuildValidateCommand()
{
    var missionArg = new Argument<FileInfo?>("mission") { Description = "Path to the .mcl mission file (default: mission.mcl)", Arity = ArgumentArity.ZeroOrOne };

    var cmd = new Command("validate", "Validate a mission file and its expert references");
    cmd.Add(missionArg);

    cmd.SetCancellableAction(async (result, cancellationToken) =>
    {
        var mission    = ResolveMission(result.GetValue(missionArg));
        var missionDir = mission.DirectoryName!;
        var lockPath   = Path.Combine(missionDir, "mcl.lock");

        var source = await TryReadFile(mission.FullName, cancellationToken);
        if (source is null) return;

        var ast = TryParse(source, mission.FullName);
        if (ast is null) return;

        // Validate forge.toml if present
        try { ForgeTomlReader.TryRead(mission.FullName); }
        catch (ForgeTomlException ex) { Die(ex.Message); return; }

        // Warn if lock file is absent or stale
        if (!File.Exists(lockPath))
        {
            Console.Error.WriteLine($"{BoldYellow("warning")}{Bold(": MCL006 mcl.lock not found — run 'forge init' to generate it")}");
        }
        if (!File.Exists(lockPath))
        {
            var expertDefs = TryLoadExperts(Path.Combine(missionDir, SourceResolver.DefaultExpertsDir));
            if (expertDefs is null) return;
            if (TryValidate(ast, expertDefs, missionFilePath: mission.FullName))
                Console.WriteLine("OK — mission is valid.");
        }
        else
        {
            var lockFile   = LockFileIO.Read(lockPath);
            Dictionary<string, ExpertDefinition> expertDefs;
            try { expertDefs = ExpertResolver.ResolveAll(lockFile, missionDir, warnings: Console.Error); }
            catch (ExpertLoadException ex) { ReportExpertDiagnostic(ex); return; }
            if (TryValidate(ast, expertDefs, missionFilePath: mission.FullName))
                Console.WriteLine("OK — mission is valid.");
        }
    });

    return cmd;
}

// ---------------------------------------------------------------------------
// fms list

static Command BuildListCommand()
{
    var listCommand    = new Command("list", "List available resources");
    var expertsOpt     = new Option<DirectoryInfo?>("--experts") { Description = "Experts directory (default: ./experts)" };
    var listExpertsCmd = new Command("experts", "List experts in the experts directory");
    listExpertsCmd.Add(expertsOpt);

    listExpertsCmd.SetCancellableAction(async (result, _) =>
    {
        var experts    = result.GetValue(expertsOpt);
        var expertsDir = experts?.FullName ?? "experts";

        var expertDefs = TryLoadExperts(expertsDir);
        if (expertDefs is null) return;

        if (expertDefs.Count == 0) { Console.WriteLine($"No experts found in {expertsDir}"); return; }

        Console.WriteLine($"Experts in {expertsDir}:");
        foreach (var (name, def) in expertDefs.OrderBy(k => k.Key))
            Console.WriteLine($"  {name,-30} {def.Input} -> {def.Output}");

        await Task.CompletedTask;
    });

    listCommand.Add(listExpertsCmd);
    return listCommand;
}

// ---------------------------------------------------------------------------
// fms expert

static Command BuildExpertCommand()
{
    var expertCommand = new Command("expert", "Manage experts");

    var nameArg      = new Argument<string>("name") { Description = "Expert name (PascalCase)" };
    var expertsOpt   = new Option<DirectoryInfo?>("--experts") { Description = "Experts directory (default: ./experts)" };
    var initExpertCmd = new Command("init", "Scaffold a new expert directory");
    initExpertCmd.Add(nameArg);
    initExpertCmd.Add(expertsOpt);

    initExpertCmd.SetCancellableAction(async (result, cancellationToken) =>
    {
        var name       = result.GetValue(nameArg)!;
        var experts    = result.GetValue(expertsOpt);
        var expertsDir = experts?.FullName ?? "experts";

        if (!char.IsUpper(name[0]))
        {
            Die($"Expert name must be PascalCase, got '{name}'.");
            return;
        }

        var expertDir = Path.Combine(expertsDir, name);
        var expertMd  = Path.Combine(expertDir, "expert.md");

        if (File.Exists(expertMd))
        {
            Die($"Expert '{name}' already exists at {expertMd}");
            return;
        }

        Directory.CreateDirectory(expertDir);
        await File.WriteAllTextAsync(expertMd, ExpertTemplate(name), cancellationToken);

        Console.WriteLine($"Created {expertMd}");
    });

    expertCommand.Add(initExpertCmd);
    return expertCommand;
}

// ---------------------------------------------------------------------------
// forge registry login  (42.5: OCI-registry credentials move under `forge registry`;
// the top-level `forge login` becomes platform sign-in)

static Command BuildRegistryCommand()
{
    var cmd = new Command("registry", "OCI registry operations");
    cmd.Add(BuildRegistryLoginCommand());
    return cmd;
}

static Command BuildRegistryLoginCommand()
{
    var registryArg = new Argument<string>("registry") { Description = "Registry host (e.g. ghcr.io)" };
    var tokenOpt    = new Option<string>("--token") { Description = "Credential token (e.g. GitHub PAT)" };

    var cmd = new Command("login", "Save registry credentials to ~/.forge/credentials.json");
    cmd.Add(registryArg);
    cmd.Add(tokenOpt);

    cmd.SetCancellableAction((result, _) =>
    {
        SaveRegistryCredential(result.GetValue(registryArg)!, result.GetValue(tokenOpt)!);
        return Task.CompletedTask;
    });
    return cmd;
}

// forge login = platform sign-in (loopback + PKCE → platform key).
// OCI-registry credentials live under `forge registry login`.
static Command BuildLoginCommand(IOptions<PlatformLoginOptions> login, IHttpClientFactory clients)
{
    var cmd = new Command("login", "Sign in to Forge (browser) and store a platform key");
    cmd.SetCancellableAction((_, cancellationToken) => PlatformLogin.RunAsync(login, clients, cancellationToken));
    return cmd;
}

static Command BuildWhoamiCommand(IHttpClientFactory clients)
{
    var cmd = new Command("whoami", "Show the signed-in Forge user and credit balance");
    cmd.SetCancellableAction((_, _) => PlatformLogin.WhoAmIAsync(clients));
    return cmd;
}

static Command BuildLogoutCommand()
{
    var cmd = new Command("logout", "Sign out of Forge (remove the stored platform key)");
    cmd.SetCancellableAction((_, _) => Task.FromResult(PlatformLogin.Logout()));
    return cmd;
}

// forge exec — the one-shot hosted-mission command (42.6 task 8). The `@` prefix on a handle is
// optional (ForgeExec.RunAsync strips it if present) — deliberately, because pwsh (the maintainer's
// default shell, see AGENTS.md) mangles an unquoted leading `@` before it reaches a native process
// ("Required argument missing", not a forge bug — confirmed 2026-07-19: `@websearch` fails in pwsh,
// `"@websearch"` or bare `websearch` both work). Documented as the plain form so pwsh users aren't
// steered into the gotcha; bash/zsh users can still type `@handle` fine.
static Command BuildExecCommand(IOptions<ForgeApiSettings> api, IHttpClientFactory clients)
{
    var targetArg = new Argument<string>("target") { Description = "Mission handle, e.g. websearch (an optional leading @ is also accepted, but quote it in pwsh: \"@websearch\")" };
    var promptArg = new Argument<string?>("prompt") { Description = "The mission's free-text input", Arity = ArgumentArity.ZeroOrOne };
    var inputOpt = new Option<string?>("--input") { Description = "Upload a file as the mission's input artifact" };
    var modeOpt = new Option<string?>("--mode") { Description = "Mission output mode, e.g. text or pdf" };
    var outOpt = new Option<string?>("--out") { Description = "Write the first returned artifact to this path" };

    var cmd = new Command("exec", "Run a hosted mission once and print the answer");
    cmd.Add(targetArg);
    cmd.Add(promptArg);
    cmd.Add(inputOpt);
    cmd.Add(modeOpt);
    cmd.Add(outOpt);

    cmd.SetCancellableAction((result, cancellationToken) =>
        ForgeExec.RunAsync(api, clients,
            result.GetValue(targetArg)!,
            result.GetValue(promptArg),
            result.GetValue(inputOpt),
            result.GetValue(modeOpt),
            result.GetValue(outOpt),
            cancellationToken));
    return cmd;
}

static void SaveRegistryCredential(string registry, string token)
{
    CredentialStore.SaveToken(registry, token);
    Console.WriteLine($"Credentials saved for {registry}");
}

// ---------------------------------------------------------------------------
// forge publish — package a mission directory and push it to an OCI registry (39.4)

static Command BuildPublishCommand()
{
    var refArg  = new Argument<string>("reference") { Description = "OCI reference, e.g. ghcr.io/katasec/forge-mission-guard:0.1.0" };
    var dirArg  = new Argument<DirectoryInfo?>("dir") { Description = "Mission directory (default: current dir)", Arity = ArgumentArity.ZeroOrOne };
    var descOpt = new Option<string?>("--description") { Description = "Human description (annotation)" };

    var cmd = new Command("publish", "Package a mission directory into a self-contained OCI artifact and push it");
    cmd.Add(refArg);
    cmd.Add(dirArg);
    cmd.Add(descOpt);

    cmd.SetCancellableAction(async (result, cancellationToken) =>
    {
        var reference = result.GetValue(refArg)!;
        var dir       = result.GetValue(dirArg) ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        var desc      = result.GetValue(descOpt);

        if (!File.Exists(Path.Combine(dir.FullName, "mission.mcl")))
        { Die($"No mission.mcl in {dir.FullName} — point at a mission directory."); return; }

        // registry/name:tag parsing is owned by OciReference (MissionRegistry).
        string registry, name, tag;
        try { (registry, name, tag) = OciReference.SplitTaggedTarget(reference); }
        catch (ArgumentException ex) { Die(ex.Message); return; }

        var bundle = MissionBundle.Pack(dir.FullName);
        var token  = CredentialStore.GetToken(registry);
        if (string.IsNullOrWhiteSpace(token))
            Console.Error.WriteLine($"Warning: no credential for {registry} (set FORGE_REGISTRY_TOKEN or 'forge registry login').");

        var annotations = desc is null
            ? null
            : new Dictionary<string, string> { ["org.opencontainers.image.description"] = desc };

        using var client = new OciClient(credential: token);
        var digest = await client.PushMissionAsync(registry, name, tag, bundle, annotations, cancellationToken);

        Console.WriteLine($"Published {registry}/{name}:{tag} ({bundle.Length} bytes)");
        Console.WriteLine($"Digest:  {digest}");
        Console.WriteLine($"Pinned:  {registry}/{name}@{digest}");
    });

    return cmd;
}

// ---------------------------------------------------------------------------
// forge serve

static Command BuildServeCommand(IOptions<WebSearchOptions> webSearch, IHttpClientFactory clients, ILogger logger)
{
    var agentArg = new Argument<FileInfo?>("agent") { Description = "Path to agent.yaml (default: agent.yaml)", Arity = ArgumentArity.ZeroOrOne };

    var cmd = new Command("serve", "Serve a mission as an OpenAI-compatible endpoint");
    cmd.Add(agentArg);

    cmd.SetCancellableAction(async (result, cancellationToken) =>
    {
        var agentFile = result.GetValue(agentArg)
            ?? new FileInfo(Path.GetFullPath("agent.yaml"));

        if (!agentFile.Exists) { Die($"Agent config not found: {agentFile.FullName}\nCreate an agent.yaml next to your mission.mcl."); return; }

        AgentConfig config;
        try { config = AgentConfigLoader.Load(agentFile.FullName); }
        catch (Exception ex) { Die($"Cannot read agent.yaml: {ex.Message}"); return; }

        var built = await TryBuildMissionServerAsync(webSearch, clients, logger, config, agentFile.DirectoryName!);
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
        catch (IOException ex) when (ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                                  || ex.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse })
        {
            Die($"Port {config.Port} is already in use. Stop the existing process or change the port in agent.yaml.");
        }
    });

    return cmd;
}

// ---------------------------------------------------------------------------
// forge claude — one command: serve the mission (Anthropic wire) + launch Claude Code
// wired to it + tear down on exit (Phase 42.2). In-process by default for speed;
// --container runs the same image the cloud runs for exact parity.

static Command BuildClaudeCommand(IOptions<WebSearchOptions> webSearch, IHttpClientFactory clients, ILogger logger)
{
    var targetArg    = new Argument<string?>("target") { Description = "Mission: a .mcl path, or empty (agent.yaml / lone .mcl in cwd)", Arity = ArgumentArity.ZeroOrOne };
    var containerOpt = new Option<bool>("--container")   { Description = "Run the mission as a Docker container (cloud parity) instead of in-process" };
    var portOpt      = new Option<int?>("--port")        { Description = "Pin the port instead of picking an ephemeral one" };
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
        if (!printEnv && !IsOnPath("claude"))
        {
            Die("claude CLI not found on PATH.\nInstall it first: npm install -g @anthropic-ai/claude-code  (https://claude.com/claude-code)");
            return;
        }

        var resolved = ResolveClaudeTarget(result.GetValue(targetArg));
        if (resolved is not { } target) return;
        var (missionPath, missionName) = target;

        if (!await EnsureInitializedAsync(missionPath, cancellationToken)) return;

        var port = result.GetValue(portOpt) ?? FindFreePort();
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
            if (started is null) return;
            teardown = started;
            mode     = "container";
        }
        else
        {
            var built = await TryBuildMissionServerAsync(webSearch, clients, logger, config, Path.GetDirectoryName(missionPath)!);
            if (built is not { } server) return;
            var app = server.App;
            await app.StartAsync(cancellationToken);
            teardown = async () => await app.StopAsync();
            mode     = "in-process";
        }

        var baseUrl = $"http://127.0.0.1:{port}";
        try
        {
            if (!await WaitUntilHealthyAsync(clients, baseUrl, timeoutMs: 60_000))
            {
                Die($"Mission endpoint did not become healthy at {baseUrl}.");
                return;
            }

            Console.Error.WriteLine($"✓ mission   {missionName,-15} {missionPath}");
            Console.Error.WriteLine($"✓ endpoint  /v1/messages    {baseUrl}   ({mode})");
            Console.Error.WriteLine($"✓ wired     ANTHROPIC_BASE_URL → forge");

            if (printEnv)
            {
                Console.WriteLine($"export ANTHROPIC_BASE_URL={baseUrl}");
                Console.WriteLine("export ANTHROPIC_API_KEY=forge-local");
                Console.Error.WriteLine("↳ serving until Ctrl-C…");
                await WaitForCtrlCAsync();
                return;
            }

            Console.Error.WriteLine("↳ launching claude…");
            Environment.ExitCode = await RunWiredClaudeAsync(
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
static (string MissionPath, string MissionName)? ResolveClaudeTarget(string? target)
{
    if (target is { } t && t.StartsWith('@'))
    {
        Die($"Mission handle '{t}' is a Runner catalog feature. Pass a local .mcl mission path.");
        return null;
    }

    if (target is { } path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) { Die($"Mission file not found: {full}"); return null; }
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
        catch (Exception ex) { Die($"Cannot read agent.yaml: {ex.Message}"); return null; }
    }

    var candidates = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.mcl");
    if (candidates.Length == 1)
        return (candidates[0], new DirectoryInfo(Directory.GetCurrentDirectory()).Name);

    Die(candidates.Length == 0
        ? "No mission found. Pass a .mcl path or run from a directory with a mission."
        : "Multiple .mcl files here — pass the one you mean: forge claude ./<mission>.mcl");
    return null;
}

// Missing mcl.lock ⇒ run `forge init` silently (local experts resolve without network).
static async Task<bool> EnsureInitializedAsync(string missionPath, CancellationToken cancellationToken)
{
    var lockPath = Path.Combine(Path.GetDirectoryName(missionPath)!, "mcl.lock");
    if (File.Exists(lockPath)) return true;

    Console.Error.WriteLine("↳ initialising mission (forge init)…");
    try { await RunInitAsync(new FileInfo(missionPath), refresh: false, cancellationToken); }
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
    if (!RunAndPrintPrereqs([docker])) return null;

    if (!await DockerCli.IsImagePresentAsync(runnerImage))
    {
        AnsiConsole.MarkupLine($"[yellow]Pulling {runnerImage}...[/]");
        await DockerCli.PullImageAsync(runnerImage);
    }
    await DockerCli.EnsureNetworkAsync("forge-net");

    var missionDir         = Path.GetDirectoryName(missionPath)!;
    var workspaceRoot      = FindGitRoot(missionDir) ?? missionDir;
    var missionInWorkspace = "/workspace/" + Path.GetRelativePath(workspaceRoot, missionPath).Replace('\\', '/');

    var containerName = $"forge-claude-{Guid.NewGuid():N}"[..20];
    await DockerCli.RunContainerAsync(
        name:          containerName,
        image:         runnerImage,
        cmd:           [],   // the image's entrypoint IS the runner; config rides in env
        // Forward the full provider-key table (docs/design/deploy.md) — the mounted mission's
        // forge.toml names whichever env(...) keys it wants, exactly as the in-process path
        // sees them.
        env:           [.. BuildEnvArray("MCL_API_KEY", "MCL_MODEL", "MCL_PROVIDER", "MCL_ENDPOINT",
                                         "OPENAI_API_KEY", "CLAUDE_API_KEY", "XAI_API_KEY",
                                         "GROK_API_KEY", "GOOGLE_SEARCH_API_KEY"),
                        $"MissionFile={missionInWorkspace}"],
        binds:         [$"{workspaceRoot}:/workspace"],
        hostPort:      hostPort,
        containerPort: config.Port,
        network:       "forge-net");

    return async () => await DockerCli.StopAndRemoveAsync(containerName);
}

static async Task<bool> WaitUntilHealthyAsync(IHttpClientFactory clients, string baseUrl, int timeoutMs)
{
    var http = clients.CreateClient(CliHttpClients.Health);
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, baseUrl + "/"));
            if (response.IsSuccessStatusCode) return true;
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
        }
        await Task.Delay(250);
    }
    return false;
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

static async Task WaitForCtrlCAsync()
{
    var stopped = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.TrySetResult(); };
    await stopped.Task;
}

static int FindFreePort()
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static bool IsOnPath(string binary) =>
    (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator)
        .Any(dir => File.Exists(Path.Combine(dir, binary))
                 || File.Exists(Path.Combine(dir, binary + ".exe")));

// Loads the mission behind an AgentConfig and builds the wire server, ready to start.
// Shared by `forge serve` (blocking RunAsync) and `forge claude` (background StartAsync).
// Returns null after reporting the error (Die) — callers just return.
static async Task<(WebApplication App, string MissionPath, bool AnthropicDoor, bool OpenAiDoor)?> TryBuildMissionServerAsync(
    IOptions<WebSearchOptions> webSearchOptions, IHttpClientFactory clients, ILogger logger, AgentConfig config, string agentDir)
{
    var missionPath = Path.GetFullPath(Path.Combine(agentDir, config.Mission));
    var lockPath    = Path.Combine(Path.GetDirectoryName(missionPath)!, "mcl.lock");

    if (!File.Exists(missionPath)) { Die($"Mission file not found: {missionPath}"); return null; }
    if (!File.Exists(lockPath))    { Die("MCL007 Mission not initialised — run 'forge init' first."); return null; }

    var source = await TryReadFile(missionPath, CancellationToken.None);
    if (source is null) return null;

    var ast = TryParse(source, missionPath);
    if (ast is null) return null;

    LockFile lockFile;
    try { lockFile = LockFileIO.Read(lockPath); }
    catch (Exception ex) { Die($"Cannot read mcl.lock: {ex.Message}"); return null; }

    Dictionary<string, ExpertDefinition> expertDefs;
    try { expertDefs = ExpertLoader.LoadFromLockFile(lockFile, Path.GetDirectoryName(missionPath)!); }
    catch (AggregateExpertLoadException ex) { foreach (var e in ex.Errors) ReportExpertDiagnostic(e); throw new ForgeExitException(1); }
    catch (ExpertLoadException ex)           { ReportExpertDiagnostic(ex); throw new ForgeExitException(1); }

    if (!TryValidate(ast, expertDefs, missionFilePath: missionPath)) return null;

    Dictionary<string, object> seedContext;
    try { seedContext = ContextBuilder.Seed(ast, new Dictionary<string, string>()); }
    catch (InvalidOperationException ex) { Die(ex.Message); return null; }

    ForgeManifest? manifest = null;
    try { manifest = ForgeTomlReader.TryRead(missionPath); }
    catch (ForgeTomlException ex) { Die(ex.Message); return null; }

    var runners = BuildRunners(manifest, seedContext);
    if (runners is null) return null;

    var defaultRunner = runners.TryGetValue("default", out var dr)
        ? dr
        : runners.Values.First();

    // Wire doors (42.4): ONE app serves every enabled wire. `wire:` in agent.yaml is a disable
    // switch, owned by ForgeServe.WirePolicy; each enabled door gets its own MissionChatClient over
    // the same mission core, built by Core's MissionServerFactory — the Anthropic wire hands the
    // mission the FULL conversation (context["conversation"]/["system"], goal = last text block of
    // the last user message); the OpenAI wire keeps the legacy last-turn behaviour so existing users
    // don't regress (42.1 decision, unchanged).
    if (!WirePolicy.TryParse(config.Wire, out var anthropicEnabled, out var openAiEnabled, out var wireError))
    {
        Die(wireError!);
        return null;
    }

    // kind:search backend (41.2) — same seam as `forge run` and the cloud runner; null when
    // XAI_API_KEY is unset, so missions without kind:search are unaffected.
    var webSearch = ProviderClientBuilder.BuildWebSearch(webSearchOptions, clients);

    var (anthropicDoor, openAiDoor) = MissionServerFactory.BuildDoors(
        ast, expertDefs, defaultRunner, anthropicEnabled, openAiEnabled, webSearch);

    // Aux passthrough (42.3 §0): client housekeeping (title-gen, state-check) is answered by a
    // plain provider model; the mission never runs — and never bills — for those. The aux client is
    // OPTIONAL: if its provider can't be built (missing key, bad profile), serving deliberately
    // degrades to a null aux client and the server answers housekeeping with its canned replies;
    // the failure is logged as a Warning.
    IChatClient? auxClient = null;
    if (anthropicDoor is not null && ResolveDefaultProfile(manifest, seedContext) is { } auxProfile)
    {
        try { auxClient = ForgeMission.ChatClients.ChatClients.BuildChatClient(auxProfile); }
        catch (Exception ex) { logger.AuxClientUnavailable(ex, auxProfile.Provider); auxClient = null; }
    }

    var app = ForgeServe.BuildApp(config.Id, config.Port, anthropicDoor, openAiDoor, auxClient);

    return (app, missionPath, anthropicDoor is not null, openAiDoor is not null);
}

// ---------------------------------------------------------------------------
// forge clean

// ---------------------------------------------------------------------------
// Helpers

static async Task<string?> TryReadFile(string path, CancellationToken cancellationToken)
{
    try { return await File.ReadAllTextAsync(path, cancellationToken); }
    catch (Exception ex) when (ex is not OperationCanceledException) { Die($"Cannot read file '{path}': {ex.Message}"); return null; }
}

static MclProgram? TryParse(string source, string filePath)
{
    var result = MclParser.TryParse(source);
    if (!result.Success)
    {
        foreach (var d in result.Diagnostics)
            ReportDiagnostic(source, filePath, d);
        throw new ForgeExitException(1);
    }
    return result.Ast!;
}

static void ReportDiagnostic(string source, string filePath, Diagnostic d)
{
    var lines      = source.Split('\n');
    var sourceLine = d.Line >= 1 && d.Line <= lines.Length
        ? lines[d.Line - 1].TrimEnd('\r')
        : string.Empty;

    var lineNumStr = d.Line.ToString();
    var gutter     = new string(' ', lineNumStr.Length);
    var col        = Math.Max(0, d.Column);
    var caretLen   = d.EndColumn > d.Column ? d.EndColumn - d.Column : 1;
    var carets     = new string('^', caretLen);
    var spaces     = new string(' ', Math.Min(col, sourceLine.Length));

    var e = Console.Error;
    e.WriteLine($"{BoldRed("error")}{Bold($": {d.Message}")}");
    e.WriteLine($"  {BoldBlue("-->")} {filePath}:{d.Line}:{col + 1}");
    e.WriteLine($"{BoldBlue($"{gutter}   |")}");
    e.WriteLine($"{BoldBlue($"{lineNumStr}   |")} {sourceLine}");
    e.WriteLine($"{BoldBlue($"{gutter}   |")} {spaces}{BoldRed(carets)}");
    e.WriteLine($"{BoldBlue($"{gutter}   |")}");
}

static void ReportExpertDiagnostic(ExpertLoadException ex)
{
    // No file path — fall back to plain Die-style output
    if (ex.FilePath is null || ex.Line == 0) { Die(ex.Message); return; }

    var source     = File.Exists(ex.FilePath) ? File.ReadAllText(ex.FilePath) : string.Empty;
    var lines      = source.Split('\n');
    var sourceLine = ex.Line >= 1 && ex.Line <= lines.Length
        ? lines[ex.Line - 1].TrimEnd('\r')
        : string.Empty;

    var lineNumStr = ex.Line.ToString();
    var gutter     = new string(' ', lineNumStr.Length);
    var col        = Math.Max(0, ex.Column);
    var caretLen   = ex.EndColumn > ex.Column ? ex.EndColumn - ex.Column : 1;
    var carets     = new string('^', caretLen);
    var spaces     = new string(' ', Math.Min(col, sourceLine.Length));

    var e = Console.Error;
    e.WriteLine($"{BoldRed("error")}{Bold($": {ex.Message}")}");
    e.WriteLine($"  {BoldBlue("-->")} {ex.FilePath}:{ex.Line}:{col + 1}");
    e.WriteLine($"{BoldBlue($"{gutter}   |")}");
    e.WriteLine($"{BoldBlue($"{lineNumStr}   |")} {sourceLine}");
    e.WriteLine($"{BoldBlue($"{gutter}   |")} {spaces}{BoldRed(carets)}");
    e.WriteLine($"{BoldBlue($"{gutter}   |")}");
}

// ANSI helpers — bold+color when stderr is a terminal and NO_COLOR is unset
static bool UseAnsi() =>
    !Console.IsErrorRedirected &&
    Environment.GetEnvironmentVariable("NO_COLOR") is null;

static string Bold(string s)       => UseAnsi() ? $"\x1b[1m{s}\x1b[0m"    : s;
static string BoldRed(string s)    => UseAnsi() ? $"\x1b[1;31m{s}\x1b[0m" : s;
static string BoldYellow(string s) => UseAnsi() ? $"\x1b[1;33m{s}\x1b[0m" : s;
static string BoldBlue(string s)   => UseAnsi() ? $"\x1b[1;34m{s}\x1b[0m" : s;

static Dictionary<string, ExpertDefinition>? TryLoadExperts(string expertsDir)
{
    if (!Directory.Exists(expertsDir)) { Die($"Experts directory not found: {expertsDir}"); return null; }
    try { return new ExpertLoader(expertsDir).LoadAll(); }
    catch (AggregateExpertLoadException ex) { foreach (var e in ex.Errors) ReportExpertDiagnostic(e); throw new ForgeExitException(1); }
    catch (ExpertLoadException ex) { ReportExpertDiagnostic(ex); throw new ForgeExitException(1); }
}

static bool TryValidate(
    MclProgram ast,
    Dictionary<string, ExpertDefinition> experts,
    bool contractErrorsAreFatal = false,
    string? missionFilePath = null)
{
    try
    {
        ExpertLoader.Validate(ast, experts, Console.Error, contractErrorsAreFatal, missionFilePath);
        return true;
    }
    catch (AggregateExpertLoadException ex) { foreach (var e in ex.Errors) ReportExpertDiagnostic(e); return false; }
    catch (ExpertLoadException ex)          { ReportExpertDiagnostic(ex); return false; }
}

// Builds the full runner dictionary from forge.toml profiles.
// Falls back to let-binding context for the "default" runner if no forge.toml.
static IReadOnlyDictionary<string, IExpertRunner>? BuildRunners(
    ForgeManifest? manifest,
    Dictionary<string, object> seedContext)
{
    var runners = new Dictionary<string, IExpertRunner>(StringComparer.Ordinal);

    // Build a runner per declared profile.
    if (manifest?.Providers is { Count: > 0 } profiles)
    {
        foreach (var (name, profile) in profiles)
        {
            try { runners[name] = ForgeMission.ChatClients.ChatClients.Build(profile); }
            catch (Exception ex) { Die($"Cannot initialise provider profile '{name}': {ex.Message}"); return null; }
        }
    }

    // If no "default" profile came from forge.toml, fall back to let-binding context.
    if (!runners.ContainsKey("default"))
    {
        var profile = MergeDefaultProfile(manifest, seedContext);

        if (string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            Die("No API key found. Add [providers.default] to forge.toml with apiKey = env(\"MCL_API_KEY\").");
            return null;
        }
        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            Die("No model found. Add [providers.default] to forge.toml with model = env(\"MCL_MODEL\", \"gpt-4o-mini\").");
            return null;
        }

        try { runners["default"] = ForgeMission.ChatClients.ChatClients.Build(profile); }
        catch (Exception ex) { Die($"Cannot initialise default provider: {ex.Message}"); return null; }
    }

    return runners;
}

// The default provider profile, resolved the same way BuildRunners falls back:
// forge.toml [providers.default] first, then the mission's let-binding context.
// Returns null when either apiKey or model is missing.
static ProviderProfile? ResolveDefaultProfile(ForgeManifest? manifest, Dictionary<string, object> seedContext)
{
    var profile = MergeDefaultProfile(manifest, seedContext);
    if (string.IsNullOrWhiteSpace(profile.ApiKey) || string.IsNullOrWhiteSpace(profile.Model)) return null;
    return profile;
}

// Layers mission context over the forge.toml [providers.default] block (Core owns the merge).
// The result may carry an empty ApiKey/Model; the caller decides how a missing value is reported.
static ProviderProfile MergeDefaultProfile(ForgeManifest? manifest, Dictionary<string, object> seedContext)
{
    var declared = manifest?.Providers?.GetValueOrDefault("default");
    return ProviderProfileResolver.ResolveDefault(declared, key => GetContextString(seedContext, key));
}

static string ExpertTemplate(string name) => $"""
    ---
    name: {name}
    version: 0.1.0
    description: [One-line description of what this expert does]
    input: [Input description]
    output: [Output description]
    ---

    You are a [role description].

    Your job is to:
    1. [Step one]
    2. [Step two]
    3. [Step three]

    Produce [output description].
    """;

static string? GetContextString(Dictionary<string, object> ctx, string key)
    => ctx.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;

static FileInfo ResolveMission(FileInfo? arg)
    => new FileInfo(Path.GetFullPath(arg?.FullName ?? "mission.mcl"));

static void Die(string message)
{
    Console.Error.WriteLine($"{BoldRed("error")}{Bold($": {message}")}");
    throw new ForgeExitException(1);
}

// ---------------------------------------------------------------------------
// forge provider

static Command BuildProviderCommand()
{
    var providerCmd = new Command("provider", "Manage LLM provider profiles");
    providerCmd.Add(BuildProviderListCommand());
    providerCmd.Add(BuildProviderScaffoldCommand());
    return providerCmd;
}

static Command BuildProviderListCommand()
{
    var cmd = new Command("list", "List all known LLM providers");
    cmd.SetCancellableAction((_, _) =>
    {
        Console.WriteLine("Known providers:\n");
        Console.WriteLine($"  {"NAME",-12} {"REQUIRED FIELDS",-30} NOTES");
        Console.WriteLine($"  {"----",-12} {"---------------",-30} -----");
        foreach (var p in ForgeMission.ChatClients.ProviderCatalog.Entries)
            Console.WriteLine($"  {p.Name,-12} {p.RequiredFields,-30} {p.Notes}");
        Console.WriteLine("\nRun 'forge provider scaffold <name>' to generate a forge.toml block.");
        return Task.CompletedTask;
    });
    return cmd;
}

static Command BuildProviderScaffoldCommand()
{
    var nameArg  = new Argument<string>("name") { Description = "Provider name (openai, anthropic, azure, ollama)" };
    var writeOpt = new Option<bool>("--write") { Description = "Append the block to forge.toml instead of printing it" };

    var cmd = new Command("scaffold", "Generate a ready-to-paste forge.toml provider block");
    cmd.Add(nameArg);
    cmd.Add(writeOpt);

    cmd.SetCancellableAction(async (result, cancellationToken) =>
    {
        var name  = result.GetValue(nameArg)!.ToLowerInvariant();
        var write = result.GetValue(writeOpt);

        var block = ForgeMission.ChatClients.ProviderCatalog.Find(name)?.ScaffoldBlock;

        if (block is null)
        {
            Die($"Unknown provider '{name}'. Run 'forge provider list' to see known providers.");
            return;
        }

        if (write)
        {
            await File.AppendAllTextAsync("forge.toml", $"\n{block}\n", cancellationToken);
            Console.WriteLine("Appended to forge.toml.");
        }
        else
        {
            Console.WriteLine(block);
        }
    });

    return cmd;
}

// ---------------------------------------------------------------------------
// forge mcp

// Thin shell: loads and validates the mission with the shared CLI helpers, then hands the prepared
// mission to McpCommand, which owns the stdio MCP server. Runners are built lazily per tool call;
// the server runs until stdin closes or the command's Ctrl+C/SIGTERM token is cancelled.
static Command BuildMcpCommand(ILoggerFactory loggerFactory, IHttpClientFactory clients, IOptions<WebSearchOptions> webSearch)
{
    var missionArg = new Argument<FileInfo?>("mission")
    {
        Description = "Path to the .mcl mission file (default: mission.mcl)",
        Arity = ArgumentArity.ZeroOrOne
    };

    var cmd = new Command("mcp", "Expose a mission as a stdio MCP server (for Claude Desktop and MCP-aware tools)");
    cmd.Add(missionArg);

    cmd.SetCancellableAction(async (result, ct) =>
    {
        var mission    = ResolveMission(result.GetValue(missionArg));
        var missionDir = mission.DirectoryName!;
        var lockPath   = Path.Combine(missionDir, "mcl.lock");

        if (!File.Exists(lockPath)) { Die("MCL007 Mission not initialised — run 'forge init' first."); return; }

        var source = await TryReadFile(mission.FullName, ct);
        if (source is null) return;

        var ast = TryParse(source, mission.FullName);
        if (ast is null) return;

        ForgeManifest? manifest = null;
        try { manifest = ForgeTomlReader.TryRead(mission.FullName); }
        catch (ForgeTomlException ex) { Die(ex.Message); return; }

        LockFile lockFile;
        try { lockFile = LockFileIO.Read(lockPath); }
        catch (Exception ex) { Die($"Cannot read mcl.lock: {ex.Message}"); return; }

        Dictionary<string, ExpertDefinition> expertDefs;
        try { expertDefs = ExpertResolver.ResolveAll(lockFile, missionDir, verbose: null, warnings: Console.Error); }
        catch (AggregateExpertLoadException ex) { foreach (var e in ex.Errors) ReportExpertDiagnostic(e); throw new ForgeExitException(1); }
        catch (ExpertLoadException ex)           { ReportExpertDiagnostic(ex); throw new ForgeExitException(1); }

        if (!TryValidate(ast, expertDefs, contractErrorsAreFatal: true, mission.FullName)) return;

        var firstMission = ast.Declarations.OfType<MissionDeclaration>().FirstOrDefault();
        if (firstMission is null) { Die("No mission declaration found in mission file."); return; }

        await McpCommand.ServeAsync(loggerFactory, clients, webSearch, firstMission, ast, expertDefs, manifest, mission.FullName,
            callSeedContext => BuildRunners(manifest, callSeedContext), ct);
    });

    return cmd;
}

// ---------------------------------------------------------------------------
// Docker helpers

static string? FindGitRoot(string startDir)
{
    var dir = startDir;
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
        dir = Path.GetDirectoryName(dir);
    }
    return null;
}

static string[] BuildEnvArray(params string[] vars) =>
    vars
        .Select(v => (Name: v, Value: Environment.GetEnvironmentVariable(v)))
        .Where(x => x.Value is not null)
        .Select(x => $"{x.Name}={x.Value}")
        .ToArray();

static bool RunAndPrintPrereqs(IEnumerable<PrereqCheck> checks)
{
    var results = DockerPrereqChecker.Evaluate(checks);
    AnsiConsole.MarkupLine("\n [bold]Checking prerequisites...[/]\n");

    var table = new Table();
    table.AddColumn("Requirement");
    table.AddColumn("Status");
    table.AddColumn("Detail");
    table.Border(TableBorder.Simple);

    foreach (var result in results)
    {
        var (statusMarkup, detailMarkup) = result.Status switch
        {
            PrereqStatus.Pass    => ("[green]✓ pass[/]", result.Detail),
            PrereqStatus.Fail    => ("[red]✗ fail[/]", result.Detail),
            PrereqStatus.Skipped => ("[grey]– skip[/]", "[grey]–[/]"),
            _                    => ("?", result.Detail)
        };
        table.AddRow(result.Label, statusMarkup, detailMarkup);
    }

    AnsiConsole.Write(table);

    var passed = results.All(result => result.Status != PrereqStatus.Fail);
    if (!passed)
        AnsiConsole.MarkupLine("[red]Prerequisites not met. Cannot continue.[/]");

    return passed;
}
