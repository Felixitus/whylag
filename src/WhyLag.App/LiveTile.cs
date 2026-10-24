using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WhyLag.Core;
using WhyLag.Core.Localization;

namespace WhyLag.App;

/// <summary>A card with the current value of one metric and a sparkline of the last minute.</summary>
internal sealed class LiveTile : Border
{
    private readonly string _key;
    private readonly Func<Tick, string?>? _note;
    private readonly TextBlock _value = new() { FontSize = 28, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _unit = new() { FontSize = 13, Margin = new Thickness(4, 0, 0, 4), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _noteText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Sparkline _spark;

    public LiveTile(string label, string key, string unit, FindingCategory category, double scale, Func<Tick, string?>? note = null)
    {
        _key = key;
        _note = note;
        Style = (Style)Application.Current.FindResource("Card");
        MinWidth = 180;
        Margin = new Thickness(0, 0, 12, 12);
        Padding = new Thickness(16, 12, 16, 12);

        var color = Palette.For(category);
        _spark = new Sparkline { Height = 36, Margin = new Thickness(0, 8, 0, 0), Stroke = Palette.Brush(color), Minimum = scale };
        _unit.Text = unit;
        _unit.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _noteText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        var title = new TextBlock { Text = label, FontSize = 12 };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Palette.Brush(color), Margin = new Thickness(0, 0, 6, 0) });
        header.Children.Add(title);

        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        valueRow.Children.Add(_value);
        valueRow.Children.Add(_unit);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(valueRow);
        panel.Children.Add(_noteText);
        panel.Children.Add(_spark);
        Child = panel;
    }

    /// <summary>Updates the tile; returns false when the metric has no data (tile can be hidden).</summary>
    public bool Update(IReadOnlyList<Tick> ticks)
    {
        var values = ticks.Select(t => t.Get(_key)).ToList();
        var latest = ticks.LastOrDefault(t => t.Get(_key).HasValue);
        if (latest is null) return false;

        var v = latest.Get(_key)!.Value;
        _value.Text = Stats.Round(v).ToString(v >= 100 ? "0" : "0.#", Loc.Culture);
        var max = values.Where(x => x.HasValue).Max();
        _noteText.Text = _note?.Invoke(latest) ?? Loc.T("tile.maxnote", Stats.Round(max ?? 0).ToString("0.#", Loc.Culture), _unit.Text);
        _spark.SetValues(values);
        return true;
    }
}
