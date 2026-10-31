using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>The story's yes / no prompt (package K13) as the UI draws it: the eboot's backdrop, window, message, two buttons, and the guide line.</summary>
public sealed class StoryPromptTests
{
    private static (UiRoot Root, PreviewServices Services) Make(UiStoryPrompt? prompt)
    {
        var services = new PreviewServices { StoryPrompt = prompt };
        return (UiTestKit.Root(services), services);
    }

    private static DrawCmd[] Tiles(UiRoot root, int child) =>
        [.. UiTestKit.Draw(root).Commands.ToArray().Where(c => c.Texture.Kind == TextureKind.UiTile && c.Texture.A == (int)UiSet.DispParts && c.Texture.C == child)];

    private static DrawCmd[] Glyphs(UiRoot root) => [.. UiTestKit.Draw(root).Commands.ToArray().Where(c => c.Texture.Kind == TextureKind.GlyphPage)];

    [Fact]
    public void ThePromptDrawsTheBackdropTheWindowTheMessageAndBothButtons()
    {
        (UiRoot root, _) = Make(new UiStoryPrompt(216, -1, 1f, 1f));
        DrawCmd[] backdrop = Tiles(root, 21);
        DrawCmd bg = Assert.Single(backdrop);
        Assert.Equal((0f, 0f, 1920f, 1080f), (bg.V0.X, bg.V0.Y, bg.V1.X, bg.V2.Y));
        Assert.Equal(255u, bg.V0.Rgba >> 24);

        // the window is 824 x 416 centred at (321, 224) units = (963, 540) px
        DrawCmd win = Assert.Single(Tiles(root, 20));
        Assert.Equal(824f, win.V1.X - win.V0.X, 2);
        Assert.Equal(416f, win.V2.Y - win.V0.Y, 2);
        Assert.Equal(963f, (win.V0.X + win.V1.X) / 2f, 2);
        Assert.Equal(540f, (win.V0.Y + win.V2.Y) / 2f, 2);

        // No at x 241 units, Yes at x 327, both 216 x 56 at y 238.52 units; nothing selected: both in the plain cell (224, y)
        DrawCmd[] buttons = Tiles(root, 19);
        Assert.Equal(2, buttons.Length);
        DrawCmd no = buttons.Single(b => b.V0.X < 900f);
        DrawCmd yes = buttons.Single(b => b.V0.X > 900f);
        Assert.Equal(241f * 3f, no.V0.X, 2);
        Assert.Equal(327f * 3f, yes.V0.X, 2);
        Assert.Equal(238.51852f * (1080f / 448f), no.V0.Y, 1);
        Assert.Equal((216f, 56f), (no.V1.X - no.V0.X, no.V2.Y - no.V0.Y));
        Assert.Equal(224f / 4000f, no.V0.U, 5);
        Assert.Equal(67f / 4000f, no.V0.V, 5);                          // the No cell is 67 rows below the Yes cell
        Assert.Equal(224f / 4000f, yes.V0.U, 5);
        Assert.Equal(0f, yes.V0.V, 5);

        // the message is glyph quads in the dialog; the guide line (Confirm, Move Cursor) is below
        Assert.NotEmpty(Glyphs(root));
    }

    [Fact]
    public void TheSelectedButtonTakesItsGlowCell()
    {
        (UiRoot root, PreviewServices services) = Make(new UiStoryPrompt(216, 0, 1f, 1f));
        DrawCmd[] buttons = Tiles(root, 19);
        DrawCmd no = buttons.Single(b => b.V0.X < 900f);
        DrawCmd yes = buttons.Single(b => b.V0.X > 900f);
        Assert.Equal(0f, no.V0.U, 5);
        Assert.Equal(202f / 4000f, no.V0.V, 5);                         // (0, 67 + 135)
        Assert.Equal(224f / 4000f, yes.V0.U, 5);

        services.StoryPrompt = new UiStoryPrompt(216, 1, 1f, 1f);
        buttons = Tiles(root, 19);
        no = buttons.Single(b => b.V0.X < 900f);
        yes = buttons.Single(b => b.V0.X > 900f);
        Assert.Equal(224f / 4000f, no.V0.U, 5);
        Assert.Equal(0f, yes.V0.U, 5);
        Assert.Equal(135f / 4000f, yes.V0.V, 5);
    }

