// Arcueid's face on the programs themselves (package K43). A release carries the port's crescent in Tsukihime.exe and TsukihimeLauncher.exe; once the launcher has
// cut her close-up from the player's own game files (GameIconPath, package K23) it writes that icon into both programs of its own folder (Core/ExeIconStamp), so
// Explorer, the taskbar and Alt+Tab show her. The switch on Help & about ("Show Arcueid on the program icons", settings.ini arcueid_icon, on by default) puts the
// crescent back. It runs off the window's thread after every refresh of the game folder (at start, after a folder is chosen, after the restart an update makes,
// since an update brings the crescent back) and writes nothing when the programs already carry the icon. A personal build (package K28) is never stamped: its
// programs carry her from the build. Every outcome is a line in launcher.log, never a message box; what the switch could not do is also said under it, in plain
// words (ProgramIconNote, review of K43). Only the window of a self-contained publish stamps (LauncherApp), and an update waits for a running stamp.
using System.Windows;
using Tsuki.Data.Icons;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher;

/// <summary>The program-icon part of the launcher's view model.</summary>
public sealed partial class LauncherModel
{
    /// <summary>The name the log and the self-test give the port's own icon.</summary>
    public const string CrescentName = "the port's crescent";

    /// <summary>The name the log and the self-test give the icon cut from the game files.</summary>
    public const string ArcueidName = "Arcueid";

    /// <summary>The note under the switch when the programs carry no Arcueid icon yet (no ready game folder, no picture).</summary>
    internal const string NoFaceYetNote = "Not applied yet: her icon is made once the launcher has found your game files (Game files).";

    /// <summary>The log's words for the same.</summary>
    private const string NoFaceYetProblem = "no Arcueid icon yet (the game folder is not ready, or her picture was not found); the programs are left as they are";

    private readonly SemaphoreSlim _stampGate = new(1, 1);
    private Task _stampTask = Task.CompletedTask;
    private Task _stampWork = Task.CompletedTask;
    private bool _stampCleaned;
    private string? _lastStampLine;
    private string _programIconNote = string.Empty;
    private static byte[]? _crescentIco;

    /// <summary>
    /// True when the launcher may write the program icons: the window of a self-contained publish (a release folder, <see cref="ExeIconStamp.IsSelfContained"/>),
    /// and the stamp self-test (<c>--stamp-selftest</c>). The other verification modes (<c>--render</c>, <c>--selftest</c>, <c>--update-selftest</c>) and a
    /// build output of the source leave the folder they run from as it is.
    /// </summary>
    public bool StampProgramIcons { get; set; }

    /// <summary>
    /// "Show Arcueid on the program icons" (settings.ini <c>arcueid_icon</c>, on when the key is absent). A personal build shows it on and fixed: its programs carry
    /// her from the build (review of K43: the switch read off while the line under it said her face is there).
    /// </summary>
    public bool ShowArcueidOnPrograms
    {
        get => Build.IsPersonal || (_profile.Settings.ArcueidIcon ?? true);
        set
        {
            if (Build.IsPersonal || value == ShowArcueidOnPrograms)
            {
                return;
            }

            UpdateSettings(s => s with { ArcueidIcon = value });
            if (StampProgramIcons)
            {
                _ = StampIconsAsync(value ? "the switch was turned on" : "the switch was turned off");
            }
        }
    }

    /// <summary>The switch can be used (not in a personal build).</summary>
    public bool ProgramIconSwitchEnabled => !Build.IsPersonal;

    /// <summary>The line under the switch, and after a stamp that could not do what the switch asks, why (<see cref="ProgramIconNote"/>).</summary>
    public string ProgramIconDescription => Build.IsPersonal
        ? "Personal build: Tsukihime.exe and TsukihimeLauncher.exe carry her face from the build itself."
        : "Tsukihime.exe and TsukihimeLauncher.exe show her face, made from your game files. Off: the port's moon icon." + (_programIconNote.Length > 0 ? " " + _programIconNote : string.Empty);

    /// <summary>What the last stamp of this session could not do, in plain words (empty: the programs carry what the switch asks, or nothing was tried).</summary>
    public string ProgramIconNote
    {
        get => _programIconNote;
        private set
        {
            if (Set(ref _programIconNote, value))
            {
                Raise(nameof(ProgramIconDescription));
            }
        }
    }

    /// <summary>The stamp that runs now or ran last (the self-test and the tests wait for it).</summary>
    public Task ProgramIconsIdle => _stampTask;

    /// <summary>
    /// The worker of the stamp that runs now or ran last: the files it changes (the application waits for it before the process ends, so that closing the window
    /// never cuts the launcher's two renames; it needs no dispatcher to finish).
    /// </summary>
    public Task ProgramIconsWork => _stampWork;

    /// <summary>
    /// Puts the icon the switch asks for on both programs of the launcher's folder (Core/ExeIconStamp): Arcueid when it is on and her icon is built, the crescent
    /// when it is off. Runs one at a time, off the window's thread; <paramref name="why"/> goes into the log line. Never throws.
    /// </summary>
    public Task StampIconsAsync(string why)
    {
        Task next = RunStampAsync(why);
        _stampTask = next;
        return next;
    }

