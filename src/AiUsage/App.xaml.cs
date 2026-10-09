using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using AiUsage.Models;
using AiUsage.Providers.Parsing;
using AiUsage.Services;
using AiUsage.Stats;
using AiUsage.Storage;
using AiUsage.Views;

namespace AiUsage;

public partial class App : Application, IDisposable
{
    private SettingsStore? _settingsStore;
    private MainWindow? _mainWindow;
    private SingleInstanceService? _singleInstance;
    private LogService? _logService;
    private StatsIndexerService? _statsIndexerService;
    private DispatcherTimer? _statsIndexerReindexTimer;
    private DispatcherTimer? _startupDelayTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // FrameworkElement.Language defaults to en-US for the whole element tree regardless of the
        // thread's culture, so any XAML StringFormat (dates, numbers) would always render US-style.
        // Deliberately CurrentCulture (regional number/date format) here, not CurrentUICulture
        // (display language, driven separately by LocalizationService) - the two are independent.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        // --status: prints the last status file and leaves. Headless and before everything else on
        // purpose: no log service, no data cleanup, no single-instance check, so it answers while the
        // widget runs and when it does not.
        if (StatusCommand.IsRequested(e.Args))
        {
            var exitCode = StatusCommand.Run(e.Args, AppPaths.DataDirectory, DateTimeOffset.Now, StatusCommand.OpenOutput());
            Shutdown(exitCode);
            return;
        }

        // --set-autostart: the installer's own "Mit Windows
        // starten" task runs the just-installed exe with this switch instead of writing the
        // registry value itself, so there is exactly one place - AutostartService.Enable - that
        // decides what the value looks like. Headless: no window, no log service, no single-
        // instance check, gone before any of that would matter.
        if (e.Args.Contains("--set-autostart", StringComparer.OrdinalIgnoreCase))
        {
            AutostartService.Enable(Environment.ProcessPath ?? AppContext.BaseDirectory);
            Shutdown();
            return;
        }

        // --create-desktop-shortcut: the installer's finish-page checkbox runs the just-installed
        // exe with this switch instead of writing the .lnk itself, for the same
        // originaluser-context reason as --set-autostart above. Headless like that switch too; the
        // one difference is a failure gets logged, since a missing shortcut is otherwise invisible
        // until the user goes looking for it - a minimal log service is enough for that one line.
        if (DesktopShortcutService.IsRequested(e.Args))
        {
            if (!DesktopShortcutService.Create(Environment.ProcessPath ?? AppContext.BaseDirectory))
                LogService.Shared.LogInfo("Desktop shortcut creation failed.");
            Shutdown();
            return;
        }

        // --second-instance: another copy alongside the one already running, for trying a fresh build
        // without closing the everyday one. Redirected before the log service opens its first file,
        // so settings, history, logs and the browser sign-ins all land in this copy's own folders and
        // the running copy's data is never written from here.
        var secondInstance = SecondInstanceMode.IsRequested(e.Args);
        if (secondInstance)
            SecondInstanceMode.Redirect();

        // Wired before anything else can throw, so every failure below
        // is caught and logged instead of surfacing a native Windows crash dialog.
        _logService = LogService.Shared;
        ToastRegistration.SetProcessId();
        _logService.LogInfo($"Start: version {AppInfo.Version}, {RuntimeInformation.ProcessArchitecture}.");
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // A portable update restarts through this switch while the old copy is still shutting down; the
        // new one waits for it so the single-instance check below does not hand over to a dying copy.
        // The downloaded update files are cleaned up here too; the previous program file stays until
        // the new copy has shown its window.
        UpdateHost.WaitForPreviousCopy(e.Args);
        UpdateHost.CleanUpStaleFiles();

