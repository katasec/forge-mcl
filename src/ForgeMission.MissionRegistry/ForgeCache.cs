namespace ForgeMission.MissionRegistry;

/// <summary>Resolves paths inside the global Forge OCI cache and is the only owner that deletes it.</summary>
public static class ForgeCache
{
    // ~/.forge; ForgeMission.Core.Resolution.ForgeHome owns the same root for assemblies that reference Core.
    internal static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge");

    internal const string ExpertFile = "expert.md";

    public static string ExpertsRoot => Path.Combine(Root, "experts");

    public static string ExpertMdPath(string registry, string ociName, string version)
        => Path.Combine(ExpertsRoot, registry, ociName, Sanitize(version), ExpertFile);

    public static string MissionsRoot => Path.Combine(Root, "missions");

    public static string MissionDir(string registry, string ociName, string version)
        => Path.Combine(MissionsRoot, registry, ociName, Sanitize(version));

    /// <summary>
    /// Removes the cached experts and missions, or only those from <paramref name="registry"/> when
    /// given, and returns how many files were removed. A missing cache counts as zero.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="registry"/> is empty or is not a single host segment.</exception>
    public static int Clear(string? registry) => Clear(Root, registry);

    // Test seam: the same eviction against a caller-owned cache root.
    internal static int Clear(string root, string? registry)
    {
        if (registry is not null)
            ValidateRegistry(registry);

        return new[] { "experts", "missions" }
            .Select(cache => registry is null ? Path.Combine(root, cache) : Path.Combine(root, cache, registry))
            .Sum(DeleteDirectory);
    }

    // The registry feeds a recursive delete, so it must name exactly one folder under the cache root.
    private static void ValidateRegistry(string registry)
    {
        if (string.IsNullOrWhiteSpace(registry) || registry.Contains("..")
            || registry.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\']) >= 0
            || Path.IsPathRooted(registry))
            throw new ArgumentException($"Registry '{registry}' must be a single host name such as ghcr.io.");
    }

    private static int DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        var count = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();
        Directory.Delete(directory, recursive: true);
        return count;
    }

    private static string Sanitize(string version) => version.Replace(':', '-');
}
