// Finds every table of the data layer in an eboot.bin by its content (package K25), so that another build of the game, or another eboot of the same release,
// runs without the fixed addresses of the verified build. Each table has a structural signature (record strides, pointer ranges, terminators, monotonic indices,
// strings the engine itself uses) and a validator; a table that cannot be found or fails validation is reported by name, never guessed. On the verified eboot
// the result equals EbootLayout.Analysed field by field (a test checks every address). Calibrated on the two eboots this machine has (the official 1.01 update and
// its 5.05 backport); the hypotheses are marked [H] in the comments and listed in docs/status/knife/K25.md.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Tsuki.Data.Fonts;

namespace Tsuki.Data.Eboot;

/// <summary>How a table's address was established.</summary>
public enum LocateMethod
{
    /// <summary>Found by its own content (a signature and a validator).</summary>
    Content,

    /// <summary>Derived from a neighbouring table (a fixed offset, an alignment rule) and validated.</summary>
    Anchored,

    /// <summary>Read out of the code that uses it (a function found by its instruction pattern).</summary>
    Code,
}

/// <summary>One located table.</summary>
/// <param name="Name">The table's name in <see cref="EbootLayout.Addresses"/>.</param>
/// <param name="Va">Where it was found.</param>
/// <param name="Count">Records found (0 when the table has no count of its own).</param>
/// <param name="Method">How it was found.</param>
/// <param name="Detail">What the validator saw.</param>
public sealed record TableLocation(string Name, long Va, int Count, LocateMethod Method, string Detail);

/// <summary>The outcome of <see cref="EbootLocator.Locate"/>.</summary>
public sealed class LocateResult
{
    internal LocateResult(EbootLayout? layout, IReadOnlyList<TableLocation> found, IReadOnlyList<(string Name, string Problem)> missing, TimeSpan elapsed)
    {
        Layout = layout;
        Found = found;
        Missing = missing;
        Elapsed = elapsed;
    }

    /// <summary>The layout, when every table was found; null otherwise.</summary>
    public EbootLayout? Layout { get; }

    /// <summary>The tables that were found.</summary>
    public IReadOnlyList<TableLocation> Found { get; }

    /// <summary>The tables that were not found or failed validation, with the reason.</summary>
    public IReadOnlyList<(string Name, string Problem)> Missing { get; }

    /// <summary>How long the search took.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>True when every table was found and validated.</summary>
    public bool Complete => Layout is not null;

    /// <summary>One line per table for the log.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        foreach (TableLocation t in Found)
        {
            sb.Append("  ").Append(t.Name.PadRight(22)).Append(" 0x").Append(t.Va.ToString("x", CultureInfo.InvariantCulture).PadRight(8)).Append(' ').Append(t.Method.ToString().ToLowerInvariant().PadRight(9)).Append(t.Detail).AppendLine();
        }

        foreach ((string name, string problem) in Missing)
        {
            sb.Append("  ").Append(name.PadRight(22)).Append(" MISSING   ").Append(problem).AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>"71 tables located in 0.4 s" or "3 of 71 tables missing: ...".</summary>
    public string Summary() => Missing.Count == 0
        ? $"{Found.Count} tables located by content in {Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s"
        : $"{Missing.Count} of {Found.Count + Missing.Count} tables missing: {string.Join(", ", Missing.Select(m => m.Name))}";
}

/// <summary>Locates every table of <see cref="EbootLayout"/> in an eboot by content; see the file comment.</summary>
public static class EbootLocator
{
    /// <summary>Runs every search on <paramref name="reader"/>. Never throws for malformed content: a table that cannot be found is in <see cref="LocateResult.Missing"/>.</summary>
    public static LocateResult Locate(EbootReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var sw = Stopwatch.StartNew();
        var session = new Session(reader);
        session.Run();
        sw.Stop();
        return new LocateResult(session.Build(), session.Found, session.Missing, sw.Elapsed);
    }

    /// <summary>Validates the tables of <paramref name="layout"/> on <paramref name="reader"/> by reading each one; the names of the tables that fail.</summary>
    public static IReadOnlyList<string> Validate(EbootReader reader, EbootLayout layout)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(layout);
        EbootReader r = reader.WithLayout(layout);
        var failed = new List<string>();
        void Try(string name, Action read)
        {
            try
            {
                read();
            }
            catch (Exception e) when (e is Containers.ContainerFormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException
                                          or OverflowException or KeyNotFoundException or InvalidCastException)
            {
                failed.Add(name + ": " + e.Message);
            }
        }

        Try("image table", () => Catalog.ImageCatalog.Read(r));
        Try("picture names", () => Catalog.PictureNameLists.Read(r));
        Try("movie table", () => Catalog.MovieTable.Read(r));
        Try("language suffixes", () => Catalog.MovieTable.ReadLanguageSuffixes(r));
        Try("ui_text", () => UiTextTable.Read(r));
        Try("ui tables", () => Catalog.UiTextureTables.ReadAll(r));
        Try("fonts", () =>
        {
            IReadOnlyList<string> issues = EbootFontData.Read(r).Verify();
            if (issues.Count > 0)
            {
                throw new EbootFormatException(string.Join("; ", issues.Take(3)));
            }
        });
        Try("scene ids", () => Scripts.SceneTable.Read(r));
        Try("motions", () => Scripts.MotionTables.Read(r));
        var engine = new Tables.EngineTables(r);
        Try("haze", () => _ = engine.Haze);
        Try("scene nodes", () => _ = engine.SceneNodes);
        Try("motion loops", () => _ = engine.MotionLoops);
        Try("jump clears", () => _ = engine.JumpClears);
        Try("keep lists", () => { _ = engine.FlowKeep; _ = engine.HistoryKeep; });
        Try("focus lines", () => { _ = engine.FclnLines; _ = engine.FclnNotes; });
        Try("voices", () => _ = engine.Voices);
        Try("test SE names", () => _ = engine.TestSes);
        Try("dialogs", () => _ = engine.Dialogs);
        Try("windows", () => { _ = engine.Windows; _ = engine.ClipRadii; _ = engine.TextColours; });
        Try("config pages", () => _ = engine.ConfigPages);
        Try("motion masks", () => _ = engine.MotionMasks);
        Try("gallery", () => _ = engine.Gallery);
        Try("flowchart", () => _ = engine.Flowchart);
        Try("cells", () =>
        {
            _ = Tables.UiCellTables.ReadSaveTitleCells(r, false);
            _ = Tables.UiCellTables.ReadSaveTitleCells(r, true);
            _ = Tables.UiCellTables.ReadPhaseTitleCells(r, Tables.PhaseTitleSet.Zh);
            _ = Tables.UiCellTables.ReadFlowTitleCells(r, true);
            _ = Tables.UiCellTables.ReadFlowLineCells(r);
            _ = Tables.UiCellTables.ReadVoiceFaceCells(r);
            _ = Tables.UiCellTables.ReadVoiceNameCells(r, true);
            _ = Tables.UiCellTables.ReadGuideIconCells(r);
            _ = Tables.UiCellTables.ReadGuideWordCells(r, 4);
            _ = Tables.UiCellTables.ReadBannerCells(r);
            _ = Tables.UiCellTables.ReadBannerKeys(r);
        });
        Try("title background", () => r.Read(layout.TitleBackground, 24));
        return failed;
    }

    /// <summary>One loaded segment of the image.</summary>
    private sealed record Seg(string Name, long Va, ReadOnlyMemory<byte> Bytes)
    {
        public long End => Va + Bytes.Length;

        public bool Contains(long va) => va >= Va && va < End;

        public int Offset(long va) => checked((int)(va - Va));
    }

    private sealed class Session
    {
        private const string ExpectedLanguageSuffixes = "ja en zc zt ko";
        private static readonly string[] LookupNames = ["narrow", "ja_s1", "en_s1", "zc_s1", "zt_s1", "ko_s1", "ja_s2", "en_s2", "zc_s2", "zt_s2", "ko_s2", "ja_wide", "en_wide", "zc_wide", "zt_wide", "ko_wide"];
        private static readonly string[] RecordNames = ["ja_s0", "ja_s2", "ja_s1", "en_s0", "en_s2", "en_s3", "en_s4", "en_s5", "en_s1", "zc_s0", "zc_s2", "zc_s1", "zt_s0", "zt_s1", "ko_s0", "ko_s2", "ko_s1"];

        private readonly EbootReader _r;
        private readonly Seg _text;
        private readonly Seg _relro;
        private readonly Seg _data;
        private readonly Seg[] _segs;
        private readonly HashSet<long> _lea = [];
        private readonly List<TableLocation> _found = [];
        private readonly List<(string, string)> _missing = [];

        // results
        private long _imageTable, _imageNames, _oldImageNames, _movieTable, _suffixes, _prologue, _uiText, _hunex;
        private int _imageRecords, _imageSlots, _movieRecords, _languageCount, _uiTextCount;
        private List<long> _uiTables = [];
        private List<FontLookupSpec> _lookups = [];
        private List<FontRecordSpec> _records = [];
        private long _fontWide, _fontTags;
        private long _sceneIds, _masks, _durations, _ends, _keys;
        private int _sceneCount, _motionCount, _keyCount;
        private long _haze, _sceneNodes, _loopFn, _clearFn, _fwords, _fcln, _notes, _voice, _rowBits, _rowWords, _testSe, _dialogs, _windows, _dx, _dy, _radii, _colours, _config;
        private int _hazeEntries, _sceneNodeCount, _voiceRecords, _windowCount;
        private long[] _flowKeep = new long[3], _historyKeep = new long[3];
        private long _groups, _aliases, _special, _seen, _music, _extras, _trophy, _catJump, _catJoin, _columns, _edges, _choice;
        private int _groupCount, _aliasCount, _specialCount, _musicCount, _extraCount, _trophyCount, _catCount, _columnCount, _edgeCount, _choiceCount;
        private long[] _saveTitle = new long[2], _phaseTitle = new long[3], _flowTitle = new long[2], _voiceName = new long[2], _bannerRects = new long[2];
        private long _flowLine, _voiceFace, _guideIcon, _guideWord, _bannerKeys, _titleBg;
        private List<short> _sceneIdList = [];
        private List<string> _movieNames = [];

        public Session(EbootReader reader)
        {
            _r = reader;
            SelfImage image = reader.Image;
            Seg Make(string name, ProgramHeader ph)
            {
                long fo = image.PhdrFileOffset(ph) ?? throw new EbootFormatException($"segment {name} has no file bytes");
                return new Seg(name, (long)ph.VAddr, image.Data.Slice(checked((int)fo), checked((int)ph.FileSize)));
            }

            _text = Make("text", image.Text);
            _data = Make("data", image.DataSegment);
            ProgramHeader relro = image.Relro
                ?? image.LoadSegments().FirstOrDefault(p => p.Index != image.Text.Index && p.Index != image.DataSegment.Index && (p.Flags & 3) == 0)
                ?? throw new EbootFormatException("no read-only data segment (SCE_RELRO)");
            _relro = Make("relro", relro);
            _segs = [_text, _relro, _data];
            ScanLea();
        }

