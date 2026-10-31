// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/game/focus.rs
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;

namespace Tsuki.Engine.Game.Effects;

/// <summary>
/// The <c>_FCLN</c> screen effects (knife <c>game/focus.rs</c>; the engine's sub_86470 start / stop, sub_86850 step, sub_87A90 draw): effects 4..33 are
/// manga focus lines (up to 400 strips radiating from a centre, re-rolled every interval, keyed by up to four timed keyframes per effect), effects 1..3
/// are falling musical notes (50 sprites with their own drift). The keyframes come from the eboot (<see cref="IEffectTables"/>, 118 of 120 equal knife's);
/// the random numbers are the engine's shared libc <c>rand()</c> (<see cref="Ps4Rand"/>), as on the console.
/// </summary>
/// <remarks>
/// Positions are kept in the 640 x 448 draw space; lines and notes are sized in native pixels of the 1920 x 1080 frame. The focus lines are the
/// 1920 x 128 strip of EFFECT_PARTS (child 3, 960 x 2 texels per line); the three falling-note sheets (children 0..2) are 16 x 16 transparent dummies on
/// the PS4, so the notes move but draw nothing visible there (archives_ui_movies.md 8). PS4 vs Switch: the keyframe table is read from the PS4 eboot.
/// </remarks>
public sealed class FocusEffect
{
    private const int LineSlots = 400;
    private const int NoteSlots = 50;
    private const float RandScale = 1f / 1073741824f;        // rand() has 30 bits on the PS4 (knife multiplies a 31-bit value by 4.6566e-10)

    // knife focus.rs SINGLE: effects (- 4) that stay on their first keyframe (a bit mask from the Switch exe; plain layout data)
    private const uint SingleKeyEffects = 0x2930_3FFB;

    private readonly IEffectTables? _tables;
    private readonly Ps4Rand _rng;
    private readonly Slot[] _slots = new Slot[LineSlots];
    private float _rate = 1f;
    private float _chance = 0.05f;
    private short _clock;
    private short _since;

    /// <summary>Creates the effect over the eboot tables (null: the lines have no keyframes and stay empty) and the shared random generator.</summary>
    public FocusEffect(IEffectTables? tables, Ps4Rand rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        _tables = tables;
        _rng = rng;
        Array.Fill(_slots, Slot.Free);
    }

    /// <summary>0 off, 1 fading in, 2 on, 3 fading out.</summary>
    public FocusMode Mode { get; private set; }

    /// <summary>The running effect id: 1..3 falling notes, 4..33 focus lines, 0 none.</summary>
    public int Effect { get; private set; }

    /// <summary>The fade level 0..1.</summary>
    public float Level { get; private set; }

    /// <summary>True while the effect runs in any mode.</summary>
    public bool Active => Mode != FocusMode.Off;

    /// <summary>The live particles: lines (effects 4..33) or notes (1..3).</summary>
    public int ParticleCount => _slots.Count(static s => s.Kind >= 0);

    /// <summary>The keyframe the last roll of the lines used (0..3).</summary>
    public int CurrentKey => _slots[0].Key;

    /// <summary>Stops everything (a scene start).</summary>
    public void Reset()
    {
        Mode = FocusMode.Off;
        Effect = 0;
        Level = 0f;
        _rate = 1f;
        _chance = 0.05f;
        _clock = 0;
        _since = 0;
        Array.Fill(_slots, Slot.Free);
    }

    /// <summary>sub_86470(effect, frames): starts <paramref name="effect"/> (frames 0) or stops it fading out over <paramref name="frames"/>. The effect already on keeps running.</summary>
    public void Start(int effect, int frames)
    {
        float rate = frames > 0 ? 1f / frames : 1f;
        switch (effect)
        {
            case 0 when frames <= 0:
                Level = 0f;
                Mode = FocusMode.Off;
                _rate = 1f;
                break;
            case 0:
                Mode = FocusMode.FadingOut;
                _rate = rate;
                break;
            case >= 4 and <= 33:
                if (Mode == FocusMode.On && Effect == effect)
                {
                    return;
                }

                (Mode, Effect, Level, _rate) = (FocusMode.FadingIn, effect, 0f, rate);
                Array.Fill(_slots, Slot.Free);          // the qword stores clear words 1..3 too: the line clocks in slots 0 and 1
                (_clock, _since) = (0, 0);
                break;
            case >= 1 and <= 3:
                (Mode, Effect, Level, _rate) = (FocusMode.FadingIn, effect, 0f, rate);
                for (int i = 0; i < NoteSlots; i++)
                {
                    _slots[i].Kind = -1;
                }

                _chance = effect == 3 ? 0.1f : 0.05f;
                break;
            default:
                break;
        }
    }

