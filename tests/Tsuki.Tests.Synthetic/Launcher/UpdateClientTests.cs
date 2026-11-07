using System.Net.Sockets;
using System.Text;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>
/// Package K40 (issue #6): the check and the download against a loopback stand-in of GitHub (<see cref="FakeGitHub"/>; no test talks to GitHub): the request,
/// the redirect to the file host, the refusals, the checksum, the progress and Cancel, offline.
/// </summary>
public sealed class UpdateClientTests : IDisposable
{
    private const string Repo = UpdateSource.DefaultRepository;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-k40-client-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHub _server = new();
    private readonly ReleaseVersion _version = ReleaseFiles.BuildVersion();

    public UpdateClientTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _server.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private UpdateClient Client() => new(UpdateSource.ForLoopback(_server.Base), "TsukihimeLauncher/0.0.1 (+tests)");

    private ReleaseVersion Older => new(_version.Major, _version.Minor, Math.Max(0, _version.Patch - 1));

    /// <summary>A release of the build's own version with a good zip and SHA256SUMS.txt (or the ones the test spoils).</summary>
    private (string Zip, string Sums) Publish(string? sumsText = null, string tag = "")
    {
        string zipName = UpdateSource.ZipAssetName(_version);
        string zip = ReleaseFiles.Zip(ReleaseFiles.Folder(_dir, "rel-" + Guid.NewGuid().ToString("N")[..6], "new"), Path.Combine(_dir, zipName));
        string sums = Path.Combine(_dir, UpdateSource.SumsAssetName);
        if (sumsText is null)
        {
            ReleaseFiles.Sums(sums, zip);
        }
        else
        {
            File.WriteAllText(sums, sumsText);
        }

        _server.List(Repo, _server.Release(Repo, tag.Length > 0 ? tag : _version.Tag, [(zipName, zip), (UpdateSource.SumsAssetName, sums)]));
        return (zip, sums);
    }

