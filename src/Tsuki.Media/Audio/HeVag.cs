// HE-VAG (High Efficiency VAG) ADPCM, the 19 vag.mrg system sounds: a C# port of tools/tsuki/audio_hevag.py
// (docs/formats/audio.md 6; ARCHITECTURE 7.6). FFmpeg's adpcm_psx decodes these files wrongly (audio.md 6.2).
using System.Buffers.Binary;

namespace Tsuki.Media.Audio;

/// <summary>The 48-byte big-endian "VAGp" header (audio.md 6.1).</summary>
/// <param name="Version">0x00020001 in every entry.</param>
/// <param name="DataSize">Bytes of ADPCM frames after the header (a multiple of 16).</param>
/// <param name="SampleRate">48,000.</param>
/// <param name="ChannelsByte">Byte 0x1E: 1, except 2 for entry 0 ("dummy").</param>
/// <param name="Name">The NUL-padded ASCII name at 0x20, e.g. <c>moon_se00001_L</c>.</param>
public readonly record struct VagHeader(uint Version, int DataSize, int SampleRate, int ChannelsByte, string Name)
{
    /// <summary>Header size.</summary>
    public const int Size = 0x30;

    /// <summary>Number of 16-byte frames.</summary>
    public int Frames => DataSize / HeVag.FrameBytes;
}

/// <summary>The HE-VAG decoder (audio.md 6.2): 16-byte frames of 28 mono samples, a 4-tap predictor from 128 rows.</summary>
public static class HeVag
{
    /// <summary>Bytes per frame.</summary>
    public const int FrameBytes = 16;

    /// <summary>Samples per frame.</summary>
    public const int FrameSamples = 28;

    // 128 x 4 predictor coefficients in units of 1/8192 (audio.md 6.2 table K; vgmstream hevag_coefs_f * 8192).
    private static readonly short[] K =
    [
        0, 0, 0, 0, 7680, 0, 0, 0, 14720, -6656, 0, 0, 12544, -7040, 0, 0,
        15616, -7680, 0, 0, 14731, -7059, 0, 0, 14507, -7366, 0, 0, 13920, -7522, 0, 0,
        13133, -7680, 0, 0, 12028, -7680, 0, 0, 10764, -7680, 0, 0, 9359, -7680, 0, 0,
        7832, -7680, 0, 0, 6201, -7680, 0, 0, 4488, -7680, 0, 0, 2717, -7680, 0, 0,
        910, -7680, 0, 0, -910, -7680, 0, 0, -2717, -7680, 0, 0, -4488, -7680, 0, 0,
        -6201, -7680, 0, 0, -7832, -7680, 0, 0, -9359, -7680, 0, 0, -10764, -7680, 0, 0,
        -12028, -7680, 0, 0, -13133, -7680, 0, 0, -13920, -7522, 0, 0, -14507, -7366, 0, 0,
        -14731, -7059, 0, 0, 5376, -9216, 3328, -3072, -6400, -7168, -3328, -2304, -10496, -7424, -3584, -1024,
        -167, -2722, -494, -541, -7430, -2221, -2298, 424, -8001, -3166, -2814, 289, 6018, -4750, 2649, -1298,
        3798, -6946, 3875, -1216, -8237, -2596, -2071, 227, 9199, 1982, -1382, -2316, 13021, -3044, -3792, 1267,
        13112, -4487, -2250, 1665, -1668, -3744, -6456, 840, 7819, -4328, 2111, -506, 9571, -1336, -757, 487,
        10032, -2562, 300, 199, -4745, -4122, -5486, -1493, -5896, 2378, -4787, -6947, -1193, -9117, -1237, -3114,
        2783, -7108, -1575, -1447, -7334, -2062, -2212, 446, 6127, -2577, -315, -18, 9457, -1858, 102, 258,
        7876, -4483, 2126, -538, -7172, -1795, -2069, 482, -7358, -2102, -2233, 440, -9170, -3509, -2674, -391,
        -2638, -2647, -1929, -1637, 1873, 9183, 1860, -5746, 9214, 1859, -1124, -2427, 13204, -3012, -4139, 1370,
        12437, -4792, -256, 622, -2653, -1144, -3182, -6878, 9331, -1048, -828, 507, 1642, -620, -946, -4229,
        4246, -7585, -533, -2259, -8988, -3891, -2807, 44, -2562, -2735, -1730, -1899, 3182, -483, -714, -1421,
        7937, -3844, 2821, -1019, 10069, -2609, 314, 195, 8400, -3297, 1551, -155, -8529, -2775, -2432, -336,
        9477, -1882, 108, 256, 75, -2241, -298, -6937, -9143, -4160, -2963, 5, -7270, -1958, -2156, 460,
        -2740, 3745, 5936, -1089, 8993, 1948, -683, -2704, 13101, -2835, -3854, 1055, 9543, -1961, 130, 250,
        5272, -4270, 3124, -3157, -7696, -3383, -2907, -456, 7309, 2523, 434, -2461, 10275, -2867, 391, 172,
        10940, -3721, 665, 97, 24, -310, -1262, 320, -8122, -2411, -2311, -271, -8511, -3067, -2337, 163,
        326, -3846, 419, -933, 8895, 2194, -541, -2880, 12073, -1876, -2017, -601, 8729, -3423, 1674, -169,
        12950, -3847, -3007, 1946, 10038, -2570, 302, 198, 9385, -2757, 1008, 41, -4720, -5006, -2852, -1161,
        7869, -4326, 2135, -501, 2450, -8597, 1299, -2780, 10192, -2763, 360, 181, 11313, -4213, 833, 53,
        10154, -2716, 345, 185, 9638, -1417, -737, 482, 3854, -4554, 2843, -3397, 6699, -5659, 2249, -1074,
        11082, -3908, 728, 80, -1026, -9810, -805, -3462, 10396, -3746, 1367, -96, 10287, 988, -1915, -1437,
        7953, 3878, -764, -3263, 12689, -3375, -3354, 2079, 6641, 3166, 231, -2089, -2348, -7354, -1944, -4122,
        9290, -4039, 1885, -246, 4633, -6403, 1748, -1619, 11247, -4125, 802, 61, 9807, -2284, 219, 222,
        9736, -1536, -706, 473, 8440, -3436, 1562, -176, 9307, -1021, -835, 509, 1698, -9025, 688, -3037,
        10214, -2791, 368, 179, 8390, 3248, -758, -2989, 7201, 3316, 46, -2614, -88, -7809, -538, -4571,
        6193, -5189, 2760, -1245, 12325, -1290, -3284, 253, 13064, -4075, -2824, 1877, 5333, 2999, 775, -1132,
    ];

