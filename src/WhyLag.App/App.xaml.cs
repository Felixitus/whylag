using System.IO;
using System.Windows;
using WhyLag.Collectors;
using WhyLag.Core;
using WhyLag.Core.Localization;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.App;

/// <summary>
/// Runs in the notification area: keeps the recorder going, listens for the hotkey and opens
/// the report window on demand. Single instance per user.
/// </summary>
public partial class App : Application
{
    private const string ExitEventName = @"Local\WhyLag.Exit";
    private Mutex? _singleInstance;
    private EventWaitHandle? _exitRequested;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkey;
    private MainWindow? _window;

    public WhyLagSettings Settings { get; private set; } = new();
    public Recorder? Recorder { get; private set; }
    public IncidentStore Store { get; } = new();
    public MachineInfo Machine { get; private set; } = new();
    public string? HotkeyError { get; private set; }

    /// <summary>True once WhyLag is shutting down; windows and dialogs must not touch the app state anymore.</summary>
    public bool IsExiting { get; private set; }

    /// <summary>The running app, or null while it starts or after it shut down.</summary>
    public static App? Instance => Application.Current as App;

    /// <summary>A newer release found by the daily update check (null if none or the check is off).</summary>
    public UpdateInfo? AvailableUpdate { get; private set; }

    public SessionStore Sessions { get; } = new();

    /// <summary>Raised on the UI thread when a gaming session summary was saved.</summary>
    public event Action<GameSession>? SessionAdded;

    private OverlayWindow? _overlay;

    private async Task CheckForUpdatesAsync()
    {
        if (!Settings.CheckForUpdates || DateTimeOffset.Now - Settings.LastUpdateCheck < TimeSpan.FromHours(20)) return;
        var update = await UpdateChecker.CheckAsync();
        if (IsExiting) return;
        UpdateSettings(s => s.LastUpdateCheck = DateTimeOffset.Now);
        if (update is null) return;
        AvailableUpdate = update;
        _tray?.Notify(T("update.title"), T("update.text", update.Version.ToString(3)), () => OpenUrl(update.Url));
        _window?.RefreshStatus();
    }

    public static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Shows or hides the small always-on-top overlay with fps, ping and load.</summary>
    public void ToggleOverlay(bool show)
    {
        if (show && _overlay is null)
        {
            _overlay = new OverlayWindow();
            _overlay.Closed += (_, _) => _overlay = null;
            _overlay.Show();
        }
        else if (!show)
        {
            _overlay?.Close();
        }
        if (Settings.OverlayEnabled != show) UpdateSettings(s => s.OverlayEnabled = show);
    }

    public bool OverlayVisible => _overlay is not null;

    private void OnSession(GameSession session)
    {
        if (IsExiting) return;
        Sessions.Save(session);
        SessionAdded?.Invoke(session);
        if (Settings.Notifications != NotifyMode.HotkeyOnly || session.Hitches > 0)
            _tray?.Notify(T("session.notify.title", session.App),
                T("session.notify.text", Duration(session.Duration), session.AverageFps.ToString("0", Loc.Culture), session.Hitches, session.IncidentIds.Count),
                () => ShowMain());
    }

    public static string Duration(TimeSpan d) => d.TotalHours >= 1 ? T("duration.hm", (int)d.TotalHours, d.Minutes) : T("duration.m", (int)Math.Round(d.TotalMinutes));

    public static new App Current => (App)Application.Current;

    /// <summary>Raised on the UI thread when a new incident was saved.</summary>
    public event Action<Incident>? IncidentAdded;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings = SettingsStore.Load();
        // "--lang <code>" overrides the language for this run without changing the settings (e.g. for screenshots).
        var langArg = Array.IndexOf(e.Args, "--lang");
        Loc.SetLanguage(langArg >= 0 && langArg + 1 < e.Args.Length ? e.Args[langArg + 1] : Settings.Language);

