using System.Reflection;

namespace AiUsage.Services;

/// <summary>
/// The handful of facts the About window shows. One constant for the archive
/// URL so a test can assert it is never duplicated as a second literal elsewhere.
/// </summary>
public static class AppInfo
{
    public const string ProductName = "AI-Usage";

    /// <summary>The copyright line, read from the assembly so it is defined once in the build props.</summary>
    public static string Copyright { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    public const string ArchiveUrl = "https://github.com/wbgcoding/AI-Usage";

    /// <summary>The FAQ section of the project page, opened from the About section and the tile menu.</summary>
    public const string HelpUrl = ArchiveUrl + "#faq";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>True for a copy the installer put on this machine, false for a portable copy. It
    /// selects the update route (run the setup versus swap the exe in place), so a wrong answer
    /// updates the wrong way. A copy counts as installed when the setup's uninstaller sits next to
    /// the exe (it works for a folder the user picked) or when the exe lies under one of the two
    /// default roots (<c>installer/AiUsage.iss</c>'s <c>{autopf}</c>: per-machine under Program
    /// Files, per-user under the local profile's own Programs folder).</summary>
    public static bool IsInstalled => IsInstalledAt(
        Environment.ProcessPath,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    internal static bool IsInstalledAt(string? exePath, string localAppData, string programFiles)
    {
        if (string.IsNullOrEmpty(exePath))
            return false;

        var directory = Path.GetDirectoryName(exePath);
        if (directory is not null && File.Exists(Path.Combine(directory, "unins000.exe")))
            return true;

        var installRoots = new[]
        {
            Path.Combine(localAppData, "Programs", ProductName),
            Path.Combine(programFiles, ProductName),
        };
        return installRoots.Any(root => exePath.StartsWith(
            root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
