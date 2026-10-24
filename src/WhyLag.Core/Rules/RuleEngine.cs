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
    public static IReadOnlyList<IRule> DefaultRules { get; } =
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
        new TcpRetransmissionRule(),
        new DriverLatencyRule(),
    ];

    /// <summary>Runs all rules, ranks the findings and assigns stable ids (F1.., E1..).</summary>
    public static List<Finding> Evaluate(IncidentContext ctx, IEnumerable<IRule>? rules = null)
    {
        var findings = new List<Finding>();
        foreach (var rule in rules ?? DefaultRules)
        {
            try
            {
                if (rule.Evaluate(ctx) is { } finding) findings.Add(finding);
            }
            catch (Exception)
            {
                // A broken rule must never break the report; the remaining rules still run.
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
