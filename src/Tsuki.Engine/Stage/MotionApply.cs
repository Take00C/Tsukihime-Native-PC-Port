using Tsuki.Engine.Contracts;
using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

/// <summary>
/// The motion record helpers (24 bytes per layer: id, divisor, frame counter, pause; VA 0xBA800, 0xBA8E0, STMA 0xC0330) and one application
/// of a motion to a layer (VA 0xBE4C0; docs/formats/layers.md 8.2, 8.3). Port of <c>motion_record</c>, <c>motion_clear</c>, <c>motion_copy</c>
/// and <c>apply_motion</c> of <c>tsuki.misc_layer_state_and_motions</c>.
/// </summary>
public static class MotionApply
{
    // keyframe field -> enable mask in the per-motion flag word
    private const ulong Mask3 = 0x40880;
    private const ulong Mask4 = 0x5100000;
    private const ulong Mask5 = 0x4000;
    private const ulong Mask6 = 0x10002;
    private const ulong Mask7 = 0x20004;
    private const ulong Mask8 = 0x88000;
    private const ulong Mask9 = 0x80200000;
    private const ulong Mask10 = 0x100400000;
    private const ulong Mask11And13 = 0x10000080;
    private const ulong Mask12And14 = 0x4000400;
    private const ulong Mask15 = 0x10;
    private const ulong Mask16 = 0x20;
    private const ulong Mask17 = 0x2000000;
    private const ulong Mask18 = 0x200;
    private const ulong Mask19 = 0x200000000;
    private const ulong Mask22 = 0x20000000;
    private const ulong Mask23 = 0x40000000;
    private const ulong Mask24 = 0x40;
    private const ulong Mask25 = 0x100;
    private const ulong Mask26 = 0x1000;
    private const ulong Mask27 = 0x1;
    private const ulong AngleBit = 1UL << 34;

    /// <summary>
    /// Record change of <c>_STMA(l, id, w, n)</c> (VA 0xC0330), made to the scene record and to the drawn record. <paramref name="id"/> (the
    /// 32-bit argument) above 0: id = i16(id), pause 0 (0xBA820), frames 0 (0xBA830), divisor n if i16(n) is above 0 else 0 (0xBA840).
    /// Otherwise (absent, 0, negative) only id = 0 and pause = 0: divisor and frame counter are kept.
    /// </summary>
    public static void MotionRecord(ref short recId, ref short recDivisor, ref long recFrames, ref byte recPause, int id, int n)
    {
        recId = id > 0 ? I16(id) : (short)0;
        recPause = 0;
        if (id > 0)
        {
            recFrames = 0;
            recDivisor = Math.Max(I16(n), (short)0);
        }
    }

    /// <summary>Applies <c>MotionRecord</c> to a drawn / committed record.</summary>
    public static void MotionRecord(LayerRecord r, int id, int n) =>
        MotionRecord(ref r.MotionId, ref r.MotionDivisor, ref r.MotionFrames, ref r.MotionPause, id, n);

    /// <summary>VA 0xBA800: id = divisor = 0, frames = 0, pause = 0.</summary>
    public static void MotionClear(LayerRecord r)
    {
        r.MotionId = 0;
        r.MotionDivisor = 0;
        r.MotionFrames = 0;
        r.MotionPause = 0;
    }

    /// <summary>
    /// VA 0xBA8E0(dst, src): id, divisor and pause byte are copied; the frame counter only when the source id is above 0 and the destination
    /// counter is not larger.
    /// </summary>
    public static void MotionCopy(ref short dstId, ref short dstDivisor, ref long dstFrames, ref byte dstPause, short srcId, short srcDivisor, long srcFrames, byte srcPause)
    {
        long dstF = dstFrames;
        dstId = srcId;
        if (dstF <= srcFrames && srcId > 0)
        {
            dstFrames = srcFrames;
        }

        dstDivisor = srcDivisor;
        dstPause = srcPause;
    }

    /// <summary><see cref="MotionCopy(ref short, ref short, ref long, ref byte, short, short, long, byte)"/> between two records.</summary>
    public static void MotionCopy(LayerRecord dst, LayerRecord src) =>
        MotionCopy(ref dst.MotionId, ref dst.MotionDivisor, ref dst.MotionFrames, ref dst.MotionPause, src.MotionId, src.MotionDivisor, src.MotionFrames, src.MotionPause);

    /// <summary>The scene copy of a layer's motion record (24 bytes at 0xD00 + 24 l) as record fields.</summary>
    public static (short Id, short Divisor, long Frames, byte Pause) SceneMotion(SceneState s, int l) =>
        (s.GetMotionId(l), s.GetMotionDivisor(l), s.GetMotionFrames(l), s.GetMotionPause(l));

