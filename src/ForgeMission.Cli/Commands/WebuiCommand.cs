using System.CommandLine;
using ForgeMission.Core.Resolution;
using ForgeMission.Docker;
using ForgeMission.Cli.Services;
using Spectre.Console;

namespace ForgeMission.Cli.Commands;

// forge webui start/stop — run Open WebUI in Docker, pre-wired to talk to a running forge agent
// container over the shared forge-net network. Docker orchestration lives in ForgeMission.Docker;
// prerequisite printing stays with the shared CLI helpers in Program and reaches this surface
// through Build's die/runAndPrintPrereqs seams.
internal static class WebuiCommand
{
    internal static Command Build(
        Action<string> die,
        Func<IEnumerable<PrereqCheck>, bool> runAndPrintPrereqs)
    {
        var webuiCmd = new Command("webui", "Manage Open WebUI connected to a forge agent");
        webuiCmd.Add(BuildStartCommand(die, runAndPrintPrereqs));
        webuiCmd.Add(BuildStopCommand());
        return webuiCmd;
    }

    static Command BuildStartCommand(
        Action<string> die,
        Func<IEnumerable<PrereqCheck>, bool> runAndPrintPrereqs)
    {
        var agentFileOpt = new Option<string>("--agent-file") { Description = "Path to agent.yaml", DefaultValueFactory = _ => "./agent.yaml" };
        var portOpt = new Option<int>("--port") { Description = "Host port for Open WebUI", DefaultValueFactory = _ => 3000 };

        var cmd = new Command("start", "Start Open WebUI pre-configured to connect to the forge agent");
        cmd.Add(agentFileOpt);
        cmd.Add(portOpt);

        cmd.SetCancellableAction(async (result, _) =>
        {
            var agentFile = result.GetValue(agentFileOpt)!;
            var webuiPort = result.GetValue(portOpt);
            var agentFileFull = Path.GetFullPath(agentFile);

            if (!File.Exists(agentFileFull))
            {
                die($"Agent config not found: {agentFileFull}");
                return;
            }

            AgentConfig config;
            try { config = AgentConfigLoader.Load(agentFileFull); }
            catch (Exception ex) { die($"Cannot read agent.yaml: {ex.Message}"); return; }

            const string webuiImage = "ghcr.io/open-webui/open-webui:main";

            var prereqs = new[]
            {
                await DockerPrereqChecker.CheckDockerAsync(),
                DockerPrereqChecker.CheckFileExists(agentFileFull, "agent.yaml"),
            };

            if (!runAndPrintPrereqs(prereqs))
                throw new ForgeExitException(1);

            var agentContainerName = $"forge-agent-{config.Id}";
            var agentUrl = $"http://{agentContainerName}:{config.Port}/v1";

            if (!await DockerCli.IsContainerRunningAsync(agentContainerName))
            {
                AnsiConsole.MarkupLine($"[yellow]Agent container {agentContainerName} is not running.[/]");
                AnsiConsole.MarkupLine("[yellow]Run 'forge agent start' first.[/]");
                throw new ForgeExitException(1);
            }

            await DockerCli.EnsureNetworkAsync("forge-net");

            if (await DockerCli.ContainerExistsAsync("open-webui"))
            {
                AnsiConsole.MarkupLine("[yellow]open-webui container already exists. Stop it first.[/]");
                throw new ForgeExitException(1);
            }

            if (!await DockerCli.IsImagePresentAsync(webuiImage))
            {
                AnsiConsole.MarkupLine("[grey]Pulling open-webui image (first run may take a minute)...[/]");
                await DockerCli.PullImageAsync(webuiImage);
            }

            await DockerCli.RunContainerAsync(
                name:          "open-webui",
                image:         webuiImage,
                cmd:           [],
                env:           [$"OPENAI_API_BASE_URL={agentUrl}", "OPENAI_API_KEY=forge"],
                binds:         ["open-webui-data:/app/backend/data"],
                hostPort:      webuiPort,
                containerPort: 8080,
                network:       "forge-net");

            AnsiConsole.MarkupLine("[green]✓[/] Open WebUI started");
            AnsiConsole.MarkupLine($"  URL      : http://localhost:{webuiPort}");
            AnsiConsole.MarkupLine($"  Agent    : {agentUrl}");
            AnsiConsole.MarkupLine($"  Container: open-webui");
        });

        return cmd;
    }

    static Command BuildStopCommand()
    {
        var cmd = new Command("stop", "Stop and remove the Open WebUI container");

        cmd.SetCancellableAction(async (_, _) =>
        {
            await DockerCli.StopAndRemoveAsync("open-webui");
            AnsiConsole.MarkupLine("[green]✓[/] Open WebUI stopped");
        });

        return cmd;
    }
}
