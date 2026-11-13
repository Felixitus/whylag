using System.Windows;
using System.Windows.Controls;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.App;

/// <summary>Asks for one short line of text.</summary>
internal sealed class InputDialog : Window
{
    private readonly TextBox _box;

    public string Value => _box.Text.Trim();

    public InputDialog(string title, string prompt, int maxLength)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");

        _box = new TextBox { MaxLength = maxLength, Margin = new Thickness(0, 10, 0, 16) };
        System.Windows.Automation.AutomationProperties.SetName(_box, prompt);
        var ok = new Button { Content = T("ui.ok"), IsDefault = true, Padding = new Thickness(18, 6, 18, 6), Style = (Style)FindResource("PrimaryButton"), IsEnabled = false };
        var cancel = new Button { Content = T("ui.cancel"), IsCancel = true, Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => DialogResult = true;
        _box.TextChanged += (_, _) => ok.IsEnabled = Value.Length > 0;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Body") });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _box.Focus();
    }
}
