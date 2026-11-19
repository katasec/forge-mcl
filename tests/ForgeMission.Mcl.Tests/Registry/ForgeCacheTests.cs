using ForgeMission.MissionRegistry;

namespace ForgeMission.Tests.Registry;

// ForgeCache.Clear is the only cache eviction: it scopes to one registry host and refuses any
// registry value that would reach outside the cache root.
public sealed class ForgeCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Clear_with_registry_removes_only_that_host_from_both_caches()
    {
        Write("experts/ghcr.io/a/1/expert.md", "missions/ghcr.io/m/1/mission.mcl", "experts/registry.test/c/3/expert.md");

        Assert.Equal(2, ForgeCache.Clear(_root, "ghcr.io"));

        Assert.False(Directory.Exists(Path.Combine(_root, "experts", "ghcr.io")));
        Assert.False(Directory.Exists(Path.Combine(_root, "missions", "ghcr.io")));
        Assert.True(File.Exists(Path.Combine(_root, "experts", "registry.test", "c", "3", "expert.md")));
    }

    [Fact]
    public void Clear_returns_zero_when_no_cache_exists()
        => Assert.Equal(0, ForgeCache.Clear(_root, null));

    [Theory]
    [InlineData("..")]
    [InlineData("ghcr.io/../..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData(" ")]
    public void Clear_rejects_a_registry_that_is_not_one_host_segment(string registry)
    {
        Write("credentials.json");

        Assert.Throws<ArgumentException>(() => ForgeCache.Clear(_root, registry));
        Assert.True(File.Exists(Path.Combine(_root, "credentials.json")));
    }

    private void Write(params string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(_root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
    }
}
