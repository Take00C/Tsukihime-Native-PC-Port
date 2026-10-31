// The launcher's view of one profile (package K17): settings.ini plus what save.dat says. The language and the window mode are applied by the game once and then live in the
// config block of save.dat (the config screen and Alt+Enter change them there), so the value to show is the pending settings.ini key if there is one, else the save file's.
// The windowed size (package K19) is settings.ini's, clamped the way the game clamps it (WindowFit, the same file in both programs).
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Platform;

namespace Tsuki.Launcher.Core;

/// <summary>A window size preset the Display page offers (package K19).</summary>
/// <param name="Width">Client width.</param>
/// <param name="Height">Client height.</param>
public sealed record WindowSizeChoice(int Width, int Height)
{
    /// <summary>"1600 × 900".</summary>
    public string Label => $"{Width} × {Height}";

    /// <summary>The presets that fit a work area, smallest first.</summary>
    public static IReadOnlyList<WindowSizeChoice> For((int Width, int Height) workArea) =>
        [.. WindowFit.PresetsFor(workArea.Width, workArea.Height).Select(p => new WindowSizeChoice(p.Width, p.Height))];

    /// <summary>Every preset (no monitor known).</summary>
    public static IReadOnlyList<WindowSizeChoice> All { get; } = [.. WindowFit.Presets.Select(p => new WindowSizeChoice(p.Width, p.Height))];
}

/// <summary>The values the settings pages show.</summary>
/// <param name="Settings">What settings.ini holds.</param>
/// <param name="Saves">What save.dat holds.</param>
public sealed record LauncherProfile(AppSettingsData Settings, SaveSummary Saves)
{
    /// <summary>The game language of the next start.</summary>
    public Language Language =>
        Settings.GameLanguage ?? (Saves.ConfigLanguage is { } l && Enum.IsDefined((Language)l) ? (Language)l : Language.En);

    /// <summary>Whether the next start is borderless fullscreen.</summary>
    public bool Fullscreen => Settings.Fullscreen ?? Saves.WindowMode == 1;

    /// <summary>VSync (on unless the file says off).</summary>
    public bool VSync => Settings.VSync ?? true;

    /// <summary>The windowed size the file asks for (1600 x 900 unless the file says otherwise); the game clamps it to the monitor, see <see cref="EffectiveWindowSize"/>.</summary>
    public (int Width, int Height) WindowSize => Settings.WindowSize ?? (WindowFit.DefaultWidth, WindowFit.DefaultHeight);

    /// <summary>The windowed size the game will use on a monitor with that work area: <see cref="WindowSize"/> made to fit (unchanged when the work area is unknown).</summary>
    public (int Width, int Height) EffectiveWindowSize((int Width, int Height)? workArea)
    {
        (int w, int h) = WindowSize;
        return workArea is { } area ? WindowFit.Clamp(w, h, area.Width, area.Height) : (w, h);
    }

    /// <summary>Reads settings.ini and save.dat, and probes whether the profile folder can be written (the ready check warns when it cannot; package R1).</summary>
    public static LauncherProfile Read(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new LauncherProfile(AppSettings.Load(paths.SettingsFile), SaveTools.Summarize(paths.SaveFile) with { WriteError = SaveTools.ProbeWritable(paths.DataDirectory) });
    }

    /// <summary>The names of the language: the native name and an English one.</summary>
    public static (string Native, string English) LanguageName(Language language) => language switch
    {
        Language.Ja => ("日本語", "Japanese"),
        Language.Zc => ("简体中文", "Simplified Chinese"),
        Language.Zt => ("繁體中文", "Traditional Chinese"),
        _ => ("English", "English"),
    };

    /// <summary>The status line of the footer: "1920 x 1080 . Windowed . Japanese . Keyboard prompts".</summary>
    /// <param name="monitorSize">The size of the monitor the game goes fullscreen on, when known (the line then starts with it).</param>
    /// <param name="workArea">The work area of that monitor, when known: a windowed size is shown as the game will clamp it.</param>
    public string StatusLine((int Width, int Height)? monitorSize = null, (int Width, int Height)? workArea = null)
    {
        (int w, int h) = EffectiveWindowSize(workArea);
        string size = Fullscreen
            ? (monitorSize is { } m ? $"{m.Width} × {m.Height}  ·  Fullscreen" : "Fullscreen")
            : $"{w} × {h}  ·  Windowed";
        string prompts = Settings.Prompts switch
        {
            Tsuki.Ui.PromptSetting.Keyboard => "Keyboard prompts",
            Tsuki.Ui.PromptSetting.PlayStation => "PlayStation prompts",
            Tsuki.Ui.PromptSetting.Xbox => "Xbox prompts",
            _ => "Auto prompts",
        };
        return $"{size}  ·  {LanguageName(Language).Native}  ·  {prompts}";
    }
}
