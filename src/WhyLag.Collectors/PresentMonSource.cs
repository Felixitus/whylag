using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using WhyLag.Core;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.Collectors;

/// <summary>
/// Frame times of the program in the foreground, captured with Intel PresentMon (MIT license) running as a child
/// process. PresentMon uses ETW and needs admin rights or membership in the "Performance Log Users" group.
/// Without PresentMon the source stays idle and explains why in its warnings.
/// </summary>
public sealed class PresentMonSource : IMetricSource
{
    private const string SessionName = "WhyLag";
    private const int MaxRestarts = 3;
    private const int MinFramesPerSecond = 10;
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { "<unknown>", "dwm.exe" };

    private readonly ConcurrentQueue<(int Pid, string App, double Ms)> _frames = new();
    private readonly List<string> _warnings = new();
    private readonly string? _exe;
    private Process? _process;
    private JobObject? _job;
    private int _restarts;
    private volatile string? _lastError;
    private bool _disposed;

    public PresentMonSource(string? exePath = null) => _exe = exePath ?? PresentMonLocator.Find();

    public string Name => WhyLag.Core.Localization.Loc.T("source.presentmon");

    public IReadOnlyList<string> Warnings => _warnings;

    public bool Available => _exe is not null;

    public void Start()
    {
        if (_exe is null)
        {
            _warnings.Add(T("warn.presentmon.missing"));
            return;
        }
        if (!PresentMonLocator.HasTracePermission())
            _warnings.Add(T("warn.presentmon.permission"));
        _job = new JobObject();
        Launch();
    }

    private void Launch()
    {
        var psi = new ProcessStartInfo(_exe!)
        {
            ArgumentList =
            {
                "--output_stdout", "--no_console_stats", "--v1_metrics", "--no_track_input",
                "--session_name", SessionName, "--stop_existing_session", "--exclude", "dwm.exe",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var header = (Dictionary<string, int>?)null;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } line) return;
            if (header is null)
            {
                if (line.StartsWith("Application,", StringComparison.Ordinal))
                    header = line.Split(',').Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);
                return;
            }
            if (ParseLine(line, header) is { } frame && !Ignored.Contains(frame.App)) _frames.Enqueue(frame);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line && line.StartsWith("error", StringComparison.OrdinalIgnoreCase)) _lastError = line;
        };
        process.Start();
        _job?.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    internal static (int Pid, string App, double Ms)? ParseLine(string line, IReadOnlyDictionary<string, int> header)
    {
        if (!header.TryGetValue("Application", out var appCol) || !header.TryGetValue("ProcessID", out var pidCol)
            || !header.TryGetValue("msBetweenPresents", out var msCol)) return null;
        var parts = line.Split(',');
        if (parts.Length <= Math.Max(msCol, Math.Max(appCol, pidCol))) return null;
        if (header.TryGetValue("Dropped", out var droppedCol) && parts.Length > droppedCol && parts[droppedCol] == "1") return null;
        if (!int.TryParse(parts[pidCol], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
        if (!double.TryParse(parts[msCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0) return null;
        return (pid, parts[appCol], ms);
    }

    public void Collect(Tick tick)
    {
        if (_exe is null || _disposed) return;
        if (_process is { HasExited: true })
        {
            if (_restarts++ >= MaxRestarts)
                throw new InvalidOperationException(_lastError ?? T("err.presentmon.stopped"));
            Launch();
            return;
        }

        var frames = new List<(int Pid, string App, double Ms)>();
        while (_frames.TryDequeue(out var f)) frames.Add(f);

        // Desktop programs present only when something changes; only continuously rendering apps (games, video)
        // reach 10 frames per second, and only for those frame times mean something. WhyLag's own window is ignored.
        var groups = frames.Where(f => f.Pid != Environment.ProcessId).GroupBy(f => f.Pid).Where(g => g.Count() >= MinFramesPerSecond).ToList();
        if (groups.Count == 0) return;

        // Prefer the program in the foreground; otherwise the one presenting the most frames.
        var foreground = tick.ForegroundProcess;
        var chosen = groups.FirstOrDefault(g => foreground is not null
                         && Path.GetFileNameWithoutExtension(g.First().App).Equals(foreground, StringComparison.OrdinalIgnoreCase))
                     ?? groups.MaxBy(g => g.Count())!;

        var times = chosen.Select(f => f.Ms).ToList();
        tick.FrameApp = Path.GetFileNameWithoutExtension(chosen.First().App);
        tick.Set(MetricKeys.FramesFps, times.Count);
        tick.Set(MetricKeys.FramesMedianMs, Stats.Median(times));
        tick.Set(MetricKeys.FramesMaxMs, times.Max());
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            if (_process is { HasExited: false }) _process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        _process?.Dispose();
        _job?.Dispose(); // also ends PresentMon if WhyLag crashes, via kill-on-close
        StopTraceSession();
    }

    /// <summary>
    /// Killing PresentMon leaves its kernel trace session running until reboot, so stop it explicitly.
    /// (After a crash, the next start does this through --stop_existing_session.)
    /// </summary>
    private void StopTraceSession()
    {
        if (_exe is null || _process is null) return;
        try
        {
            using var stop = Process.Start(new ProcessStartInfo(_exe)
            {
                ArgumentList = { "--terminate_existing_session", "--session_name", SessionName },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            stop?.WaitForExit(5000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>Kills child processes when WhyLag exits, even if it crashes.</summary>
    private sealed class JobObject : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;
        private readonly IntPtr _handle = CreateJobObjectW(IntPtr.Zero, null);

        public JobObject()
        {
            var info = new ExtendedLimitInformation();
            info.Basic.LimitFlags = KillOnJobClose;
            SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>());
        }

        public void Assign(Process p) => AssignProcessToJobObject(_handle, p.Handle);

        public void Dispose() => CloseHandle(_handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
