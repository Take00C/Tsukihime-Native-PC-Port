using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>
/// Messages and waits at frame level (docs/formats/wait_model.md 4-9, 12): the reveal, the click latch, the end modes, the timers, auto and skip.
/// Frame numbers are 0-based frames of the eboot (<c>host.Frame - 1</c> after a step).
/// </summary>
public sealed class MessageWaitTests
{
    private const string Page = "_WKST(F822,eq,1);_WKAD(F823,1);_WNTY(0);";
    private const string Twenty = "abcdefghijklmnopqrst";

    private static (GameRig Rig, GameHost Host) Start(string script, Action<GameRig>? lines = null, bool windowOpen = true)
    {
        var rig = new GameRig().Line(1, Twenty).Line(2, "second").Line(3, "third").Script(script);
        lines?.Invoke(rig);
        GameHost host = rig.Build();
        host.NewGame(100);
        if (windowOpen)
        {
            host.Wait.WindowAlpha = 1f;
            host.Wait.WindowTarget = 1;
        }

        return (rig, host);
    }

    private static int FrameIndex(GameHost h) => (int)h.Frame - 1;

    [Fact]
    public void ATwentyGlyphMessageWithoutAClickIsRevealedInFrame47AndAdvancesInFrame48()
    {
        // wait_model.md 5.4: a 20-glyph @e message started in frame 0 is revealed in frame 47 and advanced in frame 48 (the next statement runs then)
        (GameRig rig, GameHost host) = Start(Page + "_ZM00101($000001@e);_END_();");
        int shownAt = -1;
        while (host.Phase == GamePhase.Running && host.Frame < 200)
        {
            host.Step(default);
            if (shownAt < 0 && (host.Wait.Bits & WaitBits.Shown) != 0)
            {
                shownAt = FrameIndex(host);
            }
        }

        Assert.Equal(47, shownAt);
        Assert.Equal(GamePhase.Ended, host.Phase);
        Assert.Equal(48, FrameIndex(host));
        Assert.Equal(Twenty, System.Text.Encoding.UTF8.GetString(host.CurrentMessage!.Text));
        _ = rig;
    }

    [Fact]
    public void AnOpeningWindowHoldsTheFirstRevealUntilItIsFullyOpen()
    {
        // the window opens at +0.06 per frame (17 frames) and the reveal waits for it (wait_model.md 5.2)
        (GameRig _, GameHost host) = Start(Page + "_ZM00101($000001@e);_END_();", windowOpen: false);
        host.Run(h => h.Phase == GamePhase.Ended);
        Assert.True(FrameIndex(host) is > 60 and < 70, $"ended in frame {FrameIndex(host)}");
    }

    [Fact]
    public void AClickWaitMessageWaitsForAPressAndOnePressNeverCompletesAndDismissesIt()
    {
        (GameRig rig, GameHost host) = Start(Page + "_ZM00101($000001);_END_();");
        host.Step(default);                       // frame 0: the message starts
        Assert.True((host.Wait.Bits & WaitBits.Click) != 0);
        rig.Click();                              // frame 1: the press completes the reveal (head jumps to the end) ...
        for (int i = 0; i < 6; i++)
        {
            host.Step(default);
        }

        Assert.Equal(GamePhase.Running, host.Phase);       // ... but does not dismiss the message
        Assert.True((host.Wait.Bits & WaitBits.Shown) != 0);
        rig.Click();
        Assert.Equal(GamePhase.Ended, host.Phase);         // the next press does, and the script goes on in the same frame
    }

    [Fact]
    public void AnAtNMessageContinuesWithMsadAndTheContinueByteBlocksPushes()
    {
        (GameRig rig, GameHost host) = Start(Page + "_ZM00101($000002@n);_MSAD($000003@e);_END_();");
        host.Step(default);
        Assert.True(host.ContinueByte);
        Assert.Equal("second", System.Text.Encoding.UTF8.GetString(host.CurrentMessage!.Text));
        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.False(host.ContinueByte);
        Assert.Equal("secondthird", System.Text.Encoding.UTF8.GetString(host.CurrentMessage!.Text));
    }

