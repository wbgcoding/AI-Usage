using System.Text.Json;
using System.Text.RegularExpressions;
using AiUsage.Io;
using AiUsage.Models;
using AiUsage.Providers.Parsing;
using AiUsage.Services;
using AiUsage.Storage;
using AiUsage.Web;

namespace AiUsage.Providers;

/// <summary>
/// Reads OpenAI Codex's usage: its local session files always, and - once the app's own browser
/// session for chatgpt.com is signed in - the account's own usage endpoint on top of it, so the
/// numbers keep moving on a day the tool was not used at all. Both sources go through the shared
/// chooser; they are never mixed into one window list. Never throws.
/// </summary>
public sealed partial class CodexProvider : IUsageProvider
{
    // A run of short sessions can leave many files with nothing usable in them, so the scan has to
    // reach well past the newest handful. The loop returns on the first match, so this ceiling only
    // costs anything when there is genuinely nothing to find - and the age cut-off bounds that case.
    private const int MaxFilesToCheck = 40;

    // Real session files live under sessions/YYYY/MM/DD/ - four levels including the root is enough
    // for that layout with a little headroom, and bounds the walk regardless of what is actually there.
    private const int MaxDepth = 6;

    private static readonly TimeSpan OldestFileToOpen = SessionLineAge.MaxAge;

    // One browser read per this span, however often the tile ticks: the session files are free to
    // re-read and move with every turn, a page load in the hidden session is the expensive part.
    private static readonly TimeSpan WebReadInterval = TimeSpan.FromMinutes(5);

    // A single local reading that jumps this many points within the same reset window, unconfirmed
    // by the very next reading, is held back rather than shown - see ApplyPlausibilityGuard.
    private const double PlausibilityJumpThreshold = 40;

    // With no further Codex use there is no newer line to confirm or contradict a held jump; after
    // this long without a contradiction it is shown, so a real jump does not stay hidden until reset.
    private static readonly TimeSpan HeldJumpTimeout = TimeSpan.FromMinutes(15);

    private readonly string _sessionsRoot;
    private readonly Func<DateTimeOffset> _now;
    private readonly WebUsageSource? _webSource;
    private readonly AppSettings? _settings;
    private readonly Action<AppSettings>? _saveSettings;
    private readonly Func<TimeSpan> _attentionMaxAge;
    private readonly string _accountKey;
    private readonly LogService _log = LogService.Shared;

    private ProviderSnapshot? _lastWebSnapshot;
    private DateTimeOffset? _lastWebReadAt;
    private int _signInCompleted;
    private bool? _webSessionSignedIn;
    private string? _lastWebAccountLabel;

    // Per window kind: the last reading this provider actually showed/stored, and - while one
    // reading is being held back as a suspected outlier - the value it was held back in favour of.
    private readonly Dictionary<WindowKind, (double UsedPercent, DateTimeOffset? ResetsAt)> _lastAcceptedLocalReading = new();
    private readonly Dictionary<WindowKind, (double UsedPercent, DateTimeOffset? ResetsAt, DateTimeOffset? DataTimestamp, DateTimeOffset FirstSeen)> _pendingLocalOutlier = new();

    public CodexProvider() : this(DefaultSessionsRoot(), now: null)
    {
    }

    /// <summary>Real construction once a web session exists: <paramref name="webSource"/> reads the
    /// account's own usage endpoint, <paramref name="settings"/>/<paramref name="saveSettings"/>
    /// carry the discovered path across ticks (see <see cref="AppSettings.WebUsagePaths"/>).
    /// <paramref name="attentionMaxAge"/> is a delegate rather than a snapshot value so a change to
    /// <see cref="AppSettings.AttentionMaxAgeMinutes"/> takes effect on this provider's very next read
    /// instead of only after a restart; it defaults to a fixed <see
    /// cref="AttentionDetector.DefaultMaxAge"/> (today's two hours) so every existing construction
    /// keeps compiling unchanged. <see cref="Services.ProviderRegistry"/> is the one caller that ever
    /// passes a delegate reading the user's own configured value.</summary>
    public CodexProvider(AppSettings settings, Action<AppSettings> saveSettings, WebUsageSource webSource, Func<TimeSpan>? attentionMaxAge = null)
        : this(DefaultSessionsRoot(), now: null, webSource, settings, saveSettings, attentionMaxAge)
    {
    }

