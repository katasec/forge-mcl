using System.Formats.Tar;
using System.Security.Cryptography;
using Katasec.OciClient;

namespace ForgeMission.MissionRegistry;

/// <summary>Pulls a self-contained OCI mission bundle into the local Forge cache.</summary>
public static class OciMissionPuller
{
    public static Task<(string Directory, string Status)> PullAsync(string ociReference, bool refresh,
        CancellationToken cancellationToken = default) => PullVerifiedAsync(OciReference.Parse(ociReference),
            RegistryCredentialStore.GetToken(OciReference.Parse(ociReference).Registry), cancellationToken);

    public static async Task<(string Directory, string Status)> PullVerifiedAsync(OciReference reference,
        string? credential, CancellationToken cancellationToken = default)
    {
        using var client = new OciClient(credential: credential);
        var pulled = await client.PullMissionWithDigestAsync(reference.Registry, reference.Name, reference.Reference, cancellationToken);
        var cache = ForgeCache.MissionDir(reference.Registry, reference.Name, pulled.LayerDigest);
        var pending = cache + "." + Guid.NewGuid().ToString("N");
        try
        {
            await ExtractBundleAsync(pulled.Bundle, pending, cancellationToken);
            if (Directory.Exists(cache))
            {
                RequireMatchingCache(pending, cache);
                return (cache, "verified");
            }
            try { Directory.Move(pending, cache); }
            catch (IOException) when (Directory.Exists(cache)) { RequireMatchingCache(pending, cache); }
            return (cache, "pulled");
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true); }
    }

    internal static async Task ExtractBundleAsync(byte[] bundle, string directory, CancellationToken ct)
    {
        const int maxBytes = 33554432;
        if (bundle.Length > maxBytes) throw new InvalidDataException("Mission bundle exceeds 32 MiB.");
        using var input = new MemoryStream(bundle, writable: false);
        using var reader = new TarReader(input);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        Directory.CreateDirectory(directory);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: ct) is { } entry)
        {
            var parts = entry.Name.Split('/');
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                parts.Any(part => part is "" or "." or "..") || entry.Name.IndexOfAny(['\\', ':', '\0']) >= 0 ||
                !names.Add(entry.Name) || names.Count > 4096 || entry.Length < 0 || (total += entry.Length) > maxBytes)
                throw new InvalidDataException("Mission bundle contains an invalid path, duplicate, non-regular entry or exceeds its budget.");
            var path = Path.Combine(directory, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, ct);
            if (output.Length != entry.Length) throw new InvalidDataException("Mission bundle entry length mismatch.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, entry.Mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute));
        }
        if (!names.Contains("mission.mcl")) throw new InvalidDataException("Mission bundle does not contain mission.mcl.");
    }

    internal static void RequireMatchingCache(string fresh, string cache)
    {
        if ((File.GetAttributes(cache) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Mission cache is a link.");
        var expected = Directory.GetFiles(fresh, "*", SearchOption.AllDirectories);
        if (CacheFiles(cache).Count != expected.Length)
            throw new InvalidDataException("Mission cache differs from verified content.");
        foreach (var file in expected)
        {
            var stored = Path.Combine(cache, Path.GetRelativePath(fresh, file));
            if (!File.Exists(stored) || new FileInfo(stored).Length != new FileInfo(file).Length || !SameBytes(file, stored))
                throw new InvalidDataException("Mission cache differs from verified content.");
        }
    }

    private static bool SameBytes(string first, string second)
    {
        using var left = File.OpenRead(first);
        using var right = File.OpenRead(second);
        return SHA256.HashData(left).AsSpan().SequenceEqual(SHA256.HashData(right));
    }

    private static List<string> CacheFiles(string root)
    {
        var files = new List<string>();
        var directories = new Stack<string>();
        directories.Push(root);
        var count = 0;
        while (directories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Mission cache contains a link.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                else files.Add(entry);
                if (++count > 8192) throw new InvalidDataException("Mission cache exceeds its entry budget.");
            }
        }
        return files;
    }

}
