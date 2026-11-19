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

    [Theory]
    [InlineData("ghcr.io/katasec/example:0.1.0", "ghcr.io", "katasec/example", "0.1.0")]
    [InlineData("example:0.1.0", "ghcr.io", "katasec/example", "0.1.0")]
    [InlineData("localhost:5000/example:1", "localhost:5000", "example", "1")]
    public void ResolveTagged_ReadsTargetsTheWayRunResolvesThem(string selector, string registry, string name, string tag)
    {
        var target = OciReference.ResolveTagged(selector, "ghcr.io/katasec");

        Assert.Equal(OciReference.Resolve(selector, "ghcr.io/katasec"), target);
        Assert.Equal(new OciReference(registry, name, tag), target);
    }

    [Theory]
    [InlineData("ghcr.io/katasec/example")]
    [InlineData("localhost:5000/example")]
    [InlineData("example@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    public void ResolveTagged_RejectsTargetWithoutExplicitTag(string selector)
    {
        var ex = Assert.Throws<ArgumentException>(() => OciReference.ResolveTagged(selector, "ghcr.io/katasec"));
        Assert.Contains("a tag is required", ex.Message);
    }

    [Theory]
    [InlineData("ghcr.io/Katasec/Example:0.1.0")]
    [InlineData("ghcr.io/katasec/../example:0.1.0")]
    public void ResolveTagged_RejectsNamesRunCannotPull(string selector)
    {
        Assert.Throws<ArgumentException>(() => OciReference.ResolveTagged(selector, "ghcr.io/katasec"));
        Assert.Throws<ArgumentException>(() => OciReference.Resolve(selector, "ghcr.io/katasec"));
    }
}
