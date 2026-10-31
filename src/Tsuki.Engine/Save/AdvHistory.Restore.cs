using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Save;

public sealed partial class AdvHistory
{
    /// <summary>
    /// History_Restore (VA 0x7C6D0): copies ring record <paramref name="index"/> back into the live variables (the
    /// common part, adv_state.md 3.1), runs the tail of its kind (2.3, 3.2), moves <see cref="Cur"/> / <see cref="Next"/>
    /// to it and refreshes the slot staging. The command at the record's <c>CmdOffset</c> runs again: afterwards
    /// <see cref="AdvLiveState.ScriptOffset"/> is that offset, except for a rewind to a message
    /// (<paramref name="fromHistory"/>, kind 0), which re-displays the line and resumes at <c>ResumeOffset</c>.
    /// </summary>
    /// <param name="index">Ring index 0..799.</param>
    /// <param name="fromHistory">True only for rewind steps; loads, previous scene and backlog jumps pass false.</param>
    /// <param name="live">The live variables to overwrite.</param>
    /// <param name="scripts">Gives the decoded script of the record; needed only for a rewind to a message (kind 0, <paramref name="fromHistory"/>).</param>
    /// <returns>The side effects the interpreter and the stage perform.</returns>
    public RestoreOutcome Restore(int index, bool fromHistory, AdvLiveState live, ScriptBytesProvider? scripts = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        HistoryRecord rec = Record(index);
        ushort kind = rec.Kind;
        if (fromHistory && kind == Kinds.Message && scripts is null)
        {
            throw new ArgumentException("a rewind to a message needs the script bytes for the re-display", nameof(scripts));
        }

        var o = new RestoreOutcome(index, kind, fromHistory);

        // ---- common part (3.1) -------------------------------------------------------------------------------
        live.Word1750646 = 0;
        rec.MessageText.CopyTo(live.MessageText);
        live.TextFade = 1.0f;
        live.Var1653E24 = 0;
        live.ScriptOffset = rec.CmdOffset;
        live.TextBlock = rec.TextBlock;
        live.TextLine = rec.TextLine;
        live.Speaker = unchecked((ushort)(short)rec.Speaker);
        live.Var1654208 = unchecked((ushort)(short)rec.Var1654208);
        live.Var165420A = unchecked((ushort)(short)rec.Var165420A);
        rec.State174DAB0.CopyTo(live.State174DAB0);
        live.Var174D940 = rec.Var174D940;
        live.BgmTrack = rec.BgmTrack;
        live.BgmVolume = rec.BgmVolume;
        for (int i = 0; i < AdvLiveState.SoundChannels; i++)
        {
            live.LoopSeIds[i] = rec.GetLoopSeIds(i);
            live.SeVolA[i] = unchecked((ushort)(short)rec.GetSeVolA(i));
            live.SeVolB[i] = unchecked((ushort)(short)rec.GetSeVolB(i));
            live.SeArrC[i] = rec.GetSeArrC(i);
            live.SeArrD[i] = rec.GetSeArrD(i);
            live.SeArrE[i] = rec.GetSeArrEStale(i);
            CopyChannelBytes(rec.Channel(i), live.Channels.AsSpan(i * AdvLiveState.ChannelEntrySize, AdvLiveState.ChannelEntrySize));
        }

        live.SceneWord = rec.SceneWord;
        live.Scene.CopyFrom(rec.SceneState);
        live.VoiceId = rec.VoiceId;
        live.Var16541FC = rec.VoiceId;
        live.Var1654200 = rec.VoiceId;

        // effect slots that were not active in the record are stopped (VA 0x5C080)
        ushort recordActive = rec.EffectActiveBits;
        for (int s = 0; s < 14; s++)
        {
            int bit = 1 << s;
            if ((recordActive & bit) == 0 && (live.EffectActiveBits & bit) != 0)
            {
                o.StopEffectSlots |= (ushort)bit;
            }
        }

        live.EffectActiveBits = (ushort)(live.EffectActiveBits & ~o.StopEffectSlots);

        if (live.Config.RestoreLiveValueGate != 0)
        {
            o.Config04Hook = true;
            if (rec.Config04 <= 0x100)
            {
                live.Config.LiveValue04 = rec.Config04;
            }
        }

        if (rec.Script != live.ScriptNumber)
        {
            live.ScriptNumber = rec.Script;
            o.ReloadScript = true;
        }

        // F lists: u16, then s8, then bool (the later list wins where an index is in two)
        for (int k = 0; k < HistoryRecord.FU16Indices.Length; k++)
        {
            int idx = HistoryRecord.FU16Indices[k];
            if (idx is >= 0 and <= MaxListedF)
            {
                live.Vars.SetWord(idx, rec.GetFU16(k));
            }
        }

        for (int k = 0; k < HistoryRecord.FS8Indices.Length; k++)
        {
            int idx = HistoryRecord.FS8Indices[k];
            if (idx is >= 0 and <= MaxListedF)
            {
                live.Vars.SetWord(idx, unchecked((ushort)(short)rec.GetFS8(k)));
            }
        }

        for (int k = 0; k < HistoryRecord.FBoolIndices.Length; k++)
        {
            int idx = HistoryRecord.FBoolIndices[k];
            if (idx is >= 0 and <= MaxListedF)
            {
                live.Vars.SetWord(idx, rec.GetFBoolBit(k) ? (ushort)1 : (ushort)0);
            }
        }

        rec.Window164FD00.CopyTo(live.Window164FD00);
        if (kind != Kinds.Message)
        {
            live.Window164FD40[0] = 0;
        }
        else
        {
            rec.Window164FD40.CopyTo(live.Window164FD40);
        }

        int plate = live.Window164FD40[0] != 0 ? 1 : 0;
        live.Var16541EC = plate;
        live.Var16541F0 = plate;

        Cur = index;
        Next = index + 1;
        if (Next >= RingCount)
        {
            Next = 0;
            Wrapped = true;
        }

        live.Var16541E4 = unchecked((sbyte)live.Scene.Byte19A4);

        // F967 rule (0x7D513): kept only when it equals scene word +0x19A6 + 1; values <= 0 are kept
        short f967 = live.FSigned(AdvLiveState.FIndex.SceneBase);
        if (f967 > 0 && live.Scene.F967Base + 1 != f967)
        {
            live.Vars.SetWord(AdvLiveState.FIndex.SceneBase, 0);
            live.WaitFlags |= 0x200;
            o.F967Reset = true;
        }

        // ---- kind tails (2.3, 3.2) ---------------------------------------------------------------------------
        switch (kind)
        {
            case Kinds.Message:
                RestoreMessageTail(rec, live, fromHistory, scripts, o);
                break;
            case Kinds.LayerChange:
            case Kinds.Commit:
                live.Committed.CopyFrom(rec.SceneCommitted);
                live.SceneWord = rec.SceneWord;
                o.RebuildFromCommitted = true;
                break;
            case Kinds.Music:
                if (live.BgmTrack < 0)
                {
                    o.Bgm = BgmRestoreAction.Stop;
                }
                else
                {
                    o.Bgm = BgmRestoreAction.Start;
                    o.BgmTrack = live.BgmTrack;
                    o.BgmVolume = live.BgmVolume;
                }

                break;
            case 3:
            case 4:
            case 5:
                o.LoopSe = LoopSeRestores(live);
                break;
            case Kinds.SceneStart:
                PopSceneStackIf(index, o);
                break;
            case Kinds.ChoiceSavePoint:
                PopSceneStackIf(index, o);
                o.ShowRecordText = NulTerminated(rec.MessageText);
                break;
            default:
                break;
        }

        if (rec.EffectBlockingBits == 0 && kind is not (Kinds.Unused11 or Kinds.ChoiceStart) && live.FSigned(AdvLiveState.FIndex.PageMessage) <= 1)
        {
            Staging.SetSnapshot(rec, StackTop + 1, StackTopRecord);
            o.StagingUpdated = true;
        }

        return o;
    }

