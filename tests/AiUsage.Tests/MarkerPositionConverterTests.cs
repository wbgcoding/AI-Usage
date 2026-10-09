using System.Globalization;
using System.Windows;
using AiUsage.ViewModels;
using Xunit;

namespace AiUsage.Tests;

public class MarkerPositionConverterTests
{
    private static double Left(double? percent, double trackWidth)
    {
        var converter = new MarkerPositionConverter();
        var result = (Thickness)converter.Convert([percent!, trackWidth], typeof(Thickness), null, CultureInfo.InvariantCulture);
        return result.Left;
    }

    [Fact]
    public void ZeroPercentSitsAtTheLeftEdge() =>
        Assert.Equal(0, Left(0, 100));

    [Fact]
    public void FiftyPercentSitsHalfway() =>
        Assert.Equal(50, Left(50, 100));

    [Fact]
    public void HundredPercentNeverPushesTheMarkerPastTheRightEdge() =>
        Assert.Equal(99, Left(100, 100)); // track width minus the marker's own 1px width

    [Fact]
    public void MissingWidthOrPercentStaysAtZero()
    {
        Assert.Equal(0, Left(null, 100));
        Assert.Equal(0, Left(50, 0));
    }

    [Fact]
    public void ParameterWidthCentresTheMarkerOnThePercentAndKeepsItInsideTheTrack()
    {
        var converter = new MarkerPositionConverter();
        double Centred(double percent) =>
            ((Thickness)converter.Convert([percent, 100.0], typeof(Thickness), "2", CultureInfo.InvariantCulture)).Left;

        Assert.Equal(49, Centred(50));
        Assert.Equal(0, Centred(0));
        Assert.Equal(98, Centred(100));
    }
}