        if (e.Args.FirstOrDefault() == LatencyHelperHost.Argument)
        {
            // Elevated helper mode: no UI, just the kernel latency trace (see LatencyHelperHost).
            Shutdown(LatencyHelperHost.Run(e.Args));
            return;
        }
        if (e.Args.Contains("--exit"))
        {
            // Used by the installer and uninstaller: ask a running instance to shut down cleanly.
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent))
            {
                using (exitEvent) exitEvent.Set();
            }
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\WhyLag.App", out var first);
        if (!first)
        {
            MessageBox.Show(T("app.running"), "WhyLag");
            Shutdown();
            return;
        }
        _exitRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        ThreadPool.RegisterWaitForSingleObject(_exitRequested, (_, _) => Dispatcher.BeginInvoke(ExitApp), null, Timeout.Infinite, executeOnlyOnce: true);

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
            if (IsExiting) return; // errors while closing are logged, but nobody needs a dialog for them
            MessageBox.Show(T("app.error", args.Exception.Message, AppPaths.LogsDir), "WhyLag");
        };

        ApplyTheme(Settings.Theme);
        Machine = HardwareInfo.Machine();
        CreateTray();
        _hotkey = new HotkeyManager();
        _hotkey.Pressed += () => CaptureNow(TriggerKind.Hotkey);
        ApplySettings(Settings, restartRecorder: true);

        var firstRun = !File.Exists(AppPaths.ConfigFile);
        if (firstRun) SettingsStore.Save(Settings);
        if (firstRun || e.Args.Contains("--show")) ShowMain();
        else _tray!.Notify(T("app.started.title"), T("app.started.text", Settings.Hotkey));
        if (Settings.OverlayEnabled) ToggleOverlay(true);
        _ = CheckForUpdatesAsync();
    }

    private void CreateTray()
    {
        _tray?.Dispose();
        _tray = new TrayIcon(ShowMain, () => CaptureNow(TriggerKind.Manual), paused => { if (Recorder is not null) Recorder.Paused = paused; },
            snoozed => { if (Recorder is not null) Recorder.AutoDetectPausedUntil = snoozed ? DateTimeOffset.Now.AddHours(1) : default; },
            ToggleOverlay, Settings.OverlayEnabled, ShowSettings, ExitApp);
    }

    /// <summary>
    /// Applies new settings. The recorder (and with it the in-memory history and the optional elevated helper)
    /// is only restarted when something it records changed.
    /// </summary>
    public void ApplySettings(WhyLagSettings settings, bool restartRecorder)
    {
        Settings = settings;
        if (restartRecorder || Recorder is null)
        {
            Recorder?.Dispose();
            var (metrics, events) = DefaultSources.Create(settings, Machine);
            Recorder = new Recorder(settings, metrics, events, Machine, Store);
            Recorder.IncidentCaptured += incident => Dispatcher.BeginInvoke(() => OnIncident(incident));
            Recorder.SessionEnded += session => Dispatcher.BeginInvoke(() => OnSession(session));
            Recorder.Start();
        }
        else
        {
            Recorder.UpdateSettings(settings);
        }

        HotkeyError = _hotkey?.Register(settings.Hotkey);
        if (_tray is not null) _tray.Tooltip = HotkeyError is null ? T("tray.tooltip", settings.Hotkey) : T("tray.tooltip.nohotkey");
    }

    /// <summary>Changes and saves a setting that does not need a recorder restart (e.g. ignored apps).</summary>
    public void UpdateSettings(Action<WhyLagSettings> change)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<WhyLagSettings>(System.Text.Json.JsonSerializer.Serialize(Settings, Json.Options), Json.Options)!;
        change(copy);
        SettingsStore.Save(copy);
        ApplySettings(copy, restartRecorder: false);
    }

    private static bool NeedsRecorderRestart(WhyLagSettings a, WhyLagSettings b) =>
        a.BufferMinutes != b.BufferMinutes || a.PingEnabled != b.PingEnabled || a.PingTarget != b.PingTarget
        || a.FrameTimesEnabled != b.FrameTimesEnabled || a.DriverLatencyHelper != b.DriverLatencyHelper
        || a.RouterTraffic != b.RouterTraffic || a.Temperatures != b.Temperatures;

    private void CaptureNow(TriggerKind trigger)
    {
        if (Recorder is not { } recorder) return;
        Task.Run(() =>
        {
            try
            {
                recorder.Capture(trigger, trigger == TriggerKind.Hotkey ? Settings.Hotkey : T("trigger.tray"));
            }
            catch (Exception ex)
            {
                Log(ex);
            }
        });
    }

    private void OnIncident(Incident incident)
    {
        if (IsExiting) return;
        IncidentAdded?.Invoke(incident);
        var automatic = incident.Trigger == TriggerKind.Automatic;
        var mode = Settings.Notifications;
        if (automatic && mode == NotifyMode.HotkeyOnly) return;
        if (automatic && mode == NotifyMode.Summary && DateTimeOffset.Now - _lastAutoNotice < SummaryInterval)
        {
            // Several automatic incidents in a row: one summary notification instead of many.
            _pendingAutoNotices++;
            _summaryTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = SummaryInterval };
            _summaryTimer.Tick -= FlushSummary;
            _summaryTimer.Tick += FlushSummary;
            _summaryTimer.Start();
            return;
        }
        if (automatic) _lastAutoNotice = DateTimeOffset.Now;
        var headline = incident.Findings.FirstOrDefault()?.Title ?? T("app.incident.none");
        _tray?.Notify(automatic ? T("app.incident.auto") : T("app.incident.manual"), headline, () => ShowMain(incident.Id));
    }

    private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(10);
    private DateTimeOffset _lastAutoNotice;
    private int _pendingAutoNotices;
    private System.Windows.Threading.DispatcherTimer? _summaryTimer;

    private void FlushSummary(object? sender, EventArgs e)
    {
        _summaryTimer?.Stop();
        if (_pendingAutoNotices == 0 || IsExiting) return;
        _tray?.Notify(T("app.incident.auto"), T("app.incident.summary", _pendingAutoNotices), ShowMain);
        _pendingAutoNotices = 0;
        _lastAutoNotice = DateTimeOffset.Now;
    }

    /// <summary>Applies the light/dark choice (WPF Fluent theme).</summary>
    public void ApplyTheme(string theme)
    {
#pragma warning disable WPF0001
        ThemeMode = theme switch { "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System };
#pragma warning restore WPF0001
    }

    public void AddDemoIncident()
    {
        var demo = DemoIncident.Create(Machine);
        Store.Save(demo);
        IncidentAdded?.Invoke(demo);
    }

    public void ShowMain() => ShowMain(null);

    public void ShowMain(string? incidentId)
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        if (incidentId is not null) _window.Select(incidentId);
    }

    public void ShowSettings()
    {
        var dialog = new SettingsWindow(Settings) { Owner = _window?.IsVisible == true ? _window : null };
        if (dialog.ShowDialog() != true || IsExiting) return;

        var old = Settings;
        SettingsStore.Save(dialog.Result);
        ApplySettings(dialog.Result, NeedsRecorderRestart(old, dialog.Result));
        if (old.Theme != dialog.Result.Theme) ApplyTheme(dialog.Result.Theme);

        if (old.Language != dialog.Result.Language)
        {
            // Texts are resolved when windows are built, so rebuild the tray menu and the main window.
            Loc.SetLanguage(dialog.Result.Language);
            CreateTray();
            if (_window is { } window)
            {
                window.Close();
                ShowMain();
            }
        }
        else
        {
            _window?.RefreshStatus();
        }
    }

    private void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        // Close open dialogs first so that their callers return before the app state is torn down.
        _overlay?.Close();
        foreach (var window in Windows.OfType<Window>().Where(w => w != _window).ToList()) window.Close();
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Recorder?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _exitRequested?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, "whylag.log"), $"{DateTimeOffset.Now:O} {ex}\n");
        }
        catch (IOException)
        {
        }
    }
}
