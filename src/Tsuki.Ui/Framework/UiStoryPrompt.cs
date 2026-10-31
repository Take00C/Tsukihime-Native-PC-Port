using Tsuki.Engine.Game;
using Tsuki.Engine.Text;

namespace Tsuki.Ui;

/// <summary>
/// The story's two-button prompt as the game host runs it (package K13): <c>_SELA</c>, the chapter-start "Do you want to save your progress?", and <c>_SELS</c>, the
/// yes / no system prompt. The host owns the state (phase, cursor, the stored answer); the UI only draws it, because the dialog window and the buttons are UI sheets
/// and the message is a <c>ui_text</c> string.
/// </summary>
/// <param name="MessageId">The <c>ui_text</c> id of the message (216, or 1446 + F27).</param>
/// <param name="Cursor">The selected button: 0 No (left), 1 Yes (right), -1 none yet.</param>
/// <param name="Alpha">The dialog alpha 0..1 (it fades in and out at 0.1 per frame).</param>
/// <param name="Scale">The window's pop factor (1.05 .. 1 while opening, 1 .. 0.95 while closing).</param>
public readonly record struct UiStoryPrompt(int MessageId, int Cursor, float Alpha, float Scale = 1f);

/// <summary>
/// Draws <see cref="UiStoryPrompt"/> the way the eboot does (draw functions 0xA7CD0 / 0xA8250, <see cref="PromptLayout"/>): the backdrop sheet over the whole screen at the
/// dialog alpha, the window centred one unit right of the screen centre and scaled by the pop, the message in the 0.37 slot style centred in a 474-unit block whose top
/// moves with the pop, and the No and Yes buttons, the selected one in its glow cell.
/// </summary>
public static class UiStoryPromptView
{
    /// <summary>The guide line of the prompt: Confirm and Move Cursor (the prompt has no Back; the game ignores the cancel button there).</summary>
    public const int Guide = UiWidgets.GuidePrompt;

    /// <summary>Appends the quads of the dialog and its guide line.</summary>
    /// <param name="p">The painter.</param>
    /// <param name="ctx">The shared context (text drawer, services).</param>
    /// <param name="prompt">The prompt's state.</param>
    public static void Draw(UiPainter p, UiContext ctx, in UiStoryPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(ctx);
        float a = Math.Clamp(prompt.Alpha, 0f, 1f);
        if (a <= 0f)
        {
            return;
        }

        float s = prompt.Scale;
        p.Sprite(UiSheets.DialogBackdrop, UiMetrics.Screen, a);
        float w = 824f * s;
        float h = 416f * s;
        p.Sprite(
            UiSheets.DialogWindow,
            new RectF(0, 0, 824, 416),
            new RectF((PromptLayout.WindowCenterX * UiMetrics.UnitX) - (w / 2f), (PromptLayout.CenterY * UiMetrics.UnitY) - (h / 2f), w, h),
            a);

        // Text_Layout(block 20, width 474, height 220, spacing 0, leading 9, white; sx 0.37 s, sy 0.46044 s), drawn with the outline flag and centred lines
        var style = new TextStyle(
            640, 448, PromptLayout.MessageSx * s, PromptLayout.MessageSy * s, PromptLayout.MessageWidth, PromptLayout.MessageHeight, 9, 0, TextStyle.White,
            BlockDraw.Outline | BlockDraw.Center);
        ctx.Text.Draw(p, ctx.Message(prompt.MessageId), style, PromptLayout.MessageX, PromptLayout.CenterY + (PromptLayout.MessageDy * s), a);

        for (int i = 0; i < 2; i++)
        {
            bool yes = i == 1;
            float x = (yes ? PromptLayout.YesX : PromptLayout.NoX) * UiMetrics.UnitX;
            // the sheet's cell, or its words translated where the sheet is not in the UI language (package K20)
            UiDialogButtons.Draw(p, ctx, yes, prompt.Cursor == i, x, PromptLayout.ButtonY * UiMetrics.UnitY, a);
        }

        UiWidgets.Guide(p, Guide, 0f, a, ctx.Services.DecideIsCross);
    }
}
