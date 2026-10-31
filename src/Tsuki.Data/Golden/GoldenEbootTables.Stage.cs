#if TSUKI_DEV
using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;

namespace Tsuki.Data.Golden;

// The stage area of the golden eboot tables (package K2): the motions and catalog records the slice holds, in the form Tsuki.Engine/Stage reads them. The tables are
// sparse. What a script of the slice asks for is there; anything else throws GoldenSliceMissingException (a KeyNotFoundException), the haze table is not part of the slice.
public sealed partial class GoldenEbootTables
{
    private IMotionTables? _stageMotions;
    private SparsePictureCatalog? _stagePictures;
    private IPictureLayoutSource? _stageLayouts;

    /// <inheritdoc/>
    IMotionTables IEbootTables.Motions => LazyInitializer.EnsureInitialized(ref _stageMotions, BuildStageMotions)!;

    /// <inheritdoc/>
    IPictureCatalog IEbootTables.Pictures => StagePictures;

    /// <inheritdoc/>
    IHazeTable IEbootTables.Haze => throw new NotSupportedException("the golden slice does not hold the haze table");

    /// <summary>
    /// The draw description of the pictures the slice holds (<see cref="IPictureLayoutSource"/> for <c>StageRuntime.Layouts</c>): the tile geometry is the oracle's
    /// <c>copy</c> record of every tile in <c>pictures/*.json</c> and <c>retail/*.json</c> (the displayed rectangle of the stored tile and where it lands),
    /// clipped to the catalog's source rectangle of the language. A picture the slice has no file for gets no layout (null).
    /// </summary>
    public IPictureLayoutSource StageLayouts => LazyInitializer.EnsureInitialized(ref _stageLayouts, () => new SparsePictureLayouts(_slice, StagePictures))!;

    private SparsePictureCatalog StagePictures => LazyInitializer.EnsureInitialized(ref _stagePictures, BuildStagePictures)!;

    private SparseMotionTables BuildStageMotions()
    {
        // read here, not through Motions: that record narrows the field-enable word to 32 bits, and the words of the motions have bits up to 34
        const string src = Folder + "motions.json";
        JsonElement tables = Table("motions").Obj("tables", src);
        var motions = new Dictionary<int, SparseMotion>();
        foreach (SparseMotion m in ParseMotions(Table("motions"), src))
        {
            motions[m.Id] = m;
        }

        // the retail path starts motions 1..5 (RUNTIME_REQUIREMENTS 7.6); the same layout as eboot/motions.json
        const string retail = "retail/motions.json";
        if (_slice.Contains(retail))
        {
            using JsonDocument doc = _slice.ReadJson(retail);
            foreach (SparseMotion m in ParseMotions(doc.RootElement, retail))
            {
                motions[m.Id] = m;
            }
        }

        return new SparseMotionTables(tables.Int("count", src), tables.Int("keyframes", src), motions);
    }

    private static List<SparseMotion> ParseMotions(JsonElement root, string src)
    {
        var list = new List<SparseMotion>();
        foreach (JsonElement m in root.Arr("motions", src).EnumerateArray())
        {
            var keys = new List<SparseKey>();
            foreach (JsonElement k in m.Arr("keys", src).EnumerateArray())
            {
                long[] fields = [.. k.Arr("fields", src).EnumerateArray().Select(f => f.GetInt64())];
                if (fields.Length != MotionTable.FieldCount)
                {
                    throw new GoldenFormatException($"{src}: a keyframe of motion {m.Int("id", src)} has {fields.Length} fields");
                }

                keys.Add(new SparseKey(k.Int("index", src), k.Long("time", src), fields));
            }

            list.Add(new SparseMotion(
                m.Int("id", src), m.Long("duration_ms", src), m.Int("first_key", src), m.Int("end_key", src), m.Prop("field_mask", src).GetUInt64(), m.Int("loop", src), keys));
        }

        return list;
    }

