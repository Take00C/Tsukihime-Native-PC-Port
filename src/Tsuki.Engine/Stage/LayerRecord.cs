namespace Tsuki.Engine.Stage;

/// <summary>
/// One tween slot of a layer record (docs/formats/layers.md 4.3): the easing id, the counter in Q12 (starts at 0x1000 and counts down) and
/// the per-frame step. Five slots per record: move +0x84, scale +0x8A, rotation +0x90, alpha +0x96, zoom +0x9C.
/// </summary>
public struct Tween
{
    /// <summary>Easing id (+0).</summary>
    public short Ease;

    /// <summary>Counter in Q12 (+2); 0 = no tween.</summary>
    public short Counter;

    /// <summary>Step per frame (+4); negative while running.</summary>
    public short Step;

    /// <summary>Creates a tween slot.</summary>
    public Tween(short ease, short counter, short step)
    {
        Ease = ease;
        Counter = counter;
        Step = step;
    }

    /// <summary>
    /// Starts a tween over <paramref name="frames"/> 60 Hz frames (already wrapped to i16 as the engine stores them; setters VA 0x4A620, 0x49930, 0x4A4B0, 0x83350): counter 0x1000, step
    /// <c>-trunc(4096 / frames)</c>; no tween for 0 frames or fewer. The easing id is kept.
    /// </summary>
    public static Tween Start(short ease, int frames)
    {
        if (frames <= 0)
        {
            return new Tween(ease, 0, 0);
        }

        return new Tween(ease, 0x1000, StageMath.I16(-(0x1000 / frames)));
    }

    /// <summary>
    /// VA 0x49390 / 0x528DC: the fraction of the tween still to go, <c>1 - ease(id, p - 1, 0, 1, -1)</c> with <c>p = counter / 4096</c>; 0 once
    /// finished.
    /// </summary>
    public readonly float Remaining()
    {
        float p = StageMath.F(Counter * StageMath.F(1.0 / 4096.0));
        if (!(p > 0))
        {
            return 0f;
        }

        return StageMath.F(1.0 - StageMath.F(Easing.Ease(Ease, StageMath.F(p - 1.0), 0.0, 1.0, -1.0)));
    }

    /// <summary>
    /// One update of the counter (VA 0x57B2D..): a result at or below 0, or a step of 0 or more, ends the tween (counter and step 0).
    /// <paramref name="dt"/> is the number of frames of this update.
    /// </summary>
    public void Tick(float dt)
    {
        if (Counter <= 0)
        {
            return;
        }

        if (Step >= 0)
        {
            Counter = 0;
            Step = 0;
            return;
        }

        int c = StageMath.Cvtt(StageMath.F(Counter + StageMath.F(Step * dt)));
        if (c <= 0)
        {
            Counter = 0;
            Step = 0;
        }
        else
        {
            Counter = StageMath.I16(c);
        }
    }
}

/// <summary>
/// One shake slot of a drawn record (record + 0x178.., slot 0 = quake, slot 1 = shake or swing; docs/formats/quake.md 3.1). Port of
/// <c>ShakeSlot</c> of <c>tsuki.quake</c>.
/// </summary>
public sealed class ShakeSlot
{
    /// <summary>0 none, 1 quake (SQK2), 2 shake (SHK2), 3 swing (SSW2).</summary>
    public int Kind;

    /// <summary>Start delay in frames (the field layers.md called "fade").</summary>
    public int Delay;

    /// <summary>Duration in frames; -1 until stopped; 0 never runs.</summary>
    public int Time;

    /// <summary>The six parameters p0..p5.</summary>
    public readonly short[] Par = new short[6];

    /// <summary>Frames since the start (after the delay), saturating at 30000.</summary>
    public int Elapsed;

    /// <summary>Per-kind phase counter.</summary>
    public int Phase;

    /// <summary>Output offset x in canvas units.</summary>
    public float X;

    /// <summary>Output offset y in canvas units.</summary>
    public float Y;

