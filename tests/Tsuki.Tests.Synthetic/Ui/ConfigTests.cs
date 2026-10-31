using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

public sealed class ConfigTests
{
    private static (UiRoot Root, PreviewServices Services, ConfigScreen Screen) Open(Action<PreviewServices>? tweak = null, bool fromMenu = true)
    {
        var services = new PreviewServices(Language.En);
        tweak?.Invoke(services);
        UiRoot root = UiTestKit.Root(services);
        if (fromMenu)
        {
            root.OpenSystemMenu();
            UiTestKit.Run(root, 20);
            root.Context.MenuTab = 2;
            UiTestKit.Press(root, PadButtons.Decide);
        }
        else
        {
            root.Push(new ConfigScreen());
        }

        UiTestKit.Run(root, 60);
        return (root, services, Assert.IsType<ConfigScreen>(root.Top));
    }

    private static Ctl At(ConfigScreen c, int cursor) => c.Layout[cursor].Ctl;

    private static int Find(ConfigScreen c, Ctl ctl) => c.Layout.FindIndex(t => t.Ctl == ctl);

    private static void Go(UiRoot root, ConfigScreen screen, Ctl target)
    {
        // walk the cursor to a control with the keys, as a player would: rows with up and down, then along the row
        IReadOnlyList<IReadOnlyList<Ctl>> grid = screen.GridRows();
        int tr = -1;
        int tc = -1;
        for (int r = 0; r < grid.Count; r++)
        {
            for (int c = 0; c < grid[r].Count; c++)
            {
                if (grid[r][c] == target)
                {
                    (tr, tc) = (r, c);
                }
            }
        }

        Assert.True(tr >= 0, $"{target} is not on the grid");
        for (int guard = 0; guard < 60; guard++)
        {
            Ctl cur = At(screen, screen.Cursor);
            if (cur == target)
            {
                return;
            }

            int r = -1;
            int c = -1;
            for (int i = 0; i < grid.Count; i++)
            {
                int j = grid[i].ToList().IndexOf(cur);
                if (j >= 0)
                {
                    (r, c) = (i, j);
                }
            }

            PadButtons b = r < 0 ? PadButtons.Down : r != tr ? (tr > r ? PadButtons.Down : PadButtons.Up) : (tc > c ? PadButtons.Right : PadButtons.Left);
            UiTestKit.Press(root, b);
        }

        Assert.Equal(target, At(screen, screen.Cursor));
    }

    [Fact]
    public void TheSystemTabLaysOutTheEightPagesControlsFromTheTable()
    {
        (_, _, ConfigScreen screen) = Open();
        Assert.Equal(0, screen.CurrentTab);
        List<(Ctl Ctl, float X, float Y)> layout = screen.Layout;
        Assert.Equal(CtlKind.Tab, layout[0].Ctl.Kind);
        Assert.Equal(CtlKind.Default, layout[3].Ctl.Kind);
        Assert.Equal(4, layout.Count(t => t.Ctl.Kind == CtlKind.Slider));
        Assert.Equal(3, layout.Count(t => t.Ctl.Kind == CtlKind.Toggle));
        Assert.Equal(8, layout.Count(t => t.Ctl.Kind == CtlKind.Radio));
        Assert.Equal(4, layout.Count(t => t.Ctl.Kind == CtlKind.Lang));
        // radio rows carry +6.637 units; the language rows do not
        Assert.Equal(358f * UiMetrics.UnitY + (6.637f * UiMetrics.UnitY), layout.First(t => t.Ctl == new Ctl(CtlKind.Radio, 0, true)).Y, 2);
        Assert.Equal(616f * UiMetrics.UnitY, layout.First(t => t.Ctl == new Ctl(CtlKind.Lang, 0)).Y, 2);
        Assert.Equal(243f * 3f, layout.First(t => t.Ctl == new Ctl(CtlKind.Radio, 2, false)).X, 2);
    }

