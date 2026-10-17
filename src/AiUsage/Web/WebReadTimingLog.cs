using System.Globalization;

namespace AiUsage.Web;

/// <summary>Decides which web reads get a timing line in the log: the first
/// <see cref="LoggedReadsPerProvider"/> reads of each provider in one app run, then one line saying
/// that further reads are no longer logged (so a quiet log is not mistaken for a stopped refresh).
/// The line names the provider id and numbers only, never an account name or an address.</summary>
internal sealed class WebReadTimingLog
{
    public const int LoggedReadsPerProvider = 10;

    private const long BytesPerMegabyte = 1024 * 1024;

    private readonly Dictionary<string, int> _reads = [];
    private readonly object _gate = new();

    /// <summary>The counter shared by every runner of the running app.</summary>
    public static WebReadTimingLog Shared { get; } = new();

    /// <summary>The log line for one finished read, the one-time stop notice after the last timing line,
    /// or null once the provider used up its lines.
    /// <paramref name="browserBytes"/> is the working set of the browser process; null leaves the
    /// browser part out.</summary>
    public string? NextLine(string providerId, long elapsedMilliseconds, long? browserBytes)
    {
        lock (_gate)
        {
            _reads.TryGetValue(providerId, out var seen);
            if (seen > LoggedReadsPerProvider)
                return null;
            _reads[providerId] = seen + 1;
            if (seen == LoggedReadsPerProvider)
                return $"web read {providerId}: the first {LoggedReadsPerProvider} reads are logged, later reads still run but are not logged";
        }

        var line = string.Create(CultureInfo.InvariantCulture, $"web read {providerId}: {elapsedMilliseconds} ms");
        if (browserBytes is { } bytes)
            line += string.Create(CultureInfo.InvariantCulture, $", browser {(bytes + BytesPerMegabyte / 2) / BytesPerMegabyte} MB");
        return line;
    }
}
