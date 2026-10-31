// The command line of Tsukihime.exe, project Tsuki.App (package K6 extends the H1 skeleton's): the game itself, the verification mode for agents, and the old skeleton / smoke /
// folder-dialog modes.
using System.Globalization;

namespace Tsuki.App;

internal enum AppMode
{
    /// <summary>The game.</summary>
    Play,

    /// <summary>The M0 skeleton window.</summary>
    Skeleton,

    /// <summary>The smoke checks.</summary>
    Smoke,

    /// <summary>The native folder dialog.</summary>
    PickFolder,
}

internal sealed class AppArgs
{
    public const string Usage =
        "usage: Tsukihime [--game DIR] [--lang ja|en|zc|zt] [--save-dir DIR] [--fullscreen | --windowed] [--movies real|virtual] [--log FILE]\n" +
        "                 [--prompts auto|keyboard|playstation|xbox]   (button prompts for this run; the Controls tab of the config screen sets and saves them)\n" +
        "       verification: --hidden --mute --autoplay [click|skip|auto] [--choices first|last|random[:seed]|list:i,j] [--fast]\n" +
        "                     [--script \"60:Enter;120:Esc+Down;200:click=960,540;300:shot=name;400:log\" | --script @file] [--shot-at f,f,...] [--shot-dir DIR]\n" +
        "                     [--shot-on-choice] [--exit-after FRAMES] [--no-save] [--mem-log SECONDS]\n" +
        "                     [--screenshot-dir DIR]   (where F12 saves; default %USERPROFILE%\\Pictures\\Tsukihime)   [--clipboard-file FILE]   (the copied text goes there, not to the clipboard)\n" +
        "       other:        --skeleton [--seconds N] | --smoke [--visible] [--report FILE] | --pick-folder [--timeout S]";

    public AppMode Mode { get; private set; } = AppMode.Play;

    public bool Visible { get; private set; }

    /// <summary><c>--fullscreen</c>: start in borderless fullscreen (and remember it).</summary>
    public bool Fullscreen { get; private set; }

    /// <summary><c>--windowed</c> (package K18): start in a window even when the remembered window mode is fullscreen (and remember it). The launcher passes one of the two on every PLAY.</summary>
    public bool Windowed { get; private set; }

    /// <summary>The window mode this command line asks for (true = fullscreen, false = windowed); null when it says nothing, so that the remembered one is used.</summary>
    public bool? WindowModeChoice => Fullscreen ? true : Windowed ? false : null;

    public string? ReportPath { get; private set; }

    public double Seconds { get; private set; }

    public double TimeoutSeconds { get; private set; } = 50;

    public string? GameDir { get; private set; }

    public string? SaveDir { get; private set; }

    public int? Language { get; private set; }

    public bool Hidden { get; private set; }

    public bool Mute { get; private set; }

    public bool Fast { get; private set; }

    public string? Autoplay { get; private set; }

    public string Choices { get; private set; } = "first";

    public string? Script { get; private set; }

    public List<long> ShotAt { get; } = [];

    public string? ShotDir { get; private set; }

    /// <summary>The folder the screenshot key writes to (<c>--screenshot-dir</c>, package K21); null: the player's Pictures\Tsukihime.</summary>
    public string? ScreenshotDir { get; private set; }

    /// <summary>A file that receives the texts Copy Text to Clipboard would put on the clipboard (<c>--clipboard-file</c>, package K21: the verification runs never touch the desktop clipboard).</summary>
    public string? ClipboardFile { get; private set; }

    public bool ShotOnChoice { get; private set; }

    public long ExitAfter { get; private set; }

    public bool VirtualMovies { get; private set; }

    public string? LogFile { get; private set; }

    public bool NoSave { get; private set; }

    /// <summary>Seconds between memory log lines (<c>--mem-log N</c>, package K11); 0 = none.</summary>
    public double MemLogSeconds { get; private set; }

    public Tsuki.Ui.PromptSetting? Prompts { get; private set; }

    /// <summary>Folder of the route sweep (<c>--sweep-dir</c>, package K15): small screenshots at scene starts, choices, movies, endings, picture changes and a steady interval, with a manifest.</summary>
    public string? SweepDir { get; private set; }

    /// <summary>A sweep shot every this many steps while a story runs (0 = none).</summary>
    public int SweepEvery { get; private set; }

    /// <summary>A sweep shot this many steps after a scene starts (0 = none).</summary>
    public int SweepScene { get; private set; } = 20;

    /// <summary>A sweep shot a few steps after the committed pictures changed, at most one per this many steps (0 = none).</summary>
    public int SweepChanges { get; private set; }

    /// <summary>A sweep shot when every this many messages has been fully revealed (0 = none).</summary>
    public int SweepMessages { get; private set; }

    /// <summary>The run stops after this many stories have ended (<c>--exit-after-ends N</c>, package K15; the prologue is the first); 0 = never.</summary>
    public int ExitAfterEnds { get; private set; }

    /// <summary>A file of flowchart node numbers, one per line (<c>--sweep-tour FILE</c>, package K15): the run jumps to each node in turn, plays its scene (a jump to the next one when another scene starts or <c>--tour-cap</c> steps have passed) and quits after the last.</summary>
    public string? SweepTour { get; private set; }

    /// <summary>A tour goes on to the next node only when the story ends (or the cap passes), not at the next scene (<c>--tour-end</c>).</summary>
    public bool TourUntilEnd { get; private set; }

    /// <summary>The most steps a tour spends on one node.</summary>
    public int TourCap { get; private set; } = 6000;

    /// <summary>The sweep shots are the canvas divided by this (4 = 480 x 270).</summary>
    public int SweepScale { get; private set; } = 4;

