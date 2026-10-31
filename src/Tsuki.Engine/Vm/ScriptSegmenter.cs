using System.Collections.Immutable;
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Vm;

/// <summary>
/// One segment of a decoded script: the bytes the PS4 interpreter scans per statement
/// (docs/formats/script.md 3.1, engine.md 5.2; port of <c>tsuki.script.parse_script</c>).
/// </summary>
/// <param name="Start">Offset of the first byte of the segment (just after the previous <c>;</c>).</param>
/// <param name="End">Offset just after the terminating <c>;</c>, or after 0x300 bytes, or the script end.</param>
/// <param name="Underscore">
/// Offset of the first <c>_</c> of the segment when the four name bytes after it lie inside the segment, else -1
/// (the segment has no name; <c>tsuki.script</c> then reports an empty key).
/// </param>
/// <param name="IsCommand">
/// True when the first name byte is <c>A</c>..<c>Z</c>: the engine dispatches on the name. Every other segment is
/// skipped (docs/formats/script.md 3.1).
/// </param>
/// <param name="Truncated">
/// True when the segment is not <c>;</c>-terminated although the script goes on (the engine cuts it at 0x300 bytes;
/// <c>tsuki.script.parse_script</c> raises <c>ScriptError</c> here). Never true in the retail data.
/// </param>
public readonly record struct ScriptSegment(int Start, int End, int Underscore, bool IsCommand, bool Truncated)
{
    /// <summary>Number of bytes of the segment.</summary>
    public int Length => End - Start;

    /// <summary>True when the segment has four name bytes (<see cref="Underscore"/> is set).</summary>
    public bool HasName => Underscore >= 0;

    /// <summary>Offset of the first name byte (just after the <c>_</c>); only meaningful when <see cref="HasName"/>.</summary>
    public int NameStart => Underscore + 1;

    /// <summary>
    /// Offset of the byte after the four name bytes (the separator, normally <c>(</c>): where
    /// <see cref="Statement.Args"/> starts. Only meaningful when <see cref="HasName"/>.
    /// </summary>
    public int ArgsStart => Underscore + 5;
}

/// <summary>
/// The tokenizer of the main interpreter (ScriptStep, eboot text VA 0x95E60; docs/formats/script.md 3.1): cuts a
/// decoded script into segments and finds each segment's command name. Byte-exact port of
/// <c>tsuki.script.parse_script</c>; the result equals it on all 549 retail scripts (1,083,168 segments).
/// </summary>
/// <remarks>
/// A segment is everything up to and including the next <c>;</c> within 0x300 bytes. The name is the four bytes after
/// the first <c>_</c> of the segment. Where the engine and the Python reference differ on malformed input
/// (a <c>;</c> inside the four name bytes, a segment longer than 0x300 bytes) this port follows the reference
/// and reports the case instead of guessing; neither occurs in the retail data.
/// </remarks>
public static class ScriptSegmenter
{
    /// <summary>At most this many bytes per segment (VA 0x95ECA).</summary>
    public const int MaxSegmentBytes = Statement.MaxSegmentBytes;

    private const byte Semicolon = (byte)';';
    private const byte Underscore = (byte)'_';

    /// <summary>Reads the segment that starts at <paramref name="start"/> (which must lie inside the script).</summary>
    public static ScriptSegment Read(ReadOnlySpan<byte> script, int start)
    {
        if ((uint)start >= (uint)script.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "the segment start must lie inside the script");
        }

        int n = script.Length;
        int limit = n - start > MaxSegmentBytes ? start + MaxSegmentBytes : n;
        int semi = script[start..limit].IndexOf(Semicolon);
        int end;
        bool truncated = false;
        if (semi >= 0)
        {
            end = start + semi + 1;
        }
        else
        {
            end = limit;
            truncated = limit < n;
        }

        int us = script[start..end].IndexOf(Underscore);
        if (us < 0 || start + us + 5 > end)
        {
            return new ScriptSegment(start, end, -1, false, truncated);
        }

