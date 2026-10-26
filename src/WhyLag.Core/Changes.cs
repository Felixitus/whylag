namespace WhyLag.Core;

public enum ChangeKind { Driver, WindowsUpdate }

/// <summary>A driver or Windows update installed at a certain time (read from the Windows event log).</summary>
public sealed record SystemChange(DateTimeOffset At, ChangeKind Kind, string Description);

/// <summary>How often incidents happened before and after a change.</summary>
public sealed record ChangeImpact(SystemChange Change, int Before, double DaysBefore, int After, double DaysAfter)
{
    public double RateBefore => Before / Math.Max(DaysBefore, 1);
    public double RateAfter => After / Math.Max(DaysAfter, 1);
    public bool Worse => RateAfter >= Math.Max(RateBefore, 0.3) * 2;
}

/// <summary>
/// Compares incident frequency in the week before and after each driver or Windows update to answer
/// "did it get worse since the graphics driver update?". Only clear changes are reported.
/// </summary>
public static class ChangeAnalyzer
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    public static List<ChangeImpact> Compare(IEnumerable<SystemChange> changes, IEnumerable<Incident> incidents, DateTimeOffset now)
    {
        var times = incidents.Where(i => !i.Id.EndsWith("-demo", StringComparison.Ordinal))
            .Select(i => i.TriggeredAt).OrderBy(t => t).ToList();
        if (times.Count == 0) return [];
        var firstRecord = times[0];

        var result = new List<ChangeImpact>();
        // Several drivers installed together count as one change.
        foreach (var change in changes.OrderBy(c => c.At).DistinctBy(c => (c.Kind, c.At.ToLocalTime().Date, c.Description)))
        {
            var beforeStart = Max(change.At - Window, firstRecord);
            var afterEnd = Min(change.At + Window, now);
            var daysBefore = (change.At - beforeStart).TotalDays;
            var daysAfter = (afterEnd - change.At).TotalDays;
            if (daysBefore < 1 || daysAfter < 2) continue; // too little data on one side

            var before = times.Count(t => t >= beforeStart && t < change.At);
            var after = times.Count(t => t >= change.At && t < afterEnd);
            var impact = new ChangeImpact(change, before, daysBefore, after, daysAfter);
            if (impact.Worse && after >= 3) result.Add(impact);
        }
        return result.OrderByDescending(i => i.RateAfter / Math.Max(i.RateBefore, 0.1)).Take(5).ToList();
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
