namespace ForgeMission.MissionRegistry;

/// <summary>Resolves paths inside the global Forge OCI cache.</summary>
public static class ForgeCache
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".forge");

    public static string ExpertsRoot => Path.Combine(Root, "experts");

    public static string ExpertMdPath(string registry, string ociName, string version)
        => Path.Combine(ExpertsRoot, registry, ociName, version, "expert.md");

    public static string MissionsRoot => Path.Combine(Root, "missions");

    public static string MissionDir(string registry, string ociName, string version)
        => Path.Combine(MissionsRoot, registry, ociName, Sanitize(version));

    private static string Sanitize(string version) => version.Replace(':', '-');
}
