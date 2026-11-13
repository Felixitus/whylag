using System.Text.Json;

namespace WhyLag.Core;

/// <summary>Summary of one gaming session (a fullscreen program rendering continuously for a while).</summary>
public sealed class GameSession
{
    public string Id { get; init; } = "";
    public string App { get; init; } = "";
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public double AverageFps { get; init; }
    /// <summary>Frame rate of the slowest 1 % of seconds.</summary>
    public double LowFps { get; init; }
    /// <summary>Seconds in which a frame took several times longer than usual.</summary>
    public int Hitches { get; init; }
    public double AverageCpu { get; init; }
    public double AverageGpu { get; init; }
    public double? AveragePing { get; init; }
    public List<string> IncidentIds { get; init; } = new();
    /// <summary>Most frequent incident causes during the session, most frequent first.</summary>
    public List<SessionCause> Causes { get; init; } = new();

    public TimeSpan Duration => End - Start;
}

public sealed record SessionCause(string Title, FindingCategory Category, int Count);

public enum SessionVerdict { NoHistory, Usual, Better, Worse }

/// <summary>How a session compares with the earlier sessions of the same game (their medians).</summary>
public sealed record SessionComparison(SessionVerdict Verdict, int Earlier, double UsualFps, double UsualLowFps, double UsualHitchesPerHour);

/// <summary>
/// "Worse than your usual Deadlock" says more than absolute numbers: every game and PC has its own normal. A session is
/// compared with the medians of up to ten earlier sessions of the same game; at least three are needed.
/// </summary>
public static class SessionBaseline
{
    public const int MinimumHistory = 3;

    public static double HitchesPerHour(GameSession s) => s.Hitches / Math.Max(s.Duration.TotalHours, 1.0 / 12);

    public static SessionComparison Compare(GameSession session, IEnumerable<GameSession> all)
    {
        var earlier = all
            .Where(s => s.Start < session.Start && s.App.Equals(session.App, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Start)
            .Take(10)
            .ToList();
        if (earlier.Count < MinimumHistory) return new SessionComparison(SessionVerdict.NoHistory, earlier.Count, 0, 0, 0);

        var fps = Stats.Median(earlier.Select(s => s.AverageFps).ToList());
        var low = Stats.Median(earlier.Select(s => s.LowFps).ToList());
        var hitches = Stats.Median(earlier.Select(HitchesPerHour).ToList());
        var rate = HitchesPerHour(session);

        var worse = session.AverageFps < fps * 0.9 || session.LowFps < low * 0.85 || rate > Math.Max(hitches * 2, hitches + 3);
        var better = !worse && (session.AverageFps > fps * 1.1 || session.LowFps > low * 1.15) && rate <= hitches + 1;
        return new SessionComparison(worse ? SessionVerdict.Worse : better ? SessionVerdict.Better : SessionVerdict.Usual, earlier.Count, fps, low, hitches);
    }
}

/// <summary>
/// Detects gaming sessions from ticks: the program in front fills the screen and presents frames continuously.
/// A session starts after one minute and ends after 90 seconds without that program in front.
/// </summary>
public sealed class SessionTracker
{
    public static readonly TimeSpan StartAfter = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan EndAfter = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan MinimumLength = TimeSpan.FromMinutes(5);

    private string? _app;
    private DateTimeOffset _start, _lastSeen;
    private readonly List<double> _fps = new();
    private readonly List<double> _cpu = new();
    private readonly List<double> _gpu = new();
    private readonly List<double> _ping = new();
    private RingBuffer<double> _medians = new(120);
    private int _hitches;
    private readonly List<Incident> _incidents = new();

    public string? CurrentApp => _app is not null && _lastSeen - _start >= StartAfter ? _app : null;

    /// <summary>Feeds one tick; returns a finished session (at least five minutes long) when one just ended.</summary>
    public GameSession? Feed(Tick tick)
    {
        var playing = tick.ForegroundFullscreen == true && tick.FrameApp is { } app && tick.ForegroundProcess is { } fg
                      && app.Equals(fg, StringComparison.OrdinalIgnoreCase) && !GamePrograms.IsNonGame(app) && tick.Get(MetricKeys.FramesFps) >= 20;

        if (playing && _app is not null && !_app.Equals(tick.FrameApp, StringComparison.OrdinalIgnoreCase))
        {
            var finished = Finish();
            Begin(tick);
            Add(tick);
            return finished;
        }
        if (playing)
        {
            if (_app is null) Begin(tick);
            Add(tick);
            return null;
        }
        return _app is not null && tick.Timestamp - _lastSeen >= EndAfter ? Finish() : null;
    }

