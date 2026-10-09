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

    /// <summary>The verified update replaced the program file, but the new copy could not be started;
    /// the user starts it by hand.</summary>
    InstalledRestartNeeded,

    /// <summary>The swap of the program file failed and the previous program could not be put back:
    /// it sits beside the exe under the <c>.old</c> name and the user has to rename it back.</summary>
    SwapFailedRestoreNeeded,

    /// <summary>The setup was verified but did not start (for example the administrator prompt was
    /// declined); nothing was installed and the running copy stays up.</summary>
    NotStarted,
}

/// <summary>Everything the installer needs from the machine, so the whole flow runs in a test
/// without a network, a second process or a real exit.</summary>
public interface IUpdateHost
{
    bool IsInstalled { get; }

    Architecture Architecture { get; }

    /// <summary>The folder the per-run download folders are created in. Never a run's own folder: the
    /// installer makes a new, empty subfolder here for every run.</summary>
    string WorkFolder { get; }

    string PublicKey { get; }

    /// <summary>The version of the running app.</summary>
    Version RunningVersion { get; }

    /// <summary>The file version a downloaded program carries in its version resource; null when it has none.</summary>
    Version? ReadFileVersion(string path);

    /// <summary>The original file name in a program's version resource; null when it has none.</summary>
    string? ReadOriginalFilename(string path);

    /// <summary>The file description in a program's version resource; null when it has none.</summary>
    string? ReadFileDescription(string path);

    /// <summary>The processor a program is built for, from its PE header (<c>IMAGE_FILE_HEADER.Machine</c>);
    /// null when the file is not a readable PE file.</summary>
    ushort? ReadPeMachine(string path);

    /// <summary>Loads <paramref name="url"/> into <paramref name="destination"/>; false on any failure.</summary>
    Task<bool> DownloadAsync(string url, string destination, CancellationToken ct);

    /// <summary>Runs the verified setup silently and ends the running copy. The installer holds the file
    /// open without write or delete sharing for the whole call, so what starts is what was verified.</summary>
    void StartSetupAndExit(string setupPath);

    /// <summary>Puts the verified exe bytes in place of the running exe and starts it, ending the running
    /// copy. Throws <see cref="IOException"/> when the exe could not be replaced (the previous program
    /// is then still in place) and <see cref="UpdateRestoreNeededException"/> when it could not be put
    /// back either. Returns false when the exe was replaced but the new copy could not be
    /// started: the running copy then stays alive.</summary>
    bool ReplaceRunningAndRestart(byte[] verifiedExe);
}

