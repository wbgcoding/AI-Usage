using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AiUsage.Models;
using AiUsage.Services;
using AiUsage.ViewModels;

namespace AiUsage.Storage;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> under a data directory. Never lets
/// a persistence failure reach the caller as an exception - the worst case is running on defaults.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    /// <summary>Internal, not private: <see cref="ViewModels.SettingsViewModel"/>'s own settings
    /// export writes through exactly these same options, so an exported file round-trips through
    /// <see cref="Validate"/> the same way the primary file does.</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultFailedSaveRetryDelay = TimeSpan.FromSeconds(5);
    private const int MaxFlushAttempts = 3;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private string _dataDirectory;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _failedSaveRetryDelay;
    private readonly ITimer _debounceTimer;
    private readonly LogService? _logService;
    private bool _backupFailureLogged;

    // Every path that touches the pending save or the file itself holds this. The UI thread calls
    // RequestSave and SaveNow while the debounce timer fires FlushPendingSave on a pool thread;
    // without the gate two writers can be inside the same settings.json.tmp at once, and the loser
    // of that race silently drops a setting.
    private readonly object _saveGate = new();
    private AppSettings? _pendingSave;

    // How many times FlushPendingSave has already tried (and failed) to save the current
    // _pendingSave snapshot - reset whenever a fresh RequestSave replaces it, or once a save finally
    // lands. See FlushPendingSave for the retry/give-up policy this drives.
    private int _flushAttempts;

    /// <summary>Set by <see cref="Load"/> when the file came from a newer schema version - saving is
    /// refused for the rest of this session so an older build never destroys the newer file.</summary>
    private bool _readOnly;

    /// <param name="failedSaveRetryDelay">Test seam: a shorter delay so a test proving the
    /// retry-then-give-up path does not have to wait out the real 5 seconds between attempts.</param>
    /// <param name="timeProvider">Test seam: a fake clock so a test proving the debounce or retry
    /// path does not have to wait out the real delay either - see <see cref="Services.RefreshScheduler"/>
    /// for the same pattern.</param>
    public SettingsStore(string? dataDirectory = null, TimeSpan? debounceDelay = null, LogService? logService = null,
        TimeSpan? failedSaveRetryDelay = null, TimeProvider? timeProvider = null)
    {
        _dataDirectory = dataDirectory ?? AppPaths.DataDirectory;
        _debounceDelay = debounceDelay ?? DefaultDebounceDelay;
        _failedSaveRetryDelay = failedSaveRetryDelay ?? DefaultFailedSaveRetryDelay;
        _logService = logService;
        var clock = timeProvider ?? TimeProvider.System;
        _debounceTimer = clock.CreateTimer(_ => FlushPendingSave(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Internal, not private: <see cref="ViewModels.SettingsViewModel"/>'s own "change data
    /// folder" flow needs to know exactly where this store is reading and writing today, rather than
    /// re-deriving it from <see cref="AppPaths.DataDirectory"/> - the two are expected to agree in
    /// production, but only this store's own field is the one a test can seed independently.</summary>
    internal string DataDirectory => _dataDirectory;

    private string SettingsFilePath => Path.Combine(_dataDirectory, "settings.json");

    /// <summary>Called once a "move my data" operation has already copied <c>settings.json</c> and
    /// its backup to <paramref name="dataDirectory"/> - every save from this point on lands in the
    /// new location.</summary>
    internal void Redirect(string dataDirectory)
    {
        lock (_saveGate)
            _dataDirectory = dataDirectory;
    }

    /// <summary>The last-known-good copy, written just before every save overwrites the primary
    /// file - a valid file carrying a value nobody wanted still has somewhere to recover from,
    /// which "the primary parses fine" alone cannot catch.</summary>
    private string BackupFilePath => $"{SettingsFilePath}.bak";

    /// <summary>Set by the most recent <see cref="Load"/> when the file was newer than this app understands.</summary>
    public bool LastLoadWasFromNewerVersion { get; private set; }

    public AppSettings Load()
    {
        LastLoadWasFromNewerVersion = false;
        _readOnly = false;
        var path = SettingsFilePath;
        if (!File.Exists(path))
            return LoadBackupOrDefault();

        try
        {
            // File.ReadAllText, not Utf8NoBom.GetString(File.ReadAllBytes(...)): the latter decodes a
            // leading BOM (if the file was ever saved by an external editor) as a literal U+FEFF
            // character, which then fails to parse as JSON and quarantines a perfectly good file.
            // File.ReadAllText auto-detects and strips a BOM, falling back to UTF-8 when there is none.
            var text = ReadAllTextWithRetry(path);
            var (loaded, refusal) = Validate(text, LogDroppedAccount);

            if (refusal == ImportRefusal.NewerVersion)
            {
                LastLoadWasFromNewerVersion = true;
                _readOnly = true;
                return new AppSettings();
            }

            if (loaded is null)
            {
                QuarantineCorruptFile(path);
                return LoadBackupOrDefault();
            }

            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be read, which says nothing about its content: it stays untouched and
            // nothing is saved over it this session, so a later start still finds the real settings.
            _readOnly = true;
            _logService?.LogError($"Settings: the file could not be read ({ex.GetType().Name}); running on defaults without saving.");
            return CreateDefaultSettings();
        }
    }

    /// <summary>A sync client or virus scanner can hold the file for a moment. That is not corruption,
    /// so a short retry comes before the caller's quarantine-and-fall-back path.</summary>
    private static string ReadAllTextWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 3)
            {
                Thread.Sleep(150);
            }
        }
    }

    /// <summary>Called once the primary file is gone or unusable - recovers from the last good save
    /// instead of falling straight back to defaults, when a usable backup exists.</summary>
    private AppSettings LoadBackupOrDefault()
    {
        var backupPath = BackupFilePath;
        if (!File.Exists(backupPath))
            return CreateDefaultSettings();

        try
        {
            var text = File.ReadAllText(backupPath);
            var (loaded, refusal) = Validate(text, LogDroppedAccount);

            if (refusal == ImportRefusal.NewerVersion)
            {
                // Same refusal as the primary file's own newer-version path (see Load) - a backup
                // from a build ahead of this one must never be overwritten by this session's
                // defaults either.
                LastLoadWasFromNewerVersion = true;
                _readOnly = true;
                return CreateDefaultSettings();
            }

            if (loaded is null)
                return CreateDefaultSettings();

            _logService?.LogInfo("Settings: primary file missing or unusable, restored from settings.json.bak.");
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CreateDefaultSettings();
        }
    }

    /// <summary>A brand new <see cref="AppSettings"/> is already valid for every other field via its
    /// own initializers, but <see cref="ProviderSettings.Order"/> needs its owning id to mean
    /// anything - every fallback path in <see cref="LoadBackupOrDefault"/> goes through this so tile
    /// order always matches <see cref="AppSettings.KnownProviderIds"/> instead of only the one path
    /// that used to remember to fill it in.</summary>
    private static AppSettings CreateDefaultSettings()
    {
        var settings = new AppSettings();
        FillMissingProviders(settings);
        return settings;
    }

    /// <summary>Why <see cref="Validate"/> refused a file - both cases are shown to the user with the
    /// same generic message (<c>Settings.ImportRefused</c>); the distinction exists so <see cref="Load"/>
    /// can still tell "too new, never overwrite it" apart from "garbage, quarantine and recover".</summary>
    internal enum ImportRefusal { NewerVersion, Unparseable }

    /// <summary>
    /// The one gate every settings file goes through before this app trusts it - <see cref="Load"/>
    /// for the primary file and its backup, and <see cref="ViewModels.SettingsViewModel"/>'s own
    /// settings import for a file the user picked by hand. A schema version newer than this build
    /// understands, or anything that fails to parse into an <see cref="AppSettings"/> at all, is
    /// refused rather than trusted; anything else is filled in (<see cref="FillMissingProviders"/>)
    /// and clamped (<see cref="ClampToValidRanges"/>) exactly the way a good primary file always was.
    /// </summary>
    internal static (AppSettings? Settings, ImportRefusal? Refusal) Validate(string json, Action<string>? onDroppedAccount = null)
    {
        AppSettings? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return (null, ImportRefusal.Unparseable);
        }

        if (loaded is null)
            return (null, ImportRefusal.Unparseable);

        if (loaded.SchemaVersion > AppSettings.CurrentSchemaVersion)
            return (null, ImportRefusal.NewerVersion);

        FillMissingProviders(loaded, onDroppedAccount);
        LegacyWindowLabels.Migrate(loaded, MainViewModel.TrayWindowLabelPrefix);
        ClampToValidRanges(loaded);
        return (loaded, null);
    }

    /// <summary>Debounced entry point: a burst of changes (a dragged slider) collapses into one write.</summary>
    public void RequestSave(AppSettings settings)
    {
        if (_readOnly)
            return; // loaded from a newer version - never overwrite it with this session's defaults

        var snapshot = SnapshotForSave(settings);
        if (snapshot is null)
            return; // the previous file stays as it is

        lock (_saveGate)
        {
            _pendingSave = snapshot;
            _flushAttempts = 0;
            _debounceTimer.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// A deep copy the debounce timer can serialize later without racing whatever the caller (almost
    /// always the UI thread, right after this same call returns) does to <paramref name="settings"/>
    /// next. It goes through the same JSON form that gets saved, taken on the calling thread, so every
    /// nested collection (providers, accounts, window sizes, path maps) is private to the snapshot.
    /// A number that is not finite (the file format has no spelling for it) is first reset to its
    /// default. Null, after a log line, when the copy cannot be made at all - a collection changed
    /// under it, say - so the caller keeps the previous file instead of throwing into the UI.
    /// </summary>
    private AppSettings? SnapshotForSave(AppSettings settings)
    {
        try
        {
            NonFiniteNumbers.Reset(settings);
            return JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or JsonException)
        {
            _logService?.LogError($"Settings: save skipped, the settings could not be copied ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>Resets every NaN or infinite <c>double</c> / <c>double?</c> in a settings object graph
    /// to the value a fresh instance of the same class has.</summary>
    private static class NonFiniteNumbers
    {
        private const int MaxDepth = 8;

        public static void Reset(object target, int depth = 0)
        {
            if (depth > MaxDepth)
                return;

            object? defaults = null;
            foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                    continue;

                var type = property.PropertyType;
                if (type == typeof(double) || type == typeof(double?))
                {
                    if (property.CanWrite && property.GetValue(target) is double value && !double.IsFinite(value))
                    {
                        defaults ??= CreateDefaults(target.GetType());
                        property.SetValue(target, defaults is null ? (type == typeof(double) ? 0.0 : null) : property.GetValue(defaults));
                    }
                }
                else if (!type.IsValueType && type != typeof(string))
                {
                    ResetChildren(property.GetValue(target), depth + 1);
                }
            }
        }

        private static void ResetChildren(object? value, int depth)
        {
            if (value is null)
                return;

            if (value is System.Collections.IDictionary dictionary)
            {
                foreach (var item in dictionary.Values)
                    ResetChild(item, depth);
            }
            else if (value is System.Collections.IEnumerable items)
            {
                foreach (var item in items)
                    ResetChild(item, depth);
            }
            else
            {
                Reset(value, depth);
            }
        }

        private static void ResetChild(object? item, int depth)
        {
            if (item is not null && item is not string && !item.GetType().IsValueType)
                ResetChildren(item, depth);
        }

        private static object? CreateDefaults(Type type)
        {
            try
            {
                return Activator.CreateInstance(type);
            }
            catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException)
            {
                return null;
            }
        }
    }

    /// <summary>For a destructive step that must not wait for the debounce: saves a snapshot of
    /// <paramref name="settings"/> now and drops any queued save, which is older than this state and
    /// would otherwise write it back over this one. Returns whether the write landed.</summary>
    public bool SaveImmediately(AppSettings settings)
    {
        if (_readOnly)
            return false;

        var snapshot = SnapshotForSave(settings);
        if (snapshot is null)
            return false;

        lock (_saveGate)
        {
            _pendingSave = null;
            _flushAttempts = 0;
            _debounceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return SaveNow(snapshot);
        }
    }

    /// <summary>Writes immediately - atomic (tmp file + move), never throws outward. Returns whether
    /// the write actually landed, so <see cref="FlushPendingSave"/> can tell a failed save apart from
    /// a successful one instead of discarding the snapshot either way.</summary>
    public bool SaveNow(AppSettings settings)
    {
        if (_readOnly)
            return false; // loaded from a newer version - never overwrite it with this session's defaults

        lock (_saveGate)
        {
            try
            {
                Directory.CreateDirectory(_dataDirectory);
                settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

                var json = JsonSerializer.Serialize(settings, JsonOptions);
                // Carries this process's id so two simultaneous copies (--new-instance is a
                // debugging aid, deliberately not a supported configuration) never write through
                // the same temp file - cross-process locking was considered instead and left out on
                // purpose, since --new-instance already means "you're on your own". A copy left
                // behind by a process killed between this write and the move below is swept up by
                // AppPaths.CleanUpLeftoverTempFiles at the next startup.
                var tempPath = $"{SettingsFilePath}.{Environment.ProcessId}.tmp";
                File.WriteAllBytes(tempPath, Utf8NoBom.GetBytes(json));

                // Snapshot the previous state before it is overwritten - a valid file carrying a
                // value nobody wanted has no other way back once the move below lands.
                if (File.Exists(SettingsFilePath))
                    TryBackUpCurrentFile();

                File.Move(tempPath, SettingsFilePath, overwrite: true);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                // A caller that hands this its own still-mutable AppSettings directly (every SaveNow
                // call outside RequestSave's own snapshot) can still race a concurrent change to
                // Accounts or Providers while this serializes it. Logged rather than silently dropped
                // like the cases below, since it means a setting change was lost, not merely delayed.
                _logService?.LogError($"Settings: save failed, a collection changed while writing: {ex.Message}");
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A save that cannot land must not take the app down with it - the caller decides
                // whether and how to retry. ArgumentException covers a NaN/Infinity value the
                // serializer refuses; this runs on a timer thread, where anything uncaught ends the process.
                // The exception's own message often carries the file path (IOException,
                // UnauthorizedAccessException), so only its type is named here, never that message
                // verbatim - otherwise a save that cannot land would stay invisible instead of merely
                // silent about where.
                _logService?.LogError($"Settings: save did not land ({ex.GetType().Name}).");
                return false;
            }
        }
    }

    /// <summary>Copies the current file over the backup. The backup is a courtesy: a locked or
    /// read-only one is logged once and the save goes on without it. Called under <c>_saveGate</c>.</summary>
    private void TryBackUpCurrentFile()
    {
        try
        {
            File.Copy(SettingsFilePath, BackupFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_backupFailureLogged)
                return;
            _backupFailureLogged = true;
            _logService?.LogError($"Settings: the backup copy could not be written ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Writes whatever <see cref="RequestSave"/> last queued. A failed write keeps the snapshot
    /// pending and re-arms the debounce timer at a longer, fixed delay instead of the normal
    /// debounce one, for up to <see cref="MaxFlushAttempts"/> attempts total - a setting changed
    /// right before the disk briefly becomes unwritable (a sync client, an antivirus scan) is not
    /// silently dropped the moment that happens. Once every attempt has failed, the snapshot is
    /// dropped and one line is logged; the change stays lost for this session, same as before this
    /// retry existed, rather than retrying forever against a disk that is simply gone.
    /// <paramref name="allowRetry"/> is false only from <see cref="Dispose"/>, which flushes once on
    /// the way out and must not re-arm a timer it is about to dispose.
    /// </summary>
    private void FlushPendingSave(bool allowRetry = true)
    {
        lock (_saveGate)
        {
            var settings = _pendingSave;
            if (settings is null)
                return;

            if (SaveNow(settings))
            {
                _pendingSave = null;
                _flushAttempts = 0;
                return;
            }

            _flushAttempts++;
            if (!allowRetry || _flushAttempts >= MaxFlushAttempts)
            {
                _pendingSave = null;
                _flushAttempts = 0;
                _logService?.LogError("Settings: giving up on a pending save after repeated failures.");
                return;
            }

            _debounceTimer.Change(_failedSaveRetryDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Adds a fresh entry for any account key missing one entirely or carrying a JSON
    /// <c>null</c> (a hand-edited <c>"codex": null</c> deserializes fine despite the property's
    /// non-nullable C# type - nullable reference types are a compile-time check only), then gives
    /// every entry still carrying the -1 sentinel (a brand new entry, or an older settings.json saved
    /// before <see cref="ProviderSettings.Order"/> existed) its position from this key's own index in
    /// the combined list below - an explicit order already on disk is never touched. The key set is
    /// <see cref="AppSettings.KnownProviderIds"/> (every provider's own first account) plus whatever
    /// <see cref="AppSettings.Accounts"/> adds beyond that (a further Claude account) - for a settings
    /// file with no extra accounts the two lists are the same four keys, so this changes nothing for
    /// the common case.</summary>
    private static void FillMissingProviders(AppSettings settings, Action<string>? onDroppedAccount = null)
    {
        settings.Accounts ??= AppSettings.CreateDefaultAccounts();
        // A hand-edited entry such as "codex": "claude" would build a second provider under a key
        // another one already owns, which the scheduler cannot start with.
        foreach (var badKey in settings.Accounts.Where(kv => !IsValidAccountEntry(kv.Key, kv.Value)).Select(kv => kv.Key).ToList())
        {
            settings.Accounts.Remove(badKey);
            onDroppedAccount?.Invoke(badKey);
        }

        if (settings.Accounts.Count == 0)
            settings.Accounts = AppSettings.CreateDefaultAccounts();

        settings.Providers ??= [];
        settings.WebUsagePaths ??= [];
        settings.CopilotAccountLogins ??= [];
        settings.StatsSectionsCollapsed ??= [];
        // A hand-edited "someId": null for a key that is neither known nor an account would otherwise
        // survive to the clamping below and every later lookup.
        foreach (var nullKey in settings.Providers.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            settings.Providers.Remove(nullKey);
        var keys = AppSettings.KnownProviderIds.Concat(settings.Accounts.Keys).Distinct().ToList();
        for (var i = 0; i < keys.Count; i++)
        {
            var id = keys[i];
            if (!settings.Providers.TryGetValue(id, out var provider) || provider is null)
            {
                provider = new ProviderSettings();
                settings.Providers[id] = provider;
            }

            if (provider.Order < 0)
                provider.Order = i;
        }
    }

    /// <summary>An account key is its provider id (the first account) or the provider id, a hash and a
    /// number of 2 or more (a further account).</summary>
    private static bool IsValidAccountEntry(string key, string? providerId)
    {
        if (providerId is null || !AppSettings.KnownProviderIds.Contains(providerId))
            return false;
        if (key == providerId)
            return true;

        return key.StartsWith(providerId + "#", StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(providerId.Length + 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number)
            && number >= 2;
    }

    private void LogDroppedAccount(string key)
    {
        var shown = PathSanitizer.Sanitize(key);
        _logService?.LogInfo($"Settings: ignored an account entry with an invalid key '{(shown.Length > 40 ? shown[..40] : shown)}'.");
    }

    /// <summary>
    /// A hand-edited or older settings.json can carry a value outside what the UI would ever
    /// produce (e.g. "refreshSeconds": 0, or a retention short enough that no chart range covers
    /// it) - every consumer (the scheduler, the Settings window's range picker) assumes an
    /// already-valid value, so this is the one place that guarantees it.
    /// </summary>
    private static void ClampToValidRanges(AppSettings settings)
    {
        settings.RefreshSeconds = SettingsRanges.ClampRefreshSeconds(settings.RefreshSeconds);
        settings.RemoteRefreshMinutes = SettingsRanges.ClampRemoteRefreshMinutes(settings.RemoteRefreshMinutes);
        settings.AttentionMaxAgeMinutes = SettingsRanges.ClampAttentionMaxAgeMinutes(settings.AttentionMaxAgeMinutes);
        settings.HistoryRetentionDays = SettingsRanges.SnapRetentionDays(settings.HistoryRetentionDays);
        settings.WindowOpacityPercent = SettingsRanges.ClampWindowOpacityPercent(settings.WindowOpacityPercent);
        settings.DefaultThreshold = SettingsRanges.ClampThreshold(settings.DefaultThreshold);

        var availableRanges = SettingsRanges.AvailableChartRanges(settings.HistoryRetentionDays);
        if (availableRanges.Count > 0 && availableRanges.All(option => option.Value != settings.ChartRange))
            settings.ChartRange = availableRanges[0].Value;

        // Case-insensitive like the startup parse of the same value, and stored under the canonical
        // spelling, so a hand-edited "dark" means Dark instead of silently becoming Nebula.
        settings.Theme = CanonicalEnumName<AppTheme>(settings.Theme, nameof(AppTheme.Nebula));

        // "Auto" is not an AppTheme-style enum member - it is the sentinel meaning "no manual override".
        // A stage that no longer exists (a settings file written before the Compact stage was dropped)
        // is not a defined member any more and so loads as "Auto".
        if (settings.TileDensity != "Auto")
            settings.TileDensity = CanonicalEnumName<TileDensity>(settings.TileDensity, "Auto");

        settings.Window ??= new WindowSettings();
        ClampWindow(settings.Window);

        foreach (var provider in settings.Providers.Values)
        {
            provider.Thresholds ??= new ThresholdSettings();
            provider.Thresholds.FiveHour = SettingsRanges.ClampThreshold(provider.Thresholds.FiveHour);
            provider.Thresholds.Weekly = SettingsRanges.ClampThreshold(provider.Thresholds.Weekly);
            provider.Thresholds.Other = SettingsRanges.ClampThreshold(provider.Thresholds.Other);
            provider.HiddenWindows ??= [];
        }
    }

    private static string CanonicalEnumName<TEnum>(string? value, string fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed.ToString() : fallback;

    /// <summary>A hand-edited window block can carry a negative size or a non-finite position - every
    /// consumer downstream (MainWindow's Width/Height setters, WindowPlacementService) assumes a sane
    /// rectangle to start from.</summary>
    private static void ClampWindow(WindowSettings window)
    {
        window.Vertical ??= new LayoutSizes();
        window.Horizontal ??= new LayoutSizes();
        ClampLayoutSizes(window.Vertical);
        ClampLayoutSizes(window.Horizontal);
        window.SettingsWidth = ClampDimension(window.SettingsWidth, 480);
        window.Left = double.IsFinite(window.Left) ? window.Left : 100;
        window.Top = double.IsFinite(window.Top) ? window.Top : 100;
    }

    private static void ClampLayoutSizes(LayoutSizes sizes)
    {
        sizes.Width = ClampDimension(sizes.Width, WindowPlacementService.MinWindowWidth);
        sizes.Height = sizes.Height is { } height ? ClampDimension(height, WindowPlacementService.MinWindowHeight) : null;
    }

    private static double ClampDimension(double value, double min) =>
        double.IsFinite(value) ? Math.Max(min, value) : min;

    private static void QuarantineCorruptFile(string path)
    {
        try
        {
            File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort - the caller runs on defaults either way
        }
    }

    /// <summary>Flushes a still-pending debounced save before disposing the timer - otherwise a
    /// setting changed just before app exit (e.g. a slider drag followed immediately by "Exit" from
    /// the tray) would be silently lost.</summary>
    public void Dispose()
    {
        lock (_saveGate)
        {
            // One attempt only, never the retry-with-a-longer-delay FlushPendingSave otherwise
            // uses: the app is exiting either way, and re-arming a timer this method is about to
            // dispose would just be discarded, not actually retried.
            FlushPendingSave(allowRetry: false);

            // Does not wait for a callback already in flight, so this cannot deadlock against a
            // timer thread queued on the gate: that callback finds nothing pending and returns.
            _debounceTimer.Dispose();
        }
    }
}
