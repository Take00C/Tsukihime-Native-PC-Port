using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.Save;

/// <summary>The history ring, rewind, previous scene, the scene stack and the backlog on synthetic play sessions.</summary>
public sealed class AdvHistoryTests
{
    private const ushort Msg = AdvHistory.Kinds.Message;

    private static byte[] Script(params (int Offset, string Line)[] lines)
    {
        byte[] s = new byte[0x1000];
        Array.Fill(s, (byte)' ');
        foreach ((int offset, string line) in lines)
        {
            byte[] t = Encoding.ASCII.GetBytes($";_ZM00a01({line}@e);");
            t.CopyTo(s, offset);
        }

        return s;
    }

    private static bool Push(AdvHistory h, AdvLiveState live, ushort kind, ulong offset, int f823 = 1)
    {
        live.Vars.SetWord(AdvLiveState.FIndex.PageMessage, (ushort)f823);
        return h.Push(live, offset, offset + 0x20, kind);
    }

    [Fact]
    public void PushThenRestoreBringsBackTheLiveVariables()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        live.ScriptNumber = 9;
        live.TextBlock = 0x123;
        live.TextLine = 4;
        live.BgmTrack = 33;
        live.BgmVolume = -5;
        live.LoopSeIds[3] = 77;
        live.SeVolB[3] = 5;
        live.Scene.TextWindowB = 6;
        live.Vars.SetWord(959 - 959 + 900, 0x1234);
        live.Vars.SetWord(110, 0xBEEF);          // in the u16 list
        live.Vars.SetWord(299 - 299 + 298, 0xFF85);   // low byte kept as a signed byte
        Assert.True(Push(h, live, AdvHistory.Kinds.Music, 0x40));

        live.BgmTrack = 5;
        live.LoopSeIds[3] = -1;
        live.Vars.SetWord(110, 0);
        live.Vars.SetWord(298, 0);
        RestoreOutcome o = h.Restore(0, false, live);

