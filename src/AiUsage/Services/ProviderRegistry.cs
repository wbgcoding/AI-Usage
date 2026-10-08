using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Storage;
using AiUsage.Web;

namespace AiUsage.Services;

/// <summary>
/// The one place that creates every provider. Adding a provider is one line here plus the class
/// itself - nothing else in the app knows a provider by name. <see cref="AppSettings.Accounts"/> is
/// the one source of truth for how many Claude accounts exist - every entry in it gets its own
/// <see cref="ClaudeProvider"/> instance and its own web session, so this file never has to compare
/// against a provider id by name to know how many to build.
/// </summary>
public static class ProviderRegistry
{
    /// <summary>Codex's own session: the account page the sign-in window opens, the hosts its login
    /// may pass through, and its own browser profile subfolder. The foreign hosts are there for the
    /// same reason accounts.google.com is on Claude's list - the provider's own login leaves its site
    /// for whichever identity provider the user picked and comes back, and without them that whole
    /// path is indistinguishable from a session hijack attempt and gets blocked outright. Subdomains
    /// are covered by the host match itself (see <see cref="Web.SignInNavigationPolicy"/>), so the
    /// list names each site once.</summary>
    internal static readonly WebSessionDescriptor CodexWebSession = new(
        ProviderId: "codex",
        BaseUrl: "https://chatgpt.com/codex/settings/usage",
        // The login form itself, not the usage page above and not the chat front door either:
        // signed out, the deep link answers with a not-found page carrying nothing but a sign-in
        // button, and the front door answers with its landing page, where the user still has to find
        // the log-in button themselves. This address is the one the site's own sign-in link points
        // at, so the login request carries the parameters the identity service expects - an address
        // on the identity service alone, without them, is not a form anyone can sign in on.
        SignInUrl: "https://chatgpt.com/auth/login",
        // google.com rather than accounts.google.com alone: the Google login hands a two-factor
        // confirmation on to further hosts of its own (gds.google.com, myaccount.google.com) and only
        // then returns, and each one not on this list is a cancelled navigation that leaves the user
        // on a page that never finishes. youtube.com is on this list and on every other one carrying
        // google.com for exactly that reason: the last step of a Google sign-in confirms the account
        // on accounts.youtube.com before handing the session back, and being turned away there ends
        // the sign-in one step before the end. Only whole pages need naming here - the check runs on
        // top-level navigations, never on a page's own scripts, styles or frames.
        AllowedHosts: ["chatgpt.com", "openai.com", "google.com", "youtube.com", Web.SignInNavigationPolicy.GoogleCountryAccounts, "appleid.apple.com", "login.microsoftonline.com"],
        ProfileFolderName: "codex");

    /// <summary>Cursor's own session: its own usage page, its own front door (never derived from the
    /// usage page - gluing "login" onto a usage deep link produced a not-found page on the other
    /// web-backed provider, so this stays a separate field there and here), and its own browser
    /// profile subfolder. The foreign hosts are there for the same reason accounts.google.com is on
    /// Claude's list: Cursor's own sign-in leaves cursor.com before returning, and without them that
    /// whole path looks like a session hijack and is blocked outright. workos.com is the identity
    /// service behind Cursor's own login - the sign-in calls api.workos.com partway through and
    /// stopped dead there. google.com is named as the whole site because Google's two-factor
    /// confirmation travels through more of its own hosts than accounts.google.com alone, and
    /// cursor.sh for the same reason on Cursor's own side: the login itself sits on one host of that
    /// site (authenticate.cursor.sh) while the authorize step uses another (authenticator.cursor.sh),
    /// and naming only one of the two is what blocked the sign-in halfway through.</summary>
    internal static readonly WebSessionDescriptor CursorWebSession = new(
        ProviderId: "cursor",
        BaseUrl: "https://cursor.com/dashboard/usage",
        SignInUrl: "https://cursor.com/dashboard",
        AllowedHosts: ["cursor.com", "cursor.sh", "workos.com", "google.com", "youtube.com", Web.SignInNavigationPolicy.GoogleCountryAccounts, "github.com"],
        ProfileFolderName: "cursor");

