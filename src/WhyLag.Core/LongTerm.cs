namespace WhyLag.Core;

/// <summary>One sample per minute of process memory, for spotting slow memory growth over hours.</summary>
public sealed record MemorySample(DateTimeOffset At, double AvailablePct, Dictionary<string, double> PrivateMb);

/// <summary>Keeps the last 24 hours at one sample per minute (top memory users only).</summary>
public sealed class LongTermHistory
{
    private const int TopProcesses = 8;
    private readonly RingBuffer<MemorySample> _samples = new(24 * 60);
    private readonly Dictionary<string, double> _minute = new(StringComparer.OrdinalIgnoreCase);
    private double _available = double.NaN;
    private DateTimeOffset _minuteStart;

    public void Add(Tick tick)
    {
        if (_minuteStart == default) _minuteStart = tick.Timestamp;
        foreach (var p in tick.TopProcesses)
            _minute[p.Name] = Math.Max(_minute.GetValueOrDefault(p.Name), p.PrivateMb);
        if (tick.Get(MetricKeys.MemAvailablePct) is { } avail) _available = avail;

        if (tick.Timestamp - _minuteStart < TimeSpan.FromMinutes(1)) return;
        var top = _minute.OrderByDescending(kv => kv.Value).Take(TopProcesses)
            .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value), StringComparer.OrdinalIgnoreCase);
        _samples.Add(new MemorySample(tick.Timestamp, Math.Round(_available, 1), top));
        _minute.Clear();
        _minuteStart = tick.Timestamp;
    }

    /// <summary>Samples of the last <paramref name="span"/>, oldest first.</summary>
    public List<MemorySample> Last(TimeSpan span)
    {
        var all = _samples.Snapshot();
        if (all.Count == 0) return all;
        var from = all[^1].At - span;
        return all.Where(s => s.At >= from).ToList();
    }
}
