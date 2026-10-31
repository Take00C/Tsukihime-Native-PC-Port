using VarsBlock = Tsuki.Engine.Contracts.Vars;

namespace Tsuki.Engine.Vm.Variables;

/// <summary>Engine addresses of the variable side of the VM (docs/formats/vm_variables.md 2.1; <c>tsuki.vm_vars</c>).</summary>
public static class VarAddresses
{
    /// <summary>var_F (u16[1200]).</summary>
    public const long F = VarsBlock.FVa;

    /// <summary>var_Z (u16[1400]).</summary>
    public const long Z = VarsBlock.ZVa;

    /// <summary>var_G (u16[2000]).</summary>
    public const long G = VarsBlock.GVa;

    /// <summary>The first byte after var_G.</summary>
    public const long AfterG = 0x164D5C0;

    /// <summary>The scratch copy of the argument being decoded (0x40 bytes, VA 0x16543C0).</summary>
    public const long ArgBuffer = 0x16543C0;

    /// <summary>F823: message counter of the page; <c>C&lt;n&gt;</c> = F[F823 + n].</summary>
    public const int MessageIndexVar = VarsBlock.MessageIndexVar;

    /// <summary>F1118: 0 = <c>_SELA</c> opens its save prompt (and clears it).</summary>
    public const int SavePromptVar = 1118;

    /// <summary>F929: non-zero blocks History_Snapshot.</summary>
    public const int SnapshotGateVar = 929;

    /// <summary>F1102: non-zero blocks History_Snapshot except for kinds 0 and 11.</summary>
    public const int SnapshotModeVar = 1102;

    /// <summary>Address of F[<paramref name="n"/>] (no bounds check, as in the engine).</summary>
    public static long OfF(int n) => F + (2L * n);

    /// <summary>Address of G[<paramref name="n"/>] (no bounds check, as in the engine).</summary>
    public static long OfG(int n) => G + (2L * n);

    /// <summary>
    /// <c>F[12]</c> / <c>Z[0]</c> / <c>G[3]</c> for an address inside the three arrays, else the address in hex
    /// (<c>tsuki.vm_vars.address_name</c>; the slice traces name their writes this way).
    /// </summary>
    public static string Name(long va)
    {
        if (va >= F && va < F + (2L * VarsBlock.FCount) && (va - F) % 2 == 0)
        {
            return $"F[{(va - F) / 2}]";
        }

        if (va >= Z && va < Z + (2L * VarsBlock.ZCount) && (va - Z) % 2 == 0)
        {
            return $"Z[{(va - Z) / 2}]";
        }

        if (va >= G && va < G + (2L * VarsBlock.GCount) && (va - G) % 2 == 0)
        {
            return $"G[{(va - G) / 2}]";
        }

        return $"0x{va:x}";
    }
}

/// <summary>A read or a write of one 16-bit word.</summary>
public enum VarAccessKind : byte
{
    /// <summary>A read.</summary>
    Read = 0,

    /// <summary>A write.</summary>
    Write = 1,
}

/// <summary>One logged variable access: kind, engine address, value read or written.</summary>
/// <param name="Kind">Read or write.</param>
/// <param name="Va">Engine address of the word.</param>
/// <param name="Value">The value read or the (16-bit) value written.</param>
public readonly record struct VarAccess(VarAccessKind Kind, long Va, ushort Value);

/// <summary>What <see cref="VarMemory"/> does with a word outside var_F / var_Z / var_G.</summary>
public enum OutOfBlockPolicy : byte
{
    /// <summary>
    /// Throw <see cref="VarAccessException"/> (RUNTIME_REQUIREMENTS item 6: "port: reject out-of-range"; the retail
    /// scripts never form such an address).
    /// </summary>
    Reject = 0,

    /// <summary>
    /// Keep the word in a side table, as the engine's flat memory would (its neighbours are other engine state). Used by
    /// the differential tests, which address words around the arrays on purpose.
    /// </summary>
    Flat = 1,
}

