namespace Tsuki.Engine.Contracts;

/// <summary>
/// Stream numbers of the sound library (docs/formats/sound_runtime.md 2.1; ARCHITECTURE 7.1).
/// </summary>
public static class AudioStreams
{
    /// <summary>BGM slot 0; the two BGM slots alternate on every new track.</summary>
    public const int Bgm0 = 0;

    /// <summary>BGM slot 1.</summary>
    public const int Bgm1 = 1;

    /// <summary>First voice stream; voices rotate over streams 2..5 and only one plays at a time.</summary>
    public const int FirstVoice = 2;

    /// <summary>Number of voice streams.</summary>
    public const int VoiceCount = 4;

    /// <summary>First script SE stream: SE channel c (0..19) plays on stream 6 + c.</summary>
    public const int FirstSe = 6;

    /// <summary>Number of script SE channels.</summary>
    public const int SeChannelCount = 20;

    /// <summary>Number of decoder streams (BGM, voice and SE).</summary>
    public const int Count = 26;

    /// <summary>First HE-VAG voice of the system sounds (0x2C..0x2F, sound_runtime.md 10.1).</summary>
    public const int FirstSystemVoice = 0x2C;

    /// <summary>Number of HE-VAG voices used by <c>SystemSE_Play</c>.</summary>
    public const int SystemVoiceCount = 4;

    /// <summary>The stream of script SE channel <paramref name="channel"/>.</summary>
    public static int Se(int channel) => FirstSe + channel;
}

/// <summary>
/// Sound library calls (nexl_Sound, docs/formats/sound_runtime.md 2.5), named as
/// <c>tsuki.misc_audio_runtime_semantics.SoundModel.calls</c> records them. <c>is_playing</c> is a
/// query, not a command: the engine keeps its own stream status.
/// </summary>
public enum AudioOp : byte
{
    /// <summary><c>open_file(stream)</c>: BGM opens bgm.mrg.</summary>
    OpenFile,

    /// <summary><c>open_handle(stream)</c>: SE (the vse.mrg handle) or voice (the decoded RIFF in memory).</summary>
    OpenHandle,

    /// <summary><c>close(stream)</c>.</summary>
    Close,

    /// <summary><c>play(stream, bank entry, loop, volume)</c>.</summary>
    Play,

    /// <summary><c>stop(stream, fade frames)</c>; every caller passes 0.</summary>
    Stop,

    /// <summary><c>set_volume(stream, volume 0..1, time)</c>; every caller passes time 0.</summary>
    SetVolume,

    /// <summary><c>set_volume0(stream, volume)</c>: the volume before play.</summary>
    SetVolume0,

    /// <summary><c>set_pan(stream, pan -1..1)</c>.</summary>
    SetPan,

    /// <summary><c>set_loop(stream, on, count)</c>: (1, -1) forever, (0, 1) once.</summary>
    SetLoop,

    /// <summary><c>set_start_sample(stream, sample)</c> (tag <c>`069</c>).</summary>
    SetStartSample,

    /// <summary><c>set_flag6(stream, on)</c>.</summary>
    SetFlag6,

    /// <summary><c>SystemSE_Play</c> (sound_runtime.md 10.1): HE-VAG pair on a system voice.</summary>
    PlaySystem,
}

