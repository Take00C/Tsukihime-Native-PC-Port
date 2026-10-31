using Tsuki.Engine.Contracts;
using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

/// <summary>The draw-effect tables of docs/formats/compositing.md 5.1 that the stage needs to fill <see cref="DrawCmd.BlendState"/> (copy of <c>compose.effect</c>).</summary>
public static class EffectTables
{
    // 0x2D55E0, index e (blend state): 26 x 1, 6, 1, 1, 6, 22 x 1, then effects 52..67
    private static readonly int[] BlendState = Build();

    // 0xA1A800286A0: effects whose 0x100 flag turns blending off (0xD9FB9)
    private const long FlagSet = 0xA1A800286A0;

    /// <summary>The fixed-function blend state 0..13 of a draw-mode word.</summary>
    public static int BlendStateOf(int word)
    {
        int low = word & 0xFF;
        int e = low < 0x44 ? low : 0;
        int rest = word & unchecked((int)0xFFFFFEFF);
        bool flag = (word & 0x100) != 0 && rest >= 0 && rest <= 0x2B && ((FlagSet >> rest) & 1) != 0;
        return flag ? 0 : BlendState[e];
    }

    private static int[] Build()
    {
        var b = new List<int>();
        b.AddRange(Enumerable.Repeat(1, 26));
        b.AddRange([6, 1, 1, 6]);
        b.AddRange(Enumerable.Repeat(1, 22));
        b.AddRange([2, 7, 1, 10, 11, 5, 4, 9, 1, 1, 1, 2, 1, 13, 1, 1]);
        return [.. b];
    }
}

/// <summary>What the host needs from one <see cref="StageRuntime.Draw"/> call.</summary>
/// <param name="RuleTexture">The rule picture a type-2 transition draws this frame, or null.</param>
/// <param name="RuleProgress">The remaining progress of the transition (the host sets the rule texture's transition parameters from it every frame).</param>
/// <param name="LayersDrawn">Number of layers that produced quads.</param>
public readonly record struct StageDrawInfo(TextureRef? RuleTexture, float RuleProgress, int LayersDrawn);

public sealed partial class StageRuntime
{
    // transition (docs/formats/compositing.md 7): request 0xDF0754, type 0xDF0750, ms 0x781154, progress 0xE20CD0, step 0xE20D64
    private bool _requested;
    private int _reqType;
    private int _reqMs;
    private int _reqRule;
    private bool _trActive;
    private bool _trBeginPending;
    private int _trKind;
    private float _trProgress;
    private float _trStep;
    private int _trRule = -1;
    private readonly List<int> _drawnLayers = [];

    /// <summary>
    /// Called after the layers and before a running transition's capture quad (package K7): the screen effects that act on the composed stage (the
    /// ghost channel split, the focus lines) append their commands here, so the capture of the next transition holds them.
    /// </summary>
    public Action<DisplayList>? AfterLayers { get; set; }

    /// <summary>The picture layout source used to draw (null: layers produce no quads).</summary>
    public IPictureLayoutSource? Layouts { get; set; }

    /// <summary>The game language index for pictures with language bands (0 ja, 1 en, 2 zc, 3 zt).</summary>
    public int Language { get; set; }

    /// <summary>
    /// When true a changed STEC / SNX parameter regrades a layer's picture at the next commit even when the picture is unchanged; the default
    /// (false) rebuilds the graded texture only when the committed picture changes, as knife's stage does (compositing.md 9, open).
    /// </summary>
    public bool RegradeOnCommit { get; set; }

    /// <summary>The layers the last <see cref="Draw"/> produced quads for, in drawing order (a masked layer follows the layer whose window it is drawn in).</summary>
    public IReadOnlyList<int> DrawnLayers => _drawnLayers;

    /// <summary>True while a transition runs (progress above 0).</summary>
    public bool TransitionActive => _trActive;

    /// <summary>The transition progress: 1 at the start, 0 at the end (float 0xE20CD0).</summary>
    public float TransitionProgress => _trProgress;

    /// <summary>The type of the running transition (0xE23770).</summary>
    public int TransitionKind => _trKind;

    private void TransitionRequest(int type, int ms, int rule)
    {
        _requested = true;
        _reqType = type;
        _reqMs = ms;
        _reqRule = rule;
    }

    /// <summary><c>compose.fads_step</c>: the per-frame progress step of a transition of <paramref name="ms"/> milliseconds (types 12 and 49 add 100 ms).</summary>
    public static float FadsStep(int ms, int kind = 0)
    {
        if (kind is 12 or 49 && ms > 0)
        {
            ms += 100;
        }

        return ms <= 0 ? -1f : -1f / (ms / 16.666f);
    }

