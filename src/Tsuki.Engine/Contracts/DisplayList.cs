namespace Tsuki.Engine.Contracts;

/// <summary>
/// Kind of a <see cref="DrawCmd"/>. The values are the PS4 display-list record types
/// (docs/formats/compositing.md 6). The PS4's draw-mode (2), nested-list (9) and projection (12)
/// records are folded into each command: every command carries its own effect word, blend state
/// and already projected vertices.
/// </summary>
public enum DrawKind : byte
{
    /// <summary>Textured quad (record type 1).</summary>
    TexturedQuad = 1,

    /// <summary>
    /// Copy the whole current render target into capture surface <c>Texture.A</c> (0..15) at this
    /// point of the frame (record type 6). It does not change the render target.
    /// </summary>
    Capture = 6,

    /// <summary>Untextured quad with per-vertex colour (record type 7, fills).</summary>
    FillQuad = 7,

    /// <summary>
    /// Clears <see cref="DrawCmd.Target"/> to the colour of <see cref="DrawCmd.V0"/> (<c>Rgba</c>, R in bits 0-7). Not a PS4 record: the PS4 clears
    /// its offscreen surfaces with a fill (package K7: the offscreen mask / scratch targets of the stage and the screen effects).
    /// </summary>
    Clear = 8,
}

/// <summary>How a quad's fragment shader differs from the plain one (package K7; additive to the PS4 record set).</summary>
public enum QuadPass : byte
{
    /// <summary>The shader the draw-mode word selects.</summary>
    None = 0,

    /// <summary>
    /// The STRA heat haze (docs/formats/layers.md 4.2, knife stage.rs <c>Haze::params</c>): the texture coordinates are displaced by two waves along
    /// the picture's v axis; <see cref="QuadFx"/> carries (amplitude, waves, phase, v offset) for x in <c>A0..A3</c> and for y in <c>B0..B3</c>
    /// (<c>B3</c> is the v scale: wave coordinate = <c>A3 + B3 * uv.y</c>).
    /// </summary>
    Haze = 1,

    /// <summary>The rule-mask shader (compositing.md 2.4): <c>A0, A1, A2</c> = the transition parameters (lo, hi, invert) of this quad, instead of those of its texture.</summary>
    Rule = 2,

    /// <summary>
    /// One separable gaussian blur pass (package K16; the system menu's blurred backdrop, knife-render <c>fs_blur</c> mode Uv): the colour is the weighted mean of
    /// the texture sampled at <c>uv + (A0, A1) * k</c> for k = -n..n with weights <c>exp(-k^2 / (2 sigma^2))</c>, <c>A2</c> = sigma (in taps), <c>A3</c> = n.
    /// </summary>
    Blur = 3,
}

/// <summary>
/// The optional shader inputs of a quad (package K7). The default value changes nothing; every field is additive to the PS4 record.
/// </summary>
/// <param name="Pass">The shader variant.</param>
/// <param name="Mask">0 for none, else the render target whose alpha multiplies the quad's alpha at each pixel (the stage's offscreen mask).</param>
/// <param name="Nearest">True to sample the texture with nearest-texel filtering (the mosaic of the engine screens).</param>
/// <param name="A0">Pass parameter 0.</param>
/// <param name="A1">Pass parameter 1.</param>
/// <param name="A2">Pass parameter 2.</param>
/// <param name="A3">Pass parameter 3.</param>
/// <param name="B0">Pass parameter 4.</param>
/// <param name="B1">Pass parameter 5.</param>
/// <param name="B2">Pass parameter 6.</param>
/// <param name="B3">Pass parameter 7.</param>
public readonly record struct QuadFx(
    QuadPass Pass = QuadPass.None,
    byte Mask = 0,
    bool Nearest = false,
    float A0 = 0f,
    float A1 = 0f,
    float A2 = 0f,
    float A3 = 0f,
    float B0 = 0f,
    float B1 = 0f,
    float B2 = 0f,
    float B3 = 0f)
{
    /// <summary>The rule-mask shader with the given transition parameters.</summary>
    public static QuadFx ForRule(float lo, float hi, float invert, byte mask = 0) => new(QuadPass.Rule, mask, false, lo, hi, invert);

    /// <summary>A quad multiplied by the alpha of render target <paramref name="target"/>.</summary>
    public static QuadFx Masked(byte target) => new(Mask: target);
}

/// <summary>What a <see cref="TextureRef"/> points at.</summary>
public enum TextureKind : byte
{
    /// <summary>No texture (fills).</summary>
    None = 0,