    /// <summary>Swing runtime w0..w6: amplitude x / y, period x / y, sign x / y, phase y.</summary>
    public readonly short[] W = new short[7];

    /// <summary>Clears every field (outputs included).</summary>
    public void Clear()
    {
        Kind = 0;
        Delay = 0;
        Time = 0;
        Elapsed = 0;
        Phase = 0;
        Array.Clear(Par);
        Array.Clear(W);
        X = 0f;
        Y = 0f;
    }

    /// <summary>A deep copy.</summary>
    public ShakeSlot Copy()
    {
        var c = new ShakeSlot { Kind = Kind, Delay = Delay, Time = Time, Elapsed = Elapsed, Phase = Phase, X = X, Y = Y };
        Par.CopyTo(c.Par, 0);
        W.CopyTo(c.W, 0);
        return c;
    }

    /// <summary>Copies every field from <paramref name="o"/>.</summary>
    public void CopyFrom(ShakeSlot o)
    {
        Kind = o.Kind;
        Delay = o.Delay;
        Time = o.Time;
        Elapsed = o.Elapsed;
        Phase = o.Phase;
        X = o.X;
        Y = o.Y;
        o.Par.CopyTo(Par, 0);
        o.W.CopyTo(W, 0);
    }
}

/// <summary>
/// A layer record of the render arrays (40 x 0x2C8 bytes in the engine): <b>A</b> the drawn record (VA 0xDDB5E8), <b>B</b> the committed
/// record (0xDE2650). Only the fields the layer model reads or writes are kept (the oracle's <c>RENDER_FIELDS</c> plus the shake slots);
/// docs/formats/layers.md 5 lists the offsets.
/// </summary>
public sealed class LayerRecord
{
    /// <summary>+0x10: 1 while the layer is drawn (set when B is moved to A).</summary>
    public int Used;

    /// <summary>+0x14: 1 while the picture's face layer (catalog name1) is bound.</summary>
    public int Face;

    /// <summary>+0x18: picture id, -1 none.</summary>
    public int Pic = -1;

    /// <summary>+0x1C: blend mode (STBR, motion field 17).</summary>
    public short Blend;

    /// <summary>+0x1E: 0x40 SACL (skip the mask offset), 0x80 STBR a3.</summary>
    public byte Flags;

    /// <summary>+0x1F: colour-effect mode (STEC).</summary>
    public sbyte FxMode;

    /// <summary>+0x20: colour-effect parameter A (SNX2, motion field 24).</summary>
    public short FxA;

    /// <summary>+0x22: colour-effect parameter B (SNX3, motion field 25).</summary>
    public sbyte FxB;

    /// <summary>+0x24: scene state byte.</summary>
    public int State;

    /// <summary>+0x2C: alpha 0..1.</summary>
    public float Alpha = 1f;

    /// <summary>+0x30: position x in engine units (640-wide canvas).</summary>
    public float X;

    /// <summary>+0x34: position y.</summary>
    public float Y;

    /// <summary>+0x48: extra offset x.</summary>
    public float Dx;

    /// <summary>+0x4C: extra offset y.</summary>
    public float Dy;

    /// <summary>+0x50: picture offset x, minus the anchor (canvas units at scale 1).</summary>
    public float OffX;

    /// <summary>+0x54: picture offset y.</summary>
    public float OffY;

    /// <summary>+0x58: rotation in 1/100 degree.</summary>
    public long Rot;

    /// <summary>+0x70: scale per mille.</summary>
    public short Scale = 1000;

    /// <summary>+0x76: anchor x (the pivot, integer canvas units).</summary>
    public short AnchorX;

    /// <summary>+0x78: anchor y.</summary>
    public short AnchorY;

    /// <summary>+0x7A: rotation tween start minus end.</summary>
    public short RotDelta;

    /// <summary>+0x7C: current rotation tween offset (set by the draw: <c>trunc(remaining * RotDelta)</c>).</summary>
    public short RotCur;

    /// <summary>+0x84: move tween (STCL).</summary>
    public Tween TwMove;

