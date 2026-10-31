// TsukihimeLauncher.exe entry (package K17; package K20 renamed the program from TsukiLauncher.exe).
//
//   TsukihimeLauncher.exe [--game DIR] [--data DIR]       the launcher window (--game replaces the remembered folder, --data the profile folder %APPDATA%\Tsuki)
//   TsukihimeLauncher.exe --selftest FILE --data DIR [--game DIR]
//                                                    drives the view model like the pages do over a temporary profile and writes the checks to FILE (verification mode)
//   TsukihimeLauncher.exe --render OUTDIR [--game DIR] [--data DIR] [--page NAME] [--height PIXELS]
//                                                    draws every page offscreen to PNG files and exits; no window is ever shown (verification mode)
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher;

/// <summary>The WPF application.</summary>
public partial class LauncherApp : Application
{
    private const string Usage = "usage: TsukihimeLauncher [--game DIR] [--data DIR] [--render OUTDIR [--page NAME] [--height PIXELS]] [--selftest FILE]";

    private LauncherPaths _paths = LauncherPaths.ForUser();
    private Mutex? _instance;

    /// <inheritdoc/>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Report(args.Exception, _paths);
            args.Handled = true;
        };

        string? game = null;
        string? data = null;
        string? render = null;
        string? only = null;
        string? selfTest = null;
        string? bad = null;
        for (int i = 0; i < e.Args.Length && bad is null; i++)
        {
            switch (e.Args[i])
            {
                case "--game" when i + 1 < e.Args.Length:
                    game = e.Args[++i];
                    break;
                case "--data" when i + 1 < e.Args.Length:
                    data = e.Args[++i];
                    break;
                case "--render" when i + 1 < e.Args.Length:
                    render = e.Args[++i];
                    break;
                case "--page" when i + 1 < e.Args.Length:
                    only = e.Args[++i];
                    break;
                case "--selftest" when i + 1 < e.Args.Length:
                    selfTest = e.Args[++i];
                    break;
                case "--height" when i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out int height) && height is >= 400 and <= 4000:
                    OffscreenRender.Height = height;
                    i++;
                    break;
                default:
                    bad = e.Args[i];
                    break;
            }
        }

        if (bad is not null)
        {
            // the verification modes run with nobody watching: a modal box there would hang the harness that started them (package R1)
            string message = $"{Usage}{Environment.NewLine}unknown or incomplete argument: {bad}";
            if (render is not null || selfTest is not null)
            {
                Console.Error.WriteLine(message);
                WriteUsageError(render, selfTest, message);
            }
            else
            {
                MessageBox.Show(message, "Tsukihime Launcher");
            }

            Shutdown(64);
            return;
        }

        LauncherPaths paths = data is null ? LauncherPaths.ForUser() : new LauncherPaths(Path.GetFullPath(data));
        _paths = paths;
        var model = new LauncherModel(paths, AppContext.BaseDirectory, game);
        if (selfTest is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                string report = await OffscreenRender.SelfTestAsync(model, paths, game);
                File.WriteAllText(Path.GetFullPath(selfTest), report);
                Shutdown(report.Contains("FAIL", StringComparison.Ordinal) ? 1 : 0);
            });
            return;
        }

        if (render is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                int code = 0;
                try
                {
                    await OffscreenRender.RunAsync(model, Path.GetFullPath(render), only);
                }
                catch (Exception ex)
                {
                    // a WinExe has no console: the reason goes beside the pictures
                    Directory.CreateDirectory(render);
                    File.WriteAllText(Path.Combine(render, "render_error.txt"), ex.ToString());
                    code = 1;
                }

                Shutdown(code);
            });
            return;
        }

        // one launcher window per user: a second start brings the first one forward and leaves (package R1)
        _instance = new Mutex(true, @"Local\TsukihimeLauncher", out bool first);
        if (!first)
        {
            _instance.Dispose();
            _instance = null;
            if (!ActivateOther())
            {
                MessageBox.Show("The Tsukihime launcher is already open.", "Tsukihime Launcher");
            }

            Shutdown(0);
            return;
        }

        var window = new MainWindow(model);
        MainWindow = window;
        window.Show();
        _ = model.RefreshAsync();
    }

    /// <inheritdoc/>
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static void WriteUsageError(string? render, string? selfTest, string message)
    {
        try
        {
            if (render is not null)
            {
                Directory.CreateDirectory(render);
                File.WriteAllText(Path.Combine(render, "render_error.txt"), message);
            }

            if (selfTest is not null)
            {
                File.WriteAllText(Path.GetFullPath(selfTest), "FAIL  " + message + Environment.NewLine);
            }
        }
        catch (Exception io) when (io is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // the console line is all that can be said
        }
    }

    /// <summary>Brings the window of the launcher that is already running to the front; false when none could be found.</summary>
    private static bool ActivateOther()
    {
        try
        {
            using Process self = Process.GetCurrentProcess();
            foreach (Process p in Process.GetProcessesByName(self.ProcessName))
            {
                using (p)
                {
                    if (p.Id == self.Id || p.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    _ = NativeMethods.ShowWindow(p.MainWindowHandle, NativeMethods.SwRestore);
                    return NativeMethods.SetForegroundWindow(p.MainWindowHandle);
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // the other launcher is leaving or cannot be reached
        }

        return false;
    }

    private static void Report(Exception e, LauncherPaths paths)
    {
        string? log = null;
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            log = Path.Combine(paths.DataDirectory, "launcher_crash.log");
            File.AppendAllText(log, $"{DateTime.Now:s}  {e}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception io) when (io is IOException or UnauthorizedAccessException)
        {
            log = null;
        }

        MessageBox.Show(e.Message + (log is null ? string.Empty : "\n\nDetails: " + log), "Tsukihime Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static class NativeMethods
    {
        public const int SwRestore = 9;

        // DllImport, not LibraryImport: the launcher compiles without unsafe code
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
