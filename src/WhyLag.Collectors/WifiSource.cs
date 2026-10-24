using System.Runtime.InteropServices;
using WhyLag.Core;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.Collectors;

/// <summary>
/// Wi-Fi signal quality, RSSI, link rate and access-point changes (roaming) via the native Wi-Fi API.
/// SSID and BSSID are only compared in memory and never stored, because they can reveal the user's location.
/// On PCs without Wi-Fi the source stays idle.
/// </summary>
public sealed class WifiSource : IMetricSource
{
    private const int OpcodeCurrentConnection = 7;
    private const int OpcodeRssi = 0x10000102;
    private const int StateConnected = 1;
    private const int InterfaceInfoSize = 532; // GUID + WCHAR[256] + state

    private readonly List<string> _warnings = new();
    private IntPtr _client;
    private Guid? _interface;
    private ulong? _lastBssid;
    private DateTime _lastLookup = DateTime.MinValue;

    public string Name => WhyLag.Core.Localization.Loc.T("source.wifi");

    public IReadOnlyList<string> Warnings => _warnings;

    public void Start()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out _client) != 0)
        {
            _client = IntPtr.Zero; // WLAN service not running: no Wi-Fi hardware, nothing to record
            return;
        }
        _interface = FindConnectedInterface();
    }

    public void Collect(Tick tick)
    {
        if (_client == IntPtr.Zero) return;
        if (_interface is null && DateTime.UtcNow - _lastLookup > TimeSpan.FromSeconds(30))
            _interface = FindConnectedInterface();
        if (_interface is not { } id) return;

        var guid = id;
        if (WlanQueryInterface(_client, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out _, out var data, out _) == 0)
        {
            try
            {
                if (Marshal.ReadInt32(data, 0) != StateConnected)
                {
                    _interface = null; // disconnected; look again later
                    _lastBssid = null;
                    return;
                }
                // WLAN_CONNECTION_ATTRIBUTES: state(4) mode(4) profile(512) then WLAN_ASSOCIATION_ATTRIBUTES at 520
                var bssid = 0UL;
                for (var i = 0; i < 6; i++) bssid = (bssid << 8) | Marshal.ReadByte(data, 560 + i);
                var signal = Marshal.ReadInt32(data, 576);
                var rx = (uint)Marshal.ReadInt32(data, 580);
                var tx = (uint)Marshal.ReadInt32(data, 584);

                if (signal is >= 0 and <= 100) tick.Set(MetricKeys.WifiSignal, signal);
                if (rx > 0) tick.Set(MetricKeys.WifiRxMbps, rx / 1000.0);
                if (tx > 0) tick.Set(MetricKeys.WifiTxMbps, tx / 1000.0);
                if (bssid != 0)
                {
                    tick.Set(MetricKeys.WifiRoam, _lastBssid is { } last && last != bssid ? 1 : 0);
                    _lastBssid = bssid;
                }
            }
            finally
            {
                WlanFreeMemory(data);
            }
        }
        else if (_warnings.Count == 0)
        {
            // Windows 11 24H2+ only reveals connection details to apps with location access.
            _warnings.Add(T("warn.wifi.location"));
        }

        if (WlanQueryInterface(_client, ref guid, OpcodeRssi, IntPtr.Zero, out _, out var rssiData, out _) == 0)
        {
            try
            {
                var rssi = Marshal.ReadInt32(rssiData);
                if (rssi is < 0 and > -120) tick.Set(MetricKeys.WifiRssi, rssi);
            }
            finally
            {
                WlanFreeMemory(rssiData);
            }
        }
    }

    private Guid? FindConnectedInterface()
    {
        _lastLookup = DateTime.UtcNow;
        if (WlanEnumInterfaces(_client, IntPtr.Zero, out var list) != 0) return null;
        try
        {
            var count = Marshal.ReadInt32(list, 0);
            for (var i = 0; i < count; i++)
            {
                var item = list + 8 + i * InterfaceInfoSize;
                var state = Marshal.ReadInt32(item, 16 + 512);
                if (state == StateConnected) return Marshal.PtrToStructure<Guid>(item);
            }
            return null;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    public void Dispose()
    {
        if (_client != IntPtr.Zero) WlanCloseHandle(_client, IntPtr.Zero);
        _client = IntPtr.Zero;
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
        out int dataSize, out IntPtr data, out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
