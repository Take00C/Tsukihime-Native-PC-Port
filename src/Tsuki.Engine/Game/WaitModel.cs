using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Game;

/// <summary>The wait bits of the u64 at VA 0x1653E00 (docs/formats/wait_model.md 3). The dead bits of section 3 are not modelled.</summary>
public static class WaitBits
{
    /// <summary>The message text is being revealed (copy of <see cref="WaitState.Flags"/> bit 0).</summary>
    public const ulong Reveal = 0x1;

    /// <summary>Wait for the voice (message flag bit 2, or <c>_VCWT</c>).</summary>
    public const ulong Voice = 0x4;

    /// <summary><c>_FADS</c> transition running.</summary>
    public const ulong Fads = 0x8;

    /// <summary>Choice menu open.</summary>
    public const ulong Choice = 0x10;

    /// <summary><c>_STCF</c> layer condition.</summary>
    public const ulong Stcf = 0x20;

    /// <summary>Message fully revealed, waiting to be dismissed.</summary>
    public const ulong Shown = 0x40;

    /// <summary>A message is displayed.</summary>
    public const ulong Message = 0x100;

    /// <summary>A system screen or fade state runs.</summary>
    public const ulong System = 0x200;

    /// <summary>End mode 0: wait for a click (or auto / skip).</summary>
    public const ulong Click = 0x400;
}

/// <summary>The end mode of a message (wait_model.md 4.1).</summary>
public enum MessageEnd : byte
{
    /// <summary><c>)</c>: wait for a click.</summary>
    Click = 0,

    /// <summary><c>@n</c>: no wait; the next <c>_MSAD</c> appends.</summary>
    Continue = 1,

    /// <summary><c>@e</c>, or <c>)</c> with the page option: no click.</summary>
    NoWait = 2,
}

/// <summary>The ADV mode words of one frame (adv_state.md 6; <c>tsuki.wait_model.AdvMode</c>).</summary>
public struct AdvMode
{
    private static readonly int[] StepTable = [1, 1, 64, 64, 64];

    /// <summary>0x17DB230: bit 0 auto mode, bit 1 latched skip.</summary>
    public int ModeWord;

    /// <summary>0x17DB232: fast-forward active this frame.</summary>
    public int SkipActive;

    /// <summary>0x17DB234: rewinding.</summary>
    public int Rewind;

    /// <summary>0x17DB236: backlog shown.</summary>
    public int LogShown;

    /// <summary>0x78AF80: the frame multiplier (1, or 64 while skipping).</summary>
    public int Step;

    /// <summary>0xDC7BE0: vsyncs since the last update (1 at 60 Hz).</summary>
    public int Vsync;

    /// <summary>Frame count passed to both functions: <c>min(vsync, 3) * step</c> (0x61A81).</summary>
    public readonly int Frames => Math.Max(0, Math.Min(Vsync, 3)) * Step;

    /// <summary>The mode of a frame when no fast-forward or R2 is held (0x6120E..0x61261): a latched skip gives skip active and the step of table 0x2014F0.</summary>
    public static AdvMode FromModeWord(int modeWord, int vsync = 1)
    {
        int mw = modeWord & 0xFFFF;
        if (mw < 2)
        {
            return new AdvMode { ModeWord = mw, SkipActive = 0, Step = 1, Vsync = vsync };
        }

        return new AdvMode { ModeWord = mw, SkipActive = 1, Step = StepTable[Math.Min(mw, StepTable.Length - 1)], Vsync = vsync };
    }
}

/// <summary>The config fields the wait code reads (adv_state.md 9.2), copied from the <see cref="ConfigBlock"/> each frame.</summary>
public struct WaitConfig
{
    /// <summary>+0x20 ms per reveal counter (unseen text).</summary>
    public int TextSpeedUnread;

    /// <summary>+0x22 (seen text).</summary>
    public int TextSpeedRead;

    /// <summary>+0x2E auto-mode wait time (ms).</summary>
    public int AutoWaitMs;

    /// <summary>+0x30 fast-forward: read text only.</summary>
    public int FastForwardReadOnly;

