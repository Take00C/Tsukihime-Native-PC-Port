using System.Collections.Immutable;

namespace Tsuki.Engine.Contracts;

/// <summary>
/// The one interface through which the engine, the UI and the media layer read game data
/// (ARCHITECTURE 3.2, 3.3).
/// </summary>
/// <remarks>
/// <para>Two implementations exist, and they must return identical values (ARCHITECTURE 3.3):</para>
/// <list type="bullet">
/// <item><c>GameFolderSource</c> (Tsuki.Data, ships) reads the user's CUSA47999 folder with the C#
/// decoders.</item>
/// <item><c>GoldenSource</c> (Tsuki.Data, dev builds only, <c>#if TSUKI_DEV</c>) reads what
/// <c>tools/oracle</c> exported to <c>samples/golden/</c>.</item>
/// </list>
/// <para>Implementations only ever read the user's folder (ARCHITECTURE 1.2, 4.4), are safe to call
/// from the decode workers (ARCHITECTURE 3.4), and are keyed by the game's own ids
/// (ARCHITECTURE 1.10). This contract changes only through the lead (ROADMAP 1, stream L).</para>
/// </remarks>
public interface IGameData
{
    /// <summary>Identity of the copy, as established by the build check (RR 3.3, ARCHITECTURE 4.1).</summary>
    BuildInfo Build { get; }

    /// <summary>
    /// The selectable languages this copy's data carries (package K25): script text, glyph pages and UI sheets all present. The config screen and the launcher hide the
    /// others. The default is the four languages of the verified build (an implementation that predates the member, the golden slice).
    /// </summary>
    IReadOnlyList<Language> AvailableLanguages => [Language.Ja, Language.En, Language.Zc, Language.Zt];

    /// <summary>The tables that exist only in eboot.bin (RR 3.2, FORMATS 5.4).</summary>
    IEbootTables Eboot { get; }

    /// <summary>
    /// Number of scripts: the sections of allscr.mrg after the name and label tables
    /// (docs/formats/script.md 2.1-2.4; 549 in the retail data).
    /// </summary>
    int ScriptCount { get; }

    /// <summary>
    /// Script <paramref name="k"/>: section k + 3 of allscr.mrg after MZX0 and XOR 0xFF, with its
    /// segment offsets (docs/formats/script.md 2.1-2.2, 3.1; ARCHITECTURE 5.2).
    /// </summary>
    ScriptImage Script(int k);

    /// <summary>Number of lines per language in script_text.mrg (docs/formats/text.md 2; 43,969).</summary>
    int TextLineCount { get; }

    /// <summary>
    /// Raw text line <paramref name="id"/> in <paramref name="lang"/>: the bytes of the string blob
    /// with their markup, without the terminating NUL, and with <c>$</c> references not expanded
    /// (docs/formats/text.md 2, 4, 5.3, 6).
    /// </summary>
    ReadOnlyMemory<byte> TextLine(Language lang, int id);

    /// <summary>
    /// Glyph page <paramref name="page"/> of <c>ft_&lt;lang&gt;.mrg</c> (docs/formats/fonts.md 3;
    /// ARCHITECTURE 4.2, 6.4).
    /// </summary>
    FontPage FontPage(Language lang, int page);

    /// <summary>
    /// Picture <paramref name="entry"/> of image archive <paramref name="archive"/> (archive ids of
    /// docs/formats/archives_ui_movies.md 2.1: 0 allpac0 .. 5 allpac4), decoded to straight-alpha
    /// RGBA tiles (docs/formats/images.md 2-6). Colour effects (STEC/SNX, compositing 9) are not
    /// applied here.
    /// </summary>
    Picture Picture(int archive, int entry);

    /// <summary>
    /// Audio entry <paramref name="index"/> of <paramref name="bank"/>, decoded to float PCM with its
    /// loop (docs/formats/audio.md 2-6; ARCHITECTURE 7.2, 7.6).
    /// </summary>
    AudioClip Audio(AudioBank bank, int index);

    /// <summary>
    /// Full path of movie table record <paramref name="index"/> (docs/formats/archives_ui_movies.md
    /// 9.2), after the caller's language remap (9.3, ARCHITECTURE 7.5). Movies are streamed from the
    /// user's folder, never copied.
    /// </summary>
    string Movie(int index);

