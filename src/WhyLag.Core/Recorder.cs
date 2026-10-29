using System.Diagnostics;
using WhyLag.Core.Rules;

namespace WhyLag.Core;

/// <summary>
/// Keeps the rolling in-memory recording and turns it into incidents on request (hotkey/manual)
/// or when the spike detector fires. Sources that fail repeatedly are disabled, never fatal.
/// </summary>
public sealed class Recorder : IDisposable
{
    private const int MaxConsecutiveFailures = 5;
    private static readonly TimeSpan SavedHistory = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AutoCaptureDelay = TimeSpan.FromSeconds(10);

    private volatile WhyLagSettings _settings;
    private readonly List<IMetricSource> _metricSources;
    private readonly List<IEventSource> _eventSources;
    private readonly MachineInfo _machine;
    private readonly IncidentStore? _store;
    private readonly RingBuffer<Tick> _ticks;
    private readonly RingBuffer<SystemEvent> _events = new(2000);
    private readonly SpikeDetector _detector = new();
    private readonly SessionTracker _sessions = new();
    private readonly LongTermHistory _longTerm = new();
    private readonly Dictionary<string, SourceStatus> _status = new();
    private readonly Dictionary<IMetricSource, int> _failures = new();
    private readonly Lock _statusLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _captureLock = new();
    private Task? _loop;
    private SpikeSignal? _pendingAuto;
    private DateTimeOffset _lastAutoCapture = DateTimeOffset.MinValue;

    public Recorder(WhyLagSettings settings, IEnumerable<IMetricSource> metricSources, IEnumerable<IEventSource> eventSources,
        MachineInfo machine, IncidentStore? store = null)
    {
        _settings = settings;
        _metricSources = metricSources.ToList();
        _eventSources = eventSources.ToList();
        _machine = machine;
        _store = store;
        _ticks = new RingBuffer<Tick>(Math.Clamp(settings.BufferMinutes, 1, 120) * 60);
        _detector.IgnoredApps = new HashSet<string>(settings.IgnoredApps, StringComparer.OrdinalIgnoreCase);
        _detector.DetectNetwork = settings.AutoDetectNetwork;
    }

    public event Action<Incident>? IncidentCaptured;
    public event Action<Tick>? TickRecorded;
    /// <summary>Raised (on the recorder thread) when a gaming session of at least five minutes ended.</summary>
    public event Action<GameSession>? SessionEnded;

    /// <summary>The game currently being played, if a session is running.</summary>
    public string? CurrentSessionApp => _sessions.CurrentApp;

    public bool Paused { get; set; }

    /// <summary>Automatic detection is suspended until this time (e.g. during a long build or a video render).</summary>
    public DateTimeOffset AutoDetectPausedUntil { get; set; }

    /// <summary>Applies settings that do not change what is recorded (auto-detection, retention).</summary>
    public void UpdateSettings(WhyLagSettings settings)
    {
        _settings = settings;
        _detector.IgnoredApps = new HashSet<string>(settings.IgnoredApps, StringComparer.OrdinalIgnoreCase);
        _detector.DetectNetwork = settings.AutoDetectNetwork;
    }
    public int BufferedSeconds => _ticks.Count;
    public Tick? LatestTick => _ticks.Latest();

    /// <summary>The most recent ticks, oldest first (for live views).</summary>
    public List<Tick> RecentTicks(int count)
    {
        var all = _ticks.Snapshot();
        return all.Count <= count ? all : all.GetRange(all.Count - count, count);
    }

    public IReadOnlyList<SourceStatus> Status
    {
        get { lock (_statusLock) return _status.Values.ToList(); }
    }

