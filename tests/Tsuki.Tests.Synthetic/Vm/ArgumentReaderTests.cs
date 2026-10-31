using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Vm;

namespace Tsuki.Tests.Synthetic.Vm;

/// <summary>
/// Both argument readers (docs/formats/script.md 3.2-3.3a): the examples of the format document and the cases of
/// <c>tools/tests/test_script.py</c> (<c>test_inline_reader</c>, <c>test_object_parser_and_expressions</c>), whose values were
/// checked against the engine's own code under emulation.
/// </summary>
public sealed class ArgumentReaderTests
{
    private static Statement Parse(string text)
    {
        byte[] script = Encoding.UTF8.GetBytes(text);
        ScriptSegment seg = ScriptSegmenter.Read(script, 0);
        Assert.True(ScriptSegmenter.TryGetStatement(0, script, seg, out Statement st), text);
        return st;
    }

    // ---- inline reader -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("258-18", 240)]
    [InlineData("154-19", 135)]
    [InlineData("-1", -1)]
    [InlineData("", 0)]
    [InlineData("`017:100", 100)]
    [InlineData("1+2-3+40", 40)]
    [InlineData("1-2-3", -4)] // left to right, unlike the object parser
    [InlineData("242.25", 24225)] // '.' is ignored by the inline reader
    [InlineData("0x000000", 0)]
    [InlineData("a5b6", 56)]
    [InlineData("12345678901234567890", 1234567890123456 & 0xFFFF)] // 16-byte cap, then the i16 wrap
    public void InlineValueOfASingleArgument(string text, long expected)
    {
        Assert.Equal((short)expected, InlineReader.ReadText(Encoding.ASCII.GetBytes(text), 16));
    }

