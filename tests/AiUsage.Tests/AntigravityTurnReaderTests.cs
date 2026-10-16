using AiUsage.Providers.Parsing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiUsage.Tests;

/// <summary>Conversation databases are built in a temp folder by each test; the tool's own files on
/// this machine are never read. Only the step type and status columns exist in the fixtures, the real
/// payload columns are never needed.</summary>
public class AntigravityTurnReaderTests : IDisposable
{
    private readonly DisposableTestDirectory _directory = TestPaths.CreateDisposableDirectory("ai-usage-antigravity-turn");

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void The_newest_step_is_read_through_the_shared_untrusted_schema_opening()
    {
        var source = SourceScan.Read("src", "AiUsage", "Providers", "Parsing", "AntigravityTurnReader.cs");

        var body = SourceScan.Member(source, "private static SessionRecordKind? ReadLastStep");
        Assert.Contains("ForeignDatabase.Open(path)", body);
        Assert.DoesNotContain("new SqliteConnection(", body);
    }

    [Fact]
    public void A_finished_planner_answer_is_an_assistant_turn_stamped_with_the_write_time()
    {
        var root = Root("cli");
        var written = DateTime.UtcNow.AddMinutes(-5);
        CreateConversation(root, "a", written, (1, 14, 3), (2, 15, 3));

        var (kind, timestamp) = AntigravityTurnReader.ReadNewestTurn([root]);

        Assert.Equal(SessionRecordKind.AssistantTurn, kind);
        Assert.Equal(new DateTimeOffset(written, TimeSpan.Zero), timestamp);
    }

    [Fact]
    public void A_tool_step_last_is_not_an_assistant_turn()
    {
        var root = Root("cli");
        CreateConversation(root, "a", DateTime.UtcNow, (1, 14, 3), (2, 15, 3), (3, 132, 3));

        Assert.NotEqual(SessionRecordKind.AssistantTurn, AntigravityTurnReader.ReadNewestTurn([root]).Kind);
    }

    [Fact]
    public void A_planner_step_that_ended_with_an_error_is_not_an_assistant_turn()
    {
        var root = Root("cli");
        CreateConversation(root, "a", DateTime.UtcNow, (1, 14, 3), (2, 15, 7));

        Assert.NotEqual(SessionRecordKind.AssistantTurn, AntigravityTurnReader.ReadNewestTurn([root]).Kind);
    }

    [Fact]
    public void The_last_step_by_index_decides_not_the_last_row_inserted()
    {
        var root = Root("cli");
        CreateConversation(root, "a", DateTime.UtcNow, (2, 15, 3), (1, 14, 3));
        // Rows were inserted out of order above: index 2 is the newest step.
        Assert.Equal(SessionRecordKind.AssistantTurn, AntigravityTurnReader.ReadNewestTurn([root]).Kind);
    }

    [Fact]
    public void A_user_input_last_reads_as_a_user_turn()
    {
        var root = Root("cli");
        CreateConversation(root, "a", DateTime.UtcNow, (1, 14, 3));

        Assert.Equal(SessionRecordKind.UserTurn, AntigravityTurnReader.ReadNewestTurn([root]).Kind);
    }

    [Fact]
    public void Only_the_newest_conversation_across_all_folders_counts()
    {
        var cli = Root("cli");
        var ide = Root("ide");
        CreateConversation(cli, "old", DateTime.UtcNow.AddHours(-3), (1, 14, 3), (2, 15, 3));
        CreateConversation(ide, "new", DateTime.UtcNow.AddMinutes(-1), (1, 14, 3), (2, 15, 3), (3, 132, 3));

        Assert.NotEqual(SessionRecordKind.AssistantTurn, AntigravityTurnReader.ReadNewestTurn([cli, ide]).Kind);
    }

    [Fact]
    public void A_newer_write_ahead_file_makes_its_conversation_the_newest()
    {
        var root = Root("cli");
        CreateConversation(root, "a", DateTime.UtcNow.AddHours(-2), (1, 14, 3), (2, 15, 3));
        CreateConversation(root, "b", DateTime.UtcNow.AddHours(-1), (1, 14, 3), (2, 15, 3), (3, 132, 3));
        var walPath = Path.Combine(root, "a.db-wal");
        File.WriteAllBytes(walPath, []);
        File.SetLastWriteTimeUtc(walPath, DateTime.UtcNow.AddMinutes(-2));

        var (kind, timestamp) = AntigravityTurnReader.ReadNewestTurn([root]);

        Assert.Equal(SessionRecordKind.AssistantTurn, kind);
        Assert.True(DateTimeOffset.UtcNow - timestamp < TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void A_missing_folder_means_no_turn()
    {
        var (kind, timestamp) = AntigravityTurnReader.ReadNewestTurn([Path.Combine(_directory.Path, "nope")]);

        Assert.Null(kind);
        Assert.Null(timestamp);
    }

    [Fact]
    public void An_empty_folder_and_a_folder_without_databases_mean_no_turn()
    {
        var root = Root("cli");
        File.WriteAllText(Path.Combine(root, "note.txt"), "x");

        Assert.Null(AntigravityTurnReader.ReadNewestTurn([root]).Kind);
    }

    [Fact]
    public void A_file_that_is_not_a_database_never_throws()
    {
        var root = Root("cli");
        File.WriteAllText(Path.Combine(root, "broken.db"), "this is not sqlite");

        var (kind, timestamp) = AntigravityTurnReader.ReadNewestTurn([root]);

        Assert.Null(kind);
        Assert.Null(timestamp);
    }

    [Fact]
    public void A_database_without_steps_or_with_no_rows_means_no_turn()
    {
        var root = Root("cli");
        var other = Path.Combine(root, "other.db");
        using (var connection = Open(other))
            Execute(connection, "CREATE TABLE unrelated (x integer);");
        CreateConversation(root, "empty", DateTime.UtcNow.AddMinutes(-1));

        Assert.Null(AntigravityTurnReader.ReadNewestTurn([root]).Kind);
    }

    [Fact]
    public void The_conversation_file_is_left_unchanged()
    {
        var root = Root("cli");
        var path = CreateConversation(root, "a", DateTime.UtcNow.AddMinutes(-5), (1, 14, 3), (2, 15, 3));
        var before = File.ReadAllBytes(path);

        AntigravityTurnReader.ReadNewestTurn([root]);

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private string Root(string name)
    {
        var path = Path.Combine(_directory.Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A conversation database with the tool's step table, one row per (index, type, status)
    /// in the order given, whose last write time is set to <paramref name="writeTimeUtc"/>.</summary>
    internal static string CreateConversation(string root, string id, DateTime writeTimeUtc, params (long Index, long Type, long Status)[] steps)
    {
        var path = Path.Combine(root, id + ".db");
        using (var connection = Open(path))
        {
            Execute(connection, "CREATE TABLE steps (idx integer, step_type integer, status integer, step_payload blob, PRIMARY KEY (idx));");
            foreach (var (index, type, status) in steps)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO steps (idx, step_type, status, step_payload) VALUES ($idx, $type, $status, x'00')";
                command.Parameters.AddWithValue("$idx", index);
                command.Parameters.AddWithValue("$type", type);
                command.Parameters.AddWithValue("$status", status);
                command.ExecuteNonQuery();
            }
        }

        File.SetLastWriteTimeUtc(path, writeTimeUtc);
        return path;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
