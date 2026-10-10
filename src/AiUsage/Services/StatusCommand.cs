using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using AiUsage.Io;

namespace AiUsage.Services;

/// <summary>
/// The <c>--status</c> switch: prints the last <c>status.json</c> as one line per provider and exits,
/// without a window and without the single-instance check, so it works while the widget runs and when
/// it does not. Its output is machine-facing and therefore English and fixed on purpose, like the
/// JSON keys it reads.
/// </summary>
public static class StatusCommand
{
    internal const string Switch = "--status";
    private const string JsonSwitch = "--json";

    internal const string NoStatusMessage = "AI-Usage has not written a status yet.";
    internal const string UnreadableMessage = "AI-Usage status file could not be read.";

    /// <summary>A file older than this gets its age appended, since the widget is probably closed.</summary>
    internal static readonly TimeSpan AgeNoteAfter = TimeSpan.FromMinutes(15);

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the command against the status file in <paramref name="dataDirectory"/> and
    /// returns the process exit code: 0 when a status was printed, 1 when there is none to print.</summary>
    public static int Run(IReadOnlyList<string> args, string dataDirectory, DateTimeOffset now, TextWriter output)
    {
        var path = StatusFileWriter.PathIn(dataDirectory);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            output.WriteLine(NoStatusMessage);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine(UnreadableMessage);
            return 1;
        }

        if (args.Any(arg => string.Equals(arg, JsonSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            output.WriteLine(text.TrimEnd());
            return 0;
        }

        var lines = FormatLines(text, now);
        if (lines is null)
        {
            output.WriteLine(UnreadableMessage);
            return 1;
        }

        foreach (var line in lines)
            output.WriteLine(line);
        return 0;
    }

    /// <summary>One line per provider, e.g. <c>Claude 42% 5h (2h 14m) · 63% week (3d 4h)</c>; null when
    /// the document is not a status file this version understands.</summary>
    internal static IReadOnlyList<string>? FormatLines(string json, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version) || version.GetInt32() != StatusFileWriter.FormatVersion
                || !root.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array)
                return null;

            var ageNote = "";
            if (root.TryGetProperty("updated", out var updated)
                && DateTimeOffset.TryParse(updated.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var writtenAt)
                && now - writtenAt > AgeNoteAfter)
                ageNote = $" · data {FormatSpan(now - writtenAt)} old";

            var lines = new List<string>();
            foreach (var provider in providers.EnumerateArray())
                lines.Add(FormatProvider(provider, now) + ageNote);
            return lines;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string FormatProvider(JsonElement provider, DateTimeOffset now)
    {
        var name = Text(provider, "name");
        if (Text(provider, "account") is { Length: > 0 } account)
            name = $"{name} ({account})";

        var segments = new List<string>();
        if (provider.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Array)
        {
            foreach (var window in windows.EnumerateArray())
            {
                var label = Text(window, "kind") switch
                {
                    "fiveHour" => "5h",
                    "weekly" => "week",
                    _ => Text(window, "label"),
                };
                var segment = $"{window.GetProperty("usedPercent").GetInt32()}% {label}";
                if (Text(window, "resetsAt") is { Length: > 0 } resetsAt
                    && DateTimeOffset.TryParse(resetsAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var reset)
                    && reset > now)
                    segment += $" ({FormatSpan(reset - now)})";
                segments.Add(segment);
            }
        }

        var status = Text(provider, "status");
        if (segments.Count == 0)
            return $"{name}: {StatusPhrase(status)}";

        var line = $"{name} {string.Join(" · ", segments)}";
        return status == "ok" ? line : $"{line} [{StatusPhrase(status)}]";
    }

    private static string StatusPhrase(string status) => status switch
    {
        "ok" => "no data",
        "stale" => "stale",
        "failed" => "failed",
        "signin" => "sign in needed",
        _ => "unavailable",
    };

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>"3d 4h", "2h 14m", "9m"; the smaller unit is left out when it is zero.</summary>
    internal static string FormatSpan(TimeSpan span)
    {
        if (span.TotalMinutes < 1)
            return "<1m";
        if (span.TotalDays >= 1)
            return span.Hours == 0 ? $"{(int)span.TotalDays}d" : $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1)
            return span.Minutes == 0 ? $"{span.Hours}h" : $"{span.Hours}h {span.Minutes}m";
        return $"{span.Minutes}m";
    }

    /// <summary>The writer the real process prints through. A GUI-subsystem program has no standard
    /// output of its own unless its starter redirected it, so a pipe or file is used as handed over,
    /// and an interactive start attaches to the console it was started from.</summary>
    public static TextWriter OpenOutput()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (IsUsable(handle))
            return IsConsole(handle) ? new ConsoleWriter(handle) : PipeWriter(handle);

        if (AttachConsole(AttachParentProcess))
        {
            var console = CreateFile("CONOUT$", GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (IsUsable(console))
                return new ConsoleWriter(console);
        }

        return TextWriter.Null;
    }

    private static StreamWriter PipeWriter(IntPtr handle) => new(
        new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Write, 1, isAsync: false),
        AppEncoding.Utf8NoBom) { AutoFlush = true };

    private static bool IsUsable(IntPtr handle) => handle != IntPtr.Zero && handle != InvalidHandle;

    private static bool IsConsole(IntPtr handle) => GetFileType(handle) == FileTypeChar;

    /// <summary>Writes through <c>WriteConsoleW</c>, so characters outside the console's code page
    /// (the dot between two windows) arrive intact.</summary>
    private sealed class ConsoleWriter(IntPtr handle) : TextWriter
    {
        public override Encoding Encoding => Encoding.Unicode;

        public override void Write(char value) => Write(value.ToString());

        public override void Write(string? value)
        {
            if (!string.IsNullOrEmpty(value))
                WriteConsole(handle, value, value.Length, out _, IntPtr.Zero);
        }
    }

    private const int StdOutputHandle = -11;
    private const int AttachParentProcess = -1;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FileTypeChar = 2;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteConsole(IntPtr handle, string buffer, int length, out int written, IntPtr reserved);
}
