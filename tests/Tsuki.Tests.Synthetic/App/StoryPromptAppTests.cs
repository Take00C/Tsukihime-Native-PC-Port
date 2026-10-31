using Tsuki.App.Play;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// The story's two-button prompts through the app runtime (package K13): the dialog is drawn by the UI from the host's state, keyboard, pad, mouse and the guide line
/// answer it, a SELA's Yes opens the save screen over the story and the script goes on once that closes, and the autoplay answers it so a long run never stalls.
/// </summary>
public sealed class StoryPromptAppTests
{
    // many pages after the prompt, so that the story does not end (and the title does not open) within a test
    private static readonly string Tail = string.Concat(Enumerable.Repeat("_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);", 3000)) + "_END_();";

    private static readonly string SelaScript = "_WKST(F822,eq,1);_ATSV();_SELA(_D000);_WKST(F10,eq,1);" + Tail;

    private static readonly string SelsScript =
        "_WKST(F822,eq,1);_ATSV();_SELS(_D002);_GOTS(_D002,NO,YES);_ZZ00101(NO);_WKST(F10,eq,10);" + Tail + "_ZZ00201(YES);_WKST(F10,eq,20);" + Tail;

    private long _n;

    private sealed class Rt(AppRig rig, AppRuntime runtime, UiRoot ui)
    {
        public AppRig Rig { get; } = rig;

        public AppRuntime Runtime { get; } = runtime;

        public UiRoot Ui { get; } = ui;

        public GameHost Host => Rig.Host;

        public DisplayList Last { get; set; } = new(2048);
    }

    private Rt Make(string script)
    {
        var rig = new AppRig(script);
        var ui = new UiRoot(UiTestKit.Data(), rig.Services);
        var runtime = new AppRuntime(rig.Services, ui, null, null);
        rig.Services.StartScene(100);
        var rt = new Rt(rig, runtime, ui);
        for (int i = 0; i < 40 && !rt.Host.ChoiceOpen; i++)
        {
            Step(rt, new InputFrame());
        }

        Assert.True(rt.Host.ChoiceOpen);
        Run(rt, 15);                                                      // the dialog has faded in
        Assert.True(rt.Host.ChoiceSelectable);
        return rt;
    }

    private void Step(Rt rt, in InputFrame input)
    {
        rt.Last = new DisplayList(2048);
        rt.Runtime.Step(input with { Frame = ++_n }, _n * 16_666_667L, rt.Last);
    }

