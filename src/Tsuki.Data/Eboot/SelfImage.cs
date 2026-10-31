using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Tsuki.Data.Containers;

namespace Tsuki.Data.Eboot;

/// <summary>eboot.bin does not follow the SELF / ELF layout of docs/formats/engine.md 1-2. The message says which rule failed. (Not sealed since package K25: <c>UnsupportedBuildException</c> derives from it.)</summary>
public class EbootFormatException : ContainerFormatException
{
    /// <summary>Creates the exception.</summary>
    public EbootFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>One entry of the SELF segment table (docs/formats/engine.md 1.2).</summary>
/// <param name="Index">Position in the table.</param>
/// <param name="Flags">Bits 20..31 = ELF program header index, 0x800 = blocked, 0x8 = deflated, 0x2 = encrypted.</param>
/// <param name="Offset">File offset of the stored bytes.</param>
/// <param name="CompSize">Stored size.</param>
/// <param name="DecSize">Size after inflating.</param>
public sealed record SelfSegment(int Index, ulong Flags, ulong Offset, ulong CompSize, ulong DecSize)
{
    /// <summary>The ELF program header this segment holds the bytes of.</summary>
    public int PhdrId => (int)((Flags >> 20) & 0xFFF);

    /// <summary>True for a segment with data (flag 0x800).</summary>
    public bool Blocked => (Flags & 0x800) != 0;

    /// <summary>True when the segment is deflated (flag 0x8).</summary>
    public bool Deflated => (Flags & 0x8) != 0;

    /// <summary>True when the segment is encrypted (flag 0x2).</summary>
    public bool Encrypted => (Flags & 0x2) != 0;
}

/// <summary>An ELF64 program header (docs/formats/engine.md 1.2).</summary>
public sealed record ProgramHeader(
    int Index, uint Type, uint Flags, ulong Offset, ulong VAddr, ulong PAddr, ulong FileSize, ulong MemSize, ulong Align)
{
    /// <summary>The name of a known type (<c>LOAD</c>, <c>SCE_RELRO</c>, ...), else the hex value.</summary>
    public string TypeName => SelfImage.ProgramHeaderTypeName(Type);

    /// <summary>Permissions as <c>RWX</c> with <c>-</c> for a missing right.</summary>
    public string Perms => string.Concat((Flags & 4) != 0 ? 'R' : '-', (Flags & 2) != 0 ? 'W' : '-', (Flags & 1) != 0 ? 'X' : '-');
}

/// <summary>One entry of the SCE dynamic table.</summary>
/// <param name="Tag">The tag (<c>DT_*</c>), as an unsigned 64-bit value.</param>
/// <param name="Value">The value.</param>
public readonly record struct DynamicEntry(ulong Tag, ulong Value);

/// <summary>An ELF64 symbol of the dynamic symbol table; imports are named <c>NID#library#module</c>.</summary>
public sealed record ElfSymbol(int Index, string Name, byte Info, byte Other, ushort Shndx, ulong Value, ulong Size)
{
    /// <summary>The binding (high nibble of <c>st_info</c>).</summary>
    public int Bind => Info >> 4;

    /// <summary>The symbol type (low nibble of <c>st_info</c>): 2 = function, 1 = object, 6 = TLS.</summary>
    public int Type => Info & 0xF;
}

/// <summary>An <c>Elf64_Rela</c> entry.</summary>
/// <param name="Offset">The VA that is patched.</param>
/// <param name="Sym">Symbol index (<c>r_info &gt;&gt; 32</c>).</param>
/// <param name="Type">Relocation type (<c>r_info &amp; 0xFFFFFFFF</c>).</param>
/// <param name="Addend">The addend.</param>
public readonly record struct Relocation(ulong Offset, int Sym, uint Type, long Addend);

/// <summary>A needed module (<c>DT_SCE_NEEDED_MODULE</c>).</summary>
public sealed record ModuleRef(int Id, string Name, int VersionMajor, int VersionMinor);

/// <summary>An imported library (<c>DT_SCE_IMPORT_LIB</c> with its attribute).</summary>
public sealed record LibraryRef(int Id, string Name, int Version, ulong? Attr);

/// <summary>One imported symbol with its GOT slot, as <c>eboot.selfelf.Import</c>.</summary>
public sealed class ImportedSymbol
{
    /// <summary>Index into the dynamic symbol table.</summary>
    public int SymIndex { get; init; }

