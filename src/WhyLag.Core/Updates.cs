using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhyLag.Core;

/// <summary>A published release: its page and, if attached, the installer and the checksum file.</summary>
public sealed record ReleaseInfo(Version Version, string PageUrl, string? SetupUrl, string? SetupName, string? ChecksumsUrl);

/// <summary>Reads GitHub release data and verifies downloads against the release's SHA256SUMS.txt.</summary>
public static partial class Updates
{
    /// <summary>Parses the GitHub "latest release" API response; null when it is not a usable release.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
            var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v');
            var page = root.GetProperty("html_url").GetString();
            if (tag is null || page is null || !Version.TryParse(tag, out var version)) return null;

            string? setupUrl = null, setupName = null, sumsUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    var url = asset.GetProperty("browser_download_url").GetString();
                    if (SetupName().IsMatch(name)) (setupName, setupUrl) = (name, url);
                    else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sumsUrl = url;
                }
            }
            return new ReleaseInfo(version, page, setupUrl, setupName, sumsUrl);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The expected SHA-256 (lower case) of <paramref name="fileName"/> from a "hash  name" checksum list.</summary>
    public static string? ExpectedHash(string checksums, string fileName)
    {
        foreach (var line in checksums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase)
                && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    [GeneratedRegex(@"^WhyLag-\d+\.\d+\.\d+-setup\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex SetupName();
}
