using AiUsage.Models;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class ProviderLinksTests
{
    [Theory]
    [MemberData(nameof(KnownProviderIds))]
    public void EveryKnownProviderHasAnHttpsUsagePage(string providerId)
    {
        var uri = ProviderLinks.UsagePage(providerId);

        Assert.NotNull(uri);
        Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
    }

    public static IEnumerable<object[]> KnownProviderIds() =>
        AppSettings.KnownProviderIds.Select(id => new object[] { id });

    [Fact]
    public void AnUnknownProviderIdReturnsNull()
    {
        Assert.Null(ProviderLinks.UsagePage("not-a-real-provider"));
    }
}
