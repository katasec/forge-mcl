using ForgeMission.MissionRegistry;

namespace ForgeMission.Tests.Registry;

public sealed class OciReferenceTests
{
    [Fact]
    public void Parse_SplitsRegistryNameAndReference()
    {
        var reference = OciReference.Parse("ghcr.io/katasec/example@sha256:abc");

        Assert.Equal("ghcr.io", reference.Registry);
        Assert.Equal("katasec/example", reference.Name);
        Assert.Equal("sha256:abc", reference.Reference);
    }

    [Fact]
    public void Resolve_expands_a_bare_name_below_the_configured_base()
    {
        var reference = OciReference.Resolve("ocr", "ghcr.io/katasec");

        Assert.Equal("ghcr.io", reference.Registry);
        Assert.Equal("katasec/ocr", reference.Name);
        Assert.Equal(OciReference.DefaultTag, reference.Reference);
    }

    [Fact]
    public void Resolve_preserves_a_qualified_reference_and_its_digest()
    {
        var digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var reference = OciReference.Resolve($"company.jfrog.io/missions/ocr@{digest}", "ghcr.io/katasec");

        Assert.Equal("company.jfrog.io", reference.Registry);
        Assert.Equal("missions/ocr", reference.Name);
        Assert.Equal(digest, reference.Reference);
        Assert.True(reference.IsDigest);
    }

    [Fact]
    public void Resolve_preserves_a_qualified_reference_without_a_tag()
    {
        var reference = OciReference.Resolve("company.jfrog.io/missions/ocr", "ghcr.io/katasec");

        Assert.Equal("company.jfrog.io", reference.Registry);
        Assert.Equal("missions/ocr", reference.Name);
        Assert.Equal(OciReference.DefaultTag, reference.Reference);
    }

    [Theory]
    [InlineData("example@sha256:abc")]
    [InlineData("ghcr.io/example")]
    [InlineData("ghcr.io/@sha256:abc")]
    [InlineData("https://ghcr.io/katasec/example")]
    public void Parse_RejectsIncompleteReferences(string value)
    {
        Assert.Throws<ArgumentException>(() => OciReference.Parse(value));
    }
}
