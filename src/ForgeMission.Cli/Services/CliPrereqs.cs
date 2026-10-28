using ForgeMission.Docker;
using Spectre.Console;

namespace ForgeMission.Cli.Services;

// Shared CLI prerequisite/environment helpers: git-root discovery, selective env-var capture,
// PATH lookup, and the Spectre table that prints Docker prerequisite results. Used by the serve /
// claude / agent / webui surfaces, and reached by AgentCommand and WebuiCommand through their Build
// seams.
internal static class CliPrereqs
{
    internal static string? FindGitRoot(string startDir)
    {
        var dir = startDir;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    internal static string[] BuildEnvArray(params string[] vars) =>
        vars
            .Select(v => (Name: v, Value: Environment.GetEnvironmentVariable(v)))
            .Where(x => x.Value is not null)
            .Select(x => $"{x.Name}={x.Value}")
            .ToArray();

    internal static bool IsOnPath(string binary) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Combine(dir, binary))
                     || File.Exists(Path.Combine(dir, binary + ".exe")));

    internal static bool RunAndPrintPrereqs(IEnumerable<PrereqCheck> checks)
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
}
