using System.CommandLine;
using ForgeMission.Cli.Services;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Manifest;
using ForgeMission.Core.Resolution;
using ForgeMission.MissionRegistry;

namespace ForgeMission.Cli.Commands;

// Local mission-authoring commands: `forge init` (resolve experts + write mcl.lock), `forge
// validate`, `forge list experts`, and `forge expert init`. Mission parsing, expert loading and
// diagnostics live in the shared CLI helpers (CliHelpers / CliDiagnostics); this surface owns only
// the command wiring and the init orchestration (local + OCI expert resolution).
internal static class MissionDevCommands
{
    // ---------------------------------------------------------------------------
    // forge init

    internal static Command BuildInitCommand()
    {
        var missionArg  = new Argument<FileInfo>("mission") { Description = "Path to the .mcl mission file", Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => new FileInfo("mission.mcl") };
        var refreshOpt  = new Option<bool>("--refresh") { Description = "Re-pull OCI experts even if already present in ~/.forge/experts" };

        var cmd = new Command("init", "Resolve expert sources and generate mcl.lock");
        cmd.Add(missionArg);
        cmd.Add(refreshOpt);

        cmd.SetCancellableAction((result, cancellationToken) =>
            RunInitAsync(result.GetValue(missionArg)!, result.GetValue(refreshOpt), cancellationToken));

        return cmd;
    }

    // Resolve local experts, pull any OCI experts, and write mcl.lock. Each helper reports its own
    // failure through Die (which throws ForgeExitException), so the orchestration reads top-down.
    internal static async Task RunInitAsync(FileInfo mission, bool refresh, CancellationToken cancellationToken)
    {
        var missionDir = mission.DirectoryName!;

        var (_, manifest) = await CliHelpers.LoadMissionAsync(mission.FullName, cancellationToken);

        Console.WriteLine("Resolving experts...\n");

        var lockFile = BuildLocalLockFile(missionDir);
        foreach (var (name, entry) in lockFile.Experts.OrderBy(k => k.Key))
            Console.WriteLine($"  ✓ {name,-30} local    {entry.Path}");

        await PullOciExpertsAsync(manifest, refresh, lockFile, cancellationToken);

        var lockPath = CliHelpers.MclLockPath(missionDir);
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
            catch (MclException ex) { CliDiagnostics.Die(ex.Message); }
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
                Path   = ExpertResolver.ToLockPath(cachePath),
                Hash   = LockFileIO.ComputeHash(cachePath),
            };
            Console.WriteLine($"  ✓ {name,-30} {status,-8} {ociRef}");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"  ✗ {name,-30} failed   {ociRef}");
            Console.Error.WriteLine($"    {ex.Message}");
            Console.Error.WriteLine("    Run 'forge registry login <registry> --token <PAT>' if this is an auth error.");
            CliDiagnostics.Die($"MCL011 OCI pull failed for '{name}'.");
        }
    }

    // ---------------------------------------------------------------------------
    // forge validate

    internal static Command BuildValidateCommand()
    {
        var missionArg = new Argument<FileInfo>("mission") { Description = "Path to the .mcl mission file", Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => new FileInfo("mission.mcl") };

        var cmd = new Command("validate", "Validate a mission file and its expert references");
        cmd.Add(missionArg);

        cmd.SetCancellableAction(async (result, cancellationToken) =>
        {
            var mission    = result.GetValue(missionArg)!;
            var missionDir = mission.DirectoryName!;
            var lockPath   = CliHelpers.MclLockPath(missionDir);

            // forge.toml is read only to validate it.
            var (ast, _) = await CliHelpers.LoadMissionAsync(mission.FullName, cancellationToken);

            var expertDefs = File.Exists(lockPath)
                ? ResolveLockedExperts(lockPath, missionDir)
                : LoadUnlockedExperts(missionDir);
            if (expertDefs is null) return;

            if (!CliHelpers.TryValidate(ast, expertDefs, missionFilePath: mission.FullName))
                throw new ForgeExitException(1);
            Console.WriteLine("OK — mission is valid.");
        });

        return cmd;
    }

    // No mcl.lock: warn, then load experts straight from ./experts.
    static Dictionary<string, ExpertDefinition> LoadUnlockedExperts(string missionDir)
    {
        Console.Error.WriteLine($"{CliDiagnostics.BoldYellow("warning")}{CliDiagnostics.Bold(": MCL006 mcl.lock not found — run 'forge init' to generate it")}");
        return CliHelpers.LoadExperts(Path.Combine(missionDir, SourceResolver.DefaultExpertsDir));
    }

    // mcl.lock present: resolve the locked experts. A single expert-load failure is reported and
    // ends validate (null) without a non-zero exit — existing behaviour, kept as is.
    static Dictionary<string, ExpertDefinition>? ResolveLockedExperts(string lockPath, string missionDir)
    {
        var lockFile = LockFileIO.Read(lockPath);
        try { return ExpertResolver.ResolveAll(lockFile, missionDir, warnings: Console.Error); }
        catch (ExpertLoadException ex) { CliDiagnostics.ReportExpertLoadFailure(ex); return null; }
    }

    // ---------------------------------------------------------------------------
    // forge list

    internal static Command BuildListCommand()
    {
        var listCommand    = new Command("list", "List available resources");
        var expertsOpt     = new Option<DirectoryInfo?>("--experts") { Description = "Experts directory (default: ./experts)" };
        var listExpertsCmd = new Command("experts", "List experts in the experts directory");
        listExpertsCmd.Add(expertsOpt);

        listExpertsCmd.SetCancellableAction((result, _) =>
        {
            var experts    = result.GetValue(expertsOpt);
            var expertsDir = experts?.FullName ?? SourceResolver.DefaultExpertsDir;

            var expertDefs = CliHelpers.LoadExperts(expertsDir);
            if (expertDefs.Count == 0) { Console.WriteLine($"No experts found in {expertsDir}"); return Task.CompletedTask; }

            Console.WriteLine($"Experts in {expertsDir}:");
            foreach (var (name, def) in expertDefs.OrderBy(k => k.Key))
                Console.WriteLine($"  {name,-30} {def.Input} -> {def.Output}");

            return Task.CompletedTask;
        });

        listCommand.Add(listExpertsCmd);
        return listCommand;
    }

    // ---------------------------------------------------------------------------
    // forge expert

    internal static Command BuildExpertCommand()
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
            var expertsDir = experts?.FullName ?? SourceResolver.DefaultExpertsDir;

            if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0]))
                CliDiagnostics.Die($"Expert name must be PascalCase (got '{name}').");

            var expertDir = Path.Combine(expertsDir, name);
            var expertMd  = Path.Combine(expertDir, "expert.md");

            if (File.Exists(expertMd))
                CliDiagnostics.Die($"Expert '{name}' already exists at {expertMd}");

            Directory.CreateDirectory(expertDir);
            await File.WriteAllTextAsync(expertMd, CliHelpers.ExpertTemplate(name), cancellationToken);

            Console.WriteLine($"Created {expertMd}");
        });

        expertCommand.Add(initExpertCmd);
        return expertCommand;
    }
}