    /// <summary>A further Codex account: a web session only, no local session files - those on this
    /// machine belong to whichever account the Codex CLI itself is signed into, never to a further
    /// account added here (same reasoning as <see cref="ClaudeProvider"/>'s own web-only
    /// constructor).</summary>
    public static CodexProvider ForWebOnlyAccount(
        string accountKey, AppSettings settings, Action<AppSettings> saveSettings, WebUsageSource webSource) =>
        new(sessionsRoot: "", now: null, webSource, settings, saveSettings, attentionMaxAge: null, accountKey);

    /// <summary>Test seam: a fixture directory and a fixed clock instead of the real ~/.codex/sessions and DateTimeOffset.Now.</summary>
    internal CodexProvider(
        string sessionsRoot,
        Func<DateTimeOffset>? now,
        WebUsageSource? webSource = null,
        AppSettings? settings = null,
        Action<AppSettings>? saveSettings = null,
        Func<TimeSpan>? attentionMaxAge = null,
        string? accountKey = null)
    {
        _sessionsRoot = sessionsRoot;
        _now = now ?? (() => DateTimeOffset.Now);
        _webSource = webSource;
        _settings = settings;
        _saveSettings = saveSettings;
        _attentionMaxAge = attentionMaxAge ?? (() => AttentionDetector.DefaultMaxAge);
        _accountKey = accountKey ?? Id;
    }

    public string Id => "codex";

    /// <summary>This provider's own id for the primary account (the one with local session files),
    /// or that id plus a "#2"/"#3"/... suffix for a further, web-only one - see <see
    /// cref="Services.IUsageProvider.AccountKey"/>.</summary>
    public string AccountKey => _accountKey;

    public string DisplayName => "Codex";

    public IReadOnlyList<string> ReadLocations => (_sessionsRoot.Length > 0, _webSource is not null) switch
    {
        (true, false) => ["~/.codex/sessions"],
        (true, true) => ["~/.codex/sessions", LocalizationService.Instance["About.ReadLocationWebSession"]],
        _ => [LocalizationService.Instance["About.ReadLocationWebSession"]],
    };

    // Only once the app actually owns a browser session for this provider - a build without one has
    // nothing for the two buttons to act on.
    public bool SupportsInAppSignIn => _webSource is not null;

    /// <summary>A sign-in just finished: the cached answer from before it (usually "not signed in")
    /// must not stand for the rest of the throttle interval.</summary>
    public void SignInCompleted() => Interlocked.Exchange(ref _signInCompleted, 1);

    // The primary account can hold no further account of its own kind (Codex's own CLI sign-in is
    // one machine-wide account) - but a further, web-only Codex account can itself never sprout a
    // third layer, so this only ever answers for the primary instance.
    public bool SupportsMultipleAccounts => _sessionsRoot.Length > 0;

    // The web read below marshals its own work onto the WebView2 thread it owns and expects to start
    // on the calling thread; the local scan that runs alongside it moves itself onto the thread pool
    // instead (see FetchAsync), so nothing blocks the UI thread either way.
    public bool RunsOnUiThread => _webSource is not null;

    private static string DefaultSessionsRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    /// <summary>Both sources are offered to the shared chooser (available beats nothing, more windows
    /// beat fewer, newer beats older), never merged. Without a web session that is a single result -
    /// wrapping it still routes it through the same rule every other provider follows.</summary>
    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var fetchedAt = _now();

