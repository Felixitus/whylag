using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using WhyLag.AI;
using WhyLag.Collectors;
using WhyLag.Core;
using WhyLag.Core.Localization;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.App;

public partial class SettingsWindow : Window
{
    private readonly string _autoModel = T("set.ai.automodel");
    private readonly ModelSuggestion? _suggestion;
    private CancellationTokenSource? _download;

    public SettingsWindow(WhyLagSettings current)
    {
        InitializeComponent();
        Icon = TrayIcon.WindowIcon();
        // Work on a copy so Cancel really discards everything.
        Result = JsonSerializer.Deserialize<WhyLagSettings>(JsonSerializer.Serialize(current, Json.Options), Json.Options)!;

        var languages = new Dictionary<string, string> { ["auto"] = T("set.language.auto") };
        foreach (var code in Loc.Available.Order()) languages[code] = Loc.NativeName(code);
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedValue = languages.ContainsKey(Result.Language) ? Result.Language : "auto";

        ThemeBox.ItemsSource = new Dictionary<string, string> { ["System"] = T("set.theme.system"), ["Light"] = T("set.theme.light"), ["Dark"] = T("set.theme.dark") };
        ThemeBox.SelectedValue = Result.Theme;
        NotifyBox.ItemsSource = new Dictionary<NotifyMode, string>
        {
            [NotifyMode.Summary] = T("set.notify.summary"),
            [NotifyMode.All] = T("set.notify.all"),
            [NotifyMode.HotkeyOnly] = T("set.notify.hotkey"),
        };
        NotifyBox.SelectedValue = Result.Notifications;
        UpdateCheck.IsChecked = Result.CheckForUpdates;
        AutoUpdate.IsChecked = Result.AutoInstallUpdates;
        Temperatures.IsChecked = Result.Temperatures;
        RouterTraffic.IsChecked = Result.RouterTraffic;
        Hotkey.Text = Result.Hotkey;
        BufferMinutes.Text = Result.BufferMinutes.ToString();
        KeepIncidents.Text = Result.KeepIncidents.ToString();
        AutoDetect.IsChecked = Result.AutoDetect;
        AutoDetectNetwork.IsChecked = Result.AutoDetectNetwork;
        StartWithWindows.IsChecked = StartupRegistration.IsEnabled();
        PingEnabled.IsChecked = Result.PingEnabled;
        PingTarget.Text = Result.PingTarget;
        FrameTimes.IsChecked = Result.FrameTimesEnabled;
        IgnoredApps.Text = string.Join(", ", Result.IgnoredApps);
        DriverLatency.IsChecked = Result.DriverLatencyHelper;
        UpdatePresentMon();

        AiEnabled.IsChecked = Result.Ai.Enabled;
        AiEndpoint.Text = Result.Ai.Endpoint;
        AiModel.ItemsSource = Result.Ai.Model is { } model ? new List<string> { _autoModel, model } : new List<string> { _autoModel };
        AiModel.SelectedItem = Result.Ai.Model ?? _autoModel;

        _suggestion = ModelAdvisor.Recommend(HardwareInfo.Gpus().FirstOrDefault()?.VramMb ?? 0, App.Current.Machine.TotalMemoryMb);
        Recommendation.Text = _suggestion is null
            ? T("set.ai.norecommend")
            : T("set.ai.recommend", _suggestion.Name, _suggestion.DownloadGb.ToString("0.#", Culture), _suggestion.License, T(_suggestion.Note));
        DownloadButton.IsEnabled = _suggestion is not null;
        DataLocation.Text = $"{AppPaths.ConfigFile}\n{AppPaths.IncidentsDir}\n{AppPaths.LogsDir}";

        UpdateAiPanel();
        if (Result.Ai.Enabled) Loaded += (_, _) => Test_Click(this, new RoutedEventArgs());
        Closed += (_, _) => _download?.Cancel();
    }

    public WhyLagSettings Result { get; }

