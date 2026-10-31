#if TSUKI_DEV
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Golden;

/// <summary>A movie table record (archives_ui_movies.md 9.2).</summary>
/// <param name="Index">Table index.</param>
/// <param name="Va">Data VA of the 48-byte record.</param>
/// <param name="Path">The PS4 path, <c>/app0/image/movie/prologue.mp4</c>.</param>
/// <param name="Raw">The eight 32-bit fields of the record after the path, as exported.</param>
public sealed record GoldenMovieRecord(int Index, int Va, string Path, ImmutableArray<int> Raw);

/// <summary>An image id as the catalog search reports it (archives_ui_movies.md 2.2).</summary>
/// <param name="Packed">Packed id (archive in the high bits).</param>
/// <param name="Archive">Archive name (<c>allpac0</c>).</param>
/// <param name="Index">Entry index in that archive.</param>
public sealed record GoldenImageId(int Packed, string Archive, int Index);

/// <summary>
/// The catalog record of a picture the slice displays (RUNTIME_REQUIREMENTS 3.2 item 2, archives_ui_movies.md 2.4): the
/// picture-name list index, the 72-byte catalog record and where the picture lives.
/// </summary>
/// <param name="Key">Upper-case picture key (<c>IMG0597</c>).</param>
/// <param name="NameIndex">Index in the picture-name list = catalog record number.</param>
/// <param name="Name">Name from the picture-name list.</param>
/// <param name="File">File name from the NAM search (<c>img0597.MZP</c>), or null.</param>
/// <param name="FileId">The archive entry of the picture file.</param>
/// <param name="OverlayId">The archive entry of the second file (<c>name1</c>), or null.</param>
/// <param name="ReducedId">The archive entry of the reduced copy in allpac2, or null.</param>
/// <param name="Record">The exported catalog record (index, names, layer position, size, anchor, kind, flags, per-language source rectangles).</param>
public sealed record GoldenCatalogRecord(
    string Key, int NameIndex, string Name, string? File, GoldenImageId? FileId, GoldenImageId? OverlayId, GoldenImageId? ReducedId, JsonElement Record);

/// <summary>One keyframe of a motion (72 bytes in the eboot; <c>-32767</c> = not animated).</summary>
/// <param name="Index">Keyframe table index.</param>
/// <param name="Time">Time in milliseconds.</param>
/// <param name="Fields">Fields 1..27 (i16, field 8 is an i64).</param>
public sealed record GoldenMotionKey(int Index, long Time, ImmutableArray<long> Fields);

/// <summary>A motion table entry with its keyframes.</summary>
/// <param name="Id">Motion id as <c>_STMA</c> names it.</param>
/// <param name="DurationMs">Duration in milliseconds.</param>
/// <param name="FirstKey">First keyframe index.</param>
/// <param name="EndKey">One past the last keyframe index.</param>
/// <param name="FieldMask">Field mask word.</param>
/// <param name="Loop">Loop flag from <c>motion_loops.json</c>.</param>
/// <param name="Keys">The keyframes.</param>
public sealed record GoldenMotion(int Id, int DurationMs, int FirstKey, int EndKey, uint FieldMask, int Loop, ImmutableArray<GoldenMotionKey> Keys);

/// <summary>A glyph rectangle record <c>{u16 texture slot, u16 x, u16 y, s16 w, u16 h}</c> (fonts.md 5.3).</summary>
public readonly record struct GoldenGlyphRect(int Tex, int X, int Y, int W, int H);

/// <summary>A glyph lookup array: sorted UTF-8 keys (big-endian packed) with their glyph index (fonts.md 5.1).</summary>
/// <param name="Name">Table name (<c>narrow</c>, <c>ja_wide</c>).</param>
/// <param name="Va">Data VA.</param>
/// <param name="Keys">The keys in table order.</param>
/// <param name="Indices">The glyph index of each key.</param>
public sealed record GoldenFontLookup(string Name, int Va, ImmutableArray<ulong> Keys, ImmutableArray<int> Indices);

/// <summary>A rectangle record array (fonts.md 5.3).</summary>
/// <param name="Name">Table name (<c>ja_s0</c>).</param>
/// <param name="Va">Text VA.</param>
/// <param name="Entries">The records in table order.</param>
public sealed record GoldenGlyphRecords(string Name, int Va, ImmutableArray<GoldenGlyphRect> Entries);

