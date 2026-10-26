using WhyLag.Core.Rules;

namespace WhyLag.Core.Tests;

public class SessionTests
{
    private static Tick Frame(DateTimeOffset at, string app = "game", double fps = 120, double maxMs = 9, bool fullscreen = true)
    {
        var t = new Tick { Timestamp = at, ForegroundProcess = app, FrameApp = app, ForegroundFullscreen = fullscreen };
        t.Set(MetricKeys.FramesFps, fps);
        t.Set(MetricKeys.FramesMedianMs, 8.3);
        t.Set(MetricKeys.FramesMaxMs, maxMs);
        t.Set(MetricKeys.CpuTotal, 40);
        t.Set(MetricKeys.Gpu3D, 90);
        return t;
    }

    [Fact]
    public void A_long_fullscreen_game_becomes_a_session_with_hitches()
    {
        var tracker = new SessionTracker();
        var start = TestData.Start;
        for (var i = 0; i < 600; i++) Assert.Null(tracker.Feed(Frame(start.AddSeconds(i), maxMs: i % 100 == 50 ? 80 : 9)));
        Assert.Equal("game", tracker.CurrentApp);

        GameSession? session = null;
        for (var i = 600; i < 700 && session is null; i++) session = tracker.Feed(new Tick { Timestamp = start.AddSeconds(i), ForegroundProcess = "explorer" });

        Assert.NotNull(session);
        Assert.Equal("game", session.App);
        Assert.Equal(120, session.AverageFps);
        Assert.Equal(6, session.Hitches);
        Assert.True(session.Duration >= TimeSpan.FromMinutes(9));
    }

    [Fact]
    public void Short_or_windowed_play_is_not_a_session()
    {
        var tracker = new SessionTracker();
        var start = TestData.Start;
        for (var i = 0; i < 120; i++) tracker.Feed(Frame(start.AddSeconds(i)));
        for (var i = 0; i < 600; i++) tracker.Feed(Frame(start.AddSeconds(200 + i), fullscreen: false));
        Assert.Null(tracker.Feed(new Tick { Timestamp = start.AddSeconds(1000) }));
        Assert.Null(tracker.CurrentApp);
    }
}

public class ChangeTests
{
    private static Incident At(DateTimeOffset t) => new() { Id = t.ToString("yyyyMMddHHmmss"), TriggeredAt = t };

    [Fact]
    public void Update_followed_by_more_lag_is_reported()
    {
        var update = TestData.Start;
        var incidents = new List<Incident> { At(update.AddDays(-6)), At(update.AddDays(-3)) };
        for (var i = 0; i < 8; i++) incidents.Add(At(update.AddHours(6 + i * 12)));
        var change = new SystemChange(update, ChangeKind.Driver, "NVIDIA 566.14");

        var impact = Assert.Single(ChangeAnalyzer.Compare([change], incidents, update.AddDays(7)));
        Assert.Equal(2, impact.Before);
        Assert.Equal(8, impact.After);
    }

    [Fact]
    public void Steady_lag_rate_is_not_blamed_on_an_update()
    {
        var update = TestData.Start;
        var incidents = Enumerable.Range(-12, 24).Select(d => At(update.AddHours(d * 14))).ToList();
        Assert.Empty(ChangeAnalyzer.Compare([new SystemChange(update, ChangeKind.WindowsUpdate, "KB5050000")], incidents, update.AddDays(7)));
    }
}

public class NewRuleTests
{
    [Fact]
    public void Steady_memory_growth_is_found()
    {
        var samples = Enumerable.Range(0, 180).Select(m => new MemorySample(TestData.Start.AddMinutes(m), 30 - m / 10.0,
            new Dictionary<string, double> { ["leaky"] = 1000 + m * 20, ["stable"] = 800 })).ToList();
        var ctx = new IncidentContext(new TestData().Ticks, [], TestData.Start.AddMinutes(180), TriggerKind.Hotkey) { LongTerm = samples };

        var finding = new MemoryGrowthRule().Evaluate(ctx);
        Assert.NotNull(finding);
        Assert.Equal("leaky", finding.Subject);
    }

    [Fact]
    public void Hot_cpu_is_reported_only_above_the_limit()
    {
        Assert.DoesNotContain(new TestData().Last(5, t => t.Set(MetricKeys.TempCpu, 80)).Analyse().Findings, f => f.RuleId == "temperature.high");
        var incident = new TestData().Last(5, t => t.Set(MetricKeys.TempCpu, 99)).Analyse();
        Assert.Contains(incident.Findings, f => f.RuleId == "temperature.high" && f.Category == FindingCategory.Cpu);
    }

    [Fact]
    public void Traffic_from_other_devices_explains_a_ping_spike()
    {
        var incident = new TestData().Last(3, t =>
        {
            t.Set(MetricKeys.PingTargetMs, 250);
            t.Set(MetricKeys.RouterDownMbps, 300);
            t.Set(MetricKeys.RouterUpMbps, 2);
        }, endOffset: 4).Analyse();

        Assert.Contains(incident.Findings, f => f.RuleId == "net.household");
    }

