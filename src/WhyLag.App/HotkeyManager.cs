using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace WhyLag.App;

/// <summary>Global hotkey via RegisterHotKey on a message-only window.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x574C; // "WL"
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private bool _registered;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("WhyLagHotkey") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
        _source.AddHook(WndProc);
    }

    public event Action? Pressed;

    /// <summary>Registers e.g. "Ctrl+Alt+L". Returns an error message, or null on success.</summary>
    public string? Register(string gesture)
    {
        Unregister();
        if (!TryParse(gesture, out var modifiers, out var key)) return $"'{gesture}' is not a valid hotkey.";
        _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(key));
        return _registered ? null : $"The hotkey {gesture} is already used by another program. Choose a different one in Settings.";
    }

    internal static bool TryParse(string gesture, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control" or "strg": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win": modifiers |= ModWin; break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out key)) return false;
                    break;
            }
        }
        return key != Key.None && modifiers != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Unregister()
    {
        if (_registered) UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
