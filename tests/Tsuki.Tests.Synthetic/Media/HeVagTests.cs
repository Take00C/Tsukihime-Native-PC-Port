using System.Buffers.Binary;
using Tsuki.Engine.Contracts;
using Tsuki.Media.Audio;

namespace Tsuki.Tests.Synthetic.Media;

/// <summary>The HE-VAG port on hand-built frames (docs/formats/audio.md 6.2); the real-data check is in Tests.Data.</summary>
public sealed class HeVagTests
{
    // frame bytes: b0 = shift | index low nibble << 4, b1 = flag | index high nibble << 4, then 28 nibbles low first
    private static byte[] Frame(int index, int shift, int flag, params int[] nibbles)
    {
        byte[] f = new byte[16];
        f[0] = (byte)((shift & 0x0F) | ((index & 0x0F) << 4));
        f[1] = (byte)((flag & 0x0F) | (index & 0xF0));
        for (int i = 0; i < 28; i++)
        {
            int n = (i < nibbles.Length ? nibbles[i] : 0) & 0x0F;
            f[2 + (i >> 1)] |= (byte)((i & 1) != 0 ? n << 4 : n);
        }

        return f;
    }

    private static byte[] Vag(string name, params byte[][] frames)
    {
        byte[] v = new byte[0x30 + (16 * frames.Length)];
        "VAGp"u8.CopyTo(v);
        BinaryPrimitives.WriteUInt32BigEndian(v.AsSpan(4), 0x00020001);
        BinaryPrimitives.WriteUInt32BigEndian(v.AsSpan(12), (uint)(16 * frames.Length));
        BinaryPrimitives.WriteUInt32BigEndian(v.AsSpan(16), 48_000);
        v[0x1E] = 1;
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(v, 0x20);
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i].CopyTo(v, 0x30 + (16 * i));
        }

        return v;
    }

    [Fact]
    public void TheCoefficientTableIsTheDocumentedOne()
    {
        Assert.Equal(128, HeVag.Rows);
        Assert.Equal([14731, -7059, 0, 0], Enumerable.Range(0, 4).Select(t => HeVag.Coefficient8192(5, t)));
        Assert.Equal([5333, 2999, 775, -1132], Enumerable.Range(0, 4).Select(t => HeVag.Coefficient8192(127, t)));
        Assert.Equal([5376, -9216, 3328, -3072], Enumerable.Range(0, 4).Select(t => HeVag.Coefficient8192(29, t)));
    }

    [Fact]
    public void ThePlainCodesAreShiftedNibblesAndTheEndMarkerTrims()
    {
        byte[] vag = Vag("t", Frame(0, 0, 0), Frame(0, 12, 0, 1, 2, 3, 7, -8, -1), Frame(0, 0, 1, 5), Frame(0, 0, 7, 7), Frame(0, 0, 0, 7));
        VagHeader h = HeVag.ParseHeader(vag);
        Assert.Equal(("t", 48_000, 5), (h.Name, h.SampleRate, h.Frames));
        Assert.Equal(3, HeVag.PlayableFrames(vag));
        short[] pcm = HeVag.Decode(vag);
        Assert.Equal(3 * 28, pcm.Length);
        Assert.All(pcm[..28], s => Assert.Equal(0, s));
        Assert.Equal(new short[] { 1, 2, 3, 7, -8, -1, 0 }, pcm[28..35]);
        Assert.Equal(5 << 12, pcm[56]); // predictor 0: no taps
        float[] f = HeVag.DecodeFloat(vag);
        Assert.Equal((5 << 12) / 32768f, f[56]);
        Assert.Null(HeVag.Loop(vag));
    }

    [Fact]
    public void ThePredictorTruncatesTowardZeroAndTheHistoryIsNotClamped()
    {
        // row 1 = 7680 / 8192 = 0.9375 on h1; shift 0, nibble 7 -> code 28672 each sample
        byte[] vag = Vag("p", Frame(1, 0, 0, 7, 7, 7, -1), Frame(0, 0, 7));
        short[] pcm = HeVag.Decode(vag);
        long h1 = 0;
        var expected = new List<short>();
        int[] nib = [7, 7, 7, -1];
        for (int i = 0; i < 28; i++)
        {
            int code = (i < 4 ? nib[i] : 0) << 12;
            long s = code + (long)(int)(h1 * 0.9375f);
            expected.Add((short)Math.Clamp(s, short.MinValue, short.MaxValue));
            h1 = s;
        }

        Assert.Equal(expected, pcm);
        Assert.Equal(short.MaxValue, pcm[1]); // 28672 + 26880 clamps ...
        Assert.Equal((short)Math.Clamp(28672 + (int)((28672 + 26880) * 0.9375f), -32768, 32767), pcm[2]); // ... but the history keeps 55552
    }

    [Fact]
    public void AFlagSevenFrameDecodesToSilenceAndIndexAbove127IsRowZero()
    {
        var hist = new int[4];
        short[] o = new short[56];
        byte[] body = [.. Frame(0xFF, 0, 0, 3), .. Frame(0, 0, 7, 7, 7)];
        HeVag.DecodeFrames(body, 2, o, hist);
        Assert.Equal(3 << 12, o[0]); // index 255 > 127 -> row 0: no prediction
        Assert.Equal(0, o[1]);
        Assert.All(o[28..], s => Assert.Equal(0, s));
        Assert.Equal([0, 0, 0, 0], hist);
    }

    [Fact]
    public void TheMediaDecoderReturnsAMonoClip()
    {
        byte[] vag = Vag("m", Frame(0, 0, 0), Frame(0, 4, 0, 1), Frame(0, 0, 7));
        AudioClip clip = new MediaAudioDecoder().Decode(AudioBank.Vag, 3, vag);
        Assert.Equal((AudioBank.Vag, 3, 1, 48_000, 56), (clip.Bank, clip.Index, clip.Channels, clip.SampleRate, clip.FrameCount));
        Assert.Equal((1 << 12 >> 4) / 32768f, clip.Samples.Span[28]);
    }

    [Fact]
    public void ABadHeaderIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => HeVag.ParseHeader(new byte[0x30]));
        byte[] vag = Vag("x", Frame(0, 0, 0));
        BinaryPrimitives.WriteUInt32BigEndian(vag.AsSpan(12), 17);
        Assert.Throws<InvalidDataException>(() => HeVag.ParseHeader(vag));
        BinaryPrimitives.WriteUInt32BigEndian(vag.AsSpan(12), 32);
        Assert.Throws<InvalidDataException>(() => HeVag.ParseHeader(vag));
    }
}
