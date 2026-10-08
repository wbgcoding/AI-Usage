using System.Net.NetworkInformation;

namespace AiUsage.Services;

/// <summary>
/// Whether this machine has any network connection at all - the one place that answers "did we
/// never get out the door" versus "the provider itself said no", so a network-side failure can show
/// the user the right explanation instead of one written for the provider.
/// </summary>
public static class NetworkStatus
{
    // A substituted answer flows with the async call chain that set it, not process-wide: tests that
    // run side by side each see only their own answer instead of overwriting one another's.
    private static readonly AsyncLocal<Func<bool>?> Override = new();

    /// <summary>Test seam: production wraps <see cref="NetworkInterface.GetIsNetworkAvailable"/>, a
    /// test substitutes a fixed answer instead of depending on the real adapter state. The
    /// substitute is visible only to the code the setting caller itself runs or starts.</summary>
    internal static Func<bool> Probe
    {
        get => Override.Value ?? NetworkInterface.GetIsNetworkAvailable;
        set => Override.Value = value;
    }

    public static bool HasInternet() => Probe();
}
