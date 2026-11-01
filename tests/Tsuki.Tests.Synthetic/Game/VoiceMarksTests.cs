using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;
using Tsuki.Engine.Sound;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>
/// Package K36: the marks a <c>_VPLY</c> / <c>_VPL2</c> leaves in the G words (docs/formats/sound_runtime.md 3.6, adv_state.md 9.4: the heard bit of
/// G[73], the slot's last voice in G[31 + k], the row bit the Sound tab shows the character by), their persistence with the profile, and their rebuild
/// from the read text for a profile that predates them.
/// </summary>
public sealed class VoiceMarksTests
{
    private const string Head = "_WKST(F822,eq,1);_WKAD(F823,1);";

    private static GameHost Play(string body, GameProfile? profile = null)
    {
        var rig = new GameRig().Line(1, "hello").Script(Head + body + "_WKST(F11,eq,1);_END_();");
        GameHost host = rig.Build(profile: profile);
        host.NewGame(100);
        host.Step(default);
        return host;
    }

    [Fact]
    public void AVoiceStatementMarksItsCharacterHeardWithItsLastVoiceAndShowsItsRow()
    {
        GameHost host = Play("_VPLY(A10_00_0001,00005);_ZM00101($000001@e);");
        // Arcueid: group A10 = character 2 = config slot 1 (mask 0x2)
        Assert.Equal(1 << 2, host.Vars.GetG(VoiceMarks.HeardWord));
        Assert.Equal(5, host.Vars.GetG(VoiceMarks.LastVoiceWord + 1));
        Assert.Equal(VoiceMarks.RowShownBit, host.Vars.GetG(GameRig.VoiceRowWords[1]) & VoiceMarks.RowShownBit);
        Assert.True(VoiceMarks.IsRowShown(host.Vars, GameRig.VoiceRowWords, 1));
        Assert.False(VoiceMarks.IsRowShown(host.Vars, GameRig.VoiceRowWords, 0));
        Assert.Equal(GameRig.VoiceRowWords, host.VoiceRowWords);
    }

    [Fact]
    public void TheMarksAreWrittenWhetherOrNotTheVoiceSounds()
    {
        // Play All Voices off: the gate refuses the voice at the message, the handler's marks (which precede the gate) are written all the same
        GameProfile profile = GameProfile.CreateNew(Language.En);
        profile.System.Config.PlayAllVoices = 0;
        GameHost host = Play("_VPLY(A00_00_0001,00003);_ZM00101($000001@e);", profile);
        Assert.DoesNotContain(host.TakeAudioCommands(), c => c.Op == AudioOp.Play && c.Bank == AudioBank.Voice);
        Assert.Equal(1 << 1, host.Vars.GetG(VoiceMarks.HeardWord));          // Shiki: character 1, slot 0
        Assert.Equal(3, host.Vars.GetG(VoiceMarks.LastVoiceWord));
        Assert.True(VoiceMarks.IsRowShown(host.Vars, GameRig.VoiceRowWords, 0));
    }

    [Fact]
    public void Vpl2MarksTooAndTheExtraCharacterGetsNoRow()
    {
        GameHost host = Play("_VPL2(B40_00_0001,00011);_VPLY(M00_00_0001,00012);_ZM00101($000001@e);");
        // Akiha: group B40 = character 8 = slot 3 (the entry argument is hex: 0x11); M00 = character 23: its shift leaves the 16-bit heard word alone
        // (0x99F08 ORs dx), 0x3D260 returns at once
        Assert.Equal(1 << 8, host.Vars.GetG(VoiceMarks.HeardWord));
        Assert.Equal(0x11, host.Vars.GetG(VoiceMarks.LastVoiceWord + 3));
        Assert.True(VoiceMarks.IsRowShown(host.Vars, GameRig.VoiceRowWords, 3));
        Assert.Equal(0, host.Vars.GetG(VoiceMarks.LastVoiceWord + 18));
        Assert.False(VoiceMarks.IsRowShown(host.Vars, GameRig.VoiceRowWords, 18));
    }

