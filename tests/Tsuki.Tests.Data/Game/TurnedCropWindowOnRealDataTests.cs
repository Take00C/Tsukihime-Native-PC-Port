using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Stage;

namespace Tsuki.Tests.Data.Game;

/// <summary>
/// A crop window turned a quarter by its <c>_STRR</c> angle on real data: 02_00_ARC02_1D (scene 334) sets layer 38 to crop (-404, -1492, 1120, 3100), inset 154,
/// mode 0 (STCH at 0x5CE) and angle 9000 (STRR at 0x616). The PS4 turns its mask by g = 16383 (engine trace s334, 0x4CBBA): the tall strip lies across the
/// screen as a band, its feathered side along the bottom.
/// </summary>
public sealed class TurnedCropWindowOnRealDataTests(ITestOutputHelper output)
{
    [GameDataFact]
    public void Scene334sTallWindowIsTurnedIntoABandAcrossTheScreen()
    {
        GameHost host = FirstChoiceRig.NewHost(Language.En);
        host.NewGame(334);
        var list = new DisplayList(16384);
        long n = 0;
        for (int i = 0; i < 30_000 && host.Phase == GamePhase.Running; i++)
        {
            list.Reset(++n);
            host.Step(new InputFrame { Frame = n, Pressed = (i & 1) == 0 ? PadButtons.Decide : PadButtons.None }, list);
            LayerRecord r = host.Stage.Drawn[38];
            if (r.Used == 0 || r.Angle != 9000 || !host.Stage.DrawnLayers.Contains(38) || MathF.Min(1f, r.Alpha + r.AlphaCur) <= 0f)
            {
                continue;
            }

            Assert.Equal(new short[] { -404, -1492, 1120, 3100 }, r.Crop);
            Assert.Equal((154, 0), (r.A16, (int)r.A17));
            DrawCmd[] cmds = list.Commands.ToArray();
            DrawCmd fill = cmds.Single(c => c.Target == StageRuntime.SoftTarget && c.Kind == DrawKind.FillQuad
                && MathF.Abs(((c.V0.X + c.V3.X) / 2f) - 156f) < 0.5f && MathF.Abs(((c.V0.Y + c.V3.Y) / 2f) - 58f) < 0.5f);
            output.WriteLine($"frame {host.Frame}: inner ({fill.V0.X:F1},{fill.V0.Y:F1}) ({fill.V1.X:F1},{fill.V1.Y:F1}) ({fill.V3.X:F1},{fill.V3.Y:F1}) ({fill.V2.X:F1},{fill.V2.Y:F1})");

            // the crop shrunk by its fade (154 px) is x -250..562, y -1338..1454 about (156, 58); a quarter turn lays it across: x -1240..1552, y -348..464
            Assert.InRange(fill.V0.X, 1551.8f, 1552.2f);
            Assert.InRange(fill.V0.Y, -348.3f, -347.7f);
            Assert.InRange(fill.V3.X, -1240.2f, -1239.8f);
            Assert.InRange(fill.V3.Y, 463.7f, 464.3f);
            int at = Array.IndexOf(cmds, fill);                    // the eight feather pieces (all four sides, radius class 128: child 4) come just before it
            Assert.All(cmds[(at - 8)..at], c => Assert.Equal((StageRuntime.SoftTarget, DrawKind.TexturedQuad, 4), (c.Target, c.Kind, c.Texture.C)));
            return;
        }

        Assert.Fail("layer 38 of scene 334 was not drawn with its angle");
    }
}
