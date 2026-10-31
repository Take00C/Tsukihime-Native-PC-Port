using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>What the UI is doing, for the game host: story with no screen, the title, or an overlay screen over the story.</summary>
public enum UiMode
{
    /// <summary>No screen is open: the story runs (the mode panel and banners may still show).</summary>
    Story,

    /// <summary>The boot sequence or the title screen is on top; the host runs no story.</summary>
    Title,

    /// <summary>A system screen (menu, config, save / load, backlog, dialog) is open over the story or the title.</summary>
    Overlay,
}

/// <summary>
/// The owner of the screens (ARCHITECTURE 9.1): a stack of <see cref="UiScreen"/> state machines stepped once per 60 Hz frame from an
/// <see cref="InputFrame"/>, drawn bottom-up into a <see cref="DisplayList"/>, plus the mode banner. The game host calls <see cref="Step"/> and
/// <see cref="Draw"/> every frame and reads <see cref="Mode"/> and <see cref="BlurScene"/> to decide what else to run and draw.
/// </summary>
public sealed class UiRoot
{
    private readonly List<UiScreen> _stack = [];
    private readonly UiPainter _painter;
    private PointerState _previousPointer;
    private UiBanner? _banner;
    private int _blurFrames;

    /// <summary>Creates a root over a game data source and the game services.</summary>
    /// <param name="data">The game data (UI sheets, fonts and eboot tables).</param>
    /// <param name="services">The game.</param>
    /// <param name="parts">The pictures of the parts archive, when the host has them (the boot notice text).</param>
    public UiRoot(IGameData data, IGameServices services, IUiPictureSource? parts = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(services);
        var assets = new UiAssets(data, services.Language, parts);
        Context = new UiContext(this, services, assets);
        _painter = new UiPainter(assets);
    }

    /// <summary>The shared context.</summary>
    public UiContext Context { get; }

    /// <summary>The painter (for tools that count quads or missing sheets).</summary>
    public UiPainter Painter => _painter;

    /// <summary>The open screens, bottom first.</summary>
    public IReadOnlyList<UiScreen> Stack => _stack;

    /// <summary>The screen that gets the input, or null.</summary>
    public UiScreen? Top => _stack.Count > 0 ? _stack[^1] : null;

    /// <summary>True while a screen is open (the host does not run the story input).</summary>
    public bool Active => _stack.Count > 0;

    /// <summary>The mode banner on show, or null.</summary>
    public UiBanner? Banner => _banner;

    /// <summary>The port's toast on show (package K21), or null.</summary>
    public UiToast? Toast => _toast;

    /// <summary>
    /// True while the toast must not be drawn (package K21): the app sets it from the moment a screenshot is asked for until the frame was captured, so that a toast
    /// still on show is never in the saved picture.
    /// </summary>
    public bool HideToast { get; set; }

    private UiToast? _toast;

    /// <summary>What the UI is doing.</summary>
    public UiMode Mode => _stack.Count == 0 ? UiMode.Story : _stack[0] is Screens.TitleScreen or Screens.BootScreen ? UiMode.Title : UiMode.Overlay;

    /// <summary>True while the system menu or the backlog covers the story: the host blurs the frozen scene behind them and hides the message text.</summary>
    public bool BlurScene => Mode == UiMode.Overlay && _stack.Any(s => s is Screens.SystemMenuScreen or Screens.BacklogScreen);

    /// <summary>
    /// The strength of the blurred scene behind the system menu and the backlog: 0 when they are closed, rising by 1/12 a frame while they are open
    /// (<see cref="UiMenuBlur"/>; package K16).
    /// </summary>
    public float BlurAlpha => Math.Min(1f, _blurFrames / (float)UiMenuBlur.FadeFrames);

    /// <summary>The first screen of the given type in the stack, or null.</summary>
    public T? Find<T>()
        where T : UiScreen => _stack.OfType<T>().FirstOrDefault();

    /// <summary>Pushes a screen on top (it gets the input from the next step).</summary>
    public T Push<T>(T screen)
        where T : UiScreen
    {
        ArgumentNullException.ThrowIfNull(screen);
        screen.Root = this;
        _stack.Add(screen);
        screen.OnOpened();
        return screen;
    }

    /// <summary>Opens the system menu over the story (the host calls this for the system menu button); false when a screen is already open.</summary>
    public bool OpenSystemMenu()
    {
        if (Active)
        {
            return false;
        }

        Push(new Screens.SystemMenuScreen());
        return true;
    }

    /// <summary>Opens the backlog (the backlog button); false when a screen is open or the history has no page to show.</summary>
    public bool OpenBacklog()
    {
        if (Active || Context.Services.Backlog.Count == 0)
        {
            return false;
        }

        Push(new Screens.BacklogScreen());
        return true;
    }

    /// <summary>Opens the mode panel (up in the story); false when a screen is open or the story does not allow it now.</summary>
    public bool OpenModePanel()
    {
        if (Active || !Context.Services.PanelAllowed)
        {
            return false;
        }

        Push(new Screens.ModePanelScreen());
        return true;
    }

    /// <summary>
    /// The host calls this when a replay started from the gallery has ended (<see cref="IUiGalleryServices.ReplayScene"/>, docs/formats/gallery.md 3.2): the title
    /// restarts behind the gallery, which reopens on the mode and the entry the replay started from. False when a screen is open or the data has no gallery.
    /// </summary>
    public bool ReturnToGallery()
    {
        if (Active || Context.Assets.Gallery is null)
        {
            return false;
        }

        // the power-on's intro has been seen: the title does not play it again
        Context.TitleIntroShown = true;
        Screens.TitleScreen title = Push(new Screens.TitleScreen());
        title.ResumeInGallery();
        return true;
    }

