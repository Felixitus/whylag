using static WhyLag.Core.Localization.Loc;
using static WhyLag.Core.Rules.Fmt;

namespace WhyLag.Core.Rules;

/// <summary>
/// Confirms the symptom: the game in front really hitched or its frame rate dropped. Other findings then
/// explain why; this one tells the user that WhyLag saw the same thing they saw.
/// </summary>
public sealed class FrameStutterRule : IRule
{
    public string Id => "frames.stutter";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var withFrames = ctx.Focus.Where(t => t.Get(MetricKeys.FramesMaxMs).HasValue && t.Get(MetricKeys.FramesFps) >= 5).ToList();
        if (withFrames.Count < 3) return null;

        var typical = ctx.BaselineMedian(MetricKeys.FramesMedianMs);
        if (double.IsNaN(typical)) typical = Stats.Median(IncidentContext.Series(withFrames, MetricKeys.FramesMedianMs));
        if (typical > 40) return null; // below 25 fps there is no smooth baseline to compare with

        var threshold = Math.Max(50, typical * 3);
        var spikes = withFrames.Where(t => t.Get(MetricKeys.FramesMaxMs) >= threshold).ToList();
        var app = withFrames.Select(t => t.FrameApp).LastOrDefault(a => a is not null) ?? T("frames.game");

        if (spikes.Count > 0)
        {
            var worst = spikes.MaxBy(t => t.Get(MetricKeys.FramesMaxMs)!.Value)!;
            var worstMs = worst.Get(MetricKeys.FramesMaxMs)!.Value;
            return new Finding
            {
                RuleId = Id,
                Category = FindingCategory.Frames,
                Title = T("frames.title", app, N(worstMs)),
                Summary = T("frames.summary"),
                Subject = app,
                Confidence = spikes.Count >= 2 ? 0.7 : 0.6,
                Evidence =
                [
                    Ev(worst.Timestamp, T("frames.ev.worst", app, N(worstMs)), worstMs, "ms"),
                    Ev(ctx.FocusStart, T("frames.ev.typical", N(typical), N(1000 / typical)), typical, "ms"),
                    Ev(worst.Timestamp, T("frames.ev.seconds", spikes.Count), spikes.Count, "s"),
                ],
                Checks = [T("frames.check.game"), T("frames.check.limit")],
            };
        }

        var baselineFps = ctx.BaselineMedian(MetricKeys.FramesFps);
        if (double.IsNaN(baselineFps) || baselineFps < 30) return null;
        var drops = withFrames.Where(t => t.Get(MetricKeys.FramesFps) <= baselineFps * 0.5).ToList();
        if (drops.Count < 2) return null;
        var lowest = drops.MinBy(t => t.Get(MetricKeys.FramesFps)!.Value)!;
        var lowFps = lowest.Get(MetricKeys.FramesFps)!.Value;
        return new Finding
        {
            RuleId = $"{Id}.fpsdrop",
            Category = FindingCategory.Frames,
            Title = T("fpsdrop.title", app, N(lowFps)),
            Summary = T("fpsdrop.summary"),
            Subject = app,
            Confidence = 0.55,
            Evidence =
            [
                Ev(lowest.Timestamp, T("fpsdrop.ev.low", N(lowFps)), lowFps, "fps"),
                Ev(ctx.FocusStart, T("fpsdrop.ev.baseline", N(baselineFps)), baselineFps, "fps"),
                Ev(lowest.Timestamp, T("fpsdrop.ev.seconds", drops.Count), drops.Count, "s"),
            ],
            Checks = [T("fpsdrop.check")],
        };
    }
}

public sealed class WifiRule : IRule
{
    public string Id => "wifi.weak";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var withWifi = ctx.Focus.Where(t => t.Get(MetricKeys.WifiSignal).HasValue || t.Get(MetricKeys.WifiRssi).HasValue).ToList();
        if (withWifi.Count == 0) return null;

        var evidence = new List<Evidence>();
        var weakest = withWifi.MinBy(t => t.Get(MetricKeys.WifiSignal) ?? 100);
        var signal = weakest?.Get(MetricKeys.WifiSignal);
        var rssiTick = withWifi.Where(t => t.Get(MetricKeys.WifiRssi).HasValue).MinBy(t => t.Get(MetricKeys.WifiRssi)!.Value);
        var rssi = rssiTick?.Get(MetricKeys.WifiRssi);
        var weak = signal <= 40 || rssi <= -75;
        if (weak)
        {
            if (signal is { } s) evidence.Add(Ev(weakest!.Timestamp, T("wifi.ev.signal", N(s)), s, "%"));
            if (rssi is { } r) evidence.Add(Ev(rssiTick!.Timestamp, T("wifi.ev.rssi", N(r)), r, "dBm"));
        }

        var roams = ctx.Focus.Where(t => t.Get(MetricKeys.WifiRoam) >= 1).ToList();
        evidence.AddRange(roams.Take(2).Select(t => Ev(t.Timestamp, T("wifi.ev.roam"))));

        var baseRate = ctx.BaselineMedian(MetricKeys.WifiRxMbps);
        var lowRate = ctx.Focus.Where(t => t.Get(MetricKeys.WifiRxMbps).HasValue).MinBy(t => t.Get(MetricKeys.WifiRxMbps)!.Value);
        var rateDrop = !double.IsNaN(baseRate) && lowRate?.Get(MetricKeys.WifiRxMbps) is { } rate && rate <= baseRate * 0.3;
        if (rateDrop)
        {
            var rateValue = lowRate!.Get(MetricKeys.WifiRxMbps)!.Value;
            evidence.Add(Ev(lowRate.Timestamp, T("wifi.ev.rate", N(rateValue), N(baseRate)), rateValue, "Mbit/s"));
        }

        if (!weak && roams.Count == 0 && !rateDrop) return null;

        var gatewayTrouble = ctx.Focus.Any(t => t.Get(MetricKeys.PingGatewayLost) >= 1)
                             || (ctx.BaselineMedian(MetricKeys.PingGatewayMs) is var gw && !double.IsNaN(gw)
                                 && ctx.Focus.Any(t => t.Get(MetricKeys.PingGatewayMs) >= Math.Max(gw * 2, gw + 30)));
        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.Network,
            Title = roams.Count > 0 ? T("wifi.title.roam") : weak ? T("wifi.title.weak") : T("wifi.title.rate"),
            Summary = gatewayTrouble ? T("wifi.summary.router") : T("wifi.summary"),
            Subject = "Wi-Fi",
            Confidence = gatewayTrouble ? 0.75 : 0.45,
            Evidence = evidence,
            Checks = [T("wifi.check.position"), T("wifi.check.node")],
        };
    }
}
