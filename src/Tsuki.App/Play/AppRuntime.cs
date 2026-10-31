// The logic of one 60 Hz step of the app (ARCHITECTURE 3.4; package K6), with no window, GPU or audio device in it: the input is routed between the story and the
// menus, the host steps (or its last frame is repeated under a menu), the sound and movie commands go to the media layer, the menus step and draw, and a story
// that ended hands over to the title or the gallery. The GL loop (GameApp) and the tests drive it with InputFrames.
using System.Diagnostics;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Media.Movie;
using Tsuki.Media.Sound;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.App.Play;

/// <summary>What the runtime did in one step (for logs and tests).</summary>
/// <param name="Frame">The step number.</param>
/// <param name="StoryStepped">The host ran a frame.</param>
/// <param name="UiMode">The UI mode at the end of the step.</param>
/// <param name="Rewound">A rewind step ran.</param>
public readonly record struct StepReport(long Frame, bool StoryStepped, UiMode UiMode, bool Rewound);

/// <summary>The milliseconds one <see cref="AppRuntime.Step"/> spent: the movie clocks and services, the menus' step, the story step (interpreter, stage, sound model, display list), the media commands, the rest.</summary>
public readonly record struct StepTimes(double Movies, double Ui, double Story, double Media, double Rest, double Draw = 0);

/// <summary>The app's per-step logic.</summary>
public sealed class AppRuntime
{
    private const int RewindPeriod = 8;
    private readonly AudioCommandPlayer? _audio;
    private readonly MovieHost? _movies;
    private readonly DisplayList _frozen = new(2048);
    private readonly IReadOnlyList<MovieClock> _noClocks = [];
    private readonly MovieClock[] _clockBuffer = new MovieClock[8];
    private (int Width, int Height, byte[] Rgba)? _heldThumbnail;
    private (int Width, int Height, byte[] Rgba)? _storyThumbnail;
    private (int Width, int Height, byte[] Rgba)? _brightThumbnail;
    private long _brightThumbnailFrame;
    private long _storyThumbnailFrame = -StoryThumbnailGap;
    private GameHost? _thumbnailHost;
    private int _thumbnailRestores;
    private int _rewindHold;
    private bool _topArmed = true;
    private bool _hiddenByPointer;
    private (short X, short Y)? _gestureStart;
    private int _gesture;
    private bool _openSaveScreen;
    private SaveLoadScreen? _promptSave;
    private bool _ending;

    /// <summary>Creates the runtime over the services and the UI.</summary>
    public AppRuntime(AppServices services, UiRoot ui, AudioCommandPlayer? audio, MovieHost? movies)
    {
        Services = services;
        Ui = ui;
        _audio = audio;
        _movies = movies;
        services.Ui = ui;
        services.FrameCapture = () => _heldThumbnail ?? Grab?.Invoke();
        services.SaveScreenRequested += () => _openSaveScreen = true;
        services.StoryEnded += OnStoryEnded;
        HookHost(services.Host);
        services.HostReplaced += HookHost;
    }

    // _RDAS on a scene the player has completed (GameHost.CmdRdas, with Auto Skip Cleared Scenes on): the engine only raises the offer; the port answers it the
    // way the PS4's mode panel does (knife ui.rs 6177-6184, sub_40ED0 top state 1): the next scene, asked first (dialog 10) when Show Confirmation Message is
    // on, else after a 4-frame countdown (off_AE06B8). Package R1: both config options did nothing before.
    private const int AutoSkipCountdownFrames = 4;
    private bool _autoSkipOffered;
    private int _autoSkipCountdown;

    private void HookHost(GameHost host) => host.AutoSkipOffered += _ => _autoSkipOffered = true;

    private void OfferAutoSkip(bool story)
    {
        if (!story || Services.ReplayActive)
        {
            _autoSkipOffered = false;
            _autoSkipCountdown = 0;
            return;
        }

        if (_autoSkipOffered && !Ui.Active)
        {
            // a menu over the story keeps the offer until it closes
            _autoSkipOffered = false;
            if (Services.Config.SkipConfirm != 0)
            {
                BeginOverlay();
                Ui.Push(new ConfirmDialog(ConfirmKind.NextScene, yes =>
                {
                    if (yes)
                    {
                        Services.NextScene();
                    }
                }));
            }
            else
            {
                _autoSkipCountdown = AutoSkipCountdownFrames;
            }
        }

        if (_autoSkipCountdown > 0 && --_autoSkipCountdown == 0)
        {
            Services.NextScene();
        }
    }

