using static WhyLag.Core.Localization.Loc;
using static WhyLag.Core.Rules.Fmt;

namespace WhyLag.Core.Rules;

public sealed class SystemStallRule : IRule
{
    public string Id => "system.stall";

    public Finding? Evaluate(IncidentContext ctx)
    {
        if (ctx.PeakTick(MetricKeys.TickGapMs) is not { } peak) return null;
        var gap = peak.Get(MetricKeys.TickGapMs)!.Value;
        if (gap < 1500) return null;

        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.System,
            Title = T("stall.title"),
            Summary = T("stall.summary"),
            Confidence = gap >= 3000 ? 0.7 : 0.5,
            Evidence = [Ev(peak.Timestamp, T("stall.ev.gap", N(gap)), gap, "ms")],
            Checks = [T("stall.check.others"), T("stall.check.drivers")],
        };
    }
}

/// <summary>
/// Drivers that run too long in DPCs or interrupts block a CPU core: audio crackles, input lag, hitches.
/// Exact per-driver times need the optional elevated helper; without it, DPC/interrupt time per core still shows it.
/// </summary>
public sealed class DriverLatencyRule : IRule
{
    /// <summary>Common driver files and what they belong to; the value is a translation key.</summary>
    private static readonly (string Prefix, string Key)[] KnownDrivers =
    [
        ("nvlddmkm", "driver.nvidia"), ("amdkmdag", "driver.amd"), ("igdkmd", "driver.intelgpu"), ("dxgkrnl", "driver.dxgkrnl"),
        ("ndis", "driver.network"), ("tcpip", "driver.network"), ("rt6", "driver.realtek"), ("rtwlan", "driver.realtek"), ("netwtw", "driver.intelwifi"),
        ("storport", "driver.storage"), ("stornvme", "driver.storage"), ("storahci", "driver.storage"), ("iastor", "driver.storage"),
        ("acpi", "driver.acpi"), ("usbxhci", "driver.usb"), ("usbport", "driver.usb"), ("wdf01000", "driver.wdf"),
        ("hdaudbus", "driver.audio"), ("portcls", "driver.audio"), ("bthport", "driver.bluetooth"), ("ntoskrnl", "driver.kernel"),
    ];

    public string Id => "driver.latency";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var withHelper = ctx.Focus.Where(t => t.Get(MetricKeys.DpcMaxUs).HasValue).ToList();
        if (withHelper.Count > 0) return FromHelper(ctx, withHelper);

        // Without the helper: share of time one core spent in DPCs/interrupts.
        var busy = ctx.Focus.Where(t => Math.Max(t.Get(MetricKeys.DpcTimePct) ?? 0, t.Get(MetricKeys.IsrTimePct) ?? 0) >= 25).ToList();
        if (busy.Count < 2) return null;
        var worst = busy.MaxBy(t => Math.Max(t.Get(MetricKeys.DpcTimePct) ?? 0, t.Get(MetricKeys.IsrTimePct) ?? 0))!;
        var dpc = worst.Get(MetricKeys.DpcTimePct) ?? 0;
        var isr = worst.Get(MetricKeys.IsrTimePct) ?? 0;
        return new Finding
        {
            RuleId = $"{Id}.core",
            Category = FindingCategory.System,
            Title = T("dpc.core.title", N(Math.Max(dpc, isr))),
            Summary = T("dpc.core.summary"),
            Confidence = 0.5,
            Evidence =
            [
                Ev(worst.Timestamp, T("dpc.core.ev.dpc", N(dpc)), dpc, "%"),
                Ev(worst.Timestamp, T("dpc.core.ev.isr", N(isr)), isr, "%"),
                Ev(ctx.FocusStart, T("dpc.core.ev.seconds", busy.Count), busy.Count, "s"),
            ],
            Checks = [T("dpc.check.helper"), T("dpc.check.drivers")],
        };
    }

    private Finding? FromHelper(IncidentContext ctx, List<Tick> ticks)
    {
        var worst = ticks
            .SelectMany(t => t.Drivers.Select(d => (Tick: t, Driver: d)))
            .MaxBy(x => x.Driver.MaxMicroseconds);
        if (worst.Driver is null || worst.Driver.MaxMicroseconds < 1000) return null;

        var us = worst.Driver.MaxMicroseconds;
        var name = worst.Driver.Driver;
        var known = KnownDrivers.FirstOrDefault(k => name.StartsWith(k.Prefix, StringComparison.OrdinalIgnoreCase));
        var what = known.Key is null ? T("driver.unknown") : T(known.Key);
        var evidence = new List<Evidence> { Ev(worst.Tick.Timestamp, T("dpc.ev.driver", name, what, N(us)), us, "µs") };
        var others = ticks.SelectMany(t => t.Drivers.Select(d => (Tick: t, Driver: d)))
            .Where(x => !x.Driver.Driver.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Driver.MaxMicroseconds >= 500)
            .GroupBy(x => x.Driver.Driver, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(x => x.Driver.MaxMicroseconds))
            .OrderByDescending(x => x.Driver.MaxMicroseconds)
            .Take(2);
        foreach (var o in others)
            evidence.Add(Ev(o.Tick.Timestamp, T("dpc.ev.other", o.Driver.Driver, N(o.Driver.MaxMicroseconds)), o.Driver.MaxMicroseconds, "µs"));

        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.System,
            Title = T("dpc.title", name, N(us / 1000)),
            Summary = T("dpc.summary", what),
            Subject = name,
            Confidence = us >= 4000 ? 0.75 : us >= 2000 ? 0.65 : 0.5,
            Evidence = evidence,
            Checks = [T("dpc.check.update", name), T("dpc.check.power")],
        };
    }
}

