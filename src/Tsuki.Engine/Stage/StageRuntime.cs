using Tsuki.Engine.Contracts;
using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

/// <summary>
/// The 40 script layers of the scenario renderer (docs/formats/layers.md, quake.md): the script-side <see cref="SceneState"/>, the committed
/// array B and the drawn array A, the layer commands, the <c>_FADS</c> commit, the per-frame update (tweens, motions, shakes, screen shock,
/// message quake) and the build of the layer part of the display list. The game interpreter calls <see cref="Execute"/> for every layer
/// statement, <see cref="Update"/> once per 60 Hz step and <see cref="Draw"/> to append the scene to the frame's <see cref="DisplayList"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the Python oracle (<c>tsuki.misc_layer_state_and_motions</c>, <c>tsuki.quake</c>), which is verified against the engine's own
/// code under emulation; the per-statement effects on the drawn records and the per-frame order follow the eboot disassembly
/// (VA 0x57980 update, 0x81760 STCH, 0x82D30 STCC, 0x4A770 reset, 0x4A620 / 0x49930 / 0x4A4B0 tween setters). Where the oracle is silent the
/// behaviour of knife's stage (<c>stage.rs</c>) is the reference, and the status file lists each such place.
/// </para>
/// <para>The class is not thread-safe: the main thread owns it (ARCHITECTURE 3.4).</para>
/// </remarks>
public sealed partial class StageRuntime
{
    /// <summary>Number of layers.</summary>
    public const int LayerCount = 40;

    private readonly IPictureCatalog _catalog;
    private readonly MotionTable _motions;
    private bool _commitPending;
    private int _ruleSlot = -1;

    /// <summary>Creates the stage over a catalog and the motion tables.</summary>
    /// <param name="catalog">The picture catalog and name map.</param>
    /// <param name="motions">The motion tables.</param>
    /// <param name="rng">The shared <c>rand()</c>; a fresh generator at state 1 when null.</param>
    public StageRuntime(IPictureCatalog catalog, IMotionTables motions, Ps4Rand? rng = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(motions);
        _catalog = catalog;
        _motions = new MotionTable(motions);
        Scene = SceneState.CreateDefault();
        Rng = rng ?? new Ps4Rand();
        Rnd2 = new Rnd2(Rng);
        Drawn = new LayerRecord[LayerCount];
        Committed = new LayerRecord[LayerCount];
        for (int i = 0; i < LayerCount; i++)
        {
            Drawn[i] = new LayerRecord();
            Committed[i] = new LayerRecord();
            Array.Clear(Committed[i].Colour, 0xB, 6);
        }

        Shakes = new ShakeEngine(Drawn, Rng, Rnd2, NoShockPicture, Scene);
        MessageQuake = new MessageQuake();
        Shock = new ScreenShock();
    }

    /// <summary>The script-side layer state (scene_state, VA 0x16500C0).</summary>
    public SceneState Scene { get; }

    /// <summary>The drawn records A (VA 0xDDB5E8).</summary>
    public LayerRecord[] Drawn { get; }

    /// <summary>The committed records B (VA 0xDE2650).</summary>
    public LayerRecord[] Committed { get; }

    /// <summary>The picture catalog.</summary>
    public IPictureCatalog Catalog => _catalog;

    /// <summary>The session's motion table (edited by <c>_SGMA</c>).</summary>
    public MotionTable Motions => _motions;

    /// <summary>The shared <c>rand()</c>.</summary>
    public Ps4Rand Rng { get; }

    /// <summary>The flicker generator of SHK2.</summary>
    public Rnd2 Rnd2 { get; }

    /// <summary>The layer shake slots.</summary>
    public ShakeEngine Shakes { get; }

    /// <summary>The message-window quake (<c>_MSQK</c>); its offsets go to the HUD.</summary>
    public MessageQuake MessageQuake { get; }

    /// <summary>The screen shock (<c>_SHOK</c>).</summary>
    public ScreenShock Shock { get; }