/// <summary>All glyph tables of the eboot (fonts.md 5): 16 lookup arrays, 17 rectangle arrays, the wide-grid bases and the tag names.</summary>
/// <param name="Lookups">The lookup arrays.</param>
/// <param name="Records">The rectangle arrays.</param>
/// <param name="WideBase">Wide-grid base page per language (fonts.md 5.2).</param>
/// <param name="Tags">Tag glyph names (<c>[heart]</c> ...; fonts.md 5.5).</param>
public sealed record GoldenFontTables(
    ImmutableArray<GoldenFontLookup> Lookups, ImmutableArray<GoldenGlyphRecords> Records, ImmutableArray<int> WideBase, ImmutableArray<string> Tags)
{
    /// <summary>The lookup array called <paramref name="name"/>.</summary>
    public GoldenFontLookup Lookup(string name) =>
        Lookups.FirstOrDefault(t => t.Name == name) ?? throw new KeyNotFoundException($"no glyph lookup array \"{name}\"");

    /// <summary>The rectangle array called <paramref name="name"/>.</summary>
    public GoldenGlyphRecords Record(string name) =>
        Records.FirstOrDefault(t => t.Name == name) ?? throw new KeyNotFoundException($"no glyph record array \"{name}\"");
}

/// <summary>One field of the 320-byte config block defaults (<c>ConfigBlock</c>, misc_adv_system_state.md).</summary>
/// <param name="Offset">Byte offset in the block.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Name">What the field is.</param>
/// <param name="Value">The value read from the eboot, or null for blobs wider than 8 bytes.</param>
/// <param name="Documented">The value the document gives (first byte for blobs).</param>
/// <param name="Recommended">The value the port should use when it differs, or null.</param>
/// <param name="Confidence">Confidence letter of the document (<c>E</c> engine code, <c>H</c> hypothesis).</param>
public sealed record GoldenConfigField(int Offset, int Size, string Name, long? Value, long? Documented, long? Recommended, string Confidence);

/// <summary>The config block as the eboot initialises it.</summary>
/// <param name="Va">Data VA of the block.</param>
/// <param name="Raw">The 320 bytes.</param>
/// <param name="Fields">The field map with decoded values.</param>
public sealed record GoldenConfigDefaults(int Va, ReadOnlyMemory<byte> Raw, ImmutableArray<GoldenConfigField> Fields);

/// <summary>One table of <c>textbox_tables.json</c>: raw bytes, where they live and how they decode.</summary>
/// <param name="Name">Table name (<c>scales</c>, <c>window_sizes</c>).</param>
/// <param name="Va">Text VA.</param>
/// <param name="Segment">Eboot segment (<c>text</c>).</param>
/// <param name="Format">Python struct-style format of the values, or null for the region dump.</param>
/// <param name="Raw">The exact bytes.</param>
/// <param name="Values">The decoded values as the exporter wrote them (floats as decimal doubles), or null.</param>
/// <param name="MatchesTsukiTextbox">Whether <c>tsuki.textbox</c> holds the same constants (false for the scales and window sizes, followups.md).</param>
public sealed record GoldenTextboxTable(
    string Name, int Va, string Segment, string? Format, ReadOnlyMemory<byte> Raw, JsonElement? Values, bool? MatchesTsukiTextbox)
{
    /// <summary>The bytes as little-endian float32 values (exact; for tables whose format is all <c>f</c>).</summary>
    public float[] Floats()
    {
        if (Raw.Length % 4 != 0)
        {
            throw new InvalidOperationException($"{Name}: {Raw.Length} bytes are not a whole number of float32 values");
        }

        float[] result = new float[Raw.Length / 4];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(Raw.Span[(4 * i)..]));
        }

        return result;
    }
}

/// <summary>
/// The eboot-only tables of the golden slice (<c>slice/eboot</c>, RUNTIME_REQUIREMENTS 3.2): the subset the vertical slice needs,
/// not the whole eboot. <see cref="IEbootTables"/> has no members yet (the lead adds them as stream A ports each extractor), so
/// this class gives typed access to the tables that have a consumer in M2a (scene ids, movie table, picture catalog records,
/// motions, glyph tables, config defaults, text-box tables) and the exported JSON of every table through <see cref="Table"/>.
/// Tables are read on first use; values are immutable.
/// </summary>
public sealed partial class GoldenEbootTables : IEbootTables, IDisposable
{
    private const string Folder = "eboot/";

