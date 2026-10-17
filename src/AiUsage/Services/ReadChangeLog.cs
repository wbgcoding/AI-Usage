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

    // Providers that already wrote a note of their own this run (see ReportGeneral).
    private static readonly HashSet<string> ProvidersWithNote = new(StringComparer.Ordinal);

    // Test seam: a capture scope on the current async flow takes the lines instead of the log, with
    // its own once-only memory, so parallel tests neither see each other's lines nor touch the log.
    private static readonly AsyncLocal<Capture?> Current = new();

    /// <summary>Writes the WARN line for <paramref name="note"/> unless this provider already
    /// reported the very same note in this run.</summary>
    public static void Report(string providerName, string note) => Write(providerName, note, general: false);

    /// <summary>Same line, for the checks that apply to every provider alike (a moved address, an
    /// answer with nothing to show, an answer nothing could be read from). They only say that
    /// something changed, so a provider that already named what changed in a note of its own this run
    /// is not told twice.</summary>
    public static void ReportGeneral(string providerName, string note) => Write(providerName, note, general: true);

    private const int MaxNameLength = 40;

    /// <summary>A name taken from a provider's answer (a key, a model, a window, a quota id), made safe
    /// for a log line: <c>{id}</c> for anything that looks like an account or organisation id or an
    /// email address, every character outside letters, digits and a few separators replaced, and cut
    /// to a short length. The answer itself is foreign text; only its names may ever be logged.</summary>
    public static string SafeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "(none)";
        if (name.Contains('@') || Guid.TryParse(name, out _)
            || (name.Length >= 16 && !name.Contains('-') && name.Any(char.IsAsciiDigit)
                && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
            return "{id}";

        var shortened = name.Length > MaxNameLength ? name[..MaxNameLength] : name;
        return string.Create(shortened.Length, shortened, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = char.IsAsciiLetterOrDigit(source[i]) || source[i] is '_' or '-' or '.' or ' ' or ':' or '/' ? source[i] : '?';
        });
    }

    /// <summary>The names of the properties of a JSON object as a short, comma-joined list for a
    /// note (at most <paramref name="max"/> of them), each passed through <see cref="SafeName"/>.</summary>
    public static string SafeNames(IEnumerable<string> names, int max = 8)
    {
        var list = names.Take(max + 1).Select(SafeName).ToList();
        return list.Count == 0 ? "none" : list.Count > max ? string.Join(", ", list.Take(max)) + ", ..." : string.Join(", ", list);
    }

    private static void Write(string providerName, string note, bool general)
    {
        var line = $"{Prefix} ({providerName} usage read needs adapting): {note}";
        if (Current.Value is { } capture)
        {
            capture.Add(providerName, line, general);
            return;
        }

        lock (Written)
        {
            if (general && ProvidersWithNote.Contains(providerName))
                return;
            if (!Written.Add(line))
                return;
            if (!general)
                ProvidersWithNote.Add(providerName);
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
        private readonly HashSet<string> _providersWithNote = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        internal void Add(string providerName, string line, bool general)
        {
            lock (_gate)
            {
                if (general && _providersWithNote.Contains(providerName))
                    return;
                if (_lines.Contains(line))
                    return;
                _lines.Add(line);
                if (!general)
                    _providersWithNote.Add(providerName);
            }
        }

        public void Dispose() => Current.Value = null;
    }
}
