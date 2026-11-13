using System.Text.Json;
using System.Text.RegularExpressions;
using AiUsage.Models;
using AiUsage.Providers.Parsing;
using AiUsage.Services;
using AiUsage.Web;

namespace AiUsage.Providers;

/// <summary>
/// Reads and remembers the API credit balance of the Claude Console behind one Claude account, apart
/// from the usage read: at most every <see cref="ReadInterval"/>, never awaited by the usage fetch,
/// and a failure never reaches the tile. The account's own claude.ai session is asked first (the same
/// login may already be a console login); when that does not deliver, a second hidden session on the
/// console's own site answers, created only then. A console that says "not signed in" is left alone
/// for <see cref="ConsoleBackoff"/> unless <see cref="ReadNowAsync"/> says the user just signed in there.
/// </summary>
internal sealed class ClaudeConsoleCreditSource : IShutdownDisposable
{
    internal static readonly TimeSpan ReadInterval = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan KeepFor = TimeSpan.FromHours(2);
    internal static readonly TimeSpan ConsoleBackoff = TimeSpan.FromHours(6);

    /// <summary>A script runner on the console's own site and the thing to dispose with it.</summary>
    internal sealed record ConsoleRunner(Func<string, CancellationToken, Task<string>> Run, IAsyncDisposable Owner);

    private enum Step
    {
        Ok,
        NotSignedIn,
        NoApiOrganization,
        Blocked,
        Failed,
    }

    private readonly Func<string, CancellationToken, Task<string>>? _siteRun;
    private readonly Func<ConsoleRunner>? _createConsoleRunner;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private ClaudeApiCredit? _credit;
    private ClaudeCreditRoute _creditRoute;
    // The route that delivered last. Unlike _creditRoute it outlives the balance's age, so the next
    // read still knows where to ask first.
    private ClaudeCreditRoute _preferredRoute;
    private bool _consoleOff;
    private DateTimeOffset _creditReadAt;
    // The last good balance with its read time and route, kept beyond KeepFor for the status file.
    private (ClaudeApiCredit Credit, DateTimeOffset ReadAt, ClaudeCreditRoute Route)? _lastKnown;
    private bool _consoleSignInExpired;
    private DateTimeOffset? _lastAttemptAt;
    private DateTimeOffset? _consoleBlockedUntil;
    private ConsoleRunner? _console;
    private Task _running = Task.CompletedTask;
    private bool _disposed;
    private string? _lastOutcome;

    /// <param name="siteRun">Runs a script in the account's claude.ai session; null when the account
    /// has none in use.</param>
    /// <param name="createConsoleRunner">Builds the runner on the console's site, called at most once and
    /// only when the first route did not deliver; null when no console route exists.</param>
    /// <param name="hasProfile">False while the account has no browser profile at all: no sign-in exists on
    /// either site, so no hidden browser is started just to learn that.</param>
    internal ClaudeConsoleCreditSource(
        Func<string, CancellationToken, Task<string>>? siteRun, Func<ConsoleRunner>? createConsoleRunner, TimeProvider? time = null,
        Func<bool>? hasProfile = null, Func<StoredApiCredit?>? loadStored = null, Action<StoredApiCredit?>? saveStored = null)
    {
        _siteRun = siteRun;
        _createConsoleRunner = createConsoleRunner;
        _time = time ?? TimeProvider.System;
        _hasProfile = hasProfile ?? (() => true);
        _saveStored = saveStored;
        TakeStored(loadStored);
    }

    private readonly Action<StoredApiCredit?>? _saveStored;

    /// <summary>Takes over the balance of the last run: shown while it is younger than <see cref="KeepFor"/>,
    /// kept as <see cref="LastKnown"/> either way, and its route tells where to ask first. Anything unreadable is ignored.</summary>
    private void TakeStored(Func<StoredApiCredit?>? load)
    {
        StoredApiCredit? stored;
        try
        {
            stored = load?.Invoke();
        }
        catch (Exception ex)
        {
            LogService.Shared.LogError($"Reading the stored API credit failed ({ex.GetType().Name}).");
            return;
        }

        if (stored is null || stored.Route is not (ClaudeCreditRoute.ClaudeSignIn or ClaudeCreditRoute.Console)
            || !StoredCurrency.IsMatch(stored.Currency ?? "") || Math.Abs(stored.Amount) > MaxStoredAmount)
            return;

        var now = _time.GetUtcNow();
        if (stored.ReadAt.ToUniversalTime() > now + TimeSpan.FromMinutes(5))
            return;

        _preferredRoute = stored.Route;
        ClaudeCreditGrant? grant = stored.Granted is { } granted && stored.Remaining is { } remaining
            ? new ClaudeCreditGrant(granted, remaining, stored.NextExpiry)
            : null;
        var credit = new ClaudeApiCredit(stored.Amount, stored.Currency!.ToUpperInvariant(), grant);
        _lastKnown = (credit, stored.ReadAt, stored.Route);
        if (now - stored.ReadAt >= KeepFor)
            return;

        _credit = credit;
        _creditRoute = stored.Route;
        _creditReadAt = stored.ReadAt;
    }

