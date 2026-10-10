using System.Security.Cryptography;
using AiUsage.Stats;
using Microsoft.Data.Sqlite;

namespace AiUsage.Tests;

/// <summary>Importing another PC's token index: filed per machine id, idempotent, removable, and never
/// touching this PC's own rows.</summary>
public sealed class StatsMachineImportTests : IDisposable
{
    private const string Own = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Foreign = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Third = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    private static readonly DateOnly Day1 = new(2026, 5, 1);
    private static readonly DateOnly Day2 = new(2026, 5, 2);

    private readonly List<DisposableTestDirectory> _directories = [];

    private string Temp()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-import");
        _directories.Add(directory);
        return directory.Path;
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
            IndexPools.ReleaseUnder(directory.Path);
        foreach (var directory in _directories)
            directory.Dispose();
    }

    private static StatsRecord Row(DateOnly day, long tokens, string machine = "", string provider = "claude") =>
        new(provider, day, "modelA", "projA", tokens, 0, 0, 0, Hour: 1, Machine: machine);

    private static StatsSessionDelta Session(string id, long tokens, string machine) =>
        new("claude", id, "projA", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, tokens, 0, 0, 0, 0,
            new Dictionary<string, long> { ["modelA"] = tokens }, machine);

    /// <summary>This PC's index: 1000 tokens of its own on day 1.</summary>
    private StatsStore OwnIndex(out string directory)
    {
        directory = Temp();
        var store = new StatsStore(directory);
        store.AddDelta([Row(Day1, 1000)]);
        store.ReplaceSessionsAndMarkBackfilled([Session("own-session", 1000, "")]);
        return store;
    }

    /// <summary>Another PC's index as a backup carries it: its own rows (machine empty), a third PC's rows
    /// and a copy of this PC's rows under this PC's id.</summary>
    private string ForeignIndexFile(int days = 2)
    {
        var directory = Temp();
        var store = new StatsStore(directory);
        var rows = new List<StatsRecord> { Row(Day1, 100), Row(Day2, 50), Row(Day1, 7, Third), Row(Day1, 9999, Own) };
        store.AddDelta(days == 2 ? rows : [rows[0], rows[2], rows[3]]);
        store.ReplaceSessionsAndMarkBackfilled([Session("f1", 150, ""), Session("t1", 7, Third), Session("o1", 9999, Own)]);
        IndexPools.Release(directory);
        return Path.Combine(directory, "stats.db");
    }

    private static long Total(StatsStore store, string machine) =>
        store.LoadAll().Where(r => r.Machine == machine).Sum(r => r.TotalTokens);

    private static List<StatsRecord> Ordered(StatsStore store) =>
        [.. store.LoadAll().OrderBy(r => r.Machine).ThenBy(r => r.Day).ThenBy(r => r.Hour)];

    [Fact]
    public void An_import_files_the_foreign_rows_under_its_id_and_leaves_this_PCs_rows_alone()
    {
        var own = OwnIndex(out _);
        var foreign = ForeignIndexFile();

        var days = own.ImportMachine(foreign, Foreign, Own);

        Assert.Equal(2, days);
        Assert.Equal(1000, Total(own, ""));
        Assert.Equal(150, Total(own, Foreign));
        Assert.Equal(7, Total(own, Third));
        Assert.Equal(0, Total(own, Own)); // its copy of this PC's history is skipped
        var sessions = own.LoadSessions();
        Assert.Contains(sessions, s => s.SessionId == "own-session" && s.Machine == "");
        Assert.Contains(sessions, s => s.SessionId == "f1" && s.Machine == Foreign);
        Assert.Contains(sessions, s => s.SessionId == "t1" && s.Machine == Third);
        Assert.DoesNotContain(sessions, s => s.SessionId == "o1");
    }

    [Fact]
    public void Importing_the_same_PC_twice_adds_nothing()
    {
        var own = OwnIndex(out _);
        var foreign = ForeignIndexFile();

        own.ImportMachine(foreign, Foreign, Own);
        var afterFirst = Ordered(own);
        var sessionsFirst = own.LoadSessions().OrderBy(s => s.SessionId).ToList();
        var days = own.ImportMachine(foreign, Foreign, Own);

        Assert.Equal(2, days);
        Assert.Equal(afterFirst, Ordered(own));
        Assert.Equal(sessionsFirst, own.LoadSessions().OrderBy(s => s.SessionId).ToList());
    }

    [Fact]
    public void A_later_import_of_the_same_PC_replaces_its_rows()
    {
        var own = OwnIndex(out _);
        own.ImportMachine(ForeignIndexFile(), Foreign, Own);

        var days = own.ImportMachine(ForeignIndexFile(days: 1), Foreign, Own);

        Assert.Equal(1, days);
        Assert.Equal(100, Total(own, Foreign));
        Assert.Equal(1000, Total(own, ""));
    }

    [Fact]
    public void Removing_a_PC_deletes_only_that_PCs_rows()
    {
        var own = OwnIndex(out _);
        own.ImportMachine(ForeignIndexFile(), Foreign, Own);

        Assert.True(own.RemoveMachine(Foreign));

        Assert.Equal(0, Total(own, Foreign));
        Assert.Equal(1000, Total(own, ""));
        Assert.Equal(7, Total(own, Third));
        Assert.DoesNotContain(own.LoadSessions(), s => s.Machine == Foreign);
        Assert.Contains(own.LoadSessions(), s => s.SessionId == "own-session");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Removing_with_an_id_that_could_name_this_PCs_own_rows_is_refused(string id)
    {
        var own = OwnIndex(out _);

        Assert.False(own.RemoveMachine(id));

        Assert.Equal(1000, Total(own, ""));
        Assert.Contains(own.LoadSessions(), s => s.SessionId == "own-session");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData(Own)]
    public void Importing_under_this_PCs_own_id_or_an_invalid_one_is_refused(string foreignId)
    {
        var own = OwnIndex(out _);
        var before = Ordered(own);

        Assert.Null(own.ImportMachine(ForeignIndexFile(), foreignId, Own));

        Assert.Equal(before, Ordered(own));
    }

    [Fact]
    public void An_import_with_an_invalid_own_id_is_refused()
    {
        var own = OwnIndex(out _);

        Assert.Null(own.ImportMachine(ForeignIndexFile(), Foreign, ""));
        Assert.Equal(1000, Total(own, ""));
    }

    [Fact]
    public void A_foreign_file_of_a_newer_or_unknown_schema_is_refused_before_any_row_is_read()
    {
        var own = OwnIndex(out _);
        var before = Ordered(own);
        var newer = ForeignIndexFile();
        var older = ForeignIndexFile();
        var noColumn = ForeignIndexFile();
        var garbage = Path.Combine(Temp(), "stats.db");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 6000).Select(i => (byte)(i * 11)).ToArray());
        SetVersion(newer, StatsStore.SchemaVersion + 1);
        SetVersion(older, 6);
        using (var connection = Open(noColumn))
            Run(connection, "ALTER TABLE session DROP COLUMN subagent_tokens");

        foreach (var file in new[] { newer, older, noColumn, garbage, Path.Combine(Temp(), "missing.db") })
            Assert.Null(own.ImportMachine(file, Foreign, Own));

        Assert.Equal(before, Ordered(own));
        Assert.Equal(1000, Total(own, ""));
    }

    [Fact]
    public void Importing_never_changes_the_foreign_file()
    {
        var own = OwnIndex(out _);
        var foreign = ForeignIndexFile();
        var hash = SHA256.HashData(File.ReadAllBytes(foreign));

        own.ImportMachine(foreign, Foreign, Own);
        IndexPools.Release(Path.GetDirectoryName(foreign)!);

        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(foreign)));
    }

    [Fact]
    public void The_third_PCs_rows_already_held_are_not_overwritten_by_an_import()
    {
        var own = OwnIndex(out _);
        own.AddDelta([Row(Day1, 3, Third)]);

        own.ImportMachine(ForeignIndexFile(), Foreign, Own);

        // The existing row for the third PC stays as it was (3 tokens), not the foreign copy's 7.
        Assert.Equal(3, Total(own, Third));
    }

    [Fact]
    public void The_stats_window_names_the_imported_PCs_only_when_there_are_some()
    {
        var viewModel = new StatsViewModel(new StatsStore(Temp()));
        Assert.False(viewModel.HasImportedFrom);
        Assert.Equal("", viewModel.ImportedFromText);

        viewModel.ImportedMachineNames = () => ["Work PC", "Laptop"];

        Assert.True(viewModel.HasImportedFrom);
        Assert.Equal(AiUsage.Services.LocalizationService.Instance.Format("Stats.ImportedFrom", "Work PC, Laptop"), viewModel.ImportedFromText);
    }

    private static void SetVersion(string path, int version)
    {
        using var connection = Open(path);
        Run(connection, $"UPDATE schema_version SET version = {version}");
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>Lets go of the pooled connections to one test index, so its file can be moved or deleted,
/// without closing the pools other tests running at the same time still use.</summary>
internal static class IndexPools
{
    public static void Release(string directory)
    {
        var path = Path.Combine(directory, "stats.db");
        using var probe = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 2 }.ToString());
        SqliteConnection.ClearPool(probe);
    }

    public static void ReleaseUnder(string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var file in Directory.EnumerateFiles(root, "stats.db", SearchOption.AllDirectories))
            Release(Path.GetDirectoryName(file)!);
    }
}