    private void Run(Rt rt, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            Step(rt, new InputFrame());
        }
    }

    private void Press(Rt rt, PadButtons b)
    {
        Step(rt, new InputFrame { Held = b, Pressed = b, Repeated = b });
        Step(rt, new InputFrame());
    }

    private static PointerState At(float x, float y, PointerButtons held = PointerButtons.None, PointerButtons pressed = PointerButtons.None) =>
        new((short)(x * 640f / 1920f), (short)(y * 448f / 1080f), true, held, pressed, 0, x, y);

    private void Hover(Rt rt, float x, float y) => Step(rt, new InputFrame { Pointer = At(x, y) });

    private void Click(Rt rt, float x, float y)
    {
        Step(rt, new InputFrame { Pointer = At(x, y, PointerButtons.Left, PointerButtons.Left) });
        Step(rt, new InputFrame { Pointer = At(x, y) });
    }

    private static int Tiles(DisplayList list, int child) =>
        list.Commands.ToArray().Count(c => c.Texture.Kind == TextureKind.UiTile && c.Texture.A == (int)UiSet.DispParts && c.Texture.C == child);

    private const float NoX = 813f;
    private const float YesX = 1071f;
    private const float ButtonsY = 600f;

    // -- drawing -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDialogIsDrawnOverTheStoryFromTheHostsStateAndSelectsNothingAtFirst()
    {
        Rt rt = Make(SelaScript);
        Assert.Equal(1, Tiles(rt.Last, 20));                              // the dialog window
        Assert.Equal(1, Tiles(rt.Last, 21));
        Assert.Equal(2, Tiles(rt.Last, 19));
        UiStoryPrompt prompt = rt.Rig.Services.StoryPrompt!.Value;
        Assert.Equal((216, -1), (prompt.MessageId, prompt.Cursor));
        Assert.Equal(1f, prompt.Alpha);
        Assert.False(rt.Ui.Active);                                       // the dialog is the story's, not a screen
        Assert.Equal(ChoiceKind.SavePrompt, rt.Host.ChoiceKind);
    }

    [Fact]
    public void NoDialogIsDrawnAfterTheAnswerHasFadedAway()
    {
        Rt rt = Make(SelsScript);
        Press(rt, PadButtons.Right);
        Press(rt, PadButtons.Decide);
        Run(rt, 30);
        Assert.False(rt.Host.ChoiceOpen);
        Assert.Null(rt.Rig.Services.StoryPrompt);
        Assert.Equal(0, Tiles(rt.Last, 20));
        Assert.Equal(10, rt.Host.F(10));                                  // No: the first label
    }

    // -- the save screen -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void YesOpensTheSaveScreenAfterTheFadeAndTheScriptGoesOnWhenItIsClosed()
    {
        Rt rt = Make(SelaScript);
        Press(rt, PadButtons.Down);                                       // Yes
        Assert.Equal(1, rt.Host.ChoiceCursor);
        Press(rt, PadButtons.Decide);
        Assert.False(rt.Ui.Active);                                       // the dialog fades out first
        Run(rt, 14);
        var save = Assert.IsType<SaveLoadScreen>(rt.Ui.Top);
        Assert.True(save.Save);
        Assert.Equal(0, Tiles(rt.Last, 21));                              // the dialog is gone behind it
        Assert.True(rt.Host.ChoiceOpen);
        Run(rt, 200);
        Assert.True(rt.Host.ChoiceOpen);                                  // the story waits as long as the screen is open
        Assert.Equal(0, rt.Host.F(10));

        Press(rt, PadButtons.Cancel);                                     // back
        Run(rt, 60);
        Assert.False(rt.Ui.Active);
        Assert.False(rt.Host.ChoiceOpen);
        Run(rt, 10);
        Assert.Equal(1, rt.Host.F(10));
        Assert.Equal(0, rt.Host.F(1118));
    }

    [Fact]
    public void YesThenAFreeSlotSavesAndTheScriptGoesOn()
    {
        Rt rt = Make(SelaScript);
        Press(rt, PadButtons.Down);
        Press(rt, PadButtons.Decide);
        Run(rt, 40);
        Assert.IsType<SaveLoadScreen>(rt.Ui.Top);
        Press(rt, PadButtons.Decide);                                     // the first row is the free slot
        Run(rt, 200);                                                     // "Saving..." comes and goes
        Assert.Contains(rt.Rig.Services.Slots, s => s.Used && s.Index < 100);
        Assert.IsType<SaveLoadScreen>(rt.Ui.Top);                         // the list stays up after a save, as in the system menu: back leaves it
        Assert.True(rt.Host.ChoiceOpen);
        Press(rt, PadButtons.Cancel);
        Run(rt, 80);
        Assert.False(rt.Ui.Active);
        Assert.False(rt.Host.ChoiceOpen);
        Assert.Equal(1, rt.Host.F(10));
    }

    [Fact]
    public void ASaveFromThePromptCarriesTheLastPageThatWasReadNotTheBlackChapterStart()
    {
        // package K16 (K13 open issue 4): the prompt's own frame is the black start of a chapter; the slot shows the last page the player read
        string script = "_WKST(F822,eq,1);_ATSV();_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);_WKST(F823,eq,0);_ATSV();_SELA(_D000);_WKST(F10,eq,1);" + Tail;
        var rig = new AppRig(script);
        var ui = new UiRoot(UiTestKit.Data(), rig.Services);
        var runtime = new AppRuntime(rig.Services, ui, null, null);
        byte shade = 200;
        runtime.Grab = () => (ThumbnailCodec.Width, ThumbnailCodec.Height, Solid(shade));
        rig.Services.StartScene(100);
        var rt = new Rt(rig, runtime, ui);
        Run(rt, 400);                                                     // the page is typed out and read: the app grabs its frame
        Assert.False(rt.Host.ChoiceOpen);
        shade = 0;                                                        // the chapter ends on a dark page, and the chapter start is black
        Run(rt, 250);                                                     // the dark page is grabbed too: the bright one is still the better thumbnail
        Press(rt, PadButtons.Decide);
        for (int i = 0; i < 80 && !rt.Host.ChoiceSelectable; i++)
        {
            Step(rt, new InputFrame());
        }

        Assert.True(rt.Host.ChoiceSelectable);
        Press(rt, PadButtons.Down);
        Press(rt, PadButtons.Decide);
        Run(rt, 40);
        Assert.IsType<SaveLoadScreen>(rt.Ui.Top);
        Press(rt, PadButtons.Decide);                                     // the free slot
        Run(rt, 200);
        UiSlot saved = Assert.Single(rig.Services.Slots, s => s.Used && s.Index < 100);
        UiThumbnail thumb = Assert.IsType<UiThumbnail>(rig.Services.Thumbnail(saved.Index));
        Assert.True(thumb.Rgba.Span[0] > 150, $"the thumbnail is the page that was read, not black (red {thumb.Rgba.Span[0]})");
    }

    private static byte[] Solid(byte shade)
    {
        var rgba = new byte[ThumbnailCodec.Width * ThumbnailCodec.Height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = rgba[i + 1] = rgba[i + 2] = shade;
            rgba[i + 3] = 255;
        }

        return rgba;
    }

    [Fact]
    public void NoGoesStraightOnWithoutAScreen()
    {
        Rt rt = Make(SelaScript);
        Press(rt, PadButtons.Right);                                      // No
        Press(rt, PadButtons.Decide);
        for (int i = 0; i < 40; i++)
        {
            Step(rt, new InputFrame());
            Assert.False(rt.Ui.Active);
        }

        Assert.Equal(1, rt.Host.F(10));
        Assert.DoesNotContain(rt.Rig.Services.Slots, s => s.Used);
    }

    // -- the mouse -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheMouseSelectsAButtonByHoverAndAnswersItByClick()
    {
        Rt rt = Make(SelsScript);
        Hover(rt, YesX, ButtonsY);
        Assert.Equal(1, rt.Host.ChoiceCursor);
        Hover(rt, NoX, ButtonsY);
        Assert.Equal(0, rt.Host.ChoiceCursor);
        Click(rt, 300f, 300f);                                            // beside both buttons: nothing
        Assert.Equal(2, rt.Host.ChoicePhase);
        Click(rt, YesX, ButtonsY);
        Assert.Equal(1, rt.Host.F(2));
        Run(rt, 30);
        Assert.Equal(20, rt.Host.F(10));
    }

    [Fact]
    public void AClickOnTheDialogDoesNotAlsoOpenAnythingElse()
    {
        Rt rt = Make(SelaScript);
        Click(rt, NoX, ButtonsY);
        Run(rt, 40);
        Assert.False(rt.Ui.Active);
        Assert.Equal(1, rt.Host.F(10));
        Assert.DoesNotContain(rt.Rig.Services.Slots, s => s.Used);
    }

    [Fact]
    public void ClickingTheGuideLinesConfirmWordAnswersTheSelectedButton()
    {
        Rt rt = Make(SelsScript);
        Press(rt, PadButtons.Down);                                       // Yes
        GuideHit confirm = Assert.Single(rt.Ui.Painter.GuideHits, h => h.Action == GuideAction.Confirm);
        Click(rt, confirm.Rect.X + (confirm.Rect.W / 2f), confirm.Rect.Y + 5f);
        Assert.Equal(3, rt.Host.ChoicePhase);
        Assert.Equal(1, rt.Host.F(2));
    }

    [Fact]
    public void AGuideClickWithNothingSelectedAnswersNothing()
    {
        Rt rt = Make(SelsScript);
        GuideHit confirm = Assert.Single(rt.Ui.Painter.GuideHits, h => h.Action == GuideAction.Confirm);
        Click(rt, confirm.Rect.X + 4f, confirm.Rect.Y + 5f);
        Assert.Equal(2, rt.Host.ChoicePhase);
    }

    // -- the keys and the pad ------------------------------------------------------------------------------------------------------

    [Fact]
    public void CancelDoesNothingAndTheSystemMenuOverThePromptHidesItUntilItIsClosed()
    {
        Rt rt = Make(SelaScript);
        Press(rt, PadButtons.Right);
        Press(rt, PadButtons.Cancel);                                     // the cancel button alone is not the menu key
        Assert.Equal(2, rt.Host.ChoicePhase);
        Assert.Equal(0, rt.Host.ChoiceCursor);
        Step(rt, new InputFrame { Pointer = At(300f, 300f, PointerButtons.Right, PointerButtons.Right) });   // right click: the system menu
        Run(rt, 5);
        Assert.IsType<SystemMenuScreen>(rt.Ui.Top);
        Assert.Equal(0, Tiles(rt.Last, 20));                              // the screen covers the story: no dialog
        Assert.True(rt.Host.ChoiceOpen);
        Press(rt, PadButtons.Cancel);
        Run(rt, 80);
        Assert.False(rt.Ui.Active);
        Assert.True(rt.Host.ChoiceSelectable);
        Assert.Equal(1, Tiles(rt.Last, 20));
    }

    // -- the autoplay --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheAutoplayAnswersASelaNoSoThatNoScreenOpensAndASelsYes()
    {
        foreach ((string script, int target, int expected) in new[] { (SelaScript, 10, 1), (SelsScript, 10, 20) })
        {
            var rig = new AppRig(script);
            var ui = new UiRoot(UiTestKit.Data(), rig.Services);
            var runtime = new AppRuntime(rig.Services, ui, null, null);
            var auto = new AutoPlayer("click", ChoicePolicy.Parse("first"));
            rig.Services.StartScene(100);
            var seen = new List<(int Point, int Line, int Count)>();
            auto.Chose += (p, l, c) => seen.Add((p, l, c));
            for (int i = 0; i < 400 && rig.Host.F(target) != expected; i++)
            {
                auto.Keys(runtime, i);
                runtime.Step(new InputFrame { Frame = ++_n }, _n * 16_666_667L, new DisplayList(2048));
                Assert.False(ui.Active);
            }

            Assert.Equal(expected, rig.Host.F(target));
            Assert.Equal(1, auto.ChoicesTaken);
            Assert.Equal(script == SelaScript ? 0 : 1, Assert.Single(seen).Line);
        }
    }
}