    /// <summary>
    /// Child <paramref name="child"/> of the UI sheet <paramref name="set"/> in the language variant
    /// <paramref name="lang"/> (docs/formats/archives_ui_movies.md 5.2, 7.3; ARCHITECTURE 9.1).
    /// </summary>
    Picture UiSheet(UiSet set, Language lang, int child);

    /// <summary>
    /// The displayed size of a UI sheet child (<see cref="Picture.Width"/> / <see cref="Picture.Height"/>) without decoding its pixels (package K8: the choice
    /// decoration and the message window need sizes only; decoding a sheet for them cost 20..100 ms on the main thread). The default decodes the sheet.
    /// </summary>
    (int Width, int Height) UiSheetSize(UiSet set, Language lang, int child)
    {
        Picture p = UiSheet(set, lang, child);
        return (p.Width, p.Height);
    }

    /// <summary>
    /// The size and tile windows of a UI sheet child with empty pixel arrays (package K8): what the menus need to place a sheet. The default decodes the sheet;
    /// <see cref="IGameData.UiSheet"/> stays the way to get pixels.
    /// </summary>
    Picture UiSheetLayout(UiSet set, Language lang, int child) => UiSheet(set, lang, child);

    /// <summary>
    /// Entry <paramref name="index"/> of <c>image/parts.mrg</c> decoded to a picture, or null when the archive has no such entry (package K6: the
    /// boot notices, <c>parts.mrg</c> #8 + language). An implementation without the archive throws <see cref="NotSupportedException"/>
    /// (the golden slice); the default does so for implementations that predate the member.
    /// </summary>
    Picture? Parts(int index) => throw new NotSupportedException("this IGameData does not serve parts.mrg");
}

/// <summary>
/// Game language index as the engine stores it (docs/formats/text.md 3; RR 4.19).
/// </summary>
public enum Language : byte
{
    /// <summary>Japanese (index 0).</summary>
    Ja = 0,

    /// <summary>English (index 1): no ruby, English word wrapping.</summary>
    En = 1,

    /// <summary>Simplified Chinese (index 2).</summary>
    Zc = 2,

    /// <summary>Traditional Chinese (index 3).</summary>
    Zt = 3,

    /// <summary>Korean (index 4): placeholder data only, never selectable (text.md 3).</summary>
    Ko = 4,
}

/// <summary>Identity of the user's copy (RR 3.3).</summary>
/// <param name="Source">Where the data comes from: the game folder, or the golden export directory.</param>
/// <param name="EbootSize">Size of eboot.bin in bytes.</param>
/// <param name="EbootSha256">SHA-256 of eboot.bin as 64 lower-case hex digits.</param>
public sealed record BuildInfo(string Source, long EbootSize, string EbootSha256)
{
    /// <summary>Size of the analysed eboot.bin (RR 3.3 item 1).</summary>
    public const long AnalysedEbootSize = 12_767_802;

    /// <summary>SHA-256 of the analysed eboot.bin (RR 3.3 item 1).</summary>
    public const string AnalysedEbootSha256 = "c2843415fdfda5ebbc84f4d55c6cca044a7476436cf0cfdfccac83116beeb918";

    /// <summary>True when every eboot address in the documents applies to this copy (RR 3.3).</summary>
    public bool IsAnalysedBuild =>
        EbootSize == AnalysedEbootSize && string.Equals(EbootSha256, AnalysedEbootSha256, StringComparison.Ordinal);
}

/// <summary>
/// The eboot-only tables (RR 3.2, FORMATS 5.4): picture names, image catalog, scene ids, movie table
/// and prologue remap, motion tables, font lookup and rectangles, layout and window-picture tables,
/// UI texture registration, rule-mask names, blend and effect tables, config defaults, history
/// F-index lists, and later <c>ui_text</c>, title backgrounds, music mode, BGM names and the gallery
/// lists.
/// </summary>
/// <remarks>
/// Members are added by the lead as stream A ports each extractor (ROADMAP 1); each one cites its
/// RR 3.2 / FORMATS 5.4 entry.
/// </remarks>
public partial interface IEbootTables
{
}