/// <summary>An operand formed an address outside the three variable arrays under <see cref="OutOfBlockPolicy.Reject"/>.</summary>
public sealed class VarAccessException : Exception
{
    /// <summary>Creates the exception.</summary>
    public VarAccessException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public VarAccessException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public VarAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The variable memory the VM commands address: 16-bit words keyed by engine address, the way the engine forms them
/// (<c>base + 2 * n</c> with no bounds check; docs/formats/vm_variables.md 2.2). Words of var_F / var_Z / var_G live
/// in a <see cref="VarsBlock"/> (L1's contract); any other address follows the <see cref="OutOfBlockPolicy"/>. Port of
/// <c>tsuki.vm_vars.VarMemory</c>; <see cref="Log"/> records every access in program order like its <c>log</c>.
/// </summary>
public sealed class VarMemory
{
    private readonly Dictionary<long, ushort> outside = [];
    private readonly Func<long, ushort>? outsideInit;

    /// <summary>Creates a memory over <paramref name="block"/>.</summary>
    /// <param name="block">The F / Z / G block (shared, not copied).</param>
    /// <param name="policy">What happens to an address outside the block.</param>
    /// <param name="outsideInit">Under <see cref="OutOfBlockPolicy.Flat"/>: the value of an outside word never written (default 0).</param>
    public VarMemory(VarsBlock block, OutOfBlockPolicy policy = OutOfBlockPolicy.Reject, Func<long, ushort>? outsideInit = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        Block = block;
        Policy = policy;
        this.outsideInit = outsideInit;
    }

    /// <summary>Creates a memory over a new zeroed block.</summary>
    public VarMemory()
        : this(new VarsBlock())
    {
    }

    /// <summary>The F / Z / G words.</summary>
    public VarsBlock Block { get; }

    /// <summary>What happens to an address outside the block.</summary>
    public OutOfBlockPolicy Policy { get; }

    /// <summary>When not null, every <see cref="Read"/> and <see cref="Write"/> is appended here.</summary>
    public List<VarAccess>? Log { get; set; }

    /// <summary>Number of accesses (peeks included) to words outside the block so far.</summary>
    public int OutOfBlockAccesses { get; private set; }

    /// <summary>The value of the word at <paramref name="va"/> without logging.</summary>
    public ushort Peek(long va)
    {
        if (TryWord(va, out int word))
        {
            return Block.GetWord(word);
        }

        Outside(va);
        return outside.TryGetValue(va, out ushort v) ? v : outsideInit?.Invoke(va) ?? 0;
    }

    /// <summary>Reads the word at <paramref name="va"/> (logged).</summary>
    public ushort Read(long va)
    {
        ushort v = Peek(va);
        Log?.Add(new VarAccess(VarAccessKind.Read, va, v));
        return v;
    }

    /// <summary>Writes the low 16 bits of <paramref name="value"/> to <paramref name="va"/> (logged).</summary>
    public void Write(long va, int value)
    {
        ushort v = unchecked((ushort)value);
        Poke(va, v);
        Log?.Add(new VarAccess(VarAccessKind.Write, va, v));
    }

    /// <summary>Sets a word without logging (set-up).</summary>
    public void Poke(long va, ushort value)
    {
        if (TryWord(va, out int word))
        {
            Block.SetWord(word, value);
            return;
        }

        Outside(va);
        outside[va] = value;
    }

    /// <summary>Word index of <paramref name="va"/> in the block, when it is an even address inside it.</summary>
    public static bool TryWord(long va, out int word)
    {
        long d = va - VarsBlock.FVa;
        if (d >= 0 && d < 2L * VarsBlock.WordCount && (d & 1) == 0)
        {
            word = (int)(d >> 1);
            return true;
        }

        word = -1;
        return false;
    }

    private void Outside(long va)
    {
        OutOfBlockAccesses++;
        if (Policy == OutOfBlockPolicy.Reject)
        {
            throw new VarAccessException($"variable address 0x{va:x} is outside var_F / var_Z / var_G (vm_variables.md 2.2)");
        }
    }
}
