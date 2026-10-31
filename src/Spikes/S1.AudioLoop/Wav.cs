using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace S1.AudioLoop;

// IEEE float32 WAV (format tag 3) writer; samples are written unchanged (no clipping, no dither).
internal static class Wav
{
    public static void WriteFloat32(string path, ReadOnlySpan<float> interleaved, int channels, int rate)
    {
        long dataBytes = (long)interleaved.Length * 4;
        if (dataBytes + 36 > uint.MaxValue)
        {
            throw new ArgumentException("too long for a RIFF file", nameof(interleaved));
        }

        Span<byte> h = stackalloc byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(rate * channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)(channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 32);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)dataBytes);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        fs.Write(h);
        fs.Write(MemoryMarshal.AsBytes(interleaved));
    }

    public static float[] ReadF32(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        float[] f = new float[b.Length / 4];
        MemoryMarshal.Cast<byte, float>(b.AsSpan(0, f.Length * 4)).CopyTo(f);
        return f;
    }
}
