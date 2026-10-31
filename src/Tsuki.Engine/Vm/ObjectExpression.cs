using System.Buffers;

namespace Tsuki.Engine.Vm;

/// <summary>Why <see cref="ObjectExpression.TryEvaluate"/> has no value: the cases where the PS4 code would crash or return stack garbage.</summary>
public enum ExpressionError : byte
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>Division or remainder by zero, or INT_MIN / -1 (an <c>idiv</c> fault).</summary>
    DivisionFault = 1,

    /// <summary>A unary operator without an operand.</summary>
    UnaryWithoutOperand = 2,

    /// <summary>A binary operator without two operands.</summary>
    BinaryWithoutOperands = 3,

    /// <summary>The evaluation stack is empty at the end.</summary>
    EmptyStack = 4,

    /// <summary>A tokenizer stop inside <c>{...}</c>: the engine dereferences address 1.</summary>
    InsideBraces = 5,
}

/// <summary>
/// The expression evaluator behind the integer getters of the object parser (eboot text VA 0xDA50 with the tokenizer at
/// VA 0xDE30; docs/formats/script.md 3.3a). Port of <c>tsuki.script.obj_expr</c>; the reference was checked against the
/// eboot's own code under emulation on all 35,474 distinct retail argument texts.
/// </summary>
/// <remarks>
/// <para>
/// A C-like expression over decimal numbers with the operators <c>+ - * / % &lt;&lt; &gt;&gt; &lt; &lt;= &gt; &gt;= == != &amp; ^ | &amp;&amp; ||
/// ! ~</c>, 32-bit signed wrap-around arithmetic, <c>{}</c> for grouping (the round brackets delimit arguments) and
/// <b>right-associative</b> chains of equal precedence: <c>10-3-2</c> is 9. The tokenizer stops without error at the first
/// byte it does not know (<c>242.25</c> is 242, <c>img1728</c> is 0, <c>0x10</c> is 0, a tag <c>`017:100</c> is 0), keeping
/// what it has already emitted but not the pending operators (<c>5-</c> is 5).
/// </para>
/// <para>The port is allocation-free for arguments up to 1024 bytes (a statement has at most 0x300).</para>
/// </remarks>
public static class ObjectExpression
{
    // Operator codes of the tokenizer: high nibble 2 = unary, 3 = binary; the next nibble is the precedence level
    // (lower binds tighter).
    private const int UnaryPlus = 0x22000001;
    private const int UnaryNegate = 0x22000002;
    private const int UnaryNot = 0x22000003;
    private const int UnaryComplement = 0x22000004;
    private const int Mul = 0x33000001;
    private const int Div = 0x33000002;
    private const int Mod = 0x33000003;
    private const int Add = 0x34000001;
    private const int Sub = 0x34000002;
    private const int Shl = 0x35000001;
    private const int Shr = 0x35000002;
    private const int Lt = 0x36000001;
    private const int Le = 0x36000002;
    private const int Gt = 0x36000003;
    private const int Ge = 0x36000004;
    private const int Eq = 0x37000001;
    private const int Ne = 0x37000002;
    private const int BitAnd = 0x38000001;
    private const int BitXor = 0x39000001;
    private const int BitOr = 0x3A000001;
    private const int LogicalAnd = 0x3B000001;
    private const int LogicalOr = 0x3C000001;

    private const int OperandMarker = 0x10000000;

    private const int Stop = -1;
    private const int BraceError = -2;

