using System.Buffers.Binary;
using Tsuki.Data.Compression;
using Tsuki.Data.Containers;
using Tsuki.Data.IO;
using Tsuki.Data.Verification;

namespace Tsuki.Tests.Synthetic.Data;

/// <summary>The LV voice wrapper, the file views and the build check on folders that are not the game (containers.md 6). No game data.</summary>
public sealed class LvAndBuildCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-synth-" + Guid.NewGuid().ToString("N"));

    public LvAndBuildCheckTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- LV ----------------------------------------------------------------------------------

    private static byte[] MzxLiteral(byte[] raw)
    {
        // a valid literal-only stream (what compress_literal in the oracle writes)
        var model = new MzxModel();
        var words = new List<ushort>();
        for (int i = 0; i < raw.Length; i += 2)
        {
            words.Add((ushort)(raw[i] | (raw[i + 1] << 8)));
            if (words.Count == 64)
            {
                model.Literal([.. words]);
                words.Clear();
            }
        }

        if (words.Count > 0)
        {
            model.Literal([.. words]);
        }

        return model.Stream(raw.Length);
    }

    [Fact]
    public void LvHeaderIsParsedLikeTheEngine()
    {
        byte[] entry = [(byte)'L', (byte)'V', 3, 0, 0, 9, 0, (byte)'M', 1, 2];
        var h = Lv.Parse(entry);
        Assert.True(h.Present);
        Assert.Equal(3, h.Length);
        Assert.Equal(7, h.BodyOffset);
        Assert.Equal(7, h.EngineCopyLength);

        Assert.False(Lv.Parse([(byte)'M', 0, 0, 0]).Present);
        Assert.Equal(0, Lv.Parse([]).BodyOffset);

        // n above 600: the engine copies 600 + 4 bytes but skips 4 + n
        byte[] big = new byte[4 + 1000 + 8];
        big[0] = (byte)'L';
        big[1] = (byte)'V';
        BinaryPrimitives.WriteUInt16LittleEndian(big.AsSpan(2), 1000);
        var hb = Lv.Parse(big);
        Assert.Equal(1004, hb.BodyOffset);
        Assert.Equal(604, hb.EngineCopyLength);

        Assert.Throws<ContainerFormatException>(() => Lv.Parse([(byte)'L', (byte)'V']));
        Assert.Throws<ContainerFormatException>(() => Lv.Parse([(byte)'L', (byte)'V', 5, 0, 1]));
    }

    [Fact]
    public void LvEntryWithAnMzxBodyIsDecodedAndMeasured()
    {
        byte[] audio = new byte[300];
        new Random(5).NextBytes(audio);
        byte[] body = MzxLiteral(audio);
        byte[] entry = [(byte)'L', (byte)'V', 3, 0, 0, 9, 0, .. body, .. new byte[500]]; // packer padding after the stream
        var d = Lv.Decompress(entry);
        Assert.True(d.Compressed);
        Assert.Equal(audio, d.Data.ToArray());
        Assert.Equal(7 + body.Length, d.Consumed); // the exact entry size HED4 does not store
        Assert.NotNull(d.Mzx);
        Assert.Equal(body.Length, d.Mzx.Value.Consumed);
    }

    [Fact]
    public void LvEntryWithARawBodyIsReturnedUnchanged()
    {
        byte[] entry = [(byte)'L', (byte)'V', 1, 0, 0x55, 0x52, 0x49, 0x46, 0x46];
        var d = Lv.Decompress(entry);
        Assert.False(d.Compressed);
        Assert.Equal(entry[5..], d.Data.ToArray());
        Assert.Equal(entry.Length, d.Consumed);
        Assert.Null(d.Mzx);

        var noLv = Lv.Decompress([0x52, 0x49, 0x46, 0x46]);
        Assert.False(noLv.Header.Present);
        Assert.Equal([0x52, 0x49, 0x46, 0x46], noLv.Data.ToArray());
    }

    // ---- file views --------------------------------------------------------------------------

    [Fact]
    public void DirectoryViewResolvesRelativePathsIgnoringCase()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Image", "Movie"));
        File.WriteAllBytes(Path.Combine(_dir, "EBOOT.BIN"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dir, "Image", "AllScr.mrg"), [9, 8, 7, 6]);
        File.WriteAllBytes(Path.Combine(_dir, "Image", "Movie", "Intro.MP4"), []);
        var files = new DirectoryGameFiles(_dir);
        Assert.True(files.Exists("eboot.bin"));
        Assert.True(files.Exists("image/allscr.mrg"));
        Assert.True(files.Exists("image\\allscr.mrg"));
        Assert.False(files.Exists("image/nothing.mrg"));
        Assert.Equal(4, files.Length("image/allscr.mrg"));
        Assert.Equal([8, 7], files.ReadRange("image/allscr.mrg", 1, 2));
        Assert.Equal([9, 8, 7, 6], files.ReadAll("image/allscr.mrg"));
        Assert.Equal(["Intro.MP4"], files.ListFiles("image/movie"));
        Assert.Empty(files.ListFiles("image/nowhere"));
        Assert.Throws<FileNotFoundException>(() => files.Length("nothing.bin"));
        Span<byte> buffer = stackalloc byte[10];
        Assert.Equal(1, files.ReadAt("image/allscr.mrg", 3, buffer)); // short read at the end of the file
        Assert.Equal(0, files.ReadAt("image/allscr.mrg", 4, buffer));
    }

    [Fact]
    public void OverlayReplacesFlipsAndHidesWithoutTouchingTheDisk()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "image"));
        File.WriteAllBytes(Path.Combine(_dir, "eboot.bin"), [0x10, 0x20, 0x30]);
        File.WriteAllBytes(Path.Combine(_dir, "image", "a.hed"), [1]);
        var real = new DirectoryGameFiles(_dir);
        var view = new OverlayGameFiles(real).FlipByte("eboot.bin", 1, 0xFF).Hide("image/a.hed").Replace("image/new.bin", [5, 6]);
        Assert.Equal([0x10, 0xDF, 0x30], view.ReadAll("eboot.bin"));
        Assert.Equal([0x10, 0x20, 0x30], File.ReadAllBytes(Path.Combine(_dir, "eboot.bin"))); // the disk is unchanged
        Assert.False(view.Exists("image/a.hed"));
        Assert.True(real.Exists("image/a.hed"));
        Assert.Throws<FileNotFoundException>(() => view.Length("image/a.hed"));
        Assert.Equal([5, 6], view.ReadAll("image/new.bin"));
        Assert.Equal(["new.bin"], view.ListFiles("image"));
        Assert.Equal(2, view.Length("image/new.bin"));
    }

    // ---- build check on folders that are not the analysed build -----------------------------

    [Fact]
    public void AnEmptyFolderIsRejectedWithDistinctMessagesAndNoException()
    {
        var report = BuildCheck.Run(_dir);
        Assert.False(report.Accepted);
        Assert.Null(report.Info);
        Assert.Equal(BuildFailure.EbootMissing, report.PrimaryFailure);
        Assert.Contains(report.Failures, f => f.Id == "eboot.present" && f.Message.Contains("eboot.bin was not found"));
        Assert.Contains(report.Failures, f => f.Id == "data.allpac0.files" && f.Message.Contains("allpac0.hed") && f.Message.Contains("allpac0.mrg"));
        Assert.Contains(report.Failures, f => f.Id == "data.allscr.mrg.files");
        Assert.Contains(report.Failures, f => f.Id == "data.movies");
        string text = BuildReportFormatter.Format(report);
        Assert.Contains("RESULT: REJECTED (EbootMissing)", text);
        Assert.Contains("RESULT: REJECTED", BuildReportFormatter.Format(report, verbose: true));
    }

    [Fact]
    public void AnEbootOfAnotherSizeIsASizeMismatch()
    {
        File.WriteAllBytes(Path.Combine(_dir, "eboot.bin"), new byte[1000]);
        var report = BuildCheck.Run(_dir);
        Assert.Equal(BuildFailure.EbootSizeMismatch, report.PrimaryFailure);
        var size = report.Failures.Single(f => f.Id == "eboot.size");
        Assert.Contains("1,000 bytes", size.Message);
        Assert.Contains("12,767,802", size.Message);
        Assert.Contains(report.Failures, f => f.Id == "eboot.probe.self");
        Assert.Contains(report.Failures, f => f.Id == "eboot.probe.hunex");
        Assert.Equal(1000, report.Info!.EbootSize);
        Assert.False(report.Info.IsAnalysedBuild);
    }

    [Fact]
    public void AnEbootOfTheRightSizeWithOtherContentIsAHashMismatch()
    {
        File.WriteAllBytes(Path.Combine(_dir, "eboot.bin"), new byte[12_767_802]);
        var report = BuildCheck.Run(_dir);
        Assert.Contains(report.Checks, c => c.Id == "eboot.size" && c.Passed);
        var hash = report.Failures.Single(f => f.Id == "eboot.sha256");
        Assert.Equal(BuildFailure.EbootHashMismatch, hash.Failure);
        Assert.Contains("size matches but its content differs", hash.Message);
        Assert.Equal(BuildFailure.EbootHashMismatch, report.PrimaryFailure);
    }

    [Fact]
    public void AnArchiveThatIsNotAnArchiveIsALayoutFailureNotACrash()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "image"));
        File.WriteAllBytes(Path.Combine(_dir, "image", "parts.hed"), new byte[13]); // matches no layout
        File.WriteAllBytes(Path.Combine(_dir, "image", "parts.mrg"), new byte[16]);
        File.WriteAllBytes(Path.Combine(_dir, "image", "allscr.mrg"), new byte[100]); // not an mrgd00
        var report = BuildCheck.Run(_dir);
        Assert.Contains(report.Failures, f => f.Id == "data.parts.layout" && f.Failure == BuildFailure.LayoutInvalid);
        Assert.Contains(report.Failures, f => f.Id == "data.allscr.mrg.layout" && f.Failure == BuildFailure.LayoutInvalid);
    }

    [Fact]
    public void TheAnalysedBuildTablesAddUp()
    {
        Assert.Equal(AnalysedBuild.TripletEntryTotal, AnalysedBuild.Triplets.Sum(t => t.Entries));
        Assert.Equal(AnalysedBuild.StandaloneSectionTotal, AnalysedBuild.Standalone.Sum(s => s.Sections));
        Assert.Equal(12, AnalysedBuild.Triplets.Count);
        Assert.Equal(8, AnalysedBuild.Standalone.Count);
        Assert.Equal(AnalysedBuild.Scripts + 3, AnalysedBuild.Standalone.Single(s => s.FileName == "allscr.mrg").Sections);
        Assert.Equal(11, AnalysedBuild.Triplets.Single(t => t.Name == "allpac1").BankStarts.Count);
        Assert.Equal(AnalysedBuild.Voices, AnalysedBuild.Triplets.Single(t => t.Name == "voice").Entries);
    }

    // ---- static rule: no whole-archive read -------------------------------------------------

    [Fact]
    public void TsukiDataNeverReadsAFileWhole()
    {
        // ROADMAP M1 acceptance 8: no code path reads a whole archive into memory. Tsuki.Data reaches files only through
        // MappedFile (memory-mapped) and IGameFiles (positional reads of small ranges).
        var project = Repo.Projects().Single(p => p.Name == "Tsuki.Data");
        var offenders = new List<string>();
        foreach (string file in Repo.SourceFiles(project))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                bool whole = l.Contains("File.ReadAll", StringComparison.Ordinal) || l.Contains("ReadAllBytes", StringComparison.Ordinal)
                    || l.Contains("ReadAllText", StringComparison.Ordinal) || l.Contains("ReadAllLines", StringComparison.Ordinal);
                bool archiveRead = l.Contains(".ReadAll(", StringComparison.Ordinal) && l.Contains(".mrg", StringComparison.Ordinal);
                if (whole || archiveRead)
                {
                    offenders.Add($"{Repo.Relative(file)}:{i + 1}: {l.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }
}