    /// <summary>The scan-mode byte (0x174DE48, the re-execution after a slot load): timed shakes and <c>_SHOK</c> do nothing while it is set.</summary>
    public bool ScanMode { get; set; }

    /// <summary>The global colour-effect word (0x164B97C): when above 0, <c>_STCH</c> stores it as the layer's colour-effect mode.</summary>
    public int DefaultColourEffect { get; set; }

    /// <summary>The configuration byte 0x66C578+0xA5: whether <c>_STMA</c> without a wait argument waits (0 = wait).</summary>
    public int MotionWaitConfig { get; set; }

    /// <summary>Set by <c>_STMA</c> when the script must wait for the motion (word 0x164BA6E); the interpreter clears it after reading.</summary>
    public bool MotionWaitRequested { get; set; }

    /// <summary>Word 0x164B998: set by <c>_SGMA</c> and every shake start.</summary>
    public bool SceneDirty { get; set; }

    /// <summary>The system-menu fraction (float 0x1202F0C, VA 0x6F430): the whole layer update is skipped at 1.0 or more.</summary>
    public float MenuFraction { get; set; }

    /// <summary>Frames per update (word 0xDC7BE0, 1 at 60 Hz, at most 3); <see cref="Update"/>'s argument is multiplied by it.</summary>
    public int FramesPerUpdate { get; set; } = 1;

    /// <summary>True while a <c>_FADS</c> commit waits for the next update to move B to A and start the transition.</summary>
    public bool CommitPending => _commitPending;

    /// <summary>The rule number last loaded into the shared rule slot (<c>_STTR</c>, <c>_FADS</c> type 2), or -1.</summary>
    public int RuleSlot => _ruleSlot;

    /// <summary>The STRA haze table (null: <see cref="Haze"/> returns null).</summary>
    public IHazeTable? HazeTable { get; set; }

    /// <summary>The haze state of a layer while its STRA effect runs; null when it does not or the id has no table entry.</summary>
    public HazeState? Haze(int layer)
    {
        LayerRecord r = Drawn[layer];
        if (r.Stra <= 0 || HazeTable is not { } table || !table.TryGetEntry(r.Stra, out float[] entry))
        {
            return null;
        }

        return new HazeState(r.Stra, entry, r.StraPhase, r.StraFadeIn, r.StraFadeTotal, r.StraFadeLeft);
    }

    private bool NoShockPicture(int pic) => pic >= 0 && pic < _catalog.Count && (_catalog.Info(pic).DrawFlags & 2) != 0;

    /// <summary>
    /// Commit (<c>_FADS</c>, VA 0x7DD10): writes the committed records B from the scene state, puts the scene zoom and pivot flag into A, and
    /// requests the transition; the next <see cref="Update"/> moves B to A. <paramref name="type"/> and <paramref name="ms"/> go to the transition.
    /// </summary>
    public void Commit(int type, int ms, int rule)
    {
        for (int l = 0; l < LayerCount; l++)
        {
            CommitLayer(l);
        }

        _commitPending = true;
        TransitionRequest(type, ms, rule);
    }