    /// <summary>Removes every screen at once (the story takes over).</summary>
    public void Clear()
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            _stack[i].OnClosed();
        }

        _stack.Clear();
    }

    /// <summary>Shows the mode banner (Skip, quick save, auto save, auto).</summary>
    public void ShowBanner(BannerKind kind) => _banner = new UiBanner(kind);

    /// <summary>A button while the banner holds sends it away (knife <c>skip_banner</c>).</summary>
    public void DismissBanner() => _banner?.SendAway();

    /// <summary>Shows a toast with <paramref name="text"/> (package K21); a toast still on show is replaced. <paramref name="brief"/> false keeps it up longer (an address or an error to read; package K24).</summary>
    public void ShowToast(string text, bool brief = true) => _toast = new UiToast(text, brief);

    /// <summary>Steps the UI by one 60 Hz frame.</summary>
    public void Step(in InputFrame frame)
    {
        Context.Frame++;
        if (Context.Services.Language != Context.Assets.Language)
        {
            Context.Assets.SetLanguage(Context.Services.Language);
        }

        _blurFrames = BlurScene ? _blurFrames + 1 : 0;
        UiInput input = ApplyGuideClick(UiInput.From(frame, _previousPointer));
        _previousPointer = frame.Pointer;
        UiScreen[] screens = [.. _stack];
        foreach (UiScreen s in screens)
        {
            s.StepFade();
        }

        // a screen may push or clear screens while it steps: every call is checked against the live stack
        if (screens.Length > 0)
        {
            for (int i = 0; i < screens.Length - 1; i++)
            {
                if (_stack.Contains(screens[i]))
                {
                    screens[i].StepBackground();
                }
            }

            if (_stack.Contains(screens[^1]))
            {
                screens[^1].Step(input);
            }
        }

        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (i >= _stack.Count)
            {
                // an OnClosed above emptied the stack (the gallery's replay hand-off clears every screen): nothing is left below to check (package K15: the index ran off
                // the end and ended the app on the first replay started from the title's gallery)
                continue;
            }

            if (_stack[i].Finished)
            {
                UiScreen s = _stack[i];
                _stack.RemoveAt(i);
                s.OnClosed();
            }
        }

        Context.Backdrop.Update();
        _banner?.Step();
        if (_banner is { Done: true })
        {
            _banner = null;
        }

        _toast?.Step();
        if (_toast is { Done: true })
        {
            _toast = null;
        }
    }

    // package K12: a click on a word of the button guide line (drawn last frame, UiPainter.GuideHits) does what the word's button does: Back cancels, Confirm decides,
    // Delete Data deletes, a shoulder pair presses L1 on its left half and R1 on its right; the click is not also a click on whatever the screen has there
    private UiInput ApplyGuideClick(UiInput input)
    {
        if (!input.Click || !input.PointerActive)
        {
            return input;
        }

        foreach (GuideHit h in _painter.GuideHits)
        {
            if (!h.Rect.Contains(input.PointerX, input.PointerY))
            {
                continue;
            }

            UiInput u = input with { Click = false, Decide = false };
            return h.Action switch
            {
                GuideAction.Back => u with { Cancel = true },
                GuideAction.Confirm => u with { Decide = true },
                GuideAction.DeleteData => u with { DeleteData = true },
                GuideAction.Shoulders => input.PointerX < h.Rect.X + (h.Rect.W / 2f) ? u with { L1 = true } : u with { R1 = true },
                _ => u,
            };
        }

        return input;
    }

    /// <summary>Appends the quads of the open screens and the banner to <paramref name="list"/>.</summary>
    public void Draw(DisplayList list)
    {
        // the frozen scene behind the system menu and the backlog is blurred before anything of the menu is drawn over it (package K16)
        if (BlurScene)
        {
            UiMenuBlur.Append(list, BlurAlpha);
        }

        _painter.Begin(list);
        _painter.PromptStyle = Context.Services.PromptStyle;
        int first = 0;
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Opaque)
            {
                first = i;
                break;
            }
        }

        for (int i = first; i < _stack.Count; i++)
        {
            _stack[i].Draw(_painter);
        }

        _banner?.Draw(_painter, Context);

        // the story's yes / no prompt (_SELA, _SELS) is the host's; it shows only while no screen covers the story
        if (_stack.Count == 0 && Context.Services.StoryPrompt is { } prompt)
        {
            UiStoryPromptView.Draw(_painter, Context, prompt);
        }

        // the port's toast is the last thing drawn, over everything, and left out of a frame that is being captured (package K21)
        if (_toast is not null && !HideToast)
        {
            _toast.Draw(_painter);
        }
    }

    /// <summary>
    /// True when a left click went down on the Confirm word of the story prompt's guide line (the words are those of the last drawn frame). The caller turns it into
    /// the decide button; false while a screen is open or no prompt shows.
    /// </summary>
    /// <param name="pointer">The pointer of the step.</param>
    public bool StoryPromptConfirmClicked(in PointerState pointer)
    {
        if (_stack.Count > 0 || !pointer.Active || (pointer.Pressed & PointerButtons.Left) == 0 || Context.Services.StoryPrompt is null)
        {
            return false;
        }

        float x = float.IsNaN(pointer.CanvasX) ? (pointer.X + 0.5f) * UiMetrics.UnitX : pointer.CanvasX;
        float y = float.IsNaN(pointer.CanvasY) ? (pointer.Y + 0.5f) * UiMetrics.UnitY : pointer.CanvasY;
        foreach (GuideHit h in _painter.GuideHits)
        {
            if (h.Action == GuideAction.Confirm && h.Rect.Contains(x, y))
            {
                return true;
            }
        }

        return false;
    }
}
