using System.Buffers.Binary;
using System.Security.Cryptography;
using Tsuki.Data.Compression;
using Tsuki.Data.Containers;

namespace Tsuki.Tests.Data;

/// <summary>
/// MZX0 output against the Python oracle on the stratified sample of <c>mzx_samples.tsv</c> (2,733 streams over
/// every archive and standalone file: tiles, font pages, 549 inverted scripts, voices with the LV wrapper), and
/// the sector and size rules of the payloads (ROADMAP M1 acceptance 1 and 3).
/// </summary>
public sealed class MzxOracleTests
{
    private sealed record Sample(
        int Id, string Source, int Entry, string Path, string Kind, bool Invert, int Declared, int Consumed, int Commands, int Overshoot, string Sha256);

    private static List<Sample> LoadSamples() =>
        GoldenData.Tsv("mzx_samples.tsv")
            .Select(r => new Sample(r.Int("id"), r["source"], r.Int("entry"), r["path"], r["kind"], r.Flag("invert"),
                r.Int("declared"), r.Int("consumed"), r.Int("commands"), r.Int("overshoot"), r["sha256"]))
            .ToList();

    [GameDataFact]
    public void TheSampleIsStratifiedOverEveryArchive()
    {
        var samples = LoadSamples();
        Assert.True(samples.Count >= 2000, $"{samples.Count} samples");
        var counts = samples.GroupBy(s => s.Source).ToDictionary(g => g.Key, g => g.Count());
        foreach (string source in new[] { "allpac0", "allpac1", "allpac1_2", "allpac2" })
        {
            Assert.True(counts.GetValueOrDefault(source) >= 200, $"{source}: {counts.GetValueOrDefault(source)}");
        }

        Assert.True(counts.GetValueOrDefault("allpac3") >= 100);
        Assert.True(counts.GetValueOrDefault("allpac4") >= 30);
        Assert.True(counts.GetValueOrDefault("parts") >= 20);
        Assert.True(counts.GetValueOrDefault("voice") >= 300);
        Assert.Equal(2, counts.GetValueOrDefault("voice2"));
        Assert.Equal(550, counts.GetValueOrDefault("allscr.mrg"));
        foreach (string ft in new[] { "ft_en.mrg", "ft_ja.mrg", "ft_ko.mrg", "ft_zc.mrg", "ft_zt.mrg" })
        {
            Assert.True(counts.GetValueOrDefault(ft) >= 30, ft);
        }

        Assert.True(samples.Count(s => s.Declared % 2 == 1) >= 100, "odd declared sizes");
        Assert.True(samples.Any(s => s.Overshoot >= 120), "streams that overrun by about 127 bytes");
        Assert.True(samples.Count(s => s.Invert) == 549);
    }

    [GameDataFact]
    public void MzxOutputEqualsTheOracleOnEverySampledStream()
    {
        var samples = LoadSamples();
        var m = new Mismatches();
        foreach (var group in samples.GroupBy(s => s.Source))
        {
            Decode(group.Key, group.ToList(), poison: false, m);
        }

        m.AssertNone("MZX0 samples");
    }

    [GameDataFact]
    public void MzxOutputDoesNotDependOnTheOutputBufferContents()
    {
        // The decoder must never read a byte it has not written (RLE after a reset restarts from 0, ring slots
        // are written before they are read): decode again into a buffer full of 0xA5 and compare.
        var samples = LoadSamples().Where(s => s.Id % 3 == 0).ToList();
        var m = new Mismatches();
        foreach (var group in samples.GroupBy(s => s.Source))
        {
            Decode(group.Key, group.ToList(), poison: true, m);
        }

        m.AssertNone("MZX0 samples, poisoned buffer");
    }

    private static void Decode(string source, List<Sample> samples, bool poison, Mismatches m)
    {
        if (source is "voice" or "voice2")
        {
            using var archive = GameImage.Open(source);
            Parallel.ForEach(samples, s =>
            {
                var entry = archive.SectorMemory(s.Entry).Span;
                var header = Lv.Parse(entry);
                var body = entry[header.BodyOffset..];
                byte[] buffer = NewBuffer(Mzx.DeclaredSize(body), poison);
                var stats = Mzx.Decode(body, buffer);
                Compare(m, s, header.BodyOffset + stats.Consumed, stats, buffer.AsSpan(0, s.Declared));
                var lv = Lv.Decompress(entry);
                if (!lv.Compressed || lv.Length != s.Declared || lv.Consumed != s.Consumed || Hash(lv.Data.Span) != s.Sha256)
                {
                    m.Add($"{s.Source}#{s.Entry}: Lv.Decompress disagrees with the oracle");
                }
            });
        }
        else if (source.EndsWith(".mrg", StringComparison.Ordinal))
        {
            using var mrg = StandaloneMrg.Open(GameImage.Path(source));
            Parallel.ForEach(samples, s => DecodeSection(mrg.Container, s, poison, m));
        }
        else
        {
            using var archive = GameImage.Open(source);
            Parallel.ForEach(samples, s => DecodeSection(archive.OpenContainer(s.Entry), s, poison, m));
        }
    }

    private static void DecodeSection(MzpContainer container, Sample s, bool poison, Mismatches m)
    {
        string[] parts = s.Path.Split('/');
        var c = container;
        foreach (string p in parts[..^1])
        {
            c = c.Child(int.Parse(p));
        }

        var section = c.Section(int.Parse(parts[^1])).Span;
        byte[] buffer = NewBuffer(Mzx.DeclaredSize(section), poison);
        var stats = Mzx.Decode(section, buffer, s.Invert);
        Compare(m, s, stats.Consumed, stats, buffer.AsSpan(0, s.Declared));
        if (stats.Consumed != section.Length)
        {
            m.Add($"{s.Source}#{s.Entry}/{s.Path}: consumed {stats.Consumed} of a {section.Length}-byte section");
        }
    }

