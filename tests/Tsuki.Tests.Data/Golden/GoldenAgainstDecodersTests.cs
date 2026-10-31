#if TSUKI_DEV
using System.Buffers.Binary;
using System.Text;
using Tsuki.Data.Compression;
using Tsuki.Data.Containers;
using Tsuki.Data.Golden;
using Tsuki.Data.Images;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data.Golden;

/// <summary>
/// The golden slice against the C# decoders on the user's own game folder: the early form of "GameFolderSource and GoldenSource
/// must agree" (ARCHITECTURE 3.3) for the data the decoders of waves 1 and 2 already read (allscr sections, script_text, image
/// MZPs, font pages, the RIFF headers of the audio banks). The golden side is the Python oracle's export, so each test compares two
/// independent implementations. Audio PCM is not here: it needs the FFmpeg ATRAC9 decoder of package E1.
/// </summary>
public sealed class GoldenAgainstDecodersTests
{
    [GameDataFact]
    public void ScriptsEqualTheMzxDecodedSectionsOfAllscr()
    {
        using StandaloneMrg mrg = StandaloneMrg.Open(GameImage.Path("allscr.mrg"));
        foreach (int k in new[] { 2, 3 })
        {
            byte[] decoded = Mzx.Decompress(mrg.Container.Section(k + 3).Span, invert: true).ToArray();
            ScriptImage golden = SliceExpect.Source.Script(k);
            Assert.True(decoded.AsSpan().SequenceEqual(golden.Bytes.Span), $"script {k}: the C# MZX0 + XOR decode differs from the golden bytes");
        }

        Assert.Equal(SliceExpect.Source.ScriptCount + 3, mrg.Container.Count);
    }

    [GameDataFact]
    public void TextLinesEqualTheEngineCopyOfTheScriptTextBlob()
    {
        using StandaloneMrg mrg = StandaloneMrg.Open(GameImage.Path("script_text.mrg"));
        MzpContainer c = mrg.Container;
        var mismatches = new Mismatches();
        foreach ((Language lang, int k) in new[] { (Language.Ja, 0), (Language.En, 1) })
        {
            ReadOnlySpan<byte> table = c.Section(2 * k).Span;
            ReadOnlySpan<byte> blob = c.Section((2 * k) + 1).Span;
            int first = 0;
            while (BinaryPrimitives.ReadUInt32BigEndian(table[(4 * first)..]) != 0xFFFFFFFFu)
            {
                first++;
            }

            int count = first - 2;   // text.md 2.1: N = entries before the first end mark, minus the two guard entries
            mismatches.Equal($"{lang} line count", count, SliceExpect.Source.TextLineCount);
            foreach (int id in SliceExpect.Source.TextIds)
            {
                int start = (int)BinaryPrimitives.ReadUInt32BigEndian(table[(4 * id)..]);
                int next = (int)BinaryPrimitives.ReadUInt32BigEndian(table[(4 * (id + 1))..]);
                int length = next - start;
                // VA 0x75F80: len < 3 -> empty, else min(len, 0x301) - 2 bytes (CR LF and anything beyond 0x2FF dropped)
                ReadOnlySpan<byte> engine = length < 3 ? default : blob.Slice(start, Math.Min(length, 0x301) - 2);
                mismatches.Equal($"{lang} line {id} engine copy", Convert.ToHexStringLower(engine), Convert.ToHexStringLower(SliceExpect.Source.TextLine(lang, id).Span));
                mismatches.Equal($"{lang} line {id} raw", Convert.ToHexStringLower(blob.Slice(start, length)), Convert.ToHexStringLower(SliceExpect.Source.RawTextLine(lang, id).Span));
            }
        }

        mismatches.AssertNone("script_text.mrg against the golden lines");
    }

    [GameDataFact]
    public void PicturesEqualTheTilesTheCSharpImageDecoderProduces()
    {
        var mismatches = new Mismatches();
        var archives = new Dictionary<int, MrgArchive>();
        try
        {
            foreach (GoldenKey key in SliceExpect.PictureFiles().Select(p => new GoldenKey(p.Archive, p.Entry)))
            {
                if (!archives.TryGetValue(key.Archive, out MrgArchive? archive))
                {
                    archive = GameImage.Open(SliceExpect.ArchiveNames[key.Archive]);
                    archives[key.Archive] = archive;
                }

                Picture decoded = ImageMzp.Open(archive.Memory(key.Entry)).ToPicture(maxDegreeOfParallelism: 4);
                Picture golden = SliceExpect.Source.Picture(key.Archive, key.Entry);
                string label = $"{SliceExpect.ArchiveNames[key.Archive]}#{key.Entry}";
                mismatches.Equal($"{label} width", decoded.Width, golden.Width);
                mismatches.Equal($"{label} height", decoded.Height, golden.Height);
                mismatches.Equal($"{label} tile count", decoded.Tiles.Length, golden.Tiles.Length);
                for (int t = 0; t < Math.Min(decoded.Tiles.Length, golden.Tiles.Length); t++)
                {
                    PictureTile d = decoded.Tiles[t];
                    PictureTile g = golden.Tiles[t];
                    mismatches.Equal($"{label} tile {t} flags", d.Flags, g.Flags);
                    mismatches.Equal($"{label} tile {t} stored size", (d.StoredWidth, d.StoredHeight), (g.StoredWidth, g.StoredHeight));
                    mismatches.Equal($"{label} tile {t} source", d.Source, g.Source);
                    mismatches.Equal($"{label} tile {t} position", (d.X, d.Y), (g.X, g.Y));
                    if (!d.Rgba.Span.SequenceEqual(g.Rgba.Span))
                    {
                        mismatches.Add($"{label} tile {t}: RGBA differs ({d.Rgba.Length} vs {g.Rgba.Length} bytes)");
                    }
                }
            }
        }
        finally
        {
            foreach (MrgArchive a in archives.Values)
            {
                a.Dispose();
            }
        }

        mismatches.AssertNone("pictures: C# ImageMzp against the golden tiles");
    }