    /// <summary>sub_86850 for <paramref name="frames"/> frames (1, or more after a dropped frame): the fade, then the particles.</summary>
    public void Step(int frames)
    {
        short n = (short)Math.Max(1, frames);
        float nf = n;
        switch (Mode)
        {
            case FocusMode.FadingOut:
                Level -= _rate * nf;
                if (Level < 0f)
                {
                    Level = 0f;
                    Mode = FocusMode.Off;
                    return;
                }

                break;
            case FocusMode.FadingIn:
                Level += _rate * nf;
                if (Level > 1f)
                {
                    Level = 1f;
                    Mode = FocusMode.On;
                }

                break;
            case FocusMode.Off:
                return;
            default:
                break;
        }

        switch (Effect)
        {
            case >= 4 and <= 33:
                StepLines(n);
                break;
            case >= 1 and <= 3:
                StepNotes(n);
                break;
            default:
                break;
        }
    }

    private void StepLines(short n)
    {
        if (_tables is null)
        {
            return;
        }

        int idx = Effect - 4;
        _clock = unchecked((short)(_clock + n));
        if (_clock < 0)
        {
            _clock = 180;
        }

        _since = unchecked((short)(_since + n));
        float t = _clock * 1000f / 60f;
        // the keyframe in force and the time of the next (-1: none to blend towards)
        FocusKey k0 = _tables.FocusKey(idx, 0), k1 = _tables.FocusKey(idx, 1), k2 = _tables.FocusKey(idx, 2), k3 = _tables.FocusKey(idx, 3);
        float t1 = k1.TimeMs;
        int key;
        int next;
        if (t < k0.TimeMs || (((SingleKeyEffects >> idx) & 1) == 0 && t >= t1))
        {
            float t2 = k2.TimeMs;
            if (t >= t1 && (idx != 2 || t < t2))
            {
                (key, next) = (1, k2.TimeMs);
            }
            else if (t >= t2)
            {
                (key, next) = (2, k3.TimeMs);
            }
            else
            {
                (key, next) = (0, k1.TimeMs);
            }
        }
        else
        {
            (key, next) = (0, k1.TimeMs);
        }

        FocusKey kf = _tables.FocusKey(idx, key);
        float alpha = kf.Alpha;
        if (next >= 0)
        {
            FocusKey following = _tables.FocusKey(idx, key + 1);
            alpha += (t - kf.TimeMs) * (following.Alpha - kf.Alpha) / (next - kf.TimeMs);
        }

        if (_since * 1000f / 60f < kf.RerollMs)
        {
            return;
        }

        _since = 0;
        int span = Math.Max(1, Math.Abs(kf.CountMax - kf.CountMin));
        int count = Math.Min((_rng.Next() % span) + kf.CountMin, LineSlots);
        if (Effect <= 8)
        {
            count /= 2;
        }

        count = Math.Max(0, count);
        for (int i = 0; i < count; i++)
        {
            _slots[i].Key = key;
            RollLine(i, idx);
            _slots[i].Alpha = alpha / 255f;
        }

        for (int i = count; i < LineSlots; i++)
        {
            _slots[i] = Slot.Free;
        }
    }

    // sub_862E0: a line from a random point of the ring around the keyframe's centre, pointing away from it
    private void RollLine(int i, int idx)
    {
        FocusKey kf = _tables!.FocusKey(idx, _slots[i].Key);
        short angle = unchecked((short)((_rng.Next() % 65536) - 32768));
        int lo = Math.Min(kf.RadiusMin, kf.RadiusMax);
        int span = Math.Max(1, Math.Abs(kf.RadiusMax - kf.RadiusMin));
        float r = ((_rng.Next() % span) + lo) * 0.7f;
        float th = Radians(angle);
        Slot s = _slots[i];
        s.Kind = (short)(idx + 4);
        s.Angle = angle;
        s.X = ((r * MathF.Cos(th)) + kf.CentreX) * 640f / 1280f;
        s.Y = ((r * MathF.Sin(th)) + kf.CentreY) * 448f / 720f;
        _slots[i] = s;
    }

