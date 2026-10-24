using static WhyLag.Core.Localization.Loc;

namespace WhyLag.Core;

/// <summary>A cause that showed up in several incidents, with its typical time of day and weekday.</summary>
public sealed class RecurringPattern
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public FindingCategory Category { get; init; }
    public int Count { get; init; }
    public DateTimeOffset First { get; init; }
    public DateTimeOffset Last { get; init; }
    public int[] ByHour { get; init; } = new int[24];
    public int[] ByWeekday { get; init; } = new int[7]; // Monday = 0
    /// <summary>E.g. "usually between 21:00 and 23:00 · mostly on Tuesdays"; empty when there is no clear time.</summary>
    public string When { get; init; } = "";
    public List<string> IncidentIds { get; init; } = new();
}

/// <summary>
/// Groups findings over many incidents by rule and subject (program, driver, app) to answer
/// "does this keep happening, and when?". Purely statistical; no AI involved.
/// </summary>
public static class PatternAnalyzer
{
    public const int MinOccurrences = 3;
    private const double Dominant = 0.6;

    public static List<RecurringPattern> Find(IEnumerable<Incident> incidents)
    {
        var occurrences = incidents
            .Where(i => !i.Id.EndsWith("-demo", StringComparison.Ordinal))
            .SelectMany(i => i.Findings
                .Where(f => f.Confidence >= 0.5)
                .Select(f => (Incident: i, Finding: f, Key: $"{f.RuleId}|{f.Subject?.ToLowerInvariant()}")))
            .GroupBy(x => x.Key);

        var patterns = new List<RecurringPattern>();
        foreach (var group in occurrences)
        {
            // One incident counts once, and several captures within the same hour are one event.
            var items = group.DistinctBy(x => x.Incident.Id).OrderBy(x => x.Incident.TriggeredAt).ToList();
            var distinctHours = items.Select(x => x.Incident.TriggeredAt.ToLocalTime()).DistinctBy(t => (t.Date, t.Hour)).Count();
            if (items.Count < MinOccurrences || distinctHours < MinOccurrences) continue;

            var times = items.Select(x => x.Incident.TriggeredAt.ToLocalTime()).ToList();
            var byHour = new int[24];
            var byWeekday = new int[7];
            foreach (var t in times)
            {
                byHour[t.Hour]++;
                byWeekday[((int)t.DayOfWeek + 6) % 7]++;
            }

            var latest = items[^1].Finding;
            patterns.Add(new RecurringPattern
            {
                Key = group.Key,
                Title = latest.Title,
                Category = latest.Category,
                Count = items.Count,
                First = times[0],
                Last = times[^1],
                ByHour = byHour,
                ByWeekday = byWeekday,
                When = Describe(byHour, byWeekday, times),
                IncidentIds = items.Select(x => x.Incident.Id).ToList(),
            });
        }
        return patterns.OrderByDescending(p => p.Count).ThenByDescending(p => p.Last).ToList();
    }

    internal static string Describe(int[] byHour, int[] byWeekday, List<DateTimeOffset> times)
    {
        var total = times.Count;
        var parts = new List<string>();

        // Best two-hour window (wrapping around midnight).
        var bestHour = Enumerable.Range(0, 24).MaxBy(h => (byHour[h] + byHour[(h + 1) % 24], byHour[h])); // tie: start at the busy hour
        if (byHour[bestHour] + byHour[(bestHour + 1) % 24] >= total * Dominant)
            parts.Add(T("pattern.hours", $"{bestHour:00}:00", $"{(bestHour + 2) % 24:00}:00"));

        var distinctDays = times.Select(t => t.Date).Distinct().Count();
        var bestDay = Enumerable.Range(0, 7).MaxBy(d => byWeekday[d]);
        if (distinctDays >= MinOccurrences && byWeekday[bestDay] >= total * Dominant)
            parts.Add(T("pattern.weekday", Culture.DateTimeFormat.GetDayName((DayOfWeek)((bestDay + 1) % 7))));

        return string.Join(" · ", parts);
    }
}
