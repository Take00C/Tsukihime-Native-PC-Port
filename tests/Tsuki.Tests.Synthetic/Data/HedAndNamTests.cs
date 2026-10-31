using Tsuki.Data.Containers;

namespace Tsuki.Tests.Synthetic.Data;

/// <summary>HED8 / HED4 / NAM32 / NAM8 readers on crafted bytes (docs/formats/containers.md 2, 3, 7). No game data.</summary>
public sealed class HedAndNamTests
{
    [Fact]
    public void Hed8WorkedExampleWithABankSwitch()
    {
        // containers.md 2.5: entries 473 and 474 of allpac0. 473 sits at stored 0xFFA20 (nibble F); 474 stored 0x018B
        // (nibble back to 0) is the first of bank 1 and starts at 0x10018B, right after 473.
        byte[] hed = HedBuilder.Join(
            HedBuilder.Hed8Record(0xFA20, 0xF000, 0x76B, 0x5118),
            HedBuilder.Hed8Record(0x018B, 0x0000, 0x6E3, 0x10C0),
            HedBuilder.Ff(16));
        var table = HedTable.Parse(hed);
        Assert.Equal(HedFormat.Hed8, table.Format);
        Assert.Equal(2, table.Entries.Count);
        Assert.Equal([1], table.BankStarts);
        var a = table.Entries[0];
        var b = table.Entries[1];
        Assert.Equal(a.SectorOffset + a.SizeSectors, b.SectorOffset); // 474 starts right after 473
        Assert.False(table.Contiguous); // this excerpt does not start at sector 0, a whole archive does

        var whole = HedTable.Parse(HedBuilder.Join(HedBuilder.Hed8(0, 5000), HedBuilder.Hed8(3, 2048), HedBuilder.Hed8(4, 1), HedBuilder.Ff(16)));
        Assert.True(whole.Contiguous);
        Assert.False(HedTable.Parse(HedBuilder.Join(HedBuilder.Hed8(0, 5000), HedBuilder.Hed8(4, 2048), HedBuilder.Ff(16))).Contiguous);
        Assert.Equal(0xFFA20L, a.SectorOffset);
        Assert.Equal(0x3B5118L, a.Size);
        Assert.Equal(0x10018BL, b.SectorOffset);
        Assert.Equal(0x10018BL * 0x800, b.Offset);
        Assert.Equal(0x3710C0L, b.Size);
        Assert.Equal(0x6E3, b.SizeSectors);
        Assert.Equal(16, table.Terminator.Length);
        Assert.Equal(0, table.Trailing.Length);
        Assert.Equal("20fa00f06b071851", a.RawHex(HedFormat.Hed8));
        Assert.Equal("8b010000e306c010", b.RawHex(HedFormat.Hed8));
    }

    [Theory]
    [InlineData(0x800, 1, 0x800)]
    [InlineData(0x1000, 2, 0x1000)]            // exact multiple of the sector size (125 such HED8 entries)
    [InlineData(0x10000, 32, 0x10000)]         // exact multiple of 64 KiB
    [InlineData(0xFFF0, 32, 0xFFF0)]           // just below 64 KiB: the "- 0x10000" step of containers.md 2.3
    [InlineData(0x1FFF0, 64, 0x1FFF0)]
    [InlineData(0x11170, 35, 0x11170)]
    [InlineData(1, 1, 1)]
    public void Hed8ExactSizeRule(int size, int sectors, int expected)
    {
        var table = HedTable.Parse(HedBuilder.Join(HedBuilder.Hed8(0, size), HedBuilder.Ff(16)));
        Assert.Equal(sectors, table.Entries[0].SizeSectors);
        Assert.Equal(expected, table.Entries[0].Size);
        Assert.Equal(expected, Mzp.DecodeSize(sectors, size & 0xFFFF));
    }

    [Fact]
    public void Hed8TerminatorIsOptionalAndTrailingBytesAreKept()
    {
        var plain = HedTable.Parse(HedBuilder.Hed8(0, 100)); // bgm.hed: no terminator
        Assert.Single(plain.Entries);
        Assert.Equal(0, plain.Terminator.Length);

        var withTrailing = HedTable.Parse(HedBuilder.Join(HedBuilder.Hed8(0, 100), HedBuilder.Ff(16), HedBuilder.Hed8(1, 100)), HedFormat.Hed8);
        Assert.Single(withTrailing.Entries);
        Assert.Equal(16, withTrailing.Terminator.Length);
        Assert.Equal(8, withTrailing.Trailing.Length);
    }

    [Fact]
    public void Hed4WorkedExampleAndEntry7850()
    {
        // containers.md 3.2: voice.hed entries 0..3 and the odd record `ff ff 1b 30`.
        byte[] hed = HedBuilder.Join(
            new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01, 0x00, 0x02, 0x00, 0x2F, 0x00, 0x31, 0x00, 0x1D, 0x00 },
            HedBuilder.Ff(16));
        Assert.Equal(HedFormat.Hed4, HedTable.Detect(hed));
        var table = HedTable.Parse(hed);
        Assert.Equal(4, table.Entries.Count);
        Assert.True(table.Contiguous);
        Assert.Equal(0x31L, table.Entries[3].SectorOffset);
        Assert.Equal(0x1D, table.Entries[3].SizeSectors);
        Assert.False(table.Entries[3].SizeExact);
        Assert.Equal(0x1D * 0x800L, table.Entries[3].Size);
        Assert.Equal("31001d00", table.Entries[3].RawHex(HedFormat.Hed4));

