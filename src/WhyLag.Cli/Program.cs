using System.Diagnostics;
using WhyLag.AI;
using WhyLag.Collectors;
using WhyLag.Core;

// Headless companion of the WhyLag app: useful for testing collectors, scripting and bug reports.
Console.OutputEncoding = System.Text.Encoding.UTF8;
var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var settings = SettingsStore.Load();
WhyLag.Core.Localization.Loc.SetLanguage(settings.Language);

return command switch
{
    "record" => await Record(Option("--seconds", 30), args.Contains("--no-ping"), args.Contains("--explain")),
    "list" => List(),
    "show" => Show(args.ElementAtOrDefault(1)),
    "explain" => await Explain(args.ElementAtOrDefault(1), OptionString("--model")),
    "ai-status" => await AiStatus(),
    "paths" => Paths(),
    "diag" => Diag(args.Skip(1).ToArray()),
    "demo" => Demo(),
    "changes" => Changes(),
    "router" => Router(),
    "rules" => Rules(),
    "export-fixture" => ExportFixture(args.ElementAtOrDefault(1)),
    "check-fixture" => CheckFixture(args.Skip(1).ToArray()),
    "get-presentmon" => await GetPresentMon(),
    "latency-test" => LatencyTest(Option("--seconds", 5)),
    LatencyHelperHost.Argument => LatencyHelperHost.Run(args),
    _ => Help(),
};

async Task<int> Record(int seconds, bool noPing, bool explain)
{
    if (noPing) settings.PingEnabled = false;
    var machine = HardwareInfo.Machine();
    var (metrics, events) = DefaultSources.Create(settings, machine);
    using var recorder = new Recorder(settings, metrics, events, machine, new IncidentStore());
    recorder.IncidentCaptured += i =>
    {
        if (i.Trigger == TriggerKind.Automatic) Console.WriteLine($"  auto incident: {i.TriggerDetail}");
    };

    var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
    recorder.Start();
    Console.WriteLine($"Recording for {seconds} s ... (Ctrl+C to stop early)");
    foreach (var s in recorder.Status) Console.WriteLine($"  source {s.Name}: {s.State} {s.Message}");
    foreach (var p in metrics.OfType<PerformanceSource>().SelectMany(p => p.MissingCounters)) Console.WriteLine($"  missing counter: {p}");
    foreach (var c in events.OfType<EventLogSource>().SelectMany(e => e.UnavailableChannels)) Console.WriteLine($"  event channel unavailable: {c}");

    var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    var started = Stopwatch.StartNew();
    while (started.Elapsed < TimeSpan.FromSeconds(seconds) && !stop.IsCancellationRequested)
    {
        try { await Task.Delay(1000, stop.Token); } catch (TaskCanceledException) { }
        if (recorder.LatestTick is { } t)
        {
            string M(string k) => t.Get(k) is { } v ? Stats.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
            Console.Write($"\r  {recorder.BufferedSeconds,3}s  cpu {M(MetricKeys.CpuTotal),5}%  disk {M(MetricKeys.DiskActive),5}%  gpu {M(MetricKeys.Gpu3D),5}%  " +
                          $"router {M(MetricKeys.PingGatewayMs),4}ms  net {M(MetricKeys.PingTargetMs),4}ms  " +
                          $"fps {M(MetricKeys.FramesFps),4} ({t.FrameApp ?? "-"})  wifi {M(MetricKeys.WifiSignal),3}%  fg {t.ForegroundProcess ?? "-",-16}");
        }
    }
    Console.WriteLine();

    var incident = recorder.Capture(TriggerKind.Manual, "whylag record");
    var cpuUsed = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
    Console.WriteLine($"WhyLag's own CPU use: {cpuUsed.TotalMilliseconds / started.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:0.00} % of all cores, " +
                      $"{Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB working set");
    Console.WriteLine();

    if (explain && settings.Ai.Enabled) await ExplainIncident(incident, null);
    Console.WriteLine(ReportBuilder.Markdown(incident));
    Console.WriteLine($"Saved: {new IncidentStore().PathFor(incident.Id)}");
    return 0;
}

