// The update button and overlay of the macOS / Linux launcher: the same steps and sentences as the Windows launcher (LauncherModel.Updates.cs, texts from
// Core/LauncherTexts): refused while the game of the folder runs, when the folder cannot be written or has no room, or while the last update's files are in use;
// Cancel is not a failure; a failure says that nothing was changed; the game is checked again after the download; every step goes to launcher.log.
using System.Globalization;
using System.Net.Http;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher.Avalonia;

public sealed partial class UnixLauncherModel
{
    /// <summary>The overlay when the launcher was started in a way it cannot restart from (a test without a restart).</summary>
    public const string CannotRestart = "The launcher cannot restart itself from this session. Download the release instead.";

    private readonly LauncherLog _log;
    private LauncherBuild? _build;
    private ReleaseInfo? _update;
    private UnixUpdateStage _updateStage;
    private UpdateProgress? _updateProgress;
    private CancellationTokenSource? _updateCancel;
    private bool _checking;
    private bool _updateRunning;
    private UpdateCheckOutcome? _lastCheck;
    private string _updateStatus = string.Empty;
    private string _updateMessage = string.Empty;

    public RelayCommand UpdateButtonCommand { get; private set; } = null!;
    public RelayCommand ShowUpdateCommand { get; private set; } = null!;
    public RelayCommand UpdateNowCommand { get; private set; } = null!;
    public RelayCommand UpdateLaterCommand { get; private set; } = null!;
    public RelayCommand CancelUpdateCommand { get; private set; } = null!;
    public RelayCommand CheckUpdatesNowCommand { get; private set; } = null!;
    public RelayCommand OpenReleasesPageCommand { get; private set; } = null!;
    public RelayCommand OpenReleasePageCommand { get; private set; } = null!;

    public bool CheckForUpdatesOnStart
    {
        get => !UpdatesOff && (_profile.Settings.UpdateCheck ?? true);
        set { if (!UpdatesOff) { UpdateSettings(s => s with { UpdateCheck = value }); } }
    }

    public string UpdateCheckDescription => UpdatesOff
        ? "Personal build: updates are off. New versions are on the port's GitHub Releases page."
        : $"Each time the launcher opens it asks GitHub for the newest release of {UpdateSource.Repository}; nothing is downloaded until you press the update button.";

    public UpdateSource UpdateSource { get; set; } = UpdateSource.GitHub;
    public Func<ReleaseVersion, string> AssetName { get; set; } = PlatformUpdate.AssetName;

    /// <summary>Whether an update takes the macOS bundle path: the real platform by default; tests of the Linux layout set false and the Mac-layout tests true on any host.</summary>
    internal bool MacBundleUpdate { get; set; } = OperatingSystem.IsMacOS();

    /// <summary>The Mac unpack and signature check; null takes the system's (ditto and codesign) on a Mac. Tests give stand-ins.</summary>
    internal MacTools? MacTools { get; set; }

    /// <summary>The exchange of two folders a Mac update probes and then swaps the bundles with; null takes renameatx_np. Tests give a stand-in.</summary>
    internal Func<string, string, bool>? MacExchange { get; set; }

    /// <summary>Reads the free bytes of the drive of a folder for the space check; null reads the folder's own mount. Tests give a stand-in.</summary>
    internal Func<string, long>? FreeSpace { get; set; }
    public ReleaseVersion? CurrentVersionOverride { get; set; }

    /// <summary>Starts the launcher an update installed and ends this one: the new program and the version it is (set by Program; null in a session that cannot restart).</summary>
    public Action<string, ReleaseVersion>? RestartRequested { get; set; }

    public LauncherLog Log => _log;
    public LauncherBuild Build => _build ??= LauncherBuild.Of(typeof(UnixLauncherModel).Assembly, _launcherDirectory);
    public ReleaseVersion CurrentVersion => CurrentVersionOverride ?? Build.Version;
    public bool UpdatesOff => Build.IsPersonal;
    public bool UpdatesOn => !UpdatesOff;

    public string UpdateStatus
    {
        get => UpdatesOff ? "Personal build: updates are off." : _updateStatus.Length > 0 ? _updateStatus : $"You have {CurrentVersion.Tag}. Press Check now to look for a newer release.";
        private set { _updateStatus = value; Raise(nameof(UpdateStatus)); }
    }