    /// <summary>The game services.</summary>
    public AppServices Services { get; }

    /// <summary>The menus.</summary>
    public UiRoot Ui { get; }

    /// <summary>The interpreter (replaced when the language changes).</summary>
    public GameHost Host => Services.Host;

    /// <summary>The step counter.</summary>
    public long Frame { get; private set; }

    /// <summary>Reads the frame buffer as a 320 x 180 thumbnail; set by the renderer (null in tests: saves have no thumbnail).</summary>
    public Func<(int Width, int Height, byte[] Rgba)?>? Grab { get; set; }

    /// <summary>Raised for lines the runtime wants logged.</summary>
    public event Action<string>? Logged;

    /// <summary>True when the player asked to quit: the title's EXIT was confirmed (package K18) or the window's close was confirmed (package K21). The app then ends the run.</summary>
    public bool ExitRequested => Services.QuitRequested;

    /// <summary>True when the quit came from the window's close button or Alt+F4 (confirmed in the quit question, or asked twice), not from the title's EXIT.</summary>
    public bool CloseConfirmed { get; private set; }

    private ConfirmDialog? _closeDialog;

    /// <summary>True while the quit question the window's close request opened is up and unanswered.</summary>
    public bool CloseDialogOpen => _closeDialog is { Waiting: true } d && Ui.Stack.Contains(d);

    /// <summary>
    /// The window's close button or Alt+F4 (package K21): the game's own quit question ("Are you sure you want to quit without saving?", ui_text 236, the K18 dialog of
    /// the title's EXIT) opens over whatever is on the screen, the cursor on No; Yes ends the run the way EXIT does (the system data is written, no slot). While that
    /// question is already up, from this or from the title's EXIT, a second close request quits at once. Returns true when the run ends now.
    /// </summary>
    public bool RequestClose()
    {
        if (CloseDialogOpen || (Ui.Top is ConfirmDialog { Waiting: true } top && top.MessageId(Ui.Context.Assets) == TitleScreen.QuitMessage))
        {
            CloseConfirmed = true;
            Services.RequestQuit();
            return true;
        }

        BeginOverlay();
        _closeDialog = Ui.Push(new ConfirmDialog(TitleScreen.QuitMessage, noFirst: true, yes =>
        {
            if (yes)
            {
                CloseConfirmed = true;
                Services.RequestQuit();
            }
        }));
        return false;
    }

    /// <summary>The last step's report.</summary>
    public StepReport Last { get; private set; }

    /// <summary>Where the last step spent its time (verification mode: the hitch log names the part that was slow).</summary>
    public StepTimes LastTimes { get; private set; }

    /// <summary>Opens the boot sequence: the autosave notice, then the title.</summary>
    public void Start()
    {
        Ui.Push(new BootScreen());
    }

    private void OnStoryEnded(EndReason reason)
    {
        _ending = true;
        Logged?.Invoke($"story ended: {reason}");
    }

