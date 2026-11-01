namespace WhyLag.Core.Tests;

public class RecorderTests
{
    private sealed class Source(int failFirst = 0) : IMetricSource
    {
        public int Calls { get; private set; }
        public string Name => "test source";
        public void Start() { }
        public void Dispose() { }

        public void Collect(Tick tick)
        {
            if (++Calls <= failFirst) throw new InvalidOperationException("broken");
            tick.Set(MetricKeys.CpuTotal, 10);
        }
    }

    private static readonly DateTimeOffset T0 = TestData.Start;

    private static Recorder Create(IMetricSource source, IncidentStore? store = null) =>
        new(new WhyLagSettings { AutoDetect = true }, [source], [], new MachineInfo(), store);

    private static string TempDir() => Directory.CreateTempSubdirectory("whylag-test").FullName;

    [Fact]
    public void Sleep_is_not_a_freeze()
    {
        using var recorder = Create(new Source());
        recorder.Record(3_600_000, T0);
        Assert.Equal(0, recorder.LatestTick!.Get(MetricKeys.TickGapMs));
    }

    [Fact]
    public void Short_gap_is_recorded()
    {
        using var recorder = Create(new Source());
        recorder.Record(2500, T0);
        Assert.Equal(2500, recorder.LatestTick!.Get(MetricKeys.TickGapMs));
    }

    [Fact]
    public void Failing_source_is_tried_again_and_recovers()
    {
        var source = new Source(failFirst: 6);
        using var recorder = Create(source);
        for (var i = 0; i < 5; i++) recorder.Record(0, T0.AddSeconds(i));
        Assert.Equal(5, source.Calls);

        // Stopped for now, but not for good.
        for (var i = 5; i < 200; i++) recorder.Record(0, T0.AddSeconds(i));
        Assert.True(source.Calls > 6);
        Assert.Equal(10, recorder.LatestTick!.Get(MetricKeys.CpuTotal));
    }

    [Fact]
    public async Task Recording_survives_an_exception_in_a_listener()
    {
        var errors = 0;
        using var recorder = Create(new Source());
        recorder.TickRecorded += _ => throw new InvalidOperationException("listener");
        recorder.Failed += _ => Interlocked.Increment(ref errors);
        recorder.Start();
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.True(recorder.BufferedSeconds >= 2, $"only {recorder.BufferedSeconds} seconds recorded");
        Assert.True(errors >= 2);
    }

    [Fact]
    public async Task Automatic_capture_that_cannot_write_is_reported_and_recording_goes_on()
    {
        var blocked = Path.Combine(TempDir(), "file");
        File.WriteAllText(blocked, "not a folder");
        var failed = new TaskCompletionSource<Exception>();
        using var recorder = Create(new Source(), new IncidentStore(blocked));
        recorder.Failed += ex => failed.TrySetResult(ex);

        recorder.Record(2500, T0); // a stall starts an automatic incident after a short delay
        for (var i = 1; i <= 12; i++) recorder.Record(0, T0.AddSeconds(i));

        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        recorder.Record(0, T0.AddSeconds(13));
        Assert.Equal(T0.AddSeconds(13), recorder.LatestTick!.Timestamp);
    }

    [Fact]
    public void Paused_recorder_reports_it()
    {
        using var recorder = Create(new Source());
        recorder.Paused = true;
        Assert.True(recorder.Paused);
    }
}

public class StoreTests
{
    private static Incident Saved(IncidentStore store, string id, DateTimeOffset at)
    {
        var data = new TestData(30);
        var incident = new Incident { Id = id, TriggeredAt = at, Ticks = data.Ticks, Note = "note", Findings = [new Finding { RuleId = "r", Title = "t" }] };
        store.Save(incident);
        return incident;
    }