    [Fact]
    public void ThePageOptionTurnsAClickWaitIntoANoClickMessageExceptAtThePageEnd()
    {
        // wait_model.md 4.3: "display end of page instantly" makes every message but the last of its page (WNTY 0) a no-click message
        (GameRig rig, GameHost host) = Start(Page + "_ZM00101($000002);_END_();");
        host.Config.PageEndInstantUnseen = 1;
        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(GamePhase.Ended, host.Phase);

        (GameRig rig2, GameHost host2) = Start("_WKST(F822,eq,1);_WKAD(F823,1);_WNTY(1);_ZM00101($000002);_END_();");
        host2.Config.PageEndInstantUnseen = 1;
        rig2.Run(h => h.Phase == GamePhase.Ended, 300);
        Assert.Equal(GamePhase.Running, host2.Phase);
    }

    [Theory]
    [InlineData("_WTTM(1000,1);_END_();", false, 60)]
    [InlineData("_WTTM(1000,1);_END_();", true, 60)]
    [InlineData("_WTTM(1000,0);_END_();", false, 60)]
    [InlineData("_WTTM(1000,0);_END_();", true, 6)]
    [InlineData("_WTTM(0,1);_END_();", false, 1)]
    public void WttmWaitsItsFramesAndOnlyTheClickableFormEndsEarly(string script, bool click, int endFrame)
    {
        (GameRig rig, GameHost host) = Start(script);
        host.Step(default);                       // frame 0 runs _WTTM
        for (int i = 1; i < 5 && host.Phase == GamePhase.Running; i++)
        {
            host.Step(default);
        }

        if (click)
        {
            rig.Click();                          // frame 5: the cancel happens in tick_reveal, the script continues in frame 6
        }

        rig.Run(h => h.Phase == GamePhase.Ended, 500);
        Assert.Equal(endFrame, FrameIndex(host));
    }

    [Fact]
    public void AutoModeAdvancesAfterTheCountdownPlusOneFrame()
    {
        // 2200 ms inside a page = 132 frames, 3000 ms at the page end = 180 frames; the advance is in frame r + countdown + 1
        foreach ((string wnty, int countdown) in new[] { ("0", 132), ("1", 180) })
        {
            (GameRig rig, GameHost host) = Start($"_WKST(F822,eq,1);_WKAD(F823,1);_WNTY({wnty});_ZM00101($000002);_END_();");
            host.SetAutoMode(true);
            int shownAt = -1;
            while (host.Phase == GamePhase.Running && host.Frame < 600)
            {
                host.Step(default);
                if (shownAt < 0 && (host.Wait.Bits & WaitBits.Shown) != 0)
                {
                    shownAt = FrameIndex(host);
                }
            }

            Assert.Equal(GamePhase.Ended, host.Phase);
            Assert.Equal(shownAt + countdown + 1, FrameIndex(host));
            _ = rig;
        }
    }

    [Fact]
    public void SkipModePassesOneMessagePerFrame()
    {
        string script = Page + string.Concat(Enumerable.Range(1, 8).Select(i => $"_ZM{i:x3}01($000002);")) + "_END_();";
        (GameRig rig, GameHost host) = Start(script);
        host.Config.FastForwardReadOnly = 0;
        host.SetSkipLatch(true);
        rig.Run(h => h.Phase == GamePhase.Ended, 100);
        Assert.True(FrameIndex(host) <= 10, $"8 messages took {FrameIndex(host) + 1} frames");
    }

    [Fact]
    public void TheReadOnlyRuleStopsSkippingAtAnUnreadMessage()
    {
        (GameRig rig, GameHost host) = Start(Page + "_ZM00101($000002);_ZM00201($000003);_END_();");
        host.SetSkipLatch(true);                  // FastForwardReadOnly = 1 by default, nothing is read yet
        rig.Run(h => h.Phase == GamePhase.Ended, 40);
        Assert.Equal(GamePhase.Running, host.Phase);
        Assert.False(host.SkipLatch);
        Assert.True(host.SkipStoppedByReadRule);
    }

    [Fact]
    public void AMessageOfAKnownBlockIsReadAndReadTextRevealsAtTheReadSpeed()
    {
        // the second playthrough of a message: _ZM00101 is read once readmax[1] >= 1; the next marker records the previous one
        (GameRig rig, GameHost host) = Start(Page + "_ZM00101($000002@e);_ZM00201($000003@e);_END_();");
        rig.Run(h => h.Phase == GamePhase.Ended);
        Assert.Equal(1, host.SystemData.GetReadmax(1));
        Assert.False(host.CurrentMessage!.Read);
        host.NewGame(100);
        host.Wait.WindowAlpha = 1f;
        host.Wait.WindowTarget = 1;
        host.Step(default);
        Assert.True(host.CurrentMessage!.Read);
        Assert.Equal(1, host.Wait.ReadSel);
    }

