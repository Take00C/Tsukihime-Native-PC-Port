using System.Text;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Tests.Data.Source;

namespace Tsuki.Tests.Data.Game;

/// <summary>The story's two-button prompts on the retail scripts (package K13): the 26 <c>_SELA</c> chapter starts and the <c>_SELS</c> of the QACALL menu.</summary>
public sealed class StoryPromptRealDataTests(ITestOutputHelper output)
{
    private static GameFolderSource Source => SharedSource.Source;

    private static List<int> ScriptsWith(string command)
    {
        byte[] needle = Encoding.ASCII.GetBytes(command);
        var list = new List<int>();
        for (int k = 0; k < Source.ScriptCount; k++)
        {
            if (Source.Script(k).Bytes.Span.IndexOf(needle) >= 0)
            {
                list.Add(k);
            }
        }

        return list;
    }

    private static GameHost NewHost(Language language = Language.En)
    {
        var services = new GameServices(Source, new ScriptCacheForTests(Source)) { SeLookup = (n, f) => Source.SoundEffectNames.Lookup(n, f)?.Index };
        return new GameHost(services, new GameOptions { Language = language, VirtualMovies = true, Sound = false });
    }

    /// <summary>Clicks through the story (skip latched) until the prompt is selectable; returns the frames it took or -1.</summary>
    private static int PlayToPrompt(GameHost host, int limit = 6000)
    {
        for (int i = 0; i < limit && host.Phase == GamePhase.Running; i++)
        {
            if (host.PromptOpen && host.ChoiceSelectable)
            {
                return i;
            }

            host.SetSkipLatch(true);
            host.Step(new InputFrame { Frame = i, Pressed = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None, Held = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None });
        }

        return -1;
    }

    /// <summary>Answers the open prompt like a player and, when it asks for the save screen, closes that screen at once; plays on until the choice is gone.</summary>
    private static void Answer(GameHost host, int button, out int asked)
    {
        int count = 0;
        host.SavePromptAccepted += () => count++;
        Assert.True(host.Choose(button));
        for (int i = 0; i < 200 && host.ChoiceOpen; i++)
        {
            if (host.ChoicePhase == 6)
            {
                host.SavePromptScreenClosed();
            }

            host.Step(default);
        }

        asked = count;
    }

    [GameDataFact]
    public void EveryRetailSelaOpensItsSavePromptAndTheScriptGoesOnAfterNoAndAfterYes()
    {
        List<int> scripts = ScriptsWith("_SELA(");
        Assert.Equal(26, scripts.Count);
        foreach (int k in scripts)
        {
            int scene = Source.Eboot.GameFlow.SceneId(k);
            foreach (int button in new[] { 0, 1 })
            {
                GameHost host = NewHost();
                host.NewGame(scene);
                int frames = PlayToPrompt(host);
                Assert.True(frames >= 0, $"script {k} ({Source.Scripts.Names[k]}): the save prompt never opened");
                Assert.Equal(ChoiceKind.SavePrompt, host.ChoiceKind);
                Assert.Equal(216, host.PromptMessageId);
                Assert.Equal(0, host.F(1118));                             // the SELA cleared its gate
                Assert.Equal(-1, host.ChoiceCursor);
                int at = host.Machine.Cursor;

                Answer(host, button, out int asked);
                Assert.False(host.ChoiceOpen);
                Assert.Equal(button, asked);                               // only Yes asks for the save screen, exactly once
                for (int i = 0; i < 600 && host.Machine.Cursor == at && host.Script == k && host.Phase == GamePhase.Running; i++)
                {
                    host.Step(new InputFrame { Pressed = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None });
                }

                Assert.True(host.Machine.Cursor != at || host.Script != k || host.Phase != GamePhase.Running, $"script {k}: the script did not go on after button {button}");
                Assert.False(host.PromptOpen);
                Assert.Empty(host.UnknownCommands);
            }

            output.WriteLine($"script {k} {Source.Scripts.Names[k]} (scene {scene}): prompt open, No and Yes both play on");
        }
    }

    [GameDataFact]
    public void ThePromptMessagesExistInEveryLanguage()
    {
        foreach (Language l in new[] { Language.Ja, Language.En, Language.Zc, Language.Zt })
        {
            Assert.False(string.IsNullOrWhiteSpace(Source.Eboot.Ui.Text(l, 216)), $"{l} 216");
            for (int f27 = 1; f27 <= 9; f27++)
            {
                Assert.False(string.IsNullOrWhiteSpace(Source.Eboot.Ui.Text(l, 1446 + f27)), $"{l} {1446 + f27}");
            }
        }

        Assert.Equal("Do you want to save your progress?", Source.Eboot.Ui.Text(Language.En, 216));
    }

    [GameDataFact]
    public void TheQaCallSelsStoresNoAndYesAndTheScriptBranchesOnIt()
    {
        List<int> scripts = ScriptsWith("_SELS(");
        int k = Assert.Single(scripts);
        int scene = Source.Eboot.GameFlow.SceneId(k);
        var jumps = new List<int>();
        foreach (int button in new[] { 0, 1 })
        {
            GameHost host = NewHost();
            host.NewGame(scene);
            int frames = PlayToPrompt(host, 20000);
            Assert.True(frames >= 0, "the SELS prompt never opened");
            Assert.Equal(ChoiceKind.TwoButtons, host.ChoiceKind);
            Assert.InRange(host.F(27), 1, 9);                             // the number of the hint segment the question names
            Assert.Equal(1446 + host.F(27), host.PromptMessageId);
            Assert.StartsWith("Play dubious hint segment #", Source.Eboot.Ui.Text(Language.En, host.PromptMessageId), StringComparison.Ordinal);
            host.Vars.SetF(0, 5);
            int at = host.Machine.Cursor;
            Answer(host, button, out int asked);
            Assert.Equal(0, asked);                                       // a SELS never asks for the save screen
            Assert.Equal(button, host.F(0));                              // _D000 = F0 receives the button index
            for (int i = 0; i < 3000 && host.Script == k && host.Phase == GamePhase.Running; i++)
            {
                host.SetSkipLatch(true);
                host.Step(new InputFrame { Pressed = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None });
            }

            jumps.Add(host.Phase == GamePhase.Running ? host.Script : -1);
            output.WriteLine($"button {button}: F0 = {host.F(0)}, script now {host.Script} ({Source.Scripts.Names[host.Script]}), phase {host.Phase}, scene {host.SceneId}, from offset {at}");
        }

        Assert.NotEqual(jumps[0], jumps[1]);                              // No and Yes take different branches (Yes jumps to the hint scene, No ends the run)
    }

    private sealed class ScriptCacheForTests(GameFolderSource source) : Tsuki.Engine.Flow.IFlowScripts
    {
        public ScriptImage Script(int k) => source.Script(k);

        public Tsuki.Engine.Flow.LabelTable Labels(int k) => source.Scripts.LabelLookup(k);
    }
}
