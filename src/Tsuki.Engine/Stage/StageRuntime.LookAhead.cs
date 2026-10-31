using System.Globalization;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Vm;
using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

public sealed partial class StageRuntime
{
    /// <summary>
    /// Starts a look-ahead scan (package K8): the statements after the interpreter's position are fed to it, and it names the picture tiles, with their colour
    /// grade, that the layers will hold at the next commits. The scan keeps its own picture and grade per layer and starts from the script-side scene state.
    /// </summary>
    public LookAheadScan BeginLookAhead() => new(this);

    /// <summary>The prediction of the pictures a run of statements will put on the layers (<see cref="BeginLookAhead"/>).</summary>
    public sealed class LookAheadScan
    {
        private readonly StageRuntime _stage;
        private readonly int[] _pic = new int[LayerCount];
        private readonly (int Mode, int A, int B, int Rgb)[] _fx = new (int, int, int, int)[LayerCount];
        private readonly bool[] _pending = new bool[LayerCount];

        internal LookAheadScan(StageRuntime stage)
        {
            _stage = stage;
            for (int l = 0; l < LayerCount; l++)
            {
                _pic[l] = -2;                       // -2: not touched by the scan, the scene state holds the layer's picture
            }
        }

        private int PicOf(int l) => _pic[l] != -2 ? _pic[l] : _stage.Scene.GetPic(l);

        private (int Mode, int A, int B, int Rgb) FxOf(int l) =>
            _pic[l] != -2 ? _fx[l] : (_stage.Scene.GetFxMode(l), _stage.Scene.GetFxA(l), _stage.Scene.GetFxB(l), 0xFFFFFF);

        /// <summary>Reads one statement: <c>_STCH</c> / <c>_SCH2</c> put a picture on a layer, <c>_STEC</c> / <c>_SNX2</c> / <c>_SNX3</c> grade it, <c>_STCC</c> clears it.</summary>
        /// <returns>True when the statement commits the scene (<c>_FADS</c>): the pending pictures should be named now.</returns>
        public bool Feed(in Statement st)
        {
            if (st.Name.First != (byte)'S' && st.Name.First != (byte)'F')
            {
                return false;
            }

            switch (st.Name.ToString())
            {
                case "STCH" or "SCH2":
                    Put(st);
                    break;
                case "STEC":
                    Grade(st, 0);
                    break;
                case "SNX2":
                    Grade(st, 1);
                    break;
                case "SNX3":
                    Grade(st, 2);
                    break;
                case "STCC":
                {
                    var a = new StageArgs(st);
                    int l = a.Has(0) ? a.Expr(0) : 0;
                    for (int k = 0; k < LayerCount; k++)
                    {
                        if (l < 0 || k == l)
                        {
                            _pic[k] = -1;
                            _pending[k] = false;
                        }
                    }

                    break;
                }

                case "FADS":
                    return true;
            }

            return false;
        }

        private void Put(in Statement st)
        {
            var a = new StageArgs(st);
            int l = a.Has(0) ? a.Expr(0) : -1;
            string name = a.Str(1);
            if (l is < 0 or > 39)
            {
                return;
            }

            if (name.Length == 0)
            {
                _pic[l] = -1;
                _pending[l] = false;
                return;
            }

            int src = a.Has(18) ? a.Expr(18) : -1;
            bool hasSrc = src is >= 0 and <= 39;
            int h = 0;
            int v = 0;
            if (hasSrc && PicOf(src) is >= 0 and var sp && sp < _stage._catalog.Count)
            {
                (h, v) = PictureNames.VariantDigitsStch(_stage._catalog.Name(sp));
            }

            if (a.Has(9))
            {
                h = a.Expr(9);
            }

            if (a.Has(10))
            {
                v = a.Expr(10);
            }

            if ((h | v) != 0)
            {
                name += "_" + (h > 0 ? h % 100 : 0).ToString("00", CultureInfo.InvariantCulture) + "_" + (v > 0 ? v % 100 : 0).ToString("00", CultureInfo.InvariantCulture);
            }

            int pic = _stage._catalog.Lookup(name);
            (int, int, int, int) fx = hasSrc ? FxOf(src) : (0, 0, 0, 0xFFFFFF);
            if (!hasSrc && _stage.DefaultColourEffect > 0 && (pic < 0 || (_stage._catalog.Info(pic).DrawFlags & 1) == 0))
            {
                fx = (_stage.DefaultColourEffect, 0, 0, 0xFFFFFF);
            }

            _pic[l] = pic;
            _fx[l] = fx;
            _pending[l] = pic >= 0;
        }

        private void Grade(in Statement st, int which)
        {
            var a = new StageArgs(st);
            int l = a.Has(0) ? a.Expr(0) : -1;
            if (l is < 0 or > 39)
            {
                return;
            }

            if (_pic[l] == -2)
            {
                (int m0, int a0, int b0, int c0) = FxOf(l);
                _pic[l] = _stage.Scene.GetPic(l);           // the layer now has a simulated state: start it from the scene
                _fx[l] = (m0, a0, b0, c0);
            }

            (int mode, int fa, int fb, int rgb) = _fx[l];
            switch (which)
            {
                case 0:
                {
                    int m = a.Has(1) ? a.Expr(1) : 0;
                    if (m is < 0 or > 81)
                    {
                        return;
                    }

                    mode = m;
                    rgb = m == 1 ? Strtol0(a.Str(2)) & 0xFFFFFF : 0xFFFFFF;
                    break;
                }

                case 1:
                    fa = I16(a.Expr(1));
                    break;
                default:
                    fb = I8(a.Expr(1));
                    break;
            }

            _fx[l] = (mode, fa, fb, rgb);
        }

        /// <summary>
        /// Appends the tiles of the pictures that were put on layers since the last call, each with the colour grade its layer holds now, and forgets them.
        /// Pairs (picture, grade) in <paramref name="done"/> are not named again.
        /// </summary>
        /// <param name="into">Receives the tile textures.</param>
        /// <param name="done">(picture, grade key) pairs already named.</param>
        public void Flush(List<TextureRef> into, HashSet<(int Pic, int Key)> done)
        {
            for (int l = 0; l < LayerCount; l++)
            {
                if (!_pending[l])
                {
                    continue;
                }

                _pending[l] = false;
                int pic = _pic[l];
                (int mode, int fa, int fb, int rgb) = _fx[l];
                int key = GradeKeys.Intern(ColourGrade.From(mode, fa, fb, rgb, _stage.Scene.ColourMode));
                if (pic < 0 || _stage.Layouts is null || !done.Add((pic, key)) || _stage.Layouts.Get(pic, _stage.Language) is not { } layout)
                {
                    continue;
                }

                foreach (PictureFileLayout file in layout.Files)
                {
                    foreach (TileQuad t in file.Tiles)
                    {
                        into.Add(TextureRef.ForPictureTile(file.Archive, file.Entry, t.Tile, key));
                    }
                }
            }
        }
    }
}
