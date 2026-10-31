using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tsuki.Data.Containers;

namespace Tsuki.Data.Compression;

/// <summary>What one MZX0 decode did (docs/formats/containers.md 5.2, 5.5).</summary>
/// <param name="Consumed">Bytes of the stream used, header included. On retail data this is the whole section.</param>
/// <param name="Commands">Number of commands executed.</param>
/// <param name="Overshoot">Bytes the last command wrote past the declared size (0..127).</param>
public readonly record struct MzxStats(int Consumed, int Commands, int Overshoot);

/// <summary>An MZX0 result: a buffer with the decoded bytes at the front and the decoder's slack behind them.</summary>
public readonly struct MzxDecoded
{
    /// <summary>Creates a result.</summary>
    public MzxDecoded(byte[] buffer, int length, MzxStats stats)
    {
        Buffer = buffer;
        Length = length;
        Stats = stats;
    }

    /// <summary>The backing array: <see cref="Length"/> decoded bytes, then at most <see cref="Mzx.OutputSlack"/> bytes of scratch.</summary>
    public byte[] Buffer { get; }

    /// <summary>The declared (decoded) size.</summary>
    public int Length { get; }

    /// <summary>What the decode did.</summary>
    public MzxStats Stats { get; }

    /// <summary>The decoded bytes.</summary>
    public ReadOnlyMemory<byte> Data => Buffer.AsMemory(0, Length);

    /// <summary>The decoded bytes as a span.</summary>
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    /// <summary>A copy of exactly the decoded bytes.</summary>
    public byte[] ToArray() => Span.ToArray();
}

/// <summary>
/// MZX0 decompressor (docs/formats/containers.md 5): a 16-bit-word LZ codec with run, back-reference,
/// 64-entry ring-buffer and literal commands, and the engine's clear counter. A port of the engine routine
/// at VA 0xE7080 and of <c>tools/tsuki/mzx.py</c>.
/// </summary>
/// <remarks>
/// <para>The decoder tests "output full?" only between commands, so the last command may write up to
/// <see cref="MaxOvershoot"/> bytes past the declared size (containers.md 5.2). Output buffers therefore
/// need <see cref="OutputSlack"/> spare bytes: use <see cref="BufferSize"/>.</para>
/// <para>Inverted streams (allscr.mrg scripts, containers.md 5.4) are decoded plain and then XORed with 0xFF,
/// which is what the engine does.</para>
/// </remarks>
public static class Mzx
{
    /// <summary>Bytes of the stream header: <c>"MZX0"</c> and the u32 decoded size.</summary>
    public const int HeaderSize = 8;

    /// <summary>The most the last command can write past the declared size: 63 words and 1 byte.</summary>
    public const int MaxOvershoot = 127;

    /// <summary>Spare bytes an output buffer needs behind the declared size.</summary>
    public const int OutputSlack = 128;

    private const int ResetWords = 0x1000;

    /// <summary>True if <paramref name="data"/> starts with an MZX0 header.</summary>
    public static bool IsMzx(ReadOnlySpan<byte> data) => data.Length >= HeaderSize && data[..4].SequenceEqual("MZX0"u8);