    /// <summary>Runs one step: the story or the menus, and the display list of the frame.</summary>
    public StepReport Step(in InputFrame raw, long nominalTimestamp, DisplayList list)
    {
        long t0 = Stopwatch.GetTimestamp();
        Frame++;
        Services.Tick();
        IReadOnlyList<MovieClock> clocks = _movies?.Step(nominalTimestamp) ?? _noClocks;
        long t1 = Stopwatch.GetTimestamp();
        int n = Math.Min(clocks.Count, _clockBuffer.Length);
        for (int i = 0; i < n; i++)
        {
            _clockBuffer[i] = clocks[i];
        }

        Host.SetMovieClocks(_clockBuffer.AsSpan(0, n));
        Services.UpdateMovieClocks(clocks);

        InputFrame input = raw;
        bool uiWasActive = Ui.Active;
        bool story = Services.StoryActive && Host.Phase == GamePhase.Running;
        if (!story || !ReferenceEquals(Host, _thumbnailHost) || Host.RestoreCount != _thumbnailRestores)
        {
            // another story, a load or a rewind: the page seen before is not this story's last page
            _storyThumbnail = null;
            _brightThumbnail = null;
            _storyThumbnailFrame = -StoryThumbnailGap;
            _thumbnailHost = Host;
            _thumbnailRestores = Host.RestoreCount;
        }

        UpdateGesture(raw, story && !Ui.Active && !Host.ChoiceOpen && !Host.MovieActive);
        if (story && !Ui.Active)
        {
            input = RouteStoryInput(raw);
        }
        else if (!Ui.Active)
        {
            _heldThumbnail = null;
        }

        // the yes of a _SELA prompt: the save screen opens over the story (the dialog has faded out), and the script goes on once it is closed, saved or not
        if (_openSaveScreen && !Ui.Active)
        {
            _openSaveScreen = false;
            BeginOverlay();
            // a chapter start is a black screen: the save made here shows the last page the player read, as a save from the system menu does (package K16)
            (int Width, int Height, byte[] Rgba)? bright = Frame - _brightThumbnailFrame <= BrightThumbnailAge ? _brightThumbnail : null;
            _heldThumbnail = bright ?? _storyThumbnail ?? _heldThumbnail;
            _promptSave = Ui.Push(new SaveLoadScreen(save: true));
        }

        // the guide line's Confirm word, clicked while the story's yes / no prompt is up, is the decide button
        if (story && !Ui.Active && Host.PromptOpen && Ui.StoryPromptConfirmClicked(raw.Pointer))
        {
            input = input with { Pressed = input.Pressed | PadButtons.Decide, Pointer = input.Pointer with { Pressed = input.Pointer.Pressed & ~PointerButtons.Left } };
        }

        // the title asked for while a menu is still up (the system menu's Title button, the safety net below) waits until the menu has closed
        if (!Ui.Active && Services.TakePendingTitle())
        {
            Services.StopStoryOutputs();
            Ui.Push(new TitleScreen());
            story = false;
        }

        Ui.Step(uiWasActive ? raw : input with { Pressed = PadButtons.None, Repeated = PadButtons.None, MenuPressed = PadButtons.None, MenuRepeated = PadButtons.None, ShellPressed = ShellActions.None, Pointer = input.Pointer with { Pressed = PointerButtons.None, Wheel = 0 } });

        if (_promptSave is not null && !Ui.Stack.Contains(_promptSave))
        {
            _promptSave = null;
            Host.SavePromptScreenClosed();
        }

        long t2 = Stopwatch.GetTimestamp();

        // a story that started under the title (New Game, a load, a gallery replay) takes the screen: the title does not clear itself on the first-visit path, and
        // its looping background movie stops with it (the title screen's FadeIn starts it again when the story ends; package R1)
        if (Services.StoryActive && Ui.Mode == UiMode.Title && Host.Phase == GamePhase.Running)
        {
            Ui.Clear();
            Services.StopTitleMovie();
        }

        bool panelDrive = Ui.Top is ModePanelScreen && (Services.FastForwardHeld || Services.RewindHeld);
        bool rewound = false;
        bool stepped = false;
        list.Reset(Frame);
        story = Services.StoryActive && Host.Phase == GamePhase.Running;
        if (story && (!Ui.Active || panelDrive))
        {
            InputFrame gi = Ui.Active ? default : input;
            if (Services.FastForwardHeld || _gesture > 0)
            {
                gi = gi with { ShellHeld = gi.ShellHeld | ShellActions.FastForward };
            }

            rewound = Rewind(raw);
            HoverChoice(raw);
            bool auto = Host.AutoMode;
            bool skip = Host.SkipLatch;
            Host.Step(gi, list);
            stepped = true;
            if (Host.AutoMode && !auto)
            {
                Ui.ShowBanner(BannerKind.Auto);
            }

            if (Host.SkipLatch && !skip)
            {
                Ui.ShowBanner(BannerKind.Skip);
            }

            CopyFrozen(list);
            NoteStoryFrame();
        }
        else if (story && Ui.Active)
        {
            // the story stands still under the menu; the system menu and the backlog hide the message window and the text (UiRoot.BlurScene)
            ReadOnlySpan<DrawCmd> frozen = _frozen.Commands;
            int keep = Ui.BlurScene ? Math.Min(_frozenSceneCount, frozen.Length) : frozen.Length;
            for (int i = 0; i < keep; i++)
            {
                list.Add(frozen[i]);
            }
        }

        if (stepped || story)
        {
            Services.NoteStoryStep();
        }

        OfferAutoSkip(story);
        long t3 = Stopwatch.GetTimestamp();

        // the sound and movie commands the host produced go to the media layer in the same step (the voice plays went earlier, inside the step: AppServices.PerformHostAudio)
        Services.PerformHostAudio();
        MovieCommand[] movieCommands = Host.TakeMovieCommands();
        if (movieCommands.Length > 0)
        {
            _movies?.Perform(movieCommands);
        }

        _audio?.Pump();
        long t4 = Stopwatch.GetTimestamp();
        Services.CheckStoryEnd();
        if (Services.StoryActive && Host.Phase == GamePhase.NotStarted)
        {
            // a story whose host has nothing to run (a host rebuilt for a language change that could not restart its scene): the title takes over once the menus
            // have closed, so the screen can never stay dead (package R1)
            Logged?.Invoke("story host not started: back to the title");
            Services.ReturnToTitle();
        }

        if (_ending)
        {
            _ending = false;
            EndStory();
        }

        long t4b = Stopwatch.GetTimestamp();
        Ui.Draw(list);
        list.Seal();
        long t5 = Stopwatch.GetTimestamp();
        LastTimes = new StepTimes(Ms(t0, t1), Ms(t1, t2), Ms(t2, t3), Ms(t3, t4), Ms(t4, t4b), Ms(t4b, t5));
        Last = new StepReport(Frame, stepped, Ui.Mode, rewound);
        return Last;
    }

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    /// <summary>The mouse over a choice line moves the cursor to it (the click that decides it is the interpreter's own rule).</summary>
    private void HoverChoice(in InputFrame raw)
    {
        if (!Host.ChoiceSelectable || !raw.Pointer.Active)
        {
            return;
        }

        if (raw.Pointer.X == _lastPointerX && raw.Pointer.Y == _lastPointerY && raw.Pointer.Pressed == PointerButtons.None)
        {
            return;
        }

        _lastPointerX = raw.Pointer.X;
        _lastPointerY = raw.Pointer.Y;
        int line = Host.ChoiceLineAt(CanvasY(raw.Pointer));
        if (line >= 0)
        {
            Host.HoverChoice(line);
        }
    }

