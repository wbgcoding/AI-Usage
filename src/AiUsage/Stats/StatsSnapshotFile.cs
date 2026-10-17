using System.Text.Json;
using AiUsage.Io;
using AiUsage.Services;

namespace AiUsage.Stats;

/// <summary>
/// The last prepared statistics data (see <see cref="StatsDataSnapshot"/>) kept in one file in the
/// machine-local cache folder (it lists every session and project, so it must not roam with the
/// profile), so a window opened right after the next start has numbers to show at once, before
/// the first walk over the session logs has finished. It is a cache, not a setting: it holds
/// nothing the token index does not already hold, a version or a read that does not fit simply
/// removes it, and the window checks the index itself right after showing it.
/// </summary>
internal static class StatsSnapshotFile
{
    internal const string FileName = "stats-snapshot.json";

    /// <summary>Bumped whenever the shape of a record changes; a file with another number is dropped.</summary>
    internal const int CurrentVersion = 1;

    private sealed record Envelope(int Version, List<StatsRecord>? Records, List<StatsSessionRecord>? Sessions);

    internal static string PathIn(string directory) => Path.Combine(directory, FileName);

    /// <summary>The file's place in the machine-local cache folder.</summary>
    internal static string DefaultPath => PathIn(StartupProfile.CacheDirectory);

    /// <summary>Removes the copy an earlier version kept in the roaming data folder. Best effort,
    /// never throws.</summary>
    internal static void DeleteLegacy(string dataDirectory)
    {
        try
        {
            Delete(PathIn(dataDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unusable data folder name: nothing to clean up.
        }
    }

    /// <summary>Writes the snapshot atomically. A failure is returned as false, never thrown: this is a cache.</summary>
    internal static bool Save(string path, StatsDataSnapshot snapshot)
    {
        try
        {
            var envelope = new Envelope(CurrentVersion, [.. snapshot.Records], [.. snapshot.Sessions]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(envelope));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads the snapshot back. Null when there is no file; a file with an unknown version or
    /// one that cannot be read is deleted and also answers null.</summary>
    internal static StatsDataSnapshot? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(path));
            if (envelope is { Version: CurrentVersion, Records: { } records, Sessions: { } sessions })
                return StatsDataSnapshot.Prepared(records, sessions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Falls through to the removal below.
        }

        Delete(path);
        return null;
    }

    /// <summary>Removes the file, quietly: a restored backup brings another index, which the kept
    /// numbers no longer match.</summary>
    internal static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in place; the next read fails the same way and tries again.
        }
    }
}
