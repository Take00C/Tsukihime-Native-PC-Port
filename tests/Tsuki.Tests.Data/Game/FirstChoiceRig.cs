using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Tests.Data.Source;

namespace Tsuki.Tests.Data.Game;

/// <summary>A host on the user's data, played in skip mode through the prologue and START_SCRIPT to the first story choice (script 4, 01_00_ARC01_1).</summary>
internal static class FirstChoiceRig
{
    /// <summary>Script number of 01_00_ARC01_1.</summary>
    public const int Script = 4;

    private static GameFolderSource Source => SharedSource.Source;

    public static GameHost NewHost(Language language)
    {
        var services = new GameServices(Source, new ScriptCache(Source)) { SeLookup = (n, f) => Source.SoundEffectNames.Lookup(n, f)?.Index, Layouts = new GamePictureLayouts(Source) };
        return new GameHost(services, new GameOptions { Language = language, Sound = false, VirtualMovies = true, RandomSeed = 7 });
    }

    private sealed class ScriptCache(GameFolderSource source) : Tsuki.Engine.Flow.IFlowScripts
    {
        public ScriptImage Script(int k) => source.Script(k);

        public Tsuki.Engine.Flow.LabelTable Labels(int k) => source.Scripts.LabelLookup(k);
    }

    /// <summary>New Game from the title, twice: the prologue (00_01), then START_SCRIPT, which plays on to the first choice (script 4).</summary>
    public static void RunToChoice(GameHost host)
    {
        host.NewGame(host.NewGameSceneId());
        for (int n = 0, games = 1; n < 400_000 && !host.ChoiceSelectable; n++)
        {
            if (host.Phase != GamePhase.Running && games < 4)
            {
                host.NewGame(host.NewGameSceneId());
                games++;
            }

            host.SetSkipLatch(true);
            host.Step(new InputFrame { Pressed = (n & 1) == 0 ? PadButtons.Decide : PadButtons.None });
        }

        Assert.True(host.ChoiceSelectable, "the first choice was not reached");
        Assert.Equal(Script, host.Script);
        host.SetSkipLatch(false);
    }

    public static DisplayList Frame(GameHost host, ref long n)
    {
        var list = new DisplayList(8192);
        list.Reset(++n);
        host.ChoiceTrace = [];
        host.Step(new InputFrame { Frame = n }, list);
        return list;
    }

    public static void Frames(GameHost host, ref long n, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Frame(host, ref n);
        }
    }
}
