using static WhyLag.Core.Localization.Loc;
using static WhyLag.Core.Rules.Fmt;

namespace WhyLag.Core.Rules;

public sealed class CpuSaturationRule : IRule
{
    public string Id => "cpu.saturation";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var seconds = ctx.CountWhere(MetricKeys.CpuTotal, v => v >= 90);
        if (seconds < 2 || ctx.PeakTick(MetricKeys.CpuTotal) is not { } peak) return null;

        var peakValue = peak.Get(MetricKeys.CpuTotal)!.Value;
        var baseline = ctx.BaselineMedian(MetricKeys.CpuTotal);
        var top = ctx.TopProcess(p => p.CpuPercent, ctx.Focus.Where(t => t.Get(MetricKeys.CpuTotal) >= 90));

        var evidence = new List<Evidence>
        {
            Ev(peak.Timestamp, T("cpu.ev.peak", N(peakValue)), peakValue, "%"),
            Ev(ctx.FocusStart, T("cpu.ev.seconds", seconds), seconds, "s"),
        };
        if (!double.IsNaN(baseline)) evidence.Add(Ev(ctx.FocusStart, T("cpu.ev.baseline", N(baseline)), baseline, "%"));
        if (top is { } t) evidence.Add(Ev(peak.Timestamp, T("cpu.ev.top", t.Name, N(t.Peak)), t.Peak, "%"));

        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.Cpu,
            Title = top is { } tp ? T("cpu.title.by", tp.Name) : T("cpu.title"),
            Summary = T("cpu.summary", seconds),
            Subject = top?.Name,
            Confidence = Math.Min(0.55 + (top?.Peak >= 40 ? 0.2 : 0) + (baseline < 60 ? 0.1 : 0), 0.9),
            Evidence = evidence,
            Checks = [top is { } c ? T("cpu.check.process", c.Name) : T("cpu.check.taskmanager"), T("cpu.check.background")],
        };
    }
}

public sealed class CpuThrottlingRule : IRule
{
    public string Id => "cpu.throttling";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var baseline = ctx.BaselineMedian(MetricKeys.CpuPerformance);
        if (double.IsNaN(baseline) || baseline < 85) return null;

        var limit = Math.Min(75, baseline * 0.7);
        var throttled = ctx.Focus
            .Where(t => t.Get(MetricKeys.CpuPerformance) is { } perf && perf <= limit && t.Get(MetricKeys.CpuTotal) >= 40)
            .ToList();
        if (throttled.Count < 2) return null;

        var low = throttled.MinBy(t => t.Get(MetricKeys.CpuPerformance)!.Value)!;
        var lowValue = low.Get(MetricKeys.CpuPerformance)!.Value;
        var load = low.Get(MetricKeys.CpuTotal) ?? 0;
        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.Cpu,
            Title = T("throttle.title"),
            Summary = T("throttle.summary"),
            Confidence = 0.5,
            Evidence =
            [
                Ev(low.Timestamp, T("throttle.ev.speed", N(lowValue)), lowValue, "%"),
                Ev(ctx.FocusStart, T("throttle.ev.baseline", N(baseline)), baseline, "%"),
                Ev(low.Timestamp, T("throttle.ev.load", N(load)), load, "%"),
            ],
            Checks = [T("throttle.check.temp"), T("throttle.check.power")],
        };
    }
}

public sealed class DiskSaturationRule : IRule
{
    public string Id => "disk.saturation";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var busy = ctx.CountWhere(MetricKeys.DiskActive, v => v >= 90);
        var slow = ctx.CountWhere(MetricKeys.DiskLatencyMs, v => v >= 50);
        if (busy < 2 && slow < 2) return null;

        var evidence = new List<Evidence>();
        if (ctx.PeakTick(MetricKeys.DiskActive) is { } activePeak)
        {
            var v = activePeak.Get(MetricKeys.DiskActive)!.Value;
            evidence.Add(Ev(activePeak.Timestamp, T("disk.ev.busy", N(v)), v, "%"));
        }
        double latency = 0;
        if (ctx.PeakTick(MetricKeys.DiskLatencyMs) is { } latencyPeak)
        {
            latency = latencyPeak.Get(MetricKeys.DiskLatencyMs)!.Value;
            evidence.Add(Ev(latencyPeak.Timestamp, T("disk.ev.latency", N(latency)), latency, "ms"));
        }
        var top = ctx.TopProcess(p => p.IoMBps, ctx.Focus.Where(t => t.Get(MetricKeys.DiskActive) >= 90 || t.Get(MetricKeys.DiskLatencyMs) >= 50));
        if (top is { } t) evidence.Add(Ev(ctx.FocusStart, T("disk.ev.top", t.Name, N(t.Peak)), t.Peak, "MB/s"));

        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.Disk,
            Title = top is { } tp ? T("disk.title.by", tp.Name) : T("disk.title"),
            Summary = T("disk.summary"),
            Subject = top?.Name,
            Confidence = Math.Min(0.6 + (top?.Peak >= 20 ? 0.15 : 0) + (latency >= 100 ? 0.1 : 0), 0.9),
            Evidence = evidence,
            Checks = [T("disk.check.resmon"), T("disk.check.schedule")],
        };
    }
}

