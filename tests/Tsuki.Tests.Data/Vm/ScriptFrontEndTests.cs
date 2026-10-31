using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Vm;

namespace Tsuki.Tests.Data.Vm;

/// <summary>
/// Package C1: the segmenter and both argument readers of <c>Tsuki.Engine.Vm</c> against the Python oracle on the real scripts
/// (PACE 7.2 #4 part 1): the slice export of scripts 2 and 3 field by field, then all 549 scripts by per-script hash of the
/// canonical records (1,083,168 segments; the record format is in <c>tools/oracle/script.py</c>).
/// </summary>
public sealed class ScriptFrontEndTests
{
    // script.md 1: 549 scripts, 19,617,273 decoded bytes, 1,083,168 statements, 98 distinct names, longest statement 207 (2.5).
    private const int TotalBytes = 19_617_273;
    private const int TotalSegments = 1_083_168;

    // ---- slice: scripts 2 (00_01) and 3 (00_02), every field of the golden statements ---------------------------------------

    [GameDataTheory]
    [InlineData(2)]
    [InlineData(3)]
    public void SliceStatementsEqualTheGoldenExportFieldByField(int k)
    {
        using JsonDocument doc = ScriptFixtures.ReadJson(Path.Combine(ScriptFixtures.SliceScripts, $"script_{k:D4}.json"));
        JsonElement root = doc.RootElement;
        byte[] golden = File.ReadAllBytes(Path.Combine(ScriptFixtures.SliceScripts, $"script_{k:D4}.bin"));
        byte[] script = ScriptFixtures.Script(k);
        Assert.Equal(golden, script); // the slice bytes are what this machine decodes
        Assert.Equal(root.GetProperty("decoded").GetProperty("sha1").GetString(), Convert.ToHexStringLower(SHA1.HashData(script)));

        JsonElement statements = root.GetProperty("statements");
        var m = new Mismatches();
        int index = 0;
        int pos = 0;
        var ranges = new List<ArgRange>();
        foreach (JsonElement g in statements.EnumerateArray())
        {
            if (pos >= script.Length)
            {
                m.Add($"script {k}: golden has more statements than the script has segments (at {index})");
                break;
            }

            ScriptSegment s = ScriptSegmenter.Read(script, pos);
            string id = $"script {k} statement {index}";
            m.Equal(id + " index", g.GetProperty("index").GetInt32(), index);
            m.Equal(id + " offset", g.GetProperty("offset").GetInt32(), s.Start);
            m.Equal(id + " end", g.GetProperty("end").GetInt32(), s.End);
            m.Equal(id + " name_offset", g.GetProperty("name_offset").GetInt32(), s.IsCommand ? s.Underscore : -1);
            m.Equal(id + " key", g.GetProperty("key").GetString(), s.HasName ? Encoding.Latin1.GetString(script, s.NameStart, 4) : string.Empty);
            bool marker = ScriptSegmenter.TryReadMarkerId(script, s, out int block, out int sub);
            JsonElement gm = g.GetProperty("marker");
            m.Equal(id + " is marker", gm.ValueKind == JsonValueKind.Object, marker);
            if (marker && gm.ValueKind == JsonValueKind.Object)
            {
                m.Equal(id + " block", gm.GetProperty("block").GetInt32(), block);
                m.Equal(id + " sub", gm.GetProperty("sub").GetInt32(), sub);
            }

            if (s.IsCommand && ScriptSegmenter.TryGetStatement(k, script, s, out Statement st))
            {
                CompareArguments(m, id, g, st, ranges);
            }
            else
            {
                m.Equal(id + " is skipped", -1, g.GetProperty("name_offset").GetInt32());
            }

            index++;
            pos = s.End;
        }

        m.Equal($"script {k} statement count", root.GetProperty("statement_count").GetInt32(), index);
        m.Equal($"script {k} end", script.Length, pos);
        m.AssertNone($"scripts {k}: golden slice statements");
    }

