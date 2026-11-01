using Tsuki.Engine.Contracts;
using Tsuki.Engine.Sound;

namespace Tsuki.Tests.Synthetic.Sound;

/// <summary>
/// Package K36: a character's volume reaches the mixer (sound_runtime.md 7, step 2), the config screen's test voice plays at the row's volume
/// (VA 0x401E0 then Sound_ApplyConfig(0)), a tested row's slider moves the playing voice at once (VA 0xB6F0), and what the handler hands to the marks.
/// </summary>
public sealed class VoiceVolumeTests
{
    private static SoundModel Make(SoundConfig? cfg = null) => new(cfg, seLookup: (_, _) => null, isPlaying: null);

    private static AudioCommand Play(SoundModel m) => m.TakeCommands().Single(c => c.Op == AudioOp.Play);

    [Fact]
    public void EachCharactersVoicePlaysAtItsOwnVolumeTimesTheMaster()
    {
        var cfg = new SoundConfig();
        cfg.CharVol[1] = 80;                                  // Arcueid, slot 1
        cfg.CharVol[0] = 20;                                  // Shiki, slot 0
        SoundModel m = Make(cfg);
        m.Run("_VPLY(A10_00_0001,00005);");
        Assert.True(m.StartVoiceAtMessage());
        Assert.Equal(0.8f, Play(m).Value0, 5);
        Assert.Equal(80, m.VolumeByte(0xD));
        m.Run("_VPLY(A00_00_0001,00006);");
        Assert.True(m.StartVoiceAtMessage());
        Assert.Equal(0.2f, Play(m).Value0, 5);
        Assert.Equal(20, m.VolumeByte(0xD));
        cfg.CharVol[0] = 100;                                 // the slider at 100 %: the voice plays at full gain
        m.Run("_VPLY(A00_00_0001,00007);");
        Assert.True(m.StartVoiceAtMessage());
        Assert.Equal(1f, Play(m).Value0, 5);
        Assert.Equal(50, new SoundConfig().CharVol[7]);       // the boot default of every slot is 50 %, not the 40 % of the music slider
        Assert.Equal(40, new SoundConfig().Bgm);
    }

    [Fact]
    public void TheConfigTestPlaysTheRowsVoiceAtTheRowsVolumeAndAMutedRowStaysSilent()
    {
        var cfg = new SoundConfig();
        cfg.CharVol[3] = 60;                                  // Akiha, slot 3
        SoundModel m = Make(cfg);
        Assert.True(m.StartTestVoice(11, 3));
        AudioCommand play = Play(m);
        Assert.Equal(AudioBank.Voice, play.Bank);
        Assert.Equal(11, play.Entry);
        Assert.Equal(0.6f, play.Value0, 5);
        Assert.Equal(60, m.VolumeByte(0xD));
        Assert.Equal(-1, m.LastVoice);                        // a test is not the replay voice
        Assert.Equal(2, m.VoiceState);
        Assert.True(m.StartTestVoice(20_011, 3));             // voice2 entries play as the script's do
        Assert.Equal(AudioBank.Voice2, Play(m).Bank);
        cfg.CharEnable &= ~(1u << 3);
        Assert.False(m.StartTestVoice(11, 3));                // the muted row: Sound_ApplyConfig(0) stops the voice before it sounds
        Assert.DoesNotContain(m.TakeCommands(), c => c.Op == AudioOp.Play);
        Assert.Equal(0, m.VoiceState);
        Assert.False(m.StartTestVoice(11, 19));
        Assert.False(m.StartTestVoice(11, -1));
    }

    [Fact]
    public void ATestedRowsSliderMovesThePlayingVoiceAtOnce()
    {
        var cfg = new SoundConfig();
        SoundModel m = Make(cfg);
        Assert.True(m.StartTestVoice(11, 2));
        m.TakeCommands();
        cfg.CharVol[2] = 90;
        m.ApplyTestRow(2);
        AudioCommand vol = m.TakeCommands().Single(c => c.Op == AudioOp.SetVolume);
        Assert.Equal(0.9f, vol.Value0, 5);
        Assert.Equal(90, m.VolumeByte(0xD));
        cfg.Voice = 0;                                        // Play All Voices off: the master is part of the gain
        m.ApplyTestRow(2);
        Assert.Equal(0f, m.TakeCommands().Single(c => c.Op == AudioOp.SetVolume).Value0, 5);
        cfg.Voice = 100;
        cfg.CharEnable &= ~(1u << 2);                         // the row muted: the voice stops
        m.ApplyTestRow(2);
        Assert.Contains(m.TakeCommands(), c => c.Op == AudioOp.Stop);
        Assert.Equal(0, m.VoiceState);
        m.ApplyTestRow(2);                                    // no voice plays: only the byte changes
        Assert.DoesNotContain(m.TakeCommands(), c => c.Op == AudioOp.SetVolume);
    }

    [Fact]
    public void SetVoiceVolumeScalesTheRecordVolume()
    {
        SoundModel m = Make();
        m.Run("_VPLY(A10_00_0001,00005);");
        Assert.True(m.StartVoiceAtMessage());
        m.TakeCommands();
        m.SetVoiceVolume(35);
        Assert.Equal(0.35f, m.TakeCommands().Single(c => c.Op == AudioOp.SetVolume).Value0, 5);
        Assert.Equal(35, m.VolumeByte(0xD));
        m.StopVoice();
        m.TakeCommands();
        m.SetVoiceVolume(70);                                 // nothing plays: the byte alone
        Assert.Empty(m.TakeCommands());
        Assert.Equal(70, m.VolumeByte(0xD));
    }

    [Fact]
    public void AnUnderscoreNameRepeatsThePendingEntryOrFindsNone()
    {
        SoundModel m = Make();
        m.Run("_VPLY(A10_00_0001,00005);");
        Assert.Equal(5, m.VplyEntry);
        Assert.Equal(2, m.PendingVoiceCharacter);
        m.Run("_VPLY(_A10_00_0001,00099);");
        Assert.Equal(5, m.VplyEntry);                         // the previous entry, still pending
        Assert.True(m.StartVoiceAtMessage());
        m.Run("_VPLY(_B20_00_0001,00099);");
        Assert.Equal(-1, m.VplyEntry);                        // consumed by the message: 0x3D260 gets -1 and writes no row
        Assert.Equal(0, m.PendingVoiceCharacter);             // the handler keys the group on the first three bytes, "_B2" matches none: character 0
        m.Run("_VPL2(B20_00_0001,00021);");
        Assert.Equal(0x21, m.VplyEntry);                      // what the marks get (the hex entry), although the VPL2 consumed the pending voice at once
        Assert.Equal(-1, m.PendingVoice);
    }
}
