using System.Runtime.InteropServices;
using Microsoft.Win32;
using WhyLag.Core;

namespace WhyLag.Collectors;

public sealed record GpuInfo(string Name, double VramMb);

public static class HardwareInfo
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static MachineInfo Machine()
    {
        var gpus = Gpus();
        return new MachineInfo
        {
            TotalMemoryMb = TotalMemoryMb(),
            Gpus = gpus.Select(g => g.VramMb switch
            {
                >= 1024 => $"{g.Name} ({Math.Round(g.VramMb / 1024)} GB)",
                > 0 => $"{g.Name} ({Math.Round(g.VramMb)} MB)",
                _ => g.Name,
            }).ToList(),
        };
    }

    public static double TotalMemoryMb() => Memory()?.TotalMb ?? 0;

    public static (double TotalMb, double AvailableMb)? Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? (status.TotalPhys / (1024.0 * 1024.0), status.AvailPhys / (1024.0 * 1024.0))
            : null;
    }

    /// <summary>
    /// Graphics adapters from the driver registry. Unlike WMI's AdapterRAM (capped at 4 GB),
    /// HardwareInformation.qwMemorySize reports the real dedicated memory.
    /// </summary>
    public static List<GpuInfo> Gpus()
    {
        var result = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls is null) return result;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var key = cls.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string name) continue;
                double vram = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] { Length: 8 } b => BitConverter.ToInt64(b),
                    _ => key.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        byte[] { Length: 4 } b => BitConverter.ToUInt32(b),
                        _ => 0,
                    },
                };
                // Remote/basic display adapters are not real GPUs.
                if (vram == 0 && name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                if (result.All(g => g.Name != name)) result.Add(new GpuInfo(name, vram / (1024 * 1024)));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return result.OrderByDescending(g => g.VramMb).ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