        us += start;
        byte first = script[us + 1];
        return new ScriptSegment(start, end, us, first is >= (byte)'A' and <= (byte)'Z', truncated);
    }

    /// <summary>The start offset of every segment, ascending (the first one is 0); empty for an empty script.</summary>
    public static ImmutableArray<int> SegmentStarts(ReadOnlySpan<byte> script)
    {
        var builder = ImmutableArray.CreateBuilder<int>(Math.Max(4, script.Length / 18));
        for (int pos = 0; pos < script.Length;)
        {
            builder.Add(pos);
            pos = Read(script, pos).End;
        }

        return builder.ToImmutable();
    }

    /// <summary>The number of segments of the script.</summary>
    public static int CountSegments(ReadOnlySpan<byte> script)
    {
        int count = 0;
        for (int pos = 0; pos < script.Length; count++)
        {
            pos = Read(script, pos).End;
        }

        return count;
    }

    /// <summary>Builds the <see cref="ScriptImage"/> that <see cref="IGameData.Script"/> returns (decoded bytes plus segment starts).</summary>
    public static ScriptImage CreateImage(int index, ReadOnlyMemory<byte> script) =>
        new(index, script, SegmentStarts(script.Span));

    /// <summary>
    /// Turns a command segment into the <see cref="Statement"/> the host dispatches. False for a skipped segment
    /// (no name, or a first name byte outside <c>A</c>..<c>Z</c>).
    /// </summary>
    public static bool TryGetStatement(int scriptIndex, ReadOnlyMemory<byte> script, in ScriptSegment segment, out Statement statement)
    {
        if (!segment.IsCommand)
        {
            statement = default;
            return false;
        }

        ReadOnlySpan<byte> bytes = script.Span;
        var name = CommandName.FromBytes(bytes.Slice(segment.NameStart, 4));
        statement = new Statement(scriptIndex, segment.Start, segment.End, name, script[segment.ArgsStart..segment.End]);
        return true;
    }

    /// <summary>
    /// Decodes the block and sub id of a <c>_Z...</c> marker the way the engine does (VA 0x9665A..0x966E3;
    /// <see cref="MarkerId.Hex"/>): the five bytes after the first two name bytes, read from the script itself, so
    /// they may extend past the segment end. False when the name does not start with <c>Z</c> or fewer than five
    /// bytes remain.
    /// </summary>
    public static bool TryReadMarkerId(ReadOnlySpan<byte> script, in ScriptSegment segment, out int block, out int sub)
    {
        block = sub = 0;
        if (!segment.IsCommand || script[segment.NameStart] != (byte)'Z')
        {
            return false;
        }

        int at = segment.Underscore + 3;
        if (script.Length - at < 5)
        {
            return false;
        }

        block = MarkerId.Hex(script.Slice(at, 3));
        sub = MarkerId.Hex(script.Slice(at + 3, 2));
        return true;
    }
}

/// <summary>Marker ids <c>_Z&lt;k&gt;&lt;bbb&gt;&lt;ss&gt;</c> (docs/formats/script.md 3.7).</summary>
public static class MarkerId
{
    /// <summary>
    /// Marker id digits as the Z handler decodes them (VA 0x9665A..0x966E3): <c>d = c - 0x30</c> if <c>c &lt; 'a'</c> else
    /// <c>c - 0x57</c>, accumulated as <c>v = v * 16 + d</c> and truncated to a signed 16-bit value. Only lower-case hex
    /// is decoded correctly: <c>"00A"</c> gives 0x11, not 10. Port of <c>tsuki.script.marker_hex</c>.
    /// </summary>
    public static int Hex(ReadOnlySpan<byte> chars)
    {
        int v = 0;
        foreach (byte c in chars)
        {
            v = unchecked((v * 16) + (c < 0x61 ? c - 0x30 : c - 0x57));
        }

        return unchecked((short)v);
    }
}