        // The single-instance check is the one thing a second instance must skip - it exists so a
        // second start hands the running window to the front instead of opening another one, which is
        // exactly what this switch asks for. Every ordinary start still goes through it.
        if (secondInstance)
        {
            _logService.LogInfo($"Second instance: data folder {AppPaths.DataDirectory}.");
        }
        else
        {
            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.AcquireOwnership(e.Args))
            {
                SingleInstanceService.RequestShow();
                Shutdown();
                return;
            }
        }

        // Best-effort: a copy the reader could not delete last run (file still locked, app killed
        // mid-read) should not sit in the cache folder forever waiting for another lookup to fail.
        AntigravityStateReader.CleanUpLeftoverSnapshots();

        // Same reasoning, for a settings/history temp file a process killed mid-save left behind.
        AppPaths.CleanUpLeftoverTempFiles();

        _settingsStore = new SettingsStore(logService: _logService);
        var settings = _settingsStore.Load();

        // A copy that was moved since autostart was switched on would otherwise start nothing at logon.
        if (settings.Autostart && Environment.ProcessPath is { } ownPath && AutostartService.RepairIfMoved(ownPath))
            _logService.LogInfo("Autostart: the entry pointed at a program file that no longer exists and now names this copy.");

        // StatsWindow has no constructor path back here (MainWindow.xaml.cs's sole "new
        // StatsWindow(...)" call site takes no settings argument), so this static seam hands it the
        // same live settings instance and store every other window already reads and saves through -
        // see WindowPlacementService.Shared.
        WindowPlacementService.Shared = new WindowPlacementService(settings, _settingsStore);

        // One class handler for the whole element tree, not a style or a per-element ToolTip
        // suppression: WPF resolves an implicit style by an element's own concrete type, so a style
        // targeting FrameworkElement would never apply to a Button or ComboBox, and
        // ToolTipService.IsEnabled does not inherit down the tree either. The tooltip-opening event
        // does bubble through every framework element, so one handler registered once here covers
        // every tooltip in every window, including ones a later window adds - reading
        // settings.ShowTooltips live (the same mutable instance SettingsViewModel writes straight
        // into) rather than a value captured at startup, so flipping the setting takes effect at once.
        // The actual decision lives in HandleToolTipOpening below, a plain static method a test can
        // call directly - OnStartup itself needs a live WPF Application no test project has.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler((_, args) => HandleToolTipOpening(settings, args)));

        LocalizationService.Instance.SetLanguage(settings.Language);
        ThemeService.Apply(ThemeService.ParseTheme(settings.Theme));
        // No window exists yet, so this only records the ambient WindowOpacity.CurrentPercent - every
        // window created from here on applies it to itself via WindowChromeNative.Bootstrap.
        WindowOpacity.ApplyToAllOpenWindows(settings.WindowOpacityPercent);
        _logService.LogInfo($"Language {settings.Language}, theme {settings.Theme} applied.");

        // Autostart starts the tray copy while the desktop is still loading: everything below this point
        // (indexer, window, tray, first fetches) waits so logon stays quick. Ownership of the single-
        // instance lock and its show event are already held, so a second start during the wait is not
        // lost: its request stays signalled and is answered the moment the handler is registered.
        LogService logService = _logService;
        SettingsStore settingsStore = _settingsStore;
        var delay = StartupMode.StartDelay(e.Args);
        if (delay > TimeSpan.Zero)
        {
            logService.LogInfo($"Autostart: waiting {delay.TotalSeconds:0} s before the window, tray and background work start.");
            var timer = new DispatcherTimer { Interval = delay };
            _startupDelayTimer = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _startupDelayTimer = null;
                if (!Dispatcher.HasShutdownStarted)
                    CompleteStartup(e.Args, settings, settingsStore, logService);
            };
            timer.Start();
        }
        else
        {
            CompleteStartup(e.Args, settings, settingsStore, logService);
        }
    }

    /// <summary>The second half of the start: the stats indexer, the main window with its tray icon and
    /// first fetches, and the handler that answers a later start. Runs at once for an ordinary start and
    /// after the autostart delay for a <c>--tray</c> start.</summary>
    private void CompleteStartup(string[] args, AppSettings settings, SettingsStore settingsStore, LogService logService)
    {
        // One instance for the whole app's lifetime, walking session logs on its own background
        // thread so a fresh install with years of history never blocks startup - never built per
        // window, since a StartInBackground call while a walk is already running is a no op. Shared
        // hands the same instance to StatsWindow (own no constructor path back here, same reason as
        // WindowPlacementService.Shared) so opening it also starts a fresh walk and reloads once it
        // finishes. The ten-minute timer below covers the case where the window never gets reopened:
        // the tiles' week token figures read this same database, so without it usage made while the
        // app just keeps running would only show up after the next start or the next time the
        // statistics window opens.
        _statsIndexerService = new StatsIndexerService(new StatsStore(), logService);
        StatsIndexerService.Shared = _statsIndexerService;
        _statsIndexerService.StartInBackground();
        _statsIndexerReindexTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(10),
        };
        _statsIndexerReindexTimer.Tick += (_, _) => _statsIndexerService?.StartInBackground();
        _statsIndexerReindexTimer.Start();

        _mainWindow = new MainWindow(settingsStore, settings, logService);
        // The previous program a portable update left beside the exe is the way back if this copy
        // cannot run, so it goes only once the window has rendered.
        if (Environment.ProcessPath is { } runningExe)
        {
            var window = _mainWindow;
            void DeleteLeftoverOnce(object? sender, EventArgs args)
            {
                window.ContentRendered -= DeleteLeftoverOnce;
                PortableSwap.DeleteLeftover(runningExe);
            }

            window.ContentRendered += DeleteLeftoverOnce;
        }

        // --tray (Autostart): the tray icon and background polling
        // already start inside MainWindow's own constructor - only the visible window is skipped.
        // Kept in a field (not a local) so the running instance has an explicit GC root regardless
        // of Show() being called.
        if (!args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            // Shown once, on the very first real start on a fresh profile - before the widget itself
            // appears, so the very first thing shown is an explanation rather than a wall of empty
            // placeholder tiles. Skipped entirely for a --tray-only start (no window is shown then
            // either), so it still appears the next time the window is actually shown.
            if (StartupMode.ShouldShowWelcome(settings))
            {
                new WelcomeWindow(_mainWindow.ViewModel.Tiles).ShowDialog();
                settings.WelcomeShown = true;
                settingsStore.SaveNow(settings);
            }

            // A crash mid-startup (e.g. during the modal welcome dialog above) can already have run
            // Shutdown() by the time execution gets back here - Show() on a window WPF has already
            // closed as part of that shutdown throws InvalidOperationException instead of quietly
            // doing nothing.
            if (ShouldShowMainWindow(Dispatcher.HasShutdownStarted))
                _mainWindow.Show();
        }

        // A later start (window hidden into the tray, or a --tray instance with no window at all)
        // signals this instead of exiting silently; the callback runs on a pool thread and marshals
        // itself to the UI thread. A second instance holds no such service on purpose: an ordinary
        // start has to reach the everyday copy, never this one.
        _singleInstance?.RegisterShowRequestHandler(() =>
            Dispatcher.BeginInvoke(() => _mainWindow?.ShowAndActivate()));