    private static readonly Regex StoredCurrency = new(@"\A[A-Za-z]{3}\z", RegexOptions.Compiled);
    private const decimal MaxStoredAmount = 1_000_000_000m;

    private static StoredApiCredit ToStored(ClaudeApiCredit credit, ClaudeCreditRoute route, DateTimeOffset readAt) => new()
    {
        Amount = credit.Amount,
        Currency = credit.Currency,
        Granted = credit.Grant?.Granted,
        Remaining = credit.Grant?.Remaining,
        NextExpiry = credit.Grant?.NextExpiry,
        ReadAt = readAt.ToUniversalTime(),
        Route = route,
    };

    /// <summary>Hands the new state to the settings; a failing write never reaches the read.</summary>
    private void Persist(StoredApiCredit? stored)
    {
        try
        {
            _saveStored?.Invoke(stored);
        }
        catch (Exception ex)
        {
            LogService.Shared.LogError($"Storing the API credit failed ({ex.GetType().Name}).");
        }
    }

    private readonly Func<bool> _hasProfile;

    /// <summary>Raised on the thread that finished a read, whenever the balance shown changed.</summary>
    internal event Action? Changed;

    /// <summary>The last good balance, kept while it is younger than <see cref="KeepFor"/>.</summary>
    internal ClaudeApiCredit? Current
    {
        get
        {
            lock (_gate)
                return _credit is not null && _time.GetUtcNow() - _creditReadAt < KeepFor ? _credit : null;
        }
    }

    /// <summary>True while the Console sign-in answered "not signed in" and no Console read has delivered since:
    /// the app still holds the profile, but the session behind it ended and only a new sign-in brings it back.</summary>
    internal bool ConsoleSignInExpired
    {
        get
        {
            lock (_gate)
                return _consoleSignInExpired;
        }
    }

    /// <summary>The last good balance with its read time, however old; null until one was read (or after the
    /// Console sign-out that delivered it). <see cref="Current"/> drops it after <see cref="KeepFor"/>, this stays.</summary>
    internal (ClaudeApiCredit Credit, DateTimeOffset ReadAt)? LastKnown
    {
        get
        {
            lock (_gate)
                return _lastKnown is { } known ? (known.Credit, known.ReadAt) : null;
        }
    }

    /// <summary>What the status file says about the balance: <see cref="LastKnown"/> with its read time,
    /// stale once older than <see cref="KeepFor"/> or while the Console sign-in is expired. Null without a reading.</summary>
    internal ApiCreditReading? StatusReading()
    {
        lock (_gate)
        {
            if (_lastKnown is not { } known)
                return null;

            return new ApiCreditReading(
                known.Credit, known.ReadAt, _consoleSignInExpired || _time.GetUtcNow() - known.ReadAt >= KeepFor, _consoleSignInExpired);
        }
    }

    /// <summary>Which sign-in delivered <see cref="Current"/>; <see cref="ClaudeCreditRoute.None"/> without one.</summary>
    internal ClaudeCreditRoute CurrentRoute
    {
        get
        {
            lock (_gate)
                return _credit is not null && _time.GetUtcNow() - _creditReadAt < KeepFor ? _creditRoute : ClaudeCreditRoute.None;
        }
    }

    /// <summary>Starts a read when one is due and none runs; returns at once with the running read's
    /// task (completed when nothing was started). Never throws.</summary>
    internal Task RefreshIfDueAsync()
    {
        lock (_gate)
        {
            if (_disposed || !_running.IsCompleted)
                return _running;
            if (_lastAttemptAt is { } last && _time.GetUtcNow() - last < ReadInterval)
                return Task.CompletedTask;
            if (!_hasProfile())
                return Task.CompletedTask;

            return _running = ReadAsync();
        }
    }

