using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Sound;

/// <summary>
/// The sound engine's state and per-frame update: the BGM slots, the voice streams, the 20 script SE channels, the deferred-SE
/// queue, ducking and the voice gate, producing <see cref="AudioCommand"/>s (ARCHITECTURE 7.3; docs/formats/sound_runtime.md).
/// Port of <c>tsuki.misc_audio_runtime_semantics.SoundModel</c>, which was checked against the eboot's own code under emulation:
/// every float operation is float32 in the engine's order, so the channel records match the engine bit for bit.
/// </summary>
/// <remarks>
/// <para>
/// One instance is the sound state of a running game. The script handlers (<see cref="Execute(in Statement)"/>) run on the script thread;
/// <see cref="Update"/> and <see cref="DuckUpdate(bool)"/> run once per 60 Hz frame. Every sound library call the engine would make
/// is appended to the command list (<see cref="TakeCommands()"/>) in the order the engine makes it; <c>is_playing</c> is a query
/// through <see cref="IsPlaying"/>, not a command. Not thread-safe.
/// </para>
/// <para>
/// The channel records are the L-1 <see cref="SoundChannelRecord"/> blocks (streams 0..1 BGM, 2..5 voice, 6..25 SE), so a save
/// or a history record can copy them as bytes.
/// </para>
/// </remarks>
public sealed partial class SoundModel
{
    /// <summary>Number of BGM slots (streams 0, 1).</summary>
    public const int BgmSlots = 2;

    /// <summary>Number of script SE channels (streams 6..25).</summary>
    public const int SeChannels = AudioStreams.SeChannelCount;

    private readonly SoundChannelRecord[] _bgm = Enumerable.Range(0, BgmSlots).Select(_ => new SoundChannelRecord()).ToArray();
    private readonly SoundChannelRecord[] _voice = Enumerable.Range(0, AudioStreams.VoiceCount).Select(_ => new SoundChannelRecord()).ToArray();
    private readonly SoundChannelRecord[] _se = Enumerable.Range(0, SeChannels).Select(_ => new SoundChannelRecord()).ToArray();
    private readonly byte[] _vol = new byte[16];                    // the volume block 0x17DA268
    private readonly int[] _bgmLoop = [1, 1];                       // 0x624380
    private readonly int[] _seLoop = new int[SeChannels];           // 0xDD8FC0
    private readonly int[] _scrSeId = new int[SeChannels];          // 0x1654210
    private readonly int[] _scrSeLoopId = new int[SeChannels];      // 0x1654240
    private readonly int[] _scrSeVol = new int[SeChannels];         // 0x1654270
    private readonly int[] _scrSeLoop = new int[SeChannels];        // 0x16542A0
    private readonly int[] _scrSePan = new int[SeChannels];         // 0x16542D0
    private readonly int[] _scrSeChar = new int[SeChannels];        // 0x1654330
    private readonly DeferredSe[] _defer = Enumerable.Range(0, SeChannels).Select(_ => new DeferredSe { Timer = 0, Kind = 0, SeId = 0, Fade = 0, Vol = 0, Vol2 = 0, Pan = 0, Pan2 = 0, Loop = 0, Char = 0 }).ToArray();
    private readonly SortedDictionary<int, int> _musicBits = [];    // bit array 0x164D5C0: byte index -> value
    private readonly List<AudioCommand> _out = [];
    private Func<int, bool> _isPlaying = static _ => true;
    private int _slot;                                              // 0xDD8F28
    private int _track = -1;                                        // 0x62437C
    private int _scrTrack;                                          // 0x16545D0
    private int _scrBgmVol;                                         // 0x1654238
    private int _duckLevel = 256;                                   // 0x7A6184
    private int _duckApplied = -1;                                  // 0x7A608C
    private int _vsyncs = 1;

