using System.Text.Json;

namespace AiUsage.Providers.LocalLogin;

/// <summary>
/// Reads the signed-in Claude Code account's own email address out of <c>~/.claude.json</c> -
/// Claude Code's own settings file, not the credentials file <see cref="ClaudeCodeLogin"/> reads the
/// access token from, and not a secret itself: <c>oauthAccount.emailAddress</c> is a plain profile
/// field the CLI already shows in its own UI. Never throws, never guesses - a missing file, a missing
/// field or a value that does not look like an email address all read as no label at all.
/// </summary>
internal static class ClaudeAccountLabelReader
{
    private const int MaxLength = 254;

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

    /// <summary>Test seam: a fixture path instead of the real profile folder.</summary>
    internal static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!document.RootElement.TryGetProperty("oauthAccount", out var account) || account.ValueKind != JsonValueKind.Object)
                return null;
            if (!account.TryGetProperty("emailAddress", out var emailEl) || emailEl.ValueKind != JsonValueKind.String)
                return null;

            var email = emailEl.GetString();
            return email is { Length: > 0 and <= MaxLength } && email.Contains('@', StringComparison.Ordinal)
                ? email
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
