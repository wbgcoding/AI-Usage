using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AiUsage.Io;
using AiUsage.Providers;

namespace AiUsage.Providers.Parsing;

/// <summary>An already-triggered local rate limit read out of a session transcript.</summary>
public sealed record ClaudeQuotaLimit(DateTimeOffset ResetsAt, string RateLimitType);

/// <summary>
/// Reads the most recent already-triggered rate limit out of Claude's local session transcripts.
/// There is no running percentage locally - "rateLimits" is almost always
/// null; only a rejection leaves behind a "quotaLimits" object with a reset time. Never throws.
/// </summary>
public static class ClaudeLocalLimitReader
{
    private const int MaxFilesToCheck = 10;

    // Real project transcripts live under projects/<project>/ - three levels including the root is
    // enough for that layout with a little headroom, and bounds the walk regardless of what is
    // actually there.
    private const int MaxDepth = 6;

    /// <summary>
    /// Null when there is no local sign of an active limit. A snapshot can carry more than one kind
    /// of rejection (a five-hour one and a weekly one), each resetting on its own schedule, so an
    /// expired record of one kind must not hide a still-active record of another - every line within
    /// the existing file/line bounds is looked at, the newest record per <see
    /// cref="ClaudeQuotaLimit.RateLimitType"/> is kept (files and lines are walked newest-first, so
    /// the first record seen for a given type is already its newest one), and the newest of those
    /// that is still active wins.
    /// </summary>
    public static ClaudeQuotaLimit? FindActiveLimit(string projectsRoot, DateTimeOffset now, CancellationToken ct = default) =>
        FindActiveLimit(projectsRoot, now, out _, ct);

    /// <summary>Same lookup, plus the newest session file this scan already touched (null when none
    /// exists) - so a caller that also wants <see cref="SessionTurnReader"/>'s answer for "the file
    /// already found" gets it from this one directory walk, never a second one.</summary>
    public static ClaudeQuotaLimit? FindActiveLimit(
        string projectsRoot, DateTimeOffset now, out string? newestFilePath, CancellationToken ct = default) =>
        FindActiveLimit(projectsRoot, now, out newestFilePath, null, ct);

    /// <summary>Counts what a lookup actually read from disk, so a test can prove an unchanged file
    /// costs nothing.</summary>
    internal sealed class ReadStats
    {
        public int FilesRead;
        public long BytesRead;
    }

    /// <summary>What one transcript contributed: its newest rejection per limit type, with the line's
    /// own timestamp for the age bound, and how far into the file complete lines were consumed.</summary>
    private sealed record FileState(
        long Length, DateTime LastWriteUtc, long ConsumedOffset,
        IReadOnlyDictionary<string, (ClaudeQuotaLimit Limit, DateTimeOffset? LineTime)> Newest);

    // Transcripts only ever grow by appending, so a file seen before is read again from where the
    // last complete line ended instead of from the start. Entries are immutable and replaced whole,
    // so concurrent lookups at worst repeat work.
    private static readonly ConcurrentDictionary<string, FileState> Cache = new(StringComparer.OrdinalIgnoreCase);

    // A line longer than this is none of the shapes this reader recognises.
    private const int MaxLineBytes = 4 * 1024 * 1024;

