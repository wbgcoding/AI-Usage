using AiUsage.Models;
using AiUsage.Stats;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>The Antigravity reader against damaged, oversized and hostile conversation files. Every
/// database here is built in the test.</summary>
public class AntigravityHardeningTests
{
    private const long Start = 1_791_500_000;
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void InsertRaw(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static string NewDatabase(string directory, string name, params (long Index, byte[] Blob)[] calls)
    {
        var path = Path.Combine(directory, name);
        AntigravityFixtures.CreateDatabase(path, Start);
        foreach (var (index, blob) in calls)
            AntigravityFixtures.AddCall(path, index, blob);
        return path;
    }

    private static byte[] Call(long input = 10) => AntigravityFixtures.Generation("m", input, 0, 1, 1, step: null);

    private static StatsIndexResult Run(StatsStore store, string claude, string codex, params string[] roots) =>
        new StatsIndexer(store, claude, codex) { AntigravityRoots = roots }.IndexOnce();

    [Fact]
    public void An_oversized_blob_is_skipped_without_stopping_the_read()
    {
        using var directory = TestPaths.CreateDisposableDirectory("antigravity-big");
        var path = NewDatabase(directory, "c.db", (0, Call()), (2, Call(20)));
        InsertRaw(path, "INSERT INTO gen_metadata (idx, data, size) VALUES (1, zeroblob(5 * 1024 * 1024), 0)");

        var result = AntigravityUsageLogParser.ReadConversation(path, 0, LongAgo);

        Assert.Equal(2, result.Events.Count);
        Assert.Equal(1, result.RowsSkipped);
        Assert.Equal(3, result.NextIndex);
    }

    [Fact]
    public void A_text_value_in_the_data_column_is_skipped()
    {
        using var directory = TestPaths.CreateDisposableDirectory("antigravity-text");
        var path = NewDatabase(directory, "c.db", (0, Call()));
        InsertRaw(path, "INSERT INTO gen_metadata (idx, data, size) VALUES (1, 'plain text', 0)");

        var result = AntigravityUsageLogParser.ReadConversation(path, 0, LongAgo);

        Assert.Single(result.Events);
        Assert.Equal(1, result.RowsSkipped);
    }

    [Fact]
    public void A_long_or_unprintable_model_name_and_absurd_counts_are_not_calls()
    {
        Assert.False(AntigravityUsageLogParser.TryParseGeneration(
            AntigravityFixtures.Generation(new string('m', 129), 10, 0, 1, 1, step: null), out _));
        Assert.True(AntigravityUsageLogParser.TryParseGeneration(
            AntigravityFixtures.Generation(new string('m', 128), 10, 0, 1, 1, step: null), out _));
        Assert.False(AntigravityUsageLogParser.TryParseGeneration(
            AntigravityFixtures.Generation("model\u0001x", 10, 0, 1, 1, step: null), out _));
        Assert.False(AntigravityUsageLogParser.TryParseGeneration(
            AntigravityFixtures.Generation("m", 2_000_000_000, 0, 1, 1, step: null), out _));
        Assert.False(AntigravityUsageLogParser.TryParseGeneration(
            AntigravityFixtures.Generation("m", 10, 5_000_000_000, 1, 1, step: null), out _));
    }

    [Fact]
    public void A_read_takes_a_bounded_number_of_rows_and_the_rest_follows()
    {
        using var directory = TestPaths.CreateDisposableDirectory("antigravity-cap");
        var path = NewDatabase(directory, "c.db");
        var blob = Call();
        var total = AntigravityUsageLogParser.MaxRowsPerRead + 7;
        using (var connection = Open(path))
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO gen_metadata (idx, data, size) VALUES ($i, $d, 0)";
            var index = command.Parameters.Add("$i", SqliteType.Integer);
            command.Parameters.AddWithValue("$d", blob);
            for (var i = 0; i < total; i++)
            {
                index.Value = i;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var first = AntigravityUsageLogParser.ReadConversation(path, 0, LongAgo);
        var second = AntigravityUsageLogParser.ReadConversation(path, first.NextIndex, LongAgo);

        Assert.Equal(AntigravityUsageLogParser.MaxRowsPerRead, first.Events.Count);
        Assert.True(first.Incomplete);
        Assert.Equal(AntigravityUsageLogParser.MaxRowsPerRead, first.NextIndex);
        Assert.Equal(7, second.Events.Count);
        Assert.False(second.Incomplete);
    }

    [Fact]
    public void A_cancelled_read_stops_with_the_cancellation()
    {
        using var directory = TestPaths.CreateDisposableDirectory("antigravity-cancel");
        var path = NewDatabase(directory, "c.db", (0, Call()));
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            AntigravityUsageLogParser.ReadConversation(path, 0, LongAgo, cancellationToken: source.Token));
    }

    [Fact]
    public void A_view_that_generates_rows_is_refused_instead_of_read()
    {
        using var directory = TestPaths.CreateDisposableDirectory("antigravity-view");
        var path = Path.Combine(directory, "c.db");
        Execute(path, "CREATE VIEW gen_metadata AS WITH RECURSIVE c(idx) AS (SELECT 0 UNION ALL SELECT idx + 1 FROM c) SELECT idx, x'00' AS data FROM c");

        Assert.Throws<InvalidDataException>(() => AntigravityUsageLogParser.ReadConversation(path, 0, LongAgo));
    }

    [Fact]
    public void Rows_without_a_usable_index_are_skipped_and_the_index_never_wraps()
    {
        using var directory = TestPaths.CreateDisposableDirectory("antigravity-index");
        var path = Path.Combine(directory, "c.db");
        Execute(path, "CREATE TABLE gen_metadata (idx, data blob)");
        Execute(path, "CREATE TABLE steps (idx, metadata blob)");
        var blob = Call();
        InsertRaw(path, "INSERT INTO gen_metadata (idx, data) VALUES (NULL, $d), (-5, $d), ('abc', $d), (9223372036854775807, $d), (4, $d)", ("$d", blob));

        var result = AntigravityUsageLogParser.ReadConversation(path, 0, LongAgo);

        Assert.Equal(4, Assert.Single(result.Events).Index);
        Assert.Equal(5, result.NextIndex);
        Assert.Equal(1, result.RowsSkipped);
    }

    [Fact]
    public void A_call_waits_for_its_step_while_the_file_is_fresh_and_falls_back_once_it_is_not()
    {
        using var directory = TestPaths.CreateDisposableDirectory("antigravity-wait");
        var path = NewDatabase(directory, "c.db", (0, Call()));
        AntigravityFixtures.AddCall(path, 1, AntigravityFixtures.Generation("m", 7, 0, 1, 1, step: 5));

        var fresh = AntigravityUsageLogParser.ReadConversation(path, 0, DateTime.UtcNow);
        var settled = AntigravityUsageLogParser.ReadConversation(path, 0, LongAgo);

        Assert.Single(fresh.Events);
        Assert.Equal(1, fresh.NextIndex);
        Assert.True(fresh.Incomplete);
        Assert.Equal(2, settled.Events.Count);
        Assert.Equal(2, settled.NextIndex);
        Assert.False(settled.Incomplete);
    }

    [Fact]
    public void A_database_without_the_expected_tables_is_remembered_and_never_stops_the_walk()
    {
        using var data = TestPaths.CreateDisposableDirectory("antigravity-poison-data");
        using var conversations = TestPaths.CreateDisposableDirectory("antigravity-poison");
        using var claude = TestPaths.CreateDisposableDirectory("antigravity-poison-claude");
        using var codex = TestPaths.CreateDisposableDirectory("antigravity-poison-codex");
        var empty = Path.Combine(conversations, "a-empty.db");
        Execute(empty, "CREATE TABLE other (x integer)");
        var textIndex = Path.Combine(conversations, "b-textindex.db");
        Execute(textIndex, "CREATE TABLE gen_metadata (idx, data blob)");
        InsertRaw(textIndex, "INSERT INTO gen_metadata (idx, data) VALUES ('abc', $d)", ("$d", Call()));
        var good = NewDatabase(conversations, "c-good.db", (0, Call()));
        foreach (var file in new[] { empty, textIndex, good })
            File.SetLastWriteTimeUtc(file, LongAgo);
        var store = new StatsStore(data);

        var first = Run(store, claude, codex, conversations);
        var states = store.LoadSourceFiles();
        var second = Run(store, claude, codex, conversations);

        Assert.Equal(3, first.Gemini.FilesSeen);
        Assert.Equal(2, first.Gemini.FilesRead);
        Assert.Equal(12, store.LoadAll().Sum(record => record.TotalTokens));
        Assert.Equal(new FileInfo(empty).Length, states[empty].Size);
        Assert.Equal(new FileInfo(textIndex).Length, states[textIndex].Size);
        Assert.Equal(0, second.Gemini.FilesRead);
    }

    [Fact]
    public void A_read_that_stopped_early_is_continued_on_the_next_walk_even_when_the_file_is_unchanged()
    {
        using var data = TestPaths.CreateDisposableDirectory("antigravity-resume-data");
        using var conversations = TestPaths.CreateDisposableDirectory("antigravity-resume");
        using var claude = TestPaths.CreateDisposableDirectory("antigravity-resume-claude");
        using var codex = TestPaths.CreateDisposableDirectory("antigravity-resume-codex");
        var path = NewDatabase(conversations, "c.db");
        // The step is not written yet.
        AntigravityFixtures.AddCall(path, 0, AntigravityFixtures.Generation("m", 7, 0, 1, 1, step: 3));
        var store = new StatsStore(data);

        var first = Run(store, claude, codex, conversations);
        var second = Run(store, claude, codex, conversations);

        Assert.Equal(1, first.Gemini.FilesRead);
        Assert.Equal(1, second.Gemini.FilesRead);
        Assert.Empty(store.LoadAll());

        AntigravityFixtures.AddCall(path, 1, AntigravityFixtures.Generation("m", 5, 0, 1, 1, step: 3), 3, Start + 30);
        var third = Run(store, claude, codex, conversations);

        Assert.Equal(1, third.Gemini.FilesRead);
        Assert.Equal(16, store.LoadAll().Sum(record => record.TotalTokens));
    }

    [Fact]
    public void A_conversation_id_in_both_folders_is_counted_once()
    {
        using var data = TestPaths.CreateDisposableDirectory("antigravity-dup-data");
        using var cli = TestPaths.CreateDisposableDirectory("antigravity-dup-cli");
        using var ide = TestPaths.CreateDisposableDirectory("antigravity-dup-ide");
        using var claude = TestPaths.CreateDisposableDirectory("antigravity-dup-claude");
        using var codex = TestPaths.CreateDisposableDirectory("antigravity-dup-codex");
        foreach (var folder in new[] { cli.Path, ide.Path })
        {
            var path = NewDatabase(folder, "same-id.db");
            AntigravityFixtures.AddCall(path, 0, AntigravityFixtures.Generation("m", 10, 0, 1, 1, step: 0), 0, Start + 5);
        }

        var store = new StatsStore(data);
        var result = Run(store, claude, codex, cli, ide);

        Assert.Equal(1, result.Gemini.FilesSeen);
        Assert.Equal(12, store.LoadAll().Sum(record => record.TotalTokens));
    }

    [Fact]
    public void A_linked_database_file_is_not_followed()
    {
        using var data = TestPaths.CreateDisposableDirectory("antigravity-link-data");
        using var conversations = TestPaths.CreateDisposableDirectory("antigravity-link");
        using var elsewhere = TestPaths.CreateDisposableDirectory("antigravity-link-target");
        using var claude = TestPaths.CreateDisposableDirectory("antigravity-link-claude");
        using var codex = TestPaths.CreateDisposableDirectory("antigravity-link-codex");
        var target = NewDatabase(elsewhere, "real.db");
        AntigravityFixtures.AddCall(target, 0, AntigravityFixtures.Generation("m", 10, 0, 1, 1, step: 0), 0, Start + 5);
        try
        {
            File.CreateSymbolicLink(Path.Combine(conversations, "linked.db"), target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var store = new StatsStore(data);
        var result = Run(store, claude, codex, conversations);

        Assert.Equal(0, result.Gemini.FilesSeen);
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void An_hour_outside_the_day_in_a_stored_row_is_left_out_of_the_estimate()
    {
        var day = new DateOnly(2026, 10, 5);
        var records = new[]
        {
            new StatsRecord("claude", day, "m", "p", 1, 1, 0, 0, Hour: 25),
            new StatsRecord("claude", day, "m", "p", 1, 1, 0, 0, Hour: -1),
        };
        var reset = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        var points = new[] { new HistoryPoint(1, new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), WindowKind.Weekly, 10, reset) };

        Assert.Empty(StatsTokensPerPercent.BuildIntervals(records, points));
    }

    [Fact]
    public void A_percent_reading_that_is_not_a_number_gives_no_figure()
    {
        var intervals = Enumerable.Range(0, 30)
            .Select(i => new PerPercentInterval(double.NaN, new Dictionary<string, long> { ["m"] = 1000 + i }, 0))
            .ToList();

        Assert.Equal(PerPercentStatus.TooFew, StatsTokensPerPercent.Estimate(intervals).Status);
    }
}
