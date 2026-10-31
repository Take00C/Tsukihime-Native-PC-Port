namespace Tsuki.Engine.Stage;

/// <summary>The STRA haze parameter table of the eboot (text VA 0x219910: 558 entries of 10 float32, ids 1..557; the data layer implements it over <c>HazeTable</c>).</summary>
public interface IHazeTable
{
    /// <summary>The ten parameters of a STRA id, or false for an id outside the table.</summary>
    bool TryGetEntry(int straId, out float[] entry);
}

/// <summary>
/// The STRA haze of one layer as the renderer needs it: the table entry and the phase / fade counters of the drawn record (docs/formats/layers.md
/// 4.2, 6). The meaning of the ten floats is knife's <c>Haze::params</c> (hypothesis for the PS4); the stage only keeps the counters.
/// </summary>
/// <param name="Id">The STRA id of the layer (+0x2C8).</param>
/// <param name="Entry">The table entry of the id.</param>
/// <param name="Phase">Frames since the effect started, modulo 6000 (+0x2CA).</param>
/// <param name="FadeIn">Fade-in length in frames, 0 once reached (+0x2CC).</param>
/// <param name="FadeTotal">Fade-out length in frames, 0 when none runs (+0x2CE).</param>
/// <param name="FadeLeft">Fade-out frames left (+0x2D0).</param>
public readonly record struct HazeState(int Id, float[] Entry, int Phase, int FadeIn, int FadeTotal, int FadeLeft);
