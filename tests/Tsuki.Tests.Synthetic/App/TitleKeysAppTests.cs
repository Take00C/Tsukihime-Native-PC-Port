using Tsuki.App.Play;
using Tsuki.Engine.Contracts;
using Tsuki.Platform.Input;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K14: the title in the real app, driven the way a player drives it and with no pointer at all: the boot notice, the intro, then the title whose logo and
/// menu must be on the screen without any mouse movement, the keys (arrows, and W S A D in the menus) moving the cursor, and a screen opened from the title
/// (config, load list) leaving nothing behind that hides them. The user's report: after the config screen the title showed only its background, the logo and
/// the menu appeared only while the pointer moved over an item. Every frame goes through <see cref="InputMapper"/> and <see cref="AppRuntime"/>, as in the app.
/// </summary>
public sealed class TitleKeysAppTests
{
    private sealed class Rig
    {
        private readonly InputMapper _mapper = new();
        private readonly DisplayList _list = new(4096);
        private long _n;

        public Rig(bool withSave = false)
        {
            Game = new AppRig();
            Game.Services.SetGlobal(110 + (485 >> 4), (ushort)(1 << (485 & 15)));       // the prologue was read
            Game.Services.SetGlobal(10, 1);                                             // a story was started: the menu, not PRESS ANY BUTTON
            if (withSave)
            {
                Game.Begin();
                Game.Services.Save(6);
                Game.Services.ReturnToTitle();
            }

            Ui = new UiRoot(UiTestKit.Data(), Game.Services);
            Runtime = new AppRuntime(Game.Services, Ui, null, null);
            Runtime.Start();
        }

        public AppRig Game { get; }

        public UiRoot Ui { get; }

        public AppRuntime Runtime { get; }

        public TitleScreen? Title => Ui.Find<TitleScreen>();

        /// <summary>One step with the keys down and the pointer where it is (<see cref="PointerSample"/> default: outside the window, no pointer).</summary>
        public DisplayList Step(KeySet keys = default, PointerSample pointer = default)
        {
            InputFrame f = _mapper.Map(++_n, keys, default, pointer);
            Runtime.Step(f, _n * 16_666_667L, _list);
            return _list;
        }

        public void Run(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                Step();
            }
        }

        public void Tap(PcKey key)
        {
            Step(KeySet.Of(key));
            Step();
        }

        /// <summary>Presses Enter every 30 steps (two steps down) until <paramref name="done"/>: the boot notice, the intro movie and the caution are all skipped by a button.</summary>
        public void TapUntil(Func<bool> done, int limit = 3000)
        {
            for (int i = 0; i < limit && !done(); i++)
            {
                Step(i % 30 < 2 ? KeySet.Of(PcKey.Enter) : KeySet.None);
            }

            Assert.True(done());
        }

        public void ToIdleAndSettled()
        {
            TapUntil(() => Title is { Phase: not TitlePhase.Intro });      // boot notice and intro by keys: the title is fading in, nothing pressed since
            for (int i = 0; i < 600 && Title is not { Idle: true }; i++)
            {
                Step();
            }

            Assert.True(Title!.Idle);
            Run(220);                                                       // the logo rises over 121 frames once the cover is half gone
        }

