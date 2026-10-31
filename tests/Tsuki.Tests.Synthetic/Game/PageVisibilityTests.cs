using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>
/// The page text and F1103 (package K15). The PS4's HUD draws the current message only while F1103 is 0 (VA 0x54BD8) and the earlier ones through a loop that also needs
/// F1103 == 0 (VA 0x550F7 / 0x551D9). F1103 is set by <c>_CLO2</c> / <c>_CLO3</c>, by a story movie (<c>_TOMP</c>), by a layer change that hides the text and by the prompts, and
/// cleared by the next message; the last message of a scene used to stay over the ending movie. A list choice does not set it: the question stays above the lines.
/// </summary>
public sealed class PageVisibilityTests
{
    private const string Page = "_WKST(F822,eq,1);_WKAD(F823,1);_WNTY(0);";

    private static GameHost Start(string script, out GameRig rig)
    {
        rig = new GameRig().Line(1, "abcdefghijklmnopqrst").Line(2, "left").Line(3, "right").Line(43960, "1. ").Line(43961, "2. ").Script(script);
        GameHost host = rig.Build();
        host.NewGame(100);
        host.Wait.WindowAlpha = 1f;
        host.Wait.WindowTarget = 1;
        return host;
    }

    private static int Glyphs(GameHost host)
    {
        var list = new DisplayList(2048);
        list.Reset(host.Frame);
        host.Step(default, list);
        int n = 0;
        foreach (DrawCmd c in list.Commands)
        {
            if (c.Texture.Kind == TextureKind.GlyphPage)
            {
                n++;
            }
        }

        return n;
    }

    [Fact]
    public void ARevealedMessageIsDrawnAndAClo3HidesItAtOnceWhileTheWindowIsStillFading()
    {
        GameHost host = Start(Page + "_ZM00101($000001@e);_WTTM(3000,1);_CLO3();_WTTM(3000,1);_END_();", out GameRig rig);
        rig.Run(h => (h.Wait.Bits & WaitBits.Shown) != 0, 400);
        Assert.Equal(0, host.F(1103));
        Assert.True(Glyphs(host) >= 20, "the revealed message is drawn");
        int guard = 0;
        while (host.F(1103) == 0 && guard++ < 400)
        {
            host.Step(default);
        }

        Assert.Equal(1, host.F(1103));
        Assert.True(host.Wait.WindowAlpha > 0f, "the window is still fading out");
        Assert.Equal(0, Glyphs(host));
    }

    [Fact]
    public void ALayerChangeThatHidesTheTextHidesThePageUntilTheNextMessage()
    {
        GameHost host = Start(Page + "_ZM00101($000001@e);_WTTM(500,1);_FADS(1,0,0,1);_WTTM(3000,1);_ZM00201($000002@e);_WTTM(3000,1);_END_();", out GameRig rig);
        rig.Run(h => (h.Wait.Bits & WaitBits.Shown) != 0, 400);
        Assert.True(Glyphs(host) >= 20);
        rig.Run(h => h.F(1103) == 1, 400);
        Assert.Equal(1, host.F(1103));
        Assert.Equal(0, Glyphs(host));
        rig.Run(h => h.F(1103) == 0, 600);
        Assert.Equal(0, host.F(1103));
        rig.Run(h => (h.Wait.Bits & WaitBits.Shown) != 0, 400);
        Assert.True(Glyphs(host) >= 4, "the next message is drawn");
    }

    [Fact]
    public void AListChoiceDoesNotSetF1103AndTheQuestionStaysOnThePage()
    {
        GameHost host = Start(Page + "_ZM00101($000001@e);_SELR(0,$000002);_SELR(1,$000003);_SELD(_D000,5);_GOTS(_D000,A,B);_ZZ00101(A);_END_();_ZZ00201(B);_END_();", out GameRig rig);
        rig.Run(h => h.ChoiceOpen, 400);
        Assert.Equal(ChoiceKind.List, host.ChoiceKind);
        Assert.Equal(0, host.F(1103));
        int guard = 0;
        while (!host.ChoiceSelectable && guard++ < 200)
        {
            host.Step(default);
        }

        Assert.True(host.ChoiceSelectable);
        // the 20 letters of the question (fill and outline quads) plus the two lines
        Assert.True(Glyphs(host) >= 20 + 10, "the question and the lines are drawn");
    }

    [Fact]
    public void ASpeechBubbleWindowDrawsItsTextAtTheBubblesOwnOriginNotAtTheFullScreenOne()
    {
        // package K15: the QA scenes set F1105 to a bubble index; the page text used to start at the full-screen origin for every window
        string full = Page + "_ZM00101($000001@e);_WTTM(3000,1);_END_();";
        string bubble = "_WKST(F1105,eq,5);" + Page + "_ZM00101($000001@e);_WTTM(3000,1);_END_();";
        double FirstX(string script)
        {
            GameHost host = Start(script, out GameRig rig);
            rig.Run(h => (h.Wait.Bits & WaitBits.Shown) != 0, 400);
            var list = new DisplayList(2048);
            list.Reset(host.Frame);
            host.Step(default, list);
            foreach (DrawCmd c in list.Commands)
            {
                if (c.Texture.Kind == TextureKind.GlyphPage)
                {
                    return c.V0.X;
                }
            }

            return double.NaN;
        }

        double fullX = FirstX(full);
        double bubbleX = FirstX(bubble);
        Assert.InRange(fullX, 80.0, 140.0);                     // (30, 35) units * 3 px
        Assert.InRange(bubbleX, 600.0, 700.0);                  // bubble 5: text origin (213.5, 244.5) units = 640 px
    }
}
