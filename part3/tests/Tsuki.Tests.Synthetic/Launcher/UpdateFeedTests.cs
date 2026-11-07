using System.Reflection;
using System.Text.Json;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>Package K40 (issue #6): the versions, the release list of GitHub and the choice of the update.</summary>
public sealed class UpdateFeedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-k40-feed-" + Guid.NewGuid().ToString("N"));

    public UpdateFeedTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("v0.1.3", 0, 1, 3)]
    [InlineData("0.1.3", 0, 1, 3)]
    [InlineData(" V1.2.30 ", 1, 2, 30)]
    [InlineData("v10.0.0", 10, 0, 0)]
    public void PlainTagsAreReleases(string tag, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParseTag(tag, out ReleaseVersion v));
        Assert.Equal(new ReleaseVersion(major, minor, patch), v);
    }

    [Theory]
    [InlineData("v0.2.0-rc1")]
    [InlineData("v0.2.0+build")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("nightly")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v-1.2.3")]
    [InlineData("v1..3")]
    [InlineData("v1.2.x")]
    [InlineData("vv1.2.3")]
    [InlineData("v1.2.3 beta")]
    [InlineData("v123456.0.0")]
    public void AnyOtherTagIsNotARelease(string? tag)
    {
        Assert.False(ReleaseVersion.TryParseTag(tag, out _));
    }

    [Theory]
    [InlineData("0.1.3+cccace40e2db0a06b4af03ac52bfe20112ae4bed", 0, 1, 3)]
    [InlineData("0.1.3-personal+cccace4", 0, 1, 3)]
    [InlineData("0.1.3-personal", 0, 1, 3)]
    [InlineData("1.0.0", 1, 0, 0)]
    public void TheRunningVersionDropsTheCommitAndThePersonalLabel(string informational, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParseInformational(informational, out ReleaseVersion v));
        Assert.Equal(new ReleaseVersion(major, minor, patch), v);
    }

    [Fact]
    public void VersionsCompareByNumberNotByText()
    {
        Assert.True(new ReleaseVersion(0, 1, 10) > new ReleaseVersion(0, 1, 9));
        Assert.True(new ReleaseVersion(1, 0, 0) > new ReleaseVersion(0, 99, 99));
        Assert.True(new ReleaseVersion(0, 2, 0) > new ReleaseVersion(0, 1, 99));
        Assert.True(new ReleaseVersion(0, 1, 3) <= new ReleaseVersion(0, 1, 3));
        Assert.False(new ReleaseVersion(0, 1, 3) > new ReleaseVersion(0, 1, 3));
        Assert.Equal("v0.1.4", new ReleaseVersion(0, 1, 4).Tag);
        Assert.Equal("Tsukihime-v0.1.4-win-x64.zip", UpdateSource.ZipAssetName(new ReleaseVersion(0, 1, 4)));
        Assert.False(ReleaseVersion.TryParseInformational("garbage", out _));
    }

    private static object Asset(string name, long size = 1000, string state = "uploaded") =>
        new { name, browser_download_url = $"https://github.com/{UpdateSource.DefaultRepository}/releases/download/x/{name}", size, state };

    private static object Rel(string tag, bool draft = false, bool prerelease = true, object[]? assets = null, string? body = "notes") =>
        new { tag_name = tag, name = "Release " + tag, body, draft, prerelease, html_url = "https://github.com/x/y/releases/tag/" + tag, assets = assets ?? Both(tag) };

    private static object[] Both(string tag) =>
        ReleaseVersion.TryParseTag(tag, out ReleaseVersion v) ? [Asset(UpdateSource.ZipAssetName(v), 110_268_124), Asset(UpdateSource.SumsAssetName, 98)] : [];

    [Fact]
    public void PreReleasesCountDraftsAndOddTagsDoNotAndTheHighestNewerOneIsPicked()
    {
        string json = JsonSerializer.Serialize(new object[]
        {
            Rel("v0.2.0", draft: true),                                         // a draft is never offered, even the highest
            Rel("v0.1.10"),                                                     // pre-release (as every release of the port so far): offered
            Rel("v0.1.9", prerelease: false),
            Rel("v0.3.0-rc1"),                                                  // not vX.Y.Z
            Rel("v0.1.11", assets: [Asset("Tsukihime-v0.1.11-win-x64.zip")]),   // no SHA256SUMS.txt: cannot be checked, so not offered
            Rel("v0.1.12", assets: [Asset(UpdateSource.SumsAssetName)]),        // no zip
            Rel("v0.1.13", assets: [Asset("Tsukihime-v0.1.13-win-x64.zip", state: "starter"), Asset(UpdateSource.SumsAssetName)]), // an upload in progress
            Rel("v0.1.2"),
        });
        IReadOnlyList<FeedEntry> entries = UpdateFeed.Parse(json);
        Assert.Equal(8, entries.Count);
        Assert.Equal("a draft", entries[0].Skipped);
        Assert.Null(entries[1].Skipped);
        Assert.True(entries[1].Release!.Prerelease);
        Assert.Equal("the tag is not vX.Y.Z", entries[3].Skipped);
        Assert.Equal("no SHA256SUMS.txt attached", entries[4].Skipped);
        Assert.Equal("no Tsukihime-v0.1.12-win-x64.zip attached", entries[5].Skipped);
        Assert.Equal("no Tsukihime-v0.1.13-win-x64.zip attached", entries[6].Skipped);

        ReleaseInfo? pick = UpdateFeed.Pick(entries, new ReleaseVersion(0, 1, 3));
        Assert.NotNull(pick);
        Assert.Equal(new ReleaseVersion(0, 1, 10), pick.Version);
        Assert.Equal("v0.1.10", pick.Tag);
        Assert.Equal("Release v0.1.10", pick.Title);
        Assert.Equal("Tsukihime-v0.1.10-win-x64.zip", pick.Zip.Name);
        Assert.Equal(110_268_124, pick.Zip.Size);
        Assert.Equal(UpdateSource.SumsAssetName, pick.Sums.Name);
        Assert.StartsWith("https://github.com/", pick.Zip.Url.AbsoluteUri, StringComparison.Ordinal);

        Assert.Null(UpdateFeed.Pick(entries, new ReleaseVersion(0, 1, 10)));    // the same version is no update
        Assert.Null(UpdateFeed.Pick(entries, new ReleaseVersion(0, 2, 0)));     // a newer running build is not downgraded
        Assert.Equal(new ReleaseVersion(0, 1, 9), UpdateFeed.Pick(entries.Where(e => e.Tag != "v0.1.10"), new ReleaseVersion(0, 1, 3))!.Version);
    }

    [Fact]
    public void OtherPlatformAssetsNeverReplaceTheExactWindowsZip()
    {
        const string tag = "v0.1.10";
        string json = JsonSerializer.Serialize(new[]
        {
            Rel(tag, assets:
            [
                Asset($"Tsukihime-{tag}-macos-arm64.zip"),
                Asset($"Tsukihime-{tag}-macos-x64.zip"),
                Asset($"Tsukihime-{tag}-linux-x64.tar.gz"),
                Asset($"Tsukihime-{tag}-win-x64.zip"),
                Asset(UpdateSource.SumsAssetName),
            ]),
        });
        ReleaseInfo? pick = UpdateFeed.Pick(UpdateFeed.Parse(json), new ReleaseVersion(0, 1, 9));
        Assert.NotNull(pick);
        Assert.Equal($"Tsukihime-{tag}-win-x64.zip", pick.Zip.Name);
        Assert.Equal(UpdateSource.SumsAssetName, pick.Sums.Name);
    }

    [Fact]
    public void TheAssetNamesMustMatchExactly()
    {
        string json = JsonSerializer.Serialize(new object[]
        {
            Rel("v0.1.4", assets: [Asset("tsukihime-v0.1.4-win-x64.zip"), Asset(UpdateSource.SumsAssetName)]),
            Rel("v0.1.5", assets: [Asset("Tsukihime-v0.1.4-win-x64.zip"), Asset(UpdateSource.SumsAssetName)]),
            Rel("v0.1.6", assets: [Asset("Tsukihime-v0.1.6-win-x64.zip"), Asset("sha256sums.txt")]),
        });
        Assert.All(UpdateFeed.Parse(json), e => Assert.Null(e.Release));
    }

    /// <summary>
    /// Review K41: "Open the release page" hands the release's html_url to the shell (UseShellExecute), so a release list whose html_url is a program or a file
    /// (file:///...exe, C:\...exe, a UNC share) would start it. Only an https page on github.com is kept; anything else falls back to the repository's Releases page.
    /// </summary>
    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\attacker.example\share\setup.exe")]
    [InlineData("http://github.com/x/y/releases/tag/v0.1.4")]
    [InlineData("https://github.com.evil.example/x/y/releases/tag/v0.1.4")]
    [InlineData("https://user@github.com/x/y/releases/tag/v0.1.4")]
    [InlineData("https://github.com:8443/x/y/releases/tag/v0.1.4")]
    [InlineData("ms-settings:privacy")]
    [InlineData("not a url")]
    public void AReleasePageThatIsNotAGitHubPageIsNeverOpened(string htmlUrl)
    {
        object[] assets = Both("v0.1.4");
        string json = JsonSerializer.Serialize(new object[] { new { tag_name = "v0.1.4", name = "x", body = "notes", draft = false, prerelease = true, html_url = htmlUrl, assets } });
        ReleaseInfo release = Assert.Single(UpdateFeed.Parse(json)).Release!;
        Assert.NotNull(release);
        Assert.Null(release.PageUrl);
    }

    [Fact]
    public void TheReleasePageOnGitHubIsKept()
    {
        string json = JsonSerializer.Serialize(new object[] { Rel("v0.1.4") });
        Assert.Equal("https://github.com/x/y/releases/tag/v0.1.4", UpdateFeed.Parse(json)[0].Release!.PageUrl);
    }

    [Fact]
    public void AnAnswerThatIsNoReleaseListIsAnError()
    {
        Assert.Throws<JsonException>(() => UpdateFeed.Parse("{\"message\":\"API rate limit exceeded\"}"));
        Assert.ThrowsAny<JsonException>(() => UpdateFeed.Parse("<html>"));
        Assert.ThrowsAny<JsonException>(() => UpdateFeed.Parse("[{\"tag_name\": \"v0.1.4\", \"assets\": [{\"name\": \"Tsukihime-v0.1.4-win-x64.zip\""));  // cut off (review K41)
        Assert.ThrowsAny<JsonException>(() => UpdateFeed.Parse(new string('[', 64) + new string(']', 64)));                                            // nested deeper than 32
        Assert.Empty(UpdateFeed.Parse("[]"));
        Assert.Empty(UpdateFeed.Parse("[1, \"x\", null]"));                    // entries that are no objects are passed over
        Assert.Null(UpdateFeed.Pick(UpdateFeed.Parse("[{\"tag_name\": 7}]"), default));
    }

    [Fact]
    public void TheNotesArePlainText()
    {
        string notes = UpdateFeed.PlainText(
            "# Tsukihime PC port v0.1.4 (pre-release)\r\n\r\n## Fixed\r\n\r\n- **Flowchart:** a jump, then `Esc`.\r\n* Second [item](https://example.invalid/x).\r\n" +
            "<!-- hidden -->\r\n\r\n\r\n\r\n> Fan-made &amp; unofficial.\r\n\r\n---\r\n\r\n```\r\ncode line\r\n```\r\n![picture](a.png)\r\nPress <kbd>Enter</kbd> on <folder>.");
        Assert.Equal(
            "Tsukihime PC port v0.1.4 (pre-release)\n\nFixed\n\n•  Flowchart: a jump, then Esc.\n•  Second item.\n\nFan-made & unofficial.\n\ncode line\npicture\nPress Enter on <folder>.",
            notes);
        Assert.Equal("This release has no notes.", UpdateFeed.PlainText(null));
        Assert.Equal("This release has no notes.", UpdateFeed.PlainText("  \n <!-- x --> \n"));
        string longNotes = UpdateFeed.PlainText(new string('a', 30_000));
        Assert.True(longNotes.Length < UpdateFeed.MaxNotesLength + 100);
        Assert.EndsWith("(The notes go on: the release page has the rest.)", longNotes, StringComparison.Ordinal);
    }

    [Fact]
    public void APersonalBuildNeverUpdates()
    {
        // the test assembly is no personal build; the note beside it makes it one (package K28's three marks: metadata, "-personal", PERSONAL_BUILD.txt)
        Assembly self = typeof(UpdateFeedTests).Assembly;
        LauncherBuild normal = LauncherBuild.Of(self, _dir);
        Assert.False(normal.IsPersonal);
        File.WriteAllText(Path.Combine(_dir, UpdateInstaller.PersonalNote), "PERSONAL BUILD");
        Assert.True(LauncherBuild.Of(self, _dir).IsPersonal);
        Assert.Equal("TsukiPersonalBuild", LauncherBuild.PersonalKey);

        // the running launcher's version comes from its informational version
        LauncherBuild app = LauncherBuild.Of(typeof(Tsuki.App.Services.AppSettings).Assembly, Path.Combine(_dir, "nowhere"));
        Assert.False(app.IsPersonal);
        Assert.Equal(ReleaseFiles.BuildVersion(), app.Version);
        Assert.StartsWith($"TsukihimeLauncher/{app.Version} (+https://github.com/{UpdateSource.DefaultRepository})", app.UserAgent(UpdateSource.GitHub), StringComparison.Ordinal);
    }
}
