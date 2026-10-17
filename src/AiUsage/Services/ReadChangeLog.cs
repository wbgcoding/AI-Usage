namespace AiUsage.Services;

/// <summary>
/// The one place a provider's read says "the answer no longer matches what I was written for": a
/// new model, a renamed or unknown field, an unknown window, a changed shape, a moved endpoint.
/// Each distinct note is written once per run as a WARN line that starts with <see cref="Prefix"/>,
/// so a later look into the log finds every needed adaptation with one search. Notes carry only ids,
/// key names, field names, window names and HTTP status codes, never a usage figure, a token, an
/// account detail, a path or any part of a response body.
/// </summary>
public static class ReadChangeLog
{
    /// <summary>Starts every change line in the log; a search for it finds them all.</summary>
    public const string Prefix = "CHANGE DETECTED";

    // Notes already written by this process: each change is logged once per run, not on every read.
    private static readonly HashSet<string> Written = [];

    // Test seam: a capture scope on the current async flow takes the lines instead of the log, with
    // its own once-only memory, so parallel tests neither see each other's lines nor touch the log.
    private static readonly AsyncLocal<Capture?> Current = new();

    /// <summary>Writes the WARN line for <paramref name="note"/> unless this provider already
    /// reported the very same note in this run.</summary>
    public static void Report(string providerName, string note)
    {
        var line = $"{Prefix} ({providerName} usage read needs adapting): {note}";
        if (Current.Value is { } capture)
        {
            capture.Add(line);
            return;
        }

        lock (Written)
        {
            if (!Written.Add(line))
                return;
        }
        LogService.Shared.Log("WARN", line);
    }

    /// <summary>Starts collecting this flow's lines instead of logging them; dispose to end it.</summary>
    internal static Capture BeginCapture()
    {
        var capture = new Capture();
        Current.Value = capture;
        return capture;
    }

    /// <summary>The lines one capture scope collected, each distinct line once like the real log.</summary>
    internal sealed class Capture : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        internal void Add(string line)
        {
            lock (_gate)
            {
                if (!_lines.Contains(line))
                    _lines.Add(line);
            }
        }

        public void Dispose() => Current.Value = null;
    }
}
