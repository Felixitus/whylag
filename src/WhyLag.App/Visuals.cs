using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WhyLag.Core;

namespace WhyLag.App;

/// <summary>One color per finding category, used for stripes, chips and chart lanes.</summary>
internal static class Palette
{
    public static Color For(FindingCategory category) => category switch
    {
        FindingCategory.Cpu => Color.FromRgb(59, 130, 246),
        FindingCategory.Disk => Color.FromRgb(245, 158, 11),
        FindingCategory.Memory => Color.FromRgb(168, 85, 247),
        FindingCategory.Gpu => Color.FromRgb(139, 92, 246),
        FindingCategory.Network => Color.FromRgb(20, 184, 166),
        FindingCategory.System => Color.FromRgb(239, 68, 68),
        FindingCategory.Background => Color.FromRgb(100, 116, 139),
        FindingCategory.Events => Color.FromRgb(249, 115, 22),
        FindingCategory.Frames => Color.FromRgb(236, 72, 153),
        _ => Color.FromRgb(100, 116, 139),
    };

    public static SolidColorBrush Brush(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    public static string Label(FindingCategory category) => Core.Localization.Loc.T($"category.{category}");
}

/// <summary>Maps a <see cref="FindingCategory"/> (or null) to its brush.</summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    public double Opacity { get; set; } = 1;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FindingCategory c ? Palette.Brush(Palette.For(c), Opacity) : Palette.Brush(Color.FromRgb(100, 116, 139), Opacity);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Small bar chart (e.g. incidents per hour of day) with optional labels under the bars.</summary>
internal sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<int>), typeof(BarChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelsProperty =
        DependencyProperty.Register(nameof(Labels), typeof(IReadOnlyList<string>), typeof(BarChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<int>? Values
    {
        get => (IReadOnlyList<int>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>One label per bar; empty strings are skipped.</summary>
    public IReadOnlyList<string>? Labels
    {
        get => (IReadOnlyList<string>?)GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Values is not { Count: > 0 } values || ActualWidth <= 0) return;
        var labelHeight = Labels is null ? 0 : 13;
        var plot = ActualHeight - labelHeight;
        var max = Math.Max(1, values.Max());
        var slot = ActualWidth / values.Count;
        var accent = TryFindResource("SystemAccentColor") is Color a ? a : Color.FromRgb(37, 99, 235);
        var bar = Palette.Brush(accent);
        var empty = Palette.Brush(accent, 0.15);
        var text = TryFindResource("TextFillColorTertiaryBrush") as Brush ?? Brushes.Gray;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var i = 0; i < values.Count; i++)
        {
            var h = values[i] == 0 ? 2 : Math.Max(3, values[i] / (double)max * (plot - 2));
            dc.DrawRoundedRectangle(values[i] == 0 ? empty : bar, null, new Rect(i * slot + slot * 0.15, plot - h, slot * 0.7, h), 1.5, 1.5);
            if (Labels is { } labels && i < labels.Count && labels[i].Length > 0)
            {
                var ft = new FormattedText(labels[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, text, dpi);
                dc.DrawText(ft, new Point(Math.Clamp(i * slot + slot / 2 - ft.Width / 2, 0, ActualWidth - ft.Width), plot + 1));
            }
        }
    }
}

/// <summary>Tiny line chart for live tiles.</summary>
internal sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    private IReadOnlyList<double?> _values = [];

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Upper end of the scale; values above it raise the scale.</summary>
    public double Minimum { get; set; } = 100;

    public void SetValues(IReadOnlyList<double?> values)
    {
        _values = values;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var points = _values.Count;
        if (points < 2 || ActualWidth <= 0) return;
        var max = Math.Max(Minimum, _values.Max(v => v ?? 0));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var open = false;
            for (var i = 0; i < points; i++)
            {
                if (_values[i] is not { } v)
                {
                    open = false;
                    continue;
                }
                var p = new Point(i * ActualWidth / (points - 1), ActualHeight - 1 - Math.Min(v, max) / max * (ActualHeight - 2));
                if (!open) g.BeginFigure(p, false, false);
                else g.LineTo(p, true, true);
                open = true;
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, geometry);
    }
}
