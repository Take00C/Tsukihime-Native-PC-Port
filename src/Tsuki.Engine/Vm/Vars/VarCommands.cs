namespace Tsuki.Engine.Vm.Variables;

/// <summary>Operand positions with different first-byte dispatches (docs/formats/vm_variables.md 3).</summary>
public enum OperandRole : byte
{
    /// <summary>WKST, WKAD target: <c>C F G _</c>.</summary>
    WkTarget = 0,

    /// <summary>WKOR, WKOF target (and every Script_Prefetch target): <c>F G _</c>.</summary>
    OrTarget = 1,

    /// <summary>WKBO target: <c>F G</c>.</summary>
    BoTarget = 2,

    /// <summary>Value (WKST, WKAD, WKOR, WKOF value, IF__ both sides): <c>F G _</c>, everything else literal.</summary>
    Value = 3,

    /// <summary>GOTS selector: <c>_</c> + 3 digit bytes.</summary>
    Gots = 4,

    /// <summary>SELD / SELA / SELS target: <c>_D</c> + 3 digit bytes.</summary>
    Choice = 5,
}

/// <summary>
/// The variable commands of the script VM with the engine's exact read / write semantics: <c>_WKST</c>, <c>_WKAD</c>,
/// <c>_WKOR</c>, <c>_WKOF</c>, <c>_WKBO</c>, <c>_IF__</c>, <c>_GOTS</c>, <c>_SELD</c>, <c>_SELA</c>, <c>_SELS</c>,
/// <c>_SELR</c>, the choice result store and the History_Snapshot gate (docs/formats/vm_variables.md 3-5). Byte-exact
/// port of <c>tsuki.vm_vars</c> (<c>execute</c>, <c>choice_store</c>, <c>snapshot_gate</c>), which reproduces the
/// engine on 61,000 fuzzed statements under unicorn (<c>tsuki.misc_vm_variables_errata</c>).
/// </summary>
/// <remarks>
/// <para>
/// Statements are read from a byte span and the position of the first name byte (the byte after <c>_</c>); the first
/// argument byte is <c>namePos + 5</c>. Bytes past the end of the span read as 0 and a copy loop stops there as at a
/// <c>;</c>, exactly as in the reference.
/// </para>
/// <para>
/// Nothing allocates except the label of a jump (<see cref="VarEffect.Goto"/>, about 1,400 statements in the game).
/// </para>
/// </remarks>
public static class VarCommands
{
    /// <summary>Names the main interpreter (ScriptStep) handles here.</summary>
    public static readonly IReadOnlyList<string> HandledMain =
        ["WKST", "WKAD", "WKOR", "WKOF", "WKBO", "IF__", "GOTS", "SELD", "SELA", "SELS", "SELR"];

    /// <summary>Names Script_Prefetch (VA 0x9B760) handles here.</summary>
    public static readonly IReadOnlyList<string> HandledPrefetch = ["WKST", "WKAD", "WKOR", "WKOF", "IF__"];

    private const int MaxSkip = 500;
    private const byte Backtick = 0x60;

    private static uint Name(string s) => (uint)s[0] << 24 | (uint)s[1] << 16 | (uint)s[2] << 8 | s[3];

    private static readonly uint WKST = Name("WKST");
    private static readonly uint WKAD = Name("WKAD");
    private static readonly uint WKOR = Name("WKOR");
    private static readonly uint WKOF = Name("WKOF");
    private static readonly uint WKBO = Name("WKBO");
    private static readonly uint IF__ = Name("IF__");
    private static readonly uint GOTS = Name("GOTS");
    private static readonly uint SELD = Name("SELD");
    private static readonly uint SELA = Name("SELA");
    private static readonly uint SELS = Name("SELS");
    private static readonly uint SELR = Name("SELR");

