using System.Text.Json;
using AiUsage.Models;
using AiUsage.Providers.Parsing;
using AiUsage.Services;

namespace AiUsage.Providers;

/// <summary>
/// Reads and remembers the API credit balance of the Claude Console behind one Claude account, apart
/// from the usage read: at most every <see cref="ReadInterval"/>, never awaited by the usage fetch,
/// and a failure never reaches the tile. The account's own claude.ai session is asked first (the same
/// login may already be a console login); when that does not deliver, a second hidden session on the
/// console's own site answers, created only then. A console that says "not signed in" is left alone
/// for <see cref="ConsoleBackoff"/> unless <see cref="ReadNowAsync"/> says the user just signed in there.
/// </summary>
internal sealed class ClaudeConsoleCreditSource : IAsyncDisposable
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
    private DateTimeOffset _creditReadAt;
    private DateTimeOffset? _lastAttemptAt;
    private DateTimeOffset? _consoleBlockedUntil;
    private ConsoleRunner? _console;
    private Task _running = Task.CompletedTask;
    private bool _disposed;

    /// <param name="siteRun">Runs a script in the account's claude.ai session; null when the account
    /// has none in use.</param>
    /// <param name="createConsoleRunner">Builds the runner on the console's site, called at most once and
    /// only when the first route did not deliver; null when no console route exists.</param>
    /// <param name="hasProfile">False while the account has no browser profile at all: no sign-in exists on
    /// either site, so no hidden browser is started just to learn that.</param>
    internal ClaudeConsoleCreditSource(
        Func<string, CancellationToken, Task<string>>? siteRun, Func<ConsoleRunner>? createConsoleRunner, TimeProvider? time = null,
        Func<bool>? hasProfile = null)
    {
        _siteRun = siteRun;
        _createConsoleRunner = createConsoleRunner;
        _time = time ?? TimeProvider.System;
        _hasProfile = hasProfile ?? (() => true);
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

    /// <summary>The user just signed in on the console: forgets the back-off and the read pause and reads now.</summary>
    internal async Task ReadNowAsync()
    {
        Task pending;
        lock (_gate)
        {
            _consoleBlockedUntil = null;
            _lastAttemptAt = null;
            pending = _running;
        }

        await pending;
        await RefreshIfDueAsync();
    }

    private async Task ReadAsync()
    {
        // A note about the console's answer is no reason for the usage tile to say its read changed.
        using var scope = ReadChangeLog.BeginReadScope();
        try
        {
            lock (_gate)
                _lastAttemptAt = _time.GetUtcNow();

            var ct = _lifetime.Token;
            var step = Step.NotSignedIn;
            if (_siteRun is not null)
            {
                step = await TryReadAsync(_siteRun, ct);
                if (step is Step.Ok or Step.Blocked)
                    return;
            }

            if (_createConsoleRunner is null || ConsoleIsPaused())
                return;

            ConsoleRunner console;
            lock (_gate)
            {
                if (_disposed)
                    return;
                console = _console ??= _createConsoleRunner();
            }

            step = await TryReadAsync(console.Run, ct);
            if (step == Step.NotSignedIn)
            {
                lock (_gate)
                    _consoleBlockedUntil = _time.GetUtcNow() + ConsoleBackoff;
            }
        }
        catch (Exception)
        {
            // A balance that cannot be read is simply not shown; the cache ages out by itself.
        }
    }

    private bool ConsoleIsPaused()
    {
        lock (_gate)
            return _consoleBlockedUntil is { } until && _time.GetUtcNow() < until;
    }

    private async Task<Step> TryReadAsync(Func<string, CancellationToken, Task<string>> run, CancellationToken ct)
    {
        var organizations = ReadEnvelope(await run(ClaudeConsoleCreditScript.ListOrganizations(), ct));
        if (organizations.Step != Step.Ok)
            return organizations.Step;

        var pick = ClaudeConsoleCreditParser.PickOrganization(organizations.Body, organizations.LastActiveOrg);
        if (pick.Outcome == ClaudeConsoleOrgOutcome.NoApiOrganization)
            return Step.NoApiOrganization;
        if (pick.Outcome != ClaudeConsoleOrgOutcome.Found || pick.OrganizationId is not { } organizationId)
        {
            Report(ClaudeConsoleCreditParser.ChangeNotes(organizations.Body, null));
            return Step.Failed;
        }

        var credits = ReadEnvelope(await run(ClaudeConsoleCreditScript.FetchCredits(ClaudeConsoleCreditScript.CreditPath(organizationId)), ct));
        if (credits.Step != Step.Ok)
            return credits.Step;

        var reading = ClaudeConsoleCreditParser.Parse(credits.Body);
        if (reading.Outcome != ClaudeConsoleCreditOutcome.Ok || reading.Credit is not { } credit)
        {
            Report(ClaudeConsoleCreditParser.ChangeNotes(null, credits.Body));
            return Step.Failed;
        }

        Store(credit);
        return Step.Ok;
    }

    private void Store(ClaudeApiCredit credit)
    {
        bool changed;
        lock (_gate)
        {
            changed = _credit != credit;
            _credit = credit;
            _creditReadAt = _time.GetUtcNow();
        }

        if (changed)
            Changed?.Invoke();
    }

    private static void Report(IReadOnlyList<string> notes)
    {
        foreach (var note in notes)
            ReadChangeLog.Report("Claude", note);
    }

    private static (Step Step, string? Body, string? LastActiveOrg) ReadEnvelope(string? envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson))
            return (Step.Failed, null, null);

        try
        {
            using var document = JsonDocument.Parse(envelopeJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
                return (Step.Failed, null, null);

            switch (status.GetString())
            {
                case "not_signed_in":
                    return (Step.NotSignedIn, null, null);
                case "blocked":
                    return (Step.Blocked, null, null);
                case "ok":
                    var body = root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String ? bodyEl.GetString() : null;
                    var active = root.TryGetProperty("lastActiveOrg", out var activeEl) && activeEl.ValueKind == JsonValueKind.String
                        && ClaudeConsoleCreditParser.IsOrganizationId(activeEl.GetString()) ? activeEl.GetString() : null;
                    return body is null ? (Step.Failed, null, null) : (Step.Ok, body, active);
                default:
                    return (Step.Failed, null, null);
            }
        }
        catch (JsonException)
        {
            return (Step.Failed, null, null);
        }
    }

    public async ValueTask DisposeAsync()
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
            await console.Owner.DisposeAsync();
        }
        catch (Exception ex)
        {
            LogService.Shared.LogError($"Disposing the console hidden browser failed ({ex.GetType().Name}).");
        }
    }
}