        Assert.Equal(33, live.BgmTrack);
        Assert.Equal(77, live.LoopSeIds[3]);
        Assert.Equal(0xBEEF, live.Vars.GetWord(110));
        Assert.Equal(0xFF85, live.Vars.GetWord(298));      // sign-extended low byte 0x85
        Assert.Equal(BgmRestoreAction.Start, o.Bgm);
        Assert.Equal(33, o.BgmTrack);
        Assert.Equal(0x40UL, live.ScriptOffset);
        Assert.Equal(0, h.Cur);
        Assert.Equal(1, h.Next);
        Assert.False(o.ReloadScript);
    }

    [Fact]
    public void PushRespectsItsGates()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState { ScanMode = 1 };
        Assert.False(Push(h, live, Msg, 0x10));
        live.ScanMode = 0;
        live.Vars.SetWord(AdvLiveState.FIndex.PushBlock, 1);
        Assert.False(h.Push(live, 0, 0, Msg));
        live.Vars.SetWord(AdvLiveState.FIndex.PushBlock, 0);
        live.Var16541F4 = 1;
        Assert.False(h.Push(live, 0, 0, Msg));
        live.Var16541F4 = 0;
        live.Vars.SetWord(AdvLiveState.FIndex.OnlyMessages, 1);
        Assert.False(h.Push(live, 0, 0, AdvHistory.Kinds.SoundEffect));
        Assert.True(h.Push(live, 0, 0, Msg));
        Assert.True(h.Push(live, 0, 0, 11));
        Assert.Equal(2, h.Next);
        Assert.Equal(-1, h.Record(2).Serial);
    }

    [Fact]
    public void RingWrapsAtEightHundredAndMovesTheOldestIndex()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        for (int i = 0; i < 850; i++)
        {
            Assert.True(h.Push(live, (ulong)i, (ulong)i, Msg));
        }

        Assert.True(h.Wrapped);
        Assert.Equal(49, h.Cur);                     // 850 pushes -> index 849 mod 800
        Assert.Equal(50, h.Next);
        Assert.Equal(51, h.Oldest);
        Assert.Equal(-1, h.Record(50).Serial);
        Assert.Equal(849UL, h.Record(49).CmdOffset);
        Assert.Equal(799, h.Count);
        Assert.Equal(849, h.Record(49).Serial);
        Assert.True(h.IsValid(51));
    }

    [Fact]
    public void SerialCounterWrapsToZeroAtItsLimit()
    {
        var h = new AdvHistory { SerialCounter = 0x7FFE };
        var live = new AdvLiveState();
        h.Push(live, 0, 0, Msg);
        Assert.Equal(0x7FFE, h.Record(0).Serial);
        Assert.Equal(0x7FFF, h.SerialCounter);
        h.Push(live, 0, 0, Msg);
        Assert.Equal(0x7FFF, h.Record(1).Serial);
        Assert.Equal(0, h.SerialCounter);
    }

    [Fact]
    public void TheStaleSeBytesFollowTheOption()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        live.SeArrE[2] = 0x55;
        h.Push(live, 0, 0, Msg);
        Assert.Equal(0x55, h.Record(0).GetSeArrEStale(2));          // the port saves the live bytes
        Assert.Equal(0x55, live.SeArrE[2]);

        var ps4 = new AdvHistory { Ps4StaleSeBytes = true };
        ps4.Record(0).SetSeArrEStale(2, 0x11);
        ps4.Push(live, 0, 0, Msg);
        Assert.Equal(0x11, live.SeArrE[2]);                         // the PS4 overwrites the live bytes with the old record's
        Assert.Equal(0x11, ps4.Record(0).GetSeArrEStale(2));
    }

    [Fact]
    public void RewindWalksBackOnePageAtATime()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        byte[] script = Script((0x20, "Line one"), (0x50, "Line three"), (0x80, "Line four"), (0xB0, "Line five"));
        Push(h, live, AdvHistory.Kinds.SceneStart, 0x10);          // 0
        Push(h, live, Msg, 0x20, f823: 1);                        // 1  page 1
        Push(h, live, Msg, 0x50, f823: 2);                        // 2
        live.BgmTrack = 7;
        Push(h, live, AdvHistory.Kinds.Music, 0x68);               // 3
        live.BgmTrack = 9;
        Push(h, live, Msg, 0x80, f823: 1);                        // 4  page 2
        Push(h, live, Msg, 0xB0, f823: 2);                        // 5
        Assert.Equal(5, h.Cur);
        Assert.Equal(5, h.RewindCursor);

        RewindResult r1 = h.RewindStep(live, _ => script);
        Assert.True(r1.Stepped);
        Assert.Equal(4, r1.Target);                                // page 2's first message
        Assert.Equal([4], r1.Restores.Select(x => x.Index).ToArray());
        Assert.Equal(4, h.RewindCursor);
        Assert.Equal(0x80UL + 0x20, live.ScriptOffset);            // resumes after the re-displayed statement
        Assert.StartsWith("Line four", Encoding.ASCII.GetString(live.RedisplayText, 0, 9));
        Assert.Equal(1, live.RestoredFromHistory);
        Assert.Equal(9, live.BgmTrack);                            // record 4 was pushed after the music command ran
        Assert.NotNull(r1.Restores[^1].Redisplay);

        RewindResult r2 = h.RewindStep(live, _ => script);
        Assert.True(r2.Stepped);
        Assert.Equal(1, r2.Target);                                // back over the music record (3) and message 2
        Assert.Equal([3, 2, 1], r2.Restores.Select(x => x.Index).ToArray());
        Assert.Equal(BgmRestoreAction.Start, r2.Restores[0].Bgm);   // undoing the music command restarts the old track 7
        Assert.Equal(7, r2.Restores[0].BgmTrack);
        Assert.Equal(-1, live.BgmTrack);                             // record 1 was pushed before any music
        Assert.StartsWith("Line one", Encoding.ASCII.GetString(live.RedisplayText, 0, 8));
        Assert.Equal(1, h.Cur);
        Assert.Equal(2, h.Next);

        RewindResult r3 = h.RewindStep(live, _ => script);        // only the scene start is left: it is the oldest record
        Assert.True(r3.Stepped);
        Assert.Equal(0, r3.Target);
        RewindResult r4 = h.RewindStep(live, _ => script);
        Assert.False(r4.Stepped);
    }

    [Fact]
    public void RewindStopsAtRecordsWithBlockingEffects()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        byte[] script = Script((0x20, "A"), (0x50, "B"), (0x80, "C"));
        Push(h, live, Msg, 0x20, 1);                               // 0
        live.EffectBlockingBits = 0x4;
        Push(h, live, Msg, 0x50, 1);                               // 1: an effect was blocking: not a stop
        live.EffectBlockingBits = 0;
        Push(h, live, Msg, 0x80, 1);                               // 2
        RewindResult r = h.RewindStep(live, _ => script);
        Assert.Equal(0, r.Target);
        Assert.Equal([1, 0], r.Restores.Select(x => x.Index).ToArray());
    }

    [Fact]
    public void RewindAcrossTheWrapDoesNotUndoTheRecordsBetween()
    {
        // The PS4 compares the signed indices of the walk: with the cursor at 1 and the target at 799 the loop over the records
        // in between does not run (Adv_Rewind 0xA22E0..0xA22F5); only the cursor record and the target are restored.
        // The oracle test (NavOracleTests) checks this against the game's own code.
        var h = new AdvHistory();
        var live = new AdvLiveState();
        byte[] script = Script((0x20, "x"));
        for (int i = 0; i < 802; i++)
        {
            Push(h, live, i == 799 ? Msg : AdvHistory.Kinds.SoundEffect, 0x20, i == 799 ? 1 : 2);
        }

        Assert.Equal(1, h.Cur);
        Assert.True(h.Wrapped);
        RewindResult r = h.RewindStep(live, _ => script);
        Assert.True(r.Stepped);
        Assert.Equal(799, r.Target);
        Assert.Equal([1, 799], r.Restores.Select(x => x.Index).ToArray());
    }

    private static (AdvHistory H, AdvLiveState Live) FourScenes(int messagesInLast)
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        for (int s = 0; s < 4; s++)
        {
            ulong start = 0x100UL * (ulong)(s + 1);
            live.TextBlock = 100 + s;
            Push(h, live, AdvHistory.Kinds.SceneStart, start);
            Assert.True(h.PushSceneStack(0));
            Assert.Equal(2, h.SceneMessageCounter);
            int messages = s == 3 ? messagesInLast : 3;
            for (int m = 0; m < messages; m++)
            {
                Push(h, live, Msg, start + 0x10UL + (ulong)(m * 0x10), m == 0 ? 1 : 2);
                h.OnMessageShown();
            }
        }

        Assert.Equal(3, h.StackTop);
        return (h, live);
    }

    [Fact]
    public void PreviousSceneRestartsTheCurrentSceneAfterTwoMessages()
    {
        (AdvHistory h, AdvLiveState live) = FourScenes(3);
        Assert.Equal(0, h.SceneMessageCounter);
        Assert.True(h.CanGoToPreviousScene(0, false));
        SceneRestoreResult? r = h.PreviousScene(live, 0, false, out bool flush);
        Assert.NotNull(r);
        Assert.True(flush);
        Assert.Equal(0x400UL, live.ScriptOffset);                  // scene 4 starts again
        Assert.Equal(103, live.TextBlock);
        Assert.Equal(2, h.StackTop);
        Assert.Equal(2, h.SceneMessageCounter);
        Assert.Equal(2, h.AfterLoad);

        // straight away (fewer than two messages): back to the scene before
        r = h.PreviousScene(live, 0, false, out _);
        Assert.NotNull(r);
        Assert.Equal(0x200UL, live.ScriptOffset);
        Assert.Equal(101, live.TextBlock);
        Assert.Equal(0, h.StackTop);
        Assert.False(h.CanGoToPreviousScene(0, false));
        Assert.Null(h.PreviousScene(live, 0, false, out _));
    }

    [Fact]
    public void PreviousSceneWithinTheFirstMessagesGoesToTheSceneBefore()
    {
        (AdvHistory h, AdvLiveState live) = FourScenes(1);
        Assert.Equal(1, h.SceneMessageCounter);
        SceneRestoreResult? r = h.PreviousScene(live, 0, false, out _);
        Assert.NotNull(r);
        Assert.Equal(0x300UL, live.ScriptOffset);                  // scene 3, not scene 4
        Assert.Equal(1, h.StackTop);
    }

    [Fact]
    public void PreviousSceneWithTheMovieFlagNeedsTwoEarlierScenes()
    {
        (AdvHistory h, AdvLiveState live) = FourScenes(3);
        SceneRestoreResult? r = h.PreviousScene(live, 0, true, out bool flush);   // wait flag 0x10 set, nothing blocking
        Assert.NotNull(r);
        Assert.True(flush);
        Assert.Equal(0x300UL, live.ScriptOffset);                  // top - 1
        Assert.Equal(1, h.StackTop);
        Assert.Null(h.PreviousScene(live, 0, true, out _));        // top is 1 now: below 2 nothing is restored
    }

    [Fact]
    public void SceneJumpRequestsFollowAddress75420()
    {
        var h = new AdvHistory();
        h.RequestSceneJump(1, waitFlag10: true);
        Assert.Equal(0, h.TakeSceneJumpRequest());               // a next-scene request is dropped while bit 0x10 is set
        h.RequestSceneJump(1, waitFlag10: false);
        Assert.Equal(1, h.SceneJumpRequest);
        Assert.Equal(1, h.TakeSceneJumpRequest());
        Assert.Equal(0, h.SceneJumpRequest);
        h.RequestSceneJump(-1, waitFlag10: true);                // previous scene is not gated
        Assert.Equal(-1, h.TakeSceneJumpRequest());
        Assert.False(h.EndNextScene());
        h.NextSceneRunning = true;
        Assert.True(h.EndNextScene());
        Assert.False(h.NextSceneRunning);
    }

    [Fact]
    public void PreviousSceneDoesNothingWhileAnEffectBlocks()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        Push(h, live, AdvHistory.Kinds.SceneStart, 0x100);
        h.PushSceneStack(0);
        Assert.False(h.PushSceneStack(0x8));                      // SceneStack_Push is skipped while an effect blocks
        Assert.Equal(0, h.StackTop);
        Assert.Null(h.PreviousScene(live, 0, false, out _));      // top == 0
    }

    [Fact]
    public void SceneStackShiftsDownWhenItIsFull()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        for (int i = 0; i < 305; i++)
        {
            Push(h, live, AdvHistory.Kinds.SceneStart, (ulong)(0x1000 + i));
            Assert.True(h.PushSceneStack(0));
        }

        Assert.Equal(299, h.StackTop);
        Assert.Equal(0x1000UL + 304, h.StackRecord(299).CmdOffset);
        Assert.Equal(0x1000UL + 5, h.StackRecord(0).CmdOffset);
    }

    [Fact]
    public void KindSixRestorePopsTheSceneStackWhenTheRecordIsTheTop()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        Push(h, live, AdvHistory.Kinds.SceneStart, 0x10);
        h.PushSceneStack(0);
        Push(h, live, Msg, 0x20);
        Push(h, live, AdvHistory.Kinds.SceneStart, 0x30);
        h.PushSceneStack(0);
        Assert.Equal(1, h.StackTop);
        RestoreOutcome o = h.Restore(2, false, live);              // the second scene start
        Assert.True(o.ScenePopped);
        Assert.Equal(0, h.StackTop);
        Assert.Equal(0, h.SceneMessageCounter);
        RestoreOutcome o2 = h.Restore(1, false, live);             // a message: no pop
        Assert.False(o2.ScenePopped);
        Assert.Equal(0, h.StackTop);
    }

    [Fact]
    public void BacklogListsTheMessagesOfEarlierPagesNewestFirst()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        // two pages of two messages (F799 = 1 marks the last message of a page), then the current page's first message
        int[] pages = [0, 1, 0, 1, 0];
        Push(h, live, AdvHistory.Kinds.SceneStart, 0x10);          // 0
        for (int i = 0; i < pages.Length; i++)
        {
            live.Vars.SetWord(AdvLiveState.FIndex.LastMessage, (ushort)pages[i]);
            Push(h, live, Msg, (ulong)(0x20 + (i * 0x10)), i % 2 == 0 ? 1 : 2);
        }

        List<int> list = h.BacklogEntries();
        // ring: 0 scene start, 1..5 messages with F799 = 0,1,0,1,0; the current page is {5}; earlier pages end at 4 and 2
        Assert.Equal([4, 3, 2, 1], list);
    }

    [Fact]
    public void BacklogIsEmptyWithoutAnEarlierPage()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        Assert.Empty(h.BacklogEntries());
        Push(h, live, AdvHistory.Kinds.SceneStart, 0x10);
        Push(h, live, Msg, 0x20);
        Push(h, live, Msg, 0x30);
        Assert.Empty(h.BacklogEntries());                         // no record with F799 == 1 before the current page
    }

    [Fact]
    public void BacklogJumpRestoresTheEntryAndDropsNewerSceneStarts()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        Push(h, live, AdvHistory.Kinds.SceneStart, 0x10);          // 0
        h.PushSceneStack(0);
        live.Vars.SetWord(AdvLiveState.FIndex.LastMessage, 1);
        Push(h, live, Msg, 0x20);                                  // 1: an earlier page's last message
        Push(h, live, AdvHistory.Kinds.SceneStart, 0x30);          // 2
        h.PushSceneStack(0);
        live.Vars.SetWord(AdvLiveState.FIndex.LastMessage, 0);
        Push(h, live, Msg, 0x40);                                  // 3
        Assert.Equal(1, h.StackTop);

        RestoreOutcome? o = h.BacklogJump(1, live);
        Assert.NotNull(o);
        Assert.Equal(0x20UL, live.ScriptOffset);                   // from_history = 0: the command runs again
        Assert.Equal(1, h.Cur);
        Assert.Equal(1, h.Next);                                   // the next push overwrites the entry
        Assert.Equal(0, h.StackTop);                               // the scene started after the entry is dropped
        Assert.Null(h.BacklogJump(-1, live));
    }

    [Fact]
    public void ChoiceEntryJumpsToTheChoiceLineBefore()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        Push(h, live, AdvHistory.Kinds.ChoiceLine, 0x20);          // 0 (SELR)
        Push(h, live, AdvHistory.Kinds.ChoiceStart, 0x30);         // 1 (SELD)
        Push(h, live, Msg, 0x40);                                  // 2
        RestoreOutcome? o = h.BacklogJump(1, live);
        Assert.NotNull(o);
        Assert.Equal(0, o.Index);                                  // kind 12 entry -> the kind 13 record before it
        Assert.Equal(0x20UL, live.ScriptOffset);
        h.Record(0).Kind = Msg;
        Assert.Null(h.BacklogJump(1, live));                       // the record before is not a choice line
    }

    [Fact]
    public void RestoreOfARecordFromAnotherScriptAsksForTheReload()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState { ScriptNumber = 4 };
        Push(h, live, Msg, 0x20);
        live.ScriptNumber = 9;
        RestoreOutcome o = h.Restore(0, false, live);
        Assert.True(o.ReloadScript);
        Assert.Equal(4, live.ScriptNumber);
    }

    [Fact]
    public void ARewindToAMessageNeedsTheScript()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        Push(h, live, Msg, 0x20);
        Assert.Throws<ArgumentException>(() => h.Restore(0, true, live));
    }

    [Fact]
    public void RedisplayTextStopsAtTheEndCodes()
    {
        (byte[] text, int end, bool reached) = AdvHistory.ExtractStatementText(Encoding.ASCII.GetBytes(";_ZM00a01(Hi there@n);"), 0);
        Assert.Equal("Hi there", Encoding.ASCII.GetString(text));
        Assert.Equal(1, end);
        Assert.False(reached);
        (text, end, reached) = AdvHistory.ExtractStatementText(Encoding.ASCII.GetBytes(";_ZM00a01(a@b);"), 0);
        Assert.Equal("a@b", Encoding.ASCII.GetString(text));
        Assert.Equal(0, end);
        Assert.True(reached);
        (text, _, reached) = AdvHistory.ExtractStatementText(Encoding.ASCII.GetBytes(";_ZM00a01();"), 0);
        Assert.Empty(text);
        Assert.True(reached);
        (text, _, _) = AdvHistory.ExtractStatementText(Enumerable.Repeat((byte)'z', 0x500).ToArray(), 0);
        Assert.Equal(0x300, text.Length);
    }

    [Fact]
    public void MessageTextAndVoiceAreAttachedToTheNewestRecord()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        Push(h, live, Msg, 0x20);
        h.SetNewestMessageText("hello"u8);
        h.SetNewestVoice(1234, 7, 3);
        HistoryRecord r = h.Newest;
        Assert.Equal(1234, r.VoiceId);
        Assert.Equal(7, r.VoiceCharacter);
        Assert.Equal(3, r.Speaker);
        Assert.Equal("hello", Encoding.ASCII.GetString(r.MessageText[..5]));
        live.MessageText[0] = 0x7F;
        h.Restore(0, false, live);
        Assert.Equal((byte)'h', live.MessageText[0]);
        Assert.Equal(1234, live.VoiceId);
    }

    [Fact]
    public void StagingFollowsTheCurrentPageStart()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        Push(h, live, AdvHistory.Kinds.SceneStart, 0x10);
        h.PushSceneStack(0);
        Push(h, live, Msg, 0x20, f823: 1);
        Assert.True(h.UpdateStaging(h.Newest, live));
        Assert.Equal(1, h.Staging.Used);
        Assert.Equal(1, h.Staging.HasStateB);
        Assert.Equal(0x20UL, h.Staging.StateA.CmdOffset);
        Assert.Equal(0x10UL, BitConverter.ToUInt64(h.Staging.StateB[8..16]));
        // a later message of the page does not move it
        Push(h, live, Msg, 0x30, f823: 2);
        Assert.False(h.UpdateStaging(h.Newest, live));
        Assert.Equal(0x20UL, h.Staging.StateA.CmdOffset);
        // a record with a blocking effect does not
        live.EffectBlockingBits = 1;
        Push(h, live, Msg, 0x40, f823: 1);
        Assert.False(h.UpdateStaging(h.Newest, live));
    }

    [Fact]
    public void LoadSlotRunsTheSavedMessageAgain()
    {
        // a play session: scene start, a message page start; the staging is what a save stores
        var h = new AdvHistory();
        var live = new AdvLiveState { ScriptNumber = 12 };
        live.BgmTrack = 21;
        Push(h, live, AdvHistory.Kinds.SceneStart, 0x10);
        h.PushSceneStack(0);
        live.TextBlock = 300;
        live.TextLine = 2;
        live.Vars.SetWord(700, 3);                                // not in any list: lost on load, like the PS4
        live.Vars.SetWord(110, 0x55);                             // in the u16 list: kept
        Push(h, live, Msg, 0x40, f823: 1);
        h.UpdateStaging(h.Newest, live);

        var store = new SaveStore();
        Assert.True(store.SaveManual(3, h.Staging, new SlotDate(2026, 10, 26, 12, 0, 0)));

        var h2 = new AdvHistory();
        var live2 = new AdvLiveState();
        live2.Vars.SetWord(700, 9);
        live2.Vars.SetG(7, 77);                                    // G is not touched by a load
        store.Load(3, h2, live2);

        Assert.Equal(0x40UL, live2.ScriptOffset);
        Assert.Equal(12, live2.ScriptNumber);
        Assert.Equal(21, live2.BgmTrack);
        Assert.Equal(300, live2.TextBlock);
        Assert.Equal(0x55, live2.Vars.GetWord(110));
        Assert.Equal(0, live2.Vars.GetWord(700));
        Assert.Equal(77, live2.Vars.GetG(7));
        Assert.Equal(1, live2.Vars.GetWord(AdvLiveState.FIndex.SavedMarker));   // manual saves set F1118
        Assert.Equal(0, h2.StackTop);
        Assert.Equal(0, h2.Cur);
        Assert.Equal(0, h2.Next);
        Assert.Equal(0, h2.SceneMessageCounter);
        Assert.Equal(1, h2.AfterLoad);
        Assert.Equal(0x10UL, h2.StackRecord(0).CmdOffset);          // state B: the scene start
        Assert.Equal(0x40UL, h2.StackRecord(1).CmdOffset);
        Assert.Equal(0x40UL, h2.Record(0).CmdOffset);
        Assert.Equal(-1, live2.LoopSeIds[0]);
        Assert.Equal(100, live2.SeVolA[0]);
    }

    [Fact]
    public void LoadingASlotWithoutItsEndMarkerIsRejected()
    {
        var slot = new SlotImage();
        slot.MagicWord = SlotImage.Magic;
        slot.Used = 1;
        Assert.Throws<InvalidDataException>(() => new AdvHistory().LoadSlot(slot, new AdvLiveState()));
        slot.EndMarker = SlotImage.Magic;
        slot.HasStateB = 2;
        Assert.Throws<InvalidDataException>(() => new AdvHistory().LoadSlot(slot, new AdvLiveState()));
    }

    [Fact]
    public void ClearKeepsUnrelatedRecordBytesLikeThePs4()
    {
        var h = new AdvHistory();
        var live = new AdvLiveState();
        live.State174DAB0[3] = 9;
        Push(h, live, Msg, 0x20);
        h.SetNewestMessageText("x"u8);
        h.Clear(live);
        HistoryRecord r = h.Record(0);
        Assert.Equal(-1, r.Serial);
        Assert.Equal(0, r.RingIndex);
        Assert.Equal(0, r.MessageText[0]);
        Assert.Equal(9, r.State174DAB0[3]);                        // only serial, ring index, text and window bytes are reset
        Assert.False(h.IsValid(0));
        Assert.Equal(-1, h.StackTop);
    }
}
