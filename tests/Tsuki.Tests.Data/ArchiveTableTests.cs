using Tsuki.Data.Containers;

namespace Tsuki.Tests.Data;

/// <summary>
/// HED8 / HED4 / NAM32 / NAM8 readers and MRG entry access against the Python oracle: the entry tables of all
/// 12 archives (49,620 entries, ROADMAP M1 acceptance 1).
/// </summary>
public sealed class ArchiveTableTests
{
    public static TheoryData<string> Archives => new(GameImage.Triplets);

    [GameDataTheory]
    [MemberData(nameof(Archives))]
    public void ArchiveFactsEqualTheOracle(string name)
    {
        var row = GoldenData.Tsv("archives.tsv").Single(r => r["name"] == name);
        using var a = GameImage.Open(name);
        var m = new Mismatches();
        m.Equal("format", row["format"], a.Index.Format.ToString().ToLowerInvariant());
        m.Equal("entries", row.Int("entries"), a.Count);
        m.Equal("hed terminator", row["terminator_hex"], Convert.ToHexStringLower(a.Index.Hed.Terminator.Span));
        m.Equal("hed trailing", row["trailing_hex"], Convert.ToHexStringLower(a.Index.Hed.Trailing.Span));
        m.Equal("contiguous", row.Flag("contiguous"), a.Index.Hed.Contiguous);
        m.Equal("bank starts", row["bank_starts"], string.Join(",", a.Index.Hed.BankStarts));
        m.Equal("mrg bytes", row.Long("mrg_bytes"), a.Index.MrgLength);
        m.Equal("validate issues", row.Int("validate_issues"), a.Index.Validate().Count);
        int namRecordSize = row.Int("nam_record_size");
        if (namRecordSize == 0)
        {
            Assert.Null(a.Index.Nam);
        }
        else
        {
            var nam = a.Index.Nam!;
            m.Equal("nam record size", namRecordSize, nam.RecordSize);
            m.Equal("nam records", row.Int("nam_records"), nam.Count);
            m.Equal("nam terminator", row.Flag("nam_terminator"), nam.Terminator);
            m.Equal("nam trailing", row["nam_trailing_hex"], Convert.ToHexStringLower(nam.Trailing.Span));
        }

        m.AssertNone(name);
    }

    [GameDataTheory]
    [MemberData(nameof(Archives))]
    public void EveryEntryEqualsTheOracle(string name)
    {
        var rows = GoldenData.Tsv($"entries/{name}.tsv");
        using var a = GameImage.Open(name);
        Assert.Equal(rows.Count, a.Count);
        var m = new Mismatches();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var e = a.Entries[i];
            if (e.Index != r.Int("index")
                || (e.Name ?? string.Empty) != r["name"]
                || e.Offset != r.Long("offset")
                || e.Size != r.Long("size")
                || e.SizeExact != r.Flag("size_exact")
                || e.SectorOffset != r.Long("sector_offset")
                || e.SizeSectors != r.Int("size_sectors")
                || e.SizeLo != r.Int("size_lo")
                || e.HedRaw != r["hed_raw"])
            {
                m.Add($"{name} entry {i}: got {e}, oracle row {string.Join(" ", new[] { r["name"], r["offset"], r["size"], r["sector_offset"], r["size_sectors"], r["size_lo"], r["hed_raw"] })}");
            }
        }

        m.AssertNone(name);
    }

    [GameDataFact]
    public void TheTwelveArchivesHold49620Entries()
    {
        int total = 0;
        foreach (string name in GameImage.Triplets)
        {
            using var a = GameImage.Open(name);
            total += a.Count;
        }

        Assert.Equal(49_620, total);
    }

    [GameDataFact]
    public void BankStartsAreTheDocumentedOnes()
    {
        using var a0 = GameImage.Open("allpac0");
        using var a1 = GameImage.Open("allpac1");
        using var a12 = GameImage.Open("allpac1_2");
        Assert.Equal([474, 992], a0.Index.Hed.BankStarts);
        Assert.Equal([311, 799, 1403, 2212, 3449, 3725, 4197, 5033, 6199, 7280, 9115], a1.Index.Hed.BankStarts);
        Assert.Equal([2503, 3417, 3681, 4221], a12.Index.Hed.BankStarts);
    }

    [GameDataFact]
    public void Voice7850IsARealEntryAtSector0x3FFFF()
    {
        // containers.md 3.1: the only HED4 record with w0 = 0xFFFF; it must not be read as "empty".
        using var a = GameImage.Open("voice");
        var e = a.Entries[7850];
        Assert.Equal("ffff1b30", e.HedRaw);
        Assert.Equal(0x3FFFFL, e.SectorOffset);
        Assert.Equal(27, e.SizeSectors);
    }

    [GameDataTheory]
    [MemberData(nameof(Archives))]
    public void MappedEntriesEqualIndependentPositionalReads(string name)
    {
        using var a = GameImage.Open(name);
        using var handle = File.OpenHandle(GameImage.Path(name + ".mrg"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var m = new Mismatches();
        var picks = new SortedSet<int> { 0, a.Count - 1 };
        for (int i = 0; i < a.Count; i += Math.Max(1, a.Count / 60))
        {
            picks.Add(i);
        }

        byte[] buffer = new byte[64];
        foreach (int i in picks)
        {
            var e = a.Entries[i];
            var span = a.Span(i);
            Assert.Equal(e.Size, span.Length);
            int head = Math.Min(64, span.Length);
            RandomAccess.Read(handle, buffer.AsSpan(0, head), e.Offset);
            if (!buffer.AsSpan(0, head).SequenceEqual(span[..head]))
            {
                m.Add($"{name} entry {i}: first {head} bytes differ from a positional read at 0x{e.Offset:x}");
            }

            int tail = Math.Min(64, span.Length);
            RandomAccess.Read(handle, buffer.AsSpan(0, tail), e.End - tail);
            if (!buffer.AsSpan(0, tail).SequenceEqual(span[^tail..]))
            {
                m.Add($"{name} entry {i}: last {tail} bytes differ from a positional read");
            }
        }

        m.AssertNone(name);
    }
}
