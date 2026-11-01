using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WhyLag.Core;
using WhyLag.Core.Localization;

namespace WhyLag.App;

/// <summary>One line of the live table: metric, current value, the last minute and a short note.</summary>
internal sealed class LiveTile : Border
{
    private readonly string _key;
    private readonly Func<Tick, string?>? _note;
    private readonly TextBlock _value = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _unit = new() { FontSize = 12, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _noteText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Sparkline _spark;

    public LiveTile(string label, string key, string unit, FindingCategory category, double scale, Func<Tick, string?>? note = null)
    {
        _key = key;
        _note = note;
        Padding = new Thickness(16, 5, 16, 5);
        BorderThickness = new Thickness(0, 0, 0, 1);
        SetResourceReference(BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);

        var color = Palette.For(category);
        _spark = new Sparkline { Height = 22, Margin = new Thickness(16, 0, 16, 0), Stroke = Palette.Brush(color), Minimum = scale, VerticalAlignment = VerticalAlignment.Center };
        _unit.Text = unit;
        _unit.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _noteText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        var title = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var name = new DockPanel();
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Palette.Brush(color), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(dot, Dock.Left);
        name.Children.Add(dot);
        name.Children.Add(title);

        var value = new StackPanel { Orientation = Orientation.Horizontal };
        value.Children.Add(_value);
        value.Children.Add(_unit);

        var grid = new Grid();
        foreach (var width in new[] { new GridLength(170), new GridLength(100), new GridLength(1, GridUnitType.Star), new GridLength(220) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        grid.Children.Add(name);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        Grid.SetColumn(_spark, 2);
        grid.Children.Add(_spark);
        Grid.SetColumn(_noteText, 3);
        grid.Children.Add(_noteText);
        Child = grid;
    }

    /// <summary>Updates the line; returns false when the metric has no data (the line can be hidden).</summary>
    public bool Update(IReadOnlyList<Tick> ticks)
    {
        var values = ticks.Select(t => t.Get(_key)).ToList();
        var latest = ticks.LastOrDefault(t => t.Get(_key).HasValue);
        if (latest is null) return false;

        var v = latest.Get(_key)!.Value;
        _value.Text = Stats.Round(v).ToString(v >= 100 ? "0" : "0.#", Loc.Culture);
        var max = values.Where(x => x.HasValue).Max();
        _noteText.Text = _note?.Invoke(latest) ?? Loc.T("tile.maxnote", Stats.Round(max ?? 0).ToString("0.#", Loc.Culture), _unit.Text);
        _noteText.ToolTip = _noteText.Text;
        _spark.SetValues(values);
        return true;
    }
}
