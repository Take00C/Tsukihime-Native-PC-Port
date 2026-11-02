// The update check's network side (package K40): one GET of the release list and the downloads of the two release files. No authentication; a User-Agent (GitHub
// requires one) and GitHub's JSON media type; a time limit on the list and a stall limit on downloads; redirects followed by hand (at most five) so that every hop is
// checked against the HostPolicy (a release file answers with a redirect from github.com to its file host). Nothing here blocks the window: everything is async.
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Tsuki.Launcher.Core;

/// <summary>The answer of a check.</summary>
/// <param name="Update">The release to offer, or null (none newer, or the check failed).</param>
/// <param name="Entries">Every release read, with the reason a release was skipped.</param>
/// <param name="Error">Why the check failed (offline, GitHub's answer), or null.</param>
public sealed record UpdateCheckResult(ReleaseInfo? Update, IReadOnlyList<FeedEntry> Entries, string? Error);

/// <summary>Bytes received of a download.</summary>
/// <param name="Received">So far.</param>
/// <param name="Total">The whole file, or 0 when unknown.</param>
public readonly record struct DownloadProgress(long Received, long Total)
{
    /// <summary>0..1, or -1 when the total is unknown.</summary>
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : -1;
}

/// <summary>An address, a redirect or an answer the update refuses.</summary>
public sealed class UpdateRefusedException(string message) : Exception(message)
{
}

/// <summary>Talks to GitHub (or the loopback stand-in of the tests).</summary>
public sealed class UpdateClient : IDisposable
{
    /// <summary>The time limit of the release list.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A download fails when no byte arrives for this long.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The largest release list read.</summary>
    public const int MaxListBytes = 8 * 1024 * 1024;

    /// <summary>The largest release zip downloaded (GitHub's own limit for a release file is 2 GiB).</summary>
    public const long MaxZipBytes = 2L * 1024 * 1024 * 1024;

    private const int MaxRedirects = 5;

    private readonly HttpClient _http;
    private readonly UpdateSource _source;