    /// <summary>
    /// VA 0xF7A0 on a colour block (record +0x1F0, 17 bytes): +0 i16 = 0, +2 i16 = <paramref name="par"/>, the old +5..+7 / +8..+0xA move to
    /// +0xB..+0xD / +0xE..+0x10, then +5..+7 = <paramref name="rgb"/> and +8..+0xA = <paramref name="rgb2"/> (bytes R, G, B).
    /// </summary>
    public static void ColourBlock(byte[] block, int rgb, int rgb2 = 0, int par = 0)
    {
        block[0] = 0;
        block[1] = 0;
        short p = I16(par);
        block[2] = (byte)(p & 0xFF);
        block[3] = (byte)((p >> 8) & 0xFF);
        block[0xB] = block[5];
        block[0xC] = block[6];
        block[0xD] = block[7];
        block[5] = (byte)((rgb >> 16) & 0xFF);
        block[6] = (byte)((rgb >> 8) & 0xFF);
        block[7] = (byte)(rgb & 0xFF);
        block[0xE] = block[8];
        block[0xF] = block[9];
        block[0x10] = block[0xA];
        block[8] = (byte)((rgb2 >> 16) & 0xFF);
        block[9] = (byte)((rgb2 >> 8) & 0xFF);
        block[0xA] = (byte)(rgb2 & 0xFF);
    }

    /// <summary>VA 0xF700: the colour block of a drawn record is reset to white with a white history (+5..+7 and +0xB..+0xD = FF, the rest 0).</summary>
    public static void ColourReset(byte[] block)
    {
        Array.Clear(block);
        block[5] = 255;
        block[6] = 255;
        block[7] = 255;
        block[0xB] = 255;
        block[0xC] = 255;
        block[0xD] = 255;
    }

    /// <summary>VA 0xFA90(ext + 6, rgb, 0): scene ext bytes +6..+8 = R, G, B and +9..+0xB = 0.</summary>
    private static void ExtColour(SceneState scene, int layer, int rgb)
    {
        Span<byte> e = scene.Ext(layer);
        e[6] = (byte)((rgb >> 16) & 0xFF);
        e[7] = (byte)((rgb >> 8) & 0xFF);
        e[8] = (byte)(rgb & 0xFF);
        e[9] = 0;
        e[10] = 0;
        e[11] = 0;
    }

    private static void SetExtI16(SceneState scene, int layer, int pos, int value)
    {
        Span<byte> e = scene.Ext(layer);
        short v = I16(value);
        e[pos] = (byte)(v & 0xFF);
        e[pos + 1] = (byte)((v >> 8) & 0xFF);
    }

