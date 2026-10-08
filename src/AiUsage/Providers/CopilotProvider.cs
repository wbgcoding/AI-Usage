using AiUsage.Models;
using AiUsage.Providers.LocalLogin;
using AiUsage.Providers.Parsing;
using AiUsage.Services;

namespace AiUsage.Providers;

/// <summary>
/// Reads GitHub Copilot's quota through the GitHub CLI the user has already signed in on this
/// machine (see <see cref="GitHubCliUsage"/>): <c>gh api copilot_internal/user</c>. There is no
/// local file with these numbers, and GitHub exposes no personal REST endpoint for them, so the
/// signed-in CLI is the route. The widget never touches the CLI's token. Never throws.
/// </summary>
public sealed class CopilotProvider : IUsageProvider
{
    private readonly Func<CancellationToken, Task<CopilotFetch>> _read;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _accountKey;

    public CopilotProvider() : this(GitHubCliUsage.FetchAsync, now: null)
    {
    }

    /// <summary>A further Copilot account: another GitHub user already signed into the CLI (see
    /// <see cref="GitHubCliUsage.ListSignedInUsersAsync"/>), read by asking <c>gh</c> for that
    /// specific user's own token rather than the CLI's currently active one. <paramref
    /// name="accountKey"/> is this instance's own account (see <see cref="AccountKey"/>) -
    /// <see cref="Services.ProviderRegistry"/> is the one caller that ever builds one of these.</summary>
    public CopilotProvider(string accountKey, string githubLogin)
        : this(ct => GitHubCliUsage.FetchAsync(githubLogin, ct), now: null, accountKey)
    {
    }

    /// <summary>Test seam: a canned CLI result and a fixed clock instead of running gh.</summary>
    internal CopilotProvider(Func<CancellationToken, Task<CopilotFetch>> fetch, Func<DateTimeOffset>? now, string? accountKey = null)
    {
        _read = fetch;
        _now = now ?? (() => DateTimeOffset.Now);
        _accountKey = accountKey ?? Id;
    }

    public string Id => "copilot";

    /// <summary>This provider's own id for the primary account (the CLI's active user), or that id
    /// plus a "#2"/"#3"/... suffix for a further GitHub user - see <see
    /// cref="Services.IUsageProvider.AccountKey"/>.</summary>
    public string AccountKey => _accountKey;

    public string DisplayName => "Copilot";

    // Sign-in happens through the GitHub CLI's own login, outside the app - the tile only explains
    // that in its placeholder text, it never opens a console or a browser window itself. A further
    // account is not a further sign-in either: it reads an already signed-in GitHub user the CLI
    // itself lists (see GitHubCliUsage.ListSignedInUsersAsync), never a new login this app starts.
    // The Settings.SignOut and Settings.SignIn actions still make sense despite all that: stop
    // reading the CLI's already signed-in user, or resume reading it (see ProviderTileViewModel.SupportsSignOut and
    // Views.MainWindow.WireSignIn's own Copilot branch).
    public bool SupportsInAppSignIn => false;

    public bool SupportsSignOut => true;

    // Every signed-in GitHub user the CLI already knows about can become its own account (see
    // Services.ProviderRegistry.CreateCopilotAccount) - unlike Codex/Cursor/Gemini's own web
    // sessions, a further Copilot account costs no new sign-in at all.
    public bool SupportsMultipleAccounts => true;

    public TimeSpan? MinRefreshInterval => TimeSpan.FromMinutes(5);

    public IReadOnlyList<string> ReadLocations => [LocalizationService.Instance["About.ReadLocationGitHubCli"]];

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var fetchedAt = _now();
        var result = await _read(ct);

        // Copilot has exactly one source (the GitHub CLI's own sign-in), so the chooser has nothing
        // to pick between - wrapping the single result still routes it through the same rule every
        // other provider follows, rather than a source-specific exception.
        return SnapshotChooser.Pick([BuildSnapshot(result, fetchedAt)]);
    }

    private ProviderSnapshot BuildSnapshot(CopilotFetch result, DateTimeOffset fetchedAt)
    {
        switch (result.Outcome)
        {
            case GitHubCliOutcome.Ok:
                var windows = CopilotUserParser.Parse(result.Json);
                if (windows.Count == 0)
                    return Empty(fetchedAt, ProviderStatus.Failed, error: null);

                return new ProviderSnapshot(
                    ProviderId: AccountKey,
                    Windows: windows,
                    PlanType: CopilotUserParser.ParsePlan(result.Json),
                    SourceKind: SourceKind.LocalLogin,
                    FetchedAt: fetchedAt,
                    DataTimestamp: fetchedAt,
                    Status: ProviderStatus.Ok,
                    Error: null,
                    AccountLabel: CopilotUserParser.ParseLogin(result.Json));

            case GitHubCliOutcome.NotSignedIn:
                // The CLI is there but nothing is signed into it - the app must never run
                // "gh auth login" itself (that would be signing the user into another tool, not this
                // app), so the notice names the command for the user to run themselves instead of
                // offering a sign-in button that would just fail.
                return Empty(fetchedAt, ProviderStatus.NotSignedIn,
                    new ProviderError("Status_GitHubCliNotSignedIn_Reason"));

            case GitHubCliOutcome.ToolMissing:
                // Nothing to sign into until the tool exists - explain that rather than offer a
                // sign-in button that would just fail. The user installs the CLI themselves.
                return Empty(fetchedAt, ProviderStatus.SourceUnavailable,
                    new ProviderError("Status_GitHubCliMissing_Reason"));

            default:
                return ProviderSnapshots.Unavailable(AccountKey, fetchedAt);
        }
    }

    private ProviderSnapshot Empty(DateTimeOffset fetchedAt, ProviderStatus status, ProviderError? error) => new(
        ProviderId: AccountKey,
        Windows: [],
        PlanType: null,
        SourceKind: SourceKind.None,
        FetchedAt: fetchedAt,
        DataTimestamp: null,
        Status: status,
        Error: error);
}
