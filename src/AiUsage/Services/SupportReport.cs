using System.Globalization;
using System.Text;
using AiUsage.Storage;
using AiUsage.ViewModels;

namespace AiUsage.Services;

/// <summary>
/// The whole app state worth pasting into a bug report, as one plain-text block - everything the
/// per-tile "Copy details" button already offers, plus the facts that only make sense once, for the
/// app as a whole. Pure: reads only <see cref="AppInfo"/>'s own static facts, the two ambient
/// singletons every view model already reads the same way (<see cref="LocalizationService.Instance"/>,
/// <see cref="ThemeService.CurrentTheme"/>), <see cref="AppPaths.DataDirectory"/> and
/// <see cref="Environment.OSVersion"/> - never a live provider fetch, so building this report can
/// never itself touch the network.
/// </summary>
public static class SupportReport
{
    /// <summary>
    /// PRIVACY: deliberately never includes a tile's own <c>AccountText</c> (see that property's own
    /// doc in <see cref="ProviderTileViewModel"/>), any full user path, or a provider's own account
    /// identifiers. The data folder is the one path included, and only with the user name replaced
    /// via <see cref="PathSanitizer"/> - the same guard <c>LogService</c> applies to every
    /// line it writes.
    /// </summary>
    public static string Build(IReadOnlyList<ProviderTileViewModel> tiles)
    {
        var loc = LocalizationService.Instance;
        var lines = new StringBuilder();

        lines.AppendLine(CultureInfo.CurrentCulture, $"{AppInfo.ProductName} {loc.Format("About.Version", AppInfo.Version)}");
        lines.AppendLine(AppInfo.Copyright);
        lines.AppendLine(loc[AppInfo.IsInstalled ? "About.Installed" : "About.Portable"]);
        lines.AppendLine(Environment.OSVersion.VersionString);
        lines.AppendLine(loc[$"Theme.{ThemeService.CurrentTheme}"]);
        lines.AppendLine(loc.ActiveLanguageDisplayName);
        lines.AppendLine(loc.Format("About.DataFolder", PathSanitizer.Sanitize(AppPaths.DataDirectory)));
        lines.AppendLine();

        foreach (var tile in tiles)
        {
            lines.AppendLine(CultureInfo.CurrentCulture, $"{tile.DisplayName}: {tile.HeadlineText} ({tile.SourceBadgeText}) - {tile.LastUpdatedText}");
            foreach (var diagnostic in tile.Diagnostics)
                lines.AppendLine(CultureInfo.CurrentCulture, $"  {diagnostic}");
        }

        return lines.ToString();
    }
}