    private readonly GoldenSlice _slice;
    private readonly ConcurrentDictionary<string, Lazy<JsonDocument>> _documents = new(StringComparer.Ordinal);
    private readonly Lazy<ImmutableArray<int>> _sceneIds;
    private readonly Lazy<MovieTable> _movies;
    private readonly Lazy<ImmutableArray<GoldenCatalogRecord>> _catalog;
    private readonly Lazy<ImmutableArray<GoldenMotion>> _motions;
    private readonly Lazy<GoldenFontTables> _fonts;
    private readonly Lazy<GoldenConfigDefaults> _config;
    private readonly Lazy<ImmutableSortedDictionary<string, GoldenTextboxTable>> _textbox;

    /// <summary>Creates the tables over a slice (the slice stays owned by the caller).</summary>
    public GoldenEbootTables(GoldenSlice slice)
    {
        _slice = slice;
        TableNames = [.. slice.Manifest.Files
            .Where(f => f.Part == "eboot" && f.Path.StartsWith(Folder, StringComparison.Ordinal) && f.Path.EndsWith(".json", StringComparison.Ordinal))
            .Select(f => f.Path[Folder.Length..^".json".Length])
            .Order(StringComparer.Ordinal)];
        _sceneIds = new(ReadSceneIds, LazyThreadSafetyMode.ExecutionAndPublication);
        _movies = new(ReadMovies, LazyThreadSafetyMode.ExecutionAndPublication);
        _catalog = new(ReadCatalog, LazyThreadSafetyMode.ExecutionAndPublication);
        _motions = new(ReadMotions, LazyThreadSafetyMode.ExecutionAndPublication);
        _fonts = new(ReadFonts, LazyThreadSafetyMode.ExecutionAndPublication);
        _config = new(ReadConfig, LazyThreadSafetyMode.ExecutionAndPublication);
        _textbox = new(ReadTextbox, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Names of the exported tables: <c>build</c>, <c>catalog</c>, <c>compositing</c>, <c>config</c>, <c>font_tables</c>, <c>motions</c>, <c>movies</c>, <c>scenes</c>, <c>textbox_tables</c>.</summary>
    public ImmutableArray<string> TableNames { get; }

    /// <summary>Script number k to scene id (eboot text VA 0x2AB4B0, 549 entries; script.md 2.5). The slice's scripts 2 and 3 are scenes 260 and 270.</summary>
    public ImmutableArray<int> SceneIds => _sceneIds.Value;

    /// <summary>The movie table, one record per movie (134; archives_ui_movies.md 9.2).</summary>
    public ImmutableArray<GoldenMovieRecord> Movies => _movies.Value.Records;

    /// <summary>The four table indices of the prologue movie per language ja, en, zc, zt (archives_ui_movies.md 9.3).</summary>
    public ImmutableArray<int> PrologueMovieIds => _movies.Value.Prologue;

    /// <summary>Catalog records of the pictures the slice displays (18) and the files the NAM search found for them.</summary>
    public ImmutableArray<GoldenCatalogRecord> CatalogRecords => _catalog.Value;

    /// <summary>The motions the slice starts (22523..22526) with their keyframes.</summary>
    public ImmutableArray<GoldenMotion> Motions => _motions.Value;

    /// <summary>The glyph lookup and rectangle tables (17,423 lookup entries).</summary>
    public GoldenFontTables FontTables => _fonts.Value;

    /// <summary>The config block defaults.</summary>
    public GoldenConfigDefaults Config => _config.Value;

    /// <summary>The text-box tables by name (<c>colours</c>, <c>scales</c>, <c>window_table</c>, <c>window_sizes</c> ...).</summary>
    public ImmutableSortedDictionary<string, GoldenTextboxTable> Textbox => _textbox.Value;

    /// <summary>The catalog record whose key is <paramref name="key"/> (upper case), or null.</summary>
    public GoldenCatalogRecord? CatalogRecord(string key) =>
        CatalogRecords.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));

    /// <summary>The motion with id <paramref name="id"/>, or null.</summary>
    public GoldenMotion? Motion(int id) => Motions.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// The movie table record <paramref name="index"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the table.</exception>
    public GoldenMovieRecord Movie(int index)
    {
        ImmutableArray<GoldenMovieRecord> records = Movies;
        if ((uint)index >= (uint)records.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"the movie table has {records.Length} records");
        }

