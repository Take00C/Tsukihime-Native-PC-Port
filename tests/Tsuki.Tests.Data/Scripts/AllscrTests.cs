using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tsuki.Data.Containers;
using Tsuki.Data.IO;
using Tsuki.Data.Scripts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Vm;
using Tsuki.Tests.Data.Text;

namespace Tsuki.Tests.Data.Scripts;

/// <summary>
/// allscr.mrg against the oracle (<c>tsuki.script.AllScr</c>, run by <c>script_tables_oracle.py allscr</c>): the 549 names, label tables and
/// decoded scripts, and against the exporter's per-script hashes (<c>samples/golden/script/hashes</c>).
/// </summary>
public sealed class AllscrTests
{
    private const string Oracle = "tests/Tsuki.Tests.Data/Scripts/script_tables_oracle.py";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private static readonly Lazy<AllscrArchive> Shared = new(() => AllscrArchive.Open(new DirectoryGameFiles(GameFolder.Require())));

    [GameDataFact]
    public void TheLayoutHas549ScriptsAndTheirNamesEqualTheOracle()
    {
        AllscrArchive a = Shared.Value;
        using JsonDocument layout = OracleRun.One(Oracle, "allscr", "layout", Timeout);
        JsonElement l = layout.RootElement;
        Assert.Equal(549, a.Count);
        Assert.Equal(l.GetProperty("scripts").GetInt32(), a.Count);
        Assert.Equal(552, l.GetProperty("sections").GetInt32());
        Assert.Equal(l.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToArray(), a.Names.ToArray());
        Assert.Equal("START_SCRIPT", a.Names[0]);
        Assert.Equal("JUMPQA", a.Names[548]);
        Assert.True(a.NameTable.Terminator);
    }

