using Tsuki.Launcher.Core;

namespace Tsuki.Launcher.Avalonia;

public sealed partial class UnixLauncherModel
{
    public IReadOnlyList<KeyRow> Keys => [.. KeyTable.Rows.Select(row => PlatformKey(row, IsMac))];

    public IReadOnlyList<KeyRow> EssentialKeys => Keys.Where(row => KeyTable.Essentials.Any(essential => essential.Action == row.Action)).ToArray();

    public string ControllersText => "Pads are found by the game itself, at any time, also while it runs. Steam Input shows as an Xbox pad.";

    /// <summary>Whether the pages speak of a Mac (its keys, its licences, the app bundle): the real platform by default; tests choose either on any host.</summary>
    public bool IsMac { get; internal init; } = OperatingSystem.IsMacOS();

    /// <summary>The FFmpeg licence of the libraries beside the game: the Mac app builds its own LGPL 2.1-or-later FFmpeg; Linux ships the LGPL v3 build Windows has.</summary>
    public string FfmpegLicence => IsMac ? "LGPL 2.1+" : "LGPL v3";

    public string FfmpegLicenceText => IsMac
        ? "Movies and sound are decoded by FFmpeg's shared libraries (avcodec, avformat, avutil, swresample), built from unmodified FFmpeg source and shipped next to the game inside Tsukihime.app; their licence (licenses/FFmpeg-LGPL-2.1.txt, with FFmpeg-LICENSE.md) and source notice (licenses/FFmpeg-SOURCE.txt) are in Tsukihime.app/Contents/Resources/licenses."
        : "Movies and sound are decoded by FFmpeg's shared libraries (avcodec, avformat, avutil, swresample), shipped unchanged next to the game; their licence (licenses/FFmpeg-LGPL.txt, with the GPL v3 text it builds on as FFmpeg-GPL-3.0.txt) and source notice (licenses/FFmpeg-SOURCE.txt) are in the licenses folder beside it.";

    public string NoticesText => "The port's own code is under the Mozilla Public License 2.0 (licenses/LICENSE.txt). The full notices are in docs/THIRD_PARTY.md of the source and "
        + (IsMac ? "in Tsukihime.app/Contents/Resources/licenses." : "in the licenses folder beside the game.");

    public bool JaAvailable => LanguageAvailable(Tsuki.Engine.Contracts.Language.Ja);
    public bool EnAvailable => LanguageAvailable(Tsuki.Engine.Contracts.Language.En);
    public bool ZcAvailable => LanguageAvailable(Tsuki.Engine.Contracts.Language.Zc);
    public bool ZtAvailable => LanguageAvailable(Tsuki.Engine.Contracts.Language.Zt);

    private bool LanguageAvailable(Tsuki.Engine.Contracts.Language language) => !_folder.IsReady || _folder.LanguageList.Contains(language);

    /// <summary>
    /// A row in the words of a Mac keyboard: Option and Return for Alt and Enter; Fn before the function keys (alone they are media keys) and before the arrows for
    /// Page Up and Down; the key labelled Delete is Backspace there, and Fn+Delete the forward Delete. The action stays as it is (the Help page picks rows by it).
    /// </summary>
    internal static KeyRow PlatformKey(KeyRow row, bool mac)
    {
        if (!mac)
        {
            // Linux: the Windows table's last words about Print Screen do not apply; the desktop keeps that key
            return row.Action.EndsWith(" Print Screen stays Windows' own", StringComparison.Ordinal)
                ? row with { Action = row.Action[..^" Print Screen stays Windows' own".Length] + " Print Screen stays your desktop's own" }
                : row;
        }

        return row.Keys switch
        {
            "Enter / Space / Z / left click" => row with { Keys = "Return / Space / Z / click" },
            "Mouse wheel up / Page Up / I" => row with { Keys = "Mouse wheel up / Fn+Up / I" },
            "Mouse wheel / Page Up / Page Down" => row with { Keys = "Mouse wheel / Fn+Up / Fn+Down" },
            "Backspace (held)" => row with { Keys = "Delete (held)" },
            "F5 / F9" => row with { Keys = "Fn+F5 / Fn+F9" },
            "Delete" => row with { Keys = "Fn+Delete" },
            _ when row.Keys.StartsWith("Alt+Enter", StringComparison.Ordinal) => row with { Keys = "Option+Return" },
            _ when row.Keys.StartsWith("F12", StringComparison.Ordinal) => new KeyRow("Fn+F12", "Screenshot: a PNG into Pictures › Tsukihime."),
            _ => row,
        };
    }
}
