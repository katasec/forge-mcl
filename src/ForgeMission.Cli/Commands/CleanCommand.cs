using System.CommandLine;
using ForgeMission.Cli.Services;
using ForgeMission.MissionRegistry;

namespace ForgeMission.Cli.Commands;

// forge clean — remove the cached OCI experts and missions under ~/.forge, optionally scoped to
// one registry host. ForgeCache owns the eviction; this command only reports the result.
internal static class CleanCommand
{
    internal static Command Build()
    {
        var registryOpt = new Option<string?>("--registry") { Description = "Limit to a specific registry host (e.g. ghcr.io)" };

        var cmd = new Command("clean", "Remove cached OCI experts and missions from ~/.forge/experts and ~/.forge/missions");
        cmd.Add(registryOpt);

        cmd.SetCancellableAction((result, _) => Task.FromResult(Run(result.GetValue(registryOpt))));

        return cmd;
    }

    static int Run(string? registry)
    {
        int count;
        try { count = ForgeCache.Clear(registry); }
        catch (ArgumentException failure) { CliDiagnostics.Die(failure.Message); return 1; }

        var scope = registry is null ? "" : $" for {registry}";
        Console.WriteLine(count == 0
            ? $"Nothing to clean (no cached experts or missions{scope})."
            : $"Removed {count} cached file(s) from the experts and missions caches{scope}.");
        return 0;
    }
}