    private static void CompareArguments(Mismatches m, string id, JsonElement g, Statement st, List<ArgRange> ranges)
    {
        ReadOnlySpan<byte> args = st.Args.Span;

        // the textual arguments, tags and trailer
        ranges.Clear();
        int close = StatementArguments.SplitText(st, ranges);
        JsonElement gargs = g.GetProperty("args");
        m.Equal(id + " arg count", gargs.GetArrayLength(), ranges.Count);
        for (int i = 0; i < Math.Min(gargs.GetArrayLength(), ranges.Count); i++)
        {
            byte[] want = TextOrHex(gargs[i]);
            m.Equal(id + $" arg {i}", Convert.ToHexStringLower(want), Convert.ToHexStringLower(ranges[i].In(args)));
            JsonElement tag = g.GetProperty("tags")[i];
            int? got = ArgumentList.Tag(ranges[i].In(args));
            m.Equal(id + $" tag {i}", tag.ValueKind == JsonValueKind.Null ? (int?)null : tag.GetInt32(), got);
        }

        JsonElement tr = g.GetProperty("trailer");
        ReadOnlySpan<byte> trailer = ArgumentList.Trailer(args, close);
        m.Equal(id + " trailer", tr.ValueKind == JsonValueKind.Null ? string.Empty : Convert.ToHexStringLower(TextOrHex(tr)), Convert.ToHexStringLower(trailer));

        // inline readers at every index plus the first missing one
        JsonElement inline = g.GetProperty("inline");
        m.Equal(id + " inline cells", inline.GetArrayLength(), Math.Max(ranges.Count, 1) + 1);
        for (int i = 0; i < inline.GetArrayLength(); i++)
        {
            JsonElement cell = inline[i];
            m.Equal(id + $" i16[{i}]", cell.GetProperty("i16").GetInt32(), InlineReader.ReadInt16(st, i));
            m.Equal(id + $" i32[{i}]", cell.GetProperty("i32").GetInt32(), InlineReader.ReadInt32(st, i));
            m.Equal(id + $" present[{i}]", cell.GetProperty("present").GetBoolean(), InlineReader.IsPresent(st, i));
        }

        // object parser
        JsonElement obj = g.GetProperty("object");
        Span<ArgRange> r = stackalloc ArgRange[ObjectParser.MaxArguments];
        SplitStatus status = ObjectParser.Split(st, r, out int count);
        m.Equal(id + " object error", obj.GetProperty("error").ValueKind != JsonValueKind.Null, status != SplitStatus.Ok);
        JsonElement oargs = obj.GetProperty("args");
        m.Equal(id + " object arg count", oargs.GetArrayLength(), status == SplitStatus.Ok ? count : 0);
        for (int i = 0; i < Math.Min(oargs.GetArrayLength(), count); i++)
        {
            JsonElement o = oargs[i];
            ReadOnlySpan<byte> data = r[i].In(args);
            m.Equal(id + $" object arg {i}", Convert.ToHexStringLower(TextOrHex(o.GetProperty("raw"))), Convert.ToHexStringLower(data));
            bool has = data.Length > 0;
            m.Equal(id + $" has[{i}]", o.GetProperty("has").GetBoolean(), has);
            JsonElement expr = o.GetProperty("expr");
            JsonElement undef = o.GetProperty("undefined");
            if (has)
            {
                bool ok = ObjectExpression.TryEvaluate(data, out int v, out ExpressionError e);
                m.Equal(id + $" undefined[{i}]", undef.ValueKind != JsonValueKind.Null, !ok);
                if (ok && expr.ValueKind == JsonValueKind.Number)
                {
                    m.Equal(id + $" expr[{i}]", expr.GetInt32(), v);
                }
                else if (!ok && undef.ValueKind == JsonValueKind.String)
                {
                    m.Equal(id + $" undefined kind[{i}]", ExpectedUndefinedCode(undef.GetString()!), CanonicalRecords.Code(e));
                }
            }
            else
            {
                m.Equal(id + $" empty arg has no expr[{i}]", JsonValueKind.Null, expr.ValueKind);
            }

            m.Equal(id + $" strict[{i}]", NullableInt(o.GetProperty("strict")), ObjectParser.StrictInt(data));
            m.Equal(id + $" partial[{i}]", NullableInt(o.GetProperty("partial")), ObjectParser.PartialInt(data));
        }

        // the value each handler reads
        JsonElement values = g.GetProperty("values");
        m.Equal(id + " value count", values.GetArrayLength(), ranges.Count);
        for (int i = 0; i < Math.Min(values.GetArrayLength(), ranges.Count); i++)
        {
            ArgValue v = StatementArguments.Value(st, i);
            m.Equal(id + $" value[{i}]", NullableInt(values[i]), v.HasValue ? v.Value : (int?)null);
        }
    }

