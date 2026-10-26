using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using static WhyLag.Core.Localization.Loc;
using static WhyLag.Core.Rules.Fmt;

namespace WhyLag.Core.Rules;

/// <summary>One condition on a metric within a single tick.</summary>
public sealed record JsonCondition(string Metric, double? Above = null, double? Below = null)
{
    public bool Matches(Tick t) =>
        t.Get(Metric) is { } v && (Above is null || v >= Above) && (Below is null || v <= Below);
}

/// <summary>An evidence line: a text plus arguments such as "peak:cpu.total", "baseline:cpu.total" or "seconds".</summary>
public sealed record JsonEvidence(JsonElement Text, List<string>? Args = null);

public sealed record JsonBoost(List<JsonCondition> Any, double Confidence);

/// <summary>
/// A rule described as data. It matches when every condition in <see cref="When"/> holds in the same tick for at
/// least <see cref="MinSeconds"/> ticks of the focus window. Texts are either a translation key or an object with
/// one string per language code ("en" is the fallback). Title and summary get the peak value as {0}.
/// </summary>
public sealed class JsonRuleDefinition
{
    public string Id { get; set; } = "";
    public FindingCategory Category { get; set; } = FindingCategory.System;
    public List<JsonCondition> When { get; set; } = [];
    public int MinSeconds { get; set; } = 2;
    /// <summary>Metric that picks the worst tick; defaults to the first condition's metric.</summary>
    public string? Peak { get; set; }
    public double Confidence { get; set; } = 0.5;
    public JsonBoost? Boost { get; set; }
    public JsonElement Title { get; set; }
    public JsonElement Summary { get; set; }
    public List<JsonEvidence> Evidence { get; set; } = [];
    public List<JsonElement> Checks { get; set; } = [];
}

public sealed class JsonRule(JsonRuleDefinition def) : IRule
{
    public string Id => def.Id;

    public JsonRuleDefinition Definition => def;

    public Finding? Evaluate(IncidentContext ctx)
    {
        if (def.When.Count == 0) return null;
        var bad = ctx.Focus.Where(t => def.When.All(c => c.Matches(t))).ToList();
        if (bad.Count < Math.Max(1, def.MinSeconds)) return null;

        var peakKey = def.Peak ?? def.When[0].Metric;
        var worst = bad.MaxBy(t => t.Get(peakKey) ?? double.MinValue)!;
        var peak = worst.Get(peakKey) ?? 0;

        var evidence = new List<Evidence>();
        foreach (var e in def.Evidence)
        {
            var values = new List<(double Value, string? Unit)>();
            var skip = false;
            foreach (var arg in e.Args ?? [])
            {
                var (value, unit) = Resolve(arg, ctx, worst, bad.Count);
                if (double.IsNaN(value)) { skip = true; break; }
                values.Add((value, unit));
            }
            if (skip) continue;
            var at = (e.Args ?? []).Any(a => a.StartsWith("peak:", StringComparison.Ordinal)) ? worst.Timestamp : ctx.FocusStart;
            evidence.Add(Ev(at, Text(e.Text, values.Select(v => (object?)N(v.Value)).ToArray()),
                values.Count > 0 ? values[0].Value : null, values.Count > 0 ? values[0].Unit : null));
        }

        var confidence = def.Boost is { } boost && ctx.Focus.Any(t => boost.Any.Any(c => c.Matches(t))) ? boost.Confidence : def.Confidence;
        return new Finding
        {
            RuleId = Id,
            Category = def.Category,
            Title = Text(def.Title, N(peak)),
            Summary = Text(def.Summary, N(peak)),
            Confidence = Math.Clamp(confidence, 0, 1),
            Evidence = evidence,
            Checks = def.Checks.Select(c => Text(c)).ToList(),
        };
    }

    private static (double Value, string? Unit) Resolve(string arg, IncidentContext ctx, Tick worst, int seconds)
    {
        if (arg == "seconds") return (seconds, "s");
        var split = arg.IndexOf(':');
        if (split < 0) return (double.NaN, null);
        var key = arg[(split + 1)..];
        var unit = MetricKeys.Unit(key);
        return arg[..split] switch
        {
            "peak" => (worst.Get(key) ?? double.NaN, unit),
            "baseline" => (ctx.BaselineMedian(key), unit),
            "max" => (ctx.Focus.Select(t => t.Get(key)).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Max(), unit),
            _ => (double.NaN, null),
        };
    }

    /// <summary>A translation key or a per-language object, formatted with the given arguments.</summary>
    internal static string Text(JsonElement text, params object?[] args)
    {
        if (text.ValueKind == JsonValueKind.String) return T(text.GetString()!, args);
        if (text.ValueKind != JsonValueKind.Object) return "";
        var format = text.TryGetProperty(Language, out var own) ? own.GetString() ?? ""
            : text.TryGetProperty("en", out var en) ? en.GetString() ?? "" : "";
        try
        {
            return args.Length == 0 ? format : string.Format(Culture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }
}

/// <summary>Loads the built-in JSON rules (embedded) and the user's rules from <c>%LOCALAPPDATA%\WhyLag\rules\*.json</c>.</summary>
public static class JsonRules
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Problems found while loading user rules (shown in the CLI).</summary>
    public static List<string> Errors { get; } = [];

    public static IReadOnlyList<JsonRule> BuiltIn { get; } = LoadBuiltIn();

    public static List<JsonRuleDefinition> Parse(string json) =>
        JsonSerializer.Deserialize<List<JsonRuleDefinition>>(json, Options) ?? [];

    public static IReadOnlyList<JsonRule> LoadUser(string? dir = null)
    {
        dir ??= AppPaths.RulesDir;
        var rules = new List<JsonRule>();
        if (!Directory.Exists(dir)) return rules;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var def in Parse(File.ReadAllText(file)))
                {
                    if (string.IsNullOrWhiteSpace(def.Id) || def.When.Count == 0)
                        Errors.Add($"{Path.GetFileName(file)}: rule without id or conditions skipped");
                    else rules.Add(new JsonRule(def));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return rules;
    }

    private static List<JsonRule> LoadBuiltIn()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("builtin.rules.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd()).Select(d => new JsonRule(d)).ToList();
    }
}
