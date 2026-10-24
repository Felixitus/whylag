using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace WhyLag.Core.Localization;

/// <summary>
/// Translations for all user-facing text. Each language is a flat JSON file (key → .NET format string) embedded
/// from <c>Localization/*.json</c>; adding a language means adding one file. Missing keys fall back to English.
/// </summary>
public static class Loc
{
    private static readonly Dictionary<string, Dictionary<string, string>> Languages = Load();
    private static Dictionary<string, string> _current = Languages["en"];

    public static string Language { get; private set; } = "en";

    /// <summary>Culture used for numbers and dates in texts.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    public static IReadOnlyCollection<string> Available => Languages.Keys;

    /// <summary>Display name of a language in that language, e.g. "Deutsch".</summary>
    public static string NativeName(string code) =>
        Languages.TryGetValue(code, out var d) && d.TryGetValue("language.name", out var n) ? n : code;

    /// <summary>"auto" picks the Windows display language when it is available, otherwise English.</summary>
    public static void SetLanguage(string? code)
    {
        if (code is null or "" or "auto") code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (!Languages.ContainsKey(code)) code = "en";
        Language = code;
        _current = Languages[code];
        Culture = code == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo(code);
    }

    public static string T(string key, params object?[] args)
    {
        var format = _current.TryGetValue(key, out var s) ? s : Languages["en"].TryGetValue(key, out var en) ? en : key;
        return args.Length == 0 ? format : string.Format(Culture, format, args);
    }

    /// <summary>All keys of a language (used by tests to check that translations are complete).</summary>
    public static IReadOnlyDictionary<string, string> Strings(string code) => Languages[code];

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.Ordinal)))
        {
            var code = Path.GetFileNameWithoutExtension(name).Split('.').Last();
            using var stream = assembly.GetManifestResourceStream(name)!;
            result[code] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }
        return result;
    }
}
