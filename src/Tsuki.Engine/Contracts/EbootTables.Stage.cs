using Tsuki.Engine.Stage;

namespace Tsuki.Engine.Contracts;

/// <summary>The eboot tables of the layer stage (package K2; docs/formats/layers.md 8, quake.md, compositing.md 9).</summary>
public partial interface IEbootTables
{
    /// <summary>The 24,192 layer motions with their field-enable words and loop points (<c>_STMA</c> / <c>_SGMA</c>).</summary>
    IMotionTables Motions { get; }

    /// <summary>The image catalog and the picture-name map as the layer commands use them (14,744 records).</summary>
    IPictureCatalog Pictures { get; }

    /// <summary>The STRA haze parameters (text VA 0x219910, ids 1..557, ten float32 each).</summary>
    IHazeTable Haze { get; }

    /// <summary>The six soft-clip radii (texel sizes of the BORDER_PARTS gradation quadrants, text VA 0x200590: 10, 32, 40, 64, 128, 256); null when the source has none (the stage then uses its built-in copy).</summary>
    IReadOnlyList<float>? ClipRadii => null;
}