    /// <summary>Creates the client.</summary>
    /// <param name="source">Where to look.</param>
    /// <param name="userAgent">"TsukihimeLauncher/0.1.3".</param>
    /// <param name="handler">The HTTP handler (tests); by default one without automatic redirects.</param>
    public UpdateClient(UpdateSource source, string userAgent, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    /// <summary>The source.</summary>
    public UpdateSource Source => _source;

    /// <summary>Reads the release list and picks the highest installable release newer than <paramref name="current"/>. Never throws for network trouble: <see cref="UpdateCheckResult.Error"/> says it.</summary>
    public async Task<UpdateCheckResult> CheckAsync(ReleaseVersion current, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(CheckTimeout);
        try
        {
            using HttpResponseMessage response = await SendAsync(_source.ReleasesUri, "application/vnd.github+json", limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string why = response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429 && response.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string>? left) && left.FirstOrDefault() == "0"
                    ? "GitHub's limit of checks per hour was reached; the next start checks again"
                    : $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}";
                return new UpdateCheckResult(null, [], why);
            }

            string json = await ReadTextAsync(response, MaxListBytes, limit.Token).ConfigureAwait(false);
            IReadOnlyList<FeedEntry> entries = UpdateFeed.Parse(json);
            return new UpdateCheckResult(UpdateFeed.Pick(entries, current), entries, null);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new UpdateCheckResult(null, [], $"no answer within {CheckTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException or UpdateRefusedException or InvalidOperationException)
        {
            return new UpdateCheckResult(null, [], Describe(e));
        }
    }

    /// <summary>Downloads a small text file (SHA256SUMS.txt) of at most <paramref name="maxBytes"/>.</summary>
    public async Task<string> DownloadTextAsync(Uri uri, int maxBytes, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(CheckTimeout);
        using HttpResponseMessage response = await SendAsync(uri, "application/octet-stream", limit.Token).ConfigureAwait(false);
        EnsureOk(response, uri);
        return await ReadTextAsync(response, maxBytes, limit.Token).ConfigureAwait(false);
    }

    /// <summary>Downloads <paramref name="uri"/> into <paramref name="path"/> (replaced), reporting progress; a partial file is deleted when it fails or is cancelled.</summary>
    /// <param name="uri">The release file.</param>
    /// <param name="path">Where it goes.</param>
    /// <param name="expectedSize">The size GitHub reported (0: unknown); used for the progress when the answer has no length, and the file must not be larger.</param>
    /// <param name="progress">Told about every few hundred kilobytes.</param>
    /// <param name="cancel">The player's Cancel.</param>
    public async Task DownloadFileAsync(Uri uri, string path, long expectedSize, IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        bool done = false;
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            stall.CancelAfter(CheckTimeout);
            using HttpResponseMessage response = await SendAsync(uri, "application/octet-stream", stall.Token).ConfigureAwait(false);
            EnsureOk(response, uri);
            long total = response.Content.Headers.ContentLength ?? expectedSize;
            long limit = Math.Min(MaxZipBytes, expectedSize > 0 ? expectedSize : MaxZipBytes);
            if (total > limit)
            {
                throw new UpdateRefusedException($"the file is larger than expected ({total.ToString(CultureInfo.InvariantCulture)} bytes)");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            byte[] buffer = new byte[1 << 16];
            long received = 0;
            long reported = 0;
            progress?.Report(new DownloadProgress(0, total));
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                int n = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                received += n;
                if (received > limit)
                {
                    throw new UpdateRefusedException($"the file is larger than expected (more than {limit.ToString(CultureInfo.InvariantCulture)} bytes)");
                }

                await file.WriteAsync(buffer.AsMemory(0, n), stall.Token).ConfigureAwait(false);
                if (received - reported >= 256 * 1024)
                {
                    reported = received;
                    progress?.Report(new DownloadProgress(received, total));
                }
            }

            if (total > 0 && received != total)
            {
                throw new IOException($"the download ended after {received.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)} bytes");
            }

            progress?.Report(new DownloadProgress(received, total));
            done = true;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new IOException($"the download stalled (nothing for {StallTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s)");
        }
        finally
        {
            if (!done)
            {
                TryDelete(path);
            }
        }
    }

    /// <summary>Sends a GET and follows up to five redirects by hand, each one checked against the policy.</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri uri, string accept, CancellationToken cancel)
    {
        Uri current = uri;
        for (int hop = 0; ; hop++)
        {
            if (_source.Policy.Problem(current) is { } problem)
            {
                throw new UpdateRefusedException(hop == 0 ? $"refused address: {problem}" : $"refused redirect: {problem}");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (accept == "application/vnd.github+json")
            {
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }

            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                Uri? next = response.Headers.Location is { } location ? (location.IsAbsoluteUri ? location : new Uri(current, location)) : null;
                response.Dispose();
                if (next is null)
                {
                    throw new UpdateRefusedException("a redirect without a target");
                }

                if (hop >= MaxRedirects)
                {
                    throw new UpdateRefusedException("too many redirects");
                }

                current = next;
                continue;
            }

            return response;
        }
    }

    private static void EnsureOk(HttpResponseMessage response, Uri uri)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{Path.GetFileName(uri.AbsolutePath)}: GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        }
    }

    private static async Task<string> ReadTextAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancel)
    {
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
        {
            throw new UpdateRefusedException($"the answer is too large ({length.ToString(CultureInfo.InvariantCulture)} bytes)");
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int n;
        while ((n = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + n > maxBytes)
            {
                throw new UpdateRefusedException($"the answer is larger than {maxBytes.ToString(CultureInfo.InvariantCulture)} bytes");
            }

            memory.Write(buffer, 0, n);
        }

        return System.Text.Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
    }

    /// <summary>A failure in words for the log and the overlay.</summary>
    public static string Describe(Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e switch
        {
            HttpRequestException { InnerException: System.Net.Sockets.SocketException s } => $"no connection ({s.SocketErrorCode})",
            HttpRequestException h => h.Message,
            System.Text.Json.JsonException => "the answer was not a release list",
            _ => e.Message,
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the staging folder is removed as a whole later
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();
}
