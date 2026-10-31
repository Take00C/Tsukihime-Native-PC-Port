// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (Intro, enter_title, draw_title, title_entries, title_slot, act: NewGame / Continue) and knife/src/fx.rs (LogoFx)
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui.Screens;

/// <summary>The title menu's items (the order of docs/formats/system_flow.md 4.4).</summary>
public enum TitleItem
{
    /// <summary>CONTINUE: loads the newest save.</summary>
    Continue = 0,

    /// <summary>NEW GAME.</summary>
    NewGame = 1,

    /// <summary>LOAD GAME.</summary>
    Load = 2,

    /// <summary>CONFIG.</summary>
    Config = 3,

    /// <summary>GALLERY (package K5b).</summary>
    Gallery = 5,

    /// <summary>
    /// EXIT (package K18): the last item. The PS4 has none; the port adds it so that the game can be left from the title. Its answer is the PS4's own quit question
    /// (ui_text 236, dialog 14 of the table), Yes ends the run like the window's close button.
    /// </summary>
    Exit = 7,
}

/// <summary>What the title is doing.</summary>
public enum TitlePhase
{
    /// <summary>The title fades in from black (121 frames, system_flow.md 4.3 state 1).</summary>
    FadeIn,

    /// <summary>The menu, or PRESS ANY BUTTON before the first story.</summary>
    Idle,

    /// <summary>The returning player's intro: white cover in, the TM_CI movie, the second caution (system_flow.md 3.4 steps 7 to 9).</summary>
    Intro,

    /// <summary>A first-run caution or caution1 over a black cover (system_flow.md 3.4 step 12).</summary>
    Caution,

    /// <summary>A white cover rises before the story starts (state 0x0B).</summary>
    WhiteOut,

    /// <summary>Loading the newest save: the popup, then black.</summary>
    Loading,

    /// <summary>Fading to black for a new story, a load or the gallery.</summary>
    Leaving,
}

/// <summary>The G002 title stage rule (docs/formats/system_flow.md 4.2; <c>tsuki.misc_boot_title_choice_flow.advance_title_state</c>).</summary>
public static class TitleStageRule
{
    /// <summary>One step of the rule: G002 after it, given the story-started flag G010 and the scene bits 560, 383 and 562.</summary>
    public static int Advance(int g2, bool storyStarted, bool bit560, bool bit383, bool bit562)
    {
        if (!storyStarted)
        {
            return g2;
        }

        if (g2 is < 0 or > 5)
        {
            g2 = 0;
        }

        return g2 switch
        {
            0 => bit560 ? 1 : 0,
            1 => bit383 ? 3 : 1,
            3 => bit562 ? 5 : 3,
            2 or 4 => 0,
            _ => g2,
        };
    }
}

/// <summary>
/// The title screen (docs/formats/system_flow.md 4; knife <c>Screen::Title</c>). The PS4 flow: a returning player sees the TM_CI movie and the second caution
/// once per power-on, then the title fades in over the background movie (G002 picks <c>titlebg1..4</c>, or the still while no frame is ready) with the logo,
/// the moon glow and the menu CONTINUE (when there is a save), NEW GAME, LOAD GAME, CONFIG, GALLERY and (the port's addition, package K18; its cell composed from
/// the letters of the other items at run time, package K33, <see cref="TitleExitArt"/>) EXIT; a player who has not started a story sees PRESS ANY
/// BUTTON, then the two cautions, then the story; a player who has not read the prologue starts it at once. The menu draws on the 640 x 448 canvas (items 296 x 72
/// at x 272), the logo, the glow and the rest on the 960 x 544 one (docs section 4.5).
/// </summary>
public sealed class TitleScreen : UiScreen
{
    private const double LoopPoint = 8.0333;

    /// <summary>
    /// Steps without any input after which the logo and the menu fade out and leave the moving background (package K18): 3600 = 60 s at 60 Hz, the PS4's own idle
    /// counter (u32 0x1762204: set to 0xE10 = 3600 by the title update, VA 0xB074D, whenever the pad's button word has any bit of 0xF9FC set, and counted down once a
    /// frame, 0xB0739). The PS4's only reader of the counter is attract-mode code that nothing reaches, so the fade itself has no eboot value to copy: it uses the
    /// logo's own fall (1 / 60 per frame, docs/formats/system_flow.md 4.5.1) and any input snaps the logo back to 1 as every menu input does there.
    /// </summary>
    public const int IdleFrames = 3600;
    private const float ScreenW = UiMetrics.Width;

    private readonly UiItemList _items = new();
    private readonly List<Shard> _shards = [];
    private readonly List<Dot> _dots = [];
    private float _shardAcc;
    private int _g2;
    private bool _movieStarted;
    private bool _passed;
    private bool _seekDue;
    private float _black = 1f;
    private float _white;
    private float _menuAlpha;
    private float _logo;
    private bool _logoRising = true;
    private float _grow;
    private float _pulse;
    private float _clock;
    private int _frame;
    private int _introStep;
    private int _introT;
    private int _cautionIdx;
    private int _cautionStep;
    private int _cautionT;
    private float _cautionA;
    private float _cover;
    private TitleItem[] _entries = [];
    private int _loadSlot = -1;
    private MessagePopup? _popup;
    private ConfirmDialog? _quitDialog;
    private int _idleFrames;
    private bool _idleHidden;
    private bool _wokeFromIdle;
    private bool _started;

