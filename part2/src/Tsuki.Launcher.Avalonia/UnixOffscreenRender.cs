using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher.Avalonia;

/// <summary>
/// --render DIR: every page, then the question, a toast, the footer's gold button and the update overlay in its states, drawn offscreen to PNG files with the names
/// the Windows launcher's render check uses (play.png, update_offer.png …), so that the two can be laid side by side. Nothing is asked of GitHub.
/// </summary>
internal static class UnixOffscreenRender
{
    public static int Run(UnixLauncherModel model, string folder)
    {
        Directory.CreateDirectory(folder);
        AppBuilder.Configure<UnixLauncherApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        AvaloniaSynchronizationContext.InstallIfNeeded();
        var window = new MainWindow(model);
        Wait(model.RefreshAsync());
        window.Show();
        foreach (LauncherPage page in Enum.GetValues<LauncherPage>())
        {
            model.Page = page;
            Draw(window, folder, page.ToString().ToLowerInvariant());
        }

        model.Page = LauncherPage.Saves;
        Task question = model.ImportAsync("savedata000");
        Draw(window, folder, "confirm");
        window.QuestionNo.Command?.Execute(null);
        Wait(question);
        model.Page = LauncherPage.Play;
        model.Notify("The game could not be started: example message for the render check.");
        Draw(window, folder, "toast");

        ReleaseVersion next = new(model.CurrentVersion.Major, model.CurrentVersion.Minor, model.CurrentVersion.Patch + 1);
        string asset = model.AssetName(next);
        var sample = new ReleaseInfo(
            next,
            next.Tag,
            $"Tsukihime PC port {next.Tag}",
            UpdateFeed.PlainText(
                "## Fixed\n\n- **Sample notes for the render check.** A release's notes appear here as plain text, scrollable when they are long.\n" +
                "- Headings, **bold** and `code` marks of GitHub's Markdown are taken out; [links](https://example.invalid) keep their words.\n\n" +
                "## How to update\n\nPress **Update now**: the launcher downloads the release and SHA256SUMS.txt, checks the checksum, replaces its files and restarts.\n\n" +
                "> Fan-made and unofficial. This contains no game data; you must own the game."),
            "https://github.com/" + UpdateSource.DefaultRepository + "/releases/tag/" + next.Tag,
            true,
            new ReleaseAsset(asset, new Uri("https://github.com/" + UpdateSource.DefaultRepository + "/releases/download/" + next.Tag + "/" + asset), 110_268_124),
            new ReleaseAsset(UpdateSource.SumsAssetName, new Uri("https://github.com/" + UpdateSource.DefaultRepository + "/releases/download/" + next.Tag + "/" + UpdateSource.SumsAssetName), 98));
        model.Notify(string.Empty);
        model.PreviewUpdate(sample);
        Draw(window, folder, "update_footer");
        model.PreviewUpdateState(UnixUpdateStage.Offer, null, string.Empty);
        Draw(window, folder, "update_offer");
        model.PreviewUpdateState(UnixUpdateStage.Working, new UpdateProgress(UpdateStep.Downloading, new DownloadProgress(46_137_344, 110_268_124)), string.Empty);
        Draw(window, folder, "update_progress");
        model.PreviewUpdateState(UnixUpdateStage.Offer, null, LauncherTexts.UpdateGameRunning(PlatformServices.AppName));
        Draw(window, folder, "update_game_running");
        model.PreviewUpdateState(UnixUpdateStage.Failed, null, UnixUpdateChecks.Message(OperatingSystem.IsMacOS() ? "/Volumes/Games" : "/opt/Tsukihime", OperatingSystem.IsMacOS(), translocated: false, asset));
        Draw(window, folder, "update_not_writable");
        model.PreviewUpdateState(UnixUpdateStage.Failed, null, LauncherTexts.UpdateFailed($"the downloaded {asset} does not match {UpdateSource.SumsAssetName} (SHA-256 3f7a09c2d1e4...), so it was not installed"));
        Draw(window, folder, "update_failed");
        model.PreviewUpdateState(UnixUpdateStage.Hidden, null, string.Empty);
        window.Close();
        return 0;
    }

    private static void Wait(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (!task.IsCompleted) { Thread.Sleep(5); }
        }

        task.GetAwaiter().GetResult();
    }

    private static void Draw(MainWindow window, string folder, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        string path = Path.Combine(folder, name + ".png");
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The headless renderer returned no frame.");
        frame.Save(path);
        Console.WriteLine(path);
    }
}
