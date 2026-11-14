using System.Globalization;

namespace WhyLag.Core.Rules;

/// <summary>A deterministic check. Rules must only report what the recorded data shows.</summary>
public interface IRule
{
    string Id { get; }
    Finding? Evaluate(IncidentContext ctx);
}

public static class RuleEngine
{
    /// <summary>
    /// Rules written in code, the built-in JSON rules and the user's own JSON rules. The user's rule folder is read
    /// again when a file in it was added, changed or removed, so edited rules apply without a restart.
    /// </summary>
    public static IReadOnlyList<IRule> DefaultRules
    {
        get
        {
            var stamp = JsonRules.UserStamp();
            if (_rules is null || stamp != _stamp)
            {
                JsonRules.Errors.Clear();
                _rules = [.. CodeRules, .. JsonRules.BuiltIn, .. JsonRules.LoadUser()];
                _stamp = stamp;
            }
            return _rules;
        }
    }

    private static IReadOnlyList<IRule>? _rules;
    private static string? _stamp;

    /// <summary>Reads the user's rule folder again on the next evaluation.</summary>
    public static void ReloadUserRules()
    {
        JsonRules.Errors.Clear();
        _rules = null;
    }

    public static IReadOnlyList<IRule> CodeRules { get; } =
    [
        new CpuSaturationRule(),
        new CpuThrottlingRule(),
        new DiskSaturationRule(),
        new MemoryPressureRule(),
        new GpuSaturationRule(),
        new GpuBackgroundRule(),
        new BackgroundTaskRule(),
        new LocalNetworkRule(),
        new RemoteNetworkRule(),
        new BufferbloatRule(),
        new SystemStallRule(),
        new EventLogRule(),
        new FrameStutterRule(),
        new WifiRule(),
        new DriverLatencyRule(),
        new MemoryGrowthRule(),
        new TemperatureRule(),
        new HouseholdTrafficRule(),
    ];

    /// <summary>
    /// Runs all rules, ranks the findings and assigns stable ids (F1.., E1..). A rule that throws is skipped and
    /// reported to <paramref name="onError"/>; it never breaks the report.
    /// </summary>
    public static List<Finding> Evaluate(IncidentContext ctx, IEnumerable<IRule>? rules = null, Action<Exception>? onError = null)
    {
        var findings = new List<Finding>();
        foreach (var rule in rules ?? DefaultRules)
        {
            try
            {
                if (rule.Evaluate(ctx) is { } finding) findings.Add(finding);
            }
            catch (Exception ex)
            {
                onError?.Invoke(new InvalidOperationException($"Rule '{rule.Id}' failed: {ex.Message}", ex));
            }
        }

        var ordered = findings.OrderByDescending(f => f.Confidence).ToList();
        var evidenceNo = 1;
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Id = $"F{i + 1}";
            for (var j = 0; j < ordered[i].Evidence.Count; j++)
                ordered[i].Evidence[j] = ordered[i].Evidence[j] with { Id = $"E{evidenceNo++}" };
        }
        return ordered;
    }
}

internal static class Fmt
{
    /// <summary>A measured number for texts, in the current language's number format.</summary>
    public static string N(double value) => Stats.Round(value).ToString("0.#", Localization.Loc.Culture);

    public static Evidence Ev(DateTimeOffset at, string description, double? value = null, string? unit = null) =>
        new("", at, description, value is null ? null : Stats.Round(value.Value), unit);
}