#if DEBUG
        // Test-only, undocumented: forces a real DispatcherUnhandledException after startup so the
        // crash dialog and logging path can be proven end-to-end. Debug-only - a product has no
        // business shipping its own crash switch.
        if (args.Contains("--force-crash-for-test", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(() => throw new InvalidOperationException(
                "Forced crash for end-to-end crash-dialog verification (test-only)."));
#endif
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logService?.LogInfo("Exit.");
        Dispose();
        base.OnExit(e);
    }

    /// <summary>Pure enough to unit-test: Show() on a window WPF has already closed as part of an
    /// in-flight Shutdown() throws InvalidOperationException instead of doing nothing, so OnStartup
    /// checks the dispatcher's own shutdown state first instead of finding out the hard way.</summary>
    internal static bool ShouldShowMainWindow(bool dispatcherHasShutdownStarted) => !dispatcherHasShutdownStarted;

    /// <summary>The one place that decides whether a tooltip is allowed to open at all - see the
    /// class-handler registration in OnStartup above. Marking the event handled here, rather than
    /// hiding the tooltip after the fact, means it never appears at all while the setting is off, not
    /// even for one frame. Takes the base RoutedEventArgs, not the concrete ToolTipEventArgs the real
    /// event carries - ToolTipEventArgs has no public constructor, so a test proving this decision
    /// needs the base type to build one at all.</summary>
    internal static void HandleToolTipOpening(AppSettings settings, RoutedEventArgs args)
    {
        if (!settings.ShowTooltips)
            args.Handled = true;
    }

    public void Dispose()
    {
        _startupDelayTimer?.Stop();
        _statsIndexerReindexTimer?.Stop();
        if (ReferenceEquals(StatsIndexerService.Shared, _statsIndexerService))
            StatsIndexerService.Shared = null;
        _statsIndexerService?.Dispose();
        _settingsStore?.Dispose();
        _singleInstance?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleFatal(e.Exception);
        e.Handled = true;
        Shutdown();
    }

    /// <summary>A background task nobody awaited failed: on record in the log, not fatal.</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var inner = e.Exception.Flatten().InnerExceptions.FirstOrDefault() ?? e.Exception;
        _logService?.LogError($"A background task failed ({inner.GetType().Name}): {PathSanitizer.Sanitize(inner.Message)}");
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            HandleFatal(ex);
    }

    /// <summary>Logs first, then shows the dialog - a background-thread crash
    /// (AppDomain.UnhandledException can fire on any thread) must still reach the user instead of
    /// throwing InvalidOperationException from creating a window off the UI thread and being seen
    /// nowhere. The whole show attempt stays inside the try, so a dead dispatcher costs only the
    /// dialog, never a second crash.</summary>
    private void HandleFatal(Exception ex)
    {
        var details = PathSanitizer.Sanitize(ex.ToString());
        _logService?.LogError(details);

        try
        {
            FatalHandler.Show(
                () => new CrashWindow(details, AppPaths.LogsDirectory).ShowDialog(),
                Dispatcher.CheckAccess,
                action =>
                {
                    if (!FatalHandler.InvokeWhenResponsive(Dispatcher, action, TimeSpan.FromSeconds(5)))
                        _logService?.LogError("Crash dialog skipped: the interface did not respond.");
                },
                () => Dispatcher.HasShutdownStarted,
                () => _logService?.LogError("Crash dialog skipped: the application is already shutting down."));
        }
        catch (Exception dialogEx)
        {
            // The crash dialog itself must never mask the original error path.
            _logService?.LogError(dialogEx.ToString());
        }
    }
}