    [Fact]
    public void Prune_removes_automatic_incidents_first()
    {
        var store = new IncidentStore(Directory.CreateTempSubdirectory("whylag-test").FullName);
        foreach (var id in new[] { "20260101-100000-hotkey", "20260102-100000-automatic", "20260103-100000-automatic", "20260104-100000-manual", "20260105-100000-automatic" })
            Saved(store, id, TestData.Start);

        store.Prune(3);

        Assert.Equal(["20260105-100000-automatic", "20260104-100000-manual", "20260101-100000-hotkey"], store.ListIds());
    }

    [Fact]
    public void Prune_touches_personal_reports_when_nothing_else_is_left()
    {
        var store = new IncidentStore(Directory.CreateTempSubdirectory("whylag-test").FullName);
        foreach (var id in new[] { "20260101-100000-hotkey", "20260102-100000-hotkey" }) Saved(store, id, TestData.Start);
        store.Prune(1);
        Assert.Equal(["20260102-100000-hotkey"], store.ListIds());
    }

    [Fact]
    public void Summaries_contain_findings_but_no_recorded_data()
    {
        var store = new IncidentStore(Directory.CreateTempSubdirectory("whylag-test").FullName);
        Saved(store, "20260101-100000-hotkey", TestData.Start);

        var summary = Assert.Single(store.LoadSummaries());

        Assert.Equal("note", summary.Note);
        Assert.Equal("t", Assert.Single(summary.Findings).Title);
        Assert.Equal("game", summary.Ticks[^1].ForegroundProcess);
        Assert.Empty(summary.Ticks[^1].Metrics);
        Assert.Empty(summary.Ticks[^1].TopProcesses);
    }

    [Fact]
    public void Broken_files_are_skipped_in_summaries()
    {
        var store = new IncidentStore(Directory.CreateTempSubdirectory("whylag-test").FullName);
        Saved(store, "20260101-100000-hotkey", TestData.Start);
        File.WriteAllText(store.PathFor("20260102-100000-hotkey"), "{ broken");
        Assert.Single(store.LoadSummaries());
    }

    [Fact]
    public void Session_names_become_safe_file_names_and_the_folder_stays_bounded()
    {
        var dir = Directory.CreateTempSubdirectory("whylag-test").FullName;
        var store = new SessionStore(dir);
        store.Save(new GameSession { Id = @"20260101-100000-..\..\evil", App = "x" });
        Assert.Equal(dir, Path.GetDirectoryName(Assert.Single(Directory.GetFiles(dir))));

        for (var i = 0; i < 310; i++) store.Save(new GameSession { Id = $"20260102-{i:000000}-game", App = "x" });
        Assert.Equal(300, Directory.GetFiles(dir).Length);
    }

    [Fact]
    public void Broken_session_files_are_skipped()
    {
        var dir = Directory.CreateTempSubdirectory("whylag-test").FullName;
        File.WriteAllText(Path.Combine(dir, "x.json"), "{ broken");
        Assert.Empty(new SessionStore(dir).LoadAll());
    }
}

public class DetectionTests
{
    private static Tick Ping(int second, double ms, double idle, bool? fullscreen = false)
    {
        var t = new Tick { Timestamp = TestData.Start.AddSeconds(second), ForegroundFullscreen = fullscreen };
        t.Set(MetricKeys.PingTargetMs, ms);
        t.Set(MetricKeys.InputIdleSeconds, idle);
        return t;
    }

    private static SpikeSignal? SpikeAfterCalm(double idle, bool? fullscreen)
    {
        var detector = new SpikeDetector();
        for (var i = 0; i < 40; i++) detector.Feed(Ping(i, 30 + i % 5, idle, fullscreen));
        return detector.Feed(Ping(40, 220, idle, fullscreen));
    }

    [Fact]
    public void Ping_spike_counts_while_someone_is_at_the_pc() => Assert.NotNull(SpikeAfterCalm(idle: 10, fullscreen: false));

    [Fact]
    public void Ping_spike_is_ignored_when_nobody_is_there() => Assert.Null(SpikeAfterCalm(idle: 3600, fullscreen: false));