        public IReadOnlyList<TableLocation> Found => _found;

        public IReadOnlyList<(string Name, string Problem)> Missing => _missing;

        // ------------------------------------------------------------------------------------------------------ driver

        public void Run()
        {
            Step("HunexString", FindHunex);
            Step("ImageTable", FindImageTable);
            Step("ImageNames", FindNameLists);
            Step("MovieTable", FindMovies);
            Step("LanguageSuffixes", FindSuffixes);
            Step("PrologueTable", FindPrologue);
            Step("UiText", FindUiText);
            Step("UiTables", FindUiTables);
            Step("FontTags", FindFontTagsAndWideBase);
            Step("FontRecords", FindFontRecords);
            Step("FontLookups", FindFontLookups);
            Step("TestSeNames", FindTestSe);
            Step("VoiceRowWords", FindRowWords);
            Step("VoiceSpeakers", FindVoiceSpeakers);
            Step("HistoryKeepLists", FindHistoryListsAndSceneIds);
            Step("FlowJumpKeepLists", FindFlowJumpLists);
            Step("FlowTitleCells", FindFlowCells);
            Step("Haze", FindHaze);
            Step("WindowTypes", FindWindowCluster);
            Step("FclnLines", FindFclnLines);
            Step("FlowEdges", FindEdgeCluster);
            Step("TitleBackground", FindTitleBackground);
            Step("MotionEnds", FindMotionTables);
            Step("MotionKeys", FindMotionKeys);
            Step("ConfigPages", FindConfigPages);
            Step("SeenMap", FindSeenMapAndRowBits);
            Step("FclnNotes", FindFclnNotes);
            Step("BannerKeys", FindBannerAndDataCells);
            Step("Music", FindMusic);
            Step("Extras", FindExtras);
            Step("DialogMessages", FindDialogs);
            Step("TextColours", FindTextColours);
            Step("GalleryGroups", FindGallery);
            Step("FlowColumns", FindColumns);
            Step("CategoryJumpTable", FindCategorySwitch);
            Step("MotionLoopFunction", FindMotionLoopFunction);
            Step("JumpClearFunction", FindJumpClearFunction);
        }

        private void Step(string name, Action find)
        {
            try
            {
                find();
            }
            catch (Exception e) when (e is Containers.ContainerFormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException
                                          or OverflowException or KeyNotFoundException or ArithmeticException)
            {
                _missing.Add((name, e.Message));
            }
        }

        private void Ok(string name, long va, int count, LocateMethod method, string detail) => _found.Add(new TableLocation(name, va, count, method, detail));

        private static EbootFormatException Fail(string message) => new(message);

        public EbootLayout? Build()
        {
            if (_missing.Count > 0)
            {
                return null;
            }

            return new EbootLayout
            {
                IsAnalysed = false,
                ImageTable = _imageTable,
                ImageRecords = _imageRecords,
                ImageSlots = _imageSlots,
                ImageNames = _imageNames,
                OldImageNames = _oldImageNames,
                MovieTable = _movieTable,
                MovieRecords = _movieRecords,
                LanguageSuffixes = _suffixes,
                LanguageCount = _languageCount,
                PrologueTable = _prologue,
                UiText = _uiText,
                UiTextCount = _uiTextCount,
                HunexString = _hunex,
                UiTables = _uiTables,
                FontLookups = _lookups,
                FontRecords = _records,
                FontWideBase = _fontWide,
                FontTags = _fontTags,
                SceneIds = _sceneIds,
                SceneCount = _sceneCount,
                MotionMasks = _masks,
                MotionDurations = _durations,
                MotionEnds = _ends,
                MotionKeys = _keys,
                MotionCount = _motionCount,
                KeyframeCount = _keyCount,
                Haze = _haze,
                HazeEntries = _hazeEntries,
                SceneNodes = _sceneNodes,
                SceneNodeCount = _sceneNodeCount,
                MotionLoopFunction = _loopFn,
                JumpClearFunction = _clearFn,
                FWordsBase = _fwords,
                FlowJumpKeepLists = _flowKeep,
                HistoryKeepLists = _historyKeep,
                FclnLines = _fcln,
                FclnNotes = _notes,
                VoiceSpeakers = _voice,
                VoiceSpeakerRecords = _voiceRecords,
                VoiceRowBits = _rowBits,
                VoiceRowWords = _rowWords,
                TestSeNames = _testSe,
                DialogMessages = _dialogs,
                WindowTypes = _windows,
                WindowTypeCount = _windowCount,
                WindowDx = _dx,
                WindowDy = _dy,
                ClipRadii = _radii,
                TextColours = _colours,
                ConfigPages = _config,
                GalleryGroups = _groups,
                GalleryGroupCount = _groupCount,
                GalleryAliases = _aliases,
                GalleryAliasCount = _aliasCount,
                GallerySpecial = _special,
                GallerySpecialCount = _specialCount,
                SeenMap = _seen,
                Music = _music,
                MusicCount = _musicCount,
                Extras = _extras,
                ExtraCount = _extraCount,
                TrophyScenes = _trophy,
                TrophySceneCount = _trophyCount,
                CategoryJumpTable = _catJump,
                CategoryJoin = _catJoin,
                CategoryCount = _catCount,
                FlowColumns = _columns,
                FlowColumnCount = _columnCount,
                FlowEdges = _edges,
                FlowEdgeCount = _edgeCount,
                ChoiceFlags = _choice,
                ChoiceFlagCount = _choiceCount,
                SaveTitleCells = _saveTitle,
                PhaseTitleCells = _phaseTitle,
                FlowTitleCells = _flowTitle,
                FlowLineCells = _flowLine,
                VoiceFaceCells = _voiceFace,
                VoiceNameCells = _voiceName,
                GuideIconCells = _guideIcon,
                GuideWordCells = _guideWord,
                BannerRects = _bannerRects,
                BannerKeys = _bannerKeys,
                TitleBackground = _titleBg,
            };
        }

        // ----------------------------------------------------------------------------------------------------- helpers

        private Seg? SegOf(long va) => _segs.FirstOrDefault(s => s.Contains(va));

        private bool IsStringPointer(ulong p) => p != 0 && p < long.MaxValue && SegOf((long)p) is not null;

        private bool InText(long va) => _text.Contains(va);

        private byte U8(long va) => _r.Read(va, 1)[0];

        private short I16(long va) => _r.I16(va);

        private ushort U16(long va) => BinaryPrimitives.ReadUInt16LittleEndian(_r.Read(va, 2));

        private int I32(long va) => _r.I32(va);

        private uint U32(long va) => _r.U32(va);

        private long I64(long va) => BinaryPrimitives.ReadInt64LittleEndian(_r.Read(va, 8));

        private ulong U64(long va) => _r.U64(va);

        private float F32(long va) => BinaryPrimitives.ReadSingleLittleEndian(_r.Read(va, 4));

        private bool Readable(long va, int n) => SegOf(va) is { } s && va + n <= s.End;

        /// <summary>The NUL-terminated ASCII string at a VA, or null when it is not readable, longer than <paramref name="limit"/> or not printable ASCII.</summary>
        private string? AsciiString(long va, int limit = 96)
        {
            if (SegOf(va) is not { } s)
            {
                return null;
            }

            ReadOnlySpan<byte> window = s.Bytes.Span[s.Offset(va)..];
            window = window[..Math.Min(limit, window.Length)];
            int nul = window.IndexOf((byte)0);
            if (nul < 0)
            {
                return null;
            }

            foreach (byte b in window[..nul])
            {
                if (b is < 0x20 or > 0x7E)
                {
                    return null;
                }
            }

            return Encoding.ASCII.GetString(window[..nul]);
        }

        private static List<int> IndexOfAll(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> needle)
        {
            var list = new List<int>();
            int start = 0;
            while (start <= hay.Length - needle.Length)
            {
                int i = hay[start..].IndexOf(needle);
                if (i < 0)
                {
                    break;
                }

                list.Add(start + i);
                start += i + 1;
            }

            return list;
        }

        private static long Align(long v, int a) => (v + a - 1) / a * a;

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && MathF.Abs(v) < 1e6f;

        /// <summary>Every target of a 7-byte rip-relative <c>lea r64, [rip+disp32]</c> in the text segment: the addresses the code loads tables from.</summary>
        private void ScanLea()
        {
            ReadOnlySpan<byte> code = _text.Bytes.Span;
            for (int i = 0; i + 7 <= code.Length; i++)
            {
                byte rex = code[i];
                if ((rex != 0x48 && rex != 0x4C) || code[i + 1] != 0x8D || (code[i + 2] & 0xC7) != 0x05)
                {
                    continue;
                }

                int disp = BinaryPrimitives.ReadInt32LittleEndian(code[(i + 3)..]);
                _lea.Add(_text.Va + i + 7 + disp);
            }
        }

        private IEnumerable<long> LeaTargetsIn(Seg seg) => _lea.Where(seg.Contains).Order();

        /// <summary>Searches <paramref name="seg"/> for a masked byte pattern (null = any byte); the VAs of every match.</summary>
        private List<long> FindPattern(Seg seg, byte?[] pattern)
        {
            var hits = new List<long>();
            ReadOnlySpan<byte> s = seg.Bytes.Span;
            int first = Array.FindIndex(pattern, b => b.HasValue);
            byte lead = pattern[first]!.Value;
            for (int i = 0; i + pattern.Length <= s.Length; i++)
            {
                if (s[i + first] != lead)
                {
                    continue;
                }

                bool ok = true;
                for (int k = 0; k < pattern.Length; k++)
                {
                    if (pattern[k] is { } b && s[i + k] != b)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    hits.Add(seg.Va + i);
                }
            }

            return hits;
        }

        private static byte?[] Pattern(params object[] parts)
        {
            var list = new List<byte?>();
            foreach (object p in parts)
            {
                switch (p)
                {
                    case int v:
                        list.Add((byte)v);
                        break;
                    case string s when s == "....":
                        list.AddRange([null, null, null, null]);
                        break;
                    case string s when s == ".":
                        list.Add(null);
                        break;
                    default:
                        throw new ArgumentException("pattern part", nameof(parts));
                }
            }

            return [.. list];
        }

        // ---------------------------------------------------------------------------------------------------- strings

        private void FindHunex()
        {
            List<int> hits = IndexOfAll(_text.Bytes.Span, "Hunex Consumer Library\0"u8);
            _hunex = hits.Count > 0 ? _text.Va + hits[0] : throw Fail("the string \"Hunex Consumer Library\" is not in the text segment: not the HuneX engine");
            Ok("HunexString", _hunex, 0, LocateMethod.Content, hits.Count == 1 ? "unique" : $"{hits.Count} copies, first taken");
        }

        // ---------------------------------------------------------------------------------------------------- catalog

