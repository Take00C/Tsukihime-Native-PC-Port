using Tsuki.Data;
using Tsuki.Data.Eboot.Tables;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Game;
using Tsuki.Engine.Stage;
using Tsuki.Tests.Data.Source;

namespace Tsuki.Tests.Data.Effects;

/// <summary>
/// The K7 effects on the user's scenes (no GPU): the interpreter plays a real script in skip mode until the effect shows, and the display list of that
/// frame carries the effect's commands (docs/status/knife/K7.md; the pixels are checked by Tsuki.Tests.Render and by the PNGs of <c>effect-shot</c>).
/// </summary>
public sealed class EffectsOnRealScenesTests(ITestOutputHelper output)
{
    private static GameFolderSource Source => SharedSource.Source;

    private sealed class Scripts(GameFolderSource source) : IFlowScripts
    {
        private readonly Dictionary<int, ScriptImage> _images = [];
        private readonly Dictionary<int, LabelTable> _labels = [];

        public ScriptImage Script(int k) => _images.TryGetValue(k, out ScriptImage? i) ? i : _images[k] = source.Script(k);

        public LabelTable Labels(int k) => _labels.TryGetValue(k, out LabelTable? t) ? t : _labels[k] = source.Scripts.LabelLookup(k);
    }

    private static (GameHost Host, DisplayList List) RunUntil(int script, Func<GameHost, bool> reached, int settle = 4, int maxFrames = 40_000)
    {
        var services = new GameServices(Source, new Scripts(Source))
        {
            SeLookup = (n, f) => Source.SoundEffectNames.Lookup(n, f)?.Index,
            Layouts = new GamePictureLayouts(Source),
        };
        var host = new GameHost(services, new GameOptions { Language = Language.En, Sound = false, VirtualMovies = true, RandomSeed = 7 });
        host.NewGame(Source.Eboot.GameFlow.SceneId(script));
        long n = 0;
        PadButtons held = PadButtons.None;
        InputFrame Next()
        {
            n++;
            if (host.ChoiceSelectable)
            {
                host.Choose(0);
            }

            PadButtons now = (n & 1) == 0 ? PadButtons.Decide : PadButtons.None;
            PadButtons pressed = now & ~held;
            held = now;
            if (!host.MovieActive)
            {
                host.SetSkipLatch(true);
            }

            return new InputFrame { Frame = n, Held = now, Pressed = pressed, Repeated = pressed };
        }

        while (n < maxFrames && host.Phase == GamePhase.Running && !reached(host))
        {
            host.Step(Next());
        }

        Assert.True(reached(host), $"script {script}: the effect was not reached in {n} frames (phase {host.Phase})");
        host.SetSkipLatch(false);
        var list = new DisplayList(4096);
        for (int i = 0; i < settle; i++)
        {
            list.Reset(n + i);
            host.Step(new InputFrame { Frame = n + i }, list);
        }

        return (host, list);
    }

    private static bool AnyLayer(GameHost host, Func<LayerRecord, bool> predicate) =>
        host.Stage.Drawn.Any(r => r.Used != 0 && r.Pic >= 0 && r.ZOrder > 0 && predicate(r));

    [GameDataFact]
    public void TheEffectTablesAreTheEbootsFocusAndNoteTables()
    {
        IEffectTables tables = Assert.IsAssignableFrom<IEffectTables>(Source.Eboot.Effects);
        var reader = Source.Eboot is GameEbootTables g ? g.Reader : throw new InvalidOperationException("not the game tables");
        FclnLineTable lines = FclnLineTable.Read(reader);
        Assert.Equal(30, tables.FocusEffects);
        for (int e = 0; e < 30; e++)
        {
            for (int k = 0; k < 4; k++)
            {
                FclnKey want = lines.Get(e, k);
                Assert.Equal(new FocusKey(want.TimeMs, want.CentreX, want.CentreY, want.RadiusMin, want.RadiusMax, want.CountMin, want.CountMax, want.Alpha, want.RerollMs), tables.FocusKey(e, k));
            }
        }

        Assert.Equal([24, 20, 16], tables.FocusNotes.Select(n => n.Frames));
        Assert.Equal([563, 225, 225], tables.FocusNotes.Select(n => n.Width));
    }