    public ReleaseInfo? AvailableUpdate
    {
        get => _update;
        private set
        {
            _update = value;
            Raise(nameof(AvailableUpdate), nameof(UpdateOverlayTitle), nameof(UpdateReleaseTitle), nameof(UpdateNotes), nameof(UpdateVersionLine));
            RaiseUpdateButton();
        }
    }

    public UpdateButtonState UpdateButton => UpdateButtonState.Of(UpdatesOff, _checking, UpdateBusy, _update);
    public string UpdateButtonLabel => UpdateButton.Label;
    public bool UpdateButtonEnabled => UpdateButton.Enabled;
    public bool UpdateButtonAccent => UpdateButton.Accent;
    public string UpdateButtonToolTip => UpdateButton.ToolTip;
    public UpdateCheckOutcome? LastCheckOutcome => _lastCheck;

    public UnixUpdateStage UpdateStage
    {
        get => _updateStage;
        private set
        {
            if (Set(ref _updateStage, value))
            {
                Raise(nameof(UpdateOverlayVisible), nameof(UpdateBusy), nameof(UpdateChoiceVisible), nameof(UpdateCancelVisible), nameof(UpdateWorking), nameof(UpdatePrimaryLabel), nameof(UpdateSecondaryLabel), nameof(UpdateProgressText), nameof(UpdateIndeterminate), nameof(UpdatePercent));
                RaiseUpdateButton();
            }
        }
    }

    public bool UpdateOverlayVisible => _updateStage != UnixUpdateStage.Hidden;
    public bool UpdateBusy => _updateStage is UnixUpdateStage.Working or UnixUpdateStage.Restarting;
    public bool UpdateChoiceVisible => _updateStage is UnixUpdateStage.Offer or UnixUpdateStage.Failed;
    public bool UpdateCancelVisible => _updateStage == UnixUpdateStage.Working && _updateProgress is { Step: UpdateStep.Downloading or UpdateStep.Verifying or UpdateStep.Unpacking };
    public bool UpdateWorking => _updateStage is UnixUpdateStage.Working or UnixUpdateStage.Restarting;
    public string UpdatePrimaryLabel => _updateStage == UnixUpdateStage.Failed ? "Try again" : "Update now";
    public string UpdateSecondaryLabel => _updateStage == UnixUpdateStage.Failed ? "Close" : "Later";
    public string UpdateOverlayTitle => _update is null ? "Update" : $"Update to {_update.Tag}";
    public string UpdateVersionLine => _update is null ? string.Empty : $"You have {CurrentVersion.Tag}  ·  {_update.Tag}{(_update.Prerelease ? " is a pre-release" : string.Empty)}  ·  {(_update.Zip.Size > 0 ? GameFolderCheck.FormatSize(_update.Zip.Size) + " download" : "download size unknown")}";
    public string UpdateReleaseTitle => _update?.Title ?? string.Empty;
    public string UpdateNotes => _update?.Notes ?? string.Empty;
    public string UpdateMessage { get => _updateMessage; private set { if (Set(ref _updateMessage, value)) { Raise(nameof(UpdateMessageVisible)); } } }
    public bool UpdateMessageVisible => _updateMessage.Length > 0;
    public double UpdatePercent => _updateProgress is { Step: UpdateStep.Downloading } p && p.Download.Fraction >= 0 ? p.Download.Fraction * 100 : 0;
    public bool UpdateIndeterminate => _updateStage == UnixUpdateStage.Restarting || _updateProgress is not { Step: UpdateStep.Downloading } || _updateProgress is { Download.Fraction: < 0 };
    public string UpdateProgressText => _updateStage == UnixUpdateStage.Restarting ? $"Restarting into {_update?.Tag}..." : _updateProgress is { } p ? Updater.Describe(p) : string.Empty;

    private UpdateProgress? UpdateProgress
    {
        get => _updateProgress;
        set { if (Set(ref _updateProgress, value)) { Raise(nameof(UpdatePercent), nameof(UpdateIndeterminate), nameof(UpdateProgressText), nameof(UpdateCancelVisible)); } }
    }

