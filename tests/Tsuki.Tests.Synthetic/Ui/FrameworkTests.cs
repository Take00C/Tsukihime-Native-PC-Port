using Tsuki.Engine.Contracts;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.Ui;

public sealed class FrameworkTests
{
    private sealed class TwoByTwoData
    {
        // a 300 x 200 sheet in four tiles; each tile stores a 1-texel apron around its 150 x 100 displayed part
        public static Picture Picture() => new(300, 200,
        [
            Tile(0, 0, 0), Tile(1, 150, 0), Tile(2, 0, 100), Tile(3, 150, 100),
        ]);

        private static PictureTile Tile(int i, int x, int y) =>
            new(i, TileFlags.MayBeTransparent, 152, 102, new PixelRect(1, 1, 150, 100), x, y, ReadOnlyMemory<byte>.Empty);
    }

    [Fact]
    public void GeometrySkipsEmptyTilesAndKeepsTheApron()
    {
        var empty = new PictureTile(1, TileFlags.Empty, 152, 102, new PixelRect(1, 1, 150, 100), 150, 0, ReadOnlyMemory<byte>.Empty);
        var pic = new Picture(300, 200, [TwoByTwoData.Picture().Tiles[0], empty]);
        SheetGeometry g = SheetGeometry.FromPicture(pic);
        Assert.Equal(300, g.Width);
        SheetTile t = Assert.Single(g.Tiles);
        Assert.Equal((0, 0, 150, 100, 1, 1, 152, 102), (t.X, t.Y, t.W, t.H, t.SourceX, t.SourceY, t.StoredWidth, t.StoredHeight));
    }

    [Fact]
    public void ASpriteAcrossTilesIsSplitWithTheRightTextureCoordinates()
    {
        var painter = new UiPainter(new UiAssets(UiTestKit.Data(), Language.En));
        var list = new DisplayList(16);
        list.Reset(0);
        painter.Begin(list);

        // the test data serves one 4000 x 4000 tile: a sprite inside it is one quad
        Assert.True(painter.Sprite(new UiSheet(UiSet.MenuParts, 0), new RectF(100, 200, 50, 40), new RectF(10, 20, 100, 80), 0.5f));
        Assert.Equal(1, list.Count);
        DrawCmd c = list.Commands[0];
        Assert.Equal(TextureKind.UiTile, c.Texture.Kind);
        Assert.Equal((int)UiSet.MenuParts, c.Texture.A);
        Assert.Equal(10f, c.V0.X);
        Assert.Equal(110f, c.V1.X);
        Assert.Equal(20f, c.V0.Y);
        Assert.Equal(100f, c.V2.Y);
        Assert.Equal(100f / 4000f, c.V0.U, 5);
        Assert.Equal(150f / 4000f, c.V1.U, 5);
        Assert.Equal(200f / 4000f, c.V0.V, 5);
        Assert.Equal(240f / 4000f, c.V2.V, 5);
        Assert.Equal(128u, c.V0.Rgba >> 24);
    }

    [Fact]
    public void TheTileSplitMapsEachPartOfTheSourceToItsOwnTile()
    {
        var painter = new UiPainter(new UiAssets(UiTestKit.Data(TwoByTwoData.Picture()), Language.En));
        var list = new DisplayList(16);
        list.Reset(0);
        painter.Begin(list);
        // the source (100, 50)-(200, 150) overlaps all four tiles: each contributes a 50 x 50 piece, stretched 2x into (0, 0)-(200, 200)
        Assert.True(painter.Sprite(new UiSheet(UiSet.SaveParts, 8), new RectF(100, 50, 100, 100), new RectF(0, 0, 200, 200)));
        Assert.Equal(4, list.Count);
        DrawCmd top = list.Commands.ToArray().Single(c => c.Texture.D == 0);
        // tile 0 shows sheet x 0..150, y 0..100; its stored texture is 152 x 102 with the displayed part at (1, 1)
        Assert.Equal((0f, 0f, 100f, 100f), (top.V0.X, top.V0.Y, top.V1.X, top.V2.Y));
        Assert.Equal((1f + 100f) / 152f, top.V0.U, 5);
        Assert.Equal((1f + 150f) / 152f, top.V1.U, 5);
        Assert.Equal((1f + 50f) / 102f, top.V0.V, 5);
        Assert.Equal((1f + 100f) / 102f, top.V2.V, 5);
        DrawCmd bottomRight = list.Commands.ToArray().Single(c => c.Texture.D == 3);
        Assert.Equal((100f, 100f, 200f, 200f), (bottomRight.V0.X, bottomRight.V0.Y, bottomRight.V1.X, bottomRight.V2.Y));
        Assert.Equal(1f / 152f, bottomRight.V0.U, 5);
        Assert.Equal(1f / 102f, bottomRight.V0.V, 5);
    }

