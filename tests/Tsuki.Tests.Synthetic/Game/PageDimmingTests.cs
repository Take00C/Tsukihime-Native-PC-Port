using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>
/// The dimming of the earlier messages of a page (textbox.md 5.8). The PS4's HUD writes F[870 + j] = text speed - 128 every frame for the message before the current one
/// (VA 0x55C00..0x55C2E; config +0x20 unread or +0x22 read, as 0x781AB0 picks it) and dims message j over <c>(F[870 + j] + 128) * 1.2</c> frames (VA 0x55C37..0x55C5E): 42 frames
/// for unread text and 18 for read text at the boot speeds. The port read the limit once from F[870 + F823], a word nothing wrote, so every earlier message took 153.6 frames:
/// in the prologue the line above stayed bright while the next one typed, where the PS4 has it grey already. The timer advances only while F1103 == 0 and the text fade is 1
/// (VA 0x55F8A), and a message ending in <c>@k</c> dims with the next one (VA 0x55D01, 0x55E86..0x55F52).
/// </summary>
public sealed class PageDimmingTests
{
    private const string Page = "_WKST(F822,eq,1);_WKAD(F823,1);_WNTY(0);";
    private const string Next = "_WKAD(F823,1);";

    private static GameHost Start(string script, out GameRig rig)
    {
        rig = new GameRig().Line(1, "abcdefghijklmnopqrst").Line(2, "second").Line(3, "third").Script(script);
        GameHost host = rig.Build();
        host.NewGame(100);
        host.Wait.WindowAlpha = 1f;
        host.Wait.WindowTarget = 1;
        return host;
    }

    /// <summary>Steps one frame with a display list and returns the largest glyph alpha of each message row of the page, top row first (the fill quad; 1 = undimmed).</summary>
    private static List<double> RowAlphas(GameHost host)
    {
        var list = new DisplayList(4096);
        list.Reset(host.Frame);
        host.Step(default, list);
        var glyphs = new List<(float Y, double A)>();
        foreach (DrawCmd c in list.Commands)
        {
            if (c.Texture.Kind == TextureKind.GlyphPage)
            {
                glyphs.Add((c.V0.Y, (c.V0.Rgba >> 24) / 255.0));
            }
        }

        var rows = new List<(float Y, double A)>();
        foreach ((float y, double a) in glyphs.OrderBy(g => g.Y))
        {
            if (rows.Count == 0 || y - rows[^1].Y > 30f)
            {
                rows.Add((y, a));
            }
            else
            {
                rows[^1] = (rows[^1].Y, Math.Max(rows[^1].A, a));
            }
        }

        return [.. rows.Select(r => r.A)];
    }

    /// <summary>
    /// The frame in which the first message's row first draws at the dimmed level t / 255, counting the frame the second message starts as frame 1 (its timer counts that
    /// frame: it is 1 after it), and the first message's alphas from frame 2 on.
    /// </summary>
    private static (int Frame, List<double> Alphas) FramesToDim(GameHost host, GameRig rig, int max)
    {
        rig.Run(h => h.PageMessages.Count == 2, 600);
        Assert.Equal(2, host.PageMessages.Count);
        Assert.Equal(1, host.PageMessages[0].DimTimer);
        var alphas = new List<double>();
        for (int frame = 2; frame <= max; frame++)
        {
            List<double> rows = RowAlphas(host);
            Assert.NotEmpty(rows);                                // the top row is the first message (the second one may not show a glyph yet)
            alphas.Add(rows[0]);
            if (rows[0] <= (100.0 / 255.0) + 0.005)
            {
                return (frame, alphas);
            }
        }

        return (-1, alphas);
    }

