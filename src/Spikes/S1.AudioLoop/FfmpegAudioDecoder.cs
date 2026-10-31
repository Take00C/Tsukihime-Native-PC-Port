namespace S1.AudioLoop;

// Inside the namespace, so that the type Tsuki.Engine.Contracts.AudioLoop wins over the namespace S1.AudioLoop.
using Tsuki.Engine.Contracts;

// Prototype of the Tsuki.Media IAudioDecoder for ATRAC9 (ARCHITECTURE 3.2, 7.2): decode every
// superframe, then expose the playable range decoded[fact[2] : fact[2] + fact[0]] and the loop
// [smpl.start - fact[2], smpl.end - fact[2]] (end inclusive) as an AudioClip.
internal sealed class FfmpegAudioDecoder : IAudioDecoder
{
    public AudioClip Decode(AudioBank bank, int index, ReadOnlyMemory<byte> payload)
    {
        var h = RiffAtrac9.Parse(payload.Span);
        float[] decoded = DecodeAll(h, payload.Span);
        return ToClip(bank, index, h, decoded);
    }

    public static float[] DecodeAll(RiffAtrac9 h, ReadOnlySpan<byte> stream)
    {
        float[] decoded = new float[(long)h.DecodedFrames * h.Channels];
        using var dec = new FfmpegAtrac9(h);
        int n = dec.Decode(stream, 0, h.Superframes, decoded);
        n += dec.Finish(decoded.AsSpan(n * h.Channels));
        if (n != h.DecodedFrames)
        {
            throw new InvalidDataException($"atrac9 produced {n} frames, expected superframes * {h.SamplesPerBlock} = {h.DecodedFrames}");
        }

        return decoded;
    }

    public static AudioClip ToClip(AudioBank bank, int index, RiffAtrac9 h, float[] decoded)
    {
        var playable = new ReadOnlyMemory<float>(decoded, h.Delay * h.Channels, h.PlayableFrames * h.Channels);
        AudioLoop? loop = h.PlayableLoop is { } l ? new AudioLoop(l.Start, l.End) : null;
        return new AudioClip(bank, index, h.Channels, h.SampleRate, playable, loop);
    }
}