    /// <summary>Creates the sound state of a fresh game (all records idle, the BGM loop flags 1).</summary>
    /// <param name="config">The config the sound code reads; null = the boot defaults.</param>
    /// <param name="seLookup">Resolves a script SE name (<c>MOON_SE03028</c>) and the search flags to a vse entry (eboot VA 0xCC790); null = no SE names.</param>
    /// <param name="isPlaying">Asks whether a sound stream plays (the library's <c>is_playing</c>); null = every stream plays.</param>
    public SoundModel(SoundConfig? config = null, Func<string, int, int?>? seLookup = null, Func<int, bool>? isPlaying = null)
    {
        Config = config ?? new SoundConfig();
        SeLookup = seLookup ?? (static (_, _) => null);
        if (isPlaying is not null)
        {
            _isPlaying = isPlaying;
        }

        Config.FillVolumeBlock(_vol);
    }

    /// <summary>The config the sound code reads.</summary>
    public SoundConfig Config { get; }

    /// <summary>Resolves a script SE name and the search flags to a vse entry, or null (VA 0xCC790).</summary>
    public Func<string, int, int?> SeLookup { get; set; }

    /// <summary>
    /// The library's <c>is_playing</c> (sound_runtime.md 2.5): the model consults it to stop idle streams and to skip ramps of streams
    /// that ended. The default says every stream plays, which is what the reference traces assume. A running game passes the answer
    /// of the audio layer (<c>AudioCommandPlayer.IsPlaying</c>); the explorer's virtual time passes <c>_ =&gt; false</c>, so that a
    /// voice ends at the next update instead of waiting for a mixer that never runs.
    /// </summary>
    public Func<int, bool> IsPlaying
    {
        get => _isPlaying;
        set => _isPlaying = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The SE notification hook (config +0x2C, VA 0xE7EE0 / 0xCD610): called with (channel, vse entry) after every started SE when <see cref="SoundConfig.SeCaption"/> is 1.</summary>
    public Action<int, int>? SeStarted { get; set; }

    /// <summary>"ff" (word 0x120A6D2): history fast-forward / replay; sound commands only update the script-side state.</summary>
    public bool FastForward { get; set; }

    /// <summary>"scan" (byte 0x174DE48): history scan / restore; sound commands only update the script-side state.</summary>
    public bool Scan { get; set; }

    /// <summary>Skip mode (word 0x17DB232): one-shot SEs are dropped, voices follow <see cref="SoundConfig.SkipVoice"/>.</summary>
    public bool Skip { get; set; }

    /// <summary>Vsyncs since the last frame (0xD47C0 returns min(n, 3)): the deferred-SE timers count down by it.</summary>
    public int Vsyncs
    {
        get => _vsyncs;
        set => _vsyncs = value;
    }

    /// <summary>The wait in 16 ms units that the last <c>_MFAD</c> returned (word 0x1653E08).</summary>
    public int Wait16 { get; private set; }

    /// <summary>The current BGM slot (0 or 1; u32 0xDD8F28).</summary>
    public int CurrentSlot => _slot;

    /// <summary>The track started last, -1 = none (0x62437C).</summary>
    public int CurrentTrack => _track;

    /// <summary>The track of the last <c>_MPLY</c>, -1 after <c>_MPLY(-n)</c> or <c>_MFAD</c> (0x16545D0).</summary>
    public int ScriptTrack => _scrTrack;

    /// <summary>The volume of the last <c>_MPLY</c> / <c>_MVOL</c> (0x1654238).</summary>
    public int ScriptBgmVolume => _scrBgmVol;

    /// <summary>The ducking level, 256 = full (0x7A6184).</summary>
    public int DuckLevel => _duckLevel;

    /// <summary>The volume block byte <paramref name="index"/> (0xB movie, 0xC BGM, 0xD voice, 0xE SE, 0xF system SE; 0x17DA268).</summary>
    public int VolumeByte(int index) => _vol[index];

    /// <summary>The "track heard" bit array (music room unlock, bit 3200 + track; 0x164D5C0): byte index to value.</summary>
    public IReadOnlyDictionary<int, int> MusicBits => _musicBits;

    /// <summary>The script's per-channel SE state (read-only views of 0x1654210.., for history records and the reconciler).</summary>
    public ScriptSeState ScriptSe => new(this);

    /// <summary>The channel record of sound stream <paramref name="stream"/> (0..25; 0..1 BGM, 2..5 voice, 6..25 SE).</summary>
    public SoundChannelRecord Record(int stream)
    {
        if ((uint)stream >= AudioStreams.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(stream), stream, "streams are 0..25");
        }

        return stream < AudioStreams.FirstVoice ? _bgm[stream] : stream < AudioStreams.FirstSe ? _voice[stream - AudioStreams.FirstVoice] : _se[stream - AudioStreams.FirstSe];
    }

