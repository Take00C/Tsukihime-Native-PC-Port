using Tsuki.Data.Eboot.Tables;
using Tsuki.Tests.Data.Catalog;

namespace Tsuki.Tests.Data.EbootTables;

/// <summary>
/// The knife table readers on the user's eboot.bin: counts and spot values taken from the research (docs/research/knife_tables.md). The full
/// equality with the Python readers is in <see cref="EngineTableDigestTests"/>.
/// </summary>
public sealed class EngineTableTests
{
    [GameDataFact]
    public void HazeTableHoldsTheStraEntries()
    {
        var haze = HazeTable.Read(SharedEboot.Reader);
        Assert.Equal(558, haze.Count);
        Assert.True(haze.Raw(0).ToArray().All(v => v == 0f));
        Assert.Equal(new float[] { 1, 1, 1, 1, 1, 1, 2, 1, 1, 0 }, haze.Raw(1).ToArray());
        Assert.True(haze.TryGetEntry(557, out var last) && last.Length == 10);
        Assert.False(haze.TryGetEntry(0, out _));
        Assert.False(haze.TryGetEntry(558, out _));
    }

    [GameDataFact]
    public void MotionLoopsComeOutOfTheCompiledSwitch()
    {
        var loops = MotionLoopTable.Read(SharedEboot.Reader);
        Assert.Equal(2027, loops.Loops.Count);
        Assert.Equal(1, loops.LoopPoint(73));
        Assert.Equal(1, loops.LoopPoint(74));
        Assert.Equal(600, loops.LoopPoint(1693));
        Assert.Equal(4700, loops.LoopPoint(1761));
        Assert.Equal(0, loops.LoopPoint(72));
        Assert.Equal(0, loops.LoopPoint(99999));
    }

    [GameDataFact]
    public void JumpClearsAndSceneNodes()
    {
        var clears = JumpClearTable.Read(SharedEboot.Reader);
        Assert.Equal(19, clears.Pairs.Count);
        Assert.Contains((203, 69), clears.Pairs);
        Assert.Contains((160, 62), clears.Pairs);
        Assert.Contains((10, 54), clears.Pairs);
        Assert.Empty(JumpClearTable.WordsClearedAtRow(SharedEboot.Reader, 204));
        Assert.Equal([69], JumpClearTable.WordsClearedAtRow(SharedEboot.Reader, 203));
        Assert.DoesNotContain(62, JumpClearTable.WordsClearedAtRow(SharedEboot.Reader, 161));
        Assert.Contains(62, JumpClearTable.WordsClearedAtRow(SharedEboot.Reader, 160));

        var nodes = SceneNodeTable.Read(SharedEboot.Reader);
        Assert.Equal(new[] { 486, 485, -1, 1 }, nodes.Nodes[..4].ToArray());
        Assert.Equal(-1, nodes.NodeOfScriptIndex(0));
        Assert.Equal(486, nodes.NodeOfScriptIndex(1));
        Assert.Equal(-1, nodes.NodeOfScriptIndex(10_000));
    }

    [GameDataFact]
    public void KeepListsAgreeBetweenTheTwoCopies()
    {
        var flow = FlowKeepLists.Read(SharedEboot.Reader);
        var history = FlowKeepLists.Read(SharedEboot.Reader, history: true);
        Assert.Equal(flow.Words, history.Words);
        Assert.Equal(flow.Bytes, history.Bytes);
        Assert.Equal(flow.Bits, history.Bits);
        Assert.Equal((39, 276, 43), (flow.Words.Count, flow.Bytes.Count, flow.Bits.Count));
        Assert.Equal(Enumerable.Range(0, 9), flow.Words.Take(9));
        Assert.Contains(967, flow.Words);
        Assert.Contains(500, flow.Bits);
        Assert.Contains(501, flow.Bytes);
    }

