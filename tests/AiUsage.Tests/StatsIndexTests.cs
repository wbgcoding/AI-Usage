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

    [Fact]
    public void ClaudeUsageLogParser_reads_a_negative_token_count_as_none()
    {
        const string line = """
            {"type":"assistant","timestamp":"2026-08-21T05:46:56.649Z","message":{"model":"claude-opus-5","usage":{"input_tokens":-5000,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":365}}}
            """;

        Assert.True(ClaudeUsageLogParser.TryParse(line, out var result));
        Assert.Equal(0, result.InputTokens);
        Assert.Equal(365, result.OutputTokens);
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

        // A subagent transcript with no path of its own takes the project of its session's folder.
        var noCwdDir = Directory.CreateDirectory(Path.Combine(claudeRoot, "SanitisedFolderName", "session-1", "subagents")).FullName;
        var pathNoCwd = Path.Combine(noCwdDir, "agent-a1.jsonl");
        File.WriteAllText(pathNoCwd, ClaudeLine("2026-01-01T00:00:00Z", "modelA", 5, 0, 0, 0) + "\n");

        var store = new StatsStore(dataDir);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce();

        var all = store.LoadAll();
        Assert.Contains(all, record => record.Project == "C:\\Projects\\Sample" && !record.Subagent);
        Assert.Contains(all, record => record.Project == "SanitisedFolderName" && record.Subagent && record.InputTokens == 5);
        Assert.DoesNotContain(all, record => record.Project == "subagents");
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
            "Provider", "Day", "Hour", "Model", "Project", "Effort", "Machine", "Subagent",
            "InputTokens", "OutputTokens", "CacheCreationTokens", "CacheReadTokens",
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
    public void Setting_a_damaged_index_aside_keeps_the_newest_earlier_copies()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-damaged-copies");
        var dbPath = Path.Combine(dataDir, "stats.db");
        var earlier = new[] { "20260101-000000", "20260201-000000", "20260301-000000" }
            .Select((stamp, i) =>
            {
                var path = $"{dbPath}.corrupt-{stamp}";
                File.WriteAllText(path, "old");
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 1 + i, 1, 0, 0, 0, DateTimeKind.Utc));
                return path;
            })
            .ToList();
        File.WriteAllBytes(dbPath, Enumerable.Repeat((byte)'x', 8192).ToArray());

        Assert.False(new StatsStore(dataDir).TryLoadSourceFiles(out _));

        var kept = Directory.GetFiles(dataDir, "stats.db.corrupt-*");
        Assert.Equal(3, kept.Length);
        Assert.DoesNotContain(earlier[0], kept);
        Assert.Contains(earlier[1], kept);
        Assert.Contains(earlier[2], kept);
    }

    [Fact]
    public void A_file_that_is_no_database_is_set_aside_and_the_index_starts_over()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-damaged");
        var dbPath = Path.Combine(dataDir, "stats.db");
        File.WriteAllBytes(dbPath, Enumerable.Repeat((byte)'x', 8192).ToArray());
        var logged = new List<string>();
        var store = new StatsStore(dataDir, logError: logged.Add);

        Assert.False(store.TryLoadSourceFiles(out _));
        Assert.Single(Directory.GetFiles(dataDir, "stats.db.corrupt-*"));
        Assert.Contains(logged, line => line.Contains("damaged", StringComparison.Ordinal));

        Assert.True(store.SetSourceFile(new StatsSourceFileState("file-a", "claude", 100, 100, DateTime.UtcNow)));
        Assert.True(store.TryLoadSourceFiles(out var states));
        Assert.Equal(["file-a"], states.Keys);
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
    public void SumTokensSince_counts_from_the_start_hour_of_the_window()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-sum-since");
        var store = new StatsStore(dataDir);
        store.AddDelta([
            // Same day as the window start but an hour earlier: must not count.
            new StatsRecord("claude", new DateOnly(2026, 10, 14), "modelA", "proj", 10, 0, 0, 0, Hour: 15),
            // Exactly the start hour: must count.
            new StatsRecord("claude", new DateOnly(2026, 10, 14), "modelA", "proj", 20, 0, 0, 0, Hour: 16),
            // A later hour of the start day.
            new StatsRecord("claude", new DateOnly(2026, 10, 14), "modelA", "proj", 5, 0, 0, 0, Hour: 23),
            // A later day, early hour: must count whatever the hour.
            new StatsRecord("claude", new DateOnly(2026, 10, 15), "modelA", "proj", 30, 0, 0, 0, Hour: 0),
            // An earlier day: must not count.
            new StatsRecord("claude", new DateOnly(2026, 10, 13), "modelA", "proj", 1000, 0, 0, 0, Hour: 20),
            // A different provider must never bleed into claude's own sum.
            new StatsRecord("codex", new DateOnly(2026, 10, 15), "modelB", "proj", 99, 0, 0, 0, Hour: 3),
        ]);

        var sum = store.SumTokensSince("claude", new DateTime(2026, 10, 14, 16, 0, 0, DateTimeKind.Local));

        Assert.Equal(20 + 5 + 30, sum);
    }

    [Fact]
    public void SumTokensSince_with_a_model_filter_counts_only_matching_models_case_insensitively()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-sum-since-model");
        var store = new StatsStore(dataDir);
        var day = new DateOnly(2026, 10, 15);
        store.AddDelta([
            new StatsRecord("claude", day, "claude-Fable-4", "proj", 10, 0, 0, 0),
            new StatsRecord("claude", day, "claude-opus-4", "proj", 100, 0, 0, 0),
            new StatsRecord("claude", day, "fable_x", "proj", 1000, 0, 0, 0),
        ]);
        var start = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Local);

        Assert.Equal(10 + 100 + 1000, store.SumTokensSince("claude", start));
        Assert.Equal(10 + 1000, store.SumTokensSince("claude", start, "fable"));
        Assert.Equal(0, store.SumTokensSince("claude", start, "sonnet"));
        // LIKE wildcards in a label are literal text, not patterns.
        Assert.Equal(0, store.SumTokensSince("claude", start, "%"));
        Assert.Equal(1000, store.SumTokensSince("claude", start, "fable_"));
    }

    [Fact]
    public void SumTokensSince_is_zero_for_a_provider_with_no_rows_at_all()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-sum-since-empty");
        var store = new StatsStore(dataDir);

        Assert.Equal(0, store.SumTokensSince("claude", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));
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

    // Codex's own rollouts write no cache-write total; requiring one dropped every real line.
    [Fact]
    public void CodexUsageLogParser_counts_a_real_line_without_a_cache_write_total()
    {
        const string first = """
            {"timestamp":"2026-09-01T09:30:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1200,"cached_input_tokens":200,"output_tokens":90,"reasoning_output_tokens":10,"total_tokens":1290}}}}
            """;
        const string second = """
            {"timestamp":"2026-09-01T09:31:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1500,"cached_input_tokens":300,"output_tokens":100,"reasoning_output_tokens":10,"total_tokens":1600}}}}
            """;

        var parser = new CodexUsageLogParser();
        Assert.True(parser.TryParseLine(first, out var one));
        Assert.Equal(1000, one.InputTokens);
        Assert.Equal(90, one.OutputTokens);
        Assert.Equal(200, one.CacheReadTokens);
        Assert.Equal(0, one.CacheCreationTokens);

        Assert.True(parser.TryParseLine(second, out var two));
        Assert.Equal(200, two.InputTokens);
        Assert.Equal(10, two.OutputTokens);
        Assert.Equal(100, two.CacheReadTokens);
    }

    [Fact]
    public void CodexUsageLogParser_keeps_the_previous_cache_write_total_when_a_line_lacks_it()
    {
        const string withField = """
            {"timestamp":"2026-09-01T09:30:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":0,"cache_write_input_tokens":40,"output_tokens":10}}}}
            """;
        const string without = """
            {"timestamp":"2026-09-01T09:31:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":150,"cached_input_tokens":0,"output_tokens":20}}}}
            """;
        const string withAgain = """
            {"timestamp":"2026-09-01T09:32:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":160,"cached_input_tokens":0,"cache_write_input_tokens":45,"output_tokens":25}}}}
            """;

        var parser = new CodexUsageLogParser();
        Assert.True(parser.TryParseLine(withField, out _));
        Assert.True(parser.TryParseLine(without, out var gap));
        Assert.Equal(0, gap.CacheCreationTokens);
        Assert.True(parser.TryParseLine(withAgain, out var after));
        Assert.Equal(5, after.CacheCreationTokens); // 45 - 40, never the whole 45 again
    }

    // Files an older build walked without counting a line (all four totals still zero) are read
    // again once; a file that did count something, and any Claude file, keep their place. The v4 and
    // v5 migration tests above cover an index that holds Codex usage, where nothing is read again.
    [Fact]
    public void Opening_an_older_index_rereads_Codex_files_that_counted_nothing_once()
    {
        using var source = TestPaths.CreateDisposableDirectory("stats-codex-reread-src");
        var store = new StatsStore(source);
        store.SetSourceFile(new StatsSourceFileState("codex-uncounted", "codex", 500, 500, DateTime.UtcNow));
        store.SetSourceFile(new StatsSourceFileState("codex-counted", "codex", 500, 500, DateTime.UtcNow, CumulativeInputTokens: 10));
        store.SetSourceFile(new StatsSourceFileState("claude-file", "claude", 500, 500, DateTime.UtcNow));
        SqliteConnection.ClearAllPools();
        var sourceDb = Path.Combine(source, "stats.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourceDb, Pooling = false }.ToString()))
        {
            connection.Open();
            using var forget = connection.CreateCommand();
            forget.CommandText = "DELETE FROM meta WHERE key = 'codex_uncounted_files_reread'";
            forget.ExecuteNonQuery();
        }

        // A copy at a new path, so this process opens it as an older build's file.
        using var target = TestPaths.CreateDisposableDirectory("stats-codex-reread");
        File.Copy(sourceDb, Path.Combine(target, "stats.db"));
        var reopened = new StatsStore(target);

        Assert.Null(reopened.GetSourceFile("codex-uncounted"));
        Assert.Equal(500, reopened.GetSourceFile("codex-counted")!.Offset);
        Assert.Equal(500, reopened.GetSourceFile("claude-file")!.Offset);

        // Once only: a file walked by this build keeps its place on every later open.
        reopened.SetSourceFile(new StatsSourceFileState("codex-new", "codex", 300, 300, DateTime.UtcNow));
        SqliteConnection.ClearAllPools();
        using var again = TestPaths.CreateDisposableDirectory("stats-codex-reread-again");
        File.Copy(Path.Combine(target, "stats.db"), Path.Combine(again, "stats.db"));
        Assert.Equal(300, new StatsStore(again).GetSourceFile("codex-new")!.Offset);
    }

    [Fact]
    public void A_file_shorter_than_its_read_offset_when_opened_is_not_read_from_the_start()
    {
        var state = new StatsSourceFileState("file-a", "codex", 8000, 8000, DateTime.UtcNow);

        Assert.True(StatsIndexer.IsShorterThanRead(state, 5000));
        Assert.False(StatsIndexer.IsShorterThanRead(state, 8000));
        Assert.False(StatsIndexer.IsShorterThanRead(state, 10000));
        Assert.False(StatsIndexer.IsShorterThanRead(null, 0));
    }

    [Theory]
    [InlineData("'x'")]
    [InlineData("1e300")]
    [InlineData("x'00'")]
    public void A_schema_version_that_is_no_integer_reads_as_unknown_instead_of_throwing(string value)
    {
        using var directory = TestPaths.CreateDisposableDirectory("stats-bad-version");
        var path = Path.Combine(directory, "stats.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = $"CREATE TABLE schema_version (version); INSERT INTO schema_version VALUES ({value});";
            create.ExecuteNonQuery();
        }

        Assert.Null(StatsStore.TryReadSchemaVersion(path));
    }

    private static void SeedIndex(string directory, params string[] sourceFiles)
    {
        var store = new StatsStore(directory);
        foreach (var file in sourceFiles)
            Assert.True(store.SetSourceFile(new StatsSourceFileState(file, "claude", 100, 100, DateTime.UtcNow)));
    }

    private static string[] WeeklyCopyNames(string directory)
    {
        var folder = Path.Combine(directory, "stats-backups");
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder).Select(file => Path.GetFileName(file)).Order().ToArray()
            : [];
    }

    private static void WriteGarbage(string path) =>
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', 8192).ToArray());

    private static readonly DateTimeOffset WeekStart = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_weekly_copy_is_written_every_seven_days_and_the_newest_three_are_kept()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-weekly");
        SeedIndex(dataDir, "file-a");
        var store = new StatsStore(dataDir);

        store.WriteWeeklyCopy(dataDir, WeekStart);
        Assert.Equal(["stats-2026-10-01.db"], WeeklyCopyNames(dataDir));

        store.WriteWeeklyCopy(dataDir, WeekStart.AddDays(3));
        Assert.Single(WeeklyCopyNames(dataDir));

        store.WriteWeeklyCopy(dataDir, WeekStart.AddDays(7));
        Assert.Equal(["stats-2026-10-01.db", "stats-2026-10-08.db"], WeeklyCopyNames(dataDir));

        foreach (var days in new[] { 14, 21, 28 })
            store.WriteWeeklyCopy(dataDir, WeekStart.AddDays(days));
        Assert.Equal(["stats-2026-10-15.db", "stats-2026-10-22.db", "stats-2026-10-29.db"], WeeklyCopyNames(dataDir));
        Assert.All(WeeklyCopyNames(dataDir), name =>
            Assert.NotNull(StatsStore.TryReadSchemaVersion(Path.Combine(dataDir, "stats-backups", name))));
    }

    [Fact]
    public void A_leftover_temporary_copy_is_removed_and_an_unreadable_index_is_not_copied()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-weekly-tmp");
        var folder = Directory.CreateDirectory(Path.Combine(dataDir, "stats-backups")).FullName;
        File.WriteAllText(Path.Combine(folder, "stats-2026-09-24.db.tmp"), "half");
        WriteGarbage(Path.Combine(dataDir, "stats.db"));

        new StatsStore(dataDir).WriteWeeklyCopy(dataDir, WeekStart);

        Assert.Empty(WeeklyCopyNames(dataDir));
    }

    [Fact]
    public void A_damaged_index_is_set_aside_and_the_newest_valid_weekly_copy_is_restored_with_its_rows()
    {
        using var seedDir = TestPaths.CreateDisposableDirectory("stats-restore-seed");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-restore");
        SeedIndex(seedDir, "file-a", "file-b");
        new StatsStore(seedDir).WriteWeeklyCopy(seedDir, WeekStart);
        var folder = Directory.CreateDirectory(Path.Combine(dataDir, "stats-backups")).FullName;
        File.Copy(Path.Combine(seedDir, "stats-backups", "stats-2026-10-01.db"), Path.Combine(folder, "stats-2026-10-01.db"));
        WriteGarbage(Path.Combine(dataDir, "stats.db"));
        var logged = new List<string>();
        var store = new StatsStore(dataDir, logError: logged.Add);

        // The call that meets the damage may fail; a later one finds the restored index.
        store.TryLoadSourceFiles(out _);
        Assert.Single(Directory.GetFiles(dataDir, "stats.db.corrupt-*"));
        Assert.Contains(logged, line => line.Contains("restored from the weekly copy of 2026-10-01", StringComparison.Ordinal));
        Assert.DoesNotContain(logged, line => line.Contains("rebuilt", StringComparison.Ordinal));

        Assert.True(store.TryLoadSourceFiles(out var states));
        Assert.Equal(["file-a", "file-b"], states.Keys.Order());
        Assert.Empty(Directory.GetFiles(dataDir, "*.restore-tmp"));
    }

    [Fact]
    public void A_corrupt_newest_weekly_copy_falls_back_to_the_older_one()
    {
        using var seedDir = TestPaths.CreateDisposableDirectory("stats-restore-fallback-seed");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-restore-fallback");
        SeedIndex(seedDir, "older-file");
        new StatsStore(seedDir).WriteWeeklyCopy(seedDir, WeekStart);
        var folder = Directory.CreateDirectory(Path.Combine(dataDir, "stats-backups")).FullName;
        File.Copy(Path.Combine(seedDir, "stats-backups", "stats-2026-10-01.db"), Path.Combine(folder, "stats-2026-10-01.db"));
        WriteGarbage(Path.Combine(folder, "stats-2026-10-08.db"));
        WriteGarbage(Path.Combine(dataDir, "stats.db"));
        var logged = new List<string>();
        var store = new StatsStore(dataDir, logError: logged.Add);

        store.TryLoadSourceFiles(out _);

        Assert.Contains(logged, line => line.Contains("weekly copy of 2026-10-01", StringComparison.Ordinal));
        Assert.True(store.TryLoadSourceFiles(out var states));
        Assert.Equal(["older-file"], states.Keys.ToArray());
    }

    [Fact]
    public void A_damaged_index_without_a_usable_copy_still_starts_over()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-restore-none");
        var folder = Directory.CreateDirectory(Path.Combine(dataDir, "stats-backups")).FullName;
        WriteGarbage(Path.Combine(folder, "stats-2026-10-01.db"));
        WriteGarbage(Path.Combine(dataDir, "stats.db"));
        var logged = new List<string>();
        var store = new StatsStore(dataDir, logError: logged.Add);

        store.TryLoadSourceFiles(out _);

        Assert.Contains(logged, line => line.Contains("rebuilt from the session logs", StringComparison.Ordinal));
        Assert.True(store.TryLoadSourceFiles(out var states));
        Assert.Empty(states);
    }

    private static void WriteSessions(string claudeRoot, string codexRoot, int claudeCount, int codexCount)
    {
        var project = Directory.CreateDirectory(Path.Combine(claudeRoot, "Project")).FullName;
        for (var i = 0; i < claudeCount; i++)
            File.WriteAllText(Path.Combine(project, $"session-{i}.jsonl"), ClaudeLine("2026-01-01T00:00:00Z", "modelA", 10, 5, 0, 0) + "\n");
        for (var i = 0; i < codexCount; i++)
            File.WriteAllText(Path.Combine(codexRoot, $"rollout-2026-01-01T00-00-00-{i:000}.jsonl"), CodexTokenLine("2026-01-01T00:00:00Z", 100, 20) + "\n");
    }

    [Fact]
    public void A_first_build_of_twenty_five_new_files_reports_progress_up_to_all_of_them()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-progress-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-progress-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-progress-data");
        WriteSessions(claudeRoot, codexRoot, 15, 10);
        var reports = new List<(int Done, int Total)>();

        new StatsIndexer(new StatsStore(dataDir), claudeRoot, codexRoot)
            .IndexOnce(progress: (done, total) => reports.Add((done, total)));

        Assert.Equal(25, reports.Count);
        Assert.All(reports, report => Assert.Equal(25, report.Total));
        Assert.Equal(Enumerable.Range(1, 25), reports.Select(report => report.Done));
    }

    [Fact]
    public void A_small_read_and_a_walk_over_known_files_report_no_progress()
    {
        using var claudeRoot = TestPaths.CreateDisposableDirectory("stats-noprogress-claude");
        using var codexRoot = TestPaths.CreateDisposableDirectory("stats-noprogress-codex");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-noprogress-data");
        var reports = 0;
        var store = new StatsStore(dataDir);

        WriteSessions(claudeRoot, codexRoot, 3, 2);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce(progress: (_, _) => reports++);
        Assert.Equal(0, reports);

        // Many files, but only the first walk finds them new.
        WriteSessions(claudeRoot, codexRoot, 25, 0);
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce(progress: (_, _) => reports++);
        var afterFirst = reports;
        Assert.Equal(22, afterFirst); // 22 of the 25 files are new, the other 3 are known
        new StatsIndexer(store, claudeRoot, codexRoot).IndexOnce(progress: (_, _) => reports++);
        Assert.Equal(afterFirst, reports);
    }

    [Fact]
    public void A_reader_that_opens_the_index_during_a_restore_waits_for_it_instead_of_creating_an_empty_one()
    {
        using var seedDir = TestPaths.CreateDisposableDirectory("stats-restore-race-seed");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-restore-race");
        SeedIndex(seedDir, "file-a", "file-b");
        new StatsStore(seedDir).WriteWeeklyCopy(seedDir, WeekStart);
        var folder = Directory.CreateDirectory(Path.Combine(dataDir, "stats-backups")).FullName;
        File.Copy(Path.Combine(seedDir, "stats-backups", "stats-2026-10-01.db"), Path.Combine(folder, "stats-2026-10-01.db"));
        WriteGarbage(Path.Combine(dataDir, "stats.db"));
        var logged = new List<string>();
        using var inRestore = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var restoring = new StatsStore(dataDir, logError: logged.Add)
        {
            BeforeRestoreMove = () =>
            {
                inRestore.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            },
        };

        var first = Task.Run(() => restoring.TryLoadSourceFiles(out _));
        Assert.True(inRestore.Wait(TimeSpan.FromSeconds(30)));
        var second = Task.Run(() => new StatsStore(dataDir).TryLoadSourceFiles(out _));

        // The reader gets no chance to put an empty index where the restore is about to move one.
        Assert.False(second.Wait(TimeSpan.FromMilliseconds(500)));
        Assert.False(File.Exists(Path.Combine(dataDir, "stats.db")));
        release.Set();
        Assert.True(Task.WaitAll([first, second], TimeSpan.FromSeconds(30)));

        Assert.Contains(logged, line => line.Contains("restored from the weekly copy of 2026-10-01", StringComparison.Ordinal));
        Assert.DoesNotContain(logged, line => line.Contains("rebuilt", StringComparison.Ordinal));
        Assert.True(new StatsStore(dataDir).TryLoadSourceFiles(out var states));
        Assert.Equal(["file-a", "file-b"], states.Keys.Order());
    }

    // An index entry that no longer matches its table row: SQLite's quick check passes it, the full
    // integrity check does not.
    private static void BreakAnIndexEntry(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE junk (a, b); CREATE INDEX junk_a ON junk (a);"
                + " INSERT INTO junk VALUES (1, 100), (2, 200), (3, 300);"
                + " PRAGMA writable_schema = ON;"
                + " UPDATE sqlite_master SET sql = 'CREATE INDEX junk_a ON junk (b)' WHERE name = 'junk_a';"
                + " PRAGMA writable_schema = OFF;";
            command.ExecuteNonQuery();
        }

        using var check = new SqliteConnection(connectionString);
        check.Open();
        using var quick = check.CreateCommand();
        quick.CommandText = "PRAGMA quick_check(1)";
        Assert.Equal("ok", quick.ExecuteScalar());
        using var full = check.CreateCommand();
        full.CommandText = "PRAGMA integrity_check(1)";
        Assert.NotEqual("ok", full.ExecuteScalar());
    }

    [Fact]
    public void A_weekly_copy_that_fails_the_full_check_is_not_restored()
    {
        using var seedDir = TestPaths.CreateDisposableDirectory("stats-restore-full-seed");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-restore-full");
        SeedIndex(seedDir, "file-a");
        new StatsStore(seedDir).WriteWeeklyCopy(seedDir, WeekStart);
        var folder = Directory.CreateDirectory(Path.Combine(dataDir, "stats-backups")).FullName;
        var copy = Path.Combine(folder, "stats-2026-10-01.db");
        File.Copy(Path.Combine(seedDir, "stats-backups", "stats-2026-10-01.db"), copy);
        BreakAnIndexEntry(copy);
        WriteGarbage(Path.Combine(dataDir, "stats.db"));
        var logged = new List<string>();
        var store = new StatsStore(dataDir, logError: logged.Add);

        store.TryLoadSourceFiles(out _);

        Assert.DoesNotContain(logged, line => line.Contains("restored from", StringComparison.Ordinal));
        Assert.Contains(logged, line => line.Contains("rebuilt from the session logs", StringComparison.Ordinal));
    }

    [Fact]
    public void An_index_whose_copy_fails_the_full_check_leaves_no_weekly_copy()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-weekly-full");
        SeedIndex(dataDir, "file-a");
        BreakAnIndexEntry(Path.Combine(dataDir, "stats.db"));
        var logged = new List<string>();

        new StatsStore(dataDir, logError: logged.Add).WriteWeeklyCopy(dataDir, WeekStart);

        Assert.Empty(WeeklyCopyNames(dataDir));
        Assert.Contains(logged, line => line.Contains("check", StringComparison.Ordinal));
    }

    [Fact]
    public void A_weekly_copy_is_restored_once_per_process_and_the_first_damaged_file_is_kept()
    {
        using var seedDir = TestPaths.CreateDisposableDirectory("stats-restore-once-seed");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-restore-once");
        SeedIndex(seedDir, "file-a");
        new StatsStore(seedDir).WriteWeeklyCopy(seedDir, WeekStart);
        var folder = Directory.CreateDirectory(Path.Combine(dataDir, "stats-backups")).FullName;
        File.Copy(Path.Combine(seedDir, "stats-backups", "stats-2026-10-01.db"), Path.Combine(folder, "stats-2026-10-01.db"));
        var dbPath = Path.Combine(dataDir, "stats.db");
        foreach (var stamp in new[] { "20260101-000000", "20260201-000000" })
        {
            var older = $"{dbPath}.corrupt-{stamp}";
            File.WriteAllText(older, "old");
            File.SetLastWriteTimeUtc(older, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        // The newest rows sit in this file; it is the one that must survive every restore round.
        File.WriteAllText(dbPath, new string('x', 8192));
        File.SetLastWriteTimeUtc(dbPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var logged = new List<string>();
        var store = new StatsStore(dataDir, logError: logged.Add);
        store.TryLoadSourceFiles(out _);
        Assert.Contains(logged, line => line.Contains("restored from the weekly copy", StringComparison.Ordinal));
        var first = Directory.GetFiles(dataDir, "stats.db.corrupt-*")
            .Single(path => new FileInfo(path).Length == 8192);

        // The same damage shows up again in the restored file.
        SqliteConnection.ClearAllPools();
        File.WriteAllText(dbPath, new string('y', 8192));
        logged.Clear();
        store.TryLoadSourceFiles(out _);

        Assert.DoesNotContain(logged, line => line.Contains("restored from", StringComparison.Ordinal));
        Assert.Contains(logged, line => line.Contains("rebuilt from the session logs", StringComparison.Ordinal));
        Assert.True(File.Exists(first));
    }

    [Fact]
    public void A_stale_damage_report_never_sets_aside_the_index_that_replaced_the_damaged_file()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-stale-damage");
        var dbPath = Path.Combine(dataDir, "stats.db");
        File.WriteAllText(dbPath, new string('x', 8192));
        var inner = new StatsStore(dataDir);
        var ran = false;
        var outer = new StatsStore(dataDir, logError: _ => { })
        {
            // A second open meets the same damage first, rebuilds the index and writes a row into it.
            BeforeQuarantine = () =>
            {
                if (ran)
                    return;
                ran = true;
                inner.TryLoadSourceFiles(out _);
                inner.TryLoadSourceFiles(out _);
                Assert.True(inner.SetSourceFile(new StatsSourceFileState("file-new", "claude", 100, 100, DateTime.UtcNow)));
            },
        };

        outer.TryLoadSourceFiles(out _);

        Assert.True(ran);
        Assert.True(inner.TryLoadSourceFiles(out var states));
        Assert.Equal(["file-new"], states.Keys);
        Assert.Single(Directory.GetFiles(dataDir, "stats.db.corrupt-*"), path => !path.EndsWith("-wal") && !path.EndsWith("-shm"));
    }

    [Fact]
    public void A_set_aside_that_fails_midway_puts_every_moved_file_back()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-setaside-rollback");
        var dbPath = Path.Combine(dataDir, "stats.db");
        File.WriteAllText(dbPath, new string('x', 8192));
        File.WriteAllText(dbPath + "-wal", "wal");
        File.WriteAllText(dbPath + "-shm", "shm");
        var logged = new List<string>();
        var store = new StatsStore(dataDir, logError: logged.Add);

        // A scanner holding the last sidecar: its move fails after the others went through.
        using (new FileStream(dbPath + "-shm", FileMode.Open, FileAccess.Read, FileShare.Read))
            store.TryLoadSourceFiles(out _);

        Assert.True(File.Exists(dbPath));
        Assert.Equal(8192, new FileInfo(dbPath).Length);
        Assert.Equal("wal", File.ReadAllText(dbPath + "-wal"));
        Assert.Equal("shm", File.ReadAllText(dbPath + "-shm"));
        Assert.Empty(Directory.GetFiles(dataDir, "stats.db.corrupt-*"));
        Assert.Contains(logged, line => line.Contains("could not be set aside", StringComparison.Ordinal));
    }

    // Wipes the root page of a table: the schema page stays intact, so only a full structure check sees it.
    private static void WipeTablePage(string dbPath, string table)
    {
        long page;
        int pageSize;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT rootpage FROM sqlite_master WHERE name = '{table}'";
            page = (long)command.ExecuteScalar()!;
            command.CommandText = "PRAGMA page_size";
            pageSize = (int)(long)command.ExecuteScalar()!;
        }
        using var file = new FileStream(dbPath, FileMode.Open, FileAccess.Write, FileShare.None);
        file.Position = (page - 1) * pageSize;
        file.Write(new byte[pageSize]);
    }

    [Fact]
    public void The_first_open_in_a_process_finds_damage_in_a_table_page_and_sets_the_file_aside()
    {
        using var seedDir = TestPaths.CreateDisposableDirectory("stats-quickcheck-seed");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-quickcheck");
        // Written elsewhere: this process never opened the file at its final path.
        SeedIndex(seedDir, "file-a");
        SqliteConnection.ClearAllPools();
        var dbPath = Path.Combine(dataDir, "stats.db");
        File.Copy(Path.Combine(seedDir, "stats.db"), dbPath);
        WipeTablePage(dbPath, "usage");
        var logged = new List<string>();

        new StatsStore(dataDir, logError: logged.Add).TryLoadSourceFiles(out _);

        Assert.Single(Directory.GetFiles(dataDir, "stats.db.corrupt-*"));
        Assert.Contains(logged, line => line.Contains("rebuilt from the session logs", StringComparison.Ordinal));
    }

    [Fact]
    public void The_structure_check_runs_once_per_file_in_a_process()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-quickcheck-once");
        SeedIndex(dataDir, "file-a");
        var dbPath = Path.Combine(dataDir, "stats.db");
        var store = new StatsStore(dataDir);
        Assert.True(store.TryLoadSourceFiles(out _));
        SqliteConnection.ClearAllPools();
        WipeTablePage(dbPath, "usage");

        // The file was verified at its first open; later opens only pay for what they read.
        Assert.True(store.TryLoadSourceFiles(out var states));

        Assert.Equal(["file-a"], states.Keys);
        Assert.Empty(Directory.GetFiles(dataDir, "stats.db.corrupt-*"));
    }

    [Fact]
    public void A_copy_dated_in_the_future_does_not_block_the_weekly_copy()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-weekly-future");
        SeedIndex(dataDir, "file-a");
        var store = new StatsStore(dataDir);
        store.WriteWeeklyCopy(dataDir, WeekStart.AddDays(120));

        store.WriteWeeklyCopy(dataDir, WeekStart);

        Assert.Equal(["stats-2026-10-01.db", "stats-2027-01-29.db"], WeeklyCopyNames(dataDir));
    }

    // Two weekly copies in dataDir/stats-backups: 2026-09-24 knows file-a, 2026-10-01 knows file-a and file-b.
    private static void SeedTwoWeeklyCopies(string seedDir, string dataDir)
    {
        SeedIndex(seedDir, "file-a");
        var seeder = new StatsStore(seedDir);
        seeder.WriteWeeklyCopy(seedDir, WeekStart.AddDays(-7));
        Assert.True(seeder.SetSourceFile(new StatsSourceFileState("file-b", "claude", 100, 100, DateTime.UtcNow)));
        seeder.WriteWeeklyCopy(seedDir, WeekStart);
        var folder = Directory.CreateDirectory(Path.Combine(dataDir, "stats-backups")).FullName;
        foreach (var file in Directory.GetFiles(Path.Combine(seedDir, "stats-backups")))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
    }

    [Fact]
    public void A_move_that_fails_once_is_retried_instead_of_falling_back_to_an_older_copy()
    {
        using var seedDir = TestPaths.CreateDisposableDirectory("stats-restore-retry-seed");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-restore-retry");
        SeedTwoWeeklyCopies(seedDir, dataDir);
        var dbPath = Path.Combine(dataDir, "stats.db");
        WriteGarbage(dbPath);
        var calls = 0;
        var store = new StatsStore(dataDir)
        {
            BeforeRestoreMove = () =>
            {
                // The first attempt finds the target taken (a scanner holding a file there), the next finds it free.
                if (++calls == 1)
                    File.WriteAllText(dbPath, "");
                else if (File.Exists(dbPath))
                    File.Delete(dbPath);
            },
        };

        store.TryLoadSourceFiles(out _);

        Assert.True(new StatsStore(dataDir).TryLoadSourceFiles(out var states));
        Assert.Equal(["file-a", "file-b"], states.Keys.Order());
    }

    [Fact]
    public void A_locked_newest_weekly_copy_is_waited_for_not_skipped_and_the_damaged_index_stays_put()
    {
        using var seedDir = TestPaths.CreateDisposableDirectory("stats-restore-locked-seed");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-restore-locked");
        SeedTwoWeeklyCopies(seedDir, dataDir);
        var dbPath = Path.Combine(dataDir, "stats.db");
        var newest = Path.Combine(dataDir, "stats-backups", "stats-2026-10-01.db");
        WriteGarbage(dbPath);
        var logged = new List<string>();
        var store = new StatsStore(dataDir, logError: logged.Add);

        // Another process holds the newest copy exclusively for a moment (a scanner, a backup tool).
        using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = newest, Pooling = false }.ToString()))
        {
            holder.Open();
            using (var lockIt = holder.CreateCommand())
            {
                lockIt.CommandText = "PRAGMA locking_mode = EXCLUSIVE; BEGIN EXCLUSIVE;";
                lockIt.ExecuteNonQuery();
            }

            store.TryLoadSourceFiles(out _);
            Assert.DoesNotContain(logged, line => line.Contains("restored from", StringComparison.Ordinal));
            Assert.DoesNotContain(logged, line => line.Contains("rebuilt", StringComparison.Ordinal));
            Assert.Empty(Directory.GetFiles(dataDir, "stats.db.corrupt-*"));
            Assert.True(File.Exists(dbPath));
        }

        // The lock is gone: the same newest copy is used, not the older one and not an empty index.
        store.TryLoadSourceFiles(out _);
        Assert.True(store.TryLoadSourceFiles(out var states));
        Assert.Equal(["file-a", "file-b"], states.Keys.Order());
    }

    [Theory]
    [InlineData("UPDATE schema_version SET version = 99")]
    [InlineData("UPDATE schema_version SET version = 3")]
    [InlineData("DROP TABLE usage")]
    [InlineData("DROP TABLE source_file")]
    [InlineData("DROP TABLE session")]
    public void A_weekly_copy_this_build_cannot_use_is_skipped_for_the_next_older_one(string spoil)
    {
        using var seedDir = TestPaths.CreateDisposableDirectory("stats-restore-schema-seed");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-restore-schema");
        SeedTwoWeeklyCopies(seedDir, dataDir);
        var newest = Path.Combine(dataDir, "stats-backups", "stats-2026-10-01.db");
        using (var spoiler = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = newest, Pooling = false }.ToString()))
        {
            spoiler.Open();
            using var command = spoiler.CreateCommand();
            command.CommandText = spoil;
            command.ExecuteNonQuery();
        }

        WriteGarbage(Path.Combine(dataDir, "stats.db"));
        var logged = new List<string>();
        var store = new StatsStore(dataDir, logError: logged.Add);

        store.TryLoadSourceFiles(out _);

        Assert.Contains(logged, line => line.Contains("weekly copy of 2026-09-24", StringComparison.Ordinal));
        Assert.True(store.TryLoadSourceFiles(out var states));
        Assert.Equal(["file-a"], states.Keys.ToArray());
    }

    [Fact]
    public void A_move_that_keeps_failing_ends_the_restore_without_trying_an_older_copy()
    {
        using var seedDir = TestPaths.CreateDisposableDirectory("stats-restore-stop-seed");
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-restore-stop");
        SeedTwoWeeklyCopies(seedDir, dataDir);
        var dbPath = Path.Combine(dataDir, "stats.db");
        WriteGarbage(dbPath);
        var calls = 0;
        var logged = new List<string>();
        var store = new StatsStore(dataDir, logError: logged.Add)
        {
            BeforeRestoreMove = () =>
            {
                calls++;
                File.WriteAllText(dbPath, "");
            },
        };

        store.TryLoadSourceFiles(out _);

        Assert.Equal(3, calls);
        Assert.DoesNotContain(logged, line => line.Contains("restored from", StringComparison.Ordinal));
    }

    [Fact]
    public void A_locked_leftover_does_not_stop_the_weekly_copy_and_is_logged_as_a_removal_failure()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-weekly-leftover");
        SeedIndex(dataDir, "file-a");
        var folder = Directory.CreateDirectory(Path.Combine(dataDir, "stats-backups")).FullName;
        var leftover = Path.Combine(folder, "stats-2026-09-01.db.tmp");
        File.WriteAllText(leftover, "half");
        var logged = new List<string>();

        using (new FileStream(leftover, FileMode.Open, FileAccess.Read, FileShare.None))
            new StatsStore(dataDir, logError: logged.Add).WriteWeeklyCopy(dataDir, WeekStart);

        Assert.Contains("stats-2026-10-01.db", WeeklyCopyNames(dataDir));
        Assert.Contains(logged, line => line.Contains("could not be removed", StringComparison.Ordinal));
        Assert.DoesNotContain(logged, line => line.Contains("could not be written", StringComparison.Ordinal));
    }

    [Fact]
    public void A_copy_that_cannot_be_deleted_is_logged_as_a_removal_failure_not_a_write_failure()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-weekly-keep");
        SeedIndex(dataDir, "file-a");
        var store = new StatsStore(dataDir);
        foreach (var days in new[] { -21, -14, -7 })
            store.WriteWeeklyCopy(dataDir, WeekStart.AddDays(days));
        var oldest = Path.Combine(dataDir, "stats-backups", "stats-2026-09-10.db");
        var logged = new List<string>();

        using (new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.None))
            new StatsStore(dataDir, logError: logged.Add).WriteWeeklyCopy(dataDir, WeekStart);

        Assert.Contains("stats-2026-10-01.db", WeeklyCopyNames(dataDir));
        Assert.Contains(logged, line => line.Contains("could not be removed", StringComparison.Ordinal));
        Assert.DoesNotContain(logged, line => line.Contains("could not be written", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stepwise_snapshot_lets_a_writer_in_between_the_steps_and_ends_consistent()
    {
        using var dataDir = TestPaths.CreateDisposableDirectory("stats-snapshot-steps");
        var sourcePath = Path.Combine(dataDir, "source.db");
        var sourceConnection = new SqliteConnectionStringBuilder { DataSource = sourcePath, Pooling = false }.ToString();
        using (var seed = new SqliteConnection(sourceConnection))
        {
            seed.Open();
            using var create = seed.CreateCommand();
            create.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, payload BLOB);"
                + " WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 400)"
                + " INSERT INTO t (payload) SELECT zeroblob(2000) FROM n;";
            create.ExecuteNonQuery();
        }

        var steps = 0;
        var writes = 0;
        StatsStore.SnapshotIndex(sourcePath, Path.Combine(dataDir, "copy.db"), pagesPerStep: 10, betweenSteps: () =>
        {
            steps++;
            if (steps != 1)
                return;
            // A writer with no patience at all: a copy holding the source for its whole run would lock it out.
            using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Pooling = false, DefaultTimeout = 0 }.ToString());
            writer.Open();
            using var insert = writer.CreateCommand();
            insert.CommandText = "INSERT INTO t (payload) VALUES (zeroblob(10))";
            insert.ExecuteNonQuery();
            writes++;
        });

        Assert.True(steps > 1);
        Assert.Equal(1, writes);
        var copyConnection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "copy.db"), Pooling = false }.ToString();
        using var copy = new SqliteConnection(copyConnection);
        copy.Open();
        using var check = copy.CreateCommand();
        check.CommandText = "PRAGMA integrity_check(1)";
        Assert.Equal("ok", check.ExecuteScalar());
        using var count = copy.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM t";
        Assert.Equal(401L, count.ExecuteScalar());
    }
}