    private void StepNotes(short n)
    {
        if ((_rng.Next() * RandScale) < _chance)
        {
            for (int i = 0; i < NoteSlots; i++)
            {
                if (_slots[i].Kind < 0)
                {
                    DropNote(i, _rng.Next() % 3);
                    break;
                }
            }
        }

        IReadOnlyList<FocusNote>? notes = _tables?.FocusNotes;
        float nf = n;
        for (int i = 0; i < NoteSlots; i++)
        {
            if (_slots[i].Kind < 0)
            {
                continue;
            }

            Slot s = _slots[i];
            if (s.Total >= 1)
            {
                int t = s.Tick + n;
                while (t >= s.Total)
                {
                    t -= s.Total;
                }

                s.Tick = (short)t;
                s.Frame = (short)(t / s.Step);
            }

            s.Vy += s.Ay * nf;
            s.Vx += s.Ax * nf;
            s.X += s.Vx * nf;
            s.Y += s.Vy * nf;
            if (notes is not null && s.Y > notes[Math.Min(s.Kind, notes.Count - 1)].Floor)
            {
                s.Kind = -1;
            }

            _slots[i] = s;
        }
    }

    // sub_86120: a note above the top edge at a random phase of its animation
    private void DropNote(int i, int kind)
    {
        if (_tables is null || kind >= _tables.FocusNotes.Count)
        {
            return;
        }

        FocusNote d = _tables.FocusNotes[kind];
        int total = d.Frames * d.Step;
        short tick = (short)(_rng.Next() % total);
        float Jitter() => (_rng.Next() % 16) - 8;
        float jx = Jitter(), jy = Jitter(), jay = Jitter();
        float px = (_rng.Next() % 801) - 80;
        float py = (_rng.Next() % 101) - 130;
        _slots[i] = new Slot
        {
            Kind = (short)kind,
            Tick = tick,
            Total = (short)total,
            Step = (short)d.Step,
            Frame = (short)(tick / d.Step),
            X = px,
            Y = py,
            Alpha = 1f,
            Vx = d.Vx + (d.JitterX * jx),
            Vy = d.Vy + (d.JitterY * jy),
            Ax = 0f,                                    // the x acceleration's jitter scale is 0
            Ay = d.Accel * jay,
        };
    }

    // an i16 angle (a half turn per 32768) in radians
    private static float Radians(short a) => a * MathF.PI / 32768f;

    // FocusLine texel origin per effect - 4 (knife line_uv, unk_3527C0): each line is 960 x 2 texels of the 1920 x 128 strip
    private static (float U, float V) LineUv(int idx) => idx switch
    {
        2 => (0f, 10f),
        3 => (0f, 40f),
        6 => (0f, 30f),
        14 => (850f, 120f),
        _ => (0f, 0f),
    };

