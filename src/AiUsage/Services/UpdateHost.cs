using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace AiUsage.Services;

/// <summary>
/// The real machine behind <see cref="UpdateInstaller"/>: the download, the swap of the running exe,
/// the silent setup and the exit. Downloads go through <see cref="AllowListedDownloader"/>; every
/// address, redirects included, has to be on <see cref="UpdateInstaller.AllowedHosts"/>.
/// </summary>
public sealed class UpdateHost(Action exitApplication) : IUpdateHost
{
    /// <summary>The restart switch of a portable update: the new copy waits for the old one to end
    /// before it takes the single-instance lock.</summary>
    public const string AfterUpdateSwitch = "--after-update";

    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    public bool IsInstalled => AppInfo.IsInstalled;

    public Architecture Architecture => RuntimeInformation.ProcessArchitecture;

    public string WorkFolder => DefaultWorkFolder;

    public string PublicKey => UpdatePublicKey.Value;

    public Version RunningVersion => Version.TryParse(AppInfo.Version, out var running) ? running : new Version(0, 0, 0);

    public Version? ReadFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0 && info.FilePrivatePart == 0
                ? null
                : new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    public string? ReadOriginalFilename(string path) => ReadVersionText(path, info => info.OriginalFilename);

    public string? ReadFileDescription(string path) => ReadVersionText(path, info => info.FileDescription);

