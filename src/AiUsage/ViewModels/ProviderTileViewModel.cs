using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.Views.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiUsage.ViewModels;

/// <summary>Whether a web-backed provider is currently signed in, signed out, or has not yet
/// told us either way. A bool cannot carry the third case: right after startup, before the
/// first web-sourced snapshot lands, neither "signed in" nor "signed out" is known.</summary>
public enum SignInState
{
    Unknown,
    SignedIn,
    SignedOut,
}

/// <summary>
/// The one view model behind every provider tile - the view/UI never branches on
/// which provider it is looking at. Density (Full/Mini) and visibility
/// are decided by the window/eye-menu and only ever handed in from outside.
/// </summary>
public partial class ProviderTileViewModel : ObservableObject, ITileRow
{
    private ProviderSnapshot? _lastSnapshot;

    /// <summary>The window kinds this tile's provider has actually reported, from the last snapshot
    /// that carried any windows at all; null until then, so every switch stays offered while nothing
    /// is known yet.</summary>
    private HashSet<WindowKind>? _knownWindowKinds;

    /// <summary>Whether a setting about this kind of window means anything for this tile - a provider
    /// that never reports a 5-hour window (Cursor, Copilot) gets no 5-hour switch or slider.</summary>
    public bool HasFiveHourWindow => _knownWindowKinds?.Contains(WindowKind.FiveHour) ?? true;
    public bool HasWeeklyWindow => _knownWindowKinds?.Contains(WindowKind.Weekly) ?? true;
    public bool HasOtherWindow => _knownWindowKinds?.Contains(WindowKind.Other) ?? true;

    /// <summary>This tile's own hidden Other-row labels (see <see
    /// cref="Models.ProviderSettings.HiddenWindows"/>) - set by <see
    /// cref="ViewModels.MainViewModel.BuildTile"/> right after construction and again by <see
    /// cref="ViewModels.SettingsViewModel"/> whenever settings are replaced wholesale (an import or a
    /// reset), otherwise mutated only through <see cref="WindowToggles"/>. Re-assigning it does not
    /// by itself re-filter <see cref="Rows"/> or refresh the existing toggles' own <see
    /// cref="WindowToggleViewModel.IsVisible"/> - a caller that changes it after the tile already has
    /// a snapshot must also call <see cref="RefreshLocalizedText"/> (or wait for the next fetch).</summary>
    public HashSet<string> HiddenWindows { get; set; } = [];

    /// <summary>One switch per distinct row the latest snapshot carries, in tile order - the
    /// Settings window's Providers card renders these instead of the two fixed checkboxes it used
    /// to show, so a provider with several Other rows (Cursor's model breakdown, for instance) can
    /// hide them one at a time. Rebuilt only when the set of distinct labels changes (see <see
    /// cref="RebuildWindowToggles"/>); a plain re-apply of the same snapshot just refreshes <see
    /// cref="WindowToggleViewModel.DisplayText"/> in place.</summary>
    public ObservableCollection<WindowToggleViewModel> WindowToggles { get; } = [];

    /// <summary>Raised when a per-row switch for a row that is not FiveHour/Weekly flips - those two
    /// kinds go through <see cref="ShowFiveHourChanged"/>/<see cref="ShowWeeklyChanged"/> instead,
    /// since they govern a whole window kind rather than one label. MainViewModel reads this tile's
    /// own <see cref="HiddenWindows"/> afterward and persists it, the same split as those two
    /// events.</summary>
    public event EventHandler? OtherWindowVisibilityChanged;

    /// <summary>The window kinds actually reported so far: empty until a snapshot carried any, unlike
    /// the three flags above, which offer everything while nothing is known.</summary>
    public IReadOnlyCollection<WindowKind> KnownWindowKinds => _knownWindowKinds ?? [];

    /// <summary>Mirrors the last <see cref="ThresholdSettings"/> handed to <see cref="Apply"/>, so
    /// <see cref="ReapplyRowsFromLastSnapshot"/> can re-filter <see cref="Rows"/> the instant a
    /// window-visibility check box changes without losing the threshold marker in the meantime.</summary>
    private ThresholdSettings? _lastThresholds;

    /// <summary>Mirrors the last "mark a waiting tile" setting handed to <see cref="Apply"/>, so <see
    /// cref="ApplyShowAttentionMark"/> and <see cref="RefreshLocalizedText"/> can re-derive <see
    /// cref="IsWaitingForUser"/> without a fresh snapshot. Defaults to the setting's own default
    /// (on), matching every tile before its first real snapshot lands.</summary>
    private bool _lastShowAttentionMark = true;

    /// <summary>The <see cref="Models.ProviderSnapshot.WaitingSince"/> the user last ticked off with
    /// <see cref="DismissWaiting"/>, null until then. A snapshot whose own WaitingSince still equals
    /// this is the same wait already dismissed, so <see cref="IsWaitingForUser"/> stays false for it;
    /// a newer WaitingSince (a fresh wait) no longer matches and lifts the mark back on its own.</summary>
    private DateTimeOffset? _dismissedWaitingAt;

    /// <summary>Mirrors the last <c>refreshInterval</c> handed to <see cref="Apply"/>, so <see
    /// cref="RefreshLocalizedText"/>'s re-apply of the same snapshot (a language switch, no fetch
    /// involved) keeps deriving <see cref="LastUpdatedToolTip"/>'s data-age wording from the same
    /// cadence instead of losing it to the parameterless default.</summary>
    private TimeSpan? _lastRefreshInterval;

    /// <summary>This tile's own identity: the account key (see <see
    /// cref="Services.IUsageProvider.AccountKey"/>) - a provider's own id for a first/only account,
    /// that id plus a "#2" suffix for a further one. Everything that must not confuse two accounts of
    /// the same provider (the dictionary key in <see cref="ViewModels.MainViewModel"/>, the settings
    /// entry, the history file) is keyed by this, never by <see cref="RealProviderId"/>.</summary>
    public string ProviderId { get; }

    /// <summary>Which provider this actually is - the same for both a first and a further account of
    /// it, equal to <see cref="ProviderId"/> for a first/only account. Used only where the provider
    /// itself (not the specific account) is what matters: the usage-page link (<see
    /// cref="ProviderLinks"/>) is the same page for every account of a provider.</summary>
    public string RealProviderId { get; }

    public string DisplayName { get; }

    /// <summary>True for a second (or later) account of the same provider - its <see
    /// cref="ProviderId"/> (its own account key) differs from <see cref="RealProviderId"/> (the
    /// provider it actually is). Drives the Settings window's remove-account button (Settings.RemoveAccount) and
    /// <see cref="HeaderDisplayName"/> - a first/only account has neither.</summary>
    public bool IsExtraAccount => ProviderId != RealProviderId;

    /// <summary>The tile's own header text: <see cref="DisplayName"/> alone for a first/only account,
    /// "{DisplayName} · {AccountText}" once a further account's own response has named it,
    /// falling back to "{DisplayName} ({n})" until then, {n} taken from <see
    /// cref="ProviderId"/>'s own "#2"/"#3"/... suffix - two Claude tiles must never show the same
    /// plain "Claude" with nothing to tell them apart, and a third account must not look like a
    /// second one.</summary>
    public string HeaderDisplayName => HasOwnAccountName
        ? $"{DisplayName} · {AccountName}"
        : !IsExtraAccount
            ? DisplayName
            : AccountText is { Length: > 0 } label ? $"{DisplayName} · {label}" : $"{DisplayName} ({AccountSuffixNumber})";

    private string _accountName = "";

