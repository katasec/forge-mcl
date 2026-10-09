using System.Security.Cryptography;
using System.Text;
using ForgeMission.Core.Runtime;

namespace ForgeMission.Tests.Runtime;

public sealed class DurableMissionInputManifestTests
{
    [Fact]
    public void Manifest_sorts_bindings_and_has_a_stable_hash()
    {
        Assert.True(DurableMissionInputManifest.TryCreate(
            [Binding("mode", DurableMissionInputKind.Text), Binding("source_file", DurableMissionInputKind.Artifact)],
            out var first, out var firstReason), firstReason);
        Assert.True(DurableMissionInputManifest.TryCreate(
            [Binding("source_file", DurableMissionInputKind.Artifact), Binding("mode", DurableMissionInputKind.Text)],
            out var second, out var secondReason), secondReason);

        Assert.Equal(["mode", "source_file"], first!.Bindings.Select(binding => binding.Name));
        Assert.Equal(first.ManifestHash, second!.ManifestHash);
        Assert.True(first.IsValid(out var reason), reason);
    }

    [Theory]
    [InlineData("Output")]
    [InlineData("output")]
    [InlineData("feedback")]
    [InlineData("max_loops")]
    [InlineData("source-file")]
    public void Manifest_rejects_invalid_or_reserved_names(string name)
    {
        var accepted = DurableMissionInputManifest.TryCreate([Binding(name, DurableMissionInputKind.Text)], out _, out var reason);

        Assert.False(accepted);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Manifest_rejects_duplicate_names()
    {
        var accepted = DurableMissionInputManifest.TryCreate(
            [Binding("goal", DurableMissionInputKind.Text), Binding("goal", DurableMissionInputKind.Artifact)], out _, out var reason);

        Assert.False(accepted);
        Assert.Equal("The named input manifest is invalid.", reason);
    }

    private static DurableMissionInputBinding Binding(string name, DurableMissionInputKind kind)
    {
        var content = Encoding.UTF8.GetBytes(name);
        return new DurableMissionInputBinding(name, kind, content.Length,
            "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
    }
}
