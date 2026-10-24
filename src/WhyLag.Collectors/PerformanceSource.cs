using System.Globalization;
using WhyLag.Core;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.Collectors;

/// <summary>
/// System-wide and per-process metrics from Windows performance counters. Needs no admin rights.
/// GPU counters come from WDDM and work the same on AMD, NVIDIA and Intel.
/// </summary>
public sealed class PerformanceSource : IMetricSource
{
    private const string CpuPerf = @"\Processor Information(_Total)\% Processor Performance";
    private const string MemPagesIn = @"\Memory\Pages Input/sec";
    private const string DiskIdle = @"\PhysicalDisk(_Total)\% Idle Time";
    private const string DiskLatency = @"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer";
    private const string Gpu3D = @"\GPU Engine(*engtype_3D)\Utilization Percentage";
    private const string GpuDedicated = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string DpcTime = @"\Processor Information(*)\% DPC Time";
    private const string IsrTime = @"\Processor Information(*)\% Interrupt Time";

    private const int TopPerResource = 5;
    private static readonly HashSet<string> SkippedProcesses = new(StringComparer.OrdinalIgnoreCase) { "_Total", "Idle", "System Idle Process" };

    private readonly int _cores = Environment.ProcessorCount;
    private readonly CpuTimes _cpu = new();
    private readonly NetworkThroughput _net = new();
    private readonly DiskPerformance _disk = new();
    private readonly TcpRetransmissions _tcp = new();
    private readonly List<string> _warnings = new();
    private PdhQuery? _query;
    private ProcessCounters? _proc;

    public string Name => WhyLag.Core.Localization.Loc.T("source.performance");

    /// <summary>Counters that do not exist on this system (shown in diagnostics).</summary>
    public List<string> MissingCounters { get; } = new();

    public IReadOnlyList<string> Warnings => _warnings;

    public void Start()
    {
        // CPU, memory and network come from direct Windows APIs; counters are only used where no API exists.
        _cpu.Sample();
        _net.Sample();
        _disk.Open();
        _disk.Sample();
        _tcp.Sample();

        _query = new PdhQuery();
        var counters = new List<string> { CpuPerf, MemPagesIn, Gpu3D, GpuDedicated, DpcTime, IsrTime };
        if (_disk.DiskCount == 0) counters.AddRange([DiskIdle, DiskLatency]);
        foreach (var path in counters)
        {
            if (!_query.TryAdd(path)) MissingCounters.Add(path);
        }

        // "Process V2" (Windows 11) names instances "name:pid", which avoids the ambiguous "name#2" instances.
        _proc = ProcessCounters.TryCreate(_query, @"\Process V2(*)", hasIdCounter: false, MissingCounters)
                ?? ProcessCounters.TryCreate(_query, @"\Process(*)", hasIdCounter: true, MissingCounters);

        if (_proc is null) _warnings.Add(T("warn.process"));
        if (MissingCounters.Count > 0)
            _warnings.Add(T("warn.counters"));

        _query.Collect(); // rate counters need a first sample
    }

    public void Collect(Tick tick)
    {
        var q = _query ?? throw new InvalidOperationException("Not started.");
        q.Collect();

        if (_cpu.Sample() is { } cpu) tick.Set(MetricKeys.CpuTotal, cpu);
        if (q.Value(CpuPerf) is { } perf) tick.Set(MetricKeys.CpuPerformance, perf);
        if (HardwareInfo.Memory() is { } mem)
        {
            tick.Set(MetricKeys.MemAvailableMb, mem.AvailableMb);
            if (mem.TotalMb > 0) tick.Set(MetricKeys.MemAvailablePct, mem.AvailableMb / mem.TotalMb * 100);
        }
        if (q.Value(MemPagesIn) is { } pages) tick.Set(MetricKeys.MemPagesIn, pages);
        if (_disk.Sample() is { } disk)
        {
            tick.Set(MetricKeys.DiskActive, disk.ActivePct);
            if (disk.LatencyMs is { } ms) tick.Set(MetricKeys.DiskLatencyMs, ms);
        }
        else
        {
            if (q.Value(DiskIdle) is { } idle) tick.Set(MetricKeys.DiskActive, Math.Clamp(100 - idle, 0, 100));
            if (q.Value(DiskLatency) is { } latency) tick.Set(MetricKeys.DiskLatencyMs, latency * 1000);
        }
        if (_net.Sample() is { } net)
        {
            tick.Set(MetricKeys.NetUpMbps, net.UpMbps);
            tick.Set(MetricKeys.NetDownMbps, net.DownMbps);
        }
        if (_tcp.Sample() is { } tcp)
        {
            tick.Set(MetricKeys.TcpRetransPerSec, tcp.PerSecond);
            tick.Set(MetricKeys.TcpRetransPct, tcp.Percent);
        }
        // Drivers usually run their DPCs/interrupts on one core, so the busiest core matters, not the average.
        if (BusiestCore(q.Values(DpcTime)) is { } dpc) tick.Set(MetricKeys.DpcTimePct, dpc);
        if (BusiestCore(q.Values(IsrTime)) is { } isr) tick.Set(MetricKeys.IsrTimePct, isr);

        var gpuByPid = new Dictionary<int, double>();
        var gpuByAdapter = new Dictionary<string, double>();
        foreach (var (instance, value) in q.Values(Gpu3D))
        {
            if (ParseGpuInstance(instance) is not { } parsed) continue;
            gpuByPid[parsed.Pid] = gpuByPid.GetValueOrDefault(parsed.Pid) + value;
            gpuByAdapter[parsed.Luid] = gpuByAdapter.GetValueOrDefault(parsed.Luid) + value;
        }
        if (q.Has(Gpu3D)) tick.Set(MetricKeys.Gpu3D, gpuByAdapter.Count == 0 ? 0 : Math.Min(100, gpuByAdapter.Values.Max()));
        var vram = q.Values(GpuDedicated);
        if (vram.Count > 0) tick.Set(MetricKeys.GpuVramUsedMb, vram.Max(x => x.Value) / (1024 * 1024));

        if (_proc is not null) tick.TopProcesses.AddRange(TopProcesses(_proc.Read(q, _cores), gpuByPid));
    }

