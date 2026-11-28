using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tsuki.Launcher.Avalonia;
using Tsuki.Launcher.Avalonia.Views;
using Tsuki.Cli.Commands;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Data.Cli;
using Tsuki.Tests.Synthetic.Launcher;
using Path = System.IO.Path;
using Shape = Avalonia.Controls.Shapes.Path;

namespace Tsuki.Tests.Avalonia;

public sealed class TestLauncherApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<UnixLauncherApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia();
}

/// <summary>
/// The launcher window as the player sees it: each test shows the window first, then acts, then looks at the real controls (the round-5 bugs were a toast that
/// never showed, an overlay with an empty box and a restore that did not ask; tests that only read the model's values could not see them).
/// </summary>
public sealed class LauncherHeadlessTests : IDisposable
{
    private readonly string _root = TestFolders.Temp("tsuki-k48-headless-");
    private static ReleaseVersion Version => ReleaseFiles.BuildVersion();

    public LauncherHeadlessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task AllEightPagesRenderAndEveryEnabledActionButtonHasAWorkingCommand()
    {
        var model = Model();
        await Run(async () =>
        {
            Assert.Equal(global::Avalonia.Styling.ThemeVariant.Dark, Application.Current?.RequestedThemeVariant);
            Assert.Equal(NavIcons.Help, model.NavItems.Single(item => item.Page == LauncherPage.Help).Glyph);
            Assert.Equal(NavIcons.Cup, model.NavItems.Single(item => item.Page == LauncherPage.Achievements).Glyph);
            var window = Show(model);
            await model.RefreshAsync();
            foreach (LauncherPage page in Enum.GetValues<LauncherPage>())
            {
                model.Page = page;
                Pump(window);
                Assert.Equal(page + "Page", window.PageHost.Content?.GetType().Name);
                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no rendered frame");
                string file = Path.Combine(_root, page + ".png");
                frame.Save(file);
                Assert.True(new FileInfo(file).Length > 1000, page + " rendered an empty image");
                Assert.False(model.OpenGameFolderCommand.CanExecute(null));
                foreach (Button button in window.GetLogicalDescendants().OfType<Button>().Where(button => button.GetType() == typeof(Button) && button.IsEffectivelyVisible))
                {
                    Assert.True(button.Command is not null, $"No command: {button.GetType().Name} '{button.Content}' classes={string.Join(',', button.Classes)}");
                    Assert.True(button.Command.CanExecute(button.CommandParameter) || button.Command == model.OpenGameFolderCommand, button.Content?.ToString());
                }

                // the Windows launcher's pages have no Fluent switch or drop-down (their system-blue accent and On/Off text were round-5 findings)
                Assert.Empty(window.GetVisualDescendants().OfType<ToggleSwitch>());
                Assert.Empty(window.GetVisualDescendants().OfType<ComboBox>());
            }

            // one entry per page, in the pages' order: Achievements between Saves and Help
            Assert.Equal(8, model.NavItems.Count);
            Assert.Equal(Enum.GetValues<LauncherPage>(), model.NavItems.Select(item => item.Page));
            Assert.Equal(LauncherTexts.AchievementsTitle, model.NavItems[6].Label);
            Assert.True(model.ReadyRows.Count > 0);
            Assert.Equal("Check for updates", model.UpdateButtonLabel);
            model.Page = LauncherPage.GameFiles;
            object? gameFilesView = window.PageHost.Content;
            model.FolderText = Path.Combine(_root, "new-folder");
            Assert.Same(gameFilesView, window.PageHost.Content);
            Assert.Contains("Exec=\"/tmp/Tsuki hime/Tsukihime\"", PlatformServices.DesktopEntry("/tmp/Tsuki hime/Tsukihime"));
            Assert.Contains("\nIcon=/home/p/.config/Tsuki/icons/tsukihime.png\n", PlatformServices.DesktopEntry("/x/Tsukihime", "/home/p/.config/Tsuki/icons/tsukihime.png"));
            Assert.NotNull(window.Icon);
            window.Close();
        });
    }

