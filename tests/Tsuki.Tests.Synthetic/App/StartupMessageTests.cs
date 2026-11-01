using Tsuki.App.Play;
using Tsuki.Data;
using Tsuki.Media.Ffmpeg;
using Tsuki.Platform;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Review R1 (core): the error box of a start that fails tells the player what to do in a sentence; the technical message, which used to name a repository script and
/// an architecture section, stays in the log and in crash.log.
/// </summary>
public sealed class StartupMessageTests
{
    private const string Launcher = "Choose the CUSA47999 folder again in the launcher.";

    [Fact]
    public void MissingFfmpegDllsAreNamedToThePlayerWithoutTheDeveloperHint()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tsuki-r1-ffmpeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var e = new FfmpegMissingException(Path.GetFullPath(dir), FfmpegLibrary.RequiredDlls);
            Assert.Equal(FfmpegLibrary.RequiredDlls, e.MissingFiles);
            Assert.Equal(Path.GetFullPath(dir), e.Folder);
            string names = OperatingSystem.IsLinux()
                ? "libavutil.so.61, libswresample.so.7, libavcodec.so.63, libavformat.so.63"
                : "avutil-61.dll, swresample-7.dll, avcodec-63.dll, avformat-63.dll";
            Assert.Equal(OperatingSystem.IsLinux() ? "libavutil.so.61" : "avutil-61.dll", e.FileName);
            Assert.Equal($"FFmpeg {(OperatingSystem.IsLinux() ? "shared libraries" : "DLLs")} missing from {Path.GetFullPath(dir)}: {names}", e.Message);
            Assert.DoesNotContain("tools/setup", e.Message);
            Assert.DoesNotContain("ARCHITECTURE", e.Message);

            Assert.Equal(
                OperatingSystem.IsLinux()
                    ? "FFmpeg 9 shared libraries are missing (libavutil.so.61, libswresample.so.7, libavcodec.so.63, libavformat.so.63). Install the FFmpeg 9 shared libraries or place them next to Tsukihime."
                    : "The FFmpeg libraries are missing next to Tsukihime.exe (avutil-61.dll, swresample-7.dll, avcodec-63.dll, avformat-63.dll). Reinstall the port or copy them back.",
                GameApp.PlayerMessage(e));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ARefusedGlContextIsAGraphicsDriverSentence()
    {
        var context = new PlatformException("SDL_GL_CreateContext (3.3 core): Could not create GL context: The operation completed successfully.");
        Assert.Equal("Your graphics driver does not provide OpenGL 3.3. Update the driver or run the game on the dedicated GPU.", GameApp.WindowMessage(context));
        Assert.Equal(GameApp.WindowMessage(context), GameApp.WindowMessage(new PlatformException("SDL_GL_MakeCurrent: wglMakeCurrent(): The handle is invalid.")));
        Assert.Equal("The game window could not be created. Update your graphics driver or run the game on the dedicated GPU.", GameApp.WindowMessage(new PlatformException("SDL_CreateWindow: No available video device")));

        // the failure the loop reports: the player's sentence first, the SDL text kept for the log and crash.log
        var failure = new StartupFailure(GameApp.WindowMessage(context), context);
        Assert.Equal("Your graphics driver does not provide OpenGL 3.3. Update the driver or run the game on the dedicated GPU.", GameApp.PlayerMessage(failure));
        Assert.Contains("SDL_GL_CreateContext (3.3 core)", failure.ToString());
    }

    [Fact]
    public void AnIncompleteGameFolderNamesTheMissingFileAndTheWayBack()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tsuki-r1-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // an empty folder: eboot.bin is the first file the source reads
            FileNotFoundException e = Assert.ThrowsAny<FileNotFoundException>(() => GameFolderSource.Open(dir));
            Assert.Equal($"The game folder is incomplete: eboot.bin is missing from {dir}. {Launcher}", GameApp.GameFolderMessage(dir, e));
            Assert.Equal(
                $"The game folder is incomplete: image/allpac4.nam is missing from D:\\game. {Launcher}",
                GameApp.GameFolderMessage("D:\\game", new FileNotFoundException("image/allpac4.nam not found in D:\\game", "image/allpac4.nam")));

            string gone = Path.Combine(dir, "gone");
            DirectoryNotFoundException d = Assert.Throws<DirectoryNotFoundException>(() => GameFolderSource.Open(gone));
            Assert.Equal($"The game folder {gone} no longer exists. {Launcher}", GameApp.GameFolderMessage(gone, d));
            Assert.Equal($"The game folder {dir} could not be opened: odd", GameApp.GameFolderMessage(dir, new InvalidDataException("odd")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OtherFailuresShowTheirOwnMessage()
    {
        Assert.Equal("a decode failed", GameApp.PlayerMessage(new InvalidOperationException("a decode failed")));
        Assert.Equal("x", GameApp.PlayerMessage(new StartupFailure("x", new InvalidOperationException("cause"))));
    }
}
