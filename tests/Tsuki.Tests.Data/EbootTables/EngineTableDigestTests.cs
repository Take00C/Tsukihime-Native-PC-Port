using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tsuki.Data.Eboot;
using Tsuki.Data.Eboot.Tables;
using Tsuki.Tests.Data.Catalog;

namespace Tsuki.Tests.Data.EbootTables;

/// <summary>
/// The C# table readers equal the Python readers (tools/tsuki/engine_tables.py): both serialise every table to the same canonical bytes and the SHA-256
/// of those bytes is stored in <c>engine_tables_digests.json</c> (written by <c>python -m tsuki.engine_tables digests --write ...</c>). A digest is a
/// proof of equality, not game data.
/// </summary>
public sealed class EngineTableDigestTests
{
    // Not [CallerFilePath]: Release builds set ContinuousIntegrationBuild (Directory.Build.props), which maps source paths to /_/ and made this test fail
    // outside Debug; the repository root found from the test binary (GoldenData.RepoRoot) holds in every configuration.
    private static string DigestFile() =>
        Path.Combine(GoldenData.RepoRoot, "tests", "Tsuki.Tests.Data", "EbootTables", "engine_tables_digests.json");

    [GameDataFact]
    public void EveryTableEqualsThePythonReader()
    {
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(DigestFile()))!;
        Dictionary<string, byte[]> actual = Canonical(SharedEboot.Reader);
        Assert.Equal([.. expected.Keys.Order(StringComparer.Ordinal)], [.. actual.Keys.Order(StringComparer.Ordinal)]);
        foreach ((string name, byte[] bytes) in actual)
        {
            Assert.Equal(expected[name], Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }

    private sealed class Bytes
    {
        private readonly MemoryStream _ms = new();

        public void I32(int v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, v);
            _ms.Write(b);
        }

        public void Ints(params int[] values)
        {
            foreach (int v in values)
            {
                I32(v);
            }
        }

        public void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            _ms.Write(b);
        }

