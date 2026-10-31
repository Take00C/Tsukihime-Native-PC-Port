// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (Confirm, Popup, DlgOut, draw_dialog_*)
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Ui.Screens;

/// <summary>The confirm dialogs of the menus (knife <c>Confirm</c>; the number is knife's dialog id, whose message the eboot's dialog table gives).</summary>
public enum ConfirmKind
{
    /// <summary>System menu Title during an archive scene (dialog 8, Yes first).</summary>
    Title,

    /// <summary>Overwrite a save (dialog 2, No first).</summary>
    Overwrite,

    /// <summary>Load a save (dialog 0, Yes first).</summary>
    Load,

    /// <summary>Mode panel: previous scene (dialog 11, No first).</summary>
    PrevScene,

    /// <summary>Mode panel: next scene (dialog 10, No first).</summary>
    NextScene,

    /// <summary>Mode panel: quick load (dialog 9, No first; shows the quick save's thumbnail).</summary>
    QuickLoad,

    /// <summary>Config: Recommended Settings (ui_text 160, No first).</summary>
    Defaults,

    /// <summary>Backlog quick jump (dialog 23, No first).</summary>
    BacklogJump,

    /// <summary>Save list: delete the save (dialog 21, No first).</summary>
    Delete,

    /// <summary>Save list: copy the save (dialog 22, No first).</summary>
    Copy,

    /// <summary>System menu FlowChart during a replay picked in the gallery's flowchart: end the replay (dialog 12, Yes first; package K5b).</summary>
    EndReplay,
}

/// <summary>
/// A Yes / No confirm dialog (knife sub_4AE90 / sub_49F00): the screen below darkened to 75 %, the dialog window settling in (scale (1 + (1 - t) 0.2)^2, alpha
/// t), then the message and the two buttons. Any direction toggles the cursor, decide answers after a 15-frame hold, cancel answers No after one frame; the
/// answered dialog fades out over the screen below (shrinking after Yes, growing after No).
/// </summary>
public sealed class ConfirmDialog : UiScreen
{
    private readonly ConfirmKind _kind;
    private readonly int _customMessage = -1;
    private readonly bool _customNo = true;
    private readonly Action<bool> _answer;
    private readonly int _thumbSlot;
    private readonly UiItemList _items = new();
    private float _t;
    private int _hold = -1;
    private bool _yes;
    private bool _acted;

