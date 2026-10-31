using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tsuki.Data.Eboot;

namespace Tsuki.Tests.Data.Catalog;

/// <summary>
/// The SELF / ELF reader (<c>tools/eboot/selfelf.py</c>): a hand-built image whose answers are known by construction, and the
/// real eboot.bin against the oracle's <c>eboot/self.json</c> and <c>imports.tsv</c> (SELF segments, program headers, SCE dynamic
/// table, modules, libraries, symbols, relocations, imports and PLT stubs) and the counts of docs/formats/engine.md 2.1.
/// </summary>
public sealed class SelfElfTests
{
    // ---- synthetic -------------------------------------------------------------------------------------------

    private static SelfImage Synthetic() => SelfImage.Parse(SyntheticSelf.Build());

    [Fact]
    public void ASyntheticImageIsParsedLikeTheRetailLayout()
    {
        var self = Synthetic();
        Assert.Equal(4, self.SelfSegments.Count);
        Assert.Equal(0xA0, self.ElfOffset);
        Assert.Equal(0x77UL, self.Entry);
        Assert.Equal([false, true, true, true], self.SelfSegments.Select(s => s.Blocked));
        Assert.Equal([1, 0, 1, 3], self.SelfSegments.Select(s => s.PhdrId));
        Assert.Equal(["LOAD", "LOAD", "DYNAMIC", "SCE_DYNLIBDATA"], self.ProgramHeaders.Select(p => p.TypeName));
        Assert.Equal(["R-X", "RW-", "RW-", "R--"], self.ProgramHeaders.Select(p => p.Perms));
        Assert.Equal(0, self.Text.Index);
        Assert.Equal(1, self.DataSegment.Index);
        Assert.Null(self.Relro);
        Assert.Equal(13, self.Dynamic.Count); // the terminating (0, 0) entry is kept
        Assert.Equal(new DynamicEntry(0, 0), self.Dynamic[^1]);
    }

    [Fact]
    public void AddressesMapThroughTheProgramHeaders()
    {
        var self = Synthetic();
        Assert.Equal(SyntheticSelf.TextFile, self.VaToFile(0));
        Assert.Equal(SyntheticSelf.TextFile + 0xFF, self.VaToFile(0xFF));
        Assert.Null(self.VaToFile(0x100)); // the first byte after the text segment
        Assert.Equal(SyntheticSelf.DataFile, self.VaToFile(SyntheticSelf.DataVa));
        Assert.Equal(SyntheticSelf.DataFile + 0x3F, self.VaToFile(SyntheticSelf.DataVa + 0x3F));
        Assert.Null(self.VaToFile(SyntheticSelf.DataVa + 0x40)); // .bss has no file bytes
        Assert.Equal(self.DataSegment, self.SegmentOf(SyntheticSelf.DataVa + 0x40 + SyntheticSelf.Bss - 1));
        Assert.Null(self.SegmentOf(SyntheticSelf.DataVa + 0x40 + SyntheticSelf.Bss));
        Assert.Null(self.SegmentOf(0x500));
        Assert.Equal(SyntheticSelf.DynlibFile + 5, self.ElfToFile(SyntheticSelf.DynlibElf + 5));
        Assert.Equal(SyntheticSelf.DynlibFile, self.DynlibFile());
        Assert.Throws<EbootFormatException>(() => self.ElfToFile(0x123));
    }

    [Fact]
    public void ReadsInBssAreZeroAndReadsOfUnmappedAddressesFail()
    {
        var self = Synthetic();
        Assert.Equal(0x1122334455667788UL, self.U64(SyntheticSelf.DataVa));
        Assert.Equal(0x55667788U, self.U32(SyntheticSelf.DataVa));
        Assert.Equal(new byte[8], self.ReadVa(SyntheticSelf.DataVa + 0x48, 8));
        // a read that straddles the end of the file part: file bytes first, then zeros
        byte[] straddle = self.ReadVa(SyntheticSelf.DataVa + 0x3C, 8);
        Assert.Equal(self.Data.Span.Slice(SyntheticSelf.DataFile + 0x3C, 4).ToArray(), straddle[..4]);
        Assert.Equal(new byte[4], straddle[4..]);
        Assert.Throws<EbootFormatException>(() => self.ReadVa(0x500, 4));
        Assert.Equal([(byte)'H', (byte)'e'], self.ReadVa(0x80, 2));
    }

