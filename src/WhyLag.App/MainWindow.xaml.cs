using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using WhyLag.AI;
using WhyLag.Core;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.App;

public sealed record IncidentListItem(string Id, string When, string Trigger, string Headline, FindingCategory? Category);

public sealed record EvidenceRow(string Id, string Time, string Text);

public sealed record FindingView(string Id, string Title, FindingCategory Category, string CategoryLabel, string Summary,
    double Confidence, string ConfidenceText, List<EvidenceRow> Evidence, List<string> Checks)
{
    public Visibility EvidenceVisibility => Evidence.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
}

public sealed record KpiView(string Label, string Value, string Unit, string Note, FindingCategory Category);

public sealed record AiItemView(string Header, string Evidence, string Text, List<string> Steps, FindingCategory? Category);

public sealed record PatternView(string Title, FindingCategory Category, string CountText, string Range, string When,
    int[] ByHour, int[] ByWeekday, string[] HourLabels, string[] WeekdayLabels, string LatestId);

public partial class MainWindow : Window
{
    private readonly ObservableCollection<IncidentListItem> _items = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<LiveTile> _tiles;
    private Incident? _current;
    private CancellationTokenSource? _explainCts;

    private static App Host => App.Current;

    public MainWindow()
    {
        InitializeComponent();
        Icon = TrayIcon.WindowIcon();
        Logo.Source = Icon;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Culture.IetfLanguageTag);
        IncidentList.ItemsSource = _items;

        _tiles =
        [
            new LiveTile(T("tile.cpu"), MetricKeys.CpuTotal, "%", FindingCategory.Cpu, 100),
            new LiveTile(T("tile.gpu"), MetricKeys.Gpu3D, "%", FindingCategory.Gpu, 100),
            new LiveTile(T("tile.disk"), MetricKeys.DiskActive, "%", FindingCategory.Disk, 100),
            new LiveTile(T("tile.memory"), MetricKeys.MemAvailablePct, "%", FindingCategory.Memory, 100,
                t => t.Get(MetricKeys.MemAvailableMb) is { } mb ? T("tile.memory.note", (mb / 1024).ToString("0.0", Culture)) : null),
            new LiveTile(T("tile.fps"), MetricKeys.FramesFps, "fps", FindingCategory.Frames, 60,
                t => t.FrameApp is { } app ? T("tile.fps.note", app, Stats.Round(t.Get(MetricKeys.FramesMaxMs) ?? 0)) : null),
            new LiveTile(T("tile.ping"), MetricKeys.PingTargetMs, "ms", FindingCategory.Network, 50),
            new LiveTile(T("tile.router"), MetricKeys.PingGatewayMs, "ms", FindingCategory.Network, 20),
            new LiveTile(T("tile.wifi"), MetricKeys.WifiSignal, "%", FindingCategory.Network, 100,
                t => t.Get(MetricKeys.WifiRssi) is { } rssi ? T("tile.wifi.note", rssi, Stats.Round(t.Get(MetricKeys.WifiRxMbps) ?? 0)) : null),
            new LiveTile(T("tile.retrans"), MetricKeys.TcpRetransPerSec, "/s", FindingCategory.Network, 10),
            new LiveTile(T("tile.dpc"), MetricKeys.DpcTimePct, "%", FindingCategory.System, 10,
                t => t.Get(MetricKeys.DpcMaxUs) is { } us ? T("tile.dpc.note", Stats.Round(us), t.Drivers.FirstOrDefault()?.Driver ?? "–") : null),
        ];
        foreach (var tile in _tiles) LiveTiles.Children.Add(tile);

        Host.IncidentAdded += OnIncidentAdded;
        Closed += (_, _) =>
        {
            Host.IncidentAdded -= OnIncidentAdded;
            _timer.Stop();
            _explainCts?.Cancel();
        };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();

