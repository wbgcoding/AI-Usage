using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Storage;
using Xunit;

namespace AiUsage.Tests;

public class ClaudeConsoleCreditSourceTests
{
    private const string OrgId = "2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc";

    private const string OrgsOk =
        "{\"status\":\"ok\",\"body\":\"[{\\\"uuid\\\":\\\"" + OrgId + "\\\",\\\"capabilities\\\":[\\\"api\\\"]}]\",\"lastActiveOrg\":null}";

    private static string CreditsOk(int cents) => "{\"status\":\"ok\",\"body\":\"{\\\"amount\\\":" + cents + "}\"}";

    private const string NotSignedIn = "{\"status\":\"not_signed_in\"}";
    private const string NoApiOrgs = "{\"status\":\"ok\",\"body\":\"[]\"}";

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }

    /// <summary>A scripted runner: answers by script kind and counts the calls.</summary>
    private sealed class FakeRunner
    {
        public string Organizations = OrgsOk;
        public string Credits = CreditsOk(1840);
        public int Calls;
        public int Disposed;

        public Task<string> Run(string script, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(script.Contains("'/api/organizations'", StringComparison.Ordinal) ? Organizations : Credits);
        }

        public ClaudeConsoleCreditSource.ConsoleRunner AsConsole() => new(Run, new Owner(this));

        private sealed class Owner(FakeRunner runner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                runner.Disposed++;
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task Reads_through_the_claude_session_and_never_builds_the_console_runner()
    {
        var site = new FakeRunner();
        var created = 0;
        var source = new ClaudeConsoleCreditSource(site.Run, () => { created++; return new FakeRunner().AsConsole(); }, new ManualTime());

        await source.RefreshIfDueAsync();

        Assert.Equal(new ClaudeApiCredit(18.40m, "USD"), source.Current);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Starts_no_browser_while_the_account_has_no_browser_profile()
    {
        var site = new FakeRunner();
        var hasProfile = false;
        var source = new ClaudeConsoleCreditSource(site.Run, null, new ManualTime(), () => hasProfile);

        await source.RefreshIfDueAsync();
        Assert.Equal(0, site.Calls);
        Assert.Null(source.Current);

        hasProfile = true;
        await source.RefreshIfDueAsync();
        Assert.Equal(new ClaudeApiCredit(18.40m, "USD"), source.Current);
    }

    [Fact]
    public async Task Reads_at_most_every_thirty_minutes()
    {
        var time = new ManualTime();
        var site = new FakeRunner();
        var source = new ClaudeConsoleCreditSource(site.Run, null, time);

        await source.RefreshIfDueAsync();
        var afterFirst = site.Calls;
        time.Advance(TimeSpan.FromMinutes(29));
        await source.RefreshIfDueAsync();
        Assert.Equal(afterFirst, site.Calls);

        time.Advance(TimeSpan.FromMinutes(2));
        site.Credits = CreditsOk(900);
        await source.RefreshIfDueAsync();

        Assert.True(site.Calls > afterFirst);
        Assert.Equal(9.00m, source.Current!.Amount);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_last_value_for_two_hours_then_drops_it()
    {
        var time = new ManualTime();
        var site = new FakeRunner();
        var source = new ClaudeConsoleCreditSource(site.Run, null, time);
        await source.RefreshIfDueAsync();

        site.Organizations = "{\"status\":\"failed\"}";
        time.Advance(TimeSpan.FromMinutes(31));
        await source.RefreshIfDueAsync();
        Assert.Equal(18.40m, source.Current!.Amount);

        time.Advance(TimeSpan.FromMinutes(31));
        await source.RefreshIfDueAsync();
        time.Advance(TimeSpan.FromMinutes(31));
        await source.RefreshIfDueAsync();
        Assert.Equal(18.40m, source.Current!.Amount); // 93 minutes since the good read

        time.Advance(TimeSpan.FromMinutes(31));
        await source.RefreshIfDueAsync();
        Assert.Null(source.Current); // 124 minutes
    }

    [Fact]
    public async Task Falls_back_to_the_console_session_when_claude_has_no_api_organization()
    {
        var site = new FakeRunner { Organizations = NoApiOrgs };
        var console = new FakeRunner { Credits = CreditsOk(500) };
        var source = new ClaudeConsoleCreditSource(site.Run, console.AsConsole, new ManualTime());

        await source.RefreshIfDueAsync();

        Assert.Equal(5.00m, source.Current!.Amount);
        Assert.Equal(1, site.Calls);
    }

    private const string CreditsRefused = "{\"status\":\"failed\",\"code\":\"403\"}";

    [Fact]
    public async Task Asks_the_console_first_once_it_was_the_route_that_delivered()
    {
        var time = new ManualTime();
        var site = new FakeRunner { Credits = CreditsRefused };
        var console = new FakeRunner { Credits = CreditsOk(500) };
        var source = new ClaudeConsoleCreditSource(site.Run, console.AsConsole, time);

        await source.RefreshIfDueAsync();
        Assert.Equal(ClaudeCreditRoute.Console, source.CurrentRoute);
        var siteCalls = site.Calls;
        var consoleCalls = console.Calls;

        time.Advance(TimeSpan.FromMinutes(31));
        console.Credits = CreditsOk(700);
        await source.RefreshIfDueAsync();

        Assert.Equal(7.00m, source.Current!.Amount);
        Assert.Equal(siteCalls, site.Calls);
        Assert.True(console.Calls > consoleCalls);
    }

    [Fact]
    public async Task Falls_back_to_the_claude_session_when_the_preferred_console_does_not_deliver_and_prefers_it_then()
    {
        var time = new ManualTime();
        var site = new FakeRunner { Credits = CreditsRefused };
        var console = new FakeRunner { Credits = CreditsOk(500) };
        var source = new ClaudeConsoleCreditSource(site.Run, console.AsConsole, time);
        await source.RefreshIfDueAsync();

        site.Credits = CreditsOk(900);
        console.Organizations = "{\"status\":\"failed\"}";
        time.Advance(TimeSpan.FromMinutes(31));
        var consoleCalls = console.Calls;
        await source.RefreshIfDueAsync();

        Assert.Equal(9.00m, source.Current!.Amount);
        Assert.Equal(ClaudeCreditRoute.ClaudeSignIn, source.CurrentRoute);
        Assert.Equal(consoleCalls + 1, console.Calls);

        time.Advance(TimeSpan.FromMinutes(31));
        await source.RefreshIfDueAsync();
        Assert.Equal(consoleCalls + 1, console.Calls);
    }

    [Fact]
    public async Task A_preferred_console_that_is_signed_out_falls_back_and_is_left_alone_afterwards()
    {
        var time = new ManualTime();
        var site = new FakeRunner { Credits = CreditsRefused };
        var console = new FakeRunner { Credits = CreditsOk(500) };
        var source = new ClaudeConsoleCreditSource(site.Run, console.AsConsole, time);
        await source.RefreshIfDueAsync();

        site.Credits = CreditsOk(900);
        console.Organizations = NotSignedIn;
        time.Advance(TimeSpan.FromMinutes(31));
        await source.RefreshIfDueAsync();
        var consoleCalls = console.Calls;
        Assert.Equal(9.00m, source.Current!.Amount);

        time.Advance(TimeSpan.FromMinutes(31));
        await source.RefreshIfDueAsync();
        Assert.Equal(consoleCalls, console.Calls);
    }

    [Fact]
    public async Task A_blocked_answer_from_the_preferred_console_stops_the_read()
    {
        var time = new ManualTime();
        var site = new FakeRunner { Credits = CreditsRefused };
        var console = new FakeRunner { Credits = CreditsOk(500) };
        var source = new ClaudeConsoleCreditSource(site.Run, console.AsConsole, time);
        await source.RefreshIfDueAsync();
        var siteCalls = site.Calls;

        console.Organizations = "{\"status\":\"blocked\"}";
        time.Advance(TimeSpan.FromMinutes(31));
        await source.RefreshIfDueAsync();

        Assert.Equal(siteCalls, site.Calls);
    }

    [Fact]
    public async Task Does_not_ask_a_signed_out_console_again_for_six_hours()
    {
        var time = new ManualTime();
        var site = new FakeRunner { Organizations = NotSignedIn };
        var console = new FakeRunner { Organizations = NotSignedIn };
        var source = new ClaudeConsoleCreditSource(site.Run, console.AsConsole, time);

        await source.RefreshIfDueAsync();
        Assert.Equal(1, console.Calls);

        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromMinutes(31));
            await source.RefreshIfDueAsync();
        }

        Assert.Equal(1, console.Calls); // 310 minutes, still paused

        time.Advance(TimeSpan.FromMinutes(60));
        await source.RefreshIfDueAsync();
        Assert.Equal(2, console.Calls);
    }

    [Fact]
    public async Task ReadNowAsync_forgets_the_console_pause_and_reads_at_once()
    {
        var time = new ManualTime();
        var site = new FakeRunner { Organizations = NotSignedIn };
        var console = new FakeRunner { Organizations = NotSignedIn };
        var source = new ClaudeConsoleCreditSource(site.Run, console.AsConsole, time);
        await source.RefreshIfDueAsync();

        console.Organizations = OrgsOk;
        time.Advance(TimeSpan.FromMinutes(1));
        await source.ReadNowAsync();

        Assert.Equal(18.40m, source.Current!.Amount);
    }

    [Fact]
    public async Task Raises_changed_only_when_the_balance_differs()
    {
        var time = new ManualTime();
        var site = new FakeRunner();
        var source = new ClaudeConsoleCreditSource(site.Run, null, time);
        var raised = 0;
        source.Changed += () => raised++;

        await source.RefreshIfDueAsync();
        time.Advance(TimeSpan.FromMinutes(31));
        await source.RefreshIfDueAsync();
        Assert.Equal(1, raised);

        site.Credits = CreditsOk(100);
        time.Advance(TimeSpan.FromMinutes(31));
        await source.RefreshIfDueAsync();
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task An_unreadable_answer_shows_nothing_and_is_not_an_error()
    {
        var site = new FakeRunner { Credits = "{\"status\":\"ok\",\"body\":\"{\\\"balance\\\":1}\"}" };
        var source = new ClaudeConsoleCreditSource(site.Run, null, new ManualTime());

        await source.RefreshIfDueAsync();

        Assert.Null(source.Current);
    }

    [Fact]
    public async Task A_runner_that_throws_never_escapes()
    {
        var source = new ClaudeConsoleCreditSource((_, _) => throw new InvalidOperationException("boom"), null, new ManualTime());

        await source.RefreshIfDueAsync();

        Assert.Null(source.Current);
    }

    [Fact]
    public async Task Disposing_closes_the_console_runner_it_built()
    {
        var site = new FakeRunner { Organizations = NotSignedIn };
        var console = new FakeRunner { Organizations = NotSignedIn };
        var source = new ClaudeConsoleCreditSource(site.Run, console.AsConsole, new ManualTime());
        await source.RefreshIfDueAsync();

        await source.DisposeAsync();

        Assert.Equal(1, console.Disposed);
    }

    [Fact]
    public async Task Tries_the_next_api_organization_when_the_first_refuses_the_credit_address()
    {
        const string secondOrg = "9a0e7c11-3d2b-4f6a-8e55-1c4d7b2a90ef";
        var orgs = "{\"status\":\"ok\",\"body\":\"[{\\\"uuid\\\":\\\"" + OrgId + "\\\",\\\"capabilities\\\":[\\\"api\\\"]},"
            + "{\\\"uuid\\\":\\\"" + secondOrg + "\\\",\\\"capabilities\\\":[\\\"api\\\"]}]\",\"lastActiveOrg\":null}";
        var asked = new List<string>();
        Task<string> Run(string script, CancellationToken ct)
        {
            if (script.Contains("'/api/organizations'", StringComparison.Ordinal))
                return Task.FromResult(orgs);
            asked.Add(script.Contains(OrgId, StringComparison.Ordinal) ? OrgId : secondOrg);
            return Task.FromResult(script.Contains(OrgId, StringComparison.Ordinal)
                ? "{\"status\":\"failed\",\"code\":\"404\"}"
                : CreditsOk(725));
        }

        var source = new ClaudeConsoleCreditSource(Run, () => new FakeRunner().AsConsole(), new ManualTime());
        await source.RefreshIfDueAsync();

        Assert.Equal(7.25m, source.Current!.Amount);
        Assert.Equal([OrgId, secondOrg], asked);
    }

    private static StoredApiCredit Stored(ManualTime time, TimeSpan age, ClaudeCreditRoute route) => new()
    {
        Amount = 12.34m,
        Currency = "USD",
        Granted = 10m,
        Remaining = 4m,
        NextExpiry = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
        ReadAt = time.GetUtcNow() - age,
        Route = route,
    };

    [Fact]
    public void Serves_the_stored_balance_at_start_while_it_is_younger_than_two_hours()
    {
        var time = new ManualTime();
        var source = new ClaudeConsoleCreditSource(new FakeRunner().Run, null, time,
            loadStored: () => Stored(time, TimeSpan.FromMinutes(119), ClaudeCreditRoute.ClaudeSignIn));

        Assert.Equal(12.34m, source.Current!.Amount);
        Assert.Equal("USD", source.Current.Currency);
        Assert.Equal(4m, source.Current.Grant!.Remaining);
        Assert.Equal(10m, source.Current.Grant.Granted);
        Assert.Equal(ClaudeCreditRoute.ClaudeSignIn, source.CurrentRoute);
    }

    [Fact]
    public async Task Does_not_serve_a_stored_balance_that_is_two_hours_old_but_still_asks_its_route_first()
    {
        var time = new ManualTime();
        var site = new FakeRunner { Credits = CreditsRefused };
        var console = new FakeRunner { Credits = CreditsOk(500) };
        var source = new ClaudeConsoleCreditSource(site.Run, console.AsConsole, time,
            loadStored: () => Stored(time, TimeSpan.FromHours(2), ClaudeCreditRoute.Console));

        Assert.Null(source.Current);

        await source.RefreshIfDueAsync();

        Assert.Equal(5.00m, source.Current!.Amount);
        Assert.Equal(0, site.Calls);
    }

    [Fact]
    public async Task A_stored_balance_still_lets_the_first_read_run_and_a_new_read_overwrites_it()
    {
        var time = new ManualTime();
        var site = new FakeRunner();
        var saved = new List<StoredApiCredit?>();
        var source = new ClaudeConsoleCreditSource(site.Run, null, time,
            loadStored: () => Stored(time, TimeSpan.FromMinutes(10), ClaudeCreditRoute.ClaudeSignIn), saveStored: saved.Add);
        var changes = 0;
        source.Changed += () => changes++;

        await source.RefreshIfDueAsync();

        Assert.Equal(18.40m, source.Current!.Amount);
        Assert.Equal(1, changes);
        var written = Assert.Single(saved);
        Assert.Equal(18.40m, written!.Amount);
        Assert.Equal(time.GetUtcNow(), written.ReadAt);
        Assert.Equal(ClaudeCreditRoute.ClaudeSignIn, written.Route);
        Assert.Null(written.Granted);
    }

    [Fact]
    public async Task Saves_once_per_read_and_not_when_nothing_was_read()
    {
        var time = new ManualTime();
        var site = new FakeRunner();
        var saved = new List<StoredApiCredit?>();
        var source = new ClaudeConsoleCreditSource(site.Run, null, time, saveStored: saved.Add);

        await source.RefreshIfDueAsync();
        await source.RefreshIfDueAsync();
        Assert.Single(saved);

        site.Credits = "{\"status\":\"failed\"}";
        time.Advance(TimeSpan.FromMinutes(31));
        await source.RefreshIfDueAsync();
        Assert.Single(saved);
    }

    [Fact]
    public async Task Console_sign_out_removes_the_stored_balance_of_the_console_route()
    {
        var time = new ManualTime();
        var site = new FakeRunner { Credits = CreditsRefused };
        var console = new FakeRunner { Credits = CreditsOk(500) };
        var saved = new List<StoredApiCredit?>();
        var source = new ClaudeConsoleCreditSource(site.Run, console.AsConsole, time, saveStored: saved.Add);
        await source.RefreshIfDueAsync();
        Assert.Equal(ClaudeCreditRoute.Console, saved[^1]!.Route);

        await source.DisconnectConsoleAsync();

        Assert.Null(source.Current);
        Assert.Null(saved[^1]);
    }

    [Fact]
    public async Task Console_sign_out_removes_a_stored_console_balance_that_was_not_served()
    {
        var time = new ManualTime();
        var saved = new List<StoredApiCredit?> { Stored(time, TimeSpan.FromHours(3), ClaudeCreditRoute.Console) };
        var source = new ClaudeConsoleCreditSource(new FakeRunner().Run, new FakeRunner().AsConsole, time,
            loadStored: () => saved[0], saveStored: saved.Add);

        await source.DisconnectConsoleAsync();

        Assert.Null(saved[^1]);
    }

    [Fact]
    public async Task Console_sign_out_keeps_the_stored_balance_of_the_claude_route()
    {
        var time = new ManualTime();
        var saved = new List<StoredApiCredit?>();
        var source = new ClaudeConsoleCreditSource(new FakeRunner().Run, new FakeRunner().AsConsole, time, saveStored: saved.Add);
        await source.RefreshIfDueAsync();
        var count = saved.Count;

        await source.DisconnectConsoleAsync();

        Assert.Equal(count, saved.Count);
        Assert.Equal(18.40m, source.Current!.Amount);
    }

    [Fact]
    public void Ignores_a_stored_balance_with_a_bad_route_currency_or_a_read_time_in_the_future()
    {
        var time = new ManualTime();
        var noRoute = Stored(time, TimeSpan.FromMinutes(1), ClaudeCreditRoute.None);
        var badCurrency = Stored(time, TimeSpan.FromMinutes(1), ClaudeCreditRoute.Console);
        badCurrency.Currency = "<script>";
        var future = Stored(time, TimeSpan.FromMinutes(-30), ClaudeCreditRoute.Console);

        foreach (var stored in new[] { noRoute, badCurrency, future })
            Assert.Null(new ClaudeConsoleCreditSource(new FakeRunner().Run, null, time, loadStored: () => stored).Current);
    }

    [Fact]
    public void A_failing_load_or_save_never_reaches_the_read()
    {
        var time = new ManualTime();
        var source = new ClaudeConsoleCreditSource(new FakeRunner().Run, null, time,
            loadStored: () => throw new InvalidOperationException("boom"), saveStored: _ => throw new InvalidOperationException("boom"));

        Assert.Null(source.Current);
        source.RefreshIfDueAsync().GetAwaiter().GetResult();
        Assert.Equal(18.40m, source.Current!.Amount);
    }

    [Fact]
    public void A_stored_balance_survives_the_settings_file()
    {
        var time = new ManualTime();
        var settings = new AppSettings();
        settings.Providers["claude"] = new ProviderSettings { ApiCredit = Stored(time, TimeSpan.Zero, ClaudeCreditRoute.Console) };

        var json = System.Text.Json.JsonSerializer.Serialize(settings, SettingsStore.JsonOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;

        var kept = back.Providers["claude"].ApiCredit!;
        Assert.Equal(12.34m, kept.Amount);
        Assert.Equal(ClaudeCreditRoute.Console, kept.Route);
        Assert.Contains("\"route\": \"Console\"", json);
        Assert.DoesNotContain(OrgId, json);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("404", " (404)")]
    public void Names_the_failure_code_in_the_log_stage(string? code, string expected) =>
        Assert.Equal(expected, ClaudeConsoleCreditSource.CodeSuffix(code));

    [Fact]
    public void Both_scripts_name_the_console_client_like_the_console_itself()
    {
        Assert.Contains("'anthropic-client-platform'", ClaudeConsoleCreditScript.ListOrganizations());
        Assert.Contains("'anthropic-client-platform'", ClaudeConsoleCreditScript.FetchCredits(ClaudeConsoleCreditScript.CreditPath(OrgId)));
        Assert.Contains("'web_console'", ClaudeConsoleCreditScript.FetchCredits(ClaudeConsoleCreditScript.CreditPath(OrgId)));
    }
}
