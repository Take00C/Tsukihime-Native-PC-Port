using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Tests.Data.Text.Layout;

/// <summary>
/// Package K34 (GitHub issue #1, the console's text-break glyphs): runs of the dash characters draw as one connected stroke, as on the PS4
/// (docs/formats/fonts.md 5.6, VA 0x11B80 / 0x11ED0): every dash of a run becomes a piece of page 61 (texture slot 0x5E), the first an opening piece
/// (variant 3 / 6), the middle ones joining pieces (4 / 7), the last a closing piece (5 / 8), so the strokes meet. A lone dash keeps its own glyph.
/// </summary>
public sealed class JoinedDashTests(ITestOutputHelper output)
{
    private static TextBlock Layout(string text, Language lang) =>
        TextLayout.Layout(Encoding.UTF8.GetBytes(text), lang, LayoutData.FontTables, new LayoutParams(Block: 4, Window: 0));

    private static GlyphRecord[] Glyphs(TextBlock block) => block.Records.Where(r => !(r.Index == 0 && r.Flags == 0) && !r.IsRuby).ToArray();

    [GameDataTheory]
    [InlineData("「――――――」", Language.Ja, 0xE28095, 3)]
    [InlineData("――――", Language.Ja, 0xE28095, 3)]
    [InlineData("――――", Language.En, 0xE28095, 3)]
    [InlineData("――――", Language.Zc, 0xE28095, 3)]
    [InlineData("────", Language.Zt, 0xE29480, 6)]
    public void ARunOfDashesIsDrawnWithTheJoinedPiecesOfPageSixtyOne(string text, Language lang, int key, int baseVariant)
    {
        TextBlock block = Layout(text, lang);
        GlyphRecord[] glyphs = Glyphs(block);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int dashes = text.Count(c => c == '―' || c == '─');
        Assert.True(dashes >= 4);
        GlyphRecord[] pieces = glyphs.Where(r => r.Tex == 0x5E).ToArray();
        Assert.Equal(dashes, pieces.Length);
        _ = key;

        // the expected pieces: opening, joining ..., closing, at the rectangles of VA 0x11ED0 for this language's row
        LanguageGlyphs tables = LayoutData.FontTables.For(lang);
        var variants = new List<int> { baseVariant };
        for (int i = 1; i < dashes - 1; i++)
        {
            variants.Add(baseVariant + 1);
        }

        variants.Add(baseVariant + 2);
        for (int i = 0; i < dashes; i++)
        {
            Glyph want = tables.DashGlyph(variants[i], 0, 0, pieces[i].Sx * 3.0);
            Assert.Equal(want.X, pieces[i].X);
            Assert.Equal(want.Y, pieces[i].Y);
            Assert.Equal(want.W, pieces[i].X2 - pieces[i].X + 1);
        }

        // the pieces are laid end to end: each starts where the previous one ends (within the adjustment of the piece)
        for (int i = 1; i < dashes; i++)
        {
            float gap = pieces[i].Dx - (pieces[i - 1].Dx + ((pieces[i - 1].X2 - pieces[i - 1].X) * pieces[i - 1].Sx));
            Assert.InRange(gap, -3f, 3f);
        }

        output.WriteLine($"{lang} {text}: {dashes} pieces, variants {string.Join(",", variants)}, x {string.Join(" ", pieces.Select(p => p.Dx.ToString("F2")))}, atlas x {string.Join(" ", pieces.Select(p => p.X))} y {pieces[0].Y}");
    }

    [GameDataFact]
    public void ALoneDashKeepsItsOwnGlyph()
    {
        TextBlock block = Layout("あ―い", Language.Ja);
        GlyphRecord[] glyphs = Glyphs(block);
        Assert.Equal(3, glyphs.Length);
        Assert.NotEqual(0x5E, glyphs[1].Tex);
    }
}