    /// <summary>The plan changed, and with it the free credit: reads as soon as no read runs, without
    /// lifting a back-off or the pause after a Console sign-out.</summary>
    internal async Task RefreshAfterPlanChangeAsync()
    {
        Task pending;
        lock (_gate)
        {
            _lastAttemptAt = null;
            pending = _running;
        }

        await pending;
        await RefreshIfDueAsync();
    }

    /// <summary>The user just signed in on the console: forgets the back-off and the read pause and reads now.</summary>
    internal async Task ReadNowAsync()
    {
        Task pending;
        bool expiredCleared;
        lock (_gate)
        {
            _consoleBlockedUntil = null;
            _consoleOff = false;
            _lastAttemptAt = null;
            pending = _running;
            expiredCleared = _consoleSignInExpired;
            _consoleSignInExpired = false;
        }

        if (expiredCleared)
            Changed?.Invoke();
        await pending;
        await RefreshIfDueAsync();
    }

    private async Task ReadAsync()
    {
        // A note about the console's answer is no reason for the usage tile to say its read changed.
        using var scope = ReadChangeLog.BeginReadScope();
        var outcome = new List<string>();
        try
        {
            lock (_gate)
                _lastAttemptAt = _time.GetUtcNow();

            var ct = _lifetime.Token;
            foreach (var route in RouteOrder())
            {
                var step = route == ClaudeCreditRoute.Console
                    ? await ReadViaConsoleAsync(outcome, ct)
                    : await ReadViaSiteAsync(outcome, ct);
                if (step is Step.Ok or Step.Blocked)
                    return;
            }
        }
        catch (Exception ex)
        {
            // A balance that cannot be read is simply not shown; the cache ages out by itself.
            outcome.Add("failed (" + ex.GetType().Name + ")");
        }
        finally
        {
            LogOutcome(outcome);
        }
    }

    /// <summary>The route that delivered last is asked first, the other one only when it did not
    /// deliver: an account whose claude.ai login is no console login would otherwise pay for a refused
    /// read on every round.</summary>
    private ClaudeCreditRoute[] RouteOrder()
    {
        lock (_gate)
        {
            return _preferredRoute == ClaudeCreditRoute.Console
                ? [ClaudeCreditRoute.Console, ClaudeCreditRoute.ClaudeSignIn]
                : [ClaudeCreditRoute.ClaudeSignIn, ClaudeCreditRoute.Console];
        }
    }

    private async Task<Step> ReadViaSiteAsync(List<string> outcome, CancellationToken ct)
    {
        if (_siteRun is null)
            return Step.NotSignedIn;

        var (step, stage) = await TryReadAsync(_siteRun, ClaudeCreditRoute.ClaudeSignIn, ct);
        outcome.Add("claude.ai " + Word(step) + stage);
        return step;
    }

    private async Task<Step> ReadViaConsoleAsync(List<string> outcome, CancellationToken ct)
    {
        if (_createConsoleRunner is null)
            return Step.NotSignedIn;
        if (ConsoleIsOff())
        {
            outcome.Add("console signed out");
            return Step.NotSignedIn;
        }
        if (ConsoleIsPaused())
        {
            outcome.Add("console paused after not_signed_in");
            return Step.NotSignedIn;
        }

        ConsoleRunner console;
        lock (_gate)
        {
            if (_disposed || _consoleOff)
                return Step.NotSignedIn;
            console = _console ??= _createConsoleRunner();
        }

        var (step, stage) = await TryReadAsync(console.Run, ClaudeCreditRoute.Console, ct);
        outcome.Add("console " + Word(step) + stage);
        if (step == Step.NotSignedIn)
        {
            var expiredNow = false;
            lock (_gate)
            {
                _consoleBlockedUntil = _time.GetUtcNow() + ConsoleBackoff;
                // A profile exists, so a sign-in did: its session ended. Not after a sign-out or a shutdown.
                if (!_disposed && !_consoleOff && _hasProfile() && !_consoleSignInExpired)
                    expiredNow = _consoleSignInExpired = true;
            }

            if (expiredNow)
                Changed?.Invoke();
        }

        return step;
    }

    private static string Word(Step step) => step switch
    {
        Step.Ok => "ok",
        Step.NotSignedIn => "not_signed_in",
        Step.NoApiOrganization => "no_api_organization",
        Step.Blocked => "blocked",
        _ => "failed",
    };

