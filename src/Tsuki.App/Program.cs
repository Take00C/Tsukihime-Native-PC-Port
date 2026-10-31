// Tsukihime.exe (project Tsuki.App) entry point (ARCHITECTURE 3.1, 3.4; ROADMAP M0 scope 7; PACE 7.2 #1, H1).
//
//   Tsukihime [options]                          the game (package K6): see AppArgs.Usage
//   Tsukihime --skeleton [--seconds N]           the M0 skeleton: window, 60 Hz loop, Alt+Enter / F11, gamepad hot-plug log
//   Tsukihime --smoke [--visible] [--report F]  M0 acceptance 3; exit 0 only when every check passes
//   Tsukihime --pick-folder [--timeout S]       the native folder dialog; prints the chosen folder
//
// Tsukihime.exe is a WinExe: its output reaches a terminal only through a pipe or redirect (bash waits
// for it; in PowerShell use "& Tsukihime.exe --smoke | Out-Host" so the exit code is kept).
using System.Globalization;
using Tsuki.App.Play;
using Tsuki.App.Services;
using Tsuki.Media.Ffmpeg;
using Tsuki.Platform;
using Tsuki.Render.Gl;

namespace Tsuki.App;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailed = 1;
    private const int ExitUsage = 64;

    [STAThread]
    private static int Main(string[] args)
    {
        // UTF-8 on redirected output, whatever the console code page (ARCHITECTURE 1.11: no code pages)
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        string? dataDirectory = null;
        string? logFile = null;
        bool hidden = args.Contains("--hidden");
        try
        {
            if (args.Any(x => x is "--help" or "-h" or "/?"))
            {
                Console.WriteLine(AppArgs.Usage);
                return ExitOk;
            }

            if (!AppArgs.TryParse(args, out AppArgs? a, out string? error))
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine(AppArgs.Usage);
                return ExitUsage;
            }

            AppArgs parsed = a!;
            dataDirectory = StartupSettings.DataDirectory(parsed);
            logFile = parsed.LogFile;
            hidden = parsed.Hidden;

            // No exit path is silent (package R1): an exception on a worker thread (the trim thread, a decode, the SDL audio callback) ends the process with
            // Windows event 1026 and nothing in the player's hands; it is written to crash.log and shown first. A faulted task nobody awaited is logged and kept.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                ReportFatal(e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "unknown error"), dataDirectory, logFile, hidden);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                CrashLog.Write(dataDirectory, logFile, e.Exception);
                e.SetObserved();
            };

            return parsed.Mode switch
            {
                AppMode.Smoke => Smoke.Run(parsed.Visible, parsed.ReportPath),
                AppMode.PickFolder => PickFolder(parsed.TimeoutSeconds),
                AppMode.Skeleton => RunSkeleton(parsed),
                _ => GameApp.Run(parsed),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Tsukihime failed: " + e);
            ReportFatal(e, dataDirectory, logFile, hidden);
            return ExitFailed;
        }
        finally
        {
            Sdl.Shutdown();
        }
    }

    /// <summary>The last word on a failure the game itself did not report: crash.log, and the message box unless the run is hidden.</summary>
    private static void ReportFatal(Exception e, string? dataDirectory, string? logFile, bool hidden)
    {
        string? crash = CrashLog.Write(dataDirectory, logFile, e);
        if (!hidden)
        {
            // SDL's simple message box works before SDL_Init and after a failed one
            MessageBox.ShowError(AppIdentity.ProductName, e.Message + (crash is null ? string.Empty : Environment.NewLine + Environment.NewLine + "Details: " + crash));
        }
    }

    private static int RunSkeleton(AppArgs a)
    {
        FfmpegVersion ff = FfmpegLibrary.Load();
        Console.WriteLine($"FFmpeg {ff.Build} (libavcodec {ff.Avcodec}) from {FfmpegLibrary.RootPath}");
        using GameWindow window = GameWindow.Create(new WindowOptions(AppIdentity.ProductName, 1280, 720, Fullscreen: a.Fullscreen));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        Console.WriteLine($"{window.Context}; {device.Info}");
        using GamepadSet pads = GamepadSet.Create();
        foreach (GamepadInfo g in pads.Connected)
        {
            Console.WriteLine($"gamepad connected: #{g.InstanceId} {g.Name} ({g.Type})");
        }

        var loop = new MainLoop(window, device, new EventPump(window, pads));
        LoopStats stats = loop.Run(
            _ => true,
            e =>
            {
                switch (e.Kind)
                {
                    case PlatformEventKind.FullscreenToggled:
                        Console.WriteLine($"fullscreen {(e.Fullscreen ? "on" : "off")} ({(e.Accepted ? "accepted" : "refused: " + Sdl.LastError)})");
                        break;
                    case PlatformEventKind.GamepadAdded:
                        Console.WriteLine($"gamepad added: #{e.Gamepad!.InstanceId} {e.Gamepad.Name} ({e.Gamepad.Type}, south {e.Gamepad.SouthLabel})");
                        break;
                    case PlatformEventKind.GamepadRemoved:
                        Console.WriteLine($"gamepad removed: #{e.Gamepad!.InstanceId} {e.Gamepad.Name}");
                        break;
                    case PlatformEventKind.KeyDown when e.Key == "Escape":
                        return false;
                }

                return true;
            },
            a.Seconds);
        Console.WriteLine(stats);
        return ExitOk;
    }

    private static int PickFolder(double timeoutSeconds)
    {
        using GameWindow window = GameWindow.Create(new WindowOptions(AppIdentity.ProductName + ": choose the CUSA47999 folder", 640, 360, Resizable: false));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        FolderDialogRequest request = FolderDialog.Show(window);
        var pump = new EventPump(window, null);
        var loop = new MainLoop(window, device, pump);
        LoopStats stats = loop.Run(_ => !request.IsCompleted, _ => true, timeoutSeconds);
        switch (request.Outcome)
        {
            case FolderDialogOutcome.Chosen:
                Console.WriteLine("chosen: " + request.Path);
                return ExitOk;
            case FolderDialogOutcome.Cancelled:
                Console.WriteLine("cancelled");
                return 2;
            case FolderDialogOutcome.Failed:
                Console.WriteLine("failed: " + request.Error);
                return ExitFailed;
            default:
                Console.WriteLine($"timed out after {stats.WallSeconds.ToString("F1", CultureInfo.InvariantCulture)} s (dialog closed with the process)");
                return 3;
        }
    }
}
