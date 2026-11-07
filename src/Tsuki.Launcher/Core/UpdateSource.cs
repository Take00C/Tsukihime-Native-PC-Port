// Where the launcher looks for a newer release (package K40, GitHub issue #6 "Update button, pretty please?"). One constant names the repository, so a fork changes
// one line. The releases of the port are marked pre-release on GitHub, so GitHub's "latest release" endpoint (which skips pre-releases) is not used: the launcher reads
// the release list and picks the highest version itself (UpdateFeed).
//
// Every address the launcher fetches is checked by a HostPolicy first: HTTPS to github.com, api.github.com and *.githubusercontent.com only (a release download
// answers with a redirect to objects.githubusercontent.com or release-assets.githubusercontent.com; every hop is checked). The tests and the verification mode use a
// loopback policy instead, which admits plain HTTP to 127.0.0.1 / localhost / [::1] and nothing else, so no test ever talks to GitHub.
using System.Globalization;
using System.Net;

namespace Tsuki.Launcher.Core;

/// <summary>Which addresses an update may come from.</summary>
public sealed class HostPolicy
{
    private static readonly string[] GitHubHosts = ["github.com", "api.github.com"];
    private const string GitHubContentSuffix = ".githubusercontent.com";

    private readonly bool _loopback;

    private HostPolicy(bool loopback) => _loopback = loopback;

    /// <summary>The real one: HTTPS, default port, github.com, api.github.com or a host under githubusercontent.com.</summary>
    public static HostPolicy GitHub { get; } = new(loopback: false);

    /// <summary>Tests and the verification mode: http or https to the loopback address only (127.0.0.1, localhost, [::1]).</summary>
    public static HostPolicy Loopback { get; } = new(loopback: true);

    /// <summary>True for the loopback policy.</summary>
    public bool IsLoopback => _loopback;

    /// <summary>Null when <paramref name="uri"/> may be fetched, else why not (in words, for the log).</summary>
    public string? Problem(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return "not an absolute address";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return $"{uri.Host}: an address with a user name is not used";
        }

        string host = uri.IdnHost.ToLowerInvariant();
        if (_loopback)
        {
            return uri.Scheme is "http" or "https" && IsLoopbackHost(host) ? null : $"{uri.Scheme}://{host}: only the loopback address is allowed here";
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return $"{uri.Scheme}://{host}: only HTTPS is used";
        }

        if (!uri.IsDefaultPort)
        {
            return $"{host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}: only the default HTTPS port is used";
        }

        bool known = GitHubHosts.Contains(host, StringComparer.Ordinal)
            || (host.EndsWith(GitHubContentSuffix, StringComparison.Ordinal) && host.Length > GitHubContentSuffix.Length && !host.StartsWith('.'));
        return known ? null : $"{host} is not a GitHub address";
    }

    /// <summary>True when <paramref name="uri"/> may be fetched.</summary>
    public bool Allows(Uri? uri) => Problem(uri) is null;

    /// <summary>127.0.0.1 (any 127.x), localhost, ::1.</summary>
    public static bool IsLoopbackHost(string host)
    {
        string h = host.Trim('[', ']');
        return string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(h, out IPAddress? ip) && IPAddress.IsLoopback(ip));
    }
}

/// <summary>The repository the launcher updates from, and the names of the release files.</summary>
/// <param name="ApiBase">The GitHub REST API root (https://api.github.com; a loopback server in the tests).</param>
/// <param name="Repository">"owner/name".</param>
/// <param name="Policy">Which addresses may be fetched.</param>
public sealed record UpdateSource(Uri ApiBase, string Repository, HostPolicy Policy)
{
    /// <summary>The port's repository on GitHub. A fork changes this one line.</summary>
    public const string DefaultRepository = "Take00C/Tsukihime-Native-PC-Port";

    /// <summary>The checksum file every release attaches beside the zip: lines of "&lt;sha-256 hex&gt;  &lt;file name&gt;" (sha256sum's format).</summary>
    public const string SumsAssetName = "SHA256SUMS.txt";

    /// <summary>How many releases are read (the newest first).</summary>
    public const int ReleasesPerPage = 20;

    /// <summary>The real source: api.github.com and the port's repository.</summary>
    public static UpdateSource GitHub { get; } = new(new Uri("https://api.github.com/"), DefaultRepository, HostPolicy.GitHub);

    /// <summary>A loopback server standing in for GitHub (tests, the verification mode); refused for anything that is not the loopback address.</summary>
    public static UpdateSource ForLoopback(Uri apiBase, string repository = DefaultRepository)
    {
        ArgumentNullException.ThrowIfNull(apiBase);
        if (!HostPolicy.Loopback.Allows(apiBase))
        {
            throw new ArgumentException($"{apiBase} is not a loopback address", nameof(apiBase));
        }

        // the relative "repos/..." path resolves under the base only when the base ends with a slash
        Uri root = apiBase.AbsoluteUri.EndsWith('/') ? apiBase : new Uri(apiBase.AbsoluteUri + "/");
        return new UpdateSource(root, repository, HostPolicy.Loopback);
    }

    /// <summary>The release zip of a version: <c>Tsukihime-v0.1.4-win-x64.zip</c> (the name every release uses; docs/PUBLISHING.md).</summary>
    public static string ZipAssetName(ReleaseVersion version) => $"Tsukihime-{version.Tag}-win-x64.zip";

    /// <summary>GET this: the newest releases, drafts and pre-releases included (drafts are only listed for the owner and are skipped anyway).</summary>
    public Uri ReleasesUri => new(ApiBase, $"repos/{Repository}/releases?per_page={ReleasesPerPage.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The Releases page a player opens in the browser.</summary>
    public string ReleasesPage => $"https://github.com/{Repository}/releases";
}