    /// <summary>+0x4E interrupt voices on advancing text.</summary>
    public int InterruptVoice;

    /// <summary>+0x9C display end of page instantly (unseen).</summary>
    public int PageInstantUnread;

    /// <summary>+0x9D (seen).</summary>
    public int PageInstantRead;

    /// <summary>+0x9E auto-mode advance page speed (ms).</summary>
    public int AutoPageMs;

    /// <summary>Boot values (adv_state.md 9.2).</summary>
    public static WaitConfig Defaults => new()
    {
        TextSpeedUnread = 35,
        TextSpeedRead = 15,
        AutoWaitMs = 2200,
        FastForwardReadOnly = 1,
        InterruptVoice = 1,
        PageInstantUnread = 0,
        PageInstantRead = 0,
        AutoPageMs = 3000,
    };

    /// <summary>Reads the fields from a config block.</summary>
    public static WaitConfig From(ConfigBlock c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new WaitConfig
        {
            TextSpeedUnread = c.UnseenTextSpeed,
            TextSpeedRead = c.SeenTextSpeed,
            AutoWaitMs = c.AutoModeWait,
            FastForwardReadOnly = c.FastForwardReadOnly,
            InterruptVoice = c.InterruptVoices,
            PageInstantUnread = c.PageEndInstantUnseen,
            PageInstantRead = c.PageEndInstantSeen,
            AutoPageMs = c.AutoModePageSpeed,
        };
    }
}

/// <summary>What the game input flow hands to the two wait functions for one frame (<c>tsuki.wait_model.FrameInput</c>).</summary>
public struct WaitInput
{
    /// <summary>The decide action mask of preset 0.</summary>
    public const uint Decide = 0x20;

    /// <summary>R2.</summary>
    public const uint R2 = 0x2;

    /// <summary>Game input word +0x10 (held).</summary>
    public uint Held;

    /// <summary>Game input word +0x20 (newly pressed).</summary>
    public uint Newly;

    /// <summary>Mouse flags: bit 0 left held, bit 1 left newly pressed.</summary>
    public int Mouse;

    /// <summary>Sum of the mouse wheel deltas read this frame.</summary>
    public int Wheel;

    /// <summary>0xDF29E0: d-pad down newly pressed (and no HUD menu open).</summary>
    public int AdvKey;

    /// <summary>Builds the word from a frame input.</summary>
    public static WaitInput From(in InputFrame f)
    {
        int mouse = ((f.Pointer.Held & PointerButtons.Left) != 0 ? 1 : 0) | ((f.Pointer.Pressed & PointerButtons.Left) != 0 ? 2 : 0);
        return new WaitInput
        {
            Held = (uint)f.Held,
            Newly = (uint)f.Pressed,
            Mouse = mouse,
            Wheel = f.Pointer.Wheel,
            AdvKey = (f.Pressed & PadButtons.Down) != 0 ? 1 : 0,
        };
    }
}

/// <summary>What the two functions did in one frame (for the host and traces).</summary>
public struct WaitEffects
{
    /// <summary>Calls of the voice stop (0x8EA0).</summary>
    public int StopVoice;

    /// <summary>Script_Step may run.</summary>
    public bool ScriptStep;

    /// <summary>How a message advanced: "click", "auto", "skip", "nowait", "restored" or null.</summary>
    public string? Advanced;

    /// <summary>A click completed the reveal.</summary>
    public bool RevealClicked;

    /// <summary>A click cancelled the <c>_WTTM</c> wait.</summary>
    public bool TimerClicked;

    /// <summary>The choice code (0xA6180) runs this frame.</summary>
    public bool ChoiceUpdate;
}

/// <summary>The engine globals of the wait logic (docs/formats/wait_model.md 2; <c>tsuki.wait_model.WaitState</c>).</summary>
public sealed class WaitState
{
    /// <summary>The reveal head value that means "all revealed".</summary>
    public const float Instant = 768f;

    /// <summary>0x1653E00: wait bits.</summary>
    public ulong Bits;

    /// <summary>0x1653E08: <c>_WTTM</c> / <c>_WTSV</c> frames left.</summary>
    public int Timer;

