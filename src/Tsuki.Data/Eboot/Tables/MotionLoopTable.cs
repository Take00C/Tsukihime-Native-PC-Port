using System.Buffers.Binary;

namespace Tsuki.Data.Eboot.Tables;

/// <summary>
/// The STMA loop points (knife <c>motion_loops.rs</c>). The PS4 has no data table for them: the loop point of a motion id is a compiled switch,
/// <c>int loop_point(int id)</c> at VA 0xC1C70 (0 = no loop, 1 = loop the whole motion, n &gt; 1 = loop from n ms; docs/formats/layers.md 8.1).
/// This class runs that function in <see cref="TinyX86"/> for every id 1..24192, which gives the 2,027 looping motions of the retail build.
/// Port of <c>tsuki.engine_tables.read_motion_loops</c>; the result equals the unicorn extraction in samples/misc_layer_state_and_motions.
/// </summary>
public sealed class MotionLoopTable
{
    /// <summary>The function's virtual address.</summary>
    public const long FunctionVa = 0xC1C70;

    /// <summary>The highest motion id.</summary>
    public const int MaxMotionId = 24192;

    private readonly Dictionary<int, int> _loops;

    private MotionLoopTable(Dictionary<int, int> loops)
    {
        _loops = loops;
    }

    /// <summary>The looping motion ids with their loop point (ids without a loop are absent).</summary>
    public IReadOnlyDictionary<int, int> Loops => _loops;

    /// <summary>Evaluates the function for every motion id.</summary>
    /// <exception cref="EbootFormatException">The function contains an instruction the interpreter does not support.</exception>
    public static MotionLoopTable Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        var cpu = new TinyX86(eboot.Read);
        Dictionary<int, int> loops = [];
        for (int id = 1; id <= eboot.Layout.MotionCount; id++)
        {
            uint v = cpu.Run(eboot.Layout.MotionLoopFunction, (ulong)id).Eax;
            if (v != 0)
            {
                loops[id] = checked((int)v);
            }
        }

        return new MotionLoopTable(loops);
    }

    /// <summary>The loop point of a motion id: 0 = no loop, 1 = the whole motion, n &gt; 1 = from n ms.</summary>
    public int LoopPoint(int motionId) => _loops.GetValueOrDefault(motionId);
}

/// <summary>
/// The F words the flowchart jump zeroes by column (knife <c>game.rs</c> <c>JUMP_CLEARS</c>, sub_95800): the PS4 function at VA 0xBA700 compares the
/// column with 203, 184, 176, 161, ... and stores zero into chapter words F054..F074. Evaluated with <see cref="TinyX86"/>. Port of
/// <c>tsuki.engine_tables.read_jump_clears</c>.
/// </summary>
public sealed class JumpClearTable
{
    /// <summary>The function's virtual address.</summary>
    public const long FunctionVa = 0xBA700;

    /// <summary>The F word array F000..F1199 (u16 each).</summary>
    public const long FWordsBase = 0x164B1D0;

    /// <summary>The last flowchart column (277 columns).</summary>
    public const int MaxRow = 276;

    private JumpClearTable(IReadOnlyList<(int LastRow, int Word)> pairs)
    {
        Pairs = pairs;
    }

    /// <summary>(last column, F word) pairs: a jump to a column at or before the last column zeroes the word. Sorted by descending column, then word.</summary>
    public IReadOnlyList<(int LastRow, int Word)> Pairs { get; }

    /// <summary>The F word numbers the jump to column <paramref name="row"/> zeroes.</summary>
    /// <exception cref="EbootFormatException">The function stores something else than zero into the F array.</exception>
    public static IReadOnlyList<int> WordsClearedAtRow(EbootReader eboot, int row)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        var cpu = new TinyX86(eboot.Read);
        List<int> words = [];
        foreach (X86Store s in cpu.Run(eboot.Layout.JumpClearFunction, (ulong)row).Stores)
        {
            long off = s.Address - eboot.Layout.FWordsBase;
            if (s.Value != 0 || off < 0 || (off & 1) != 0)
            {
                throw new EbootFormatException($"the jump-clear function at 0x{eboot.Layout.JumpClearFunction:x} stores 0x{s.Value:x} at 0x{s.Address:x}, not a zero into the F array");
            }

            for (int w = 0; w < s.Size / 2; w++)
            {
                words.Add((int)(off / 2) + w);
            }
        }

