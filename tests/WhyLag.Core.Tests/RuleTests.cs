using WhyLag.Core.Rules;

namespace WhyLag.Core.Tests;

public class RuleTests
{
    [Fact]
    public void Calm_recording_has_no_findings()
    {
        var incident = new TestData().Analyse();
        Assert.Empty(incident.Findings);
    }

    [Fact]
    public void Defender_scan_with_full_cpu_and_disk_is_explained()
    {
        var incident = new TestData().Last(6, t =>
        {
            t.Set(MetricKeys.CpuTotal, 97);
            t.Set(MetricKeys.DiskActive, 100);
            t.Set(MetricKeys.DiskLatencyMs, 120);
            t.TopProcesses.Add(new ProcessStat(200, "MsMpEng", 45, 80, 0, 300));
        }, endOffset: 5).Analyse();

        var rules = incident.Findings.Select(f => f.RuleId).ToList();
        Assert.Contains("cpu.saturation", rules);
        Assert.Contains("disk.saturation", rules);
        Assert.Contains("background.task", rules);
        Assert.Contains(incident.Findings, f => f.Title.Contains("MsMpEng"));
    }

    [Fact]
    public void Ids_are_sequential_and_unique()
    {
        var incident = new TestData().Last(5, t =>
        {
            t.Set(MetricKeys.CpuTotal, 99);
            t.Set(MetricKeys.DiskActive, 99);
        }).Analyse();

        Assert.Equal(incident.Findings.Select((_, i) => $"F{i + 1}"), incident.Findings.Select(f => f.Id));
        var evidenceIds = incident.AllEvidence.Select(e => e.Id).ToList();
        Assert.Equal(evidenceIds.Count, evidenceIds.Distinct().Count());
        Assert.Equal("E1", evidenceIds[0]);
        Assert.True(incident.Findings.SequenceEqual(incident.Findings.OrderByDescending(f => f.Confidence)));
    }

    [Fact]
    public void Slow_internet_with_healthy_router_points_beyond_the_router()
    {
        var incident = new TestData().Last(3, t => t.Set(MetricKeys.PingTargetMs, 250), endOffset: 4).Analyse();

        Assert.Contains(incident.Findings, f => f.RuleId == "net.remote");
        Assert.DoesNotContain(incident.Findings, f => f.RuleId == "net.local");
    }

    [Fact]
    public void Slow_router_points_to_the_local_network()
    {
        var incident = new TestData().Last(3, t =>
        {
            t.Set(MetricKeys.PingGatewayMs, 180);
            t.Set(MetricKeys.PingTargetMs, 220);
        }).Analyse();

        var local = Assert.Single(incident.Findings, f => f.RuleId == "net.local");
        Assert.True(local.Confidence >= 0.6);
        Assert.DoesNotContain(incident.Findings, f => f.RuleId == "net.remote");
    }

    [Fact]
    public void Wifi_event_raises_local_network_confidence()
    {
        var data = new TestData().Last(3, t =>
        {
            t.Set(MetricKeys.PingGatewayMs, 300);
            t.Set(MetricKeys.PingTargetMs, 330);
        });
        data.Events.Add(new SystemEvent(data.End.AddSeconds(-2), "Microsoft-Windows-WLAN-AutoConfig/Operational",
            "Microsoft-Windows-WLAN-AutoConfig", 8003, EventLevel.Information, "WLAN disconnected"));

        var local = Assert.Single(data.Analyse().Findings, f => f.RuleId == "net.local");
        Assert.Equal("Wi-Fi connection problem", local.Title);
        Assert.True(local.Confidence >= 0.75);
    }

    [Fact]
    public void Own_upload_during_latency_spike_is_bufferbloat_not_provider()
    {
        var incident = new TestData().Last(4, t =>
        {
            t.Set(MetricKeys.PingTargetMs, 400);
            t.Set(MetricKeys.NetUpMbps, 38);
            t.TopProcesses.Add(new ProcessStat(300, "OneDrive", 3, 5, 0, 200));
        }, endOffset: 3).Analyse();

        Assert.Contains(incident.Findings, f => f.RuleId == "net.bufferbloat" && f.Title.Contains("upload"));
        Assert.DoesNotContain(incident.Findings, f => f.RuleId == "net.remote");
    }

    [Fact]
    public void Packet_loss_alone_is_reported()
    {
        var incident = new TestData().Last(2, t =>
        {
            t.Metrics.Remove(MetricKeys.PingTargetMs);
            t.Set(MetricKeys.PingTargetLost, 1);
        }, endOffset: 2).Analyse();

        var remote = Assert.Single(incident.Findings, f => f.RuleId == "net.remote");
        Assert.Contains(remote.Evidence, e => e.Description.Contains("got no answer"));
    }

    [Fact]
    public void Recorder_pause_is_a_system_stall()
    {
        var incident = new TestData().Last(1, t => t.Set(MetricKeys.TickGapMs, 3500), endOffset: 5).Analyse();

        var stall = Assert.Single(incident.Findings, f => f.RuleId == "system.stall");
        Assert.Equal(0.7, stall.Confidence);
    }

    [Fact]
    public void Graphics_driver_reset_is_recognised()
    {
        var data = new TestData();
        data.Events.Add(new SystemEvent(data.End.AddSeconds(-8), "System", "Display", 4101, EventLevel.Warning,
            "Display driver amdkmdag stopped responding and has successfully recovered."));

        var finding = Assert.Single(data.Analyse().Findings);
        Assert.Equal("events.correlated.tdr", finding.RuleId);
        Assert.Equal(FindingCategory.Gpu, finding.Category);
    }

