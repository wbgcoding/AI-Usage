using System.Globalization;
using System.Text.Json;
using AiUsage.Models;
using AiUsage.Web;

namespace AiUsage.Providers;

public enum WebUsageOutcome { Ok, NotSignedIn, Blocked, Failed }

/// <param name="SessionSignedIn">True whenever the site's own session route handed out a key and
/// the usage address answered with JSON - the session is signed in even when that answer's shape is
/// one the parser does not know yet, so the sign-out button must not vanish with the numbers.</param>
/// <param name="PlanType">The subscription tier as the provider's own response words it - read from a
/// field of an answer the read already fetched, never from a request of its own; null when the
/// answer carries none. <see cref="Services.PlanTier"/> turns it into the name the tile shows.</param>
public sealed record WebUsageResult(
    WebUsageOutcome Outcome, IReadOnlyList<UsageWindow> Windows, string? AccountLabel = null, bool SessionSignedIn = false,
    string? PlanType = null)
{
    public static readonly WebUsageResult NotSignedIn = new(WebUsageOutcome.NotSignedIn, []);
    public static readonly WebUsageResult Blocked = new(WebUsageOutcome.Blocked, []);
    public static readonly WebUsageResult Failed = new(WebUsageOutcome.Failed, []);
}

/// <summary>
/// The provider-neutral half of a web-backed provider's read path: discovers the real usage
/// endpoint once, caches it in settings, and re-fetches it directly on every later call. Never
/// reads, stores or logs the session cookie itself - only the JSON the endpoint itself returns.
/// The other half - what to fetch and how to parse the response - arrives as an
/// <see cref="IWebUsageEndpoint"/>, so this class names no provider at all.
/// </summary>
public sealed class WebUsageSource
{
    // A discovery answer names at most the provider's own candidate paths; a page that returns more
    // than this is not describing a walk the app made, so the rest never reaches the log.
    private const int MaxLoggedAttempts = 10;

    private readonly Func<string, CancellationToken, Task<string>> _executeScript;
    private readonly IWebUsageEndpoint _endpoint;
    private readonly Action<string>? _log;

    /// <summary>Which provider this instance reads for - carried mainly so callers and future
    /// providers have one place to confirm which session a given instance belongs to.</summary>
    public WebSessionDescriptor Descriptor { get; }

    /// <param name="descriptor">Identifies which provider's web session this instance reads.</param>
    /// <param name="endpoint">This provider's own scripts, path allow-list and response shape.</param>
    /// <param name="executeScript">Runs a script in the hidden, signed-in WebView2 session (see
    /// Web/WebViewHost.cs) and returns its result's JSON text - production wraps
    /// CoreWebView2.ExecuteScriptAsync; a test injects a canned envelope instead, so everything
    /// below this line is testable without a live browser.</param>
    /// <param name="log">Optional: receives one line per discovery run naming each candidate path
    /// and the status code it answered with. Never a response body, a cookie or a token - just
    /// enough for the app's own log to say which address exists today.</param>
    public WebUsageSource(
        WebSessionDescriptor descriptor,
        IWebUsageEndpoint endpoint,
        Func<string, CancellationToken, Task<string>> executeScript,
        Action<string>? log = null)
    {
        Descriptor = descriptor;
        _executeScript = executeScript;
        _endpoint = endpoint;
        _log = log;
    }

    public async Task<WebUsageResult> FetchAsync(AppSettings settings, Action<AppSettings> save, CancellationToken ct)
    {
        var accountKey = Descriptor.ProviderId;
        if (settings.WebUsagePaths.TryGetValue(accountKey, out var cachedPath) && cachedPath is { Length: > 0 })
        {
            if (!_endpoint.IsAllowedUsagePath(cachedPath))
            {
                // A stored path outside the allow-list is treated as poisoned, never handed to
                // Fetch: drop it and fall through to a fresh discovery.
                settings.WebUsagePaths.Remove(accountKey);
                save(settings);
            }
            else
            {
                var cached = await RunAsync(_endpoint.Fetch(cachedPath), ct);
                if (cached.Outcome != WebUsageOutcome.Failed)
                    return cached;
            }
            // A cached path that stopped answering, or one that failed the allow-list, is
            // re-discovered below rather than trusted forever - the API can change shape without
            // this app ever being told.
        }

        var (result, discoveredPath) = await RunDiscoveryAsync(ct);
        if (result.Outcome == WebUsageOutcome.Ok && discoveredPath is not null)
        {
            settings.WebUsagePaths[accountKey] = discoveredPath;
            save(settings);
        }
        return result;
    }

    private async Task<WebUsageResult> RunAsync(string script, CancellationToken ct)
    {
        var (result, _) = await RunWithPathAsync(script, ct);
        return result;
    }

