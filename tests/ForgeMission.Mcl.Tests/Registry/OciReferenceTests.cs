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

    [Theory]
    [InlineData("example@sha256:abc")]
    [InlineData("ghcr.io/example")]
    [InlineData("ghcr.io/@sha256:abc")]
    public void Parse_RejectsIncompleteReferences(string value)
    {
        Assert.Throws<ArgumentException>(() => OciReference.Parse(value));
    }

    [Fact]
    public void SplitTaggedTarget_SplitsOnFirstSlashAndLastColon()
    {
        var (registry, name, tag) = OciReference.SplitTaggedTarget("ghcr.io/katasec/example:0.1.0");

        Assert.Equal("ghcr.io", registry);
        Assert.Equal("katasec/example", name);
        Assert.Equal("0.1.0", tag);
    }

    [Fact]
    public void SplitTaggedTarget_MissingSlash_ReportsRegistryNameTag()
    {
        var ex = Assert.Throws<ArgumentException>(() => OciReference.SplitTaggedTarget("example:0.1.0"));
        Assert.Equal("Invalid reference 'example:0.1.0': expected registry/name:tag", ex.Message);
    }

    [Fact]
    public void SplitTaggedTarget_MissingTag_ReportsTag()
    {
        var ex = Assert.Throws<ArgumentException>(() => OciReference.SplitTaggedTarget("ghcr.io/example"));
        Assert.Equal("Invalid reference 'ghcr.io/example': expected a :tag", ex.Message);
    }
}
