using Tsuki.App.Play;

namespace Tsuki.Tests.Synthetic.App;

[Collection("Profile environment")]
public sealed class ScreenshotFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-screenshot-folder-" + Guid.NewGuid().ToString("N"));
    private readonly string? _home = Environment.GetEnvironmentVariable("HOME");
    private readonly string? _config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly string _workingDirectory = Environment.CurrentDirectory;

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static bool IsWindows => OperatingSystem.IsWindows();

    public ScreenshotFolderTests()
    {
        Directory.CreateDirectory(_root);
        Environment.CurrentDirectory = _root;
        if (IsLinux)
        {
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            Directory.CreateDirectory(Path.Combine(_root, "config"));
            Environment.SetEnvironmentVariable("HOME", Path.Combine(_root, "home"));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, "config"));
        }
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _workingDirectory;
        Environment.SetEnvironmentVariable("HOME", _home);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);
        Directory.Delete(_root, recursive: true);
    }

    [Theory(Skip = "Linux Pictures lookup.", SkipUnless = nameof(IsLinux))]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingPicturesIsCreatedWithoutCollidingWithTheProgram(bool redirected)
    {
        string pictures = redirected ? Path.Combine(_root, "redirected pictures") : Path.Combine(_root, "home", "Pictures");
        if (redirected)
        {
            File.WriteAllText(Path.Combine(_root, "config", "user-dirs.dirs"), $"XDG_PICTURES_DIR=\"{pictures}\"\n");
        }

        Assert.Equal(string.Empty, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        File.WriteAllText(Path.Combine(_root, "Tsukihime"), "synthetic program");
        var writer = new ScreenshotWriter(ScreenshotWriter.DefaultFolder);
        Assert.False(Directory.Exists(pictures));

        string path = writer.Save(1, 1, [10, 20, 30, 0]);
        Assert.True(writer.WaitAll(TimeSpan.FromSeconds(10)));
        Assert.True(writer.TryTakeResult(out ScreenshotResult result));
        Assert.True(result.Saved, result.Error);
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Equal(Path.Combine(pictures, "Tsukihime"), writer.Folder);
        Assert.Equal(path, result.Path);
        (int width, int height, byte[] rgba) = ScreenshotWriterTests.ReadPng(path);
        Assert.Equal((1, 1), (width, height));
        Assert.Equal([10, 20, 30, 255], rgba);
        Assert.Equal("synthetic program", File.ReadAllText(Path.Combine(_root, "Tsukihime")));
    }

    [Theory(Skip = "Linux Pictures lookup.", SkipUnless = nameof(IsLinux))]
    [InlineData("")]
    [InlineData("relative-pictures")]
    public void ANonAbsolutePicturesSettingFallsBackToTheUserProfile(string pictures)
    {
        File.WriteAllText(Path.Combine(_root, "config", "user-dirs.dirs"), $"XDG_PICTURES_DIR=\"{pictures}\"\n");
        string folder = ScreenshotWriter.DefaultFolder;

        Assert.True(Path.IsPathFullyQualified(folder));
        Assert.Equal(Path.Combine(_root, "home", "Pictures", "Tsukihime"), folder);
        Assert.False(Directory.Exists(folder));
    }

    [Fact(Skip = "Linux Pictures lookup.", SkipUnless = nameof(IsLinux))]
    public void ANonAbsoluteProfileRequiresAnExplicitScreenshotFolder()
    {
        Environment.SetEnvironmentVariable("HOME", "relative-home");
        File.WriteAllText(Path.Combine(_root, "config", "user-dirs.dirs"), "XDG_PICTURES_DIR=\"\"\n");

        IOException error = Assert.Throws<IOException>(() => ScreenshotWriter.DefaultFolder);
        Assert.Contains("--screenshot-dir", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "relative-home")));
    }

    [Fact(Skip = "Windows Pictures lookup.", SkipUnless = nameof(IsWindows))]
    public void WindowsKeepsTheConfiguredPicturesFolder()
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify);
        Assert.Equal(Path.Combine(pictures, "Tsukihime"), ScreenshotWriter.DefaultFolder);
    }
}
