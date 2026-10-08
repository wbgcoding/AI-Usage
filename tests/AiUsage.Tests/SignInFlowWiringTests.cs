using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// The two entry points of the web sign-in (a tile's button, the settings button) cannot be built
/// without a real WebView2 window, so this reads their source: both hand the finished flow to
/// <c>MainViewModel.CompleteSignIn</c> and neither reconnects the account before the window opens.
/// </summary>
public class SignInFlowWiringTests
{
    [Theory]
    [InlineData("MainWindow.xaml.cs", "WebSignInFlow.Open(this, ProviderRegistry.WebSessionFor(accountKey)")]
    [InlineData("SettingsWindow.xaml.cs", "WebSignInFlow.Open(this, ProviderRegistry.WebSessionFor(tile.ProviderId)")]
    public void TheWebSignInReconnectsOnlyThroughTheFinishedFlow(string file, string openCall)
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AiUsage", "Views", file));

        var open = source.IndexOf(openCall, StringComparison.Ordinal);
        Assert.True(open >= 0, $"{openCall} not found in {file}");

        // Nothing between the line before the call and the call itself reconnects the account.
        var lineStart = source.LastIndexOf('\n', source.LastIndexOf('\n', open - 1) - 1);
        Assert.DoesNotContain("Reconnect(", source[lineStart..open], StringComparison.Ordinal);

        var after = source[open..Math.Min(source.Length, open + 400)];
        Assert.Contains("CompleteSignIn(", after, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find the repository root (.git) above " + AppContext.BaseDirectory);
    }
}
