using Tsuki.Data.Containers;
using Tsuki.Data.IO;

namespace Tsuki.Tests.Synthetic.Data;

/// <summary>mrgd00 containers, the memory-mapped file and the triplet reader on crafted files (containers.md 2, 4). No game data.</summary>
public sealed class ContainerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-synth-" + Guid.NewGuid().ToString("N"));

    public ContainerTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Pattern(int length, int seed)
    {
        byte[] b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    // ---- mrgd00 ------------------------------------------------------------------------------

    [Fact]
    public void ContainerRoundTripFollowsThePackingRule()
    {
        // sizes cross sector and 64 KiB boundaries: 0x1FFF0 needs the "- 0x10000" step, 70,000 the plain rule
        byte[][] sections = [Pattern(1, 1), Pattern(0x424, 2), Pattern(0x408, 3), Pattern(70_000, 4), Pattern(0x1FFF0, 5), Pattern(2048, 6), Pattern(7, 7)];
        byte[] file = MzpBuilder.Build(sections);
        var c = MzpContainer.Parse(file);
        Assert.Equal(sections.Length, c.Count);
        Assert.Equal(8 + (8 * sections.Length), c.DataStart);
        for (int i = 0; i < sections.Length; i++)
        {
            Assert.Equal(sections[i].Length, c.Sections[i].Size);
            Assert.True(sections[i].AsSpan().SequenceEqual(c.Section(i).Span), $"section {i}");
        }

        Assert.Empty(c.Validate());
        Assert.Equal(file.Length, c.PackedSize);
        Assert.True(c.End < c.PackedSize);
        Assert.False(c.IsNested(0));
        Assert.Equal(4, c.Head(1, 4).Length);
        Assert.Equal(1, c.Head(0, 16).Length);
    }

    [Fact]
    public void SectionsAreEightByteAlignedWithOneToEightBytesOfFf()
    {
        byte[] file = MzpBuilder.Build(Pattern(8, 1), Pattern(5, 2), Pattern(16, 3));
        var c = MzpContainer.Parse(file);
        // section 0 ends on an 8-byte boundary: the pad is a full 8 bytes (never 0)
        Assert.Equal(c.Sections[0].End + 8, c.Sections[1].Offset);
        Assert.Equal(c.Sections[1].End + 3, c.Sections[2].Offset);
        Assert.Equal(0, c.Sections[2].Offset % 8);
        Assert.All(file.AsSpan((int)c.Sections[0].End, 8).ToArray(), b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void ValidateFindsDeviations()
    {
        byte[] file = MzpBuilder.Build(Pattern(10, 1), Pattern(10, 2));
        var c = MzpContainer.Parse(file);
        file[(int)c.Sections[0].End] = 0x00; // damage the padding
        var issues = MzpContainer.Parse(file).Validate();
        Assert.Contains(issues, i => i.Contains("padding before it is not 0xFF"));
    }

    [Fact]
    public void NestedContainersAreDetectedAndAddressedRelativeToTheirOwnStart()
    {
        byte[] inner = MzpBuilder.Build(Pattern(100, 1), Pattern(3000, 2));
        byte[] outer = MzpBuilder.Build(Pattern(5, 9), inner, Pattern(12, 8));
        var c = MzpContainer.Parse(outer);
        Assert.False(c.IsNested(0));
        Assert.True(c.IsNested(1));
        var child = c.Child(1);
        Assert.Equal(2, child.Count);
        Assert.True(Pattern(3000, 2).AsSpan().SequenceEqual(child.Section(1).Span));
        Assert.Empty(c.Validate());
        Assert.Empty(child.Validate());
        Assert.True(MzpContainer.IsMzp(inner));
        Assert.False(MzpContainer.IsMzp(Pattern(100, 5)));
    }

    [Fact]
    public void BadContainersAreRejectedWithAReason()
    {
        Assert.Contains("magic", Assert.Throws<ContainerFormatException>(() => MzpContainer.Parse(new byte[64])).Message);
        Assert.Throws<ContainerFormatException>(() => MzpContainer.Parse(new byte[4]));

        byte[] file = MzpBuilder.Build(Pattern(100, 1));
        Assert.Contains("exceeds", Assert.Throws<ContainerFormatException>(() => MzpContainer.Parse(file.AsMemory(0, file.Length - 50))).Message);

        byte[] tooMany = MzpBuilder.Build(Pattern(10, 1));
        tooMany[6] = 0xFF;
        tooMany[7] = 0xFF; // 65535 sections cannot fit
        Assert.Contains("section table", Assert.Throws<ContainerFormatException>(() => MzpContainer.Parse(tooMany)).Message);
    }

    [Fact]
    public void DecodeSizeMatchesTheDocumentedExamples()
    {
        // containers.md 2.3 / 4.2: just below a 64 KiB multiple needs the "- 0x10000" step
        Assert.Equal(0x1FFF0, Mzp.DecodeSize(64, 0xFFF0));
        Assert.Equal(0xFFF0, Mzp.DecodeSize(32, 0xFFF0));
        Assert.Equal(0x424, Mzp.DecodeSize(1, 0x0424));
        Assert.Equal(0x408, Mzp.DecodeSize(2, 0x0408));
        Assert.Equal(0, Mzp.DecodeSize(0, 0));
        Assert.Equal(5, Mzp.PadAfter(3));
        Assert.Equal(8, Mzp.PadAfter(16));
    }

    [Fact]
    public void ReadTableWorksOnTheHeadAloneWithAnotherTotalLength()
    {
        byte[] file = MzpBuilder.Build(Pattern(100, 1), Pattern(5000, 2));
        byte[] head = file[..(8 + 16)];
        var sections = MzpContainer.ReadTable(head, file.Length);
        Assert.Equal(2, sections.Length);
        Assert.Equal(5000, sections[1].Size);
        Assert.Throws<ContainerFormatException>(() => MzpContainer.ReadTable(file.AsSpan(0, 12), file.Length));
        Assert.Throws<ContainerFormatException>(() => MzpContainer.ReadTable(head, 50));
    }

    // ---- memory-mapped file ------------------------------------------------------------------

    [Fact]
    public void MappedFileServesRangesWithoutCopyingTheFile()
    {
        string path = Path.Combine(_dir, "big.bin");
        byte[] content = Pattern(3 * 1024 * 1024 + 17, 42);
        File.WriteAllBytes(path, content);
        using (var f = MappedFile.Open(path))
        {
            Assert.Equal(content.Length, f.Length);
            Assert.True(content.AsSpan(1_000_000, 4096).SequenceEqual(f.Span(1_000_000, 4096)));
            Assert.True(content.AsSpan(content.Length - 17).SequenceEqual(f.Memory(content.Length - 17, 17).Span));
            Assert.Equal(content[5], f.Read(5, 1)[0]);
            Assert.Equal(0, f.Span(content.Length, 0).Length);
            Assert.Throws<ArgumentOutOfRangeException>(() => f.Span(content.Length - 1, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => f.Span(-1, 1));
        }
    }

    [Fact]
    public void MappedFileRefusesAccessAfterDispose()
    {
        string path = Path.Combine(_dir, "small.bin");
        File.WriteAllBytes(path, Pattern(100, 1));
        var f = MappedFile.Open(path);
        var memory = f.Memory(0, 50);
        Assert.Equal(50, memory.Span.Length);
        f.Dispose();
        f.Dispose(); // idempotent
        Assert.Throws<ObjectDisposedException>(() => f.Span(0, 1));
        Assert.Throws<ObjectDisposedException>(() => memory.Span.Length);
    }

    [Fact]
    public void MappedFileHandlesEmptyAndMissingFiles()
    {
        string empty = Path.Combine(_dir, "empty.bin");
        File.WriteAllBytes(empty, []);
        using var f = MappedFile.Open(empty);
        Assert.Equal(0, f.Length);
        Assert.Equal(0, f.Span(0, 0).Length);
        Assert.Throws<FileNotFoundException>(() => MappedFile.Open(Path.Combine(_dir, "missing.bin")));
    }

    [Fact]
    public void MappedFileCanBeOpenedByManyThreadsAndReadConcurrently()
    {
        string path = Path.Combine(_dir, "shared.bin");
        byte[] content = Pattern(1 << 20, 3);
        File.WriteAllBytes(path, content);
        using var f = MappedFile.Open(path);
        int bad = 0;
        Parallel.For(0, 64, i =>
        {
            int off = i * 4000;
            if (!content.AsSpan(off, 4000).SequenceEqual(f.Span(off, 4000)))
            {
                Interlocked.Increment(ref bad);
            }
        });
        Assert.Equal(0, bad);
    }

    // ---- triplet archive ---------------------------------------------------------------------

    [Fact]
    public void Hed8TripletOpensAndServesEntries()
    {
        byte[] e0 = Pattern(100, 1);
        byte[] e1 = Pattern(0x800, 2);
        byte[] e2 = Pattern(0x1801, 3);
        byte[] mrg = [.. Padded(e0), .. Padded(e1), .. Padded(e2)];
        byte[] hed = HedBuilder.Join(HedBuilder.Hed8(0, e0.Length), HedBuilder.Hed8(1, e1.Length), HedBuilder.Hed8(2, e2.Length), HedBuilder.Ff(16));
        byte[] nam = NamBuilder.Nam32(true, "BLACK.MZP", "Blue.Mzp", "THIRD.MZP");
        File.WriteAllBytes(Path.Combine(_dir, "demo.hed"), hed);
        File.WriteAllBytes(Path.Combine(_dir, "demo.nam"), nam);
        File.WriteAllBytes(Path.Combine(_dir, "demo.mrg"), mrg);

        using var a = MrgArchive.Open(Path.Combine(_dir, "demo"));
        Assert.Equal(3, a.Count);
        Assert.Equal(HedFormat.Hed8, a.Index.Format);
        Assert.Empty(a.Index.Validate());
        Assert.True(a.Index.NamesAligned);
        Assert.Equal("BLACK.MZP", a.Entries[0].Name);
        Assert.Equal(1, a.Find("blue.mzp"));
        Assert.Equal(-1, a.Find("nothing"));
        Assert.True(e0.AsSpan().SequenceEqual(a.Span(0)));
        Assert.True(e2.AsSpan().SequenceEqual(a.Memory(2).Span));
        Assert.Equal(e1, a.Read(1));
        Assert.Equal(0x2000, a.SectorMemory(2).Length); // the whole sector span, padding included
        Assert.Equal(0x800L, a.Entries[1].Offset);
        Assert.Equal(0x1801L, a.Entries[2].Size);

        // the same archive opened by its .mrg path
        using var b = MrgArchive.Open(Path.Combine(_dir, "demo.mrg"));
        Assert.Equal(3, b.Count);
    }

    [Fact]
    public void Hed4TripletReportsSectorRoundedSizes()
    {
        byte[] mrg = new byte[3 * 0x800];
        byte[] hed = HedBuilder.Join(HedBuilder.Hed4(0, 1), HedBuilder.Hed4(1, 2), HedBuilder.Ff(16));
        byte[] nam = NamBuilder.Nam8("DU", "A00_");
        File.WriteAllBytes(Path.Combine(_dir, "voicey.hed"), hed);
        File.WriteAllBytes(Path.Combine(_dir, "voicey.nam"), nam);
        File.WriteAllBytes(Path.Combine(_dir, "voicey.mrg"), mrg);
        using var a = MrgArchive.Open(Path.Combine(_dir, "voicey"));
        Assert.Equal(HedFormat.Hed4, a.Index.Format);
        Assert.False(a.Entries[1].SizeExact);
        Assert.Equal(0x1000L, a.Entries[1].Size);
        Assert.Equal(-1, a.Entries[1].SizeLo);
        Assert.Equal("A00_", a.Entries[1].Name);
        Assert.Empty(a.Index.Validate());
    }

    [Fact]
    public void ValidateReportsGapsExtentsAndNames()
    {
        var hed = HedBuilder.Join(HedBuilder.Hed8(0, 100), HedBuilder.Hed8(3, 100), HedBuilder.Ff(16)); // gap: entry 1 at sector 3
        var index = ArchiveIndex.Create("gappy", hed, NamTable.ParseNam32(NamBuilder.Nam32(true, "A")), 2 * 0x800);
        var issues = index.Validate();
        Assert.Contains(issues, i => i.Contains("sector 3 != previous end 1"));
        Assert.Contains(issues, i => i.Contains("beyond file size"));
        Assert.Contains(issues, i => i.Contains("NAM has 1 names for 2 entries"));
        Assert.False(index.NamesAligned);
    }

    [Fact]
    public void StandaloneFileOpensAsAContainer()
    {
        byte[] file = MzpBuilder.Build(Pattern(40, 1), Pattern(9000, 2));
        string path = Path.Combine(_dir, "stand.mrg");
        File.WriteAllBytes(path, file);
        using var s = StandaloneMrg.Open(path);
        Assert.Equal(2, s.Container.Count);
        Assert.Equal(file.Length, s.Length);
        Assert.Equal(s.Length, s.Container.PackedSize);
        File.WriteAllBytes(Path.Combine(_dir, "notmrg.mrg"), Pattern(100, 1));
        Assert.Throws<ContainerFormatException>(() => StandaloneMrg.Open(Path.Combine(_dir, "notmrg.mrg")));
    }

    private static byte[] Padded(byte[] data)
    {
        int sectors = (data.Length + 0x7FF) / 0x800;
        byte[] padded = new byte[sectors * 0x800];
        data.CopyTo(padded, 0);
        for (int i = data.Length; i < padded.Length; i++)
        {
            padded[i] = (byte)(i % 16 == 8 ? 0x0C : 0); // packer garbage, never read
        }

        return padded;
    }
}
