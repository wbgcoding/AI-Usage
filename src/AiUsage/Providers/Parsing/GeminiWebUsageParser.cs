using AiUsage.Models;

namespace AiUsage.Providers.Parsing;

/// <summary>
/// Turns one AI Studio usage response into windows, the same way <see cref="CodexWebUsageParser"/>
/// turns Codex's web response into windows: a pure function, no clock or network of its own, an
/// unrecognised shape yields no windows rather than a guessed number. The only Gemini quota JSON
/// shape this codebase has ever actually measured is the one <see cref="GeminiUsageParser"/> already
/// reads from the Antigravity CLI's own sign-in (see its own remarks) - both ultimately read the
/// same Google quota backend, just through a different front door, so the web response is expected
/// to carry the same groups/buckets/remainingFraction shape until a real capture proves otherwise.
/// </summary>
public static class GeminiWebUsageParser
{
    public static IReadOnlyList<UsageWindow> Parse(string json) => GeminiUsageParser.Parse(json);
}
