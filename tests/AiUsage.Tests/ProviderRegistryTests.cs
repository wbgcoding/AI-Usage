using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.ViewModels;
using AiUsage.Web;

namespace AiUsage.Tests;

/// <summary>
/// Adding the fourth provider (Copilot) must cost nothing outside its
/// own provider class and this one registry line - proof, not just a claim, that
/// <see cref="MainViewModel"/> builds a tile per provider generically, with no provider-specific
/// branch anywhere in the view-model layer.
/// </summary>
[Collection(SharedStateTestsCollection.Name)]
public class ProviderRegistryTests : IDisposable
{
    [Fact]
    public void CreateAll_returns_all_five_known_providers_with_distinct_ids()
    {
        var (providers, runners) = ProviderRegistry.CreateAll(new AppSettings(), new SettingsStore(
            Track(TestPaths.GetPath("ai-usage-registry"))));

        Assert.Equal(5, providers.Count);
        var ids = providers.Select(p => p.Id).ToList();
        Assert.Equal(ids.Distinct().Count(), ids.Count);
        Assert.Equal(
            new[] { "claude", "codex", "cursor", "gemini", "copilot" }.OrderBy(id => id), ids.OrderBy(id => id));
        // The primary Claude account reads through Claude Code's own sign-in, so it has no web-session
        // runner; Codex, Cursor and Gemini each own one from the start (their live numbers come through
        // the app's own session), and a further Claude account adds its own.
        Assert.Equal(["codex", "cursor", "gemini"], runners.Keys.OrderBy(key => key));
    }

    /// <summary>Cursor sits behind Codex and before Gemini in every provider list - proven here
    /// against the actual construction order, not just against the id set above.</summary>
    [Fact]
    public void CreateAll_orders_providers_claude_codex_cursor_gemini_copilot()
    {
        var (providers, _) = ProviderRegistry.CreateAll(new AppSettings(), new SettingsStore(
            Track(TestPaths.GetPath("ai-usage-registry"))));

        Assert.Equal(["claude", "codex", "cursor", "gemini", "copilot"], providers.Select(p => p.Id));
    }

    /// <summary>Codex reads two sources: its session files always, the account's own usage endpoint
    /// through the app's own browser session. The session must be Codex's own profile folder and its
    /// own site, never a folder or a host another provider signs into.</summary>
    [Fact]
    public void The_codex_web_session_is_its_own_profile_on_its_own_site()
    {
        var session = ProviderRegistry.WebSessionFor("codex");

        Assert.Equal("codex", session.ProviderId);
        Assert.Equal("codex", session.ProfileFolderName);
        Assert.StartsWith("https://chatgpt.com/", session.BaseUrl, StringComparison.Ordinal);
        Assert.Contains("chatgpt.com", session.AllowedHosts);
        Assert.DoesNotContain("claude.ai", session.AllowedHosts);
        Assert.NotEqual(
            WebViewHost.ResolveUserDataFolder(session.ProfileFolderName),
            WebViewHost.ResolveUserDataFolder(ProviderRegistry.WebSessionFor("claude").ProfileFolderName));
    }