    /// <summary>0x1653E0E: auto countdown (frames).</summary>
    public int Auto;

    /// <summary>0x1653E18: 1 = the timer ignores clicks.</summary>
    public int TimerNoClick;

    /// <summary>0x1653E24: text fade step (positive while the text fades in).</summary>
    public float TextFadeStep;

    /// <summary>0x1653E30: <c>_STCF</c> layer mask.</summary>
    public ulong StcfLayers;

    /// <summary>0x1653E40: <c>_STCF</c> condition per layer.</summary>
    public byte[] StcfMask { get; } = new byte[40];

    /// <summary>0x1653E70: message flags (bit 0 reveal, bit 2 voice wait).</summary>
    public ulong Flags;

    /// <summary>0x1653E7C: 1 after a click advance until decide and the left button are released.</summary>
    public int AdvanceLatch;

    /// <summary>0x1653E80: reveal head.</summary>
    public float Head;

    /// <summary>0x1653E84: reveal tail.</summary>
    public float Tail;

    /// <summary>0x1653E88: fade width.</summary>
    public float Width;

    /// <summary>0x1653DF8: free-running counter.</summary>
    public int FrameCounter;

    /// <summary>0xDF29E0: d-pad down newly pressed, consumed by a click.</summary>
    public int AdvKey;

    /// <summary>0x86AA24: the next <c>_ZM</c> starts a new message slot.</summary>
    public int NewPage;

    /// <summary>0xDF0734: window target (1 = open).</summary>
    public int WindowTarget;

    /// <summary>0xDF0730: window fade.</summary>
    public float WindowAlpha = 1f;

    /// <summary>0x781AB0: 1 = the current message was read (speed +0x22).</summary>
    public int ReadSel;

    /// <summary>0xDD8E50: voice state (0 idle, 1 loading, 2 playing).</summary>
    public int VoiceState;

    /// <summary>0xDF0774: screen fade state; 1 and 2 block the script.</summary>
    public int FadeState;

    /// <summary>F988 != 0: fixed reveal step 0.675.</summary>
    public int F988;

    /// <summary>F1103: the window was closed.</summary>
    public int F1103;

    /// <summary>F1123: keep the voice when the text advances.</summary>
    public int F1123;

    /// <summary>F799: 1 = last message of the page.</summary>
    public int F799;

    /// <summary>F823: message counter of the page.</summary>
    public int F823;

    /// <summary>0x16541D8: block bits (1 / 2 extra blocks).</summary>
    public int Blocks = 1;

    /// <summary>0x16541F8: next-scene request.</summary>
    public int SceneJump;

    /// <summary>F859..: dimming timers of previous messages (-1 = dimmed).</summary>
    public int[] Dim { get; } = new int[12];

    /// <summary>The page "display end of page instantly" latch (VA 0xDF0EF0).</summary>
    public bool PageLatch;
}

/// <summary>
/// Ports of the two wait functions the ADV input flow calls every frame, Adv_Update (VA 0xA0B70) and tick_reveal (VA 0xA2630)
/// (docs/formats/wait_model.md 5; <c>tsuki.wait_model.adv_update</c>, <c>tick_reveal</c>; verified against the eboot's own code under
/// emulation). All float arithmetic is float32.
/// </summary>
public static class WaitModel
{
    private const float HeadAll = 76800f;
    private const float F988Step = 0.675f;
    private const float FastMult = 60f;

    /// <summary>The auto countdown conversion <c>trunc(ms / 1000.0f * 60.0f)</c> (VA 0xA2968).</summary>
    public static int MsToFrames(int ms)
    {
        float x = (short)ms / 1000.0f * 60.0f;
        return float.IsFinite(x) && Math.Abs(x) < 2147483648.0 ? (int)x : int.MinValue;
    }

    /// <summary><c>_WTTM</c> / <c>_WTSV</c> frames: <c>(3 * ms) / 50</c> with C truncation, at least 1 (VA 0x9ACF7).</summary>
    public static int WaitFrames(int ms)
    {
        int t = unchecked(3 * ms);
        int q = t / 50;
        return q >= 2 ? q : 1;
    }

