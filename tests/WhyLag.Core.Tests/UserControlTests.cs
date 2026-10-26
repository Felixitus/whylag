namespace WhyLag.Core.Tests;

public class UserControlTests
{
    private static Tick Frame(int second, string app, double max)
    {
        var t = new Tick { Timestamp = TestData.Start.AddSeconds(second), FrameApp = app };
        t.Set(MetricKeys.FramesFps, 140);
        t.Set(MetricKeys.FramesMedianMs, 7);
        t.Set(MetricKeys.FramesMaxMs, max);
        return t;
    }

    [Fact]
    public void Ignored_app_does_not_trigger_but_others_still_do()
    {
        var detector = new SpikeDetector { IgnoredApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SomeLauncher" } };
        for (var i = 0; i < 40; i++) detector.Feed(Frame(i, "game", 10));

        Assert.Null(detector.Feed(Frame(40, "somelauncher", 400)));
        Assert.NotNull(detector.Feed(Frame(41, "game", 400)));
    }

    [Fact]
    public void Windowed_or_background_apps_do_not_trigger()
    {
        var detector = new SpikeDetector();
        for (var i = 0; i < 40; i++) detector.Feed(Frame(i, "game", 10));

        var windowed = Frame(40, "game", 400);
        windowed.ForegroundProcess = "game";
        windowed.ForegroundFullscreen = false;
        Assert.Null(detector.Feed(windowed));

        var background = Frame(41, "game", 400);
        background.ForegroundProcess = "explorer";
        background.ForegroundFullscreen = true;
        Assert.Null(detector.Feed(background));

        var fullscreen = Frame(42, "game", 400);
        fullscreen.ForegroundProcess = "game";
        fullscreen.ForegroundFullscreen = true;
        Assert.NotNull(detector.Feed(fullscreen));
    }

    [Fact]
    public void Note_is_part_of_the_report()
    {
        var incident = new TestData().Analyse();
        incident.Note = "Was compiling a large project";

        Assert.Contains("Was compiling a large project", ReportBuilder.Markdown(incident));
    }
}
