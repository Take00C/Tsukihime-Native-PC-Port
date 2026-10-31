using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Vm;

namespace Tsuki.Engine.Stage;

/// <summary>
/// The arguments of one layer statement as the object parser gives them to the handlers: <c>has(i)</c> (VA 0xD8C0), <c>expr(i)</c>
/// (0xDA50) and the raw text of an argument. An argument whose expression has no defined engine value reads as 0, like the oracle's
/// <c>_Args.expr</c>. Variable operands (<c>C&lt;n&gt;</c>, <c>F&lt;n&gt;</c>) are the caller's concern: the VM expands them before a statement
/// reaches the stage.
/// </summary>
public readonly struct StageArgs
{
    private readonly ReadOnlyMemory<byte> _buf;
    private readonly ArgRange[] _ranges;
    private readonly int _count;

    /// <summary>Splits the arguments of <paramref name="statement"/> with the object parser.</summary>
    public StageArgs(in Statement statement)
    {
        _buf = statement.Args;
        _ranges = new ArgRange[ObjectParser.MaxArguments];
        if (ObjectParser.Split(statement, _ranges, out _count) != SplitStatus.Ok)
        {
            _count = 0;
        }

        Statement = statement;
    }

    /// <summary>The statement.</summary>
    public Statement Statement { get; }

    /// <summary>The number of arguments the parser found.</summary>
    public int Count => _count;

    /// <summary><c>has(i)</c>: the argument exists and is not empty.</summary>
    public bool Has(int i) => ObjectParser.Has(_ranges, _count, i);

    /// <summary><c>expr(i)</c>: the 32-bit value of the argument expression, 0 for an absent argument or an undefined expression.</summary>
    public int Expr(int i)
    {
        if (!Has(i))
        {
            return 0;
        }

        return ObjectExpression.TryEvaluate(_ranges[i].In(_buf.Span), out int v, out _) ? v : 0;
    }

    /// <summary>The argument text (ISO-8859-1), "" when absent.</summary>
    public string Str(int i) => i < _count ? Encoding.Latin1.GetString(_ranges[i].In(_buf.Span)) : string.Empty;

    /// <summary>An argument as the inline readers see it: present test of the statement's textual argument list.</summary>
    public bool InlinePresent(int i) => InlineReader.IsPresent(Statement, i);

    /// <summary>The 16-bit inline value of argument <paramref name="i"/>.</summary>
    public int Inline16(int i) => InlineReader.ReadInt16(Statement, i);
}
