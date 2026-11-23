using Tsuki.App.Play;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;
using Tsuki.Engine.Trophies;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// v0.1.9: achievements in the running game, over the hand-made game. The services earn by the PS4's rules (the <c>_RDED</c> of a chapter's last scene outside a
/// replay, <c>_TRPY</c>, the last lesson, a lesson started from the ending list), record at once in trophies.ini beside the save and catch up at start with what the
/// save's progress already holds; the runtime shows the notice while the Config switch is on (it is off until the player turns it on: the rigs here turn it on
/// unless a test says otherwise), holds the catch-up back until the title's menu is up and then shows one short notice per achievement, and keeps the notice out
/// of save thumbnails. For each notice it has that achievement's icon made on a worker, once, after the earn was recorded.
/// </summary>
public sealed class TrophyAppTests : IDisposable
{
    // the PS4's table: the flowchart node whose _RDED earns trophy 1..27
    private static readonly int[] Scenes = [335, 15, 357, 332, 492, 107, 118, 141, 154, 172, 443, 445, 349, 14, 391, 397, 405, 414, 422, 431, 327, 274, 456, 463, 480, 300, 309];

    private const string Page = "_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);";

    // node 335 (trophy 1) is the node being played and is completed before the first page
    private const string ChapterEnd = "_WKST(F822,eq,1);_WKST(F110,eq,335);_RTST(335);_RDED(335);" + Page + Page + "_END_();";

    // the same scene as a gallery replay runs it: F024 set (the ARCHIVE scene's flag); _RDED then ends the replay
    private const string ChapterEndInAReplay = "_WKST(F822,eq,1);_WKST(F24,eq,1);_RTST(335);_RDED(335);" + Page + "_END_();";

    private const string FirstLesson = "_WKST(F822,eq,1);_TRPY(28);" + Page + Page + "_END_();";

    private const string TrophyCallsInAReplay = "_WKST(F822,eq,1);_WKST(F24,eq,1);_TRPY(28);_TRPY(5);" + Page + "_TRPY(29);" + Page + "_END_();";

    // lesson 25 (node 557): _RTST of a lesson sets F110 itself
    private const string LastLesson = "_WKST(F822,eq,1);_RTST(557);_RDED(557);" + Page + Page + "_END_();";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-v019-trophy-" + Guid.NewGuid().ToString("N"));

    public TrophyAppTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string SavePath => Path.Combine(_dir, "save.dat");

    private string TrophyFile => Path.Combine(_dir, TrophyLog.FileName);

    /// <summary>
    /// v0.1.9 FIXES D10 Q-1: the log a player is sent to (Help &amp; about &gt; Open the log) says "message" for what appears and "Notify" for the switch, as
    /// the Config rows and the launchers do; only the settings.ini keys trophy_notice and screenshot_notice keep the old word.
    /// </summary>
    private static void SaysMessageNotNotice(List<string> log) =>
        Assert.DoesNotContain(log, l => l.Replace("trophy_notice", "", StringComparison.Ordinal).Replace("screenshot_notice", "", StringComparison.Ordinal).Contains("notice", StringComparison.OrdinalIgnoreCase));

    private static void SetDone(SaveStore store, int node) =>
        store.SystemData.Block.SetG(SystemData.SceneDoneWord + (node >> 4), (ushort)(store.SystemData.Block.GetG(SystemData.SceneDoneWord + (node >> 4)) | (1 << (node & 15))));

    private static void SetSeen(SaveStore store, int node) =>
        store.SystemData.Block.SetG(SystemData.SceneSeenWord + (node >> 4), (ushort)(store.SystemData.Block.GetG(SystemData.SceneSeenWord + (node >> 4)) | (1 << (node & 15))));

    /// <summary>The services with the runtime and a UI over them, stepped like the app steps them.</summary>
    private sealed class Rt
    {
        private long _n;

        /// <param name="rig">The services.</param>
        /// <param name="pictureRun">A run that writes pictures on its own schedule.</param>
        /// <param name="notice">The Achievement Notify switch the player set (v0.1.9: off unless the player turns it on); null: a game without the port's switches.
        /// A rig whose services have their switches already keeps them.</param>
        /// <param name="sound">The Achievement Sound switch.</param>
        public Rt(AppRig rig, bool pictureRun = false, bool? notice = true, bool sound = false)
        {
            Rig = rig;
            if (notice is { } on)
            {
                rig.Services.Port ??= new PreviewPortSettings(new PreviewServices(Language.En)) { TrophyNotice = on, TrophySound = sound };
            }

            Ui = new UiRoot(UiTestKit.Data(), rig.Services);
            Runtime = new AppRuntime(rig.Services, Ui, null, null) { PictureRun = pictureRun };
            Runtime.Logged += Log.Add;
            rig.Services.Logged += Log.Add;
            rig.Services.TrophyEarned += Earned.Add;
        }

        public AppRig Rig { get; }

        public UiRoot Ui { get; }

        public AppRuntime Runtime { get; }

        public List<string> Log { get; } = [];

        public List<int> Earned { get; } = [];

        public void Step(bool click = false, ShellActions shell = ShellActions.None) => StepAndDraw(click, shell);

        /// <summary>One step with <paramref name="buttons"/> newly pressed (a key the title takes, such as Down).</summary>
        public void Press(PadButtons buttons)
        {
            var list = new DisplayList(2048);
            Runtime.Step(new InputFrame { Frame = ++_n, Held = buttons, Pressed = buttons, Repeated = buttons }, _n * 16_666_667L, list);
        }

        /// <summary>One step with the pointer at canvas pixel (<paramref name="x"/>, <paramref name="y"/>), no button.</summary>
        public void PointAt(float x, float y)
        {
            var list = new DisplayList(2048);
            Runtime.Step(UiInput.PointerAt(x, y, frame: ++_n), _n * 16_666_667L, list);
        }

        /// <summary>One step in which the left button goes down at canvas pixel (<paramref name="x"/>, <paramref name="y"/>), then one in which it is up again.</summary>
        public void ClickAt(float x, float y)
        {
            var list = new DisplayList(2048);
            Runtime.Step(UiInput.PointerAt(x, y, click: true, frame: ++_n), _n * 16_666_667L, list);
            PointAt(x, y);
        }

        /// <summary>One step, as the app takes it; the commands of the frame it drew.</summary>
        public DrawCmd[] StepAndDraw(bool click = false, ShellActions shell = ShellActions.None)
        {
            PadButtons b = click ? PadButtons.Decide : PadButtons.None;
            var list = new DisplayList(2048);
            Runtime.Step(new InputFrame { Frame = ++_n, Held = b, Pressed = b, Repeated = b, ShellPressed = shell }, _n * 16_666_667L, list);
            return list.Commands.ToArray();
        }

        public void Run(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                Step();
            }
        }

