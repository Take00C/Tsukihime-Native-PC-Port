// TsukihimeLauncher on macOS and Linux.
//
//   TsukihimeLauncher [--game DIR] [--data DIR]          the launcher window; one per profile (--data, else the user's profile folder)
//   --after-update PID --updated-from X.Y.Z               given by an update to the launcher it restarts into: wait for the old one to end and for the profile's
//                                                         lock, clean up, then say "Updated from" (as the Windows launcher, LauncherApp.xaml.cs)
//   --render DIR                                          draws every page offscreen to PNG files and exits (verification mode)
//   --selftest [FILE] [--shots DIR]                       reads the folders like the window does and writes one line (verification mode, no window shown)
//   --update-selftest [FILE] --update-api URL [--update-current X.Y.Z] [--shots DIR]
//                                                         checks a loopback stand-in of GitHub, installs what it offers and restarts into the new launcher, which
//                                                         runs --selftest FILE.after.txt; --shots draws the overlay and the new launcher's page (verification mode)
//   --window-smoke                                        the window path itself (lock, clean-up, lifetime, main loop) on the headless platform; quits with 0 once the
//                                                         window is up (verification mode, no window shown)
//   --window-smoke native                                 the same on the real platform (a CI diagnostic for a hosted Mac: it loads libAvaloniaNative and shows the
//                                                         window for half a second)
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Tsuki.App.Services;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher.Avalonia;

internal static class Program
{
    /// <summary>How long a launcher an update restarted into waits for the old one to end.</summary>
    private static readonly TimeSpan OldLauncherWait = TimeSpan.FromSeconds(30);

    [STAThread]
    public static int Main(string[] args)
    {
        string? data = Option(args, "--data");
        string? game = Option(args, "--game");
        string? updateApi = Option(args, "--update-api");
        string? current = Option(args, "--update-current");
        string? render = Option(args, "--render");
        string? shots = Option(args, "--shots");
        int afterUpdate = int.TryParse(Option(args, "--after-update"), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ? pid : 0;
        ReleaseVersion? updatedFrom = ReleaseVersion.TryParse(Option(args, "--updated-from") ?? string.Empty, out ReleaseVersion from) ? from : null;
        var options = new LauncherPaths(data ?? ProfileDirectory.ForUser());
        var model = new UnixLauncherModel(options, AppContext.BaseDirectory, game);
        if (updateApi is not null) { model.UpdateSource = UpdateSource.ForLoopback(new Uri(updateApi)); }
        if (current is not null && ReleaseVersion.TryParse(current, out ReleaseVersion version)) { model.CurrentVersionOverride = version; }
        if (render is not null)
        {
            return UnixOffscreenRender.Run(model, render);
        }

        if (args.Contains("--selftest") || args.Contains("--update-selftest"))
        {
            AppBuilder.Configure<UnixLauncherApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            AvaloniaSynchronizationContext.InstallIfNeeded();
        }

        if (args.Contains("--selftest"))
        {
            WaitForOldLauncher(afterUpdate, model);
            PlatformUpdate.CleanupPrevious(AppContext.BaseDirectory);
            return SelfTest(model, ReportPath(args, "--selftest"), shots, updatedFrom);
        }

        if (args.Contains("--update-selftest"))
        {
            return UpdateSelfTest(model, options, ReportPath(args, "--update-selftest"), shots);
        }

        // the window: one per profile. A launcher an update restarted into waits for the old one, then for its lock; it cleans up only once it holds the lock,
        // so that a second start never cleans up under a launcher that is downloading an update
        WaitForOldLauncher(afterUpdate, model);
        UnixLauncherLock? held = UnixLauncherLock.TryAcquire(options, afterUpdate > 0 ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        UnixLauncherApp.Model = model;
        if (held is null)
        {
            model.Log.Write("launcher: another launcher holds " + Path.Combine(options.DataDirectory, UnixLauncherLock.FileName) + "; this one only says so");
            UnixLauncherApp.AlreadyOpen = true;
            return AppBuilder.Configure<UnixLauncherApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        }

        using (held)
        {
            PlatformUpdate.CleanupPrevious(AppContext.BaseDirectory);
            UnixLauncherApp.UpdatedFrom = updatedFrom;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => UnixLauncherCrash.Write(options, e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()));
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                model.Log.Write("launcher error (unobserved task): " + e.Exception.GetBaseException().Message);
                UnixLauncherCrash.Write(options, e.Exception);
                e.SetObserved();
            };
            (bool smoke, bool headless) = WindowSmoke(args);
            return WindowBuilder(model, headless)
                .StartWithClassicDesktopLifetime(args, (IClassicDesktopStyleApplicationLifetime lifetime) =>
                {
                    // this runs before Avalonia is set up: nothing here may touch Dispatcher.UIThread (the crash hook goes in AfterSetup, WindowBuilder)
                    model.RestartRequested = (exe, _) => RestartInto(exe, RestartArguments(data, game, updateApi, Environment.ProcessId, model.CurrentVersion), held, () => Dispatcher.UIThread.Post(() => lifetime.Shutdown()));
                    if (smoke) { lifetime.Startup += (_, _) => DispatcherTimer.RunOnce(() => lifetime.Shutdown(0), TimeSpan.FromMilliseconds(500)); }
                });
        }
    }

