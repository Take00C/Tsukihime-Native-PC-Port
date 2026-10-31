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
        }
    }

    /// <summary>
    /// Drives the view model the way the pages do (every setter, the folder choice) over a temporary profile and checks what lands in settings.ini and game_dir.txt. Returns the
    /// report; it contains a line that starts with FAIL for every check that did not hold.
    /// </summary>
    public static async Task<string> SelfTestAsync(LauncherModel model, LauncherPaths paths, string? gameFolder)
    {
        var report = new System.Text.StringBuilder();
        void Check(string what, bool ok) => report.AppendLine((ok ? "ok    " : "FAIL  ") + what);
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

    private static async Task DrawAsync(FrameworkElement root, string path)
    {
        // let the loaded events, the bindings and the layout passes that wait for idle run
        await root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        root.UpdateLayout();
        await root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }
}
