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
}
