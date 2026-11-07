using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tsuki.Launcher.Avalonia;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Data.Cli;
using Tsuki.Tests.Synthetic.Launcher;

namespace Tsuki.Tests.Avalonia;

/// <summary>The macOS / Linux launcher's model without a window: notifications, the update's gates and sentences, and the save actions' guards.</summary>
public sealed class UnixLauncherStateTests : IDisposable
{
    // the real temporary folder: macOS reports a running game under /private/var, not through the /var link (ARunningGameDisablesPlay...)
    private readonly string _root = TestFolders.Temp("tsuki-k48-state-");
    private readonly LauncherPaths _paths;
    private static ReleaseVersion Version => ReleaseFiles.BuildVersion();

    public UnixLauncherStateTests()
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
    public async Task EachChangeIsAnnouncedUnderItsOwnName()
    {
        using var server = new FakeGitHub();
        server.List(UpdateSource.DefaultRepository);
        var model = Model("status", server.Base);
        var properties = new List<string?>();
        model.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        model.FolderText = "a different folder";
        Assert.Contains(nameof(model.FolderText), properties);
        await model.CheckForUpdatesAsync(manual: true);
        Assert.Contains("newest version", model.UpdateStatus, StringComparison.Ordinal);
        Assert.Contains(nameof(model.UpdateStatus), properties);

        // names outside the fixed list of a full refresh: a lost [CallerMemberName] (round 5) or a setter that stops notifying shows here
        properties.Clear();
        model.Notify("hello");
        Assert.Equal("hello", model.Toast);
        Assert.Contains(nameof(model.Toast), properties);
        Assert.Contains(nameof(model.ToastVisible), properties);
        Assert.DoesNotContain(null, properties);
        properties.Clear();
        model.Page = LauncherPage.Saves;
        Assert.Equal([nameof(model.Page), nameof(model.SelectedNav)], properties);
    }

    [Fact]
    public async Task ARefusedUpdateAnnouncesItsMessageAndStage()
    {
        using var server = new FakeGitHub();
        Offer(server, size: 1);
        var model = Model("refused", server.Base);
        await model.CheckForUpdatesAsync(manual: true);
        var properties = new List<string?>();
        model.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        await model.RunUpdateAsync();
        Assert.Equal(UnixLauncherModel.CannotRestart, model.UpdateMessage);
        Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
        Assert.Contains(nameof(model.UpdateMessage), properties);
        Assert.Contains(nameof(model.UpdateMessageVisible), properties);
        Assert.Contains(nameof(model.UpdateStage), properties);
        Assert.Contains(nameof(model.UpdatePrimaryLabel), properties);
        Assert.DoesNotContain(null, properties);
    }

    [Fact]
    public async Task AnUpdateFoundLaterAnnouncesTheOverlaysText()
    {
        using var server = new FakeGitHub();
        Offer(server, size: 1);
        var model = Model("found", server.Base);
        var properties = new List<string?>();
        model.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        await model.CheckForUpdatesAsync(manual: false);
        foreach (string name in new[] { nameof(model.UpdateOverlayTitle), nameof(model.UpdateVersionLine), nameof(model.UpdateReleaseTitle), nameof(model.UpdateNotes) })
        {
            Assert.Contains(name, properties);
        }

        Assert.Equal("Update to " + Version.Tag, model.UpdateOverlayTitle);
        Assert.False(model.UpdateOverlayVisible);
    }