    /// <summary>Creates a dialog of <paramref name="kind"/>; <paramref name="answer"/> is called with the answer once the hold has run out.</summary>
    /// <param name="kind">Which dialog.</param>
    /// <param name="answer">Called with true for Yes.</param>
    /// <param name="thumbnailSlot">The slot whose thumbnail the dialog shows (quick load), or -1.</param>
    public ConfirmDialog(ConfirmKind kind, Action<bool> answer, int thumbnailSlot = -1)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _kind = kind;
        _answer = answer;
        _thumbSlot = thumbnailSlot;
    }

    /// <summary>Creates a dialog with a message of the caller's choice (the <c>ui_text</c> id <paramref name="messageId"/>).</summary>
    /// <param name="messageId">The <c>ui_text</c> id of the message.</param>
    /// <param name="noFirst">The cursor starts on No.</param>
    /// <param name="answer">Called with true for Yes once the hold has run out.</param>
    public ConfirmDialog(int messageId, bool noFirst, Action<bool> answer)
        : this(ConfirmKind.EndReplay, answer)
    {
        _customMessage = messageId;
        _customNo = noFirst;
    }

    /// <summary>True while the dialog still waits for an answer.</summary>
    public bool Waiting => _hold < 0 && !Closing;

    /// <summary>The cursor is on Yes.</summary>
    public bool CursorOnYes => _items.Cursor == 0;

    /// <summary>knife dialog id and whether the cursor starts on No.</summary>
    private (int Dialog, bool No) Info => _customMessage >= 0 ? (-1, _customNo) : _kind switch
    {
        ConfirmKind.Title => (8, false),
        ConfirmKind.Overwrite => (2, true),
        ConfirmKind.Load => (0, false),
        ConfirmKind.PrevScene => (11, true),
        ConfirmKind.NextScene => (10, true),
        ConfirmKind.QuickLoad => (9, true),
        ConfirmKind.BacklogJump => (23, true),
        ConfirmKind.Delete => (21, true),
        ConfirmKind.Copy => (22, true),
        ConfirmKind.EndReplay => (12, false),
        _ => (-1, true),
    };

    /// <summary>The <c>ui_text</c> id of the message.</summary>
    public int MessageId(UiAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (_kind == ConfirmKind.Defaults)
        {
            return UiAssets.Sysmes(160);
        }

        if (_customMessage >= 0)
        {
            return _customMessage;
        }

        // the PS4 table has two more dialogs after knife's dialog 20 (docs/research/knife_tables.md 5.1)
        int d = Info.Dialog;
        int index = d >= 21 ? d + 2 : d;
        IReadOnlyList<int> ids = assets.Tables.DialogMessages;
        return index < ids.Count ? ids[index] : -1;
    }

    private bool Thumbnail => _kind == ConfirmKind.QuickLoad;

    /// <inheritdoc/>
    public override void OnOpened()
    {
        float y = Thumbnail ? 261.46f : 249.01f;
        // Yes (object 0) at x 330 and No (object 1) at x 252; touch rect (0, -2)-(72, 24)
        _items.Add(ObjectRect(330f, y), 0);
        _items.Add(ObjectRect(252f, y), 1);
        _items.Cursor = Info.No ? 1 : 0;
        Ctx.Sound(UiSound.Open);
    }

    private static RectF ObjectRect(float x, float y) => new(
        MathF.Truncate(x) * UiMetrics.UnitX, (MathF.Truncate(y) - 2f) * UiMetrics.UnitY, 73f * UiMetrics.UnitX, 27f * UiMetrics.UnitY);

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        if (_hold >= 0)
        {
            if (_hold > 0)
            {
                _hold--;
            }
            else
            {
                if (!_acted)
                {
                    _acted = true;
                    _answer(_yes);
                }

                FadeOut();
            }

            return;
        }

        _t = MathF.Min(1f, _t + 0.07f);
        if (_t < 1f)
        {
            return;
        }

        if (input.Cancel)
        {
            Ctx.Sound(UiSound.Buzzer);
            _yes = false;
            _hold = 1;
            return;
        }

        if (input.Up || input.Down || input.Left || input.Right)
        {
            _items.Cursor = _items.Cursor == 1 ? 0 : 1;
            Ctx.Sound(UiSound.Cursor);
        }

        if (_items.Hover(input))
        {
            Ctx.Sound(UiSound.Cursor);
        }

        int hit = _items.Clicked(input);
        if (hit >= 0 || (input.Decide && !input.Click))
        {
            if (hit >= 0)
            {
                _items.Cursor = hit;
            }

            _yes = _items.Cursor == 0;
            Ctx.Sound(UiSound.Decide);
            _hold = 15;
        }
    }

    /// <summary>
    /// An answered dialog goes on fading out under the popup its answer opened (Saving..., Loading..., the delete's Deleting...) and leaves the stack once invisible:
    /// knife's <c>DlgOut</c> timer runs whatever screen is on top, while the root steps only the top screen with input (package R1: the dialog's window and its 75 %
    /// backdrop stayed under the popup until the popup closed, five seconds for a delete).
    /// </summary>
    public override void StepBackground()
    {
        if (_acted)
        {
            FadeOut();
        }
    }

    private void FadeOut()
    {
        _t -= 0.07f;
        if (_t <= 0f)
        {
            Close();
        }
    }

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        float t = _t;
        p.Sprite(UiSheets.DialogBackdrop, UiMetrics.Screen, t * 0.75f);
        float d = (1f - t) * 0.2f;
        float k = _acted && _yes ? 1f - d : 1f + d;
        UiWidgets.DialogWindow(p, t, k * k);
        if (_t < 1f)
        {
            return;
        }

        string msg = Ctx.Message(MessageId(Ctx.Assets));
        DialogText(p, Ctx, msg, Thumbnail ? 160f : 184f, 0f);
        if (Thumbnail && _thumbSlot >= 0)
        {
            DrawThumbnail(p, Ctx.Services.Thumbnail(_thumbSlot), _thumbSlot);
        }

        float by = Thumbnail ? 261.46f : 249.01f;
        for (int i = 0; i < 2; i++)
        {
            bool yes = i == 0;
            // the sheet's cell, or its words translated where the sheet is not in the UI language (package K20: the Japanese sheet says Yes / No)
            UiDialogButtons.Draw(p, Ctx, yes, _items.Cursor == i, yes ? 990f : 756f, by * UiMetrics.UnitY);
        }
    }

    /// <summary>The width of the dark inner area of the 824 px <c>dialog_win</c> (x 566..1347 of the rendered window, package R1).</summary>
    internal const float DialogInnerWidth = 782f;

    /// <summary>The widest ink a line of a dialog message may have: the inner area less 11 px on each side.</summary>
    internal const float DialogTextWidth = 760f;

    /// <summary>
    /// A dialog message (knife <c>draw_dialog_text</c>, text slot 12 + n): scale 0.6 x 0.7 of the base glyph, lines centred from <paramref name="top"/>
    /// (units of 448), <c>^</c> breaks lines.
    /// </summary>
    internal static void DialogText(UiPainter p, UiContext ctx, string message, float top, float lineSpacing)
    {
        ctx.Text.Draw(p, message, DialogStyle(ctx.Text, message, lineSpacing), 0f, top);
    }

    /// <summary>
    /// The style of a dialog message: 0.6 x 0.7 of the base glyph, lines centred in a 640-unit box, leading <paramref name="lineSpacing"/> + 6. Neither the PS4's call nor
    /// knife fits the text to the window, and at that size the English "Do you want to perform a quick jump?" (837 px of ink), the two-line Recommended Settings and
    /// flowchart rewind messages (844 / 859 px) and every Japanese ending replay prompt (904..913 px) run past the 824 px window: a message whose widest <c>^</c> line
    /// is wider than <see cref="DialogTextWidth"/> takes both glyph scales down by that ratio (the K12 <see cref="ConfigScreen.SampleScale"/> rule), keeping its top and
    /// its centring (package R1). The layout is cached, so the measure costs a pass over the line's quads per frame.
    /// </summary>
    internal static TextStyle DialogStyle(UiText text, string message, float lineSpacing)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(message);
        TextStyle style = TextStyle.Slot(0.6, 0.7) with { Width = 640, Height = 200, Leading = (int)MathF.Round(lineSpacing + 6f), DrawFlags = BlockDraw.Always | BlockDraw.Outline | BlockDraw.Center };
        float widest = 0f;
        foreach (string line in message.Split('^'))
        {
            (float left, float right) = text.InkSpan(line, style);
            widest = MathF.Max(widest, right - left);
        }

        if (widest <= DialogTextWidth)
        {
            return style;
        }

        double k = DialogTextWidth / widest;
        return style with { Sx = style.Sx * k, Sy = style.Sy * k };
    }

    /// <summary>
    /// The top of the quick save's thumbnail frame in units of 448: 212 in English, whose message ("Do you want to perform^a quick load?") takes two lines from 160,
    /// else 200 (knife <c>draw_dialog_thumb</c>; package R1: the port hung it from 200 in every language, over the lower half of the second English line).
    /// </summary>
    internal static float ThumbnailTop(Language language) => language == Language.En ? 212f : 200f;

    private void DrawThumbnail(UiPainter p, UiThumbnail? thumb, int slot)
    {
        if (thumb is null)
        {
            return;
        }

        // sub_4AE90: 100 * 0.82667 * 0.825 x 53 * 1.0859 * 0.825 units hanging from (320, 212 / 200) over a black frame 2 units wider each side
        float w = 100f * 0.82667f * 0.825f;
        float h = 53f * 1.0859f * 0.825f;
        float y = ThumbnailTop(Ctx.Language);
        p.Fill(new RectF((318f - (w / 2f)) * UiMetrics.UnitX, y * UiMetrics.UnitY, (w + 4f) * UiMetrics.UnitX, (h + 4f) * UiMetrics.UnitY), 0f, 0f, 0f, 1f);
        p.Thumbnail(slot, thumb.Version, new RectF((320f - (w / 2f)) * UiMetrics.UnitX, (y + 2f) * UiMetrics.UnitY, w * UiMetrics.UnitX, h * UiMetrics.UnitY));
    }
}