        /// <summary>CONFIG (or LOAD GAME) by keys, 90 steps in it, then Esc and back on the title (no key since).</summary>
        public void ThroughAScreenAndBack(int downs)
        {
            for (int i = 0; i < downs; i++)
            {
                Tap(PcKey.Down);
            }

            Tap(PcKey.Enter);
            Assert.True(Ui.Top is ConfigScreen or SaveLoadScreen);
            Run(90);
            Assert.True(Ui.Context.Backdrop.Visible);                       // the screens fade the backdrop in over the title
            Tap(PcKey.Escape);
            for (int i = 0; i < 120 && Ui.Top is not TitleScreen; i++)
            {
                Step();
            }

            Assert.IsType<TitleScreen>(Ui.Top);
        }
    }

    private static float Alpha(in DrawCmd c) => (c.V0.Rgba >> 24) / 255f;

    private static DrawCmd[] TitleQuads(DisplayList list, int child) =>
        [.. list.Commands.ToArray().Where(c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts } t && t.C == child)];

    private static void AssertLogoAndMenuShow(DisplayList list, int items)
    {
        DrawCmd[] logo = TitleQuads(list, 1);
        Assert.NotEmpty(logo);
        Assert.All(logo, c => Assert.True(Alpha(c) > 0.98f, $"logo band alpha {Alpha(c)}"));
        DrawCmd[] menu = TitleQuads(list, 0);
        Assert.Equal(items, menu.Length);
        Assert.All(menu, c => Assert.True(Alpha(c) > 0.98f, $"menu item alpha {Alpha(c)}"));
        Assert.NotEmpty(TitleQuads(list, 4));       // the moon glow
    }

    [Fact]
    public void TheTitleShowsItsLogoAndMenuWithNoPointerAndTheKeysDriveIt()
    {
        var rig = new Rig();
        rig.ToIdleAndSettled();
        Assert.False(rig.Ui.Context.Backdrop.Visible);
        AssertLogoAndMenuShow(rig.Step(), 4);
        Assert.Equal(TitleItem.NewGame, rig.Title!.Cursor);               // no save: the cursor starts on NEW GAME

        rig.Tap(PcKey.Down);
        Assert.Equal(TitleItem.Load, rig.Title.Cursor);
        rig.Tap(PcKey.S);                                                 // S is down in the menus
        Assert.Equal(TitleItem.Config, rig.Title.Cursor);
        rig.Tap(PcKey.W);
        Assert.Equal(TitleItem.Load, rig.Title.Cursor);
        rig.Tap(PcKey.Up);
        Assert.Equal(TitleItem.NewGame, rig.Title.Cursor);
        rig.Tap(PcKey.D);                                                 // D and A are right and left: the title menu has no use for them, the cursor stays
        rig.Tap(PcKey.A);
        Assert.Equal(TitleItem.NewGame, rig.Title.Cursor);
        AssertLogoAndMenuShow(rig.Step(), 4);

        rig.Tap(PcKey.Down);
        rig.Tap(PcKey.Down);                                              // CONFIG
        rig.Tap(PcKey.Enter);
        Assert.IsType<ConfigScreen>(rig.Ui.Top);
    }

    [Fact]
    public void TheCursorStartsOnContinueWhenThereIsASaveAndEnterTakesIt()
    {
        var rig = new Rig(withSave: true);
        rig.ToIdleAndSettled();
        Assert.Equal(TitleItem.Continue, rig.Title!.Cursor);
        AssertLogoAndMenuShow(rig.Step(), 5);
        rig.Tap(PcKey.Enter);
        Assert.Equal(TitlePhase.Loading, rig.Title.Phase);
    }

    [Theory]
    [InlineData(2)]       // CONFIG (the cursor starts on NEW GAME)
    [InlineData(1)]       // LOAD GAME
    public void AScreenOpenedFromTheTitleLeavesNothingThatHidesTheLogoAndMenu(int downs)
    {
        var rig = new Rig();
        rig.ToIdleAndSettled();
        rig.ThroughAScreenAndBack(downs);

        // no key and no pointer from here: the backdrop must go and the logo and menu come back by themselves (before package K14 they never did)
        rig.Run(400);
        Assert.False(rig.Ui.Context.Backdrop.Visible);
        AssertLogoAndMenuShow(rig.Step(), 4);
    }

    [Fact]
    public void AnyKeyBringsTheLogoAndMenuBackAtOnceAfterAScreenClosed()
    {
        var rig = new Rig();
        rig.ToIdleAndSettled();
        rig.ThroughAScreenAndBack(2);
        rig.Run(3);
        DrawCmd[] hidden = TitleQuads(rig.Step(), 0);
        Assert.True(hidden.Length == 0 || hidden.All(c => Alpha(c) < 0.5f));        // still faded: the backdrop is on its way out
        Assert.True(rig.Ui.Context.Backdrop.Visible);

        rig.Step(KeySet.Of(PcKey.W));                                     // a key: logo and menu at full strength at once, backdrop gone
        Assert.False(rig.Ui.Context.Backdrop.Visible);
        AssertLogoAndMenuShow(rig.Step(), 4);
    }

    [Fact]
    public void AKeyTheGameBindsToNothingEndsTheBootNoticeAndWakesTheTitleToo()
    {
        var rig = new Rig();
        for (int i = 0; i < 200; i++)
        {
            rig.Step();
        }

        Assert.IsType<BootScreen>(rig.Ui.Top);
        rig.Step(KeySet.Of(PcKey.Other));                                 // G, Shift, F1: the notice ends on any key
        rig.Run(200);
        Assert.False(rig.Ui.Top is BootScreen);

        rig.ToIdleAndSettled();
        rig.ThroughAScreenAndBack(2);
        rig.Run(3);
        Assert.True(rig.Ui.Context.Backdrop.Visible);
        rig.Step(KeySet.Of(PcKey.Other));
        Assert.False(rig.Ui.Context.Backdrop.Visible);
        AssertLogoAndMenuShow(rig.Step(), 4);
        Assert.Equal(TitleItem.Config, rig.Title!.Cursor);                // the key moved no cursor and decided nothing
    }

    [Fact]
    public void AnyPointerMoveAnywhereBringsTheLogoAndMenuBackAtOnce()
    {
        var rig = new Rig();
        rig.ToIdleAndSettled();
        rig.ThroughAScreenAndBack(2);
        rig.Run(3);
        Assert.True(rig.Ui.Context.Backdrop.Visible);

        // the pointer comes into the window far from every menu item, and moves a little: that is all it takes
        static PointerSample At(float x, float y) => new((short)(x * 640f / 1920f), (short)(y * 448f / 1080f), true, PointerButtons.None, 0, x, y);
        rig.Step(KeySet.None, At(200f, 200f));
        rig.Step(KeySet.None, At(230f, 210f));
        Assert.False(rig.Ui.Context.Backdrop.Visible);
        AssertLogoAndMenuShow(rig.Step(KeySet.None, At(230f, 210f)), 4);
    }

    [Fact]
    public void AStaleBackdropIsNotLeftUnderANewTitle()
    {
        var rig = new Rig();
        rig.Ui.Context.Backdrop.Fade(0.0555f);                            // what a screen that never closed leaves up when the title comes back
        for (int i = 0; i < 40; i++)
        {
            rig.Ui.Context.Backdrop.Update();
        }

        Assert.True(rig.Ui.Context.Backdrop.Visible);
        rig.ToIdleAndSettled();
        Assert.False(rig.Ui.Context.Backdrop.Visible);
        AssertLogoAndMenuShow(rig.Step(), 4);
    }

    // -- package K18: no input at all, and EXIT ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 5)]
    public void TheBootNoticeTheIntroAndTheTitleShowTheLogoAndMenuByThemselvesWithNoKeyAndNoPointerAtAll(bool withSave, int sheetItems)
    {
        var rig = new Rig(withSave);
        rig.Run(3200);                                                    // the notice, the intro (the first title of the run), the cover falling away
        Assert.IsType<TitleScreen>(rig.Ui.Top);
        Assert.True(rig.Title!.Idle);
        for (int i = 0; i < 3; i++)
        {
            rig.Run(i == 0 ? 200 : i == 1 ? 160 : 240);                   // 3.3 s, 6 s and 10 s after the title was fully up
            Assert.False(rig.Ui.Context.Backdrop.Visible);
            AssertLogoAndMenuShow(rig.Step(), sheetItems);
        }
    }

    [Theory]
    [InlineData(2)]       // CONFIG
    [InlineData(1)]       // LOAD GAME
    public void AfterAScreenClosedTheLogoAndMenuAreBackAfterThreeSecondsWithNoInputAtAll(int downs)
    {
        var rig = new Rig();
        rig.ToIdleAndSettled();
        rig.ThroughAScreenAndBack(downs);
        rig.Run(200);                                                     // 3.3 s: no key, no pointer since the Esc
        Assert.False(rig.Ui.Context.Backdrop.Visible);
        AssertLogoAndMenuShow(rig.Step(), 4);
        rig.Run(160);
        AssertLogoAndMenuShow(rig.Step(), 4);
        rig.Run(240);
        AssertLogoAndMenuShow(rig.Step(), 4);
    }

    [Fact]
    public void ExitIsReachedByUpDownAndTheLettersAndYesEndsTheRunLikeTheWindowsCloseButton()
    {
        var rig = new Rig();
        rig.ToIdleAndSettled();
        Assert.Equal(TitleItem.NewGame, rig.Title!.Cursor);               // no save: NEW GAME, LOAD GAME, CONFIG, GALLERY, EXIT
        rig.Tap(PcKey.Up);                                                // wraps to the last item
        Assert.Equal(TitleItem.Exit, rig.Title.Cursor);
        rig.Tap(PcKey.W);
        Assert.Equal(TitleItem.Gallery, rig.Title.Cursor);
        rig.Tap(PcKey.S);
        Assert.Equal(TitleItem.Exit, rig.Title.Cursor);

        Assert.False(rig.Runtime.ExitRequested);
        rig.Tap(PcKey.Enter);
        var dialog = Assert.IsType<ConfirmDialog>(rig.Ui.Top);
        rig.Run(40);
        Assert.False(dialog.CursorOnYes);
        Assert.False(rig.Runtime.ExitRequested);
        rig.Tap(PcKey.Left);                                              // to Yes
        rig.Tap(PcKey.Enter);
        rig.Run(60);
        Assert.True(rig.Runtime.ExitRequested);                           // GameApp ends the loop on this, as it does on the window's close button
        Assert.True(rig.Game.Services.QuitRequested);
    }

    [Fact]
    public void EscapeOnTheTitleAsksToQuitWithoutOpeningTheSystemMenuAndNoLeavesTheGameRunning()
    {
        var rig = new Rig(withSave: true);
        rig.ToIdleAndSettled();
        rig.Tap(PcKey.Escape);                                            // the key is both Cancel and the system menu's key
        var dialog = Assert.IsType<ConfirmDialog>(rig.Ui.Top);
        Assert.Equal(1, rig.Ui.Stack.Count(s => s is ConfirmDialog));
        Assert.DoesNotContain(rig.Ui.Stack, s => s is SystemMenuScreen);
        rig.Run(40);
        rig.Tap(PcKey.Enter);                                             // the cursor starts on No
        rig.Run(60);
        Assert.IsType<TitleScreen>(rig.Ui.Top);
        Assert.False(rig.Runtime.ExitRequested);
        Assert.False(dialog.Waiting);
        AssertLogoAndMenuShow(rig.Step(), 5);
    }

    [Fact]
    public void AfterTheIdleTimeTheLogoAndMenuAreGoneAndOnePointerMoveInTheWindowBringsThemBack()
    {
        var rig = new Rig();
        rig.ToIdleAndSettled();
        static PointerSample At(float x, float y) => new((short)(x * 640f / 1920f), (short)(y * 448f / 1080f), true, PointerButtons.None, 0, x, y);
        rig.Step(KeySet.None, At(200f, 200f));                            // a pointer in the window, then nothing at all for the idle time
        rig.Step(KeySet.None, At(230f, 210f));
        for (int i = 0; i < TitleScreen.IdleFrames - 100; i++)
        {
            rig.Step(KeySet.None, At(230f, 210f));                        // resting, not moving
        }

        AssertLogoAndMenuShow(rig.Step(KeySet.None, At(230f, 210f)), 4);
        for (int i = 0; i < 300; i++)
        {
            rig.Step(KeySet.None, At(230f, 210f));
        }

        Assert.True(rig.Title!.IdleHidden);
        Assert.DoesNotContain(TitleQuads(rig.Step(KeySet.None, At(230f, 210f)), 1), c => Alpha(c) > 0.01f);

        rig.Step(KeySet.None, At(260f, 230f));                            // one move
        Assert.False(rig.Title.IdleHidden);
        AssertLogoAndMenuShow(rig.Step(KeySet.None, At(260f, 230f)), 4);
    }
}