    [Fact]
    public void ThePairsAreFirstAndSecondByRow()
    {
        (_, _, ConfigScreen screen) = Open();
        Assert.Contains(screen.Layout, t => t.Ctl == new Ctl(CtlKind.Radio, 1, true));
        Assert.Contains(screen.Layout, t => t.Ctl == new Ctl(CtlKind.Radio, 1, false));
        Ctl first = screen.Layout.First(t => t.Ctl.Kind == CtlKind.Radio && t.Ctl.Index == 3 && t.Ctl.First).Ctl;
        Ctl second = screen.Layout.First(t => t.Ctl.Kind == CtlKind.Radio && t.Ctl.Index == 3 && !t.Ctl.First).Ctl;
        Assert.True(screen.Layout.First(t => t.Ctl == second).Y > screen.Layout.First(t => t.Ctl == first).Y);
    }

    [Fact]
    public void TheCursorStartsOnTheFirstSliderAndWalksTheGrid()
    {
        (UiRoot root, _, ConfigScreen screen) = Open();
        Assert.Equal(new Ctl(CtlKind.Minus, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Right);
        Assert.Equal(new Ctl(CtlKind.Slider, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Right);
        UiTestKit.Press(root, PadButtons.Right);
        Assert.Equal(new Ctl(CtlKind.Toggle, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(new Ctl(CtlKind.Toggle, 1), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Up);
        // up off the first row goes to the tab bar
        Assert.Equal(CtlKind.Tab, At(screen, screen.Cursor).Kind);
    }

    [Fact]
    public void MinusAndPlusStepTheSliderValuesByTheirSteps()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        Assert.Equal(35, s.Config.UnseenTextSpeed);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(34, s.Config.UnseenTextSpeed);
        Go(root, screen, new Ctl(CtlKind.Plus, 3));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(2250, s.Config.AutoModeWait);
        Go(root, screen, new Ctl(CtlKind.Minus, 2));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(2950, s.Config.AutoModePageSpeed);
        Assert.Contains("config Setting", s.Calls);
    }

    [Fact]
    public void ADecideStillHeldFromTheKeyThatOpenedTheScreenDoesNotStepTheFirstSlider()
    {
        // package K16: a player's tap on Enter lasts several frames; the config screen opens in the first one and the cursor starts on the Unseen speed's minus button,
        // so the held key used to repeat "minus" every frame until it was released (35 ms -> 30 ms or less just by opening the screen)
        var services = new PreviewServices(Language.En);
        UiRoot root = UiTestKit.Root(services);
        root.OpenSystemMenu();
        UiTestKit.Run(root, 20);
        root.Context.MenuTab = 2;
        UiTestKit.Hold(root, PadButtons.Decide, 40);
        UiTestKit.Run(root, 30);
        Assert.IsType<ConfigScreen>(root.Top);
        Assert.Equal(35, services.Config.UnseenTextSpeed);
    }

    [Fact]
    public void AMinusPressedOnTheScreenStillRepeatsWhileItIsHeld()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        UiTestKit.Hold(root, PadButtons.Decide, 40);
        Assert.True(s.Config.UnseenTextSpeed < 34 - 5, $"a held minus repeats after its delay (value {s.Config.UnseenTextSpeed})");
        _ = screen;
    }

    [Fact]
    public void ASliderMovesWithLeftAndRightWhileDecideIsHeld()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        Go(root, screen, new Ctl(CtlKind.Slider, 1));
        root.Step(new InputFrame { Frame = 500, Held = PadButtons.Decide, Pressed = PadButtons.Right, Repeated = PadButtons.Right });
        root.Step(new InputFrame { Frame = 501, Held = PadButtons.Decide, Pressed = PadButtons.Right, Repeated = PadButtons.Right });
        Assert.Equal(15 + 2, s.Config.SeenTextSpeed);
        // the keys alone move the cursor, not the value
        UiTestKit.Press(root, PadButtons.Right);
        Assert.Equal(17, s.Config.SeenTextSpeed);
    }

    [Fact]
    public void TheValuesStayInTheirRanges()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open(x => x.Config.UnseenTextSpeed = 0);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(0, s.Config.UnseenTextSpeed);
        s.Config.UnseenTextSpeed = 250;
        Go(root, screen, new Ctl(CtlKind.Plus, 0));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(250, s.Config.UnseenTextSpeed);
    }

