using System.Text;
using System.Text.RegularExpressions;

namespace AiUsage.Providers.LocalLogin;

/// <summary>
/// Finds the OAuth client id/secret pair that Antigravity's own CLI uses, by scanning the locally
/// installed <c>agy.exe</c> for them - the same credentials that binary already ships and uses on
/// this machine, read only to refresh the user's own already-granted token. Nothing here is a user
/// secret and nothing is written anywhere; the discovered pairs are cached in memory so the (large)
/// binary is scanned at most once per version, and only when a token actually needs
/// refreshing. A scan that finds nothing is remembered for the same file (length and write time), so
/// an exe without a complete pair is not read in full on every attempt.
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

    // The file a completed scan found nothing in. A different length or write time (an update) scans again.
    private static (long Length, DateTime LastWriteUtc)? _emptyScanOf;
    private static readonly object Gate = new();

    private static string DefaultExePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");

    /// <summary>Every distinct client-id × client-secret pairing found in the binary, cached after
    /// the first scan. Empty when the binary is absent or carries neither. The caller tries each pair
    /// against the token endpoint until one is accepted.</summary>
    public static IReadOnlyList<(string ClientId, string ClientSecret)> DiscoverPairs() =>
        DiscoverPairs(DefaultExePath());

    /// <summary>Drops the cached pairs, so the next discovery reads the binary again. Called when no
    /// pair was accepted: the CLI was probably updated and ships new ones.</summary>
    public static void ForgetPairs()
    {
        lock (Gate)
        {
            _cachedPairs = null;
            _emptyScanOf = null;
        }
    }

    internal static IReadOnlyList<(string ClientId, string ClientSecret)> DiscoverPairs(
        string exePath, Func<string, (List<(string ClientId, string ClientSecret)> Pairs, bool Completed)>? scan = null)
    {
        lock (Gate)
        {
            if (_cachedPairs is not null)
                return _cachedPairs;

            FileInfo info;
            try
            {
                info = new FileInfo(exePath);
                if (!info.Exists)
                    return [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return [];
            }

            var key = (info.Length, info.LastWriteTimeUtc);
            if (_emptyScanOf == key)
                return [];

            var (pairs, completed) = (scan ?? ScanWithOutcome)(exePath);
            if (pairs.Count > 0)
                _cachedPairs = pairs;
            else if (completed)
                _emptyScanOf = key; // a binary locked mid-update is not remembered: it was never read
            return pairs;
        }
    }

    private static (List<(string ClientId, string ClientSecret)> Pairs, bool Completed) ScanWithOutcome(string exePath)
    {
        var pairs = ScanForPairs(exePath, out var completed);
        return (pairs, completed);
    }

    // A binary can carry many look-alike strings; the real pair sits among the first few. Keeping a
    // handful of each bounds the token endpoint attempts to MaxPerKind squared.
    private const int MaxPerKind = 4;

    internal static List<(string ClientId, string ClientSecret)> ScanForPairs(string exePath) =>
        ScanForPairs(exePath, out _);

    /// <summary><paramref name="completed"/> is false when the file could not be read, so an empty
    /// answer says nothing about what the binary holds.</summary>
    internal static List<(string ClientId, string ClientSecret)> ScanForPairs(string exePath, out bool completed)
    {
        completed = false;
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

        completed = true;
        var pairs = new List<(string, string)>(ids.Count * secrets.Count);
        foreach (var id in ids)
            foreach (var secret in secrets)
                pairs.Add((id, secret));
        return pairs;
    }
}