    /// <summary>Gemini's own session: its own usage page on AI Studio, its own front door, and its
    /// own browser profile subfolder. <c>google.com</c> is named as the whole site rather than
    /// <c>aistudio.google.com</c> alone for the same reason it is on Codex's and Claude's lists -
    /// Google's own two-factor confirmation travels through more of its own hosts than the front
    /// door alone, and <c>aistudio.google.com</c> itself already matches as a subdomain of the
    /// listed site (see <see cref="Web.SignInNavigationPolicy.IsAllowedHost"/>), so nothing else
    /// needs naming here.</summary>
    internal static readonly WebSessionDescriptor GeminiWebSession = new(
        ProviderId: "gemini",
        BaseUrl: "https://aistudio.google.com/usage",
        // Google account sign-in with the usage page as its return address, not aistudio.google.com
        // itself: signed out, that address answers with the product welcome page, which carries no
        // visible way in and is exactly the "just a Google page, no sign-in" this used to show.
        SignInUrl: "https://accounts.google.com/ServiceLogin?continue=https%3A%2F%2Faistudio.google.com%2Fusage",
        // The country account hosts too: after the login Google sets the session cookies through
        // accounts.google.<the user's country domain>, which google.com above does not cover.
        AllowedHosts: ["google.com", "youtube.com", Web.SignInNavigationPolicy.GoogleCountryAccounts],
        ProfileFolderName: "gemini");

    /// <summary>Which provider's fixed session shape a "&lt;provider&gt;#&lt;n&gt;"-style account key
    /// belongs to - everything before the first "#", or the whole key for a first/only account (see
    /// <see cref="Models.IUsageProvider.AccountKey"/>).</summary>
    internal static string BaseProviderId(string accountKey) =>
        accountKey.IndexOf('#') is var hashIndex && hashIndex >= 0 ? accountKey[..hashIndex] : accountKey;

    /// <summary>The session one account key signs into: Codex's, Cursor's, Gemini's or Claude's own
    /// fixed shape (base URL, sign-in page, allowed hosts), always with its OWN browser profile
    /// subfolder (see <see cref="AppPaths.SanitizeAccountKeyForFileName"/>) - a first/only account
    /// keeps the provider's plain folder name, a further one gets its own so two accounts of the same
    /// provider never share a session. The one place both the sign-in window and the sign-out path
    /// ask, so neither has to know which provider an account key belongs to.</summary>
    internal static WebSessionDescriptor WebSessionFor(string accountKey)
    {
        var baseDescriptor = BaseProviderId(accountKey) switch
        {
            "codex" => CodexWebSession,
            "cursor" => CursorWebSession,
            "gemini" => GeminiWebSession,
            _ => ClaudeWebSession,
        };

        return accountKey == baseDescriptor.ProviderId
            ? baseDescriptor
            : baseDescriptor with
            {
                ProviderId = accountKey,
                ProfileFolderName = AppPaths.SanitizeAccountKeyForFileName(accountKey),
            };
    }

    /// <summary>Claude's own session shape - same fixed URL/allowed-hosts shape the other three
    /// providers' own constants above carry, kept as a plain descriptor (never itself handed out
    /// directly) so <see cref="WebSessionFor"/> can derive every Claude account's own descriptor from
    /// it the same way it derives a further Codex/Cursor/Gemini account's.</summary>
    private static readonly WebSessionDescriptor ClaudeWebSession = new(
        ProviderId: "claude",
        BaseUrl: "https://claude.ai/",
        SignInUrl: "https://claude.ai/login",
        // google.com: Claude's own "Sign in with Google" button leaves claude.ai for Google's real
        // login/consent flow before returning - without it that whole path is indistinguishable from
        // a session hijack attempt and gets blocked outright. Named as the whole site for the same
        // reason as on Codex's list above: the two-factor confirmation travels through more than one
        // Google host.
        AllowedHosts: ["claude.ai", "anthropic.com", "google.com", "youtube.com", Web.SignInNavigationPolicy.GoogleCountryAccounts],
        ProfileFolderName: "claude");