    [Fact]
    public void AlphaAndClipScopesNestAndRestore()
    {
        var painter = new UiPainter(new UiAssets(UiTestKit.Data(), Language.En));
        var list = new DisplayList(16);
        list.Reset(0);
        painter.Begin(list);
        using (painter.WithAlpha(0.5f))
        using (painter.WithClip(new RectF(10, 10, 100, 100)))
        {
            using (painter.WithAlpha(0.5f))
            using (painter.WithClip(new RectF(50, 50, 200, 200)))
            {
                painter.Fill(new RectF(0, 0, 500, 500), 1, 0, 0, 1f);
            }

            painter.Fill(new RectF(0, 0, 500, 500), 0, 1, 0, 1f);
        }

        painter.Fill(new RectF(0, 0, 10, 10), 0, 0, 1, 1f);
        Assert.Equal(3, list.Count);
        Assert.Equal(new PixelRect(50, 50, 60, 60), list.Commands[0].Clip);
        Assert.Equal(64u, list.Commands[0].V0.Rgba >> 24);
        Assert.Equal(new PixelRect(10, 10, 100, 100), list.Commands[1].Clip);
        Assert.Equal(128u, list.Commands[1].V0.Rgba >> 24);
        Assert.Null(list.Commands[2].Clip);
        Assert.Equal(255u, list.Commands[2].V0.Rgba >> 24);
        Assert.Equal(0x00FF0000u, list.Commands[2].V0.Rgba & 0xFFFFFF);
    }

    [Fact]
    public void FillsAreSkippedAtAlphaZeroAndAdditiveBlendsCarryTheirEffect()
    {
        var painter = new UiPainter(new UiAssets(UiTestKit.Data(), Language.En));
        var list = new DisplayList(16);
        list.Reset(0);
        painter.Begin(list);
        painter.Fill(new RectF(0, 0, 10, 10), 1, 1, 1, 0f);
        painter.Sprite(new UiSheet(UiSet.TitleParts, 4), new RectF(0, 0, 8, 8), new RectF(0, 0, 8, 8), 1f, UiBlend.Add);
        painter.Sprite(new UiSheet(UiSet.TitleParts, 4), new RectF(0, 0, 8, 8), new RectF(0, 0, 8, 8), 1f, UiBlend.PsAdd);
        Assert.Equal(2, list.Count);
        Assert.Equal((52, 2), (list.Commands[0].EffectWord, (int)list.Commands[0].BlendState));
        Assert.Equal((11, 1), (list.Commands[1].EffectWord, (int)list.Commands[1].BlendState));
    }

    [Fact]
    public void FlippedSpritesMirrorTheTextureCoordinates()
    {
        var painter = new UiPainter(new UiAssets(UiTestKit.Data(), Language.En));
        var list = new DisplayList(16);
        list.Reset(0);
        painter.Begin(list);
        painter.Sprite(new UiSheet(UiSet.TitleParts, 5), new RectF(0, 0, 40, 40), new RectF(100, 100, 40, 40), 1f, UiBlend.Alpha, flipX: true);
        DrawCmd c = list.Commands[0];
        Assert.True(c.V0.U > c.V1.U);
        Assert.Equal(100f, c.V0.X);
        Assert.Equal(140f, c.V1.X);
    }

