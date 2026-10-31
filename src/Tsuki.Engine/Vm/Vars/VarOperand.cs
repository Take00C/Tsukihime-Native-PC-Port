using System.Collections.Immutable;
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Vm.Variables;

/// <summary>What a statically decoded operand addresses.</summary>
public enum OperandKind : byte
{
    /// <summary>The command ignores the operand / does nothing.</summary>
    None = 0,

    /// <summary>The literal value <see cref="VarOperand.Index"/>.</summary>
    Literal = 1,

    /// <summary>F[<see cref="VarOperand.Index"/>].</summary>
    F = 2,

    /// <summary>G[<see cref="VarOperand.Index"/>].</summary>
    G = 3,

    /// <summary>F[F823 + <see cref="VarOperand.Index"/>].</summary>
    C = 4,
}

/// <summary>
/// A statically decoded operand (for dumps, census and tools; port of <c>tsuki.vm_vars.Operand</c> /
/// <c>parse_operand</c>). The executing path is <see cref="VarCommands"/>, which reads the same bytes in the engine's order.
/// </summary>
/// <param name="Kind">What the operand addresses.</param>
/// <param name="Index">Variable index, or the literal value.</param>
public readonly record struct VarOperand(OperandKind Kind, int Index)
{
    /// <summary>The address the operand forms (C with the given F823), or null for a literal / nothing.</summary>
    public long? Address(int f823 = 0) => Kind switch
    {
        OperandKind.F => VarAddresses.F + (2L * Index),
        OperandKind.G => VarAddresses.G + (2L * Index),
        OperandKind.C => VarAddresses.F + (2L * ((short)f823 + Index)),
        _ => null,
    };

    /// <summary>Same text as the reference's <c>str(Operand)</c>: <c>F[12]</c>, <c>F[F823+847]</c>, <c>lit(3)</c>, <c>none(0)</c>.</summary>
    public override string ToString() => Kind switch
    {
        OperandKind.F => $"F[{Index}]",
        OperandKind.G => $"G[{Index}]",
        OperandKind.C => $"F[F823+{Index}]",
        OperandKind.Literal => $"lit({Index})",
        _ => $"none({Index})",
    };

    /// <summary>Decodes one argument text (an optional <c>`NNN:</c> tag included) the way the engine does in <paramref name="role"/>.</summary>
    public static VarOperand Parse(ReadOnlySpan<byte> raw, OperandRole role)
    {
        ReadOnlySpan<byte> body = raw.Length > 0 && raw[0] == (byte)'`' ? (raw.Length > 5 ? raw[5..] : []) : raw;
        byte first = body.Length > 0 ? body[0] : (byte)0;
        if (role is OperandRole.Gots or OperandRole.Choice)
        {
            if (first != 0x5F)
            {
                return new VarOperand(OperandKind.None, 0);
            }

            int d = VarCommands.ThreeDigits(body);
            if (role == OperandRole.Choice)
            {
                return body.Length > 1 && body[1] == 0x44 ? new VarOperand(OperandKind.F, (short)d) : new VarOperand(OperandKind.None, d);
            }

            return VarCommands.UnderscoreIsG(d) ? Underscore(d) : new VarOperand(OperandKind.F, d);
        }

        int n = InlineReader.ReadText(raw);
        bool allowed = role switch
        {
            OperandRole.Value => first is 0x46 or 0x47 or 0x5F,
            OperandRole.WkTarget => first is 0x43 or 0x46 or 0x47 or 0x5F,
            OperandRole.OrTarget => first is 0x46 or 0x47 or 0x5F,
            _ => first is 0x46 or 0x47,
        };
        if (first == 0 || !allowed)
        {
            return new VarOperand(role == OperandRole.Value ? OperandKind.Literal : OperandKind.None, n);
        }

        return first switch
        {
            0x43 => new VarOperand(OperandKind.C, (short)n),
            0x5F => Underscore(n),
            0x46 => new VarOperand(OperandKind.F, (short)n),
            _ => new VarOperand(OperandKind.G, (short)n),
        };
    }

    /// <summary>Decodes an ASCII / Latin-1 argument text.</summary>
    public static VarOperand Parse(string text, OperandRole role)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(System.Text.Encoding.Latin1.GetBytes(text), role);
    }

    private static VarOperand Underscore(int n) =>
        VarCommands.UnderscoreIsG(n) ? new VarOperand(OperandKind.G, (n & 0xFFFF) - 600) : new VarOperand(OperandKind.F, (short)n);
}

/// <summary>How a history record / save slot keeps one F value (docs/formats/vm_variables.md 2.4; adv_state.md 2.1).</summary>
public enum HistoryStorage : byte
{
    /// <summary>Not kept: 0 after loading a slot, unchanged by a rewind.</summary>
    None = 0,

    /// <summary>Kept exactly.</summary>
    U16 = 1,

    /// <summary>Kept as its low byte, restored sign-extended.</summary>
    S8 = 2,

    /// <summary>Kept as a bit, restored as value != 0.</summary>
    Bool = 3,
}

/// <summary>
/// The F values a history record keeps (port of <c>tsuki.vm_vars.history_storage</c> / <c>history_roundtrip</c> /
/// <c>history_after_rewind</c>), over the index lists of L1's <see cref="HistoryRecord"/>. The restore order is u16, s8,
/// bool, so an index in two lists ends with the later list's storage (F967: s8, F991: bool).
/// </summary>
public static class HistoryVars
{
    private static readonly ImmutableArray<HistoryStorage> Table = Build();

    /// <summary>How F[<paramref name="index"/>] is kept.</summary>
    public static HistoryStorage Storage(int index) => (uint)index < (uint)Table.Length ? Table[index] : HistoryStorage.None;

    /// <summary>F[<paramref name="index"/>] after saving <paramref name="value"/> and loading the slot again.</summary>
    public static ushort RoundTrip(int index, int value) => Storage(index) switch
    {
        HistoryStorage.U16 => unchecked((ushort)value),
        HistoryStorage.S8 => unchecked((ushort)(sbyte)value),
        HistoryStorage.Bool => (value & 0xFFFF) != 0 ? (ushort)1 : (ushort)0,
        _ => 0,
    };

    /// <summary>F[<paramref name="index"/>] after a rewind to a record taken when it was <paramref name="saved"/>; <paramref name="current"/> is its value before the rewind.</summary>
    public static ushort AfterRewind(int index, int saved, int current) =>
        Storage(index) == HistoryStorage.None ? unchecked((ushort)current) : RoundTrip(index, saved);

    private static ImmutableArray<HistoryStorage> Build()
    {
        var t = new HistoryStorage[Contracts.Vars.FCount + 1];
        foreach (int i in HistoryRecord.FU16Indices)
        {
            t[i] = HistoryStorage.U16;
        }

        foreach (int i in HistoryRecord.FS8Indices)
        {
            t[i] = HistoryStorage.S8;
        }

        foreach (int i in HistoryRecord.FBoolIndices)
        {
            t[i] = HistoryStorage.Bool;
        }

        return [.. t];
    }
}
