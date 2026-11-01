using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Engine.Sound;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K36 over the app services: a voiced line leaves the character's marks the Sound tab reads, the config test plays the row's last voice at the
/// row's volume and follows its slider, the marks reach the system block, and a profile from before the marks gets them rebuilt at start.
/// </summary>
public sealed class VoiceRowsAppTests
{
    private const string Voiced =
        "_WKST(F822,eq,1);_WKAD(F823,1);_VPLY(A10_00_0001,00005);_ZM00101($000002@e);" +
        "_WKST(F823,eq,0);_WKAD(F823,1);_VPLY(A00_00_0001,00006);_ZM00102($000003@e);" +
        "_WKST(F11,eq,1);_END_();";

    [Fact]
    public void AVoicedLineLeavesTheMarksAndTheTestPlaysTheRowsVoiceAtItsVolume()
    {
        var rig = new AppRig(Voiced);
        AppServices s = rig.Services;
        Assert.Null(s.VoiceMarksRebuilt);
        rig.Begin();
        Assert.Equal(1 << 2, s.Global(VoiceMarks.HeardWord));                    // Arcueid (A10, character 2, slot 1)
        Assert.Equal(5, s.Global(VoiceMarks.LastVoiceWord + 1));
        Assert.True(VoiceMarks.IsRowShown(rig.Host.Vars, rig.Host.VoiceRowWords!, 1));
        Assert.False(s.PlayTestVoice(0));                                        // Shiki has not spoken yet
        Assert.True(s.PlayTestVoice(1));
        Assert.Equal(2, rig.Host.Sound.VoiceState);
        Assert.Equal(50, rig.Host.Sound.VolumeByte(0xD));
        // the tested row's slider moves the voice at once; another row's does not touch it
        s.Config.SetCharacterVoiceVolume(1, 70);
        s.ConfigChanged(ConfigChange.Volume);
        Assert.Equal(70, rig.Host.Sound.VolumeByte(0xD));
        s.Config.SetCharacterVoiceVolume(0, 10);
        s.ConfigChanged(ConfigChange.Volume);
        Assert.Equal(70, rig.Host.Sound.VolumeByte(0xD));
        // a Test press that plays nothing (Shiki's row has no voice yet) forgets the test row first (VA 0x401E9; package K37): the voice of the earlier test
        // goes on, but row 1's slider no longer follows it
        Assert.False(s.PlayTestVoice(0));
        Assert.Equal(2, rig.Host.Sound.VoiceState);
        rig.Host.TakeAudioCommands();
        s.Config.SetCharacterVoiceVolume(1, 65);
        s.ConfigChanged(ConfigChange.Volume);
        Assert.DoesNotContain(rig.Host.TakeAudioCommands(), c => c.Op is AudioOp.SetVolume or AudioOp.Stop);
        Assert.Equal(2, rig.Host.Sound.VoiceState);
        Assert.True(s.PlayTestVoice(1));                                        // tested again: the row follows once more
        s.Config.SetCharacterVoiceVolume(1, 70);
        s.ConfigChanged(ConfigChange.Volume);
        Assert.Equal(70, rig.Host.Sound.VolumeByte(0xD));
        s.StopTests();
        Assert.Equal(0, rig.Host.Sound.VoiceState);
        rig.Host.TakeAudioCommands();
        s.Config.SetCharacterVoiceVolume(1, 30);
        s.ConfigChanged(ConfigChange.Volume);                                    // no test row any more: nothing plays, nothing is commanded
        Assert.DoesNotContain(rig.Host.TakeAudioCommands(), c => c.Op is AudioOp.SetVolume or AudioOp.Play);
        rig.RunTo("beta line");
        Assert.Equal(10, rig.Host.Sound.VolumeByte(0xD));                        // Shiki's line at his 10 %
        Assert.Equal((1 << 2) | (1 << 1), s.Global(VoiceMarks.HeardWord));
        Assert.Equal(6, s.Global(VoiceMarks.LastVoiceWord));
        Assert.True(s.PlayTestVoice(0));
        Assert.Equal(10, rig.Host.Sound.VolumeByte(0xD));
    }

    [Fact]
    public void TheMarksReachTheSystemBlockWithTheNextCommit()
    {
        string path = Path.Combine(Path.GetTempPath(), "tsuki-k36-" + Guid.NewGuid().ToString("N"), "save.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var rig = new AppRig(Voiced, savePath: path);
            rig.Begin();
            Assert.True(rig.Services.Commit());
            using FileStream file = File.OpenRead(path);
            SaveStore again = SaveFile.Read(file);
            Assert.Equal(5, again.SystemData.Block.GetG(VoiceMarks.LastVoiceWord + 1));
            Assert.Equal(1 << 2, again.SystemData.Block.GetG(VoiceMarks.HeardWord));
            var vars = new Vars();
            again.SystemData.LoadGlobalsInto(vars);
            Assert.True(VoiceMarks.IsRowShown(vars, rig.Host.VoiceRowWords!, 1));
            Assert.False(VoiceMarks.NeedsRebuild(vars, again.SystemData.Block));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void AProfileFromBeforeTheMarksGetsThemAtStartFromTheReadText()
    {
        SaveStore store = AppRig.NewStore();
        store.SystemData.MarkRead(1, 1);                   // the first line's marker (_ZM00101) was passed; the second was not
        var rig = new AppRig(Voiced, store: store);
        AppServices s = rig.Services;
        Assert.NotNull(s.VoiceMarksRebuilt);
        Assert.Equal(1, s.VoiceMarksRebuilt!.Value.Credited);
        Assert.Equal(5, s.Global(VoiceMarks.LastVoiceWord + 1));
        Assert.Equal(1 << 2, s.Global(VoiceMarks.HeardWord));
        Assert.Equal(0, s.Global(VoiceMarks.LastVoiceWord));
        Assert.Equal(5, store.SystemData.Block.GetG(VoiceMarks.LastVoiceWord + 1));      // captured into the system block at once
        Assert.True(s.PlayTestVoice(1));                                                   // the row's test works before any line of this session
        // the rebuilt profile needs nothing on the next start
        var next = new AppRig(Voiced, store: store);
        Assert.Null(next.Services.VoiceMarksRebuilt);
    }

    [Fact]
    public void AFreshProfileNeedsNoRebuild()
    {
        var rig = new AppRig(Voiced);
        Assert.Null(rig.Services.VoiceMarksRebuilt);
        Assert.Equal(0, rig.Services.Global(VoiceMarks.HeardWord));
    }

    [Fact]
    public void TheReplayVoiceKeepsTheLastVoicesCharacter()
    {
        var rig = new AppRig(Voiced);
        rig.Begin();
        rig.Host.Sound.StopVoice();
        rig.Host.TakeAudioCommands();
        rig.Services.Config.SetCharacterVoiceVolume(1, 80);
        rig.Services.ConfigChanged(ConfigChange.Volume);
        rig.Services.ReplayVoice();
        Assert.Equal(2, rig.Host.Sound.VoiceState);
        Assert.Equal(80, rig.Host.Sound.VolumeByte(0xD));                        // Arcueid's row volume, through the gate
        Assert.Equal(5, rig.Host.Sound.LastVoice);
    }
}
