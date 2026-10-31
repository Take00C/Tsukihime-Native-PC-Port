using Tsuki.Data.Eboot.Tables;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data;

// The screen-effect area of the eboot tables (package K7): the focus-line keys and the falling-note records in the form Tsuki.Engine/Game/Effects reads them.
public sealed partial class GameEbootTables
{
    private IEffectTables? _effects;

    /// <inheritdoc/>
    IEffectTables? IEbootTables.Effects => LazyInitializer.EnsureInitialized(ref _effects, () => new GameEffectTables(StageEngineTables))!;

    private sealed class GameEffectTables(EngineTables tables) : IEffectTables
    {
        private readonly IReadOnlyList<FocusNote> _notes =
        [
            .. tables.FclnNotes.Select(n => new FocusNote(n.Frames, n.Sequence, n.Width, n.Height, n.Step, n.Vx, n.Vy, n.JitterX, n.JitterY, n.Accel, n.Floor)),
        ];

        public int FocusEffects => FclnLineTable.Effects;

        public FocusKey FocusKey(int effect, int key)
        {
            FclnKey k = tables.FclnLines.Get(effect, key);
            return new FocusKey(k.TimeMs, k.CentreX, k.CentreY, k.RadiusMin, k.RadiusMax, k.CountMin, k.CountMax, k.Alpha, k.RerollMs);
        }

        public IReadOnlyList<FocusNote> FocusNotes => _notes;
    }
}
