using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Save;

/// <summary>The result of one rewind step (<see cref="AdvHistory.RewindStep"/>).</summary>
public sealed class RewindResult
{
    internal RewindResult()
    {
    }

    /// <summary>True when the rewind moved to an earlier page (the PS4 calls VA 0x56730(1)); false when there was nothing earlier.</summary>
    public bool Stepped { get; internal set; }

    /// <summary>The ring index the step landed on (the cursor does not move when <see cref="Stepped"/> is false).</summary>
    public int Target { get; internal set; }

    /// <summary>Every restore the step performed, newest record first; the last one is the target.</summary>
    public List<RestoreOutcome> Restores { get; } = [];

    /// <summary>The scene re-sync (VA 0x7E4E0) after the target: the engine rebuilds the stage from the live scene / committed copy and restarts the music and loop SEs from the live variables.</summary>
    public bool Resync { get; internal set; }

    /// <summary>True when the undone cursor record was a kind-1 record: the engine flushes pending effects (VA 0x75CD0(1)).</summary>
    public bool FlushPending { get; internal set; }

    /// <summary>OR of the effect slots the restores stopped.</summary>
    public ushort StopEffectSlots => Restores.Aggregate((ushort)0, (a, r) => (ushort)(a | r.StopEffectSlots));
}

/// <summary>The result of a scene restore (previous scene, slot load, <c>0x7E790</c>).</summary>
public sealed class SceneRestoreResult
{
    internal SceneRestoreResult(RestoreOutcome restore)
    {
        Restore = restore;
    }

    /// <summary>The History_Restore of the scene record.</summary>
    public RestoreOutcome Restore { get; }

    /// <summary>The record's voice to replay when voice is enabled (VA 0x7EB19): voice id and the speaker's config row; the id is negative when there is none.</summary>
    public int ReplayVoiceId { get; internal set; }

    /// <summary>See <see cref="ReplayVoiceId"/>: the record's voice_character (the config row is looked up by VA 0x3D2D0).</summary>
    public short ReplayVoiceCharacter { get; internal set; }

    /// <summary>
    /// What the scene counters want after the restore: F967 = scene word +0x19A6 + 1 with the 0x495C0 / 0x5C380 calls, or F967 = 0
    /// (0x7EB72..0x7EBE1). True when the word was not negative.
    /// </summary>
    public bool SceneBoundValid { get; internal set; }
}

public sealed partial class AdvHistory
{
    // -- re-sync (VA 0x7E4E0) -----------------------------------------------------------------------------------------

    /// <summary>
    /// 0x7E4E0(index), run after the History_Restore of a rewind target, a backlog jump and a scene restore: the committed
    /// copy and the scene state take the record's, window words follow F931, and the engine restarts BGM and loop SEs from
    /// the live variables (VA 0x7E620) and rebuilds the stage layers.
    /// </summary>
    public void ResyncScene(int index, AdvLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        HistoryRecord rec = Record(index);
        live.SceneWord = rec.SceneWord;
        live.Committed.CopyFrom(rec.SceneCommitted);
        live.Scene.CopyFrom(rec.SceneState);
        short f931 = live.FSigned(AdvLiveState.FIndex.WindowStyle);
        if (f931 >= 0)
        {
            live.Scene.TextWindowB = f931;
        }

        live.Scene.TextWindowA = live.Scene.TextWindowB;
    }

    // -- rewind (adv_state.md 5.1) ----------------------------------------------------------------------------------

    /// <summary>True when a rewind step has somewhere to go (the newest record is not alone).</summary>
    public bool CanRewind => IsValid(Cur) && Count >= 2;

    /// <summary>Number of valid records in the ring (those between the oldest and <see cref="Cur"/>).</summary>
    public int Count
    {
        get
        {
            int n = Cur - Oldest;
            if (n <= -2)
            {
                return Wrapped ? n + RingCount + 1 : 0;
            }

            return Math.Max(0, n + 1);
        }
    }

    /// <summary>
    /// One step of the held rewind action (Adv_Rewind VA 0xA2020, after its wait checks pass): undoes the newest record
    /// unless it is a message / choice record, walks back to the previous page start (a kind-0 record with no blocking
    /// effect and F823 &lt;= 1), undoes every record in between (each with <c>History_Restore(j, 1)</c>), and restores
    /// the target with the re-display of its line. Nothing is re-executed; ordinary script execution continues from the
    /// target's <c>ResumeOffset</c>. The caller checks the pending-wait conditions of Adv_Rewind first.
    /// </summary>
    /// <param name="live">The live variables.</param>
    /// <param name="scripts">Decoded scripts for the re-display.</param>
    public RewindResult RewindStep(AdvLiveState live, ScriptBytesProvider scripts)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(scripts);
        var result = new RewindResult();
        int c = RewindCursor;
        if (IsValid(c))
        {
            ushort kind = Record(c).Kind;
            if (kind is not (Kinds.Message or Kinds.ChoiceStart or Kinds.ChoiceLine))
            {
                result.Restores.Add(Restore(c, true, live, scripts));
                result.FlushPending = kind == Kinds.LayerChange;
            }
        }

