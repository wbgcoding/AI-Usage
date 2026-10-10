using System.Security.Cryptography;
using System.Text;
using AiUsage.Stats;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>Builds protobuf messages by field number, the way the Antigravity conversation files are encoded.</summary>
internal sealed class ProtoBuilder
{
    private readonly List<byte> _bytes = [];

    public byte[] ToArray() => [.. _bytes];

    public ProtoBuilder Varint(int field, ulong value)
    {
        WriteVarint((ulong)field << 3);
        WriteVarint(value);
        return this;
    }

    public ProtoBuilder Bytes(int field, byte[] value)
    {
        WriteVarint(((ulong)field << 3) | 2);
        WriteVarint((ulong)value.Length);
        _bytes.AddRange(value);
        return this;
    }

    public ProtoBuilder Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    public ProtoBuilder Message(int field, ProtoBuilder inner) => Bytes(field, inner.ToArray());

    public ProtoBuilder Fixed32(int field, uint value)
    {
        WriteVarint(((ulong)field << 3) | 5);
        _bytes.AddRange(BitConverter.GetBytes(value));
        return this;
    }

    private void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        _bytes.Add((byte)value);
    }
}

internal static class AntigravityFixtures
{
    /// <summary>One model call: usage with the proven field numbers, the model name, the step entry,
    /// a repeated copy of the usage and fields standing in for prompt text that must never be read.</summary>
    public static byte[] Generation(
        string? model, long input, long cacheRead, long thinking, long answer, long? step, bool includeUsage = true)
    {
        var usage = new ProtoBuilder()
            .Varint(1, 1016)
            .Varint(2, (ulong)input);
        if (cacheRead > 0)
            usage.Varint(5, (ulong)cacheRead);
        usage.Varint(3, (ulong)(thinking + answer))
            .Varint(6, 24)
            .Text(7, "bot-synthetic")
            .Varint(9, (ulong)thinking)
            .Varint(10, (ulong)answer);

        var call = new ProtoBuilder().Varint(3, 1016);
        if (includeUsage)
            call.Message(4, usage);
        call.Text(9, "SYNTHETIC PROMPT TEXT");
        call.Message(17, new ProtoBuilder().Message(2, usage));
        if (model is not null)
            call.Text(19, model);
        call.Message(20, new ProtoBuilder().Text(1, "cascade_id").Text(2, "synthetic-conversation"));
        if (step is not null)
            call.Message(20, new ProtoBuilder().Text(1, "last_step_index").Text(2, step.Value.ToString()));
        call.Message(20, new ProtoBuilder().Text(1, "used_claude").Text(2, "false"));

        return new ProtoBuilder().Message(1, call).Fixed32(5, 7).ToArray();
    }

    public static byte[] StepMetadata(long seconds) =>
        new ProtoBuilder().Message(1, new ProtoBuilder().Varint(1, (ulong)seconds).Varint(2, 505910100)).ToArray();

    public static byte[] TrajectoryMetadata(long startSeconds, string? workspaceUri)
    {
        var builder = new ProtoBuilder();
        if (workspaceUri is not null)
            builder.Message(1, new ProtoBuilder().Text(1, workspaceUri).Text(2, workspaceUri));
        builder.Message(2, new ProtoBuilder().Varint(1, (ulong)startSeconds).Varint(2, 253568000));
        return builder.ToArray();
    }