    /// <summary>The title is on show (the host's story is not running).</summary>
    public bool Idle => Phase == TitlePhase.Idle;

    /// <summary>What the title is doing.</summary>
    public TitlePhase Phase { get; private set; } = TitlePhase.FadeIn;

    /// <summary>The cursor item, or null while there is none (PRESS ANY BUTTON).</summary>
    public TitleItem? Cursor => _items.Cursor >= 0 && _items.Cursor < _entries.Length ? _entries[_items.Cursor] : null;

    /// <summary>True while the logo and the menu have faded out after <see cref="IdleFrames"/> steps with no input (package K18); any input brings them back.</summary>
    public bool IdleHidden => _idleHidden;

    /// <summary>True while PRESS ANY BUTTON shows instead of the menu (a story was never started).</summary>
    public bool PressMode { get; private set; }

    /// <summary>The G002 title stage after the rule step.</summary>
    public int Stage => _g2;

    /// <summary>The still behind the menu: TITLE_PARTS child 9 + T (docs 4.2), or 9 when no story was started.</summary>
    public UiSheet Still => PressMode ? UiSheets.TitleBg0 : UiSheets.TitleStill(Ctx.Assets.Tables.TitleBackgroundTable[Math.Clamp(_g2, 0, 5)]);

    /// <inheritdoc/>
    public override bool Opaque => true;

    /// <summary>True when the player has to see the intro: a returning player's first title of this power-on.</summary>
    private bool NeedsIntro => Ctx.Services.Title is { PrologueSeen: true, StoryStarted: true } && !Ctx.TitleIntroShown;

    /// <inheritdoc/>
    public override void OnOpened()
    {
        // a title starts from nothing: the backdrop of an earlier screen (the story's system menu, whose "Return to Title" never closes it) must not stay up under the
        // logo, where it keeps the logo and the menu hidden for good (package K14)
        Ctx.Backdrop.Reset();
        UiTitleInfo info = Ctx.Services.Title;
        IGameServices s = Ctx.Services;
        PressMode = !info.StoryStarted;
        _g2 = TitleStageRule.Advance(info.TitleStage, info.StoryStarted, s.SceneBit(560), s.SceneBit(383), s.SceneBit(562));
        if (_g2 != info.TitleStage)
        {
            s.SetGlobal(2, _g2);
        }

        if (!info.PrologueSeen)
        {
            // title init: bit 485 clear -> title state 0x0C, the title is not drawn and New Game follows at once (scene 260, the prologue)
            Phase = TitlePhase.Leaving;
            _started = true;
            s.StartScene(260);
            return;
        }

        if (NeedsIntro)
        {
            Phase = TitlePhase.Intro;
            _introStep = 0;
        }
        else
        {
            BeginTitle();
        }

        BuildEntries();
    }

    private void BeginTitle()
    {
        Phase = TitlePhase.FadeIn;
        _black = 1f;
        _logo = 0f;
        _logoRising = true;
        IGameServices s = Ctx.Services;
        UiTitleInfo info = Ctx.Services.Title;
        if (info.StoryStarted)
        {
            int t = Ctx.Assets.Tables.TitleBackgroundTable[Math.Clamp(_g2, 0, 5)];
            if (t - 1 < 4)
            {
                s.StartTitleMovie(t - 1);
                _movieStarted = true;
            }
        }

        s.PlayBgm(_g2 == 5 ? 72 : 2);
        BuildEntries();
    }

    private void BuildEntries()
    {
        var list = new List<TitleItem>();
        if (Ctx.Services.Title.ContinueSlot >= 0)
        {
            list.Add(TitleItem.Continue);
        }

        list.AddRange([TitleItem.NewGame, TitleItem.Load, TitleItem.Config, TitleItem.Gallery, TitleItem.Exit]);
        _entries = [.. list];
        _items.Clear();
        for (int i = 0; i < _entries.Length; i++)
        {
            RectF slot = ItemRect(i, _entries.Length);
            // the touch rect of an item is the object's own (0, 4)-(98, 25) from its slot, whole units inclusive (sub_AE840), not the 72 px sprite; the port's six rows
            // (package K28) are closer than 22 units, so their rects are as tall as the pitch and tile without overlapping (the letters and the cursor underline stay inside)
            bool six = _entries.Length >= 6;
            float top = six ? slot.Y + (4f * UiMetrics.UnitY) : (MathF.Truncate(slot.Y / UiMetrics.UnitY) + 4f) * UiMetrics.UnitY;
            float height = (six ? SixRowPitch : 22f) * UiMetrics.UnitY;
            _items.Add(new RectF(MathF.Truncate(272f) * UiMetrics.UnitX, top, 99f * UiMetrics.UnitX, height), (int)_entries[i]);
        }

        // the cursor on entry: CONTINUE when offered, else NEW GAME (system_flow.md 4.3 state 0x05)
        if (_items.Cursor < 0 || _items.Cursor >= _entries.Length)
        {
            _items.Cursor = PressMode ? -1 : 0;
        }
    }

