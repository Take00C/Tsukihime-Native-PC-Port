using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Sound;
using Tsuki.Engine.Vm.Variables;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>
/// The story's two-button prompts (package K13): <c>_SELS</c> (a yes / no system prompt that stores 0 or 1 in its target) and <c>_SELA</c> (the chapter-start "save
/// your progress?" that stores nothing, and whose Yes opens the save screen), on hand-made scripts. The numbers are those of the eboot's choice code: the fade is 0.1 per
/// frame, button 0 is No and 1 is Yes, the cursor starts on neither, decide does nothing until a button is selected, cancel voids the frame.
/// </summary>
public sealed class PromptTests
{
    private const string Sels =
        "_SELS(_D002);_GOTS(_D002,NO,YES);_ZZ00101(NO);_WKST(F10,eq,10);_END_();_ZZ00201(YES);_WKST(F10,eq,20);_END_();";

    private const string Sela = "_SELA(_D001);_WKST(F10,eq,1);_END_();";

    private static GameRig Rig(string script) => new GameRig().Line(2, "left").Line(3, "right").Script(script);

    private static InputFrame Press(PadButtons b) => new() { Held = b, Pressed = b, Repeated = b };

    private static InputFrame Repeat(PadButtons b) => new() { Held = b, Repeated = b };

    private static InputFrame Pointer(float x, float y, PointerButtons pressed = PointerButtons.None) =>
        new() { Pointer = new PointerState((short)(x * 640f / 1920f), (short)(y * 448f / 1080f), true, pressed, pressed, 0, x, y) };

    /// <summary>Opens the prompt of <paramref name="script"/> and lets it fade in; returns the host with the choice selectable.</summary>
    private static GameHost Open(string script, out GameRig rig)
    {
        rig = Rig(script);
        GameHost host = rig.Build();
        host.NewGame(100);
        rig.Run(h => h.ChoiceSelectable, 100);
        Assert.True(host.ChoiceSelectable);
        return host;
    }

