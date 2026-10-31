namespace Tsuki.Engine.Vm.Variables;

/// <summary>Choice modes (dword VA 0x17506B0; docs/formats/vm_variables.md 4.5).</summary>
public static class ChoiceMode
{
    /// <summary>No choice set up yet (the eboot image value).</summary>
    public const uint None = 0;

    /// <summary><c>_SELD</c>: list of <c>_SELR</c> lines.</summary>
    public const uint List = 1;

    /// <summary><c>_SELS</c>: two buttons.</summary>
    public const uint TwoButtons = 2;

    /// <summary><c>_SELA</c>: the "save your progress?" prompt.</summary>
    public const uint SavePrompt = 3;
}

/// <summary>
/// The engine state the variable commands touch (port of <c>tsuki.vm_vars.VarState</c>): the variable memory, the
/// argument scratch buffer, the if-block skip counter, scan mode, the snapshot block byte and the choice globals.
/// </summary>
public sealed class VarState
{
    /// <summary>Size of the argument scratch buffer (VA 0x16543C0).</summary>
    public const int ArgBufferSize = 0x40;

    /// <summary>Creates a state over <paramref name="memory"/>.</summary>
    public VarState(VarMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        Memory = memory;
    }

    /// <summary>Creates a state over a new zeroed block (out-of-block addresses rejected).</summary>
    public VarState()
        : this(new VarMemory())
    {
    }

    /// <summary>The variable memory.</summary>
    public VarMemory Memory { get; }

    /// <summary>Scratch copy of the argument being decoded (VA 0x16543C0, 0x40 bytes); also the label text of a jump.</summary>
    public byte[] ArgBuffer { get; } = new byte[ArgBufferSize];

    /// <summary>If-block skip counter (VA 0x165420E; <c>IFF_</c>/<c>ELS_</c>/<c>EIF_</c>, always 0 in retail).</summary>
    public ushort SkipCounter { get; set; }

    /// <summary>Save-restore scan mode (byte VA 0x174DE48).</summary>
    public bool ScanMode { get; set; }

    /// <summary>"Message continues" byte (VA 0x16541F4); non-zero blocks History_Snapshot.</summary>
    public byte SnapshotBlock { get; set; }

    /// <summary>Array of the pending choice target (u8 VA 0x1750648): <c>'F'</c> 0x46, <c>'G'</c> 0x47, or 0.</summary>
    public byte ChoiceKind { get; set; }

    /// <summary>Index of the pending choice target (u16 VA 0x175064A).</summary>
    public ushort ChoiceIndex { get; set; }

    /// <summary><c>_SELD</c> argument 1 (choice id), -1 when absent (i16 VA 0x1750698).</summary>
    public short ChoiceId { get; set; } = -1;

    /// <summary><c>_SELR</c> lines registered for the next <c>_SELD</c> (u16 VA 0x1750646).</summary>
    public int ChoiceCount { get; set; }

    /// <summary>Per-line result values, <c>_SELR</c> argument 0 (u16 table VA 0x1750630).</summary>
    public List<ushort> ChoiceValues { get; } = [];

    /// <summary>Per-line "locked" flags, <c>_SELR</c> argument 2 == 1 (byte table VA 0x175064C).</summary>
    public List<bool> ChoiceLocked { get; } = [];

    /// <summary>Choice mode (u32 VA 0x17506B0), see <see cref="Variables.ChoiceMode"/>.</summary>
    public uint ChoiceMode { get; set; }

    /// <summary>The argument buffer up to its first NUL (the whole buffer when it has none): the text a jump looks up.</summary>
    public ReadOnlySpan<byte> ArgText
    {
        get
        {
            int n = Array.IndexOf(ArgBuffer, (byte)0);
            return ArgBuffer.AsSpan(0, n < 0 ? ArgBufferSize : n);
        }
    }
}

/// <summary>
/// What a variable command did (port of <c>tsuki.vm_vars.Effect</c>).
/// </summary>
/// <param name="Handled">The interpreter has a case for the name.</param>
/// <param name="Goto">The label text handed to the label lookup (VA 0x794D0), or null for no jump; may be empty (<c>_GOTS</c> past its last label).</param>
/// <param name="Yielded">ScriptStep returns to its caller (VA 0x9B516).</param>
/// <param name="Snapshot">History_Snapshot passed its gate (body VA 0x7AFDD).</param>
/// <param name="ChoiceUi">The choice UI was set up (VA 0xA5DF0).</param>
/// <param name="ChoicePoint"><c>_SELD</c>: s16 argument 1 passed to VA 0xB8620 (flowchart marks), else null.</param>
public readonly record struct VarEffect(bool Handled, byte[]? Goto = null, bool Yielded = false, bool Snapshot = false,
    bool ChoiceUi = false, int? ChoicePoint = null)
{
    /// <summary>No variable semantics (the name is not a variable command).</summary>
    public static VarEffect NotHandled => default;

    /// <summary>Handled, nothing else.</summary>
    public static VarEffect Done => new(true);

    /// <summary>True when the command asks for a label lookup.</summary>
    public bool HasGoto => Goto is not null;
}