    private void PopSceneStackIf(int index, RestoreOutcome o)
    {
        if (StackTop >= 0 && StackRecord(StackTop).RingIndex == index)
        {
            StackTop--;
            SceneMessageCounter = 0;
            AfterLoad = 0;
            o.ScenePopped = true;
        }
    }

    private static byte[] NulTerminated(ReadOnlySpan<byte> text)
    {
        int n = text.IndexOf((byte)0);
        return text[..(n < 0 ? text.Length : n)].ToArray();
    }

    private static LoopSeRestore[] LoopSeRestores(AdvLiveState live)
    {
        var list = new LoopSeRestore[AdvLiveState.SoundChannels];
        for (int i = 0; i < list.Length; i++)
        {
            short id = live.LoopSeIds[i];
            bool restart = id >= 0 && unchecked((short)live.SeVolB[i]) > 0;
            list[i] = new LoopSeRestore(i, restart, id, unchecked((sbyte)live.SeArrE[i]), unchecked((short)live.SeVolA[i]), live.SeArrC[i], live.SeArrD[i]);
        }

        return list;
    }

    /// <summary>The kind-0 tail (VA 0x7D6F2, adv_state.md 3.2).</summary>
    private void RestoreMessageTail(HistoryRecord rec, AdvLiveState live, bool fromHistory, ScriptBytesProvider? scripts, RestoreOutcome o)
    {
        if (live.Var86AA24 != 0)
        {
            o.FlushPending = true;
            live.Var86AA24 = 0;
        }

        live.RedisplayLength = 0;
        live.Var174DE00 = 0;
        short f931 = live.FSigned(AdvLiveState.FIndex.WindowStyle);
        if (f931 >= 0)
        {
            live.Scene.TextWindowB = f931;
        }

        live.Scene.TextWindowA = live.Scene.TextWindowB;
        if (!fromHistory)
        {
            return;
        }

        // re-display of the line (rewind only)
        live.RestoredFromHistory = 1;
        live.Var1654208 = 0;
        live.Var16541F4 = 0;
        ReadOnlyMemory<byte> script = scripts!(rec.Script);
        (byte[] text, int end, bool reachedEnd) = ExtractStatementText(script.Span, rec.CmdOffset);
        text.CopyTo(live.RedisplayText, 0);
        if (text.Length < live.RedisplayText.Length)
        {
            live.RedisplayText[text.Length] = 0;
        }

        live.RedisplayLength = (ushort)text.Length;
        live.RedisplayText.CopyTo(live.RedisplayCopy, 0);

        byte[] namePlate = rec.Window164FD40.ToArray();
        live.Window164FD40.CopyTo(live.Window164FD60, 0);
        live.Window164FD40[0] = 0;

        live.Scene.TextWindowA = live.Scene.TextWindowB;
        live.Var16541D8 = 1;
        live.Var16541FC = -1;
        live.WaitFlags70 |= 1;

        if (reachedEnd && live.PageEndInstantLatch && live.FSigned(AdvLiveState.FIndex.LastMessage) != 1)
        {
            end = 2;
        }

        if (end == 1)
        {
            live.Word1653E08 = 0;
            live.Var16541F4 = 1;
        }
        else
        {
            live.WaitFlags |= (end == 2 ? 0x140UL : 0x500UL) | live.WaitFlags70;
        }

        live.TextBlock = live.TextBlock2 = rec.TextBlock;
        live.TextLine = live.TextLine2 = rec.TextLine;
        live.Var1654200 = -1;
        live.ScriptOffset = rec.ResumeOffset;
        o.Redisplay = new RedisplayInfo(text, end, namePlate);
    }

