using System.Runtime.InteropServices;

namespace AiUsage.Services;

public enum UpdateOutcome
{
    /// <summary>The verified update was handed over; the running copy is about to end.</summary>
    Started,

    /// <summary>The signature was missing or did not check out, or the file carries no readable version; nothing was run.</summary>
    NotVerified,

    /// <summary>The file is genuine but not newer than the running version; nothing was run.</summary>
    NotNewer,

    /// <summary>The release carries no file for this copy (installed or portable, this processor).</summary>
    NoMatchingFile,

    /// <summary>A file could not be loaded.</summary>
    DownloadFailed,
}

/// <summary>Everything the installer needs from the machine, so the whole flow runs in a test
/// without a network, a second process or a real exit.</summary>
public interface IUpdateHost
{
    bool IsInstalled { get; }

    Architecture Architecture { get; }

    /// <summary>The folder the files are loaded into.</summary>
    string WorkFolder { get; }

    string PublicKey { get; }

    /// <summary>The version of the running app.</summary>
    Version RunningVersion { get; }

    /// <summary>The file version a downloaded program carries in its version resource; null when it has none.</summary>
    Version? ReadFileVersion(string path);

    /// <summary>Loads <paramref name="url"/> into <paramref name="destination"/>; false on any failure.</summary>
    Task<bool> DownloadAsync(string url, string destination, CancellationToken ct);

    /// <summary>Runs the verified setup silently and ends the running copy. The installer holds the file
    /// open without write or delete sharing for the whole call, so what starts is what was verified.</summary>
    void StartSetupAndExit(string setupPath);

    /// <summary>Puts the verified exe bytes in place of the running exe and starts it, ending the running copy.</summary>
    void ReplaceRunningAndRestart(byte[] verifiedExe);
}

