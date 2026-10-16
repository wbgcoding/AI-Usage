using Microsoft.Data.Sqlite;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Reads, for <see cref="AttentionDetector"/>, whether the newest Antigravity conversation ended on
/// a finished answer. Each conversation is one SQLite file whose <c>steps</c> table holds one row per
/// step; only the step type and status columns of the newest row are ever read, never a payload or
/// any other column. The file is opened read-only, unpooled and with a short lock wait, so the
/// tool's own data is never changed or held. Never throws: a missing folder, a locked or damaged
/// file all read as "no turn", like a missing session file upstream.
/// </summary>
public static class AntigravityTurnReader
{
    // Measured on real conversations: the model's own answer is step type 15, and a step that
    // finished normally has status 3 (7 means it ended with an error). User input is 14; tool steps
    // (132, 101, ...) mean the agent is still working.
    private const long PlannerResponseType = 15;
    private const long UserInputType = 14;
    private const long DoneStatus = 3;

    /// <summary>The folders the command line tool and the IDE keep their conversations in, under
    /// <c>~/.gemini</c>; the ones that do not exist are skipped when reading.</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var gemini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
        return
        [
            Path.Combine(gemini, "antigravity-cli", "conversations"),
            Path.Combine(gemini, "antigravity-ide", "conversations"),
            Path.Combine(gemini, "antigravity", "conversations"),
        ];
    }

    /// <summary>Looks only at the conversation written to most recently across <paramref name="roots"/>
    /// (newest of the database file and its write-ahead file). A finished planner answer is <see
    /// cref="SessionRecordKind.AssistantTurn"/>, user input is <see cref="SessionRecordKind.UserTurn"/>
    /// and anything else, an errored answer included, is <see cref="SessionRecordKind.ToolCall"/>;
    /// the timestamp is that last write time.</summary>
    public static (SessionRecordKind? Kind, DateTimeOffset? Timestamp) ReadNewestTurn(IEnumerable<string> roots)
    {
        try
        {
            (string Path, DateTime WriteTimeUtc)? newest = null;
            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var database in Directory.EnumerateFiles(root, "*.db"))
                {
                    var written = LastWriteTimeUtc(database);
                    if (newest is null || written > newest.Value.WriteTimeUtc)
                        newest = (database, written);
                }
            }

            if (newest is not { } chosen)
                return (null, null);

            var kind = ReadLastStep(chosen.Path);
            return kind is null ? (null, null) : (kind, new DateTimeOffset(chosen.WriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    private static DateTime LastWriteTimeUtc(string database)
    {
        var written = File.GetLastWriteTimeUtc(database);
        var walWritten = File.GetLastWriteTimeUtc(database + "-wal");
        return walWritten > written ? walWritten : written;
    }

    private static SessionRecordKind? ReadLastStep(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        // A view or virtual table could generate rows without end; only a plain table is read.
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT type FROM sqlite_master WHERE name = 'steps'";
            if (check.ExecuteScalar() is not string type || type != "table")
                return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT step_type, status FROM steps ORDER BY idx DESC LIMIT 1";
        using var row = command.ExecuteReader();
        if (!row.Read() || row.IsDBNull(0) || row.IsDBNull(1))
            return null;

        var (stepType, status) = (row.GetInt64(0), row.GetInt64(1));
        if (stepType == PlannerResponseType && status == DoneStatus)
            return SessionRecordKind.AssistantTurn;
        return stepType == UserInputType ? SessionRecordKind.UserTurn : SessionRecordKind.ToolCall;
    }
}