        // A further, web-only account (see ForWebOnlyAccount): the local session files on this
        // machine belong to the CLI's own signed-in account, never to this one, so there is nothing
        // to scan at all - only the web read below ever answers for it.
        if (_sessionsRoot.Length == 0)
        {
            var webOnly = await ReadWebAsync(fetchedAt, ct);
            return webOnly is not null
                ? SnapshotChooser.Pick([webOnly]) with { WebSessionSignedIn = _webSessionSignedIn }
                : new ProviderSnapshot(
                    ProviderId: AccountKey, Windows: [], PlanType: null, SourceKind: SourceKind.None,
                    FetchedAt: fetchedAt, DataTimestamp: null,
                    Status: _webSessionSignedIn == false ? ProviderStatus.NotSignedIn : ProviderStatus.Failed,
                    Error: null, WebSessionSignedIn: _webSessionSignedIn);
        }

        if (_webSource is null)
            return SnapshotChooser.Pick([FetchCore(fetchedAt, ct).Snapshot]);

        // This provider stays off the scheduler's thread-pool wrap (see RunsOnUiThread) so the web
        // read keeps starting on the calling thread it needs - which would otherwise leave the
        // synchronous directory walk running there too.
        var local = await Task.Run(() => FetchCore(fetchedAt, ct), ct);
        var web = await ReadWebAsync(fetchedAt, ct);

        // A local read that found no rate-limit line at all still reports 0% rather than an empty
        // tile. That placeholder must not outrank real numbers from the web session: it carries the
        // same two windows and the same timestamp, so it would win the chooser's cheapest-source
        // tie-break and hide them.
        var candidates = new List<ProviderSnapshot>();
        if (web is not null)
            candidates.Add(web);
        if (web is null || local.FromSessionFile)
            candidates.Add(local.Snapshot);

        var chosen = SnapshotChooser.Pick(candidates);

        // The sign-in state belongs to the browser session, not to whichever source happened to win:
        // the local files answer perfectly well while the session is signed out, and the tile still
        // has to be able to offer the sign-in that makes the numbers live. "Waiting for the user"
        // comes the other way round - only the session files can see it - so it has to survive a tick
        // the web snapshot wins, or the attention mark would never appear again once signed in. The
        // combined WaitingSince is the newer of the two read routes' own timestamps, null when
        // neither is waiting.
        var combinedWaitingSince = (chosen.WaitingSince, local.Snapshot.WaitingSince) switch
        {
            (null, null) => (DateTimeOffset?)null,
            ({ } a, null) => a,
            (null, { } b) => b,
            ({ } a, { } b) => a > b ? a : b,
        };

