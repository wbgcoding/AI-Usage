namespace AiUsage.Views.Controls;

/// <summary>The tray popup's content: no logic, everything comes through the DataContext, a
/// <see cref="Services.TrayPopupModel"/>.</summary>
public partial class TrayPopupView : UserControl
{
    public TrayPopupView()
    {
        InitializeComponent();
    }

    private const int MaxMeasurePasses = 4;

    /// <summary>The size the content needs, in device independent units. One measure is not enough:
    /// the rows share their label and percent columns, and a column widened by a later row reaches
    /// the earlier rows only in the next layout pass, so the content is measured until its size
    /// holds still.</summary>
    internal System.Windows.Size MeasureContent()
    {
        var unbounded = new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity);
        Measure(unbounded);
        var size = DesiredSize;
        for (var pass = 1; pass < MaxMeasurePasses; pass++)
        {
            Arrange(new System.Windows.Rect(size));
            UpdateLayout();
            InvalidateMeasure();
            Measure(unbounded);
            var next = DesiredSize;
            if (next.Width <= size.Width && next.Height <= size.Height)
                break;
            size = new System.Windows.Size(Math.Max(size.Width, next.Width), Math.Max(size.Height, next.Height));
        }
        return size;
    }
}
