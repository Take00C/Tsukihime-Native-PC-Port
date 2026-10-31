using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Game.Effects;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Text;

namespace Tsuki.Tests.Synthetic.Effects;

/// <summary>
/// The screen effects of the interpreter (package K7): the focus lines and falling notes (<c>_FCLN</c>), the ghost channel split (<c>_FADE(40)</c>) and the
/// decoration of the choice screen (system_flow.md 5.3). knife's own unit tests (game/focus.rs, game/ghost.rs) are the model for the first two.
/// </summary>
public sealed class GameEffectTests
{
    // effect 18 (index 14) and effect 6 (index 2) of the eboot table, which knife's tests use
    private sealed class Tables : IEffectTables
    {
        private static readonly FocusKey None = new(-1, -1, -1, -1, -1, -1, -1, -1, -1);

        public int FocusEffects => 30;

        public IReadOnlyList<FocusNote> FocusNotes { get; } =
        [
            new FocusNote(24, Enumerable.Range(0, 24).ToArray(), 563, 187, 4, -0.4f, 4f, 0.005f, 0.005f, 0.001f, 450),
            new FocusNote(20, Enumerable.Range(0, 20).ToArray(), 225, 112, 4, -0.4f, 4f, 0.005f, 0.005f, 0.0002f, 450),
            new FocusNote(16, Enumerable.Range(0, 16).ToArray(), 225, 150, 4, -0.4f, 4f, 0.005f, 0.005f, 0.0015f, 450),
        ];

        public FocusKey FocusKey(int effect, int key) => (effect, key) switch
        {
            (14, 0) => new FocusKey(0, 878, 279, 242, 329, 10, 15, 0, 50),
            (14, 1) => new FocusKey(200, 878, 279, 242, 329, 10, 15, 128, 50),
            (2, 0) => new FocusKey(0, 806, 360, 288, 479, 40, 80, 255, 50),
            (2, 1) => new FocusKey(1100, 734, 417, 323, 407, 40, 80, 255, 50),
            (2, 2) => new FocusKey(1200, 734, 417, 323, 407, 40, 80, 255, 50),
            _ => None,
        };
    }