    /// <summary>Builds exactly one Claude account's provider and its own web-session runner -
    /// <paramref name="settings"/>/<paramref name="store"/> back its web fallback (the cached usage
    /// endpoint, AppSettings.WebUsagePaths, and where a freshly discovered one is saved). The one
    /// body <see cref="CreateAll"/> loops over for every configured account, and what
    /// <see cref="ViewModels.MainViewModel.AddAccountCommand"/> calls again for an account added after
    /// startup - never duplicated between the two.</summary>
    internal static (IUsageProvider Provider, WebSessionScriptRunner Runner) CreateClaudeAccount(
        string accountKey, AppSettings settings, SettingsStore store)
    {
        var descriptor = WebSessionFor(accountKey);
        var runner = new WebSessionScriptRunner(descriptor);
        var provider = new ClaudeProvider(
            accountKey, settings, store.RequestSave,
            new WebUsageSource(descriptor, new ClaudeUsageEndpoint(), runner.ExecuteScriptAsync),
            () => TimeSpan.FromMinutes(settings.AttentionMaxAgeMinutes));
        return (provider, runner);
    }

    /// <summary>Codex's provider and its own web-session runner. <paramref name="accountKey"/>'s
    /// primary account ("codex") reads its local session files whether or not anyone ever signs in,
    /// the web session adding the live numbers on top (see <see cref="Providers.CodexProvider"/>); a
    /// further account ("codex#2", ...) is a web session only - the local files on this machine
    /// belong to whichever account is signed into the Codex CLI, never to a further account added
    /// here. Built the same way a Claude account is, so sign-out can rebuild either of them through
    /// one dispatch.</summary>
    internal static (IUsageProvider Provider, WebSessionScriptRunner Runner) CreateCodexAccount(
        string accountKey, AppSettings settings, SettingsStore store, LogService? logService = null)
    {
        var descriptor = WebSessionFor(accountKey);
        var runner = new WebSessionScriptRunner(descriptor);
        var webSource = new WebUsageSource(
            descriptor, new CodexUsageEndpoint(), runner.ExecuteScriptAsync, logService == null ? null : logService.LogInfo);
        var provider = accountKey == CodexWebSession.ProviderId
            ? new CodexProvider(settings, store.RequestSave, webSource, () => TimeSpan.FromMinutes(settings.AttentionMaxAgeMinutes))
            : CodexProvider.ForWebOnlyAccount(accountKey, settings, store.RequestSave, webSource);
        return (provider, runner);
    }

    /// <summary>Cursor's provider and its own web-session runner for one account key - Cursor keeps
    /// no local usage file at all for any account (see <see cref="Providers.CursorProvider"/>), so
    /// every account, primary or further, is a web session only. Built the same way the Claude and
    /// Codex accounts above are, so sign-out can rebuild it through the same dispatch.</summary>
    internal static (IUsageProvider Provider, WebSessionScriptRunner Runner) CreateCursorAccount(
        string accountKey, AppSettings settings, SettingsStore store, LogService? logService = null)
    {
        var descriptor = WebSessionFor(accountKey);
        var runner = new WebSessionScriptRunner(descriptor);
        var provider = new CursorProvider(
            accountKey,
            new WebUsageSource(
                descriptor, new CursorDiscoveryScript(), runner.ExecuteScriptAsync,
                logService == null ? null : logService.LogInfo),
            settings, store.RequestSave);
        return (provider, runner);
    }

    /// <summary>Gemini's provider and its own web-session runner. <paramref name="accountKey"/>'s
    /// primary account ("gemini") reads the Antigravity CLI's own sign-in whether or not anyone ever
    /// signs into the app's own browser session, the web session adding the live numbers on top (see
    /// <see cref="Providers.GeminiProvider"/>); a further account is a web session only - the CLI's
    /// own local sign-in belongs to whichever account it is signed into, never to a further account
    /// added here. Built the same way the Codex account above is, so sign-out can rebuild it through
    /// the same dispatch.</summary>
    internal static (IUsageProvider Provider, WebSessionScriptRunner Runner) CreateGeminiAccount(
        string accountKey, AppSettings settings, SettingsStore store, LogService? logService = null)
    {
        var descriptor = WebSessionFor(accountKey);
        var runner = new WebSessionScriptRunner(descriptor);
        var webSource = new WebUsageSource(
            descriptor, new GeminiUsageEndpoint(), runner.ExecuteScriptAsync, logService == null ? null : logService.LogInfo);
        var provider = accountKey == GeminiWebSession.ProviderId
            ? new GeminiProvider(settings, store.RequestSave, webSource)
            : GeminiProvider.ForWebOnlyAccount(accountKey, settings, store.RequestSave, webSource);
        return (provider, runner);
    }

