using System.Diagnostics;
using AiUsage.Stats;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>
/// Fixture-based coverage for the token usage index: the two session log parsers, the
/// incremental walk <see cref="StatsIndexer"/> does over them, and the schema guard
/// <see cref="StatsStore"/> keeps against a database a future build wrote.
/// </summary>
public class StatsIndexTests
{
    [Fact]
    public void ClaudeUsageLogParser_reads_the_real_session_line_shape()
    {
        const string line = """
            {"type":"assistant","timestamp":"2026-08-21T05:46:56.649Z","message":{"model":"claude-opus-5","usage":{"input_tokens":2,"cache_creation_input_tokens":56172,"cache_read_input_tokens":0,"output_tokens":365}}}
            """;

        Assert.True(ClaudeUsageLogParser.TryParse(line, out var result));
        Assert.Equal("claude-opus-5", result.Model);
        Assert.Equal(2, result.InputTokens);
        Assert.Equal(365, result.OutputTokens);
        Assert.Equal(56172, result.CacheCreationTokens);
        Assert.Equal(0, result.CacheReadTokens);
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 5, 46, 56, 649, TimeSpan.Zero), result.Timestamp);
    }

    // An assistant line's own "effort" field (medium/high/...) rides along into the same
    // UsageEvent the token counts already come from, so a record can later be grouped by it.
    [Fact]
    public void ClaudeUsageLogParser_reads_the_effort_field_off_the_same_line()
    {
        const string line = """
            {"type":"assistant","timestamp":"2026-08-21T05:46:56.649Z","effort":"medium","message":{"model":"claude-opus-5","usage":{"input_tokens":2,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":365}}}
            """;

        Assert.True(ClaudeUsageLogParser.TryParse(line, out var result));
        Assert.Equal("medium", result.Effort);
    }

    [Fact]
    public void ClaudeUsageLogParser_falls_back_to_perTurnEffort_when_effort_is_missing()
    {
        const string line = """
            {"type":"assistant","timestamp":"2026-08-21T05:46:56.649Z","perTurnEffort":"high","message":{"model":"claude-opus-5","usage":{"input_tokens":2,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":365}}}
            """;

        Assert.True(ClaudeUsageLogParser.TryParse(line, out var result));
        Assert.Equal("high", result.Effort);
    }

    [Fact]
    public void ClaudeUsageLogParser_leaves_effort_empty_when_neither_field_is_present()
    {
        const string line = """
            {"type":"assistant","timestamp":"2026-08-21T05:46:56.649Z","message":{"model":"claude-opus-5","usage":{"input_tokens":2,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":365}}}
            """;

        Assert.True(ClaudeUsageLogParser.TryParse(line, out var result));
        Assert.Equal("", result.Effort);
    }

    // Codex carries its own effort on a "turn_context" line, not on the token_count line itself, so
    // this instance-level parser has to carry it forward the same way it already carries the model.
    [Fact]
    public void CodexUsageLogParser_carries_the_turn_context_effort_onto_following_token_lines()
    {
        const string turnContext = """{"type":"turn_context","payload":{"model":"gpt-5.6-sol","effort":"xhigh"}}""";
        const string tokenCount = """
            {"type":"event_msg","timestamp":"2026-07-29T18:33:50.308Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":10,"cache_write_input_tokens":5,"output_tokens":20,"total_tokens":135}}}}
            """;

        var parser = new CodexUsageLogParser();
        Assert.False(parser.TryParseLine(turnContext, out _));
        Assert.True(parser.TryParseLine(tokenCount, out var result));
        Assert.Equal("xhigh", result.Effort);
    }

    [Fact]
    public void CodexUsageLogParser_reads_reasoning_effort_when_effort_is_absent()
    {
        const string turnContext = """{"type":"turn_context","payload":{"model":"gpt-5.6-sol","reasoning_effort":"low"}}""";
        const string tokenCount = """
            {"type":"event_msg","timestamp":"2026-07-29T18:33:50.308Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":5,"total_tokens":15}}}}
            """;

        var parser = new CodexUsageLogParser();
        parser.TryParseLine(turnContext, out _);
        Assert.True(parser.TryParseLine(tokenCount, out var result));
        Assert.Equal("low", result.Effort);
    }

    [Fact]
    public void CodexUsageLogParser_reseeded_with_a_saved_effort_keeps_reporting_it()
    {
        const string tokenCount = """
            {"type":"event_msg","timestamp":"2026-07-29T18:33:50.308Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":5,"total_tokens":15}}}}
            """;

        var parser = new CodexUsageLogParser(effort: "medium");
        Assert.True(parser.TryParseLine(tokenCount, out var result));
        Assert.Equal("medium", result.Effort);
    }

    [Fact]
    public void CodexUsageLogParser_reads_the_real_token_count_line_shape()
    {
        const string turnContext = """{"type":"turn_context","payload":{"model":"gpt-5.6-sol"}}""";
        const string tokenCount = """
            {"type":"event_msg","timestamp":"2026-07-29T18:33:50.308Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":10,"cache_write_input_tokens":5,"output_tokens":20,"total_tokens":135}}}}
            """;

        var parser = new CodexUsageLogParser();
        Assert.False(parser.TryParseLine(turnContext, out _));
        Assert.True(parser.TryParseLine(tokenCount, out var result));
        Assert.Equal("gpt-5.6-sol", result.Model);
        // 100 total input minus the 10 that were already served from cache, so a summed record
        // never counts that cached share twice.
        Assert.Equal(90, result.InputTokens);
        Assert.Equal(20, result.OutputTokens);
        Assert.Equal(5, result.CacheCreationTokens);
        Assert.Equal(10, result.CacheReadTokens);
    }

    [Fact]
    public void CodexUsageLogParser_does_not_double_count_the_cached_share_of_input_tokens()
    {
        // The real shape from the reported bug: input_tokens already includes the cached_input_tokens
        // share, so the naive sum of every field would double count it (85,809 instead of 51,505).
        const string tokenCount = """
            {"type":"event_msg","timestamp":"2026-09-01T10:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":51066,"cached_input_tokens":34304,"cache_write_input_tokens":0,"output_tokens":439,"total_tokens":51505}}}}
            """;

        var parser = new CodexUsageLogParser();
        Assert.True(parser.TryParseLine(tokenCount, out var result));

        var record = new StatsRecord("codex", new DateOnly(2026, 9, 1), result.Model, "proj",
            result.InputTokens, result.OutputTokens, result.CacheCreationTokens, result.CacheReadTokens);
        Assert.Equal(51505, record.TotalTokens);
    }

    [Fact]
    public void ClaudeUsageLogParser_keeps_its_total_unaffected_since_its_fields_never_overlap()
    {
        const string line = """
            {"type":"assistant","timestamp":"2026-08-21T05:46:56.649Z","message":{"model":"claude-opus-5","usage":{"input_tokens":2,"cache_creation_input_tokens":56172,"cache_read_input_tokens":0,"output_tokens":365}}}
            """;

        Assert.True(ClaudeUsageLogParser.TryParse(line, out var result));
        var record = new StatsRecord("claude", new DateOnly(2026, 8, 21), result.Model, "proj",
            result.InputTokens, result.OutputTokens, result.CacheCreationTokens, result.CacheReadTokens);

        Assert.Equal(2 + 365 + 56172 + 0, record.TotalTokens);
    }

    [Fact]
    public void Both_parsers_skip_a_line_they_do_not_recognise()
    {
        Assert.False(ClaudeUsageLogParser.TryParse("""{"type":"user","message":{"content":"hi"}}""", out _));
        Assert.False(ClaudeUsageLogParser.TryParse("not json at all", out _));

        var codexParser = new CodexUsageLogParser();
        Assert.False(codexParser.TryParseLine("""{"type":"task_started"}""", out _));
        Assert.False(codexParser.TryParseLine("not json at all", out _));
    }

    [Fact]
    public void CodexUsageLogParser_turns_the_cumulative_total_into_a_delta_against_the_previous_line()
    {
        var parser = new CodexUsageLogParser();
        var first = """{"type":"event_msg","timestamp":"2026-07-29T18:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":50}}}}""";
        var second = """{"type":"event_msg","timestamp":"2026-07-29T18:05:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":140,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":70}}}}""";

        Assert.True(parser.TryParseLine(first, out var firstEvent));
        Assert.Equal(100, firstEvent.InputTokens);
        Assert.Equal(50, firstEvent.OutputTokens);

        Assert.True(parser.TryParseLine(second, out var secondEvent));
        Assert.Equal(40, secondEvent.InputTokens); // 140 - 100
        Assert.Equal(20, secondEvent.OutputTokens); // 70 - 50
    }

    [Fact]
    public void Project_name_is_extracted_for_claude_from_the_folder_and_for_codex_from_session_meta()
    {
        var claudePath = Path.Combine("C--Projects-AI-Usage", "01debd7f.jsonl");
        Assert.Equal("C--Projects-AI-Usage", ClaudeUsageLogParser.ExtractProjectFromFilePath(claudePath));

        const string sessionMeta = """{"type":"session_meta","payload":{"cwd":"C:\\Projects\\SampleTool"}}""";
        Assert.Equal("C:\\Projects\\SampleTool", CodexUsageLogParser.TryExtractProjectFromSessionMetaLine(sessionMeta));
        const string withTrailingSeparator = """{"type":"session_meta","payload":{"cwd":"C:\\Projects\\SampleTool\\"}}""";
        Assert.Equal("C:\\Projects\\SampleTool", CodexUsageLogParser.TryExtractProjectFromSessionMetaLine(withTrailingSeparator));
        Assert.Null(CodexUsageLogParser.TryExtractProjectFromSessionMetaLine("""{"type":"turn_context"}"""));
    }

    [Fact]
    public void ClaudeUsageLogParser_reads_the_real_path_from_a_transcript_lines_own_cwd_field()
    {
        const string withCwd = """{"type":"assistant","cwd":"C:\\Projects\\Sample","message":{"model":"modelA"}}""";
        Assert.Equal("C:\\Projects\\Sample", ClaudeUsageLogParser.TryExtractProjectFromLine(withCwd));

        Assert.Null(ClaudeUsageLogParser.TryExtractProjectFromLine("""{"type":"assistant"}"""));
        Assert.Null(ClaudeUsageLogParser.TryExtractProjectFromLine("not json"));
        Assert.Null(ClaudeUsageLogParser.TryExtractProjectFromLine(""));
    }

    [Fact]
    public void Indexer_prefers_the_real_cwd_path_over_the_sanitised_folder_name()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-cwd-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-cwd-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-cwd-data");

        // A folder name that does not match the real path at all - proving the real cwd wins, not
        // just a coincidental match.
        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "SanitisedFolderName")).FullName;
        var pathLine = Path.Combine(projectDir, "with-cwd.jsonl");
        File.WriteAllText(
            pathLine,
            """{"type":"assistant","cwd":"C:\\Projects\\Sample","timestamp":"2026-01-01T00:00:00Z","message":{"model":"modelA","usage":{"input_tokens":10,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0}}}""" + "\n");

        var noCwdDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "subagents")).FullName;
        var pathNoCwd = Path.Combine(noCwdDir, "no-cwd.jsonl");
        File.WriteAllText(pathNoCwd, ClaudeLine("2026-01-01T00:00:00Z", "modelA", 5, 0, 0, 0) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        var all = store.LoadAll();
        Assert.Contains(all, record => record.Project == "C:\\Projects\\Sample");
        Assert.Contains(all, record => record.Project == "subagents");
    }

    [Fact]
    public void Indexer_rereads_a_file_whose_size_or_write_time_changed_but_skips_an_unchanged_one()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-data");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "MyProject")).FullName;
        var filePath = Path.Combine(projectDir, "session.jsonl");
        File.WriteAllText(filePath, ClaudeLine("2026-01-01T00:00:00Z", "modelA", 10, 5, 0, 0) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        var totalAfterFirstRun = store.LoadAll().Sum(r => r.TotalTokens);
        Assert.Equal(15, totalAfterFirstRun);

        // Unchanged file: a second run must not add the same line's tokens again.
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(15, store.LoadAll().Sum(r => r.TotalTokens));

        // Grown file: only the appended line is new, so only its tokens are added.
        File.AppendAllText(filePath, ClaudeLine("2026-01-01T01:00:00Z", "modelA", 3, 2, 0, 0) + "\n");
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(20, store.LoadAll().Sum(r => r.TotalTokens));
    }

    [Fact]
    public void Indexer_resumes_from_the_stored_offset_instead_of_rereading_the_whole_file()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-claude-resume");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-codex-resume");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-data-resume");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "AnotherProject")).FullName;
        var filePath = Path.Combine(projectDir, "session.jsonl");
        File.WriteAllText(filePath, ClaudeLine("2026-01-01T00:00:00Z", "modelA", 10, 0, 0, 0) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        var stateAfterFirstRun = store.GetSourceFile(filePath);
        Assert.NotNull(stateAfterFirstRun);
        var lengthAfterFirstRun = new FileInfo(filePath).Length;
        Assert.Equal(lengthAfterFirstRun, stateAfterFirstRun!.Offset);

        File.AppendAllText(filePath, ClaudeLine("2026-01-01T02:00:00Z", "modelA", 7, 0, 0, 0) + "\n");
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        // Only the newly appended 7 tokens were added on top of the first run's 10 - proving the
        // second run started reading at the stored offset rather than from byte zero again.
        Assert.Equal(17, store.LoadAll().Sum(r => r.TotalTokens));
    }

    [Fact]
    public void Indexer_reads_every_configured_claude_root_not_just_one()
    {
        using var claudeRootA = TestPaths.CreateDisposableDirectory("stats-claude-root-a");
        using var claudeRootB = TestPaths.CreateDisposableDirectory("stats-claude-root-b");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-codex-multiroot");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-data-multiroot");

        var projectDirA = Directory.CreateDirectory(Path.Combine(claudeRootA, "ProjectA")).FullName;
        File.WriteAllText(Path.Combine(projectDirA, "session.jsonl"), ClaudeLine("2026-01-01T00:00:00Z", "modelA", 10, 0, 0, 0) + "\n");

        var projectDirB = Directory.CreateDirectory(Path.Combine(claudeRootB, "ProjectB")).FullName;
        File.WriteAllText(Path.Combine(projectDirB, "session.jsonl"), ClaudeLine("2026-01-01T00:00:00Z", "modelB", 7, 0, 0, 0) + "\n");

        var store = new StatsStore(dataDir);
        IReadOnlyList<string> claudeRoots = [claudeRootA.Path, claudeRootB.Path];
        new StatsIndexer(store, claudeRoots, codexRoot).IndexOnce();

        Assert.Equal(17, store.LoadAll().Sum(r => r.TotalTokens));
    }

    [Fact]
    public void Indexer_reports_how_much_of_each_provider_it_actually_read()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-counters-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-counters-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-counters-data");

        var projectDirA = Directory.CreateDirectory(Path.Combine(claudeRoot, "CountersProjectA")).FullName;
        File.WriteAllText(Path.Combine(projectDirA, "session-a.jsonl"),
            ClaudeLine("2026-01-01T00:00:00Z", "modelA", 5, 0, 0, 0) + "\n" +
            "not json at all" + "\n");

        var projectDirB = Directory.CreateDirectory(Path.Combine(claudeRoot, "CountersProjectB")).FullName;
        File.WriteAllText(Path.Combine(projectDirB, "session-b.jsonl"), """{"type":"user","message":{"content":"hi"}}""" + "\n");

        var store = new StatsStore(dataDir);
        var result = new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal(2, result.Claude.FilesSeen);
        Assert.Equal(2, result.Claude.FilesRead);
        Assert.Equal(1, result.Claude.LinesParsed);
        Assert.Equal(2, result.Claude.LinesSkipped);
        Assert.Equal(0, result.Codex.FilesSeen);
        Assert.Equal(0, result.Codex.FilesRead);
    }

    [Fact]
    public void StatsStore_refuses_a_database_a_future_build_wrote()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-future-schema");
        var dbPath = Path.Combine(dataDir, "stats.db");

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_version (version INTEGER NOT NULL); INSERT INTO schema_version (version) VALUES (999);";
            command.ExecuteNonQuery();
        }

        var store = new StatsStore(dataDir);
        Assert.Empty(store.LoadAll());
        Assert.False(store.IsUsable);

        store.AddDelta([new StatsRecord("claude", new DateOnly(2026, 1, 1), "modelA", "proj", 1, 1, 0, 0)]);
        Assert.Empty(store.LoadAll());
    }

    // Buckets by the local day and hour, not the UTC ones the raw timestamp carries - the same
    // shape MainViewModel already uses for its own weekly total, so a chart reader in a timezone
    // ahead of or behind UTC sees the hour and day it actually worked in, not a shifted one.
    [Fact]
    public void A_session_line_lands_in_the_hour_and_day_bucket_its_own_local_timestamp_names()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-hour-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-hour-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-hour-data");

        var utcTimestamp = new DateTimeOffset(2026, 3, 16, 14, 30, 0, TimeSpan.Zero);
        var expectedLocal = utcTimestamp.ToLocalTime();

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "HourProject")).FullName;
        File.WriteAllText(Path.Combine(projectDir, "session.jsonl"), ClaudeLine("2026-03-16T14:30:00Z", "modelA", 9, 0, 0, 0) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        var record = Assert.Single(store.LoadAll());
        Assert.Equal(expectedLocal.Hour, record.Hour);
        Assert.Equal(DateOnly.FromDateTime(expectedLocal.DateTime), record.Day);
    }

    [Fact]
    public void Opening_a_database_an_older_build_wrote_clears_markers_and_rebuilds_the_same_total()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-migrate-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-migrate-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-migrate-data");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "MigrateProject")).FullName;
        var filePath = Path.Combine(projectDir, "session.jsonl");
        File.WriteAllText(filePath, ClaudeLine("2026-01-01T00:00:00Z", "modelA", 10, 5, 0, 0) + "\n");
        var fileInfo = new FileInfo(filePath);
        const long oldTotal = 15; // 10 input + 5 output, the same shape the v1 row below carries.

        var dbPath = Path.Combine(dataDir, "stats.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
        {
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                // The v1 shape: no hour column, a marker that already claims this file
                // fully read - so a plain re-run without a schema bump would skip it and find nothing
                // new, which is exactly why the migration has to drop the marker, not just the rows.
                command.CommandText = """
                    CREATE TABLE schema_version (version INTEGER NOT NULL);
                    INSERT INTO schema_version (version) VALUES (1);
                    CREATE TABLE usage (
                        provider TEXT NOT NULL, day TEXT NOT NULL, model TEXT NOT NULL, project TEXT NOT NULL,
                        input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0,
                        cache_creation_tokens INTEGER NOT NULL DEFAULT 0, cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (provider, day, model, project)
                    );
                    INSERT INTO usage (provider, day, model, project, input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens)
                    VALUES ('claude', '2026-01-01', 'modelA', 'MigrateProject', 10, 5, 0, 0);
                    CREATE TABLE source_file (
                        path TEXT PRIMARY KEY, provider TEXT NOT NULL, offset INTEGER NOT NULL, size INTEGER NOT NULL,
                        write_time_utc TEXT NOT NULL, current_model TEXT NOT NULL DEFAULT '',
                        cumulative_input_tokens INTEGER NOT NULL DEFAULT 0, cumulative_output_tokens INTEGER NOT NULL DEFAULT 0,
                        cumulative_cache_creation_tokens INTEGER NOT NULL DEFAULT 0, cumulative_cache_read_tokens INTEGER NOT NULL DEFAULT 0
                    );
                    """;
                command.ExecuteNonQuery();
            }

            using var insertMarker = connection.CreateCommand();
            insertMarker.CommandText = """
                INSERT INTO source_file (path, provider, offset, size, write_time_utc, current_model,
                    cumulative_input_tokens, cumulative_output_tokens, cumulative_cache_creation_tokens, cumulative_cache_read_tokens)
                VALUES ($path, 'claude', $size, $size, $writeTime, 'modelA', 10, 5, 0, 0)
                """;
            insertMarker.Parameters.AddWithValue("$path", filePath);
            insertMarker.Parameters.AddWithValue("$size", fileInfo.Length);
            insertMarker.Parameters.AddWithValue("$writeTime", fileInfo.LastWriteTimeUtc.ToString("O"));
            insertMarker.ExecuteNonQuery();
        }

        var store = new StatsStore(dataDir);
        Assert.True(store.IsUsable);
        Assert.Null(store.GetSourceFile(filePath));
        Assert.Empty(store.LoadAll());

        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal(oldTotal, store.LoadAll().Sum(record => record.TotalTokens));
    }

    [Fact]
    public void StatsRecord_has_no_field_that_could_hold_message_text()
    {
        var allowedFieldNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Provider", "Day", "Hour", "Model", "Project", "Effort", "InputTokens", "OutputTokens", "CacheCreationTokens", "CacheReadTokens",
        };

        var actualFieldNames = typeof(StatsRecord).GetProperties().Select(property => property.Name)
            .Where(name => name != "TotalTokens" && name != "EqualityContract");

        Assert.All(actualFieldNames, name => Assert.Contains(name, allowedFieldNames));
    }

    [Fact]
    public void ApplyIndexResult_writes_the_delta_and_the_marker_together_or_not_at_all()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-atomic");
        var store = new StatsStore(dataDir, busyTimeoutMs: 100);
        store.LoadAll(); // primes the schema so the raw connection below sees the real tables

        var dbPath = Path.Combine(dataDir, "stats.db");
        using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        blocker.Open();
        using var blockingTransaction = blocker.BeginTransaction();
        using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = blockingTransaction;
            lockCommand.CommandText =
                "INSERT INTO source_file (path, provider, offset, size, write_time_utc) VALUES ('lock-holder', 'x', 0, 0, '2026-01-01T00:00:00Z')";
            lockCommand.ExecuteNonQuery(); // takes the database's write lock and holds it open, uncommitted
        }

        // The store's own writer waits out its busy timeout against the held lock, then gives up -
        // exactly the abort a crash mid-write would cause, forced here instead of waiting for one.
        store.ApplyIndexResult(
            [new StatsRecord("claude", new DateOnly(2026, 1, 1), "modelA", "proj", 5, 5, 0, 0)],
            new StatsSourceFileState("file-a", "claude", 100, 100, DateTime.UtcNow));

        blockingTransaction.Rollback();

        // Neither half of the write landed - a restart that reruns this file sees no marker at all,
        // and reads it from the beginning rather than skipping a range it never actually counted.
        Assert.Empty(store.LoadAll());
        Assert.Null(store.GetSourceFile("file-a"));
    }

    [Fact]
    public void TryGetSourceFile_tells_an_unreadable_index_apart_from_a_missing_row()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-unreadable");
        var store = new StatsStore(dataDir, busyTimeoutMs: 100);
        store.SetSourceFile(new StatsSourceFileState("file-a", "claude", 100, 100, DateTime.UtcNow));

        Assert.True(store.TryGetSourceFile("file-b", out var missing));
        Assert.Null(missing);

        var dbPath = Path.Combine(dataDir, "stats.db");
        using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        blocker.Open();
        using (var exclusive = blocker.CreateCommand())
        {
            exclusive.CommandText = "BEGIN EXCLUSIVE";
            exclusive.ExecuteNonQuery(); // no reader gets in until this ends
        }

        // A read that fails is reported as such, never as "no row" - the indexer would otherwise
        // take a known file for a new one and count all of it again.
        Assert.False(store.TryGetSourceFile("file-a", out _));
        Assert.Empty(store.LoadSourceFiles());

        using (var release = blocker.CreateCommand())
        {
            release.CommandText = "ROLLBACK";
            release.ExecuteNonQuery();
        }

        Assert.True(store.TryGetSourceFile("file-a", out var found));
        Assert.Equal(100, found!.Offset);
        Assert.Equal(["file-a"], store.LoadSourceFiles().Keys);
    }

    [Fact]
    public async Task Indexer_counts_a_file_that_grows_while_being_read_exactly_once_per_line()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-growing-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-growing-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-growing-data");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "GrowingProject")).FullName;
        var filePath = Path.Combine(projectDir, "session.jsonl");

        const int initialLineCount = 400;
        using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(stream))
        {
            for (var i = 0; i < initialLineCount; i++)
                writer.Write(ClaudeLine($"2026-01-01T00:{i % 60:00}:00Z", "modelA", 1, 0, 0, 0) + "\n");
        }

        var store = new StatsStore(dataDir);

        // Appends more lines while the indexer's own walk is in flight against the very same file -
        // a real, not simulated, race between the writer and the reader.
        var appendedLineCount = 0;
        var appendTask = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                using var appendStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var appendWriter = new StreamWriter(appendStream);
                appendWriter.Write(ClaudeLine($"2026-01-01T01:{i % 60:00}:00Z", "modelA", 1, 0, 0, 0) + "\n");
                Interlocked.Increment(ref appendedLineCount);
            }
        });

        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        await appendTask;
        // Mops up whatever the first, concurrent run's read window did not yet reach.
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        // Unchanged afterwards: a third run must add nothing further.
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal(initialLineCount + appendedLineCount, store.LoadAll().Sum(r => r.TotalTokens));
    }

    [Fact]
    public void Indexer_withholds_a_record_until_its_line_is_written_completely()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-partial-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-partial-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-partial-data");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "PartialProject")).FullName;
        var filePath = Path.Combine(projectDir, "session.jsonl");

        // Written without its trailing newline yet, as if the writer paused mid line.
        File.WriteAllText(filePath, ClaudeLine("2026-01-01T00:00:00Z", "modelA", 9, 0, 0, 0));

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Empty(store.LoadAll());

        // The writer finishes the line.
        File.AppendAllText(filePath, "\n");
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(9, store.LoadAll().Sum(r => r.TotalTokens));

        // Unchanged afterwards: the now-complete line is never counted a second time.
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(9, store.LoadAll().Sum(r => r.TotalTokens));
    }

    [Fact]
    public void Indexer_reads_an_unterminated_trailing_line_once_the_file_has_settled()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-settle-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-settle-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-settle-data");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "SettleProject")).FullName;
        var filePath = Path.Combine(projectDir, "session.jsonl");

        // Written without its trailing newline yet, as if the writer paused mid line, and freshly
        // written so it is still inside the 60 s settling window.
        File.WriteAllText(filePath, ClaudeLine("2026-01-01T00:00:00Z", "modelA", 9, 0, 0, 0));

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Empty(store.LoadAll()); // still settling - the line is correctly left alone

        // The write time moves two minutes into the past with the size unchanged, standing in for
        // the writer having gone quiet rather than merely pausing mid line.
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-2));
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(9, store.LoadAll().Sum(r => r.TotalTokens));

        // Unchanged afterwards - the now fully-consumed file is skipped again, never read twice.
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(9, store.LoadAll().Sum(r => r.TotalTokens));
    }

    [Fact]
    public void Indexer_skips_a_settled_file_with_no_trailing_remainder_on_the_next_pass()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-settle-complete-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-settle-complete-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-settle-complete-data");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "SettleCompleteProject")).FullName;
        var filePath = Path.Combine(projectDir, "session.jsonl");
        File.WriteAllText(filePath, ClaudeLine("2026-01-01T00:00:00Z", "modelA", 6, 0, 0, 0) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(6, store.LoadAll().Sum(r => r.TotalTokens));

        // Write time moves into the past with no new content appended - a file with no trailing
        // remainder must never be read a second time just because time passed.
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-2));
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(6, store.LoadAll().Sum(r => r.TotalTokens));
    }

    [Fact]
    public void Indexer_does_not_read_a_replaced_shorter_file_again_and_only_moves_its_state()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-shrink-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-shrink-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-shrink-data");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "ShrinkProject")).FullName;
        var filePath = Path.Combine(projectDir, "session.jsonl");
        File.WriteAllText(filePath,
            ClaudeLine("2026-01-01T00:00:00Z", "modelA", 20, 0, 0, 0) + "\n" +
            ClaudeLine("2026-01-01T00:01:00Z", "modelA", 5, 0, 0, 0) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(25, store.LoadAll().Sum(r => r.TotalTokens));

        // The file ends up shorter than what was read before (a rewrite, a rotation). Whatever it
        // held is already counted and cannot be told apart from new lines, so it is not read again;
        // only the remembered size moves, and a later append is read from the new end.
        File.WriteAllText(filePath, ClaudeLine("2026-01-01T02:00:00Z", "modelB", 3, 0, 0, 0) + "\n");
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal(25, store.LoadAll().Sum(r => r.TotalTokens));
        Assert.DoesNotContain(store.LoadAll(), r => r.Model == "modelB");

        var state = store.GetSourceFile(filePath);
        Assert.NotNull(state);
        Assert.Equal(new FileInfo(filePath).Length, state!.Size);
        Assert.Equal(new FileInfo(filePath).Length, state.Offset);

        File.AppendAllText(filePath, ClaudeLine("2026-01-01T03:00:00Z", "modelC", 7, 0, 0, 0) + "\n");
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal(32, store.LoadAll().Sum(r => r.TotalTokens));
    }

    [Fact]
    public void Indexer_second_run_over_an_unmodified_file_adds_nothing()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-noop-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-noop-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-noop-data");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "NoopProject")).FullName;
        var filePath = Path.Combine(projectDir, "session.jsonl");
        File.WriteAllText(filePath,
            ClaudeLine("2026-01-01T00:00:00Z", "modelA", 4, 1, 0, 0) + "\n" +
            ClaudeLine("2026-01-01T00:02:00Z", "modelA", 2, 1, 0, 0) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(8, store.LoadAll().Sum(r => r.TotalTokens));

        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal(8, store.LoadAll().Sum(r => r.TotalTokens));
    }

    [Fact]
    public void EnumerateNewestFirst_skips_an_unreadable_subdirectory_but_keeps_the_rest()
    {
        using var root = TestPaths.CreateDisposableDirectory("stats-unreadable-root");
        var readableFile = Path.Combine(root, "readable.jsonl");
        File.WriteAllText(readableFile, "{}");

        var blockedDir = Directory.CreateDirectory(Path.Combine(root, "blocked")).FullName;
        File.WriteAllText(Path.Combine(blockedDir, "hidden.jsonl"), "{}");

        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        RunIcacls($"\"{blockedDir}\" /deny \"{identity}:(RX)\"");
        try
        {
            var found = StatsIndexer.EnumerateNewestFirst(root, "*.jsonl");
            Assert.Contains(found, path => path == readableFile);
        }
        finally
        {
            RunIcacls($"\"{blockedDir}\" /grant \"{identity}:(RX)\"");
        }
    }

    [Fact]
    public void Indexer_skips_a_record_past_the_size_limit_and_keeps_reading_after_it()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-bigline-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-bigline-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-bigline-data");
        using var logDir = TestPaths.CreateDisposableDirectory("stats-bigline-log");
        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "MyProject")).FullName;
        var filePath = Path.Combine(projectDir, "session.jsonl");
        File.WriteAllText(
            filePath,
            ClaudeLine("2026-01-01T00:00:00Z", "modelA", 10, 5, 0, 0) + "\n"
            + new string('x', 20000) + "\n"
            + ClaudeLine("2026-01-01T01:00:00Z", "modelA", 3, 2, 0, 0) + "\n");
        var log = new AiUsage.Services.LogService(logDir);
        var store = new StatsStore(dataDir);
        StatsIndexer Indexer() => new(store, claudeRoot, codexRoot) { MaxRecordBytes = 2000, Log = log };

        Indexer().IndexOnce();

        Assert.Equal(20, store.LoadAll().Sum(r => r.TotalTokens));
        Assert.Single(File.ReadAllLines(log.CurrentFile));

        // The offset moved past the skipped record: a second run adds nothing and logs nothing new.
        Indexer().IndexOnce();
        Assert.Equal(20, store.LoadAll().Sum(r => r.TotalTokens));
        Assert.Single(File.ReadAllLines(log.CurrentFile));
    }

    [Fact]
    public void Indexer_skips_an_oversized_last_record_that_has_no_newline_once_the_file_has_settled()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-bigtail-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-bigtail-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-bigtail-data");
        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "MyProject")).FullName;
        File.WriteAllText(
            Path.Combine(projectDir, "session.jsonl"),
            ClaudeLine("2026-01-01T00:00:00Z", "modelA", 10, 5, 0, 0) + "\n" + new string('x', 20000));
        var store = new StatsStore(dataDir);

        new StatsIndexer(store, claudeRoot, codexRoot, () => DateTime.UtcNow.AddHours(1)) { MaxRecordBytes = 2000 }.IndexOnce();

        Assert.Equal(15, store.LoadAll().Sum(r => r.TotalTokens));
    }

    [Fact]
    public void EnumerateNewestFirst_does_not_follow_a_junction_back_into_its_own_tree()
    {
        using var root = TestPaths.CreateDisposableDirectory("stats-junction-root");
        var sub = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
        File.WriteAllText(Path.Combine(sub, "one.jsonl"), "{}");
        var junction = Path.Combine(sub, "loop");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{root}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using (var mklink = System.Diagnostics.Process.Start(start)!)
        {
            mklink.StandardOutput.ReadToEnd();
            mklink.WaitForExit();
        }

        Assert.True(Directory.Exists(junction), "the test could not create a junction");
        try
        {
            var found = StatsIndexer.EnumerateNewestFirst(root, "*.jsonl", out var skipped);

            Assert.Single(found);
            Assert.Equal(0, skipped);
        }
        finally
        {
            // Removes the link only, never what it points at.
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void EnumerateNewestFirst_reports_how_many_subdirectories_it_could_not_list()
    {
        using var root = TestPaths.CreateDisposableDirectory("stats-unreadable-count-root");
        var blockedDir = Directory.CreateDirectory(Path.Combine(root, "blocked")).FullName;
        File.WriteAllText(Path.Combine(blockedDir, "hidden.jsonl"), "{}");

        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        RunIcacls($"\"{blockedDir}\" /deny \"{identity}:(RX)\"");
        try
        {
            StatsIndexer.EnumerateNewestFirst(root, "*.jsonl", out var foldersSkipped);
            Assert.Equal(1, foldersSkipped);
        }
        finally
        {
            RunIcacls($"\"{blockedDir}\" /grant \"{identity}:(RX)\"");
        }
    }

    [Fact]
    public void Indexer_reports_an_unreadable_claude_subdirectory_in_its_result()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-unreadable-indexer-root");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-unreadable-indexer-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-unreadable-indexer-data");

        var blockedDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "blocked")).FullName;
        File.WriteAllText(Path.Combine(blockedDir, "hidden.jsonl"), "{}");

        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        RunIcacls($"\"{blockedDir}\" /deny \"{identity}:(RX)\"");
        try
        {
            var store = new StatsStore(dataDir);
            var result = new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
            Assert.Equal(1, result.Claude.FoldersSkipped);
        }
        finally
        {
            RunIcacls($"\"{blockedDir}\" /grant \"{identity}:(RX)\"");
        }
    }

    [Fact]
    public void LoadAll_skips_a_row_whose_day_value_is_corrupted_and_keeps_the_rest()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-corrupt-day");
        var store = new StatsStore(dataDir);
        store.AddDelta([new StatsRecord("claude", new DateOnly(2026, 1, 1), "modelA", "proj", 3, 0, 0, 0)]);

        var dbPath = Path.Combine(dataDir, "stats.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO usage (provider, day, model, project, input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens) " +
                "VALUES ('claude', 'not-a-date', 'modelB', 'proj', 9, 0, 0, 0)";
            command.ExecuteNonQuery();
        }

        var results = store.LoadAll();

        Assert.Single(results);
        Assert.Equal(3, results[0].TotalTokens);
    }

    [Fact]
    public void A_database_that_fails_to_initialise_does_not_leave_its_file_held_open()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-bad-schema");
        var dbPath = Path.Combine(dataDir, "stats.db");
        File.WriteAllText(dbPath, new string('x', 4096));
        var store = new StatsStore(dataDir);

        store.AddDelta([new StatsRecord("claude", new DateOnly(2026, 1, 1), "modelA", "proj", 3, 0, 0, 0)]);

        // Pooled connections are released here; one that was never disposed still holds the file.
        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);
        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void SumTokensSince_adds_only_the_rows_on_or_after_the_cutoff_day()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-sum-since");
        var store = new StatsStore(dataDir);
        store.AddDelta([
            // Week 1 - before the cutoff, must not count.
            new StatsRecord("claude", new DateOnly(2026, 1, 1), "modelA", "proj", 10, 0, 0, 0),
            // Week 2 - exactly on the cutoff day, must count.
            new StatsRecord("claude", new DateOnly(2026, 1, 8), "modelA", "proj", 20, 0, 0, 0),
            // Week 3 - after the cutoff, must count.
            new StatsRecord("claude", new DateOnly(2026, 1, 15), "modelA", "proj", 30, 0, 0, 0),
            // A different provider on a counted day must never bleed into claude's own sum.
            new StatsRecord("codex", new DateOnly(2026, 1, 15), "modelB", "proj", 99, 0, 0, 0),
        ]);

        var sum = store.SumTokensSince("claude", new DateOnly(2026, 1, 8));

        Assert.Equal(20 + 30, sum);
    }

    [Fact]
    public void SumTokensSince_is_zero_for_a_provider_with_no_rows_at_all()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-sum-since-empty");
        var store = new StatsStore(dataDir);

        Assert.Equal(0, store.SumTokensSince("claude", new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void ClaudeUsageLogParser_does_not_throw_when_a_lines_root_is_an_array()
    {
        Assert.False(ClaudeUsageLogParser.TryParse("[1,2,3]", out _));
    }

    [Fact]
    public void CodexUsageLogParser_does_not_throw_when_a_lines_root_is_an_array()
    {
        var parser = new CodexUsageLogParser();
        Assert.False(parser.TryParseLine("[1,2,3]", out _));
        Assert.Null(CodexUsageLogParser.TryExtractProjectFromSessionMetaLine("[1,2,3]"));
    }

    [Fact]
    public void Indexer_leaves_no_partial_credit_for_a_file_whose_read_is_cancelled_partway_through()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-cancel-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-cancel-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-cancel-data");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "CancelProject")).FullName;
        var filePath = Path.Combine(projectDir, "session.jsonl");

        const int lineCount = 200_000;
        using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(stream))
        {
            for (var i = 0; i < lineCount; i++)
                writer.Write(ClaudeLine($"2026-01-01T00:{i % 60:00}:00Z", "modelA", 1, 0, 0, 0) + "\n");
        }

        var store = new StatsStore(dataDir);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));

        Assert.Throws<OperationCanceledException>(() =>
            new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce(cancellation.Token));

        // Cancelling mid file earns it nothing this run - no partial count, no moved marker.
        Assert.Null(store.GetSourceFile(filePath));
        Assert.Empty(store.LoadAll());

        // An uncancelled retry reads the whole file, exactly once, from the beginning.
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(lineCount, store.LoadAll().Sum(r => r.TotalTokens));
    }

    private static void RunIcacls(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("icacls", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"icacls {arguments} failed: {error}");
    }

    [Fact]
    public void Indexer_counts_a_response_written_several_times_only_once()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-dup-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-dup-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-dup-data");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "DupProject")).FullName;
        File.WriteAllText(Path.Combine(projectDir, "session.jsonl"),
            KeyedClaudeLine("msg_1", "req_1", 10) + "\n" +
            KeyedClaudeLine("msg_1", "req_1", 10) + "\n" +
            KeyedClaudeLine("msg_2", "req_2", 7) + "\n" +
            KeyedClaudeLine("msg_1", "req_1", 10) + "\n");

        var store = new StatsStore(dataDir);
        var result = new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal(17, store.LoadAll().Sum(r => r.TotalTokens));
        Assert.Equal(2, result.Claude.LinesParsed);
        Assert.Equal(2, result.Claude.LinesSkipped);
    }

    [Fact]
    public void Indexer_resuming_after_the_first_copy_does_not_count_the_second_copy()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-dup-resume-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-dup-resume-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-dup-resume-data");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "DupResumeProject")).FullName;
        var filePath = Path.Combine(projectDir, "session.jsonl");
        File.WriteAllText(filePath, KeyedClaudeLine("msg_1", "req_1", 10) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();
        Assert.Equal(10, store.LoadAll().Sum(r => r.TotalTokens));
        Assert.Equal("msg_1|req_1", store.GetSourceFile(filePath)!.LastMessageKey);

        File.AppendAllText(filePath,
            KeyedClaudeLine("msg_1", "req_1", 10) + "\n" +
            KeyedClaudeLine("msg_2", "req_2", 4) + "\n");
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal(14, store.LoadAll().Sum(r => r.TotalTokens));
        Assert.Equal("msg_2|req_2", store.GetSourceFile(filePath)!.LastMessageKey);
    }

    private static string CodexTokenLine(string timestamp, long input, long output) =>
        "{\"type\":\"event_msg\",\"timestamp\":\"" + timestamp + "\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":"
        + input + ",\"cached_input_tokens\":0,\"cache_write_input_tokens\":0,\"output_tokens\":" + output + "}}}}";

    [Fact]
    public void Indexer_counts_an_archived_codex_session_once()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-archived-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-archived-codex");
        using var archivedRoot = TestPaths.CreateDisposableDirectory("stats-archived-archive");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-archived-data");

        File.WriteAllText(Path.Combine(archivedRoot, "rollout-2026-01-01T00-00-00-aaa.jsonl"),
            CodexTokenLine("2026-01-01T00:00:00Z", 100, 20) + "\n");

        var store = new StatsStore(dataDir);
        var result = new StatsIndexer(store, claudeRoot, codexRoot, archivedRoot).IndexOnce();
        new StatsIndexer(store, claudeRoot, codexRoot, archivedRoot).IndexOnce();

        Assert.Equal(120, store.LoadAll().Sum(r => r.TotalTokens));
        Assert.Equal(1, result.Codex.FilesSeen);
    }

    [Fact]
    public void Indexer_does_not_count_a_codex_session_twice_after_it_moved_to_the_archive()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-moved-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-moved-codex");
        using var archivedRoot = TestPaths.CreateDisposableDirectory("stats-moved-archive");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-moved-data");

        const string name = "rollout-2026-01-01T00-00-00-bbb.jsonl";
        var original = Path.Combine(codexRoot, name);
        File.WriteAllText(original, CodexTokenLine("2026-01-01T00:00:00Z", 100, 20) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot, archivedRoot).IndexOnce();
        Assert.Equal(120, store.LoadAll().Sum(r => r.TotalTokens));

        var archived = Path.Combine(archivedRoot, name);
        File.Move(original, archived);
        File.AppendAllText(archived, CodexTokenLine("2026-01-01T00:05:00Z", 140, 30) + "\n");
        new StatsIndexer(store, claudeRoot, codexRoot, archivedRoot).IndexOnce();

        // Only the 50 tokens appended after the move are new; the first 120 are not read again.
        Assert.Equal(170, store.LoadAll().Sum(r => r.TotalTokens));
        Assert.Null(store.GetSourceFile(original));
        Assert.NotNull(store.GetSourceFile(archived));
    }

    [Fact]
    public void Indexer_does_not_adopt_a_same_name_codex_file_whose_old_path_still_exists()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-samename-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-samename-codex");
        using var archivedRoot = TestPaths.CreateDisposableDirectory("stats-samename-archive");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-samename-data");

        const string name = "rollout-2026-01-01T00-00-00-ccc.jsonl";
        File.WriteAllText(Path.Combine(codexRoot, name), CodexTokenLine("2026-01-01T00:00:00Z", 100, 20) + "\n");
        File.WriteAllText(Path.Combine(archivedRoot, name), CodexTokenLine("2026-01-02T00:00:00Z", 10, 5) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot, archivedRoot).IndexOnce();

        Assert.Equal(135, store.LoadAll().Sum(r => r.TotalTokens));
        Assert.NotNull(store.GetSourceFile(Path.Combine(codexRoot, name)));
        Assert.NotNull(store.GetSourceFile(Path.Combine(archivedRoot, name)));
    }

    [Fact]
    public void Indexer_never_deduplicates_lines_without_a_complete_key()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-nokey-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-nokey-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-nokey-data");

        var projectDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "NoKeyProject")).FullName;
        File.WriteAllText(Path.Combine(projectDir, "session.jsonl"),
            ClaudeLine("2026-01-01T00:00:00Z", "modelA", 3, 0, 0, 0) + "\n" +
            ClaudeLine("2026-01-01T00:00:00Z", "modelA", 3, 0, 0, 0) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        Assert.Equal(6, store.LoadAll().Sum(r => r.TotalTokens));
    }

    private static void WriteV4Database(string dbPath)
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_version (version INTEGER NOT NULL);
                INSERT INTO schema_version (version) VALUES (4);
                CREATE TABLE usage (
                    provider TEXT NOT NULL, day TEXT NOT NULL, hour INTEGER NOT NULL DEFAULT 0, model TEXT NOT NULL,
                    project TEXT NOT NULL, effort TEXT NOT NULL DEFAULT '',
                    input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0,
                    cache_creation_tokens INTEGER NOT NULL DEFAULT 0, cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (provider, day, hour, model, project, effort)
                );
                INSERT INTO usage (provider, day, hour, model, project, effort, input_tokens)
                VALUES ('claude', '2026-01-01', 1, 'modelA', 'P', '', 100), ('codex', '2026-01-01', 1, 'modelB', 'P', '', 40);
                CREATE TABLE source_file (
                    path TEXT PRIMARY KEY, provider TEXT NOT NULL, offset INTEGER NOT NULL, size INTEGER NOT NULL,
                    write_time_utc TEXT NOT NULL, current_model TEXT NOT NULL DEFAULT '',
                    cumulative_input_tokens INTEGER NOT NULL DEFAULT 0, cumulative_output_tokens INTEGER NOT NULL DEFAULT 0,
                    cumulative_cache_creation_tokens INTEGER NOT NULL DEFAULT 0, cumulative_cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                    current_effort TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO source_file (path, provider, offset, size, write_time_utc)
                VALUES ('c.jsonl', 'claude', 5, 5, '2026-01-01T00:00:00.0000000Z'), ('x.jsonl', 'codex', 6, 6, '2026-01-01T00:00:00.0000000Z');
                """;
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Store_keeps_v4_rows_untouched_when_the_backup_cannot_be_written()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-v4-nobackup");
        var dbPath = Path.Combine(dataDir, "stats.db");
        WriteV4Database(dbPath);
        // A folder under the backup's name makes the copy fail.
        Directory.CreateDirectory(Path.Combine(dataDir, "stats.v4.bak"));

        var store = new StatsStore(dataDir);
        Assert.Empty(store.LoadAll());
        Assert.False(store.IsUsable);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT (SELECT version FROM schema_version) || ':' || (SELECT COUNT(*) FROM usage)";
        Assert.Equal("4:2", query.ExecuteScalar());
    }

    [Fact]
    public void Store_does_not_retry_a_failed_v4_backup_on_every_open()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-v4-nobackup-latch");
        var dbPath = Path.Combine(dataDir, "stats.db");
        WriteV4Database(dbPath);
        var blocker = Path.Combine(dataDir, "stats.v4.bak");
        Directory.CreateDirectory(blocker);

        var store = new StatsStore(dataDir);
        Assert.Empty(store.LoadAll());

        // The copy would now succeed, but this instance already gave up for the session.
        Directory.Delete(blocker);
        Assert.Empty(store.LoadAll());
        Assert.Null(store.GetSourceFile("x.jsonl"));
        Assert.False(store.IsUsable);
        Assert.False(File.Exists(blocker));

        // A fresh instance (the next session) migrates.
        Assert.Single(new StatsStore(dataDir).LoadAll());
    }

    [Fact]
    public void Store_migrates_v4_to_v5_keeping_codex_rows_clearing_claude_and_backing_up()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-v4-migrate");
        var dbPath = Path.Combine(dataDir, "stats.db");

        WriteV4Database(dbPath);

        var store = new StatsStore(dataDir);
        var rows = store.LoadAll();

        Assert.True(store.IsUsable);
        var remaining = Assert.Single(rows);
        Assert.Equal("codex", remaining.Provider);
        Assert.Null(store.GetSourceFile("c.jsonl"));
        Assert.NotNull(store.GetSourceFile("x.jsonl"));
        var backupPath = Path.Combine(dataDir, "stats.v4.bak");
        Assert.True(File.Exists(backupPath));
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            backup.Open();
            using var query = backup.CreateCommand();
            query.CommandText = "SELECT (SELECT version FROM schema_version) || ':' || (SELECT COUNT(*) FROM usage WHERE provider = 'claude')";
            Assert.Equal("4:1", query.ExecuteScalar());
        }

        // A second open (the window next to the indexer) finds the current version and leaves everything alone.
        Assert.Single(new StatsStore(dataDir).LoadAll());
    }

    [Fact]
    public void Store_migrates_v5_to_v6_keeping_every_row_and_collapsing_the_version_rows()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-v5-migrate");
        var dbPath = Path.Combine(dataDir, "stats.db");
        WriteV4Database(dbPath);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // The v5 shape, including the duplicate version row the old open race could leave.
            command.CommandText = """
                ALTER TABLE source_file ADD COLUMN last_message_key TEXT NOT NULL DEFAULT '';
                UPDATE schema_version SET version = 5;
                INSERT INTO schema_version (version) VALUES (5);
                """;
            command.ExecuteNonQuery();
        }

        var store = new StatsStore(dataDir);
        var rows = store.LoadAll();

        Assert.True(store.IsUsable);
        Assert.Equal(["claude", "codex"], rows.Select(row => row.Provider).Order());
        Assert.NotNull(store.GetSourceFile("c.jsonl"));
        Assert.NotNull(store.GetSourceFile("x.jsonl"));
        Assert.False(File.Exists(Path.Combine(dataDir, "stats.v4.bak")));

        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT COUNT(*) || ':' || MIN(version) || ':' || MIN(id) FROM schema_version";
        Assert.Equal($"1:{StatsStore.SchemaVersion}:1", query.ExecuteScalar());

        // The constraint itself: a second version row is refused.
        using var duplicate = check.CreateCommand();
        duplicate.CommandText = "INSERT INTO schema_version (id, version) VALUES (2, 6)";
        Assert.Throws<SqliteException>(() => duplicate.ExecuteNonQuery());
    }

    [Fact]
    public void Store_first_opens_racing_each_other_leave_exactly_one_version_row()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var dataDir = TestPaths.CreateDisposableDirectory("stats-open-race");
            const int openers = 8;
            using var barrier = new Barrier(openers);
            var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var usable = new System.Collections.Concurrent.ConcurrentBag<bool>();

            var threads = Enumerable.Range(0, openers).Select(_ => new Thread(() =>
            {
                try
                {
                    var store = new StatsStore(dataDir);
                    barrier.SignalAndWait();
                    store.LoadAll();
                    usable.Add(store.IsUsable);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            })).ToList();
            threads.ForEach(thread => thread.Start());
            threads.ForEach(thread => thread.Join());

            Assert.Empty(failures);
            Assert.All(usable, isUsable => Assert.True(isUsable));

            using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "stats.db"), Pooling = false }.ToString());
            check.Open();
            using var query = check.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM schema_version";
            Assert.Equal(1L, query.ExecuteScalar());
        }
    }

    private static string KeyedClaudeLine(string messageId, string requestId, long input) =>
        "{\"type\":\"assistant\",\"requestId\":\"" + requestId + "\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"message\":{\"id\":\"" +
        messageId + "\",\"model\":\"modelA\",\"usage\":{\"input_tokens\":" + input +
        ",\"output_tokens\":0,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}";

    private static string ClaudeLine(string timestamp, string model, long input, long output, long cacheCreation, long cacheRead) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp + "\",\"message\":{\"model\":\"" + model +
        "\",\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output +
        ",\"cache_creation_input_tokens\":" + cacheCreation + ",\"cache_read_input_tokens\":" + cacheRead + "}}}";
}
