using WhyLag.Core.Rules;

namespace WhyLag.Core;

/// <summary>
/// A synthetic incident (a Defender scan and a cloud upload during a game) so users can see a
/// report and try the AI explanation without waiting for a real problem. Clearly marked as demo.
/// </summary>
public static class DemoIncident
{
    public static Incident Create(MachineInfo machine)
    {
        var end = DateTimeOffset.Now;
        var start = end.AddSeconds(-150);
        var ticks = new List<Tick>();
        for (var i = 0; i < 150; i++)
        {
            var t = new Tick { Timestamp = start.AddSeconds(i), ForegroundProcess = "DemoGame" };
            var spike = i is >= 128 and <= 136;
            t.Set(MetricKeys.TickGapMs, i == 131 ? 1850 : 0);
            t.Set(MetricKeys.CpuTotal, spike ? 94 + i % 4 : 22 + i % 5);
            t.Set(MetricKeys.CpuPerformance, 108);
            t.Set(MetricKeys.MemAvailablePct, 46);
            t.Set(MetricKeys.MemAvailableMb, machine.TotalMemoryMb * 0.46);
            t.Set(MetricKeys.DiskActive, spike ? 100 : 4);
            t.Set(MetricKeys.DiskLatencyMs, spike ? 85 : 0.4);
            t.Set(MetricKeys.NetUpMbps, spike ? 31 : 0.4);
            t.Set(MetricKeys.NetDownMbps, 3);
            t.Set(MetricKeys.Gpu3D, 78);
            t.Set(MetricKeys.PingGatewayMs, 2 + i % 2);
            t.Set(MetricKeys.PingGatewayLost, 0);
            t.Set(MetricKeys.PingTargetMs, spike ? 180 + i % 3 * 20 : 24 + i % 4);
            t.Set(MetricKeys.PingTargetLost, 0);
            t.TopProcesses.Add(new ProcessStat(1000, "DemoGame", 18, 2, 75, 6200));
            if (spike)
            {
                t.TopProcesses.Add(new ProcessStat(2000, "MsMpEng", 41, 96, 0, 310));
                t.TopProcesses.Add(new ProcessStat(3000, "OneDrive", 6, 4, 0, 180));
            }
            ticks.Add(t);
        }

        var triggeredAt = start.AddSeconds(140);
        var focus = ticks.Where(t => t.Timestamp <= triggeredAt.AddSeconds(3)).ToList();
        var ctx = new IncidentContext(focus, [], triggeredAt, TriggerKind.Hotkey);
        return new Incident
        {
            Id = $"{triggeredAt.ToLocalTime():yyyyMMdd-HHmmss}-demo",
            TriggeredAt = triggeredAt,
            Trigger = TriggerKind.Manual,
            TriggerDetail = "Demo incident with synthetic data",
            AppVersion = typeof(DemoIncident).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            Machine = machine,
            Ticks = focus,
            Findings = RuleEngine.Evaluate(ctx),
        };
    }
}
