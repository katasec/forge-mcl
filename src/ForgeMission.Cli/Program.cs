using System.CommandLine;
using ForgeMission.Cli.Commands;
using ForgeMission.Cli.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// No @file response-file expansion — @handles (forge claude @websearch) are arguments.
// One top-level exit/exception handler owns every exit code: helpers signal a deliberate exit by
// throwing ForgeExitException (see CliDiagnostics.Die) rather than calling Environment.Exit deep in
// the stack, so teardown/finally runs on the way out. System.CommandLine's own exception handler is
// disabled so those exceptions reach this handler; it also keeps the library's prior behaviour for
// any unexpected exception (print it, exit 1). Ctrl+C/SIGTERM reaches every action as its token and
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
    rootCommand.Add(MissionDevCommands.BuildInitCommand());
    rootCommand.Add(HostedMissionCommands.BuildRunCommand(api, clients));
    rootCommand.Add(MissionDevCommands.BuildValidateCommand());
    rootCommand.Add(MissionDevCommands.BuildListCommand());
    rootCommand.Add(MissionDevCommands.BuildExpertCommand());
    rootCommand.Add(AuthCommands.BuildRegistryCommand());
    rootCommand.Add(AuthCommands.BuildLoginCommand(services.GetRequiredService<IOptions<PlatformLoginOptions>>(), clients));
    rootCommand.Add(AuthCommands.BuildWhoamiCommand(clients));
    rootCommand.Add(AuthCommands.BuildLogoutCommand());
    rootCommand.Add(HostedMissionCommands.BuildExecCommand(api, clients));
    rootCommand.Add(ForgeChat.BuildCommand(services.GetRequiredService<IOptions<ForgeSettings>>(), clients)); // forge chat (53.2; --hands, Phase 55)
    rootCommand.Add(ForgeProject.BuildCommand(clients));
    rootCommand.Add(HostedMissionCommands.BuildPublishCommand());
    rootCommand.Add(CleanCommand.Build());
    rootCommand.Add(ServeCommands.BuildServeCommand(webSearch, clients, logger));
    rootCommand.Add(ServeCommands.BuildClaudeCommand(webSearch, clients, logger));
    rootCommand.Add(ConnectCommand.Build(ServeCommands.ResolveClaudeTarget, ServeCommands.EnsureInitializedAsync,
        (config, agentDir, cancellationToken) => MissionServerBuilder.TryBuildMissionServerAsync(webSearch, clients, logger, config, agentDir, cancellationToken), CliDiagnostics.Die));
    rootCommand.Add(AgentCommand.Build(CliDiagnostics.Die, CliPrereqs.RunAndPrintPrereqs, CliPrereqs.BuildEnvArray, CliPrereqs.FindGitRoot));
    rootCommand.Add(WebuiCommand.Build(CliDiagnostics.Die, CliPrereqs.RunAndPrintPrereqs));
    rootCommand.Add(ProviderCommand.BuildProviderCommand());
    rootCommand.Add(McpCommand.BuildMcpCommand(services.GetRequiredService<ILoggerFactory>(), clients, webSearch));
    return rootCommand;
}