    /// <summary>The 11-character NID.</summary>
    public string Nid { get; init; } = string.Empty;

    /// <summary>Library name, or <c>lib#N</c> when its id is unknown.</summary>
    public string Library { get; init; } = string.Empty;

    /// <summary>Module name, or <c>mod#N</c> when its id is unknown.</summary>
    public string Module { get; init; } = string.Empty;

    /// <summary>Library id.</summary>
    public int LibId { get; init; }

    /// <summary>Module id.</summary>
    public int ModId { get; init; }

    /// <summary>Symbol type: 2 = function, 1 = object, 6 = TLS.</summary>
    public int SymType { get; init; }

    /// <summary>GOT slot VA (jump slot) or data slot (GLOB_DAT / 64).</summary>
    public ulong GotVa { get; init; }

    /// <summary>Relocation type.</summary>
    public uint RelocType { get; init; }

    /// <summary>The PLT stub VA in the text segment, filled by <see cref="SelfImage.FindPlt"/>.</summary>
    public ulong? PltVa { get; set; }
}

/// <summary>
/// A SELF + ELF64 reader for the (fake-signed, plain) PS4 executable. A port of <c>tools/eboot/selfelf.py</c>
/// (docs/formats/engine.md 1-2). The class holds the whole file as given; it reads nothing else.
/// </summary>
public sealed class SelfImage
{
    /// <summary>The SELF magic at the start of the file.</summary>
    public static ReadOnlySpan<byte> SelfMagic => [0x4F, 0x15, 0x3D, 0x1D];

    /// <summary><c>R_X86_64_64</c>.</summary>
    public const uint RX8664 = 1;

    /// <summary><c>R_X86_64_GLOB_DAT</c>.</summary>
    public const uint RX86GlobDat = 6;

    /// <summary><c>R_X86_64_JUMP_SLOT</c>.</summary>
    public const uint RX86JumpSlot = 7;

    /// <summary><c>R_X86_64_RELATIVE</c>.</summary>
    public const uint RX86Relative = 8;

    private const uint PtLoad = 1;
    private const uint PtDynamic = 2;
    private const uint PtSceDynlibData = 0x61000000;
    private const uint PtSceRelro = 0x61000010;

    private const ulong DtSceStrtab = 0x61000035;
    private const ulong DtSceSymtab = 0x61000039;
    private const ulong DtSceSymtabSz = 0x6100003F;
    private const ulong DtSceJmprel = 0x61000029;
    private const ulong DtScePltrelSz = 0x6100002D;
    private const ulong DtSceRela = 0x6100002F;
    private const ulong DtSceRelaSz = 0x61000031;
    private const ulong DtSceNeededModule = 0x6100000F;
    private const ulong DtSceImportLib = 0x61000015;
    private const ulong DtSceImportLibAttr = 0x61000019;
    private const ulong DtSceModuleInfo = 0x6100000D;

    private const string IdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-";

    private static readonly Dictionary<uint, string> TypeNames = new()
    {
        [0] = "NULL", [1] = "LOAD", [2] = "DYNAMIC", [3] = "INTERP", [4] = "NOTE", [7] = "TLS",
        [0x60000000] = "SCE_RELA", [0x61000000] = "SCE_DYNLIBDATA", [0x61000001] = "SCE_PROCPARAM",
        [0x61000002] = "SCE_MODULE_PARAM", [0x61000010] = "SCE_RELRO",
        [0x6FFFFF00] = "SCE_COMMENT", [0x6FFFFF01] = "SCE_LIBVERSION", [0x6474E550] = "GNU_EH_FRAME",
    };