public sealed class MemoryPressureRule : IRule
{
    public string Id => "memory.pressure";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var lowMem = ctx.Focus.Where(t => t.Get(MetricKeys.MemAvailablePct) <= 10).ToList();
        var paging = ctx.CountWhere(MetricKeys.MemPagesIn, v => v >= 1000);
        if (lowMem.Count == 0 && paging < 2) return null;

        var evidence = new List<Evidence>();
        if (lowMem.MinBy(t => t.Get(MetricKeys.MemAvailablePct)!.Value) is { } low)
        {
            var pct = low.Get(MetricKeys.MemAvailablePct)!.Value;
            evidence.Add(Ev(low.Timestamp, T("mem.ev.free", N(pct), N(low.Get(MetricKeys.MemAvailableMb) ?? 0)), pct, "%"));
        }
        if (ctx.PeakTick(MetricKeys.MemPagesIn) is { } pagePeak && paging >= 2)
        {
            var v = pagePeak.Get(MetricKeys.MemPagesIn)!.Value;
            evidence.Add(Ev(pagePeak.Timestamp, T("mem.ev.paging", N(v)), v, "pages/s"));
        }
        var top = ctx.TopProcess(p => p.PrivateMb);
        if (top is { } t) evidence.Add(Ev(ctx.FocusStart, T("mem.ev.top", t.Name, N(t.Peak)), t.Peak, "MB"));

        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.Memory,
            Title = T("mem.title"),
            Summary = T("mem.summary"),
            Subject = top?.Name,
            Confidence = lowMem.Count > 0 && paging >= 2 ? 0.75 : 0.55,
            Evidence = evidence,
            Checks = [T("mem.check.close"), T("mem.check.ram")],
        };
    }
}

public sealed class GpuSaturationRule : IRule
{
    public string Id => "gpu.saturation";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var seconds = ctx.CountWhere(MetricKeys.Gpu3D, v => v >= 97);
        if (seconds < 3 || ctx.PeakTick(MetricKeys.Gpu3D) is not { } peak) return null;

        var v = peak.Get(MetricKeys.Gpu3D)!.Value;
        var evidence = new List<Evidence>
        {
            Ev(peak.Timestamp, T("gpu.ev.peak", N(v)), v, "%"),
            Ev(ctx.FocusStart, T("gpu.ev.seconds", seconds), seconds, "s"),
        };
        if (ctx.PeakTick(MetricKeys.GpuVramUsedMb) is { } vram)
        {
            var mb = vram.Get(MetricKeys.GpuVramUsedMb)!.Value;
            evidence.Add(Ev(vram.Timestamp, T("gpu.ev.vram", N(mb)), mb, "MB"));
        }

        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.Gpu,
            Title = T("gpu.title"),
            Summary = T("gpu.summary"),
            Confidence = 0.35,
            Evidence = evidence,
            Checks = [T("gpu.check.settings"), T("gpu.check.limit")],
        };
    }
}

public sealed class GpuBackgroundRule : IRule
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { "dwm", "csrss", "System", "Idle" };

    public string Id => "gpu.background";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var foreground = ctx.Focus.Select(t => t.ForegroundProcess).LastOrDefault(n => n is not null);
        if (foreground is null) return null;

        var others = ctx.Focus
            .SelectMany(t => t.TopProcesses.Select(p => (Tick: t, Proc: p)))
            .Where(x => x.Proc.GpuPercent >= 15
                        && !string.Equals(x.Proc.Name, foreground, StringComparison.OrdinalIgnoreCase)
                        && !Ignored.Contains(x.Proc.Name))
            .GroupBy(x => x.Proc.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Select(g => g.MaxBy(x => x.Proc.GpuPercent))
            .OrderByDescending(x => x.Proc.GpuPercent)
            .ToList();
        if (others.Count == 0) return null;

        var evidence = new List<Evidence> { Ev(ctx.FocusStart, T("gpubg.ev.foreground", foreground)) };
        evidence.AddRange(others.Take(3).Select(x =>
            Ev(x.Tick.Timestamp, T("gpubg.ev.process", x.Proc.Name, N(x.Proc.GpuPercent)), x.Proc.GpuPercent, "%")));
        var names = string.Join(", ", others.Take(3).Select(x => x.Proc.Name));

        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.Gpu,
            Title = T("gpubg.title", names),
            Summary = T("gpubg.summary", foreground),
            Subject = others[0].Proc.Name,
            Confidence = 0.55,
            Evidence = evidence,
            Checks = [T("gpubg.check.close"), T("gpubg.check.acceleration")],
        };
    }
}