/// <summary>
/// One decoded script (docs/formats/script.md 2.2, 3.1): the bytes the VM runs, plus the start
/// offset of every segment.
/// </summary>
public sealed class ScriptImage
{
    /// <summary>Creates a script image.</summary>
    /// <param name="index">Script number k (allscr.mrg section k + 3).</param>
    /// <param name="bytes">The script after MZX0 and XOR 0xFF.</param>
    /// <param name="segmentStarts">Start offset of every segment, ascending, the first one 0.</param>
    public ScriptImage(int index, ReadOnlyMemory<byte> bytes, ImmutableArray<int> segmentStarts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (segmentStarts.IsDefault)
        {
            throw new ArgumentException("segmentStarts must be initialised", nameof(segmentStarts));
        }

        Index = index;
        Bytes = bytes;
        SegmentStarts = segmentStarts;
    }

    /// <summary>Script number k.</summary>
    public int Index { get; }

    /// <summary>The script bytes (docs/formats/script.md 2.2).</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>
    /// Start offset of every segment in ascending order. A segment ends just after the first
    /// <c>;</c> within 0x300 bytes, or after 0x300 bytes (docs/formats/script.md 3.1, engine.md 5.2).
    /// </summary>
    public ImmutableArray<int> SegmentStarts { get; }

    /// <summary>End offset (exclusive) of segment <paramref name="i"/>.</summary>
    public int SegmentEnd(int i) => i + 1 < SegmentStarts.Length ? SegmentStarts[i + 1] : Bytes.Length;
}

/// <summary>A rectangle in pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Tile flag byte of an image MZP (docs/formats/images.md 2.3).</summary>
public enum TileFlags : byte
{
    /// <summary>Flag 0: no texture is built and the tile is never displayed.</summary>
    Empty = 0,

    /// <summary>Flag 1: "may contain transparency"; decoded and blended.</summary>
    MayBeTransparent = 1,

    /// <summary>Flag 2: "opaque" hint; decoded and still blended with its alpha.</summary>
    Opaque = 2,
}

/// <summary>
/// A decoded picture: straight-alpha RGBA8 tiles (docs/formats/images.md 2.2-2.3, 6;
/// compositing.md 4.2).
/// </summary>
/// <param name="width">Displayed width (images.md 2.2 <c>out_w</c>).</param>
/// <param name="height">Displayed height (images.md 2.2 <c>out_h</c>).</param>
/// <param name="tiles">Every tile in row-major order, including empty ones.</param>
public sealed class Picture(int width, int height, ImmutableArray<PictureTile> tiles)
{
    /// <summary>Displayed width in pixels.</summary>
    public int Width { get; } = width;

    /// <summary>Displayed height in pixels.</summary>
    public int Height { get; } = height;

    /// <summary>Tiles in row-major order (images.md 2.2: <c>tx = t % tiles_x</c>).</summary>
    public ImmutableArray<PictureTile> Tiles { get; } = tiles;
}

/// <summary>One stored tile of a <see cref="Picture"/> (docs/formats/images.md 2.2-2.3).</summary>
/// <param name="index">Tile number t, row-major.</param>
/// <param name="flags">The tile flag byte.</param>
/// <param name="storedWidth">Stored tile width, border included.</param>
/// <param name="storedHeight">Stored tile height, border included.</param>
/// <param name="source">The displayed part of the stored tile: the inner rectangle, clipped to the displayed size.</param>
/// <param name="x">Where <paramref name="source"/> lands in the displayed picture (x).</param>
/// <param name="y">Where <paramref name="source"/> lands in the displayed picture (y).</param>
/// <param name="rgba">storedWidth x storedHeight x 4 bytes, straight alpha, top-down; empty for <see cref="TileFlags.Empty"/>.</param>
public sealed class PictureTile(
    int index, TileFlags flags, int storedWidth, int storedHeight, PixelRect source, int x, int y, ReadOnlyMemory<byte> rgba)
{
    /// <summary>Tile number t, row-major.</summary>
    public int Index { get; } = index;

    /// <summary>The tile flag byte (images.md 2.3).</summary>
    public TileFlags Flags { get; } = flags;

    /// <summary>Stored tile width, border included (images.md 2.2).</summary>
    public int StoredWidth { get; } = storedWidth;

    /// <summary>Stored tile height, border included (images.md 2.2).</summary>
    public int StoredHeight { get; } = storedHeight;

    /// <summary>The displayed part of the stored tile; the border pixels around it are the filter apron (ARCHITECTURE 4.3).</summary>
    public PixelRect Source { get; } = source;

    /// <summary>Displayed x position of <see cref="Source"/> in the picture.</summary>
    public int X { get; } = x;

    /// <summary>Displayed y position of <see cref="Source"/> in the picture.</summary>
    public int Y { get; } = y;

    /// <summary>Straight-alpha RGBA8 pixels of the whole stored tile, top-down; empty for empty tiles.</summary>
    public ReadOnlyMemory<byte> Rgba { get; } = rgba;
}