/// <summary>The swap of a portable update failed after the running exe was moved aside, and moving it
/// back failed too: the previous program is only at <see cref="OldPath"/>.</summary>
public sealed class UpdateRestoreNeededException(string oldPath)
    : IOException("The previous program file could not be put back; it is at " + oldPath + ".")
{
    public string OldPath { get; } = oldPath;
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
    private const string SetupFileDescription = "AI-Usage Setup";
    private const string PortableOriginalFilename = "AI-Usage.dll";
    private const ushort MachineX64 = 0x8664;
    private const ushort MachineArm64 = 0xAA64;

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
                && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && IsPlainFileName(a.Name));
        }

        var portableName = architecture == Architecture.Arm64 ? PortableArm64Name : PortableX64Name;
        return assets.FirstOrDefault(a => string.Equals(a.Name, portableName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An asset name becomes a path on disk, so only a bare file name qualifies: no folder
    /// parts, no drive or stream separator, no parent-folder step.</summary>
    private static bool IsPlainFileName(string name) =>
        name.Length > 0 && Path.GetFileName(name) == name && !name.Contains(':') && !name.Contains("..");

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

        // Every run downloads into a folder nobody else can have prepared: new name, still empty.
        var runFolder = TryCreateRunFolder();
        if (runFolder is null)
            return UpdateOutcome.DownloadFailed;

        var filePath = Path.Combine(runFolder, Path.GetFileName(asset.Name));
        var signaturePath = filePath + ".sig";
        FileStream? lease = null;
        byte[] verifiedBytes;
        try
        {
            if (!await host.DownloadAsync(asset.DownloadUrl, filePath, ct)
                || !await host.DownloadAsync(signatureAsset.DownloadUrl, signaturePath, ct))
            {
                CleanUpRun(runFolder, filePath, signaturePath);
                return UpdateOutcome.DownloadFailed;
            }

            if (IsLink(filePath))
            {
                log?.Invoke("Update refused: the downloaded file is a link.");
                CleanUpRun(runFolder, filePath, signaturePath);
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
                CleanUpRun(runFolder, filePath, signaturePath);
                return UpdateOutcome.NotVerified;
            }

            var offered = host.ReadFileVersion(filePath);
            if (!IsNewerThanRunning(offered, host.RunningVersion))
            {
                lease.Dispose();
                CleanUpRun(runFolder, filePath, signaturePath);
                if (offered is null)
                    return UpdateOutcome.NotVerified;

                log?.Invoke($"Update refused: the downloaded file is version {offered}, the running version is {host.RunningVersion}.");
                return UpdateOutcome.NotNewer;
            }

            // A genuinely signed file can still be the wrong kind (a setup for a portable copy, the other
            // processor's build): it is read from the same held file whose bytes were just verified.
            if (!MatchesRole(filePath))
            {
                lease.Dispose();
                CleanUpRun(runFolder, filePath, signaturePath);
                log?.Invoke("Update refused: the downloaded file is not the kind this copy installs.");
                return UpdateOutcome.DownloadFailed;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lease?.Dispose();
            CleanUpRun(runFolder, filePath, signaturePath);
            return UpdateOutcome.DownloadFailed;
        }
        catch (OperationCanceledException)
        {
            lease?.Dispose();
            CleanUpRun(runFolder, filePath, signaturePath);
            throw;
        }

        // The signature file has done its job.
        DeleteQuietly(signaturePath);
        var held = lease!;
        try
        {
            if (host.IsInstalled)
            {
                // The setup runs with elevated rights from this folder, and a program loads libraries
                // from its own folder: anything beside the verified file means the folder was tampered with.
                if (!HoldsOnly(runFolder, filePath))
                {
                    log?.Invoke("Update refused: the download folder holds more than the downloaded file.");
                    held.Dispose();
                    CleanUpRun(runFolder, filePath);
                    return UpdateOutcome.DownloadFailed;
                }

                host.StartSetupAndExit(filePath);
            }
            else
            {
                // The swap writes the verified bytes itself; the downloaded file is not needed any more.
                held.Dispose();
                CleanUpRun(runFolder, filePath);
                if (!host.ReplaceRunningAndRestart(verifiedBytes))
                    return UpdateOutcome.InstalledRestartNeeded;
            }
        }
        catch (UpdateRestoreNeededException ex)
        {
            held.Dispose();
            CleanUpRun(runFolder, filePath);
            log?.Invoke($"Update swap failed and the previous program could not be put back: it is at {ex.OldPath}.");
            return UpdateOutcome.SwapFailedRestoreNeeded;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // The setup itself did not start (a declined administrator prompt is code 1223): not a failed download.
            held.Dispose();
            CleanUpRun(runFolder, filePath);
            log?.Invoke($"Update setup did not start (error code {ex.NativeErrorCode}).");
            return UpdateOutcome.NotStarted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            held.Dispose();
            CleanUpRun(runFolder, filePath);
            return UpdateOutcome.DownloadFailed;
        }

        held.Dispose();
        return UpdateOutcome.Started;
    }

    /// <summary>An installed copy takes only a setup; a portable copy only the portable program built for
    /// its own processor.</summary>
    private bool MatchesRole(string path)
    {
        if (host.IsInstalled)
            return string.Equals(host.ReadFileDescription(path), SetupFileDescription, StringComparison.Ordinal);

        ushort? expectedMachine = host.Architecture switch
        {
            Architecture.X64 => MachineX64,
            Architecture.Arm64 => MachineArm64,
            _ => null,
        };
        return expectedMachine is not null
            && string.Equals(host.ReadOriginalFilename(path), PortableOriginalFilename, StringComparison.Ordinal)
            && host.ReadPeMachine(path) == expectedMachine;
    }

    /// <summary>A new, empty folder under the work folder for this run, or null (with one log line)
    /// when the work folder cannot be trusted: a link in its place, a name that already exists, or
    /// something already inside the new folder.</summary>
    private string? TryCreateRunFolder()
    {
        try
        {
            var root = host.WorkFolder;
            Directory.CreateDirectory(root);
            if (IsLink(root) || (Path.GetDirectoryName(root) is { Length: > 0 } parent && IsLink(parent)))
            {
                log?.Invoke("Update refused: the download folder is a link.");
                return null;
            }

            var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            if (Directory.Exists(folder) || File.Exists(folder))
            {
                log?.Invoke("Update refused: the download folder already exists.");
                return null;
            }

            Directory.CreateDirectory(folder);
            if (IsLink(folder) || Directory.EnumerateFileSystemEntries(folder).Any())
            {
                log?.Invoke("Update refused: the download folder was not empty.");
                return null;
            }

            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>True when <paramref name="folder"/> contains exactly <paramref name="file"/>: no other
    /// file, no subfolder.</summary>
    private static bool HoldsOnly(string folder, string file)
    {
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(folder).Take(2).ToList();
            return entries.Count == 1 && string.Equals(entries[0], file, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes the run's files and, when nothing else is left, its folder.</summary>
    private static void CleanUpRun(string runFolder, params string[] files)
    {
        DeleteQuietly(files);
        try
        {
            Directory.Delete(runFolder, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not empty or still in use: the next start's cleanup of the work folder removes it.
        }
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

/// <summary>How a portable swap ended.</summary>
public enum SwapResult
{
    /// <summary>The new program is in place; the previous one sits beside it under the <c>.old</c> name.</summary>
    Replaced,

    /// <summary>Nothing changed: the running exe is still in place.</summary>
    Unchanged,

    /// <summary>The running exe was moved aside and could not be moved back: it is at the <c>.old</c> name.</summary>
    RestoreNeeded,
}

/// <summary>The file moves of a portable update, kept apart from the process handling so they run in a test.</summary>
public static class PortableSwap
{
    private const string OldSuffix = ".old";
    private const string StagedSuffix = ".new";

    /// <summary>Writes the verified bytes beside the running exe, checks the written copy against them,
    /// renames the running exe aside and the new copy into its place. A running exe can be renamed but
    /// not deleted; the leftover goes once the new copy has shown its window. <see cref="SwapResult.Unchanged"/>,
    /// with everything back as it was, when the new file could not be put in place - the running exe
    /// is only touched once a complete, identical copy of the new one exists.
    /// <see cref="SwapResult.RestoreNeeded"/> when even moving the previous program back failed.</summary>
    public static SwapResult Replace(string runningExePath, byte[] newExe) =>
        Replace(runningExePath, newExe, (from, to, overwrite) => File.Move(from, to, overwrite));

    /// <summary>The name the previous program is parked under during a swap.</summary>
    public static string OldPathFor(string runningExePath) => runningExePath + OldSuffix;

    /// <summary>The move is a parameter so a test can fail one of the renames.</summary>
    internal static SwapResult Replace(string runningExePath, byte[] newExe, Action<string, string, bool> move)
    {
        if (!File.Exists(runningExePath))
            return SwapResult.Unchanged;

        var oldPath = runningExePath + OldSuffix;
        var stagedPath = runningExePath + StagedSuffix;
        FileStream? staged = null;
        try
        {
            // Whatever sits at the staging name (a stale copy, a link) is removed, never written through.
            File.Delete(stagedPath);
            using (var writer = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writer.Write(newExe);
                writer.Flush(flushToDisk: true);
            }

            // Held until the rename is done: nobody can change the copy between the comparison and the
            // move (writing is denied), while this process can still rename it.
            staged = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!HasContent(staged, newExe))
            {
                DiscardStaged(ref staged, stagedPath);
                return SwapResult.Unchanged;
            }

            if (File.Exists(oldPath))
                File.Delete(oldPath);
            move(runningExePath, oldPath, false);
            try
            {
                move(stagedPath, runningExePath, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The previous program goes back where it was; if even that fails, only the .old file holds it.
                var restored = true;
                try
                {
                    move(oldPath, runningExePath, true);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    restored = false;
                }

                DiscardStaged(ref staged, stagedPath);
                return restored ? SwapResult.Unchanged : SwapResult.RestoreNeeded;
            }

            return SwapResult.Replaced;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiscardStaged(ref staged, stagedPath);
            return SwapResult.Unchanged;
        }
        finally
        {
            staged?.Dispose();
        }
    }

    private static bool HasContent(Stream stream, byte[] expected)
    {
        if (stream.Length != expected.Length)
            return false;

        var buffer = new byte[81920];
        var offset = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (!buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan(offset, read)))
                return false;
            offset += read;
        }

        return offset == expected.Length;
    }

    private static void DiscardStaged(ref FileStream? staged, string stagedPath)
    {
        staged?.Dispose();
        staged = null;
        try
        {
            File.Delete(stagedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover staging file is replaced by the next update.
        }
    }

    /// <summary>Deletes the previous program a portable update left beside the exe. Never while the
    /// exe itself is missing: the <c>.old</c> file is then the only copy of the program.</summary>
    public static void DeleteLeftover(string runningExePath)
    {
        try
        {
            if (!File.Exists(runningExePath))
                return;

            File.Delete(runningExePath + OldSuffix);
            File.Delete(runningExePath + StagedSuffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use or read-only: tried again at the next start.
        }
    }
}
