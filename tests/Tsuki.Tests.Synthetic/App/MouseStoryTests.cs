using Tsuki.App.Play;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// The mouse in the story (package K12), the rows of the Controls page of the PS4 game: the pointer at the top of the screen opens the control panel, at the bottom it
/// hides the message window, a drag from the middle fast-forwards or rewinds, the wheel button toggles skip, and a click on empty space does not pick a choice.
/// </summary>
public sealed class MouseStoryTests
{
    private const string ChoiceScript =
        "_WKST(F822,eq,1);_ATSV();_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);_SELR(0,$000003);_SELR(1,$000004);_SELR(2,$000005);_SELD(_D000,5);_GOTS(_D000,A,B,C);"
        + "_ZZ00101(A);_WKST(F10,eq,10);_END_();_ZZ00201(B);_WKST(F10,eq,20);_END_();_ZZ00301(C);_WKST(F10,eq,30);_END_();";

    // a story of many pages, so that fast-forwarding does not run off its end within a test
    private static readonly string LongScript = "_WKST(F822,eq,1);_ATSV();" + string.Concat(Enumerable.Repeat("_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);", 3000)) + "_END_();";

    private long _n;

    private sealed class Rt(AppRig rig, AppRuntime runtime, UiRoot ui)
    {
        public AppRig Rig { get; } = rig;

        public AppRuntime Runtime { get; } = runtime;

        public UiRoot Ui { get; } = ui;
    }

    private Rt Make(string script = AppRig.ThreePages)
    {
        var rig = new AppRig(script);
        var ui = new UiRoot(UiTestKit.Data(), rig.Services);
        var runtime = new AppRuntime(rig.Services, ui, null, null);
        rig.Services.StartScene(100);
        var rt = new Rt(rig, runtime, ui);
        Run(rt, 200);
        return rt;
    }

    private void Step(Rt rt, in InputFrame input) => rt.Runtime.Step(input with { Frame = ++_n }, _n * 16_666_667L, new DisplayList(2048));

