using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Tsuki.Media.Audio;
using Tsuki.Media.Movie;

namespace Tsuki.Tests.Synthetic.Media;

public sealed class MediaInputSecurityTests
{
    [Theory]
    [InlineData(0x7FFF_FFFCu)]
    [InlineData(0xFFFF_FFFCu)]
    [InlineData(1024u)]
    public void RiffRejectsTruncatedFactBeforeAllocating(uint size)
    {
        byte[] riff = Riff("fact"u8, []);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(16), size);
        Assert.Throws<InvalidDataException>(() => RiffAtrac9.Parse(riff));
    }

    [Fact]
    public void RiffRejectsCountsAndSizesOutsideTheChunk()
    {
        byte[] smpl = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(28), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => RiffAtrac9.Parse(Riff("smpl"u8, smpl)));
        Assert.Throws<InvalidDataException>(() => RiffAtrac9.Parse(Riff("smpl"u8, new byte[20])));
        Assert.Throws<InvalidDataException>(() => RiffAtrac9.Parse(Riff("fact"u8, new byte[3])));
        byte[] riff = Riff("fact"u8, new byte[12]);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => RiffAtrac9.Parse(riff));
    }

    [Theory]
    [InlineData(1, 0xFE700BF0u, 384)]
    [InlineData(2, 0xFE740FF0u, 512)]
    public void ValidAtrac9HeadersStillParse(int channels, uint config, int align)
    {
        RiffAtrac9 h = RiffAtrac9.Parse(Wave(channels, config, align));
        Assert.Equal(channels, h.Channels);
        Assert.Equal(48_000, h.SampleRate);
        Assert.Equal(2, h.Superframes);
        Assert.Equal(2048, h.DecodedFrames);
        Assert.Equal(1792, h.PlayableFrames);
    }

    [Fact]
    public void ZeroBlockAlignmentFailsAsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => RiffAtrac9.Parse(Wave(2, 0xFE740FF0, 0)));
    }

    [Theory]
    [InlineData(65_536, 65_536)]
    [InlineData(65_536, 65_538)]
    [InlineData(2_147_483_646, 2_147_483_646)]
    public void OverflowingVideoDimensionsAreRejectedBeforeAllocation(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoFrame(width, height));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeVideoPlanesCopyWithPositiveAndNegativeStrides(bool inverted)
    {
        using var native = new NativeFrame(inverted);
        var frame = new VideoFrame(4, 4);
        native.CopyTo(frame);
        byte[] expected = inverted ? [12, 13, 14, 15, 8, 9, 10, 11, 4, 5, 6, 7, 0, 1, 2, 3] : [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
        Assert.Equal(expected, frame.Y);
        Assert.Equal(inverted ? [18, 19, 16, 17] : new byte[] { 16, 17, 18, 19 }, frame.U);
        Assert.Equal(inverted ? [22, 23, 20, 21] : new byte[] { 20, 21, 22, 23 }, frame.V);
    }

    [Fact]
    public void NativeVideoPlanesRejectShortStridesAndMissingPointers()
    {
        using var native = new NativeFrame(inverted: false);
        var frame = new VideoFrame(4, 4);
        native.SetStride(3);
        Assert.IsType<InvalidDataException>(Assert.Throws<TargetInvocationException>(() => native.CopyTo(frame)).InnerException);
        native.SetStride(4);
        native.ClearLumaPointer();
        Assert.IsType<InvalidDataException>(Assert.Throws<TargetInvocationException>(() => native.CopyTo(frame)).InnerException);
    }

    private sealed class NativeFrame : IDisposable
    {
        private static readonly MethodInfo CopyFrom = typeof(VideoFrame).GetMethod("CopyFrom", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly nint _frame = Marshal.AllocHGlobal(Marshal.SizeOf<AVFrame>());
        private readonly nint _planes = Marshal.AllocHGlobal(24);

        public NativeFrame(bool inverted)
        {
            Marshal.Copy(new byte[Marshal.SizeOf<AVFrame>()], 0, _frame, Marshal.SizeOf<AVFrame>());
            Marshal.Copy([.. Enumerable.Range(0, 24).Select(i => (byte)i)], 0, _planes, 24);
            int data = Offset("data");
            int strides = Offset("linesize");
            Marshal.WriteInt32(_frame, Offset("format"), (int)AVPixelFormat.AV_PIX_FMT_YUV420P);
            Marshal.WriteInt32(_frame, Offset("width"), 4);
            Marshal.WriteInt32(_frame, Offset("height"), 4);
            Marshal.WriteIntPtr(_frame, data, _planes + (inverted ? 12 : 0));
            Marshal.WriteIntPtr(_frame, data + nint.Size, _planes + (inverted ? 18 : 16));
            Marshal.WriteIntPtr(_frame, data + 2 * nint.Size, _planes + (inverted ? 22 : 20));
            Marshal.WriteInt32(_frame, strides, inverted ? -4 : 4);
            Marshal.WriteInt32(_frame, strides + 4, inverted ? -2 : 2);
            Marshal.WriteInt32(_frame, strides + 8, inverted ? -2 : 2);
        }

        public void SetStride(int stride) => Marshal.WriteInt32(_frame, Offset("linesize"), stride);

        public void ClearLumaPointer() => Marshal.WriteIntPtr(_frame, Offset("data"), 0);

        public void CopyTo(VideoFrame frame) => CopyFrom.Invoke(frame, [_frame]);

        private static int Offset(string field) => (int)Marshal.OffsetOf<AVFrame>(field);

        public void Dispose()
        {
            Marshal.FreeHGlobal(_planes);
            Marshal.FreeHGlobal(_frame);
        }
    }

    private static byte[] Riff(ReadOnlySpan<byte> id, byte[] body)
    {
        byte[] riff = new byte[20 + body.Length + (body.Length & 1)];
        "RIFF"u8.CopyTo(riff);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)riff.Length - 8);
        "WAVE"u8.CopyTo(riff.AsSpan(8));
        id.CopyTo(riff.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(16), (uint)body.Length);
        body.CopyTo(riff.AsSpan(20));
        return riff;
    }

    private static byte[] Wave(int channels, uint config, int align)
    {
        byte[] fmt = new byte[52];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 48_000);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)align);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(16), 34);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(18), 1024);
        Convert.FromHexString("D242E147BA368D4D88FC61654F8C836C").CopyTo(fmt.AsSpan(24));
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32BigEndian(fmt.AsSpan(44), config);
        byte[] fact = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(fact, 1792);
        BinaryPrimitives.WriteUInt32LittleEndian(fact.AsSpan(4), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(fact.AsSpan(8), 256);
        byte[] result = [.. Riff("fmt "u8, fmt), .. Riff("fact"u8, fact).AsSpan(12), .. Riff("data"u8, new byte[2 * align]).AsSpan(12)];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length - 8);
        return result;
    }
}