    /// <summary>One line per change of what each route answered, so a balance that never shows can be
    /// traced to its route; status words only, never a value or an id.</summary>
    private void LogOutcome(List<string> outcome)
    {
        if (outcome.Count == 0)
            return;
        var line = string.Join(", ", outcome);
        lock (_gate)
        {
            if (line == _lastOutcome)
                return;
            _lastOutcome = line;
        }
        LogService.Shared.LogInfo("Claude API credit read: " + line + ".");
    }

    private bool ConsoleIsOff()
    {
        lock (_gate)
            return _consoleOff;
    }

    private bool ConsoleIsPaused()
    {
        lock (_gate)
            return _consoleBlockedUntil is { } until && _time.GetUtcNow() < until;
    }

    /// <summary>One route's answer, and for a failure which of its two requests it came from (" at
    /// organizations" or " at credits"), for the log line.</summary>
    private async Task<(Step Step, string Stage)> TryReadAsync(
        Func<string, CancellationToken, Task<string>> run, ClaudeCreditRoute route, CancellationToken ct)
    {
        const string AtOrganizations = " at organizations";
        const string AtCredits = " at credits";
        const int MaxOrganizations = 5;
        var organizations = ReadEnvelope(await run(ClaudeConsoleCreditScript.ListOrganizations(), ct));
        if (organizations.Step != Step.Ok)
            return (organizations.Step, AtOrganizations + CodeSuffix(organizations.Code));

        var pick = ClaudeConsoleCreditParser.PickOrganization(organizations.Body, organizations.LastActiveOrg);
        if (pick.Outcome == ClaudeConsoleOrgOutcome.NoApiOrganization)
            return (Step.NoApiOrganization, "");
        if (pick.Outcome != ClaudeConsoleOrgOutcome.Found || pick.OrganizationId is not { } organizationId)
        {
            Report(ClaudeConsoleCreditParser.ChangeNotes(organizations.Body, null));
            return (Step.Failed, AtOrganizations);
        }

        // Every API organisation in turn, the one the console has open first: a person can belong to
        // several, and one without prepaid billing refuses the credit address.
        var ids = pick.OrganizationIds is { Count: > 0 } list ? list : [organizationId];
        (Step Step, string Stage) last = (Step.Failed, AtCredits);
        foreach (var id in ids.Take(MaxOrganizations))
        {
            var credits = ReadEnvelope(await run(ClaudeConsoleCreditScript.FetchCredits(ClaudeConsoleCreditScript.CreditPath(id)), ct));
            if (credits.Step != Step.Ok)
            {
                last = (credits.Step, AtCredits + CodeSuffix(credits.Code));
                if (credits.Step is Step.Blocked or Step.NotSignedIn)
                    return last;
                continue;
            }

            var reading = ClaudeConsoleCreditParser.Parse(credits.Body, _time.GetUtcNow());
            // Also on success: the balance can read fine while the free credit grants changed shape.
            Report(ClaudeConsoleCreditParser.ChangeNotes(null, credits.Body));
            if (reading.Outcome != ClaudeConsoleCreditOutcome.Ok || reading.Credit is not { } credit)
            {
                last = (Step.Failed, AtCredits);
                continue;
            }

            Store(credit, route);
            return (Step.Ok, "");
        }

        return last;
    }

    /// <summary>" (404)" for a failure that names its HTTP code or reason, else nothing.</summary>
    internal static string CodeSuffix(string? code) => string.IsNullOrEmpty(code) ? "" : $" ({code})";

    private void Store(ClaudeApiCredit credit, ClaudeCreditRoute route)
    {
        bool changed;
        StoredApiCredit stored;
        lock (_gate)
        {
            // A read that was already under way when the console was signed out must not bring its balance back.
            // Nor one that ends after the source was closed (the account signed out meanwhile).
            if (_disposed || (route == ClaudeCreditRoute.Console && _consoleOff))
                return;

            // Also when the balance was unchanged but had aged out of Current: it is shown again, and the
            // status file's read time moves on.
            changed = _credit != credit || _creditRoute != route || _time.GetUtcNow() - _creditReadAt >= KeepFor;
            if (route == ClaudeCreditRoute.Console && _consoleSignInExpired)
            {
                _consoleSignInExpired = false;
                changed = true;
            }

            _credit = credit;
            _creditRoute = route;
            _preferredRoute = route;
            _creditReadAt = _time.GetUtcNow();
            _lastKnown = (credit, _creditReadAt, route);
            stored = ToStored(credit, route, _creditReadAt);
        }

        // Once per good read: the read time is what keeps the stored balance honest after a restart.
        Persist(stored);
        if (changed)
            Changed?.Invoke();
    }

