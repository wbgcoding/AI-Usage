using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AiUsage.Stats;

/// <summary>
/// Reads the token counts of the Antigravity command line tool. Each conversation is one SQLite file
/// (<c>conversations/&lt;id&gt;.db</c>) whose <c>gen_metadata</c> table holds one protobuf message per
/// model call. Only numeric fields, the model name, the step reference and the workspace folder are
/// ever decoded; every other field (which is where prompts and answers live) is skipped by length
/// without being read, and the files are opened read-only, so the tool's own data is never changed.
///
/// Field map, inside the message in field 1 of a call: field 4 holds the usage (2 = prompt tokens not
/// served from cache, 5 = prompt tokens from cache, 3 = output tokens, which is thinking (9) plus the
/// visible answer (10)); field 19 is the model name; field 20 repeats key and value strings, among
/// them "last_step_index", the step whose metadata field 1 is the call's Unix time.
/// </summary>
public static class AntigravityUsageLogParser
{
    /// <summary>Largest call blob, and largest metadata blob, that is read at all. A bigger one is a
    /// damaged or foreign file and counts as a skipped row; the size test runs inside SQLite, so the
    /// blob is never loaded.</summary>
    private const int MaxCallBlobBytes = 4 * 1024 * 1024;
    private const int MaxMetadataBlobBytes = 1024 * 1024;
    private const int MaxModelBytes = 128;
    private const int MaxProjectBytes = 4096;

    /// <summary>One read takes at most this many rows and this much time; the rest follows on the next
    /// walk, so a huge or hostile file never holds the indexer.</summary>
    internal const int MaxRowsPerRead = 5000;
    private const int MaxStepRows = 100_000;
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(10);

    /// <summary>A real count is far below this; anything above is a corrupt row, not usage.</summary>
    private const long MaxTokenCount = 1_000_000_000;
    private const long MaxRowIndex = 1_000_000_000_000;

    /// <summary>How long after its last write a conversation may still be waiting for a step's time
    /// before the call is dated at the conversation's start instead.</summary>
    private static readonly TimeSpan StepGrace = TimeSpan.FromMinutes(10);

    /// <summary>The numbers of one model call. <see cref="StepIndex"/> is -1 when the call names no step.</summary>
    public readonly record struct Generation(string Model, long InputTokens, long CacheReadTokens, long OutputTokens, long StepIndex);

    /// <summary>One model call as the index stores it.</summary>
    public readonly record struct UsageEvent(
        long Index, DateTimeOffset Timestamp, string Model, long InputTokens, long OutputTokens, long CacheReadTokens);

    /// <summary>What one conversation file yielded: the new calls, where to resume, the folder the
    /// conversation ran in, and how many rows could not be used. <paramref name="Incomplete"/> is set
    /// when the read stopped early (row or time limit, or a call still waiting for its step), so the
    /// file has to be looked at again even when it does not change.</summary>
    public sealed record ReadResult(
        IReadOnlyList<UsageEvent> Events, long NextIndex, string Project, int RowsSkipped, bool Incomplete = false);

