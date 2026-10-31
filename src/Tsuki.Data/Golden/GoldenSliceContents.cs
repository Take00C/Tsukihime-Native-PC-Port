#if TSUKI_DEV
using System.Collections.Immutable;
using System.Globalization;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Golden;

/// <summary>A picture of the slice: image archive id (0 allpac0 .. 5 allpac4) and entry index.</summary>
/// <param name="Archive">Archive id.</param>
/// <param name="Entry">Entry index in the archive.</param>
public readonly record struct GoldenPictureKey(int Archive, int Entry)
{
    /// <inheritdoc />
    public override string ToString() => $"{GoldenNames.Archive(Archive)}#{Entry}";
}

/// <summary>An audio entry of the slice.</summary>
/// <param name="Bank">The bank.</param>
/// <param name="Index">Entry index in the bank.</param>
public readonly record struct GoldenAudioKey(AudioBank Bank, int Index)
{
    /// <inheritdoc />
    public override string ToString() => $"{GoldenNames.Bank(Bank)}#{Index}";
}

/// <summary>
/// What the golden slice holds, found from the file names of the manifest (<c>scripts/script_0003.json</c>,
/// <c>pictures/allpac0_00270.json</c>, <c>audio/voice_15616.json</c>, <c>fonts/ft_ja.json</c>).
/// </summary>
public sealed class GoldenSliceContents
{
    private readonly HashSet<int> _scripts;
    private readonly HashSet<GoldenPictureKey> _pictures;
    private readonly HashSet<GoldenAudioKey> _audio;

    private GoldenSliceContents(
        ImmutableArray<int> scripts, ImmutableArray<GoldenPictureKey> pictures, ImmutableArray<GoldenAudioKey> audio, ImmutableArray<Language> fonts)
    {
        Scripts = scripts;
        Pictures = pictures;
        Audio = audio;
        FontLanguages = fonts;
        _scripts = [.. scripts];
        _pictures = [.. pictures];
        _audio = [.. audio];
    }

    /// <summary>Script numbers present, ascending (2 and 3).</summary>
    public ImmutableArray<int> Scripts { get; }

    /// <summary>Pictures present, ascending by archive and entry (18 of allpac0, 4 of allpac3).</summary>
    public ImmutableArray<GoldenPictureKey> Pictures { get; }

    /// <summary>Audio entries present (9 voices, 1 sound effect, 1 BGM track).</summary>
    public ImmutableArray<GoldenAudioKey> Audio { get; }

    /// <summary>Languages with a font (ja, en).</summary>
    public ImmutableArray<Language> FontLanguages { get; }

    /// <summary>True if script <paramref name="k"/> is in the slice.</summary>
    public bool HasScript(int k) => _scripts.Contains(k);

    /// <summary>True if the picture is in the slice.</summary>
    public bool HasPicture(int archive, int entry) => _pictures.Contains(new GoldenPictureKey(archive, entry));

    /// <summary>True if the audio entry is in the slice.</summary>
    public bool HasAudio(AudioBank bank, int index) => _audio.Contains(new GoldenAudioKey(bank, index));

    /// <summary>True if the font of <paramref name="lang"/> is in the slice.</summary>
    public bool HasFont(Language lang) => FontLanguages.Contains(lang);

    internal string DescribePictures() => "pictures " + string.Join(", ", Pictures);

    internal string DescribeAudio() => "audio " + string.Join(", ", Audio);

    internal static GoldenSliceContents Scan(GoldenManifest manifest)
    {
        var scripts = new List<int>();
        var pictures = new List<GoldenPictureKey>();
        var audio = new List<GoldenAudioKey>();
        var fonts = new List<Language>();
        foreach (GoldenFileRecord f in manifest.Files)
        {
            string p = f.Path;
            if (!p.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            string stem = p[..^".json".Length];
            if (TryAfter(stem, "scripts/script_", out string rest) && int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int k))
            {
                scripts.Add(k);
            }
            else if (TryAfter(stem, "pictures/", out rest) && SplitEntry(rest, out string archive, out int entry) && GoldenNames.ArchiveId(archive) is var id and >= 0)
            {
                pictures.Add(new GoldenPictureKey(id, entry));
            }
            else if (TryAfter(stem, "audio/", out rest) && SplitEntry(rest, out string bank, out int index) && GoldenNames.BankOf(bank) is { } b)
            {
                audio.Add(new GoldenAudioKey(b, index));
            }
            else if (TryAfter(stem, "fonts/ft_", out rest) && GoldenNames.LanguageOf(rest) is { } lang)
            {
                fonts.Add(lang);
            }
        }

        scripts.Sort();
        pictures.Sort((a, b) => a.Archive != b.Archive ? a.Archive.CompareTo(b.Archive) : a.Entry.CompareTo(b.Entry));
        audio.Sort((a, b) => a.Bank != b.Bank ? a.Bank.CompareTo(b.Bank) : a.Index.CompareTo(b.Index));
        fonts.Sort();
        return new GoldenSliceContents([.. scripts], [.. pictures], [.. audio], [.. fonts]);
    }

    private static bool TryAfter(string text, string prefix, out string rest)
    {
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            rest = text[prefix.Length..];
            return true;
        }

        rest = string.Empty;
        return false;
    }

    // "allpac1_2_00123" -> ("allpac1_2", 123); the entry number is the last five digits after the last underscore
    private static bool SplitEntry(string name, out string owner, out int entry)
    {
        owner = string.Empty;
        entry = 0;
        if (name.Length < 7 || name[^6] != '_')
        {
            return false;
        }

        owner = name[..^6];
        return int.TryParse(name[^5..], NumberStyles.None, CultureInfo.InvariantCulture, out entry);
    }
}
#endif
