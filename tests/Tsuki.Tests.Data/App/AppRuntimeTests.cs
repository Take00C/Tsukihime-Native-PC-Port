using System.Text;
using Tsuki.App.Play;
using Tsuki.App.Services;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;
using Tsuki.Platform.Input;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Data.App;

/// <summary>
/// The app runtime over the user's game with no window, GPU or audio device: the boot notice, the title, the prologue and the first scenes by autoplay, then
/// save, load, rewind, the backlog and the menus on the real story (package K6).
/// </summary>
public sealed class AppRuntimeTests(ITestOutputHelper output)
{
    private static GameFolderSource Source => SharedSource.Source;

    private sealed record Snap(int Script, int Scene, string Text, int[] Layers, int Bgm, string[] Lines);

    private sealed class Rig
    {
        private readonly DisplayList _list = new(4096);
        private long _nominal;

        public Rig(SaveStore? store = null, string autoplay = "skip")
        {
            store ??= NewStore();
            Store = store;
            var scripts = new ScriptCache(Source);
            Services = new AppServices(new AppServicesParts
            {
                Data = Source,
                Store = store,
                InitialLanguage = Language.En,
                HostFactory = (language, profile) => new GameHost(
                    new GameServices(Source, scripts)
                    {
                        SeLookup = (name, flags) => Source.SoundEffectNames.Lookup(name, flags)?.Index,
                        Layouts = new GamePictureLayouts(Source),
                    },
                    new GameOptions { Language = language, VirtualMovies = true },
                    profile),
            });
            Ui = new UiRoot(Source, Services, new DataParts(Source));
            Runtime = new AppRuntime(Services, Ui, null, null);
            Auto = new AutoPlayer(autoplay, ChoicePolicy.Parse("first")) { HoldChoice = true };
            Input = new ScriptedInput(null, Auto) { Runtime = Runtime };
            Runtime.Start();
            Hook();
        }

        public SaveStore Store { get; }

        /// <summary>The display list of the last step.</summary>
        public DisplayList List => _list;

        public AppServices Services { get; }

        public UiRoot Ui { get; }

        public AppRuntime Runtime { get; }

        public AutoPlayer Auto { get; }

        public ScriptedInput Input { get; set; }

        public GameHost Host => Services.Host;

        public List<(int Script, int Offset, string Text)> Messages { get; } = [];

        public static SaveStore NewStore()
        {
            var store = new SaveStore();
            store.SystemData.Block.Magic = SystemBlock.MagicValue;
            store.SystemData.Config.CopyFrom(ConfigBlock.CreateBootDefaults());
            store.SystemData.Config.Language = -1;
            return store;
        }

        private void Hook() =>
            Host.MessageStarted += (script, offset, _) =>
                Messages.Add((script, offset, Host.CurrentMessage is { } m ? Encoding.UTF8.GetString(m.Text) : string.Empty));

        public InputScript? Script { get; set; }

        /// <summary>When set, every step must end with the story running or a screen open (issue #7, package K38: the dead black screen that took no key).</summary>
        public bool NoDeadFrames { get; set; }

        public void Step()
        {
            _nominal += 16_666_667;
            InputFrame input = Input.Sample(Runtime.Frame + 1);
            Runtime.Step(input, _nominal, _list);
            if (NoDeadFrames)
            {
                Assert.True(Services.StoryActive || Ui.Active, $"step {Runtime.Frame}: no story and no screen (the dead black screen of issue #7)");
            }
        }

        public bool RunUntil(Func<bool> condition, int limit)
        {
            for (int i = 0; i < limit; i++)
            {
                if (condition())
                {
                    return true;
                }

                Step();
            }

            return condition();
        }

        public void Run(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                Step();
            }
        }

