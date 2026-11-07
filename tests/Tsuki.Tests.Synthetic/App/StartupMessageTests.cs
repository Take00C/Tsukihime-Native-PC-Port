using Tsuki.App;
using Tsuki.App.Play;
using Tsuki.Data;
using Tsuki.Data.IO;
using Tsuki.Engine.Contracts;
using Tsuki.Media.Ffmpeg;
using Tsuki.Platform;
using Tsuki.Ui;

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
            string[] required = OperatingSystem.IsWindows()
                ? ["avutil-61.dll", "swresample-7.dll", "avcodec-63.dll", "avformat-63.dll"]
                : OperatingSystem.IsMacOS()
                    ? ["libavutil.61.dylib", "libswresample.7.dylib", "libavcodec.63.dylib", "libavformat.63.dylib"]
                    : ["libavutil.so.61", "libswresample.so.7", "libavcodec.so.63", "libavformat.so.63"];
            Assert.Equal(required, FfmpegLibrary.RequiredDlls);
            var e = new FfmpegMissingException(Path.GetFullPath(dir), required);
            Assert.Equal(required, e.MissingFiles);
            Assert.Equal(Path.GetFullPath(dir), e.Folder);
            string names = string.Join(", ", required);
            Assert.Equal(required[0], e.FileName);
            Assert.Equal($"FFmpeg {(!OperatingSystem.IsWindows() ? "shared libraries" : "DLLs")} missing from {Path.GetFullPath(dir)}: {names}", e.Message);
            Assert.DoesNotContain("tools/setup", e.Message);
            Assert.DoesNotContain("ARCHITECTURE", e.Message);

            Assert.Equal(
                OperatingSystem.IsLinux()
                    ? $"FFmpeg 9 shared libraries are missing ({names}). Install the FFmpeg 9 shared libraries or place them next to Tsukihime."
                    : OperatingSystem.IsMacOS()
                        ? $"The FFmpeg libraries are missing inside Tsukihime.app ({names}). Reinstall the port or copy them back."
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

    [Fact]
    public void AFolderDialogTheSystemCannotOpenNamesTheErrorAndTheWayToNameTheFolder()
    {
        // K46: on a Linux desktop without xdg-desktop-portal and zenity, SDL's folder dialog fails at once; the first run used to quit without a word
        const string sdl = "File dialog driver unsupported (supported values for SDL_HINT_FILE_DIALOG_DRIVER are 'zenity' and 'portal')";
        string command = OperatingSystem.IsWindows()
            ? @"Tsukihime.exe --game ""D:\Games\CUSA47999"""
            : OperatingSystem.IsMacOS() ? @"Tsukihime.app/Contents/MacOS/Tsukihime --game ""/path/to/CUSA47999""" : "./Tsukihime --game /path/to/CUSA47999";
        Assert.Equal(
            $"The folder window could not be opened ({sdl}).\n\nStart the game with the path of your CUSA47999 folder instead, for example:\n{command}\n\n"
            + "or set the environment variable TSUKI_GAME_DIR to that folder. On Linux the folder window needs xdg-desktop-portal or zenity.",
            GameApp.FolderDialogFailedMessage(sdl, steamDeck: false));
        foreach (Language lang in PortText.Languages)
        {
            string text = GameApp.FolderDialogFailedMessage(sdl, lang, steamDeck: false);
            Assert.Contains(sdl, text, StringComparison.Ordinal);
            Assert.Contains(command, text, StringComparison.Ordinal);
            Assert.Contains("TSUKI_GAME_DIR", text, StringComparison.Ordinal);
            Assert.Contains("zenity", text, StringComparison.Ordinal);
        }

        Assert.Contains("(?)", GameApp.FolderDialogFailedMessage(null), StringComparison.Ordinal);
        Assert.True(AppArgs.TryParse(["--game", "/g/CUSA47999"], out AppArgs? args, out _));
        Assert.Equal("/g/CUSA47999", args!.GameDir);
    }

    [Fact]
    public void OnASteamDeckTheFolderBoxNamesTheLaunchOptionsInEachLanguage()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("the Steam Deck advice is Linux's");
        }

        string[] where = ["起動オプション", "Launch Options", "启动选项", "啟動選項"];
        for (int i = 0; i < PortText.Languages.Count; i++)
        {
            string text = GameApp.FolderDialogFailedMessage("no portal", PortText.Languages[i], steamDeck: true);
            Assert.Contains(where[i], text, StringComparison.Ordinal);
            Assert.Contains("\n--game \"/home/deck/Games/CUSA47999\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("./Tsukihime", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AGameFolderOnTheCommandLineThatHoldsNoGameIsNamedAsTypedAndAsRead()
    {
        var missing = new GameLayout("/home/deck/Games/CUSA4799", GameLayoutKind.Missing);
        Assert.Equal("--game \"~/Games/CUSA4799\"\n\nThe game folder /home/deck/Games/CUSA4799 no longer exists.", GameApp.CommandLineFolderMessage("~/Games/CUSA4799", missing));
        var empty = new GameLayout("/run/media/deck/SD", GameLayoutKind.NoGame);
        Assert.Equal(
            "--game \"/run/media/deck/SD\"\n\n/run/media/deck/SD holds no game: no folder with eboot.bin and image\\ was found in it, not even one or two levels down.",
            GameApp.CommandLineFolderMessage("/run/media/deck/SD", empty));
        var pkg = new GameLayout("/home/deck/game.pkg", GameLayoutKind.PkgFile);
        Assert.Equal("--game \"/home/deck/game.pkg\"\n\n" + GameApp.GameFolderMessage("/home/deck/game.pkg", new GameLayoutException(pkg)), GameApp.CommandLineFolderMessage("/home/deck/game.pkg", pkg));
        Assert.DoesNotContain("launcher", GameApp.CommandLineFolderMessage("x", missing, Language.En), StringComparison.Ordinal);
        Assert.StartsWith("--game \"x\"\n\nゲームフォルダ /home/deck/Games/CUSA4799", GameApp.CommandLineFolderMessage("x", missing, Language.Ja), StringComparison.Ordinal);
    }

    [Fact]
    public void OffWindowsACommandLineTheGameCannotReadIsWrittenToTheProfilesCrashLog()
    {
        string profile = Path.Combine(Path.GetTempPath(), "tsuki-badargs-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Steam's Launch Options with an unquoted path that has a space: the shell splits it
            string[] args = ["--save-dir", profile, "--hidden", "--game", "/run/media/deck/SD", "Card/CUSA47999"];
            Assert.False(AppArgs.TryParse(args, out _, out string? error));
            Assert.Null(Program.RefuseCommandLine(args, error!, hidden: true, windows: true));             // Windows: the console only, as v0.1.6
            Assert.False(Directory.Exists(profile));

            string? crash = Program.RefuseCommandLine(args, error!, hidden: true, windows: false);
            Assert.Equal(Path.Combine(profile, "crash.log"), crash);
            string text = File.ReadAllText(crash!);
            Assert.Contains("the command line could not be read: unknown or incomplete argument: Card/CUSA47999", text, StringComparison.Ordinal);
            Assert.Contains("\"/run/media/deck/SD\" \"Card/CUSA47999\"", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(profile))
            {
                Directory.Delete(profile, recursive: true);
            }
        }
    }

    [Fact]
    public void OffWindowsACommandLineTheGameCannotReadFindsATildeProfileInTheHomeFolder()
    {
        string home = Path.Combine(Path.GetTempPath(), "tsuki-badargs-home-" + Guid.NewGuid().ToString("N"));
        string literal = Path.GetFullPath("~");
        bool literalExisted = Directory.Exists(literal);
        Directory.CreateDirectory(home);
        try
        {
            // Steam's Launch Options pass ~ as typed
            string[] args = ["--save-dir", "~/profile", "--hidden", "--bogus"];
            Assert.False(AppArgs.TryParse(args, home, out _, out string? error));
            Assert.Null(Program.RefuseCommandLine(args, error!, hidden: true, windows: true, home));                 // Windows: the console only, as v0.1.6
            Assert.Equal(Path.Combine(home, "profile", "crash.log"), Program.RefuseCommandLine(args, error!, hidden: true, windows: false, home));

            // a --save-dir that is no folder: the folder of the --log file, read the same way
            string notAFolder = Path.Combine(home, "file");
            File.WriteAllText(notAFolder, "x");
            string[] logArgs = ["--save-dir", notAFolder, "--log", "~/logs/run.log", "--hidden", "--bogus"];
            Assert.Equal(Path.Combine(home, "logs", "crash.log"), Program.RefuseCommandLine(logArgs, error!, hidden: true, windows: false, home));
            Assert.True(literalExisted || !Directory.Exists(literal), $"a folder named ~ was made in {Path.GetDirectoryName(literal)}");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            if (!literalExisted && Directory.Exists(literal))
            {
                Directory.Delete(literal, recursive: true);
            }
        }
    }
}
