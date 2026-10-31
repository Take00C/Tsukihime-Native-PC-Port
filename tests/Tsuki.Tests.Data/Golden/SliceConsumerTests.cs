#if TSUKI_DEV
using System.Text;
using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Vm;

namespace Tsuki.Tests.Data.Golden;

/// <summary>
/// What the M2a streams read from the slice through <see cref="IGameData"/>, checked against the values of the golden manifest
/// (ARCHITECTURE 3.3: "every consumer test runs against both sources, and they must agree"). The class knows nothing about
/// <c>GoldenSource</c>; the golden source is the only concrete subclass today, <c>GameFolderSource</c> (package A3) adds a second.
/// The expected values come from the slice files read with <see cref="System.Text.Json"/> (<see cref="SliceExpect"/>).
/// </summary>
public abstract class SliceConsumerTests
{
    /// <summary>The source under test.</summary>
    protected abstract IGameData Source { get; }

    [GameDataFact]
    public void BuildIsTheEbootTheSliceWasExportedFrom()
    {
        JsonElement eboot = SliceExpect.Area.GetProperty("game").GetProperty("eboot");
        Assert.Equal(eboot.GetProperty("size").GetInt64(), Source.Build.EbootSize);
        Assert.Equal(eboot.GetProperty("sha256").GetString(), Source.Build.EbootSha256);
        Assert.True(Source.Build.IsAnalysedBuild, "the slice must come from the analysed build (RR 3.3)");

        using JsonDocument build = SliceExpect.Json("eboot/build.json");
        Assert.Equal(build.RootElement.GetProperty("expected").GetProperty("sha256").GetString(), Source.Build.EbootSha256);
        Assert.Equal(BuildInfo.AnalysedEbootSize, Source.Build.EbootSize);
    }

    [GameDataFact]
    public void ScriptCountIs549AsTheSceneTableAndTheNameTableSay()
    {
        using JsonDocument scenes = SliceExpect.Json("eboot/scenes.json");
        using JsonDocument names = SliceExpect.Json("scripts/allscr_names.json");
        Assert.Equal(549, Source.ScriptCount);
        Assert.Equal(scenes.RootElement.GetProperty("count").GetInt32(), Source.ScriptCount);
        Assert.Equal(names.RootElement.GetProperty("names").GetArrayLength(), Source.ScriptCount);
    }

    [GameDataFact]
    public void ScriptBytesAndSegmentOffsetsEqualTheManifest()
    {
        var mismatches = new Mismatches();
        foreach (int k in new[] { 2, 3 })
        {
            using JsonDocument doc = SliceExpect.Json($"scripts/script_{k:D4}.json");
            JsonElement decoded = doc.RootElement.GetProperty("decoded");
            JsonElement statements = doc.RootElement.GetProperty("statements");
            ScriptImage script = Source.Script(k);

            mismatches.Equal($"script {k} index", k, script.Index);
            mismatches.Equal($"script {k} byte count", decoded.GetProperty("size").GetInt32(), script.Bytes.Length);
            mismatches.Equal($"script {k} SHA-1", decoded.GetProperty("sha1").GetString()!, SliceExpect.Sha1(script.Bytes.Span));
            mismatches.Equal($"script {k} SHA-1 (file record)",
                SliceExpect.FileRecord("scripts/" + decoded.GetProperty("file").GetString()).GetProperty("sha1").GetString()!, SliceExpect.Sha1(script.Bytes.Span));
            mismatches.Equal($"script {k} segment count", doc.RootElement.GetProperty("statement_count").GetInt32(), script.SegmentStarts.Length);
            mismatches.Equal($"script {k} first segment", 0, script.SegmentStarts[0]);

            int i = 0;
            foreach (JsonElement s in statements.EnumerateArray())
            {
                if (i >= script.SegmentStarts.Length)
                {
                    mismatches.Add($"script {k}: statement {i} has no segment");
                    break;
                }

                mismatches.Equal($"script {k} segment {i} start", s.GetProperty("offset").GetInt32(), script.SegmentStarts[i]);
                mismatches.Equal($"script {k} segment {i} end", s.GetProperty("end").GetInt32(), script.SegmentEnd(i));
                i++;
            }

            mismatches.Equal($"script {k} segments walked", script.SegmentStarts.Length, i);
            mismatches.Equal($"script {k} last segment end", script.Bytes.Length, script.SegmentEnd(script.SegmentStarts.Length - 1));
        }

        mismatches.AssertNone("scripts 2 and 3 through IGameData.Script");
    }