    private SparsePictureCatalog BuildStagePictures()
    {
        const string src = Folder + "catalog.json";
        JsonElement root = Table("catalog");
        var records = new List<JsonElement>();
        records.AddRange(root.Arr("records", src).EnumerateArray());
        records.AddRange(root.Arr("seen_records", src).EnumerateArray());
        JsonDocument? retail = null;
        try
        {
            // the retail path displays pictures the scripts 2 and 3 do not (RUNTIME_REQUIREMENTS 7.6)
            if (_slice.Contains("retail/path.json"))
            {
                retail = _slice.ReadJson("retail/path.json");
                records.AddRange(retail.RootElement.Arr("catalog", "retail/path.json").EnumerateArray());
            }

            var cat = new SparsePictureCatalog(root.Obj("table", src).Int("used", src));
            foreach (JsonElement r in records)
            {
                JsonElement rec = r.Obj("record", src);
                var bands = new PixelRect[4];
                int b = 0;
                foreach (string lang in new[] { "ja", "en", "zc", "zt" })
                {
                    JsonElement rect = rec.Obj("source_rect", src).Arr(lang, src);
                    bands[b++] = new PixelRect(rect[0].GetInt32(), rect[1].GetInt32(), rect[2].GetInt32() - rect[0].GetInt32(), rect[3].GetInt32() - rect[1].GetInt32());     // exported as [left, top, right, bottom]
                }

                cat.Add(
                    r.Str("key", src), r.Int("name_index", src), r.Str("name", src),
                    new PictureInfo(rec.Int("anchor_x", src), rec.Int("anchor_y", src), rec.Int("scale_class", src), rec.Int("draw_flags", src), rec.StrOrNull("name1", src) is not null),
                    ReadFile(r, "file_id", src), ReadFile(r, "overlay_id", src), bands);
            }

            return cat;
        }
        finally
        {
            retail?.Dispose();
        }
    }

    private static (int Archive, int Entry)? ReadFile(JsonElement owner, string name, string src)
    {
        if (!owner.TryGetProperty(name, out JsonElement e) || e.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        int archive = GoldenNames.ArchiveId(e.Str("archive", src));
        return archive < 0 ? throw new GoldenFormatException($"{src}: unknown archive \"{e.Str("archive", src)}\"") : (archive, e.Int("index", src));
    }

    internal sealed record SparseKey(int Index, long Time, long[] Fields);

    internal sealed record SparseMotion(int Id, long Duration, int FirstKey, int EndKey, ulong Mask, int Loop, List<SparseKey> Keys);

    /// <summary>The motions of the slice as <see cref="IMotionTables"/>; ids and keyframe indices the slice lacks throw <see cref="GoldenSliceMissingException"/>.</summary>
    internal sealed class SparseMotionTables : IMotionTables
    {
        private readonly Dictionary<int, SparseMotion> _motions;
        private readonly Dictionary<int, SparseKey> _keys = [];

        public SparseMotionTables(int motionCount, int keyCount, Dictionary<int, SparseMotion> motions)
        {
            MotionCount = motionCount;
            KeyCount = keyCount;
            _motions = motions;
            foreach (SparseMotion m in motions.Values)
            {
                foreach (SparseKey k in m.Keys)
                {
                    _keys[k.Index] = k;
                }
            }
        }

        public int MotionCount { get; }

        public int KeyCount { get; }

        public long Duration(int id) => Motion(id).Duration;

        /// <summary>The end of motion <paramref name="id"/>; for a motion the slice lacks, the start of the next one (the table is dense), so that the start of a known motion is known.</summary>
        public int KeyEnd(int id)
        {
            if (id < 1)
            {
                return 0;
            }

            if (_motions.TryGetValue(id, out SparseMotion? m))
            {
                return m.EndKey;
            }

            return _motions.TryGetValue(id + 1, out SparseMotion? next) ? next.FirstKey : throw Missing($"motion {id}");
        }

        public long KeyTime(int index) => Key(index).Time;

        public long KeyField(int index, int field) => Key(index).Fields[field - 1];

        public ulong FieldMask(int id) => Motion(id).Mask;

        public int LoopPoint(int id) => Motion(id).Loop;

        private SparseMotion Motion(int id) => _motions.TryGetValue(id, out SparseMotion? m) ? m : throw Missing($"motion {id}");

        private SparseKey Key(int index) => _keys.TryGetValue(index, out SparseKey? k) ? k : throw Missing($"motion keyframe {index}");

        private GoldenSliceMissingException Missing(string what) => new(what, "the motions " + string.Join(", ", _motions.Keys.Order()));
    }

    /// <summary>The catalog records of the slice and its retail path as <see cref="IPictureCatalog"/>, with the files and displayed rectangles of the pictures.</summary>
    internal sealed class SparsePictureCatalog(int count) : IPictureCatalog
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _names = [];
        private readonly Dictionary<int, PictureInfo> _info = [];
        private readonly Dictionary<int, (int Archive, int Entry)?[]> _files = [];
        private readonly Dictionary<int, PixelRect[]> _bands = [];

        public int Count { get; } = count;

        public void Add(string key, int nameIndex, string name, PictureInfo info, (int Archive, int Entry)? body, (int Archive, int Entry)? face, PixelRect[] bands)
        {
            _index.TryAdd(key.ToUpperInvariant(), nameIndex);
            _names[nameIndex] = name;
            _info[nameIndex] = info;
            _files[nameIndex] = [body, face];
            _bands[nameIndex] = bands;
        }

        /// <summary>The archive entries of the body and the face of picture <paramref name="pic"/> and its displayed rectangle for <paramref name="language"/>, or null.</summary>
        public ((int Archive, int Entry)? Body, (int Archive, int Entry)? Face, PixelRect Band)? Files(int pic, int language) =>
            _files.TryGetValue(pic, out (int Archive, int Entry)?[]? f) && (uint)language < 4 ? (f[0], f[1], _bands[pic][language]) : null;

        public int Lookup(string name)
        {
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            string key = dot >= 0 ? name[..dot] : name;
            if (key.Length > 32)
            {
                key = key[..32];
            }

            return _index.TryGetValue(key.ToUpperInvariant(), out int id) ? id : -1;
        }

        public string Name(int pic) => _names.TryGetValue(pic, out string? n) ? n : string.Empty;

        public PictureInfo Info(int pic) => _info.TryGetValue(pic, out PictureInfo i) ? i : throw new GoldenSliceMissingException($"catalog record {pic}", "the pictures of the slice scripts");
    }

