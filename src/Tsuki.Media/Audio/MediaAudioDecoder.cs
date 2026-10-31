// The IAudioDecoder of Tsuki.Media (ARCHITECTURE 3.2, 7.1, 7.2, 7.6): FFmpeg "atrac9" for the RIFF/WAVE banks
// and the C# HE-VAG port for vag. Tsuki.Data's GameFolderSource gets it injected, because Tsuki.Data makes no
// P/Invoke (ARCHITECTURE 3.1). Software decode only.
using Tsuki.Engine.Contracts;

namespace Tsuki.Media.Audio;

/// <summary>Decodes bank entries to the engine's <see cref="AudioClip"/> (thread-safe: one decoder context per call).</summary>
public sealed class MediaAudioDecoder : IAudioDecoder
{
    /// <inheritdoc />
    /// <remarks>
    /// ATRAC9: the playable range <c>decoded[fact[2] : fact[2] + fact[0]]</c> and the loop
    /// <c>[smpl.start - fact[2], smpl.end - fact[2]]</c>, null when start == end (audio.md 2.3). HE-VAG: the
    /// samples before the end marker / 32768, mono, with the PS-ADPCM flag loop (none in the retail data).
    /// </remarks>
    public AudioClip Decode(AudioBank bank, int index, ReadOnlyMemory<byte> payload)
    {
        if (bank == AudioBank.Vag)
        {
            ReadOnlySpan<byte> vag = payload.Span;
            VagHeader h = HeVag.ParseHeader(vag);
            AudioLoop? vloop = HeVag.Loop(vag) is { } l ? new AudioLoop(l.Start, l.End) : null;
            return new AudioClip(bank, index, 1, h.SampleRate, HeVag.DecodeFloat(vag), vloop);
        }

        RiffAtrac9 riff = RiffAtrac9.Parse(payload.Span);
        float[] decoded = Atrac9Decoder.DecodeAll(riff, payload.Span);
        return ToClip(bank, index, riff, decoded);
    }

    /// <summary>The playable clip of a full ATRAC9 decode (<see cref="Atrac9Decoder.DecodeAll"/>), without a copy.</summary>
    public static AudioClip ToClip(AudioBank bank, int index, RiffAtrac9 riff, float[] decoded)
    {
        ArgumentNullException.ThrowIfNull(riff);
        ArgumentNullException.ThrowIfNull(decoded);
        var playable = new ReadOnlyMemory<float>(decoded, riff.Delay * riff.Channels, riff.PlayableFrames * riff.Channels);
        AudioLoop? loop = riff.PlayableLoop is { } l ? new AudioLoop(l.Start, l.End) : null;
        return new AudioClip(bank, index, riff.Channels, riff.SampleRate, playable, loop);
    }
}
