using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Services;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

public class ClaudeConsoleWiringTests
{
    private const string OrgId = "2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc";

    private const string OrgsOk =
        "{\"status\":\"ok\",\"body\":\"[{\\\"uuid\\\":\\\"" + OrgId + "\\\",\\\"capabilities\\\":[\\\"api\\\"]}]\",\"lastActiveOrg\":null}";

    private const string CreditsOk = "{\"status\":\"ok\",\"body\":\"{\\\"amount\\\":1840}\"}";

    private const string WebOkEnvelope =
        "{\"status\":\"ok\",\"path\":\"/api/organizations/abc/usage\",\"body\":\"{\\\"five_hour\\\":{\\\"utilization\\\":55,\\\"resets_at\\\":1788624000}}\"}";

    private static readonly WebSessionDescriptor Descriptor = new(
        ProviderId: "claude", BaseUrl: "https://claude.ai/",
        SignInUrl: "https://claude.ai/login", AllowedHosts: ["claude.ai"], ProfileFolderName: "claude");

    [Theory]
    [InlineData("claude")]
    [InlineData("claude#2")]
    public void The_console_session_shares_the_accounts_profile_folder(string accountKey)
    {
        var account = ProviderRegistry.WebSessionFor(accountKey);
        var console = ProviderRegistry.ConsoleSessionFor(accountKey);

        Assert.Equal(account.ProfileFolderName, console.ProfileFolderName);
        Assert.Contains("platform.claude.com", console.AllowedHosts);
        Assert.StartsWith("https://platform.claude.com/", console.BaseUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://platform.claude.com/", console.SignInUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("claude#2")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public void Only_the_console_id_counts_as_a_console_session(string accountKey)
    {
        var console = ProviderRegistry.ConsoleSessionFor(accountKey);

        Assert.True(ProviderRegistry.IsConsoleSession(console.ProviderId));
        Assert.NotEqual(accountKey, console.ProviderId);
        Assert.False(ProviderRegistry.IsConsoleSession(accountKey));
    }

    [Fact]
    public void The_console_sign_in_check_lists_the_organizations()
    {
        var script = ProviderRegistry.SessionCheckScriptFor(ProviderRegistry.ConsoleSessionFor("claude").ProviderId);

        Assert.Equal(ClaudeConsoleCreditScript.ListOrganizations(), script);
    }

    private sealed class Counter : IAsyncDisposable
    {
        public int Disposed;

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_companion_is_closed_with_the_runner()
    {
        var runner = new WebSessionScriptRunner(ProviderRegistry.WebSessionFor("claude"));
        var companion = new Counter();
        runner.AttachCompanion(companion);

        await runner.DisposeAsync();
        await runner.DisposeAsync();

        Assert.Equal(1, companion.Disposed);
    }

    [Fact]
    public async Task A_companion_attached_after_disposal_is_closed_at_once()
    {
        var runner = new WebSessionScriptRunner(ProviderRegistry.WebSessionFor("claude"));
        await runner.DisposeAsync();
        var companion = new Counter();

        runner.AttachCompanion(companion);
        await Task.Delay(100);

        Assert.Equal(1, companion.Disposed);
    }

    private static ClaudeConsoleCreditSource Source() => new(
        (script, _) => Task.FromResult(script.Contains("'/api/organizations'", StringComparison.Ordinal) ? OrgsOk : CreditsOk),
        () => throw new InvalidOperationException("the console runner is not needed"));

    private static ClaudeProvider Provider(AppSettings settings, ClaudeConsoleCreditSource? source) => new(
        projectsRoot: Path.Combine(AppContext.BaseDirectory, "Fixtures", "does-not-exist"), now: null,
        new WebUsageSource(Descriptor, new ClaudeUsageEndpoint(), (_, _) => Task.FromResult(WebOkEnvelope)),
        settings, _ => { })
    {
        CreditSource = source,
    };

    private static async Task<ClaudeApiCredit?> CreditAfterTwoReads(ClaudeProvider provider)
    {
        await provider.FetchAsync(CancellationToken.None);
        await Task.Delay(300);
        return (await provider.FetchAsync(CancellationToken.None)).ApiCredit;
    }

    [Fact]
    public async Task The_snapshot_carries_the_balance_once_it_was_read()
    {
        var credit = await CreditAfterTwoReads(Provider(new AppSettings(), Source()));

        Assert.Equal(new ClaudeApiCredit(18.40m, "USD"), credit);
    }

    [Fact]
    public async Task A_local_only_account_never_reads_or_shows_a_balance()
    {
        var settings = new AppSettings();
        settings.Providers["claude"] = new ProviderSettings { LocalOnly = true };

        var credit = await CreditAfterTwoReads(Provider(settings, Source()));

        Assert.Null(credit);
    }

    [Fact]
    public async Task An_account_without_a_credit_source_has_no_balance()
    {
        Assert.Null(await CreditAfterTwoReads(Provider(new AppSettings(), null)));
    }
}
