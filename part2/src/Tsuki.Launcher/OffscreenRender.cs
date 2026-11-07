// Verification mode (package K17): draws the shell to PNG files without ever showing a window. The shell is attached to a message-only HwndSource (an invisible
// window handle, so that controls are loaded and text boxes draw their text), measured and arranged at a fixed size and drawn with RenderTargetBitmap.
// Nothing appears on the screen; animations are off because no clock is needed.
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher;

internal static class OffscreenRender
{
    public const int Width = 1200;

    /// <summary>The window's smallest width (MainWindow.xaml MinWidth), for the check that the footer still fits there (package K42).</summary>
    private const int MainWindowMinWidth = 960;

    /// <summary>The height of the picture (780 is the window; a taller value shows a whole page, for the check of the lower cards).</summary>
    public static int Height { get; set; } = 780;

    /// <summary>HWND_MESSAGE: a window parented to it is never visible.</summary>
    private static readonly IntPtr MessageOnly = new(-3);

    public static async Task RunAsync(LauncherModel model, string outDirectory, string? onlyPage)
    {
        Directory.CreateDirectory(outDirectory);
        await model.RefreshAsync();
        var shell = new Shell { Animate = false, DataContext = model, Width = Width, Height = Height };
        using var source = new HwndSource(new HwndSourceParameters("TsukihimeLauncherRender", Width, Height) { ParentWindow = MessageOnly, WindowStyle = 0 })
        {
            RootVisual = shell,
        };

        if (model.HeroArt is BitmapSource art)
        {
            LauncherArt.SavePng(art, Path.Combine(outDirectory, "art_source.png"));
        }

        foreach (NavItem item in model.NavItems)
        {
            string name = item.Page.ToString().ToLowerInvariant();
            if (onlyPage is not null && !name.Equals(onlyPage, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            model.Page = item.Page;
            await DrawAsync(shell, Path.Combine(outDirectory, name + ".png"));
        }

        if (onlyPage is null)
        {
            // the real window, never shown: its handle is created so that the frame, the chrome and the icon are known to load
            var window = new MainWindow(model);
            _ = new WindowInteropHelper(window).EnsureHandle();
            File.WriteAllText(Path.Combine(outDirectory, "window_ok.txt"), $"window {window.Title}, min {window.MinWidth} x {window.MinHeight}, icon {window.Icon is not null}, chrome {WindowChrome.GetWindowChrome(window) is not null}, game icon {window.GameIconApplied ?? "none (the crescent)"}");
            window.Close();

            // the confirm overlay, as the saves page shows it
            model.Page = LauncherPage.Saves;
            model.AskImport("savedata000");
            await DrawAsync(shell, Path.Combine(outDirectory, "confirm.png"));
            model.ConfirmNoCommand.Execute(null);
            model.Page = LauncherPage.Play;
            model.ShowToast("The game could not be started: example message for the render check.");
            await DrawAsync(shell, Path.Combine(outDirectory, "toast.png"));
            model.ShowToast(string.Empty);

            await DrawUpdateAsync(model, shell, outDirectory);
        }
    }

    /// <summary>
    /// Package K40: the update button and its overlay, drawn from a made-up release (no network): the footer with "Update to vX.Y.Z", the overlay with the notes,
    /// the download in progress, the two refusals (the game is running, a folder that cannot be written), a failure, the Help page's Updates card in a normal and in
    /// a personal build. Package K42: before that the always-shown button as "Check for updates", "Checking…" and the toasts of a manual check (newest, offline),
    /// the gold button at the window's smallest width (the status line trims, PLAY keeps its size), and a personal build's button with its toast.
    /// </summary>
    private static async Task DrawUpdateAsync(LauncherModel model, Shell shell, string outDirectory)
    {
        model.Page = LauncherPage.Play;
        model.PreviewUpdate(null);
        await DrawAsync(shell, Path.Combine(outDirectory, "update_footer_idle.png"));
        model.PreviewChecking(true);
        await DrawAsync(shell, Path.Combine(outDirectory, "update_footer_checking.png"));
        model.PreviewChecking(false);
        model.PreviewManualCheck(new UpdateCheckResult(null, [], null));
        await DrawAsync(shell, Path.Combine(outDirectory, "update_toast_newest.png"));
        model.PreviewManualCheck(new UpdateCheckResult(null, [], "no connection (No such host is known. (api.github.com:443))"));
        await DrawAsync(shell, Path.Combine(outDirectory, "update_toast_offline.png"));
        model.ShowToast(string.Empty);

        ReleaseVersion next = new(model.CurrentVersion.Major, model.CurrentVersion.Minor, model.CurrentVersion.Patch + 1);
        string zip = UpdateSource.ZipAssetName(next);
        var sample = new ReleaseInfo(
            next,
            next.Tag,
            $"Tsukihime PC port {next.Tag}",
            UpdateFeed.PlainText(
                "## Fixed\n\n- **Sample notes for the render check.** A release's notes appear here as plain text, scrollable when they are long.\n" +
                "- Headings, **bold** and `code` marks of GitHub's Markdown are taken out; [links](https://example.invalid) keep their words.\n\n" +
                "## How to update\n\nPress **Update now**: the launcher downloads the zip and SHA256SUMS.txt, checks the checksum, replaces its files and restarts.\n\n" +
                "> Fan-made and unofficial. This contains no game data; you must own the game."),
            "https://github.com/" + UpdateSource.DefaultRepository + "/releases/tag/" + next.Tag,
            true,
            new ReleaseAsset(zip, new Uri("https://github.com/" + UpdateSource.DefaultRepository + "/releases/download/" + next.Tag + "/" + zip), 110_268_124),
            new ReleaseAsset(UpdateSource.SumsAssetName, new Uri("https://github.com/" + UpdateSource.DefaultRepository + "/releases/download/" + next.Tag + "/" + UpdateSource.SumsAssetName), 98));
        model.PreviewUpdate(sample);
        await DrawAsync(shell, Path.Combine(outDirectory, "update_footer.png"));
        shell.Width = MainWindowMinWidth;
        await DrawAsync(shell, Path.Combine(outDirectory, "update_footer_narrow.png"), MainWindowMinWidth);
        shell.Width = Width;
        model.PreviewUpdateState(UpdateStage.Offer, null, string.Empty);   // the overlay a manual check opens (a press of the gold button would start an update)
        await DrawAsync(shell, Path.Combine(outDirectory, "update_offer.png"));
        model.PreviewUpdateState(UpdateStage.Working, new UpdateProgress(UpdateStep.Downloading, new DownloadProgress(46_137_344, 110_268_124)), string.Empty);
        await DrawAsync(shell, Path.Combine(outDirectory, "update_progress.png"));
        model.PreviewUpdateState(UpdateStage.Offer, null, "Close the game first: Tsukihime.exe of this folder is running, and its files are the ones the update replaces. Quit the game (your progress is in your saves), then press Update now again.");
        await DrawAsync(shell, Path.Combine(outDirectory, "update_game_running.png"));
        model.PreviewUpdateState(UpdateStage.Failed, null, UpdateInstaller.NotWritableMessage(@"C:\Program Files\Tsukihime", next));
        await DrawAsync(shell, Path.Combine(outDirectory, "update_not_writable.png"));
        model.PreviewUpdateState(UpdateStage.Failed, null, $"The update did not work: the downloaded {zip} does not match its checksum in {UpdateSource.SumsAssetName} (SHA-256 3f7a09c2d1e4...), so it was not installed. Nothing was changed; the launcher you have keeps working.");
        await DrawAsync(shell, Path.Combine(outDirectory, "update_failed.png"));
        model.PreviewUpdateState(UpdateStage.Hidden, null, string.Empty);
        model.Page = LauncherPage.Help;
        await DrawAsync(shell, Path.Combine(outDirectory, "help_update.png"));
        model.PreviewPersonal(true);
        await DrawAsync(shell, Path.Combine(outDirectory, "help_personal.png"));
        model.Page = LauncherPage.Play;
        model.ShowToast(UpdateButtonState.PersonalToast);          // what the button says in a personal build (PressUpdateButtonAsync, without its log line)
        await DrawAsync(shell, Path.Combine(outDirectory, "update_toast_personal.png"));
        model.ShowToast(string.Empty);
        model.PreviewPersonal(false);
        model.PreviewUpdate(null);
    }

    /// <summary>
    /// Package K40, verification mode: checks the loopback stand-in of GitHub the model was given, opens the overlay and installs the update over the launcher's own
    /// folder (the folder of this process), as "Update now" does. The caller then starts the new launcher. Returns the report (a FAIL line per check that did not hold).
    /// Package K42: the check is a press of the footer button, whose states before, during and after it (and its toast) are reported too; a run without a newer
    /// release (up to date, offline, a personal build) ends after those lines with the FAIL of "the check found an update".
    /// </summary>
    public static async Task<string> UpdateSelfTestAsync(LauncherModel model)
    {
        var report = new System.Text.StringBuilder();
        void Check(string what, bool ok) => report.AppendLine((ok ? "ok    " : "FAIL  ") + what);
        string app = AppContext.BaseDirectory;
        report.AppendLine($"ok    launcher {model.Build.InformationalVersion} runs from {app}; the check compares with {model.CurrentVersion.Tag}; source {model.UpdateSource.ReleasesUri}");
        Check("the source is the loopback address", model.UpdateSource.Policy.IsLoopback);

        // package K42: the check is the footer button's, pressed the way a click does; the button is there before it, reads "Checking…" while it runs, and says the outcome
        Check($"before the check the footer button reads \"{model.UpdateButtonLabel}\" in the quiet look and can be pressed", model.UpdateButtonLabel == UpdateButtonState.CheckLabel && !model.UpdateButtonAccent && model.UpdateButtonEnabled);
        Task press = model.PressUpdateButtonAsync();
        if (!model.UpdatesOff)
        {
            Check($"while it checks the footer button reads \"{model.UpdateButtonLabel}\" and is disabled", press.IsCompleted || (model.UpdateButtonLabel == UpdateButtonState.CheckingLabel && !model.UpdateButtonEnabled));
        }

        await press;
        ReleaseInfo? found = model.AvailableUpdate;
        string expectedToast = model.UpdatesOff ? UpdateButtonState.PersonalToast : model.LastCheckOutcome switch
        {
            UpdateCheckOutcome.Failed => UpdateButtonState.FailedToast,
            UpdateCheckOutcome.Newest => $"You have the newest version ({model.CurrentVersion.Tag}).",
            _ => string.Empty,
        };
        Check($"after the press the footer button reads \"{model.UpdateButtonLabel}\" ({(model.UpdateButtonAccent ? "gold" : "quiet")}, {(model.UpdateButtonEnabled ? "enabled" : "disabled")}); outcome {model.LastCheckOutcome?.ToString() ?? "no check"}; toast \"{model.Toast}\"",
            model.Toast == expectedToast && model.UpdateButtonAccent == (found is not null) && (model.LastCheckOutcome is not null) != model.UpdatesOff);
        Check($"the check found an update ({found?.Tag ?? "none"}; {model.UpdateStatus})", found is not null);
        if (found is null)
        {
            return report.ToString();
        }

        Check($"the footer offers \"{model.UpdateButtonLabel}\" in the gold look", model.UpdateButtonAccent && model.UpdateButtonLabel == $"Update to {found.Tag}");
        Check("the manual check opened the overlay at once", model.UpdateStage == UpdateStage.Offer);
        Check($"the overlay shows the release's title and notes ({found.Title}; {found.Notes.Length} characters)", model.UpdateOverlayVisible && model.UpdateStage == UpdateStage.Offer && model.UpdateNotes.Length > 0 && model.UpdateReleaseTitle == found.Title);
        // one click: Later closes the overlay, the gold button stays, and one press of it installs the update
        model.UpdateLaterCommand.Execute(null);
        Check("Later closed the overlay and the gold button still offers the update; one press of it installs", model.UpdateStage == UpdateStage.Hidden && model.UpdateButtonAccent && model.UpdateButtonEnabled);
        await model.PressUpdateButtonAsync();
        Check($"the update was installed and the restart was asked for (stage {model.UpdateStage}{(model.UpdateMessage.Length > 0 ? "; " + model.UpdateMessage : string.Empty)})", model.UpdateStage == UpdateStage.Restarting);
        string exe = Path.Combine(app, GameLauncher.LauncherFileName);
        Check("the running TsukihimeLauncher.exe was renamed to .old and the new one moved in", File.Exists(exe + UpdateInstaller.OldSuffix) && File.Exists(exe));
        string journal = new UpdateStaging(app).Journal;
        Check("the journal of the update ends with done", File.Exists(journal) && File.ReadAllLines(journal).Contains("done"));
        Tsuki.Data.Icons.AssemblyMarks? marks = Tsuki.Data.Icons.PeIcons.Marks(Path.Combine(app, "TsukihimeLauncher.dll"));
        Check($"the launcher now in the folder is {found.Tag} ({marks?.InformationalVersion})", ReleaseVersion.TryParseInformational(marks?.InformationalVersion, out ReleaseVersion installed) && installed == found.Version);
        return report.ToString();
    }

    /// <summary>
    /// Drives the view model the way the pages do (every setter, the folder choice) over a temporary profile and checks what lands in settings.ini and game_dir.txt. Returns the
    /// report; it contains a line that starts with FAIL for every check that did not hold.
    /// </summary>
    /// <param name="model">The view model.</param>
    /// <param name="paths">The temporary profile.</param>
    /// <param name="gameFolder">The game folder of <c>--game</c>, or null.</param>
    /// <param name="afterUpdate">Package K40: the version an update came from and the .old files its cleanup left, when an update restarted into this launcher.</param>
    public static async Task<string> SelfTestAsync(LauncherModel model, LauncherPaths paths, string? gameFolder, (ReleaseVersion From, int Left)? afterUpdate = null)
    {
        var report = new System.Text.StringBuilder();
        void Check(string what, bool ok) => report.AppendLine((ok ? "ok    " : "FAIL  ") + what);
        string app = AppContext.BaseDirectory;
        report.AppendLine($"ok    launcher {model.Build.InformationalVersion} runs from {app} (personal build: {model.Build.IsPersonal})");
        if (afterUpdate is { } after)
        {
            string[] old = Directory.GetFiles(app, "*" + UpdateInstaller.OldSuffix, SearchOption.AllDirectories);
            Check($"after the update from {after.From.Tag}: {after.Left} file(s) left by the cleanup, {old.Length} .old file(s) in the folder, staging folder {(Directory.Exists(new UpdateStaging(app).Root) ? "still there" : "gone")}",
                after.Left == 0 && old.Length == 0 && !Directory.Exists(new UpdateStaging(app).Root));
            Check($"this launcher ({model.Build.Version.Tag}) is newer than the one it replaced ({after.From.Tag})", model.Build.Version > after.From);
        }

        await model.RefreshAsync();
        Check("fresh profile: English, windowed, vsync on, 1600 x 900, auto prompts", model.LanguageIndex == 1 && !model.Fullscreen && model.VSync && model.SelectedWindowSize is { Width: 1600, Height: 900 } && model.PromptsIndex == 0);
        Check("the window sizes offered fit the chosen monitor", model.WindowSizes.Count > 0 && model.WindowSizes.All(c => model.SelectedMonitor is not { } sm || Tsuki.Platform.WindowFit.Fits(c.Width, c.Height, sm.WorkArea.Width, sm.WorkArea.Height)));

        model.LanguageIndex = 0;
        model.Fullscreen = true;
        model.VSync = false;
        WindowSizeChoice? big = model.WindowSizes.FirstOrDefault(c => c.Width == 1920) ?? model.WindowSizes[^1];
        model.SelectedWindowSize = big;
        model.PromptsIndex = 3;
        MonitorEntry? monitor = model.Monitors.LastOrDefault();
        if (monitor is not null)
        {
            model.SelectedMonitor = monitor;
        }

        Tsuki.App.Services.AppSettingsData saved = Tsuki.App.Services.AppSettings.Load(paths.SettingsFile);
        Check("settings.ini holds language=ja", saved.GameLanguage == Tsuki.Engine.Contracts.Language.Ja);
        Check("settings.ini holds fullscreen=on", saved.Fullscreen == true);
        Check("settings.ini holds vsync=off", saved.VSync == false);
        Check($"settings.ini holds window={big.Width}x{big.Height}", saved.WindowSize == (big.Width, big.Height));
        Check("the Display page shows that preset as selected", model.SelectedWindowSize == big);
        Check("settings.ini holds prompts=xbox", saved.Prompts == Tsuki.Ui.PromptSetting.Xbox);
        Check("settings.ini holds the monitor corner", monitor is null || saved.Monitor == new Tsuki.App.Services.MonitorOrigin(monitor.X, monitor.Y));
        Check("the status line follows", model.StatusLine.Contains("Fullscreen", StringComparison.Ordinal) && model.StatusLine.Contains("Xbox", StringComparison.Ordinal));
        Check("the sidebar follows the page", ((model.Page = LauncherPage.Saves) == LauncherPage.Saves) && model.SelectedNav.Page == LauncherPage.Saves);

        model.BackupNowCommand.Execute(null);
        Check("a backup of settings.ini was made (no save yet)", model.Backups.Count == 1 && model.Backups[0].Info.Files.SequenceEqual(["settings.ini"]));

        // package K20: the shortcuts go into a temporary folder of the profile, never onto the desktop of the machine the self-test runs on
        string links = Path.Combine(paths.DataDirectory, "selftest-links");
        model.ShortcutFolders = new ShortcutFolders(Path.Combine(links, "desktop"), Path.Combine(links, "startmenu"));
        (IReadOnlyList<ShortcutSpec> desktop, string? desktopProblem) = model.PlanShortcuts(LauncherModel.ShortcutKind.DesktopGame);
        Check("the desktop shortcut plan names Tsukihime.lnk pointing at Tsukihime.exe" + (desktopProblem is null ? string.Empty : $" ({desktopProblem})"),
            desktopProblem is null && desktop.Count == 1 && Path.GetFileName(desktop[0].LinkPath) == Shortcuts.GameLinkName && Path.GetFileName(desktop[0].TargetPath) == GameLauncher.AppFileName);
        if (desktopProblem is null)
        {
            model.DesktopShortcutCommand.Execute(null);
            ShortcutInfo? made = Shortcuts.Read(desktop[0].LinkPath);
            Check("the desktop shortcut was written into the temporary folder and reads back", made is not null && made.TargetPath == desktop[0].TargetPath && made.WorkingDirectory == desktop[0].WorkingDirectory && made.IconPath == desktop[0].IconPath);
            Check("the shortcut's icon is the Arcueid .ico of the profile when the folder is ready, else the executable's crescent",
                model.Folder.IsReady ? desktop[0].IconPath == model.GameIconPath && model.GameIconPath is not null : desktop[0].IconPath == desktop[0].TargetPath);
            Check("the Play page says the shortcut exists and offers Replace", model.DesktopShortcutLabel == "Replace" && model.DesktopShortcutText.Contains("is on your desktop", StringComparison.Ordinal));
            model.DesktopShortcutCommand.Execute(null);
            Check("a second request asks before replacing", model.ConfirmVisible && model.Confirm is { ConfirmLabel: "Replace" });
            model.ConfirmNoCommand.Execute(null);
        }

        (IReadOnlyList<ShortcutSpec> start, string? startProblem) = model.PlanShortcuts(LauncherModel.ShortcutKind.StartMenu);
        if (startProblem is null)
        {
            model.StartMenuShortcutsCommand.Execute(null);
            Check("the Start-menu entries were written into the temporary folder", start.All(s => Shortcuts.Read(s.LinkPath) is { } info && info.TargetPath == s.TargetPath));
        }

        if (gameFolder is not null)
        {
            await model.SetGameFolderAsync(Path.GetDirectoryName(gameFolder.TrimEnd('\\', '/')) ?? gameFolder);
            Check("the parent of the game folder is resolved to the game folder", model.Folder.IsReady && model.Folder.Path == Path.GetFullPath(gameFolder));
            Check("game_dir.txt holds the folder", paths.ReadGameDirectory() == Path.GetFullPath(gameFolder));
            Check("the ready check is all good and Play is possible", model.ReadyAllGood && model.CanPlay);
            // package K23: the Arcueid icon was built from the player's files into the profile's icons folder, in every size, and the window shows it
            string? icon = model.GameIconPath;
            IReadOnlyList<Tsuki.Data.Icons.IconFrame> frames = icon is not null && File.Exists(icon) ? Tsuki.Data.Icons.FaceIcon.DecodeIco(File.ReadAllBytes(icon)) : [];
            Check($"the Arcueid icon was built into the profile ({icon ?? "none"}; {frames.Count} frames)",
                icon == Tsuki.Data.Icons.FaceIconCache.IconPath(paths.DataDirectory) && frames.Select(f => f.Size).SequenceEqual(Tsuki.Data.Icons.FaceIcon.Sizes) && Tsuki.Data.Icons.FaceIconCache.IsCurrent(paths.DataDirectory, gameFolder));
            Check("the launcher window takes the Arcueid icon", icon is not null && GameIcon.Load(icon) is { } loaded && loaded.Decoder?.Frames.Count == frames.Count);
            await model.SetGameFolderAsync(Path.Combine(paths.DataDirectory, "nowhere"));
            Check("a folder that is not the game is not remembered", paths.ReadGameDirectory() == Path.GetFullPath(gameFolder) && model.FolderMessage.Contains("not remembered", StringComparison.Ordinal));
        }

        return report.ToString();
    }

    private static async Task DrawAsync(FrameworkElement root, string path, int width = Width)
    {
        // let the loaded events, the bindings and the layout passes that wait for idle run
        await root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        root.Measure(new Size(width, Height));
        root.Arrange(new Rect(0, 0, width, Height));
        root.UpdateLayout();
        await root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap(width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }
}
