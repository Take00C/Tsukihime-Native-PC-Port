using System.Buffers.Binary;
using System.Text;

namespace Tsuki.Tests.Data.Catalog;

/// <summary>
/// A hand-built SELF + ELF64 image with the structure of the retail eboot (a text segment with a PLT stub, a data segment with
/// .bss, PT_DYNAMIC inside PT_SCE_DYNLIBDATA, a symbol table, relocations, modules and libraries), small enough that the expected
/// values of every reader call are known by construction. Offsets are fixed constants so the tests can state them.
/// </summary>
public static class SyntheticSelf
{
    /// <summary>File offset of the text segment bytes.</summary>
    public const int TextFile = 0x200;

    /// <summary>Size of the text segment.</summary>
    public const int TextSize = 0x100;

    /// <summary>File offset of the data segment bytes.</summary>
    public const int DataFile = 0x400;

    /// <summary>Size of the data segment in the file.</summary>
    public const int DataSize = 0x40;

    /// <summary>Extra .bss bytes after the file part of the data segment.</summary>
    public const int Bss = 0x80;

    /// <summary>VA of the data segment.</summary>
    public const ulong DataVa = 0x1000;

    /// <summary>File offset of the dynlib segment.</summary>
    public const int DynlibFile = 0x500;

    /// <summary>ELF file offset of the dynlib segment.</summary>
    public const ulong DynlibElf = 0x20000;

    /// <summary>VA of the PLT stub whose target is the GOT slot of the jump-slot import.</summary>
    public const ulong PltStub = 0x40;

    /// <summary>GOT slot of the jump-slot import.</summary>
    public const ulong Got = 0x1010;

    /// <summary>Builds the image.</summary>
    public static byte[] Build()
    {
        // ---- dynlib segment ----
        var str = new List<byte> { 0 };
        int Add(string s)
        {
            int at = str.Count;
            str.AddRange(Encoding.ASCII.GetBytes(s));
            str.Add(0);
            return at;
        }

        int libName = Add("libfoo");
        int modName = Add("modbar");
        int symA = Add("AAAAAAAAAAA#B#C");
        int symB = Add("BBBBBBBBBBB#B#D");
        int infoName = Add("devmod");
        var dyn = new List<byte>();
        var strtab = str.ToArray();
        byte[] symtab = new byte[3 * 24];
        void Sym(int i, int name, byte info, ushort shndx)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(symtab.AsSpan(i * 24), (uint)name);
            symtab[(i * 24) + 4] = info;
            BinaryPrimitives.WriteUInt16LittleEndian(symtab.AsSpan((i * 24) + 6), shndx);
        }

        Sym(1, symA, 0x12, 0);
        Sym(2, symB, 0x11, 0);
        byte[] jmprel = Rela((Got, 1, 7, 0));
        byte[] rela = Rela((0x1020, 0, 8, 0x1234), (0x1018, 2, 6, 0), (0x1028, 1, 1, 7));

        // layout inside the dynlib segment
        int oStr = 0;
        int oSym = Align8(oStr + strtab.Length);
        int oJmp = oSym + symtab.Length;
        int oRel = oJmp + jmprel.Length;
        int oDyn = oRel + rela.Length;
        var dynamic = new List<(ulong, ulong)>
        {
            (0x61000035, (ulong)oStr), (0x61000037, (ulong)strtab.Length), (0x61000039, (ulong)oSym), (0x6100003F, (ulong)symtab.Length),
            (0x61000029, (ulong)oJmp), (0x6100002D, (ulong)jmprel.Length), (0x6100002F, (ulong)oRel), (0x61000031, (ulong)rela.Length),
            (0x6100000F, (2UL << 48) | (3UL << 40) | (4UL << 32) | (uint)modName),
            (0x61000015, (1UL << 48) | (5UL << 32) | (uint)libName),
            (0x61000019, (1UL << 48) | 0x0102UL),
            (0x6100000D, (0x99UL << 32) | (uint)infoName),
            (0, 0),
        };
        int dynBytes = dynamic.Count * 16;
        byte[] dynlib = new byte[oDyn + dynBytes];
        strtab.CopyTo(dynlib, oStr);
        symtab.CopyTo(dynlib, oSym);
        jmprel.CopyTo(dynlib, oJmp);
        rela.CopyTo(dynlib, oRel);
        for (int i = 0; i < dynamic.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(dynlib.AsSpan(oDyn + (i * 16)), dynamic[i].Item1);
            BinaryPrimitives.WriteUInt64LittleEndian(dynlib.AsSpan(oDyn + (i * 16) + 8), dynamic[i].Item2);
        }

