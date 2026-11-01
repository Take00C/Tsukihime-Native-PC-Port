// RIFF/WAVE ATRAC9 header of one bgm / vse / voice stream (docs/formats/audio.md 2.1-2.3). Same fields
// and strict checks as tools/tsuki/audio.py parse_wave. Lifted from spike S1 RiffAtrac9.
using System.Buffers.Binary;

namespace Tsuki.Media.Audio;

/// <summary>The parsed header of an ATRAC9 RIFF/WAVE stream (audio.md 2.1-2.3).</summary>
public sealed class RiffAtrac9
{
    // {47E142D2-36BA-4D8D-88FC-61654F8C836C}, as stored (audio.md 2.1, fmt + 0x18)
    private static readonly byte[] Atrac9Guid =
        [0xD2, 0x42, 0xE1, 0x47, 0xBA, 0x36, 0x8D, 0x4D, 0x88, 0xFC, 0x61, 0x65, 0x4F, 0x8C, 0x83, 0x6C];

    private static readonly int[] SampleRates =
        [11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 44100, 48000, 64000, 88200, 96000, 128000, 176400, 192000];

    private static readonly int[] FrameLog2 = [6, 6, 7, 7, 7, 8, 8, 8, 6, 6, 7, 7, 7, 8, 8, 8];
    private static readonly int[] ConfigChannels = [1, 2, 2, 6, 8, 4, 0, 0];

    private RiffAtrac9()
    {
    }

    /// <summary>RIFF size field + 8: the whole stream in bytes.</summary>
    public int RiffSize { get; private init; }

    /// <summary>Channel count (fmt).</summary>
    public int Channels { get; private init; }

    /// <summary>Sample rate (fmt).</summary>
    public int SampleRate { get; private init; }

    /// <summary>Bytes per superframe (fmt block align).</summary>
    public int BlockAlign { get; private init; }

    /// <summary>Samples per superframe (fmt wSamplesPerBlock, 1024).</summary>
    public int SamplesPerBlock { get; private init; }

    /// <summary>
    /// The 12 bytes at fmt + 0x28 (dwVersionInfo, config word, reserved): the extradata FFmpeg's wav demuxer
    /// hands to the <c>atrac9</c> decoder, which requires exactly 12 (S1.md 1).
    /// </summary>
    public ReadOnlyMemory<byte> Extradata { get; private init; }

    /// <summary>The ATRAC9 config word, big-endian at fmt + 0x2C (audio.md 2.2).</summary>
    public uint ConfigWord { get; private init; }

    /// <summary>The fact chunk: (samples, 256, encoder delay 256).</summary>
    public IReadOnlyList<int> Fact { get; private init; } = [];

    /// <summary>The smpl loop records as stored (encoder delay included, end inclusive).</summary>
    public IReadOnlyList<(int Start, int End)> SmplLoops { get; private init; } = [];

    /// <summary>Offset of the data chunk's bytes in the stream.</summary>
    public int DataOffset { get; private init; }

    /// <summary>Size of the data chunk.</summary>
    public int DataSize { get; private init; }

    /// <summary>Samples per ATRAC9 frame (config word).</summary>
    public int SamplesPerFrame => 1 << FrameLog2[(int)(ConfigWord >> 20) & 0xF];

    /// <summary>Frames per superframe (config word).</summary>
    public int FramesPerSuperframe => 1 << (int)((ConfigWord >> 3) & 3);

    /// <summary>Bytes per ATRAC9 frame (config word, stored minus 1).</summary>
    public int FrameBytes => (int)((ConfigWord >> 5) & 0x7FF) + 1;

    /// <summary>Playable sample frames: fact[0].</summary>
    public int PlayableFrames => Fact[0];

    /// <summary>The encoder delay the engine subtracts: fact[2] (VA 0x113256).</summary>
    public int Delay => Fact.Count >= 3 ? Fact[2] : 0;

    /// <summary>Superframes in the data chunk.</summary>
    public int Superframes => DataSize / BlockAlign;

    /// <summary>Frames a decoder outputs from every superframe, delay included.</summary>
    public int DecodedFrames => Superframes * SamplesPerBlock;

    /// <summary>
    /// The loop in playable frames, end inclusive: <c>[smpl.start - fact[2], smpl.end - fact[2]]</c> (audio.md 2.3,
    /// VA 0x11324F / 0x11325D); null without a smpl loop or when start == end (VA 0x112530).
    /// </summary>
    public (int Start, int End)? PlayableLoop =>
        SmplLoops.Count == 0 || SmplLoops[0].Start == SmplLoops[0].End ? null : (SmplLoops[0].Start - Delay, SmplLoops[0].End - Delay);

