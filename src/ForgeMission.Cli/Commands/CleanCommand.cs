using System.CommandLine;
using ForgeMission.MissionRegistry;

namespace ForgeMission.Cli;

// forge clean — remove cached OCI experts from ~/.forge/experts, optionally scoped to one
// registry host. Self-contained: the cache root lives in ForgeMission.MissionRegistry and the
// only side effect is the recursive delete of that directory.
internal static class CleanCommand
{
    internal static Command Build()
    {
        var registryOpt = new Option<string?>("--registry") { Description = "Limit to a specific registry host (e.g. ghcr.io)" };

        var cmd = new Command("clean", "Remove cached OCI experts from ~/.forge/experts");
        cmd.Add(registryOpt);

        cmd.SetAction(result =>
        {
            var registry = result.GetValue(registryOpt);
            var target   = registry is not null
                ? Path.Combine(ForgeCache.ExpertsRoot, registry)
                : ForgeCache.ExpertsRoot;

            if (!Directory.Exists(target))
            {
                Console.WriteLine($"Nothing to clean ({target} does not exist).");
                return;
            }

            var files = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
            Directory.Delete(target, recursive: true);

            Console.WriteLine($"Removed {files.Length} cached expert file(s) from {target}");
        });

        return cmd;
    }
}
