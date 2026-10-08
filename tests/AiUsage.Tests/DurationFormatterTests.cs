using System.Globalization;
using System.Threading;
using AiUsage.Services;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class DurationFormatterTests
{
    // The fractional-year decimal separator follows CultureInfo.CurrentCulture, so this test pins
    // the thread culture to de-DE for the duration of the assertion
    // instead of assuming whatever locale happens to be installed on the machine running it.
    [Theory]
    [InlineData(1, "1 Tag")]
    [InlineData(2, "2 Tage")]
    [InlineData(30, "30 Tage")]
    [InlineData(365, "1 Jahr")]
    [InlineData(547, "1,5 Jahre")]
    [InlineData(3650, "10 Jahre")]
    public void Describe_matches_the_documented_examples(int days, string expected)
    {
        var original = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal(expected, DurationFormatter.Describe(days));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