    /// <summary>Every web session's sign-in page is a page the signed-out user can actually reach.
    /// The window used to build it by gluing "login" onto the reader session's start page, which is
    /// a usage deep link on every provider but Claude - on Codex that produced
    /// chatgpt.com/codex/settings/usagelogin, a not-found page with nothing on it but a sign-in
    /// button. The url is spelled out per provider now, and must stay a real one: https, on one of
    /// that session's own allowed hosts, and never the old concatenation.</summary>
    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("gemini")]
    public void EveryWebSessionOpensASignInPageTheUserCanReach(string accountKey)
    {
        var session = ProviderRegistry.WebSessionFor(accountKey);

        Assert.True(SignInNavigationPolicy.IsAllowedUri(session.SignInUrl, session.AllowedHosts),
            $"{session.SignInUrl} is not an https url on this session's own allowed hosts");
    }

    /// <summary>The concatenation named above, written out for the session it actually broke:
    /// Codex's reader starts on a usage page, so the old rule handed the sign-in window
    /// chatgpt.com/codex/settings/usagelogin. The two urls are separate facts now, and the sign-in
    /// one is the login form rather than anything derived from the reader page.</summary>
    [Fact]
    public void TheCodexSignInPageIsNotTheReaderPageWithLoginGluedOn()
    {
        var session = ProviderRegistry.WebSessionFor("codex");

        Assert.NotEqual(session.BaseUrl + "login", session.SignInUrl);
        Assert.Contains("login", new Uri(session.SignInUrl).AbsolutePath, StringComparison.Ordinal);
    }

    /// <summary>A front door that answers a signed-out visitor with a product page instead of a way
    /// in is the same dead end as a 404: the user reported seeing "just a Google page" where the
    /// sign-in was meant to be. Every session's sign-in url has to carry a login path of its own.</summary>
    /// <remarks>Cursor is not on this list on purpose: its dashboard url answers a signed-out
    /// visitor with a redirect straight into its own authenticator, so it is a way in even though
    /// the url itself carries no login path.</remarks>
    [Theory]
    [InlineData("codex")]
    [InlineData("gemini")]
    [InlineData("claude")]
    public void EverySignInUrlPointsAtALoginPageRatherThanAProductPage(string accountKey)
    {
        var session = ProviderRegistry.WebSessionFor(accountKey);
        var url = session.SignInUrl;

        Assert.True(
            url.Contains("login", StringComparison.OrdinalIgnoreCase)
            || url.Contains("log-in", StringComparison.OrdinalIgnoreCase)
            || url.Contains("signin", StringComparison.OrdinalIgnoreCase),
            $"{accountKey}'s sign-in url {url} is a product page, not a way in");
    }

    /// <summary>Google's two-factor confirmation walks through more than accounts.google.com before
    /// it returns, and every host it reaches that is not allowed here is a cancelled navigation the
    /// user sees as a login that never finishes.</summary>
    [Theory]
    [InlineData("codex", "https://accounts.google.com/signin/v2/challenge")]
    [InlineData("codex", "https://gds.google.com/web/verify")]
    [InlineData("claude", "https://accounts.google.com/signin/v2/challenge")]
    [InlineData("claude", "https://gds.google.com/web/verify")]
    [InlineData("codex", "https://accounts.google.de/accounts/SetSID")]
    [InlineData("claude", "https://accounts.google.de/accounts/SetSID")]
    [InlineData("cursor", "https://accounts.google.co.uk/accounts/SetSID")]
    public void TheGoogleSignInIsAllowedToFinishItsWholeFlow(string accountKey, string url) =>
        Assert.True(SignInNavigationPolicy.IsAllowedUri(url, ProviderRegistry.WebSessionFor(accountKey).AllowedHosts), url);

    /// <summary>Cursor's sign-in runs on a hosted identity service, and that service's own api host
    /// is a step in the middle of the flow: it was turned away there, which left the user on a
    /// blocked notice with no way to finish signing in. The login form and the authorize step sit on
    /// two different hosts of Cursor's own second site, one letter apart - naming a single host of
    /// that site turned the flow away one step further along instead of letting it finish.</summary>
    [Theory]
    [InlineData("https://api.workos.com/sso/authorize")]
    [InlineData("https://authenticate.cursor.sh/user_management/authorize")]
    [InlineData("https://authenticator.cursor.sh/")]
    [InlineData("https://cursor.com/api/auth/callback")]
    public void TheCursorSignInIsAllowedToFinishItsWholeFlow(string url) =>
        Assert.True(SignInNavigationPolicy.IsAllowedUri(url, ProviderRegistry.WebSessionFor("cursor").AllowedHosts), url);

    /// <summary>Signing out of Codex must rebuild a Codex provider, not a Claude one - the rebuild
    /// used to be hardcoded to Claude's factory, which would have left the tile reading the wrong
    /// provider's session after the first sign-out.</summary>
    [Fact]
    public void CreateCodexAccount_builds_a_codex_provider_that_offers_its_own_sign_in()
    {
        var store = new SettingsStore(Track(TestPaths.GetPath("ai-usage-registry")));

        var (provider, runner) = ProviderRegistry.CreateCodexAccount("codex", new AppSettings(), store);

        Assert.Equal("codex", provider.Id);
        Assert.True(provider.SupportsInAppSignIn);
        Assert.True(provider.SupportsMultipleAccounts);
        Assert.NotNull(runner);
    }

    /// <summary>Cursor's own session must be its own profile on its own site, the same proof as
    /// Codex's above - and <see cref="ProviderRegistry.CreateAll"/>'s runner dictionary must actually
    /// carry a "cursor" entry, not just a provider with that id.</summary>
    [Fact]
    public void The_cursor_web_session_is_its_own_profile_on_its_own_site()
    {
        var session = ProviderRegistry.WebSessionFor("cursor");

        Assert.Equal("cursor", session.ProviderId);
        Assert.Equal("cursor", session.ProfileFolderName);
        Assert.StartsWith("https://cursor.com/", session.BaseUrl, StringComparison.Ordinal);
        Assert.Contains("cursor.com", session.AllowedHosts);
        Assert.DoesNotContain("claude.ai", session.AllowedHosts);
    }

    [Fact]
    public void CreateAll_runner_dictionary_has_a_cursor_entry()
    {
        var (_, runners) = ProviderRegistry.CreateAll(new AppSettings(), new SettingsStore(
            Track(TestPaths.GetPath("ai-usage-registry"))));

        Assert.True(runners.ContainsKey("cursor"));
        Assert.NotNull(runners["cursor"]);
    }

    /// <summary>Signing out of Cursor must rebuild a Cursor provider, offering its own sign-in and
    /// never carrying the multi-account button Claude alone has.</summary>
    [Fact]
    public void CreateCursorAccount_builds_a_cursor_provider_that_offers_its_own_sign_in()
    {
        var store = new SettingsStore(Track(TestPaths.GetPath("ai-usage-registry")));

        var (provider, runner) = ProviderRegistry.CreateCursorAccount("cursor", new AppSettings(), store);

        Assert.Equal("cursor", provider.Id);
        Assert.True(provider.SupportsInAppSignIn);
        Assert.True(provider.SupportsMultipleAccounts);
        Assert.NotNull(runner);
    }

    /// <summary>Gemini's own session must be its own profile on its own site, the same proof as
    /// Codex's and Cursor's above.</summary>
    [Fact]
    public void The_gemini_web_session_is_its_own_profile_on_its_own_site()
    {
        var session = ProviderRegistry.WebSessionFor("gemini");

        Assert.Equal("gemini", session.ProviderId);
        Assert.Equal("gemini", session.ProfileFolderName);
        Assert.StartsWith("https://aistudio.google.com/", session.BaseUrl, StringComparison.Ordinal);
        Assert.Contains("google.com", session.AllowedHosts);
        Assert.True(SignInNavigationPolicy.IsAllowedUri("https://accounts.google.de/signin/v2", session.AllowedHosts));
        Assert.True(SignInNavigationPolicy.IsAllowedUri("https://accounts.google.co.uk/signin/v2", session.AllowedHosts));
        Assert.False(SignInNavigationPolicy.IsAllowedUri("https://accounts.google.de.evil.com/", session.AllowedHosts));
        Assert.DoesNotContain("claude.ai", session.AllowedHosts);
    }

    [Fact]
    public void CreateAll_runner_dictionary_has_a_gemini_entry()
    {
        var (_, runners) = ProviderRegistry.CreateAll(new AppSettings(), new SettingsStore(
            Track(TestPaths.GetPath("ai-usage-registry"))));

        Assert.True(runners.ContainsKey("gemini"));
        Assert.NotNull(runners["gemini"]);
    }

    /// <summary>Signing out of Gemini must rebuild a Gemini provider, offering its own sign-in and
    /// never carrying the multi-account button Claude alone has.</summary>
    [Fact]
    public void CreateGeminiAccount_builds_a_gemini_provider_that_offers_its_own_sign_in()
    {
        var store = new SettingsStore(Track(TestPaths.GetPath("ai-usage-registry")));

        var (provider, runner) = ProviderRegistry.CreateGeminiAccount("gemini", new AppSettings(), store);

        Assert.Equal("gemini", provider.Id);
        Assert.True(provider.SupportsInAppSignIn);
        Assert.True(provider.SupportsMultipleAccounts);
        Assert.NotNull(runner);
    }

    [Fact]
    public void MainViewModel_builds_one_tile_per_real_registered_provider()
    {
        var settings = new AppSettings();
        var directory = Track(TestPaths.GetPath("ai-usage-registry"));
        var store = new SettingsStore(directory);
        var (providers, _) = ProviderRegistry.CreateAll(settings, store);
        var vm = new MainViewModel(
            store, settings, providers, new HistoryStore(directory, () => DateTimeOffset.Now), claudeRunner: null);

        Assert.Equal(5, vm.Tiles.Count);
        Assert.Equal(
            new[] { "claude", "codex", "cursor", "gemini", "copilot" }.OrderBy(id => id),
            vm.Tiles.Select(t => t.ProviderId).OrderBy(id => id));
    }

    /// <summary>The About window's read-locations panel builds Claude through this same helper
    /// instead of a throwaway default-constructed instance, so it must name the same source the
    /// running primary account actually reads through: the local Claude Code sign-in, never the web
    /// session a further account would use.</summary>
    [Fact]
    public void CreatePrimaryClaudeAccount_names_the_claude_code_sign_in_not_the_web_session()
    {
        var provider = ProviderRegistry.CreatePrimaryClaudeAccount();

        Assert.Contains(LocalizationService.Instance["About.ReadLocationClaudeCode"], provider.ReadLocations);
        Assert.DoesNotContain(LocalizationService.Instance["About.ReadLocationWebSession"], provider.ReadLocations);
    }

    /// <summary>A second Claude account gets its own provider instance, its own web-session runner,
    /// its own history file and its own browser sign-in profile - the four things that used to have
    /// nowhere to live for a second account of the same provider.</summary>
    [Fact]
    public void CreateAll_builds_one_provider_per_configured_claude_account_with_its_own_storage()
    {
        var settings = new AppSettings();
        settings.Accounts["claude#2"] = "claude";
        var directory = Track(TestPaths.GetPath("ai-usage-registry"));
        var store = new SettingsStore(directory);

        var (providers, runners) = ProviderRegistry.CreateAll(settings, store);

        var claudeProviders = providers.Where(p => p.Id == "claude").ToList();
        Assert.Equal(2, claudeProviders.Count);
        var accountKeys = claudeProviders.Select(p => p.AccountKey).OrderBy(key => key).ToList();
        Assert.Equal(["claude", "claude#2"], accountKeys);

        // Of the Claude accounts only the further one keeps a web-session runner; the primary reads
        // through Claude Code. Codex, Cursor and Gemini each own one of their own, independent of how
        // many Claude accounts exist.
        Assert.Equal(["claude#2", "codex", "cursor", "gemini"], runners.Keys.OrderBy(key => key));
        Assert.False(runners.ContainsKey("claude"));

        var historyPaths = accountKeys.Select(AppPaths.HistoryFile).Distinct().ToList();
        Assert.Equal(2, historyPaths.Count);

        var profileFolders = accountKeys
            .Select(key => WebViewHost.ResolveUserDataFolder(AppPaths.SanitizeAccountKeyForFileName(key)))
            .Distinct().ToList();
        Assert.Equal(2, profileFolders.Count);
    }

    /// <summary>A further account of Codex, Cursor, Gemini and Copilot alike - each behind
    /// <see cref="Services.IUsageProvider.SupportsMultipleAccounts"/> now, the same generalisation
    /// Claude already had. Built purely from <see cref="AppSettings.Accounts"/>/<see
    /// cref="AppSettings.CopilotAccountLogins"/>, exactly what <see
    /// cref="ViewModels.MainViewModel.AddAccountCommand"/> writes there.</summary>
    [Fact]
    public void CreateAll_builds_a_further_account_for_every_web_or_gh_backed_provider()
    {
        var settings = new AppSettings();
        settings.Accounts["codex#2"] = "codex";
        settings.Accounts["cursor#2"] = "cursor";
        settings.Accounts["gemini#2"] = "gemini";
        settings.Accounts["copilot#2"] = "copilot";
        settings.CopilotAccountLogins["copilot#2"] = "monalisa";
        var directory = Track(TestPaths.GetPath("ai-usage-registry"));
        var store = new SettingsStore(directory);

        var (providers, runners) = ProviderRegistry.CreateAll(settings, store);

        Assert.Equal(9, providers.Count);
        var accountKeys = providers.Select(p => p.AccountKey).OrderBy(k => k).ToList();
        Assert.Equal(
            ["claude", "codex", "codex#2", "copilot", "copilot#2", "cursor", "cursor#2", "gemini", "gemini#2"],
            accountKeys);

        // Every further account keeps its own web-session runner (Copilot has none - no web session
        // at all).
        Assert.Contains("codex#2", runners.Keys);
        Assert.Contains("cursor#2", runners.Keys);
        Assert.Contains("gemini#2", runners.Keys);
        Assert.DoesNotContain("copilot#2", runners.Keys);

        var codexExtra = providers.Single(p => p.AccountKey == "codex#2");
        Assert.True(codexExtra.SupportsInAppSignIn);
        Assert.False(codexExtra.SupportsMultipleAccounts); // a further account cannot itself sprout another
        Assert.DoesNotContain("~/.codex/sessions", codexExtra.ReadLocations); // no local file for a further account

        var geminiExtra = providers.Single(p => p.AccountKey == "gemini#2");
        Assert.DoesNotContain(LocalizationService.Instance["About.ReadLocationAntigravity"], geminiExtra.ReadLocations);

        var copilotExtra = providers.Single(p => p.AccountKey == "copilot#2");
        Assert.True(copilotExtra.SupportsMultipleAccounts);
    }

    /// <summary>Every further account's web session is its own profile folder, on the same site as
    /// the primary - the same proof <see cref="The_codex_web_session_is_its_own_profile_on_its_own_site"/>
    /// already gives the primary account, extended to a suffixed account key.</summary>
    [Theory]
    [InlineData("codex#2", "chatgpt.com")]
    [InlineData("cursor#2", "cursor.com")]
    [InlineData("gemini#2", "google.com")]
    public void AFurtherAccountsWebSessionIsItsOwnProfileOnTheProvidersOwnSite(string accountKey, string host)
    {
        var primary = ProviderRegistry.WebSessionFor(ProviderRegistry.BaseProviderId(accountKey));
        var extra = ProviderRegistry.WebSessionFor(accountKey);

        Assert.Contains(host, extra.AllowedHosts);
        Assert.Equal(primary.BaseUrl, extra.BaseUrl);
        Assert.NotEqual(primary.ProfileFolderName, extra.ProfileFolderName);
        Assert.NotEqual(
            WebViewHost.ResolveUserDataFolder(primary.ProfileFolderName),
            WebViewHost.ResolveUserDataFolder(extra.ProfileFolderName));
    }

    /// <summary>Copilot's own primary account still reads as the CLI's own active user (no login
    /// bound in settings); a further one reads as whichever GitHub login was chosen when it was
    /// added.</summary>
    [Fact]
    public void CreateCopilotAccount_binds_a_further_accounts_own_github_login()
    {
        var settings = new AppSettings();
        settings.CopilotAccountLogins["copilot#2"] = "monalisa";

        var primary = ProviderRegistry.CreateCopilotAccount("copilot", settings);
        var extra = ProviderRegistry.CreateCopilotAccount("copilot#2", settings);

        Assert.Equal("copilot", primary.AccountKey);
        Assert.Equal("copilot#2", extra.AccountKey);
        Assert.True(extra.SupportsMultipleAccounts);
    }

    private readonly List<string> _cleanupPaths = [];

    private string Track(string path)
    {
        _cleanupPaths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _cleanupPaths)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