    [Fact]
    public async Task TheCheckAsksForTheReleaseListWithTheRightHeadersAndFindsTheUpdate()
    {
        Publish();
        using UpdateClient client = Client();
        UpdateCheckResult result = await client.CheckAsync(Older, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.NotNull(result.Update);
        Assert.Equal(_version, result.Update.Version);
        FakeRequest request = Assert.Single(_server.Requests);
        Assert.Equal($"/repos/{Repo}/releases?per_page=20", request.Path);
        Assert.Equal("TsukihimeLauncher/0.0.1 (+tests)", request.Headers["user-agent"]);
        Assert.Equal("application/vnd.github+json", request.Headers["accept"]);
        Assert.Equal("2022-11-28", request.Headers["x-github-api-version"]);
        Assert.False(request.Headers.ContainsKey("authorization"));

        // the running version is the newest: no update, no error
        UpdateCheckResult same = await client.CheckAsync(_version, CancellationToken.None);
        Assert.Null(same.Update);
        Assert.Null(same.Error);
    }

    [Fact]
    public async Task OfflineOrAnErrorIsAnErrorTextAndNeverAnException()
    {
        // a port nobody listens on (taken and released again)
        var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int closed = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using (var offline = new UpdateClient(UpdateSource.ForLoopback(new Uri($"http://127.0.0.1:{closed}/")), "t/1"))
        {
            UpdateCheckResult result = await offline.CheckAsync(Older, CancellationToken.None);
            Assert.Null(result.Update);
            Assert.Contains("no connection", result.Error, StringComparison.Ordinal);
        }

        using UpdateClient client = Client();
        UpdateCheckResult missing = await client.CheckAsync(Older, CancellationToken.None);   // nothing mapped: 404
        Assert.Contains("404", missing.Error, StringComparison.Ordinal);

        _server.Map($"/repos/{Repo}/releases?per_page=20", FakeResponse.Json("{\"message\":\"Not Found\"}"));
        Assert.Equal("the answer was not a release list", (await client.CheckAsync(Older, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task ARedirectAwayFromTheAllowedHostsIsNotFollowed()
    {
        string zipName = UpdateSource.ZipAssetName(_version);
        _server.Map("/elsewhere", FakeResponse.Redirect("http://192.0.2.1/payload.zip"));      // TEST-NET-1, never reached: refused before any connection
        using UpdateClient client = Client();
        var e = await Assert.ThrowsAsync<UpdateRefusedException>(() => client.DownloadFileAsync(new Uri(_server.Base, "/elsewhere"), Path.Combine(_dir, zipName), 0, null, CancellationToken.None));
        Assert.StartsWith("refused redirect", e.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, zipName)));

        // a release list that points at another host: the download is refused before it starts
        var github = new UpdateClient(UpdateSource.GitHub, "t/1");
        using (github)
        {
            var refused = await Assert.ThrowsAsync<UpdateRefusedException>(() => github.DownloadFileAsync(new Uri("https://example.invalid/x.zip"), Path.Combine(_dir, "x.zip"), 0, null, CancellationToken.None));
            Assert.Contains("not a GitHub address", refused.Message, StringComparison.Ordinal);
        }

        // too many redirects
        _server.Map("/loop", FakeResponse.Redirect(new Uri(_server.Base, "/loop").AbsoluteUri));
        Assert.Contains("too many redirects", (await Assert.ThrowsAsync<UpdateRefusedException>(() => client.DownloadTextAsync(new Uri(_server.Base, "/loop"), 1000, CancellationToken.None))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReleaseIsDownloadedThroughTheRedirectCheckedUnpackedAndChecked()
    {
        Publish();
        using UpdateClient client = Client();
        ReleaseInfo release = (await client.CheckAsync(Older, CancellationToken.None)).Update!;
        var staging = new UpdateStaging(Path.Combine(_dir, "app"));
        Directory.CreateDirectory(staging.AppDirectory);
        var steps = new List<UpdateProgress>();
        StagedRelease staged = await Updater.PrepareAsync(client, release, staging, new SyncProgress(steps.Add), CancellationToken.None);
        Assert.Equal(Path.GetFullPath(staging.FilesDirectory), staged.Root);
        Assert.Equal("new", File.ReadAllText(Path.Combine(staged.Root, "marker.txt")));
        Assert.Contains(steps, s => s.Step == UpdateStep.Downloading && s.Download.Received == release.Zip.Size && s.Download.Total == release.Zip.Size);
        Assert.Contains(steps, s => s.Step == UpdateStep.Verifying);
        Assert.Contains(steps, s => s.Step == UpdateStep.Unpacking);
        // the files came through GitHub's download path and its redirect to the file host
        Assert.Contains(_server.Requests, r => r.Path == $"/{Repo}/releases/download/{_version.Tag}/{release.Zip.Name}");
        Assert.Contains(_server.Requests, r => r.Path == $"/assets/{_version.Tag}/{release.Zip.Name}");
        Assert.Contains(_server.Requests, r => r.Path == $"/assets/{_version.Tag}/{UpdateSource.SumsAssetName}");
        Assert.Equal(ReleaseSums.Sha256Hex(Path.Combine(_dir, release.Zip.Name)), ReleaseSums.Sha256Hex(Path.Combine(staging.DownloadDirectory, release.Zip.Name)));
    }

    [Fact]
    public async Task AZipThatDoesNotMatchItsChecksumIsRefusedAndNothingIsLeft()
    {
        string zipName = UpdateSource.ZipAssetName(_version);
        Publish(sumsText: new string('0', 64) + "  " + zipName + "\n");
        using UpdateClient client = Client();
        ReleaseInfo release = (await client.CheckAsync(Older, CancellationToken.None)).Update!;
        var staging = new UpdateStaging(Path.Combine(_dir, "app"));
        Directory.CreateDirectory(staging.AppDirectory);
        var e = await Assert.ThrowsAsync<UpdateRefusedException>(() => Updater.PrepareAsync(client, release, staging, null, CancellationToken.None));
        Assert.Contains("does not match its checksum", e.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(staging.Root));
        Assert.Empty(Directory.GetFileSystemEntries(staging.AppDirectory));
    }

    [Fact]
    public async Task AChecksumFileWithoutTheZipIsRefusedBeforeTheZipIsDownloaded()
    {
        Publish(sumsText: new string('0', 64) + "  Tsukihime-v9.9.9-win-x64.zip\n");
        using UpdateClient client = Client();
        ReleaseInfo release = (await client.CheckAsync(Older, CancellationToken.None)).Update!;
        var staging = new UpdateStaging(Path.Combine(_dir, "app"));
        Directory.CreateDirectory(staging.AppDirectory);
        var e = await Assert.ThrowsAsync<UpdateRefusedException>(() => Updater.PrepareAsync(client, release, staging, null, CancellationToken.None));
        Assert.Contains("has no checksum for", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_server.Requests, r => r.Path.EndsWith(".zip", StringComparison.Ordinal));
        Assert.False(Directory.Exists(staging.Root));
    }

    [Fact]
    public async Task AZipLargerThanGitHubSaidIsRefused()
    {
        string zipName = UpdateSource.ZipAssetName(_version);
        string zip = ReleaseFiles.Zip(ReleaseFiles.Folder(_dir, "big", "x"), Path.Combine(_dir, zipName));
        string sums = ReleaseSums_(zip);
        _server.List(Repo, _server.Release(Repo, _version.Tag, [(zipName, zip), (UpdateSource.SumsAssetName, sums)], sizeOverride: 100));
        using UpdateClient client = Client();
        ReleaseInfo release = (await client.CheckAsync(Older, CancellationToken.None)).Update!;
        Assert.Equal(100, release.Zip.Size);
        string target = Path.Combine(_dir, "download.zip");
        var e = await Assert.ThrowsAsync<UpdateRefusedException>(() => client.DownloadFileAsync(release.Zip.Url, target, release.Zip.Size, null, CancellationToken.None));
        Assert.Contains("larger than expected", e.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task CancelStopsTheDownloadAndRemovesThePartialFile()
    {
        // a large body, so the download is still running when Cancel comes
        string big = Path.Combine(_dir, "big.bin");
        using (var f = new FileStream(big, FileMode.Create))
        {
            f.SetLength(64L * 1024 * 1024);
        }

        _server.Map("/big", () => new FakeResponse(200, File: big));
        using UpdateClient client = Client();
        using var cancel = new CancellationTokenSource();
        string target = Path.Combine(_dir, "partial.bin");
        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            if (p.Received > 1024 * 1024)
            {
                cancel.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadFileAsync(new Uri(_server.Base, "/big"), target, 0, progress, cancel.Token));
        Assert.False(File.Exists(target));
    }

    private string ReleaseSums_(string zip) => ReleaseFiles.Sums(Path.Combine(_dir, UpdateSource.SumsAssetName), zip);

    /// <summary>An IProgress that runs its callback at once (Progress&lt;T&gt; posts to the thread pool, too late for the assertions).</summary>
    private sealed class SyncProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => report(value);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
