using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhyLag.Core;

namespace WhyLag.App;

/// <summary>
/// <c>WhyLag.exe --screenshots &lt;folder&gt; [--theme Light|Dark] [--lang en] [--size 1320x900]</c> renders every view
/// to PNG files, with sample data in a temporary folder. Used for the images in docs/img and to check layouts; it
/// never touches real incidents and works without a visible desktop.
/// </summary>
internal static class ScreenshotMode
{
    public static string TemporaryRoot => Path.Combine(Path.GetTempPath(), "WhyLag-screenshots");

    public static async Task RunAsync(App app, string folder, int width, int height)
    {
        Directory.CreateDirectory(folder);
        var demo = Seed(app);

        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            Width = width,
            Height = height,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        window.ShowPreview(demo.Ticks);
        await Settle();

        foreach (var (name, button) in new (string, RadioButton)[]
                 {
                     ("overview", window.LiveButton), ("patterns", window.PatternsButton), ("sessions", window.SessionsButton),
                 })
        {
            button.IsChecked = true;
            window.ShowPreview(demo.Ticks);
            await Settle();
            Save(window, Path.Combine(folder, $"{name}.png"));
        }

        window.TableButton.IsChecked = true;
        await Settle();
        window.Select(demo.Id);
        await Settle();
        Save(window, Path.Combine(folder, "report.png"));
        window.ReportView.ScrollToBottom();
        await Settle();
        Save(window, Path.Combine(folder, "report-end.png"));

        window.CategoryFilter.SelectedValue = FindingCategory.Disk.ToString();
        await Settle();
        window.IncidentList.SelectAll();
        await Settle();
        Save(window, Path.Combine(folder, "incidents-filtered.png"));

        window.Close();

        var settings = new SettingsWindow(app.Settings)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            Width = Math.Min(width, 1000),
            Height = height,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        settings.Show();
        await Settle();
        foreach (var page in settings.PageNames)
        {
            settings.ShowPage(page);
            await Settle();
            Save(settings, Path.Combine(folder, $"settings-{page}.png"));
        }
        settings.Close();
    }

    private static async Task Settle()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(250);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string path)
    {
        if (window.Content is not FrameworkElement content) return;
        var width = content.ActualWidth;
        var height = content.ActualHeight;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)(width * dpi.DpiScaleX), (int)(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background is SolidColorBrush { Color.A: 255 } b ? b : Brushes.Black, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Fills the temporary folder with incidents over the last days and a few sessions.</summary>
    private static Incident Seed(App app)
    {
        var demo = DemoIncident.Create(app.Machine);
        var random = new Random(7);
        var json = JsonSerializer.Serialize(demo, Json.Options);
        var hours = new[] { 21, 21, 22, 20, 21, 22, 21, 18, 21, 22, 20 };
        for (var i = 0; i < 16; i++)
        {
            var when = new DateTimeOffset(DateTime.Today.AddDays(-(i / 3))).AddHours(i < 3 ? 8 + i * 2 : hours[i % hours.Length]).AddMinutes(random.Next(59)).AddSeconds(random.Next(59));
            if (when > DateTimeOffset.Now) when = DateTimeOffset.Now.AddMinutes(-20 - i * 7);
            var node = JsonNode.Parse(json)!;
            var trigger = i % 4 == 0 ? TriggerKind.Hotkey : TriggerKind.Automatic;
            node["id"] = $"{when:yyyyMMdd-HHmmss}-{trigger.ToString().ToLowerInvariant()}";
            node["triggeredAt"] = when.ToString("O");
            node["trigger"] = trigger.ToString();
            if (node["findings"] is JsonArray findings && findings.Count > 1 && i % 3 == 1)
            {
                // A different cause first, so the list and the patterns are not all the same.
                var first = findings[1]!.DeepClone();
                findings.RemoveAt(1);
                findings.Insert(0, first);
            }
            app.Store.Save(node.Deserialize<Incident>(Json.Options)!);
        }
        app.Store.Save(demo);

        foreach (var (name, days, fps, low, hitches, minutes) in new[] { ("Deadlock", 0, 214.0, 96.0, 3, 74), ("cs2", 1, 322.0, 148.0, 1, 41), ("Cyberpunk2077", 3, 71.0, 38.0, 12, 128) })
        {
            var start = DateTimeOffset.Now.AddDays(-days).AddHours(-3);
            app.Sessions.Save(new GameSession
            {
                Id = $"{start:yyyyMMdd-HHmmss}",
                App = name,
                Start = start,
                End = start.AddMinutes(minutes),
                AverageFps = fps,
                LowFps = low,
                Hitches = hitches,
                AverageCpu = 42,
                AverageGpu = 88,
            });
        }
        return demo;
    }
}