    /// <summary>A conversation database with the tool's table layout, in write-ahead mode like the real ones.</summary>
    public static void CreateDatabase(string path, long startSeconds, string? workspaceUri = null)
    {
        using var connection = Open(path);
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "CREATE TABLE gen_metadata (idx integer, data blob, size integer NOT NULL DEFAULT 0, PRIMARY KEY (idx));");
        Execute(connection, "CREATE TABLE steps (idx integer, metadata blob, PRIMARY KEY (idx));");
        Execute(connection, "CREATE TABLE trajectory_metadata_blob (id text DEFAULT 'main', data blob, PRIMARY KEY (id));");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO trajectory_metadata_blob (data) VALUES ($data)";
        command.Parameters.AddWithValue("$data", TrajectoryMetadata(startSeconds, workspaceUri));
        command.ExecuteNonQuery();
    }

    public static void AddCall(string path, long index, byte[] generation, long? stepIndex = null, long? stepSeconds = null)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO gen_metadata (idx, data, size) VALUES ($idx, $data, $size)";
        command.Parameters.AddWithValue("$idx", index);
        command.Parameters.AddWithValue("$data", generation);
        command.Parameters.AddWithValue("$size", generation.Length);
        command.ExecuteNonQuery();
        if (stepIndex is not null && stepSeconds is not null)
        {
            using var step = connection.CreateCommand();
            step.CommandText = "INSERT OR REPLACE INTO steps (idx, metadata) VALUES ($idx, $data)";
            step.Parameters.AddWithValue("$idx", stepIndex.Value);
            step.Parameters.AddWithValue("$data", StepMetadata(stepSeconds.Value));
            step.ExecuteNonQuery();
        }

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

    public static string HashOf(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

public class AntigravityUsageLogParserTests
{
    private const long Start = 1_791_500_000;

    [Fact]
    public void A_call_is_read_from_the_proven_usage_fields()
    {
        var blob = AntigravityFixtures.Generation("gemini-pro-default", input: 4692, cacheRead: 52710, thinking: 409, answer: 49, step: 2);

        Assert.True(AntigravityUsageLogParser.TryParseGeneration(blob, out var call));

        Assert.Equal("gemini-pro-default", call.Model);
        Assert.Equal(4692, call.InputTokens);
        Assert.Equal(52710, call.CacheReadTokens);
        Assert.Equal(458, call.OutputTokens);
        Assert.Equal(2, call.StepIndex);
    }

    [Fact]
    public void A_call_without_cache_reads_has_none_and_one_without_a_step_names_none()
    {
        var blob = AntigravityFixtures.Generation("gemini-3.7-flash", 100, 0, 10, 5, step: null);

        Assert.True(AntigravityUsageLogParser.TryParseGeneration(blob, out var call));

        Assert.Equal(0, call.CacheReadTokens);
        Assert.Equal(-1, call.StepIndex);
        Assert.Equal(15, call.OutputTokens);
    }

    [Fact]
    public void The_repeated_copy_of_the_usage_is_never_added()
    {
        var blob = AntigravityFixtures.Generation("m", 1000, 0, 7, 3, step: 0);

        Assert.True(AntigravityUsageLogParser.TryParseGeneration(blob, out var call));

        Assert.Equal(1000, call.InputTokens);
        Assert.Equal(10, call.OutputTokens);
    }

    [Fact]
    public void Rows_without_a_model_or_without_counts_are_skipped()
    {
        Assert.False(AntigravityUsageLogParser.TryParseGeneration(AntigravityFixtures.Generation(null, 10, 0, 1, 1, null), out _));
        Assert.False(AntigravityUsageLogParser.TryParseGeneration(AntigravityFixtures.Generation("m", 10, 0, 1, 1, null, includeUsage: false), out _));
        Assert.False(AntigravityUsageLogParser.TryParseGeneration(AntigravityFixtures.Generation("m", 0, 0, 0, 0, null), out _));
        Assert.False(AntigravityUsageLogParser.TryParseGeneration([], out _));
    }

    [Fact]
    public void A_truncated_or_garbled_blob_is_skipped_without_throwing()
    {
        var blob = AntigravityFixtures.Generation("gemini-pro-default", 10, 0, 1, 1, 0);

        Assert.False(AntigravityUsageLogParser.TryParseGeneration(blob.AsSpan(0, blob.Length / 2), out _));
        Assert.False(AntigravityUsageLogParser.TryParseGeneration(new byte[] { 0xff, 0xff, 0xff }, out _));
        Assert.False(AntigravityUsageLogParser.TryParseGeneration(new byte[] { 0x0a, 0x7f, 0x01 }, out _));
    }

    [Fact]
    public void Step_time_start_and_workspace_are_read_from_their_blobs()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_791_564_399), AntigravityUsageLogParser.TryParseStepTime(AntigravityFixtures.StepMetadata(1_791_564_399)));
        Assert.Null(AntigravityUsageLogParser.TryParseStepTime([]));

        var trajectory = AntigravityFixtures.TrajectoryMetadata(Start, "file:///C:/Work/Sample%20App");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Start), AntigravityUsageLogParser.TryParseConversationStart(trajectory));
        Assert.Equal("C:\\Work\\Sample App", AntigravityUsageLogParser.TryParseProject(trajectory));
        Assert.Equal("", AntigravityUsageLogParser.TryParseProject(AntigravityFixtures.TrajectoryMetadata(Start, null)));
    }

    [Fact]
    public void A_conversation_file_gives_its_calls_with_step_times_and_resumes_after_the_last_row()
    {
        using var directory = TestPaths.CreateDisposableDirectory("antigravity-parse");
        var path = Path.Combine(directory, "conversation.db");
        AntigravityFixtures.CreateDatabase(path, Start, "file:///C:/Work/App");
        AntigravityFixtures.AddCall(path, 0, AntigravityFixtures.Generation("gemini-pro-default", 100, 0, 5, 5, step: 0), 0, Start + 60);
        AntigravityFixtures.AddCall(path, 1, AntigravityFixtures.Generation(null, 0, 0, 0, 0, step: null));
        AntigravityFixtures.AddCall(path, 2, AntigravityFixtures.Generation("gemini-3.7-flash", 200, 1000, 20, 10, step: 2), 2, Start + 120);

        var first = AntigravityUsageLogParser.ReadConversation(path, 0, DateTime.UtcNow);

        Assert.Equal(2, first.Events.Count);
        Assert.Equal(1, first.RowsSkipped);
        Assert.Equal(3, first.NextIndex);
        Assert.Equal("C:\\Work\\App", first.Project);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Start + 60), first.Events[0].Timestamp);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Start + 120), first.Events[1].Timestamp);
        Assert.Equal(("gemini-3.7-flash", 200L, 30L, 1000L), (first.Events[1].Model, first.Events[1].InputTokens, first.Events[1].OutputTokens, first.Events[1].CacheReadTokens));

        AntigravityFixtures.AddCall(path, 3, AntigravityFixtures.Generation("gemini-pro-default", 5, 0, 1, 1, step: 3), 3, Start + 180);
        var resumed = AntigravityUsageLogParser.ReadConversation(path, first.NextIndex, DateTime.UtcNow);

        var added = Assert.Single(resumed.Events);
        Assert.Equal(3, added.Index);
        Assert.Equal(4, resumed.NextIndex);
    }

    [Fact]
    public void A_call_whose_step_has_no_time_falls_back_to_the_conversation_start()
    {
        using var directory = TestPaths.CreateDisposableDirectory("antigravity-fallback");
        var path = Path.Combine(directory, "conversation.db");
        AntigravityFixtures.CreateDatabase(path, Start);
        AntigravityFixtures.AddCall(path, 0, AntigravityFixtures.Generation("m", 10, 0, 1, 1, step: 9));

        var result = AntigravityUsageLogParser.ReadConversation(path, 0, DateTime.UtcNow.AddHours(-1));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Start), Assert.Single(result.Events).Timestamp);
    }

    [Fact]
    public void Reading_never_changes_the_file()
    {
        using var directory = TestPaths.CreateDisposableDirectory("antigravity-readonly");
        var path = Path.Combine(directory, "conversation.db");
        AntigravityFixtures.CreateDatabase(path, Start);
        AntigravityFixtures.AddCall(path, 0, AntigravityFixtures.Generation("m", 10, 0, 1, 1, step: 0), 0, Start + 5);
        var before = AntigravityFixtures.HashOf(path);
        var sizes = Directory.GetFiles(directory).ToDictionary(file => file, file => new FileInfo(file).Length);

        _ = AntigravityUsageLogParser.ReadConversation(path, 0, DateTime.UtcNow);
        _ = AntigravityUsageLogParser.ReadConversation(path, 0, DateTime.UtcNow);

        Assert.Equal(before, AntigravityFixtures.HashOf(path));
        foreach (var (file, size) in sizes.Where(entry => File.Exists(entry.Key)))
            Assert.Equal(size, new FileInfo(file).Length);
    }

    [Fact]
    public void A_file_that_is_not_a_database_throws_a_sqlite_error_for_the_caller_to_handle()
    {
        using var directory = TestPaths.CreateDisposableDirectory("antigravity-notdb");
        var path = Path.Combine(directory, "conversation.db");
        File.WriteAllText(path, "this is not a database file, only text of some length to be rejected");

        Assert.ThrowsAny<SqliteException>(() => AntigravityUsageLogParser.ReadConversation(path, 0, DateTime.UtcNow));
    }
}