    private void StartTransition()
    {
        _requested = false;
        _trKind = _reqType;
        _trProgress = 1.0f;
        _trStep = FadsStep(_reqMs, _reqType);
        _trActive = true;
        _trBeginPending = true;
        _trRule = _reqRule > 0 ? _reqRule : -1;
    }

    private void AdvanceTransition(int dt)
    {
        _trProgress += Math.Min(3, dt) * _trStep;
        if (_trProgress <= 0f)
        {
            _trProgress = 0f;
            _trStep = 0f;
            _trActive = false;
        }
        else if (_trProgress >= 1f)
        {
            _trProgress = 1f;
            _trStep = -1f;
        }
    }

    private static readonly HashSet<int> Capture15Types = [2, .. Enumerable.Range(12, 13), 29, 30, 31, 32, 46, 49, 52, 53, 70];

    private static uint White(byte a = 255) => 0xFFFFFFu | ((uint)a << 24);

    private static DrawCmd Fill(float x0, float y0, float x1, float y1, uint rgba, int word) => new()
    {
        Kind = DrawKind.FillQuad,
        V0 = new Vertex(x0, y0, 0, 0, rgba),
        V1 = new Vertex(x1, y0, 0, 0, rgba),
        V2 = new Vertex(x0, y1, 0, 0, rgba),
        V3 = new Vertex(x1, y1, 0, 0, rgba),
        EffectWord = unchecked((short)word),
        BlendState = (byte)EffectTables.BlendStateOf(word),
    };

    private static DrawCmd Textured(TextureRef tex, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint rgba, int word) => new()
    {
        Kind = DrawKind.TexturedQuad,
        Texture = tex,
        V0 = new Vertex(x0, y0, u0, v0, rgba),
        V1 = new Vertex(x1, y0, u1, v0, rgba),
        V2 = new Vertex(x0, y1, u0, v1, rgba),
        V3 = new Vertex(x1, y1, u1, v1, rgba),
        EffectWord = unchecked((short)word),
        BlendState = (byte)EffectTables.BlendStateOf(word),
    };

    /// <summary>
    /// Appends the scene to <paramref name="list"/>: the transition start (the alpha-1 fill and the captures of the previous frame, once), the visible
    /// layers back to front by committed z (ties by layer number), and the transition frame (docs/formats/compositing.md 6, 7; layers.md 7, 9).
    /// </summary>
    public StageDrawInfo Draw(DisplayList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (_trBeginPending)
        {
            _trBeginPending = false;
            list.Add(Fill(0, 0, 1920, 1080, White(), 56));
            list.Add(new DrawCmd { Kind = DrawKind.Capture, Texture = TextureRef.ForCapture(0) });
            if (Capture15Types.Contains(_trKind))
            {
                list.Add(new DrawCmd { Kind = DrawKind.Capture, Texture = TextureRef.ForCapture(15) });
            }
        }

        _drawnLayers.Clear();
        _noiseFrame++;
        int drawn = DrawLayers(list);
        AfterLayers?.Invoke(list);
        (TextureRef? rule, float progress) = DrawTransitionFrame(list);
        return new StageDrawInfo(rule, progress, drawn);
    }

    private (TextureRef? Rule, float Progress) DrawTransitionFrame(DisplayList list)
    {
        if (!_trActive || _trKind == 1 || _trProgress <= 0f)
        {
            return (null, 0f);
        }

        if (TransitionScreens.Handles(_trKind))
        {
            // types 18, 20..24 and 68..70: the engine screens (TransitionScreens); only type 69 reads the rule picture
            TextureRef? screenRule = _trKind == 69 && _trRule > 0 && _ruleSlot >= 0 ? TextureRef.ForPictureTile(4, 85 + _trRule, 0) : null;
            TransitionScreens.Frame(list, _trKind, _trProgress, screenRule, Rng.Next);
            return (null, 0f);
        }

        int? surface;
        if (_trKind == 49)
        {
            surface = null;
        }
        else if (_trKind == 2)
        {
            surface = 15;
        }
        else if (_trKind is < 0 or > 70)
        {
            surface = null;
        }
        else
        {
            surface = 0;
        }

        if (surface is null)
        {
            return (null, 0f);
        }

        TextureRef capture = TextureRef.ForCapture(surface.Value);
        if (_trKind == 2 && _trRule > 0 && _ruleSlot >= 0)
        {
            var rule = TextureRef.ForPictureTile(4, 85 + _trRule, 0);
            list.Add(Textured(rule, 0, 0, 1920, 1080, 0, 0, 1, 1, White(), 58));
            list.Add(Textured(capture, 0, 0, 1920, 1080, 0, 0, 1, 1, White(), 26));
            return (rule, _trProgress);
        }

        byte alpha = (byte)Math.Min(255, (int)(MathF.Max(0f, _trProgress) * 255f));
        list.Add(Textured(capture, 0, 0, 1920, 1080, 0, 0, 1, 1, White(alpha), 0));
        return (null, 0f);
    }