    /// <summary>Removes and returns the commands produced since the last call, in the order the engine makes the library calls.</summary>
    public AudioCommand[] TakeCommands()
    {
        if (_out.Count == 0)
        {
            return [];
        }

        AudioCommand[] a = [.. _out];
        _out.Clear();
        return a;
    }

    /// <summary>Appends the commands produced since the last call to <paramref name="into"/> and clears them.</summary>
    public void TakeCommands(List<AudioCommand> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.AddRange(_out);
        _out.Clear();
    }

    /// <summary>The commands produced since the last <see cref="TakeCommands()"/>, without removing them.</summary>
    public IReadOnlyList<AudioCommand> PendingCommands => _out;

    /// <summary>Number of commands waiting.</summary>
    public int PendingCommandCount => _out.Count;

    /// <summary><c>Sound_ApplyConfig(full)</c> (VA 0x3E9B0 -> 0x674B0): refreshes the volume block from the config and forces the ducking to re-apply.</summary>
    public void ApplyConfig()
    {
        Config.FillVolumeBlock(_vol);
        _duckApplied = -1;
    }

    // -- backend ---------------------------------------------------------------------------------
    private void Emit(AudioCommand c) => _out.Add(c);

    private void PlayCall(int s, AudioBank bank, int entry, int loop, float v) => Emit(AudioCommand.Play(s, bank, entry, loop != 0, v));

    private void StopCall(int s)
    {
        Emit(AudioCommand.Stop(s));
        Emit(AudioCommand.Close(s));
    }

    private void SetVolumeCall(int s, float v) => Emit(AudioCommand.SetVolume(s, v, 0f));

    // -- BGM primitives --------------------------------------------------------------------------

    /// <summary>VA 0x9A90: stop + close the current slot now, track = -1 (the record keeps its state; the frame update will not touch a stream that does not play).</summary>
    private void BgmStop()
    {
        Emit(AudioCommand.Stop(_slot));
        Emit(AudioCommand.Close(_slot));
        _track = -1;
    }

    /// <summary>VA 0x9EE0 (<c>_MSTP</c>, <c>_MFAD</c>, the <c>_MPLY</c> cross-fade): the current slot -> state 2, target 0.</summary>
    private void BgmFadeOut(float rate)
    {
        SoundChannelRecord r = _bgm[_slot];
        if (r.State != 0)
        {
            r.State = 2;
            r.VolumeStep = rate;
            r.VolumeTarget = 0f;
        }

        _track = -1;
    }

    private int BgmEntry(int track) => Config.AltBgm == 1 && track - 1 >= 0 && track - 1 < 0x83 ? track + 0x83 : track;

    /// <summary>The previous slot fades out at 4.0 per frame when a new track starts on the other slot (VA 0x98C0 / 0x9B40).</summary>
    private void BgmReplaceOld()
    {
        SoundChannelRecord old = _bgm[_slot];
        if (old.State == 1)
        {
            old.State = 2;
            old.VolumeTarget = 0f;
            old.VolumeStep = SoundMath.BgmReplaceFade;
        }
    }