        // `ff ff 1b 30` is sector 0x3FFFF, 27 sectors: a real entry, empty only when BOTH words are 0xFFFF
        var odd = HedTable.Parse(HedBuilder.Join(new byte[] { 0xFF, 0xFF, 0x1B, 0x30 }, HedBuilder.Ff(16)), HedFormat.Hed4);
        Assert.Single(odd.Entries);
        Assert.Equal(0x3FFFFL, odd.Entries[0].SectorOffset);
        Assert.Equal(27, odd.Entries[0].SizeSectors);
        Assert.Equal("ffff1b30", odd.Entries[0].RawHex(HedFormat.Hed4));
        Assert.Equal(16, odd.Terminator.Length);
    }

    [Fact]
    public void DetectionPicksHed8OnlyWhenTheReservedBitsAreZero()
    {
        Assert.Equal(HedFormat.Hed8, HedTable.Detect(HedBuilder.Join(HedBuilder.Hed8(5, 4000), HedBuilder.Ff(16))));
        // 4-byte records w0 = 0, w1 = 1: the u32 at an 8-byte boundary has bit 16 set
        Assert.Equal(HedFormat.Hed4, HedTable.Detect(new byte[] { 0, 0, 1, 0, 1, 0, 1, 0 }));
        Assert.Throws<ContainerFormatException>(() => HedTable.Detect(new byte[7]));
    }

    [Fact]
    public void Hed8RejectsReservedBitsAndBadSizes()
    {
        byte[] reserved = HedBuilder.Hed8Record(0, 0x0100, 1, 1); // bit 24 of the u32 set
        var e = Assert.Throws<ContainerFormatException>(() => HedTable.Parse(reserved, HedFormat.Hed8));
        Assert.Contains("reserved offset bits", e.Message);
        Assert.Throws<ContainerFormatException>(() => HedTable.Parse(new byte[12], HedFormat.Hed8));
    }

    [Fact]
    public void MoreThanElevenBankSwitchesAreRejected()
    {
        // 12 switches = 24 banks of nibble pairs: (nibble 1, nibble 0) repeated
        var parts = new List<byte[]>();
        for (int i = 0; i < 12; i++)
        {
            parts.Add(HedBuilder.Hed8(0x10000, 100));
            parts.Add(HedBuilder.Hed8(0x00000, 100));
        }

        var e = Assert.Throws<ContainerFormatException>(() => HedTable.Parse([.. parts.SelectMany(p => p)], HedFormat.Hed8));
        Assert.Contains("bank switches", e.Message);

        // 11 switches are the engine maximum and parse
        parts.RemoveRange(parts.Count - 2, 2);
        var ok = HedTable.Parse([.. parts.SelectMany(p => p)], HedFormat.Hed8);
        Assert.Equal(11, ok.BankStarts.Count);
    }

    // ---- NAM ---------------------------------------------------------------------------------

    [Fact]
    public void Nam32WithTerminatorAndTags()
    {
        byte[] data = [.. NamBuilder.Nam32Record("BLACK.MZP"), .. NamBuilder.Nam32Record("SE_A0018", "se"), .. NamBuilder.Nam32Record("LONGNAME_27_CHARS_ABCDEFGHI"), .. new byte[32], .. new byte[] { 1, 2 }.Concat(new byte[30]).ToArray()];
        Assert.True(NamTable.LooksLikeNam32(data[..96]));
        var t = NamTable.ParseNam32(data);
        Assert.Equal(32, t.RecordSize);
        Assert.Equal(["BLACK.MZP", "SE_A0018", "LONGNAME_27_CHARS_ABCDEFGHI"], t.Names);
        Assert.Equal(["", "se", ""], t.Tags);
        Assert.True(t.Terminator);
        Assert.Equal(32, t.Trailing.Length); // whatever follows the all-zero record is kept
        Assert.Equal("SE_A0018", t[1]);
    }

    [Fact]
    public void Nam32RejectsBadRecords()
    {
        byte[] noCrlf = NamBuilder.Nam32Record("X");
        noCrlf[31] = 0x20;
        Assert.Contains("CRLF", Assert.Throws<ContainerFormatException>(() => NamTable.ParseNam32(noCrlf)).Message);
        Assert.Throws<ContainerFormatException>(() => NamTable.ParseNam32(new byte[33]));
        byte[] nonAscii = NamBuilder.Nam32Record("X");
        nonAscii[0] = 0xE9;
        Assert.Throws<ContainerFormatException>(() => NamTable.ParseNam32(nonAscii));
        Assert.False(NamTable.LooksLikeNam32(noCrlf));
        Assert.False(NamTable.LooksLikeNam32([]));
    }

    [Fact]
    public void Nam8IsDetectedAndKeepsItsTwoByteTail()
    {
        // voice.nam: 8-byte records, no terminator record, 2 zero bytes at the end (containers.md 7.2)
        byte[] data = NamBuilder.Nam8("DU", "DU", "A00_", "A00_", "A00_");
        Assert.Equal(5 * 8 + 2, data.Length);
        Assert.False(NamTable.LooksLikeNam32(data));
        var t = NamTable.Parse(data);
        Assert.Equal(8, t.RecordSize);
        Assert.Equal(["DU", "DU", "A00_", "A00_", "A00_"], t.Names);
        Assert.False(t.Terminator);
        Assert.Equal(2, t.Trailing.Length);
        Assert.All(t.Tags, tag => Assert.Equal(string.Empty, tag));

        // voice2.nam is 2 * 8 + 2 = 18 bytes
        Assert.Equal(2, NamTable.Parse(NamBuilder.Nam8("DU", "DU")).Count);
    }

    [Fact]
    public void ParseDetectsNam32()
    {
        var t = NamTable.Parse(NamBuilder.Nam32(true, "A", "B"));
        Assert.Equal(32, t.RecordSize);
        Assert.Equal(["A", "B"], t.Names);
        Assert.True(t.Terminator);
    }
}