    /// <summary>Whether this is a --window-smoke run, and whether its window is headless (not for "--window-smoke native", nor for a real start).</summary>
    internal static (bool Smoke, bool Headless) WindowSmoke(string[] args)
    {
        bool smoke = args.Contains("--window-smoke");
        return (smoke, smoke && Option(args, "--window-smoke") != "native");
    }

    /// <summary>
    /// The window's AppBuilder: the real platform, or the headless one for --window-smoke. The crash hook goes in once Avalonia is set up: a Dispatcher.UIThread
    /// first used before that belongs to no platform, and the main loop then cannot start (every start crashed that way before this was split out).
    /// </summary>
    internal static AppBuilder WindowBuilder(UnixLauncherModel model, bool headless) =>
        (headless
            ? AppBuilder.Configure<UnixLauncherApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia()
            : AppBuilder.Configure<UnixLauncherApp>().UsePlatformDetect())
        .AfterSetup(_ => HookCrashes(model));

    /// <summary>
    /// Starts the launcher an update installed and lets this one end. The lock goes first, so that the new launcher can take it once this process is gone; when
    /// the start fails the lock is taken back and the exception goes on (PlatformUpdate then puts the old version back).
    /// </summary>
    internal static void RestartInto(string exe, IEnumerable<string> arguments, UnixLauncherLock? held, Action shutdown)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        held?.Release();
        try
        {
            Process.Start(start)!.Dispose();
        }
        catch
        {
            held?.Retake();
            throw;
        }

