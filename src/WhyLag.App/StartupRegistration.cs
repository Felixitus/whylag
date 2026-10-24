using Microsoft.Win32;

namespace WhyLag.App;

/// <summary>
/// Optional autostart through the per-user Run key (no admin rights, easy to remove). The registry value itself is
/// the setting, so the installer's "start with Windows" option and the checkbox in Settings always agree.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WhyLag";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled && Environment.ProcessPath is { } exe) key.SetValue(ValueName, $"\"{exe}\"");
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            App.Log(ex);
        }
    }
}
