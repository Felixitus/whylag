using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace WhyLag.App;

/// <summary>
/// The WhyLag palette. WPF's Fluent theme supplies the controls; these colors replace its brushes by key, so every
/// view gets the same calm, neutral look in light and dark mode, independent of the Windows accent color.
/// </summary>
internal static class Theme
{
    private sealed record Tokens(
        string Background, string Rail, string Surface, string Hover, string Selected, string Stroke, string Divider,
        string Text, string TextSecondary, string TextTertiary, string Accent, string AccentHover, string OnAccent,
        string Success, string Caution, string CautionSoft, string Critical);

    private static readonly Tokens Dark = new(
        Background: "#14161A", Rail: "#101216", Surface: "#1B1E23", Hover: "#23272E", Selected: "#2A2F38", Stroke: "#2E333B", Divider: "#262A31",
        Text: "#ECEEF1", TextSecondary: "#A8AFBA", TextTertiary: "#7E8693", Accent: "#4C8DFF", AccentHover: "#6AA1FF", OnAccent: "#0C1320",
        Success: "#3DBE8B", Caution: "#E5B045", CautionSoft: "#2C2619", Critical: "#F0696A");

    private static readonly Tokens Light = new(
        Background: "#F5F6F8", Rail: "#FBFBFC", Surface: "#FFFFFF", Hover: "#EFF1F4", Selected: "#E6EAF1", Stroke: "#DFE3E8", Divider: "#E9ECF0",
        Text: "#14181F", TextSecondary: "#515B69", TextTertiary: "#69727F", Accent: "#1F5FDB", AccentHover: "#174EB8", OnAccent: "#FFFFFF",
        Success: "#128358", Caution: "#9A6700", CautionSoft: "#FDF5E3", Critical: "#C5302F");

    public static bool IsDark { get; private set; }

    /// <summary>Sets the colors for "Light", "Dark" or "System".</summary>
    public static void Apply(string theme)
    {
        IsDark = theme switch { "Light" => false, "Dark" => true, _ => SystemUsesDark() };
        var t = IsDark ? Dark : Light;
        var resources = Application.Current.Resources;

        void Brush(string value, params string[] keys)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            foreach (var key in keys) resources[key] = brush;
        }

        Brush(t.Background, "AppBackgroundBrush", "ApplicationPageBackgroundThemeBrush");
        Brush(t.Rail, "RailBackgroundBrush", "LayerFillColorDefaultBrush");
        Brush(t.Surface, "CardBackgroundFillColorDefaultBrush");
        Brush(t.Hover, "HoverFillBrush", "SubtleFillColorSecondaryBrush");
        Brush(t.Selected, "SelectedFillBrush");
        Brush(t.Stroke, "CardStrokeColorDefaultBrush");
        Brush(t.Divider, "DividerStrokeColorDefaultBrush");
        Brush(t.Text, "TextFillColorPrimaryBrush");
        Brush(t.TextSecondary, "TextFillColorSecondaryBrush");
        Brush(t.TextTertiary, "TextFillColorTertiaryBrush");
        Brush(t.Accent, "AccentFillColorDefaultBrush", "AccentTextFillColorPrimaryBrush");
        Brush(t.AccentHover, "AccentFillColorSecondaryBrush");
        Brush(t.OnAccent, "TextOnAccentFillColorPrimaryBrush");
        Brush(t.Success, "SystemFillColorSuccessBrush");
        Brush(t.Caution, "SystemFillColorCautionBrush");
        Brush(t.CautionSoft, "SystemFillColorCautionBackgroundBrush");
        Brush(t.Critical, "SystemFillColorCriticalBrush");
        resources["SystemAccentColor"] = (Color)ColorConverter.ConvertFromString(t.Accent);
    }

    private static bool SystemUsesDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }
}

/// <summary>One line in the settings: title and explanation on the left, the control on the right.</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow));

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