    [GameDataFact]
    public void SmallTables()
    {
        var eboot = SharedEboot.Reader;
        var names = TestSeNames.Read(eboot);
        Assert.Equal(14, names.Count);
        Assert.Equal("moon_se02009", names[0]);
        Assert.Equal("moon_se09005", names[^1]);

        var voices = VoiceSpeakerTable.Read(eboot);
        Assert.Equal(1, voices.SpeakerOf("A00_0001"));
        Assert.Equal(13, voices.SpeakerOf("b90_0002"));
        Assert.Equal(23, voices.SpeakerOf("M12"));
        Assert.Equal(0, voices.SpeakerOf("D40_0001"));
        Assert.Equal(0, voices.SpeakerOf("ab"));
        Assert.Equal([262144, 1, 2, 4, 512, 65536], voices.RowBits.Take(6));
        Assert.Equal([11, 12, 13, 14, 15, 16, 27], voices.RowWords.Take(7));

        var dialogs = DialogMessageTable.Read(eboot);
        Assert.Equal(new[] { 179, 178, 180, 199 }, dialogs.Ids[..4].ToArray());
        Assert.Equal(-1, dialogs.Ids[13]);
        Assert.Equal(182, dialogs.Ids[23]);
        Assert.Equal([216, 218, 1498], new[] { DialogMessageTable.Ps4SysmesId(216), DialogMessageTable.Ps4SysmesId(217), DialogMessageTable.Ps4SysmesId(1497) });

        var windows = MessageWindowTable.Read(eboot);
        Assert.Equal(new WindowType(22, 0f, 0f), windows.Types[0]);
        Assert.Equal((525f, 414f), (windows.Types[3].X, windows.Types[3].Y));
        Assert.Equal((27, 37, 100f, 100f), (windows.Dx.Count, windows.Dy.Count, windows.Dx[1], windows.Dy[2]));
        Assert.Equal([10f, 32f, 40f, 64f, 128f, 256f], SoftClipRadii.Read(eboot));
        Assert.Equal((255, 0, 0), TextColourTable.Read(eboot)[0]);
    }

    [GameDataFact]
    public void FclnLinesAndFieldMasks()
    {
        var lines = FclnLineTable.Read(SharedEboot.Reader);
        Assert.Equal(0, lines.Get(0, 0).TimeMs);
        Assert.Equal(640, lines.Get(0, 0).CentreX);
        Assert.Equal(-1, lines.Get(0, 1).TimeMs);
        Assert.Throws<ArgumentOutOfRangeException>(() => lines.Get(30, 0));

        var masks = MotionFieldMasks.Read(SharedEboot.Reader);
        Assert.Equal(24193, masks.Masks.Length);
        Assert.Equal(0x8804001UL, masks.MaskOf(1));
        Assert.Equal(0UL, masks.MaskOf(0));
        Assert.Equal(0UL, masks.MaskOf(24193));
    }

    [GameDataFact]
    public void TheTableSetBuildsEachTableOnceAndSharesIt()
    {
        var tables = new EngineTables(SharedEboot.Reader);
        Assert.Same(tables.Haze, tables.Haze);
        Assert.Same(tables.MotionLoops, tables.MotionLoops);
        Assert.Same(tables.Gallery, tables.Gallery);
        Assert.Equal(558, tables.Haze.Count);
        Assert.Equal(2027, tables.MotionLoops.Loops.Count);
        Assert.Equal(1286, tables.Flowchart.Nodes.Count());
        Assert.Equal(tables.FlowKeep.Words, tables.HistoryKeep.Words);
        Assert.Equal(3, tables.FclnNotes.Count);
        Assert.Equal(14, tables.TestSes.Count);
        Assert.Equal(2, tables.ConfigPages.Count);
        Assert.Same(SharedEboot.Reader, tables.Reader);
    }