    /// <summary>The canvas rectangle of item <paramref name="i"/> of <paramref name="n"/> (docs 4.5 row 7: x 272, y 312.356 + 23.2296 i for 4 items, 298.667 + ... for 5; 296 x 72 px; the port's six rows: <see cref="SixRowTop"/> + <see cref="SixRowPitch"/> i).</summary>
    public static RectF ItemRect(int i, int n)
    {
        if (n >= 6)
        {
            return new RectF(272f * UiMetrics.UnitX, (SixRowTop + (i * SixRowPitch)) * UiMetrics.UnitY, 296f, 72f);
        }

        float y0 = n >= 5 ? 298.66666f : 312.35553f;
        return new RectF(272f * UiMetrics.UnitX, (y0 + (i * 23.22963f)) * UiMetrics.UnitY, 296f, 72f);
    }

    /// <summary>
    /// The first row of the port's six-row menu (CONTINUE, NEW GAME, LOAD GAME, CONFIG, GALLERY, EXIT), in units of the 640 x 448 canvas. The PS4's five rows start at
    /// 298.667 and are 23.23 units (56 px) apart; six rows at that pitch would run into the copyright, so package K18 kept the first row and closed the pitch to 21.2
    /// units (51.1 px), which left the letters of EXIT 11..13 px above the copyright's letters against 32..35 px between the letters of two items (measured on the
    /// renders, package K28). Package K28 raises the first row by 1.67 units (4 px) and closes the pitch to <see cref="SixRowPitch"/>: the letters of two items (rows
    /// 28..46 of a 72 px slot) are then 28..31 px apart, the letters of EXIT end 35 px above the copyright's first row (y 1035 on the canvas) and the top of CONTINUE
    /// (744) stays 33 px under the lowest row of the Japanese logo's subtitle "A piece of blue glass moon" (710; the English subtitle ends at 614).
    /// </summary>
    public const float SixRowTop = 297f;

    /// <summary>The pitch of the six-row menu in canvas units (47.25 px); see <see cref="SixRowTop"/>.</summary>
    public const float SixRowPitch = 19.6f;

    /// <summary>The first row of the letters of an item inside its 72 px slot (the art and the glyphs of EXIT alike; the letters end at row 46, inclusive).</summary>
    public const float ItemInkTop = 28f;

    /// <summary>The row of the canvas where the copyright's letters begin (measured on the renders of every language; the sprite itself starts at y 1008).</summary>
    public const float CopyrightInkTop = 1035f;

    private static RectF Cell(TitleItem item, int col)
    {
        float y = item switch { TitleItem.Continue => 376f, TitleItem.NewGame => 24f, TitleItem.Load => 112f, TitleItem.Config => 200f, _ => 288f };
        return new RectF(24f + (312f * col), y, 296f, 72f);
    }

    // ---- frame ----------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override void StepBackground() => Step(default);

    /// <inheritdoc/>
    public override void Step(in UiInput input)
    {
        _frame++;
        _clock += 1f / 60f;
        _pulse += 1f;
        _grow = MathF.Min(1f, _grow + 0.1f);
        ReclaimTitle(input);
        StepFx();
        StepMovie();
        switch (Phase)
        {
            case TitlePhase.Intro:
                StepIntro(input);
                return;
            case TitlePhase.Caution:
                StepCaution(input);
                return;
            case TitlePhase.WhiteOut:
                _white += 1f / 60f;
                if (_white > 1.2f)
                {
                    Begin(110);
                }

                return;
            case TitlePhase.Loading:
                StepLoading();
                return;
            case TitlePhase.Leaving:
                StepLeaving();
                return;
        }

        StepTitle(input);
    }

