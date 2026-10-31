using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>
/// Appends the quads of the menus to a <see cref="DisplayList"/> (ARCHITECTURE 9.1): sprites cut from UI sheets (split along the sheet's tiles),
/// fills, and the global alpha and scissor that knife's <c>fade_quads</c> and <c>q.clip</c> apply to a group of quads. Coordinates are canvas
/// pixels of the 1920 x 1080 frame; helpers in <see cref="UiMetrics"/> convert the 640 x 448 units.
/// </summary>
public sealed class UiPainter
{
    private static readonly int[] BlendStates = BuildBlendStates();

    private readonly List<PixelRect> _clips = [];
    private readonly List<GuideHit> _guideHits = [];
    private float _alpha = 1f;

    /// <summary>Creates a painter over the sheets of <paramref name="assets"/>.</summary>
    public UiPainter(UiAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        Assets = assets;
    }

    /// <summary>The sheets and tables.</summary>
    public UiAssets Assets { get; }

    /// <summary>
    /// How the button prompts are drawn this frame (the guide line and the pictures of the pad): the root sets it from the game services before the screens
    /// draw (package K9). A painter outside a root draws the game's own PlayStation icons.
    /// </summary>
    public PromptStyle PromptStyle { get; set; } = PromptStyle.PlayStation;

    /// <summary>
    /// The glyph text drawer of the key caps and the Latin words of the prompts: always the English glyph pages (the sans face of the English UI), whatever the
    /// UI language is, so that Enter, Esc and PgUp look the same everywhere. It is the painter's own, so a cap does not depend on a screen's context.
    /// </summary>
    internal UiText KeyText => _keyText ??= new UiText(new UiAssets(Assets.Data, Tsuki.Engine.Contracts.Language.En));

    /// <summary>The glyph text drawer of the words of the prompts that are translated (the heading of the setting row): the UI language's own glyph pages.</summary>
    internal UiText LabelText => _labelText ??= new UiText(Assets);

    private UiText? _keyText;
    private UiText? _labelText;

    /// <summary>The clickable words of the button guide lines drawn since <see cref="Begin"/> (package K12: the root reads them on the next step).</summary>
    public IReadOnlyList<GuideHit> GuideHits => _guideHits;

    internal void AddGuideHit(GuideHit hit) => _guideHits.Add(hit);

    /// <summary>The list being filled.</summary>
    public DisplayList List { get; private set; } = new();

    /// <summary>Quads appended since <see cref="Begin"/>.</summary>
    public int QuadCount { get; private set; }

    /// <summary>The factor every draw's alpha is multiplied by.</summary>
    public float Alpha => _alpha;

    /// <summary>Starts filling <paramref name="list"/>: alpha 1, no scissor.</summary>
    public void Begin(DisplayList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        List = list;
        _alpha = 1f;
        _clips.Clear();
        _guideHits.Clear();
        QuadCount = 0;
    }

    /// <summary>Multiplies the alpha of everything drawn until the scope is disposed.</summary>
    public AlphaScope WithAlpha(float alpha)
    {
        var s = new AlphaScope(this, _alpha);
        _alpha *= Math.Clamp(alpha, 0f, 1f);
        return s;
    }

    /// <summary>Clips everything drawn until the scope is disposed to <paramref name="rect"/> (intersected with the enclosing clip).</summary>
    public ClipScope WithClip(RectF rect)
    {
        var s = new ClipScope(this, _clips.Count);
        var r = new PixelRect((int)MathF.Floor(rect.X), (int)MathF.Floor(rect.Y), (int)MathF.Ceiling(rect.W), (int)MathF.Ceiling(rect.H));
        if (_clips.Count > 0)
        {
            PixelRect o = _clips[^1];
            int x0 = Math.Max(r.X, o.X);
            int y0 = Math.Max(r.Y, o.Y);
            int x1 = Math.Min(r.X + r.Width, o.X + o.Width);
            int y1 = Math.Min(r.Y + r.Height, o.Y + o.Height);
            r = new PixelRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }

        _clips.Add(r);
        return s;
    }