    [Fact]
    public void NoteFollowsTheHandler()
    {
        var vars = new Vars();
        VoiceMarks.Note(vars, GameRig.VoiceRowWords, 6, -1);              // Hisui (slot 4) without an entry: the heard bit only
        Assert.Equal(1 << 6, vars.GetG(VoiceMarks.HeardWord));
        Assert.Equal(0, vars.GetG(VoiceMarks.LastVoiceWord + 4));
        Assert.False(VoiceMarks.IsRowShown(vars, GameRig.VoiceRowWords, 4));
        VoiceMarks.Note(vars, GameRig.VoiceRowWords, 18, 20_100);         // character 18 (C40) = slot 6: the shift by 18 leaves the 16-bit word alone
        Assert.Equal(1 << 6, vars.GetG(VoiceMarks.HeardWord));
        Assert.Equal(20_100, vars.GetG(VoiceMarks.LastVoiceWord + 6));
        Assert.True(VoiceMarks.IsRowShown(vars, GameRig.VoiceRowWords, 6));
        VoiceMarks.Note(vars, null, 1, 7);                                  // no row table (a rig without one): heard bit and last voice only
        Assert.Equal((1 << 6) | (1 << 1), vars.GetG(VoiceMarks.HeardWord));
        Assert.Equal(7, vars.GetG(VoiceMarks.LastVoiceWord));
        Assert.False(VoiceMarks.IsRowShown(vars, GameRig.VoiceRowWords, 0));
        VoiceMarks.Note(vars, GameRig.VoiceRowWords, 40, 9);                // an unknown character counts as character 0 (mask index 0, slot 18)
        Assert.Equal(9, vars.GetG(VoiceMarks.LastVoiceWord + 18));
    }

    [Fact]
    public void TheMarksPersistInTheSystemBlockWithTheProfile()
    {
        GameHost host = Play("_VPLY(A10_00_0001,00005);_ZM00101($000001@e);");
        Assert.True(GameRig.VoiceRowWords.All(w => w < SystemBlock.GCount));
        Assert.True(VoiceMarks.LastVoiceWord + VoiceMarks.RowCount <= SystemBlock.GCount);
        var system = new SystemData(host.SystemData);
        system.CaptureGlobalsFrom(host.Vars);
        var again = new Vars();
        system.LoadGlobalsInto(again);
        Assert.Equal(5, again.GetG(VoiceMarks.LastVoiceWord + 1));
        Assert.Equal(1 << 2, again.GetG(VoiceMarks.HeardWord));
        Assert.True(VoiceMarks.IsRowShown(again, GameRig.VoiceRowWords, 1));
    }

    [Fact]
    public void AFreshProfileAndAProfileWithMarksNeedNoRebuild()
    {
        var vars = new Vars();
        var system = new SystemBlock();
        Assert.False(VoiceMarks.NeedsRebuild(vars, system));                 // nothing read yet
        system.SetReadmax(0x12, 3);
        Assert.True(VoiceMarks.NeedsRebuild(vars, system));                  // text read, nobody heard: a profile from before the marks
        vars.SetG(VoiceMarks.HeardWord, 2);
        Assert.False(VoiceMarks.NeedsRebuild(vars, system));
    }

