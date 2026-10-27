using System.CommandLine;
using ForgeMission.Core.Resolution;
using ForgeMission.Docker;
using Spectre.Console;

namespace ForgeMission.Cli;

// forge agent start/stop — run `forge serve` inside a Docker container on the shared forge-net
// network. Docker orchestration lives in ForgeMission.Docker; prerequisite printing, env capture
// and git-root resolution stay with the shared CLI helpers in Program and reach this surface
// through Build's seam parameters.
internal static class AgentCommand
{
    internal static Command Build(
        Action<string> die,
        Func<IEnumerable<PrereqCheck>, bool> runAndPrintPrereqs,
        Func<string[], string[]> buildEnvArray,
        Func<string, string?> findGitRoot)
    {
        var agentCmd = new Command("agent", "Manage forge agents running in Docker");
        agentCmd.Add(BuildStartCommand(die, runAndPrintPrereqs, buildEnvArray, findGitRoot));
        agentCmd.Add(BuildStopCommand(die));
        return agentCmd;
    }

    static Command BuildStartCommand(
        Action<string> die,
        Func<IEnumerable<PrereqCheck>, bool> runAndPrintPrereqs,
        Func<string[], string[]> buildEnvArray,
        Func<string, string?> findGitRoot)
    {
        var agentFileOpt = new Option<string?>("--agent-file") { Description = "Path to agent.yaml (default: ./agent.yaml)" };

        var cmd = new Command("start", "Start forge serve inside a Docker container");
        cmd.Add(agentFileOpt);

        cmd.SetCancellableAction(async (result, _) =>
        {
            var agentFile = result.GetValue(agentFileOpt) ?? "./agent.yaml";
            var agentFileFull = Path.GetFullPath(agentFile);

            if (!File.Exists(agentFileFull))
            {
                die($"Agent config not found: {agentFileFull}");
                return;
            }

            AgentConfig config;
            try { config = AgentConfigLoader.Load(agentFileFull); }
            catch (Exception ex) { die($"Cannot read agent.yaml: {ex.Message}"); return; }

            const string forgeImage = "ghcr.io/katasec/forge:latest";

            var prereqs = new[]
            {
                await DockerPrereqChecker.CheckDockerAsync(),
                DockerPrereqChecker.CheckPort(config.Port),
                DockerPrereqChecker.CheckFileExists(agentFileFull, "agent.yaml"),
            };

            if (!runAndPrintPrereqs(prereqs))
                throw new ForgeExitException(1);

            if (!await DockerCli.IsImagePresentAsync(forgeImage))
            {
                AnsiConsole.MarkupLine($"[yellow]Pulling {forgeImage}...[/]");
                await DockerCli.PullImageAsync(forgeImage);
            }

            await DockerCli.EnsureNetworkAsync("forge-net");

            var containerName = $"forge-agent-{config.Id}";
            if (await DockerCli.ContainerExistsAsync(containerName))
            {
                AnsiConsole.MarkupLine($"[yellow]Container {containerName} already exists. Stop it first.[/]");
                throw new ForgeExitException(1);
            }

            // Mount the git root so relative mission paths in agent.yaml resolve correctly.
            // Fall back to the agent file's directory if not in a git repo.
            var agentDir     = Path.GetDirectoryName(agentFileFull)!;
            var workspaceRoot = findGitRoot(agentDir) ?? agentDir;
            var agentInWorkspace = "/workspace/" + Path.GetRelativePath(workspaceRoot, agentFileFull).Replace('\\', '/');

            await DockerCli.RunContainerAsync(
                name:          containerName,
                image:         forgeImage,
                cmd:           ["serve", agentInWorkspace],
                env:           [.. buildEnvArray(["MCL_API_KEY", "MCL_MODEL", "MCL_PROVIDER", "MCL_ENDPOINT"]),
                               "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1"],
                binds:         [$"{workspaceRoot}:/workspace"],
                hostPort:      config.Port,
                containerPort: config.Port,
                network:       "forge-net");

            AnsiConsole.MarkupLine($"[green]✓[/] Agent [bold]{config.Id}[/] started");
            AnsiConsole.MarkupLine($"  Endpoint : http://localhost:{config.Port}/v1");
            AnsiConsole.MarkupLine($"  Container: {containerName}");
            AnsiConsole.MarkupLine($"  Network  : forge-net");
        });

        return cmd;
    }

    static Command BuildStopCommand(Action<string> die)
    {
        var agentFileOpt = new Option<string?>("--agent-file") { Description = "Path to agent.yaml (default: ./agent.yaml)" };

        var cmd = new Command("stop", "Stop and remove the forge agent container");
        cmd.Add(agentFileOpt);

        cmd.SetCancellableAction(async (result, _) =>
        {
            var agentFile = result.GetValue(agentFileOpt) ?? "./agent.yaml";
            var agentFileFull = Path.GetFullPath(agentFile);

            if (!File.Exists(agentFileFull))
            {
                die($"Agent config not found: {agentFileFull}");
                return;
            }

            AgentConfig config;
            try { config = AgentConfigLoader.Load(agentFileFull); }
            catch (Exception ex) { die($"Cannot read agent.yaml: {ex.Message}"); return; }

            var containerName = $"forge-agent-{config.Id}";
            await DockerCli.StopAndRemoveAsync(containerName);
            AnsiConsole.MarkupLine($"[green]✓[/] Agent [bold]{config.Id}[/] stopped");
        });

        return cmd;
    }
}