    /// <summary>+0x8A: scale tween.</summary>
    public Tween TwScale;

    /// <summary>+0x90: rotation tween (STRT).</summary>
    public Tween TwRot;

    /// <summary>+0x96: alpha tween (STGS).</summary>
    public Tween TwAlpha;

    /// <summary>+0x9C: zoom tween (STZ4).</summary>
    public Tween TwZoom;

    /// <summary>+0xAA: zoom x per mille (negative = mirrored).</summary>
    public short ZoomX = 1000;

    /// <summary>+0xAC: zoom y per mille.</summary>
    public short ZoomY = 1000;

    /// <summary>+0xB0: STZ4 pivot flag: zoom in screen space about the layer position.</summary>
    public byte ZoomPivot;

    /// <summary>+0xCC: current alpha tween offset (<c>remaining * AlphaDelta</c>).</summary>
    public float AlphaCur;

    /// <summary>+0xE0: scale tween start minus end.</summary>
    public float ScaleDelta;

    /// <summary>+0xE4: current scale tween offset.</summary>
    public float ScaleCur;

    /// <summary>+0xFC: zoom tween start minus end, x (zoom units / 1000).</summary>
    public float ZoomDx;

    /// <summary>+0x100: zoom tween start minus end, y.</summary>
    public float ZoomDy;

    /// <summary>+0x104: move tween start minus end, x.</summary>
    public float MoveDx;

    /// <summary>+0x108: move tween start minus end, y.</summary>
    public float MoveDy;

    /// <summary>+0x110: alpha tween start minus end.</summary>
    public float AlphaDelta;

    /// <summary>+0x120: mask layer (STCH a11), -1 none.</summary>
    public int Mask = -1;

    /// <summary>+0x128: parent layer (SBAS), -1 none.</summary>
    public int Parent = -1;

    /// <summary>+0x12C: STTR rule value, -1 none.</summary>
    public int Sttr = -1;

    /// <summary>+0x130: SOXY x.</summary>
    public sbyte SoxyX;

    /// <summary>+0x131: SOXY y.</summary>
    public sbyte SoxyY;

    /// <summary>+0x132: crop rectangle x, y, w, h (STCH a12..a15, motion fields 11..14).</summary>
    public readonly short[] Crop = new short[4];

    /// <summary>+0x13A: STCH a16.</summary>
    public ushort A16;

    /// <summary>+0x13C: STCH a17.</summary>
    public byte A17;

    /// <summary>+0x15C: STCH a19 / STRR angle (no reader in the draw path).</summary>
    public short Angle;

    /// <summary>+0x160: motion id.</summary>
    public short MotionId;

    /// <summary>+0x162: motion divisor (STMA a3).</summary>
    public short MotionDivisor;

    /// <summary>+0x168: motion frame counter.</summary>
    public long MotionFrames;

    /// <summary>+0x170: motion pause byte (SPMA).</summary>
    public byte MotionPause;

    /// <summary>+0x1D0: STNS noise mode.</summary>
    public byte Stns;

    /// <summary>+0x1D1: STNS second byte.</summary>
    public byte StnsB;

    /// <summary>+0x1F0: the 17-byte colour block (0xF7A0): +5..+7 RGB, +8..+0xA second RGB, +0xB.. the previous values.</summary>
    public readonly byte[] Colour = NewColour();

    /// <summary>The colour-effect key of the picture's texture (<c>TextureRef.D</c>): bound when the picture is (re)loaded by the commit.</summary>
    public int GradeKey;

    /// <summary>+0x2C8: STRA value (the periodic effect id).</summary>
    public short Stra;

    /// <summary>+0x2CA: STRA phase in frames.</summary>
    public short StraPhase;

    /// <summary>+0x2CC: STRA fade-in length in frames.</summary>
    public short StraFadeIn;

    /// <summary>+0x2CE: STRA fade-out length.</summary>
    public short StraFadeTotal;

    /// <summary>+0x2D0: STRA fade-out frames left.</summary>
    public short StraFadeLeft;

