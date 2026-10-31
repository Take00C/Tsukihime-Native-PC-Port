using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Flow;

/// <summary>
/// The common processing of every marker <c>_Z&lt;k&gt;&lt;bbb&gt;&lt;ss&gt;</c> (docs/formats/script.md 3.7, engine.md 5.6):
/// the previous marker's line is recorded in <c>readmax</c> when the next marker is reached, then the new block / sub
/// become current and the "already read" test runs for them.
/// </summary>
/// <remarks>
/// <c>readmax</c> is the system block's table (+0x738, 4,020 bytes; RAM 0x17DA278). The engine bound is a signed upper
/// bound only (<c>prev_block &lt;= 0xFB3</c>); this port never writes outside the table (a negative block, possible only
/// with malformed digits, is not recorded) and treats the read test of a block outside 0..0xFB3 as reading 0 (the
/// engine reads the neighbouring memory there). Retail blocks are 0x00A..0xF82.
/// </remarks>
public sealed class MarkerState
{
    /// <summary>Highest block the engine records (<c>cmp edi, 0xFB3; jg</c>).</summary>
    public const int MaxRecordedBlock = 0xFB3;

    private readonly SystemBlock system;

    /// <summary>Creates the state over the system block that holds <c>readmax</c>.</summary>
    public MarkerState(SystemBlock system)
    {
        ArgumentNullException.ThrowIfNull(system);
        this.system = system;
    }

    /// <summary>The previous marker's block id (VA 0x1654344; 0 before the first marker).</summary>
    public int Block { get; private set; }

    /// <summary>The previous marker's sub id (VA 0x1654348).</summary>
    public int Sub { get; private set; }

    /// <summary>True once a marker was reached (word VA 0x1654354 = 1).</summary>
    public bool Seen { get; private set; }

    /// <summary>The "already read" result of the current marker.</summary>
    public bool IsRead { get; private set; }

    /// <summary>
    /// Sets the current ids without touching <c>readmax</c> (restoring a history record or a save) and recomputes the "already read" result for them:
    /// the engine computes it from the current ids each time (0x75C90, adv_state.md 6), so a line re-displayed by a rewind is read when <c>readmax</c>
    /// says so, whatever the newest marker was.
    /// </summary>
    public void Restore(int block, int sub)
    {
        Block = block;
        Sub = sub;
        Seen = true;
        IsRead = ReadNow(block, sub);
    }

    private bool ReadNow(int block, int sub)
    {
        int max = block >= 0 && block <= MaxRecordedBlock ? system.GetReadmax(block) : 0;
        return max != 0 && (sub <= max || block > MaxRecordedBlock);
    }

    /// <summary>
    /// Processes a marker with the decoded <paramref name="block"/> and <paramref name="sub"/> ids (script.md 3.7, in the
    /// engine's order) and returns its "already read" result.
    /// </summary>
    public bool Enter(int block, int sub)
    {
        if (Block >= 0 && Block <= MaxRecordedBlock && Sub > system.GetReadmax(Block))
        {
            system.SetReadmax(Block, unchecked((byte)Sub));
        }

        Block = block;
        Sub = sub;
        Seen = true;
        IsRead = ReadNow(block, sub);
        return IsRead;
    }
}