int List()
{
    var store = new IncidentStore();
    foreach (var id in store.ListIds())
    {
        var i = store.Load(id);
        Console.WriteLine($"{id}  {i?.Findings.FirstOrDefault()?.Title ?? "(no findings)"}");
    }
    return 0;
}

int Show(string? id)
{
    if (LoadIncident(id) is not { } incident) return 1;
    Console.WriteLine(ReportBuilder.Markdown(incident));
    return 0;
}

async Task<int> Explain(string? id, string? model)
{
    if (LoadIncident(id) is not { } incident) return 1;
    var ok = await ExplainIncident(incident, model);
    Console.WriteLine(ReportBuilder.Markdown(incident));
    return ok ? 0 : 2;
}

async Task<bool> ExplainIncident(Incident incident, string? model)
{
    if (model is not null) settings.Ai.Model = model;
    var client = OllamaClient.Create(settings.Ai.Endpoint, TimeSpan.FromSeconds(settings.Ai.TimeoutSeconds));
    if (!client.IsLocal) Console.WriteLine($"Note: {client.Endpoint} is not on this PC – the findings will be sent there.");
    Console.WriteLine("Asking the local AI ...");
    var result = await new IncidentExplainer(client, settings.Ai).ExplainAsync(incident);
    Console.WriteLine($"AI: {result.Status} – {result.Message}");
    if (result.Explanation is { } e)
    {
        foreach (var note in e.ValidationNotes) Console.WriteLine($"  validator: {note}");
        incident.Explanation = e;
        new IncidentStore().Save(incident);
    }
    Console.WriteLine();
    return result.Status == ExplainStatus.Ok;
}

async Task<int> AiStatus()
{
    Console.WriteLine($"Local AI enabled in settings: {settings.Ai.Enabled}");
    var client = OllamaClient.Create(settings.Ai.Endpoint, TimeSpan.FromSeconds(5));
    var version = await client.GetVersionAsync();
    Console.WriteLine($"Ollama at {client.Endpoint}: {(version is null ? "not reachable" : "version " + version)}");
    var machine = HardwareInfo.Machine();
    var gpus = HardwareInfo.Gpus();
    var suggestion = ModelAdvisor.Recommend(gpus.FirstOrDefault()?.VramMb ?? 0, machine.TotalMemoryMb);
    Console.WriteLine($"Hardware: {string.Join(", ", machine.Gpus)}; {Math.Round(machine.TotalMemoryMb / 1024)} GB RAM");
    Console.WriteLine(suggestion is null
        ? "Suggested model: none (AI explanations are not recommended on this PC)"
        : $"Suggested model: {suggestion.Name} (≈{suggestion.DownloadGb} GB download, license {suggestion.License}) – install with: ollama pull {suggestion.Name}");
    if (version is null) return 1;

    var models = await client.ListModelsAsync();
    Console.WriteLine($"Installed models: {(models.Count == 0 ? "none" : string.Join(", ", models.Select(m => m.Name)))}");
    Console.WriteLine($"WhyLag would use: {ModelAdvisor.PickInstalled(models) ?? "(none)"}");
    return 0;
}

int Paths()
{
    Console.WriteLine($"Config:    {AppPaths.ConfigFile}");
    Console.WriteLine($"Incidents: {AppPaths.IncidentsDir}");
    Console.WriteLine($"Logs:      {AppPaths.LogsDir}");
    return 0;
}

async Task<int> GetPresentMon()
{
    if (PresentMonLocator.Find() is { } existing)
    {
        Console.WriteLine($"PresentMon is already available: {existing}");
    }
    else
    {
        Console.WriteLine($"Downloading PresentMon {PresentMonLocator.Version} (≈{PresentMonLocator.DownloadMb} MB, license {PresentMonLocator.License})");
        Console.WriteLine($"from {PresentMonLocator.DownloadUrl}");
        using var http = new HttpClient();
        var path = await PresentMonLocator.DownloadAsync(http);
        Console.WriteLine($"Checksum verified. Saved to {path}");
    }
    Console.WriteLine(PresentMonLocator.HasTracePermission()
        ? "Permission: OK (administrator or 'Performance Log Users')."
        : $"Permission missing. Run in an administrator PowerShell, then sign out and in again:\n  {PresentMonLocator.GrantPermissionCommand()}");
    return 0;
}