    /// <summary>VA 0x56160: counters per reveal step (768 when the speed is not positive).</summary>
    public static float RevealSpeed(in WaitConfig cfg, int readSel, int vsync = 1)
    {
        short v = unchecked((short)(readSel == 1 ? cfg.TextSpeedRead : cfg.TextSpeedUnread));
        if (v <= 0)
        {
            return WaitState.Instant;
        }

        float d = (short)Math.Min(vsync, 3);
        return d / 60.0f * 1000.0f / v;
    }

    /// <summary>VA 0x561D0: the fade width (0 when the speed is not positive or instant).</summary>
    public static float RevealWidth(in WaitConfig cfg, int readSel, int vsync = 1)
    {
        short v = unchecked((short)(readSel == 1 ? cfg.TextSpeedRead : cfg.TextSpeedUnread));
        if (v <= 0)
        {
            return 0f;
        }

        float d = (short)Math.Min(vsync, 3);
        float sp = d / 60.0f * 1000.0f / v;
        if (sp == WaitState.Instant)
        {
            return 0f;
        }

        float x = v * 0.1f / 35.0f;
        x = 0.5f < x ? 0.5f : x;
        return sp * x * 60.0f;
    }

    /// <summary>
    /// The 0xCA070 result for a block's glyph records: the alpha of the last record with non-zero flags equals 1.0; an empty block is done.
    /// </summary>
    public static bool RevealDone(ReadOnlySpan<(int Flags, int Ctr)> records, float head, float tail)
    {
        float? alpha = null;
        foreach ((int flags, int ctr) in records)
        {
            if (flags == 0)
            {
                continue;
            }

            float c = ctr;
            if (c > head)
            {
                alpha = 0f;
            }
            else if (tail > c)
            {
                alpha = 1f;
            }
            else
            {
                float d = head - tail;
                alpha = 0f >= d ? 1f : (head - c) / d;
            }
        }

        return alpha is null || alpha == 1f;
    }

    private static bool ClickReveal(WaitState st, in WaitInput inp) =>
        st.AdvKey != 0 || (inp.Mouse & 2) != 0 || (inp.Newly & WaitInput.Decide) != 0 || inp.Wheel < 0;

    private static bool ClickTimer(WaitState st, in WaitInput inp) =>
        st.AdvKey != 0 || (inp.Mouse & 2) != 0 || (inp.Newly & WaitInput.Decide) != 0;

    private static bool StopVoiceCondition(WaitState st, in WaitConfig cfg, in AdvMode mode) =>
        (st.F1123 == 0 && cfg.InterruptVoice != 0) || mode.SkipActive != 0;