    private static void Report(IReadOnlyList<string> notes)
    {
        foreach (var note in notes)
            ReadChangeLog.Report("Claude", note);
    }

    private static readonly Regex EnvelopeCodePattern = new(@"\A[a-z0-9_]{1,16}\z", RegexOptions.Compiled);

    /// <summary>The script's answer: its step, the body for "ok", the console's open organisation, and
    /// for a failure the HTTP code or reason word the script names (only short plain words pass).</summary>
    private static (Step Step, string? Body, string? LastActiveOrg, string? Code) ReadEnvelope(string? envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson))
            return (Step.Failed, null, null, null);

        try
        {
            using var document = JsonDocument.Parse(envelopeJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
                return (Step.Failed, null, null, null);

            var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String
                && EnvelopeCodePattern.IsMatch(codeEl.GetString()!) ? codeEl.GetString() : null;
            switch (status.GetString())
            {
                case "not_signed_in":
                    return (Step.NotSignedIn, null, null, code);
                case "blocked":
                    return (Step.Blocked, null, null, code);
                case "ok":
                    var body = root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String ? bodyEl.GetString() : null;
                    var active = root.TryGetProperty("lastActiveOrg", out var activeEl) && activeEl.ValueKind == JsonValueKind.String
                        && ClaudeConsoleCreditParser.IsOrganizationId(activeEl.GetString()) ? activeEl.GetString() : null;
                    return body is null ? (Step.Failed, null, null, null) : (Step.Ok, body, active, null);
                default:
                    return (Step.Failed, null, null, code);
            }
        }
        catch (JsonException)
        {
            return (Step.Failed, null, null, null);
        }
    }

    /// <summary>The user signed out of the console: closes its session, forgets the balance and reads nothing
    /// through the console until <see cref="ReadNowAsync"/> says a new sign-in happened. The claude.ai route
    /// is left alone. A read under way ends first, so its answer cannot bring the balance back.</summary>
    internal async Task DisconnectConsoleAsync()
    {
        ConsoleRunner? console;
        Task pending;
        lock (_gate)
        {
            if (_disposed)
                return;
            _consoleOff = true;
            console = _console;
            _console = null;
            pending = _running;
        }

        if (console is not null)
        {
            try
            {
                await console.Owner.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogService.Shared.LogError($"Disposing the console hidden browser failed ({ex.GetType().Name}).");
            }
        }

        await pending;
        bool changed;
        bool storedGoes;
        lock (_gate)
        {
            var creditGoes = _credit is not null && _creditRoute == ClaudeCreditRoute.Console;
            changed = creditGoes || _consoleSignInExpired;
            storedGoes = creditGoes || _preferredRoute == ClaudeCreditRoute.Console;
            _consoleSignInExpired = false;
            if (_lastKnown is { Route: ClaudeCreditRoute.Console })
                _lastKnown = null;
            if (creditGoes)
            {
                _credit = null;
                _creditRoute = ClaudeCreditRoute.None;
            }

            if (_preferredRoute == ClaudeCreditRoute.Console)
                _preferredRoute = ClaudeCreditRoute.None;
        }

        if (storedGoes)
            Persist(null);
        if (changed)
            Changed?.Invoke();
    }

    public ValueTask DisposeAsync() => DisposeCoreAsync(forShutdown: false);

    /// <summary>App shutdown (UI thread blocked): the console's session is closed without waiting for a
    /// browser start that is still in flight, which could only finish on that thread.</summary>
    public ValueTask DisposeForShutdownAsync() => DisposeCoreAsync(forShutdown: true);

    private async ValueTask DisposeCoreAsync(bool forShutdown)
    {
        ConsoleRunner? console;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            console = _console;
            _console = null;
        }

        _lifetime.Cancel();
        if (console is null)
            return;

        try
        {
            if (forShutdown && console.Owner is IShutdownDisposable shutdownAware)
                await shutdownAware.DisposeForShutdownAsync();
            else
                await console.Owner.DisposeAsync();
        }
        catch (Exception ex)
        {
            LogService.Shared.LogError($"Disposing the console hidden browser failed ({ex.GetType().Name}).");
        }
    }
}