    [Fact]
    public void InlineWidthsWrapLikeTheEngine()
    {
        Assert.Equal(40000 - 65536, InlineReader.ReadText("40000"u8, 16));
        Assert.Equal(40000, InlineReader.ReadText("40000"u8, 32));
        Assert.Equal(unchecked((int)1234567890123456), InlineReader.ReadText("12345678901234567890"u8, 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => InlineReader.ReadText("1"u8, 8));
    }

    [Fact]
    public void MissingIsMinusOneAndEmptyIsZero()
    {
        Statement atsv = Parse("_ATSV();");
        Assert.Equal((0, -1), (InlineReader.ReadInt16(atsv, 0), InlineReader.ReadInt16(atsv, 1))); // '()' index 0 is empty, not missing
        Assert.False(InlineReader.IsPresent(atsv, 0)); // ... and not "present": the first byte is the ')'
        Assert.False(InlineReader.IsPresent(atsv, 1));

        Statement stcc = Parse("_STCC(5);");
        Assert.Equal((5, -1), (InlineReader.ReadInt16(stcc, 0), InlineReader.ReadInt16(stcc, 1)));

        Statement aaaa = Parse("_AAAA(,5);");
        Assert.Equal((0, 5), (InlineReader.ReadInt16(aaaa, 0), InlineReader.ReadInt16(aaaa, 1)));
        Assert.Equal([false, true, false], [InlineReader.IsPresent(aaaa, 0), InlineReader.IsPresent(aaaa, 1), InlineReader.IsPresent(aaaa, 2)]);
    }

    [Fact]
    public void TheTagSkipIsBlind()
    {
        // '`' skips five bytes, even past a ',': "`a,12" is skipped and "345" is read
        Assert.Equal(345, InlineReader.ReadInt("_AAAA(`a,12345);"u8, 6, 0));
        Assert.Equal(4000, InlineReader.ReadInt("_SEPL(0,1,X,,`001:4000,,`017:50);"u8, 6, 4));
        Assert.Equal(50, InlineReader.ReadInt("_SEPL(0,1,X,,`001:4000,,`017:50);"u8, 6, 6));
        Assert.True(InlineReader.IsPresent("_SEPL(0,1,X,,`001:4000,,`017:50);"u8, 6, 4));
        Assert.False(InlineReader.IsPresent("_SEPL(0,1,X,,`001:4000,,`017:50);"u8, 6, 3));
    }

    [Fact]
    public void SkipAndReadLimits()
    {
        // an argument longer than 500 bytes: the skip loop gives up after 500 bytes and moves on without finding a ','
        byte[] longArg = [.. "_X("u8.ToArray(), .. Enumerable.Repeat((byte)'a', 510), (byte)',', (byte)'7', (byte)')'];
        Assert.Equal(0, InlineReader.ReadInt(longArg, 3, 1)); // lands 10 bytes before the ',': letters are ignored, then ','
        Assert.True(InlineReader.IsPresent(longArg, 3, 1)); // the first byte there is an 'a'
        Assert.Equal(7, InlineReader.ReadInt(longArg, 3, 2));

        // at most 16 bytes of the value are read
        Assert.Equal(unchecked((int)1234567890123456), InlineReader.ReadInt("12345678901234567890)"u8, 0, 0, 32));
        Assert.Equal(12, InlineReader.ReadInt("0000000000000012)"u8, 0, 0)); // exactly 16 bytes
        Assert.Equal(1, InlineReader.ReadInt("00000000000000012)"u8, 0, 0)); // the '2' is byte 17: not read
    }

    [Fact]
    public void InlineReadsPastTheEndAsZero()
    {
        Assert.Equal(0, InlineReader.ReadInt([], 0, 0));
        Assert.True(InlineReader.IsPresent([], 0, 0)); // a NUL is not one of ',' ')' ';'
        Assert.Equal(5, InlineReader.ReadInt("5"u8, 0, 0)); // no terminator needed
    }

    // ---- object parser -----------------------------------------------------------------------------------------------------

    private static string Split(string text, int sep)
    {
        var ranges = new ArgRange[ObjectParser.MaxArguments];
        SplitStatus st = ObjectParser.Split(Encoding.ASCII.GetBytes(text), sep, ranges, out int n);
        return st != SplitStatus.Ok ? st.ToString() : string.Join(" ", ranges.Take(n).Select(r => $"{r.Start}-{r.End}"));
    }

    [Fact]
    public void ObjectSplit()
    {
        Assert.Equal("6-7 9-12 14-15", Split("_STCH(1,\"a,b\",c);", 5)); // a quoted argument may hold a comma
        Assert.Equal(string.Empty, Split("_STCH();", 5)); // () has no arguments
        Assert.Equal(string.Empty, Split("_STCH(;", 5));
        Assert.Equal("6-6 7-7", Split("_STCH(,);", 5)); // two empty arguments
        Assert.Equal(20, Split("_STCH(" + string.Concat(Enumerable.Repeat("1,", 30)) + "1);", 5).Split(' ').Length);
        Assert.Equal("NoTerminator", Split("_STCH(1,2", 5));
        Assert.Equal("UnterminatedQuote", Split("_STCH(\"ab);", 5));
    }

    [Fact]
    public void ObjectSplitOfAStatementStartsAtTheSeparator()
    {
        Statement st = Parse("_STCH(18,img1728,240,156,1000,242.25,2,,CC,,,);");
        var ranges = new ArgRange[ObjectParser.MaxArguments];
        Assert.Equal(SplitStatus.Ok, ObjectParser.Split(st, ranges, out int count));
        Assert.Equal(12, count);
        Assert.Equal("img1728", Encoding.ASCII.GetString(ranges[1].In(st.Args.Span)));
        Assert.True(ObjectParser.Has(ranges, count, 1));
        Assert.False(ObjectParser.Has(ranges, count, 7)); // empty
        Assert.False(ObjectParser.Has(ranges, count, 12)); // missing
    }

    [Theory]
    [InlineData("258-18", 240)]
    [InlineData("242.25", 242)]
    [InlineData("2000.0", 2000)]
    [InlineData("0x000000", 0)]
    [InlineData("img1728", 0)]
    [InlineData("`017:100", 0)]
    [InlineData("10-3-2", 9)] // right-associative
    [InlineData("{10-3}-2", 5)]
    [InlineData("2*-3", -6)]
    [InlineData("1<<4|1", 17)]
    [InlineData("", 0)]
    [InlineData("-1", -1)]
    [InlineData("--5", 5)]
    [InlineData("5-", 5)]
    [InlineData("+5", 5)]
    [InlineData("1+2*3", 7)]
    [InlineData("-7/2", -3)]
    [InlineData("-7%3", -1)]
    [InlineData("!0", 1)]
    [InlineData("~0", -1)]
    [InlineData("5!", 5)]
    [InlineData("{1+2}*3", 9)]
    [InlineData("12345678901234567890", -1)] // strtol clamps to LONG_MAX, the low 32 bits are all ones
    [InlineData("1 2", 1)]
    [InlineData("3<5", 1)]
    [InlineData("1==1", 1)]
    [InlineData("2!=2", 0)]
    [InlineData("1&&0", 0)]
    [InlineData("0||3", 1)]
    [InlineData("2147483647+1", int.MinValue)] // 32-bit wrap
    [InlineData("1<<33", 2)] // the shift count uses its low 5 bits
    [InlineData("-8>>1", -4)] // arithmetic shift
    public void ExpressionValues(string text, int expected)
    {
        Assert.Equal(expected, ObjectExpression.Evaluate(Encoding.ASCII.GetBytes(text)));
    }

    [Theory]
    [InlineData("1/0", ExpressionError.DivisionFault)]
    [InlineData("1%0", ExpressionError.DivisionFault)]
    [InlineData("{1/0}", ExpressionError.DivisionFault)]
    [InlineData("{1x}", ExpressionError.InsideBraces)]
    [InlineData("*5", ExpressionError.BinaryWithoutOperands)]
    [InlineData("!=", ExpressionError.BinaryWithoutOperands)]
    [InlineData("-{}", ExpressionError.UnaryWithoutOperand)]
    public void UndefinedExpressions(string text, ExpressionError expected)
    {
        Assert.False(ObjectExpression.TryEvaluate(Encoding.ASCII.GetBytes(text), out _, out ExpressionError error));
        Assert.Equal(expected, error);
        Assert.Null(ObjectExpression.Evaluate(Encoding.ASCII.GetBytes(text)));
    }

    [Theory]
    [InlineData("{")] // an unterminated group is an empty group
    [InlineData("{}")]
    [InlineData("}")]
    [InlineData("*")] // an operator as the last byte stops the tokenizer
    [InlineData("=")]
    [InlineData("=5")]
    [InlineData("1.5.5x")]
    public void StoppedOrEmptyExpressionsAreZeroOrWhatWasEmitted(string text)
    {
        Assert.True(ObjectExpression.TryEvaluate(Encoding.ASCII.GetBytes(text), out int v, out _), text);
        Assert.Equal(text == "1.5.5x" ? 1 : 0, v);
    }

    [Fact]
    public void IntMinOverMinusOneIsUndefined()
    {
        // 2147483648 is strtol 2147483648, truncated to INT_MIN; "/-1" then faults (idiv)
        Assert.False(ObjectExpression.TryEvaluate("2147483648/-1"u8, out _, out ExpressionError e));
        Assert.Equal(ExpressionError.DivisionFault, e);
        Assert.Equal(int.MinValue, ObjectExpression.Evaluate("2147483648"u8));
    }

    [Fact]
    public void LongExpressionsUseTheHeap()
    {
        string text = string.Join("+", Enumerable.Repeat("1", 3000)); // 5999 bytes, beyond the stack buffer
        Assert.Equal(3000, ObjectExpression.Evaluate(Encoding.ASCII.GetBytes(text)));
        string deep = new string('{', 400) + "7" + new string('}', 400);
        Assert.Equal(7, ObjectExpression.Evaluate(Encoding.ASCII.GetBytes(deep)));
    }

    [Fact]
    public void StrtolGetters()
    {
        Assert.Equal(-12, ObjectParser.StrictInt("-12"u8));
        Assert.Equal(7, ObjectParser.StrictInt(" 7"u8));
        Assert.Equal(7, ObjectParser.StrictInt("+7"u8));
        Assert.Null(ObjectParser.StrictInt("242.25"u8));
        Assert.Null(ObjectParser.StrictInt("5 "u8));
        Assert.Null(ObjectParser.StrictInt(""u8));
        Assert.Null(ObjectParser.StrictInt("1234567890123456"u8)); // 16 bytes: longer than the 15-byte copy
        Assert.Equal(242, ObjectParser.PartialInt("242.25"u8)); // what TOMP stores even when the getter reports failure
        Assert.Equal(0, ObjectParser.PartialInt("img1"u8));
        Assert.Equal(0, ObjectParser.PartialInt("- 5"u8));
        Assert.Null(ObjectParser.PartialInt(""u8));
        Assert.Equal(unchecked((int)123456789012345), ObjectParser.StrictInt("123456789012345"u8)); // low 32 bits
    }

    // ---- which reader a command uses -----------------------------------------------------------------------------------------

    [Fact]
    public void ObjectCommandsAreFiftyTwoNames()
    {
        Assert.Equal(52, ObjectCommands.Names.Count);
        Assert.Equal(52, ObjectCommands.Names.Distinct().Count());
        Assert.All(ObjectCommands.Names, n => Assert.True(ObjectCommands.Contains(CommandName.FromAscii(n)), n));
        Assert.False(ObjectCommands.Contains(CommandName.FromAscii("WKST")));
        Assert.False(ObjectCommands.Contains(CommandName.FromAscii("SEPL")));
        Assert.Equal(ObjectCommands.Names.Order(StringComparer.Ordinal), ObjectCommands.Names);
    }

    [Fact]
    public void ValueUsesTheReaderOfTheCommand()
    {
        // STCH is an object-parser command: 242.25 is 242, an empty argument is absent
        Statement stch = Parse("_STCH(18,img1728,240,156,1000,242.25,2,,CC,,,);");
        Assert.Equal(new ArgValue(ArgValueKind.Value, 242), StatementArguments.Value(stch, 5));
        Assert.Equal(24225, StatementArguments.Int16(stch, 5)); // the inline reader would say 24225
        Assert.Equal(ArgValueKind.Absent, StatementArguments.Value(stch, 7).Kind);
        Assert.Equal(0, StatementArguments.Int16(stch, 7));
        Assert.Equal(ArgValueKind.Absent, StatementArguments.Value(stch, 40).Kind);
        Assert.Equal(7, StatementArguments.Value(stch, 40).Or(7));
        Assert.Equal(0, StatementArguments.Value(stch, 1).Value); // "img1728" evaluates to 0
        Assert.Equal(new ArgValue(ArgValueKind.Value, 240), StatementArguments.ObjectValue(stch, 2));

        // WKST is an inline command
        Statement wkst = Parse("_WKST(F823,eq,0);");
        Assert.Equal(new ArgValue(ArgValueKind.Value, 823), StatementArguments.Value(wkst, 0));
        Assert.Equal(new ArgValue(ArgValueKind.Value, -1), StatementArguments.Value(wkst, 3));

        // undefined expressions and unsplittable lists are reported, not thrown
        ArgValue div = StatementArguments.Value(Parse("_STCC(1/0);"), 0);
        Assert.Equal(ArgValueKind.Undefined, div.Kind);
        Assert.Equal(ExpressionError.DivisionFault, div.Error);
        Assert.Equal(ArgValueKind.SplitError, StatementArguments.Value(Parse("_STCC(\"1);"), 0).Kind);

        // TOMP reads the movie id with the partial strtol getter
        Assert.Equal(new ArgValue(ArgValueKind.Value, 4), StatementArguments.Value(Parse("_TOMP(4);"), 0));
        Assert.Equal(new ArgValue(ArgValueKind.Value, 4), StatementArguments.Value(Parse("_TOMP(4x);"), 0));
        Assert.Equal(new ArgValue(ArgValueKind.Value, 0), StatementArguments.Value(Parse("_TOMP(x);"), 0));
    }

    // ---- textual arguments, trailer, tags --------------------------------------------------------------------------------

    private static (string[] Args, string Trailer, string[] Tags) Text(string statement)
    {
        Statement st = Parse(statement);
        var ranges = new List<ArgRange>();
        int close = StatementArguments.SplitText(st, ranges);
        byte[] args = st.Args.ToArray();
        return (ranges.Select(r => Encoding.UTF8.GetString(r.In(args))).ToArray(),
            Encoding.UTF8.GetString(ArgumentList.Trailer(args, close)),
            ranges.Select(r => ArgumentList.Tag(r.In(args))?.ToString() ?? "-").ToArray());
    }

    [Fact]
    public void TextualArguments()
    {
        Assert.Equal(["F823", "eq", "0"], Text("_WKST(F823,eq,0);").Args);
        Assert.Empty(Text("_CLO3();").Args);
        Assert.Equal(["", ""], Text("_AAAA(,);").Args);
        Assert.Equal(["0", "26"], Text("_STTR,0,26);").Args); // the separator byte is never checked
        (string[] args, string trailer, _) = Text("_STZ4(5,498,498,498,498,0,0,0)gb);");
        Assert.Equal(8, args.Length);
        Assert.Equal("gb)", trailer);
        Assert.Equal("#)", Text("_STGS(0,,0,800,0)#);").Trailer);
        Assert.Equal(string.Empty, Text("_WKST(F1,eq,0);").Trailer);

        // markers: the arguments start after "_Zkbbbss("
        Assert.Equal(["START"], Text("_ZZ00b01(START);").Args);
        Assert.Empty(Text("_ZZ00a01();").Args);
        Assert.Equal(["$000107@k@e"], Text("_ZM01801($000107@k@e);").Args);
        Assert.Empty(Text("_ZZ0;").Args); // too short for arguments
    }

    [Fact]
    public void KeywordTags()
    {
        (string[] args, _, string[] tags) = Text("_SEPL(0,1,MOON_SE03001,,`001:4000,,`017:50);");
        Assert.Equal(["0", "1", "MOON_SE03001", "", "`001:4000", "", "`017:50"], args);
        Assert.Equal(["-", "-", "-", "-", "1", "-", "17"], tags);
        Assert.Equal("4000", Encoding.ASCII.GetString(ArgumentList.Untagged("`001:4000"u8)));
        Assert.Equal("abc", Encoding.ASCII.GetString(ArgumentList.Untagged("abc"u8)));
        Assert.Empty(ArgumentList.Untagged("`01"u8).ToArray());
        Assert.Null(ArgumentList.Tag("`01:5"u8));
        Assert.Null(ArgumentList.Tag("`0a1:5"u8));
        Assert.Equal(68, ArgumentList.Tag("`068:100"u8));
    }
}
