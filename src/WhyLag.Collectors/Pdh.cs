using System.Runtime.InteropServices;

namespace WhyLag.Collectors;

/// <summary>
/// Minimal wrapper around the Performance Data Helper API. Uses English counter paths
/// (PdhAddEnglishCounter), so it works on every Windows display language.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCStatusValidData = 0x0;
    private const uint PdhCStatusNewData = 0x1;

    private readonly IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new();

    public PdhQuery()
    {
        Check(PdhOpenQueryW(null, IntPtr.Zero, out _query), "PdhOpenQuery");
    }

    /// <summary>Adds a counter. Returns false when the counter does not exist on this system.</summary>
    public bool TryAdd(string path)
    {
        if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) != 0) return false;
        _counters[path] = counter;
        return true;
    }

    public bool Has(string path) => _counters.ContainsKey(path);

    public void Collect() => Check(PdhCollectQueryData(_query), "PdhCollectQueryData");

    public double? Value(string path)
    {
        if (!_counters.TryGetValue(path, out var counter)) return null;
        if (PdhGetFormattedCounterValue(counter, PdhFmtDouble | PdhFmtNoCap100, out _, out var value) != 0) return null;
        return value.CStatus is PdhCStatusValidData or PdhCStatusNewData ? value.DoubleValue : null;
    }

    /// <summary>All instances of a wildcard counter, e.g. "\Process(*)\% Processor Time".</summary>
    public List<(string Instance, double Value)> Values(string path)
    {
        var result = new List<(string, double)>();
        if (!_counters.TryGetValue(path, out var counter)) return result;

        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return result;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out var count, buffer) != 0) return result;
            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + i * itemSize);
                if (item.Value.CStatus is not (PdhCStatusValidData or PdhCStatusNewData)) continue;
                result.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.DoubleValue));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    public void Dispose() => PdhCloseQuery(_query);

    private static void Check(uint status, string call)
    {
        if (status != 0) throw new InvalidOperationException($"{call} failed with 0x{status:X8}");
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
