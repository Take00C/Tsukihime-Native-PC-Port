using System.Buffers.Binary;

namespace Tsuki.Data.Eboot.Tables;

/// <summary>Reads <paramref name="count"/> bytes at a virtual address of the analysed image (see <see cref="EbootReader.Read"/>).</summary>
/// <param name="va">The virtual address.</param>
/// <param name="count">The number of bytes.</param>
public delegate ReadOnlySpan<byte> CodeReader(long va, int count);

/// <summary>One store the interpreted code made. The interpreter never writes memory; it only logs the stores.</summary>
/// <param name="Address">The effective address.</param>
/// <param name="Size">1, 2, 4 or 8 bytes.</param>
/// <param name="Value">The stored value (zero extended to the store size).</param>
public readonly record struct X86Store(long Address, int Size, ulong Value);

/// <summary>The state after <see cref="TinyX86.Run"/> reached its <c>ret</c>.</summary>
public sealed class X86Result
{
    internal X86Result(ulong[] registers, IReadOnlyList<X86Store> stores)
    {
        Registers = registers;
        Stores = stores;
    }

    /// <summary>The 16 general registers (rax, rcx, rdx, rbx, rsp, rbp, rsi, rdi, r8..r15).</summary>
    public IReadOnlyList<ulong> Registers { get; }

    /// <summary>The stores in program order.</summary>
    public IReadOnlyList<X86Store> Stores { get; }

    /// <summary>The 32-bit return value (<c>eax</c>).</summary>
    public uint Eax => (uint)Registers[0];
}

/// <summary>
/// A tiny x86-64 interpreter for the two branch-only table functions of the eboot: the STMA loop switch (VA 0xC1C70) and the flowchart-jump
/// clears (VA 0xBA700). It supports exactly the instruction forms those functions use and throws <see cref="EbootFormatException"/> on anything
/// else, so a different build fails loudly instead of being misread. Port of <c>tsuki.engine_tables.TinyX86</c>.
/// </summary>
/// <remarks>
/// Supported: xor / add / cmp on registers and immediates, lea (SIB and rip-relative), movsxd, mov r32 imm32, mov [mem] imm (16, 32, 64 bit),
/// jcc rel8 / rel32, jmp rel8 / rel32 / r64, ret and the nop forms.
/// </remarks>
public sealed class TinyX86
{
    private const int Rax = 0;
    private const int Rdi = 7;

    private readonly CodeReader _read;
    private readonly byte[] _buffer = new byte[16];

    /// <summary>Creates an interpreter over a memory reader.</summary>
    public TinyX86(CodeReader read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
    }

    private enum Op
    {
        Ret,
        Nop,
        Jcc,
        Jmp,
        JmpReg,
        Xor,
        Add,
        Cmp,
        Lea,
        Movsxd,
        MovImm,
        StoreImm,
    }

    private readonly record struct Mem(bool Rip, int Base, int Index, int Scale, long Disp);

    private readonly record struct Insn(Op Op, int Length, int Cc, long Rel, int Dst, int Src, bool SrcIsImm, long Imm, int Width, Mem Mem, int Size);