    private void CreateUpdateCommands()
    {
        UpdateButtonCommand = new RelayCommand(async _ => await PressUpdateButtonAsync(), _ => UpdateButton.Enabled);
        ShowUpdateCommand = new RelayCommand(_ => ShowUpdate());
        UpdateNowCommand = new RelayCommand(async _ => await RunUpdateAsync(), _ => _updateStage is UnixUpdateStage.Offer or UnixUpdateStage.Failed);
        UpdateLaterCommand = new RelayCommand(_ => CloseUpdate());
        CancelUpdateCommand = new RelayCommand(_ => CancelUpdate(), _ => _updateStage == UnixUpdateStage.Working);
        CheckUpdatesNowCommand = new RelayCommand(async _ => await CheckForUpdatesAsync(manual: true), _ => !UpdatesOff && !_checking && !UpdateBusy);
        OpenReleasesPageCommand = new RelayCommand(_ => OpenUrl(UpdateSource.ReleasesPage));
        OpenReleasePageCommand = new RelayCommand(_ => OpenUrl(_update?.PageUrl ?? UpdateSource.ReleasesPage));
    }

    public void CheckOnStart()
    {
        if (UpdatesOff)
        {
            _log.Write($"update check: off, personal build ({Build.InformationalVersion})");
            return;
        }

        if (!CheckForUpdatesOnStart)
        {
            _log.Write("update check: off (Help & about > Check for updates when the launcher starts)");
            return;
        }

        _ = CheckForUpdatesAsync(manual: false);
    }

    public async Task PressUpdateButtonAsync()
    {
        if (UpdatesOff)
        {
            _log.Write("update button: personal build, no check");
            ShowToast(UpdateButtonState.PersonalToast);
            return;
        }

        if (!UpdateButton.Enabled) { return; }
        if (_update is not null)
        {
            ShowUpdate();
            await RunUpdateAsync();
            return;
        }

        await CheckForUpdatesAsync(manual: true);
    }

    public void ShowUpdate()
    {
        if (_update is null || UpdatesOff || UpdateBusy) { return; }
        UpdateMessage = string.Empty;
        UpdateProgress = null;
        UpdateStage = UnixUpdateStage.Offer;
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (UpdatesOff || _checking || UpdateBusy) { return; }
        SetChecking(true);
        UpdateStatus = LauncherTexts.Checking;
        ReleaseVersion current = CurrentVersion;
        _log.Write($"update check ({(manual ? "Check now" : "start")}): {UpdateSource.ReleasesUri} for {current.Tag} (running {Build.InformationalVersion})");
        UpdateCheckResult result;
        try
        {
            using var client = new UpdateClient(UpdateSource, Build.UserAgent(UpdateSource));
            result = await client.CheckAsync(current, CancellationToken.None, AssetName);
        }
        finally
        {
            SetChecking(false);
        }

        foreach (FeedEntry entry in result.Entries)
        {
            _log.Write($"  {(entry.Tag.Length == 0 ? "(no tag)" : entry.Tag)}: {entry.Skipped ?? (entry.Release!.Version > current ? "newer" : "not newer")}");
        }

        _log.Write(result switch
        {
            { Error: { } error } => "update check failed: " + error,
            { Update: { } u } => $"update check: {u.Tag} is available ({u.Zip.Name}, {u.Zip.Size.ToString(CultureInfo.InvariantCulture)} bytes)",
            _ => $"update check: {current.Tag} is the newest",
        });
        _lastCheck = UpdateButtonState.OutcomeOf(result);
        Raise(nameof(LastCheckOutcome));
        if (result.Error is { } failed)
        {
            // a release found earlier stays offered
            UpdateStatus = LauncherTexts.CheckFailed(failed);
        }
        else
        {
            AvailableUpdate = result.Update;
            string at = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            UpdateStatus = _update is null ? LauncherTexts.Newest(current, at) : LauncherTexts.UpdateAvailable(_update.Tag, at);
        }

        if (!manual) { return; }
        if (UpdateButtonState.ToastAfterManualCheck(result, current) is { } text) { ShowToast(text); }
        else { ShowUpdate(); }
    }

    private void SetChecking(bool checking)
    {
        _checking = checking;
        RaiseUpdateButton();
    }

    /// <summary>"Update now": the checks, then download, verify and unpack (PlatformUpdate.PrepareAsync), the game checked again, then the files replaced and the restart.</summary>
    public async Task RunUpdateAsync()
    {
        if (_update is not { } release || UpdatesOff || UpdateBusy || _updateRunning) { return; }
        _updateRunning = true;
        try
        {
            await InstallUpdateAsync(release);
        }
        finally
        {
            _updateRunning = false;
        }
    }