    [Fact]
    public void TheFocusLinesFollowTheEngineStepOfEffect18()
    {
        var f = new FocusEffect(new Tables(), new Ps4Rand());
        f.Start(18, 0);
        f.Step(1);
        Assert.Equal((FocusMode.FadingIn, 1f), (f.Mode, f.Level));      // level 1 after the first frame, on after the second
        f.Step(1);
        Assert.Equal(FocusMode.On, f.Mode);
        Assert.Equal(0, f.ParticleCount);
        f.Step(1);                                                       // the first roll comes after 50 ms (3 frames)
        Assert.InRange(f.ParticleCount, 10, 14);
        var list = new DisplayList();
        list.Reset(0);
        f.Draw(list);
        Assert.Equal(f.ParticleCount, list.Count);
        DrawCmd line = list.Commands[0];
        Assert.Equal(TextureRef.ForUiTile(UiSet.EffectParts, Language.Ja, 3, -1), line.Texture);
        // alpha: 18 fades from 0 to 128 over the first 200 ms, 50 ms in
        Assert.Equal((byte)MathF.Round(50f / 200f * 128f / 255f * 255f), (byte)(line.V0.Rgba >> 24));
        // a line is 960 x 2 texels of the 1920 x 128 strip
        Assert.Equal((850f / 1920f, 1810f / 1920f), (line.V0.U, line.V1.U));      // effect 18 cuts its lines at texel (850, 120)
        Assert.Equal((120f / 128f, 122f / 128f), (line.V0.V, line.V2.V));

        f.Start(18, 0);                                                  // restarting the running effect changes nothing
        Assert.Equal(FocusMode.On, f.Mode);
        f.Start(0, 120);                                                 // FCSP(2000) fades out over 120 frames
        for (int i = 0; i < 119; i++)
        {
            f.Step(1);
        }

        Assert.Equal(FocusMode.FadingOut, f.Mode);
        f.Step(1);
        f.Step(1);
        Assert.Equal(FocusMode.Off, f.Mode);
        list.Reset(0);
        f.Draw(list);
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void AFocusEffectSwitchesKeyframesAndHalvesItsCountBelowEffect9()
    {
        var f = new FocusEffect(new Tables(), new Ps4Rand());
        f.Start(6, 0);
        for (int i = 0; i < 66; i++)
        {
            f.Step(1);
        }

        Assert.InRange(f.ParticleCount, 20, 39);                        // 1100 ms: keyframe 1; 40..79 lines halved
        Assert.Equal(1, f.CurrentKey);
        for (int i = 0; i < 6; i++)
        {
            f.Step(1);
        }

        Assert.Equal(2, f.CurrentKey);
    }

    [Fact]
    public void TheFallingNotesDropAboveTheEdgeAndLeaveAtTheFloor()
    {
        var f = new FocusEffect(new Tables(), new Ps4Rand(7));
        f.Start(3, 0);
        for (int i = 0; i < 2000; i++)
        {
            f.Step(1);
        }

        Assert.InRange(f.ParticleCount, 1, 50);
        // no focus lines without tables
        var none = new FocusEffect(null, new Ps4Rand());
        none.Start(7, 0);
        for (int i = 0; i < 100; i++)
        {
            none.Step(1);
        }

        Assert.Equal(0, none.ParticleCount);
    }

    [Fact]
    public void TheGhostAmplitudeAndAngleFollowTheEngineStep()
    {
        var g = new GhostEffect();
        g.Start(0, 0, reset: true);
        Assert.Equal((0f, 0.62f, 30f), (g.Alpha, g.Amplitude, g.Angle));
        g.Step(6, 0, 0);
        // the first 6-frame step: phase 100, angle 27, amplitude from the curve plus the wobble
        Assert.Equal(27f, g.Angle);
        float curve = 0.5f - (MathF.Cos(100f * MathF.PI / 1666f) * 0.5f) + (2f * MathF.Sin(100f * MathF.PI / 3333f));
        Assert.Equal(3.1f * ((curve * 80f) + 20f) / 100f, g.Amplitude, 4);
        Assert.Equal(0.1f, g.Alpha, 2);
        g.Step(6 * 20, 0, 0);
        Assert.InRange(g.Angle, -30f, 30f);                              // the angle reflects at the ends
        g.SetMode(5, 0, 0);
        g.Step(200, 0, 0);
        Assert.Equal(0, g.Mode);
        Assert.False(g.Active);
    }

    [Fact]
    public void TheGhostDrawsThreeShiftedChannelsAndOverlaysThemOnTheFrame()
    {
        var g = new GhostEffect();
        g.Start(-90, 0, reset: true);
        g.Step(30, -90, 0);
        var list = new DisplayList();
        list.Reset(0);
        g.Draw(list, null);
        DrawCmd[] cmds = list.Commands.ToArray();
        Assert.Equal(DrawKind.Capture, cmds[0].Kind);
        Assert.Equal((0, GhostEffect.SourceSurface), ((int)cmds[0].Target, cmds[0].Texture.A));
        Assert.Equal(1 + (3 * 4) + 1 + 1, cmds.Length);
        // red: v = -90 + 255 = 165 in the red byte of the first channel pair
        Assert.Equal(0xA5u, cmds[1].V0.Rgba & 0xFF);
        Assert.Equal(0xA5u << 8, cmds[5].V0.Rgba & 0xFFFF);              // green byte of the second channel
        Assert.Equal(0xA5u << 16, cmds[9].V0.Rgba & 0xFFFFFF);           // blue byte of the third
        Assert.Equal(0xFFu, cmds[1].V0.Rgba >> 24);
        Assert.Equal(GhostEffect.ChannelTarget, cmds[1].Target);
        Assert.Equal(GhostEffect.ChannelTarget, cmds[2].Target);
        Assert.NotEqual(cmds[1].V0.X, cmds[2].V0.X);                     // the second draw is shifted
        Assert.Equal(0, cmds[4].BlendState);                              // red replaces the sum
        Assert.Equal(2, cmds[8].BlendState);                              // green and blue add
        Assert.Equal(2, cmds[12].BlendState);
        DrawCmd overlay = cmds[^1];
        Assert.Equal((22, 1, (byte)0), (overlay.EffectWord, (int)overlay.BlendState, overlay.Target));
        Assert.Equal(TextureRef.ForCapture(GhostEffect.SumSurface), overlay.Texture);
        Assert.Equal((byte)MathF.Round(g.Alpha * 255f), (byte)(overlay.V0.Rgba >> 24));

        list.Reset(0);
        new GhostEffect().Draw(list, null);
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void TheChoiceCursorLineGetsTheGlowTheUnderlineAndTheFlare()
    {
        var glow = new ChoiceGlow(child => child switch { 2 => (1455, 48), 4 => (930, 243), 5 => (1920, 1080), _ => (100, 100) });
        var blocks = new List<(int Line, int Colour)>();
        var drawn = new List<(double X, double Y, double A)>();
        var list = new DisplayList();
        list.Reset(0);
        var view = new ChoiceView(2, Cursor: 1, Previous: 0, Alpha: 0.8f, Highlight: 0.5f, Flash: 0f, X0: 87.0, Y0: 49.15, UnitX: 3.0, UnitY: 3.0);
        void Text(TextBlock _, double x, double y, double a) => drawn.Add((x, y, a));
        TextBlock? Glow(int line, int colour)
        {
            blocks.Add((line, colour));
            return TextBlockStub.Instance;
        }

        glow.DrawVignette(list, view);
        DrawCmd vignette = list.Commands[0];
        Assert.Equal((0, 0), (vignette.EffectWord, (int)vignette.V0.X));
        Assert.Equal((1920f, 1080f), (vignette.V3.X, vignette.V3.Y));
        Assert.Equal((byte)MathF.Round(0.8f * 255f), (byte)(vignette.V0.Rgba >> 24));

        glow.DrawLine(list, view, 0, Glow, Text);                        // the previous line: its glow fades out with 1 - h, no underline
        Assert.Equal(8, drawn.Count);
        Assert.Equal([(0, 0x77FF)], blocks.Distinct());
        double g0 = (1 - 0.5) * 0.8 * 192 / 255 / 8;
        Assert.All(drawn, d => Assert.Equal(g0, d.A, 5));
        Assert.Equal(87.0 - 0.9, drawn[0].X, 5);                         // the first of the eight offsets
        Assert.Equal(49.15, drawn[0].Y, 5);
        Assert.Equal(1, list.Count);

        drawn.Clear();
        glow.DrawLine(list, view, 1, Glow, Text);                        // the cursor line: glow with h, then the underline and the flare
        Assert.Equal(8, drawn.Count);
        Assert.Equal(0.5 * 0.8 * 192 / 255 / 8, drawn[0].A, 5);
        Assert.Equal(1 + 2, list.Count);
        DrawCmd underline = list.Commands[1];
        DrawCmd flare = list.Commands[2];
        Assert.Equal((0xB, 1), (underline.EffectWord, (int)underline.BlendState));
        Assert.Equal(1455 * ((0.9 * 0.5) + 0.1), underline.V1.X - underline.V0.X, 2);   // the underline grows with the highlight
        Assert.Equal(48 * 0.31917 * 3.0, underline.V2.Y - underline.V0.Y, 2);
        Assert.Equal((87.0 - 16.3333) * 3.0, underline.V0.X, 2);
        Assert.Equal(930f, flare.V1.X - flare.V0.X, 2);
        Assert.Equal((byte)MathF.Round(0.8f * 1f * 255f), (byte)(flare.V0.Rgba >> 24));   // the pulse starts at 1

        drawn.Clear();
        glow.DrawLine(list, view, 2, Glow, Text);                        // other lines carry nothing
        Assert.Empty(drawn);
        Assert.Equal(3, list.Count);

        // decided: the pale colour word, and the ring, the bands and the lens flare after the lines
        blocks.Clear();
        view = view with { Phase = 3, Flash = 0.5f };
        glow.DrawLine(list, view, 1, Glow, Text);
        Assert.Contains((1, 0xC7FF), blocks);
        int before = list.Count;
        glow.DrawDecided(list, view);
        Assert.Equal(before + 3, list.Count);
        DrawCmd ring = list.Commands[before];
        Assert.Equal(TextureRef.ForUiTile(UiSet.SelectParts, Language.Ja, 3, -1), ring.Texture);
        // the ring is centred on (86, y0 + 18.8308 + 26.02 c) and turned
        double cx = (ring.V0.X + ring.V3.X) / 2.0;
        Assert.Equal(86.0 * 3.0, cx, 2);
        list.Reset(0);
        glow.DrawDecided(list, view with { Phase = 2 });
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void TheFlarePulseBouncesBetweenAThirdAndOne()
    {
        var glow = new ChoiceGlow(_ => (10, 10));
        glow.Tick(1);
        Assert.Equal(1f - 0.0111f, glow.Pulse, 5);
        glow.Tick(80);
        Assert.InRange(glow.Pulse, 0.333f, 1f);
        float min = 1f;
        for (int i = 0; i < 400; i++)
        {
            glow.Tick(1);
            min = MathF.Min(min, glow.Pulse);
        }

        Assert.Equal(0.333f, min, 3);
        glow.OnCursorChange();
        Assert.Equal(1f, glow.Pulse);
    }

    // the glow code only passes the block on to the caller's draw delegate
    private static class TextBlockStub
    {
        public static readonly Tsuki.Engine.Text.TextBlock Instance =
            (Tsuki.Engine.Text.TextBlock)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Tsuki.Engine.Text.TextBlock));
    }
}
