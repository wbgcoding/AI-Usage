using AiUsage.Models;
using AiUsage.Providers;
using AiUsage.Providers.LocalLogin;
using AiUsage.Storage;
using AiUsage.Web;

namespace AiUsage.Services;

/// <summary>
/// The one place that creates every provider. A new provider also needs entries in
/// <see cref="AppSettings.KnownProviderIds"/>, <see cref="ProviderLinks"/> and <see cref="Stats.ChartPalette"/>.
/// <see cref="AppSettings.Accounts"/> decides how many Claude accounts exist: every entry gets its
/// own <see cref="ClaudeProvider"/> instance and its own web session.
/// </summary>
public static class ProviderRegistry
{
    /// <summary>The shortest span between two reads of a provider's remote side (its web session or
    /// its network endpoint), however often the tile ticks.</summary>
    internal static readonly TimeSpan RemoteReadFloor = TimeSpan.FromMinutes(5);

    private static readonly Lazy<IReadOnlyList<ProviderKind>> LazyKinds = new(() =>
    [
        .. AppSettings.KnownProviderIds.Select(id => new ProviderKind(id switch
        {
            "codex" => new CodexProvider(),
            "cursor" => new CursorProvider(),
            "gemini" => new GeminiProvider(),
            "copilot" => new CopilotProvider(),
            "claude" => CreatePrimaryClaudeAccount(),
            _ => throw new InvalidOperationException($"No display provider for the known id '{id}'."),
        })),
    ]);

    /// <summary>Every provider kind the app knows, in tile order: the one list the About page and the
    /// statistics read, so a provider added to <see cref="AppSettings.KnownProviderIds"/> shows up in both.</summary>
    public static IReadOnlyList<ProviderKind> Kinds => LazyKinds.Value;

    /// <summary>How often one account may be read at most: its own value when it has one, else the
    /// global one. A route that costs more than a plain request (a web session, a network endpoint)
    /// never goes below <see cref="RemoteReadFloor"/>; <paramref name="belowFloorAllowed"/> lifts that
    /// for the route that does.</summary>
    internal static TimeSpan ReadInterval(AppSettings? settings, string accountKey, bool belowFloorAllowed = false)
    {
        if (settings is null)
            return RemoteReadFloor;

        var own = settings.Providers.TryGetValue(accountKey, out var providerSettings)
            ? ProviderSettings.NormalizeRefreshMinutes(providerSettings.RefreshMinutes)
            : null;
        var configured = TimeSpan.FromMinutes(own ?? SettingsRanges.ClampRemoteRefreshMinutes(settings.RemoteRefreshMinutes));
        return belowFloorAllowed || configured > RemoteReadFloor ? configured : RemoteReadFloor;
    }

    /// <summary>Codex's own session: the account page the sign-in window opens, the hosts its login
    /// may pass through, and its own browser profile subfolder. The login leaves chatgpt.com for the
    /// identity provider the user picked, so those hosts are listed; subdomains are covered by the
    /// host match (see <see cref="Web.SignInNavigationPolicy"/>).</summary>
    internal static readonly WebSessionDescriptor CodexWebSession = new(
        ProviderId: "codex",
        BaseUrl: "https://chatgpt.com/codex/settings/usage",
        // The login form itself: signed out, the usage page answers with a not-found page and the
        // front door with a landing page. This is the address the site's own sign-in link points at,
        // so the request carries the parameters the identity service expects.
        SignInUrl: "https://chatgpt.com/auth/login",
        // Google's two-factor step passes through gds.google.com, myaccount.google.com and
        // accounts.youtube.com before returning. They are named one by one, not as whole domains,
        // which would admit user-content hosts such as sites.google.com. Only top-level navigations
        // are checked, so scripts, styles and frames need no entry.
        AllowedHosts: ["chatgpt.com", "openai.com", .. Web.SignInNavigationPolicy.GoogleSignInHosts, "appleid.apple.com", "login.microsoftonline.com"],
        ProfileFolderName: "codex");

    /// <summary>Cursor's session. The sign-in address is its own field, never derived from the usage
    /// page. The login leaves cursor.com: workos.com is its identity service, cursor.sh covers
    /// authenticate. and authenticator., Google is named by its sign-in hosts (see
    /// <see cref="Web.SignInNavigationPolicy.GoogleSignInHosts"/>) and GitHub by github.com.</summary>
    internal static readonly WebSessionDescriptor CursorWebSession = new(
        ProviderId: "cursor",
        BaseUrl: "https://cursor.com/dashboard/usage",
        SignInUrl: "https://cursor.com/dashboard",
        AllowedHosts: ["cursor.com", "cursor.sh", "workos.com", .. Web.SignInNavigationPolicy.GoogleSignInHosts, Web.SignInNavigationPolicy.ExactPrefix + "github.com"],
        ProfileFolderName: "cursor");

