using System.Globalization;
using System.Text;
using System.Text.Json;
using AiUsage.Io;
using AiUsage.Models;

namespace AiUsage.Services;

/// <summary>One provider tile as the status file shows it. <see cref="Account"/> is only ever the
/// person's own name for the account, never the label the provider reported.</summary>
public sealed record StatusEntry(
    string Id, string Name, string? Account, ProviderStatus Status, IReadOnlyList<UsageWindow> Windows);

/// <summary>
/// The small <c>status.json</c> other programs read (a terminal prompt, a status line): one entry per
/// provider tile with its state and usage windows. This type builds the document, writes it
/// atomically so a reader never sees half a file, and debounces the writes that follow a burst of
/// fetches into one.
/// </summary>
public sealed class StatusFileWriter : IDisposable
{
    public const string FileName = "status.json";

    /// <summary>Bumped only when a field changes meaning or goes away; added fields keep it.</summary>
    public const int FormatVersion = 1;

    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    private readonly Func<string> _pathProvider;
    private readonly Action<string>? _log;
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();
    private readonly Timer _timer;
    private string? _pending;
    private bool _disposed;

    /// <param name="pathProvider">Asked at every write, so a moved data folder is followed without a restart.</param>
    public StatusFileWriter(Func<string> pathProvider, TimeSpan? debounce = null, Action<string>? log = null)
    {
        _pathProvider = pathProvider;
        _debounce = debounce ?? DefaultDebounce;
        _log = log;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The path of the status file inside <paramref name="dataDirectory"/>.</summary>
    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>Remembers <paramref name="json"/> as the newest document and writes it once the
    /// debounce time has passed without a newer one.</summary>
    public void Schedule(string json)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _pending = json;
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes the pending document now, if there is one.</summary>
    public void Flush()
    {
        string? json;
        lock (_gate)
        {
            json = _pending;
            _pending = null;
        }

        if (json is not null)
            TryWrite(_pathProvider(), json, _log);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        Flush();
        _timer.Dispose();
    }

    /// <summary>The word a provider state is published as. Sign-in states fold into one word, and the
    /// three "nothing to read here" states into another, since a reader only needs to know whether the
    /// numbers can be trusted and what to do about it.</summary>
    public static string StatusWord(ProviderStatus status) => status switch
    {
        ProviderStatus.Ok => "ok",
        ProviderStatus.Stale => "stale",
        ProviderStatus.Failed => "failed",
        ProviderStatus.NotSignedIn or ProviderStatus.Blocked => "signin",
        _ => "unavailable",
    };

    public static string KindWord(WindowKind kind) => kind switch
    {
        WindowKind.FiveHour => "fiveHour",
        WindowKind.Weekly => "weekly",
        _ => "other",
    };

    /// <summary>A local timestamp with its offset, e.g. <c>2026-10-10T14:03:09+02:00</c>.</summary>
    public static string FormatTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public static string BuildJson(IEnumerable<StatusEntry> entries, DateTimeOffset now)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("version", FormatVersion);
            json.WriteString("updated", FormatTime(now));
            json.WriteStartArray("providers");
            foreach (var entry in entries)
            {
                json.WriteStartObject();
                json.WriteString("id", entry.Id);
                json.WriteString("name", entry.Name);
                if (string.IsNullOrEmpty(entry.Account))
                    json.WriteNull("account");
                else
                    json.WriteString("account", entry.Account);
                json.WriteString("status", StatusWord(entry.Status));
                json.WriteStartArray("windows");
                foreach (var window in entry.Windows)
                {
                    json.WriteStartObject();
                    json.WriteString("kind", KindWord(window.Kind));
                    json.WriteString("label", StatusTextMap.Resolve(window.Label));
                    json.WriteNumber("usedPercent", StatusTextMap.UsagePercent(window.UsedPercent));
                    if (window.ResetsAt is { } resetsAt)
                        json.WriteString("resetsAt", FormatTime(resetsAt));
                    else
                        json.WriteNull("resetsAt");
                    if (window.Tokens is { } tokens)
                        json.WriteNumber("tokens", tokens.TotalTokens);
                    else
                        json.WriteNull("tokens");
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return AppEncoding.Utf8NoBom.GetString(stream.ToArray());
    }

    /// <summary>Writes <paramref name="json"/> to a temp file beside <paramref name="path"/> and moves
    /// it into place, so a program reading the file sees the old document or the new one, never a
    /// part of either.</summary>
    internal static void WriteAtomic(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json, AppEncoding.Utf8NoBom);
            MoveIntoPlace(tempPath, path);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next start's cleanup of the app's own temp files.
            }

            throw;
        }
    }

    /// <summary>A program that is reading the file right now can make the swap fail for a few
    /// milliseconds (Windows refuses to replace a file that is open without delete sharing), so the
    /// swap is tried again briefly before giving up on this update.</summary>
    private static void MoveIntoPlace(string tempPath, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                Thread.Sleep(MoveRetryDelay);
            }
        }
    }

    private const int MoveAttempts = 20;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(10);

    private static void TryWrite(string path, string json, Action<string>? log)
    {
        try
        {
            WriteAtomic(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A reader holding the file open or a read-only folder only costs this one update.
            log?.Invoke($"Status file not written ({ex.GetType().Name}).");
        }
    }
}