    /// <summary>Version texts of a setup built by the installer tool come padded with blanks; blank
    /// only means none.</summary>
    internal static string? NormalizeVersionText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? ReadVersionText(string path, Func<FileVersionInfo, string?> pick)
    {
        try
        {
            return NormalizeVersionText(pick(FileVersionInfo.GetVersionInfo(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    public ushort? ReadPeMachine(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadPeMachine(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The machine field of a PE file: the DOS header's offset at 0x3C points at the PE
    /// signature ("PE" and two zero bytes), the machine value follows it. Null for anything that does not fit that layout.</summary>
    internal static ushort? ReadPeMachine(Stream stream)
    {
        Span<byte> dos = stackalloc byte[0x40];
        if (stream.Read(dos) < dos.Length || dos[0] != (byte)'M' || dos[1] != (byte)'Z')
            return null;

        var peOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
        if (peOffset < dos.Length || peOffset > stream.Length - 6)
            return null;

        stream.Position = peOffset;
        Span<byte> pe = stackalloc byte[6];
        if (stream.Read(pe) < pe.Length || pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0)
            return null;

        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]);
    }

    public static string DefaultWorkFolder => Path.Combine(Path.GetTempPath(), AppInfo.ProductName, "update");

    public Task<bool> DownloadAsync(string url, string destination, CancellationToken ct) =>
        DownloadAsync(url, destination, null, AllowListedDownloader.StallTimeout, AllowListedDownloader.OverallTimeout, ct);

    /// <summary>The handler and the limits are parameters so a test can stall the body. A failed
    /// download is <c>false</c>; only the caller's own cancellation propagates.</summary>
    internal static async Task<bool> DownloadAsync(
        string url, string destination, HttpMessageHandler? handler, TimeSpan stallLimit, TimeSpan overallLimit, CancellationToken ct)
    {
        try
        {
            await AllowListedDownloader.DownloadAsync(
                url, destination, uri => UpdateInstaller.IsAllowedUrl(uri.AbsoluteUri), handler, stallLimit, overallLimit, null, ct);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
            or UnauthorizedAccessException or UriFormatException or InvalidOperationException)
        {
            // The caller's own cancellation propagates; the time limit running out is a failed download.
            if (ct.IsCancellationRequested)
                throw;
            return false;
        }
    }

    public void StartSetupAndExit(string setupPath)
    {
        // The caller holds its lock on the file until this returns, so the process is created from the
        // verified file; a declined administrator prompt throws before anything is ended.
        Process.Start(BuildSetupStartInfo(setupPath, BuildSetupArguments(Environment.ProcessPath)))?.Dispose();

        exitApplication();
    }

    /// <summary>A per-machine setup is started elevated right away, from the verified file: left to
    /// start unelevated it would relaunch itself with administrator rights after this app ended and
    /// the file lock was gone.</summary>
    internal static ProcessStartInfo BuildSetupStartInfo(string setupPath, string arguments)
    {
        var start = new ProcessStartInfo(setupPath, arguments) { UseShellExecute = true };
        if (arguments.Contains("/ALLUSERS", StringComparison.OrdinalIgnoreCase))
            start.Verb = "runas";

        return start;
    }

    /// <summary>The setup's command line. The setup does not reuse the previous install scope on its
    /// own, so the scope of the running copy is passed along. The uninstall entries the setup wrote
    /// say which scope owns this folder (it works for a folder the user picked); only when neither
    /// names it does a copy under Program Files count as per-machine and any other as per-user.</summary>
    internal static string BuildSetupArguments(string? exePath) => BuildSetupArguments(exePath,
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    ], ReadInstallLocation);

    internal static string BuildSetupArguments(string? exePath, IEnumerable<string> programFilesRoots, Func<bool, string?>? readInstallLocation = null)
    {
        var directory = string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(ExpandShortPath(exePath));
        if (directory is not null && readInstallLocation is not null)
        {
            if (SameFolder(readInstallLocation(true), directory))
                return "/SILENT /ALLUSERS";
            if (SameFolder(readInstallLocation(false), directory))
                return "/SILENT /CURRENTUSER";
        }

        var perMachine = directory is not null && programFilesRoots
            .Where(root => !string.IsNullOrEmpty(root))
            .Any(root => (directory + Path.DirectorySeparatorChar).StartsWith(
                root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        return perMachine ? "/SILENT /ALLUSERS" : "/SILENT /CURRENTUSER";
    }

    private static bool SameFolder(string? recorded, string directory) =>
        !string.IsNullOrWhiteSpace(recorded)
        && string.Equals(recorded.Trim().TrimEnd(Path.DirectorySeparatorChar), directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{9FF5FEED-5C44-428A-97A9-028E7614B137}_is1";

    /// <summary>The folder the setup recorded for a per-machine (<paramref name="perMachine"/>) or
    /// per-user installation, or null when that scope has none.</summary>
    private static string? ReadInstallLocation(bool perMachine)
    {
        try
        {
            using var key = (perMachine ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser).OpenSubKey(UninstallKeyPath);
            return key?.GetValue("InstallLocation") as string ?? key?.GetValue("Inno Setup: App Path") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The long form of an 8.3 path (C:\PROGRA~1\...), so the prefix and registry
    /// comparisons see the same spelling the setup recorded. The input when it cannot be expanded.</summary>
    private static string ExpandShortPath(string path)
    {
        try
        {
            var buffer = new char[1024];
            var length = GetLongPathNameW(path, buffer, buffer.Length);
            return length > 0 && length < buffer.Length ? new string(buffer, 0, length) : path;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return path;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetLongPathNameW(string shortPath, char[] longPath, int bufferLength);

    public bool ReplaceRunningAndRestart(byte[] verifiedExe)
    {
        var running = Environment.ProcessPath ?? throw new IOException("The program file could not be replaced.");
        var swap = PortableSwap.Replace(running, verifiedExe);
        if (swap == SwapResult.RestoreNeeded)
            throw new UpdateRestoreNeededException(PortableSwap.OldPathFor(running));
        if (swap != SwapResult.Replaced)
            throw new IOException("The program file could not be replaced.");

        try
        {
            using var started = Process.Start(new ProcessStartInfo(running, $"{AfterUpdateSwitch} {Environment.ProcessId}") { UseShellExecute = false });
            if (started is null)
                return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The new program is already in place; this copy stays up and the user starts it by hand.
            return false;
        }

        exitApplication();
        return true;
    }

    /// <summary>True when the command line is the restart a portable update starts.</summary>
    public static bool IsAfterUpdateStart(string[] args) =>
        Array.Exists(args, a => string.Equals(a, AfterUpdateSwitch, StringComparison.OrdinalIgnoreCase));

    /// <summary>The restart after a portable update: the old copy still holds the single-instance
    /// lock for a moment, so the new one waits for it to end (bounded) before asking for the lock.</summary>
    public static void WaitForPreviousCopy(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, AfterUpdateSwitch, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid))
            return;

        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>Removes what finished or abandoned runs left in the work folder: stale files and the
    /// stale per-run subfolders. Each entry is handled on its own, so one that is still in use does
    /// not keep the others from going.</summary>
    public static void CleanUpStaleFiles() => CleanUpStaleFiles(DefaultWorkFolder, DateTime.UtcNow);

    internal static void CleanUpStaleFiles(string workFolder, DateTime nowUtc)
    {
        IEnumerable<string> entries;
        try
        {
            if (!Directory.Exists(workFolder))
                return;

            entries = Directory.EnumerateFileSystemEntries(workFolder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                var attributes = File.GetAttributes(entry);
                var isFolder = (attributes & FileAttributes.Directory) != 0;
                var written = isFolder ? Directory.GetLastWriteTimeUtc(entry) : File.GetLastWriteTimeUtc(entry);
                if (nowUtc - written <= StaleAfter)
                    continue;

                if (!isFolder)
                    File.Delete(entry);
                else if ((attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(entry, recursive: false); // removes the link only, never what it points at
                else
                    Directory.Delete(entry, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: whatever is still in use is picked up at the next start.
            }
        }
    }
}
