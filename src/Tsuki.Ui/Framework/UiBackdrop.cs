// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/fx.rs (MenuBg, phase) and knife/src/ui.rs (draw_menu_layer, STRIPS)
namespace Tsuki.Ui;

/// <summary>
/// The shared submenu backdrop (knife <c>MenuBg</c>, sub_4B880): MENU_PARTS child 9 over the whole screen at alpha <see cref="A"/>, eight cloud strips
/// (children 10..17) easing in from the top and bottom, and sparkles (child 8) drifting upwards. The title, the system menu and the screens opened from them
/// share one instance, so it keeps fading across screen changes. The PS4 draws the strips at the positions of docs/formats/system_flow.md "System menu"
/// (process 0x10, 0x6BF10): x 320 units (283.25 and 276.5 for two strips), y = (q n + 720) * 448 / 720, alpha q * {64, 96, 160, 224, 64, 96, 128, 160} / 255.
/// </summary>
public sealed class UiBackdrop
{
    // (child group, strip, alpha / 255, x centre in px, y centre at full slide in px, slide distance in px): menuBG04_k rise from the bottom edge,
    // menuBG05_k drop from the top edge (anchor: centre). Equal to the PS4 positions above.
    private static readonly (bool Low, int K, float Alpha, float Cx, float Y0, float Dy)[] Strips =
    [
        (true, 0, 64f, 960f, 1080f, -198f), (true, 1, 96f, 960f, 1080f, -160.5f), (true, 2, 160f, 960f, 1080f, -127.5f), (true, 3, 224f, 960f, 1080f, -82.5f),
        (false, 0, 64f, 960f, 0f, 155.25f), (false, 1, 96f, 849.75f, 0f, 117.75f), (false, 2, 128f, 960f, 0f, 84.75f), (false, 3, 160f, 829.5f, 0f, 39.75f),
    ];

    private readonly UiRandom _rng;
    private readonly List<Spark> _sparks = [];
    private float _acc;

    /// <summary>Creates a hidden backdrop.</summary>
    public UiBackdrop(UiRandom rng)
    {
        _rng = rng;
    }

    /// <summary>The backdrop alpha.</summary>
    public float A { get; private set; }

    /// <summary>The alpha step per frame (sub_49630).</summary>
    public float Speed { get; private set; }

    /// <summary>The strip animation 0..1.</summary>
    public float T { get; private set; }

    /// <summary>The strip animation step per frame.</summary>
    public float TSpeed { get; private set; }

    /// <summary>A fade in pauses the title movie, a fade out resumes it (knife <c>movie_paused</c>).</summary>
    public bool MoviePaused { get; private set; }

    /// <summary>True while any of it shows.</summary>
    public bool Visible => A > 0f;

    /// <summary>Starts fading in (speed above 0) or out (below 0); 0 cuts it at once to where it was heading (sub_49630).</summary>
    public void Fade(float speed)
    {
        if (speed == 0f)
        {
            A = Speed < 0f ? 0f : Speed > 0f ? 1f : A;
        }

        Speed = speed;
        if (speed > 0f)
        {
            TSpeed = 1f / 60f;
            MoviePaused = true;
        }
        else if (speed < 0f)
        {
            TSpeed = speed;
            MoviePaused = false;
        }
    }

    /// <summary>
    /// Hides the backdrop at once and stops its fade (package K14): a title that starts fresh, or that must show its logo and menu now, has nothing of an earlier
    /// screen left over. The PS4 does the same through its overlay rate (system state machine 0x68B7F, title re-entry), only slower (see <c>TitleScreen</c>).
    /// </summary>
    public void Reset()
    {
        A = 0f;
        Speed = 0f;
        T = 0f;
        TSpeed = 0f;
        MoviePaused = false;
        _acc = 0f;
        _sparks.Clear();
    }

    /// <summary>Steps <paramref name="frames"/> 60 Hz frames.</summary>
    public void Update(float frames = 1f)
    {
        A = Math.Clamp(A + (Speed * frames), 0f, 1f);
        T = Math.Clamp(T + (TSpeed * frames), 0f, 1f);
        if (A <= 0f)
        {
            _sparks.Clear();
            return;
        }

        // sub_4C190: one sparkle per frame (100 slots)
        _acc += frames;
        while (_acc >= 1f)
        {
            _acc -= 1f;
            if (_sparks.Count >= 100)
            {
                continue;
            }

            float scale = (_rng.Next() * 0.2f) + 0.1f;
            float life = MathF.Floor((_rng.Next() * 150f) + 120f);
            float alpha = _rng.Range(0.25f, 0.5f);
            float x = _rng.Next() * 960f;
            float y = _rng.Next() * 540f;
            float vy = (_rng.Next() * 0.36667f) + 0.13333f;
            _sparks.Add(new Spark(scale, life, 0f, vy * (480f - x) / (y + 75f), vy, alpha, x, y));
        }

        for (int i = 0; i < _sparks.Count; i++)
        {
            Spark s = _sparks[i];
            _sparks[i] = s with { X = s.X + (s.Vx * frames), Y = s.Y - (s.Vy * frames), Age = s.Age + frames };
        }

        _sparks.RemoveAll(s => s.Age > s.Life);
    }

    /// <summary>The strip offset factor: eased in while opening, held at full slide while closing.</summary>
    public float Slide => TSpeed >= 0f ? 1f - ((1f - T) * (1f - T)) : 1f;

    /// <summary>The strip alpha factor.</summary>
    public float StripAlpha => 1f - ((1f - T) * (1f - T));

    /// <summary>Draws the backdrop (nothing while it is hidden).</summary>
    public void Draw(UiPainter p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (!Visible)
        {
            return;
        }

        p.Sprite(UiSheets.Backdrop, UiMetrics.Screen, A);
        float k = Slide;
        float sa = StripAlpha;
        foreach ((bool low, int idx, float alpha, float cx, float y0, float dy) in Strips)
        {
            UiSheet sheet = low ? UiSheets.StripLow(idx) : UiSheets.StripHigh(idx);
            (int w, int h) = p.SizeOf(sheet);
            if (w == 0)
            {
                continue;
            }

            float cy = y0 + (dy * k);
            p.Sprite(sheet, new RectF(0, 0, w, h), new RectF(cx - (w / 2f), cy - (h / 2f), w, h), sa * alpha / 255f);
        }

        foreach (Spark s in _sparks)
        {
            (float kk, float dim) = Phase(s.Age, s.Life);
            float size = s.Scale * kk * 80f;
            p.Sprite(UiSheets.Spark, new RectF(0, 0, 80, 80), new RectF(s.X * 2f, s.Y * 2f, size, size), s.Alpha * dim * A);
        }
    }

    /// <summary>Fade-in / fade-out over 60 frames at both ends of a particle's life; the flicker dims odd frames of the fade-out to 70%.</summary>
    internal static (float K, float Dim) Phase(float age, float life)
    {
        int ai = (int)age;
        if (ai <= 59)
        {
            return (age / 60f, 1f);
        }

        float left = life - age;
        if (left > 59f)
        {
            return (1f, 1f);
        }

        return (MathF.Max(left, 0f) / 60f, (ai & 2) == 0 ? 0.7f : 1f);
    }

    private readonly record struct Spark(float Scale, float Life, float Age, float Vx, float Vy, float Alpha, float X, float Y);
}