    [GameDataFact]
    public void SegmentOffsetsEqualTheEngineSegmenterOnTheSameBytes()
    {
        // The VM stream's tokenizer (C) and the segment list of the oracle are independent implementations of script.md 3.1.
        foreach (int k in new[] { 2, 3 })
        {
            ScriptImage script = Source.Script(k);
            Assert.Equal(ScriptSegmenter.SegmentStarts(script.Bytes.Span), script.SegmentStarts);
        }
    }

    [GameDataFact]
    public void TextLinesEqualTheManifestInJapaneseAndEnglish()
    {
        using JsonDocument doc = SliceExpect.Json("text/lines.json");
        var mismatches = new Mismatches();
        int checkedLines = 0;
        int languageCount = 0;
        foreach (JsonProperty language in doc.RootElement.GetProperty("languages").EnumerateObject())
        {
            languageCount++;
            Language lang = language.Name switch { "ja" => Language.Ja, "en" => Language.En, _ => throw new InvalidOperationException(language.Name) };
            mismatches.Equal($"{language.Name} line count", language.Value.GetProperty("count").GetInt32(), Source.TextLineCount);
            foreach (JsonElement line in language.Value.GetProperty("lines").EnumerateArray())
            {
                int id = line.GetProperty("id").GetInt32();
                byte[] expected = Convert.FromHexString(line.GetProperty("engine_line_hex").GetString()!);
                byte[] actual = Source.TextLine(lang, id).ToArray();
                mismatches.Equal($"{language.Name} line {id} bytes", Convert.ToHexStringLower(expected), Convert.ToHexStringLower(actual));
                mismatches.Equal($"{language.Name} line {id} text", line.GetProperty("text").GetString()!, Encoding.UTF8.GetString(actual));
                checkedLines++;
            }
        }

        Assert.Equal(SliceExpect.Count("text.messages") * languageCount, checkedLines);   // the exporter's own count of the messages it wrote
        mismatches.AssertNone("text lines 0..7 through IGameData.TextLine");
        Assert.Equal(43_969, Source.TextLineCount);
    }

    [GameDataFact]
    public void TheEnglishLinesAreTheOnesTheGameShowsFirst()
    {
        // text.md 2.3: ja/en line 0 and 3 are quoted in the format document; the English ones carry two leading or trailing blanks.
        string en0 = Encoding.UTF8.GetString(Source.TextLine(Language.En, 0).Span);
        Assert.Equal("While the world is still asleep, I wake to the chill of the night breeze.  ", en0);
        string en1 = Encoding.UTF8.GetString(Source.TextLine(Language.En, 1).Span);
        Assert.Equal("  A dark night.", en1);
        // ja line 1 is U+3000 (ideographic space), "kurai yo" in hiragana and kanji, U+3002 (full stop); text.md 2.3
        Assert.Equal("e38080e3818fe38289e38184e5a49ce38082", Convert.ToHexStringLower(Source.TextLine(Language.Ja, 1).Span));
        Assert.DoesNotContain((byte)'\r', Source.TextLine(Language.Ja, 0).ToArray());
        Assert.DoesNotContain((byte)'\n', Source.TextLine(Language.Ja, 0).ToArray());
    }

