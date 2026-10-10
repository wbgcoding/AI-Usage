namespace AiUsage.Services;

/// <summary>
/// Caches the last monitor enumeration and only re-enumerates on an explicit <see cref="Refresh"/> -
/// NativeMonitors.WorkAreas() is a Win32 EnumDisplayMonitors round trip, too costly to repeat on the
/// once-a-second tick when the monitor layout essentially never changes mid-session.
/// </summary>
internal sealed class MonitorAreaCache(Func<IReadOnlyList<MonitorArea>> lookup)
{
    private IReadOnlyList<MonitorArea> _areas = lookup();

    public IReadOnlyList<MonitorArea> Areas => _areas;

    public void Refresh() => _areas = lookup();
}