        LoadList();
        RefreshStatus();
        ShowLive();
    }

    public void Select(string id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is not null) IncidentList.SelectedItem = item;
    }

    public void RefreshStatus()
    {
        var s = Host.Settings;
        HotkeyHint.Text = Host.HotkeyError ?? T("ui.hotkeyhint", s.Hotkey);
        HowTo.Text = T("ui.howto", s.Hotkey) + " " + (s.AutoDetect ? T("ui.howto.auto") : T("ui.howto.autooff"));

        if (Host.Recorder is not { } recorder) return;
        StatusTitle.Text = recorder.Paused ? T("ui.status.paused") : T("ui.status.recording");
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            recorder.Paused ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
        StatusDetail.Text = T("ui.status.detail", recorder.BufferedSeconds / 60, recorder.BufferedSeconds % 60,
            s.Ai.Enabled ? T("ui.status.ai.on") : T("ui.status.ai.off"));

        if (LiveView.Visibility == Visibility.Visible)
        {
            var recent = recorder.RecentTicks(60);
            foreach (var tile in _tiles) tile.Visibility = tile.Update(recent) ? Visibility.Visible : Visibility.Collapsed;
            SourceWarnings.ItemsSource = recorder.Status.Where(x => x.State != SourceState.Running)
                .Select(x => $"{x.Name}: {x.Message}").ToList();
        }
    }

    private void LoadList()
    {
        _items.Clear();
        foreach (var incident in Host.Store.LoadAll()) _items.Add(ToItem(incident));
        UpdateCount();
    }

    private void UpdateCount()
    {
        IncidentCount.Text = _items.Count.ToString(Culture);
        NoIncidents.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IncidentListItem ToItem(Incident i)
    {
        var local = i.TriggeredAt.ToLocalTime();
        var when = local.Date == DateTime.Today ? local.ToString("HH:mm:ss", Culture) : local.ToString("g", Culture);
        var trigger = i.Id.EndsWith("-demo", StringComparison.Ordinal) ? T("trigger.short.demo") : T($"trigger.short.{i.Trigger}");
        var top = i.Findings.FirstOrDefault();
        return new IncidentListItem(i.Id, when, trigger, top?.Title ?? T("report.none.title"), top?.Category);
    }

    private void OnIncidentAdded(Incident incident)
    {
        _items.Insert(0, ToItem(incident));
        while (_items.Count > Math.Max(Host.Settings.KeepIncidents, 1)) _items.RemoveAt(_items.Count - 1);
        UpdateCount();
        IncidentList.SelectedIndex = 0;
    }

    private void IncidentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _explainCts?.Cancel();
        if (IncidentList.SelectedItem is IncidentListItem item && Host.Store.Load(item.Id) is { } incident) Show(incident);
    }

    private void ShowView(UIElement view)
    {
        foreach (var v in new UIElement[] { LiveView, PatternsView, ReportView }) v.Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Live_Click(object sender, RoutedEventArgs e) => ShowLive();

    private void ShowLive()
    {
        _current = null;
        IncidentList.SelectedItem = null;
        ShowView(LiveView);
        RefreshStatus();
    }

    private void Patterns_Click(object sender, RoutedEventArgs e)
    {
        _current = null;
        IncidentList.SelectedItem = null;
        ShowView(PatternsView);

        var days = Enumerable.Range(0, 7).Select(d => Culture.DateTimeFormat.GetShortestDayName((DayOfWeek)((d + 1) % 7))).ToArray();
        var hours = Enumerable.Range(0, 24).Select(h => h % 6 == 0 ? h.ToString(Culture) : "").ToArray();
        var patterns = PatternAnalyzer.Find(Host.Store.LoadAll()).Select(p => new PatternView(
            p.Title, p.Category, T("ui.patterns.count", p.Count),
            T("ui.patterns.range", p.First.ToString("d", Culture), p.Last.ToString("d", Culture)),
            p.When.Length > 0 ? p.When : T("ui.patterns.notime"),
            p.ByHour, p.ByWeekday, hours, days, p.IncidentIds[^1])).ToList();
        PatternList.ItemsSource = patterns;
        NoPatterns.Visibility = patterns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenPatternIncident_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) Select(id);
    }

    private void Show(Incident incident)
    {
        _current = incident;
        ShowView(ReportView);
        ReportView.ScrollToTop();

        var top = incident.Findings.FirstOrDefault();
        var color = Palette.For(top?.Category ?? FindingCategory.Background);
        HeroChip.Background = Palette.Brush(color, 0.16);
        HeroChipText.Foreground = Palette.Brush(color);
        HeroChipText.Text = top is null ? T("ui.hero.none") : T("ui.hero.likely", Palette.Label(top.Category));
        ReportWhen.Text = incident.TriggeredAt.ToLocalTime().ToString("D", Culture) + " · " + incident.TriggeredAt.ToLocalTime().ToString("HH:mm:ss", Culture);
        ReportTitle.Text = top?.Title ?? T("report.none.title");

        var fg = incident.Ticks.LastOrDefault(t => t.ForegroundProcess is not null)?.ForegroundProcess;
        ReportSubtitle.Text = ReportBuilder.TriggerText(incident)
                              + (fg is null ? "" : " · " + T("ui.infront", fg))
                              + " · " + T("ui.recorded", incident.Ticks.Count, incident.Findings.Count);

        Kpis.ItemsSource = BuildKpis(incident);
        Chart.Incident = incident;

        FindingsHeader.Text = incident.Findings.Count == 0 ? T("ui.findings.none") : T("ui.findings");
        FindingsList.ItemsSource = incident.Findings.Count == 0
            ? new[] { new FindingView("", T("report.none.title"), FindingCategory.Background, T("ui.result"), T("report.none.text"), 0, "", [], []) }
            : incident.Findings.Select(f => new FindingView(f.Id, f.Title, f.Category, Palette.Label(f.Category), f.Summary,
                f.Confidence * 100, T("ui.rulematch", f.Confidence.ToString("P0", Culture)),
                f.Evidence.Select(e => new EvidenceRow(e.Id, e.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), e.Description)).ToList(),
                f.Checks)).ToList();

        ShowAi(incident.Explanation, null);
    }

    /// <summary>Peak values in the analysed window, so the most important numbers are visible at a glance.</summary>
    private static List<KpiView> BuildKpis(Incident incident)
    {
        var ctx = new Core.Rules.IncidentContext(incident.Ticks, incident.Events, incident.TriggeredAt, incident.Trigger);
        var kpis = new List<KpiView>();
        void Add(string label, string key, string unit, FindingCategory category)
        {
            var series = Core.Rules.IncidentContext.Series(ctx.Focus, key);
            if (series.Count == 0) return;
            var baseline = ctx.BaselineMedian(key);
            var note = double.IsNaN(baseline) ? T("kpi.peak") : T("kpi.usually", Stats.Round(baseline).ToString("0.#", Culture), unit);
            kpis.Add(new KpiView(T(label), Stats.Round(series.Max()).ToString("0.#", Culture), unit, note, category));
        }
        Add("kpi.frame", MetricKeys.FramesMaxMs, "ms", FindingCategory.Frames);
        Add("kpi.dpc", MetricKeys.DpcMaxUs, "µs", FindingCategory.System);
        Add("kpi.cpu", MetricKeys.CpuTotal, "%", FindingCategory.Cpu);
        Add("kpi.disk", MetricKeys.DiskActive, "%", FindingCategory.Disk);
        Add("kpi.ping", MetricKeys.PingTargetMs, "ms", FindingCategory.Network);
        Add("kpi.gpu", MetricKeys.Gpu3D, "%", FindingCategory.Gpu);
        return kpis.Take(4).ToList();
    }

    private void ShowAi(AiExplanation? explanation, string? status, bool busy = false)
    {
        var ai = Host.Settings.Ai;
        var hasFindings = _current?.Findings.Count > 0;
        AiIntro.Text = !ai.Enabled ? T("ui.ai.intro.off")
            : hasFindings ? T("ui.ai.intro.on", ai.Model ?? T("ui.ai.automodel"))
            : T("ai.nothing");
        AiExplainButton.Visibility = ai.Enabled && !busy ? Visibility.Visible : Visibility.Collapsed;
        AiExplainButton.IsEnabled = hasFindings;
        AiExplainText.Text = explanation is null ? T("ui.ai.explain") : T("ui.ai.again");
        AiSettingsButton.Visibility = ai.Enabled ? Visibility.Collapsed : Visibility.Visible;
        AiCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AiBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AiBusyText.Text = status ?? "";
        AiStatus.Text = busy ? "" : status ?? "";
        AiStatus.Visibility = !busy && status is not null ? Visibility.Visible : Visibility.Collapsed;

        if (explanation is null || busy)
        {
            AiResult.Visibility = Visibility.Collapsed;
            return;
        }

        var categories = _current?.Findings.ToDictionary(f => f.Id, f => f.Category) ?? new();
        AiSummary.Text = explanation.Summary;
        AiSummary.Visibility = explanation.Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AiItems.ItemsSource = explanation.Items.Select(i => new AiItemView(
            $"{i.FindingId} · {T($"likelihood.{i.Likelihood}")}", T("ui.ai.basedon", string.Join(", ", i.EvidenceIds)), i.Explanation, i.NextSteps,
            categories.TryGetValue(i.FindingId, out var c) ? c : null)).ToList();
        AiCaveats.Text = string.Join("\n", explanation.Caveats);
        AiCaveats.Visibility = explanation.Caveats.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var notes = T("ui.ai.meta", explanation.Model, (explanation.DurationMs / 1000.0).ToString("0.#", Culture), explanation.CreatedAt.ToLocalTime().ToString("g", Culture));
        if (explanation.ValidationNotes.Count > 0) notes += "\n" + T("ui.ai.removed", explanation.ValidationNotes.Count);
        AiNotes.Text = notes;
        AiResult.Visibility = Visibility.Visible;
    }

    private async void Explain_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } incident) return;
        var settings = Host.Settings.Ai;
        OllamaClient client;
        try
        {
            client = OllamaClient.Create(settings.Endpoint, TimeSpan.FromSeconds(Math.Max(30, settings.TimeoutSeconds)));
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            ShowAi(incident.Explanation, T("ui.ai.badaddress", ex.Message));
            return;
        }
        if (!client.IsLocal && MessageBox.Show(this, T("ui.ai.remote", client.Endpoint), "WhyLag", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var cts = new CancellationTokenSource();
        _explainCts = cts;
        ShowAi(incident.Explanation, T("ui.ai.busy"), busy: true);
        try
        {
            var result = await new IncidentExplainer(client, settings).ExplainAsync(incident, cts.Token);
            if (!ReferenceEquals(_current, incident)) return;
            if (result.Explanation is { } explanation)
            {
                incident.Explanation = explanation;
                Host.Store.Save(incident);
            }
            ShowAi(incident.Explanation, result.Status switch
            {
                ExplainStatus.Ok => null,
                ExplainStatus.Unavailable => result.Message + " " + T("ui.ai.install"),
                _ => result.Message,
            });
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_current, incident)) ShowAi(incident.Explanation, T("ui.cancelled"));
        }
        finally
        {
            if (ReferenceEquals(_explainCts, cts)) _explainCts = null;
            cts.Dispose();
        }
    }

    private void CancelExplain_Click(object sender, RoutedEventArgs e) => _explainCts?.Cancel();

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (Host.Recorder is not { } recorder) return;
        if (recorder.BufferedSeconds < 5)
        {
            MessageBox.Show(this, T("ui.capture.tooearly"), "WhyLag");
            return;
        }
        Task.Run(() => recorder.Capture(TriggerKind.Manual, T("trigger.button")));
    }

    private void Demo_Click(object sender, RoutedEventArgs e) => Host.AddDemoIncident();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Host.ShowSettings();
        if (!IsLoaded) return; // the window is rebuilt when the language changed
        if (_current is not null) ShowAi(_current.Explanation, null);
        RefreshStatus();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        Clipboard.SetText(ReportBuilder.Markdown(_current, anonymize: true));
        ShowAi(_current.Explanation, T("ui.copied"));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var dialog = new SaveFileDialog { FileName = $"whylag-{_current.Id}.md", Filter = "Markdown|*.md|Text|*.txt" };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, ReportBuilder.Markdown(_current, anonymize: true));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.IncidentsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.IncidentsDir}\"") { UseShellExecute = true });
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || IncidentList.SelectedItem is not IncidentListItem item) return;
        if (MessageBox.Show(this, T("ui.delete.confirm"), "WhyLag", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Host.Store.Delete(item.Id);
        _items.Remove(item);
        UpdateCount();
        if (_items.Count > 0) IncidentList.SelectedIndex = 0;
        else ShowLive();
    }
}
