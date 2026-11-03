using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiUsage.Models;
using AiUsage.Services;

namespace AiUsage.Providers.Parsing;

internal enum ClaudeConsoleOrgOutcome
{
    Found,
    NoApiOrganization,
    Unreadable,
}

internal readonly record struct ClaudeConsoleOrgPick(ClaudeConsoleOrgOutcome Outcome, string? OrganizationId = null,
    IReadOnlyList<string>? OrganizationIds = null);

internal enum ClaudeConsoleCreditOutcome
{
    Ok,
    Unreadable,
}

internal readonly record struct ClaudeConsoleCreditReading(ClaudeConsoleCreditOutcome Outcome, ClaudeApiCredit? Credit = null);

/// <summary>
/// Reads the two answers of the Claude Console that the API credit balance needs: the organisation
/// list (which one is the API organisation) and the prepaid credit answer (an amount in cents).
/// Pure text in, values out; what no longer matches is named by <see cref="ChangeNotes"/>.
/// </summary>
internal static class ClaudeConsoleCreditParser
{
    private const string DefaultCurrency = "USD";

    private static readonly Regex OrganizationIdPattern = new(@"\A[0-9a-fA-F-]{36}\z", RegexOptions.Compiled);
    private static readonly Regex CurrencyPattern = new(@"\A[A-Za-z]{3}\z", RegexOptions.Compiled);

    internal static bool IsOrganizationId(string? value) => value is not null && OrganizationIdPattern.IsMatch(value);

    /// <summary>The API organisation to ask: the one the console itself has open (<paramref name="lastActiveOrg"/>,
    /// the cookie value) when it is an API organisation, else the first API organisation. An API
    /// organisation is one whose capabilities name "api". <see cref="ClaudeConsoleOrgPick.OrganizationIds"/>
    /// holds every API organisation in asking order, the picked one first.</summary>
    internal static ClaudeConsoleOrgPick PickOrganization(string? organizationsJson, string? lastActiveOrg)
    {
        if (string.IsNullOrWhiteSpace(organizationsJson))
            return new ClaudeConsoleOrgPick(ClaudeConsoleOrgOutcome.Unreadable);

        try
        {
            using var document = JsonDocument.Parse(organizationsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new ClaudeConsoleOrgPick(ClaudeConsoleOrgOutcome.Unreadable);

            var apiIds = new List<string>();
            var readable = false;
            foreach (var org in document.RootElement.EnumerateArray())
            {
                if (org.ValueKind != JsonValueKind.Object
                    || !org.TryGetProperty("uuid", out var uuid) || uuid.ValueKind != JsonValueKind.String
                    || !org.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array)
                    continue;

                readable = true;
                var id = uuid.GetString();
                if (IsOrganizationId(id) && capabilities.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "api"))
                    apiIds.Add(id!);
            }

            if (apiIds.Count > 0)
            {
                var active = lastActiveOrg is not null
                    ? apiIds.FirstOrDefault(id => string.Equals(id, lastActiveOrg, StringComparison.OrdinalIgnoreCase))
                    : null;
                var first = active ?? apiIds[0];
                var ordered = new List<string> { first };
                ordered.AddRange(apiIds.Where(id => !string.Equals(id, first, StringComparison.OrdinalIgnoreCase)));
                return new ClaudeConsoleOrgPick(ClaudeConsoleOrgOutcome.Found, first, ordered);
            }

            // An empty list or organisations without the API capability: signed in, but no console account.
            return new ClaudeConsoleOrgPick(readable || document.RootElement.GetArrayLength() == 0
                ? ClaudeConsoleOrgOutcome.NoApiOrganization
                : ClaudeConsoleOrgOutcome.Unreadable);
        }
        catch (JsonException)
        {
            return new ClaudeConsoleOrgPick(ClaudeConsoleOrgOutcome.Unreadable);
        }
    }

    /// <summary>The balance from the prepaid credit answer: "amount" is an integer count of cents, the
    /// optional "currency" a three-letter code (USD when absent).</summary>
    internal static ClaudeConsoleCreditReading Parse(string? body, DateTimeOffset? now = null)
    {
        var unreadable = new ClaudeConsoleCreditReading(ClaudeConsoleCreditOutcome.Unreadable);
        if (string.IsNullOrWhiteSpace(body))
            return unreadable;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number
                || !amount.TryGetDecimal(out var cents))
                return unreadable;

