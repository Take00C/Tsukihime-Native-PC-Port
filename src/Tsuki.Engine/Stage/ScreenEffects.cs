using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

/// <summary>
/// <c>_MSQK</c>: the message-window quake (globals 0xDF0894..0xDF08A8; docs/formats/quake.md 6). Port of <c>tsuki.quake.MessageQuake</c>.
/// The offsets (<see cref="X"/>, <see cref="Y"/>) are added by the HUD (0x53CF0) to the message window and text positions, in 640 x 448 UI
/// canvas units.
/// </summary>
public sealed class MessageQuake
{
    private float _dur;
    private float _t;
    private float _ampY;
    private float _ampX;

    /// <summary>Offset x (0xDF08A4).</summary>
    public float X { get; private set; }

    /// <summary>Offset y (0xDF08A8).</summary>
    public float Y { get; private set; }

    /// <summary>True while a quake runs (duration above 0).</summary>
    public bool Active => _dur > 0f;

    /// <summary>0x962D3 -> 0x53BE0(ms, a2, a1): <c>_MSQK(ms = 1000, a1 = 5, a2 = 5)</c>.</summary>
    public void Start(int ms, int a1, int a2)
    {
        _dur = (uint)ms < 10001u ? ms : 1000;
        _t = 0f;
        _ampY = a2 * 448.0f / 720.0f;
        _ampX = a1 * 640.0f / 1280.0f;
        X = 0f;
        Y = 0f;
    }

    /// <summary>
    /// Top of 0x57980 (before the layer loop; also runs while the layer update is frozen): two <c>rand()</c> calls per active frame, x first.
    /// <paramref name="arg"/> is the update argument and <paramref name="fpu"/> the frames per update (both 1 at 60 Hz).
    /// </summary>
    public void Update(Ps4Rand rng, int arg = 1, int fpu = 1)
    {
        if (!(_dur > 0f))
        {
            return;
        }

        _t = ((((float)arg * fpu) * 1000.0f) / 60.0f) + _t;
        if (_t >= _dur)
        {
            _dur = 0f;
            X = 0f;
            Y = 0f;
            return;
        }

        X = Ps4Math.RndRange(rng, -_ampX, _ampX);
        Y = Ps4Math.RndRange(rng, -_ampY, _ampY);
    }

    /// <summary>0x57590 zeroes all six globals: a running quake is cancelled (ADV-mode entry, scan load).</summary>
    public void Reset()
    {
        _dur = 0f;
        _t = 0f;
        _ampY = 0f;
        _ampX = 0f;
        X = 0f;
        Y = 0f;
    }
}

/// <summary>
/// <c>_SHOK</c>: the screen shock, screen-effect kind 1 (globals 0xDF07B4.., 0xDF0818..; docs/formats/quake.md 7). A damped sine without
/// <c>rand()</c>. <see cref="X"/> and <see cref="Y"/> (0xDF0720 / 0xDF0724, canvas units) are added to every scene layer except pictures
/// with catalog draw_flags bit 1, and not to the HUD. Port of <c>tsuki.quake.ScreenShock</c>.
/// </summary>
public sealed class ScreenShock
{
    private int _kind;
    private int _remaining;
    private float _period;
    private float _count;
    private float _ax;
    private float _ay;
    private float _phase;
    private float _amp;
    private int _delay;
    private (int Kind, int Ax, int Ay, int Ms, int N) _pending;

    /// <summary>Output x (0xDF0720).</summary>
    public float X { get; private set; }

    /// <summary>Output y (0xDF0724).</summary>
    public float Y { get; private set; }

    /// <summary>Start delay remaining in ms (0xDF0818).</summary>
    public int Delay => _delay;

    /// <summary>Frames remaining (0xDF07D4); negative = endless.</summary>
    public int Remaining => _remaining;

    /// <summary>Screen-effect kind (0xDF07B4): 1 = shock, -1 after <c>_SHAF</c>, 0 idle.</summary>
    public int Kind => _kind;

    /// <summary>0x438C0(1, ax, ay, ms, n, delay): <c>_SHOK(ax, ay, ms = 300, n = 3, delay = 0)</c> (values already as i16).</summary>
    public void Start(int ax, int ay, int ms, int n, int delay = 0)
    {
        _delay = delay;
        if (delay > 0)
        {
            _pending = (1, ax, ay, ms, n);
            return;
        }

        Begin(1, ax, ay, ms, n);
    }

    private void Begin(int kind, int ax, int ay, int ms, int n)
    {
        _kind = kind;
        _phase = 0f;
        _amp = 1f;
        float t = (ms * 3.0f) / 50.0f;
        _remaining = CDiv(I32(3L * I32((long)ms * n)), 50);
        _period = t == 0f ? 1f : t;
        _count = n >= 2 ? n : 1;
        _ax = ax;
        _ay = ay;
    }

    /// <summary><c>_SHAF()</c> (0x80AE0 -> 0x42DC0(0,0,0,0)): kind -1, remaining 0; offsets are 0 from the next update. A pending delayed shock is not cancelled.</summary>
    public void Stop()
    {
        _kind = -1;
        _remaining = 0;
    }

    /// <summary>0x57590 zeroes only the outputs (VA 0x578BB / 0x578C5); the effect state is not reset.</summary>
    public void AdvStart()
    {
        X = 0f;
        Y = 0f;
    }

    /// <summary>The shock term of a scene layer's draw (VA 0x51808..0x51833): (X, Y), or (0, 0) for a picture with catalog draw_flags bit 1.</summary>
    public (float X, float Y) LayerOffset(bool noShock = false) => noShock ? (0f, 0f) : (X, Y);

    /// <summary>0x439D0(dt), kind 1 only (other kinds belong to other commands).</summary>
    public void Update(int dt)
    {
        if (_delay > 0)
        {
            int v = _delay - CDiv((long)dt * 1000, 60);
            _delay = v > 0 ? v : 0;
            if (v <= 0)
            {
                Begin(_pending.Kind, _pending.Ax, _pending.Ay, _pending.Ms, _pending.N);
            }
        }

        if (_remaining == 0)
        {
            X = 0f;
            Y = 0f;
            return;
        }

        if (_kind != 1)
        {
            return;
        }

        if (_remaining > 0)
        {
            int v = _remaining - dt;
            _remaining = v > 0 ? v : 0;
            if (v <= 0)
            {
                _kind = 0;
                _period = 0f;
                _ax = 0f;
                _ay = 0f;
                _phase = 0f;
                _amp = 0f;
                X = 0f;
                Y = 0f;
                return;
            }
        }

        float fr = dt;
        float dph = (fr * Ps4Math.TwoPi) / _period;
        float dam = (fr / _period) / _count;
        _phase = dph + _phase;
        float a = _amp - dam;
        _amp = 0f > a ? 0f : a;
        float s = Ps4Math.Sinf(_phase);
        X = ((((s * _ax) * _amp) * 640.0f) / 480.0f);
        Y = (((_amp * (s * _ay)) * 480.0f) / 360.0f);
    }
}
