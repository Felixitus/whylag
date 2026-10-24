using System.Security.Cryptography;
using System.Security.Principal;
using WhyLag.Core;
using static WhyLag.Core.Localization.Loc;

namespace WhyLag.Collectors;

/// <summary>Finds, downloads (with user consent and hash check) and checks permissions for PresentMon.</summary>
public static class PresentMonLocator
{
    public const string Version = "2.6.0";
    public const string FileName = "PresentMon-2.6.0-x64.exe";
    public const string DownloadUrl = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";
    /// <summary>SHA-256 of the official release asset. A download with any other hash is discarded.</summary>
    public const string Sha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    public const double DownloadMb = 1.0;
    public const string License = "MIT (Intel Corporation)";

    private const string PerformanceLogUsersSid = "S-1-5-32-559";

    public static string UserToolsDir => Path.Combine(AppPaths.Root, "tools");

    /// <summary>Bundled copy next to WhyLag (installer, portable ZIP) or a copy downloaded by the user.</summary>
    public static string? Find()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools"),
            UserToolsDir,
        };
        foreach (var dir in candidates.Where(Directory.Exists))
        {
            var exe = Directory.GetFiles(dir, "PresentMon-*-x64.exe").OrderDescending().FirstOrDefault();
            if (exe is not null) return exe;
        }
        return null;
    }

    /// <summary>True when ETW sessions can be started: elevated, or member of "Performance Log Users".</summary>
    public static bool HasTracePermission()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator)
               || principal.IsInRole(new SecurityIdentifier(PerformanceLogUsersSid));
    }

    /// <summary>
    /// PowerShell command that adds the current user to "Performance Log Users". Run elevated, only after the user
    /// agreed; it takes effect after signing out and in again.
    /// </summary>
    public static string GrantPermissionCommand() =>
        $"Add-LocalGroupMember -SID '{PerformanceLogUsersSid}' -Member '{WindowsIdentity.GetCurrent().Name}'";

    public static async Task<string> DownloadAsync(HttpClient http, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UserToolsDir);
        var target = Path.Combine(UserToolsDir, FileName);
        var temp = target + ".download";
        using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(temp);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }

        string hash;
        await using (var check = File.OpenRead(temp))
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false));
        if (hash != Sha256)
        {
            File.Delete(temp);
            throw new InvalidDataException(T("err.presentmon.hash"));
        }
        File.Move(temp, target, overwrite: true);
        return target;
    }
}
