namespace WhyLag.Core.Tests;

public class RingBufferTests
{
    [Fact]
    public void Overwrites_oldest_items()
    {
        var buffer = new RingBuffer<int>(3);
        foreach (var i in Enumerable.Range(1, 5)) buffer.Add(i);

        Assert.Equal([3, 4, 5], buffer.Snapshot());
        Assert.Equal(5, buffer.Latest());
        Assert.Equal(3, buffer.Count);
    }
}

public class SpikeDetectorTests
{
    private static Tick Ping(int second, double ms) => Tick(second, MetricKeys.PingTargetMs, ms);

    private static Tick Tick(int second, string key, double value)
    {
        var t = new Tick { Timestamp = TestData.Start.AddSeconds(second) };
        t.Set(key, value);
        return t;
    }

    [Fact]
    public void Detects_latency_spike_after_warmup()
    {
        var detector = new SpikeDetector();
        for (var i = 0; i < 40; i++) Assert.Null(detector.Feed(Ping(i, 30 + i % 5)));

        var signal = detector.Feed(Ping(40, 220));

        Assert.NotNull(signal);
        Assert.Equal(MetricKeys.PingTargetMs, signal.Metric);
    }

    [Fact]
    public void Network_detection_can_be_turned_off()
    {
        var detector = new SpikeDetector { DetectNetwork = false };
        for (var i = 0; i < 40; i++) detector.Feed(Ping(i, 30 + i % 5));
        Assert.Null(detector.Feed(Ping(40, 220)));
        Assert.Equal(MetricKeys.TickGapMs, detector.Feed(Tick(41, MetricKeys.TickGapMs, 2500))?.Metric);
    }

    [Fact]
    public void Ignores_normal_jitter()
    {
        var detector = new SpikeDetector();
        var random = new Random(1);
        for (var i = 0; i < 300; i++) Assert.Null(detector.Feed(Ping(i, 30 + random.Next(0, 15))));
    }

    [Fact]
    public void Detects_system_stall()
    {
        var signal = new SpikeDetector().Feed(Tick(0, MetricKeys.TickGapMs, 2500));
        Assert.Equal(MetricKeys.TickGapMs, signal?.Metric);
    }

    [Fact]
    public void Detects_frame_hitch_in_smooth_game()
    {
        var detector = new SpikeDetector();
        Tick Frame(int s, double max)
        {
            var t = new Tick { Timestamp = TestData.Start.AddSeconds(s), FrameApp = "game" };
            t.Set(MetricKeys.FramesFps, 140);
            t.Set(MetricKeys.FramesMedianMs, 7);
            t.Set(MetricKeys.FramesMaxMs, max);
            return t;
        }
        for (var i = 0; i < 40; i++) Assert.Null(detector.Feed(Frame(i, 11)));

        Assert.Equal(MetricKeys.FramesMaxMs, detector.Feed(Frame(40, 180))?.Metric);
    }

    [Theory]
    [InlineData("brave")]
    [InlineData("webwallpaper64")]
    public void Hitches_in_non_games_do_not_trigger(string app)
    {
        var detector = new SpikeDetector();
        for (var i = 0; i < 41; i++)
        {
            var t = new Tick { Timestamp = TestData.Start.AddSeconds(i), FrameApp = app };
            t.Set(MetricKeys.FramesFps, 60);
            t.Set(MetricKeys.FramesMedianMs, 16);
            t.Set(MetricKeys.FramesMaxMs, i == 40 ? 900 : 20);
            Assert.Null(detector.Feed(t));
        }
    }

    [Fact]
    public void Two_lost_pings_in_a_row_trigger()
    {
        var detector = new SpikeDetector();
        Assert.Null(detector.Feed(Tick(0, MetricKeys.PingTargetLost, 1)));
        Assert.NotNull(detector.Feed(Tick(1, MetricKeys.PingTargetLost, 1)));
    }
}

public class ReportAndStoreTests
{
    [Fact]
    public void Report_lists_findings_with_evidence()
    {
        var incident = new TestData().Last(5, t => t.Set(MetricKeys.CpuTotal, 99)).Analyse();
        var md = ReportBuilder.Markdown(incident);

        Assert.Contains("F1: CPU fully loaded", md);
        Assert.Contains("| E1 |", md);
        Assert.Contains("not proof of the cause", md);
    }

    [Fact]
    public void Anonymize_removes_user_name()
    {
        var text = ReportBuilder.Anonymize($@"C:\Users\{Environment.UserName}\file");
        if (Environment.UserName.Length >= 3) Assert.DoesNotContain(Environment.UserName, text);
    }

    [Fact]
    public void Store_round_trips_and_prunes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "whylag-test-" + Guid.NewGuid());
        try
        {
            var store = new IncidentStore(dir);
            var incident = new TestData(30).Last(5, t => t.Set(MetricKeys.CpuTotal, 99)).Analyse();
            foreach (var id in new[] { "20261001-200000-hotkey", "20261001-200100-hotkey", "20261001-200200-manual" })
                store.Save(new Incident { Id = id, Findings = incident.Findings, Ticks = incident.Ticks });

            var loaded = store.Load("20261001-200100-hotkey");
            Assert.NotNull(loaded);
            Assert.Equal(incident.Findings[0].Title, loaded.Findings[0].Title);
            Assert.Equal(incident.Ticks[3].Get(MetricKeys.CpuTotal), loaded.Ticks[3].Get(MetricKeys.CpuTotal));

            store.Prune(2);
            Assert.Equal(["20261001-200200-manual", "20261001-200100-hotkey"], store.ListIds());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