        public void U64(ulong v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, v);
            _ms.Write(b);
        }

        public void U16(ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            _ms.Write(b);
        }

        public void I16(int v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteInt16LittleEndian(b, (short)v);
            _ms.Write(b);
        }

        public void F32(float v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(b, v);
            _ms.Write(b);
        }

        public void Ascii0(string s)
        {
            _ms.Write(Encoding.ASCII.GetBytes(s));
            _ms.WriteByte(0);
        }

        public void Byte(byte v) => _ms.WriteByte(v);

        public void Cells(IEnumerable<UiCell> cells)
        {
            foreach (UiCell c in cells)
            {
                I16(c.A);
                I16(c.B);
                I16(c.C);
                I16(c.D);
            }
        }

        public byte[] ToArray() => _ms.ToArray();
    }

    private static Dictionary<string, byte[]> Canonical(EbootReader eboot)
    {
        Dictionary<string, byte[]> o = [];

        var haze = HazeTable.Read(eboot);
        var b = new Bytes();
        for (int k = 0; k < haze.Count; k++)
        {
            foreach (float f in haze.Raw(k).ToArray())
            {
                b.F32(f);
            }
        }

        o["haze"] = b.ToArray();

        b = new Bytes();
        foreach (int n in SceneNodeTable.Read(eboot).Nodes)
        {
            b.I32(n);
        }

        o["scene_node"] = b.ToArray();

        b = new Bytes();
        foreach ((int id, int value) in MotionLoopTable.Read(eboot).Loops.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)))
        {
            b.I32(id);
            b.I32(value);
        }

        o["motion_loops"] = b.ToArray();

        b = new Bytes();
        foreach ((int last, int word) in JumpClearTable.Read(eboot).Pairs)
        {
            b.I32(last);
            b.I32(word);
        }

        o["jump_clears"] = b.ToArray();

        foreach ((string tag, bool history) in new[] { ("flow_keep", false), ("save_keep", true) })
        {
            var keep = FlowKeepLists.Read(eboot, history);
            b = new Bytes();
            foreach (IReadOnlyList<int> list in new[] { keep.Words, keep.Bytes, keep.Bits })
            {
                b.I32(list.Count);
                foreach (int v in list)
                {
                    b.I32(v);
                }
            }

            o[tag] = b.ToArray();
        }

        var fcln = FclnLineTable.Read(eboot);
        b = new Bytes();
        for (int e = 0; e < FclnLineTable.Effects; e++)
        {
            for (int k = 0; k < FclnLineTable.KeysPerEffect; k++)
            {
                FclnKey key = fcln.Get(e, k);
                foreach (int v in new[] { key.TimeMs, key.CentreX, key.CentreY, key.RadiusMin, key.RadiusMax, key.CountMin, key.CountMax, key.Alpha, key.RerollMs })
                {
                    b.I32(v);
                }
            }
        }

        o["fcln_lines"] = b.ToArray();

        b = new Bytes();
        foreach (FclnNote n in FclnNoteTable.Read(eboot))
        {
            b.U16((ushort)n.Image);
            b.U16((ushort)n.Frames);
            for (int f = 0; f < 24; f++)
            {
                b.I16(f < n.Frames ? n.Sequence[f] : -1);
            }

            b.I16(n.Width);
            b.I16(n.Height);
            b.I16(n.Step);
            foreach (float v in new[] { n.Vx, n.Vy, n.JitterX, n.JitterY, n.Drift, n.Accel })
            {
                b.F32(v);
            }

            b.I32(n.Floor);
        }

        o["fcln_notes"] = b.ToArray();

        var voices = VoiceSpeakerTable.Read(eboot);
        b = new Bytes();
        foreach (string key in voices.Prefixes.Keys.Order(StringComparer.Ordinal))
        {
            b.Ascii0(key);
            b.I32(voices.Prefixes[key]);
        }

        o["voice_speakers"] = b.ToArray();
        o["speaker_row_bits"] = Ints(voices.RowBits);
        o["voice_row_words"] = Ints(voices.RowWords);

        b = new Bytes();
        foreach (string n in TestSeNames.Read(eboot))
        {
            b.Ascii0(n);
        }

        o["test_se"] = b.ToArray();
        o["dialog_messages"] = Ints(DialogMessageTable.Read(eboot).Ids.ToArray());

        var windows = MessageWindowTable.Read(eboot);
        b = new Bytes();
        foreach (WindowType w in windows.Types)
        {
            b.U32(w.Slot);
            b.F32(w.X);
            b.F32(w.Y);
        }

        o["window_types"] = b.ToArray();
        b = new Bytes();
        foreach (float f in windows.Dx.Concat(windows.Dy))
        {
            b.F32(f);
        }

        o["window_offsets"] = b.ToArray();
        b = new Bytes();
        foreach (float f in SoftClipRadii.Read(eboot))
        {
            b.F32(f);
        }

        o["soft_clip_radii"] = b.ToArray();

        o["save_title_other"] = Cells(UiCellTables.ReadSaveTitleCells(eboot, english: false));
        o["save_title_en"] = Cells(UiCellTables.ReadSaveTitleCells(eboot, english: true));
        o["phase_title_ja"] = Cells(UiCellTables.ReadPhaseTitleCells(eboot, PhaseTitleSet.Ja));
        o["phase_title_en"] = Cells(UiCellTables.ReadPhaseTitleCells(eboot, PhaseTitleSet.En));
        o["phase_title_zh"] = Cells(UiCellTables.ReadPhaseTitleCells(eboot, PhaseTitleSet.Zh));
        o["flow_title_other"] = Cells(UiCellTables.ReadFlowTitleCells(eboot, english: false));
        o["flow_title_en"] = Cells(UiCellTables.ReadFlowTitleCells(eboot, english: true));
        o["flow_line"] = Cells(UiCellTables.ReadFlowLineCells(eboot));
        o["voice_face"] = Cells(UiCellTables.ReadVoiceFaceCells(eboot));
        o["voice_name"] = Cells(UiCellTables.ReadVoiceNameCells(eboot, english: false));
        o["voice_name_en"] = Cells(UiCellTables.ReadVoiceNameCells(eboot, english: true));
        o["guide_icon"] = Cells(UiCellTables.ReadGuideIconCells(eboot));
        for (int lang = 0; lang < 5; lang++)
        {
            o[$"guide_word_{lang}"] = Cells(UiCellTables.ReadGuideWordCells(eboot, lang));
        }

        o["banner_rects"] = Cells(UiCellTables.ReadBannerCells(eboot));
        b = new Bytes();
        foreach ((float x, float y) in UiCellTables.ReadBannerKeys(eboot))
        {
            b.F32(x);
            b.F32(y);
        }

        o["banner_keys"] = b.ToArray();

        b = new Bytes();
        foreach (ConfigPage page in ConfigPageTable.Read(eboot))
        {
            b.I32(page.FirstItem);
            b.I32(page.LastItem);
            b.I32(page.Rows.Count);
            foreach (ConfigRow row in page.Rows)
            {
                b.I32(row.Controls.Count);
                foreach (ConfigControl c in row.Controls)
                {
                    b.I32(c.Item);
                    b.I32(c.Kind);
                    b.F32(c.X);
                    b.F32(c.Y);
                }

                b.I32(row.Current);
                b.I32(row.Maximum);
            }
        }

        o["config_pages"] = b.ToArray();

        b = new Bytes();
        foreach ((byte r, byte g, byte bl) in TextColourTable.Read(eboot))
        {
            b.Byte(r);
            b.Byte(g);
            b.Byte(bl);
        }

        o["text_colours"] = b.ToArray();

        b = new Bytes();
        foreach (ulong m in MotionFieldMasks.Read(eboot).Masks)
        {
            b.U64(m);
        }

        o["motion_masks"] = b.ToArray();

        var gallery = GalleryTables.Read(eboot);
        b = new Bytes();
        foreach (CgGroup g in gallery.Groups)
        {
            b.I32(g.Index);
            b.I32(g.Category);
            b.I32(g.Entries.Count);
            foreach (CgEntry e in g.Entries)
            {
                b.Ascii0(e.Name);
                b.Ascii0(e.Prefix.ToString());
                b.Ints(e.Thumbnail, e.X, e.Y, e.Width, e.Height, e.Flag);
            }
        }

        o["cg_entries"] = b.ToArray();
        b = new Bytes();
        foreach (CgAlias a in gallery.Aliases)
        {
            b.Ascii0(a.Key);
            b.I32(a.Aliases.Count);
            foreach (string n in a.Aliases)
            {
                b.Ascii0(n);
            }
        }

        o["cg_aliases"] = b.ToArray();
        b = new Bytes();
        foreach (string n in gallery.Special)
        {
            b.Ascii0(n);
        }

        o["cg_special"] = b.ToArray();
        b = new Bytes();
        foreach (SeenMapEntry m in gallery.SeenMap)
        {
            b.Ascii0(m.Name);
            b.I32(m.SeenIndex);
        }

        o["seen_map"] = b.ToArray();
        b = new Bytes();
        foreach (MusicTrack t in gallery.Music)
        {
            b.Ints(t.Bgm, t.TitleUi, t.VocalUi, t.LyricsUi, t.ComposerUi, t.ArrangerUi, t.LengthMs, t.LoopStart, t.LoopEnd, t.Variants.Count);
            foreach (int v in t.Variants)
            {
                b.I32(v);
            }
        }

        o["music"] = b.ToArray();
        b = new Bytes();
        foreach (ExtraScenario x in gallery.Extras)
        {
            b.Ints(x.ScriptScene, x.FlowScene, x.UnlockA, x.UnlockB, x.Message, x.MessageUi);
        }

        o["extras"] = b.ToArray();
        o["help_messages"] = Ints(gallery.HelpMessages);
        o["trophy_scenes"] = Ints(gallery.TrophyScenes);
        b = new Bytes();
        foreach ((int category, IReadOnlyList<int> flags) in gallery.CategoryFlags.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)))
        {
            b.I32(category);
            b.I32(flags.Count);
            foreach (int f in flags)
            {
                b.I32(f);
            }
        }

        o["category_flags"] = b.ToArray();

        var flow = FlowchartTables.Read(eboot);
        b = new Bytes();
        foreach (FlowColumn c in flow.Columns)
        {
            b.Ints(c.Index, c.Group, c.Nodes.Count);
            foreach (FlowNode n in c.Nodes)
            {
                b.Ints(n.Scene, n.Connector, n.Link, n.ScriptScene, n.ChapterLabel, n.Order, n.Thumbnail, n.TitleUi, n.SynopsisUi);
            }
        }

        o["flow_columns"] = b.ToArray();
        b = new Bytes();
        foreach (FlowEdge e in flow.Edges)
        {
            b.Ints(e.From, e.To, e.GlobalIds.Count);
            foreach (int v in e.GlobalIds)
            {
                b.I32(v);
            }

            b.I32(e.PlaythroughIds.Count);
            foreach (int v in e.PlaythroughIds)
            {
                b.I32(v);
            }
        }

        o["flow_edges"] = b.ToArray();
        b = new Bytes();
        foreach ((int x, int y, int z) in flow.ChoiceFlags)
        {
            b.Ints(x, y, z);
        }

        o["choice_flags"] = b.ToArray();
        return o;
    }

    private static byte[] Ints(IEnumerable<int> values)
    {
        var b = new Bytes();
        foreach (int v in values)
        {
            b.I32(v);
        }

        return b.ToArray();
    }

    private static byte[] Cells(IEnumerable<UiCell> cells)
    {
        var b = new Bytes();
        b.Cells(cells);
        return b.ToArray();
    }
}
