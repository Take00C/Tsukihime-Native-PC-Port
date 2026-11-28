using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Tsuki.App.Services;
using Tsuki.Engine.Trophies;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher.Avalonia;

/// <summary>A row of the Achievements page.</summary>
/// <param name="Row">The achievement.</param>
/// <param name="Tile">
/// The square at the row's left. An earned achievement: its icon, cut from the player's own pictures, and the blank tile of its grade until that has been made;
/// the emblem of its grade (a cup the launcher draws in the tiles' frame) where the icon cannot be made (no game folder, or the picture is not in it), and the
/// blank tile there for the first moment the page is shown, until the emblems are made. A locked
/// one: the padlock tile of its grade, never a game picture. Null only before the page was first shown: no bitmap is made before that.
/// </param>
public sealed record AchievementItem(AchievementRow Row, Bitmap? Tile)
{
    private const string Check = "M6.5,12.5 L10.5,16.5 L17.5,8.5";
    private const string Lock = "M6.5,10.5 L17.5,10.5 L17.5,19.5 L6.5,19.5 Z M8.5,10.5 L8.5,8 C8.5,5.8 10,4.5 12,4.5 C14,4.5 15.5,5.8 15.5,8 L15.5,10.5";
    private static readonly IBrush Bright = Brush("#E9EDF8");
    private static readonly IBrush Muted = Brush("#98A2C8");
    private static readonly IBrush Soft = Brush("#B9C3E3");
    private static readonly IBrush Faint = Brush("#6C7699");
    private static readonly IBrush Green = Brush("#7ED6A8");

    // the Windows launcher's grade colours: platinum, gold, silver, bronze
    private static readonly IBrush[] GradeInks = [Brush("#BFD9F2"), Brush("#E6C36A"), Brush("#C3CBDB"), Brush("#CB8F62")];

    public string Name => Row.Name;
    public string Detail => Row.Detail;
    public string Grade => Row.GradeLabel;
    public string State => Row.StateLabel;
    public bool Earned => Row.Earned;
    public bool HasTile => Tile is not null;

    /// <summary>The sign before the state: a check once earned, a lock before (path data, 24 × 24).</summary>
    public string StateGlyph => Row.Earned ? Check : Lock;

    // the words of a locked achievement are dimmed: its name, its grade and its state (its tile is the quiet one with the padlock)
    public IBrush NameInk => Row.Earned ? Bright : Muted;
    public IBrush GradeInk => Row.Earned ? GradeInks[Math.Clamp((int)Row.Grade, 0, GradeInks.Length - 1)] : Faint;
    public IBrush GlyphInk => Row.Earned ? Green : Faint;
    public IBrush StateInk => Row.Earned ? Soft : Faint;

    private static IBrush Brush(string colour) => new ImmutableSolidColorBrush(Color.Parse(colour));
}

/// <summary>One grade of the summary: its emblem, the grade's word and "2 / 4".</summary>
/// <param name="Grade">The grade.</param>
/// <param name="Emblem">The emblem of the grade (a cup the launcher draws, no game picture), twice the size it is drawn at; null until it is made, after the page was first shown.</param>
/// <param name="Label">The grade's word.</param>
/// <param name="Earned">How many of the grade are earned.</param>
/// <param name="Total">How many of the grade there are.</param>
public sealed record GradeTally(TrophyGrade Grade, Bitmap? Emblem, string Label, int Earned, int Total)
{
    public string Count => LauncherTexts.AchievementGradeCount(Earned, Total);
}

public sealed partial class UnixLauncherModel
{
    private AchievementSummary _achievements;
    private AchievementStamp _achievementStamp;
    private int _achievementReading;
    private int _achievementTab;
    private FolderLook? _achievementFolderLook;

    // settings.ini as the launcher last read or wrote it (its size and time), and the look a worker is reading it for now
    private FileLook _settingsLook;
    private FileLook? _settingsLookRead;
    private int _settingsReading;
    private Task _settingsWork = Task.CompletedTask;

    // The tiles of the rows. The icon of an earned achievement is cut from the player's own pictures, so it is made on a worker, one at a time, and only once
    // the page was shown; its bitmap is kept for as long as the game folder stays the same. All of this is read and written on the window's thread.
    private readonly Bitmap?[] _icons = new Bitmap?[TrophyTable.Count];
    private readonly IconState[] _iconStates = new IconState[TrophyTable.Count];
    private readonly Bitmap?[] _lockedTiles = new Bitmap?[4];
    private readonly Bitmap?[] _blankTiles = new Bitmap?[4];

