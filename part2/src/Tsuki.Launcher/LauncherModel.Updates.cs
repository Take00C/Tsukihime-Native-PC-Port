// The update button of the launcher (package K40, GitHub issue #6 "Update button, pretty please?"): a check of the port's GitHub releases when the launcher starts
// (a setting on the Help & about page, plus "Check now"), an "Update to vX.Y.Z" button left of Log when a newer release exists, an overlay with the release's title
// and notes, and "Update now": the release zip and SHA256SUMS.txt are downloaded over HTTPS, the zip's checksum is checked, it is unpacked into a staging folder,
// checked, the launcher's files are replaced (UpdateInstaller) and the new launcher is started. A failed check at the start is a line in launcher.log and a sentence
// on the Help page, nothing more. A personal build (package K28) never checks and never updates.
// Package K42 (issue #6 again, "Something around this corner"): the footer button is always there. Without a known release it is "Check for updates" (a manual
// check, as Check now), "Checking…" while a check runs, and K40's gold "Update to vX.Y.Z" once a newer release is known (Core/UpdateButtonState). A manual check
// says its outcome in the footer's toast, or opens the overlay at once when it found a newer release; the start's check stays silent.
// One click (the user, 2026-11-01: "just like the Bloodborne launcher where you can just instantly update"): the gold button starts the update itself; the overlay
// opens on the download with the release's notes, the progress and Cancel. "Update now" remains for the overlay a manual check opens and after a refusal or failure.
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows.Input;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher;

/// <summary>Where the update overlay is.</summary>
public enum UpdateStage
{
    /// <summary>Not shown.</summary>
    Hidden,

    /// <summary>The release's notes, "Update now" / "Later".</summary>
    Offer,

    /// <summary>Downloading, checking, unpacking, replacing.</summary>
    Working,

    /// <summary>It did not work; the message says why and that nothing was lost.</summary>
    Failed,

    /// <summary>The new launcher is being started.</summary>
    Restarting,
}

/// <summary>The update part of the launcher's view model.</summary>
public sealed partial class LauncherModel
{
    private readonly LauncherLog _log;
    private LauncherBuild? _build;
    private ReleaseInfo? _update;
    private UpdateStage _updateStage;
    private string _updateStatus = string.Empty;
    private string _updateMessage = string.Empty;
    private UpdateProgress? _updateProgress;
    private CancellationTokenSource? _updateCancel;
    private bool _checking;
    private bool _updateRunning;
    private UpdateCheckOutcome? _lastCheck;
    private RelayCommand? _updateButton;
    private RelayCommand? _showUpdate;
    private RelayCommand? _updateNow;
    private RelayCommand? _updateLater;
    private RelayCommand? _cancelUpdate;
    private RelayCommand? _checkNow;
    private RelayCommand? _openReleases;
    private RelayCommand? _openReleasePage;

    /// <summary>Where the check looks: GitHub, or the loopback stand-in of the verification mode (<c>--update-api</c>).</summary>
    public UpdateSource UpdateSource { get; set; } = UpdateSource.GitHub;

    /// <summary>The version the check compares with when it is not the running one (<c>--update-current</c>, verification mode with a loopback source only).</summary>
    public ReleaseVersion? CurrentVersionOverride { get; set; }

    /// <summary>Starts the new launcher after an update and ends this one (set by the application; the argument is the new TsukihimeLauncher.exe and the version updated from).</summary>
    public Action<string, ReleaseVersion>? RestartRequested { get; set; }

    /// <summary>The launcher's log (launcher.log in the profile folder).</summary>
    public LauncherLog Log => _log;

    /// <summary>The running launcher: version, personal or not.</summary>
    public LauncherBuild Build => _build ??= LauncherBuild.Of(typeof(LauncherModel).Assembly, _launcherDirectory);

    /// <summary>The version the check compares with.</summary>
    public ReleaseVersion CurrentVersion => CurrentVersionOverride ?? Build.Version;

    /// <summary>True for a personal build: no check, no update (package K28).</summary>
    public bool UpdatesOff => Build.IsPersonal;

    /// <summary>The opposite of <see cref="UpdatesOff"/>, for the Help page's switch.</summary>
    public bool UpdatesOn => !UpdatesOff;