    /// <summary>
    /// One call of VA 0xBE4C0 for <paramref name="layer"/> on its drawn record <paramref name="rec"/>: advance the motion by <paramref name="dt"/> frames,
    /// evaluate the enabled fields at the new time and write them to the record and, like the engine, to <paramref name="scene"/> (scene writes
    /// need a scene picture, except the motion time and the colour key). Returns the key time used (ms) or null when nothing was applied.
    /// </summary>
    public static double? Apply(MotionTable table, IPictureCatalog catalog, LayerRecord rec, int layer, double dt, SceneState? scene)
    {
        if (layer is < 0 or > 39)
        {
            return null;
        }

        int mid = rec.MotionId;
        int div = rec.MotionDivisor;
        MotionRef? mm = table.Get(mid);
        if (mid <= 0 || mm is null || mm.Value.End <= mm.Value.Start || mm.Value.Duration <= 0)
        {
            return null;
        }

        MotionRef m = mm.Value;
        int idt = Cvtt(dt);
        if (idt != 0 && rec.MotionPause == 0)
        {
            long frames = rec.MotionFrames + idt;
            rec.MotionFrames = frames >= 2 ? frames : 1;
        }

        long qf = MotionMath.MotionFrames(rec.MotionFrames, div);
        double t0 = MotionMath.MotionTimeMs(rec.MotionFrames, div);
        double? tk = MotionMath.KeyTime(table, m, t0, table.Loop(mid));
        if (tk is null)
        {
            return null;
        }

        double t = tk.Value;
        if (scene is not null)
        {
            scene.SetMotionFrames(layer, qf);
        }

        bool whole = mid == 9999;
        if (whole)
        {
            long eid = table.Val(m.Start, 1);
            double last = table.Val(m.End - 1, 0);
            t = F(Easing.Ease(eid is MotionTable.Unset or MotionTable.Hold ? 0 : (int)eid, t, 0.0, last, last));
        }

        int k = -1;
        for (int i = m.Start; i < m.End; i++)
        {
            if ((double)table.Val(i, 0) >= t)
            {
                k = i;
                break;
            }
        }

        if (k < 0)
        {
            return null;
        }

        int s = m.Start;
        int e = m.End - 1;
        ulong flags = table.Flags(mid);
        double tt = t;

        double Val(int f, double cur) => MotionMath.MotionValue(table, f, s, e, k, tt, cur, whole);
        bool Found(int f) => MotionMath.Found(table, m, k, f);
        int SPic() => scene is not null ? scene.GetPic(layer) : -1;
        bool SceneOk() => scene is not null && SPic() != -1;

        // 3, 4: position (0xBE7D2.., 0xBE912..)
        if (!Found(3))
        {
            flags &= ~Mask3;
        }

        if ((flags & Mask3) != 0)
        {
            double v = Val(3, F(F(480.0 * rec.X) / 640.0));
            rec.X = (float)F(F(v * 640.0) / 480.0);
            if (SceneOk())
            {
                scene!.SetX(layer, I16(Cvtt(rec.X)));
            }
        }

        if (!Found(4))
        {
            flags &= ~Mask4;
        }

        if ((flags & Mask4) != 0)
        {
            double v = Val(4, F(F(360.0 * rec.Y) / 480.0));
            rec.Y = (float)F(F(v * 480.0) / 360.0);
            if (SceneOk())
            {
                scene!.SetY(layer, I16(Cvtt(rec.Y)));
            }
        }

        // 5: alpha (0xBEA47..); the running alpha-tween value is folded in only when dt == 0
        if (!Found(5))
        {
            flags &= ~Mask5;
        }

        if ((flags & Mask5) != 0)
        {
            float a = rec.Alpha;
            if (dt == 0.0)
            {
                a += rec.AlphaCur;
                rec.AlphaCur = 0f;
            }

            double v = Val(5, F(a * 255.0));
            double c = 0.0 > v ? 0.0 : (v <= 255.0 ? v : 255.0);
            rec.Alpha = (float)F(c / 255.0);
            if (SceneOk())
            {
                scene!.SetAlpha(layer, I16(Cvtt(c)));
            }
        }

        // 22, 23: picture variant (0xBEB37..0xBF314): the name comes from the scene picture at t == 0 and from the drawn picture otherwise
        int srcPic = rec.Pic;
        if (t == 0.0 && scene is not null)
        {
            srcPic = SPic();
        }

        string name = srcPic >= 0 ? PictureNames.EngineName(catalog, srcPic) : string.Empty;
        (int hh, int vv) = PictureNames.VariantDigits(name);
        if (!Found(22))
        {
            flags &= ~Mask22;
        }

        if (!Found(23))
        {
            flags &= ~Mask23;
        }

        if ((flags & (Mask22 | Mask23)) != 0 && rec.Pic != -1)
        {
            int nh = (flags & Mask22) != 0 ? Math.Max(Cvtt(Val(22, hh)), 0) : hh;
            int nv = (flags & Mask23) != 0 ? Math.Max(Cvtt(Val(23, vv)), 0) : vv;
            if (nh != hh || nv != vv)
            {
                string n2 = PictureNames.VariantName(name, hh, vv, nh, nv);
                int nid = catalog.Lookup(n2);
                if (nid >= 0)
                {
                    if (scene is not null)
                    {
                        int sp = SPic();
                        if (t == 0.0 || rec.Pic == sp)
                        {
                            (int cax, int cay) = PictureNames.CatalogAnchor(catalog, nid);
                            int ax;
                            int ay;
                            if (sp == -1)
                            {
                                ax = cax;
                                ay = cay;
                            }
                            else
                            {
                                (int csx, int csy) = PictureNames.CatalogAnchor(catalog, sp);
                                ax = scene.GetAnchorX(layer) + cax - csx;
                                ay = scene.GetAnchorY(layer) + cay - csy;
                            }

                            scene.SetAnchorX(layer, I16(ax));
                            scene.SetAnchorY(layer, I16(ay));
                            scene.SetPic(layer, I16(nid));
                            SetExtI16(scene, layer, 2, -ax);
                            SetExtI16(scene, layer, 4, -ay);
                        }
                    }

                    if (t > 0.0)
                    {
                        (int ax, int ay) = PictureNames.CatalogAnchor(catalog, nid);
                        rec.Pic = nid;
                        rec.AnchorX = I16(ax);
                        rec.AnchorY = I16(ay);
                        rec.OffX = -I16(ax);
                        rec.OffY = -I16(ay);
                        rec.Face = nid >= 0 && nid < catalog.Count && catalog.Info(nid).HasFace ? 1 : 0;
                    }

                    name = n2;
                    hh = nh;
                    vv = nv;
                }
            }
        }

        // anchor difference of a variant picture to its base (0xBF31B..0xBF389)
        int diffX = 0;
        int diffY = 0;
        if ((hh | vv) != 0)
        {
            int bid = catalog.Lookup(name.Length >= 6 ? name[..^6] : string.Empty);
            (int a1x, int a1y) = PictureNames.CatalogAnchor(catalog, rec.Pic);
            (int b1x, int b1y) = PictureNames.CatalogAnchor(catalog, bid);
            diffX = I16(a1x - b1x);
            diffY = I16(a1y - b1y);
        }

        // 6, 7: anchor (0xBF391.., 0xBF4EA..); no found-scan
        if ((flags & Mask6) != 0)
        {
            double cur = CDiv((long)rec.AnchorX * 480, 640);
            double v = Val(6, cur);
            double x = F(F(F(v * 640.0) / 480.0) + diffX);
            int sp = scene is not null ? SPic() : rec.Pic;
            if (sp != -1)
            {
                x = F(x * ClassFactor(catalog, sp));
            }

            rec.AnchorX = I16(Cvtt(x));
            rec.OffX = (float)-x;
            if (scene is not null && SPic() != -1)
            {
                scene.SetAnchorX(layer, I16(Cvtt(x)));
                SetExtI16(scene, layer, 2, Cvtt(-x));
            }
        }

        if ((flags & Mask7) != 0)
        {
            double cur = CDiv((long)rec.AnchorY * 360, 480);
            double v = Val(7, cur);
            double x = F(F(F(v * 480.0) / 360.0) + diffY);
            int sp = scene is not null ? SPic() : rec.Pic;
            if (sp != -1)
            {
                x = F(x * ClassFactor(catalog, sp));
            }

            rec.AnchorY = I16(Cvtt(x));
            rec.OffY = (float)-x;
            if (scene is not null && SPic() != -1)
            {
                scene.SetAnchorY(layer, I16(Cvtt(x)));
                SetExtI16(scene, layer, 4, Cvtt(-x));
            }
        }

        // 8: rotation (0xBF648); the +0x15C angle only with flag bit 34 (0xBF6B8)
        if ((flags & Mask8) != 0)
        {
            double v = Val(8, F((double)rec.Rot));
            rec.Rot = Cvtt64(v);
            if (SceneOk())
            {
                scene!.SetRot(layer, rec.Rot);
            }
        }

        if ((flags & AngleBit) != 0)
        {
            double v = Val(8, F((double)rec.Rot));
            rec.Angle = I16(Cvtt64(v));
            if (SceneOk())
            {
                scene!.SetAngle(layer, rec.Angle);
            }
        }

        // 9, 10: zoom. 0xBF724..0xBF754 first ends the zoom tween (delta 0, counter 0) on every call; a fractional value is stored rounded away
        // from zero with the fraction in that tween (delta = (v - zoom) * 0.001, counter 4096), so the drawn zoom is exactly v
        rec.ZoomDx = 0f;
        rec.ZoomDy = 0f;
        rec.TwZoom.Counter = 0;
        if (!Found(9))
        {
            flags &= ~Mask9;
        }

        if (!Found(10))
        {
            flags &= ~Mask10;
        }

        for (int pass = 0; pass < 2; pass++)
        {
            int f = 9 + pass;
            ulong mask = pass == 0 ? Mask9 : Mask10;
            if ((flags & mask) == 0)
            {
                continue;
            }

            double v = Val(f, pass == 0 ? rec.ZoomX : rec.ZoomY);
            int zi = Cvtt(v);
            if ((double)I16(zi) != v)
            {
                zi += v >= 0.0 ? 1 : -1;
                float delta = (float)F(F(v - I16(zi)) * F(0.001));
                if (pass == 0)
                {
                    rec.ZoomDx = delta;
                }
                else
                {
                    rec.ZoomDy = delta;
                }

                rec.TwZoom.Counter = 4096;
            }

            if (pass == 0)
            {
                rec.ZoomX = I16(zi);
                if (SceneOk())
                {
                    scene!.SetZoomX(layer, I16(zi));
                }
            }
            else
            {
                rec.ZoomY = I16(zi);
                if (SceneOk())
                {
                    scene!.SetZoomY(layer, I16(zi));
                }
            }
        }

        // 11..14: crop rectangle (0xBFA7A..); cur = the scene value when it differs from the drawn one
        for (int grp = 0; grp < 2; grp++)
        {
            ulong cmask = grp == 0 ? Mask11And13 : Mask12And14;
            if ((flags & cmask) == 0)
            {
                continue;
            }

            for (int sub = 0; sub < 2; sub++)
            {
                int f = grp == 0 ? (sub == 0 ? 11 : 13) : (sub == 0 ? 12 : 14);
                int j = f - 11;
                double cur = rec.Crop[j];
                if (SceneOk())
                {
                    short sv = scene!.GetCrop(layer, j);
                    cur = sv == rec.Crop[j] ? rec.Crop[j] : sv;
                }

                rec.Crop[j] = I16(Cvtt(Val(f, cur)));
                if (SceneOk())
                {
                    scene!.SetCrop(layer, j, rec.Crop[j]);
                }
            }
        }

        // 15, 16: a17 / a16 (max 0)
        if ((flags & Mask15) != 0)
        {
            double v = Math.Max(0.0, Val(15, I8(rec.A17)));
            rec.A17 = (byte)(Cvtt(v) & 0xFF);
            if (SceneOk())
            {
                scene!.SetA17(layer, rec.A17);
            }
        }

        if ((flags & Mask16) != 0)
        {
            double v = Math.Max(0.0, Val(16, I16(rec.A16)));
            rec.A16 = (ushort)(Cvtt(v) & 0xFFFF);
            if (SceneOk())
            {
                scene!.SetA16(layer, rec.A16);
            }
        }

        // 17: blend (written when it changes)
        if ((flags & Mask17) != 0)
        {
            int iv = Cvtt(Val(17, rec.Blend));
            if (rec.Blend != iv)
            {
                rec.Blend = I16(iv);
                if (SceneOk())
                {
                    scene!.SetBlend(layer, I16(iv));
                }
            }
        }

        int Rgb() => (Cvtt(Val(19, 0.0)) << 16) + (Cvtt(Val(20, 0.0)) << 8) + Cvtt(Val(21, 0.0));

        // 18: colour key (0xBFDF0..): value 1 sets the colour; the scene colour is set when the scene fx_mode byte is not 0xFF (no picture test)
        if ((flags & Mask18) != 0 && Cvtt(Val(18, rec.FxMode)) == 1)
        {
            int c = Rgb();
            ColourBlock(rec.Colour, c);
            if (scene is not null && (scene.GetFxMode(layer) & 0xFF) != 0xFF)
            {
                ExtColour(scene, layer, c);
            }
        }

        // 19..21: colour (bit 33, 0xBFF57..)
        if ((flags & Mask19) != 0)
        {
            int c = Rgb();
            ColourBlock(rec.Colour, c);
            if (SceneOk())
            {
                ExtColour(scene!, layer, c);
            }
        }

        // 24, 25: colour-effect parameters; 26: noise
        if ((flags & Mask24) != 0)
        {
            int iv = Cvtt(Val(24, rec.FxA));
            rec.FxA = I16(iv);
            if (SceneOk())
            {
                scene!.SetFxA(layer, I16(iv));
            }
        }

        if ((flags & Mask25) != 0)
        {
            int iv = Cvtt(Val(25, rec.FxB));
            if (rec.FxB != iv)
            {
                rec.FxB = I8(iv);
                if (SceneOk())
                {
                    scene!.SetFxB(layer, I8(iv));
                }
            }
        }

        if ((flags & Mask26) != 0)
        {
            int iv = Cvtt(Val(26, I8(rec.Stns)));
            if (I8(rec.Stns) != iv)
            {
                byte nb = iv < 3 ? (byte)20 : (byte)12;
                rec.Stns = (byte)(iv & 0xFF);
                rec.StnsB = nb;
                if (SceneOk())
                {
                    scene!.SetStns(layer, rec.Stns);
                    scene.SetStnsB(layer, nb);
                }
            }
        }

        // 27: z order (only when v != 0 and the scene z is not 0)
        if ((flags & Mask27) != 0 && scene is not null)
        {
            double v = Val(27, 0.0);
            if (v != 0.0 && scene.GetZ(layer) != 0)
            {
                rec.ZOrder = Cvtt(v);
                if (SceneOk())
                {
                    scene.SetZ(layer, I16(Cvtt(v)));
                }
            }
        }

        return t;
    }

    private static float ClassFactor(IPictureCatalog c, int pic)
    {
        int cls = PictureNames.ScaleClass(c, pic);
        return cls switch { 1 => 0.5f, 2 => 0.25f, 3 => 0.125f, _ => 1.0f };
    }
}