    /// <summary>Draws the part <paramref name="src"/> (sheet texels) of <paramref name="sheet"/> into <paramref name="dst"/> (canvas pixels).</summary>
    /// <param name="sheet">The sheet.</param>
    /// <param name="src">The part of the sheet in texels.</param>
    /// <param name="dst">Where it lands, in canvas pixels.</param>
    /// <param name="alpha">Alpha 0..1.</param>
    /// <param name="blend">How the sprite is combined with what is under it.</param>
    /// <param name="flipX">Mirror horizontally.</param>
    /// <returns>False when the sheet does not exist.</returns>
    public bool Sprite(UiSheet sheet, RectF src, RectF dst, float alpha = 1f, UiBlend blend = UiBlend.Alpha, bool flipX = false)
    {
        SheetGeometry? g = Assets.Geometry(sheet);
        return g is not null && DrawTiles(g, new TileSource(sheet.Set, Assets.Language, sheet.Child, 0, true), src, dst, alpha, blend, flipX);
    }

    /// <summary>Draws the part <paramref name="src"/> (picture texels) of a picture of an image archive into <paramref name="dst"/>.</summary>
    /// <returns>False when the picture does not exist.</returns>
    public bool Picture(UiPicture picture, RectF src, RectF dst, float alpha = 1f, UiBlend blend = UiBlend.Alpha)
    {
        SheetGeometry? g = Assets.Geometry(picture);
        return g is not null && DrawTiles(g, new TileSource(default, default, picture.Archive, picture.Entry, false), src, dst, alpha, blend, false);
    }

    /// <summary>Draws the whole picture stretched into <paramref name="dst"/>.</summary>
    public bool Picture(UiPicture picture, RectF dst, float alpha = 1f, UiBlend blend = UiBlend.Alpha)
    {
        SheetGeometry? g = Assets.Geometry(picture);
        return g is not null && DrawTiles(g, new TileSource(default, default, picture.Archive, picture.Entry, false), new RectF(0, 0, g.Width, g.Height), dst, alpha, blend, false);
    }

    private readonly record struct TileSource(UiSet Set, Language Language, int A, int B, bool Ui)
    {
        public TextureRef Tile(int index) => Ui ? TextureRef.ForUiTile(Set, Language, A, index) : TextureRef.ForPictureTile(A, B, index);
    }

    private bool DrawTiles(SheetGeometry g, in TileSource source, RectF src, RectF dst, float alpha, UiBlend blend, bool flipX)
    {
        float a = Math.Clamp(alpha, 0f, 1f) * _alpha;
        if (src.IsEmpty || dst.IsEmpty || a <= 0f)
        {
            return true;
        }

        uint color = Pack(a);
        float sx = dst.W / src.W;
        float sy = dst.H / src.H;
        foreach (SheetTile t in g.Tiles)
        {
            float ix0 = MathF.Max(src.X, t.X);
            float iy0 = MathF.Max(src.Y, t.Y);
            float ix1 = MathF.Min(src.Right, t.X + t.W);
            float iy1 = MathF.Min(src.Bottom, t.Y + t.H);
            if (ix1 <= ix0 || iy1 <= iy0)
            {
                continue;
            }

            float u0 = (t.SourceX + (ix0 - t.X)) / t.StoredWidth;
            float u1 = (t.SourceX + (ix1 - t.X)) / t.StoredWidth;
            float v0 = (t.SourceY + (iy0 - t.Y)) / t.StoredHeight;
            float v1 = (t.SourceY + (iy1 - t.Y)) / t.StoredHeight;
            float x0 = dst.X + ((ix0 - src.X) * sx);
            float x1 = dst.X + ((ix1 - src.X) * sx);
            float y0 = dst.Y + ((iy0 - src.Y) * sy);
            float y1 = dst.Y + ((iy1 - src.Y) * sy);
            if (flipX)
            {
                (x0, x1) = (dst.X + dst.W - (x1 - dst.X), dst.X + dst.W - (x0 - dst.X));
                (u0, u1) = (u1, u0);
            }

            Add(source.Tile(t.Index), x0, y0, x1, y1, u0, v0, u1, v1, color, blend);
        }

        return true;
    }

