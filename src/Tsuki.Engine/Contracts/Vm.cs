namespace Tsuki.Engine.Contracts;

/// <summary>A script position: script number k and a byte offset into its <see cref="ScriptImage.Bytes"/>.</summary>
/// <param name="Script">Script number k (allscr.mrg section k + 3, docs/formats/script.md 2.1).</param>
/// <param name="Offset">Byte offset in the decoded script; equal to the PS4 offset (ARCHITECTURE 5.1).</param>
public readonly record struct ScriptPosition(int Script, int Offset)
{
    /// <summary>No position (both fields -1).</summary>
    public static ScriptPosition None => new(-1, -1);

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Script < 0;
}

/// <summary>
/// The 4 name bytes after the first <c>_</c> of a segment, packed big-endian: the first letter
/// selects the dispatch table entry, the other three form the 24-bit key
/// (docs/formats/script.md 3.1, 3.6; engine.md 5.2).
/// </summary>
/// <param name="Value">The packed name: <c>name[0] &lt;&lt; 24 | name[1] &lt;&lt; 16 | name[2] &lt;&lt; 8 | name[3]</c>.</param>
public readonly record struct CommandName(uint Value)
{
    /// <summary>The first name byte (dispatch letter, <c>A</c>..<c>Z</c> for a command).</summary>
    public byte First => (byte)(Value >> 24);

    /// <summary>The 24-bit key of the other three bytes (engine.md 5.2).</summary>
    public uint Key => Value & 0x00FF_FFFF;

    /// <summary>Packs the first four bytes of <paramref name="name"/>.</summary>
    public static CommandName FromBytes(ReadOnlySpan<byte> name)
    {
        if (name.Length < 4)
        {
            throw new ArgumentException("a command name has 4 bytes", nameof(name));
        }

        return new CommandName((uint)name[0] << 24 | (uint)name[1] << 16 | (uint)name[2] << 8 | name[3]);
    }

    /// <summary>Packs a 4-character ASCII name such as <c>"WKST"</c> (for tables and tests).</summary>
    public static CommandName FromAscii(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length != 4)
        {
            throw new ArgumentException("a command name has 4 characters", nameof(name));
        }

        Span<byte> b = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (name[i] > 0x7F)
            {
                throw new ArgumentException("a command name is ASCII", nameof(name));
            }

            b[i] = (byte)name[i];
        }

        return FromBytes(b);
    }

    /// <summary>The name as 4 characters; bytes outside printable ASCII show as <c>?</c>.</summary>
    public override string ToString()
    {
        Span<char> c = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            int b = (int)(Value >> (24 - (8 * i))) & 0xFF;
            c[i] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }

        return new string(c);
    }
}

/// <summary>
/// One decoded statement: a segment whose name passed the tokenizer (docs/formats/script.md 3.1;
/// engine.md 5.2; ARCHITECTURE 3.2, 5.2).
/// </summary>
public readonly struct Statement
{
    /// <summary>At most this many bytes per segment (0x300, script.md 3.1).</summary>
    public const int MaxSegmentBytes = 0x300;

    /// <summary>At most this many segments run per ScriptStep call (1000, script.md 3.1; ARCHITECTURE 5.2).</summary>
    public const int SegmentsPerStep = 1000;

    /// <summary>Creates a statement.</summary>
    /// <param name="script">Script number k.</param>
    /// <param name="offset">Offset of the segment start.</param>
    /// <param name="end">Offset just after the segment (after its <c>;</c>), where the next segment starts.</param>
    /// <param name="name">The 4 name bytes.</param>
    /// <param name="args">The bytes from name + 4 up to <paramref name="end"/>; handlers parse <c>(</c> themselves (engine.md 5.2).</param>
    public Statement(int script, int offset, int end, CommandName name, ReadOnlyMemory<byte> args)
    {
        Script = script;
        Offset = offset;
        End = end;
        Name = name;
        Args = args;
    }

    /// <summary>Script number k.</summary>
    public int Script { get; }

    /// <summary>Offset of the segment start (the PS4 offset; used by history records and traces).</summary>
    public int Offset { get; }

    /// <summary>Offset just after the segment: where execution resumes after this statement.</summary>
    public int End { get; }

    /// <summary>The 4 name bytes after the first <c>_</c>.</summary>
    public CommandName Name { get; }

    /// <summary>
    /// The raw argument bytes, from name + 4 up to the segment end, read by the inline reader or the
    /// object parser (script.md 3.2-3.4).
    /// </summary>
    public ReadOnlyMemory<byte> Args { get; }

    /// <summary>Position of the segment start.</summary>
    public ScriptPosition Position => new(Script, Offset);

    /// <summary>Position just after the segment.</summary>
    public ScriptPosition Next => new(Script, End);
}

/// <summary>What the VM does after <see cref="IHost.Dispatch"/> (docs/formats/script.md 3.6 "Frame behaviour").</summary>
public enum StepKind : byte
{
    /// <summary>Run the next segment in the same ScriptStep call.</summary>
    Continue = 0,

    /// <summary>End this ScriptStep call; the next call resumes after the statement (or at the target).</summary>
    Yield = 1,

    /// <summary>Stop the VM (end of the script flow, or a fatal error in dev builds).</summary>
    Halt = 2,
}

/// <summary>
/// The result of one statement (ARCHITECTURE 3.2). Resuming always continues after the statement
/// or at <see cref="Target"/>, so no command runs twice; a handler that waits (WTTM, FADS, messages)
/// yields and keeps its wait state in the host (Mahoyo port design, ARCHITECTURE 3.2).
/// </summary>
/// <param name="Kind">Continue, yield or halt.</param>
/// <param name="Target">Where execution goes next; <see cref="ScriptPosition.None"/> means just after the statement. Jumps, scene changes and returns set it (script.md 4.1).</param>
public readonly record struct StepResult(StepKind Kind, ScriptPosition Target)
{
    /// <summary>Continue with the next statement.</summary>
    public static StepResult Continue => new(StepKind.Continue, ScriptPosition.None);

    /// <summary>Yield; resume after the statement on a later step.</summary>
    public static StepResult Yield => new(StepKind.Yield, ScriptPosition.None);

    /// <summary>Stop the VM.</summary>
    public static StepResult Halt => new(StepKind.Halt, ScriptPosition.None);

    /// <summary>True when <see cref="Target"/> is set.</summary>
    public bool HasTarget => !Target.IsNone;

    /// <summary>Continue at <paramref name="target"/> in the same ScriptStep call.</summary>
    public static StepResult ContinueAt(ScriptPosition target) => new(StepKind.Continue, target);

    /// <summary>Yield and resume at <paramref name="target"/>.</summary>
    public static StepResult YieldAt(ScriptPosition target) => new(StepKind.Yield, target);
}

/// <summary>
/// The command side of the VM (ARCHITECTURE 3.2, 5.3-5.4). The VM decodes a
/// <see cref="Statement"/> and calls <see cref="Dispatch"/>; the host's yield set is the PS4's
/// (docs/formats/script.md 3.6; ARCHITECTURE 5.3).
/// </summary>
public interface IHost
{
    /// <summary>Runs one statement and says how the VM goes on.</summary>
    StepResult Dispatch(in Statement statement);
}
