#if TSUKI_DEV
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Golden;

/// <summary>
/// <see cref="IGameData"/> over the golden slice (<c>samples/golden/slice</c>, written by <c>python -m oracle.run slice</c> from
/// the user's own game files; ARCHITECTURE 3.3). It exists in dev builds only (<c>TSUKI_DEV</c>) and lets the VM, text, render
/// and audio streams run before the C# decoders read the game folder.
/// </summary>
/// <remarks>
/// <para>
/// The slice is small: scripts 2 and 3, text lines 0..7 in ja and en, 65 font pages of ft_ja and ft_en, the 18 pictures the
/// traced statements display plus the four CAUTION screens, nine voices, one sound effect, one BGM track and the eboot tables
/// the slice needs. What the slice does not hold throws <see cref="GoldenSliceMissingException"/>
/// (a <see cref="KeyNotFoundException"/>); an argument outside the game's own range throws
/// <see cref="ArgumentOutOfRangeException"/>. <see cref="Contents"/> lists what is there.
/// </para>
/// <para>
/// Values are returned exactly as the oracle wrote them: the script bytes after MZX0 and XOR 0xFF with the statement offsets as
/// segment starts; <see cref="TextLine"/> as the engine copies a line (CR LF removed, at most 0x2FF bytes; text.md 2, the
/// exporter's <c>engine_line_hex</c>), with the raw blob bytes in <see cref="RawTextLine"/>; font pages as RGBA built from the
/// 8-bit coverage planes; picture tiles as straight-alpha RGBA of the stored tile with the clip rectangle of the oracle's
/// <c>copy</c> record; audio as the float PCM of the playable range with the loop in playable frames (end inclusive).
/// Nothing large is cached, every call returns a fresh array, and all methods are safe to call from several threads.
/// </para>
/// </remarks>
public sealed class GoldenSource : IGameData, IDisposable
{
    private readonly bool _ownsSlice;
    private readonly string? _explicitGameDirectory;
    private readonly Lazy<int> _scriptCount;
    private readonly Lazy<TextSet> _text;
    private readonly ConcurrentDictionary<int, Lazy<ScriptImage>> _scripts = new();
    private readonly ConcurrentDictionary<Language, Lazy<FontFile>> _fonts = new();
    private bool _disposed;

    /// <summary>Creates a source over an opened slice.</summary>
    /// <param name="slice">The slice.</param>
    /// <param name="gameDirectory">
    /// The user's game folder, needed only to turn the movie table's <c>/app0/</c> paths into full paths; null to look for it
    /// (<c>TSUKI_GAME_DIR</c>, then the folder the export was made from).
    /// </param>
    /// <param name="ownsSlice">True to dispose <paramref name="slice"/> with this source.</param>
    public GoldenSource(GoldenSlice slice, string? gameDirectory = null, bool ownsSlice = false)
    {
        ArgumentNullException.ThrowIfNull(slice);
        Slice = slice;
        _ownsSlice = ownsSlice;
        _explicitGameDirectory = gameDirectory;
        Tables = new GoldenEbootTables(slice);
        _scriptCount = new(ReadScriptCount, LazyThreadSafetyMode.ExecutionAndPublication);
        _text = new(ReadText, LazyThreadSafetyMode.ExecutionAndPublication);
        GoldenGame game = slice.Manifest.Game;
        if (game.EbootSize is not { } size || game.EbootSha256 is not { } sha)
        {
            throw new GoldenFormatException($"{slice.Directory}: the manifest has no game.eboot size and SHA-256");
        }

        Build = new BuildInfo(slice.Directory, size, sha);
        Contents = GoldenSliceContents.Scan(slice.Manifest);
    }

    /// <summary>The slice folder and its manifest.</summary>
    public GoldenSlice Slice { get; }

    /// <summary>What the slice holds (derived from the manifest and the text file).</summary>
    public GoldenSliceContents Contents { get; }

    /// <summary>The eboot tables with their typed accessors (the same object as <see cref="Eboot"/>).</summary>
    public GoldenEbootTables Tables { get; }

