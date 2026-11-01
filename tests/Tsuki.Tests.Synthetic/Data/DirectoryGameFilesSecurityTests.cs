using Tsuki.Data.IO;

namespace Tsuki.Tests.Synthetic.Data;

public sealed class DirectoryGameFilesSecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-paths-" + Guid.NewGuid().ToString("N"));
    private readonly DirectoryGameFiles _files;

    public DirectoryGameFilesSecurityTests()
    {
        string root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(Path.Combine(root, "Image", "Movie"));
        Directory.CreateDirectory(Path.Combine(_dir, "root-other"));
        File.WriteAllBytes(Path.Combine(root, "Image", "Movie", "Intro.MP4"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dir, "outside.bin"), [99]);
        File.WriteAllBytes(Path.Combine(_dir, "root-other", "outside.bin"), [99]);
        _files = new DirectoryGameFiles(root);
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("..\\outside.bin")]
    [InlineData("Image/../../outside.bin")]
    [InlineData("../root-other/outside.bin")]
    [InlineData("Image/.. /../outside.bin")]
    [InlineData("C:\\outside.bin")]
    [InlineData("C:outside.bin")]
    [InlineData("/outside.bin")]
    [InlineData("\\outside.bin")]
    [InlineData("\\\\server\\share\\outside.bin")]
    [InlineData("Image/Movie/Intro.MP4:stream")]
    public void UnsafePathsCannotResolveReadOrEnumerate(string path)
    {
        Assert.Null(_files.ResolveFile(path));
        Assert.False(_files.Exists(path));
        Assert.Throws<FileNotFoundException>(() => _files.Length(path));
        Assert.Throws<FileNotFoundException>(() => _files.ReadAt(path, 0, new byte[1]));
        Assert.Empty(_files.ListFiles(path));
    }

    [Fact]
    public void AnExistingAbsolutePathCannotBypassTheRoot()
    {
        string outside = Path.Combine(_dir, "outside.bin");
        Assert.Null(_files.ResolveFile(outside));
        Assert.Throws<FileNotFoundException>(() => _files.ReadAll(outside));
        Assert.Null(_files.ResolveFile(Path.Combine(_files.Root, "Image", "Movie", "Intro.MP4")));
        Assert.Empty(_files.ListFiles(_dir));
    }

    [Theory]
    [InlineData("Image/Movie/Intro.MP4")]
    [InlineData("image/movie/intro.mp4")]
    [InlineData("IMAGE\\MOVIE\\INTRO.MP4")]
    [InlineData("Image//Movie/Intro.MP4")]
    public void NormalPathsKeepCaseInsensitiveReads(string path)
    {
        Assert.Equal([1, 2, 3], _files.ReadAll(path));
        Assert.Equal(3, _files.Length(path));
        Assert.Equal(Path.Combine(_files.Root, "Image", "Movie", "Intro.MP4"), _files.ResolveFile(path),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Assert.Equal(["Intro.MP4"], _files.ListFiles("image/movie"));
        Assert.Empty(_files.ListFiles("missing"));
        Assert.Empty(_files.ListFiles(""));
    }

    [Fact(Skip = "Symbolic links require OS support and permission.", SkipUnless = nameof(CanCreateLinks))]
    public void LinkedFilesDirectoriesAndChainsKeepCaseInsensitiveReadsAndListing()
    {
        string outside = Path.Combine(_dir, "outside.bin");
        string link = Path.Combine(_files.Root, "linked.bin");
        string dirLink = Path.Combine(_files.Root, "linked");
        string chain = Path.Combine(_files.Root, "chain");
        File.CreateSymbolicLink(link, outside);
        Directory.CreateSymbolicLink(dirLink, _dir);
        Directory.CreateSymbolicLink(chain, dirLink);

        foreach (string path in new[] { "linked.bin", "LINKED.BIN", "linked/outside.bin", "LINKED/OUTSIDE.BIN", "chain/outside.bin", "CHAIN/OUTSIDE.BIN" })
        {
            Assert.NotNull(_files.ResolveFile(path));
            Assert.True(_files.Exists(path));
            Assert.Equal(1, _files.Length(path));
            Assert.Equal([99], _files.ReadAll(path));
        }

        Assert.Equal(["outside.bin"], _files.ListFiles("linked"));
        Assert.Equal(["outside.bin"], _files.ListFiles("LINKED"));
        Assert.Equal(["outside.bin"], _files.ListFiles("chain"));
        Assert.Equal(["outside.bin"], _files.ListFiles("CHAIN"));
        Assert.Equal(["linked.bin"], _files.ListFiles(""));

        File.CreateSymbolicLink(Path.Combine(_files.Root, "broken.bin"), Path.Combine(_dir, "missing"));
        Assert.Throws<FileNotFoundException>(() => _files.ReadAll("broken.bin"));
    }

    public static bool CanCreateLinks
    {
        get
        {
            string dir = Path.Combine(Path.GetTempPath(), "tsuki-link-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "target"), []);
                File.CreateSymbolicLink(Path.Combine(dir, "link"), Path.Combine(dir, "target"));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return false;
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