    private static byte[] NewBuffer(int declared, bool poison)
    {
        byte[] buffer = new byte[Mzx.BufferSize(declared)];
        if (poison)
        {
            Array.Fill(buffer, (byte)0xA5);
        }

        return buffer;
    }

    private static void Compare(Mismatches m, Sample s, int consumed, MzxStats stats, ReadOnlySpan<byte> output)
    {
        string id = $"{s.Source}#{s.Entry}/{s.Path}";
        if (output.Length != s.Declared)
        {
            m.Add($"{id}: declared {output.Length}, oracle {s.Declared}");
            return;
        }

        if (consumed != s.Consumed)
        {
            m.Add($"{id}: consumed {consumed}, oracle {s.Consumed}");
        }

        if (stats.Commands != s.Commands)
        {
            m.Add($"{id}: commands {stats.Commands}, oracle {s.Commands}");
        }

        if (stats.Overshoot != s.Overshoot)
        {
            m.Add($"{id}: overshoot {stats.Overshoot}, oracle {s.Overshoot}");
        }

        if (Hash(output) != s.Sha256)
        {
            m.Add($"{id}: SHA-256 of the output differs from the oracle's");
        }
    }

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // ---- payload size fields (ROADMAP M1 acceptance 1) --------------------------------------------

    [GameDataTheory]
    [InlineData("allpac0")]
    [InlineData("allpac1")]
    [InlineData("allpac1_2")]
    [InlineData("allpac2")]
    [InlineData("allpac3")]
    [InlineData("allpac4")]
    [InlineData("parts")]
    public void HedSizeEqualsThePackedSizeOfTheContainer(string name)
    {
        using var archive = GameImage.Open(name);
        var m = new Mismatches();
        Parallel.For(0, archive.Count, e =>
        {
            var c = archive.OpenContainer(e);
            if (c.PackedSize != archive.Entries[e].Size)
            {
                m.Add($"{name}#{e}: packed size {c.PackedSize} != HED size {archive.Entries[e].Size}");
            }
        });
        m.AssertNone(name);
    }

    [GameDataTheory]
    [InlineData("bgm", 132)]
    [InlineData("vse", 1327)]
    public void RiffSizeEqualsTheHedSize(string name, int count)
    {
        using var archive = GameImage.Open(name);
        Assert.Equal(count, archive.Count);
        var m = new Mismatches();
        for (int i = 0; i < archive.Count; i++)
        {
            var data = archive.Span(i);
            if (!data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
            {
                m.Add($"{name}#{i}: not RIFF/WAVE");
            }
            else if (BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) + 8L != archive.Entries[i].Size)
            {
                m.Add($"{name}#{i}: RIFF size + 8 != HED size");
            }
        }

        m.AssertNone(name);
    }

    [GameDataFact]
    public void VagDataSizePlus48EqualsTheHedSize()
    {
        using var archive = GameImage.Open("vag");
        Assert.Equal(19, archive.Count);
        for (int i = 0; i < archive.Count; i++)
        {
            var data = archive.Span(i);
            Assert.True(data[..4].SequenceEqual("VAGp"u8), $"vag#{i} magic");
            Assert.Equal(archive.Entries[i].Size, BinaryPrimitives.ReadUInt32BigEndian(data[12..]) + 48L);
        }
    }

    [GameDataTheory]
    [InlineData("voice", 16464)]
    [InlineData("voice2", 2)]
    public void VoiceEntriesFollowTheLvAndHed4Rules(string name, int count)
    {
        // containers.md 3.1 and 6: header 4c 56 03 00 00 09 00, MZX body, RIFF size == MZX size, and the HED4
        // sector count is exact / 0x800 + 1 with exact = 4 + n + consumed. All 16,466 entries.
        using var archive = GameImage.Open(name);
        Assert.Equal(count, archive.Count);
        var m = new Mismatches();
        long ceilDiffers = 0;
        Parallel.For(0, archive.Count, () => new byte[Mzx.BufferSize(1 << 22)], (i, _, buffer) =>
        {
            var entry = archive.SectorMemory(i).Span;
            var header = Lv.Parse(entry);
            if (!header.Present || header.Length != 3 || !entry[..7].SequenceEqual(new byte[] { 0x4C, 0x56, 0x03, 0x00, 0x00, 0x09, 0x00 }))
            {
                m.Add($"{name}#{i}: unexpected LV header");
            }

            var body = entry[header.BodyOffset..];
            int declared = Mzx.DeclaredSize(body);
            if (buffer.Length < Mzx.BufferSize(declared))
            {
                buffer = new byte[Mzx.BufferSize(declared)];
            }

            var stats = Mzx.Decode(body, buffer);
            long exact = header.BodyOffset + stats.Consumed;
            if (archive.Entries[i].SizeSectors != (exact / Mzp.Sector) + 1)
            {
                m.Add($"{name}#{i}: {archive.Entries[i].SizeSectors} sectors, exact {exact} / 0x800 + 1");
            }

            if (archive.Entries[i].SizeSectors != (exact + Mzp.Sector - 1) / Mzp.Sector)
            {
                Interlocked.Increment(ref ceilDiffers);
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)) + 8L != declared || !buffer.AsSpan(0, 4).SequenceEqual("RIFF"u8))
            {
                m.Add($"{name}#{i}: RIFF size + 8 != MZX declared size {declared}");
            }

            return buffer;
        }, _ => { });
        m.AssertNone(name);
        if (name == "voice")
        {
            Assert.Equal(4, ceilDiffers); // entries 3028, 4044, 13042, 13595 carry the extra sector
        }
    }
}
