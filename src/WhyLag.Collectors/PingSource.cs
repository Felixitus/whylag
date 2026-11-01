using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WhyLag.Core;

namespace WhyLag.Collectors;

/// <summary>
/// Pings the default gateway (your router) and an internet target once per second in the background.
/// Comparing both tells local network problems apart from problems at the provider.
/// </summary>
public sealed class PingSource(string? target) : IMetricSource
{
    private const int TimeoutMs = 1000;
    private readonly CancellationTokenSource _cts = new();
    private volatile PingResult? _gateway;
    private volatile PingResult? _target;
    private IPAddress? _gatewayAddress;
    private IPAddress? _targetAddress;
    private DateTime _gatewayResolvedAt = DateTime.MinValue;
    private DateTime _targetResolvedAt = DateTime.MinValue;

    public string Name => WhyLag.Core.Localization.Loc.T("source.ping");

    private sealed record PingResult(DateTime At, bool Success, double Ms);

    public void Start()
    {
        if (IPAddress.TryParse(target, out var ip)) _targetAddress = ip;
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            if (ct.IsCancellationRequested) return;
            if (DateTime.UtcNow - _gatewayResolvedAt > TimeSpan.FromSeconds(30))
            {
                _gatewayAddress = FindGateway();
                _gatewayResolvedAt = DateTime.UtcNow;
            }
            await ResolveTargetAsync(ct).ConfigureAwait(false);
            var gw = _gatewayAddress is { } g ? SendAsync(g) : Task.FromResult<PingResult?>(null);
            var tg = _targetAddress is { } t ? SendAsync(t) : Task.FromResult<PingResult?>(null);
            await Task.WhenAll(gw, tg).ConfigureAwait(false);
            _gateway = gw.Result;
            _target = tg.Result;
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Host names are looked up here and not at start: at Windows sign-in the network is often not up yet, and DNS
    /// must not block the caller. The lookup repeats every few minutes so a moved server is followed.
    /// </summary>
    private async Task ResolveTargetAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target) || IPAddress.TryParse(target, out _)) return;
        var age = DateTime.UtcNow - _targetResolvedAt;
        if (age < (_targetAddress is null ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(5))) return;
        _targetResolvedAt = DateTime.UtcNow;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target, ct).ConfigureAwait(false);
            _targetAddress = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? _targetAddress;
        }
        catch (SocketException)
        {
            // Offline: keep the last address and try again.
        }
    }

    private static async Task<PingResult?> SendAsync(IPAddress address)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeoutMs).ConfigureAwait(false);
            return new PingResult(DateTime.UtcNow, reply.Status == IPStatus.Success, reply.RoundtripTime);
        }
        catch (PingException)
        {
            return new PingResult(DateTime.UtcNow, false, 0);
        }
    }

    public void Collect(Tick tick)
    {
        Apply(tick, _gateway, MetricKeys.PingGatewayMs, MetricKeys.PingGatewayLost);
        Apply(tick, _target, MetricKeys.PingTargetMs, MetricKeys.PingTargetLost);
    }

    private static void Apply(Tick tick, PingResult? result, string msKey, string lostKey)
    {
        // Results older than ~2 s belong to an earlier tick and are not repeated.
        if (result is null || DateTime.UtcNow - result.At > TimeSpan.FromSeconds(2)) return;
        tick.Set(lostKey, result.Success ? 0 : 1);
        if (result.Success) tick.Set(msKey, result.Ms);
    }

    /// <summary>IPv4 gateway of the interface Windows would use for internet traffic.</summary>
    internal static IPAddress? FindGateway()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => n.GetIPProperties())
            .ToList();

        // "Connecting" a UDP socket sends nothing but makes Windows pick the route, revealing the local address in use.
        IPAddress? local = null;
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(IPAddress.Parse("192.0.2.1"), 9); // TEST-NET-1, never routed to a real host
            local = (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
        }

        var preferred = interfaces.Where(p => local is not null && p.UnicastAddresses.Any(u => u.Address.Equals(local)));
        return preferred.Concat(interfaces)
            .SelectMany(p => p.GatewayAddresses.Select(g => g.Address))
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
    }

    public void Dispose() => _cts.Cancel();
}
