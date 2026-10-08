using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AiUsage.Io;
using AiUsage.Models;

namespace AiUsage.Storage;

/// <summary>
/// One append-only file per provider: sparse writes, backward reads for a
/// recent range, and a two-stage compaction so a ten-year history stays a few MB. Never throws.
/// </summary>
public class HistoryStore
{
    // Internal, not private: a test plants a line carrying SchemaVersion + 1 to prove a file with a
    // point from a schema this build does not understand is never rewritten.
    internal const int SchemaVersion = 1;
    private static readonly TimeSpan MinAppendInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HourCompactionAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan DayCompactionAge = TimeSpan.FromDays(365);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private volatile string _dataDirectory;
    private readonly Func<DateTimeOffset> _now;

    // One lock per provider file: Append is called from MainViewModel.OnSnapshotReady's
    // dispatcher-marshalled tail, while Compact/Prune run off a periodic timer on a ThreadPool
    // thread - without a lock, a rewrite racing an Append could silently lose the just-appended
    // line. Per provider, so one provider's whole-file rewrite never holds up another's Append.
    private readonly ConcurrentDictionary<string, ProviderState> _providers = new(StringComparer.OrdinalIgnoreCase);

    private sealed class ProviderState
    {
        public readonly object Gate = new();
        public readonly object PendingGate = new();
        public List<PendingAppend> Pending = [];

        // The points of the file as last parsed; only touched under Gate.
        public LoadCache? Points;
    }

    /// <summary>The parsed points of one provider file from <see cref="CoveredFrom"/> on, and the file
    /// state they were read at. <see cref="Consumed"/> is where the last complete line ended, which is
    /// less than the length while a writer is mid line.</summary>
    private sealed class LoadCache(
        string path, long length, DateTime writeUtc, long consumed, DateTimeOffset coveredFrom, List<HistoryPoint> points)
    {
        public readonly DateTimeOffset CoveredFrom = coveredFrom;
        public readonly string Path = path;
        public readonly long Length = length;
        public readonly DateTime WriteUtc = writeUtc;
        public readonly long Consumed = consumed;
        public readonly List<HistoryPoint> Points = points;
    }

    // A history line is a few dozen bytes; anything past this is garbage and skipped.
    private const int MaxLineBytes = 1024 * 1024;

    // An Append that found its provider's lock taken (a rewrite is running): kept here and written
    // once the lock holder is done, so the UI thread never waits for a rewrite.
    private readonly record struct PendingAppend(
        WindowKind Window, double Percent, DateTimeOffset? ResetsAt, long? Tokens, string Label, DateTimeOffset Now);

    private ProviderState StateOf(string providerId) =>
        _providers.GetOrAdd(AppPaths.SanitizeAccountKeyForFileName(providerId), _ => new ProviderState());

    // The newest point per provider and window, as it stands on disk. Every refresh asks for it
    // once per window, for a value this process itself wrote a moment ago; without this the answer
    // came off disk each time, four providers times two windows times every tick. A null entry
    // means "looked, and there is none" - that answer is worth caching just as much.
    // Label is part of the key too: a provider can carry more than one WindowKind.Other series at
    // once (Cursor's per-model bars, its Grok Bot bar), and each needs its own "newest point on
    // disk" answer - sharing one entry between them let one series's write suppress another's.
    private readonly ConcurrentDictionary<(string ProviderId, WindowKind Window, string Label), HistoryPoint?> _lastPoints = new();

    public HistoryStore() : this(AppPaths.DataDirectory, now: null)
    {
    }

    /// <summary>Test seam: a temp directory and a fixed clock instead of the real data folder and DateTimeOffset.Now.</summary>
    internal HistoryStore(string dataDirectory, Func<DateTimeOffset>? now)
    {
        _dataDirectory = dataDirectory;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    private string FilePath(string providerId) =>
        Path.Combine(_dataDirectory, $"history-{AppPaths.SanitizeAccountKeyForFileName(providerId)}.jsonl");

    /// <summary>Called once a "move my data" operation has already copied every history file to
    /// <paramref name="dataDirectory"/> - every append/read/compact from this point on targets the
    /// new location. The cached <see cref="_lastPoints"/> stay valid across the move: the copy is
    /// byte-for-byte, so a value already known to be the newest point is still the newest point at
    /// the new path.</summary>
    internal void Redirect(string dataDirectory)
    {
        // Every provider lock, in a fixed order: a rewrite still running against the old folder
        // finishes first, and no other thread ever holds two of them at once.
        var states = _providers.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Value).ToList();
        RedirectLocked(states, 0, dataDirectory);
    }