    /// <summary>VA 0x98C0: start <paramref name="track"/> at volume <paramref name="vol"/> on the other slot, no fade.</summary>
    private void BgmStart(int track, float vol)
    {
        if (track < 0)
        {
            BgmStop();
            return;
        }

        if (_track == track)
        {
            return;
        }

        BgmReplaceOld();
        _track = track;
        _slot ^= 1;
        SoundChannelRecord r = _bgm[_slot];
        int entry = BgmEntry(track);
        r.State = 1;
        r.VolumeClass = 0;
        r.Entry = unchecked((ushort)entry);
        r.Volume = vol;
        r.VolumeTarget = vol;
        r.VolumeStep = vol;
        ClearPans(r);
        int g = SoundMath.Trunc(SoundMath.I8(_vol[0xC]) * vol * 0.01f);
        Emit(AudioCommand.OpenFile(_slot, AudioBank.Bgm));
        PlayCall(_slot, AudioBank.Bgm, entry, _bgmLoop[_slot] != 0 ? 1 : 0, SoundMath.I16(g) * 0.01f);
        Emit(AudioCommand.SetFlag6(_slot, false));
    }

    /// <summary>VA 0x9B40: start <paramref name="track"/> from volume 0 ramping to <paramref name="vol"/> at <paramref name="rate"/> per frame.</summary>
    private void BgmStartFade(int track, int vol, float rate)
    {
        if (track < 0)
        {
            BgmStop();
            return;
        }

        if (_track == track)
        {
            return;
        }

        BgmReplaceOld();
        _track = track;
        _slot ^= 1;
        SoundChannelRecord r = _bgm[_slot];
        int entry = BgmEntry(track);
        r.State = 1;
        r.VolumeClass = 0;
        r.Entry = unchecked((ushort)entry);
        r.Volume = 0f;
        r.VolumeStep = rate;
        r.VolumeTarget = vol;
        Emit(AudioCommand.OpenFile(_slot, AudioBank.Bgm));
        Emit(AudioCommand.SetVolume0(_slot, 0f));
        PlayCall(_slot, AudioBank.Bgm, entry, _bgmLoop[_slot] != 0 ? 1 : 0, 0f);
    }

    /// <summary>VA 0x9EA0: both slots' loop flags; the current slot's stream gets loop (on, count -1) for flag 1, else (off, 1).</summary>
    private void BgmSetLoop(int flag)
    {
        _bgmLoop[0] = flag;
        _bgmLoop[1] = flag;
        if (flag == 1)
        {
            Emit(AudioCommand.SetLoop(_slot, true, -1));
        }
        else
        {
            Emit(AudioCommand.SetLoop(_slot, false, 1));
        }
    }

    private static void ClearPans(SoundChannelRecord r)
    {
        r.Pan = 0f;
        r.PanTarget = 0f;
        r.PanStep = 0f;
        r.Ramp3 = 0f;
        r.Ramp3Target = 0f;
        r.Ramp3Step = 0f;
    }

    // -- per frame -------------------------------------------------------------------------------

