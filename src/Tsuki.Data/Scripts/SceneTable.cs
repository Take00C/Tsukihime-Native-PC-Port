using System.Buffers.Binary;
using Tsuki.Data.Eboot;

namespace Tsuki.Data.Scripts;

/// <summary>
/// The scene-id table: the scene id of every allscr script, an i16 per script at text VA 0x216130 (docs/formats/script.md 2.5). <c>_JUMP(n)</c>
/// and the title menu address scripts by scene id; the engine maps an id to a script index by linear search (VA 0x792D0 / 0x79320). A port of
/// <c>script_tables.read_scene_ids</c> and <c>scene_index</c>.
/// </summary>
public sealed class SceneTable
{
    /// <summary>Scripts in the retail data.</summary>
    public const int RetailCount = 549;

    private readonly short[] _ids;

    private SceneTable(short[] ids)
    {
        _ids = ids;
    }

    /// <summary>The scene id of every script, by script index.</summary>
    public ReadOnlySpan<short> Ids => _ids;

    /// <summary>Number of scripts.</summary>
    public int Count => _ids.Length;

    /// <summary>Reads <paramref name="count"/> ids from the eboot.</summary>
    /// <exception cref="EbootFormatException">The table is not file-backed.</exception>
    public static SceneTable Read(EbootReader eboot, int? count = null)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        int n = count ?? eboot.Layout.SceneCount;
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        ReadOnlySpan<byte> raw = eboot.Read(eboot.Layout.SceneIds, 2 * n);
        short[] ids = new short[n];
        for (int i = 0; i < n; i++)
        {
            ids[i] = BinaryPrimitives.ReadInt16LittleEndian(raw[(2 * i)..]);
        }

        return new SceneTable(ids);
    }

    /// <summary>Builds a table from ids (tests).</summary>
    public static SceneTable FromIds(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return new SceneTable([.. ids.Select(i => checked((short)i))]);
    }

    /// <summary>The scene id of script <paramref name="index"/>.</summary>
    public int SceneId(int index) => _ids[index];

    /// <summary>The script index of a scene id: the first table entry that holds it (the engine's linear search), or null.</summary>
    public int? IndexOf(int sceneId)
    {
        for (int i = 0; i < _ids.Length; i++)
        {
            if (_ids[i] == sceneId)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>True when no scene id occurs twice (the retail table: all 549 are distinct).</summary>
    public bool IdsAreUnique() => _ids.Distinct().Count() == _ids.Length;
}
