using System.Buffers.Binary;
using System.Text;
using Tsuki.Data;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>Package K17: is this folder the game? The launcher only looks; these tests also prove that a check leaves the folder exactly as it was.</summary>
public sealed class GameFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k17-folder-" + Guid.NewGuid().ToString("N"));

    public GameFolderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private string Make(string name, bool eboot = true, bool sfo = true)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "image"));
        Directory.CreateDirectory(Path.Combine(dir, "sce_sys"));
        if (eboot)
        {
            File.WriteAllBytes(Path.Combine(dir, "eboot.bin"), new byte[4096]);
        }

        if (sfo)
        {
            File.WriteAllBytes(Path.Combine(dir, "sce_sys", "param.sfo"), Sfo(("APP_VER", "01.00"), ("CONTENT_ID", "HP3279-CUSA47999_00-GAME000TSUKIHIME"), ("TITLE", "TSUKIHIME")));
        }

        return dir;
    }

    /// <summary>A param.sfo with string entries (the public PSF layout).</summary>
    internal static byte[] Sfo(params (string Key, string Value)[] entries)
    {
        using var keys = new MemoryStream();
        using var data = new MemoryStream();
        var index = new List<(int KeyOffset, int Length, int DataOffset)>();
        foreach ((string key, string value) in entries)
        {
            int keyOffset = (int)keys.Length;
            keys.Write(Encoding.UTF8.GetBytes(key + "\0"));
            int dataOffset = (int)data.Length;
            byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
            data.Write(bytes);
            index.Add((keyOffset, bytes.Length, dataOffset));
        }

        int keyTable = 20 + (entries.Length * 16);
        int dataTable = keyTable + (int)keys.Length;
        byte[] file = new byte[dataTable + (int)data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0x46535000u);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x101);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)keyTable);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)dataTable);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), (uint)entries.Length);
        for (int i = 0; i < index.Count; i++)
        {
            Span<byte> e = file.AsSpan(20 + (i * 16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)index[i].KeyOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(e[2..], 0x0204);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)index[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)index[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)index[i].DataOffset);
        }

        keys.ToArray().CopyTo(file, keyTable);
        data.ToArray().CopyTo(file, dataTable);
        return file;
    }

    private static string[] Listing(string dir) =>
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .Select(p => $"{Path.GetRelativePath(dir, p)}|{(File.Exists(p) ? new FileInfo(p).Length : -1)}|{File.GetLastWriteTimeUtc(p).Ticks}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void NoFolderAMissingFolderAndAFolderWithoutEbootAreToldApart()
    {
        Assert.Equal(FolderState.NotSet, GameFolderCheck.Inspect(null).State);
        Assert.Equal(FolderState.NotSet, GameFolderCheck.Inspect("   ").State);
        Assert.Equal(FolderState.Missing, GameFolderCheck.Inspect(Path.Combine(_root, "nowhere")).State);
        GameFolderInfo none = GameFolderCheck.Inspect(Make("empty", eboot: false));
        Assert.Equal(FolderState.NotTheGame, none.State);
        Assert.Contains("eboot.bin", none.Summary, StringComparison.Ordinal);
        Assert.False(none.IsReady);
    }

    [Fact]
    public void AnEbootOfAnotherBuildIsIncompleteAndSaysWhy()
    {
        string dir = Make("other");
        GameFolderInfo info = GameFolderCheck.Inspect(dir);
        Assert.Equal(FolderState.Incomplete, info.State);
        Assert.False(info.IsReady);
        Assert.True(info.ChecksTotal > 0);
        Assert.True(info.ChecksPassed < info.ChecksTotal);
        Assert.NotEmpty(info.ProblemList);
        Assert.Equal(info.ProblemList[0], info.Summary);
        Assert.Equal("CUSA47999", info.TitleId);                       // from CONTENT_ID: the sfo has no TITLE_ID here
        Assert.Equal("01.00", info.AppVersion);                        // not the analysed build, so only param.sfo can say
        Assert.Equal(4096, info.EbootBytes);
        Assert.True(info.TotalBytes >= 4096);
    }

    [Fact]
    public void InspectingNeverChangesTheFolder()
    {
        string dir = Make("readonly");
        string[] before = Listing(dir);
        _ = GameFolderCheck.Inspect(dir);
        _ = GameFolderCheck.Inspect(dir, measureSize: false);
        _ = GameFolderCheck.Resolve(dir);
        Assert.Equal(before, Listing(dir));
    }

    [Fact]
    public void ThePathTheUserChoseIsResolvedToTheGameFolder()
    {
        string game = Make("outer" + Path.DirectorySeparatorChar + "CUSA47999");
        string outer = Path.GetDirectoryName(game)!;
        Assert.Equal(game, GameFolderCheck.Resolve(game));
        Assert.Equal(game, GameFolderCheck.Resolve(outer));                                              // picked the folder above
        Assert.Equal(game, GameFolderCheck.Resolve(Path.Combine(game, "image")));                        // picked a folder inside
        Assert.Equal(game, GameFolderCheck.Resolve("\"" + game + "\""));                                 // pasted with quotes
        Assert.Equal(Path.Combine(_root, "nowhere"), GameFolderCheck.Resolve(Path.Combine(_root, "nowhere")));
        Assert.Equal(string.Empty, GameFolderCheck.Resolve(null));
    }

    [Fact]
    public void ParamSfoIsReadAndABrokenFileIsRefused()
    {
        ParamSfo? sfo = ParamSfo.TryParse(Sfo(("APP_VER", "01.00"), ("TITLE", "TSUKIHIME"), ("TITLE_ID", "CUSA47999")));
        Assert.NotNull(sfo);
        Assert.Equal("01.00", sfo["APP_VER"]);
        Assert.Equal("CUSA47999", sfo["TITLE_ID"]);
        Assert.Null(sfo["NOPE"]);
        Assert.Null(ParamSfo.TryParse(new byte[10]));
        Assert.Null(ParamSfo.TryParse(new byte[64]));
        byte[] bad = Sfo(("A", "B"));
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(16), 9999);                                 // more entries than the file holds
        Assert.Null(ParamSfo.TryParse(bad));
        Assert.Null(ParamSfo.TryRead(Path.Combine(_root, "none.sfo")));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(13_366_000L, "12.7 MB")]
    [InlineData(47_332_000_000L, "44.1 GB")]
    public void SizesAreReadable(long bytes, string expected)
    {
        Assert.Equal(expected, GameFolderCheck.FormatSize(bytes));
    }

    // -- the ready check -----------------------------------------------------------------------------------------------

    private static readonly SaveSummary NoSaves = new(false, 0, 0, null, null);

    private static GameFolderInfo Ready() => new("C:\\game", FolderState.Ready, "CUSA47999", GameFolderCheck.AnalysedVersion, "TSUKIHIME", 12_767_802, 47_332_000_000, 76, 76, null, 20, 134, "01.00");

    [Fact]
    public void ABadFolderMakesTheFirstTwoRowsWarningsThatLeadToTheGameFilesPage()
    {
        IReadOnlyList<ReadyRow> rows = ReadyCheck.Build(GameFolderCheck.NotSet, @"C:\x\Tsukihime.exe", [], ["Some GPU"], NoSaves);
        Assert.Equal(5, rows.Count);
        Assert.Equal(CheckState.Warning, rows[0].State);
        Assert.Equal(LauncherPage.GameFiles, rows[0].Fix);
        Assert.Equal(CheckState.Warning, rows[1].State);
        Assert.Equal(LauncherPage.GameFiles, rows[1].Fix);
        Assert.All(rows.Skip(2), r => Assert.Equal(CheckState.Ok, r.State));
        Assert.True(rows[0].HasFix);
        Assert.False(rows[2].HasFix);
    }

    [Fact]
    public void AReadyFolderMakesEveryRowFineAndNamesTheVersion()
    {
        IReadOnlyList<ReadyRow> rows = ReadyCheck.Build(Ready(), @"C:\x\Tsukihime.exe", [], ["AMD Radeon RX 6700 XT"], new SaveSummary(true, 12, 9, DateTime.Now, null));
        Assert.All(rows, r => Assert.Equal(CheckState.Ok, r.State));
        Assert.Contains("version 01.01", rows[0].Detail, StringComparison.Ordinal);
        Assert.Contains("20 archives", rows[1].Detail, StringComparison.Ordinal);
        Assert.Contains("AMD Radeon RX 6700 XT", rows[3].Detail, StringComparison.Ordinal);
        Assert.Contains("OpenGL 3.3", rows[3].Detail, StringComparison.Ordinal);
        Assert.Contains("12 save slots", rows[4].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingProgramOrLibraryIsAWarningThatLeadsToHelpAndStopsPlay()
    {
        IReadOnlyList<ReadyRow> noApp = ReadyCheck.Build(Ready(), null, [], [], NoSaves);
        Assert.Equal(CheckState.Warning, noApp[2].State);
        Assert.Equal(LauncherPage.Help, noApp[2].Fix);
        IReadOnlyList<ReadyRow> noDll = ReadyCheck.Build(Ready(), @"C:\x\Tsukihime.exe", ["avcodec-63.dll", "avutil-61.dll"], [], NoSaves);
        Assert.Equal(CheckState.Warning, noDll[2].State);
        Assert.Contains("avcodec-63.dll", noDll[2].Detail, StringComparison.Ordinal);
        Assert.Contains("1 more", noDll[2].Detail, StringComparison.Ordinal);

        Assert.True(ReadyCheck.CanPlay(Ready(), @"C:\x\Tsukihime.exe", []));
        Assert.False(ReadyCheck.CanPlay(Ready(), null, []));
        Assert.False(ReadyCheck.CanPlay(Ready(), @"C:\x\Tsukihime.exe", ["avcodec-63.dll"]));
        Assert.False(ReadyCheck.CanPlay(GameFolderCheck.NotSet, @"C:\x\Tsukihime.exe", []));
    }

    [Fact]
    public void ADamagedSaveFileIsAWarningThatLeadsToTheSavesPage()
    {
        IReadOnlyList<ReadyRow> rows = ReadyCheck.Build(Ready(), @"C:\x\Tsukihime.exe", [], [], new SaveSummary(true, 0, 0, DateTime.Now, "bad crc"));
        Assert.Equal(CheckState.Warning, rows[4].State);
        Assert.Equal(LauncherPage.Saves, rows[4].Fix);
        Assert.Contains("bad crc", rows[4].Detail, StringComparison.Ordinal);
    }
}
