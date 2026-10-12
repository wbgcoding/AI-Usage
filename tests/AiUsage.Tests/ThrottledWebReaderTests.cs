using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Services;
using AiUsage.Web;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public sealed class ThrottledWebReaderTests : IDisposable
{
    private const string OkEnvelope = """
        {"status":"ok","path":"/backend-api/codex/usage","body":"{\"rate_limits\":{\"primary\":{\"used_percent\":12.5,\"window_minutes\":300},\"secondary\":{\"used_percent\":64,\"window_minutes\":10080}}}"}
        """;

    private const string FailedEnvelope = """{"status":"failed"}""";
    private const string NotSignedInEnvelope = """{"status":"not_signed_in"}""";

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-18T20:00:00Z");

    private readonly List<IDisposable> _directories = [];

    private ThrottledWebReader NewReader(Func<string> answer, out List<string> scriptRuns, bool profileFolderExists = true, AppSettings? settings = null)
    {
        var profileRoot = TestPaths.CreateDisposableDirectory("ai-usage-throttled-webview");
        _directories.Add(profileRoot);
        WebViewHost.SetRootOverride(profileRoot);
        if (profileFolderExists)
            Directory.CreateDirectory(Path.Combine(profileRoot, "codex"));

        var runs = new List<string>();
        scriptRuns = runs;
        var descriptor = new WebSessionDescriptor(
            ProviderId: "codex", BaseUrl: "https://chatgpt.com/codex/settings/usage",
            SignInUrl: "https://chatgpt.com/", AllowedHosts: ["chatgpt.com"], ProfileFolderName: "codex");
        var source = new WebUsageSource(descriptor, new CodexUsageEndpoint(() => Start), (script, _) =>
        {
            runs.Add(script);
            return Task.FromResult(answer());
        });
        return new ThrottledWebReader("codex", source, settings ?? new AppSettings(), _ => { }, showsReportedPlan: true, pausesAfterFailedDiscovery: false);
    }

    [Fact]
    public async Task A_failed_read_does_not_hold_back_the_next_one()
    {
        var answer = FailedEnvelope;
        var reader = NewReader(() => answer, out var runs);

        Assert.Null(await reader.ReadAsync(Start, CancellationToken.None));
        var afterFirst = runs.Count;
        Assert.True(afterFirst > 0);

        answer = OkEnvelope;
        var second = await reader.ReadAsync(Start.AddSeconds(30), CancellationToken.None);

        Assert.True(runs.Count > afterFirst);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task A_good_read_still_holds_back_the_next_one_for_the_floor()
    {
        var reader = NewReader(() => OkEnvelope, out var runs);

        await reader.ReadAsync(Start, CancellationToken.None);
        var afterFirst = runs.Count;
        var second = await reader.ReadAsync(Start.AddSeconds(30), CancellationToken.None);

        Assert.Equal(afterFirst, runs.Count);
        Assert.NotNull(second);
    }

    [Theory]
    [InlineData(null, 6, true)]
    [InlineData(null, 4, false)]
    [InlineData(10, 6, false)]
    [InlineData(10, 11, true)]
    public async Task The_accounts_own_interval_decides_how_long_a_good_read_is_held_back(int? ownMinutes, int minutesLater, bool readsAgain)
    {
        var settings = new AppSettings();
        settings.Providers["codex"].RefreshMinutes = ownMinutes;
        var reader = NewReader(() => OkEnvelope, out var runs, settings: settings);

        await reader.ReadAsync(Start, CancellationToken.None);
        var afterFirst = runs.Count;
        await reader.ReadAsync(Start.AddMinutes(minutesLater), CancellationToken.None);

        Assert.Equal(readsAgain, runs.Count > afterFirst);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_last_good_snapshot()
    {
        var answer = OkEnvelope;
        var reader = NewReader(() => answer, out _);
        var good = await reader.ReadAsync(Start, CancellationToken.None);
        Assert.NotNull(good);

        answer = FailedEnvelope;
        var failed = await reader.ReadAsync(Start.AddMinutes(6), CancellationToken.None);

        Assert.NotNull(failed);
        Assert.Equal(good!.Windows.Select(w => w.UsedPercent), failed!.Windows.Select(w => w.UsedPercent));
        Assert.True(failed.HeldOver);
    }

    [Fact]
    public async Task A_failure_that_outlasts_the_freshness_window_is_no_longer_held_over()
    {
        var answer = OkEnvelope;
        var reader = NewReader(() => answer, out _);
        Assert.NotNull(await reader.ReadAsync(Start, CancellationToken.None));

        answer = FailedEnvelope;
        var justInside = await reader.ReadAsync(Start + ProviderFreshness.StaleAfter - TimeSpan.FromMinutes(1), CancellationToken.None);
        var past = await reader.ReadAsync(Start + ProviderFreshness.StaleAfter + TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(justInside);
        Assert.True(justInside!.HeldOver);
        Assert.Null(past);
    }

    [Fact]
    public async Task A_signed_out_answer_drops_the_cached_snapshot()
    {
        var answer = OkEnvelope;
        var reader = NewReader(() => answer, out _);
        Assert.NotNull(await reader.ReadAsync(Start, CancellationToken.None));

        answer = NotSignedInEnvelope;
        Assert.Null(await reader.ReadAsync(Start.AddMinutes(6), CancellationToken.None));
        Assert.False(reader.SessionSignedIn);

        // The signed-out read set the floor; the cached snapshot is gone, not replayed.
        Assert.Null(await reader.ReadAsync(Start.AddMinutes(7), CancellationToken.None));
    }

    [Fact]
    public async Task A_held_network_skips_the_read_without_running_a_script()
    {
        var reader = NewReader(() => OkEnvelope, out var runs);

        using (FetchContext.HoldNetwork())
            Assert.Null(await reader.ReadUnlessHeldAsync(Start, CancellationToken.None));
        Assert.Empty(runs);

        Assert.NotNull(await reader.ReadUnlessHeldAsync(Start, CancellationToken.None));
    }

    public void Dispose()
    {
        WebViewHost.ClearRootOverrideForTests();
        foreach (var directory in _directories)
            directory.Dispose();
    }
}
