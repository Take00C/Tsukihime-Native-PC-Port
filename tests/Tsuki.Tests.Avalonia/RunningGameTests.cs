using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Tsuki.Launcher.Avalonia;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Data.Cli;
using Tsuki.Tests.Synthetic.Launcher;

namespace Tsuki.Tests.Avalonia;

/// <summary>
/// The tests that start a copy of a program named Tsukihime run alone, after the others: a game whose program cannot be read yet (one that is just starting)
/// counts as running from any folder, so a launcher test elsewhere could find it and refuse its update.
/// </summary>
[CollectionDefinition(nameof(CopiedGames), DisableParallelization = true)]
public sealed class CopiedGames;

/// <summary>A game of the launcher's folder that the launcher did not start (by hand, by Steam): found, followed until it ends, found through a link.</summary>
[Collection(nameof(CopiedGames))]
public sealed class RunningGameTests : IDisposable
{
    private readonly string _root = TestFolders.Temp("tsuki-k48-running-");
    private readonly LauncherPaths _paths;
    private static ReleaseVersion Version => ReleaseFiles.BuildVersion();

    public RunningGameTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new LauncherPaths(Path.Combine(_root, "profile"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ARunningGameDisablesPlayAndRefusesAnUpdateBeforeDownloading()
    {
        string install = Path.Combine(_root, "running");
        Directory.CreateDirectory(install);
        using Process process = CopiedGame(install, out string game);
        try
        {
            using var server = new FakeGitHub();
            string name = PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64);
            string asset = Path.Combine(_root, name);
            File.WriteAllText(asset, "must not be installed");
            string sums = Path.Combine(_root, UpdateSource.SumsAssetName);
            File.WriteAllText(sums, new string('0', 64) + "  " + name + "\n");
            server.List(UpdateSource.DefaultRepository, server.Release(UpdateSource.DefaultRepository, Version.Tag, [(name, asset), (UpdateSource.SumsAssetName, sums)], sizeOverride: 1));
            var model = Model(install, server.Base);
            model.RestartRequested = (_, _) => throw new InvalidOperationException("an update must never reach restart");
            Assert.True(SpinWait.SpinUntil(() => model.GameRunning, TimeSpan.FromSeconds(5)), $"the copied Tsukihime process was not detected: exited={process.HasExited}, name={process.ProcessName}, wanted={game}");
            Assert.False(model.PlayCommand.CanExecute(null));
            Assert.False(model.RestoreCommand.CanExecute(null));
            Assert.False(model.ImportCommand.CanExecute(null));
            await model.CheckForUpdatesAsync(manual: true);
            await model.RunUpdateAsync();
            Assert.Equal(UnixUpdateStage.Offer, model.UpdateStage);
            Assert.Equal(LauncherTexts.UpdateGameRunning(PlatformServices.AppName), model.UpdateMessage);
            Assert.DoesNotContain(server.Requests, request => request.Path.Contains("/download/", StringComparison.Ordinal));
            Assert.Contains(model.Log.Lines(), line => line.EndsWith("update: refused, the game of this folder is running", StringComparison.Ordinal));
        }
        finally
        {
            Stop(process);
        }
    }

    [Fact]
    public void PlayComesBackWhenAGameTheLauncherDidNotStartEnds()
    {
        // nothing tells the launcher when a game started by hand or by Steam ends: it looks again while that game runs
        string install = Path.Combine(_root, "external");
        Directory.CreateDirectory(install);
        using Process process = CopiedGame(install, out _, seconds: 2);
        try
        {
            var raised = new List<string?>();
            UnixLauncherModel watched = Model(install, new Uri("http://127.0.0.1:9/"));
            watched.PropertyChanged += (_, e) => { lock (raised) { raised.Add(e.PropertyName); } };
            Assert.True(SpinWait.SpinUntil(() => watched.GameRunning, TimeSpan.FromSeconds(5)), "the copied Tsukihime process was not detected");
            Assert.Equal("RUNNING", watched.PlayLabel);
            Assert.False(watched.PlayCommand.CanExecute(null));
            process.WaitForExit();
            Assert.True(SpinWait.SpinUntil(() => { lock (raised) { return raised.Contains(nameof(watched.PlayLabel)); } }, TimeSpan.FromSeconds(10)), "PLAY was never announced again after the game ended");
            Assert.Equal("PLAY", watched.PlayLabel);
            Assert.True(watched.PlayCommand.CanExecute(null));
            Assert.True(watched.RestoreCommand.CanExecute(null));
        }
        finally
        {
            Stop(process);
        }
    }

    [Fact]
    public void PlayFollowsTheWindowModeAGameTheLauncherDidNotStartLeft()
    {
        // started from the application menu or Steam while the launcher is open, switched to a window (display_mode=windowed) and quit: the launcher reads
        // settings.ini again, so its switch shows the window and PLAY does not put the game back in fullscreen
        string install = Path.Combine(_root, "menu");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(_paths.DataDirectory);
        File.WriteAllText(_paths.SettingsFile, "display_mode=fullscreen\n");
        using Process process = CopiedGame(install, out _);
        try
        {
            var raised = new List<string?>();
            UnixLauncherModel model = Model(install, new Uri("http://127.0.0.1:9/"), linux: true);
            Assert.True(model.Fullscreen);
            Assert.True(SpinWait.SpinUntil(() => model.GameRunning, TimeSpan.FromSeconds(5)), "the copied Tsukihime process was not detected");
            model.PropertyChanged += (_, e) => { lock (raised) { raised.Add(e.PropertyName); } };
            File.WriteAllText(_paths.SettingsFile, "display_mode=windowed\n");
            Stop(process);
            Assert.True(SpinWait.SpinUntil(() => { lock (raised) { return raised.Contains(nameof(model.PlayLabel)); } }, TimeSpan.FromSeconds(10)), "PLAY was never announced again after the game ended");
            Assert.True(SpinWait.SpinUntil(() => { lock (raised) { return raised.Contains(nameof(model.Fullscreen)); } }, TimeSpan.FromSeconds(10)), "settings.ini was not read again after the game ended");
            Assert.Equal("PLAY", model.PlayLabel);
            Assert.False(model.Fullscreen);
            IReadOnlyList<string> arguments = model.PlayPlan(Path.Combine(install, PlatformServices.AppName), Path.Combine(_root, "no-game")).Arguments;
            Assert.Contains("--windowed", arguments);
            Assert.DoesNotContain("--fullscreen", arguments);
        }
        finally
        {
            Stop(process);
        }
    }

    [Fact]
    public void PlayFollowsTheWindowModeAGameLeftWhileTheLauncherNeverNoticedIt()
    {
        // the launcher is open and idle, so it never looks for a running game: the game is started from the application menu, switched to a window and quit.
        // PLAY's plan still reads settings.ini as it is now, so it does not put the game back in fullscreen
        string install = Path.Combine(_root, "idle");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(_paths.DataDirectory);
        File.WriteAllText(_paths.SettingsFile, "display_mode=fullscreen\n");
        UnixLauncherModel model = Model(install, new Uri("http://127.0.0.1:9/"), linux: true);
        Assert.True(model.Fullscreen);
        File.WriteAllText(_paths.SettingsFile, "display_mode=windowed\n");      // what the game wrote while it ran
        IReadOnlyList<string> arguments = model.PlayPlan(Path.Combine(install, PlatformServices.AppName), Path.Combine(_root, "no-game")).Arguments;
        Assert.Contains("--windowed", arguments);
        Assert.DoesNotContain("--fullscreen", arguments);
        Assert.False(model.Fullscreen);
    }

    [Fact]
    public void OnlyOneLookOfTheWatchSaysThatTheGameEnded()
    {
        // a slow machine (GitHub's Intel Mac) ran two looks at once: both said the game had ended and two reloads crashed into each other
        string install = Path.Combine(_root, "overlap");
        Directory.CreateDirectory(install);
        using Process process = CopiedGame(install, out _);
        try
        {
            UnixLauncherModel model = Model(install, new Uri("http://127.0.0.1:9/"), interval: TimeSpan.FromHours(1));   // the timer never looks by itself
            Assert.True(SpinWait.SpinUntil(() => model.GameRunning, TimeSpan.FromSeconds(5)), "the copied Tsukihime process was not detected");
            Assert.False(model.ExternalWatchTick(null));                       // still running: nothing to say
            Stop(process);
            Assert.True(SpinWait.SpinUntil(() => !GameLauncher.IsGameRunningFrom(install, PlatformServices.AppName), TimeSpan.FromSeconds(10)));
            Assert.True(model.ExternalWatchTick(null));                        // the look that takes the watch says it
            Assert.False(model.ExternalWatchTick(null));                       // a look that overlapped it says nothing
        }
        finally
        {
            Stop(process);
        }
    }

    [Fact]
    public void BackInFrontTheLauncherReadsTheProfileAgainOnlyWhenAFileChanged()
    {
        // the window's Activated: settings.ini or save.dat written since the last read are read again and said; otherwise only the play state is said
        string install = Path.Combine(_root, "front");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(_paths.DataDirectory);
        File.WriteAllText(_paths.SettingsFile, "display_mode=fullscreen\n");
        UnixLauncherModel model = Model(install, new Uri("http://127.0.0.1:9/"), linux: true);
        model.ReadProfileAgain(onlyIfChanged: true);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        model.ReadProfileAgain(onlyIfChanged: true);
        Assert.DoesNotContain(nameof(model.Fullscreen), raised);                  // nothing changed: no read
        Assert.Contains(nameof(model.PlayLabel), raised);
        File.WriteAllText(_paths.SettingsFile, "display_mode=windowed\n");
        File.SetLastWriteTimeUtc(_paths.SettingsFile, DateTime.UtcNow.AddMinutes(1));   // a later stamp even on a coarse file clock
        raised.Clear();
        model.ReadProfileAgain(onlyIfChanged: true);
        Assert.Contains(nameof(model.Fullscreen), raised);
        Assert.False(model.Fullscreen);
    }

    [Fact]
    public void TheWindowReadsTheProfileAgainWhenItComesToTheFront()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "src", "Tsuki.slnx"))) { root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)) ?? throw new InvalidOperationException("no repository above the test"); }
        string source = File.ReadAllText(Path.Combine(root, "src", "Tsuki.Launcher.Avalonia", "MainWindow.axaml.cs"));
        Assert.Contains("Activated += (_, _) => _model.ReadProfileAgain(onlyIfChanged: true);", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePlayButtonComesBackWhenAGameTheLauncherDidNotStartEnds()
    {
        // the same in the window: RUNNING while that game runs, PLAY again by itself once it has ended
        string install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);
        using Process game = CopiedGame(install, out _, seconds: 2);
        UnixLauncherModel model = Model(install, new Uri("http://127.0.0.1:9/"));
        try
        {
            await LauncherHeadlessTests.Run(async () =>
            {
                MainWindow window = LauncherHeadlessTests.Show(model);
                await LauncherHeadlessTests.Until(window, () => Equals(window.PlayButton.Content, "RUNNING"));
                Assert.Equal("RUNNING", window.PlayButton.Content);
                Assert.False(window.PlayButton.IsEffectivelyEnabled);
                await game.WaitForExitAsync();
                await LauncherHeadlessTests.Until(window, () => Equals(window.PlayButton.Content, "PLAY"));
                Assert.Equal("PLAY", window.PlayButton.Content);
                Assert.True(window.PlayButton.IsEffectivelyEnabled);
                Assert.True(model.BackupNowCommand.CanExecute(null));
                window.Close();
            });
        }
        finally
        {
            Stop(game);
        }
    }

    [Fact]
    public void AGameOfTheFolderIsFoundThroughALinkToTheFolder()
    {
        // macOS names a running program by its real path (/private/var/..., while the temporary folder is /var/...), and Linux's /proc does the same
        if (OperatingSystem.IsWindows()) { Assert.Skip("Windows compares the paths as they are written; making a symbolic link there needs extra rights"); }
        string real = Path.Combine(_root, "real");
        string install = Path.Combine(real, "install");
        Directory.CreateDirectory(install);
        string link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, real);
        using Process process = CopiedGame(install, out string game);
        try
        {
            string throughLink = Path.Combine(link, "install");
            Assert.True(SpinWait.SpinUntil(() => GameLauncher.IsGameRunningFrom(throughLink, PlatformServices.AppName), TimeSpan.FromSeconds(5)), $"{game} was not found through {throughLink}");
            Assert.True(GameLauncher.IsGameRunningFrom(install, PlatformServices.AppName));
            Assert.False(GameLauncher.IsGameRunningFrom(Path.Combine(_root, "another-copy"), PlatformServices.AppName));
            Assert.Equal(Path.Combine(install, "Tsukihime"), GameLauncher.ResolveLinks(Path.Combine(throughLink, "Tsukihime")));
        }
        finally
        {
            Stop(process);
        }
    }

    [Fact]
    public void TheStandInGameMacOSRunsIsFoundInItsFolderAndEndsByItself()
    {
        // macOS kills a copied /bin/bash (a launch constraint), so its copied game is the program cc builds: it is built and run on Linux too, wherever cc is
        if (OperatingSystem.IsWindows()) { Assert.Skip("the stand-in is a C program for macOS and Linux; Windows copies cmd.exe"); }
        string install = Path.Combine(_root, "built");
        Directory.CreateDirectory(install);
        StandInProgram.Require();                   // built before the clock starts: a slow first cc run must not stand in for the 3 seconds it sleeps
        var clock = Stopwatch.StartNew();
        using Process process = CopiedGame(install, out string game, seconds: 3, built: true);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => GameLauncher.IsGameRunningFrom(install, PlatformServices.AppName), TimeSpan.FromSeconds(5)), $"{game} was not found running from {install}");
            Assert.Equal(PlatformServices.AppName, process.ProcessName);
            Assert.False(GameLauncher.IsGameRunningFrom(Path.Combine(_root, "another-copy"), PlatformServices.AppName));
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "the stand-in did not end by itself");
            Assert.Equal(0, process.ExitCode);
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(2.5), $"the stand-in ended after {clock.Elapsed}, not the 3 seconds it was given");
            Assert.False(GameLauncher.IsGameRunningFrom(install, PlatformServices.AppName));
        }
        finally
        {
            Stop(process);
        }
    }

    private UnixLauncherModel Model(string install, Uri api, bool? linux = null, TimeSpan? interval = null) => new(_paths, install, Path.Combine(_root, "no-game"))
    {
        IsLinux = linux ?? OperatingSystem.IsLinux(),
        // a desktop, not a Steam Deck (whose launcher leaves the window mode to the game)
        SteamDeck = null,
        UpdateSource = UpdateSource.ForLoopback(api),
        AssetName = version => PlatformUpdate.AssetNameFor(version, "linux", Architecture.X64),
        CurrentVersionOverride = new ReleaseVersion(0, 0, 1),
        // the Linux layout on every host, a Mac included
        MacBundleUpdate = false,
        ExternalGameInterval = interval ?? TimeSpan.FromMilliseconds(100),
    };

    /// <summary>Ends a stand-in game (one the model followed and disposed has ended already).</summary>
    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // disposed after it ended
        }
    }

    /// <summary>
    /// A program named Tsukihime in <paramref name="install"/>, running for <paramref name="seconds"/>: what a game started by hand or by Steam looks like. A copy
    /// of cmd.exe on Windows and of /bin/bash on Linux; on macOS, which kills a copied system program, and with <paramref name="built"/>, the program cc builds.
    /// </summary>
    private static Process CopiedGame(string install, out string game, int seconds = 30, bool? built = null)
    {
        game = Path.Combine(install, PlatformServices.AppName);
        var start = new ProcessStartInfo(game) { UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsWindows())
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), game);
            start.Arguments = $"/c ping 127.0.0.1 -n {seconds + 1} >NUL";
        }
        else if (built ?? OperatingSystem.IsMacOS())
        {
            File.Copy(StandInProgram.Require(), game);
            start.ArgumentList.Add(seconds.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            File.Copy("/bin/bash", game);
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"sleep {seconds} & wait");
        }

        return Process.Start(start) ?? throw new InvalidOperationException("the copied game process could not start");
    }
}