    /// <summary>"Check for updates when the launcher starts" (settings.ini <c>update_check</c>, on when the key is absent; always off for a personal build).</summary>
    public bool CheckForUpdatesOnStart
    {
        get => !UpdatesOff && (_profile.Settings.UpdateCheck ?? true);
        set
        {
            if (!UpdatesOff)
            {
                UpdateSettings(s => s with { UpdateCheck = value });
            }
        }
    }

    /// <summary>The sentence under the switch: what the check is and where it looks.</summary>
    public string UpdateCheckDescription => UpdatesOff
        ? "Personal build: updates are off. It carries her face from the build itself; new versions are on the port's GitHub Releases page."
        : $"Each time the launcher opens it asks GitHub for the newest release of {UpdateSource.Repository}; nothing is downloaded until you press the update button.";

    /// <summary>The Help page's status line: newest, available, offline, personal.</summary>
    public string UpdateStatus
    {
        get => UpdatesOff ? "Personal build: updates are off." : _updateStatus.Length > 0 ? _updateStatus : $"You have {CurrentVersion.Tag}. Press Check now to look for a newer release.";
        private set => Set(ref _updateStatus, value);
    }

    /// <summary>The release the footer button installs, or null.</summary>
    public ReleaseInfo? AvailableUpdate
    {
        get => _update;
        private set
        {
            _update = value;
            Raise(nameof(AvailableUpdate));
            Raise(nameof(UpdateOverlayTitle));
            Raise(nameof(UpdateReleaseTitle));
            Raise(nameof(UpdateNotes));
            Raise(nameof(UpdateVersionLine));
            RaiseUpdateButton();
        }
    }

    /// <summary>The footer button as it is now (package K42: always shown; its text, whether it can be pressed, its look, its tooltip).</summary>
    public UpdateButtonState UpdateButton => UpdateButtonState.Of(UpdatesOff, _checking, UpdateBusy, _update);

    /// <summary>"Check for updates", "Checking…" or "Update to v0.1.4" (also the button's name for screen readers).</summary>
    public string UpdateButtonLabel => UpdateButton.Label;

    /// <summary>The footer button can be pressed (not while a check runs or an update downloads).</summary>
    public bool UpdateButtonEnabled => UpdateButton.Enabled;

    /// <summary>The footer button has K40's gold look: a newer release is known.</summary>
    public bool UpdateButtonAccent => UpdateButton.Accent;

    /// <summary>The footer button's tooltip.</summary>
    public string UpdateButtonToolTip => UpdateButton.ToolTip;

    /// <summary>What the last check came to (null before the first one).</summary>
    public UpdateCheckOutcome? LastCheckOutcome => _lastCheck;

    /// <summary>Where the overlay is.</summary>
    public UpdateStage UpdateStage
    {
        get => _updateStage;
        private set
        {
            if (Set(ref _updateStage, value))
            {
                Raise(nameof(UpdateOverlayVisible));
                Raise(nameof(OverlayVisible));
                Raise(nameof(UpdateBusy));
                Raise(nameof(UpdateChoiceVisible));
                Raise(nameof(UpdateCancelVisible));
                Raise(nameof(UpdateWorking));
                Raise(nameof(UpdatePrimaryLabel));
                Raise(nameof(UpdateSecondaryLabel));
                _updateNow?.Raise();
                _cancelUpdate?.Raise();
                _checkNow?.Raise();
                (PlayCommand as RelayCommand)?.Raise();
                RaiseUpdateButton();
            }
        }
    }

    /// <summary>The overlay is up.</summary>
    public bool UpdateOverlayVisible => _updateStage != UpdateStage.Hidden;

    /// <summary>An update is downloading or installing (PLAY waits).</summary>
    public bool UpdateBusy => _updateStage is UpdateStage.Working or UpdateStage.Restarting;

    /// <summary>"Update now" and "Later" are shown.</summary>
    public bool UpdateChoiceVisible => _updateStage is UpdateStage.Offer or UpdateStage.Failed;

    /// <summary>"Cancel" is shown (while downloading).</summary>
    public bool UpdateCancelVisible => _updateStage == UpdateStage.Working && _updateProgress is { Step: UpdateStep.Downloading or UpdateStep.Verifying or UpdateStep.Unpacking };

    /// <summary>The progress bar is shown.</summary>
    public bool UpdateWorking => _updateStage is UpdateStage.Working or UpdateStage.Restarting;

