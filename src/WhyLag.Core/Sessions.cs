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
    private readonly RingBuffer<double> _medians = new(120);
    private int _hitches;
    private readonly List<Incident> _incidents = new();

    public string? CurrentApp => _app is not null && _lastSeen - _start >= StartAfter ? _app : null;

    /// <summary>Feeds one tick; returns a finished session (at least five minutes long) when one just ended.</summary>
    public GameSession? Feed(Tick tick)
    {
        var playing = tick.ForegroundFullscreen == true && tick.FrameApp is { } app && tick.ForegroundProcess is { } fg
                      && app.Equals(fg, StringComparison.OrdinalIgnoreCase) && tick.Get(MetricKeys.FramesFps) >= 20;

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
    public string Directory { get; } = directory ?? Path.Combine(AppPaths.Root, "sessions");

    public void Save(GameSession session)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(Path.Combine(Directory, $"{session.Id}.json"), JsonSerializer.Serialize(session, Json.Options));
    }

    public List<GameSession> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(f =>
            {
                try { return JsonSerializer.Deserialize<GameSession>(File.ReadAllText(f), Json.Options); }
                catch (JsonException) { return null; }
            })
            .OfType<GameSession>()
            .OrderByDescending(s => s.Start)
            .ToList();
    }

    public void Delete(string id)
    {
        var path = Path.Combine(Directory, $"{id}.json");
        if (File.Exists(path)) File.Delete(path);
    }
}