    /// <summary>A picture tile: A = archive, B = entry, C = tile, D = colour-effect key (STEC/SNX, compositing 9; 0 = none).</summary>
    PictureTile = 1,

    /// <summary>A UI sheet tile: A = <see cref="UiSet"/>, B = <see cref="Language"/>, C = child, D = tile.</summary>
    UiTile = 2,

    /// <summary>A glyph page: A = <see cref="Language"/>, B = page (fonts.md 3; ARCHITECTURE 6.4).</summary>
    GlyphPage = 3,

    /// <summary>A 1920 x 1080 capture surface: A = surface 0..15 (compositing 6; ARCHITECTURE 6.3).</summary>
    Capture = 4,

    /// <summary>The current frame (Y, U, V planes) of a movie: A = movie slot (ARCHITECTURE 7.5).</summary>
    MovieFrame = 5,

    /// <summary>A save-slot thumbnail of the menus: A = slot, B = version (package K5a; the pixels come from the UI services, not from the game data).</summary>
    Thumbnail = 6,

    /// <summary>
    /// A picture the menus compose at run time from the player's own UI sheets (package K33: the EXIT item of the title, cut from the letters of the other
    /// items): A = the composed picture's id, B = <see cref="Language"/>, C = version. The pixels come from the UI's own cache, never from a file.
    /// </summary>
    UiComposed = 7,
}

/// <summary>
/// A texture reference by game ids (ARCHITECTURE 1.10, 3.2). The engine resolves the PS4 texture
/// slots (archives_ui_movies.md 2.1) to the picture they hold when it builds the list, so the
/// executor never tracks slot state.
/// </summary>
/// <param name="Kind">What the reference points at; the meaning of A..D is given per <see cref="TextureKind"/>.</param>
/// <param name="A">First id.</param>
/// <param name="B">Second id.</param>
/// <param name="C">Third id.</param>
/// <param name="D">Fourth id.</param>
public readonly record struct TextureRef(TextureKind Kind, int A = 0, int B = 0, int C = 0, int D = 0)
{
    /// <summary>No texture.</summary>
    public static TextureRef None => default;

    /// <summary>A picture tile with an optional colour-effect key.</summary>
    public static TextureRef ForPictureTile(int archive, int entry, int tile, int colourEffect = 0) =>
        new(TextureKind.PictureTile, archive, entry, tile, colourEffect);

    /// <summary>A UI sheet tile.</summary>
    public static TextureRef ForUiTile(UiSet set, Language language, int child, int tile) =>
        new(TextureKind.UiTile, (int)set, (int)language, child, tile);

    /// <summary>A glyph page.</summary>
    public static TextureRef ForGlyphPage(Language language, int page) =>
        new(TextureKind.GlyphPage, (int)language, page);

    /// <summary>A capture surface (0..15).</summary>
    public static TextureRef ForCapture(int surface) => new(TextureKind.Capture, surface);

    /// <summary>The current frame of the movie in <paramref name="slot"/>.</summary>
    public static TextureRef ForMovieFrame(int slot) => new(TextureKind.MovieFrame, slot);

    /// <summary>The thumbnail of save slot <paramref name="slot"/> in its <paramref name="version"/> (a changed save is a new version).</summary>
    public static TextureRef ForThumbnail(int slot, int version) => new(TextureKind.Thumbnail, slot, version);

    /// <summary>The run-time picture <paramref name="id"/> the menus composed from the sheets of <paramref name="language"/> (package K33).</summary>
    public static TextureRef ForUiComposed(int id, Language language, int version = 0) => new(TextureKind.UiComposed, id, (int)language, version);
}

/// <summary>
/// One quad vertex (docs/formats/compositing.md 6: payload 4 x (u, v, rgba8, x, y)).
/// </summary>
/// <param name="X">Position in pixels of the 1920 x 1080 target, after the projection of compositing.md 6.</param>
/// <param name="Y">Position in pixels of the 1920 x 1080 target, after the projection.</param>
/// <param name="U">Texture coordinate.</param>
/// <param name="V">Texture coordinate.</param>
/// <param name="Rgba">Vertex colour, packed little-endian: R in bits 0-7, G 8-15, B 16-23, A 24-31.</param>
public readonly record struct Vertex(float X, float Y, float U, float V, uint Rgba);

