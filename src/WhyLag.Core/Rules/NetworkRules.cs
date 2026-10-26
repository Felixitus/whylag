using static WhyLag.Core.Localization.Loc;
using static WhyLag.Core.Rules.Fmt;

namespace WhyLag.Core.Rules;

internal readonly record struct PingAnalysis(bool HasData, bool Problem, Tick? Peak, double PeakMs, double Baseline, int Lost, Tick? FirstLost)
{
    public static PingAnalysis Of(IncidentContext ctx, string msKey, string lostKey)
    {
        var hasData = ctx.Focus.Any(t => t.Get(msKey).HasValue || t.Get(lostKey).HasValue);
        if (!hasData) return default;

        var baseline = ctx.BaselineMedian(msKey);
        var threshold = double.IsNaN(baseline) ? 100 : Math.Max(baseline * 2, baseline + 40);
        var peak = ctx.PeakTick(msKey);
        var peakMs = peak?.Get(msKey) ?? 0;
        var lostTicks = ctx.Focus.Where(t => t.Get(lostKey) >= 1).ToList();
        return new PingAnalysis(true, peakMs >= threshold || lostTicks.Count > 0, peak, peakMs, baseline, lostTicks.Count, lostTicks.FirstOrDefault());
    }

    public void AddEvidence(List<Evidence> evidence, string what)
    {
        if (Peak is not null && PeakMs > 0)
            evidence.Add(Ev(Peak.Timestamp, T("ping.ev.peak", what, N(PeakMs)), PeakMs, "ms"));
        if (!double.IsNaN(Baseline) && Baseline > 0)
            evidence.Add(Ev(Peak?.Timestamp ?? FirstLost?.Timestamp ?? default, T("ping.ev.baseline", what, N(Baseline)), Baseline, "ms"));
        if (Lost > 0 && FirstLost is not null)
            evidence.Add(Ev(FirstLost.Timestamp, T("ping.ev.lost", Lost, what), Lost, "pings"));
    }
}

internal static class OwnTraffic
{
    /// <summary>Upload/download peaks around the moment the latency spiked, if they are unusually high.</summary>
    public static (Tick Tick, string Key, double Value, double Baseline)? Saturation(IncidentContext ctx, Tick spike)
    {
        var around = ctx.Focus.Where(t => Math.Abs((t.Timestamp - spike.Timestamp).TotalSeconds) <= 3).ToList();
        foreach (var (key, absolute, minimum) in new[] { (MetricKeys.NetUpMbps, 5.0, 2.0), (MetricKeys.NetDownMbps, 50.0, 10.0) })
        {
            var peak = around.Where(t => t.Get(key).HasValue).MaxBy(t => t.Get(key)!.Value);
            if (peak is null) continue;
            var value = peak.Get(key)!.Value;
            var baseline = ctx.BaselineMedian(key);
            var saturated = double.IsNaN(baseline) ? value >= absolute : value >= Math.Max(minimum, baseline * 4);
            if (saturated) return (peak, key, value, baseline);
        }
        return null;
    }
}

public sealed class LocalNetworkRule : IRule
{
    public string Id => "net.local";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var gateway = PingAnalysis.Of(ctx, MetricKeys.PingGatewayMs, MetricKeys.PingGatewayLost);
        if (!gateway.Problem) return null;
        var target = PingAnalysis.Of(ctx, MetricKeys.PingTargetMs, MetricKeys.PingTargetLost);

        var evidence = new List<Evidence>();
        gateway.AddEvidence(evidence, T("net.router"));
        var wlan = ctx.FocusEvents(TimeSpan.FromSeconds(30))
            .Where(e => e.Provider.Contains("WLAN", StringComparison.OrdinalIgnoreCase))
            .Take(3).ToList();
        evidence.AddRange(wlan.Select(e => Ev(e.Timestamp, T("net.ev.wifievent", e.EventId, Shorten(e.Message)))));

        // Many routers answer pings with low priority. If the internet target was fine, the router ping alone is weak evidence.
        var onlyRouterPingSlow = target.HasData && !target.Problem;
        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.Network,
            Title = wlan.Count > 0 ? T("netlocal.title.wifi") : T("netlocal.title"),
            Summary = onlyRouterPingSlow ? T("netlocal.summary.weak") : T("netlocal.summary"),
            Confidence = Math.Min((onlyRouterPingSlow ? 0.35 : 0.6) + (wlan.Count > 0 ? 0.15 : 0), 0.85),
            Evidence = evidence,
            Checks = [T("netlocal.check.wifi"), T("netlocal.check.router")],
        };
    }

    internal static string Shorten(string message) =>
        message.Length <= 140 ? message.ReplaceLineEndings(" ") : message[..140].ReplaceLineEndings(" ") + "…";
}