    internal static ClaudeQuotaLimit? FindActiveLimit(
        string projectsRoot, DateTimeOffset now, out string? newestFilePath, ReadStats? stats, CancellationToken ct = default)
    {
        newestFilePath = null;
        if (!Directory.Exists(projectsRoot))
            return null;

        try
        {
            var files = LocalFileScan.NewestFiles(projectsRoot, "*.jsonl", MaxDepth, MaxFilesToCheck);
            newestFilePath = files.Count > 0 ? files[0].FullName : null;
            PruneCache(projectsRoot, files);

            var newestPerType = new Dictionary<string, ClaudeQuotaLimit>(StringComparer.Ordinal);
            var cutoff = now - SessionLineAge.MaxAge;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                // Files are newest-first by write time: once one is past the age bound, so are the rest.
                if (file.LastWriteTimeUtc < cutoff.UtcDateTime)
                    break;

                var state = LoadState(file, stats, ct);
                if (state is null)
                    continue;

                // Files are walked newest-first, so the first record seen for a type is its newest.
                foreach (var (type, (limit, lineTime)) in state.Newest)
                {
                    if (lineTime is { } at && at < cutoff)
                        continue; // older than any live window
                    newestPerType.TryAdd(type, limit);
                }
            }

            return newestPerType.Values
                .Where(limit => limit.ResetsAt > now)
                .OrderByDescending(limit => limit.ResetsAt)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void PruneCache(string root, IReadOnlyList<FileInfo> current)
    {
        var keep = new HashSet<string>(current.Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        foreach (var path in Cache.Keys)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !keep.Contains(path))
                Cache.TryRemove(path, out _);
    }

    private static FileState? LoadState(FileInfo file, ReadStats? stats, CancellationToken ct)
    {
        var path = file.FullName;
        var length = file.Length;
        var writeUtc = file.LastWriteTimeUtc;
        Cache.TryGetValue(path, out var cached);

        if (cached is not null && cached.Length == length && cached.LastWriteUtc == writeUtc && cached.ConsumedOffset == length)
            return cached;

        // Only a grown file keeps its earlier bytes; anything else is read from the start.
        var startOffset = 0L;
        var known = new Dictionary<string, (ClaudeQuotaLimit, DateTimeOffset?)>(StringComparer.Ordinal);
        if (cached is not null && length >= cached.Length && writeUtc >= cached.LastWriteUtc)
        {
            startOffset = cached.ConsumedOffset;
            foreach (var pair in cached.Newest)
                known[pair.Key] = pair.Value;
        }

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var consumed = CompleteLineReader.Read(
                stream, startOffset, acceptTrailingLineWithoutNewline: false, MaxLineBytes, onOversized: () => { },
                line => Absorb(line, known), ct);
            var bytesRead = Math.Max(0, consumed - startOffset);

            var state = new FileState(length, writeUtc, consumed,
                new Dictionary<string, (ClaudeQuotaLimit Limit, DateTimeOffset? LineTime)>(known));
            Cache[path] = state;

            // A final line without its newline may still be growing, so it is looked at for this
            // answer only and never cached.
            var result = state;
            var end = Math.Min(stream.Length, length);
            if (end > consumed && end - consumed <= MaxLineBytes)
            {
                var tail = new byte[end - consumed];
                stream.Seek(consumed, SeekOrigin.Begin);
                var got = stream.ReadAtLeast(tail, tail.Length, throwOnEndOfStream: false);
                bytesRead += got;
                Absorb(Encoding.UTF8.GetString(tail, 0, got).TrimEnd('\r'), known);
                result = state with
                {
                    Newest = new Dictionary<string, (ClaudeQuotaLimit Limit, DateTimeOffset? LineTime)>(known),
                };
            }

            if (stats is not null)
            {
                stats.FilesRead++;
                stats.BytesRead += bytesRead;
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // vanished or locked between the scan and the open
        }
    }

    private static void Absorb(string line, Dictionary<string, (ClaudeQuotaLimit, DateTimeOffset?)> newest)
    {
        if (!line.Contains("quotaLimits", StringComparison.Ordinal))
            return;
        if (!TryParseQuotaLimit(line, out var limit, out var lineTime) || limit is null)
            return; // the substring matched but the line did not parse as expected

        newest[limit.RateLimitType] = (limit, lineTime); // later in the file = newer
    }

    private static bool TryParseQuotaLimit(string line, out ClaudeQuotaLimit? limit, out DateTimeOffset? lineTime)
    {
        limit = null;
        lineTime = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("quotaLimits", out var quotaLimits) ||
                quotaLimits.ValueKind != JsonValueKind.Object)
                return false;
            if (!quotaLimits.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
                status.GetString() != "rejected")
                return false;
            if (!quotaLimits.TryGetProperty("resetsAt", out var resetsAtElement) ||
                resetsAtElement.ValueKind != JsonValueKind.Number || !resetsAtElement.TryGetInt64(out var resetsAtUnix))
                return false;

            var resetsAt = UnixTimeConversion.FromUnixSecondsOrNull(resetsAtUnix) is { } instant
                ? UnixTimeConversion.PlausibleOrNull(instant)
                : null;
            if (resetsAt is null)
                return false; // out of range, or implausibly far off - treat like any other unrecognised line

            var rateLimitType = quotaLimits.TryGetProperty("rateLimitType", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString() ?? "unknown"
                    : "unknown";

            if (document.RootElement.TryGetProperty("timestamp", out var stamp) && stamp.ValueKind == JsonValueKind.String &&
                SessionLineAge.TryParse(stamp.GetString(), out var parsedTime))
                lineTime = parsedTime;

            limit = new ClaudeQuotaLimit(resetsAt.Value, rateLimitType);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
