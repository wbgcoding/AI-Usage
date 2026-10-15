using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Views;

namespace AiUsage.Tests;

/// <summary>The start-up caches: the JIT profile, the statistics window built ahead of time and the
/// last statistics data kept between runs.</summary>
public class StartupCacheTests : IDisposable
{
    private readonly string _directory = TestPaths.GetPath("startup-cache");

    public StartupCacheTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot.Find(), "src", "AiUsage", .. parts]));

    private static StatsDataSnapshot Sample() => new(
        [
            new StatsRecord("claude", new DateOnly(2026, 10, 14), "opus", "C:/proj/a", 10, 20, 30, 40, 13, "high", "pc-1", true),
            new StatsRecord("codex", new DateOnly(2026, 10, 15), "gpt", "b", 1, 2, 3, 4),
        ],
        [
            new StatsSessionRecord("claude", "pc-1", "s1", "C:/proj/a",
                new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.Zero),
                10, 20, 30, 40, "opus", 5),
        ]);

    // ---- JIT profile ----

    [Fact]
    public void The_profile_root_is_set_before_the_profile_starts_and_the_folder_exists()
    {
        var folder = Path.Combine(_directory, "cache");
        var calls = new List<string>();

        var started = StartupProfile.Start(folder,
            root =>
            {
                Assert.True(Directory.Exists(root));
                calls.Add("root " + root);
            },
            name => calls.Add("start " + name));

        Assert.True(started);
        Assert.Equal(["root " + folder, "start " + StartupProfile.ProfileName], calls);
    }

    [Fact]
    public void A_failing_runtime_call_never_stops_the_start()
    {
        Assert.False(StartupProfile.Start(Path.Combine(_directory, "cache"), _ => throw new InvalidOperationException("set twice"), _ => { }));
        Assert.False(StartupProfile.Start(Path.Combine(_directory, "cache2"), _ => { }, _ => throw new UnauthorizedAccessException()));
    }

    [Fact]
    public void The_profile_folder_is_machine_local()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "AI-Usage", "cache"), StartupProfile.CacheDirectory);
    }

    // The order inside OnStartup cannot be run without a live application, so it is read from the source.
    [Fact]
    public void The_profile_starts_before_the_base_start_and_before_any_window_is_built()
    {
        var app = Source("App.xaml.cs");
        var profile = app.IndexOf("StartupProfile.Start()", StringComparison.Ordinal);

        Assert.True(profile > 0);
        Assert.True(profile < app.IndexOf("base.OnStartup(e)", StringComparison.Ordinal));
        Assert.True(profile < app.IndexOf("new MainWindow(", StringComparison.Ordinal));
    }

    // ---- prebuild ----

    [Fact]
    public void The_statistics_window_is_prebuilt_two_seconds_after_the_first_idle_moment()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), MainWindow.PrebuildDelay);

        var source = Source("Views", "MainWindow.xaml.cs");
        var start = source.IndexOf("private void SchedulePreparedStatsWindow()", StringComparison.Ordinal);
        var body = source[start..source.IndexOf("private void TitleBarControl_MinimizeRequested", start, StringComparison.Ordinal)];
        Assert.Contains("Interval = PrebuildDelay", body);
        Assert.Contains("DispatcherPriority.ApplicationIdle, timer.Start", body);
    }

    [Theory]
    [InlineData(false, false, MainWindow.PrebuildStep.Build)]
    [InlineData(false, true, MainWindow.PrebuildStep.Wait)]
    [InlineData(true, false, MainWindow.PrebuildStep.Skip)]
    [InlineData(true, true, MainWindow.PrebuildStep.Skip)]
    public void The_prebuild_waits_while_the_startup_walk_runs(bool windowExists, bool indexRunning, MainWindow.PrebuildStep expected)
    {
        Assert.Equal(expected, MainWindow.NextPrebuildStep(windowExists, indexRunning));
    }

    // ---- kept statistics data ----

    [Fact]
    public void The_snapshot_survives_a_round_trip()
    {
        var path = StatsSnapshotFile.PathIn(_directory);
        var snapshot = Sample();

        Assert.True(StatsSnapshotFile.Save(path, snapshot));
        var loaded = StatsSnapshotFile.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Records, loaded.Records);
        Assert.Equal(snapshot.Sessions, loaded.Sessions);
        Assert.Contains("\"Version\":" + StatsSnapshotFile.CurrentVersion, File.ReadAllText(path));
    }

    [Fact]
    public void An_unknown_version_is_ignored_and_deleted()
    {
        var path = StatsSnapshotFile.PathIn(_directory);
        File.WriteAllText(path, "{\"Version\":" + (StatsSnapshotFile.CurrentVersion + 1) + ",\"Records\":[],\"Sessions\":[]}");

        Assert.Null(StatsSnapshotFile.Load(path));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"Version\":1,\"Records\":[{\"Provider\":5}],\"Sessions\":[]}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("")]
    public void An_unreadable_file_is_ignored_and_deleted(string content)
    {
        var path = StatsSnapshotFile.PathIn(_directory);
        File.WriteAllText(path, content);

        Assert.Null(StatsSnapshotFile.Load(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_missing_file_gives_nothing()
    {
        Assert.Null(StatsSnapshotFile.Load(StatsSnapshotFile.PathIn(_directory)));
    }

    [Fact]
    public void The_next_run_has_the_kept_data_at_once_and_a_walk_replaces_it()
    {
        var path = StatsSnapshotFile.PathIn(_directory);
        var fresh = new StatsDataSnapshot([new StatsRecord("codex", new DateOnly(2026, 10, 16), "gpt", "p", 9, 9, 9, 9)], []);

        using (var first = StatsIndexerService.ForTest(_ => new StatsIndexResult(new StatsIndexCounters(1, 1, 0, 0, 0), new StatsIndexCounters(1, 0, 0, 0, 0)),
            loadSnapshot: Sample, snapshotPath: () => path))
        {
            using var done = new ManualResetEventSlim();
            first.IndexCompleted += (_, _) => done.Set();
            first.StartInBackground();
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
            first.SavePersistedSnapshot();
        }

        Assert.True(File.Exists(path));

        using var second = StatsIndexerService.ForTest(_ => new StatsIndexResult(new StatsIndexCounters(1, 1, 0, 0, 0), new StatsIndexCounters(1, 0, 0, 0, 0)),
            loadSnapshot: () => fresh, snapshotPath: () => path);
        Assert.Null(second.Latest);
        second.LoadPersistedSnapshotInBackground().Wait(TimeSpan.FromSeconds(5));

        // Before any walk: the data of the last run.
        Assert.Equal(Sample().Records, second.Latest?.Records);

        using var walked = new ManualResetEventSlim();
        second.IndexCompleted += (_, _) => walked.Set();
        second.StartInBackground();
        Assert.True(walked.Wait(TimeSpan.FromSeconds(5)));
        Assert.Same(fresh, second.Latest);
    }

    [Fact]
    public void Data_from_a_walk_is_not_overwritten_by_a_late_read_of_the_file()
    {
        var path = StatsSnapshotFile.PathIn(_directory);
        Assert.True(StatsSnapshotFile.Save(path, Sample()));
        var fresh = new StatsDataSnapshot([], []);
        using var service = StatsIndexerService.ForTest(
            _ => new StatsIndexResult(new StatsIndexCounters(1, 1, 0, 0, 0), new StatsIndexCounters(1, 0, 0, 0, 0)),
            loadSnapshot: () => fresh, snapshotPath: () => path);
        using var walked = new ManualResetEventSlim();
        service.IndexCompleted += (_, _) => walked.Set();
        service.StartInBackground();
        Assert.True(walked.Wait(TimeSpan.FromSeconds(5)));

        service.LoadPersistedSnapshotInBackground().Wait(TimeSpan.FromSeconds(5));

        Assert.Same(fresh, service.Latest);
    }

    [Fact]
    public void An_unchanged_snapshot_is_not_written_again()
    {
        var path = StatsSnapshotFile.PathIn(_directory);
        Assert.True(StatsSnapshotFile.Save(path, Sample()));
        using var service = StatsIndexerService.ForTest(
            _ => new StatsIndexResult(new StatsIndexCounters(1, 1, 0, 0, 0), new StatsIndexCounters(1, 0, 0, 0, 0)),
            snapshotPath: () => path);
        service.LoadPersistedSnapshotInBackground().Wait(TimeSpan.FromSeconds(5));
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        service.SavePersistedSnapshot();

        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Both_exits_write_the_kept_data()
    {
        Assert.Contains("SavePersistedSnapshot", Source("App.xaml.cs"));
        Assert.Contains("SavePersistedSnapshot", Source("Views", "StatsWindow.xaml.cs"));
        Assert.Contains("LoadPersistedSnapshotInBackground", Source("App.xaml.cs"));
    }
}
