using System.Globalization;
using Tsuki.Engine.Contracts;
using static Tsuki.Engine.Stage.StageMath;

namespace Tsuki.Engine.Stage;

public sealed partial class StageRuntime
{
    private static readonly float OneOver255 = (float)F(1.0 / 255.0);

    /// <summary>
    /// Runs one layer statement: <c>STCH SCH2 STCC STCP STBR STEC SNX2 SNX3 STZ4 STRT STRR STRA STPV SOXY STGS STMA SPMA STNS STTR SACL SBAS STCL
    /// SQK2 SHK2 SSW2 SQS2 SHS2 SSS2 SGMA STCF FADS SHOK SHAF MSQK</c> (docs/formats/layers.md 4, quake.md 3, 6, 7). The arguments are read the way the
    /// PS4 handlers read them (the object parser; <c>STPV</c> and <c>SHOK</c> inline). Returns false for a statement that is not a stage command.
    /// </summary>
    public bool Execute(in Statement statement)
    {
        if (statement.Name.First is not ((byte)'S' or (byte)'F' or (byte)'M'))
        {
            return false;
        }

        string name = statement.Name.ToString();
        switch (name)
        {
            case "STPV":
                CmdStpv(statement);
                return true;
            case "SHOK":
                CmdShok(statement);
                return true;
            case "STCH" or "SCH2" or "STCC" or "STCP" or "STBR" or "STEC" or "SNX2" or "SNX3" or "STZ4" or "STRT" or "STRR" or "STRA" or "SOXY"
                or "STGS" or "STMA" or "SPMA" or "STNS" or "STTR" or "SACL" or "SBAS" or "STCL" or "SQK2" or "SHK2" or "SSW2" or "SQS2" or "SHS2"
                or "SSS2" or "SGMA" or "STCF" or "FADS" or "SHAF" or "MSQK":
                break;
            default:
                return false;
        }

        var a = new StageArgs(statement);
        switch (name)
        {
            case "STCH":
                CmdStch(a, true);
                break;
            case "SCH2":
                CmdStch(a, false);
                break;
            case "STCC":
                CmdStcc(a);
                break;
            case "STCP":
            {
                int l = a.Has(0) ? a.Expr(0) : 0;
                if (l is >= 0 and <= 39)
                {
                    Scene.SetZ(l, I16(a.Expr(1)));
                }

                break;
            }

            case "STBR":
            {
                int l = a.Has(0) ? a.Expr(0) : 0;
                if (l is >= 0 and <= 39)
                {
                    Scene.SetBlend(l, I16(a.Expr(1)));
                    if (a.Has(3) && a.Expr(3) == 1)
                    {
                        Scene.SetFlags(l, (byte)(Scene.GetFlags(l) | 0x80));
                    }
                }

                break;
            }

            case "STEC":
                CmdStec(a);
                break;
            case "SNX2":
            {
                int l = a.Has(0) ? a.Expr(0) : -1;
                if (l is >= 0 and <= 39)
                {
                    Scene.SetFxA(l, I16(a.Expr(1)));
                }

                break;
            }

            case "SNX3":
            {
                int l = a.Has(0) ? a.Expr(0) : -1;
                if (l is >= 0 and <= 39)
                {
                    Scene.SetFxB(l, I8(a.Expr(1)));
                }

                break;
            }

            case "STZ4":
                CmdStz4(a);
                break;
            case "STRT":
                CmdStrt(a);
                break;
            case "STRR":
            {
                int l = a.Has(0) ? a.Expr(0) : 0;
                if (l is >= 0 and <= 39)
                {
                    int v = 0;
                    if (a.Has(1))
                    {
                        int r = a.Expr(1);
                        v = r == -100001 ? -32768 : WrapAngle(r);
                    }

                    Scene.SetAngle(l, I16(v));
                }

                break;
            }

            case "STRA":
                CmdStra(a);
                break;
            case "SOXY":
            {
                // inline handler 0x99885: two byte stores without a layer bound check (layer 40 writes soxy_y[0] and the first byte of crop[0])
                int l = a.Has(0) ? a.Expr(0) : 0;
                WriteRawByte(0xAA8 + l, (byte)a.Expr(1));
                WriteRawByte(0xAD0 + l, (byte)a.Expr(2));
                break;
            }

            case "STGS":
                CmdStgs(a);
                break;
            case "STMA":
                CmdStma(a);
                break;
            case "SPMA":
            {
                byte v = a.Has(0) && a.Expr(0) == 1 ? (byte)1 : (byte)0;
                for (int k = 0; k < LayerCount; k++)
                {
                    Scene.SetMotionPause(k, v);
                    Drawn[k].MotionPause = v;
                }

                break;
            }

            case "STNS":
                CmdStns(a);
                break;
            case "STTR":
                CmdSttr(a);
                break;
            case "SACL":
            {
                int l = a.Has(0) ? a.Expr(0) : 0;
                if (l is >= 0 and <= 39)
                {
                    byte f = Scene.GetFlags(l);
                    Scene.SetFlags(l, a.Has(1) && a.Expr(1) != 0 ? (byte)(f | 0x40) : (byte)(f & ~0x40));
                }

                break;
            }

            case "SBAS":
            {
                int l = a.Has(0) ? a.Expr(0) : 0;
                if (l is >= 0 and <= 39)
                {
                    Scene.SetParent(l, a.Has(1) ? a.Expr(1) : -1);
                }

                break;
            }

            case "STCL":
                CmdStcl(a);
                break;
            case "SQK2":
                CmdSqk2(a);
                break;
            case "SHK2":
                CmdShk2(a);
                break;
            case "SSW2":
                CmdSsw2(a);
                break;
            case "SQS2":
                CmdStopShake(a, 1);
                break;
            case "SHS2":
                CmdStopShake(a, 2);
                break;
            case "SSS2":
                CmdStopShake(a, 3);
                break;
            case "SGMA":
                CmdSgma(a);
                break;
            case "STCF":
                // the wait word 0x1653E30 and the mask bytes belong to the wait state (GameHost.Stage.cs, WaitModel.AdvUpdate clears them); nothing changes on the stage
                break;

            case "FADS":
                CmdFads(a);
                break;
            case "SHAF":
                Shock.Stop();
                break;
            case "MSQK":
                MessageQuake.Start(a.Has(0) ? a.Expr(0) : 1000, a.Has(1) ? a.Expr(1) : 5, a.Has(2) ? a.Expr(2) : 5);
                break;
        }

        return true;
    }

