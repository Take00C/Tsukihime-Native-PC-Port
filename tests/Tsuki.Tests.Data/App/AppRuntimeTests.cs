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

        public void Step()
        {
            _nominal += 16_666_667;
            InputFrame input = Input.Sample(Runtime.Frame + 1);
            Runtime.Step(input, _nominal, _list);
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
}