    private void RedirectLocked(List<ProviderState> states, int index, string dataDirectory)
    {
        if (index == states.Count)
        {
            _dataDirectory = dataDirectory;
            foreach (var state in states)
                state.Points = null; // parsed from the old folder
            return;
        }

        lock (states[index].Gate)
            RedirectLocked(states, index + 1, dataDirectory);
    }

    /// <summary>
    /// How often the newest point had to be read back off disk. A test asserts this stays flat
    /// across repeated appends of an unchanged value; nothing in the app reads it.
    /// </summary>
    internal int LastPointReadsForTest { get; private set; }

    /// <summary>How many history lines <see cref="Load(string, DateTimeOffset, DateTimeOffset)"/> had
    /// to parse in total. A test asserts an unchanged file adds nothing; nothing in the app reads it.</summary>
    internal int LoadLinesParsedForTest => Volatile.Read(ref _loadLinesParsed);

    private int _loadLinesParsed;

    /// <summary>Test seam: runs inside a rewrite's lock (Compact/Prune), right after it was taken, so
    /// a test can hold one provider's rewrite open and watch what else still gets through.</summary>
    internal Action<string>? InsideRewriteLockForTest { get; set; }

    /// <summary>
    /// Writes a point only if the value changed since the last stored point for this window, or at
    /// least <see cref="MinAppendInterval"/> has passed - a steady value otherwise writes nothing.
    /// </summary>
    public void Append(string providerId, WindowKind window, double percent, DateTimeOffset? resetsAt, long? tokens = null, string label = "")
    {
        var state = StateOf(providerId);
        var pending = new PendingAppend(window, percent, resetsAt, tokens, label, _now());
        if (!Monitor.TryEnter(state.Gate))
        {
            lock (state.PendingGate)
                state.Pending.Add(pending);
            // The holder may have just released: whoever finds the lock free writes the queue.
            DrainIfFree(providerId, state);
            return;
        }

        try
        {
            FlushPending(providerId, state);
            AppendCore(providerId, pending);
        }
        finally
        {
            Monitor.Exit(state.Gate);
        }

        DrainIfFree(providerId, state);
    }

    private void AppendCore(string providerId, PendingAppend request)
    {
        var last = LastPoint(providerId, request.Window, request.Label);
        if (last is not null)
        {
            var unchanged = Math.Abs(last.Percent - request.Percent) < 0.001;
            var tooSoon = request.Now - last.Timestamp < MinAppendInterval;
            if (unchanged && tooSoon)
                return;
        }

        AppendLine(providerId, new HistoryPoint(
            SchemaVersion, request.Now, request.Window, request.Percent, request.ResetsAt, Tokens: request.Tokens,
            Label: request.Label.Length > 0 ? request.Label : null));
    }

    /// <summary>Writes the queued appends, oldest first. The caller holds the provider's lock.</summary>
    private void FlushPending(string providerId, ProviderState state)
    {
        List<PendingAppend> queued;
        lock (state.PendingGate)
        {
            if (state.Pending.Count == 0)
                return;
            queued = state.Pending;
            state.Pending = [];
        }

        foreach (var request in queued)
            AppendCore(providerId, request);
    }

    private void DrainIfFree(string providerId, ProviderState state)
    {
        while (HasPending(state) && Monitor.TryEnter(state.Gate))
        {
            try
            {
                FlushPending(providerId, state);
            }
            finally
            {
                Monitor.Exit(state.Gate);
            }
        }
    }

    private static bool HasPending(ProviderState state)
    {
        lock (state.PendingGate)
            return state.Pending.Count > 0;
    }

    /// <summary>Runs <paramref name="body"/> under the provider's lock, then writes whatever
    /// appends queued up meanwhile.</summary>
    private T Locked<T>(string providerId, Func<T> body)
    {
        var state = StateOf(providerId);
        T result;
        lock (state.Gate)
        {
            try
            {
                result = body();
            }
            finally
            {
                FlushPending(providerId, state);
            }
        }

        DrainIfFree(providerId, state);
        return result;
    }

    private void Locked(string providerId, Action body) => Locked<object?>(providerId, () =>
    {
        body();
        return null;
    });

