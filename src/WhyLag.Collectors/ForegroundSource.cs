using System.Diagnostics;
using System.Runtime.InteropServices;
using WhyLag.Core;

namespace WhyLag.Collectors;

/// <summary>Records which program is in the foreground. Only the process name, never the window title.</summary>
public sealed class ForegroundSource : IMetricSource
{
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
    }

    public void Dispose()
    {
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
