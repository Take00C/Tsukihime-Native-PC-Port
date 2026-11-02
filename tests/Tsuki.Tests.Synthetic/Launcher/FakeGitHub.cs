using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>A request the fake server received.</summary>
/// <param name="Path">The path and query.</param>
/// <param name="Headers">The headers, keys lower-case.</param>
internal sealed record FakeRequest(string Path, IReadOnlyDictionary<string, string> Headers);

/// <summary>What the fake server answers.</summary>
/// <param name="Status">The status code.</param>
/// <param name="Body">The body (null: none).</param>
/// <param name="File">A file to send as the body instead.</param>
/// <param name="Location">A redirect target.</param>
/// <param name="ContentType">The media type.</param>
internal sealed record FakeResponse(int Status, byte[]? Body = null, string? File = null, string? Location = null, string ContentType = "application/octet-stream")
{
    public static FakeResponse Json(string json) => new(200, Encoding.UTF8.GetBytes(json), ContentType: "application/json; charset=utf-8");

    public static FakeResponse Redirect(string location) => new(302, Location: location);

    public static FakeResponse Text(string text) => new(200, Encoding.UTF8.GetBytes(text), ContentType: "text/plain");
}

/// <summary>
/// Package K40: a stand-in for GitHub on the loopback address (a TcpListener on 127.0.0.1 with a small HTTP/1.1 answer, one request per connection). It serves what the
/// test maps: the release list of /repos/{owner}/{name}/releases, the release files under /{owner}/{name}/releases/download/..., redirected to /assets/... like
/// GitHub's file host. No test talks to GitHub itself.
/// </summary>
internal sealed class FakeGitHub : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Func<FakeResponse>> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<Task> _serving = [];
    private readonly Task _loop;

    public FakeGitHub()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(LoopAsync);
    }

    public int Port { get; }

    /// <summary>http://127.0.0.1:PORT/.</summary>
    public Uri Base => new($"http://127.0.0.1:{Port}/");

    public ConcurrentQueue<FakeRequest> Requests { get; } = new();

    public void Map(string pathAndQuery, Func<FakeResponse> answer) => _routes[pathAndQuery] = answer;

    public void Map(string pathAndQuery, FakeResponse answer) => _routes[pathAndQuery] = () => answer;

    /// <summary>
    /// Serves a release the way GitHub does: the list at /repos/{repo}/releases?per_page=20 and each file at /{repo}/releases/download/{tag}/{name}, which redirects
    /// to /assets/{tag}/{name}. Returns the JSON entry for the list.
    /// </summary>
    public object Release(string repo, string tag, IReadOnlyList<(string Name, string Path)> files, bool prerelease = true, bool draft = false, string? body = null, long? sizeOverride = null)
    {
        var assets = new List<object>();
        foreach ((string name, string path) in files)
        {
            string download = $"/{repo}/releases/download/{tag}/{name}";
            string asset = $"/assets/{tag}/{name}";
            Map(download, FakeResponse.Redirect(new Uri(Base, asset).AbsoluteUri));
            Map(asset, () => new FakeResponse(200, File: path));
            assets.Add(new { name, browser_download_url = new Uri(Base, download).AbsoluteUri, size = sizeOverride ?? new FileInfo(path).Length, state = "uploaded" });
        }

        return new { tag_name = tag, name = $"Tsukihime PC port {tag}", body = body ?? $"## Fixed\n\n- **Something** in {tag}.", draft, prerelease, html_url = $"https://github.com/{repo}/releases/tag/{tag}", assets };
    }

    public void List(string repo, params object[] releases) =>
        Map($"/repos/{repo}/releases?per_page=20", FakeResponse.Json(JsonSerializer.Serialize(releases)));

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _serving.Add(Task.Run(() => ServeAsync(client)));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && head.Length < 16384)
                {
                    if (await stream.ReadAsync(one, _stop.Token) == 0)
                    {
                        return;
                    }

                    head.Append((char)one[0]);
                }

                string[] lines = head.ToString().Split("\r\n");
                string[] request = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon > 0)
                    {
                        headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
                    }
                }

                string path = request.Length > 1 ? request[1] : "/";
                Requests.Enqueue(new FakeRequest(path, headers));
                FakeResponse answer = _routes.TryGetValue(path, out Func<FakeResponse>? route) ? route() : new FakeResponse(404, Encoding.UTF8.GetBytes("not found"));
                long length = answer.File is not null ? new FileInfo(answer.File).Length : answer.Body?.Length ?? 0;
                var reply = new StringBuilder();
                reply.Append($"HTTP/1.1 {answer.Status} {(answer.Status == 200 ? "OK" : answer.Status == 302 ? "Found" : "Not Found")}\r\n");
                reply.Append($"Content-Type: {answer.ContentType}\r\nContent-Length: {length}\r\nConnection: close\r\n");
                if (answer.Location is not null)
                {
                    reply.Append($"Location: {answer.Location}\r\n");
                }

                reply.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply.ToString()), _stop.Token);
                if (answer.File is not null)
                {
                    await using FileStream file = File.OpenRead(answer.File);
                    await file.CopyToAsync(stream, _stop.Token);
                }
                else if (answer.Body is not null)
                {
                    await stream.WriteAsync(answer.Body, _stop.Token);
                }

                await stream.FlushAsync(_stop.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // the client went away (a cancelled download)
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));

            // review K41: an answer still being sent (a cancelled download's file) keeps its file open; the test deletes its folder right after this
            Task.WaitAll([.. _serving], TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}