    [GameDataTheory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void FontPagesEqualTheManifest(Language lang)
    {
        string code = lang == Language.Ja ? "ja" : "en";
        using JsonDocument doc = SliceExpect.Json($"fonts/ft_{code}.json");
        JsonElement pages = doc.RootElement.GetProperty("pages");
        var mismatches = new Mismatches();
        int count = 0;
        foreach (JsonElement p in pages.EnumerateArray())
        {
            int section = p.GetProperty("section").GetInt32();
            FontPage page = Source.FontPage(lang, section);
            int w = p.GetProperty("width").GetInt32();
            int h = p.GetProperty("height").GetInt32();
            mismatches.Equal($"{code} page {section} language", lang, page.Language);
            mismatches.Equal($"{code} page {section} number", section, page.Page);
            mismatches.Equal($"{code} page {section} width", w, page.Width);
            mismatches.Equal($"{code} page {section} height", h, page.Height);
            mismatches.Equal($"{code} page {section} RGBA bytes", w * h * 4, page.Rgba.Length);
            mismatches.Equal($"{code} page {section} RGBA SHA-1", p.GetProperty("rgba_sha1").GetString()!, SliceExpect.Sha1(page.Rgba.Span));

            byte[] coverage = new byte[w * h];
            ReadOnlySpan<byte> rgba = page.Rgba.Span;
            for (int i = 0; i < coverage.Length; i++)
            {
                coverage[i] = rgba[(4 * i) + 3];
            }

            mismatches.Equal($"{code} page {section} coverage SHA-1", p.GetProperty("alpha_sha1").GetString()!, SliceExpect.Sha1(coverage));
            count++;
        }

        Assert.Equal(SliceExpect.Count($"fonts.{code}.pages"), count);
        mismatches.AssertNone($"ft_{code} through IGameData.FontPage");
    }

    [GameDataFact]
    public void PictureTilesAndAssembledPicturesEqualTheManifest()
    {
        var files = SliceExpect.PictureFiles();
        Assert.Equal(SliceExpect.Count("pictures.pictures"), files.Count);
        var mismatches = new Mismatches();
        foreach ((int archive, int entry, string jsonPath) in files)
        {
            using JsonDocument doc = SliceExpect.Json(jsonPath);
            JsonElement root = doc.RootElement;
            JsonElement header = root.GetProperty("header");
            string label = $"{SliceExpect.ArchiveNames[archive]}#{entry} ({root.GetProperty("name").GetString()})";
            Picture picture = Source.Picture(archive, entry);

            int outW = header.GetProperty("out_width").GetInt32();
            int outH = header.GetProperty("out_height").GetInt32();
            int tileW = header.GetProperty("tile_w").GetInt32();
            int tileH = header.GetProperty("tile_h").GetInt32();
            mismatches.Equal($"{label} width", outW, picture.Width);
            mismatches.Equal($"{label} height", outH, picture.Height);
            mismatches.Equal($"{label} tile count", header.GetProperty("ntiles").GetInt32(), picture.Tiles.Length);

            // the displayed picture assembled the way the oracle does it: zero RGBA, every tile with data copied src -> dst
            byte[] assembled = new byte[outW * outH * 4];
            int t = 0;
            foreach (JsonElement tile in root.GetProperty("tiles").EnumerateArray())
            {
                if (t >= picture.Tiles.Length)
                {
                    break;
                }

                PictureTile actual = picture.Tiles[t];
                int flag = tile.GetProperty("flag").GetInt32();
                JsonElement copy = tile.GetProperty("copy");
                int sx = copy.GetProperty("src")[0].GetInt32();
                int sy = copy.GetProperty("src")[1].GetInt32();
                int dx = copy.GetProperty("dst")[0].GetInt32();
                int dy = copy.GetProperty("dst")[1].GetInt32();
                int cw = copy.GetProperty("size")[0].GetInt32();
                int ch = copy.GetProperty("size")[1].GetInt32();
                string at = $"{label} tile {t}";
                mismatches.Equal($"{at} index", tile.GetProperty("index").GetInt32(), actual.Index);
                mismatches.Equal($"{at} flags", flag, (int)actual.Flags);
                mismatches.Equal($"{at} flags (header)", header.GetProperty("tile_flags")[t].GetInt32(), (int)actual.Flags);
                mismatches.Equal($"{at} stored width", tileW, actual.StoredWidth);
                mismatches.Equal($"{at} stored height", tileH, actual.StoredHeight);
                mismatches.Equal($"{at} source", new PixelRect(sx, sy, cw, ch), actual.Source);
                mismatches.Equal($"{at} x", dx, actual.X);
                mismatches.Equal($"{at} y", dy, actual.Y);

                if (flag == 0)
                {
                    mismatches.Equal($"{at} pixel bytes (empty tile)", 0, actual.Rgba.Length);
                }
                else
                {
                    mismatches.Equal($"{at} pixel bytes", tileW * tileH * 4, actual.Rgba.Length);
                    mismatches.Equal($"{at} RGBA SHA-1", tile.GetProperty("rgba_sha1").GetString()!, SliceExpect.Sha1(actual.Rgba.Span));
                    if (actual.Rgba.Length == tileW * tileH * 4)
                    {
                        ReadOnlySpan<byte> rgba = actual.Rgba.Span;
                        for (int row = 0; row < ch; row++)
                        {
                            rgba.Slice((((sy + row) * tileW) + sx) * 4, cw * 4).CopyTo(assembled.AsSpan((((dy + row) * outW) + dx) * 4));
                        }
                    }
                }

                t++;
            }

            mismatches.Equal($"{label} tiles walked", picture.Tiles.Length, t);
            JsonElement assembledInfo = root.GetProperty("assembled");
            mismatches.Equal($"{label} assembled width", assembledInfo.GetProperty("width").GetInt32(), outW);
            mismatches.Equal($"{label} assembled height", assembledInfo.GetProperty("height").GetInt32(), outH);
            mismatches.Equal($"{label} assembled SHA-1", assembledInfo.GetProperty("sha1").GetString()!, SliceExpect.Sha1(assembled));
        }

        mismatches.AssertNone("pictures through IGameData.Picture");
    }

    [GameDataFact]
    public void AudioClipsEqualTheManifestWithTheirLoops()
    {
        var files = SliceExpect.AudioFiles();
        Assert.Equal(SliceExpect.Count("audio.voices") + SliceExpect.Count("audio.ses") + SliceExpect.Count("audio.bgm"), files.Count);
        var mismatches = new Mismatches();
        foreach ((AudioBankName bankName, int entry, string jsonPath) in files)
        {
            using JsonDocument doc = SliceExpect.Json(jsonPath);
            JsonElement root = doc.RootElement;
            string label = $"{bankName.Name}#{entry}";
            AudioClip clip = Source.Audio(bankName.Bank, entry);
            JsonElement pcm = root.GetProperty("pcm");
            JsonElement fact = root.GetProperty("fact");

            mismatches.Equal($"{label} bank", bankName.Bank, clip.Bank);
            mismatches.Equal($"{label} index", entry, clip.Index);
            mismatches.Equal($"{label} channels", root.GetProperty("channels").GetInt32(), clip.Channels);
            mismatches.Equal($"{label} sample rate", root.GetProperty("rate").GetInt32(), clip.SampleRate);
            mismatches.Equal($"{label} frames (header)", root.GetProperty("frames").GetInt32(), clip.FrameCount);
            mismatches.Equal($"{label} frames (fact[0])", fact[0].GetInt32(), clip.FrameCount);
            mismatches.Equal($"{label} sample count", pcm.GetProperty("frames").GetInt32() * pcm.GetProperty("channels").GetInt32(), clip.Samples.Length);
            mismatches.Equal($"{label} PCM SHA-1", pcm.GetProperty("sha1").GetString()!, SliceExpect.Sha1(clip.Samples.Span));

            double peak = 0;
            double sumSquares = 0;
            foreach (float s in clip.Samples.Span)
            {
                peak = Math.Max(peak, Math.Abs((double)s));
                sumSquares += (double)s * s;
            }

            mismatches.Equal($"{label} peak", pcm.GetProperty("peak").GetDouble(), peak);
            double rms = Math.Sqrt(sumSquares / clip.Samples.Length);
            if (Math.Abs(rms - pcm.GetProperty("rms").GetDouble()) > 1e-9)
            {
                mismatches.Add($"{label} rms: expected {pcm.GetProperty("rms").GetDouble():R}, got {rms:R}");
            }

            // the loop: smpl loop point minus the encoder delay fact[2], end inclusive; none when there is no smpl or start == end
            JsonElement smpl = root.GetProperty("smpl");
            AudioLoop? expectedLoop = null;
            if (smpl.GetArrayLength() > 0)
            {
                int start = smpl[0].GetProperty("start").GetInt32() - fact[2].GetInt32();
                int end = smpl[0].GetProperty("end").GetInt32() - fact[2].GetInt32();
                expectedLoop = start == end ? null : new AudioLoop(start, end);
            }

            mismatches.Equal($"{label} loop", expectedLoop, clip.Loop);
            JsonElement loop = root.GetProperty("loop");
            AudioLoop? manifestLoop = loop.ValueKind == JsonValueKind.Object
                ? new AudioLoop(loop.GetProperty("start").GetInt32(), loop.GetProperty("end").GetInt32())
                : null;
            mismatches.Equal($"{label} loop (manifest field)", manifestLoop, clip.Loop);
        }

        mismatches.AssertNone("audio through IGameData.Audio");
    }

    [GameDataFact]
    public void TheBgmLoopOfTrack33IsTheOneTheSpikeMeasured()
    {
        // ROADMAP spike S1 / audio.md 2.4: bgm #33 loops [962000, 8257999] in playable frames, end inclusive.
        AudioClip bgm = Source.Audio(AudioBank.Bgm, 33);
        Assert.Equal(new AudioLoop(962_000, 8_257_999), bgm.Loop);
        Assert.Equal(7_296_000, bgm.Loop!.Value.Length);
        Assert.Equal(2, bgm.Channels);
        Assert.Equal(48_000, bgm.SampleRate);
        Assert.Equal(8_258_048, bgm.FrameCount);
    }

    [GameDataFact]
    public void VoicesDoNotLoopAndTheSoundEffectDoes()
    {
        for (int index = 0x3D00; index <= 0x3D08; index++)
        {
            AudioClip voice = Source.Audio(AudioBank.Voice, index);
            Assert.Null(voice.Loop);
            Assert.Equal(1, voice.Channels);
        }

        AudioClip se = Source.Audio(AudioBank.Se, 598);
        Assert.Equal(new AudioLoop(133_437, 893_347), se.Loop);
        Assert.Equal(923_520, se.FrameCount);
    }

    [GameDataFact]
    public void MoviePathsPointAtThePrologueFilesWithTheManifestSizes()
    {
        using JsonDocument doc = SliceExpect.Json("eboot/movies.json");
        int checkedFiles = 0;
        foreach (JsonElement f in doc.RootElement.GetProperty("files").EnumerateArray())
        {
            int index = f.GetProperty("index").GetInt32();
            string path = Source.Movie(index);
            Assert.True(Path.IsPathRooted(path), path);
            Assert.EndsWith(f.GetProperty("file").GetString()!, path, StringComparison.Ordinal);
            Assert.True(File.Exists(path), $"movie {index}: {path} does not exist");
            Assert.Equal(f.GetProperty("size").GetInt64(), new FileInfo(path).Length);
            checkedFiles++;
        }

        Assert.True(checkedFiles > 0, "the slice records no movie file");
    }
}
#endif
