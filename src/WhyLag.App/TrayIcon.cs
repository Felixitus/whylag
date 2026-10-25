using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using static WhyLag.Core.Localization.Loc;
using Forms = System.Windows.Forms;

namespace WhyLag.App;

/// <summary>Notification-area icon with the main actions. The icon is drawn at runtime (no binary assets).</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private Action? _balloonClick;

    public TrayIcon(Action open, Action capture, Action<bool> pause, Action<bool> snooze, Action settings, Action exit)
    {
        Forms.ToolStripMenuItem? snoozeItem = null;
        snoozeItem = new Forms.ToolStripMenuItem(T("tray.snooze"), null, (_, _) =>
        {
            snoozeItem!.Checked = !snoozeItem.Checked;
            snooze(snoozeItem.Checked);
        });
        _pauseItem = new Forms.ToolStripMenuItem(T("tray.pause"), null, (_, _) =>
        {
            _pauseItem!.Checked = !_pauseItem.Checked;
            pause(_pauseItem.Checked);
        });

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem(T("tray.open"), null, (_, _) => open()) { Font = new Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(T("tray.capture"), null, (_, _) => capture());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(snoozeItem);
        menu.Items.Add(T("tray.settings"), null, (_, _) => settings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(T("tray.exit"), null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "WhyLag",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
    }

    public string Tooltip
    {
        set => _icon.Text = value.Length > 63 ? value[..63] : value;
    }

    public void Notify(string title, string text, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    public static BitmapSource WindowIcon()
    {
        using var icon = CreateIcon();
        return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    }

    /// <summary>A rounded square with a small "lag spike" line.</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            path.AddArc(1, 1, 10, 10, 180, 90);
            path.AddArc(21, 1, 10, 10, 270, 90);
            path.AddArc(21, 21, 10, 10, 0, 90);
            path.AddArc(1, 21, 10, 10, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(37, 99, 235));
            g.FillPath(fill, path);
            using var pen = new Pen(Color.White, 2.6f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new PointF[] { new(6, 19), new(11, 19), new(14, 8), new(18, 25), new(21, 16), new(26, 16) });
        }
        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
