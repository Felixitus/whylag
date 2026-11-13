namespace WhyLag.Core;

public enum ChangeKind { Driver, WindowsUpdate, Marker }

/// <summary>
/// A driver or Windows update installed at a certain time (read from the Windows event log), or a moment the user
/// marked ("new router", "changed graphics settings").
/// </summary>
public sealed record SystemChange(DateTimeOffset At, ChangeKind Kind, string Description, string? MarkerId = null);

/// <summary>A moment the user marked to compare the time before and after it.</summary>
public sealed record Marker(string Id, DateTimeOffset At, string Text)
{
    public SystemChange AsChange() => new(At, ChangeKind.Marker, Text, Id);
}

/// <summary>The user's markers, in one small JSON file next to the settings.</summary>
public sealed class MarkerStore(string? file = null)
{
    public const int MaxMarkers = 50;
    public const int MaxTextLength = 80;

    public string File { get; } = file ?? Path.Combine(AppPaths.Root, "markers.json");

    public List<Marker> Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return [];
            return (System.Text.Json.JsonSerializer.Deserialize<List<Marker>>(System.IO.File.ReadAllText(File), Json.Options) ?? [])
                .Where(m => m is { Id.Length: > 0, Text.Length: > 0 })
                .OrderByDescending(m => m.At).Take(MaxMarkers).ToList();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public Marker? Add(string text, DateTimeOffset at)
    {
        text = string.Concat(text.Where(c => !char.IsControl(c))).Trim();
        if (text.Length == 0) return null;
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];
        var marker = new Marker(Guid.NewGuid().ToString("N"), at, text);
        Save([marker, .. Load()]);
        return marker;
    }

    public void Remove(string id) => Save(Load().Where(m => m.Id != id).ToList());

    private void Save(List<Marker> markers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        var tmp = File + ".tmp";
        System.IO.File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(markers.Take(MaxMarkers), Json.Options));
        System.IO.File.Move(tmp, File, overwrite: true);
    }
}

/// <summary>How often incidents happened before and after a change.</summary>
public sealed record ChangeImpact(SystemChange Change, int Before, double DaysBefore, int After, double DaysAfter)
{
    public double RateBefore => Before / Math.Max(DaysBefore, 1);
    public double RateAfter => After / Math.Max(DaysAfter, 1);
    public bool Worse => RateAfter >= Math.Max(RateBefore, 0.3) * 2;
}

/// <summary>
/// Compares incident frequency in the week before and after each driver or Windows update to answer
/// "did it get worse since the graphics driver update?". Only clear changes are reported; the user's own markers are
/// always reported, newest first, because the user asked about exactly that moment.
/// </summary>
public static class ChangeAnalyzer
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    public static List<ChangeImpact> Compare(IEnumerable<SystemChange> changes, IEnumerable<Incident> incidents, DateTimeOffset now)
    {
        var times = incidents.Where(i => !i.Id.EndsWith("-demo", StringComparison.Ordinal))
            .Select(i => i.TriggeredAt).OrderBy(t => t).ToList();
        var firstRecord = times.Count > 0 ? times[0] : now;

        var result = new List<ChangeImpact>();
        var marked = new List<ChangeImpact>();
        // Several drivers installed together count as one change.
        foreach (var change in changes.OrderBy(c => c.At).DistinctBy(c => (c.Kind, c.At.ToLocalTime().Date, c.Description)))
        {
            var beforeStart = Max(change.At - Window, firstRecord);
            var afterEnd = Min(change.At + Window, now);
            var daysBefore = (change.At - beforeStart).TotalDays;
            var daysAfter = (afterEnd - change.At).TotalDays;
            var before = times.Count(t => t >= beforeStart && t < change.At);
            var after = times.Count(t => t >= change.At && t < afterEnd);
            var impact = new ChangeImpact(change, before, Math.Max(daysBefore, 0), after, Math.Max(daysAfter, 0));
            if (change.Kind == ChangeKind.Marker)
            {
                marked.Add(impact);
                continue;
            }
            if (daysBefore < 1 || daysAfter < 2) continue; // too little data on one side
            if (impact.Worse && after >= 3) result.Add(impact);
        }
        return marked.OrderByDescending(i => i.Change.At)
            .Concat(result.OrderByDescending(i => i.RateAfter / Math.Max(i.RateBefore, 0.1)).Take(5)).ToList();
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
