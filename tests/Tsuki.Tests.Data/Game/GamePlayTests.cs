using System.Text;
using Tsuki.Cli.Commands;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Vm;
using Tsuki.Tests.Data.Source;

namespace Tsuki.Tests.Data.Game;

/// <summary>The game interpreter on the user's data: the eboot flow tables, every command name of the 549 scripts, and the headless runner.</summary>
public sealed class GamePlayTests(ITestOutputHelper output)
{
    private static GameFolderSource Source => SharedSource.Source;

    [GameDataFact]
    public void TheFlowTablesOfTheEbootHoldTheRetailCounts()
    {
        IGameFlowTables flow = Source.Eboot.GameFlow;
        Assert.Equal(549, flow.SceneCount);
        Assert.Equal(0, flow.ScriptOfSceneId(110));            // START_SCRIPT
        Assert.Equal(2, flow.ScriptOfSceneId(260));            // 00_01
        Assert.Equal(-1, flow.ScriptOfSceneId(1800));
        Assert.Equal(630, flow.Edges.Count);
        Assert.Equal(1286, flow.Nodes.Count);
        Assert.Equal(19, flow.JumpClears.Count);
        Assert.Contains(967, flow.JumpKeepWords);               // PS4-only variables of the keep lists (knife_tables.md)
        Assert.Contains(501, flow.JumpKeepBytes);
        Assert.Contains(500, flow.JumpKeepBits);
        Assert.Contains(flow.Nodes, n => n.Scene == 485 && n.ScriptScene >= 0);
        output.WriteLine($"{flow.SceneCount} scenes, {flow.Nodes.Count} nodes, {flow.Edges.Count} edges, {flow.JumpClears.Count} clear pairs, keep lists {flow.JumpKeepWords.Count}/{flow.JumpKeepBytes.Count}/{flow.JumpKeepBits.Count}");
    }

    [GameDataFact]
    public void AVariantPictureCountsAsItsBaseForTheGallery()
    {
        IGameFlowTables flow = Source.Eboot.GameFlow;
        var pictures = Source.Eboot.Pictures;
        int baseId = pictures.Lookup("IMG1696");
        int variant = pictures.Lookup("IMG1696_11_11");
        Assert.True(baseId >= 0 && variant >= 0);
        int seen = flow.PictureSeenIndex(baseId);
        Assert.InRange(seen, 0, SystemBlock.PictureSeenBits - 1);
        Assert.Equal(seen, flow.PictureSeenIndex(variant));
        Assert.Equal(-1, flow.PictureSeenIndex(-1));
    }

    [GameDataFact]
    public void EveryStatementOfEveryScriptIsACommandTheInterpreterKnows()
    {
        var used = new Dictionary<string, int>();
        long statements = 0;
        for (int k = 0; k < Source.ScriptCount; k++)
        {
            ScriptImage image = Source.Script(k);
            ReadOnlyMemory<byte> bytes = image.Bytes;
            int pos = 0;
            while (pos < bytes.Length)
            {
                ScriptSegment seg = ScriptSegmenter.Read(bytes.Span, pos);
                pos = seg.End;
                if (!ScriptSegmenter.TryGetStatement(k, bytes, seg, out Statement st))
                {
                    continue;
                }

                statements++;
                Cmd c = GameCommands.Classify(st.Name);
                if (c == Cmd.Unknown)
                {
                    Assert.Fail($"script {k} offset 0x{st.Offset:x}: {st.Name} is not in the command table");
                }

                string n = st.Name.First is (byte)'Z' or (byte)'J' ? st.Name.ToString()[..2] : st.Name.ToString();
                used[n] = used.GetValueOrDefault(n) + 1;
            }
        }

        output.WriteLine($"{statements} statements, {used.Count} distinct command names (markers and jumps counted by their first two letters)");
        Assert.InRange(used.Count, 95, 110);
    }

