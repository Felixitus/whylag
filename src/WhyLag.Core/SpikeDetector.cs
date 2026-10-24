using static WhyLag.Core.Localization.Loc;

namespace WhyLag.Core;

public sealed record SpikeSignal(DateTimeOffset Timestamp, string Metric, double Value, double Baseline, string Description);

/// <summary>
/// Detects moments worth an automatic incident. Deliberately conservative: only signals that are
/// rarely "normal" (system stalls, network latency spikes, packet loss). Full CPU or GPU load is
/// expected during games and builds, so it never triggers on its own.
/// </summary>
public sealed class SpikeDetector
{
    private readonly RingBuffer<double> _gatewayHistory = new(120);
    private readonly RingBuffer<double> _targetHistory = new(120);
    private readonly RingBuffer<double> _frameHistory = new(120);
    private int _targetLostStreak;

    /// <summary>
    /// Programs that render continuously but are not games. Their frame hiccups (a paused video, an idle tab)
    /// are not worth an automatic incident; a manual capture still shows their frame times.
    /// </summary>
    private static readonly HashSet<string> NonGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "brave", "chrome", "msedge", "firefox", "opera", "vivaldi", "arc", "msedgewebview2", "discord", "teams", "ms-teams",
        "slack", "zoom", "spotify", "vlc", "mpc-hc64", "potplayermini64", "obs64", "explorer", "code", "devenv",
        "whatsapp", "telegram", "steamwebhelper", "epicgameslauncher", "battle.net",
    };

    public SpikeSignal? Feed(Tick tick)
    {
        // Every detector sees every tick, so their histories stay complete even when another one fires.
        var frames = FrameSpike(tick);
        var internet = Latency(tick, MetricKeys.PingTargetMs, _targetHistory, T("net.target"));
        var router = Latency(tick, MetricKeys.PingGatewayMs, _gatewayHistory, T("net.router"));
        // A single lost ping is common; two in a row is a real interruption.
        _targetLostStreak = tick.Get(MetricKeys.PingTargetLost) >= 1 ? _targetLostStreak + 1 : 0;

        if (tick.Get(MetricKeys.TickGapMs) is >= 2000 and var gap)
            return new SpikeSignal(tick.Timestamp, MetricKeys.TickGapMs, gap, 0, T("spike.stall", Stats.Round(gap)));
        return frames ?? internet ?? router ?? (_targetLostStreak == 2
            ? new SpikeSignal(tick.Timestamp, MetricKeys.PingTargetLost, 2, 0, T("spike.loss"))
            : null);
    }

    /// <summary>
    /// A frame several times slower than usual in a game running smoothly (≥ 30 fps). Loading screens
    /// usually drop the frame rate first, so they rarely trigger this.
    /// </summary>
    private SpikeSignal? FrameSpike(Tick tick)
    {
        if (tick.Get(MetricKeys.FramesMaxMs) is not { } max || tick.Get(MetricKeys.FramesMedianMs) is not { } median
            || tick.Get(MetricKeys.FramesFps) < 20 || (tick.FrameApp is { } app && NonGames.Contains(app))) return null;
        var history = _frameHistory.Snapshot();
        _frameHistory.Add(median);
        if (history.Count < 30) return null;

        var typical = Stats.Median(history);
        if (typical > 33) return null;
        return max >= Math.Max(100, typical * 5)
            ? new SpikeSignal(tick.Timestamp, MetricKeys.FramesMaxMs, max, typical,
                T("spike.frame", tick.FrameApp ?? T("frames.game"), Stats.Round(max), Stats.Round(typical)))
            : null;
    }

    private static SpikeSignal? Latency(Tick tick, string key, RingBuffer<double> history, string what)
    {
        if (tick.Get(key) is not { } ms) return null;
        var values = history.Snapshot();
        history.Add(ms);
        if (values.Count < 30) return null;

        var median = Stats.Median(values);
        var mad = Stats.Mad(values);
        var threshold = Math.Max(median + 50, median + 8 * Math.Max(mad, 1));
        return ms >= threshold
            ? new SpikeSignal(tick.Timestamp, key, ms, median, T("spike.ping", what, Stats.Round(ms), Stats.Round(median)))
            : null;
    }
}