/// <summary>
/// One display-list command (ARCHITECTURE 3.2, 6.1; docs/formats/compositing.md 4-6).
/// </summary>
public readonly record struct DrawCmd
{
    /// <summary>The command kind.</summary>
    public DrawKind Kind { get; init; }

    /// <summary>Render target: 0 is the 1920 x 1080 frame; other values are offscreen surfaces of masked or offscreen layers (layers.md 9).</summary>
    public byte Target { get; init; }

    /// <summary>The texture (quads), or the destination surface (<see cref="DrawKind.Capture"/>).</summary>
    public TextureRef Texture { get; init; }

    /// <summary>Vertex 0, in the PS4 payload order.</summary>
    public Vertex V0 { get; init; }

    /// <summary>Vertex 1.</summary>
    public Vertex V1 { get; init; }

    /// <summary>Vertex 2.</summary>
    public Vertex V2 { get; init; }

    /// <summary>Vertex 3.</summary>
    public Vertex V3 { get; init; }

    /// <summary>The draw-mode word (compositing.md 5.1): effect <c>e = low byte</c> selects shader mode and strength.</summary>
    public short EffectWord { get; init; }

    /// <summary>Fixed-function blend state 0..13 (compositing.md 4.1), resolved from the effect word as in 5.1.</summary>
    public byte BlendState { get; init; }

    /// <summary>Scissor rectangle in target pixels, or null for none.</summary>
    public PixelRect? Clip { get; init; }

    /// <summary>Optional shader inputs (haze, rule mask, offscreen mask, filter); the default changes nothing (package K7).</summary>
    public QuadFx Fx { get; init; }
}

/// <summary>
/// The commands of one 60 Hz step in PS4 order: scene layers by z, then the message window and HUD,
/// the choice screen and the UI (ARCHITECTURE 3.2, 6.1). It is filled by its producer and then
/// sealed; a sealed list is immutable. Entries are structs in a reused array, so steady-state frames
/// do not allocate (ARCHITECTURE 4.3).
/// </summary>
public sealed class DisplayList
{
    private DrawCmd[] _items;
    private int _count;

    /// <summary>Creates an empty, writable list.</summary>
    /// <param name="capacity">Initial capacity.</param>
    public DisplayList(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new DrawCmd[capacity];
    }

    /// <summary>The logic step (60 Hz frame counter) that produced the list; -1 before the first <see cref="Reset"/>.</summary>
    public long Frame { get; private set; } = -1;

    /// <summary>True once <see cref="Seal"/> has been called.</summary>
    public bool IsSealed { get; private set; }

    /// <summary>Number of commands.</summary>
    public int Count => _count;

    /// <summary>The commands in draw order.</summary>
    public ReadOnlySpan<DrawCmd> Commands => _items.AsSpan(0, _count);

    /// <summary>Empties the list for a new step and makes it writable again (producer only).</summary>
    public void Reset(long frame)
    {
        _count = 0;
        Frame = frame;
        IsSealed = false;
    }

    /// <summary>Appends a command.</summary>
    /// <exception cref="InvalidOperationException">The list is sealed.</exception>
    public void Add(in DrawCmd command)
    {
        if (IsSealed)
        {
            throw new InvalidOperationException("the display list is sealed");
        }

        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        _items[_count++] = command;
    }

    /// <summary>Makes the list immutable.</summary>
    public void Seal() => IsSealed = true;
}

/// <summary>
/// Double buffer of <see cref="DisplayList"/>s (ARCHITECTURE 3.2, 3.4): the logic step fills the
/// back list and publishes it; the executor draws the newest published list.
/// </summary>
/// <remarks>
/// Producer and executor run on the main thread (ARCHITECTURE 3.4). <see cref="Front"/> stays
/// valid until the next <see cref="BeginFrame"/>.
/// </remarks>
public sealed class DisplayListBuffer
{
    private DisplayList _front = new();
    private DisplayList _back = new();

    /// <summary>Creates a buffer whose front list is empty and sealed.</summary>
    public DisplayListBuffer() => _front.Seal();

    /// <summary>The newest published list (sealed).</summary>
    public DisplayList Front => Volatile.Read(ref _front);

    /// <summary>Resets the back list for <paramref name="frame"/> and returns it for filling.</summary>
    public DisplayList BeginFrame(long frame)
    {
        _back.Reset(frame);
        return _back;
    }

    /// <summary>Seals the back list and makes it the front list.</summary>
    public void Publish()
    {
        _back.Seal();
        _back = Interlocked.Exchange(ref _front, _back);
    }
}