    /// <summary>
    /// BGM ducking in the ADV frame (VA 0x694B9..0x6955F): the level (256 = full) falls by 4 per frame to 128 while a voice plays and
    /// config +0x1C is set, else rises by 2 to 256; on a change the BGM volume byte becomes <c>level * BGM / 256</c>. Returns the new
    /// BGM volume byte, or null when nothing changed.
    /// </summary>
    /// <param name="voiceActive">The voice state word is non-zero.</param>
    public int? DuckUpdate(bool voiceActive)
    {
        int lv = _duckLevel;
        if (voiceActive && Config.Duck != 0)
        {
            if (lv >= 0x81)
            {
                lv = Math.Max(lv, 0x84) - 4;
            }
        }
        else if (lv <= 0xFF)
        {
            lv = Math.Min(lv, 0xFE) + 2;
        }

        _duckLevel = lv;
        if (lv == _duckApplied)
        {
            return null;
        }

        _duckApplied = lv;
        float x = (float)lv * (float)(1 - SoundMath.I8(Config.MuteBgm));
        x *= SoundMath.I16(Config.Bgm);
        x = x * 256.0f / 100.0f * 0.001953125f;
        x = x * 100.0f * 0.0078125f;
        x = SoundMath.MinSs(100.0f, SoundMath.MaxSs(0f, x));
        int v = SoundMath.Trunc(x);
        _vol[0xC] = unchecked((byte)v);
        SoundChannelRecord r = _bgm[_slot];
        if (r.State != 0)
        {
            int g = SoundMath.Trunc(SoundMath.I8(v) * r.Volume * 0.01f);
            g = Math.Min(Math.Max(g, 0), 100);
            SetVolumeCall(_slot, g * 0.01f);
        }

        return v;
    }

    /// <summary>
    /// The ducking step with the voice state of the model itself (<see cref="VoiceState"/> != 0 means a voice plays).
    /// </summary>
    public int? DuckUpdate() => DuckUpdate(VoiceState != 0);