    /// <summary>The layouts of the slice's pictures from the exported tile records.</summary>
    internal sealed class SparsePictureLayouts(GoldenSlice slice, SparsePictureCatalog catalog) : IPictureLayoutSource
    {
        private readonly Dictionary<(int Pic, int Language), PictureLayout?> _cache = [];

        public PictureLayout? Get(int pic, int language)
        {
            lock (_cache)
            {
                if (!_cache.TryGetValue((pic, language), out PictureLayout? layout))
                {
                    layout = Build(pic, language);
                    _cache[(pic, language)] = layout;
                }

                return layout;
            }
        }

        private PictureLayout? Build(int pic, int language)
        {
            if (catalog.Files(pic, language) is not { } f)
            {
                return null;
            }

            var files = new List<PictureFileLayout>(2);
            foreach ((int Archive, int Entry)? file in new[] { f.Body, f.Face })
            {
                if (file is not { } id)
                {
                    continue;
                }

                string name = $"{GoldenNames.Archive(id.Archive)}_{id.Entry:D5}.json";
                string path = slice.Contains("pictures/" + name) ? "pictures/" + name : "retail/" + name;
                if (!slice.Contains(path))
                {
                    return null;
                }

                files.Add(ReadFileLayout(path, id.Archive, id.Entry, f.Band));
            }

            return files.Count == 0 ? null : new PictureLayout(f.Band.Width, f.Band.Height, files);
        }

        private PictureFileLayout ReadFileLayout(string path, int archive, int entry, PixelRect band)
        {
            using JsonDocument doc = slice.ReadJson(path);
            JsonElement root = doc.RootElement;
            JsonElement header = root.Obj("header", path);
            int tileW = header.Int("tile_w", path);
            int tileH = header.Int("tile_h", path);
            var tiles = new List<TileQuad>();
            foreach (JsonElement t in root.Arr("tiles", path).EnumerateArray())
            {
                if (t.Int("flag", path) == 0 || !t.TryGetProperty("copy", out JsonElement copy))
                {
                    continue;        // an empty tile (flag 0: no texture, never displayed; the record still has a copy rectangle)
                }

                JsonElement src = copy.Arr("src", path);
                JsonElement dst = copy.Arr("dst", path);
                JsonElement size = copy.Arr("size", path);
                int sx = src[0].GetInt32();
                int sy = src[1].GetInt32();
                int x = dst[0].GetInt32();
                int y = dst[1].GetInt32();
                int w = size[0].GetInt32();
                int h = size[1].GetInt32();
                int x0 = Math.Max(x, band.X);
                int y0 = Math.Max(y, band.Y);
                int x1 = Math.Min(x + w, band.X + band.Width);
                int y1 = Math.Min(y + h, band.Y + band.Height);
                if (x1 <= x0 || y1 <= y0)
                {
                    continue;
                }

                tiles.Add(new TileQuad(
                    t.Int("index", path), x0 - band.X, y0 - band.Y, x1 - band.X, y1 - band.Y,
                    (sx + (x0 - x)) / (float)tileW, (sy + (y0 - y)) / (float)tileH, (sx + (x1 - x)) / (float)tileW, (sy + (y1 - y)) / (float)tileH));
            }

            return new PictureFileLayout(archive, entry, 0, 0, tiles);
        }
    }
}
#endif
