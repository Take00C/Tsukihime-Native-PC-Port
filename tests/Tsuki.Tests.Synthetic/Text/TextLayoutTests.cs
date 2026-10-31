using System.Security.Cryptography;
using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Tests.Synthetic.Text;

/// <summary>
/// <c>Text_Layout</c> without game data (ARCHITECTURE 10.2): unit cases whose expected blocks the Python
/// oracle computed with the real font (the glyph entries it read are embedded), and the pure helpers.
/// The differential tests on the real data are in <c>Tsuki.Tests.Data/Text/Layout</c>.
/// </summary>
public sealed class TextLayoutTests
{
    private static readonly FontTables Fonts = SyntheticFont.Create();

    public static TheoryData<string> CaseNames() => [.. SyntheticLayoutCases.Cases.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ABlockEqualsTheOracleBitForBit(string name)
    {
        var c = SyntheticLayoutCases.Cases.Single(x => x.Name == name);
        var block = TextLayout.Layout(Encoding.UTF8.GetBytes(c.Text), c.Lang, Fonts, new LayoutParams(Flags: c.Flags, Window: c.Window));
        Assert.Equal(c.N, block.N);
        Assert.Equal(c.NLines, block.NLines);
        Assert.Equal(c.LineGlyphs, string.Join(",", block.LineGlyphs.Take(block.NLines)));
        Assert.Equal(c.Sha1, Convert.ToHexStringLower(SHA1.HashData(CanonicalBlock.Serialize(block))));
    }

    [Fact]
    public void AnEmptyLineGetsTwentyTimesTheGlyphScale()
    {
        var b = TextLayout.Layout("A^B^^C"u8, Language.En, Fonts);
        Assert.Equal(4, b.NLines);
        Assert.Equal(BitConverter.SingleToUInt32Bits(20f * b.Sy), BitConverter.SingleToUInt32Bits(b.LineHeight[2]));
        Assert.Equal(BitConverter.SingleToUInt32Bits((float)0.33333334 * (float)0.74), BitConverter.SingleToUInt32Bits(b.Sy));
    }

    [Fact]
    public void AnEmptyTextLeavesTheBlockUntouched()
    {
        var b = TextLayout.Layout(ReadOnlySpan<byte>.Empty, Language.En, Fonts);
        Assert.True(b.Empty);
        Assert.Equal(-1, b.EndCtr);
        Assert.Empty(b.Records);
        Assert.Equal(574, b.BoxWidth);
    }

    [Fact]
    public void TheScaleTableHoldsTheEbootFloat32Patterns()
    {
        // followups.md: entries 8 and 9 were 1 ulp off in the oracle (VA 0x2D3730).
        Assert.Equal(0x3E92AAAAu, BitConverter.SingleToUInt32Bits(TextLayout.Scales[8]));
        Assert.Equal(0x3EA15556u, BitConverter.SingleToUInt32Bits(TextLayout.Scales[9]));
        Assert.Equal(0.34375f, TextLayout.Scales[0]);
        Assert.Equal(0.4296875f, TextLayout.Scales[3]);
        Assert.Equal(new[] { 480, 536, 524, 0 }, TextLayout.FontMetrics);
        Assert.Equal(new[] { 0x632C, 0xF800, 0x345F, 0x1082, 0xAD55, 0x001F }, TextLayout.Colors);
    }

    [Fact]
    public void TheMainBoxWidthRule()
    {
        Assert.Equal(474, TextLayout.MainBoxWidth(Language.Ja));
        Assert.Equal(574, TextLayout.MainBoxWidth(Language.En));
        Assert.Equal(600, TextLayout.MainBoxWidth(Language.En, window: 42));
        Assert.Equal(600, TextLayout.MainBoxWidth(Language.Ja, window: 42));
        Assert.Equal(474, TextLayout.MainBoxWidth(Language.En, block: 14));
        Assert.Equal(480, TextLayout.MainBoxWidth(Language.En, 480));
    }

    [Fact]
    public void AGlyphRecordTableReadsPastItsEndLikeTheEngine()
    {
        var rects = new[] { new GlyphRect(34, 1, 2, 3, 4), new GlyphRect(35, 5, 6, -7, 8), new GlyphRect(36, 9, 10, 11, 12) };
        var t = new GlyphRecordTable("t", 2, GlyphRecordTable.Pack(rects));
        Assert.True(t.InRange(1));
        Assert.False(t.InRange(2));
        Assert.Equal(new GlyphRect(35, 5, 6, -7, 8), t.Get(1));
        Assert.Equal(new GlyphRect(36, 9, 10, 11, 12), t.Get(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.Get(3));
        Assert.Throws<ArgumentException>(() => new GlyphRecordTable("short", 4, new byte[30]));
    }

    [Fact]
    public void ALookupTreatsANegativeIndexAsMissing()
    {
        var t = new GlyphLookupTable("t", [0x41, 0x42], [5, -1]);
        Assert.Equal(5, t.Get(0x41));
        Assert.Equal(-1, t.Get(0x42));
        Assert.Equal(-1, t.Get(0x43));
    }

    // ---- pass 1 ----

    [Fact]
    public void WideCharactersAreTheOnesTheEngineCallsWide()
    {
        Assert.False(TextClassifier.IsWide(0x41));
        Assert.False(TextClassifier.IsWide(0xC3A9));       // é
        Assert.True(TextClassifier.IsWide(0xC397));        // ×
        Assert.True(TextClassifier.IsWide(0xC2B1));        // ±
        Assert.True(TextClassifier.IsWide(0xE38182));      // あ
        Assert.False(TextClassifier.IsWide(0xEFBDB1));     // halfwidth ｱ
        Assert.True(TextClassifier.IsWide(0xF09F9880));    // 4-byte
    }

    private static byte[] ClassesOf(string text, Language lang = Language.Ja)
    {
        var r = TextClassifier.Classify(Encoding.UTF8.GetBytes(text), lang);
        return r.Classes[..r.N];
    }

    [Fact]
    public void KinsokuClasses()
    {
        Assert.All(ClassesOf("。」』），．：；》’”"), c => Assert.Equal(0x71, c));
        Assert.All(ClassesOf("「『（‘“"), c => Assert.Equal(0x0E, c));
        Assert.All(ClassesOf("、ぁっゃァッャー・！？‧"), c => Assert.Equal(0x0D, c));
        Assert.All(ClassesOf("あ漢"), c => Assert.Equal(1, c));
        Assert.Equal(new byte[] { 1 }, ClassesOf("　"));       // the ideographic space is an ordinary wide character
    }

    [Fact]
    public void EnglishWordsGetClassesAndLengths()
    {
        var r = TextClassifier.Classify("  The cat, sat."u8, Language.En);
        Assert.Equal(15, r.N);
        // 0 = a word starts here (break allowed before it), 2 = inside a word, 3 = ',' '.'
        Assert.Equal(new byte[] { 2, 0, 0, 2, 2, 2, 0, 2, 2, 3, 2, 0, 2, 2, 3 }, r.Classes[..15]);
        // wordlen[i-1] = length + 1 of the word that starts at element i
        Assert.Equal(new ushort[] { 1, 4, 0, 0, 0, 5, 0, 0, 0, 0, 4, 0, 0, 0, 0 }, r.WordLen[..15]);
    }

    [Fact]
    public void ThePassOneBudgetRubySkipAndCodes()
    {
        Assert.Equal(TextClassifier.ElementBudget, TextClassifier.Classify(Encoding.UTF8.GetBytes(new string('あ', 1300)), Language.Ja).N);
        Assert.Equal(1200, TextClassifier.ElementBudget);
        var ruby = Encoding.UTF8.GetBytes("<漢|かん>字");
        Assert.Equal(7, TextClassifier.Classify(ruby, Language.Ja).N);
        Assert.Equal(4, TextClassifier.Classify(ruby, Language.Ja, 0x2000).N);       // flag 0x2000 skips the reading
        Assert.Equal(10, TextClassifier.Classify("@s12,AB@x123C@q(zz)D"u8, Language.Ja).N);   // pass 1 knows no '@s' parameter
    }

    // ---- message window ----

    [Fact]
    public void TheWindowTableAndBubbles()
    {
        Assert.Equal(56, MessageWindow.WindowCount);
        var w0 = MessageWindow.WindowFor(0)!.Value;
        Assert.Equal((11, 0.0, 0.0, 1.0), (w0.DispChild, w0.XPx, w0.YPx, w0.Alpha));
        Assert.Equal(13, MessageWindow.WindowFor(2)!.Value.DispChild);
        Assert.Equal(22, MessageWindow.WindowFor(3)!.Value.DispChild);
        var w5 = MessageWindow.WindowFor(5, opacity: 128)!.Value;
        Assert.True(w5.Bubble);
        Assert.Equal(24, w5.DispChild);
        Assert.Equal((492.0, 589.5), (w5.XPx, w5.YPx));
        Assert.InRange(Math.Abs(w5.Alpha - 0.784314), 0, 1e-6);
        Assert.InRange(Math.Abs(MessageWindow.WindowFor(33)!.Value.ScaleX - 1.12), 0, 1e-9);
        Assert.Null(MessageWindow.WindowFor(56));
        Assert.Equal((213.5, 244.5), MessageWindow.BubbleTextOrigin(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => MessageWindow.DispChildForSlot(21));
    }

    [Fact]
    public void TheMainBoxParameters()
    {
        Assert.Equal(1 << 20, MessageWindow.MainBoxParams(Language.Ja, 5).Flags);
        Assert.Equal(30.0, MessageWindow.MainBoxParams(Language.En).X);
        Assert.Equal(49.0, MessageWindow.MainBoxParams(Language.Ja).Y);
        Assert.Equal(0xDC6D, MessageWindow.MainBoxParams(Language.Ja, read: true, readColorMode: 1).Color);
        Assert.Equal(0xFFFF, MessageWindow.MainBoxParams(Language.Ja, 5, read: true, readColorMode: 1).Color);
        Assert.Equal((double)(30f / 24f * (float)0.74), MessageWindow.MainBoxParams(Language.Ja, f31: 30).Sx);
    }

    private static TextBlock FakeBlock(int lines, float height = 17.02f) => new()
    {
        N = 1, WidthArg = 474, Height = 220, Leading = 9, BoxWidth = 474, Sx = 0.24666667f, Sy = 0.24666667f, NLines = lines,
        LastGlyphLine = lines - 1, LineGlyphs = Enumerable.Repeat((ushort)1, 32).ToArray(),
        LineWidth = Enumerable.Repeat(100f, 32).ToArray(), LineHeight = Enumerable.Repeat(height, 32).ToArray(),
        Records = [], Classes = [], WordLen = [], EndCtr = 2,
    };

    [Fact]
    public void MessagesOfOnePageAreStacked()
    {
        var blocks = new[] { FakeBlock(2), FakeBlock(1), FakeBlock(2) };
        Assert.Equal(26, MessageWindow.LastLineOffset(blocks[0]));
        Assert.Equal(0, MessageWindow.LastLineOffset(blocks[1]));
        Assert.Equal(new[] { (30.0, 35.0), (30.0, 87.0), (30.0, 113.0) }, MessageWindow.PagePositions(blocks, Language.En));
        Assert.Equal(new[] { 49.0, 127.0, 153.0 }, MessageWindow.PagePositions(blocks, Language.Ja, [0, 1, 0]).Select(p => p.Y));
        // values at or above 0x21 mean C - 64 and clamp the position
        Assert.Equal(49.823528f, (float)MessageWindow.PagePositions([FakeBlock(1)], Language.Ja, [0x21 + 5], null, (87.0, 20.0))[0].Y);
    }

    [Fact]
    public void LineOffsetsFollowTheDrawFlags()
    {
        var b = FakeBlock(2);
        (float[] xs, float[] ys, float total) = BlockDraw.LineOffsets(b, 87f, 49f);
        Assert.Equal(new[] { 87f, 87f }, xs);
        Assert.Equal(49f, ys[0]);
        Assert.Equal(BitConverter.SingleToUInt32Bits(75.02000427246094f), BitConverter.SingleToUInt32Bits(ys[1]));
        Assert.Equal(52.04f, total, 0.01f);
        Assert.Equal(10f + (474f - 100f) / 2f, BlockDraw.LineOffsets(b, 10f, 0f, BlockDraw.Center).Xs[0]);
        Assert.Equal(10f + 474f - 100f, BlockDraw.LineOffsets(b, 10f, 0f, BlockDraw.Right).Xs[0]);
    }

    [Fact]
    public void FillColoursComeFromTheRgb565Word()
    {
        Assert.Equal((0xF8, 0xF8, 0xF8), BlockDraw.Rgb565Bytes(0xFFFF));
        Assert.Equal((0xF8, 0x00, 0x00), BlockDraw.Rgb565Bytes(0xF800));
        Assert.Equal((0xD8, 0x88, 0x68), BlockDraw.Rgb565Bytes(0xDC6D));
    }

    [Fact]
    public void TheRevealWindowSelectsAndFadesGlyphs()
    {
        // "ABCDE": counters 1..5; head 3.5 hides 4 and 5, tail 2.0 shows 1 and 2 fully and fades 3.
        var b = TextLayout.Layout("ABCDE"u8, Language.En, Fonts, new LayoutParams());
        var fills = BlockDraw.BlockQuads(b, 30f, 35f, head: 3.5, tail: 2.0, lang: Language.En).Where(q => q.Kind == QuadKind.Fill).ToArray();
        Assert.Equal(3, fills.Length);
        Assert.Equal(new[] { 1.0, 1.0, Math.Round((3.5 - 3) / 1.5, 4) }, fills.Select(f => Math.Round(f.Alpha, 4)));
        var outlines = BlockDraw.BlockQuads(b, 30f, 35f, head: 3.5, tail: 2.0, lang: Language.En).Where(q => q.Kind == QuadKind.Outline).ToArray();
        Assert.Equal(fills[0].V0 + 512, outlines[0].V0);
        Assert.DoesNotContain(BlockDraw.BlockQuads(b, 0f, 0f, BlockDraw.Always, lang: Language.En), q => q.Kind == QuadKind.Outline);
        Assert.Equal(5, BlockDraw.BlockQuads(b, 0f, 0f, 0x30, lang: Language.En).Count(q => q.Kind == QuadKind.Shadow));
    }
}
