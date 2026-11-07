// Package K42 (GitHub issue #6, "Something around this corner"): the footer's update button is always shown. The states (UpdateButtonState: text, enabled, gold
// look, tooltip), the toast after a manual check against the loopback stand-in of GitHub (FakeGitHub; no test talks to GitHub), the personal build, the footer's
// XAML and theme, and the real TsukihimeLauncher.exe of this build pressing its button in the verification mode (--update-selftest, no window).
using System.Net.Sockets;
using System.Xml.Linq;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

public sealed class UpdateButtonTests : IDisposable
{
    private const string Repo = UpdateSource.DefaultRepository;
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-k42-button-" + Guid.NewGuid().ToString("N"));
    private readonly ReleaseVersion _version = ReleaseFiles.BuildVersion();

    public UpdateButtonTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a launcher that has not quite ended; the temp folder is the system's to clean
        }
    }

    private ReleaseVersion Older => new(_version.Major, _version.Minor, Math.Max(0, _version.Patch - 1));

    /// <summary>A release of the build's own version with a zip and its SHA256SUMS.txt, as the check needs them to offer it.</summary>
    private object Publish(FakeGitHub server)
    {
        string zipName = UpdateSource.ZipAssetName(_version);
        string zip = ReleaseFiles.Zip(ReleaseFiles.Folder(_dir, "rel-" + Guid.NewGuid().ToString("N")[..6], "new"), Path.Combine(_dir, zipName));
        string sums = ReleaseFiles.Sums(Path.Combine(_dir, UpdateSource.SumsAssetName), zip);
        return server.Release(Repo, _version.Tag, [(zipName, zip), (UpdateSource.SumsAssetName, sums)]);
    }

    private static ReleaseInfo Sample(string tag) => new(
        ReleaseVersion.TryParseTag(tag, out ReleaseVersion v) ? v : default,
        tag,
        "Tsukihime PC port " + tag,
        "notes",
        null,
        true,
        new ReleaseAsset(UpdateSource.ZipAssetName(v), new Uri("https://github.com/x/y/releases/download/" + tag + "/z.zip"), 1),
        new ReleaseAsset(UpdateSource.SumsAssetName, new Uri("https://github.com/x/y/releases/download/" + tag + "/SHA256SUMS.txt"), 1));

    private static int ClosedPort()
    {
        var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public void TheButtonReadsCheckForUpdatesUntilANewerReleaseIsKnown()
    {
        UpdateButtonState idle = UpdateButtonState.Of(personal: false, checking: false, busy: false, update: null);
        Assert.Equal(new UpdateButtonState("Check for updates", true, false, "Ask GitHub whether a newer release of the port is out"), idle);
        Assert.Equal("CheckUpdatesButton", idle.StyleKey);

        UpdateButtonState checking = UpdateButtonState.Of(false, checking: true, false, null);
        Assert.Equal("Checking…", checking.Label);                                  // the real ellipsis, one character
        Assert.False(checking.Enabled);
        Assert.False(checking.Accent);
        Assert.Equal("CheckUpdatesButton", checking.StyleKey);

        UpdateButtonState newer = UpdateButtonState.Of(false, false, false, Sample("v0.1.5"));
        Assert.Equal("Update to v0.1.5", newer.Label);
        Assert.True(newer.Enabled);
        Assert.True(newer.Accent);
        Assert.Equal("UpdateButton", newer.StyleKey);                                     // K40's gold look
        Assert.Contains("v0.1.5", newer.ToolTip, StringComparison.Ordinal);

        // while the update downloads or installs it cannot be pressed again; a check started from Help & about makes it "Checking…" for that while
        UpdateButtonState busy = UpdateButtonState.Of(false, false, busy: true, Sample("v0.1.5"));
        Assert.Equal(("Update to v0.1.5", false, true), (busy.Label, busy.Enabled, busy.Accent));
        Assert.Equal(checking, UpdateButtonState.Of(false, checking: true, false, Sample("v0.1.5")));

        // every state has a tooltip, and the text never is empty (the button is always shown)
        foreach (UpdateButtonState s in new[] { idle, checking, newer, busy, UpdateButtonState.Of(true, false, false, null) })
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label));
            Assert.False(string.IsNullOrWhiteSpace(s.ToolTip));
        }
    }

    [Fact]
    public void APersonalBuildKeepsTheQuietButtonAndOnlySaysSo()
    {
        // a personal build never checks (package K28), so it is never checking or offered a release; its button still reads "Check for updates" and can be pressed
        UpdateButtonState personal = UpdateButtonState.Of(personal: true, checking: false, busy: false, update: null);
        Assert.Equal(("Check for updates", true, false, "CheckUpdatesButton"), (personal.Label, personal.Enabled, personal.Accent, personal.StyleKey));
        Assert.Equal(personal, UpdateButtonState.Of(true, true, false, Sample("v9.9.9")));
        Assert.Contains("personal build", personal.ToolTip, StringComparison.Ordinal);
        Assert.Equal("This personal build doesn't update itself. New versions are on the port's GitHub Releases page.", UpdateButtonState.PersonalToast);
    }

    [Fact]
    public async Task AManualCheckSaysItsOutcomeInTheToast()
    {
        using var server = new FakeGitHub();
        server.List(Repo, Publish(server));
        using var client = new UpdateClient(UpdateSource.ForLoopback(server.Base), "TsukihimeLauncher/0.0.1 (+tests)");

        // the newest: "You have the newest version (vX.Y.Z).", the button stays "Check for updates"
        UpdateCheckResult same = await client.CheckAsync(_version, CancellationToken.None);
        Assert.Equal(UpdateCheckOutcome.Newest, UpdateButtonState.OutcomeOf(same));
        Assert.Equal($"You have the newest version ({_version.Tag}).", UpdateButtonState.ToastAfterManualCheck(same, _version));
        Assert.Equal("Check for updates", UpdateButtonState.Of(false, false, false, same.Update).Label);

        // a newer release: no toast (the overlay opens at once), the button turns gold
        UpdateCheckResult newer = await client.CheckAsync(Older, CancellationToken.None);
        Assert.Equal(UpdateCheckOutcome.Newer, UpdateButtonState.OutcomeOf(newer));
        Assert.Null(UpdateButtonState.ToastAfterManualCheck(newer, Older));
        UpdateButtonState offered = UpdateButtonState.Of(false, false, false, newer.Update);
        Assert.Equal(($"Update to {_version.Tag}", true, true), (offered.Label, offered.Enabled, offered.Accent));

        // GitHub's answer is not a release list, or a 404: the short sentence, never the raw error
        server.Map($"/repos/{Repo}/releases?per_page=20", FakeResponse.Json("{\"message\":\"Not Found\"}"));
        UpdateCheckResult notAList = await client.CheckAsync(Older, CancellationToken.None);
        Assert.Equal(UpdateCheckOutcome.Failed, UpdateButtonState.OutcomeOf(notAList));
        Assert.Equal("Couldn't reach GitHub. Try again later; Help & about says what went wrong.", UpdateButtonState.ToastAfterManualCheck(notAList, Older));
        server.Map($"/repos/{Repo}/releases?per_page=20", new FakeResponse(404, "not found"u8.ToArray()));
        UpdateCheckResult missing = await client.CheckAsync(Older, CancellationToken.None);
        Assert.Contains("404", missing.Error, StringComparison.Ordinal);
        Assert.Equal(UpdateButtonState.FailedToast, UpdateButtonState.ToastAfterManualCheck(missing, Older));

        // offline (a port nobody listens on)
        using var offline = new UpdateClient(UpdateSource.ForLoopback(new Uri($"http://127.0.0.1:{ClosedPort()}/")), "t/1");
        UpdateCheckResult unreachable = await offline.CheckAsync(Older, CancellationToken.None);
        Assert.Equal(UpdateCheckOutcome.Failed, UpdateButtonState.OutcomeOf(unreachable));
        Assert.Equal(UpdateButtonState.FailedToast, UpdateButtonState.ToastAfterManualCheck(unreachable, Older));
        Assert.DoesNotContain(unreachable.Error!, UpdateButtonState.FailedToast, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFooterAlwaysShowsTheButtonLeftOfLogAndTheStatusLineTrims()
    {
        XDocument shell = XDocument.Load(Path.Combine(global::Tsuki.Tests.Synthetic.Repo.Src, "Tsuki.Launcher", "Shell.xaml"));
        XElement button = shell.Descendants(Xaml + "Button").Single(b => (string?)b.Attribute(X + "Name") == "UpdateButton");
        Assert.Null(button.Attribute("Visibility"));                                       // always there (K40 hid it without a release)
        Assert.Equal("{Binding UpdateButtonCommand}", (string?)button.Attribute("Command"));
        Assert.Equal("{Binding UpdateButtonLabel}", (string?)button.Attribute("Content"));
        Assert.Equal("{Binding UpdateButtonLabel}", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding UpdateButtonToolTip}", (string?)button.Attribute("ToolTip"));
        Assert.Equal("True", (string?)button.Attribute("ToolTipService.ShowOnDisabled"));      // review of K42: "Checking…" and a busy update are disabled, and WPF hides their tooltips otherwise
        Assert.Equal("{StaticResource CheckUpdatesButton}", (string?)button.Attribute("Style"));

        // left of Log in the same row; Log and the quiet button are both 42 high
        XElement footer = button.Parent!;
        XElement log = footer.Elements(Xaml + "Button").Single(b => (string?)b.Attribute("Content") == "Log");
        Assert.Equal("1", (string?)button.Attribute("Grid.Column"));
        Assert.Equal("2", (string?)log.Attribute("Grid.Column"));
        Assert.Equal("42", (string?)log.Attribute("Height"));
        Assert.True(footer.Elements().ToList().IndexOf(button) < footer.Elements().ToList().IndexOf(log), "the button comes before Log in the tab order");

        // the status line has only the room the buttons leave: not in a horizontal StackPanel (which would give it infinite width and never trim)
        XElement status = footer.Descendants(Xaml + "TextBlock").Single(t => (string?)t.Attribute("Text") == "{Binding StatusLine}");
        Assert.Equal("CharacterEllipsis", (string?)status.Attribute("TextTrimming"));
        Assert.NotEqual(Xaml + "StackPanel", status.Parent!.Name);

        // both looks are in the theme, the quiet one at Log's height
        XDocument theme = XDocument.Load(Path.Combine(global::Tsuki.Tests.Synthetic.Repo.Src, "Tsuki.Launcher", "Themes", "Theme.xaml"));
        XElement[] styles = [.. theme.Root!.Elements(Xaml + "Style")];
        foreach (string key in new[] { UpdateButtonState.Of(false, false, false, null).StyleKey, UpdateButtonState.Of(false, false, false, Sample("v1.0.0")).StyleKey })
        {
            Assert.Contains(styles, s => (string?)s.Attribute(X + "Key") == key);
        }

        XElement quiet = styles.Single(s => (string?)s.Attribute(X + "Key") == "CheckUpdatesButton");
        Assert.Contains(quiet.Elements(Xaml + "Setter"), s => (string?)s.Attribute("Property") == "Height" && (string?)s.Attribute("Value") == "42");

        // the render's narrow footer (update_footer_narrow.png) is drawn at the window's real smallest width
        string minWidth = (string?)XDocument.Load(Path.Combine(global::Tsuki.Tests.Synthetic.Repo.Src, "Tsuki.Launcher", "MainWindow.xaml")).Root!.Attribute("MinWidth") ?? "none";
        Assert.Contains($"private const int MainWindowMinWidth = {minWidth};", File.ReadAllText(Path.Combine(global::Tsuki.Tests.Synthetic.Repo.Src, "Tsuki.Launcher", "OffscreenRender.cs")), StringComparison.Ordinal);
    }

    /// <summary>The installed copy of the launcher's build output presses its footer button against <paramref name="api"/> (no window); null when there is no build output.</summary>
    private (string Report, int Exit)? PressInTheRealLauncher(Uri api, params string[] extra)
    {
        string? output = UpdateEndToEndTests.LauncherOutput();
        if (output is null)
        {
            return null;
        }

        string install = UpdateEndToEndTests.CopyInstall(output, Path.Combine(_dir, "Tsukihime-" + Guid.NewGuid().ToString("N")[..6]));
        (string report, string after, int exit) = UpdateEndToEndTests.RunUpdate(install, Path.Combine(_dir, "profile"), api, _dir, extra);
        Assert.Equal("(no report of the new launcher)", after);
        return (report, exit);
    }

    [Fact]
    public void TheRealLauncherSaysItIsTheNewestAfterAPress()
    {
        using var server = new FakeGitHub();
        server.List(Repo, Publish(server));
        if (PressInTheRealLauncher(server.Base, "--update-current", _version.ToString()) is not { } run)
        {
            Assert.Skip("the launcher's build output is not beside the tests (build src/Tsuki.slnx)");
            return;
        }

        Assert.Equal(1, run.Exit);                                                         // the self-test is about updating: no update is its one FAIL
        Assert.Contains("ok    before the check the footer button reads \"Check for updates\" in the quiet look and can be pressed", run.Report, StringComparison.Ordinal);
        Assert.Contains("ok    while it checks the footer button reads \"Checking…\" and is disabled", run.Report, StringComparison.Ordinal);
        Assert.Contains($"ok    after the press the footer button reads \"Check for updates\" (quiet, enabled); outcome Newest; toast \"You have the newest version ({_version.Tag}).\"", run.Report, StringComparison.Ordinal);
        Assert.Contains($"FAIL  the check found an update (none; You have the newest version, {_version.Tag}", run.Report, StringComparison.Ordinal);
        Assert.Single(run.Report.Split('\n'), l => l.StartsWith("FAIL", StringComparison.Ordinal));
        Assert.Equal(new[] { $"/repos/{Repo}/releases?per_page=20" }, server.Requests.Select(r => r.Path));   // one check, nothing downloaded
    }

    [Fact]
    public void TheRealLauncherSaysItCouldNotReachGitHubWhenOffline()
    {
        if (PressInTheRealLauncher(new Uri($"http://127.0.0.1:{ClosedPort()}/")) is not { } run)
        {
            Assert.Skip("the launcher's build output is not beside the tests (build src/Tsuki.slnx)");
            return;
        }

        Assert.Equal(1, run.Exit);
        Assert.Contains("ok    while it checks the footer button reads \"Checking…\" and is disabled", run.Report, StringComparison.Ordinal);
        Assert.Contains($"ok    after the press the footer button reads \"Check for updates\" (quiet, enabled); outcome Failed; toast \"{UpdateButtonState.FailedToast}\"", run.Report, StringComparison.Ordinal);
        Assert.Contains("FAIL  the check found an update (none; Could not check for updates (no connection", run.Report, StringComparison.Ordinal);
        Assert.Single(run.Report.Split('\n'), l => l.StartsWith("FAIL", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRealLauncherTurnsTheButtonGoldAndOpensTheOverlayWhenAPressFindsARelease()
    {
        string? output = UpdateEndToEndTests.LauncherOutput();
        if (output is null)
        {
            Assert.Skip("the launcher's build output is not beside the tests (build src/Tsuki.slnx)");
        }

        // a release that the update would install: the build's own files (it is installed and restarted into; the K40 tests check that part)
        string zipName = UpdateSource.ZipAssetName(_version);
        string zip = ReleaseFiles.Zip(UpdateEndToEndTests.CopyInstall(output, Path.Combine(_dir, "release")), Path.Combine(_dir, zipName));
        string sums = ReleaseFiles.Sums(Path.Combine(_dir, UpdateSource.SumsAssetName), zip);
        using var server = new FakeGitHub();
        server.List(Repo, server.Release(Repo, _version.Tag, [(zipName, zip), (UpdateSource.SumsAssetName, sums)]));
        (string report, int exit) = PressInTheRealLauncherAndRestart(output, server.Base, "--update-current", Older.ToString());

        Assert.True(exit == 0, report);
        Assert.DoesNotContain("FAIL", report, StringComparison.Ordinal);
        Assert.Contains("ok    while it checks the footer button reads \"Checking…\" and is disabled", report, StringComparison.Ordinal);
        Assert.Contains($"ok    after the press the footer button reads \"Update to {_version.Tag}\" (gold, enabled); outcome Newer; toast \"\"", report, StringComparison.Ordinal);
        Assert.Contains($"ok    the footer offers \"Update to {_version.Tag}\" in the gold look", report, StringComparison.Ordinal);
        Assert.Contains("ok    the manual check opened the overlay at once", report, StringComparison.Ordinal);
    }

    private (string Report, int Exit) PressInTheRealLauncherAndRestart(string output, Uri api, params string[] extra)
    {
        string install = UpdateEndToEndTests.CopyInstall(output, Path.Combine(_dir, "Tsukihime"));
        (string report, string after, int exit) = UpdateEndToEndTests.RunUpdate(install, Path.Combine(_dir, "profile"), api, _dir, extra);
        Assert.DoesNotContain("FAIL", after, StringComparison.Ordinal);
        return (report, exit);
    }

    [Fact]
    public void TheRealLauncherOfAPersonalBuildOnlySaysItDoesNotUpdateItself()
    {
        string? output = UpdateEndToEndTests.LauncherOutput();
        if (output is null)
        {
            Assert.Skip("the launcher's build output is not beside the tests (build src/Tsuki.slnx)");
        }

        string install = UpdateEndToEndTests.CopyInstall(output, Path.Combine(_dir, "Tsukihime"));
        File.WriteAllText(Path.Combine(install, UpdateInstaller.PersonalNote), "PERSONAL BUILD OF THE TSUKIHIME PC PORT - DO NOT SHARE");
        using var server = new FakeGitHub();
        server.List(Repo, Publish(server));
        (string report, string after, int exit) = UpdateEndToEndTests.RunUpdate(install, Path.Combine(_dir, "profile"), server.Base, _dir, "--update-current", "0.0.1");

        Assert.Equal(1, exit);
        Assert.Equal("(no report of the new launcher)", after);
        Assert.Contains("ok    before the check the footer button reads \"Check for updates\" in the quiet look and can be pressed", report, StringComparison.Ordinal);
        Assert.DoesNotContain("while it checks", report, StringComparison.Ordinal);
        Assert.Contains($"ok    after the press the footer button reads \"Check for updates\" (quiet, enabled); outcome no check; toast \"{UpdateButtonState.PersonalToast}\"", report, StringComparison.Ordinal);
        Assert.Contains("FAIL  the check found an update (none; Personal build: updates are off.)", report, StringComparison.Ordinal);
        Assert.Empty(server.Requests);                                                    // the press asked nothing
        Assert.Contains("update button: personal build, no check", File.ReadAllText(Path.Combine(_dir, "profile", LauncherLog.FileName)), StringComparison.Ordinal);
    }
}
