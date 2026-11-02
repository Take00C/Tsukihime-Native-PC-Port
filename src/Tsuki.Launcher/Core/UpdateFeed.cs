// The release list of GitHub's REST API as the update check reads it (package K40): GET /repos/{owner}/{repo}/releases answers a JSON array of releases, newest first,
// each with tag_name, name, body (Markdown), draft, prerelease, html_url and assets (name, browser_download_url, size, state). Drafts are skipped; pre-releases are
// not (every release of the port so far is marked pre-release). A release counts when its tag is a plain vX.Y.Z and it carries both files the launcher needs: the
// zip named after the tag and SHA256SUMS.txt.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tsuki.Launcher.Core;

/// <summary>A file attached to a release.</summary>
/// <param name="Name">Its file name.</param>
/// <param name="Url">browser_download_url (github.com/.../releases/download/...; GitHub redirects it to its file host).</param>
/// <param name="Size">Its size in bytes as GitHub reports it (0 when unknown).</param>
public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <summary>A release that an update can install.</summary>
/// <param name="Version">From the tag.</param>
/// <param name="Tag">The tag as GitHub has it ("v0.1.4").</param>
/// <param name="Title">The release's title (the tag when it has none).</param>
/// <param name="Notes">The release notes as plain text (<see cref="UpdateFeed.PlainText"/>).</param>
/// <param name="PageUrl">The release's page on GitHub (html_url), or null.</param>
/// <param name="Prerelease">GitHub's pre-release flag.</param>
/// <param name="Zip">The zip (<see cref="UpdateSource.ZipAssetName"/>).</param>
/// <param name="Sums">SHA256SUMS.txt.</param>
public sealed record ReleaseInfo(ReleaseVersion Version, string Tag, string Title, string Notes, string? PageUrl, bool Prerelease, ReleaseAsset Zip, ReleaseAsset Sums);

/// <summary>One entry of the release list, before the choice (what the log says about each).</summary>
/// <param name="Tag">tag_name.</param>
/// <param name="Release">The release when it is installable, else null.</param>
/// <param name="Skipped">Why it is not, else null.</param>
public sealed record FeedEntry(string Tag, ReleaseInfo? Release, string? Skipped);

/// <summary>Reads the release list and picks the update.</summary>
public static partial class UpdateFeed
{
    /// <summary>The longest release notes shown (a release body may hold up to 125,000 characters on GitHub).</summary>
    public const int MaxNotesLength = 20_000;

    /// <summary>Reads the JSON array; every release gets an entry, installable or with the reason it is skipped. Throws <see cref="JsonException"/> on text that is not a release list.</summary>
    public static IReadOnlyList<FeedEntry> Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json ?? string.Empty, new JsonDocumentOptions { MaxDepth = 32 });
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("the answer is not a list of releases");
        }

        var list = new List<FeedEntry>();
        foreach (JsonElement r in doc.RootElement.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string tag = String(r, "tag_name") ?? string.Empty;
            list.Add(Read(r, tag));
        }

        return list;
    }

    private static FeedEntry Read(JsonElement r, string tag)
    {
        if (Bool(r, "draft"))
        {
            return new FeedEntry(tag, null, "a draft");
        }

        if (!ReleaseVersion.TryParseTag(tag, out ReleaseVersion version))
        {
            return new FeedEntry(tag, null, "the tag is not vX.Y.Z");
        }

        string zipName = UpdateSource.ZipAssetName(version);
        ReleaseAsset? zip = null;
        ReleaseAsset? sums = null;
        if (r.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement a in assets.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object || String(a, "name") is not { } name || String(a, "browser_download_url") is not { } url
                    || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (String(a, "state") is { } state && state != "uploaded"))
                {
                    continue;
                }

                long size = a.TryGetProperty("size", out JsonElement s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long n) ? n : 0;
                if (string.Equals(name, zipName, StringComparison.Ordinal))
                {
                    zip ??= new ReleaseAsset(name, uri, size);
                }
                else if (string.Equals(name, UpdateSource.SumsAssetName, StringComparison.Ordinal))
                {
                    sums ??= new ReleaseAsset(name, uri, size);
                }
            }
        }

        if (zip is null)
        {
            return new FeedEntry(tag, null, $"no {zipName} attached");
        }

        if (sums is null)
        {
            return new FeedEntry(tag, null, $"no {UpdateSource.SumsAssetName} attached");
        }

        string title = String(r, "name") is { Length: > 0 } t ? t.Trim() : tag;
        string? page = ReleasePage(String(r, "html_url"));
        var release = new ReleaseInfo(version, tag, title, PlainText(String(r, "body")), page, Bool(r, "prerelease"), zip, sums);
        return new FeedEntry(tag, release, null);
    }

    /// <summary>
    /// The release's html_url when it is an https page on github.com, else null (then "Open the release page" opens the repository's Releases page). The overlay
    /// hands this address to the shell, which would start a program for a file:, UNC or other-scheme address, so nothing else from the answer is opened (review K41).
    /// </summary>
    public static string? ReleasePage(string? htmlUrl) =>
        Uri.TryCreate(htmlUrl, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
            && string.Equals(uri.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;

    /// <summary>The highest installable release newer than <paramref name="current"/>, or null.</summary>
    public static ReleaseInfo? Pick(IEnumerable<FeedEntry> entries, ReleaseVersion current)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ReleaseInfo? best = null;
        foreach (FeedEntry e in entries)
        {
            if (e.Release is { } r && r.Version > current && (best is null || r.Version > best.Version))
            {
                best = r;
            }
        }

        return best;
    }

    /// <summary>
    /// The release notes as plain text: GitHub's Markdown without its marks (headings, emphasis, code ticks, link targets, HTML tags and comments), list items as
    /// bullets, at most <see cref="MaxNotesLength"/> characters. Empty notes say so.
    /// </summary>
    public static string PlainText(string? markdown)
    {
        string text = (markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = HtmlComment().Replace(text, string.Empty);
        text = HtmlTag().Replace(text, string.Empty);
        var lines = new List<string>();
        bool fence = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fence = !fence;
                continue;
            }

            if (!fence && HorizontalRule().IsMatch(line))
            {
                line = string.Empty;
            }
            else if (!fence)
            {
                line = Heading().Replace(line, string.Empty);
                line = Quote().Replace(line, string.Empty);
                line = Bullet().Replace(line, "$1•  ");
                line = Image().Replace(line, "$1");
                line = Link().Replace(line, "$1");
                line = line.Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
                line = line.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
            }

            lines.Add(line);
        }

        var result = new StringBuilder();
        int blank = 0;
        foreach (string line in lines)
        {
            if (line.Trim().Length == 0)
            {
                blank++;
                continue;
            }

            if (result.Length > 0)
            {
                result.Append(blank > 0 ? "\n\n" : "\n");
            }

            blank = 0;
            result.Append(line);
        }

        string plain = result.ToString().Trim();
        if (plain.Length == 0)
        {
            return "This release has no notes.";
        }

        return plain.Length <= MaxNotesLength ? plain : plain[..MaxNotesLength].TrimEnd() + "\n\n(The notes go on: the release page has the rest.)";
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"</?(?:a|b|i|u|s|em|strong|br|p|div|span|img|details|summary|sub|sup|kbd|code|pre|ul|ol|li|h[1-6]|hr|table|thead|tbody|tr|td|th|picture|source|video)\b[^<>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s{0,3}>\s?", RegexOptions.CultureInvariant)]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(\s*)[-*+]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalRule();
}
