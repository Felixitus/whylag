using System.Diagnostics;
using System.Runtime.InteropServices;
using WhyLag.Core;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.Collectors;

/// <summary>Latency summary of one second.</summary>
public sealed record LatencySecond(DateTimeOffset Timestamp, double DpcMaxUs, double IsrMaxUs, List<DriverLatency> Drivers);

/// <summary>
/// Measures how long drivers run in deferred procedure calls (DPC) and interrupt service routines (ISR) using the
/// kernel event trace ("NT Kernel Logger"). Long DPCs/ISRs block a CPU core and cause audio crackles, input lag and
/// hitches. Needs administrator rights, which is why WhyLag runs it in a separate, optional helper process.
/// </summary>
public sealed class KernelLatencyTracer : IDisposable
{
    private const string KernelLoggerName = "NT Kernel Logger";
    private static readonly Guid SystemTraceControlGuid = new("9e814aad-3204-11d2-9a82-006008a86939");
    private static readonly Guid PerfInfoGuid = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");

    private const uint EventTraceFlagDpc = 0x20;
    private const uint EventTraceFlagInterrupt = 0x40;
    private const uint EventTraceRealTimeMode = 0x100;
    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint ProcessTraceModeRealTime = 0x100;
    private const uint ProcessTraceModeRawTimestamp = 0x1000;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const uint EventTraceControlStop = 1;
    private const int PropertiesSize = 120;
    private const int LogFileSize = 448;
    private const int ErrorAlreadyExists = 183;

    private readonly object _lock = new();
    private readonly EventRecordCallback _callback;
    private readonly double _ticksPerMicrosecond = Stopwatch.Frequency / 1_000_000.0;
    private Dictionary<ulong, (double Max, int Count)> _dpc = new();
    private Dictionary<ulong, (double Max, int Count)> _isr = new();
    private DriverMap _drivers = DriverMap.Load();
    private DateTime _driversLoaded = DateTime.UtcNow;
    private ulong _sessionHandle;
    private ulong _traceHandle;
    private Thread? _thread;

    public KernelLatencyTracer() => _callback = OnEvent;

