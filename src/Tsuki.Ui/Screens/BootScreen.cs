using Tsuki.Engine.Contracts;

namespace Tsuki.Ui.Screens;

/// <summary>
/// The boot screen of the PS4 flow (docs/formats/system_flow.md 3.3 and 3.4 step 5): there is no logo screen; the one notice every start shows is the
/// autosave notice, "This game features an autosave function ...", with the spinning autosave icon and the Saving... caption at the bottom right. It fades in
/// (+0.025 per frame to 1.1: 45 frames), holds 1 to 240 frames (any button ends it), fades out (-0.025 to -0.1: 45 frames), and the title follows. The text is
/// parts.mrg #8 + language, which needs an <see cref="IUiPictureSource"/>; the spinner, caption and glow are the DISP_PARTS pictures that carry the same art.
/// </summary>
public sealed class BootScreen : UiScreen
{
    private float _a;
    private int _hold;
    private int _step;
    private float _angle;
    private float _pulse = 0.3f;
    private float _pulseStep = 0.02f;

    /// <summary>The notice alpha (it runs to 1.1 while fading in and to -0.1 while fading out, as the engine's fade does).</summary>
    public float Alpha => _a;

    /// <summary>True while the notice shows.</summary>
    public bool Showing => _step < 3;

    /// <inheritdoc/>
    public override bool Opaque => true;

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        _angle += 500f / 65536f * 2f * MathF.PI;
        _pulse += _pulseStep;
        if (_pulse >= 1f || _pulse <= 0.3f)
        {
            _pulseStep = -_pulseStep;
            _pulse = Math.Clamp(_pulse, 0.3f, 1f);
        }

        switch (_step)
        {
            case 0:
                _a += 0.025f;
                if (_a >= 1.1f)
                {
                    _step = 1;
                }

                break;
            case 1:
                _hold++;
                if (_hold >= 240 || input.Any)
                {
                    _step = 2;
                }

                break;
            case 2:
                _a -= 0.025f;
                if (_a <= -0.1f)
                {
                    _step = 3;
                    Root.Push(new TitleScreen());
                    Close();
                }

                break;
        }
    }

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        p.Cover(1f);
        float a = Math.Clamp(_a, 0f, 1f);
        int index = 8 + Math.Min((int)Ctx.Language, 4);
        p.Picture(new UiPicture(UiPictureArchive.Parts, index), UiMetrics.Screen, a);
        // spinner (sprite 0x1D) at (895, 497.5) of 960 x 540 = centre (1790, 995), 80 x 80 px, turning 500 / 65536 of a turn per frame
        p.SpriteRotated(UiSheets.DispSpinner, new RectF(0, 0, 160, 160), new RectF(1750f, 955f, 80, 80), _angle, a);
        // "Saving..." caption at (881, 491.5) of 960 x 540, 64 x 28 px, alpha a x pulse (bouncing 0.3 .. 1)
        p.Sprite(UiSheets.DispSaving, new RectF(0, 0, 128, 56), new RectF(1762f, 983f, 64, 28), a * _pulse);
        // the glow (sprite 0x1B) at (875, 477.5), 80 x 80 px, added
        p.Sprite(UiSheets.DispGlow, new RectF(0, 0, 160, 160), new RectF(1750f, 955f, 80, 80), a, UiBlend.PsAdd);
    }
}