    [GameDataFact]
    public void FallingNotesFollowTheFclnRecords()
    {
        var notes = FclnNoteTable.Read(SharedEboot.Reader);
        Assert.Equal([24, 20, 16], notes.Select(n => n.Frames));
        Assert.Equal([(563, 187), (225, 112), (225, 150)], notes.Select(n => (n.Width, n.Height)));
        Assert.All(notes, n => Assert.Equal((4, 450, -0.4f, 4.0f), (n.Step, n.Floor, n.Vx, n.Vy)));
        Assert.Equal(new[] { 0.001f, 0.0002f, 0.0015f }, notes.Select(n => n.Accel).ToArray());
        Assert.Equal(24, notes[0].Sequence.Count);
    }

    [GameDataFact]
    public void GalleryAndFlowchartTables()
    {
        var gallery = GalleryTables.Read(SharedEboot.Reader);
        Assert.Equal(458, gallery.Groups.Count);
        Assert.Equal(1972, gallery.Entries.Count());
        Assert.Equal(105, gallery.Aliases.Count);
        Assert.Equal(11, gallery.Special.Count);
        Assert.Equal(2144, gallery.SeenMap.Count);
        Assert.Equal(95, gallery.Music.Count);
        Assert.Equal(25, gallery.Extras.Count);
        Assert.Equal(19, gallery.CategoryFlags.Count);
        Assert.Equal([0x113, 0x114], gallery.CategoryFlags[15]);
        Assert.Equal("IMG1685.MZP", gallery.Groups[0].Entries[0].Name);
        Assert.Equal(214901, gallery.Music[0].LengthMs);
        Assert.Equal((1011, 544), (gallery.Extras[0].ScriptScene, gallery.Extras[0].FlowScene));

        var flow = FlowchartTables.Read(SharedEboot.Reader);
        Assert.Equal(277, flow.Columns.Count);
        Assert.Equal(1286, flow.Nodes.Count());
        Assert.Equal(630, flow.Edges.Count);
        Assert.Equal(83, flow.ChoiceFlags.Count);
        FlowNode first = flow.Columns[0].Nodes[0];
        Assert.Equal((485, 260, 385), (first.Scene, first.ScriptScene, first.Order));
        Assert.Equal(DialogMessageTable.Ps4SysmesId(397), first.TitleUi);
    }

    [GameDataFact]
    public void CellTablesAndConfigPages()
    {
        var eboot = SharedEboot.Reader;
        Assert.Equal(new UiCell(1192, 696, 1760, 776), UiCellTables.ReadPhaseTitleCells(eboot, PhaseTitleSet.Ja)[1]);
        Assert.Equal(new UiCell(64, 112, 504, 152), UiCellTables.ReadSaveTitleCells(eboot, english: true)[1]);
        Assert.Equal(23, UiCellTables.ReadGuideIconCells(eboot).Count);
        Assert.Equal(new UiCell(10, 10, 57, 38), UiCellTables.ReadGuideWordCells(eboot, 1)[0]);
        Assert.Equal(new UiCell(8, 72, 424, 128), UiCellTables.ReadBannerCells(eboot)[0]);
        Assert.Equal(10, UiCellTables.ReadBannerKeys(eboot).Count);
        Assert.Equal(30, UiCellTables.ReadFlowTitleCells(eboot, english: false).Count);
        Assert.Equal(new UiCell(0, 0, 0, 0), UiCellTables.ReadFlowTitleCells(eboot, english: false)[29]);

        var pages = ConfigPageTable.Read(eboot);
        Assert.Equal([(52, 78, 16), (79, 190, 26)], pages.Select(p => (p.FirstItem, p.LastItem, p.Rows.Count)));
        ConfigControl first = pages[0].Rows[0].Controls[0];
        Assert.Equal(52, first.Item);
        Assert.Equal(245f, first.X);
        Assert.Equal(100.8f, first.Y, 3f);
        Assert.Equal(ConfigPageTable.UnusedPosition, pages[1].Rows[7].Controls[0].X);
    }
}
