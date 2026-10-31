using System.Diagnostics;
using Tsuki.Data.Catalog;
using Tsuki.Data.Containers;
using Tsuki.Data.Eboot;
using Tsuki.Data.IO;

namespace Tsuki.Tests.Data.Catalog;

/// <summary>
/// Loading the whole catalog from a folder view (<see cref="GameCatalog"/>), the way the runtime does at start: only
/// <c>eboot.bin</c> and five small <c>.nam</c> files are read, in well under a second, and a wrong folder is an error with a
/// message.
/// </summary>
public sealed class GameCatalogTests
{
    [GameDataFact]
    public void TheCatalogLoadsFromTheUsersFolderWithTheDocumentedCounts()
    {
        var cat = GameCatalog.Load(new DirectoryGameFiles(GameFolder.Require()));
        Assert.Equal(14_744, cat.Images.Count);
        Assert.Equal(31, cat.Images.EmptySlots);
        Assert.Equal(14_745, cat.PictureNames.Count);
        Assert.Equal(9_959, cat.OldPictureNames.Count);
        Assert.Equal(134, cat.Movies.Count);
        Assert.Equal(21, cat.UiTables.Count);
        Assert.Equal(220, cat.UiTables.Sum(t => t.Records.Count));
        Assert.Equal(["ja", "en", "zc", "zt", "ko"], cat.LanguageSuffixes);
        Assert.Equal([4, 46, 47, 106], cat.PrologueIds);
        Assert.Equal(5, cat.Nams.Count);
        Assert.Equal([37, 136, 4_317, 9_968, 2_449], ImageNameIndex.SearchOrder.Select(a => cat.Nams[a].Count));
        Assert.Equal(1_499, cat.UiText.Rows(1).Count);
        Assert.Same(cat.UiText, cat.UiText);
    }

    [GameDataFact]
    public void TheCatalogLoadsInWellUnderASecondAndReadsNothingBig()
    {
        string root = GameFolder.Require();
        var files = new CountingFiles(new DirectoryGameFiles(root));
        _ = GameCatalog.Load(files); // JIT
        files.Reset();
        var sw = Stopwatch.StartNew();
        var cat = GameCatalog.Load(files);
        sw.Stop();
        Assert.Equal(14_744, cat.Images.Count);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"{sw.ElapsedMilliseconds} ms");
        // eboot.bin (12.8 MB) and five .nam files; never an archive
        Assert.Equal(6, files.Opened.Count);
        Assert.Equal("eboot.bin", files.Opened[0]);
        Assert.All(files.Opened.Skip(1), n => Assert.EndsWith(".nam", n, StringComparison.Ordinal));
        Assert.True(files.BytesRead < 14_000_000, $"{files.BytesRead} bytes read");
    }

    [GameDataFact]
    public void TheEntryPointsAgreeWithEachOther()
    {
        var cat = SharedEboot.Catalog;
        // script id -> record -> NAM search -> archive entry, and back to the NAM name
        var r = cat.Resolver.Resolve("img2511");
        var nam = cat.Nams[r.FileId!.Value.Archive];
        Assert.Equal("IMG2511.MZP", nam[r.FileId.Value.Index].ToUpperInvariant());
        Assert.Equal(cat.Images[r.Index].Name0, r.File);
        Assert.Equal("IMG2511", cat.PictureNames[r.Index]);
        // the UI string table is the same object as the one read directly
        Assert.Equal(UiTextTable.Read(cat.Eboot.Image).Rows(0)[100].Text, cat.UiText.Rows(0)[100].Text);
    }

    [Fact]
    public void AFolderWithoutAnEbootOrNamFileFailsWithTheMissingFile()
    {
        var empty = new DirectoryGameFiles(Path.Combine(Path.GetTempPath(), "tsuki-no-such-game-folder"));
        Assert.Throws<FileNotFoundException>(() => GameCatalog.Load(empty));
    }

    [Fact]
    public void AnEbootThatIsNotASelfIsAFormatError()
    {
        var files = new MemoryFiles(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["eboot.bin"] = new byte[4096] });
        Assert.Throws<EbootFormatException>(() => GameCatalog.Load(files));
    }

    [GameDataFact]
    public void AMissingNamFileIsAFileError()
    {
        var real = new DirectoryGameFiles(GameFolder.Require());
        var files = new MemoryFiles(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["eboot.bin"] = SharedEboot.Data });
        Assert.Throws<FileNotFoundException>(() => GameCatalog.Load(files));
        Assert.True(real.Exists("image/allpac0.nam"));
    }

    [GameDataFact]
    public void ANamFileThatBreaksItsLayoutIsAContainerError()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["eboot.bin"] = SharedEboot.Data };
        var real = new DirectoryGameFiles(GameFolder.Require());
        foreach (var (_, path) in GameCatalog.SearchedArchives)
        {
            map[path] = real.ReadAll(path);
        }

        map["image/allpac3.nam"] = map["image/allpac3.nam"][..(map["image/allpac3.nam"].Length - 5)];
        Assert.Throws<ContainerFormatException>(() => GameCatalog.Load(new MemoryFiles(map)));
    }

    private sealed class MemoryFiles(Dictionary<string, byte[]> files) : IGameFiles
    {
        public string Description => "memory";

        public bool Exists(string relativePath) => files.ContainsKey(relativePath);

        public long Length(string relativePath) =>
            files.TryGetValue(relativePath, out var b) ? b.Length : throw new FileNotFoundException(relativePath);

        public int ReadAt(string relativePath, long offset, Span<byte> buffer)
        {
            var b = files.TryGetValue(relativePath, out var f) ? f : throw new FileNotFoundException(relativePath);
            int n = (int)Math.Min(buffer.Length, Math.Max(0, b.Length - offset));
            b.AsSpan((int)offset, n).CopyTo(buffer);
            return n;
        }

        public IReadOnlyList<string> ListFiles(string relativeDirectory) => [];
    }

    private sealed class CountingFiles(IGameFiles inner) : IGameFiles
    {
        public List<string> Opened { get; } = [];

        public long BytesRead { get; private set; }

        public string Description => inner.Description;

        public void Reset()
        {
            Opened.Clear();
            BytesRead = 0;
        }

        public bool Exists(string relativePath) => inner.Exists(relativePath);

        public long Length(string relativePath) => inner.Length(relativePath);

        public int ReadAt(string relativePath, long offset, Span<byte> buffer)
        {
            if (offset == 0 && !Opened.Contains(relativePath))
            {
                Opened.Add(relativePath);
            }

            int n = inner.ReadAt(relativePath, offset, buffer);
            BytesRead += n;
            return n;
        }

        public IReadOnlyList<string> ListFiles(string relativeDirectory) => inner.ListFiles(relativeDirectory);
    }
}