    /// <summary>Starts the kernel trace. Throws with a readable message when it is not allowed or busy.</summary>
    public void Start()
    {
        var props = Properties();
        try
        {
            var status = StartTraceW(out _sessionHandle, KernelLoggerName, props);
            if (status == ErrorAlreadyExists)
                throw new InvalidOperationException(T("err.kernel.busy"));
            if (status == 5)
                throw new UnauthorizedAccessException(T("err.kernel.admin"));
            if (status != 0) throw new InvalidOperationException(T("err.kernel.start", status));
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }

        var logFile = Marshal.AllocHGlobal(LogFileSize);
        var loggerName = Marshal.StringToHGlobalUni(KernelLoggerName);
        try
        {
            for (var i = 0; i < LogFileSize; i++) Marshal.WriteByte(logFile, i, 0);
            Marshal.WriteIntPtr(logFile, 8, loggerName);
            Marshal.WriteInt32(logFile, 28, unchecked((int)(ProcessTraceModeRealTime | ProcessTraceModeEventRecord | ProcessTraceModeRawTimestamp)));
            Marshal.WriteIntPtr(logFile, 424, Marshal.GetFunctionPointerForDelegate(_callback));
            _traceHandle = OpenTraceW(logFile);
            if (_traceHandle == ulong.MaxValue)
            {
                Stop();
                throw new InvalidOperationException(T("err.kernel.open", Marshal.GetLastWin32Error()));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(logFile);
            Marshal.FreeHGlobal(loggerName);
        }

        _thread = new Thread(() => ProcessTrace([_traceHandle], 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "WhyLag kernel trace" };
        _thread.Start();
    }

    /// <summary>Returns and resets the statistics collected since the last call.</summary>
    public LatencySecond TakeSecond()
    {
        Dictionary<ulong, (double Max, int Count)> dpc, isr;
        lock (_lock)
        {
            (dpc, _dpc) = (_dpc, new());
            (isr, _isr) = (_isr, new());
        }
        if (DateTime.UtcNow - _driversLoaded > TimeSpan.FromMinutes(1))
        {
            _drivers = DriverMap.Load(); // drivers can be loaded later (USB devices etc.)
            _driversLoaded = DateTime.UtcNow;
        }

        var perDriver = new Dictionary<string, (double Max, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (routine, stat) in dpc.Concat(isr))
        {
            var name = _drivers.Resolve(routine);
            perDriver[name] = perDriver.TryGetValue(name, out var s) ? (Math.Max(s.Max, stat.Max), s.Count + stat.Count) : stat;
        }
        return new LatencySecond(
            DateTimeOffset.Now,
            dpc.Count == 0 ? 0 : dpc.Values.Max(v => v.Max),
            isr.Count == 0 ? 0 : isr.Values.Max(v => v.Max),
            perDriver.OrderByDescending(d => d.Value.Max).Take(5)
                .Select(d => new DriverLatency(d.Key, Math.Round(d.Value.Max), d.Value.Count)).ToList());
    }

    private void OnEvent(IntPtr record)
    {
        // EVENT_RECORD (x64): TimeStamp @16, ProviderId @24, Opcode @45, UserDataLength @86, UserData @96
        var provider = Marshal.PtrToStructure<Guid>(record + 24);
        if (provider != PerfInfoGuid) return;
        var opcode = Marshal.ReadByte(record, 45);
        var isDpc = opcode is 66 or 68 or 69;
        var isIsr = opcode is 67 or 50;
        if (!isDpc && !isIsr) return;
        if (Marshal.ReadInt16(record, 86) < 16) return;

        var data = Marshal.ReadIntPtr(record, 96);
        var timestamp = Marshal.ReadInt64(record, 16);
        var initial = Marshal.ReadInt64(data, 0);
        var routine = unchecked((ulong)Marshal.ReadInt64(data, 8));
        var us = (timestamp - initial) / _ticksPerMicrosecond;
        if (us is < 0 or > 10_000_000) return;

        lock (_lock)
        {
            var map = isDpc ? _dpc : _isr;
            map[routine] = map.TryGetValue(routine, out var s) ? (Math.Max(s.Max, us), s.Count + 1) : (us, 1);
        }
    }

    private static IntPtr Properties()
    {
        var size = PropertiesSize + (KernelLoggerName.Length + 1) * 2;
        var p = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, size);                                        // Wnode.BufferSize
        Marshal.StructureToPtr(SystemTraceControlGuid, p + 24, false);          // Wnode.Guid
        Marshal.WriteInt32(p, 40, 1);                                           // Wnode.ClientContext = QPC
        Marshal.WriteInt32(p, 44, unchecked((int)WnodeFlagTracedGuid));        // Wnode.Flags
        Marshal.WriteInt32(p, 48, 64);                                          // BufferSize (KB)
        Marshal.WriteInt32(p, 64, unchecked((int)EventTraceRealTimeMode));      // LogFileMode
        Marshal.WriteInt32(p, 68, 1);                                           // FlushTimer (s)
        Marshal.WriteInt32(p, 72, unchecked((int)(EventTraceFlagDpc | EventTraceFlagInterrupt)));
        Marshal.WriteInt32(p, 116, PropertiesSize);                             // LoggerNameOffset
        return p;
    }

    /// <summary>Stops the kernel trace session, e.g. one left behind by a helper that was killed.</summary>
    public static void StopKernelLogger() => Stop();

    private static void Stop()
    {
        var props = Properties();
        try
        {
            ControlTraceW(0, KernelLoggerName, props, EventTraceControlStop);
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
    }

    public void Dispose()
    {
        if (_sessionHandle != 0) Stop();
        if (_traceHandle != 0 && _traceHandle != ulong.MaxValue) CloseTrace(_traceHandle);
        _thread?.Join(TimeSpan.FromSeconds(3));
        _sessionHandle = 0;
        _traceHandle = 0;
    }

    /// <summary>Maps kernel code addresses to driver file names (only possible with administrator rights).</summary>
    private sealed class DriverMap
    {
        private readonly (ulong Base, string Name)[] _modules;

        private DriverMap((ulong, string)[] modules) => _modules = modules;

        public static DriverMap Load()
        {
            EnumDeviceDrivers(null, 0, out var needed);
            var bases = new IntPtr[needed / IntPtr.Size];
            if (bases.Length == 0 || !EnumDeviceDrivers(bases, needed, out _)) return new DriverMap([]);
            var list = new List<(ulong, string)>();
            var name = new char[260];
            foreach (var b in bases)
            {
                if (b == IntPtr.Zero) continue;
                var len = GetDeviceDriverBaseNameW(b, name, name.Length);
                if (len > 0) list.Add((unchecked((ulong)b.ToInt64()), new string(name, 0, len)));
            }
            return new DriverMap(list.OrderBy(m => m.Item1).ToArray());
        }

        public string Resolve(ulong address)
        {
            var lo = 0;
            var hi = _modules.Length - 1;
            string? best = null;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (_modules[mid].Base <= address)
                {
                    best = _modules[mid].Name;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return best ?? "unknown driver";
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void EventRecordCallback(IntPtr eventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logFile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong[] handles, int count, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong handle);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumDeviceDrivers([Out] IntPtr[]? imageBases, int size, out int needed);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    private static extern int GetDeviceDriverBaseNameW(IntPtr imageBase, [Out] char[] baseName, int size);
}
