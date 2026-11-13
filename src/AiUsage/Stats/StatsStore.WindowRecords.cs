using Microsoft.Data.Sqlite;

namespace AiUsage.Stats;

public sealed partial class StatsStore
{
    /// <summary>The same slice as <see cref="SumTokensSince"/> (a provider, everything from the local
    /// hour of <paramref name="localStart"/> on) as priced records: one per model with the four token
    /// kinds summed, the shape the cost estimate takes. Models without tokens are left out. Null when
    /// the index could not be read, which is not the same as no usage.</summary>
    public IReadOnlyList<StatsRecord>? WindowRecordsSince(string providerId, DateTime localStart)
    {
        try
        {
            using var connection = Open();
            if (!IsUsable)
                return null;

            using var command = connection.CreateCommand();
            // The interpolated part is the fixed filter text of WindowSliceFilter; every value in it is a bound parameter.
            command.CommandText = $"""
                SELECT model, SUM(input_tokens), SUM(output_tokens), SUM(cache_creation_tokens), SUM(cache_read_tokens)
                FROM usage WHERE {WindowSliceFilter(command, providerId, localStart, null)} GROUP BY model
                """;

            var day = DateOnly.FromDateTime(localStart);
            var rows = new List<StatsRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var record = new StatsRecord(
                    providerId, day, reader.GetString(0), "",
                    reader.IsDBNull(1) ? 0 : reader.GetInt64(1), reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3), reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    localStart.Hour);
                if (record.TotalTokens > 0)
                    rows.Add(record);
            }

            return rows;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return null;
        }
    }
}
