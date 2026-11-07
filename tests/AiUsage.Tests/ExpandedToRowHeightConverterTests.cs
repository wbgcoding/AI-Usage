using System.Globalization;
using System.Windows;
using AiUsage.Views.Controls;
using Xunit;

namespace AiUsage.Tests;

public class ExpandedToRowHeightConverterTests
{
    private readonly ExpandedToRowHeightConverter _converter = new();

    [Fact]
    public void An_expanded_section_takes_an_equal_share_of_the_row_group()
    {
        var height = (GridLength)_converter.Convert(true, typeof(GridLength), null, CultureInfo.InvariantCulture);

        Assert.True(height.IsStar);
        Assert.Equal(1, height.Value);
    }

    [Fact]
    public void An_expanded_section_that_sizes_to_its_content_keeps_an_auto_row()
    {
        var height = (GridLength)_converter.Convert(true, typeof(GridLength), true, CultureInfo.InvariantCulture);

        Assert.True(height.IsAuto);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void A_collapsed_section_keeps_only_its_header(object? expanded)
    {
        var height = (GridLength)_converter.Convert(expanded!, typeof(GridLength), null, CultureInfo.InvariantCulture);

        Assert.True(height.IsAuto);
    }
}