    private void UpdatePresentMon()
    {
        var exe = PresentMonLocator.Find();
        var permitted = PresentMonLocator.HasTracePermission();
        PresentMonStatus.Text = (exe is null ? T("set.frames.missing") : T("set.frames.found", Path.GetFileName(exe))) + " "
                                + (permitted ? T("set.frames.permitted") : T("set.frames.notpermitted"));
        PresentMonDownload.Content = T("set.frames.download", PresentMonLocator.Version, PresentMonLocator.DownloadMb);
        PresentMonDownload.Visibility = exe is null ? Visibility.Visible : Visibility.Collapsed;
        PresentMonPermission.Visibility = permitted ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void PresentMonDownload_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, T("set.frames.download.confirm", PresentMonLocator.Version, PresentMonLocator.DownloadMb, PresentMonLocator.License, PresentMonLocator.DownloadUrl),
                "PresentMon", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        PresentMonDownload.IsEnabled = false;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            await PresentMonLocator.DownloadAsync(http, new Progress<double>(p => PresentMonStatus.Text = T("set.downloading", p.ToString("P0", Culture))));
            FrameTimes.IsChecked = true;
            UpdatePresentMon();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            PresentMonStatus.Text = T("set.downloadfailed", ex.Message);
            PresentMonDownload.IsEnabled = true;
        }
    }

    private void PresentMonPermission_Click(object sender, RoutedEventArgs e)
    {
        var command = PresentMonLocator.GrantPermissionCommand();
        if (MessageBox.Show(this, T("set.frames.permission.confirm", command), T("set.frames.permission"),
                MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{command}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            process?.WaitForExit();
            MessageBox.Show(this, process?.ExitCode == 0 ? T("set.frames.permission.done") : T("set.frames.permission.failed"), "WhyLag");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user declined the administrator prompt.
        }
    }

    private void AiEnabled_Changed(object sender, RoutedEventArgs e)
    {
        UpdateAiPanel();
        if (AiEnabled.IsChecked == true && IsLoaded) Test_Click(sender, e);
    }

    private void UpdateAiPanel() => AiPanel.IsEnabled = AiEnabled.IsChecked == true;

    private OllamaClient? Client(TimeSpan timeout)
    {
        try
        {
            return OllamaClient.Create(AiEndpoint.Text.Trim(), timeout);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            AiConnection.Text = T("ui.ai.badaddress", ex.Message);
            return null;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Client(TimeSpan.FromSeconds(5)) is not { } client) return;
        AiConnection.Text = T("set.ai.checking");
        var version = await client.GetVersionAsync();
        if (version is null)
        {
            AiConnection.Text = T("ai.unreachable") + " " + T("ui.ai.install");
            return;
        }

        List<OllamaModel> models;
        try
        {
            models = await client.ListModelsAsync();
        }
        catch (OllamaException ex)
        {
            AiConnection.Text = ex.Message;
            return;
        }

        var local = models.Where(m => !ModelAdvisor.IsCloudModel(m.Name)).ToList();
        var cloud = models.Count - local.Count;
        AiConnection.Text = T("set.ai.connected", version, local.Count)
                            + (cloud > 0 ? " " + T("set.ai.cloudhidden", cloud) : "")
                            + (client.IsLocal ? "" : " " + T("set.ai.notlocal"))
                            + (local.Count == 0 ? " " + T("set.ai.nomodels") : "");

        var previous = AiModel.SelectedItem as string;
        var names = new List<string> { _autoModel };
        names.AddRange(local.Select(m => m.Name));
        AiModel.ItemsSource = names;
        AiModel.SelectedItem = previous is not null && names.Contains(previous) ? previous : _autoModel;
        if (_suggestion is not null && local.Any(m => ModelAdvisor.SameModel(m.Name, _suggestion.Name)))
        {
            DownloadButton.IsEnabled = false;
            DownloadStatus.Text = T("set.ai.installed");
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_suggestion is null || Client(TimeSpan.FromHours(2)) is not { } client) return;
        if (MessageBox.Show(this, T("set.ai.download.confirm", _suggestion.Name, _suggestion.DownloadGb.ToString("0.#", Culture), _suggestion.License),
                T("set.ai.download"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _download = new CancellationTokenSource();
        DownloadButton.IsEnabled = false;
        try
        {
            await foreach (var p in client.PullAsync(_suggestion.Name, _download.Token))
            {
                DownloadStatus.Text = p is { Total: > 0, Completed: { } done }
                    ? $"{p.Status} {(double)done / p.Total.Value:P0}"
                    : p.Status;
            }
            DownloadStatus.Text = T("set.done");
            AiModel.ItemsSource = new List<string> { _autoModel, _suggestion.Name };
            AiModel.SelectedItem = _suggestion.Name;
        }
        catch (Exception ex) when (ex is OllamaException or System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            DownloadStatus.Text = T("set.downloadfailed", ex.Message);
            DownloadButton.IsEnabled = true;
        }
    }

    /// <summary>Scrolls to the section named in the button's Tag.</summary>
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name } && FindName(name) is FrameworkElement card)
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + card.TranslatePoint(new Point(), Scroller).Y - 20);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string? error = null;
        if (!HotkeyManager.TryParse(Hotkey.Text, out _, out _)) error = T("set.err.hotkey");
        else if (!int.TryParse(BufferMinutes.Text, out var m) || m is < 1 or > 120) error = T("set.err.buffer");
        else if (!int.TryParse(KeepIncidents.Text, out var k) || k is < 1 or > 1000) error = T("set.err.keep");
        else if (PingEnabled.IsChecked == true && Uri.CheckHostName(PingTarget.Text.Trim()) == UriHostNameType.Unknown) error = T("set.err.target");
        else if (AiEnabled.IsChecked == true && (!Uri.TryCreate(AiEndpoint.Text.Trim(), UriKind.Absolute, out var ai) || ai.Scheme is not ("http" or "https")))
            error = T("set.err.address");
        if (error is not null)
        {
            MessageBox.Show(this, error, "WhyLag");
            return;
        }

        if (DriverLatency.IsChecked == true && !Result.DriverLatencyHelper
            && MessageBox.Show(this, T("set.latency.confirm"), T("set.latency"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            DriverLatency.IsChecked = false;

        Result.Language = LanguageBox.SelectedValue as string ?? "auto";
        Result.Theme = ThemeBox.SelectedValue as string ?? "System";
        Result.Notifications = NotifyBox.SelectedValue is NotifyMode mode ? mode : NotifyMode.Summary;
        Result.CheckForUpdates = UpdateCheck.IsChecked == true;
        Result.AutoInstallUpdates = AutoUpdate.IsChecked == true;
        Result.Temperatures = Temperatures.IsChecked == true;
        Result.RouterTraffic = RouterTraffic.IsChecked == true;
        Result.Hotkey = Hotkey.Text.Trim();
        Result.BufferMinutes = int.Parse(BufferMinutes.Text);
        Result.KeepIncidents = int.Parse(KeepIncidents.Text);
        Result.AutoDetect = AutoDetect.IsChecked == true;
        Result.AutoDetectNetwork = AutoDetectNetwork.IsChecked == true;
        if (StartWithWindows.IsChecked != StartupRegistration.IsEnabled()) StartupRegistration.Apply(StartWithWindows.IsChecked == true);
        Result.PingEnabled = PingEnabled.IsChecked == true;
        Result.PingTarget = PingTarget.Text.Trim();
        Result.FrameTimesEnabled = FrameTimes.IsChecked == true;
        Result.IgnoredApps = IgnoredApps.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Result.DriverLatencyHelper = DriverLatency.IsChecked == true;
        Result.Ai.Enabled = AiEnabled.IsChecked == true;
        Result.Ai.Endpoint = AiEndpoint.Text.Trim();
        Result.Ai.Model = AiModel.SelectedItem is string model && model != _autoModel ? model : null;
        Result.Ai.Language = "auto"; // explanations follow the app language
        DialogResult = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
    }

    private void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        var store = App.Current.Store;
        var ids = store.ListIds();
        if (ids.Count == 0 || MessageBox.Show(this, T("set.data.deleteall.confirm", ids.Count), "WhyLag",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var id in ids) store.Delete(id);
        MessageBox.Show(this, T("set.data.deleted"), "WhyLag");
    }

    private void BugReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"whylag-bugreport-{DateTime.Now:yyyyMMdd-HHmm}.zip", Filter = "ZIP|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        var app = App.Current;
        BugReport.Create(dialog.FileName, app.Settings, app.Recorder, app.Machine, app.Store);
        if (MessageBox.Show(this, T("set.data.bugreport.done", dialog.FileName), "WhyLag", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            App.OpenUrl(BugReport.NewIssueUrl);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, T("set.data.reset.confirm"), "WhyLag", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        SettingsStore.Reset();
        var defaults = new WhyLagSettings();
        SettingsStore.Save(defaults);
        App.Current.ApplySettings(defaults, restartRecorder: true);
        DialogResult = false;
    }
}