/// <summary>
/// Downloads the release file that fits this copy together with its detached signature and runs it
/// only when the signature verifies. A failed check deletes both files and runs nothing.
/// </summary>
public sealed class UpdateInstaller(IUpdateHost host, Action<string>? log = null)
{
    /// <summary>The only hosts a release file or its signature may come from. A release asset URL on
    /// GitHub answers with a redirect to the content host, so each hop is checked against this list
    /// as well (see <see cref="UpdateHost"/>).</summary>
    public static readonly IReadOnlyList<string> AllowedHosts =
    [
        "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    ];

    private const string PortableX64Name = "AI-Usage.exe";
    private const string PortableArm64Name = "AI-Usage-arm64.exe";
    private const string SetupPrefix = "Setup-AI-Usage-";

    public static bool IsAllowedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && AllowedHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase));

    /// <summary>The release file for this copy: the setup for an installed copy, the exe of its own
    /// processor for a portable one. Null when the release has none.</summary>
    public static UpdateCheck.ReleaseAsset? PickAsset(
        IReadOnlyList<UpdateCheck.ReleaseAsset> assets, bool installed, Architecture architecture)
    {
        if (installed)
        {
            return assets.FirstOrDefault(a =>
                a.Name.StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }

        var portableName = architecture == Architecture.Arm64 ? PortableArm64Name : PortableX64Name;
        return assets.FirstOrDefault(a => string.Equals(a.Name, portableName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<UpdateOutcome> InstallAsync(UpdateCheck.Release release, CancellationToken ct)
    {
        var assets = release.Assets ?? [];
        var asset = PickAsset(assets, host.IsInstalled, host.Architecture);
        if (asset is null)
            return UpdateOutcome.NoMatchingFile;

        var signatureAsset = assets.FirstOrDefault(a => string.Equals(a.Name, asset.Name + ".sig", StringComparison.OrdinalIgnoreCase));
        if (!IsAllowedUrl(asset.DownloadUrl))
            return UpdateOutcome.DownloadFailed;

        // A release without a signature file is not one this program may run; nothing is loaded.
        if (signatureAsset is null || !IsAllowedUrl(signatureAsset.DownloadUrl))
            return UpdateOutcome.NotVerified;

        var filePath = Path.Combine(host.WorkFolder, Path.GetFileName(asset.Name));
        var signaturePath = filePath + ".sig";
        FileStream? lease = null;
        byte[] verifiedBytes;
        try
        {
            Directory.CreateDirectory(host.WorkFolder);
            if (!await host.DownloadAsync(asset.DownloadUrl, filePath, ct)
                || !await host.DownloadAsync(signatureAsset.DownloadUrl, signaturePath, ct))
            {
                DeleteQuietly(filePath, signaturePath);
                return UpdateOutcome.DownloadFailed;
            }

            // The handle denies writing and deleting from here on, and the bytes are read once from it:
            // only those bytes are verified and only they (or this locked file) are ever run.
            lease = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            verifiedBytes = new byte[lease.Length];
            await lease.ReadExactlyAsync(verifiedBytes, ct);
            // The signature covers the file bytes only, so the version is read from the verified file
            // itself: an older, genuinely signed build must not pass for an update.
            if (!UpdateSignature.Verify(verifiedBytes, await File.ReadAllTextAsync(signaturePath, ct), host.PublicKey))
            {
                lease.Dispose();
                DeleteQuietly(filePath, signaturePath);
                return UpdateOutcome.NotVerified;
            }

            var offered = host.ReadFileVersion(filePath);
            if (!IsNewerThanRunning(offered, host.RunningVersion))
            {
                lease.Dispose();
                DeleteQuietly(filePath, signaturePath);
                if (offered is null)
                    return UpdateOutcome.NotVerified;

                log?.Invoke($"Update refused: the downloaded file is version {offered}, the running version is {host.RunningVersion}.");
                return UpdateOutcome.NotNewer;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lease?.Dispose();
            DeleteQuietly(filePath, signaturePath);
            return UpdateOutcome.DownloadFailed;
        }
        catch (OperationCanceledException)
        {
            lease?.Dispose();
            DeleteQuietly(filePath, signaturePath);
            throw;
        }

        // The signature file has done its job.
        DeleteQuietly(signaturePath);
        var held = lease!;
        try
        {
            if (host.IsInstalled)
            {
                host.StartSetupAndExit(filePath);
            }
            else
            {
                // The swap writes the verified bytes itself; the downloaded file is not needed any more.
                held.Dispose();
                DeleteQuietly(filePath);
                host.ReplaceRunningAndRestart(verifiedBytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            held.Dispose();
            DeleteQuietly(filePath);
            return UpdateOutcome.DownloadFailed;
        }

        held.Dispose();
        return UpdateOutcome.Started;
    }

    private static bool IsNewerThanRunning(Version? offered, Version running) =>
        offered is not null && Normalize(offered) > Normalize(running);

    // A three-part version has no revision; .NET orders that below revision 0.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static void DeleteQuietly(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file another process still holds goes with the next start's cleanup of the work folder.
            }
        }
    }
}

/// <summary>The file moves of a portable update, kept apart from the process handling so they run in a test.</summary>
public static class PortableSwap
{
    private const string OldSuffix = ".old";

    /// <summary>Renames the running exe aside and writes the verified bytes at its path. A running exe
    /// can be renamed but not deleted; the leftover goes at the next start. False, with everything back
    /// as it was, when the new file could not be put in place.</summary>
    public static bool Replace(string runningExePath, byte[] newExe)
    {
        var oldPath = runningExePath + OldSuffix;
        try
        {
            if (File.Exists(oldPath))
                File.Delete(oldPath);
            File.Move(runningExePath, oldPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            File.WriteAllBytes(runningExePath, newExe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Move(oldPath, runningExePath, overwrite: true);
            }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
            {
                // Nothing more can be done from here; the .old file still holds the previous program.
            }

            return false;
        }
    }

    /// <summary>Deletes the previous program a portable update left beside the exe.</summary>
    public static void DeleteLeftover(string runningExePath)
    {
        try
        {
            File.Delete(runningExePath + OldSuffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use or read-only: tried again at the next start.
        }
    }
}