    [GameDataTheory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    public void FontPagesEqualThePalettedPagesOfTheFontArchive(Language lang)
    {
        string code = lang == Language.Ja ? "ja" : "en";
        using StandaloneMrg mrg = StandaloneMrg.Open(GameImage.Path($"ft_{code}.mrg"));
        Assert.Equal(65, mrg.Container.Count);
        var mismatches = new Mismatches();
        for (int page = 0; page < 65; page++)
        {
            MzpContainer inner = mrg.Container.Child(page);
            ReadOnlySpan<byte> header = inner.Section(0).Span;           // fonts.md 3.1: 16-byte head, 256 x RGBA palette, flag byte
            int width = BinaryPrimitives.ReadUInt16LittleEndian(header);
            int height = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
            byte[] indices = Mzx.Decompress(inner.Section(1).Span).ToArray();
            Assert.Equal(width * height, indices.Length);

            // the palette is stored in PS2 CSM1 order (bits 3 and 4 swapped); alpha 0x80 -> 0xFF, else 2a & 0xFF (VA 0xD90B0)
            byte[] palette = new byte[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                int stored = (i & 0xE7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1);
                header.Slice(16 + (4 * stored), 4).CopyTo(palette.AsSpan(4 * i));
                int a = palette[(4 * i) + 3];
                palette[(4 * i) + 3] = (byte)(a == 0x80 ? 0xFF : (2 * a) & 0xFF);
            }

            FontPage golden = SliceExpect.Source.FontPage(lang, page);
            mismatches.Equal($"{code} page {page} size", (width, height), (golden.Width, golden.Height));
            ReadOnlySpan<byte> rgba = golden.Rgba.Span;
            int differing = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                ReadOnlySpan<byte> expected = palette.AsSpan(4 * indices[i], 4);
                if (!expected.SequenceEqual(rgba.Slice(4 * i, 4)))
                {
                    differing++;
                }
            }

            if (differing != 0)
            {
                mismatches.Add($"{code} page {page}: {differing} of {indices.Length} pixels differ from palette[index]");
            }
        }

        mismatches.AssertNone($"ft_{code}.mrg against the golden font pages");
    }

    [GameDataFact]
    public void AudioLoopsEqualTheSmplChunksOfTheRiffInTheBanks()
    {
        // audio.md 2.3-2.4: loop = smpl loop point - fact[2], end inclusive; the frame count is fact[0]
        foreach ((string bank, AudioBank id, int index) in new[] { ("bgm", AudioBank.Bgm, 33), ("vse", AudioBank.Se, 598) })
        {
            using MrgArchive archive = GameImage.Open(bank);
            ReadOnlySpan<byte> riff = archive.Span(index);
            Assert.True(riff[..4].SequenceEqual("RIFF"u8) && riff.Slice(8, 4).SequenceEqual("WAVE"u8), $"{bank}#{index} is not RIFF/WAVE");
            uint[]? fact = null;
            uint loopStart = 0;
            uint loopEnd = 0;
            for (int pos = 12; pos + 8 <= riff.Length;)
            {
                ReadOnlySpan<byte> id4 = riff.Slice(pos, 4);
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(riff[(pos + 4)..]);
                ReadOnlySpan<byte> body = riff.Slice(pos + 8, Math.Min(size, riff.Length - pos - 8));
                if (id4.SequenceEqual("fact"u8))
                {
                    fact = [BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[4..]), BinaryPrimitives.ReadUInt32LittleEndian(body[8..])];
                }
                else if (id4.SequenceEqual("smpl"u8))
                {
                    Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(body[28..]));      // number of loops
                    loopStart = BinaryPrimitives.ReadUInt32LittleEndian(body[44..]);            // 36-byte header, then cue id, type, start, end
                    loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(body[48..]);
                }

                pos += 8 + size + (size & 1);
            }

            Assert.NotNull(fact);
            AudioClip clip = SliceExpect.Source.Audio(id, index);
            Assert.Equal((int)fact[0], clip.FrameCount);
            Assert.Equal(new AudioLoop((int)(loopStart - fact[2]), (int)(loopEnd - fact[2])), clip.Loop);
        }
    }

    [GameDataFact]
    public void EveryMovieTablePathPointsAtAFileOfTheGameFolder()
    {
        GoldenSource source = SliceExpect.Source;
        Assert.Equal(134, source.Tables.Movies.Length);
        var missing = new List<string>();
        for (int i = 0; i < source.Tables.Movies.Length; i++)
        {
            string path = source.Movie(i);
            if (!File.Exists(path))
            {
                missing.Add($"{i}: {path}");
            }
        }

        Assert.True(missing.Count == 0, "movie files missing: " + string.Join("; ", missing.Take(5)));   // counts: eboot.movie_files_present = 134
        Assert.StartsWith(GameFolder.Require(), source.Movie(4), StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct GoldenKey(int Archive, int Entry);
}
#endif