    [Fact]
    public void WindowCommandsMoveTheWindowAndTextFadeStates()
    {
        (GameRig rig, GameHost host) = Start(Page + "_ZM00101($000002@e);_CLO3();_ZM00201($000003@e);_CLO4(500);_CLO5(500);_CLO2();_END_();");
        host.Step(default);
        Assert.Equal(1, host.Wait.WindowTarget);
        rig.Run(h => h.F(1103) == 1 && h.Wait.WindowTarget == 0, 300);       // CLO3: the window closes, F1103 = 1
        Assert.Equal(1, host.F(1103));
        rig.Run(h => h.CurrentMessage is { } m && System.Text.Encoding.UTF8.GetString(m.Text) == "third", 300);
        Assert.Equal(0, host.F(1103));                                        // a message clears F1103
        rig.Run(h => h.Phase == GamePhase.Ended, 600);
        Assert.Empty(host.PageMessages);                                      // CLO2 cleared the page
        Assert.Equal(0f, host.Wait.WindowAlpha);
        Assert.Equal(1, host.F(1103));
    }

    [Fact]
    public void ThePageKeepsEarlierMessagesUntilTheCounterRestarts()
    {
        (GameRig rig, GameHost host) = Start(
            "_WKST(F822,eq,1);_WKAD(F823,1);_ZM00101($000002@e);_WKAD(F823,1);_ZM00201($000003@e);_WKST(F823,eq,0);_WKAD(F823,1);_ZM00301($000001@e);_END_();");
        rig.Run(h => h.PageMessages.Count == 2, 400);
        Assert.Equal(2, host.PageMessages.Count);
        rig.Run(h => h.Phase == GamePhase.Ended, 400);
        Assert.Single(host.PageMessages);
    }

    [Fact]
    public void CancelHidesAndShowsTheWindowAndANewMessageShowsItAgain()
    {
        (GameRig _, GameHost host) = Start(Page + "_ZM00101($000002);_WTTM(100,1);_WKAD(F823,1);_ZM00201($000003@e);_END_();");
        host.Step(default);
        Assert.False(host.WindowHidden);
        host.Step(new InputFrame { Pressed = PadButtons.Cancel });
        Assert.True(host.WindowHidden);
        host.Step(new InputFrame { ShellPressed = ShellActions.HideWindow });
        Assert.False(host.WindowHidden);
        host.Step(new InputFrame { ShellPressed = ShellActions.HideWindow });
        Assert.True(host.WindowHidden);
        for (int i = 0; i < 400 && host.PageMessages.Count < 2; i++)
        {
            host.Step((i & 1) == 0 ? new InputFrame { Held = PadButtons.Decide, Pressed = PadButtons.Decide } : default);
        }

        Assert.Equal(2, host.PageMessages.Count);
        Assert.False(host.WindowHidden);
    }

    [Fact]
    public void ModeWordsComeFromTheFrameInput()
    {
        (GameRig _, GameHost host) = Start(Page + "_ZM00101($000002);_WTTM(900000,1);_END_();");
        host.Config.FastForwardReadOnly = 0;
        host.Step(new InputFrame { ShellPressed = ShellActions.AutoToggle });
        Assert.True(host.AutoMode);
        host.Step(new InputFrame { ShellPressed = ShellActions.SkipToggle });
        Assert.True(host.SkipLatch);
        host.Step(new InputFrame { Pressed = PadButtons.Cancel });
        Assert.False(host.SkipLatch);
        host.Step(new InputFrame { ShellHeld = ShellActions.FastForward });
        Assert.Equal(64, host.Mode.Step);
    }
}

internal static class HostRunExtensions
{
    /// <summary>Steps without input until the predicate holds (at most <paramref name="max"/> frames).</summary>
    public static int Run(this GameHost host, Func<GameHost, bool> stop, int max = 5000)
    {
        int n = 0;
        while (n < max && !stop(host))
        {
            host.Step(default);
            n++;
        }

        return n;
    }
}