    /// <summary>Reads backward and stops as soon as a point falls outside <paramref name="range"/> - a
    /// multi-year file never has to be read whole for a 24-hour chart. A thin wrapper over <see
    /// cref="Load(string, DateTimeOffset, DateTimeOffset)"/> anchored to this store's own clock.</summary>
    public IReadOnlyList<HistoryPoint> Load(string providerId, TimeSpan range)
    {
        var now = _now();
        return Load(providerId, now - range, now);
    }

    /// <summary>Reads backward and stops as soon as a point falls before <paramref name="from"/> - a
    /// multi-year file never has to be read whole for a narrow slice. A point newer than <paramref
    /// name="to"/> is skipped without stopping the scan, so a slice that ends in the past (the
    /// previous-week comparison line) still finds its data behind whatever was appended since.</summary>
    public IReadOnlyList<HistoryPoint> Load(string providerId, DateTimeOffset from, DateTimeOffset to)
    {
        var path = FilePath(providerId);
        if (!File.Exists(path))
            return [];

        return Locked<IReadOnlyList<HistoryPoint>>(providerId, () =>
        {
            var state = StateOf(providerId);
            try
            {
                var all = LoadPoints(state, providerId, path, from);
                // A new list every time: callers get to keep and change theirs.
                return all.Where(point => point.Timestamp >= from && point.Timestamp <= to).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the file existed a moment ago but became locked/unreadable/vanished since - never
                // worth crashing over, the chart just stays empty for this tick.
                state.Points = null;
                return [];
            }
        });
    }

    /// <summary>The valid points of the file from <paramref name="from"/> on, in file order, kept in
    /// the cache. A first read (or one asking further back than the cache reaches) scans from the end
    /// and stops at the first point before <paramref name="from"/>, so a multi-year file is never read
    /// whole for a narrow slice. The file only grows by appends between rewrites (which drop the
    /// cache), so a cached file is later read again only from where its last complete line ended; any
    /// other change scans afresh. A final line without its newline is shown but not cached, since the
    /// writer may still be adding to it. The caller holds the provider's lock and must not change the
    /// returned list.</summary>
    private List<HistoryPoint> LoadPoints(ProviderState state, string providerId, string path, DateTimeOffset from)
    {
        var info = new FileInfo(path);
        var length = info.Length;
        var writeUtc = info.LastWriteTimeUtc;
        var cache = state.Points;

        if (cache is not null && cache.Path == path && from >= cache.CoveredFrom)
        {
            if (cache.Length == length && cache.WriteUtc == writeUtc && cache.Consumed == length)
                return cache.Points;
            if (length >= cache.Length && writeUtc >= cache.WriteUtc)
                return ExtendCache(state, cache, providerId, length, writeUtc);
        }

        return ScanFromEnd(state, providerId, path, from, length, writeUtc);
    }

    private List<HistoryPoint> ExtendCache(ProviderState state, LoadCache cache, string providerId, long length, DateTime writeUtc)
    {
        var added = new List<HistoryPoint>();
        using var stream = new FileStream(cache.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var consumed = CompleteLineReader.Read(
            stream, cache.Consumed, acceptTrailingLineWithoutNewline: false, MaxLineBytes, onOversized: () => { },
            line =>
            {
                Interlocked.Increment(ref _loadLinesParsed);
                if (ParseLineOrNull(line, providerId) is { } point)
                    added.Add(point);
            }, CancellationToken.None);

        cache.Points.AddRange(added);
        state.Points = new LoadCache(cache.Path, length, writeUtc, consumed, cache.CoveredFrom, cache.Points);

        var end = Math.Min(stream.Length, length);
        if (end <= consumed || end - consumed > MaxLineBytes)
            return cache.Points;

        var tail = new byte[end - consumed];
        stream.Seek(consumed, SeekOrigin.Begin);
        var got = stream.ReadAtLeast(tail, tail.Length, throwOnEndOfStream: false);
        Interlocked.Increment(ref _loadLinesParsed);
        return ParseLineOrNull(Utf8NoBom.GetString(tail, 0, got).TrimEnd('\r'), providerId) is { } tailPoint
            ? [.. cache.Points, tailPoint]
            : cache.Points;
    }

    private List<HistoryPoint> ScanFromEnd(
        ProviderState state, string providerId, string path, DateTimeOffset from, long length, DateTime writeUtc)
    {
        state.Points = null;

        // The length is taken from the open file itself and the scan is bounded to it: lines another
        // copy of the app appends meanwhile belong to the next incremental read, not to this cache.
        bool endsWithNewline;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            length = stream.Length;
            if (length == 0)
                return [];

            stream.Seek(length - 1, SeekOrigin.Begin);
            endsWithNewline = stream.ReadByte() == '\n';
        }

        var points = new List<HistoryPoint>();
        HistoryPoint? tailPoint = null;
        long tailBytes = 0;
        var first = true;
        foreach (var line in ReverseLineReader.ReadLinesReversed(path, upToLength: length))
        {
            var isTail = first && !endsWithNewline;
            first = false;
            if (isTail)
                tailBytes = Utf8NoBom.GetByteCount(line);

            Interlocked.Increment(ref _loadLinesParsed);
            var point = ParseLineOrNull(line, providerId);
            if (point is null)
                continue; // a corrupt single line is skipped, never treated as "out of range"

            if (point.Timestamp < from)
                break;

            if (isTail)
                tailPoint = point;
            else
                points.Add(point);
        }

        points.Reverse();

        // Where the last complete line ends. Anything else (an unfinished line that does not add up)
        // is simply not cached.
        var consumed = endsWithNewline ? length : length - tailBytes;
        if (consumed >= 0 && (consumed == 0 || ByteBefore(path, consumed) == '\n'))
            state.Points = new LoadCache(path, length, writeUtc, consumed, from, points);

        return tailPoint is null ? points : [.. points, tailPoint];
    }

