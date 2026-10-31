using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Tests.Data.Text.Layout;

/// <summary>
/// The page-1 messages of the M2 slice (00_02, ja and en): the layout block, the glyph records, the
/// page origins and the drawn quads against <c>samples/golden/slice/layouts</c> (textbox.md 5, 7; the
/// expanded <c>_ZM</c> messages come from <c>slice/text/lines.json</c>).
/// </summary>
public sealed class SliceLayoutTests
{
    private static readonly (string File, Language Lang)[] Files = [("layout_ja.json", Language.Ja), ("layout_en.json", Language.En)];

    private static uint Bits(JsonElement e) => e.GetProperty("bits").GetUInt32();

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    [GameDataFact]
    public void TheSliceMessagesLayOutLikeTheOracle()
    {
        using var text = JsonDocument.Parse(File.ReadAllBytes(LayoutData.GoldenFile("slice", Path.Combine("text", "lines.json"), LayoutData.SliceCommand)));
        var expanded = text.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var m = new Mismatches();
        int checkedMessages = 0;
        foreach ((string file, Language lang) in Files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(LayoutData.GoldenFile("slice", Path.Combine("layouts", file), LayoutData.SliceCommand)));
            string name = lang == Language.Ja ? "ja" : "en";
            var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(expanded.Length, messages.Length);
            var blocks = new TextBlock[messages.Length];
            for (int k = 0; k < messages.Length; k++)
            {
                var msg = messages[k];
                string tag = $"{name} message {k}";
                Assert.Equal(expanded[k].GetProperty("text_ids").ToString(), msg.GetProperty("text_ids").ToString());
                byte[] bytes = Convert.FromHexString(expanded[k].GetProperty("expanded").GetProperty(name).GetProperty("hex").GetString()!);
                var jp = msg.GetProperty("params");
                int window = jp.GetProperty("window").GetInt32();

                // the arguments the _ZM handler passes
                var box = MessageWindow.MainBoxParams(lang, window);
                m.Equal($"{tag} width", jp.GetProperty("width").GetInt32(), box.Width);
                m.Equal($"{tag} flags", jp.GetProperty("flags").GetInt32(), box.Flags);
                m.Equal($"{tag} color", jp.GetProperty("color").GetInt32(), box.Color);
                m.Equal($"{tag} x", jp.GetProperty("x").GetDouble(), box.X);
                m.Equal($"{tag} y", jp.GetProperty("y").GetDouble(), box.Y);
                m.Equal($"{tag} sx", Bits(jp.GetProperty("sx")), Bits((float)box.Sx));

                var p = box.ToLayoutParams(msg.GetProperty("layout_block").GetInt32());
                var b = TextLayout.Layout(bytes, lang, LayoutData.FontTables, p);
                blocks[k] = b;
                var jb = msg.GetProperty("block");
                m.Equal($"{tag} n", jb.GetProperty("n").GetInt32(), b.N);
                m.Equal($"{tag} box_width", jb.GetProperty("box_width").GetInt32(), b.BoxWidth);
                m.Equal($"{tag} nlines", jb.GetProperty("nlines").GetInt32(), b.NLines);
                m.Equal($"{tag} last_glyph_line", jb.GetProperty("last_glyph_line").GetInt32(), b.LastGlyphLine);
                m.Equal($"{tag} end_ctr", jb.GetProperty("end_ctr").GetInt32(), b.EndCtr);
                m.Equal($"{tag} sx", Bits(jb.GetProperty("sx")), Bits(b.Sx));
                m.Equal($"{tag} classes", jb.GetProperty("classes").GetString(), Convert.ToHexStringLower(b.Classes));
                int[] wordLen = [.. jb.GetProperty("wordlen").EnumerateArray().Select(e => e.GetInt32())];
                m.Equal($"{tag} wordlen", string.Join(",", wordLen), string.Join(",", b.WordLen.Take(wordLen.Length)));
                CompareFloatList(m, $"{tag} line_width", jb.GetProperty("line_width"), b.LineWidth);
                CompareFloatList(m, $"{tag} line_height", jb.GetProperty("line_height"), b.LineHeight);
                var recs = msg.GetProperty("records").EnumerateArray().ToArray();
                m.Equal($"{tag} record count", recs.Length, b.Records.Length);
                for (int i = 0; i < Math.Min(recs.Length, b.Records.Length); i++)
                {
                    var r = recs[i];
                    var g = b.Records[i];
                    long[] want =
                    [
                        r.GetProperty("index").GetInt32(), r.GetProperty("flags").GetInt32(), r.GetProperty("color").GetInt32(),
                        r.GetProperty("ext").GetInt32(), r.GetProperty("line").GetInt32(), r.GetProperty("tex").GetInt32(),
                        r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(), r.GetProperty("x2").GetInt32(),
                        r.GetProperty("y2").GetInt32(), r.GetProperty("ctr").GetInt32(), Bits(r.GetProperty("dx")),
                        Bits(r.GetProperty("dy")), Bits(r.GetProperty("sx")), Bits(r.GetProperty("sy")),
                    ];
                    long[] got =
                    [
                        g.Index, g.Flags, g.Color, g.Ext, g.Line, g.Tex, g.X, g.Y, g.X2, g.Y2, g.Ctr,
                        Bits(g.Dx), Bits(g.Dy), Bits(g.Sx), Bits(g.Sy),
                    ];
                    if (!want.AsSpan().SequenceEqual(got))
                    {
                        m.Add($"{tag} record {i}: expected [{string.Join(",", want)}], got [{string.Join(",", got)}]");
                    }
                }

                // the drawn quads at the page origin
                var origin = msg.GetProperty("origin");
                var jq = msg.GetProperty("quads");
                var quads = BlockDraw.BlockQuads(
                    b, origin[0].GetProperty("value").GetSingle(), origin[1].GetProperty("value").GetSingle(),
                    jq.GetProperty("draw_flags").GetInt32(), 1.0, jq.GetProperty("head").GetDouble(), jq.GetProperty("tail").GetDouble(),
                    lang: lang);
                var want2 = jq.GetProperty("list").EnumerateArray().ToArray();
                m.Equal($"{tag} quad count", want2.Length, quads.Count);
                for (int i = 0; i < Math.Min(want2.Length, quads.Count); i++)
                {
                    var w = want2[i];
                    var q = quads[i];
                    string kind = q.Kind switch { QuadKind.Shadow => "shadow", QuadKind.Outline => "outline", _ => "fill" };
                    bool ok = w.GetProperty("kind").GetString() == kind
                        && w.GetProperty("tex").GetInt32() == q.Tex
                        && w.GetProperty("u0").GetInt32() == q.U0 && w.GetProperty("v0").GetInt32() == q.V0
                        && w.GetProperty("u1").GetInt32() == q.U1 && w.GetProperty("v1").GetInt32() == q.V1
                        && Bits(w.GetProperty("x")) == Bits((float)q.X) && Bits(w.GetProperty("y")) == Bits((float)q.Y)
                        && w.GetProperty("x").GetProperty("value").GetDouble() == q.X
                        && w.GetProperty("y").GetProperty("value").GetDouble() == q.Y
                        && w.GetProperty("sx").GetProperty("value").GetDouble() == q.Sx
                        && w.GetProperty("sy").GetProperty("value").GetDouble() == q.Sy
                        && w.GetProperty("rgb")[0].GetInt32() == q.R && w.GetProperty("rgb")[1].GetInt32() == q.G
                        && w.GetProperty("rgb")[2].GetInt32() == q.B
                        && w.GetProperty("alpha").GetDouble() == q.Alpha
                        && w.GetProperty("record").GetInt32() == b.Records[q.Record].Index;     // the oracle names the element index
                    if (!ok)
                    {
                        m.Add($"{tag} quad {i}: expected {w}, got {q}");
                    }
                }

                checkedMessages++;
            }

            // how the messages of a page are stacked
            foreach (var page in doc.RootElement.GetProperty("pages").EnumerateArray())
            {
                int pageNo = page.GetProperty("page").GetInt32();
                var idx = Enumerable.Range(0, messages.Length).Where(i => messages[i].GetProperty("page").GetInt32() == pageNo)
                    .OrderBy(i => messages[i].GetProperty("j").GetInt32()).ToArray();
                int[] blank = [.. page.GetProperty("blank_before").EnumerateArray().Select(e => e.GetInt32())];
                var pos = MessageWindow.PagePositions(idx.Select(i => blocks[i]).ToArray(), lang, blank);
                var origins = page.GetProperty("origins").EnumerateArray().ToArray();
                m.Equal($"{name} page {pageNo} origin count", origins.Length, pos.Length);
                for (int i = 0; i < Math.Min(origins.Length, pos.Length); i++)
                {
                    m.Equal($"{name} page {pageNo} origin {i} x", origins[i][0].GetProperty("value").GetDouble(), pos[i].X);
                    m.Equal($"{name} page {pageNo} origin {i} y", origins[i][1].GetProperty("value").GetDouble(), pos[i].Y);
                }
            }
        }

        Assert.Equal(16, checkedMessages);
        m.AssertNone("slice page-1 layouts (ja, en)");
    }

    private static void CompareFloatList(Mismatches m, string what, JsonElement expected, float[] actual)
    {
        var list = expected.EnumerateArray().ToArray();
        for (int i = 0; i < list.Length; i++)
        {
            m.Equal($"{what}[{i}]", Bits(list[i]), Bits(actual[i]));
        }
    }
}