    private static readonly Dictionary<ulong, string> DynamicNames = new()
    {
        [0] = "NULL", [1] = "NEEDED", [5] = "STRTAB", [6] = "SYMTAB", [7] = "RELA", [8] = "RELASZ", [9] = "RELAENT",
        [10] = "STRSZ", [11] = "SYMENT", [12] = "INIT", [13] = "FINI", [14] = "SONAME", [15] = "RPATH",
        [16] = "SYMBOLIC", [20] = "PLTREL", [21] = "DEBUG", [22] = "TEXTREL", [24] = "BIND_NOW",
        [25] = "INIT_ARRAY", [26] = "FINI_ARRAY", [27] = "INIT_ARRAYSZ", [28] = "FINI_ARRAYSZ",
        [30] = "FLAGS", [32] = "PREINIT_ARRAY", [33] = "PREINIT_ARRAYSZ",
        [0x61000005] = "SCE_IDTABENTSZ", [0x61000007] = "SCE_FINGERPRINT",
        [0x61000009] = "SCE_ORIGINAL_FILENAME", [0x6100000D] = "SCE_MODULE_INFO",
        [0x6100000F] = "SCE_NEEDED_MODULE", [0x61000011] = "SCE_MODULE_ATTR",
        [0x61000013] = "SCE_EXPORT_LIB", [0x61000015] = "SCE_IMPORT_LIB",
        [0x61000017] = "SCE_EXPORT_LIB_ATTR", [0x61000019] = "SCE_IMPORT_LIB_ATTR",
        [0x6100001D] = "SCE_STUB_MODULE_NAME", [0x6100001F] = "SCE_STUB_MODULE_VERSION",
        [0x61000021] = "SCE_STUB_LIBRARY_NAME", [0x61000023] = "SCE_STUB_LIBRARY_VERSION",
        [0x61000025] = "SCE_HASH", [0x61000027] = "SCE_PLTGOT", [0x61000029] = "SCE_JMPREL",
        [0x6100002B] = "SCE_PLTREL", [0x6100002D] = "SCE_PLTRELSZ", [0x6100002F] = "SCE_RELA",
        [0x61000031] = "SCE_RELASZ", [0x61000033] = "SCE_RELAENT", [0x61000035] = "SCE_STRTAB",
        [0x61000037] = "SCE_STRSZ", [0x61000039] = "SCE_SYMTAB", [0x6100003B] = "SCE_SYMENT",
        [0x6100003D] = "SCE_HASHSZ", [0x6100003F] = "SCE_SYMTABSZ",
    };

    private readonly ReadOnlyMemory<byte> _data;
    private readonly SelfSegment[] _segments;
    private readonly ProgramHeader[] _phdrs;
    private DynamicEntry[] _dynamic = [];

    private SelfImage(ReadOnlyMemory<byte> data, SelfSegment[] segments, int elfOffset, ulong entry, ProgramHeader[] phdrs)
    {
        _data = data;
        _segments = segments;
        ElfOffset = elfOffset;
        Entry = entry;
        _phdrs = phdrs;
    }

    /// <summary>The SELF segment table.</summary>
    public IReadOnlyList<SelfSegment> SelfSegments => _segments;

    /// <summary>File offset of the embedded ELF header (<c>0x20 + 0x20 * segments</c>; 0x160 in the retail file).</summary>
    public int ElfOffset { get; }

    /// <summary>The ELF entry point VA.</summary>
    public ulong Entry { get; }

    /// <summary>The ELF program headers.</summary>
    public IReadOnlyList<ProgramHeader> ProgramHeaders => _phdrs;

    /// <summary>The entries of PT_DYNAMIC up to and including the terminating tag 0.</summary>
    public IReadOnlyList<DynamicEntry> Dynamic => _dynamic;

    /// <summary>The file bytes.</summary>
    public ReadOnlyMemory<byte> Data => _data;

