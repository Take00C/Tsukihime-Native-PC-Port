using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Vm;

/// <summary>What <see cref="StatementArguments.Value"/> found.</summary>
public enum ArgValueKind : byte
{
    /// <summary>An integer value.</summary>
    Value = 0,

    /// <summary>The argument is missing or empty for an object-parser command: the handler uses its own default (<c>Command.value</c> returns None).</summary>
    Absent = 1,

    /// <summary>The expression has no defined engine value (<see cref="ArgValue.Error"/> says why).</summary>
    Undefined = 2,

    /// <summary>The argument list cannot be split (<see cref="SplitStatus"/> is not Ok).</summary>
    SplitError = 3,
}

/// <summary>An integer argument as its command's handler reads it.</summary>
/// <param name="Kind">What was found.</param>
/// <param name="Value">The integer when <paramref name="Kind"/> is <see cref="ArgValueKind.Value"/>, else 0.</param>
/// <param name="Error">The reason when <paramref name="Kind"/> is <see cref="ArgValueKind.Undefined"/>.</param>
public readonly record struct ArgValue(ArgValueKind Kind, int Value, ExpressionError Error = ExpressionError.None)
{
    /// <summary>True when an integer was read.</summary>
    public bool HasValue => Kind == ArgValueKind.Value;

    /// <summary>The integer, or <paramref name="fallback"/> when the argument is absent or has no defined value.</summary>
    public int Or(int fallback) => HasValue ? Value : fallback;
}

/// <summary>
/// Argument access for a <see cref="Statement"/> (docs/formats/script.md 3.2): which reader the command uses and the
/// position conventions of <see cref="Statement.Args"/>, whose first byte is the separator after the four name bytes.
/// Port of <c>tsuki.script.Command.int_arg</c>, <c>present</c>, <c>obj_args</c>, <c>expr_arg</c> and <c>value</c>.
/// </summary>
public static class StatementArguments
{
    /// <summary>
    /// The integer argument <paramref name="index"/> read the way the command's PS4 handler reads it: object-parser commands
    /// (<see cref="ObjectCommands"/>) go through <see cref="ObjectExpression"/> (TOMP through the partial strtol getter);
    /// every other command through the 16-bit <see cref="InlineReader"/>.
    /// </summary>
    public static ArgValue Value(in Statement statement, int index)
    {
        if (!ObjectCommands.Contains(statement.Name))
        {
            return new ArgValue(ArgValueKind.Value, InlineReader.ReadInt16(statement, index));
        }

        Span<ArgRange> ranges = stackalloc ArgRange[ObjectParser.MaxArguments];
        if (ObjectParser.Split(statement, ranges, out int count) != SplitStatus.Ok)
        {
            return new ArgValue(ArgValueKind.SplitError, 0);
        }

        if (!ObjectParser.Has(ranges, count, index))
        {
            return new ArgValue(ArgValueKind.Absent, 0);
        }

        ReadOnlySpan<byte> arg = ranges[index].In(statement.Args.Span);
        if (statement.Name == ObjectCommands.Tomp)
        {
            return ObjectParser.PartialInt(arg) is { } partial
                ? new ArgValue(ArgValueKind.Value, partial)
                : new ArgValue(ArgValueKind.Absent, 0);
        }

        return ObjectExpression.TryEvaluate(arg, out int v, out ExpressionError error)
            ? new ArgValue(ArgValueKind.Value, v)
            : new ArgValue(ArgValueKind.Undefined, 0, error);
    }

    /// <summary>
    /// The object-parser value of argument <paramref name="index"/> regardless of the command (<c>expr_arg</c>): absent when the
    /// argument is missing or empty.
    /// </summary>
    public static ArgValue ObjectValue(in Statement statement, int index)
    {
        Span<ArgRange> ranges = stackalloc ArgRange[ObjectParser.MaxArguments];
        if (ObjectParser.Split(statement, ranges, out int count) != SplitStatus.Ok)
        {
            return new ArgValue(ArgValueKind.SplitError, 0);
        }

        if (!ObjectParser.Has(ranges, count, index))
        {
            return new ArgValue(ArgValueKind.Absent, 0);
        }

        return ObjectExpression.TryEvaluate(ranges[index].In(statement.Args.Span), out int v, out ExpressionError error)
            ? new ArgValue(ArgValueKind.Value, v)
            : new ArgValue(ArgValueKind.Undefined, 0, error);
    }

    /// <summary>The 32-bit inline value of argument <paramref name="index"/> (<c>int_arg(i, 32)</c>).</summary>
    public static int Int32(in Statement statement, int index) => InlineReader.ReadInt32(statement, index);

    /// <summary>The 16-bit inline value of argument <paramref name="index"/> (<c>int_arg(i)</c>).</summary>
    public static int Int16(in Statement statement, int index) => InlineReader.ReadInt16(statement, index);

    /// <summary>The inline "present" test (<c>present(i)</c>).</summary>
    public static bool Present(in Statement statement, int index) => InlineReader.IsPresent(statement, index);

    /// <summary>
    /// The textual arguments of the statement as byte ranges inside <see cref="Statement.Args"/> (<c>Command.args</c>), appended to
    /// <paramref name="ranges"/>; returns the index in <see cref="Statement.Args"/> of the terminating <c>)</c> or <c>;</c>.
    /// </summary>
    public static int SplitText(in Statement statement, List<ArgRange> ranges) =>
        ArgumentList.Split(statement.Args.Span, ArgumentList.StartIn(statement), ranges);
}