            var currency = root.TryGetProperty("currency", out var code) && code.ValueKind == JsonValueKind.String
                && code.GetString() is { } text && CurrencyPattern.IsMatch(text)
                ? text.ToUpperInvariant()
                : DefaultCurrency;
            var grant = ReadGrant(root, now ?? DateTimeOffset.UtcNow, out _);
            return new ClaudeConsoleCreditReading(ClaudeConsoleCreditOutcome.Ok, new ClaudeApiCredit(cents / 100m, currency, grant));
        }
        catch (JsonException)
        {
            return unreadable;
        }
    }

    /// <summary>What in the two answers no longer matches what this parser was written for: an
    /// organisation list whose entries carry no uuid or capabilities, a credit answer without a
    /// numeric amount. Key names only, never a figure or an id; empty for text that is no JSON (a
    /// challenge page is no change of shape).</summary>
    /// <summary>
    /// The free credit grants of the credit answer ("promo_tranches": each with "granted_amount_minor_units",
    /// "remaining_amount_minor_units" and an optional "expires_at"), summed over the grants not yet
    /// expired at <paramref name="now"/>. Null when the answer lists none, every grant has expired, or
    /// one entry does not hold together (then <paramref name="unreadable"/> is true): a half-read sum
    /// would show a wrong "used" figure, so nothing is shown instead, the way the console does it.
    /// </summary>
    internal static ClaudeCreditGrant? ReadGrant(JsonElement root, DateTimeOffset now, out bool unreadable)
    {
        unreadable = false;
        if (!root.TryGetProperty("promo_tranches", out var tranches) || tranches.ValueKind == JsonValueKind.Null)
            return null;

        if (tranches.ValueKind != JsonValueKind.Array)
        {
            unreadable = true;
            return null;
        }

        decimal granted = 0, remaining = 0;
        DateTimeOffset? nextExpiry = null;
        foreach (var tranche in tranches.EnumerateArray())
        {
            if (tranche.ValueKind != JsonValueKind.Object
                || !tranche.TryGetProperty("granted_amount_minor_units", out var grantedEl) || grantedEl.ValueKind != JsonValueKind.Number
                || !grantedEl.TryGetDecimal(out var grantedCents)
                || !tranche.TryGetProperty("remaining_amount_minor_units", out var remainingEl) || remainingEl.ValueKind != JsonValueKind.Number
                || !remainingEl.TryGetDecimal(out var remainingCents)
                || grantedCents <= 0 || remainingCents < 0 || remainingCents > grantedCents)
            {
                unreadable = true;
                return null;
            }

            DateTimeOffset? expires = null;
            if (tranche.TryGetProperty("expires_at", out var expiresEl) && expiresEl.ValueKind == JsonValueKind.String)
            {
                if (!DateTimeOffset.TryParse(expiresEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    unreadable = true;
                    return null;
                }

                expires = parsed;
            }

            if (expires is { } end && end <= now)
                continue;

            granted += grantedCents;
            remaining += remainingCents;
            if (expires is { } next && (nextExpiry is null || next < nextExpiry))
                nextExpiry = next;
        }

        return granted > 0 ? new ClaudeCreditGrant(granted / 100m, remaining / 100m, nextExpiry) : null;
    }

    internal static IReadOnlyList<string> ChangeNotes(string? organizationsJson, string? creditJson)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(organizationsJson) && PickOrganization(organizationsJson, null).Outcome == ClaudeConsoleOrgOutcome.Unreadable)
        {
            var keys = FirstObjectKeys(organizationsJson);
            if (keys is not null)
                notes.Add($"the Claude Console organisation list holds no entry with uuid and capabilities (keys: {keys}).");
        }

        if (!string.IsNullOrWhiteSpace(creditJson) && Parse(creditJson).Outcome == ClaudeConsoleCreditOutcome.Unreadable)
        {
            try
            {
                using var document = JsonDocument.Parse(creditJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    notes.Add($"the Claude Console credit answer holds no numeric amount (keys: {ReadChangeLog.SafeNames(document.RootElement.EnumerateObject().Select(p => p.Name))}).");
            }
            catch (JsonException)
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(creditJson))
        {
            try
            {
                using var document = JsonDocument.Parse(creditJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    ReadGrant(document.RootElement, DateTimeOffset.UtcNow, out var unreadable);
                    if (unreadable)
                        notes.Add("the Claude Console credit answer lists free credit grants in a shape that no longer matches (promo_tranches).");
                }
            }
            catch (JsonException)
            {
            }
        }

        return notes;
    }

    // Key names of the first object in the list (or of the answer itself when it is an object).
    private static string? FirstObjectKeys(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                return ReadChangeLog.SafeNames(root.EnumerateObject().Select(p => p.Name));
            if (root.ValueKind == JsonValueKind.Array)
                foreach (var entry in root.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object)
                        return ReadChangeLog.SafeNames(entry.EnumerateObject().Select(p => p.Name));
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
