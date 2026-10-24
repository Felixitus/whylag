using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhyLag.Core;

public sealed class WhyLagSettings
{
    /// <summary>How much history is kept in memory. Nothing is written to disk unless an incident is captured.</summary>
    public int BufferMinutes { get; set; } = 10;
    public bool AutoDetect { get; set; } = true;
    public int AutoDetectCooldownSeconds { get; set; } = 120;
    public bool PingEnabled { get; set; } = true;
    /// <summary>Internet host that is pinged once per second. This is the only external connection WhyLag makes by default.</summary>
    public string PingTarget { get; set; } = "1.1.1.1";
    public string Hotkey { get; set; } = "Ctrl+Alt+L";
    /// <summary>Records frame times with PresentMon when it is available and permitted.</summary>
    public bool FrameTimesEnabled { get; set; } = true;
    /// <summary>Starts the elevated helper that names drivers with long DPC/ISR times (asks for admin approval).</summary>
    public bool DriverLatencyHelper { get; set; }
    /// <summary>"auto" uses the Windows display language; otherwise a language code such as "en" or "de".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Older incidents are deleted. Kept long enough to recognise recurring patterns.</summary>
    public int KeepIncidents { get; set; } = 100;
    public AiSettings Ai { get; set; } = new();
}

/// <summary>The local AI explainer is optional and off until the user enables it.</summary>
public sealed class AiSettings
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    public string? Model { get; set; }
    /// <summary>"auto" uses the Windows display language.</summary>
    public string Language { get; set; } = "auto";
    public int TimeoutSeconds { get; set; } = 180;
}

public static class AppPaths
{
    public static string Root { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhyLag");

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string IncidentsDir => Path.Combine(Root, "incidents");
    public static string LogsDir => Path.Combine(Root, "logs");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Single-line JSON, e.g. for streaming over a pipe.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}

public static class SettingsStore
{
    public static WhyLagSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
                return JsonSerializer.Deserialize<WhyLagSettings>(File.ReadAllText(AppPaths.ConfigFile), Json.Options) ?? new();
        }
        catch (Exception)
        {
            // A broken config file must not stop the recorder; defaults are safe.
        }
        return new WhyLagSettings();
    }

    public static void Save(WhyLagSettings settings)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = AppPaths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json.Options));
        File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
    }

    public static void Reset()
    {
        if (File.Exists(AppPaths.ConfigFile)) File.Delete(AppPaths.ConfigFile);
    }
}