    [Fact]
    public void StringsStopAtTheNulOrTheLimit()
    {
        var self = Synthetic();
        Assert.Equal("Hello", Encoding.ASCII.GetString(self.CString(0x80)));
        Assert.Equal("Hel", Encoding.ASCII.GetString(self.CString(0x80, 3)));
        Assert.Empty(self.CString(SyntheticSelf.DataVa + 0x48)); // .bss
        Assert.Empty(self.CString(0x500));
        Assert.Equal("data", Encoding.ASCII.GetString(self.CString(SyntheticSelf.DataVa + 0x20)));
        Assert.Equal(SyntheticSelf.TextSize, self.SegmentBytes(self.Text).Length);
    }

    [Fact]
    public void TheDynamicTableGivesModulesLibrariesSymbolsAndImports()
    {
        var self = Synthetic();
        Assert.Equal([new ModuleRef(2, "modbar", 3, 4)], self.Modules());
        Assert.Equal([new LibraryRef(1, "libfoo", 5, 0x0102)], self.Libraries());
        Assert.Equal(("devmod", 0x99UL), self.ModuleInfo());
        var syms = self.Symbols();
        Assert.Equal(["", "AAAAAAAAAAA#B#C", "BBBBBBBBBBB#B#D"], syms.Select(s => s.Name));
        Assert.Equal([2, 1], syms.Skip(1).Select(s => s.Type));
        Assert.Equal([1, 1], syms.Skip(1).Select(s => s.Bind));
        Assert.Single(self.JmpRel());
        Assert.Equal(new Relocation(SyntheticSelf.Got, 1, 7, 0), self.JmpRel()[0]);
        Assert.Equal(
            [new Relocation(0x1020, 0, 8, 0x1234), new Relocation(0x1018, 2, 6, 0), new Relocation(0x1028, 1, 1, 7)],
            self.Rela());

        // the PLT relocations first, then the symbol relocations of RELA (the RELATIVE one has symbol 0 and is skipped)
        var imports = self.Imports();
        Assert.Equal([(1, SyntheticSelf.Got, 7u), (2, 0x1018UL, 6u), (1, 0x1028UL, 1u)], imports.Select(i => (i.SymIndex, i.GotVa, i.RelocType)));
        Assert.Equal(["AAAAAAAAAAA", "BBBBBBBBBBB", "AAAAAAAAAAA"], imports.Select(i => i.Nid));
        Assert.Equal(["libfoo", "libfoo", "libfoo"], imports.Select(i => i.Library));
        Assert.Equal(["modbar", "mod#3", "modbar"], imports.Select(i => i.Module));
        Assert.Equal([2, 3, 2], imports.Select(i => i.ModId));
        Assert.Equal([2, 1, 2], imports.Select(i => i.SymType));
    }

    [Fact]
    public void ThePltStubOfAJumpSlotImportIsFoundOnce()
    {
        var self = Synthetic();
        var imports = self.Imports();
        Assert.Equal(1, self.FindPlt(imports));
        Assert.Equal(SyntheticSelf.PltStub, imports[0].PltVa);
        Assert.Null(imports[1].PltVa);
        Assert.Null(imports[2].PltVa);
        // a second pass finds nothing new: a stub is assigned once
        Assert.Equal(0, self.FindPlt(imports));
    }

    [Fact]
    public void ImportIdsDecodeInBase64DigitsWithTheSceAlphabet()
    {
        Assert.Equal(0, SelfImage.DecodeId("A"));
        Assert.Equal(20, SelfImage.DecodeId("U"));
        Assert.Equal(25, SelfImage.DecodeId("Z"));
        Assert.Equal(26, SelfImage.DecodeId("a"));
        Assert.Equal(62, SelfImage.DecodeId("+"));
        Assert.Equal(63, SelfImage.DecodeId("-"));
        Assert.Equal(64, SelfImage.DecodeId("BA"));
        Assert.Equal((63 * 64) + 63, SelfImage.DecodeId("--"));
        Assert.Throws<ArgumentException>(() => SelfImage.DecodeId("A!"));
    }

