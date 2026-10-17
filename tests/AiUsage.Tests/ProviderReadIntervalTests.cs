using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

/// <summary>How often each provider may be read: the account's own value, else the global one, and a
/// chosen value down to one minute on every route.</summary>
public class ProviderReadIntervalTests
{
    private static AppSettings Settings(int global, string? accountKey = null, int? own = null)
    {
        var settings = new AppSettings { RemoteRefreshMinutes = global };
        if (accountKey is not null)
            settings.Providers[accountKey] = new ProviderSettings { RefreshMinutes = own };
        return settings;
    }

    [Theory]
    [InlineData(1, null, 1)]
    [InlineData(3, null, 3)]
    [InlineData(5, null, 5)]
    [InlineData(30, null, 30)]
    [InlineData(30, 10, 10)]
    [InlineData(1, 30, 30)]
    [InlineData(30, 5, 5)]
    [InlineData(60, 15, 15)]
    // Not one of the allowed values: the global one counts.
    [InlineData(30, 7, 30)]
    [InlineData(30, 0, 30)]
    public void AnyRouteTakesTheOwnValueThenTheGlobalOneDownToOneMinute(int global, int? own, int expectedMinutes)
    {
        var settings = Settings(global, "cursor", own);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), ProviderRegistry.ReadInterval(settings, "cursor"));
    }

    [Theory]
    [InlineData(1, null, 1)]
    [InlineData(3, null, 3)]
    [InlineData(1, 10, 10)]
    [InlineData(30, 5, 5)]
    public void TheClaudeAccountFollowsTheValueDownToTheGlobalMinimum(int global, int? own, int expectedMinutes)
    {
        var settings = Settings(global, "claude", own);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), ProviderRegistry.ReadInterval(settings, "claude"));
    }

    [Fact]
    public void AnAccountWithoutAnEntryFollowsTheGlobalValue()
    {
        var settings = Settings(20);
        settings.Providers.Remove("gemini#2");

        Assert.Equal(TimeSpan.FromMinutes(20), ProviderRegistry.ReadInterval(settings, "gemini#2"));
    }

    [Fact]
    public void NoSettingsAtAllHoldsTheFloor()
    {
        Assert.Equal(ProviderRegistry.RemoteReadFloor, ProviderRegistry.ReadInterval(null, "cursor"));
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(15, 15)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(1, null)]
    [InlineData(7, null)]
    [InlineData(61, null)]
    [InlineData(-5, null)]
    [InlineData(0, null)]
    [InlineData(null, null)]
    public void OnlyTheFiveAllowedValuesAreKept(int? stored, int? expected) =>
        Assert.Equal(expected, ProviderSettings.NormalizeRefreshMinutes(stored));

    [Fact]
    public void ALoadedFileKeepsAnAllowedOwnValueAndDropsAnyOtherOne()
    {
        var directory = TestPaths.CreateDirectory("read-interval-load");
        File.WriteAllText(Path.Combine(directory, "settings.json"),
            """{"schemaVersion":1,"providers":{"claude":{"refreshMinutes":15},"codex":{"refreshMinutes":7},"cursor":{"refreshMinutes":-1},"gemini":{}}}""");
        using var store = new SettingsStore(directory);

        var settings = store.Load();

        Assert.Equal(15, settings.Providers["claude"].RefreshMinutes);
        Assert.Null(settings.Providers["codex"].RefreshMinutes);
        Assert.Null(settings.Providers["cursor"].RefreshMinutes);
        Assert.Null(settings.Providers["gemini"].RefreshMinutes);
    }

    [Fact]
    public void TheOwnValueSurvivesASaveAndALoad()
    {
        var directory = TestPaths.CreateDirectory("read-interval-roundtrip");
        using (var store = new SettingsStore(directory))
        {
            var settings = store.Load();
            settings.Providers["copilot"].RefreshMinutes = 30;
            store.SaveNow(settings);
        }

        using var reloadStore = new SettingsStore(directory);
        var loaded = reloadStore.Load();

        Assert.Equal(30, loaded.Providers["copilot"].RefreshMinutes);
        Assert.Null(loaded.Providers["claude"].RefreshMinutes);
    }

    private static readonly WebSessionDescriptor Descriptor = new(
        ProviderId: "cursor", BaseUrl: "https://cursor.com/dashboard/usage", SignInUrl: "https://cursor.com/dashboard",
        AllowedHosts: ["cursor.com"], ProfileFolderName: "cursor");

    [Fact]
    public void CursorHonoursTheOwnValueThenTheGlobalOneDownToOneMinute()
    {
        var source = new WebUsageSource(Descriptor, new CursorUsageEndpoint(), (_, _) => throw new InvalidOperationException());

        Assert.Equal(TimeSpan.FromMinutes(10),
            new CursorProvider("cursor", source, Settings(30, "cursor", 10), _ => { }).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(30),
            new CursorProvider("cursor", source, Settings(30), _ => { }).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(1),
            new CursorProvider("cursor", source, Settings(1), _ => { }).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(15),
            new CursorProvider("cursor#2", source, Settings(1, "cursor#2", 15), _ => { }).MinRefreshInterval);
    }

    [Fact]
    public void GeminiHonoursTheOwnValueThenTheGlobalOneDownToOneMinute()
    {
        var source = new WebUsageSource(Descriptor with { ProviderId = "gemini" }, new GeminiUsageEndpoint(), (_, _) => throw new InvalidOperationException());

        Assert.Equal(TimeSpan.FromMinutes(60),
            new GeminiProvider(Settings(5, "gemini", 60), _ => { }, source).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(20),
            new GeminiProvider(Settings(20), _ => { }, source).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(1),
            new GeminiProvider(Settings(1), _ => { }, source).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), new GeminiProvider().MinRefreshInterval);
    }

    [Fact]
    public void CopilotHonoursTheOwnValueThenTheGlobalOneDownToOneMinute()
    {
        Assert.Equal(TimeSpan.FromMinutes(30),
            new CopilotProvider(Settings(5, "copilot", 30)).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(15),
            new CopilotProvider(Settings(15)).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(2),
            new CopilotProvider(Settings(2)).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(10),
            new CopilotProvider("copilot#2", "someone", Settings(2, "copilot#2", 10)).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), new CopilotProvider().MinRefreshInterval);
    }

    [Fact]
    public void ClaudeHonoursTheChosenValueOnBothRoutesDownToTheMinimum()
    {
        var withOwn = Settings(1, "claude", 15);
        var withoutOwn = Settings(2);
        Task<ClaudeCodeUsage> NotSignedIn(CancellationToken _) => Task.FromResult(ClaudeCodeUsage.NotSignedIn);

        Assert.Equal(TimeSpan.FromMinutes(15), new ClaudeProvider("claude", NotSignedIn, withOwn).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(2), new ClaudeProvider("claude", NotSignedIn, withoutOwn).MinRefreshInterval);

        var webSource = new WebUsageSource(
            Descriptor with { ProviderId = "claude" }, new ClaudeUsageEndpoint(), (_, _) => throw new InvalidOperationException());
        Assert.Equal(TimeSpan.FromMinutes(15), new ClaudeProvider("claude", withOwn, _ => { }, webSource).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(2), new ClaudeProvider("claude", withoutOwn, _ => { }, webSource).MinRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(1), new ClaudeProvider("claude", Settings(1), _ => { }, webSource).MinRefreshInterval);
    }
}
