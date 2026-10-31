using System.Globalization;
using Tsuki.Data.Eboot;

namespace Tsuki.Data.Catalog;

/// <summary>
/// How a script image id (<c>img2511</c>, <c>WHITE</c>, <c>boy_a_00_00_n01</c>) becomes a name-list key: VA 0x78410
/// (upper-case flag set at every call site) and the STCH / SCH2 variant suffix of VA 0x81760
/// (docs/formats/archives_ui_movies.md 2.4). A port of <c>script_image_key</c> and <c>stch_image_name</c>.
/// </summary>
public static class ScriptImageNames
{
    /// <summary>The key copies at most 32 bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>
    /// The name-list key of a script id: at most 32 bytes, stopping at a NUL or a '.', bytes at least 0x80 unchanged, 'a'..'z'
    /// upper-cased. One character per byte.
    /// </summary>
    public static string Key(ReadOnlySpan<byte> name)
    {
        var chars = new List<char>(KeyLength);
        foreach (byte ch in name[..Math.Min(KeyLength, name.Length)])
        {
            if (ch == 0 || ch == 0x2E)
            {
                break;
            }

            chars.Add((char)(ch is >= 0x61 and <= 0x7A ? ch - 0x20 : ch));
        }

        return new string([.. chars]);
    }

    /// <summary>The key of an ASCII id.</summary>
    public static string Key(string name) => Key(ImageNameIndex.Ascii(name));

    /// <summary>
    /// The name STCH / SCH2 look up for the arguments a9 = <paramref name="aa"/> and a10 = <paramref name="bb"/>: when either is
    /// non-zero the suffix <c>_%02d_%02d</c> is appended, with <c>v % 100</c> for a positive value and <c>00</c> otherwise.
    /// </summary>
    public static string StchName(string baseName, int aa, int bb) =>
        (aa | bb) != 0 ? $"{baseName}_{TwoDigits(aa)}_{TwoDigits(bb)}" : baseName;

    /// <summary>The same for a name in bytes.</summary>
    public static byte[] StchName(ReadOnlySpan<byte> baseName, int aa, int bb) =>
        (aa | bb) != 0 ? [.. baseName, .. ImageNameIndex.Ascii($"_{TwoDigits(aa)}_{TwoDigits(bb)}")] : baseName.ToArray();

    private static string TwoDigits(int v) =>
        v > 0 ? (v % 100).ToString("D2", CultureInfo.InvariantCulture) : "00";
}

/// <summary>What the engine draws for one script image id.</summary>
/// <param name="Key">The name-list key (<see cref="ScriptImageNames.Key(ReadOnlySpan{byte})"/>).</param>
/// <param name="Index">Image-table / name-list index k; -1 when the name is unknown.</param>
/// <param name="File">Record file, drawn first.</param>
/// <param name="Overlay">Record overlay, drawn over the file (paired sprites only).</param>
/// <param name="FileId">The file found by the NAM search; null when missing (3 retail bodies).</param>
/// <param name="OverlayId">The overlay found by the NAM search.</param>
/// <param name="ReducedId">The allpac2 copy (<c>0x30000 | k</c>): the thumbnail path.</param>
public sealed record ResolvedImage(
    string Key, int Index, string? File = null, string? Overlay = null, ImageId? FileId = null, ImageId? OverlayId = null,
    ImageId? ReducedId = null)
{
    /// <summary>True when the id is in the name list.</summary>
    public bool Found => Index >= 0;
}

/// <summary>
/// The engine-faithful resolution of script image ids: key, then position in the embedded name list (the first of equal
/// names), then the image-table record, then its file and overlay through the NAM search. A port of
/// <c>tsuki.catalog.ScriptImageResolver</c>.
/// </summary>
public sealed class ScriptImageResolver
{
    private readonly Dictionary<string, int> _map = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<CatalogEntry> _table;
    private readonly ImageNameIndex _index;

    /// <summary>Creates a resolver.</summary>
    /// <param name="names">The name list of data VA 0x7F6800 (<see cref="PictureNameLists.Read"/>).</param>
    /// <param name="table">The image table records.</param>
    /// <param name="index">The NAM search over allpac4, allpac3, allpac1_2, allpac1 and allpac0.</param>
    public ScriptImageResolver(IReadOnlyList<string> names, IReadOnlyList<CatalogEntry> table, ImageNameIndex index)
    {
        for (int k = 0; k < names.Count; k++)
        {
            _map.TryAdd(names[k], k); // a std::map insert keeps the first key
        }

        _table = table;
        _index = index;
    }

    /// <summary>Position of the id in the name list, or -1.</summary>
    public int IndexOf(ReadOnlySpan<byte> name) => _map.GetValueOrDefault(ScriptImageNames.Key(name), -1);

    /// <summary>Position of an ASCII id in the name list, or -1.</summary>
    public int IndexOf(string name) => IndexOf(ImageNameIndex.Ascii(name));

    /// <summary>
    /// Resolves <paramref name="name"/> as STCH / SCH2 would with a9 = <paramref name="aa"/>, a10 = <paramref name="bb"/> (pass 0, 0 for
    /// SPFC / SPRD / STCG and for a plain STCH).
    /// </summary>
    public ResolvedImage Resolve(ReadOnlySpan<byte> name, int aa = 0, int bb = 0)
    {
        string key = ScriptImageNames.Key(ScriptImageNames.StchName(name, aa, bb));
        int k = _map.GetValueOrDefault(key, -1);
        if (k < 0 || k >= _table.Count)
        {
            return new ResolvedImage(key, -1);
        }

        var r = _table[k];
        return new ResolvedImage(
            key, k, r.Name0, r.Name1, _index.Find(r.Name0), r.Name1 is null ? null : _index.Find(r.Name1),
            new ImageId(ImageArchive.Allpac2, k));
    }

    /// <summary>Resolves an ASCII id.</summary>
    public ResolvedImage Resolve(string name, int aa = 0, int bb = 0) => Resolve(ImageNameIndex.Ascii(name), aa, bb);
}
