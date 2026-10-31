namespace Tsuki.Engine.Game;

/// <summary>
/// Where the story's two-button prompts (<c>_SELS</c>, <c>_SELA</c>) put their pieces, in the 640 x 448 units of the PS4's HUD canvas (package K13). Every number is an
/// operand of the eboot's draw functions 0xA7CD0 (mode 2) / 0xA8250 (mode 3) and of the input functions 0xA7AA0 / 0xA7FD0, read with <c>tools/eboot/disasm.py</c>
/// (docs/status/knife/K13.md); knife's <c>sub_85900</c> has the same values.
/// </summary>
public static class PromptLayout
{
    /// <summary>The x of button 0 (No), the left one (widget 0xD5; float 0x2169E0).</summary>
    public const float NoX = 241f;

    /// <summary>The x of button 1 (Yes), the right one (widget 0xD6; float 0x2169E8).</summary>
    public const float YesX = 327f;

    /// <summary>The y of both buttons' top edge (float 0x2169E4).</summary>
    public const float ButtonY = 238.51852f;

    /// <summary>The dialog window's centre x (float 0x2169C8; one unit right of the screen centre, as the eboot has it).</summary>
    public const float WindowCenterX = 321f;

    /// <summary>The centre y of the window and of the backdrop (float 0x2169BC).</summary>
    public const float CenterY = 224f;

    /// <summary>The message block's left edge (float 0x2169D8); the block is 474 units wide and centres its lines.</summary>
    public const float MessageX = 83.333336f;

    /// <summary>The message block's top is <c>CenterY + MessageDy * scale</c> (float 0x2169CC).</summary>
    public const float MessageDy = -42.31111f;

    /// <summary>The layout width of the message (474, the <c>Text_Layout</c> width argument 0x1DA).</summary>
    public const int MessageWidth = 474;

    /// <summary>The layout height of the message (220, argument 0xDC).</summary>
    public const int MessageHeight = 220;

    /// <summary>Glyph scale x of the message, times the pop scale (float 0x2169D0).</summary>
    public const double MessageSx = 0.37;

    /// <summary>Glyph scale y of the message, times the pop scale (float 0x2169D4; 0.37 x 448 / 360).</summary>
    public const double MessageSy = 0.46044448;

    /// <summary>The per-frame step of the fade in and out (floats 0x216884 / 0x216888 / 0x216890 / 0x216894).</summary>
    public const float FadeStep = 0.1f;

    /// <summary>The window's pop: it is 1 + 0.05 (1 - alpha) while opening (phase 0) and 1 - 0.05 (1 - alpha) while closing (phase 3), else 1 (floats 0x216954 / 0x216958).</summary>
    public const float Pop = 0.05f;

    // the hit box of a button widget (record 0x8731F0 + 0x30 k: shorts -10, -10, 60, 45 at +0x0C), added to the widget's truncated position; the pointer test of
    // 0xD05B0 takes the first widget whose box holds the pointer, ends included

    /// <summary>Hit box left edge relative to the button's x.</summary>
    public const int HitX0 = -10;

    /// <summary>Hit box top edge relative to the button's y.</summary>
    public const int HitY0 = -10;

    /// <summary>Hit box right edge (inclusive) relative to the button's x.</summary>
    public const int HitX1 = 60;

    /// <summary>Hit box bottom edge (inclusive) relative to the button's y.</summary>
    public const int HitY1 = 45;

    /// <summary>The button (0 No, 1 Yes) whose hit box holds the pointer unit (<paramref name="ux"/>, <paramref name="uy"/>), or -1.</summary>
    public static int ButtonAt(int ux, int uy)
    {
        int y = (int)ButtonY;
        if (uy < y + HitY0 || uy > y + HitY1)
        {
            return -1;
        }

        if (ux >= (int)NoX + HitX0 && ux <= (int)NoX + HitX1)
        {
            return 0;
        }

        if (ux >= (int)YesX + HitX0 && ux <= (int)YesX + HitX1)
        {
            return 1;
        }

        return -1;
    }
}
