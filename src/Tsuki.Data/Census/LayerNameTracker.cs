using System.Text;

namespace Tsuki.Data.Census;

/// <summary>
/// The picture a layer shows by name, following <c>_STCH</c> / <c>_SCH2</c> / <c>_STCC</c> in statement order (no control flow), and the
/// name <c>_STCH</c> / <c>_SCH2</c> look up (docs/formats/archives_ui_movies.md 2.4: backend VA 0x81760). A port of
/// <c>tsuki.script.picture_key</c>, <c>_variant_digits</c> and <c>LayerTracker</c>; it is an approximation of what the engine does at run
/// time, good enough to name every picture a script can show.
/// </summary>
internal sealed class LayerNameTracker
{
    /// <summary>Layers 0..39.</summary>
    public const int LayerCount = 40;

    private readonly Dictionary<int, string> _names = [];

    /// <summary>The key of the picture now on the layer named by argument 18 of the statement, if any.</summary>
    public string? SourceName(StatementView s)
    {
        if (s.Key is not ("STCH" or "SCH2") || s.Expr(18) is not { } src)
        {
            return null;
        }

        return _names.GetValueOrDefault(src);
    }

    /// <summary>
    /// The name an <c>_STCH</c> / <c>_SCH2</c> looks up, before the key rules: the picture id with the <c>_hh_vv</c> variant suffix, or
    /// null when the statement names no picture. <paramref name="aa"/> and <paramref name="bb"/> are the suffix numbers it applied.
    /// </summary>
    public static byte[]? PictureName(StatementView s, string? sourceName, out int aa, out int bb)
    {
        aa = bb = 0;
        ReadOnlySpan<byte> arg = s.ObjArg(1);
        if (arg.IsEmpty)
        {
            return null;
        }

        if (arg.Length > 255)
        {
            arg = arg[..255];
        }

        int h = 0;
        int v = 0;
        if (s.Expr(18) is { } src && src is >= 0 and < LayerCount && !string.IsNullOrEmpty(sourceName) && VariantDigits(sourceName) is { } dv)
        {
            (h, v) = dv;
        }

        h = s.Expr(9) ?? h;
        v = s.Expr(10) ?? v;
        aa = h;
        bb = v;
        return arg.ToArray();
    }

    /// <summary>Records what the statement does to the layers (call after the statement has been resolved).</summary>
    public void Update(StatementView s, string? pictureKey)
    {
        if (s.Key is "STCH" or "SCH2")
        {
            if (s.Expr(0) is { } layer && layer is >= 0 and < LayerCount && !string.IsNullOrEmpty(pictureKey))
            {
                _names[layer] = pictureKey;
            }
        }
        else if (s.Key == "STCC" && s.Expr(0) is { } l)
        {
            if (l == -1)
            {
                _names.Clear();
            }
            else
            {
                _names.Remove(l);
            }
        }
    }

    /// <summary>
    /// <c>(hh, vv)</c> parsed from a layer's current picture name <c>..._hh_vv</c> the way VA 0x81862..0x81935 does: the name needs at least
    /// seven characters, an underscore at length - 6 and length - 3 and either the prefix <c>IMG</c> or at least 15 characters; the four
    /// digit bytes are not validated.
    /// </summary>
    public static (int H, int V)? VariantDigits(string name)
    {
        int n = name.Length;
        if (n < 7 || name[n - 6] != '_' || name[n - 3] != '_')
        {
            return null;
        }

        if (!(name.StartsWith("IMG", StringComparison.Ordinal) || n >= 15))
        {
            return null;
        }

        int D(int at) => name[at] - '0';
        return ((10 * D(n - 5)) + D(n - 4), (10 * D(n - 2)) + D(n - 1));
    }

    /// <summary>The name as text, one character per byte.</summary>
    public static string Text(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);
}