    private async Task<(WebUsageResult Result, string? Path)> RunDiscoveryAsync(CancellationToken ct)
    {
        var (envelopeJson, result) = await RunEnvelopeAsync(_endpoint.Discover(), ct);
        if (_log is not null && envelopeJson is not null)
        {
            var attempts = ReadAttempts(envelopeJson);
            if (attempts.Length > 0)
                _log($"{Descriptor.ProviderId}: usage endpoint discovery tried {attempts}");

            // Cursor's answers differ by plan - one shape line per collected candidate shows which
            // fields a live run actually got, without ever writing a value out of the body itself.
            foreach (var (path, body) in ReadResultEntries(envelopeJson))
                _log($"{Descriptor.ProviderId}: usage response shape {MaskIds(path)} {DescribeShape(body)}");

            // A signed-in answer the parser could not read: its key layout (never a value) is what
            // fixing the parser needs, and this is the only place that ever sees it.
            if (result.Result.Outcome == WebUsageOutcome.Failed && result.Result.SessionSignedIn
                && ReadSingleBody(envelopeJson) is { } single)
                _log($"{Descriptor.ProviderId}: unread usage response shape {MaskIds(single.Path)} {DescribeShape(single.Body)}");
        }

        return result;
    }

    /// <summary>A usage path with every id-like part replaced by <c>{id}</c>: a GUID, or any path
    /// segment or query value of 16 or more letters, digits, underscores and hyphens. The path of the
    /// Claude usage read carries the organisation id and Cursor's carries the account id; neither may
    /// reach the log.</summary>
    internal static string MaskIds(string path)
    {
        var queryStart = path.IndexOf('?');
        var pathPart = queryStart < 0 ? path : path[..queryStart];
        var masked = string.Join('/', pathPart.Split('/').Select(segment => IsIdLike(segment) ? "{id}" : segment));
        if (queryStart < 0)
            return masked;

        var query = string.Join('&', path[(queryStart + 1)..].Split('&').Select(pair =>
        {
            var equals = pair.IndexOf('=');
            return equals < 0 ? pair : pair[..(equals + 1)] + "{id}";
        }));
        return masked + "?" + query;
    }

