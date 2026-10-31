using System.Buffers.Binary;

namespace S1.AudioLoop;

// RIFF/WAVE ATRAC9 header of one bgm/vse/voice stream (docs/formats/audio.md 2.1-2.3).
// Same fields and checks as tools/tsuki/audio.py parse_wave (strict mode).
internal sealed class RiffAtrac9
{
    // {47E142D2-36BA-4D8D-88FC-61654F8C836C}, as stored (audio.md 2.1, offset 0x2C)
    private static readonly byte[] Atrac9Guid =
        [0xD2, 0x42, 0xE1, 0x47, 0xBA, 0x36, 0x8D, 0x4D, 0x88, 0xFC, 0x61, 0x65, 0x4F, 0x8C, 0x83, 0x6C];

    private static readonly int[] SampleRates =
        [11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 44100, 48000, 64000, 88200, 96000, 128000, 176400, 192000];

    private static readonly int[] FrameLog2 = [6, 6, 7, 7, 7, 8, 8, 8, 6, 6, 7, 7, 7, 8, 8, 8];
    private static readonly int[] ConfigChannels = [1, 2, 2, 6, 8, 4, 0, 0];

    public required int RiffSize { get; init; }
    public required int Channels { get; init; }
    public required int SampleRate { get; init; }
    public required int BlockAlign { get; init; }
    public required int SamplesPerBlock { get; init; }

    // The 12 bytes after the 22-byte WAVEFORMATEXTENSIBLE extension (fmt + 0x28: dwVersionInfo,
    // config word, reserved). FFmpeg's wav demuxer hands exactly these to the atrac9 decoder as
    // extradata (riffdec: cbSize 0x22 - 22 = 12), and atrac9dec requires extradata_size == 12.
    public required byte[] Extradata { get; init; }

    public required uint ConfigWord { get; init; }
    public required int[] Fact { get; init; }
    public required (int Start, int End)[] SmplLoops { get; init; }
    public required int DataOffset { get; init; }
    public required int DataSize { get; init; }

    public int SamplesPerFrame => 1 << FrameLog2[(int)(ConfigWord >> 20) & 0xF];
    public int FramesPerSuperframe => 1 << (int)((ConfigWord >> 3) & 3);
    public int FrameBytes => (int)((ConfigWord >> 5) & 0x7FF) + 1;
    public int PlayableFrames => Fact[0];
    public int Delay => Fact.Length >= 3 ? Fact[2] : 0;
    public int Superframes => DataSize / BlockAlign;
    public int DecodedFrames => Superframes * SamplesPerBlock;

    // Loop in playable coordinates, end inclusive: [smpl.start - fact[2], smpl.end - fact[2]]
    // (audio.md 2.3, engine VA 0x11324F / 0x11325D). No loop when start == end (VA 0x112530).
    public (int Start, int End)? PlayableLoop
    {
        get
        {
            if (SmplLoops.Length == 0 || SmplLoops[0].Start == SmplLoops[0].End)
            {
                return null;
            }

            return (SmplLoops[0].Start - Delay, SmplLoops[0].End - Delay);
        }
    }

    public static RiffAtrac9 Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12 || !b[..4].SequenceEqual("RIFF"u8) || !b.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a RIFF/WAVE stream");
        }

        int riffSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) + 8;
        int o = 12;
        int channels = 0, rate = 0, align = 0, spb = 0;
        uint config = 0;
        byte[]? extradata = null;
        int[] fact = [];
        var loops = new List<(int, int)>();
        int dataOff = -1, dataSize = -1;
        while (o + 8 <= b.Length)
        {
            ReadOnlySpan<byte> id = b.Slice(o, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 4)..]);
            ReadOnlySpan<byte> body = b.Slice(o + 8, Math.Min(size, b.Length - o - 8));
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
                fact = new int[size / 4];
                for (int i = 0; i < fact.Length; i++)
                {
                    fact[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[(4 * i)..]);
                }
            }
            else if (id.SequenceEqual("smpl"u8))
            {
                int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[28..]);
                for (int k = 0; k < n && 36 + 24 * (k + 1) <= body.Length; k++)
                {
                    ReadOnlySpan<byte> r = body[(36 + 24 * k)..];
                    loops.Add(((int)BinaryPrimitives.ReadUInt32LittleEndian(r[8..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(r[12..])));
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataOff = o + 8;
                dataSize = size;
                break;
            }

            o += 8 + size + (size & 1);
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

        if (ConfigChannels[(config >> 17) & 7] != channels)
        {
            problems.Add("config channel count != fmt channels");
        }

        if (SampleRates[(config >> 20) & 0xF] != rate)
        {
            problems.Add("config rate != fmt rate");
        }

        if (h.FrameBytes * h.FramesPerSuperframe != align)
        {
            problems.Add("superframe bytes != block align");
        }

        if (h.SamplesPerFrame * h.FramesPerSuperframe != spb)
        {
            problems.Add("superframe samples != wSamplesPerBlock");
        }

        if (dataSize % align != 0 || dataOff + dataSize > b.Length)
        {
            problems.Add("data size is not whole superframes inside the stream");
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException(string.Join("; ", problems));
        }

        return h;
    }
}