    /// <summary>Executes from <paramref name="rip"/> until <c>ret</c> with <c>rdi</c> set (the argument of both functions).</summary>
    /// <param name="rip">The function's virtual address.</param>
    /// <param name="rdi">First integer argument.</param>
    /// <param name="maxSteps">An upper bound against runaway code.</param>
    /// <exception cref="EbootFormatException">An instruction is not supported, or the step limit was reached.</exception>
    public X86Result Run(long rip, ulong rdi = 0, int maxSteps = 4096)
    {
        ulong[] r = new ulong[16];
        r[Rdi] = rdi;
        List<X86Store> stores = [];
        bool zf = false, sf = false, cf = false, of = false;
        for (int step = 0; step < maxSteps; step++)
        {
            Insn ins = Decode(rip);
            long next = rip + ins.Length;
            switch (ins.Op)
            {
                case Op.Ret:
                    return new X86Result(r, stores);
                case Op.Nop:
                    break;
                case Op.Jcc:
                    if (Condition(ins.Cc, zf, sf, cf, of))
                    {
                        next += ins.Rel;
                    }

                    break;
                case Op.Jmp:
                    next += ins.Rel;
                    break;
                case Op.JmpReg:
                    next = (long)r[ins.Dst];
                    break;
                case Op.Xor:
                {
                    ulong v = Value(r, ins.Dst, ins.Width) ^ Value(r, ins.Src, ins.Width);
                    Set(r, ins.Dst, v, ins.Width);
                    zf = v == 0;
                    sf = ((v >> (ins.Width - 1)) & 1) != 0;
                    cf = false;
                    of = false;
                    break;
                }

                case Op.Add:
                case Op.Cmp:
                {
                    ulong m = ins.Width == 64 ? ulong.MaxValue : 0xFFFFFFFFUL;
                    ulong a = Value(r, ins.Dst, ins.Width);
                    ulong b = ins.SrcIsImm ? (ulong)ins.Imm & m : Value(r, ins.Src, ins.Width);
                    int top = ins.Width - 1;
                    if (ins.Op == Op.Add)
                    {
                        ulong res = (a + b) & m;
                        zf = res == 0;
                        sf = ((res >> top) & 1) != 0;
                        cf = res < a;
                        of = (((~(a ^ b)) & (a ^ res)) >> top & 1) != 0;
                        Set(r, ins.Dst, res, ins.Width);
                    }
                    else
                    {
                        ulong res = (a - b) & m;
                        zf = res == 0;
                        sf = ((res >> top) & 1) != 0;
                        cf = a < b;
                        of = (((a ^ b) & (a ^ res)) >> top & 1) != 0;
                    }

                    break;
                }

                case Op.Lea:
                    Set(r, ins.Dst, (ulong)EffectiveAddress(ins.Mem, r, next), ins.Width);
                    break;
                case Op.Movsxd:
                    r[ins.Dst] = (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(_read(EffectiveAddress(ins.Mem, r, next), 4));
                    break;
                case Op.MovImm:
                    Set(r, ins.Dst, (ulong)ins.Imm, ins.Width);
                    break;
                case Op.StoreImm:
                {
                    ulong v = ins.Size == 8 ? (ulong)ins.Imm : (ulong)ins.Imm & ((1UL << (8 * ins.Size)) - 1);
                    stores.Add(new X86Store(EffectiveAddress(ins.Mem, r, next), ins.Size, v));
                    break;
                }

                default:
                    throw new EbootFormatException($"TinyX86: internal error at 0x{rip:x}");
            }

            rip = next;
        }

        throw new EbootFormatException("TinyX86: step limit reached");
    }

    private static ulong Value(ulong[] r, int reg, int width) => width == 64 ? r[reg] : r[reg] & 0xFFFFFFFFUL;

    private static void Set(ulong[] r, int reg, ulong v, int width) => r[reg] = width == 64 ? v : v & 0xFFFFFFFFUL;

    private static long EffectiveAddress(Mem m, ulong[] r, long next)
    {
        if (m.Rip)
        {
            return next + m.Disp;
        }

        long a = m.Disp;
        if (m.Base >= 0)
        {
            a += (long)r[m.Base];
        }

        if (m.Index >= 0)
        {
            a += (long)r[m.Index] * m.Scale;
        }

        return a;
    }

    private static bool Condition(int cc, bool zf, bool sf, bool cf, bool of)
    {
        bool baseValue = (cc >> 1) switch
        {
            0 => of,
            1 => cf,
            2 => zf,
            3 => cf || zf,
            4 => sf,
            5 => throw new EbootFormatException("TinyX86: parity conditions are not used by these tables"),
            6 => sf != of,
            _ => zf || sf != of,
        };
        return (cc & 1) != 0 ? !baseValue : baseValue;
    }

    private Insn Decode(long rip)
    {
        byte[] buf = _buffer;
        _read(rip, 16).CopyTo(buf);
        int i = 0;
        bool opsz16 = false;
        while (buf[i] == 0x66)
        {
            opsz16 = true;
            i++;
        }

        int rex = 0;
        if (buf[i] is >= 0x40 and <= 0x4F)
        {
            rex = buf[i];
            i++;
        }

        bool rexw = (rex & 8) != 0;
        int rexr = (rex >> 2) & 1, rexx = (rex >> 1) & 1, rexb = rex & 1;
        int op = buf[i++];
        int width = rexw ? 64 : 32;

        // returns reg field, and either the rm register (rmReg >= 0) or the memory operand
        (int Reg, int RmReg, Mem Mem) ModRm()
        {
            int m = buf[i++];
            int mod = m >> 6;
            int reg = ((m >> 3) & 7) | (rexr << 3);
            int rm = m & 7;
            if (mod == 3)
            {
                return (reg, rm | (rexb << 3), default);
            }

            if (rm == 4)
            {
                int sib = buf[i++];
                int scale = 1 << (sib >> 6);
                int idx = ((sib >> 3) & 7) | (rexx << 3);
                int bas = (sib & 7) | (rexb << 3);
                int index = idx == 4 ? -1 : idx;
                if ((sib & 7) == 5 && mod == 0)
                {
                    long d32 = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i));
                    i += 4;
                    return (reg, -1, new Mem(false, -1, index, scale, d32));
                }

                return (reg, -1, new Mem(false, bas, index, scale, Displacement(mod)));
            }

            if (rm == 5 && mod == 0)
            {
                long d32 = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i));
                i += 4;
                return (reg, -1, new Mem(true, -1, -1, 1, d32));
            }

            return (reg, -1, new Mem(false, rm | (rexb << 3), -1, 1, Displacement(mod)));
        }

        long Displacement(int mod)
        {
            if (mod == 1)
            {
                return (sbyte)buf[i++];
            }

            if (mod == 2)
            {
                long d = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i));
                i += 4;
                return d;
            }

            return 0;
        }

        Insn Make(Op o, int cc = 0, long rel = 0, int dst = 0, int src = 0, bool srcImm = false, long imm = 0, int w = 32, Mem mem = default, int size = 0) =>
            new(o, i, cc, rel, dst, src, srcImm, imm, w, mem, size);

        if (op == 0xC3)
        {
            return Make(Op.Ret);
        }

        if (op == 0x90)
        {
            return Make(Op.Nop);
        }

        if (op is >= 0x70 and <= 0x7F)
        {
            long rel = (sbyte)buf[i++];
            return Make(Op.Jcc, op & 15, rel);
        }

        if (op == 0x0F)
        {
            int op2 = buf[i++];
            if (op2 is >= 0x80 and <= 0x8F)
            {
                long rel = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i));
                i += 4;
                return Make(Op.Jcc, op2 & 15, rel);
            }

            if (op2 == 0x1F)
            {
                _ = ModRm();
                return Make(Op.Nop);
            }

            throw new EbootFormatException($"TinyX86: opcode 0F {op2:X2} at 0x{rip:x} is not supported");
        }

        if (op == 0xEB)
        {
            long rel = (sbyte)buf[i++];
            return Make(Op.Jmp, rel: rel);
        }

        if (op == 0xE9)
        {
            long rel = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i));
            i += 4;
            return Make(Op.Jmp, rel: rel);
        }

        if (op is >= 0xB8 and <= 0xBF)
        {
            long imm = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i));
            i += 4;
            return Make(Op.MovImm, dst: (op - 0xB8) | (rexb << 3), imm: imm, w: 32);
        }

        if (op is 0x31 or 0x01 or 0x39)
        {
            (int reg, int rmReg, _) = ModRm();
            if (rmReg < 0)
            {
                throw new EbootFormatException($"TinyX86: memory operand of opcode {op:X2} at 0x{rip:x} is not supported");
            }

            Op o = op == 0x31 ? Op.Xor : op == 0x01 ? Op.Add : Op.Cmp;
            return Make(o, dst: rmReg, src: reg, w: width);
        }

        if (op is 0x81 or 0x83)
        {
            (int reg, int rmReg, _) = ModRm();
            long imm;
            if (op == 0x81)
            {
                imm = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i));
                i += 4;
            }
            else
            {
                imm = (sbyte)buf[i++];
            }

            if (rmReg < 0 || ((reg & 7) != 0 && (reg & 7) != 7))
            {
                throw new EbootFormatException($"TinyX86: group-1 form at 0x{rip:x} is not supported");
            }

            return Make((reg & 7) == 0 ? Op.Add : Op.Cmp, dst: rmReg, srcImm: true, imm: imm, w: width);
        }

        if (op == 0x3D)
        {
            long imm = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i));
            i += 4;
            return Make(Op.Cmp, dst: Rax, srcImm: true, imm: imm, w: width);
        }

        if (op == 0x8D)
        {
            (int reg, int rmReg, Mem mem) = ModRm();
            if (rmReg >= 0)
            {
                throw new EbootFormatException($"TinyX86: lea r,r at 0x{rip:x} is not supported");
            }

            return Make(Op.Lea, dst: reg, mem: mem, w: width);
        }

        if (op == 0x63 && rexw)
        {
            (int reg, int rmReg, Mem mem) = ModRm();
            if (rmReg >= 0)
            {
                throw new EbootFormatException($"TinyX86: movsxd r,r at 0x{rip:x} is not supported");
            }

            return Make(Op.Movsxd, dst: reg, mem: mem);
        }

        if (op == 0xFF)
        {
            (int reg, int rmReg, _) = ModRm();
            if ((reg & 7) == 4 && rmReg >= 0)
            {
                return Make(Op.JmpReg, dst: rmReg);
            }

            throw new EbootFormatException($"TinyX86: opcode FF /{reg & 7} at 0x{rip:x} is not supported");
        }

        if (op == 0xC7)
        {
            (int reg, int rmReg, Mem mem) = ModRm();
            if ((reg & 7) != 0 || rmReg >= 0)
            {
                throw new EbootFormatException($"TinyX86: opcode C7 form at 0x{rip:x} is not supported");
            }

            if (opsz16)
            {
                long imm16 = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i));
                i += 2;
                return Make(Op.StoreImm, imm: imm16, mem: mem, size: 2);
            }

            long imm32 = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(i));
            i += 4;
            return Make(Op.StoreImm, imm: imm32, mem: mem, size: rexw ? 8 : 4);
        }

        throw new EbootFormatException($"TinyX86: opcode {op:X2} at 0x{rip:x} is not supported");
    }
}
