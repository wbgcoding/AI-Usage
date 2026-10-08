using System.Text;
using AiUsage.Storage;

namespace AiUsage.Services;

/// <summary>
/// Rolling plain-text log: UTF-8 without a byte-order mark, capped at a
/// fixed number of fixed-size files so it never grows without bound. Never throws - a logging
/// failure must not crash the app it exists to diagnose. Every line passes through
/// <see cref="PathSanitizer"/> once before it is written, so a user path never reaches the file;
/// callers still never pass a credential.
/// </summary>
public sealed class LogService
{
    private readonly Func<string> _directory;
    private readonly string _stem;
    private readonly string _extension;
    private readonly long _maxFileSizeBytes;
    private readonly int _maxFiles;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    /// <summary>The one log every part of the app writes to: a single lock over a single app.log, so
    /// concurrent writers queue instead of failing on each other's file handle. The folder is looked
    /// up on every write, so a moved or overridden data folder applies at once.</summary>
    public static LogService Shared { get; } = new(() => AppPaths.LogsDirectory);

    /// <summary>One line into the log of <paramref name="directory"/>, for the rare moment before the
    /// data folder itself is known and <see cref="Shared"/> cannot be asked yet.</summary>
    internal static void AppendTo(string directory, string message) => new LogService(directory).LogInfo(message);

    public LogService(string directory, long maxFileSizeBytes = 1_000_000, int maxFiles = 5, Func<DateTimeOffset>? now = null, string fileName = "app.log")
        : this(() => directory, maxFileSizeBytes, maxFiles, now, fileName)
    {
    }

    private LogService(Func<string> directory, long maxFileSizeBytes = 1_000_000, int maxFiles = 5, Func<DateTimeOffset>? now = null, string fileName = "app.log")
    {
        _directory = directory;
        _stem = Path.GetFileNameWithoutExtension(fileName);
        _extension = Path.GetExtension(fileName);
        _maxFileSizeBytes = maxFileSizeBytes;
        _maxFiles = maxFiles;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string CurrentFile => PathFor(0);

    public void LogInfo(string message) => Log("INFO", message);

    public void LogError(string message) => Log("ERROR", message);

    public void Log(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory());
                var line = $"{_now():yyyy-MM-dd HH:mm:ss.fff} [{level}] {PathSanitizer.Sanitize(message)}{Environment.NewLine}";
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(line);

                var current = CurrentFile;
                var existingLength = File.Exists(current) ? new FileInfo(current).Length : 0;
                if (existingLength > 0 && existingLength + bytes.Length > _maxFileSizeBytes)
                    Rotate();

                using var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A broken log target must never take the rest of the app down with it.
        }
    }

    private string PathFor(int index) => index == 0
        ? Path.Combine(_directory(), _stem + _extension)
        : Path.Combine(_directory(), $"{_stem}.{index}{_extension}");

    private void Rotate()
    {
        var oldest = PathFor(_maxFiles - 1);
        if (File.Exists(oldest))
            File.Delete(oldest);

        for (var i = _maxFiles - 2; i >= 0; i--)
        {
            var source = PathFor(i);
            if (File.Exists(source))
                File.Move(source, PathFor(i + 1));
        }
    }
}