    /// <summary>
    /// Waits until no stamp runs. The update (<see cref="RunUpdateAsync"/>) calls it after it has set <c>_updateRunning</c>, so no stamp starts after it: the two
    /// never rename the programs or probe the folder at the same time (review of K43).
    /// </summary>
    private async Task WaitForStampAsync()
    {
        await _stampGate.WaitAsync();
        _stampGate.Release();
    }

    private async Task RunStampAsync(string why)
    {
        await _stampGate.WaitAsync();
        try
        {
            if (Build.IsPersonal)
            {
                LogStamp($"program icons: personal build, its programs carry Arcueid from the build ({Build.InformationalVersion}); nothing is written");
                return;
            }

            if (UpdateBusy || _updateRunning)
            {
                // review of K43: from the moment Update now is pressed (RunUpdateAsync waits for a stamp that runs, then none starts until the update is over)
                LogStamp($"program icons ({why}): not now, an update is being installed");
                return;
            }

            bool arcueid = ShowArcueidOnPrograms;
            string? face = _gameIconPath;
            byte[]? crescent = CrescentIco();
            string app = _launcherDirectory;
            bool clean = !_stampCleaned;
            _stampCleaned = true;
            Task<(StampCleanup Cleanup, IReadOnlyList<StampResult>? Results, string? Problem)> work = Task.Run(() =>
            {
                // every stamp first puts back a program an earlier one left missing; the first of a session also deletes the old copies (review of K43)
                StampCleanup done = ExeIconStamp.CleanLeftovers(app, deleteCopies: clean);
                (IReadOnlyList<StampResult>? r, string? p) = Stamp(app, arcueid, face, crescent);
                return (done, r, p);
            });
            _stampWork = work;
            (StampCleanup c, IReadOnlyList<StampResult>? results, string? problem) = await work;

            if (c.Repaired.Count > 0 || c.Deleted > 0 || c.Left > 0)
            {
                _log.Write($"program icons: clean-up of an earlier stamp: {(c.Repaired.Count > 0 ? "put back " + string.Join(" and ", c.Repaired) + ", " : string.Empty)}{c.Deleted} old copy(ies) deleted, {c.Left} still in use");
            }

            LogStamp(problem is not null
                ? $"program icons ({why}): {problem}"
                : $"program icons ({why}, {(arcueid ? ArcueidName : CrescentName)}): {string.Join("; ", results!.Select(r => r.Describe()))}");
            ProgramIconNote = results is not null ? ExeIconStamp.PlayerNote(results)
                : problem == NoFaceYetProblem ? NoFaceYetNote
                : "Not applied: the launcher couldn't read its own moon icon. launcher.log, next to your saves, says more.";
        }
        catch (Exception e)
        {
            // whatever went wrong, the programs are as they were (the stamp only replaces a program with a checked copy) and the launcher goes on: one line, no box
            _log.Write($"program icons ({why}): not changed ({e.GetType().Name}: {e.Message})");
        }
        finally
        {
            _stampGate.Release();
        }
    }

    /// <summary>The stamp itself (worker thread): the results, or why nothing could be tried.</summary>
    private static (IReadOnlyList<StampResult>? Results, string? Problem) Stamp(string app, bool arcueid, string? facePath, byte[]? crescentIco)
    {
        IReadOnlyList<IcoImage>? crescent = crescentIco is null ? null : IcoFile.Read(crescentIco);
        IReadOnlyList<IcoImage>? face = facePath is null ? null : IcoFile.ReadFile(facePath);
        if (crescent is null)
        {
            return (null, "the launcher's own crescent icon could not be read; nothing is written");
        }

        var known = new List<IconChoice> { new(CrescentName, crescent) };
        if (face is not null)
        {
            known.Add(new IconChoice(ArcueidName, face));
        }

        if (arcueid && face is null)
        {
            return (null, NoFaceYetProblem);
        }

        return (ExeIconStamp.StampFolder(app, arcueid ? known[^1] : known[0], known), null);
    }

    /// <summary>Writes the line unless it is the one written last (the refresh after every game runs the check again; an unchanged result is said once).</summary>
    private void LogStamp(string line)
    {
        if (line != _lastStampLine)
        {
            _lastStampLine = line;
            _log.Write(line);
        }
    }

    /// <summary>The crescent .ico of the launcher's resources (assets/icon/tsukihime.ico, the icon a release's programs carry); null when it cannot be read.</summary>
    internal static byte[]? CrescentIco()
    {
        if (_crescentIco is { } cached)
        {
            return cached;
        }

        try
        {
            System.Windows.Resources.StreamResourceInfo? info = Application.GetResourceStream(new Uri("pack://application:,,,/TsukihimeLauncher;component/Assets/tsukihime.ico", UriKind.Absolute));
            if (info?.Stream is not { } stream)
            {
                return null;
            }

            using (stream)
            {
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                return _crescentIco = bytes.ToArray();
            }
        }
        catch (Exception e) when (e is IOException or UriFormatException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