    // the scale (qword_AC0D68) starts at the native-pixel one and is only changed by the kinds and angles below, so a line can inherit the previous line's
    private static bool LineScale(short kind, short angle, out float sx, out float sy)
    {
        const float native = 448f / 1080f;
        ushort u = (ushort)angle;
        bool near = (ushort)(u + 7999) <= 0x3E7E;
        (sx, sy) = (0f, 0f);
        switch (kind)
        {
            case 7:
                (sx, sy) = (2f / 3f, native / 2f);
                return true;
            case 8 when near:
                (sx, sy) = (1f / 6f, native);
                return true;
            case 8 when (ushort)(u + 27000) >= 0xD2F1:
                (sx, sy) = (0.4f, native);
                return true;
            case 9 or 10:
                (sx, sy) = (0.5f, native);
                return true;
            case 13 or 19:
                (sx, sy) = (0.4f, native);
                return true;
            case >= 15 and <= 17:
                (sx, sy) = (0.6f, native);
                return true;
            case 18 when (ushort)(u + 21000) >= 0xA411:
                (sx, sy) = (0.43333334f, native);
                return true;
            case 20 when near:
                (sx, sy) = (0.4f, native);
                return true;
            case >= 21 and <= 23 when u >= 0xE0C1:
                (sx, sy) = (0.4f, native);
                return true;
            case 26 or 27 when near:
                (sx, sy) = (0.6f, native);
                return true;
            case >= 28 and <= 31:
                (sx, sy) = (0.5333333f, native);
                return true;
            case 32 when (ushort)(u + 27000) >= 0xD2F1:
                (sx, sy) = (0.5333333f, native);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// sub_87A90(2): appends the lines (alpha-blended strips of the FocusLine texture) or the notes (sprites of the three FallNote sheets) to
    /// <paramref name="list"/>. Nothing when the effect is off. Sheets are whole-sheet UI tiles of EFFECT_PARTS.
    /// </summary>
    public void Draw(DisplayList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (!Active)
        {
            return;
        }

        const float kx = 1920f / 640f, ky = 1080f / 448f;
        switch (Effect)
        {
            case >= 4 and <= 33:
            {
                TextureRef strip = TextureRef.ForUiTile(UiSet.EffectParts, Language.Ja, 3, -1);
                const float native = 448f / 1080f;
                float sx = 640f / 1920f, sy = native;
                foreach (Slot s in _slots)
                {
                    if (s.Alpha <= 0f || s.Kind < 0)
                    {
                        continue;
                    }

                    if (LineScale(s.Kind, s.Angle, out float nsx, out float nsy))
                    {
                        (sx, sy) = (nsx, nsy);
                    }

                    float th = Radians(s.Angle);
                    float c = MathF.Cos(th), sn = MathF.Sin(th);
                    float len = 960f * sx, wid = 2f * sy;
                    (float u0, float v0) = LineUv(s.Kind - 4);
                    uint rgba = Pack(s.Alpha * Level);
                    (float ax, float ay) = Px(s, c, sn, 0, 0, kx, ky);
                    (float bx, float by) = Px(s, c, sn, len, 0, kx, ky);
                    (float cx, float cy) = Px(s, c, sn, 0, wid, kx, ky);
                    (float dx, float dy) = Px(s, c, sn, len, wid, kx, ky);
                    list.Add(new DrawCmd
                    {
                        Kind = DrawKind.TexturedQuad,
                        Texture = strip,
                        V0 = new Vertex(ax, ay, u0 / 1920f, v0 / 128f, rgba),
                        V1 = new Vertex(bx, by, (u0 + 960f) / 1920f, v0 / 128f, rgba),
                        V2 = new Vertex(cx, cy, u0 / 1920f, (v0 + 2f) / 128f, rgba),
                        V3 = new Vertex(dx, dy, (u0 + 960f) / 1920f, (v0 + 2f) / 128f, rgba),
                        BlendState = 1,
                    });
                }

                break;
            }

            case >= 1 and <= 3 when _tables is { } tables:
            {
                for (int i = 0; i < NoteSlots; i++)
                {
                    Slot s = _slots[i];
                    if (s.Alpha <= 0f || s.Kind < 0 || s.Kind >= tables.FocusNotes.Count)
                    {
                        continue;
                    }

                    FocusNote d = tables.FocusNotes[s.Kind];
                    float a = Math.Max(0f, s.Alpha * Level);
                    float x0 = s.X * kx, y0 = s.Y * ky;
                    float w = d.Width, h = d.Height;
                    uint rgba = Pack(a);
                    TextureRef sheet = TextureRef.ForUiTile(UiSet.EffectParts, Language.Ja, s.Kind, -1);
                    list.Add(new DrawCmd
                    {
                        Kind = DrawKind.TexturedQuad,
                        Texture = sheet,
                        V0 = new Vertex(x0, y0, 0, 0, rgba),
                        V1 = new Vertex(x0 + w, y0, 1, 0, rgba),
                        V2 = new Vertex(x0, y0 + h, 0, 1, rgba),
                        V3 = new Vertex(x0 + w, y0 + h, 1, 1, rgba),
                        BlendState = 1,
                    });
                }

                break;
            }

            default:
                break;
        }
    }

    private static (float X, float Y) Px(Slot s, float c, float sn, float dl, float dw, float kx, float ky) =>
        ((s.X + (c * dl) - (sn * dw)) * kx, (s.Y + (sn * dl) + (c * dw)) * ky);

    private static uint Pack(float alpha)
    {
        byte a = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
        return 0x00FFFFFFu | ((uint)a << 24);
    }

    /// <summary>One particle (56 bytes at word_14715DC on the console).</summary>
    private struct Slot
    {
        public static readonly Slot Free = new() { Kind = -1 };

        /// <summary>Word 0: effect (lines) or note kind; -1 free.</summary>
        public short Kind;

        /// <summary>Notes: animation tick, ticks per loop, ticks per frame, frame.</summary>
        public short Tick;
        public short Total;
        public short Step;
        public short Frame;

        /// <summary>Lines: the keyframe it was rolled from.</summary>
        public int Key;

        /// <summary>Lines: angle (i16 of a half turn).</summary>
        public short Angle;
        public float X;
        public float Y;
        public float Alpha;
        public float Vx;
        public float Vy;
        public float Ax;
        public float Ay;
    }
}