    // The emblems of the four grades, drawn by the launcher's core (no game picture): at the size of a row's tile, and at the size of the summary's. Drawing one
    // at 240 px takes tens of milliseconds, so the four are made on a worker the first time the page is shown, and kept for the life of the model: nothing
    // replaces them. Until they are there the summary shows its counts without them, and a row that waits for one shows the blank tile of its grade.
    private readonly Bitmap?[] _emblemTiles = new Bitmap?[4];
    private readonly Bitmap?[] _emblemBadges = new Bitmap?[4];
    private Task _emblemWork = Task.CompletedTask;
    private readonly Queue<(int Id, string Folder, int Round)> _iconQueue = new();
    private Task _iconWork = Task.CompletedTask;
    private string? _iconFolder;
    private bool _iconFolderKnown;
    private int _iconRound;
    private bool _iconsBeingMade;
    private bool _tilesShown;

    private enum IconState
    {
        /// <summary>Nobody asked for it yet.</summary>
        NotAsked,

        /// <summary>It waits for the worker, or the worker is making it.</summary>
        Asked,

        /// <summary>Its bitmap is in <see cref="_icons"/>.</summary>
        Made,

        /// <summary>It cannot be made from this game folder: the row shows the emblem of its grade.</summary>
        None,
    }

    /// <summary>
    /// The side in pixels of a tile's bitmap: twice the <see cref="AchievementTileSize"/> a row draws it at. The launcher reduces the 240 px picture to it by
    /// area average itself, so that the page only halves it at 100 % (exactly: every pixel is the mean of four) and draws it as it is at 200 %. The ring, hardly
    /// more than a pixel wide in a row, then comes out the same on all four sides; left to the toolkit, a fifth of 240 px is a quarter-size copy filtered once more.
    /// </summary>
    internal const int TilePixels = 2 * AchievementTileSize;

    /// <summary>The side a row draws its tile at (the page's markup says the same number).</summary>
    internal const int AchievementTileSize = 52;

    /// <summary>The side of a grade's emblem in its bitmap: twice the <see cref="GradeEmblemSize"/> the summary draws it at, for the reason <see cref="TilePixels"/> gives.</summary>
    internal const int EmblemPixels = 2 * GradeEmblemSize;

    /// <summary>The side the summary draws a grade's emblem at (the page's markup says the same number).</summary>
    internal const int GradeEmblemSize = 40;

    /// <summary>
    /// Where the icon of an earned achievement comes from: the game folder and the trophy id give the picture, or null when it cannot be made. Called on a
    /// worker thread, one call at a time, and never for a locked achievement. Tests count the calls here.
    /// </summary>
    internal Func<string, int, AchievementIcon?> IconSource { get; init; } = Achievements.Icon;

    /// <summary>Where the emblem of a grade comes from (the core's drawing, 240 × 240). Called on a worker thread, once per grade; tests hold it back.</summary>
    internal Func<TrophyGrade, AchievementIcon> EmblemSource { get; init; } = Achievements.EmblemIcon;

    /// <summary>For the tests: called on the thread that reads settings.ini for a look of the page or its timer, each time such a look reads it.</summary>
    internal Action? SettingsLookRead { get; init; }

    /// <summary>
    /// Ends once the grades' emblems are made and every icon asked for so far has arrived or was found impossible to make (the offscreen render waits for it
    /// before it draws the page).
    /// </summary>
    internal Task AchievementIconWork => _emblemWork.IsCompleted ? _iconWork : Task.WhenAll(_emblemWork, _iconWork);

    /// <summary>The rows of the chosen tab, by id. Changed in place, so that the page keeps its scroll position when the game earns one.</summary>
    public ObservableCollection<AchievementItem> AchievementRows { get; } = [];

    /// <summary>The summary's four grades, each with its emblem: platinum, gold, silver, bronze.</summary>
    public IReadOnlyList<GradeTally> AchievementGrades { get; private set; } = [];

    public string AchievementsPercent => LauncherTexts.AchievementPercent(_achievements.Percent);
    public string AchievementsCount => _achievements.CountLine;
    public int AchievementsEarned => _achievements.Earned;
    public int AchievementsTotal => _achievements.Total;
    public string AchievementsAllTab => LauncherTexts.AchievementsTab(LauncherTexts.AchievementsTabAll, _achievements.Total);
    public string AchievementsEarnedTab => LauncherTexts.AchievementsTab(LauncherTexts.AchievementsTabEarned, _achievements.Earned);
    public string AchievementsLockedTab => LauncherTexts.AchievementsTab(LauncherTexts.AchievementsTabLocked, _achievements.Total - _achievements.Earned);