    /// <summary>+0x6F50: the committed z (draw order), copied from B by the commit.</summary>
    public int ZOrder;

    /// <summary>+0x124: 1 while the layer is drawn offscreen as a mask / crop target (set by the offscreen pass).</summary>
    public bool Offscreen;

    /// <summary>The two shake slots +0x178...</summary>
    public readonly ShakeSlot[] Slots = [new ShakeSlot(), new ShakeSlot()];

    /// <summary>The colour block after a reset (VA 0xF700 on a white block): +5..+7 and +0xB..+0xD white.</summary>
    public static byte[] NewColour()
    {
        var c = new byte[17];
        c[5] = 255;
        c[6] = 255;
        c[7] = 255;
        c[0xB] = 255;
        c[0xC] = 255;
        c[0xD] = 255;
        return c;
    }

    /// <summary>The motion record as 24 bytes (id, divisor, 4 unused, frames, pause, 7 unused).</summary>
    public void MotionRecord(Span<byte> dst24)
    {
        dst24.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dst24, MotionId);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dst24[2..], MotionDivisor);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(dst24[8..], MotionFrames);
        dst24[16] = MotionPause;
    }

    /// <summary>A deep copy of the record.</summary>
    public LayerRecord Clone()
    {
        var c = new LayerRecord();
        c.CopyFrom(this);
        return c;
    }

    /// <summary>Copies every field of <paramref name="o"/> into this record (the arrays by value).</summary>
    public void CopyFrom(LayerRecord o)
    {
        ArgumentNullException.ThrowIfNull(o);
        Used = o.Used;
        Face = o.Face;
        Pic = o.Pic;
        Blend = o.Blend;
        Flags = o.Flags;
        FxMode = o.FxMode;
        FxA = o.FxA;
        FxB = o.FxB;
        State = o.State;
        Alpha = o.Alpha;
        X = o.X;
        Y = o.Y;
        Dx = o.Dx;
        Dy = o.Dy;
        OffX = o.OffX;
        OffY = o.OffY;
        Rot = o.Rot;
        Scale = o.Scale;
        AnchorX = o.AnchorX;
        AnchorY = o.AnchorY;
        RotDelta = o.RotDelta;
        RotCur = o.RotCur;
        TwMove = o.TwMove;
        TwScale = o.TwScale;
        TwRot = o.TwRot;
        TwAlpha = o.TwAlpha;
        TwZoom = o.TwZoom;
        ZoomX = o.ZoomX;
        ZoomY = o.ZoomY;
        ZoomPivot = o.ZoomPivot;
        AlphaCur = o.AlphaCur;
        ScaleDelta = o.ScaleDelta;
        ScaleCur = o.ScaleCur;
        ZoomDx = o.ZoomDx;
        ZoomDy = o.ZoomDy;
        MoveDx = o.MoveDx;
        MoveDy = o.MoveDy;
        AlphaDelta = o.AlphaDelta;
        Mask = o.Mask;
        Parent = o.Parent;
        Sttr = o.Sttr;
        SoxyX = o.SoxyX;
        SoxyY = o.SoxyY;
        o.Crop.CopyTo(Crop, 0);
        A16 = o.A16;
        A17 = o.A17;
        Angle = o.Angle;
        MotionId = o.MotionId;
        MotionDivisor = o.MotionDivisor;
        MotionFrames = o.MotionFrames;
        MotionPause = o.MotionPause;
        Stns = o.Stns;
        StnsB = o.StnsB;
        o.Colour.CopyTo(Colour, 0);
        GradeKey = o.GradeKey;
        Stra = o.Stra;
        StraPhase = o.StraPhase;
        StraFadeIn = o.StraFadeIn;
        StraFadeTotal = o.StraFadeTotal;
        StraFadeLeft = o.StraFadeLeft;
        ZOrder = o.ZOrder;
        Offscreen = o.Offscreen;
        Slots[0].CopyFrom(o.Slots[0]);
        Slots[1].CopyFrom(o.Slots[1]);
    }
}
