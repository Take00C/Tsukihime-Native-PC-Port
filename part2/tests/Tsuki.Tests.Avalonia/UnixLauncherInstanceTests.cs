using System.Diagnostics;
using Tsuki.Launcher.Avalonia;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Avalonia;

/// <summary>One launcher per profile, the restart of an update across that rule, and the crash log.</summary>
public sealed class UnixLauncherInstanceTests : IDisposable
{
    private readonly string _root = TestFolders.Temp("tsuki-k48-instance-");

    public UnixLauncherInstanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task TheWindowPathStartsItsMainLoopAndQuitsWithoutACrash()
    {
        // the real program in a child process (Avalonia sets up once per process): the window path of Program.Main on the headless platform, which quits once the
        // window is up. Touching Dispatcher.UIThread before Avalonia's set-up made every start crash in the main loop, and only this path showed it
        string host = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "TsukihimeLauncher.exe" : "TsukihimeLauncher");
        Assert.True(File.Exists(host), host + " is not beside the tests");
        string profile = Path.Combine(_root, "smoke-profile");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "--window-smoke", "--data", profile, "--game", Path.Combine(_root, "no-game") }) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the launcher's window path did not quit within 2 minutes");
        }

        string crash = Path.Combine(profile, UnixLauncherCrash.FileName);
        string said = $"{await output} {await error} {(File.Exists(crash) ? await File.ReadAllTextAsync(crash, TestContext.Current.CancellationToken) : string.Empty)}";
        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}; {said}");
        Assert.False(File.Exists(crash), "the window path wrote " + crash);
        using UnixLauncherLock? after = UnixLauncherLock.TryAcquire(new LauncherPaths(profile));
        Assert.True(after is not null, "the lock outlived the launcher");
    }

    [Fact]
    public void TheWindowSmokeIsHeadlessUnlessItAsksForTheRealPlatform()
    {
        // "--window-smoke native" opens the real window for a moment (a hosted Mac's check that libAvaloniaNative loads); the plain one and CI's stay headless
        Assert.Equal((false, false), Program.WindowSmoke(["--data", "p", "--game", "g"]));
        Assert.Equal((true, true), Program.WindowSmoke(["--window-smoke", "--data", "p"]));
        Assert.Equal((true, false), Program.WindowSmoke(["--window-smoke", "native", "--data", "p"]));
        Assert.Equal((true, true), Program.WindowSmoke(["--data", "native", "--window-smoke"]));
    }

    [Fact]
    public void OneLauncherPerProfile()
    {
        var paths = new LauncherPaths(Path.Combine(_root, "profile"));
        using UnixLauncherLock first = Assert.IsType<UnixLauncherLock>(UnixLauncherLock.TryAcquire(paths));
        Assert.True(first.Held);
        Assert.Null(UnixLauncherLock.TryAcquire(paths));
        // another profile (--data) is another launcher
        using UnixLauncherLock other = Assert.IsType<UnixLauncherLock>(UnixLauncherLock.TryAcquire(new LauncherPaths(Path.Combine(_root, "other"))));
        first.Release();
        using UnixLauncherLock second = Assert.IsType<UnixLauncherLock>(UnixLauncherLock.TryAcquire(paths));
        Assert.False(first.Retake());
    }

    [Fact]
    public async Task TheLauncherAnUpdateStartsWaitsForTheLock()
    {
        var paths = new LauncherPaths(Path.Combine(_root, "profile"));
        UnixLauncherLock old = UnixLauncherLock.TryAcquire(paths)!;
        Task<UnixLauncherLock?> next = Task.Run(() => UnixLauncherLock.TryAcquire(paths, TimeSpan.FromSeconds(20)));
        await Task.Delay(600, TestContext.Current.CancellationToken);
        Assert.False(next.IsCompleted);
        old.Dispose();
        using UnixLauncherLock taken = Assert.IsType<UnixLauncherLock>(await next.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        Assert.True(taken.Held);
    }

    [Fact]
    public void TheRestartLetsTheLockGoAndTakesItBackWhenTheNewLauncherDoesNotStart()
    {
        var paths = new LauncherPaths(Path.Combine(_root, "profile"));
        using UnixLauncherLock held = UnixLauncherLock.TryAcquire(paths)!;
        bool shut = false;
        Exception? error = Record.Exception(() => Program.RestartInto(Path.Combine(_root, "missing", "TsukihimeLauncher"), [], held, () => shut = true));
        Assert.NotNull(error);
        Assert.False(shut);
        Assert.True(held.Held);
        Assert.Null(UnixLauncherLock.TryAcquire(paths));

        // macOS has no /bin/true; /usr/bin/true is on macOS and Linux alike
        string exe = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/usr/bin/true";
        Program.RestartInto(exe, OperatingSystem.IsWindows() ? ["/c", "exit 0"] : [], held, () => shut = true);
        Assert.True(shut);
        Assert.False(held.Held);
        using UnixLauncherLock next = Assert.IsType<UnixLauncherLock>(UnixLauncherLock.TryAcquire(paths));
    }

    [Fact]
    public void ACrashIsAppendedToTheProfilesCrashLog()
    {
        var paths = new LauncherPaths(Path.Combine(_root, "profile"));
        string? log = UnixLauncherCrash.Write(paths, new InvalidOperationException("first"));
        UnixLauncherCrash.Write(paths, new IOException("second."));
        Assert.Equal(Path.Combine(paths.DataDirectory, "launcher_crash.log"), log);
        string text = File.ReadAllText(log!);
        Assert.Contains("System.InvalidOperationException: first", text, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException: second.", text, StringComparison.Ordinal);
        Assert.Equal("The launcher hit an error: second. Details are in " + log + ".", UnixLauncherCrash.Message(new IOException("second."), log));
        Assert.Equal("The launcher hit an error: first.", UnixLauncherCrash.Message(new InvalidOperationException("first"), null));
    }
}