    private static double? BusiestCore(List<(string Instance, double Value)> values)
    {
        var cores = values.Where(v => !v.Instance.Contains("_Total", StringComparison.Ordinal)).ToList();
        return cores.Count == 0 ? null : Math.Min(100, cores.Max(v => v.Value));
    }

    private static IEnumerable<ProcessStat> TopProcesses(List<ProcessStat> all, Dictionary<int, double> gpuByPid)
    {
        var withGpu = all
            .Where(p => !SkippedProcesses.Contains(p.Name) && p.Pid != 0)
            .Select(p => gpuByPid.TryGetValue(p.Pid, out var g) ? p with { GpuPercent = Math.Min(100, g) } : p)
            .ToList();

        var selected = new Dictionary<int, ProcessStat>();
        foreach (var p in withGpu.OrderByDescending(p => p.CpuPercent).Take(TopPerResource).Where(p => p.CpuPercent >= 0.5)) selected[p.Pid] = p;
        foreach (var p in withGpu.OrderByDescending(p => p.IoMBps).Take(TopPerResource).Where(p => p.IoMBps >= 0.5)) selected[p.Pid] = p;
        foreach (var p in withGpu.OrderByDescending(p => p.GpuPercent).Take(3).Where(p => p.GpuPercent >= 1)) selected[p.Pid] = p;
        foreach (var p in withGpu.OrderByDescending(p => p.PrivateMb).Take(3)) selected[p.Pid] = p;
        return selected.Values.Select(p => p with
        {
            CpuPercent = Stats.Round(p.CpuPercent),
            IoMBps = Stats.Round(p.IoMBps),
            GpuPercent = Stats.Round(p.GpuPercent),
            PrivateMb = Math.Round(p.PrivateMb),
        });
    }

    /// <summary>Parses "pid_1234_luid_0x00000000_0x0000D1B5_phys_0_eng_0_engtype_3D".</summary>
    internal static (int Pid, string Luid)? ParseGpuInstance(string instance)
    {
        var parts = instance.Split('_');
        if (parts.Length < 5 || parts[0] != "pid" || parts[2] != "luid") return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
        return (pid, parts[3] + "_" + parts[4]);
    }

    public void Dispose()
    {
        _query?.Dispose();
        _disk.Dispose();
    }

    private sealed class ProcessCounters
    {
        private readonly string _cpu, _io, _private, _id;
        private readonly bool _hasIdCounter;

        private ProcessCounters(string set, bool hasIdCounter)
        {
            _cpu = set + @"\% Processor Time";
            _io = set + @"\IO Data Bytes/sec";
            _private = set + @"\Working Set - Private";
            _id = set + @"\ID Process";
            _hasIdCounter = hasIdCounter;
        }

        public static ProcessCounters? TryCreate(PdhQuery q, string set, bool hasIdCounter, List<string> missing)
        {
            var c = new ProcessCounters(set, hasIdCounter);
            if (!q.TryAdd(c._cpu)) return null;
            if (hasIdCounter && !q.TryAdd(c._id)) return null;
            if (!q.TryAdd(c._io)) missing.Add(c._io);
            if (!q.TryAdd(c._private)) missing.Add(c._private);
            return c;
        }

        public List<ProcessStat> Read(PdhQuery q, int cores)
        {
            static Dictionary<string, double> Map(List<(string Instance, double Value)> values) =>
                values.DistinctBy(x => x.Instance).ToDictionary(x => x.Instance, x => x.Value);
            var io = Map(q.Values(_io));
            var priv = Map(q.Values(_private));
            var ids = _hasIdCounter ? Map(q.Values(_id)) : null;

            var result = new List<ProcessStat>();
            foreach (var (instance, cpu) in q.Values(_cpu).DistinctBy(x => x.Instance))
            {
                string name;
                int pid;
                if (ids is null)
                {
                    var colon = instance.LastIndexOf(':');
                    if (colon <= 0 || !int.TryParse(instance[(colon + 1)..], out pid)) continue;
                    name = instance[..colon];
                }
                else
                {
                    if (!ids.TryGetValue(instance, out var id)) continue;
                    pid = (int)id;
                    var hash = instance.IndexOf('#');
                    name = hash > 0 ? instance[..hash] : instance;
                }
                result.Add(new ProcessStat(pid, name, cpu / cores,
                    io.GetValueOrDefault(instance) / (1024 * 1024),
                    0,
                    priv.GetValueOrDefault(instance) / (1024 * 1024)));
            }
            return result;
        }
    }
}
