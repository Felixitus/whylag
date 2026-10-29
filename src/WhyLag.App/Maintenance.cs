using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhyLag.Core;

namespace WhyLag.App;

/// <summary>
/// Asks GitHub whether a newer release exists (one anonymous request, at most once a day; can be turned off in
/// Settings) and installs it: the installer is downloaded from the release, checked against the release's
/// SHA256SUMS.txt and run silently; it restarts WhyLag afterwards. Installing needs the user's click unless
/// automatic updates are turned on.
/// </summary>
internal static class UpdateChecker
{
    public const string Repository = Updates.Repository;

    public static Version Current => typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0);

    private static string DownloadDir => Path.Combine(Path.GetTempPath(), "WhyLag-update");

    /// <summary>Removes the installer of a finished update.</summary>
    public static void CleanUp()
    {
        try
        {
            if (Directory.Exists(DownloadDir)) Directory.Delete(DownloadDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still in use by a running installer; removed next time
        }
    }

    /// <summary>Only an installed copy can update itself; a portable copy opens the release page instead.</summary>
    public static bool CanInstall => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WhyLag/{Current.ToString(3)}");
        return http;
    }

    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = CreateClient(TimeSpan.FromSeconds(10));
            var release = Updates.ParseRelease(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false));
            return release is not null && release.Version > Current ? release : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null; // offline or no release yet
        }
    }

    /// <summary>Downloads the installer and verifies its checksum. Throws <see cref="InvalidDataException"/> on a mismatch.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<int> progress, CancellationToken ct = default)
    {
        if (release.SetupUrl is null || release.SetupName is null || release.ChecksumsUrl is null)
            throw new InvalidDataException("The release has no installer or checksum file.");

        using var http = CreateClient(TimeSpan.FromMinutes(10));
        var expected = Updates.ExpectedHash(await http.GetStringAsync(release.ChecksumsUrl, ct).ConfigureAwait(false), release.SetupName)
                       ?? throw new InvalidDataException("The installer is not listed in SHA256SUMS.txt.");

        var dir = DownloadDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, release.SetupName);
        using (var response = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress.Report((int)(done * 100 / total));
            }
        }

        if (Updates.HashFile(path) != expected)
        {
            File.Delete(path);
            throw new InvalidDataException(Core.Localization.Loc.T("update.badhash"));
        }
        return path;
    }

    /// <summary>
    /// Starts the installer silently. It keeps the current autostart and desktop shortcut choices and starts WhyLag
    /// again when it is done; the caller exits WhyLag right after this.
    /// </summary>
    public static void StartInstaller(string setupPath)
    {
        var tasks = new List<string>();
        if (StartupRegistration.IsEnabled()) tasks.Add("autostart");
        if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "WhyLag.lnk"))) tasks.Add("desktopicon");
        Process.Start(new ProcessStartInfo(setupPath)
        {
            ArgumentList = { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", $"/TASKS={string.Join(',', tasks)}", "/RELAUNCH=1" },
            UseShellExecute = false,
        });
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