    /// <summary>
    /// The sound frame update, VA 0xAD80(dt) (the ADV frame passes <paramref name="dt"/> = 1 when a vsync elapsed, else 0): the deferred
    /// timers, the BGM, voice and SE volume / pan ramps and the fade-to-stop, then the end of the script voice.
    /// </summary>
    public void Update(int dt = 1)
    {
        int dec = Math.Min(_vsyncs, 3) * dt;
        for (int ch = 0; ch < SeChannels; ch++)                       // VA 0x880F0
        {
            int t = _defer[ch].Timer;
            if (SoundMath.I16(t) > 0)
            {
                t = SoundMath.I16(t - dec);
                _defer[ch].Timer = t;
                if (t <= 0)
                {
                    RunDeferred(ch);
                }
            }
        }

        float dtf = dt;
        for (int s = 0; s < BgmSlots; s++)
        {
            SoundChannelRecord r = _bgm[s];
            if (r.State == 0)
            {
                if (_isPlaying(s))
                {
                    StopCall(s);
                }

                continue;
            }

            float rate = r.VolumeStep;
            if (!(0f < rate || float.IsNaN(rate)))
            {
                rate = SoundMath.RampDefault;
                r.VolumeStep = SoundMath.RampDefault;
            }

            if (!_isPlaying(s))
            {
                continue;
            }

            if (!(r.Volume == r.VolumeTarget))
            {
                r.Volume = SoundMath.Ramp(r.Volume, r.VolumeTarget, rate * dtf);
                int g = SoundMath.Trunc(r.Volume * SoundMath.I8(_vol[0xC]) * 0.01f);
                g = Math.Min(Math.Max(g, 0), 100);
                SetVolumeCall(s, g * 0.01f);
            }

            if (r.State == 2 && !(0f < r.Volume || float.IsNaN(r.Volume)))
            {
                r.State = 0;
                StopCall(s);
            }
        }

        for (int s = AudioStreams.FirstVoice; s < AudioStreams.FirstSe; s++)
        {
            SoundChannelRecord r = _voice[s - AudioStreams.FirstVoice];
            if (r.State == 0)
            {
                continue;
            }

            float rate = r.VolumeStep;
            if (!(0f < rate || float.IsNaN(rate)))
            {
                rate = SoundMath.RampDefault;
                r.VolumeStep = SoundMath.RampDefault;
            }

            if (_isPlaying(s) && !(r.Volume == r.VolumeTarget))
            {
                r.Volume = SoundMath.Ramp(r.Volume, r.VolumeTarget, rate * dtf);
                int vb = r.VolumeClass == 1 ? 0xE : (r.VolumeClass == 3 ? 0xF : 0xD);
                int g = SoundMath.Trunc(r.Volume * SoundMath.I8(_vol[vb]) * 0.01f);
                g = Math.Min(Math.Max(g, 0), 100);
                SetVolumeCall(s, g * 0.01f);
            }

            if (r.State == 2 && !(0f < r.Volume || float.IsNaN(r.Volume)))
            {
                r.State = 0;
                SetVolumeCall(s, 0f);
                StopCall(s);
            }
        }

        for (int s = AudioStreams.FirstSe; s < AudioStreams.Count; s++)
        {
            int ch = s - AudioStreams.FirstSe;
            SoundChannelRecord r = _se[ch];
            if (r.State == 0)
            {
                if (_isPlaying(s))
                {
                    StopCall(s);
                }

                continue;
            }

            if (r.Started == 0)
            {
                continue;
            }

            bool playing = _isPlaying(s);
            float rate = r.VolumeStep;
            if (!(0f < rate || float.IsNaN(rate)))
            {
                rate = SoundMath.RampDefault;
                r.VolumeStep = SoundMath.RampDefault;
            }

            if (playing)
            {
                r.Volume = SoundMath.Ramp(r.Volume, r.VolumeTarget, rate * dtf, always: true);
                int vb = r.VolumeClass == 1 ? 0xE : (r.VolumeClass == 3 ? 0xF : 0xD);
                int c = r.Character;
                int g = c - 1 is >= 0 and <= 0x12 ? Config.CharGain(c - 1) : SoundMath.I8(_vol[vb]);
                float x = g * r.Volume * 0.01f;
                float v = x < 0 ? 0f : SoundMath.MinSs(100.0f, x) * 0.01f;
                SetVolumeCall(s, v);
                if (g == 0 && r.Volume > r.VolumeTarget)
                {
                    StopCall(s);
                    r.State = 0;
                }

                if (r.PanStep != 0 || float.IsNaN(r.PanStep))
                {
                    (float pan, float panRate) = SoundMath.Sweep(r.Pan, r.PanTarget, r.PanStep, dtf);
                    r.Pan = pan;
                    r.PanStep = panRate;
                    Emit(AudioCommand.SetPan(s, r.Pan * 0.001f));
                }

                if (r.Ramp3Step != 0 || float.IsNaN(r.Ramp3Step))
                {
                    (float p3, float p3Rate) = SoundMath.Sweep(r.Ramp3, r.Ramp3Target, r.Ramp3Step, dtf);
                    r.Ramp3 = p3;
                    r.Ramp3Step = p3Rate;
                }
            }

            if (r.State == 2 && !(0f < r.Volume || float.IsNaN(r.Volume)))
            {
                r.State = 0;
                StopCall(s);
            }
        }

        EndOfVoice(dt);
    }

    /// <summary>
    /// The state of the model as a list of 32-bit words in a fixed order (the integer globals, the script arrays and the deferred queue;
    /// the channel records are separate: <see cref="Record"/>). Used by the trace tests to compare every step with the reference model.
    /// </summary>
    public int[] StateWords()
    {
        var w = new List<int>(512) { _slot, _track, _bgmLoop[0], _bgmLoop[1] };
        w.AddRange(_seLoop);
        w.Add(_scrTrack);
        w.Add(_scrBgmVol);
        w.AddRange(_scrSeId);
        w.AddRange(_scrSeLoopId);
        w.AddRange(_scrSeVol);
        w.AddRange(_scrSeLoop);
        w.AddRange(_scrSePan);
        w.AddRange(_scrSeChar);
        w.Add(_voicePending);
        w.Add(_voiceChar);
        w.Add(_voiceArg3);
        for (int i = 0xB; i <= 0xF; i++)
        {
            w.Add(_vol[i]);
        }

        w.Add(_duckLevel);
        w.Add(_duckApplied);
        foreach (DeferredSe d in _defer)
        {
            w.Add(d.Timer);
            w.Add(d.Kind);
            w.Add(d.SeId);
            w.Add(unchecked((int)d.Fade));
            w.Add((int)(d.Fade >> 32));
            w.Add(d.Vol);
            w.Add(d.Vol2);
            w.Add(d.Pan);
            w.Add(d.Pan2);
            w.Add(d.Loop);
            w.Add(d.Char);
        }

        w.Add(_musicBits.Count);
        foreach ((int k, int v) in _musicBits)
        {
            w.Add(k);
            w.Add(v);
        }

        return [.. w];
    }