    [Fact]
    public void MalformedFilesAreRejectedWithTheRuleThatFailed()
    {
        byte[] good = SyntheticSelf.Build();
        byte[] Copy() => (byte[])good.Clone();

        var badMagic = Copy();
        badMagic[0] ^= 0xFF;
        Assert.Contains("not a SELF", Assert.Throws<EbootFormatException>(() => SelfImage.Parse(badMagic)).Message, StringComparison.Ordinal);
        Assert.Throws<EbootFormatException>(() => SelfImage.Parse(good.AsMemory(0, 0x10)));
        Assert.Throws<EbootFormatException>(() => SelfImage.Parse(ReadOnlyMemory<byte>.Empty));

        var tooManySegments = Copy();
        tooManySegments[0x18] = 0xFF;
        Assert.Contains("segment table", Assert.Throws<EbootFormatException>(() => SelfImage.Parse(tooManySegments)).Message, StringComparison.Ordinal);

        var noElf = Copy();
        noElf[0xA1] = (byte)'X';
        Assert.Contains("no ELF header", Assert.Throws<EbootFormatException>(() => SelfImage.Parse(noElf)).Message, StringComparison.Ordinal);

        var deflated = Copy();
        deflated[0x20 + 0x20] |= 0x08; // segment 1: blocked and deflated
        Assert.Contains("compressed or encrypted", Assert.Throws<EbootFormatException>(() => SelfImage.Parse(deflated)).Message, StringComparison.Ordinal);

        var encrypted = Copy();
        encrypted[0x20 + 0x40] |= 0x02; // segment 2
        Assert.Throws<EbootFormatException>(() => SelfImage.Parse(encrypted));

        // an info segment (not blocked) may be flagged anything
        var info = Copy();
        info[0x20] |= 0x0A;
        Assert.Equal(4, SelfImage.Parse(info).SelfSegments.Count);

        Assert.Contains("program header", Assert.Throws<EbootFormatException>(() => SelfImage.Parse(good.AsMemory(0, 0xE0 + 100))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADynamicTagMustAppearExactlyOnce()
    {
        var self = Synthetic();
        Assert.Throws<EbootFormatException>(() => self.Dyn1(0x61000025)); // SCE_HASH: absent
        Assert.Equal(SyntheticSelf.DynlibFile, self.DynlibFile());
        Assert.Equal(0x61000035UL, self.Dynamic[0].Tag);
        Assert.Equal("SCE_STRTAB", SelfImage.DynamicTagName(0x61000035));
        Assert.Equal("0x1234", SelfImage.DynamicTagName(0x1234));
        Assert.Equal("0x42", SelfImage.ProgramHeaderTypeName(0x42));
        Assert.Equal("SCE_RELRO", SelfImage.ProgramHeaderTypeName(0x61000010));
    }

    [Fact]
    public void AFileWithoutDynamicDataCannotAnswerDynamicQueries()
    {
        byte[] file = SyntheticSelf.Build();
        file[0xE0 + (2 * 56)] = 0x07; // phdr 2: PT_DYNAMIC becomes PT_TLS
        var self = SelfImage.Parse(file);
        Assert.Empty(self.Dynamic);
        Assert.Empty(self.Modules());
        Assert.Throws<EbootFormatException>(() => self.Symbols());
        Assert.Throws<EbootFormatException>(() => self.ModuleInfo());
    }

    // ---- real data --------------------------------------------------------------------------------------------

    [GameDataFact]
    public void TheSelfSegmentsAndProgramHeadersEqualTheOracle()
    {
        var self = SharedEboot.Reader.Image;
        using var doc = CatalogGolden.Json("eboot/self.json");
        var root = doc.RootElement;
        Assert.Equal(root.GetProperty("file_size").GetInt64(), self.Data.Length);
        Assert.Equal(root.GetProperty("elf_off").GetInt32(), self.ElfOffset);
        Assert.Equal(CatalogGolden.U64(root.GetProperty("entry")), self.Entry);

        var segs = root.GetProperty("self_segments").EnumerateArray().ToArray();
        Assert.Equal(segs.Length, self.SelfSegments.Count);
        for (int i = 0; i < segs.Length; i++)
        {
            var s = self.SelfSegments[i];
            Assert.Equal(CatalogGolden.U64(segs[i].GetProperty("flags")), s.Flags);
            Assert.Equal(CatalogGolden.U64(segs[i].GetProperty("offset")), s.Offset);
            Assert.Equal(CatalogGolden.U64(segs[i].GetProperty("comp_size")), s.CompSize);
            Assert.Equal(CatalogGolden.U64(segs[i].GetProperty("dec_size")), s.DecSize);
            Assert.Equal(segs[i].GetProperty("phdr_id").GetInt32(), s.PhdrId);
            Assert.Equal(segs[i].GetProperty("blocked").GetBoolean(), s.Blocked);
            Assert.Equal(segs[i].GetProperty("deflated").GetBoolean(), s.Deflated);
            Assert.Equal(segs[i].GetProperty("encrypted").GetBoolean(), s.Encrypted);
        }

        var phdrs = root.GetProperty("phdrs").EnumerateArray().ToArray();
        Assert.Equal(phdrs.Length, self.ProgramHeaders.Count);
        for (int i = 0; i < phdrs.Length; i++)
        {
            var p = self.ProgramHeaders[i];
            var g = phdrs[i];
            Assert.Equal(g.GetProperty("type").GetUInt32(), p.Type);
            Assert.Equal(g.GetProperty("type_name").GetString(), p.TypeName);
            Assert.Equal(g.GetProperty("flags").GetUInt32(), p.Flags);
            Assert.Equal(g.GetProperty("perms").GetString(), p.Perms);
            Assert.Equal(CatalogGolden.U64(g.GetProperty("offset")), p.Offset);
            Assert.Equal(CatalogGolden.U64(g.GetProperty("vaddr")), p.VAddr);
            Assert.Equal(CatalogGolden.U64(g.GetProperty("paddr")), p.PAddr);
            Assert.Equal(CatalogGolden.U64(g.GetProperty("filesz")), p.FileSize);
            Assert.Equal(CatalogGolden.U64(g.GetProperty("memsz")), p.MemSize);
            Assert.Equal(CatalogGolden.U64(g.GetProperty("align")), p.Align);
            var fo = g.GetProperty("file_offset");
            Assert.Equal(OptLong(fo), self.PhdrFileOffset(p));
        }

        Assert.Equal(root.GetProperty("text_index").GetInt32(), self.Text.Index);
        Assert.Equal(root.GetProperty("data_index").GetInt32(), self.DataSegment.Index);
        Assert.Equal(root.GetProperty("relro_index").GetInt32(), self.Relro!.Index);
    }

    [GameDataFact]
    public void TheDynamicTableModulesLibrariesAndModuleInfoEqualTheOracle()
    {
        var self = SharedEboot.Reader.Image;
        using var doc = CatalogGolden.Json("eboot/self.json");
        var root = doc.RootElement;
        var dyn = root.GetProperty("dynamic").EnumerateArray().ToArray();
        Assert.Equal(dyn.Length, self.Dynamic.Count);
        for (int i = 0; i < dyn.Length; i++)
        {
            Assert.Equal(CatalogGolden.U64(dyn[i][0]), self.Dynamic[i].Tag);
            Assert.Equal(CatalogGolden.U64(dyn[i][1]), self.Dynamic[i].Value);
        }

        var mods = root.GetProperty("modules").EnumerateArray().Select(m => new ModuleRef(
            m.GetProperty("id").GetInt32(), m.GetProperty("name").GetString()!, m.GetProperty("major").GetInt32(), m.GetProperty("minor").GetInt32()));
        Assert.Equal(mods, self.Modules());
        var libs = root.GetProperty("libraries").EnumerateArray().Select(l => new LibraryRef(
            l.GetProperty("id").GetInt32(), l.GetProperty("name").GetString()!, l.GetProperty("version").GetInt32(),
            l.GetProperty("attr").ValueKind == JsonValueKind.Null ? null : l.GetProperty("attr").GetUInt64()));
        Assert.Equal(libs, self.Libraries());
        var info = root.GetProperty("module_info");
        Assert.Equal((info[0].GetString()!, CatalogGolden.U64(info[1])), self.ModuleInfo());

        foreach (var kv in root.GetProperty("decode_id").EnumerateObject())
        {
            Assert.Equal(kv.Value.GetInt32(), SelfImage.DecodeId(kv.Name));
        }
    }

    [GameDataFact]
    public void SymbolsAndRelocationsEqualTheOracleByDigest()
    {
        var self = SharedEboot.Reader.Image;
        using var doc = CatalogGolden.Json("eboot/self.json");
        var root = doc.RootElement;
        var syms = self.Symbols();
        var symLines = syms.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Index}|{s.Name}|{s.Info}|{s.Other}|{s.Shndx}|{s.Value}|{s.Size}")).ToArray();
        Assert.Equal(root.GetProperty("symbols").GetProperty("count").GetInt32(), syms.Count);
        Assert.Equal(root.GetProperty("symbols").GetProperty("sha1").GetString(), Digest(symLines));
        Assert.Equal(root.GetProperty("symbols").GetProperty("first").EnumerateArray().Select(e => e.GetString()), symLines.Take(3));
        Assert.Equal(root.GetProperty("symbols").GetProperty("last").EnumerateArray().Select(e => e.GetString()), symLines.TakeLast(2));

        string Rel(Relocation r) => string.Create(CultureInfo.InvariantCulture, $"{r.Offset}|{r.Sym}|{r.Type}|{r.Addend}");
        var jmp = self.JmpRel();
        var rela = self.Rela();
        Assert.Equal(root.GetProperty("jmprel").GetProperty("count").GetInt32(), jmp.Count);
        Assert.Equal(root.GetProperty("jmprel").GetProperty("sha1").GetString(), Digest(jmp.Select(Rel)));
        Assert.Equal(root.GetProperty("rela").GetProperty("count").GetInt32(), rela.Count);
        Assert.Equal(root.GetProperty("rela").GetProperty("sha1").GetString(), Digest(rela.Select(Rel)));
        foreach (var kv in root.GetProperty("rela").GetProperty("by_type").EnumerateObject())
        {
            Assert.Equal(kv.Value.GetInt32(), rela.Count(r => r.Type == uint.Parse(kv.Name, CultureInfo.InvariantCulture)));
        }
    }

    [GameDataFact]
    public void TheCountsOfEngineMd21AreReproduced()
    {
        var self = SharedEboot.Reader.Image;
        Assert.Equal(10, self.SelfSegments.Count);
        Assert.All(self.SelfSegments, s => Assert.False(s.Deflated || s.Encrypted));
        Assert.Equal(339, self.Symbols().Count);
        Assert.Equal(334, self.JmpRel().Count);
        var rela = self.Rela();
        Assert.Equal(39_836, rela.Count);
        Assert.Equal(39_459, rela.Count(r => r.Type == SelfImage.RX86Relative));
        Assert.Equal(377, rela.Count(r => r.Type == SelfImage.RX8664));
        Assert.Equal(27, self.Modules().Count);
        Assert.Equal(27, self.Libraries().Count);
        var imports = self.Imports();
        Assert.Equal(337, imports.Select(i => i.Nid).Distinct().Count());
        Assert.Equal(334, self.FindPlt(imports));
        Assert.Equal(0xEF390UL, self.Entry);
        Assert.Equal(0x160, self.ElfOffset);
    }

    [GameDataFact]
    public void ImportsAndPltStubsEqualTheOracle()
    {
        var self = SharedEboot.Reader.Image;
        var imports = self.Imports();
        self.FindPlt(imports);
        var rows = CatalogGolden.Tsv("eboot/imports.tsv", out string[] header);
        Assert.Equal(["sym_index", "nid", "library", "module", "lib_id", "mod_id", "sym_type", "got_va", "reloc_type", "plt_va"], header);
        Assert.Equal(rows.Count, imports.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var imp = imports[i];
            string ctx = $"import {i}";
            Assert.True(imp.SymIndex.ToString(CultureInfo.InvariantCulture) == r[0], ctx);
            Assert.True(imp.Nid == r[1], ctx);
            Assert.True(imp.Library == r[2], ctx);
            Assert.True(imp.Module == r[3], ctx);
            Assert.True(imp.LibId.ToString(CultureInfo.InvariantCulture) == r[4], ctx);
            Assert.True(imp.ModId.ToString(CultureInfo.InvariantCulture) == r[5], ctx);
            Assert.True(imp.SymType.ToString(CultureInfo.InvariantCulture) == r[6], ctx);
            Assert.True(imp.GotVa.ToString(CultureInfo.InvariantCulture) == r[7], ctx);
            Assert.True(imp.RelocType.ToString(CultureInfo.InvariantCulture) == r[8], ctx);
            Assert.True((imp.PltVa?.ToString(CultureInfo.InvariantCulture) ?? "-") == r[9], ctx);
        }
    }

    [GameDataFact]
    public void VaMappingReadsAndStringsEqualTheOracle()
    {
        var self = SharedEboot.Reader.Image;
        using var doc = CatalogGolden.Json("eboot/self.json");
        var root = doc.RootElement;
        foreach (var p in root.GetProperty("va_probes").EnumerateArray())
        {
            ulong va = p.GetProperty("va").GetUInt64();
            var file = p.GetProperty("file");
            Assert.Equal(OptLong(file), self.VaToFile(va));
            Assert.Equal(OptLong(p.GetProperty("segment")), self.SegmentOf(va)?.Index);
        }

        foreach (var r in root.GetProperty("reads").EnumerateArray())
        {
            Assert.Equal(r.GetProperty("hex").GetString(), Convert.ToHexStringLower(self.ReadVa(r.GetProperty("va").GetUInt64(), r.GetProperty("n").GetInt32())));
        }

        foreach (var c in root.GetProperty("cstrs").EnumerateArray())
        {
            Assert.Equal(c.GetProperty("hex").GetString(), Convert.ToHexStringLower(self.CString(c.GetProperty("va").GetUInt64(), c.GetProperty("limit").GetInt32())));
        }

        // the three fixed segments of catalog_eboot.py are the file-backed program headers
        Assert.Equal(
            [(0x000000L, 0x002280L, 0x329DC8L), (0x400000L, 0x32C210L, 0x035ED0L), (0x600000L, 0x365FA0L, 0x7D66E0L)],
            new[] { self.Text, self.Relro!, self.DataSegment }.Select(p => ((long)p.VAddr, self.PhdrFileOffset(p)!.Value, (long)p.FileSize)));
        Assert.Equal(EbootAddresses.TextSize, (long)SharedEboot.Reader.TextEnd);
    }

    [GameDataFact]
    public void TheReaderIsStrictAboutBssAndUnmappedAddresses()
    {
        var reader = SharedEboot.Reader;
        Assert.Throws<EbootFormatException>(() => reader.Read(0xDD66E0, 4)); // first .bss byte
        Assert.Throws<EbootFormatException>(() => reader.Read(0x329DC8, 4)); // just after the text segment
        Assert.Throws<EbootFormatException>(() => reader.Read(0x329DC4, 8)); // straddles the end of the text segment
        Assert.Equal(4, reader.Read(0x329DC4, 4).Length);
        Assert.Throws<EbootFormatException>(() => reader.Read(-1, 4));
        Assert.Equal("Hunex Consumer Library", reader.CString(EbootAddresses.HunexString));
        Assert.Equal(SharedEboot.TextFile(EbootAddresses.HunexString), reader.FileOffset(EbootAddresses.HunexString));
        Assert.Null(reader.FileOffset(0xDD66E0));
    }

    [GameDataFact]
    public void TheWholeRealImageParsesFast()
    {
        // warm up, then time a parse + the dynamic tables (RR 3.3 asks for a start-up that is not noticeable)
        var data = SharedEboot.Data;
        _ = EbootReader.Parse(data).Image.Symbols();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var image = EbootReader.Parse(data).Image;
        var imports = image.Imports();
        image.FindPlt(imports);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");
    }

    private static long? OptLong(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt64();

    private static string Digest(IEnumerable<string> lines)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (string l in lines)
        {
            h.AppendData(Encoding.UTF8.GetBytes(l));
            h.AppendData("\n"u8);
        }

        return Convert.ToHexStringLower(h.GetHashAndReset());
    }
}