    [Fact]
    public async Task AToastShowsItsTextAboveTheFooter()
    {
        var model = Model();
        await Run(() =>
        {
            var window = Show(model);
            Assert.False(window.ToastHost.IsEffectivelyVisible);
            model.Notify("A toast for the test.");
            Pump(window);
            Assert.True(window.ToastHost.IsEffectivelyVisible);
            Assert.Equal("A toast for the test.", window.ToastText.Text);
            Assert.True(window.ToastHost.Bounds.Height > 20);
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task APictureOfAWindowThatKeepsMovingIsStillTaken()
    {
        // The pictures of the render check and of the self-tests' --shots wait for two frames in a row that are the same. A window with an animation that never
        // ends never gives two: its picture is still taken, without waiting long
        var model = Model();
        await Run(() =>
        {
            var window = Show(model);
            var moving = new Border { Width = 300, Height = 300, Background = Brushes.White };
            window.PageHost.Content = moving;
            var fade = new global::Avalonia.Animation.Animation
            {
                Duration = TimeSpan.FromSeconds(1),
                IterationCount = global::Avalonia.Animation.IterationCount.Infinite,
                Children =
                {
                    new global::Avalonia.Animation.KeyFrame { Cue = new global::Avalonia.Animation.Cue(0), Setters = { new global::Avalonia.Styling.Setter(Visual.OpacityProperty, 0.0) } },
                    new global::Avalonia.Animation.KeyFrame { Cue = new global::Avalonia.Animation.Cue(1), Setters = { new global::Avalonia.Styling.Setter(Visual.OpacityProperty, 1.0) } },
                },
            };
            using var stop = new CancellationTokenSource();
            _ = fade.RunAsync(moving, stop.Token);
            Pump(window);
            byte[] first = Png(UnixOffscreenRender.Frame(window));
            Thread.Sleep(120);
            var taking = Stopwatch.StartNew();
            byte[] second = Png(UnixOffscreenRender.Frame(window));
            Assert.True(taking.Elapsed < TimeSpan.FromSeconds(5), $"the picture took {taking.Elapsed.TotalSeconds:0.0} s");
            Assert.False(first.AsSpan().SequenceEqual(second), "the window did not move: this test sees nothing");
            stop.Cancel();
            window.Close();
            return Task.CompletedTask;
        });

        static byte[] Png(WriteableBitmap frame)
        {
            using (frame)
            {
                using var stream = new MemoryStream();
                frame.Save(stream);
                return stream.ToArray();
            }
        }
    }

    [Fact]
    public async Task AnUpdateFoundAfterTheWindowOpenedFillsTheOverlayAndTakesTheKeyboard()
    {
        using var server = new FakeGitHub();
        Offer(server, "BAD-NOT-NEEDED", notes: "## Fixed\n\n- **Overlay notes** for the test.");
        var model = Model(server.Base);
        await Run(async () =>
        {
            var window = Show(model);
            Assert.DoesNotContain("Gold", window.UpdateButton.Classes);
            await model.CheckForUpdatesAsync(manual: true);
            Pump(window);
            Assert.True(window.UpdateOverlay.IsEffectivelyVisible);
            Assert.Equal("Update to " + Version.Tag, window.UpdateHeading.Text);
            Assert.StartsWith("You have v0.0.1  ·  " + Version.Tag, window.UpdateVersion.Text, StringComparison.Ordinal);
            Assert.Equal("Tsukihime PC port " + Version.Tag, window.UpdateTitle.Text);
            Assert.Contains("Overlay notes for the test.", window.UpdateNotesText.Text, StringComparison.Ordinal);
            Assert.True(window.UpdateNowButton.IsEffectivelyVisible);
            Assert.False(window.UpdateMessageBox.IsEffectivelyVisible);
            // the footer turned gold, and nothing behind the overlay takes keys or clicks
            Assert.Contains("Gold", window.UpdateButton.Classes);
            Assert.Equal("Update to " + Version.Tag, window.UpdateButton.Content);
            Assert.False(window.ContentArea.IsEnabled);
            Assert.False(window.PlayButton.IsEffectivelyEnabled);
            window.UpdateLaterButton.Command!.Execute(null);
            Pump(window);
            Assert.False(window.UpdateOverlay.IsEffectivelyVisible);
            Assert.True(window.ContentArea.IsEnabled);
            Assert.True(window.UpdateButton.IsEffectivelyEnabled);
            window.Close();
        });
    }

    [Fact]
    public async Task AFailedUpdateShowsWhyInTheOverlayAndSaysNothingChanged()
    {
        using var server = new FakeGitHub();
        Offer(server, new string('0', 64));
        var model = Model(server.Base, install: LinuxRelease.Folder(_root, "install"));
        model.RestartRequested = (_, _) => throw new InvalidOperationException("a refused update must never restart");
        await Run(async () =>
        {
            var window = Show(model);
            await model.CheckForUpdatesAsync(manual: true);
            await model.RunUpdateAsync();
            Pump(window);
            Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
            Assert.True(window.UpdateMessageBox.IsEffectivelyVisible);
            Assert.StartsWith("The update did not work: the downloaded ", window.UpdateMessageText.Text, StringComparison.Ordinal);
            Assert.EndsWith("Nothing was changed; the launcher you have keeps working.", window.UpdateMessageText.Text, StringComparison.Ordinal);
            Assert.Equal("Update to " + Version.Tag, window.UpdateHeading.Text);
            Assert.Equal("Try again", window.UpdateNowButton.Content);
            Assert.Equal("Close", window.UpdateLaterButton.Content);
            window.Close();
        });
    }

    [Fact]
    public async Task AnUpdateThatWorkedSaysItRestartsAndTheRestartedLauncherSaysUpdatedFrom()
    {
        string install = LinuxRelease.Folder(_root, "install");
        string release = LinuxRelease.Folder(_root, "release");
        string name = PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64);
        string archive = LinuxRelease.Tar(release, Path.Combine(_root, name));
        using var server = new FakeGitHub();
        server.List(UpdateSource.DefaultRepository, server.Release(UpdateSource.DefaultRepository, Version.Tag, [(name, archive), (UpdateSource.SumsAssetName, ReleaseFiles.Sums(Path.Combine(_root, UpdateSource.SumsAssetName), archive))]));
        var model = Model(server.Base, install: install);
        string? progressAtRestart = null;
        await Run(async () =>
        {
            var window = Show(model);
            model.RestartRequested = (_, _) => progressAtRestart = window.UpdateProgressLine.Text;
            await model.CheckForUpdatesAsync(manual: true);
            await model.RunUpdateAsync();
            Pump(window);
            Assert.Equal(UnixUpdateStage.Restarting, model.UpdateStage);
            Assert.Equal($"Restarting into {Version.Tag}...", progressAtRestart);
            Assert.Equal($"Restarting into {Version.Tag}...", window.UpdateProgressLine.Text);
            Assert.Contains(model.Log.Lines(), line => line.Contains("update: the files of " + Version.Tag + " are in place", StringComparison.Ordinal));
            window.Close();

            // the launcher it restarts into says so once its window is up (Program passes --updated-from)
            var restarted = Model();
            UnixLauncherApp.Model = restarted;
            UnixLauncherApp.UpdatedFrom = new ReleaseVersion(0, 0, 1);
            try
            {
                var next = (MainWindow)new UnixLauncherApp().CreateMainWindow();
                next.Show();
                Pump(next);
                Assert.True(next.ToastHost.IsEffectivelyVisible);
                Assert.Equal(LauncherTexts.UpdatedToast(new ReleaseVersion(0, 0, 1), restarted.Build.Version), next.ToastText.Text);
                Assert.Equal(LauncherTexts.UpdatedStatus(new ReleaseVersion(0, 0, 1), restarted.Build.Version), restarted.UpdateStatus);
                next.Close();
            }
            finally
            {
                UnixLauncherApp.Model = null;
                UnixLauncherApp.UpdatedFrom = null;
            }
        });
    }

    [Fact]
    public async Task RestoreAsksInTheWindowFirstAndOnlyYesReplacesTheSave()
    {
        var model = Model();
        LauncherPaths paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.SaveFile, "current save");
        string backup = Path.Combine(paths.BackupsDirectory, "20260101-000000");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "save.dat"), "old save");
        await Run(async () =>
        {
            var window = Show(model);
            Assert.True(model.CanAsk);
            await model.RefreshAsync();
            model.Page = LauncherPage.Saves;
            Pump(window);
            Assert.False(window.PageHost.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "NoBackupsText").IsEffectivelyVisible);
            Button restore = window.PageHost.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Restore"));
            restore.Command!.Execute(restore.CommandParameter);
            Pump(window);
            Assert.True(window.QuestionOverlay.IsEffectivelyVisible);
            Assert.Equal("Restore this backup?", window.QuestionTitle.Text);
            Assert.Contains("Your current save is backed up first", window.QuestionBody.Text, StringComparison.Ordinal);
            Assert.Equal("Restore", window.QuestionYes.Content);
            Assert.False(window.ContentArea.IsEnabled);
            window.QuestionNo.Command!.Execute(null);
            Pump(window);
            Assert.False(window.QuestionOverlay.IsEffectivelyVisible);
            Assert.True(window.ContentArea.IsEnabled);
            Assert.Equal("current save", File.ReadAllText(paths.SaveFile));

            restore = window.PageHost.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Restore"));
            restore.Command!.Execute(restore.CommandParameter);
            Pump(window);
            window.QuestionYes.Command!.Execute(null);
            for (int i = 0; i < 100 && File.ReadAllText(paths.SaveFile) != "old save"; i++) { Pump(window); await Task.Delay(20); }
            Assert.Equal("old save", File.ReadAllText(paths.SaveFile));
            Assert.StartsWith("Restored 20260101-000000.", window.ToastText.Text, StringComparison.Ordinal);
            window.Close();
            Assert.False(model.CanAsk);
        });
    }

    [Fact]
    public async Task TheLauncherStepsAsideForTheGameAndSaysHowItEnded()
    {
        var model = Model();
        await Run(async () =>
        {
            var window = Show(model);
            using Process game = Exiting(7);
            model.Track(game);
            Assert.Equal(WindowState.Minimized, window.WindowState);
            Pump(window);
            Assert.Equal("RUNNING", window.PlayButton.Content);
            Assert.False(model.PlayCommand.CanExecute(null));
            game.WaitForExit();
            // the process has ended before its Exited event reached the window: PLAY must not stay stuck on it
            Assert.False(model.GameRunning);
            await Until(window, () => window.ToastText.Text?.Contains("code 7", StringComparison.Ordinal) == true);
            Assert.Equal("The game closed with an error (code 7). Press Log to see why.", window.ToastText.Text);
            Assert.True(window.ToastHost.IsEffectivelyVisible);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal("PLAY", window.PlayButton.Content);

            using Process second = Exiting(GameLauncher.ExitAlreadyRunning);
            model.Track(second);
            second.WaitForExit();
            await Until(window, () => window.ToastText.Text?.Contains("another window", StringComparison.Ordinal) == true);
            Assert.Equal("The game is already running in another window.", window.ToastText.Text);
            window.Close();
        });
    }

    [Fact]
    public async Task TheFooterAndTheSidebarLookLikeTheWindowsLauncher()
    {
        var model = Model();
        await Run(() =>
        {
            var window = Show(model);
            // the crescent is a disc with a disc cut out, not two discs XORed into a ring
            Assert.Equal(GeometryCombineMode.Exclude, Assert.IsType<CombinedGeometry>(window.Crescent.Data).GeometryCombineMode);
            // the window has its icon from the start (the crescent until the game folder gives Arcueid's face), not only after a refresh
            Assert.NotNull(window.Icon);
            // the unselected entries are muted, the selected one is bright
            TextBlock[] labels = window.Nav.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "Txt").ToArray();
            Assert.Equal(8, labels.Length);
            Assert.Equal(Color.Parse("#E9EDF8"), BrushColor(labels[0].Foreground));
            Assert.All(labels.Skip(1), label => Assert.Equal(Color.Parse("#98A2C8"), BrushColor(label.Foreground)));
            // Log is the glass button, PLAY carries its glyph and its letter spacing
            Button log = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Log"));
            Assert.Equal(Color.Parse("#10FFFFFF"), BrushColor(Chrome(log).Background));
            Assert.Contains(window.PlayButton.GetVisualDescendants().OfType<Shape>(), path => path.Data is not null);
            Assert.Contains(window.PlayButton.GetVisualDescendants().OfType<TextBlock>(), text => text.LetterSpacing > 3);
            Assert.All(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("SmallCaps")), text => Assert.True(text.LetterSpacing >= 2, text.Text));
            // the version is "Launcher X.Y.Z", never the commit
            Assert.Matches(@"^Launcher \d+\.\d+\.\d+$", model.LauncherVersion);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == model.LauncherVersion);
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheSerifTitlesFindASerifFaceWithoutShippingAFont()
    {
        await Run(() =>
        {
            var serif = Assert.IsType<FontFamily>(Application.Current!.FindResource("SerifFont"));
            foreach (string family in new[] { "Palatino Linotype", "Palatino", "Georgia", "Noto Serif", "DejaVu Serif", "Liberation Serif" })
            {
                Assert.Contains(family, serif.FamilyNames);
            }

            // on Linux the serif titles take a serif face the desktop has (DejaVu Serif in WSL Ubuntu), not the sans one a missing font falls back to
            Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(serif), out IGlyphTypeface? face));
            Assert.Contains(face!.FamilyName, serif.FamilyNames);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheGoldUpdateButtonStaysGoldUnderThePointer()
    {
        var model = Model();
        await Run(() =>
        {
            var window = Show(model);
            window.UpdateButton.Classes.Add("Gold");
            ((IPseudoClasses)window.UpdateButton.Classes).Set(":pointerover", true);
            Pump(window);
            Border chrome = Chrome(window.UpdateButton);
            var gradient = Assert.IsAssignableFrom<IGradientBrush>(chrome.Background);
            Assert.Equal(Color.Parse("#33D9C58E"), gradient.GradientStops[0].Color);
            Assert.Equal(Color.Parse("#E6D9C58E"), BrushColor(chrome.BorderBrush));
            Assert.Equal(Color.Parse("#E9EDF8"), BrushColor(window.UpdateButton.Foreground));
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheTypedGamePathIsBright()
    {
        var model = Model();
        await Run(() =>
        {
            var window = Show(model);
            model.Page = LauncherPage.GameFiles;
            model.FolderText = "/home/player/Games/CUSA47999";
            Pump(window);
            TextPresenter presenter = window.PageHost.GetVisualDescendants().OfType<TextPresenter>().Single();
            Assert.Equal("/home/player/Games/CUSA47999", presenter.Text);
            Assert.Equal(Color.Parse("#E9EDF8"), BrushColor(presenter.GetValue(TextElement.ForegroundProperty)));
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheReadyCheckStacksEachDetailUnderItsTitleAndColoursItsBadge()
    {
        var model = Model();
        await Run(async () =>
        {
            var window = Show(model);
            await model.RefreshAsync();
            Pump(window);
            Assert.Equal("Fix", window.PageHost.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("LinkButton")).Content);
            var rows = window.PageHost.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "ReadyList").GetRealizedContainers().ToArray();
            Assert.Equal(model.ReadyRows.Count, rows.Length);
            for (int i = 0; i < rows.Length; i++)
            {
                Shape badge = rows[i].GetVisualDescendants().OfType<Shape>().First();
                Assert.Equal(model.ReadyRows[i].State == CheckState.Ok ? Color.Parse("#7ED6A8") : Color.Parse("#E8C877"), BrushColor(badge.Stroke));
                TextBlock detail = rows[i].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == model.ReadyRows[i].Detail);
                TextBlock title = rows[i].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == model.ReadyRows[i].Title);
                Assert.True(detail.Bounds.Width > 250 || detail.DesiredSize.Width > 250 || detail.Bounds.Height < 20, $"'{detail.Text}' is squeezed to {detail.Bounds.Width} px");
                Assert.Equal(title.TranslatePoint(default, window)!.Value.X, detail.TranslatePoint(default, window)!.Value.X, 1);
            }

            Assert.Contains(model.ReadyRows, row => row.State == CheckState.Warning);
            window.Close();
        });
    }

    [Fact]
    public async Task TheHeroArtKeepsItsShape()
    {
        await Run(() =>
        {
            var hero = new HeroView { DataContext = Model(), Width = 886, Height = 274 };
            var window = new Window { Width = 1000, Height = 400, Content = hero };
            window.Show();
            using var art = new WriteableBitmap(new PixelSize(1335, 633), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            hero.Art.Source = art;
            Pump(window);
            Assert.Equal(886 * 1.25, hero.Art.Width, 1);
            Assert.Equal(hero.Art.Width * 633 / 1335, hero.Art.Height, 1);
            Assert.Equal(886 * 0.116, hero.Art.Margin.Left, 1);
            hero.Art.Source = null;
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheGameFilesPageShowsWhatTheCheckFound()
    {
        string folder = Path.Combine(_root, "CUSA47999");
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
        Directory.CreateDirectory(Path.Combine(folder, "image"));
        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), new byte[64]);
        var model = new UnixLauncherModel(Paths(), AppContext.BaseDirectory, folder);
        await Run(async () =>
        {
            var window = Show(model);
            await model.RefreshAsync();
            model.Page = LauncherPage.GameFiles;
            Pump(window);
            var texts = window.PageHost.GetVisualDescendants().OfType<TextBlock>().ToArray();
            Assert.Equal(model.StatusText, texts.Single(t => t.Name == "StatusValue").Text);
            foreach (string fact in new[] { model.TitleText, model.BuildDetail, model.LanguagesDetail, model.FolderSize, model.ChecksText, model.ContentsText })
            {
                Assert.Contains(texts, t => t.Text == fact);
            }

            ItemsControl problems = window.PageHost.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "ProblemList");
            Assert.NotEmpty(model.Folder.ProblemList);
            Assert.Equal(model.Folder.ProblemList.Count, problems.ItemCount);
            Assert.True(problems.IsEffectivelyVisible);
            Assert.Contains(problems.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == model.Folder.ProblemList[0] && t.IsEffectivelyVisible);
            window.Close();
        });
    }

    [Fact]
    public async Task ErrorsOfTheWindowThreadGoToTheCrashLogAndTheToast()
    {
        var model = Model();
        await Run(() =>
        {
            var window = Show(model);
            using (Program.HookCrashes(model))
            {
                Dispatcher.UIThread.Post(() => throw new InvalidOperationException("deliberate error of a button"));
                Pump(window);
            }

            string log = Path.Combine(Paths().DataDirectory, UnixLauncherCrash.FileName);
            Assert.Contains("deliberate error of a button", File.ReadAllText(log), StringComparison.Ordinal);
            Assert.True(window.ToastHost.IsEffectivelyVisible);
            Assert.Equal("The launcher hit an error: deliberate error of a button. Details are in " + log + ".", window.ToastText.Text);
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheSelfTestFailsWhenAStepOfTheRefreshFailed()
    {
        // The refresh reports an error of one of its steps and goes on (UnixLauncherStateTests.AStepOfTheRefreshThatFailsDoesNotSkipTheOnesAfterItAndIsReportedOnce),
        // which is right for the window. The verification modes run that refresh: with the error caught they went on and passed ("SELFTEST PASS", exit 0, and
        // launcher_crash.log in a profile nobody reads), where the error itself had ended them. A hosted Mac's release check is these modes and nothing else
        string report = Path.Combine(_root, "selftest.txt");
        await Run(() =>
        {
            Assert.Equal(1, Program.SelfTest(FailingRefresh(), report, shots: null, updatedFrom: null));
            Assert.Equal("SELFTEST FAIL: " + RefreshFailed, File.ReadAllText(report).TrimEnd());
            // the launcher an update restarted into runs this check too, and the update's end-to-end tests read its report
            Assert.Equal(1, Program.SelfTest(FailingRefresh(), report, shots: null, updatedFrom: new ReleaseVersion(0, 0, 1)));
            Assert.Equal("SELFTEST FAIL: " + RefreshFailed, File.ReadAllText(report).TrimEnd());
            // the same profile without the error passes as it always did (the crash log the failed ones left does not count)
            Assert.True(File.Exists(Path.Combine(Paths().DataDirectory, UnixLauncherCrash.FileName)));
            Assert.Equal(0, Program.SelfTest(Model(), report, shots: null, updatedFrom: null));
            Assert.StartsWith("SELFTEST PASS: 8 pages, ", File.ReadAllText(report), StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheUpdateSelfTestFailsWhenAStepOfTheRefreshFailed()
    {
        string report = Path.Combine(_root, "update-selftest.txt");
        await Run(() =>
        {
            Assert.Equal(1, Program.UpdateSelfTest(FailingRefresh(), Paths(), report, shots: null));
            // said before GitHub's stand-in is asked: without the check the report was about the update it did not find there
            Assert.Equal("UPDATE SELFTEST FAIL: " + RefreshFailed, File.ReadAllText(report).TrimEnd());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheRenderCheckFailsWhenAStepOfTheRefreshFailed()
    {
        string pictures = Path.Combine(_root, "pictures");
        Directory.CreateDirectory(pictures);
        await Run(() =>
        {
            Assert.Equal(1, UnixOffscreenRender.Pages(FailingRefresh(), pictures, size: null));
            // it stops at the refresh, where the error stopped it: no picture (without the check it drew all of them and ended with 0)
            Assert.Empty(Directory.GetFiles(pictures));
            return Task.CompletedTask;
        });
    }

    /// <summary>What a verification mode says of <see cref="FailingRefresh"/>'s error.</summary>
    private const string RefreshFailed = "the refresh failed: InvalidOperationException: deliberate error of the backups step";

    /// <summary>A model whose refresh fails in the step that lists the backups, without a folder that cannot be listed: a listener throws when it hears of them.</summary>
    private UnixLauncherModel FailingRefresh()
    {
        UnixLauncherModel model = Model();
        bool thrown = false;
        model.PropertyChanged += (_, e) =>
        {
            if (thrown || e.PropertyName != nameof(model.NoBackups)) { return; }
            thrown = true;
            throw new InvalidOperationException("deliberate error of the backups step");
        };
        return model;
    }

    [Fact]
    public async Task TheRenderCheckFailsWhenTheLauncherReportedAnErrorWhileItsWindowWasShown()
    {
        // The window coming to the front reports what goes wrong there and goes on (RunningGameTests.AnErrorWhileTheWindowComesToTheFrontIsReportedAndDoesNotLeaveTheHandler),
        // which is right for the window. The verification modes show that window for their pictures: with the error caught they went on and ended with 0, over
        // a launcher_crash.log in a profile nobody reads, where the error itself had ended them. A hosted Mac's release check reads only their exit codes
        string pictures = Directory.CreateDirectory(Path.Combine(_root, "pictures")).FullName;
        await Run(() =>
        {
            UnixLauncherModel model = FailingInFront();
            Assert.Equal(1, UnixOffscreenRender.Pages(model, pictures, size: null));
            // said at the end: the refresh itself was fine, the one error was reported while the window was up, and every picture is there
            Assert.Null(model.RefreshError);
            Assert.Equal(1, model.ReportedErrors);
            Assert.Equal(ReportedInFront, Program.ReportedFailure(model));
            Assert.True(File.Exists(Path.Combine(pictures, "play.png")));
            Assert.True(File.Exists(Path.Combine(pictures, "update_failed.png")));
            return Task.CompletedTask;
        });

        // the same profile without the error passes with the same pictures (the crash log the failed one left does not count)
        string fine = Directory.CreateDirectory(Path.Combine(_root, "pictures-fine")).FullName;
        await Run(() =>
        {
            Assert.True(File.Exists(Path.Combine(Paths().DataDirectory, UnixLauncherCrash.FileName)));
            UnixLauncherModel model = Model();
            Assert.Equal(0, UnixOffscreenRender.Pages(model, fine, size: null));
            Assert.Equal(0, model.ReportedErrors);
            Assert.Null(Program.ReportedFailure(model));
            Assert.Equal(Directory.GetFiles(pictures).Select(Path.GetFileName).Order(), Directory.GetFiles(fine).Select(Path.GetFileName).Order());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheSelfTestFailsWhenTheLauncherReportedAnErrorWhileItsWindowWasShown()
    {
        string report = Path.Combine(_root, "selftest.txt");
        string shots = Path.Combine(_root, "shots");
        await Run(() =>
        {
            // --selftest shows the window for --shots only
            Assert.Equal(1, Program.SelfTest(FailingInFront(), report, shots, updatedFrom: null));
            Assert.Equal("SELFTEST FAIL: " + ReportedInFront, File.ReadAllText(report).TrimEnd());
            Assert.True(File.Exists(Path.Combine(shots, "play.png")));
            // the same profile without the error passes as it always did
            Assert.Equal(0, Program.SelfTest(Model(), report, shots, updatedFrom: null));
            Assert.StartsWith("SELFTEST PASS: 8 pages, ", File.ReadAllText(report), StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheUpdateSelfTestFailsWhenTheLauncherReportedAnErrorWhileItsWindowWasShown()
    {
        // run to its end on an update the launcher refuses (a checksum that does not match: nothing is installed, nothing restarts). That end fails either
        // way; its line is about the error the launcher reported, where it was about the update alone
        using var server = new FakeGitHub();
        Offer(server, new string('0', 64));
        string report = Path.Combine(_root, "update-selftest.txt");
        await Run(() =>
        {
            UnixLauncherModel model = FailingInFront(server.Base, LinuxRelease.Folder(_root, "install"));
            Assert.Equal(1, Program.UpdateSelfTest(model, Paths(), report, Path.Combine(_root, "update-shots")));
            Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
            Assert.Equal("UPDATE SELFTEST FAIL: " + ReportedInFront, File.ReadAllText(report).TrimEnd());
            // the same refused update without the error: the update's own line
            model = Model(server.Base, LinuxRelease.Folder(_root, "install-fine"));
            Assert.Equal(1, Program.UpdateSelfTest(model, Paths(), report, Path.Combine(_root, "update-shots-fine")));
            Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
            Assert.StartsWith("UPDATE SELFTEST FAIL: Update to " + Version.Tag + "; ", File.ReadAllText(report), StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    /// <summary>What a verification mode says of <see cref="FailingInFront"/>'s error.</summary>
    private const string ReportedInFront = "the launcher reported an error: InvalidOperationException: deliberate error while the window comes to the front";

    /// <summary>
    /// A model whose refresh works and whose window fails as it comes to the front, which a window does when it is shown: a listener throws the first time
    /// the play state is said once a window of this model has opened. The refresh says the play state too; in every verification mode it is over by then.
    /// </summary>
    private UnixLauncherModel FailingInFront(Uri? api = null, string? install = null)
    {
        UnixLauncherModel model = Model(api, install);
        bool opened = false, thrown = false;
        IDisposable watch = Window.WindowOpenedEvent.AddClassHandler(typeof(MainWindow), (window, _) => opened |= ReferenceEquals((window as MainWindow)?.DataContext, model));
        model.PropertyChanged += (_, e) =>
        {
            if (!opened || thrown || e.PropertyName != nameof(model.PlayLabel)) { return; }
            thrown = true;
            watch.Dispose();
            throw new InvalidOperationException("deliberate error while the window comes to the front");
        };
        return model;
    }

    [Fact]
    public async Task ASecondLauncherOnTheSameProfileOnlySaysTheFirstIsOpen()
    {
        await Run(() =>
        {
            UnixLauncherApp.AlreadyOpen = true;
            try
            {
                Window window = new UnixLauncherApp().CreateMainWindow();
                Assert.IsNotType<MainWindow>(window);
                window.Show();
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == UnixLauncherApp.AlreadyOpenText);
                window.Close();
            }
            finally
            {
                UnixLauncherApp.AlreadyOpen = false;
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheAppUsesTheModelConfiguredByProgram()
    {
        await Run(async () =>
        {
            var model = Model();
            UnixLauncherApp.Model = model;
            try
            {
                var app = new UnixLauncherApp();
                var window = app.CreateMainWindow();
                Assert.Same(model, window.DataContext);
                window.Close();
            }
            finally
            {
                UnixLauncherApp.Model = null;
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheHeadlessDispatcherPropagatesAnInnerTaskFailure()
    {
        Exception? error = await Record.ExceptionAsync(() => Run(() => Task.FromException(new InvalidOperationException("deliberate dispatcher failure"))));
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void RestartKeepsOnlyTheUsersProfileAndGamePaths()
    {
        Assert.Equal(["--data", Path.GetFullPath("test-profile"), "--game", Path.GetFullPath("test-game")], Program.ProfileArguments("test-profile", "test-game"));
        Assert.Empty(Program.ProfileArguments(null, null));
        Assert.Equal(["--data", Path.GetFullPath("p"), "--update-api", "http://127.0.0.1:9/", "--after-update", "4242", "--updated-from", "0.1.6"],
            Program.RestartArguments("p", null, "http://127.0.0.1:9/", 4242, new ReleaseVersion(0, 1, 6)));
    }

    [Theory]
    [InlineData(1200, 780)]
    [InlineData(800, 460)]
    public async Task EveryCardAndItsControlsStayInsideThePage(double width, double height)
    {
        // the scrolling pages measured their cards 24 px wider than they laid them out: the right border, its corners and the Game files page's Open button were
        // cut; and at the smallest window (a Steam Deck at 150 %) nothing in a card may run past it
        var model = Model();
        await Run(async () =>
        {
            var window = new MainWindow(model) { Width = width, Height = height };
            window.Show();
            await model.RefreshAsync();
            foreach (LauncherPage page in Enum.GetValues<LauncherPage>())
            {
                model.Page = page;
                Pump(window);
                double right = window.PageHost.TranslatePoint(new Point(window.PageHost.Bounds.Width, 0), window)!.Value.X;
                Border[] cards = window.PageHost.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("Card")).ToArray();
                Assert.NotEmpty(cards);
                foreach (Border card in cards)
                {
                    double cardRight = RightEdge(card, window);
                    Assert.True(cardRight <= right + 0.5, $"{page} at {width} × {height}: a card ends at {cardRight:0.0}, past the page's right edge at {right:0.0}");
                    foreach (Control control in card.GetVisualDescendants().OfType<Control>().Where(c => c is Button or ListBox or CheckBox or TextBox && c.IsEffectivelyVisible))
                    {
                        Assert.True(RightEdge(control, window) <= cardRight + 0.5, $"{page} at {width} × {height}: a {control.GetType().Name} ends at {RightEdge(control, window):0.0}, past its card at {cardRight:0.0}");
                    }

                    // the labels of the choices (languages, prompts, sizes) are drawn whole, smaller if need be, never cut
                    foreach (ListBoxItem item in card.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsEffectivelyVisible))
                    {
                        foreach (TextBlock label in item.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
                        {
                            Assert.True(label.TextLayout.Width <= label.Bounds.Width + 0.5, $"{page} at {width} × {height}: '{label.Text}' needs {label.TextLayout.Width:0.0} px and got {label.Bounds.Width:0.0}");
                            Assert.True(RightEdge(label, window) <= RightEdge(item, window) + 0.5, $"{page} at {width} × {height}: '{label.Text}' ends at {RightEdge(label, window):0.0}, past its choice at {RightEdge(item, window):0.0}");
                        }
                    }
                }

                if (page == LauncherPage.Play)
                {
                    // Quick settings goes under the ready check on a narrow page, where its choices have room
                    Border quick = window.PageHost.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "QuickSettings");
                    Border ready = window.PageHost.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("Card"));
                    Assert.Equal(width < 1000, quick.TranslatePoint(default, window)!.Value.X - ready.TranslatePoint(default, window)!.Value.X < 1);
                }
            }

            window.Close();
        });
    }

    private static double RightEdge(Visual visual, Visual window) => visual.TranslatePoint(new Point(visual.Bounds.Width, 0), window)!.Value.X;

    [Fact]
    public async Task EveryPageOpensAtItsTopInTheSmallestWindow()
    {
        // a choice list (the window sizes, the languages, the achievement tabs) brought its chosen entry into view when the page opened, which scrolled the page
        // down to that list wherever it lay below the fold: in a Steam Deck's 800 × 460 window the Display page opened in its middle
        var model = Model();
        await Run(async () =>
        {
            var window = new MainWindow(model) { Width = 800, Height = 460 };
            window.Show();
            await model.RefreshAsync();
            foreach (LauncherPage page in Enum.GetValues<LauncherPage>())
            {
                model.Page = page;
                Pump(window);
                ScrollViewer scroll = window.PageHost.GetVisualDescendants().OfType<ScrollViewer>().First(viewer => viewer.Classes.Contains("Page"));
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height, $"{page} does not scroll at this size");
                Assert.True(scroll.Offset.Y == 0, $"{page} opened scrolled down by {scroll.Offset.Y:0}");
            }

            window.Close();
        });
    }

    [Fact]
    public void TheRenderCheckTakesAWindowSize()
    {
        Assert.Equal((800, 460), Program.WindowSize("800x460"));
        Assert.Equal((1200, 780), Program.WindowSize("1200X780"));
        foreach (string? bad in new[] { null, "", "800", "800x", "x460", "0x460", "800x460x2", "wide", "-800x460" })
        {
            Assert.Null(Program.WindowSize(bad));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APageNamesTheKeysAndLicencesOfItsPlatform(bool mac)
    {
        var model = new UnixLauncherModel(Paths(), AppContext.BaseDirectory, Path.Combine(_root, "no-game"))
        {
            UpdateSource = UpdateSource.ForLoopback(new Uri("http://127.0.0.1:9/")),
            CurrentVersionOverride = new ReleaseVersion(0, 0, 1),
            MacBundleUpdate = false,
            IsMac = mac,
            DockIcon = _ => true,
        };
        await Run(() =>
        {
            var window = Show(model);
            // a Mac keyboard's F11 lowers the volume and Fn+F11 shows the desktop; F5 and F9 need Fn; its Delete key is Backspace
            model.Page = LauncherPage.Display;
            Pump(window);
            string display = PageText(window);
            Assert.Contains(mac ? "In the game, Option+Return or Config › System › Display Mode switches it" : "In the game, Alt+Enter, F11 or Config › System › Display Mode switches it", display, StringComparison.Ordinal);
            Assert.Equal(!mac, display.Contains("F11", StringComparison.Ordinal));
            model.Page = LauncherPage.Controls;
            Pump(window);
            string controls = PageText(window);
            Assert.Equal(mac, controls.Contains("Fn+F5 / Fn+F9", StringComparison.Ordinal));
            Assert.Equal(mac, controls.Contains("Fn+Delete", StringComparison.Ordinal));
            Assert.Equal(!mac, controls.Contains("Backspace (held)", StringComparison.Ordinal));
            Assert.Equal(!mac, controls.Contains("Alt+Enter", StringComparison.Ordinal));

            // the FFmpeg the download carries: the Mac app builds its own LGPL 2.1-or-later FFmpeg and keeps the notices inside Tsukihime.app
            model.Page = LauncherPage.Help;
            Pump(window);
            TextBlock[] help = window.PageHost.GetVisualDescendants().OfType<TextBlock>().ToArray();
            Assert.Equal(mac ? "LGPL 2.1+" : "LGPL v3", help.Single(t => t.Name == "FfmpegLicenceTag").Text);
            string notices = help.Single(t => t.Name == "FfmpegLicenceText").Text + " " + help.Single(t => t.Name == "NoticesText").Text;
            Assert.Equal(mac, notices.Contains("Tsukihime.app/Contents/Resources/licenses", StringComparison.Ordinal));
            IReadOnlyList<string> shipped = mac ? ReleaseRules.MacLicences : ReleaseRules.LinuxLicences;
            string[] named = Regex.Matches(notices, @"(?:licenses/)?((?:FFmpeg|LICENSE)[A-Za-z0-9.\-]*\.(?:txt|md))").Select(m => "licenses/" + m.Groups[1].Value).ToArray();
            Assert.True(named.Length >= 4, string.Join(", ", named));
            Assert.All(named, file => Assert.Contains(file, shipped));
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task OnAMacTheDockTileTakesTheFaceFromThePlayersFiles()
    {
        // Avalonia's window icon does nothing on macOS: the launcher's Dock tile is the application's, so the face goes there
        var docked = new List<string?>();
        var model = new UnixLauncherModel(Paths(), AppContext.BaseDirectory, Path.Combine(_root, "no-game"))
        {
            UpdateSource = UpdateSource.ForLoopback(new Uri("http://127.0.0.1:9/")),
            CurrentVersionOverride = new ReleaseVersion(0, 0, 1),
            MacBundleUpdate = false,
            IsMac = true,
            DockIcon = png => { docked.Add(png); return true; },
        };
        await Run(async () =>
        {
            var window = Show(model);
            Assert.NotEmpty(docked);
            Assert.All(docked, png => Assert.Null(png));
            using var face = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            await model.UseGameIconAsync(Path.Combine(_root, "game"), face);
            Pump(window);
            string png = Assert.IsType<string>(model.GameIconPng);
            Assert.Equal(png, docked[^1]);
            Assert.Equal(Path.Combine(Paths().DataDirectory, "icons", "tsukihime.png"), png);
            Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, File.ReadAllBytes(png)[..4]);
            window.Close();
        });

        // nowhere else, and never from a thread that is not AppKit's
        var linux = new List<string?>();
        await Run(() =>
        {
            var window = Show(new UnixLauncherModel(Paths(), AppContext.BaseDirectory, Path.Combine(_root, "no-game")) { IsMac = false, MacBundleUpdate = false, DockIcon = png => { linux.Add(png); return true; } });
            window.Close();
            return Task.CompletedTask;
        });
        Assert.Empty(linux);
        Assert.False(MacDock.SetApplicationIcon(null));
    }

    [Fact]
    public async Task TheMacAppMenuIsTheLaunchersOwnWithoutAvalonias()
    {
        await Run(() =>
        {
            var app = new UnixLauncherApp();
            UnixLauncherApp.UseMacMenu(app);
            Assert.Equal("Tsukihime", app.Name);
            NativeMenuItem about = Assert.IsType<NativeMenuItem>(Assert.Single(Assert.IsType<NativeMenu>(NativeMenu.GetMenu(app)).Items));
            Assert.Equal("About Tsukihime", about.Header);
            var model = Model();
            UnixLauncherApp.Model = model;
            try
            {
                about.Command!.Execute(null);
                Assert.Equal(LauncherPage.Help, model.Page);
            }
            finally
            {
                UnixLauncherApp.Model = null;
            }

            // the running launcher has that menu on a Mac only
            Assert.Equal(OperatingSystem.IsMacOS(), NativeMenu.GetMenu(Application.Current!) is not null);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheWindowFitsASteamDecksDesktop()
    {
        // 1280 × 800 with the 44 px panel at 100 %, 125 % and 150 %: PLAY and the update button stay on the screen (every page scrolls)
        await Run(() =>
        {
            foreach (double scaling in new[] { 1.0, 1.25, 1.5 })
            {
                var window = new MainWindow(Model());
                window.FitTo(new PixelRect(0, 0, 1280, 756), scaling);
                double width = 1280 / scaling, height = 756 / scaling;
                Assert.True(window.Width + 24 <= width + 0.5 && window.Height + 40 <= height, $"{scaling:P0}: {window.Width} × {window.Height} in {width:0} × {height:0}");
                Assert.True(window.Width >= window.MinWidth && window.Height >= window.MinHeight);
            }

            var big = new MainWindow(Model());
            big.FitTo(new PixelRect(0, 0, 2560, 1400), 1.0);
            Assert.Equal((1200, 780), (big.Width, big.Height));

            // at the smallest size every page's entry is in the sidebar, clear of the version note (which gives way), and PLAY is on the screen: the eight
            // entries need 502 px as they are drawn in a tall window, so a short one draws them tighter
            foreach (double height in new[] { 460.0, MainWindow.SidebarCompactBelow - 1, MainWindow.SidebarCompactBelow, MainWindow.SidebarNoteMinHeight - 1, MainWindow.SidebarNoteMinHeight, 780.0 })
            {
                var window = new MainWindow(Model()) { Width = 800, Height = height };
                window.Show();
                Pump(window);
                ListBoxItem[] entries = window.Nav.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
                Assert.Equal(8, entries.Length);
                ListBoxItem last = entries[^1];
                double entriesEnd = last.TranslatePoint(new Point(0, last.Bounds.Height), window)!.Value.Y;
                double noteTop = window.SidebarNote.IsEffectivelyVisible ? window.SidebarNote.TranslatePoint(default, window)!.Value.Y : window.Bounds.Height;
                Assert.True(entriesEnd <= noteTop, $"{height}: the entries end at {entriesEnd:0}, the note starts at {noteTop:0}");
                Assert.True(window.PlayButton.TranslatePoint(new Point(0, window.PlayButton.Bounds.Height), window)!.Value.Y <= window.Bounds.Height);
                Assert.Equal(height >= MainWindow.SidebarNoteMinHeight, window.SidebarNote.IsEffectivelyVisible);

                // only a short window is tighter: from SidebarCompactBelow on the sidebar is the one it always was (the list starts at 118, an entry takes 48)
                bool compact = height < MainWindow.SidebarCompactBelow;
                double listTop = window.Nav.TranslatePoint(default, window)!.Value.Y;
                Assert.True(compact ? listTop < 118 : listTop == 118, $"{height}: the list starts at {listTop:0}");
                Assert.All(entries, entry => Assert.Equal(compact ? 44 : 48, entry.Bounds.Height));
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheLanguageNamesFindTheCjkFacesOfSteamOsAndCommonDesktops()
    {
        await Run(() =>
        {
            // SteamOS (Arch's noto-fonts-cjk), Fedora and Ubuntu name their CJK faces "Noto Sans CJK JP / SC / TC"
            foreach ((string key, string family) in new[] { ("JapaneseFont", "Noto Sans CJK JP"), ("ChineseSimplifiedFont", "Noto Sans CJK SC"), ("ChineseTraditionalFont", "Noto Sans CJK TC") })
            {
                Assert.Contains(family, Assert.IsType<FontFamily>(Application.Current!.FindResource(key)).FamilyNames);
            }

            return Task.CompletedTask;
        });
    }

    private static string PageText(MainWindow window) =>
        string.Join("\n", window.PageHost.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

    [Fact]
    public async Task ThePortableSwitchIsOnTheLinuxSavesPageOnly()
    {
        const string label = "Keep saves next to the program (portable)";
        var linux = new UnixLauncherModel(Paths(), AppContext.BaseDirectory, Path.Combine(_root, "no-game")) { IsLinux = true };
        await Run(() =>
        {
            var window = Show(linux);
            linux.Page = LauncherPage.Saves;
            Pump(window);
            CheckBox toggle = window.PageHost.GetVisualDescendants().OfType<CheckBox>().Single(box => ControlAutomationPeer.CreatePeerForElement(box).GetName() == label);
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.Same(linux.PortableSavesCommand, toggle.Command);
            Assert.True(linux.PortableSavesCommand.CanExecute(null));
            window.Close();
            return Task.CompletedTask;
        });

        var mac = new UnixLauncherModel(Paths(), AppContext.BaseDirectory, Path.Combine(_root, "no-game")) { IsLinux = false };
        await Run(() =>
        {
            var window = Show(mac);
            mac.Page = LauncherPage.Saves;
            Pump(window);
            Assert.DoesNotContain(window.PageHost.GetVisualDescendants().OfType<CheckBox>(), box => ControlAutomationPeer.CreatePeerForElement(box).GetName() == label && box.IsEffectivelyVisible);
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ThePortableSwitchCopiesTheCurrentProfile()
    {
        LauncherPaths paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.SettingsFile, "language=en\n");
        string program = Path.Combine(_root, "program");
        Directory.CreateDirectory(program);
        var model = new UnixLauncherModel(paths, program, Path.Combine(_root, "no-game")) { IsLinux = true, AnyGameRunning = () => false };
        await Run(async () =>
        {
            var window = Show(model);
            model.Page = LauncherPage.Saves;
            Pump(window);
            Assert.True(model.PortableSavesCommand.CanExecute(null), $"portable command disabled; game running: {model.GameRunning}");
            model.PortableSavesCommand.Execute(null);
            await Until(window, () => model.Toast.Length > 0);
            Assert.Contains("Close and reopen", model.Toast, StringComparison.Ordinal);
            Assert.True(model.PortableSaves);
            Assert.False(model.PortableSavesCommand.CanExecute(null), "the copied profile cannot be switched again before the launcher reopens");
            Assert.Equal("language=en\n", File.ReadAllText(Path.Combine(program, "save", "settings.ini")));
            Assert.True(File.Exists(paths.SettingsFile));
            model.VSync = !model.VSync;
            await model.SetGameFolderAsync(Path.Combine(_root, "still-not-a-game"));
            Assert.Equal("language=en\n", File.ReadAllText(paths.SettingsFile));
            Assert.Contains("before using the profile", model.Toast, StringComparison.Ordinal);
            window.Close();
        });
    }

    [Fact]
    public async Task WhileThePortableSwitchCopiesNothingElseTouchesTheProfile()
    {
        // v0.1.11 review: the switch asks once, before copying, whether a game runs. PLAY, Back up, Import and Restore did not wait for the copy, so a game
        // started or a save restored during it landed in the profile being left. They are disabled and refused until the copy has ended
        LauncherPaths paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.SettingsFile, "language=en\n");
        File.WriteAllBytes(paths.SaveFile, [1, 2, 3]);
        string program = Path.Combine(_root, "program");
        Directory.CreateDirectory(program);
        using var copying = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var model = new UnixLauncherModel(paths, program, Path.Combine(_root, "no-game"))
        {
            IsLinux = true,
            AnyGameRunning = () => false,
            PortableCopyStep = _ => { copying.Set(); release.Wait(TimeSpan.FromSeconds(60)); },
        };
        await Run(async () =>
        {
            var window = Show(model);
            model.Page = LauncherPage.Saves;
            Pump(window);
            Assert.True(model.BackupNowCommand.CanExecute(null));
            model.PortableSavesCommand.Execute(null);
            await Until(window, () => copying.IsSet);
            try
            {
                Assert.False(model.PlayCommand.CanExecute(null), "PLAY during the copy");
                Assert.False(model.BackupNowCommand.CanExecute(null), "Back up during the copy");
                Assert.False(model.ImportCommand.CanExecute(null), "Import during the copy");
                Assert.False(model.RestoreCommand.CanExecute(null), "Restore during the copy");
                int backups = Directory.GetDirectories(paths.BackupsDirectory).Length;
                model.BackupNowCommand.Execute(null);                     // a direct call is refused too
                Assert.Equal(backups, Directory.GetDirectories(paths.BackupsDirectory).Length);
                Assert.Contains("copy", model.Toast, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                release.Set();
            }

            await Until(window, () => model.Toast.Contains("Close and reopen", StringComparison.Ordinal));
            Assert.True(model.PortableSaves);
            window.Close();
        });
    }

    private void Offer(FakeGitHub server, string checksum, string? notes = null)
    {
        string name = PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64);
        string asset = Path.Combine(_root, name);
        File.WriteAllText(asset, "not a release");
        string sums = Path.Combine(_root, UpdateSource.SumsAssetName);
        File.WriteAllText(sums, checksum + "  " + name + "\n");
        server.List(UpdateSource.DefaultRepository, server.Release(UpdateSource.DefaultRepository, Version.Tag, [(name, asset), (UpdateSource.SumsAssetName, sums)], body: notes));
    }

    private LauncherPaths Paths() => new(Path.Combine(_root, "profile"));

    private UnixLauncherModel Model(Uri? api = null, string? install = null) => new(Paths(), install ?? AppContext.BaseDirectory, Path.Combine(_root, "no-game"))
    {
        UpdateSource = UpdateSource.ForLoopback(api ?? new Uri("http://127.0.0.1:9/")),
        AssetName = version => PlatformUpdate.AssetNameFor(version, "linux", Architecture.X64),
        CurrentVersionOverride = new ReleaseVersion(0, 0, 1),
        // the Linux layout on every host, a Mac included
        MacBundleUpdate = false,
    };

    internal static MainWindow Show(UnixLauncherModel model)
    {
        var window = new MainWindow(model);
        window.Show();
        Pump(window);
        return window;
    }

    /// <summary>A window whose Achievements timer looks every <paramref name="watchInterval"/> (an hour: only the test's own looks).</summary>
    internal static MainWindow Show(UnixLauncherModel model, TimeSpan watchInterval)
    {
        var window = new MainWindow(model) { AchievementsWatchInterval = watchInterval };
        window.Show();
        Pump(window);
        return window;
    }

    internal static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    internal static async Task Until(Window window, Func<bool> done)
    {
        for (int i = 0; i < 500 && !done(); i++)
        {
            Pump(window);
            await Task.Delay(20);
        }

        Pump(window);
    }

    private static Border Chrome(Button button) => button.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Chrome");

    private static Color BrushColor(object? brush) => brush switch
    {
        ISolidColorBrush solid => solid.Color,
        _ => throw new InvalidOperationException("not a solid colour: " + brush),
    };

    /// <summary>A process that ends after about a second with <paramref name="code"/> (the stand-in for a game that failed).</summary>
    internal static Process Exiting(int code)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 2 127.0.0.1 >NUL & exit " + code)
            : new ProcessStartInfo("/bin/sh", ["-c", "sleep 1; exit " + code]);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        return Process.Start(start) ?? throw new InvalidOperationException("the stand-in game did not start");
    }

    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(TestLauncherApp), AvaloniaTestIsolationLevel.PerAssembly));

    internal static async Task Run(Func<Task> test)
    {
        await Task.Run(async () =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            // the Func<Task<T>> overload: the session runs the window's jobs until the test's task ends (a Func<Task> would be taken as Func<T> and its awaits
            // would wait for jobs nobody runs)
            await Session.Value.Dispatch(async () =>
            {
                await test();
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }
}
