// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/game/ghost.rs
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Game.Effects;

/// <summary>
/// The <c>_FADE(40, p, q)</c> screen effect (knife <c>game/ghost.rs</c>; the engine's sub_2E280 / sub_2F910 state and sub_2FC50 draw): each colour channel
/// of the frame is shifted along a slowly swinging direction and the three are overlaid back onto the frame, with the shift amplitude and angle animated
/// every 60 Hz frame. The parameters live in F words: F1100 = screen mode (4 while on), F1120 = p + 127 (brightness), F1121 = q (preset). <c>_FADE(41)</c>
/// sets mode 5, which fades the effect out. The PS4 has the same command and the same words (compositing.md 8); the animation is knife's.
/// </summary>
public sealed class GhostEffect
{
    private float _bright;
    private float _latched;
    private float _base;
    private float _wobble;
    private int _frame;
    private int _sub;
    private int _phase;
    private float _curve;
    private bool _up;
    private float _amp;
    private float _angle;

    /// <summary>The screen mode: 4 on (the alpha rises 1/60 a frame), 5 fading out, anything else off.</summary>
    public int Mode { get; private set; }

    /// <summary>The effect's alpha 0..1.</summary>
    public float Alpha { get; private set; }

    /// <summary>True once the phase wrapped (the wobble stops).</summary>
    public bool Wrapped { get; private set; }

    /// <summary>True while the effect is on or fading out.</summary>
    public bool Active => Mode is 4 or 5;

    /// <summary>The current shift amplitude in 960-wide pixels (tests).</summary>
    public float Amplitude => _amp;

    /// <summary>The current shift direction in degrees, swinging between -30 and 30 (tests).</summary>
    public float Angle => _angle;

    /// <summary>Stops the effect at once (a scene start).</summary>
    public void Reset()
    {
        Mode = 0;
        Alpha = 0f;
        Wrapped = false;
        _up = false;
        _frame = _sub = _phase = 0;
    }

    // the q presets (sub_2E280): (amplitude, wobble, starting amplitude x 100)
    private static (float Base, float Wobble, float Init) Preset(int q) => q switch
    {
        6 or 7 or 12 => (2.5f, 200f, 50f),
        8 => (3.5f, 200f, 70f),
        9 => (3.1f, 105f, 62f),
        10 => (3.1f, 150f, 62f),
        13 => (1.5f, 105f, 30f),
        _ => (3.1f, 200f, 62f),
    };

    // q = 16: the brightness climbs from p to 0 by 90 / 8 per second
    private static float Ramp(float p, int frame) => MathF.Min((p + (int)(frame * 90f * 0.125f / 60f)), 0f);

    private void ResetPhase(float init)
    {
        _frame = 0;
        _sub = 0;
        _phase = 0;
        _curve = 0f;
        Wrapped = false;
        _up = false;
        _amp = init / 100f;
        _angle = 30f;
    }

    /// <summary>
    /// FADE(40) (sub_2E3E0(4, reset) -> sub_2E280): <paramref name="reset"/> when the screen mode was 0 before, so a repeated FADE(40) only changes the
    /// brightness and preset of the running effect. A non-zero <paramref name="p"/> shows it at once.
    /// </summary>
    public void Start(int p, int q, bool reset)
    {
        Mode = 4;
        (_base, _wobble, float init) = Preset(q);
        _bright = q == 16 && !reset ? Ramp(p, _frame) : p;
        if (reset)
        {
            ResetPhase(init);
        }

        if (p != 0)
        {
            Alpha = 1f;
        }
    }

    /// <summary>sub_2E430(mode): FADE(41) (mode 5) and the replay restoring F1100. Mode 4 always restarts the phase.</summary>
    public void SetMode(int mode, int p, int q)
    {
        Mode = mode;
        if (mode == 4)
        {
            (_base, _wobble, float init) = Preset(q);
            _bright = p;
            ResetPhase(init);
            if (p != 0)
            {
                Alpha = 1f;
            }
        }
    }

    /// <summary>Marks the phase as wrapped (the load of a running effect, knife game.rs).</summary>
    public void MarkWrapped() => Wrapped = true;

    /// <summary>
    /// The effect as a restored record had it (knife game.rs restore_with: sub_2E430(F1100) with the restored words, the wobble marked as wrapped): mode 4
    /// with the preset's amplitude and the full alpha, since the effect was on when the record was pushed.
    /// </summary>
    public void Restore(int p, int q)
    {
        SetMode(4, p, q);
        Alpha = 1f;
        Wrapped = true;
    }

    /// <summary>Runs <paramref name="frames"/> 60 Hz steps of sub_2F910 with the brightness word <paramref name="p"/> (F1120 - 127) and the preset <paramref name="q"/> (F1121).</summary>
    public void Step(int frames, int p, int q)
    {
        for (int i = 0; i < Math.Max(1, frames) && Active; i++)
        {
            StepOne(p, q);
        }
    }

