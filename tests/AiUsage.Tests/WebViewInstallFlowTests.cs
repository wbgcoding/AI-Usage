using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// The decisions before a sign-in window may open when the WebView2 Runtime might be missing: the
/// question is asked only when it is missing, declining or cancelling ends quietly, a failure is
/// shown with its technical line, and only a runtime that is really there lets the sign-in go on.
/// </summary>
public class WebViewInstallFlowTests
{
    private sealed class FakeUi(bool answer, WebViewInstallResult result) : IWebViewInstallUi
    {
        public int Questions;
        public int Installs;
        public readonly List<string?> Failures = [];

        public bool ConfirmInstall()
        {
            Questions++;
            return answer;
        }

        public WebViewInstallResult RunInstall(WebViewRuntimeInstaller installer)
        {
            Installs++;
            return result;
        }

        public void ShowFailure(string? cause) => Failures.Add(cause);
    }

    private static WebViewRuntimeInstaller Installer() => new(
        (_, _, _, _) => Task.CompletedTask, _ => new SignatureCheckResult(true, ""), (_, _, _) => Task.FromResult(0), () => true, () => "unused");

    [Fact]
    public void A_present_runtime_opens_the_sign_in_without_asking_anything()
    {
        var ui = new FakeUi(true, new WebViewInstallResult(WebViewInstallOutcome.Installed));

        Assert.True(WebViewInstallFlow.EnsureRuntime(() => true, Installer(), ui));

        Assert.Equal(0, ui.Questions);
        Assert.Equal(0, ui.Installs);
    }

    [Fact]
    public void A_missing_runtime_shows_the_install_question_and_declining_ends_it_quietly()
    {
        var ui = new FakeUi(false, new WebViewInstallResult(WebViewInstallOutcome.Installed));

        Assert.False(WebViewInstallFlow.EnsureRuntime(() => false, Installer(), ui));

        Assert.Equal(1, ui.Questions);
        Assert.Equal(0, ui.Installs);
        Assert.Empty(ui.Failures);
    }

    [Fact]
    public void A_successful_install_lets_the_sign_in_open_once_the_runtime_is_really_there()
    {
        var present = false;
        var ui = new FakeUi(true, new WebViewInstallResult(WebViewInstallOutcome.Installed));
        var installer = Installer();

        // The probe flips only after the install ran, like the real runtime does.
        bool Probe() => present;
        var ran = false;
        var flowUi = new FlipUi(ui, () => { present = true; ran = true; });

        Assert.True(WebViewInstallFlow.EnsureRuntime(Probe, installer, flowUi));

        Assert.True(ran);
        Assert.Empty(ui.Failures);
    }

    private sealed class FlipUi(FakeUi inner, Action onInstall) : IWebViewInstallUi
    {
        public bool ConfirmInstall() => inner.ConfirmInstall();

        public WebViewInstallResult RunInstall(WebViewRuntimeInstaller installer)
        {
            onInstall();
            return inner.RunInstall(installer);
        }

        public void ShowFailure(string? cause) => inner.ShowFailure(cause);
    }

    [Fact]
    public void An_install_that_reports_success_but_leaves_no_runtime_does_not_open_the_sign_in()
    {
        var ui = new FakeUi(true, new WebViewInstallResult(WebViewInstallOutcome.Installed));

        Assert.False(WebViewInstallFlow.EnsureRuntime(() => false, Installer(), ui));
    }

    [Fact]
    public void A_cancelled_install_ends_quietly()
    {
        var ui = new FakeUi(true, new WebViewInstallResult(WebViewInstallOutcome.Cancelled));

        Assert.False(WebViewInstallFlow.EnsureRuntime(() => false, Installer(), ui));

        Assert.Empty(ui.Failures);
    }

    [Theory]
    [InlineData(WebViewInstallOutcome.DownloadFailed)]
    [InlineData(WebViewInstallOutcome.SignatureInvalid)]
    [InlineData(WebViewInstallOutcome.InstallerFailed)]
    [InlineData(WebViewInstallOutcome.StillMissing)]
    public void A_failed_install_shows_the_failure_with_its_technical_line_and_opens_no_sign_in(WebViewInstallOutcome outcome)
    {
        var ui = new FakeUi(true, new WebViewInstallResult(outcome, "install: exit code 5"));

        Assert.False(WebViewInstallFlow.EnsureRuntime(() => false, Installer(), ui));

        Assert.Equal(["install: exit code 5"], ui.Failures);
    }

    [Fact]
    public async Task An_invalid_signature_through_the_real_flow_runs_nothing_and_ends_in_the_failure_notice()
    {
        var ran = false;
        var installer = new WebViewRuntimeInstaller(
            (_, destination, _, _) => { File.WriteAllText(destination, "x"); return Task.CompletedTask; },
            _ => new SignatureCheckResult(false, "signature: WinVerifyTrust 0x800B0100"),
            (_, _, _) => { ran = true; return Task.FromResult(0); },
            () => false,
            () => TestPaths.CreateDirectory("webview-install-flow"));
        var ui = new RealInstallUi();

        var opened = await Task.Run(() => WebViewInstallFlow.EnsureRuntime(() => false, installer, ui));

        Assert.False(opened);
        Assert.False(ran);
        Assert.Equal(["signature: WinVerifyTrust 0x800B0100"], ui.Failures);
    }

    private sealed class RealInstallUi : IWebViewInstallUi
    {
        public readonly List<string?> Failures = [];

        public bool ConfirmInstall() => true;

        public WebViewInstallResult RunInstall(WebViewRuntimeInstaller installer) =>
            installer.InstallAsync(null, null, CancellationToken.None).GetAwaiter().GetResult();

        public void ShowFailure(string? cause) => Failures.Add(cause);
    }

    [Fact]
    public void The_sign_in_flow_asks_the_install_flow_before_it_opens_a_window()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "AiUsage", "Views", "WebSignInFlow.cs"));

        var ensure = source.IndexOf("WebViewInstallFlow.EnsureRuntime(", StringComparison.Ordinal);
        var window = source.IndexOf("new SignInWindow(", StringComparison.Ordinal);
        Assert.True(ensure >= 0 && window > ensure, "the runtime is ensured before the sign-in window is built");
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