    /// <summary>
    /// Identity of the copy the slice was exported from: <see cref="BuildInfo.Source"/> is the slice folder, size and SHA-256 are the
    /// manifest's <c>game.eboot</c>.
    /// </summary>
    public BuildInfo Build { get; }

    /// <inheritdoc />
    public IEbootTables Eboot => Tables;

    /// <summary>
    /// The game folder used to resolve movie paths: the one given to the constructor, else <c>TSUKI_GAME_DIR</c>, else the folder
    /// the export was made from, each only if it holds <c>eboot.bin</c>; null when none does.
    /// </summary>
    public string? GameDirectory
    {
        get
        {
            foreach (string? candidate in new[] { _explicitGameDirectory, Environment.GetEnvironmentVariable("TSUKI_GAME_DIR"), Slice.Manifest.Game.Dir })
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(Path.Combine(candidate, "eboot.bin")))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return null;
        }
    }

    /// <inheritdoc />
    public int ScriptCount => _scriptCount.Value;

    /// <inheritdoc />
    public int TextLineCount => _text.Value.Count;

    /// <summary>Languages whose lines the slice holds (ja and en), ascending.</summary>
    public ImmutableArray<Language> TextLanguages => [.. _text.Value.Languages.Keys.Order()];

    /// <summary>The line ids the slice holds in every text language (0..7), ascending.</summary>
    public ImmutableArray<int> TextIds => [.. _text.Value.Languages.Values.First().Engine.Keys];

    /// <summary>
    /// Opens the slice and creates a source that owns it.
    /// </summary>
    /// <param name="sliceDirectory">The slice folder; null to find <c>samples/golden/slice</c> above the program.</param>
    /// <param name="gameDirectory">See <see cref="GoldenSource(GoldenSlice, string?, bool)"/>.</param>
    /// <param name="verify">How files are checked against the manifest when first opened.</param>
    public static GoldenSource Open(string? sliceDirectory = null, string? gameDirectory = null, GoldenVerifyMode verify = GoldenVerifyMode.Size)
    {
        GoldenSlice slice = GoldenSlice.Open(sliceDirectory, verify);
        try
        {
            return new GoldenSource(slice, gameDirectory, ownsSlice: true);
        }
        catch
        {
            slice.Dispose();
            throw;
        }
    }

    // -- scripts ------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public ScriptImage Script(int k)
    {
        ThrowIfDisposed();
        if (k < 0 || k >= ScriptCount)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, $"script numbers are 0..{ScriptCount - 1}");
        }

        if (!Contents.HasScript(k))
        {
            throw new GoldenSliceMissingException($"script {k}", "scripts " + string.Join(", ", Contents.Scripts));
        }

        return _scripts.GetOrAdd(k, key => new Lazy<ScriptImage>(() => ReadScript(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private ScriptImage ReadScript(int k)
    {
        string stem = $"scripts/script_{k:D4}";
        string jsonPath = stem + ".json";
        using JsonDocument doc = Slice.ReadJson(jsonPath);
        JsonElement root = doc.RootElement;
        JsonElement decoded = root.Obj("decoded", jsonPath);
        string binPath = "scripts/" + decoded.Str("file", jsonPath);
        byte[] bytes = Slice.ReadAll(binPath);
        if (bytes.Length != decoded.Long("size", jsonPath))
        {
            throw new GoldenFormatException($"{binPath}: {bytes.Length} bytes, the header says {decoded.Long("size", jsonPath)}");
        }

        JsonElement statements = root.Arr("statements", jsonPath);
        var starts = ImmutableArray.CreateBuilder<int>(statements.GetArrayLength());
        int previousEnd = 0;
        foreach (JsonElement s in statements.EnumerateArray())
        {
            int offset = s.Int("offset", jsonPath);
            int end = s.Int("end", jsonPath);
            if (offset != previousEnd || end <= offset)
            {
                throw new GoldenFormatException($"{jsonPath}: statement {starts.Count} spans {offset}..{end}, expected to start at {previousEnd}");
            }

            starts.Add(offset);
            previousEnd = end;
        }

        if (previousEnd != bytes.Length || starts.Count != root.Int("statement_count", jsonPath))
        {
            throw new GoldenFormatException($"{jsonPath}: statements cover {previousEnd} of {bytes.Length} bytes ({starts.Count} statements)");
        }

        return new ScriptImage(k, bytes, starts.MoveToImmutable());
    }

    private int ReadScriptCount()
    {
        const string path = "scripts/allscr_names.json";
        using JsonDocument doc = Slice.ReadJson(path);
        return doc.RootElement.Arr("names", path).GetArrayLength();
    }

    // -- text ---------------------------------------------------------------------------------------------------------

    private sealed record TextLines(ImmutableSortedDictionary<int, byte[]> Engine, ImmutableSortedDictionary<int, byte[]> Raw);

    private sealed record TextSet(int Count, ImmutableDictionary<Language, TextLines> Languages);

    /// <inheritdoc />
    /// <remarks>
    /// The bytes the engine copies out of the blob at VA 0x75F80: <c>off[id+1] - off[id]</c> bytes, none when that is under 3,
    /// else at most 0x301 of them minus the CR LF (text.md 2). Markup is intact and <c>$</c> references are not expanded.
    /// </remarks>
    public ReadOnlyMemory<byte> TextLine(Language lang, int id) => Line(lang, id, raw: false);

    /// <summary>
    /// Line <paramref name="id"/> as it lies in the string blob: from <c>off[id]</c> to <c>off[id+1]</c>, CR LF included
    /// (text.md 2.2). Not part of <see cref="IGameData"/>; the manifest's SHA-1s are of these bytes.
    /// </summary>
    public ReadOnlyMemory<byte> RawTextLine(Language lang, int id) => Line(lang, id, raw: true);

    private ReadOnlyMemory<byte> Line(Language lang, int id, bool raw)
    {
        ThrowIfDisposed();
        _ = GoldenNames.Code(lang);
        TextSet text = _text.Value;
        if (id < 0 || id >= text.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"text line ids are 0..{text.Count - 1}");
        }

        if (!text.Languages.TryGetValue(lang, out TextLines? lines))
        {
            throw new GoldenSliceMissingException(
                $"text in {GoldenNames.Code(lang)}", "text in " + string.Join(", ", text.Languages.Keys.Select(GoldenNames.Code)));
        }

        ImmutableSortedDictionary<int, byte[]> map = raw ? lines.Raw : lines.Engine;
        if (!map.TryGetValue(id, out byte[]? bytes))
        {
            throw new GoldenSliceMissingException(
                $"text line {id}", "lines " + string.Join(", ", map.Keys));
        }

        return bytes;
    }

    private TextSet ReadText()
    {
        const string path = "text/lines.json";
        using JsonDocument doc = Slice.ReadJson(path);
        JsonElement root = doc.RootElement;
        var languages = ImmutableDictionary.CreateBuilder<Language, TextLines>();
        int count = -1;
        foreach (JsonProperty p in root.Obj("languages", path).EnumerateObject())
        {
            Language lang = GoldenNames.LanguageOf(p.Name) ?? throw new GoldenFormatException($"{path}: unknown language \"{p.Name}\"");
            int n = p.Value.Int("count", path);
            if (count >= 0 && n != count)
            {
                throw new GoldenFormatException($"{path}: {p.Name} has {n} lines, another language has {count}");
            }

            count = n;
            var engine = ImmutableSortedDictionary.CreateBuilder<int, byte[]>();
            var rawLines = ImmutableSortedDictionary.CreateBuilder<int, byte[]>();
            foreach (JsonElement line in p.Value.Arr("lines", path).EnumerateArray())
            {
                int id = line.Int("id", path);
                byte[] raw = GoldenJson.Hex(line.Str("raw_hex", path), path);
                if (raw.Length != line.Int("next_offset", path) - line.Int("offset", path))
                {
                    throw new GoldenFormatException($"{path}: {p.Name} line {id} has {raw.Length} bytes but its offsets span {line.Int("next_offset", path) - line.Int("offset", path)}");
                }

                engine[id] = GoldenJson.Hex(line.Str("engine_line_hex", path), path);
                rawLines[id] = raw;
            }

            languages[lang] = new TextLines(engine.ToImmutable(), rawLines.ToImmutable());
        }

        return new TextSet(count, languages.ToImmutable());
    }

    // -- font pages ---------------------------------------------------------------------------------------------------

    private sealed record FontPageInfo(int Section, int Width, int Height, long Offset, int Length, byte[] Lut);

    private sealed record FontFile(string PagesPath, ImmutableArray<FontPageInfo> Pages);

    /// <inheritdoc />
    /// <remarks>
    /// The golden export stores each page as its 8-bit coverage plane (alpha after the engine's palette conversion, VA 0xD90B0) and
    /// the palette. RGBA is rebuilt as the oracle's <c>FontPage.rgba()</c> builds it: every pixel takes the colour of the palette
    /// entries that map to its coverage (white wherever coverage is above 0, fonts.md 3.1), which reproduces the manifest's
    /// <c>rgba_sha1</c> on all 130 pages. Page 0 is 256 x 256, the others 1024 x 1024.
    /// </remarks>
    public FontPage FontPage(Language lang, int page)
    {
        ThrowIfDisposed();
        string code = GoldenNames.Code(lang);
        FontFile file = _fonts.GetOrAdd(lang, l => new Lazy<FontFile>(() => ReadFontFile(l), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (page < 0 || page >= file.Pages.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, $"ft_{code} has pages 0..{file.Pages.Length - 1}");
        }

        FontPageInfo info = file.Pages[page];
        byte[] coverage = Slice.ReadRange(file.PagesPath, info.Offset, info.Length);
        byte[] rgba = GC.AllocateUninitializedArray<byte>(info.Length * 4);
        ReadOnlySpan<byte> lut = info.Lut;
        for (int i = 0; i < coverage.Length; i++)
        {
            byte a = coverage[i];
            int o = 4 * i;
            rgba[o] = lut[3 * a];
            rgba[o + 1] = lut[(3 * a) + 1];
            rgba[o + 2] = lut[(3 * a) + 2];
            rgba[o + 3] = a;
        }

        return new FontPage(lang, page, info.Width, info.Height, rgba);
    }

    private FontFile ReadFontFile(Language lang)
    {
        string code = GoldenNames.Code(lang);
        string jsonPath = $"fonts/ft_{code}.json";
        if (!Slice.Contains(jsonPath))
        {
            throw new GoldenSliceMissingException($"font ft_{code}", "fonts " + string.Join(", ", Contents.FontLanguages.Select(GoldenNames.Code)));
        }

        using JsonDocument doc = Slice.ReadJson(jsonPath);
        JsonElement root = doc.RootElement;
        string pagesPath = "fonts/" + root.Str("pages_file", jsonPath);
        var pages = ImmutableArray.CreateBuilder<FontPageInfo>();
        foreach (JsonElement p in root.Arr("pages", jsonPath).EnumerateArray())
        {
            int section = p.Int("section", jsonPath);
            if (section != pages.Count)
            {
                throw new GoldenFormatException($"{jsonPath}: page {pages.Count} has section {section}");
            }

            int width = p.Int("width", jsonPath);
            int height = p.Int("height", jsonPath);
            int length = p.Int("length", jsonPath);
            if (length != width * height)
            {
                throw new GoldenFormatException($"{jsonPath}: page {section} stores {length} bytes for {width} x {height} pixels");
            }

            pages.Add(new FontPageInfo(section, width, height, p.Long("offset", jsonPath), length, BuildFontLut(GoldenJson.Hex(p.Str("palette_rgba", jsonPath), jsonPath), jsonPath)));
        }

        return new FontFile(pagesPath, pages.ToImmutable());
    }

    // palette (256 x RGBA in pixel-index order, PS2 alpha) -> colour of every coverage value: the engine converts the alpha
    // (0x80 -> 0xFF, else 2a & 0xFF); the colour of a coverage value is the channel-wise maximum over the entries that map to it
    // (entries that no pixel uses may be black with a coverage that used entries also have, used entries are white; fonts.md 3.1)
    private static byte[] BuildFontLut(byte[] palette, string source)
    {
        if (palette.Length != 1024)
        {
            throw new GoldenFormatException($"{source}: palette has {palette.Length} bytes, expected 1024");
        }

        byte[] lut = new byte[256 * 3];
        for (int i = 0; i < 256; i++)
        {
            int ps2 = palette[(4 * i) + 3];
            int alpha = ps2 == 0x80 ? 0xFF : (2 * ps2) & 0xFF;
            for (int c = 0; c < 3; c++)
            {
                lut[(3 * alpha) + c] = Math.Max(lut[(3 * alpha) + c], palette[(4 * i) + c]);
            }
        }

        return lut;
    }

    // -- pictures -----------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Tiles are the oracle's stored tiles (border included, <c>tile_w x tile_h</c>) in row-major order; a flag-0 tile has no pixels
    /// and an empty <see cref="PictureTile.Rgba"/>. <see cref="PictureTile.Source"/> and the tile position are the oracle's
    /// <c>copy.src/size</c> and <c>copy.dst</c>, which is the inner rectangle clipped to the displayed size.
    /// </remarks>
    public Picture Picture(int archive, int entry)
    {
        ThrowIfDisposed();
        string name = GoldenNames.Archive(archive);
        if (entry < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), entry, "entry numbers are not negative");
        }

        if (!Contents.HasPicture(archive, entry))
        {
            throw new GoldenSliceMissingException($"picture {name}#{entry}", Contents.DescribePictures());
        }

        string stem = $"pictures/{name}_{entry:D5}";
        string jsonPath = stem + ".json";
        using JsonDocument doc = Slice.ReadJson(jsonPath);
        JsonElement root = doc.RootElement;
        JsonElement header = root.Obj("header", jsonPath);
        int width = header.Int("out_width", jsonPath);
        int height = header.Int("out_height", jsonPath);
        int tileW = header.Int("tile_w", jsonPath);
        int tileH = header.Int("tile_h", jsonPath);
        int tileBytes = tileW * tileH * 4;
        string tilesPath = "pictures/" + root.Str("tiles_file", jsonPath);
        JsonElement tiles = root.Arr("tiles", jsonPath);
        if (tiles.GetArrayLength() != header.Int("ntiles", jsonPath))
        {
            throw new GoldenFormatException($"{jsonPath}: {tiles.GetArrayLength()} tiles, the header says {header.Int("ntiles", jsonPath)}");
        }

        var list = ImmutableArray.CreateBuilder<PictureTile>(tiles.GetArrayLength());
        foreach (JsonElement t in tiles.EnumerateArray())
        {
            int index = t.Int("index", jsonPath);
            if (index != list.Count)
            {
                throw new GoldenFormatException($"{jsonPath}: tile {list.Count} has index {index}");
            }

            int flag = t.Int("flag", jsonPath);
            if (flag is < 0 or > 2)
            {
                throw new GoldenFormatException($"{jsonPath}: tile {index} has flag {flag}");
            }

            JsonElement copy = t.Obj("copy", jsonPath);
            JsonElement src = copy.Arr("src", jsonPath);
            JsonElement dst = copy.Arr("dst", jsonPath);
            JsonElement size = copy.Arr("size", jsonPath);
            var source = new PixelRect(src[0].AsInt(jsonPath), src[1].AsInt(jsonPath), size[0].AsInt(jsonPath), size[1].AsInt(jsonPath));
            JsonElement data = t.Prop("data", jsonPath);
            ReadOnlyMemory<byte> rgba;
            if (data.ValueKind == JsonValueKind.Null)
            {
                if (flag != 0)
                {
                    throw new GoldenFormatException($"{jsonPath}: tile {index} has flag {flag} but no pixels");
                }

                rgba = ReadOnlyMemory<byte>.Empty;
            }
            else
            {
                if (flag == 0 || data.Int("length", jsonPath) != tileBytes || data.Int("width", jsonPath) != tileW || data.Int("height", jsonPath) != tileH)
                {
                    throw new GoldenFormatException($"{jsonPath}: tile {index} data does not match a {tileW} x {tileH} RGBA tile of flag {flag}");
                }

                rgba = Slice.ReadRange(tilesPath, data.Long("offset", jsonPath), tileBytes);
            }

            list.Add(new PictureTile(index, (TileFlags)flag, tileW, tileH, source, dst[0].AsInt(jsonPath), dst[1].AsInt(jsonPath), rgba));
        }

        return new Picture(width, height, list.MoveToImmutable());
    }

    // -- audio --------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// The oracle's float PCM of the playable range (the encoder delay removed), not clipped. The loop is the <c>smpl</c> loop in
    /// playable frames with the end inclusive; it is null when the entry has none or when start equals end (audio.md 2.4).
    /// </remarks>
    public AudioClip Audio(AudioBank bank, int index)
    {
        ThrowIfDisposed();
        string name = GoldenNames.Bank(bank);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "entry indices are not negative");
        }

        if (!Contents.HasAudio(bank, index))
        {
            throw new GoldenSliceMissingException($"audio {name}#{index}", Contents.DescribeAudio());
        }

        string jsonPath = $"audio/{name}_{index:D5}.json";
        using JsonDocument doc = Slice.ReadJson(jsonPath);
        JsonElement root = doc.RootElement;
        JsonElement pcm = root.Obj("pcm", jsonPath);
        int channels = root.Int("channels", jsonPath);
        int frames = root.Int("frames", jsonPath);
        if (pcm.Str("dtype", jsonPath) != "f32le" || pcm.Int("channels", jsonPath) != channels || pcm.Int("frames", jsonPath) != frames)
        {
            throw new GoldenFormatException($"{jsonPath}: the PCM description disagrees with the header");
        }

        string pcmPath = "audio/" + root.Str("pcm_file", jsonPath);
        float[] samples = new float[checked(frames * channels)];
        Slice.ReadAt(pcmPath, 0, MemoryMarshal.AsBytes(samples.AsSpan()));
        if (!BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(BitConverter.SingleToInt32Bits(samples[i])));
            }
        }

        AudioLoop? loop = null;
        if (root.TryProp("loop", out JsonElement l) && l.ValueKind == JsonValueKind.Object)
        {
            int start = l.Int("start", jsonPath);
            int end = l.Int("end", jsonPath);
            if (start != end)
            {
                loop = new AudioLoop(start, end);
            }
        }

        return new AudioClip(bank, index, channels, root.Int("rate", jsonPath), samples, loop);
    }

    // -- movies and UI ------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// The movie table's PS4 path (<c>/app0/image/movie/prologue.mp4</c>) placed under <see cref="GameDirectory"/>. The movie
    /// file is not part of the slice and is not opened here.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No game folder is known.</exception>
    public string Movie(int index)
    {
        ThrowIfDisposed();
        string path = Tables.Movie(index).Path;
        const string prefix = "/app0/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new GoldenFormatException($"movie {index}: path \"{path}\" does not start with {prefix}");
        }

        string game = GameDirectory
            ?? throw new InvalidOperationException("movies live in the user's game folder, which GoldenSource cannot find (set TSUKI_GAME_DIR or pass gameDirectory)");
        return Path.Combine(game, path[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    /// <inheritdoc />
    /// <exception cref="GoldenSliceMissingException">Always: the slice exports no UI sheets (the CAUTION screens are plain pictures of allpac3).</exception>
    public Picture UiSheet(UiSet set, Language lang, int child)
    {
        ThrowIfDisposed();
        throw new GoldenSliceMissingException($"UI sheet {set}/{GoldenNames.Code(lang)}/{child}", "no UI sheets; the four CAUTION screens are pictures of archive 4 (allpac3)");
    }

    /// <inheritdoc />
    /// <exception cref="GoldenSliceMissingException">Always: the slice exports no parts.mrg pictures.</exception>
    public Picture? Parts(int index)
    {
        ThrowIfDisposed();
        throw new GoldenSliceMissingException($"parts.mrg entry {index}", "no parts.mrg pictures (the boot notices are not part of the slice)");
    }

    /// <summary>Releases the slice (if this source owns it) and the parsed tables.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Tables.Dispose();
        if (_ownsSlice)
        {
            Slice.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
#endif