/// <summary>Release folders and zips for the update tests.</summary>
internal static class ReleaseFiles
{
    /// <summary>
    /// A folder that passes the update's check: TsukihimeLauncher.exe and Tsukihime.exe (made-up bytes; nothing runs them), TsukihimeLauncher.dll (a copy of the real
    /// Tsukihime.dll beside the tests, so its version is the build's), a licence file in a sub-folder and a marker.
    /// </summary>
    public static string Folder(string root, string name, string marker)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(folder, "licenses"));
        File.WriteAllText(Path.Combine(folder, "TsukihimeLauncher.exe"), "launcher " + marker);
        File.WriteAllText(Path.Combine(folder, "Tsukihime.exe"), "game " + marker);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tsukihime.dll"), Path.Combine(folder, "TsukihimeLauncher.dll"));
        File.WriteAllText(Path.Combine(folder, "licenses", "LICENSE.txt"), "licence " + marker);
        File.WriteAllText(Path.Combine(folder, "marker.txt"), marker);
        return folder;
    }

    /// <summary>The version of the real Tsukihime.dll beside the tests (the version a zip made by <see cref="Folder"/> carries).</summary>
    public static Tsuki.Launcher.Core.ReleaseVersion BuildVersion()
    {
        string? info = Tsuki.Data.Icons.PeIcons.Marks(Path.Combine(AppContext.BaseDirectory, "Tsukihime.dll"))?.InformationalVersion;
        Assert.True(Tsuki.Launcher.Core.ReleaseVersion.TryParseInformational(info, out Tsuki.Launcher.Core.ReleaseVersion v), info);
        return v;
    }

    /// <summary>Zips the contents of <paramref name="folder"/> (entries relative to it, forward slashes).</summary>
    public static string Zip(string folder, string zipPath)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
        return zipPath;
    }

    /// <summary>SHA256SUMS.txt for the files.</summary>
    public static string Sums(string path, params string[] files)
    {
        var lines = files.Select(f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))) + "  " + Path.GetFileName(f));
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    /// <summary>A zip with the given raw entry names and contents (for the zip-slip cases).</summary>
    public static string RawZip(string zipPath, params (string Name, string Content)[] entries)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return zipPath;
    }
}