int LatencyTest(int seconds)
{
    static void Print(double dpc, double isr, IEnumerable<DriverLatency> drivers) =>
        Console.WriteLine($"DPC max {dpc:0} µs, ISR max {isr:0} µs; " +
                          string.Join(", ", drivers.Select(d => $"{d.Driver} {d.MaxMicroseconds:0} µs ({d.Count}×)")));

    using var tracer = new KernelLatencyTracer();
    try
    {
        tracer.Start();
    }
    catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
    {
        // Not elevated: use the same helper the app starts (Windows asks for administrator approval).
        Console.WriteLine($"{ex.Message} Starting the elevated helper – confirm the Windows prompt ...");
        return LatencyTestWithHelper(seconds, Print);
    }
    for (var i = 0; i < seconds; i++)
    {
        Thread.Sleep(1000);
        var s = tracer.TakeSecond();
        Print(s.DpcMaxUs, s.IsrMaxUs, s.Drivers);
    }
    return 0;
}

int LatencyTestWithHelper(int seconds, Action<double, double, IEnumerable<DriverLatency>> print)
{
    using var helper = new LatencyHelperSource();
    helper.Start();
    var received = 0;
    var waited = Stopwatch.StartNew();
    while (received < seconds && waited.Elapsed < TimeSpan.FromSeconds(seconds + 180))
    {
        Thread.Sleep(1000);
        var tick = new Tick { Timestamp = DateTimeOffset.Now };
        try
        {
            helper.Collect(tick);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        if (tick.Get(MetricKeys.DpcMaxUs) is not { } dpc) continue;
        print(dpc, tick.Get(MetricKeys.IsrMaxUs) ?? 0, tick.Drivers);
        received++;
    }
    if (received == 0) Console.Error.WriteLine("No data from the helper.");
    return received > 0 ? 0 : 1;
}

int Router()
{
    using var router = new RouterSource();
    router.Start();
    if (router.Warnings.Count > 0) { Console.WriteLine(router.Warnings[0]); return 1; }
    for (var i = 0; i < 4; i++)
    {
        Thread.Sleep(2500);
        var t = new Tick { Timestamp = DateTimeOffset.Now };
        router.Collect(t);
        Console.WriteLine($"router: down {t.Get(MetricKeys.RouterDownMbps):0.00} Mbit/s, up {t.Get(MetricKeys.RouterUpMbps):0.00} Mbit/s");
    }
    return 0;
}

int Changes()
{
    var changes = SystemChangeReader.Read(TimeSpan.FromDays(90));
    Console.WriteLine($"{changes.Count} driver/Windows update(s) in the last 90 days:");
    foreach (var c in changes.OrderByDescending(c => c.At).Take(15)) Console.WriteLine($"  {c.At.ToLocalTime():g}  {c.Kind,-13} {c.Description}");
    var impacts = ChangeAnalyzer.Compare(changes, new IncidentStore().LoadAll(), DateTimeOffset.Now);
    Console.WriteLine(impacts.Count == 0 ? "No change with clearly more incidents afterwards." : "Changes followed by more incidents:");
    foreach (var i in impacts) Console.WriteLine($"  {i.Change.At.ToLocalTime():d} {i.Change.Description}: {i.Before} before ({i.DaysBefore:0} d), {i.After} after ({i.DaysAfter:0} d)");
    return 0;
}

int Demo()
{
    var incident = DemoIncident.Create(HardwareInfo.Machine());
    new IncidentStore().Save(incident);
    Console.WriteLine(ReportBuilder.Markdown(incident));
    Console.WriteLine($"Saved demo incident: {incident.Id}  (try: whylag-cli explain {incident.Id})");
    return 0;
}

int Diag(string[] paths)
{
    if (paths.Length == 0)
        paths = [@"\Process V2(*)\% Processor Time", @"\Process V2(*)\IO Data Bytes/sec", @"\GPU Engine(*engtype_3D)\Utilization Percentage", @"\Processor Information(_Total)\% Processor Performance"];
    foreach (var path in paths)
    {
        var values = CounterDiagnostics.Read(path, TimeSpan.FromSeconds(1));
        Console.WriteLine(values is null ? $"{path}: not available" : $"{path}: {values.Count} instance(s)");
        foreach (var (instance, value) in (values ?? []).OrderByDescending(v => v.Value).Take(5))
            Console.WriteLine($"    {instance,-60} {value:0.##}");
    }
    return 0;
}

int Rules()
{
    foreach (var rule in WhyLag.Core.Rules.RuleEngine.DefaultRules)
        Console.WriteLine($"  {rule.Id,-28} {(rule is WhyLag.Core.Rules.JsonRule ? "json" : "code")}");
    Console.WriteLine($"Your own rules: {AppPaths.RulesDir}");
    foreach (var error in WhyLag.Core.Rules.JsonRules.Errors) Console.Error.WriteLine($"  error: {error}");
    return WhyLag.Core.Rules.JsonRules.Errors.Count == 0 ? 0 : 1;
}

int ExportFixture(string? id)
{
    if (LoadIncident(id) is not { } incident) return 1;
    static List<string> Ids(string? list) => list?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];
    var expectation = new FixtureExpectation
    {
        Description = OptionString("--description") ?? "",
        Expect = Ids(OptionString("--expect")),
        Forbid = Ids(OptionString("--forbid")),
        AutoTrigger = OptionString("--auto") is { } auto ? bool.Parse(auto) : null,
    };
    var path = OptionString("--out") ?? $"{incident.Id}.json";
    File.WriteAllText(path, Fixtures.Export(incident, expectation));
    var problems = Fixtures.Check(expectation, Fixtures.Load(path).Incident);
    Console.WriteLine($"Wrote {path} ({new FileInfo(path).Length / 1024} KB)");
    foreach (var p in problems) Console.WriteLine($"  does not match yet: {p}");
    return 0;
}

