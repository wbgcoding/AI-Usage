using System.Runtime.InteropServices;

namespace AiUsage.Services;

/// <summary>What the machine says about its power right now: running on the battery, and the
/// system-wide energy saver (Windows battery saver) being switched on.</summary>
public readonly record struct PowerState(bool OnBattery, bool EnergySaver);

/// <summary>Reads <see cref="PowerState"/> from the system. The interpretation is split from the
/// native call so a test can feed it raw values.</summary>
public static class PowerStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>The current state; mains power and no energy saver when the system cannot say.</summary>
    public static PowerState Read() =>
        GetSystemPowerStatus(out var status) ? Interpret(status.ACLineStatus, status.SystemStatusFlag) : default;

    /// <summary><paramref name="acLineStatus"/> is 0 offline, 1 online, 255 unknown (a desktop
    /// without a battery reports online or unknown, never offline);
    /// <paramref name="systemStatusFlag"/> is 1 while the energy saver is on.</summary>
    internal static PowerState Interpret(byte acLineStatus, byte systemStatusFlag) =>
        new(OnBattery: acLineStatus == 0, EnergySaver: systemStatusFlag == 1);
}
