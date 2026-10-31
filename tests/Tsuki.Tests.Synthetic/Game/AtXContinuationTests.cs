using Tsuki.Engine.Game;
using Tsuki.Engine.Text;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>
/// The <c>@x</c> continuation (package K15): a message that starts with <c>@x</c> continues on the line where the message before it on the page ended (the global VA 0x178BE20,
/// set to 16.5267 + the x of the previous message's last glyph by <c>_ZM</c> through 0x7C340 and by the HUD's history loop). Before, nothing set it: the message started at the left
/// edge, on top of the end of the previous one (291 messages of the story; 08_01_CIEL08_1: the message starting "All right" then the one starting "With my").
/// </summary>
public sealed class AtXContinuationTests
{
    private const string Page = "_WKST(F822,eq,1);_WKAD(F823,1);_WNTY(0);";

    private static GameHost Start(string script, out GameRig rig)
    {
        rig = new GameRig().Line(2, "ab").Line(3, "@xcd").Line(4, "cd").Script(script);
        GameHost host = rig.Build();
        host.NewGame(100);
        host.Wait.WindowAlpha = 1f;
        host.Wait.WindowTarget = 1;
        return host;
    }

    [Fact]
    public void AnAtXMessageStartsWhereThePreviousMessageOfThePageEnded()
    {
        GameHost host = Start(Page + "_ZM00101($000002@e);_WKAD(F823,1);_ZM00201($000003@e);_WTTM(3000,1);_END_();", out GameRig rig);
        rig.Run(h => h.PageMessages.Count == 2, 600);
        Assert.Equal(2, host.PageMessages.Count);
        TextBlock first = host.PageMessages[0].Block;
        TextBlock second = host.PageMessages[1].Block;
        float expected = MessageWindow.ContinueX(first);
        Assert.True(expected > MessageWindow.ContinueGap, "the previous message has glyphs to the right of the origin");
        Assert.Equal(expected, host.PageMessages[1].SavedX);
        Assert.Equal(expected, second.Records[0].Dx, 3);
        Assert.Equal(0, second.Records[0].Line);
        Assert.True(second.Records[0].Dx > first.Records[^1].Dx, "the continuation lies right of the last glyph of the previous message");
    }

    [Fact]
    public void AMessageWithoutAtXStartsAtTheLeftEdgeEvenAfterAnotherMessage()
    {
        GameHost host = Start(Page + "_ZM00101($000002@e);_WKAD(F823,1);_ZM00201($000004@e);_WTTM(3000,1);_END_();", out GameRig rig);
        rig.Run(h => h.PageMessages.Count == 2, 600);
        Assert.Equal(2, host.PageMessages.Count);
        Assert.Equal(0f, host.PageMessages[1].Block.Records[0].Dx, 3);
    }

    [Fact]
    public void TheFirstMessageOfAPageHasNoContinuationPosition()
    {
        GameHost host = Start(Page + "_ZM00101($000003@e);_WTTM(3000,1);_END_();", out GameRig rig);
        rig.Run(h => h.PageMessages.Count == 1, 600);
        Assert.Equal(0f, host.PageMessages[0].SavedX);
        Assert.Equal(0f, host.PageMessages[0].Block.Records[0].Dx, 3);
    }

    [Fact]
    public void ContinueXIsTheLastGlyphXPlusOneGlyphAdvance()
    {
        GameHost host = Start(Page + "_ZM00101($000002@e);_WTTM(3000,1);_END_();", out GameRig rig);
        rig.Run(h => h.PageMessages.Count == 1, 600);
        TextBlock block = host.PageMessages[0].Block;
        Assert.Equal(block.Records[^1].Dx + 16.52666664123535f, MessageWindow.ContinueX(block), 3);
    }
}
