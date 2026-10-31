using Tsuki.Engine.Contracts;
using Tsuki.Engine.Vm;
using Tsuki.Engine.Vm.Variables;

namespace Tsuki.Engine.Flow;

/// <summary>The scripts and label tables the VM can switch to (a jump to another scene, a load).</summary>
public interface IFlowScripts
{
    /// <summary>Script <paramref name="k"/> (<see cref="IGameData.Script"/>).</summary>
    ScriptImage Script(int k);

    /// <summary>The label table of script <paramref name="k"/> (allscr section 1, child k).</summary>
    LabelTable Labels(int k);
}

/// <summary>Why <see cref="ScriptVm.Step"/> returned.</summary>
public enum StepEnd : byte
{
    /// <summary>A statement yielded; the next call resumes at <see cref="ScriptVm.Position"/>.</summary>
    Yielded = 0,

    /// <summary>1000 segments ran without a yield (VA 0x95EB0 budget).</summary>
    Budget = 1,

    /// <summary>The host halted the VM.</summary>
    Halted = 2,

    /// <summary>The cursor reached the end of the script.</summary>
    EndOfScript = 3,
}

/// <summary>What the VM did with a statement (for traces).</summary>
public enum FlowAction : byte
{
    /// <summary>A variable command (WK*, SEL* variable side).</summary>
    Variables = 0,

    /// <summary>A flow command (IF__, GOTO, GOTS, IFNX) that did not jump.</summary>
    NoJump = 1,

    /// <summary>A flow command that jumped to <see cref="FlowStep.Target"/>.</summary>
    Jump = 2,

    /// <summary>A marker (<c>_Z*</c>); <c>ZM</c> also went to the host.</summary>
    Marker = 3,

    /// <summary>Dispatched to the host.</summary>
    Host = 4,
}

/// <summary>One executed statement, as reported to <see cref="ScriptVm.Observer"/>.</summary>
/// <param name="Script">Script number.</param>
/// <param name="Offset">Segment start.</param>
/// <param name="Index">Statement (segment) index in the script, or -1 when the cursor was not at a segment start.</param>
/// <param name="Name">The 4 name bytes.</param>
/// <param name="Action">What the VM did.</param>
/// <param name="Writes">Variable writes of the statement in program order (address, value).</param>
/// <param name="Goto">The label text of a jump, or null.</param>
/// <param name="Target">The jump target, or <see cref="ScriptPosition.None"/>.</param>
/// <param name="Result">How the VM goes on.</param>
public sealed record FlowStep(int Script, int Offset, int Index, CommandName Name, FlowAction Action,
    IReadOnlyList<(long Va, ushort Value)> Writes, byte[]? Goto, ScriptPosition Target, StepKind Result)
{
    /// <summary>The display name: the 4 name bytes, or <c>ZM</c> / <c>ZZ</c> / <c>ZY</c> / <c>ZD</c> for markers (<c>tsuki.script.Command.name</c>).</summary>
    public string DisplayName
    {
        get
        {
            string s = Name.ToString();
            return s[0] == 'Z' ? s[..2] : s;
        }
    }
}

/// <summary>A jump to a label the script's table does not have (the engine reads garbage; no retail label is missing).</summary>
public sealed class ScriptFlowException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ScriptFlowException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ScriptFlowException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public ScriptFlowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The main interpreter loop (ScriptStep, eboot VA 0x95E60; docs/formats/script.md 3.1, 3.6, 3.7; engine.md 5.2): it
/// segments from the cursor, runs at most 1000 segments per <see cref="Step"/> call, and executes the VM's own commands
/// itself: the variables (<see cref="VarCommands"/>), the flow commands <c>_IF__</c> (jumps when its condition is false),
/// <c>_GOTO</c>, <c>_GOTS</c>, <c>_IFNX</c>, the variable side of <c>_SEL*</c> and the common marker processing. Every
/// other command goes to <see cref="IHost.Dispatch"/>, whose result (and target, e.g. for <c>_JUMP</c>) the loop follows.
/// </summary>
/// <remarks>
/// <para>
/// Frame behaviour of the jumps, measured under unicorn on the eboot's own handlers (docs/status/wave2/C2.md): <c>_IF__</c>
/// and <c>_GOTO</c> yield with the cursor at the label (the next call starts there); <c>_GOTS</c> and <c>_IFNX</c>
/// continue at the label in the same call; a true <c>_IF__</c> and an <c>_IFNX</c> without label continue with the next
/// statement.
/// </para>
/// <para>
/// A segment the engine would cut at 0x300 bytes (<see cref="ScriptSegment.Truncated"/>, never in retail data) is a hard
/// error in dev builds.
/// </para>
/// </remarks>
public sealed class ScriptVm
{
    private static readonly uint WKST = Key("WKST");
    private static readonly uint WKAD = Key("WKAD");
    private static readonly uint WKOR = Key("WKOR");
    private static readonly uint WKOF = Key("WKOF");
    private static readonly uint WKBO = Key("WKBO");
    private static readonly uint IF__ = Key("IF__");
    private static readonly uint GOTO = Key("GOTO");
    private static readonly uint GOTS = Key("GOTS");
    private static readonly uint IFNX = Key("IFNX");
    private static readonly uint SELD = Key("SELD");
    private static readonly uint SELA = Key("SELA");
    private static readonly uint SELS = Key("SELS");
    private static readonly uint SELR = Key("SELR");

