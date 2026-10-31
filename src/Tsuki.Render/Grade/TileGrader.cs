// The seam between the texture cache and the recolour kernel: a picture tile whose TextureRef.D is not 0 is built through the grade
// GradeKeys.Resolve(D) names, and the graded texture is cached under that key (ARCHITECTURE 4.3; compositing.md 9; K2.md open issue 2).
using Tsuki.Data.Eboot;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;

namespace Tsuki.Render.Grade;

/// <summary>Applies the <c>_STEC</c> / <c>_SNX2</c> / <c>_SNX3</c> grade of a texture key to decoded RGBA pixels. Stateless and thread-safe.</summary>
/// <param name="tables">The PS4 grade tables.</param>
public sealed class TileGrader(StecTables tables)
{
    /// <summary>The tables.</summary>
    public StecTables Tables { get; } = tables ?? throw new ArgumentNullException(nameof(tables));

    /// <summary>Reads the tables from the eboot and wraps them.</summary>
    public static TileGrader FromEboot(EbootReader eboot) => new(StecTables.Read(eboot));

    /// <summary>True when the texture of <paramref name="key"/> is a graded picture tile (<c>Kind = PictureTile</c>, <c>D != 0</c>).</summary>
    public static bool IsGraded(in TextureRef key) => key.Kind == TextureKind.PictureTile && key.D != 0;

    /// <summary>Grades the pixels of a tile built for <paramref name="key"/> in place; keys without a grade are left alone.</summary>
    public void Apply(in TextureRef key, Span<byte> rgba)
    {
        if (IsGraded(key))
        {
            StecKernel.Apply(rgba, GradeKeys.Resolve(key.D), Tables);
        }
    }
}