        return words;
    }

    /// <summary>Evaluates the function for every column and derives the (last column, word) pairs.</summary>
    /// <exception cref="EbootFormatException">A word is not cleared for every column up to its last one.</exception>
    public static JumpClearTable Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        Dictionary<int, List<int>> rowsOf = [];
        for (int row = 0; row <= MaxRow; row++)
        {
            foreach (int w in WordsClearedAtRow(eboot, row))
            {
                if (!rowsOf.TryGetValue(w, out List<int>? rows))
                {
                    rowsOf[w] = rows = [];
                }

                rows.Add(row);
            }
        }

        List<(int LastRow, int Word)> pairs = [];
        foreach ((int word, List<int> rows) in rowsOf)
        {
            int last = rows.Max();
            if (rows.Count != last + 1)
            {
                throw new EbootFormatException($"F{word:D3} is not cleared for every column up to {last}");
            }

            pairs.Add((last, word));
        }

        pairs.Sort(static (a, b) => a.LastRow != b.LastRow ? b.LastRow.CompareTo(a.LastRow) : a.Word.CompareTo(b.Word));
        return new JumpClearTable(pairs);
    }
}

/// <summary>
/// The three -1-terminated <c>i16</c> lists of F word numbers that survive a flowchart jump or are saved in a history record: kept whole
/// (<see cref="Words"/>), kept as a signed byte (<see cref="Bytes"/>) and kept as a flag (<see cref="Bits"/>). knife: <c>game.rs</c> <c>JUMP_KEEP_*</c> and
/// <c>save_tables.rs</c> <c>F_WORDS / F_BYTES / F_BITS</c>. The PS4 lists contain knife's numbers in the same order plus PS4-only variables
/// (967 in the words and bytes, 501 in the bytes, 500 in the bits). Port of <c>tsuki.engine_tables.read_flow_keep_lists</c>.
/// </summary>
/// <param name="Words">F words kept whole.</param>
/// <param name="Bytes">F words kept as a signed byte.</param>
/// <param name="Bits">F words kept as a flag.</param>
public sealed record FlowKeepLists(IReadOnlyList<int> Words, IReadOnlyList<int> Bytes, IReadOnlyList<int> Bits)
{
    /// <summary>Text VA of the flow-jump copy (words, bytes, bits), used by the functions at 0x2A080 and 0x2A1D0.</summary>
    public static IReadOnlyList<long> FlowJumpVa { get; } = [0x1A4250, 0x1A42A0, 0x1A4500];

    /// <summary>Text VA of the history-record copy (words, bytes, bits), used by History_Push / _Restore.</summary>
    public static IReadOnlyList<long> HistoryVa { get; } = [0x2160E0, 0x2165F0, 0x216580];

    private const int Limit = 1024;

    /// <summary>Reads the flow-jump copy (<paramref name="history"/> false) or the history-record copy (true).</summary>
    /// <exception cref="EbootFormatException">A list has no terminator.</exception>
    public static FlowKeepLists Read(EbootReader eboot, bool history = false)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        IReadOnlyList<long> at = history ? eboot.Layout.HistoryKeepLists : eboot.Layout.FlowJumpKeepLists;
        return new FlowKeepLists(ReadList(eboot, at[0]), ReadList(eboot, at[1]), ReadList(eboot, at[2]));
    }

    private static int[] ReadList(EbootReader eboot, long va)
    {
        List<int> list = [];
        while (true)
        {
            short v = BinaryPrimitives.ReadInt16LittleEndian(eboot.Read(va + (2 * list.Count), 2));
            if (v == -1)
            {
                return [.. list];
            }

            list.Add(v);
            if (list.Count > Limit)
            {
                throw new EbootFormatException($"the list at 0x{va:x} has no terminator");
            }
        }
    }
}
