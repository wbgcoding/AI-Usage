using AiUsage.Models;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

public class UpdateCheckTests
{
    // The release link is opened through the shell: anything but an https page on github.com could
    // start a local program instead.
    [Theory]
    [InlineData("https://github.com/owner/repo/releases/tag/v1.0.0", true)]
    [InlineData("http://github.com/owner/repo/releases/tag/v1.0.0", false)]
    [InlineData("https://github.com.example.net/x", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("C:\\Windows\\System32\\calc.exe", false)]
    [InlineData(null, false)]
    public void OnlyAnHttpsGithubPageCountsAsAReleaseLink(string? url, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsReleasePageUrl(url));

    [Theory]
    [InlineData("1.2.0", "1.3.0", true)] // newer
    [InlineData("1.3.0", "1.2.0", false)] // older
    [InlineData("1.2.0", "1.2.0", false)] // equal
    [InlineData("1.2.0", "v1.3.0", true)] // v-prefixed
    [InlineData("1.2.0", "not-a-version", false)] // unparseable
    public void IsNewer_compares_running_against_the_tag(string runningVersion, string tagName, bool expected)
    {
        Assert.Equal(expected, UpdateCheck.IsNewer(runningVersion, tagName));
    }

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CheckIfDueAsync_skips_the_fetch_and_returns_null_when_checking_is_turned_off()
    {
        var settings = new AppSettings { CheckForUpdates = false };
        var calls = 0;

        var result = await UpdateCheck.CheckIfDueAsync(settings, _ => { }, _ => { calls++; return Task.FromResult<UpdateCheck.Release?>(new("v9.9.9", "https://example.invalid")); }, Now, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CheckIfDueAsync_does_not_call_the_fetcher_a_second_time_inside_the_interval()
    {
        var settings = new AppSettings();
        var calls = 0;
        Task<UpdateCheck.Release?> Fetch(CancellationToken ct) { calls++; return Task.FromResult<UpdateCheck.Release?>(new("v1.0.0", "https://example.invalid")); }

        var first = await UpdateCheck.CheckIfDueAsync(settings, _ => { }, Fetch, Now, CancellationToken.None);
        var second = await UpdateCheck.CheckIfDueAsync(settings, _ => { }, Fetch, Now + TimeSpan.FromHours(1), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CheckIfDueAsync_calls_the_fetcher_again_once_the_interval_has_passed()
    {
        var settings = new AppSettings();
        var calls = 0;
        Task<UpdateCheck.Release?> Fetch(CancellationToken ct) { calls++; return Task.FromResult<UpdateCheck.Release?>(new("v1.0.0", "https://example.invalid")); }

        await UpdateCheck.CheckIfDueAsync(settings, _ => { }, Fetch, Now, CancellationToken.None);
        await UpdateCheck.CheckIfDueAsync(settings, _ => { }, Fetch, Now + UpdateCheck.MinCheckInterval, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CheckIfDueAsync_stamps_LastUpdateCheckUtc_and_saves_even_when_the_fetch_fails()
    {
        var settings = new AppSettings();
        AppSettings? saved = null;

        await UpdateCheck.CheckIfDueAsync(settings, s => saved = s, _ => Task.FromResult<UpdateCheck.Release?>(null), Now, CancellationToken.None);

        Assert.Equal(Now, settings.LastUpdateCheckUtc);
        Assert.Same(settings, saved);
    }
}