    /// <summary>"Update now", or "Try again" after a failure.</summary>
    public string UpdatePrimaryLabel => _updateStage == UpdateStage.Failed ? "Try again" : "Update now";

    /// <summary>"Later", or "Close" after a failure.</summary>
    public string UpdateSecondaryLabel => _updateStage == UpdateStage.Failed ? "Close" : "Later";

    /// <summary>"Update to v0.1.4".</summary>
    public string UpdateOverlayTitle => _update is null ? "Update" : $"Update to {_update.Tag}";

    /// <summary>"You have v0.1.3  ·  pre-release  ·  105.3 MB download".</summary>
    public string UpdateVersionLine => _update is null
        ? string.Empty
        : $"You have {CurrentVersion.Tag}  ·  {_update.Tag}{(_update.Prerelease ? " is a pre-release" : string.Empty)}  ·  {(_update.Zip.Size > 0 ? GameFolderCheck.FormatSize(_update.Zip.Size) + " download" : "download size unknown")}";

    /// <summary>The release's title.</summary>
    public string UpdateReleaseTitle => _update?.Title ?? string.Empty;

    /// <summary>The release notes, plain text.</summary>
    public string UpdateNotes => _update?.Notes ?? string.Empty;

    /// <summary>Why the update cannot go on (the game is running, the folder cannot be written, a failure), or empty.</summary>
    public string UpdateMessage
    {
        get => _updateMessage;
        private set
        {
            if (Set(ref _updateMessage, value))
            {
                Raise(nameof(UpdateMessageVisible));
            }
        }
    }

    /// <summary>The message box of the overlay is shown.</summary>
    public bool UpdateMessageVisible => _updateMessage.Length > 0;

    /// <summary>0..100 for the progress bar.</summary>
    public double UpdatePercent => _updateProgress is { Step: UpdateStep.Downloading } p && p.Download.Fraction >= 0 ? p.Download.Fraction * 100 : 0;

    /// <summary>True when the bar cannot show a share (checking, unpacking, replacing, or a download of unknown size).</summary>
    public bool UpdateIndeterminate => _updateStage == UpdateStage.Restarting || _updateProgress is not { Step: UpdateStep.Downloading } || _updateProgress is { Download.Fraction: < 0 };

    /// <summary>"Downloading: 46.0 MB of 105.3 MB (43 %)".</summary>
    public string UpdateProgressText => _updateStage == UpdateStage.Restarting
        ? $"Restarting into {_update?.Tag}..."
        : _updateProgress is { } p ? Updater.Describe(p) : string.Empty;

    // -- commands -----------------------------------------------------------------------------------------------------

    /// <summary>The footer button (package K42): a manual check, the overlay of the newer release, or in a personal build a toast (<see cref="PressUpdateButtonAsync"/>).</summary>
    public ICommand UpdateButtonCommand => _updateButton ??= new RelayCommand(async _ => await PressUpdateButtonAsync(), _ => UpdateButton.Enabled);

    /// <summary>Opens the overlay of the newer release (the footer button once one is known).</summary>
    public ICommand ShowUpdateCommand => _showUpdate ??= new RelayCommand(_ => ShowUpdate());

    /// <summary>"Update now" / "Try again".</summary>
    public ICommand UpdateNowCommand => _updateNow ??= new RelayCommand(async _ => await RunUpdateAsync(), _ => _updateStage is UpdateStage.Offer or UpdateStage.Failed);

    /// <summary>"Later" / "Close".</summary>
    public ICommand UpdateLaterCommand => _updateLater ??= new RelayCommand(_ => CloseUpdate());

    /// <summary>"Cancel" while downloading.</summary>
    public ICommand CancelUpdateCommand => _cancelUpdate ??= new RelayCommand(_ => _updateCancel?.Cancel(), _ => _updateStage == UpdateStage.Working);

    /// <summary>"Check now" on the Help page.</summary>
    public ICommand CheckUpdatesNowCommand => _checkNow ??= new RelayCommand(async _ => await CheckForUpdatesAsync(manual: true), _ => !UpdatesOff && !_checking && !UpdateBusy);

    /// <summary>Opens the repository's Releases page in the browser.</summary>
    public ICommand OpenReleasesPageCommand => _openReleases ??= new RelayCommand(_ => OpenUrl(UpdateSource.ReleasesPage));

