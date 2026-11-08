using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public sealed class TextComposeTests
{
    [Theory]
    [InlineData("en", "a · b", "a (b)", "1 x", "a b", "a, b")]
    [InlineData("ja", "a · b", "a (b)", "1x", "a b", "a、b")]
    [InlineData("zh-Hans", "a · b", "a (b)", "1x", "a b", "a、b")]
    public void Each_helper_follows_the_language_format(
        string language, string pair, string detail, string numberUnit, string nameAndItem, string list)
    {
        var loc = LocalizationService.Instance;
        loc.SetLanguage(language);
        try
        {
            Assert.Equal(pair, TextCompose.Pair("a", "b"));
            Assert.Equal(detail, TextCompose.WithDetail("a", "b"));
            Assert.Equal(numberUnit, TextCompose.NumberUnit("1", "x"));
            Assert.Equal(nameAndItem, TextCompose.NameAndItem("a", "b"));
            Assert.Equal(list, TextCompose.List(["a", "", "b"]));
        }
        finally
        {
            loc.SetLanguage("de");
        }
    }

    [Fact]
    public void PairAll_skips_empty_parts_and_chains_the_rest()
    {
        Assert.Equal("a · b · c", TextCompose.PairAll(["a", "", "b", "c"]));
        Assert.Equal("", TextCompose.PairAll([]));
    }
}
