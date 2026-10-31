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

/// <summary>One entry of the incident list. <see cref="Day"/> groups the list ("Today", "Yesterday", a date).</summary>
public sealed record IncidentListItem(string Id, string Time, string Trigger, string Headline, FindingCategory? Category, string CategoryText,
    string App, string Detail, DateTime Local, bool Automatic, string Day)
{
    /// <summary>What screen readers announce for the entry.</summary>
    public override string ToString() => $"{Time} · {CategoryText} · {Headline}";
}

public sealed record EvidenceRow(string Id, string Time, string Text);

public sealed record FindingView(string Id, string Title, FindingCategory Category, string CategoryLabel, string Summary,
    double Confidence, string ConfidenceText, List<EvidenceRow> Evidence, List<string> Checks)
{
    public Visibility EvidenceVisibility => Evidence.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
}

public sealed record KpiView(string Label, string Value, string Unit, string Note, FindingCategory Category);

public sealed record AiItemView(string Header, string Evidence, string Text, List<string> Steps, FindingCategory? Category);

public sealed record SessionView(string Title, string When, string Fps, string Low, string Hitches, string Incidents, string Load, string Causes);

public sealed record ChangeView(string Title, string Detail);

public sealed record EventRow(string Time, string Level, string Source, int Id, string Message);

public sealed record PatternView(string Title, FindingCategory Category, string CountText, string Range, string When,
    int[] ByHour, int[] ByWeekday, string[] HourLabels, string[] WeekdayLabels, string LatestId);

public partial class MainWindow : Window
{
    private readonly ObservableCollection<IncidentListItem> _items = new();
    private readonly System.ComponentModel.ICollectionView _view;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<LiveTile> _tiles;
    private Incident? _current;
    private CancellationTokenSource? _explainCts;

    private static App Host => App.Current;

