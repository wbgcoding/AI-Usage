using System.Globalization;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

public class FigureCardColumnsConverterTests
{
    private readonly FigureCardColumnsConverter _converter = new();

    private int Columns(int cards, object width) =>
        (int)_converter.Convert([cards, width], typeof(int), null, CultureInfo.InvariantCulture);

    [Fact]
    public void AFullWidthSectionKeepsEveryCardInOneRow() => Assert.Equal(5, Columns(5, 690.0));

    [Fact]
    public void AHalfWidthSectionWrapsTheCards() => Assert.Equal(2, Columns(5, 330.0));

    [Fact]
    public void AVeryNarrowSectionStillShowsOneColumn() => Assert.Equal(1, Columns(4, 60.0));

    [Fact]
    public void AnUnmeasuredSectionFallsBackToTheCardCount()
    {
        Assert.Equal(4, Columns(4, 0.0));
        Assert.Equal(4, Columns(4, double.NaN));
        Assert.Equal(4, Columns(4, "unset"));
    }
}
