using System.Net;
using System.Xml;

namespace WhyLag.Collectors.Tests;

public class RouterDiscoveryTests
{
    private static readonly IPAddress Router = IPAddress.Parse("192.168.1.1");

    [Theory]
    [InlineData("http://192.168.1.1:49000/igd.xml", true)]
    [InlineData("http://192.168.1.2:49000/igd.xml", false)] // another device than the one that answered
    [InlineData("https://192.168.1.1/igd.xml", false)]
    [InlineData("http://router.example/igd.xml", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    public void Only_the_device_that_answered_is_asked(string url, bool allowed) =>
        Assert.Equal(allowed, RouterSource.IsDeviceUrl(new Uri(url), Router));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    public void Public_addresses_are_refused(string address)
    {
        var sender = IPAddress.Parse(address);
        Assert.False(RouterSource.IsDeviceUrl(new Uri($"http://{address}/igd.xml"), sender));
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.178.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("127.0.0.1", false)]
    public void Local_networks(string address, bool local) => Assert.Equal(local, RouterSource.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public void Xml_with_a_dtd_is_refused() =>
        Assert.Throws<XmlException>(() => RouterSource.ParseXml("""<!DOCTYPE a [<!ENTITY b "c">]><a>&b;</a>"""));

    [Fact]
    public void Normal_xml_is_read() => Assert.Equal("a", RouterSource.ParseXml("<a/>").Root?.Name.LocalName);
}

public class ElevatedHelperTests
{
    [Theory]
    [InlineData("WhyLag.Latency.0123456789abcdef0123456789abcdef", true)]
    [InlineData("WhyLag.Latency.short", false)]
    [InlineData("WhyLag.Latency.0123456789abcdef0123456789abcdeg", false)]
    [InlineData(@"..\..\pipe\other", false)]
    [InlineData("", false)]
    public void Pipe_names(string name, bool valid) => Assert.Equal(valid, LatencyHelperHost.IsValidPipeName(name));

    [Theory]
    [InlineData]
    [InlineData("--latency-helper")]
    [InlineData("--latency-helper", "other-pipe", "S-1-5-21-1", "1")]
    [InlineData("--latency-helper", "WhyLag.Latency.0123456789abcdef0123456789abcdef", "not-a-sid", "1")]
    [InlineData("--latency-helper", "WhyLag.Latency.0123456789abcdef0123456789abcdef", "S-1-5-21-1", "not-a-pid")]
    public void Bad_arguments_stop_the_helper_before_anything_starts(params string[] args) =>
        Assert.Equal(2, LatencyHelperHost.Run(args));
}

public class PresentMonTests
{
    private static readonly Dictionary<string, int> Header = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Application"] = 0,
        ["ProcessID"] = 1,
        ["msBetweenPresents"] = 2,
        ["Dropped"] = 3,
    };

    [Fact]
    public void Frame_line_is_read() => Assert.Equal((12, "game.exe", 16.5), PresentMonSource.ParseLine("game.exe,12,16.5,0", Header));

    [Theory]
    [InlineData("game.exe,12,16.5,1")] // dropped
    [InlineData("game.exe,abc,16.5,0")]
    [InlineData("game.exe,12,0,0")]
    [InlineData("game.exe")]
    public void Unusable_lines_are_skipped(string line) => Assert.Null(PresentMonSource.ParseLine(line, Header));

    [Fact]
    public void Only_the_known_build_may_run()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "something else");
            Assert.False(PresentMonLocator.IsGenuine(path));
            Assert.False(PresentMonLocator.IsGenuine(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Account_name_cannot_leave_the_powershell_string() =>
        Assert.EndsWith("-Member 'PC\\O''Brien''; evil; '''", PresentMonLocator.GrantPermissionCommand("PC\\O'Brien'; evil; '"));
}

public class CounterParsingTests
{
    [Fact]
    public void Gpu_instance_is_read() =>
        Assert.Equal((1234, "0x00000000_0x0000D1B5"), PerformanceSource.ParseGpuInstance("pid_1234_luid_0x00000000_0x0000D1B5_phys_0_eng_0_engtype_3D"));

    [Theory]
    [InlineData("")]
    [InlineData("pid_x_luid_a_b")]
    [InlineData("other")]
    public void Odd_instances_are_skipped(string instance) => Assert.Null(PerformanceSource.ParseGpuInstance(instance));
}

public class VerifiedFileTests
{
    [Fact]
    public void Pipe_served_by_another_process_is_refused()
    {
        var name = $"WhyLag.Test.{Guid.NewGuid():N}";
        using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.Out);
        using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.In);
        client.Connect(5000);
        server.WaitForConnection();

        using var self = System.Diagnostics.Process.GetCurrentProcess();
        using var other = System.Diagnostics.Process.GetProcessById(4); // the System process: certainly not this pipe's server
        Assert.True(LatencyHelperSource.IsServedBy(client, self));
        Assert.False(LatencyHelperSource.IsServedBy(client, other));
        Assert.True(LatencyHelperSource.IsServedBy(client, null));
    }

    [Fact]
    public void A_file_of_another_content_is_not_opened()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "something else");
            Assert.Null(PresentMonLocator.OpenGenuine(path));
            File.WriteAllText(path, "can still be written: nothing was kept open");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_checked_file_cannot_be_replaced_while_it_is_held()
    {
        var copy = Path.Combine(Directory.CreateTempSubdirectory("whylag-test").FullName, "tool.exe");
        File.WriteAllText(copy, "checked content");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(copy)));
        using (PresentMonLocator.OpenVerified(copy, hash) ?? throw new InvalidOperationException("file not accepted"))
        {
            Assert.Throws<IOException>(() => File.WriteAllText(copy, "replaced"));
            Assert.Throws<IOException>(() => File.Delete(copy));
            using var reader = File.Open(copy, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); // what starting it needs
        }
        File.Delete(copy);
    }
}
