using System.Globalization;
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Sound;

/// <summary>A voice the gate let through: the voice.hed entry, the config slot k and the voice gain 0..100 (sound_runtime.md 7).</summary>
/// <param name="Entry">The entry requested by <c>_VPLY</c> (&lt;= 19,999: voice.hed; 20,000..59,999: voice2.hed entry - 20,000).</param>
/// <param name="Slot">The config slot k of the character (bit k of config +0x3C, volume at config +0x76 + 2k).</param>
/// <param name="Gain">The voice gain <c>char_vol[k] * voice / 100</c> (C division), also written to volume byte 0xD.</param>
public readonly record struct VoiceStart(int Entry, int Slot, int Gain);

// The voice pipeline and the system sounds (docs/formats/sound_runtime.md 3.6, 7, 9, 10.1, 12): _VPLY makes the voice pending, the
// next message display runs the gate and starts it on one of streams 2..5, the next advance stops it, and the model reports whether
// a voice plays (for _VCWT, auto mode and the BGM ducking).
public sealed partial class SoundModel
{
    private int _voicePending = -1;     // 0x16541FC
    private int _voiceChar;             // 0x1654208
    private int _voiceArg3;             // 0x165420A
    private int _voiceCounter;          // 0xDD8F08: stream = 2 + (++n & 3)
    private int _voiceState;            // 0xDD8E50: 0 idle, 2 playing (loading, state 1, does not exist: clips are decoded before the start)
    private int _voiceStream = -1;
    private int _voiceElapsed;          // 0xDD8F04
    private int _systemCounter;         // the rotating system voice 0x2C + n

    /// <summary>The voice state word (0xDD8E50): 0 idle, 2 playing. <c>_VCWT</c> waits while it is not 0; ducking follows it.</summary>
    public int VoiceState => _voiceState;

    /// <summary>The voice entry <c>_VPLY</c> requested and the next message display will play, -1 = none (0x16541FC).</summary>
    public int PendingVoice => _voicePending;

    /// <summary>The character of the pending voice (0x1654208).</summary>
    public int PendingVoiceCharacter => _voiceChar;

    /// <summary>The third <c>_VPLY</c> argument, 0 if absent; the history record keeps it (0x165420A).</summary>
    public int VoiceArg3 => _voiceArg3;

    /// <summary>The last voice a message started (0x86AA28), for the replay button; -1 = none.</summary>
    public int LastVoice { get; private set; } = -1;

    /// <summary>
    /// The time auto mode waits on the voice (VA 0x96F0): elapsed frames * 17 ms while it plays, 10,000 ms when no voice state exists.
    /// </summary>
    public int AutoModeVoiceMilliseconds => _voiceState == 2 ? _voiceElapsed * 17 : 10_000;

    /// <summary>
    /// VA 0x968B2: <c>_VPLY(name, hex entry [, n])</c> remembers the voice (the next message display plays it); <c>_VPL2</c> plays it
    /// at once through the same gate and does not become the replay voice (knife game.rs "VPLY | VPL2": the PS4 docs 3.6 say "play
    /// immediately, same gate and gain as 7, and clear the pending voice"). A name starting with an underscore repeats the previous entry.
    /// </summary>
    private int CmdVply(ReadOnlySpan<byte> b, int sep, bool immediate)
    {
        string name = SoundTags.ArgText(b, sep, 0);
        _voiceChar = VoiceGroups.CharacterOf(name);
        int nargs = SoundTags.CountArgs(b, sep);
        _voiceArg3 = nargs >= 3 ? SoundTags.Int(b, sep, 2) & 0xFFFF : 0;
        if (!name.StartsWith('_'))
        {
            string hex = SoundTags.ArgText(b, sep, 1).Trim();
            _voicePending = hex.Length == 0 ? 0 : int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int idx) ? idx : 0;
        }

        if (immediate)
        {
            VoiceStart? start = VoiceGate();
            if (start is { } v)
            {
                StartVoice(v.Entry, remember: false);
            }
        }