    public void Start()
    {
        foreach (var source in _metricSources)
        {
            try
            {
                source.Start();
                SetStartedStatus(source.Name, source.Warnings);
            }
            catch (Exception ex)
            {
                SetStatus(source.Name, SourceState.Failed, ex.Message);
            }
        }
        foreach (var source in _eventSources)
        {
            try
            {
                source.Start(e => _events.Add(e));
                SetStartedStatus(source.Name, source.Warnings);
            }
            catch (Exception ex)
            {
                SetStatus(source.Name, SourceState.Failed, ex.Message);
            }
        }
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var clock = Stopwatch.StartNew();
        TimeSpan? last = null;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var now = clock.Elapsed;
            var gapMs = last is null ? 0 : Math.Max(0, (now - last.Value).TotalMilliseconds - 1000);
            last = now;
            if (Paused) continue;

            var tick = new Tick { Timestamp = DateTimeOffset.Now };
            tick.Set(MetricKeys.TickGapMs, gapMs);
            CollectAll(tick);
            _ticks.Add(tick);
            TickRecorded?.Invoke(tick);
            _longTerm.Add(tick);
            if (_sessions.Feed(tick) is { } session) SessionEnded?.Invoke(session);

            if (_settings.AutoDetect && tick.Timestamp >= AutoDetectPausedUntil) HandleAutoDetect(tick);
        }
    }

    private void CollectAll(Tick tick)
    {
        foreach (var source in _metricSources)
        {
            _failures.TryGetValue(source, out var failures);
            if (failures >= MaxConsecutiveFailures) continue;
            try
            {
                source.Collect(tick);
                if (failures > 0) SetStatus(source.Name, SourceState.Running, null);
                _failures[source] = 0;
            }
            catch (Exception ex)
            {
                _failures[source] = ++failures;
                SetStatus(source.Name, failures >= MaxConsecutiveFailures ? SourceState.Failed : SourceState.Degraded, ex.Message);
            }
        }
    }

    private void HandleAutoDetect(Tick tick)
    {
        if (_pendingAuto is null && _detector.Feed(tick) is { } signal
            && signal.Timestamp - _lastAutoCapture >= TimeSpan.FromSeconds(_settings.AutoDetectCooldownSeconds))
        {
            _pendingAuto = signal;
        }
        else if (_pendingAuto is null)
        {
            return;
        }

        // Wait a few seconds after the spike so the incident contains what happened right after it.
        if (_pendingAuto is { } pending && tick.Timestamp - pending.Timestamp >= AutoCaptureDelay)
        {
            _pendingAuto = null;
            _lastAutoCapture = pending.Timestamp;
            Capture(TriggerKind.Automatic, pending.Description, pending.Timestamp);
        }
    }

    /// <summary>Freezes the current recording into an incident, runs all rules and stores it.</summary>
    public Incident Capture(TriggerKind trigger, string? detail = null, DateTimeOffset? at = null)
    {
        lock (_captureLock)
        {
            var triggeredAt = at ?? DateTimeOffset.Now;
            var from = triggeredAt - SavedHistory;
            var ticks = _ticks.Snapshot().Where(t => t.Timestamp >= from).ToList();
            var events = _events.Snapshot().Where(e => e.Timestamp >= from).ToList();

            var longTerm = _longTerm.Last(TimeSpan.FromHours(6));
            var ctx = new IncidentContext(ticks, events, triggeredAt, trigger) { LongTerm = longTerm };
            var incident = new Incident
            {
                Id = $"{triggeredAt.ToLocalTime():yyyyMMdd-HHmmss}-{trigger.ToString().ToLowerInvariant()}",
                TriggeredAt = triggeredAt,
                Trigger = trigger,
                TriggerDetail = detail,
                AppVersion = typeof(Recorder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                Machine = _machine,
                Ticks = ticks,
                Events = events,
                LongTerm = longTerm,
                Findings = RuleEngine.Evaluate(ctx),
            };

            if (_store is not null)
            {
                _store.Save(incident);
                _store.Prune(_settings.KeepIncidents);
            }
            _sessions.NoteIncident(incident);
            IncidentCaptured?.Invoke(incident);
            return incident;
        }
    }

    private void SetStartedStatus(string name, IReadOnlyList<string> warnings) =>
        SetStatus(name, warnings.Count == 0 ? SourceState.Running : SourceState.Degraded,
            warnings.Count == 0 ? null : string.Join(" ", warnings));

    private void SetStatus(string name, SourceState state, string? message)
    {
        lock (_statusLock) _status[name] = new SourceStatus(name, state, message);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        foreach (var s in _metricSources) s.Dispose();
        foreach (var s in _eventSources) s.Dispose();
        _cts.Dispose();
    }
}
