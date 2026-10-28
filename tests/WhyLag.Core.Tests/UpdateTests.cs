namespace WhyLag.Core.Tests;

public class UpdateTests
{
    private const string Release = """
        {
          "tag_name": "v0.2.0", "html_url": "https://github.com/Felixitus/whylag/releases/tag/v0.2.0",
          "draft": false, "prerelease": false,
          "assets": [
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.test/SHA256SUMS.txt" },
            { "name": "WhyLag-0.2.0-win-x64-portable.zip", "browser_download_url": "https://example.test/portable.zip" },
            { "name": "WhyLag-0.2.0-setup.exe", "browser_download_url": "https://example.test/setup.exe" }
          ]
        }
        """;

    [Fact]
    public void Release_with_installer_and_checksums_is_parsed()
    {
        var release = Updates.ParseRelease(Release);
        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal("WhyLag-0.2.0-setup.exe", release.SetupName);
        Assert.Equal("https://example.test/setup.exe", release.SetupUrl);
        Assert.Equal("https://example.test/SHA256SUMS.txt", release.ChecksumsUrl);
    }

    [Theory]
    [InlineData("\"draft\": false", "\"draft\": true")]
    [InlineData("\"prerelease\": false", "\"prerelease\": true")]
    [InlineData("v0.2.0\", \"html_url", "nightly\", \"html_url")]
    public void Drafts_prereleases_and_odd_tags_are_ignored(string from, string to) =>
        Assert.Null(Updates.ParseRelease(Release.Replace(from, to)));

    [Fact]
    public void Broken_json_is_ignored() => Assert.Null(Updates.ParseRelease("{ \"tag_name\": "));

    [Fact]
    public void Checksum_is_found_for_the_exact_file_name()
    {
        var hash = new string('a', 64);
        var sums = $"{new string('b', 64)}  WhyLag-0.2.0-win-x64-portable.zip\r\n{hash.ToUpperInvariant()}  WhyLag-0.2.0-setup.exe\r\n";
        Assert.Equal(hash, Updates.ExpectedHash(sums, "WhyLag-0.2.0-setup.exe"));
        Assert.Null(Updates.ExpectedHash(sums, "WhyLag-0.1.0-setup.exe"));
        Assert.Null(Updates.ExpectedHash("not-a-hash  WhyLag-0.2.0-setup.exe", "WhyLag-0.2.0-setup.exe"));
    }

    [Fact]
    public void File_hash_matches_sha256()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Updates.HashFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
