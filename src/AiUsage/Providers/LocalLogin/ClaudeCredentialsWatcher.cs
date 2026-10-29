namespace AiUsage.Providers.LocalLogin;

/// <summary>
/// Calls back shortly after Claude Code rewrites its sign-in file, so a fresh login or a renewed
/// token is read within seconds instead of at the next scheduled read, which can be minutes away.
/// Watches only the folders that may hold the file and only that one file name; never opens it.
/// A burst of writes (Claude Code writes, renames and touches the file in one go) ends in one call.
/// </summary>
internal sealed class ClaudeCredentialsWatcher : IDisposable
{
    internal static readonly TimeSpan DefaultSettle = TimeSpan.FromSeconds(2);

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Action _onChanged;
    private readonly TimeSpan _settle;
    private readonly Lock _gate = new();
    private Timer? _timer;
    private bool _disposed;

    internal ClaudeCredentialsWatcher(IEnumerable<string> folders, string fileName, Action onChanged, TimeSpan? settle = null)
    {
        _onChanged = onChanged;
        _settle = settle ?? DefaultSettle;
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder))
                continue;
            try
            {
                var watcher = new FileSystemWatcher(folder, fileName)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                watcher.Changed += OnEvent;
                watcher.Created += OnEvent;
                watcher.Deleted += OnEvent;
                watcher.Renamed += OnEvent;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                // A folder that cannot be watched is read on the normal schedule only.
            }
        }
    }

    /// <summary>Every folder Claude Code may keep its sign-in in on this machine.</summary>
    public static ClaudeCredentialsWatcher ForThisMachine(Action onChanged) =>
        new(ClaudeConfigRoot.Candidates(), ClaudeCodeLogin.CredentialsFileName, onChanged);

    internal int WatchedFolderCount => _watchers.Count;

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _timer?.Dispose();
            _timer = new Timer(_ => Fire(), null, _settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }
        _onChanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer?.Dispose();
        }
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }
}
