using Katasec.OciClient;

namespace ForgeMission.MissionRegistry;

/// <summary>Pulls a self-contained OCI mission bundle into the local Forge cache.</summary>
public static class OciMissionPuller
{
    public static async Task<(string Directory, string Status)> PullAsync(
        string ociReference,
        bool refresh,
        CancellationToken cancellationToken = default)
    {
        var reference = OciReference.Parse(ociReference);
        var cacheDirectory = ForgeCache.MissionDir(reference.Registry, reference.Name, reference.Reference);
        if (!refresh && File.Exists(Path.Combine(cacheDirectory, "mission.mcl")))
            return (cacheDirectory, "cached");

        using var client = new OciClient(credential: RegistryCredentialStore.GetToken(reference.Registry));
        var bundle = await client.PullMissionAsync(
            reference.Registry, reference.Name, reference.Reference, cancellationToken);

        if (Directory.Exists(cacheDirectory))
            Directory.Delete(cacheDirectory, recursive: true);
        MissionBundle.Unpack(bundle, cacheDirectory);
        return (cacheDirectory, "pulled");
    }
}