    /// <summary>The sweep stops taking shots after this many.</summary>
    public int SweepMax { get; private set; } = 200_000;

    public static bool TryParse(string[] args, out AppArgs? result, out string? error)
    {
        var a = new AppArgs();
        result = null;
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--smoke":
                    a.Mode = AppMode.Smoke;
                    break;
                case "--skeleton":
                    a.Mode = AppMode.Skeleton;
                    break;
                case "--pick-folder":
                    a.Mode = AppMode.PickFolder;
                    break;
                case "--visible":
                    a.Visible = true;
                    break;
                case "--fullscreen":
                    a.Fullscreen = true;
                    a.Windowed = false;
                    break;
                case "--windowed":
                    a.Windowed = true;
                    a.Fullscreen = false;
                    break;
                case "--hidden":
                    a.Hidden = true;
                    break;
                case "--mute":
                    a.Mute = true;
                    break;
                case "--fast":
                    a.Fast = true;
                    break;
                case "--shot-on-choice":
                    a.ShotOnChoice = true;
                    break;
                case "--no-save":
                    a.NoSave = true;
                    break;
                case "--autoplay":
                    a.Autoplay = "skip";
                    if (i + 1 < args.Length && args[i + 1] is "click" or "skip" or "auto")
                    {
                        a.Autoplay = args[++i];
                    }

                    break;
                case "--report" when i + 1 < args.Length:
                    a.ReportPath = args[++i];
                    break;
                case "--log" when i + 1 < args.Length:
                    a.LogFile = args[++i];
                    break;
                case "--game" when i + 1 < args.Length:
                    a.GameDir = args[++i];
                    break;
                case "--save-dir" when i + 1 < args.Length:
                    a.SaveDir = args[++i];
                    break;
                case "--shot-dir" when i + 1 < args.Length:
                    a.ShotDir = args[++i];
                    break;
                case "--screenshot-dir" when i + 1 < args.Length:
                    a.ScreenshotDir = args[++i];
                    break;
                case "--clipboard-file" when i + 1 < args.Length:
                    a.ClipboardFile = args[++i];
                    break;
                case "--choices" when i + 1 < args.Length:
                    a.Choices = args[++i];
                    break;
                case "--script" when i + 1 < args.Length:
                    a.Script = args[++i];
                    break;
                case "--sweep-dir" when i + 1 < args.Length:
                    a.SweepDir = args[++i];
                    break;
                case "--sweep-every" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int se):
                    a.SweepEvery = se;
                    i++;
                    break;
                case "--sweep-scene" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ss):
                    a.SweepScene = ss;
                    i++;
                    break;
                case "--sweep-messages" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sn):
                    a.SweepMessages = sn;
                    i++;
                    break;
                case "--exit-after-ends" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ea):
                    a.ExitAfterEnds = ea;
                    i++;
                    break;
                case "--sweep-tour" when i + 1 < args.Length:
                    a.SweepTour = args[++i];
                    break;
                case "--tour-end":
                    a.TourUntilEnd = true;
                    break;
                case "--tour-cap" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tc):
                    a.TourCap = tc;
                    i++;
                    break;
                case "--sweep-changes" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sc):
                    a.SweepChanges = sc;
                    i++;
                    break;
                case "--sweep-scale" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sd) && sd >= 1:
                    a.SweepScale = sd;
                    i++;
                    break;
                case "--sweep-max" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sm):
                    a.SweepMax = sm;
                    i++;
                    break;
                case "--prompts" when i + 1 < args.Length:
                    if (!Tsuki.Ui.PromptStyles.TryParse(args[++i], out Tsuki.Ui.PromptSetting prompts))
                    {
                        error = "--prompts takes auto, keyboard, playstation or xbox";
                        return false;
                    }

                    a.Prompts = prompts;
                    break;
                case "--lang" when i + 1 < args.Length:
                    a.Language = args[++i].ToLowerInvariant() switch
                    {
                        "ja" => 0,
                        "en" => 1,
                        "zc" => 2,
                        "zt" => 3,
                        _ => -1,
                    };
                    if (a.Language < 0)
                    {
                        error = "--lang takes ja, en, zc or zt";
                        return false;
                    }

                    break;
                case "--movies" when i + 1 < args.Length:
                    string m = args[++i];
                    if (m is not ("real" or "virtual"))
                    {
                        error = "--movies takes real or virtual";
                        return false;
                    }

                    a.VirtualMovies = m == "virtual";
                    break;
                case "--shot-at" when i + 1 < args.Length:
                    foreach (string part in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out long f) || f < 1)
                        {
                            error = "--shot-at takes a comma list of frame numbers";
                            return false;
                        }

                        a.ShotAt.Add(f);
                    }

                    break;
                case "--exit-after" when i + 1 < args.Length && long.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n > 0:
                    a.ExitAfter = n;
                    i++;
                    break;
                case "--mem-log" when i + 1 < args.Length && TryPositive(args[i + 1], out double ml):
                    a.MemLogSeconds = ml;
                    i++;
                    break;
                case "--seconds" when i + 1 < args.Length && TryPositive(args[i + 1], out double s):
                    a.Seconds = s;
                    i++;
                    break;
                case "--timeout" when i + 1 < args.Length && TryPositive(args[i + 1], out double t):
                    a.TimeoutSeconds = t;
                    i++;
                    break;
                default:
                    error = "unknown or incomplete argument: " + arg;
                    return false;
            }
        }

        if (a.Hidden && a.ShotAt.Count > 0 && a.ShotDir is null)
        {
            error = "--shot-at needs --shot-dir";
            return false;
        }

        result = a;
        return true;
    }

    private static bool TryPositive(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0;
}
