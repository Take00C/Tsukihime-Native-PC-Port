using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Vm;

namespace Tsuki.Data.Census;

/// <summary>
/// One statement with its argument lists cut both ways, as <c>tsuki.script.Command</c> offers them: the textual arguments
/// (<c>Command.args</c>, <see cref="ArgumentList"/>), the object-parser arguments (<c>obj_args</c>, <see cref="ObjectParser"/>) and the
/// integer readers (<c>int_arg</c>, <c>expr_arg</c>, <c>value</c>). The cuts are made on first use.
/// </summary>
internal sealed class StatementView
{
    private ArgRange[]? _text;
    private ArgRange[]? _obj;
    private int _objCount;
    private SplitStatus _objStatus;

    public StatementView(Statement statement)
    {
        Statement = statement;
        string key = statement.Name.ToString();
        Key = key;

        // tsuki.script.parse_script: a marker _ZMbbbss is called ZM, ZZ, ZY ...; every other command is its four name bytes
        Name = key[0] == 'Z' ? "Z" + key[1] : key;
    }

    public Statement Statement { get; }

    /// <summary>The four name bytes.</summary>
    public string Key { get; }

    /// <summary>The display name (<c>Command.name</c>): <c>ZM</c> for every <c>_ZM...</c> marker, else the four bytes.</summary>
    public string Name { get; }

    public int Offset => Statement.Offset;

    /// <summary>Number of textual arguments.</summary>
    public int TextCount => TextRanges.Length;

    /// <summary>The textual argument <paramref name="i"/> as the bytes after its keyword tag (<c>Arg.value</c>).</summary>
    public ReadOnlySpan<byte> TextValue(int i) => ArgumentList.Untagged(TextRanges[i].In(Statement.Args.Span));

    /// <summary>The raw textual argument, tag included (<c>Arg.raw</c>).</summary>
    public ReadOnlySpan<byte> TextRaw(int i) => TextRanges[i].In(Statement.Args.Span);

    /// <summary>The textual argument <paramref name="i"/> (<c>Arg.value</c>) as Latin-1 text, one character per byte.</summary>
    public string TextString(int i) => Encoding.Latin1.GetString(TextValue(i));

    /// <summary>The 16-bit inline value of argument <paramref name="i"/> (<c>int_arg(i)</c>).</summary>
    public int Inline16(int i) => StatementArguments.Int16(Statement, i);

    /// <summary>The object-parser value of argument <paramref name="i"/> (<c>expr_arg</c>); null when it is absent, empty or has no defined value.</summary>
    public int? Expr(int i)
    {
        EnsureObj();
        if (_objStatus != SplitStatus.Ok || !ObjectParser.Has(_obj, _objCount, i))
        {
            return null;
        }

        return ObjectExpression.TryEvaluate(_obj![i].In(Statement.Args.Span), out int v, out _) ? v : null;
    }

    /// <summary>Whether argument <paramref name="i"/> of the object parser exists and is not empty.</summary>
    public bool ObjHas(int i)
    {
        EnsureObj();
        return _objStatus == SplitStatus.Ok && ObjectParser.Has(_obj, _objCount, i);
    }

    /// <summary>Object-parser argument <paramref name="i"/> (<c>obj_args[i]</c>); empty when it does not exist.</summary>
    public ReadOnlySpan<byte> ObjArg(int i)
    {
        EnsureObj();
        return _objStatus == SplitStatus.Ok && i < _objCount ? _obj![i].In(Statement.Args.Span) : default;
    }

    /// <summary>The value of argument <paramref name="i"/> as its handler reads it (<c>Command.value</c>).</summary>
    public ArgValue Value(int i) => StatementArguments.Value(Statement, i);

    private ArgRange[] TextRanges
    {
        get
        {
            if (_text is null)
            {
                var list = new List<ArgRange>(8);
                ArgumentList.Split(Statement.Args.Span, ArgumentList.StartIn(Statement), list);
                _text = [.. list];
            }

            return _text;
        }
    }

    private void EnsureObj()
    {
        if (_obj is not null)
        {
            return;
        }

        var ranges = new ArgRange[ObjectParser.MaxArguments];
        _objStatus = ObjectParser.Split(Statement, ranges, out _objCount);
        _obj = ranges;
    }
}