    /// <summary>The tab: 0 all, 1 earned, 2 locked. It is the model's, so it stays when the page is left and shown again.</summary>
    public int AchievementTabIndex
    {
        get => _achievementTab;
        set
        {
            if (value is < 0 or > 2 || !Set(ref _achievementTab, value)) { return; }
            ShowAchievementRows();
        }
    }

    public bool AchievementsListEmpty => AchievementRows.Count == 0;
    /// <summary>The sentence in place of the list when the tab on show has no row (the words both launchers share).</summary>
    public string AchievementsEmptyText => _achievementTab == 1 ? LauncherTexts.AchievementsNothingEarned : LauncherTexts.AchievementsNothingLocked;

    /// <summary>True when trophies.ini is there and cannot be read (the page says so; the rows then show what the saves imply).</summary>
    public bool AchievementsDamaged => _achievements.Damaged;

    /// <summary>True while no game folder gives the chapter names and the chapters' scenes (the page says where to choose it).</summary>
    public bool AchievementsNoGame => !_achievements.GameNames;

    /// <summary>
    /// The notice in the game when an achievement is earned (settings.ini <c>trophy_notice</c>: off unless the file says on, AppSettingsData.TrophyNoticeOn).
    /// Off only hides the notice: the achievement is recorded either way.
    /// </summary>
    public bool TrophyNotice
    {
        get => _profile.Settings.TrophyNoticeOn;
        set => UpdateSettings(s => s with { TrophyNotice = value });
    }

    /// <summary>
    /// The chime as an achievement's notice comes in (settings.ini <c>trophy_sound</c>: off unless the file says on, AppSettingsData.TrophySoundOn). The game
    /// plays it only while <see cref="TrophyNotice"/> is on too; the switch can be set either way, and its words say so.
    /// </summary>
    public bool TrophySound
    {
        get => _profile.Settings.TrophySoundOn;
        set => UpdateSettings(s => s with { TrophySound = value });
    }

    /// <summary>
    /// Whether a game runs on this machine, from any folder: a restore or an import then leaves trophies.ini to that game, which writes it while it runs. Tests
    /// answer for the machine.
    /// </summary>
    internal Func<bool> AnyGameRunning { get; init; } = GameLauncher.IsGameRunning;

    /// <summary>Called on the copying thread before each file of a portable switch is copied (tests hold the copy there); none in the launcher.</summary>
    internal Action<string>? PortableCopyStep { get; init; }

    /// <summary>
    /// The folder whose chapter names, chapter scenes and pictures the page shows: the chosen folder once it looks like the game, also while the check still
    /// misses a file (what can be read of it is shown; the rest falls back to the launcher's own names and the grades' emblems).
    /// </summary>
    private string? AchievementFolder => _folder.State is FolderState.Ready or FolderState.Incomplete ? _folder.Path : null;

    /// <summary>
    /// Reads the achievements again, on a worker thread (the first reading of a game folder opens it, and a changed save.dat is read whole). With
    /// <paramref name="onlyIfChanged"/>, only when trophies.ini or save.dat changed since the last reading, or when the last reading was short of what they hold
    /// (a file another program held, which can come free without changing): two file lookups otherwise, which is what the window does every two seconds, so
    /// that the page follows a game that is running. The notice switch follows settings.ini on the same looks (also read on a worker). The launcher only reads
    /// these files.
    /// </summary>
    internal Task ReadAchievementsAgainAsync(bool onlyIfChanged = false)
    {
        Task settings = FollowSettingsFileAsync();
        AchievementStamp stamp = Achievements.Stamp(_paths);
        if (onlyIfChanged && stamp == _achievementStamp && !_achievements.Incomplete) { return settings; }
        _achievementStamp = stamp;
        Task achievements = ReadAchievementsAsync();
        return settings.IsCompleted ? achievements : Task.WhenAll(settings, achievements);
    }

    private async Task ReadAchievementsAsync()
    {
        int reading = ++_achievementReading;
        string? folder = AchievementFolder;
        AchievementSummary read = await Task.Run(() => Achievements.Read(_paths, folder));
        // a reading that a later one overtook says nothing, and one that found nothing new leaves the page alone
        if (reading != _achievementReading || read.Equals(_achievements)) { return; }
        _achievements = read;
        ShowAchievements();
    }