    /// <summary>Decodes one <c>gen_metadata</c> blob. False for a call that carries no model or no
    /// token count (the tool writes a few such rows), or for a blob that is not valid protobuf.</summary>
    public static bool TryParseGeneration(ReadOnlySpan<byte> blob, out Generation generation)
    {
        generation = default;
        try
        {
            var call = ProtoReader.Find(blob, 1);
            if (call.IsEmpty)
                return false;

            var usage = default(ReadOnlySpan<byte>);
            var modelBytes = default(ReadOnlySpan<byte>);
            long step = -1;
            var reader = new ProtoReader(call);
            while (reader.Next())
            {
                switch (reader.Field)
                {
                    case 4 when reader.IsBytes:
                        usage = reader.Bytes;
                        break;
                    case 19 when reader.IsBytes:
                        modelBytes = reader.Bytes;
                        break;
                    case 20 when reader.IsBytes && TryReadStep(reader.Bytes, out var parsed):
                        step = parsed;
                        break;
                }
            }

            if (modelBytes.IsEmpty || modelBytes.Length > MaxModelBytes || usage.IsEmpty)
                return false;

            long input = 0, cacheRead = 0, output = 0;
            var usageReader = new ProtoReader(usage);
            while (usageReader.Next())
            {
                if (!usageReader.IsVarint)
                    continue;

                if (usageReader.Value > MaxTokenCount)
                    return false;

                var value = (long)usageReader.Value;
                switch (usageReader.Field)
                {
                    case 2:
                        input = value;
                        break;
                    case 3:
                        output = value;
                        break;
                    case 5:
                        cacheRead = value;
                        break;
                }
            }

            if (input + cacheRead + output == 0)
                return false;

            // The model name is a short printable label; anything else is not a call this reader knows.
            foreach (var character in modelBytes)
            {
                if (character is < 0x20 or > 0x7e)
                    return false;
            }

            var model = Encoding.ASCII.GetString(modelBytes);
            generation = new Generation(model, input, cacheRead, output, step);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The Unix time in a step's metadata blob (field 1, whose field 1 is the seconds).</summary>
    public static DateTimeOffset? TryParseStepTime(ReadOnlySpan<byte> metadata) => TryParseTimestamp(ProtoReader.Find(metadata, 1));

    /// <summary>The conversation's start in the trajectory metadata (field 2, whose field 1 is the seconds).</summary>
    public static DateTimeOffset? TryParseConversationStart(ReadOnlySpan<byte> metadata) => TryParseTimestamp(ProtoReader.Find(metadata, 2));

    /// <summary>The folder the conversation ran in: the first <c>file:///</c> string in field 1 of the
    /// trajectory metadata, as a local path; empty when there is none.</summary>
    public static string TryParseProject(ReadOnlySpan<byte> metadata)
    {
        try
        {
            var reader = new ProtoReader(metadata);
            while (reader.Next())
            {
                if (reader.Field != 1 || !reader.IsBytes)
                    continue;

                var inner = new ProtoReader(reader.Bytes);
                while (inner.Next())
                {
                    if (inner.Field != 1 || !inner.IsBytes)
                        continue;

                    if (inner.Bytes.Length > MaxProjectBytes || !inner.Bytes.StartsWith("file:///"u8))
                        continue;

                    if (Uri.TryCreate(Encoding.UTF8.GetString(inner.Bytes), UriKind.Absolute, out var uri) && uri.IsFile)
                        return uri.LocalPath;
                }

                return "";
            }
        }
        catch (FormatException)
        {
        }

        return "";
    }

    /// <summary>Reads the model calls of the conversation file at <paramref name="path"/> whose row
    /// index is at least <paramref name="fromIndex"/>, at most <see cref="MaxRowsPerRead"/> rows per
    /// call. The file is opened read-only without a pooled connection and with a one second lock wait,
    /// so the tool that owns it is never blocked or changed (a read-only connection to a write-ahead
    /// database may still create the empty <c>-wal</c> and <c>-shm</c> files beside it). A file that is
    /// locked or unreadable throws <see cref="SqliteException"/>; one whose call table is not a plain
    /// table throws <see cref="InvalidDataException"/>. <paramref name="utcNow"/> only matters for the
    /// grace a call gets to receive its step time.</summary>
    public static ReadResult ReadConversation(
        string path, long fromIndex, DateTime fileWriteTimeUtc, DateTime? utcNow = null, CancellationToken cancellationToken = default)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        var clock = Stopwatch.StartNew();

        // A view or virtual table could generate rows without end; only plain tables are read.
        if (!IsPlainTable(connection, "gen_metadata"))
            throw new InvalidDataException("The call table is not a plain table.");

        var calls = new List<(long Index, Generation Generation)>();
        var skipped = 0;
        var rowsSeen = 0;
        var lastRowIndex = fromIndex - 1;
        var incomplete = false;
        using (var command = connection.CreateCommand())
        {
            // The size test is part of the query: an oversized or non-blob value comes back as NULL
            // without its bytes being loaded.
            command.CommandText =
                "SELECT idx, CASE WHEN typeof(data) = 'blob' AND length(data) <= $max THEN data END " +
                "FROM gen_metadata WHERE typeof(idx) = 'integer' AND idx >= $from ORDER BY idx LIMIT $cap";
            command.Parameters.AddWithValue("$max", MaxCallBlobBytes);
            command.Parameters.AddWithValue("$from", Math.Max(fromIndex, 0));
            command.Parameters.AddWithValue("$cap", MaxRowsPerRead);
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed > ReadBudget)
                {
                    incomplete = true;
                    break;
                }

                rowsSeen++;
                if (rows.IsDBNull(0))
                {
                    skipped++;
                    continue;
                }

                var index = rows.GetInt64(0);
                if (index < 0 || index > MaxRowIndex)
                {
                    skipped++;
                    continue;
                }

                lastRowIndex = index;
                if (!rows.IsDBNull(1) && TryParseGeneration((byte[])rows.GetValue(1), out var generation))
                    calls.Add((index, generation));
                else
                    skipped++;
            }
        }