    [Fact]
    public void TheWindowPopsWithTheScaleAndEveryPieceFadesWithTheAlpha()
    {
        (UiRoot root, _) = Make(new UiStoryPrompt(216, -1, 0.5f, 1.025f));
        DrawCmd win = Assert.Single(Tiles(root, 20));
        Assert.Equal(824f * 1.025f, win.V1.X - win.V0.X, 1);
        Assert.Equal(963f, (win.V0.X + win.V1.X) / 2f, 1);
        Assert.InRange((int)(win.V0.Rgba >> 24), 127, 129);
        Assert.InRange((int)(Assert.Single(Tiles(root, 21)).V0.Rgba >> 24), 127, 129);
        Assert.All(Tiles(root, 19), b => Assert.InRange((int)(b.V0.Rgba >> 24), 127, 129));
    }

    [Fact]
    public void NothingIsDrawnWithoutAPromptOrAtAlphaZeroOrOverAScreen()
    {
        (UiRoot none, _) = Make(null);
        Assert.Empty(UiTestKit.Draw(none).Commands.ToArray());

        (UiRoot gone, _) = Make(new UiStoryPrompt(216, 0, 0f, 1f));
        Assert.Empty(UiTestKit.Draw(gone).Commands.ToArray());

        (UiRoot covered, _) = Make(new UiStoryPrompt(216, 0, 1f, 1f));
        Assert.NotEmpty(Tiles(covered, 20));
        covered.Push(new SystemMenuScreen());
        Assert.Empty(Tiles(covered, 19));
        Assert.Empty(Tiles(covered, 20));
        Assert.Empty(Tiles(covered, 21));
    }

    [Fact]
    public void TheGuideLineNamesConfirmAndMoveCursorAndItsConfirmWordIsClickable()
    {
        (UiRoot root, _) = Make(new UiStoryPrompt(216, -1, 1f, 1f));
        UiTestKit.Draw(root);
        GuideHit confirm = Assert.Single(root.Painter.GuideHits, h => h.Action == GuideAction.Confirm);
        Assert.Contains(root.Painter.GuideHits, h => h.Word == 2);                        // Move Cursor
        Assert.DoesNotContain(root.Painter.GuideHits, h => h.Action == GuideAction.Back);   // the prompt has no Back

        float x = confirm.Rect.X + (confirm.Rect.W / 2f);
        float y = confirm.Rect.Y + 5f;
        PointerState onWord = new((short)(x / 3f), (short)(y / 2.4107f), true, PointerButtons.Left, PointerButtons.Left, 0, x, y);
        Assert.True(root.StoryPromptConfirmClicked(onWord));
        Assert.False(root.StoryPromptConfirmClicked(onWord with { Pressed = PointerButtons.None }));
        Assert.False(root.StoryPromptConfirmClicked(onWord with { CanvasX = 1500f, X = 500 }));
        Assert.False(root.StoryPromptConfirmClicked(onWord with { Active = false }));
    }

    [Fact]
    public void AGuideClickIsIgnoredWhenNoPromptShows()
    {
        (UiRoot root, PreviewServices services) = Make(new UiStoryPrompt(216, -1, 1f, 1f));
        UiTestKit.Draw(root);
        GuideHit confirm = Assert.Single(root.Painter.GuideHits, h => h.Action == GuideAction.Confirm);
        float x = confirm.Rect.X + 4f;
        float y = confirm.Rect.Y + 5f;
        var click = new PointerState((short)(x / 3f), (short)(y / 2.4107f), true, PointerButtons.Left, PointerButtons.Left, 0, x, y);
        services.StoryPrompt = null;
        Assert.False(root.StoryPromptConfirmClicked(click));
    }

    [Fact]
    public void ThePromptLayoutNumbersAreTheEbootsAndTheMessageSitsInsideTheWindow()
    {
        // the window is 824 x 416 px at (963, 540); the message block starts at 224 - 42.31 = 181.7 units = 438 px and its 474-unit width centres on x 320.3
        Assert.Equal(181.69f, PromptLayout.CenterY + PromptLayout.MessageDy, 2);
        Assert.Equal(320.33f, PromptLayout.MessageX + (PromptLayout.MessageWidth / 2f), 1);
        (UiRoot root, _) = Make(new UiStoryPrompt(216, -1, 1f, 1f));
        DrawCmd[] glyphs = Glyphs(root);
        Assert.NotEmpty(glyphs);
        float minX = glyphs.Min(g => g.V0.X);
        float maxX = glyphs.Max(g => g.V1.X);
        float minY = glyphs.Min(g => g.V0.Y);
        float maxY = glyphs.Max(g => g.V2.Y);
        Assert.InRange(minX, 963f - 412f, 963f + 412f);
        Assert.InRange(maxX, 963f - 412f, 963f + 412f);
        Assert.InRange(minY, 540f - 208f, 540f + 208f);
        Assert.InRange(maxY, 540f - 208f, 540f + 208f);
        Assert.True(maxY < 238.5f * (1080f / 448f));                    // above the buttons
    }
}
