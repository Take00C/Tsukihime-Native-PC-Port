using Tsuki.App.Play;
using Tsuki.Engine.Contracts;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package R1: the config options "Auto Skip Cleared Scenes" and "Show Confirmation Message" do something. <c>_RDAS</c> on a node the player has completed raises
/// the engine's offer (GameHost.CmdRdas); the runtime answers it as the PS4's mode panel does (knife ui.rs 6177-6184): the next-scene question (dialog 10) when
/// the confirmation is on, else the next scene after a 4-frame countdown. Nothing listened to the offer before.
/// </summary>
public sealed class AutoSkipTests
{
    // node 540 is marked played and done (RTST sets F110 for 533..557, RDED marks the node done) before the first page; RDAS on it comes with the second page
    private const string Script =
        "_WKST(F822,eq,1);_RTST(540);_RDED(540);" +
        "_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);" +
        "_RDAS(540);_WKST(F823,eq,0);_WNTY(1);_ZM00102($000003);" +
        "_WKST(F823,eq,0);_WNTY(1);_ZM00103($000004);" +
        "_END_();";

    private sealed class Rt
    {
        private long _n;

        public Rt(byte autoSkip, byte confirm)
        {
            Rig = new AppRig(Script);
            Rig.Services.Config.AutoSkipCleared = autoSkip;
            Rig.Services.Config.SkipConfirm = confirm;
            Ui = new UiRoot(UiTestKit.Data(), Rig.Services);
            Runtime = new AppRuntime(Rig.Services, Ui, null, null);
            Rig.Services.StartScene(100);
            RunTo("alpha line");
        }

        public AppRig Rig { get; }

        public UiRoot Ui { get; }

        public AppRuntime Runtime { get; }

        public void Step(bool click = false)
        {
            PadButtons b = click ? PadButtons.Decide : PadButtons.None;
            Runtime.Step(new InputFrame { Frame = ++_n, Held = b, Pressed = b, Repeated = b }, _n * 16_666_667L, new DisplayList(2048));
        }

        public void RunTo(string text)
        {
            for (int i = 0; i < 400 && Rig.Text != text; i++)
            {
                Step();
            }

            Assert.Equal(text, Rig.Text);
            for (int i = 0; i < 90; i++)
            {
                Step();                                                   // the line has finished revealing and waits for the click
            }
        }

        /// <summary>Clicks once (the page advances and the script runs on to _RDAS), then a frame with nothing held.</summary>
        public void ClickThroughToRdas()
        {
            Step(click: true);
            Step();
        }
    }

    [Fact]
    public void WithTheOptionOffTheDoneNodeIsReadLikeAnyOther()
    {
        var rt = new Rt(autoSkip: 0, confirm: 1);
        rt.ClickThroughToRdas();
        for (int i = 0; i < 10; i++)
        {
            rt.Step();
        }

        Assert.DoesNotContain(rt.Ui.Stack, s => s is ConfirmDialog);
        Assert.Equal("beta line", rt.Rig.Text);
        Assert.True(rt.Rig.Host.PageMessages.Count <= 2);
    }

    [Fact]
    public void WithTheConfirmationOnTheNextSceneQuestionOpensOverTheStory()
    {
        var rt = new Rt(autoSkip: 1, confirm: 1);
        rt.ClickThroughToRdas();
        Assert.Contains(rt.Ui.Stack, s => s is ConfirmDialog);
        string shown = rt.Rig.Text;
        for (int i = 0; i < 20; i++)
        {
            rt.Step();
        }

        Assert.Equal(shown, rt.Rig.Text);                                   // the story stands still under the question
        Assert.Contains(rt.Ui.Stack, s => s is ConfirmDialog);
    }

    [Fact]
    public void WithTheConfirmationOffTheNextSceneFollowsAfterFourFrames()
    {
        var rt = new Rt(autoSkip: 1, confirm: 0);
        rt.ClickThroughToRdas();
        Assert.DoesNotContain(rt.Ui.Stack, s => s is ConfirmDialog);
        string shown = rt.Rig.Text;
        rt.Step();
        rt.Step();
        Assert.Equal(shown, rt.Rig.Text);                                   // the countdown has not ended
        for (int i = 0; i < 6; i++)
        {
            rt.Step();
        }

        // the scene skip ran the script on past the waiting page without a click: the next unread message is on show
        Assert.NotEqual(shown, rt.Rig.Text);
        Assert.Equal("gamma line", rt.Rig.Text);
    }
}