int CheckFixture(string[] paths)
{
    var failed = 0;
    foreach (var path in paths)
    {
        var (expectation, incident) = Fixtures.Load(path);
        var problems = Fixtures.Check(expectation, incident);
        Console.WriteLine($"{(problems.Count == 0 ? "ok  " : "FAIL")} {Path.GetFileName(path)}");
        foreach (var p in problems) Console.WriteLine($"     {p}");
        if (problems.Count > 0) failed++;
    }
    return failed == 0 ? 0 : 1;
}

int Help()
{
    Console.WriteLine("""
        WhyLag command line (whylag-cli)

          whylag-cli record [--seconds 30] [--no-ping] [--explain]   record, then analyse the last moment
          whylag-cli list                                             list saved incidents
          whylag-cli show <id|latest>                                 print an incident report
          whylag-cli explain <id|latest> [--model qwen3:4b]           explain an incident with local AI (Ollama)
          whylag-cli ai-status                                        check Ollama, models and the suggested model
          whylag-cli demo                                             create a demo incident with synthetic data
          whylag-cli get-presentmon                                   download PresentMon for frame times (checksum-verified)
          whylag-cli diag [counter paths]                             show raw performance counter values
          whylag-cli changes                                          driver/Windows updates that made lag worse
          whylag-cli router                                           read household traffic from the router (UPnP)
          whylag-cli latency-test [--seconds 5]                       measure DPC/ISR latency (asks for admin rights)
          whylag-cli rules                                            list rules, including your own JSON rules
          whylag-cli export-fixture <id> --expect a,b [--forbid c] [--auto true|false] [--out file]
                                                                      turn an incident into an anonymized test case
          whylag-cli check-fixture <files>                            run rules on test cases
          whylag-cli paths                                            show where data is stored
        """);
    return 0;
}

Incident? LoadIncident(string? id)
{
    var store = new IncidentStore();
    id = id is null or "latest" ? store.ListIds().FirstOrDefault() : id;
    var incident = id is null ? null : File.Exists(id) ? IncidentStore.LoadFile(id) : store.Load(id);
    if (incident is null) Console.Error.WriteLine("Incident not found. Use 'whylag-cli list'.");
    return incident;
}

int Option(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

string? OptionString(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