    private void CommitLayer(int l)
    {
        SceneState g = Scene;
        LayerRecord b = Committed[l];
        LayerRecord a = Drawn[l];
        int pic = g.GetPic(l);
        b.Pic = pic < 0x59D8 ? pic : -1;
        b.Blend = g.GetBlend(l);
        b.FxMode = g.GetFxMode(l);
        b.FxA = g.GetFxA(l);
        b.FxB = g.GetFxB(l);
        b.X = g.GetX(l);
        b.Y = g.GetY(l);
        ReadOnlySpan<byte> ex = g.Ext(l);
        b.OffX = (short)(ex[2] | (ex[3] << 8));
        b.OffY = (short)(ex[4] | (ex[5] << 8));
        b.Alpha = g.GetAlpha(l) * F(1.0 / 255.0);
        b.State = g.GetState(l);
        b.Scale = g.GetScale(l);
        b.AnchorX = g.GetAnchorX(l);
        b.AnchorY = g.GetAnchorY(l);
        b.Rot = g.GetRot(l);
        b.Mask = g.GetMask(l);
        b.Parent = g.GetParent(l);
        b.Sttr = g.GetSttr(l);
        b.SoxyX = g.GetSoxyX(l);
        b.SoxyY = g.GetSoxyY(l);
        b.Flags = g.GetFlags(l);
        for (int j = 0; j < 4; j++)
        {
            b.Crop[j] = g.GetCrop(l, j);
        }

        b.A16 = g.GetA16(l);
        b.A17 = g.GetA17(l);
        b.Angle = g.GetAngle(l);
        b.Stns = g.GetStns(l);
        b.StnsB = g.GetStnsB(l);
        b.ZOrder = g.GetZ(l);
        b.Stra = g.GetStra(l);

        // colour block from the scene ext (0xF760): the previous colours shift to +0xB.., +5..+7 = ext +6..+8, +8..+0xA = ext +9..+0xB
        MotionApply.ColourBlock(b.Colour, (ex[6] << 16) | (ex[7] << 8) | ex[8], (ex[9] << 16) | (ex[10] << 8) | ex[11]);

        // motion record (0xBA8E0) and shake slots (kind, delay, time, parameters; the runtime fields stay 0)
        short mid = g.GetMotionId(l);
        short mdiv = g.GetMotionDivisor(l);
        long mfr = g.GetMotionFrames(l);
        byte mpause = g.GetMotionPause(l);
        MotionApply.MotionClear(b);
        MotionApply.MotionCopy(ref b.MotionId, ref b.MotionDivisor, ref b.MotionFrames, ref b.MotionPause, mid, mdiv, mfr, mpause);
        for (int s = 0; s < 2; s++)
        {
            ShakeSlot slot = b.Slots[s];
            slot.Clear();
            slot.Kind = g.GetShakeKind(l, s);
            slot.Delay = g.GetShakeFade(l, s);
            slot.Time = g.GetShakeTime(l, s);
            for (int p = 0; p < 6; p++)
            {
                slot.Par[p] = g.GetShakePar(l, (6 * s) + p);
            }
        }

        // 0x49930(l, zoom, zoom, 0 frames): the scene zoom and the pivot flag go to A at once, ending a zoom tween
        a.ZoomX = g.GetZoomX(l);
        a.ZoomY = g.GetZoomY(l);
        a.ZoomPivot = (byte)g.GetZoomPivot(l);
        a.TwZoom = new Tween(0, 0, 0);
        a.ZoomDx = 0f;
        a.ZoomDy = 0f;
    }

