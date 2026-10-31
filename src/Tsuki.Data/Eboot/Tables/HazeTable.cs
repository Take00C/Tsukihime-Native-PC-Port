using System.Buffers.Binary;

namespace Tsuki.Data.Eboot.Tables;

/// <summary>
/// The <c>_STRA</c> heat-haze parameters: 558 entries of 10 floats, indexed by the STRA id (entry 0 is the all-zero filler) in the text segment at
/// VA 0x219910 (accessor 0xB3630: ids 1..0x22D). knife keeps the same table as <c>exe.rs</c> <c>ExeTables::haze</c>; 16 of the 558 entries were retuned
/// for the PS4. Port of <c>tsuki.engine_tables.read_haze</c>.
/// </summary>
/// <remarks>
/// Field use (knife <c>stage.rs</c> <c>Haze::params</c>): [0] / [3] x and y scale, [2] / [5] amplitude factors, [6] amplitude, [7] period term,
/// [9] fade-in time in ms. The meaning on the PS4 is [H]: the engine advances the haze phase, but its draw was not traced (layers.md 4.3).
/// </remarks>
public sealed class HazeTable
{
    /// <summary>Text VA of entry 0.</summary>
    public const long Va = 0x219910;

    /// <summary>The highest STRA id that has an entry.</summary>
    public const int MaxId = 557;

    /// <summary>Floats per entry.</summary>
    public const int FloatsPerEntry = 10;

    private readonly float[] _values;

    private HazeTable(float[] values)
    {
        _values = values;
    }

    /// <summary>Number of entries, including entry 0.</summary>
    public int Count => _values.Length / FloatsPerEntry;

    /// <summary>The highest STRA id that has an entry (<see cref="Count"/> - 1).</summary>
    public int LastId => Count - 1;

    /// <summary>Reads the table.</summary>
    /// <exception cref="EbootFormatException">The table is not file-backed.</exception>
    public static HazeTable Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        int entries = eboot.Layout.HazeEntries;
        ReadOnlySpan<byte> raw = eboot.Read(eboot.Layout.Haze, 4 * FloatsPerEntry * entries);
        float[] values = new float[FloatsPerEntry * entries];
        for (int k = 0; k < values.Length; k++)
        {
            values[k] = BinaryPrimitives.ReadSingleLittleEndian(raw[(4 * k)..]);
        }

        return new HazeTable(values);
    }

    /// <summary>The raw entry <paramref name="index"/> (0..557), whatever its content.</summary>
    public ReadOnlySpan<float> Raw(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, LastId);
        return _values.AsSpan(index * FloatsPerEntry, FloatsPerEntry);
    }

    /// <summary>The entry of a STRA id; ids outside 1..557 have none (the engine's accessor returns NULL).</summary>
    public bool TryGetEntry(int straId, out ReadOnlySpan<float> entry)
    {
        if (straId >= 1 && straId <= LastId)
        {
            entry = Raw(straId);
            return true;
        }

        entry = default;
        return false;
    }
}

/// <summary>
/// The flowchart node (RTST bit) of each scene, <c>i32[527]</c> at text VA 0x228EF0, indexed by script index - 1 (knife <c>save_tables.rs</c>
/// <c>SCENE_NODE</c>; -1 = no node). The first script (scene 110) has none. Port of <c>tsuki.engine_tables.read_scene_node</c>.
/// </summary>
public sealed class SceneNodeTable
{
    /// <summary>Text VA.</summary>
    public const long Va = 0x228EF0;

    /// <summary>Number of entries.</summary>
    public const int Count = 527;

    private readonly int[] _nodes;

    private SceneNodeTable(int[] nodes)
    {
        _nodes = nodes;
    }

    /// <summary>The nodes by script index - 1.</summary>
    public ReadOnlySpan<int> Nodes => _nodes;

    /// <summary>Reads the table.</summary>
    /// <exception cref="EbootFormatException">The table is not file-backed.</exception>
    public static SceneNodeTable Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        int count = eboot.Layout.SceneNodeCount;
        ReadOnlySpan<byte> raw = eboot.Read(eboot.Layout.SceneNodes, 4 * count);
        int[] nodes = new int[count];
        for (int k = 0; k < count; k++)
        {
            nodes[k] = BinaryPrimitives.ReadInt32LittleEndian(raw[(4 * k)..]);
        }

        return new SceneNodeTable(nodes);
    }

    /// <summary>The node of the script with allscr index <paramref name="scriptIndex"/> (0-based), -1 when it has none (index 0 or out of range).</summary>
    public int NodeOfScriptIndex(int scriptIndex)
    {
        int k = scriptIndex - 1;
        return k >= 0 && k < _nodes.Length ? _nodes[k] : -1;
    }
}
