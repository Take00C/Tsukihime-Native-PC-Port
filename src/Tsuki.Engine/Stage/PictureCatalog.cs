namespace Tsuki.Engine.Stage;

/// <summary>The catalog fields of a picture that the layer model reads (docs/formats/images.md 8, layers.md 7.2).</summary>
/// <param name="AnchorX">Catalog anchor x (+0x34): the point of the picture that sits on the layer position.</param>
/// <param name="AnchorY">Catalog anchor y (+0x38).</param>
/// <param name="ScaleClass">Size class (+0x42): 0 A, 1 L, 2 M, 3 S.</param>
/// <param name="DrawFlags">Draw flags (+0x3D): bit 0 = no own colour effect, bit 1 = ignores the screen shock (the seven colour fills), bit 2 = animated.</param>
/// <param name="HasFace">The record has a face layer (catalog name1).</param>
public readonly record struct PictureInfo(int AnchorX, int AnchorY, int ScaleClass, int DrawFlags, bool HasFace)
{
    /// <summary>The factor of the size class: 1, 0.5, 0.25, 0.125 for A, L, M, S (VA 0x526A3, 0x865A2).</summary>
    public float ClassFactor => ScaleClass switch { 1 => 0.5f, 2 => 0.25f, 3 => 0.125f, _ => 1.0f };
}

/// <summary>
/// The picture catalog and the picture-name map as the layer model uses them (STCH name lookup VA 0x78410, the name list at data VA 0x7F6800,
/// the image catalog at 0x671900). The data layer implements it over <c>GameCatalog</c>; tests over arrays.
/// </summary>
public interface IPictureCatalog
{
    /// <summary>Number of catalog records (14,744 in v01.01).</summary>
    int Count { get; }

    /// <summary>The picture id of a script picture name (VA 0x78410: upper case up to the first '.', 32 bytes), or -1.</summary>
    int Lookup(string name);

    /// <summary>The name list entry of a picture, without padding; "" outside 0..Count-1.</summary>
    string Name(int pic);

    /// <summary>The catalog record of a picture in 0..Count-1.</summary>
    PictureInfo Info(int pic);
}

/// <summary>Helpers on <see cref="IPictureCatalog"/> that carry the engine's quirks.</summary>
public static class PictureNames
{
    /// <summary>
    /// The name the engine reads for a picture (VA 0x82BD0): the 32-byte entry, NUL-terminated at byte 32; names of 30 or more characters
    /// carry the CR LF of the list (<c>name.ljust(30) + "\r\n"</c>, cut to 32 bytes).
    /// </summary>
    public static string EngineName(IPictureCatalog c, int pic)
    {
        string n = c.Name(pic);
        if (n.Length < 30)
        {
            return n;
        }

        return (n.PadRight(30, '\0') + "\r\n")[..32];
    }

    /// <summary>The catalog anchor of a picture; the id -1 (or any id outside the table) reads the bytes before the table, whose low halves are (256, 255).</summary>
    public static (int X, int Y) CatalogAnchor(IPictureCatalog c, int pic)
    {
        if (pic >= 0 && pic < c.Count)
        {
            PictureInfo i = c.Info(pic);
            return (i.AnchorX, i.AnchorY);
        }

        return (256, 255);
    }

    /// <summary>The size class of a picture (VA 0x41BA0: catalog +0x42 for ids 0..0x39B6, else 0).</summary>
    public static int ScaleClass(IPictureCatalog c, int pic) => pic >= 0 && pic <= 0x39B6 && pic < c.Count ? c.Info(pic).ScaleClass : 0;

    /// <summary>
    /// The variant digits (hh, vv) of a <c>&lt;base&gt;_hh_vv</c> picture name (STCH 0x81900, motion 0xBEB9C): length of 7 or more, '_' at
    /// len-6 and len-3, prefix "IMG" (exact case as the oracle's motion path) or length of 15 or more; digits are read without validation.
    /// </summary>
    public static (int H, int V) VariantDigits(string n)
    {
        if (n.Length >= 7 && n[^6] == '_' && n[^3] == '_' && (n.StartsWith("IMG", StringComparison.Ordinal) || n.Length >= 15))
        {
            return (Digits2(n[^5], n[^4]), Digits2(n[^2], n[^1]));
        }

        return (0, 0);
    }

    /// <summary>The same test as STCH makes for the source layer's name (prefix compared case-insensitively).</summary>
    public static (int H, int V) VariantDigitsStch(string n)
    {
        if (n.Length >= 7 && n[^6] == '_' && n[^3] == '_' && (n.StartsWith("IMG", StringComparison.OrdinalIgnoreCase) || n.Length >= 15))
        {
            return (Digits2(n[^5], n[^4]), Digits2(n[^2], n[^1]));
        }

        return (0, 0);
    }

    /// <summary>Two name characters read as decimal digits without validation (VA 0x81900).</summary>
    public static int Digits2(char a, char b) => ((a - 48) * 10) + (b - 48);

    /// <summary>
    /// VA 0xBEE91..0xBF03F: the picture name for new digits (nh, nv). (0, 0) cuts the <c>_hh_vv</c> suffix off; a name without digits gets
    /// <c>_00_00</c> appended; then the units digits are written, and the tens digit only for values of 10 or more (a quirk: 12 to 5 gives 15).
    /// </summary>
    public static string VariantName(string name, int hh, int vv, int nh, int nv)
    {
        char[] b = name.ToCharArray();
        if ((nh | nv) == 0)
        {
            return new string(b, 0, Math.Max(b.Length - 6, 0));
        }

        if ((hh | vv) == 0)
        {
            b = [.. b, .. "_00_00"];
        }

        int n = b.Length;
        b[n - 4] = (char)(48 + (nh % 10));
        if (nh >= 10)
        {
            b[n - 5] = (char)(48 + (nh / 10 % 10));
        }

        b[n - 1] = (char)(48 + (nv % 10));
        if (nv >= 10)
        {
            b[n - 2] = (char)(48 + (nv / 10 % 10));
        }

        return new string(b);
    }
}

/// <summary>
/// An <see cref="IPictureCatalog"/> over arrays: the name list and the catalog records in id order (tests, golden data). The name lookup is
/// the oracle's: the upper-cased text up to the first '.', cut to 32 characters, in a dictionary where a later duplicate wins.
/// </summary>
public sealed class ArrayPictureCatalog : IPictureCatalog
{
    private readonly string[] _names;
    private readonly PictureInfo[] _info;
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    /// <summary>Creates the catalog.</summary>
    public ArrayPictureCatalog(IReadOnlyList<string> names, IReadOnlyList<PictureInfo> info)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(info);
        _names = [.. names];
        _info = [.. info];
        for (int i = 0; i < _names.Length; i++)
        {
            _index[_names[i].ToUpperInvariant()] = i;
        }
    }

    /// <inheritdoc/>
    public int Count => _info.Length;

    /// <inheritdoc/>
    public int Lookup(string name)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string key = (dot >= 0 ? name[..dot] : name);
        if (key.Length > 32)
        {
            key = key[..32];
        }

        return _index.TryGetValue(key.ToUpperInvariant(), out int id) ? id : -1;
    }

    /// <inheritdoc/>
    public string Name(int pic) => pic >= 0 && pic < _names.Length ? _names[pic] : string.Empty;

    /// <inheritdoc/>
    public PictureInfo Info(int pic) => _info[pic];
}
