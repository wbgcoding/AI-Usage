using AiUsage.Services;
using AiUsage.Storage;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Sweeps leftover snapshot copies of Antigravity's state database that older versions made, and
/// shortens plan-tier wording for every producer of a plan name.
/// </summary>
public static class AntigravityStateReader
{
    private const string SnapshotPrefix = "antigravity-";
    private const string SnapshotExtension = ".vscdb";

    /// <summary>Where snapshot copies were made: machine-local temp space of this app, never the
    /// roaming data folder.</summary>
    internal static string SnapshotDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.ProductName, "tmp");

    private static readonly TimeSpan LeftoverMinAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Removes snapshots an earlier run could not delete. Safe to call at any time: a snapshot younger
    /// than an hour may belong to a read in progress and is left alone, and one still in use simply
    /// refuses to be deleted.
    /// </summary>
    public static void CleanUpLeftoverSnapshots()
    {
        CleanUpLeftoverSnapshots(SnapshotDirectory);
        CleanUpLegacyCache(Path.Combine(AppPaths.DataDirectory, "cache"));
    }

    /// <summary>Exposed so a test can plant a leftover in a folder of its own.</summary>
    internal static void CleanUpLeftoverSnapshots(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            // The trailing wildcard also catches the "-wal"/"-shm" side files SQLite may leave.
            foreach (var path in Directory.EnumerateFiles(directory, $"{SnapshotPrefix}*{SnapshotExtension}*"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= LeftoverMinAge)
                    DeleteQuietly(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Snapshots were once made in a "cache" folder of the roaming data folder. Copies left
    /// there would stay for good, so the same sweep runs on it once per start and the folder is
    /// removed when nothing else is in it.</summary>
    internal static void CleanUpLegacyCache(string directory)
    {
        CleanUpLeftoverSnapshots(directory);
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Shortens whatever long plan-tier wording a producer's own source hands back to the
    /// one short form the rest of the program ever shows on a tile. Called from both places that
    /// produce a plan name (this reader and <see cref="LocalLogin.AntigravityLocalLogin"/>), so the
    /// long form never reaches anything downstream. A name that names none of the known tiers passes
    /// through unchanged, so an unrecognised plan is never hidden rather than mislabeled.</summary>
    public static string? ShortenPlanName(string? planName)
    {
        if (string.IsNullOrEmpty(planName))
            return planName;
        if (planName.Contains("ultra", StringComparison.OrdinalIgnoreCase))
            return "Ultra";
        if (planName.Contains("pro", StringComparison.OrdinalIgnoreCase))
            return "Pro";
        if (planName.Contains("free", StringComparison.OrdinalIgnoreCase))
            return "Free";
        return planName;
    }
}