    private void Begin(int scene)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Ctx.Services.StartScene(scene);
        Root.Clear();
    }

    /// <summary>
    /// The title is the top screen with nothing covering it: the backdrop that a screen opened from the title (the load list, the config screen) left up fades out at 0.02
    /// per frame, as the PS4's title re-entry does (system state machine 0x68B7F, docs/formats/system_flow.md 4.5.1), and the logo and the menu come back behind it. Nothing
    /// took the backdrop down before package K14: <see cref="StepFx"/> counted it as a screen covering the title, the logo fell to nothing and the menu (which follows the logo)
    /// never returned; only the cursor moving lit them for a second. A pointer move anywhere, a click, a key or a pad button cuts the fade at once and shows them.
    /// Package K18 adds the PS4's idle behaviour: with no input at all for <see cref="IdleFrames"/> steps the logo and the menu fade out (the logo's fall of 1 / 60 per frame) and the
    /// same inputs bring them back and start the count again.
    /// </summary>
    private void ReclaimTitle(in UiInput input)
    {
        _wokeFromIdle = false;
        if (Root.Top != this || Phase is not (TitlePhase.Idle or TitlePhase.FadeIn))
        {
            // another screen, the quit question or a story on the way: the idle clock starts again when the title is back
            _idleFrames = 0;
            _idleHidden = false;
            return;
        }

        UiBackdrop backdrop = Ctx.Backdrop;
        if (backdrop is { Visible: true, Speed: >= 0f })
        {
            backdrop.Fade(-0.02f);
        }

        bool touched = input.PointerMoved || input.Any || input.Up || input.Down || input.Left || input.Right || input.Wheel != 0;
        if (touched)
        {
            // any input restarts the idle clock; one that finds the logo and menu faded out only brings them back (below), it does not also choose the item under the cursor
            _wokeFromIdle = _idleHidden;
            _idleFrames = 0;
            _idleHidden = false;
        }
        else if (Phase == TitlePhase.Idle && !PressMode && _menuAlpha >= 1f && ++_idleFrames >= IdleFrames)
        {
            _idleHidden = true;
        }

        if (touched && Phase == TitlePhase.Idle && _menuAlpha >= 1f)
        {
            if (backdrop.Visible)
            {
                backdrop.Reset();
            }

            _logo = 1f;
            _logoRising = false;
        }
    }

    private void StepFx()
    {
        // the logo alpha (docs 4.5.1): rises at 1/120 per frame while the title is up, falls at 1/60 while a screen covers it (and, package K18, while the idle fade-out
        // holds it down; the quit question over the title does not cover it)
        bool covered = (Root.Top != this && Root.Top != _quitDialog) || Ctx.Backdrop.Visible || _idleHidden;
        if (Phase is TitlePhase.Idle or TitlePhase.FadeIn && _black < 0.5f)
        {
            _logoRising = !covered;
            _logo = Math.Clamp(_logo + (covered ? -1f / 60f : 1f / 120f), 0f, 1f);
        }

        UpdateParticles();
    }

    private void StepMovie()
    {
        if (!_movieStarted)
        {
            return;
        }

        double t = Ctx.Services.TitleMovieTime;
        if (t > LoopPoint)
        {
            _passed = true;
            _seekDue = true;
        }
        else if (t >= 0 && t < LoopPoint && _passed && _seekDue)
        {
            Ctx.Services.SeekTitleMovie(LoopPoint);
            _seekDue = false;
        }
    }

    private void StepTitle(in UiInput input)
    {
        if (Phase == TitlePhase.FadeIn)
        {
            // state 1: the black cover falls over 121 frames (+1/120 per frame, strict end)
            _black = MathF.Max(0f, _black - (1f / 120f));
            if (_black <= 0f)
            {
                Phase = TitlePhase.Idle;
            }
        }

        // the menu alpha follows the uncovered part of the screen: max(menu_alpha, 1 - black)
        _menuAlpha = MathF.Min(1f, MathF.Max(_menuAlpha, 1f - _black));
        if (Root.Top != this)
        {
            return;
        }

        if (input.Cancel && _menuAlpha >= 1f && !_wokeFromIdle)
        {
            // Esc, a right click or the pad's cancel ask the EXIT question too (package K18), also over PRESS ANY BUTTON, where every other button goes on
            AskToQuit();
            return;
        }

        if (PressMode)
        {
            if (input.Any && _menuAlpha >= 1f)
            {
                _logo = 1f;
                Ctx.Sound(UiSound.Start);
                Ctx.Services.FadeOutBgm(1.5f);
                Phase = TitlePhase.Caution;
                _cautionIdx = 0;
                _cautionStep = 0;
                _cautionT = 0;
                _cover = 0f;
            }

            return;
        }

        if (_menuAlpha < 1f)
        {
            return;
        }

        int before = _items.Cursor;
        if (input.Up)
        {
            _items.Move(-1);
        }
        else if (input.Down)
        {
            _items.Move(1);
        }

        _items.Hover(input);
        if (_items.Cursor != before)
        {
            _grow = 0f;
            _logo = 1f;
            _logoRising = false;
            Ctx.Sound(UiSound.Cursor);
        }

        if (_wokeFromIdle)
        {
            return;                 // the press that brought the faded-out menu back is not an answer to it (package K18)
        }

        int hit = _items.Clicked(input);
        if (hit >= 0)
        {
            _items.Cursor = hit;
            Choose(_entries[hit]);
        }
        else if (input.Decide && !input.Click && _items.Cursor >= 0)
        {
            Choose(_entries[_items.Cursor]);
        }
    }

    private void Choose(TitleItem item)
    {
        IGameServices s = Ctx.Services;
        _logo = 1f;
        switch (item)
        {
            case TitleItem.Continue:
            {
                int slot = s.Title.ContinueSlot;
                if (slot < 0)
                {
                    Ctx.Sound(UiSound.Buzzer);
                    _popup = Root.Push(new MessagePopup(Ctx.Sysmes(193), 480f));
                    return;
                }

                Ctx.Sound(UiSound.Decide);
                _loadSlot = slot;
                _popup = Root.Push(new MessagePopup(Ctx.Sysmes(184), held: true));
                Phase = TitlePhase.Loading;
                break;
            }

            case TitleItem.NewGame:
                Ctx.Sound(UiSound.Start);
                s.FadeOutBgm(1.5f);
                if (s.Title.PrologueSeen)
                {
                    Phase = TitlePhase.WhiteOut;
                    _white = 0f;
                }
                else
                {
                    Begin(260);
                }

                break;
            case TitleItem.Load:
                Ctx.Sound(UiSound.Decide);
                Root.Push(new SaveLoadScreen(save: false));
                break;
            case TitleItem.Config:
                Ctx.Sound(UiSound.Decide);
                Root.Push(new ConfigScreen());
                break;
            case TitleItem.Gallery:
                Ctx.Sound(UiSound.Decide);
                Ctx.Backdrop.Fade(0.02f);
                Phase = TitlePhase.Leaving;
                _loadSlot = -2;
                break;
            case TitleItem.Exit:
                AskToQuit();
                break;
        }
    }

    /// <summary>
    /// The EXIT question (package K18): the PS4's own "Are you sure you want to quit without saving?" (ui_text 236, the table's dialog 14), Yes / No with the cursor on No.
    /// Yes asks the host to end the run; the title keeps its logo and menu behind the dialog.
    /// </summary>
    private void AskToQuit()
    {
        if (_quitDialog is { Finished: false } || _started)
        {
            return;
        }

        Ctx.Sound(UiSound.Open);
        _quitDialog = Root.Push(new ConfirmDialog(QuitMessage, true, yes =>
        {
            if (yes)
            {
                Ctx.Services.RequestQuit();
            }
        }));
    }

    /// <summary>The <c>ui_text</c> id of the quit question: "Are you sure you want to quit without saving?" (ja: セーブせずに終了してよろしいですか？).</summary>
    public const int QuitMessage = 236;

    /// <summary>A save was picked in the load list opened over the title (title case 32, result 2): the BGM fades out, the UI hides and the screen goes black before the load.</summary>
    public void BeginLoad(int slot)
    {
        Ctx.Services.FadeOutBgm(1.5f);
        _loadSlot = slot;
        _black = 0f;
        Phase = TitlePhase.Leaving;
    }

    /// <summary>The title waits behind the gallery: its movie stops (the gallery has its own BGM handling) and it stays black until the gallery closes (<see cref="ReturnFromGallery"/>).</summary>
    private void EnterGallery(bool afterReplay)
    {
        Ctx.Services.StopTitleMovie();
        _movieStarted = false;
        _passed = false;
        _seekDue = false;
        Phase = TitlePhase.Idle;
        _started = false;
        _loadSlot = -1;
        _black = 1f;
        _menuAlpha = 0f;
        Root.Push(new GalleryScreen(afterReplay));
    }

    /// <summary>The host reopens the gallery after a replay: the title (restarted by <see cref="UiRoot.ReturnToGallery"/>) goes behind it at once.</summary>
    public void ResumeInGallery() => EnterGallery(afterReplay: true);

    /// <summary>The gallery closed (sub_8D2E0): the title restarts from black with the cursor on GALLERY, the backdrop fading out over it.</summary>
    public void ReturnFromGallery()
    {
        Ctx.Backdrop.Fade(-0.02f);
        BeginTitle();
        _menuAlpha = 0f;
        int i = Array.IndexOf(_entries, TitleItem.Gallery);
        if (i >= 0)
        {
            _items.Cursor = i;
        }
    }

    private void StepLoading()
    {
        if (_popup is { FullyShown: true })
        {
            _popup.Release();
            _popup = null;
            Ctx.Services.FadeOutBgm(1.5f);
            _black = 0f;
            Phase = TitlePhase.Leaving;
        }
    }

    private void StepLeaving()
    {
        if (_started)
        {
            return;
        }

        // state 30 / 0x17: black rises to 1.2 (+0.01 per frame), then the game takes over
        _black += _loadSlot == -2 ? 0.02f : 0.01f;
        if (_black < 1.2f)
        {
            return;
        }

        _started = true;
        if (_loadSlot == -2)
        {
            // the host is told (it may stop the title's movie); the gallery screen (package K5b) opens over the title
            Ctx.Services.OpenGallery();
            if (Ctx.Assets.Gallery is not null)
            {
                EnterGallery(afterReplay: false);
            }

            return;
        }

        if (Ctx.Services.Load(_loadSlot))
        {
            Ctx.Services.ResumeLoaded();
            Root.Clear();
        }
        else
        {
            _started = false;
            _black = 0f;
            Phase = TitlePhase.Idle;
            _popup = Root.Push(new MessagePopup(Ctx.Sysmes(190), 480f));
        }
    }

    // ---- the returning player's intro -----------------------------------------------------------------------------------------

    private void StepIntro(in UiInput input)
    {
        IGameServices s = Ctx.Services;
        switch (_introStep)
        {
            case 0:
                // white cover in (61 frames), then the TM_CI movie
                _white = MathF.Min(1.2f, _white + (1f / 60f));
                if (++_introT >= 61)
                {
                    s.StartIntroMovie(38);
                    _introStep = 1;
                    _introT = 0;
                    _white = 1f;
                }

                break;
            case 1:
                _introT++;
                if (_introT > 30)
                {
                    _white = MathF.Max(0f, _white - (1f / 60f));
                }

                if (input.Any && _introT > 120 && s.IntroMoviePlaying)
                {
                    s.StopIntroMovie();
                }

                if (!s.IntroMoviePlaying && _introT > 2)
                {
                    _introStep = 2;
                    _introT = 0;
                    _cautionA = 0f;
                }

                break;
            case 2:
                // caution2: it fades in (0.025 per frame), holds up to 480 frames (a button skips at once), fades out
                _cautionA = MathF.Min(1f, _cautionA + 0.025f);
                if (_cautionA >= 1f)
                {
                    _introT++;
                }

                if (_cautionA >= 1f && (_introT >= 480 || input.Any))
                {
                    _introStep = 3;
                }

                break;
            case 3:
                _cautionA -= 0.025f;
                if (_cautionA <= 0f)
                {
                    Ctx.TitleIntroShown = true;
                    _white = 0f;
                    BeginTitle();
                }

                break;
        }
    }

    // ---- first-run cautions ----------------------------------------------------------------------------------------------------

    private void StepCaution(in UiInput input)
    {
        switch (_cautionStep)
        {
            case 0:
                _cover = MathF.Min(1f, _cover + (1f / 60f));
                if (_cover >= 1f)
                {
                    _cautionStep = 1;
                }

                break;
            case 1:
                _cover = MathF.Max(0f, _cover - (1f / 60f));
                if (_cover <= 0f)
                {
                    _cautionStep = 2;
                    _cautionT = 0;
                }

                break;
            case 2:
                // the countdown holds it; input is ignored while the old value is above 480, so the hold is 241..720 frames
                _cautionT++;
                if (_cautionT >= 720 || (_cautionT > 240 && input.Any))
                {
                    _cautionStep = 3;
                }

                break;
            case 3:
                _cover = MathF.Min(1f, _cover + (1f / 60f));
                if (_cover >= 1f)
                {
                    if (_cautionIdx == 0)
                    {
                        _cautionIdx = 1;
                        _cautionStep = 1;
                    }
                    else
                    {
                        Phase = TitlePhase.WhiteOut;
                        _white = 0f;
                    }
                }

                break;
        }
    }

    // ---- particles (knife LogoFx, sub_905B0 / sub_90D00) ------------------------------------------------------------------------

    private readonly record struct Shard(int Kind, float Scale, float Life, float Age, float Vx, float Vy, float Alpha, float Angle, float Spin, float X, float Y);

    private readonly record struct Dot(int Kind, float Alpha, float Life, float Age, float X, float Y);

    private (float Cy, float W, float H, float Dw, float Dy) Spawn() => Ctx.Language switch
    {
        Language.En => (260.25f, 675f, 50f, 675f, 319.375f),
        Language.Zc or Language.Zt => (334.5f, 135f, 75f, 150f, 395.375f),
        _ => (340.5f, 135f, 75f, 150f, 399.38f),
    };

    private float ClipY => Ctx.Language == Language.En ? 519f : 680f;

    private void UpdateParticles()
    {
        if (Phase is not (TitlePhase.Idle or TitlePhase.FadeIn))
        {
            return;
        }

        UiRandom r = Ctx.Random;
        (float cy, float sw, float sh, float dw, float dy) = Spawn();
        _shardAcc += 1f;
        while (_shardAcc >= 2f)
        {
            _shardAcc -= 2f;
            if (_shards.Count >= 120)
            {
                continue;
            }

            float vy = (r.Next() * 0.45f) + 0.13333f;
            float dir = MathF.Cos(((r.Next() * 20f) + 80f) * MathF.PI / 180f);
            _shards.Add(new Shard(
                r.Int(0, 2), r.Range(0.3f, 0.75f), MathF.Floor((r.Next() * 150f) + 120f), 0f, vy * dir, vy, (r.Next() * 0.5f) + 0.5f,
                r.Next() * 640f * MathF.PI / 180f, ((r.Next() * 2.5f) + 0.5f) * MathF.PI / 180f, 480f - (sw * 0.5f) + (sw * r.Next()), cy - (sh * 0.5f) + (sh * r.Next())));
        }

        for (int k = 0; k < 4 && _dots.Count < 200; k++)
        {
            _dots.Add(new Dot(r.Int(0, 2), r.Range(0.25f, 1f), MathF.Floor((r.Next() * 42f) + 18f), 0f, (dw * -0.5f) + 480f + (dw * r.Next()), dy + (r.Next() * -215.25f)));
        }

        for (int i = 0; i < _shards.Count; i++)
        {
            Shard s = _shards[i];
            _shards[i] = s with { X = s.X + s.Vx, Y = s.Y - s.Vy, Angle = s.Angle + s.Spin, Age = s.Age + 1f };
        }

        _shards.RemoveAll(s => s.Age > s.Life);
        for (int i = 0; i < _dots.Count; i++)
        {
            _dots[i] = _dots[i] with { Age = _dots[i].Age + 1f };
        }

        _dots.RemoveAll(d => d.Age > d.Life);
    }

    private void DrawParticles(UiPainter p, float a)
    {
        if (a <= 0f)
        {
            return;
        }

        float[] shardW = [33f, 32f, 29f];
        float[] shardH = [44f, 55f, 45f];
        int[][] dotAt = [[15, 20], [14, 26], [13, 21]];
        using UiPainter.ClipScope clip = p.WithClip(new RectF(0, 0, UiMetrics.Width, ClipY));
        foreach (Dot d in _dots)
        {
            float t = 2f * d.Age / d.Life;
            t = t > 1f ? 2f - t : t;
            int[] at = dotAt[d.Kind];
            p.Sprite(UiSheets.TitleShard(d.Kind), new RectF(at[0], at[1], 1, 1), new RectF((d.X * 2f) - 0.5f, (d.Y * 2f) - 0.5f, 1, 1), a * d.Alpha * t);
        }

        foreach (Shard s in _shards)
        {
            (float k, float dim) = UiBackdrop.Phase(s.Age, s.Life);
            float w = shardW[s.Kind] * s.Scale * k * MathF.Cos(s.Angle);
            float h = shardH[s.Kind] * s.Scale * k;
            p.Sprite(
                UiSheets.TitleShard(s.Kind), new RectF(0, 0, shardW[s.Kind], shardH[s.Kind]),
                new RectF((s.X * 2f) - (MathF.Abs(w) / 2f), (s.Y * 2f) - (h / 2f), MathF.Abs(w), h), a * s.Alpha * dim, UiBlend.Alpha, flipX: w < 0f);
        }
    }

    // ---- drawing --------------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override void Draw(UiPainter p)
    {
        if (Phase == TitlePhase.Leaving && _started && !Ctx.Services.Title.PrologueSeen)
        {
            p.Cover(1f);
            return;
        }

        if (Phase == TitlePhase.Intro)
        {
            DrawIntro(p);
            return;
        }

        if (Phase == TitlePhase.Caution)
        {
            DrawTitle(p);
            DrawCaution(p);
            return;
        }

        DrawTitle(p);
        if (Phase == TitlePhase.WhiteOut)
        {
            p.Cover(MathF.Min(1f, _white), 1f);
        }
        else if (_black > 0f)
        {
            p.Cover(MathF.Min(1f, _black));
        }
    }

    private void DrawIntro(UiPainter p)
    {
        p.Cover(1f, _introStep >= 2 ? 0f : 1f);
        if (_introStep == 1 && Ctx.Services.IntroMoviePlaying)
        {
            p.Quad(TextureRef.ForMovieFrame(Ctx.Services.IntroMovieSlot), 0, 0, ScreenW, UiMetrics.Height, 0, 0, 1, 1, UiPainter.Pack(1f));
            p.Cover(MathF.Min(1f, _white), 1f);
        }
        else if (_introStep == 0)
        {
            p.Cover(MathF.Min(1f, _white), 1f);
        }
        else if (_introStep >= 2)
        {
            DrawCautionPicture(p, "caution2", _cautionA);
        }
    }

    private void DrawCaution(UiPainter p)
    {
        p.Cover(_cover);
        if (_cautionStep is 1 or 2 or 3)
        {
            p.Cover(1f);
            DrawCautionPicture(p, _cautionIdx == 0 ? "caution" : "caution1", 1f);
            p.Cover(_cover);
        }
    }

    private void DrawCautionPicture(UiPainter p, string kind, float alpha)
    {
        string[] suffix = ["ja", "en", "zc", "zt"];
        string lang = suffix[(int)Ctx.Language < 4 ? (int)Ctx.Language : 1];
        // caution_xx has an explicit _ja; caution1 and caution2 name the Japanese file without a suffix (docs/formats/archives_ui_movies.md 5.4)
        string name = kind == "caution" ? $"caution_{lang}.mzp" : Ctx.Language == Language.Ja ? $"{kind}.mzp" : $"{kind}_{lang}.mzp";
        (int Archive, int Entry)? found = Ctx.Assets.Tables.FindPicture(name);
        if (found is { } f)
        {
            p.Picture(new UiPicture(f.Archive, f.Entry), UiMetrics.Screen, alpha);
        }
    }

    private void DrawTitle(UiPainter p)
    {
        IGameServices s = Ctx.Services;
        bool en = Ctx.Language == Language.En;
        double t = s.TitleMovieTime;
        bool movie = _movieStarted && t >= 0 && (t >= LoopPoint || !_passed);
        if (movie)
        {
            p.Quad(TextureRef.ForMovieFrame(s.TitleMovieSlot), 0, 0, ScreenW, UiMetrics.Height, 0, 0, 1, 1, UiPainter.Pack(1f));
        }
        else if (PressMode)
        {
            // child 9 (2670 x 1266) at (-187.5, -55.4074) of 960 x 544 = (-375, -110) px
            p.Sprite(UiSheets.TitleBg0, new RectF(0, 0, 2670, 1266), new RectF(-375f, -110f, 2670, 1266));
        }
        else
        {
            p.Sprite(Still, UiMetrics.Screen);
        }

        float la = _logo;
        float hi = _logoRising ? la * la * la : la;
        if (Phase is TitlePhase.Idle or TitlePhase.FadeIn or TitlePhase.Caution or TitlePhase.Loading or TitlePhase.Leaving or TitlePhase.WhiteOut)
        {
            DrawParticles(p, hi);
            // logo bands (docs 4.5 rows 2..4): ja (680, 72) 560 wide, en (35, 222) 1850 wide
            (float lx, float ly) = en ? (35f, 222f) : (680f, 72f);
            (int, int, int)[] bands = en ? [(0, 1, 1), (1, 330, 1850), (330, 440, 1850)] : [(0, 285, 560), (285, 570, 560), (570, 648, 560)];
            for (int k = 0; k < 3; k++)
            {
                (int y0, int y1, int w) = bands[k];
                float alpha = _logoRising ? MathF.Pow(la, k + 1) : la;
                p.Sprite(UiSheets.TitleLogo, new RectF(0, y0, w, y1 - y0), new RectF(lx, ly + y0, w, y1 - y0), alpha);
            }

            if (PressMode)
            {
                float blink = 0.1f + (0.9f * UiEase.Triangle(_pulse, 60f));
                if (p.PromptStyle == PromptStyle.Keyboard)
                {
                    UiPrompts.DrawPressAnyKey(p, _menuAlpha * blink);
                }
                else
                {
                    p.Sprite(UiSheets.PressAnyButton, new RectF(0, 0, 472, 88), new RectF(724f, 814.01f, 472, 88), _menuAlpha * blink);
                }
            }
            else
            {
                float glow = UiEase.Triangle(_clock * 0.5f);
                p.Sprite(UiSheets.TitleGlow, new RectF(0, 0, 488, 256), new RectF(720f, en ? 391f : 552f, 488, 256), hi * glow, UiBlend.Add);
                DrawMenu(p, hi);
            }
        }

        p.Sprite(UiSheets.TitleCopyright, new RectF(0, 0, 184, 64), new RectF(872f, 1008f, 184, 64));
        Ctx.Backdrop.Draw(p);
    }

    private void DrawMenu(UiPainter p, float lf)
    {
        int n = _entries.Length;
        float[] y5 = [383.5f, 411.5f, 439.5f, 467f, 495.5f];
        float[] y4 = [400f, 428f, 455.5f, 484f];
        for (int i = 0; i < n; i++)
        {
            RectF slot = ItemRect(i, n);
            bool hover = i == _items.Cursor;
            float alpha = Math.Clamp((2f * _menuAlpha) - ((i + 1f) / n), 0f, 1f) * lf;
            if (_entries[i] == TitleItem.Exit)
            {
                // package K33: EXIT is a 296 x 72 cell composed at run time from the letters of the other items (TitleExitArt), drawn like them; the glyph
                // font of K18 stays as the fallback for a sheet the composer does not recognise
                if (!p.Composed(hover ? UiComposedId.TitleExitCursor : UiComposedId.TitleExit, new RectF(0, 0, TitleExitArt.CellWidth, TitleExitArt.CellHeight), slot, alpha))
                {
                    DrawExitLabel(p, slot, hover, alpha);
                }
            }
            else
            {
                RectF src = Cell(_entries[i], hover ? 1 : 0);
                p.Sprite(UiSheets.TitleMenu, src, slot, alpha);
            }

            if (hover && _menuAlpha >= 1f)
            {
                // the cursor underline (row 6): 336 x 8 px grown 0 -> 1, centred at x 966, 2 px per unit of the 960 x 540 canvas; 47 px under the top of the item's slot
                float w = 336f * _grow;
                float y = n >= 6 ? slot.Y + 47f : (n >= 5 ? y5[i] : y4[Math.Min(i, 3)]) * 2f;
                p.Sprite(UiSheets.TitleUnderline, new RectF(24, 24, 336, 8), new RectF(966f - (w / 2f), y, w, 8), _grow * _menuAlpha * lf);
            }
        }
    }

    // package K18: the fallback EXIT, drawn with the game's glyph font when TitleExitArt cannot cut the letters out of the player's TITLE_PARTS (package K33 draws the
    // composed cell otherwise). The look of the neighbours' art (measured on child 0 of TITLE_PARTS, 296 x 72 cells): capitals 19 px high, 11 to 12 px between
    // letters, white with a soft light-blue glow, and (cursor column) cyan (17, 222, 248) with a faint cyan glow; the text is centred in the cell, its top at row 28
    // and its baseline at row 46.
    private static readonly (int R, int G, int B) ExitWhite = (255, 255, 255);
    private static readonly (int R, int G, int B) ExitWhiteGlow = (192, 238, 254);
    private static readonly (int R, int G, int B) ExitCyan = (17, 222, 248);
    private static readonly (int R, int G, int B) ExitCyanGlow = (2, 221, 255);

    private void DrawExitLabel(UiPainter p, RectF slot, bool hover, float alpha)
    {
        if (alpha <= 0f)
        {
            return;
        }

        TextStyle style = ExitStyle(Ctx.Language);
        Ctx.Text.DrawGlowLabel(
            p, "EXIT", style, slot.X + (slot.W / 2f), slot.Y + ExitTop(Ctx.Language),
            hover ? ExitCyan : ExitWhite, hover ? ExitCyanGlow : ExitWhiteGlow, alpha, hover ? 0.014f : 0.03f);
    }

    /// <summary>The glyph style of EXIT: square glyphs, 2 units (6 px) of extra advance between the letters.</summary>
    private static TextStyle ExitStyle(Language language) => TextStyle.Menu(ExitScale(language)) with { Width = 600, Height = 100, Spacing = 2, Leading = 0 };

    private static double ExitScale(Language language) => language is Language.Zc or Language.Zt ? 0.395 : 0.416;

    private static float ExitTop(Language language) => language is Language.Zc or Language.Zt ? 23f : 24f;
}
