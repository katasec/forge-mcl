using System.Formats.Tar;
using System.Security.Cryptography;
using Katasec.OciClient;

namespace ForgeMission.MissionRegistry;

/// <summary>Pulls a self-contained OCI mission bundle into the local Forge cache.</summary>
public static partial class OciMissionPuller
{
    internal const int MaxBundleBytes = 32 * 1024 * 1024;
    private const int MaxBundleEntries = 4096;

    public static async Task<(string Directory, string Status)> PullVerifiedAsync(OciReference reference,
        string? credential, CancellationToken cancellationToken = default)
    {
        reference = reference.Canonical();
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
        if (bundle.Length > MaxBundleBytes) throw new InvalidDataException($"Mission bundle exceeds {MaxBundleBytes / (1024 * 1024)} MiB.");
        using var input = new MemoryStream(bundle, writable: false);
        using var reader = new TarReader(input);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        Directory.CreateDirectory(directory);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: ct) is { } entry)
        {
            var parts = EntryPathParts(entry);
            if (!IsAllowedEntry(entry, parts))
                throw InvalidBundle();

            // Standard tar writes directory entries; their directories are created from file paths.
            if (entry.EntryType == TarEntryType.Directory) continue;

            total = AdmitEntry(entry, names, total);

            await ExtractFileAsync(entry, Path.Combine(directory, Path.Combine(parts)), ct);
        }
        if (!names.Any(name => name == "mission.mcl")) throw new InvalidDataException("Mission bundle does not contain mission.mcl.");
    }

    // Enforces unique names, the entry-count limit and the total-size limit; returns the new total.
    private static long AdmitEntry(TarEntry entry, HashSet<string> names, long total)
    {
        if (!names.Add(entry.Name) || names.Count > MaxBundleEntries || entry.Length < 0)
            throw InvalidBundle();
        total += entry.Length;
        if (total > MaxBundleBytes)
            throw InvalidBundle();
        return total;
    }

    private static string[] EntryPathParts(TarEntry entry)
    {
        var name = entry.EntryType == TarEntryType.Directory && entry.Name.EndsWith('/')
            ? entry.Name[..^1]
            : entry.Name;
        return name.Split('/');
    }

    // Only directories and regular files, with relative paths free of empty, dot and reserved segments.
    private static bool IsAllowedEntry(TarEntry entry, string[] parts)
    {
        var allowedType = entry.EntryType is TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile;
        var safeParts = !parts.Any(part => part is "" or "." or "..");
        var safeCharacters = entry.Name.IndexOfAny(['\\', ':', '\0']) < 0;
        return allowedType && safeParts && safeCharacters;
    }

    private static async Task ExtractFileAsync(TarEntry entry, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = new FileStream(path, NewFileOptions());
        if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, ct);
        if (output.Length != entry.Length) throw new InvalidDataException("Mission bundle entry length mismatch.");
    }

    private static InvalidDataException InvalidBundle() =>
        new("Mission bundle contains an invalid path, duplicate, non-regular entry or exceeds its budget.");

    // Extracted files are never executable: the entry's mode is ignored and every
    // file is created owner read/write, others read-only, still reduced by the process umask.
    private static FileStreamOptions NewFileOptions() => OperatingSystem.IsWindows()
        ? new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None }
        : new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead };

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

    internal static void RequireRegularFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidDataException("Mission cache contains a non-regular file.");
            return;
        }
        if (LStat(path, out var status) != 0)
            throw new IOException("Could not inspect mission cache file.", new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastPInvokeError()));
        var mode = OperatingSystem.IsMacOS() ? status.MacMode :
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? status.LinuxArm64Mode : status.LinuxX64Mode;
        if ((mode & 0xf000) != 0x8000) throw new InvalidDataException("Mission cache contains a non-regular file.");
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
                else { RequireRegularFile(entry); files.Add(entry); }
                if (++count > 8192) throw new InvalidDataException("Mission cache exceeds its entry budget.");
            }
        }
        return files;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 256)]
    private struct NativeFileStatus
    {
        [System.Runtime.InteropServices.FieldOffset(4)] public ushort MacMode;
        [System.Runtime.InteropServices.FieldOffset(16)] public uint LinuxArm64Mode;
        [System.Runtime.InteropServices.FieldOffset(24)] public uint LinuxX64Mode;
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "lstat", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int LStat(string path, out NativeFileStatus status);
}