    private async Task InstallUpdateAsync(ReleaseInfo release)
    {
        bool mac = MacBundleUpdate;
        UpdateStaging staging = PlatformUpdate.StagingDirectory(_launcherDirectory, mac);
        string app = staging.AppDirectory;
        // where the update writes: the launcher's folder on Linux; on a Mac the folder that holds Tsukihime.app (its hidden staging folder and the swap of the bundles go there)
        string target = UnixUpdateChecks.UpdateFolder(_launcherDirectory, mac);
        UpdateMessage = string.Empty;
        if (GameRunning)
        {
            Refuse(LauncherTexts.UpdateGameRunning(PlatformServices.AppName), "the game of this folder is running", UnixUpdateStage.Offer);
            return;
        }

        if (RestartRequested is not { } restart)
        {
            Refuse(CannotRestart, "this session cannot restart the launcher", UnixUpdateStage.Failed);
            return;
        }

        if (UnixUpdateChecks.NotWritable(_launcherDirectory, target, mac, AssetName(release.Version)) is { } notWritable)
        {
            Refuse(notWritable.Message, $"{notWritable.Folder} cannot be written ({notWritable.Reason})", UnixUpdateStage.Failed);
            return;
        }

        if (UnixUpdateChecks.SpaceProblem(target, release.Zip.Size, FreeSpace) is { } space)
        {
            Refuse(space, space, UnixUpdateStage.Failed);
            return;
        }

        if (!mac && UpdateInstaller.Cleanup(app, PlatformUpdate.LauncherProgram) > 0)
        {
            // the .old files of the last update are still in use: its journal must stay until they are gone, so no new update starts over it
            Refuse(LauncherTexts.UpdateLastStillInUse, "the .old files of the last update are still in use", UnixUpdateStage.Failed);
            return;
        }

        using var cancel = new CancellationTokenSource();
        _updateCancel = cancel;
        UpdateProgress = new UpdateProgress(UpdateStep.Downloading, new DownloadProgress(0, release.Zip.Size));
        UpdateStage = UnixUpdateStage.Working;
        _log.Write($"update: {release.Tag} into {app}: {release.Zip.Url}");
        UnixStagedUpdate staged;
        try
        {
            using var client = new UpdateClient(UpdateSource, Build.UserAgent(UpdateSource));
            staged = await PlatformUpdate.PrepareAsync(client, release, staging, cancel.Token, PlatformUpdate.Progress(this), macOs: mac, macTools: MacTools, exchange: MacExchange);
            _log.Write($"update: {release.Zip.Name} matches {UpdateSource.SumsAssetName}; unpacked and checked into {staging.FilesDirectory}");
        }
        catch (OperationCanceledException)
        {
            Cancelled();
            return;
        }
        catch (UnixUpdateRefusedException e)
        {
            Refused(release.Zip.Name, e);
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UpdateRefusedException or HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Fail(LauncherTexts.UpdateFailed(UpdateClient.Describe(e)), "update failed: " + UpdateClient.Describe(e));
            return;
        }
        finally
        {
            _updateCancel = null;
        }

        if (cancel.IsCancellationRequested)
        {
            // Cancel pressed as the unpacking ended: the update still goes nowhere
            PlatformUpdate.Discard(staged);
            Cancelled();
            return;
        }

        if (GameRunning)
        {
            PlatformUpdate.Discard(staged);
            Refuse(LauncherTexts.UpdateGameStartedMeanwhile, "the game was started while the update downloaded", UnixUpdateStage.Offer);
            return;
        }

        UpdateProgress = new UpdateProgress(UpdateStep.Installing, default);
        ReleaseVersion from = CurrentVersion;
        SynchronizationContext? ui = SynchronizationContext.Current;
        try
        {
            // the files are replaced off the window's thread; the restart (its last step: PlatformUpdate puts the old version back when it throws) runs on it
            await Task.Run(() => PlatformUpdate.ApplyAndRestart(staged, (exe, version) => OnUi(ui, () =>
            {
                _log.Write($"update: the files of {version.Tag} are in place; restarting into {exe} (updated from {from.Tag})");
                UpdateStage = UnixUpdateStage.Restarting;
                restart(exe, version);
            }), MacExchange));
        }
        catch (UnixUpdateRefusedException e)
        {
            Refused(release.Zip.Name, e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UpdateRefusedException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // PlatformUpdate's sentence says whether the old version was put back
            Fail($"The update did not work: {UpdateClient.Describe(e)}", "update: replacing the files failed: " + UpdateClient.Describe(e));
        }
    }

    private static void OnUi(SynchronizationContext? ui, Action action)
    {
        if (ui is null) { action(); }
        else
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
            ui.Send(_ =>
            {
                try { action(); }
                catch (Exception e) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
            }, null);
            error?.Throw();
        }
    }