/// <summary>
/// One glyph page of <c>ft_&lt;lang&gt;.mrg</c>: 1024 x 1024, fill images in the upper half and the
/// same images dilated (outline) 512 px lower (docs/formats/fonts.md 3, 3.4).
/// </summary>
/// <param name="language">Language of the font archive.</param>
/// <param name="page">Page number (fonts.md 3.5).</param>
/// <param name="width">Page width (1024).</param>
/// <param name="height">Page height (1024).</param>
/// <param name="rgba">width x height x 4 bytes, straight alpha, top-down.</param>
public sealed class FontPage(Language language, int page, int width, int height, ReadOnlyMemory<byte> rgba)
{
    /// <summary>Language of the font archive.</summary>
    public Language Language { get; } = language;

    /// <summary>Page number.</summary>
    public int Page { get; } = page;

    /// <summary>Page width in pixels.</summary>
    public int Width { get; } = width;

    /// <summary>Page height in pixels.</summary>
    public int Height { get; } = height;

    /// <summary>Straight-alpha RGBA8 pixels, top-down.</summary>
    public ReadOnlyMemory<byte> Rgba { get; } = rgba;
}

/// <summary>Audio banks (docs/formats/audio.md 1).</summary>
public enum AudioBank : byte
{
    /// <summary>bgm.hed/.mrg: 132 ATRAC9 tracks; script track number = entry index.</summary>
    Bgm = 0,

    /// <summary>vse.hed/.mrg: 1,327 ATRAC9 sound effects (names in se_nam.mrg).</summary>
    Se = 1,

    /// <summary>voice.hed/.nam/.mrg: 16,464 LV + MZX0 wrapped ATRAC9 voices (HED4 index).</summary>
    Voice = 2,

    /// <summary>voice2.hed/.nam/.mrg: 2 silent entries.</summary>
    Voice2 = 3,

    /// <summary>vag.hed/.mrg: 19 HE-VAG system sounds (audio.md 6).</summary>
    Vag = 4,
}

/// <summary>A loop in playable sample frames, end inclusive (docs/formats/audio.md 2.3-2.4; ARCHITECTURE 7.2).</summary>
/// <param name="Start">First frame of the loop: <c>smpl.loop_start - fact[2]</c>.</param>
/// <param name="End">Last frame of the loop, inclusive: <c>smpl.loop_end - fact[2]</c>.</param>
public readonly record struct AudioLoop(int Start, int End)
{
    /// <summary>Loop length in frames (<c>End - Start + 1</c>).</summary>
    public int Length => End - Start + 1;
}

/// <summary>
/// Decoded audio: interleaved float32 PCM at 48 kHz, cut to the playable range
/// <c>decoded[fact[2] : fact[2] + fact[0]]</c> (docs/formats/audio.md 2.3; ARCHITECTURE 7.2).
/// Samples are never clipped: SE peaks reach 1.878 of full scale (ARCHITECTURE 7.1).
/// </summary>
/// <param name="bank">The bank the entry comes from.</param>
/// <param name="index">The entry index in its bank.</param>
/// <param name="channels">1 or 2.</param>
/// <param name="sampleRate">48,000 for every bank (audio.md 1).</param>
/// <param name="samples">Interleaved float PCM of the playable range.</param>
/// <param name="loop">The smpl loop; null when there is none or when start == end (audio.md 2.3).</param>
public sealed class AudioClip(AudioBank bank, int index, int channels, int sampleRate, ReadOnlyMemory<float> samples, AudioLoop? loop)
{
    /// <summary>The bank the entry comes from.</summary>
    public AudioBank Bank { get; } = bank;

