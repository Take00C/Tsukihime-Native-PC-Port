using Tsuki.App.Play;
using Tsuki.Engine.Contracts;
using Tsuki.Platform.Input;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package K21: the window's close button and Alt+F4 ask the game's own quit question (ui_text 236, the K18 dialog) wherever the game is; No returns, Yes ends the run
/// without a new save, a second close request while the question is up quits at once, also when the question came from the title's EXIT. Every frame goes through
/// <see cref="InputMapper"/> and <see cref="AppRuntime"/>, as in the app.
/// </summary>
public sealed class CloseConfirmTests
{
    private sealed class Rig
    {
        private readonly InputMapper _mapper = new();
        private readonly DisplayList _list = new(4096);
        private long _n;

        public Rig()
        {
            Game = new AppRig();
            Game.Services.SetGlobal(110 + (485 >> 4), (ushort)(1 << (485 & 15)));       // the prologue was read
            Game.Services.SetGlobal(10, 1);                                             // a story was started: the menu, not PRESS ANY BUTTON
            Ui = new UiRoot(UiTestKit.Data(), Game.Services);
            Runtime = new AppRuntime(Game.Services, Ui, null, null);
            Runtime.Start();
        }

        public AppRig Game { get; }

        public UiRoot Ui { get; }

        public AppRuntime Runtime { get; }

        public TitleScreen? Title => Ui.Find<TitleScreen>();

        public void Step(KeySet keys = default)
        {
            InputFrame f = _mapper.Map(++_n, keys, default, default);
            Runtime.Step(f, _n * 16_666_667L, _list);
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

        public void ToTitle()
        {
            for (int i = 0; i < 3000 && Title is not { Idle: true }; i++)
            {
                Step(i % 30 < 2 ? KeySet.Of(PcKey.Enter) : KeySet.None);
            }

            Assert.True(Title!.Idle);
            Run(10);
        }

        public void ToStory()
        {
            ToTitle();
            Game.Services.StartScene(100);
            for (int i = 0; i < 400 && Game.Text != "alpha line"; i++)
            {
                Step();
            }

            Assert.Equal("alpha line", Game.Text);
            Assert.Equal(UiMode.Story, Ui.Mode);
        }

        public ConfirmDialog QuitDialog()
        {
            var d = Assert.IsType<ConfirmDialog>(Ui.Top);
            Assert.Equal(TitleScreen.QuitMessage, d.MessageId(Ui.Context.Assets));
            return d;
        }
    }

    [Fact]
    public void TheCloseRequestOnTheTitleAsksTheQuitQuestionAndNoReturnsToTheTitle()
    {
        var rig = new Rig();
        rig.ToTitle();
        Assert.False(rig.Runtime.RequestClose());
        rig.Run(20);                                                   // the dialog settles in over 15 frames before it takes input
        ConfirmDialog d = rig.QuitDialog();
        Assert.False(d.CursorOnYes);                                   // the cursor starts on No
        Assert.True(rig.Runtime.CloseDialogOpen);
        Assert.False(rig.Runtime.ExitRequested);

        rig.Tap(PcKey.Enter);                                          // No
        rig.Run(40);
        Assert.False(rig.Runtime.ExitRequested);
        Assert.False(rig.Runtime.CloseDialogOpen);
        Assert.IsType<TitleScreen>(rig.Ui.Top);
        Assert.False(rig.Runtime.CloseConfirmed);
    }

    [Fact]
    public void YesEndsTheRunAndASecondCloseWhileTheQuestionIsUpQuitsAtOnce()
    {
        var rig = new Rig();
        rig.ToTitle();
        rig.Runtime.RequestClose();
        rig.Run(20);
        rig.QuitDialog();
        rig.Tap(PcKey.Left);                                           // to Yes
        Assert.True(rig.QuitDialog().CursorOnYes);
        rig.Tap(PcKey.Enter);
        rig.Run(40);                                                   // the 15-frame hold, then the answer
        Assert.True(rig.Runtime.ExitRequested);
        Assert.True(rig.Runtime.CloseConfirmed);

        var twice = new Rig();
        twice.ToTitle();
        Assert.False(twice.Runtime.RequestClose());
        twice.Run(20);
        twice.QuitDialog();
        Assert.True(twice.Runtime.RequestClose());                     // Alt+F4 again: no second question
        Assert.True(twice.Runtime.ExitRequested);
        Assert.True(twice.Runtime.CloseConfirmed);
    }

    [Fact]
    public void InTheStoryTheQuestionOpensOverThePageAndTheStoryStandsStillUnderIt()
    {
        var rig = new Rig();
        rig.ToStory();
        int restores = rig.Game.Host.RestoreCount;
        Assert.False(rig.Runtime.RequestClose());
        rig.Run(20);
        rig.QuitDialog();
        Assert.Equal(UiMode.Overlay, rig.Ui.Mode);
        rig.Tap(PcKey.Enter);                                          // No: the Enter answers the dialog, it does not turn the page
        rig.Run(40);
        Assert.Equal("alpha line", rig.Game.Text);
        Assert.Equal(UiMode.Story, rig.Ui.Mode);
        Assert.Equal(restores, rig.Game.Host.RestoreCount);
        Assert.False(rig.Runtime.ExitRequested);

        rig.Runtime.RequestClose();
        rig.Run(20);
        rig.Tap(PcKey.Left);
        rig.Tap(PcKey.Enter);
        rig.Run(40);
        Assert.True(rig.Runtime.ExitRequested);
        Assert.Empty(rig.Game.Store.StoredSlots());                    // nothing new was saved by the quit itself
    }

    [Fact]
    public void ACloseWhileTheTitlesOwnQuitQuestionIsUpQuitsAtOnce()
    {
        var rig = new Rig();
        rig.ToTitle();
        rig.Tap(PcKey.Escape);                                         // the title's EXIT question (package K18)
        rig.Run(20);
        rig.QuitDialog();
        Assert.False(rig.Runtime.CloseDialogOpen);                     // not ours ...
        Assert.True(rig.Runtime.RequestClose());                       // ... but the same question: the second request quits
        Assert.True(rig.Runtime.ExitRequested);
    }

    [Fact]
    public void TheScriptVerbsOfThePackageParse()
    {
        InputScript script = InputScript.Parse("10:screenshot;20:close;30:blur;40:focus");
        Assert.Single(script.VerbsAt(10, "screenshot"));
        Assert.Single(script.VerbsAt(20, "close"));
        Assert.Single(script.VerbsAt(30, "blur"));
        Assert.Single(script.VerbsAt(40, "focus"));
        Assert.Empty(script.VerbsAt(10, "close"));
        Assert.Throws<FormatException>(() => InputScript.Parse("10:screenshoot"));
    }
}