    [Fact]
    public void ItemListMovesOverEnabledItemsAndWraps()
    {
        var items = new UiItemList();
        items.Add(new RectF(0, 0, 10, 10), 10);
        items.Add(new RectF(0, 10, 10, 10), 11, enabled: false);
        items.Add(new RectF(0, 20, 10, 10), 12);
        Assert.True(items.Move(1));
        Assert.Equal(0, items.Cursor);
        Assert.True(items.Move(1));
        Assert.Equal(2, items.Cursor);
        Assert.True(items.Move(1));
        Assert.Equal(0, items.Cursor);
        Assert.True(items.Move(-1));
        Assert.Equal(2, items.Cursor);
        Assert.False(items.Move(1, wrap: false));
        Assert.Equal(12, items.FocusedId);
        items.Cursor = -1;
        Assert.True(items.Move(-1));
        Assert.Equal(2, items.Cursor);
    }

    [Fact]
    public void ItemListHoverNeedsAMovedPointerAndSkipsDisabledItems()
    {
        var items = new UiItemList();
        items.Add(new RectF(0, 0, 100, 100), 1);
        items.Add(new RectF(0, 100, 100, 100), 2, enabled: false);
        Assert.False(items.Hover(new UiInput { PointerActive = true, PointerX = 50, PointerY = 50, PointerMoved = false }));
        Assert.True(items.Hover(new UiInput { PointerActive = true, PointerX = 50, PointerY = 50, PointerMoved = true }));
        Assert.Equal(0, items.Cursor);
        Assert.False(items.Hover(new UiInput { PointerActive = true, PointerX = 50, PointerY = 150, PointerMoved = true }));
        Assert.Equal(0, items.Cursor);
        Assert.Equal(-1, items.Clicked(new UiInput { PointerActive = true, PointerX = 50, PointerY = 50 }));
        Assert.Equal(0, items.Clicked(new UiInput { PointerActive = true, PointerX = 50, PointerY = 50, Click = true }));
    }

    [Fact]
    public void InputMapsPadAndPointerToMenuActions()
    {
        var f = new InputFrame
        {
            Pressed = PadButtons.Decide | PadButtons.Square,
            Repeated = PadButtons.Decide | PadButtons.Down,
            Held = PadButtons.Decide,
            Pointer = new PointerState(320, 224, true, PointerButtons.None, PointerButtons.None, 1),
        };
        UiInput i = UiInput.From(f, new PointerState(0, 0, true, PointerButtons.None, PointerButtons.None, 0));
        Assert.True(i.Decide);
        Assert.True(i.Down);
        Assert.False(i.DownPressed);
        Assert.False(i.Cancel);
        Assert.True(i.Square);
        Assert.True(i.DecideHeld);
        Assert.True(i.Any);
        Assert.True(i.PointerMoved);
        Assert.Equal(1, i.Wheel);
        Assert.Equal(960f + 1.5f, i.PointerX, 3);
        Assert.Equal((224f + 0.5f) * UiMetrics.UnitY, i.PointerY, 3);

        UiInput click = UiInput.From(UiInput.PointerAt(100, 100, click: true), default);
        Assert.True(click.Click);
        Assert.True(click.Decide);
        Assert.False(click.RightClick);
        UiInput right = UiInput.From(new InputFrame { Pointer = new PointerState(1, 1, true, PointerButtons.Right, PointerButtons.Right, 0) }, default);
        Assert.True(right.Cancel);
        Assert.True(right.RightClick);
    }

    [Fact]
    public void ThePointerIsKeptToThePixelAndTheReleaseOfTheLeftButtonIsSeen()
    {
        UiInput down = UiInput.From(UiInput.PointerFrame(1855.3f, 401.7f, PointerButtons.Left, PointerButtons.Left), default);
        Assert.Equal(1855.3f, down.PointerX);
        Assert.Equal(401.7f, down.PointerY);
        Assert.True(down.Click);
        Assert.False(down.PointerReleased);
        PointerState held = UiInput.PointerFrame(1855.3f, 401.7f, PointerButtons.Left, PointerButtons.Left).Pointer;
        UiInput up = UiInput.From(UiInput.PointerFrame(1855.3f, 402.9f, PointerButtons.None, PointerButtons.None), held);
        Assert.True(up.PointerReleased);
        Assert.True(up.PointerMoved);                                           // a pixel is a move even inside one 640 x 448 unit
        UiInput still = UiInput.From(UiInput.PointerFrame(1855.3f, 402.9f, PointerButtons.None, PointerButtons.None), up.PointerX > 0 ? UiInput.PointerFrame(1855.3f, 402.9f, PointerButtons.None, PointerButtons.None).Pointer : default);
        Assert.False(still.PointerMoved);
        Assert.False(still.PointerReleased);
    }