    [GameDataFact]
    public void AStnsLayerOfAScenePutsTheNoisePictureOnTheLayersRectangle()
    {
        (GameHost host, DisplayList list) = RunUntil(54, h => AnyLayer(h, r => r.Stns is >= 1 and <= 12 && r.Alpha > 0.1f));
        DrawCmd[] noise = [.. list.Commands.ToArray().Where(c => c.Kind == DrawKind.TexturedQuad && c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.NoiseParts })];
        Assert.NotEmpty(noise);
        Assert.All(noise, c => Assert.Equal(-1, c.Texture.D));
        Assert.All(noise, c => Assert.InRange(c.Texture.C, 0, 1));
        output.WriteLine($"script 54: {noise.Length} noise quads, stns {host.Stage.Drawn.First(r => r.Stns != 0).Stns}");
    }

    [GameDataFact]
    public void AStraLayerOfAScenePutsTheHazeOnItsQuads()
    {
        (_, DisplayList list) = RunUntil(69, h => AnyLayer(h, r => r.Stra > 0 && r.Alpha > 0.5f), settle: 20);
        DrawCmd[] haze = [.. list.Commands.ToArray().Where(c => c.Fx.Pass == QuadPass.Haze)];
        Assert.NotEmpty(haze);
        Assert.All(haze, c => Assert.Equal(TextureKind.PictureTile, c.Texture.Kind));
        Assert.Contains(haze, c => Math.Abs(c.Fx.A0) > 0f || Math.Abs(c.Fx.B0) > 0f);
        output.WriteLine($"script 69: {haze.Length} haze quads, first amplitude ({haze[0].Fx.A0:G4}, {haze[0].Fx.B0:G4}) waves ({haze[0].Fx.A1:G4}, {haze[0].Fx.B1:G4})");
    }

    [GameDataFact]
    public void AnSttrLayerOfASceneWipesThroughItsRulePicture()
    {
        (_, DisplayList list) = RunUntil(49, h => AnyLayer(h, r => r.Sttr >= 0 && r.Alpha > 0.05f), settle: 2);
        DrawCmd[] rules = [.. list.Commands.ToArray().Where(c => c.Fx.Pass == QuadPass.Rule)];
        Assert.NotEmpty(rules);
        Assert.All(rules, c => Assert.Equal((TextureKind.PictureTile, 4), (c.Texture.Kind, c.Texture.A)));
        Assert.All(rules, c => Assert.InRange(c.Texture.B, 86, 134));              // RULE_01..RULE_49 are allpac3 entries 86..134
        Assert.Contains(list.Commands.ToArray(), c => c.Kind == DrawKind.Clear && c.Target == StageRuntime.MaskTarget);
        Assert.Contains(list.Commands.ToArray(), c => c.Fx.Mask == StageRuntime.MaskTarget);
    }

    [GameDataFact]
    public void ACutInWindowMasksTheLayersThatNameIt()
    {
        (GameHost host, DisplayList list) = RunUntil(1, h => AnyLayer(h, r => r.Mask >= 0 && r.Alpha > 0.5f && h.Stage.Drawn[r.Mask].Used != 0 && h.Stage.Drawn[r.Mask].Alpha > 0.5f));
        DrawCmd[] cmds = list.Commands.ToArray();
        int clears = cmds.Count(c => c.Kind == DrawKind.Clear && c.Target == StageRuntime.MaskTarget);
        Assert.True(clears >= 1, "the window layer clears and fills the mask target");
        Assert.Contains(cmds, c => c.Target == StageRuntime.MaskTarget && c.EffectWord == 58);                     // the silhouette
        Assert.Contains(cmds, c => c.Kind == DrawKind.TexturedQuad && c.Target == 0 && c.Fx.Mask == StageRuntime.MaskTarget);
        output.WriteLine($"script 1: {clears} mask group(s), {cmds.Count(c => c.Fx.Mask != 0)} masked quads, {host.Stage.DrawnLayers.Count} layers");
    }

    [GameDataFact]
    public void AGradedLayerCarriesAGradeKeyThatResolvesToAnStecMode()
    {
        (_, DisplayList list) = RunUntil(1, h => AnyLayer(h, r => r.GradeKey != 0 && r.Alpha > 0.5f));
        DrawCmd graded = list.Commands.ToArray().First(c => c.Kind == DrawKind.TexturedQuad && c.Texture is { Kind: TextureKind.PictureTile, D: not 0 });
        ColourGrade grade = GradeKeys.Resolve(graded.Texture.D);
        Assert.InRange(grade.Mode, 1, 81);
        output.WriteLine($"script 1: grade key {graded.Texture.D} = mode {grade.Mode}, brightness {grade.Brightness}, contrast {grade.Contrast}");
    }

    [GameDataFact]
    public void FclnDrawsFocusLinesOfTheStripAndFadeFortyDrawsTheGhost()
    {
        (GameHost host, DisplayList list) = RunUntil(173, h => h.FocusState == FocusMode.On);
        Assert.InRange(host.FocusEffect, 4, 33);
        DrawCmd[] lines = [.. list.Commands.ToArray().Where(c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.EffectParts, C: 3 })];
        Assert.NotEmpty(lines);
        output.WriteLine($"script 173: effect {host.FocusEffect}, {lines.Length} lines");

        (host, list) = RunUntil(1, h => h.GhostMode == 4 && h.GhostAlpha >= 0.9f);
        DrawCmd[] cmds = list.Commands.ToArray();
        Assert.Contains(cmds, c => c.Kind == DrawKind.Capture && c.Texture.A == Tsuki.Engine.Game.Effects.GhostEffect.SourceSurface);
        Assert.Contains(cmds, c => c.Kind == DrawKind.TexturedQuad && c.EffectWord == 22 && c.Texture.A == Tsuki.Engine.Game.Effects.GhostEffect.SumSurface);
        Assert.Equal(host.Ghost.Mode, host.GhostMode);
    }

    [GameDataFact]
    public void AChoiceDrawsItsVignetteAndTheCursorGlow()
    {
        (GameHost host, DisplayList list) = RunUntil(4, h => h.ChoiceSelectable, settle: 1);
        DrawCmd[] cmds = list.Commands.ToArray();
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.SelectParts, C: 5 });          // the vignette
        // press down: the cursor moves onto line 0 and its glow, underline and flare join the list
        var input = new InputFrame { Frame = 1000, Pressed = PadButtons.Down, Repeated = PadButtons.Down, Held = PadButtons.Down };
        list.Reset(1000);
        host.Step(input, list);
        for (int i = 0; i < 12; i++)
        {
            list.Reset(1001 + i);
            host.Step(new InputFrame { Frame = 1001 + i }, list);
        }

        Assert.Equal(0, host.ChoiceCursor);
        cmds = list.Commands.ToArray();
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.SelectParts, C: 2 } && c.EffectWord == 0xB);   // the underline
        Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.SelectParts, C: 4 } && c.EffectWord == 0xB);   // the flare
        int glyphQuads = cmds.Count(c => c.Texture.Kind == TextureKind.GlyphPage);
        output.WriteLine($"script 4: {cmds.Length} commands, {glyphQuads} glyph quads with the 8 glow copies");
        // decide: the ring, the bands and the lens flare
        host.Step(new InputFrame { Frame = 2000, Pressed = PadButtons.Decide, Repeated = PadButtons.Decide, Held = PadButtons.Decide }, list = NewList());
        for (int i = 0; i < 6; i++)
        {
            list.Reset(2001 + i);
            host.Step(new InputFrame { Frame = 2001 + i }, list);
        }

        Assert.Equal(3, host.ChoicePhase);
        foreach (int child in new[] { 0, 1, 3 })
        {
            Assert.Contains(list.Commands.ToArray(), c => c.Texture is { Kind: TextureKind.UiTile, A: (int)UiSet.SelectParts } && c.Texture.C == child);
        }
    }

    private static DisplayList NewList()
    {
        var l = new DisplayList(4096);
        l.Reset(0);
        return l;
    }
}