    private static int? NullableInt(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();

    private static byte[] TextOrHex(JsonElement e) =>
        e.TryGetProperty("text", out JsonElement t) ? Encoding.UTF8.GetBytes(t.GetString()!) : Convert.FromHexString(e.GetProperty("hex").GetString()!);

    private static string ExpectedUndefinedCode(string message) => message switch
    {
        _ when message.StartsWith("division fault", StringComparison.Ordinal) => "div",
        _ when message.StartsWith("unary operator", StringComparison.Ordinal) => "un",
        _ when message.StartsWith("binary operator", StringComparison.Ordinal) => "bin",
        _ when message.StartsWith("empty evaluation", StringComparison.Ordinal) => "empty",
        _ when message.StartsWith("error inside", StringComparison.Ordinal) => "brace",
        _ => message,
    };

    // ---- all 549 scripts by per-script hash ---------------------------------------------------------------------------------

    [GameDataFact]
    public void AllScriptsEqualTheOracleByPerScriptHash()
    {
        using JsonDocument doc = ScriptFixtures.ReadJson(Path.Combine(ScriptFixtures.ScriptArea, "hashes", "script_hashes.json"));
        JsonElement scripts = doc.RootElement.GetProperty("scripts");
        Assert.Equal(ScriptFixtures.ScriptCount, scripts.GetArrayLength());
        JsonElement totals = doc.RootElement.GetProperty("totals");
        Assert.Equal(TotalSegments, totals.GetProperty("segments").GetInt32());

        var m = new Mismatches();
        long segments = 0;
        long bytes = 0;
        long truncated = 0;
        Parallel.For(0, ScriptFixtures.ScriptCount, k =>
        {
            JsonElement want = scripts[k];
            byte[] script = ScriptFixtures.Script(k);
            CanonicalRecords.Records r = CanonicalRecords.Build(script, out int cut);
            Interlocked.Add(ref segments, r.SegmentCount);
            Interlocked.Add(ref bytes, script.Length);
            Interlocked.Add(ref truncated, cut);
            string id = $"script {k} ({want.GetProperty("name").GetString()})";
            if (want.GetProperty("script").GetInt32() != k)
            {
                m.Add($"{id}: golden list out of order");
            }

            m.Equal(id + " bytes", want.GetProperty("bytes").GetInt32(), script.Length);
            m.Equal(id + " decoded SHA-256", want.GetProperty("raw_sha256").GetString(), ScriptFixtures.Sha256(script));
            m.Equal(id + " segments", want.GetProperty("segments").GetInt32(), r.SegmentCount);
            m.Equal(id + " commands", want.GetProperty("commands").GetInt32(), r.CommandCount);
            m.Equal(id + " segment records", want.GetProperty("seg_sha256").GetString(), CanonicalRecords.Sha256(r.Segments));
            m.Equal(id + " argument records", want.GetProperty("args_sha256").GetString(), CanonicalRecords.Sha256(r.Arguments));
        });

        m.AssertNone("per-script hashes of the canonical records (diff one with: python -m oracle.script --lines K)");
        Assert.Equal(TotalSegments, segments);
        Assert.Equal(TotalBytes, bytes);
        Assert.Equal(0, truncated);
    }

    // ---- facts of script.md about the segmentation --------------------------------------------------------------------------

    [GameDataFact]
    public void TheSegmentsOfEveryScriptTileItExactlyAndAreAllCommands()
    {
        var names = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        int longest = 0;
        long total = 0;
        long markers = 0;
        var m = new Mismatches();
        Parallel.For(0, ScriptFixtures.ScriptCount, k =>
        {
            byte[] script = ScriptFixtures.Script(k);
            var image = ScriptSegmenter.CreateImage(k, script);
            int pos = 0;
            int localLongest = 0;
            for (int i = 0; i < image.SegmentStarts.Length; i++)
            {
                ScriptSegment s = ScriptSegmenter.Read(script, pos);
                if (s.Start != image.SegmentStarts[i] || s.End != image.SegmentEnd(i))
                {
                    m.Add($"script {k} segment {i}: image says {image.SegmentStarts[i]}..{image.SegmentEnd(i)}, reader says {s.Start}..{s.End}");
                }

                if (s.Truncated || !s.IsCommand || script[s.End - 1] != (byte)';')
                {
                    m.Add($"script {k} segment {i} at 0x{s.Start:x}: not a ';'-terminated command");
                }

                localLongest = Math.Max(localLongest, s.Length);
                string key = Encoding.Latin1.GetString(script, s.NameStart, 4);
                names.AddOrUpdate(key[0] == 'Z' ? "Z" + key[1] : key, 1, (_, c) => c + 1);
                if (key[0] == 'Z')
                {
                    Interlocked.Increment(ref markers);
                }

                pos = s.End;
            }

            m.Equal($"script {k} tiles the bytes", script.Length, pos);
            Interlocked.Add(ref total, image.SegmentStarts.Length);
            int seen;
            do
            {
                seen = Volatile.Read(ref longest);
            }
            while (localLongest > seen && Interlocked.CompareExchange(ref longest, localLongest, seen) != seen);
        });

        m.AssertNone("segmentation facts");
        Assert.Equal(TotalSegments, total);
        Assert.Equal(207, longest); // script.md 2.5: the longest statement is a _GOTS in JUMP10
        Assert.Equal(98, names.Count); // script.md 1: 94 commands + ZM / ZZ / ZY / ZD
        Assert.Equal(42_590, names["ZM"]); // ARCHITECTURE 5.4
        Assert.Equal(487_857, names["WKST"]);
        Assert.Equal(1_741, names.Where(p => p.Key[0] == 'J').Sum(p => p.Value)); // every J... is JUMP (script.md 3.6)
        Assert.True(markers > 40_000);
    }

    [GameDataFact]
    public void ObjectCommandsAreTheOraclesFiftyTwo()
    {
        using JsonDocument doc = ScriptFixtures.ReadJson(Path.Combine(ScriptFixtures.ScriptArea, "tables", "tables.json"));
        string[] want = doc.RootElement.GetProperty("obj_commands").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(52, want.Length);
        Assert.Equal(want.Order(StringComparer.Ordinal), ObjectCommands.Names.Order(StringComparer.Ordinal));
        Assert.All(want, n => Assert.True(ObjectCommands.Contains(CommandName.FromAscii(n)), n));
        Assert.False(ObjectCommands.Contains(CommandName.FromAscii("WKST")));
        Assert.Equal(0x300, doc.RootElement.GetProperty("max_statement").GetInt32());
        Assert.Equal(0x300, ScriptSegmenter.MaxSegmentBytes);
    }

    [GameDataFact]
    public void RetailArgumentTextsFollowTheDocumentedShapes()
    {
        // script.md 3.2: 14,305 statements with '()'; 28 decimal arguments of object-parser commands read as integers
        long emptyList = 0;
        long decimals = 0;
        long tagged = 0;
        Parallel.For(0, ScriptFixtures.ScriptCount, k =>
        {
            byte[] script = ScriptFixtures.Script(k);
            var ranges = new List<ArgRange>();
            for (int pos = 0; pos < script.Length;)
            {
                ScriptSegment s = ScriptSegmenter.Read(script, pos);
                pos = s.End;
                ScriptSegmenter.TryGetStatement(k, script, s, out Statement st);
                ranges.Clear();
                StatementArguments.SplitText(st, ranges);
                if (st.Args.Length >= 3 && st.Args.Span[1] == (byte)')' && st.Name.First != (byte)'Z')
                {
                    Interlocked.Increment(ref emptyList);
                }

                if (ObjectCommands.Contains(st.Name))
                {
                    foreach (ArgRange a in ranges)
                    {
                        ReadOnlySpan<byte> t = a.In(st.Args.Span);
                        if (t.IndexOf((byte)'.') > 0 && t[0] is >= (byte)'0' and <= (byte)'9')
                        {
                            Interlocked.Increment(ref decimals);
                        }
                    }
                }

                foreach (ArgRange a in ranges)
                {
                    if (ArgumentList.Tag(a.In(st.Args.Span)) is not null)
                    {
                        Interlocked.Increment(ref tagged);
                    }
                }
            }
        });

        Assert.Equal(14_305, emptyList);
        Assert.Equal(28, decimals); // 24 STCH alpha, 3 STGS, 1 SSW2
        Assert.Equal(32_221, tagged);
    }
}
