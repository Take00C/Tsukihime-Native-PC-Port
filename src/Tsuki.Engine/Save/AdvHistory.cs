using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Save;

/// <summary>
/// Loads the decoded bytes of a script (the text the history record's command offsets point into). Only a rewind to a
/// message needs it, to copy the statement text out for the re-display (adv_state.md 3.2).
/// </summary>
/// <param name="script">The script number (record +0x18).</param>
public delegate ReadOnlyMemory<byte> ScriptBytesProvider(ushort script);

/// <summary>
/// The history ring of 800 <see cref="HistoryRecord"/>s, the scene stack of 300, the slot staging and the counters the
/// PS4 keeps around them (docs/formats/adv_state.md 2-5, 7.1). <see cref="Push"/> is History_Push (VA 0x7AF90),
/// <see cref="Restore"/> is History_Restore (VA 0x7C6D0); rewind, previous scene, backlog and slot loading are in
/// <c>AdvHistory.Navigation.cs</c>. The class holds records and indices only: every live engine variable a record
/// captures lives in an <see cref="AdvLiveState"/> that the caller passes in.
/// </summary>
public sealed partial class AdvHistory
{
    /// <summary>Records in the ring (the PS4 allocates 808 and wraps at 800; VA 0x7B44C).</summary>
    public const int RingCount = 800;

    /// <summary>Records in the scene stack (300; SceneStack_Push shifts the stack down when it is full).</summary>
    public const int SceneStackCount = 300;

    /// <summary>The record kinds of adv_state.md 2.3.</summary>
    public static class Kinds
    {
        /// <summary>A message marker (<c>_ZM</c>): the rewind target and backlog entry.</summary>
        public const ushort Message = 0;

        /// <summary>The first layer change since the last commit.</summary>
        public const ushort LayerChange = 1;

        /// <summary>Music commands.</summary>
        public const ushort Music = 2;

        /// <summary>Sound-effect commands.</summary>
        public const ushort SoundEffect = 3;

        /// <summary>Scene start (<c>_ATSV</c>).</summary>
        public const ushort SceneStart = 6;

        /// <summary>Choice save point (<c>_ATSV(1)</c>).</summary>
        public const ushort ChoiceSavePoint = 7;

        /// <summary>Never pushed in v01.01; the backlog still lists it.</summary>
        public const ushort Unused8 = 8;

        /// <summary>Commit point of a pending layer change.</summary>
        public const ushort Commit = 9;

        /// <summary>Never pushed in v01.01; the backlog still lists it.</summary>
        public const ushort Unused11 = 11;

        /// <summary>Choice start (<c>_SELD</c>).</summary>
        public const ushort ChoiceStart = 12;

        /// <summary>Choice line (<c>_SELR</c>).</summary>
        public const ushort ChoiceLine = 13;
    }

    private readonly HistoryRecord?[] _ring = new HistoryRecord?[RingCount];
    private readonly HistoryRecord?[] _stack = new HistoryRecord?[SceneStackCount];

    /// <summary>Creates an empty history (equal to the state after the PS4 ring clear at boot).</summary>
    public AdvHistory()
    {
        Staging = new SlotImage();
    }

    // -- indices (RAM 0x120A6E8..0x120A6F2, 0x164B1C0, 0x174DE04/08) ---------------------------------------------------

    /// <summary>The i16 serial counter (RAM 0x120A6EA): the serial of the next pushed record.</summary>
    public short SerialCounter { get; set; }

    /// <summary>Index of the newest record (RAM 0x120A6EC, "cur").</summary>
    public int Cur { get; set; }

    /// <summary>Index the next push writes (RAM 0x120A6EE).</summary>
    public int Next { get; set; }

    /// <summary>True once the ring has wrapped (RAM 0x120A6F0).</summary>
    public bool Wrapped { get; set; }

    /// <summary>Index of the oldest valid record once the ring is full (RAM 0x120A6F2).</summary>
    public int Oldest { get; set; }

    /// <summary>The record the rewind is positioned on (RAM 0x120A6E8); follows <see cref="Cur"/> while not rewinding.</summary>
    public int RewindCursor { get; set; }

    /// <summary>Scene-stack top index, -1 = empty (RAM 0x164B1C0).</summary>
    public int StackTop { get; set; } = -1;

    /// <summary>
    /// Messages left before the scene counts as started (RAM 0x174DE04): 2 right after a scene start, 0 after a load,
    /// down by one per message marker (the interpreter calls <see cref="OnMessageShown"/>).
    /// </summary>
    public int SceneMessageCounter { get; set; }