    /// <summary>
    /// Draws a one-tile sheet turned by <paramref name="radians"/> about its centre (the boot notice's spinner, draw flag 0x20; the rotation direction on screen is
    /// not settled, docs/formats/system_flow.md 8.11). Sheets of several tiles are drawn unturned.
    /// </summary>
    public bool SpriteRotated(UiSheet sheet, RectF src, RectF dst, float radians, float alpha = 1f, UiBlend blend = UiBlend.Alpha)
    {
        SheetGeometry? g = Assets.Geometry(sheet);
        if (g is null)
        {
            return false;
        }

        float a = Math.Clamp(alpha, 0f, 1f) * _alpha;
        if (g.Tiles.Count != 1 || a <= 0f)
        {
            return Sprite(sheet, src, dst, alpha, blend);
        }

        SheetTile t = g.Tiles[0];
        float u0 = (t.SourceX + (src.X - t.X)) / t.StoredWidth;
        float u1 = (t.SourceX + (src.Right - t.X)) / t.StoredWidth;
        float v0 = (t.SourceY + (src.Y - t.Y)) / t.StoredHeight;
        float v1 = (t.SourceY + (src.Bottom - t.Y)) / t.StoredHeight;
        float cx = dst.X + (dst.W / 2f);
        float cy = dst.Y + (dst.H / 2f);
        float cs = MathF.Cos(radians);
        float sn = MathF.Sin(radians);
        Vertex V(float dx, float dy, float u, float v) => new(cx + (dx * cs) - (dy * sn), cy + (dx * sn) + (dy * cs), u, v, Pack(a));
        float hw = dst.W / 2f;
        float hh = dst.H / 2f;
        int effect = (int)blend;
        var cmd = new DrawCmd
        {
            Kind = DrawKind.TexturedQuad,
            Texture = TextureRef.ForUiTile(sheet.Set, Assets.Language, sheet.Child, t.Index),
            V0 = V(-hw, -hh, u0, v0),
            V1 = V(hw, -hh, u1, v0),
            V2 = V(-hw, hh, u0, v1),
            V3 = V(hw, hh, u1, v1),
            EffectWord = (short)effect,
            BlendState = (byte)BlendStates[effect],
            Clip = _clips.Count > 0 ? _clips[^1] : null,
        };
        List.Add(in cmd);
        QuadCount++;
        return true;
    }

    /// <summary>Draws the whole <paramref name="sheet"/> stretched into <paramref name="dst"/>.</summary>
    public bool Sprite(UiSheet sheet, RectF dst, float alpha = 1f, UiBlend blend = UiBlend.Alpha)
    {
        SheetGeometry? g = Assets.Geometry(sheet);
        return g is not null && Sprite(sheet, new RectF(0, 0, g.Width, g.Height), dst, alpha, blend);
    }

    /// <summary>Draws a cell of a sheet at (<paramref name="x"/>, <paramref name="y"/>) in its own size (1 texel = 1 pixel).</summary>
    public bool SpriteAt(UiSheet sheet, RectF src, float x, float y, float alpha = 1f, UiBlend blend = UiBlend.Alpha) =>
        Sprite(sheet, src, new RectF(x, y, src.W, src.H), alpha, blend);

    /// <summary>Draws the whole sheet at (<paramref name="x"/>, <paramref name="y"/>) in its own size.</summary>
    public bool SpriteAt(UiSheet sheet, float x, float y, float alpha = 1f, UiBlend blend = UiBlend.Alpha)
    {
        SheetGeometry? g = Assets.Geometry(sheet);
        return g is not null && Sprite(sheet, new RectF(0, 0, g.Width, g.Height), new RectF(x, y, g.Width, g.Height), alpha, blend);
    }

    /// <summary>The size of a picture in texels, or (0, 0) when it does not exist.</summary>
    public (int W, int H) SizeOf(UiPicture picture)
    {
        SheetGeometry? g = Assets.Geometry(picture);
        return g is null ? (0, 0) : (g.Width, g.Height);
    }

    /// <summary>The size of a sheet in texels, or (0, 0) when it does not exist.</summary>
    public (int W, int H) SizeOf(UiSheet sheet)
    {
        SheetGeometry? g = Assets.Geometry(sheet);
        return g is null ? (0, 0) : (g.Width, g.Height);
    }

    /// <summary>Fills <paramref name="dst"/> with a colour (components 0..1) at alpha <paramref name="a"/>.</summary>
    public void Fill(RectF dst, float r, float g, float b, float a, UiBlend blend = UiBlend.Alpha)
    {
        float al = Math.Clamp(a, 0f, 1f) * _alpha;
        if (dst.IsEmpty || al <= 0f)
        {
            return;
        }

        uint bgr = ((uint)Math.Round(Math.Clamp(b, 0f, 1f) * 255f) << 16) | ((uint)Math.Round(Math.Clamp(g, 0f, 1f) * 255f) << 8) | (uint)Math.Round(Math.Clamp(r, 0f, 1f) * 255f);
        Add(TextureRef.None, dst.X, dst.Y, dst.Right, dst.Bottom, 0, 0, 0, 0, Pack(al, bgr), blend);
    }