    /// <summary>Opens the offered release's page in the browser.</summary>
    public ICommand OpenReleasePageCommand => _openReleasePage ??= new RelayCommand(_ => OpenUrl(_update?.PageUrl ?? UpdateSource.ReleasesPage));

    // -- the check ----------------------------------------------------------------------------------------------------

    /// <summary>The check of the launcher's start: only when the setting is on and this is not a personal build; never waits for the answer.</summary>
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

    /// <summary>
    /// The footer button (package K42). A personal build only says in a toast that it does not update itself (no check); with a newer release known the update starts
    /// at once, in the overlay (one click, <see cref="RunUpdateAsync"/>); otherwise GitHub is asked once, as Help &amp; about &gt; Check now does, and the outcome is
    /// said (<see cref="CheckForUpdatesAsync"/> with <c>manual</c>).
    /// </summary>
    public async Task PressUpdateButtonAsync()
    {
        if (UpdatesOff)
        {
            _log.Write("update button: personal build, no check");
            ShowToast(UpdateButtonState.PersonalToast);
            return;
        }

        if (!UpdateButton.Enabled)
        {
            return;
        }

        if (_update is not null)
        {
            ShowUpdate();
            await RunUpdateAsync();
            return;
        }

        await CheckForUpdatesAsync(manual: true);
    }

    /// <summary>
    /// Reads the release list once; offline or any error is a line in the log and the Help page's status, never a message box. A manual check (the footer button,
    /// Check now) also says its outcome in the toast, or opens the overlay when it found a newer release (package K42); the start's check stays silent.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (UpdatesOff || _checking)
        {
            return;
        }

        SetChecking(true);
        UpdateStatus = LauncherTexts.Checking;
        ReleaseVersion current = CurrentVersion;
        _log.Write($"update check ({(manual ? "Check now" : "start")}): {UpdateSource.ReleasesUri} for {current.Tag} (running {Build.InformationalVersion})");
        UpdateCheckResult result;
        try
        {
            using var client = new UpdateClient(UpdateSource, Build.UserAgent(UpdateSource));
            result = await client.CheckAsync(current, CancellationToken.None);
        }
        finally
        {
            SetChecking(false);
        }

        foreach (FeedEntry e in result.Entries)
        {
            _log.Write($"  {(e.Tag.Length == 0 ? "(no tag)" : e.Tag)}: {e.Skipped ?? (e.Release!.Version > current ? "newer" : "not newer")}");
        }

