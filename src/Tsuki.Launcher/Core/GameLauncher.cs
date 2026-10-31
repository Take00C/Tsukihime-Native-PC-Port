// Starting the game (package K17): where Tsukihime.exe (project Tsuki.App; package K20 gave the program the player's name) is, which arguments it gets, and the process. The
// launcher passes the game folder (--game) and a log file (--log) and, since package K18, the window mode (--fullscreen or --windowed, every time); language and prompts
// travel in settings.ini, which the game reads itself.
using System.Diagnostics;

namespace Tsuki.Launcher.Core;

/// <summary>How to start the game once.</summary>
/// <param name="ExePath">Tsukihime.exe.</param>
/// <param name="WorkingDirectory">Its own folder.</param>
/// <param name="Arguments">The argument list (no shell quoting involved).</param>
public sealed record LaunchPlan(string ExePath, string WorkingDirectory, IReadOnlyList<string> Arguments);

/// <summary>Finds and starts Tsukihime.exe.</summary>
public static class GameLauncher
{
    /// <summary>The file name of the game program (the AssemblyName of src/Tsuki.App).</summary>
    public const string AppFileName = "Tsukihime.exe";

    /// <summary>The file name of the launcher (the AssemblyName of src/Tsuki.Launcher); the Start-menu shortcut points at it.</summary>
    public const string LauncherFileName = "TsukihimeLauncher.exe";

    /// <summary>The FFmpeg libraries the game loads (Tsuki.Media.Ffmpeg.FfmpegLibrary.RequiredDlls).</summary>
    public static IReadOnlyList<string> FfmpegDlls { get; } = ["avutil-61.dll", "swresample-7.dll", "avcodec-63.dll", "avformat-63.dll"];

    /// <summary>The exit code of a game that found another game running on the same profile (GameApp.ExitAlreadyRunning): not an error, nothing to see in the log.</summary>
    public const int ExitAlreadyRunning = 3;

    /// <summary>True while a Tsukihime.exe process runs on this machine, started by this launcher or not (package R1: a second copy would fight the first over save.dat).</summary>
    public static bool IsGameRunning()
    {
        try
        {
            Process[] running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppFileName));
            bool any = running.Length > 0;
            foreach (Process p in running)
            {
                p.Dispose();
            }

            return any;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The game program: next to the launcher, else (a development build, where each project has its own output folder) the same place in the Tsuki.App output.
    /// Null when neither exists.
    /// </summary>
    public static string? FindApp(string launcherDirectory)
    {
        string own = Path.Combine(launcherDirectory, AppFileName);
        if (File.Exists(own))
        {
            return own;
        }

        string sibling = launcherDirectory
            .Replace("Tsuki.Launcher", "Tsuki.App", StringComparison.OrdinalIgnoreCase)
            .Replace("net10.0-windows", "net10.0", StringComparison.OrdinalIgnoreCase);
        string candidate = Path.Combine(sibling, AppFileName);
        return !string.Equals(sibling, launcherDirectory, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate) ? candidate : null;
    }

    /// <summary>The player's setup guide beside the programs (package K29): the plain-text copy of docs/SETUP_GUIDE.md that the build and the publish carry.</summary>
    public const string SetupGuideFileName = "SETUP_GUIDE.txt";

    /// <summary>The setup guide next to the launcher, or null when it is not there (the Help page's button then says so instead of failing).</summary>
    public static string? FindSetupGuide(string launcherDirectory)
    {
        string own = Path.Combine(launcherDirectory, SetupGuideFileName);
        return File.Exists(own) ? own : null;
    }

    /// <summary>The FFmpeg DLLs that are missing next to the game program.</summary>
    public static IReadOnlyList<string> MissingFfmpegDlls(string appDirectory) =>
        FfmpegDlls.Where(d => !File.Exists(Path.Combine(appDirectory, d))).ToList();

    /// <summary>The launcher program next to the game program, or null when it is not there (a development build of the game alone).</summary>
    public static string? FindLauncher(string launcherDirectory)
    {
        string own = Path.Combine(launcherDirectory, LauncherFileName);
        return File.Exists(own) ? own : null;
    }

    /// <summary>The plan for a game folder.</summary>
    /// <param name="appExe">Tsukihime.exe.</param>
    /// <param name="gameFolder">The CUSA47999 folder.</param>
    /// <param name="paths">The profile (the log goes beside the saves).</param>
    /// <param name="fullscreen">The launcher's choice, passed as <c>--fullscreen</c> or <c>--windowed</c> on every start (the game remembers it), so that the remembered window mode of an earlier run can never override what the switch showed.</param>
    /// <param name="extra">More arguments, for tests (<c>--hidden --mute</c>) and for support.</param>
    public static LaunchPlan Plan(string appExe, string gameFolder, LauncherPaths paths, bool fullscreen, IEnumerable<string>? extra = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appExe);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        ArgumentNullException.ThrowIfNull(paths);
        var args = new List<string> { "--game", gameFolder, "--log", paths.LastRunLog, fullscreen ? "--fullscreen" : "--windowed" };
        if (!paths.IsUserProfile)
        {
            // a launcher on another profile (--data: the verification runs) starts the game on that profile too, where its pending language and window mode are
            args.Add("--save-dir");
            args.Add(paths.DataDirectory);
        }

        if (extra is not null)
        {
            args.AddRange(extra);
        }

        return new LaunchPlan(appExe, Path.GetDirectoryName(Path.GetFullPath(appExe))!, args);
    }

    /// <summary>Starts the game process (no console window, no shell). The folder of the log file (<c>%APPDATA%\Tsuki</c>) is created first.</summary>
    public static Process Start(LaunchPlan plan, LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.DataDirectory);
        var info = new ProcessStartInfo(plan.ExePath)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (string a in plan.Arguments)
        {
            info.ArgumentList.Add(a);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("the game process did not start");
    }
}
