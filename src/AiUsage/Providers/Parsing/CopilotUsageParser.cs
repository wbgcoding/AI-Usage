using System.Text.Json;
using AiUsage.Models;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Parses a speculative future Copilot usage file: a "quotas" array of objects carrying
/// "used_percent" and "resets_at" - the same shape as the Gemini/Antigravity hypothetical file,
/// since no real Copilot usage file exists today and the real shape is
/// unknown until one actually appears. Stays deliberately permissive and skips any entry missing
/// a usable percentage rather than guessing one.
/// </summary>
public static class CopilotUsageParser
{
    public static IReadOnlyList<UsageWindow> TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("quotas", out var quotas) || quotas.ValueKind != JsonValueKind.Array)
                return [];

            var windows = new List<UsageWindow>();
            foreach (var entry in quotas.EnumerateArray())
            {
                if (!entry.TryGetProperty("used_percent", out var percentElement)
                    || percentElement.ValueKind != JsonValueKind.Number || !percentElement.TryGetDouble(out var percent))
                    continue;

                DateTimeOffset? resetsAt = entry.TryGetProperty("resets_at", out var resetsElement) &&
                    resetsElement.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(resetsElement.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                        ? parsed
                        : null;

                windows.Add(new UsageWindow("Window_Other", WindowKind.Other, percent, resetsAt, windowMinutes: null));
            }
            return windows;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return [];
        }
    }
}
