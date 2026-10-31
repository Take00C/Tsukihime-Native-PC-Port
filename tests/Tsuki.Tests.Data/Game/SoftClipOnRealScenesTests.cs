using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Stage;

namespace Tsuki.Tests.Data.Game;

/// <summary>
/// Package K8: the prologue's picture panels (script 1 = 00_02, scene 240: <c>_STCH(17,img1688,266,82,1000,255,518,,CC,,,,160,-285,888,1650,90,0)</c> and its kin) are clip windows with
/// a soft edge (STCH a16 = 90, a17 = 0). The stage's drawn record equals the statement, the window is the crop rectangle in frame pixels, and the display list builds the nine-slice mask
/// of the PS4's soft clip for it (fade 90 px at the two sides that lie inside the screen, none where the panel runs past the frame).
/// </summary>
public sealed class SoftClipOnRealScenesTests(ITestOutputHelper output)
{
    [GameDataFact]
    public void TheProloguePanelIsASoftWindowOfThePicturesCropRectangle()
    {
        GameHost host = FirstChoiceRig.NewHost(Language.En);
        host.NewGame(host.NewGameSceneId());              // the prologue movie (00_01); the second New Game starts START_SCRIPT, which runs on into scene 240
        long n = 8_000_000;
        int found = -1;
        for (int i = 0, games = 1; i < 40_000 && found < 0; i++)
        {
            if (host.Phase != GamePhase.Running && games < 3)
            {
                host.NewGame(host.NewGameSceneId());
                games++;
            }

            host.SetSkipLatch(true);
            host.Step(new InputFrame { Pressed = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None });
            for (int l = 0; l < StageRuntime.LayerCount; l++)
            {
                LayerRecord r = host.Stage.Drawn[l];
                if (r.Used != 0 && r.Crop[2] == 888 && r.Crop[3] == 1650 && r.A16 == 90)
                {
                    found = l;
                    break;
                }
            }
        }

        Assert.True(found >= 0, "the 888 x 1650 panel with a 90 px inset was not reached in the prologue");
        LayerRecord panel = host.Stage.Drawn[found];
        Assert.Equal(new short[] { 160, -285, 888, 1650 }, panel.Crop);
        Assert.Equal(90, panel.A16);
        Assert.Equal(0, panel.A17);
        host.SetSkipLatch(false);
        var list = new DisplayList(8192);
        list.Reset(++n);
        host.Step(new InputFrame { Frame = n }, list);

        DrawCmd[] soft = list.Commands.ToArray().Where(c => c.Target == StageRuntime.SoftTarget && c.Kind != DrawKind.Clear).ToArray();
        Assert.NotEmpty(soft);
        DrawCmd fill = soft.Single(c => c.Kind == DrawKind.FillQuad);
        // 160 + 90 .. 160 + 888 - 90 = 958 horizontally (r = 45 units = 90 px); the panel runs past the top and the bottom of the frame (-285 .. 1365): no vertical fade
        Assert.Equal((250f, -285f, 958f, 1365f), (fill.V0.X, fill.V0.Y, fill.V3.X, fill.V3.Y));
        Assert.Equal(2, soft.Count(c => c.Kind == DrawKind.TexturedQuad));
        // the 90 px band picks the 128-texel gradation (BORDER_PARTS child 3) in the stage's radius classes 10, 32, 40, 64, 128, 256
        Assert.All(soft.Where(c => c.Kind == DrawKind.TexturedQuad), c => Assert.Equal(3, c.Texture.C));
        DrawCmd[] windowQuads = list.Commands.ToArray().Where(c => c.Target == 0 && c.Fx.Mask == StageRuntime.SoftTarget).ToArray();
        Assert.NotEmpty(windowQuads);
        Assert.All(windowQuads, c => Assert.Equal(new PixelRect(160, 0, 888, 1080), c.Clip));
        output.WriteLine($"layer {found}: crop {string.Join(',', panel.Crop)}, a16 {panel.A16}, a17 {panel.A17}; {soft.Length} mask quads, {windowQuads.Length} masked picture quads, window {windowQuads[0].Clip}");
    }
}