    /// <summary>Evaluates one argument text; false (with the reason in <paramref name="error"/>) where the engine has no defined value.</summary>
    public static bool TryEvaluate(ReadOnlySpan<byte> arg, out int value, out ExpressionError error)
    {
        int need = (3 * arg.Length) + 8;
        int[]? rented = null;
        Span<int> work = need <= 3080 ? stackalloc int[need] : (rented = ArrayPool<int>.Shared.Rent(need));
        try
        {
            return Run(arg, work, out value, out error);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Evaluates one argument text; null where the engine has no defined value.</summary>
    public static int? Evaluate(ReadOnlySpan<byte> arg) => TryEvaluate(arg, out int v, out _) ? v : null;

    private static bool Run(ReadOnlySpan<byte> arg, Span<int> work, out int value, out ExpressionError error)
    {
        value = 0;
        error = ExpressionError.None;
        int tokenCapacity = (2 * arg.Length) + 4;
        var tokenizer = new Tokenizer(arg, work[..tokenCapacity], work[tokenCapacity..]);
        int r = tokenizer.Run(0, 0);
        if (r == BraceError)
        {
            error = ExpressionError.InsideBraces;
            return false;
        }

        // r == Stop: the tokenizer stopped at an unknown byte; what was emitted so far is evaluated.
        ReadOnlySpan<int> toks = tokenizer.Tokens;
        if (toks.IsEmpty)
        {
            return true;
        }

        Span<int> stack = work[tokenCapacity..];
        int sp = 0;
        for (int i = 0; i < toks.Length; i++)
        {
            int t = toks[i];
            if (t == OperandMarker)
            {
                stack[sp++] = toks[++i];
                continue;
            }

            int kind = t >> 28;
            if (kind == 2)
            {
                if (sp == 0)
                {
                    error = ExpressionError.UnaryWithoutOperand;
                    return false;
                }

                int x = stack[sp - 1];
                stack[sp - 1] = t switch
                {
                    UnaryNegate => unchecked(-x),
                    UnaryNot => x == 0 ? 1 : 0,
                    UnaryComplement => ~x,
                    _ => x,
                };
            }
            else if (kind == 3)
            {
                if (sp < 2)
                {
                    error = ExpressionError.BinaryWithoutOperands;
                    return false;
                }

                int b = stack[--sp];
                if (!TryApply(t, stack[sp - 1], b, out int r2))
                {
                    error = ExpressionError.DivisionFault;
                    return false;
                }

                stack[sp - 1] = r2;
            }
        }

        if (sp == 0)
        {
            error = ExpressionError.EmptyStack;
            return false;
        }

        value = stack[sp - 1];
        return true;
    }

    private static bool TryApply(int code, int a, int b, out int result)
    {
        result = 0;
        switch (code)
        {
            case Mul:
                result = unchecked(a * b);
                return true;
            case Div:
            case Mod:
                if (b == 0 || (a == int.MinValue && b == -1))
                {
                    return false;
                }

                result = code == Div ? a / b : a % b;
                return true;
            case Add:
                result = unchecked(a + b);
                return true;
            case Sub:
                result = unchecked(a - b);
                return true;
            case Shl:
                result = a << (b & 31);
                return true;
            case Shr:
                result = a >> (b & 31);
                return true;
            case Lt:
                result = a < b ? 1 : 0;
                return true;
            case Le:
                result = a <= b ? 1 : 0;
                return true;
            case Gt:
                result = a > b ? 1 : 0;
                return true;
            case Ge:
                result = a >= b ? 1 : 0;
                return true;
            case Eq:
                result = a == b ? 1 : 0;
                return true;
            case Ne:
                result = a != b ? 1 : 0;
                return true;
            case BitAnd:
                result = a & b;
                return true;
            case BitXor:
                result = a ^ b;
                return true;
            case BitOr:
                result = a | b;
                return true;
            case LogicalAnd:
                result = a != 0 && b != 0 ? 1 : 0;
                return true;
            case LogicalOr:
                result = (a | b) != 0 ? 1 : 0;
                return true;
            default:
                result = a;
                return true;
        }
    }

    private static int Level(int code) => (code >> 24) & 0xF;

    /// <summary>
    /// VA 0xDE30: a shunting-yard over the argument bytes producing postfix tokens (an operand is the marker followed by its
    /// value). One operator stack is shared by the nesting levels; a level only touches the entries above its base.
    /// </summary>
    private ref struct Tokenizer
    {
        private readonly ReadOnlySpan<byte> _s;
        private readonly Span<int> _tokens;
        private readonly Span<int> _ops;
        private int _count;
        private int _opCount;

        public Tokenizer(ReadOnlySpan<byte> s, Span<int> tokens, Span<int> ops)
        {
            _s = s;
            _tokens = tokens;
            _ops = ops;
            _count = 0;
            _opCount = 0;
        }

        public readonly ReadOnlySpan<int> Tokens => _tokens[.._count];

        private readonly int At(int i) => (uint)i < (uint)_s.Length ? _s[i] : 0;

        /// <summary>
        /// Tokenizes from <paramref name="p"/> up to the terminator <paramref name="term"/>. Returns the position where it
        /// stopped, <see cref="Stop"/> at an unknown byte (pending operators are abandoned), or <see cref="BraceError"/>
        /// when a stop happened inside <c>{...}</c>.
        /// </summary>
        public int Run(int p, byte term)
        {
            int baseOps = _opCount;
            int c = At(p);
            if (c == 0 || c == term || p >= _s.Length)
            {
                return Finish(baseOps, p);
            }

            bool after = false; // false: expecting an operand; true: just after an operand
            while (true)
            {
                c = At(p);
                if (after && (c == 0 || c == term || p >= _s.Length))
                {
                    return Finish(baseOps, p);
                }

                if (c is >= '0' and <= '9')
                {
                    long v = Strtol10(ref p);
                    _tokens[_count++] = OperandMarker;
                    _tokens[_count++] = unchecked((int)v);
                    after = true;
                    continue;
                }

                if (c == '{')
                {
                    int r = Run(p + 1, (byte)'}');
                    if (r < 0)
                    {
                        return BraceError;
                    }

                    p = r + 1;
                    after = true;
                    continue;
                }

                if (_s.Length - p < 2)
                {
                    return Stop; // an operator as the last byte: stop without flushing
                }

                int next = At(p + 1);
                int code;
                switch (c)
                {
                    case '!':
                        code = next == '=' ? Ne : UnaryNot;
                        p += next == '=' ? 1 : 0;
                        break;
                    case '%':
                        code = Mod;
                        break;
                    case '&':
                        code = next == '&' ? LogicalAnd : BitAnd;
                        p += next == '&' ? 1 : 0;
                        break;
                    case '*':
                        code = Mul;
                        break;
                    case '+':
                        code = after ? Add : UnaryPlus;
                        break;
                    case '-':
                        code = after ? Sub : UnaryNegate;
                        break;
                    case '/':
                        code = Div;
                        break;
                    case '<':
                        code = next == '=' ? Le : next == '<' ? Shl : Lt;
                        p += next is '=' or '<' ? 1 : 0;
                        break;
                    case '=':
                        if (next != '=')
                        {
                            return Stop; // a lone '='
                        }

                        code = Eq;
                        p += 1;
                        break;
                    case '>':
                        code = next == '=' ? Ge : next == '>' ? Shr : Gt;
                        p += next is '=' or '>' ? 1 : 0;
                        break;
                    case '^':
                        code = BitXor;
                        break;
                    case '|':
                        code = next == '|' ? LogicalOr : BitOr;
                        p += next == '|' ? 1 : 0;
                        break;
                    case '~':
                        code = UnaryComplement;
                        break;
                    default:
                        return Stop; // any other byte (letters, '.', '`', space, '(', ...)
                }

                int level = Level(code);
                while (_opCount > baseOps && level > Level(_ops[_opCount - 1]))
                {
                    _tokens[_count++] = _ops[--_opCount];
                }

                _ops[_opCount++] = code;
                p++;
                c = At(p);
                if (c == 0 || c == term || p >= _s.Length)
                {
                    return Finish(baseOps, p);
                }

                after = false;
            }
        }

        private int Finish(int baseOps, int p)
        {
            while (_opCount > baseOps)
            {
                _tokens[_count++] = _ops[--_opCount];
            }

            return p;
        }

        /// <summary>
        /// C <c>strtol(s + p, &amp;end, 10)</c> for a string that starts with a digit (the only way VA 0xDE30 calls it): the
        /// value clamped to a 64-bit long; <paramref name="p"/> moves to the end of the digits.
        /// </summary>
        private readonly long Strtol10(ref int p)
        {
            ulong v = 0;
            const ulong Cap = long.MaxValue;
            while (p < _s.Length && _s[p] is >= (byte)'0' and <= (byte)'9')
            {
                ulong d = (ulong)(_s[p] - '0');
                v = v <= (Cap - d) / 10 ? (v * 10) + d : Cap;
                p++;
            }

            return (long)v;
        }
    }
}
