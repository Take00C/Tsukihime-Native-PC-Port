using System.Buffers.Binary;

namespace Tsuki.Tests.Synthetic.Data;

/// <summary>Builds HED files byte by byte, straight from the record layouts of containers.md 2.1 and 3.1.</summary>
internal static class HedBuilder
{
    /// <summary>One HED8 record: u16 sector_lo, u16 sector_hi, u16 size_sectors, u16 size_lo.</summary>
    public static byte[] Hed8Record(int sectorLo, int sectorHi, int sizeSectors, int sizeLo)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), (ushort)sectorLo);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)sectorHi);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)sizeSectors);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)sizeLo);
        return b;
    }

    /// <summary>A HED8 record for a 20-bit sector offset and an exact size.</summary>
    public static byte[] Hed8(int stored20, int size)
    {
        int sectors = (size + 0x7FF) / 0x800;
        return Hed8Record(stored20 & 0xFFFF, (stored20 >> 16) << 12, sectors, size & 0xFFFF);
    }

    /// <summary>One HED4 record: u16 w0 (sector bits 0..15), u16 w1 (bits 0..11 sectors, 12..15 sector bits 16..19).</summary>
    public static byte[] Hed4(int stored20, int sectors)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), (ushort)(stored20 & 0xFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)(((stored20 >> 16) << 12) | sectors));
        return b;
    }

    public static byte[] Ff(int count) => Enumerable.Repeat((byte)0xFF, count).ToArray();

    public static byte[] Join(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}

/// <summary>Builds NAM32 and NAM8 tables.</summary>
internal static class NamBuilder
{
    public static byte[] Nam32Record(string name, string tag = "")
    {
        byte[] r = new byte[32];
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(r, 0);
        if (tag.Length > 0)
        {
            System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(r, 28);
        }

        r[30] = (byte)'\r';
        r[31] = (byte)'\n';
        return r;
    }

    public static byte[] Nam32(bool terminator, params string[] names)
    {
        var list = names.Select(n => Nam32Record(n)).ToList();
        if (terminator)
        {
            list.Add(new byte[32]);
        }

        return [.. list.SelectMany(r => r)];
    }

    public static byte[] Nam8(params string[] names)
    {
        var bytes = new List<byte>();
        foreach (string n in names)
        {
            byte[] r = new byte[8];
            System.Text.Encoding.ASCII.GetBytes(n).CopyTo(r, 0);
            bytes.AddRange(r);
        }

        bytes.AddRange(new byte[2]);
        return [.. bytes];
    }
}

/// <summary>Builds mrgd00 containers following the packing rule of containers.md 4.3.</summary>
internal static class MzpBuilder
{
    public static byte[] Build(params byte[][] sections)
    {
        int n = sections.Length;
        int dataStart = 8 + (8 * n);
        var data = new List<byte>();
        var records = new List<byte>();
        long end = dataStart;
        long cursor = dataStart;
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                long pad = 8 - (end % 8);
                for (long p = 0; p < pad; p++)
                {
                    data.Add(0xFF);
                }

                cursor = end + pad;
            }

            long rel = cursor - dataStart;
            int sectorOffset = (int)(rel / 0x800);
            int byteOffset = (int)(rel % 0x800);
            int size = sections[i].Length;
            int sizeSectors = (byteOffset + size + 0x7FF) / 0x800;
            byte[] rec = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(0), (ushort)sectorOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(2), (ushort)byteOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(4), (ushort)sizeSectors);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(6), (ushort)(size & 0xFFFF));
            records.AddRange(rec);
            data.AddRange(sections[i]);
            end = cursor + size;
        }

        long tail = 8 - (end % 8);
        for (long p = 0; p < tail; p++)
        {
            data.Add(0xFF);
        }

        var head = new List<byte>("mrgd00"u8.ToArray()) { (byte)(n & 0xFF), (byte)(n >> 8) };
        head.AddRange(records);
        head.AddRange(data);
        return [.. head];
    }
}