    /// <summary>
    /// Fills a rectangle with rounded corners of <paramref name="radius"/> pixels (a full circle when the radius is half the shorter side). The edges are
    /// anti-aliased: every corner row is one full-coverage span plus one partly covered pixel on each side (package K9: the key caps and the Xbox glyphs).
    /// A shape that lies inside another one (the dark face of a key cap inside its border) passes <paramref name="smooth"/> false: its corner rows are plain
    /// spans, three times fewer quads.
    /// </summary>
    public void FillRounded(RectF dst, float radius, float r, float g, float b, float a, bool smooth = true)
    {
        float al = Math.Clamp(a, 0f, 1f) * _alpha;
        if (dst.IsEmpty || al <= 0f)
        {
            return;
        }

        float x0 = MathF.Round(dst.X);
        float y0 = MathF.Round(dst.Y);
        float w = MathF.Max(1f, MathF.Round(dst.Right) - x0);
        float h = MathF.Max(1f, MathF.Round(dst.Bottom) - y0);
        int rad = (int)MathF.Min(MathF.Round(radius), MathF.Floor(MathF.Min(w, h) / 2f));
        uint bgr = ((uint)Math.Round(Math.Clamp(b, 0f, 1f) * 255f) << 16) | ((uint)Math.Round(Math.Clamp(g, 0f, 1f) * 255f) << 8) | (uint)Math.Round(Math.Clamp(r, 0f, 1f) * 255f);
        void Span(float x, float y, float sw, float sh, float coverage)
        {
            if (sw > 0f && sh > 0f && coverage > 0f)
            {
                Add(TextureRef.None, x, y, x + sw, y + sh, 0, 0, 0, 0, Pack(al * coverage, bgr), UiBlend.Alpha);
            }
        }

        if (rad <= 0)
        {
            Span(x0, y0, w, h, 1f);
            return;
        }

        // the middle rows between the two corner bands
        Span(x0, y0 + rad, w, h - (2 * rad), 1f);
        for (int i = 0; i < rad; i++)
        {
            // distance of the row's centre line above the circle's centre, then where the circle's edge crosses that line
            float dy = rad - i - 0.5f;
            float inset = rad - MathF.Sqrt(MathF.Max(0f, (rad * rad) - (dy * dy)));
            float whole = smooth ? MathF.Ceiling(inset) : MathF.Round(inset);
            float edge = smooth ? whole - inset : 0f;         // coverage of the pixel the edge cuts through
            if (edge >= 0.85f)
            {
                // nearly covered: a full pixel of the span
                whole -= 1f;
                edge = 0f;
            }
            else if (edge < 0.15f)
            {
                edge = 0f;
            }
            Row(y0 + i);
            Row(y0 + h - 1 - i);

            void Row(float y)
            {
                Span(x0 + whole, y, w - (2 * whole), 1f, 1f);
                if (edge > 0f)
                {
                    Span(x0 + whole - 1f, y, 1f, 1f, edge);
                    Span(x0 + w - whole, y, 1f, 1f, edge);
                }
            }
        }
    }

    /// <summary>Covers the whole frame with <paramref name="gray"/> (0 black, 1 white) at alpha <paramref name="a"/>.</summary>
    public void Cover(float a, float gray = 0f) => Fill(UiMetrics.Screen, gray, gray, gray, a);

    /// <summary>
    /// Appends a quad with final canvas positions: <paramref name="texture"/> is a glyph page, a movie frame or none (a flat quad). The vertex colour
    /// <paramref name="rgba"/> is used as it is (the painter's alpha factor is applied to its alpha byte).
    /// </summary>
    public void Quad(TextureRef texture, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint rgba, UiBlend blend = UiBlend.Alpha)
    {
        uint a = (uint)Math.Round((rgba >> 24) * _alpha);
        Add(texture, x0, y0, x1, y1, u0, v0, u1, v1, (rgba & 0xFFFFFF) | (a << 24), blend);
    }