/// <summary>
/// A black message popup (knife sub_4ABE0 / sub_49750): <c>dialog_win</c> with the message centred, fading in and out at 0.07 per frame. It stays <c>frames</c>
/// frames and any button closes it early; a held popup waits for <see cref="Release"/> (the save flow's "Saving..." closes when the write is done).
/// </summary>
public sealed class MessagePopup : UiScreen
{
    private readonly string _message;
    private readonly bool _held;
    private readonly float _minimum;
    private float _frames;
    private float _a;
    private bool _closing;
    private bool _released;
    private float _age;

    /// <summary>Creates a popup.</summary>
    /// <param name="message">The text (<c>^</c> breaks lines).</param>
    /// <param name="frames">Frames to stay once fully shown.</param>
    /// <param name="held">Buttons do not close it; call <see cref="Release"/>.</param>
    /// <param name="minimumFrames">A held popup closes no earlier than this many frames after opening.</param>
    public MessagePopup(string message, float frames = 480f, bool held = false, float minimumFrames = 0f)
    {
        _message = message;
        _frames = frames;
        _held = held;
        _minimum = minimumFrames;
    }

    /// <summary>The text (<c>^</c> breaks lines).</summary>
    public string Message => _message;

    /// <summary>The window alpha 0..1.</summary>
    public float Alpha => _a;

    /// <summary>True while the popup is fully shown.</summary>
    public bool FullyShown => _a >= 1f;

    /// <summary>Lets a held popup go (it still stays for its minimum time).</summary>
    public void Release() => _released = true;

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        _age++;
        if (_held)
        {
            if (_released && _age >= _minimum)
            {
                _closing = true;
            }
        }
        else if (input.Any || input.Decide || input.Cancel)
        {
            _closing = true;
        }

        if (_closing)
        {
            _a -= 0.07f;
            if (_a <= 0f)
            {
                Close();
            }
        }
        else if (_a < 1f)
        {
            _a = MathF.Min(1f, _a + 0.07f);
        }
        else if (!_held)
        {
            _frames--;
            _closing = _frames < 1f;
        }
    }

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        float scale = (float)Math.Pow(1f - ((1f - _a) * 0.2f), 2);
        UiWidgets.DialogWindow(p, _a, scale);
        if (_a >= 1f)
        {
            ConfirmDialog.DialogText(p, Ctx, _message, 214f, 4f);
        }
    }
}