/// <summary>
/// Builds MZX0 streams command by command and, at the same time, plays them through a plain model of the engine
/// routine (containers.md 5.2) written independently of Tsuki.Data: the model output is what a decoder must produce.
/// </summary>
internal sealed class MzxModel
{
    private readonly List<byte> _stream = [];
    private readonly List<byte> _out = [];
    private readonly ushort[] _ring = new ushort[64];
    private int _ringPos;
    private int _counter;
    private ushort _last;
    private int _literalWords;
    private int _commands;
    private int _outBeforeLast;

    public IReadOnlyList<byte> Output => _out;

    public int Commands => _commands;

    public int OutputBeforeLastCommand => _outBeforeLast;

    public int StreamLength => _stream.Count;

    public int LiteralWords => _literalWords;

    private void Begin()
    {
        if (_counter <= 0)
        {
            _counter = 0x1000;
            _last = 0;
        }

        _commands++;
        _outBeforeLast = _out.Count;
    }

    private void Put(ushort w)
    {
        _out.Add((byte)(w & 0xFF));
        _out.Add((byte)(w >> 8));
    }

    public void Literal(params ushort[] words)
    {
        Begin();
        _counter -= words.Length;
        _stream.Add((byte)(3 | ((words.Length - 1) << 2)));
        foreach (ushort w in words)
        {
            _stream.Add((byte)(w & 0xFF));
            _stream.Add((byte)(w >> 8));
            Put(w);
            _ring[_ringPos] = w;
            _ringPos = (_ringPos + 1) & 63;
            _last = w;
            _literalWords++;
        }
    }

    public void Rle(int n)
    {
        Begin();
        _counter -= n;
        _stream.Add((byte)(0 | ((n - 1) << 2)));
        for (int i = 0; i < n; i++)
        {
            Put(_last);
        }
    }

    public void Backref(int n, int distanceWords)
    {
        Begin();
        _counter -= n;
        _stream.Add((byte)(1 | ((n - 1) << 2)));
        _stream.Add((byte)(distanceWords - 1));
        int dist = 2 * distanceWords;
        for (int i = 0; i < n; i++)
        {
            int src = _out.Count - dist;
            ushort w = (ushort)(_out[src] | (_out[src + 1] << 8));
            Put(w);
            _last = w;
        }
    }

    public void Ring(int slot)
    {
        Begin();
        _counter -= 1;
        _stream.Add((byte)(2 | (slot << 2)));
        _last = _ring[slot];
        Put(_last);
    }

    /// <summary>The stream with the header for <paramref name="declaredSize"/>.</summary>
    public byte[] Stream(int declaredSize)
    {
        byte[] h = new byte[8];
        "MZX0"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)declaredSize);
        return [.. h, .. _stream];
    }

    /// <summary>A random but valid command sequence of about <paramref name="targetBytes"/> output bytes.</summary>
    public static MzxModel Random(Random rng, int targetBytes)
    {
        var m = new MzxModel();
        while (m._out.Count < targetBytes)
        {
            int roll = rng.Next(100);
            int words = m._out.Count / 2;
            int n = rng.Next(4) == 0 ? rng.Next(1, 65) : rng.Next(1, 9);
            if (roll < 35 || words == 0)
            {
                ushort[] w = new ushort[n];
                for (int i = 0; i < n; i++)
                {
                    w[i] = (ushort)rng.Next(0x10000);
                }

                m.Literal(w);
            }
            else if (roll < 65)
            {
                m.Backref(n, rng.Next(1, Math.Min(256, words) + 1));
            }
            else if (roll < 80)
            {
                m.Rle(n);
            }
            else if (m._literalWords > 0)
            {
                m.Ring(rng.Next(Math.Min(64, m._literalWords)));
            }
            else
            {
                m.Rle(n);
            }
        }

        return m;
    }
}
