using System.Collections.Immutable;

namespace Tsuki.Data.Images;

/// <summary>
/// The parts of a sprite file name <c>CHR_S_PP_VV_Xnn[_...].MZP</c> (docs/formats/images.md 7): <c>ARU_L_81_00_N00_B.MZP</c> is the body
/// ("base") and <c>ARU_L_81_00_N00.MZP</c> or <c>ARU_L_81_00_A01_00_01.MZP</c> are face layers on the same tile grid. A port of
/// <c>tsuki.image.split_sprite_name</c>.
/// </summary>
/// <param name="Char">Character code, 3 letters (<c>ARU</c>).</param>
/// <param name="Size">Size class, 1 letter (<c>A</c>, <c>L</c>, <c>M</c>, <c>S</c>).</param>
/// <param name="Pose">Pose, two digits.</param>
/// <param name="Variant">Variant, two digits.</param>
/// <param name="Face">Face or expression, 3 characters (<c>N00</c>, <c>A01</c>).</param>
/// <param name="Suffix">The remaining <c>_xx</c> parts (<c>04</c>, <c>04</c>) or <c>B</c>.</param>
public sealed record SpriteName(string Char, string Size, string Pose, string Variant, string Face, ImmutableArray<string> Suffix)
{
    /// <summary>True for a body layer: the suffix is exactly <c>B</c>.</summary>
    public bool IsBase => Suffix.Length == 1 && Suffix[0] == "B";

    /// <summary>The name shared by a base and its face layers: <c>CHAR_SIZE_POSE_VAR</c>.</summary>
    public string Group => $"{Char}_{Size}_{Pose}_{Variant}";

    /// <summary>Parses a file name; null for names that are not sprites (<c>IMG0001.MZP</c>, <c>BLACK.MZP</c> ...).</summary>
    public static SpriteName? Split(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int dot = name.LastIndexOf('.');
        string stem = dot >= 0 ? name[..dot] : name;
        string[] p = stem.Split('_');
        if (p.Length < 5 || p[0].Length != 3 || p[1].Length != 1 || p[4].Length != 3)
        {
            return null;
        }

        return new SpriteName(p[0], p[1], p[2], p[3], p[4], [.. p.AsSpan(5)]);
    }
}
