namespace WhyLag.Core;

/// <summary>Well-known metric keys. Values are recorded once per tick (1 Hz).</summary>
public static class MetricKeys
{
    public const string CpuTotal = "cpu.total";              // % of all logical cores
    public const string CpuPerformance = "cpu.perf";         // % of nominal frequency (boost > 100)
    public const string MemAvailableMb = "mem.available.mb";
    public const string MemAvailablePct = "mem.available.pct";
    public const string MemPagesIn = "mem.pagesin";          // pages read from disk per second (hard faults)
    public const string DiskActive = "disk.active";          // % of time at least one disk was busy
    public const string DiskLatencyMs = "disk.latency.ms";   // avg. ms per transfer
    public const string NetUpMbps = "net.up.mbps";
    public const string NetDownMbps = "net.down.mbps";
    public const string Gpu3D = "gpu.3d";                    // % 3D engine utilization (busiest adapter)
    public const string GpuVramUsedMb = "gpu.vram.used.mb";  // dedicated memory in use (largest adapter)
    public const string PingGatewayMs = "ping.gateway.ms";
    public const string PingGatewayLost = "ping.gateway.lost";
    public const string PingTargetMs = "ping.target.ms";
    public const string PingTargetLost = "ping.target.lost";
    public const string TickGapMs = "rec.tickgap.ms";        // how late the recorder tick fired
    public const string WifiSignal = "wifi.signal";          // Windows signal quality 0-100 %
    public const string WifiRssi = "wifi.rssi";              // dBm
    public const string WifiRxMbps = "wifi.rx.mbps";         // negotiated link rate
    public const string WifiTxMbps = "wifi.tx.mbps";
    public const string WifiRoam = "wifi.roam";              // 1 when the access point changed since the last tick
    public const string FramesFps = "frames.fps";            // presented frames in this second (app in front)
    public const string FramesMedianMs = "frames.median.ms";
    public const string FramesMaxMs = "frames.max.ms";       // slowest frame in this second
    public const string TcpRetransPerSec = "net.tcp.retrans";     // TCP segments resent per second
    public const string TcpRetransPct = "net.tcp.retrans.pct";    // resent share of sent segments
    public const string DpcTimePct = "cpu.dpc.pct";          // DPC time of the busiest core
    public const string IsrTimePct = "cpu.isr.pct";          // interrupt time of the busiest core
    public const string DpcMaxUs = "lat.dpc.max.us";         // longest single DPC (elevated helper)
    public const string IsrMaxUs = "lat.isr.max.us";         // longest single ISR (elevated helper)

    public static string Unit(string key) => key switch
    {
        CpuTotal or CpuPerformance or MemAvailablePct or DiskActive or Gpu3D or WifiSignal or TcpRetransPct or DpcTimePct or IsrTimePct => "%",
        TcpRetransPerSec => "segments/s",
        DpcMaxUs or IsrMaxUs => "µs",
        MemAvailableMb or GpuVramUsedMb => "MB",
        MemPagesIn => "pages/s",
        DiskLatencyMs or PingGatewayMs or PingTargetMs or TickGapMs or FramesMedianMs or FramesMaxMs => "ms",
        NetUpMbps or NetDownMbps or WifiRxMbps or WifiTxMbps => "Mbit/s",
        WifiRssi => "dBm",
        FramesFps => "fps",
        _ => "",
    };
}

/// <summary>Per-process resource usage at one tick. CPU is normalized to all cores (0-100).</summary>
public sealed record ProcessStat(int Pid, string Name, double CpuPercent, double IoMBps, double GpuPercent, double PrivateMb);

/// <summary>Longest DPC/ISR execution of one driver in one second.</summary>
public sealed record DriverLatency(string Driver, double MaxMicroseconds, int Count);

/// <summary>One second of recorded system state.</summary>
public sealed class Tick
{
    public DateTimeOffset Timestamp { get; init; }
    public Dictionary<string, double> Metrics { get; init; } = new();
    public List<ProcessStat> TopProcesses { get; init; } = new();
    public string? ForegroundProcess { get; set; }
    /// <summary>Program whose frames are measured in this tick (usually the game in front).</summary>
    public string? FrameApp { get; set; }
    /// <summary>Drivers with the longest DPC/ISR times in this second (only with the elevated helper).</summary>
    public List<DriverLatency> Drivers { get; init; } = new();

    public double? Get(string key) => Metrics.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, double value)
    {
        if (!double.IsNaN(value) && !double.IsInfinity(value)) Metrics[key] = value;
    }
}

public enum EventLevel { Critical = 1, Error = 2, Warning = 3, Information = 4 }

/// <summary>A Windows event log entry (or any other discrete event).</summary>
public sealed record SystemEvent(DateTimeOffset Timestamp, string Log, string Provider, int EventId, EventLevel Level, string Message);

public enum TriggerKind { Hotkey, Automatic, Manual }

public enum FindingCategory { Cpu, Disk, Memory, Gpu, Network, System, Background, Events, Frames }

/// <summary>A measured fact that supports a finding. Ids (E1, E2, ...) are unique per incident.</summary>
public sealed record Evidence(string Id, DateTimeOffset Timestamp, string Description, double? Value = null, string? Unit = null);

/// <summary>Result of one deterministic rule. Confidence is a rule heuristic, never produced by the AI.</summary>
public sealed class Finding
{
    public string Id { get; set; } = "";
    public string RuleId { get; init; } = "";
    public FindingCategory Category { get; init; }
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    /// <summary>Program, driver or device the finding is about, if any. Used to find recurring patterns.</summary>
    public string? Subject { get; init; }
    public double Confidence { get; init; }
    public List<Evidence> Evidence { get; init; } = new();
    public List<string> Checks { get; init; } = new();
}

public sealed class MachineInfo
{
    public string OsVersion { get; init; } = Environment.OSVersion.VersionString;
    public int LogicalCores { get; init; } = Environment.ProcessorCount;
    public double TotalMemoryMb { get; init; }
    public List<string> Gpus { get; init; } = new();
}

/// <summary>Optional output of the local AI explainer. Every item references validated evidence.</summary>
public sealed class AiExplanation
{
    public string Model { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public long DurationMs { get; init; }
    public string Summary { get; init; } = "";
    public List<AiExplanationItem> Items { get; init; } = new();
    public List<string> Caveats { get; init; } = new();
    /// <summary>Problems the validator found and removed. Shown to the user for transparency.</summary>
    public List<string> ValidationNotes { get; init; } = new();
}

public sealed class AiExplanationItem
{
    public string FindingId { get; init; } = "";
    public string Likelihood { get; init; } = "";
    public string Explanation { get; init; } = "";
    public List<string> EvidenceIds { get; init; } = new();
    public List<string> NextSteps { get; init; } = new();
}

public sealed class Incident
{
    public string Id { get; init; } = "";
    public DateTimeOffset TriggeredAt { get; init; }
    public TriggerKind Trigger { get; init; }
    public string? TriggerDetail { get; init; }
    public string AppVersion { get; init; } = "";
    public MachineInfo Machine { get; init; } = new();
    public List<Tick> Ticks { get; init; } = new();
    public List<SystemEvent> Events { get; init; } = new();
    public List<Finding> Findings { get; set; } = new();
    public AiExplanation? Explanation { get; set; }
    /// <summary>The user's own note, e.g. what they were doing or what the real cause turned out to be.</summary>
    public string? Note { get; set; }

    public IEnumerable<Evidence> AllEvidence => Findings.SelectMany(f => f.Evidence);
}