public sealed class RemoteNetworkRule : IRule
{
    public string Id => "net.remote";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var target = PingAnalysis.Of(ctx, MetricKeys.PingTargetMs, MetricKeys.PingTargetLost);
        if (!target.Problem) return null;
        var gateway = PingAnalysis.Of(ctx, MetricKeys.PingGatewayMs, MetricKeys.PingGatewayLost);
        if (gateway.Problem) return null; // covered by net.local
        if (target.Peak is { } spike && OwnTraffic.Saturation(ctx, spike) is not null) return null; // covered by net.bufferbloat

        var evidence = new List<Evidence>();
        target.AddEvidence(evidence, T("net.target"));
        if (gateway.HasData)
            evidence.Add(Ev(ctx.FocusStart, gateway.Peak is null ? T("netremote.ev.router") : T("netremote.ev.router.max", N(gateway.PeakMs)), gateway.PeakMs, "ms"));

        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.Network,
            Title = gateway.HasData ? T("netremote.title") : T("netremote.title.unknown"),
            Summary = gateway.HasData ? T("netremote.summary") : T("netremote.summary.unknown"),
            Confidence = gateway.HasData ? 0.6 : 0.4,
            Evidence = evidence,
            Checks = [T("netremote.check.provider"), T("netremote.check.target")],
        };
    }
}

public sealed class BufferbloatRule : IRule
{
    public string Id => "net.bufferbloat";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var target = PingAnalysis.Of(ctx, MetricKeys.PingTargetMs, MetricKeys.PingTargetLost);
        if (!target.Problem || target.Peak is not { } spike) return null;
        if (OwnTraffic.Saturation(ctx, spike) is not { } traffic) return null;

        var direction = traffic.Key == MetricKeys.NetUpMbps ? T("net.upload") : T("net.download");
        var evidence = new List<Evidence>();
        target.AddEvidence(evidence, T("net.target"));
        evidence.Add(Ev(traffic.Tick.Timestamp, T("bloat.ev.traffic", direction, N(traffic.Value)), traffic.Value, "Mbit/s"));
        if (!double.IsNaN(traffic.Baseline))
            evidence.Add(Ev(ctx.FocusStart, T("bloat.ev.baseline", direction, N(traffic.Baseline)), traffic.Baseline, "Mbit/s"));
        var around = ctx.Focus.Where(t => Math.Abs((t.Timestamp - spike.Timestamp).TotalSeconds) <= 3).ToList();
        var top = ctx.TopProcess(p => p.IoMBps, around);
        if (top is { } tp) evidence.Add(Ev(spike.Timestamp, T("bloat.ev.top", tp.Name, N(tp.Peak)), tp.Peak, "MB/s"));

        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.Network,
            Title = T("bloat.title", direction),
            Summary = T("bloat.summary", direction),
            Subject = top?.Name,
            Confidence = 0.6,
            Evidence = evidence,
            Checks = [T("bloat.check.pause", direction), T("bloat.check.sqm")],
        };
    }
}

/// <summary>
/// Latency rose while the router reported much more traffic than this PC caused: another device in the household
/// (streaming, downloads, backups) filled the line. Needs the optional UPnP router reading.
/// </summary>
public sealed class HouseholdTrafficRule : IRule
{
    public string Id => "net.household";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var target = PingAnalysis.Of(ctx, MetricKeys.PingTargetMs, MetricKeys.PingTargetLost);
        if (!target.Problem || target.Peak is not { } spike) return null;
        var around = ctx.Focus.Where(t => Math.Abs((t.Timestamp - spike.Timestamp).TotalSeconds) <= 3 && t.Get(MetricKeys.RouterDownMbps).HasValue).ToList();
        if (around.Count == 0) return null;

        var worst = around.MaxBy(t => Others(t).Down + Others(t).Up * 5)!;
        var (down, up) = Others(worst);
        if (down < 30 && up < 5) return null;

        var direction = up * 5 >= down ? T("net.upload") : T("net.download");
        var evidence = new List<Evidence>();
        target.AddEvidence(evidence, T("net.target"));
        evidence.Add(Ev(worst.Timestamp, T("household.ev.router", N(worst.Get(MetricKeys.RouterDownMbps) ?? 0), N(worst.Get(MetricKeys.RouterUpMbps) ?? 0)), worst.Get(MetricKeys.RouterDownMbps), "Mbit/s"));
        evidence.Add(Ev(worst.Timestamp, T("household.ev.pc", N(worst.Get(MetricKeys.NetDownMbps) ?? 0), N(worst.Get(MetricKeys.NetUpMbps) ?? 0)), worst.Get(MetricKeys.NetDownMbps), "Mbit/s"));
        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.Network,
            Title = T("household.title", direction),
            Summary = T("household.summary"),
            Confidence = 0.65,
            Evidence = evidence,
            Checks = [T("household.check.devices"), T("bloat.check.sqm")],
        };
    }

    private static (double Down, double Up) Others(Tick t) =>
        (Math.Max(0, (t.Get(MetricKeys.RouterDownMbps) ?? 0) - (t.Get(MetricKeys.NetDownMbps) ?? 0)),
         Math.Max(0, (t.Get(MetricKeys.RouterUpMbps) ?? 0) - (t.Get(MetricKeys.NetUpMbps) ?? 0)));
}
