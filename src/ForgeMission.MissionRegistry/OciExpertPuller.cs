using Katasec.OciClient;

namespace ForgeMission.MissionRegistry;

/// <summary>Pulls immutable expert content into the local Forge OCI cache.</summary>
public static class OciExpertPuller
{
    public static Task<(string Path, string Status)> PullAsync(string ociReference, bool refresh,
        CancellationToken cancellationToken = default) => PullVerifiedAsync(OciReference.Parse(ociReference),
            RegistryCredentialStore.GetToken(OciReference.Parse(ociReference).Registry), cancellationToken);

    public static async Task<(string Path, string Status)> PullVerifiedAsync(OciReference reference, string? credential,
        CancellationToken cancellationToken = default)
    {
        reference = reference.Canonical();
        using var client = new OciClient(credential: credential);
        var pulled = await client.PullExpertWithDigestAsync(reference.Registry, reference.Name, reference.Reference, cancellationToken);
        var cachePath = ForgeCache.ExpertMdPath(reference.Registry, reference.Name, pulled.ManifestDigest.Replace(':', '-'));
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        if (File.Exists(cachePath))
        {
            OciMissionPuller.RequireRegularFile(cachePath);
            if ((File.GetAttributes(cachePath) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(cachePath).Length > 33554432 || await File.ReadAllTextAsync(cachePath, cancellationToken) != pulled.Content)
                throw new InvalidDataException("Expert cache differs from verified content.");
            return (cachePath, "verified");
        }
        await File.WriteAllTextAsync(cachePath, pulled.Content, cancellationToken);
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
