namespace AiUsage.Services;

/// <summary>
/// Caches the last monitor enumeration and only re-enumerates on an explicit <see cref="Refresh"/> -
/// MainWindow used to call NativeMonitors.WorkAreas() (a Win32 EnumDisplayMonitors round trip) from
/// four places, every one of them re-run by the once-a-second tick timer even though the monitor
/// layout essentially never changes mid-session.
/// </summary>
internal sealed class MonitorAreaCache(Func<IReadOnlyList<MonitorArea>> lookup)
{
    private IReadOnlyList<MonitorArea> _areas = lookup();

    public IReadOnlyList<MonitorArea> Areas => _areas;

    public void Refresh() => _areas = lookup();
}