    /// <summary>The declared decoded size of a stream.</summary>
    /// <exception cref="ContainerFormatException">Not an MZX0 stream, or a size no buffer can hold.</exception>
    public static int DeclaredSize(ReadOnlySpan<byte> stream)
    {
        if (!IsMzx(stream))
        {
            throw new ContainerFormatException("not an MZX0 stream");
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(stream[4..]);
        if (size > (uint)(Array.MaxLength - OutputSlack))
        {
            throw new ContainerFormatException($"MZX0 declared size {size} is too large");
        }

        return (int)size;
    }

    /// <summary>The output buffer length a stream of <paramref name="declaredSize"/> bytes needs.</summary>
    public static int BufferSize(int declaredSize) => checked(declaredSize + OutputSlack);

    /// <summary>
    /// Decodes <paramref name="stream"/> into <paramref name="output"/>. The first <c>DeclaredSize</c> bytes of
    /// <paramref name="output"/> are the result; the bytes behind them are scratch.
    /// </summary>
    /// <param name="stream">The MZX0 stream, starting at its header. Trailing bytes are ignored.</param>
    /// <param name="output">At least <c>BufferSize(DeclaredSize(stream))</c> bytes.</param>
    /// <param name="invert">XOR the result with 0xFF (script sections).</param>
    /// <exception cref="ContainerFormatException">Malformed or truncated stream.</exception>
    /// <exception cref="ArgumentException"><paramref name="output"/> is too small.</exception>
    public static unsafe MzxStats Decode(ReadOnlySpan<byte> stream, Span<byte> output, bool invert = false)
    {
        int size = DeclaredSize(stream);
        if (output.Length < size + OutputSlack)
        {
            throw new ArgumentException(
                $"output buffer of {output.Length} bytes is smaller than the declared size {size} plus {OutputSlack} bytes of slack",
                nameof(output));
        }

        MzxStats stats;
        fixed (byte* s = stream)
        fixed (byte* d = output)
        {
            stats = DecodeCore(s, stream.Length, d, size);
        }

        if (invert)
        {
            XorFf(output[..size]);
        }

        return stats;
    }

    /// <summary>Decodes into a new buffer.</summary>
    /// <exception cref="ContainerFormatException">Malformed or truncated stream.</exception>
    public static MzxDecoded Decompress(ReadOnlySpan<byte> stream, bool invert = false)
    {
        int size = DeclaredSize(stream);
        // One command writes at most 128 bytes from one input byte, so a stream cannot declare more.
        if (size > (long)(stream.Length - HeaderSize) * 128)
        {
            throw new ContainerFormatException(
                $"MZX0 declared size {size} cannot come from a {stream.Length}-byte stream");
        }

        byte[] buffer = GC.AllocateUninitializedArray<byte>(BufferSize(size));
        var stats = Decode(stream, buffer, invert);
        return new MzxDecoded(buffer, size, stats);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe MzxStats DecodeCore(byte* src, int srcLen, byte* dst, int size)
    {
        byte* sp = src + HeaderSize;
        byte* se = src + srcLen;
        byte* dp = dst;
        byte* dEnd = dst + size;
        uint last = 0;
        int counter = 0;
        int rpos = 0;
        int commands = 0;

        // 64 words, doubled so that a literal can be stored without a wrap test (see the literal case).
        ushort* ring = stackalloc ushort[128];
        new Span<ushort>(ring, 128).Clear();

        while (dp < dEnd)
        {
            if (counter <= 0)
            {
                counter = ResetWords;
                last = 0;
            }

            if (sp >= se)
            {
                ThrowExhausted(dp - dst, size);
            }

            uint c = *sp++;
            int n = (int)(c >> 2) + 1;
            commands++;
            switch (c & 3)
            {
                case 0: // RLE: repeat the last word n times
                    {
                        counter -= n;
                        ushort w = (ushort)last;
                        if (w == 0)
                        {
                            Unsafe.InitBlockUnaligned(dp, 0, (uint)(n * 2));
                        }
                        else
                        {
                            ushort* p = (ushort*)dp;
                            for (int i = 0; i < n; i++)
                            {
                                p[i] = w;
                            }
                        }

                        dp += n * 2;
                        break;
                    }

                case 1: // BACKREF: n words from 2 * (d + 1) bytes back, word by word (the copy may overlap)
                    {
                        counter -= n;
                        if (sp >= se)
                        {
                            ThrowTruncated("backref distance byte missing");
                        }

                        int dist = (*sp++ + 1) * 2;
                        if (dp - dst < dist)
                        {
                            ThrowBackref(dp - dst, dist);
                        }

                        int len = n * 2;
                        byte* from = dp - dist;
                        if (dist >= len)
                        {
                            Unsafe.CopyBlockUnaligned(dp, from, (uint)len);
                        }
                        else
                        {
                            ushort* d = (ushort*)dp;
                            ushort* s = (ushort*)from;
                            for (int i = 0; i < n; i++)
                            {
                                d[i] = s[i];
                            }
                        }

                        dp += len;
                        last = *(ushort*)(dp - 2);
                        break;
                    }

                case 2: // RINGBUF: exactly one word, counted as one
                    counter -= 1;
                    last = ring[c >> 2];
                    *(ushort*)dp = (ushort)last;
                    dp += 2;
                    break;

                default: // LITERAL: n words from the stream; every word also enters the ring
                    {
                        counter -= n;
                        int len = n * 2;
                        if (se - sp < len)
                        {
                            ThrowTruncated("literal runs past end of input");
                        }

                        Unsafe.CopyBlockUnaligned(dp, sp, (uint)len);
                        Unsafe.CopyBlockUnaligned(ring + rpos, dp, (uint)len);
                        rpos += n;
                        if (rpos >= 64)
                        {
                            rpos -= 64;
                            if (rpos > 0)
                            {
                                Unsafe.CopyBlockUnaligned(ring, ring + 64, (uint)(rpos * 2));
                            }
                        }

                        sp += len;
                        dp += len;
                        last = *(ushort*)(dp - 2);
                        break;
                    }
            }
        }

        int produced = (int)(dp - dst);
        return new MzxStats((int)(sp - src), commands, Math.Max(0, produced - size));
    }

    private static void XorFf(Span<byte> data)
    {
        ref byte r = ref MemoryMarshal.GetReference(data);
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int w = Vector<byte>.Count;
            for (; i <= data.Length - w; i += w)
            {
                Vector.OnesComplement(Vector.LoadUnsafe(ref r, (nuint)i)).StoreUnsafe(ref r, (nuint)i);
            }
        }

        for (; i < data.Length; i++)
        {
            data[i] = (byte)~data[i];
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowExhausted(long produced, int size) =>
        throw new ContainerFormatException($"MZX0 input exhausted at output {produced}/{size}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTruncated(string what) => throw new ContainerFormatException($"MZX0: {what}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowBackref(long produced, int dist) =>
        throw new ContainerFormatException($"MZX0 backref before start of output (op={produced}, dist={dist})");
}