    [Fact]
    public void StickPressesMoveLikeTheDpad()
    {
        UiInput i = UiInput.From(new InputFrame { Pressed = PadButtons.StickUp, Repeated = PadButtons.StickUp }, default);
        Assert.True(i.Up);
        Assert.True(i.UpPressed);
    }

    [Fact]
    public void BackdropFadesStripsAndSparksAndCutsAtZero()
    {
        var b = new UiBackdrop(new UiRandom(3));
        Assert.False(b.Visible);
        b.Fade(0.05f);
        for (int i = 0; i < 70; i++)
        {
            b.Update();
        }

        Assert.True(b.Visible);
        Assert.Equal(1f, b.A, 3);
        Assert.True(b.MoviePaused);
        Assert.Equal(1f, b.T, 3);
        b.Fade(-0.5f);
        b.Update();
        b.Update();
        Assert.False(b.Visible);
        Assert.False(b.MoviePaused);
        b.Fade(0.05f);
        b.Update();
        b.Fade(0f);
        Assert.Equal(1f, b.A);
    }

    [Fact]
    public void BackdropResetHidesItAtOnceAndStopsItsFade()
    {
        var b = new UiBackdrop(new UiRandom(3));
        b.Fade(0.05f);
        for (int i = 0; i < 30; i++)
        {
            b.Update();
        }

        Assert.True(b.Visible);
        b.Reset();
        Assert.False(b.Visible);
        Assert.Equal(0f, b.Speed);
        Assert.Equal(0f, b.T);
        Assert.False(b.MoviePaused);
        for (int i = 0; i < 30; i++)
        {
            b.Update();
        }

        Assert.False(b.Visible);                              // nothing is still fading in
        b.Fade(0f);
        Assert.Equal(0f, b.A);                                // and a cut to "where it was heading" has nowhere to go
    }

    [Fact]
    public void EasingsAndTheTriangleWave()
    {
        Assert.Equal(0.75f, UiEase.Out(0.5f), 5);
        Assert.Equal(0.25f, UiEase.In(0.5f), 5);
        Assert.Equal(0f, UiEase.Triangle(0f), 5);
        Assert.Equal(1f, UiEase.Triangle(1f), 5);
        Assert.Equal(0f, UiEase.Triangle(2f), 5);
        Assert.Equal(0.5f, UiEase.Triangle(3.5f), 5);
    }

    [Fact]
    public void TheMessageIdOfAKnifeSysmesNumberShiftsFrom217()
    {
        Assert.Equal(216, UiAssets.Sysmes(216));
        Assert.Equal(218, UiAssets.Sysmes(217));
        Assert.Equal(396, UiAssets.Sysmes(395));
    }

    private sealed class PlainScreen : UiScreen
    {
        public override void Step(in UiInput input)
        {
        }

        public override void Draw(UiPainter painter)
        {
        }
    }

    // the gallery's replay hand-off: the screen that leaves clears every screen from its OnClosed
    private sealed class ClearingScreen : UiScreen
    {
        public int Closed;

        public override void OnClosed()
        {
            Closed++;
            Root.Clear();
        }

        public override void Step(in UiInput input)
        {
        }

        public override void Draw(UiPainter painter)
        {
        }
    }

    [Fact]
    public void AScreenThatClearsTheStackWhenItLeavesDoesNotBreakTheStepOverTheScreensBelowIt()
    {
        // package K15: with the title under the gallery, the first replay started from the gallery ended the app (index out of range in UiRoot.Step)
        var services = new PreviewServices();
        UiRoot root = UiTestKit.Root(services);
        var under = new PlainScreen();
        var clearing = new ClearingScreen();
        root.Push(under);
        root.Push(clearing);
        clearing.Close();
        UiTestKit.Run(root, 2);
        Assert.Equal(1, clearing.Closed);
        Assert.False(root.Active);
        UiTestKit.Run(root, 2);                                 // and the root still steps
    }
}
