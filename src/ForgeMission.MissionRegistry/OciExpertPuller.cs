using Katasec.OciClient;

namespace ForgeMission.MissionRegistry;

/// <summary>Pulls immutable expert content into the local Forge OCI cache.</summary>
public static class OciExpertPuller
{
    public static async Task<(string Path, string Status)> PullAsync(
        string ociReference,
        bool refresh,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        var reference = OciReference.Parse(ociReference);
        var cachePath = ForgeCache.ExpertMdPath(reference.Registry, reference.Name, reference.Reference);
        if (!refresh && File.Exists(cachePath))
            return (cachePath, "cached");

        using var client = new OciClient(credential);
        var content = await client.PullExpertAsync(reference.Registry, reference.Name, reference.Reference);

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllTextAsync(cachePath, content, cancellationToken);
        return (cachePath, "pulled");
    }

    public static string ToLockPath(string absolutePath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return absolutePath.StartsWith(home, StringComparison.Ordinal)
            ? "~/" + absolutePath[(home.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/')
            : absolutePath;
    }
}