        /// <summary>Starts scene 100 and runs until its first page is on show.</summary>
        public void Play()
        {
            Rig.Services.StartScene(100);
            for (int i = 0; i < 400 && Rig.Text != "alpha line"; i++)
            {
                Step();
            }

            Assert.Equal("alpha line", Rig.Text);
        }
    }

    [Fact]
    public void TheRdedOfAChapterEndEarnsItsTrophyShowsTheNoticeAndWritesTheFileAtOnceWithTheUtcTime()
    {
        var clock = new DateTime(2026, 11, 12, 13, 3, 11, DateTimeKind.Utc);
        var rt = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: Scenes, now: () => clock));
        Assert.Empty(rt.Rig.Services.TrophiesCaughtUp);
        Assert.False(File.Exists(TrophyFile));                               // nothing earned, nothing written
        rt.Play();

        Assert.Equal([1], rt.Earned);
        Assert.True(rt.Rig.Services.Trophies.IsEarned(1));
        Assert.Equal(clock, rt.Rig.Services.Trophies.EarnedUtc(1));
        Assert.Contains("trophy 1 earned", rt.Log);

        // the notice: bronze, the port's heading, the chapter's own title (the stand-in tables answer "text N" for ui_text 358 + 3)
        UiTrophyNotice notice = Assert.IsType<UiTrophyNotice>(rt.Ui.TrophyNotice);
        Assert.Equal((1, TrophyGrade.Bronze, "Achievement unlocked", "text 361"), (notice.Id, notice.Grade, notice.Heading, notice.Name));
        Assert.Equal(0, rt.Ui.TrophyNoticesWaiting);

        // the file beside the save file, written at the moment of earning (the story is still on its first page; no save was made)
        Assert.Equal(TrophyFile, rt.Rig.Services.TrophyPath);
        Assert.Contains("1=2026-11-12T13:03:11Z\n", File.ReadAllText(TrophyFile), StringComparison.Ordinal);
        TrophyLog onDisk = TrophyLog.Read(TrophyFile, out bool damaged);
        Assert.False(damaged);
        Assert.Equal([1], onDisk.Ids);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // the event fires again when the same _RDED runs again (a load, a rewind, a flowchart jump): nothing new
        rt.Rig.Services.StartScene(100);
        rt.Run(60);
        Assert.Equal([1], rt.Earned);
        Assert.Equal(1, rt.Log.Count(l => l.StartsWith("trophy 1 earned", StringComparison.Ordinal)));

        // and the next start reads the record back: no catch-up, no second award
        var again = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: Scenes));
        Assert.True(again.Rig.Services.Trophies.IsEarned(1));
        Assert.Equal(clock, again.Rig.Services.Trophies.EarnedUtc(1));
        Assert.Empty(again.Rig.Services.TrophiesCaughtUp);
        again.Play();
        Assert.Empty(again.Earned);
        Assert.Null(again.Ui.TrophyNotice);
    }

    [Fact]
    public void AChapterEndInsideAReplayEarnsNothing()
    {
        var rt = new Rt(new AppRig(ChapterEndInAReplay, savePath: SavePath, trophyScenes: Scenes));
        rt.Rig.Services.StartScene(100);
        rt.Run(120);
        Assert.True(rt.Rig.Host.IsSceneDone(335));                           // the node was completed (the replay ended there)
        Assert.Empty(rt.Earned);
        Assert.Equal(0, rt.Rig.Services.Trophies.Count);
        Assert.Null(rt.Ui.TrophyNotice);
        Assert.False(File.Exists(TrophyFile));
        Assert.DoesNotContain(rt.Log, l => l.StartsWith("trophy ", StringComparison.Ordinal));
    }

    [Fact]
    public void ANodeThatIsNotInTheTableEarnsNothing()
    {
        // the same script against a table whose first record is another node
        var rt = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: [336, .. Scenes.Skip(1)]));
        rt.Play();
        Assert.True(rt.Rig.Host.IsSceneDone(335));
        Assert.Empty(rt.Earned);
        Assert.Null(rt.Ui.TrophyNotice);
        Assert.DoesNotContain(rt.Log, l => l.Contains("trophy scene table", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(26)]
    [InlineData(28)]
    public void ATrophySceneTableThatHasNot27RecordsIsNotUsedAndTheLogSaysSoOnce(int records)
    {
        // An eboot that is not a known build, whose table was found with another length: record i may not be trophy i + 1 there, so the end of a chapter
        // earns nothing, in play or from the save's progress at start, and the log says why in one line. The lesson achievements do not need the table.
        int[] table = records < Scenes.Length ? Scenes[..records] : [.. Scenes, .. Enumerable.Range(600, records - Scenes.Length)];
        SaveStore store = ProfileWithProgress(2, 3);
        SetSeen(store, 535);
        var rt = new Rt(new AppRig(ChapterEnd, savePath: SavePath, store: store, trophyScenes: table));
        Assert.Equal([28], rt.Rig.Services.TrophiesCaughtUp);                 // chapters 2 and 3 are done in the save: not taken from this table
        rt.Play();
        Assert.True(rt.Rig.Host.IsSceneDone(335));                            // chapter 1's last scene was completed: nothing earned
        Assert.Empty(rt.Earned);
        Assert.Equal([28], rt.Rig.Services.Trophies.Ids);
        Assert.Equal([28], TrophyLog.Read(TrophyFile, out _).Ids);
        rt.Run(120);
        string line = Assert.Single(rt.Log, l => l.Contains("trophy scene table", StringComparison.Ordinal));
        Assert.Equal($"achievements: this game's trophy scene table has {records} records, not 27: it is not used, so the end of a chapter earns no achievement here (the two lesson achievements still work)", line);
        SaysMessageNotNotice(rt.Log);
    }

    [Fact]
    public void TheScriptsTrophyCallEarnsTheFirstLessonAndIsIgnoredInAReplayExceptForTwentyNine()
    {
        var rt = new Rt(new AppRig(FirstLesson, savePath: SavePath, trophyScenes: Scenes));
        rt.Play();
        Assert.Equal([28], rt.Earned);
        UiTrophyNotice notice = Assert.IsType<UiTrophyNotice>(rt.Ui.TrophyNotice);
        Assert.Equal((28, TrophyGrade.Silver, "First Lesson"), (notice.Id, notice.Grade, notice.Name));
        Assert.Contains("28=", File.ReadAllText(TrophyFile), StringComparison.Ordinal);

        // under F024 (a replay) _TRPY(28) and _TRPY(5) are skipped; only 29 passes, as on the PS4
        string other = Path.Combine(_dir, "replay");
        Directory.CreateDirectory(other);
        var replay = new Rt(new AppRig(TrophyCallsInAReplay, savePath: Path.Combine(other, "save.dat"), trophyScenes: Scenes));
        replay.Play();
        Assert.True(replay.Rig.Host.ArchiveMode);
        Assert.Empty(replay.Earned);
        Assert.Null(replay.Ui.TrophyNotice);
        Assert.False(File.Exists(Path.Combine(other, TrophyLog.FileName)));
        for (int i = 0; i < 200 && replay.Earned.Count == 0; i++)
        {
            replay.Step(click: i % 3 == 0);
        }

        Assert.Equal([29], replay.Earned);
    }

    [Fact]
    public void ALessonStartedFromTheEndingListEarnsTheFirstLessonBeforeAnyScriptRuns()
    {
        // 11 scripts: the rig has no ARCHIVE scene, so a replay starts scene 110 (AppRig)
        var rt = new Rt(new AppRig(AppRig.ThreePages, savePath: SavePath, copies: 11, trophyScenes: Scenes));
        rt.Rig.Services.ReplayScene(999, 0);                                  // not a lesson
        Assert.Empty(rt.Earned);
        rt.Rig.Services.ReplayScene(1024, 0);                                 // the last lesson's script scene
        Assert.Equal([28], rt.Earned);
        Assert.Contains("trophy 28 earned", rt.Log);
        Assert.NotNull(rt.Ui.TrophyNotice);
        rt.Rig.Services.ReplayScene(1000, 0);
        Assert.Equal([28], rt.Earned);                                       // once
    }

    [Fact]
    public void TheLastLessonEarnsTwentyNineAndThenThePlatinumWhenTheRestIsThere()
    {
        // a profile that holds everything but the last lesson: the 27 chapter scenes done, lessons 1..24 done and seen
        SaveStore store = AppRig.NewStore();
        foreach (int node in Scenes)
        {
            SetDone(store, node);
        }

        for (int node = 533; node <= 556; node++)
        {
            SetDone(store, node);
            SetSeen(store, node);
        }

        var clock = new DateTime(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc);
        var rt = new Rt(new AppRig(LastLesson, savePath: SavePath, store: store, trophyScenes: Scenes, now: () => clock));
        Assert.Equal([.. Enumerable.Range(1, 28)], rt.Rig.Services.TrophiesCaughtUp);
        Assert.False(rt.Rig.Services.Trophies.IsEarned(0));
        rt.Play();

        // 29 first, the platinum after it: two events, two notices in that order, both with the time
        Assert.Equal([29, 0], rt.Earned);
        UiTrophyNotice head = Assert.IsType<UiTrophyNotice>(rt.Ui.TrophyNotice);
        Assert.Equal((29, TrophyGrade.Gold, "Every Lesson"), (head.Id, head.Grade, head.Name));
        Assert.Equal(1, rt.Ui.TrophyNoticesWaiting);
        rt.Run(UiTrophyNotice.FadeInFrames + UiTrophyNotice.QueuedHoldFrames + UiTrophyNotice.FadeOutFrames + 2);
        UiTrophyNotice next = Assert.IsType<UiTrophyNotice>(rt.Ui.TrophyNotice);
        Assert.Equal((0, TrophyGrade.Platinum, "All Achievements"), (next.Id, next.Grade, next.Name));
        Assert.Equal(0, rt.Ui.TrophyNoticesWaiting);
        int first = rt.Log.IndexOf("trophy 29 earned");
        Assert.True(first >= 0 && rt.Log.IndexOf("trophy 0 earned") > first);

        TrophyLog onDisk = TrophyLog.Read(TrophyFile, out _);
        Assert.Equal(30, onDisk.Count);
        Assert.Equal(clock, onDisk.EarnedUtc(29));
        Assert.Equal(clock, onDisk.EarnedUtc(0));
        Assert.Null(onDisk.EarnedUtc(1));                                     // what the save's progress had earned carries no date
        Assert.Null(onDisk.EarnedUtc(28));
        string text = File.ReadAllText(TrophyFile);
        Assert.Contains("1=earlier\n", text, StringComparison.Ordinal);
        Assert.Contains("29=2026-11-12T20:00:00Z\n", text, StringComparison.Ordinal);
        Assert.Contains("0=2026-11-12T20:00:00Z\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALessonThatIsNotTheLastMissingOneEarnsNothing()
    {
        SaveStore store = AppRig.NewStore();
        for (int node = 533; node <= 555; node++)                             // lesson 24 (node 556) is still open
        {
            SetDone(store, node);
        }

        var rt = new Rt(new AppRig(LastLesson, savePath: SavePath, store: store, trophyScenes: Scenes));
        rt.Play();
        Assert.True(rt.Rig.Host.IsSceneDone(557));
        Assert.Empty(rt.Earned);
        Assert.False(rt.Rig.Services.Trophies.IsEarned(29));
    }

    [Fact]
    public void WithTheNoticeOffTheTrophyIsRecordedAndLoggedButNothingIsShown()
    {
        var rt = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: Scenes));
        rt.Rig.Services.Port = new PreviewPortSettings(new PreviewServices(Language.En)) { TrophyNotice = false };
        rt.Play();
        Assert.Equal([1], rt.Earned);
        Assert.Contains("trophy 1 earned (no message: Notify is off)", rt.Log);
        Assert.Null(rt.Ui.TrophyNotice);
        Assert.Equal(0, rt.Ui.TrophyNoticesWaiting);
        Assert.Equal([1], TrophyLog.Read(TrophyFile, out _).Ids);            // recorded all the same

        // switched on again: the next one shows
        rt.Rig.Services.Port.TrophyNotice = true;
        rt.Runtime.ShowTrophy(7);
        Assert.Equal(7, rt.Ui.TrophyNotice!.Id);
        Assert.Contains("trophy 7 earned", rt.Log);
        SaysMessageNotNotice(rt.Log);
    }

    [Fact]
    public void ARunThatWritesPicturesOnItsOwnScheduleNeverShowsTheNotice()
    {
        var rt = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: Scenes), pictureRun: true);
        rt.Play();
        Assert.Equal([1], rt.Earned);
        Assert.Contains("trophy 1 earned (no message: this run writes pictures)", rt.Log);
        Assert.Null(rt.Ui.TrophyNotice);
        Assert.Equal([1], TrophyLog.Read(TrophyFile, out _).Ids);
        rt.Runtime.ShowTrophiesFound([1, 2, 3]);
        Assert.Contains("trophies from the save's progress: 3 (no message: this run writes pictures)", rt.Log);
        SaysMessageNotNotice(rt.Log);

        // the frames of such a run are the frames of a run that earns nothing: the same steps over a record that already holds achievement 1 draw the same
        // lists, frame for frame (a notice in one of them would add its box, its picture and its words)
        var clock = new DateTime(2026, 11, 12, 13, 3, 11, DateTimeKind.Utc);
        DrawCmd[][] Frames(string name, bool holding, out List<int> earned)
        {
            string dir = Path.Combine(_dir, name);
            Directory.CreateDirectory(dir);
            if (holding)
            {
                var record = new TrophyLog();
                record.Add(1, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
                record.Write(Path.Combine(dir, TrophyLog.FileName));
            }

            var run = new Rt(new AppRig(ChapterEnd, savePath: Path.Combine(dir, "save.dat"), trophyScenes: Scenes, now: () => clock), pictureRun: true);
            run.Rig.Services.StartScene(100);
            var frames = new List<DrawCmd[]>();
            for (int i = 0; i < UiTrophyNotice.FadeInFrames + 120; i++)
            {
                frames.Add(run.StepAndDraw(click: i % 40 == 39));
            }

            earned = run.Earned;
            return [.. frames];
        }

        DrawCmd[][] earning = Frames("earning", holding: false, out List<int> earnedThere);
        DrawCmd[][] already = Frames("holding", holding: true, out List<int> earnedHere);
        Assert.Equal([1], earnedThere);
        Assert.Empty(earnedHere);
        Assert.Contains(earning, f => f.Length > 0);
        for (int i = 0; i < earning.Length; i++)
        {
            Assert.True(earning[i].AsSpan().SequenceEqual(already[i]), $"frame {i}: {earning[i].Length} commands in the run that earned, {already[i].Length} in the run that already had the achievement");
        }
    }

    [Fact]
    public void WithoutASaveFileNothingIsReadOrWritten()
    {
        string before = string.Join("|", Directory.GetFiles(Directory.GetCurrentDirectory(), "trophies*"));
        var rt = new Rt(new AppRig(ChapterEnd, savePath: null, trophyScenes: Scenes));
        Assert.Null(rt.Rig.Services.TrophyPath);
        rt.Play();
        Assert.Equal([1], rt.Earned);                                        // the run keeps its achievements in memory
        Assert.True(rt.Rig.Services.Trophies.IsEarned(1));
        Assert.NotNull(rt.Ui.TrophyNotice);
        Assert.True(rt.Rig.Services.Commit());
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Equal(before, string.Join("|", Directory.GetFiles(Directory.GetCurrentDirectory(), "trophies*")));
        Assert.DoesNotContain(rt.Log, l => l.Contains("achievements file", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileThatCannotBeWrittenIsLoggedAndWrittenWithTheNextSave()
    {
        // a held file keeps readers out on Windows only: elsewhere the read fails but the rename over it goes through (the next test covers every system)
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // another program holds trophies.ini and shares nothing: it can be neither read nor replaced. It holds trophy 9 with its date
        File.WriteAllText(TrophyFile, "# held\nversion=1\n9=2026-01-02T03:04:05Z\n");
        long ms = 0;
        Rt rt;
        using (new FileStream(TrophyFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            rt = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: Scenes, milliseconds: () => ms));
            Assert.True(rt.Rig.Services.TrophyFileDamaged);
            Assert.Equal(TrophyFileState.Unreadable, rt.Rig.Services.TrophyFileAtStart);
            rt.Play();
            Assert.Equal([1], rt.Earned);                                    // earned and shown all the same
            Assert.NotNull(rt.Ui.TrophyNotice);
            Assert.Contains(rt.Log, l => l.StartsWith("achievements file could not be read", StringComparison.Ordinal));
            Assert.Equal(1, rt.Log.Count(l => l.StartsWith("achievements file not written:", StringComparison.Ordinal)));

            // still held at the next saves: tried again once five seconds have passed, said once
            rt.Rig.Services.Commit();
            ms += AppServices.TrophyRetryMilliseconds;
            rt.Rig.Services.Commit();
            Assert.Equal(1, rt.Log.Count(l => l.StartsWith("achievements file not written:", StringComparison.Ordinal)));
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        }

        // released: the next save (a scene start, an auto save, the quit) writes the record, and what the file held is kept with its date
        ms += AppServices.TrophyRetryMilliseconds;
        Assert.True(rt.Rig.Services.Commit());
        TrophyLog onDisk = TrophyLog.Read(TrophyFile, out bool damaged);
        Assert.False(damaged);
        Assert.Equal([1, 9], onDisk.Ids);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), onDisk.EarnedUtc(9));
        Assert.NotNull(onDisk.EarnedUtc(1));
        Assert.True(rt.Rig.Services.Trophies.IsEarned(9));
        Assert.Contains(rt.Log, l => l.StartsWith("achievements file written after an earlier failure", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void AFolderInTheRecordsPlaceIsSaidOnceAndTheRecordIsWrittenOnceItIsGone()
    {
        // trophies.ini is a folder: it can be neither read nor written over, on every system, and the words of the failure name no temporary file
        Directory.CreateDirectory(TrophyFile);
        long ms = 0;
        var rt = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: Scenes, milliseconds: () => ms));
        Assert.True(rt.Rig.Services.TrophyFileDamaged);
        rt.Play();
        Assert.Equal([1], rt.Earned);                                        // earned and shown all the same
        Assert.NotNull(rt.Ui.TrophyNotice);
        Assert.Equal(1, rt.Log.Count(l => l.StartsWith("achievements file not written:", StringComparison.Ordinal)));
        Assert.True(rt.Rig.Services.TrophyWriteDue);

        // the next saves: tried again once five seconds have passed, said once
        rt.Rig.Services.Commit();
        ms += AppServices.TrophyRetryMilliseconds;
        rt.Rig.Services.Commit();
        Assert.Equal(1, rt.Log.Count(l => l.StartsWith("achievements file not written:", StringComparison.Ordinal)));

        // the folder gone: the next save writes the record
        Directory.Delete(TrophyFile);
        ms += AppServices.TrophyRetryMilliseconds;
        Assert.True(rt.Rig.Services.Commit());
        Assert.False(rt.Rig.Services.TrophyWriteDue);
        TrophyLog onDisk = TrophyLog.Read(TrophyFile, out bool damaged);
        Assert.False(damaged);
        Assert.Equal([1], onDisk.Ids);
        Assert.Contains(rt.Log, l => l.StartsWith("achievements file written after an earlier failure", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    // how long a holder below may hold trophies.ini at most, whatever happens: tens of seconds, far beyond a start on a loaded machine
    private static readonly TimeSpan HolderBound = TimeSpan.FromSeconds(60);

    // a program that holds trophies.ini without sharing it (Windows only: elsewhere it keeps no reader out) until it lets go
    private static Thread Hold(string path, ManualResetEventSlim held, Func<bool> letGo)
    {
        var thread = new Thread(() =>
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                held.Set();
                while (!letGo())
                {
                    Thread.Sleep(1);
                }
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(30)), "the holder never took the file");
        return thread;
    }

    [Fact]
    public void ARecordThatCouldNotBeReadAtStartIsNeverWrittenOverEvenWhenItsHolderLetsGoDuringTheWrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // the record holds trophy 5 with its date; another program holds it from before the start until the start has returned, and lets go the moment a
        // write's temporary file appears beside it. The save implies trophy 1, so the start has something to write. The holder waits for the start, not for a
        // fixed time: on a loaded machine the start took longer than the 0.6 s once allowed, the holder let go after the start's first reading, and its second
        // one read the record and wrote the two together, which is right but not this case. The bound only keeps a start that never returns from holding the
        // file for ever
        const string Held = "# held\nversion=1\n5=2026-01-02T03:04:05Z\n";
        File.WriteAllText(TrophyFile, Held);
        using var held = new ManualResetEventSlim(false);
        bool sawTemporary = false;
        bool startReturned = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Thread holder = Hold(
            TrophyFile,
            held,
            () => (sawTemporary = Directory.GetFiles(_dir, TrophyLog.FileName + ".*.tmp").Length > 0) || Volatile.Read(ref startReturned) || watch.Elapsed > HolderBound);
        long ms = 0;
        Rt rt;
        try
        {
            rt = new Rt(new AppRig(AppRig.ThreePages, savePath: SavePath, store: ProfileWithProgress(1), trophyScenes: Scenes, milliseconds: () => ms));
        }
        finally
        {
            Volatile.Write(ref startReturned, true);
        }

        Assert.True(holder.Join(HolderBound), "the holder never let go");
        Assert.False(sawTemporary, "a write was begun over a file that had not been read");
        Assert.Equal(TrophyFileState.Unreadable, rt.Rig.Services.TrophyFileAtStart);
        Assert.Equal([1], rt.Rig.Services.TrophiesCaughtUp);
        Assert.True(rt.Rig.Services.TrophyWriteDue);
        Assert.Equal(Held, File.ReadAllText(TrophyFile));

        // let go: the next save five seconds on reads it and writes the two together, the old line with its date
        ms += AppServices.TrophyRetryMilliseconds;
        Assert.True(rt.Rig.Services.Commit());
        TrophyLog onDisk = TrophyLog.Read(TrophyFile, out bool damaged);
        Assert.False(damaged);
        Assert.Equal([1, 5], onDisk.Ids);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), onDisk.EarnedUtc(5));
        Assert.Null(onDisk.EarnedUtc(1));
        Assert.False(rt.Rig.Services.TrophyWriteDue);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecordStillHeldWhenTheStartWouldWriteItGetsNoCatchUpAndWhatTheSaveHoldsIsWrittenOnceItCanBe(bool heldAtStart)
    {
        // the record holds achievement 1 from an earlier run; the save's progress implies 1 and 2. Held: another program (a sync tool, a virus scan) holds the
        // file without sharing it through the start and the title, so neither the start's reading nor the second one before the write can tell what it holds:
        // 1 and 2 are recorded, silently (the catch-up could replay achievements earned long ago), and written with what the file holds once it can be read.
        // Free: the normal catch-up, of 2 alone
        const string Record = "# held\nversion=1\n1=2026-01-02T03:04:05Z\n";
        File.WriteAllText(TrophyFile, Record);
        long ms = 0;
        var found = new List<IReadOnlyList<int>>();
        Rt rt;
        using (heldAtStart ? new FileStream(TrophyFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
        {
            rt = new Rt(new AppRig(AppRig.ThreePages, savePath: SavePath, store: ProfileWithProgress(1, 2), trophyScenes: Scenes, milliseconds: () => ms));
            rt.Rig.Services.TrophiesFoundInSave += found.Add;
            if (!heldAtStart)
            {
                Assert.Equal(TrophyFileState.Read, rt.Rig.Services.TrophyFileAtStart);
                Assert.Equal([2], rt.Rig.Services.TrophiesCaughtUp);
                Assert.False(rt.Rig.Services.TrophyWriteDue);
                ToTheTitlesMenu(rt);
                Assert.Equal([2], Assert.Single(found));
                Assert.Contains("achievements: 1 already earned by the save's progress, recorded without a date (2)", rt.Log);
                Assert.DoesNotContain(rt.Log, l => l.StartsWith("achievements: no catch-up messages", StringComparison.Ordinal));
                UiTrophyNotice shown = StartOfTheCatchUp(rt);
                Assert.Equal((2, true), (shown.Id, shown.CatchUp));
                Assert.Equal([1, 2], TrophyLog.Read(TrophyFile, out _).Ids);
                Assert.Empty(rt.Earned);
                return;
            }

            // a hold keeps readers out on Windows; elsewhere .NET's advisory lock does unless file locking is turned off for it
            if (!OperatingSystem.IsWindows() && rt.Rig.Services.TrophyFileAtStart != TrophyFileState.Unreadable)
            {
                return;
            }

            Assert.Equal(TrophyFileState.Unreadable, rt.Rig.Services.TrophyFileAtStart);
            Assert.Equal([1, 2], rt.Rig.Services.TrophiesCaughtUp);         // recorded: what the save implies, the file unread
            Assert.True(rt.Rig.Services.Trophies.IsEarned(1) && rt.Rig.Services.Trophies.IsEarned(2));
            Assert.True(rt.Rig.Services.TrophyWriteDue);
            ToTheTitlesMenu(rt);
            rt.Run(300);
            Assert.Empty(found);
            Assert.Null(rt.Ui.TrophyNotice);
            Assert.Equal(0, rt.Ui.TrophyNoticesWaiting);
            Assert.Contains("achievements: 2 already earned by the save's progress, recorded without a date (1, 2)", rt.Log);
            Assert.Contains($"achievements: no catch-up messages: the achievements file could still not be read ({TrophyFile}), so it may hold these already from earlier runs; they are recorded silently and written once it can be read", rt.Log);
            Assert.DoesNotContain(rt.Log, l => l.StartsWith("trophies from the save's progress", StringComparison.Ordinal));
            SaysMessageNotNotice(rt.Log);
        }

        // let go: nothing was written over it. The next save five seconds on reads the file and writes the two together, the old line with its date; still nothing
        // is shown
        Assert.Equal(Record, File.ReadAllText(TrophyFile));
        ms += AppServices.TrophyRetryMilliseconds;
        Assert.True(rt.Rig.Services.Commit());
        Assert.False(rt.Rig.Services.TrophyWriteDue);
        TrophyLog onDisk = TrophyLog.Read(TrophyFile, out bool damaged);
        Assert.False(damaged);
        Assert.Equal([1, 2], onDisk.Ids);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), onDisk.EarnedUtc(1));
        Assert.Null(onDisk.EarnedUtc(2));
        Assert.Contains(rt.Log, l => l.StartsWith("achievements file written after an earlier failure", StringComparison.Ordinal));
        rt.Run(300);
        Assert.Empty(found);
        Assert.Null(rt.Ui.TrophyNotice);
        Assert.Empty(rt.Earned);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void AFileThatIsNotARecordIsSetAsideUnderABrokenNameAndTheRecordTakesItsPlace()
    {
        // lines that are no record's (a note saved under the name, a copy in another encoding): the start has trophy 1 to write and sets the file aside first.
        // Such a file holds no achievement, so the catch-up of 1 is no replay: it is raised as usual
        var clock = new DateTime(2026, 11, 12, 6, 33, 48, DateTimeKind.Utc);
        byte[] note = "these are not achievements\nat all\n"u8.ToArray();
        File.WriteAllBytes(TrophyFile, note);
        var rt = new Rt(new AppRig(AppRig.ThreePages, savePath: SavePath, store: ProfileWithProgress(1), trophyScenes: Scenes, now: () => clock));
        var found = new List<IReadOnlyList<int>>();
        rt.Rig.Services.TrophiesFoundInSave += found.Add;
        Assert.Equal(TrophyFileState.NotOurs, rt.Rig.Services.TrophyFileAtStart);
        Assert.True(rt.Rig.Services.TrophyFileDamaged);
        string broken = TrophyFile + ".broken-20261112T063348Z";
        Assert.Equal(note, File.ReadAllBytes(broken));
        TrophyLog onDisk = TrophyLog.Read(TrophyFile, out bool damaged);
        Assert.False(damaged);
        Assert.Equal([1], onDisk.Ids);
        Assert.Equal([1], rt.Rig.Services.TrophiesCaughtUp);
        Assert.False(rt.Rig.Services.TrophyWriteDue);
        rt.Step();
        Assert.Equal([1], Assert.Single(found));
        Assert.DoesNotContain(rt.Log, l => l.StartsWith("achievements: no catch-up messages", StringComparison.Ordinal));
        Assert.Contains(rt.Log, l => l.StartsWith($"achievements file is not a record of achievements ({TrophyFile})", StringComparison.Ordinal));
        Assert.Contains($"achievements file was not a record of achievements: moved to {broken}", rt.Log);

        // with nothing to write at start the file stays as it is; the first earn sets it aside (a name taken already gets a number) and writes the record
        string other = Path.Combine(_dir, "later");
        Directory.CreateDirectory(other);
        string otherFile = Path.Combine(other, TrophyLog.FileName);
        File.WriteAllBytes(otherFile, [.. "# a record saved as UTF-16\n"u8.ToArray().SelectMany(b => new byte[] { b, 0 })]);
        File.WriteAllBytes(otherFile + ".broken-20261112T063348Z", [1, 2, 3]);
        byte[] before = File.ReadAllBytes(otherFile);
        var later = new Rt(new AppRig(ChapterEnd, savePath: Path.Combine(other, "save.dat"), trophyScenes: Scenes, now: () => clock));
        Assert.Equal(TrophyFileState.NotOurs, later.Rig.Services.TrophyFileAtStart);
        Assert.Equal(before, File.ReadAllBytes(otherFile));
        later.Play();
        Assert.Equal([1], later.Earned);
        Assert.Equal(before, File.ReadAllBytes(otherFile + ".broken-20261112T063348Z-2"));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(otherFile + ".broken-20261112T063348Z"));
        Assert.Equal([1], TrophyLog.Read(otherFile, out _).Ids);
    }

    [Fact]
    public void AFileThatIsNotARecordAndCannotBeSetAsideAtStartStillGetsTheCatchUp()
    {
        // lines that are no record's, held for reading by another program (a backup tool) through the start and the title: the file can be read, so it is known to
        // hold no achievement, but it cannot be moved aside (on Windows; elsewhere a reader keeps no rename out and it is set aside at once). What the save implies
        // (1 and 2) is then no replay of old achievements: the normal catch-up is shown while the record's write waits. Once the holder lets go, the next save sets
        // the file aside and writes the record, and nothing is shown again
        var clock = new DateTime(2026, 11, 12, 6, 33, 48, DateTimeKind.Utc);
        byte[] note = "these are not achievements\nat all\n"u8.ToArray();
        File.WriteAllBytes(TrophyFile, note);
        string broken = TrophyFile + ".broken-20261112T063348Z";
        long ms = 0;
        var found = new List<IReadOnlyList<int>>();
        Rt rt;
        using (new FileStream(TrophyFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            rt = new Rt(new AppRig(AppRig.ThreePages, savePath: SavePath, store: ProfileWithProgress(1, 2), trophyScenes: Scenes, now: () => clock, milliseconds: () => ms));
            rt.Rig.Services.TrophiesFoundInSave += found.Add;
            Assert.Equal(TrophyFileState.NotOurs, rt.Rig.Services.TrophyFileAtStart);
            Assert.Equal([1, 2], rt.Rig.Services.TrophiesCaughtUp);
            ToTheTitlesMenu(rt);
            if (OperatingSystem.IsWindows())
            {
                // read, not moved: nothing was written over it, and the write stays due
                Assert.Equal(note, File.ReadAllBytes(TrophyFile));
                Assert.False(File.Exists(broken));
                Assert.True(rt.Rig.Services.TrophyWriteDue);
                Assert.Contains(rt.Log, l => l.StartsWith("achievements file not written: the file there is not a record of achievements and could not be set aside", StringComparison.Ordinal));
            }

            Assert.Equal([1, 2], Assert.Single(found));
            Assert.Contains("achievements: 2 already earned by the save's progress, recorded without a date (1, 2)", rt.Log);
            Assert.DoesNotContain(rt.Log, l => l.StartsWith("achievements: no catch-up messages", StringComparison.Ordinal));
            UiTrophyNotice shown = StartOfTheCatchUp(rt);
            Assert.Equal((1, true), (shown.Id, shown.CatchUp));
            Assert.Contains("trophies from the save's progress: 2, one message each on the title (1, 2)", rt.Log);
        }

        // let go: the next save five seconds on sets the file aside under its broken name and writes the record; the catch-up is not raised again
        ms += AppServices.TrophyRetryMilliseconds;
        Assert.True(rt.Rig.Services.Commit());
        Assert.False(rt.Rig.Services.TrophyWriteDue);
        Assert.Equal(note, File.ReadAllBytes(broken));
        TrophyLog onDisk = TrophyLog.Read(TrophyFile, out bool damaged);
        Assert.False(damaged);
        Assert.Equal([1, 2], onDisk.Ids);
        Assert.Null(onDisk.EarnedUtc(1));
        Assert.Null(onDisk.EarnedUtc(2));
        Assert.Contains($"achievements file was not a record of achievements: moved to {broken}", rt.Log);
        rt.Run(300);
        Assert.Single(found);
        Assert.Empty(rt.Earned);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void AnAchievementTheUnreadFileHoldsIsNotAnnouncedAgainOnceTheFileCanBeRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // the record holds trophy 1 with its date; it is held at the start (the save implies nothing) and let go before the chapter's _RDED: the earn reads it
        // first, so achievement 1 is no news: no event, no notice, the date kept, nothing written
        const string Record = "# held\nversion=1\n1=2026-01-02T03:04:05Z\n";
        File.WriteAllText(TrophyFile, Record);
        using var held = new ManualResetEventSlim(false);
        bool release = false;
        Thread holder = Hold(TrophyFile, held, () => Volatile.Read(ref release));
        var rt = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: Scenes));
        Assert.Equal(TrophyFileState.Unreadable, rt.Rig.Services.TrophyFileAtStart);
        Assert.False(rt.Rig.Services.TrophyWriteDue);                        // nothing to write yet
        Volatile.Write(ref release, true);
        holder.Join();
        rt.Play();
        Assert.True(rt.Rig.Host.IsSceneDone(335));
        Assert.Empty(rt.Earned);
        Assert.Null(rt.Ui.TrophyNotice);
        Assert.DoesNotContain(rt.Log, l => l.StartsWith("trophy 1 earned", StringComparison.Ordinal));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), rt.Rig.Services.Trophies.EarnedUtc(1));
        Assert.Equal(Record, File.ReadAllText(TrophyFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnreadRecordAndTheSaveThatCompleteTheSetTogetherGiveThePlatinumWithADate(bool letGoAfterTheEarn)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // the record holds the 27 chapters with their dates and is held at the start; the save has lessons 1..24 done and seen (it implies 28). The last
        // lesson's _RDED earns 29: the platinum comes with it when the file was let go before (the earn reads it first), else at the next save once it can be
        // read. Either way both are announced and the file holds the platinum with its date
        var record = new TrophyLog();
        for (int id = 1; id <= 27; id++)
        {
            record.Add(id, new DateTime(2026, 11, 1, 10, 0, id, DateTimeKind.Utc));
        }

        record.Write(TrophyFile);
        SaveStore store = AppRig.NewStore();
        for (int node = 533; node <= 556; node++)
        {
            SetDone(store, node);
            SetSeen(store, node);
        }

        var clock = new DateTime(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc);
        long ms = 0;
        using var held = new ManualResetEventSlim(false);
        bool release = false;
        Thread holder = Hold(TrophyFile, held, () => Volatile.Read(ref release));
        var rt = new Rt(new AppRig(LastLesson, savePath: SavePath, store: store, trophyScenes: Scenes, now: () => clock, milliseconds: () => ms));
        Assert.Equal([28], rt.Rig.Services.TrophiesCaughtUp);              // what the save implies; the record could not be read
        Assert.True(rt.Rig.Services.TrophyWriteDue);
        if (!letGoAfterTheEarn)
        {
            Volatile.Write(ref release, true);
            holder.Join();
        }

        rt.Play();
        if (letGoAfterTheEarn)
        {
            Assert.Equal([29], rt.Earned);                                   // the file still held: the platinum cannot be told yet
            Volatile.Write(ref release, true);
            holder.Join();
            ms += AppServices.TrophyRetryMilliseconds;
            Assert.True(rt.Rig.Services.Commit());
        }

        Assert.Equal([29, 0], rt.Earned);
        Assert.False(rt.Rig.Services.TrophyWriteDue);
        TrophyLog onDisk = TrophyLog.Read(TrophyFile, out bool damaged);
        Assert.False(damaged);
        Assert.Equal(30, onDisk.Count);
        Assert.Equal(clock, onDisk.EarnedUtc(0));
        Assert.Equal(clock, onDisk.EarnedUtc(29));
        Assert.Null(onDisk.EarnedUtc(28));
        Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 1, DateTimeKind.Utc), onDisk.EarnedUtc(1));
        rt.Run(UiTrophyNotice.FadeInFrames + UiTrophyNotice.QueuedHoldFrames + UiTrophyNotice.FadeOutFrames + 2);
        Assert.Equal((0, TrophyGrade.Platinum), (rt.Ui.TrophyNotice!.Id, rt.Ui.TrophyNotice.Grade));
    }

    [Fact]
    public void AWriteThatFailedIsTriedAgainBySavesAtMostEveryFiveSecondsAndAtOnceByAnEarnAndTheQuit()
    {
        // a reader that does not share deleting keeps the file from being replaced (Windows only; elsewhere the rename goes through), and each try waits for it
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string TwoEarns = "_WKST(F822,eq,1);_WKST(F110,eq,335);_RTST(335);_RDED(335);" + Page + "_TRPY(28);" + Page + "_END_();";
        File.WriteAllText(TrophyFile, "version=1\n9=2026-01-02T03:04:05Z\n");
        long ms = 1_000_000;
        Rt rt;
        using (new FileStream(TrophyFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            rt = new Rt(new AppRig(TwoEarns, savePath: SavePath, trophyScenes: Scenes, milliseconds: () => ms));
            Assert.Equal(TrophyFileState.Read, rt.Rig.Services.TrophyFileAtStart);    // the reader shares reading
            rt.Play();
            Assert.Equal([1], rt.Earned);
            Assert.True(rt.Rig.Services.TrophyWriteDue);
            int tries = rt.Rig.Services.TrophyWriteAttempts;

            // saves within five seconds of the failure leave the file alone (no wait on the game thread)
            rt.Rig.Services.Commit();
            rt.Rig.Services.Commit();
            Assert.Equal(tries, rt.Rig.Services.TrophyWriteAttempts);

            // five seconds on: one save tries (and fails again), the next waits again
            ms += AppServices.TrophyRetryMilliseconds;
            rt.Rig.Services.Commit();
            rt.Rig.Services.Commit();
            Assert.Equal(tries + 1, rt.Rig.Services.TrophyWriteAttempts);

            // an earn writes at once, whenever the last try was
            for (int i = 0; i < 400 && rt.Earned.Count < 2; i++)
            {
                rt.Step(click: i % 3 == 0);
            }

            Assert.Equal([1, 28], rt.Earned);
            Assert.Equal(tries + 2, rt.Rig.Services.TrophyWriteAttempts);
            Assert.True(rt.Rig.Services.TrophyWriteDue);
        }

        // let go: a save within five seconds of the last failure still waits, the one after writes; what the file held stays with its date
        rt.Rig.Services.Commit();
        Assert.True(rt.Rig.Services.TrophyWriteDue);
        ms += AppServices.TrophyRetryMilliseconds;
        Assert.True(rt.Rig.Services.Commit());
        Assert.False(rt.Rig.Services.TrophyWriteDue);
        TrophyLog onDisk = TrophyLog.Read(TrophyFile, out bool damaged);
        Assert.False(damaged);
        Assert.Equal([1, 9, 28], onDisk.Ids);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), onDisk.EarnedUtc(9));
        Assert.Contains(rt.Log, l => l.StartsWith("achievements file written after an earlier failure", StringComparison.Ordinal));

        // the quit does not wait: a write that failed a moment ago is tried at once
        string other = Path.Combine(_dir, "quit");
        Directory.CreateDirectory(other);
        string otherFile = Path.Combine(other, TrophyLog.FileName);
        File.WriteAllText(otherFile, "version=1\n9=2026-01-02T03:04:05Z\n");
        long otherMs = 0;
        Rt quitting;
        using (new FileStream(otherFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            quitting = new Rt(new AppRig(ChapterEnd, savePath: Path.Combine(other, "save.dat"), trophyScenes: Scenes, milliseconds: () => otherMs));
            quitting.Play();
            Assert.True(quitting.Rig.Services.TrophyWriteDue);
        }

        quitting.Rig.Services.Commit();
        Assert.True(quitting.Rig.Services.TrophyWriteDue);                   // a save, a moment after the failure: not yet
        Assert.True(quitting.Rig.Services.CommitAtQuit());
        Assert.False(quitting.Rig.Services.TrophyWriteDue);
        Assert.Equal([1, 9], TrophyLog.Read(otherFile, out _).Ids);
    }

    [Fact]
    public void AchievementsAreStillEarnedAfterALanguageChangeRebuiltTheHost()
    {
        // the language is fixed per host: Config's language row builds a new one, and the trophy rules must hang on it as they hung on the first
        var rt = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: Scenes));
        GameHost before = rt.Rig.Host;
        rt.Rig.Services.Config.Language = (short)Language.Ja;
        rt.Rig.Services.ConfigChanged(ConfigChange.Language);
        Assert.NotSame(before, rt.Rig.Host);
        rt.Play();
        Assert.Equal([1], rt.Earned);
        Assert.Equal(1, rt.Ui.TrophyNotice!.Id);
        Assert.Equal([1], TrophyLog.Read(TrophyFile, out _).Ids);
    }

    // ---- the catch-up -------------------------------------------------------------------------------------------------------------------------------

    private static SaveStore ProfileWithProgress(params int[] doneTrophies)
    {
        SaveStore store = AppRig.NewStore();
        store.SystemData.Block.SetG(SystemData.SceneSeenWord + (485 >> 4), 1 << (485 & 15));       // the prologue was read
        store.SystemData.Block.SetG(10, 1);                                                         // a story was started: the title shows its menu
        foreach (int trophy in doneTrophies)
        {
            SetDone(store, Scenes[trophy - 1]);
        }

        return store;
    }

    private static void ToTheTitlesMenu(Rt rt)
    {
        rt.Runtime.Start();
        for (int i = 0; i < 3000 && rt.Ui.Find<TitleScreen>() is not { MenuShown: true }; i++)
        {
            rt.Step(click: i % 30 < 2);                                       // the boot notice, the intro and the caution are skipped by a button
        }

        Assert.True(rt.Ui.Find<TitleScreen>() is { MenuShown: true });
    }

    // steps until the first notice of the title's catch-up has started its way in; returns it
    private static UiTrophyNotice StartOfTheCatchUp(Rt rt)
    {
        for (int i = 0; i < 200 && rt.Ui.TrophyNotice is not { Started: true }; i++)
        {
            rt.Step();
        }

        UiTrophyNotice first = Assert.IsType<UiTrophyNotice>(rt.Ui.TrophyNotice);
        Assert.True(first.Started && first.CatchUp, "the catch-up never started");
        return first;
    }

    // the notices of the title's catch-up as each comes in (the step of the first frame of its way in), over the next steps; one already on show is not counted
    private static List<(long Frame, UiTrophyNotice Notice)> CatchUpLine(Rt rt, int steps)
    {
        var shown = new List<(long Frame, UiTrophyNotice Notice)>();
        UiTrophyNotice? before = rt.Ui.TrophyNotice is { Started: true } up ? up : null;
        for (int i = 0; i < steps; i++)
        {
            rt.Step();
            if (rt.Ui.TrophyNotice is { Started: true } n && !ReferenceEquals(n, before) && (shown.Count == 0 || !ReferenceEquals(shown[^1].Notice, n)))
            {
                Assert.Equal(1f / UiTrophyNotice.CatchUpFadeInFrames, n.Alpha);                 // the step it is first seen in is the first of its way in
                shown.Add((rt.Runtime.Frame, n));
            }
        }

        return shown;
    }

    [Fact]
    public void TheStartAddsWhatTheSavesProgressHoldsWithoutADateAndTheTitleShowsOneShortNoticeEachInTheOrderOfTheirIds()
    {
        // chapters 1..5 and 14 (a gold one) finished before achievements were recorded; lesson 3 was seen
        SaveStore store = ProfileWithProgress(1, 2, 3, 4, 5, 14);
        SetSeen(store, 535);
        var rig = new AppRig(AppRig.ThreePages, savePath: SavePath, store: store, trophyScenes: Scenes);
        Assert.Equal([1, 2, 3, 4, 5, 14, 28], rig.Services.TrophiesCaughtUp);

        // recorded at start, silently, with no date
        string text = File.ReadAllText(TrophyFile);
        foreach (int id in new[] { 1, 2, 3, 4, 5, 14, 28 })
        {
            Assert.Contains($"{id}=earlier\n", text, StringComparison.Ordinal);
            Assert.Null(rig.Services.Trophies.EarnedUtc(id));
        }

        var rt = new Rt(rig);
        Assert.Empty(rt.Earned);
        rt.Runtime.Start();
        rt.Step();
        Assert.Contains(rt.Log, l => l.StartsWith("achievements: 7 already earned by the save's progress", StringComparison.Ordinal));

        // nothing over the boot notice or the title's fade-in
        Assert.Null(rt.Ui.TrophyNotice);
        for (int i = 0; i < 3000 && rt.Ui.Find<TitleScreen>() is not { MenuShown: true }; i++)
        {
            Assert.Null(rt.Ui.TrophyNotice);
            rt.Step(click: i % 30 < 2);
        }

        Assert.True(rt.Ui.Find<TitleScreen>() is { MenuShown: true });
        Assert.Contains("trophies from the save's progress: 7, one message each on the title (1, 2, 3, 4, 5, 14, 28)", rt.Log);
        SaysMessageNotNotice(rt.Log);
        Assert.Equal(6, rt.Ui.TrophyNoticesWaiting);

        // then one notice per achievement in the order of their ids, each with the heading of every notice, the achievement's own name and its grade (its emblem
        // here: the rig makes no icons), in the short times: 10 frames in, 70 up, 12 out, the next one right after (a new one every 92 steps, about 1.5 s)
        List<(long Frame, UiTrophyNotice Notice)> shown = CatchUpLine(rt, 800);
        Assert.Equal([1, 2, 3, 4, 5, 14, 28], shown.Select(x => x.Notice.Id));
        UiAssets assets = rt.Ui.Context.Assets;
        Assert.All(shown, x => Assert.Equal((true, "Achievement unlocked", UiTrophyText.Name(assets, x.Notice.Id), TrophyTable.Grade(x.Notice.Id), UiTrophyPicture.Emblem), (x.Notice.CatchUp, x.Notice.Heading, x.Notice.Name, x.Notice.Grade, x.Notice.Picture)));
        Assert.Equal("text 361", shown[0].Notice.Name);
        Assert.Equal([true, false, false, false, false, false, false], shown.Select(x => x.Notice.Chime));         // the chime, when it is on, once for the line
        Assert.All(shown.Zip(shown.Skip(1)), pair => Assert.Equal(92L, pair.Second.Frame - pair.First.Frame));
        Assert.All(shown, x => Assert.True(x.Notice.Done && x.Notice.Held == UiTrophyNotice.CatchUpHoldFrames, $"achievement {x.Notice.Id}: held {x.Notice.Held}"));
        Assert.Equal((10, 70, 12), (UiTrophyNotice.CatchUpFadeInFrames, UiTrophyNotice.CatchUpHoldFrames, UiTrophyNotice.CatchUpFadeOutFrames));
        Assert.Null(rt.Ui.TrophyNotice);
        Assert.Empty(rt.Earned);                                              // no earned event: nothing was earned anew
        Assert.DoesNotContain(rt.Log, l => l.StartsWith("trophy ", StringComparison.Ordinal));

        // once: nothing comes back, in this run or the next
        rt.Run(300);
        Assert.Null(rt.Ui.TrophyNotice);
        var next = new Rt(new AppRig(AppRig.ThreePages, savePath: SavePath, store: store, trophyScenes: Scenes));
        Assert.Empty(next.Rig.Services.TrophiesCaughtUp);
        ToTheTitlesMenu(next);
        next.Run(60);
        Assert.Null(next.Ui.TrophyNotice);
        Assert.DoesNotContain(next.Log, l => l.StartsWith("achievements:", StringComparison.Ordinal));
        Assert.Equal(text, File.ReadAllText(TrophyFile));
    }

    [Fact]
    public void ANoticeOfTheCatchUpReadsAsAnAchievementsOwnAndShowsItsStateLikeOne()
    {
        // one achievement found in the save: one notice, the one an earned achievement has, in the short times
        var rt = new Rt(new AppRig(AppRig.ThreePages, savePath: SavePath, store: ProfileWithProgress(2), trophyScenes: Scenes));
        Assert.Equal([2], rt.Rig.Services.TrophiesCaughtUp);
        ToTheTitlesMenu(rt);
        UiTrophyNotice notice = StartOfTheCatchUp(rt);
        Assert.Equal((2, TrophyGrade.Bronze, "Achievement unlocked", "text 363", true), (notice.Id, notice.Grade, notice.Heading, notice.Name, notice.CatchUp));
        Assert.Equal(0, rt.Ui.TrophyNoticesWaiting);
        rt.Run(UiTrophyNotice.CatchUpFadeInFrames - 1);
        Assert.Equal("2 \"text 363\" emblem a 1.00 +0", rt.Runtime.TrophyNoticeSummary());
        rt.Run(UiTrophyNotice.CatchUpHoldFrames + UiTrophyNotice.CatchUpFadeOutFrames);
        Assert.Null(rt.Ui.TrophyNotice);
        Assert.Contains("trophies from the save's progress: 1, one message each on the title (2)", rt.Log);
    }

    [Fact]
    public void TheCatchUpComesInTheOrderOfTheIdsWhateverOrderItIsAskedInAndOnceEach()
    {
        // the start and the script verb trophies= both come to ShowTrophiesFound; asked out of order, with a repeat and an id that is no achievement's, the title
        // shows each achievement once, in the order of the ids
        var rt = new Rt(new AppRig(AppRig.ThreePages, savePath: SavePath, store: ProfileWithProgress(), trophyScenes: Scenes));
        Assert.Empty(rt.Rig.Services.TrophiesCaughtUp);
        rt.Runtime.ShowTrophiesFound([14, 2, 30, 5, 2]);
        ToTheTitlesMenu(rt);
        UiTrophyNotice first = StartOfTheCatchUp(rt);
        List<(long Frame, UiTrophyNotice Notice)> rest = CatchUpLine(rt, 400);
        Assert.Equal([2, 5, 14], new[] { first }.Concat(rest.Select(x => x.Notice)).Select(n => n.Id));
        Assert.Contains("trophies from the save's progress: 3, one message each on the title (2, 5, 14)", rt.Log);
    }

    [Fact]
    public void WithTheNoticeOffOrNotYetTurnedOnTheCatchUpRecordsAndShowsNothing()
    {
        // the notice off: recorded, logged, never shown
        string other = Path.Combine(_dir, "quiet");
        Directory.CreateDirectory(other);
        var quiet = new Rt(new AppRig(AppRig.ThreePages, savePath: Path.Combine(other, "save.dat"), store: ProfileWithProgress(1, 2, 3), trophyScenes: Scenes), notice: false);
        ToTheTitlesMenu(quiet);
        quiet.Run(30);
        Assert.Null(quiet.Ui.TrophyNotice);
        Assert.Contains("trophies from the save's progress: 3 (no message: Notify is off)", quiet.Log);
        Assert.Equal([1, 2, 3], TrophyLog.Read(Path.Combine(other, TrophyLog.FileName), out _).Ids);

        // the game's own switches over a settings file that does not name the key (every player's, until they turn the notice on): the same
        string unset = Path.Combine(_dir, "unset");
        Directory.CreateDirectory(unset);
        var fresh = new Rt(new AppRig(AppRig.ThreePages, savePath: Path.Combine(unset, "save.dat"), store: ProfileWithProgress(1, 2, 3), trophyScenes: Scenes), notice: null);
        fresh.Rig.Services.Port = new PortSettings(null, new AppSettingsData());
        ToTheTitlesMenu(fresh);
        fresh.Run(30);
        Assert.Null(fresh.Ui.TrophyNotice);
        Assert.Contains("trophies from the save's progress: 3 (no message: Notify is off)", fresh.Log);
        Assert.Equal([1, 2, 3], TrophyLog.Read(Path.Combine(unset, TrophyLog.FileName), out _).Ids);
        SaysMessageNotNotice(quiet.Log);
        SaysMessageNotNotice(fresh.Log);
    }

    [Fact]
    public void ARecordOfTheChaptersAndASaveOfTheLessonsCompleteTheSetAtStartAndThePlatinumComesFirstOnTheTitle()
    {
        // the 27 chapters recorded with their dates (an earlier run), the 25 lessons done in the save (an imported PS4 save): nothing is left to earn, so the
        // start itself gives the platinum, without a date, beside the two lessons; the title shows the three in the order of their ids, each with its grade
        var record = new TrophyLog();
        for (int id = 1; id <= 27; id++)
        {
            record.Add(id, new DateTime(2026, 11, 1, 10, 0, id, DateTimeKind.Utc));
        }

        record.Write(TrophyFile);
        SaveStore store = ProfileWithProgress();
        for (int node = 533; node <= 557; node++)
        {
            SetDone(store, node);
        }

        SetSeen(store, 533);
        var rig = new AppRig(AppRig.ThreePages, savePath: SavePath, store: store, trophyScenes: Scenes);
        Assert.Equal([0, 28, 29], rig.Services.TrophiesCaughtUp);
        string text = File.ReadAllText(TrophyFile);
        foreach (int id in new[] { 0, 28, 29 })
        {
            Assert.Contains($"\n{id}=earlier\n", text, StringComparison.Ordinal);
        }

        Assert.Contains("\n1=2026-11-01T10:00:01Z\n", text, StringComparison.Ordinal);           // the chapters keep their dates
        Assert.Equal(30, TrophyLog.Read(TrophyFile, out _).Count);

        var rt = new Rt(rig);
        ToTheTitlesMenu(rt);
        UiTrophyNotice first = StartOfTheCatchUp(rt);
        rt.Run(UiTrophyNotice.CatchUpFadeInFrames - 1);
        Assert.Equal("0 \"All Achievements\" emblem a 1.00 +2", rt.Runtime.TrophyNoticeSummary());
        var list = new DisplayList(4096);
        rt.Ui.Draw(list);
        DrawCmd emblem = Assert.Single(list.Commands.ToArray(), c => c.Texture.Kind == TextureKind.UiComposed && UiComposedId.IsTrophyEmblem(c.Texture.A));
        Assert.Equal(UiComposedId.TrophyEmblem(TrophyGrade.Platinum), emblem.Texture.A);
        List<(long Frame, UiTrophyNotice Notice)> rest = CatchUpLine(rt, 300);
        Assert.Equal([(0, TrophyGrade.Platinum, "All Achievements"), (28, TrophyGrade.Silver, "First Lesson"), (29, TrophyGrade.Gold, "Every Lesson")], new[] { first }.Concat(rest.Select(x => x.Notice)).Select(n => (n.Id, n.Grade, n.Name)));
        Assert.Empty(rt.Earned);
        Assert.Contains("trophies from the save's progress: 3, one message each on the title (0, 28, 29)", rt.Log);
    }

    [Fact]
    public void AKeyOrAClickTheTitleTakesEndsTheCatchUpAndTheTitleStillGetsIt()
    {
        var rt = new Rt(new AppRig(AppRig.ThreePages, savePath: SavePath, store: ProfileWithProgress(1, 2, 3, 4, 5), trophyScenes: Scenes));
        ToTheTitlesMenu(rt);
        UiTrophyNotice first = StartOfTheCatchUp(rt);
        Assert.Equal((1, 4), (first.Id, rt.Ui.TrophyNoticesWaiting));

        // the pointer only moving over the title is no answer to it: the line stays
        rt.PointAt(1500f, 500f);
        rt.PointAt(1540f, 520f);
        Assert.Equal(4, rt.Ui.TrophyNoticesWaiting);

        // nor is a key or a click the title's menu has no use for (D7: "a key press or click that the title handles"): Ctrl, Tab, a key bound to nothing, a
        // shoulder button, a click beside the items
        TitleScreen title = Assert.IsType<TitleScreen>(rt.Ui.Top);
        TitleItem? cursor = title.Cursor;
        rt.Step(shell: ShellActions.FastForward);
        rt.Step(shell: ShellActions.SkipToggle);
        rt.Step(shell: ShellActions.AnyKey);
        rt.Press(PadButtons.L1);
        rt.ClickAt(1700f, 300f);
        Assert.Equal(4, rt.Ui.TrophyNoticesWaiting);
        Assert.Equal(cursor, title.Cursor);
        Assert.Same(title, rt.Ui.Top);

        // the second comes in at its time; a key the title takes (Down moves its cursor) ends the line: the one on show finishes its short time, nothing follows
        rt.Run(92 - 2 - 6);
        UiTrophyNotice second = Assert.IsType<UiTrophyNotice>(rt.Ui.TrophyNotice);
        Assert.Equal((2, true, 3), (second.Id, second.Started, rt.Ui.TrophyNoticesWaiting));
        rt.Run(20);
        Assert.Same(title, rt.Ui.Top);
        rt.Press(PadButtons.Down);
        Assert.NotEqual(cursor, title.Cursor);                                // the title got the key
        Assert.Equal(0, rt.Ui.TrophyNoticesWaiting);
        Assert.Same(second, rt.Ui.TrophyNotice);
        Assert.Equal(1f, second.Alpha);
        rt.Run(UiTrophyNotice.CatchUpHoldFrames - second.Held + UiTrophyNotice.CatchUpFadeOutFrames);
        Assert.True(second.Done);
        Assert.Null(rt.Ui.TrophyNotice);
        rt.Run(300);
        Assert.Null(rt.Ui.TrophyNotice);
        Assert.Equal([1, 2, 3, 4, 5], TrophyLog.Read(TrophyFile, out _).Ids);         // the record is whole: only the showing ended

        // a click does the same: the right click asks the title's quit question, and the line ends with it
        string other = Path.Combine(_dir, "click");
        Directory.CreateDirectory(other);
        var clicked = new Rt(new AppRig(AppRig.ThreePages, savePath: Path.Combine(other, "save.dat"), store: ProfileWithProgress(1, 2, 3), trophyScenes: Scenes));
        ToTheTitlesMenu(clicked);
        UiTrophyNotice shown = StartOfTheCatchUp(clicked);
        Assert.Equal(2, clicked.Ui.TrophyNoticesWaiting);
        var input = UiInput.PointerFrame(900f, 500f, PointerButtons.Right, PointerButtons.Right);
        clicked.Runtime.Step(input with { Frame = 100_000 }, 0, new DisplayList(2048));
        Assert.IsType<ConfirmDialog>(clicked.Ui.Top);
        Assert.Equal(0, clicked.Ui.TrophyNoticesWaiting);
        Assert.Same(shown, clicked.Ui.TrophyNotice);
    }

    [Fact]
    public void TheCatchUpAsksForTheIconsOfTheNoticeOnShowAndOfTheNextOneOnly()
    {
        // five achievements found in the save: the icons are made one ahead (the notice on show and the next), not all at once; a line a key ends leaves the
        // others unmade
        var rt = new Rt(new AppRig(AppRig.ThreePages, savePath: SavePath, store: ProfileWithProgress(1, 2, 3, 4, 5), trophyScenes: Scenes));
        var made = new List<int>();
        rt.Runtime.TrophyIconMaker = id =>
        {
            made.Add(id);
            return Icon();
        };
        var workers = new HeldWorkers(rt);
        ToTheTitlesMenu(rt);
        Assert.Equal((2, 2), (rt.Runtime.TrophyIconsStarted, workers.Held));

        // the first waits for its icon as a notice of its own does, then comes in with it
        rt.Run(5);
        UiTrophyNotice first = Assert.IsType<UiTrophyNotice>(rt.Ui.TrophyNotice);
        Assert.Equal((1, UiTrophyPicture.Waiting), (first.Id, first.Picture));
        workers.RunNext();
        workers.RunNext();
        Assert.Equal([1, 2], made);
        rt.Step();
        Assert.Equal((UiTrophyPicture.Icon, 1f / UiTrophyNotice.CatchUpFadeInFrames), (first.Picture, first.Alpha));

        // while it is up nothing more is asked for; the moment the second is on show the third's icon is
        rt.Run(90);
        Assert.Equal(2, rt.Runtime.TrophyIconsStarted);
        rt.Run(2);
        UiTrophyNotice second = Assert.IsType<UiTrophyNotice>(rt.Ui.TrophyNotice);
        Assert.Equal((2, UiTrophyPicture.Icon, 0, 1f / UiTrophyNotice.CatchUpFadeInFrames), (second.Id, second.Picture, second.Waited, second.Alpha));          // its icon was made while the first was up
        Assert.Equal((3, 1), (rt.Runtime.TrophyIconsStarted, workers.Held));

        // a key the title takes ends the line: nothing more is made
        rt.Press(PadButtons.Down);
        rt.Run(200);
        Assert.Null(rt.Ui.TrophyNotice);
        Assert.Equal(3, rt.Runtime.TrophyIconsStarted);
        Assert.Equal([1, 2], made);
    }

    [Fact]
    public void TheChimeSoundsOnceForTheWholeCatchUpWhenTheSoundIsOn()
    {
        var rt = new Rt(new AppRig(AppRig.ThreePages, savePath: SavePath, store: ProfileWithProgress(1, 2, 3), trophyScenes: Scenes), sound: true);
        int asked = 0;
        rt.Runtime.ChimeStarter = work =>
        {
            asked++;
            work();
        };
        ToTheTitlesMenu(rt);
        Assert.Equal(1, asked);                                               // its samples are made as the line is put in, once
        rt.Run((3 * 92) + 20);
        Assert.Null(rt.Ui.TrophyNotice);
        Assert.Equal(1, rt.Runtime.ChimesPlayed);
        Assert.Equal(1, asked);
        Assert.Equal("achievement chime for achievement 1 (gain 0.200)", Assert.Single(rt.Log, l => l.Contains("achievement chime", StringComparison.Ordinal)));

        // the sound off (the default): no chime at all, and nothing is said about one
        string other = Path.Combine(_dir, "quiet");
        Directory.CreateDirectory(other);
        var quiet = new Rt(new AppRig(AppRig.ThreePages, savePath: Path.Combine(other, "save.dat"), store: ProfileWithProgress(1, 2, 3), trophyScenes: Scenes));
        quiet.Runtime.ChimeStarter = _ => asked++;
        ToTheTitlesMenu(quiet);
        quiet.Run((3 * 92) + 20);
        Assert.Equal(0, quiet.Runtime.ChimesPlayed);
        Assert.Equal(1, asked);
        Assert.DoesNotContain(quiet.Log, l => l.Contains("achievement chime", StringComparison.Ordinal));
    }

    [Fact]
    public void NoNoticeIsShownUntilThePlayerTurnsItOn()
    {
        // a game without the port's switches shows none: the rule is "on", not "not off"
        var bare = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: Scenes), notice: null);
        Assert.Null(bare.Rig.Services.Port);
        bare.Play();
        Assert.Equal([1], bare.Earned);
        Assert.Contains("trophy 1 earned (no message: Notify is off)", bare.Log);
        Assert.Null(bare.Ui.TrophyNotice);
        Assert.Equal([1], TrophyLog.Read(TrophyFile, out _).Ids);                // recorded all the same

        // the game's own switches over a settings file that does not name the key: off; trophy_notice=on: shown
        string unset = Path.Combine(_dir, "unset");
        Directory.CreateDirectory(unset);
        var fresh = new Rt(new AppRig(ChapterEnd, savePath: Path.Combine(unset, "save.dat"), trophyScenes: Scenes), notice: null);
        fresh.Rig.Services.Port = new PortSettings(null, new AppSettingsData());
        fresh.Play();
        Assert.Contains("trophy 1 earned (no message: Notify is off)", fresh.Log);
        Assert.Null(fresh.Ui.TrophyNotice);

        string on = Path.Combine(_dir, "on");
        Directory.CreateDirectory(on);
        var shown = new Rt(new AppRig(ChapterEnd, savePath: Path.Combine(on, "save.dat"), trophyScenes: Scenes), notice: null);
        shown.Rig.Services.Port = new PortSettings(null, new AppSettingsData(TrophyNotice: true));
        shown.Play();
        Assert.Contains("trophy 1 earned", shown.Log);
        Assert.Equal(1, shown.Ui.TrophyNotice!.Id);
    }

    [Fact]
    public void ARecordMarkedReadOnlyIsWrittenAtStartAndItsCatchUpDoesNotComeBack()
    {
        // trophies.ini carries the read-only attribute (the profile was copied back from a backup or a zip). On Windows the record could then never be written:
        // every start found again what the save implies and put the whole line of messages on the title again
        File.WriteAllText(TrophyFile, "# old\nversion=1\n9=2026-01-02T03:04:05Z\n");
        File.SetAttributes(TrophyFile, File.GetAttributes(TrophyFile) | FileAttributes.ReadOnly);
        SaveStore store = ProfileWithProgress(1, 2);
        try
        {
            var rig = new AppRig(AppRig.ThreePages, savePath: SavePath, store: store, trophyScenes: Scenes);
            Assert.Equal([1, 2], rig.Services.TrophiesCaughtUp);
            Assert.False(rig.Services.TrophyWriteDue);
            TrophyLog onDisk = TrophyLog.Read(TrophyFile, out bool damaged);
            Assert.False(damaged);
            Assert.Equal([1, 2, 9], onDisk.Ids);
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), onDisk.EarnedUtc(9));
            Assert.Equal((FileAttributes)0, File.GetAttributes(TrophyFile) & FileAttributes.ReadOnly);
            Assert.Equal([TrophyLog.FileName], Directory.GetFiles(_dir).Select(Path.GetFileName));

            // the next start finds nothing new
            var next = new AppRig(AppRig.ThreePages, savePath: SavePath, store: store, trophyScenes: Scenes);
            Assert.Empty(next.Services.TrophiesCaughtUp);
            Assert.False(next.Services.TrophyWriteDue);
        }
        finally
        {
            File.SetAttributes(TrophyFile, FileAttributes.Normal);       // (the folder is removed after the test)
        }
    }

    [Fact]
    public void AFreshProfileCatchesUpNothingAndWritesNoFile()
    {
        var rig = new AppRig(AppRig.ThreePages, savePath: SavePath, trophyScenes: Scenes);
        Assert.Empty(rig.Services.TrophiesCaughtUp);
        Assert.Equal(0, rig.Services.Trophies.Count);
        Assert.False(File.Exists(TrophyFile));
        var log = new List<string>();
        rig.Services.Logged += log.Add;
        rig.Begin();
        Assert.DoesNotContain(log, l => l.Contains("achievements", StringComparison.Ordinal));
    }

    // ---- the icon of the notice ---------------------------------------------------------------------------------------------------------------------
    // The notice of one achievement shows that achievement's icon, which the app cuts from the player's pictures on a worker (AppRuntime.TrophyIconMaker). Here
    // the maker is the test's, and where the moment matters the worker is too: the starter hands the work over instead of running it

    private static UiComposedPicture Icon() => new(UiTrophyNotice.IconSize, UiTrophyNotice.IconSize, new byte[UiTrophyNotice.IconSize * UiTrophyNotice.IconSize * 4]);

    /// <summary>The icon workers the runtime asked for, kept until the test runs them (on the test's thread).</summary>
    private sealed class HeldWorkers
    {
        private readonly List<(Func<string> Work, TaskCompletionSource<string> Done)> _held = [];

        public HeldWorkers(Rt rt, Action? asked = null)
        {
            rt.Runtime.TrophyIconStarter = work =>
            {
                asked?.Invoke();
                var done = new TaskCompletionSource<string>();
                _held.Add((work, done));
                return done.Task;
            };
        }

        public int Held => _held.Count;

        /// <summary>Runs the oldest worker to its end, as the thread pool would have by now.</summary>
        public void RunNext()
        {
            (Func<string> work, TaskCompletionSource<string> done) = _held[0];
            _held.RemoveAt(0);
            try
            {
                done.SetResult(work());
            }
            catch (InvalidOperationException e)
            {
                done.SetException(e);
            }
        }
    }

    [Fact]
    public void AnEarnIsWrittenToTheRecordBeforeAnyIconExistsAndTheNoticeComesInWhenTheWorkerIsDone()
    {
        var clock = new DateTime(2026, 11, 12, 13, 3, 11, DateTimeKind.Utc);
        var rt = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: Scenes, now: () => clock));
        UiAssets assets = rt.Ui.Context.Assets;
        var made = new List<int>();
        rt.Runtime.TrophyIconMaker = id =>
        {
            made.Add(id);
            return Icon();
        };

        // what is true at the moment the worker is asked for: the achievement is in the record and in the file, and no notice is up yet
        var atRequest = new List<(bool Earned, string File, bool Notice)>();
        var workers = new HeldWorkers(rt, () => atRequest.Add((rt.Rig.Services.Trophies.IsEarned(1), File.Exists(TrophyFile) ? File.ReadAllText(TrophyFile) : "(no file)", rt.Ui.TrophyNotice is not null)));
        rt.Play();

        Assert.Equal([1], rt.Earned);
        (bool earned, string file, bool noticeUp) = Assert.Single(atRequest);
        Assert.True(earned);
        Assert.Contains("1=2026-11-12T13:03:11Z\n", file, StringComparison.Ordinal);
        Assert.False(noticeUp);
        Assert.Equal((1, 1), (rt.Runtime.TrophyIconsStarted, workers.Held));

        // the worker has not run: nothing was made, the icon is not settled, and the earn is complete all the same (recorded, written, logged, its notice in line)
        Assert.Empty(made);
        Assert.False(assets.TrophyIconSettled(1));
        Assert.Null(assets.Composed(UiComposedId.TrophyIcon(1)));
        Assert.Contains("trophy 1 earned", rt.Log);
        Assert.Equal([1], TrophyLog.Read(TrophyFile, out _).Ids);
        UiTrophyNotice notice = Assert.IsType<UiTrophyNotice>(rt.Ui.TrophyNotice);
        Assert.Equal((1, UiTrophyPicture.Waiting, 0f), (notice.Id, notice.Picture, notice.Alpha));

        // the story goes on under the notice that waits; the state line says so
        rt.Run(10);
        Assert.Equal((UiTrophyPicture.Waiting, 0f), (notice.Picture, notice.Alpha));
        Assert.Equal("1 \"text 361\" waiting a 0.00 +0", rt.Runtime.TrophyNoticeSummary());
        Assert.False(rt.Runtime.WaitForTrophyIcons(TimeSpan.Zero));           // a worker is still out

        // the worker finishes: the icon is stored, the next step starts the notice with it and writes the worker's line in the log
        workers.RunNext();
        Assert.Equal([1], made);
        Assert.True(assets.TrophyIconSettled(1));
        Assert.NotNull(assets.Composed(UiComposedId.TrophyIcon(1)));
        Assert.True(rt.Runtime.WaitForTrophyIcons(TimeSpan.Zero));
        rt.Step();
        Assert.Equal((UiTrophyPicture.Icon, 1f / UiTrophyNotice.FadeInFrames), (notice.Picture, notice.Alpha));
        Assert.Single(rt.Log, l => l.StartsWith("achievement icon 1: 72 px from the game's pictures in ", StringComparison.Ordinal));
        rt.Run(UiTrophyNotice.FadeInFrames);
        Assert.Equal("1 \"text 361\" icon a 1.00 +0", rt.Runtime.TrophyNoticeSummary());
        var list = new DisplayList(2048);
        rt.Ui.Draw(list);
        DrawCmd drawn = Assert.Single(list.Commands.ToArray(), c => c.Texture.Kind == TextureKind.UiComposed);
        Assert.Equal(UiComposedId.TrophyIcon(1), drawn.Texture.A);
        rt.Run(UiTrophyNotice.HoldFrames + UiTrophyNotice.FadeOutFrames);
        Assert.Equal("-", rt.Runtime.TrophyNoticeSummary());
    }

    [Fact]
    public void TheIconsWorkerRunsOffTheLoopAndIsStartedOncePerAchievementHoweverOftenItsNoticeIsAskedFor()
    {
        var rt = new Rt(new AppRig(AppRig.ThreePages, savePath: SavePath, trophyScenes: Scenes));
        UiAssets assets = rt.Ui.Context.Assets;
        int loop = Environment.CurrentManagedThreadId;
        var calls = new System.Collections.Concurrent.ConcurrentQueue<(int Id, int Thread)>();
        using var gate = new ManualResetEventSlim(false);
        rt.Runtime.TrophyIconMaker = id =>
        {
            calls.Enqueue((id, Environment.CurrentManagedThreadId));
            gate.Wait(TimeSpan.FromSeconds(30));
            return Icon();
        };
        rt.Play();

        // the script verb trophy=5 comes here (GameApp: _runtime.ShowTrophy): the notice of achievement 5, its icon asked for, nothing recorded
        rt.Runtime.ShowTrophy(5);
        Assert.Equal(1, rt.Runtime.TrophyIconsStarted);
        Assert.True(SpinWait.SpinUntil(() => !calls.IsEmpty, TimeSpan.FromSeconds(30)), "the maker was never called");
        Assert.True(calls.TryPeek(out (int Id, int Thread) call));
        Assert.Equal(5, call.Id);
        Assert.NotEqual(loop, call.Thread);                                   // on a worker: the loop goes on while a large picture is read
        Assert.Equal(0, rt.Rig.Services.Trophies.Count);
        Assert.False(File.Exists(TrophyFile));

        // asked for again while the first worker is still at it, and by a third notice: no second worker. Another achievement gets its own
        rt.Run(5);
        rt.Runtime.ShowTrophy(5);
        rt.Runtime.ShowTrophy(5);
        Assert.Equal(1, rt.Runtime.TrophyIconsStarted);
        rt.Runtime.ShowTrophy(7);
        Assert.Equal(2, rt.Runtime.TrophyIconsStarted);
        Assert.Equal(3, rt.Ui.TrophyNoticesWaiting);
        Assert.False(assets.TrophyIconSettled(5));
        Assert.Equal(UiTrophyPicture.Waiting, rt.Ui.TrophyNotice!.Picture);

        gate.Set();
        Assert.True(rt.Runtime.WaitForTrophyIcons(TimeSpan.FromSeconds(30)), "the workers did not finish");
        Assert.True(assets.TrophyIconSettled(5) && assets.TrophyIconSettled(7));
        Assert.Equal([5, 7], calls.Select(c => c.Id).Order());                // one call each
        rt.Step();
        Assert.Equal(UiTrophyPicture.Icon, rt.Ui.TrophyNotice!.Picture);
        Assert.Single(rt.Log, l => l.StartsWith("achievement icon 5: ", StringComparison.Ordinal));
        Assert.Single(rt.Log, l => l.StartsWith("achievement icon 7: ", StringComparison.Ordinal));

        // once its icon is made, a later notice of the same achievement starts no worker and waits for nothing
        rt.Run((3 * (UiTrophyNotice.FadeInFrames + UiTrophyNotice.QueuedHoldFrames + UiTrophyNotice.FadeOutFrames)) + UiTrophyNotice.FadeInFrames + UiTrophyNotice.HoldFrames + UiTrophyNotice.FadeOutFrames);
        Assert.Null(rt.Ui.TrophyNotice);
        rt.Runtime.ShowTrophy(5);
        rt.Step();
        Assert.Equal((2, 2), (rt.Runtime.TrophyIconsStarted, calls.Count));
        Assert.Equal((UiTrophyPicture.Icon, 0), (rt.Ui.TrophyNotice!.Picture, rt.Ui.TrophyNotice.Waited));

        // not a trophy: no notice, no worker
        rt.Runtime.ShowTrophy(30);
        rt.Runtime.ShowTrophy(-1);
        Assert.Equal(2, rt.Runtime.TrophyIconsStarted);
        Assert.Equal(0, rt.Ui.TrophyNoticesWaiting);
    }

    [Fact]
    public void TheLastLessonAndThePlatinumEachGetTheirIcon()
    {
        SaveStore store = AppRig.NewStore();
        foreach (int node in Scenes)
        {
            SetDone(store, node);
        }

        for (int node = 533; node <= 556; node++)
        {
            SetDone(store, node);
            SetSeen(store, node);
        }

        var rt = new Rt(new AppRig(LastLesson, savePath: SavePath, store: store, trophyScenes: Scenes));
        var made = new List<int>();
        rt.Runtime.TrophyIconMaker = id =>
        {
            made.Add(id);
            return Icon();
        };
        var workers = new HeldWorkers(rt);
        rt.Play();

        // 29 and the platinum in one step: two workers, in that order, and what the start caught up from the save (28 achievements, no notice each) asked for none
        Assert.Equal([29, 0], rt.Earned);
        Assert.Equal((2, 2), (rt.Runtime.TrophyIconsStarted, workers.Held));
        workers.RunNext();
        workers.RunNext();
        Assert.Equal([29, 0], made);
        rt.Run(UiTrophyNotice.FadeInFrames);
        Assert.Equal("29 \"Every Lesson\" icon a 1.00 +1", rt.Runtime.TrophyNoticeSummary());
        rt.Run(UiTrophyNotice.QueuedHoldFrames + UiTrophyNotice.FadeOutFrames + UiTrophyNotice.FadeInFrames);
        Assert.Equal("0 \"All Achievements\" icon a 1.00 +0", rt.Runtime.TrophyNoticeSummary());
        UiAssets assets = rt.Ui.Context.Assets;
        Assert.Equal([0, 29], Enumerable.Range(0, TrophyTable.Count).Where(assets.TrophyIconSettled));
    }

    [Fact]
    public void WithoutAMakerOrAnIconTheNoticeShowsTheGradesEmblem()
    {
        // no maker (a run without a game folder; every other test of this class): "there is none" is said at once, no worker, no wait, the bronze emblem
        var rt = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: Scenes));
        UiAssets assets = rt.Ui.Context.Assets;
        rt.Play();
        Assert.Equal([1], rt.Earned);
        Assert.True(assets.TrophyIconSettled(1));
        Assert.Null(assets.Composed(UiComposedId.TrophyIcon(1)));
        Assert.Equal(0, rt.Runtime.TrophyIconsStarted);
        Assert.True(rt.Runtime.WaitForTrophyIcons(TimeSpan.Zero));
        UiTrophyNotice notice = Assert.IsType<UiTrophyNotice>(rt.Ui.TrophyNotice);
        Assert.Equal(0, notice.Waited);
        rt.Run(UiTrophyNotice.FadeInFrames);
        Assert.Equal("1 \"text 361\" emblem a 1.00 +0", rt.Runtime.TrophyNoticeSummary());
        Assert.DoesNotContain(rt.Log, l => l.StartsWith("achievement icon", StringComparison.Ordinal));
        var list = new DisplayList(2048);
        rt.Ui.Draw(list);
        Assert.Equal(UiComposedId.TrophyEmblem(TrophyGrade.Bronze), Assert.Single(list.Commands.ToArray(), c => c.Texture.Kind == TextureKind.UiComposed).Texture.A);

        // a maker that cannot make the icon (the picture is not in this game folder): the emblem of the grade, as soon as the worker has said so
        var workers = new HeldWorkers(rt);
        rt.Runtime.TrophyIconMaker = _ => null;
        rt.Run(UiTrophyNotice.HoldFrames + UiTrophyNotice.FadeOutFrames);
        rt.Runtime.ShowTrophy(5);
        rt.Run(3);
        Assert.Equal("5 \"text 369\" waiting a 0.00 +0", rt.Runtime.TrophyNoticeSummary());
        workers.RunNext();
        rt.Run(UiTrophyNotice.FadeInFrames);
        Assert.Equal("5 \"text 369\" emblem a 1.00 +0", rt.Runtime.TrophyNoticeSummary());
        Assert.Contains("achievement icon 5: not in this game folder's pictures; the message shows the grade's emblem", rt.Log);
        list = new DisplayList(2048);
        rt.Ui.Draw(list);
        Assert.Equal(UiComposedId.TrophyEmblem(TrophyGrade.Silver), Assert.Single(list.Commands.ToArray(), c => c.Texture.Kind == TextureKind.UiComposed).Texture.A);

        // a maker that fails: the loop says "there is none" for it at its next step, with the reason in the log; the notice does not sit out its 45 frames
        rt.Runtime.TrophyIconMaker = _ => throw new InvalidOperationException("the picture is broken");
        rt.Run(UiTrophyNotice.HoldFrames + UiTrophyNotice.FadeOutFrames);
        rt.Runtime.ShowTrophy(6);
        workers.RunNext();
        Assert.False(assets.TrophyIconSettled(6));
        Assert.True(rt.Runtime.WaitForTrophyIcons(TimeSpan.Zero));            // a worker that failed has finished
        rt.Step();
        Assert.True(assets.TrophyIconSettled(6));
        Assert.Contains("achievement icon 6: not made (the picture is broken); the message shows the grade's emblem", rt.Log);
        SaysMessageNotNotice(rt.Log);
        Assert.Equal((UiTrophyPicture.Emblem, 0), (rt.Ui.TrophyNotice!.Picture, rt.Ui.TrophyNotice.Waited));

        // a worker that never finishes costs the notice its 45 frames and nothing else
        rt.Runtime.TrophyIconMaker = _ => Icon();
        rt.Run(UiTrophyNotice.FadeInFrames + UiTrophyNotice.HoldFrames + UiTrophyNotice.FadeOutFrames);
        rt.Runtime.ShowTrophy(8);
        rt.Run(UiTrophyNotice.IconWaitFrames);
        Assert.Equal("8 \"text 376\" waiting a 0.00 +0", rt.Runtime.TrophyNoticeSummary());
        rt.Run(UiTrophyNotice.FadeInFrames);
        Assert.Equal("8 \"text 376\" emblem a 1.00 +0", rt.Runtime.TrophyNoticeSummary());
        Assert.Equal(1, workers.Held);
    }

    [Fact]
    public void NoIconIsMadeForANoticeThatIsNotShown()
    {
        // the notice switched off, and a run that writes pictures on its own schedule: the achievement is recorded, nothing is shown, no picture is read for it
        int made = 0;
        var quiet = new Rt(new AppRig(ChapterEnd, savePath: SavePath, trophyScenes: Scenes));
        quiet.Runtime.TrophyIconMaker = _ =>
        {
            Interlocked.Increment(ref made);
            return Icon();
        };
        quiet.Rig.Services.Port = new PreviewPortSettings(new PreviewServices(Language.En)) { TrophyNotice = false };
        quiet.Play();
        Assert.Equal([1], quiet.Earned);
        Assert.Equal(0, quiet.Runtime.TrophyIconsStarted);
        Assert.False(quiet.Ui.Context.Assets.TrophyIconSettled(1));

        string other = Path.Combine(_dir, "pictures");
        Directory.CreateDirectory(other);
        var pictures = new Rt(new AppRig(ChapterEnd, savePath: Path.Combine(other, "save.dat"), trophyScenes: Scenes), pictureRun: true);
        pictures.Runtime.TrophyIconMaker = quiet.Runtime.TrophyIconMaker;
        pictures.Play();
        Assert.Equal([1], pictures.Earned);
        Assert.Equal(0, pictures.Runtime.TrophyIconsStarted);

        // the title's catch-up with the notice off shows nothing and reads no picture either
        string third = Path.Combine(_dir, "caught-up");
        Directory.CreateDirectory(third);
        var caughtUp = new Rt(new AppRig(AppRig.ThreePages, savePath: Path.Combine(third, "save.dat"), store: ProfileWithProgress(1, 2, 3), trophyScenes: Scenes), notice: false);
        caughtUp.Runtime.TrophyIconMaker = quiet.Runtime.TrophyIconMaker;
        ToTheTitlesMenu(caughtUp);
        caughtUp.Run(UiTrophyNotice.CatchUpFadeInFrames + 1);
        Assert.Equal("-", caughtUp.Runtime.TrophyNoticeSummary());
        Assert.Equal(0, caughtUp.Runtime.TrophyIconsStarted);
        Assert.Equal(0, Volatile.Read(ref made));

        // switched on again, the next notice of the quiet run gets its icon
        quiet.Rig.Services.Port.TrophyNotice = true;
        quiet.Runtime.ShowTrophy(7);
        Assert.Equal(1, quiet.Runtime.TrophyIconsStarted);
        Assert.True(quiet.Runtime.WaitForTrophyIcons(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, Volatile.Read(ref made));
    }

    // ---- save thumbnails ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AFrameThatShowsTheNoticeIsNotGrabbedForASaveThumbnail()
    {
        var rt = new Rt(new AppRig(AppRig.ThreePages, trophyScenes: Scenes));
        int grabs = 0;
        rt.Runtime.Grab = () =>
        {
            grabs++;
            return (4, 4, new byte[64]);
        };
        rt.Play();
        rt.Run(200);                                                          // a page being read is grabbed now and then (the thumbnail of a save from a prompt)
        Assert.True(grabs >= 1, "the rig's page is never grabbed: the test proves nothing");

        // while the notice is in the frame: no grab of the page, and a menu that opens takes the last clean page instead of the frame
        rt.Runtime.ShowTrophy(5);
        rt.Run(3);
        Assert.True(rt.Ui.TrophyNotice is { Alpha: > 0f });
        int before = grabs;
        rt.Run(200);
        Assert.True(rt.Ui.TrophyNotice is { Alpha: 1f });
        Assert.Equal(before, grabs);
        rt.Step(shell: ShellActions.SystemMenu);
        Assert.IsType<SystemMenuScreen>(rt.Ui.Top);
        Assert.Equal(before, grabs);

        // once it has gone the frame is the thumbnail again
        rt.Ui.Clear();
        rt.Run(UiTrophyNotice.HoldFrames + UiTrophyNotice.FadeOutFrames + 10);
        Assert.Null(rt.Ui.TrophyNotice);
        rt.Step(shell: ShellActions.SystemMenu);
        Assert.IsType<SystemMenuScreen>(rt.Ui.Top);
        Assert.True(grabs > before, "the frame without a notice was not grabbed when the menu opened");
    }
}