    /// <summary>
    /// VA 0xA2630 for one frame. <paramref name="done"/> returns the 0xCA070 value of a block (slot + 4, 6, 8): 1 = the last record is fully
    /// visible, 0 = not yet, -1 = block not allocated. <paramref name="n"/> overrides the frame count (the read-only rule after Adv_Update).
    /// </summary>
    public static WaitEffects TickReveal(WaitState st, in WaitInput inp, in WaitConfig cfg, in AdvMode mode, Func<int, int> done, int? n = null)
    {
        ArgumentNullException.ThrowIfNull(st);
        ArgumentNullException.ThrowIfNull(done);
        var eff = new WaitEffects();
        int count = n ?? mode.Frames;
        bool fast = mode.Step >= 0x3D;
        st.FrameCounter = unchecked((short)(st.FrameCounter + count));
        float mult = fast ? FastMult : 1.0f;
        for (int i = 0; i < count; i++)
        {
            if ((st.Flags & WaitBits.Reveal) == 0 && (st.Bits & WaitBits.Reveal) == 0)
            {
                continue;
            }

            if (st.WindowTarget != 0 && mode.LogShown == 0 && (st.TextFadeStep > 0 || st.WindowAlpha < 1f) && st.F1103 == 0)
            {
                continue;
            }

            st.WindowTarget = 1;
            bool haveHead = true;
            float sp;
            if (st.F988 == 0)
            {
                sp = RevealSpeed(cfg, st.ReadSel, mode.Vsync);
                st.Width = RevealWidth(cfg, st.ReadSel, mode.Vsync);
                if (sp >= WaitState.Instant)
                {
                    st.Head = WaitState.Instant;
                    st.Tail = WaitState.Instant;
                    haveHead = false;
                }
            }
            else
            {
                sp = F988Step;
            }

            if (haveHead)
            {
                if (WaitState.Instant > st.Head)
                {
                    st.Head = (sp * mult) + st.Head;
                }

                float x = st.Head - st.Width;
                if (x > st.Tail)
                {
                    st.Tail = x;
                }
            }

            if (st.AdvanceLatch == 0 && ClickReveal(st, inp))
            {
                st.Head = HeadAll;
                eff.RevealClicked = true;
                int k = st.F823;
                if (k >= 2)
                {
                    for (int j = 0; j < k - 1 && j < st.Dim.Length; j++)
                    {
                        st.Dim[j] = -1;
                    }
                }
            }
        }

        if (st.Timer > 0)
        {
            st.Timer = Math.Max(0, (int)unchecked((short)(st.Timer - count)));
            if (!(st.TimerNoClick > 0) && st.AdvanceLatch == 0 && ClickTimer(st, inp))
            {
                st.Timer = 0;
                eff.TimerClicked = true;
            }
        }

        if ((mode.SkipActive > 0 || (mode.ModeWord & 2) != 0) && st.Auto >= 0x3D)
        {
            st.Auto = 0x3C;
        }

        if (st.Auto > 0)
        {
            int lim = MsToFrames(st.F799 == 1 ? cfg.AutoPageMs : cfg.AutoWaitMs);
            int v = st.Auto;
            if (lim < v)
            {
                v = lim;
            }

            v = unchecked((short)(v - count));
            st.Auto = v > 0 ? v : 0;
        }

        int r = done(4);
        if ((st.Blocks & 2) != 0)
        {
            r &= done(6);
        }

        if ((st.Blocks & 4) != 0)
        {
            r &= done(8);
        }

        if (r != 0)
        {
            if ((st.Bits & WaitBits.Reveal) != 0)
            {
                if ((st.Bits & WaitBits.Click) != 0)
                {
                    st.Auto = unchecked((short)MsToFrames(st.F799 == 1 ? cfg.AutoPageMs : cfg.AutoWaitMs));
                }

                st.Bits = (st.Bits & ~(WaitBits.Reveal | WaitBits.Shown)) | WaitBits.Shown;
            }

            st.Flags &= ~WaitBits.Reveal;
        }

        if (fast && (mode.Rewind != 0 || mode.SkipActive > 0 || (mode.ModeWord & 2) != 0))
        {
            st.Auto = 0;
        }

        if ((st.Bits & WaitBits.Choice) != 0)
        {
            st.WindowTarget = 1;
            eff.ChoiceUpdate = true;
        }

        return eff;
    }