public class AntigravityIndexingTests
{
    private const long Start = 1_791_500_000;

    private static StatsIndexResult Run(StatsStore store, string conversations, string claude, string codex) =>
        new StatsIndexer(store, claude, codex) { AntigravityRoots = [conversations] }.IndexOnce();

    [Fact]
    public void Gemini_calls_are_stored_once_and_a_grown_file_adds_only_its_new_rows()
    {
        using var data = TestPaths.CreateDisposableDirectory("antigravity-index-data");
        using var conversations = TestPaths.CreateDisposableDirectory("antigravity-index-cli");
        using var claude = TestPaths.CreateDisposableDirectory("antigravity-index-claude");
        using var codex = TestPaths.CreateDisposableDirectory("antigravity-index-codex");
        var path = Path.Combine(conversations, "11111111-2222-3333-4444-555555555555.db");
        AntigravityFixtures.CreateDatabase(path, Start, "file:///C:/Work/App");
        AntigravityFixtures.AddCall(path, 0, AntigravityFixtures.Generation("gemini-pro-default", 100, 1000, 20, 10, step: 0), 0, Start + 60);
        AntigravityFixtures.AddCall(path, 1, AntigravityFixtures.Generation("gemini-pro-default", 50, 0, 5, 5, step: 1), 1, Start + 90);
        var store = new StatsStore(data);

        var first = Run(store, conversations, claude, codex);

        Assert.Equal(1, first.Gemini.FilesRead);
        Assert.Equal(2, first.Gemini.LinesParsed);
        var records = store.LoadAll();
        Assert.All(records, record => Assert.Equal("gemini", record.Provider));
        Assert.Equal(150, records.Sum(record => record.InputTokens));
        Assert.Equal(40, records.Sum(record => record.OutputTokens));
        Assert.Equal(1000, records.Sum(record => record.CacheReadTokens));
        Assert.Equal(0, records.Sum(record => record.CacheCreationTokens));
        Assert.All(records, record => Assert.Equal("C:\\Work\\App", record.Project));
        var session = Assert.Single(store.LoadSessions());
        Assert.Equal("11111111-2222-3333-4444-555555555555", session.SessionId);
        Assert.Equal(1190, session.TotalTokens);

        var again = Run(store, conversations, claude, codex);
        Assert.Equal(0, again.Gemini.FilesRead);
        Assert.Equal(1190, store.LoadAll().Sum(record => record.TotalTokens));

        AntigravityFixtures.AddCall(path, 2, AntigravityFixtures.Generation("gemini-pro-default", 7, 0, 2, 1, step: 2), 2, Start + 120);
        var grown = Run(store, conversations, claude, codex);

        Assert.Equal(1, grown.Gemini.FilesRead);
        Assert.Equal(1, grown.Gemini.LinesParsed);
        Assert.Equal(1190 + 10, store.LoadAll().Sum(record => record.TotalTokens));
    }