        return records[index];
    }

    /// <summary>The exported JSON of the table <paramref name="name"/> (one of <see cref="TableNames"/>), as written by the oracle.</summary>
    /// <exception cref="GoldenSliceMissingException">There is no such table.</exception>
    public JsonElement Table(string name)
    {
        if (!TableNames.Contains(name))
        {
            throw new GoldenSliceMissingException($"eboot table \"{name}\"", string.Join(", ", TableNames));
        }

        return _documents.GetOrAdd(
            name,
            n => new Lazy<JsonDocument>(() => _slice.ReadJson(Folder + n + ".json"), LazyThreadSafetyMode.ExecutionAndPublication)).Value.RootElement;
    }

    /// <summary>Releases the parsed JSON documents.</summary>
    public void Dispose()
    {
        foreach (Lazy<JsonDocument> d in _documents.Values)
        {
            if (d.IsValueCreated)
            {
                d.Value.Dispose();
            }
        }

        _documents.Clear();
    }

    private sealed record MovieTable(ImmutableArray<GoldenMovieRecord> Records, ImmutableArray<int> Prologue);

    private ImmutableArray<int> ReadSceneIds()
    {
        const string src = Folder + "scenes.json";
        JsonElement root = Table("scenes");
        JsonElement ids = root.Arr("scene_ids", src);
        int count = root.Int("count", src);
        if (ids.GetArrayLength() != count)
        {
            throw new GoldenFormatException($"{src}: count {count} but {ids.GetArrayLength()} scene ids");
        }

        return [.. ids.EnumerateArray().Select(e => e.AsInt(src))];
    }

    private MovieTable ReadMovies()
    {
        const string src = Folder + "movies.json";
        JsonElement root = Table("movies");
        var records = ImmutableArray.CreateBuilder<GoldenMovieRecord>();
        foreach (JsonElement r in root.Arr("records", src).EnumerateArray())
        {
            int id = r.Int("id", src);
            if (id != records.Count)
            {
                throw new GoldenFormatException($"{src}: record {records.Count} has id {id}");
            }

            records.Add(new GoldenMovieRecord(id, r.Int("va", src), r.Str("path", src), [.. r.Arr("raw", src).EnumerateArray().Select(e => e.AsInt(src))]));
        }

        ImmutableArray<int> prologue = [.. root.Obj("prologue_table", src).Arr("ids", src).EnumerateArray().Select(e => e.AsInt(src))];
        return new MovieTable(records.ToImmutable(), prologue);
    }

    private ImmutableArray<GoldenCatalogRecord> ReadCatalog()
    {
        const string src = Folder + "catalog.json";
        JsonElement root = Table("catalog");
        var list = ImmutableArray.CreateBuilder<GoldenCatalogRecord>();
        foreach (JsonElement r in root.Arr("records", src).EnumerateArray())
        {
            list.Add(new GoldenCatalogRecord(
                r.Str("key", src), r.Int("name_index", src), r.Str("name", src), r.StrOrNull("file", src),
                ReadImageId(r, "file_id", src), ReadImageId(r, "overlay_id", src), ReadImageId(r, "reduced_id", src),
                r.Obj("record", src).Clone()));
        }

        return list.ToImmutable();
    }

    private static GoldenImageId? ReadImageId(JsonElement owner, string name, string src)
    {
        if (!owner.TryGetProperty(name, out JsonElement e) || e.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return new GoldenImageId(e.Int("packed", src), e.Str("archive", src), e.Int("index", src));
    }

    private ImmutableArray<GoldenMotion> ReadMotions()
    {
        const string src = Folder + "motions.json";
        JsonElement root = Table("motions");
        var list = ImmutableArray.CreateBuilder<GoldenMotion>();
        foreach (JsonElement m in root.Arr("motions", src).EnumerateArray())
        {
            var keys = ImmutableArray.CreateBuilder<GoldenMotionKey>();
            foreach (JsonElement k in m.Arr("keys", src).EnumerateArray())
            {
                keys.Add(new GoldenMotionKey(
                    k.Int("index", src), k.Long("time", src), [.. k.Arr("fields", src).EnumerateArray().Select(f => f.GetInt64())]));
            }

            list.Add(new GoldenMotion(
                m.Int("id", src), m.Int("duration_ms", src), m.Int("first_key", src), m.Int("end_key", src),
                (uint)m.Long("field_mask", src), m.Int("loop", src), keys.ToImmutable()));
        }

        return list.ToImmutable();
    }

    private GoldenFontTables ReadFonts()
    {
        const string src = Folder + "font_tables.json";
        JsonElement root = Table("font_tables");
        var lookups = ImmutableArray.CreateBuilder<GoldenFontLookup>();
        foreach (JsonElement t in root.Arr("lookups", src).EnumerateArray())
        {
            string name = t.Str("name", src);
            JsonElement entries = t.Arr("entries", src);
            var keys = ImmutableArray.CreateBuilder<ulong>(entries.GetArrayLength());
            var indices = ImmutableArray.CreateBuilder<int>(entries.GetArrayLength());
            foreach (JsonElement e in entries.EnumerateArray())
            {
                keys.Add(e[0].GetUInt64());
                indices.Add(e[1].AsInt(src));
            }

            if (keys.Count != t.Int("count", src))
            {
                throw new GoldenFormatException($"{src}: lookup {name} has count {t.Int("count", src)} but {keys.Count} entries");
            }

            lookups.Add(new GoldenFontLookup(name, t.Int("va", src), keys.MoveToImmutable(), indices.MoveToImmutable()));
        }

        var records = ImmutableArray.CreateBuilder<GoldenGlyphRecords>();
        foreach (JsonElement t in root.Arr("records", src).EnumerateArray())
        {
            string name = t.Str("name", src);
            JsonElement entries = t.Arr("entries", src);
            var rects = ImmutableArray.CreateBuilder<GoldenGlyphRect>(entries.GetArrayLength());
            foreach (JsonElement e in entries.EnumerateArray())
            {
                rects.Add(new GoldenGlyphRect(e[0].AsInt(src), e[1].AsInt(src), e[2].AsInt(src), e[3].AsInt(src), e[4].AsInt(src)));
            }

            if (rects.Count != t.Int("count", src))
            {
                throw new GoldenFormatException($"{src}: record table {name} has count {t.Int("count", src)} but {rects.Count} entries");
            }

            records.Add(new GoldenGlyphRecords(name, t.Int("va", src), rects.MoveToImmutable()));
        }

        return new GoldenFontTables(
            lookups.ToImmutable(),
            records.ToImmutable(),
            [.. root.Arr("wide_base", src).EnumerateArray().Select(e => e.AsInt(src))],
            [.. root.Arr("tags", src).EnumerateArray().Select(e => e.GetString() ?? string.Empty)]);
    }

    private GoldenConfigDefaults ReadConfig()
    {
        const string src = Folder + "config.json";
        JsonElement root = Table("config");
        byte[] raw = GoldenJson.Hex(root.Str("raw", src), src);
        if (raw.Length != root.Int("size", src))
        {
            throw new GoldenFormatException($"{src}: size {root.Int("size", src)} but {raw.Length} raw bytes");
        }

        static long? Opt(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

        var fields = ImmutableArray.CreateBuilder<GoldenConfigField>();
        foreach (JsonElement f in root.Arr("fields", src).EnumerateArray())
        {
            fields.Add(new GoldenConfigField(
                f.Int("offset", src), f.Int("size", src), f.Str("name", src),
                Opt(f, "value"), Opt(f, "documented"), Opt(f, "recommended"), f.Str("confidence", src)));
        }

        return new GoldenConfigDefaults(root.Int("va", src), raw, fields.ToImmutable());
    }

    private ImmutableSortedDictionary<string, GoldenTextboxTable> ReadTextbox()
    {
        const string src = Folder + "textbox_tables.json";
        JsonElement root = Table("textbox_tables");
        var tables = ImmutableSortedDictionary.CreateBuilder<string, GoldenTextboxTable>(StringComparer.Ordinal);
        foreach (JsonProperty p in root.EnumerateObject())
        {
            JsonElement t = p.Value;
            JsonElement? values = t.TryGetProperty("values", out JsonElement v) ? v.Clone() : null;
            bool? matches = t.TryGetProperty("matches_tsuki_textbox", out JsonElement m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? m.GetBoolean()
                : null;
            tables[p.Name] = new GoldenTextboxTable(
                p.Name, t.Int("va", src), t.Str("segment", src), t.StrOrNull("format", src),
                GoldenJson.Hex(t.Str("raw", src), src), values, matches);
        }

        return tables.ToImmutable();
    }
}
#endif