    /// <summary>The name of a program header type, or its hex value.</summary>
    public static string ProgramHeaderTypeName(uint type) =>
        TypeNames.TryGetValue(type, out string? n) ? n : "0x" + type.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>The name of a dynamic tag, or its hex value.</summary>
    public static string DynamicTagName(ulong tag) =>
        DynamicNames.TryGetValue(tag, out string? n) ? n : "0x" + tag.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>Decodes the library or module id of an import name (base 64 digits, most significant first).</summary>
    /// <exception cref="ArgumentException">A character outside the alphabet.</exception>
    public static int DecodeId(string s)
    {
        long v = 0;
        foreach (char ch in s)
        {
            int i = IdAlphabet.IndexOf(ch, StringComparison.Ordinal);
            if (i < 0)
            {
                throw new ArgumentException($"bad id character '{ch}' in \"{s}\"", nameof(s));
            }

            v = (v * 64) + i;
        }

        return checked((int)v);
    }

    /// <summary>Parses a SELF file: the segment table, the ELF header and program headers and the dynamic table.</summary>
    /// <param name="data">The whole file; it must stay unchanged while the image is in use.</param>
    /// <exception cref="EbootFormatException">The file is not a plain SELF with an ELF64 image.</exception>
    public static SelfImage Parse(ReadOnlyMemory<byte> data)
    {
        var m = data.Span;
        if (m.Length < 0x20 || !m[..4].SequenceEqual(SelfMagic))
        {
            string found = Convert.ToHexString(m[..Math.Min(4, m.Length)]);
            throw new EbootFormatException($"not a SELF (magic {found})");
        }

        int nseg = BinaryPrimitives.ReadUInt16LittleEndian(m[0x18..]);
        if (0x20 + (nseg * 0x20) > m.Length)
        {
            throw new EbootFormatException($"SELF segment table of {nseg} entries runs past the end of the file");
        }

        var segs = new SelfSegment[nseg];
        for (int i = 0; i < nseg; i++)
        {
            var e = m.Slice(0x20 + (i * 0x20), 0x20);
            segs[i] = new SelfSegment(
                i, BinaryPrimitives.ReadUInt64LittleEndian(e), BinaryPrimitives.ReadUInt64LittleEndian(e[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(e[16..]), BinaryPrimitives.ReadUInt64LittleEndian(e[24..]));
        }

        int elfOff = 0x20 + (nseg * 0x20);
        ReadOnlySpan<byte> elfMagic = [0x7F, (byte)'E', (byte)'L', (byte)'F'];
        if (elfOff + 0x40 > m.Length || !m.Slice(elfOff, 4).SequenceEqual(elfMagic))
        {
            throw new EbootFormatException($"no ELF header at 0x{elfOff:x}");
        }

        var h = m[(elfOff + 0x10)..];
        ulong entry = BinaryPrimitives.ReadUInt64LittleEndian(h[8..]);
        ulong phoff = BinaryPrimitives.ReadUInt64LittleEndian(h[16..]);
        int phentsize = BinaryPrimitives.ReadUInt16LittleEndian(h[38..]);
        int phnum = BinaryPrimitives.ReadUInt16LittleEndian(h[40..]);
        if (phentsize < 56 && phnum > 0)
        {
            throw new EbootFormatException($"ELF program header size {phentsize} is smaller than 56");
        }

        var phdrs = new ProgramHeader[phnum];
        for (int i = 0; i < phnum; i++)
        {
            ulong at = (ulong)elfOff + phoff + ((ulong)i * (ulong)phentsize);
            if (at + 56 > (ulong)m.Length)
            {
                throw new EbootFormatException($"ELF program header {i} at 0x{at:x} runs past the end of the file");
            }

            var p = m[(int)at..];
            phdrs[i] = new ProgramHeader(
                i, BinaryPrimitives.ReadUInt32LittleEndian(p), BinaryPrimitives.ReadUInt32LittleEndian(p[4..]),
                BinaryPrimitives.ReadUInt64LittleEndian(p[8..]), BinaryPrimitives.ReadUInt64LittleEndian(p[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(p[24..]), BinaryPrimitives.ReadUInt64LittleEndian(p[32..]),
                BinaryPrimitives.ReadUInt64LittleEndian(p[40..]), BinaryPrimitives.ReadUInt64LittleEndian(p[48..]));
        }

        foreach (var s in segs)
        {
            if (s.Blocked && (s.Deflated || s.Encrypted))
            {
                throw new EbootFormatException($"segment {s.Index} is compressed or encrypted; a decrypted SELF is required");
            }
        }

        var image = new SelfImage(data, segs, elfOff, entry, phdrs);
        image._dynamic = image.ParseDynamic();
        return image;
    }

    // ------------------------------------------------------------------ mapping

    /// <summary>SELF file offset where the file bytes of <paramref name="ph"/> start, or null if no blocked segment holds them.</summary>
    public long? PhdrFileOffset(ProgramHeader ph)
    {
        foreach (var s in _segments)
        {
            if (s.Blocked && s.PhdrId == ph.Index)
            {
                return checked((long)s.Offset);
            }
        }

        return null;
    }

    /// <summary>Maps an ELF file offset to a SELF file offset.</summary>
    /// <exception cref="EbootFormatException">No program header with SELF data contains the offset.</exception>
    public long ElfToFile(ulong elfOffset)
    {
        foreach (var ph in _phdrs)
        {
            if (ph.FileSize != 0 && ph.Offset <= elfOffset && elfOffset < ph.Offset + ph.FileSize
                && PhdrFileOffset(ph) is { } baseOffset)
            {
                return baseOffset + (long)(elfOffset - ph.Offset);
            }
        }

        throw new EbootFormatException($"ELF offset 0x{elfOffset:x} is not backed by a SELF segment");
    }

    /// <summary>The loaded segments: type LOAD and SCE_RELRO.</summary>
    public IEnumerable<ProgramHeader> LoadSegments() => _phdrs.Where(p => p.Type is PtLoad or PtSceRelro);

    /// <summary>The first LOAD segment with the execute flag (text and read-only data).</summary>
    public ProgramHeader Text => _phdrs.First(p => p.Type == PtLoad && (p.Flags & 1) != 0);

    /// <summary>The first LOAD segment with the write flag (data and bss).</summary>
    public ProgramHeader DataSegment => _phdrs.First(p => p.Type == PtLoad && (p.Flags & 2) != 0);

    /// <summary>The SCE_RELRO segment, or null.</summary>
    public ProgramHeader? Relro => _phdrs.FirstOrDefault(p => p.Type == PtSceRelro);

    /// <summary>
    /// The SELF file offset of a VA inside the file image of a loaded segment, or null for a VA in .bss or outside
    /// every segment.
    /// </summary>
    public long? VaToFile(ulong va)
    {
        foreach (var ph in LoadSegments())
        {
            if (ph.VAddr <= va && va < ph.VAddr + ph.FileSize)
            {
                return PhdrFileOffset(ph) is { } b ? b + (long)(va - ph.VAddr) : null;
            }
        }

        return null;
    }

    /// <summary>The loaded segment whose memory image (file part and bss) contains <paramref name="va"/>, or null.</summary>
    public ProgramHeader? SegmentOf(ulong va)
    {
        foreach (var ph in LoadSegments())
        {
            if (ph.VAddr <= va && va < ph.VAddr + ph.MemSize)
            {
                return ph;
            }
        }

        return null;
    }

    /// <summary>Reads <paramref name="n"/> bytes at a VA; bytes in .bss (beyond the segment's file size) read as zero.</summary>
    /// <exception cref="EbootFormatException">The VA is not mapped.</exception>
    public byte[] ReadVa(ulong va, int n)
    {
        var ph = SegmentOf(va) ?? throw new EbootFormatException($"VA 0x{va:x} not mapped");
        byte[] outBytes = new byte[n];
        ulong fileEnd = ph.VAddr + ph.FileSize;
        int avail = va >= fileEnd ? 0 : (int)Math.Min((ulong)n, fileEnd - va);
        if (avail > 0)
        {
            long fo = VaToFile(va) ?? throw new EbootFormatException($"VA 0x{va:x} has no file bytes");
            Slice(fo, avail).CopyTo(outBytes);
        }

        return outBytes;
    }

    /// <summary>Reads a little-endian u64 at a VA.</summary>
    public ulong U64(ulong va) => BinaryPrimitives.ReadUInt64LittleEndian(ReadVa(va, 8));

    /// <summary>Reads a little-endian u32 at a VA.</summary>
    public uint U32(ulong va) => BinaryPrimitives.ReadUInt32LittleEndian(ReadVa(va, 4));

    /// <summary>
    /// The bytes of the NUL-terminated string at a VA (at most <paramref name="limit"/> bytes, without the NUL);
    /// empty if the VA has no file bytes (.bss or unmapped).
    /// </summary>
    public byte[] CString(ulong va, int limit = 4096)
    {
        if (VaToFile(va) is not { } fo)
        {
            return [];
        }

        var all = _data.Span;
        int start = checked((int)fo);
        int count = Math.Min(limit, all.Length - start);
        var window = all.Slice(start, Math.Max(count, 0));
        int nul = window.IndexOf((byte)0);
        return window[..(nul >= 0 ? nul : window.Length)].ToArray();
    }

    /// <summary>The file bytes of a loaded segment.</summary>
    public ReadOnlySpan<byte> SegmentBytes(ProgramHeader ph)
    {
        long b = PhdrFileOffset(ph) ?? throw new EbootFormatException($"phdr {ph.Index} has no SELF data");
        return Slice(b, checked((int)ph.FileSize));
    }

    // ------------------------------------------------------------ dynamic

    /// <summary>Values of every dynamic entry with <paramref name="tag"/>.</summary>
    public IEnumerable<ulong> Dyn(ulong tag) => _dynamic.Where(e => e.Tag == tag).Select(e => e.Value);

    /// <summary>The value of the only dynamic entry with <paramref name="tag"/>.</summary>
    /// <exception cref="EbootFormatException">There is no such entry, or more than one.</exception>
    public ulong Dyn1(ulong tag)
    {
        var v = Dyn(tag).ToArray();
        return v.Length == 1
            ? v[0]
            : throw new EbootFormatException($"dynamic tag {DynamicTagName(tag)}: {v.Length} entries");
    }

    /// <summary>The SELF file offset of PT_SCE_DYNLIBDATA (strings, symbols, relocations).</summary>
    public long DynlibFile()
    {
        var ph = _phdrs.FirstOrDefault(p => p.Type == PtSceDynlibData)
            ?? throw new EbootFormatException("no SCE_DYNLIBDATA");
        return PhdrFileOffset(ph) ?? ElfToFile(ph.Offset);
    }

    /// <summary>A string of the dynamic string table, as ASCII (other bytes become U+FFFD).</summary>
    public string DynStr(ulong offset)
    {
        long start = DynlibFile() + (long)Dyn1(DtSceStrtab) + (long)offset;
        var all = _data.Span;
        if (start < 0 || start > all.Length)
        {
            throw new EbootFormatException($"dynamic string at 0x{start:x} is outside the file");
        }

        var rest = all[(int)start..];
        int nul = rest.IndexOf((byte)0);
        var s = rest[..(nul >= 0 ? nul : rest.Length)];
        var sb = new StringBuilder(s.Length);
        foreach (byte b in s)
        {
            sb.Append(b < 0x80 ? (char)b : '�');
        }

        return sb.ToString();
    }

    /// <summary>The needed modules by id (<c>DT_SCE_NEEDED_MODULE</c>), in table order.</summary>
    public IReadOnlyList<ModuleRef> Modules()
    {
        var byId = new Dictionary<int, int>();
        var list = new List<ModuleRef>();
        foreach (ulong v in Dyn(DtSceNeededModule))
        {
            int id = (int)((v >> 48) & 0xFFFF);
            var m = new ModuleRef(id, DynStr(v & 0xFFFFFFFF), (int)((v >> 40) & 0xFF), (int)((v >> 32) & 0xFF));
            if (byId.TryGetValue(id, out int at))
            {
                list[at] = m;
            }
            else
            {
                byId[id] = list.Count;
                list.Add(m);
            }
        }

        return list;
    }

    /// <summary>The imported libraries by id (<c>DT_SCE_IMPORT_LIB</c> with <c>DT_SCE_IMPORT_LIB_ATTR</c>), in table order.</summary>
    public IReadOnlyList<LibraryRef> Libraries()
    {
        var byId = new Dictionary<int, int>();
        var list = new List<LibraryRef>();
        foreach (ulong v in Dyn(DtSceImportLib))
        {
            int id = (int)((v >> 48) & 0xFFFF);
            var lib = new LibraryRef(id, DynStr(v & 0xFFFFFFFF), (int)((v >> 32) & 0xFFFF), null);
            if (byId.TryGetValue(id, out int at))
            {
                list[at] = lib;
            }
            else
            {
                byId[id] = list.Count;
                list.Add(lib);
            }
        }

        foreach (ulong v in Dyn(DtSceImportLibAttr))
        {
            int id = (int)((v >> 48) & 0xFFFF);
            if (byId.TryGetValue(id, out int at))
            {
                list[at] = list[at] with { Attr = v & 0xFFFFFFFFFFFF };
            }
        }

        return list;
    }

    /// <summary>The module name and the version word (<c>DT_SCE_MODULE_INFO</c>).</summary>
    public (string Name, ulong Version) ModuleInfo()
    {
        ulong v = Dyn1(DtSceModuleInfo);
        return (DynStr(v & 0xFFFFFFFF), v >> 32);
    }

    /// <summary>The dynamic symbol table.</summary>
    public IReadOnlyList<ElfSymbol> Symbols()
    {
        long baseOffset = DynlibFile() + (long)Dyn1(DtSceSymtab);
        int n = checked((int)(Dyn1(DtSceSymtabSz) / 24));
        var table = Slice(baseOffset, checked(n * 24));
        var list = new List<ElfSymbol>(n);
        for (int i = 0; i < n; i++)
        {
            var e = table.Slice(i * 24, 24);
            uint stName = BinaryPrimitives.ReadUInt32LittleEndian(e);
            list.Add(new ElfSymbol(
                i, DynStr(stName), e[4], e[5], BinaryPrimitives.ReadUInt16LittleEndian(e[6..]),
                BinaryPrimitives.ReadUInt64LittleEndian(e[8..]), BinaryPrimitives.ReadUInt64LittleEndian(e[16..])));
        }

        return list;
    }

    /// <summary>The PLT relocations (<c>DT_SCE_JMPREL</c>).</summary>
    public IReadOnlyList<Relocation> JmpRel() => Relas(DtSceJmprel, DtScePltrelSz);

    /// <summary>The relocations of <c>DT_SCE_RELA</c> (39,836 in the retail file).</summary>
    public IReadOnlyList<Relocation> Rela() => Relas(DtSceRela, DtSceRelaSz);

    /// <summary>
    /// All imported symbols with their GOT slot: first the PLT relocations (functions), then the symbol relocations
    /// of <c>DT_SCE_RELA</c> (data imports).
    /// </summary>
    public IReadOnlyList<ImportedSymbol> Imports()
    {
        var syms = Symbols();
        var libs = Libraries().ToDictionary(l => l.Id);
        var mods = Modules().ToDictionary(m => m.Id);
        var seen = new HashSet<(int, ulong)>();
        var list = new List<ImportedSymbol>();
        foreach (var r in JmpRel().Concat(Rela().Where(x => x.Sym != 0)))
        {
            var s = syms[r.Sym];
            if (!s.Name.Contains('#', StringComparison.Ordinal) || s.Shndx != 0)
            {
                continue;
            }

            string[] parts = s.Name.Split('#');
            if (parts.Length != 3)
            {
                throw new EbootFormatException($"import symbol name \"{s.Name}\" is not NID#library#module");
            }

            int li = DecodeId(parts[1]);
            int mi = DecodeId(parts[2]);
            if (!seen.Add((r.Sym, r.Offset)))
            {
                continue;
            }

            list.Add(new ImportedSymbol
            {
                SymIndex = r.Sym,
                Nid = parts[0],
                Library = libs.TryGetValue(li, out var lib) ? lib.Name : $"lib#{li}",
                Module = mods.TryGetValue(mi, out var mod) ? mod.Name : $"mod#{mi}",
                LibId = li,
                ModId = mi,
                SymType = s.Type,
                GotVa = r.Offset,
                RelocType = r.Type,
            });
        }

        return list;
    }

    /// <summary>
    /// Finds the PLT stubs (<c>FF 25 disp32</c>, <c>jmp [rip+disp]</c>) whose target is the GOT slot of a jump-slot
    /// import and stores their VA in <see cref="ImportedSymbol.PltVa"/>; returns how many were found.
    /// </summary>
    public int FindPlt(IReadOnlyList<ImportedSymbol> imports)
    {
        var text = Text;
        var code = SegmentBytes(text);
        var byGot = new Dictionary<ulong, ImportedSymbol>();
        foreach (var i in imports)
        {
            if (i.RelocType == RX86JumpSlot)
            {
                byGot[i.GotVa] = i;
            }
        }

        int found = 0;
        ReadOnlySpan<byte> pattern = [0xFF, 0x25];
        int pos = code.IndexOf(pattern);
        while (pos >= 0)
        {
            if (pos + 6 <= code.Length)
            {
                int disp = BinaryPrimitives.ReadInt32LittleEndian(code[(pos + 2)..]);
                ulong target = unchecked(text.VAddr + (ulong)pos + 6 + (ulong)(long)disp);
                if (byGot.TryGetValue(target, out var imp) && imp.PltVa is null)
                {
                    imp.PltVa = text.VAddr + (ulong)pos;
                    found++;
                }
            }

            int next = code[(pos + 1)..].IndexOf(pattern);
            pos = next < 0 ? -1 : pos + 1 + next;
        }

        return found;
    }

    // ------------------------------------------------------------ internals

    private ReadOnlySpan<byte> Slice(long offset, int length)
    {
        var all = _data.Span;
        if (offset < 0 || length < 0 || offset > all.Length - length)
        {
            throw new EbootFormatException($"range 0x{offset:x}+0x{length:x} is outside the file (size 0x{all.Length:x})");
        }

        return all.Slice((int)offset, length);
    }

    private DynamicEntry[] ParseDynamic()
    {
        var ph = _phdrs.FirstOrDefault(p => p.Type == PtDynamic);
        if (ph is null)
        {
            return [];
        }

        long fo = ElfToFile(ph.Offset);
        var list = new List<DynamicEntry>();
        for (ulong i = 0; i < ph.FileSize / 16; i++)
        {
            var e = Slice(fo + ((long)i * 16), 16);
            ulong tag = BinaryPrimitives.ReadUInt64LittleEndian(e);
            list.Add(new DynamicEntry(tag, BinaryPrimitives.ReadUInt64LittleEndian(e[8..])));
            if (tag == 0)
            {
                break;
            }
        }

        return [.. list];
    }

    private List<Relocation> Relas(ulong offsetTag, ulong sizeTag)
    {
        long baseOffset = DynlibFile() + (long)Dyn1(offsetTag);
        int n = checked((int)(Dyn1(sizeTag) / 24));
        var table = Slice(baseOffset, checked(n * 24));
        var list = new List<Relocation>(n);
        for (int i = 0; i < n; i++)
        {
            var e = table.Slice(i * 24, 24);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(e);
            ulong info = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
            long addend = BinaryPrimitives.ReadInt64LittleEndian(e[16..]);
            list.Add(new Relocation(offset, (int)(info >> 32), (uint)(info & 0xFFFFFFFF), addend));
        }

        return list;
    }
}