    private static double CanvasY(in PointerState p) => float.IsNaN(p.CanvasY) ? (p.Y + 0.5) * UiMetrics.UnitY : p.CanvasY;

    // a drag that starts in the middle of the screen (knife touch.rs: x 121..519, y 125..383 of 640 x 448) and runs 45 units or more to the right fast-forwards while the
    // button stays down, to the left it rewinds (the PS4 Controls page: "Click the center of the screen and drag right to fast-forward / left to rewind")
    private void UpdateGesture(in InputFrame raw, bool allowed)
    {
        PointerState p = raw.Pointer;
        if (!allowed || !p.Active || (p.Held & PointerButtons.Left) == 0)
        {
            _gestureStart = null;
            _gesture = 0;
            return;
        }

        if (_gestureStart is null && (p.Pressed & PointerButtons.Left) != 0 && p.X is >= 121 and <= 519 && p.Y is >= 125 and <= 383)
        {
            _gestureStart = (p.X, p.Y);
        }

        if (_gestureStart is { } s)
        {
            int dx = p.X - s.X;
            _gesture = dx >= GestureUnits ? 1 : dx <= -GestureUnits ? -1 : 0;
        }
    }

    private const int TopEdgeUnits = 12;
    private const int BottomEdgeUnits = 436;
    private const int GestureUnits = 45;

    private short _lastPointerX = -1;
    private short _lastPointerY = -1;

    private void CopyFrozen(DisplayList list)
    {
        _frozen.Reset(Frame);
        _frozenSceneCount = 0;
        int index = 0;
        foreach (DrawCmd c in list.Commands)
        {
            if (c.Kind != DrawKind.Capture)
            {
                _frozen.Add(c);
                if (index < Host.SceneCommandCount)
                {
                    _frozenSceneCount++;
                }
            }

            index++;
        }
    }

