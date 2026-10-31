namespace Tsuki.Engine.Save;

/// <summary>What a restore asks the BGM to do (kind-2 tail, VA 0x7D742).</summary>
public enum BgmRestoreAction : byte
{
    /// <summary>The record kind does not touch the music.</summary>
    None = 0,

    /// <summary>Start <see cref="RestoreOutcome.BgmTrack"/> at <see cref="RestoreOutcome.BgmVolume"/> (VA 0x98C0, then 0x9CE0).</summary>
    Start = 1,

    /// <summary>Stop the music (VA 0x9A90): the record's track is negative.</summary>
    Stop = 2,
}

/// <summary>
/// One loop-SE channel of a kind 3..5 restore (tail at VA 0x7D575): the channel restarts when its loop id is not
/// negative and <c>SeVolB</c> is above 0 (calls 0xAA80, 0xAD60, 0xA300, 0xA9E0, 0xAA50), otherwise it stops (0xAAB0).
/// </summary>
/// <param name="Channel">Channel 0..19.</param>
/// <param name="Restart">True to restart the loop, false to stop the channel.</param>
/// <param name="LoopSeId">The loop SE id (0x1654240[i]).</param>
/// <param name="Volume">Sign-extended byte 0x1654330[i] (argument of 0xAD60).</param>
/// <param name="Pitch">Sign-extended word 0x1654270[i] (the pitch argument of 0xA300, converted to float by the engine).</param>
/// <param name="ArrC">Word 0x16542D0[i] (argument of 0xA9E0).</param>
/// <param name="ArrD">Word 0x1654300[i] (argument of 0xAA50).</param>
public readonly record struct LoopSeRestore(int Channel, bool Restart, short LoopSeId, sbyte Volume, short Pitch, short ArrC, short ArrD);

/// <summary>
/// The side effects of one <see cref="AdvHistory.Restore"/> that are not variable copies (those are already in the
/// <see cref="AdvLiveState"/>): effect slots to stop, music and sound to restart, the stage to rebuild, the message
/// to re-display, the staging snapshot. The interpreter and the stage perform them (docs/formats/adv_state.md 3.1, 3.2).
/// </summary>
public sealed class RestoreOutcome
{
    internal RestoreOutcome(int index, ushort kind, bool fromHistory)
    {
        Index = index;
        Kind = kind;
        FromHistory = fromHistory;
    }

    /// <summary>Ring (or scene-stack) index that was restored.</summary>
    public int Index { get; }

    /// <summary>The record's kind (adv_state.md 2.3).</summary>
    public ushort Kind { get; }

    /// <summary>True for a rewind (the kind-0 re-display runs); false for loads, previous scene and backlog jumps.</summary>
    public bool FromHistory { get; }

    /// <summary>True when the record belongs to another script: <see cref="AdvLiveState.ScriptNumber"/> was set and the interpreter must load and decode that script.</summary>
    public bool ReloadScript { get; internal set; }

    /// <summary>Bit i = stop effect slot i (it was active and the record says it was not; VA 0x5C080).</summary>
    public ushort StopEffectSlots { get; internal set; }

    /// <summary>True when config +0x4C is set: the UI hook at VA 0x423D0 runs; config +0x04 (<c>LiveValue04</c>) already took the record's value when that is at most 0x100.</summary>
    public bool Config04Hook { get; internal set; }

    /// <summary>The music action of a kind-2 record.</summary>
    public BgmRestoreAction Bgm { get; internal set; }

    /// <summary>BGM track of a <see cref="BgmRestoreAction.Start"/>.</summary>
    public short BgmTrack { get; internal set; }

    /// <summary>BGM volume word of a <see cref="BgmRestoreAction.Start"/>.</summary>
    public short BgmVolume { get; internal set; }

    /// <summary>The 20 loop-SE channels of a kind 3..5 record; null for other kinds.</summary>
    public IReadOnlyList<LoopSeRestore>? LoopSe { get; internal set; }

    /// <summary>True for kinds 1 and 9: the stage rebuilds its layers from <see cref="AdvLiveState.Committed"/> (VA 0xE98D0, 0x7DD10 ...) and then returns to <see cref="AdvLiveState.Scene"/>.</summary>
    public bool RebuildFromCommitted { get; internal set; }

    /// <summary>True for kind 0 when the F967 rule reset F967 (VA 0x7D52C: also WaitFlags |= 0x200).</summary>
    public bool F967Reset { get; internal set; }

    /// <summary>True when a kind-0 restore found VA 0x86AA24 set and cleared it: the engine flushes pending effects (VA 0x75CD0(1)).</summary>
    public bool FlushPending { get; internal set; }

    /// <summary>True for kinds 6 and 7 when the record was the scene-stack top and the stack was popped (the scene counters were cleared).</summary>
    public bool ScenePopped { get; internal set; }

    /// <summary>For kind 7 (choice save point): the record's message text (up to the NUL) that the engine shows again (VA 0x7B790).</summary>
    public byte[]? ShowRecordText { get; internal set; }

    /// <summary>The kind-0 re-display of a rewind (null for other restores).</summary>
    public RedisplayInfo? Redisplay { get; internal set; }

    /// <summary>True when the restore refreshed the slot staging (Staging_SetSnapshot, VA 0x73980).</summary>
    public bool StagingUpdated { get; internal set; }
}

/// <summary>
/// The kind-0 re-display of a rewind (adv_state.md 3.2): the statement text was copied out of the script into
/// <see cref="AdvLiveState.RedisplayText"/>, and the engine lays it out again (name plate, message box) and resumes the
/// script at the record's <c>ResumeOffset</c>.
/// </summary>
/// <param name="Text">The statement text (after <c>;_ZMbbbll(</c>) up to <c>)</c>, <c>@e</c> or <c>@n</c> (at most 0x300 bytes).</param>
/// <param name="EndCode">0 = the statement ended at <c>)</c> or after 0x300 bytes, 1 = <c>@n</c> (no click wait), 2 = <c>@e</c> (or forced by the instant-page latch).</param>
/// <param name="NamePlate">The record's name-plate state (0x1C bytes) that the engine lays out as the name plate.</param>
public sealed record RedisplayInfo(byte[] Text, int EndCode, byte[] NamePlate);