        /// <summary>The image table: record 0 names BLACK.mzp (name0 pointer 0, file pointer at +8), record 1 BLUE.mzp; used records contiguous, then empty slots.</summary>
        private void FindImageTable()
        {
            const int Rec = EbootAddresses.ImageRecordSize;
            ReadOnlySpan<byte> data = _data.Bytes.Span;
            byte[] ptr = new byte[8];
            foreach (int s in IndexOfAll(_text.Bytes.Span, "BLACK.mzp\0"u8))
            {
                BinaryPrimitives.WriteUInt64LittleEndian(ptr, (ulong)(_text.Va + s));
                foreach (int p in IndexOfAll(data, ptr))
                {
                    int rec = p - 8;
                    if (rec < 0 || rec + (2 * Rec) > data.Length || BinaryPrimitives.ReadUInt64LittleEndian(data[rec..]) != 0)
                    {
                        continue;
                    }

                    string? second = AsciiString((long)BinaryPrimitives.ReadUInt64LittleEndian(data[(rec + Rec + 8)..]), 48);
                    if (!string.Equals(second, "BLUE.mzp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int used = 0;
                    while (rec + ((used + 1) * Rec) <= data.Length && BinaryPrimitives.ReadUInt64LittleEndian(data[(rec + (used * Rec) + 8)..]) != 0)
                    {
                        used++;
                    }

                    int empty = 0;
                    while (true)
                    {
                        int o = rec + ((used + empty) * Rec);
                        if (o + Rec > data.Length || BinaryPrimitives.ReadUInt64LittleEndian(data[(o + 8)..]) != 0
                            || BinaryPrimitives.ReadUInt64LittleEndian(data[(o + 16)..]) != 0 || BinaryPrimitives.ReadInt32LittleEndian(data[(o + 0x20)..]) != -1)
                        {
                            break;
                        }

                        empty++;
                    }

                    if (used < 1000 || empty == 0)
                    {
                        throw Fail($"image table at 0x{_data.Va + rec:x}: {used} records and {empty} empty slots");
                    }

                    _imageTable = _data.Va + rec;
                    _imageRecords = used;
                    _imageSlots = used + empty;
                    Ok("ImageTable", _imageTable, used, LocateMethod.Content, $"{used} records, {empty} empty slots, BLACK.mzp / BLUE.mzp first");
                    return;
                }
            }

            throw Fail("no image table (no record pair naming BLACK.mzp and BLUE.mzp)");
        }

        /// <summary>The picture-name lists: 32-byte records, BLACK then BLUE, CR LF at bytes 30..31, 0xFF after the last record; the one with one record per image record is the live list.</summary>
        private void FindNameLists()
        {
            ReadOnlySpan<byte> data = _data.Bytes.Span;
            byte[] first = new byte[32];
            "BLACK"u8.CopyTo(first);
            first[30] = 0x0D;
            first[31] = 0x0A;
            var lists = new List<(long Va, int Count)>();
            foreach (int p in IndexOfAll(data, first))
            {
                if (p + 36 > data.Length || !data.Slice(p + 32, 4).SequenceEqual("BLUE"u8))
                {
                    continue;
                }

                int n = 0;
                while (p + (32 * n) < data.Length && data[p + (32 * n)] != 0xFF)
                {
                    n++;
                }

                lists.Add((_data.Va + p, n));
            }

            (long va, int count) = lists.FirstOrDefault(l => l.Count == _imageRecords + 1);
            if (va == 0)
            {
                throw Fail($"no picture-name list with {_imageRecords + 1} records (found {string.Join(", ", lists.Select(l => l.Count))})");
            }

            _imageNames = va;
            (long oldVa, int oldCount) = lists.FirstOrDefault(l => l.Va != va);
            _oldImageNames = oldVa;
            Ok("ImageNames", va, count, LocateMethod.Content, $"{count} records ending in 0xFF");
            Ok("OldImageNames", oldVa, oldCount, LocateMethod.Content, oldVa == 0 ? "none (optional)" : $"{oldCount} records");
        }

        /// <summary>The movie table: 0x30-byte records {i32 id = k, pad, char* "/app0/image/movie/..."}; ends with a negative id.</summary>
        private void FindMovies()
        {
            ReadOnlySpan<byte> data = _data.Bytes.Span;
            byte[] ptr = new byte[8];
            foreach (int s in IndexOfAll(_text.Bytes.Span, "/app0/image/movie/"u8))
            {
                if (s > 0 && _text.Bytes.Span[s - 1] != 0)
                {
                    continue;
                }

                BinaryPrimitives.WriteUInt64LittleEndian(ptr, (ulong)(_text.Va + s));
                foreach (int p in IndexOfAll(data, ptr))
                {
                    if (p < 8 || BinaryPrimitives.ReadInt32LittleEndian(data[(p - 8)..]) != 0)
                    {
                        continue;
                    }

                    long va = _data.Va + p - 8;
                    int n = 0;
                    var names = new List<string>();
                    while (Readable(va + (0x30L * n), 0x30) && I32(va + (0x30L * n)) == n)
                    {
                        string? path = AsciiString((long)U64(va + (0x30L * n) + 8), 96);
                        if (path is null || !path.StartsWith("/app0/image/movie/", StringComparison.Ordinal))
                        {
                            throw Fail($"movie record {n} at 0x{va + (0x30L * n):x} has no movie path");
                        }

                        names.Add(path);
                        n++;
                    }

                    if (n < 10 || I32(va + (0x30L * n)) >= 0)
                    {
                        throw Fail($"movie table at 0x{va:x}: {n} records and no terminator");
                    }

                    _movieTable = va;
                    _movieRecords = n;
                    _movieNames = names;
                    Ok("MovieTable", va, n, LocateMethod.Content, $"{n} records, terminator id {I32(va + (0x30L * n))}, first {names[0][18..]}");
                    return;
                }
            }

            throw Fail("no movie table (no record pointing at a /app0/image/movie/ path)");
        }

        /// <summary>The prologue ids: the four u32 (ids of prologue, prologue_en, prologue_zc, prologue_zt) somewhere in the text segment, once.</summary>
        private void FindPrologue()
        {
            int Id(string name)
            {
                int i = _movieNames.IndexOf("/app0/image/movie/" + name + ".mp4");
                return i >= 0 ? i : throw Fail($"the movie table has no {name}.mp4");
            }

            byte[] pattern = new byte[16];
            int[] ids = [Id("prologue"), Id("prologue_en"), Id("prologue_zc"), Id("prologue_zt")];
            for (int k = 0; k < 4; k++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(pattern.AsSpan(4 * k), ids[k]);
            }

            List<int> hits = IndexOfAll(_text.Bytes.Span, pattern);
            if (hits.Count != 1)
            {
                throw Fail($"the prologue id table ({string.Join(", ", ids)}) occurs {hits.Count} times in the text segment");
            }

            _prologue = _text.Va + hits[0];
            Ok("PrologueTable", _prologue, 4, LocateMethod.Content, $"ids {string.Join(", ", ids)}, unique");
        }

        /// <summary>The language suffix table: consecutive pointers to "ja", "en", "zc", "zt", "ko" in the RELRO segment.</summary>
        private void FindSuffixes()
        {
            string[] want = ExpectedLanguageSuffixes.Split(' ');
            ReadOnlySpan<byte> relro = _relro.Bytes.Span;
            for (int i = 0; i + (8 * want.Length) <= relro.Length; i += 8)
            {
                bool ok = true;
                for (int k = 0; k < want.Length && ok; k++)
                {
                    ulong p = BinaryPrimitives.ReadUInt64LittleEndian(relro[(i + (8 * k))..]);
                    ok = IsStringPointer(p) && AsciiString((long)p, 8) == want[k];
                }

                if (ok)
                {
                    _suffixes = _relro.Va + i;
                    _languageCount = want.Length;
                    Ok("LanguageSuffixes", _suffixes, want.Length, LocateMethod.Content, ExpectedLanguageSuffixes);
                    return;
                }
            }

            throw Fail("no language suffix table (five pointers to ja en zc zt ko)");
        }

        /// <summary>
        /// <c>ui_text</c>: in the RELRO segment, one block of <c>N</c> string pointers per language, entry 0 of every block pointing at "NULL" (the Chinese and Korean
        /// blocks say "NULL " with a space). The stride between the first two gives N; the language count is the suffix table's.
        /// </summary>
        private void FindUiText()
        {
            ReadOnlySpan<byte> relro = _relro.Bytes.Span;
            var nulls = new List<int>();
            for (int i = 0; i + 8 <= relro.Length; i += 8)
            {
                ulong p = BinaryPrimitives.ReadUInt64LittleEndian(relro[i..]);
                if (IsStringPointer(p) && AsciiString((long)p, 8) is { } s && s.TrimEnd() == "NULL")
                {
                    nulls.Add(i);
                }
            }

            int langs = _languageCount;
            for (int a = 0; a < nulls.Count; a++)
            {
                for (int b = a + 1; b < nulls.Count; b++)
                {
                    int stride = nulls[b] - nulls[a];
                    if (stride % 8 != 0 || stride < 8 * 100)
                    {
                        continue;
                    }

                    bool ok = true;
                    for (int k = 2; k < langs && ok; k++)
                    {
                        ok = nulls.Contains(nulls[a] + (k * stride));
                    }

                    if (!ok)
                    {
                        continue;
                    }

                    // every pointer of every block is a string pointer
                    int count = stride / 8;
                    for (int k = 0; k < langs * count; k++)
                    {
                        if (!IsStringPointer(BinaryPrimitives.ReadUInt64LittleEndian(relro[(nulls[a] + (8 * k))..])))
                        {
                            throw Fail($"ui_text entry {k} is not a string pointer");
                        }
                    }

                    _uiText = _relro.Va + nulls[a];
                    _uiTextCount = count;
                    Ok("UiText", _uiText, count, LocateMethod.Content, $"{langs} languages x {count} strings, entry 0 = NULL");
                    return;
                }
            }

            throw Fail($"no ui_text table ({nulls.Count} pointers to \"NULL\" in RELRO, none {langs} blocks apart)");
        }

        /// <summary>
        /// The UI texture registration tables: runs of 0x18-byte records {i32 0, i32 slot 0..4095, char* "name.mrg", i32 child -1..512, i32 -1 or 1} in the data and
        /// RELRO segments, each ended by a record that breaks the shape (a negative slot or a null name), in address order.
        /// </summary>
        private void FindUiTables()
        {
            var tables = new List<(long Va, int Count)>();
            foreach (Seg seg in new[] { _data, _relro })
            {
                int length = seg.Bytes.Length;
                int i = 0;
                while (i + 24 <= length)
                {
                    if (UiRecordOk(seg, i))
                    {
                        int j = i;
                        while (UiRecordOk(seg, j))
                        {
                            j += 24;
                        }

                        tables.Add((seg.Va + i, (j - i) / 24));
                        i = j + 24;
                    }
                    else
                    {
                        i += 8;
                    }
                }
            }

            tables.Sort();
            if (tables.Count < 10)
            {
                throw Fail($"only {tables.Count} UI texture registration tables found");
            }

            _uiTables = [.. tables.Select(t => t.Va)];
            Ok("UiTables", tables[0].Va, tables.Count, LocateMethod.Content, $"{tables.Count} tables, {tables.Sum(t => t.Count)} records");
        }

        /// <summary>One UI texture registration record at <paramref name="i"/> in <paramref name="seg"/>: {0, slot 0..4095, char* "name.mrg", child -1..512, -1 or 1}.</summary>
        private bool UiRecordOk(Seg seg, int i)
        {
            ReadOnlySpan<byte> s = seg.Bytes.Span;
            if (i + 24 > s.Length)
            {
                return false;
            }

            int state = BinaryPrimitives.ReadInt32LittleEndian(s[i..]);
            int slot = BinaryPrimitives.ReadInt32LittleEndian(s[(i + 4)..]);
            ulong p = BinaryPrimitives.ReadUInt64LittleEndian(s[(i + 8)..]);
            int child = BinaryPrimitives.ReadInt32LittleEndian(s[(i + 16)..]);
            int child2 = BinaryPrimitives.ReadInt32LittleEndian(s[(i + 20)..]);
            if (state != 0 || slot < 0 || slot > 4095 || child < -1 || child > 512 || child2 is not (-1 or 1) || !IsStringPointer(p))
            {
                return false;
            }

            string? name = AsciiString((long)p, 64);
            return name is not null && Catalog.ImageCatalog.IsFileName(name) && name.EndsWith(".mrg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The sheets a UI registration table registers (the record names, lower case without the extension) and its record count.</summary>
        private (HashSet<string> Sheets, int Records) UiTableSheets(long va)
        {
            var sheets = new HashSet<string>(StringComparer.Ordinal);
            int n = 0;
            while (I32(va + (24L * n) + 4) >= 0 && AsciiString((long)U64(va + (24L * n) + 8), 64) is { Length: > 4 } name)
            {
                sheets.Add(name[..^4].ToLowerInvariant());
                n++;
            }

            return (sheets, n);
        }

        /// <summary>The first UI registration table (after <paramref name="after"/>) that registers <paramref name="sheet"/> (<c>disp_parts</c>) with at least <paramref name="minRecords"/> records.</summary>
        private long UiTableFor(string sheet, int minRecords, long after = 0)
        {
            foreach (long va in _uiTables)
            {
                if (va <= after)
                {
                    continue;
                }

                (HashSet<string> sheets, int n) = UiTableSheets(va);
                if (sheets.Contains(sheet) && n >= minRecords)
                {
                    return va;
                }
            }

            throw Fail($"no UI registration table for {sheet}.mrg with {minRecords} records");
        }

        // ------------------------------------------------------------------------------------------------------ fonts

        /// <summary>The tag names ([heart], [heart2], [star] ...) are 11-byte records; the wide-grid bases follow them at the next even address.</summary>
        private void FindFontTagsAndWideBase()
        {
            byte[] pattern = new byte[33];
            "[heart]"u8.CopyTo(pattern);
            "[heart2]"u8.CopyTo(pattern.AsSpan(11));
            "[star]"u8.CopyTo(pattern.AsSpan(22));
            // the names exist twice (the text renderer has its own copy, text.md 6.4): the font block is the one that ends in the five wide-grid base slots, right after
            // the glyph record tables, and is loaded by code
            List<long> hits = [.. IndexOfAll(_text.Bytes.Span, pattern).Select(p => _text.Va + p)];
            List<long> fontBlocks = [.. hits.Where(t =>
            {
                long wide = Align(t + (EbootFontData.TagStride * EbootFontData.TagCount), 2);
                return Readable(wide, 10) && Enumerable.Range(0, 5).All(k => U16(wide + (2 * k)) is >= 0x21 and <= 0x61) && _lea.Contains(t)
                    && Enumerable.Range(1, 2).Any(k => GlyphRecordOk(t - (10L * k)) || GlyphRecordOk(t - (10L * k) - 6));
            })];
            if (fontBlocks.Count != 1)
            {
                throw Fail($"the [tag] name block occurs {hits.Count} times, {fontBlocks.Count} of them followed by texture slots after glyph records");
            }

            _fontTags = fontBlocks[0];
            _fontWide = Align(_fontTags + (EbootFontData.TagStride * EbootFontData.TagCount), 2);
            int[] wide = [.. Enumerable.Range(0, 5).Select(k => (int)U16(_fontWide + (2 * k)))];
            Ok("FontTags", _fontTags, EbootFontData.TagCount, LocateMethod.Content, $"[heart] [heart2] [star] ... ({hits.Count} copies, the one before the wide bases)");
            Ok("FontWideBase", _fontWide, 5, LocateMethod.Anchored, $"slots {string.Join(", ", wide)}");
        }

        private bool GlyphRecordOk(long va)
        {
            if (!Readable(va, 10))
            {
                return false;
            }

            ReadOnlySpan<byte> r = _r.Read(va, 10);
            int slot = BinaryPrimitives.ReadUInt16LittleEndian(r);
            int x = BinaryPrimitives.ReadUInt16LittleEndian(r[2..]);
            int y = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
            int w = BinaryPrimitives.ReadInt16LittleEndian(r[6..]);
            int h = BinaryPrimitives.ReadUInt16LittleEndian(r[8..]);
            return slot is >= 0x21 and <= 0x61 && x < 1024 && y < 512 && w is >= 1 and <= 128 && h == 73;
        }

        /// <summary>
        /// The 17 glyph record tables lie back to back before the tag block; each starts where the code loads it (a lea target) and its first record is a glyph
        /// rectangle (slot 0x21..0x61, h = 73). Names by engine order [H]; counts: the feeding lookup's count, capped by the distance to the next table.
        /// </summary>
        private void FindFontRecords()
        {
            long lo = _fontTags - 0x14000;
            List<long> starts = [.. _lea.Where(t => t >= lo && t < _fontTags && GlyphRecordOk(t)).Order()];
            if (starts.Count != RecordNames.Length)
            {
                throw Fail($"{starts.Count} glyph record tables before the tag block, expected {RecordNames.Length}");
            }

            // counts come after the lookups are known: keep the starts, fill in FindFontLookups
            _records = [.. starts.Select((va, i) => new FontRecordSpec(RecordNames[i], va, 0, 0))];
        }

        /// <summary>A glyph lookup record at data offset <paramref name="i"/>: {u64 key != 0, s16 index >= 0, 6 zero bytes}.</summary>
        private bool LookupRecordOk(int i)
        {
            ReadOnlySpan<byte> data = _data.Bytes.Span;
            return i + 16 <= data.Length
                && BinaryPrimitives.ReadUInt16LittleEndian(data[(i + 10)..]) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 12)..]) == 0
                && BinaryPrimitives.ReadInt16LittleEndian(data[(i + 8)..]) >= 0 && BinaryPrimitives.ReadUInt64LittleEndian(data[i..]) != 0;
        }

        /// <summary>
        /// The 16 lookup tables: one contiguous run of 16-byte records {u64 key, s16 index, 6 zero bytes} in the data segment, split where the code loads a table
        /// (lea targets), each piece sorted by key with indices a permutation of 0..n-1. Names by engine order [H].
        /// </summary>
        private void FindFontLookups()
        {
            if (_records.Count == 0)
            {
                throw Fail("the glyph record tables were not found, so the lookups cannot be named");
            }

            int length = _data.Bytes.Length;
            (long Start, long End) best = default;
            int i = 0;
            while (i + 16 <= length)
            {
                if (LookupRecordOk(i))
                {
                    int j = i;
                    while (LookupRecordOk(j))
                    {
                        j += 16;
                    }

                    if ((j - i) / 16 >= 1000 && j - i > best.End - best.Start)
                    {
                        best = (_data.Va + i, _data.Va + j);
                    }

                    i = j + 16;
                }
                else
                {
                    i += 8;
                }
            }

            if (best.End == 0)
            {
                throw Fail("no run of glyph lookup records in the data segment");
            }

            List<long> starts = [.. _lea.Where(t => t >= best.Start && t < best.End).Order()];
            var pieces = new List<(long Va, int Count)>();
            for (int k = 0; k < starts.Count; k++)
            {
                long a = starts[k];
                long b = k + 1 < starts.Count ? starts[k + 1] : best.End;
                int n = (int)((b - a) / 16);
                var idx = new int[n];
                ulong prev = 0;
                for (int e = 0; e < n; e++)
                {
                    ulong key = U64(a + (16L * e));
                    if (e > 0 && key <= prev)
                    {
                        throw Fail($"glyph lookup table at 0x{a:x}: keys not strictly increasing at entry {e}");
                    }

                    prev = key;
                    idx[e] = I16(a + (16L * e) + 8);
                }

                Array.Sort(idx);
                if (idx.Where((v, e) => v != e).Any())
                {
                    throw Fail($"glyph lookup table at 0x{a:x}: indices are not a permutation of 0..{n - 1}");
                }

                pieces.Add((a, n));
            }

            if (pieces.Count != LookupNames.Length)
            {
                throw Fail($"{pieces.Count} glyph lookup tables in the run at 0x{best.Start:x}, expected {LookupNames.Length}");
            }

            _lookups = [.. pieces.Select((p, k) => new FontLookupSpec(LookupNames[k], p.Va, p.Count, 0))];
            Ok("FontLookups", best.Start, pieces.Count, LocateMethod.Content, $"{pieces.Count} sorted permutation tables, {pieces.Sum(p => p.Count)} entries");

            // record counts: the feeding lookup, capped by the gap to the next table (fonts.md 5.3)
            int LookupCount(string name) => _lookups.First(l => l.Name == name).Count;
            var records = new List<FontRecordSpec>();
            for (int k = 0; k < _records.Count; k++)
            {
                FontRecordSpec r = _records[k];
                long next = k + 1 < _records.Count ? _records[k + 1].Va : _fontTags;
                string lang = r.Name[..2];
                int feed = r.Name[3..] switch
                {
                    "s0" => LookupCount("narrow"),
                    "s2" => LookupCount(lang + "_s2"),
                    _ => LookupCount(lang + "_s1"),
                };
                int gap = (int)((next - r.Va) / 10);
                int count = Math.Min(feed, gap);
                if (count < 1)
                {
                    throw Fail($"glyph record table {r.Name} at 0x{r.Va:x} has no room for a record");
                }

                records.Add(r with { Count = count });
            }

            _records = records;
            Ok("FontRecords", _records[0].Va, _records.Count, LocateMethod.Content, $"{_records.Count} tables, {_records.Sum(r => r.Count)} records");
        }

        // ------------------------------------------------------------------------------------------ engine tables (text)

        /// <summary>The 14 config SE test names: inline 13-byte strings "moon_se%05d".</summary>
        private void FindTestSe()
        {
            ReadOnlySpan<byte> text = _text.Bytes.Span;
            foreach (int p in IndexOfAll(text, "moon_se"u8))
            {
                if (p > 0 && text[p - 1] != 0)
                {
                    continue;
                }

                int n = 0;
                while (p + (13 * (n + 1)) <= text.Length && AsciiString(_text.Va + p + (13 * n), 13) is { Length: 12 } s && s.StartsWith("moon_se", StringComparison.Ordinal))
                {
                    n++;
                }

                if (n >= 10)
                {
                    _testSe = _text.Va + p;
                    Ok("TestSeNames", _testSe, n, LocateMethod.Content, $"{n} names");
                    return;
                }
            }

            throw Fail("no block of moon_se names");
        }

        /// <summary>The config voice row words follow the test SE names after a few C strings and zero padding: i32 F word numbers.</summary>
        private void FindRowWords()
        {
            long va = _testSe + (13L * Tables.TestSeNames.Count);
            // skip the strings that follow ("/app0/data/if_", ".png", "conf_parts.mrg"), then the zero padding
            while (!(U8(va) == 0 && U8(va + 1) == 0))
            {
                va++;
            }

            while (U8(va) == 0)
            {
                va++;
            }

            for (int k = 0; k < Tables.VoiceSpeakerTable.RowCount; k++)
            {
                int w = I32(va + (4L * k));
                if (w is < 0 or > 1199)
                {
                    throw Fail($"voice row word {k} = {w} is not an F word number");
                }
            }

            _rowWords = va;
            Ok("VoiceRowWords", va, Tables.VoiceSpeakerTable.RowCount, LocateMethod.Anchored, "after the test SE names");
        }

        /// <summary>The voice speaker records: {char[8] key, i32 speaker, i32 0}, the first key "ZZZ", keys reversed three-letter prefixes.</summary>
        private void FindVoiceSpeakers()
        {
            ReadOnlySpan<byte> text = _text.Bytes.Span;
            byte[] first = new byte[16];
            "ZZZ"u8.CopyTo(first);
            foreach (int p in IndexOfAll(text, first))
            {
                int n = 0;
                while (p + (16 * (n + 1)) <= text.Length)
                {
                    ReadOnlySpan<byte> rec = text.Slice(p + (16 * n), 16);
                    bool key = rec[3] == 0 && rec[4..8].IndexOfAnyExcept((byte)0) < 0 && rec[..3].IndexOfAnyExcept("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"u8) < 0;
                    if (!key || BinaryPrimitives.ReadInt32LittleEndian(rec[12..]) != 0 || BinaryPrimitives.ReadInt32LittleEndian(rec[8..]) is < 0 or > 63)
                    {
                        break;
                    }

                    n++;
                }

                if (n >= 20)
                {
                    _voice = _text.Va + p;
                    _voiceRecords = n;
                    Ok("VoiceSpeakers", _voice, n, LocateMethod.Content, $"{n} records, ZZZ first");
                    return;
                }
            }

            throw Fail("no voice speaker table (records keyed ZZZ, 00A, ...)");
        }

        /// <summary>A -1-terminated i16 list of F word numbers (0..1199) at <paramref name="va"/>, or null.</summary>
        private List<int>? FWordList(long va)
        {
            var list = new List<int>();
            while (true)
            {
                short v = I16(va + (2L * list.Count));
                if (v == -1)
                {
                    return list.Count > 0 ? list : null;
                }

                if (v is < 0 or > 1199 || list.Count > 2000)
                {
                    return null;
                }

                list.Add(v);
            }
        }

        /// <summary>The first 16-aligned address at or after <paramref name="va"/> whose zero padding ends at or after it: the next table after a gap of zero bytes.</summary>
        private long AfterZeroGap(long va)
        {
            long p = va;
            while (U8(p) == 0)
            {
                p++;
            }

            return Math.Max(p / 16 * 16, va);
        }

        /// <summary>
        /// The history-record keep lists and the scene-id table follow the voice speakers: words (first entry F000), the -1-terminated scene ids, bits, bytes, each after a
        /// gap of zero bytes.
        /// </summary>
        private void FindHistoryListsAndSceneIds()
        {
            long words = AfterZeroGap(_voice + (16L * _voiceRecords));
            List<int> w = FWordList(words) ?? throw Fail($"no F word list after the voice speakers at 0x{words:x}");
            long scenes = words + (2L * (w.Count + 1));
            var ids = new List<short>();
            while (I16(scenes + (2L * ids.Count)) != -1)
            {
                short id = I16(scenes + (2L * ids.Count));
                if (id <= 0 || ids.Count > 4000)
                {
                    throw Fail($"scene id {ids.Count} = {id} at 0x{scenes:x} is not positive");
                }

                ids.Add(id);
            }

            if (ids.Count < 100 || ids.Distinct().Count() != ids.Count)
            {
                throw Fail($"the scene-id table at 0x{scenes:x} has {ids.Count} ids, {ids.Distinct().Count()} distinct");
            }

            long bits = AfterZeroGap(scenes + (2L * (ids.Count + 1)));
            List<int> b = FWordList(bits) ?? throw Fail($"no F bit list at 0x{bits:x}");
            long bytes = AfterZeroGap(bits + (2L * (b.Count + 1)));
            List<int> y = FWordList(bytes) ?? throw Fail($"no F byte list at 0x{bytes:x}");
            _historyKeep = [words, bytes, bits];
            _sceneIds = scenes;
            _sceneCount = ids.Count;
            _sceneIdList = ids;
            // the flowchart node table covers the story scripts: those whose scene id is below 3000 (the utility scripts START, ARCHIVE, QACALL, STAFFROLL, TEST and the
            // JUMP scripts have ids 3000 and up), minus script 0 [H]
            int story = 0;
            while (story < ids.Count && ids[story] < 3000)
            {
                story++;
            }

            _sceneNodeCount = story - 1;
            Ok("HistoryKeepLists", words, w.Count + b.Count + y.Count, LocateMethod.Anchored, $"words {w.Count}, bits {b.Count}, bytes {y.Count}");
            Ok("SceneIds", scenes, ids.Count, LocateMethod.Content, $"{ids.Count} distinct ids, -1 terminated; {story} story scripts");
        }

        /// <summary>The flow-jump copy of the keep lists holds the same words as the history copy; the bytes and bits follow it after zero gaps.</summary>
        private void FindFlowJumpLists()
        {
            List<int> words = FWordList(_historyKeep[0])!;
            byte[] pattern = new byte[2 * (words.Count + 1)];
            for (int k = 0; k < words.Count; k++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(pattern.AsSpan(2 * k), (short)words[k]);
            }

            BinaryPrimitives.WriteInt16LittleEndian(pattern.AsSpan(2 * words.Count), -1);
            List<long> hits = [.. IndexOfAll(_text.Bytes.Span, pattern).Select(p => _text.Va + p).Where(va => va != _historyKeep[0])];
            if (hits.Count != 1)
            {
                throw Fail($"the keep-word list occurs {hits.Count + 1} times in the text segment, expected twice");
            }

            long fw = hits[0];
            long bytes = AfterZeroGap(fw + pattern.Length);
            List<int> y = FWordList(bytes) ?? throw Fail($"no flow-jump byte list at 0x{bytes:x}");
            long bits = AfterZeroGap(bytes + (2L * (y.Count + 1)));
            List<int> b = FWordList(bits) ?? throw Fail($"no flow-jump bit list at 0x{bits:x}");
            List<int> hb = FWordList(_historyKeep[1])!;
            List<int> hbits = FWordList(_historyKeep[2])!;
            if (!y.SequenceEqual(hb) || !b.SequenceEqual(hbits))
            {
                throw Fail("the flow-jump keep lists differ from the history copy");
            }

            _flowKeep = [fw, bytes, bits];
            Ok("FlowJumpKeepLists", fw, words.Count + y.Count + b.Count, LocateMethod.Content, "equal to the history copy");
        }

        private bool CellsOk(long va, int count, bool sizes)
        {
            for (int k = 0; k < count; k++)
            {
                if (!Readable(va + (8L * k), 8))
                {
                    return false;
                }

                int a = I16(va + (8L * k)), b = I16(va + (8L * k) + 2), c = I16(va + (8L * k) + 4), e = I16(va + (8L * k) + 6);
                bool ok = sizes
                    ? a is >= 0 and <= 4096 && b is >= 0 and <= 4096 && c is >= 0 and <= 4096 && e is >= 0 and <= 4096
                    : a >= 0 && a <= c && c <= 4096 && b >= 0 && b <= e && e <= 4096;
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>A cell table anchored at a fixed offset from a found table [H]; the position is kept only when its cells validate.</summary>
        private long AnchoredCells(string name, long anchor, long offset, int count, bool sizes)
        {
            long va = anchor + offset;
            if (!CellsOk(va, count, sizes))
            {
                throw Fail($"{name}: the {count} cells at 0x{va:x} (anchor 0x{anchor:x} {(offset >= 0 ? "+" : "-")} 0x{Math.Abs(offset):x}) are not texel rectangles");
            }

            Ok(name, va, count, LocateMethod.Anchored, $"anchor 0x{anchor:x} {(offset >= 0 ? "+" : "-")} 0x{Math.Abs(offset):x}");
            return va;
        }

        /// <summary>The flowchart title and connector cells sit just before the flow-jump keep lists.</summary>
        private void FindFlowCells()
        {
            long fw = _flowKeep[0];
            _flowTitle = [AnchoredCells("FlowTitleCells[0]", fw, -0x370, 30, true), AnchoredCells("FlowTitleCells[1]", fw, -0x280, 30, true)];
            _flowLine = AnchoredCells("FlowLineCells", fw, -0x190, 12, true);
        }

        /// <summary>The haze table: entry 0 is 40 zero bytes, entry 1 the default parameters (1, 1, 1, 1, 1, 1, 2, 1, 1, 0); it runs while the entries are finite floats.</summary>
        private void FindHaze()
        {
            byte[] pattern = new byte[80];
            float[] entry1 = [1, 1, 1, 1, 1, 1, 2, 1, 1, 0];
            for (int k = 0; k < 10; k++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(40 + (4 * k)), entry1[k]);
            }

            List<int> hits = IndexOfAll(_text.Bytes.Span, pattern);
            if (hits.Count != 1)
            {
                throw Fail($"the haze table head (zero entry, default entry) occurs {hits.Count} times");
            }

            long va = _text.Va + hits[0];
            int n = 0;
            while (Readable(va + (40L * n), 40) && Enumerable.Range(0, 10).All(k => Finite(F32(va + (40L * n) + (4 * k)))))
            {
                n++;
            }

            if (n < 100)
            {
                throw Fail($"the haze table at 0x{va:x} has only {n} entries");
            }

            _haze = va;
            _hazeEntries = n;
            Ok("Haze", va, n, LocateMethod.Content, $"{n} entries of 10 floats");
        }

        private bool WindowRecordOk(long va)
        {
            if (!Readable(va, 12))
            {
                return false;
            }

            uint slot = U32(va);
            float x = F32(va + 4), y = F32(va + 8);
            // an all-zero record is not a window type: the table of float pairs that follows the window types begins with zero pairs
            return slot < 4096 && Finite(x) && Finite(y) && MathF.Abs(x) < 4096 && MathF.Abs(y) < 4096 && (slot != 0 || x != 0f || y != 0f);
        }

        /// <summary>
        /// The message window cluster: the six soft-clip radii (10, 32, 40, 64, 128, 256 as floats), then at the next 16-byte boundaries the 27 x offsets, the 37 y offsets
        /// and the window types {u32 slot, f32 x, f32 y} whose first slots count up by one [H for the offset counts, which are code constants].
        /// </summary>
        private void FindWindowCluster()
        {
            byte[] pattern = new byte[24];
            float[] radii = [10, 32, 40, 64, 128, 256];
            for (int k = 0; k < 6; k++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(4 * k), radii[k]);
            }

            List<int> hits = IndexOfAll(_text.Bytes.Span, pattern);
            if (hits.Count != 1)
            {
                throw Fail($"the soft-clip radii occur {hits.Count} times");
            }

            _radii = _text.Va + hits[0];
            _dx = Align(_radii + 24, 16);
            _dy = Align(_dx + (4 * Tables.MessageWindowTable.DxCount), 16);
            long windows = Align(_dy + (4 * Tables.MessageWindowTable.DyCount), 16);
            uint first = U32(windows);
            for (int k = 0; k < 20; k++)
            {
                if (!WindowRecordOk(windows + (12L * k)) || U32(windows + (12L * k)) != first + (uint)k)
                {
                    throw Fail($"no window type table at 0x{windows:x} (record {k} breaks the slot sequence)");
                }
            }

            int n = 0;
            while (WindowRecordOk(windows + (12L * n)))
            {
                n++;
            }

            for (int k = 0; k < Tables.MessageWindowTable.DxCount + Tables.MessageWindowTable.DyCount; k++)
            {
                if (!Finite(F32(_dx + (4L * k))))
                {
                    throw Fail($"window offset {k} is not a finite float");
                }
            }

            _windows = windows;
            _windowCount = n;
            Ok("ClipRadii", _radii, 6, LocateMethod.Content, "10 32 40 64 128 256");
            Ok("WindowDx", _dx, Tables.MessageWindowTable.DxCount, LocateMethod.Anchored, "after the radii");
            Ok("WindowDy", _dy, Tables.MessageWindowTable.DyCount, LocateMethod.Anchored, "after the x offsets");
            Ok("WindowTypes", windows, n, LocateMethod.Content, $"{n} records, slots from {first}");
        }

        private bool FclnKeyOk(long va)
        {
            if (!Readable(va, 36))
            {
                return false;
            }

            int t = I32(va);
            if (t == -1)
            {
                return Enumerable.Range(1, 8).All(k => I32(va + (4 * k)) == -1);
            }

            int cx = I32(va + 4), cy = I32(va + 8), r0 = I32(va + 12), r1 = I32(va + 16), c0 = I32(va + 20), c1 = I32(va + 24), alpha = I32(va + 28), reroll = I32(va + 32);
            return t is >= 0 and <= 600000 && cx is >= -2000 and <= 2000 && cy is >= -2000 and <= 2000 && r0 is >= 0 and <= 4000 && r1 is >= 0 and <= 4000
                && c0 is >= 0 and <= 4000 && c1 is >= 0 and <= 4000 && alpha is >= 0 and <= 255 && reroll is >= 0 and <= 600000;
        }

        /// <summary>
        /// The focus-line keys: 30 x 4 records of 9 i32 loaded by the code (a lea target); the first key at time 0 with a centre on the screen, a visible alpha and an outer
        /// radius; at least 30 keys used and at least 10 unused (all -1).
        /// </summary>
        private void FindFclnLines()
        {
            int n = Tables.FclnLineTable.Effects * Tables.FclnLineTable.KeysPerEffect;
            foreach (long t in LeaTargetsIn(_text))
            {
                if (!Readable(t, 36 * n) || I32(t) != 0 || I32(t + 4) < 100 || I32(t + 16) <= 0 || I32(t + 28) < 100)
                {
                    continue;
                }

                int used = Enumerable.Range(0, n).Count(k => I32(t + (36L * k)) != -1);
                if (used >= 30 && n - used >= 10 && Enumerable.Range(0, n).All(k => FclnKeyOk(t + (36L * k))))
                {
                    _fcln = t;
                    Ok("FclnLines", t, n, LocateMethod.Content, $"{n} keys, {Enumerable.Range(0, n).Count(k => I32(t + (36L * k)) != -1)} used");
                    return;
                }
            }

            throw Fail("no focus-line key table (120 keys of 9 ints at a code reference)");
        }

        private bool EdgeFlag(int v) => v == -1 || v is >= 500 and <= 1300;

        private bool EdgeOk(long va) => Readable(va, 48) && I32(va) is >= 0 and < 2000 && I32(va + 4) is >= 0 and < 2000 && Enumerable.Range(2, 10).All(k => EdgeFlag(I32(va + (4 * k))));

        /// <summary>
        /// The flowchart edge cluster: the 48-byte edges {from, to, 10 flag ids} at a code reference, the scene-node table right after them, and before them (after zero
        /// padding) the 12-byte choice flag triples and the 8-byte trophy scene pairs {scene, -1}.
        /// </summary>
        private void FindEdgeCluster()
        {
            foreach (long t in LeaTargetsIn(_text))
            {
                if (!Enumerable.Range(0, 600).All(k => EdgeOk(t + (48L * k))))
                {
                    continue;
                }

                int n = 0;
                while (EdgeOk(t + (48L * n)))
                {
                    n++;
                }

                _edges = t;
                _edgeCount = n;
                _sceneNodes = t + (48L * n);
                if (_sceneNodeCount < 100 || Enumerable.Range(0, _sceneNodeCount).Any(k => I32(_sceneNodes + (4L * k)) is < -1 or > 4000))
                {
                    throw Fail($"the scene-node table at 0x{_sceneNodes:x} ({_sceneNodeCount} entries) holds values that are not node indices");
                }

                long end = t;
                while (U32(end - 4) == 0)
                {
                    end -= 4;
                }

                int choice = 0;
                while (Enumerable.Range(0, 3).All(k => EdgeFlag(I32(end - (12L * (choice + 1)) + (4 * k)))))
                {
                    choice++;
                }

                if (choice < 10)
                {
                    throw Fail($"only {choice} choice flag triples before the edges at 0x{t:x}");
                }

                _choice = end - (12L * choice);
                _choiceCount = choice;
                end = _choice;
                while (U32(end - 4) == 0)
                {
                    end -= 4;
                }

                int trophy = 0;
                while (I32(end - (8L * (trophy + 1)) + 4) == -1 && I32(end - (8L * (trophy + 1))) is >= 0 and < 2000)
                {
                    trophy++;
                }

                if (trophy < 5)
                {
                    throw Fail($"only {trophy} trophy scene pairs before the choice flags at 0x{_choice:x}");
                }

                _trophy = end - (8L * trophy);
                _trophyCount = trophy;
                Ok("FlowEdges", t, n, LocateMethod.Content, $"{n} edges");
                Ok("SceneNodes", _sceneNodes, _sceneNodeCount, LocateMethod.Anchored, $"after the edges, {_sceneNodeCount} story scripts");
                Ok("ChoiceFlags", _choice, choice, LocateMethod.Anchored, $"{choice} triples before the edges");
                Ok("TrophyScenes", _trophy, trophy, LocateMethod.Anchored, $"{trophy} pairs before the choice flags");
                return;
            }

            throw Fail("no flowchart edge table (600 records of {from, to, flags} at a code reference)");
        }

        /// <summary>The six title background indices sit right before the string "caution_ja.mzp".</summary>
        private void FindTitleBackground()
        {
            List<int> hits = IndexOfAll(_text.Bytes.Span, "caution_ja.mzp\0"u8);
            if (hits.Count != 1)
            {
                throw Fail($"the string caution_ja.mzp occurs {hits.Count} times");
            }

            long va = _text.Va + hits[0] - 24;
            int[] v = [.. Enumerable.Range(0, 6).Select(k => I32(va + (4 * k)))];
            if (v.Any(x => x is < 0 or > 15))
            {
                throw Fail($"the six words before caution_ja.mzp ({string.Join(", ", v)}) are not title background indices");
            }

            _titleBg = va;
            Ok("TitleBackground", va, 6, LocateMethod.Anchored, string.Join(" ", v));
        }

        /// <summary>
        /// The motion tables: the keyframe end indices are the longest run of non-decreasing i64 (at a code reference) in the text segment; the durations are the same number
        /// of i64 right before them, the field masks (one more word) before those.
        /// </summary>
        private void FindMotionTables()
        {
            (long Va, int Count) best = default;
            foreach (long t in LeaTargetsIn(_text))
            {
                int n = 0;
                long prev = 0;
                while (Readable(t + (8L * n), 8))
                {
                    long v = I64(t + (8L * n));
                    if (v < prev || v > 500_000)
                    {
                        break;
                    }

                    prev = v;
                    n++;
                }

                if (n >= 20000 && n > best.Count)
                {
                    best = (t, n);
                }
            }

            if (best.Count == 0)
            {
                throw Fail("no keyframe end-index table (a run of at least 20,000 non-decreasing words)");
            }

            _ends = best.Va;
            _motionCount = best.Count;
            _keyCount = checked((int)I64(_ends + (8L * (best.Count - 1))));
            _durations = _ends - (8L * best.Count);
            _masks = _durations - (8L * (best.Count + 1));
            if (!_lea.Contains(_durations) || !_lea.Contains(_masks))
            {
                throw Fail($"the durations (0x{_durations:x}) or the masks (0x{_masks:x}) before the end indices are not referenced by code");
            }

            for (int k = 0; k < best.Count; k++)
            {
                long d = I64(_durations + (8L * k));
                if (d is < 0 or > 36_000_000)
                {
                    throw Fail($"motion {k + 1}: duration {d} ms");
                }
            }

            if (U64(_masks) != 0)
            {
                throw Fail("field mask 0 is not zero");
            }

            Ok("MotionEnds", _ends, best.Count, LocateMethod.Content, $"{best.Count} motions, {_keyCount} keyframes");
            Ok("MotionDurations", _durations, best.Count, LocateMethod.Anchored, "before the end indices");
            Ok("MotionMasks", _masks, best.Count + 1, LocateMethod.Anchored, "before the durations");
        }

        /// <summary>The keyframes: 72-byte records at a code reference in the data segment, record 0 at time 0, every time non-negative and bounded.</summary>
        private void FindMotionKeys()
        {
            foreach (long t in LeaTargetsIn(_data))
            {
                if (!Readable(t, 72 * _keyCount) || I64(t) != 0)
                {
                    continue;
                }

                bool ok = true;
                for (int k = 0; k < _keyCount && ok; k++)
                {
                    long time = I64(t + (72L * k));
                    ok = time is >= 0 and <= 10_000_000;
                }

                if (ok)
                {
                    _keys = t;
                    Ok("MotionKeys", t, _keyCount, LocateMethod.Content, $"{_keyCount} keyframes of 72 bytes");
                    return;
                }
            }

            throw Fail($"no keyframe table ({_keyCount} records of 72 bytes at a code reference in the data segment)");
        }

        private bool ConfigPageOk(long va)
        {
            if (!Readable(va, Tables.ConfigPageTable.Stride))
            {
                return false;
            }

            int first = I32(va), last = I32(va + 4), m1 = I32(va + 8), rows = I32(va + 12);
            if (first < 0 || last < first || last >= 500 || m1 != -1 || rows is < 0 or > 26)
            {
                return false;
            }

            return Enumerable.Range(0, rows).All(r => I32(va + 16 + (4L * r)) is >= 0 and <= 5);
        }

        /// <summary>The two config pages: 0x9D0-byte pages {i32 first item, i32 last, -1, row count, per-row control counts ...}, the first page with more than five rows.</summary>
        private void FindConfigPages()
        {
            for (long va = _data.Va; va + (2L * Tables.ConfigPageTable.Stride) <= _data.End; va += 16)
            {
                if (I32(va + 12) > 5 && ConfigPageOk(va) && ConfigPageOk(va + Tables.ConfigPageTable.Stride))
                {
                    _config = va;
                    _voiceFace = AnchoredCells("VoiceFaceCells", va, -480, 20, false);
                    _voiceName = [AnchoredCells("VoiceNameCells[0]", va, -320, 20, false), AnchoredCells("VoiceNameCells[1]", va, -160, 20, false)];
                    Ok("ConfigPages", va, 2, LocateMethod.Content, $"items {I32(va)}..{I32(va + 4)}, {I32(va + 12)} and {I32(va + Tables.ConfigPageTable.Stride + 12)} rows");
                    return;
                }
            }

            throw Fail("no config page pair (0x9D0-byte pages with item ids and row counts)");
        }

        /// <summary>The seen-index map: {char* name, i32 -1, i32 seen} records whose seen indices count up from the 2700s; the speaker row bits sit 0x1E0 before it.</summary>
        private void FindSeenMapAndRowBits()
        {
            for (long va = _data.Va; va + 32 <= _data.End; va += 8)
            {
                ulong p = U64(va);
                if (!IsStringPointer(p) || I32(va + 8) != -1)
                {
                    continue;
                }

                int seen = I32(va + 12);
                if (seen is < 2000 or > 6000 || !IsStringPointer(U64(va + 16)) || I32(va + 24) != -1 || I32(va + 28) != seen + 1)
                {
                    continue;
                }

                int n = 0;
                while (U64(va + (16L * n)) != 0)
                {
                    if (!IsStringPointer(U64(va + (16L * n))))
                    {
                        throw Fail($"seen-map record {n} has no name");
                    }

                    n++;
                }

                _seen = va;
                long bits = va - 0x1E0;
                for (int k = 0; k < Tables.VoiceSpeakerTable.SpeakerCount; k++)
                {
                    int bit = I32(bits + (4L * k));
                    if (bit <= 0 || (bit & (bit - 1)) != 0)
                    {
                        throw Fail($"speaker row bit {k} = 0x{bit:x} is not a power of two (0x{bits:x})");
                    }
                }

                _rowBits = bits;
                Ok("SeenMap", va, n, LocateMethod.Content, $"{n} records, seen indices from {seen}");
                Ok("VoiceRowBits", bits, Tables.VoiceSpeakerTable.SpeakerCount, LocateMethod.Anchored, "0x1E0 before the seen map, powers of two");
                return;
            }

            throw Fail("no seen-index map ({name, -1, seen} records with ascending seen indices)");
        }

        /// <summary>The three falling-note records (0x58 bytes: u16 image, u16 frames 1..24, the frame order, i16 w h step, six floats, i32 floor).</summary>
        private void FindFclnNotes()
        {
            const int Stride = Tables.FclnNoteTable.Stride;
            bool NoteOk(long va)
            {
                if (!Readable(va, Stride))
                {
                    return false;
                }

                int image = U16(va), frames = U16(va + 2);
                int w = I16(va + 0x36), h = I16(va + 0x38), step = I16(va + 0x3A), floor = I32(va + 0x54);
                if (image is <= 0 or >= 1000 || frames is < 1 or > 24 || w is < 1 or > 1024 || h is < 1 or > 1024 || step is < 1 or > 60 || floor is <= 0 or > 1080)
                {
                    return false;
                }

                return Enumerable.Range(0, frames).All(f => I16(va + 4 + (2L * f)) is >= 0 and < 24) && Enumerable.Range(0, 6).All(k => Finite(F32(va + 0x3C + (4L * k))));
            }

            for (long va = _data.Va; va + (3L * Stride) <= _data.End; va += 8)
            {
                if (NoteOk(va) && NoteOk(va + Stride) && NoteOk(va + (2L * Stride)))
                {
                    _notes = va;
                    Ok("FclnNotes", va, 3, LocateMethod.Content, $"frames {U16(va + 2)}, {U16(va + Stride + 2)}, {U16(va + (2L * Stride) + 2)}");
                    return;
                }
            }

            throw Fail("no falling-note records");
        }

        /// <summary>The banner keys and rectangles sit 0xB0 before the effect registration table; the other data cells at fixed offsets from their sheet's registration table.</summary>
        private void FindBannerAndDataCells()
        {
            long effect = UiTableFor("border_parts", 20);
            long keys = effect - 0xB0;
            for (int k = 0; k < 20; k++)
            {
                float f = F32(keys + (4L * k));
                if (!Finite(f) || f < -1f || f > 4f)
                {
                    throw Fail($"banner key {k} = {f} is not a scale (0x{keys:x})");
                }
            }

            _bannerKeys = keys;
            Ok("BannerKeys", keys, 10, LocateMethod.Anchored, "0xB0 before the effect registration table");
            _bannerRects = [AnchoredCells("BannerRects[0]", keys, 80, 1, false), AnchoredCells("BannerRects[1]", keys, 96, 1, false)];
            long menu = UiTableFor("menu_parts", 10);          // 0x7A67F0 in the verified build; the help cells lie before it
            _guideIcon = AnchoredCells("GuideIconCells", menu, -0x660, 23, false);
            _guideWord = AnchoredCells("GuideWordCells", menu, -0x5A0, 27 * 5, false);
            long menu2 = UiTableFor("menu_parts", 5, after: menu);    // the second menu_parts table; the phase-title cells follow it

            _phaseTitle = [AnchoredCells("PhaseTitleCells[0]", menu2, 0x350, 38, false), AnchoredCells("PhaseTitleCells[1]", menu2, 0x480, 38, false), AnchoredCells("PhaseTitleCells[2]", menu2, 0x5B0, 38, false)];
            long save = UiTableFor("save_parts", 5);
            _saveTitle = [AnchoredCells("SaveTitleCells[0]", save, 0x2C0, 38, false), AnchoredCells("SaveTitleCells[1]", save, 0x3F0, 38, false)];
        }

        private bool MusicOk(long va)
        {
            if (!Readable(va, 0x38))
            {
                return false;
            }

            int[] v = [.. Enumerable.Range(0, 14).Select(k => I32(va + (4L * k)))];
            return v[0] is >= 0 and < 300 && v[1] is >= 1 and <= 2000 && v[2..6].All(x => x == -1 || x is >= 1 and <= 2000) && v[6] is >= 0 and <= 3_600_000
                && v[7] >= 0 && v[7] <= v[8] && v[8] <= 48000 * 1200 && v[9..].All(x => x == -1 || x is >= 0 and < 300);
        }

        /// <summary>The music room records: 0x38 bytes {bgm, title id, 4 credit ids, length, loop start, loop end, 5 variants} at a code reference, at least 50 in a row.</summary>
        private void FindMusic()
        {
            foreach (long t in LeaTargetsIn(_text))
            {
                if (!Enumerable.Range(0, 50).All(k => MusicOk(t + (0x38L * k))))
                {
                    continue;
                }

                int n = 0;
                while (MusicOk(t + (0x38L * n)))
                {
                    n++;
                }

                _music = t;
                _musicCount = n;
                Ok("Music", t, n, LocateMethod.Content, $"{n} tracks");
                return;
            }

            throw Fail("no music room table (50 records of {bgm, title, credits, length, loop, variants})");
        }

        private bool ExtraOk(long va) => Readable(va, 20) && I32(va) is >= 900 and <= 1200 && I32(va + 4) is >= 400 and <= 700
            && (I32(va + 8) == -1 || I32(va + 8) is >= 0 and < 2000) && (I32(va + 12) == -1 || I32(va + 12) is >= 0 and < 2000) && I32(va + 16) is >= 0 and < 100;

        /// <summary>The ending-list records: 20 bytes {script scene 1000.., flow scene 533.., two unlock scenes, help message index} at a code reference, at least 20 in a row.</summary>
        private void FindExtras()
        {
            foreach (long t in LeaTargetsIn(_text))
            {
                if (!Enumerable.Range(0, 20).All(k => ExtraOk(t + (20L * k))))
                {
                    continue;
                }

                int n = 0;
                while (ExtraOk(t + (20L * n)))
                {
                    n++;
                }

                _extras = t;
                _extraCount = n;
                Ok("Extras", t, n, LocateMethod.Content, $"{n} scenarios");
                return;
            }

            throw Fail("no ending-list table (20 records of {scene, flow scene, unlocks, message})");
        }

        /// <summary>
        /// The dialog / help message ids: 55 i32 (ui_text ids or -1) at a code reference, followed by zero bytes, with at least three -1 entries and 30 distinct values, and
        /// holding a message for every ending-list prompt.
        /// </summary>
        private void FindDialogs()
        {
            const int Count = Tables.DialogMessageTable.Count;
            int[] messages = [.. Enumerable.Range(0, _extraCount).Select(k => I32(_extras + (20L * k) + 16))];
            var hits = new List<long>();
            foreach (long t in LeaTargetsIn(_text))
            {
                if (!Readable(t, (4 * Count) + 8))
                {
                    continue;
                }

                int[] v = [.. Enumerable.Range(0, Count).Select(k => I32(t + (4L * k)))];
                if (!v.All(x => x == -1 || x is >= 1 and <= 2000) || v.Count(x => x == -1) < 3 || v.Distinct().Count() < 30)
                {
                    continue;
                }

                if (I64(t + (4L * Count)) != 0 || !messages.All(m => m < Count && v[m] > 0))
                {
                    continue;
                }

                hits.Add(t);
            }

            if (hits.Count != 1)
            {
                throw Fail($"{hits.Count} candidate dialog message tables");
            }

            _dialogs = hits[0];
            Ok("DialogMessages", _dialogs, Count, LocateMethod.Content, $"{Count} ids, prompts of {messages.Length} scenarios resolve");
        }

        /// <summary>The fill colours: six RGB565 words (word 1 pure red) followed by eight copies of word 0.</summary>
        private void FindTextColours()
        {
            ReadOnlySpan<byte> text = _text.Bytes.Span;
            for (int i = 0; i + 28 <= text.Length; i += 2)
            {
                ushort w0 = BinaryPrimitives.ReadUInt16LittleEndian(text[i..]);
                if (w0 == 0 || BinaryPrimitives.ReadUInt16LittleEndian(text[(i + 2)..]) != 0xF800)
                {
                    continue;
                }

                bool ok = true;
                for (int k = 6; k < 14 && ok; k++)
                {
                    ok = BinaryPrimitives.ReadUInt16LittleEndian(text[(i + (2 * k))..]) == w0;
                }

                var distinct = new HashSet<ushort>();
                for (int k = 0; k < 6; k++)
                {
                    distinct.Add(BinaryPrimitives.ReadUInt16LittleEndian(text[(i + (2 * k))..]));
                }

                if (ok && distinct.Count >= 5)
                {
                    _colours = _text.Va + i;
                    Ok("TextColours", _colours, 6, LocateMethod.Content, "RGB565, red second, default repeated after");
                    return;
                }
            }

            throw Fail("no fill colour table (six RGB565 words then the default repeated)");
        }

        private bool MzpName(ulong p) => IsStringPointer(p) && AsciiString((long)p, 64) is { } s && Catalog.ImageCatalog.IsFileName(s) && s.EndsWith(".MZP", StringComparison.Ordinal);

        /// <summary>
        /// The gallery: group records {relro* entries, i32 category 0..19, 20 zero bytes} in RELRO (at least 400 in a row), each entry list NULL-terminated 32-byte records
        /// naming a picture; the special names are the pointers right before the first group's list (back to a NULL); the aliases are 0x28-byte records of picture-name
        /// pointers (at least 50 in a row).
        /// </summary>
        private void FindGallery()
        {
            bool GroupOk(long va) => Readable(va, 32) && _relro.Contains((long)U64(va)) && I32(va + 8) is >= 0 and <= 19 && _r.Read(va + 12, 20).IndexOfAnyExcept((byte)0) < 0;
            long groups = 0;
            for (long va = _relro.Va; va + (32L * 400) <= _relro.End; va += 8)
            {
                if (Enumerable.Range(0, 400).All(k => GroupOk(va + (32L * k))))
                {
                    groups = va;
                    break;
                }
            }

            if (groups == 0)
            {
                throw Fail("no CG group table (400 records of {entry list, category, zeros} in RELRO)");
            }

            int n = 0;
            int entries = 0;
            while (GroupOk(groups + (32L * n)))
            {
                long list = (long)U64(groups + (32L * n));
                while (U64(list) != 0)
                {
                    if (!IsStringPointer(U64(list)))
                    {
                        throw Fail($"CG group {n}: entry name pointer 0x{U64(list):x} is not a string");
                    }

                    entries++;
                    list += 32;
                }

                n++;
            }

            _groups = groups;
            _groupCount = n;
            Ok("GalleryGroups", groups, n, LocateMethod.Content, $"{n} groups, {entries} entries");

            long firstList = (long)U64(groups);
            long nul = firstList - 8;
            if (U64(nul) != 0)
            {
                throw Fail("the first CG entry list is not preceded by a NULL-terminated name list");
            }

            int special = 0;
            while (IsStringPointer(U64(nul - (8L * (special + 1)))))
            {
                special++;
            }

            if (special == 0)
            {
                throw Fail("no special CG names before the first entry list");
            }

            _special = nul - (8L * special);
            _specialCount = special;
            Ok("GallerySpecial", _special, special, LocateMethod.Anchored, $"{special} names before the first entry list");

            bool AliasOk(long va)
            {
                if (!Readable(va, 40) || !MzpName(U64(va)))
                {
                    return false;
                }

                bool any = false;
                for (int k = 1; k < 5; k++)
                {
                    ulong p = U64(va + (8L * k));
                    if (p == 0)
                    {
                        continue;
                    }

                    if (!MzpName(p))
                    {
                        return false;
                    }

                    any = true;
                }

                return any;
            }

            for (long va = _relro.Va; va + (40L * 50) <= _relro.End; va += 8)
            {
                if (!Enumerable.Range(0, 50).All(k => AliasOk(va + (40L * k))))
                {
                    continue;
                }

                int m = 0;
                while (AliasOk(va + (40L * m)))
                {
                    m++;
                }

                _aliases = va;
                _aliasCount = m;
                Ok("GalleryAliases", va, m, LocateMethod.Content, $"{m} alias records");
                return;
            }

            throw Fail("no CG alias table (50 records of picture-name pointers in RELRO)");
        }

        /// <summary>The flowchart columns: 32-byte RELRO records {i32 index = k, i32 group, i32 count 1..64, 0, text* nodes, 0}, at least 200 in a row.</summary>
        private void FindColumns()
        {
            bool ColumnOk(long va, int k) => Readable(va, 32) && I32(va) == k && I32(va + 4) is >= 0 and <= 31 && I32(va + 8) is >= 1 and <= 64 && I32(va + 12) == 0
                && InText((long)U64(va + 16)) && U64(va + 24) == 0;
            for (long va = _relro.Va; va + (32L * 200) <= _relro.End; va += 8)
            {
                if (!Enumerable.Range(0, 200).All(k => ColumnOk(va + (32L * k), k)))
                {
                    continue;
                }

                int n = 0;
                int nodes = 0;
                while (ColumnOk(va + (32L * n), n))
                {
                    nodes += I32(va + (32L * n) + 8);
                    n++;
                }

                _columns = va;
                _columnCount = n;
                Ok("FlowColumns", va, n, LocateMethod.Content, $"{n} columns, {nodes} nodes");
                return;
            }

            throw Fail("no flowchart column table (200 indexed records pointing at node records)");
        }

        // ------------------------------------------------------------------------------------------------------- code

        private long Rel32Target(long insn, int opcodeLength) => insn + opcodeLength + 4 + I32(insn + opcodeLength);

        /// <summary>
        /// The CG category switch: <c>push rbp; mov rbp,rsp; push rbx; push rax; dec edi; mov bl,1; cmp edi,N; ja; lea rax,[rip+table]; movsxd rcx,[rax+rdi*4]; add rcx,rax; jmp rcx</c>.
        /// The jump table, the category count (N + 1) and the join (the <c>jmp rel32</c> of the first case) come out of the instructions.
        /// </summary>
        private void FindCategorySwitch()
        {
            List<long> hits = FindPattern(_text, Pattern(0x55, 0x48, 0x89, 0xE5, 0x53, 0x50, 0xFF, 0xCF, 0xB3, 0x01, 0x83, 0xFF, ".", 0x0F, 0x87, "....", 0x48, 0x8D, 0x05, "....", 0x48, 0x63, 0x0C, 0xB8, 0x48, 0x01, 0xC1, 0xFF, 0xE1));
            if (hits.Count != 1)
            {
                throw Fail($"the CG category switch pattern occurs {hits.Count} times");
            }

            long f = hits[0];
            int categories = U8(f + 12) + 1;
            long table = Rel32Target(f + 19, 3);
            long firstCase = table + I32(table);
            long at = firstCase;
            while (U8(at) != 0xE9)
            {
                at++;
                if (at > firstCase + 64)
                {
                    throw Fail("the first category case has no jmp rel32");
                }
            }

            long join = Rel32Target(at, 1);
            for (int c = 0; c < categories; c++)
            {
                long target = table + I32(table + (4L * c));
                if (target <= f || target >= join)
                {
                    throw Fail($"category {c + 1}: case at 0x{target:x} is outside the switch");
                }
            }

            _catJump = table;
            _catJoin = join;
            _catCount = categories;
            Ok("CategoryJumpTable", table, categories, LocateMethod.Code, $"switch at 0x{f:x}, join 0x{join:x}, {categories} categories");
        }

        /// <summary>The STMA loop switch: <c>xor eax,eax; cmp edi,imm; jle; lea ecx,[rdi-N]; cmp ecx,K; ja; lea rdx,[rip+table]; movsxd rcx,[rdx+rcx*4]; add rcx,rdx; jmp rcx</c>; validated by running it.</summary>
        private void FindMotionLoopFunction()
        {
            List<long> hits = FindPattern(_text, Pattern(0x31, 0xC0, 0x81, 0xFF, "....", 0x7E, ".", 0x8D, 0x8F, "....", 0x81, 0xF9, "....", 0x0F, 0x87, "....", 0x48, 0x8D, 0x15, "....", 0x48, 0x63, 0x0C, 0x8A, 0x48, 0x01, 0xD1, 0xFF, 0xE1));
            if (hits.Count != 1)
            {
                throw Fail($"the loop-point switch pattern occurs {hits.Count} times");
            }

            long f = hits[0];
            var cpu = new Tables.TinyX86(_r.Read);
            int loops = 0;
            for (int id = 1; id <= _motionCount; id++)
            {
                uint v = cpu.Run(f, (ulong)id).Eax;
                if (v > 36_000_000)
                {
                    throw Fail($"loop point of motion {id} is {v}");
                }

                if (v != 0)
                {
                    loops++;
                }
            }

            if (loops < 100)
            {
                throw Fail($"the loop-point switch at 0x{f:x} yields only {loops} looping motions");
            }

            _loopFn = f;
            Ok("MotionLoopFunction", f, loops, LocateMethod.Code, $"{loops} looping motions of {_motionCount}");
        }

        /// <summary>The flowchart-jump clears: <c>cmp edi,imm; jg; lea rax,[rip+F words]; cmp edi,imm; mov word [rax+off],0; jg ...</c>; the F array is the lea target; validated by running it.</summary>
        private void FindJumpClearFunction()
        {
            List<long> hits = FindPattern(_text, Pattern(0x81, 0xFF, "....", 0x0F, 0x8F, "....", 0x48, 0x8D, 0x05, "....", 0x81, 0xFF, "....", 0x66, 0xC7, 0x80, "....", 0x00, 0x00, 0x0F, 0x8F));
            if (hits.Count != 1)
            {
                throw Fail($"the jump-clear pattern occurs {hits.Count} times");
            }

            long f = hits[0];
            long fwords = Rel32Target(f + 12, 3);
            if (_r.Image.SegmentOf((ulong)fwords) is not { } seg || (seg.Flags & 2) == 0)
            {
                throw Fail($"the F word array at 0x{fwords:x} is not in the writable segment");
            }

            var cpu = new Tables.TinyX86(_r.Read);
            int words = 0;
            for (int row = 0; row <= 300; row++)
            {
                foreach (Tables.X86Store s in cpu.Run(f, (ulong)row).Stores)
                {
                    long off = s.Address - fwords;
                    if (s.Value != 0 || off < 0 || off > 2400 || (off & 1) != 0)
                    {
                        throw Fail($"the clear function at 0x{f:x} stores 0x{s.Value:x} at 0x{s.Address:x}");
                    }

                    words++;
                }
            }

            _clearFn = f;
            _fwords = fwords;
            Ok("JumpClearFunction", f, words, LocateMethod.Code, $"{words} zero stores over 301 columns");
            Ok("FWordsBase", fwords, 0, LocateMethod.Code, "the lea of the clear function");
        }
    }
}
