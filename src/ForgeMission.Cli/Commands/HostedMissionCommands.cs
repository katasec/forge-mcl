using System.CommandLine;
using ForgeMission.Cli.Services;
using ForgeMission.Core.Resolution;
using ForgeMission.MissionRegistry;
using Katasec.OciClient;
using Microsoft.Extensions.Options;

namespace ForgeMission.Cli.Commands;

// Hosted-mission and packaging commands: `forge run` and `forge exec` (run a hosted mission through
// ForgeAPI) and `forge publish` (package a mission directory into an OCI artifact and push it). The
// hosted-mission transport lives in ForgeExec; OCI packaging/push lives in MissionRegistry and
// Katasec.OciClient. This surface owns only the command wiring.
internal static class HostedMissionCommands
{
    // ---------------------------------------------------------------------------
    // fms run

    internal static Command BuildRunCommand(IOptions<ForgeApiSettings> api, IHttpClientFactory clients)
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

    // forge exec — the one-shot hosted-mission command (42.6 task 8). The `@` prefix on a handle is
    // optional (ForgeExec.RunAsync strips it if present) — deliberately, because pwsh (the maintainer's
    // default shell, see AGENTS.md) mangles an unquoted leading `@` before it reaches a native process
    // ("Required argument missing", not a forge bug — confirmed 2026-07-19: `@websearch` fails in pwsh,
    // `"@websearch"` or bare `websearch` both work). Documented as the plain form so pwsh users aren't
    // steered into the gotcha; bash/zsh users can still type `@handle` fine.
    internal static Command BuildExecCommand(IOptions<ForgeApiSettings> api, IHttpClientFactory clients)
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