    [GameDataFact]
    public void EveryDecodedScriptAndEveryLabelTableEqualTheOracle()
    {
        AllscrArchive a = Shared.Value;
        var mismatches = new Mismatches();
        int scripts = 0;
        long bytes = 0;
        int labelsTotal = 0;
        foreach (JsonDocument doc in OracleRun.Records(Oracle, "allscr", "script", Timeout))
        {
            using (doc)
            {
                JsonElement e = doc.RootElement;
                int i = e.GetProperty("i").GetInt32();
                ReadOnlyMemory<byte> script = a.ScriptBytes(i);
                mismatches.Equal($"script {i} length", e.GetProperty("len").GetInt32(), script.Length);
                mismatches.Equal($"script {i} sha256", e.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(script.Span)));
                ReadOnlyMemory<byte> records = a.LabelRecords(i);
                mismatches.Equal($"script {i} label bytes", e.GetProperty("labels_len").GetInt32(), records.Length);
                mismatches.Equal($"script {i} label sha256", e.GetProperty("labels_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(records.Span)));
                IReadOnlyList<ScriptLabel> labels = a.Labels(i);
                var theirs = e.GetProperty("labels").EnumerateArray().ToList();
                mismatches.Equal($"script {i} label count", theirs.Count, labels.Count);
                for (int k = 0; k < Math.Min(theirs.Count, labels.Count); k++)
                {
                    mismatches.Equal($"script {i} label {k}", $"{theirs[k][0].GetString()}|{theirs[k][1].GetInt32()}|{theirs[k][2].GetInt32()}", $"{labels[k].Name}|{labels[k].Block}|{labels[k].Offset}");
                }

                // the engine's lookup (C's LabelTable) agrees with the parsed records for the first record of each name
                LabelTable lookup = a.LabelLookup(i);
                mismatches.Equal($"script {i} lookup count", labels.Count, lookup.Count);
                foreach (ScriptLabel label in labels.GroupBy(l => l.Name).Select(g => g.First()))
                {
                    mismatches.Equal($"script {i} lookup {label.Name}", label.Offset, lookup.Find(label.Name));
                }

                scripts++;
                bytes += script.Length;
                labelsTotal += labels.Count;
            }
        }

        mismatches.AssertNone("allscr");
        Assert.Equal(549, scripts);
        Assert.Equal(19_617_273, bytes);          // script.md 1
        Assert.Equal(3_654, labelsTotal);         // script.md 2.4
    }

    [GameDataFact]
    public void TheDecodedScriptsAndSegmentCountsEqualTheExportersHashes()
    {
        AllscrArchive a = Shared.Value;
        string path = Path.Combine(GoldenData.RepoRoot, "samples", "golden", "script", "hashes", "script_hashes.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement scripts = doc.RootElement.GetProperty("scripts");
        Assert.Equal(549, scripts.GetArrayLength());
        var mismatches = new Mismatches();
        long segments = 0;
        foreach (JsonElement s in scripts.EnumerateArray())
        {
            int i = s.GetProperty("script").GetInt32();
            mismatches.Equal($"script {i} name", s.GetProperty("name").GetString(), a.Names[i]);
            Tsuki.Engine.Contracts.ScriptImage image = a.Script(i);
            mismatches.Equal($"script {i} bytes", s.GetProperty("bytes").GetInt32(), image.Bytes.Length);
            mismatches.Equal($"script {i} raw sha256", s.GetProperty("raw_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(image.Bytes.Span)));
            mismatches.Equal($"script {i} segments", s.GetProperty("segments").GetInt32(), image.SegmentStarts.Length);
            segments += image.SegmentStarts.Length;
        }

        mismatches.AssertNone("script hashes");
        Assert.Equal(1_083_168, segments);
    }

    [GameDataFact]
    public void OpeningReadsTheNamesAndLabelsOnlyAndAScriptReadsItsOwnSection()
    {
        var files = new CountingFiles(new DirectoryGameFiles(GameFolder.Require()));
        AllscrArchive a = AllscrArchive.Open(files, "image/allscr.mrg");
        long length = files.Length("image/allscr.mrg");
        Assert.True(files.BytesRead < 200_000, $"opening read {files.BytesRead} of {length} bytes");
        long before = files.BytesRead;
        ReadOnlyMemory<byte> s = a.ScriptBytes(0);
        Assert.Equal(1173, s.Length);                              // START_SCRIPT, script.md 2.1: a 0x186-byte MZX0 section
        Assert.Equal(0x186, files.BytesRead - before);
        _ = a.LabelRecords(5);                                     // label tables were read when the file was opened
        Assert.Equal(0x186, files.BytesRead - before);
    }

    [GameDataFact]
    public void ScriptZeroIsTheStartScriptAndStartsWithItsFirstMarker()
    {
        AllscrArchive a = Shared.Value;
        ReadOnlySpan<byte> s = a.ScriptBytes(0).Span;
        Assert.True(s.StartsWith("_ZZ00a01();_ZZ00b01(START);"u8), Encoding.ASCII.GetString(s[..40]));
        Assert.Equal("START", a.Labels(0)[0].Name);
        Assert.Equal(0x00b, a.Labels(0)[0].Block);
        Assert.Equal(0x0000b, a.Labels(0)[0].Offset);
        Assert.Equal(0x0000b, a.LabelLookup(0).Find("START"));
    }

    [GameDataFact]
    public void IndicesOutsideTheArchiveAreRefused()
    {
        AllscrArchive a = Shared.Value;
        Assert.Throws<ArgumentOutOfRangeException>(() => a.ScriptBytes(549));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.LabelRecords(-1));
    }

    // -- no game data ---------------------------------------------------------------------------------------------------

    private static byte[] Nam32(params string[] names)
    {
        var b = new List<byte>();
        foreach (string n in names)
        {
            byte[] rec = new byte[32];
            Encoding.ASCII.GetBytes(n).CopyTo(rec, 0);
            rec[30] = (byte)'\r';
            rec[31] = (byte)'\n';
            b.AddRange(rec);
        }

        b.AddRange(new byte[32]);
        return [.. b];
    }

    private static byte[] LabelRecord(string name, int block, int offset) =>
        Encoding.ASCII.GetBytes(name.PadRight(21) + block.ToString("x3") + "," + offset.ToString("x5") + "\r\n");

    private static byte[] ScriptStream(string text)
    {
        byte[] raw = Encoding.ASCII.GetBytes(text);
        if (raw.Length % 2 != 0)
        {
            raw = [.. raw, (byte)' '];
        }

        return MiniMzx.Literal([.. raw.Select(c => (byte)(c ^ 0xFF))], raw.Length);
    }

    private static byte[] SyntheticArchive(string[] names, string[] scripts, byte[][] labelTables)
    {
        var sections = new List<byte[]>
        {
            Nam32(names),
            MiniMzp.Build(labelTables),
            new byte[16],
        };
        sections.AddRange(scripts.Select(ScriptStream));
        return MiniMzp.Build(sections);
    }

    [Fact]
    public void ASyntheticArchiveDecodesScriptsAndLabels()
    {
        byte[] file = SyntheticArchive(
            ["START_SCRIPT", "00_00"],
            ["_ZZ00a01();_ZZ00b01(START);_WTTM(1);_END_();", "_ZZ00a01();_ZZ00b01(START);_ZZ00c02(LBL_000);_END_()"],
            [LabelRecord("START", 0x00b, 0x0000b), [.. LabelRecord("START", 0x00b, 0x0000b), .. LabelRecord("LBL_000", 0x00c, 0x00020)]]);
        AllscrArchive a = AllscrArchive.FromMemory(file);
        Assert.Equal(2, a.Count);
        Assert.Equal(["START_SCRIPT", "00_00"], a.Names);
        Assert.Equal("_ZZ00a01();_ZZ00b01(START);_WTTM(1);_END_();", Encoding.ASCII.GetString(a.ScriptBytes(0).Span));
        Assert.Equal(4, a.Script(0).SegmentStarts.Length);
        IReadOnlyList<ScriptLabel> labels = a.Labels(1);
        Assert.Equal([new ScriptLabel("START", 0x00b, 0x0000b), new ScriptLabel("LBL_000", 0x00c, 0x00020)], labels);
        Assert.Equal(0x20, a.LabelLookup(1).Find("LBL_000"));
        Assert.Equal(0x0b, a.LabelLookup(1).Find("START"));
    }

    [Fact]
    public void ALayoutThatDoesNotLineUpIsRefused()
    {
        byte[] file = SyntheticArchive(["A", "B"], ["_END_();"], [LabelRecord("X", 1, 1), LabelRecord("X", 1, 1)]);
        ContainerFormatException e = Assert.Throws<ContainerFormatException>(() => AllscrArchive.FromMemory(file));
        Assert.Contains("do not line up", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LabelRecordsAreCheckedAgainstTheLayout()
    {
        Assert.Empty(AllscrArchive.ParseLabels([]));
        Assert.Throws<ContainerFormatException>(() => AllscrArchive.ParseLabels(new byte[31]));
        byte[] bad = LabelRecord("X", 1, 1);
        bad[24] = (byte)';';
        Assert.Throws<ContainerFormatException>(() => AllscrArchive.ParseLabels(bad));
        byte[] badHex = LabelRecord("X", 1, 1);
        badHex[26] = (byte)'g';
        Assert.Throws<ContainerFormatException>(() => AllscrArchive.ParseLabels(badHex));
        byte[] upper = LabelRecord("Y", 0, 0);
        upper[21] = (byte)'A';
        Assert.Equal(0xA00, AllscrArchive.ParseLabels(upper)[0].Block);   // Python's int(x, 16) accepts upper case
    }
}

/// <summary>A literal-only MZX0 stream (containers.md 5), enough to store a script for tests.</summary>
internal static class MiniMzx
{
    public static byte[] Literal(byte[] raw, int declaredSize)
    {
        var s = new List<byte>();
        s.AddRange("MZX0"u8.ToArray());
        s.AddRange(BitConverter.GetBytes((uint)declaredSize));
        for (int i = 0; i < raw.Length; i += 128)
        {
            int words = Math.Min(64, (raw.Length - i) / 2);
            s.Add((byte)(3 | ((words - 1) << 2)));
            s.AddRange(raw.AsSpan(i, 2 * words).ToArray());
        }

        return [.. s];
    }
}
