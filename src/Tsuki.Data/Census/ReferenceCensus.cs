using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Tsuki.Data.Catalog;
using Tsuki.Data.Containers;
using Tsuki.Data.Scripts;
using Tsuki.Data.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Vm;

namespace Tsuki.Data.Census;

/// <summary>
/// Census T0 (ROADMAP M1 acceptance 5 and 7; PACE row 10): every reference the 549 scripts make to the rest of the game, resolved through
/// <see cref="GameFolderSource"/>: the pictures (<c>_STCH</c> ...), voices, sound effects, music, movies, scene jumps and labels, text lines,
/// layer motions and wipe masks, and then the targets themselves (the picture header, the audio payload, the movie file, the text line, the
/// glyphs of every character). Everything must resolve except <see cref="CensusAllowList"/>: the five picture names that are script bugs and
/// the three sprite bodies that are on no disk (archives_ui_movies.md 2.4 and 3.3; FORMATS 5.2).
/// </summary>
/// <remarks>
/// <para>
/// The first part walks the statements the way <c>tsuki.script</c> does and prints, for each reference, the line the Python oracle prints
/// (<c>tests/Tsuki.Tests.Data/Census/census_oracle.py refs</c>), so a test compares the two reference by reference. It follows
/// <c>tsuki.script.command_assets</c>, <c>picture_key</c> and <c>LayerTracker</c>; the layer names are tracked in statement order without
/// control flow, which names every picture a script can show but is not the engine's run-time state.
/// </para>
/// <para>
/// The second part reads what the references point at. It touches each distinct target once and never decodes more than the options say:
/// image headers and tile tables, not pixels (a sample is decoded to pixels), audio payloads unwrapped and checked for their RIFF header (a
/// sample is decoded to PCM).
/// </para>
/// </remarks>
public static class ReferenceCensus
{
    private const int TextLineCount = TextRules.RetailLineCount;

    /// <summary>The commands that carry a picture id, and the textual argument that holds it (<c>IMAGE_ARG</c>).</summary>
    private static readonly Dictionary<string, int> ImageArgument = new(StringComparer.Ordinal)
    {
        ["SPFC"] = 1, ["SPRD"] = 1, ["STCG"] = 0,
    };

    private static readonly Dictionary<string, int> LabelJumps = new(StringComparer.Ordinal)
    {
        ["GOTO"] = 0, ["IF__"] = 3, ["IFNX"] = 1, ["IFP4"] = 1, ["IFSC"] = 1, ["IFUR"] = 2,
    };

    private static readonly HashSet<string> TextCommands = new(StringComparer.Ordinal) { "ZM", "ZD", "MSAD", "SELR" };

