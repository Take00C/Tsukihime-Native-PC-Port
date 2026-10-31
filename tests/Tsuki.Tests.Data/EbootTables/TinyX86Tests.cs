using System.Buffers.Binary;
using Tsuki.Data.Eboot;
using Tsuki.Data.Eboot.Tables;

namespace Tsuki.Tests.Data.EbootTables;

/// <summary>
/// The x86-64 interpreter behind the STMA loop points and the flowchart-jump clears, on hand-assembled programs (the same programs as
/// tools/tests/test_engine_tables.py): jump tables, signed and unsigned branches, logged stores and loud failure on foreign code.
/// </summary>
public sealed class TinyX86Tests
{
    private static CodeReader Reader(long baseVa, byte[] code) => (va, count) =>
    {
        byte[] buffer = new byte[count];
        for (int k = 0; k < count; k++)
        {
            long at = va - baseVa + k;
            buffer[k] = at >= 0 && at < code.Length ? code[at] : (byte)0;
        }

        return buffer;
    };

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    /// <summary>int f(int id): 0 for id &lt;= 4; a jump table over ids 5..8 giving 11, 22, 33, 0; 0 above.</summary>
    private static byte[] SwitchProgram()
    {
        byte[] code = new byte[0x80];
        Hex("31C0").CopyTo(code, 0);                    // xor eax, eax
        Hex("8D4FFB").CopyTo(code, 2);                  // lea ecx, [rdi - 5]
        Hex("83F903").CopyTo(code, 5);                  // cmp ecx, 3
        const int retAt = 0x40;
        code[8] = 0x77;                                  // ja ret
        code[9] = (byte)(retAt - 0x0A);
        Hex("488D15").CopyTo(code, 0x0A);               // lea rdx, [rip + table]
        BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(0x0D), 0x50 - 0x11);
        Hex("48630C8A").CopyTo(code, 0x11);             // movsxd rcx, dword [rdx + rcx*4]
        Hex("4801D1").CopyTo(code, 0x15);               // add rcx, rdx
        Hex("FFE1").CopyTo(code, 0x18);                 // jmp rcx
        code[retAt] = 0xC3;
        int[] targets = new int[4];
        uint[] values = [11, 22, 33];
        for (int k = 0; k < 3; k++)
        {
            int t = 0x20 + (8 * k);
            code[t] = 0xB8;                              // mov eax, imm32
            BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(t + 1), values[k]);
            code[t + 5] = 0xC3;
            targets[k] = t;
        }

        targets[3] = retAt;
        for (int k = 0; k < 4; k++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(0x50 + (4 * k)), targets[k] - 0x50);
        }

        return code;
    }

    [Fact]
    public void RunsAJumpTableFunction()
    {
        var cpu = new TinyX86(Reader(0x1000, SwitchProgram()));
        uint[] got = [.. Enumerable.Range(0, 12).Select(i => cpu.Run(0x1000, (ulong)i).Eax)];
        Assert.Equal(new uint[] { 0, 0, 0, 0, 0, 11, 22, 33, 0, 0, 0, 0 }, got);
    }

    [Fact]
    public void SignedAndUnsignedConditionsDiffer()
    {
        // cmp edi, 10 ; jg +6 ; mov eax, 1 ; ret ; mov eax, 2 ; ret
        byte[] jg = [.. Hex("81FF0A000000"), 0x7F, 0x06, 0xB8, 1, 0, 0, 0, 0xC3, 0xB8, 2, 0, 0, 0, 0xC3];
        var cpu = new TinyX86(Reader(0, jg));
        Assert.Equal(1u, cpu.Run(0, 10).Eax);
        Assert.Equal(2u, cpu.Run(0, 11).Eax);
        Assert.Equal(1u, cpu.Run(0, 0xFFFFFFFBUL).Eax);   // -5 > 10 is false as a signed compare

        byte[] ja = (byte[])jg.Clone();
        ja[6] = 0x77;                                       // ja: unsigned, so 0xFFFFFFFB is above 10
        Assert.Equal(2u, new TinyX86(Reader(0, ja)).Run(0, 0xFFFFFFFBUL).Eax);
    }

    [Fact]
    public void LogsConstantStoresWithoutPerformingThem()
    {
        // lea rax, [rip + 0x100] ; mov word [rax + 0x8A], 0 ; mov qword [rax + 0x82], 0 ; ret
        byte[] code = [.. Hex("488D05"), .. BitConverter.GetBytes(0x100 - 7), .. Hex("66C7808A0000000000"), .. Hex("48C7808200000000000000"), 0xC3];
        X86Result result = new TinyX86(Reader(0, code)).Run(0);
        Assert.Equal([new X86Store(0x100 + 0x8A, 2, 0), new X86Store(0x100 + 0x82, 8, 0)], result.Stores);
    }

    [Fact]
    public void UnknownInstructionsAndRunawayLoopsFailLoudly()
    {
        var ex = Assert.Throws<EbootFormatException>(() => new TinyX86(Reader(0, [0xCC, 0xC3])).Run(0));
        Assert.Contains("not supported", ex.Message, StringComparison.Ordinal);
        var loop = Assert.Throws<EbootFormatException>(() => new TinyX86(Reader(0, [0xEB, 0xFE])).Run(0, maxSteps: 50));
        Assert.Contains("step limit", loop.Message, StringComparison.Ordinal);
    }
}