    [Fact]
    public void The_walk_leaves_the_conversation_file_untouched()
    {
        using var data = TestPaths.CreateDisposableDirectory("antigravity-index-ro-data");
        using var conversations = TestPaths.CreateDisposableDirectory("antigravity-index-ro");
        using var claude = TestPaths.CreateDisposableDirectory("antigravity-index-ro-claude");
        using var codex = TestPaths.CreateDisposableDirectory("antigravity-index-ro-codex");
        var path = Path.Combine(conversations, "c.db");
        AntigravityFixtures.CreateDatabase(path, Start);
        AntigravityFixtures.AddCall(path, 0, AntigravityFixtures.Generation("m", 10, 0, 1, 1, step: 0), 0, Start + 5);
        var before = AntigravityFixtures.HashOf(path);

        Run(new StatsStore(data), conversations, claude, codex);

        Assert.Equal(before, AntigravityFixtures.HashOf(path));
    }

    [Fact]
    public void A_file_the_tool_holds_locked_is_left_for_the_next_walk()
    {
        using var data = TestPaths.CreateDisposableDirectory("antigravity-index-lock-data");
        using var conversations = TestPaths.CreateDisposableDirectory("antigravity-index-lock");
        using var claude = TestPaths.CreateDisposableDirectory("antigravity-index-lock-claude");
        using var codex = TestPaths.CreateDisposableDirectory("antigravity-index-lock-codex");
        var path = Path.Combine(conversations, "c.db");
        AntigravityFixtures.CreateDatabase(path, Start);
        AntigravityFixtures.AddCall(path, 0, AntigravityFixtures.Generation("m", 10, 0, 1, 1, step: 0), 0, Start + 5);
        var store = new StatsStore(data);

        // A reader that must not pass: the same lock a writer in rollback mode takes.
        using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            holder.Open();
            using (var mode = holder.CreateCommand())
            {
                mode.CommandText = "PRAGMA journal_mode=DELETE;";
                mode.ExecuteNonQuery();
            }

            using var begin = holder.CreateCommand();
            begin.CommandText = "BEGIN EXCLUSIVE;";
            begin.ExecuteNonQuery();

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var blocked = Run(store, conversations, claude, codex);

            // The lock wait is short, never the library's default of thirty seconds.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The walk waited {watch.Elapsed} for a locked file.");

            Assert.Equal(0, blocked.Gemini.FilesRead);
            Assert.Empty(store.LoadAll());
        }