    private static (int Exit, string Out, string Err) RunCli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = new PlayCommand().Run(args[1..], o, e);
        return (code, o.ToString(), e.ToString());
    }

    [GameDataFact]
    public void AFreshProfilePlaysThePrologueToTheFirstChoiceWithoutAnUnknownCommandOrAnException()
    {
        (int code, string text, string err) = RunCli("play", "--until-first-choice", "--games", "5", "--mode", "click");
        output.WriteLine(text);
        Assert.Equal(string.Empty, err);
        Assert.Equal(0, code);
        Assert.Contains("game 1: New Game -> scene 260 (00_01)", text, StringComparison.Ordinal);
        Assert.Contains("game 2: New Game -> scene 110 (START_SCRIPT)", text, StringComparison.Ordinal);
        Assert.Contains("first choice: script 4 (01_00_ARC01_1), choice point 1, 3 lines", text, StringComparison.Ordinal);
        Assert.Contains("unknown commands: none", text, StringComparison.Ordinal);
        Assert.Contains("exceptions: none", text, StringComparison.Ordinal);
        Assert.Contains("scenes reached: 4 of 549", text, StringComparison.Ordinal);
    }

    [GameDataFact]
    public void ACoverageRunFromTheTitleReachesMoreThanAHundredScenesWithoutAnUnknownCommandOrAnException()
    {
        (int code, string text, string err) = RunCli("play", "--coverage", "--no-aim", "--no-jump", "--max-runs", "5", "--patience", "2", "--mode", "skip");
        output.WriteLine(string.Join(Environment.NewLine, text.Split((char)10).Where(l => !l.StartsWith("run ", StringComparison.Ordinal))));
        Assert.Equal(string.Empty, err);
        Assert.Equal(0, code);
        Assert.Contains("unknown commands: none", text, StringComparison.Ordinal);
        Assert.Contains("exceptions: none", text, StringComparison.Ordinal);
        int at = text.IndexOf("scenes reached: ", StringComparison.Ordinal) + "scenes reached: ".Length;
        Assert.True(int.Parse(text[at..text.IndexOf(' ', at)], System.Globalization.CultureInfo.InvariantCulture) > 100);
    }

    [GameDataFact]
    public void NoStcfWaitOfTheArcueidClickSweepOutlivesItsOwnLayersCondition()
    {
        // R1-engine 1: the K15 click sweep of this route (zc) sat on a black screen for up to 798 s because _STCF waited for every layer an earlier _STCF
        // had named. With the statement registering only its own layer the longest wait is a layer's own motion (456 frames in this run); 600 is the ceiling.
        string trace = Path.Combine(Path.GetTempPath(), "tsuki-r1-stcf-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            (int code, string text, string err) = RunCli(
                "play", "--policy", "list:2,1,0,1,2,1,2,1,1,0,2,1,0,0,0,1,1,0,0,1,1,0,1,2,0,2,1,1,2,1,2,1,0,1,2,1,0,0,2,2,1,0,1,0,0", "--mode", "click", "--lang", "zc",
                "--games", "2", "--max-frames", "1600000", "--trace", trace);
            Assert.Equal(string.Empty, err);
            Assert.Equal(0, code);
            Assert.Contains("exceptions: none", text, StringComparison.Ordinal);
            int waits = 0;
            int longest = 0;
            long total = 0;
            string where = string.Empty;
            string? pending = null;
            long pendingFrame = 0;
            long lastFrame = 0;
            foreach (string line in File.ReadLines(trace))
            {
                if (line.Length < 10 || line[0] != 'F' || !long.TryParse(line.AsSpan(1, 8), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long frame))
                {
                    continue;
                }

                lastFrame = frame;
                if (pending is not null)
                {
                    int gap = (int)(frame - pendingFrame);
                    total += gap;
                    if (gap > longest)
                    {
                        longest = gap;
                        where = pending;
                    }

                    pending = null;
                }

                if (line.Contains(" STCF Host -> Yield", StringComparison.Ordinal))
                {
                    waits++;
                    pending = line;
                    pendingFrame = frame;
                }
            }

            output.WriteLine($"{lastFrame} frames, {waits} _STCF waits holding {total} frames in all, the longest {longest} frames at: {where}");
            Assert.True(waits > 500, $"only {waits} _STCF waits in the trace");
            Assert.True(longest <= 600, $"an _STCF held the script for {longest} frames: {where}");
            Assert.True(lastFrame < 1_000_000, $"the sweep took {lastFrame} frames (1,442,117 with the accumulated waits)");
        }
        finally
        {
            File.Delete(trace);
        }
    }

    private static GameHost NewHost()
    {
        var services = new GameServices(Source, new ScriptCacheForTests(Source)) { SeLookup = (n, f) => Source.SoundEffectNames.Lookup(n, f)?.Index };
        return new GameHost(services, new GameOptions { Language = Language.En, VirtualMovies = true });
    }

    private static int Play(GameHost host, int maxFrames = 400_000)
    {
        int n = 0;
        while (host.Phase == GamePhase.Running && n < maxFrames)
        {
            if (host.ChoiceSelectable)
            {
                host.Choose(host.ChoiceKind == ChoiceKind.List ? host.ChoiceLines.Select((l, i) => (l, i)).First(t => !t.l.Locked).i : host.ChoiceKind == ChoiceKind.SavePrompt ? 0 : 1);
            }

            host.SetSkipLatch(true);
            host.Step(new InputFrame { Pressed = (n & 1) == 0 ? PadButtons.Decide : PadButtons.None });
            n++;
        }

        return n;
    }

    [GameDataFact]
    public void TheFlowchartJumpStartsTheSceneOfANodeAndTheSpecialEntriesRun()
    {
        GameHost host = NewHost();
        host.Config.FastForwardReadOnly = 0;
        int node = Source.Eboot.GameFlow.NodeOfScript(76);                 // 03_01_CIEL03_3_2
        Assert.True(node >= 1);
        Assert.True(host.JumpToNode(node));
        Assert.Equal(76, host.Script);
        Assert.Equal(node, host.F(110));
        Assert.Equal(Source.Eboot.GameFlow.SceneId(76), host.F(26));
        Play(host);
        Assert.Equal(GamePhase.Ended, host.Phase);
        Assert.True(host.ScriptsReached.Count > 3);
        output.WriteLine($"jump to node {node}: {host.ScriptsReached.Count} scenes, ended {host.EndReason}");

        host.NewGame(4000);                                                // the staff roll
        Play(host);
        Assert.Equal(GamePhase.Ended, host.Phase);
        host.NewGame(270);                                                 // the scripted prologue 00_02
        Play(host);
        Assert.Equal(GamePhase.Ended, host.Phase);
        Assert.True(host.StartReplay(1000));                               // a gallery replay of QA_1 through ARCHIVE
        Play(host);
        Assert.Equal(GamePhase.Ended, host.Phase);
        Assert.Contains(528, host.ScriptsReached);
        Assert.Empty(host.UnknownCommands);
    }

    [GameDataFact]
    public void ThePrologueSceneCompletesItsMovieAndSetsTheMovieSeenBitAndNodeBit485()
    {
        var services = new GameServices(Source, new ScriptCacheForTests(Source));
        var host = new GameHost(services, new GameOptions { Language = Language.En, VirtualMovies = true });
        host.NewGame(260);
        int frames = 0;
        while (host.Phase == GamePhase.Running && frames < 5000)
        {
            host.Step(default);
            frames++;
        }

        Assert.Equal(EndReason.ReturnKeepBgm, host.EndReason);
        Assert.True(host.IsSceneReached(485));
        Assert.True((host.Vars.GetG(5) & 4) != 0);
        Assert.Equal(110, host.NewGameSceneId());
        output.WriteLine($"00_01 ended after {frames} frames");
    }

    [GameDataFact]
    public void ThePrologueNarrationRunsOnTheFramesOfTheWaitModelWorkedExample()
    {
        // wait_model.md 12.1 (00_00, English, default config, the click policy "press in the first frame the message is clickable"):
        // _ZM01801 starts in frame 294 and is revealed in 410 (116 frames, the window opens meanwhile); the next message starts in 471 and is revealed in 556
        GameHost host = NewHost();
        host.NewGame(110);
        int origin = -1;
        host.SceneEntered += (k, _) =>
        {
            if (k == 1 && origin < 0)
            {
                origin = (int)host.Frame;            // the _JUMP yields: 00_00 runs from the next frame
            }
        };
        var started = new List<int>();
        var shown = new List<int>();
        host.MessageStarted += (_, _, _) => started.Add((int)host.Frame - 1 - origin);
        bool wasShown = false;
        int guard = 0;
        while (host.Phase == GamePhase.Running && shown.Count < 2 && guard++ < 5000)
        {
            bool press = (host.Wait.Bits & WaitBits.Shown) != 0 && (host.Wait.Bits & WaitBits.Click) != 0;
            host.Step(press ? new InputFrame { Held = PadButtons.Decide, Pressed = PadButtons.Decide } : default);
            bool now = (host.Wait.Bits & WaitBits.Shown) != 0;
            if (now && !wasShown && origin >= 0)
            {
                shown.Add((int)host.Frame - 1 - origin);
            }

            wasShown = now;
        }

        output.WriteLine($"origin {origin}, starts {string.Join(",", started)}, shown {string.Join(",", shown)}");
        // The sim's linear plan has no _IF__ yield and no F1103 from _STMA(3,1) (the layer motion without a wait argument hides the text, VA 0x164BA6E):
        // the first message starts one frame earlier here, its text fades in over 18 frames after the window (17 stalled frames, not 16), and every other
        // number of the worked example comes out the same: revealed in 410, the next message in 471 and revealed in 556
        Assert.Equal([293, 471], started.Take(2).ToArray());
        Assert.Equal([410, 556], shown.Take(2).ToArray());
    }

    [GameDataFact]
    public void AFrameWithAMessageDrawsTheWindowSheetAndTheGlyphQuads()
    {
        GameHost host = NewHost();
        host.NewGame(110);
        int guard = 0;
        while (host.Phase == GamePhase.Running && (host.Wait.Bits & WaitBits.Shown) == 0 && guard++ < 2000)
        {
            host.Step(default);
        }

        Assert.NotNull(host.CurrentMessage);
        var list = new DisplayList();
        list.Reset(host.Frame);
        host.Step(default, list);
        int glyphs = 0;
        int sheets = 0;
        foreach (DrawCmd c in list.Commands)
        {
            if (c.Texture.Kind == TextureKind.GlyphPage)
            {
                glyphs++;
                Assert.InRange(c.V0.X, -200f, 2200f);
                Assert.InRange(c.V0.U, 0f, 1f);
            }
            else if (c.Texture.Kind == TextureKind.UiTile)
            {
                sheets++;
            }
        }

        output.WriteLine($"{list.Count} commands: {glyphs} glyph quads, {sheets} window sheet quads");
        Assert.Equal(1, sheets);
        Assert.True(glyphs >= 2 * 43, $"only {glyphs} glyph quads");        // the first line of 00_00 (id 107) has 43 glyphs: outline + fill per glyph
    }

    private sealed class ScriptCacheForTests(GameFolderSource source) : Tsuki.Engine.Flow.IFlowScripts
    {
        public ScriptImage Script(int k) => source.Script(k);

        public Tsuki.Engine.Flow.LabelTable Labels(int k) => source.Scripts.LabelLookup(k);
    }
}
