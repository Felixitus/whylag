using WhyLag.Core;

namespace WhyLag.Core.Tests;

/// <summary>Builds synthetic recordings: a calm baseline followed by whatever a test injects.</summary>
internal sealed class TestData
{
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    public List<Tick> Ticks { get; } = new();
    public List<SystemEvent> Events { get; } = new();

    public TestData(int seconds = 150)
    {
        for (var i = 0; i < seconds; i++)
        {
            var t = new Tick { Timestamp = Start.AddSeconds(i), ForegroundProcess = "game" };
            t.Set(MetricKeys.TickGapMs, 0);
            t.Set(MetricKeys.CpuTotal, 15 + i % 3);
            t.Set(MetricKeys.CpuPerformance, 105);
            t.Set(MetricKeys.MemAvailableMb, 16000);
            t.Set(MetricKeys.MemAvailablePct, 50);
            t.Set(MetricKeys.MemPagesIn, 5);
            t.Set(MetricKeys.DiskActive, 3);
            t.Set(MetricKeys.DiskLatencyMs, 0.5);
            t.Set(MetricKeys.NetUpMbps, 0.5);
            t.Set(MetricKeys.NetDownMbps, 2);
            t.Set(MetricKeys.Gpu3D, 60);
            t.Set(MetricKeys.PingGatewayMs, 3 + i % 2);
            t.Set(MetricKeys.PingGatewayLost, 0);
            t.Set(MetricKeys.PingTargetMs, 30 + i % 4);
            t.Set(MetricKeys.PingTargetLost, 0);
            t.TopProcesses.Add(new ProcessStat(100, "game", 10, 1, 55, 4000));
            Ticks.Add(t);
        }
    }

    public DateTimeOffset End => Ticks[^1].Timestamp;

    /// <summary>Applies a change to the last <paramref name="seconds"/> ticks (ending <paramref name="endOffset"/> s before the end).</summary>
    public TestData Last(int seconds, Action<Tick> change, int endOffset = 0)
    {
        foreach (var t in Ticks.Skip(Ticks.Count - seconds - endOffset).Take(seconds)) change(t);
        return this;
    }

    public Incident Analyse(TriggerKind trigger = TriggerKind.Hotkey)
    {
        var ctx = new Rules.IncidentContext(Ticks, Events, End, trigger);
        return new Incident { Id = "test", TriggeredAt = End, Trigger = trigger, Ticks = Ticks, Events = Events, Findings = Rules.RuleEngine.Evaluate(ctx) };
    }
}
