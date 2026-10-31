using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Web;
using Xunit;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class ClaudeConsoleRowTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private const string OrgsOk =
        "{\"status\":\"ok\",\"body\":\"[{\\\"uuid\\\":\\\"2f1c8a90-4b3e-4c1a-9d77-0b6a5e3f21cc\\\",\\\"capabilities\\\":[\\\"api\\\"]}]\",\"lastActiveOrg\":null}";

    private const string NotSignedIn = "{\"status\":\"not_signed_in\"}";

    private readonly List<DisposableTestDirectory> _directories = [];

    public void Dispose()
    {
        WebViewHost.ClearRootOverrideForTests();
        foreach (var directory in _directories)
            directory.Dispose();
    }

    private string TempDirectory()
    {
        var directory = TestPaths.CreateDisposableDirectory("ai-usage-console-row");
        _directories.Add(directory);
        return directory;
    }

    private static string Money(decimal amount) => new ClaudeApiCredit(amount, "USD").FormatAmount(LocalizationService.Instance.DisplayCulture);

    private static string CreditsOf(int cents) => "{\"status\":\"ok\",\"body\":\"{\\\"amount\\\":" + cents + "}\"}";

    private static string Run(string script, string credits) =>
        script.Contains("'/api/organizations'", StringComparison.Ordinal) ? OrgsOk : credits;

    private sealed class Owner : IAsyncDisposable
    {
        public int Disposed;

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

    // ---- status text per state ----

    [Fact]
    public void A_console_balance_reads_signed_in_with_the_balance()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        tile.SetApiCredit(new ClaudeApiCredit(12.34m, "USD"), ClaudeCreditRoute.Console);

        var loc = LocalizationService.Instance;
        Assert.Equal(loc.Format("Settings.ConsoleStatus.Balance", Money(12.34m)), tile.ConsoleStatusText);
        Assert.Contains(Money(12.34m), tile.ConsoleStatusText, StringComparison.Ordinal);
        Assert.True(tile.ShowConsoleSignOut);
        Assert.False(tile.ShowConsoleSignIn);
    }

    [Fact]
    public void A_balance_through_the_claude_sign_in_says_so_and_offers_no_console_sign_out()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        tile.SetApiCredit(new ClaudeApiCredit(12.34m, "USD"), ClaudeCreditRoute.ClaudeSignIn);

        var loc = LocalizationService.Instance;
        Assert.Equal(loc.Format("Settings.ConsoleStatus.ViaClaude", Money(12.34m)), tile.ConsoleStatusText);
        Assert.NotEqual(loc.Format("Settings.ConsoleStatus.Balance", Money(12.34m)), tile.ConsoleStatusText);
        Assert.False(tile.ShowConsoleSignOut);
        Assert.False(tile.ShowConsoleSignIn);
    }

    [Fact]
    public void A_zero_balance_is_still_written_out_in_the_status()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        tile.SetApiCredit(new ClaudeApiCredit(0m, "USD"), ClaudeCreditRoute.Console);

        Assert.Contains(Money(0m), tile.ConsoleStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_session_or_balance_the_row_says_not_signed_in_and_offers_the_sign_in()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");

        Assert.Equal(LocalizationService.Instance["Settings.ConsoleStatus.SignedOut"], tile.ConsoleStatusText);
        Assert.True(tile.ShowConsoleRow);
        Assert.True(tile.ShowConsoleSignIn);
        Assert.False(tile.ShowConsoleSignOut);
    }

    [Fact]
    public void A_console_session_without_a_balance_says_so_and_offers_the_sign_out()
    {
        var tile = new ProviderTileViewModel("claude", "Claude") { HasConsoleSession = true };

        Assert.Equal(LocalizationService.Instance["Settings.ConsoleStatus.NoBalance"], tile.ConsoleStatusText);
        Assert.False(tile.ShowConsoleSignIn);
        Assert.True(tile.ShowConsoleSignOut);
    }

    [Fact]
    public void Dropping_the_balance_returns_the_row_to_not_signed_in()
    {
        var tile = new ProviderTileViewModel("claude", "Claude");
        tile.SetApiCredit(new ClaudeApiCredit(5m, "USD"), ClaudeCreditRoute.Console);
        var raised = new List<string?>();
        tile.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tile.SetApiCredit(null);

        Assert.Equal(LocalizationService.Instance["Settings.ConsoleStatus.SignedOut"], tile.ConsoleStatusText);
        Assert.True(tile.ShowConsoleSignIn);
        Assert.False(tile.ShowConsoleSignOut);
        Assert.Contains(nameof(ProviderTileViewModel.ConsoleStatusText), raised);
        Assert.Contains(nameof(ProviderTileViewModel.ShowConsoleSignOut), raised);
    }

    [Fact]
    public void A_local_only_or_other_account_shows_no_console_row()
    {
        Assert.False(new ProviderTileViewModel("claude", "Claude") { LocalOnly = true }.ShowConsoleRow);
        Assert.False(new ProviderTileViewModel("codex", "Codex").ShowConsoleRow);
        Assert.True(new ProviderTileViewModel("claude#2", "Claude", "claude").ShowConsoleRow);
    }

    // ---- the credit source ----

    [Fact]
    public async Task The_route_that_delivered_the_balance_is_remembered()
    {
        var site = new ClaudeConsoleCreditSource((s, _) => Task.FromResult(Run(s, CreditsOf(900))), null);
        var console = new ClaudeConsoleCreditSource(
            (_, _) => Task.FromResult(NotSignedIn),
            () => new ClaudeConsoleCreditSource.ConsoleRunner((s, _) => Task.FromResult(Run(s, CreditsOf(700))), new Owner()));

        await site.RefreshIfDueAsync();
        await console.RefreshIfDueAsync();

        Assert.Equal(ClaudeCreditRoute.ClaudeSignIn, site.CurrentRoute);
        Assert.Equal(ClaudeCreditRoute.Console, console.CurrentRoute);
    }

    [Fact]
    public async Task Disconnecting_the_console_closes_its_runner_clears_its_balance_and_stops_its_reads()
    {
        var owner = new Owner();
        var created = 0;
        var source = new ClaudeConsoleCreditSource(
            (_, _) => Task.FromResult(NotSignedIn),
            () =>
            {
                created++;
                return new ClaudeConsoleCreditSource.ConsoleRunner((s, _) => Task.FromResult(Run(s, CreditsOf(700))), owner);
            });
        var changes = 0;
        source.Changed += () => changes++;
        await source.RefreshIfDueAsync();
        Assert.Equal(7.00m, source.Current!.Amount);

        await source.DisconnectConsoleAsync();

        Assert.Equal(1, owner.Disposed);
        Assert.Null(source.Current);
        Assert.Equal(ClaudeCreditRoute.None, source.CurrentRoute);
        Assert.Equal(2, changes);

        // Nothing reads the console again until a new sign-in is announced.
        await source.RefreshIfDueAsync();
        Assert.Null(source.Current);
        Assert.Equal(1, created);

        await source.ReadNowAsync();
        Assert.Equal(7.00m, source.Current!.Amount);
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task Disconnecting_the_console_keeps_a_balance_the_claude_sign_in_delivered()
    {
        var source = new ClaudeConsoleCreditSource((s, _) => Task.FromResult(Run(s, CreditsOf(900))), null);
        await source.RefreshIfDueAsync();

        await source.DisconnectConsoleAsync();

        Assert.Equal(9.00m, source.Current!.Amount);
        Assert.Equal(ClaudeCreditRoute.ClaudeSignIn, source.CurrentRoute);
    }

    // ---- the sign-out ----

    private (MainViewModel Vm, ClaudeProvider Claude, Owner Console) BuildClaude()
    {
        var owner = new Owner();
        var source = new ClaudeConsoleCreditSource(
            (_, _) => Task.FromResult(NotSignedIn),
            () => new ClaudeConsoleCreditSource.ConsoleRunner((s, _) => Task.FromResult(Run(s, CreditsOf(1234))), owner));
        var settings = new AppSettings();
        var webSource = new WebUsageSource(
            new WebSessionDescriptor("claude", "https://claude.ai/", "https://claude.ai/login", ["claude.ai"], "claude"),
            new ClaudeUsageEndpoint(), (_, _) => Task.FromResult(NotSignedIn));
        var claude = new ClaudeProvider(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "does-not-exist"), now: null, webSource, settings, _ => { })
        {
            CreditSource = source,
        };
        var vm = new MainViewModel(new SettingsStore(TempDirectory()), settings, [claude], new HistoryStore(TempDirectory(), () => Now),
            notifications: new NotificationService(TempDirectory()));
        return (vm, claude, owner);
    }

    [Fact]
    public async Task Signing_out_of_the_console_drops_the_balance_and_the_console_session_only()
    {
        var (vm, claude, owner) = BuildClaude();
        var accountSignOuts = 0;
        vm.SignOutWebView = _ => { accountSignOuts++; return SignOutResult.Removed; };
        var deleted = new List<string>();
        vm.SignOutConsoleWebView = key => { deleted.Add(key); return SignOutResult.Removed; };
        var profileThere = true;
        vm.ConsoleProfileExists = _ => profileThere;
        var tile = vm.Tiles.Single();
        await claude.ReadApiCreditNowAsync();
        Assert.NotEqual("", tile.ApiCreditText);
        Assert.Equal(ClaudeCreditRoute.Console, claude.CurrentApiCreditRoute);

        profileThere = false;
        var result = await vm.SignOutConsoleAsync("claude");

        Assert.True(result);
        Assert.Equal(["claude"], deleted);
        Assert.Equal(0, accountSignOuts);
        Assert.Equal(1, owner.Disposed);
        Assert.Null(claude.CurrentApiCredit);
        Assert.False(tile.ShowApiCredit);
        Assert.Equal("", tile.ApiCreditText);
        Assert.False(tile.HasConsoleSession);
        Assert.True(tile.ShowConsoleSignIn);
        Assert.False(tile.ShowConsoleSignOut);
        Assert.Equal(LocalizationService.Instance["Settings.ConsoleStatus.SignedOut"], tile.ConsoleStatusText);
        Assert.False(vm.IsSigningOut("claude"));
    }

    [Fact]
    public async Task A_console_folder_that_stays_locked_reports_failure_but_still_drops_the_balance()
    {
        var (vm, claude, _) = BuildClaude();
        vm.SignOutRetryDelay = TimeSpan.Zero;
        var attempts = 0;
        vm.SignOutConsoleWebView = _ => { attempts++; return SignOutResult.StillOpen; };
        vm.ConsoleProfileExists = _ => true;
        await claude.ReadApiCreditNowAsync();

        var result = await vm.SignOutConsoleAsync("claude");

        Assert.False(result);
        Assert.Equal(5, attempts);
        Assert.Null(claude.CurrentApiCredit);
        Assert.True(vm.Tiles.Single().HasConsoleSession);
    }

    [Fact]
    public async Task Signing_out_of_the_console_deletes_only_the_consoles_own_profile_folder()
    {
        var root = TempDirectory();
        WebViewHost.SetRootOverride(root);
        var (vm, _, _) = BuildClaude();
        var account = WebViewHost.ProfileFolderPath(ProviderRegistry.WebSessionFor("claude").ProfileFolderName);
        var console = WebViewHost.ProfileFolderPath(ProviderRegistry.ConsoleSessionFor("claude").ProfileFolderName);
        var other = WebViewHost.ProfileFolderPath(ProviderRegistry.ConsoleSessionFor("claude#2").ProfileFolderName);
        foreach (var folder in new[] { account, console, other })
        {
            Directory.CreateDirectory(Path.Combine(folder, "Default"));
            File.WriteAllText(Path.Combine(folder, "Default", "Cookies"), "x");
        }

        Assert.NotEqual(account, console);
        var result = await vm.SignOutConsoleAsync("claude");

        Assert.True(result);
        Assert.False(Directory.Exists(console));
        Assert.True(File.Exists(Path.Combine(account, "Default", "Cookies")));
        Assert.True(File.Exists(Path.Combine(other, "Default", "Cookies")));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void Signing_out_of_the_claude_account_removes_the_console_folder_too()
    {
        var root = TempDirectory();
        WebViewHost.SetRootOverride(root);
        var account = WebViewHost.ProfileFolderPath(ProviderRegistry.WebSessionFor("claude").ProfileFolderName);
        var console = WebViewHost.ProfileFolderPath(ProviderRegistry.ConsoleSessionFor("claude").ProfileFolderName);
        Directory.CreateDirectory(account);
        Directory.CreateDirectory(console);
        var (vm, _, _) = BuildClaude();

        var result = vm.SignOutWebView("claude");

        Assert.Equal(SignOutResult.Removed, result);
        Assert.False(Directory.Exists(account));
        Assert.False(Directory.Exists(console));
    }

    [Fact]
    public async Task An_unknown_or_non_claude_account_has_nothing_to_sign_out()
    {
        var (vm, _, _) = BuildClaude();

        Assert.False(await vm.SignOutConsoleAsync("claude#2"));
        Assert.False(await vm.SignOutConsoleAsync("codex"));
    }
}