public sealed class BackgroundTaskRule : IRule
{
    /// <summary>Process names (without .exe) that commonly cause unexpected load; the value is a translation key.</summary>
    public static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MsMpEng"] = "known.defender",
        ["MpDefenderCoreService"] = "known.defender.core",
        ["TiWorker"] = "known.tiworker",
        ["TrustedInstaller"] = "known.trustedinstaller",
        ["MoUsoCoreWorker"] = "known.uso",
        ["usocoreworker"] = "known.uso",
        ["SearchIndexer"] = "known.search",
        ["SearchProtocolHost"] = "known.search.helper",
        ["CompatTelRunner"] = "known.telemetry",
        ["MsiExec"] = "known.msiexec",
        ["MRT"] = "known.mrt",
        ["WmiPrvSE"] = "known.wmi",
        ["OneDrive"] = "known.onedrive",
        ["GoogleDriveFS"] = "known.googledrive",
        ["Dropbox"] = "known.dropbox",
        ["steam"] = "known.steam",
        ["steamwebhelper"] = "known.steamweb",
        ["EpicGamesLauncher"] = "known.epic",
        ["ollama"] = "known.ollama",
        ["ollama_llama_server"] = "known.ollama.runner",
    };

    public string Id => "background.task";

    public Finding? Evaluate(IncidentContext ctx)
    {
        var hits = ctx.Focus
            .SelectMany(t => t.TopProcesses.Select(p => (Tick: t, Proc: p)))
            .Where(x => Known.ContainsKey(x.Proc.Name) && (x.Proc.CpuPercent >= 10 || x.Proc.IoMBps >= 10 || x.Proc.GpuPercent >= 15))
            .GroupBy(x => x.Proc.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key,
                          Cpu: g.MaxBy(x => x.Proc.CpuPercent),
                          Io: g.MaxBy(x => x.Proc.IoMBps),
                          Gpu: g.MaxBy(x => x.Proc.GpuPercent)))
            .OrderByDescending(h => h.Cpu.Proc.CpuPercent + h.Io.Proc.IoMBps + h.Gpu.Proc.GpuPercent)
            .ToList();
        if (hits.Count == 0) return null;

        var evidence = new List<Evidence>();
        var strong = false;
        foreach (var hit in hits.Take(4))
        {
            var desc = T(Known[hit.Name]);
            if (hit.Cpu.Proc.CpuPercent >= 10)
                evidence.Add(Ev(hit.Cpu.Tick.Timestamp, T("bg.ev.cpu", hit.Name, desc, N(hit.Cpu.Proc.CpuPercent)), hit.Cpu.Proc.CpuPercent, "%"));
            if (hit.Io.Proc.IoMBps >= 10)
                evidence.Add(Ev(hit.Io.Tick.Timestamp, T("bg.ev.io", hit.Name, desc, N(hit.Io.Proc.IoMBps)), hit.Io.Proc.IoMBps, "MB/s"));
            if (hit.Gpu.Proc.GpuPercent >= 15)
                evidence.Add(Ev(hit.Gpu.Tick.Timestamp, T("bg.ev.gpu", hit.Name, desc, N(hit.Gpu.Proc.GpuPercent)), hit.Gpu.Proc.GpuPercent, "%"));
            strong |= hit.Cpu.Proc.CpuPercent >= 25 || hit.Io.Proc.IoMBps >= 30;
        }

        var first = hits[0].Name;
        return new Finding
        {
            RuleId = Id,
            Category = FindingCategory.Background,
            Title = hits.Count == 1 ? T("bg.title.one", T(Known[first])) : T("bg.title.many", string.Join(", ", hits.Take(3).Select(h => h.Name))),
            Summary = T("bg.summary"),
            Subject = first,
            Confidence = strong ? 0.7 : 0.5,
            Evidence = evidence,
            Checks = [T("bg.check.schedule"), T("bg.check.security")],
        };
    }
}