    [Fact]
    public void UnreadTextDimsTheMessageBeforeInFortyTwoFramesAndTheHudWordIsTheTextSpeedMinus128()
    {
        GameHost host = Start(Page + "_ZM00101($000001@e);" + Next + "_ZM00201($000002@e);_WTTM(4000,1);_END_();", out GameRig rig);
        (int frame, List<double> alphas) = FramesToDim(host, rig, 200);
        Assert.False(host.CurrentMessage!.Read);
        Assert.Equal(0, host.Wait.ReadSel);
        Assert.Equal(42, frame);                                  // timer 42 = the limit: alpha t / 255 (with the old limit of 153.6 frames: frame 153)
        Assert.True(alphas[19] is > 0.65 and < 0.75, $"halfway (frame 21) the line is half dimmed ({alphas[19]:F3})");
        Assert.Equal(35 - 128, host.F(870));                      // F[870 + j], j = F823 - 2 = 0: the unread speed (config +0x20, boot 35) - 128
        Assert.Equal(42f, host.PageMessages[0].DimLimit);         // (F870 + 128) * 1.2
    }

    [Fact]
    public void ReadTextDimsTheMessageBeforeInEighteenFrames()
    {
        string script = Page + "_ZM00101($000001@e);" + Next + "_ZM00201($000002@e);_WTTM(4000,1);" + Next + "_ZM00301($000003@e);_END_();";
        GameHost host = Start(script, out GameRig rig);
        rig.Run(h => h.Phase == GamePhase.Ended, 2000);
        Assert.Equal(GamePhase.Ended, host.Phase);
        host.NewGame(100);                                        // the second playthrough: the lines are read now
        host.Wait.WindowAlpha = 1f;
        host.Wait.WindowTarget = 1;
        (int frame, _) = FramesToDim(host, rig, 200);
        Assert.True(host.CurrentMessage!.Read);
        Assert.Equal(1, host.Wait.ReadSel);
        Assert.Equal(18, frame);
        Assert.Equal(15 - 128, host.F(870));                      // the read speed (config +0x22, boot 15) - 128
    }

    [Fact]
    public void AReadLineAboveAnUnreadOneDimsInFortyTwoFramesByTheCurrentMessagesReadFlag()
    {
        // the HUD picks the speed by the current message's read flag (VA 0x55C14), not by the earlier message's own: knife does the latter and dims this line in 18 frames
        GameHost host = Start(Page + "_ZM00101($000001@e);" + Next + "_ZM00201($000002@e);_WTTM(4000,1);_END_();", out GameRig rig);
        host.SystemData.SetReadmax(1, 1);                         // _ZM00101 is read, _ZM00201 is not
        (int frame, _) = FramesToDim(host, rig, 200);
        Assert.True(host.PageMessages[0].Read);
        Assert.False(host.CurrentMessage!.Read);
        Assert.Equal(42, frame);
        Assert.Equal(35 - 128, host.F(870));                      // the unread speed, for the read line above
    }

    [Fact]
    public void AClickThatCompletesARevealDimsTheEarlierLinesAtOnceButNotTheLineItCompleted()
    {
        // the reveal click memsets F823 - 1 words from F859 to -1 (VA 0xA27FC..0xA2830): the earlier messages of the page, not the current one. The port set every timer of the
        // page to -1, so the line whose reveal the player completed dropped to the dimmed level at once when the next line started, skipping its 42 frames
        string script = Page + "_ZM00101($000001);" + Next + "_ZM00201($000002);" + Next + "_ZM00301($000003);_END_();";
        GameHost host = Start(script, out GameRig rig);
        host.Step(default);                                       // message 1 starts and types
        Assert.Single(host.PageMessages);
        Assert.True(host.Wait.Head < WaitState.Instant, $"message 1 is still typing (head {host.Wait.Head})");
        rig.Click();
        Assert.True(host.LastReveal.RevealClicked);
        Assert.Equal(0, host.PageMessages[0].DimTimer);           // the current message keeps its timer (it was -1)
        for (int i = 0; i < 6; i++)
        {
            host.Step(default);
        }

        rig.Click();                                              // the advance: message 2 starts in the same frame
        (int frame, List<double> alphas) = FramesToDim(host, rig, 200);
        Assert.Equal(42, frame);                                  // the whole fade, as without the reveal click (it was at the dimmed level from frame 1)
        Assert.True(alphas[0] > 0.95, $"frame 2: message 1 is still bright ({alphas[0]:F3})");

        // a reveal click during message 3 still dims message 2 at once, and leaves message 3's timer alone
        rig.Click();                                              // the advance: message 3 starts
        Assert.Equal(3, host.PageMessages.Count);
        host.Step(default);                                       // the press is let go (the advance latch clears)
        Assert.True(host.Wait.Head < WaitState.Instant, $"message 3 is still typing (head {host.Wait.Head})");
        Assert.InRange(host.PageMessages[1].DimTimer, 1, 41);
        rig.Click();
        Assert.True(host.LastReveal.RevealClicked);
        Assert.Equal(-1, host.PageMessages[0].DimTimer);
        Assert.Equal(-1, host.PageMessages[1].DimTimer);
        Assert.Equal(0, host.PageMessages[2].DimTimer);
    }

