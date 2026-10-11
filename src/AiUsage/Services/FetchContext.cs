namespace AiUsage.Services;

/// <summary>What the scheduler tells a provider about the fetch it is running. A provider that reads a
/// local source and the network in one fetch checks <see cref="NetworkHeld"/> and leaves the network
/// part out while a pause or the energy saver holds network reads back.</summary>
internal static class FetchContext
{
    private static readonly AsyncLocal<bool> Held = new();

    /// <summary>True inside a fetch that runs only for its local source: no network read may start.</summary>
    public static bool NetworkHeld => Held.Value;

    /// <summary>Marks the current flow as network held until the returned scope is disposed.</summary>
    internal static IDisposable HoldNetwork()
    {
        var previous = Held.Value;
        Held.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Held.Value = previous;
    }
}
