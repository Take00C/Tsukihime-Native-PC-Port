using System.Buffers.Binary;
using Tsuki.Data.Containers;

namespace Tsuki.Tests.Data;

/// <summary>
/// mrgd00 containers against the oracle: the 891 sections of the 8 standalone files, NAM32 sections, the
/// script_text offset tables, and the section tables of every container in the image archives (35,375 of them,
/// 1,126,424 section records, one nesting level).
/// </summary>
public sealed class StandaloneAndContainerTests
{
    public static TheoryData<string> StandaloneFiles => new(GameImage.Standalone);

    public static TheoryData<string> ImageArchives => new(GameImage.ImageTriplets);

    [GameDataTheory]
    [MemberData(nameof(StandaloneFiles))]
    public void StandaloneSectionsEqualTheOracle(string file)
    {
        var fileRow = GoldenData.Tsv("standalone_files.tsv").Single(r => r["file"] == file);
        var sectionRows = GoldenData.Tsv("standalone_sections.tsv").Where(r => r["file"] == file).ToList();
        using var mrg = StandaloneMrg.Open(GameImage.Path(file));
        var c = mrg.Container;
        var m = new Mismatches();
        m.Equal("bytes", fileRow.Long("bytes"), mrg.Length);
        m.Equal("count", fileRow.Int("count"), c.Count);
        m.Equal("data_start", fileRow.Int("data_start"), c.DataStart);
        m.Equal("end", fileRow.Long("end"), c.End);
        m.Equal("packed_size", fileRow.Long("packed_size"), c.PackedSize);
        m.Equal("packed size == file size", mrg.Length, c.PackedSize);
        m.Equal("validate issues", fileRow.Int("validate_issues"), c.Validate().Count);
        Assert.Equal(sectionRows.Count, c.Count);
        for (int i = 0; i < c.Count; i++)
        {
            var s = c.Sections[i];
            var r = sectionRows[i];
            m.Equal($"{file}[{i}] section", r.Int("section"), s.Index);
            m.Equal($"{file}[{i}] sector_offset", r.Int("sector_offset"), s.SectorOffset);
            m.Equal($"{file}[{i}] byte_offset", r.Int("byte_offset"), s.ByteOffset);
            m.Equal($"{file}[{i}] size_sectors", r.Int("size_sectors"), s.SizeSectors);
            m.Equal($"{file}[{i}] size_lo", r.Int("size_lo"), s.SizeLo);
            m.Equal($"{file}[{i}] offset", r.Long("offset"), s.Offset);
            m.Equal($"{file}[{i}] size", r.Int("size"), s.Size);
            m.Equal($"{file}[{i}] nested", r.Flag("nested"), c.IsNested(i));
            m.Equal($"{file}[{i}] head", r["head8"], Convert.ToHexStringLower(c.Head(i, 8)));
        }

        m.AssertNone(file);
    }

    [GameDataFact]
    public void TheEightStandaloneFilesHold891Sections()
    {
        int total = 0;
        foreach (string file in GameImage.Standalone)
        {
            using var mrg = StandaloneMrg.Open(GameImage.Path(file));
            total += mrg.Container.Count;
        }

        Assert.Equal(891, total);
    }

    [GameDataFact]
    public void StandaloneContainersAndTheirNestedOnesEqualTheOracle()
    {
        var rows = GoldenData.Tsv("standalone_containers.tsv");
        var m = new Mismatches();
        int checkedCount = 0;
        foreach (string file in GameImage.Standalone)
        {
            using var mrg = StandaloneMrg.Open(GameImage.Path(file));
            foreach (var r in rows.Where(r => r["source"] == file))
            {
                var c = mrg.Container;
                string path = r["path"];
                if (path.Length > 0)
                {
                    foreach (string part in path.Split('/'))
                    {
                        c = c.Child(int.Parse(part));
                    }
                }

                Compare(m, $"{file}:{path}", r, c);
                checkedCount++;
            }
        }

        m.AssertNone("standalone containers");
        Assert.Equal(rows.Count, checkedCount);
        Assert.Equal(335, rows.Count);
    }

    [GameDataFact]
    public void Nam32SectionsEqualTheOracle()
    {
        var tables = GoldenData.Tsv("nam_sections.tsv");
        var names = GoldenData.Tsv("nam_names.tsv");
        var m = new Mismatches();
        foreach (var t in tables)
        {
            string file = t["file"];
            int section = t.Int("section");
            using var mrg = StandaloneMrg.Open(GameImage.Path(file));
            var table = NamTable.ParseNam32(mrg.Container.Section(section).Span);
            string id = $"{file}[{section}]";
            m.Equal(id + " record size", t.Int("record_size"), table.RecordSize);
            m.Equal(id + " count", t.Int("count"), table.Count);
            m.Equal(id + " terminator", t.Flag("terminator"), table.Terminator);
            m.Equal(id + " trailing", t["trailing_hex"], Convert.ToHexStringLower(table.Trailing.Span));
            var expected = names.Where(r => r["file"] == file && r.Int("section") == section).ToList();
            m.Equal(id + " name rows", expected.Count, table.Count);
            for (int i = 0; i < Math.Min(expected.Count, table.Count); i++)
            {
                m.Equal($"{id} name {i}", expected[i]["name"], table.Names[i]);
                m.Equal($"{id} tag {i}", expected[i]["tag"], table.Tags[i]);
            }
        }

        m.AssertNone("NAM32 sections");
        Assert.Equal(5, tables.Count);
        Assert.Equal(549, tables.Single(t => t["file"] == "allscr.mrg").Int("count"));
    }

