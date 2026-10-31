using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Text;

namespace Tsuki.Tests.Data.Game;

/// <summary>
/// Package K15: the page layout on retail scenes. A message that starts with <c>@x</c> continues the line of the message before it (08_01_CIEL08_1: the message starting "All right" then the one
/// starting "With my"), a speech-bubble window starts its text at the bubble's own origin (QA_03).
/// </summary>
public sealed class PageLayoutOnRealScenesTests(ITestOutputHelper output)
{
    private static void RunUntil(GameHost host, Func<GameHost, bool> stop, int max = 400_000)
    {
        for (int n = 0; n < max && host.Phase == GamePhase.Running && !stop(host); n++)
        {
            host.SetSkipLatch(true);
            host.Step(new InputFrame { Pressed = (n & 1) == 0 ? PadButtons.Decide : PadButtons.None });
        }
    }

    [GameDataFact]
    public void AnAtXMessageOfCiel08StartsRightOfTheEndOfThePreviousOne()
    {
        GameHost host = FirstChoiceRig.NewHost(Language.En);
        host.NewGame(host.NewGameSceneId());
        RunUntil(host, h => h.Phase != GamePhase.Running);                         // the prologue
        host.NewGame(host.NewGameSceneId());
        RunUntil(host, h => h.F(822) == 1);                                        // START_SCRIPT sets up the page mode
        Assert.True(host.JumpToNode(219), "the flowchart node of 08_01_CIEL08_1");
        RunUntil(host, h => h.CurrentMessage is { } m && System.Text.Encoding.UTF8.GetString(m.Text).StartsWith("@xWith my", StringComparison.Ordinal));
        output.WriteLine($"phase {host.Phase}, script {host.Script}, frame {host.Frame}, message {(host.CurrentMessage is { } cm ? System.Text.Encoding.UTF8.GetString(cm.Text) : "none")}");
        Assert.NotNull(host.CurrentMessage);
        PageMessage current = host.CurrentMessage!;
        output.WriteLine($"page {host.PageMessages.Count} messages, F822 {host.F(822)}, F823 {host.F(823)}");
        Assert.True(host.PageMessages.Count >= 2, "the @x message shares the page with the line it continues");
        PageMessage before = host.PageMessages[^2];
        float expected = MessageWindow.ContinueX(before.Block);
        output.WriteLine($"previous message '{System.Text.Encoding.UTF8.GetString(before.Text)}' ends at x {before.Block.Records[^1].Dx:F2}; the @x message starts at {current.Block.Records[0].Dx:F2} (saved {current.SavedX:F2})");
        Assert.Equal(expected, current.SavedX);
        Assert.InRange(current.Block.Records[0].Dx, 30f, 1000f);
        Assert.True(current.Block.Records[0].Dx > 100f || current.Block.Records[0].Line > 0, "it started at the left edge on the previous line's text");
    }

    [GameDataFact]
    public void TheSpeechBubbleOfQa03StartsItsTextAtItsOwnOrigin()
    {
        GameHost host = FirstChoiceRig.NewHost(Language.En);
        host.NewGame(host.NewGameSceneId());
        RunUntil(host, h => h.Phase != GamePhase.Running);
        host.NewGame(host.NewGameSceneId());
        Assert.True(host.StartReplay(1002), "the gallery replay of QA_03");
        RunUntil(host, h => h.CurrentMessage is not null && (h.F(1105) >= 3 || h.F(1109) >= 3));
        Assert.NotNull(host.CurrentMessage);
        int window = host.F(1109) > 0 ? host.F(1109) : host.F(1105);
        Assert.InRange(window, 3, 55);
        var list = new DisplayList(8192);
        for (int i = 0; i < 400 && (host.Wait.Bits & WaitBits.Shown) == 0; i++)
        {
            host.Step(default);
        }

        list.Reset(host.Frame);
        host.Step(default, list);
        double firstX = double.NaN;
        foreach (DrawCmd c in list.Commands)
        {
            if (c.Texture.Kind == TextureKind.GlyphPage)
            {
                firstX = c.V0.X;
                break;
            }
        }

        (double ox, _) = MessageWindow.BubbleTextOrigin(window, host.F(1106), host.F(1107));
        output.WriteLine($"bubble window {window}: text origin x {ox:F1} units = {ox * 3:F0} px; first glyph at {firstX:F0} px");
        Assert.InRange(firstX, (ox * 3) - 2, (ox * 3) + 400);
        Assert.True(firstX > 150, "the bubble text does not start at the full-screen origin (90 px)");
    }
}