    /// <summary>
    /// Runs the variable semantics of the statement whose four name bytes start at <c>buf[namePos]</c> (the byte after
    /// <c>_</c>). <paramref name="prefetch"/> selects Script_Prefetch's handlers (VA 0x9B760: no <c>C</c> targets, no
    /// WKBO / GOTS / SEL*). Other names return <see cref="VarEffect.NotHandled"/> and change nothing.
    /// </summary>
    public static VarEffect Execute(ReadOnlySpan<byte> buf, int namePos, VarState state, bool prefetch = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (namePos < 0 || namePos + 4 > buf.Length)
        {
            return VarEffect.NotHandled;
        }

        uint name = (uint)buf[namePos] << 24 | (uint)buf[namePos + 1] << 16 | (uint)buf[namePos + 2] << 8 | buf[namePos + 3];
        int p0 = namePos + 5;
        if (name == WKST)
        {
            return WkSt(buf, p0, state, prefetch);
        }

        if (name == WKAD)
        {
            return Rmw(buf, p0, state, prefetch ? OperandRole.OrTarget : OperandRole.WkTarget, RmwOp.Add);
        }

        if (name == WKOR)
        {
            return Rmw(buf, p0, state, OperandRole.OrTarget, RmwOp.Or);
        }

        if (name == WKOF)
        {
            return Rmw(buf, p0, state, OperandRole.OrTarget, RmwOp.AndNot);
        }

        if (name == IF__)
        {
            return If(buf, p0, state);
        }

        if (prefetch)
        {
            return VarEffect.NotHandled;
        }

        if (name == WKBO)
        {
            return WkBo(buf, p0, state);
        }

        if (name == GOTS)
        {
            return Gots(buf, p0, state);
        }

        if (name == SELD)
        {
            return SelD(buf, p0, state);
        }

        if (name == SELA)
        {
            return SelA(buf, p0, state);
        }

        if (name == SELS)
        {
            return new VarEffect(true, Yielded: true, ChoiceUi: TwoAnswerDialog(buf, p0, state, ChoiceMode.TwoButtons));
        }

        if (name == SELR)
        {
            return SelR(buf, p0, state);
        }

        return VarEffect.NotHandled;
    }

    /// <summary>Runs one statement text such as <c>"_WKST(C847,eq,0);"</c> (Latin-1; for tests and tools).</summary>
    public static VarEffect ExecuteText(string statement, VarState state, bool prefetch = false)
    {
        ArgumentNullException.ThrowIfNull(statement);
        byte[] b = System.Text.Encoding.Latin1.GetBytes(statement);
        return Execute(b, Array.IndexOf(b, (byte)'_') + 1, state, prefetch);
    }

