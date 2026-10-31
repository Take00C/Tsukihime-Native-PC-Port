using Tsuki.Data.Containers;
using Tsuki.Data.Eboot;

namespace Tsuki.Data.Catalog;

/// <summary>
/// The engine's NAM file-name search (VA 0xC3270, docs/formats/archives_ui_movies.md 2.3): the key of a file name and the
/// search over the five name tables in the order allpac4, allpac3, allpac1_2, allpac1, allpac0. A port of
/// <c>tsuki.catalog.nam_key</c>, <c>find_image</c> and <c>ImageNameIndex</c>.
/// </summary>
public sealed class ImageNameIndex
{
    /// <summary>Compared characters of a name (0x1C), extension included.</summary>
    public const int KeyLength = 28;

    /// <summary>Records scanned per table (0x5014).</summary>
    public const int ScanLimit = 0x5014;

    private readonly Dictionary<ImageArchive, Dictionary<string, int>> _maps = [];
    private readonly IReadOnlyDictionary<ImageArchive, NamTable> _nams;

    /// <summary>The archives searched by name, in search order (allpac2 is never searched).</summary>
    public static IReadOnlyList<ImageArchive> SearchOrder { get; } =
        [ImageArchive.Allpac4, ImageArchive.Allpac3, ImageArchive.Allpac1_2, ImageArchive.Allpac1, ImageArchive.Allpac0];

    /// <summary>Builds the index from the name tables of the archives (missing archives are skipped).</summary>
    public ImageNameIndex(IReadOnlyDictionary<ImageArchive, NamTable> nams)
    {
        _nams = nams;
        foreach (var (arc, table) in nams)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < Math.Min(table.Count, ScanLimit); i++)
            {
                string name = table[i];
                if (name.Length == 0)
                {
                    break; // 'cmp byte [rcx], 0; je': the end of this table
                }

                map.TryAdd(Truncate(name), i);
            }

            _maps[arc] = map;
        }
    }

    /// <summary>
    /// The lookup key of <paramref name="name"/> exactly as VA 0xC3280..0xC32FE builds it: the key starts after the last
    /// backslash seen in the first 256 bytes before a NUL; 28 bytes are copied, 'a'..'z' become upper case unless the
    /// previous output byte is at least 0x80 (a Shift-JIS lead byte). Returned without padding: the bytes before the
    /// first NUL.
    /// </summary>
    public static byte[] Key(ReadOnlySpan<byte> name)
    {
        int nul = name.IndexOf((byte)0);
        var raw = nul >= 0 ? name[..nul] : name;
        int start = 0;
        for (int i = 0; i < Math.Min(256, raw.Length); i++)
        {
            if (raw[i] == 0x5C)
            {
                start = i + 1;
            }
        }

        var src = raw[start..];
        src = src[..Math.Min(KeyLength, src.Length)];
        byte[] output = new byte[src.Length];
        byte prev = 0;
        for (int i = 0; i < src.Length; i++)
        {
            byte ch = src[i];
            byte o = ch is >= 0x61 and <= 0x7A && prev < 0x80 ? (byte)(ch - 0x20) : ch;
            output[i] = o;
            prev = o;
        }

        return output;
    }

    /// <summary>The key of an ASCII name.</summary>
    /// <exception cref="ArgumentException">The name has a character above 0x7F: pass the bytes instead.</exception>
    public static byte[] Key(string name) => Key(Ascii(name));

    /// <summary>Packs an ASCII string into bytes.</summary>
    /// <exception cref="ArgumentException">A character above 0x7F.</exception>
    internal static byte[] Ascii(string name)
    {
        byte[] b = new byte[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] > 0x7F)
            {
                throw new ArgumentException("the name has a character above 0x7F; pass its bytes (Shift-JIS) instead", nameof(name));
            }

            b[i] = (byte)name[i];
        }

        return b;
    }

    /// <summary>
    /// The first match of <paramref name="name"/> (a file name with its extension) in the search order, or null.
    /// With <paramref name="allpac1Available"/> false allpac1 and allpac1_2 are skipped (the PlayGo chunks are not there).
    /// </summary>
    public ImageId? Find(ReadOnlySpan<byte> name, bool allpac1Available = true)
    {
        string key = Latin1(Key(name));
        foreach (var arc in SearchOrder)
        {
            if (!allpac1Available && arc is ImageArchive.Allpac1 or ImageArchive.Allpac1_2)
            {
                continue;
            }

            if (_maps.TryGetValue(arc, out var map) && map.TryGetValue(key, out int index))
            {
                return new ImageId(arc, index);
            }
        }

        return null;
    }

    /// <summary>The first match of an ASCII file name.</summary>
    public ImageId? Find(string name, bool allpac1Available = true) => Find(Ascii(name), allpac1Available);

    /// <summary>
    /// The engine's own linear scan (<c>find_image</c>): the same result as <see cref="Find(ReadOnlySpan{byte}, bool)"/>
    /// without the dictionaries.
    /// </summary>
    public ImageId? FindLinear(ReadOnlySpan<byte> name, bool allpac1Available = true)
    {
        string key = Latin1(Key(name));
        foreach (var arc in SearchOrder)
        {
            if (!allpac1Available && arc is ImageArchive.Allpac1 or ImageArchive.Allpac1_2)
            {
                continue;
            }

            if (!_nams.TryGetValue(arc, out var table))
            {
                continue;
            }

            for (int i = 0; i < Math.Min(table.Count, ScanLimit); i++)
            {
                string rec = table[i];
                if (rec.Length == 0)
                {
                    break;
                }

                if (string.Equals(Truncate(rec), key, StringComparison.Ordinal))
                {
                    return new ImageId(arc, i);
                }
            }
        }

        return null;
    }

    private static string Truncate(string name) => name.Length > KeyLength ? name[..KeyLength] : name;

    private static string Latin1(ReadOnlySpan<byte> bytes) => EbootReader.Latin1(bytes);
}
