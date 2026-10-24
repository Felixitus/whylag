using System.Text.RegularExpressions;
using WhyLag.Core.Localization;

// Some tests switch the global language; run test classes one after another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WhyLag.Core.Tests;

public class LocalizationTests
{
    [Fact]
    public void Every_language_has_all_keys_with_the_same_placeholders()
    {
        var en = Loc.Strings("en");
        foreach (var code in Loc.Available.Where(c => c != "en"))
        {
            var other = Loc.Strings(code);
            Assert.Empty(en.Keys.Except(other.Keys));
            foreach (var (key, text) in en)
            {
                static string Placeholders(string s) => string.Join(",", Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Distinct().Order());
                Assert.True(Placeholders(text) == Placeholders(other[key]), $"{code}: placeholders differ for {key}");
            }
        }
    }

    [Fact]
    public void Rules_produce_german_text_and_numbers()
    {
        try
        {
            Loc.SetLanguage("de");
            var incident = new TestData().Last(5, t => t.Set(MetricKeys.CpuTotal, 97.5)).Analyse();
            var cpu = Assert.Single(incident.Findings);
            Assert.StartsWith("CPU voll ausgelastet", cpu.Title);
            Assert.Contains("97,5 %", cpu.Evidence[0].Description);
            Assert.Contains("Ergebnisse", ReportBuilder.Markdown(incident));
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    [Fact]
    public void Unknown_language_falls_back_to_english()
    {
        Loc.SetLanguage("xx");
        Assert.Equal("en", Loc.Language);
        Assert.Equal("CPU fully loaded", Loc.T("cpu.title"));
    }
}

public class TcpAndDriverRuleTests
{
    [Fact]
    public void Tcp_retransmissions_are_reported()
    {
        var incident = new TestData().Last(4, t =>
        {
            t.Set(MetricKeys.TcpRetransPct, 6.5);
            t.Set(MetricKeys.TcpRetransPerSec, 40);
        }).Analyse();

        var f = Assert.Single(incident.Findings, f => f.RuleId == "net.retransmissions");
        Assert.Contains("6.5", f.Title);
    }

    [Fact]
    public void Occasional_retransmissions_are_normal()
    {
        var incident = new TestData().Last(1, t =>
        {
            t.Set(MetricKeys.TcpRetransPct, 8);
            t.Set(MetricKeys.TcpRetransPerSec, 15);
        }).Analyse();

        Assert.DoesNotContain(incident.Findings, f => f.RuleId == "net.retransmissions");
    }

    [Fact]
    public void Helper_data_names_the_blocking_driver()
    {
        var incident = new TestData().Last(5, t =>
        {
            t.Set(MetricKeys.DpcMaxUs, 4800);
            t.Set(MetricKeys.IsrMaxUs, 120);
            t.Drivers.Add(new DriverLatency("nvlddmkm.sys", 4800, 120));
            t.Drivers.Add(new DriverLatency("ndis.sys", 600, 900));
        }).Analyse();

        var f = Assert.Single(incident.Findings, f => f.RuleId == "driver.latency");
        Assert.Equal("nvlddmkm.sys", f.Subject);
        Assert.Contains("NVIDIA", f.Evidence[0].Description);
        Assert.Equal(0.75, f.Confidence);
        Assert.Contains(f.Evidence, e => e.Description.Contains("ndis.sys"));
    }

    [Fact]
    public void Short_driver_calls_are_ignored()
    {
        var incident = new TestData().Last(5, t =>
        {
            t.Set(MetricKeys.DpcMaxUs, 300);
            t.Drivers.Add(new DriverLatency("ndis.sys", 300, 50));
        }).Analyse();

        Assert.DoesNotContain(incident.Findings, f => f.RuleId.StartsWith("driver.latency"));
    }

    [Fact]
    public void High_dpc_time_on_a_core_is_reported_without_helper()
    {
        var incident = new TestData().Last(3, t => t.Set(MetricKeys.DpcTimePct, 42)).Analyse();

        var f = Assert.Single(incident.Findings, f => f.RuleId == "driver.latency.core");
        Assert.Contains(f.Checks, c => c.Contains("driver latency analysis"));
    }
}

public class PatternTests
{
    private static Incident At(DateTime local, string ruleId, string? subject, double confidence = 0.7, string title = "CPU fully loaded, mainly by MsMpEng") => new()
    {
        Id = $"{local:yyyyMMdd-HHmmss}-hotkey",
        TriggeredAt = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)),
        Findings = [new Finding { Id = "F1", RuleId = ruleId, Subject = subject, Confidence = confidence, Title = title, Category = FindingCategory.Cpu }],
    };

    [Fact]
    public void Same_cause_every_tuesday_evening_is_a_pattern()
    {
        var tuesday = new DateTime(2026, 9, 1, 21, 10, 0); // a Tuesday
        var incidents = Enumerable.Range(0, 4).Select(w => At(tuesday.AddDays(7 * w).AddMinutes(w * 7), "cpu.saturation", "MsMpEng")).ToList();
        incidents.Add(At(new DateTime(2026, 9, 3, 9, 0, 0), "disk.saturation", "OneDrive"));

        var pattern = Assert.Single(PatternAnalyzer.Find(incidents));
        Assert.Equal(4, pattern.Count);
        Assert.Equal(4, pattern.ByWeekday[1]);
        Assert.Contains("21:00", pattern.When);
        Assert.Contains("Tuesday", pattern.When);
    }

    [Fact]
    public void Subject_is_compared_case_insensitively_and_low_confidence_is_ignored()
    {
        var day = new DateTime(2026, 9, 1, 8, 0, 0);
        var incidents = new List<Incident>
        {
            At(day, "background.task", "OneDrive"),
            At(day.AddDays(1).AddHours(5), "background.task", "onedrive"),
            At(day.AddDays(2).AddHours(10), "background.task", "ONEDRIVE"),
            At(day.AddDays(3), "net.local", null, confidence: 0.35),
            At(day.AddDays(4), "net.local", null, confidence: 0.35),
            At(day.AddDays(5), "net.local", null, confidence: 0.35),
        };

        var pattern = Assert.Single(PatternAnalyzer.Find(incidents));
        Assert.Equal(3, pattern.Count);
        Assert.Equal("", pattern.When); // no dominant time or weekday
    }

    [Fact]
    public void Bursts_within_one_hour_and_demos_do_not_count()
    {
        var start = new DateTime(2026, 9, 1, 20, 0, 0);
        var incidents = Enumerable.Range(0, 5).Select(i => At(start.AddMinutes(i * 3), "system.stall", null)).ToList();
        incidents.Add(new Incident { Id = "x-demo", TriggeredAt = start, Findings = incidents[0].Findings });

        Assert.Empty(PatternAnalyzer.Find(incidents));
    }
}