    private void Run(Rt rt, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            Step(rt, new InputFrame());
        }
    }

    private static PointerState At(float x, float y, PointerButtons held = PointerButtons.None, PointerButtons pressed = PointerButtons.None, int wheel = 0) =>
        new((short)(x * 640f / 1920f), (short)(y * 448f / 1080f), true, held, pressed, wheel, x, y);

    private void Move(Rt rt, float x, float y, int frames = 1)
    {
        for (int i = 0; i < frames; i++)
        {
            Step(rt, new InputFrame { Pointer = At(x, y) });
        }
    }

    private void Click(Rt rt, float x, float y)
    {
        Step(rt, new InputFrame { Pointer = At(x, y, PointerButtons.Left, PointerButtons.Left) });
        Step(rt, new InputFrame { Pointer = At(x, y) });
    }

    // ---- the control panel and the message window ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePointerAtTheTopOfTheScreenOpensTheControlPanelOnceUntilItLeavesAndComesBack()
    {
        Rt rt = Make();
        Assert.False(rt.Ui.Active);
        Move(rt, 960f, 500f);
        Assert.False(rt.Ui.Active);
        Move(rt, 960f, 4f);
        Assert.IsType<ModePanelScreen>(rt.Ui.Top);

        // cancel (once the panel has slid in) closes the panel; the pointer is still at the top edge, and the panel stays closed
        Move(rt, 960f, 4f, 30);
        Step(rt, new InputFrame { Pressed = PadButtons.Cancel, Pointer = At(960f, 4f) });
        Move(rt, 960f, 4f, 60);
        Assert.False(rt.Ui.Active);

        // leaving the edge arms it again
        Move(rt, 960f, 400f);
        Move(rt, 960f, 4f);
        Assert.IsType<ModePanelScreen>(rt.Ui.Top);
    }

    [Fact]
    public void ThePointerAtTheBottomHidesTheMessageWindowWhileItStaysThereAndAClickThereDoesNotAdvanceTheText()
    {
        Rt rt = Make();
        rt.Rig.WaitFor("alpha line");
        Run(rt, 100);
        string text = rt.Rig.Text;
        Assert.False(rt.Rig.Host.WindowHidden);
        Move(rt, 960f, 1070f);
        Assert.True(rt.Rig.Host.WindowHidden);
        Move(rt, 960f, 1070f, 10);
        Assert.True(rt.Rig.Host.WindowHidden);                                  // it stays hidden (the toggle is not repeated)
        Click(rt, 960f, 1070f);
        Run(rt, 5);
        Assert.Equal(text, rt.Rig.Text);                                        // the click did not advance
        Move(rt, 960f, 600f);
        Assert.False(rt.Rig.Host.WindowHidden);
        Click(rt, 960f, 600f);
        Run(rt, 100);
        Assert.NotEqual(text, rt.Rig.Text);
    }

    [Fact]
    public void ThePointerOutsideTheGameAreaTouchesNeitherEdge()
    {
        Rt rt = Make();
        Step(rt, new InputFrame { Pointer = new PointerState(10, 0, false, PointerButtons.None, PointerButtons.None, 0, 30f, 0f) });
        Assert.False(rt.Ui.Active);
        Step(rt, new InputFrame { Pointer = new PointerState(10, 447, false, PointerButtons.None, PointerButtons.None, 0, 30f, 1079f) });
        Assert.False(rt.Rig.Host.WindowHidden);
    }

    // ---- fast-forward and rewind by dragging --------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADragRightFromTheMiddleFastForwardsWhileTheButtonIsDownAndEndsWhenItComesUp()
    {
        Rt rt = Make(LongScript);
        Assert.Equal(0, rt.Rig.Host.Mode.SkipActive);
        Step(rt, new InputFrame { Pointer = At(960f, 540f, PointerButtons.Left, PointerButtons.Left) });
        Assert.Equal(0, rt.Rig.Host.Mode.SkipActive);                            // not yet 45 units
        for (int i = 1; i <= 10; i++)
        {
            Step(rt, new InputFrame { Pointer = At(960f + (i * 30f), 540f, PointerButtons.Left) });
        }

        Assert.Equal(1, rt.Rig.Host.Mode.SkipActive);
        Step(rt, new InputFrame { Pointer = At(1260f, 540f) });
        Run(rt, 2);
        Assert.Equal(0, rt.Rig.Host.Mode.SkipActive);
    }

    [Fact]
    public void ADragLeftFromTheMiddleRewindsAndOneThatStartsAtTheEdgeDoesNot()
    {
        Rt rt = Make(LongScript);
        for (int i = 0; i < 6; i++)
        {
            Step(rt, new InputFrame { Pressed = PadButtons.Decide, Held = PadButtons.Decide });
            Run(rt, 60);
        }

        int cursor = rt.Rig.Host.Machine.Cursor;

        // a drag that starts at the corner is only a click (which advances); nothing is fast-forwarded or rewound however far it runs
        Step(rt, new InputFrame { Pointer = At(100f, 100f, PointerButtons.Left, PointerButtons.Left) });
        for (int i = 1; i <= 8; i++)
        {
            Step(rt, new InputFrame { Pointer = At(100f + (i * 160f), 100f, PointerButtons.Left) });
            Assert.Equal(0, rt.Rig.Host.Mode.SkipActive);
            Assert.False(rt.Runtime.Last.Rewound);
        }

        Step(rt, new InputFrame { Pointer = At(900f, 540f) });
        Run(rt, 60);
        cursor = rt.Rig.Host.Machine.Cursor;

        int rewinds = 0;
        Step(rt, new InputFrame { Pointer = At(960f, 540f, PointerButtons.Left, PointerButtons.Left) });
        for (int i = 1; i <= 40; i++)
        {
            Step(rt, new InputFrame { Pointer = At(Math.Max(960f - (i * 30f), 10f), 540f, PointerButtons.Left) });
            rewinds += rt.Runtime.Last.Rewound ? 1 : 0;
        }

        Assert.InRange(rewinds, 3, 8);                                           // one step at the start, then every eighth frame
        Step(rt, new InputFrame { Pointer = At(100f, 540f) });
        Run(rt, 60);
        Assert.True(rt.Rig.Host.Machine.Cursor < cursor, "the story went back");
    }

    // ---- choices ----------------------------------------------------------------------------------------------------------------------------------------

    private static int LineY(GameHost host, int line)
    {
        for (int y = 0; y < 1080; y++)
        {
            if (host.ChoiceLineAt(y) == line)
            {
                return y + 4;
            }
        }

        throw new InvalidOperationException($"no row of the choice list holds line {line}");
    }

    [Fact]
    public void HoveringAChoiceLineHighlightsItAClickOnEmptySpaceDoesNothingAndAClickOnALinePicksIt()
    {
        Rt rt = Make(ChoiceScript);
        GameHost host = rt.Rig.Host;
        for (int i = 0; i < 600 && !host.ChoiceSelectable; i++)
        {
            Step(rt, i % 5 == 0 ? new InputFrame { Pressed = PadButtons.Decide } : new InputFrame());
        }

        Assert.True(host.ChoiceSelectable);
        var decided = new List<int>();
        host.ChoiceDecided += (_, _, value, _) => decided.Add(value);
        float y1 = LineY(host, 1);
        Move(rt, 900f, y1);
        Assert.Equal(1, host.ChoiceCursor);
        float y2 = LineY(host, 2);
        Move(rt, 900f, y2);
        Assert.Equal(2, host.ChoiceCursor);

        Click(rt, 900f, 30f);                                                    // empty space above the list: nothing is picked
        Assert.Empty(decided);
        Assert.True(host.ChoiceSelectable);
        Click(rt, 900f, y1);
        Assert.Equal([1], decided);
    }
}