    /// <summary>The "just loaded" word (RAM 0x174DE08): 1 right after a slot load, 2 after a scene restore, cleared by <c>_ATSV</c>.</summary>
    public int AfterLoad { get; set; }

    /// <summary>The slot staging (RAM 0x12031E0): the save image of the current page, kept current while playing (adv_state.md 7.1).</summary>
    public SlotImage Staging { get; }

    /// <summary>
    /// When true, a push reproduces the PS4 bug of record +0x36BC (adv_state.md 2.1): the live bytes 0x1654330 take the
    /// ring slot's old bytes and the record keeps them. The default saves the live bytes in the record, as a port should.
    /// The oracle tests set it.
    /// </summary>
    public bool Ps4StaleSeBytes { get; set; }

    // -- records -------------------------------------------------------------------------------------------------

    /// <summary>The ring record <paramref name="index"/> (0..799), created cleared on first use.</summary>
    public HistoryRecord Record(int index)
    {
        if ((uint)index >= RingCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"ring index must be in 0..{RingCount - 1}");
        }

        return _ring[index] ??= NewClearedRecord(index);
    }

    /// <summary>The newest record (<see cref="Cur"/>).</summary>
    public HistoryRecord Newest => Record(Cur);

    /// <summary>The scene-stack record <paramref name="index"/> (0..299).</summary>
    public HistoryRecord StackRecord(int index)
    {
        if ((uint)index >= SceneStackCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"scene stack index must be in 0..{SceneStackCount - 1}");
        }

        return _stack[index] ??= new HistoryRecord();
    }

    /// <summary>The scene-stack top record, or null when the stack is empty.</summary>
    public HistoryRecord? StackTopRecord => StackTop >= 0 ? StackRecord(StackTop) : null;

    private static HistoryRecord NewClearedRecord(int index)
    {
        var r = new HistoryRecord { RingIndex = index, Serial = -1 };
        return r;
    }

    /// <summary>
    /// The ring clear of VA 0x7AF10 / 0xA2D50: every record gets its own index and serial -1 and loses its message text
    /// and window bytes (other bytes stay, as on the PS4); the indices and the scene-stack top reset; the current
    /// message buffer is emptied.
    /// </summary>
    /// <param name="live">When given, its message buffer's first byte (RAM 0x174D9B0) is cleared.</param>
    public void Clear(AdvLiveState? live = null)
    {
        for (int i = 0; i < RingCount; i++)
        {
            if (_ring[i] is { } r)
            {
                r.RingIndex = i;
                r.Serial = -1;
                r.MessageText[0] = 0;
                r.Window164FD00[0] = 0;
                r.Window164FD40[0] = 0;
            }
        }

        Cur = 0;
        Next = 0;
        Wrapped = false;
        Oldest = 0;
        RewindCursor = 0;
        StackTop = -1;
        if (live is not null)
        {
            live.MessageText[0] = 0;
        }
    }

    /// <summary>True when ring record <paramref name="index"/> holds a pushed record (serial &gt;= 0).</summary>
    public bool IsValid(int index) => (uint)index < RingCount && Record(index).Serial >= 0;

    /// <summary>The index before <paramref name="index"/> in the ring, wrapping 0 to 799 only once the ring has wrapped.</summary>
    internal int Before(int index) => index > 0 ? index - 1 : (Wrapped ? RingCount - 1 : -1);

    // -- push ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// History_Push (VA 0x7AF90): writes the record at <see cref="Next"/> from the live variables and advances the
    /// ring. Nothing happens (false) in scan mode, while F929 or byte 0x16541F4 is set, or while F1102 is set for a
    /// kind other than 0 and 11. The command runs after the push: the record holds the state <i>before</i> it.
    /// </summary>
    /// <param name="live">The live variables the record captures.</param>
    /// <param name="cmdOffset">Offset of the pushing statement in the decoded script (Script_Step passes name - 2, the <c>;</c> before the <c>_</c>).</param>
    /// <param name="resumeOffset">Offset where a rewind resumes (the end of the statement).</param>
    /// <param name="kind">The record kind (<see cref="Kinds"/>).</param>
    /// <returns>True when a record was pushed.</returns>
    public bool Push(AdvLiveState live, ulong cmdOffset, ulong resumeOffset, ushort kind)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.ScanMode != 0 || live.F(AdvLiveState.FIndex.PushBlock) != 0 || live.Var16541F4 != 0)
        {
            return false;
        }

        if (live.F(AdvLiveState.FIndex.OnlyMessages) != 0 && kind != Kinds.Message && kind != Kinds.Unused11)
        {
            return false;
        }

        int k = Next;
        HistoryRecord rec = Record(k);
        live.RestoredFromHistory = 0;
        Cur = k;
        RewindCursor = k;

        rec.Serial = SerialCounter;
        short advanced = unchecked((short)(SerialCounter + 1));
        SerialCounter = advanced <= 0 ? (short)0 : advanced;

        rec.CmdOffset = cmdOffset;
        rec.ResumeOffset = resumeOffset;
        rec.Script = live.ScriptNumber;
        rec.TextBlock = unchecked((short)live.TextBlock);
        rec.TextLine = unchecked((short)live.TextLine);
        rec.Speaker = unchecked((sbyte)(byte)live.Speaker);
        rec.Var1654208 = unchecked((sbyte)(byte)live.Var1654208);
        rec.Kind = kind;
        rec.BgmTrack = live.BgmTrack;
        for (int i = 0; i < AdvLiveState.SoundChannels; i++)
        {
            rec.SetLoopSeIds(i, live.LoopSeIds[i]);
        }

        rec.SceneWord = live.SceneWord;

        // 0x7B283 / 0x7B2A2: a live write of the low byte of F995 into scene +0x19A3, copied with the block.
        live.Scene.F995Low = (byte)live.F(AdvLiveState.FIndex.Low995);
        rec.SceneState.CopyFrom(live.Scene);
        rec.SceneCommitted.CopyFrom(live.Committed);
        live.State174DAB0.CopyTo(rec.State174DAB0);
        rec.Var174D940 = live.Var174D940;

        CopyFLists(live, rec);

        rec.VoiceId = -1;
        rec.VoiceCharacter = -1;
        rec.EffectActiveBits = live.EffectActiveBits;
        rec.EffectBlockingBits = live.EffectBlockingBits;
        rec.Flags363A = live.LastMessageOfPage ? (ushort)1 : (ushort)0;
        rec.BgmVolume = live.BgmVolume;
        rec.Config04 = live.Config.LiveValue04;

        for (int i = 0; i < AdvLiveState.SoundChannels; i++)
        {
            rec.SetSeVolA(i, unchecked((sbyte)(byte)live.SeVolA[i]));
            rec.SetSeVolB(i, unchecked((sbyte)(byte)live.SeVolB[i]));
            rec.SetSeArrC(i, live.SeArrC[i]);
            rec.SetSeArrD(i, live.SeArrD[i]);
            if (Ps4StaleSeBytes)
            {
                live.SeArrE[i] = rec.GetSeArrEStale(i);
            }
            else
            {
                rec.SetSeArrEStale(i, live.SeArrE[i]);
            }

            ReadOnlySpan<byte> src = live.Channels.AsSpan(i * AdvLiveState.ChannelEntrySize, AdvLiveState.ChannelEntrySize);
            Span<byte> dst = rec.Channel(i);
            CopyChannelBytes(src, dst);
        }

        live.Window164FD00.CopyTo(rec.Window164FD00);
        live.Window164FD40.CopyTo(rec.Window164FD40);
        rec.MessageText[0] = 0;

        int next = k + 1;
        if (next >= RingCount)
        {
            next = 0;
            Wrapped = true;
        }

        Next = next;
        if (next == Oldest)
        {
            Oldest = next < RingCount - 1 ? next + 1 : 0;
        }

        Record(next).Serial = -1;
        return true;
    }

    /// <summary>The F lists of a push: u16 values, "not zero" bits and low bytes (adv_state.md 2.1).</summary>
    private static void CopyFLists(AdvLiveState live, HistoryRecord rec)
    {
        for (int k = 0; k < HistoryRecord.FU16Count && k < HistoryRecord.FU16Indices.Length; k++)
        {
            rec.SetFU16(k, live.Vars.GetWord(HistoryRecord.FU16Indices[k]));
        }

        for (int k = 0; k < HistoryRecord.FBoolIndices.Length; k++)
        {
            int idx = HistoryRecord.FBoolIndices[k];
            rec.SetFBoolBit(k, idx is >= 0 and <= MaxListedF && live.Vars.GetWord(idx) != 0);
        }

        for (int k = 0; k < HistoryRecord.FS8Indices.Length; k++)
        {
            int idx = HistoryRecord.FS8Indices[k];
            if (idx is >= 0 and <= MaxListedF)
            {
                rec.SetFS8(k, unchecked((sbyte)(byte)live.Vars.GetWord(idx)));
            }
        }
    }

    /// <summary>The highest F index the history lists use (the PS4 skips larger entries; 0x4B0).</summary>
    internal const int MaxListedF = 0x4B0;

    /// <summary>The channel-table bytes a record keeps: +0..+2, +4..+5 and +8..+0x15 (not +3, +6..+7, +0x16..+0x17).</summary>
    internal static void CopyChannelBytes(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        src[..3].CopyTo(dst);
        src.Slice(4, 2).CopyTo(dst[4..]);
        src.Slice(8, 14).CopyTo(dst[8..]);
    }

    // -- follow-up writes of the commands that push ----------------------------------------------------------------------

    /// <summary>
    /// After a push, the message commands (<c>_ZM</c> 0x92380, <c>_MSAD</c> 0x92B60, 0x7B5C0) copy the current message
    /// buffer into the newest record and clear its tail byte +0x39F6; <c>_ATSV</c> does the same (adv_state.md 2.5).
    /// </summary>
    /// <param name="text">The text (at most 0xF8 bytes are kept; the rest of the 0xFA bytes is zeroed).</param>
    public void SetNewestMessageText(ReadOnlySpan<byte> text)
    {
        Span<byte> dst = Newest.MessageText;
        dst.Clear();
        text[..Math.Min(text.Length, 0xF8)].CopyTo(dst);
    }

    /// <summary>
    /// Voice play stores the line's voice, the speaker's config row and the speaker byte in the newest record (not while
    /// <see cref="AdvLiveState.RestoredFromHistory"/> is set, i.e. for a line re-displayed by a rewind; the caller checks).
    /// </summary>
    public void SetNewestVoice(int voiceId, short voiceCharacter, sbyte speaker)
    {
        HistoryRecord r = Newest;
        r.VoiceId = voiceId;
        r.VoiceCharacter = voiceCharacter;
        r.Speaker = speaker;
    }

    /// <summary>
    /// Messages count down the scene-start counter: the <c>_ZM</c> handler decrements it (VA 0x92414) while it is above 0.
    /// "Previous scene" uses it to tell whether fewer than two messages were shown since the scene started.
    /// </summary>
    public void OnMessageShown()
    {
        if (SceneMessageCounter > 0)
        {
            SceneMessageCounter--;
        }
    }

    // -- scene stack -----------------------------------------------------------------------------------------------

    /// <summary>
    /// SceneStack_Push (VA 0x7E2E0), called by <c>_ATSV</c> after its kind-6 / kind-7 push: copies the newest record onto
    /// the scene stack (shifting the stack down by one when it is full) and sets the scene-message counter to 2.
    /// Nothing happens while any effect slot is blocking.
    /// </summary>
    /// <param name="effectBlockingBits">Bit i = effect slot i is blocking (OR of VA 0x5C680(i) for i &lt; 14).</param>
    /// <returns>True when the record was pushed.</returns>
    public bool PushSceneStack(ushort effectBlockingBits)
    {
        if ((effectBlockingBits & 0x3FFF) != 0)
        {
            return false;
        }

        if (StackTop >= SceneStackCount - 1)
        {
            for (int i = 0; i < SceneStackCount - 1; i++)
            {
                StackRecord(i).CopyFrom(StackRecord(i + 1));
            }

            StackTop = SceneStackCount - 1;
        }
        else
        {
            StackTop++;
        }

        StackRecord(StackTop).CopyFrom(Newest);
        SceneMessageCounter = 2;
        return true;
    }

    // -- staging -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Staging_SetSnapshot with the caller condition of adv_state.md 7.1: the record is copied into the slot staging only
    /// when its effect_blocking_bits are 0, its kind is not 11 or 12, and the live F823 is at most 1 (the first message of a
    /// page). The scene-stack top becomes state B. Called by the message and <c>_ATSV</c> commands (with the newest
    /// record) and at the end of every restore.
    /// </summary>
    /// <returns>True when the staging was updated.</returns>
    public bool UpdateStaging(HistoryRecord record, AdvLiveState live)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(live);
        if (record.EffectBlockingBits != 0 || record.Kind is Kinds.Unused11 or Kinds.ChoiceStart
            || live.FSigned(AdvLiveState.FIndex.PageMessage) > 1)
        {
            return false;
        }

        Staging.SetSnapshot(record, StackTop + 1, StackTopRecord);
        return true;
    }

    /// <summary>The record's copy of an F variable kept in the s8 list (VA 0x765C0), or 0 when the list does not hold it.</summary>
    public static int RecordF8(HistoryRecord record, int fIndex)
    {
        ArgumentNullException.ThrowIfNull(record);
        int k = HistoryRecord.FS8Indices.IndexOf(fIndex);
        return k < 0 ? 0 : record.GetFS8(k);
    }
}