    [Fact]
    public void AnOldProfileGetsItsMarksFromTheReadText()
    {
        // two voiced lines; the first marker (block 0x012, line 1) counts as read (readmax[0x12] = 1), the second (line 2) does not
        var rig = new GameRig().Line(1, "a").Script(Head + "_VPLY(A00_00_0001,00003);_ZM01201($000001@e);_VPLY(A10_00_0001,00004);_ZM01202($000001@e);_END_();");
        GameHost host = rig.Build();
        var system = new SystemData(host.SystemData);
        system.MarkRead(0x12, 1);
        var vars = new Vars();
        Assert.True(VoiceMarks.NeedsRebuild(vars, system.Block));
        Assert.Equal(1, VoiceMarks.RebuildFromReadText(rig.Data, system, vars, GameRig.VoiceRowWords));
        Assert.Equal(1 << 1, vars.GetG(VoiceMarks.HeardWord));
        Assert.Equal(3, vars.GetG(VoiceMarks.LastVoiceWord));
        Assert.True(VoiceMarks.IsRowShown(vars, GameRig.VoiceRowWords, 0));
        Assert.Equal(0, vars.GetG(VoiceMarks.LastVoiceWord + 1));
        Assert.False(VoiceMarks.IsRowShown(vars, GameRig.VoiceRowWords, 1));
        Assert.False(VoiceMarks.NeedsRebuild(vars, system.Block));           // once a character is heard the profile never qualifies again
        // reading on marks the second line too
        system.MarkRead(0x12, 2);
        Assert.Equal(2, VoiceMarks.RebuildFromReadText(rig.Data, system, vars, GameRig.VoiceRowWords));
        Assert.Equal(4, vars.GetG(VoiceMarks.LastVoiceWord + 1));
        Assert.True(VoiceMarks.IsRowShown(vars, GameRig.VoiceRowWords, 1));
    }

    [Fact]
    public void TheRebuildCreditsEveryVoiceBeforeAReadMarkerAndTheLastEntryWins()
    {
        // four statements before one marker: all credited when it is read; a slot keeps the last entry in script order; the _VPL2 consumes its entry, so
        // the underscore name after it finds none (-1) and only sets the heard bit, as the handler does (of character 0: "_B2" names no group)
        var rig = new GameRig().Line(1, "a").Script(Head + "_VPLY(A10_00_0001,00004);_VPLY(A10_00_0002,00006);_VPL2(B20_00_0001,00008);_VPLY(_B20_00_0001,00009);_ZM03401($000001@e);_END_();");
        GameHost host = rig.Build();
        var system = new SystemData(host.SystemData);
        var vars = new Vars();
        Assert.Equal(0, VoiceMarks.RebuildFromReadText(rig.Data, system, vars, GameRig.VoiceRowWords));   // nothing read: nothing credited
        Assert.Equal(0, vars.GetG(VoiceMarks.HeardWord));
        system.MarkRead(0x34, 1);
        Assert.Equal(4, VoiceMarks.RebuildFromReadText(rig.Data, system, vars, GameRig.VoiceRowWords));
        Assert.Equal(6, vars.GetG(VoiceMarks.LastVoiceWord + 1));
        Assert.Equal(8, vars.GetG(VoiceMarks.LastVoiceWord + 4));           // Hisui, slot 4
        Assert.Equal((1 << 2) | (1 << 6) | (1 << 0), vars.GetG(VoiceMarks.HeardWord));
        Assert.Equal(0, vars.GetG(VoiceMarks.LastVoiceWord + 18));          // the underscore statement: character 0 = slot 18, no entry, no row
        Assert.False(VoiceMarks.IsRowShown(vars, GameRig.VoiceRowWords, 18));
    }

    [Fact]
    public void TheRebuildReadsEveryScriptOfTheData()
    {
        var rig = new GameRig().Line(1, "a")
            .Script(Head + "_VPLY(A00_00_0001,00003);_ZM01201($000001@e);_END_();")
            .Script(Head + "_VPLY(B40_00_0001,00005);_ZM01301($000001@e);_END_();");
        GameHost host = rig.Build();
        var system = new SystemData(host.SystemData);
        system.MarkRead(0x13, 1);
        var vars = new Vars();
        Assert.Equal(1, VoiceMarks.RebuildFromReadText(rig.Data, system, vars, GameRig.VoiceRowWords));
        Assert.Equal(5, vars.GetG(VoiceMarks.LastVoiceWord + 3));
        Assert.Equal(0, vars.GetG(VoiceMarks.LastVoiceWord));
    }
}