    private void StepOne(int p, int q)
    {
        if (Mode == 4)
        {
            Alpha = MathF.Min(Alpha + 0.016667f, 1f);
        }
        else
        {
            Alpha -= 0.016667f;
            if (Alpha <= 0f)
            {
                Mode = 0;
                Alpha = 0f;
            }
        }

        _frame++;
        (_base, _wobble, _) = Preset(q);
        _bright = q == 16 ? Ramp(p, _frame) : p;
        bool tick = _sub >= 5;
        _sub++;
        if (!tick)
        {
            return;
        }

        _sub = 0;
        _phase += 100;
        if (_phase >= 3334)
        {
            _phase -= 3333;
            Wrapped = true;
        }

        int folded = _phase > 1666 ? 3333 - _phase : _phase;
        _curve = 0.5f - (MathF.Cos(folded * MathF.PI / 1666f) * 0.5f);
        if (!Wrapped)
        {
            _curve += _wobble / 100f * MathF.Sin(_phase * MathF.PI / 3333f);
        }

        _amp = _base * (((_curve * 80f) + 20f) / 100f);
        // the angle swings by 3 degrees a step and reflects off +-30
        if (_up)
        {
            _angle += 3f;
            if (_angle > 30f)
            {
                _angle = 30f - (_angle - 30f);
                _up = false;
            }
        }
        else
        {
            _angle -= 3f;
            if (_angle < -30f)
            {
                _angle = -30f - (_angle + 30f);
                _up = true;
            }
        }
    }

    /// <summary>
    /// sub_2FC50 as display-list commands over the composed frame (target 0): the frame is copied into capture surface <see cref="SourceSurface"/>,
    /// each of the three channels (red along the angle, green opposite, blue twice as far opposite) is drawn twice, unshifted then shifted, into target
    /// <see cref="ChannelTarget"/> through the channel colour, copied, and summed into target <see cref="SumTarget"/> (red replaces, green and blue add),
    /// and that is overlaid onto the frame (effect 22) at the effect's alpha. <paramref name="transition"/> is the elapsed fraction of a running
    /// transition (0..1), which eases a brightness change in; null when none runs.
    /// </summary>
    public void Draw(DisplayList list, float? transition)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (Alpha <= 0f || !Active)
        {
            return;
        }

        float bright = _bright;
        if (transition is { } t)
        {
            bright = _latched + (t * (_bright - _latched));
        }
        else
        {
            _latched = _bright;
        }

        const float w = 1920f, h = 1080f;
        float k = _amp * w / 960f;
        float th = _angle * MathF.PI / 180f;
        float opp = th + MathF.PI;
        // red along the angle, green opposite, blue twice as far opposite (y down)
        (float X, float Y)[] offsets =
        [
            (MathF.Cos(th) * k, -(MathF.Sin(th) * k)),
            (MathF.Cos(opp) * k, -(MathF.Sin(opp) * k)),
            (2f * k * MathF.Cos(opp), -(2f * k * MathF.Sin(opp))),
        ];

        // vertex colours 0xAARRGGBB: ((int)bright + 255) in one channel, built with the exe's wrapping maths
        int v = (int)bright + 255;
        uint[] words = [unchecked((uint)((v << 16) - 0x0100_0000)), unchecked((uint)((v << 8) - 0x0100_0000)), unchecked((uint)(v - 0x0100_0000))];
        list.Add(Capture(0, SourceSurface));
        for (int c = 0; c < 3; c++)
        {
            uint word = words[c];
            uint rgba = ((word >> 16) & 0xFF) | (((word >> 8) & 0xFF) << 8) | ((word & 0xFF) << 16) | (word & 0xFF000000u);
            (float ox, float oy) = offsets[c];
            TextureRef src = TextureRef.ForCapture(SourceSurface);
            list.Add(Quad(src, 0, 0, w, h, rgba, 0, 0, ChannelTarget));
            list.Add(Quad(src, ox, oy, ox + w, oy + h, rgba, 0, 0, ChannelTarget));
            list.Add(Capture(ChannelTarget, ChannelSurface));
            list.Add(Quad(TextureRef.ForCapture(ChannelSurface), 0, 0, w, h, rgba, 0, c == 0 ? 0 : 2, SumTarget));
        }

        list.Add(Capture(SumTarget, SumSurface));
        uint overlay = 0x00FFFFFFu | ((uint)(byte)Math.Clamp((int)MathF.Round(Alpha * 255f), 0, 255) << 24);
        list.Add(Quad(TextureRef.ForCapture(SumSurface), 0, 0, w, h, overlay, 22, 1, 0));
    }

    /// <summary>Capture surface that holds the frame the channels are cut from.</summary>
    public const int SourceSurface = 8;

    /// <summary>Capture surface that holds one shifted channel.</summary>
    public const int ChannelSurface = 9;

    /// <summary>Capture surface that holds the sum of the three channels.</summary>
    public const int SumSurface = 10;

    /// <summary>Render target of one shifted channel.</summary>
    public const byte ChannelTarget = 2;

    /// <summary>Render target of the channel sum.</summary>
    public const byte SumTarget = 3;

    private static DrawCmd Capture(byte target, int surface) => new() { Kind = DrawKind.Capture, Target = target, Texture = TextureRef.ForCapture(surface) };

    private static DrawCmd Quad(TextureRef tex, float x0, float y0, float x1, float y1, uint rgba, int word, int blendState, byte target) => new()
    {
        Kind = DrawKind.TexturedQuad,
        Target = target,
        Texture = tex,
        V0 = new Vertex(x0, y0, 0, 0, rgba),
        V1 = new Vertex(x1, y0, 1, 0, rgba),
        V2 = new Vertex(x0, y1, 0, 1, rgba),
        V3 = new Vertex(x1, y1, 1, 1, rgba),
        EffectWord = (short)word,
        BlendState = (byte)blendState,
    };
}