    private void WriteRawByte(int offset, byte value)
    {
        if (offset >= 0 && offset < SceneState.Size)
        {
            Scene.Storage[Scene.Offset + offset] = value;
        }
    }

    // -- helpers on the scene's ext block (18 bytes per layer: +2 off_x, +4 off_y, +6..+0xD colour block) ---------------------------------------

    private void SetExtOffsets(int l, int ax, int ay)
    {
        Span<byte> e = Scene.Ext(l);
        short x = I16(-ax);
        short y = I16(-ay);
        e[2] = (byte)(x & 0xFF);
        e[3] = (byte)((x >> 8) & 0xFF);
        e[4] = (byte)(y & 0xFF);
        e[5] = (byte)((y >> 8) & 0xFF);
    }

    private void SetExtWhite(int l)
    {
        Span<byte> e = Scene.Ext(l);
        e[6] = 255;
        e[7] = 255;
        e[8] = 255;
        e[9] = 0;
        e[10] = 0;
        e[11] = 0;
        e[12] = 0;
        e[13] = 0;
    }

    // -- 0x4A770: the reset of the drawn record that STCH and SCH2 perform -----------------------------------------------------------------

    private void ResetDrawn(int l)
    {
        LayerRecord r = Drawn[l];
        r.AlphaCur = 0f;
        r.MoveDx = 0f;
        r.MoveDy = 0f;
        r.AlphaDelta = 0f;
        r.Angle = 0;
        MotionApply.MotionClear(r);
        r.Blend = 0;
        r.FxMode = 0;
        r.FxA = 0;
        r.FxB = 0;
        MotionApply.ColourReset(r.Colour);
        Shakes.ResetLayer(l);
        r.Stra = 0;
        r.StraPhase = 0;
        r.Stns = 0;
        r.StnsB = 0;
    }

    // -- STCH / SCH2 (backend VA 0x81760, edi = 1 / 0) ---------------------------------------------------------------------------------------

