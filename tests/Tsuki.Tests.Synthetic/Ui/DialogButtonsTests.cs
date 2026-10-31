using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>
/// Package K20: the Yes / No buttons of the dialogs. The English and Chinese sheets are drawn as cells; the Japanese sheet is the English one, so Japanese draws the
/// translated words over the sheet's underline (the glyph quads themselves need the real pages: Tsuki.Tests.Data DialogButtonsRealDataTests).
/// </summary>
public sealed class DialogButtonsTests
{
    private static (UiRoot Root, PreviewServices Services) OpenQuitQuestion(Language language)
    {
        var services = new PreviewServices(language);
        UiRoot root = UiTestKit.Root(services);
        root.Context.TitleIntroShown = true;
        root.Push(new TitleScreen());
        UiTestKit.Run(root, 200);
        UiTestKit.Press(root, PadButtons.Cancel);                 // Esc on the title asks the quit question (package K18)
        UiTestKit.Run(root, 20);
        Assert.IsType<ConfirmDialog>(root.Stack[^1]);
        return (root, services);
    }

    private static DrawCmd[] ButtonSprites(UiRoot root)
    {
        DrawCmd[] cmds = UiTestKit.Draw(root).Commands.ToArray();
        return [.. cmds.Where(c => c.Texture.Kind == TextureKind.UiTile && (UiSet)c.Texture.A == UiSheets.DialogButtons.Set && c.Texture.C == UiSheets.DialogButtons.Child)];
    }

    [Fact]
    public void TheSheetSpeaksEnglishAndChineseButNotJapanese()
    {
        Assert.True(UiDialogButtons.SheetIsLocalized(Language.En));
        Assert.True(UiDialogButtons.SheetIsLocalized(Language.Zc));
        Assert.True(UiDialogButtons.SheetIsLocalized(Language.Zt));
        Assert.False(UiDialogButtons.SheetIsLocalized(Language.Ja));
        Assert.Null(UiDialogButtons.Label(Language.En, true));
        Assert.Null(UiDialogButtons.Label(Language.Zt, false));
        Assert.Equal("はい", UiDialogButtons.Label(Language.Ja, true));
        Assert.Equal("いいえ", UiDialogButtons.Label(Language.Ja, false));
    }

    [Fact]
    public void TheSourceCellsAreKnifesFrames()
    {
        // dialog_btn_ja frames: Yes (224, 0) / glow (0, 135), No (224, 67) / glow (0, 202)
        Assert.Equal(new RectF(224, 0, 216, 56), UiDialogButtons.SourceCell(yes: true, selected: false));
        Assert.Equal(new RectF(0, 135, 216, 56), UiDialogButtons.SourceCell(yes: true, selected: true));
        Assert.Equal(new RectF(224, 67, 216, 56), UiDialogButtons.SourceCell(yes: false, selected: false));
        Assert.Equal(new RectF(0, 202, 216, 56), UiDialogButtons.SourceCell(yes: false, selected: true));
    }

    [Fact]
    public void InEnglishTheQuitQuestionDrawsBothCellsWholeFromTheSheet()
    {
        (UiRoot root, _) = OpenQuitQuestion(Language.En);
        DrawCmd[] cells = ButtonSprites(root);
        Assert.Equal(2, cells.Length);
        Assert.All(cells, c => Assert.Equal(UiDialogButtons.CellHeight, c.V3.Y - c.V0.Y, 0.5f));
        Assert.All(cells, c => Assert.Equal(UiDialogButtons.CellWidth, c.V3.X - c.V0.X, 0.5f));
    }

    [Fact]
    public void TheTranslatedWordIsLaidOutAtAboutTheSheetsLetterHeight()
    {
        // the synthetic glyph tables lay out English only, so the geometry is checked with the English word in the style Japanese uses (the kana themselves: DialogButtonsRealDataTests)
        var services = new PreviewServices(Language.En);
        UiRoot root = UiTestKit.Root(services);
        TextStyle style = UiDialogButtons.LabelStyle(Language.Ja);
        Assert.Equal(UiDialogButtons.LabelScale, style.Sx);
        Assert.Equal(0, style.Spacing);
        float height = root.Context.Text.Height("Yes", style) * UiMetrics.UnitY;
        Assert.InRange(height, 20f, 44f);
        Assert.InRange(UiDialogButtons.LetterTop, 0f, UiDialogButtons.CellHeight - UiDialogButtons.LetterHeight);
        Assert.True(UiDialogButtons.UnderlineRow < UiDialogButtons.CellHeight);
    }
}
