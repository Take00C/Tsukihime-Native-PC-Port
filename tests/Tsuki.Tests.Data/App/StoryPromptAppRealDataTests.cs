using Tsuki.App.Play;
using Tsuki.App.Services;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Data.App;

/// <summary>The chapter-start save prompt through the app runtime on the user's data (package K13): the first retail <c>_SELA</c>, scene 331 (<c>02_00_ARC02_1</c>).</summary>
public sealed class StoryPromptAppRealDataTests(ITestOutputHelper output)
{
    private static GameFolderSource Source => SharedSource.Source;

    private const int SelaScene = 331;

    private sealed class Rig
    {
        private readonly DisplayList _list = new(4096);
        private long _n;

        public Rig(Language language)
        {
            var store = new SaveStore();
            store.SystemData.Block.Magic = SystemBlock.MagicValue;
            store.SystemData.Config.CopyFrom(ConfigBlock.CreateBootDefaults());
            store.SystemData.Config.Language = -1;
            var scripts = new ScriptCache(Source);
            Services = new AppServices(new AppServicesParts
            {
                Data = Source,
                Store = store,
                InitialLanguage = language,
                HostFactory = (lang, profile) => new GameHost(
                    new GameServices(Source, scripts)
                    {
                        SeLookup = (name, flags) => Source.SoundEffectNames.Lookup(name, flags)?.Index,
                        Layouts = new GamePictureLayouts(Source),
                    },
                    new GameOptions { Language = lang, VirtualMovies = true, Sound = false },
                    profile),
            });
            Ui = new UiRoot(Source, Services, new DataParts(Source));
            Runtime = new AppRuntime(Services, Ui, null, null);
            Services.StartScene(SelaScene);
        }

        public AppServices Services { get; }

        public UiRoot Ui { get; }

        public AppRuntime Runtime { get; }

        public GameHost Host => Services.Host;

        public DisplayList Last => _list;

        public void Step(InputFrame input) => Runtime.Step(input with { Frame = ++_n }, _n * 16_666_667L, _list);

        public void Run(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                Step(new InputFrame { Pressed = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None });
            }
        }

        public void Press(PadButtons b)
        {
            Step(new InputFrame { Held = b, Pressed = b, Repeated = b });
            Step(new InputFrame());
        }

        public int Tiles(int child) =>
            _list.Commands.ToArray().Count(c => c.Texture.Kind == TextureKind.UiTile && c.Texture.A == (int)UiSet.DispParts && c.Texture.C == child);

        /// <summary>Plays (clicking, skip latched) until the dialog is selectable.</summary>
        public void ToPrompt()
        {
            for (int i = 0; i < 4000 && !(Host.PromptOpen && Host.ChoiceSelectable); i++)
            {
                Host.SetSkipLatch(true);
                Step(new InputFrame { Pressed = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None, Held = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None });
            }

            Assert.True(Host.PromptOpen && Host.ChoiceSelectable, "the save prompt never opened");
        }
    }

    [GameDataTheory]
    [InlineData(Language.Ja)]
    [InlineData(Language.En)]
    [InlineData(Language.Zc)]
    [InlineData(Language.Zt)]
    public void TheDialogIsDrawnOverTheStoryInEveryLanguageWithItsMessageAndButtons(Language language)
    {
        var rig = new Rig(language);
        rig.ToPrompt();
        rig.Step(new InputFrame());
        Assert.InRange(rig.Tiles(20), 1, 40);                               // the real sheets are cut into tiles: the window, the backdrop, two buttons
        Assert.InRange(rig.Tiles(21), 1, 40);
        if (UiDialogButtons.SheetIsLocalized(language))
        {
            Assert.InRange(rig.Tiles(19), 2, 40);
        }
        else
        {
            // package K20: the Japanese sheet says Yes / No in English, so the buttons are はい / いいえ in glyphs; nothing is selected yet, so no underline from the sheet either
            Assert.Equal(0, rig.Tiles(19));
        }

        UiStoryPrompt prompt = rig.Services.StoryPrompt!.Value;
        Assert.Equal(216, prompt.MessageId);
        Assert.False(string.IsNullOrWhiteSpace(rig.Ui.Context.Message(prompt.MessageId)));
        Assert.Contains(rig.Last.Commands.ToArray(), c => c.Texture.Kind == TextureKind.GlyphPage);        // the message
        Assert.False(rig.Ui.Active);
    }

    [GameDataFact]
    public void YesOpensTheSaveScreenOverTheStoryAndTheScriptGoesOnAfterItClosesAndNoneOpensForNo()
    {
        var rig = new Rig(Language.En);
        rig.ToPrompt();
        int offset = rig.Host.Machine.Cursor;
        rig.Press(PadButtons.Down);
        Assert.Equal(1, rig.Host.ChoiceCursor);
        rig.Press(PadButtons.Decide);
        rig.Run(0);
        for (int i = 0; i < 30 && !rig.Ui.Active; i++)
        {
            rig.Step(new InputFrame());
        }

        var save = Assert.IsType<SaveLoadScreen>(rig.Ui.Top);
        Assert.True(save.Save);
        Assert.True(rig.Host.ChoiceOpen);
        for (int i = 0; i < 120; i++)
        {
            rig.Step(new InputFrame());
        }

        Assert.True(rig.Host.ChoiceOpen);                                  // the story waits while the screen is open
        rig.Press(PadButtons.Cancel);
        for (int i = 0; i < 80; i++)
        {
            rig.Step(new InputFrame());
        }

        Assert.False(rig.Ui.Active);
        Assert.False(rig.Host.ChoiceOpen);
        for (int i = 0; i < 400 && rig.Host.Machine.Cursor == offset; i++)
        {
            rig.Step(new InputFrame { Pressed = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None });
        }

        Assert.NotEqual(offset, rig.Host.Machine.Cursor);
        output.WriteLine($"Yes: save screen opened and closed, the script went on from {offset} to {rig.Host.Machine.Cursor}");

        var no = new Rig(Language.En);
        no.ToPrompt();
        no.Press(PadButtons.Right);
        no.Press(PadButtons.Decide);
        for (int i = 0; i < 60; i++)
        {
            no.Step(new InputFrame());
            Assert.False(no.Ui.Active);
        }

        Assert.False(no.Host.ChoiceOpen);
    }

    [GameDataFact]
    public void ALoadedGameDoesNotAskAgainBecauseEverySaveCarriesTheMarker()
    {
        // answer Yes, save in the first slot, load it into a fresh app: the chapter start runs again with F1118 = 1, so no prompt opens
        var rig = new Rig(Language.En);
        rig.ToPrompt();
        rig.Press(PadButtons.Down);
        rig.Press(PadButtons.Decide);
        for (int i = 0; i < 30 && !rig.Ui.Active; i++)
        {
            rig.Step(new InputFrame());
        }

        Assert.IsType<SaveLoadScreen>(rig.Ui.Top);
        rig.Press(PadButtons.Decide);                                      // the free slot
        for (int i = 0; i < 200; i++)
        {
            rig.Step(new InputFrame());
        }

        Assert.Contains(rig.Services.Slots, s => s.Used && s.Index < 100);
        int slot = rig.Services.Slots.First(s => s.Used && s.Index < 100).Index;
        Assert.True(rig.Services.Load(slot));
        Assert.Equal(1, rig.Host.F(1118));
        for (int i = 0; i < 1500; i++)
        {
            Assert.False(rig.Host.PromptOpen, "the prompt opened again after a load");
            rig.Host.SetSkipLatch(true);
            rig.Step(new InputFrame { Pressed = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None });
        }
    }
}