    /// <summary>
    /// The game writes settings.ini too (Config › System › Achievement Notify and Achievement Sound, the window mode, the prompts), also while the launcher's
    /// window stays in front beside it. Its size and time are kept whenever the launcher reads or writes it; when the page or the timer finds them changed,
    /// settings.ini alone is read again on a worker (LauncherProfile.Read writes a probe file and is not for polling), and the switches show what it holds now,
    /// the defaults too.
    /// </summary>
    private Task FollowSettingsFileAsync()
    {
        FileLook look = FileLook.Of(_paths.SettingsFile);
        if (look == _settingsLook) { return Task.CompletedTask; }
        // a reading of this very look is under way: what it finds is the answer to this look too
        if (_settingsLookRead == look) { return _settingsWork; }
        return _settingsWork = ReadSettingsFileAsync(look);
    }

    private async Task ReadSettingsFileAsync(FileLook look)
    {
        int reading = ++_settingsReading;
        _settingsLookRead = look;
        string path = _paths.SettingsFile;
        Action? told = SettingsLookRead;
        AppSettingsData? read = await Task.Run(() =>
        {
            told?.Invoke();
            return ReadSettingsFile(path, look);
        });

        // the launcher read or wrote the file meanwhile, or a later look reads it: this reading says nothing
        if (reading != _settingsReading) { return; }
        _settingsLookRead = null;
        // not read (another program holds it, or it changed while it was read): the switches keep what they show, and the next look reads it again
        if (read is not { } settings) { return; }
        _settingsLook = look;
        if (settings == _profile.Settings) { return; }
        _profile = _profile with { Settings = settings };
        Raise(nameof(TrophyNotice), nameof(TrophySound), nameof(Fullscreen), nameof(VSync), nameof(PromptsIndex), nameof(WindowSize), nameof(WindowSizeNote), nameof(LanguageIndex), nameof(StatusLine));
    }