    private readonly IFlowScripts scripts;
    private ScriptImage image;
    private LabelTable labels;

    /// <summary>Creates a VM at offset <paramref name="offset"/> of script <paramref name="script"/>.</summary>
    public ScriptVm(IFlowScripts scripts, VarState vars, MarkerState markers, int script, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(vars);
        ArgumentNullException.ThrowIfNull(markers);
        this.scripts = scripts;
        Vars = vars;
        Markers = markers;
        image = scripts.Script(script);
        labels = scripts.Labels(script);
        Cursor = offset;
    }

    /// <summary>The variable state.</summary>
    public VarState Vars { get; }

    /// <summary>The marker state (read tracking).</summary>
    public MarkerState Markers { get; }

    /// <summary>The script being run.</summary>
    public ScriptImage Image => image;

    /// <summary>The byte offset of the next segment.</summary>
    public int Cursor { get; private set; }

    /// <summary>The position of the next segment.</summary>
    public ScriptPosition Position => new(image.Index, Cursor);

    /// <summary>The local-label flag (VA 0x165420C), set to 1 by a <c>_ZY</c> marker.</summary>
    public int LocalLabelFlag { get; set; }

    /// <summary>When set, called for every executed statement (allocates the write list; for traces and tests).</summary>
    public Action<FlowStep>? Observer { get; set; }

    /// <summary>Moves the cursor (a load, a rewind, a scene change by the caller).</summary>
    public void Seek(ScriptPosition position)
    {
        if (position.Script != image.Index)
        {
            image = scripts.Script(position.Script);
            labels = scripts.Labels(position.Script);
        }

        Cursor = position.Offset;
    }

    /// <summary>One ScriptStep call: runs segments until a statement yields or halts, or 1000 segments ran.</summary>
    public StepEnd Step(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        for (int n = 0; n < Statement.SegmentsPerStep; n++)
        {
            ReadOnlyMemory<byte> bytes = image.Bytes;
            if (Cursor >= bytes.Length)
            {
                return StepEnd.EndOfScript;
            }

            ScriptSegment seg = ScriptSegmenter.Read(bytes.Span, Cursor);
#if TSUKI_DEV
            if (seg.Truncated)
            {
                throw new ScriptFlowException($"script {image.Index}: no ';' within 0x300 bytes at 0x{seg.Start:x}");
            }
#endif
            Cursor = seg.End;
            if (!ScriptSegmenter.TryGetStatement(image.Index, bytes, seg, out Statement st))
            {
                continue;
            }

            StepResult r = Execute(st, seg, host);
            if (r.HasTarget)
            {
                Seek(r.Target);
            }

            if (r.Kind == StepKind.Yield)
            {
                return StepEnd.Yielded;
            }

            if (r.Kind == StepKind.Halt)
            {
                return StepEnd.Halted;
            }
        }

        return StepEnd.Budget;
    }

    private static uint Key(string s) => CommandName.FromAscii(s).Value;

    private StepResult Execute(in Statement st, in ScriptSegment seg, IHost host)
    {
        List<VarAccess>? outer = Vars.Memory.Log;
        List<VarAccess>? log = null;
        if (Observer is not null)
        {
            log = [];
            Vars.Memory.Log = log;
        }

        try
        {
            return Run(st, seg, host, log);
        }
        finally
        {
            if (log is not null)
            {
                Vars.Memory.Log = outer;
                outer?.AddRange(log);
            }
        }
    }

