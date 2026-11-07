using Tsuki.Data.IO;
using Tsuki.Data.Verification;

namespace Tsuki.Tests.Synthetic.Data;

/// <summary>
/// The files macOS writes beside others on a drive without Mac metadata (exFAT, FAT32): an AppleDouble <c>._NAME</c> for every file that carries an extended
/// attribute, and <c>.DS_Store</c>. Off Windows the game folder listing leaves them out, so the movie check counts the game's own movies (a folder on an exFAT drive
/// used to show 268 movies and was refused as another release); Windows lists every file, as v0.1.6 did.
/// </summary>
public sealed class MacMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-applDouble-" + Guid.NewGuid().ToString("N"));

    public MacMetadataTests()
    {
        string movies = Path.Combine(_root, "image", "movie");
        Directory.CreateDirectory(movies);
        foreach (string name in new[] { "op.mp4", "ed_en.mp4", "._op.mp4", "._ed_en.mp4", ".DS_Store" })
        {
            File.WriteAllBytes(Path.Combine(movies, name), [0]);
        }
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("._op.mp4", true)]
    [InlineData("._", true)]
    [InlineData(".DS_Store", true)]
    [InlineData("op.mp4", false)]
    [InlineData("_op.mp4", false)]
    [InlineData(".op.mp4", false)]
    [InlineData(".ds_store", false)]
    [InlineData("x._op.mp4", false)]
    public void AppleDoubleFilesAndTheFinderStoreAreMacMetadata(string name, bool metadata)
    {
        Assert.Equal(metadata, DirectoryGameFiles.IsMacMetadata(name));
    }

    [Fact]
    public void OffWindowsTheMovieFolderListsOnlyTheGamesOwnFiles()
    {
        IReadOnlyList<string> listed = new DirectoryGameFiles(_root).ListFiles("image/movie");
        Assert.Equal(
            OperatingSystem.IsWindows() ? [".DS_Store", "._ed_en.mp4", "._op.mp4", "ed_en.mp4", "op.mp4"] : ["ed_en.mp4", "op.mp4"],
            listed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OffWindowsTheMovieCheckCountsTheRealMoviesOnly()
    {
        BuildReport report = BuildCheck.Run(_root);
        Assert.Equal(OperatingSystem.IsWindows() ? 4 : 2, report.Counts.MovieFiles);
    }
}
