using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace WhyLag.Collectors;

/// <summary>
/// CPU load from GetSystemTimes. Works even when the classic performance counters are damaged
/// (a common Windows problem that "lodctr /R" repairs).
/// </summary>
internal sealed class CpuTimes
{
    private ulong _lastIdle, _lastTotal;

    /// <summary>CPU load in percent since the previous call, or null on the first call.</summary>
    public double? Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var idleNow = idle;
        var totalNow = kernel + user; // kernel time includes idle time
        double? result = null;
        if (_lastTotal != 0 && totalNow > _lastTotal)
        {
            var total = totalNow - _lastTotal;
            var idleDelta = idleNow - _lastIdle;
            result = Math.Clamp(100.0 * (total - idleDelta) / total, 0, 100);
        }
        _lastIdle = idleNow;
        _lastTotal = totalNow;
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
}

/// <summary>
/// Disk activity per physical drive via IOCTL_DISK_PERFORMANCE. Opening a drive with no access
/// rights is allowed for normal users and this IOCTL needs none.
/// </summary>
internal sealed class DiskPerformance : IDisposable
{
    private const uint IoctlDiskPerformance = 0x00070020;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    private readonly List<(SafeFileHandleWrapper Handle, int Number)> _disks = new();
    private readonly Dictionary<int, DiskPerf> _last = new();

    public int DiskCount => _disks.Count;

    public void Open()
    {
        for (var i = 0; i < 16; i++)
        {
            var handle = CreateFileW($@"\\.\PhysicalDrive{i}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) continue;
            var wrapper = new SafeFileHandleWrapper(handle);
            if (Query(wrapper) is null)
            {
                wrapper.Dispose();
                continue;
            }
            _disks.Add((wrapper, i));
        }
    }

    /// <summary>Busiest drive: % active time and average ms per I/O since the previous call.</summary>
    public (double ActivePct, double? LatencyMs)? Sample()
    {
        double? active = null, latency = null;
        foreach (var (handle, number) in _disks)
        {
            if (Query(handle) is not { } now) continue;
            if (_last.TryGetValue(number, out var prev) && now.QueryTime > prev.QueryTime)
            {
                var span = now.QueryTime - prev.QueryTime;
                var idle = Math.Clamp(now.IdleTime - prev.IdleTime, 0, span);
                var pct = 100.0 * (span - idle) / span;
                active = Math.Max(active ?? 0, pct);

                var ios = (now.ReadCount - prev.ReadCount) + (long)(now.WriteCount - prev.WriteCount);
                if (ios > 0)
                {
                    var ms = (now.ReadTime - prev.ReadTime + now.WriteTime - prev.WriteTime) / 10_000.0 / ios;
                    latency = Math.Max(latency ?? 0, ms);
                }
            }
            _last[number] = now;
        }
        return active is null ? null : (active.Value, latency);
    }

    private static DiskPerf? Query(SafeFileHandleWrapper handle)
    {
        var size = (uint)Marshal.SizeOf<DiskPerf>();
        return DeviceIoControl(handle.Handle, IoctlDiskPerformance, IntPtr.Zero, 0, out var perf, size, out _, IntPtr.Zero) ? perf : null;
    }

    public void Dispose()
    {
        foreach (var (handle, _) in _disks) handle.Dispose();
        _disks.Clear();
    }

    internal sealed class SafeFileHandleWrapper(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; private set; } = handle;

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) CloseHandle(Handle);
            Handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DiskPerf
    {
        public long BytesRead;
        public long BytesWritten;
        public long ReadTime;
        public long WriteTime;
        public long IdleTime;
        public uint ReadCount;
        public uint WriteCount;
        public uint QueueDepth;
        public uint SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string StorageManagerName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr device, uint code, IntPtr inBuffer, uint inSize, out DiskPerf outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// System-wide TCP retransmissions (IPv4 + IPv6). Resent segments mean packets were lost or delayed on the way,
/// e.g. by a weak Wi-Fi signal or an overloaded line. No admin rights needed.
/// </summary>
internal sealed class TcpRetransmissions
{
    private long _lastSent, _lastResent;
    private DateTime _lastAt;

    public (double PerSecond, double Percent)? Sample()
    {
        long sent = 0, resent = 0;
        var props = IPGlobalProperties.GetIPGlobalProperties();
        foreach (var stats in new Func<TcpStatistics>[] { props.GetTcpIPv4Statistics, props.GetTcpIPv6Statistics })
        {
            try
            {
                var s = stats();
                sent += s.SegmentsSent;
                resent += s.SegmentsResent;
            }
            catch (NetworkInformationException)
            {
            }
        }

        var now = DateTime.UtcNow;
        (double, double)? result = null;
        var seconds = (now - _lastAt).TotalSeconds;
        if (_lastAt != default && seconds > 0.2 && sent >= _lastSent && resent >= _lastResent)
        {
            var dSent = sent - _lastSent;
            var dResent = resent - _lastResent;
            // With only a handful of segments a single resend looks like a huge percentage; it means nothing.
            result = (dResent / seconds, dSent < 50 ? 0 : 100.0 * dResent / dSent);
        }
        (_lastSent, _lastResent, _lastAt) = (sent, resent, now);
        return result;
    }
}

/// <summary>Network throughput from interface byte counters (no performance counters needed).</summary>
internal sealed class NetworkThroughput
{
    private readonly Dictionary<string, (long Sent, long Received)> _last = new();
    private DateTime _lastAt;

    /// <summary>Upload/download of the busiest adapter in Mbit/s since the previous call.</summary>
    public (double UpMbps, double DownMbps)? Sample()
    {
        var now = DateTime.UtcNow;
        var seconds = (now - _lastAt).TotalSeconds;
        double up = 0, down = 0;
        var any = false;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceStatistics stats;
            try
            {
                stats = nic.GetIPStatistics();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            if (_last.TryGetValue(nic.Id, out var prev) && seconds > 0.2)
            {
                // Virtual adapters mirror the physical one, so take the busiest adapter, not the sum.
                up = Math.Max(up, Math.Max(0, stats.BytesSent - prev.Sent) * 8 / seconds / 1_000_000);
                down = Math.Max(down, Math.Max(0, stats.BytesReceived - prev.Received) * 8 / seconds / 1_000_000);
                any = true;
            }
            _last[nic.Id] = (stats.BytesSent, stats.BytesReceived);
        }
        _lastAt = now;
        return any ? (up, down) : null;
    }
}