    private StepResult Run(in Statement st, in ScriptSegment seg, IHost host, List<VarAccess>? log)
    {

        ReadOnlySpan<byte> s = image.Bytes.Span[seg.Start..seg.End];
        int namePos = seg.NameStart - seg.Start;
        uint name = st.Name.Value;
        FlowAction action;
        StepResult result;
        byte[]? label = null;
        if (st.Name.First == (byte)'Z')
        {
            action = FlowAction.Marker;
            result = Marker(st, seg, host);
        }
        else if (name == WKST || name == WKAD || name == WKOR || name == WKOF || name == WKBO)
        {
            VarCommands.Execute(s, namePos, Vars);
            action = FlowAction.Variables;
            result = StepResult.Continue;
        }
        else if (name == IF__)
        {
            label = VarCommands.Execute(s, namePos, Vars).Goto;
            (action, result) = label is null ? (FlowAction.NoJump, StepResult.Continue) : (FlowAction.Jump, StepResult.YieldAt(Lookup(label)));
        }
        else if (name == GOTO)
        {
            if (Vars.SkipCounter != 0)
            {
                (action, result) = (FlowAction.NoJump, StepResult.Continue);
            }
            else
            {
                VarCommands.CopyArg(s, namePos + 5, 0, Vars);
                label = Vars.ArgText.ToArray();
                (action, result) = (FlowAction.Jump, StepResult.YieldAt(Lookup(label)));
            }
        }
        else if (name == GOTS)
        {
            VarEffect e = VarCommands.Execute(s, namePos, Vars);
            label = e.Goto;
            (action, result) = e.Yielded ? (FlowAction.NoJump, StepResult.Yield)
                : label is null ? (FlowAction.NoJump, StepResult.Continue)
                : (FlowAction.Jump, StepResult.ContinueAt(Lookup(label)));
        }
        else if (name == IFNX)
        {
            if (InlineReader.IsPresent(s, namePos + 5, 1))
            {
                VarCommands.CopyArg(s, namePos + 5, 1, Vars);
                label = Vars.ArgText.ToArray();
                (action, result) = (FlowAction.Jump, StepResult.ContinueAt(Lookup(label)));
            }
            else
            {
                (action, result) = (FlowAction.NoJump, StepResult.Continue);
            }
        }
        else if (name == SELD || name == SELA || name == SELS || name == SELR)
        {
            VarEffect e = VarCommands.Execute(s, namePos, Vars);
            StepResult h = host.Dispatch(st);
            action = FlowAction.Variables;
            result = e.Yielded ? new StepResult(h.Kind == StepKind.Halt ? StepKind.Halt : StepKind.Yield, h.Target) : h;
        }
        else
        {
            action = FlowAction.Host;
            result = host.Dispatch(st);
        }

        if (Observer is { } observer)
        {
            var writes = new List<(long, ushort)>();
            foreach (VarAccess a in log!)
            {
                if (a.Kind == VarAccessKind.Write)
                {
                    writes.Add((a.Va, a.Value));
                }
            }

            int index = image.SegmentStarts.AsSpan().BinarySearch(seg.Start);
            observer(new FlowStep(image.Index, seg.Start, index < 0 ? -1 : index, st.Name, action, writes, label, result.Target, result.Kind));
        }

        return result;
    }

    /// <summary>
    /// Marker processing (VA 0x96615..0x96750): read tracking with the decoded ids, then <c>ZM</c> goes to the host (it
    /// displays the message and yields), <c>ZY</c> copies its label (from name + 8) to the argument buffer and sets the
    /// local-label flag, <c>ZZ</c> / <c>ZD</c> do nothing more.
    /// </summary>
    private StepResult Marker(in Statement st, in ScriptSegment seg, IHost host)
    {
        ReadOnlySpan<byte> script = image.Bytes.Span;
        if (ScriptSegmenter.TryReadMarkerId(script, seg, out int block, out int sub))
        {
            Markers.Enter(block, sub);
        }

        byte kind = (byte)(st.Name.Value >> 16);
        if (kind == (byte)'M')
        {
            return host.Dispatch(st);
        }

        if (kind == (byte)'Y')
        {
            ReadOnlySpan<byte> s = script[seg.Start..seg.End];
            int p = seg.NameStart - seg.Start + 8;
            int k = 0;
            byte[] ab = Vars.ArgBuffer;
            while (p + k < s.Length && s[p + k] is not ((byte)')' or (byte)',' or (byte)';') && k < ab.Length - 1)
            {
                ab[k] = s[p + k];
                k++;
            }

            ab[k] = 0;
            LocalLabelFlag = 1;
        }

        return StepResult.Continue;
    }

    private ScriptPosition Lookup(byte[] label)
    {
        int offset = labels.Find(label);
        if (offset < 0)
        {
            throw new ScriptFlowException(
                $"script {image.Index}: label '{System.Text.Encoding.Latin1.GetString(label)}' is not in its label table");
        }

        return new ScriptPosition(image.Index, offset);
    }
}
