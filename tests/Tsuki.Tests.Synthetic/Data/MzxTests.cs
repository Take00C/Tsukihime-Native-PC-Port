using System.Buffers.Binary;
using Tsuki.Data.Compression;
using Tsuki.Data.Containers;

namespace Tsuki.Tests.Synthetic.Data;

/// <summary>
/// MZX0 on crafted streams (docs/formats/containers.md 5): every command, the clear counter, the output overrun,
/// inverted streams and malformed input; plus randomised streams checked against a plain model of the engine
/// routine. No game data; the real-data comparison with the Python oracle is in Tsuki.Tests.Data.
/// </summary>
public sealed class MzxTests
{
    private static byte[] Header(int size)
    {
        byte[] h = new byte[8];
        "MZX0"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)size);
        return h;
    }

    private static byte[] Stream(int size, params byte[] commands) => [.. Header(size), .. commands];

    private static byte[] Decode(byte[] stream, bool invert = false)
    {
        var r = Mzx.Decompress(stream, invert);
        return r.ToArray();
    }

    [Fact]
    public void TheWorkedExampleOfSection53DecodesToTheRiffHeader()
    {
        // voice entry 0: 23 <9 words> | 0e | 0b <3 words> | 0e | 03 <1 word> | 0e   (containers.md 5.3)
        byte[] cmds =
        [
            0x23, 0x52, 0x49, 0x46, 0x46, 0x3C, 0x07, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20, 0x34, 0x00,
            0x0E,
            0x0B, 0xFE, 0xFF, 0x01, 0x00, 0x80, 0xBB,
            0x0E,
            0x03, 0x4C, 0x1D,
            0x0E,
        ];
        byte[] stream = Stream(32, cmds);
        var r = Mzx.Decompress(stream);
        Assert.Equal(32, r.Length);
        byte[] expected =
        [
            .. "RIFF"u8.ToArray(), 0x3C, 0x07, 0x00, 0x00, .. "WAVEfmt "u8.ToArray(), 0x34, 0x00,
            0x00, 0x00,
            0xFE, 0xFF, 0x01, 0x00, 0x80, 0xBB,
            0x00, 0x00,
            0x4C, 0x1D,
            0x00, 0x00,
        ];
        Assert.Equal(expected, r.ToArray());
        Assert.Equal(new MzxStats(stream.Length, 6, 0), r.Stats);
    }

    [Fact]
    public void RleRepeatsTheLastWordAndStartsFromZero()
    {
        // RLE first: last = 0x0000. Then a literal word, then RLE of that word.
        byte[] stream = Stream(12, 0x08, /* RLE n=3 -> 6 zero bytes */ 0x03, 0xAB, 0xCD, /* literal 1 word */ 0x04 /* RLE n=2 */);
        Assert.Equal([0, 0, 0, 0, 0, 0, 0xAB, 0xCD, 0xAB, 0xCD, 0xAB, 0xCD], Decode(stream));
        Assert.Equal(12, Mzx.DeclaredSize(stream));
    }

    [Fact]
    public void BackrefOverlapsWordByWord()
    {
        // literal AB CD; backref n=4 distance 1 word -> ABCD x5 (the copy reads words it has just written)
        byte[] stream = Stream(10, 0x03, 0xAB, 0xCD, 0x0D, 0x00);
        Assert.Equal([0xAB, 0xCD, 0xAB, 0xCD, 0xAB, 0xCD, 0xAB, 0xCD, 0xAB, 0xCD], Decode(stream));

        // literal 4 words 0..7; backref n=2 distance 4 words (d = 3) -> copies words 0,1 again
        byte[] stream2 = Stream(12, 0x0F, 0, 1, 2, 3, 4, 5, 6, 7, 0x05, 0x03);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3], Decode(stream2));

        // distance 2 words with n=5 repeats a 2-word pattern
        byte[] stream3 = Stream(14, 0x07, 1, 2, 3, 4, 0x11, 0x01);
        Assert.Equal([1, 2, 3, 4, 1, 2, 3, 4, 1, 2, 3, 4, 1, 2], Decode(stream3));
    }

    [Fact]
    public void RingBufferReadsTheLiteralWordsAndWrapsAt64()
    {
        // 70 literal words (64 + 6): the second command overwrites ring slots 0..5, slot 6 still holds the first word 7
        var model = new MzxModel();
        ushort[] a = Enumerable.Range(1, 64).Select(i => (ushort)(0x0100 + i)).ToArray();
        ushort[] b = Enumerable.Range(1, 6).Select(i => (ushort)(0x0200 + i)).ToArray();
        model.Literal(a);
        model.Literal(b);
        model.Ring(0);
        model.Ring(6);
        model.Ring(63);
        byte[] stream = model.Stream(model.Output.Count);
        byte[] output = Decode(stream);
        Assert.Equal(model.Output.ToArray(), output);
        // slot 0 = first word of the second command (0x0201), slot 6 = 0x0107, slot 63 = 0x0140
        int n = output.Length;
        Assert.Equal(0x0201, BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(n - 6)));
        Assert.Equal(0x0107, BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(n - 4)));
        Assert.Equal(0x0140, BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(n - 2)));
    }

    [Fact]
    public void TheClearCounterResetsTheLastWordEvery0x1000Words()
    {
        // 64 literal commands of 64 words = 4096 words: the counter is exactly 0, so the next command clears `last`.
        var atBoundary = new MzxModel();
        for (int i = 0; i < 64; i++)
        {
            atBoundary.Literal(Enumerable.Repeat((ushort)(0x1111 * ((i % 9) + 1)), 64).ToArray());
        }

        atBoundary.Rle(4);
        byte[] s1 = atBoundary.Stream(atBoundary.Output.Count);
        byte[] o1 = Decode(s1);
        Assert.Equal(atBoundary.Output.ToArray(), o1);
        Assert.All(o1[^8..], b => Assert.Equal(0, b)); // RLE after the boundary repeats 0x0000

        // one word short of the boundary (4095 words): no reset, RLE repeats the last literal word
        var beforeBoundary = new MzxModel();
        for (int i = 0; i < 63; i++)
        {
            beforeBoundary.Literal(Enumerable.Repeat((ushort)(0x1111 * ((i % 9) + 1)), 64).ToArray());
        }

        beforeBoundary.Literal(Enumerable.Repeat((ushort)0x7777, 63).ToArray());
        beforeBoundary.Rle(4);
        byte[] o2 = Decode(beforeBoundary.Stream(beforeBoundary.Output.Count));
        Assert.Equal(beforeBoundary.Output.ToArray(), o2);
        Assert.All(o2[^8..], b => Assert.Equal(0x77, b));
    }

    [Fact]
    public void RingBufferReadsCountAsOneWordForTheCounter()
    {
        // 63 literal commands of 64 words + 1 literal of 62 words = 4094 words, then ONE ring read (counter -1 -> 1),
        // then an RLE of 1 word: counter 1 > 0, no reset, so the RLE repeats the ring word.
        var m = new MzxModel();
        for (int i = 0; i < 63; i++)
        {
            m.Literal(Enumerable.Repeat((ushort)(0x0101 * (i + 1)), 64).ToArray());
        }

        m.Literal(Enumerable.Repeat((ushort)0x5A5A, 62).ToArray());
        m.Ring(2);
        m.Rle(1);
        byte[] output = Decode(m.Stream(m.Output.Count));
        Assert.Equal(m.Output.ToArray(), output);
        Assert.Equal(output[^2], output[^4]);
        Assert.Equal(0x5A5A, BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(output.Length - 2)));
    }

    [Fact]
    public void TheLastCommandMayRunPastTheDeclaredSize()
    {
        // declared size 1 (odd): a 64-word literal still runs to the end, 127 bytes past the size
        byte[] literal = [.. new byte[] { 0xFF }.Concat(Enumerable.Range(0, 128).Select(i => (byte)i))];
        byte[] stream = Stream(1, literal);
        var r = Mzx.Decompress(stream);
        Assert.Equal(1, r.Length);
        Assert.Equal(127, r.Stats.Overshoot);
        Assert.Equal(Mzx.MaxOvershoot, r.Stats.Overshoot);
        Assert.Equal(stream.Length, r.Stats.Consumed);
        Assert.Equal(0, r.ToArray()[0]);
        Assert.True(r.Buffer.Length >= 1 + 127); // the buffer really holds the overrun

        // an even declared size overruns by an even amount: 2 bytes wanted, 64 words written
        var r2 = Mzx.Decompress(Stream(2, literal));
        Assert.Equal(126, r2.Stats.Overshoot);
    }

    [Fact]
    public void ExactFitHasNoOvershootAndTrailingBytesAreIgnored()
    {
        byte[] stream = [.. Stream(4, 0x07, 1, 2, 3, 4), .. new byte[] { 9, 9, 9, 9 }];
        var r = Mzx.Decompress(stream);
        Assert.Equal([1, 2, 3, 4], r.ToArray());
        Assert.Equal(0, r.Stats.Overshoot);
        Assert.Equal(stream.Length - 4, r.Stats.Consumed);
        Assert.Equal(1, r.Stats.Commands);
    }

    [Fact]
    public void ZeroSizeStreamIsJustAHeader()
    {
        var r = Mzx.Decompress(Header(0));
        Assert.Equal(0, r.Length);
        Assert.Equal(8, r.Stats.Consumed);
        Assert.Equal(0, r.Stats.Commands);
    }

    [Fact]
    public void InvertedStreamsAreDecodedPlainThenXorFf()
    {
        var model = MzxModel.Random(new Random(77), 6000);
        byte[] stream = model.Stream(model.Output.Count);
        byte[] plain = Decode(stream);
        byte[] inverted = Decode(stream, invert: true);
        Assert.Equal(plain.Select(b => (byte)(b ^ 0xFF)).ToArray(), inverted);

        // the output buffer API: the slack bytes behind the declared size are not part of the result
        byte[] buffer = new byte[Mzx.BufferSize(plain.Length)];
        Mzx.Decode(stream, buffer, invert: true);
        Assert.Equal(inverted, buffer[..plain.Length]);
    }

    [Fact]
    public void BufferContractIsChecked()
    {
        byte[] stream = Stream(100, 0x03, 1, 2);
        Assert.Equal(228, Mzx.BufferSize(100));
        Assert.Throws<ArgumentException>(() => Mzx.Decode(stream, new byte[100]));
        Assert.Throws<ArgumentException>(() => Mzx.Decode(stream, new byte[227]));
    }

    [Fact]
    public void MalformedStreamsAreRejectedWithAReason()
    {
        Assert.Throws<ContainerFormatException>(() => Mzx.Decompress("MZX1\0\0\0\0"u8));
        Assert.Throws<ContainerFormatException>(() => Mzx.Decompress("MZX"u8));
        Assert.False(Mzx.IsMzx("MZX"u8));

        // input exhausted: declares 10 bytes, delivers 2
        var e1 = Assert.Throws<ContainerFormatException>(() => Mzx.Decompress(Stream(10, 0x03, 1, 2)));
        Assert.Contains("exhausted", e1.Message);

        // literal cut off: n=3 words announced, 1 delivered
        var e2 = Assert.Throws<ContainerFormatException>(() => Mzx.Decompress(Stream(6, 0x0B, 1, 2)));
        Assert.Contains("literal", e2.Message);

        // backref distance byte missing
        var e3 = Assert.Throws<ContainerFormatException>(() => Mzx.Decompress(Stream(6, 0x03, 1, 2, 0x01)));
        Assert.Contains("distance", e3.Message);

        // backref before the start of the output: distance 4 words with only 1 word written
        var e4 = Assert.Throws<ContainerFormatException>(() => Mzx.Decompress(Stream(6, 0x03, 1, 2, 0x01, 0x03)));
        Assert.Contains("before start", e4.Message);

        // a declared size no stream of this length can produce (128 bytes per input byte at most)
        var e5 = Assert.Throws<ContainerFormatException>(() => Mzx.Decompress(Stream(1_000_000, 0x03, 1, 2)));
        Assert.Contains("cannot come from", e5.Message);

        // sizes that cannot be a buffer at all
        byte[] huge = Header(0);
        BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(4), 0xFFFFFFF0u);
        Assert.Throws<ContainerFormatException>(() => Mzx.DeclaredSize(huge));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 40)]
    [InlineData(4, 300)]
    [InlineData(5, 4096)]
    [InlineData(6, 8192)]
    [InlineData(7, 8193)]
    [InlineData(8, 20_000)]
    [InlineData(9, 70_001)]
    [InlineData(10, 262_144)]
    public void RandomStreamsEqualTheModel(int seed, int target)
    {
        var rng = new Random(seed * 7919);
        var m = MzxModel.Random(rng, target);
        // the declared size lies in (output before the last command, output after it]: the last command is
        // the one that may overrun
        int lo = m.OutputBeforeLastCommand + 1;
        int hi = m.Output.Count;
        int declared = rng.Next(lo, hi + 1);
        byte[] stream = m.Stream(declared);

        var r = Mzx.Decompress(stream);
        Assert.Equal(declared, r.Length);
        Assert.Equal(m.Output.Take(declared).ToArray(), r.ToArray());
        Assert.Equal(stream.Length, r.Stats.Consumed);
        Assert.Equal(m.Commands, r.Stats.Commands);
        Assert.Equal(m.Output.Count - declared, r.Stats.Overshoot);
        Assert.InRange(r.Stats.Overshoot, 0, Mzx.MaxOvershoot);

        // the same into a buffer full of junk: no byte may be read before it is written
        byte[] junk = new byte[Mzx.BufferSize(declared)];
        Array.Fill(junk, (byte)0xC3);
        var stats = Mzx.Decode(stream, junk);
        Assert.Equal(r.Stats, stats);
        Assert.Equal(r.ToArray(), junk[..declared]);
    }

    [Fact]
    public void GarbageStreamsFailCleanlyAndNeverWritePastTheSlack()
    {
        // 4000 random byte strings behind a valid header with a random declared size: each either decodes or is
        // rejected with a ContainerFormatException, and nothing is ever written behind size + OutputSlack.
        var rng = new Random(31337);
        int decoded = 0;
        int rejected = 0;
        for (int round = 0; round < 4000; round++)
        {
            int declared = rng.Next(0, 3000);
            byte[] body = new byte[rng.Next(0, 600)];
            rng.NextBytes(body);
            byte[] stream = [.. Header(declared), .. body];
            byte[] buffer = new byte[Mzx.BufferSize(declared) + 64];
            Array.Fill(buffer, (byte)0xEE);
            try
            {
                Mzx.Decode(stream, buffer, invert: round % 2 == 1);
                decoded++;
            }
            catch (ContainerFormatException)
            {
                rejected++;
            }

            Assert.True(buffer.AsSpan(Mzx.BufferSize(declared)).ToArray().All(b => b == 0xEE), $"round {round}: bytes written past the slack");
        }

        Assert.True(decoded > 0 && rejected > 0, $"{decoded} decoded, {rejected} rejected");
    }

    [Fact]
    public void ManyRandomStreamsEqualTheModel()
    {
        for (int seed = 100; seed < 400; seed++)
        {
            var rng = new Random(seed);
            var m = MzxModel.Random(rng, rng.Next(1, 12_000));
            int declared = rng.Next(m.OutputBeforeLastCommand + 1, m.Output.Count + 1);
            var r = Mzx.Decompress(m.Stream(declared));
            Assert.True(m.Output.Take(declared).SequenceEqual(r.Span.ToArray()), $"seed {seed}");
            Assert.Equal(m.Commands, r.Stats.Commands);
        }
    }

    [Fact]
    public void LargeRandomStreamCrossesManyCounterResets()
    {
        // 1 MiB: 128 resets of the 0x1000-word counter, with literals, runs, back references and ring reads
        var m = MzxModel.Random(new Random(2026), 1 << 20);
        int declared = m.Output.Count - Math.Min(100, m.Output.Count - m.OutputBeforeLastCommand - 1);
        byte[] stream = m.Stream(declared);
        var r = Mzx.Decompress(stream);
        Assert.True(m.Output.Take(declared).SequenceEqual(r.Span.ToArray()));
    }
}
