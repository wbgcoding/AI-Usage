using System.Collections.Concurrent;
using System.Globalization;
using AiUsage.Storage;
using Microsoft.Data.Sqlite;

namespace AiUsage.Stats;

/// <summary>
/// The token usage index's own store: <c>%APPDATA%\AI-Usage\stats.db</c>, read once by <see
/// cref="StatsIndexer"/> and by <see cref="StatsAggregator"/>'s caller, written incrementally as new
/// session lines are found. Own schema version, in its own table - a database written by a future
/// build with a higher version is left alone rather than risking a rewrite that silently drops
/// whatever that version added, the same contract <see cref="Storage.HistoryStore"/> already keeps
/// for its own file. Never throws: every operation degrades to "nothing indexed yet" rather than
/// crashing the window that shows it.
/// </summary>
public sealed class StatsStore
{
    // Schema v2 adds the `hour` column to the usage table's own primary key. Schema v3 changes what
    // a Claude record's `project` column holds (the real path instead of the sanitised folder name).
    // Schema v4 adds the `effort` column to the usage table's own primary key AND switches the
    // day/hour bucket itself from UTC to local time - the same reindex covers both, since a
    // local-time rebucket needs every row rebuilt anyway. Schema v5 adds source_file.last_message_key
    // so a Claude response written several times is counted once. A database opened below v4 has
    // its usage/source_file tables dropped and rebuilt (see EnsureSchema) since the older rows carry
    // nothing to migrate from; v4 migrates in place. Schema v6 gives schema_version a single-row
    // constraint (id = 1) so two first opens can never leave two version rows; every row stays.
    // Schema v7 adds the `machine` and `subagent` columns to the usage table's own primary key (a
    // table rebuild that keeps every row; Claude rows filed under the project "subagents" are marked
    // as subagent rows) and the session tables; the sessions of rows already stored are filled in
    // once from the source files that still exist (see StatsIndexer).
    internal const int SchemaVersion = 7;

    // Null for the production store, which follows AppPaths.DataDirectory on every open, so a moved
    // data folder takes the index along without rebuilding any store that already exists.
    private readonly string? _fixedDirectory;

    private string DatabasePath => DatabasePathIn(_fixedDirectory ?? AppPaths.DataDirectory);

    /// <summary>The data folder of a test store; null for the production store.</summary>
    internal string? FixedDirectory => _fixedDirectory;

    private static string DatabasePathIn(string dataDirectory) => Path.Combine(dataDirectory, "stats.db");
    private readonly int _busyTimeoutMs;

    public StatsStore()
    {
        _busyTimeoutMs = 2000;
    }

    /// <summary>Test seam: a temp directory instead of the real data folder, and optionally a
    /// shorter busy timeout than the production default of 2000 ms - see <see cref="EnsureSchema"/>
    /// - so a test that deliberately holds the write lock does not have to wait out the real one.</summary>
    internal StatsStore(string dataDirectory, int busyTimeoutMs = 2000)
    {
        _fixedDirectory = dataDirectory;
        _busyTimeoutMs = busyTimeoutMs;
    }