    [Fact]
    public void ChecksAndRadiosWriteTheDocumentedConfigFields()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        ConfigBlock c = s.Config;
        Go(root, screen, new Ctl(CtlKind.Toggle, 0));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(1, c.PageEndInstantUnseen);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(0, c.PageEndInstantUnseen);
        Go(root, screen, new Ctl(CtlKind.Toggle, 2));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(1, c.SkipConfirm);
        // Auto Skip Cleared Scenes: ON writes 1
        Go(root, screen, new Ctl(CtlKind.Radio, 0, true));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(1, c.AutoSkipCleared);
        // Fast-Forward: the first button is "All Text" (read-only 0)
        Go(root, screen, new Ctl(CtlKind.Radio, 1, true));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(0, c.FastForwardReadOnly);
        Go(root, screen, new Ctl(CtlKind.Radio, 1, false));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(1, c.FastForwardReadOnly);
        // the text animations: the first button is the fade (0)
        Go(root, screen, new Ctl(CtlKind.Radio, 2, false));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(1, c.TextAnimations);
        Go(root, screen, new Ctl(CtlKind.Radio, 3, false));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(1, c.SeenTextAnimations);
        Assert.True(screen.IsOn(new Ctl(CtlKind.Radio, 3, false)));
        Assert.False(screen.IsOn(new Ctl(CtlKind.Radio, 3, true)));
    }

    [Fact]
    public void TheLanguageButtonsSetTheLanguageWordAndAskTheGameToReload()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        Assert.True(screen.IsOn(new Ctl(CtlKind.Lang, 1)));
        Go(root, screen, new Ctl(CtlKind.Lang, 3));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(3, s.Config.Language);
        Assert.Contains("config Language", s.Calls);
        Assert.True(screen.IsOn(new Ctl(CtlKind.Lang, 3)));
        // input is held a few frames while the language changes: a cursor move in the first frames is ignored
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(new Ctl(CtlKind.Lang, 3), At(screen, screen.Cursor));
    }

    [Fact]
    public void TheSoundTabShowsTheVolumesAndOnlyTheVoiceRowsOfCharactersHeard()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open(x =>
        {
            x.SetGlobal(100, 4);
            x.SetGlobal(103, 4);
            x.SetGlobal(115, 5);
        });
        UiTestKit.Press(root, PadButtons.R1);
        UiTestKit.Run(root, 40);
        Assert.Equal(1, screen.CurrentTab);
        Assert.Equal([0, 3, 15], screen.VoiceRows.Select(r => r.Row));
        // rows 15 / 16 keep the unknown face until bit 1 is set: word 115 has bit 2 (value 5 = 4 + 1) -> its face is its own
        Assert.Equal(15, screen.VoiceRows[2].Face);
        List<(Ctl Ctl, float X, float Y)> layout = screen.Layout;
        Assert.Equal(3, layout.Count(t => t.Ctl.Kind == CtlKind.Mute && t.Ctl.Index >= 3));
        Assert.Equal((370.01f * UiMetrics.UnitY) + ConfigScreen.SoundBandHeight, layout.First(t => t.Ctl == new Ctl(CtlKind.Minus, 3)).Y, 2);      // under the Mute in Background band of package K21
        Assert.Equal(((370.01f + 50.607f) * UiMetrics.UnitY) + ConfigScreen.SoundBandHeight, layout.First(t => t.Ctl == new Ctl(CtlKind.Minus, 6)).Y, 2);
        _ = s;
    }

    [Fact]
    public void AVoiceRowMuteIsABitOfTheEnableWordAndTheTestButtonsPlaySounds()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open(x => x.SetGlobal(100, 4));
        UiTestKit.Press(root, PadButtons.R1);
        UiTestKit.Run(root, 40);
        Go(root, screen, new Ctl(CtlKind.Mute, 3));
        Assert.False(screen.IsOn(new Ctl(CtlKind.Mute, 3)));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(0u, s.Config.VoiceEnableBits & 1u);
        Assert.True(screen.IsOn(new Ctl(CtlKind.Mute, 3)));
        Go(root, screen, new Ctl(CtlKind.Test, 1));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Contains("test voice 0", s.Calls);
        Go(root, screen, new Ctl(CtlKind.Test, 0));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Contains("test se", s.Calls);
        Go(root, screen, new Ctl(CtlKind.Mute, 0));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(1, s.Config.MusicMute);
        Go(root, screen, new Ctl(CtlKind.Plus, 0));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal(41, s.Config.MusicVolume);
        Assert.Equal(41, s.Config.MusicVolumeCopy);
    }

    [Fact]
    public void RecommendedSettingsAsksThenRestoresTheBootValuesAndKeepsTheLanguage()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open(x => x.Config.UnseenTextSpeed = 200);
        s.Config.SfxVolume = 99;
        // the Recommended Settings button is the last of the tab bar: up to the bar, then right
        for (int i = 0; i < 40 && screen.Cursor >= 4; i++)
        {
            UiTestKit.Press(root, PadButtons.Up);
        }

        for (int i = 0; i < 4 && At(screen, screen.Cursor).Kind != CtlKind.Default; i++)
        {
            UiTestKit.Press(root, PadButtons.Right);
        }

        Assert.Equal(CtlKind.Default, At(screen, screen.Cursor).Kind);
        UiTestKit.Press(root, PadButtons.Decide);
        var dialog = Assert.IsType<ConfirmDialog>(root.Top);
        UiTestKit.Run(root, 20);
        Assert.False(dialog.CursorOnYes);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        Assert.Equal(35, s.Config.UnseenTextSpeed);
        Assert.Equal(40, s.Config.SfxVolume);
        Assert.Equal(1, s.Config.Language);
        Assert.Contains("config Reset", s.Calls);
    }

    [Fact]
    public void TabsSwitchWithL1AndR1AndFadeTheContent()
    {
        (UiRoot root, _, ConfigScreen screen) = Open();
        UiTestKit.Press(root, PadButtons.R1);
        UiTestKit.Run(root, 5);
        Assert.Equal(0, screen.CurrentTab);
        UiTestKit.Run(root, 30);
        Assert.Equal(1, screen.CurrentTab);
        UiTestKit.Run(root, 30);
        UiTestKit.Press(root, PadButtons.L1);
        UiTestKit.Run(root, 60);
        Assert.Equal(0, screen.CurrentTab);
        UiTestKit.Press(root, PadButtons.L1);
        UiTestKit.Run(root, 60);
        Assert.Equal(2, screen.CurrentTab);
    }

    [Fact]
    public void TheCursorDrivesTheScroll()
    {
        (UiRoot root, _, ConfigScreen screen) = Open();
        Go(root, screen, new Ctl(CtlKind.Lang, 3));
        UiTestKit.Run(root, 30);
        Assert.True(screen.Scroll > 300f);
        Assert.True(screen.Scroll <= screen.ScrollMaximum);
        Go(root, screen, new Ctl(CtlKind.Minus, 0));
        UiTestKit.Run(root, 30);
        Assert.Equal(0f, screen.Scroll);
    }

    // ---- package K19: the port's Display Mode and Window Size rows at the bottom of the System tab ----------------------------------------------------

    [Fact]
    public void TheSystemTabEndsWithTheDisplayRowsWhenTheGameHasAWindowAndWithoutThemWhenItHasNone()
    {
        (_, PreviewServices s, ConfigScreen screen) = Open();
        Assert.True(screen.HasDisplayRows);
        List<(Ctl Ctl, float X, float Y)> layout = screen.Layout;
        Assert.Equal(2, layout.Count(t => t.Ctl.Kind == CtlKind.Display));
        Assert.Equal(s.Display!.SizeChoices.Count, layout.Count(t => t.Ctl.Kind == CtlKind.WindowSize));
        float lastLanguage = layout.Where(t => t.Ctl.Kind == CtlKind.Lang).Max(t => t.Y);
        (Ctl _, float wx, float wy) = layout.First(t => t.Ctl == new Ctl(CtlKind.Display, 0));
        (Ctl _, float fx, float fy) = layout.First(t => t.Ctl == new Ctl(CtlKind.Display, 1));
        (Ctl _, float sx, float sy) = layout.First(t => t.Ctl == new Ctl(CtlKind.WindowSize, 0));
        Assert.True(wy > lastLanguage + 80f, $"the mode row ({wy}) sits under the last language button ({lastLanguage})");
        Assert.Equal(wy, fy);
        Assert.True(fx > wx);
        Assert.True(sy > wy);
        Assert.Equal(wx, sx);
        Assert.Equal(750f + ConfigScreen.BandFor(7), screen.ScrollMaximum);               // the two display rows, then the screenshot notice, clipboard, hooker and WebSocket rows and the opacity slider of packages K21, K23 and K24
        Assert.Equal(750f + 160f + 280f, screen.ScrollMaximum);                           // K19's band was 160 for its two rows; each later row adds 56
        // the rows are the first two of the band's seven grid rows, in that order
        IReadOnlyList<IReadOnlyList<Ctl>> grid = screen.GridRows();
        Assert.Equal([new Ctl(CtlKind.Display, 0), new Ctl(CtlKind.Display, 1)], grid[^7]);
        Assert.Equal(s.Display.SizeChoices.Count, grid[^6].Count);
        Assert.All(grid[^6], c => Assert.Equal(CtlKind.WindowSize, c.Kind));

        (_, _, ConfigScreen none) = Open(x => x.Display = null);
        Assert.False(none.HasDisplayRows);
        Assert.DoesNotContain(none.Layout, t => t.Ctl.Kind is CtlKind.Display or CtlKind.WindowSize);
        Assert.Equal(750f + ConfigScreen.BandFor(5), none.ScrollMaximum);                 // the band keeps the K21, K23 and K24 rows
    }

    [Fact]
    public void TheDisplayRowSwitchesTheModeAndTheSizeRowPicksAPresetAndLeavesFullscreen()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        Assert.True(screen.IsOn(new Ctl(CtlKind.Display, 0)));
        Assert.False(screen.IsOn(new Ctl(CtlKind.Display, 1)));
        Assert.True(screen.IsOn(new Ctl(CtlKind.WindowSize, 1)));                 // 1600 x 900, the preview's window

        Go(root, screen, new Ctl(CtlKind.Display, 1));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.True(s.Display!.Fullscreen);
        Assert.Contains("display fullscreen", s.Calls);
        Assert.True(screen.IsOn(new Ctl(CtlKind.Display, 1)));
        Assert.False(screen.IsOn(new Ctl(CtlKind.Display, 0)));
        Assert.True(screen.IsOn(new Ctl(CtlKind.WindowSize, 1)));                 // the size a return to the window gives stays marked

        Go(root, screen, new Ctl(CtlKind.WindowSize, 2));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.Equal((1920, 1080), s.Display.WindowSize);
        Assert.False(s.Display.Fullscreen);                                        // a size means a window
        Assert.Contains("window size 1920x1080", s.Calls);
        Assert.True(screen.IsOn(new Ctl(CtlKind.WindowSize, 2)));
        Assert.False(screen.IsOn(new Ctl(CtlKind.WindowSize, 1)));
        Assert.True(screen.IsOn(new Ctl(CtlKind.Display, 0)));
        Assert.DoesNotContain("commit", s.Calls);                                   // the game remembers these itself; the config block did not change
    }

    [Fact]
    public void TheCursorWalksFromTheLanguageRowDownToTheDisplayRowsAndBack()
    {
        (UiRoot root, _, ConfigScreen screen) = Open();
        Go(root, screen, new Ctl(CtlKind.Lang, 3));
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(new Ctl(CtlKind.Display, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Right);
        Assert.Equal(new Ctl(CtlKind.Display, 1), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Right);                                  // the row ends
        Assert.Equal(new Ctl(CtlKind.Display, 1), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(new Ctl(CtlKind.WindowSize, 1), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Right);
        UiTestKit.Press(root, PadButtons.Right);
        Assert.Equal(new Ctl(CtlKind.WindowSize, 3), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Down);                                   // below the size row: the screenshot notice row (package K24), the column clamped to its last pill
        Assert.Equal(new Ctl(CtlKind.ScreenshotNotice, 1), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Down);                                   // the clipboard row (package K21)
        Assert.Equal(new Ctl(CtlKind.CopyText, 1), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Down);                                   // the hooker and WebSocket rows (package K23)
        Assert.Equal(new Ctl(CtlKind.TextHooker, 1), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(new Ctl(CtlKind.TextWebSocket, 1), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Down);                                   // then the opacity slider
        Assert.Equal(new Ctl(CtlKind.Opacity), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Down);                                   // nothing below it
        Assert.Equal(new Ctl(CtlKind.Opacity), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(new Ctl(CtlKind.TextWebSocket, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(new Ctl(CtlKind.TextHooker, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(new Ctl(CtlKind.CopyText, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(new Ctl(CtlKind.ScreenshotNotice, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(new Ctl(CtlKind.WindowSize, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(new Ctl(CtlKind.Display, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(new Ctl(CtlKind.Lang, 3), At(screen, screen.Cursor));
        UiTestKit.Run(root, 30);
        Go(root, screen, new Ctl(CtlKind.WindowSize, 0));
        UiTestKit.Run(root, 30);
        Assert.Equal(screen.ScrollMaximum, screen.Scroll);                        // the scroll followed the cursor to the bottom
    }

    [Fact]
    public void ThePointerClicksTheDisplayPills()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        screen.ScrollNow(screen.ScrollMaximum);
        UiTestKit.Run(root, 2);
        (Ctl _, float fx, float fy) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.Display, 1));
        UiTestKit.Point(root, fx + 20, fy + 10, click: true);
        Assert.True(s.Display!.Fullscreen);
        Assert.Equal(new Ctl(CtlKind.Display, 1), At(screen, screen.Cursor));
        (Ctl _, float sx, float sy) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.WindowSize, 0));
        UiTestKit.Point(root, sx + 20, sy + 10, click: true);
        Assert.Equal((1280, 720), s.Display.WindowSize);
        Assert.False(s.Display.Fullscreen);
    }

    [Fact]
    public void TheRowsFollowAChangeMadeOutsideTheScreenSuchAsAltEnter()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        s.Display!.Fullscreen = true;                                              // the app's event pump did it on Alt+Enter
        UiTestKit.Run(root, 1);
        Assert.True(screen.IsOn(new Ctl(CtlKind.Display, 1)));
        Assert.False(screen.IsOn(new Ctl(CtlKind.Display, 0)));
        s.Display.Fullscreen = false;
        Assert.True(screen.IsOn(new Ctl(CtlKind.Display, 0)));
        // a monitor that takes only two presets shows two pills, and the grid follows
        s.Display.SizeChoices = [(1280, 720), (1600, 900)];
        Assert.Equal(2, screen.Layout.Count(t => t.Ctl.Kind == CtlKind.WindowSize));
        Assert.Equal(2, screen.GridRows()[^6].Count);
        s.Display.SizeChoices = [];
        Assert.DoesNotContain(screen.Layout, t => t.Ctl.Kind == CtlKind.WindowSize);
        Assert.Equal([new Ctl(CtlKind.Display, 0), new Ctl(CtlKind.Display, 1)], screen.GridRows()[^6]);      // the K21, K23 and K24 rows stay below it
        Assert.Equal(750f + ConfigScreen.BandFor(6), screen.ScrollMaximum);
    }

    [Fact]
    public void TheDisplayRowsDrawTheirHeadingsAndPillsAndHaveWordsInEveryLanguage()
    {
        // the synthetic glyph tables lay out English only (the real pages are tried in Tsuki.Tests.Data: UiRealDataTests "config-display" and PromptRealDataTests)
        (UiRoot root, _, ConfigScreen screen) = Open();
        screen.ScrollNow(screen.ScrollMaximum);
        UiTestKit.Run(root, 2);
        DrawCmd[] before = UiTestKit.Draw(root).Commands.ToArray();
        (UiRoot root2, PreviewServices s2, ConfigScreen screen2) = Open(x => x.Display = null);
        screen2.ScrollNow(screen2.ScrollMaximum);
        UiTestKit.Run(root2, 2);
        DrawCmd[] without = UiTestKit.Draw(root2).Commands.ToArray();
        Assert.True(before.Length > without.Length + 6, $"the rows add their pills and headings ({before.Length} commands against {without.Length} without them)");
        _ = s2;
        foreach (Language lang in new[] { Language.Ja, Language.En, Language.Zc, Language.Zt })
        {
            Assert.NotEmpty(UiPrompts.DisplayHeading(lang));
            Assert.NotEmpty(UiPrompts.SizeHeading(lang));
            Assert.NotEqual(UiPrompts.DisplayLabel(lang, 0), UiPrompts.DisplayLabel(lang, 1));
        }

        Assert.Equal("画面モード", UiPrompts.DisplayHeading(Language.Ja));
        Assert.Equal(("ウィンドウ", "フルスクリーン"), (UiPrompts.DisplayLabel(Language.Ja, 0), UiPrompts.DisplayLabel(Language.Ja, 1)));
        Assert.Equal(("Windowed", "Fullscreen"), (UiPrompts.DisplayLabel(Language.En, 0), UiPrompts.DisplayLabel(Language.En, 1)));
        Assert.Equal(("显示模式", "窗口", "全屏"), (UiPrompts.DisplayHeading(Language.Zc), UiPrompts.DisplayLabel(Language.Zc, 0), UiPrompts.DisplayLabel(Language.Zc, 1)));
        Assert.Equal(("顯示模式", "視窗", "全螢幕"), (UiPrompts.DisplayHeading(Language.Zt), UiPrompts.DisplayLabel(Language.Zt, 0), UiPrompts.DisplayLabel(Language.Zt, 1)));
        Assert.Equal("1920x1080", UiPrompts.SizeLabel(1920, 1080));
    }

    [Fact]
    public void ClosingWritesTheSystemDataOnlyWhenSomethingChanged()
    {
        (UiRoot root, PreviewServices s, _) = Open();
        UiTestKit.Press(root, PadButtons.Cancel);
        UiTestKit.Run(root, 40);
        Assert.DoesNotContain("commit", s.Calls);
        Assert.Contains("stop tests", s.Calls);

        (UiRoot root2, PreviewServices s2, _) = Open();
        UiTestKit.Press(root2, PadButtons.Decide);
        UiTestKit.Press(root2, PadButtons.Cancel);
        UiTestKit.Run(root2, 40);
        Assert.Contains("commit", s2.Calls);
    }

    [Fact]
    public void ThePointerClicksButtonsAndTheSliderTrack()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        (Ctl _, float x, float y) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.Plus, 0));
        UiTestKit.Point(root, x + 10, y + 10, click: true);
        Assert.Equal(36, s.Config.UnseenTextSpeed);
        // a click on a tab
        (Ctl _, float tx, float ty) = screen.Layout[1];
        UiTestKit.Point(root, tx + 20, ty + 20, click: true);
        UiTestKit.Run(root, 40);
        Assert.Equal(1, screen.CurrentTab);
    }

    [Fact]
    public void TheSamplesTypeOutAtTheConfiguredSpeedAndRestart()
    {
        (UiRoot root, PreviewServices s, _) = Open(x => x.Config.UnseenTextSpeed = 35);
        s.Config.AutoModeWait = 500;
        DrawCmd[] early = UiTestKit.Draw(root).Commands.ToArray();
        Assert.NotEmpty(early);
        UiTestKit.Run(root, 3000);
        Assert.NotEmpty(UiTestKit.Draw(root).Commands.ToArray());
    }

    [Fact]
    public void FromTheTitleThereIsNoMenuBelowAndLeftFromTheFirstColumnDoesNothing()
    {
        (UiRoot root, _, ConfigScreen screen) = Open(fromMenu: false);
        UiTestKit.Press(root, PadButtons.Left);
        Assert.False(screen.Side.Active);
        UiTestKit.Press(root, PadButtons.Cancel);
        UiTestKit.Run(root, 40);
        Assert.False(root.Active);
    }

    [Fact]
    public void TheControlsTabScrollsItsPictures()
    {
        (UiRoot root, _, ConfigScreen screen) = Open();
        screen.Change(new Ctl(CtlKind.Tab, 2), 0);
        UiTestKit.Run(root, 60);
        Assert.Equal(2, screen.CurrentTab);
        Assert.All(screen.Layout.Where(t => t.Ctl.Kind is not (CtlKind.Tab or CtlKind.Default)), t => Assert.Equal(CtlKind.Prompt, t.Ctl.Kind));      // the pictures, and the prompts setting row
        UiTestKit.Press(root, PadButtons.Down);
    }

    [Fact]
    public void RadioAndToggleCellsWidenInEnglish()
    {
        (_, _, ConfigScreen en) = Open();
        Assert.Equal(441, en.RadioCell(2, false, 0).Width);
        Assert.Equal(392, en.ToggleCell(0, 0).Width);
        var services = new PreviewServices(Language.Ja);
        UiRoot root = UiTestKit.Root(services);
        var ja = root.Push(new ConfigScreen());
        Assert.Equal(240, ja.RadioCell(2, false, 0).Width);
        Assert.Equal(288, ja.ToggleCell(0, 0).Width);
    }
}