    private void Cancelled()
    {
        _log.Write("update: cancelled");
        UpdateMessage = LauncherTexts.UpdateCancelled;
        UpdateStage = UnixUpdateStage.Offer;
    }

    /// <summary>A refusal the overlay says in a player's words: launcher.log gets every reason (release-engineering detail included) on a line of its own.</summary>
    private void Refused(string asset, UnixUpdateRefusedException e)
    {
        _log.Write($"update: refused {asset}: {e.Problems.Count.ToString(CultureInfo.InvariantCulture)} problem(s)");
        foreach (string problem in e.Problems)
        {
            _log.Write("  " + problem);
        }

        UpdateMessage = e.Message;
        UpdateStage = UnixUpdateStage.Failed;
        ShowToast(e.Message);
    }

    private void Refuse(string message, string why, UnixUpdateStage stage)
    {
        UpdateMessage = message;
        _log.Write("update: refused, " + why);
        UpdateStage = stage;
    }

    private void Fail(string message, string log)
    {
        _log.Write(log);
        UpdateMessage = message;
        UpdateStage = UnixUpdateStage.Failed;
        ShowToast(message);
    }

    public void CloseUpdate()
    {
        if (UpdateBusy) { return; }
        UpdateMessage = string.Empty;
        UpdateStage = UnixUpdateStage.Hidden;
    }

    /// <summary>Cancels a running download (the Cancel button, or the window is closing); nothing of the launcher's folder was changed yet.</summary>
    public void CancelUpdate() => _updateCancel?.Cancel();

    /// <summary>After the restart: says that the update worked (the toast and the Help page of the new launcher).</summary>
    public void SayUpdated(ReleaseVersion from)
    {
        _log.Write(LauncherTexts.UpdatedLog(from, Build.Version, Build.InformationalVersion));
        UpdateStatus = LauncherTexts.UpdatedStatus(from, Build.Version);
        ShowToast(LauncherTexts.UpdatedToast(from, Build.Version));
    }

    internal void Report(UpdateProgress progress) => UpdateProgress = progress;

    /// <summary>Shows <paramref name="release"/> as found, for the render check (nothing is asked of GitHub).</summary>
    internal void PreviewUpdate(ReleaseInfo? release)
    {
        AvailableUpdate = release;
        UpdateStatus = release is null ? string.Empty : LauncherTexts.UpdateAvailable(release.Tag, "14:02");
    }

    /// <summary>Puts the overlay into a state, for the render check.</summary>
    internal void PreviewUpdateState(UnixUpdateStage stage, UpdateProgress? progress, string message)
    {
        UpdateProgress = progress;
        UpdateMessage = message;
        UpdateStage = stage;
    }

    private void RaiseUpdateButton()
    {
        Raise(nameof(UpdateButtonLabel), nameof(UpdateButtonEnabled), nameof(UpdateButtonAccent), nameof(UpdateButtonToolTip), nameof(UpdateStatus));
        PlayCommand.Raise();
        UpdateButtonCommand.Raise();
        UpdateNowCommand.Raise();
        CancelUpdateCommand.Raise();
        CheckUpdatesNowCommand.Raise();
    }
}

/// <summary>Whether the folders an update changes can be written, in the words the overlay shows (macOS and Linux; Windows has UpdateInstaller.NotWritableMessage).</summary>
public static class UnixUpdateChecks
{
    /// <summary>A folder that cannot take the update: the sentence for the overlay, the folder, and the system's reason for the log.</summary>
    public sealed record Problem(string Message, string Folder, string Reason);

