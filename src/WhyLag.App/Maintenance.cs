using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhyLag.Core;

namespace WhyLag.App;

public sealed record UpdateInfo(Version Version, string Url);

/// <summary>
/// Asks GitHub once per day whether a newer release exists. A single anonymous request; nothing is downloaded or
/// installed automatically. Can be turned off in Settings.
/// </summary>
internal static class UpdateChecker
{
    public const string Repository = "Felixitus/whylag";

    public static Version Current => typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0);

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"WhyLag/{Current.ToString(3)}");
            var json = JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false));
            var tag = json?["tag_name"]?.GetValue<string>()?.TrimStart('v');
            var url = json?["html_url"]?.GetValue<string>();
            if (tag is null || url is null || !Version.TryParse(tag, out var latest)) return null;
            return latest > Current ? new UpdateInfo(latest, url) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null; // offline, private repository or no release yet
        }
    }
}

/// <summary>
/// Packs everything needed to investigate a problem into one ZIP the user can attach to a GitHub issue:
/// error log, settings, source status, system info and the latest report – with user and computer names removed.
/// Nothing is uploaded automatically.
/// </summary>
internal static class BugReport
{
    public static string NewIssueUrl => $"https://github.com/{UpdateChecker.Repository}/issues/new?template=bug.yml";

    public static void Create(string zipPath, WhyLagSettings settings, Recorder? recorder, MachineInfo machine, IncidentStore store)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        void Add(string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(ReportBuilder.Anonymize(content));
        }

        var info = new StringBuilder()
            .AppendLine($"WhyLag {UpdateChecker.Current.ToString(3)}")
            .AppendLine($"OS: {machine.OsVersion}")
            .AppendLine($"Cores: {machine.LogicalCores}, RAM: {Math.Round(machine.TotalMemoryMb / 1024)} GB")
            .AppendLine($"GPUs: {string.Join(", ", machine.Gpus)}")
            .AppendLine($"Language: {Core.Localization.Loc.Language}")
            .AppendLine()
            .AppendLine("Sources:");
        foreach (var s in recorder?.Status ?? []) info.AppendLine($"  {s.Name}: {s.State} {s.Message}");
        Add("system.txt", info.ToString());

        // Settings without the Ollama address, which may contain a host name.
        var copy = JsonNode.Parse(JsonSerializer.Serialize(settings, Json.Options))!;
        copy["ai"]!["endpoint"] = settings.Ai.Endpoint.Contains("127.0.0.1") || settings.Ai.Endpoint.Contains("localhost") ? "local" : "remote";
        copy["window"] = null;
        Add("settings.json", copy.ToJsonString(Json.Options));

        var log = Path.Combine(AppPaths.LogsDir, "whylag.log");
        if (File.Exists(log)) Add("whylag.log", string.Join('\n', File.ReadLines(log).TakeLast(500)));

        if (store.LoadAll().FirstOrDefault() is { } latest) Add("latest-report.md", ReportBuilder.Markdown(latest, anonymize: true));
    }
}