    /// <summary>Runs the census over every script of the source.</summary>
    public static CensusReport Run(GameFolderSource source, CensusOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new CensusOptions();
        var watch = Stopwatch.StartNew();
        int scripts = source.ScriptCount;
        var context = new Context(source);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism) };

        var perScript = new ScriptScan[scripts];
        Parallel.For(0, scripts, parallel, k => perScript[k] = ScanScript(context, k));

        var references = new List<CensusReference>(perScript.Sum(p => p.References.Count));
        var findings = new List<CensusFinding>();
        int statements = 0;
        foreach (ScriptScan scan in perScript)
        {
            references.AddRange(scan.References);
            findings.AddRange(scan.Findings);
            statements += scan.Statements;
        }

        var checks = new List<CensusCheck>();
        if (options.CheckTargets)
        {
            CheckTargets(context, references, parallel, options, findings, checks);
        }

        IReadOnlyList<GlyphCensusLanguage> glyphs = [];
        if (options.CheckGlyphs)
        {
            glyphs = GlyphCensus.Run(source, TextIds(references), parallel, findings);
        }

        var totals = new List<CensusKindTotals>();
        foreach (ReferenceKind kind in Enum.GetValues<ReferenceKind>())
        {
            string name = kind.ToString().ToLowerInvariant();
            var of = references.Where(r => r.Kind == kind).ToList();
            int allowed = findings.Count(f => f.Check == name && f.Allowed);
            int unexpected = findings.Count(f => f.Check == name && !f.Allowed);
            totals.Add(new CensusKindTotals(kind, of.Count, of.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count(), of.Count(r => r.Resolved), allowed, unexpected));
        }

        return new CensusReport(scripts, statements, references, totals, findings, checks, glyphs, watch.Elapsed);
    }

    // -- the walk ---------------------------------------------------------------------------------------------------

    private sealed class Context(GameFolderSource source)
    {
        public GameFolderSource Source { get; } = source;

        public ScriptImageResolver Resolver { get; } = source.Catalog.Resolver;

        public IReadOnlyList<string> VoiceNames { get; } =
            source.Archive("voice").Index.Nam?.Names ?? throw new InvalidDataException("voice.nam is missing");

        public int BgmCount { get; } = source.AudioCount(AudioBank.Bgm);

        public SeNamesAccessor Se { get; } = new(source);

        public int MovieCount { get; } = source.Tables.Movies.Count;

        public SceneTable Scenes { get; } = source.Tables.SceneIds;

        public IReadOnlyList<string> Allpac3Names { get; } = source.Catalog.Nams[ImageArchive.Allpac3].Names;

        public int MaskBase { get; } =
            source.Catalog.NameIndex.Find("mask_top.mzp") is { Archive: ImageArchive.Allpac3 } id
                ? id.Index
                : throw new InvalidDataException("mask_top.mzp is not in allpac3");
    }

    private sealed class SeNamesAccessor(GameFolderSource source)
    {
        public int VseIndex(string name) => source.SoundEffectNames.VseIndex(name);

        public int VseCount => source.SoundEffectNames.VseCount;
    }

    private sealed class ScriptScan
    {
        public List<CensusReference> References { get; } = [];

        public List<CensusFinding> Findings { get; } = [];

        public int Statements { get; set; }
    }

    private static ScriptScan ScanScript(Context cx, int k)
    {
        var scan = new ScriptScan();
        ScriptImage image = cx.Source.Script(k);
        ReadOnlyMemory<byte> bytes = image.Bytes;
        var tracker = new LayerNameTracker();
        LabelTable ownLabels = cx.Source.Scripts.LabelLookup(k);
        foreach (int start in image.SegmentStarts)
        {
            ScriptSegment segment = ScriptSegmenter.Read(bytes.Span, start);
            if (!ScriptSegmenter.TryGetStatement(k, bytes, segment, out Statement statement))
            {
                continue;
            }

            scan.Statements++;
            var s = new StatementView(statement);
            string n = s.Name;
            string? pictureKey = null;
            if (n is "STCH" or "SCH2")
            {
                byte[]? name = LayerNameTracker.PictureName(s, tracker.SourceName(s), out int aa, out int bb);
                if (name is not null)
                {
                    ResolvedImage r = cx.Resolver.Resolve(name, aa, bb);
                    pictureKey = r.Key;
                    if (!IsNumber(r.Key))
                    {
                        AddImage(scan, k, s, r);
                    }
                }
            }
            else if (ImageArgument.TryGetValue(n, out int arg))
            {
                if (arg < s.TextCount && s.TextValue(arg) is { IsEmpty: false } value && !IsNumber(Encoding.Latin1.GetString(value)))
                {
                    AddImage(scan, k, s, cx.Resolver.Resolve(value.ToArray()));
                }
            }
            else if (n is "VPLY" or "VPL2")
            {
                AddVoice(cx, scan, k, s);
            }
            else if ((n is "SEPL" or "SEFI") && s.TextCount > 2)
            {
                string name = s.TextString(2);
                int index = cx.Se.VseIndex(name);
                Add(scan, ReferenceKind.Se, k, s, SeKey(name), index < 0 ? "-" : Num(index), index >= 0);
            }
            else if (n == "MPLY" && s.TextCount > 0)
            {
                int track = s.Inline16(0);
                bool ok = track >= 0 && track < cx.BgmCount;
                Add(scan, ReferenceKind.Bgm, k, s, Num(track), ok ? Num(track) : "-", ok);
            }
            else if (n == "TOMP" && s.TextCount > 0)
            {
                int id = s.Value(0).Or(0);
                bool ok = id >= 0 && id < cx.MovieCount;
                Add(scan, ReferenceKind.Movie, k, s, Num(id), ok ? cx.Source.Tables.Movies[id].FileName : "-", ok);
            }
            else if (n.StartsWith('J') && s.TextCount > 0)
            {
                AddJump(cx, scan, k, s);
            }
            else if (LabelJumps.TryGetValue(n, out int labelArg) && s.TextCount > labelArg)
            {
                AddLabel(scan, k, s, s.TextString(labelArg), ownLabels.Find(s.TextValue(labelArg)) >= 0);
            }
            else if (n == "GOTS")
            {
                for (int i = 1; i < s.TextCount; i++)
                {
                    AddLabel(scan, k, s, s.TextString(i), ownLabels.Find(s.TextValue(i)) >= 0);
                }
            }

            if (n is "STMA" or "SGMA")
            {
                int? m = s.Expr(1);
                string state = m is null ? "none" : m == 0 ? "zero" : m >= 1 && m <= MotionTables.MotionCount ? "ok" : "out";
                Add(scan, ReferenceKind.Motion, k, s, m is null ? "-" : Num(m.Value), state, state == "ok");
            }

            if (n == "FADS" && s.Expr(0) == 2 && s.Expr(2) is { } rule)
            {
                int at = cx.MaskBase + rule;
                string file = at >= 0 && at < cx.Allpac3Names.Count ? cx.Allpac3Names[at] : "-";
                // rule n >= 1 is allpac3 RULE_nn; rule 0 is the entry MASK_TOP itself, a one-tile placeholder (archives_ui_movies.md 5.4)
                bool ok = rule == 0
                    ? file == "MASK_TOP.MZP"
                    : rule >= 1 && file == string.Create(CultureInfo.InvariantCulture, $"RULE_{rule:D2}.MZP");
                Add(scan, ReferenceKind.Rule, k, s, Num(rule), file, ok);
            }

            if (TextCommands.Contains(n))
            {
                for (int i = 0; i < s.TextCount; i++)
                {
                    foreach (int id in TextReferences.ScanRefs(Encoding.Latin1.GetString(s.TextRaw(i))))
                    {
                        bool ok = id >= 0 && id < TextLineCount;
                        Add(scan, ReferenceKind.Text, k, s, Num(id), ok ? "ok" : "range", ok);
                    }
                }
            }

            tracker.Update(s, pictureKey);
        }

        return scan;
    }

    private static void AddImage(ScriptScan scan, int k, StatementView s, ResolvedImage r)
    {
        var layers = new List<string>(2);
        int missing = 0;
        if (r.Found)
        {
            if (r.FileId is { } f)
            {
                layers.Add(f.ToString());
            }
            else
            {
                missing++;
            }

            if (r.Overlay is not null)
            {
                if (r.OverlayId is { } o)
                {
                    layers.Add(o.ToString());
                }
                else
                {
                    missing++;
                }
            }
        }

        string result = (layers.Count == 0 ? "-" : string.Join('+', layers)) + ";m=" + Num(missing);
        bool resolved = r.Found && missing == 0;
        string? allowed = resolved ? null : CensusAllowList.ImageReason(r);
        Add(scan, ReferenceKind.Image, k, s, r.Key, result, resolved, allowed);
    }

    private static void AddVoice(Context cx, ScriptScan scan, int k, StatementView s)
    {
        string name = s.TextCount > 0 ? s.TextString(0) : string.Empty;
        int index = -1;
        bool ok = false;
        if (s.TextCount > 1 && int.TryParse(s.TextString(1).Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int idx))
        {
            index = idx;
            ok = idx < cx.VoiceNames.Count && name.StartsWith(cx.VoiceNames[idx], StringComparison.Ordinal);
        }

        Add(scan, ReferenceKind.Voice, k, s, name, (index < 0 ? "-" : Num(index)) + "|" + (ok ? "1" : "0"), ok);
    }

    private static void AddJump(Context cx, ScriptScan scan, int k, StatementView s)
    {
        int sceneId = s.Inline16(0);
        int? target = cx.Scenes.IndexOf(sceneId);
        Add(scan, ReferenceKind.Scene, k, s, Num(sceneId), target is null ? "-" : Num(target.Value), target is not null);
        if (s.TextCount > 1 && !s.TextValue(1).IsEmpty)
        {
            string label = s.TextString(1);
            string key = Num(sceneId) + ":" + label;
            if (target is null)
            {
                Add(scan, ReferenceKind.Label, k, s, key, "noscript", false);
            }
            else
            {
                bool found = cx.Source.Scripts.LabelLookup(target.Value).Find(s.TextValue(1)) >= 0;
                Add(scan, ReferenceKind.Label, k, s, key, found ? "found" : "missing", found);
            }
        }
    }

    private static void AddLabel(ScriptScan scan, int k, StatementView s, string label, bool found) =>
        Add(scan, ReferenceKind.Label, k, s, label, found ? "found" : "missing", found);

    private static void Add(ScriptScan scan, ReferenceKind kind, int k, StatementView s, string key, string result, bool resolved, string? allowed = null)
    {
        string command = s.Name;
        var reference = new CensusReference(kind, k, s.Offset, command, key, result, resolved);
        scan.References.Add(reference);
        if (!resolved)
        {
            allowed ??= CensusAllowList.Reason(reference);
            scan.Findings.Add(new CensusFinding(kind.ToString().ToLowerInvariant(), reference.Line, allowed));
        }
    }

    private static string SeKey(string name) => name.ToUpperInvariant();

    private static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>value.lstrip("-").isdigit()</c>: such an "id" is a number, not a picture.</summary>
    private static bool IsNumber(string value)
    {
        string t = value.TrimStart('-');
        return t.Length > 0 && t.All(char.IsAsciiDigit);
    }

    // -- the targets ------------------------------------------------------------------------------------------------

    private static void CheckTargets(
        Context cx, List<CensusReference> references, ParallelOptions parallel, CensusOptions options,
        List<CensusFinding> findings, List<CensusCheck> checks)
    {
        GameFolderSource source = cx.Source;
        var failures = new ConcurrentBag<CensusFinding>();

        // pictures: the header and tile table of every file the name resolves to (and its allpac2 copy)
        var imageKeys = references.Where(r => r.Kind == ReferenceKind.Image).Select(r => r.Key).Distinct(StringComparer.Ordinal).ToList();
        var files = new HashSet<ImageId>();
        foreach (string key in imageKeys)
        {
            ResolvedImage r = cx.Resolver.Resolve(Encoding.Latin1.GetBytes(key));
            if (!r.Found)
            {
                continue;
            }

            foreach (ImageId? id in new[] { r.FileId, r.OverlayId, r.ReducedId })
            {
                if (id is { } i)
                {
                    files.Add(i);
                }
            }
        }

        var fileList = files.OrderBy(f => f.Packed).ToList();
        Parallel.ForEach(fileList, parallel, id =>
        {
            try
            {
                var image = source.OpenImage((int)id.Archive, id.Index);
                if (image.Header.Tiles < 1)
                {
                    failures.Add(new CensusFinding("image-file", $"{id}: no tiles", null));
                }
            }
            catch (Exception e) when (e is ContainerFormatException or ArgumentOutOfRangeException or FileNotFoundException)
            {
                failures.Add(new CensusFinding("image-file", $"{id}: {e.Message}", null));
            }
        });
        checks.Add(new CensusCheck("picture files opened (header and tile table)", fileList.Count, failures.Count(f => f.Check == "image-file")));

        // audio: every referenced entry is read from its archive and unwrapped, and starts like a RIFF/WAVE file
        var voices = DistinctEntries(references, ReferenceKind.Voice, r => r.Result.EndsWith("|1", StringComparison.Ordinal) ? r.Result[..r.Result.IndexOf('|', StringComparison.Ordinal)] : null);
        var sounds = DistinctEntries(references, ReferenceKind.Se, r => r.Result == "-" ? null : r.Result);
        var tracks = DistinctEntries(references, ReferenceKind.Bgm, r => r.Result == "-" ? null : r.Result);
        CheckAudio(source, AudioBank.Voice, voices, "voice", parallel, failures, checks);
        CheckAudio(source, AudioBank.Se, sounds, "vse", parallel, failures, checks);
        CheckAudio(source, AudioBank.Bgm, tracks, "bgm", parallel, failures, checks);

        // movies: the table record of every language variant exists as a file
        var movieIds = references.Where(r => r.Kind == ReferenceKind.Movie && r.Resolved).Select(r => int.Parse(r.Key, CultureInfo.InvariantCulture)).Distinct().Order().ToList();
        int movieFiles = 0;
        foreach (int id in movieIds)
        {
            for (int lang = 0; lang < 4; lang++)
            {
                int index = MovieRules.IndexForLanguage(id, lang);
                movieFiles++;
                try
                {
                    string path = source.Movie(index);
                    if (new FileInfo(path).Length == 0)
                    {
                        failures.Add(new CensusFinding("movie-file", $"movie {id} ({MovieRules.Languages[lang]}): {path} is empty", null));
                    }
                }
                catch (Exception e) when (e is FileNotFoundException or ArgumentOutOfRangeException)
                {
                    failures.Add(new CensusFinding("movie-file", $"movie {id} ({MovieRules.Languages[lang]}) -> record {index}: {e.Message}", null));
                }
            }
        }

        checks.Add(new CensusCheck("movie ids x 4 languages -> files", movieFiles, failures.Count(f => f.Check == "movie-file")));

        // text: every referenced line is readable in the four selectable languages
        var textIds = TextIds(references);
        int textReads = 0;
        foreach (Language lang in TextRules.SelectableLanguages)
        {
            foreach (int id in textIds)
            {
                textReads++;
                try
                {
                    _ = source.TextLine(lang, id);
                }
                catch (ArgumentOutOfRangeException e)
                {
                    failures.Add(new CensusFinding("text-line", $"{lang} line {id}: {e.Message}", null));
                }
            }
        }

        checks.Add(new CensusCheck("text lines x 4 languages read", textReads, failures.Count(f => f.Check == "text-line")));

        // motions and masks: the table entries are readable
        MotionTables motions = source.Tables.Motions;
        var motionIds = references.Where(r => r.Kind == ReferenceKind.Motion && r.Resolved).Select(r => int.Parse(r.Key, CultureInfo.InvariantCulture)).Distinct().ToList();
        foreach (int id in motionIds)
        {
            (int first, int end) = motions.KeyRange(id);
            if (end < first || motions.Duration(id) < 0)
            {
                failures.Add(new CensusFinding("motion-entry", $"motion {id}: keyframes {first}..{end}, duration {motions.Duration(id)}", null));
            }
        }

        checks.Add(new CensusCheck("motion ids read from the eboot tables", motionIds.Count, failures.Count(f => f.Check == "motion-entry")));

        var rules = references.Where(r => r.Kind == ReferenceKind.Rule && r.Resolved).Select(r => int.Parse(r.Key, CultureInfo.InvariantCulture)).Distinct().ToList();
        foreach (int rule in rules)
        {
            try
            {
                _ = source.OpenImage((int)ImageArchive.Allpac3, cx.MaskBase + rule);
            }
            catch (Exception e) when (e is ContainerFormatException or ArgumentOutOfRangeException)
            {
                failures.Add(new CensusFinding("rule-file", $"rule {rule}: {e.Message}", null));
            }
        }

        checks.Add(new CensusCheck("wipe masks opened (allpac3 RULE_nn)", rules.Count, failures.Count(f => f.Check == "rule-file")));

        // a sample decoded for real: pixels and PCM (the full decodes are the nightly runs of the image and audio packages)
        if (options.DecodePictureSample > 0)
        {
            var sample = Spread(fileList.Where(f => f.Archive != ImageArchive.Allpac2).ToList(), options.DecodePictureSample);
            Parallel.ForEach(sample, parallel, id =>
            {
                try
                {
                    Picture p = source.Picture(id);
                    if (p.Width <= 0 || p.Height <= 0 || p.Tiles.Length == 0)
                    {
                        failures.Add(new CensusFinding("picture-decode", $"{id}: empty picture", null));
                    }
                }
                catch (Exception e) when (e is ContainerFormatException or InvalidDataException)
                {
                    failures.Add(new CensusFinding("picture-decode", $"{id}: {e.Message}", null));
                }
            });
            checks.Add(new CensusCheck("pictures decoded to pixels (sample)", sample.Count, failures.Count(f => f.Check == "picture-decode")));
        }

        if (options.DecodeAudioSample > 0)
        {
            var entries = new List<(AudioBank, int)>();
            entries.AddRange(voices.Select(i => (AudioBank.Voice, i)));
            entries.AddRange(sounds.Select(i => (AudioBank.Se, i)));
            entries.AddRange(tracks.Select(i => (AudioBank.Bgm, i)));
            var sample = Spread(entries, options.DecodeAudioSample);
            Parallel.ForEach(sample, parallel, e =>
            {
                try
                {
                    AudioClip clip = source.Audio(e.Item1, e.Item2);
                    if (clip.FrameCount <= 0 || clip.SampleRate != 48_000)
                    {
                        failures.Add(new CensusFinding("audio-decode", $"{e.Item1} #{e.Item2}: {clip.FrameCount} frames at {clip.SampleRate} Hz", null));
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ContainerFormatException)
                {
                    failures.Add(new CensusFinding("audio-decode", $"{e.Item1} #{e.Item2}: {ex.Message}", null));
                }
            });
            checks.Add(new CensusCheck("audio entries decoded to PCM (sample)", sample.Count, failures.Count(f => f.Check == "audio-decode")));
        }

        findings.AddRange(failures.OrderBy(f => f.Check, StringComparer.Ordinal).ThenBy(f => f.Detail, StringComparer.Ordinal));
    }

    private static List<int> DistinctEntries(List<CensusReference> references, ReferenceKind kind, Func<CensusReference, string?> entry) =>
    [
        .. references.Where(r => r.Kind == kind)
            .Select(entry)
            .Where(e => e is not null)
            .Select(e => int.Parse(e!, CultureInfo.InvariantCulture))
            .Distinct()
            .Order(),
    ];

    private static void CheckAudio(
        GameFolderSource source, AudioBank bank, List<int> entries, string label, ParallelOptions parallel,
        ConcurrentBag<CensusFinding> failures, List<CensusCheck> checks)
    {
        string check = "audio-" + label;
        Parallel.ForEach(entries, parallel, index =>
        {
            try
            {
                ReadOnlySpan<byte> head = source.AudioPayload(bank, index).Span;
                if (head.Length < 12 || !head[..4].SequenceEqual("RIFF"u8) || !head.Slice(8, 4).SequenceEqual("WAVE"u8))
                {
                    failures.Add(new CensusFinding(check, $"{label} #{index}: the payload is not RIFF/WAVE", null));
                }
            }
            catch (Exception e) when (e is ContainerFormatException or InvalidDataException or ArgumentOutOfRangeException)
            {
                failures.Add(new CensusFinding(check, $"{label} #{index}: {e.Message}", null));
            }
        });
        checks.Add(new CensusCheck($"{label} entries read and unwrapped", entries.Count, failures.Count(f => f.Check == check)));
    }

    private static List<T> Spread<T>(List<T> items, int count)
    {
        if (items.Count <= count)
        {
            return items;
        }

        var picked = new List<T>(count);
        for (int i = 0; i < count; i++)
        {
            picked.Add(items[(int)((long)i * items.Count / count)]);
        }

        return picked;
    }

    /// <summary>The distinct text line ids the scripts reference, ascending.</summary>
    public static List<int> TextIds(IEnumerable<CensusReference> references) =>
    [
        .. references.Where(r => r.Kind == ReferenceKind.Text && r.Resolved)
            .Select(r => int.Parse(r.Key, CultureInfo.InvariantCulture))
            .Distinct()
            .Order(),
    ];
}
