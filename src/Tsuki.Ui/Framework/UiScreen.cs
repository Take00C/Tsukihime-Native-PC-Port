namespace Tsuki.Ui;

/// <summary>
/// A menu screen: a state machine that steps once per 60 Hz frame and draws itself through a <see cref="UiPainter"/> (ARCHITECTURE 9.1). Screens stack on
/// the <see cref="UiRoot"/>; the top one gets the input. The base class keeps the content fade every PS4 screen has (knife <c>fade_speeds</c>: per-frame
/// steps in and out, the screen leaves when the fade-out ends).
/// </summary>
public abstract class UiScreen
{
    /// <summary>The root that owns the screen (set when the screen is pushed).</summary>
    public UiRoot Root { get; internal set; } = null!;

    /// <summary>The shared services, assets and drawing helpers.</summary>
    protected UiContext Ctx => Root.Context;

    /// <summary>The content alpha 0..1 (rises while opening, falls while closing).</summary>
    public float Fade { get; protected set; } = 1f;

    /// <summary>True once <see cref="Close"/> was called.</summary>
    public bool Closing { get; private set; }

    /// <summary>True when the screen has finished closing and the root should remove it.</summary>
    public virtual bool Finished => Closing && Fade <= 0f;

    /// <summary>True when the screen draws the whole frame, so the screens below it are not drawn.</summary>
    public virtual bool Opaque => false;

    /// <summary>The fade-in step per frame (1 = instant).</summary>
    protected virtual float FadeInStep => 1f;

    /// <summary>The fade-out step per frame (1 = instant).</summary>
    protected virtual float FadeOutStep => 1f;

    /// <summary>The screen starts at alpha 0 and fades in (set by screens with a fade-in step below 1).</summary>
    protected void StartHidden() => Fade = 0f;

    /// <summary>Called once when the screen is pushed.</summary>
    public virtual void OnOpened()
    {
    }

    /// <summary>Called once when the screen has been removed.</summary>
    public virtual void OnClosed()
    {
    }

    /// <summary>Starts leaving: the content fades out, then <see cref="Finished"/> becomes true.</summary>
    public virtual void Close()
    {
        if (Closing)
        {
            return;
        }

        Closing = true;
        if (FadeOutStep >= 1f)
        {
            Fade = 0f;
        }
    }

    /// <summary>One frame of the content fade; the root calls it before <see cref="Step"/>.</summary>
    internal void StepFade()
    {
        if (Closing)
        {
            Fade = MathF.Max(0f, Fade - FadeOutStep);
        }
        else if (Fade < 1f)
        {
            Fade = MathF.Min(1f, Fade + FadeInStep);
        }
    }

    /// <summary>One frame with the input; only the top screen gets this.</summary>
    public abstract void Step(in UiInput input);

    /// <summary>One frame for a screen that is covered by another (animations go on, input does not).</summary>
    public virtual void StepBackground()
    {
    }

    /// <summary>Appends the screen's quads.</summary>
    public abstract void Draw(UiPainter painter);
}
