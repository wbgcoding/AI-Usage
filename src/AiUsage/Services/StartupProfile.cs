using System.Runtime;

namespace AiUsage.Services;

/// <summary>
/// The runtime's own start-up cache: with a profile root set, the multi-core JIT records which
/// methods a run compiled and compiles the same ones on background threads at the next start, before
/// the code asks for them. The folder lies under the machine-local app data (like the sign-in browser
/// profiles), never in the roaming data folder. Nothing here may stop the start: every failure is
/// ignored.
/// </summary>
internal static class StartupProfile
{
    /// <summary>The profile's file name inside the cache folder.</summary>
    internal const string ProfileName = "startup.jitprofile";

    /// <summary>The machine-local cache folder: <c>%LOCALAPPDATA%\AI-Usage\cache</c>.</summary>
    internal static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Usage", "cache");

    /// <summary>Points the runtime at the cache folder and starts the profile. False when any step
    /// failed. The two actions are the test seams for the runtime calls.</summary>
    internal static bool Start(string? directory = null, Action<string>? setRoot = null, Action<string>? startProfile = null)
    {
        try
        {
            var folder = directory ?? CacheDirectory;
            Directory.CreateDirectory(folder);
            (setRoot ?? ProfileOptimization.SetProfileRoot)(folder);
            (startProfile ?? ProfileOptimization.StartProfile)(ProfileName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or NotSupportedException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