    /// <summary>Copies the index from one data folder into another through SQLite's own backup, which
    /// stays consistent even while the indexer is writing. Nothing to copy is not an error. Throws
    /// <see cref="SqliteException"/> or an IO exception when the copy cannot be made, so the caller
    /// can keep the old folder.</summary>
    internal static void CopyIndex(string sourceDirectory, string destinationDirectory)
    {
        var sourcePath = DatabasePathIn(sourceDirectory);
        if (!File.Exists(sourcePath))
            return;

        var destinationPath = DatabasePathIn(destinationDirectory);
        var sourceConnectionString = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString();
        using var source = new SqliteConnection(sourceConnectionString);
        using var destination = new SqliteConnection(destinationConnectionString);
        // The copy replaces whatever schema the destination file had.
        EnsuredDatabases.TryRemove(Path.GetFullPath(destinationPath), out _);
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    /// <summary>A consistent copy of the index file at <paramref name="sourcePath"/> through SQLite's own
    /// backup (never a raw file copy, which can catch a write half done), for a backup zip. The source
    /// is opened read-only and the destination file must not exist yet.</summary>
    internal static void SnapshotIndex(string sourcePath, string destinationPath)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var source = new SqliteConnection(sourceConnectionString);
        using var destination = new SqliteConnection(destinationConnectionString);
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    /// <summary>The schema version a database file names, read without changing the file; null when
    /// it is no SQLite file, fails SQLite's own quick check or has no version row.</summary>
    internal static int? TryReadSchemaVersion(string databasePath)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check(1)";
                if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            using var version = connection.CreateCommand();
            version.CommandText = "SELECT MAX(version) FROM schema_version";
            var value = version.ExecuteScalar();
            return value is null or DBNull ? null : (int)Math.Min(int.MaxValue, Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static readonly string[] UsageColumns =
        ["provider", "day", "hour", "model", "project", "effort", "machine", "subagent", "input_tokens", "output_tokens", "cache_creation_tokens", "cache_read_tokens"];

    private static readonly string[] SessionColumns =
        ["provider", "machine", "session_id", "project", "first_utc", "last_utc", "input", "output", "cache_creation", "cache_read", "main_model", "subagent_tokens"];

    private static readonly string[] SessionModelColumns = ["provider", "machine", "session_id", "model", "tokens"];

    /// <summary>True when the foreign file is a token index this build can read: a version this build
    /// knows (not newer) that already has the machine column, and the three tables with every column
    /// the import copies. Checked before a single row is read.</summary>
    private static bool HasImportableSchema(string databasePath)
    {
        if (TryReadSchemaVersion(databasePath) is not { } version || version < 7 || version > SchemaVersion)
            return false;

        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            return TableHasColumns(connection, "usage", UsageColumns)
                && TableHasColumns(connection, "session", SessionColumns)
                && TableHasColumns(connection, "session_model", SessionModelColumns);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static bool TableHasColumns(SqliteConnection connection, string table, string[] columns)
    {
        using (var type = connection.CreateCommand())
        {
            type.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            type.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt64(type.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                return false;
        }

        using var info = connection.CreateCommand();
        info.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = info.ExecuteReader();
        while (reader.Read())
            present.Add(reader.GetString(0));
        return columns.All(present.Contains);
    }

    private const string DayGlob = "[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]";

    // A foreign or restored file is data, not code: values outside what this app writes (a text or
    // negative number where a count belongs, an hour off the clock, a megabyte-long label) are never
    // taken over, so no later reader has to guard against them.
    private const string ValidUsageRow =
        "typeof(hour) = 'integer' AND hour BETWEEN 0 AND 23 AND typeof(subagent) = 'integer' "
        + "AND typeof(input_tokens) = 'integer' AND input_tokens >= 0 AND typeof(output_tokens) = 'integer' AND output_tokens >= 0 "
        + "AND typeof(cache_creation_tokens) = 'integer' AND cache_creation_tokens >= 0 AND typeof(cache_read_tokens) = 'integer' AND cache_read_tokens >= 0 "
        + "AND day GLOB '" + DayGlob + "' AND length(provider) <= 512 AND length(model) <= 512 AND length(project) <= 4096 "
        + "AND length(effort) <= 512 AND length(machine) <= 64";

    private const string ValidSessionRow =
        "typeof(input) = 'integer' AND input >= 0 AND typeof(output) = 'integer' AND output >= 0 "
        + "AND typeof(cache_creation) = 'integer' AND cache_creation >= 0 AND typeof(cache_read) = 'integer' AND cache_read >= 0 "
        + "AND typeof(subagent_tokens) = 'integer' AND subagent_tokens >= 0 AND length(provider) <= 512 AND length(session_id) <= 512 "
        + "AND length(project) <= 4096 AND length(main_model) <= 512 AND length(first_utc) <= 64 AND length(last_utc) <= 64 AND length(machine) <= 64";

    private const string ValidSessionModelRow =
        "typeof(tokens) = 'integer' AND tokens >= 0 AND length(provider) <= 512 AND length(session_id) <= 512 "
        + "AND length(model) <= 512 AND length(machine) <= 64";

    /// <summary>Canonical lower-case GUID text, as a SQLite GLOB pattern.</summary>
    private const string GuidGlob =
        "[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-"
        + "[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]";

    /// <summary>
    /// Brings another PC's token history in from <paramref name="foreignDatabasePath"/> (a backup's
    /// index, already extracted to a file of its own). Its own rows (machine column empty there)
    /// are filed here under <paramref name="foreignId"/>; rows it holds for third PCs (canonical GUID
    /// labels only) come along only for a PC this index has no rows for yet; rows carrying
    /// <paramref name="ownId"/> (its copy of this PC's history) are skipped. Whatever rows this index
    /// already holds for <paramref name="foreignId"/> are replaced first, in the same transaction, so
    /// importing the same PC again changes nothing. Rows of this PC (machine column empty here) are
    /// never touched, and rows that do not look like what this app writes are left out.
    /// Returns the number of distinct days now filed under the PC, or null when nothing was imported:
    /// an id that is not a GUID or is this PC's own (in any letter case), a foreign file of an unknown
    /// or newer schema, a damaged file, or this index being unusable.
    /// </summary>
    internal int? ImportMachine(string foreignDatabasePath, string foreignId, string ownId)
    {
        if (!BackupService.TryNormalizeMachineId(foreignId, out var id)
            || !BackupService.TryNormalizeMachineId(ownId, out var own)
            || id == own
            || !HasImportableSchema(foreignDatabasePath))
            return null;

        try
        {
            using var connection = Open();
            if (!IsUsable)
                return null;

            // The attached file is a foreign schema: nothing it declares (views, triggers, functions) may run.
            Execute(connection, "PRAGMA trusted_schema = OFF");
            using (var attach = connection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $path AS imported_source";
                attach.Parameters.AddWithValue("$path", foreignDatabasePath);
                attach.ExecuteNonQuery();
            }

            try
            {
                Execute(connection, "BEGIN IMMEDIATE");
                try
                {
                    var usage = string.Join(", ", UsageColumns);
                    var usageFromSource = string.Join(", ", UsageColumns.Select(c => c == "machine" ? "$id" : c));
                    var session = string.Join(", ", SessionColumns);
                    var sessionFromSource = string.Join(", ", SessionColumns.Select(c => c == "machine" ? "$id" : c));
                    var model = string.Join(", ", SessionModelColumns);
                    var modelFromSource = string.Join(", ", SessionModelColumns.Select(c => c == "machine" ? "$id" : c));
                    var third = $"machine GLOB '{GuidGlob}' AND machine <> $id AND machine <> $own AND machine NOT IN (SELECT machine FROM temp.known_machines)";

                    ExecuteWith(connection, $"""
                        DELETE FROM session_model WHERE machine = $id;
                        DELETE FROM session WHERE machine = $id;
                        DELETE FROM usage WHERE machine = $id;
                        CREATE TEMP TABLE known_machines AS SELECT machine FROM main.usage UNION SELECT machine FROM main.session UNION SELECT machine FROM main.session_model;
                        INSERT INTO usage ({usage}) SELECT {usageFromSource} FROM imported_source.usage WHERE machine = '' AND {ValidUsageRow};
                        INSERT INTO session ({session}) SELECT {sessionFromSource} FROM imported_source.session WHERE machine = '' AND {ValidSessionRow};
                        INSERT INTO session_model ({model}) SELECT {modelFromSource} FROM imported_source.session_model WHERE machine = '' AND {ValidSessionModelRow};
                        INSERT OR IGNORE INTO session ({session}) SELECT {session} FROM imported_source.session WHERE {third} AND {ValidSessionRow};
                        INSERT OR IGNORE INTO session_model ({model}) SELECT {model} FROM imported_source.session_model WHERE {third} AND {ValidSessionModelRow};
                        INSERT OR IGNORE INTO usage ({usage}) SELECT {usage} FROM imported_source.usage WHERE {third} AND {ValidUsageRow};
                        DROP TABLE temp.known_machines;
                        """, id, own);

                    int days;
                    using (var count = connection.CreateCommand())
                    {
                        count.CommandText = "SELECT COUNT(DISTINCT day) FROM usage WHERE machine = $id";
                        count.Parameters.AddWithValue("$id", id);
                        days = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
                    }

                    Execute(connection, "COMMIT");
                    return days;
                }
                catch
                {
                    TryExecute(connection, "ROLLBACK");
                    throw;
                }
            }
            finally
            {
                TryExecute(connection, "DETACH DATABASE imported_source");
            }
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>Every machine label other than this PC's own (the empty one) that has rows in the
    /// index, whoever put it there: imported PCs and whatever came along with them.</summary>
    internal IReadOnlyList<string> ListMachines()
    {
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return [];

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT machine FROM usage WHERE machine <> '' AND length(machine) <= 64
                UNION SELECT machine FROM session WHERE machine <> '' AND length(machine) <= 64
                UNION SELECT machine FROM session_model WHERE machine <> '' AND length(machine) <= 64
                ORDER BY 1 LIMIT 200
                """;
            var found = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                found.Add(reader.GetString(0));
            return found;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>Deletes every row filed under one other machine label (see <see cref="ListMachines"/>).
    /// Any non-empty label is accepted, exactly as listed; the empty label that marks this PC's own
    /// rows can never be named here. False when the label is not valid or the index could not be changed.</summary>
    internal bool RemoveMachine(string machineId)
    {
        if (string.IsNullOrEmpty(machineId) || machineId.Length > 64)
            return false;
        var id = machineId;

        try
        {
            using var connection = Open();
            if (!IsUsable)
                return false;

            Execute(connection, "BEGIN IMMEDIATE");
            try
            {
                ExecuteWith(connection, """
                    DELETE FROM session_model WHERE machine = $id;
                    DELETE FROM session WHERE machine = $id;
                    DELETE FROM usage WHERE machine = $id;
                    """, id, null);
                Execute(connection, "COMMIT");
                return true;
            }
            catch
            {
                TryExecute(connection, "ROLLBACK");
                throw;
            }
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets an index file taken from a backup ready to become this PC's: rows that do not look like what
    /// this app writes are dropped, and every row filed under one of <paramref name="machineIdsToOwn"/>
    /// (the backup PC's own id and this PC's id) becomes a row of this PC (machine column empty), counts
    /// added up where such a row exists already. Rows of every other imported PC keep their label.
    /// Works on the file directly, in one transaction; the live index is not involved.
    /// </summary>
    internal static void PrepareRestoredIndex(string databasePath, IReadOnlyCollection<string> machineIdsToOwn)
    {
        var ids = machineIdsToOwn.Select(i => i.ToLowerInvariant()).Distinct().ToList();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "PRAGMA trusted_schema = OFF");
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            using var command = connection.CreateCommand();
            var names = ids.Count == 0 ? "NULL" : string.Join(", ", ids.Select((_, i) => "$m" + i));
            for (var i = 0; i < ids.Count; i++)
                command.Parameters.AddWithValue("$m" + i, ids[i]);

            var usage = string.Join(", ", UsageColumns);
            var usageKeys = "provider, day, hour, model, project, effort, subagent";
            var usageKeysConflict = "provider, day, hour, model, project, effort, machine, subagent";
            var session = string.Join(", ", SessionColumns);
            var sessionAsOwn = string.Join(", ", SessionColumns.Select(c => c == "machine" ? "''" : c));
            var model = string.Join(", ", SessionModelColumns);
            var modelAsOwn = string.Join(", ", SessionModelColumns.Select(c => c == "machine" ? "''" : c));
            command.CommandText = $"""
                DELETE FROM usage WHERE NOT COALESCE(({ValidUsageRow}), 0);
                DELETE FROM session WHERE NOT COALESCE(({ValidSessionRow}), 0);
                DELETE FROM session_model WHERE NOT COALESCE(({ValidSessionModelRow}), 0);
                INSERT INTO usage ({usage})
                    SELECT provider, day, hour, model, project, effort, '', subagent,
                           SUM(input_tokens), SUM(output_tokens), SUM(cache_creation_tokens), SUM(cache_read_tokens)
                    FROM usage WHERE lower(machine) IN ({names}) GROUP BY {usageKeys}
                    ON CONFLICT ({usageKeysConflict}) DO UPDATE SET
                        input_tokens = input_tokens + excluded.input_tokens,
                        output_tokens = output_tokens + excluded.output_tokens,
                        cache_creation_tokens = cache_creation_tokens + excluded.cache_creation_tokens,
                        cache_read_tokens = cache_read_tokens + excluded.cache_read_tokens;
                DELETE FROM usage WHERE lower(machine) IN ({names});
                INSERT OR IGNORE INTO session ({session}) SELECT {sessionAsOwn} FROM session WHERE lower(machine) IN ({names});
                DELETE FROM session WHERE lower(machine) IN ({names});
                INSERT OR IGNORE INTO session_model ({model}) SELECT {modelAsOwn} FROM session_model WHERE lower(machine) IN ({names});
                DELETE FROM session_model WHERE lower(machine) IN ({names});
                """;
            command.ExecuteNonQuery();
            Execute(connection, "COMMIT");
        }
        catch
        {
            TryExecute(connection, "ROLLBACK");
            throw;
        }

        // The file leaves this method as one plain database file, no journal beside it.
        TryExecute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
        TryExecute(connection, "PRAGMA journal_mode = DELETE");
    }

    /// <summary>Folds the write-ahead log of the index at <paramref name="databasePath"/> into the
    /// database file and closes it again, so the file is complete by itself and can be moved alone.
    /// Failing (no such file, not a database) is not an error here.</summary>
    internal static void CheckpointIndex(string databasePath)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            TryExecute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
        catch (SqliteException)
        {
        }
    }

    private static void ExecuteWith(SqliteConnection connection, string sql, string id, string? own)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        if (own is not null)
            command.Parameters.AddWithValue("$own", own);
        command.ExecuteNonQuery();
    }

    private static void TryExecute(SqliteConnection connection, string sql)
    {
        try
        {
            Execute(connection, sql);
        }
        catch (SqliteException)
        {
        }
    }

    /// <summary>False once an existing database names a schema version newer than <see
    /// cref="SchemaVersion"/> - every read answers empty and every write is refused rather than
    /// touching a file a newer build understands better than this one does.</summary>
    public bool IsUsable { get; private set; } = true;

    // Set once a migration backup could not be written: this instance then stays unusable instead of
    // copying the whole database again on every later open (the indexer opens once per file).
    private bool _backupFailed;

    // True while a migration step runs inside EnsureSchema; an exception then marks the instance as
    // done with migrating for this session, so a failed step (a full disk, a damaged page) is not
    // retried with another full copy of the database on every later open.
    private bool _migrating;

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        // Microsoft.Data.Sqlite retries a busy statement itself, bounded by "Default Timeout"
        // (whole seconds) rather than by the PRAGMA below - both are set so a contended write gives
        // up after the same, short window instead of the connection's 30 s ADO default.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(_busyTimeoutMs / 1000.0)),
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        var fullPath = Path.GetFullPath(DatabasePath);
        // Checked before the connection creates the file: a deleted database has to be rebuilt.
        var schemaKnown = EnsuredDatabases.ContainsKey(fullPath) && File.Exists(fullPath);
        try
        {
            connection.Open();
            if (schemaKnown)
            {
                SetBusyTimeout(connection);
            }
            else
            {
                EnsureSchema(connection);
                if (IsUsable)
                    EnsuredDatabases[fullPath] = true;
            }
        }
        catch
        {
            // The caller never sees this connection, so it would otherwise keep the file open.
            connection.Dispose();
            throw;
        }

        return connection;
    }

    // Database files whose schema this process already brought up to date; later opens (the indexer
    // opens once per file) skip the schema work. Files a newer build wrote are never listed.
    private static readonly ConcurrentDictionary<string, bool> EnsuredDatabases = new(StringComparer.OrdinalIgnoreCase);

    private void SetBusyTimeout(SqliteConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutMs};";
        pragma.ExecuteNonQuery();
    }

    /// <summary>Brings the file up to <see cref="SchemaVersion"/> in one <c>BEGIN IMMEDIATE</c>
    /// transaction, so two first opens (the indexer and the window) cannot both create the version
    /// row: the second waits for the write lock and then finds the first one's work already done.
    /// A database at v4 keeps its Codex rows; older ones are rebuilt from the session files; from v5
    /// on every row stays. A file from a newer build is left entirely alone.</summary>
    private void EnsureSchema(SqliteConnection connection)
    {
        SetBusyTimeout(connection);
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            if (!ApplySchema(connection))
            {
                Execute(connection, "ROLLBACK");
                return;
            }

            Execute(connection, "COMMIT");
            IsUsable = true;
        }
        catch
        {
            if (_migrating)
                _backupFailed = true;

            try
            {
                Execute(connection, "ROLLBACK");
            }
            catch (SqliteException)
            {
            }

            throw;
        }
    }

    /// <summary>The schema work itself, run inside <see cref="EnsureSchema"/>'s transaction. False
    /// means nothing may change: a newer build's file, or a migration backup that could not be written
    /// (this instance then sits out the session instead of changing rows that exist nowhere else).</summary>
    private bool ApplySchema(SqliteConnection connection)
    {
        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_version (id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL);");

        long? existingVersion;
        using (var versionCommand = connection.CreateCommand())
        {
            // MAX rather than the first row: a file written before the single-row constraint
            // can hold two rows from the old open race.
            versionCommand.CommandText = "SELECT MAX(version) FROM schema_version";
            var existing = versionCommand.ExecuteScalar();
            existingVersion = existing is null or DBNull ? null : Convert.ToInt64(existing, CultureInfo.InvariantCulture);
        }

        if (existingVersion > SchemaVersion)
        {
            // A future build's own database - left alone entirely, including whatever shape its
            // tables are actually in, since this build might not even recognise it.
            IsUsable = false;
            return false;
        }

        // Older files hold a plain version table without the id column; rebuild it as the
        // single-row table. The version itself is written again at the end.
        bool hasIdColumn;
        using (var info = connection.CreateCommand())
        {
            info.CommandText = "SELECT 1 FROM pragma_table_info('schema_version') WHERE name = 'id'";
            hasIdColumn = info.ExecuteScalar() is not null;
        }

        if (!hasIdColumn)
        {
            Execute(connection, """
                CREATE TABLE schema_version_new (id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL);
                DROP TABLE schema_version;
                ALTER TABLE schema_version_new RENAME TO schema_version;
                """);
        }

        // Future version jumps migrate and keep the rows that stay valid; they never wipe the
        // tables wholesale again (the index is the only copy of past usage).
        var originalVersion = existingVersion;
        if (existingVersion == 4)
        {
            if (_backupFailed || !MigrateFromV4(connection))
            {
                _backupFailed = true;
                IsUsable = false;
                return false;
            }

            existingVersion = 5;
        }

        // The v4 step already left a backup of the untouched file; v5 and v6 files get theirs here.
        if (existingVersion is 5 or 6)
        {
            if (_backupFailed || (originalVersion != 4 && !BackUpBeforeMigration(originalVersion!.Value)))
            {
                _backupFailed = true;
                IsUsable = false;
                return false;
            }

            _migrating = true;
            MigrateToV7(connection);
            _migrating = false;
        }

        // A database written at an older schema version has usage rows this version cannot
        // trust (v1's rows carry no hour at all, the column itself did not exist yet) - rather than
        // guess a value for them, every usage row and every source_file marker is dropped here, so
        // StatsIndexer treats every session file as new again and rebuilds the index with whatever
        // this version's own schema needs. A brand new database (existingVersion is null) never hits
        // this - CREATE TABLE IF NOT EXISTS below is what builds its tables for the first time.
        if (existingVersion < 4)
            Execute(connection, "DROP TABLE IF EXISTS usage; DROP TABLE IF EXISTS source_file;");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS usage (
                provider TEXT NOT NULL,
                day TEXT NOT NULL,
                hour INTEGER NOT NULL DEFAULT 0,
                model TEXT NOT NULL,
                project TEXT NOT NULL,
                effort TEXT NOT NULL DEFAULT '',
                machine TEXT NOT NULL DEFAULT '',
                subagent INTEGER NOT NULL DEFAULT 0,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (provider, day, hour, model, project, effort, machine, subagent)
            );
            CREATE TABLE IF NOT EXISTS source_file (
                path TEXT PRIMARY KEY,
                provider TEXT NOT NULL,
                offset INTEGER NOT NULL,
                size INTEGER NOT NULL,
                write_time_utc TEXT NOT NULL,
                current_model TEXT NOT NULL DEFAULT '',
                cumulative_input_tokens INTEGER NOT NULL DEFAULT 0,
                cumulative_output_tokens INTEGER NOT NULL DEFAULT 0,
                cumulative_cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                cumulative_cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                current_effort TEXT NOT NULL DEFAULT '',
                last_message_key TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS session (
                provider TEXT NOT NULL,
                machine TEXT NOT NULL DEFAULT '',
                session_id TEXT NOT NULL,
                project TEXT NOT NULL DEFAULT '',
                first_utc TEXT NOT NULL,
                last_utc TEXT NOT NULL,
                input INTEGER NOT NULL DEFAULT 0,
                output INTEGER NOT NULL DEFAULT 0,
                cache_creation INTEGER NOT NULL DEFAULT 0,
                cache_read INTEGER NOT NULL DEFAULT 0,
                main_model TEXT NOT NULL DEFAULT '',
                subagent_tokens INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (provider, machine, session_id)
            );
            CREATE TABLE IF NOT EXISTS session_model (
                provider TEXT NOT NULL,
                machine TEXT NOT NULL DEFAULT '',
                session_id TEXT NOT NULL,
                model TEXT NOT NULL,
                tokens INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (provider, machine, session_id, model)
            );
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);

        // A database that held no usage has nothing to backfill: the walk fills the sessions in
        // together with the usage. Only a migrated one needs the one-time pass.
        if (originalVersion is null or < 4)
            Execute(connection, $"INSERT OR IGNORE INTO meta (key, value) VALUES ('{SessionsBackfilledKey}', '1')");

        using (var writeVersion = connection.CreateCommand())
        {
            writeVersion.CommandText = "INSERT OR REPLACE INTO schema_version (id, version) VALUES (1, $version)";
            writeVersion.Parameters.AddWithValue("$version", SchemaVersion);
            writeVersion.ExecuteNonQuery();
        }

        return true;
    }

    private const string SessionsBackfilledKey = "sessions_backfilled";

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Version 4 to 5: copies the database next to itself as <c>stats.v4.bak</c>, adds the
    /// <c>last_message_key</c> column and clears only the Claude rows, so the next indexer run reads
    /// every Claude file again without counting repeated responses. Codex rows stay untouched. Runs
    /// inside <see cref="EnsureSchema"/>'s write transaction, so the write lock is held while the
    /// file is copied. Returns false and changes nothing when the backup cannot be written.</summary>
    private bool MigrateFromV4(SqliteConnection connection)
    {
        var backupPath = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "stats.v4.bak");
        try
        {
            File.Copy(DatabasePath, backupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        bool hasColumn;
        using (var info = connection.CreateCommand())
        {
            info.CommandText = "SELECT 1 FROM pragma_table_info('source_file') WHERE name = 'last_message_key'";
            hasColumn = info.ExecuteScalar() is not null;
        }

        Execute(connection, (hasColumn ? "" : "ALTER TABLE source_file ADD COLUMN last_message_key TEXT NOT NULL DEFAULT '';")
            + "DELETE FROM usage WHERE provider = 'claude';"
            + "DELETE FROM source_file WHERE provider = 'claude';");
        return true;
    }

    /// <summary>Copies the database next to itself as <c>stats.v{old}.bak</c> before a migration
    /// changes it. False when the copy cannot be written.</summary>
    private bool BackUpBeforeMigration(long oldVersion)
    {
        var backupPath = Path.Combine(Path.GetDirectoryName(DatabasePath)!, $"stats.v{oldVersion}.bak");
        // Written under a temporary name and swapped in only when complete, so a copy that fails
        // halfway (a full disk) never destroys the good backup an earlier attempt left.
        var temporaryPath = backupPath + ".tmp";
        try
        {
            File.Copy(DatabasePath, temporaryPath, overwrite: true);
            File.Move(temporaryPath, backupPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }

    /// <summary>Version 5 or 6 to 7: rebuilds the usage table with the <c>machine</c> and
    /// <c>subagent</c> key columns, keeping every row and every token count. Claude rows filed under
    /// the project "subagents" (a transcript of a subagent that carried no path of its own) become
    /// subagent rows and keep that label. Runs inside <see cref="EnsureSchema"/>'s write
    /// transaction; the session tables come from the table creation that follows.</summary>
    private static void MigrateToV7(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE usage_v7 (
            provider TEXT NOT NULL,
            day TEXT NOT NULL,
            hour INTEGER NOT NULL DEFAULT 0,
            model TEXT NOT NULL,
            project TEXT NOT NULL,
            effort TEXT NOT NULL DEFAULT '',
            machine TEXT NOT NULL DEFAULT '',
            subagent INTEGER NOT NULL DEFAULT 0,
            input_tokens INTEGER NOT NULL DEFAULT 0,
            output_tokens INTEGER NOT NULL DEFAULT 0,
            cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
            cache_read_tokens INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (provider, day, hour, model, project, effort, machine, subagent)
        );
        INSERT INTO usage_v7 (provider, day, hour, model, project, effort, machine, subagent,
            input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens)
        SELECT provider, day, hour, model, project, effort, '',
            CASE WHEN provider = 'claude' AND project = 'subagents' THEN 1 ELSE 0 END,
            input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens
        FROM usage;
        DROP TABLE usage;
        ALTER TABLE usage_v7 RENAME TO usage;
        """);

    /// <summary>Test fixture helper: seeds usage rows the way a finished index walk would. Adds every
    /// record's token counts to whatever is already stored under its key - never replaces a row, so
    /// calling this twice with the same records doubles the totals. The indexer itself writes through
    /// <see cref="ApplyIndexResult"/>.</summary>
    internal void AddDelta(IReadOnlyList<StatsRecord> records)
    {
        if (records.Count == 0)
            return;

        try
        {
            using var connection = Open();
            if (!IsUsable)
                return;

            using var transaction = connection.BeginTransaction();
            AddDeltaWithinTransaction(connection, transaction, records);
            transaction.Commit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // best effort, same contract as HistoryStore - a missed write is never worth crashing over
        }
    }

    /// <summary>What <see cref="StatsIndexer"/> actually calls: a walk's new usage rows, the session
    /// totals they belong to and its source file's new offset, written in one transaction over one connection - so a crash between
    /// the two writes can never leave a range counted without its marker having moved, which would
    /// count that same range again on the next run.</summary>
    public void ApplyIndexResult(
        IReadOnlyList<StatsRecord> records, StatsSourceFileState sourceFileState, IReadOnlyList<StatsSessionDelta>? sessions = null)
    {
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return;

            using var transaction = connection.BeginTransaction();
            AddDeltaWithinTransaction(connection, transaction, records);
            if (sessions is { Count: > 0 })
                AddSessionDeltasWithinTransaction(connection, transaction, sessions, replace: false);
            SetSourceFileWithinTransaction(connection, transaction, sourceFileState);
            transaction.Commit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
        }
    }

    private static void AddDeltaWithinTransaction(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<StatsRecord> records)
    {
        if (records.Count == 0)
            return;

        // One command for the whole batch: only the parameter values change per record.
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO usage (provider, day, hour, model, project, effort, machine, subagent, input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens)
            VALUES ($provider, $day, $hour, $model, $project, $effort, $machine, $subagent, $input, $output, $cacheCreation, $cacheRead)
            ON CONFLICT (provider, day, hour, model, project, effort, machine, subagent) DO UPDATE SET
                input_tokens = input_tokens + excluded.input_tokens,
                output_tokens = output_tokens + excluded.output_tokens,
                cache_creation_tokens = cache_creation_tokens + excluded.cache_creation_tokens,
                cache_read_tokens = cache_read_tokens + excluded.cache_read_tokens
            """;
        var provider = AddParameter(command, "$provider", SqliteType.Text);
        var day = AddParameter(command, "$day", SqliteType.Text);
        var hour = AddParameter(command, "$hour", SqliteType.Integer);
        var model = AddParameter(command, "$model", SqliteType.Text);
        var project = AddParameter(command, "$project", SqliteType.Text);
        var effort = AddParameter(command, "$effort", SqliteType.Text);
        var machine = AddParameter(command, "$machine", SqliteType.Text);
        var subagent = AddParameter(command, "$subagent", SqliteType.Integer);
        var input = AddParameter(command, "$input", SqliteType.Integer);
        var output = AddParameter(command, "$output", SqliteType.Integer);
        var cacheCreation = AddParameter(command, "$cacheCreation", SqliteType.Integer);
        var cacheRead = AddParameter(command, "$cacheRead", SqliteType.Integer);
        command.Prepare();

        foreach (var record in records)
        {
            provider.Value = record.Provider;
            day.Value = record.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            hour.Value = record.Hour;
            model.Value = record.Model;
            project.Value = record.Project;
            effort.Value = record.Effort;
            machine.Value = record.Machine;
            subagent.Value = record.Subagent ? 1 : 0;
            input.Value = record.InputTokens;
            output.Value = record.OutputTokens;
            cacheCreation.Value = record.CacheCreationTokens;
            cacheRead.Value = record.CacheReadTokens;
            command.ExecuteNonQuery();
        }
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Adds each session delta to its stored session (or, with <paramref name="replace"/>,
    /// replaces the stored session by it) and brings the session's main model up to date from the
    /// per-model totals. A delta without main agent tokens never replaces a project that is already
    /// known.</summary>
    private static void AddSessionDeltasWithinTransaction(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<StatsSessionDelta> deltas, bool replace)
    {
        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = """
            DELETE FROM session_model WHERE provider = $provider AND machine = $machine AND session_id = $session;
            DELETE FROM session WHERE provider = $provider AND machine = $machine AND session_id = $session;
            """;

        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO session (provider, machine, session_id, project, first_utc, last_utc,
                input, output, cache_creation, cache_read, main_model, subagent_tokens)
            VALUES ($provider, $machine, $session, $project, $first, $last, $input, $output, $cacheCreation, $cacheRead, '', $subagent)
            ON CONFLICT (provider, machine, session_id) DO UPDATE SET
                project = CASE WHEN excluded.project <> '' AND ($main = 1 OR session.project = '')
                               THEN excluded.project ELSE session.project END,
                first_utc = MIN(first_utc, excluded.first_utc),
                last_utc = MAX(last_utc, excluded.last_utc),
                input = input + excluded.input,
                output = output + excluded.output,
                cache_creation = cache_creation + excluded.cache_creation,
                cache_read = cache_read + excluded.cache_read,
                subagent_tokens = subagent_tokens + excluded.subagent_tokens
            """;

        using var model = connection.CreateCommand();
        model.Transaction = transaction;
        model.CommandText = """
            INSERT INTO session_model (provider, machine, session_id, model, tokens)
            VALUES ($provider, $machine, $session, $model, $tokens)
            ON CONFLICT (provider, machine, session_id, model) DO UPDATE SET tokens = tokens + excluded.tokens
            """;

        using var mainModel = connection.CreateCommand();
        mainModel.Transaction = transaction;
        mainModel.CommandText = """
            UPDATE session SET main_model = COALESCE((
                SELECT model FROM session_model
                WHERE provider = $provider AND machine = $machine AND session_id = $session
                ORDER BY tokens DESC, model LIMIT 1), '')
            WHERE provider = $provider AND machine = $machine AND session_id = $session
            """;

        foreach (var delta in deltas)
        {
            foreach (var command in new[] { clear, upsert, model, mainModel })
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$provider", delta.Provider);
                command.Parameters.AddWithValue("$machine", delta.Machine);
                command.Parameters.AddWithValue("$session", delta.SessionId);
            }

            if (replace)
                clear.ExecuteNonQuery();

            upsert.Parameters.AddWithValue("$project", delta.Project);
            upsert.Parameters.AddWithValue("$first", FormatUtc(delta.FirstUtc));
            upsert.Parameters.AddWithValue("$last", FormatUtc(delta.LastUtc));
            upsert.Parameters.AddWithValue("$input", delta.InputTokens);
            upsert.Parameters.AddWithValue("$output", delta.OutputTokens);
            upsert.Parameters.AddWithValue("$cacheCreation", delta.CacheCreationTokens);
            upsert.Parameters.AddWithValue("$cacheRead", delta.CacheReadTokens);
            upsert.Parameters.AddWithValue("$subagent", delta.SubagentTokens);
            upsert.Parameters.AddWithValue("$main", delta.HasMainThreadTokens ? 1 : 0);
            upsert.ExecuteNonQuery();

            foreach (var (name, tokens) in delta.ModelTokens)
            {
                model.Parameters.Clear();
                model.Parameters.AddWithValue("$provider", delta.Provider);
                model.Parameters.AddWithValue("$machine", delta.Machine);
                model.Parameters.AddWithValue("$session", delta.SessionId);
                model.Parameters.AddWithValue("$model", name);
                model.Parameters.AddWithValue("$tokens", tokens);
                model.ExecuteNonQuery();
            }

            mainModel.ExecuteNonQuery();
        }
    }

    /// <summary>True once the sessions of the usage rows an older version left behind were filled in.</summary>
    /// <summary>True when the index holds at least one usage row. False too when it cannot be read.</summary>
    public bool HasUsageRows()
    {
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return false;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM usage)";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return false;
        }
    }

    public bool IsSessionBackfillDone()
    {
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return true;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key";
            command.Parameters.AddWithValue("$key", SessionsBackfilledKey);
            return command.ExecuteScalar() as string == "1";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // Unreadable: do not start a pass that could not be written either.
            return true;
        }
    }

    /// <summary>Writes the sessions found by the one-time backfill, replacing whatever the store holds
    /// for the same sessions, and marks the backfill done - in one transaction, so a failed write
    /// leaves the pass to be repeated. The usage rows are not touched. False when nothing was written.</summary>
    public bool ReplaceSessionsAndMarkBackfilled(IReadOnlyList<StatsSessionDelta> sessions)
    {
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return false;

            using var transaction = connection.BeginTransaction();
            AddSessionDeltasWithinTransaction(connection, transaction, sessions, replace: true);
            using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, '1')";
            mark.Parameters.AddWithValue("$key", SessionsBackfilledKey);
            mark.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return false;
        }
    }

    /// <summary>Every stored session, unfiltered; the window filters them by their start day.</summary>
    public IReadOnlyList<StatsSessionRecord> LoadSessions()
    {
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return [];

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT provider, machine, session_id, project, first_utc, last_utc,
                       input, output, cache_creation, cache_read, main_model, subagent_tokens
                FROM session
                """;
            using var reader = command.ExecuteReader();

            var results = new List<StatsSessionRecord>();
            while (reader.Read())
            {
                if (!DateTimeOffset.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var first)
                    || !DateTimeOffset.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last))
                    continue;

                results.Add(new StatsSessionRecord(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), first, last,
                    reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetString(10), reader.GetInt64(11)));
            }

            return results;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or FormatException)
        {
            return [];
        }
    }

    private static SqliteParameter AddParameter(SqliteCommand command, string name, SqliteType type)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.SqliteType = type;
        command.Parameters.Add(parameter);
        return parameter;
    }

    /// <summary>Every stored row, unfiltered - the raw material <c>StatsAggregator</c> groups by
    /// whatever the window's period/grouping selection currently is.</summary>
    public IReadOnlyList<StatsRecord> LoadAll()
    {
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return [];

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT provider, day, model, project, input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens, hour, effort, machine, subagent FROM usage";
            using var reader = command.ExecuteReader();

            var results = new List<StatsRecord>();
            while (reader.Read())
            {
                // A row a future build wrote in some shape this one no longer parses is skipped
                // rather than failing the whole load - the statistics window still opens with
                // whatever else is readable.
                if (!DateOnly.TryParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    continue;

                results.Add(new StatsRecord(
                    Provider: reader.GetString(0),
                    Day: day,
                    Model: reader.GetString(2),
                    Project: reader.GetString(3),
                    InputTokens: reader.GetInt64(4),
                    OutputTokens: reader.GetInt64(5),
                    CacheCreationTokens: reader.GetInt64(6),
                    CacheReadTokens: reader.GetInt64(7),
                    Hour: reader.GetInt32(8),
                    Effort: reader.GetString(9),
                    Machine: reader.GetString(10),
                    Subagent: reader.GetInt32(11) != 0));
            }

            return results;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or FormatException)
        {
            return [];
        }
    }

    /// <summary>The sum of every token column across every row for <paramref name="providerId"/> on
    /// or after <paramref name="from"/>, computed directly in SQL rather than through <see
    /// cref="LoadAll"/> - a caller that only wants one provider's running weekly total should never
    /// pay for pulling and summing every row of every provider first.</summary>
    public long SumTokensSince(string providerId, DateOnly from)
    {
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return 0;

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT SUM(input_tokens + output_tokens + cache_creation_tokens + cache_read_tokens)
                FROM usage WHERE provider = $provider AND day >= $from
                """;
            command.Parameters.AddWithValue("$provider", providerId);
            command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var result = command.ExecuteScalar();
            return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return 0;
        }
    }

    public StatsSourceFileState? GetSourceFile(string path) =>
        TryGetSourceFile(path, out var state) ? state : null;

    /// <summary>False when the index could not be read at all, which is not the same as "no row":
    /// the indexer must then leave the file alone for this walk, because reading it as a new file
    /// would add all of its usage a second time.</summary>
    public bool TryGetSourceFile(string path, out StatsSourceFileState? state)
    {
        state = null;
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return false;

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT provider, offset, size, write_time_utc, current_model,
                       cumulative_input_tokens, cumulative_output_tokens, cumulative_cache_creation_tokens, cumulative_cache_read_tokens,
                       current_effort, last_message_key
                FROM source_file WHERE path = $path
                """;
            command.Parameters.AddWithValue("$path", path);
            using var reader = command.ExecuteReader();
            if (reader.Read())
                state = ReadSourceFileState(reader, path, firstColumn: 0);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return false;
        }
    }

    /// <summary>Every remembered source file in one query, keyed by path exactly as stored. The indexer
    /// reads this once per walk instead of opening the database once for each of several thousand
    /// unchanged session files. Empty when the index cannot be read, so the caller falls back to
    /// asking per file.</summary>
    public Dictionary<string, StatsSourceFileState> LoadSourceFiles()
    {
        TryLoadSourceFiles(out var states);
        return states;
    }

    /// <summary>The same snapshot, saying whether it could be read: false when the index is locked,
    /// unreadable or from a newer build - an empty result is then a failed read, not an index without
    /// files, which a one-time pass must never mistake for "nothing to do".</summary>
    public bool TryLoadSourceFiles(out Dictionary<string, StatsSourceFileState> states)
    {
        states = new Dictionary<string, StatsSourceFileState>(StringComparer.Ordinal);
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return false;

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT path, provider, offset, size, write_time_utc, current_model,
                       cumulative_input_tokens, cumulative_output_tokens, cumulative_cache_creation_tokens, cumulative_cache_read_tokens,
                       current_effort, last_message_key
                FROM source_file
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                states[path] = ReadSourceFileState(reader, path, firstColumn: 1);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or FormatException)
        {
            states = new Dictionary<string, StatsSourceFileState>(StringComparer.Ordinal);
            return false;
        }
    }

    private static StatsSourceFileState ReadSourceFileState(SqliteDataReader reader, string path, int firstColumn) => new(
        Path: path,
        Provider: reader.GetString(firstColumn),
        Offset: reader.GetInt64(firstColumn + 1),
        Size: reader.GetInt64(firstColumn + 2),
        WriteTimeUtc: DateTime.Parse(reader.GetString(firstColumn + 3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        CurrentModel: reader.GetString(firstColumn + 4),
        CumulativeInputTokens: reader.GetInt64(firstColumn + 5),
        CumulativeOutputTokens: reader.GetInt64(firstColumn + 6),
        CumulativeCacheCreationTokens: reader.GetInt64(firstColumn + 7),
        CumulativeCacheReadTokens: reader.GetInt64(firstColumn + 8),
        CurrentEffort: reader.GetString(firstColumn + 9),
        LastMessageKey: reader.GetString(firstColumn + 10));

    /// <summary>Takes over the row of a file that was moved: when <paramref name="provider"/> has a
    /// row whose file name equals <paramref name="newPath"/>'s and whose path no longer exists on
    /// disk, that row is re-keyed to <paramref name="newPath"/> (offset and running totals kept) and
    /// returned. A same-name file that still exists at its old path is a different file and is never
    /// taken over. Null when there is nothing to adopt.</summary>
    public StatsSourceFileState? TryAdoptMovedSourceFile(string newPath, string provider)
    {
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return null;

            var fileName = Path.GetFileName(newPath);
            var candidates = new List<string>();
            using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT path FROM source_file WHERE provider = $provider AND path <> $path";
                list.Parameters.AddWithValue("$provider", provider);
                list.Parameters.AddWithValue("$path", newPath);
                using var reader = list.ExecuteReader();
                while (reader.Read())
                    candidates.Add(reader.GetString(0));
            }

            var oldPath = candidates.FirstOrDefault(candidate =>
                string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase) && !File.Exists(candidate));
            if (oldPath is null)
                return null;

            var old = GetSourceFile(oldPath);
            if (old is null)
                return null;

            var moved = old with { Path = newPath };
            using var transaction = connection.BeginTransaction();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM source_file WHERE path = $path";
                delete.Parameters.AddWithValue("$path", oldPath);
                delete.ExecuteNonQuery();
            }

            SetSourceFileWithinTransaction(connection, transaction, moved);
            transaction.Commit();
            return moved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return null;
        }
    }

    public void SetSourceFile(StatsSourceFileState state)
    {
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return;

            using var transaction = connection.BeginTransaction();
            SetSourceFileWithinTransaction(connection, transaction, state);
            transaction.Commit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
        }
    }

    private static void SetSourceFileWithinTransaction(SqliteConnection connection, SqliteTransaction transaction, StatsSourceFileState state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO source_file (path, provider, offset, size, write_time_utc, current_model,
                cumulative_input_tokens, cumulative_output_tokens, cumulative_cache_creation_tokens, cumulative_cache_read_tokens,
                current_effort, last_message_key)
            VALUES ($path, $provider, $offset, $size, $writeTime, $model, $cumInput, $cumOutput, $cumCacheCreation, $cumCacheRead, $effort, $lastKey)
            ON CONFLICT (path) DO UPDATE SET
                provider = excluded.provider,
                offset = excluded.offset,
                size = excluded.size,
                write_time_utc = excluded.write_time_utc,
                current_model = excluded.current_model,
                cumulative_input_tokens = excluded.cumulative_input_tokens,
                cumulative_output_tokens = excluded.cumulative_output_tokens,
                cumulative_cache_creation_tokens = excluded.cumulative_cache_creation_tokens,
                cumulative_cache_read_tokens = excluded.cumulative_cache_read_tokens,
                current_effort = excluded.current_effort,
                last_message_key = excluded.last_message_key
            """;
        command.Parameters.AddWithValue("$path", state.Path);
        command.Parameters.AddWithValue("$provider", state.Provider);
        command.Parameters.AddWithValue("$offset", state.Offset);
        command.Parameters.AddWithValue("$size", state.Size);
        command.Parameters.AddWithValue("$writeTime", state.WriteTimeUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$model", state.CurrentModel);
        command.Parameters.AddWithValue("$cumInput", state.CumulativeInputTokens);
        command.Parameters.AddWithValue("$cumOutput", state.CumulativeOutputTokens);
        command.Parameters.AddWithValue("$cumCacheCreation", state.CumulativeCacheCreationTokens);
        command.Parameters.AddWithValue("$cumCacheRead", state.CumulativeCacheReadTokens);
        command.Parameters.AddWithValue("$effort", state.CurrentEffort);
        command.Parameters.AddWithValue("$lastKey", state.LastMessageKey);
        command.ExecuteNonQuery();
    }
}