    [Fact]
    public void Old_events_outside_the_window_are_ignored()
    {
        var data = new TestData();
        data.Events.Add(new SystemEvent(TestData.Start, "System", "Display", 4101, EventLevel.Warning, "old"));
        Assert.Empty(data.Analyse().Findings);
    }

    [Fact]
    public void Background_gpu_user_is_named()
    {
        var incident = new TestData().Last(4, t => t.TopProcesses.Add(new ProcessStat(400, "ollama_llama_server", 5, 0, 40, 9000))).Analyse();

        Assert.Contains(incident.Findings, f => f.RuleId == "gpu.background" && f.Title.Contains("ollama_llama_server"));
    }

    [Fact]
    public void Low_memory_with_paging_is_reported()
    {
        var incident = new TestData().Last(4, t =>
        {
            t.Set(MetricKeys.MemAvailablePct, 4);
            t.Set(MetricKeys.MemAvailableMb, 1300);
            t.Set(MetricKeys.MemPagesIn, 4000);
        }).Analyse();

        var mem = Assert.Single(incident.Findings, f => f.RuleId == "memory.pressure");
        Assert.Equal(0.75, mem.Confidence);
    }

    [Fact]
    public void Throttling_needs_a_drop_under_load()
    {
        var incident = new TestData().Last(5, t =>
        {
            t.Set(MetricKeys.CpuPerformance, 55);
            t.Set(MetricKeys.CpuTotal, 70);
        }).Analyse();

        Assert.Contains(incident.Findings, f => f.RuleId == "cpu.throttling");
    }

    private static TestData WithFrames(double medianMs = 7, double fps = 140) => new TestData().Last(150, t =>
    {
        t.FrameApp = "game";
        t.Set(MetricKeys.FramesFps, fps);
        t.Set(MetricKeys.FramesMedianMs, medianMs);
        t.Set(MetricKeys.FramesMaxMs, medianMs * 1.4);
    });

    private static TestData WithWifi() => new TestData().Last(150, t =>
    {
        t.Set(MetricKeys.WifiSignal, 85);
        t.Set(MetricKeys.WifiRssi, -55);
        t.Set(MetricKeys.WifiRxMbps, 866);
        t.Set(MetricKeys.WifiRoam, 0);
    });

    [Fact]
    public void Smooth_frames_and_good_wifi_produce_no_findings()
    {
        var data = WithFrames();
        foreach (var t in data.Ticks)
        {
            t.Set(MetricKeys.WifiSignal, 85);
            t.Set(MetricKeys.WifiRssi, -55);
        }
        Assert.Empty(data.Analyse().Findings);
    }

    [Fact]
    public void Frame_hitch_is_confirmed_with_app_name()
    {
        var incident = WithFrames().Last(2, t => t.Set(MetricKeys.FramesMaxMs, 160), endOffset: 4).Analyse();

        var stutter = Assert.Single(incident.Findings, f => f.RuleId == "frames.stutter");
        Assert.Equal("game stuttered: a frame took 160 ms", stutter.Title);
        Assert.Equal(0.7, stutter.Confidence);
    }

    [Fact]
    public void Frame_rate_drop_is_reported()
    {
        var incident = WithFrames().Last(4, t => t.Set(MetricKeys.FramesFps, 50), endOffset: 2).Analyse();
        Assert.Contains(incident.Findings, f => f.RuleId == "frames.stutter.fpsdrop");
    }

    [Fact]
    public void Low_fps_games_are_not_judged()
    {
        var incident = WithFrames(medianMs: 50, fps: 20).Last(2, t => t.Set(MetricKeys.FramesMaxMs, 200)).Analyse();
        Assert.DoesNotContain(incident.Findings, f => f.Category == FindingCategory.Frames);
    }

    [Fact]
    public void Weak_wifi_with_slow_router_is_likely_the_cause()
    {
        var incident = WithWifi().Last(4, t =>
        {
            t.Set(MetricKeys.WifiSignal, 25);
            t.Set(MetricKeys.WifiRssi, -82);
            t.Set(MetricKeys.PingGatewayMs, 140);
        }).Analyse();

        var wifi = Assert.Single(incident.Findings, f => f.RuleId == "wifi.weak");
        Assert.Equal("Weak Wi-Fi signal", wifi.Title);
        Assert.Equal(0.75, wifi.Confidence);
    }

    [Fact]
    public void Wifi_roaming_is_reported()
    {
        var incident = WithWifi().Last(1, t => t.Set(MetricKeys.WifiRoam, 1), endOffset: 3).Analyse();
        Assert.Contains(incident.Findings, f => f.RuleId == "wifi.weak" && f.Title.Contains("access point"));
    }

    [Fact]
    public void Broken_rule_does_not_break_the_engine()
    {
        var data = new TestData().Last(5, t => t.Set(MetricKeys.CpuTotal, 99));
        var ctx = new IncidentContext(data.Ticks, data.Events, data.End, TriggerKind.Hotkey);

        var findings = RuleEngine.Evaluate(ctx, [new ThrowingRule(), new CpuSaturationRule()]);

        Assert.Single(findings);
    }

    private sealed class ThrowingRule : IRule
    {
        public string Id => "throws";
        public Finding Evaluate(IncidentContext ctx) => throw new InvalidOperationException();
    }
}
