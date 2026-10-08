using AiUsage.Services;

namespace AiUsage.Tests;

/// <summary>
/// Only the pure parts: recognising the switch and resolving the target path. Actually creating
/// the shortcut goes through COM interop and would leave a real file on whatever desktop the test
/// runs on, so <see cref="DesktopShortcutService.Create"/> itself is not exercised here.
/// </summary>
public class DesktopShortcutServiceTests
{
    [Fact]
    public void IsRequested_recognises_the_switch_case_insensitively()
    {
        Assert.True(DesktopShortcutService.IsRequested(["--create-desktop-shortcut"]));
        Assert.True(DesktopShortcutService.IsRequested(["--CREATE-DESKTOP-SHORTCUT"]));
        Assert.False(DesktopShortcutService.IsRequested(["--tray"]));
        Assert.False(DesktopShortcutService.IsRequested([]));
    }

    [Fact]
    public void ResolveTargetPath_points_at_a_lnk_file_on_the_desktop()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AI-Usage.lnk");

        Assert.Equal(expected, DesktopShortcutService.ResolveTargetPath());
    }
}