    /// <summary>The channel records of streams 0..25 as one byte array (26 x 0x34 bytes), for traces and snapshots.</summary>
    public byte[] RecordBytes()
    {
        var all = new byte[AudioStreams.Count * SoundChannelRecord.Size];
        for (int s = 0; s < AudioStreams.Count; s++)
        {
            Record(s).Bytes.CopyTo(all.AsSpan(s * SoundChannelRecord.Size));
        }

        return all;
    }
}

/// <summary>One deferred SE command (0x18 bytes at 0x16543F0 + 0x18 c; sound_runtime.md 5).</summary>
internal sealed class DeferredSe
{
    /// <summary>Frames until the command runs (i16).</summary>
    public int Timer;

    /// <summary>1 SEPL, 2 SEFD, 3 SEVL, 4 SEAC (engine-only).</summary>
    public int Kind;

    /// <summary>The vse entry (i16).</summary>
    public int SeId;

    /// <summary>The <c>`001</c> time in ms (-1 absent; i64).</summary>
    public long Fade;

    /// <summary>The <c>`017</c> volume byte (0xFF absent).</summary>
    public int Vol;

    /// <summary>Second volume (SEAC).</summary>
    public int Vol2;

    /// <summary>The raw <c>`018</c> byte (0xFF absent).</summary>
    public int Pan;

    /// <summary>Second pan (SEAC).</summary>
    public int Pan2;

    /// <summary>The loop argument byte.</summary>
    public int Loop;

    /// <summary>The <c>`070</c> character.</summary>
    public int Char;

    /// <summary>State after execution or cancel (VA 0x88098): timer 0, kind 0, id -1, bytes +8..+0x14 = 0xFF, +0x15 = 0.</summary>
    public void Clear()
    {
        Timer = 0;
        Kind = 0;
        SeId = -1;
        Fade = -1;
        Vol = 0xFF;
        Vol2 = 0xFF;
        Pan = 0xFF;
        Pan2 = 0xFF;
        Loop = 0xFF;
        Char = 0;
    }
}

/// <summary>Read-only views of the script-side SE arrays (the history record and the reconciler read them).</summary>
public readonly struct ScriptSeState
{
    private readonly SoundModel _m;

    internal ScriptSeState(SoundModel m) => _m = m;

    /// <summary>The vse entry of the last <c>_SEPL</c> on channel <paramref name="ch"/> (0x1654210).</summary>
    public int LastEntry(int ch) => _m.ScriptSeIdAt(ch);

    /// <summary>The vse entry of the channel's looping SE, -1 = none (0x1654240).</summary>
    public int LoopEntry(int ch) => _m.ScriptSeLoopIdAt(ch);

    /// <summary>The script volume (0x1654270).</summary>
    public int Volume(int ch) => _m.ScriptSeVolAt(ch);

    /// <summary>1 = the channel's last SE was looped (0x16542A0).</summary>
    public int Looped(int ch) => _m.ScriptSeLoopAt(ch);

    /// <summary>The pan -1000..1000 (0x16542D0).</summary>
    public int Pan(int ch) => _m.ScriptSePanAt(ch);

    /// <summary>The <c>`070</c> character (0x1654330).</summary>
    public int Character(int ch) => _m.ScriptSeCharAt(ch);
}