    [Fact]
    public void TheDimmingStopsWhileF1103HidesThePage()
    {
        // VA 0x55F8A: the timer advances only with F1103 == 0 (and the page loop runs only then); _CLO3 sets F1103 and leaves the text fade at 1
        GameHost host = Start(Page + "_ZM00101($000001@e);" + Next + "_ZM00201($000002@e);_CLO3();_WTTM(3000,1);_END_();", out GameRig rig);
        rig.Run(h => h.F(1103) == 1, 600);
        Assert.Equal(1, host.F(1103));
        Assert.Equal(1f, host.TextFade);
        int timer = host.PageMessages[0].DimTimer;
        Assert.InRange(timer, 1, 41);
        rig.Run(_ => false, 120);
        Assert.Equal(GamePhase.Running, host.Phase);
        Assert.Equal(timer, host.PageMessages[0].DimTimer);
    }

    [Fact]
    public void SeenTextAnimationsOffDimsTheEarlierLinesAtOnceAlsoWhileTheTextFadesIn()
    {
        // config +0xA4 == 1 sets the timer to -1 in the page loop itself (VA 0x55C4A), before the guard that waits for the text fade: after a _CLO3 the next message fades in
        // over 18 frames and the line above is dimmed from its first frame (it used to come back bright and drop only once the fade had ended)
        GameHost host = Start(Page + "_ZM00101($000001@e);_CLO3();_WTTM(500,1);" + Next + "_ZM00201($000002@e);_WTTM(3000,1);_END_();", out GameRig rig);
        host.Config.SeenTextAnimations = 1;
        rig.Run(h => h.PageMessages.Count == 2, 600);
        Assert.Equal(2, host.PageMessages.Count);
        Assert.True(host.TextFade < 1f, $"the text is fading in ({host.TextFade:F2})");
        Assert.Equal(-1, host.PageMessages[0].DimTimer);
    }

    [Fact]
    public void AMessageEndingInAtKStaysBrightWhileItsRunReachesTheCurrentMessageAndThenDimsWithTheNextOne()
    {
        // a $N@k@e line (469 in the story) and the line after it dim as one: the HUD takes the timer and the limit of the next message that does not end in @k (VA 0x55E86..0x55F52)
        string script = Page + "_ZM00101($000001@k@e);" + Next + "_ZM00201($000002@e);_WTTM(4000,1);" + Next + "_ZM00301($000003@e);_WTTM(4000,1);_END_();";
        GameHost host = Start(script, out GameRig rig);
        rig.Run(h => h.PageMessages.Count == 2, 600);
        for (int i = 0; i < 120; i++)
        {
            List<double> rows = RowAlphas(host);
            Assert.NotEmpty(rows);
            Assert.Equal(1.0, rows[0], 3);                       // its run reaches the current message: not dimmed (it used to reach the dimmed level)
        }

        rig.Run(h => h.PageMessages.Count == 3, 600);
        Assert.Equal(3, host.PageMessages.Count);
        bool dimmed = false;
        for (int i = 0; i < 60; i++)
        {
            List<double> rows = RowAlphas(host);
            Assert.True(rows.Count >= 2);
            Assert.Equal(rows[1], rows[0], 3);                   // the two dim together, by the second one's timer
            dimmed = rows[0] <= (100.0 / 255.0) + 0.005;
        }

        Assert.True(dimmed, "both lines reach the dimmed level");
    }
}
