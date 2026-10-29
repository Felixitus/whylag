using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WhyLag.Core;

namespace WhyLag.Collectors;

/// <summary>
/// Total internet traffic of the whole household, read from the router via UPnP IGD (WANCommonInterfaceConfig).
/// Compared with this PC's own traffic it shows whether another device was using the line. Local network only;
/// the router must have UPnP enabled (many do, e.g. FRITZ!Box: "Allow access for applications").
/// </summary>
public sealed class RouterSource : IMetricSource
{
    private const string WanCommon = "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1";
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(3),
        MaxResponseContentBufferSize = 256 * 1024,
    };
    private readonly List<string> _warnings = new();
    private volatile Sample? _latest;
    private Uri? _control;

    private sealed record Sample(DateTime At, double DownMbps, double UpMbps);

    public string Name => WhyLag.Core.Localization.Loc.T("source.router");

    public IReadOnlyList<string> Warnings => _warnings;

    public void Start()
    {
        _control = DiscoverAsync(_cts.Token).GetAwaiter().GetResult();
        if (_control is null)
        {
            _warnings.Add(WhyLag.Core.Localization.Loc.T("warn.router"));
            return;
        }
        _ = Task.Run(() => PollAsync(_control, _cts.Token));
    }

    public void Collect(Tick tick)
    {
        if (_latest is not { } s || DateTime.UtcNow - s.At > TimeSpan.FromSeconds(5)) return;
        tick.Set(MetricKeys.RouterDownMbps, s.DownMbps);
        tick.Set(MetricKeys.RouterUpMbps, s.UpMbps);
    }

    private async Task PollAsync(Uri control, CancellationToken ct)
    {
        (uint Down, uint Up, DateTime At)? last = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                var down = await CallAsync(control, "GetTotalBytesReceived", "NewTotalBytesReceived", ct).ConfigureAwait(false);
                var up = await CallAsync(control, "GetTotalBytesSent", "NewTotalBytesSent", ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                if (last is { } l)
                {
                    var seconds = (now - l.At).TotalSeconds;
                    // The counters are 32-bit and wrap around; unchecked subtraction handles one wrap.
                    _latest = new Sample(now, unchecked(down - l.Down) * 8 / seconds / 1_000_000, unchecked(up - l.Up) * 8 / seconds / 1_000_000);
                }
                last = (down, up, now);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Xml.XmlException)
            {
                last = null;
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task<uint> CallAsync(Uri control, string action, string field, CancellationToken ct)
    {
        var body = $"""<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body><u:{action} xmlns:u="{WanCommon}"/></s:Body></s:Envelope>""";
        using var request = new HttpRequestMessage(HttpMethod.Post, control) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        request.Headers.Add("SOAPAction", $"\"{WanCommon}#{action}\"");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var xml = ParseXml(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var value = xml.Descendants().First(e => e.Name.LocalName == field).Value;
        return uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>SSDP search for an Internet Gateway Device, then the control URL of its traffic counters.</summary>
    private async Task<Uri?> DiscoverAsync(CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var search = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
                         "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
            var data = Encoding.ASCII.GetBytes(search);
            await udp.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900)).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            while (!timeout.IsCancellationRequested)
            {
                var reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(reply.Buffer);
                var location = text.Split("\r\n").FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
                if (location is null || !Uri.TryCreate(location, UriKind.Absolute, out var url)
                    || !IsDeviceUrl(url, reply.RemoteEndPoint.Address)) continue;
                if (await FindControlUrlAsync(url, ct).ConfigureAwait(false) is { } control) return control;
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or HttpRequestException or System.Xml.XmlException)
        {
        }
        return null;
    }

    private async Task<Uri?> FindControlUrlAsync(Uri description, CancellationToken ct)
    {
        var xml = ParseXml(await _http.GetStringAsync(description, ct).ConfigureAwait(false));
        var service = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "service"
            && e.Elements().Any(c => c.Name.LocalName == "serviceType" && c.Value == WanCommon));
        var path = service?.Elements().FirstOrDefault(c => c.Name.LocalName == "controlURL")?.Value;
        if (path is null) return null;
        var control = new Uri(description, path);
        // The control URL must stay on the device that answered; anything else is ignored.
        return control.Scheme == Uri.UriSchemeHttp && control.Host == description.Host ? control : null;
    }

    /// <summary>
    /// Accepts only plain http URLs that point at the device that sent the SSDP reply, and only when that device is
    /// on the local network. A device must not be able to make WhyLag request other hosts.
    /// </summary>
    internal static bool IsDeviceUrl(Uri url, IPAddress sender) =>
        url.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(url.Host, out var host) && host.Equals(sender) && IsLocal(sender);

    internal static bool IsLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return address.IsIPv6LinkLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Parses device XML without DTD processing.</summary>
    internal static XDocument ParseXml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
