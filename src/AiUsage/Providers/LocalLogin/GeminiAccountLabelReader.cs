using System.Text.Json;

namespace AiUsage.Providers.LocalLogin;

/// <summary>
/// Reads the signed-in Google account's email address out of <c>~/.gemini/google_accounts.json</c> -
/// the CLI's own record of which account is active (<c>{"active": "...", "old": [...]}</c>), a plain
/// profile field and not a secret itself; the token <see cref="AntigravityLocalLogin"/> uses lives
/// elsewhere. Never throws, never guesses - a missing file, a missing field or a value that does not
/// look like an email address all read as no label at all.
/// </summary>
internal static class GeminiAccountLabelReader
{
    private const int MaxLength = 254;

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "google_accounts.json");

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
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("active", out var activeEl)
                || activeEl.ValueKind != JsonValueKind.String)
                return null;

            var email = activeEl.GetString();
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
