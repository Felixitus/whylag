using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WhyLag.Core;

namespace WhyLag.App;

/// <summary>
/// Stacked lanes over the recorded seconds with the analysed window shaded and the trigger marked.
/// Lanes without data (no Wi-Fi, no frame times) are left out. Colors follow the light/dark theme.
/// </summary>
internal sealed class TimelineChart : FrameworkElement
{
    private sealed record Lane(string Key, string Label, FindingCategory Category, double Scale, bool Inverted = false);

    private static readonly Lane[] Lanes =
    [
        new(MetricKeys.FramesMaxMs, "chart.frames", FindingCategory.Frames, 50),
        new(MetricKeys.CpuTotal, "chart.cpu", FindingCategory.Cpu, 100),
        new(MetricKeys.DiskActive, "chart.disk", FindingCategory.Disk, 100),
        new(MetricKeys.Gpu3D, "chart.gpu", FindingCategory.Gpu, 100),
        new(MetricKeys.PingTargetMs, "chart.ping", FindingCategory.Network, 100),
        new(MetricKeys.WifiSignal, "chart.wifi", FindingCategory.Network, 100),
    ];

    private const double LabelWidth = 64;
    private const double LaneGap = 6;
    private const double AxisHeight = 18;
    private Incident? _incident;

    public TimelineChart()
    {
        // Re-render when the user switches between light and dark mode.
        Loaded += (_, _) => InvalidateVisual();
    }

    public Incident? Incident
    {
        get => _incident;
        set
        {
            _incident = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private List<Lane> VisibleLanes() =>
        _incident is null ? [] : Lanes.Where(l => _incident.Ticks.Any(t => t.Get(l.Key).HasValue)).ToList();

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, Math.Max(1, VisibleLanes().Count) * 42 + AxisHeight);

    private Brush Res(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    protected override void OnRender(DrawingContext dc)
    {
        var text = Res("TextFillColorSecondaryBrush", Color.FromRgb(94, 104, 120));
        var faint = Res("TextFillColorTertiaryBrush", Color.FromRgb(140, 140, 140));
        var grid = new Pen(Res("DividerStrokeColorDefaultBrush", Color.FromRgb(226, 229, 234)), 1);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var ticks = _incident?.Ticks;
        var lanes = VisibleLanes();
        if (ticks is null || ticks.Count < 2 || lanes.Count == 0)
        {
            dc.DrawText(Text(Core.Localization.Loc.T("chart.nodata"), text, dpi, 12), new Point(0, 8));
            return;
        }

        var start = ticks[0].Timestamp;
        var span = Math.Max(1, (ticks[^1].Timestamp - start).TotalSeconds);
        var plotWidth = Math.Max(10, ActualWidth - LabelWidth);
        var plotHeight = ActualHeight - AxisHeight;
        var laneHeight = (plotHeight - LaneGap * (lanes.Count - 1)) / lanes.Count;
        double X(DateTimeOffset t) => LabelWidth + (t - start).TotalSeconds / span * plotWidth;

        var ctx = new Core.Rules.IncidentContext([], [], _incident!.TriggeredAt, _incident.Trigger);
        var x1 = Math.Clamp(X(ctx.FocusStart), LabelWidth, ActualWidth);
        var x2 = Math.Clamp(X(ctx.FocusEnd), LabelWidth, ActualWidth);
        var accent = TryFindResource("SystemAccentColor") is Color a ? a : Color.FromRgb(37, 99, 235);
        dc.DrawRoundedRectangle(Palette.Brush(accent, 0.10), null, new Rect(x1, 0, Math.Max(0, x2 - x1), plotHeight), 4, 4);

        for (var i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i];
            var top = i * (laneHeight + LaneGap);
            var values = ticks.Select(t => (t.Timestamp, Value: t.Get(lane.Key))).ToList();
            var max = Math.Max(lane.Scale, values.Max(v => v.Value ?? 0));
            var color = Palette.For(lane.Category);

            dc.DrawLine(grid, new Point(LabelWidth, top + laneHeight), new Point(ActualWidth, top + laneHeight));
            dc.DrawText(Text(Core.Localization.Loc.T(lane.Label), text, dpi, 12), new Point(0, top + laneHeight / 2 - 9));
            var maxText = Text(max.ToString("0", CultureInfo.InvariantCulture), faint, dpi, 9);
            dc.DrawText(maxText, new Point(LabelWidth - maxText.Width - 6, top));

            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var g = line.Open())
            using (var f = area.Open())
            {
                Point? first = null, last = null;
                foreach (var (time, value) in values)
                {
                    if (value is not { } v)
                    {
                        CloseArea(f, first, last, top + laneHeight);
                        first = last = null;
                        continue;
                    }
                    var p = new Point(X(time), top + laneHeight - Math.Min(v, max) / max * (laneHeight - 2));
                    if (first is null)
                    {
                        g.BeginFigure(p, false, false);
                        f.BeginFigure(new Point(p.X, top + laneHeight), true, true);
                        f.LineTo(p, false, false);
                        first = p;
                    }
                    else
                    {
                        g.LineTo(p, true, true);
                        f.LineTo(p, false, false);
                    }
                    last = p;
                }
                CloseArea(f, first, last, top + laneHeight);
            }
            line.Freeze();
            area.Freeze();
            dc.DrawGeometry(Palette.Brush(color, 0.10), null, area);
            dc.DrawGeometry(null, new Pen(Palette.Brush(color), 1.4) { LineJoin = PenLineJoin.Round }, line);

            if (lane.Key == MetricKeys.PingTargetMs)
            {
                foreach (var lost in ticks.Where(t => t.Get(MetricKeys.PingTargetLost) >= 1))
                    dc.DrawEllipse(Palette.Brush(Palette.For(FindingCategory.System)), null, new Point(X(lost.Timestamp), top + laneHeight - 3), 3, 3);
            }
        }

        var triggerX = X(_incident.TriggeredAt);
        if (triggerX >= LabelWidth && triggerX <= ActualWidth + 1)
        {
            var pen = new Pen(Palette.Brush(Palette.For(FindingCategory.System)), 1.5) { DashStyle = DashStyles.Dash };
            dc.DrawLine(pen, new Point(triggerX, 0), new Point(triggerX, plotHeight));
        }

        dc.DrawText(Text(start.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), faint, dpi, 10), new Point(LabelWidth, plotHeight + 3));
        var endText = Text(ticks[^1].Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), faint, dpi, 10);
        dc.DrawText(endText, new Point(ActualWidth - endText.Width, plotHeight + 3));
    }

    private static void CloseArea(StreamGeometryContext f, Point? first, Point? last, double baseline)
    {
        if (first is null || last is null) return;
        f.LineTo(new Point(last.Value.X, baseline), false, false);
    }

    private static FormattedText Text(string text, Brush brush, double dpi, double size) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Text, Segoe UI"), size, brush, dpi);
}
