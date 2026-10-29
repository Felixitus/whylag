namespace WhyLag.Core.Tests;

public class UpdateTests
{
    private const string Release = """
        {
          "tag_name": "v0.2.0", "html_url": "https://github.com/Felixitus/whylag/releases/tag/v0.2.0",
          "draft": false, "prerelease": false,
          "assets": [
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/Felixitus/whylag/releases/download/v0.2.0/SHA256SUMS.txt" },
            { "name": "WhyLag-0.2.0-win-x64-portable.zip", "browser_download_url": "https://github.com/Felixitus/whylag/releases/download/v0.2.0/WhyLag-0.2.0-win-x64-portable.zip" },
            { "name": "WhyLag-0.2.0-setup.exe", "browser_download_url": "https://github.com/Felixitus/whylag/releases/download/v0.2.0/WhyLag-0.2.0-setup.exe" }
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
        Assert.Equal("https://github.com/Felixitus/whylag/releases/download/v0.2.0/WhyLag-0.2.0-setup.exe", release.SetupUrl);
        Assert.Equal("https://github.com/Felixitus/whylag/releases/download/v0.2.0/SHA256SUMS.txt", release.ChecksumsUrl);
    }

    [Theory]
    [InlineData("\"draft\": false", "\"draft\": true")]
    [InlineData("\"prerelease\": false", "\"prerelease\": true")]
    [InlineData("v0.2.0\", \"html_url", "nightly\", \"html_url")]
    public void Drafts_prereleases_and_odd_tags_are_ignored(string from, string to) =>
        Assert.Null(Updates.ParseRelease(Release.Replace(from, to)));

    [Theory]
    [InlineData("https://evil.example/Felixitus/whylag/releases/download/v0.2.0/WhyLag-0.2.0-setup.exe")]
    [InlineData("http://github.com/Felixitus/whylag/releases/download/v0.2.0/WhyLag-0.2.0-setup.exe")]
    [InlineData("https://github.com/someone/else/releases/download/v0.2.0/WhyLag-0.2.0-setup.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    public void Downloads_outside_the_repository_are_ignored(string url)
    {
        var release = Updates.ParseRelease(Release.Replace("https://github.com/Felixitus/whylag/releases/download/v0.2.0/WhyLag-0.2.0-setup.exe", url));
        Assert.NotNull(release);
        Assert.Null(release.SetupUrl);
    }

    [Fact]
    public void Release_page_outside_the_repository_is_rejected() =>
        Assert.Null(Updates.ParseRelease(Release.Replace("https://github.com/Felixitus/whylag/releases/tag/v0.2.0", "file:///C:/x.exe")));

    [Theory]
    [InlineData("20261029-221902-automatic", true)]
    [InlineData("demo", true)]
    [InlineData("..\\..\\config", false)]
    [InlineData("../x", false)]
    [InlineData("C:\\x", false)]
    [InlineData("", false)]
    public void Incident_ids_never_contain_paths(string id, bool valid)
    {
        Assert.Equal(valid, IncidentStore.IsValidId(id));
        var store = new IncidentStore(Path.GetTempPath());
        if (!valid)
        {
            Assert.Null(store.Load(id));
            Assert.Throws<ArgumentException>(() => store.PathFor(id));
        }
    }

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