    [Fact]
    public async Task AGameStartedWhileTheUpdateDownloadsStopsItBeforeAnyFileIsReplaced()
    {
        string install = LinuxRelease.Folder(_root, "install");
        string[] before = Snapshot(install);
        using var server = new FakeGitHub();
        ServeRelease(server, LinuxRelease.Folder(_root, "release"));
        var model = Model(install, server.Base, absolute: true);
        model.RestartRequested = (_, _) => throw new InvalidOperationException("an update must never reach restart");
        await model.CheckForUpdatesAsync(manual: true);
        using Process game = Sleeper();
        try
        {
            Task update = model.RunUpdateAsync();
            Assert.Equal(UnixUpdateStage.Working, model.UpdateStage);
            model.Track(game);
            await update;
            Assert.Equal(UnixUpdateStage.Offer, model.UpdateStage);
            Assert.Equal(LauncherTexts.UpdateGameStartedMeanwhile, model.UpdateMessage);
            Assert.Equal(before, Snapshot(install));
            Assert.False(Directory.Exists(Path.Combine(install, UpdateStaging.FolderName)));
            Assert.Contains(server.Requests, request => request.Path.Contains("/download/", StringComparison.Ordinal));
        }
        finally
        {
            Stop(game);
        }
    }

    [Fact]
    public async Task CancelIsNotAFailure()
    {
        string install = LinuxRelease.Folder(_root, "install");
        string[] before = Snapshot(install);
        using var server = new FakeGitHub();
        ServeRelease(server, LinuxRelease.Folder(_root, "release"));
        var model = Model(install, server.Base, absolute: true);
        model.RestartRequested = (_, _) => throw new InvalidOperationException("a cancelled update must never restart");
        await model.CheckForUpdatesAsync(manual: true);
        model.Notify(string.Empty);
        Task update = model.RunUpdateAsync();
        Assert.True(model.CancelUpdateCommand.CanExecute(null));
        model.CancelUpdateCommand.Execute(null);
        await update;
        Assert.Equal(UnixUpdateStage.Offer, model.UpdateStage);
        Assert.Equal("The download was cancelled. Nothing was changed.", model.UpdateMessage);
        Assert.Equal(string.Empty, model.Toast);
        Assert.Equal("Update now", model.UpdatePrimaryLabel);
        Assert.Equal(before, Snapshot(install));
        Assert.Contains(model.Log.Lines(), line => line.EndsWith("update: cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedDownloadSaysNothingWasChangedAndIsLogged()
    {
        string install = LinuxRelease.Folder(_root, "install");
        using var server = new FakeGitHub();
        Offer(server, size: null);
        var model = Model(install, server.Base, absolute: true);
        model.RestartRequested = (_, _) => throw new InvalidOperationException("a refused update must never restart");
        await model.CheckForUpdatesAsync(manual: true);
        await model.RunUpdateAsync();
        Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
        Assert.StartsWith("The update did not work: the downloaded ", model.UpdateMessage, StringComparison.Ordinal);
        Assert.EndsWith(". Nothing was changed; the launcher you have keeps working.", model.UpdateMessage, StringComparison.Ordinal);
        Assert.Contains(model.Log.Lines(), line => line.Contains("update failed: the downloaded ", StringComparison.Ordinal));
        Assert.Contains(model.Log.Lines(), line => line.Contains("update: " + Version.Tag + " into ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TooLittleRoomRefusesBeforeDownloading()
    {
        string install = LinuxRelease.Folder(_root, "install");
        using var server = new FakeGitHub();
        Offer(server, size: long.MaxValue / 8);
        var model = Model(install, server.Base, absolute: true);
        model.RestartRequested = (_, _) => throw new InvalidOperationException("never");
        await model.CheckForUpdatesAsync(manual: true);
        await model.RunUpdateAsync();
        Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
        Assert.StartsWith("There is not enough room for the update in " + install + ": ", model.UpdateMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(server.Requests, request => request.Path.Contains("/download/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLastUpdatesFilesStillInUseRefuseANewOne()
    {
        string install = LinuxRelease.Folder(_root, "install");
        var staging = new UpdateStaging(install);
        Directory.CreateDirectory(staging.Root);
        File.WriteAllText(staging.Journal, "old\t" + Path.Combine(install, "Tsukihime") + "\n");
        using var server = new FakeGitHub();
        Offer(server, size: 1);
        var model = Model(install, server.Base, absolute: true);
        model.RestartRequested = (_, _) => throw new InvalidOperationException("never");
        await model.CheckForUpdatesAsync(manual: true);
        using (new FileStream(staging.Journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await model.RunUpdateAsync();
        }

        Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
        Assert.Equal(LauncherTexts.UpdateLastStillInUse, model.UpdateMessage);
        Assert.True(File.Exists(staging.Journal));
        Assert.DoesNotContain(server.Requests, request => request.Path.Contains("/download/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFolderThatCannotBeWrittenIsNamedWithTheReleaseToDownload()
    {
        using var server = new FakeGitHub();
        Offer(server, size: 1);
        string missing = Path.Combine(_root, "not-there", "install");
        var model = Model(missing, server.Base, absolute: true);
        model.RestartRequested = (_, _) => throw new InvalidOperationException("never");
        await model.CheckForUpdatesAsync(manual: true);
        await model.RunUpdateAsync();
        Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
        Assert.Equal(UnixUpdateChecks.Message(missing, mac: false, translocated: false, PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64)), model.UpdateMessage);
        Assert.Contains("Tsukihime-" + Version.Tag + "-linux-x64.tar.gz", model.UpdateMessage, StringComparison.Ordinal);
        Assert.Contains("Your saves and settings stay where they are.", model.UpdateMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void OnAMacBothTheBundleAndItsFolderMustBeWritable()
    {
        string folder = Path.Combine(_root, "Applications");
        Directory.CreateDirectory(folder);
        string bundle = Path.Combine(folder, "Tsukihime.app", "Contents", "MacOS");
        // the folder of the bundle can be written, the bundle itself not (it is missing here): a Mac update is refused, Linux only looks at its one folder
        UnixUpdateChecks.Problem problem = Assert.IsType<UnixUpdateChecks.Problem>(UnixUpdateChecks.NotWritable(bundle, folder, mac: true, "Tsukihime-v0.1.7-macos-arm64.zip"));
        Assert.Equal(bundle, problem.Folder);
        Assert.StartsWith("The folder of Tsukihime.app cannot be changed by your account", problem.Message, StringComparison.Ordinal);
        Assert.Null(UnixUpdateChecks.NotWritable(bundle, folder, mac: false, "x"));
        // and the bundle writable, its folder not
        Directory.CreateDirectory(bundle);
        string gone = Path.Combine(_root, "gone");
        Assert.Equal(gone, UnixUpdateChecks.NotWritable(bundle, gone, mac: true, "x")!.Folder);
        Assert.Null(UnixUpdateChecks.NotWritable(bundle, folder, mac: true, "x"));
        // a copy macOS runs from App Translocation (opened straight from Downloads) is told to move to Applications
        string translocated = "/private/var/folders/q1/T/AppTranslocation/0A1B/d/Tsukihime.app/Contents/MacOS";
        Assert.True(UnixUpdateChecks.IsTranslocated(translocated));
        Assert.False(UnixUpdateChecks.IsTranslocated(bundle));
        Assert.Contains("move Tsukihime.app into Applications with Finder", UnixUpdateChecks.NotWritable(translocated, translocated, mac: true, "x")!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMacUpdateWritesBesideTheBundleAndLinuxIntoTheLaunchersFolder()
    {
        string folder = Path.Combine(_root, "Apps");
        string launcher = Path.Combine(folder, "Tsukihime.app", "Contents", "MacOS");
        Directory.CreateDirectory(launcher);
        Assert.Equal(Path.GetFullPath(folder), UnixUpdateChecks.UpdateFolder(launcher, mac: true));
        Assert.Equal(launcher, UnixUpdateChecks.UpdateFolder(launcher, mac: false));
        // the staging folder lies in that folder, so a fresh Mac install (no staging folder yet) can be updated
        Assert.Equal(Path.GetFullPath(folder), Path.GetDirectoryName(PlatformUpdate.MacStaging(PlatformUpdate.MacBundle(launcher)).AppDirectory));
        Assert.Null(UnixUpdateChecks.NotWritable(launcher, UnixUpdateChecks.UpdateFolder(launcher, mac: true), mac: true, "x"));
    }

    [Fact]
    public void TheSpaceCheckMeasuresTheUpdatesOwnFolder()
    {
        Assert.Null(UnixUpdateChecks.SpaceProblem(_root, 1024));
        Assert.Null(UnixUpdateChecks.SpaceProblem(_root, 0));
        string problem = Assert.IsType<string>(UnixUpdateChecks.SpaceProblem(_root, long.MaxValue / 8));
        Assert.StartsWith("There is not enough room for the update in " + _root + ": it needs about ", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsRestoredOrImportedWithoutAWindowToAsk()
    {
        WriteSaveAndBackup();
        var model = Model("restore", new Uri("http://127.0.0.1/"));
        await model.RefreshAsync();
        model.RestoreCommand.Execute(Assert.Single(model.Backups));
        await model.ImportAsync(Path.Combine(_root, "savedata000"));
        Assert.Equal("current save", File.ReadAllText(_paths.SaveFile));
        Assert.Equal(UnixLauncherModel.NobodyToAsk, model.Toast);
        Assert.Single(Directory.GetDirectories(_paths.BackupsDirectory));
    }

    [Fact]
    public async Task ADeclinedRestoreWritesNothingAndAnAcceptedOneReplacesTheSave()
    {
        WriteSaveAndBackup();
        var model = Model("restore", new Uri("http://127.0.0.1/"));
        await model.RefreshAsync();
        BackupRow row = Assert.Single(model.Backups);
        bool answer = false;
        LauncherQuestion? asked = null;
        model.QuestionRequested += question => { asked = question; return Task.FromResult(answer); };
        model.RestoreCommand.Execute(row);
        Assert.Equal("Restore this backup?", asked?.Title);
        Assert.Equal("current save", File.ReadAllText(_paths.SaveFile));
        Assert.False(File.Exists(_paths.SettingsFile));
        Assert.Equal(["20260101-000000"], Directory.GetDirectories(_paths.BackupsDirectory).Select(Path.GetFileName));
        answer = true;
        await model.RestoreAsync(row.Info);
        Assert.Equal("old save", File.ReadAllText(_paths.SaveFile));
        Assert.Equal(2, Directory.GetDirectories(_paths.BackupsDirectory).Length);
    }

    [Fact]
    public async Task ImportAsksBeforeItReplacesTheSave()
    {
        WriteSaveAndBackup();
        var model = Model("import", new Uri("http://127.0.0.1/"));
        LauncherQuestion? asked = null;
        model.QuestionRequested += question => { asked = question; return Task.FromResult(false); };
        await model.ImportAsync(Path.Combine(_root, "savedata000"));
        Assert.Equal(LauncherTexts.ImportQuestion(Path.Combine(_root, "savedata000")), (asked?.Title, asked?.Body, asked?.ConfirmLabel));
        Assert.Equal("current save", File.ReadAllText(_paths.SaveFile));

        model.ImportRequested += () => Task.FromResult<string?>(Path.Combine(_root, "chosen.img"));
        model.ImportCommand.Execute(null);
        Assert.EndsWith("chosen.img is read (the file itself is never changed). Its slots and system data replace save.dat; your current save is backed up first.", asked?.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSaveActionsWaitForTheGame()
    {
        WriteSaveAndBackup();
        var model = Model("waiting", new Uri("http://127.0.0.1/"));
        await model.RefreshAsync();
        BackupRow row = Assert.Single(model.Backups);
        bool asked = false;
        model.QuestionRequested += _ => { asked = true; return Task.FromResult(true); };
        int restoreRaised = 0;
        model.RestoreCommand.CanExecuteChanged += (_, _) => restoreRaised++;
        Assert.True(model.RestoreCommand.CanExecute(row));
        using Process game = Sleeper();
        try
        {
            model.Track(game);
            Assert.True(restoreRaised > 0, "a Restore button bound before PLAY must ask again whether it can be pressed");
            Assert.False(model.RestoreCommand.CanExecute(row));
            Assert.False(model.BackupNowCommand.CanExecute(null));
            Assert.Equal("RUNNING", model.PlayLabel);
            await model.RestoreAsync(row.Info);
            await model.ImportAsync(Path.Combine(_root, "savedata000"));
            Assert.False(asked);
            Assert.Equal(UnixLauncherModel.CloseGameFirst, model.Toast);
            Assert.Equal("current save", File.ReadAllText(_paths.SaveFile));
            Assert.Single(Directory.GetDirectories(_paths.BackupsDirectory));
        }
        finally
        {
            Stop(game);
        }
    }

    [Fact]
    public async Task AnInvalidFolderCannotReplaceTheRememberedOne()
    {
        string remembered = Path.Combine(_root, "remembered");
        Directory.CreateDirectory(remembered);
        Assert.True(_paths.WriteGameDirectory(remembered));
        var model = Model("folders", new Uri("http://127.0.0.1/"));
        await model.RefreshAsync();
        string before = model.Folder.Path!;
        model.FolderText = Path.Combine(_root, "not-a-game");
        await model.ApplyFolderText();
        Assert.Equal(before, model.Folder.Path);
        Assert.Equal(Path.GetFullPath(remembered), _paths.ReadGameDirectory());
        Assert.Contains("not remembered", model.FolderMessage, StringComparison.Ordinal);
        Assert.True(model.FolderMessageVisible);
    }

    [Fact]
    public async Task TheReadyCheckSaysHowManyThingsNeedAttentionInGoodEnglish()
    {
        var model = Model("ready", new Uri("http://127.0.0.1/"));
        await model.RefreshAsync();
        Assert.Equal(LauncherTexts.ReadySummary(model.ReadyRows), model.ReadySummary);
        Assert.Matches(@"^(All set|1 thing needs attention|[2-9] things need attention)$", model.ReadySummary);
    }

    [Fact]
    public void OnLinuxTheFullscreenSwitchIsAChoiceASteamDeckStartFollows()
    {
        const string game = "/games/CUSA47999";
        const string exe = "/opt/Tsukihime/Tsukihime";
        // a desktop: a fresh profile is windowed (the save's byte), PLAY passes the switch, and the switch writes display_mode as well
        UnixLauncherModel desktop = Platform(linux: true, steamDeck: null);
        Assert.False(desktop.Fullscreen);
        Assert.Equal(["--game", game, "--log", _paths.LastRunLog, "--windowed", "--save-dir", _paths.DataDirectory], desktop.PlayPlan(exe, game).Arguments);
        desktop.Fullscreen = true;
        Assert.Equal(["fullscreen=on", "display_mode=fullscreen"], SettingsLines());
        Assert.Contains("--fullscreen", desktop.PlayPlan(exe, game).Arguments);
        // the game's own choice (F11 or Config writes display_mode on Linux) is what the switch and the footer show
        File.AppendAllText(_paths.SettingsFile, "display_mode=windowed\n");
        UnixLauncherModel later = Platform(linux: true, steamDeck: null);
        Assert.False(later.Fullscreen);
        Assert.Contains("Windowed", later.StatusLine, StringComparison.Ordinal);

        // a Steam Deck: PLAY leaves the window mode to the game's Deck rule, and the switch shows the Deck's fullscreen default until the player chooses
        File.Delete(_paths.SettingsFile);
        UnixLauncherModel deck = Platform(linux: true, steamDeck: "1");
        Assert.True(deck.Fullscreen);
        Assert.StartsWith("Fullscreen", deck.StatusLine, StringComparison.Ordinal);
        Assert.Equal(["--game", game, "--log", _paths.LastRunLog, "--save-dir", _paths.DataDirectory], deck.PlayPlan(exe, game).Arguments);
        Assert.False(File.Exists(_paths.SettingsFile), "PLAY must never write display_mode");
        deck.Fullscreen = false;
        Assert.Equal(["fullscreen=off", "display_mode=windowed"], SettingsLines());
        Assert.False(deck.Fullscreen);
        Assert.DoesNotContain("--windowed", deck.PlayPlan(exe, game).Arguments);

        // macOS keeps v0.1.6's rule: the pending key and the save's byte, every PLAY with the mode, no display_mode
        File.Delete(_paths.SettingsFile);
        UnixLauncherModel mac = Platform(linux: false, steamDeck: "1");
        mac.Fullscreen = true;
        Assert.Equal(["fullscreen=on"], SettingsLines());
        Assert.Contains("--fullscreen", mac.PlayPlan(exe, game).Arguments);
        // and the Windows launcher's plan is v0.1.6's
        Assert.Equal(["--game", game, "--log", _paths.LastRunLog, "--windowed", "--save-dir", _paths.DataDirectory], GameLauncher.Plan(exe, game, _paths, fullscreen: false).Arguments);
    }

    [Fact]
    public async Task TheApplicationMenuEntryStartsTheGameInItsFolderWithTheFace()
    {
        string install = Path.Combine(_root, "Tsuki hime");
        Directory.CreateDirectory(install);
        string entry = Path.Combine(_root, "applications", "tsukihime.desktop");
        var model = new UnixLauncherModel(_paths, install + Path.DirectorySeparatorChar, Path.Combine(_root, "no-game")) { IsLinux = true, DesktopEntryPath = entry, MacBundleUpdate = false };
        Assert.True(model.ShortcutSupported);
        Assert.Equal("Create", model.ShortcutLabel);
        // the face of a ready game folder (a stand-in picture here) is saved for the entry; a bitmap needs Avalonia's platform, which the headless session sets
        // up (without it the test failed whenever it ran before every window test)
        await LauncherHeadlessTests.Run(async () =>
        {
            using var face = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            await model.UseGameIconAsync(Path.Combine(_root, "game"), face);
            string png = Assert.IsType<string>(model.GameIconPng);
            Assert.True(File.Exists(png));
            model.ShortcutCommand.Execute(null);
            string text = File.ReadAllText(entry);
            // the game, as the launcher's texts and the Windows desktop shortcut say, not TsukihimeLauncher
            Assert.Equal(Path.Combine(install, "Tsukihime"), EntryValue(text, "Exec"));
            Assert.Equal(install, EntryValue(text, "Path"));
            Assert.Equal(png, EntryValue(text, "Icon"));
            Assert.Equal("Tsukihime", EntryValue(text, "Name"));
            Assert.Equal("Replace", model.ShortcutLabel);
            Assert.Equal("Tsukihime is in your application menu. It starts the game without the launcher.", model.ShortcutText);
        });
        Assert.False(new UnixLauncherModel(_paths, install, Path.Combine(_root, "no-game")) { IsLinux = false, DesktopEntryPath = entry }.ShortcutSupported);
    }

    [Theory]
    [InlineData("/home/deck/Games/Tsukihime/Tsukihime")]
    [InlineData("/home/deck/100% Tsukihime/Tsukihime")]
    [InlineData("/home/deck/$HOME `x` \"q\"/Tsukihime")]
    [InlineData("/home/deck/back\\slash/Tsukihime")]
    public void TheEntryIsWrittenAsTheDesktopEntrySpecificationReadsIt(string exe)
    {
        string folder = exe[..exe.LastIndexOf('/')];
        string text = PlatformServices.DesktopEntry(exe, folder + "/icon.png", folder);
        Assert.Equal(exe, EntryValue(text, "Exec"));
        Assert.Equal(folder, EntryValue(text, "Path"));
        Assert.Equal(folder + "/icon.png", EntryValue(text, "Icon"));
    }

    [Fact]
    public void AMacNamesTheKeysOfItsOwnKeyboard()
    {
        var mac = new UnixLauncherModel(_paths, _root) { IsMac = true };
        var linux = new UnixLauncherModel(_paths, _root) { IsMac = false };
        // Linux has the Windows table, apart from the Print Screen sentence of the screenshot row (the desktop keeps that key there)
        Assert.Equal(KeyTable.Rows.Count, linux.Keys.Count);
        foreach ((KeyRow table, KeyRow shown) in KeyTable.Rows.Zip(linux.Keys))
        {
            Assert.Equal(table.Keys, shown.Keys);
            Assert.Equal(table.Action.Replace("Print Screen stays Windows' own", "Print Screen stays your desktop's own", StringComparison.Ordinal), shown.Action);
        }

        Assert.Contains(linux.Keys, row => row.Keys == "F12" && row.Action.EndsWith("Print Screen stays your desktop's own", StringComparison.Ordinal));
        Assert.DoesNotContain(linux.Keys, row => row.Action.Contains("Windows", StringComparison.Ordinal));
        Assert.Equal(KeyTable.Rows.Count, mac.Keys.Count);
        Assert.Equal(KeyTable.Essentials.Count, mac.EssentialKeys.Count);
        // alone, a Mac's function keys are media keys (F11 lowers the volume, Fn+F11 shows the desktop); its Delete key is Backspace, Fn+Delete the forward Delete
        foreach (KeyRow row in mac.Keys)
        {
            Assert.DoesNotMatch(@"(^|/ )F\d", row.Keys);
            foreach (string pc in new[] { "F11", "Alt", "Enter", "Backspace", "Page Up", "Page Down" }) { Assert.DoesNotContain(pc, row.Keys, StringComparison.Ordinal); }
        }

        Assert.Contains(new KeyRow("Fn+F5 / Fn+F9", "Quick save / quick load"), mac.Keys);
        Assert.Contains(new KeyRow("Fn+Delete", "Delete the slot under the cursor in the save list"), mac.Keys);
        Assert.Contains(new KeyRow("Delete (held)", "Rewind (back in menus)"), mac.Keys);
        Assert.Contains(mac.Keys, row => row.Keys == "Option+Return" && row.Action.StartsWith("Fullscreen", StringComparison.Ordinal));
        Assert.Equal("Borderless fullscreen; off, the game opens as a window. In the game, Alt+Enter, F11 or Config › System › Display Mode switches it at any time, and the switch here shows what the game remembers.", linux.FullscreenDescription);
        Assert.DoesNotContain("F11", mac.FullscreenDescription, StringComparison.Ordinal);
    }

    /// <summary>The window-mode lines of settings.ini.</summary>
    private string[] SettingsLines() => File.ReadAllLines(_paths.SettingsFile).Where(line => line.StartsWith("fullscreen=", StringComparison.Ordinal) || line.StartsWith("display_mode=", StringComparison.Ordinal)).ToArray();

    /// <summary>A model of the macOS or Linux rules on any host, with SteamDeck as given.</summary>
    private UnixLauncherModel Platform(bool linux, string? steamDeck) =>
        new(_paths, Path.Combine(_root, "install"), Path.Combine(_root, "no-game")) { IsLinux = linux, IsMac = !linux, SteamDeck = steamDeck, MacBundleUpdate = false };

    /// <summary>A key of a desktop entry as a reader of the specification gets it: the string escapes undone, and Exec's one quoted argument unquoted (%% is %).</summary>
    private static string EntryValue(string entry, string key)
    {
        string raw = entry.Split('\n').Single(line => line.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..];
        var value = new System.Text.StringBuilder();
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\') { value.Append(raw[i]); continue; }
            char escaped = raw[++i];
            value.Append(escaped switch
            {
                's' => ' ',
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '\\' => '\\',
                _ => throw new InvalidDataException($"'\\{escaped}' is not an escape of a desktop entry's string: {raw}"),
            });
        }

        if (key != "Exec") { return value.ToString(); }
        string exec = value.ToString();
        Assert.True(exec.Length >= 2 && exec[0] == '"' && exec[^1] == '"', "Exec is not one quoted argument: " + raw);
        var argument = new System.Text.StringBuilder();
        for (int i = 1; i < exec.Length - 1; i++)
        {
            char c = exec[i];
            if (c == '\\')
            {
                char quoted = exec[++i];
                Assert.True("\"`$\\".Contains(quoted, StringComparison.Ordinal), $"'\\{quoted}' is not a quoting escape: {raw}");
                argument.Append(quoted);
            }
            else if (c == '%')
            {
                Assert.True(exec[++i] == '%', "a field code in Exec: " + raw);
                argument.Append('%');
            }
            else
            {
                Assert.False("\"`$".Contains(c, StringComparison.Ordinal), $"'{c}' must be escaped inside Exec's quotes: {raw}");
                argument.Append(c);
            }
        }

        return argument.ToString();
    }

    private void WriteSaveAndBackup()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        File.WriteAllText(_paths.SaveFile, "current save");
        string backup = Path.Combine(_paths.BackupsDirectory, "20260101-000000");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "save.dat"), "old save");
        File.WriteAllText(Path.Combine(backup, "settings.ini"), "prompts=xbox\n");
    }

    /// <summary>A release whose download is <paramref name="size"/> bytes as GitHub lists it (null: the real size) and whose checksum never matches.</summary>
    private void Offer(FakeGitHub server, long? size)
    {
        string name = PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64);
        string asset = Path.Combine(_root, name);
        File.WriteAllText(asset, "must not be installed");
        string sums = Path.Combine(_root, UpdateSource.SumsAssetName);
        File.WriteAllText(sums, new string('0', 64) + "  " + name + "\n");
        server.List(UpdateSource.DefaultRepository, server.Release(UpdateSource.DefaultRepository, Version.Tag, [(name, asset), (UpdateSource.SumsAssetName, sums)], sizeOverride: size));
    }

    /// <summary>A real release of <paramref name="folder"/> with a matching checksum.</summary>
    private void ServeRelease(FakeGitHub server, string folder)
    {
        string name = PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64);
        string archive = LinuxRelease.Tar(folder, Path.Combine(_root, name));
        server.List(UpdateSource.DefaultRepository, server.Release(UpdateSource.DefaultRepository, Version.Tag, [(name, archive), (UpdateSource.SumsAssetName, ReleaseFiles.Sums(Path.Combine(_root, UpdateSource.SumsAssetName), archive))]));
    }

    /// <summary>Ends a stand-in game. The model disposes a process it followed once it has ended, so a disposed one has ended already.</summary>
    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // disposed by the model after it ended
        }
    }

    /// <summary>A long-running process standing in for the game (it is killed at the end of the test).</summary>
    private static Process Sleeper()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 31 127.0.0.1 >NUL")
            : new ProcessStartInfo("/bin/sh", ["-c", "sleep 30"]);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        return Process.Start(start) ?? throw new InvalidOperationException("the stand-in game did not start");
    }

    private static string[] Snapshot(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(folder, file) + ":" + ReleaseSums.Sha256Hex(file)).ToArray();

    private UnixLauncherModel Model(string install, Uri api, bool absolute = false) => new(_paths, absolute ? install : Path.Combine(_root, install), Path.Combine(_root, "no-game"))
    {
        UpdateSource = UpdateSource.ForLoopback(api),
        AssetName = version => PlatformUpdate.AssetNameFor(version, "linux", Architecture.X64),
        CurrentVersionOverride = new ReleaseVersion(0, 0, 1),
        // the Linux layout on every host, a Mac included
        MacBundleUpdate = false,
    };
}