        // ---- text and data ----
        byte[] text = new byte[TextSize];
        Array.Fill(text, (byte)0x90);
        Plt(text, 0x40, (int)Got - (0x40 + 6));
        Plt(text, 0x60, 1); // targets VA 0x67: no import
        Encoding.ASCII.GetBytes("Hello\0").CopyTo(text, 0x80);
        text[0xFE] = (byte)'a';
        text[0xFF] = (byte)'b';
        byte[] data = new byte[DataSize];
        BinaryPrimitives.WriteUInt64LittleEndian(data, 0x1122334455667788);
        Encoding.ASCII.GetBytes("data\0").CopyTo(data, 0x20);

        // ---- headers ----
        byte[] file = new byte[DynlibFile + dynlib.Length];
        file[0] = 0x4F;
        file[1] = 0x15;
        file[2] = 0x3D;
        file[3] = 0x1D;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x18), 4);
        void Seg(int i, ulong flags, ulong off, ulong size)
        {
            var s = file.AsSpan(0x20 + (i * 0x20));
            BinaryPrimitives.WriteUInt64LittleEndian(s, flags);
            BinaryPrimitives.WriteUInt64LittleEndian(s[8..], off);
            BinaryPrimitives.WriteUInt64LittleEndian(s[16..], size);
            BinaryPrimitives.WriteUInt64LittleEndian(s[24..], size);
        }

        Seg(0, (1UL << 20) | 4, 0x180, 0x20);
        Seg(1, (0UL << 20) | 0x800, TextFile, TextSize);
        Seg(2, (1UL << 20) | 0x800, DataFile, DataSize);
        Seg(3, (3UL << 20) | 0x800, DynlibFile, (ulong)dynlib.Length);
        const int elf = 0xA0;
        file[elf] = 0x7F;
        file[elf + 1] = (byte)'E';
        file[elf + 2] = (byte)'L';
        file[elf + 3] = (byte)'F';
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(elf + 0x18), 0x77); // entry
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(elf + 0x20), 0x40); // e_phoff
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(elf + 0x36), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(elf + 0x38), 4);
        void Phdr(int i, uint type, uint flags, ulong off, ulong va, ulong filesz, ulong memsz)
        {
            var p = file.AsSpan(elf + 0x40 + (i * 56));
            BinaryPrimitives.WriteUInt32LittleEndian(p, type);
            BinaryPrimitives.WriteUInt32LittleEndian(p[4..], flags);
            BinaryPrimitives.WriteUInt64LittleEndian(p[8..], off);
            BinaryPrimitives.WriteUInt64LittleEndian(p[16..], va);
            BinaryPrimitives.WriteUInt64LittleEndian(p[24..], va);
            BinaryPrimitives.WriteUInt64LittleEndian(p[32..], filesz);
            BinaryPrimitives.WriteUInt64LittleEndian(p[40..], memsz);
            BinaryPrimitives.WriteUInt64LittleEndian(p[48..], 0x4000);
        }

        Phdr(0, 1, 5, 0x4000, 0, TextSize, TextSize);
        Phdr(1, 1, 6, 0x8000, DataVa, DataSize, DataSize + Bss);
        Phdr(2, 2, 6, DynlibElf + (ulong)oDyn, 0, (ulong)dynBytes, (ulong)dynBytes);
        Phdr(3, 0x61000000, 4, DynlibElf, 0, (ulong)dynlib.Length, 0);
        text.CopyTo(file, TextFile);
        data.CopyTo(file, DataFile);
        dynlib.CopyTo(file, DynlibFile);
        return file;
    }

    private static int Align8(int n) => (n + 7) & ~7;

    private static void Plt(byte[] text, int at, int disp)
    {
        text[at] = 0xFF;
        text[at + 1] = 0x25;
        BinaryPrimitives.WriteInt32LittleEndian(text.AsSpan(at + 2), disp);
    }

    private static byte[] Rela(params (ulong Offset, uint Sym, uint Type, long Addend)[] entries)
    {
        byte[] b = new byte[entries.Length * 24];
        for (int i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(i * 24), entries[i].Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan((i * 24) + 8), ((ulong)entries[i].Sym << 32) | entries[i].Type);
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan((i * 24) + 16), entries[i].Addend);
        }

        return b;
    }
}
