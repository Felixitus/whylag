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

    public static new App Current => (App)Application.Current;

    /// <summary>Raised on the UI thread when a new incident was saved.</summary>
    public event Action<Incident>? IncidentAdded;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings = SettingsStore.Load();
        Loc.SetLanguage(Settings.Language);

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
            MessageBox.Show(T("app.error", args.Exception.Message, AppPaths.LogsDir), "WhyLag");
            args.Handled = true;
        };

        Machine = HardwareInfo.Machine();
        CreateTray();
        _hotkey = new HotkeyManager();
        _hotkey.Pressed += () => CaptureNow(TriggerKind.Hotkey);
        ApplySettings(Settings, restartRecorder: true);

        var firstRun = !File.Exists(AppPaths.ConfigFile);
        if (firstRun) SettingsStore.Save(Settings);
        if (firstRun || e.Args.Contains("--show")) ShowMain();
        else _tray!.Notify(T("app.started.title"), T("app.started.text", Settings.Hotkey));
    }

    private void CreateTray()
    {
        _tray?.Dispose();
        _tray = new TrayIcon(ShowMain, () => CaptureNow(TriggerKind.Manual), paused => { if (Recorder is not null) Recorder.Paused = paused; }, ShowSettings, ExitApp);
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
            Recorder.Start();
        }
        else
        {
            Recorder.UpdateSettings(settings);
        }

        HotkeyError = _hotkey?.Register(settings.Hotkey);
        if (_tray is not null) _tray.Tooltip = HotkeyError is null ? T("tray.tooltip", settings.Hotkey) : T("tray.tooltip.nohotkey");
    }

    private static bool NeedsRecorderRestart(WhyLagSettings a, WhyLagSettings b) =>
        a.BufferMinutes != b.BufferMinutes || a.PingEnabled != b.PingEnabled || a.PingTarget != b.PingTarget
        || a.FrameTimesEnabled != b.FrameTimesEnabled || a.DriverLatencyHelper != b.DriverLatencyHelper;

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
        IncidentAdded?.Invoke(incident);
        var headline = incident.Findings.FirstOrDefault()?.Title ?? T("app.incident.none");
        var title = incident.Trigger == TriggerKind.Automatic ? T("app.incident.auto") : T("app.incident.manual");
        _tray?.Notify(title, headline, () => ShowMain(incident.Id));
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
        if (dialog.ShowDialog() != true) return;

        var old = Settings;
        SettingsStore.Save(dialog.Result);
        ApplySettings(dialog.Result, NeedsRecorderRestart(old, dialog.Result));

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
