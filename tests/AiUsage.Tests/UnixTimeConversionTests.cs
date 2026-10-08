using AiUsage.Providers.Parsing;

namespace AiUsage.Tests;

public class UnixTimeConversionTests
{
    [Fact]
    public void FromUnixSecondsOrNull_converts_a_representable_value()
    {
        var result = UnixTimeConversion.FromUnixSecondsOrNull(1788642834);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788642834), result);
    }

    [Fact]
    public void FromUnixSecondsOrNull_returns_null_instead_of_throwing_when_out_of_range()
    {
        var result = UnixTimeConversion.FromUnixSecondsOrNull(99999999999999);

        Assert.Null(result);
    }
}