    private static int ByteBefore(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset - 1, SeekOrigin.Begin);
        return stream.ReadByte();
    }

    /// <summary>Whether a history file for <paramref name="providerId"/> is on disk, so a new account
    /// never takes over a key a kept history still belongs to.</summary>
    internal bool HasHistory(string providerId) =>
        Locked(providerId, () => File.Exists(FilePath(providerId)));

    /// <summary>Deletes every named provider's history file - never throws, same contract as every
    /// other write here. A provider with no file yet is simply skipped. Returns false when at least
    /// one file could not be deleted, so a caller never reports a deletion that did not happen.</summary>
    public bool DeleteAll(IEnumerable<string> providerIds)
    {
        var allDeleted = true;
        foreach (var providerId in providerIds)
        {
            Locked(providerId, () =>
            {
                try
                {
                    File.Delete(FilePath(providerId));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // best effort, same reasoning as AppendLine
                    allDeleted = false;
                }

                // The cached "newest point" must not keep answering for a file that no longer
                // exists - otherwise the very next Append would think a recent point is already on
                // disk and skip writing the fresh one. Every label variant for this provider goes,
                // not just the plain per-kind keys - the exact set of labels a provider used is not
                // known here.
                foreach (var key in _lastPoints.Keys.Where(key => key.ProviderId == providerId).ToList())
                    _lastPoints[key] = null;
                StateOf(providerId).Points = null;
            });
        }

        return allDeleted;
    }

    /// <summary>Total bytes across every named provider's history file, 0 for one that does not
    /// exist. Best-effort like every other read here: a file that becomes briefly unreadable (mid
    /// rewrite elsewhere) counts as 0 for that file rather than throwing. Virtual so a test can
    /// override it with a fault, the same seam <see cref="Prune"/> and <see cref="Compact"/> already
    /// give <see cref="ViewModels.SettingsViewModel"/>'s own background read a way to prove it
    /// survives a failing disk.</summary>
    public virtual long TotalHistoryBytes(IEnumerable<string> providerIds)
    {
        long total = 0;
        foreach (var providerId in providerIds)
        {
            try
            {
                var info = new FileInfo(FilePath(providerId));
                if (info.Exists)
                    total += info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort, same reasoning as AppendLine
            }
        }

        return total;
    }

    /// <summary>Rewrites the file without points older than <paramref name="retentionDays"/> (atomic, like SettingsStore).</summary>
    public virtual void Prune(string providerId, int retentionDays)
    {
        var path = FilePath(providerId);
        if (!File.Exists(path))
            return;

        Locked(providerId, () =>
        {
            InsideRewriteLockForTest?.Invoke(providerId);
            var cutoff = _now() - TimeSpan.FromDays(retentionDays);
            var (ok, points, hasFutureVersionLine) = ReadAllValid(path, providerId);
            // The file could not be read right now - never overwrite it with an empty history. A
            // line from a schema version newer than this build understands is refused the same way:
            // rewriting the file would silently drop whatever fields that newer version added.
            if (!ok || hasFutureVersionLine)
                return;

            var kept = points.Where(point => point.Timestamp >= cutoff).ToList();
            WriteAllAtomic(providerId, kept);
        });
    }

    /// <summary>
    /// Reduces raw points older than 30 days to one per hour and window, and points older than a
    /// year to one per day, always keeping the highest value so spikes are not lost. The two stages
    /// run one after the other over a file's lifetime: an hourly point that has since passed the
    /// one-year mark is rolled up to a daily one, otherwise the second stage would never reach
    /// anything and the file would grow without limit after the first year.
    /// </summary>
    public virtual void Compact(string providerId)
    {
        var path = FilePath(providerId);
        if (!File.Exists(path))
            return;

        Locked(providerId, () =>
        {
            InsideRewriteLockForTest?.Invoke(providerId);
            var now = _now();
            // Floored to whole UTC hours/days: an exact instant would split one bucket across two
            // runs and leave two rolled-up points for the same hour or day.
            var hourThreshold = FloorToUtcHour(now - HourCompactionAge);
            var dayThreshold = FloorToUtcDay(now - DayCompactionAge);

            var (ok, points, hasFutureVersionLine) = ReadAllValid(path, providerId);
            // The file could not be read right now - never overwrite it with an empty history. A
            // line from a schema version newer than this build understands is refused the same way:
            // rewriting the file would silently drop whatever fields that newer version added.
            if (!ok || hasFutureVersionLine)
                return;

            var kept = new List<HistoryPoint>();
            var toHourCompact = new List<HistoryPoint>();
            var toDayCompact = new List<HistoryPoint>();

            foreach (var point in points)
            {
                if (point.CompactionLevel == 2)
                    kept.Add(point); // already one point per day, there is nothing coarser
                else if (point.Timestamp < dayThreshold)
                    toDayCompact.Add(point); // raw or hourly, and past the one-year mark
                else if (point.CompactionLevel is not null)
                    kept.Add(point); // hourly and not old enough for the daily stage yet
                else if (point.Timestamp < hourThreshold)
                    toHourCompact.Add(point);
                else
                    kept.Add(point);
            }

            kept.AddRange(ReduceToPeak(toDayCompact, point => point.Timestamp.UtcDateTime.Date, compactionLevel: 2));
            kept.AddRange(ReduceToPeak(toHourCompact, UtcHourBucket, compactionLevel: 1));

            WriteAllAtomic(providerId, kept.OrderBy(point => point.Timestamp).ToList());
        });
    }

    /// <summary>
    /// The start of the UTC hour a point falls in. Reading the year/month/day/hour off the stored
    /// offset and labelling the result UTC named a different instant in every zone but UTC, and put
    /// two points an hour apart into one bucket when the clocks went back.
    /// </summary>
    private static DateTimeOffset FloorToUtcHour(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset FloorToUtcDay(DateTimeOffset value) =>
        new(value.UtcDateTime.Date, TimeSpan.Zero);

    private static DateTime UtcHourBucket(HistoryPoint point)
    {
        var utc = point.Timestamp.UtcDateTime;
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
    }

    private static IEnumerable<HistoryPoint> ReduceToPeak(
        List<HistoryPoint> points, Func<HistoryPoint, DateTime> bucketSelector, int compactionLevel) =>
        points
            // Label is part of the bucket key too - otherwise compacting a provider with more than
            // one Other series (Cursor's per-model bars, its Grok Bot bar) would pick a single
            // "peak" mixed across series that have nothing to do with each other.
            .GroupBy(point => (point.Window, Label: point.Label ?? "", Bucket: bucketSelector(point)))
            .Select(group => group.OrderByDescending(point => point.Percent).First() with
            {
                CompactionLevel = compactionLevel,
            });

    private HistoryPoint? LastPoint(string providerId, WindowKind window, string label)
    {
        var key = (providerId, window, label);
        if (_lastPoints.TryGetValue(key, out var cached))
            return cached;

        var point = FindLastPoint(providerId, window, label);
        _lastPoints[key] = point;
        return point;
    }

    private HistoryPoint? FindLastPoint(string providerId, WindowKind window, string label)
    {
        LastPointReadsForTest++;

        var path = FilePath(providerId);
        if (!File.Exists(path))
            return null;

        try
        {
            foreach (var line in ReverseLineReader.ReadLinesReversed(path))
            {
                var point = ParseLineOrNull(line, providerId);
                if (point is not null && point.Window == window && (point.Label ?? "") == label)
                    return point;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Returns Ok = false when the file could not be read at all, so callers never mistake
    /// "unreadable right now" for "empty" and overwrite real history with nothing. HasFutureVersionLine
    /// is true when at least one line named a schema version newer than <see cref="SchemaVersion"/> -
    /// counted separately from a corrupt line (see <see cref="ParseLineOrNull"/>) so a caller that must
    /// never rewrite such a file (<see cref="Compact"/>, <see cref="Prune"/>) can tell the two apart
    /// from a line that is simply garbage.</summary>
    private static (bool Ok, List<HistoryPoint> Points, bool HasFutureVersionLine) ReadAllValid(string path, string providerId)
    {
        try
        {
            var points = new List<HistoryPoint>();
            var hasFutureVersionLine = false;
            foreach (var line in File.ReadLines(path))
            {
                var (point, isFutureVersion) = ParseLine(line, providerId);
                if (isFutureVersion)
                    hasFutureVersionLine = true;
                else if (point is not null)
                    points.Add(point);
            }

            return (true, points, hasFutureVersionLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, [], false);
        }
    }

    /// <summary>Null for a blank line, unparseable JSON, or a point whose own schema version is newer
    /// than <see cref="SchemaVersion"/> - the last case is a point this build does not fully
    /// understand, so it is skipped rather than trusted, and (see <see cref="ReadAllValid"/>) never
    /// rewritten back to disk missing whatever a newer build added to it.</summary>
    private static HistoryPoint? ParseLineOrNull(string line, string providerId) => ParseLine(line, providerId).Point;

    private static (HistoryPoint? Point, bool IsFutureVersion) ParseLine(string line, string providerId)
    {
        if (string.IsNullOrWhiteSpace(line))
            return (null, false);

        try
        {
            var point = JsonSerializer.Deserialize<HistoryPoint>(line);
            if (point is not null && point.Version > SchemaVersion)
                return (null, true);
            // Valid JSON without a version or a timestamp ("{}") is not a point: its MinValue
            // timestamp would end a backward scan early and hide every older point.
            if (point is not null && (point.Version <= 0 || point.Timestamp == default))
                return (null, false);
            // A label stored under an older spelling reads under its current one, so the chart keeps
            // the points recorded before the rename (and the next rewrite stores them that way).
            if (point?.Label is { } label && LegacyWindowLabels.Migrate(providerId, label) is var current && current != label)
                point = point with { Label = current };
            return (point, false);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    private void AppendLine(string providerId, HistoryPoint point)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var json = JsonSerializer.Serialize(point) + "\n";
            var path = FilePath(providerId);
            // A crash mid-append can leave a last line without its newline; gluing the next point
            // onto it would lose both as one corrupt line.
            if (EndsWithoutNewline(path))
                json = "\n" + json;
            File.AppendAllText(path, json, Utf8NoBom);
            _lastPoints[(providerId, point.Window, point.Label ?? "")] = point;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a missed history point is never worth crashing over
        }
    }

    private static bool EndsWithoutNewline(string path)
    {
        if (!File.Exists(path))
            return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
            return false;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    private void WriteAllAtomic(string providerId, IEnumerable<HistoryPoint> points)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var written = points.ToList();
            var builder = new StringBuilder();
            foreach (var point in written)
                builder.Append(JsonSerializer.Serialize(point)).Append('\n');

            var path = FilePath(providerId);
            // Carries this process's id, same reasoning as SettingsStore.SaveNow's own temp file:
            // two simultaneous copies (--new-instance) must never write through the same temp file,
            // and a copy left behind by a process killed before the move below is swept up by
            // AppPaths.CleanUpLeftoverTempFiles at the next startup.
            var tempPath = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllBytes(tempPath, Utf8NoBom.GetBytes(builder.ToString()));
            File.Move(tempPath, path, overwrite: true);
            StateOf(providerId).Points = null;
            RememberNewestOf(providerId, written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort, same reasoning as AppendLine
        }
    }

    /// <summary>
    /// A rewrite can drop or replace the newest point of any window, so the cache is rebuilt from
    /// what was actually written rather than invalidated - a pruned-empty file then answers "none"
    /// without another read.
    /// </summary>
    private void RememberNewestOf(string providerId, List<HistoryPoint> written)
    {
        foreach (var key in _lastPoints.Keys.Where(key => key.ProviderId == providerId).ToList())
            _lastPoints[key] = null;

        foreach (var point in written)
        {
            var key = (providerId, point.Window, point.Label ?? "");
            if (!_lastPoints.TryGetValue(key, out var newest) || newest is null || point.Timestamp >= newest.Timestamp)
                _lastPoints[key] = point;
        }
    }
}