    private static bool IsIdLike(string segment) =>
        Guid.TryParse(segment, out _)
        || (segment.Length >= 16 && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));

    private const int MaxLoggedNameLength = 40;

    private static bool IsPrintable(char c) => char.GetUnicodeCategory(c) is not (
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
        or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);

    /// <summary>A property name from an external response, safe for the log: <c>{id}</c> for a GUID,
    /// an email address or a long unbroken token with digits (an account or organisation id keying
    /// the body), otherwise cut to a short length with every non-printable character replaced. A long
    /// hyphenated name such as a model name stays readable.</summary>
    private static string CleanName(string name)
    {
        if (name.Contains('@') || Guid.TryParse(name, out _)
            || (name.Length >= 16 && !name.Contains('-') && name.Any(char.IsAsciiDigit) && IsIdLike(name)))
            return "{id}";

        var shortened = name.Length > MaxLoggedNameLength ? name[..MaxLoggedNameLength] : name;
        return string.Create(shortened.Length, shortened, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = IsPrintable(source[i]) ? source[i] : '?';
        });
    }

    /// <summary>The one path/body pair a first-answer-wins discovery returns, only when the path is
    /// one of this provider's own allowed usage paths.</summary>
    private (string Path, string Body)? ReadSingleBody(string envelopeJson)
    {
        try
        {
            using var document = JsonDocument.Parse(envelopeJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("body", out var bodyEl) || bodyEl.ValueKind != JsonValueKind.String)
                return null;

            var path = pathEl.GetString();
            return path is not null && _endpoint.IsAllowedUsagePath(path) ? (path, bodyEl.GetString() ?? "") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The path/body pairs a discovery script collected (see
    /// <see cref="CursorDiscoveryScript.Discover"/>), for <see cref="DescribeShape"/> to describe -
    /// only entries whose path is one of this provider's own allowed usage paths, capped the same way
    /// <see cref="ReadAttempts"/> caps its own list.</summary>
    private List<(string Path, string Body)> ReadResultEntries(string envelopeJson)
    {
        var entries = new List<(string Path, string Body)>();
        try
        {
            using var document = JsonDocument.Parse(envelopeJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
                return entries;

            foreach (var entry in results.EnumerateArray().Take(MaxLoggedAttempts))
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("body", out var bodyEl) || bodyEl.ValueKind != JsonValueKind.String)
                    continue;

                var path = pathEl.GetString();
                if (path is null || !_endpoint.IsAllowedUsagePath(path))
                    continue;

                entries.Add((path, bodyEl.GetString() ?? ""));
            }
        }
        catch (JsonException)
        {
        }
        return entries;
    }

    // Bounds for DescribeShape's own output - a future API returning a huge or deeply nested body
    // must never grow the log unbounded, and two levels is already enough to show a per-model object
    // like Cursor's candidate responses nest their numbers under.
    private const int MaxShapeEntries = 20;
    private const int MaxShapeDepth = 2;

    /// <summary>One usage response body's shape, as a flat line of field names and value kinds only -
    /// "gpt-5:{numRequests:number,maxRequestUsage:null}, startOfMonth:string" - proving what fields a
    /// live response actually carries without ever writing a value, a number or a string content into
    /// the log. At most <see cref="MaxShapeDepth"/> levels deep and <see cref="MaxShapeEntries"/>
    /// entries total, so a huge or deeply nested response cannot grow the log unbounded. Internal, not
    /// private: this class's own testable surface runs through <c>InternalsVisibleTo</c>, the same as
    /// <see cref="Interpret"/> above.</summary>
    internal static string DescribeShape(string bodyJson)
    {
        try
        {
            using var document = JsonDocument.Parse(bodyJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return "";

            var budget = MaxShapeEntries;
            return string.Join(", ", DescribeProperties(document.RootElement, depth: 1, ref budget));
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static List<string> DescribeProperties(JsonElement obj, int depth, ref int budget)
    {
        var entries = new List<string>();
        foreach (var property in obj.EnumerateObject())
        {
            if (budget <= 0)
                break;
            budget--;

            entries.Add(property.Value.ValueKind == JsonValueKind.Object && depth < MaxShapeDepth
                ? $"{CleanName(property.Name)}:{{{string.Join(",", DescribeProperties(property.Value, depth + 1, ref budget))}}}"
                : $"{CleanName(property.Name)}:{ShapeKindName(property.Value.ValueKind)}");
        }
        return entries;
    }

    private static string ShapeKindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Number => "number",
        JsonValueKind.String => "string",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => "unknown",
    };

    /// <summary>The candidate/status pairs a discovery script reports back, as one flat line. The page
    /// itself shapes this answer, so only entries of the exact expected form survive: one of this
    /// provider's own allowed paths, a space, and a three-digit status code. Anything else is dropped
    /// rather than written into the log.</summary>
    private string ReadAttempts(string envelopeJson)
    {
        try
        {
            using var document = JsonDocument.Parse(envelopeJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("attempts", out var attempts)
                || attempts.ValueKind != JsonValueKind.Array)
                return "";

            return string.Join(", ", attempts.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.String)
                .Select(entry => entry.GetString())
                .OfType<string>()
                .Where(IsPathAndStatus)
                .Take(MaxLoggedAttempts)
                .Select(entry => MaskIds(entry[..entry.LastIndexOf(' ')]) + entry[entry.LastIndexOf(' ')..]));
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>"&lt;an allowed usage path&gt; &lt;three-digit status&gt;" and nothing else.</summary>
    private bool IsPathAndStatus(string? entry)
    {
        if (entry is null)
            return false;

        var separator = entry.LastIndexOf(' ');
        if (separator <= 0 || entry.Length - separator - 1 != 3)
            return false;

        var status = entry.AsSpan(separator + 1);
        foreach (var digit in status)
        {
            if (!char.IsAsciiDigit(digit))
                return false;
        }

        return _endpoint.IsAllowedUsagePath(entry[..separator]);
    }

    private async Task<(WebUsageResult Result, string? Path)> RunWithPathAsync(string script, CancellationToken ct)
    {
        var (_, result) = await RunEnvelopeAsync(script, ct);
        return result;
    }

    /// <summary>Runs one script and hands back both the raw envelope (null when the call itself
    /// failed) and its interpretation, so a caller that wants more out of the envelope than the
    /// result does not have to run the script twice.</summary>
    private async Task<(string? EnvelopeJson, (WebUsageResult Result, string? Path) Result)> RunEnvelopeAsync(
        string script, CancellationToken ct)
    {
        string envelopeJson;
        try
        {
            envelopeJson = await _executeScript(script, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, (WebUsageResult.Failed, null));
        }

        return (envelopeJson, Interpret(envelopeJson));
    }

    // A label loose enough to have come from a page script must look at least roughly like an email
    // address before it is trusted as one - never guessed, never reconstructed from a partial value.
    private const int MaxAccountLabelLength = 254;

    /// <summary>An optional "email" field a discovery/fetch script may carry at the envelope's own
    /// root level, alongside "status" - never required, and never anything but a plain string the
    /// page itself read from the signed-in account (see <see cref="CursorDiscoveryScript.Discover"/>
    /// and <see cref="CodexDiscoveryScript.Discover"/>). Accepted only when it is a string containing
    /// '@' and no longer than <see cref="MaxAccountLabelLength"/> - anything else is dropped rather
    /// than shown as a guessed account name.</summary>
    internal static string? ReadAccountLabel(JsonElement root)
    {
        if (!root.TryGetProperty("email", out var emailEl) || emailEl.ValueKind != JsonValueKind.String)
            return null;

        // Printable characters only: the label reaches the screen, and a control or bidirectional
        // character in it could reorder or hide what stands around it.
        var email = emailEl.GetString();
        return email is { Length: > 0 and <= MaxAccountLabelLength } && email.Contains('@', StringComparison.Ordinal)
            && email.All(IsPrintable)
            ? email
            : null;
    }

    // The tier wording a page script reads out of the signed-in account; a plain string, bounded so a
    // page can never push a long text through to the tile.
    private const int MaxPlanLength = 200;

    /// <summary>An optional "plan" string at the envelope's own root level, next to "email" - used
    /// where the tier lives in an account answer rather than in the usage body itself (Claude's
    /// organisation record). Anything that is not a short string reads as no plan at all.</summary>
    private static string? ReadPlan(JsonElement root)
    {
        if (!root.TryGetProperty("plan", out var planEl) || planEl.ValueKind != JsonValueKind.String)
            return null;

        var plan = planEl.GetString();
        return plan is { Length: > 0 and <= MaxPlanLength } ? plan : null;
    }

    /// <summary>Pure envelope parsing - the actual unit-tested surface of this class.</summary>
    internal (WebUsageResult Result, string? Path) Interpret(string envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson))
            return (WebUsageResult.Failed, null);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(envelopeJson);
        }
        catch (JsonException)
        {
            return (WebUsageResult.Failed, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var statusEl) || statusEl.ValueKind != JsonValueKind.String)
                return (WebUsageResult.Failed, null);

            switch (statusEl.GetString())
            {
                case "not_signed_in":
                    return (WebUsageResult.NotSignedIn, null);
                case "blocked":
                    return (WebUsageResult.Blocked, null);
                case "ok":
                    var accountLabel = ReadAccountLabel(root);
                    var envelopePlan = ReadPlan(root);
                    if (root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String)
                    {
                        var parsed = _endpoint.ParseBody(bodyEl.GetString() ?? "");
                        // The session named its account even when the body carried no usable numbers.
                        if (parsed.Outcome != WebUsageOutcome.Ok)
                            return (parsed with { AccountLabel = accountLabel, SessionSignedIn = true, PlanType = parsed.PlanType ?? envelopePlan }, null);

                        var path = root.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String
                            ? pathEl.GetString()
                            : null;
                        // A page that shapes the discovery answer must never get an unvalidated path
                        // stored and later handed to Fetch - drop it instead of caching it.
                        if (path is not null && !_endpoint.IsAllowedUsagePath(path))
                            path = null;
                        return (parsed with { AccountLabel = accountLabel, SessionSignedIn = true, PlanType = parsed.PlanType ?? envelopePlan }, path);
                    }

                    // No single body: a discovery walk that never stops at the first JSON answer (see
                    // CursorDiscoveryScript.Discover) reports every candidate it collected instead, and
                    // this picks the first one that actually parses into a window - the earlier
                    // candidates in the walk are not necessarily the right endpoint.
                    if (root.TryGetProperty("results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in resultsEl.EnumerateArray().Take(MaxLoggedAttempts))
                        {
                            if (entry.ValueKind != JsonValueKind.Object
                                || !entry.TryGetProperty("body", out var entryBodyEl) || entryBodyEl.ValueKind != JsonValueKind.String)
                                continue;

                            var entryParsed = _endpoint.ParseBody(entryBodyEl.GetString() ?? "");
                            if (entryParsed.Outcome != WebUsageOutcome.Ok)
                                continue;

                            var entryPath = entry.TryGetProperty("path", out var entryPathEl) && entryPathEl.ValueKind == JsonValueKind.String
                                ? entryPathEl.GetString()
                                : null;
                            // Same allow-list gate as the single-body path above: an entry whose path
                            // is not one of this provider's own usage paths is skipped rather than
                            // returned with a null path, so it never wins over a later, valid entry.
                            if (entryPath is null || !_endpoint.IsAllowedUsagePath(entryPath))
                                continue;

                            return (entryParsed with { AccountLabel = accountLabel, SessionSignedIn = true, PlanType = entryParsed.PlanType ?? envelopePlan }, entryPath);
                        }
                    }

                    return (WebUsageResult.Failed with { AccountLabel = accountLabel, SessionSignedIn = true, PlanType = envelopePlan }, null);
                default:
                    return (WebUsageResult.Failed, null);
            }
        }
    }
}