    /// <summary>The person's own name for this account (see <see cref="ProviderSettings.AccountName"/>),
    /// already trimmed and cut to the allowed length. Empty = none, and the header, tray and
    /// notification names stay as they were without it.</summary>
    public string AccountName
    {
        get => _accountName;
        set
        {
            var normalized = ProviderSettings.NormalizeAccountName(value);
            if (normalized == _accountName)
                return;
            _accountName = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasOwnAccountName));
            OnPropertyChanged(nameof(HeaderDisplayName));
            OnPropertyChanged(nameof(TitleName));
            OnPropertyChanged(nameof(TitleAccountSuffix));
            OnPropertyChanged(nameof(TrayName));
            OnPropertyChanged(nameof(NotificationName));
            OnPropertyChanged(nameof(ToggleVisibilityActionText));
            AccountNameChanged?.Invoke(this, normalized);
        }
    }

    /// <summary>Raised when the own account name changed, so MainViewModel can persist it (same
    /// split as <see cref="ChartHiddenChanged"/>).</summary>
    public event EventHandler<string>? AccountNameChanged;

    public bool HasOwnAccountName => _accountName.Length > 0;

    /// <summary>The first part of the tile's title: the provider name when an own account name follows
    /// it in its own, quieter run (<see cref="TitleAccountSuffix"/>), else the whole header text.</summary>
    public string TitleName => HasOwnAccountName ? DisplayName : HeaderDisplayName;

    /// <summary>" · name" for the quieter run after <see cref="TitleName"/>; empty without an own name.</summary>
    public string TitleAccountSuffix => HasOwnAccountName ? $" · {_accountName}" : "";

    /// <summary>"Claude (Work)" with an own name, else the same header text as before; used by the
    /// tray tooltip.</summary>
    public string TrayName => HasOwnAccountName ? $"{DisplayName} ({_accountName})" : HeaderDisplayName;

    /// <summary>"Claude (Work)" with an own name, else the plain provider name; used by notifications.</summary>
    public string NotificationName => HasOwnAccountName ? $"{DisplayName} ({_accountName})" : DisplayName;

    /// <summary>What a screen reader reads for the tile's list item, which has no name of its own
    /// and therefore falls back to this - the type name until now.</summary>
    public override string ToString() => HeaderDisplayName;

    /// <summary>The digit after <see cref="ProviderId"/>'s "#", used by <see cref="HeaderDisplayName"/>'s
    /// fallback - "2" when the key carries no suffix at all, which only a malformed <see
    /// cref="ProviderId"/> could produce since <see cref="IsExtraAccount"/> is already false for a
    /// first/only account.</summary>
    private string AccountSuffixNumber
    {
        get
        {
            var hashIndex = ProviderId.IndexOf('#');
            return hashIndex >= 0 ? ProviderId[(hashIndex + 1)..] : "2";
        }
    }

    /// <summary>Whether the provider behind this tile can be signed in/out of directly from the app -
    /// set once at construction from the provider's own capability, so no <c>[ObservableProperty]</c>
    /// is needed.</summary>
    public bool SupportsInAppSignIn { get; init; }

    /// <summary>Whether this tile shows a connect/disconnect pair at all - true for every provider
    /// <see cref="SupportsInAppSignIn"/> already covers, plus Copilot, which disconnects the GitHub
    /// CLI read this app makes rather than a web session of its own (see <see
    /// cref="Services.IUsageProvider.SupportsSignOut"/>). Real provider id, not the account key: a
    /// further Copilot account gets the same pair as the primary one.</summary>
    public bool SupportsSignOut => SupportsInAppSignIn || RealProviderId == "copilot" || DisconnectsLocalSignIn;

    /// <summary>Set from <see cref="Services.IUsageProvider.SupportsSignOut"/>: the account reads a
    /// sign-in another tool holds (the primary Claude account through Claude Code), so "sign out"
    /// only stops this app's reads and "sign in" resumes them.</summary>
    public bool DisconnectsLocalSignIn { get; init; }

    /// <summary>Whether the Settings window offers an "add account" button for this tile - only a
    /// provider that can hold more than one account at once (Claude today).</summary>
    public bool SupportsAddAccount { get; init; }

    /// <summary>Where this tile's provider actually looks (<see cref="Services.IUsageProvider.ReadLocations"/>),
    /// set once at construction.</summary>
    public IReadOnlyList<string> ReadLocations { get; init; } = [];

    [ObservableProperty]
    private string? planText;

    /// <summary>Which account this tile is reading, when the provider's response names one - may be
    /// an email address or login. PRIVACY: shown on the tile itself and in the Settings window's provider row
    /// (<see cref="SettingsRowText"/>) and nowhere else - never add this to
    /// <see cref="DiagnosticsText"/>, never pass it to a logger, never fold it into whatever a future
    /// "copy everything" feature collects. Same "must never reach a log" contract <c>PathSanitizer.cs</c>
    /// documents for user paths.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderDisplayName))]
    [NotifyPropertyChangedFor(nameof(TitleName))]
    [NotifyPropertyChangedFor(nameof(TrayName))]
    [NotifyPropertyChangedFor(nameof(ToggleVisibilityActionText))]
    [NotifyPropertyChangedFor(nameof(SettingsRowText))]
    [NotifyPropertyChangedFor(nameof(NameTooltipText))]
    private string? accountText;

    /// <summary>The header's name button opens the tile's details (see <c>TileNameButtonStyle</c> in
    /// ProviderTile.xaml) - its tooltip says so while there are details to open, then names the known
    /// account on its own line (the account has no column of its own, to give the name more room) and
    /// the data source on a further line once one answered. Nothing to say -> empty, no tooltip.</summary>
    public string NameTooltipText
    {
        get
        {
            var loc = LocalizationService.Instance;
            var lines = new List<string>();
            if (HasDiagnostics)
                lines.Add(loc["Tile.NameTooltip"]);
            if (AccountText is { Length: > 0 } label)
                lines.Add(label);
            if (HasSource)
                lines.Add(loc.Format("Tile.SourceTip", SourceBadgeText));
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>The Settings window's one-line row for this provider: the account (when one is
    /// known) and a short phrase for the source that last delivered numbers.</summary>
    public string SettingsRowText
    {
        get
        {
            var via = ViaText();
            return AccountText is { Length: > 0 } label ? $"{label} · {via}" : via;
        }
    }

    /// <summary>The source that last actually delivered numbers; a failed read (no source at all)
    /// leaves it as it was, the same way <see cref="AccountText"/> survives one. Null until a read
    /// has delivered anything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsRowText))]
    private SourceKind? lastSourceKind;

    private string ViaText()
    {
        var loc = LocalizationService.Instance;
        if (LastSourceKind is not { } source)
            return loc["Settings.Via.None"];

        var key = (source, ProviderRegistry.BaseProviderId(RealProviderId)) switch
        {
            (SourceKind.WebSession, _) => "Settings.Via.WebSession",
            (SourceKind.LocalLogin, "claude") => "Settings.Via.ClaudeCode",
            (SourceKind.LocalLogin or SourceKind.LocalFile or SourceKind.LocalDatabase, "gemini") => "Settings.Via.Antigravity",
            (SourceKind.LocalLogin, "copilot") => "Settings.Via.GitHubCli",
            (SourceKind.LocalFile, "codex") => "Settings.Via.CodexFiles",
            (SourceKind.LocalFile, "claude") => "Settings.Via.ClaudeFiles",
            _ => null,
        };
        var via = key is null ? StatusTextMap.SourceBadge(source) : loc[key];

        // A signed-in browser session the numbers do NOT come from: a local source delivers, and the
        // row says the web sign-in exists too, so both sign-ins are visible and which one delivers.
        return _webSessionSignedIn && source != SourceKind.WebSession
            ? $"{via} · {loc["Settings.Via.WebAlsoSignedIn"]}"
            : via;
    }

    // Whether this account's own browser session is signed in, as far as the latest answers say:
    // a provider that knows reports it on its snapshot, a web read that delivered proves it, a
    // finished sign-in window sets it, a sign-out clears it.
    private bool _webSessionSignedIn;

    private void SetWebSessionSignedIn(bool signedIn)
    {
        if (_webSessionSignedIn == signedIn)
            return;
        _webSessionSignedIn = signedIn;
        OnPropertyChanged(nameof(SettingsRowText));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceDiagnosticText))]
    [NotifyPropertyChangedFor(nameof(NameTooltipText))]
    private string sourceBadgeText = "";

    /// <summary>Whether the latest answer came from any source at all; a tile with nothing to show yet
    /// has none, and its source line would only read "not connected" beside the call to action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameTooltipText))]
    private bool hasSource;

    /// <summary>A plain, stable word naming why this tile's primary read route was skipped instead of
    /// answering (see <see cref="Models.ProviderSnapshot.SkipReasonWord"/>) - null once it did answer,
    /// and always null for a provider with only one route. Never a token, never a path.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceDiagnosticText))]
    private string? skipReasonWord;

    /// <summary>The About window's one-line diagnostic for this tile: which source last answered, and
    /// - only when the primary route was skipped instead - the plain reason word behind it.</summary>
    public string SourceDiagnosticText => SkipReasonWord is { Length: > 0 } reason
        ? LocalizationService.Instance.Format("About.ProviderSourceSkipped", DisplayName, SourceBadgeText, reason)
        : LocalizationService.Instance.Format("About.ProviderSource", DisplayName, SourceBadgeText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk))]
    [NotifyPropertyChangedFor(nameof(IsNotOk))]
    [NotifyPropertyChangedFor(nameof(IsStale))]
    [NotifyPropertyChangedFor(nameof(HasNumbers))]
    [NotifyPropertyChangedFor(nameof(ShowRows))]
    [NotifyPropertyChangedFor(nameof(ShowDiagram))]
    [NotifyPropertyChangedFor(nameof(ShowChartBox))]
    [NotifyPropertyChangedFor(nameof(ShowChartHint))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowStaleNotice))]
    [NotifyPropertyChangedFor(nameof(ShowCodexSignInLink))]
    [NotifyPropertyChangedFor(nameof(DimLastValues))]
    [NotifyPropertyChangedFor(nameof(ShowMiniRows))]
    [NotifyPropertyChangedFor(nameof(ShowMiniHeadline))]
    [NotifyPropertyChangedFor(nameof(ShowLastUpdated))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderSignIn))]
    private ProviderStatus status = ProviderStatus.NoLocalData;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignIn))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderSignIn))]
    [NotifyPropertyChangedFor(nameof(ShowSignOut))]
    [NotifyPropertyChangedFor(nameof(ShowCodexSignInLink))]
    private SignInState signInState = SignInState.Unknown;

    /// <summary>What kind of failure the tile's current snapshot is, <see cref="Models.FailureKind.Other"/>
    /// for anything that is not a failed read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignIn))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderSignIn))]
    [NotifyPropertyChangedFor(nameof(ShowStatusLink))]
    [NotifyPropertyChangedFor(nameof(ShowFailureLink))]
    private FailureKind failureKind;

    /// <summary>True while a fetch the user started (tile menu, F5, tray, title bar menu) runs for this
    /// tile, for at least a short minimum so it can be seen; automatic fetches never set it. Drives the
    /// small turning icon next to the name.</summary>
    [ObservableProperty]
    private bool showRefreshSpinner;

    /// <summary>True while a failed fetch is on the tile but the last good rows and chart are still
    /// shown (dimmed), with <see cref="FailureNoticeText"/> as one muted line above them. The next Ok or
    /// Stale snapshot clears it, and so does any other status.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFailureNotice))]
    [NotifyPropertyChangedFor(nameof(ShowFailureLink))]
    [NotifyPropertyChangedFor(nameof(DimLastValues))]
    [NotifyPropertyChangedFor(nameof(ShowStaleNotice))]
    [NotifyPropertyChangedFor(nameof(ShowCodexSignInLink))]
    private bool isShowingLastValues;

    /// <summary>The failed read's headline and reason as one line, shown while <see cref="IsShowingLastValues"/>.</summary>
    [ObservableProperty]
    private string failureNoticeText = "";

    public bool ShowFailureNotice => IsShowingLastValues && !IsMini;

    /// <summary>The status page link under the failure line that sits above the last values.</summary>
    public bool ShowFailureLink => ShowFailureNotice && ShowStatusLink;

    /// <summary>Rows and chart dim for last values on their own only when the stale look does not
    /// already dim the whole body.</summary>
    public bool DimLastValues => IsShowingLastValues && !IsStale;

    /// <summary>The failed snapshot whose texts <see cref="FailureNoticeText"/> shows; null otherwise.</summary>
    private ProviderSnapshot? _keptFailure;

    [ObservableProperty]
    private string headlineText = "";

    [ObservableProperty]
    private string reasonText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAction))]
    private string? actionLabelText;

    /// <summary>The unresolved action key behind <see cref="ActionLabelText"/>, kept so <see
    /// cref="RunPlaceholderAction"/> can tell a retry apart from a sign-in.</summary>
    private string? _actionKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMini))]
    [NotifyPropertyChangedFor(nameof(ShowHeader))]
    [NotifyPropertyChangedFor(nameof(ShowRows))]
    [NotifyPropertyChangedFor(nameof(ShowDiagram))]
    [NotifyPropertyChangedFor(nameof(ShowChartBox))]
    [NotifyPropertyChangedFor(nameof(ShowChartHint))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowStaleNotice))]
    [NotifyPropertyChangedFor(nameof(ShowFailureNotice))]
    [NotifyPropertyChangedFor(nameof(ShowCodexSignInLink))]
    [NotifyPropertyChangedFor(nameof(ShowFailureLink))]
    [NotifyPropertyChangedFor(nameof(ShowChartMenuItem))]
    [NotifyPropertyChangedFor(nameof(ShowMiniRows))]
    [NotifyPropertyChangedFor(nameof(ShowMiniHeadline))]
    [NotifyPropertyChangedFor(nameof(ShowLastUpdated))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderSignIn))]
    private TileDensity density = TileDensity.Full;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleVisibilityActionText))]
    private bool isHidden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDiagram))]
    [NotifyPropertyChangedFor(nameof(ShowChartBox))]
    [NotifyPropertyChangedFor(nameof(ShowChartHint))]
    [NotifyPropertyChangedFor(nameof(ChartShown))]
    [NotifyPropertyChangedFor(nameof(ChartMenuHeader))]
    private bool chartHidden;

    /// <summary>True when the "agent is waiting" mark is switched off for this tile alone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttentionEnabled))]
    private bool attentionDisabled;

    /// <summary>The inverse of <see cref="AttentionDisabled"/>, so the settings check box reads
    /// "mark".</summary>
    public bool AttentionEnabled
    {
        get => !AttentionDisabled;
        set => AttentionDisabled = !value;
    }

    /// <summary>Whether this provider can report a waiting agent at all (see <see
    /// cref="Providers.Parsing.AttentionDetector.CanDetect"/>); the settings list only the ones that
    /// can.</summary>
    public bool SupportsAttention => Providers.Parsing.AttentionDetector.CanDetect(RealProviderId);

    public event EventHandler<bool>? AttentionDisabledChanged;

    partial void OnAttentionDisabledChanged(bool value)
    {
        AttentionDisabledChanged?.Invoke(this, value);
        ApplyShowAttentionMark(_lastShowAttentionMark);
    }

    /// <summary>The inverse of <see cref="ChartHidden"/>, so the settings check box reads "show".</summary>
    public bool ChartShown
    {
        get => !ChartHidden;
        set => ChartHidden = !value;
    }

    /// <summary>The tile menu's chart entry: "Hide chart" while the chart shows, "Show chart" once hidden.</summary>
    public string ChartMenuHeader => LocalizationService.Instance[ChartHidden ? "Tile.Menu.ShowChart" : "Tile.Menu.HideChart"];

    /// <summary>The chart entry only makes sense where a chart can show at all: Full density.</summary>
    public bool ShowChartMenuItem => Density == TileDensity.Full;

    /// <summary>The tile menu's chart entry flips the same switch the settings check box does, so the
    /// choice persists through <see cref="ChartHiddenChanged"/> and the check box follows.</summary>
    [RelayCommand]
    private void ToggleChart() => ChartShown = ChartHidden;

    /// <summary>Raised when the chart of this tile is switched on or off, so MainViewModel can
    /// persist the choice (same split as <see cref="ShowFiveHourChanged"/>).</summary>
    public event EventHandler<bool>? ChartHiddenChanged;

    partial void OnChartHiddenChanged(bool value) => ChartHiddenChanged?.Invoke(this, value);

    [ObservableProperty]
    private bool showFiveHour = true;

    [ObservableProperty]
    private bool showWeekly = true;

    /// <summary>This provider's own token sum since the Monday of the current ISO week - set from
    /// outside (see <see cref="ViewModels.MainViewModel"/>'s own periodic query against <see
    /// cref="Stats.StatsStore.SumTokensSince"/>), never computed here, same split as <see
    /// cref="IsFetching"/>. Null until that query has landed at least once, and always null for a
    /// provider <see cref="Stats.ProviderCoverage.HasLocalTokenData"/> says has no local token
    /// index at all - <see cref="WeekTokensText"/> stays empty either way.</summary>
    [ObservableProperty]
    private long? weekTokens;

    partial void OnWeekTokensChanged(long? value) => PushWeekTokensToRows();

    /// <summary>Hands the week sum to every weekly row (each row decides whether it shows it).</summary>
    private void PushWeekTokensToRows()
    {
        foreach (var row in Rows)
            row.WeekTokens = WeekTokens;
    }

    /// <summary>True once the newest record in this tile's own session file reads as "waiting for
    /// the user" (see <see cref="Models.ProviderSnapshot.IsWaitingForUser"/>) AND the "mark a waiting
    /// tile" setting is on - set in <see cref="Apply"/> from both together, never from the snapshot
    /// alone, so a switched-off setting always reads false whatever the snapshot says.</summary>
    [ObservableProperty]
    private bool isWaitingForUser;

    /// <summary>Whether the eye popup's own reorder arrows are enabled for this tile - both true
    /// until MainViewModel narrows them once it knows this tile's actual position among <see
    /// cref="ViewModels.MainViewModel.Tiles"/>. Kept here rather than computed from a bound index in
    /// XAML because a plain view-model bool is trivial to unit test and never depends on which
    /// container WPF happens to generate for the eye popup's ItemsControl.</summary>
    [ObservableProperty]
    private bool canMoveUp = true;

    [ObservableProperty]
    private bool canMoveDown = true;

    /// <summary>Raised when the user flips one of the two window-visibility check boxes in the
    /// Settings window's Providers card - MainViewModel owns <see cref="Models.AppSettings"/>, so the
    /// actual persistence happens there (same split as <see cref="HideRequested"/>); this tile
    /// re-filters its own <see cref="Rows"/> immediately either way (see <see cref="OnShowFiveHourChanged"/>),
    /// so the checkbox never waits on a round trip through settings before the tile visibly reacts.</summary>
    public event EventHandler<bool>? ShowFiveHourChanged;

    public event EventHandler<bool>? ShowWeeklyChanged;

    partial void OnShowFiveHourChanged(bool value)
    {
        ShowFiveHourChanged?.Invoke(this, value);
        ReapplyRowsFromLastSnapshot();
    }

    partial void OnShowWeeklyChanged(bool value)
    {
        ShowWeeklyChanged?.Invoke(this, value);
        ReapplyRowsFromLastSnapshot();
    }

    /// <summary>"Claude: Show this provider"/"Claude: Hide this provider" for the two provider-toggle
    /// buttons (Settings window's Anzeige card, the title bar's eye popup) - both used to announce
    /// only the bare provider name, giving no hint that the button is a bidirectional show/hide
    /// toggle or which way it would flip next. The "Tile.Show" resx string existed for exactly this
    /// and was never actually bound anywhere.</summary>
    public string ToggleVisibilityActionText =>
        $"{HeaderDisplayName}: {LocalizationService.Instance[IsHidden ? "Tile.Show" : "Tile.Hide"]}";

    /// <summary>Timestamp of the last snapshot that actually carried data, regardless of status - a
    /// Stale snapshot still has a last-known timestamp. Feeds the Settings window's "zuletzt
    /// aktualisiert" line per provider, and (via <see cref="LastUpdatedText"/>) the same line on the
    /// tile itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastUpdatedText))]
    [NotifyPropertyChangedFor(nameof(LastUpdatedToolTip))]
    [NotifyPropertyChangedFor(nameof(LastSuccessAgeText))]
    [NotifyPropertyChangedFor(nameof(ShowLastUpdated))]
    private DateTimeOffset? lastSuccessAt;

    /// <summary>The bare "vor ..." age the Settings window lists per provider. A property of its own
    /// (not a converter on <see cref="LastSuccessAt"/>) so <see cref="TickCountdowns"/> and a language
    /// switch can re-raise it and the age keeps moving.</summary>
    public string LastSuccessAgeText => AgeText.Describe(LastSuccessAt);

    /// <summary>When the last snapshot that carried data was fetched (set alongside <see
    /// cref="LastSuccessAt"/>): the header age counts from here, so it reads as how fresh the
    /// reading attempt is, not how old the provider's own data point is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastUpdatedText))]
    [NotifyPropertyChangedFor(nameof(LastUpdatedToolTip))]
    private DateTimeOffset? lastFetchAt;

    /// <summary>The tile's own "last updated" caption, in the same wording the Settings window
    /// already uses for the same value. It counts from the last fetch; when the data point itself is
    /// older than that (see <see cref="LastUpdatedToolTip"/>) the caption stays ordinary. Re-evaluated
    /// once a second by <see cref="TickCountdowns"/>, not only when a fetch actually lands, so it
    /// never freezes on a stale-looking age.</summary>
    public string LastUpdatedText => (LastFetchAt ?? LastSuccessAt) is { } at
        ? CountdownFormatter.FormatAge(at, DateTimeOffset.Now)
        : LocalizationService.Instance["Settings.NeverUpdated"];

    /// <summary>Names the age of the underlying data once it is older than the poll cadence
    /// (<see cref="Apply"/>'s <c>refreshInterval</c>): a Codex reading comes off the newest
    /// rate-limit event in a session file, which can sit unchanged between events. Empty otherwise,
    /// which the tile's tooltip trigger turns into no tooltip at all.</summary>
    public string LastUpdatedToolTip => LastSuccessAt is { } at
        && _lastRefreshInterval is { } interval && DateTimeOffset.Now - at > interval
            ? LocalizationService.Instance.Format("Tile.SnapshotAge", CountdownFormatter.FormatElapsed(DateTimeOffset.Now - at))
            : "";

    /// <summary>Whether the age line shows at all - once there has ever been a real timestamp, and
    /// never in Mini (which has no room for it). Kept visible through a failed fetch, deliberately:
    /// the age of the last GOOD number matters most exactly when the current one cannot be trusted,
    /// so this no longer lives inside the Ok-only content block that a failure hides along with the
    /// bars.</summary>
    public bool ShowLastUpdated => LastSuccessAt is not null && !IsMini;

    /// <summary>Timestamp of construction, then of every snapshot whose status is <see
    /// cref="ProviderStatus.Ok"/> or <see cref="ProviderStatus.Stale"/> - either one means the
    /// provider is still answering, even if the answer itself is old. Feeds <see cref="IsSilent"/>:
    /// a provider that has neither succeeded nor even started trying in a full day, not merely one
    /// between two normal fetches.</summary>
    public DateTimeOffset? LastSuccessOrStart { get; private set; } = DateTimeOffset.Now;

    /// <summary>True once <see cref="LastSuccessOrStart"/> is at least 24h in the past. Re-evaluated
    /// once a second by <see cref="TickCountdowns"/>, not only when a snapshot lands - nothing landing
    /// at all is exactly the condition this detects, so waiting for the next snapshot would mean it
    /// never appears on its own.</summary>
    public bool IsSilent { get; private set; }

    /// <summary>The tile's own one extra line while <see cref="IsSilent"/> - empty otherwise. Added
    /// to whatever placeholder the current status already shows, not a status of its own.</summary>
    public string SilentReasonText { get; private set; } = "";

    public ObservableCollection<UsageRowViewModel> Rows { get; } = [];

    /// <summary>Where this provider looked and what it found, in the user's language - the answer to
    /// "I see no data and nothing tells me why". Empty whenever there is nothing to explain.</summary>
    public ObservableCollection<string> Diagnostics { get; } = [];

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public string DiagnosticsText => string.Join(Environment.NewLine, Diagnostics);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsMenuHeader))]
    private bool detailsExpanded;

    /// <summary>The context menu's details toggle only does anything once <see cref="HasDiagnostics"/>
    /// is true (see its <c>Visibility</c> binding in ProviderTile.xaml), so its header only ever needs
    /// to say which way THIS click will flip the panel: "Show details" while collapsed, "Hide details"
    /// once <see cref="DetailsExpanded"/> is true.</summary>
    public string DetailsMenuHeader => LocalizationService.Instance[DetailsExpanded ? "Tile.Menu.HideDetails" : "Tile.Menu.Details"];

    /// <summary>True from the instant this provider's own fetch starts until shortly after its
    /// snapshot lands - MainViewModel owns the exact timing (including the minimum visible duration,
    /// so a fetch finishing in a handful of milliseconds does not just flicker); this is only ever
    /// set from outside, never computed here.</summary>
    [ObservableProperty]
    private bool isFetching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChartSummaryText))]
    [NotifyPropertyChangedFor(nameof(HasChartData))]
    [NotifyPropertyChangedFor(nameof(ShowChartBox))]
    [NotifyPropertyChangedFor(nameof(ShowChartHint))]
    private IReadOnlyList<HistoryChart.ChartPoint> fiveHourValues = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChartSummaryText))]
    [NotifyPropertyChangedFor(nameof(HasChartData))]
    [NotifyPropertyChangedFor(nameof(ShowChartBox))]
    [NotifyPropertyChangedFor(nameof(ShowChartHint))]
    private IReadOnlyList<HistoryChart.ChartPoint> weeklyValues = [];

    /// <summary>The chart's first-slot legend text - normally "5 hours", but a provider with no
    /// five-hour window at all (Cursor, and any other Other-only provider) shows that slot's real
    /// series label instead (see <see cref="UpdateHistory"/>), so the legend and hover caption never
    /// claim to be showing a five-hour window that was never fetched.</summary>
    [ObservableProperty]
    private string fiveHourLabelText = StatusTextMap.Resolve("Window_FiveHour");

    /// <summary>Same role as <see cref="FiveHourLabelText"/> for the chart's second slot, normally
    /// "Week".</summary>
    [ObservableProperty]
    private string weeklyLabelText = StatusTextMap.Resolve("Window_Weekly");

    /// <summary>The chart's visible time window - forwarded from <see cref="UpdateHistory"/> straight
    /// to the bound <see cref="HistoryChart.RangeStart"/>/<see cref="HistoryChart.RangeEnd"/>, so the
    /// chart draws the actual requested range instead of ending at the newest stored point.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChartSummaryText))]
    private DateTimeOffset rangeStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChartSummaryText))]
    private DateTimeOffset rangeEnd;

    /// <summary>Last week's figures, shifted forward by exactly seven days so the chart draws them
    /// on the same axis as the current window without knowing about the offset. Mirrors whichever
    /// current series is meaningful to compare against - see <see cref="UpdateHistory"/>.</summary>
    [ObservableProperty]
    private IReadOnlyList<HistoryChart.ChartPoint> previousWeekValues = [];

    /// <summary>Screen-reader summary of the whole diagram, reachable without a mouse hover: the
    /// visible time span, how many points it actually holds, and the highest one - the same kind of
    /// information the hover caption shows visually, since hovering has no keyboard equivalent.</summary>
    public string ChartSummaryText
    {
        get
        {
            var combined = FiveHourValues.Concat(WeeklyValues).ToList();
            var highest = combined.Count > 0 ? combined.Max(p => p.Percent) : 0;
            var spanDays = Math.Max(1, (int)(RangeEnd - RangeStart).TotalDays);
            return LocalizationService.Instance.Format("Chart.Summary",
                DurationFormatter.Describe(spanDays), combined.Count, StatusTextMap.UsagePercent(highest));
        }
    }

    public bool IsOk => Status == ProviderStatus.Ok;

    public bool IsNotOk => !IsOk;

    public bool IsStale => Status == ProviderStatus.Stale;

    /// <summary>The sign-in button shows only for a provider that supports it, and never before a
    /// snapshot has actually landed (<see cref="SignInState"/> alone cannot say that: it starts at
    /// <see cref="SignInState.Unknown"/> and a provider whose read fails outright never moves it).
    /// Once one has landed, the button shows whenever the tile is not known to be signed in and has
    /// no numbers to show - a read that fails for its own reasons must still leave a way in, or a
    /// provider that cannot even tell signed-out apart from broken strands the user on a tile that
    /// only ever says "failed". A failure that is plainly not about the sign-in (the server's own
    /// error, no answer, no connection) never shows it.</summary>
    public bool ShowSignIn => SupportsSignOut
        && !ErrorPresenter.IsNotASignInProblem(FailureKind)
        && SignInState != SignInState.SignedIn
        && (SignInState == SignInState.SignedOut || (_lastSnapshot is not null && _lastSnapshot.Windows.Count == 0));

    /// <summary>The header's own sign-in button: <see cref="ShowSignIn"/> unless the placeholder body
    /// already offers the same action, which would put two identical buttons on one tile. The
    /// Settings and Welcome windows have no body and keep using <see cref="ShowSignIn"/>.</summary>
    public bool ShowHeaderSignIn => ShowSignIn && !(ShowPlaceholder && HasAction && _actionKey == "Action_SignIn");

    /// <summary>The sign-out button shows only for a provider that supports it, and only once a
    /// web-sourced snapshot has actually reported success - never while <see cref="SignInState"/> is
    /// still <see cref="SignInState.Unknown"/>.</summary>
    public bool ShowSignOut => SupportsSignOut && SignInState == SignInState.SignedIn;

    /// <summary>Stale data is still data: it keeps its bars and its chart, dimmed and with the age
    /// written above them, instead of collapsing into a placeholder that looks exactly like a
    /// provider that was never found at all.</summary>
    public bool HasNumbers => IsOk || IsStale;

    public bool HasAction => !string.IsNullOrEmpty(ActionLabelText);

    public bool IsMini => Density == TileDensity.Mini;

    public bool ShowHeader => !IsMini;

    public bool ShowRows => HasNumbers && !IsMini;

    public bool ShowStaleNotice => IsStale && !IsMini && !IsShowingLastValues;

    public bool ShowDiagram => HasNumbers && Density == TileDensity.Full && !ChartHidden;

    /// <summary>Whether the chart in its current range has two readings or more in at least one
    /// series - the least a line needs.</summary>
    public bool HasChartData => FiveHourValues.Count >= 2 || WeeklyValues.Count >= 2;

    /// <summary>The bordered chart box: only once there is something to draw.</summary>
    public bool ShowChartBox => ShowDiagram && HasChartData;

    /// <summary>The one muted line standing in for the chart until it has two readings.</summary>
    public bool ShowChartHint => ShowDiagram && !HasChartData;

    public bool ShowPlaceholder => !HasNumbers && !IsMini;

    public bool ShowMiniRows => HasNumbers && IsMini;

    public bool ShowMiniHeadline => !HasNumbers && IsMini;

    /// <summary>True once any row is at (display-rounded) 100%, not merely Crit (which starts at
    /// 85%) - the state that actually stops work needs to look different from the one that only
    /// warns about it. Re-raised whenever rows are applied or updated, since it depends on the whole
    /// collection rather than any single observable property.</summary>
    public bool IsLimitReached => Rows.Any(r => r.UsedPercent >= 99.5);

    /// <summary>The highest color level among the windows this tile shows; what the "order by usage"
    /// mode sorts by. A tile without windows counts as normal.</summary>
    public UsageLevel UrgencyLevel => Rows.Select(r => r.Level).DefaultIfEmpty(UsageLevel.Ok).Max();

    /// <summary>Wired up by the eye-menu/visibility feature once it exists - this tile only raises intent.</summary>
    public event EventHandler? HideRequested;

    /// <summary>Raised by the tile header's own refresh button - wired up to a single-provider
    /// refresh, unlike the title bar's button which refreshes every provider.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Wired up by whatever the current status' action leads to (sign-in window, download page).</summary>
    public event EventHandler? ActionRequested;

    /// <summary>Raised by the tile header's own sign-out button - wired up the same way as <see
    /// cref="ActionRequested"/>, but always to a sign-out flow rather than whatever the current
    /// status' action happens to be.</summary>
    public event EventHandler? SignOutRequested;

    /// <param name="providerId">This tile's own account key (see <see cref="ProviderId"/>).</param>
    /// <param name="displayName">The provider's own display name, e.g. "Claude".</param>
    /// <param name="realProviderId">Which provider this actually is (see <see
    /// cref="RealProviderId"/>) - defaults to <paramref name="providerId"/>, correct for every tile
    /// except a further account, which passes its provider's real id separately.</param>
    public ProviderTileViewModel(string providerId, string displayName, string? realProviderId = null)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        RealProviderId = realProviderId ?? providerId;
    }

    [RelayCommand]
    private void Hide() => HideRequested?.Invoke(this, EventArgs.Empty);

    // When the sign-in window last reported a finished sign-in; see MarkSignedIn.
    private DateTimeOffset? _signInMarkedAt;

    /// <summary>A sign-in window just finished: the session exists from <paramref name="at"/> on.
    /// Any answer from a read that started before then looked at the session before it existed (a
    /// slow web read begun while the window was still open lands afterwards), so its "signed out"
    /// verdict no longer counts - see <see cref="Apply"/>. A read that started later is believed
    /// again, including a real "signed out".</summary>
    internal void MarkSignedIn(DateTimeOffset at)
    {
        _signInMarkedAt = at;
        SignInState = SignInState.SignedIn;
        SetWebSessionSignedIn(true);
    }

    /// <summary>True for a "signed out" verdict (the provider's own status or its web-session flag)
    /// from a read that started before the last <see cref="MarkSignedIn"/>.</summary>
    private bool IsSignedOutVerdictFromBeforeTheSignIn(ProviderSnapshot snapshot) =>
        _signInMarkedAt is { } marked
        && snapshot.FetchedAt < marked
        && (snapshot.WebSessionSignedIn == false || snapshot.Status == ProviderStatus.NotSignedIn);

    [RelayCommand]
    private void Refresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RunAction() => ActionRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The placeholder's own button: a "refresh now" or "fetch now" action asks for this
    /// tile's data again, every other action (sign-in, the WebView2 download) goes the same way as
    /// <see cref="RunAction"/>. Routing the retry through the sign-in flow instead opened a browser
    /// window for an account that was only offline.</summary>
    [RelayCommand]
    private void RunPlaceholderAction()
    {
        if (IsRefreshAction(_actionKey))
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        else
            ActionRequested?.Invoke(this, EventArgs.Empty);
    }

    internal static bool IsRefreshAction(string? actionKey) => actionKey is "Action_Retry" or "Action_FetchNow";

    [RelayCommand]
    private void SignOut() => SignOutRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(HasDiagnostics))]
    private void ToggleDetails() => DetailsExpanded = !DetailsExpanded;

    /// <summary>Ticks off the currently shown wait: remembers its <see
    /// cref="Models.ProviderSnapshot.WaitingSince"/> so <see cref="Apply"/> and <see
    /// cref="ApplyShowAttentionMark"/> stop marking THIS wait, while a newer one (a different
    /// WaitingSince on a later snapshot) still lights the mark up again on its own.</summary>
    [RelayCommand]
    private void DismissWaiting()
    {
        _dismissedWaitingAt = _lastSnapshot?.WaitingSince;
        IsWaitingForUser = false;
    }

    /// <summary>Puts the same lines on the clipboard, so reporting a stuck provider needs no
    /// screenshot. The clipboard call is a settable delegate purely so unit tests can read back what
    /// would have been copied without needing a live desktop; ClipboardHelper wraps it with a retry
    /// for when another process is holding the clipboard open.</summary>
    internal Action<string> CopyToClipboard { get; set; } = text => Clipboard.SetText(text);

    [RelayCommand]
    private void CopyDetails() => ClipboardHelper.SetTextSafely(DiagnosticsText, CopyToClipboard);

    /// <summary>Backs the context menu's "open usage page" entry - disabled rather than hidden for a
    /// provider <see cref="ProviderLinks"/> has no address for, since a greyed entry explains itself
    /// and a missing one does not.</summary>
    private bool CanOpenUsagePage() => ProviderLinks.UsagePage(RealProviderId) is not null;

    /// <summary>Opens the provider's own usage page in the system browser, best-effort - same
    /// try/catch shape as <c>SettingsViewModel.OpenLogsFolder</c>, since both are "hand the OS a path
    /// and move on" actions with nothing sensible to recover into on failure.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenUsagePage))]
    private void OpenUsagePage()
    {
        if (ProviderLinks.UsagePage(RealProviderId) is { } uri)
            OpenInBrowser(uri);
    }

    /// <summary>Whether the failure on the tile is one the provider's own status page can explain:
    /// the server answered with an error or did not answer. Also true while the last good values stay
    /// on show under the failure line.</summary>
    public bool ShowStatusLink => FailureKind is FailureKind.ServerError or FailureKind.Timeout
        && ProviderLinks.StatusPage(RealProviderId) is not null;

    /// <summary>True on Codex's stale tile that reads the local files only and has no web session yet:
    /// next to the hint, a link starts the web sign-in for live values.</summary>
    public bool ShowCodexSignInLink => _codexLocalHint && SignInState != SignInState.SignedIn && ShowStaleNotice;

    private bool _codexLocalHint;

    /// <summary>"Check Claude status" - the label of the status page link.</summary>
    public string StatusLinkText => LocalizationService.Instance.Format("Tile.CheckStatus", DisplayName);

    /// <summary>Opens the provider's status page in the system browser.</summary>
    [RelayCommand]
    private void OpenStatusPage()
    {
        if (ProviderLinks.StatusPage(RealProviderId) is { } uri)
            OpenInBrowser(uri);
    }

    private static void OpenInBrowser(Uri uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // Nothing sensible to recover into - opening a browser tab is best-effort.
        }
    }

    /// <summary>Re-derives every localized text field from the last snapshot, for an
    /// immediate language switch instead of waiting for the next scheduled fetch. Passes
    /// <c>advanceSilenceClock: false</c>: re-applying the same snapshot must not look like a fresh
    /// one arrived, or a language switch alone would push <see cref="LastSuccessOrStart"/> - and with
    /// it the 24h <see cref="IsSilent"/> proof - a full day into the future with no fetch involved.</summary>
    public void RefreshLocalizedText()
    {
        var keptFailure = _keptFailure;
        if (_lastSnapshot is { } snapshot)
            Apply(snapshot, DateTimeOffset.Now, _lastThresholds, _lastShowAttentionMark, advanceSilenceClock: false, refreshInterval: _lastRefreshInterval);
        if (keptFailure is not null)
            ShowKeptFailure(keptFailure);
        OnPropertyChanged(nameof(ToggleVisibilityActionText));
        OnPropertyChanged(nameof(StatusLinkText));
        OnPropertyChanged(nameof(ChartMenuHeader));
        OnPropertyChanged(nameof(NameTooltipText));
        OnPropertyChanged(nameof(SettingsRowText));
        OnPropertyChanged(nameof(DetailsMenuHeader));
        OnPropertyChanged(nameof(LastSuccessAgeText));
    }

    /// <summary><paramref name="thresholds"/> is optional (defaults to null, meaning "no marker on
    /// any row") purely so every existing caller that has no thresholds handy keeps compiling
    /// unchanged; a caller that does hold the provider's <see cref="ThresholdSettings"/> passes them
    /// here to light up the bar's own threshold marker. <paramref name="showAttentionMark"/> mirrors
    /// <see cref="Models.AppSettings.ShowAttentionMark"/> - false forces <see
    /// cref="IsWaitingForUser"/> to false regardless of what <paramref name="snapshot"/> itself says.
    /// <paramref name="advanceSilenceClock"/> is false only for <see cref="RefreshLocalizedText"/>'s
    /// re-apply of the very same snapshot - every real caller (a fetch actually landing) leaves it at
    /// its default true. <paramref name="refreshInterval"/> is the poll cadence <see
    /// cref="LastUpdatedToolTip"/> compares the data's age against to decide whether it names that
    /// age - null (every existing caller that has none handy) leaves the tooltip empty.</summary>
    public void Apply(
        ProviderSnapshot snapshot, DateTimeOffset now, ThresholdSettings? thresholds = null,
        bool showAttentionMark = true, bool advanceSilenceClock = true, TimeSpan? refreshInterval = null)
    {
        var staleSignedOut = IsSignedOutVerdictFromBeforeTheSignIn(snapshot);
        // An answer that carries nothing but that stale verdict has nothing else to show either: the
        // read that follows the sign-in replaces it.
        if (staleSignedOut && snapshot.Windows.Count == 0 && snapshot.Status == ProviderStatus.NotSignedIn)
            return;

        // A failed read after good numbers keeps those numbers on show instead of emptying the tile.
        if (ShouldKeepLastValues(snapshot, now))
        {
            ShowKeptFailure(snapshot);
            return;
        }

        _keptFailure = null;
        IsShowingLastValues = false;
        FailureNoticeText = "";
        _lastSnapshot = snapshot;
        _lastThresholds = thresholds;
        if (!staleSignedOut)
        {
            if (snapshot.WebSessionSignedIn is { } webSignedIn)
                SetWebSessionSignedIn(webSignedIn);
            else if (snapshot.Status == ProviderStatus.NotSignedIn)
                SetWebSessionSignedIn(false);
            else if (snapshot.Status == ProviderStatus.Ok && snapshot.SourceKind == SourceKind.WebSession)
                SetWebSessionSignedIn(true);
        }
        if (snapshot.Windows.Count > 0)
        {
            var kinds = snapshot.Windows.Select(window => window.Kind).ToHashSet();
            if (_knownWindowKinds is null || !_knownWindowKinds.SetEquals(kinds))
            {
                _knownWindowKinds = kinds;
                OnPropertyChanged(nameof(HasFiveHourWindow));
                OnPropertyChanged(nameof(HasWeeklyWindow));
                OnPropertyChanged(nameof(HasOtherWindow));
                OnPropertyChanged(nameof(KnownWindowKinds));
            }
            RebuildWindowToggles(snapshot.Windows);
        }
        _lastShowAttentionMark = showAttentionMark;
        _lastRefreshInterval = refreshInterval;
        IsWaitingForUser = showAttentionMark && !AttentionDisabled && snapshot.IsWaitingForUser && snapshot.WaitingSince != _dismissedWaitingAt;
        Status = snapshot.Status;
        // A provider that read through a cheaper route this tick can still say what it learned about
        // its own browser session (see ProviderSnapshot.WebSessionSignedIn) - that wins over what the
        // shown snapshot's own status would otherwise imply, because it is the newer fact about the
        // session the two buttons act on.
        SignInState = staleSignedOut ? SignInState : snapshot.WebSessionSignedIn switch
        {
            true => SignInState.SignedIn,
            false => SignInState.SignedOut,
            null => snapshot.Status switch
            {
                ProviderStatus.NotSignedIn => SignInState.SignedOut,
                ProviderStatus.Ok when snapshot.SourceKind is SourceKind.WebSession or SourceKind.LocalLogin => SignInState.SignedIn,
                _ => SignInState,
            },
        };
        // Same rule as the account name below: a tick whose source cannot know the tier (a local
        // limit hit, a failed read) keeps the last one shown; only a sign-out forgets it.
        PlanText = PlanTier.Normalize(snapshot.PlanType) ?? (snapshot.Status == ProviderStatus.NotSignedIn ? null : PlanText);
        // One tick without a name (a failed read, a source that cannot know it) must not blank the
        // account again; only a sign-out forgets it.
        AccountText = snapshot.AccountLabel ?? (snapshot.Status == ProviderStatus.NotSignedIn ? null : AccountText);
        SourceBadgeText = StatusTextMap.SourceBadge(snapshot.SourceKind);
        HasSource = snapshot.SourceKind != SourceKind.None;
        // A failed read keeps the last working source; a sign-out forgets it like the account.
        if (snapshot.SourceKind != SourceKind.None)
            LastSourceKind = snapshot.SourceKind;
        else if (snapshot.Status == ProviderStatus.NotSignedIn)
            LastSourceKind = null;
        SkipReasonWord = snapshot.SkipReasonWord;

        var (headlineKey, reasonKey, actionKey) = ErrorPresenter.Describe(
            snapshot.Status, snapshot.Error, snapshot.SourceKind, SupportsInAppSignIn, RealProviderId);
        _codexLocalHint = reasonKey == ErrorPresenter.CodexLocalReason;
        OnPropertyChanged(nameof(ShowCodexSignInLink));
        FailureKind = snapshot.Status == ProviderStatus.Failed ? snapshot.Error?.Kind ?? FailureKind.Other : FailureKind.Other;
        HeadlineText = StatusTextMap.ResolveFailure(headlineKey, DisplayName, snapshot.Error?.HttpStatus);
        ReasonText = StatusTextMap.ResolveReason(reasonKey, snapshot.Error, DisplayName);
        _actionKey = actionKey;
        ActionLabelText = actionKey is null ? null : StatusTextMap.Resolve(actionKey);
        if (snapshot.DataTimestamp is not null)
        {
            LastSuccessAt = snapshot.DataTimestamp;
            LastFetchAt = snapshot.FetchedAt;
        }

        if (advanceSilenceClock && snapshot.Status is ProviderStatus.Ok or ProviderStatus.Stale)
            LastSuccessOrStart = now;
        RefreshSilentState(now);

        ApplyRows(snapshot.Windows, now, thresholds);

        Diagnostics.Clear();
        foreach (var line in snapshot.Diagnostics ?? [])
            Diagnostics.Add(line);
        if (Diagnostics.Count == 0)
            DetailsExpanded = false;
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(NameTooltipText));
        ToggleDetailsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DiagnosticsText));
        // ShowSignIn reads the landed snapshot and the row count as well as SignInState, and the
        // generated notification for SignInState only fires when that value actually changes - a
        // provider stuck at Unknown would otherwise never reveal its button.
        OnPropertyChanged(nameof(ShowSignIn));
        OnPropertyChanged(nameof(ShowHeaderSignIn));
    }

    /// <summary>Whether <paramref name="snapshot"/> is a failed read that arrives while the tile still
    /// shows numbers from an Ok or Stale snapshot younger than <see cref="ProviderFreshness.StaleAfter"/>.</summary>
    private bool ShouldKeepLastValues(ProviderSnapshot snapshot, DateTimeOffset now) =>
        snapshot.Status == ProviderStatus.Failed
        && Status is ProviderStatus.Ok or ProviderStatus.Stale
        && Rows.Count > 0
        && LastSuccessAt is { } lastGood
        && now - lastGood < ProviderFreshness.StaleAfter;

    /// <summary>Puts the failed read's wording in the notice line and leaves rows, chart, status and
    /// age exactly as the last good snapshot set them.</summary>
    private void ShowKeptFailure(ProviderSnapshot snapshot)
    {
        _keptFailure = snapshot;
        FailureKind = snapshot.Error?.Kind ?? FailureKind.Other;
        var (headlineKey, reasonKey, _) = ErrorPresenter.Describe(ProviderStatus.Failed, snapshot.Error);
        FailureNoticeText = $"{StatusTextMap.ResolveFailure(headlineKey, DisplayName, snapshot.Error?.HttpStatus)} · "
            + StatusTextMap.ResolveReason(reasonKey, snapshot.Error, DisplayName);
        IsShowingLastValues = true;
    }

    /// <summary>Recomputes <see cref="IsSilent"/> (and, with it, <see cref="SilentReasonText"/>)
    /// against the given clock reading - called from both <see cref="Apply"/> and <see
    /// cref="TickCountdowns"/>, since the 24h threshold can be crossed by time passing alone, with
    /// no snapshot ever arriving to trigger a re-check.</summary>
    private void RefreshSilentState(DateTimeOffset now)
    {
        var silent = LastSuccessOrStart is { } at && now - at >= TimeSpan.FromHours(24);
        var reason = silent
            ? LocalizationService.Instance.Format("State.Silent.Reason", LastSuccessOrStart!.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))
            : "";

        // Only a real change is announced; nothing else writes these two.
        if (silent != IsSilent)
        {
            IsSilent = silent;
            OnPropertyChanged(nameof(IsSilent));
        }

        if (reason != SilentReasonText)
        {
            SilentReasonText = reason;
            OnPropertyChanged(nameof(SilentReasonText));
        }
    }

    /// <summary>Updates the existing <see cref="UsageRowViewModel"/> instances in place when the
    /// windows actually shown (after <see cref="ResolveToggleVisibility"/> filtering - <see
    /// cref="ShowFiveHour"/>/<see cref="ShowWeekly"/> for their own kind, <see cref="HiddenWindows"/>
    /// for every other row) have the same count and the same <see cref="WindowKind"/> order as <see
    /// cref="Rows"/> already has (the normal case, every 60 seconds) - otherwise falls back to
    /// clear-and-rebuild. Update-in-place
    /// stops every successful fetch from destroying and recreating the bound `UsageBar` visuals (and
    /// any keyboard focus or tooltip inside them). A window the caller chose to hide is filtered out
    /// here only - it still reaches <see cref="Storage.HistoryStore.Append"/> unconditionally, so
    /// hiding a row can never create a gap in the recorded history.</summary>
    private void ApplyRows(IReadOnlyList<UsageWindow> windows, DateTimeOffset now, ThresholdSettings? thresholds)
    {
        var visibleWindows = windows.Where(w => ResolveToggleVisibility(w.Kind, w.Label))
            .OrderBy(w => w.Kind)
            .ToList();

        if (visibleWindows.Count == Rows.Count && SameKindOrder(visibleWindows))
        {
            for (var i = 0; i < visibleWindows.Count; i++)
            {
                var key = Rows[i].Update(visibleWindows[i], now, Density, ThresholdPercentFor(visibleWindows[i].Kind, thresholds));
                if (key is not null && !IsHidden)
                    AnnounceLevel(Rows[i], key);
            }
        }
        else
        {
            Rows.Clear();
            foreach (var window in visibleWindows)
                Rows.Add(new UsageRowViewModel(window, now, Density, ThresholdPercentFor(window.Kind, thresholds)));
        }

        PushWeekTokensToRows();
        OnPropertyChanged(nameof(IsLimitReached));
        // Rebuilt rows start without a forecast: hand them the series the last history read produced.
        RefreshForecastsFromLastSeries();
    }

    /// <summary>Raised with the finished sentence when a shown window rises into yellow, red or full;
    /// the main window hands it to screen readers. Never raised for a hidden tile.</summary>
    internal event Action<string>? LevelAnnounced;

    private void AnnounceLevel(UsageRowViewModel row, string key) =>
        LevelAnnounced?.Invoke(LocalizationService.Instance.Format(
            key, HeaderDisplayName, row.LabelText, StatusTextMap.UsagePercent(row.UsedPercent)));

    /// <summary>Re-filters <see cref="Rows"/> against the last snapshot the instant a
    /// window-visibility check box changes, instead of waiting for the next scheduled fetch (up to a
    /// minute away) to reflect a preference that is supposed to apply instantly.</summary>
    private void ReapplyRowsFromLastSnapshot()
    {
        if (_lastSnapshot is { } snapshot)
            ApplyRows(snapshot.Windows, DateTimeOffset.Now, _lastThresholds);
    }

    /// <summary>Builds <see cref="WindowToggles"/> from every distinct row label the snapshot
    /// carries, in the order they first appear - rebuilt only when that set of labels actually
    /// changes; otherwise just refreshes each existing toggle's <see
    /// cref="WindowToggleViewModel.DisplayText"/>; for a language switch (see <see
    /// cref="RefreshLocalizedText"/>) re-applying the very same snapshot.</summary>
    private void RebuildWindowToggles(IReadOnlyList<UsageWindow> windows)
    {
        var distinctWindows = windows.GroupBy(w => w.Label).Select(g => g.First()).ToList();

        if (distinctWindows.Count == WindowToggles.Count
            && distinctWindows.Select(w => w.Label).SequenceEqual(WindowToggles.Select(t => t.Label)))
        {
            foreach (var (window, toggle) in distinctWindows.Zip(WindowToggles))
            {
                toggle.DisplayText = StatusTextMap.Resolve(window.Label);
                SetToggleVisibilitySilently(toggle, ResolveToggleVisibility(window.Kind, window.Label));
            }
            return;
        }

        foreach (var toggle in WindowToggles)
            toggle.PropertyChanged -= OnWindowToggleChanged;
        WindowToggles.Clear();
        foreach (var window in distinctWindows)
        {
            var toggle = new WindowToggleViewModel(
                window.Label, window.Kind, StatusTextMap.Resolve(window.Label), ResolveToggleVisibility(window.Kind, window.Label));
            toggle.PropertyChanged += OnWindowToggleChanged;
            WindowToggles.Add(toggle);
        }
    }

    /// <summary>Whether one row should show, per whichever setting governs its own <see
    /// cref="WindowKind"/> - shared by <see cref="RebuildWindowToggles"/> (building or re-syncing a
    /// toggle) and <see cref="ApplyRows"/> (filtering <see cref="Rows"/> itself), so the two can never
    /// disagree about one row.</summary>
    private bool ResolveToggleVisibility(WindowKind kind, string label) => kind switch
    {
        WindowKind.FiveHour => ShowFiveHour,
        WindowKind.Weekly => ShowWeekly,
        _ => !HiddenWindows.Contains(label),
    };

    /// <summary>Re-syncs every existing <see cref="WindowToggles"/> entry's own <see
    /// cref="WindowToggleViewModel.IsVisible"/> against whatever <see cref="ShowFiveHour"/>/<see
    /// cref="ShowWeekly"/>/<see cref="HiddenWindows"/> currently hold and re-filters <see
    /// cref="Rows"/> to match - needed only when <see cref="HiddenWindows"/> is reassigned wholesale
    /// from outside (an import or a reset, see <see cref="ViewModels.SettingsViewModel"/>), since
    /// that plain assignment touches neither on its own.</summary>
    public void SyncWindowVisibility()
    {
        foreach (var toggle in WindowToggles)
            SetToggleVisibilitySilently(toggle, ResolveToggleVisibility(toggle.Kind, toggle.Label));
        ReapplyRowsFromLastSnapshot();
    }

    /// <summary>Assigns <see cref="WindowToggleViewModel.IsVisible"/> without going through <see
    /// cref="OnWindowToggleChanged"/> - used whenever the new value is being derived FROM this tile's
    /// own state rather than a user click, so applying it can never re-enter <see
    /// cref="HiddenWindows"/>/<see cref="ShowFiveHour"/>/<see cref="ShowWeekly"/> or fire <see
    /// cref="OtherWindowVisibilityChanged"/> for a change nothing outside this tile made.</summary>
    private void SetToggleVisibilitySilently(WindowToggleViewModel toggle, bool isVisible)
    {
        toggle.PropertyChanged -= OnWindowToggleChanged;
        toggle.IsVisible = isVisible;
        toggle.PropertyChanged += OnWindowToggleChanged;
    }

    /// <summary>Routes one <see cref="WindowToggleViewModel.IsVisible"/> flip to whichever setting it
    /// actually governs: a FiveHour/Weekly row flips the tile's own <see cref="ShowFiveHour"/>/<see
    /// cref="ShowWeekly"/> (which already re-filters <see cref="Rows"/> and announces itself through
    /// <see cref="ShowFiveHourChanged"/>/<see cref="ShowWeeklyChanged"/>), any other row updates <see
    /// cref="HiddenWindows"/> directly and announces <see cref="OtherWindowVisibilityChanged"/> for
    /// MainViewModel to persist.</summary>
    private void OnWindowToggleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WindowToggleViewModel.IsVisible) || sender is not WindowToggleViewModel toggle)
            return;

        switch (toggle.Kind)
        {
            case WindowKind.FiveHour:
                ShowFiveHour = toggle.IsVisible;
                break;
            case WindowKind.Weekly:
                ShowWeekly = toggle.IsVisible;
                break;
            default:
                if (toggle.IsVisible)
                    HiddenWindows.Remove(toggle.Label);
                else
                    HiddenWindows.Add(toggle.Label);
                ReapplyRowsFromLastSnapshot();
                OtherWindowVisibilityChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>Moves every bar's threshold marker to a changed notification threshold right away,
    /// instead of waiting for the next fetch to carry the new value in.</summary>
    internal void ApplyThresholds(ThresholdSettings thresholds)
    {
        _lastThresholds = thresholds;
        ReapplyRowsFromLastSnapshot();
    }

    /// <summary>Applies a live "mark a waiting tile" setting change to the last snapshot immediately,
    /// instead of waiting for the next scheduled fetch (up to a minute away) - same reasoning as <see
    /// cref="ReapplyRowsFromLastSnapshot"/>.</summary>
    internal void ApplyShowAttentionMark(bool value)
    {
        _lastShowAttentionMark = value;
        IsWaitingForUser = value && !AttentionDisabled && (_lastSnapshot?.IsWaitingForUser ?? false)
            && _lastSnapshot?.WaitingSince != _dismissedWaitingAt;
    }

    /// <summary>Null (no marker) unless <paramref name="thresholds"/> is supplied and that window's
    /// own notification is switched on - matches exactly the two windows <see
    /// cref="NotificationService.Evaluate"/> already watches.</summary>
    private static double? ThresholdPercentFor(WindowKind kind, ThresholdSettings? thresholds) => (kind, thresholds) switch
    {
        (WindowKind.FiveHour, { FiveHourEnabled: true } t) => t.FiveHour,
        (WindowKind.Weekly, { WeeklyEnabled: true } t) => t.Weekly,
        _ => null,
    };

    private bool SameKindOrder(List<UsageWindow> windows)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            if (Rows[i].Kind != windows[i].Kind)
                return false;
        }

        return true;
    }

    public void TickCountdowns(DateTimeOffset now)
    {
        foreach (var row in Rows)
            row.RefreshCountdown(now, Density);
        // The age texts only change when their minute (or second, below a minute) turns over.
        if (LastUpdatedText != _raisedLastUpdatedText)
            OnPropertyChanged(nameof(LastUpdatedText));
        if (LastUpdatedToolTip != _raisedLastUpdatedToolTip)
            OnPropertyChanged(nameof(LastUpdatedToolTip));
        if (LastSuccessAgeText != _raisedLastSuccessAgeText)
            OnPropertyChanged(nameof(LastSuccessAgeText));
        RefreshSilentState(now);
    }

    // What the bindings last heard for the two age texts, whoever raised it: a tick compares against
    // these instead of re-raising an identical value every second.
    private string? _raisedLastUpdatedText;
    private string? _raisedLastUpdatedToolTip;
    private string? _raisedLastSuccessAgeText;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LastUpdatedText):
                _raisedLastUpdatedText = LastUpdatedText;
                break;
            case nameof(LastUpdatedToolTip):
                _raisedLastUpdatedToolTip = LastUpdatedToolTip;
                break;
            case nameof(LastSuccessAgeText):
                _raisedLastSuccessAgeText = LastSuccessAgeText;
                break;
        }

        base.OnPropertyChanged(e);
    }

    /// <summary>A density switch (from the eye-menu, or the automatic-height stepper) is the only
    /// event besides a snapshot that can change whether a row's <see
    /// cref="UsageRowViewModel.ClockText"/> should be showing - re-derive every row's countdown text
    /// right away instead of waiting for the next tick.</summary>
    partial void OnDensityChanged(TileDensity value)
    {
        var now = DateTimeOffset.Now;
        foreach (var row in Rows)
            row.RefreshCountdown(now, value);
        // A forecast belongs to Full density only: show it again or clear it right away.
        RefreshForecastsFromLastSeries();
    }

    /// <summary>Feeds the tile's self-drawn history chart - a time-ordered list of points per
    /// window, already filtered to whatever range the Settings window's chart-range picker currently
    /// selects, plus the range itself so the chart draws real elapsed time rather than evenly
    /// spaced samples. <paramref name="previousWeekPoints"/> is last week's slice, already shifted
    /// forward by seven days by the caller - empty when the comparison line is turned off.</summary>
    public void UpdateHistory(
        IReadOnlyList<HistoryPoint> points, IReadOnlyList<HistoryPoint> previousWeekPoints,
        DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        var fiveHour = points.Where(p => p.Window == WindowKind.FiveHour)
            .Select(p => new HistoryChart.ChartPoint(p.Timestamp, p.Percent)).ToArray();
        var weekly = points.Where(p => p.Window == WindowKind.Weekly)
            .Select(p => new HistoryChart.ChartPoint(p.Timestamp, p.Percent)).ToArray();

        // A provider with no five-hour/weekly windows at all (Cursor, and any other web-backed
        // provider that only ever reports WindowKind.Other) would otherwise show no diagram - its
        // own Other series fill the two chart slots instead, in the order their label first appears
        // in this snapshot (matches UsageWindow order, e.g. Cursor models/Other models/Grok Bot).
        // Each slot holds exactly one label's own points, never a mix of two.
        var otherLabels = points.Where(p => p.Window == WindowKind.Other)
            .Select(p => p.Label).Where(label => label is { Length: > 0 }).Distinct().ToList();

        string? slotOneLabel = null;
        string? slotTwoLabel = null;
        var slotOneValues = fiveHour;
        var slotTwoValues = weekly;

        if (fiveHour.Length == 0 && otherLabels.Count > 0)
        {
            slotOneLabel = otherLabels[0];
            slotOneValues = points.Where(p => p.Window == WindowKind.Other && p.Label == slotOneLabel)
                .Select(p => new HistoryChart.ChartPoint(p.Timestamp, p.Percent)).ToArray();
        }
        if (weekly.Length == 0 && otherLabels.Count > (slotOneLabel is null ? 0 : 1))
        {
            slotTwoLabel = otherLabels[slotOneLabel is null ? 0 : 1];
            slotTwoValues = points.Where(p => p.Window == WindowKind.Other && p.Label == slotTwoLabel)
                .Select(p => new HistoryChart.ChartPoint(p.Timestamp, p.Percent)).ToArray();
        }

        FiveHourValues = slotOneValues;
        WeeklyValues = slotTwoValues;
        FiveHourLabelText = StatusTextMap.Resolve(slotOneLabel ?? "Window_FiveHour");
        WeeklyLabelText = StatusTextMap.Resolve(slotTwoLabel ?? "Window_Weekly");
        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
        // Mirrors the weekly series when it has anything to show, otherwise the five-hour one -
        // comparing a five-hour window against the same clock time a week ago is meaningful, but
        // comparing it against a weekly figure is not, so the two kinds are never mixed. Always the
        // real WindowKind.Weekly/FiveHour data, never a slot that borrowed an Other series above.
        var mirrorKind = weekly.Length >= 2 ? WindowKind.Weekly : WindowKind.FiveHour;
        PreviousWeekValues = previousWeekPoints.Where(p => p.Window == mirrorKind)
            .Select(p => new HistoryChart.ChartPoint(p.Timestamp, p.Percent)).ToArray();

        _forecastFiveHour = fiveHour;
        _forecastWeekly = weekly;
        _forecastNow = rangeEnd;
        RefreshForecasts(fiveHour, weekly, rangeEnd);
    }

    // The five-hour and weekly series of the last history read, so a row rebuild or a density switch
    // can bring the forecasts back without waiting for the next read.
    private IReadOnlyList<HistoryChart.ChartPoint>? _forecastFiveHour;
    private IReadOnlyList<HistoryChart.ChartPoint>? _forecastWeekly;
    private DateTimeOffset _forecastNow;

    private void RefreshForecastsFromLastSeries()
    {
        if (_forecastFiveHour is { } fiveHour && _forecastWeekly is { } weekly)
            RefreshForecasts(fiveHour, weekly, _forecastNow);
    }

    /// <summary>A guess on old numbers is worse than silence: a forecast only ever shows at Full
    /// density, for an Ok, non-silent provider - every other row is explicitly cleared rather than
    /// left holding a stale answer from before the density/status changed.</summary>
    private void RefreshForecasts(
        IReadOnlyList<HistoryChart.ChartPoint> fiveHour, IReadOnlyList<HistoryChart.ChartPoint> weekly, DateTimeOffset now)
    {
        var eligible = Density == TileDensity.Full && IsOk && !IsSilent;
        foreach (var row in Rows)
        {
            if (!eligible)
            {
                row.ClearForecast();
                continue;
            }

            switch (row.Kind)
            {
                case WindowKind.FiveHour:
                    row.UpdateForecast(fiveHour, now, TimeSpan.FromHours(2));
                    break;
                case WindowKind.Weekly:
                    row.UpdateForecast(weekly, now, TimeSpan.FromHours(24));
                    break;
                default:
                    row.ClearForecast();
                    break;
            }
        }
    }
}