    private static void StepAll(GameHost host, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            host.Step(default);
        }
    }

    // -- opening -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASelsOpensTwoButtonsFadingInAtATenthPerFrameWithNoButtonSelected()
    {
        var rig = Rig(Sels);
        GameHost host = rig.Build();
        host.NewGame(100);
        int frames = rig.Run(h => h.ChoiceOpen, 20);
        Assert.True(frames <= 2);
        Assert.Equal(ChoiceKind.TwoButtons, host.ChoiceKind);
        Assert.True(host.PromptOpen);
        Assert.Equal(-1, host.ChoicePoint);
        Assert.Equal(-1, host.ChoiceCursor);
        Assert.Equal(0, host.ChoicePhase);
        Assert.False(host.ChoiceSelectable);

        // the alpha grows by 0.1 a frame to 1; the window pops from 1.05 to 1 meanwhile
        float last = host.ChoiceAlpha;
        float scale = host.PromptScale;
        Assert.InRange(scale, 1.04f, 1.0501f);
        int steps = 0;
        while (host.ChoicePhase == 0)
        {
            host.Step(default);
            steps++;
            Assert.True(host.ChoiceAlpha >= last);
            Assert.InRange(host.ChoiceAlpha - last, 0.099f, 0.101f);
            last = host.ChoiceAlpha;
            Assert.InRange(steps, 1, 12);
        }

        Assert.InRange(steps, 9, 11);
        Assert.Equal(1f, host.ChoiceAlpha);
        Assert.Equal(1f, host.PromptScale);
        Assert.True(host.ChoiceSelectable);
        Assert.Equal(2, host.ChoicePhase);
    }

    [Fact]
    public void TheMessageIsUiText216ForASelaAnd1446PlusF27ForASels()
    {
        GameHost sela = Open(Sela, out _);
        Assert.Equal(216, sela.PromptMessageId);
        GameHost sels = Open("_WKST(F27,eq,3);" + Sels, out _);
        Assert.Equal(1449, sels.PromptMessageId);
    }

    [Fact]
    public void OpeningAPromptClearsThePageAndSetsF823ToZeroAndF1103ToOne()
    {
        GameHost host = Open("_WKST(F822,eq,1);_WKAD(F823,1);_ZM00101($000002@e);_SELA(_D001);_END_();", out GameRig rig);
        Assert.Empty(host.PageMessages);
        Assert.Equal(0, host.F(823));
        Assert.Equal(1, host.F(1103));
        Assert.NotNull(rig.Host);
    }

    // -- the answer ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void DecideDoesNothingWhileNoButtonIsSelected()
    {
        GameHost host = Open(Sels, out _);
        host.Step(Press(PadButtons.Decide));
        host.Step(Press(PadButtons.Decide));
        Assert.Equal(2, host.ChoicePhase);
        Assert.Equal(-1, host.ChoiceCursor);
        Assert.Equal(0, host.F(2));
    }

    [Fact]
    public void ASelsStoresTheButtonIndexAndTheScriptBranchesOnIt()
    {
        foreach ((PadButtons dir, int button, int branch) in new[] { (PadButtons.Right, 0, 10), (PadButtons.Down, 1, 20) })
        {
            GameHost host = Open(Sels, out GameRig rig);
            host.VarState.Memory.Write(Tsuki.Engine.Vm.Variables.VarAddresses.OfF(2), 7);      // overwritten by the answer
            var decided = new List<(ChoiceKind Kind, int Point, int Value, int Cursor)>();
            host.ChoiceDecided += (k, p, v, c) => decided.Add((k, p, v, c));
            host.Step(Press(dir));
            Assert.Equal(button == 1 ? 1 : 0, host.ChoiceCursor);
            Assert.Equal(7, host.F(2));                                     // a selection stores nothing
            host.Step(Press(PadButtons.Decide));
            Assert.Equal(button, host.F(2));
            Assert.Equal(3, host.ChoicePhase);
            Assert.Equal([(ChoiceKind.TwoButtons, -1, button, button)], decided);
            rig.Run(h => h.Phase == GamePhase.Ended, 100);
            Assert.Equal(branch, host.F(10));
        }
    }

    [Fact]
    public void TheDialogFadesOutAtATenthPerFrameAndTheScriptGoesOnWhenTheAlphaIsSpent()
    {
        GameHost host = Open(Sels, out _);
        host.Step(Press(PadButtons.Right));
        host.Step(Press(PadButtons.Decide));
        Assert.Equal(1f, host.ChoiceAlpha);
        Assert.Equal(1f, host.PromptScale);
        int steps = 0;
        while (host.ChoiceOpen && steps < 40)
        {
            host.Step(default);
            steps++;
            if (host.ChoiceOpen)
            {
                Assert.InRange(host.PromptScale, 0.949f, 1f);
            }
        }

        Assert.InRange(steps, 9, 11);
        Assert.False(host.ChoiceOpen);
        Assert.Equal(ChoiceKind.None, host.ChoiceKind);
    }

    [Fact]
    public void ASelaNoStoresNothingContinuesAndNeverAsksForTheSaveScreen()
    {
        GameHost host = Open(Sela, out GameRig rig);
        bool asked = false;
        host.SavePromptAccepted += () => asked = true;
        host.VarState.Memory.Write(Tsuki.Engine.Vm.Variables.VarAddresses.OfF(1), 9);
        host.Step(Press(PadButtons.Right));                               // none -> No
        Assert.Equal(0, host.ChoiceCursor);
        host.Step(Press(PadButtons.Decide));
        rig.Run(h => h.Phase == GamePhase.Ended, 100);
        Assert.False(asked);
        Assert.Equal(1, host.F(10));
        Assert.Equal(9, host.F(1));                                      // the target of a SELA is never written
        Assert.Equal(0, host.F(1118));
    }

    [Fact]
    public void ASelaYesFadesOutThenAsksForTheSaveScreenAndWaitsUntilItIsClosed()
    {
        GameHost host = Open(Sela, out GameRig rig);
        int asked = 0;
        float alphaWhenAsked = 1f;
        host.SavePromptAccepted += () =>
        {
            asked++;
            alphaWhenAsked = host.ChoiceAlpha;
        };
        host.Step(Press(PadButtons.Down));                                // none -> Yes
        Assert.Equal(1, host.ChoiceCursor);
        host.Step(Press(PadButtons.Decide));
        Assert.Equal(4, host.ChoicePhase);
        Assert.Equal(0, asked);                                           // the dialog fades out first
        int steps = 0;
        while (asked == 0 && steps < 40)
        {
            host.Step(default);
            steps++;
        }

        Assert.InRange(steps, 9, 12);
        Assert.Equal(1, asked);
        Assert.True(alphaWhenAsked <= 0f);                                // nothing of the dialog is left on screen
        Assert.Equal(6, host.ChoicePhase);

        // the script waits for the save screen as long as it is open
        StepAll(host, 300);
        Assert.True(host.ChoiceOpen);
        Assert.Equal(0, host.F(10));
        Assert.Equal(1, asked);

        host.SavePromptScreenClosed();
        rig.Run(h => h.Phase == GamePhase.Ended, 20);
        Assert.Equal(1, host.F(10));
        Assert.Equal(1, asked);
        Assert.False(host.ChoiceOpen);
    }

    [Fact]
    public void ASelaYesWithNoScreenToOpenGoesStraightOn()
    {
        GameHost host = Open(Sela, out GameRig rig);
        host.Step(Press(PadButtons.Down));
        host.Step(Press(PadButtons.Decide));
        rig.Run(h => h.Phase == GamePhase.Ended, 100);
        Assert.Equal(1, host.F(10));
    }

    [Fact]
    public void ChooseAnswersLikeAPlayerAndCompletesTheFadeInFirst()
    {
        var rig = Rig(Sels);
        GameHost host = rig.Build();
        host.NewGame(100);
        rig.Run(h => h.ChoiceOpen, 20);
        Assert.False(host.Choose(2));
        Assert.False(host.Choose(-1));
        Assert.True(host.Choose(1));                                      // still fading in: the fade is completed
        Assert.Equal(1, host.F(2));
        Assert.Equal(3, host.ChoicePhase);
        Assert.False(host.Choose(0));                                     // already answered
        rig.Run(h => h.Phase == GamePhase.Ended, 100);
        Assert.Equal(20, host.F(10));
    }

    // -- the keys ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDirectionsMoveTheSelectionAsTheEbootsInputCodeDoes()
    {
        GameHost host = Open(Sela, out _);
        host.Step(Press(PadButtons.Right));
        Assert.Equal(0, host.ChoiceCursor);                               // none -> No
        host.Step(Press(PadButtons.Right));
        Assert.Equal(1, host.ChoiceCursor);                               // No -> Yes
        host.Step(Repeat(PadButtons.Right));
        Assert.Equal(1, host.ChoiceCursor);                               // a repeat does not wrap
        host.Step(Press(PadButtons.Right));
        Assert.Equal(0, host.ChoiceCursor);                               // a fresh press wraps to No
        host.Step(Repeat(PadButtons.Left));
        Assert.Equal(0, host.ChoiceCursor);                               // from No a repeat of left stays
        host.Step(Press(PadButtons.Left));
        Assert.Equal(1, host.ChoiceCursor);                               // a fresh press wraps to Yes
        host.Step(Press(PadButtons.Left));
        Assert.Equal(0, host.ChoiceCursor);
        host.Step(Press(PadButtons.Down));
        Assert.Equal(1, host.ChoiceCursor);                               // down is Yes, whatever the selection was
        host.Step(Press(PadButtons.Down));
        Assert.Equal(1, host.ChoiceCursor);
        host.Step(Press(PadButtons.Up));
        Assert.Equal(0, host.ChoiceCursor);                               // up is No
    }

    [Fact]
    public void LeftFromNoSelectionStartsOnYesAndRightOnNo()
    {
        GameHost left = Open(Sela, out _);
        left.Step(Press(PadButtons.Left));
        Assert.Equal(1, left.ChoiceCursor);
        GameHost right = Open(Sela, out _);
        right.Step(Press(PadButtons.Right));
        Assert.Equal(0, right.ChoiceCursor);
        GameHost leftRepeat = Open(Sela, out _);
        leftRepeat.Step(Repeat(PadButtons.Left));
        Assert.Equal(0, leftRepeat.ChoiceCursor);
    }

    [Fact]
    public void TheLeftStickMovesTheSelectionLikeTheDpad()
    {
        GameHost host = Open(Sela, out _);
        host.Step(Press(PadButtons.StickRight));
        Assert.Equal(0, host.ChoiceCursor);
        host.Step(Press(PadButtons.StickRight));
        Assert.Equal(1, host.ChoiceCursor);
        host.Step(Press(PadButtons.StickUp));
        Assert.Equal(0, host.ChoiceCursor);
        host.Step(Press(PadButtons.StickDown));
        Assert.Equal(1, host.ChoiceCursor);
        host.Step(Press(PadButtons.StickLeft));
        Assert.Equal(0, host.ChoiceCursor);
    }

    [Fact]
    public void MovingTheSelectionPlaysTheCursorSoundAndAnAnswerTheDecideSound()
    {
        GameHost host = Open(Sela, out _);
        host.TakeAudioCommands();
        host.Step(Press(PadButtons.Right));
        Assert.Contains(host.TakeAudioCommands(), c => c.Op == AudioOp.PlaySystem && c.Entry == SystemSounds.Vags(2).Left);
        host.Step(Press(PadButtons.Right));
        host.TakeAudioCommands();
        host.Step(Press(PadButtons.Up));
        host.TakeAudioCommands();
        host.Step(Press(PadButtons.Decide));
        Assert.Contains(host.TakeAudioCommands(), c => c.Op == AudioOp.PlaySystem && c.Entry == SystemSounds.Vags(0).Left);
    }

    [Fact]
    public void ACancelPressOrARightClickVoidsTheWholeFrame()
    {
        GameHost host = Open(Sels, out _);
        host.Step(Press(PadButtons.Right));
        host.Step(new InputFrame { Pressed = PadButtons.Decide | PadButtons.Cancel, Held = PadButtons.Decide | PadButtons.Cancel, Repeated = PadButtons.Decide | PadButtons.Cancel });
        Assert.Equal(2, host.ChoicePhase);                                // not answered
        host.Step(new InputFrame { Pointer = new PointerState(200, 300, true, PointerButtons.Right, PointerButtons.Right, 0), Pressed = PadButtons.Decide, Repeated = PadButtons.Decide });
        Assert.Equal(2, host.ChoicePhase);
        host.Step(new InputFrame { Pressed = PadButtons.Cancel, Repeated = PadButtons.Down });
        Assert.Equal(0, host.ChoiceCursor);                               // the move of the same frame is voided too
        host.Step(Press(PadButtons.Decide));
        Assert.Equal(3, host.ChoicePhase);
    }

    // -- the pointer ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheButtonBoxesAreTheEbootsWidgetRects()
    {
        // No at x 241 and Yes at x 327, y 238; boxes (-10, -10)-(60, 45) around those points, ends included
        Assert.Equal(0, PromptLayout.ButtonAt(241, 238));
        Assert.Equal(0, PromptLayout.ButtonAt(231, 228));
        Assert.Equal(0, PromptLayout.ButtonAt(301, 283));
        Assert.Equal(-1, PromptLayout.ButtonAt(230, 238));
        Assert.Equal(-1, PromptLayout.ButtonAt(302, 238));
        Assert.Equal(-1, PromptLayout.ButtonAt(310, 238));                // between the two
        Assert.Equal(1, PromptLayout.ButtonAt(317, 228));
        Assert.Equal(1, PromptLayout.ButtonAt(387, 283));
        Assert.Equal(-1, PromptLayout.ButtonAt(388, 238));
        Assert.Equal(-1, PromptLayout.ButtonAt(341, 227));
        Assert.Equal(-1, PromptLayout.ButtonAt(341, 284));
    }

    [Fact]
    public void ThePointerSelectsTheButtonItMovesOverAndALeftClickAnswersIt()
    {
        GameHost host = Open(Sels, out GameRig rig);
        host.Step(Pointer(1071f, 600f));                                   // over Yes
        Assert.Equal(1, host.ChoiceCursor);
        host.Step(Pointer(813f, 600f));                                   // over No
        Assert.Equal(0, host.ChoiceCursor);
        host.Step(Pointer(813f, 100f));                                   // away: the selection stays
        Assert.Equal(0, host.ChoiceCursor);
        host.Step(Pointer(1071f, 620f, PointerButtons.Left));              // a click on Yes selects it and answers it
        Assert.Equal(3, host.ChoicePhase);
        Assert.Equal(1, host.F(2));
        rig.Run(h => h.Phase == GamePhase.Ended, 100);
        Assert.Equal(20, host.F(10));
    }

    [Fact]
    public void AClickBesideBothButtonsDoesNothingAndAPointerRestingWhereTheDialogOpenedSelectsNothing()
    {
        var rig = Rig(Sels);
        GameHost host = rig.Build();
        host.NewGame(100);
        // the pointer rests over Yes before and while the dialog opens
        for (int i = 0; i < 40; i++)
        {
            host.Step(Pointer(1071f, 620f));
        }

        Assert.True(host.ChoiceSelectable);
        Assert.Equal(-1, host.ChoiceCursor);
        host.Step(Pointer(100f, 100f, PointerButtons.Left));              // beside both buttons
        Assert.Equal(2, host.ChoicePhase);
        Assert.Equal(-1, host.ChoiceCursor);
        host.Step(Pointer(100f, 100f));
        host.Step(Press(PadButtons.Down));
        host.Step(Pointer(1071f, 620f, PointerButtons.Left));
        Assert.Equal(3, host.ChoicePhase);
        Assert.Equal(1, host.F(2));
    }

    [Fact]
    public void TheWheelDoesNotMoveThePromptSelection()
    {
        GameHost host = Open(Sela, out _);
        host.Step(new InputFrame { Pointer = new PointerState(10, 10, true, PointerButtons.None, PointerButtons.None, 3) });
        Assert.Equal(-1, host.ChoiceCursor);
    }

    // -- scan mode, skip mode, the F1118 gate --------------------------------------------------------------------------------------

    [Fact]
    public void InScanModeNoneOfThePromptsOpensAndASelaStillClearsF1118()
    {
        foreach (string script in new[] { Sels, Sela, "_SELR(0,$000002);_SELR(1,$000003);_SELD(_D000,5);_WKST(F10,eq,3);_END_();" })
        {
            var rig = Rig("_WKST(F1118,eq,1);" + script);
            GameHost host = rig.Build();
            host.NewGame(100);
            host.VarState.ScanMode = true;
            host.VarState.Memory.Write(VarAddresses.OfF(2), 5);
            host.VarState.Memory.Write(VarAddresses.OfF(0), 6);
            rig.Run(h => h.Phase == GamePhase.Ended, 100);
            Assert.False(host.ChoiceOpen);
            Assert.Equal(ChoiceMode.None, host.VarState.ChoiceMode);
            Assert.Equal(script == Sela ? 0 : 1, host.F(1118));            // only a SELA clears the marker
            Assert.Equal(5, host.F(2));
            Assert.Equal(6, host.F(0));
        }
    }

    [Fact]
    public void ASelaWithF1118SetOpensNothingAndClearsTheMarker()
    {
        var rig = Rig("_WKST(F1118,eq,1);" + Sela);
        GameHost host = rig.Build();
        host.NewGame(100);
        rig.Run(h => h.Phase == GamePhase.Ended, 100);
        Assert.Equal(1, host.F(10));
        Assert.Equal(0, host.F(1118));
    }

    [Fact]
    public void SkipModeDoesNotAnswerAPromptForThePlayer()
    {
        var rig = Rig(Sela);
        GameHost host = rig.Build();
        host.NewGame(100);
        host.Config.FastForwardReadOnly = 0;                              // skipping unread lines is allowed, so the latch holds
        host.SetSkipLatch(true);
        for (int i = 0; i < 400; i++)
        {
            host.Step(Press(PadButtons.Decide));                          // clicks with no button selected answer nothing
        }

        Assert.True(host.PromptOpen);
        Assert.True(host.ChoiceSelectable);
        Assert.Equal(0, host.F(10));
        Assert.True(host.SkipLatch);
    }

    [Fact]
    public void ThePromptHoldsTheScriptAndOnlyThePromptDoes()
    {
        var rig = Rig(Sela);
        GameHost host = rig.Build();
        host.NewGame(100);
        StepAll(host, 500);
        Assert.Equal(GamePhase.Running, host.Phase);
        Assert.Equal(0, host.F(10));
        host.Choose(0);
        rig.Run(h => h.Phase == GamePhase.Ended, 100);
        Assert.Equal(1, host.F(10));
    }
}