        /// <summary>Plays the given script (frames relative to now) in addition to autoplay.</summary>
        public void Play(string text)
        {
            long now = Runtime.Frame;
            InputScript s = InputScript.Parse(string.Join(';', text.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e =>
            {
                int c = e.IndexOf(':');
                return (long.Parse(e[..c], System.Globalization.CultureInfo.InvariantCulture) + now).ToString(System.Globalization.CultureInfo.InvariantCulture) + e[c..];
            })));
            Input = new ScriptedInput(s, Auto) { Runtime = Runtime };
        }

        public Snap Take()
        {
            GameHost h = Host;
            int[] layers = [.. h.Stage.Drawn.Select(r => r.Used != 0 ? r.Pic : -1)];
            string text = h.CurrentMessage is { } m ? Encoding.UTF8.GetString(m.Text) : string.Empty;
            string[] lines = [.. h.ChoiceLines.Select(l => Encoding.UTF8.GetString(l.Text))];
            return new Snap(h.Script, h.SceneId, text, layers, h.Sound.ScriptTrack, lines);
        }
    }

    [GameDataFact]
    public void ThePartsArchiveServesTheBootNoticeAndNothingPastItsEnd()
    {
        Picture? notice = Source.Parts(8);
        Assert.NotNull(notice);
        Assert.True(notice.Width >= 1280);
        Assert.Null(Source.Parts(100_000));
    }

    [GameDataFact]
    public void FromBootThroughTheTitleAndThePrologueToTheFirstChoice()
    {
        var rig = new Rig();
        Assert.Equal(UiMode.Title, rig.Ui.Mode);
        Assert.IsType<BootScreen>(rig.Ui.Stack[0]);

        bool reached = rig.RunUntil(() => rig.Host.ChoiceSelectable, 9000);
        Assert.True(reached, "the first choice was never reached");
        output.WriteLine($"first choice at frame {rig.Runtime.Frame}: scene {rig.Host.SceneId}, {rig.Host.ChoiceLines.Count} lines, {rig.Messages.Count} messages");
        Assert.Equal(UiMode.Story, rig.Ui.Mode);
        Assert.Equal(300, rig.Host.SceneId);
        Assert.Equal(3, rig.Host.ChoiceLines.Count);
        Assert.True(rig.Services.StoryActive);
        Assert.Empty(rig.Host.UnknownCommands);
    }

    /// <summary>
    /// Package K14: the title of the real game on the real sheets, driven by keys alone with no pointer: the logo and the menu are on the screen, the keys (arrows,
    /// W / S) move the cursor, and after the config screen has been opened and closed they come back (before the fix the backdrop it left up kept them hidden for good).
    /// </summary>
    [GameDataFact]
    public void TheTitleShowsItsLogoAndMenuByKeysAloneAndAfterTheConfigScreen()
    {
        var rig = new Rig();
        rig.Auto.Paused = true;
        rig.Services.SetGlobal(110 + (485 >> 4), (ushort)(1 << (485 & 15)));       // the prologue was read
        rig.Services.SetGlobal(10, 1);                                             // a story was started
        TitleScreen? Title() => rig.Ui.Find<TitleScreen>();

        // the boot notice, the intro movie and the caution all end with a button; stop pressing once the title itself fades in
        for (int i = 0; i < 400 && Title() is not { Phase: not TitlePhase.Intro }; i++)
        {
            rig.Play("0:Enter");
            rig.Run(30);
        }

        Assert.True(Title() is { Phase: not TitlePhase.Intro });
        Assert.True(rig.RunUntil(() => Title() is { Idle: true }, 600));
        rig.Run(260);

        static float Alpha(in DrawCmd c) => (c.V0.Rgba >> 24) / 255f;
        DrawCmd[] Strong(int child) => [.. rig.List.Commands.ToArray().Where(c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts } t && t.C == child && Alpha(c) > 0.98f)];
        void AssertShown()
        {
            Assert.NotEmpty(Strong(1));                      // the logo
            Assert.True(Strong(0).Length >= 4);              // the four menu items (a sheet tile each or more)
            Assert.Contains(rig.List.Commands.ToArray(), c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.TitleParts, C: 4 } && Alpha(c) > 0f);      // the moon glow (it pulses)
        }

        AssertShown();
        Assert.Equal(TitleItem.NewGame, Title()!.Cursor);
        rig.Play("0:Down");
        rig.Run(5);
        Assert.Equal(TitleItem.Load, Title()!.Cursor);
        rig.Play("0:S");
        rig.Run(5);
        Assert.Equal(TitleItem.Config, Title()!.Cursor);
        rig.Play("0:W");
        rig.Run(5);
        Assert.Equal(TitleItem.Load, Title()!.Cursor);
        rig.Play("0:S");
        rig.Run(5);
        AssertShown();

        // CONFIG and back by keys, nothing else: the logo and the menu come back by themselves
        rig.Play("0:Enter");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is ConfigScreen, 60));
        rig.Run(120);
        Assert.True(rig.Ui.Context.Backdrop.Visible);
        rig.Play("0:Esc");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is TitleScreen, 120));
        rig.Run(400);
        Assert.False(rig.Ui.Context.Backdrop.Visible);
        AssertShown();
    }

    /// <summary>
    /// Package K14: a profile that read the prologue but never started the story sees PRESS ANY BUTTON on the title (docs/formats/system_flow.md 4); with the keyboard prompt
    /// style its text is PRESS ANY KEY. The text must show (it blinks between 0.1 and 1 of the menu alpha), and a key the game binds to nothing, or a click, must advance it.
    /// </summary>
    [GameDataTheory]
    [InlineData("Other")]
    [InlineData("click=960,540")]
    public void PressAnyKeyShowsItsTextAndAnyKeyOrClickAdvancesIt(string press)
    {
        var rig = new Rig();
        rig.Auto.Paused = true;
        rig.Services.SetGlobal(110 + (485 >> 4), (ushort)(1 << (485 & 15)));       // the prologue was read; G010 stays 0: no story was started
        TitleScreen? Title() => rig.Ui.Find<TitleScreen>();

        // the boot notice ends on a key; there is no intro for a profile that never started the story
        rig.Run(120);
        rig.Play("0:Other");
        Assert.True(rig.RunUntil(() => Title() is not null, 200));
        Assert.True(rig.RunUntil(() => Title() is { Idle: true }, 600));
        rig.Run(200);
        Assert.True(Title()!.PressMode);
        Assert.Null(Title()!.Cursor);

        // the glyphs of PRESS ANY KEY: the English sans pages, one quad per letter (plus outline copies), at full strength once in a blink (a blink is 120 steps)
        float best = 0f;
        for (int i = 0; i < 130; i++)
        {
            rig.Run(1);
            DrawCmd[] glyphs = [.. rig.List.Commands.ToArray().Where(c => c.Texture.Kind == TextureKind.GlyphPage)];
            Assert.True(glyphs.Length >= "PRESSANYKEY".Length, $"{glyphs.Length} glyph quads");
            best = Math.Max(best, glyphs.Max(c => (c.V0.Rgba >> 24) / 255f));
        }

        Assert.True(best > 0.9f, $"PRESS ANY KEY never got stronger than {best}");

        rig.Play("0:" + press);
        Assert.True(rig.RunUntil(() => Title() is { Phase: TitlePhase.Caution }, 10), "the press did not advance PRESS ANY KEY");
    }

    [GameDataFact]
    public void SaveLoadAndRewindRoundTripOnTheRealStory()
    {
        var rig = new Rig();
        Assert.True(rig.RunUntil(() => rig.Host.ChoiceSelectable, 9000));
        Snap atChoice = rig.Take();
        Assert.Equal(3, atChoice.Lines.Length);
        Assert.Contains(atChoice.Layers, p => p >= 0);                           // a background is on stage

        // rewind pages back, then play forward to the same choice again
        Assert.True(rig.Services.Save(0));
        UiSlot slot = rig.Services.Slots[0];
        Assert.True(slot.Used);
        Assert.False(string.IsNullOrWhiteSpace(slot.Notes));
        output.WriteLine($"slot 0: phase {slot.Phase}, title {slot.SceneTitle}, notes '{slot.Notes}'");
        Assert.True(rig.Host.CanRewind);
        Assert.True(rig.Host.RewindStep());
        Assert.Equal(RestoreKind.Rewind, rig.Host.LastRestore!.Kind);
        Assert.False(rig.Host.ChoiceOpen);
        string rewound = rig.Take().Text;
        Assert.Contains(rig.Messages, m => m.Text == rewound);                   // a line the player has seen
        Assert.True(rig.Host.RewindStep());
        Assert.Equal(2, rig.Host.RestoreCount);
        int before = rig.Messages.Count;
        Assert.True(rig.RunUntil(() => rig.Host.ChoiceSelectable, 6000), "the choice did not come back after the rewind");
        Snap after = rig.Take();
        Assert.Equal(atChoice.Lines, after.Lines);
        Assert.Equal(atChoice.Text, after.Text);
        Assert.Equal(atChoice.Layers, after.Layers);
        output.WriteLine($"rewound to '{rewound}', {rig.Messages.Count - before} messages shown again; restores {rig.Host.RestoreCount}");

        // play on past the choice, then load the save: the saved page runs again and the same choice opens with the same stage
        rig.Auto.HoldChoice = false;
        rig.Run(400);
        Assert.True(rig.Auto.ChoicesTaken >= 1);
        rig.Auto.HoldChoice = true;
        Assert.True(rig.Services.Load(0));
        Assert.Equal(3, rig.Host.RestoreCount);
        Assert.True(rig.RunUntil(() => rig.Host.ChoiceSelectable, 6000), "the choice did not come back after the load");
        Snap again = rig.Take();
        Assert.Equal(atChoice.Scene, again.Scene);
        Assert.Equal(atChoice.Lines, again.Lines);
        Assert.Equal(atChoice.Text, again.Text);
        Assert.Equal(atChoice.Layers, again.Layers);
        Assert.Equal(atChoice.Bgm, again.Bgm);
    }

    [GameDataFact]
    public void TheBacklogAndTheMenusWorkOnTheRealStory()
    {
        var rig = new Rig();
        Assert.True(rig.RunUntil(() => rig.Host.ChoiceSelectable, 9000));
        IReadOnlyList<BacklogPage> pages = rig.Services.Backlog;
        Assert.NotEmpty(pages);
        Assert.NotEmpty(pages[0].Messages);
        string first = Encoding.UTF8.GetString(pages[0].Messages[0].Text.Span);
        Assert.False(string.IsNullOrWhiteSpace(first));

        // Esc opens the system menu over the story and the story stands still under it
        long cursor = rig.Host.Machine.Cursor;
        rig.Play("3:Esc");
        Assert.True(rig.RunUntil(() => rig.Ui.Find<SystemMenuScreen>() is not null, 30));
        Assert.Equal(UiMode.Overlay, rig.Ui.Mode);
        rig.Run(30);
        Assert.Equal(cursor, rig.Host.Machine.Cursor);

        // Save, Decide on the first row: a manual save appears
        rig.Play("40:Decide*2;120:Decide*30");
        Assert.True(rig.RunUntil(() => rig.Services.Slots.Any(s => s.Used), 400), "the save list did not store a slot");
        Assert.True(rig.Services.Slots.Count(s => s.Used) >= 1);

        // Esc closes the menu again and the choice is still open
        for (int i = 0; i < 6 && rig.Ui.Active; i++)
        {
            rig.Play("2:Esc");
            rig.Run(60);
        }

        Assert.False(rig.Ui.Active, "the menus did not close");
        Assert.True(rig.Host.ChoiceOpen);

        // F5 quick saves with its banner, F9 asks and loads
        rig.Run(310);                                                            // the quick-save cooldown from the 'save' is not shared, but keep clear of it
        rig.Play("3:F5");
        Assert.True(rig.RunUntil(() => rig.Services.HasQuickSave, 60));
        Assert.NotNull(rig.Ui.Banner);
        rig.Run(150);
        rig.Play("3:F9;60:Left;75:Decide*30");
        Assert.True(rig.RunUntil(() => rig.Host.RestoreCount == 1, 600), "the quick load did not run");
        Assert.True(rig.RunUntil(() => rig.Host.ChoiceSelectable, 6000));
    }

    [GameDataFact]
    public void AGalleryReplayPlaysTheSceneAndReturnsToTheGallery()
    {
        var rig = new Rig();
        // the player has been through the prologue before (scene bit 485), so the title does not start it again
        rig.Services.SetGlobal(110 + (485 >> 4), (ushort)(1 << (485 & 15)));
        rig.Run(5);
        rig.Ui.Clear();
        rig.Auto.HoldChoice = false;
        rig.Services.ReplayScene(240, node: 0);
        Assert.True(rig.Services.StoryActive);
        Assert.True(rig.Services.ReplayActive);
        Assert.True(rig.RunUntil(() => rig.Ui.Find<GalleryScreen>() is not null, 9000), "the replay never ended in the gallery");
        Assert.False(rig.Services.StoryActive);
        output.WriteLine($"replay of scene 240 ended in the gallery at frame {rig.Runtime.Frame}");
    }

    // ---- issue #7 (package K38): a scene entered from a flowchart, then Esc and the flowchart again ------------------------------------------------------------

    private static IUiGalleryTables GalleryTables => Source.Eboot.Gallery ?? throw new InvalidOperationException("the game has no gallery tables");

    /// <summary>The profile has finished the scenes of the first <paramref name="columns"/> chart columns (the system data's scene and edge bits; all of them for -1).</summary>
    private static void FinishScenes(Rig rig, int columns = -1)
    {
        foreach (ChartNodeRow row in GalleryTables.ChartColumns.Take(columns < 0 ? int.MaxValue : columns).SelectMany(c => c.Nodes))
        {
            if (row.Scene > 0)
            {
                SystemData.SetSceneSeen(rig.Host.Vars, row.Scene);
                SystemData.SetSceneDone(rig.Host.Vars, row.Scene);
            }

            if (row.Link >= 564)
            {
                SystemData.SetEdgeSeen(rig.Host.Vars, row.Link);
            }
        }
    }

    /// <summary>The playthrough has passed the scenes and edges of the first <paramref name="columns"/> chart columns (its own marks F[201..272], F[600..687]).</summary>
    private static void PlayedThrough(Rig rig, int columns)
    {
        foreach (ChartNodeRow row in GalleryTables.ChartColumns.Take(columns).SelectMany(c => c.Nodes))
        {
            if (row.Scene is > 0 and <= 0x23F)
            {
                rig.Host.Vars.SetF(201 + (row.Scene >> 3), (ushort)(rig.Host.Vars.GetF(201 + (row.Scene >> 3)) | (1 << (row.Scene & 7))));
            }

            int e = row.Link - 564;
            if (e is >= 0 and < 704)
            {
                rig.Host.Vars.SetF(600 + (e >> 3), (ushort)(rig.Host.Vars.GetF(600 + (e >> 3)) | (1 << (e & 7))));
            }
        }
    }

    private static Rig ProfileWithProgress()
    {
        var rig = new Rig();
        rig.Auto.Paused = true;
        rig.Services.SetGlobal(110 + (485 >> 4), (ushort)(1 << (485 & 15)));       // the prologue was read
        rig.Services.SetGlobal(10, 1);                                             // a story was started
        return rig;
    }

    private static string Screens(Rig rig) => string.Join('>', rig.Ui.Stack.Select(s => s.GetType().Name));

    private static string Centre(RectF r) => $"{(int)(r.X + (r.W / 2f))},{(int)(r.Y + (r.H / 2f))}";

    // the Yes of a confirm dialog (object 0 at x 330, y 249.01 of 640 x 448, touch rect (0, -2)-(72, 24); Dialogs.ConfirmDialog.OnOpened)
    private static readonly string DialogYes = Centre(new RectF(330f * UiMetrics.UnitX, 247f * UiMetrics.UnitY, 73f * UiMetrics.UnitX, 27f * UiMetrics.UnitY));

    /// <summary>A node of the open chart other than the cursor's that has a scene and is fully in view (for a click), nearest above the cursor first.</summary>
    private static (ChartNode Node, ChartRecord Record) OtherNodeInView(FlowchartScreen chart)
    {
        float cursorY = chart.Chart.Nodes[chart.Selected].Y;
        var candidates = new List<(ChartNode, ChartRecord, float)>();
        for (int i = 0; i < chart.Chart.Nodes.Count; i++)
        {
            ChartNode n = chart.Chart.Nodes[i];
            float y = n.Y - chart.Scroll;
            if (i == chart.Selected || y < 0f || y + FlowchartChart.NodeH > FlowchartChart.ViewHeight || chart.Chart.CellOf(i) is not { } cell
                || chart.Chart.RecordAt(cell) is not { ScriptScene: >= 0 } rec)
            {
                continue;
            }

            candidates.Add((n, rec, n.Y <= cursorY ? cursorY - n.Y : 100_000f + n.Y - cursorY));
        }

        (ChartNode node, ChartRecord record, _) = candidates.MinBy(c => c.Item3);
        return (node, record);
    }

    private static FlowchartScreen OpenInGameChart(Rig rig, string open, string tab)
    {
        rig.Play(open);
        Assert.True(rig.RunUntil(() => rig.Ui.Top is SystemMenuScreen, 60), "the system menu did not open: " + Screens(rig));

        // the menu's backdrop fades in from nothing every time (after a jump or a load it was left at full alpha, and the next menu showed it at once)
        Assert.InRange(rig.Ui.Context.Backdrop.A, 0f, 0.2f);
        rig.Run(30);
        rig.Play(tab);
        Assert.True(rig.RunUntil(() => rig.Ui.Top is FlowchartScreen, 120), "the chart did not open: " + Screens(rig));
        var chart = (FlowchartScreen)rig.Ui.Top!;
        Assert.False(chart.Gallery);
        rig.Run(40);
        Assert.Equal(1f, chart.Fade);
        Assert.Equal(rig.Services.CurrentNode, chart.Chart.RecordAt(chart.Cell)?.Node);
        return chart;
    }

    private void JumpedTo(Rig rig, ChartRecord target)
    {
        Assert.True(rig.RunUntil(() => !rig.Ui.Active, 150), "the screens did not leave after the jump: " + Screens(rig));
        Assert.True(rig.Services.StoryActive);
        Assert.Equal(GamePhase.Running, rig.Host.Phase);
        Assert.Equal(target.Node, rig.Services.CurrentNode);
        output.WriteLine($"frame {rig.Runtime.Frame}: jumped to node {target.Node} (scene {target.ScriptScene}), script {rig.Host.Script}");
        rig.Run(60);
    }

    /// <summary>
    /// Issue #7 (package K38), the way through the story's menu: Esc, FlowChart, an earlier node, "rewind to this scene?", Yes, the scene starts over; then Esc and
    /// FlowChart again. By keys, then by the mouse (a right click, the tab, a click on a node, a click on Yes) with the backlog in between, then once more after a
    /// save and a load. No step ends with no story and no screen, the chart reopens on the scene that runs, and every menu fades its backdrop in from nothing.
    /// </summary>
    [GameDataFact]
    public void JumpingFromTheInGameChartThenOpeningItAgainWorksByKeysByTheMouseAndAfterALoad()
    {
        Rig rig = ProfileWithProgress();
        const int Columns = 14;
        FinishScenes(rig, Columns);
        rig.Run(5);
        rig.Ui.Clear();

        // the playthrough is in the last scene of the first columns (as a save from there)
        PlayedThrough(rig, Columns);
        FlowNodeRow start = Source.Eboot.GameFlow.Nodes.Last(r => r.Column < Columns && r.Scene > 0 && r.ScriptScene is >= 0 and < 2000);
        Assert.True(rig.Services.FlowchartJump(start.Column, start.Scene, start.ScriptScene));
        rig.Run(60);
        rig.NoDeadFrames = true;

        // keys: Esc, FlowChart (the fourth tab), Up to an earlier scene, Enter, Yes (first), Enter
        FlowchartScreen chart = OpenInGameChart(rig, "3:Esc", "2:Down;14:Down;26:Down;38:Decide*2");
        Assert.Equal(start.Scene, chart.Chart.RecordAt(chart.Cell)?.Node);
        ChartRecord target = default;
        for (int i = 0; i < 6 && (target.Node <= 0 || target.Node == start.Scene || target.ScriptScene < 0); i++)
        {
            rig.Play("2:Up");
            rig.Run(30);
            target = chart.Chart.RecordAt(chart.Cell) ?? default;
        }

        Assert.True(target.ScriptScene >= 0 && target.Node != start.Scene, "no earlier scene above the cursor");
        rig.Play("2:Decide*2");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is ConfirmDialog, 60));
        rig.Run(25);
        Assert.True(((ConfirmDialog)rig.Ui.Top!).CursorOnYes);
        rig.Play("2:Decide*2");
        JumpedTo(rig, target);

        // Esc and FlowChart again (the menu kept the tab): the chart is on the scene that runs now
        chart = OpenInGameChart(rig, "3:Esc", "2:Decide*2");

        // the mouse, with the backlog first: the chart and the menu close, the wheel opens the backlog of a few pages and Esc closes it, a right click opens the menu,
        // a click on the FlowChart tab the chart, a click on a node the question, a click on Yes jumps
        rig.Play("2:Esc;60:Esc");
        Assert.True(rig.RunUntil(() => !rig.Ui.Active, 150), "the chart and the menu did not close: " + Screens(rig));
        rig.Auto.Paused = false;
        rig.Run(150);
        rig.Auto.Paused = true;
        rig.Run(10);
        rig.Play("2:wheel=1");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is BacklogScreen, 60), "the backlog did not open: " + Screens(rig));
        rig.Run(40);
        rig.Play("2:Esc");
        Assert.True(rig.RunUntil(() => !rig.Ui.Active, 90), "the backlog did not close: " + Screens(rig));
        chart = OpenInGameChart(rig, "3:rclick=960,540", "2:click=" + Centre(SystemMenuScreen.TabRect((int)MenuTab.FlowChart)));
        (ChartNode node, target) = OtherNodeInView(chart);
        rig.Play($"2:click={(int)(node.X + (FlowchartChart.NodeW / 2f))},{(int)(node.Y - chart.Scroll + (FlowchartChart.NodeH / 2f))}");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is ConfirmDialog, 60), "a click on a node did not ask: " + Screens(rig));
        rig.Run(25);
        rig.Play("2:click=" + DialogYes);
        JumpedTo(rig, target);
        chart = OpenInGameChart(rig, "3:rclick=960,540", "2:click=" + Centre(SystemMenuScreen.TabRect((int)MenuTab.FlowChart)));

        // a save and a load in between: Esc to the menu, Save (three tabs up), the free slot; back, Load, the newest slot, Yes; then Esc and FlowChart again
        int jumps = rig.Host.RestoreCount;
        rig.Play("2:Esc");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is SystemMenuScreen, 90));
        rig.Run(20);
        rig.Play("2:Up;14:Up;26:Up;38:Decide*2");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is SaveLoadScreen, 90), "the save list did not open: " + Screens(rig));
        rig.Run(40);
        rig.Play("2:Decide*2");
        Assert.True(rig.RunUntil(() => rig.Services.Slots.Any(s => s.Used && s.Index < 100), 300), "the save list did not store a slot");
        rig.Run(120);
        rig.Play("2:Esc");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is SystemMenuScreen, 120), "the save list did not close: " + Screens(rig));
        rig.Run(20);
        rig.Play("2:Down;14:Decide*2");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is SaveLoadScreen, 90), "the load list did not open: " + Screens(rig));
        rig.Run(40);
        rig.Play("2:Decide*2");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is ConfirmDialog, 90), "the load did not ask: " + Screens(rig));
        rig.Run(25);
        rig.Play("2:Decide*2");
        Assert.True(rig.RunUntil(() => !rig.Ui.Active && rig.Host.RestoreCount > jumps, 600), "the load did not run: " + Screens(rig));
        rig.Run(60);
        Assert.Equal((int)MenuTab.Load, rig.Ui.Context.MenuTab);                // the menu keeps the tab it was left on: FlowChart is two down
        chart = OpenInGameChart(rig, "3:Esc", "2:Down;14:Down;26:Decide*2");
        rig.Play("2:Esc;60:Esc");
        Assert.True(rig.RunUntil(() => !rig.Ui.Active, 150));
        Assert.True(rig.Services.StoryActive);
    }

    private static FlowchartScreen OpenGalleryChart(Rig rig)
    {
        rig.Ui.Clear();
        GalleryScreen gallery = rig.Ui.Push(new GalleryScreen());
        rig.Run(40);
        Assert.Same(gallery, rig.Ui.Top);

        // the hub's third button is the Flowchart; the chart opens on its first record
        rig.Play("2:Down;14:Down;26:Decide*2");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is FlowchartScreen, 120), "the chart did not open from the gallery");
        var chart = (FlowchartScreen)rig.Ui.Top!;
        rig.Run(40);
        return chart;
    }

    private void ReplayRuns(Rig rig, int node)
    {
        Assert.True(rig.RunUntil(() => rig.Services.ReplayActive && rig.Host.Phase == GamePhase.Running && !rig.Ui.Active, 300), "the replay did not start: " + Screens(rig));
        rig.Run(120);
        output.WriteLine($"frame {rig.Runtime.Frame}: replay of node {node}: scene {rig.Host.SceneId}, script {rig.Host.Script}");
    }

    private FlowchartScreen BackOnTheGalleryChart(Rig rig, int node, string yes)
    {
        var dialog = Assert.IsType<ConfirmDialog>(rig.Ui.Top);
        rig.Run(25);
        Assert.True(dialog.CursorOnYes);
        long asked = rig.Runtime.Frame;
        rig.Play(yes);

        // the menu leaves by itself (no key) and the gallery reopens on the chart, the cursor on the node the replay started from
        Assert.True(rig.RunUntil(() => rig.Ui.Top is FlowchartScreen { Gallery: true }, 400), "not back on the gallery's chart: " + Screens(rig) + $", story {rig.Services.StoryActive}");
        output.WriteLine($"back on the gallery's chart {rig.Runtime.Frame - asked} frames after Yes");
        var back = (FlowchartScreen)rig.Ui.Top!;
        Assert.False(rig.Services.StoryActive);
        Assert.False(rig.Services.ReplayActive);
        Assert.Null(rig.Ui.Find<SystemMenuScreen>());
        Assert.IsType<TitleScreen>(rig.Ui.Stack[0]);
        Assert.NotNull(rig.Ui.Find<GalleryScreen>());
        Assert.Equal(node, back.Chart.RecordAt(back.Cell)?.Node);
        rig.Run(60);
        Assert.Equal(1f, back.Fade);
        return back;
    }

    /// <summary>
    /// Issue #7 (package K38), the player's route: GALLERY, Flowchart, a node (the scene replays), Esc, FlowChart again, "Do you want to return to the flowchart?",
    /// Yes. The menu leaves by itself, the gallery comes back on the chart with the cursor on the node the replay started from, and no step on the way ends with no
    /// story and no screen (the black screen that took no key). Then the same node again, by the mouse (a right click, the tab, a click on Yes) with the backlog in
    /// between, and the chart still takes its keys afterwards.
    /// </summary>
    [GameDataFact]
    public void EndingAChartReplayFromTheSystemMenuReturnsToTheGalleryChartTwiceInARow()
    {
        Rig rig = ProfileWithProgress();
        FinishScenes(rig, 10);
        rig.Run(5);
        rig.NoDeadFrames = true;
        FlowchartScreen chart = OpenGalleryChart(rig);
        ChartRecord rec = chart.Chart.RecordAt(chart.Cell) ?? throw new InvalidOperationException("the chart has no record under its cursor");
        Assert.True(rec.ScriptScene >= 0, "the first record has no scene to replay");
        rig.Play("2:Decide*2");
        ReplayRuns(rig, rec.Node);

        // keys: Esc, FlowChart (the fourth tab), the dialog with Yes first, Enter
        rig.Play("3:Esc");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is SystemMenuScreen, 30));
        rig.Run(30);
        rig.Play("2:Down;14:Down;26:Down;38:Decide*2");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is ConfirmDialog, 120), "the end-replay dialog did not open: " + Screens(rig));
        FlowchartScreen back = BackOnTheGalleryChart(rig, rec.Node, "2:Decide*2");

        // again from the chart that came back: the same node, the backlog of a few pages, then the mouse
        rig.Play("2:Decide*2");
        ReplayRuns(rig, rec.Node);
        rig.Auto.Paused = false;
        rig.Run(150);
        rig.Auto.Paused = true;
        rig.Run(10);
        rig.Play("2:wheel=1");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is BacklogScreen, 60), "the backlog did not open: " + Screens(rig));
        rig.Run(40);
        rig.Play("2:Esc");
        Assert.True(rig.RunUntil(() => !rig.Ui.Active, 90));
        Assert.True(rig.Services.ReplayActive);
        rig.Play("3:rclick=960,540");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is SystemMenuScreen, 30));
        rig.Run(30);
        rig.Play("2:click=" + Centre(SystemMenuScreen.TabRect((int)MenuTab.FlowChart)));
        Assert.True(rig.RunUntil(() => rig.Ui.Top is ConfirmDialog, 120), "the end-replay dialog did not open on a click: " + Screens(rig));
        back = BackOnTheGalleryChart(rig, rec.Node, "2:click=" + DialogYes);

        // the chart still works: Esc goes back to the hub, which takes input
        rig.Play("2:Esc");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is GalleryScreen, 120));
    }

    /// <summary>
    /// Issue #7 (package K38) over the whole chart, every fifth node: in the story, a jump to the node from the chart, then Esc and FlowChart again (the chart opens
    /// on the node, or says it cannot be used where the PS4 says so), every fifth of those once more by Yes on the cursor; in the gallery, a replay of the node, then
    /// Esc, FlowChart, Yes back to the chart on the same node. No exception, no step with no story and no screen.
    /// </summary>
    [GameDataFact]
    public void EveryFifthNodeJumpsAndReplaysAndComesBackToItsChart()
    {
        Rig rig = ProfileWithProgress();
        FinishScenes(rig);
        rig.Run(5);
        rig.Ui.Clear();
        rig.NoDeadFrames = true;
        var problems = new List<string>();

        // in the story
        List<FlowNodeRow> nodes = [.. Source.Eboot.GameFlow.Nodes.Where(r => r.ScriptScene >= 0 && r.Scene > 0)];
        int jumps = 0;
        for (int i = 0; i < nodes.Count; i += 5)
        {
            FlowNodeRow r = nodes[i];
            string at = $"jump to node {r.Scene} (column {r.Column}, scene {r.ScriptScene})";
            rig.NoDeadFrames = false;
            rig.Ui.Clear();
            PlayedThrough(rig, GalleryTables.ChartColumns.Count);
            Assert.True(rig.Services.FlowchartJump(r.Column, r.Scene, r.ScriptScene), at + ": refused");
            rig.NoDeadFrames = true;
            rig.Run(30);
            rig.Ui.Context.MenuTab = (int)MenuTab.FlowChart;
            rig.Play("1:Esc");
            Assert.True(rig.RunUntil(() => rig.Ui.Top is SystemMenuScreen, 90), at + ": no system menu: " + Screens(rig));
            rig.Run(20);
            rig.Play("1:Decide*2");
            Assert.True(rig.RunUntil(() => rig.Ui.Top is FlowchartScreen or MessagePopup, 60), at + ": FlowChart did nothing: " + Screens(rig));
            if (rig.Ui.Top is MessagePopup)
            {
                // the prologue's nodes: "You cannot use the flowchart yet." (scene 1 is not entered in this playthrough)
                problems.Add(at + $": the chart is not available here (F110 {rig.Services.CurrentNode})");
                continue;
            }

            var chart = (FlowchartScreen)rig.Ui.Top!;
            rig.Run(35);
            if (chart.Chart.RecordAt(chart.Cell)?.Node != rig.Services.CurrentNode)
            {
                // the eight scenes that start inside a script (2000..2007) set F110 to a node of another column: the cursor stays on the first record (sub_F8FC)
                problems.Add(at + $": the chart opened on node {chart.Chart.RecordAt(chart.Cell)?.Node}, F110 is {rig.Services.CurrentNode}");
            }

            if (i % 25 == 0 && chart.Chart.RecordAt(chart.Cell) is { ScriptScene: >= 0 } again)
            {
                rig.Play("1:Decide*2");
                Assert.True(rig.RunUntil(() => rig.Ui.Top is ConfirmDialog, 30), at + ": no rewind question");
                rig.Run(20);
                rig.Play("1:Decide*2");
                Assert.True(rig.RunUntil(() => !rig.Ui.Active, 120), at + ": the second jump left " + Screens(rig));
                Assert.Equal(again.Node, rig.Services.CurrentNode);
                rig.Run(30);
            }
            else
            {
                rig.Play("1:Esc;40:Esc");
                rig.Run(100);
            }

            Assert.True(rig.Services.StoryActive, at + ": the story is gone");
            jumps++;
        }

        // in the gallery
        rig.NoDeadFrames = false;
        List<ChartNodeRow> rows = [.. GalleryTables.ChartColumns.SelectMany(c => c.Nodes).Where(r => r.ScriptScene >= 0 && r.Scene > 0).DistinctBy(r => r.Scene)];
        int replays = 0;
        for (int i = 0; i < rows.Count; i += 5)
        {
            ChartNodeRow r = rows[i];
            string at = $"replay of node {r.Scene} (scene {r.ScriptScene})";
            rig.Ui.Clear();
            rig.Ui.Context.Gallery.Mode = (int)GalleryMode.Flowchart;              // as GalleryScreen.ReplayFromChart leaves it
            rig.Ui.Context.Gallery.Node = r.Scene;
            rig.Services.ReplayScene(r.ScriptScene, r.Scene);
            Assert.True(rig.RunUntil(() => rig.Services.ReplayActive && rig.Host.Phase == GamePhase.Running && !rig.Ui.Active, 300), at + ": did not start");
            rig.NoDeadFrames = true;
            rig.Run(40);
            rig.Ui.Context.MenuTab = (int)MenuTab.FlowChart;
            rig.Play("1:Esc");
            Assert.True(rig.RunUntil(() => rig.Ui.Top is SystemMenuScreen, 90), at + ": no system menu: " + Screens(rig));
            rig.Run(20);
            rig.Play("1:Decide*2");
            Assert.True(rig.RunUntil(() => rig.Ui.Top is ConfirmDialog, 60), at + ": no end-replay question: " + Screens(rig));
            rig.Run(20);
            rig.Play("1:Decide*2");
            Assert.True(rig.RunUntil(() => rig.Ui.Top is FlowchartScreen { Gallery: true }, 300), at + ": not back on the chart: " + Screens(rig));
            var back = (FlowchartScreen)rig.Ui.Top!;
            Assert.True(back.Chart.RecordAt(back.Cell)?.Node == r.Scene, at + $": back on node {back.Chart.RecordAt(back.Cell)?.Node}");
            rig.NoDeadFrames = false;
            replays++;
        }

        foreach (string p in problems)
        {
            output.WriteLine(p);
        }

        output.WriteLine($"{jumps} jumps, {replays} replays");
        Assert.True(jumps >= 90 && replays >= 90);
        Assert.All(problems, p => Assert.True(p.Contains("not available", StringComparison.Ordinal) || p.Contains("scene 20", StringComparison.Ordinal), p));
    }

    // ---- package K39 (review of K38): the replay flag after a replay --------------------------------------------------------------------------------------------

    /// <summary>
    /// A chart replay left by the system menu's Title tab, then the title's CONTINUE / LOAD GAME: the loaded story is not a replay, so Esc and FlowChart open the
    /// in-game chart. The host's replay flag, which a rewind keeps, survived the load: the story asked "Do you want to return to the flowchart?" (the gallery's mode
    /// was still the chart), and Yes threw it away into the gallery; in v0.1.2 onto the black screen of issue #7.
    /// </summary>
    [GameDataFact]
    public void AStoryLoadedAfterAChartReplayLeftByTheTitleTabOpensTheInGameChart()
    {
        Rig rig = ProfileWithProgress();
        const int Columns = 14;
        FinishScenes(rig, Columns);
        rig.Run(5);
        rig.Ui.Clear();

        // a save in the story (the chart is available there), then back to the title
        PlayedThrough(rig, Columns);
        FlowNodeRow start = Source.Eboot.GameFlow.Nodes.Last(r => r.Column < Columns && r.Scene > 0 && r.ScriptScene is >= 0 and < 2000);
        Assert.True(rig.Services.FlowchartJump(start.Column, start.Scene, start.ScriptScene));
        Assert.True(rig.RunUntil(() => rig.Host.History is { Staging.Used: not 0 }, 900), "no page to save");
        Assert.True(rig.Services.Save(0));
        rig.Services.ReturnToTitle();
        Assert.True(rig.RunUntil(() => rig.Ui.Top is TitleScreen, 60));

        // GALLERY, Flowchart, the first node replays; Esc, the Title tab (four down from Save)
        rig.NoDeadFrames = true;
        FlowchartScreen chart = OpenGalleryChart(rig);
        ChartRecord rec = chart.Chart.RecordAt(chart.Cell) ?? throw new InvalidOperationException("the chart has no record under its cursor");
        rig.Play("2:Decide*2");
        ReplayRuns(rig, rec.Node);
        rig.Play("3:Esc");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is SystemMenuScreen, 30));
        rig.Run(30);
        rig.Ui.Context.MenuTab = (int)MenuTab.Save;
        rig.Play("2:Up;14:Decide*2");
        Assert.True(rig.RunUntil(() => rig.Ui.Find<SystemMenuScreen>() is null && rig.Ui.Find<TitleScreen>() is not null, 300), "not on the title: " + Screens(rig));
        Assert.False(rig.Services.StoryActive);
        Assert.Equal((int)GalleryMode.Flowchart, rig.Ui.Context.Gallery.Mode);

        // the title's CONTINUE: the saved story, which is no replay
        Assert.True(rig.Services.Load(0));
        Assert.True(rig.RunUntil(() => !rig.Ui.Active, 300), "the story did not take the screen: " + Screens(rig));
        Assert.False(rig.Services.ReplayActive);
        Assert.False(rig.Host.ReplayRequested);
        rig.Run(60);

        // Esc, FlowChart (one up from Title): the in-game chart on the scene that runs, not the end of a replay
        FlowchartScreen inGame = OpenInGameChart(rig, "3:Esc", "2:Up;14:Decide*2");
        Assert.Equal(start.Scene, inGame.Chart.RecordAt(inGame.Cell)?.Node);
        rig.Play("2:Esc;60:Esc");
        Assert.True(rig.RunUntil(() => !rig.Ui.Active, 150));
        Assert.True(rig.Services.StoryActive);
    }

    /// <summary>
    /// A language change during a chart replay builds a new host and carries the page into it; the replay must still end back on the gallery's chart. The new host
    /// had no replay flag, and the replay ended on the title.
    /// </summary>
    [GameDataFact]
    public void AChartReplayEndsBackOnTheGalleryChartAfterALanguageChange()
    {
        Rig rig = ProfileWithProgress();
        FinishScenes(rig, 10);
        rig.Run(5);
        rig.Auto.HoldChoice = false;
        rig.NoDeadFrames = true;
        FlowchartScreen chart = OpenGalleryChart(rig);
        ChartRecord rec = chart.Chart.RecordAt(chart.Cell) ?? throw new InvalidOperationException("the chart has no record under its cursor");
        rig.Play("2:Decide*2");
        ReplayRuns(rig, rec.Node);

        // a page is on show, so the new host gets it through a slot (a replay with no page yet starts its ARCHIVE scene over instead, AppServices.RebuildHost)
        Assert.True(rig.RunUntil(() => rig.Host.History is { Staging.Used: not 0 } && rig.Host.CurrentMessage is not null, 600), "no page in the replay");
        GameHost before = rig.Host;
        rig.Services.Config.Language = (short)Language.Ja;
        rig.Services.ConfigChanged(ConfigChange.Language);
        Assert.NotSame(before, rig.Host);
        Assert.True(rig.Services.ReplayActive);

        // the autoplay skips to the end of the scene
        rig.Auto.Paused = false;
        Assert.True(rig.RunUntil(() => !rig.Services.StoryActive, 12000), "the replay did not end");
        Assert.True(rig.RunUntil(() => rig.Ui.Top is FlowchartScreen { Gallery: true }, 120), "not back on the gallery's chart: " + Screens(rig));
        var back = (FlowchartScreen)rig.Ui.Top!;
        Assert.Equal(rec.Node, back.Chart.RecordAt(back.Cell)?.Node);
        Assert.Equal(Language.Ja, rig.Services.Language);
        output.WriteLine($"frame {rig.Runtime.Frame}: the replay of node {rec.Node} ended back on the gallery's chart");
    }
}