    /// <summary>The entry index in its bank.</summary>
    public int Index { get; } = index;

    /// <summary>Channel count (1 or 2).</summary>
    public int Channels { get; } = channels;

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; } = sampleRate;

    /// <summary>Interleaved float PCM of the playable range.</summary>
    public ReadOnlyMemory<float> Samples { get; } = samples;

    /// <summary>Number of sample frames (<c>fact[0]</c> for ATRAC9 entries).</summary>
    public int FrameCount => Channels == 0 ? 0 : Samples.Length / Channels;

    /// <summary>
    /// The file's loop. Whether it is used is decided by the caller (the <c>_SEPL</c> loop flag, the
    /// BGM slot flag; audio.md 2.4).
    /// </summary>
    public AudioLoop? Loop { get; } = loop;
}

/// <summary>
/// Decodes one audio entry's payload to PCM (docs/formats/audio.md 2, 6; ARCHITECTURE 7.2, 7.6).
/// </summary>
/// <remarks>
/// Implemented in Tsuki.Media (FFmpeg <c>atrac9</c> for RIFF/WAVE, the C# HE-VAG port for vag) and
/// handed to Tsuki.Data's <c>GameFolderSource</c>, because Tsuki.Data makes no P/Invoke and
/// references no native package (ARCHITECTURE 3.1).
/// </remarks>
public interface IAudioDecoder
{
    /// <summary>Decodes one entry.</summary>
    /// <param name="bank">The bank of the entry.</param>
    /// <param name="index">The entry index.</param>
    /// <param name="payload">The RIFF/WAVE file (bgm, vse; voice after the LV + MZX0 unwrap of audio.md 5.1) or the VAGp file (vag).</param>
    AudioClip Decode(AudioBank bank, int index, ReadOnlyMemory<byte> payload);
}

/// <summary>
/// UI sheet sets of allpac3 by their eboot names (docs/formats/archives_ui_movies.md 5.2). The
/// numeric values are ours (the table order of 5.2), not eboot ids.
/// </summary>
public enum UiSet : byte
{
    /// <summary>CONF_PARTS: config screen.</summary>
    ConfParts,

    /// <summary>DISP_PARTS: in-game HUD, message windows, control panel.</summary>
    DispParts,

    /// <summary>MENU_PARTS: system menu.</summary>
    MenuParts,

    /// <summary>MODEPNL_PARTS: mode panel bar.</summary>
    ModePnlParts,

    /// <summary>SAVE_PARTS: save and load.</summary>
    SaveParts,

    /// <summary>TITLE_PARTS: title screen.</summary>
    TitleParts,

    /// <summary>SELECT_PARTS: choice screen frames (no language).</summary>
    SelectParts,

    /// <summary>SELECTTXT_PARTS: pre-rendered Japanese choice texts (loaded, never drawn; system_flow 5.4).</summary>
    SelectTxtParts,

    /// <summary>BORDER_PARTS: frame and border textures.</summary>
    BorderParts,

    /// <summary>EFFECT_PARTS: dummies and a 1920 x 128 strip.</summary>
    EffectParts,

    /// <summary>NOISE_PARTS: two 1920 x 1080 noise textures.</summary>
    NoiseParts,

    /// <summary>GALLERY_PARTS_COMMON.</summary>
    GalleryPartsCommon,

    /// <summary>GALLERY_PARTS_CG.</summary>
    GalleryPartsCg,

    /// <summary>GALLERY_PARTS_CG_TH: CG thumbnails.</summary>
    GalleryPartsCgTh,

    /// <summary>GALLERY_PARTS_MUSIC.</summary>
    GalleryPartsMusic,

    /// <summary>GALLERY_PARTS_FC: flowchart.</summary>
    GalleryPartsFc,

    /// <summary>GALLERY_PARTS_FC_TH: flowchart scene thumbnails.</summary>
    GalleryPartsFcTh,

    /// <summary>GALLERY_PARTS_MOVIE.</summary>
    GalleryPartsMovie,

    /// <summary>GALLERY_PARTS_ENDING.</summary>
    GalleryPartsEnding,
}