        if (rowsSeen >= MaxRowsPerRead)
            incomplete = true;

        var capped = incomplete;
        if (calls.Count == 0)
        {
            // A capped read made progress even when every row in it was unusable.
            var next = capped ? Math.Max(fromIndex, lastRowIndex + 1) : fromIndex;
            return new ReadResult([], next, "", skipped, incomplete);
        }

        DateTimeOffset? start = null;
        var project = "";
        if (IsPlainTable(connection, "trajectory_metadata_blob"))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT CASE WHEN typeof(data) = 'blob' AND length(data) <= $max THEN data END FROM trajectory_metadata_blob LIMIT 1";
            command.Parameters.AddWithValue("$max", MaxMetadataBlobBytes);
            if (command.ExecuteScalar() is byte[] blob)
            {
                start = TryParseConversationStart(blob);
                project = TryParseProject(blob);
            }
        }

        var stepTimes = IsPlainTable(connection, "steps")
            ? LoadStepTimes(connection, calls, clock, cancellationToken)
            : [];
        var settled = (utcNow ?? DateTime.UtcNow) - fileWriteTimeUtc > StepGrace;
        var fallback = start ?? new DateTimeOffset(DateTime.SpecifyKind(fileWriteTimeUtc, DateTimeKind.Utc));
        var events = new List<UsageEvent>(calls.Count);
        var deferredAt = -1L;
        foreach (var (index, generation) in calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var moment = fallback;
            if (generation.StepIndex >= 0 && stepTimes.TryGetValue(generation.StepIndex, out var stepTime))
            {
                moment = stepTime;
            }
            else if (generation.StepIndex >= 0 && !settled)
            {
                // The step may simply not be written yet: stop before this call and try again later.
                deferredAt = index;
                incomplete = true;
                break;
            }

            events.Add(new UsageEvent(
                index, moment, generation.Model, generation.InputTokens, generation.OutputTokens, generation.CacheReadTokens));
        }

        // Rows after the last counted call that carry no usage yet may be filled in later, so the
        // resume point only moves past them when the read was capped (to guarantee progress).
        long nextIndex;
        if (deferredAt >= 0)
            nextIndex = deferredAt;
        else if (capped)
            nextIndex = lastRowIndex + 1;
        else
            nextIndex = calls[^1].Index + 1;
        return new ReadResult(events, nextIndex, project, skipped, incomplete);
    }

    private static bool IsPlainTable(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM sqlite_master WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is string type && type == "table";
    }

    /// <summary>The Unix time of every step the given calls refer to, from one range query. Only the
    /// needed steps are decoded, and an oversized metadata blob is never loaded.</summary>
    private static Dictionary<long, DateTimeOffset> LoadStepTimes(
        SqliteConnection connection, List<(long Index, Generation Generation)> calls, Stopwatch clock, CancellationToken cancellationToken)
    {
        var needed = new HashSet<long>();
        foreach (var (_, generation) in calls)
        {
            if (generation.StepIndex >= 0)
                needed.Add(generation.StepIndex);
        }

        var times = new Dictionary<long, DateTimeOffset>();
        if (needed.Count == 0)
            return times;

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT idx, CASE WHEN typeof(metadata) = 'blob' AND length(metadata) <= $max THEN metadata END " +
            "FROM steps WHERE idx >= $low AND idx <= $high LIMIT $cap";
        command.Parameters.AddWithValue("$max", MaxMetadataBlobBytes);
        command.Parameters.AddWithValue("$low", needed.Min());
        command.Parameters.AddWithValue("$high", needed.Max());
        command.Parameters.AddWithValue("$cap", MaxStepRows);
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > ReadBudget)
                break;
            if (rows.IsDBNull(0) || rows.IsDBNull(1))
                continue;

            var index = rows.GetInt64(0);
            if (needed.Contains(index) && TryParseStepTime((byte[])rows.GetValue(1)) is { } time)
                times[index] = time;
        }

        return times;
    }

    private static bool TryReadStep(ReadOnlySpan<byte> entry, out long step)
    {
        step = -1;
        var isStepKey = false;
        var value = default(ReadOnlySpan<byte>);
        var reader = new ProtoReader(entry);
        while (reader.Next())
        {
            if (reader.Field == 1 && reader.IsBytes)
                isStepKey = reader.Bytes.SequenceEqual("last_step_index"u8);
            else if (reader.Field == 2 && reader.IsBytes)
                value = reader.Bytes;
        }

        return isStepKey && long.TryParse(Encoding.UTF8.GetString(value), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out step);
    }

    private static DateTimeOffset? TryParseTimestamp(ReadOnlySpan<byte> message)
    {
        try
        {
            if (message.IsEmpty)
                return null;

            var reader = new ProtoReader(message);
            while (reader.Next())
            {
                // Seconds since 1970; anything outside 2001 to 2100 is not a time.
                if (reader.Field == 1 && reader.IsVarint && reader.Value is > 1_000_000_000 and < 4_100_000_000)
                    return DateTimeOffset.FromUnixTimeSeconds((long)reader.Value);
            }
        }
        catch (FormatException)
        {
        }

        return null;
    }

    /// <summary>Walks the fields of one protobuf message. Length-delimited fields are exposed as a
    /// slice of the original bytes and never copied or decoded unless the caller asks.</summary>
    private ref struct ProtoReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public ProtoReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
            Field = 0;
            Wire = 0;
            Value = 0;
            Bytes = default;
        }

        public int Field { get; private set; }

        public int Wire { get; private set; }

        public ulong Value { get; private set; }

        public ReadOnlySpan<byte> Bytes { get; private set; }

        public readonly bool IsVarint => Wire == 0;

        public readonly bool IsBytes => Wire == 2;

        /// <summary>The first length-delimited field <paramref name="field"/> of <paramref name="message"/>;
        /// empty when there is none or the message is malformed.</summary>
        public static ReadOnlySpan<byte> Find(ReadOnlySpan<byte> message, int field)
        {
            try
            {
                var reader = new ProtoReader(message);
                while (reader.Next())
                {
                    if (reader.Field == field && reader.IsBytes)
                        return reader.Bytes;
                }
            }
            catch (FormatException)
            {
            }

            return default;
        }

        /// <summary>Moves to the next field; false at the end. Throws <see cref="FormatException"/> on a
        /// truncated or unsupported encoding.</summary>
        public bool Next()
        {
            if (_position >= _data.Length)
                return false;

            var key = ReadVarint();
            Field = (int)(key >> 3);
            Wire = (int)(key & 7);
            Value = 0;
            Bytes = default;
            switch (Wire)
            {
                case 0:
                    Value = ReadVarint();
                    break;
                case 1:
                    Skip(8);
                    break;
                case 2:
                    var length = ReadVarint();
                    if (length > (ulong)(_data.Length - _position))
                        throw new FormatException("Length runs past the end of the message.");
                    Bytes = _data.Slice(_position, (int)length);
                    _position += (int)length;
                    break;
                case 5:
                    Skip(4);
                    break;
                default:
                    throw new FormatException("Unsupported wire type.");
            }

            return true;
        }

        private void Skip(int count)
        {
            if (count > _data.Length - _position)
                throw new FormatException("Fixed field runs past the end of the message.");
            _position += count;
        }

        private ulong ReadVarint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (_position >= _data.Length)
                    throw new FormatException("Varint runs past the end of the message.");
                var current = _data[_position++];
                result |= (ulong)(current & 0x7f) << shift;
                if ((current & 0x80) == 0)
                    return result;
            }

            throw new FormatException("Varint is too long.");
        }
    }
}