        return chosen with
        {
            // The address belongs to the signed-in account, not to whichever source won the numbers:
            // a local session file that outranks the web read this tick must not blank it again.
            AccountLabel = chosen.AccountLabel ?? _lastWebAccountLabel,
            WebSessionSignedIn = _webSessionSignedIn,
            IsWaitingForUser = chosen.IsWaitingForUser || local.Snapshot.IsWaitingForUser,
            WaitingSince = combinedWaitingSince,
        };
    }

    /// <summary>The web read, throttled to <see cref="WebReadInterval"/>: in between, the snapshot
    /// from the last real read is offered again, so the cheap local source can keep ticking at the
    /// scheduler's own pace without paying for a page load every time.</summary>
    private async Task<ProviderSnapshot?> ReadWebAsync(DateTimeOffset fetchedAt, CancellationToken ct)
    {
        if (_webSource is null || _settings is null || _saveSettings is null)
            return null;

        // Nothing was ever signed in here: starting a browser session would cost a whole WebView2
        // process and a request to the provider's site for a user who may never want that route at
        // all. Reporting "signed out" without asking is both honest and free - it is exactly what the
        // tile needs to offer the sign-in, and the moment that sign-in happens the profile folder
        // exists and the read below starts.
        if (!Directory.Exists(WebViewHost.ResolveUserDataFolder(_webSource.Descriptor.ProfileFolderName)))
        {
            _webSessionSignedIn = false;
            return null;
        }

        // Taken here, before the read, so a sign-in finishing while a read is already running still
        // counts for the next one.
        var signInJustCompleted = Interlocked.Exchange(ref _signInCompleted, 0) == 1;
        if (!signInJustCompleted && _lastWebReadAt is { } lastReadAt && fetchedAt - lastReadAt < WebReadInterval)
            // A window that reset since that read is over, the same as for a session file.
            return _lastWebSnapshot is { } cached ? ProviderSnapshots.ExpirePastWindows(cached, fetchedAt) with { FetchedAt = fetchedAt, HeldOver = true } : null;

        WebUsageResult result;
        try
        {
            result = await _webSource.FetchAsync(_settings, _saveSettings, ct);
        }
        catch (OperationCanceledException)
        {
            if (signInJustCompleted)
                Interlocked.Exchange(ref _signInCompleted, 1);
            // Cancelled mid-read (a refresh cut short, a sign-out, shutdown): the local snapshot this
            // tick already produced still stands, and the next tick may read the web again - the
            // throttle below is deliberately not advanced for a read that never finished.
            return null;
        }

        _lastWebReadAt = fetchedAt;
        if (result.AccountLabel is { } webLabel)
            _lastWebAccountLabel = webLabel;

        _webSessionSignedIn = ProviderSnapshots.SessionSignedIn(result, _webSessionSignedIn);

        _lastWebSnapshot = result.Outcome == WebUsageOutcome.Ok && result.Windows.Count > 0
            ? ProviderSnapshots.FromWeb(AccountKey, result, fetchedAt, result.PlanType)
            : null;

        return _lastWebSnapshot;
    }

    /// <summary>One local read: the snapshot, plus whether it actually came off a rate-limit line in
    /// a session file. False for both honest stand-ins - the empty "no local data" placeholder and
    /// the 0% one - which a second source's real numbers must be able to outrank.</summary>
    private readonly record struct LocalRead(ProviderSnapshot Snapshot, bool FromSessionFile);

    private LocalRead FetchCore(DateTimeOffset fetchedAt, CancellationToken ct)
    {
        var diagnostics = new ProviderDiagnostics().SearchedIn(_sessionsRoot);
        var filesChecked = 0;
        DateTimeOffset? newestFile = null;

        try
        {
            if (!Directory.Exists(_sessionsRoot))
                return new LocalRead(NoLocalData(fetchedAt, diagnostics.DirectoryMissing()), FromSessionFile: false);

            var files = LocalFileScan.NewestFiles(_sessionsRoot, "rollout-*.jsonl", MaxDepth, MaxFilesToCheck);
            ProviderSnapshot? newestSnapshot = null;
            var newestIsRejection = false;

            // The newest file by write time - the same one this scan already found, never a second
            // directory walk - is what "is the agent waiting right now" is asked about, regardless of
            // whether it happens to carry a usable rate-limit line too.
            var isWaitingForUser = false;
            DateTimeOffset? waitingSince = null;
            if (files.Count > 0)
            {
                var (turnKind, turnTimestamp) = SessionTurnReader.ReadNewestCodexTurn(files[0].FullName);
                isWaitingForUser = AttentionDetector.IsWaiting(turnKind, turnTimestamp, fetchedAt, _attentionMaxAge());
                waitingSince = isWaitingForUser ? turnTimestamp : null;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                newestFile ??= new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);

                // Anything this old cannot describe a live window any more, so it is not worth opening.
                if (fetchedAt.UtcDateTime - file.LastWriteTimeUtc > OldestFileToOpen)
                    break;

                filesChecked++;

                // A file's write time says nothing about how recent the rate-limit EVENT inside it
                // is - a session touched a minute ago for an unrelated reason can still carry an
                // older event than one written earlier. So every file worth opening contributes its
                // own newest usable line as a candidate, and the candidate with the newest event
                // timestamp wins across all of them, not just the first file examined.
                try
                {
                    // Set the instant a later line in this file is a genuine error event naming a
                    // rejection (HTTP 429, or a message naming a usage limit) - reading newest-first,
                    // that is any such line seen BEFORE the percentage line itself is reached below.
                    // Codex stops writing fresh percentages once it starts blocking, so the last
                    // written number is otherwise shown as current when the account is really already
                    // maxed out.
                    var sawUsageLimitRejectionSinceNewestPercent = false;

                    var lineCutoff = fetchedAt - OldestFileToOpen;
                    foreach (var line in ReverseLineReader.ReadLinesReversed(file.FullName))
                    {
                        // Newest-first within the file: the first line past the age bound ends it.
                        if (SessionLineAge.IsOlderThan(line, lineCutoff))
                            break;

                        if (IsUsageLimitRejection(line))
                        {
                            sawUsageLimitRejectionSinceNewestPercent = true;
                            continue;
                        }

                        // Cheap prefilter before the real (JSON) parse: almost no line mentions a rate limit.
                        if (!line.Contains("rate_limits", StringComparison.Ordinal))
                            continue;
                        if (!CodexRateLimitParser.TryParse(line, out var limits) || limits is null)
                            continue;
                        if (BuildSnapshot(limits, fetchedAt) is not { } snapshot)
                            continue;

                        if (sawUsageLimitRejectionSinceNewestPercent)
                            snapshot = ReportFiveHourWindowAsFull(snapshot);

                        if (newestSnapshot is null || snapshot.DataTimestamp > newestSnapshot.DataTimestamp)
                        {
                            newestSnapshot = snapshot;
                            newestIsRejection = sawUsageLimitRejectionSinceNewestPercent;
                        }

                        break; // this file's newest usable event is found - move on to the next file
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Deleted or locked between the directory scan and this read - the other files
                    // still count.
                }
            }

            if (newestSnapshot is not null)
            {
                // A rejection is certain, so it is never held back as an implausible jump; it becomes
                // the baseline the next reading is judged against.
                var checkedSnapshot = newestIsRejection
                    ? AcceptWithoutGuard(newestSnapshot)
                    : ApplyPlausibilityGuard(newestSnapshot, fetchedAt);
                var guarded = ProviderSnapshots.ExpirePastWindows(checkedSnapshot, fetchedAt);
                return new LocalRead(guarded with { IsWaitingForUser = isWaitingForUser, WaitingSince = waitingSince }, FromSessionFile: true);
            }

            // The directory exists and every file that was worth opening was readable - the source
            // answered, it simply has nothing to report yet. That reads as 0%, not as the empty
            // placeholder: the placeholder stays reserved for a source that could not be reached at
            // all (the directory-missing case above).
            return new LocalRead(ZeroUsage(fetchedAt, isWaitingForUser, waitingSince), FromSessionFile: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LocalRead(
                NoLocalData(fetchedAt, diagnostics.FilesChecked(filesChecked).NewestFile(newestFile).NothingFound()),
                FromSessionFile: false);
        }
    }

    private ProviderSnapshot ZeroUsage(DateTimeOffset fetchedAt, bool isWaitingForUser = false, DateTimeOffset? waitingSince = null) => new(
        ProviderId: AccountKey,
        Windows:
        [
            new UsageWindow("Window_FiveHour", WindowKind.FiveHour, 0, null, null),
            new UsageWindow("Window_Weekly", WindowKind.Weekly, 0, null, null),
        ],
        PlanType: null,
        SourceKind: SourceKind.LocalFile,
        FetchedAt: fetchedAt,
        DataTimestamp: fetchedAt,
        Status: ProviderStatus.Ok,
        Error: null,
        IsWaitingForUser: isWaitingForUser,
        WaitingSince: waitingSince);

    /// <summary>
    /// Builds one bar per window the line actually carries. Null when the line describes no
    /// window at all, so the caller keeps looking instead of showing an empty tile.
    /// </summary>
    private ProviderSnapshot? BuildSnapshot(CodexRateLimits limits, DateTimeOffset fetchedAt)
    {
        var windows = new List<UsageWindow>(capacity: 2);

        if (limits.PrimaryUsedPercent is { } primaryUsedPercent)
            windows.Add(new UsageWindow(
                "Window_FiveHour", WindowKind.FiveHour, primaryUsedPercent,
                limits.PrimaryResetsAt, limits.PrimaryWindowMinutes,
                // The figure counts the whole rollout; it describes this window only while the rollout
                // was still talking inside it.
                limits.TotalTokens is { } total && limits.Timestamp >= fetchedAt - TimeSpan.FromMinutes(limits.PrimaryWindowMinutes ?? 300)
                    ? new TokenUsage(total)
                    : null));

        if (limits.SecondaryUsedPercent is { } secondaryUsedPercent)
            windows.Add(new UsageWindow(
                "Window_Weekly", WindowKind.Weekly, secondaryUsedPercent,
                limits.SecondaryResetsAt, limits.SecondaryWindowMinutes));

        if (windows.Count == 0)
            return null;

        // A window whose reset already passed is reported as a fresh, empty one by ExpirePastWindows
        // once the snapshot is chosen, so only the age of the data itself decides freshness here.
        var status = ProviderFreshness.IsStale(limits.Timestamp, [], fetchedAt)
            ? ProviderStatus.Stale
            : ProviderStatus.Ok;

        return new ProviderSnapshot(
            ProviderId: AccountKey,
            Windows: windows,
            PlanType: limits.PlanType,
            SourceKind: SourceKind.LocalFile,
            FetchedAt: fetchedAt,
            DataTimestamp: limits.Timestamp,
            Status: status,
            Error: null);
    }

    // The status as an error message words it ("HTTP 429", "status 429", "429 Too Many Requests"); a
    // bare "429" inside an id or a hash does not count.
    [GeneratedRegex(@"\b(HTTP|status)\s*429\b|\b429\s+Too Many\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooManyRequests();

    /// <summary>A genuine rejection, not a coincidental substring: matched only on the structured
    /// shape Codex itself writes for one (<c>type: "event_msg"</c>, <c>payload.type: "error"</c>,
    /// naming an HTTP 429 or a usage limit in its own message text) - never a plain text search across
    /// the whole line, which used to also match a session's own conversation and tool-output content
    /// (an assistant reply quoting this very file, a hash or id that happens to contain "429") and
    /// reported a five-hour window as fully used from nothing but that coincidence. See
    /// <see cref="ApplyPlausibilityGuard"/> for the second, independent guard against exactly that
    /// failure mode.</summary>
    internal static bool IsUsageLimitRejection(string line)
    {
        // Cheap prefilter before the real (JSON) parse: almost no line is an error event at all.
        if (!line.Contains("\"type\":\"error\"", StringComparison.Ordinal))
            return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var outerType) || outerType.GetString() != "event_msg")
                return false;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return false;
            if (!payload.TryGetProperty("type", out var payloadType) || payloadType.GetString() != "error")
                return false;

            var message = payload.TryGetProperty("message", out var messageProperty)
                && messageProperty.ValueKind == JsonValueKind.String
                    ? messageProperty.GetString() ?? ""
                    : "";

            return TooManyRequests().IsMatch(message)
                || message.Contains("usage limit", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Overrides the five-hour window to a full, critical 100 % once a later event in the
    /// same file shows the account was actually rejected - the reset time already on the window
    /// stands, only the percentage was stale. The weekly window is untouched: a five-hour block says
    /// nothing about the weekly figure.</summary>
    private static ProviderSnapshot ReportFiveHourWindowAsFull(ProviderSnapshot snapshot) => snapshot with
    {
        Windows = snapshot.Windows
            .Select(window => window.Kind == WindowKind.FiveHour
                ? new UsageWindow(window.Label, window.Kind, 100, window.ResetsAt, window.WindowMinutes, window.Tokens, window.Allowance)
                : window)
            .ToList(),
    };

    /// <summary>
    /// Guards a local session-file snapshot against a single implausible reading within the same
    /// reset window (<see cref="PlausibilityJumpThreshold"/> points or more) - a second, independent
    /// line of defence alongside the now-structural <see cref="IsUsageLimitRejection"/>: whatever
    /// future shape a false rejection marker takes, a real five-hour percentage does not jump 40+
    /// points and then straight back within a couple of readings. A jump this size is held back -
    /// shown and stored as the previous, still-current value - while the new one is remembered. The
    /// next NEWER line either repeats a similarly large jump (a real change, let through and adopted
    /// as the new baseline) or lands back near the old value (proving the held one was noise); the
    /// same line read again on a later tick confirms nothing. Nothing here ever hides a jump a newer
    /// line confirms.
    /// </summary>
    private ProviderSnapshot ApplyPlausibilityGuard(ProviderSnapshot snapshot, DateTimeOffset fetchedAt)
    {
        List<UsageWindow>? guardedWindows = null;

        for (var i = 0; i < snapshot.Windows.Count; i++)
        {
            var window = snapshot.Windows[i];
            var guardedPercent = GuardLocalReading(window.Kind, window.UsedPercent, window.ResetsAt, snapshot.DataTimestamp, fetchedAt);
            if (guardedPercent == window.UsedPercent)
                continue;

            guardedWindows ??= new List<UsageWindow>(snapshot.Windows);
            guardedWindows[i] = new UsageWindow(
                window.Label, window.Kind, guardedPercent, window.ResetsAt, window.WindowMinutes, window.Tokens, window.Allowance);
        }

        return guardedWindows is null ? snapshot : snapshot with { Windows = guardedWindows };
    }

    /// <summary>Takes every window of a certain reading (a rejection) as the new baseline and drops any
    /// reading held back for it.</summary>
    private ProviderSnapshot AcceptWithoutGuard(ProviderSnapshot snapshot)
    {
        foreach (var window in snapshot.Windows)
        {
            _lastAcceptedLocalReading[window.Kind] = (window.UsedPercent, window.ResetsAt);
            _pendingLocalOutlier.Remove(window.Kind);
        }

        return snapshot;
    }

    private double GuardLocalReading(
        WindowKind kind, double rawUsedPercent, DateTimeOffset? resetsAt, DateTimeOffset? dataTimestamp, DateTimeOffset fetchedAt)
    {
        if (!_lastAcceptedLocalReading.TryGetValue(kind, out var lastAccepted))
        {
            _lastAcceptedLocalReading[kind] = (rawUsedPercent, resetsAt);
            return rawUsedPercent;
        }

        var sameResetWindow = resetsAt == lastAccepted.ResetsAt;
        var jump = Math.Abs(rawUsedPercent - lastAccepted.UsedPercent);

        if (sameResetWindow && jump > PlausibilityJumpThreshold)
        {
            if (_pendingLocalOutlier.TryGetValue(kind, out var pending) && pending.ResetsAt == resetsAt
                && Math.Abs(rawUsedPercent - pending.UsedPercent) <= PlausibilityJumpThreshold)
            {
                // The same line read again on the next tick proves nothing on its own: only a newer
                // line that repeats a similarly large jump is a real change, not noise - or, with no
                // newer line at all, the passing of the hold time-out.
                if (dataTimestamp == pending.DataTimestamp && fetchedAt - pending.FirstSeen < HeldJumpTimeout)
                    return lastAccepted.UsedPercent;

                _lastAcceptedLocalReading[kind] = (rawUsedPercent, resetsAt);
                _pendingLocalOutlier.Remove(kind);
                return rawUsedPercent;
            }

            _pendingLocalOutlier[kind] = (rawUsedPercent, resetsAt, dataTimestamp, fetchedAt);
            _log.LogInfo($"Ignored an implausible jump in the {kind} window from a local session file, kept the previous reading.");
            return lastAccepted.UsedPercent;
        }

        _lastAcceptedLocalReading[kind] = (rawUsedPercent, resetsAt);
        _pendingLocalOutlier.Remove(kind);
        return rawUsedPercent;
    }

    private ProviderSnapshot NoLocalData(DateTimeOffset fetchedAt, ProviderDiagnostics diagnostics) => new(
        ProviderId: AccountKey,
        Windows: Array.Empty<UsageWindow>(),
        PlanType: null,
        SourceKind: SourceKind.None,
        FetchedAt: fetchedAt,
        DataTimestamp: null,
        Status: ProviderStatus.NoLocalData,
        Error: null,
        Diagnostics: diagnostics.Lines);
}