        return 0;
    }

    /// <summary><c>_VCLR()</c> (VA 0x97277; no script uses it): the pending voice is dropped.</summary>
    private int CmdVclr()
    {
        _voicePending = -1;
        return 0;
    }

    /// <summary>
    /// The gate of the voice start at the message display (VA 0x9281E..0x92902): takes the pending voice (always clearing it) and
    /// returns it when voices are on, the character's voice is enabled and neither fast-forward nor skip mode (without "voices in skip")
    /// forbids it; writes the voice gain into volume byte 0xD. Does not play anything.
    /// </summary>
    public VoiceStart? VoiceGate()
    {
        int idx = _voicePending;
        _voicePending = -1;
        if (idx < 0)
        {
            return null;
        }

        if (Skip && Config.SkipVoice == 0)
        {
            return null;
        }

        if (FastForward || !(Config.Voice != 0 && Config.VoiceOn2 != 0))
        {
            return null;
        }

        int k = VoiceGroups.SlotOf(_voiceChar);
        if ((Config.CharEnable & VoiceGroups.MaskOf(_voiceChar)) == 0)
        {
            return null;
        }

        int gain = SoundMath.I16(Config.CharVol[k]) * SoundMath.I16(Config.Voice) / 100;
        _vol[0xD] = unchecked((byte)gain);
        return new VoiceStart(idx, k, gain);
    }

    /// <summary>
    /// The voice part of the <c>_ZM</c> handler (VA 0x92380): runs the gate and, when it lets the pending voice through, starts it
    /// (<see cref="StartVoice"/>) and remembers it for the replay button. Returns true when a voice started.
    /// </summary>
    public bool StartVoiceAtMessage()
    {
        VoiceStart? start = VoiceGate();
        if (start is not { } v)
        {
            return false;
        }

        return StartVoice(v.Entry, remember: true);
    }

    /// <summary>
    /// <c>VoiceRequest(idx)</c> + the start of the stream (VA 0xC3A80, 0x8F60, 0x9220, 0x8BD0; sound_runtime.md 7 steps 3-4): stops
    /// streams 2..5, picks stream <c>2 + (++n &amp; 3)</c>, sets its record {1, kind 0, vol = target = rate = 100} and plays the entry
    /// from memory at <c>volume byte 0xD * 0.01</c>. Returns false for an entry above 59,999 (rejected).
    /// </summary>
    public bool StartVoice(int entry, bool remember = false)
    {
        AudioBank bank;
        int bankEntry;
        if (entry is >= 0 and <= 19_999)
        {
            bank = AudioBank.Voice;
            bankEntry = entry;
        }
        else if (entry is >= 20_000 and <= 59_999)
        {
            bank = AudioBank.Voice2;
            bankEntry = entry - 20_000;
        }
        else
        {
            return false;
        }

        StopVoice();
        _voiceCounter++;
        int s = AudioStreams.FirstVoice + (_voiceCounter & 3);
        SoundChannelRecord r = _voice[s - AudioStreams.FirstVoice];
        r.State = 1;
        r.VolumeClass = 0;
        r.Volume = 100.0f;
        r.VolumeTarget = 100.0f;
        r.VolumeStep = 100.0f;
        Emit(AudioCommand.OpenHandle(s, bank));
        PlayCall(s, bank, bankEntry, 0, SoundMath.I8(_vol[0xD]) * 0.01f);
        _voiceState = 2;
        _voiceStream = s;
        _voiceElapsed = 0;
        if (remember)
        {
            LastVoice = entry;
        }

        return true;
    }

    /// <summary>
    /// The voice stop of the advance (VA 0xA1015..0xA1040, 0xA1149, 0xA124D): true when the message ending stops the voice, that is
    /// <c>(F1123 == 0 &amp;&amp; config +0x4E) || skip</c>.
    /// </summary>
    public bool ShouldStopVoiceOnAdvance(int f1123 = 0) => (f1123 == 0 && Config.StopOnAdvance != 0) || Skip;

    /// <summary>Applies <see cref="ShouldStopVoiceOnAdvance"/>: stops the voice when it says so. Returns what it said.</summary>
    public bool StopVoiceOnAdvance(int f1123 = 0)
    {
        bool stop = ShouldStopVoiceOnAdvance(f1123);
        if (stop)
        {
            StopVoice();
        }

        return stop;
    }

    /// <summary>VA 0x8EA0 / 0x8F60: stops streams 2..5 (every caller passes fade 0) and ends the voice state.</summary>
    public void StopVoice()
    {
        for (int s = AudioStreams.FirstVoice; s < AudioStreams.FirstSe; s++)
        {
            if (_isPlaying(s))
            {
                StopCall(s);
            }
        }

        _voiceState = 0;
        _voiceStream = -1;
    }

    /// <summary>The end of the script voice, at the end of the frame update: the stream stopped, so the voice state returns to 0 (VA 0x9220).</summary>
    private void EndOfVoice(int dt)
    {
        if (_voiceState == 0)
        {
            return;
        }

        if (!_isPlaying(_voiceStream))
        {
            Emit(AudioCommand.Close(_voiceStream));
            _voiceState = 0;
            _voiceStream = -1;
            return;
        }

        _voiceElapsed += dt;
    }

    /// <summary>
    /// <c>SystemSE_Play(id)</c> (VA 0xCC670; sound_runtime.md 10.1): the HE-VAG pair of the id on the next of the four system voices
    /// 0x2C..0x2F, left hard left and right hard right, at <c>volume byte 0xF * 0.01</c>. A mono sound (right &lt; 0) plays centred,
    /// and a pair with left &lt;= 0 plays nothing (the voice is still consumed). Returns true when a sound was commanded.
    /// </summary>
    public bool SystemSePlay(int id)
    {
        (int left, int right) = SystemSounds.Vags(id);
        float vol = SoundMath.I8(_vol[0xF]) * 0.01f;
        int voice = AudioStreams.FirstSystemVoice + _systemCounter;
        _systemCounter = (_systemCounter + 1) & 3;
        if (left <= 0)
        {
            return false;
        }

        Emit(AudioCommand.PlaySystem(voice, left, right, vol));
        return true;
    }
}