    [Fact]
    public void Own_traffic_is_not_blamed_on_other_devices()
    {
        var incident = new TestData().Last(3, t =>
        {
            t.Set(MetricKeys.PingTargetMs, 250);
            t.Set(MetricKeys.NetDownMbps, 290);
            t.Set(MetricKeys.RouterDownMbps, 300);
        }, endOffset: 4).Analyse();

        Assert.DoesNotContain(incident.Findings, f => f.RuleId == "net.household");
    }
}

public class JsonRuleTests
{
    private const string Rule = """
        [ // comments are allowed
          {
            "id": "custom.vram",
            "category": "Gpu",
            "when": [ { "metric": "gpu.vram.used.mb", "above": 7500 } ],
            "minSeconds": 2,
            "title": { "en": "VRAM nearly full ({0} MB)", "de": "VRAM fast voll ({0} MB)" },
            "summary": { "en": "Textures no longer fit." },
            "evidence": [ { "text": { "en": "Peak {0} for {1} s" }, "args": [ "peak:gpu.vram.used.mb", "seconds" ] },
                          { "text": { "en": "never shown" }, "args": [ "baseline:unknown.metric" ] } ],
            "checks": [ { "en": "Lower texture quality." } ],
          },
        ]
        """;

    [Fact]
    public void A_json_rule_matches_and_formats_its_texts()
    {
        var rule = new JsonRule(Assert.Single(JsonRules.Parse(Rule)));
        var data = new TestData().Last(3, t => t.Set(MetricKeys.GpuVramUsedMb, 7900));
        var finding = rule.Evaluate(new IncidentContext(data.Ticks, [], data.End, TriggerKind.Hotkey));

        Assert.NotNull(finding);
        Assert.Equal(FindingCategory.Gpu, finding.Category);
        Assert.Equal("VRAM nearly full (7900 MB)", finding.Title);
        var evidence = Assert.Single(finding.Evidence); // the line with a missing baseline is skipped
        Assert.Equal("Peak 7900 for 3 s", evidence.Description);
        Assert.Equal("Lower texture quality.", Assert.Single(finding.Checks));
    }

    [Fact]
    public void A_json_rule_needs_enough_seconds()
    {
        var rule = new JsonRule(Assert.Single(JsonRules.Parse(Rule)));
        var data = new TestData().Last(1, t => t.Set(MetricKeys.GpuVramUsedMb, 7900));
        Assert.Null(rule.Evaluate(new IncidentContext(data.Ticks, [], data.End, TriggerKind.Hotkey)));
    }

    [Fact]
    public void User_rules_are_loaded_and_broken_files_reported()
    {
        var dir = Directory.CreateTempSubdirectory("whylag-rules").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "good.json"), Rule);
            File.WriteAllText(Path.Combine(dir, "broken.json"), "[ { \"id\": ");
            JsonRules.Errors.Clear();
            Assert.Equal("custom.vram", Assert.Single(JsonRules.LoadUser(dir)).Id);
            Assert.Contains(JsonRules.Errors, e => e.StartsWith("broken.json", StringComparison.Ordinal));
        }
        finally
        {
            JsonRules.Errors.Clear();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Built_in_retransmission_rule_comes_from_json()
    {
        Assert.Contains(JsonRules.BuiltIn, r => r.Id == "net.retransmissions");
        var incident = new TestData().Last(4, t =>
        {
            t.Set(MetricKeys.TcpRetransPct, 6);
            t.Set(MetricKeys.TcpRetransPerSec, 40);
            t.Set(MetricKeys.WifiSignal, 30);
        }).Analyse();
        var finding = Assert.Single(incident.Findings, f => f.RuleId == "net.retransmissions");
        Assert.Equal(0.7, finding.Confidence);
        Assert.Equal(2, finding.Evidence.Count); // no baseline recorded, so that line is left out
    }
}

public class HtmlReportTests
{
    [Fact]
    public void Html_report_is_self_contained_and_escaped()
    {
        var incident = new TestData().Last(5, t => t.Set(MetricKeys.CpuTotal, 98)).Analyse();
        incident.Note = "<script>alert(1)</script>";
        var html = HtmlReport.Build(incident);

        Assert.StartsWith("<!doctype html>", html.TrimStart());
        Assert.Contains("<svg", html);
        Assert.Contains("polyline", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("http://", html.Replace("http://www.w3.org/2000/svg", ""));
    }
}

/// <summary>Real recordings (anonymized) that must keep producing the same result. Add new ones with whylag-cli export-fixture.</summary>
public class RealCaseTests
{
    public static TheoryData<string> Files() => [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.json").Select(f => Path.GetFileName(f))];

    [Theory]
    [MemberData(nameof(Files))]
    public void Real_case_produces_the_expected_result(string file)
    {
        var (expectation, incident) = Fixtures.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        Assert.True(incident.Ticks.Count > 100, "fixture has too little data");
        Assert.Empty(Fixtures.Check(expectation, incident));
    }
}