    /// <summary>
    /// B to A (the first update after a commit; VA 0x58B3D.. 0x59957): every field the commit wrote is moved to the drawn record, the layer
    /// becomes used, the motion record is merged (0xBA8E0) and committed shake slots replace the running ones when their kind differs.
    /// </summary>
    private void MoveCommitted()
    {
        for (int l = 0; l < LayerCount; l++)
        {
            LayerRecord a = Drawn[l];
            LayerRecord b = Committed[l];

            // the parameters of the picture's texture (blend mode, colour effect, colour block) are applied only while the layer has a texture:
            // it showed one before or it gets one now (picture load 0x4BB50 / 0x49F90 / 0x49FB0)
            bool texture = a.Used != 0 || b.Pic >= 0;
            bool regrade = RegradeOnCommit || a.Used == 0 || a.Pic != b.Pic;
            a.Used = b.Pic >= 0 ? 1 : 0;
            a.Pic = b.Pic;
            a.Flags = b.Flags;
            a.State = b.State;
            a.Alpha = b.Alpha;
            a.X = b.X;
            a.Y = b.Y;
            a.OffX = b.OffX;
            a.OffY = b.OffY;
            a.Rot = b.Rot;
            a.Scale = b.Scale;
            a.AnchorX = b.AnchorX;
            a.AnchorY = b.AnchorY;
            a.Mask = b.Mask;
            a.Parent = b.Parent;
            a.SoxyX = b.SoxyX;
            a.SoxyY = b.SoxyY;
            b.Crop.CopyTo(a.Crop, 0);
            a.A16 = b.A16;
            a.A17 = b.A17;
            a.Angle = b.Angle;
            a.Stns = b.Stns;
            a.StnsB = b.StnsB;
            a.Stra = b.Stra;
            if (texture)
            {
                a.Blend = b.Blend;
                a.FxMode = b.FxMode;
                a.FxA = b.FxA;
                a.FxB = b.FxB;
                Array.Copy(b.Colour, a.Colour, 17);
                if (regrade)
                {
                    int rgb = (b.Colour[5] << 16) | (b.Colour[6] << 8) | b.Colour[7];
                    a.GradeKey = GradeKeys.Intern(ColourGrade.From(b.FxMode, b.FxA, b.FxB, rgb, Scene.ColourMode));
                }
            }

            a.ZOrder = b.ZOrder;
            a.Face = a.Pic >= 0 && a.Pic < _catalog.Count && _catalog.Info(a.Pic).HasFace ? 1 : 0;
            MotionApply.MotionCopy(a, b);
            Shakes.MergeCommitted(l, b);

            // 0x59957: the motion is applied to the moved record at once (dt = 0)
            MotionApply.Apply(_motions, _catalog, a, l, 0.0, Scene);
        }
    }

    /// <summary>
    /// One update of the layer stage with <paramref name="arg"/> 60 Hz frames (VA 0x57980; <c>trunc(arg * FramesPerUpdate)</c> frames of
    /// time): the message quake, then per layer the tweens, the motion, the shake slots and the STRA phase, then the commit transfer and the screen
    /// shock. The whole layer part is skipped while the system menu covers the screen.
    /// </summary>
    public void Update(int arg = 1)
    {
        MessageQuake.Update(Rng, arg, FramesPerUpdate);
        if (MenuFraction >= 1.0f)
        {
            return;
        }

        int dt = Cvtt((float)arg * FramesPerUpdate);
        float dtf = dt;
        if (_trActive && !_requested)
        {
            AdvanceTransition(dt);
        }

        for (int l = 0; l < LayerCount; l++)
        {
            LayerRecord r = Drawn[l];
            r.TwMove.Tick(dtf);
            r.TwScale.Tick(dtf);
            r.TwRot.Tick(dtf);
            r.TwAlpha.Tick(dtf);
            float rem = r.TwAlpha.Remaining();
            r.AlphaCur = rem * r.AlphaDelta;
            r.TwZoom.Tick(dtf);
            MotionApply.Apply(_motions, _catalog, r, l, dtf, Scene);
            Shakes.UpdateLayer(l, dtf);
            UpdateStra(r, dt);
        }

        if (_commitPending)
        {
            _commitPending = false;
            MoveCommitted();
        }

        if (_requested)
        {
            StartTransition();
        }

        Shock.Update(dt);
    }

    private static void UpdateStra(LayerRecord r, int dt)
    {
        // 0x588a0: the STRA phase runs modulo 6000, the fade-in ends when it is reached, the fade-out ends the effect
        int phase = r.StraPhase + dt;
        phase -= phase / 6000 * 6000;
        r.StraPhase = I16(phase);
        if (r.StraFadeIn > 0 && phase >= r.StraFadeIn)
        {
            r.StraFadeIn = 0;
        }

        if (r.StraFadeTotal > 0)
        {
            short left = I16(r.StraFadeLeft - dt);
            r.StraFadeLeft = left;
            if (left <= 0)
            {
                r.Stra = 0;
                r.StraFadeTotal = 0;
                r.StraFadeLeft = 0;
            }
        }
    }
}
