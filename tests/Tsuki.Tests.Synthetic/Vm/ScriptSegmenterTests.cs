using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Vm;

namespace Tsuki.Tests.Synthetic.Vm;

/// <summary>
/// Segmenting (docs/formats/script.md 3.1, engine.md 5.2): the cases of <c>tools/tests/test_script.py</c>
/// (<c>test_parse_basic_and_markers</c>, <c>test_parse_engine_quirks</c>) and the edges of the 0x300-byte window.
/// </summary>
public sealed class ScriptSegmenterTests
{
    private static List<ScriptSegment> Segments(byte[] script)
    {
        var list = new List<ScriptSegment>();
        for (int pos = 0; pos < script.Length;)
        {
            ScriptSegment s = ScriptSegmenter.Read(script, pos);
            list.Add(s);
            pos = s.End;
        }

        return list;
    }

    private static string Key(byte[] script, ScriptSegment s) => Encoding.Latin1.GetString(script, s.NameStart, 4);

    [Fact]
    public void BasicStatementsAndMarkers()
    {
        byte[] src = "_ZZ00a01();_ZZ00b01(START);_WKST(F823,eq,0);_STCH(0,img2345,258-18,154-19,1000,255,2,,CC,,);"u8
            .ToArray().Concat("_SEPL(0,1,MOON_SE03001,,`001:4000,,`017:50);_ZM01801($000107@k@e);_JUMP(240,START);"u8.ToArray()).ToArray();
        List<ScriptSegment> segs = Segments(src);
        Assert.Equal(["ZZ00", "ZZ00", "WKST", "STCH", "SEPL", "ZM01", "JUMP"], segs.Select(s => Key(src, s)));
        Assert.All(segs, s => Assert.True(s.IsCommand && !s.Truncated));

        // the segments tile the bytes exactly
        Assert.Equal(0, segs[0].Start);
        for (int i = 1; i < segs.Count; i++)
        {
            Assert.Equal(segs[i - 1].End, segs[i].Start);
        }

        Assert.Equal(src.Length, segs[^1].End);
        Assert.Equal(Encoding.ASCII.GetString(src).IndexOf("_JUMP", StringComparison.Ordinal), segs[6].Underscore);

        // markers: ids decoded like the engine (only lower-case hex is meaningful)
        Assert.True(ScriptSegmenter.TryReadMarkerId(src, segs[0], out int block, out int sub));
        Assert.Equal((0x00A, 1), (block, sub));
        Assert.True(ScriptSegmenter.TryReadMarkerId(src, segs[1], out block, out sub));
        Assert.Equal((0x00B, 1), (block, sub));
        Assert.False(ScriptSegmenter.TryReadMarkerId(src, segs[2], out _, out _)); // WKST is not a marker
        Assert.Equal(0x11, MarkerId.Hex("00A"u8));
        Assert.Equal(0xF82, MarkerId.Hex("f82"u8));
        Assert.Equal(unchecked((short)0xFFF0), (short)MarkerId.Hex("fff0"u8));

        byte[] upper = "_ZZ00A01();"u8.ToArray();
        Assert.True(ScriptSegmenter.TryReadMarkerId(upper, ScriptSegmenter.Read(upper, 0), out block, out _));
        Assert.Equal(0x11, block);
    }

    [Fact]
    public void EngineQuirksAreFollowed()
    {
        // missing '(' (retail 14_01A_CIEL14A_2), junk after ')', no '_' at all, lower-case name, text before the '_'
        byte[] src = "_STTR,0,26);_STZ4(5,498,498,498,498,0,0,0)gb);xyz;_abcd(1);_DMMY(0);junk_IFP4(,LV00100);_RTM2();"u8.ToArray();
        List<ScriptSegment> segs = Segments(src);
        Assert.Equal(7, segs.Count);

        Assert.True(segs[0].IsCommand);
        Assert.Equal("STTR", Key(src, segs[0]));

        Assert.True(segs[1].IsCommand);

        Assert.False(segs[2].HasName); // "xyz;" has no '_'
        Assert.False(segs[2].IsCommand);
        Assert.Equal(-1, segs[2].Underscore);

        Assert.True(segs[3].HasName); // "_abcd(1);" has a name, but the first letter is not A..Z: skipped
        Assert.False(segs[3].IsCommand);
        Assert.Equal("abcd", Key(src, segs[3]));

        Assert.True(segs[4].IsCommand); // DMMY is a name for the segmenter; the dispatch table ignores it later
        Assert.Equal("DMMY", Key(src, segs[4]));
        Assert.Equal("junk_IFP4(,LV00100);".Length, segs[5].Length);
        Assert.Equal("IFP4", Key(src, segs[5]));
        Assert.Equal(4, segs[5].Underscore - segs[5].Start);
        Assert.Equal("RTM2", Key(src, segs[6]));
    }