/// <summary>
/// One sound command of a 60 Hz step, as the SoundModel outputs it (ARCHITECTURE 3.2, 7.3;
/// docs/formats/sound_runtime.md 2.5, 6, 12). Volumes and pans are final values: the mixer applies
/// them with linear ramps and the pan law of ARCHITECTURE 7.1 and never re-derives them.
/// </summary>
/// <param name="Op">The call.</param>
/// <param name="Stream">The stream (see <see cref="AudioStreams"/>).</param>
/// <param name="Bank">The bank (open and play).</param>
/// <param name="Entry">The bank entry (play), or the left vag entry (play system).</param>
/// <param name="Arg0">Loop flag (play), fade frames (stop), on (set loop, set flag 6), start sample, or the right vag entry (play system, -1 for none).</param>
/// <param name="Arg1">Loop count (set loop).</param>
/// <param name="Value0">Volume 0..1 (play, set volume, play system) or pan -1..1 (set pan).</param>
/// <param name="Value1">Ramp time (set volume).</param>
public readonly record struct AudioCommand(
    AudioOp Op, int Stream, AudioBank Bank = AudioBank.Bgm, int Entry = 0, int Arg0 = 0, int Arg1 = 0, float Value0 = 0f, float Value1 = 0f)
{
    /// <summary><c>open_file</c>.</summary>
    public static AudioCommand OpenFile(int stream, AudioBank bank) => new(AudioOp.OpenFile, stream, bank);

    /// <summary><c>open_handle</c>.</summary>
    public static AudioCommand OpenHandle(int stream, AudioBank bank) => new(AudioOp.OpenHandle, stream, bank);

    /// <summary><c>close</c>.</summary>
    public static AudioCommand Close(int stream) => new(AudioOp.Close, stream);

    /// <summary><c>play</c>.</summary>
    public static AudioCommand Play(int stream, AudioBank bank, int entry, bool loop, float volume) =>
        new(AudioOp.Play, stream, bank, entry, Arg0: loop ? 1 : 0, Value0: volume);

    /// <summary><c>stop</c>.</summary>
    public static AudioCommand Stop(int stream, int fadeFrames = 0) => new(AudioOp.Stop, stream, Arg0: fadeFrames);

    /// <summary><c>set_volume</c>.</summary>
    public static AudioCommand SetVolume(int stream, float volume, float time = 0f) =>
        new(AudioOp.SetVolume, stream, Value0: volume, Value1: time);

    /// <summary><c>set_volume0</c>.</summary>
    public static AudioCommand SetVolume0(int stream, float volume) => new(AudioOp.SetVolume0, stream, Value0: volume);

    /// <summary><c>set_pan</c>.</summary>
    public static AudioCommand SetPan(int stream, float pan) => new(AudioOp.SetPan, stream, Value0: pan);

    /// <summary><c>set_loop</c>.</summary>
    public static AudioCommand SetLoop(int stream, bool on, int count) =>
        new(AudioOp.SetLoop, stream, Arg0: on ? 1 : 0, Arg1: count);

    /// <summary><c>set_start_sample</c>.</summary>
    public static AudioCommand SetStartSample(int stream, int sample) => new(AudioOp.SetStartSample, stream, Arg0: sample);

    /// <summary><c>set_flag6</c>.</summary>
    public static AudioCommand SetFlag6(int stream, bool on) => new(AudioOp.SetFlag6, stream, Arg0: on ? 1 : 0);

    /// <summary>
    /// <c>SystemSE_Play</c>: vag <paramref name="left"/> hard left and <paramref name="right"/> hard
    /// right on system voice <paramref name="voice"/>; with <paramref name="right"/> &lt; 0 the left
    /// entry plays centred (sound_runtime.md 10.1).
    /// </summary>
    public static AudioCommand PlaySystem(int voice, int left, int right, float volume) =>
        new(AudioOp.PlaySystem, voice, AudioBank.Vag, left, Arg0: right, Value0: volume);
}

/// <summary>Movie calls (ARCHITECTURE 3.2, 7.5; docs/formats/archives_ui_movies.md 9).</summary>
public enum MovieOp : byte
{
    /// <summary>Open and start a movie table record in a slot.</summary>
    Open,

    /// <summary>Stop and close the movie in a slot.</summary>
    Stop,

    /// <summary>Set the movie audio volume (config +0x54, sound_runtime.md 2.4).</summary>
    SetVolume,
}

/// <summary>
/// One movie command of a 60 Hz step (ARCHITECTURE 3.2, 7.5). Slot numbers are ours: a slot is one
/// open movie (the full-screen movie of <c>_TOMP</c>, or a looping title background).
/// </summary>
/// <param name="Op">The call.</param>
/// <param name="Slot">The movie slot.</param>
/// <param name="MovieIndex">Movie table index after the language remap (archives_ui_movies.md 9.2-9.3).</param>
/// <param name="Loop">Loop at the end (the title backgrounds, system_flow.md 4.2).</param>
/// <param name="Volume">Audio volume 0..1.</param>
public readonly record struct MovieCommand(MovieOp Op, int Slot, int MovieIndex = -1, bool Loop = false, float Volume = 0f)
{
    /// <summary>Open and start <paramref name="movieIndex"/> in <paramref name="slot"/>.</summary>
    public static MovieCommand Open(int slot, int movieIndex, bool loop, float volume) =>
        new(MovieOp.Open, slot, movieIndex, loop, volume);

    /// <summary>Stop the movie in <paramref name="slot"/>.</summary>
    public static MovieCommand Stop(int slot) => new(MovieOp.Stop, slot);

    /// <summary>Set the audio volume of <paramref name="slot"/>.</summary>
    public static MovieCommand SetVolume(int slot, float volume) => new(MovieOp.SetVolume, slot, Volume: volume);
}

/// <summary>
/// The movie clock reported back to the engine once per step (ARCHITECTURE 7.5): the clock is the
/// movie's audio, or the frame clock when the track is silent. In virtual time (the explorer,
/// ARCHITECTURE 10.3) a movie ends as soon as it is opened and probed.
/// </summary>
/// <param name="Slot">The movie slot.</param>
/// <param name="MovieIndex">The movie table index playing in the slot, or -1.</param>
/// <param name="Playing">True while the movie plays.</param>
/// <param name="Ended">True once the last frame has been presented (what <c>_TOMW</c> waits for).</param>
/// <param name="PositionMicroseconds">Presentation clock in microseconds.</param>
public readonly record struct MovieClock(int Slot, int MovieIndex, bool Playing, bool Ended, long PositionMicroseconds);
