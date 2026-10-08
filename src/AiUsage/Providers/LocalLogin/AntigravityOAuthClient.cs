using System.Text;
using System.Text.RegularExpressions;

namespace AiUsage.Providers.LocalLogin;

/// <summary>
/// Finds the OAuth client id/secret pair that Antigravity's own CLI uses, by scanning the locally
/// installed <c>agy.exe</c> for them - the same credentials that binary already ships and uses on
/// this machine, read only to refresh the user's own already-granted token. Nothing here is a user
/// secret and nothing is written anywhere; the discovered pair is cached in memory for the process
/// lifetime so the (large) binary is scanned at most once, and only when a token actually needs
/// refreshing.
/// </summary>
internal static class AntigravityOAuthClient
{
    private static readonly Regex ClientIdPattern = new(
        @"[0-9]{6,14}-[a-z0-9]{32}\.apps\.googleusercontent\.com", RegexOptions.Compiled);
    private static readonly Regex ClientSecretPattern = new(
        @"GOCSPX-[A-Za-z0-9_-]{28}", RegexOptions.Compiled);

    // Longest pattern is ~72 chars; overlapping consecutive chunks by this many bytes means no match
    // is ever split across a chunk boundary and missed.
    private const int OverlapBytes = 128;
    private const int ChunkBytes = 8 * 1024 * 1024;

    private static IReadOnlyList<(string ClientId, string ClientSecret)>? _cachedPairs;
    private static readonly object Gate = new();

    private static string DefaultExePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");

    /// <summary>Every distinct client-id × client-secret pairing found in the binary, cached after
    /// the first scan. Empty when the binary is absent or carries neither. The caller tries each pair
    /// against the token endpoint until one is accepted.</summary>
    public static IReadOnlyList<(string ClientId, string ClientSecret)> DiscoverPairs() =>
        DiscoverPairs(DefaultExePath());

    internal static IReadOnlyList<(string ClientId, string ClientSecret)> DiscoverPairs(string exePath)
    {
        lock (Gate)
        {
            if (_cachedPairs is not null)
                return _cachedPairs;

            var pairs = ScanForPairs(exePath);
            // An empty result (binary missing, or locked mid-update) is not cached, so installing or
            // updating the CLI works without restarting this app.
            if (pairs.Count > 0)
                _cachedPairs = pairs;
            return pairs;
        }
    }

    // A binary can carry many look-alike strings; the real pair sits among the first few. Keeping a
    // handful of each bounds the token endpoint attempts to MaxPerKind squared.
    private const int MaxPerKind = 4;

    internal static List<(string ClientId, string ClientSecret)> ScanForPairs(string exePath)
    {
        var ids = new List<string>();
        var secrets = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenSecrets = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[ChunkBytes + OverlapBytes];
            var carry = 0;
            int read;
            while ((read = stream.Read(buffer, carry, ChunkBytes)) > 0)
            {
                var available = carry + read;
                // Latin1 maps every byte to one char without loss, so ASCII patterns match exactly
                // and no multi-byte decoding can hide or shift a match.
                var text = Encoding.Latin1.GetString(buffer, 0, available);
                foreach (Match match in ClientIdPattern.Matches(text))
                    if (ids.Count < MaxPerKind && seenIds.Add(match.Value))
                        ids.Add(match.Value);
                foreach (Match match in ClientSecretPattern.Matches(text))
                    if (secrets.Count < MaxPerKind && seenSecrets.Add(match.Value))
                        secrets.Add(match.Value);

                if (ids.Count >= MaxPerKind && secrets.Count >= MaxPerKind)
                    break;

                carry = Math.Min(OverlapBytes, available);
                Array.Copy(buffer, available - carry, buffer, 0, carry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new List<(string, string)>();
        }

        var pairs = new List<(string, string)>(ids.Count * secrets.Count);
        foreach (var id in ids)
            foreach (var secret in secrets)
                pairs.Add((id, secret));
        return pairs;
    }
}