    [Fact]
    public void ANameThatDoesNotFitInTheSegmentIsNotAName()
    {
        // "_AB;" has only two name bytes before the terminator: tsuki.script reports an empty key
        byte[] src = "_AB;_ABC;_ABCD;"u8.ToArray();
        List<ScriptSegment> segs = Segments(src);
        Assert.Equal(3, segs.Count);
        Assert.False(segs[0].HasName);
        Assert.False(segs[0].IsCommand);

        // the fourth name byte may be the ';' itself: "_ABC;" is complete (us + 5 == end), as in the reference
        Assert.True(segs[1].HasName && segs[1].IsCommand);
        Assert.Equal("ABC;", Key(src, segs[1]));

        Assert.True(segs[2].HasName);
        Assert.Equal("ABCD", Key(src, segs[2]));
    }

    [Fact]
    public void TheStatementContractStartsTheArgumentsAtTheSeparator()
    {
        byte[] src = "_ZZ00a01();_WKST(F823,eq,0);"u8.ToArray();
        ScriptSegment seg = ScriptSegmenter.Read(src, 11);
        Assert.True(ScriptSegmenter.TryGetStatement(7, src, seg, out Statement st));
        Assert.Equal(CommandName.FromAscii("WKST"), st.Name);
        Assert.Equal(7, st.Script);
        Assert.Equal(11, st.Offset);
        Assert.Equal(src.Length, st.End);
        Assert.Equal("(F823,eq,0);", Encoding.ASCII.GetString(st.Args.Span));
        Assert.Equal(new ScriptPosition(7, src.Length), st.Next);

        byte[] skipped = "xyz;"u8.ToArray();
        Assert.False(ScriptSegmenter.TryGetStatement(0, skipped, ScriptSegmenter.Read(skipped, 0), out _));
    }

    [Fact]
    public void TheSegmentWindowIs0x300Bytes()
    {
        // ';' as the last byte of the window (offset 0x2FF): the segment is 0x300 bytes long
        byte[] a = [.. Enumerable.Repeat((byte)'x', 0x2FF), (byte)';', .. "_END_();"u8.ToArray()];
        ScriptSegment s = ScriptSegmenter.Read(a, 0);
        Assert.Equal(0x300, s.End);
        Assert.False(s.Truncated);
        Assert.False(s.HasName);

        // ';' at offset 0x300 is outside the window: the segment is cut at 0x300 and flagged
        byte[] b = [.. Enumerable.Repeat((byte)'x', 0x300), (byte)';', .. "_END_();"u8.ToArray()];
        s = ScriptSegmenter.Read(b, 0);
        Assert.Equal(0x300, s.End);
        Assert.True(s.Truncated);
        ScriptSegment next = ScriptSegmenter.Read(b, s.End);
        Assert.Equal(1, next.End - next.Start); // the lone ';'

        // a name late in the window is still found; a name cut by the window is not
        byte[] c = [.. Enumerable.Repeat((byte)'x', 0x2F0), .. "_STCC(1);"u8.ToArray()];
        s = ScriptSegmenter.Read(c, 0);
        Assert.Equal(0x2F0, s.Underscore);
        Assert.Equal(c.Length, s.End);

        // no ';' before the end of the script and less than 0x300 bytes left: not truncated
        byte[] d = "_WKST(F1,eq,0)"u8.ToArray();
        s = ScriptSegmenter.Read(d, 0);
        Assert.Equal(d.Length, s.End);
        Assert.False(s.Truncated);
        Assert.True(s.IsCommand);

        // no ';' and exactly 0x300 bytes left: still the end of the script, so not truncated either
        byte[] e = [.. Enumerable.Repeat((byte)'y', 0x300)];
        Assert.False(ScriptSegmenter.Read(e, 0).Truncated);
    }

    [Fact]
    public void SegmentStartsTileAnyBytes()
    {
        var rng = new Random(12345);
        byte[] alphabet = "_;AZaz(),01 \0"u8.ToArray();
        for (int round = 0; round < 300; round++)
        {
            byte[] script = new byte[rng.Next(0, 2000)];
            for (int i = 0; i < script.Length; i++)
            {
                script[i] = alphabet[rng.Next(alphabet.Length)];
            }

            var starts = ScriptSegmenter.SegmentStarts(script);
            Assert.Equal(ScriptSegmenter.CountSegments(script), starts.Length);
            ScriptImage image = ScriptSegmenter.CreateImage(round, script);
            int pos = 0;
            for (int i = 0; i < starts.Length; i++)
            {
                Assert.Equal(pos, starts[i]);
                ScriptSegment s = ScriptSegmenter.Read(script, pos);
                Assert.Equal(s.End, image.SegmentEnd(i));
                Assert.InRange(s.Length, 1, ScriptSegmenter.MaxSegmentBytes);
                pos = s.End;
            }

            Assert.Equal(script.Length, pos);
        }

        Assert.Empty(ScriptSegmenter.SegmentStarts([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScriptSegmenter.Read("_A;"u8, 3));
    }
}