    [GameDataFact]
    public void ScriptTextOffsetTablesEqualTheOracle()
    {
        var rows = GoldenData.Tsv("scripttext.tsv");
        using var mrg = StandaloneMrg.Open(GameImage.Path("script_text.mrg"));
        Assert.Equal(5, rows.Count);
        var m = new Mismatches();
        for (int k = 0; k < rows.Count; k++)
        {
            var r = rows[k];
            var table = mrg.Container.Section(2 * k).Span;
            int entries = table.Length / 4;
            int end = -1;
            for (int i = 0; i < entries; i++)
            {
                if (BinaryPrimitives.ReadUInt32BigEndian(table[(i * 4)..]) == 0xFFFFFFFFu)
                {
                    end = i;
                    break;
                }
            }

            m.Equal(r["lang"] + " table entries", r.Int("table_entries"), entries);
            m.Equal(r["lang"] + " lines", r.Int("count"), end - 2);
            m.Equal(r["lang"] + " end marks", r.Int("end_marks"), entries - end);
            m.Equal(r["lang"] + " blob bytes", r.Int("blob_bytes"), mrg.Container.Sections[(2 * k) + 1].Size);
            m.Equal(r["lang"] + " table bytes", r.Int("table_bytes"), table.Length);
            m.Equal(r["lang"] + " blob size guard", (uint)mrg.Container.Sections[(2 * k) + 1].Size, BinaryPrimitives.ReadUInt32BigEndian(table[((end - 1) * 4)..]));
        }

        m.AssertNone("script_text.mrg");
    }

    [GameDataTheory]
    [MemberData(nameof(ImageArchives))]
    public void EveryContainerOfAnImageArchiveEqualsTheOracle(string name)
    {
        // containers.tsv: every HED entry as a container plus every nested one (path "3", "3/7").
        var all = GoldenData.Tsv("containers.tsv").Where(r => r["source"] == name).ToList();
        var byEntry = all.GroupBy(r => r.Int("entry")).ToDictionary(g => g.Key, g => g.ToList());
        using var archive = GameImage.Open(name);
        var m = new Mismatches();
        int containers = 0;
        long sections = 0;
        Parallel.For(0, archive.Count, e =>
        {
            var top = archive.OpenContainer(e);
            var entry = archive.Entries[e];
            if (top.PackedSize != entry.Size)
            {
                m.Add($"{name}#{e}: packed size {top.PackedSize} != HED size {entry.Size}");
            }

            var expected = byEntry[e];
            int seen = 0;
            Walk(top, string.Empty);
            if (seen != expected.Count)
            {
                m.Add($"{name}#{e}: {seen} containers, oracle has {expected.Count}");
            }

            Interlocked.Add(ref containers, seen);
            void Walk(MzpContainer c, string path)
            {
                var row = expected.FirstOrDefault(r => r["path"] == path);
                if (row is null)
                {
                    m.Add($"{name}#{e}/{path}: container not in the oracle's list");
                }
                else
                {
                    Compare(m, $"{name}#{e}/{path}", row, c);
                }

                seen++;
                Interlocked.Add(ref sections, c.Count);
                for (int i = 0; i < c.Count; i++)
                {
                    if (c.IsNested(i))
                    {
                        Walk(c.Child(i), path.Length == 0 ? i.ToString() : path + "/" + i);
                    }
                }
            }
        });

        m.AssertNone(name);
        Assert.Equal(all.Count, containers);
        Assert.Equal(all.Sum(r => (long)r.Int("count")), sections);
    }

    [GameDataFact]
    public void TheImageArchivesHold35375ContainersWithOneLevelOfNesting()
    {
        var rows = GoldenData.Tsv("containers.tsv");
        Assert.Equal(35_375, rows.Count);
        Assert.Equal(1_126_424L, rows.Sum(r => (long)r.Int("count")));
        // exactly one level of nesting (containers.md 4.4): a path has at most one slash-free component
        Assert.DoesNotContain(rows, r => r["path"].Contains('/'));
    }

    [GameDataTheory]
    [InlineData("allpac2")]
    [InlineData("allpac3")]
    [InlineData("allpac4")]
    [InlineData("parts")]
    public void ContainersFollowThePackingRule(string name)
    {
        // containers.md 4.3: size_sectors rule, gapless layout, 0xFF padding. 0 deviations on retail data.
        using var archive = GameImage.Open(name);
        var m = new Mismatches();
        for (int e = 0; e < archive.Count; e += Math.Max(1, archive.Count / 400))
        {
            var top = archive.OpenContainer(e);
            foreach (string issue in top.Validate())
            {
                m.Add($"{name}#{e}: {issue}");
            }
        }

        m.AssertNone(name);
    }

    private static void Compare(Mismatches m, string id, TsvRow row, MzpContainer c)
    {
        m.Equal(id + " count", row.Int("count"), c.Count);
        m.Equal(id + " data_start", row.Int("data_start"), c.DataStart);
        m.Equal(id + " end", row.Long("end"), c.End);
        m.Equal(id + " packed_size", row.Long("packed_size"), c.PackedSize);
        m.Equal(id + " digest", row["digest"], c.SectionDigest());
    }
}