    private int DrawLayers(DisplayList list)
    {
        // 0x56DE0: a selection sort of the committed z values; ties keep the lowest layer number
        Span<int> order = stackalloc int[LayerCount];
        Span<int> z = stackalloc int[LayerCount];
        for (int i = 0; i < LayerCount; i++)
        {
            z[i] = Drawn[i].ZOrder;
        }

        for (int i = 0; i < LayerCount; i++)
        {
            int best = int.MaxValue;
            int idx = -1;
            for (int j = 0; j < LayerCount; j++)
            {
                if (z[j] < best)
                {
                    best = z[j];
                    idx = j;
                }
            }

            order[i] = idx;
            if (idx >= 0)
            {
                z[idx] = int.MaxValue;
            }
        }

        int count = 0;
        (float X, float Y) shock = Shock.LayerOffset();
        for (int i = 0; i < LayerCount; i++)
        {
            int l = order[i];
            if (l < 0)
            {
                continue;
            }

            LayerRecord r = Drawn[l];
            if (r.Mask >= 0)
            {
                continue;       // drawn by its mask layer's pass (0x59EA0)
            }

            if (!Visible(r))
            {
                continue;
            }

            ClipWindow? clip = ClipOf(l, shock);
            PixelRect? window = clip is { } cw ? ToScissor(cw.Scissor()) : null;
            SoftWindow? soft = EffectPasses && clip is { } cs ? SoftOf(cs) : null;
            bool windowed = HasCrop(r);
            bool kids = HasVisibleMaskedLayerAfter(order, i, l);
            bool group = windowed || r.Sttr >= 0 || kids;
            _maskBuilt = false;
            bool windowDrawn = DrawLayer(list, l, window, !windowed, group, EffectPasses && kids, 0, soft);
            if (windowDrawn)
            {
                count++;
                _drawnLayers.Add(l);
            }

            byte kidMask = _maskBuilt ? MaskTarget : (byte)0;

            // layers that name this one as their mask are drawn inside its window, in z order; only those that come after it in z order are found (0x5A8E1 scans
            // the draw-order slots from i + 1), so a masked layer below its mask layer is never drawn
            for (int k = i + 1; k < LayerCount; k++)
            {
                int d = order[k];
                if (d >= 0 && Drawn[d].Mask == l && Visible(Drawn[d]) && DrawLayer(list, d, window, windowed, false, false, kidMask))
                {
                    count++;
                    _drawnLayers.Add(d);
                }
            }

            // the ridge of a window in the modes 1..6 lies over the window and the layers drawn in it
            if (windowDrawn && EffectPasses && clip is { IsBand: true, Inset: > 0 } band)
            {
                AddBand(list, band, window);
            }
        }

        return count;
    }

    // 0x5A5E0: used, committed z above 0
    private static bool Visible(LayerRecord r) => r.Used != 0 && r.ZOrder > 0;

    private static bool HasCrop(LayerRecord r) => r.Crop[2] > 0 && r.Crop[3] > 0;