    /// <summary>
    /// VA 0xA0B70 for one frame (live paths). <paramref name="stcf"/> is 0x4AB80 (layer, mask). The result says whether Script_Step runs; the caller
    /// then executes statements up to the next yield.
    /// </summary>
    public static WaitEffects AdvUpdate(WaitState st, in WaitInput inp, in WaitConfig cfg, in AdvMode mode, Func<int, int, bool> stcf)
    {
        ArgumentNullException.ThrowIfNull(st);
        ArgumentNullException.ThrowIfNull(stcf);
        var eff = new WaitEffects();
        if ((inp.Mouse & 1) == 0 && (inp.Held & WaitInput.Decide) == 0)
        {
            st.AdvanceLatch = 0;
        }

        if ((st.Bits & WaitBits.Voice) != 0 && st.VoiceState == 0)
        {
            st.Bits &= ~WaitBits.Voice;
            if (st.Auto < 0)
            {
                st.Auto = 0;
            }
        }

        if ((st.Bits & WaitBits.Stcf) != 0)
        {
            ulong m = st.StcfLayers;
            for (int l = 0; l < 40; l++)
            {
                if (((m >> l) & 1) != 0 && !stcf(l, st.StcfMask[l]))
                {
                    m &= ~(1UL << l);
                }
            }

            st.StcfLayers = m;
            if (m == 0)
            {
                st.Bits &= ~WaitBits.Stcf;
            }
        }

        if ((st.Bits & WaitBits.Reveal) == 0 && (st.Flags & WaitBits.Reveal) == 0 && (st.Bits & WaitBits.Voice) != 0
            && (st.AdvKey != 0 || (inp.Newly & WaitInput.Decide) != 0 || (inp.Mouse & 2) != 0 || mode.SkipActive != 0 || inp.Wheel < 0))
        {
            if (StopVoiceCondition(st, cfg, mode))
            {
                eff.StopVoice++;
            }

            st.Flags &= ~WaitBits.Voice;
            st.Bits &= ~WaitBits.Voice;
        }

        if ((st.Bits & WaitBits.Shown) != 0 && st.Timer == 0)
        {
            if (ClickReveal(st, inp))
            {
                st.AdvKey = 0;
                st.Auto = 0;
                if ((st.Bits & WaitBits.Message) != 0)
                {
                    st.Bits &= ~WaitBits.Message;
                    st.Flags = 0;
                    st.NewPage = 1;
                }

                bool cl = StopVoiceCondition(st, cfg, mode);
                if ((st.Bits & WaitBits.Voice) != 0)
                {
                    if (cl)
                    {
                        eff.StopVoice++;
                    }

                    st.Flags &= ~WaitBits.Voice;
                    st.Bits &= ~WaitBits.Voice;
                }
                else if (cl)
                {
                    eff.StopVoice++;
                }

                st.Bits &= ~(WaitBits.Reveal | WaitBits.Shown | WaitBits.Click);
                st.AdvanceLatch = 1;
                eff.Advanced = "click";
            }
            else
            {
                bool go;
                if ((st.Bits & WaitBits.Click) != 0 && (mode.ModeWord & 1) == 0 && !(mode.SkipActive > 0))
                {
                    go = false;
                }
                else if ((st.Bits & WaitBits.Voice) == 0 && st.Auto == 0)
                {
                    go = true;
                }
                else
                {
                    go = mode.SkipActive != 0 && st.Auto == 0;
                }

                if (go)
                {
                    bool hadClick = (st.Bits & WaitBits.Click) != 0;
                    if ((st.Bits & WaitBits.Message) != 0)
                    {
                        st.Bits &= ~WaitBits.Message;
                        st.Flags = 0;
                        st.NewPage = 1;
                    }

                    st.Bits &= ~(WaitBits.Reveal | WaitBits.Shown | WaitBits.Click);
                    eff.Advanced = !hadClick ? "nowait" : mode.SkipActive != 0 ? "skip" : "auto";
                }
            }
        }

        if (st.Bits == (WaitBits.Click | WaitBits.Message))
        {
            if (st.AdvKey != 0 || (inp.Mouse & 2) != 0 || (inp.Newly & WaitInput.Decide) != 0 || inp.Wheel < 0)
            {
                st.AdvKey = 0;
                st.Bits &= ~(WaitBits.Click | WaitBits.Message);
                eff.Advanced = "restored";
            }
            else if (mode.SkipActive > 0 || (mode.ModeWord & 2) != 0 || ((mode.ModeWord & 1) != 0 && st.Auto <= 0))
            {
                st.Bits &= ~(WaitBits.Click | WaitBits.Message);
                eff.Advanced = "restored";
            }
        }
        else if (st.Bits == WaitBits.Message)
        {
            st.Bits = 0;
            st.NewPage = 1;
        }

        if ((inp.Held & WaitInput.R2) != 0 && st.F1123 == 0 && cfg.InterruptVoice != 0)
        {
            eff.StopVoice++;
        }

        if (st.Bits == 0 && st.Timer == 0 && st.Auto == 0 && st.FadeState is not (1 or 2))
        {
            eff.ScriptStep = true;
        }

        return eff;
    }
}