        shutdown();
    }

    /// <summary>
    /// Errors nothing else caught on the window's thread (an exception of a button, say): written to launcher_crash.log, said in the toast, and the launcher goes on.
    /// Returns what unhooks it.
    /// </summary>
    internal static IDisposable HookCrashes(UnixLauncherModel model)
    {
        DispatcherUnhandledExceptionEventHandler handler = (_, e) =>
        {
            model.ReportCrash(e.Exception);
            e.Handled = true;
        };
        Dispatcher.UIThread.UnhandledException += handler;
        return new Unhook(() => Dispatcher.UIThread.UnhandledException -= handler);
    }

    /// <summary>Waits up to 30 s for the launcher an update replaced (its process id from --after-update) to end.</summary>
    private static void WaitForOldLauncher(int pid, UnixLauncherModel model)
    {
        if (pid <= 0 || pid == Environment.ProcessId) { return; }
        try
        {
            using Process old = Process.GetProcessById(pid);
            if (!old.WaitForExit(OldLauncherWait))
            {
                model.Log.Write($"update: the old launcher (process {pid.ToString(CultureInfo.InvariantCulture)}) has not ended after 30 s");
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // it has ended already
        }
    }

    private static int SelfTest(UnixLauncherModel model, string? report, string? shots, ReleaseVersion? updatedFrom)
    {
        Pump(model.RefreshAsync());
        if (updatedFrom is { } from) { model.SayUpdated(from); }
        string result = $"SELFTEST PASS: {model.NavItems.Count} pages, {model.UpdateButtonLabel}, {model.ReadySummary}, launcher {model.CurrentVersion.Tag}";
        if (updatedFrom is not null) { result += "; " + model.UpdateStatus; }
        if (shots is not null)
        {
            var window = new MainWindow(model);
            window.Show();
            Shot(window, shots, "play");
            window.Close();
        }

        Report(report, result);
        return 0;
    }

    private static int UpdateSelfTest(UnixLauncherModel model, LauncherPaths paths, string? report, string? shots)
    {
        Pump(model.RefreshAsync());
        MainWindow? window = null;
        if (shots is not null)
        {
            window = new MainWindow(model);
            window.Show();
        }

        Pump(model.CheckForUpdatesAsync(manual: true));
        if (model.AvailableUpdate is null)
        {
            Report(report, "UPDATE SELFTEST FAIL: no newer release with this platform's asset; " + model.UpdateStatus);
            return 1;
        }

        if (window is not null) { Shot(window, shots!, "1_update_offer"); }
        model.RestartRequested = (exe, _) =>
        {
            var after = new List<string> { "--selftest" };
            if (report is not null) { after.Add(Path.GetFullPath(report) + ".after.txt"); }
            after.AddRange(["--data", Path.GetFullPath(paths.DataDirectory), "--game", Path.Combine(paths.DataDirectory, "no-game")]);
            if (shots is not null) { after.AddRange(["--shots", Path.Combine(Path.GetFullPath(shots), "after")]); }
            after.AddRange(["--after-update", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--updated-from", model.CurrentVersion.ToString()]);
            RestartInto(exe, after, null, () => { });
        };
        Pump(model.RunUpdateAsync());
        bool updated = model.UpdateStage == UnixUpdateStage.Restarting;
        // drawn once the update's work is over (the new launcher waits for this one to end): a frame drawn inside the restart could be the one before it
        if (window is not null) { Shot(window, shots!, updated ? "2_update_restarting" : "2_update_failed"); }
        window?.Close();
        Report(report, $"{(updated ? "UPDATE SELFTEST PASS" : "UPDATE SELFTEST FAIL")}: {model.UpdateButtonLabel}; {model.UpdateStatus}; {model.UpdateMessage}");
        return updated ? 0 : 1;
    }

    /// <summary>Runs the window's jobs until <paramref name="task"/> is done (the verification modes have no message loop of their own).</summary>
    private static void Pump(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (!task.IsCompleted) { Thread.Sleep(5); }
        }

        Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    private static void Shot(MainWindow window, string folder, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Directory.CreateDirectory(folder);
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The headless renderer returned no frame.");
        frame.Save(Path.Combine(folder, name + ".png"));
    }

    private static string? ReportPath(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
    }

    private static void Report(string? path, string text)
    {
        Console.WriteLine(text);
        if (path is null) { return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text + Environment.NewLine);
    }

    internal static IEnumerable<string> ProfileArguments(string? data, string? game)
    {
        if (data is not null)
        {
            yield return "--data";
            yield return Path.GetFullPath(data);
        }

        if (game is not null)
        {
            yield return "--game";
            yield return Path.GetFullPath(game);
        }
    }

    /// <summary>The arguments of the launcher an update restarts into: the profile and game of this one, its loopback update source, and who it replaces.</summary>
    internal static IEnumerable<string> RestartArguments(string? data, string? game, string? updateApi, int pid, ReleaseVersion from)
    {
        foreach (string argument in ProfileArguments(data, game)) { yield return argument; }
        if (updateApi is not null)
        {
            yield return "--update-api";
            yield return updateApi;
        }

        yield return "--after-update";
        yield return pid.ToString(CultureInfo.InvariantCulture);
        yield return "--updated-from";
        yield return from.ToString();
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private sealed class Unhook(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
