using System.Formats.Tar;
using System.Text;
using ForgeMission.MissionRegistry;

namespace ForgeMission.Tests.Registry;

public sealed class OciMissionPullerTests
{
    [Theory]
    [InlineData("ocr", "ghcr.io/katasec", "ghcr.io", "katasec/ocr", "latest")]
    [InlineData("team/ocr:V1", "ghcr.io/katasec", "ghcr.io", "katasec/team/ocr", "V1")]
    [InlineData("localhost:5000/team/ocr:1", "ghcr.io/katasec", "localhost:5000", "team/ocr", "1")]
    public void Selector_ResolvesWithoutTreatingBareTagAsHost(string value, string origin, string host, string name, string tag)
        => Assert.Equal(new OciReference(host, name, tag), OciReference.Resolve(value, origin));

    [Theory]
    [InlineData("../ocr")]
    [InlineData("https://ghcr.io/ocr")]
    [InlineData("team//ocr")]
    [InlineData("@handle")]
    [InlineData("team/Ocr")]
    [InlineData("ocr@bad")]
    [InlineData("ocr:tag@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    public void Selector_RejectsNoncanonicalReferences(string value)
        => Assert.Throws<ArgumentException>(() => OciReference.Resolve(value, "ghcr.io/katasec"));

    [Fact]
    public async Task Bundle_ExtractsOnlyRegularCanonicalEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            await OciMissionPuller.ExtractBundleAsync(Bundle(("mission.mcl", TarEntryType.RegularFile), ("experts/Read/data.bin", TarEntryType.RegularFile)), directory, default);
            Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(directory, "experts/Read/data.bin")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task VerifiedPull_RejectsDirectlyConstructedTraversalBeforeNetworkOrCacheWrites()
    {
        var reference = new OciReference("ghcr.io", "../escape", "latest");
        await Assert.ThrowsAsync<ArgumentException>(() => OciMissionPuller.PullVerifiedAsync(reference, null));
        await Assert.ThrowsAsync<ArgumentException>(() => OciExpertPuller.PullVerifiedAsync(reference, null));
    }

    [Theory]
    [InlineData("../outside", TarEntryType.RegularFile)]
    [InlineData("/absolute", TarEntryType.RegularFile)]
    [InlineData("mission.mcl", TarEntryType.RegularFile)]
    [InlineData("Mission.mcl", TarEntryType.RegularFile)]
    [InlineData("link", TarEntryType.SymbolicLink)]
    [InlineData("fifo", TarEntryType.Fifo)]
    [InlineData("folder/", TarEntryType.Directory)]
    public async Task Bundle_RejectsTraversalDuplicateAndNonRegularEntries(string path, TarEntryType kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-registry-" + Guid.NewGuid().ToString("N"));
        try { await Assert.ThrowsAsync<InvalidDataException>(() => OciMissionPuller.ExtractBundleAsync(Bundle(("mission.mcl", TarEntryType.RegularFile), (path, kind)), directory, default)); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Cache_MustMatchVerifiedBytesAndContainNoLinksOrExtraFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-registry-" + Guid.NewGuid().ToString("N"));
        var fresh = Path.Combine(root, "fresh");
        var cache = Path.Combine(root, "cache");
        try
        {
            var bytes = Bundle(("mission.mcl", TarEntryType.RegularFile));
            await OciMissionPuller.ExtractBundleAsync(bytes, fresh, default);
            await OciMissionPuller.ExtractBundleAsync(bytes, cache, default);
            OciMissionPuller.RequireMatchingCache(fresh, cache);
            await File.WriteAllTextAsync(Path.Combine(cache, "mission.mcl"), "corrupt");
            Assert.Throws<InvalidDataException>(() => OciMissionPuller.RequireMatchingCache(fresh, cache));
            File.Copy(Path.Combine(fresh, "mission.mcl"), Path.Combine(cache, "mission.mcl"), true);
            File.WriteAllText(Path.Combine(cache, "extra"), "unexpected");
            Assert.Throws<InvalidDataException>(() => OciMissionPuller.RequireMatchingCache(fresh, cache));
            File.Delete(Path.Combine(cache, "extra"));
            File.CreateSymbolicLink(Path.Combine(cache, "extra"), Path.Combine(fresh, "mission.mcl"));
            Assert.Throws<InvalidDataException>(() => OciMissionPuller.RequireMatchingCache(fresh, cache));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Bundle_RequiresSelectedSourceAndEnforcesRetrievalBudget()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => OciMissionPuller.ExtractBundleAsync(Bundle(("other", TarEntryType.RegularFile)), directory, default));
            await Assert.ThrowsAsync<InvalidDataException>(() => OciMissionPuller.ExtractBundleAsync(new byte[33554433], directory, default));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static byte[] Bundle(params (string Path, TarEntryType Kind)[] items)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, leaveOpen: true))
        {
            foreach (var (path, kind) in items)
            {
                var entry = new PaxTarEntry(kind, path);
                if (kind == TarEntryType.RegularFile) entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
                if (kind == TarEntryType.SymbolicLink) entry.LinkName = "mission.mcl";
                writer.WriteEntry(entry);
            }
        }
        return stream.ToArray();
    }
}
