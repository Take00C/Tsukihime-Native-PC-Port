// The footer's update button (package K42, GitHub issue #6 "Something around this corner"): always shown left of Log. Without a known newer release it reads
// "Check for updates" in the quiet look of Log and asks GitHub once (the same manual check as Help & about > Check now); while a check runs it reads "Checking…"
// and cannot be pressed; once a newer release is known it is K40's gold "Update to vX.Y.Z", which opens the update overlay. A personal build (package K28) keeps
// the quiet button, and pressing it only says that such a build does not update itself. No WPF types here, so the Synthetic tests compile this file too.
namespace Tsuki.Launcher.Core;

/// <summary>What a check of the release list came to.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>A newer release can be installed.</summary>
    Newer,

    /// <summary>The running version is the newest.</summary>
    Newest,

    /// <summary>GitHub could not be asked or gave no usable answer (offline, an error status, not a release list).</summary>
    Failed,
}

/// <summary>The footer's update button in one state.</summary>
/// <param name="Label">Its text, also its name for screen readers.</param>
/// <param name="Enabled">It can be pressed.</param>
/// <param name="Accent">K40's gold <c>UpdateButton</c> look (a newer release is known); otherwise the quiet <c>CheckUpdatesButton</c> look of Log.</param>
/// <param name="ToolTip">What pressing it does.</param>
public sealed record UpdateButtonState(string Label, bool Enabled, bool Accent, string ToolTip)
{
    /// <summary>The text without a known newer release.</summary>
    public const string CheckLabel = "Check for updates";

    /// <summary>The text while a check runs (U+2026, the ellipsis; the launcher's fonts have it).</summary>
    public const string CheckingLabel = "Checking…";

    /// <summary>The toast of a personal build's button.</summary>
    public const string PersonalToast = "This personal build doesn't update itself. New versions are on the port's GitHub Releases page.";

    /// <summary>The toast after a manual check that could not reach GitHub or got no usable answer (the Help page's status line has the reason).</summary>
    public const string FailedToast = "Couldn't reach GitHub. Try again later; Help & about says what went wrong.";

    /// <summary>The theme's resource key of the look: <c>UpdateButton</c> (gold) or <c>CheckUpdatesButton</c> (quiet).</summary>
    public string StyleKey => Accent ? "UpdateButton" : "CheckUpdatesButton";

    /// <summary>The button for the launcher's state.</summary>
    /// <param name="personal">A personal build: never checks, the button only says so.</param>
    /// <param name="checking">A check (the start's or a manual one) is running.</param>
    /// <param name="busy">An update is downloading or installing.</param>
    /// <param name="update">The newer release known, or null.</param>
    public static UpdateButtonState Of(bool personal, bool checking, bool busy, ReleaseInfo? update)
    {
        if (personal)
        {
            return new UpdateButtonState(CheckLabel, true, false, "This personal build doesn't update itself");
        }

        if (checking)
        {
            return new UpdateButtonState(CheckingLabel, false, false, "Asking GitHub for the newest release");
        }

        return update is not null
            ? new UpdateButtonState($"Update to {update.Tag}", !busy, true, $"{update.Tag} of the port is on GitHub: see what is new and install it")
            : new UpdateButtonState(CheckLabel, !busy, false, "Ask GitHub whether a newer release of the port is out");
    }

    /// <summary>What <paramref name="result"/> came to.</summary>
    public static UpdateCheckOutcome OutcomeOf(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Error is not null ? UpdateCheckOutcome.Failed : result.Update is not null ? UpdateCheckOutcome.Newer : UpdateCheckOutcome.Newest;
    }

    /// <summary>The toast after a manual check, or null when it found a newer release (the overlay opens instead).</summary>
    /// <param name="result">The check's result.</param>
    /// <param name="current">The version compared with.</param>
    public static string? ToastAfterManualCheck(UpdateCheckResult result, ReleaseVersion current) => OutcomeOf(result) switch
    {
        UpdateCheckOutcome.Failed => FailedToast,
        UpdateCheckOutcome.Newest => $"You have the newest version ({current.Tag}).",
        _ => null,
    };
}
