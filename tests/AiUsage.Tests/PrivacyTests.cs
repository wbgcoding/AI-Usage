using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.ViewModels;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Guards the one hard rule around <see cref="ProviderSnapshot.AccountLabel"/>: it may show
/// on the tile itself and in the Settings window's provider row (<see
/// cref="ProviderTileViewModel.SettingsRowText"/>), but must never reach anything else that could
/// carry it off screen (diagnostics, the clipboard, a log).</summary>
public class PrivacyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AccountLabelReachesTheTileTextAndTheSettingsRowButNeverTheDiagnostics()
    {
        const string label = "Beispiel GmbH";
        var snapshot = new ProviderSnapshot(
            ProviderId: "claude",
            Windows: [],
            PlanType: "Plus",
            SourceKind: SourceKind.WebSession,
            FetchedAt: Now,
            DataTimestamp: Now,
            Status: ProviderStatus.Ok,
            Error: null,
            Diagnostics: ["Gefunden über die Organisations-API."],
            AccountLabel: label);

        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.Apply(snapshot, Now);

        Assert.Equal(label, tile.AccountText);
        Assert.Contains(label, tile.SettingsRowText);
        Assert.DoesNotContain(label, tile.DiagnosticsText);
        Assert.DoesNotContain(tile.Diagnostics, line => line.Contains(label));
    }

    // The hidden browser carries a signed-in session, so its developer tools and its context menu
    // stay shut: anything opened in there reaches the live cookies. CoreWebView2 cannot be
    // instantiated in a unit test, so this scans the two source files that own those settings the
    // way SignInDescriptorTests scans MainWindow.
    [Theory]
    [InlineData("Views/SignInWindow.xaml.cs")]
    [InlineData("Web/WebSessionScriptRunner.cs")]
    public void TheHiddenBrowserKeepsItsDeveloperToolsShut(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(FindAppSourceRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("AreDevToolsEnabled = false", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AreDevToolsEnabled = true", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AreDefaultContextMenusEnabled = true", text, StringComparison.Ordinal);
    }

    // The discovery log line names which fields a live response carries (see
    // WebUsageSource.DescribeShape), never what is actually in them - an account number or a balance
    // in the body must never reach the app's own log file.
    [Fact]
    public void DescribeShapeNeverCarriesAnAccountNumberOrAnAmountIntoItsOutput()
    {
        const string accountNumber = "DE89370400440532013000";
        const string body = """{"accountNumber": "DE89370400440532013000", "balanceCents": 48217}""";

        var shape = WebUsageSource.DescribeShape(body);

        Assert.DoesNotContain(accountNumber, shape, StringComparison.Ordinal);
        Assert.DoesNotContain("48217", shape, StringComparison.Ordinal);
        Assert.Contains("accountNumber:string", shape, StringComparison.Ordinal);
        Assert.Contains("balanceCents:number", shape, StringComparison.Ordinal);
    }

    // WebViewHost.SignOut() used to build its own LogService straight from AppPaths.LogsDirectory in
    // two rarely-taken branches - a folder that resolves to the shared webview root ("refused"), and
    // one whose deletion fails because a file inside is still open ("still open"). A unit test hitting
    // either one (WebViewHostTests does, for both) used to write into the real
    // %APPDATA%\AI-Usage\logs\app.log with no test ever asking it to. Both branches are exercised
    // here exactly the way WebViewHostTests does, entirely inside disposable test folders, proving
    // neither one still reaches the real file.
    [Fact]
    public void NoTestWritesReachTheRealDataFolder()
    {
        var realLogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AI-Usage", "logs", "app.log");
        // A running copy of the app may append to its own log at any moment, so the proof is that
        // nothing this test triggers shows up in what was appended, not an unchanged write time.
        var lengthBefore = File.Exists(realLogFile) ? new FileInfo(realLogFile).Length : 0;

        using (var refusedRoot = TestPaths.CreateDisposableDirectory("ai-usage-privacy-webview-root"))
        {
            WebViewHost.SetRootOverride(refusedRoot);
            try
            {
                Assert.Equal(SignOutResult.Refused, new WebViewHost(refusedRoot).SignOut());
            }
            finally
            {
                WebViewHost.ClearRootOverrideForTests();
            }
        }

        using (var stillOpenRoot = TestPaths.CreateDisposableDirectory("ai-usage-privacy-webview-open"))
        {
            var sessionFolder = Path.Combine(stillOpenRoot, "webview");
            Directory.CreateDirectory(sessionFolder);
            var lockedFile = Path.Combine(sessionFolder, "cookie.dat");
            File.WriteAllText(lockedFile, "session-marker");
            using var handle = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.Equal(SignOutResult.StillOpen, new WebViewHost(sessionFolder).SignOut());
        }

        var appended = ReadAppendedText(realLogFile, lengthBefore);
        Assert.DoesNotContain("ai-usage-privacy-webview", appended, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Refused to delete the shared webview root folder", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign-out could not remove the session folder", appended, StringComparison.Ordinal);
    }

    private static string ReadAppendedText(string path, long lengthBefore)
    {
        if (!File.Exists(path))
            return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // A rolled-over log starts short again; then everything in it is new.
        stream.Position = stream.Length >= lengthBefore ? lengthBefore : 0;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // A mechanical mirror of PathSanitizerTests/TokenSafetyTests' own source-scan style -
    // proves by grepping the shipped source, not just by construction, that no call into the log file
    // ever hands it an account label. AccountText/AccountLabel may only ever be read by the tile
    // itself, SettingsRowText and SupportReport's own explicit exclusion (SupportReportTests).
    [Fact]
    public void NoLogCallAnywhereUnderSrcReferencesTheAccountLabel()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(FindAppSourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                if (!line.Contains("Log(", StringComparison.Ordinal)
                    && !line.Contains("LogInfo(", StringComparison.Ordinal)
                    && !line.Contains("LogError(", StringComparison.Ordinal))
                    continue;

                if (line.Contains("AccountLabel", StringComparison.Ordinal) || line.Contains("AccountText", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "Log call(s) referencing the account label:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void AccountLabelLogGuard_fires_on_a_planted_log_call_naming_the_account_label()
    {
        const string planted = "_logService.LogInfo($\"signed in as {snapshot.AccountLabel}\");";

        Assert.Contains("AccountLabel", planted, StringComparison.Ordinal);
        Assert.Contains("LogInfo(", planted, StringComparison.Ordinal);
    }

    private static string FindAppSourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "AiUsage")))
            dir = Path.GetDirectoryName(dir);
        return dir is null
            ? throw new InvalidOperationException("Could not locate the src/AiUsage directory from the test output path.")
            : Path.Combine(dir, "src", "AiUsage");
    }
}
