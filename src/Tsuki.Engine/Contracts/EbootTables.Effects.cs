namespace Tsuki.Engine.Contracts;

/// <summary>The eboot tables of the screen effects (package K7; docs/research/knife_tables.md rows 2.4 and 2.5).</summary>
public partial interface IEbootTables
{
    /// <summary>The focus-line and falling-note tables of <c>_FCLN</c> / <c>_FLNT</c>, or null when the source holds none (the golden slice); the effects then draw nothing.</summary>
    IEffectTables? Effects => null;
}

/// <summary>One timed key of a focus-line effect; <see cref="TimeMs"/> -1 marks an unused key. Positions are in 1280 x 720 units.</summary>
/// <param name="TimeMs">Key time in ms, -1 = unused.</param>
/// <param name="CentreX">Centre x.</param>
/// <param name="CentreY">Centre y.</param>
/// <param name="RadiusMin">Inner radius of the lines.</param>
/// <param name="RadiusMax">Outer radius of the lines.</param>
/// <param name="CountMin">Fewest lines.</param>
/// <param name="CountMax">Most lines.</param>
/// <param name="Alpha">0..255.</param>
/// <param name="RerollMs">Interval at which the lines are rolled again.</param>
public readonly record struct FocusKey(int TimeMs, int CentreX, int CentreY, int RadiusMin, int RadiusMax, int CountMin, int CountMax, int Alpha, int RerollMs);

/// <summary>One falling-note kind of the effects 1..3 (docs/research/knife_tables.md row 2.5).</summary>
/// <param name="Frames">Animation frames.</param>
/// <param name="Sequence">The frame shown at each animation step (<paramref name="Frames"/> entries).</param>
/// <param name="Width">Frame width in texels.</param>
/// <param name="Height">Frame height in texels.</param>
/// <param name="Step">Ticks per animation frame.</param>
/// <param name="Vx">Initial x velocity.</param>
/// <param name="Vy">Initial y velocity.</param>
/// <param name="JitterX">Velocity jitter scale in x.</param>
/// <param name="JitterY">Velocity jitter scale in y.</param>
/// <param name="Accel">Acceleration in y per frame.</param>
/// <param name="Floor">The y at which a note ends.</param>
public sealed record FocusNote(int Frames, IReadOnlyList<int> Sequence, int Width, int Height, int Step, float Vx, float Vy, float JitterX, float JitterY, float Accel, int Floor);

/// <summary>The focus-line and falling-note tables read from the user's eboot (K1b's readers).</summary>
public interface IEffectTables
{
    /// <summary>Number of focus-line effects (30: <c>_FCLN</c> effects 4..33).</summary>
    int FocusEffects { get; }

    /// <summary>Key <paramref name="key"/> (0..3) of focus-line effect <paramref name="effect"/> (0..29; 0 is <c>_FCLN</c> effect 4).</summary>
    FocusKey FocusKey(int effect, int key);

    /// <summary>The three falling-note kinds (<c>_FCLN</c> effects 1..3).</summary>
    IReadOnlyList<FocusNote> FocusNotes { get; }
}
