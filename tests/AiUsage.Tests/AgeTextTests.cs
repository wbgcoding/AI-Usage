using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class AgeTextTests
{
    private static string Convert(DateTimeOffset? value) => AgeText.Describe(value);

    [Fact]
    public void TimestampFormatsAsTheBareElapsedText() =>
        Assert.Equal("vor 3h", Convert(DateTimeOffset.Now - TimeSpan.FromHours(3)));

    [Fact]
    public void MissingTimestampFallsBackToNeverUpdated() =>
        Assert.Equal("noch nie", Convert(null));
}
