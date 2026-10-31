using System.Text;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;
using Tsuki.Media.Sound;
using Tsuki.Tests.Synthetic.Game;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// The app services over the hand-made game of <see cref="GameRig"/>: three pages in scene 100 (a layer and a track change between them), no data, no window.
/// </summary>
internal sealed class AppRig
{
    /// <summary>Page 1, then music (track 5) and a layer on page 2, then page 3, then the end.</summary>
    public const string ThreePages =
        "_WKST(F822,eq,1);_ATSV();" +
        "_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);" +
        "_MPLY(5);_STCH(1,IMG0001,240,136);_FADS(1,0,0);_WKST(F823,eq,0);_WNTY(1);_ZM00102($000003);" +
        "_WKST(F823,eq,0);_WNTY(1);_ZM00103($000004);" +
        "_END_();";

    private long _inputFrame;

    public AppRig(string script = ThreePages, string? savePath = null, SaveStore? store = null, AudioCommandPlayer? audio = null)
    {
        Rig = new GameRig().Line(2, "alpha line").Line(3, "beta line").Line(4, "gamma line").Line(5, "delta line").Line(6, "epsilon line")
            .Line(43960, "1. ").Line(43961, "2. ").Line(43962, "3. ").Script(script);
        Rig.Build();
        Store = store ?? NewStore();
        Services = new AppServices(new AppServicesParts
        {
            Data = Rig.Data,
            Store = Store,
            SavePath = savePath,
            Audio = audio,
            InitialLanguage = Language.En,
            HostFactory = (language, profile) => Rig.Build(new GameOptions { Language = language, RecordUnknownCommands = true }, profile),
        });
    }

    public GameRig Rig { get; }

    public SaveStore Store { get; }

    public AppServices Services { get; }

    public GameHost Host => Services.Host;

    public static SaveStore NewStore()
    {
        var store = new SaveStore();
        store.SystemData.Block.Magic = SystemBlock.MagicValue;
        store.SystemData.Config.CopyFrom(ConfigBlock.CreateBootDefaults());
        store.SystemData.Config.Language = -1;
        return store;
    }

    /// <summary>The text of the message on show, or an empty string.</summary>
    public string Text => Host.CurrentMessage is { } m ? Encoding.UTF8.GetString(m.Text) : string.Empty;

    /// <summary>Clicks through the story until the message <paramref name="text"/> is on show, then lets it finish revealing (it waits for its click).</summary>
    public void RunTo(string text, int limit = 4000)
    {
        for (int i = 0; i < limit; i++)
        {
            if (Text == text)
            {
                Idle(120);
                return;
            }

            Step(i % 3 == 0);
        }

        throw new InvalidOperationException($"message '{text}' never came (on show: '{Text}')");
    }

    /// <summary>Steps the story without clicking until the message <paramref name="text"/> is on show (a click would run the script past it).</summary>
    public void WaitFor(string text, int limit = 400)
    {
        for (int i = 0; i < limit; i++)
        {
            if (Text == text)
            {
                return;
            }

            Step(false);
        }

        throw new InvalidOperationException($"message '{text}' never came (on show: '{Text}')");
    }

    public void Step(bool click)
    {
        _inputFrame++;
        Services.Tick();
        var input = new InputFrame
        {
            Frame = _inputFrame,
            Held = click ? PadButtons.Decide : PadButtons.None,
            Pressed = click ? PadButtons.Decide : PadButtons.None,
            Repeated = click ? PadButtons.Decide : PadButtons.None,
        };
        Host.Step(input);
    }

    /// <summary>Runs <paramref name="frames"/> plain frames.</summary>
    public void Idle(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            Step(false);
        }
    }

    /// <summary>Starts the story and shows its first page.</summary>
    public void Begin()
    {
        Services.StartScene(100);
        WaitFor("alpha line");
    }
}