    private int _frozenSceneCount;

    private void EndStory()
    {
        Services.StopStoryOutputs();
        if (Host.ReplayRequested && Ui.ReturnToGallery())
        {
            return;
        }

        if (!Ui.Active)
        {
            Ui.Push(new TitleScreen());
        }
    }

    // The last frame of a page that was being read (a message fully shown in an open window), grabbed at most once in StoryThumbnailGap steps: the thumbnail of
    // a save made from a story prompt, whose own frame is the black start of a chapter (package K16; K13 open issue 4)
    private const int StoryThumbnailGap = 180;

    private void NoteStoryFrame()
    {
        if (Grab is null || Frame - _storyThumbnailFrame < StoryThumbnailGap || Host.ChoiceOpen || Host.MovieActive || Host.SkipLatch || Services.FastForwardHeld
            || Host.PageMessages.Count == 0 || Host.Wait.WindowAlpha < 0.99f || Host.TextFade < 1f || Host.F(1103) != 0)
        {
            return;
        }

        _storyThumbnailFrame = Frame;
        _storyThumbnail = Grab();
        if (_storyThumbnail is { } g && MeanLuma(g.Rgba) >= BrightLuma)
        {
            // a chapter ends on a dark page: the last bright page, if it is recent, says more of where the save is than the dark one
            _brightThumbnail = g;
            _brightThumbnailFrame = Frame;
        }
    }

    private const int BrightThumbnailAge = 6000;
    private const double BrightLuma = 14.0;

    private static double MeanLuma(byte[] rgba)
    {
        long sum = 0;
        for (int i = 0; i + 2 < rgba.Length; i += 4)
        {
            sum += rgba[i] + rgba[i + 1] + rgba[i + 2];
        }

        return rgba.Length < 4 ? 0 : sum / 3.0 / (rgba.Length / 4);
    }

    private void BeginOverlay()
    {
        _heldThumbnail = Grab?.Invoke();
    }

    private bool Rewind(in InputFrame raw)
    {
        bool held = (raw.Held & PadButtons.L2) != 0 || (raw.ShellHeld & ShellActions.Rewind) != 0 || Services.RewindHeld || _gesture < 0;
        if (!held)
        {
            _rewindHold = 0;
            return false;
        }

        _rewindHold++;
        if (_rewindHold != 1 && _rewindHold % RewindPeriod != 0)
        {
            return false;
        }

        bool ok = Host.RewindStep();
        if (ok)
        {
            Host.SetSkipLatch(false);
            Logged?.Invoke($"rewind: script {Host.Script} offset {Host.Machine.Cursor}");
        }

        return ok;
    }

