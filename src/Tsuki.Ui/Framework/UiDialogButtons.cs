// The Yes / No buttons of the two-button dialogs (package K20). The PS4 draws them from DISP_PARTS child 19 of the UI language (knife dialog_btn_ja: a normal cell and a
// cursor cell per button, 216 x 56); the Chinese sheets say 是 / 否, but the Japanese sheet is the English one (byte-identical to disp_parts_en's: the PS4's Japanese UI keeps
// its menu words in English, as it does for Save / Load / Config). The user wants the quit question in Japanese, so for a language whose sheet is not in that language
// the words come from PortText in the game's glyph font, drawn where the sheet's letters sit and in their colours, over the sheet's own cursor underline.
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>Draws one dialog button cell the way the sheet's cell looks, translated where the sheet is not.</summary>
public static class UiDialogButtons
{
    /// <summary>The width of a button cell on the sheet and on the screen.</summary>
    public const float CellWidth = 216f;

    /// <summary>The height of a button cell.</summary>
    public const float CellHeight = 56f;

    /// <summary>The row of the 1 px cursor underline in a cursor cell (measured on the sheet: columns 12..203, colour (0, 255, 240)).</summary>
    public const int UnderlineRow = 42;

    /// <summary>Where the translated word's line box starts in the cell: the sheet's letters are rows 14..42 of the cell (a 28 px tall "Yes" standing on the underline), and the kana's own box has 4 px of air above the ink.</summary>
    public const float LetterTop = 10f;

    /// <summary>The height of the sheet's letters.</summary>
    public const float LetterHeight = 28f;

    private static readonly (int R, int G, int B) White = (255, 255, 255);
    private static readonly (int R, int G, int B) WhiteGlow = (192, 238, 254);
    private static readonly (int R, int G, int B) Cyan = (0, 255, 240);
    private static readonly (int R, int G, int B) CyanGlow = (2, 221, 255);

    /// <summary>True when the UI language's sheet carries the words in that language (English and the two Chinese); false for Japanese, whose sheet is the English one.</summary>
    public static bool SheetIsLocalized(Language language) => language != Language.Ja;

    /// <summary>The word drawn instead of the sheet's letters, or null when the sheet is used.</summary>
    public static string? Label(Language language, bool yes) =>
        SheetIsLocalized(language) ? null : PortText.Get(yes ? PortString.DialogYes : PortString.DialogNo, language);

    /// <summary>The source cell of a button on the sheet: Yes at row 0, No at row 67; the normal column at x 224, the cursor column (cyan letters over the underline) at x 0, 135 lower.</summary>
    public static RectF SourceCell(bool yes, bool selected)
    {
        float cy = yes ? 0f : 67f;
        return selected ? new RectF(0, cy + 135f, CellWidth, CellHeight) : new RectF(224, cy, CellWidth, CellHeight);
    }

    /// <summary>
    /// Draws the button whose cell's top-left is (<paramref name="x"/>, <paramref name="y"/>) in canvas pixels: the sheet's cell in a language the sheet speaks, else the
    /// cursor underline from the sheet (selected only) and the translated word in the game's glyph font, white, or cyan with a faint glow under the cursor, as the sheet's letters are.
    /// </summary>
    public static void Draw(UiPainter p, UiContext ctx, bool yes, bool selected, float x, float y, float alpha = 1f)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(ctx);
        RectF src = SourceCell(yes, selected);
        string? label = Label(ctx.Language, yes);
        if (label is null)
        {
            p.Sprite(UiSheets.DialogButtons, src, new RectF(x, y, CellWidth, CellHeight), alpha);
            return;
        }

        if (selected)
        {
            // the underline and whatever lies under the letters' rows is kept; the letters themselves are cut away
            var line = new RectF(src.X, src.Y + UnderlineRow, CellWidth, CellHeight - UnderlineRow);
            p.Sprite(UiSheets.DialogButtons, line, new RectF(x, y + UnderlineRow, CellWidth, CellHeight - UnderlineRow), alpha);
        }

        ctx.Text.DrawGlowLabel(
            p, label, LabelStyle(ctx.Language), x + (CellWidth / 2f), y + LetterTop,
            selected ? Cyan : White, selected ? CyanGlow : WhiteGlow, alpha, selected ? 0.014f : 0.02f);
    }

    /// <summary>The glyph scale of the translated word: calibrated on the real Japanese pages so that はい / いいえ are about as tall as the sheet's 28 px "Yes" (K20.md, section 5).</summary>
    public const double LabelScale = 0.54;

    /// <summary>The glyph style of the translated word: square glyphs at <see cref="LabelScale"/>, no extra spacing.</summary>
    public static TextStyle LabelStyle(Language language)
    {
        _ = language;
        return TextStyle.Menu(LabelScale) with { Width = 600, Height = 100, Spacing = 0, Leading = 0 };
    }
}
