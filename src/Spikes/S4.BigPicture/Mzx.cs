// Spike S4: MZX0 decompressor (docs/formats/containers.md 5.1-5.2; engine VA 0xE7080).
// Plain streams only (image tiles); inverted script streams are not needed here.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace S4.BigPicture;

internal static unsafe class Mzx
{
    /// <summary>A command may run up to 127 bytes past the declared size (containers.md 5.2).</summary>
    public const int Overrun = 128;

    private const uint Magic = 0x30585A4D; // "MZX0"

    public static int DeclaredSize(ReadOnlySpan<byte> stream)
    {
        if (stream.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(stream) != Magic)
            throw new InvalidDataException("not an MZX0 stream");
        return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(stream[4..]));
    }

    /// <summary>
    /// Decompresses <paramref name="src"/> into <paramref name="dst"/>, which must hold
    /// <c>size + Overrun</c> bytes; bytes past <c>size</c> are scratch. Returns the input bytes consumed.
    /// </summary>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int size = DeclaredSize(src);
        if (dst.Length < size + Overrun)
            throw new ArgumentException($"output needs {size + Overrun} bytes, got {dst.Length}");
        fixed (byte* ps = src)
        fixed (byte* pd = dst)
            return Decompress(ps, src.Length, pd, size);
    }

    [SkipLocalsInit]
    public static int Decompress(byte* src, int srcLen, byte* dst, int size)
    {
        byte* ip = src + 8;
        byte* iend = src + srcLen;
        byte* op = dst;
        byte* oend = dst + size;
        ushort* ring = stackalloc ushort[64];
        int ringPos = 0;
        int counter = 0;
        ushort last = 0;
        while (op < oend)
        {
            if (counter <= 0)
            {
                // "clear counter": every 0x1000 words the last word goes back to INIT (5.2)
                counter = 0x1000;
                last = 0;
            }
            if (ip >= iend)
                throw new InvalidDataException("MZX0 stream truncated");
            int c = *ip++;
            int n = (c >> 2) + 1;
            switch (c & 3)
            {
                case 0: // RLE: n copies of the last word
                {
                    counter -= n;
                    ulong pat = last * 0x0001_0001_0001_0001UL;
                    byte* e = op + 2 * n;
                    // 8-byte stores round 2n up to a multiple of 8 (<= 128 bytes): stays inside the overrun area
                    for (byte* q = op; q < e; q += 8)
                        Unsafe.WriteUnaligned(q, pat);
                    op = e;
                    break;
                }
                case 1: // BACKREF: n words from 2*(d+1) bytes back, front to back
                {
                    counter -= n;
                    if (ip >= iend)
                        throw new InvalidDataException("MZX0 stream truncated");
                    int dist = 2 * (*ip++ + 1);
                    byte* from = op - dist;
                    if (from < dst)
                        throw new InvalidDataException("MZX0 back-reference before the output start");
                    int len = 2 * n;
                    if (dist >= 8)
                    {
                        // each 8-byte read ends at or before the current write position, so the
                        // copy equals the word-by-word copy; rounding stays inside the overrun area
                        for (int k = 0; k < len; k += 8)
                            Unsafe.WriteUnaligned(op + k, Unsafe.ReadUnaligned<ulong>(from + k));
                    }
                    else
                    {
                        for (int k = 0; k < len; k += 2)
                            Unsafe.WriteUnaligned(op + k, Unsafe.ReadUnaligned<ushort>(from + k));
                    }
                    op += len;
                    last = Unsafe.ReadUnaligned<ushort>(op - 2);
                    break;
                }
                case 2: // RINGBUF: one word from ring slot c >> 2
                    counter -= 1;
                    last = ring[c >> 2];
                    Unsafe.WriteUnaligned(op, last);
                    op += 2;
                    break;
                default: // LITERAL: n words from the input, each also pushed into the ring
                {
                    counter -= n;
                    int len = 2 * n;
                    if (ip + len > iend)
                        throw new InvalidDataException("MZX0 literal runs past the stream end");
                    Unsafe.CopyBlockUnaligned(op, ip, (uint)len);
                    for (int k = 0; k < n; k++)
                    {
                        ring[ringPos] = Unsafe.ReadUnaligned<ushort>(ip + 2 * k);
                        ringPos = (ringPos + 1) & 63;
                    }
                    last = Unsafe.ReadUnaligned<ushort>(ip + len - 2);
                    ip += len;
                    op += len;
                    break;
                }
            }
        }
        return (int)(ip - src);
    }
}