    /// <summary>
    /// What settings.ini holds, as the game and the launcher read it (AppSettings.Parse): the defaults when there is no file, and also for a file that holds
    /// nothing but defaults (the game writes such files: its prompts back to Auto, the one-start keys cleared). Null when the file is there and could not be
    /// opened or read, or changed while it was read: what it holds is not known then. One try, no waiting for a program that holds the file.
    /// </summary>
    private static AppSettingsData? ReadSettingsFile(string path, FileLook look)
    {
        if (!look.Exists) { return default(AppSettingsData); }
        try
        {
            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                text = reader.ReadToEnd();
            }

            return FileLook.Of(path) == look ? AppSettings.Parse(text) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps the size and time of settings.ini as the launcher reads or writes it now, so that the next look does not read again what the launcher knows; a look's
    /// reading that is still under way is older than that and is dropped when it ends.
    /// </summary>
    private void KeepSettingsLook()
    {
        _settingsLook = FileLook.Of(_paths.SettingsFile);
        _settingsReading++;
        _settingsLookRead = null;
    }

    /// <summary>
    /// The achievements with the game folder's chapter names and scenes, read on a worker thread. What was read of the game folder before (the names, the
    /// chapter scenes, the icons) is forgotten first when the folder is another one or looks different now: its state, its passed checks or its size changed, as
    /// when a copy that was still running at the first reading has finished, or a file was repaired. A refresh that finds the folder as it was keeps all of it,
    /// so that the icons do not go back to blank tiles each time a game ends; "Check again" forgets it whatever it looks like.
    /// </summary>
    private async Task LoadAchievementsAsync()
    {
        var look = new FolderLook(_folder.Path, _folder.State, _folder.ChecksPassed, _folder.TotalBytes);
        bool changed = _achievementFolderLook is { } last && last != look;
        _achievementFolderLook = look;
        if (changed) { Achievements.Forget(); }
        await ReadAchievementsAgainAsync();
        string? folder = AchievementFolder;
        // another game folder, or none any more, or the same one changed: the icons made before are not this one's
        if (changed || !_iconFolderKnown || _iconFolder != folder) { ForgetIcons(folder); }
    }

    /// <summary>
    /// The Game files page's "Check again": what was read from the game folder (names, the trophy scenes, the icons) is read again too. The icons are made again
    /// at once when the page was shown before; the rows show their blank tiles meanwhile.
    /// </summary>
    private void ForgetAchievementFolder()
    {
        Achievements.Forget();
        // before the first look at the game folder has ended there is no icon to forget
        if (_iconFolderKnown) { ForgetIcons(_iconFolder); }
    }

    /// <summary>
    /// The Achievements page is about to be shown for the first time: from now on the rows carry their tiles, the grades' emblems are made on a worker, and
    /// the icons of the earned ones are made (never at the launcher's start: a player who does not open the page pays nothing for them, and before the window
    /// is up no bitmap can be made at all). The rows' padlocks and blank tiles are made here before the worker starts (a millisecond or two each): the core draws
    /// its tiles one at a time, and the first showing of the page does not wait behind an emblem.
    /// </summary>
    private void ShowAchievementTiles()
    {
        if (_tilesShown) { return; }
        _tilesShown = true;
        ShowAchievements();
        _emblemWork = MakeEmblemsAsync();
    }

    /// <summary>The four emblems at the rows' size and at the summary's, made on a worker; once they are there, the summary and the rows that wait show them.</summary>
    private async Task MakeEmblemsAsync()
    {
        Func<TrophyGrade, AchievementIcon> source = EmblemSource;
        (Bitmap Tile, Bitmap Badge)[] made = await Task.Run(() => MakeEmblems(source));
        for (int grade = 0; grade < made.Length; grade++) { (_emblemTiles[grade], _emblemBadges[grade]) = made[grade]; }
        ShowAchievements();
    }

    private static (Bitmap Tile, Bitmap Badge)[] MakeEmblems(Func<TrophyGrade, AchievementIcon> source)
    {
        var made = new (Bitmap Tile, Bitmap Badge)[4];
        for (int grade = 0; grade < made.Length; grade++)
        {
            AchievementIcon emblem = source((TrophyGrade)grade);
            made[grade] = (UnixLauncherArt.TileBitmap(emblem, TilePixels), UnixLauncherArt.TileBitmap(emblem, EmblemPixels));
        }

        return made;
    }

    /// <summary>
    /// What a row shows at its left. A locked achievement: the padlock tile of its grade, never a game picture. An earned one: its icon; until that has
    /// arrived, and while the game folder is still being looked at, the blank tile of its grade (not the emblem, which would only flash by); the emblem of its
    /// grade when there is no game folder or the picture is not in it (the blank tile until the emblems are made).
    /// </summary>
    private Bitmap? Tile(AchievementRow row)
    {
        if (!_tilesShown) { return null; }
        int grade = GradeSlot(row.Grade);
        if (!row.Earned) { return _lockedTiles[grade] ??= UnixLauncherArt.TileBitmap(Achievements.LockedIcon((TrophyGrade)grade), TilePixels); }
        if ((uint)row.Id >= (uint)_icons.Length || (_iconFolderKnown && _iconFolder is null)) { return EmblemTile(grade); }
        return _iconStates[row.Id] switch
        {
            IconState.Made => _icons[row.Id],
            IconState.None => EmblemTile(grade),
            _ => BlankTile(grade),
        };
    }

    // the emblem of a grade at a row's size, and the blank tile of the grade until the emblems are made
    private Bitmap EmblemTile(int grade) => _emblemTiles[grade] ?? BlankTile(grade);

    private Bitmap BlankTile(int grade) => _blankTiles[grade] ??= UnixLauncherArt.TileBitmap(Achievements.BlankIcon((TrophyGrade)grade), TilePixels);

    // the emblem of a grade at the summary's size; null until the emblems are made
    private Bitmap? GradeEmblem(TrophyGrade grade) => _emblemBadges[GradeSlot(grade)];

    private int GradeSlot(TrophyGrade grade) => Math.Clamp((int)grade, 0, _lockedTiles.Length - 1);

    /// <summary>
    /// Asks for the icons of the earned achievements nobody asked for yet: when the page is first shown, when the game earns one while the launcher is open, and
    /// after the icons were forgotten. A locked achievement is never asked for: its picture would give away what is to come.
    /// </summary>
    private void RequestIcons()
    {
        if (!_tilesShown || _iconFolder is not { } folder) { return; }
        foreach (AchievementRow row in _achievements.Rows)
        {
            if (!row.Earned || (uint)row.Id >= (uint)_iconStates.Length || _iconStates[row.Id] != IconState.NotAsked) { continue; }
            _iconStates[row.Id] = IconState.Asked;
            _iconQueue.Enqueue((row.Id, folder, _iconRound));
        }

        if (_iconQueue.Count > 0 && !_iconsBeingMade) { _iconWork = MakeIconsAsync(); }
    }

    /// <summary>
    /// The worker's loop: one icon after the other is cut from the game's pictures and made a bitmap on a worker thread (never two at once: each decodes a large
    /// picture), and each row gets its icon as soon as it is there. What was asked for before the icons were forgotten is dropped.
    /// </summary>
    private async Task MakeIconsAsync()
    {
        // said before the first icon is awaited: a row that asks while this loop runs joins its queue and starts no second one
        _iconsBeingMade = true;
        try
        {
            while (_iconQueue.TryDequeue(out (int Id, string Folder, int Round) next))
            {
                if (next.Round != _iconRound) { continue; }
                Func<string, int, AchievementIcon?> source = IconSource;
                Bitmap? icon = await Task.Run(() => MakeIcon(source, next.Folder, next.Id));
                if (next.Round != _iconRound)
                {
                    // forgotten meanwhile (Check again, another game folder): this picture is nobody's
                    icon?.Dispose();
                    continue;
                }

                (_icons[next.Id], _iconStates[next.Id]) = (icon, icon is null ? IconState.None : IconState.Made);
                ShowAchievementRows();
            }
        }
        finally
        {
            _iconsBeingMade = false;
        }
    }

    private static Bitmap? MakeIcon(Func<string, int, AchievementIcon?> source, string folder, int id)
    {
        try
        {
            return source(folder, id) is { } icon ? UnixLauncherArt.TileBitmap(icon, TilePixels) : null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            // not a picture a bitmap can be made of: the row shows the emblem of its grade
            return null;
        }
    }

    /// <summary>
    /// Forgets the icons: they belonged to another game folder, or the folder is read again. The rows go back to their blank tiles (or to the emblems, without
    /// a game folder) before the bitmaps are released, and the icons of <paramref name="folder"/> are asked for anew.
    /// </summary>
    private void ForgetIcons(string? folder)
    {
        _iconRound++;
        _iconQueue.Clear();
        Bitmap?[] old = [.. _icons];
        Array.Clear(_icons);
        Array.Clear(_iconStates);
        (_iconFolderKnown, _iconFolder) = (true, folder);
        // the rows let go of the pictures first: nothing shown may still hold a released one
        ShowAchievementRows();
        foreach (Bitmap? icon in old) { icon?.Dispose(); }
    }

    private void ShowAchievements()
    {
        AchievementGrades =
        [
            .. new[] { TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze }
                .Select(grade => new GradeTally(grade, GradeEmblem(grade), LauncherTexts.AchievementGrade(grade), _achievements.EarnedOf(grade), _achievements.TotalOf(grade))),
        ];
        Raise(nameof(AchievementsPercent), nameof(AchievementsCount), nameof(AchievementsEarned), nameof(AchievementsTotal), nameof(AchievementGrades),
            nameof(AchievementsAllTab), nameof(AchievementsEarnedTab), nameof(AchievementsLockedTab), nameof(AchievementsDamaged), nameof(AchievementsNoGame));
        ShowAchievementRows();
    }

    /// <summary>Brings <see cref="AchievementRows"/> to the chosen tab's rows. Both are ordered by id, so one pass replaces, inserts and removes in place.</summary>
    private void ShowAchievementRows()
    {
        int at = 0;
        foreach (AchievementRow row in _achievements.Rows)
        {
            if (_achievementTab == 1 ? !row.Earned : _achievementTab == 2 && row.Earned) { continue; }
            var item = new AchievementItem(row, Tile(row));
            while (at < AchievementRows.Count && AchievementRows[at].Row.Id < row.Id) { AchievementRows.RemoveAt(at); }
            if (at < AchievementRows.Count && AchievementRows[at].Row.Id == row.Id)
            {
                if (!AchievementRows[at].Equals(item)) { AchievementRows[at] = item; }
            }
            else
            {
                AchievementRows.Insert(at, item);
            }

            at++;
        }

        while (AchievementRows.Count > at) { AchievementRows.RemoveAt(AchievementRows.Count - 1); }
        Raise(nameof(AchievementsListEmpty), nameof(AchievementsEmptyText));
        RequestIcons();
    }

    /// <summary>What the folder check said of the game folder, as far as the page's reading of it goes: when it changes, that reading may be out of date.</summary>
    private readonly record struct FolderLook(string? Path, FolderState State, int ChecksPassed, long TotalBytes);

    /// <summary>Whether a file is there, its size and its last-write time: one lookup, nothing is opened.</summary>
    private readonly record struct FileLook(bool Exists, long Length, DateTime WriteUtc)
    {
        public static FileLook Of(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new FileLook(true, info.Length, info.LastWriteTimeUtc) : default;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                return default;
            }
        }
    }
}
