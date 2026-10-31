using Tsuki.App.Play;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Stage;
using Tsuki.Platform;
using Tsuki.Render.Gl;
using Tsuki.Render.Textures;
using Tsuki.Tests.Data.Source;

namespace Tsuki.Tests.Data.App;

/// <summary>
/// Package K8, the hitches: nothing the main thread needs is decoded on it when it can be known earlier. The scripts a script jumps to are decoded on a worker,
/// the sizes and layouts of the UI sheets come from the image headers, a tile the look-ahead names is decoded behind the urgent ones and is promoted when a frame
/// names it, and the look-ahead of a story scene predicts the tiles the stage then draws (the picture and its colour grade).
/// </summary>
[Collection(SdlWindowCollection.Name)]
public sealed class LoadingTests(ITestOutputHelper output)
{
    private static GameFolderSource Source => SharedSource.Source;

    [GameDataFact]
    public void ASheetSizeAndLayoutComeFromTheHeaderWithoutDecodingAPixel()
    {
        foreach ((UiSet set, int child) in new[] { (UiSet.DispParts, 11), (UiSet.DispParts, 12), (UiSet.SelectParts, 5), (UiSet.BorderParts, 3), (UiSet.MenuParts, 0) })
        {
            Picture full = Source.UiSheet(set, Language.En, child);
            Assert.Equal((full.Width, full.Height), Source.UiSheetSize(set, Language.En, child));
            Picture layout = Source.UiSheetLayout(set, Language.En, child);
            Assert.Equal((full.Width, full.Height), (layout.Width, layout.Height));
            Assert.Equal(full.Tiles.Length, layout.Tiles.Length);
            for (int i = 0; i < full.Tiles.Length; i++)
            {
                Assert.Equal(full.Tiles[i].Flags, layout.Tiles[i].Flags);
                Assert.Equal(full.Tiles[i].Source, layout.Tiles[i].Source);
                Assert.Equal((full.Tiles[i].X, full.Tiles[i].Y, full.Tiles[i].StoredWidth, full.Tiles[i].StoredHeight), (layout.Tiles[i].X, layout.Tiles[i].Y, layout.Tiles[i].StoredWidth, layout.Tiles[i].StoredHeight));
                Assert.True(layout.Tiles[i].Rgba.IsEmpty);
            }
        }
    }

    [GameDataFact]
    public void TheScriptsAScriptJumpsToAreDecodedOnAWorkerWhenItIsFirstUsed()
    {
        var cache = new ScriptCache(Source);
        int start = Source.Eboot.GameFlow.ScriptOfSceneId(110);          // START_SCRIPT jumps on into the story
        _ = cache.Script(start);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // the prefetch runs on the thread pool, which the full parallel run keeps busy with the decode tests: it can be seconds before a pool thread is free (package K10)
        while (cache.Prefetched == 0 && sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            Thread.Sleep(5);
        }

        Assert.True(cache.Prefetched > 0, "no jump target was decoded ahead");
        output.WriteLine($"script {start}: {cache.Prefetched} jump targets decoded ahead after {sw.ElapsedMilliseconds} ms");
        Assert.NotNull(cache.Script(1));
    }

