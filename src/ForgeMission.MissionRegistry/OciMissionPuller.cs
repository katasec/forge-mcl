using Katasec.OciClient;

namespace ForgeMission.MissionRegistry;

/// <summary>Pulls a self-contained OCI mission bundle into the local Forge cache.</summary>
public static class OciMissionPuller
{
    /// <summary>Resolves a tag to its manifest digest before calculating its cache location.</summary>
    public static async Task<PulledMissionDirectory> PullWithDigestAsync(
        OciReference reference,
        bool refresh,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        if (!refresh && reference.IsDigest && TryCached(reference, out var cached))
            return new PulledMissionDirectory(cached, reference, "cached");

        using var client = new OciClient(credential);
        var pulled = await client.PullMissionWithDigestAsync(
            reference.Registry, reference.Name, reference.Reference, cancellationToken);
        var pinned = reference with { Reference = pulled.ManifestDigest };
        if (!refresh && TryCached(pinned, out var pinnedCache))
            return new PulledMissionDirectory(pinnedCache, pinned, "cached");

        var cacheDirectory = ForgeCache.MissionDir(pinned.Registry, pinned.Name, pinned.Reference);
        ReplaceCacheDirectory(cacheDirectory, pulled.Bundle);
        return new PulledMissionDirectory(cacheDirectory, pinned, "pulled");
    }

    private static bool TryCached(OciReference reference, out string directory)
    {
        directory = ForgeCache.MissionDir(reference.Registry, reference.Name, reference.Reference);
        return Directory.Exists(directory) && File.Exists(Path.Combine(directory, "mission.mcl"));
    }

    private static void ReplaceCacheDirectory(string cacheDirectory, byte[] bundle)
    {
        if (Directory.Exists(cacheDirectory))
            Directory.Delete(cacheDirectory, recursive: true);
        MissionBundle.Unpack(bundle, cacheDirectory);
    }
}

public sealed record PulledMissionDirectory(string Directory, OciReference Reference, string Status);
