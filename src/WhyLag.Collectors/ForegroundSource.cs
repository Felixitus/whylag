using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WhyLag.Core;

namespace WhyLag.Collectors;

/// <summary>
/// Records which program is in the foreground (process name only, never the window title) and whether its window
/// covers the whole monitor – the typical sign of a game in fullscreen or borderless mode.
/// </summary>
public sealed class ForegroundSource : IMetricSource
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd" };
    private readonly Dictionary<uint, string> _names = new();

    public string Name => WhyLag.Core.Localization.Loc.T("source.foreground");

    public void Start()
    {
    }

    public void Collect(Tick tick)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return;

        if (!_names.TryGetValue(pid, out var name))
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
            }
            catch (ArgumentException)
            {
                return; // process already exited
            }
            if (_names.Count > 500) _names.Clear();
            _names[pid] = name;
        }
        tick.ForegroundProcess = name;
        tick.ForegroundFullscreen = IsFullscreen(hwnd);
    }

    internal static bool IsFullscreen(IntPtr hwnd)
    {
        var cls = new StringBuilder(64);
        GetClassNameW(hwnd, cls, cls.Capacity);
        if (ShellClasses.Contains(cls.ToString())) return false; // the desktop (and wallpaper engines) is not a game
        if (!GetWindowRect(hwnd, out var window)) return false;
        var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return false;
        var m = info.Monitor;
        return window.Left <= m.Left && window.Top <= m.Top && window.Right >= m.Right && window.Bottom >= m.Bottom;
    }

    public void Dispose()
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
}