        // find the target t
        int t = c;
        while (true)
        {
            t--;
            if (t < 0)
            {
                if (!Wrapped)
                {
                    t = 0;
                    break;
                }

                t = RingCount - 1;
            }

            HistoryRecord r = Record(t);
            if (r.Serial < 0)
            {
                t = c;
                break;
            }

            if (r.EffectBlockingBits != 0 || r.Kind != Kinds.Message)
            {
                continue;
            }

            if (RecordF8(r, AdvLiveState.FIndex.PageMessage) <= 1)
            {
                break;
            }
        }

        // undo every command in between: j = c - 1 down to t + 1 (a signed compare, as the PS4 does: across a wrap
        // from the low indices to the high ones the loop does not run)
        for (int j = c - 1; t < j; j--)
        {
            result.Restores.Add(Restore(j, true, live, scripts));
        }

        if (!IsValid(t) || t == c)
        {
            result.Stepped = false;
            result.Target = c;
            return result;
        }

        RewindCursor = t;
        result.Restores.Add(Restore(t, true, live, scripts));
        ResyncScene(Cur, live);
        result.Stepped = true;
        result.Resync = true;
        result.Target = t;
        return result;
    }

    // -- previous / next scene (adv_state.md 5.2, 5.3) -----------------------------------------------------------------

    /// <summary>
    /// The pending scene-jump request (RAM 0x16541F8): 1 = next scene, -1 = previous scene, 0 = none. Set by
    /// <see cref="RequestSceneJump"/> after the confirmation dialog and consumed by <see cref="TakeSceneJumpRequest"/>.
    /// </summary>
    public int SceneJumpRequest { get; private set; }

    /// <summary>
    /// True while the next-scene skip runs (RAM 0x120A6D2): Adv_Update runs Script_Step in a loop with every wait cleared until a
    /// command ends the skip (<c>_ATSV</c>, an unread or flagged message, <c>_JUMP</c>, <c>_RDED</c> / <c>_RTMN</c> / <c>_RTM_</c>, <c>_TOMP</c>,
    /// <c>_END_</c>, <c>_SELD</c>; adv_state.md 5.2). The interpreter sets it when it takes a next-scene request.
    /// </summary>
    public bool NextSceneRunning { get; set; }

    /// <summary>
    /// 0x75420(direction): requests a next (1) or previous (-1) scene. A next-scene request is dropped while wait-flag bit 0x10 is set.
    /// </summary>
    public void RequestSceneJump(int direction, bool waitFlag10)
    {
        SceneJumpRequest = direction == 1 ? (waitFlag10 ? 0 : 1) : direction;
    }

    /// <summary>Returns the pending scene-jump request and clears it (VA 0xA2B40).</summary>
    public int TakeSceneJumpRequest()
    {
        int r = SceneJumpRequest;
        SceneJumpRequest = 0;
        return r;
    }

    /// <summary>
    /// Ends the next-scene skip at an <c>_ATSV</c> (VA 0x96CBD): returns true when it was running, in which case the engine restarts
    /// BGM and loop SEs from the live variables (VA 0x7E620) because the skip silenced them.
    /// </summary>
    public bool EndNextScene()
    {
        bool was = NextSceneRunning;
        NextSceneRunning = false;
        return was;
    }

    /// <summary>
    /// Previous scene (action 24, VA 0xA23F0..0xA25C4): restarts the current scene, or the one before it within its first
    /// two messages. The caller has done the confirmation dialog.
    /// </summary>
    /// <param name="live">The live variables.</param>
    /// <param name="effectBlockingBits">Bit i = effect slot i is blocking (VA 0x5C680).</param>
    /// <param name="waitFlag10">Bit 0x10 of the wait flags (VA 0x1653E00): set while a movie-like task holds the engine.</param>
    /// <param name="flushPending">True when the PS4 calls 0x75CD0(1) before restoring (out parameter for the interpreter).</param>
    /// <returns>The scene restore, or null when there is nothing to go back to.</returns>
    public SceneRestoreResult? PreviousScene(AdvLiveState live, ushort effectBlockingBits, bool waitFlag10, out bool flushPending)
    {
        ArgumentNullException.ThrowIfNull(live);
        flushPending = false;
        bool blocking = (effectBlockingBits & 0x3FFF) != 0;
        int top = StackTop;
        if (!blocking && waitFlag10)
        {
            if (top < 2)
            {
                return null;
            }

            flushPending = true;
            top--;
        }
        else
        {
            if (top <= 0)
            {
                return null;
            }

            if (SceneMessageCounter > 0)
            {
                if (top < 2)
                {
                    return null;
                }

                top--;
            }

            flushPending = true;
        }

        return RestoreSceneRecord(top, live);
    }

    /// <summary>True when the previous-scene action would restore something now (the mode panel button is lit).</summary>
    public bool CanGoToPreviousScene(ushort effectBlockingBits, bool waitFlag10)
    {
        bool blocking = (effectBlockingBits & 0x3FFF) != 0;
        if (!blocking && waitFlag10)
        {
            return StackTop >= 2;
        }

        return StackTop > 0 && !(SceneMessageCounter > 0 && StackTop < 2);
    }

    /// <summary>
    /// VA 0x7E790(n): restores scene-stack record <paramref name="n"/>. The ring record is restored in place when the ring
    /// still holds it (same serial), else the ring is rebuilt around the stack record. Afterwards the stack top is n-1.
    /// </summary>
    public SceneRestoreResult RestoreSceneRecord(int n, AdvLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        HistoryRecord r = StackRecord(n);
        int i = r.RingIndex;
        int restoreIndex;
        RestoreOutcome outcome;
        live.RedisplayLength = 0;
        live.Word1750646 = 0;
        bool inRing = (uint)i < RingCount && (i < Cur || Wrapped) && Record(i).Serial == r.Serial;
        if (inRing)
        {
            outcome = Restore(i, false, live);
            restoreIndex = i;
        }
        else
        {
            Clear(live);
            Record(0).CopyFrom(r);
            outcome = Restore(0, false, live);
            restoreIndex = 0;
        }

        StackTop = n - 1;
        live.WaitFlags = 0;
        live.WaitFlags70 = 0;
        ResyncScene(restoreIndex, live);
        HistoryRecord rec = Record(restoreIndex);
        var result = new SceneRestoreResult(outcome)
        {
            ReplayVoiceId = rec.VoiceId,
            ReplayVoiceCharacter = rec.VoiceCharacter,
            SceneBoundValid = live.Scene.F967Base >= 0,
        };

        if (live.Scene.F967Base >= 0)
        {
            live.Vars.SetWord(AdvLiveState.FIndex.SceneBase, (ushort)(live.Scene.F967Base + 1));
        }
        else
        {
            live.Vars.SetWord(AdvLiveState.FIndex.SceneBase, 0);
            live.WaitFlags |= 0x200;
        }

        SceneMessageCounter = 2;
        AfterLoad = 2;
        RewindCursor = Cur;
        return result;
    }

    // -- backlog (adv_state.md 5.4, 5.5) ----------------------------------------------------------------------------

    /// <summary>
    /// Backlog_Open (VA 0x3BFB0): the backlog is the history ring. Returns the ring indices of the messages of the earlier
    /// pages, newest first: records of kind 0, 8, 11 or 12 walking back from <see cref="Cur"/>, with trailing choice
    /// records dropped and the current page (everything before the first earlier page-end record, F799 == 1) removed.
    /// An empty list means there is nothing to show.
    /// </summary>
    public List<int> BacklogEntries()
    {
        var list = new List<int>();
        int n = Cur - Oldest;
        if (n <= -2)
        {
            if (!Wrapped)
            {
                return list;
            }

            n += RingCount + 1;
            if (n <= 0)
            {
                return list;
            }
        }
        else
        {
            n++;
            if (n <= 0)
            {
                return list;
            }
        }

        int i = Cur;
        for (; n > 0; n--)
        {
            ushort kind = Record(i).Kind;
            if (kind <= 12 && ((0x1901 >> kind) & 1) != 0)
            {
                list.Add(i);
            }

            i = Wrapped && i <= 0 ? i + 799 : i - 1;
        }

        if (list.Count <= 1)
        {
            list.Clear();
            return list;
        }

        // drop trailing kind-12 entries from the oldest end, keeping list[0]
        int count = list.Count;
        while (count > 1 && Record(list[count - 1]).Kind == Kinds.ChoiceStart)
        {
            count--;
        }

        list.RemoveRange(count, list.Count - count);
        if (list.Count <= 1)
        {
            list.Clear();
            return list;
        }

        int k = -1;
        for (int q = 1; q < list.Count; q++)
        {
            if (RecordF8(Record(list[q]), AdvLiveState.FIndex.LastMessage) == 1)
            {
                k = q;
                break;
            }
        }

        if (k < 0)
        {
            list.Clear();
            return list;
        }

        list.RemoveRange(0, k);
        return list;
    }

    /// <summary>
    /// Backlog jump (VA 0x3CC70, adv_state.md 5.5): restores the entry's record (<c>History_Restore(i, 0)</c>, so the
    /// command runs again) after discarding the scene starts of the newer records. The caller has stopped the music and
    /// voice. The next push overwrites the entry's ring slot.
    /// </summary>
    /// <param name="ringIndex">An index from <see cref="BacklogEntries"/>.</param>
    /// <param name="live">The live variables.</param>
    /// <returns>The restore, or null when the entry is not a valid jump target (the PS4 plays its error sound).</returns>
    public RestoreOutcome? BacklogJump(int ringIndex, AdvLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (ringIndex < 0 || ringIndex >= RingCount)
        {
            return null;
        }

        int i = ringIndex;
        if (Record(i).Kind == Kinds.ChoiceStart)
        {
            i = i >= 1 ? i - 1 : RingCount - 1;
            if (Record(i).Kind != Kinds.ChoiceLine)
            {
                return null;
            }
        }

        if (i != Cur)
        {
            int c = Cur;
            while (true)
            {
                if (c < 0)
                {
                    c = RingCount - 1;
                }

                HistoryRecord r = Record(c);
                if (r.Serial < 0)
                {
                    break;
                }

                if ((r.Kind & ~1) == 6 && StackTop >= 0 && StackRecord(StackTop).RingIndex == c)
                {
                    StackTop--;
                }

                c--;
                if (c == i)
                {
                    break;
                }
            }
        }

        live.WaitFlags = 0;
        live.WaitFlags70 = 0;
        live.Word1653E08 = 0;
        live.Var16541FC = -1;
        RestoreOutcome outcome = Restore(i, false, live);
        Next = Cur;
        RewindCursor = Cur;
        ResyncScene(i, live);
        return outcome;
    }

    // -- slot load (adv_state.md 7.3) ----------------------------------------------------------------------------------

    /// <summary>
    /// Loads a stored slot the way Slot_Load (VA 0xA2D10) does, not the scan-mode fallback: the slot becomes the staging,
    /// the ring and the live script variables are reset, state A becomes the scene-stack record n (state B record n-1),
    /// and 0x7E790(n) restores it. The saved line is not re-displayed: <see cref="AdvLiveState.ScriptOffset"/> is the
    /// record's <c>CmdOffset</c> and the page's first message statement runs again, pushing a new record over ring slot 0.
    /// </summary>
    /// <param name="slot">The slot to load (copied into the staging).</param>
    /// <param name="live">The live variables.</param>
    /// <exception cref="InvalidDataException">The slot lacks its end marker (the PS4 would scan-load an older save) or its state-B flag is out of range.</exception>
    public SceneRestoreResult LoadSlot(SlotImage slot, AdvLiveState live)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(live);
        if (slot.EndMarker != SlotImage.Magic)
        {
            throw new InvalidDataException("the slot has no end marker (a scan-mode save of an older build is not supported)");
        }

        if (slot.HasStateB is < 0 or > 1)
        {
            throw new InvalidDataException($"slot state-B flag {slot.HasStateB} is not 0 or 1");
        }

        Staging.CopyFrom(slot);
        live.Word1750646 = 0;
        Clear(live);
        for (int w = 0; w < Vars.ZWord + Vars.ZCount; w++)
        {
            live.Vars.SetWord(w, 0);
        }

        live.ResetSound();
        live.Scene.Word199A = 0;
        live.Scene.Word199C = 0;
        live.Scene.Word199E = 0;

        int n = Staging.HasStateB;
        HistoryRecord stackN = StackRecord(n);
        slot.StateA.Bytes.CopyTo(stackN.Bytes);
        if (n >= 1)
        {
            HistoryRecord below = StackRecord(n - 1);
            slot.StateB.CopyTo(below.Bytes);
            below.MessageText[0] = 0;
        }

        SceneRestoreResult result = RestoreSceneRecord(n, live);
        Cur = 0;
        Next = 0;
        RewindCursor = 0;
        SceneMessageCounter = 0;
        AfterLoad = 1;
        return result;
    }
}
