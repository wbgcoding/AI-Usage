namespace AiUsage.Models;

/// <summary>Where the widget was left for one display setup (the set of monitors and their areas):
/// back on that setup again, it returns here. See <see cref="WindowSettings.Setups"/>.</summary>
public sealed class WindowSetupPlacement
{
    /// <summary>The setup's key, built by <see cref="Services.WindowPlacementService.SetupKey"/>.</summary>
    public string Setup { get; set; } = "";

    public double Left { get; set; }

    public double Top { get; set; }

    public string MonitorDeviceName { get; set; } = "";
}
