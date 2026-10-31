using System.CommandLine;
using ForgeMission.Cli.Services;
using ForgeMission.Core.Resolution;
using ForgeMission.MissionRegistry;
using Katasec.OciClient;

namespace ForgeMission.Cli.Commands;

// Hosted-mission and packaging commands: `forge run` (run a mission as a cloud conversation in a
// project) and `forge publish` (package a mission directory into an OCI artifact and push it). The
// cloud run lives in ForgeDurableRun; OCI packaging/push lives in MissionRegistry and
// Katasec.OciClient. This surface owns only the command wiring.
internal static class HostedMissionCommands
{
    // ---------------------------------------------------------------------------
    // fms run

    internal static Command BuildRunCommand(IHttpClientFactory clients)
    {
        var missionArg = new Argument<string>("mission") { Description = "A .mcl path or OCI reference" };
        var promptArg = new Argument<string>("prompt") { Description = "The mission's free-text input" };
        var projectOpt = new Option<string?>("--project") { Description = "Project file (default ./forge.project.json)" };
        var cmd = new Command("run", "Run a mission as a cloud conversation in a project and print the answer");
        cmd.Add(missionArg);
        cmd.Add(promptArg);
        cmd.Add(projectOpt);
        cmd.SetCancellableAction((result, cancellationToken) =>
        {
            var project = RunProject(result.GetValue(projectOpt), Directory.GetCurrentDirectory());
            if (!File.Exists(project))
            {
                Console.Error.WriteLine($"Project file not found: {Path.GetFullPath(project)}. Run `forge project create`.");
                return Task.FromResult(1);
            }
            return ForgeDurableRun.RunAsync(clients, project, result.GetValue(missionArg)!, result.GetValue(promptArg)!, cancellationToken);
        });
        return cmd;
    }

    private const string DefaultProjectFile = "forge.project.json";

    /// <summary>The run's project file: --project when given, else forge.project.json in
    /// <paramref name="directory"/>.</summary>
    internal static string RunProject(string? option, string directory) =>
        option ?? Path.Combine(directory, DefaultProjectFile);

    // ---------------------------------------------------------------------------
    // forge publish — package a mission directory and push it to an OCI registry (39.4)

    internal static Command BuildPublishCommand()
    {
        var refArg  = new Argument<string>("reference") { Description = "OCI reference, e.g. ghcr.io/katasec/forge-mission-guard:0.1.0" };
        var dirArg  = new Argument<DirectoryInfo>("dir") { Description = "Mission directory", Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => new DirectoryInfo(".") };
        var descOpt = new Option<string?>("--description") { Description = "Human description (annotation)" };

        var cmd = new Command("publish", "Package a mission directory into a self-contained OCI artifact and push it");
        cmd.Add(refArg);
        cmd.Add(dirArg);
        cmd.Add(descOpt);

        cmd.SetCancellableAction(async (result, cancellationToken) =>
        {
            var reference = result.GetValue(refArg)!;
            var dir       = result.GetValue(dirArg)!;
            var desc      = result.GetValue(descOpt);

            if (!File.Exists(Path.Combine(dir.FullName, "mission.mcl")))
            { CliDiagnostics.Die($"No mission.mcl in {dir.FullName} — point at a mission directory."); return; }

            // registry/name:tag parsing is owned by OciReference (MissionRegistry).
            string registry, name, tag;
            try { (registry, name, tag) = OciReference.SplitTaggedTarget(reference); }
            catch (ArgumentException ex) { CliDiagnostics.Die(ex.Message); return; }

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
}