    // 0x5A8E1: a visible layer (used, alpha plus alpha offset not 0) that names layer l as its mask and comes after it in the draw-order slots
    private bool HasVisibleMaskedLayerAfter(ReadOnlySpan<int> order, int i, int l)
    {
        for (int k = i + 1; k < LayerCount; k++)
        {
            int d = order[k];
            if (d < 0)
            {
                continue;
            }

            LayerRecord c = Drawn[d];
            if (c.Used > 0 && MathF.Min(1.0f, c.Alpha + c.AlphaCur) != 0f && c.Mask == l)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The clip window a layer draws through: its own crop rectangle, or the one of its mask layer, in 1920 x 1080 pixels (STCH a12..a15; the window
    /// moves with the shake offsets of its layer and with the screen shock, VA 0x49A60), with the inset and mode of STCH a16 / a17. Null when there is no
    /// window. The rectangle is not cut to the screen (<see cref="ToScissor"/> does).
    /// </summary>
    private ClipWindow? ClipOf(int l, (float X, float Y) shock)
    {
        int w = Drawn[l].Mask >= 0 ? Drawn[l].Mask : l;
        LayerRecord wr = Drawn[w];
        if (wr.Crop[2] <= 0 || wr.Crop[3] <= 0)
        {
            return null;
        }

        LayerRecord src = wr.Mask >= 0 ? Drawn[wr.Mask] : wr;
        float sx = 0f;
        float sy = 0f;
        foreach (ShakeSlot s in src.Slots)
        {
            if (s.Kind != 0)
            {
                sx += s.X;
                sy += s.Y;
            }
        }

        (float shx, float shy) = NoShockPicture(wr.Pic) ? (0f, 0f) : shock;
        float x = wr.Crop[0] + (sx * 3.0f) + (shx * 3.0f);
        float y = wr.Crop[1] + (sy * Ps4Math.YPixels) + (shy * Ps4Math.YPixels);
        int x0 = (int)MathF.Round(x);
        int y0 = (int)MathF.Round(y);
        return new ClipWindow(x0, y0, wr.Crop[2], wr.Crop[3], wr.A16, wr.A17);
    }

    /// <summary>The window as a scissor rectangle: cut to the 1920 x 1080 frame.</summary>
    private static PixelRect ToScissor(ClipWindow c)
    {
        int x0 = (int)c.X;
        int y0 = (int)c.Y;
        int x1 = x0 + (int)c.W;
        int y1 = y0 + (int)c.H;
        x0 = Math.Max(x0, 0);
        y0 = Math.Max(y0, 0);
        x1 = Math.Min(x1, 1920);
        y1 = Math.Min(y1, 1080);
        return new PixelRect(x0, y0, Math.Max(x1 - x0, 0), Math.Max(y1 - y0, 0));
    }

    /// <summary>
    /// 0x4F0E0: the noise overlay of a layer with <c>_STNS</c> (<c>stns</c> 1..12) is drawn after its picture and advances its frame counter once per draw: the
    /// counter takes the frames of the step (0xD47C0, at most 3) and wraps at the period, 20 for <c>stns</c> below 3, else 12 (the engine's remainder). The
    /// quads of the overlay are not built (open issue).
    /// </summary>
    private void AdvanceNoise(LayerRecord r)
    {
        if (r.Stns == 0)
        {
            return;
        }

        int period = r.Stns < 3 ? 20 : 12;
        int b = I8(r.StnsB + Math.Clamp(FramesPerUpdate, 0, 3));
        r.StnsB = (byte)b;
        if (period < b)
        {
            r.StnsB = (byte)(b % period);
        }
    }

    /// <param name="list">The display list.</param>
    /// <param name="l">The layer.</param>
    /// <param name="window">The clip window of the layer's pass.</param>
    /// <param name="noise">True when the layer's draw advances its STNS counter.</param>
    /// <param name="group">True for a layer the engine takes through the group pass (a crop window, or masked layers after it): the pass draws the layer at alpha 1, so it is "drawn" even at alpha 0.</param>
    /// <param name="windowOfKids">True when other layers are masked by this one: its silhouette goes into the mask target (<see cref="MaskTarget"/>) for them.</param>
    /// <param name="maskedBy">0, or the mask target that multiplies the alpha of this layer's quads (a layer named by a window layer).</param>
    /// <param name="soft">The soft edge of the window this layer is the window layer of (STCH a16 / a17): its mask multiplies this layer's quads and the silhouette that the layers it holds are masked by.</param>
    private bool DrawLayer(DisplayList list, int l, PixelRect? window, bool noise, bool group = false, bool windowOfKids = false, byte maskedBy = 0, SoftWindow? soft = null)
    {
        LayerRecord r = Drawn[l];
        if (r.Pic < 0)
        {
            return false;
        }

        // 0x51060 start: the tween offsets of scale, rotation and alpha are evaluated for the draw
        r.ScaleCur = r.TwScale.Remaining() * r.ScaleDelta;
        r.RotCur = I16(Cvtt(r.TwRot.Remaining() * r.RotDelta));
        r.AlphaCur = r.TwAlpha.Remaining() * r.AlphaDelta;
        float alpha = MathF.Min(1.0f, r.Alpha + r.AlphaCur);
        bool visible = alpha > 0f;
        if (!visible && !group)
        {
            return false;
        }

        // the noise counter moves when the layer is drawn, a window layer's own pass has none and the layers masked by a window with a crop rectangle get theirs in its mask pass
        // (observed: plain layer and window without crop advance themselves, the child of a window without crop does not, the child of a window with a crop does)
        if (noise)
        {
            AdvanceNoise(r);
        }

        if (!visible)
        {
            if (windowOfKids)
            {
                list.Add(ClearTarget(MaskTarget));      // the group is invisible with its window: the layers it masks see an empty mask
                _maskBuilt = true;
            }

            return true;        // the group pass draws the layer at alpha 1 as the mask source; at alpha 0 it leaves nothing for the display list
        }

        PictureLayout? layout = Layouts?.Get(r.Pic, Language);
        if (layout is null)
        {
            return false;
        }

        if (window is { Width: <= 0 } or { Height: <= 0 })
        {
            return layout.Files.Any(f => f.Tiles.Count > 0);        // the window clips the layer away; the engine still draws it into the pass
        }

        (float shx, float shy) = NoShockPicture(r.Pic) ? (0f, 0f) : Shock.LayerOffset();
        (float slx, float sly) = Shakes.SlotOffset(l);
        (float mx, float my) = Shakes.MaskOffset(l, (0f, 0f));
        Affine m = Projection.FrameMatrix(Drawn, l, _catalog, (shx + slx, shy + sly), (mx, my));
        int key = GradeKeyOf(l);
        uint rgba = White((byte)Math.Min(255, (int)(MathF.Max(0f, alpha) * 255f)));
        int word = ((r.Flags & 0x80) != 0 ? 0x100 : 0) | (r.Blend & 0xFF);
        var rule = EffectPasses && maskedBy == 0 ? RuleOf(r) : null;           // a masked layer keeps its window's mask: it must not rebuild the shared target
        HazeState? haze = EffectPasses && r.Stns == 0 ? Haze(l) : null;
        bool noisy = EffectPasses && r.Stns is >= 1 and <= 12;
        byte ownMask = rule is not null ? MaskTarget : maskedBy;
        SoftWindow? ownSoft = rule is null && maskedBy == 0 ? soft : null;
        if (ownSoft is not null)
        {
            ownMask = SoftTarget;
        }

        _quads.Clear();
        foreach (PictureFileLayout file in layout.Files)
        {
            foreach (TileQuad t in file.Tiles)
            {
                double ox = file.OffsetX;
                double oy = file.OffsetY;
                (double ax, double ay) = m.Apply(t.X0 + ox, t.Y0 + oy);
                (double bx, double by) = m.Apply(t.X1 + ox, t.Y0 + oy);
                (double cx, double cy) = m.Apply(t.X0 + ox, t.Y1 + oy);
                (double dx, double dy) = m.Apply(t.X1 + ox, t.Y1 + oy);
                _quads.Add(new DrawCmd
                {
                    Kind = DrawKind.TexturedQuad,
                    Texture = TextureRef.ForPictureTile(file.Archive, file.Entry, t.Tile, key),
                    V0 = new Vertex((float)ax, (float)ay, t.U0, t.V0, rgba),
                    V1 = new Vertex((float)bx, (float)by, t.U1, t.V0, rgba),
                    V2 = new Vertex((float)cx, (float)cy, t.U0, t.V1, rgba),
                    V3 = new Vertex((float)dx, (float)dy, t.U1, t.V1, rgba),
                    EffectWord = unchecked((short)word),
                    BlendState = (byte)EffectTables.BlendStateOf(word),
                    Clip = window,
                    Fx = haze is { } h ? HazeFx(h, layout, file, t, ownMask) : (ownMask != 0 ? QuadFx.Masked(ownMask) : default),
                });
            }
        }

        // a picture whose tiles all have flag 0 (the unused solid colours, placeholders) has no texture: the engine makes no draw call for it
        if (_quads.Count == 0)
        {
            return false;
        }

        if (noisy)
        {
            DrawCmd grain = NoiseQuad(r, m, layout, rgba, word, window, ownMask != 0 ? QuadFx.Masked(ownMask) : default);
            _quads.Clear();
            _quads.Add(grain);
        }

        if (ownSoft is { } sw)
        {
            list.Add(ClearTarget(SoftTarget));
            AddSoftMask(list, sw, SoftTarget, multiply: false);
        }

        if (rule is { } rf)
        {
            list.Add(ClearTarget(MaskTarget));
            AddRuleMask(list, rf, alpha);
            _maskBuilt = true;
        }
        else if (windowOfKids)
        {
            list.Add(ClearTarget(MaskTarget));
            AddSilhouette(list, multiply: false);
            if (ownSoft is { } sk)
            {
                AddSoftMask(list, sk, MaskTarget, multiply: true);      // the layers held by the window fade with its edge
            }

            _maskBuilt = true;
        }

        foreach (DrawCmd q in _quads)
        {
            list.Add(q);
        }

        if (rule is not null && windowOfKids)
        {
            AddSilhouette(list, multiply: true);
        }

        return true;
    }

    private int GradeKeyOf(int l)
    {
        LayerRecord r = Drawn[l];
        return r.GradeKey;
    }
}