    /// <summary>
    /// Null when the update can write where it must: the launcher's folder on Linux; on a Mac the folder that holds Tsukihime.app (the staging folder and the swap of the
    /// bundles go there) and the bundle itself. A copy macOS runs from a read-only App Translocation folder (opened straight from Downloads) gets its own sentence.
    /// </summary>
    public static Problem? NotWritable(string launcherDirectory, string appDirectory, bool mac, string assetName)
    {
        foreach (string folder in mac ? new[] { appDirectory, launcherDirectory } : [appDirectory])
        {
            if (UpdateInstaller.ProbeWritable(folder) is not { } reason) { continue; }
            string shown = Path.TrimEndingDirectorySeparator(folder);
            return new Problem(Message(shown, mac, IsTranslocated(launcherDirectory), assetName, mac ? Path.GetFileName(PlatformUpdate.MacBundle(launcherDirectory)) : MacAppName), shown, reason);
        }

        return null;
    }

    /// <summary>
    /// The folder an update writes into: <paramref name="launcherDirectory"/> on Linux; on a Mac the folder that holds Tsukihime.app, which the staging folder
    /// (".Tsukihime.app.update", <see cref="PlatformUpdate.MacStaging"/>) and the swap of the bundles use.
    /// </summary>
    public static string UpdateFolder(string launcherDirectory, bool mac) =>
        mac ? Path.GetDirectoryName(PlatformUpdate.MacBundle(launcherDirectory))! : Path.TrimEndingDirectorySeparator(launcherDirectory);

    /// <summary>
    /// Null when the file system of <paramref name="folder"/> has room for an update of <paramref name="zipSize"/> bytes (or the free space cannot be read), else the
    /// overlay's sentence. Unlike Windows' <see cref="Updater.SpaceProblem"/> it measures the folder's own mount, not "/" (a Steam Deck's /home is a file system of its own).
    /// <paramref name="freeSpace"/> reads the free bytes of the full path of <paramref name="folder"/> (tests); by default its drive's.
    /// </summary>
    public static string? SpaceProblem(string folder, long zipSize, Func<string, long>? freeSpace = null)
    {
        try
        {
            if (zipSize <= 0) { return null; }
            long free = (freeSpace ?? (path => new DriveInfo(path).AvailableFreeSpace))(Path.GetFullPath(folder));
            long need = (zipSize * Updater.SpaceFactor) + (64L * 1024 * 1024);
            return free >= need ? null
                : $"There is not enough room for the update in {folder}: it needs about {GameFolderCheck.FormatSize(need)}, {GameFolderCheck.FormatSize(free)} are free.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The app bundle's usual name.</summary>
    private const string MacAppName = "Tsukihime.app";

    /// <summary>
    /// The overlay's sentence for <paramref name="folder"/>, which cannot be written. <paramref name="bundleName"/> is the running bundle's name on a Mac ("Tsukihime
    /// 2.app" too). Only an administrator account can change /Applications: a standard account there is sent to ~/Applications, not "into Applications".
    /// </summary>
    public static string Message(string folder, bool mac, bool translocated, string assetName, string bundleName = MacAppName) =>
        mac && translocated
            ? $"macOS runs this copy of {bundleName} from a read-only place, because it was opened straight from where it was downloaded. Quit the launcher, move {bundleName} into Applications with Finder, open it from there, then update. Your saves and settings stay where they are."
        : mac && IsInApplications(folder)
            ? $"Only an administrator account can change {folder}. Update from an administrator account, or move {bundleName} into the Applications folder of your home folder (~/Applications) with Finder and update from there, or download {assetName} from the Releases page. Your saves and settings stay where they are."
        : mac
            ? $"The folder of {bundleName} cannot be changed by your account: {folder}. Move {bundleName} into Applications (or another folder of your own) with Finder and update from there, or download {assetName} from the Releases page. Your saves and settings stay where they are."
            : $"The launcher's folder cannot be changed by your account: {folder}. Download {assetName} from the Releases page and unpack it into a folder of your own (for example in your home folder), or move this folder there and update from the copy. Your saves and settings stay where they are.";

    /// <summary>True for the system's /Applications and the folders in it (not ~/Applications), but not for a folder inside an app bundle there.</summary>
    private static bool IsInApplications(string folder)
    {
        string path = Path.TrimEndingDirectorySeparator(folder.Replace('\\', '/'));
        return (path == "/Applications" || path.StartsWith("/Applications/", StringComparison.Ordinal))
            && !path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !path.Contains(".app/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a path under macOS's App Translocation (a quarantined app opened from Downloads runs from a read-only copy there).</summary>
    public static bool IsTranslocated(string path) => path.Replace('\\', '/').Contains("/AppTranslocation/", StringComparison.Ordinal);
}
