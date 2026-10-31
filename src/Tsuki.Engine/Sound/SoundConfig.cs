using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Sound;

/// <summary>
/// The persistent config fields the sound code reads (config block at data VA 0x66C578; docs/formats/sound_runtime.md 2.4), with the
/// boot defaults of eboot VA 0x3EAC0. Port of <c>tsuki.misc_audio_runtime_semantics.Config</c>. The model reads these fields when it
/// needs them; <see cref="SoundModel.ApplyConfig"/> is <c>Sound_ApplyConfig</c> (VA 0x3E9B0) and copies them into the volume block.
/// </summary>
public sealed class SoundConfig
{
    /// <summary>Number of per-character voice volumes (config slots 0..18).</summary>
    public const int CharacterSlots = 19;

    /// <summary>+0x14: BGM volume 0..100.</summary>
    public int Bgm { get; set; } = 40;

    /// <summary>+0x16: voice master (menu item 0x5C: on = 100, off = 0).</summary>
    public int Voice { get; set; } = 100;

    /// <summary>+0x18: SE volume.</summary>
    public int Se { get; set; } = 40;

    /// <summary>+0x1A: system SE volume.</summary>
    public int SysSe { get; set; } = 10;

    /// <summary>+0x1C: 1 = lower the BGM while a voice plays (sound_runtime.md 9; config.md calls the field "constant (movie path)").</summary>
    public int Duck { get; set; }

    /// <summary>+0x1E: 1 = the 5.1ch BGM set (entries n + 131, absent on the PS4 data).</summary>
    public int AltBgm { get; set; }

    /// <summary>+0x2C: 1 = the SE notification hook runs after every started SE (<see cref="SoundModel.SeStarted"/>).</summary>
    public int SeCaption { get; set; }

    /// <summary>+0x30: voices also play in skip mode (read text only).</summary>
    public int SkipVoice { get; set; } = 1;

    /// <summary>+0x36: second voice enable (0x41AB0); not written by the boot defaults routine, 1 in the saved config.</summary>
    public int VoiceOn2 { get; set; } = 1;

    /// <summary>+0x3C: bit k = the voice of config slot k is on.</summary>
    public uint CharEnable { get; set; } = 0x3FFFFFFF;

    /// <summary>+0x4E: stop the voice when the text advances.</summary>
    public int StopOnAdvance { get; set; } = 1;

    /// <summary>+0x54: movie volume (a copy of +0x14).</summary>
    public int Movie { get; set; } = 40;

    /// <summary>+0x76 + 2k: voice volume of config slot k (0..100).</summary>
    public int[] CharVol { get; } = Enumerable.Repeat(50, CharacterSlots).ToArray();

    /// <summary>+0xA6: mute BGM and movie.</summary>
    public int MuteBgm { get; set; }

    /// <summary>+0xA7: mute SE.</summary>
    public int MuteSe { get; set; }

    /// <summary>+0xA8: mute system SE.</summary>
    public int MuteSys { get; set; }

    /// <summary>Reads the sound fields of a config block (the persistent block of the engine).</summary>
    public static SoundConfig FromBlock(ConfigBlock block)
    {
        var c = new SoundConfig();
        c.ReadFrom(block);
        return c;
    }

    /// <summary>Copies the sound fields of <paramref name="block"/>.</summary>
    public void ReadFrom(ConfigBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        Bgm = block.MusicVolume;
        Voice = block.PlayAllVoices;
        Se = block.SfxVolume;
        SysSe = block.SystemSeVolume;
        Duck = block.Word1C;
        AltBgm = block.Word1E;
        SeCaption = block.Audio2C;
        SkipVoice = block.FastForwardReadOnly;
        VoiceOn2 = block.VoiceGate36;
        CharEnable = block.VoiceEnableBits;
        StopOnAdvance = block.InterruptVoices;
        Movie = block.MusicVolumeCopy;
        for (int i = 0; i < CharacterSlots; i++)
        {
            CharVol[i] = block.GetCharacterVoiceVolume(i);
        }

        MuteBgm = block.MusicMute;
        MuteSe = block.SfxMute;
        MuteSys = block.SystemSeMute;
    }

    /// <summary>
    /// <c>Sound_ApplyConfig(full)</c> (VA 0x3E9B0): the bytes +0xB..+0xF of the volume block 0x17DA268 written into
    /// <paramref name="volume"/> (a 16-byte array).
    /// </summary>
    public void FillVolumeBlock(byte[] volume)
    {
        volume[0xC] = (byte)((1 - MuteBgm) * Bgm);
        volume[0xB] = (byte)((1 - MuteBgm) * Movie);
        volume[0xE] = (byte)((1 - MuteSe) * Se);
        volume[0xF] = (byte)((1 - MuteSys) * SysSe);
        volume[0xD] = (byte)Voice;
    }

    /// <summary>
    /// The voice gain of config slot <paramref name="slot"/>: <c>char_vol[k] * voice / 100</c> (C division), or 0 when the slot's enable
    /// bit is clear (VA 0xB26D / 0x3EA4F / 0x928A2).
    /// </summary>
    public int CharGain(int slot)
    {
        if ((CharEnable & (1u << slot)) == 0)
        {
            return 0;
        }

        return (SoundMath.I16(CharVol[slot]) * SoundMath.I16(Voice)) / 100;
    }
}
