using System.Text;
using AiUsage.Services;
using AiUsage.Storage;
using Microsoft.Data.Sqlite;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Reads the plan tier out of Antigravity IDE's local state database: a
/// SQLite database, table ItemTable, key "antigravityUnifiedStateSync.userStatus", value
/// Base64-over-Protobuf. No protobuf schema is needed - the plan tier is a plain ASCII string
/// inside the decoded bytes, found by a simple search. Never
/// throws; any failure (missing file, locked file, missing key, unreadable value) yields null -
/// this is a nice-to-have label, never a reason to fail the tile.
/// </summary>
public static class AntigravityStateReader
{
    private const string StateKey = "antigravityUnifiedStateSync.userStatus";
    private const string SnapshotPrefix = "antigravity-";
    private const string SnapshotExtension = ".vscdb";

    // The only plan tiers ever measured on this account. A future tier
    // simply won't be recognised yet - that is a missing label, not a wrong one.
    private static readonly string[] KnownPlanMarkers = ["Google AI Ultra", "Google AI Pro", "Google AI Standard"];

    private static int _leftoversCleaned;

    public static string? TryReadPlanTier(string databasePath)
    {
        if (!File.Exists(databasePath))
            return null;

        try
        {
            // A normal read-only open goes through SQLite's real locking and WAL-reading protocol, so
            // it sees a write the IDE committed but has not yet checkpointed back into the main file -
            // the immutable open below is blind to exactly that, since it pins a snapshot of the main
            // file at open time and never looks at the WAL at all. The short busy timeout only absorbs
            // the instant the IDE itself holds the write lock; it never turns into a real wait for a
            // file that is genuinely gone or unreadable (see the exception filters below).
            return ReadPlanTier(ReadOnlySharedConnectionString(databasePath), busyTimeoutMs: 200);
        }
        catch (SqliteException)
        {
            return TryReadPlanTierImmutable(databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryReadPlanTierImmutable(string databasePath)
    {
        try
        {
            // An immutable open skips SQLite's locking protocol entirely, so the IDE having the file
            // open is no longer a reason to duplicate a foreign application's database at all.
            return ReadPlanTier(ImmutableConnectionString(databasePath));
        }
        catch (SqliteException)
        {
            return ReadPlanTierFromSnapshot(databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Where a snapshot of the locked database is made: machine-local temp space of this
    /// app, never the roaming data folder, so a crash can only leave a stray file on this machine.</summary>
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

    /// <summary>Older versions made their snapshots in a "cache" folder of the roaming data folder.
    /// Copies a crash left there would stay for good, so the same sweep runs on it once per start
    /// and the folder is removed when nothing else is in it.</summary>
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

    /// <summary>
    /// SQLite's URI form is the only way to ask for an immutable open. Microsoft.Data.Sqlite hands a
    /// "file:" data source straight to SQLite, which unescapes it itself - so the three characters
    /// that would otherwise change what the URI means are escaped here.
    /// </summary>
    internal static string ImmutableConnectionString(string databasePath)
    {
        var uriPath = Path.GetFullPath(databasePath)
            .Replace("\\", "/", StringComparison.Ordinal)
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("?", "%3f", StringComparison.Ordinal)
            .Replace("#", "%23", StringComparison.Ordinal);

        return ConnectionString($"file:{uriPath}?immutable=1");
    }

    /// <summary>A plain read-only open with a shared cache - unlike <see cref="ImmutableConnectionString"/>,
    /// this goes through SQLite's normal locking and reads the WAL like any other connection would.
    /// Not pooled, so no handle on Antigravity's own file outlives the read.</summary>
    internal static string ReadOnlySharedConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();

    private static string? ReadPlanTierFromSnapshot(string databasePath) =>
        ReadPlanTierFromSnapshot(databasePath, SnapshotDirectory);

    /// <summary>Copies the locked database into <paramref name="snapshotDirectory"/>, reads the copy
    /// read-only and deletes it again. Exposed so a test can use a folder of its own.</summary>
    internal static string? ReadPlanTierFromSnapshot(string databasePath, string snapshotDirectory)
    {
        // Anything left behind by an earlier run goes before another copy is made, so the folder can
        // never grow past what this run itself is holding.
        if (Interlocked.Exchange(ref _leftoversCleaned, 1) == 0)
            CleanUpLeftoverSnapshots(snapshotDirectory);

        var snapshot = Path.Combine(
            snapshotDirectory, $"{SnapshotPrefix}{Guid.NewGuid():N}{SnapshotExtension}");
        try
        {
            Directory.CreateDirectory(snapshotDirectory);
            CopySnapshot(databasePath, snapshot);
            return ReadPlanTier(ConnectionString(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return null;
        }
        finally
        {
            // A WAL-mode source database can make SQLite create "-wal"/"-shm" side files next to the
            // snapshot even for a read-only open - deleting only the main file would otherwise leave
            // those behind, one small leak per locked-file fallback.
            foreach (var path in new[] { snapshot, snapshot + "-wal", snapshot + "-shm" })
                DeleteQuietly(path);
        }
    }

    /// <summary>Copies the database. A plain copy keeps the source's write time, so a database the IDE
    /// last wrote hours ago would produce a snapshot that already looks old enough for the leftover
    /// sweep (of a second running instance) to delete under the reader.</summary>
    internal static void CopySnapshot(string databasePath, string snapshot)
    {
        File.Copy(databasePath, snapshot, overwrite: true);
        File.SetLastWriteTimeUtc(snapshot, DateTime.UtcNow);
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

    // No pooling: a pooled connection keeps its file open after Dispose, and an open file cannot be
    // deleted, which would leave every snapshot behind.
    private static string ConnectionString(string dataSource) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

    private static string? ReadPlanTier(string connectionString, int? busyTimeoutMs = null)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        if (busyTimeoutMs is { } timeoutMs)
        {
            using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout = {timeoutMs};";
            pragma.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1";
        command.Parameters.AddWithValue("$key", StateKey);

        if (command.ExecuteScalar() is not string raw || raw.Length == 0)
            return null;

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(decoded);
        return ShortenPlanName(KnownPlanMarkers.FirstOrDefault(marker => text.Contains(marker, StringComparison.Ordinal)));
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
