using AiUsage.Services;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

/// <summary>
/// The WebView2 install keeps its order and its guarantees whatever the outside steps do: nothing is
/// executed unless the signature check passed, the downloaded file never outlives the call, and every
/// failure ends as a result with a short technical line, never as an exception or a message text.
/// The outside steps are fakes here; the signature check itself is also run for real against files
/// of the installed .NET runtime.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class WebViewRuntimeInstallerTests
{
    private sealed class Rig
    {
        public readonly string Folder = TestPaths.CreateDirectory("webview-installer");
        public readonly List<string> Calls = [];
        public string? DownloadedFile;
        public string? RunPath;
        public string? RunArguments;
        public bool Installed;
        public int ExitCode;
        public Exception? DownloadFailure;
        public SignatureCheckResult Signature = new(true, "");

        public WebViewRuntimeInstaller Build() => new(
            download: (url, destination, _, ct) =>
            {
                Calls.Add("download:" + url);
                DownloadedFile = destination;
                if (DownloadFailure is not null)
                    return Task.FromException(DownloadFailure);
                File.WriteAllText(destination, "fake installer");
                return Task.CompletedTask;
            },
            verify: file =>
            {
                Calls.Add("verify");
                Assert.True(File.Exists(file));
                return Signature;
            },
            run: (path, arguments, ct) =>
            {
                Calls.Add("run");
                // Between the check and the run nobody else can open the file for writing or delete it.
                Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
                Assert.Throws<IOException>(() => File.Delete(path));
                RunPath = path;
                RunArguments = arguments;
                Assert.True(File.Exists(path));
                return Task.FromResult(ExitCode);
            },
            isInstalled: () =>
            {
                Calls.Add("probe");
                return Installed;
            },
            tempFolder: () => Folder);
    }

    [Fact]
    public async Task A_signed_download_is_run_silently_and_the_runtime_is_checked_afterwards()
    {
        var rig = new Rig { Installed = true };
        var installingSeen = false;

        var result = await rig.Build().InstallAsync(null, () => installingSeen = true, CancellationToken.None);

        Assert.Equal(WebViewInstallOutcome.Installed, result.Outcome);
        Assert.Null(result.Cause);
        Assert.Equal(["download:" + WebViewRuntimeInstaller.BootstrapperUrl, "verify", "run", "probe"], rig.Calls);
        Assert.Equal("/silent /install", rig.RunArguments);
        Assert.True(installingSeen);
        Assert.False(File.Exists(rig.DownloadedFile), "the downloaded installer must be deleted afterwards");
    }

    [Fact]
    public async Task A_file_whose_signature_is_not_valid_is_never_run()
    {
        var rig = new Rig { Signature = new SignatureCheckResult(false, "signature: WinVerifyTrust 0x800B0100") };
        var installingSeen = false;

        var result = await rig.Build().InstallAsync(null, () => installingSeen = true, CancellationToken.None);

        Assert.Equal(WebViewInstallOutcome.SignatureInvalid, result.Outcome);
        Assert.Equal("signature: WinVerifyTrust 0x800B0100", result.Cause);
        Assert.DoesNotContain("run", rig.Calls);
        Assert.DoesNotContain("probe", rig.Calls);
        Assert.False(installingSeen);
        Assert.False(File.Exists(rig.DownloadedFile), "a rejected file must be deleted too");
    }

    [Fact]
    public async Task A_signature_check_that_throws_counts_as_invalid_and_runs_nothing()
    {
        var rig = new Rig();
        var installer = new WebViewRuntimeInstaller(
            (_, destination, _, _) => { File.WriteAllText(destination, "x"); return Task.CompletedTask; },
            _ => throw new InvalidOperationException("boom"),
            (_, _, _) => { rig.Calls.Add("run"); return Task.FromResult(0); },
            () => true,
            () => rig.Folder);

        var result = await installer.InstallAsync(null, null, CancellationToken.None);

        Assert.Equal(WebViewInstallOutcome.SignatureInvalid, result.Outcome);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task A_failed_download_runs_nothing_and_names_the_failure_without_its_message()
    {
        var rig = new Rig
        {
            DownloadFailure = new System.Net.Http.HttpRequestException(@"No such host (D:\data\someone\secret path)", null, System.Net.HttpStatusCode.NotFound),
        };

        var result = await rig.Build().InstallAsync(null, null, CancellationToken.None);

        Assert.Equal(WebViewInstallOutcome.DownloadFailed, result.Outcome);
        Assert.StartsWith("download: HttpRequestException 0x", result.Cause);
        Assert.EndsWith(LocalizationService.Instance.Format("Error.StatusSuffix", 404), result.Cause);
        Assert.DoesNotContain("someone", result.Cause);
        Assert.Equal(["download:" + WebViewRuntimeInstaller.BootstrapperUrl], rig.Calls);
    }

    [Fact]
    public async Task A_cancelled_download_ends_as_cancelled_and_runs_nothing()
    {
        var rig = new Rig { DownloadFailure = new OperationCanceledException() };
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var result = await rig.Build().InstallAsync(null, null, cancel.Token);

        Assert.Equal(WebViewInstallOutcome.Cancelled, result.Outcome);
        Assert.Null(result.Cause);
        Assert.DoesNotContain("verify", rig.Calls);
    }

    // HttpClient's own timeout surfaces as a TaskCanceledException while the caller never cancelled.
    [Fact]
    public async Task A_download_that_times_out_is_a_failure_not_a_cancel()
    {
        var rig = new Rig { DownloadFailure = new TaskCanceledException("timeout") };

        var result = await rig.Build().InstallAsync(null, null, CancellationToken.None);

        Assert.Equal(WebViewInstallOutcome.DownloadFailed, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Cause));
        Assert.DoesNotContain("verify", rig.Calls);
    }

    [Fact]
    public async Task A_failing_installer_is_reported_with_its_exit_code()
    {
        var rig = new Rig { ExitCode = 5, Installed = false };

        var result = await rig.Build().InstallAsync(null, null, CancellationToken.None);

        Assert.Equal(WebViewInstallOutcome.InstallerFailed, result.Outcome);
        Assert.Equal("install: exit code 5", result.Cause);
        Assert.False(File.Exists(rig.DownloadedFile));
    }

    [Fact]
    public async Task An_installer_error_code_does_not_matter_when_the_runtime_is_there_afterwards()
    {
        var rig = new Rig { ExitCode = 5, Installed = true };

        var result = await rig.Build().InstallAsync(null, null, CancellationToken.None);

        Assert.Equal(WebViewInstallOutcome.Installed, result.Outcome);
    }

    [Fact]
    public async Task A_clean_exit_without_a_runtime_afterwards_is_still_missing()
    {
        var rig = new Rig { ExitCode = 0, Installed = false };

        var result = await rig.Build().InstallAsync(null, null, CancellationToken.None);

        Assert.Equal(WebViewInstallOutcome.StillMissing, result.Outcome);
    }

    [Fact]
    public async Task An_installer_that_cannot_start_is_a_result_not_an_exception()
    {
        var rig = new Rig();
        var installer = new WebViewRuntimeInstaller(
            (_, destination, _, _) => { File.WriteAllText(destination, "x"); return Task.CompletedTask; },
            _ => new SignatureCheckResult(true, ""),
            (_, _, _) => throw new System.ComponentModel.Win32Exception(1223),
            () => false,
            () => rig.Folder);

        var result = await installer.InstallAsync(null, null, CancellationToken.None);

        Assert.Equal(WebViewInstallOutcome.InstallerFailed, result.Outcome);
        Assert.StartsWith("install: Win32Exception 0x", result.Cause);
    }

    [Theory]
    [InlineData("https://go.microsoft.com/fwlink/p/?LinkId=2124703", true)]
    [InlineData("https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/x/MicrosoftEdgeWebview2Setup.exe", true)]
    [InlineData("http://go.microsoft.com/fwlink/p/?LinkId=2124703", false)]
    [InlineData("https://go.microsoft.com.evil.example/fwlink", false)]
    [InlineData("https://evilgo.microsoft.com/fwlink", false)]
    [InlineData("https://example.com/MicrosoftEdgeWebview2Setup.exe", false)]
    [InlineData("https://download.microsoft.com/x.exe", false)]
    public void Only_the_two_microsoft_download_hosts_over_https_are_allowed(string url, bool expected) =>
        Assert.Equal(expected, WebViewRuntimeInstaller.IsAllowedDownloadUrl(new Uri(url)));

    [Fact]
    public async Task The_real_download_refuses_an_address_outside_the_allow_list_before_any_request()
    {
        var destination = Path.Combine(TestPaths.CreateDirectory("webview-installer-download"), "setup.exe");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WebViewRuntimeInstaller.DownloadAsync("https://example.invalid/setup.exe", destination, null, CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    // The real signature check, against files that exist on every machine running these tests: the core
    // library of the installed .NET runtime is signed by Microsoft Corporation.

    private static string SignedMicrosoftFile() => typeof(object).Assembly.Location;

    [Fact]
    public void A_file_signed_by_microsoft_passes_the_real_check()
    {
        var result = AuthenticodeSignature.Check(SignedMicrosoftFile());

        Assert.True(result.IsValid, result.Detail);
    }

    [Fact]
    public void A_valid_signature_of_another_publisher_does_not_pass()
    {
        var result = AuthenticodeSignature.Check(SignedMicrosoftFile(), "Some Other Publisher");

        Assert.False(result.IsValid);
        Assert.Contains("signer", result.Detail);
    }

    [Fact]
    public void A_changed_signed_file_does_not_pass()
    {
        var copy = Path.Combine(TestPaths.CreateDirectory("webview-installer-tamper"), "changed.dll");
        File.Copy(SignedMicrosoftFile(), copy);
        using (var stream = new FileStream(copy, FileMode.Open, FileAccess.Write))
        {
            stream.Seek(1024, SeekOrigin.Begin);
            stream.WriteByte(0x42);
        }

        var result = AuthenticodeSignature.Check(copy);

        Assert.False(result.IsValid);
        Assert.StartsWith("signature: WinVerifyTrust 0x", result.Detail);
    }

    [Fact]
    public void An_unsigned_file_does_not_pass()
    {
        var file = Path.Combine(TestPaths.CreateDirectory("webview-installer-unsigned"), "setup.exe");
        File.WriteAllText(file, "MZ not really an executable");

        var result = AuthenticodeSignature.Check(file);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_revoked_certificate_is_rejected_online_and_offline()
    {
        const int revoked = unchecked((int)0x800B010C);

        Assert.False(AuthenticodeSignature.IsTrustResultAcceptable(revoked, hasInternet: true));
        Assert.False(AuthenticodeSignature.IsTrustResultAcceptable(revoked, hasInternet: false));
    }

    [Fact]
    public void An_unreachable_revocation_server_passes_only_while_offline()
    {
        const int revocationFailure = unchecked((int)0x800B010E);

        Assert.True(AuthenticodeSignature.IsTrustResultAcceptable(revocationFailure, hasInternet: false));
        Assert.False(AuthenticodeSignature.IsTrustResultAcceptable(revocationFailure, hasInternet: true));
    }

    [Fact]
    public void Only_a_clean_result_passes_regardless_of_the_network()
    {
        Assert.True(AuthenticodeSignature.IsTrustResultAcceptable(0, hasInternet: true));
        Assert.True(AuthenticodeSignature.IsTrustResultAcceptable(0, hasInternet: false));
        Assert.False(AuthenticodeSignature.IsTrustResultAcceptable(unchecked((int)0x800B0100), hasInternet: false));
    }
}
