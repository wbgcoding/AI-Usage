using System.Text.Json;

namespace AiUsage.Providers.LocalLogin;

/// <summary>
/// Reads the signed-in Claude Code account's email address and plan out of <c>~/.claude.json</c>,
/// Claude Code's own settings file, not the credentials file <see cref="ClaudeCodeLogin"/> reads the
/// access token from, and not a secret itself: <c>oauthAccount</c> is the profile Claude Code fetches
/// from its server and shows in its own UI. Never throws, never guesses: a missing file, a missing
/// field or a value that does not look like an email address all read as no label at all.
/// </summary>
internal static class ClaudeAccountLabelReader
{
    // Claude Code keeps this file inside a custom config folder (CLAUDE_CONFIG_DIR), else directly in
    // the profile folder; the default config folders are never searched for it.
    private static string DefaultPath() => PathFor(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Same as the default, from a given variable value and profile folder.</summary>
    internal static string PathFor(string? configDirValue, string userProfile)
    {
        foreach (var candidate in ClaudeConfigRoot.ConfiguredRoots(configDirValue))
        {
            var inside = Path.Combine(candidate, ".claude.json");
            if (File.Exists(inside))
                return inside;
        }

        return Path.Combine(userProfile, ".claude.json");
    }

    public static string? Read() => Read(DefaultPath());

    /// <summary>The account's plan wording (organisation type and rate limit tier), or null. Claude
    /// Code refreshes this profile from its server, while the credentials file keeps the tier of the
    /// last sign-in, so a plan changed since then shows here first.</summary>
    public static string? ReadPlan() => ReadPlan(DefaultPath());

    internal static string? DefaultProfilePath() => DefaultPath();

    // Claude Code keeps its whole project history in the same file, often several megabytes; an
    // unchanged file is not parsed again just to find the same values.
    private static readonly Lock Gate = new();
    private static (string Path, long Length, DateTime LastWriteUtc, string? Label, string? Plan)? _last;

    /// <summary>Test seam: a fixture path instead of the real profile folder.</summary>
    internal static string? Read(string path) => ReadAccount(path).Label;

    /// <summary>Test seam: a fixture path instead of the real profile folder.</summary>
    internal static string? ReadPlan(string path) => ReadAccount(path).Plan;

    private static (string? Label, string? Plan) ReadAccount(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return (null, null);

            lock (Gate)
            {
                if (_last is { } last && last.Path == path && last.Length == info.Length && last.LastWriteUtc == info.LastWriteTimeUtc)
                    return (last.Label, last.Plan);
            }

            var (label, plan) = Parse(path);
            lock (Gate)
                _last = (path, info.Length, info.LastWriteTimeUtc, label, plan);
            return (label, plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException)
        {
            return (null, null);
        }
    }

    private static (string? Label, string? Plan) Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return (null, null);
        if (!document.RootElement.TryGetProperty("oauthAccount", out var account) || account.ValueKind != JsonValueKind.Object)
            return (null, null);

        var email = Text(account, "emailAddress");
        var label = AccountLabels.LooksLikeEmail(email) ? email : null;

        var parts = new[] { Text(account, "organizationType"), Text(account, "organizationRateLimitTier") }.Where(part => part is not null);
        var plan = string.Join(' ', parts);
        return (label, plan.Length > 0 ? plan : null);
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 and <= 320 } value
            ? value
            : null;
}
