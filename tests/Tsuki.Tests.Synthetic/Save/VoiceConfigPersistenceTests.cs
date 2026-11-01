using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Engine.Sound;

namespace Tsuki.Tests.Synthetic.Save;

/// <summary>Package K36: the per-character voice volumes and enable bits (config +0x76, +0x3C) and the voice marks (G words) survive the save file.</summary>
public sealed class VoiceConfigPersistenceTests
{
    [Fact]
    public void TheCharacterVolumesEnableBitsAndMarksRoundTrip()
    {
        var store = new SaveStore();
        ConfigBlock cfg = store.SystemData.Config;
        Assert.Equal(19, ConfigBlock.CharacterVoiceVolumeCount);
        for (int k = 0; k < ConfigBlock.CharacterVoiceVolumeCount; k++)
        {
            Assert.Equal(50, cfg.GetCharacterVoiceVolume(k));                  // the boot default of every row
        }

        cfg.SetCharacterVoiceVolume(0, 100);
        cfg.SetCharacterVoiceVolume(7, 0);
        cfg.SetCharacterVoiceVolume(18, 33);
        cfg.VoiceEnableBits &= ~(1u << 7);
        SystemBlock sys = store.SystemData.Block;
        sys.SetG(VoiceMarks.HeardWord, 0x0106);
        sys.SetG(VoiceMarks.LastVoiceWord + 7, 1234);
        sys.SetG(11, VoiceMarks.RowShownBit | VoiceMarks.RowNameBit);
        using var ms = new MemoryStream();
        SaveFile.Write(ms, store);
        ms.Position = 0;
        SaveStore again = SaveFile.Read(ms);
        ConfigBlock c2 = again.SystemData.Config;
        Assert.Equal(100, c2.GetCharacterVoiceVolume(0));
        Assert.Equal(0, c2.GetCharacterVoiceVolume(7));
        Assert.Equal(33, c2.GetCharacterVoiceVolume(18));
        Assert.Equal(50, c2.GetCharacterVoiceVolume(1));
        Assert.Equal(0x3FFFFFFFu & ~(1u << 7), c2.VoiceEnableBits);
        Assert.Equal(0x0106, again.SystemData.Block.GetG(VoiceMarks.HeardWord));
        Assert.Equal(1234, again.SystemData.Block.GetG(VoiceMarks.LastVoiceWord + 7));
        Assert.Equal(6, again.SystemData.Block.GetG(11));
        // the sound model reads the volumes back as the gate does
        SoundConfig sound = SoundConfig.FromBlock(c2);
        Assert.Equal(100, sound.CharGain(0));
        Assert.Equal(0, sound.CharGain(7));
        Assert.Equal(33, sound.CharGain(18));
    }
}