    /// <summary>The primary account's own provider, built the one way <see cref="CreateAll"/> builds
    /// it (the local Claude Code sign-in, never a web session) - shared with
    /// <see cref="Views.SettingsWindow"/>'s read-locations panel, which used to build a throwaway
    /// default-constructed <c>ClaudeProvider</c> instead and so named the web session even though the
    /// running primary account never reads through it.</summary>
    internal static IUsageProvider CreatePrimaryClaudeAccount(AppSettings? settings = null) =>
        new ClaudeProvider(
            AppSettings.CreateDefaultAccounts().Keys.Single(), ClaudeCodeLogin.FetchAsync, settings,
            settings is null ? null : () => TimeSpan.FromMinutes(settings.AttentionMaxAgeMinutes));

    /// <summary>Every runner is handed back rather than only captured as a method group, so the
    /// caller can own and dispose it (sign-out needs to tear it down before it deletes the browser
    /// profile it still has open).</summary>
    /// <summary>Copilot's own primary provider and, for every account key <see
    /// cref="AppSettings.CopilotAccountLogins"/> names, a further one reading as that already
    /// signed-in GitHub user - never a new sign-in, never a web session (see <see
    /// cref="Providers.CopilotProvider"/>).</summary>
    internal static IUsageProvider CreateCopilotAccount(string accountKey, AppSettings settings) =>
        accountKey == "copilot"
            ? new CopilotProvider()
            : new CopilotProvider(accountKey, settings.CopilotAccountLogins.GetValueOrDefault(accountKey, accountKey));

    public static (IReadOnlyList<IUsageProvider> Providers, IReadOnlyDictionary<string, WebSessionScriptRunner> Runners) CreateAll(
        AppSettings settings, SettingsStore store, LogService? logService = null)
    {
        var providers = new List<IUsageProvider>();
        var runners = new Dictionary<string, WebSessionScriptRunner>();
        var primaryKey = AppSettings.CreateDefaultAccounts().Keys.Single();

        foreach (var (accountKey, providerId) in settings.Accounts)
        {
            if (providerId != "claude")
                continue; // a further account of another provider - built below instead

            // The primary account reads through the sign-in Claude Code already holds on this machine
            // (no web session, so no runner); a further account keeps the app's own web session.
            if (accountKey == primaryKey)
            {
                providers.Add(CreatePrimaryClaudeAccount(settings));
                continue;
            }

            var (provider, runner) = CreateClaudeAccount(accountKey, settings, store);
            providers.Add(provider);
            runners[accountKey] = runner;
        }

        var (codexProvider, codexRunner) = CreateCodexAccount("codex", settings, store, logService);
        providers.Add(codexProvider);
        runners["codex"] = codexRunner;

        var (cursorProvider, cursorRunner) = CreateCursorAccount("cursor", settings, store, logService);
        providers.Add(cursorProvider);
        runners["cursor"] = cursorRunner;

        var (geminiProvider, geminiRunner) = CreateGeminiAccount("gemini", settings, store, logService);
        providers.Add(geminiProvider);
        runners["gemini"] = geminiRunner;

        providers.Add(new CopilotProvider());

        // A further account of Codex, Cursor, Gemini (its own web session) or Copilot (another
        // already signed-in GitHub user) - every entry in settings.Accounts whose key differs from
        // its own provider id (a first/only account's key always equals it, see AppSettings.Accounts).
        foreach (var (accountKey, providerId) in settings.Accounts)
        {
            if (accountKey == providerId)
                continue;

            switch (providerId)
            {
                case "codex":
                    var (codexExtra, codexExtraRunner) = CreateCodexAccount(accountKey, settings, store, logService);
                    providers.Add(codexExtra);
                    runners[accountKey] = codexExtraRunner;
                    break;
                case "cursor":
                    var (cursorExtra, cursorExtraRunner) = CreateCursorAccount(accountKey, settings, store, logService);
                    providers.Add(cursorExtra);
                    runners[accountKey] = cursorExtraRunner;
                    break;
                case "gemini":
                    var (geminiExtra, geminiExtraRunner) = CreateGeminiAccount(accountKey, settings, store, logService);
                    providers.Add(geminiExtra);
                    runners[accountKey] = geminiExtraRunner;
                    break;
                case "copilot":
                    providers.Add(CreateCopilotAccount(accountKey, settings));
                    break;
                // "claude" is already fully covered by the loop above.
            }
        }

        return (providers, runners);
    }
}