        var free = Run(store, conversations, claude, codex);

        Assert.Equal(1, free.Gemini.FilesRead);
        Assert.Equal(12, store.LoadAll().Sum(record => record.TotalTokens));
    }

    [Fact]
    public void A_missing_conversation_folder_and_a_corrupt_file_do_not_stop_the_walk()
    {
        using var data = TestPaths.CreateDisposableDirectory("antigravity-index-bad-data");
        using var conversations = TestPaths.CreateDisposableDirectory("antigravity-index-bad");
        using var claude = TestPaths.CreateDisposableDirectory("antigravity-index-bad-claude");
        using var codex = TestPaths.CreateDisposableDirectory("antigravity-index-bad-codex");
        File.WriteAllText(Path.Combine(conversations, "broken.db"), "not a database file at all, just some text that is long enough");
        var good = Path.Combine(conversations, "good.db");
        AntigravityFixtures.CreateDatabase(good, Start);
        AntigravityFixtures.AddCall(good, 0, AntigravityFixtures.Generation("m", 10, 0, 1, 1, step: 0), 0, Start + 5);
        var store = new StatsStore(data);

        var result = new StatsIndexer(store, claude, codex) { AntigravityRoots = [Path.Combine(conversations, "missing"), conversations] }.IndexOnce();

        Assert.Equal(2, result.Gemini.FilesSeen);
        Assert.Equal(1, result.Gemini.FilesRead);
        Assert.Equal(12, store.LoadAll().Sum(record => record.TotalTokens));
    }

    [Fact]
    public void Existing_rows_of_other_providers_stay_when_gemini_rows_arrive()
    {
        using var data = TestPaths.CreateDisposableDirectory("antigravity-index-keep-data");
        using var conversations = TestPaths.CreateDisposableDirectory("antigravity-index-keep");
        using var claude = TestPaths.CreateDisposableDirectory("antigravity-index-keep-claude");
        using var codex = TestPaths.CreateDisposableDirectory("antigravity-index-keep-codex");
        var store = new StatsStore(data);
        store.AddDelta([new StatsRecord("claude", new DateOnly(2026, 1, 1), "modelA", "p", 11, 22, 0, 0)]);
        var path = Path.Combine(conversations, "c.db");
        AntigravityFixtures.CreateDatabase(path, Start);
        AntigravityFixtures.AddCall(path, 0, AntigravityFixtures.Generation("m", 10, 0, 1, 1, step: 0), 0, Start + 5);

        Run(store, conversations, claude, codex);

        var all = store.LoadAll();
        Assert.Contains(all, record => record.Provider == "claude" && record.InputTokens == 11 && record.OutputTokens == 22);
        Assert.Contains(all, record => record.Provider == "gemini");
    }
}
