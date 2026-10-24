namespace WhyLag.Core.Rules;

/// <summary>
/// Read-only view of an incident for rules. The focus window is where the problem most likely
/// happened; everything before it is the baseline ("normal" for this session).
/// </summary>
public sealed class IncidentContext
{
    public IncidentContext(IReadOnlyList<Tick> ticks, IReadOnlyList<SystemEvent> events, DateTimeOffset triggeredAt, TriggerKind trigger)
    {
        Ticks = ticks;
        Events = events;
        TriggeredAt = triggeredAt;
        Trigger = trigger;

        // A hotkey is pressed after the user noticed the problem, so look further back.
        var before = trigger == TriggerKind.Automatic ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(30);
        var after = trigger == TriggerKind.Automatic ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(3);
        FocusStart = triggeredAt - before;
        FocusEnd = triggeredAt + after;
        Focus = ticks.Where(t => t.Timestamp >= FocusStart && t.Timestamp <= FocusEnd).ToList();
        Baseline = ticks.Where(t => t.Timestamp < FocusStart).ToList();
    }

    public IReadOnlyList<Tick> Ticks { get; }
    public IReadOnlyList<SystemEvent> Events { get; }
    public DateTimeOffset TriggeredAt { get; }
    public TriggerKind Trigger { get; }
    public DateTimeOffset FocusStart { get; }
    public DateTimeOffset FocusEnd { get; }
    public IReadOnlyList<Tick> Focus { get; }
    public IReadOnlyList<Tick> Baseline { get; }

    public IEnumerable<SystemEvent> FocusEvents(TimeSpan extraBefore) =>
        Events.Where(e => e.Timestamp >= FocusStart - extraBefore && e.Timestamp <= FocusEnd);

    public static List<double> Series(IEnumerable<Tick> ticks, string key) =>
        ticks.Select(t => t.Get(key)).Where(v => v.HasValue).Select(v => v!.Value).ToList();

    /// <summary>Median of the metric before the focus window, or NaN when there is not enough history.</summary>
    public double BaselineMedian(string key, int minSamples = 10)
    {
        var s = Series(Baseline, key);
        return s.Count >= minSamples ? Stats.Median(s) : double.NaN;
    }

    /// <summary>Focus tick with the highest value of the metric.</summary>
    public Tick? PeakTick(string key) =>
        Focus.Where(t => t.Get(key).HasValue).MaxBy(t => t.Get(key)!.Value);

    /// <summary>Number of focus ticks where the predicate holds for the metric.</summary>
    public int CountWhere(string key, Func<double, bool> predicate) =>
        Focus.Count(t => t.Get(key) is { } v && predicate(v));

    /// <summary>Process that used the most of a resource across the focus window (summed per name).</summary>
    public (string Name, double Peak)? TopProcess(Func<ProcessStat, double> selector, IEnumerable<Tick>? ticks = null)
    {
        var best = (ticks ?? Focus)
            .SelectMany(t => t.TopProcesses)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Peak: g.Max(selector), Sum: g.Sum(selector)))
            .Where(x => x.Peak > 0)
            .OrderByDescending(x => x.Sum)
            .FirstOrDefault();
        return best.Name is null ? null : (best.Name, best.Peak);
    }
}