    /// <summary>Draws the thumbnail of save slot <paramref name="slot"/> (its <paramref name="version"/> names the pixels) into <paramref name="dst"/>.</summary>
    public void Thumbnail(int slot, int version, RectF dst, float alpha = 1f)
    {
        float a = Math.Clamp(alpha, 0f, 1f) * _alpha;
        if (!dst.IsEmpty && a > 0f)
        {
            Add(TextureRef.ForThumbnail(slot, version), dst.X, dst.Y, dst.Right, dst.Bottom, 0f, 0f, 1f, 1f, Pack(a), UiBlend.Alpha);
        }
    }

    /// <summary>
    /// Draws the part <paramref name="src"/> (texels) of the run-time picture <paramref name="id"/> (<see cref="UiComposedId"/>, package K33) into <paramref name="dst"/>
    /// through the same quad path as <see cref="Sprite(UiSheet, RectF, RectF, float, UiBlend, bool)"/>: the painter's alpha and clip apply, so a screen's fades treat it like any sheet cell.
    /// </summary>
    /// <returns>False when the picture could not be built from the player's sheets (the caller draws its fallback).</returns>
    public bool Composed(int id, RectF src, RectF dst, float alpha = 1f, UiBlend blend = UiBlend.Alpha)
    {
        UiComposedPicture? pic = Assets.Composed(id);
        if (pic is null)
        {
            return false;
        }

        float a = Math.Clamp(alpha, 0f, 1f) * _alpha;
        if (src.IsEmpty || dst.IsEmpty || a <= 0f)
        {
            return true;
        }

        Add(
            TextureRef.ForUiComposed(id, Assets.Language), dst.X, dst.Y, dst.Right, dst.Bottom,
            src.X / pic.Width, src.Y / pic.Height, src.Right / pic.Width, src.Bottom / pic.Height, Pack(a), blend);
        return true;
    }

    /// <summary>Packs alpha (0..1) and 0xBBGGRR colour bytes into a vertex colour (R in bits 0-7, A in bits 24-31).</summary>
    public static uint Pack(float alpha, uint bgr = 0xFFFFFF) => ((uint)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255f) << 24) | (bgr & 0xFFFFFF);

    private void Add(TextureRef texture, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint rgba, UiBlend blend)
    {
        int effect = (int)blend;
        var cmd = new DrawCmd
        {
            Kind = texture.Kind == TextureKind.None ? DrawKind.FillQuad : DrawKind.TexturedQuad,
            Texture = texture,
            V0 = new Vertex(x0, y0, u0, v0, rgba),
            V1 = new Vertex(x1, y0, u1, v0, rgba),
            V2 = new Vertex(x0, y1, u0, v1, rgba),
            V3 = new Vertex(x1, y1, u1, v1, rgba),
            EffectWord = (short)effect,
            BlendState = (byte)BlendStates[effect],
            Clip = _clips.Count > 0 ? _clips[^1] : null,
        };
        List.Add(in cmd);
        QuadCount++;
    }

    // docs/formats/compositing.md 5.1 (0x2D55E0): the fixed-function blend state of draw effect e.
    private static int[] BuildBlendStates()
    {
        var b = new List<int>();
        b.AddRange(Enumerable.Repeat(1, 26));
        b.AddRange([6, 1, 1, 6]);
        b.AddRange(Enumerable.Repeat(1, 22));
        b.AddRange([2, 7, 1, 10, 11, 5, 4, 9, 1, 1, 1, 2, 1, 13, 1, 1]);
        return [.. b];
    }

    /// <summary>Restores the alpha factor when disposed.</summary>
    public readonly struct AlphaScope : IDisposable
    {
        private readonly UiPainter _p;
        private readonly float _previous;

        internal AlphaScope(UiPainter p, float previous)
        {
            _p = p;
            _previous = previous;
        }

        /// <inheritdoc/>
        public void Dispose() => _p._alpha = _previous;
    }

    /// <summary>Restores the scissor when disposed.</summary>
    public readonly struct ClipScope : IDisposable
    {
        private readonly UiPainter _p;
        private readonly int _count;

        internal ClipScope(UiPainter p, int count)
        {
            _p = p;
            _count = count;
        }

        /// <inheritdoc/>
        public void Dispose() => _p._clips.RemoveRange(_count, _p._clips.Count - _count);
    }
}