    /// <summary>
    /// Copies the statement text of the kind-0 re-display out of the script (VA 0x7D8B3): from <c>cmdOffset + 0xA</c>
    /// (after <c>;_ZMbbbll(</c>) up to <c>)</c>, <c>@e</c> or <c>@n</c>, at most 0x300 bytes. Bytes past the end of the
    /// script read as 0.
    /// </summary>
    /// <returns>The text, the end code (1 = <c>@n</c>, 2 = <c>@e</c>, else 0) and whether the text ended at <c>)</c> or at 0x300 bytes.</returns>
    public static (byte[] Text, int EndCode, bool ReachedEnd) ExtractStatementText(ReadOnlySpan<byte> script, ulong cmdOffset)
    {
        byte[] buf = new byte[AdvLiveState.RedisplayBufferSize];
        int n = 0;
        int end = 0;
        bool reached = true;
        long start = checked((long)cmdOffset) + 0xA;
        for (; n < buf.Length; n++)
        {
            byte c = At(script, start + n);
            if (c == (byte)'@')
            {
                byte d = At(script, start + n + 1);
                if (d == (byte)'e')
                {
                    end = 2;
                    reached = false;
                    break;
                }

                if (d == (byte)'n')
                {
                    end = 1;
                    reached = false;
                    break;
                }
            }
            else if (c == (byte)')')
            {
                break;
            }

            buf[n] = c;
        }

        return (buf[..n], end, reached);
    }

    private static byte At(ReadOnlySpan<byte> s, long i) => i >= 0 && i < s.Length ? s[(int)i] : (byte)0;
}