    [GameDataFact]
    public void ALookAheadTileIsDecodedInTheBackgroundAndPromotedWhenAFrameNamesIt()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("loading", 64, 64, Hidden: true, Resizable: false, SwapInterval: 0));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        using var cache = new TextureCache(device.Gl, 512L << 20);
        using var feeder = new TextureFeeder(Source, cache);
        int pic = Source.Eboot.Pictures.Lookup("IMG1696");
        PictureLayout layout = new GamePictureLayouts(Source).Get(pic, (int)Language.En)!;
        PictureFileLayout file = layout.Files[0];
        TextureRef[] keys = [.. file.Tiles.Take(6).Select(t => TextureRef.ForPictureTile(file.Archive, file.Entry, t.Tile))];
        feeder.Prefetch(keys);
        Assert.Equal(keys.Length, feeder.Stats.Prefetched);
        feeder.Prefetch(keys);                                              // named twice: queued once
        Assert.Equal(keys.Length, feeder.Stats.Prefetched);

        // a frame that names them finds them on their way: nothing is queued again, the wait is short, every key becomes resident
        var list = new DisplayList(16);
        list.Reset(1);
        foreach (TextureRef key in keys)
        {
            list.Add(new DrawCmd { Kind = DrawKind.TexturedQuad, Texture = key, V0 = new Vertex(0, 0, 0, 0, 0xFFFFFFFF), V1 = new Vertex(1, 0, 1, 0, 0xFFFFFFFF), V2 = new Vertex(0, 1, 0, 1, 0xFFFFFFFF), V3 = new Vertex(1, 1, 1, 1, 0xFFFFFFFF), BlendState = 1 });
        }

        list.Seal();
        Assert.Equal(0, feeder.EnsureResident(list, TimeSpan.FromSeconds(30)));
        Assert.Empty(feeder.LastFresh);
        Assert.All(keys, k => Assert.True(cache.Contains(k)));
        Assert.Equal(keys.Length, feeder.Stats.Requested);
    }

    [GameDataFact]
    public void TheLookAheadOfTheFirstScenePredictsTheTilesTheStageDraws()
    {
        // run the first story scene (script 1, 00_02) and, at every step, ask the look-ahead what the next statements need; the stage's own display lists must then
        // name (almost) every picture tile it draws among the predicted ones, with the same colour-grade key
        var services = new GameServices(Source, new ScriptCacheForTests(Source)) { Layouts = new GamePictureLayouts(Source), SeLookup = (n, f) => Source.SoundEffectNames.Lookup(n, f)?.Index };
        var host = new GameHost(services, new GameOptions { Language = Language.En, Sound = false, VirtualMovies = true, RandomSeed = 7 });
        host.NewGame(Source.Eboot.GameFlow.SceneId(1));
        var predicted = new HashSet<TextureRef>();
        var drawn = new HashSet<TextureRef>();
        var hints = new List<TextureRef>();
        var list = new DisplayList(8192);
        for (int i = 0; i < 1500 && host.Phase == GamePhase.Running; i++)
        {
            host.SetSkipLatch(true);
            list.Reset(i);
            host.Step(new InputFrame { Frame = i, Pressed = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None }, list);
            hints.Clear();
            host.LookAhead(hints);
            foreach (TextureRef h in hints)
            {
                predicted.Add(h);
            }

            foreach (DrawCmd c in list.Commands)
            {
                if (c.Kind == DrawKind.TexturedQuad && c.Texture.Kind == TextureKind.PictureTile)
                {
                    drawn.Add(c.Texture);
                }
            }
        }

        Assert.NotEmpty(drawn);
        int hit = drawn.Count(predicted.Contains);
        int graded = drawn.Count(k => k.D != 0);
        int gradedHit = drawn.Count(k => k.D != 0 && predicted.Contains(k));
        output.WriteLine($"{drawn.Count} picture tiles drawn in 1,500 steps of script 1: {hit} were named by the look-ahead before; of {graded} graded ones {gradedHit}");
        Assert.True(hit >= drawn.Count * 9 / 10, $"only {hit} of {drawn.Count} drawn tiles were predicted");
        Assert.True(graded == 0 || gradedHit >= graded * 8 / 10, $"only {gradedHit} of {graded} graded tiles were predicted");
    }

    private sealed class ScriptCacheForTests(GameFolderSource source) : Tsuki.Engine.Flow.IFlowScripts
    {
        public ScriptImage Script(int k) => source.Script(k);

        public Tsuki.Engine.Flow.LabelTable Labels(int k) => source.Scripts.LabelLookup(k);
    }
}
