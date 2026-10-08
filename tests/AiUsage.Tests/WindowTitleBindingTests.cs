using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace AiUsage.Tests;

/// <summary>
/// A dialog that stays open across a language switch - Settings itself, since that is where the
/// switch happens - must keep every visible string live, including its own title bar. Three
/// windows used to assign their title once in the constructor instead of binding it, so the title
/// bar (and, for Settings, nothing else) stayed in the old language until the window was reopened.
/// Static XAML/source scan rather than constructing a live Window: <see cref="AccessibilityTests"/>
/// documents why an actual visual-tree instantiation hangs `dotnet test` in this codebase.
/// </summary>
public class WindowTitleBindingTests
{
    private static readonly string SrcViewsDir = FindSrcViewsDir();

    private static string FindSrcViewsDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(here))!;
        var repoRoot = Path.GetDirectoryName(testsDir)!;
        return Path.Combine(repoRoot, "src", "AiUsage", "Views");
    }

    private static readonly Regex WindowTitleAttribute = new(@"Title=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex TitleTextAttribute = new(@"TitleText=""([^""]*)""", RegexOptions.Compiled);

    [Theory]
    [InlineData("SettingsWindow.xaml", "Settings.Title")]
    [InlineData("SignInWindow.xaml", "SignIn.Title")]
    public void The_window_title_binds_live_to_LocalizationService_instead_of_a_literal(string fileName, string resourceKey)
    {
        var text = File.ReadAllText(Path.Combine(SrcViewsDir, fileName));

        var match = WindowTitleAttribute.Match(text);
        Assert.True(match.Success, $"{fileName}: no Window Title attribute found.");
        Assert.StartsWith("{Binding", match.Groups[1].Value);
        Assert.Contains("LocalizationService.Instance", match.Groups[1].Value);
        Assert.Contains($"Path=[{resourceKey}]", match.Groups[1].Value);
    }

    [Theory]
    [InlineData("SettingsWindow.xaml", "Settings.Title")]
    [InlineData("SignInWindow.xaml", "SignIn.Title")]
    public void The_title_bar_control_binds_live_to_LocalizationService_instead_of_a_literal(string fileName, string resourceKey)
    {
        var text = File.ReadAllText(Path.Combine(SrcViewsDir, fileName));

        var match = TitleTextAttribute.Match(text);
        Assert.True(match.Success, $"{fileName}: no TitleBar TitleText attribute found.");
        Assert.StartsWith("{Binding", match.Groups[1].Value);
        Assert.Contains("LocalizationService.Instance", match.Groups[1].Value);
        Assert.Contains($"Path=[{resourceKey}]", match.Groups[1].Value);
    }

    [Theory]
    [InlineData("SettingsWindow.xaml.cs")]
    [InlineData("SignInWindow.xaml.cs")]
    public void The_code_behind_no_longer_assigns_the_title_bar_text_once_in_the_constructor(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(SrcViewsDir, fileName));

        Assert.DoesNotContain("TitleBarControl.TitleText =", text);
    }

    // ConfirmWindow and CrashWindow genuinely receive a caller-supplied title (not a fixed
    // resource-key lookup), so they keep the code assignment - this locks that exception in so a
    // future change cannot silently widen the fix onto them too.
    [Theory]
    [InlineData("ConfirmWindow.xaml.cs")]
    [InlineData("CrashWindow.xaml.cs")]
    public void A_window_with_a_genuinely_caller_supplied_title_still_assigns_it_in_code(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(SrcViewsDir, fileName));

        Assert.Contains("TitleBarControl.TitleText =", text);
    }
}
