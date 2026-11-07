using System.Runtime.InteropServices;
using Tsuki.Media.Ffmpeg;
using Tsuki.Platform;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>The native names of each release, including the Mac names checked on every build platform.</summary>
public sealed class NativeLibraryTests
{
    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public void MacSdlComesFromItsOwnRuntimeFolderOrBesideTheProgram(string architecture)
    {
        Architecture arch = architecture == "x64" ? Architecture.X64 : Architecture.Arm64;
        string root = Path.Combine(Path.GetTempPath(), "app");
        Assert.Equal(
            [
                Path.Combine(root, "runtimes", "osx-" + architecture, "native", "libSDL3.dylib"),
                Path.Combine(root, "libSDL3.dylib"),
            ],
            NativeSdl.Candidates(root, OSPlatform.OSX, arch));
    }

    [Fact]
    public void MacFfmpegUsesThePinnedDylibNames()
    {
        Assert.Equal(
            ["libavutil.61.dylib", "libswresample.7.dylib", "libavcodec.63.dylib", "libavformat.63.dylib"],
            FfmpegLibrary.RequiredDllsFor(OSPlatform.OSX));
    }
}