    private void CmdStch(in StageArgs a, bool reset)
    {
        SceneState s = Scene;
        int l = a.Has(0) ? a.Expr(0) : -1;
        if (l is < 0 or > 39)
        {
            return;
        }

        int src = a.Has(18) ? a.Expr(18) : -1;
        string name = a.Str(1);
        if (name.Length > 255)
        {
            name = name[..255];
        }

        bool hasSrc = src >= 0 && src <= 39;
        int h = 0;
        int v = 0;
        if (hasSrc)
        {
            int p = s.GetPic(src);
            if (p >= 0 && p < _catalog.Count)
            {
                (h, v) = PictureNames.VariantDigitsStch(_catalog.Name(p));
            }
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

        // live: 0x49570 (STCH only: rotation words) and 0x4A770 (both) reset the drawn record
        if (reset)
        {
            LayerRecord ar = Drawn[l];
            ar.Rot = 0;
            ar.RotDelta = 0;
            ar.RotCur = 0;
        }

        ResetDrawn(l);

        s.SetZoomY(l, 1000);
        s.SetZoomX(l, 1000);
        s.SetZoomPivot(l, 0);
        int pic = _catalog.Lookup(name);
        if (hasSrc)
        {
            CopySourceLayer(src, l, pic, name);
        }
        else
        {
            for (int sl = 0; sl < 2; sl++)
            {
                s.SetShakeKind(l, sl, 0);
                s.SetShakeFade(l, sl, 0);
                s.SetShakeTime(l, sl, 0);
            }

            for (int i = 0; i < 12; i++)
            {
                s.SetShakePar(l, i, 0);
            }
        }

        if (name.Length == 0)
        {
            // 0x82618: no picture name -> clear (everything _STCC resets except x, y, alpha, z, zoom, pivot, stra, stns)
            s.SetPic(l, -1);
            s.SetScale(l, 1000);
            s.SetAnchorX(l, 0);
            s.SetAnchorY(l, 0);
            s.SetRot(l, 0);
            s.SetMask(l, -1);
            s.SetParent(l, -1);
            s.SetSoxyX(l, 0);
            s.SetSoxyY(l, 0);
            s.SetFlags(l, 0);
            for (int j = 0; j < 4; j++)
            {
                s.SetCrop(l, j, 0);
            }

            s.SetA16(l, 0);
            s.SetA17(l, 0);
            s.SetAngle(l, 0);
            s.SetSttr(l, -1);
            s.Motion(l).Clear();
            s.SetBlend(l, 0);
            s.SetFxMode(l, 0);
            s.SetFxA(l, 0);
            s.SetFxB(l, 0);
            for (int sl = 0; sl < 2; sl++)
            {
                s.SetShakeKind(l, sl, 0);
                s.SetShakeFade(l, sl, 0);
                s.SetShakeTime(l, sl, 0);
            }

            for (int i = 0; i < 12; i++)
            {
                s.SetShakePar(l, i, 0);
            }

            MotionApply.MotionClear(Committed[l]);
            SetExtWhite(l);
            return;
        }

        if (a.Has(11))
        {
            s.SetMask(l, I16(a.Expr(11)));
        }

        s.SetSttr(l, -1);
        for (int k = 0; k < 4; k++)
        {
            if (a.Has(12 + k))
            {
                s.SetCrop(l, k, I16(a.Expr(12 + k)));
            }
        }

        if (a.Has(16))
        {
            s.SetA16(l, (ushort)(a.Expr(16) & 0xFFFF));
        }

        if (a.Has(17))
        {
            s.SetA17(l, (byte)(a.Expr(17) & 0xFF));
        }

        if (a.Has(19))
        {
            s.SetAngle(l, I16(WrapAngle(a.Expr(19))));
        }

        if (src < 0)
        {
            // 0x827BA: the scene, drawn and committed motion records are cleared, the drawn colour is set white
            s.Motion(l).Clear();
            MotionApply.MotionClear(Drawn[l]);
            MotionApply.MotionClear(Committed[l]);
            MotionApply.ColourBlock(Drawn[l].Colour, 0xFFFFFF);
            s.SetBlend(l, 0);
            s.SetFxMode(l, 0);
            s.SetFxA(l, 0);
            s.SetFxB(l, 0);
            SetExtWhite(l);
            if (s.GetPic(l) < 0)
            {
                // 0x8284B: a layer without a picture starts transparent (the drawn record too)
                s.SetAlpha(l, 0);
                Drawn[l].Alpha = 0f;
            }
        }

        s.SetPic(l, I16(pic));
        if (a.Has(2))
        {
            s.SetX(l, I16(ScriptX(a.Expr(2))));
        }

        if (a.Has(3))
        {
            s.SetY(l, I16(ScriptY(a.Expr(3))));
        }

        if (reset)
        {
            if (pic >= 0)
            {
                PictureInfo pi = _catalog.Info(pic);
                s.SetAnchorX(l, I16(pi.AnchorX));
                s.SetAnchorY(l, I16(pi.AnchorY));
            }

            s.SetRot(l, 0);
        }

        if (a.Has(4))
        {
            s.SetScale(l, I16(a.Expr(4)));
        }

        if (reset || a.Has(5))
        {
            s.SetAlpha(l, I16(a.Has(5) ? Math.Max(0, Math.Min(a.Expr(5), 255)) : 255));
            s.SetState(l, 0);
        }

        if (reset || a.Has(6))
        {
            s.SetZ(l, I16(a.Has(6) ? a.Expr(6) : 10 * l));
        }

        if (!hasSrc)
        {
            // 0x82B07
            (int ax, int ay) = pic >= 0 ? (_catalog.Info(pic).AnchorX, _catalog.Info(pic).AnchorY) : (256, 255);
            SetExtOffsets(l, ax, ay);
            if (pic >= 0)
            {
                s.SetAnchorX(l, I16(ax));
                s.SetAnchorY(l, I16(ay));
            }
        }

        if (DefaultColourEffect > 0 && (pic < 0 || (_catalog.Info(pic).DrawFlags & 1) == 0))
        {
            s.SetFxMode(l, I8(DefaultColourEffect));
        }
    }

    // 0x81AF6..0x82612: the source layer's state is copied to the layer
    private void CopySourceLayer(int src, int l, int pic, string name)
    {
        SceneState s = Scene;
        s.SetBlend(l, s.GetBlend(src));
        s.SetFxMode(l, s.GetFxMode(src));
        s.SetFxA(l, s.GetFxA(src));
        s.SetFxB(l, s.GetFxB(src));
        s.SetX(l, s.GetX(src));
        s.SetY(l, s.GetY(src));
        s.SetAlpha(l, s.GetAlpha(src));
        s.SetState(l, s.GetState(src));
        s.SetU284(l, s.GetU284(src));
        s.SetU2AC(l, s.GetU2AC(src));
        s.SetU2D4(l, s.GetU2D4(src));
        s.SetZ(l, s.GetZ(src));
        s.SetRot(l, s.GetRot(src));
        s.SetScale(l, s.GetScale(src));
        s.SetAnchorX(l, s.GetAnchorX(src));
        s.SetAnchorY(l, s.GetAnchorY(src));
        s.SetU580(l, s.GetU580(src));
        s.SetU5D0(l, s.GetU5D0(src));
        s.SetU620(l, s.GetU620(src));
        s.SetU670(l, s.GetU670(src));
        s.SetU6C0(l, s.GetU6C0(src));
        s.SetU710(l, s.GetU710(src));
        s.SetU760(l, s.GetU760(src));
        s.SetU7B0(l, s.GetU7B0(src));
        s.SetU800(l, s.GetU800(src));
        s.SetU850(l, s.GetU850(src));
        int sp = s.GetPic(src);
        if (sp >= 0 && sp < _catalog.Count && _catalog.Info(sp).AnchorX == s.GetAnchorX(src) && _catalog.Info(sp).AnchorY == s.GetAnchorY(src))
        {
            // an unknown name reads the bytes before the table (256, 255); the empty name an empty slot after it (0, -1)
            (int dx, int dy) = name.Length == 0 ? (0, -1) : (256, 255);
            bool known = pic >= 0 && pic < _catalog.Count;
            s.SetAnchorX(l, I16(known ? _catalog.Info(pic).AnchorX : dx));
            s.SetAnchorY(l, I16(known ? _catalog.Info(pic).AnchorY : dy));
        }

        s.SetZoomX(l, s.GetZoomX(src));
        s.SetZoomY(l, s.GetZoomY(src));
        s.SetZoomPivot(l, s.GetZoomPivot(src));
        s.SetMask(l, s.GetMask(src));
        for (int k = 0; k < LayerCount; k++)
        {
            if (s.GetMask(k) == src)
            {
                s.SetMask(k, I16(l));
            }
        }

        s.SetParent(l, s.GetParent(src));
        for (int k = 0; k < LayerCount; k++)
        {
            if (s.GetParent(k) == src)
            {
                s.SetParent(k, l);
            }
        }

        s.SetSoxyX(l, s.GetSoxyX(src));
        s.SetSoxyY(l, s.GetSoxyY(src));
        s.SetFlags(l, s.GetFlags(src));
        for (int j = 0; j < 4; j++)
        {
            s.SetCrop(l, j, s.GetCrop(src, j));
        }

        s.SetA16(l, s.GetA16(src));
        s.SetA17(l, s.GetA17(src));
        s.SetAngle(l, s.GetAngle(src));
        s.SetU19AB(l, s.GetU19AB(src));
        s.SetU19D3(l, s.GetU19D3(src));

        // motion record: 0xBA800 clears the destination, then 0xBA8E0 copies the source (0x8250E / 0x8252A). With src == l the source is the record
        // just cleared, so a self-source statement stops the layer's motion
        s.SetMotionId(l, 0);
        s.SetMotionDivisor(l, 0);
        s.SetMotionFrames(l, 0);
        s.SetMotionPause(l, 0);
        short sid = s.GetMotionId(src);
        short sdiv = s.GetMotionDivisor(src);
        long sfr = s.GetMotionFrames(src);
        byte sps = s.GetMotionPause(src);
        long dfr = s.GetMotionFrames(l);
        s.SetMotionId(l, sid);
        if (dfr <= sfr && sid > 0)
        {
            s.SetMotionFrames(l, sfr);
        }

        s.SetMotionDivisor(l, sdiv);
        s.SetMotionPause(l, sps);

        for (int sl = 0; sl < 2; sl++)
        {
            s.SetShakeKind(l, sl, s.GetShakeKind(src, sl));
            s.SetShakeFade(l, sl, s.GetShakeFade(src, sl));
            s.SetShakeTime(l, sl, s.GetShakeTime(src, sl));
        }

        for (int i = 0; i < 12; i++)
        {
            s.SetShakePar(l, i, s.GetShakePar(src, i));
        }

        s.SetStra(l, s.GetStra(src));
        int ax = s.GetAnchorX(l);
        int ay = s.GetAnchorY(l);
        s.Ext(src).CopyTo(s.Ext(l));
        SetExtOffsets(l, ax, ay);

        // live: the drawn colour takes the copied ext colour (0xF7A0 after the reset)
        ReadOnlySpan<byte> ex = s.Ext(l);
        MotionApply.ColourBlock(Drawn[l].Colour, (ex[6] << 16) | (ex[7] << 8) | ex[8], (ex[9] << 16) | (ex[10] << 8) | ex[11]);
    }

    // -- STCC (VA 0x82D30) ---------------------------------------------------------------------------------------------------------------------

    private void CmdStcc(in StageArgs a)
    {
        int l = a.Has(0) ? a.Expr(0) : 0;
        if (l > 39)
        {
            return;
        }

        int from = l < 0 ? 0 : l;
        int to = l < 0 ? LayerCount : l + 1;
        for (int k = from; k < to; k++)
        {
            SceneState s = Scene;
            s.SetPic(k, -1);
            s.SetScale(k, 1000);
            s.SetAnchorY(k, 0);
            s.SetAnchorX(k, 0);
            s.SetRot(k, 0);
            s.SetZ(k, 0);
            s.SetX(k, 0);
            s.SetY(k, 0);
            s.SetAlpha(k, 255);
            s.SetZoomY(k, 1000);
            s.SetZoomX(k, 1000);
            s.SetZoomPivot(k, 0);
            s.SetMask(k, -1);
            s.SetParent(k, -1);
            s.SetSoxyX(k, 0);
            s.SetSoxyY(k, 0);
            s.SetFlags(k, 0);
            for (int j = 0; j < 4; j++)
            {
                s.SetCrop(k, j, 0);
            }

            s.SetA16(k, 0);
            s.SetA17(k, 0);
            s.SetAngle(k, 0);
            s.SetSttr(k, -1);
            s.Motion(k).Clear();
            s.SetBlend(k, 0);
            s.SetFxMode(k, 0);
            s.SetFxA(k, 0);
            s.SetFxB(k, 0);
            for (int sl = 0; sl < 2; sl++)
            {
                s.SetShakeKind(k, sl, 0);
                s.SetShakeFade(k, sl, 0);
                s.SetShakeTime(k, sl, 0);
            }

            for (int i = 0; i < 12; i++)
            {
                s.SetShakePar(k, i, 0);
            }

            s.SetStra(k, 0);
            s.SetStns(k, 0);
            s.SetStnsB(k, 0);

            // live: the drawn record's angle, crop, a16 / a17, sttr, motion (A and B) and colour
            LayerRecord r = Drawn[k];
            r.Angle = 0;
            Array.Clear(r.Crop);
            r.A16 = 0;
            r.A17 = 0;
            r.Sttr = -1;
            MotionApply.MotionClear(r);
            MotionApply.MotionClear(Committed[k]);
            MotionApply.ColourReset(r.Colour);
        }
    }

    // -- STEC / STZ4 / STRT / STGS / STCL / STMA / STNS / STTR / STRA / STPV -----------------------------------------------------------------

    private void CmdStec(in StageArgs a)
    {
        int l = a.Has(0) ? a.Expr(0) : -1;
        int mode = a.Has(1) ? a.Expr(1) : 0;
        if (l is < 0 or > 39 || mode is < 0 or > 81)
        {
            return;
        }

        int rgb = mode == 1 ? Strtol0(a.Str(2)) & 0xFFFFFF : 0xFFFFFF;
        Scene.SetFxMode(l, I8(mode));
        Span<byte> e = Scene.Ext(l);
        e[6] = (byte)((rgb >> 16) & 0xFF);
        e[7] = (byte)((rgb >> 8) & 0xFF);
        e[8] = (byte)(rgb & 0xFF);
        MotionApply.ColourBlock(Drawn[l].Colour, rgb);
    }

    /// <summary><c>strtol(s, NULL, 0)</c> of the STEC colour argument (hexadecimal with 0x, octal with a leading 0, else decimal).</summary>
    internal static int Strtol0(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r'))
        {
            i++;
        }

        bool neg = false;
        if (i < s.Length && s[i] is '+' or '-')
        {
            neg = s[i] == '-';
            i++;
        }

        int radix = 10;
        if (i + 1 < s.Length && s[i] == '0' && (s[i + 1] is 'x' or 'X') && i + 2 < s.Length && Uri.IsHexDigit(s[i + 2]))
        {
            radix = 16;
            i += 2;
        }
        else if (i < s.Length && s[i] == '0')
        {
            radix = 8;
        }

        long val = 0;
        int digits = 0;
        for (; i < s.Length; i++)
        {
            int d = s[i] is >= '0' and <= '9' ? s[i] - '0' : s[i] is >= 'a' and <= 'f' ? s[i] - 'a' + 10 : s[i] is >= 'A' and <= 'F' ? s[i] - 'A' + 10 : 99;
            if (d >= radix)
            {
                break;
            }

            val = unchecked((val * radix) + d);
            digits++;
        }

        if (digits == 0)
        {
            return 0;
        }

        return unchecked((int)(neg ? -val : val));
    }

    private void CmdStz4(in StageArgs a)
    {
        SceneState s = Scene;
        int l = a.Has(0) ? a.Expr(0) : 0;
        if (l is < 0 or > 39)
        {
            return;
        }

        int zx0 = a.Has(1) ? a.Expr(1) : s.GetZoomX(l);
        int zy0 = a.Has(2) ? a.Expr(2) : s.GetZoomY(l);
        if (a.Has(3))
        {
            s.SetZoomX(l, I16(a.Expr(3)));
        }

        if (a.Has(4))
        {
            s.SetZoomY(l, I16(a.Expr(4)));
        }

        int frames = a.Has(5) ? MsToFrames60(a.Expr(5)) : 0;
        int piv = a.Has(7) ? a.Expr(7) : s.GetZoomPivot(l);
        s.SetZoomPivot(l, I8(piv));
        int ease = a.Has(6) ? a.Expr(6) : 0;

        // live: 0x49930
        LayerRecord r = Drawn[l];
        short x0 = I16(zx0);
        short y0 = I16(zy0);
        short x1 = s.GetZoomX(l);
        short y1 = s.GetZoomY(l);
        short f16 = I16(frames);
        r.ZoomX = x1;
        r.ZoomY = y1;
        r.ZoomPivot = (byte)(piv & 0xFF);
        r.TwZoom = Tween.Start(I16(ease), f16);
        if (f16 == 0)
        {
            r.ZoomDx = 0f;
            r.ZoomDy = 0f;
        }
        else
        {
            // the engine adds the two products and flips the sign (0x499D0..0x499F8), so equal zooms give -0
            r.ZoomDx = -((x1 * 0.001f) + (x0 * -0.001f));
            r.ZoomDy = -((y1 * 0.001f) + (y0 * -0.001f));
        }
    }

    private void CmdStrt(in StageArgs a)
    {
        SceneState s = Scene;
        int l = a.Has(0) ? a.Expr(0) : 0;
        if (l is < 0 or > 39)
        {
            return;
        }

        int r0 = a.Has(1) ? a.Expr(1) : I32(s.GetRot(l));
        int r1 = a.Has(2) ? a.Expr(2) : 0;
        s.SetRot(l, r1);
        int frames = a.Has(3) ? MsToFrames60(a.Expr(3)) : 0;
        int ease = a.Has(4) ? a.Expr(4) : 0;

        // live: 0x4A4B0
        LayerRecord r = Drawn[l];
        short f16 = I16(frames);
        r.Rot = r1;
        short delta = I16(r0 - r1);
        r.RotDelta = delta;
        r.RotCur = delta;
        r.TwRot = Tween.Start(I16(ease), f16);
        if (f16 == 0)
        {
            r.RotDelta = 0;
        }
    }

    private void CmdStgs(in StageArgs a)
    {
        SceneState s = Scene;
        int l = a.Has(0) ? a.Expr(0) : 0;
        if (l is < 0 or > 39)
        {
            return;
        }

        int start = a.Has(1) ? Math.Min(a.Expr(1), 255) : Math.Min((int)s.GetAlpha(l), 255);
        int end = a.Has(2) ? Math.Min(a.Expr(2), 255) : 0;
        s.SetAlpha(l, I16(end));
        int frames = a.Has(3) ? MsToFrames60(a.Expr(3)) : 0;
        int ease = a.Has(4) ? a.Expr(4) : 0;

        // live: 0x4A620
        LayerRecord r = Drawn[l];
        short f16 = I16(frames);
        float endF = end / 255.0f;
        float startF = start / 255.0f;
        r.Alpha = endF;
        r.TwAlpha = Tween.Start(I16(ease), f16);
        if (f16 == 0)
        {
            r.AlphaDelta = 0f;
            r.AlphaCur = 0f;
        }
        else
        {
            float d = startF - endF;
            r.AlphaDelta = d;
            r.AlphaCur = d;
        }
    }

    private void CmdStcl(in StageArgs a)
    {
        SceneState s = Scene;
        int l = a.Has(1) ? a.Expr(1) : 0;
        if (l is < 0 or > 39)
        {
            return;
        }

        int x0 = a.Has(2) ? ScriptX(a.Expr(2)) : s.GetX(l);
        int y0 = a.Has(3) ? ScriptY(a.Expr(3)) : s.GetY(l);
        int x1 = a.Has(4) ? ScriptX(a.Expr(4)) : x0;
        int y1 = a.Has(5) ? ScriptY(a.Expr(5)) : y0;
        s.SetX(l, I16(x1));
        s.SetY(l, I16(y1));
        int frames = a.Has(6) ? MsToFrames60(a.Expr(6)) : 0;
        int ease = a.Expr(7);

        // live: 0x83350
        LayerRecord r = Drawn[l];
        short f16 = I16(frames);
        r.X = x1;
        r.Y = y1;
        r.TwMove = Tween.Start(I16(ease), f16);
        if (f16 == 0)
        {
            r.MoveDx = 0f;
            r.MoveDy = 0f;
        }
        else
        {
            r.MoveDx = x0 - x1;
            r.MoveDy = y0 - y1;
        }
    }

    private void CmdStma(in StageArgs a)
    {
        int l = a.Has(0) ? a.Expr(0) : 0;
        if (l is < 0 or > 39)
        {
            return;
        }

        int mid = a.Has(1) ? a.Expr(1) : 0;
        int n = a.Has(3) ? I16(a.Expr(3)) : 0;

        // the same record change is made to the scene record (+0xD00) and to the drawn record (+0x160)
        short id = Scene.GetMotionId(l);
        short div = Scene.GetMotionDivisor(l);
        long fr = Scene.GetMotionFrames(l);
        byte ps = Scene.GetMotionPause(l);
        MotionApply.MotionRecord(ref id, ref div, ref fr, ref ps, mid, n);
        Scene.SetMotionId(l, id);
        Scene.SetMotionDivisor(l, div);
        Scene.SetMotionFrames(l, fr);
        Scene.SetMotionPause(l, ps);
        LayerRecord r = Drawn[l];
        MotionApply.MotionRecord(r, mid, n);

        // word 0x164BA6E: the script waits for the motion when w == 1, or w is absent and the configuration byte is 0, or w == 2 and the byte is 1
        int w = a.Has(2) ? a.Expr(2) : -1;
        if (w == 1 || (!a.Has(2) && MotionWaitConfig == 0) || (w == 2 && MotionWaitConfig == 1))
        {
            MotionWaitRequested = true;
        }

        // 0xBE4C0(l, dt = 0): the motion at t = 0 is applied to the drawn record and to the scene state
        MotionApply.Apply(_motions, _catalog, r, l, 0.0, Scene);
    }

    private void CmdStns(in StageArgs a)
    {
        int l = a.Has(0) ? a.Expr(0) : 0;
        if (l is < 0 or > 39)
        {
            return;
        }

        int n = a.Has(1) ? a.Expr(1) & 0xFFFF : 0;
        byte b;
        if (n >= 13)
        {
            n = 0;
            b = 20;
        }
        else
        {
            b = n < 3 ? (byte)20 : (byte)12;
        }

        Scene.SetStns(l, (byte)n);
        Scene.SetStnsB(l, b);
        Drawn[l].Stns = (byte)n;
        Drawn[l].StnsB = b;
    }

    private void CmdSttr(in StageArgs a)
    {
        int l = a.Has(0) ? a.Expr(0) : 0;
        int n = a.Has(1) ? a.Expr(1) : 0;
        int m = a.Has(2) ? a.Expr(2) : 0;
        if (l is < 0 or > 39)
        {
            return;
        }

        int v = n > 0 ? ((n - 1) | (m << 12)) & 0xFFFF : 0xFFFF;
        Scene.SetSttr(l, I16(v));
        if (Drawn[l].Used != 0)
        {
            Drawn[l].Sttr = I16(v);
        }

        // 0x6B460(n - 1): the rule picture goes into the shared slot (a negative argument frees it)
        _ruleSlot = n - 1;
    }

    private void CmdStra(in StageArgs a)
    {
        int l = a.Has(0) ? a.Expr(0) : 0;
        if (l is < -1 or > 39)
        {
            return;
        }

        int n = a.Has(1) ? a.Expr(1) : 0;
        int frames = a.Has(2) ? MsToFrames(a.Expr(2)) : 0;
        short n16 = I16(n);
        short f16 = I16(frames);
        if (l == -1)
        {
            if (n16 <= 0)
            {
                // scene STRA and STNS bytes 0x16516D0.. cleared; every drawn record's STRA words (+0x2C8, +0x2CA) cleared
                for (int k = 0; k < LayerCount; k++)
                {
                    Scene.SetStra(k, 0);
                    Drawn[k].Stra = 0;
                    Drawn[k].StraPhase = 0;
                }
            }

            return;
        }

        Scene.SetStra(l, n16);
        LayerRecord r = Drawn[l];
        if (n16 != 0 || f16 == 0)
        {
            r.Stra = n16;
            r.StraPhase = 0;
            if (n16 != 0)
            {
                r.StraFadeIn = f16;
            }
            else
            {
                r.StraFadeLeft = f16;
                r.StraFadeTotal = f16;
            }
        }
        else
        {
            r.StraFadeLeft = f16;
            r.StraFadeTotal = f16;
        }
    }

    // STPV: inline arguments, its own layer parser (VA 0x86478) and the 0x76690 readers
    private void CmdStpv(in Statement st)
    {
        var a = new StageArgs(st);
        int l = StpvLayer(st);
        if (l is < 0 or > 39)
        {
            return;
        }

        SceneState s = Scene;
        int pic = s.GetPic(l);
        float fac = pic > 0 && pic < _catalog.Count ? _catalog.Info(pic).ClassFactor : 1.0f;
        bool p1 = a.InlinePresent(1);
        bool p2 = a.InlinePresent(2);
        if (p1)
        {
            float v = a.Inline16(1) * 640.0f / 480.0f * fac;
            s.SetAnchorX(l, I16(Cvtt(v)));
        }

        if (p2)
        {
            float v = a.Inline16(2) * 480.0f / 360.0f * fac;
            s.SetAnchorY(l, I16(Cvtt(v)));
        }

        // 0x86716..0x868C9: on a `_hh_vv` variant (hh_vv != 00_00) the anchor is given in the base picture's frame
        if (pic >= 0 && pic < _catalog.Count)
        {
            string n = _catalog.Name(pic);
            if (n.Length >= 7 && n[^6] == '_' && n[^3] == '_' && (n.StartsWith("IMG", StringComparison.OrdinalIgnoreCase) || n.Length >= 15)
                && !(n[^5] == '0' && n[^4] == '0' && n[^2] == '0' && n[^1] == '0'))
            {
                int bp = _catalog.Lookup(n[..^6]);
                if (bp >= 0)
                {
                    PictureInfo pi = _catalog.Info(pic);
                    PictureInfo bi = _catalog.Info(bp);
                    if (p1)
                    {
                        s.SetAnchorX(l, I16(s.GetAnchorX(l) + pi.AnchorX - bi.AnchorX));
                    }

                    if (p2)
                    {
                        s.SetAnchorY(l, I16(s.GetAnchorY(l) + pi.AnchorY - bi.AnchorY));
                    }
                }
            }
        }

        SetExtOffsets(l, s.GetAnchorX(l), s.GetAnchorY(l));
        if (a.InlinePresent(3))
        {
            s.SetRot(l, a.Inline16(3));
        }

        // live: 0x4B230(l, scene anchor x, scene anchor y) writes both anchors of the drawn record, whether or not the arguments were present
        LayerRecord r = Drawn[l];
        r.AnchorX = s.GetAnchorX(l);
        r.AnchorY = s.GetAnchorY(l);
    }

    // VA 0x86478: decimal digits of the first argument; '-' makes the number negative
    private static int StpvLayer(in Statement st)
    {
        ReadOnlySpan<byte> raw = st.Args.Span;
        int i = ArgumentListStart(raw);
        if (i < raw.Length && raw[i] == (byte)'`')
        {
            i += 5;
        }

        int v = 0;
        int sign = 1;
        int end = Math.Min(raw.Length, i + 16);
        for (; i < end; i++)
        {
            byte ch = raw[i];
            if (ch is (byte)',' or (byte)')' or (byte)';')
            {
                break;
            }

            if (ch == (byte)'-')
            {
                sign = -1;
            }
            else if (ch is >= (byte)'0' and <= (byte)'9')
            {
                v = (v * 10) + (ch - '0');
            }
        }

        return I16(v * sign);
    }

    private static int ArgumentListStart(ReadOnlySpan<byte> args) => Math.Min(1, args.Length);

    // -- quake / shake / swing and the screen effects --------------------------------------------------------------------------------------------------

    private void CmdSqk2(in StageArgs a)
    {
        int l = a.Has(0) ? a.Expr(0) : 0;
        if (l > 39)
        {
            return;
        }

        int dur = a.Has(4) ? a.Expr(4) : -1;
        int fade = a.Has(5) ? a.Expr(5) : 0;
        int p2;
        if (!a.Has(3))
        {
            p2 = 2;
        }
        else
        {
            int a3 = a.Expr(3);
            p2 = a3 < 0 ? 1 : (unchecked((uint)((3 * a3) + 0x31)) < 0x63u ? 2 : MsToFrames(a3) + 1);
        }

        int a7 = a.Has(7) ? a.Expr(7) : 0;
        int p4 = dur >= 0 && a7 <= 16 ? MsToFrames(dur) : 0;
        int[] par = [a.Has(1) ? a.Expr(1) : 15, a.Has(2) ? a.Expr(2) : 15, p2, MsToFrames(a7), p4, a.Has(6) ? a.Expr(6) : 0];
        StartShake(l, 1, par, dur, fade, Sqk2Targets);
    }

    private void CmdShk2(in StageArgs a)
    {
        int l = a.Has(0) ? a.Expr(0) : 0;
        if (l is < -1 or > 39)
        {
            return;
        }

        int[] par =
        [
            a.Has(1) ? a.Expr(1) : 0,
            a.Has(2) ? a.Expr(2) : 15,
            a.Has(3) ? MsToFrames(a.Expr(3)) : 18,
            a.Has(6) ? a.Expr(6) : 0,
            a.Has(7) ? MsToFrames(a.Expr(7)) : 0,
            a.Has(8) ? a.Expr(8) : 1,
        ];
        StartShake(l, 2, par, a.Has(4) ? a.Expr(4) : -1, a.Has(5) ? a.Expr(5) : 0, Shk2Targets);
    }

    private void CmdSsw2(in StageArgs a)
    {
        int l = a.Has(0) ? a.Expr(0) : 0;
        if (l is < 0 or > 39)
        {
            return;
        }

        int[] par =
        [
            a.Has(1) ? a.Expr(1) : 7,
            a.Has(2) ? a.Expr(2) : 15,
            a.Has(3) ? MsToFrames(a.Expr(3)) : 60,
            a.Has(4) ? MsToFrames(a.Expr(4)) : 120,
            a.Has(5) ? a.Expr(5) : 1,
            0,
        ];
        StartShake(l, 3, par, a.Has(6) ? a.Expr(6) : -1, a.Has(7) ? a.Expr(7) : 0, Sqk2Targets);
    }

    private void StartShake(int l, int kind, int[] par, int dur, int fade, Func<List<int>> targets)
    {
        // 0x84670: with a duration, nothing starts in scan mode (the re-execution after a slot load)
        if (dur >= 0 && ScanMode)
        {
            return;
        }

        List<int> list = l >= 0 ? [l] : targets();
        int slot = kind == 1 ? 0 : 1;
        foreach (int k in list)
        {
            SceneState s = Scene;
            s.SetShakeKind(k, slot, (byte)kind);
            s.SetShakeFade(k, slot, I16(MsToFrames(fade)));
            s.SetShakeTime(k, slot, dur >= 0 ? I16(MsToFrames(dur)) : (short)-1);
            for (int i = 0; i < 6; i++)
            {
                s.SetShakePar(k, (6 * slot) + i, I16(par[i]));
            }

            Shakes.Start(new SlotStart(k, kind, par, dur, fade));
        }

        SceneDirty = true;
    }

    // 0xB6E08: layers 0 and 1 if in use; 2..39 if (in use and committed z above 0) or the scene has a picture
    private List<int> Sqk2Targets()
    {
        var t = new List<int>();
        for (int k = 0; k < 2; k++)
        {
            if (Drawn[k].Used != 0)
            {
                t.Add(k);
            }
        }

        for (int k = 2; k < LayerCount; k++)
        {
            if ((Drawn[k].Used != 0 && Drawn[k].ZOrder > 0) || Scene.GetPic(k) != -1)
            {
                t.Add(k);
            }
        }

        return t;
    }

    // 0xB7250: as Sqk2Targets but layers 0 and 1 also need a committed z above 0
    private List<int> Shk2Targets()
    {
        var t = new List<int>();
        for (int k = 0; k < 2; k++)
        {
            if (Drawn[k].Used != 0 && Drawn[k].ZOrder > 0)
            {
                t.Add(k);
            }
        }

        for (int k = 2; k < LayerCount; k++)
        {
            if ((Drawn[k].Used != 0 && Drawn[k].ZOrder > 0) || Scene.GetPic(k) != -1)
            {
                t.Add(k);
            }
        }

        return t;
    }

    private void CmdStopShake(in StageArgs a, int kind)
    {
        int l = a.Has(0) ? a.Expr(0) : 0;
        if (l > 39)
        {
            return;
        }

        int slot = kind == 1 ? 0 : 1;
        int from = l < 0 ? 0 : l;
        int to = l < 0 ? LayerCount : l + 1;
        for (int k = from; k < to; k++)
        {
            Scene.SetShakeKind(k, slot, 0);
            Scene.SetShakeFade(k, slot, 0);
            Scene.SetShakeTime(k, slot, 0);
            for (int i = 0; i < 6; i++)
            {
                Scene.SetShakePar(k, (6 * slot) + i, 0);
            }
        }

        Shakes.Stop(l, kind);
    }

    private void CmdShok(in Statement st)
    {
        // handler 0x98C44: nothing during a next-scene skip or in scan mode
        if (ScanMode || SkipActive)
        {
            return;
        }

        var a = new StageArgs(st);
        int[] v = [0, 0, 300, 3, 0];
        for (int i = 0; i < 5; i++)
        {
            if (a.InlinePresent(i))
            {
                v[i] = a.Inline16(i);
            }
        }

        Shock.Start(v[0], v[1], v[2], v[3], v[4]);
    }

    /// <summary>Word 0x120A6D2: a next-scene skip runs; <c>_SHOK</c> does nothing then.</summary>
    public bool SkipActive { get; set; }

    private void CmdSgma(in StageArgs a)
    {
        int l = a.Has(0) ? a.Expr(0) : 0;
        if (l is < 0 or > 39 || !a.Has(1) || a.Expr(1) <= 0)
        {
            return;
        }

        SceneDirty = true;
        MotionMath.EditMotion(_motions, Drawn[l], a.Expr(1), a.Has(2) ? a.Expr(2) : 0);
    }

    private void CmdFads(in StageArgs a)
    {
        int type = a.Expr(0);
        int ms = a.Expr(1);
        int rule = a.Expr(2);
        if (rule > 0 && !SkipActive)
        {
            _ruleSlot = rule - 1;
        }

        Commit(type, ms, rule);
    }
}
