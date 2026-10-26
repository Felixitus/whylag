using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhyLag.Core;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.App;

/// <summary>
/// Small always-on-top window with the most important live values. It is a normal window above the game (works
/// in borderless/windowed mode) and never injects anything into games, so anti-cheat systems are not affected.
/// Drag to move, right-click to close.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, LineHeight = 17 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        var settings = App.Current.Settings;
        Left = settings.OverlayLeft;
        Top = settings.OverlayTop;
        Title = "WhyLag overlay";

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 20, 20, 24)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Child = _text,
            ToolTip = T("overlay.tip"),
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            App.Current.UpdateSettings(s => (s.OverlayLeft, s.OverlayTop) = (Left, Top));
        };
        MouseRightButtonUp += (_, _) => App.Current.ToggleOverlay(false);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        if (App.Instance is not { IsExiting: false } app || app.Recorder?.LatestTick is not { } t)
        {
            _text.Text = "WhyLag";
            return;
        }
        string V(string key, string format = "0") => t.Get(key) is { } v ? v.ToString(format, Culture) : "–";
        var lines = new List<string>();
        if (t.Get(MetricKeys.FramesFps) is not null)
            lines.Add($"{t.FrameApp}: {V(MetricKeys.FramesFps)} fps · {V(MetricKeys.FramesMaxMs, "0.0")} ms");
        lines.Add($"CPU {V(MetricKeys.CpuTotal)} %  GPU {V(MetricKeys.Gpu3D)} %");
        if (t.Get(MetricKeys.TempCpu) is not null || t.Get(MetricKeys.TempGpu) is not null)
            lines.Add($"{V(MetricKeys.TempCpu)} °C / {V(MetricKeys.TempGpu)} °C");
        lines.Add($"Ping {V(MetricKeys.PingTargetMs)} ms  Router {V(MetricKeys.PingGatewayMs)} ms");
        _text.Text = string.Join('\n', lines);
    }
}