    /// <summary>Gemini's own session: the usage page of the Gemini app, its own front door, and its
    /// own browser profile subfolder. The product's own host plus Google's sign-in hosts (see
    /// <see cref="Web.SignInNavigationPolicy.GoogleSignInHosts"/>): Google's two-factor confirmation
    /// travels through more of its own hosts than the front door alone.</summary>
    internal static readonly WebSessionDescriptor GeminiWebSession = new(
        ProviderId: "gemini",
        BaseUrl: "https://gemini.google.com/usage",
        // Google account sign-in with the usage page as its return address, not gemini.google.com
        // itself: signed out, the usage page redirects to the app's welcome page, which carries no
        // visible way in and is exactly the "just a Google page, no sign-in" a signed-out user would
        // see.
        SignInUrl: "https://accounts.google.com/ServiceLogin?continue=https%3A%2F%2Fgemini.google.com%2Fusage",
        // The sign-in hosts include the country account hosts: after the login Google sets the
        // session cookies through accounts.google.<the user's country domain>. gemini.google.com can
        // send a session to Google's unusual-traffic check first; the sign-in window lets the person
        // pass it, the hidden session reports it as "sign in again" (see WebSessionCheckRequiredException).
        AllowedHosts: ["gemini.google.com", .. Web.SignInNavigationPolicy.GoogleSignInHosts, Web.SignInNavigationPolicy.GoogleTrafficCheck],
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
        // Google's sign-in hosts: Claude's own "Sign in with Google" button leaves claude.ai for
        // Google's real login/consent flow before returning - without them that whole path is
        // indistinguishable from a session hijack attempt and gets blocked outright. Several hosts
        // for the same reason as on Codex's list above: the two-factor confirmation travels through
        // more than one Google host.
        AllowedHosts: ["claude.ai", "anthropic.com", .. Web.SignInNavigationPolicy.GoogleSignInHosts],
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
        return (BuildClaudeProvider(accountKey, settings, store.RequestSave, runner), runner);
    }

    /// <summary>The primary account ("claude") reads Claude Code's own sign-in first and the app's
    /// web session when that sign-in has no token; a further account is the web session only.</summary>
    private static ClaudeProvider BuildClaudeProvider(
        string accountKey, AppSettings settings, Action<AppSettings> saveSettings, WebSessionScriptRunner runner)
    {
        var webSource = new WebUsageSource(WebSessionFor(accountKey), new ClaudeUsageEndpoint(), runner.ExecuteScriptAsync);
        Func<TimeSpan> attentionMaxAge = () => TimeSpan.FromMinutes(settings.AttentionMaxAgeMinutes);
        return accountKey == ClaudeWebSession.ProviderId
            ? new ClaudeProvider(accountKey, settings, saveSettings, webSource, ClaudeCodeLogin.FetchAsync, attentionMaxAge)
            : new ClaudeProvider(accountKey, settings, saveSettings, webSource, attentionMaxAge);
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
                descriptor, new CursorUsageEndpoint(), runner.ExecuteScriptAsync,
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
            ? new GeminiProvider(settings, store.RequestSave, webSource, () => TimeSpan.FromMinutes(settings.AttentionMaxAgeMinutes))
            : GeminiProvider.ForWebOnlyAccount(accountKey, settings, store.RequestSave, webSource);
        return (provider, runner);
    }

    /// <summary>The primary account's own provider, built the one way <see cref="CreateAll"/> builds
    /// it (Claude Code's sign-in first, the web session as the fallback) - shared with
    /// <see cref="Views.SettingsWindow"/>'s read-locations panel, so the panel names both sources.
    /// Nothing here is ever read: the runner behind the web source is never started.</summary>
    internal static IUsageProvider CreatePrimaryClaudeAccount(AppSettings? settings = null) =>
        BuildClaudeProvider(
            AppSettings.CreateDefaultAccounts().Keys.Single(), settings ?? new AppSettings(), _ => { },
            new WebSessionScriptRunner(WebSessionFor(AppSettings.CreateDefaultAccounts().Keys.Single())));

    /// <summary>Copilot's own primary provider and, for every account key <see
    /// cref="AppSettings.CopilotAccountLogins"/> names, a further one reading as that already
    /// signed-in GitHub user - never a new sign-in, never a web session (see <see
    /// cref="Providers.CopilotProvider"/>).</summary>
    internal static IUsageProvider CreateCopilotAccount(string accountKey, AppSettings settings) =>
        accountKey == "copilot"
            ? new CopilotProvider(settings)
            : new CopilotProvider(accountKey, settings.CopilotAccountLogins.GetValueOrDefault(accountKey, accountKey), settings);

    /// <summary>Every runner is handed back rather than only captured as a method group, so the
    /// caller can own and dispose it (sign-out needs to tear it down before it deletes the browser
    /// profile it still has open).</summary>
    public static (IReadOnlyList<IUsageProvider> Providers, IReadOnlyDictionary<string, WebSessionScriptRunner> Runners) CreateAll(
        AppSettings settings, SettingsStore store, LogService? logService = null)
    {
        var providers = new List<IUsageProvider>();
        var runners = new Dictionary<string, WebSessionScriptRunner>();

        foreach (var (accountKey, providerId) in settings.Accounts)
        {
            if (providerId != "claude")
                continue; // a further account of another provider - built below instead

            // The primary account reads Claude Code's sign-in first and the app's web session when
            // that has no token; a further account reads the web session only. Both own a runner.
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

        providers.Add(CreateCopilotAccount("copilot", settings));

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