public sealed class EventLogRule : IRule
{
    private sealed record Pattern(string Key, Func<SystemEvent, bool> Match, FindingCategory Category, double Confidence);

    private static readonly Pattern[] Patterns =
    [
        new("tdr", e => e.Provider.Equals("Display", StringComparison.OrdinalIgnoreCase) && e.EventId == 4101, FindingCategory.Gpu, 0.8),
        new("whea", e => e.Provider.Contains("WHEA-Logger", StringComparison.OrdinalIgnoreCase), FindingCategory.System, 0.75),
        new("storage", e => (e.Provider is "disk" or "stornvme" or "storahci" or "Ntfs") && e.EventId is 7 or 11 or 51 or 129 or 153, FindingCategory.Disk, 0.7),
        new("lowmem", e => e.Provider.Contains("Resource-Exhaustion-Detector", StringComparison.OrdinalIgnoreCase) && e.EventId == 2004, FindingCategory.Memory, 0.7),
        new("hang", e => e.Provider.Equals("Application Hang", StringComparison.OrdinalIgnoreCase) && e.EventId == 1002, FindingCategory.System, 0.5),
        new("crash", e => e.Provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase) && e.EventId == 1000, FindingCategory.System, 0.4),
        new("wlan", e => e.Provider.Contains("WLAN-AutoConfig", StringComparison.OrdinalIgnoreCase), FindingCategory.Network, 0.6),
        new("update", e => e.Provider.Contains("WindowsUpdateClient", StringComparison.OrdinalIgnoreCase), FindingCategory.Background, 0.45),
    ];

    public string Id => "events.correlated";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var events = ctx.FocusEvents(TimeSpan.FromSeconds(30)).ToList();
        if (events.Count == 0) return null;

        // The best matching known pattern becomes the finding; other errors in the window are added as evidence.
        var matched = Patterns
            .Select(p => (Pattern: p, Events: events.Where(p.Match).ToList()))
            .Where(x => x.Events.Count > 0)
            .OrderByDescending(x => x.Pattern.Confidence)
            .ToList();
        var unmatchedErrors = events.Where(e => e.Level <= EventLevel.Error && !Patterns.Any(p => p.Match(e))).ToList();
        if (matched.Count == 0 && unmatchedErrors.Count == 0) return null;

        var evidence = new List<Evidence>();
        foreach (var e in matched.SelectMany(m => m.Events).Concat(unmatchedErrors).DistinctBy(e => (e.Timestamp, e.Provider, e.EventId)).Take(6))
            evidence.Add(Ev(e.Timestamp, T("events.ev", T($"level.{e.Level}"), e.Provider, e.EventId, LocalNetworkRule.Shorten(e.Message))));

        if (matched.Count == 0)
        {
            return new Finding
            {
                RuleId = Id,
                Category = FindingCategory.Events,
                Title = T("events.generic.title"),
                Summary = T("events.generic.summary"),
                Confidence = 0.3,
                Evidence = evidence,
                Checks = [T("events.generic.check")],
            };
        }

        var best = matched[0].Pattern;
        return new Finding
        {
            RuleId = $"{Id}.{best.Key}",
            Category = best.Category,
            Title = T($"events.{best.Key}.title"),
            Summary = T($"events.{best.Key}.summary"),
            Confidence = best.Confidence,
            Evidence = evidence,
            Checks = matched.Select(m => T($"events.{m.Pattern.Key}.check")).Distinct().ToList(),
        };
    }
}