    public void NoteIncident(Incident incident)
    {
        if (CurrentApp is not null) _incidents.Add(incident);
    }

    private void Begin(Tick tick)
    {
        _app = tick.FrameApp;
        _start = tick.Timestamp;
        _fps.Clear();
        _cpu.Clear();
        _gpu.Clear();
        _ping.Clear();
        _hitches = 0;
        _medians = new RingBuffer<double>(120);
        _incidents.Clear();
    }

    private void Add(Tick tick)
    {
        _lastSeen = tick.Timestamp;
        _fps.Add(tick.Get(MetricKeys.FramesFps)!.Value);
        if (tick.Get(MetricKeys.CpuTotal) is { } cpu) _cpu.Add(cpu);
        if (tick.Get(MetricKeys.Gpu3D) is { } gpu) _gpu.Add(gpu);
        if (tick.Get(MetricKeys.PingTargetMs) is { } ping) _ping.Add(ping);
        if (tick.Get(MetricKeys.FramesMedianMs) is { } median && tick.Get(MetricKeys.FramesMaxMs) is { } max)
        {
            var history = _medians.Snapshot();
            if (history.Count >= 10 && max >= Math.Max(50, Stats.Median(history) * 3)) _hitches++;
            _medians.Add(median);
        }
    }

    private GameSession? Finish()
    {
        var app = _app!;
        _app = null;
        if (_lastSeen - _start < MinimumLength || _fps.Count == 0) return null;

        var sorted = _fps.OrderBy(f => f).ToList();
        return new GameSession
        {
            Id = $"{_start.ToLocalTime():yyyyMMdd-HHmmss}-{app}",
            App = app,
            Start = _start,
            End = _lastSeen,
            AverageFps = Math.Round(_fps.Average(), 1),
            LowFps = Math.Round(sorted[(int)Math.Floor((sorted.Count - 1) * 0.01)], 1),
            Hitches = _hitches,
            AverageCpu = _cpu.Count == 0 ? 0 : Math.Round(_cpu.Average(), 1),
            AverageGpu = _gpu.Count == 0 ? 0 : Math.Round(_gpu.Average(), 1),
            AveragePing = _ping.Count == 0 ? null : Math.Round(_ping.Average(), 1),
            IncidentIds = _incidents.Select(i => i.Id).ToList(),
            Causes = _incidents
                .Select(i => i.Findings.FirstOrDefault(f => f.Category != FindingCategory.Frames) ?? i.Findings.FirstOrDefault())
                .OfType<Finding>()
                .GroupBy(f => f.Title)
                .Select(g => new SessionCause(g.Key, g.First().Category, g.Count()))
                .OrderByDescending(c => c.Count)
                .Take(5)
                .ToList(),
        };
    }
}

/// <summary>Stores session summaries as small JSON files next to the incidents.</summary>
public sealed class SessionStore(string? directory = null)
{
    private const int MaxSessions = 300;

    public string Directory { get; } = directory ?? Path.Combine(AppPaths.Root, "sessions");

    public void Save(GameSession session)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var name = string.Concat(session.Id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        File.WriteAllText(Path.Combine(Directory, $"{name}.json"), JsonSerializer.Serialize(session, Json.Options));
        foreach (var old in System.IO.Directory.GetFiles(Directory, "*.json").OrderDescending(StringComparer.Ordinal).Skip(MaxSessions)) File.Delete(old);
    }

    public List<GameSession> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(f =>
            {
                try { return JsonSerializer.Deserialize<GameSession>(File.ReadAllText(f), Json.Options); }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
            })
            .OfType<GameSession>()
            .Where(s => !GamePrograms.IsNonGame(s.App)) // saved by versions that counted browsers as games
            .OrderByDescending(s => s.Start)
            .ToList();
    }
}
