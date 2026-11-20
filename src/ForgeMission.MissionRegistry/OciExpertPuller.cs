using Katasec.OciClient;

namespace ForgeMission.MissionRegistry;

/// <summary>Pulls immutable expert content into the local Forge OCI cache.</summary>
public static class OciExpertPuller
{
    public static Task<(string Path, string Status)> PullAsync(string ociReference,
        CancellationToken cancellationToken = default)
    {
        var reference = OciReference.Parse(ociReference);
        return PullVerifiedAsync(reference, RegistryCredentialStore.GetToken(reference.Registry), cancellationToken);
    }

    public static async Task<(string Path, string Status)> PullVerifiedAsync(OciReference reference, string? credential,
        CancellationToken cancellationToken = default)
    {
        reference = reference.Canonical();
        using var client = new OciClient(credential: credential);
        var pulled = await client.PullExpertWithDigestAsync(reference.Registry, reference.Name, reference.Reference, cancellationToken);
        var cachePath = ForgeCache.ExpertMdPath(reference.Registry, reference.Name, pulled.ManifestDigest);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        if (File.Exists(cachePath))
        {
            await VerifyCachedEntryAsync(cachePath, pulled.Content, cancellationToken);
            return (cachePath, "verified");
        }
        await WriteCacheAtomicAsync(cachePath, pulled.Content, cancellationToken);
        return (cachePath, "pulled");
    }

    // An existing cache entry must be a small regular file holding exactly the verified content.
    private static async Task VerifyCachedEntryAsync(string cachePath, string content, CancellationToken cancellationToken)
    {
        OciMissionPuller.RequireRegularFile(cachePath);
        if ((File.GetAttributes(cachePath) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(cachePath).Length > OciMissionPuller.MaxBundleBytes || await File.ReadAllTextAsync(cachePath, cancellationToken) != content)
            throw new InvalidDataException("Expert cache differs from verified content.");
    }

    // Atomic write: stage into a temp file in the SAME cache directory, then
    // replace/move into place so a crash or concurrent read never sees a
    // truncated cache entry.
    private static async Task WriteCacheAtomicAsync(string cachePath, string content, CancellationToken cancellationToken)
    {
        var cacheDir = Path.GetDirectoryName(cachePath)!;
        var temp = Path.Combine(cacheDir, $".{Path.GetFileName(cachePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, content, cancellationToken);
            if (File.Exists(cachePath))
                File.Replace(temp, cachePath, null);
            else
                File.Move(temp, cachePath);
        }
        catch
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort cleanup */ }
            }
            throw;
        }
    }
}
