using ForgeMission.Core.Resolution;

namespace ForgeMission.Tests.Resolution;

public class MclErrorCodeTests
{
    [Fact]
    public void Every_code_has_a_unique_value()
    {
        var values = Enum.GetValues<MclErrorCode>().Select(c => (int)c).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
    }

    [Theory]
    [InlineData(MclErrorCode.SourceNotFound,          "MCL005")]
    [InlineData(MclErrorCode.InputKeyMissingUpstream, "MCL011")]
    [InlineData(MclErrorCode.OciPullFailed,           "MCL013")]
    public void Code_formats_as_MCL_and_three_digits(MclErrorCode code, string expected) =>
        Assert.Equal(expected, code.Code());
}