        _log.Write(result switch
        {
            { Error: { } error } => "update check failed: " + error,
            { Update: { } u } => $"update check: {u.Tag} is available ({u.Zip.Name}, {u.Zip.Size.ToString(CultureInfo.InvariantCulture)} bytes)",
            _ => $"update check: {current.Tag} is the newest",
        });
        TakeCheckResult(result, current, manual);
    }

    /// <summary>Shows what a check found: the Help page's status line, the footer button and, after a manual check, the toast or the overlay.</summary>
    private void TakeCheckResult(UpdateCheckResult result, ReleaseVersion current, bool manual)
    {
        _lastCheck = UpdateButtonState.OutcomeOf(result);
        Raise(nameof(LastCheckOutcome));
        string at = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (result.Error is { } error)
        {
            // a release found earlier stays offered
            UpdateStatus = LauncherTexts.CheckFailed(error);
        }
        else
        {
            AvailableUpdate = result.Update;
            UpdateStatus = result.Update is { } u ? LauncherTexts.UpdateAvailable(u.Tag, at) : LauncherTexts.Newest(current, at);
        }

        if (!manual)
        {
            return;
        }

        if (UpdateButtonState.ToastAfterManualCheck(result, current) is { } toast)
        {
            ShowToast(toast);
        }
        else if (!ConfirmVisible)
        {
            ShowUpdate();
        }
    }

    private void SetChecking(bool checking)
    {
        _checking = checking;
        _checkNow?.Raise();
        RaiseUpdateButton();
    }

    /// <summary>Tells the footer button that its state may have changed.</summary>
    private void RaiseUpdateButton()
    {
        Raise(nameof(UpdateButton));
        Raise(nameof(UpdateButtonLabel));
        Raise(nameof(UpdateButtonEnabled));
        Raise(nameof(UpdateButtonAccent));
        Raise(nameof(UpdateButtonToolTip));
        _updateButton?.Raise();
    }

    // -- the overlay --------------------------------------------------------------------------------------------------

    private void ShowUpdate()
    {
        if (_update is null || UpdatesOff)
        {
            return;
        }

        UpdateMessage = string.Empty;
        _updateProgress = null;
        UpdateStage = UpdateStage.Offer;
    }

    private void CloseUpdate()
    {
        if (UpdateBusy)
        {
            return;
        }

        UpdateMessage = string.Empty;
        UpdateStage = UpdateStage.Hidden;
    }

    /// <summary>Cancels a running download (the window is closing).</summary>
    public void CancelUpdate() => _updateCancel?.Cancel();

    private void OnUpdateProgress(UpdateProgress p)
    {
        _updateProgress = p;
        Raise(nameof(UpdatePercent));
        Raise(nameof(UpdateIndeterminate));
        Raise(nameof(UpdateProgressText));
        Raise(nameof(UpdateCancelVisible));
    }

    /// <summary>
    /// "Update now": the game of this folder must not be running, the folder must be writable and have room; then download, check, unpack (Updater.PrepareAsync),
    /// replace the files (UpdateInstaller.Apply) and restart. Every outcome is logged; a failure leaves the old version whole and says so. A stamp of the program
    /// icons that runs when the button is pressed ends first, and none starts until the update is over (package K43, review: both rename the programs and probe
    /// the folder, and a late stamp could put its copy of the old version over a program the update just installed).
    /// </summary>
    public async Task RunUpdateAsync()
    {
        if (_update is not { } release || UpdatesOff || UpdateBusy || _updateRunning)
        {
            return;
        }

        _updateRunning = true;
        try
        {
            await WaitForStampAsync();
            await InstallUpdateAsync(release);
        }
        finally
        {
            _updateRunning = false;
        }
    }

    /// <summary>The body of <see cref="RunUpdateAsync"/>, with no stamp running.</summary>
    private async Task InstallUpdateAsync(ReleaseInfo release)
    {
        string app = _launcherDirectory;
        var staging = new UpdateStaging(app);
        UpdateMessage = string.Empty;
        if (GameRunning || GameLauncher.IsGameRunningFrom(app))
        {
            UpdateMessage = LauncherTexts.UpdateGameRunning(GameLauncher.AppFileName);
            _log.Write("update: refused, the game of this folder is running");
            UpdateStage = UpdateStage.Offer;
            return;
        }

        if (UpdateInstaller.ProbeWritable(app) is { } notWritable)
        {
            UpdateMessage = UpdateInstaller.NotWritableMessage(app, release.Version);
            _log.Write($"update: refused, {app} cannot be written ({notWritable})");
            UpdateStage = UpdateStage.Failed;
            return;
        }

        if (Updater.SpaceProblem(app, release.Zip.Size) is { } space)
        {
            UpdateMessage = space;
            _log.Write("update: refused, " + space);
            UpdateStage = UpdateStage.Failed;
            return;
        }

        if (UpdateInstaller.Cleanup(app) > 0)
        {
            // the .old files of the last update are still in use: its journal must stay until they are gone, so no new update starts over it
            UpdateMessage = LauncherTexts.UpdateLastStillInUse;
            _log.Write("update: refused, the .old files of the last update are still in use");
            UpdateStage = UpdateStage.Failed;
            return;
        }

        using var cancel = new CancellationTokenSource();
        _updateCancel = cancel;
        _updateProgress = new UpdateProgress(UpdateStep.Downloading, new DownloadProgress(0, release.Zip.Size));
        UpdateStage = UpdateStage.Working;
        OnUpdateProgress(_updateProgress.Value);
        _log.Write($"update: {release.Tag} into {app}: {release.Zip.Url}");
        StagedRelease staged;
        try
        {
            using var client = new UpdateClient(UpdateSource, Build.UserAgent(UpdateSource));
            staged = await Updater.PrepareAsync(client, release, staging, new Progress<UpdateProgress>(OnUpdateProgress), cancel.Token);
            _log.Write($"update: {release.Zip.Name} matches {UpdateSource.SumsAssetName}; {staged.Files.Count.ToString(CultureInfo.InvariantCulture)} files unpacked and checked (launcher {staged.LauncherVersion})");
        }
        catch (OperationCanceledException)
        {
            _log.Write("update: cancelled");
            UpdateMessage = LauncherTexts.UpdateCancelled;
            UpdateStage = UpdateStage.Offer;
            return;
        }
        catch (Exception e) when (e is UpdateRefusedException or HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            string why = UpdateClient.Describe(e);
            _log.Write("update failed: " + why);
            UpdateMessage = LauncherTexts.UpdateFailed(why);
            UpdateStage = UpdateStage.Failed;
            return;
        }
        finally
        {
            _updateCancel = null;
        }

        if (GameRunning || GameLauncher.IsGameRunningFrom(app))
        {
            UpdateInstaller.DeleteTree(staging.Root);
            UpdateMessage = LauncherTexts.UpdateGameStartedMeanwhile;
            UpdateStage = UpdateStage.Offer;
            return;
        }

        OnUpdateProgress(new UpdateProgress(UpdateStep.Installing, default));
        ApplyResult applied = await Task.Run(() => UpdateInstaller.Apply(app, staged, staging.Journal));
        if (!applied.Applied)
        {
            _log.Write($"update: replacing the files failed ({applied.Problem}); {(applied.RolledBack ? "rolled back" : "ROLLBACK INCOMPLETE")}");
            if (applied.RolledBack)
            {
                UpdateInstaller.DeleteTree(staging.Root);
            }

            UpdateMessage = applied.RolledBack
                ? $"The files could not be replaced ({applied.Problem}). The old version was put back as it was; nothing was lost."
                : $"The files could not be replaced ({applied.Problem}), and some could not be put back. Download {release.Zip.Name} from the Releases page and unzip it over this folder; your saves and settings are not affected.";
            UpdateStage = UpdateStage.Failed;
            return;
        }

        _log.Write($"update: {applied.Replaced.ToString(CultureInfo.InvariantCulture)} files replaced, {applied.Added.ToString(CultureInfo.InvariantCulture)} added; restarting into {release.Tag}");
        UpdateInstaller.DeleteTree(staging.DownloadDirectory);
        UpdateStage = UpdateStage.Restarting;
        RestartRequested?.Invoke(Path.Combine(app, GameLauncher.LauncherFileName), CurrentVersion);
    }

    /// <summary>After the restart: says that the update worked (the toast of the new launcher).</summary>
    public void SayUpdated(ReleaseVersion from)
    {
        _log.Write(LauncherTexts.UpdatedLog(from, Build.Version, Build.InformationalVersion));
        UpdateStatus = LauncherTexts.UpdatedStatus(from, Build.Version);
        ShowToast(LauncherTexts.UpdatedToast(from, Build.Version));
    }

    // -- the offscreen render (no network) ----------------------------------------------------------------------------

    /// <summary>Shows <paramref name="release"/> as found (the render check draws the footer button and the overlay without asking GitHub).</summary>
    internal void PreviewUpdate(ReleaseInfo? release)
    {
        AvailableUpdate = release;
        UpdateStatus = release is null ? string.Empty : LauncherTexts.UpdateAvailable(release.Tag, "14:02");
    }

    /// <summary>Puts the overlay into a state for the render check.</summary>
    internal void PreviewUpdateState(UpdateStage stage, UpdateProgress? progress, string message)
    {
        _updateProgress = progress;
        UpdateMessage = message;
        UpdateStage = stage;
        Raise(null);
    }

    /// <summary>Makes the model a personal build for the render check of the Help page.</summary>
    internal void PreviewPersonal(bool personal)
    {
        _build = Build with { IsPersonal = personal };
        AvailableUpdate = personal ? null : _update;
        _checkNow?.Raise();
        RaiseUpdateButton();
        Raise(null);
    }

    /// <summary>Shows the footer button as while a check runs, for the render check (package K42; nothing is asked).</summary>
    internal void PreviewChecking(bool checking) => SetChecking(checking);

    /// <summary>Takes a made-up check result as a manual check would (the toast, the overlay), for the render check (package K42; nothing is asked or logged).</summary>
    internal void PreviewManualCheck(UpdateCheckResult result) => TakeCheckResult(result, CurrentVersion, manual: true);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // no browser: nothing else to try
        }
    }
}
