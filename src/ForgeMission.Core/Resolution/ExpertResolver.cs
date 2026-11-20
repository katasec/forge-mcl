using ForgeMission.Core.Experts;

namespace ForgeMission.Core.Resolution;

// Resolves expert names to ExpertDefinition objects using the two-source order:
//   1. <mission-dir>/experts/<Name>/expert.md  — local, always wins
//   2. Lock file recorded path (may point to OCI cache at ~/.forge/experts/...)
//
// Hash verification: if the lock file entry has a hash, the loaded file must match.
// If it doesn't match → the expert was modified since last 'forge init'.
//
// The verbose writer, when given, gets each expert's resolution source and path; the warnings
// writer, when given, gets shadowing warnings whatever the verbose setting.
public static class ExpertResolver
{
    // The resolution source each expert reports in verbose output.
    private const string LocalSource = "local";
    private const string CacheSource = "cache";

    public static Dictionary<string, ExpertDefinition> ResolveAll(
        LockFile      lockFile,
        string        missionDir,
        TextWriter?   verbose  = null,
        TextWriter?   warnings = null)
    {
        var result = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal);
        var errors = new List<ExpertLoadException>();

        foreach (var (name, entry) in lockFile.Experts)
        {
            try { result[name] = Resolve(name, entry, missionDir, verbose, warnings); }
            catch (AggregateExpertLoadException ex) { errors.AddRange(ex.Errors); }
            catch (ExpertLoadException ex)           { errors.Add(ex); }
        }

        // One error keeps its own exception and message; several surface together, as LoadAll does.
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new AggregateExpertLoadException(errors);
        return result;
    }

    // Locates, checks and parses one lock-file expert; throws ExpertLoadException (or the
    // parser's AggregateExpertLoadException) when it cannot be loaded.
    private static ExpertDefinition Resolve(
        string name, LockFileExpert entry, string missionDir, TextWriter? verbose, TextWriter? warnings)
    {
        var (absPath, source) = Locate(name, entry, missionDir);

        if (absPath is null)
            throw new ExpertLoadException(
                $"{MclErrorCode.ExpertNotFound.Code()} Expert '{name}' not found at '{entry.Path}'. Run 'forge init' to regenerate the lock file.");

        // A local expert shadows the lock entry when the entry records a different path. Local wins
        // intentionally, but silent shadowing is a reproducibility risk, so warn when the recorded
        // file exists; the lock hash is for that recorded version, so it is not checked.
        var recorded = string.IsNullOrEmpty(entry.Path) ? null : ResolveLockPath(entry.Path, missionDir);
        var isShadowing = source == LocalSource && recorded is not null
            && !string.Equals(absPath, recorded, StringComparison.OrdinalIgnoreCase);
        if (isShadowing && File.Exists(recorded))
        {
            warnings?.WriteLine(
                $"warning {MclErrorCode.LocalExpertShadowsLock.Code()}: local expert '{name}' shadows the lock-file entry " +
                $"({ToLockPath(recorded!)}). Local version will be used. " +
                $"Run 'forge init' to update the lock file if this is intentional.");
        }

        // Hash verification — skip for legacy lock files without a recorded hash.
        if (!isShadowing && entry.Hash is { Length: > 0 } expectedHash)
        {
            var actualHash = LockFileIO.ComputeHash(absPath);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new ExpertLoadException(
                    $"{MclErrorCode.ExpertChanged.Code()} Expert '{name}' has changed since last 'forge init'. " +
                    $"Run 'forge init' to update the lock file.");
        }

        verbose?.WriteLine($"[forge] {name,-30} → {source,-8} ({ToLockPath(absPath)})");

        return ExpertLoader.ParseFile(absPath);
    }

    // Returns (absolutePath, sourceLabel) using the resolution order.
    // Returns (null, "") if the expert cannot be found at any source.
    private static (string? Path, string Source) Locate(string name, LockFileExpert entry, string missionDir)
    {
        // 1. Local expert directory always wins over the recorded lock path
        var localPath = Path.Combine(missionDir, SourceResolver.DefaultExpertsDir, name, SourceResolver.ExpertFile);
        if (File.Exists(localPath))
            return (localPath, LocalSource);

        // 2. Lock file recorded path (relative → absolute, or ~/... → home-relative)
        if (!string.IsNullOrEmpty(entry.Path))
        {
            var recorded = ResolveLockPath(entry.Path, missionDir);
            if (File.Exists(recorded))
                return (recorded, entry.Source == SourceResolver.DefaultExpertsDir ? LocalSource : CacheSource);
        }

        return (null, "");
    }

    // The mcl.lock form of a cache path: ~/... when under the user's home, otherwise unchanged.
    // Also the verbose/warning display form.
    public static string ToLockPath(string absolutePath)
        => ToLockPath(absolutePath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // Abbreviates only at a directory boundary, so a sibling such as /Users/xyz under home
    // /Users/xy stays unchanged.
    internal static string ToLockPath(string absolutePath, string home)
    {
        if (absolutePath == home)
            return "~";

        var homePrefix = home + Path.DirectorySeparatorChar;
        if (!absolutePath.StartsWith(homePrefix, StringComparison.Ordinal))
            return absolutePath;

        return "~/" + absolutePath[homePrefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string ResolveLockPath(string path, string missionDir)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..].Replace('/', Path.DirectorySeparatorChar));

        if (Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(Path.Combine(missionDir, path));
    }
}
