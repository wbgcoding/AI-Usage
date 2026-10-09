using System.Reflection;
using System.Windows;

namespace AiUsage.Tests;

/// <summary>WPF keeps "an application is shutting down" in a process-wide static that a test's
/// <c>Application.Shutdown()</c> sets and that nothing clears again once that test's dispatcher is
/// gone. While it is set, <c>Window.Show()</c> returns without creating a window, so a test that
/// shows a plain window (no <c>AiUsage.App</c> of its own) clears it first. The classes that do so
/// sit in <see cref="SharedStateTestsCollection"/>, so no application shuts down while they run.</summary>
internal static class WpfShutdownState
{
    public static void Clear() =>
        typeof(Application).GetField("_isShuttingDown", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
}
