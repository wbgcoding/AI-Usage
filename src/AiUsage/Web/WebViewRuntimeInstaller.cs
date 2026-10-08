using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace AiUsage.Web;

public enum WebViewInstallOutcome
{
    Installed,
    Cancelled,
    DownloadFailed,
    SignatureInvalid,
    InstallerFailed,
    StillMissing,
}

/// <summary><paramref name="Cause"/> is a short technical line for the error dialog (never a path,
/// never a message text), null when nothing went wrong or the user cancelled.</summary>
public sealed record WebViewInstallResult(WebViewInstallOutcome Outcome, string? Cause = null);

/// <summary>What the signature check found. <paramref name="Detail"/> is the technical line shown
/// when it is not valid.</summary>
public sealed record SignatureCheckResult(bool IsValid, string Detail);

/// <summary>
/// Installs Microsoft's WebView2 Runtime for the sign-in window when it is missing: downloads the
/// small bootstrapper from Microsoft, checks that it is signed by Microsoft, runs it silently and
/// checks again. Each outside step (download, signature check, running the installer, probing the
/// runtime) sits behind a plain delegate, so the order and the guarantees of this class are testable
/// without a network, a certificate or an installer: a file whose signature is not valid is never
/// run, the downloaded file never outlives the call, and nothing here ever throws to the caller.
/// </summary>
public sealed class WebViewRuntimeInstaller
{
    /// <summary>Microsoft's own address for the evergreen bootstrapper.</summary>
    public const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>The certificate subject the bootstrapper must be signed with.</summary>
    public const string RequiredSigner = "Microsoft Corporation";

    internal const string InstallerArguments = "/silent /install";

    // The two hosts the download touches: the fixed address above and the host it redirects to.
    // Exact names, no wildcard - a redirect anywhere else ends the download.
    private static readonly string[] AllowedDownloadHosts = ["go.microsoft.com", "msedge.sf.dl.delivery.mp.microsoft.com"];

    private const int MaxRedirects = 5;

    // The bootstrapper is about 2 MB; anything far beyond that is not it.
    private const long MaxDownloadBytes = 32L * 1024 * 1024;

    internal static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    internal delegate Task DownloadFile(string url, string destination, IProgress<double>? progress, CancellationToken ct);

    internal delegate Task<int> RunInstaller(string path, string arguments, CancellationToken ct);

    private readonly DownloadFile _download;
    private readonly Func<string, SignatureCheckResult> _verify;
    private readonly RunInstaller _run;
    private readonly Func<bool> _isInstalled;
    private readonly Func<string> _tempFolder;

    internal WebViewRuntimeInstaller(
        DownloadFile download, Func<string, SignatureCheckResult> verify, RunInstaller run, Func<bool> isInstalled, Func<string> tempFolder)
    {
        _download = download;
        _verify = verify;
        _run = run;
        _isInstalled = isInstalled;
        _tempFolder = tempFolder;
    }

    /// <summary>The real thing: Microsoft's download, the Windows signature check, the real installer
    /// process, and the runtime probe the sign-in flow already uses.</summary>
    public static WebViewRuntimeInstaller CreateDefault() => new(
        DownloadAsync, AuthenticodeSignature.Check, RunProcessAsync, WebViewAvailability.IsInstalled,
        () => Path.Combine(Path.GetTempPath(), "AI-Usage"));