    [Fact]
    public void Ping_spike_counts_during_a_game_played_with_a_controller() => Assert.NotNull(SpikeAfterCalm(idle: 3600, fullscreen: true));

    [Fact]
    public void Stall_counts_even_when_nobody_is_there()
    {
        var t = Ping(0, 30, 3600);
        t.Set(MetricKeys.TickGapMs, 2500);
        Assert.Equal(MetricKeys.TickGapMs, new SpikeDetector().Feed(t)?.Metric);
    }

    private static Tick Frame(int second, string app)
    {
        var t = new Tick { Timestamp = TestData.Start.AddSeconds(second), FrameApp = app, ForegroundProcess = app, ForegroundFullscreen = true };
        t.Set(MetricKeys.FramesFps, 60);
        t.Set(MetricKeys.FramesMedianMs, 16);
        t.Set(MetricKeys.FramesMaxMs, 20);
        return t;
    }

    private static GameSession? Played(string app)
    {
        var tracker = new SessionTracker();
        for (var i = 0; i < 600; i++) tracker.Feed(Frame(i, app));
        var idle = new Tick { Timestamp = TestData.Start.AddSeconds(800) };
        return tracker.Feed(idle);
    }

    [Theory]
    [InlineData("brave")]
    [InlineData("Discord")]
    public void Browsers_and_chat_programs_are_no_gaming_sessions(string app) => Assert.Null(Played(app));

    [Fact]
    public void Games_are_sessions() => Assert.Equal("deadlock", Played("deadlock")?.App);
}

public class ReportSafetyTests
{
    [Theory]
    [InlineData(@"Faulting file C:\Users\Jo\AppData\app.dll", @"Faulting file C:\Users\<redacted>\AppData\app.dll")]
    [InlineData(@"c:\users\Some One\x", @"c:\users\<redacted>\x")]
    [InlineData(@"D:\Games\x", @"D:\Games\x")]
    public void Profile_paths_lose_the_account(string text, string expected) => Assert.Equal(expected, ReportBuilder.Anonymize(text));

    [Fact]
    public void Current_user_and_machine_are_removed()
    {
        var text = ReportBuilder.Anonymize($"{Environment.MachineName} {Environment.UserName}".ToUpperInvariant());
        if (Environment.MachineName.Length >= 3) Assert.DoesNotContain(Environment.MachineName.ToUpperInvariant(), text);
    }

    [Fact]
    public void Html_report_cannot_run_anything_from_incident_text()
    {
        var incident = new TestData().Analyse();
        incident.Note = "<script>alert(1)</script>";
        incident.Events.Add(new SystemEvent(TestData.Start, "System", "<img src=x onerror=alert(1)>", 1, EventLevel.Error, "\"><script>"));

        var html = HtmlReport.Build(incident);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default-src 'none'", html);
    }

    private static Incident Remote(string id, DateTimeOffset at, string rule = ProviderReport.RemoteRule) => new()
    {
        Id = id,
        TriggeredAt = at,
        Findings = [new Finding { RuleId = rule, Title = "t", Evidence = [new Evidence("E1", at, $@"Ping 200 ms C:\Users\Jo\x")] }],
    };

    [Fact]
    public void Provider_report_lists_only_moments_behind_the_router()
    {
        var now = TestData.Start;
        var text = ProviderReport.Markdown(
        [
            Remote("a", now.AddDays(-1)),
            Remote("b", now.AddDays(-1).AddHours(-1)),
            Remote("c", now.AddDays(-2), "net.local"),
            Remote("d-demo", now.AddDays(-2)),
            Remote("e", now.AddDays(-60)),
        ], now);

        Assert.Equal(2, text.Split("Ping 200 ms").Length - 1);
        Assert.DoesNotContain(@"\Jo\", text);
    }

    [Fact]
    public void Provider_report_without_moments_says_so() =>
        Assert.Contains(Localization.Loc.T("provider.none"), ProviderReport.Markdown([], TestData.Start));
}