    /// <summary>Parses the header (the bytes up to the data chunk header must be present, and the data chunk itself).</summary>
    /// <exception cref="InvalidDataException">Not a valid ATRAC9 RIFF stream.</exception>
    public static RiffAtrac9 Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12 || !b[..4].SequenceEqual("RIFF"u8) || !b.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a RIFF/WAVE stream");
        }

        long declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) + 8L;
        if (declaredSize < 12 || declaredSize > b.Length)
        {
            throw new InvalidDataException("RIFF size is outside the stream");
        }

        int riffSize = (int)declaredSize;
        int o = 12;
        int channels = 0, rate = 0, align = 0, spb = 0;
        uint config = 0;
        byte[]? extradata = null;
        int[] fact = [];
        var loops = new List<(int, int)>();
        int dataOff = -1, dataSize = -1;
        while (o <= riffSize - 8)
        {
            ReadOnlySpan<byte> id = b.Slice(o, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 4)..]);
            if (chunkSize > riffSize - o - 8)
            {
                throw new InvalidDataException("RIFF chunk size is outside the stream");
            }

            int size = (int)chunkSize;
            ReadOnlySpan<byte> body = b.Slice(o + 8, size);
            if (id.SequenceEqual("fmt "u8))
            {
                if (body.Length < 0x34)
                {
                    throw new InvalidDataException($"fmt chunk of {size} bytes is too short for ATRAC9");
                }

                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                if (tag != 0xFFFE || !body.Slice(24, 16).SequenceEqual(Atrac9Guid))
                {
                    throw new InvalidDataException($"not ATRAC9 (format tag 0x{tag:X4})");
                }

                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                align = BinaryPrimitives.ReadUInt16LittleEndian(body[12..]);
                spb = BinaryPrimitives.ReadUInt16LittleEndian(body[18..]);
                extradata = body.Slice(40, 12).ToArray();
                config = BinaryPrimitives.ReadUInt32BigEndian(body[44..]);
            }
            else if (id.SequenceEqual("fact"u8))
            {
                if (size % 4 != 0)
                {
                    throw new InvalidDataException("fact chunk is not whole sample counts");
                }

                fact = new int[size / 4];
                for (int i = 0; i < fact.Length; i++)
                {
                    fact[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[(4 * i)..]);
                }
            }
            else if (id.SequenceEqual("smpl"u8))
            {
                if (size < 36)
                {
                    throw new InvalidDataException("smpl chunk is shorter than its header");
                }

                uint n = BinaryPrimitives.ReadUInt32LittleEndian(body[28..]);
                if (n > (size - 36) / 24)
                {
                    throw new InvalidDataException("smpl loop count exceeds the chunk");
                }

                for (int k = 0; k < n; k++)
                {
                    ReadOnlySpan<byte> r = body[(36 + (24 * k))..];
                    loops.Add(((int)BinaryPrimitives.ReadUInt32LittleEndian(r[8..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(r[12..])));
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataOff = o + 8;
                dataSize = size;
                break;
            }

            long next = o + 8L + size + (size & 1);
            if (next > riffSize)
            {
                throw new InvalidDataException("RIFF chunk padding is outside the stream");
            }

            o = (int)next;
        }

        if (extradata is null || dataOff < 0 || fact.Length == 0)
        {
            throw new InvalidDataException("fmt, fact or data chunk missing");
        }

        var h = new RiffAtrac9
        {
            RiffSize = riffSize, Channels = channels, SampleRate = rate, BlockAlign = align, SamplesPerBlock = spb,
            Extradata = extradata, ConfigWord = config, Fact = fact, SmplLoops = [.. loops], DataOffset = dataOff, DataSize = dataSize,
        };

        // parse_wave strict checks (audio.md 2.2)
        var problems = new List<string>();
        if (config >> 24 != 0xFE)
        {
            problems.Add("config sync byte != 0xFE");
        }

        if (((config >> 16) & 1) != 0)
        {
            problems.Add("config validation bit set");
        }

        if (ConfigChannels[(config >> 17) & 7] != channels || channels is not (1 or 2))
        {
            problems.Add($"config channel count != fmt channels ({channels})");
        }

        if (SampleRates[(config >> 20) & 0xF] != rate)
        {
            problems.Add("config rate != fmt rate");
        }

        if (align <= 0 || h.FrameBytes * h.FramesPerSuperframe != align)
        {
            problems.Add("superframe bytes != block align");
        }

        if (h.SamplesPerFrame * h.FramesPerSuperframe != spb)
        {
            problems.Add("superframe samples != wSamplesPerBlock");
        }

        if (align > 0 && (dataSize % align != 0 || (long)dataOff + dataSize > b.Length))
        {
            problems.Add("data size is not whole superframes inside the stream");
        }

        long decoded = align > 0 ? (long)(dataSize / align) * spb : 0;
        if (decoded > int.MaxValue || fact[0] < 0 || fact[0] > decoded
            || (fact.Length >= 3 && (fact[2] < 0 || (long)fact[2] + fact[0] > decoded)))
        {
            problems.Add("fact range runs past the decoded samples");
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException(string.Join("; ", problems));
        }

        return h;
    }
}