    /// <param name="downloadProgress">0..1 while the file comes down.</param>
    /// <param name="installing">Called once the downloaded file is about to run: from there on the
    /// installer owns the machine and cancelling no longer applies.</param>
    public async Task<WebViewInstallResult> InstallAsync(
        IProgress<double>? downloadProgress, Action? installing, CancellationToken ct)
    {
        string? file = null;
        FileStream? guard = null;
        try
        {
            var folder = _tempFolder();
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, $"MicrosoftEdgeWebview2Setup-{Guid.NewGuid():N}.exe");

            try
            {
                await _download(BootstrapperUrl, file, downloadProgress, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Only the caller's own cancel; the client's timeout falls into DownloadFailed below.
                return new WebViewInstallResult(WebViewInstallOutcome.Cancelled);
            }
            catch (Exception ex)
            {
                return new WebViewInstallResult(WebViewInstallOutcome.DownloadFailed, Services.ErrorPresenter.TechnicalLine("download", ex));
            }

            if (ct.IsCancellationRequested)
                return new WebViewInstallResult(WebViewInstallOutcome.Cancelled);

            // Held open for reading from here to the end of the run: nobody can change or replace the
            // file between the signature check and the start of the installer, which asks for
            // elevation - the checked file is the file that runs.
            guard = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);

            SignatureCheckResult signature;
            try
            {
                // Off the UI thread: the revocation lookup may wait on the network.
                signature = await Task.Run(() => _verify(file), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new WebViewInstallResult(WebViewInstallOutcome.Cancelled);
            }
            catch (Exception ex)
            {
                signature = new SignatureCheckResult(false, Services.ErrorPresenter.TechnicalLine("signature", ex));
            }

            // The one gate before anything runs: not proven to be Microsoft's, not executed.
            if (!signature.IsValid)
                return new WebViewInstallResult(WebViewInstallOutcome.SignatureInvalid, signature.Detail);

            installing?.Invoke();
            int exitCode;
            try
            {
                exitCode = await _run(file, InstallerArguments, ct);
            }
            catch (OperationCanceledException)
            {
                return new WebViewInstallResult(WebViewInstallOutcome.Cancelled);
            }
            catch (Exception ex)
            {
                return new WebViewInstallResult(WebViewInstallOutcome.InstallerFailed, Services.ErrorPresenter.TechnicalLine("install", ex));
            }

            // The runtime decides, not the exit code: a bootstrapper that reports an error while the
            // runtime is in fact there (already installed by a parallel update) is a success.
            if (await Task.Run(_isInstalled))
                return new WebViewInstallResult(WebViewInstallOutcome.Installed);

            return exitCode != 0
                ? new WebViewInstallResult(WebViewInstallOutcome.InstallerFailed, $"install: exit code {exitCode}")
                : new WebViewInstallResult(WebViewInstallOutcome.StillMissing, "install: runtime not found afterwards");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temp folder itself was not usable.
            return new WebViewInstallResult(WebViewInstallOutcome.DownloadFailed, Services.ErrorPresenter.TechnicalLine("prepare", ex));
        }
        finally
        {
            guard?.Dispose();
            if (file is not null)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left behind in the temp folder; the next run uses another name.
                }
            }
        }
    }

    /// <summary>Whether a download step may talk to this address: https and one of the two Microsoft
    /// hosts above, nothing else.</summary>
    internal static bool IsAllowedDownloadUrl(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Plain GET with the redirects followed by hand, so every hop is checked against
    /// <see cref="IsAllowedDownloadUrl"/> before a request goes out; the body is capped.</summary>
    internal static async Task DownloadAsync(string url, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

        var current = new Uri(url);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsAllowedDownloadUrl(current))
                throw new InvalidOperationException("download address is not on the allow-list");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total > MaxDownloadBytes)
                throw new InvalidOperationException("download is larger than the bootstrapper can be");

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                received += read;
                if (received > MaxDownloadBytes)
                    throw new InvalidOperationException("download is larger than the bootstrapper can be");
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                if (total is > 0)
                    progress?.Report(Math.Min(1.0, (double)received / total.Value));
            }

            progress?.Report(1.0);
            return;
        }

        throw new HttpRequestException("too many redirects");
    }

    private static async Task<int> RunProcessAsync(string path, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path, arguments) { UseShellExecute = false, CreateNoWindow = true },
        };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InstallTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own limit, not the user's cancel: a stuck installer is a failure to report.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            throw new TimeoutException("the installer did not finish in time");
        }
        return process.ExitCode;
    }
}
