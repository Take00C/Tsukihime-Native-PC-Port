using Tsuki.Engine.Contracts;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Data.Ui;

/// <summary>
/// Package K20 on the real sheets and glyph pages: the quit question's buttons read はい / いいえ in Japanese (glyphs of the Japanese pages inside the two button cells,
/// the sheet's underline under the selected one), while English and the two Chinese draw the sheet's own cells, whose letters are in those languages.
/// </summary>
public sealed class DialogButtonsRealDataTests
{
    private static readonly float ButtonTop = 249.01f * UiMetrics.UnitY;

    private static UiRoot OpenQuitQuestion(Language language)
    {
        var services = new PreviewServices(language);
        var root = new UiRoot(SharedSource.Source, services, null);
        root.Context.TitleIntroShown = true;
        root.Push(new TitleScreen());
        for (int i = 0; i < 200; i++)
        {
            root.Step(new InputFrame { Frame = root.Context.Frame + 1 });
        }

        root.Step(new InputFrame { Frame = root.Context.Frame + 1, Pressed = PadButtons.Cancel });
        for (int i = 0; i < 20; i++)
        {
            root.Step(new InputFrame { Frame = root.Context.Frame + 1 });
        }

        Assert.IsType<ConfirmDialog>(root.Stack[^1]);
        return root;
    }

    private static DrawCmd[] InCell(IEnumerable<DrawCmd> cmds, float x) =>
        [.. cmds.Where(c => c.V0.X >= x - 1f && c.V3.X <= x + UiDialogButtons.CellWidth + 1f && c.V0.Y >= ButtonTop - 1f && c.V3.Y <= ButtonTop + UiDialogButtons.CellHeight + 1f)];

    [GameDataFact]
    public void InJapaneseBothButtonsAreGlyphsOfTheJapanesePagesInsideTheirCellsAndTheSelectedOneKeepsTheUnderline()
    {
        UiRoot root = OpenQuitQuestion(Language.Ja);
        var list = new DisplayList(4096);
        root.Draw(list);
        DrawCmd[] cmds = [.. list.Commands];
        foreach ((float x, bool selected) in new[] { (756f, true), (990f, false) })                  // No (left, the cursor's home) and Yes (right)
        {
            DrawCmd[] cell = InCell(cmds, x);
            DrawCmd[] glyphs = [.. cell.Where(c => c.Texture.Kind == TextureKind.GlyphPage)];
            Assert.NotEmpty(glyphs);
            Assert.All(glyphs, g => Assert.Equal((int)Language.Ja, g.Texture.A));
            Assert.Contains(glyphs, g => (g.V0.Rgba >> 24) > 250);                                    // the letters are opaque; the rest is glow
            float inkLeft = glyphs.Min(g => g.V0.X);
            float inkRight = glyphs.Max(g => g.V3.X);
            float inkTop = glyphs.Where(g => (g.V0.Rgba >> 24) > 250).Min(g => g.V0.Y);
            float inkBottom = glyphs.Where(g => (g.V0.Rgba >> 24) > 250).Max(g => g.V3.Y);
            Assert.InRange(inkRight - inkLeft, 30f, UiDialogButtons.CellWidth - 20f);
            Assert.InRange(inkBottom - inkTop, 20f, 40f);                                             // about the sheet's 28 px letters
            Assert.InRange((inkLeft + inkRight) / 2f, x + 90f, x + 126f);                             // centred in the cell like the sheet's letters

            DrawCmd[] sheet = [.. cell.Where(c => c.Texture.Kind == TextureKind.UiTile && (UiSet)c.Texture.A == UiSheets.DialogButtons.Set)];
            if (selected)
            {
                Assert.NotEmpty(sheet);
                Assert.All(sheet, s => Assert.True(s.V0.Y >= ButtonTop + UiDialogButtons.UnderlineRow - 1f, "only the underline rows of the cursor cell come from the sheet"));
            }
            else
            {
                Assert.Empty(sheet);
            }
        }

        // Left moves the cursor to Yes: the underline follows it, and No loses its sheet rows
        root.Step(new InputFrame { Frame = root.Context.Frame + 1, Pressed = PadButtons.Left });
        root.Step(new InputFrame { Frame = root.Context.Frame + 1 });
        var moved = new DisplayList(4096);
        root.Draw(moved);
        DrawCmd[] after = [.. moved.Commands];
        Assert.Contains(InCell(after, 990f), c => c.Texture.Kind == TextureKind.UiTile && (UiSet)c.Texture.A == UiSheets.DialogButtons.Set);
        Assert.DoesNotContain(InCell(after, 756f), c => c.Texture.Kind == TextureKind.UiTile && (UiSet)c.Texture.A == UiSheets.DialogButtons.Set);
    }

    [GameDataTheory]
    [InlineData(Language.En)]
    [InlineData(Language.Zc)]
    [InlineData(Language.Zt)]
    public void InTheLanguagesTheSheetSpeaksTheButtonsAreTheSheetsCellsWithoutGlyphs(Language language)
    {
        UiRoot root = OpenQuitQuestion(language);
        var list = new DisplayList(4096);
        root.Draw(list);
        DrawCmd[] cmds = [.. list.Commands];
        foreach (float x in new[] { 756f, 990f })
        {
            DrawCmd[] cell = InCell(cmds, x);
            Assert.DoesNotContain(cell, c => c.Texture.Kind == TextureKind.GlyphPage);
            DrawCmd[] sheet = [.. cell.Where(c => c.Texture.Kind == TextureKind.UiTile && (UiSet)c.Texture.A == UiSheets.DialogButtons.Set)];
            Assert.NotEmpty(sheet);
            Assert.Equal(ButtonTop, sheet.Min(s => s.V0.Y), 0.5f);
            Assert.Equal(ButtonTop + UiDialogButtons.CellHeight, sheet.Max(s => s.V3.Y), 0.5f);
        }
    }
}
