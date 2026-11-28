using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Stage;

namespace Tsuki.Tests.Data.Game;

/// <summary>
/// Issue #16 (01_00_ARC01_1, scene 300): Ciel's two three-slice reveals at the station, played in the port's own frame schedule (no input while the animated
/// reveal is staged, so only the script's <c>_STCF</c> and <c>_WTTM</c> waits pass time). The static reveal commits three crop windows at alpha 0 (<c>_FADS</c> at
/// 0x73B3): the PS4 lays each group over the screen through the destination alpha that only the window's own mask pass writes, at the window's alpha
/// (0x5A5E0 -> 0x51060 with esi = 1), so nothing of them shows. The animated reveal runs its windows' fade-in motions on records that are not drawn yet and
/// commits once (0x8679): the three groups appear together at full strength, not right, middle, left.
/// </summary>
public sealed class TramRevealOnRealDataTests(ITestOutputHelper output)
{
    private const int StaticCommit = 0x73B3;
    private const int AnimatedFirst = 0x7FC2;       // the _STCH of layer 28, the first window of the animated reveal
    private const int AnimatedCommit = 0x8679;

    private static readonly int[] StaticWindows = [16, 21, 24];
    private static readonly int[] AnimatedWindows = [28, 33, 36];

    [GameDataFact]
    public void TheStaticRevealShowsNoSliceAndTheAnimatedOneShowsAllThreeAtOnce()
    {
        GameHost host = FirstChoiceRig.NewHost(Language.En);
        var seen = new List<int>();
        host.SetObserver(s =>
        {
            if (s.Script == FirstChoiceRig.Script && s.DisplayName is "FADS" or "STCH")
            {
                seen.Add(s.Offset);
            }
        });
        host.NewGame(300);
        var list = new DisplayList(16384);
        long n = 0;
        int staticFrames = 0;
        long staticAt = -1;
        long stagedAt = -1;
        bool animatedChecked = false;
        for (int i = 0; i < 30_000 && !animatedChecked && host.Phase == GamePhase.Running; i++)
        {
            seen.Clear();
            list.Reset(++n);
            bool staged = stagedAt >= 0;
            host.Step(new InputFrame { Frame = n, Pressed = !staged && (i & 1) == 0 ? PadButtons.Decide : PadButtons.None }, list);
            DrawCmd[] cmds = list.Commands.ToArray();

            if (seen.Contains(StaticCommit))
            {
                staticAt = host.Frame;
            }

            // from the static commit until the script clears the windows: they stay at alpha 0 and give the layers they hold an empty mask
            if (staticAt >= 0 && stagedAt < 0 && StaticWindows.All(w => host.Stage.Drawn[w].Used != 0))
            {
                foreach (int w in StaticWindows)
                {
                    LayerRecord r = host.Stage.Drawn[w];
                    Assert.Equal(0f, MathF.Min(1f, r.Alpha + r.AlphaCur));
                    Assert.Equal(-1, r.Sttr);
                    Assert.True(r.Crop[2] > 0 && r.Crop[3] > 0, $"window {w} has no crop rectangle");
                }

                int silhouette = cmds.Count(c => c.Target == StageRuntime.MaskTarget && c.Kind != DrawKind.Clear);
                Assert.True(silhouette == 0, $"frame {host.Frame}: {silhouette} silhouette quads give the held layers a mask above 0");
                Assert.Contains(cmds, c => c.Kind == DrawKind.TexturedQuad && c.Fx.Mask == StageRuntime.MaskTarget);  // the held layers, masked by the empty target

                // nor a ridge: the windows' ridges (inset 15, modes 2 / 2 / 1) go through the same weight; one ridge piece is a fill or a border tile in a window's scissor
                PixelRect?[] scissors = [.. cmds.Where(c => c.Fx.Mask == StageRuntime.MaskTarget && c.Clip is not null).Select(c => c.Clip).Distinct()];
                int ridge = cmds.Count(c => c.Target == 0 && scissors.Contains(c.Clip) && (c.Kind == DrawKind.FillQuad || c.Texture.Kind == TextureKind.UiTile));
                Assert.True(ridge == 0, $"frame {host.Frame}: {ridge} ridge pieces of invisible windows");
                staticFrames++;
            }

            if (seen.Contains(AnimatedFirst))
            {
                stagedAt = host.Frame;
            }

            if (stagedAt >= 0 && !seen.Contains(AnimatedCommit))
            {
                // staged: the motions run, nothing of the reveal is drawn
                Assert.True(!host.Stage.DrawnLayers.Any(l => l is >= 28 and <= 38), $"frame {host.Frame}: a staged layer of the reveal was drawn");
                continue;
            }

            if (seen.Contains(AnimatedCommit))
            {
                Assert.True(stagedAt >= 0, "the animated reveal was committed before it was staged");
                (short Motion, long Frames)[] expected = [(110, 109), (115, 75), (118, 49)];
                for (int k = 0; k < AnimatedWindows.Length; k++)
                {
                    LayerRecord r = host.Stage.Drawn[AnimatedWindows[k]];
                    Assert.NotEqual(0, r.Used);
                    Assert.Equal(1f, MathF.Min(1f, r.Alpha + r.AlphaCur));
                    Assert.Equal(expected[k], (r.MotionId, r.MotionFrames));
                    Assert.Contains(AnimatedWindows[k], host.Stage.DrawnLayers);
                }

                DrawCmd[] silhouettes = [.. cmds.Where(c => c.Target == StageRuntime.MaskTarget && c.Kind == DrawKind.TexturedQuad && c.EffectWord == 58)];
                Assert.True(silhouettes.Length >= AnimatedWindows.Length, $"{silhouettes.Length} silhouettes");
                Assert.All(silhouettes, c => Assert.Equal(0xFFu, c.V0.Rgba >> 24));                                  // all three at full strength at once

                // the PS4 turns windows 28 and 33 by their _STRR angles about their crop centres (0x4C720 / 0xDEC90, flags 0x34): their masks are turned inner
                // rectangles (nine cells on the ridge's grid, after a clear of the soft target); window 36 has no angle and keeps its straight scissor (crop -64,
                // -226, 903, 1512 shrunk by its inset 15: x 0..824)
                Assert.Equal([-1386, 1153, 0], AnimatedWindows.Select(w => (int)host.Stage.Drawn[w].Angle));
                var masks = new List<List<DrawCmd>>();
                foreach (DrawCmd c in cmds.Where(c => c.Target == StageRuntime.SoftTarget))
                {
                    if (c.Kind == DrawKind.Clear)
                    {
                        masks.Add([]);
                    }
                    else
                    {
                        masks[^1].Add(c);
                    }
                }

                List<DrawCmd>[] turned = [.. masks.Where(m => m.Count == 9 && m.All(c => c.Kind == DrawKind.FillQuad))];
                Assert.Equal(2, turned.Length);
                foreach (int w in AnimatedWindows[..2])
                {
                    LayerRecord r = host.Stage.Drawn[w];
                    (float cx, float cy) = (r.Crop[0] + (r.Crop[2] / 2f), r.Crop[1] + (r.Crop[3] / 2f));
                    List<DrawCmd> m = turned.Single(m => MathF.Abs(((m[0].V0.X + m[8].V3.X) / 2f) - cx) < 0.5f && MathF.Abs(((m[0].V0.Y + m[8].V3.Y) / 2f) - cy) < 0.5f);
                    (Vertex tl, Vertex tr, Vertex bl, Vertex br) = (m[0].V0, m[2].V1, m[6].V2, m[8].V3);
                    double top = Math.Atan2(tr.Y - tl.Y, tr.X - tl.X) * 180 / Math.PI;
                    Assert.InRange(top, (r.Angle / 100.0) - 0.02, (r.Angle / 100.0) + 0.02);
                    output.WriteLine($"window {w}: crop {string.Join(',', r.Crop)}, angle {r.Angle}, inner ({tl.X:F1},{tl.Y:F1}) ({tr.X:F1},{tr.Y:F1}) ({br.X:F1},{br.Y:F1}) ({bl.X:F1},{bl.Y:F1}), top edge {top:F3} deg");
                }

                Assert.Contains(cmds, c => c.Target == 0 && c.Kind == DrawKind.TexturedQuad && c.Clip == new PixelRect(0, 0, 824, 1080));
                output.WriteLine($"static commit at frame {staticAt} ({staticFrames} frames checked), staged at {stagedAt}, committed at {host.Frame} " +
                    $"({host.Frame - stagedAt} frames staged), {silhouettes.Length} silhouette quads");
                animatedChecked = true;
            }
        }

        Assert.True(staticFrames > 40, $"the static reveal was checked on {staticFrames} frames");
        Assert.True(animatedChecked, "the animated reveal's commit was not reached");
    }
}
