using System.Globalization;
using AiUsage.Services;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class LabelAndValueConverterTests
{
    [Theory]
    [InlineData("en", "Total", "$5", "Total $5")]
    [InlineData("de", "Gesamt", "5 $", "Gesamt 5 $")]
    [InlineData("ja", "合計", "$5", "合計$5")]
    public void ValueFollowsTheLabelInTheLanguagesSpacing(string language, string label, string value, string expected)
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage(language);
        try
        {
            Assert.Equal(expected, new LabelAndValueConverter().Convert([label, value], typeof(string), null!, CultureInfo.InvariantCulture));
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void AnEmptyValueLeavesTheLabelAlone()
    {
        Assert.Equal("Total", new LabelAndValueConverter().Convert(["Total", ""], typeof(string), null!, CultureInfo.InvariantCulture));
    }
}
