// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/ui.rs (ListScroll, sub_27BD4)
namespace Tsuki.Ui.Screens;

/// <summary>A tween of a list's scroll position: ease-out quad over a number of 60 Hz frames (knife <c>ListScroll</c>, sub_27BD4).</summary>
internal sealed class ListScroll
{
    /// <summary>The tween length in frames (1/6 s).</summary>
    public const float Frames = 10f;

    private float _from;
    private float _t;
    private float _len;

    /// <summary>The shown position.</summary>
    public float Pos { get; private set; }

    /// <summary>The position it heads for.</summary>
    public float Target { get; private set; }

    /// <summary>Heads for <paramref name="v"/> (clamped to 0..<paramref name="max"/>), over the standard tween or at once.</summary>
    public void To(float v, float max, bool animate) => Over(v, max, animate ? Frames : 0f);

    /// <summary>Heads for <paramref name="v"/> over <paramref name="frames"/> frames (0: at once).</summary>
    public void Over(float v, float max, float frames)
    {
        Target = Math.Clamp(v, 0f, Math.Max(max, 0f));
        _len = frames;
        if (frames > 0f)
        {
            _from = Pos;
            _t = 0f;
        }
        else
        {
            Pos = Target;
            _t = 0f;
        }
    }

    /// <summary>Jumps to <paramref name="v"/>.</summary>
    public void Set(float v)
    {
        Pos = v;
        Target = v;
        _len = 0f;
    }

    /// <summary>One frame.</summary>
    public void Step()
    {
        if (_t < _len)
        {
            _t = Math.Min(_t + 1f, _len);
            float t = _t / _len;
            Pos = _from + ((Target - _from) * -(t * (t - 2f)));
        }
    }

    /// <summary>True while the tween runs.</summary>
    public bool Moving => _t < _len;
}
