using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Text;

/// <summary>A decoded script as text: its name (allscr section 0) and its statements as one string (UTF-8 decoded).</summary>
/// <param name="Name">The script name.</param>
/// <param name="Text">The decoded script.</param>
public sealed record ScriptSource(string Name, string Text);

/// <summary>The four commands that carry message text (docs/formats/text.md 5.1).</summary>
public enum TextCommandOp
{
    /// <summary><c>_ZM&lt;5 hex&gt;(message)</c>: shows a message.</summary>
    Zm,

    /// <summary><c>_ZD&lt;5 hex&gt;(message)</c>: a disabled message, never shown.</summary>
    Zd,

    /// <summary><c>_MSAD(message)</c>: adds text to the current message.</summary>
    Msad,

    /// <summary><c>_SELR(n,message[,locked])</c>: defines a choice.</summary>
    Selr,
}

/// <summary>
/// One text command of a script, found by a light scan of the statements (<c>tsuki.text_refs.TextCommand</c>; docs/formats/text.md 5).
/// </summary>
public sealed record TextCommand(
    int Script,
    string Name,
    int Pos,
    TextCommandOp Op,
    string Label,
    string Arg,
    int? Choice,
    ImmutableArray<string> Extra)
{
    /// <summary>The op as the Python reference writes it (<c>ZM</c>, <c>ZD</c>, <c>MSAD</c>, <c>SELR</c>).</summary>
    public string OpName => Op switch
    {
        TextCommandOp.Zm => "ZM",
        TextCommandOp.Zd => "ZD",
        TextCommandOp.Msad => "MSAD",
        _ => "SELR",
    };

    /// <summary>
    /// The part of the argument the engine keeps: <c>_ZM/_ZD/_MSAD</c> cut at the first <c>@e</c> / <c>@n</c> and at 0x300 bytes
    /// (copy loop at VA 0x92458..0x924B0); <c>_SELR</c> argument 1 as it is.
    /// </summary>
    public string Message
    {
        get
        {
            if (Op == TextCommandOp.Selr)
            {
                return Arg;
            }

            int cut = EndIndex(Arg);
            string msg = cut >= 0 ? Arg[..cut] : Arg;
            return TextReferences.TruncateUtf8(msg, TextRules.MessageArgMax);
        }
    }

    /// <summary><c>e</c>, <c>n</c> or empty (the closing parenthesis); always empty for <c>_SELR</c>.</summary>
    public string EndCode
    {
        get
        {
            if (Op == TextCommandOp.Selr)
            {
                return string.Empty;
            }

            int cut = EndIndex(Arg);
            return cut >= 0 ? Arg[cut + 1].ToString() : string.Empty;
        }
    }

    /// <summary>The engine's end mode: 0 for <c>)</c>, 1 for <c>@n</c>, 2 for <c>@e</c>; null for <c>_SELR</c>.</summary>
    public int? EndMode => Op == TextCommandOp.Selr ? null : EndCode switch { "n" => 1, "e" => 2, _ => 0 };

    /// <summary>False for <c>_ZD</c> (the marker dispatcher at VA 0x98479 never shows a disabled message).</summary>
    public bool Displayed => Op != TextCommandOp.Zd;

    /// <summary>A <c>_SELR</c> whose third argument is 1: the choice the menu cursor skips (text.md 5.2; the meaning is a hypothesis).</summary>
    public bool Locked => Op == TextCommandOp.Selr && Extra.Length > 0 && TextReferences.PyStrip(Extra[0]) == "1";

    /// <summary>The <c>$NNNNNN</c> ids in <see cref="Message"/>.</summary>
    public IReadOnlyList<int> Refs => TextReferences.ScanRefs(Message);

    /// <summary>The slot string the engine builds for menu position <paramref name="slot"/> (<c>_SELR</c> only), as UTF-8.</summary>
    public byte[] ChoiceSlot(int slot) =>
        Op == TextCommandOp.Selr
            ? TextRules.ChoiceSlotText(slot, Encoding.UTF8.GetBytes(Message))
            : throw new InvalidOperationException("not a _SELR command");

    private static int EndIndex(string arg)
    {
        for (int i = 0; i + 1 < arg.Length; i++)
        {
            if (arg[i] == '@' && arg[i + 1] is 'e' or 'n')
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// How the scripts reference <c>script_text</c> lines (docs/formats/text.md 5): the text commands, the reference index, the message
/// groups, the census and the classes of the unreferenced ids; a port of <c>tsuki.text_refs</c>. The scan is regex-level (statements
/// split at <c>;</c>), exactly as the Python reference does it; the full bytecode-level reading is the VM's.
/// </summary>
public static class TextReferences
{
    /// <summary>Lines that script 00_02 shows as pre-rendered images (allpac0 <c>IMGxxxx</c>) instead of text commands (text.md 5.7).</summary>
    public static IReadOnlyDictionary<int, string> ImageTextIds { get; } = new SortedDictionary<int, string>
    {
        [12] = "IMG2629", [13] = "IMG2630", [14] = "IMG2631", [15] = "IMG2632", [16] = "IMG2633",
        [59] = "IMG2634", [63] = "IMG2635", [68] = "IMG2636", [82] = "IMG2459", [87] = "IMG2460", [93] = "IMG2461",
    };

    /// <summary>The classes of an unreferenced id, in the order of the Python reference.</summary>
    public static IReadOnlyList<string> UnreferencedClasses { get; } =
        ["choice-number", "image-text", "blank", "merged-fragment", "not-shown"];

    /// <summary>
    /// Python's <c>str.strip()</c>: removes the characters <c>str.isspace()</c> accepts (including U+001C..U+001F, U+0085, U+00A0 and the
    /// ideographic space), which differ from <see cref="char.IsWhiteSpace(char)"/>.
    /// </summary>
    public static string PyStrip(string s)
    {
        int a = 0;
        int b = s.Length;
        while (a < b && IsPySpace(s[a]))
        {
            a++;
        }

        while (b > a && IsPySpace(s[b - 1]))
        {
            b--;
        }

        return s[a..b];
    }

    /// <summary>True for the characters Python's <c>str.isspace()</c> accepts.</summary>
    public static bool IsPySpace(char c) =>
        c is (>= (char)0x09 and <= (char)0x0D) or (>= (char)0x1C and <= (char)0x20) or (char)0x85 or (char)0xA0 or (char)0x1680
            or (>= (char)0x2000 and <= (char)0x200A) or (char)0x2028 or (char)0x2029 or (char)0x202F or (char)0x205F or (char)0x3000;

    /// <summary>The ids of every <c>$</c> followed by six decimal digits (Unicode digits count, as in Python's <c>\d</c>), scanning without overlap.</summary>
    public static List<int> ScanRefs(string s)
    {
        var ids = new List<int>();
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '$' && TryRef(s, i, out int id))
            {
                ids.Add(id);
                i += 7;
            }
            else
            {
                i++;
            }
        }

        return ids;
    }

    /// <summary>Replaces every <c>$</c> + six digits with <c>$N</c> (the argument shape of the census).</summary>
    public static string ReplaceRefs(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '$' && TryRef(s, i, out _))
            {
                sb.Append("$N");
                i += 7;
            }
            else
            {
                sb.Append(s[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>The longest prefix of <paramref name="s"/> whose UTF-8 form fits in <paramref name="maxBytes"/> (Python's <c>raw[:n].decode("utf-8", "ignore")</c> for a valid string).</summary>
    public static string TruncateUtf8(string s, int maxBytes)
    {
        int bytes = 0;
        int i = 0;
        while (i < s.Length)
        {
            int len = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(s.AsSpan(i, len));
            if (bytes + size > maxBytes)
            {
                return s[..i];
            }

            bytes += size;
            i += len;
        }

        return s;
    }

    /// <summary>Decodes every script of an archive as strict UTF-8 text (<c>load_scripts</c>).</summary>
    public static List<ScriptSource> LoadScripts(Scripts.AllscrArchive allscr)
    {
        ArgumentNullException.ThrowIfNull(allscr);
        var list = new List<ScriptSource>(allscr.Count);
        for (int k = 0; k < allscr.Count; k++)
        {
            list.Add(new ScriptSource(allscr.Names[k], TextTable.StrictUtf8.GetString(allscr.ScriptBytes(k).Span)));
        }

        return list;
    }

    /// <summary>Every text command of every script, in script and statement order.</summary>
    public static List<TextCommand> IterTextCommands(IReadOnlyList<ScriptSource> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        var list = new List<TextCommand>();
        for (int k = 0; k < scripts.Count; k++)
        {
            string[] statements = scripts[k].Text.Split(';');
            for (int pos = 0; pos < statements.Length; pos++)
            {
                if (TryParseCommand(PyStrip(statements[pos]), out TextCommandOp op, out string label, out string arg))
                {
                    int? choice = null;
                    ImmutableArray<string> extra = [];
                    if (op == TextCommandOp.Selr)
                    {
                        string[] parts = arg.Split(',');
                        if (parts.Length > 1 && parts[1].StartsWith('`'))
                        {
                            parts[1] = parts[1].Length > 5 ? parts[1][5..] : string.Empty;
                        }

                        string head = PyStrip(parts[0]);
                        string digits = head.TrimStart('-');
                        if (digits.Length > 0 && digits.All(c => c is >= '0' and <= '9') &&
                            int.TryParse(head, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n))
                        {
                            choice = n;
                        }

                        arg = parts.Length > 1 ? parts[1] : string.Empty;
                        extra = [.. parts.Skip(2)];
                    }

                    list.Add(new TextCommand(k, scripts[k].Name, pos, op, label, arg, choice, extra));
                }
            }
        }

        return list;
    }

    /// <summary>The commands that reference each id (ids in ascending order).</summary>
    public static SortedDictionary<int, List<TextCommand>> RefIndex(IEnumerable<TextCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var index = new SortedDictionary<int, List<TextCommand>>();
        foreach (TextCommand c in commands)
        {
            foreach (int r in c.Refs)
            {
                if (!index.TryGetValue(r, out List<TextCommand>? list))
                {
                    list = [];
                    index[r] = list;
                }

                list.Add(c);
            }
        }

        return index;
    }

    /// <summary>
    /// The displayed messages as the engine's raw buffer sees them: a <c>_ZM</c> followed by the <c>_MSAD</c> commands that append to it
    /// (until the next <c>_ZM</c>/<c>_ZD</c> or the next script). <c>_SELR</c> does not touch the buffer; a <c>_ZD</c> starts no group.
    /// </summary>
    public static List<List<TextCommand>> MessageGroups(IEnumerable<TextCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var groups = new List<List<TextCommand>>();
        List<TextCommand>? cur = null;
        foreach (TextCommand c in commands)
        {
            if (c.Op == TextCommandOp.Selr)
            {
                continue;
            }

            if (c.Op == TextCommandOp.Zd)
            {
                cur = null;
            }
            else if (c.Op == TextCommandOp.Zm || cur is null || cur[0].Script != c.Script)
            {
                cur = [c];
                groups.Add(cur);
            }
            else
            {
                cur.Add(c);
            }
        }

        return groups;
    }

    /// <summary>The ids referenced only by disabled <c>_ZD</c> messages (so never displayed), ascending.</summary>
    public static List<int> ZdOnlyIds(IEnumerable<TextCommand> commands) =>
        [.. RefIndex(commands).Where(kv => kv.Value.All(c => c.Op == TextCommandOp.Zd)).Select(kv => kv.Key)];

    /// <summary>Counts of ops, argument shapes and markup codes in the message arguments (<c>script_census</c>).</summary>
    public static (SortedDictionary<string, int> Ops, SortedDictionary<string, int> Shapes, SortedDictionary<string, int> Codes) ScriptCensus(
        IEnumerable<TextCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var ops = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var shapes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var codes = new SortedDictionary<string, int>(StringComparer.Ordinal);

        static void Bump(SortedDictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;

        foreach (TextCommand c in commands)
        {
            Bump(ops, c.OpName);
            bool hasRefs = c.Refs.Count > 0;
            if (c.Op == TextCommandOp.Selr)
            {
                string tail = string.Concat(c.Extra.Select(x => "," + x));
                string body = hasRefs ? ReplaceRefs(c.Arg) : "<label or inline text>";
                Bump(shapes, $"SELR(<n>,{body}{tail})");
            }
            else if (hasRefs)
            {
                Bump(shapes, $"{c.OpName}(" + ReplaceRefs(c.Arg) + ")");
            }
            else
            {
                Bump(shapes, $"{c.OpName}(<inline text>)");
            }

            foreach (MarkupToken t in Markup.Tokenize(Encoding.UTF8.GetBytes(c.Arg), Language.Ja, script: c.Op != TextCommandOp.Selr))
            {
                if (t is CodeToken code)
                {
                    string letter = Encoding.UTF8.GetString(code.Code.AsSpan());
                    string arg = code.Arg is { } a ? Encoding.UTF8.GetString(a.AsSpan()) : string.Empty;
                    Bump(codes, "@" + letter + arg);
                }
            }
        }

        return (ops, shapes, codes);
    }

    /// <summary>
    /// A readable transcript of one script in one language: every text command, expanded. Choices are shown without the engine's number
    /// prefix (it depends on which <c>_SELR</c> commands the <c>_IF__</c> branches execute).
    /// </summary>
    public static List<string> SceneTranscript(ScriptText text, IReadOnlyList<ScriptSource> scripts, string name, Language lang)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(scripts);
        var rows = new List<string>();
        foreach (TextCommand c in IterTextCommands([.. scripts.Where(s => s.Name == name)]))
        {
            byte[] expanded = text.ExpandBytes(Encoding.UTF8.GetBytes(c.Message), lang);
            byte[] decoded = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(expanded));
            string shown = Encoding.UTF8.GetString(Markup.PlainText(decoded, lang, RubyRendering.Paren)).Replace("\n", " / ", StringComparison.Ordinal);
            string prefix = c.Op switch
            {
                TextCommandOp.Selr => $"> choice {c.Choice?.ToString(CultureInfo.InvariantCulture) ?? "None"}{(c.Locked ? " (locked)" : string.Empty)}: ",
                TextCommandOp.Zm => string.Empty,
                TextCommandOp.Zd => "[ZD] ",
                _ => "+ ",
            };
            string ids = string.Join(",", c.Refs.Select(r => r.ToString("D6", CultureInfo.InvariantCulture)));
            rows.Add($"{(ids.Length == 0 ? "------" : ids)}\t{prefix}{shown}");
        }

        return rows;
    }

    /// <summary>
    /// For every <c>_VPLY</c> / <c>_VPL2</c>, how many text commands follow it before the next voice command or the end of the script:
    /// count to number of voice commands, per op.
    /// </summary>
    public static (SortedDictionary<int, int> Vply, SortedDictionary<int, int> Vpl2) VoiceTextCounts(IReadOnlyList<ScriptSource> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        var vply = new SortedDictionary<int, int>();
        var vpl2 = new SortedDictionary<int, int>();
        foreach (ScriptSource script in scripts)
        {
            SortedDictionary<int, int>? cur = null;
            int n = 0;
            foreach (string statement in script.Text.Split(';'))
            {
                string c = PyStrip(statement);
                SortedDictionary<int, int>? voice = c.StartsWith("_VPLY(", StringComparison.Ordinal) ? vply
                    : c.StartsWith("_VPL2(", StringComparison.Ordinal) ? vpl2 : null;
                if (voice is not null)
                {
                    if (cur is not null)
                    {
                        cur[n] = cur.GetValueOrDefault(n) + 1;
                    }

                    cur = voice;
                    n = 0;
                }
                else if (TryParseCommand(c, out _, out _, out _))
                {
                    n++;
                }
            }

            if (cur is not null)
            {
                cur[n] = cur.GetValueOrDefault(n) + 1;
            }
        }

        return (vply, vpl2);
    }

    /// <summary>
    /// The class of every line id that no text command references: <c>choice-number</c> (43959..43968), <c>image-text</c>,
    /// <c>blank</c> (the ja line is a blank or <c>■</c>), <c>merged-fragment</c> (the ja text repeats the end of the previous line or the
    /// start of the next one) and <c>not-shown</c>.
    /// </summary>
    public static SortedDictionary<int, string> ClassifyUnreferenced(ScriptText text, IEnumerable<TextCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(text);
        SortedDictionary<int, List<TextCommand>> used = RefIndex(commands);
        TextTable ja = text.Table(Language.Ja);
        var result = new SortedDictionary<int, string>();
        for (int i = 0; i < text.Count; i++)
        {
            if (used.ContainsKey(i))
            {
                continue;
            }

            string s = StripIdeographic(ja.Line(i));
            string prev = i > 0 ? StripIdeographic(ja.Line(i - 1)) : string.Empty;
            string next = i + 1 < text.Count ? StripIdeographic(ja.Line(i + 1)) : string.Empty;
            if (i >= TextRules.ChoiceNumberOverflow)
            {
                result[i] = "choice-number";
            }
            else if (ImageTextIds.ContainsKey(i))
            {
                result[i] = "image-text";
            }
            else if (s is "" or "■")
            {
                result[i] = "blank";
            }
            else if (prev.EndsWith(s, StringComparison.Ordinal) || next.StartsWith(s, StringComparison.Ordinal))
            {
                result[i] = "merged-fragment";
            }
            else
            {
                result[i] = "not-shown";
            }
        }

        return result;
    }

    private static string StripIdeographic(string s) => s.Trim('　', ' ');

    private static bool TryRef(string s, int at, out int id)
    {
        id = 0;
        if (at + 7 > s.Length)
        {
            return false;
        }

        for (int k = 1; k <= 6; k++)
        {
            char ch = s[at + k];
            if (!char.IsDigit(ch))
            {
                return false;
            }

            id = (id * 10) + (int)char.GetNumericValue(ch);
        }

        return true;
    }

    /// <summary>
    /// <c>^_(?:(ZM|ZD)([0-9a-fA-F]{5})|(MSAD)|(SELR))\((.*)\)\s*$</c> on an already stripped statement: the op, the five hex digits of a
    /// <c>_ZM/_ZD</c>, and everything between the first <c>(</c> and the final <c>)</c>.
    /// </summary>
    private static bool TryParseCommand(string stripped, out TextCommandOp op, out string label, out string arg)
    {
        op = default;
        label = string.Empty;
        arg = string.Empty;
        if (stripped.Length < 6 || stripped[0] != '_')
        {
            return false;
        }

        int open;
        if (stripped.StartsWith("_ZM", StringComparison.Ordinal) || stripped.StartsWith("_ZD", StringComparison.Ordinal))
        {
            if (stripped.Length < 8 || stripped.AsSpan(3, 5).ContainsAnyExcept("0123456789abcdefABCDEF"))
            {
                return false;
            }

            op = stripped[2] == 'M' ? TextCommandOp.Zm : TextCommandOp.Zd;
            label = stripped.Substring(3, 5);
            open = 8;
        }
        else if (stripped.StartsWith("_MSAD", StringComparison.Ordinal))
        {
            op = TextCommandOp.Msad;
            open = 5;
        }
        else if (stripped.StartsWith("_SELR", StringComparison.Ordinal))
        {
            op = TextCommandOp.Selr;
            open = 5;
        }
        else
        {
            return false;
        }

        if (open >= stripped.Length || stripped[open] != '(' || stripped.Length < open + 2 || stripped[^1] != ')')
        {
            return false;
        }

        arg = stripped[(open + 1)..^1];
        return true;
    }
}
