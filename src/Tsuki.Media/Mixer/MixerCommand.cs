using Tsuki.Engine.Contracts;

namespace Tsuki.Media.Mixer;

/// <summary>What a <see cref="MixerCommand"/> does.</summary>
public enum MixerOp : byte
{
    /// <summary>Start a clip on a voice (replacing what it played), gain and pan set at once.</summary>
    Play,

    /// <summary>Start a streaming source (the movie AAC ring) on a voice.</summary>
    PlayStream,

    /// <summary>Stop a voice at once (every engine caller passes fade 0; fades are gain ramps).</summary>
    Stop,

    /// <summary>Ramp the voice gain to a new value.</summary>
    SetGain,

    /// <summary>Ramp the voice pan (-1 left .. 1 right) to a new value.</summary>
    SetPan,

    /// <summary>Stop every voice.</summary>
    StopAll,
}

/// <summary>
/// A streaming PCM source for a mixer voice (the movie audio, ARCHITECTURE 7.5). <see cref="Read"/> runs on the audio
/// thread and must not block or allocate.
/// </summary>
public interface IMixerSource
{
    /// <summary>1 or 2.</summary>
    int Channels { get; }

    /// <summary>True once the source has no more frames and never will.</summary>
    bool Ended { get; }

    /// <summary>Reads up to <c>interleaved.Length / Channels</c> frames; returns the frames read (fewer on an underflow).</summary>
    int Read(Span<float> interleaved);
}

/// <summary>
/// One mixer command (ARCHITECTURE 7.1): posted from any thread, applied by the audio thread before output frame
/// <see cref="Frame"/> is rendered (<see cref="Now"/> = before the next frame). See <c>tools/oracle/audio.py</c>
/// for the exact semantics the oracle renders.
/// </summary>
/// <param name="Op">The operation.</param>
/// <param name="Voice">The voice slot, 0 .. <see cref="AudioMixer.MaxVoices"/> - 1.</param>
/// <param name="Frame">The absolute output frame the command applies at, or <see cref="Now"/>.</param>
/// <param name="Clip">The clip (play).</param>
/// <param name="Source">The streaming source (play stream).</param>
/// <param name="Loop">Use the clip's loop (play).</param>
/// <param name="Repeats">Jumps from the loop end back to the loop start; -1 = forever (play).</param>
/// <param name="StartFrame">The first clip frame to play (play).</param>
/// <param name="Gain">Linear gain, not clipped (play, set gain).</param>
/// <param name="Pan">Pan -1 .. 1 (play, set pan).</param>
/// <param name="RampFrames">Ramp length in frames; negative = <see cref="AudioMixer.DefaultRampFrames"/>, 0 = at once.</param>
public readonly record struct MixerCommand(
    MixerOp Op, int Voice, long Frame = MixerCommand.Now, AudioClip? Clip = null, IMixerSource? Source = null, bool Loop = false,
    int Repeats = -1, int StartFrame = 0, float Gain = 1f, float Pan = 0f, int RampFrames = -1)
{
    /// <summary>Apply before the next rendered frame.</summary>
    public const long Now = -1;

    /// <summary>Plays <paramref name="clip"/> on <paramref name="voice"/>.</summary>
    public static MixerCommand Play(int voice, AudioClip clip, bool loop, float gain, float pan, int repeats = -1, int startFrame = 0, long frame = Now) =>
        new(MixerOp.Play, voice, frame, Clip: clip, Loop: loop, Repeats: repeats, StartFrame: startFrame, Gain: gain, Pan: pan);

    /// <summary>Plays a streaming source on <paramref name="voice"/>.</summary>
    public static MixerCommand PlayStream(int voice, IMixerSource source, float gain, float pan = 0f, long frame = Now) =>
        new(MixerOp.PlayStream, voice, frame, Source: source, Gain: gain, Pan: pan);

    /// <summary>Stops <paramref name="voice"/>.</summary>
    public static MixerCommand Stop(int voice, long frame = Now) => new(MixerOp.Stop, voice, frame);

    /// <summary>Stops every voice.</summary>
    public static MixerCommand StopAll(long frame = Now) => new(MixerOp.StopAll, 0, frame);

    /// <summary>Ramps the gain of <paramref name="voice"/>.</summary>
    public static MixerCommand SetGain(int voice, float gain, int rampFrames = -1, long frame = Now) =>
        new(MixerOp.SetGain, voice, frame, Gain: gain, RampFrames: rampFrames);

    /// <summary>Ramps the pan of <paramref name="voice"/>.</summary>
    public static MixerCommand SetPan(int voice, float pan, int rampFrames = -1, long frame = Now) =>
        new(MixerOp.SetPan, voice, frame, Pan: pan, RampFrames: rampFrames);
}
