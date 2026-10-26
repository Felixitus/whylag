using System.IO.MemoryMappedFiles;
using System.Management;
using System.Text;
using WhyLag.Core;

namespace WhyLag.Collectors;

/// <summary>
/// CPU and GPU temperatures. Reading sensors directly needs a kernel driver, which WhyLag deliberately avoids;
/// instead it reads what HWiNFO (shared memory) or LibreHardwareMonitor (WMI) already measure when one of them
/// is running. Without either, the source stays silent.
/// </summary>
public sealed class TemperatureSource : IMetricSource
{
    private const string HwInfoMap = @"Global\HWiNFO_SENS_SM2";
    private const uint TemperatureReading = 1;
    private DateTime _lastWmi = DateTime.MinValue;
    private (double? Cpu, double? Gpu) _wmiCache;

    public string Name => WhyLag.Core.Localization.Loc.T("source.temperature");

    public void Start()
    {
    }

    public void Collect(Tick tick)
    {
        var (cpu, gpu) = ReadHwInfo() ?? ReadLibreHardwareMonitor();
        if (cpu is { } c) tick.Set(MetricKeys.TempCpu, c);
        if (gpu is { } g) tick.Set(MetricKeys.TempGpu, g);
    }

    /// <summary>HWiNFO "Shared Memory Support" (must be enabled in HWiNFO's settings).</summary>
    private static (double? Cpu, double? Gpu)? ReadHwInfo()
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(HwInfoMap, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != 0x53695748) return null; // "HWiS"
            var readingOffset = view.ReadUInt32(32);
            var readingSize = view.ReadUInt32(36);
            var readingCount = view.ReadUInt32(40);

            double? cpu = null, gpu = null;
            for (var i = 0L; i < readingCount; i++)
            {
                var at = readingOffset + i * readingSize;
                if (view.ReadUInt32(at) != TemperatureReading) continue;
                var label = ReadString(view, at + 12, 128);
                var value = view.ReadDouble(at + 284);
                if (cpu is null && IsCpuLabel(label)) cpu = value;
                else if (gpu is null && label.StartsWith("GPU Temperature", StringComparison.OrdinalIgnoreCase)) gpu = value;
            }
            return cpu is null && gpu is null ? null : (cpu, gpu);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    internal static bool IsCpuLabel(string label) =>
        label.StartsWith("CPU (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase)
        || label.StartsWith("CPU Package", StringComparison.OrdinalIgnoreCase)
        || label.Equals("Core Max", StringComparison.OrdinalIgnoreCase);

    private static string ReadString(MemoryMappedViewAccessor view, long at, int length)
    {
        var bytes = new byte[length];
        view.ReadArray(at, bytes, 0, length);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.Latin1.GetString(bytes, 0, end < 0 ? length : end);
    }

    /// <summary>LibreHardwareMonitor publishes its sensors in WMI while it is running.</summary>
    private (double? Cpu, double? Gpu) ReadLibreHardwareMonitor()
    {
        if (DateTime.UtcNow - _lastWmi < TimeSpan.FromSeconds(2)) return _wmiCache;
        _lastWmi = DateTime.UtcNow;
        double? cpu = null, gpu = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\LibreHardwareMonitor",
                "SELECT Name, Value, Identifier FROM Sensor WHERE SensorType = 'Temperature'");
            foreach (var o in searcher.Get().Cast<ManagementObject>())
            {
                var name = o["Name"]?.ToString() ?? "";
                var id = o["Identifier"]?.ToString() ?? "";
                var value = Convert.ToDouble(o["Value"]);
                if (cpu is null && id.Contains("/cpu", StringComparison.OrdinalIgnoreCase)
                    && (name.Contains("Package", StringComparison.OrdinalIgnoreCase) || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)))
                    cpu = value;
                else if (gpu is null && id.Contains("/gpu", StringComparison.OrdinalIgnoreCase) && name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                    gpu = value;
            }
        }
        catch (ManagementException)
        {
            // Namespace missing: LibreHardwareMonitor is not running.
        }
        _wmiCache = (cpu, gpu);
        return _wmiCache;
    }

    public void Dispose()
    {
    }
}