    /// <summary>
    /// The shell actions of a story with no menu open: the system menu, the backlog, the mode panel, quick save and load, the scene jumps and the voice replay.
    /// Returns the input the host gets (the keys that opened a menu are taken out of it).
    /// </summary>
    private InputFrame RouteStoryInput(in InputFrame raw)
    {
        InputFrame input = raw;
        ShellActions sp = raw.ShellPressed;
        PadButtons pp = raw.Pressed;
        bool right = (raw.Pointer.Pressed & PointerButtons.Right) != 0;
        bool movie = Host.MovieActive;
        bool choice = Host.ChoiceOpen;
        PointerState ptr = raw.Pointer;

        // the mouse rows of the PS4 Controls page (package K12): the pointer at the top of the screen opens the control panel (the mode panel), at the bottom it
        // hides the message window while it stays there, a drag from the middle of the screen to the right fast-forwards and to the left rewinds (UpdateGesture); the
        // wheel button toggles skip (InputMapper), the wheel up opens the backlog, a left click advances and a right click opens the system menu
        bool inTop = ptr.Active && ptr.Y < TopEdgeUnits;
        bool inBottom = ptr.Active && ptr.Y > BottomEdgeUnits;
        if (!ptr.Active || ptr.Y >= TopEdgeUnits + 10)
        {
            _topArmed = true;
        }

        ShellActions mouseShell = ShellActions.None;
        if (!choice && !movie)
        {
            if (inBottom && !_hiddenByPointer && !Host.WindowHidden)
            {
                mouseShell |= ShellActions.HideWindow;
                _hiddenByPointer = true;
            }
            else if (!inBottom && _hiddenByPointer)
            {
                _hiddenByPointer = false;
                if (Host.WindowHidden)
                {
                    mouseShell |= ShellActions.HideWindow;
                }
            }
        }
        else if (!inBottom)
        {
            _hiddenByPointer = false;
        }

        // the click that would advance the text does not when it lands where the window is hidden, nor on empty space while a choice list waits for a pick
        if ((ptr.Pressed & PointerButtons.Left) != 0
            && ((inBottom && Host.WindowHidden && !choice) || (Host.ChoiceSelectable && Host.ChoiceKind == ChoiceKind.List && Host.ChoiceLineAt(CanvasY(ptr)) < 0)))
        {
            input = input with { Pointer = ptr with { Pressed = ptr.Pressed & ~PointerButtons.Left } };
        }

        bool menuKey = (sp & ShellActions.SystemMenu) != 0 || (pp & PadButtons.Square) != 0 || right;
        if (menuKey && !movie)
        {
            if (!Ui.Active)
            {
                BeginOverlay();
                Ui.OpenSystemMenu();
            }

            return Strip(input);
        }

        if (!choice && !movie && ((pp & PadButtons.Triangle) != 0 || (sp & ShellActions.Backlog) != 0) && Services.Backlog.Count > 0)
        {
            BeginOverlay();
            if (Ui.OpenBacklog())
            {
                return Strip(input);
            }
        }

        if (!choice && !movie && ((pp & PadButtons.Up) != 0 || (inTop && _topArmed)) && Services.PanelAllowed)
        {
            BeginOverlay();
            if (Ui.OpenModePanel())
            {
                _topArmed = false;
                return Strip(input);
            }
        }

        if ((sp & ShellActions.QuickSave) != 0 && !movie)
        {
            if (Services.QuickSave())
            {
                Ui.ShowBanner(BannerKind.QuickSave);
            }
        }

        if ((sp & ShellActions.QuickLoad) != 0 && !movie && Services.HasQuickSave)
        {
            BeginOverlay();
            Ui.Push(new ConfirmDialog(ConfirmKind.QuickLoad, yes =>
            {
                if (yes && Services.QuickLoad())
                {
                    Ui.Clear();
                }
            }, thumbnailSlot: 139));
            return Strip(input);
        }

        if ((sp & ShellActions.NextScene) != 0 && Services.PanelAllowed && Services.SceneJumpEnabled)
        {
            BeginOverlay();
            Ui.Push(new ConfirmDialog(ConfirmKind.NextScene, yes =>
            {
                if (yes)
                {
                    Services.NextScene();
                }
            }));
            return Strip(input);
        }

        if ((sp & ShellActions.PreviousScene) != 0 && Services.PanelAllowed && Services.SceneJumpEnabled && Host.CanGoToPreviousScene)
        {
            BeginOverlay();
            Ui.Push(new ConfirmDialog(ConfirmKind.PrevScene, yes =>
            {
                if (yes)
                {
                    Services.PreviousScene();
                }
            }));
            return Strip(input);
        }

        if ((sp & ShellActions.ReplayVoice) != 0)
        {
            Services.ReplayVoice();
        }

        // the keys the menus use must not also act on the story
        PadButtons mask = ~(PadButtons.Square | PadButtons.Triangle | PadButtons.L2);
        return input with { Held = input.Held & mask, Pressed = input.Pressed & mask, Repeated = input.Repeated & mask, ShellPressed = input.ShellPressed | mouseShell };
    }

    private static InputFrame Strip(in InputFrame input)
    {
        PadButtons mask = ~(PadButtons.Cancel | PadButtons.Square | PadButtons.Triangle | PadButtons.L2 | PadButtons.Decide | PadButtons.Up);
        PointerState p = input.Pointer;
        return input with
        {
            Held = input.Held & mask,
            Pressed = input.Pressed & mask,
            Repeated = input.Repeated & mask,
            Pointer = p with { Held = p.Held & ~(PointerButtons.Right | PointerButtons.Left), Pressed = p.Pressed & ~(PointerButtons.Right | PointerButtons.Left), Wheel = 0 },
            ShellPressed = ShellActions.None,
        };
    }
}