    /// <summary>
    /// History_Snapshot's entry test (VA 0x7AF9B..0x7AFD7), in the engine's order: scan mode -> no; F929 != 0 -> no;
    /// F1102 != 0 and <paramref name="kind"/> not 0 or 11 -> no (F1102 is read only when F929 == 0); block byte != 0 -> no.
    /// </summary>
    public static bool SnapshotGate(VarState state, int kind)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ScanMode)
        {
            return false;
        }

        VarMemory mem = state.Memory;
        if (mem.Read(VarAddresses.OfF(VarAddresses.SnapshotGateVar)) != 0)
        {
            return false;
        }

        if (mem.Read(VarAddresses.OfF(VarAddresses.SnapshotModeVar)) != 0 && kind is not (0 or 0xB))
        {
            return false;
        }

        return state.SnapshotBlock == 0;
    }

    /// <summary>
    /// The choice UI's result store when the player confirms line <paramref name="cursor"/> (u16 VA 0x17506A2), by
    /// choice mode (dispatch VA 0xA6180): list (mode 1 and any mode other than 2 / 3) stores values[s16 cursor] (0 for
    /// a cursor outside the registered lines); two buttons (mode 2) store the cursor itself when it is 0 or 1; the save
    /// prompt (mode 3) stores nothing. Kind 'F' writes var_F[s16 index], kind 'Z' var_Z[s16 index], any other kind
    /// nothing. Returns the address written, or null.
    /// </summary>
    public static long? ChoiceStore(VarState state, int cursor)
    {
        ArgumentNullException.ThrowIfNull(state);
        int value;
        if (state.ChoiceMode == ChoiceMode.SavePrompt)
        {
            return null;
        }

        if (state.ChoiceMode == ChoiceMode.TwoButtons)
        {
            if ((cursor & 0xFFFF) > 1)
            {
                return null;
            }

            value = cursor & 0xFFFF;
        }
        else
        {
            int i = (short)cursor;
            value = i >= 0 && i < state.ChoiceValues.Count ? state.ChoiceValues[i] : 0;
        }

        long a;
        if (state.ChoiceKind == 0x46)
        {
            a = VarAddresses.F + (2L * (short)state.ChoiceIndex);
        }
        else if (state.ChoiceKind == 0x5A)
        {
            a = VarAddresses.Z + (2L * (short)state.ChoiceIndex);
        }
        else
        {
            return null;
        }

        state.Memory.Write(a, value);
        return a;
    }

    // -----------------------------------------------------------------------------------------------------------
    // argument access (the inlined copies of the inline reader's loops)
    // -----------------------------------------------------------------------------------------------------------

    private static byte At(ReadOnlySpan<byte> buf, int i) => (uint)i < (uint)buf.Length ? buf[i] : (byte)0;

    /// <summary>
    /// Start of argument <paramref name="index"/> (that many commas skipped, at most 500 bytes each), or -1 when <c>)</c>
    /// or <c>;</c> comes first. Same loop as the inline reader VA 0x76690 (<c>tsuki.vm_vars.arg_start</c>).
    /// </summary>
    public static int ArgStart(ReadOnlySpan<byte> buf, int p0, int index)
    {
        int p = p0;
        for (int a = 0; a < index; a++)
        {
            int k = 0;
            for (; k < MaxSkip; k++)
            {
                byte c = At(buf, p + k);
                if (c is 0x29 or 0x3B)
                {
                    return -1;
                }

                if (c == 0x2C)
                {
                    p += k + 1;
                    break;
                }
            }

            if (k == MaxSkip)
            {
                p += MaxSkip;
            }
        }

        return p;
    }

    /// <summary>
    /// The handlers' argument copy into the scratch buffer (VA 0x96566..0x965A7 and others): a leading backtick skips 5
    /// bytes, then bytes are copied up to the first <c>)</c> <c>,</c> <c>;</c> (NUL is copied like any byte; the span end
    /// stops it) and a NUL is appended when there is room. A missing argument only stores NUL at buffer[0]. Returns
    /// buffer[0] (<c>tsuki.vm_vars.copy_arg</c>).
    /// </summary>
    public static byte CopyArg(ReadOnlySpan<byte> buf, int p0, int index, VarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        int p = index > 0 ? ArgStart(buf, p0, index) : p0;
        if (p < 0)
        {
            state.ArgBuffer[0] = 0;
            return 0;
        }

        CopyFrom(buf, p, state.ArgBuffer);
        return state.ArgBuffer[0];
    }

    private static void CopyFrom(ReadOnlySpan<byte> buf, int p, byte[] ab)
    {
        if (At(buf, p) == Backtick)
        {
            p += 5;
        }

        int k = 0;
        while (true)
        {
            int i = p + k;
            byte c = i < buf.Length ? At(buf, i) : (byte)0x3B;
            if (c is 0x29 or 0x2C or 0x3B)
            {
                break;
            }

            if (k < ab.Length)
            {
                ab[k] = c;
            }

            k++;
        }

        if (k < ab.Length)
        {
            ab[k] = 0;
        }
    }

    // -----------------------------------------------------------------------------------------------------------
    // operand -> address
    // -----------------------------------------------------------------------------------------------------------

    /// <summary>True when an <c>_</c> operand value selects G[n-600] (VA 0x9880F: n in 600..700 mod 2^16).</summary>
    public static bool UnderscoreIsG(int n) => ((n - 701) & 0xFFFF) > 0xFF9A;

    /// <summary><c>_</c> operands: n in 600..700 (mod 2^16) selects G[n-600], everything else F[s16 n].</summary>
    public static long UnderscoreAddress(int n) =>
        UnderscoreIsG(n) ? VarAddresses.G + (2L * ((n & 0xFFFF) - 600)) : VarAddresses.F + (2L * (short)n);

    /// <summary>
    /// Address read for a value operand with first byte <paramref name="first"/> and inline value <paramref name="n"/>,
    /// or null for a literal (dispatch VA 0x987E3 and its copies).
    /// </summary>
    public static long? ValueAddress(byte first, int n) => first switch
    {
        0x46 => VarAddresses.F + (2L * (short)n),
        0x47 => VarAddresses.G + (2L * (short)n),
        0x5F => UnderscoreAddress(n),
        _ => null,
    };

    /// <summary>
    /// Address written by a target operand in <paramref name="role"/>, or null when the command does nothing. A <c>C</c>
    /// target reads F823 (logged) to form F[F823 + n].
    /// </summary>
    public static long? TargetAddress(byte first, int n, OperandRole role, VarMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (first == 0x43 && role == OperandRole.WkTarget)
        {
            int b = (short)memory.Read(VarAddresses.OfF(VarAddresses.MessageIndexVar));
            return VarAddresses.F + (2L * (b + (short)n));
        }

        if (first == 0x46)
        {
            return VarAddresses.F + (2L * (short)n);
        }

        if (first == 0x47)
        {
            return VarAddresses.G + (2L * (short)n);
        }

        if (first == 0x5F && role is OperandRole.WkTarget or OperandRole.OrTarget)
        {
            return UnderscoreAddress(n);
        }

        return null;
    }

    /// <summary>GOTS / SELD / SELA / SELS index: bytes 2..4 as decimal digits without any check (b2*100 + b3*10 + b4 - 5328).</summary>
    public static int ThreeDigits(ReadOnlySpan<byte> b) => (At(b, 2) * 100) + (At(b, 3) * 10) + At(b, 4) - 0x14D0;

    private static int OperandValue(byte first, int n, VarMemory mem)
    {
        long? a = ValueAddress(first, n);
        return a is { } va ? mem.Read(va) : n & 0xFFFF;
    }

    private static int WkValue(ReadOnlySpan<byte> buf, int p0, int index, VarState st)
    {
        byte first = CopyArg(buf, p0, index, st);
        int n = InlineReader.ReadInt16(buf, p0, index);
        return OperandValue(first, n, st.Memory);
    }

    // -----------------------------------------------------------------------------------------------------------
    // handlers
    // -----------------------------------------------------------------------------------------------------------

    private enum RmwOp : byte
    {
        Add,
        Or,
        AndNot,
    }

    /// <summary>ScriptStep 0x96566 / Script_Prefetch 0x9BFA2: target = value; only a leading 'e' of argument 1 makes argument 2 the value.</summary>
    private static VarEffect WkSt(ReadOnlySpan<byte> buf, int p0, VarState st, bool prefetch)
    {
        byte first0 = CopyArg(buf, p0, 0, st);
        int n0 = InlineReader.ReadInt16(buf, p0, 0);
        byte first1 = CopyArg(buf, p0, 1, st);
        int value = first1 == 0x65
            ? WkValue(buf, p0, 2, st)
            : OperandValue(first1, InlineReader.ReadInt16(buf, p0, 1), st.Memory);
        if (TargetAddress(first0, n0, prefetch ? OperandRole.OrTarget : OperandRole.WkTarget, st.Memory) is { } a)
        {
            st.Memory.Write(a, value);
        }

        return VarEffect.Done;
    }

    private static VarEffect Rmw(ReadOnlySpan<byte> buf, int p0, VarState st, OperandRole role, RmwOp op)
    {
        byte first0 = CopyArg(buf, p0, 0, st);
        int n0 = InlineReader.ReadInt16(buf, p0, 0);
        int value = WkValue(buf, p0, 1, st);
        if (TargetAddress(first0, n0, role, st.Memory) is { } a)
        {
            int old = st.Memory.Read(a);
            st.Memory.Write(a, op switch
            {
                RmwOp.Add => old + value,
                RmwOp.Or => old | value,
                _ => old & ~value,
            });
        }

        return VarEffect.Done;
    }

    /// <summary>VA 0x97BB8: target |= 1 &lt;&lt; (int(arg1) &amp; 31), 16-bit store; argument 1 is never a variable and not copied.</summary>
    private static VarEffect WkBo(ReadOnlySpan<byte> buf, int p0, VarState st)
    {
        byte first0 = CopyArg(buf, p0, 0, st);
        int n0 = InlineReader.ReadInt16(buf, p0, 0);
        int bit = (int)((1u << (InlineReader.ReadInt16(buf, p0, 1) & 31)) & 0xFFFF);
        if (TargetAddress(first0, n0, OperandRole.BoTarget, st.Memory) is { } a)
        {
            st.Memory.Write(a, st.Memory.Read(a) | bit);
        }

        return VarEffect.Done;
    }

    /// <summary>IF__ operator (VA 0x9A474..0x9A64C): first byte e/n/g/l, a second byte 'e' makes g/l inclusive; signed 16-bit compares.</summary>
    public static bool Condition(byte op0, byte op1, int lhs, int rhs)
    {
        int a = (short)lhs;
        int b = (short)rhs;
        return op0 switch
        {
            0x65 => a == b,
            0x6E => a != b,
            0x67 => op1 == 0x65 ? a >= b : a > b,
            0x6C => op1 == 0x65 ? a <= b : a < b,
            _ => false,
        };
    }

    /// <summary>
    /// ScriptStep 0x9708D / Script_Prefetch 0x9BAD5: <c>_IF__(lhs,op,rhs,label)</c> continues when <c>lhs op rhs</c>
    /// holds and jumps to <c>label</c> when it does NOT hold (a structured if: the label ends the block).
    /// </summary>
    private static VarEffect If(ReadOnlySpan<byte> buf, int p0, VarState st)
    {
        if (st.SkipCounter != 0)
        {
            return VarEffect.Done;
        }

        int lhs = WkValue(buf, p0, 0, st);
        int rhs = WkValue(buf, p0, 2, st);
        CopyArg(buf, p0, 1, st);
        if (Condition(st.ArgBuffer[0], st.ArgBuffer[1], lhs, rhs))
        {
            return VarEffect.Done;
        }

        if (!InlineReader.IsPresent(buf, p0, 3))
        {
            return VarEffect.Done;
        }

        CopyArg(buf, p0, 3, st);
        return new VarEffect(true, st.ArgText.ToArray());
    }

    /// <summary>
    /// VA 0x9609A: computed goto. Selector <c>_?ddd</c>: v = F[ddd] (or G[ddd-600]); the label is argument s16(v + 1),
    /// argument 0 itself when that is &lt;= 0, the empty text when the argument does not exist.
    /// </summary>
    private static VarEffect Gots(ReadOnlySpan<byte> buf, int p0, VarState st)
    {
        if (st.ScanMode)
        {
            return new VarEffect(true, Yielded: true);
        }

        if (st.SkipCounter != 0)
        {
            return VarEffect.Done;
        }

        if (CopyArg(buf, p0, 0, st) != 0x5F)
        {
            return VarEffect.Done;
        }

        int n = ThreeDigits(st.ArgBuffer);
        ushort v = UnderscoreIsG(n)
            ? st.Memory.Read(VarAddresses.G + (2L * ((n & 0xFFFF) - 600)))
            : st.Memory.Read(VarAddresses.F + (2L * n));
        int k = (short)(v + 1);
        int p = k > 0 ? ArgStart(buf, p0, k) : p0;
        if (p < 0)
        {
            st.ArgBuffer[0] = 0;
        }
        else
        {
            CopyFrom(buf, p, st.ArgBuffer);
        }

        return new VarEffect(true, st.ArgText.ToArray());
    }

    /// <summary>
    /// SELD / SELS / SELA target decode: only a target starting with <c>_</c> is stored, kind 'F' when byte 1 is 'D',
    /// else 'G'; index = 3 digit bytes. Any other target leaves the previous kind / index in place.
    /// </summary>
    private static void ChoiceTarget(ReadOnlySpan<byte> b, VarState st)
    {
        if (At(b, 0) != 0x5F)
        {
            return;
        }

        st.ChoiceKind = At(b, 1) == 0x44 ? (byte)0x46 : (byte)0x47;
        st.ChoiceIndex = unchecked((ushort)ThreeDigits(b));
    }

    /// <summary>ScriptStep's SELD (VA 0x9B1DC): in scan mode it yields at once; else mode 1, target, choice id, UI set-up, yield.</summary>
    private static VarEffect SelD(ReadOnlySpan<byte> buf, int p0, VarState st)
    {
        if (st.ScanMode)
        {
            return new VarEffect(true, Yielded: true);
        }

        st.ChoiceMode = ChoiceMode.List;
        CopyArg(buf, p0, 0, st);
        ChoiceTarget(st.ArgBuffer, st);
        int? point = null;
        if (InlineReader.IsPresent(buf, p0, 1))
        {
            st.ChoiceId = unchecked((short)InlineReader.ReadInt16(buf, p0, 1));
            point = st.ChoiceId;
        }
        else
        {
            st.ChoiceId = -1;
        }

        return new VarEffect(true, Yielded: true, ChoiceUi: true, ChoicePoint: point);
    }

    /// <summary>
    /// The SELS helper VA 0xB82E0 (mode 2) / SELA helper VA 0xB8400 (mode 3). Scan mode: no change. Otherwise mode, target
    /// via the object parser (argument 0, at most 15 bytes, NUL-padded), id -1, count 2, values {0, 1}, UI set-up.
    /// </summary>
    private static bool TwoAnswerDialog(ReadOnlySpan<byte> buf, int p0, VarState st, uint mode)
    {
        if (st.ScanMode)
        {
            return false;
        }

        st.ChoiceMode = mode;
        Span<ArgRange> ranges = stackalloc ArgRange[ObjectParser.MaxArguments];
        Span<byte> target = stackalloc byte[15 + 5];
        target.Clear();
        if (ObjectParser.Split(buf, p0 - 1, ranges, out int count) == SplitStatus.Ok && count > 0 && ranges[0].Start < ranges[0].End)
        {
            ArgRange r = ranges[0];
            int end = Math.Min(Math.Min(r.End, r.Start + 15), buf.Length);
            if (end > r.Start)
            {
                buf[r.Start..end].CopyTo(target);
            }
        }

        ChoiceTarget(target, st);
        st.ChoiceId = -1;
        st.ChoiceCount = 2;
        st.ChoiceValues.Clear();
        st.ChoiceValues.Add(0);
        st.ChoiceValues.Add(1);
        return true;
    }

    /// <summary>ScriptStep's SELA (VA 0x9B1B6): opens the save prompt only when F1118 == 0, then always F1118 = 0, and yields.</summary>
    private static VarEffect SelA(ReadOnlySpan<byte> buf, int p0, VarState st)
    {
        long a = VarAddresses.OfF(VarAddresses.SavePromptVar);
        bool opened = false;
        if (st.Memory.Read(a) == 0)
        {
            opened = TwoAnswerDialog(buf, p0, st, ChoiceMode.SavePrompt);
        }

        st.Memory.Write(a, 0);
        return new VarEffect(true, Yielded: true, ChoiceUi: opened);
    }

    /// <summary>
    /// SELR's variable side (VA 0x9936D..0x99525): for the first line of a group the History_Snapshot gate (kind 0xD) and,
    /// when F823 &gt; 0, C847 += 1 (also in scan mode); every line stores argument 0 as its value and argument 2 == 1 as its
    /// locked flag and counts itself. SELR does not yield.
    /// </summary>
    private static VarEffect SelR(ReadOnlySpan<byte> buf, int p0, VarState st)
    {
        int value = InlineReader.ReadInt16(buf, p0, 0);
        bool snap = false;
        if (st.ChoiceCount == 0)
        {
            snap = SnapshotGate(st, 0xD);
            int k = (short)st.Memory.Read(VarAddresses.OfF(VarAddresses.MessageIndexVar));
            if (k > 0)
            {
                long a = VarAddresses.OfF(k + 847);
                st.Memory.Write(a, st.Memory.Read(a) + 1);
            }
        }

        Truncate(st.ChoiceValues, st.ChoiceCount);
        Truncate(st.ChoiceLocked, st.ChoiceCount);
        st.ChoiceValues.Add(unchecked((ushort)value));
        st.ChoiceLocked.Add(InlineReader.ReadInt16(buf, p0, 2) == 1);
        st.ChoiceCount++;
        return new VarEffect(true, Snapshot: snap);
    }

    private static void Truncate<T>(List<T> list, int count)
    {
        if (count >= 0 && list.Count > count)
        {
            list.RemoveRange(count, list.Count - count);
        }
    }
}