    /// <summary>False while WhyLag is shutting down; UI callbacks then do nothing.</summary>
    private bool Alive => App.Instance is { IsExiting: false } && IsLoaded;

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();
        Icon = TrayIcon.WindowIcon();
        Logo.Source = Icon;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Culture.IetfLanguageTag);
        IncidentList.ItemsSource = _items;
        _view = System.Windows.Data.CollectionViewSource.GetDefaultView(_items);
        _view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(IncidentListItem.Day)));
        _view.Filter = o => o is IncidentListItem i && MatchesFilter(i);

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
            new LiveTile(T("tile.tempcpu"), MetricKeys.TempCpu, "°C", FindingCategory.Cpu, 100),
            new LiveTile(T("tile.tempgpu"), MetricKeys.TempGpu, "°C", FindingCategory.Gpu, 100),
            new LiveTile(T("tile.router.traffic"), MetricKeys.RouterDownMbps, "Mbit/s", FindingCategory.Network, 10,
                t => T("tile.router.note", Stats.Round(t.Get(MetricKeys.RouterUpMbps) ?? 0), Stats.Round(t.Get(MetricKeys.NetDownMbps) ?? 0))),
            new LiveTile(T("tile.dpc"), MetricKeys.DpcTimePct, "%", FindingCategory.System, 10,
                t => t.Get(MetricKeys.DpcMaxUs) is { } us ? T("tile.dpc.note", Stats.Round(us), t.Drivers.FirstOrDefault()?.Driver ?? "–") : null),
        ];
        foreach (var tile in _tiles) LiveTiles.Children.Add(tile);

        Host.IncidentAdded += OnIncidentAdded;
        Closing += (_, _) => SavePlacement();
        Closed += (_, _) =>
        {
            Host.IncidentAdded -= OnIncidentAdded;
            _timer.Stop();
            _explainCts?.Cancel();
        };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();

        var categories = new Dictionary<string, string> { [""] = T("ui.table.allcategories") };
        foreach (var c in Enum.GetValues<FindingCategory>()) categories[c.ToString()] = Palette.Label(c);
        CategoryFilter.ItemsSource = categories;
        CategoryFilter.SelectedValue = "";
        PreviewKeyDown += Window_PreviewKeyDown;

        LoadList();
        RefreshStatus();
        ShowLive();
    }

    /// <summary>Restores the last window position and size if it is still on a connected screen.</summary>
    private void RestorePlacement()
    {
        if (Host.Settings.Window is not { } w || w.Width < MinWidth || w.Height < MinHeight) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.IntersectsWith(new Rect(w.Left, w.Top, w.Width, w.Height))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (w.Left, w.Top, w.Width, w.Height);
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (App.Instance is not { IsExiting: false } app) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        app.UpdateSettings(s => s.Window = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized));
    }

    public void Select(string id)
    {
        ShowView(IncidentsView);
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null) return;
        if (!MatchesFilter(item))
        {
            TableSearch.Text = "";
            CategoryFilter.SelectedValue = "";
        }
        IncidentList.SelectedItem = item;
        IncidentList.ScrollIntoView(item);
    }

    public void RefreshStatus()
    {
        if (!Alive) return;
        var s = Host.Settings;
        HotkeyHint.Text = Host.HotkeyError ?? T("ui.hotkeyhint", s.Hotkey);
        HowTo.Text = T("ui.howto", s.Hotkey) + " " + (s.AutoDetect ? T("ui.howto.auto") : T("ui.howto.autooff"));
        HowToCard.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateBanner.Visibility = Host.AvailableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        if (Host.AvailableUpdate is { } update) UpdateText.Text = Host.UpdateStatus ?? T("update.text", update.Version.ToString(3));
        UpdateButtons.Visibility = Host.UpdateStatus is null ? Visibility.Visible : Visibility.Collapsed;

        if (Host.Recorder is not { } recorder) return;
        StatusTitle.Text = recorder.Paused ? T("ui.status.paused") : T("ui.status.recording");
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            recorder.Paused ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
        StatusDetail.Text = T("ui.status.buffer", recorder.BufferedSeconds / 60, recorder.BufferedSeconds % 60);

        if (LiveView.Visibility == Visibility.Visible)
        {
            var recent = recorder.RecentTicks(60);
            foreach (var tile in _tiles) tile.Visibility = tile.Update(recent) ? Visibility.Visible : Visibility.Collapsed;
            SourceWarnings.ItemsSource = recorder.Status.Where(x => x.State != SourceState.Running)
                .Select(x => $"{x.Name}: {x.Message}").ToList();
        }
    }

    /// <summary>Shows the page with recorded values when no recorder runs (screenshots).</summary>
    internal void ShowPreview(IReadOnlyList<Tick> ticks)
    {
        StatusTitle.Text = T("ui.status.recording");
        StatusDetail.Text = T("ui.status.buffer", 10, 0);
        foreach (var tile in _tiles) tile.Visibility = tile.Update(ticks.TakeLast(60).ToList()) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadList()
    {
        var selected = (IncidentList.SelectedItem as IncidentListItem)?.Id;
        _items.Clear();
        foreach (var incident in Host.Store.LoadAll()) _items.Add(ToItem(incident));
        UpdateCount();
        if (_items.FirstOrDefault(i => i.Id == selected) is { } item) IncidentList.SelectedItem = item;
    }

    private void UpdateCount()
    {
        IncidentCount.Text = _items.Count.ToString(Culture);
        NoIncidents.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TableSummary.Text = T("ui.table.summary", _view.Cast<object>().Count(), _items.Count);
        RefreshToday();
    }

    /// <summary>The summary on the overview: incidents today and this week, and the most frequent cause today.</summary>
    private void RefreshToday()
    {
        var real = _items.Where(i => !i.Id.EndsWith("-demo", StringComparison.Ordinal)).ToList();
        var today = real.Where(i => i.Local.Date == DateTime.Today).ToList();
        var top = today.Where(i => i.Category is not null).GroupBy(i => i.Headline).OrderByDescending(g => g.Count()).FirstOrDefault();
        TodayCount.Text = today.Count.ToString(Culture);
        TodayAuto.Text = today.Count(i => i.Automatic).ToString(Culture);
        TodayWeek.Text = real.Count(i => i.Local >= DateTime.Today.AddDays(-6)).ToString(Culture);
        TodayText.Text = top is not null ? $"{top.Key} ({top.Count()}×)" : T("ui.today.none");
        RecentList.ItemsSource = _items.Take(5).ToList();
        RecentCard.Visibility = RecentHeader.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoisyHint.Visibility = Host.Settings is { AutoDetect: true, AutoDetectNetwork: true }
                               && today.Count(i => i.Automatic && i.Category == FindingCategory.Network) >= 10
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool MatchesFilter(IncidentListItem item)
    {
        var text = TableSearch.Text.Trim();
        var category = CategoryFilter.SelectedValue as string ?? "";
        return (category == "" || item.Category?.ToString() == category)
               && (text.Length == 0 || item.Headline.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                                    || item.App.Contains(text, StringComparison.CurrentCultureIgnoreCase));
    }

    private static IncidentListItem ToItem(Incident i)
    {
        var local = i.TriggeredAt.ToLocalTime();
        var trigger = i.Id.EndsWith("-demo", StringComparison.Ordinal) ? T("trigger.short.demo") : T($"trigger.short.{i.Trigger}");
        var top = i.Findings.FirstOrDefault();
        var app = i.Ticks.LastOrDefault(t => t.ForegroundProcess is not null)?.ForegroundProcess ?? "";
        var flags = (i.Explanation is null ? "" : " · AI") + (string.IsNullOrWhiteSpace(i.Note) ? "" : " · ✎");
        return new IncidentListItem(i.Id, local.ToString("HH:mm:ss", Culture), trigger, top?.Title ?? T("report.none.title"), top?.Category,
            top is null ? T("ui.result") : Palette.Label(top.Category), app, (app.Length > 0 ? $"{app} · {trigger}" : trigger) + flags,
            local.DateTime, i.Trigger == TriggerKind.Automatic, DayLabel(local.DateTime));
    }

    private static string DayLabel(DateTime local) =>
        local.Date == DateTime.Today ? T("ui.day.today")
        : local.Date == DateTime.Today.AddDays(-1) ? T("ui.day.yesterday")
        : local.ToString("D", Culture);

    private void OnIncidentAdded(Incident incident)
    {
        var item = ToItem(incident);
        if (_items.Count > 0 && _items[0].Local.Date != item.Local.Date)
        {
            // First incident of a new day: relabel the days ("Today" became "Yesterday") and put the new group on top.
            var all = _items.Select(i => i with { Day = DayLabel(i.Local) }).Prepend(item).ToList();
            _items.Clear();
            foreach (var i in all) _items.Add(i);
        }
        else _items.Insert(0, item);
        while (_items.Count > Math.Max(Host.Settings.KeepIncidents, 1)) _items.RemoveAt(_items.Count - 1);
        UpdateCount();
        // An automatic incident must not pull the user away from what they are looking at.
        if (incident.Trigger != TriggerKind.Automatic) Select(item.Id);
    }

    private void IncidentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_current is not null) NoteBox_LostFocus(this, new RoutedEventArgs()); // keep an unsaved note
        _explainCts?.Cancel();
        var count = IncidentList.SelectedItems.Count;
        SelectionBar.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = T("ui.table.selected", count);
        if (IncidentList.SelectedItem is IncidentListItem item && Host.Store.Load(item.Id) is { } incident) Show(incident);
        else if (count == 0) ShowNoReport();
    }

    private void ShowNoReport()
    {
        _current = null;
        ReportPane.Visibility = Visibility.Collapsed;
        ReportEmpty.Visibility = Visibility.Visible;
    }

    private void IncidentList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete)
        {
            DeleteSelected_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ShowView(UIElement view)
    {
        foreach (var (page, nav) in new (UIElement, RadioButton)[] { (LiveView, LiveButton), (IncidentsView, TableButton), (PatternsView, PatternsButton), (SessionsView, SessionsButton) })
        {
            page.Visibility = page == view ? Visibility.Visible : Visibility.Collapsed;
            if (page == view) nav.IsChecked = true;
        }
    }

    private void Live_Click(object sender, RoutedEventArgs e) => ShowLive();

    private void ShowLive()
    {
        ShowView(LiveView);
        RefreshToday();
        RefreshStatus();
    }

    private void Incidents_Click(object sender, RoutedEventArgs e)
    {
        ShowView(IncidentsView);
        if (IncidentList.SelectedItem is null && !_view.IsEmpty) IncidentList.SelectedIndex = 0;
    }

    private void Patterns_Click(object sender, RoutedEventArgs e)
    {
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
        _ = LoadChangesAsync();
    }

    private void TableFilter_Changed(object sender, EventArgs e)
    {
        if (!IsInitialized || _view is null) return;
        SearchHint.Visibility = TableSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        TableSummary.Text = T("ui.table.summary", _view.Cast<object>().Count(), _items.Count);
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = IncidentList.SelectedItems.OfType<IncidentListItem>().ToList();
        if (selected.Count == 0) return;
        if (MessageBox.Show(this, T("ui.table.deleteconfirm", selected.Count), "WhyLag", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var item in selected)
        {
            Host.Store.Delete(item.Id);
            _items.Remove(item);
        }
        UpdateCount();
        if (!_view.IsEmpty) IncidentList.SelectedIndex = 0;
        else ShowNoReport();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.FocusedElement is TextBox) return;
        var ctrl = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
        switch (e.Key)
        {
            case System.Windows.Input.Key.F5:
                LoadList();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Delete when ReportPane.Visibility == Visibility.Visible && IncidentsView.Visibility == Visibility.Visible:
                Delete_Click(this, e);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.C when ctrl && ReportPane.Visibility == Visibility.Visible && IncidentsView.Visibility == Visibility.Visible:
                Copy_Click(this, e);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Reads driver/Windows updates from the event log (can take a moment) and compares incident rates.</summary>
    private async Task LoadChangesAsync()
    {
        ChangesStatus.Text = T("ui.changes.loading");
        ChangeList.ItemsSource = null;
        var incidents = Host.Store.LoadAll();
        var impacts = await Task.Run(() => ChangeAnalyzer.Compare(Collectors.SystemChangeReader.Read(TimeSpan.FromDays(90)), incidents, DateTimeOffset.Now));
        if (!Alive) return;
        ChangeList.ItemsSource = impacts.Select(i => new ChangeView(
            T(i.Change.Kind == ChangeKind.Driver ? "ui.changes.driver" : "ui.changes.update", i.Change.Description, i.Change.At.ToLocalTime().ToString("d", Culture)),
            T("ui.changes.detail", i.After, i.DaysAfter.ToString("0", Culture), i.Before, i.DaysBefore.ToString("0", Culture)))).ToList();
        ChangesStatus.Text = impacts.Count == 0 ? T("ui.changes.none") : "";
    }

    private void Sessions_Click(object sender, RoutedEventArgs e)
    {
        ShowView(SessionsView);
        var sessions = Host.Sessions.LoadAll();
        SessionList.ItemsSource = sessions.Select(s => new SessionView(
            T("session.title", s.App, App.Duration(s.Duration)),
            s.Start.ToLocalTime().ToString("g", Culture),
            s.AverageFps.ToString("0", Culture), s.LowFps.ToString("0", Culture), s.Hitches.ToString(Culture),
            s.IncidentIds.Count.ToString(Culture), $"{s.AverageCpu:0} / {s.AverageGpu:0} %",
            s.Causes.Count == 0 ? T("session.nocauses") : T("session.causes", string.Join(" · ", s.Causes.Select(c => $"{c.Title} ({c.Count}×)"))))).ToList();
        var running = Host.Recorder?.CurrentSessionApp;
        SessionStatus.Text = running is not null ? T("session.running", running) : sessions.Count == 0 ? T("session.none") : "";
        SessionStatus.Visibility = SessionStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        App.OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void Update_Click(object sender, RoutedEventArgs e) => _ = Host.InstallUpdateAsync();

    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (Host.AvailableUpdate is { } update) App.OpenUrl(update.PageUrl);
    }

    private void OpenPatternIncident_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) Select(id);
    }

    private void Show(Incident incident)
    {
        _current = incident;
        ShowView(IncidentsView);
        ReportEmpty.Visibility = Visibility.Collapsed;
        ReportPane.Visibility = Visibility.Visible;

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

        EventsTable.ItemsSource = incident.Events.OrderBy(ev => ev.Timestamp).Select(ev => new EventRow(
            ev.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), T($"level.{ev.Level}"), ev.Provider, ev.EventId,
            ev.Message.ReplaceLineEndings(" "))).ToList();
        EventsTable.Visibility = incident.Events.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoEvents.Visibility = incident.Events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EventsCount.Text = T("ui.events.count", incident.Events.Count);
        NoteBox.Text = incident.Note ?? "";

        // Offer to ignore an app whose hitches started this incident (e.g. a browser or a wallpaper engine).
        var frameApp = incident.Findings.FirstOrDefault(f => f.Category == FindingCategory.Frames)?.Subject
                       ?? (incident.Trigger == TriggerKind.Automatic ? incident.Ticks.LastOrDefault(t => t.FrameApp is not null)?.FrameApp : null);
        IgnoreAppButton.Tag = frameApp;
        IgnoreAppButton.Visibility = frameApp is null || Host.Settings.IgnoredApps.Contains(frameApp, StringComparer.OrdinalIgnoreCase)
            ? Visibility.Collapsed : Visibility.Visible;
        if (frameApp is not null) IgnoreAppText.Text = T("ui.ignoreapp", frameApp);

        ShowAi(incident.Explanation, null);
        ReportView.ScrollToTop();
    }

    private void NoteBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_current is not { } incident || !Alive) return;
        var note = NoteBox.Text.Trim();
        if (note == (incident.Note ?? "")) return;
        incident.Note = note.Length == 0 ? null : note;
        Host.Store.Save(incident);
    }

    private void IgnoreApp_Click(object sender, RoutedEventArgs e)
    {
        if (IgnoreAppButton.Tag is not string app) return;
        if (MessageBox.Show(this, T("ui.ignoreapp.confirm", app), "WhyLag", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Host.UpdateSettings(s => s.IgnoredApps.Add(app));
        IgnoreAppButton.Visibility = Visibility.Collapsed;
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
            if (!Alive || !ReferenceEquals(_current, incident)) return;
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
        if (!Alive) return; // shutting down, or the window was rebuilt because the language changed
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
        // One export for both formats: HTML (with timeline, opens in the browser) or Markdown (for forums and issues).
        var dialog = new SaveFileDialog { FileName = $"whylag-{_current.Id}", Filter = "HTML|*.html|Markdown|*.md|Text|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        if (dialog.FilterIndex == 1)
        {
            File.WriteAllText(dialog.FileName, HtmlReport.Build(_current));
            App.OpenFile(dialog.FileName);
        }
        else File.WriteAllText(dialog.FileName, ReportBuilder.Markdown(_current, anonymize: true));
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
        if (!_view.IsEmpty) IncidentList.SelectedIndex = 0;
        else ShowNoReport();
    }
}