    // C[i][j] = K[i][j] / 8192 as float32 (exact: |K| < 2^15). audio_hevag.py: HEVAG_COEFS_F32.
    private static readonly float[] C = BuildCoefficients();

    /// <summary>Number of predictor rows (128).</summary>
    public static int Rows => K.Length / 4;

    /// <summary>Coefficient <paramref name="tap"/> of row <paramref name="row"/> in units of 1/8192.</summary>
    public static int Coefficient8192(int row, int tap) => K[(row * 4) + tap];

    /// <summary>Parses the header; unless <paramref name="headerOnly"/> the frames must follow it in <paramref name="data"/>.</summary>
    /// <exception cref="InvalidDataException">Not a VAGp stream.</exception>
    public static VagHeader ParseHeader(ReadOnlySpan<byte> data, bool headerOnly = false)
    {
        if (data.Length < VagHeader.Size || !data[..4].SequenceEqual("VAGp"u8))
        {
            throw new InvalidDataException("not a VAGp stream");
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
        uint rate = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        if (size % FrameBytes != 0 || size > int.MaxValue)
        {
            throw new InvalidDataException($"VAG data size {size} is not a multiple of {FrameBytes}");
        }

        if (!headerOnly && VagHeader.Size + (long)size > data.Length)
        {
            throw new InvalidDataException($"VAG data size {size} runs past the buffer ({data.Length} bytes)");
        }

        ReadOnlySpan<byte> name = data.Slice(0x20, 16);
        int nul = name.IndexOf((byte)0);
        if (nul >= 0)
        {
            name = name[..nul];
        }

        return new VagHeader(version, (int)size, (int)rate, data[0x1E], System.Text.Encoding.ASCII.GetString(name));
    }

    /// <summary>Playable frames of a whole VAGp file: the frames before the first one whose flag is 7 or more (audio.md 6.2).</summary>
    public static int PlayableFrames(ReadOnlySpan<byte> vag)
    {
        VagHeader h = ParseHeader(vag);
        ReadOnlySpan<byte> body = vag.Slice(VagHeader.Size, h.DataSize);
        for (int f = 0; f < h.Frames; f++)
        {
            if ((body[(f * FrameBytes) + 1] & 0x0F) >= 7)
            {
                return f;
            }
        }

        return h.Frames;
    }

    /// <summary>
    /// The loop of a whole VAGp file from the PS-ADPCM flags (bit 2 = loop start frame, bits 0 + 1 = loop end), in
    /// samples with the end inclusive, or null (no vag.mrg entry has one; audio.py <c>_vag_loop</c>).
    /// </summary>
    public static (int Start, int End)? Loop(ReadOnlySpan<byte> vag)
    {
        VagHeader h = ParseHeader(vag);
        ReadOnlySpan<byte> body = vag.Slice(VagHeader.Size, h.DataSize);
        int start = -1, end = -1;
        for (int f = 0; f < h.Frames; f++)
        {
            int fl = body[(f * FrameBytes) + 1] & 0x0F;
            if (fl >= 7)
            {
                continue;
            }

            if (start < 0 && (fl & 4) != 0)
            {
                start = f;
            }

            if (end < 0 && (fl & 3) == 3)
            {
                end = f;
            }
        }

        return start < 0 || end < 0 || end < start ? null : (start * FrameSamples, ((end + 1) * FrameSamples) - 1);
    }

    /// <summary>
    /// Decodes <paramref name="frames"/> frames of <paramref name="body"/> (mono) to int16 PCM in <paramref name="output"/>
    /// (28 per frame). <paramref name="history"/> (h1..h4, newest first) carries the predictor state across calls.
    /// </summary>
    public static void DecodeFrames(ReadOnlySpan<byte> body, int frames, Span<short> output, Span<int> history)
    {
        if ((long)frames * FrameBytes > body.Length || (long)frames * FrameSamples > output.Length || history.Length < 4)
        {
            throw new ArgumentException("frame count exceeds the data, the output or the history");
        }

        int h1 = history[0], h2 = history[1], h3 = history[2], h4 = history[3];
        float[] c = C;
        int o = 0;
        for (int f = 0; f < frames; f++)
        {
            int p = f * FrameBytes;
            int b0 = body[p];
            int b1 = body[p + 1];
            int idx = (b1 & 0xF0) | (b0 >> 4);
            if (idx > 127)
            {
                idx = 0;
            }

            int shift = b0 & 0x0F;
            int flag = b1 & 0x0F;
            float c0 = c[idx * 4], c1 = c[(idx * 4) + 1], c2 = c[(idx * 4) + 2], c3 = c[(idx * 4) + 3];
            for (int i = 0; i < FrameSamples; i++)
            {
                int s;
                if (flag < 7)
                {
                    int b = body[p + 2 + (i >> 1)];
                    int nib = (i & 1) != 0 ? b >> 4 : b & 0x0F;
                    if (nib >= 8)
                    {
                        nib -= 16;
                    }

                    int code = (nib << 12) >> shift;

                    // float32, left to right, each product and sum rounded (audio.md 6.2)
                    float acc = (h1 * c0) + (h2 * c1);
                    acc += h3 * c2;
                    acc += h4 * c3;
                    s = code + (int)acc; // truncation toward zero, as a C cast
                }
                else
                {
                    s = 0;
                }

                output[o++] = (short)Math.Clamp(s, short.MinValue, short.MaxValue);
                h4 = h3;
                h3 = h2;
                h2 = h1;
                h1 = s; // the history keeps the unclamped value
            }
        }

        history[0] = h1;
        history[1] = h2;
        history[2] = h3;
        history[3] = h4;
    }

    /// <summary>Decodes a whole mono VAGp file to int16 PCM up to its end marker (audio_hevag.decode_vag, trim_end).</summary>
    public static short[] Decode(ReadOnlySpan<byte> vag)
    {
        VagHeader h = ParseHeader(vag);
        int n = PlayableFrames(vag);
        short[] pcm = new short[n * FrameSamples];
        Span<int> hist = stackalloc int[4];
        hist.Clear();
        DecodeFrames(vag.Slice(VagHeader.Size, h.DataSize), n, pcm, hist);
        return pcm;
    }

    /// <summary>The float PCM the mixer plays: <see cref="Decode"/> / 32768 (audio.py <c>AudioBank.decode(float_output=True)</c>).</summary>
    public static float[] DecodeFloat(ReadOnlySpan<byte> vag)
    {
        short[] pcm = Decode(vag);
        float[] f = new float[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            f[i] = pcm[i] / 32768f;
        }

        return f;
    }

    private static float[] BuildCoefficients()
    {
        float[] c = new float[K.Length];
        for (int i = 0; i < K.Length; i++)
        {
            c[i] = (float)(K[i] / 8192.0);
        }

        return c;
    }
}
