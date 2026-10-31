// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (Banner, draw_banner)
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>
/// The mode banner (docs/formats/archives_ui_movies.md 5.2 DISP_PARTS child 10: Skip, Quicksaving, Autosaving, Auto): <c>t</c> rises 0.04 per frame to 3
/// (the first third pops the banner in through the scale keys), holds, then falls from 1 to 0 replaying the pop backwards (knife <c>Banner</c>, sub_3C970).
/// </summary>
public sealed class UiBanner
{
    // popup_ja rows per banner (x0, y0, x1, y1) in DISP_PARTS child 10 (knife BANNER_SRC): Skip, Quick Save, Auto Save, Auto.
    private static readonly int[][] Rows = [[8, 200, 424, 256], [8, 72, 424, 128], [8, 8, 424, 64], [8, 136, 424, 192]];

    private bool _out;

    internal UiBanner(BannerKind kind)
    {
        Kind = kind;
    }

    /// <summary>Which banner.</summary>
    public BannerKind Kind { get; }

    /// <summary>The animation clock (0..3 rising, 1..0 falling).</summary>
    public float T { get; private set; }

    /// <summary>True once the banner has gone.</summary>
    public bool Done { get; private set; }

    /// <summary>True while the banner is leaving.</summary>
    public bool Leaving => _out;

    /// <summary>A button while the banner holds sends it away (sub_3B480).</summary>
    public void SendAway()
    {
        if (!_out && T >= 1f)
        {
            T = 3f;
        }
    }

    internal void Step()
    {
        if (_out)
        {
            T -= 0.04f;
            if (T < 0f)
            {
                Done = true;
            }
        }
        else
        {
            T += 0.04f;
            if (T > 3f)
            {
                _out = true;
                T = 1f;
            }
        }
    }

    internal void Draw(UiPainter p, UiContext ctx)
    {
        float k = MathF.Min(MathF.Max(T, 0f) * 9f, 9f);
        int i = (int)k;
        float f = k - i;
        IReadOnlyList<(float X, float Y)> keys = ctx.Assets.Tables.BannerKeys;
        (float X, float Y) k0 = keys[Math.Min(i, 9)];
        (float X, float Y) k1 = keys[Math.Min(i + 1, 9)];
        float sx = (k0.X * (1f - f)) + (k1.X * f);
        float sy = (k0.Y * (1f - f)) + (k1.Y * f);
        int[] r = Rows[(int)Kind];
        var src = RectF.FromCorners(r[0], r[1], r[2], r[3]);
        // anchor top-left at (0, 32) of 640 x 448
        p.Sprite(UiSheets.Banner, src, new RectF(0, UiMetrics.Y(32f), src.W * sx, src.H * sy));
    }
}
