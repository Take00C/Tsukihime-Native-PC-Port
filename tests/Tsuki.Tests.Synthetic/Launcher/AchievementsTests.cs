using System.Diagnostics;
using Tsuki.App.Services;
using Tsuki.Engine.Save;
using Tsuki.Engine.Trophies;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Synthetic.Save;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>
/// v0.1.9: what the launchers' Achievements page is built from, over temporary profiles: the 30 rows from trophies.ini united with what save.dat implies, the
/// stamp the pages poll, and the profile files around it (backups, the settings key). Nothing here has a game folder: the chapter names and the chapter scenes
/// come from the game and are tested with it (Tsuki.Tests.Data/Trophies).
/// </summary>
public sealed class AchievementsTests : IDisposable
{
    private static readonly SlotDate T0 = new(2026, 11, 11, 9, 0, 0);
    private static readonly DateTime Earned1 = new(2026, 11, 12, 13, 3, 11, DateTimeKind.Utc);
    private static readonly DateTime Earned2 = new(2027, 3, 5, 4, 7, 59, DateTimeKind.Utc);
    private static readonly int[] Lessons = [.. Enumerable.Range(533, 25)];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-v019-achievements-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;

    public AchievementsTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "Tsuki"));
        Directory.CreateDirectory(_paths.DataDirectory);
        Achievements.Forget();
    }

    public void Dispose()
    {
        Achievements.Forget();
        Directory.Delete(_root, recursive: true);
    }

    // -- helpers ----------------------------------------------------------------------------------------------------------

    // scene_done(n) is bit n % 16 of G[300 + n / 16], scene_seen(n) the same bit of G[110 + n / 16] (the PS4's system block)
    private static void SetBit(SaveStore store, int firstWord, int n)
    {
        int word = firstWord + (n / 16);
        store.SystemData.Block.SetG(word, (ushort)(store.SystemData.Block.GetG(word) | (1 << (n % 16))));
    }

    private static SaveStore Store(IEnumerable<int> done, IEnumerable<int> seen, int slots = 1)
    {
        var store = new SaveStore();
        for (int i = 0; i < slots; i++)
        {
            store.SaveManual(i, SaveStoreTests.Staging((ulong)(0x40 + i)), T0 with { Minute = i });
        }

        foreach (int n in done)
        {
            SetBit(store, 300, n);
        }

        foreach (int n in seen)
        {
            SetBit(store, 110, n);
        }

        return store;
    }

    private void WriteSave(IEnumerable<int> done, IEnumerable<int> seen) => SaveFile.WriteAtomic(_paths.SaveFile, Store(done, seen));

    private void WriteLog(params (int Id, DateTime? When)[] earned)
    {
        var log = new TrophyLog();
        foreach ((int id, DateTime? when) in earned)
        {
            log.Add(id, when);
        }

        log.Write(_paths.TrophiesFile);
    }

    private AchievementSummary Read() => Achievements.Read(_paths, null);

    private static int[] EarnedIds(AchievementSummary summary) => [.. summary.Rows.Where(r => r.Earned).Select(r => r.Id)];

    // the local date and time as the page words it: "5 Mar 2027, 04:07" (English, no leading zero on the day, 24 hours)
    private static string Local(DateTime utc)
    {
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
        return $"{local.Day} {months[local.Month - 1]} {local.Year}, {local.Hour:00}:{local.Minute:00}";
    }

    // every entry below the profile folder with its size and time, and the folder's own time (it changes when anything is created or removed in it)
    private string[] ProfileListing()
    {
        if (!Directory.Exists(_paths.DataDirectory))
        {
            return ["(no folder)"];
        }

        var listing = new List<string> { "folder written " + Directory.GetLastWriteTimeUtc(_paths.DataDirectory).Ticks };
        foreach (string entry in Directory.EnumerateFileSystemEntries(_paths.DataDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(entry);
            listing.Add($"{Path.GetRelativePath(_paths.DataDirectory, entry)} {(info.Exists ? info.Length : -1)} {File.GetLastWriteTimeUtc(entry).Ticks}");
        }

        return [.. listing];
    }

    // -- the rows -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithNoFilesThereAre30LockedRowsWithThePortsOwnNames()
    {
        AchievementSummary summary = Read();
        Assert.Equal(30, summary.Rows.Count);
        Assert.Equal(Enumerable.Range(0, 30), summary.Rows.Select(r => r.Id));
        Assert.All(summary.Rows, row =>
        {
            Assert.False(row.Earned);
            Assert.Null(row.EarnedUtc);
            Assert.Equal("Locked", row.StateLabel);
            Assert.Equal(TrophyTable.Grade(row.Id), row.Grade);
            Assert.Equal(LauncherTexts.AchievementFallbackName(row.Id), row.Name);
            Assert.Equal(LauncherTexts.AchievementDetail(row.Id), row.Detail);
            Assert.False(string.IsNullOrWhiteSpace(row.Name));
            Assert.False(string.IsNullOrWhiteSpace(row.Detail));
        });
        Assert.Equal(30, summary.Rows.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(30, summary.Rows.Select(r => r.Detail).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(30, summary.Total);
        Assert.Equal(0, summary.Earned);
        Assert.Equal(0, summary.Percent);
        Assert.Equal("0 of 30 earned", summary.CountLine);
        Assert.False(summary.Damaged);
        Assert.False(summary.GameNames);

        // the same without even a profile folder, which reading does not make
        var nowhere = new LauncherPaths(Path.Combine(_root, "no", "such", "profile"));
        Assert.Equal(summary, Achievements.Read(nowhere, null));
        Assert.Equal(default, Achievements.Stamp(nowhere));
        Assert.False(Directory.Exists(Path.Combine(_root, "no")));
    }

    [Fact]
    public void TheRecordAloneShowsWhatItHoldsWithItsDates()
    {
        WriteLog((5, Earned1), (7, null), (26, Earned2));
        AchievementSummary summary = Read();
        Assert.Equal([5, 7, 26], EarnedIds(summary));
        Assert.Equal(Earned1, summary.Rows[5].EarnedUtc);
        Assert.Null(summary.Rows[7].EarnedUtc);
        Assert.Equal(Earned2, summary.Rows[26].EarnedUtc);
        Assert.Equal(Local(Earned1), summary.Rows[5].StateLabel);
        Assert.Equal("Earned", summary.Rows[7].StateLabel);
        Assert.Equal(Local(Earned2), summary.Rows[26].StateLabel);
        Assert.Equal("Locked", summary.Rows[6].StateLabel);
        Assert.Equal(3, summary.Earned);
        Assert.Equal(10, summary.Percent);
        Assert.Equal("3 of 30 earned", summary.CountLine);
        Assert.False(summary.Damaged);
    }

    [Fact]
    public void ASaveAloneShowsTheLessonsItImpliesAsEarnedWithNoDate()
    {
        // one lesson started and not finished
        WriteSave(done: [], seen: [541]);
        AchievementSummary summary = Read();
        Assert.Equal([28], EarnedIds(summary));
        Assert.Null(summary.Rows[28].EarnedUtc);
        Assert.Equal("Earned", summary.Rows[28].StateLabel);
        Assert.Equal("1 of 30 earned", summary.CountLine);
        Assert.False(summary.Damaged);

        // 24 of the 25 lessons completed
        WriteSave(done: Lessons.Where(n => n != 557), seen: Lessons);
        Assert.Equal([28], EarnedIds(Read()));

        // all 25
        WriteSave(done: Lessons, seen: Lessons);
        summary = Read();
        Assert.Equal([28, 29], EarnedIds(summary));
        Assert.All(new[] { 28, 29 }, id => Assert.Null(summary.Rows[id].EarnedUtc));

        // a save with no progress, and one whose bits are the lessons' neighbours and nothing else
        WriteSave(done: [], seen: []);
        Assert.Empty(EarnedIds(Read()));
        WriteSave(done: [532, 558], seen: [532, 558]);
        Assert.Empty(EarnedIds(Read()));
    }

    [Fact]
    public void TheLessonBitsAreReadFromTheWordsOfThePs4sSystemBlock()
    {
        // done: G[333] bits 5..15 and G[334] bits 0..13 are nodes 533..557; seen: the same bits of G[143] and G[144]
        var store = new SaveStore();
        store.SystemData.Block.SetG(333, 0xFFE0);
        store.SystemData.Block.SetG(334, 0x3FFF);
        SaveFile.WriteAtomic(_paths.SaveFile, store);
        Assert.Equal([29], EarnedIds(Read()));

        store = new SaveStore();
        store.SystemData.Block.SetG(143, 0x0020);
        SaveFile.WriteAtomic(_paths.SaveFile, store);
        Assert.Equal([28], EarnedIds(Read()));
        store = new SaveStore();
        store.SystemData.Block.SetG(144, 0x2000);
        SaveFile.WriteAtomic(_paths.SaveFile, store);
        Assert.Equal([28], EarnedIds(Read()));

        // one bit to either side is another scene's
        store = new SaveStore();
        store.SystemData.Block.SetG(143, 0x001F);
        store.SystemData.Block.SetG(144, 0xC000);
        store.SystemData.Block.SetG(333, 0xFFFF);
        store.SystemData.Block.SetG(334, 0xDFFF);
        SaveFile.WriteAtomic(_paths.SaveFile, store);
        Assert.Empty(EarnedIds(Read()));
    }

    [Fact]
    public void WithoutAGameFolderASaveShowsOnlyTheTwoLessonAchievements()
    {
        // Every scene of the flowchart is done and seen. Which 27 of them earn the chapter achievements is a table of the game's own program, so without a game
        // folder the page cannot tell and shows none of them from the save; the game records them at its next start, and the record needs no game folder.
        WriteSave(done: Enumerable.Range(0, 0x240), seen: Enumerable.Range(0, 0x240));
        AchievementSummary summary = Read();
        Assert.Equal([28, 29], EarnedIds(summary));
        Assert.False(summary.GameNames);
        Assert.Equal(2, summary.Earned);
        Assert.Equal(6, summary.Percent);

        // and the note shown in this state says why the count is short, not only that names and pictures are missing; the pictures it promises are the icons cut
        // from the player's own pictures (the PS4's cup pictures are shown nowhere: the grades have emblems drawn by the port)
        Assert.Contains("count the chapters your saves have already finished", LauncherTexts.AchievementsNoGameNote, StringComparison.Ordinal);
        Assert.Contains("the icons cut from your own game's pictures", LauncherTexts.AchievementsNoGameNote, StringComparison.Ordinal);
        Assert.DoesNotContain("cup", LauncherTexts.AchievementsNoGameNote, StringComparison.OrdinalIgnoreCase);

        // a folder that is no game folder is the same as none
        string notAGame = Path.Combine(_root, "not-a-game");
        Directory.CreateDirectory(notAGame);
        File.WriteAllText(Path.Combine(notAGame, "readme.txt"), "x");
        foreach (string? folder in new[] { notAGame, Path.Combine(_root, "missing"), string.Empty, "   ", null })
        {
            Assert.Equal(summary, Achievements.Read(_paths, folder));
        }

        // once the game has recorded them, they show without a game folder
        WriteLog([.. Enumerable.Range(1, 27).Select(id => (id, (DateTime?)null))]);
        Assert.Equal(Enumerable.Range(0, 30), EarnedIds(Read()));
    }

    [Fact]
    public void TheRecordAndTheSaveAreUnitedAndTogetherCanCompleteThePlatinum()
    {
        // the record holds the 27 chapters with their dates, the save implies the two lesson achievements: neither holds all 29, together they do
        WriteLog([.. Enumerable.Range(1, 27).Select(id => (id, (DateTime?)Earned1.AddHours(id)))]);
        Assert.Equal(Enumerable.Range(1, 27), EarnedIds(Read()));

        WriteSave(done: Lessons, seen: Lessons);
        AchievementSummary summary = Read();
        Assert.Equal(Enumerable.Range(0, 30), EarnedIds(summary));
        Assert.Equal(30, summary.Earned);
        Assert.Equal(100, summary.Percent);
        Assert.Equal("30 of 30 earned", summary.CountLine);

        // dates come from the record only
        for (int id = 1; id <= 27; id++)
        {
            Assert.Equal(Earned1.AddHours(id), summary.Rows[id].EarnedUtc);
        }

        Assert.All(new[] { 0, 28, 29 }, id =>
        {
            Assert.Null(summary.Rows[id].EarnedUtc);
            Assert.Equal("Earned", summary.Rows[id].StateLabel);
        });

        // without the save again: the page follows the files, the record alone
        File.Delete(_paths.SaveFile);
        Assert.Equal(Enumerable.Range(1, 27), EarnedIds(Read()));
    }

    [Fact]
    public void WhatBothTheRecordAndTheSaveShowKeepsTheRecordsDate()
    {
        WriteLog((28, Earned1), (3, null));
        WriteSave(done: Lessons, seen: Lessons);
        AchievementSummary summary = Read();
        Assert.Equal([3, 28, 29], EarnedIds(summary));
        Assert.Equal(Earned1, summary.Rows[28].EarnedUtc);
        Assert.Null(summary.Rows[29].EarnedUtc);
        Assert.Null(summary.Rows[3].EarnedUtc);
    }

    [Fact]
    public void ADamagedRecordIsSaidAndTheSaveStillCounts()
    {
        File.WriteAllBytes(_paths.TrophiesFile, new byte[300]);
        AchievementSummary summary = Read();
        Assert.True(summary.Damaged);
        Assert.Empty(EarnedIds(summary));
        Assert.Equal(30, summary.Rows.Count);

        // a file that is not a record reads the same every time: no reason for a page to read it again before it changes
        Assert.False(summary.Incomplete);

        WriteSave(done: Lessons, seen: Lessons);
        summary = Read();
        Assert.True(summary.Damaged);
        Assert.Equal([28, 29], EarnedIds(summary));

        // a folder of the record's name
        File.Delete(_paths.TrophiesFile);
        Directory.CreateDirectory(_paths.TrophiesFile);
        summary = Read();
        Assert.True(summary.Damaged);
        Assert.Equal([28, 29], EarnedIds(summary));
        Assert.False(Achievements.Stamp(_paths).TrophiesExists);
        Directory.Delete(_paths.TrophiesFile);

        // the record saved again as UTF-16 without a byte order mark: its achievements cannot be seen, so the page says the record could not be read, rather
        // than showing the rows without their dates; and like any file that is not a record it reads the same every time
        var record = new TrophyLog();
        record.Add(1, Earned1);
        File.WriteAllBytes(_paths.TrophiesFile, System.Text.Encoding.Unicode.GetBytes(record.Format()));
        summary = Read();
        Assert.True(summary.Damaged);
        Assert.False(summary.Incomplete);
        Assert.Equal([28, 29], EarnedIds(summary));

        // and once the record is good again the note goes
        WriteLog((1, Earned1));
        summary = Read();
        Assert.False(summary.Damaged);
        Assert.False(summary.Incomplete);
        Assert.Equal([1, 28, 29], EarnedIds(summary));
    }

    [Fact]
    public void AFileAnotherProgramHoldsMakesTheReadingIncompleteUntilItIsFreeAgain()
    {
        if (!OperatingSystem.IsWindows())
        {
            // elsewhere another program's handle never makes an open fail
            return;
        }

        WriteLog((5, Earned1));
        WriteSave(done: [], seen: [533]);

        // a time ahead of the clock: the save is read from disk at every reading, whatever the machine's pace (a settled reading would be kept instead)
        File.SetLastWriteTimeUtc(_paths.SaveFile, DateTime.UtcNow.AddHours(1));
        AchievementSummary whole = Read();
        Assert.False(whole.Damaged);
        Assert.False(whole.Incomplete);
        Assert.Equal([5, 28], EarnedIds(whole));

        // the record is held without sharing: the reading says it could not read it, and that this may pass
        using (new FileStream(_paths.TrophiesFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AchievementSummary held = Read();
            Assert.True(held.Damaged);
            Assert.True(held.Incomplete);
            Assert.Equal([28], EarnedIds(held));
            Assert.NotEqual(whole, held);
        }

        // let go without a change of the file (its size and time are as they were): the next reading is whole again, with the date
        AchievementSummary again = Read();
        Assert.False(again.Damaged);
        Assert.False(again.Incomplete);
        Assert.Equal(Earned1, again.Rows[5].EarnedUtc);
        Assert.Equal(whole, again);

        // The save is held: the last reading's words stand in (or none, when a test of another class read a profile of its own in between: one reading is
        // kept for the whole program), and the reading says so.
        using (new FileStream(_paths.SaveFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AchievementSummary busy = Read();
            Assert.False(busy.Damaged);
            Assert.True(busy.Incomplete);
            Assert.Contains(5, EarnedIds(busy));
            Assert.NotEqual(whole, busy);
        }

        Assert.Equal(whole, Read());
    }

    [Fact]
    public void ADamagedSaveCostsOnlyWhatTheSaveWouldHaveShown()
    {
        WriteLog((2, Earned1));
        File.WriteAllBytes(_paths.SaveFile, new byte[5000]);
        AchievementSummary summary = Read();
        Assert.False(summary.Damaged);                              // the note is about the record, and the record is fine
        Assert.Equal([2], EarnedIds(summary));

        // like the game, the page falls back on the save's previous generation
        SaveFile.WriteAtomic(_paths.SaveFile + ".bak", Store(done: Lessons, seen: [533]));
        File.SetLastWriteTimeUtc(_paths.SaveFile, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal([2, 28, 29], EarnedIds(Read()));

        // a folder of the save's name, and a truncated save
        File.Delete(_paths.SaveFile);
        File.Delete(_paths.SaveFile + ".bak");
        Directory.CreateDirectory(_paths.SaveFile);
        Assert.Equal([2], EarnedIds(Read()));
        Directory.Delete(_paths.SaveFile);
        byte[] whole = SaveFile.ToBytes(Store(done: Lessons, seen: Lessons));
        File.WriteAllBytes(_paths.SaveFile, whole[..(whole.Length / 2)]);
        Assert.Equal([2], EarnedIds(Read()));
        File.WriteAllBytes(_paths.SaveFile, whole);
        Assert.Equal([2, 28, 29], EarnedIds(Read()));
    }

    [Fact]
    public void ASaveThatChangedIsReadAgain()
    {
        WriteSave(done: [], seen: []);
        Assert.Empty(EarnedIds(Read()));
        Assert.Empty(EarnedIds(Read()));

        // the same size, a later time: what the game's next commit looks like
        long length = new FileInfo(_paths.SaveFile).Length;
        WriteSave(done: [], seen: [533]);
        Assert.Equal(length, new FileInfo(_paths.SaveFile).Length);
        File.SetLastWriteTimeUtc(_paths.SaveFile, DateTime.UtcNow.AddSeconds(3));
        Assert.Equal([28], EarnedIds(Read()));

        WriteSave(done: Lessons, seen: [533]);
        File.SetLastWriteTimeUtc(_paths.SaveFile, DateTime.UtcNow.AddSeconds(6));
        Assert.Equal([28, 29], EarnedIds(Read()));

        // and a save that is gone is gone
        File.Delete(_paths.SaveFile);
        Assert.Empty(EarnedIds(Read()));
    }

    [Fact]
    public void AFreshSaveRewrittenAtTheSameSizeAndTimeIsReadAgain()
    {
        // Two commits of the game inside one tick of the file clock (or inside the two seconds a FAT volume keeps): the size and the time stay, the content does
        // not. The first reading must come within the settle time of the write, as it does when the game commits twice in a row; a machine that stalled for two
        // seconds between the two lines has made that reading a settled one, which is kept, and then the round is done again.
        DateTime written;
        AchievementStamp stamp;
        for (int round = 0; ; round++)
        {
            Achievements.Forget();
            var clock = Stopwatch.StartNew();
            WriteSave(done: [], seen: [533]);
            written = File.GetLastWriteTimeUtc(_paths.SaveFile);
            stamp = Achievements.Stamp(_paths);
            Assert.Equal([28], EarnedIds(Read()));
            if (clock.Elapsed < TimeSpan.FromSeconds(2) || round == 4)
            {
                break;
            }
        }

        WriteSave(done: Lessons, seen: [533]);
        File.SetLastWriteTimeUtc(_paths.SaveFile, written);
        Assert.Equal(stamp, Achievements.Stamp(_paths));            // the stamp cannot tell the two apart
        Assert.Equal([28, 29], EarnedIds(Read()));

        // the same for a save whose time lies ahead of the clock (a copy from another machine)
        File.SetLastWriteTimeUtc(_paths.SaveFile, DateTime.UtcNow.AddHours(2));
        Assert.Equal([28, 29], EarnedIds(Read()));
        DateTime ahead = File.GetLastWriteTimeUtc(_paths.SaveFile);
        WriteSave(done: [], seen: []);
        File.SetLastWriteTimeUtc(_paths.SaveFile, ahead);
        Assert.Empty(EarnedIds(Read()));
    }

    [Fact]
    public void ASaveThatHasSettledIsReadOncePerStamp()
    {
        // the pages' readings must not cost a read of the whole save each: a file whose time is well in the past is read once and kept until its size or time changes
        DateTime old = DateTime.UtcNow.AddMinutes(-10);
        bool kept = false;
        for (int round = 0; round < 10 && !kept; round++)
        {
            Achievements.Forget();
            WriteSave(done: [], seen: [533]);
            File.SetLastWriteTimeUtc(_paths.SaveFile, old);
            Assert.Equal([28], EarnedIds(Read()));

            // Other bytes under the same size and the same old time, which no writer leaves: the kept reading answers. (One reading is kept for the whole
            // program; a test of another class that reads a profile of its own in between takes its place, and then this is done again.)
            WriteSave(done: Lessons, seen: [533]);
            File.SetLastWriteTimeUtc(_paths.SaveFile, old);
            kept = EarnedIds(Read()).SequenceEqual([28]);
        }

        Assert.True(kept, "a save whose size and time did not change was read again every time");

        // forgetting looks again, and so does any change of the time
        Achievements.Forget();
        Assert.Equal([28, 29], EarnedIds(Read()));
        WriteSave(done: [], seen: []);
        File.SetLastWriteTimeUtc(_paths.SaveFile, old.AddSeconds(1));
        Assert.Empty(EarnedIds(Read()));
    }

    // -- the summary --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, "0 of 30 earned")]
    [InlineData(1, 3, "1 of 30 earned")]
    [InlineData(2, 6, "2 of 30 earned")]
    [InlineData(3, 10, "3 of 30 earned")]
    [InlineData(10, 33, "10 of 30 earned")]
    [InlineData(15, 50, "15 of 30 earned")]
    [InlineData(20, 66, "20 of 30 earned")]
    [InlineData(29, 96, "29 of 30 earned")]         // rounded down: 100 is for all 30 only
    [InlineData(30, 100, "30 of 30 earned")]
    public void ThePercentageIsRoundedDownAndTheCountLineSaysHowMany(int earned, int percent, string line)
    {
        // ids 0.. upward: with 29 of them the platinum is among them and trophy 29 is not, so nothing is added
        WriteLog([.. Enumerable.Range(0, earned).Select(id => (id, (DateTime?)null))]);
        AchievementSummary summary = Read();
        Assert.Equal(earned, summary.Earned);
        Assert.Equal(30, summary.Total);
        Assert.Equal(percent, summary.Percent);
        Assert.Equal(line, summary.CountLine);
    }

    [Fact]
    public void ASummaryOfNoRowsIsZeroPercent()
    {
        var empty = new AchievementSummary([]);
        Assert.Equal(0, empty.Total);
        Assert.Equal(0, empty.Earned);
        Assert.Equal(0, empty.Percent);
        Assert.Equal("0 of 0 earned", empty.CountLine);
    }

    [Fact]
    public void EachGradeCountsItsOwnRows()
    {
        AchievementSummary none = Read();
        Assert.Equal(1, none.TotalOf(TrophyGrade.Platinum));
        Assert.Equal(4, none.TotalOf(TrophyGrade.Gold));
        Assert.Equal(21, none.TotalOf(TrophyGrade.Silver));
        Assert.Equal(4, none.TotalOf(TrophyGrade.Bronze));
        Assert.All(Enum.GetValues<TrophyGrade>(), grade => Assert.Equal(0, none.EarnedOf(grade)));

        // two bronze (1, 2), four silver (5, 6, 7, 28), one gold (14) and the platinum
        WriteLog((0, null), (1, null), (2, null), (5, null), (6, null), (7, null), (14, null), (28, null));
        AchievementSummary some = Read();
        Assert.Equal(1, some.EarnedOf(TrophyGrade.Platinum));
        Assert.Equal(1, some.EarnedOf(TrophyGrade.Gold));
        Assert.Equal(4, some.EarnedOf(TrophyGrade.Silver));
        Assert.Equal(2, some.EarnedOf(TrophyGrade.Bronze));
        Assert.Equal(4, some.TotalOf(TrophyGrade.Gold));
        Assert.Equal(8, some.Earned);

        Assert.Equal(["Platinum", "Bronze", "Gold", "Silver"], new[] { 0, 1, 14, 5 }.Select(id => some.Rows[id].GradeLabel));
    }

    [Fact]
    public void TheStateIsLockedOrEarnedOrTheLocalDateAndTime()
    {
        AchievementRow Row(bool earned, DateTime? when) => new(5, TrophyGrade.Silver, "n", "d", earned, when);

        Assert.Equal("Locked", Row(false, null).StateLabel);
        Assert.Equal("Locked", Row(false, Earned1).StateLabel);
        Assert.Equal("Earned", Row(true, null).StateLabel);

        // a time that is local already is shown as it is: the day without a leading zero, the month in English, 24 hours with leading zeros
        Assert.Equal("12 Nov 2026, 14:03", Row(true, new DateTime(2026, 11, 12, 14, 3, 59, DateTimeKind.Local)).StateLabel);
        Assert.Equal("5 Jan 2027, 00:05", Row(true, new DateTime(2027, 1, 5, 0, 5, 0, DateTimeKind.Local)).StateLabel);
        Assert.Equal("31 Aug 2026, 23:59", Row(true, new DateTime(2026, 8, 31, 23, 59, 59, DateTimeKind.Local)).StateLabel);

        // a UTC time is shown in the player's time zone; a time of no kind is UTC (what the record holds)
        foreach (DateTime utc in new[] { Earned1, Earned2, new DateTime(2026, 12, 31, 23, 30, 0, DateTimeKind.Utc), new DateTime(2026, 7, 1, 0, 30, 0, DateTimeKind.Utc) })
        {
            Assert.Equal(Local(utc), Row(true, utc).StateLabel);
            Assert.Equal(Local(utc), Row(true, DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)).StateLabel);
            Assert.Equal(Local(utc), LauncherTexts.AchievementEarnedAt(utc));
        }
    }

    [Fact]
    public void ADateAtTheEdgeOfTheCalendarIsShownAndNeverThrows()
    {
        // a hand-edited record: the local time of these dates lies outside what a date can hold in some time zones
        File.WriteAllText(_paths.TrophiesFile, "version=1\n1=0001-01-01T00:00:00Z\n2=9999-12-31T23:59:59Z\n");
        AchievementSummary summary = Read();
        Assert.False(summary.Damaged);
        Assert.Equal([1, 2], EarnedIds(summary));
        Assert.All(new[] { 1, 2 }, id =>
        {
            Assert.NotNull(summary.Rows[id].EarnedUtc);
            Assert.Matches("^[0-9]{1,2} [A-Z][a-z]{2} [0-9]{4}, [0-9]{2}:[0-9]{2}$", summary.Rows[id].StateLabel);
        });
    }

    [Fact]
    public void ThePagesWordsWithNumbersReadAsTheDesignSays()
    {
        Assert.Equal("Earned (12)", LauncherTexts.AchievementsTab(LauncherTexts.AchievementsTabEarned, 12));
        Assert.Equal("All (30)", LauncherTexts.AchievementsTab(LauncherTexts.AchievementsTabAll, 30));
        Assert.Equal("Locked (0)", LauncherTexts.AchievementsTab(LauncherTexts.AchievementsTabLocked, 0));
        Assert.Equal("12 of 30 earned", LauncherTexts.AchievementCount(12, 30));
        Assert.Equal("40%", LauncherTexts.AchievementPercent(40));
        Assert.Equal("Achievements", LauncherTexts.AchievementsTitle);
        Assert.Contains(TrophyLog.FileName, LauncherTexts.AchievementsFileNote, StringComparison.Ordinal);
        Assert.Contains(TrophyLog.FileName, LauncherTexts.AchievementsDamagedNote, StringComparison.Ordinal);

        // the page's promise, which a restore and an import keep (the tests of the backups below), and the restore question that says how
        Assert.EndsWith("Restoring an older save or importing a PS4 save never removes one.", LauncherTexts.AchievementsFileNote, StringComparison.Ordinal);
        var backup = new BackupInfo("20261027-153000", _root, new DateTime(2026, 10, 27, 15, 30, 0), ["save.dat"], 1, new SaveSummary(true, 1, 1, null, null));
        Assert.EndsWith(
            "Settings are kept as they are. Achievements are kept too, and the backup's are added to them.", LauncherTexts.RestoreQuestion(backup).Body, StringComparison.Ordinal);
        Assert.Equal(string.Empty, LauncherTexts.AchievementDetail(30));
        Assert.Equal(string.Empty, LauncherTexts.AchievementDetail(-1));

        // the page sits between Saves and Help
        Assert.Equal(
            [LauncherPage.Play, LauncherPage.GameFiles, LauncherPage.Display, LauncherPage.Language, LauncherPage.Controls, LauncherPage.Saves, LauncherPage.Achievements, LauncherPage.Help],
            Enum.GetValues<LauncherPage>());
    }

    [Fact]
    public void TheWordsOfEachAchievementNameItsDayAndItsSide()
    {
        // The order of the PS4's table: days 1..4 before the story parts (1..4), days 5..13 and the ending of the first side (5..14), then days 5..13 of the
        // second side (15..23), its two day 14s (24, 25) and its two endings (26, 27).
        for (int id = 1; id <= 4; id++)
        {
            Assert.Equal($"Finish day {id}.", LauncherTexts.AchievementDetail(id));
            Assert.Equal($"Day {id}", LauncherTexts.AchievementFallbackName(id));
        }

        for (int id = 5; id <= 13; id++)
        {
            Assert.Equal($"Finish day {id} on Arcueid's route.", LauncherTexts.AchievementDetail(id));
            Assert.Equal($"Finish day {id} on Ciel's route.", LauncherTexts.AchievementDetail(id + 10));
            Assert.EndsWith($"day {id}", LauncherTexts.AchievementFallbackName(id), StringComparison.Ordinal);
            Assert.EndsWith($"day {id}", LauncherTexts.AchievementFallbackName(id + 10), StringComparison.Ordinal);
            Assert.StartsWith("Arcueid", LauncherTexts.AchievementFallbackName(id), StringComparison.Ordinal);
            Assert.StartsWith("Ciel", LauncherTexts.AchievementFallbackName(id + 10), StringComparison.Ordinal);
        }

        Assert.Contains("Arcueid", LauncherTexts.AchievementDetail(14), StringComparison.Ordinal);
        foreach (int id in new[] { 24, 25 })
        {
            Assert.Contains("day 14 on Ciel's route", LauncherTexts.AchievementDetail(id), StringComparison.Ordinal);
            Assert.Contains("day 14", LauncherTexts.AchievementFallbackName(id), StringComparison.Ordinal);
        }

        foreach (int id in new[] { 26, 27 })
        {
            Assert.Contains("ending of Ciel's route", LauncherTexts.AchievementDetail(id), StringComparison.Ordinal);
        }

        Assert.Contains("first", LauncherTexts.AchievementDetail(24), StringComparison.Ordinal);
        Assert.Contains("second", LauncherTexts.AchievementDetail(25), StringComparison.Ordinal);
        Assert.Contains("first", LauncherTexts.AchievementDetail(26), StringComparison.Ordinal);
        Assert.Contains("second", LauncherTexts.AchievementDetail(27), StringComparison.Ordinal);
        Assert.Contains("one of", LauncherTexts.AchievementDetail(28), StringComparison.Ordinal);
        Assert.Contains("all 25", LauncherTexts.AchievementDetail(29), StringComparison.Ordinal);
        Assert.Contains("every other", LauncherTexts.AchievementDetail(0), StringComparison.Ordinal);

        // one short line each
        for (int id = 0; id < 30; id++)
        {
            Assert.InRange(LauncherTexts.AchievementDetail(id).Length, 10, 80);
            Assert.InRange(LauncherTexts.AchievementFallbackName(id).Length, 3, 40);
            Assert.DoesNotContain("\n", LauncherTexts.AchievementDetail(id), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TwoReadingsOfTheSameStateAreEqualAndAChangeIsNot()
    {
        WriteLog((5, Earned1));
        AchievementSummary a = Read();
        AchievementSummary b = Read();
        Assert.NotSame(a, b);
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // one more earned, another date, a record that turned unreadable: each is a change
        WriteLog((5, Earned1), (6, null));
        AchievementSummary more = Read();
        Assert.False(a.Equals(more));
        WriteLog((5, Earned2));
        Assert.False(a.Equals(Read()));
        File.WriteAllText(_paths.TrophiesFile, "no such thing");
        AchievementSummary damaged = Read();
        File.Delete(_paths.TrophiesFile);
        AchievementSummary none = Read();
        Assert.Equal(EarnedIds(damaged), EarnedIds(none));
        Assert.False(damaged.Equals(none));
        Assert.False(a.Equals(null));
    }

    // -- the stamp and the profile ----------------------------------------------------------------------------------------

    [Fact]
    public void TheStampChangesWhenEitherFileChangesAndOnlyThen()
    {
        AchievementStamp none = Achievements.Stamp(_paths);
        Assert.Equal(default, none);
        Assert.Equal(none, Achievements.Stamp(_paths));

        WriteLog((1, Earned1));
        AchievementStamp record = Achievements.Stamp(_paths);
        Assert.NotEqual(none, record);
        Assert.True(record.TrophiesExists);
        Assert.False(record.SaveExists);
        Assert.Equal(new FileInfo(_paths.TrophiesFile).Length, record.TrophiesLength);
        Assert.Equal(File.GetLastWriteTimeUtc(_paths.TrophiesFile), record.TrophiesWriteUtc);
        Assert.Equal(record, Achievements.Stamp(_paths));

        // one more line: another size
        WriteLog((1, Earned1), (2, Earned1));
        AchievementStamp longer = Achievements.Stamp(_paths);
        Assert.NotEqual(record, longer);
        Assert.NotEqual(record.TrophiesLength, longer.TrophiesLength);

        // the same size at another time
        File.SetLastWriteTimeUtc(_paths.TrophiesFile, longer.TrophiesWriteUtc.AddSeconds(2));
        AchievementStamp later = Achievements.Stamp(_paths);
        Assert.NotEqual(longer, later);
        Assert.Equal(longer.TrophiesLength, later.TrophiesLength);

        // the save: there, rewritten at the same size, gone
        WriteSave(done: [], seen: []);
        AchievementStamp withSave = Achievements.Stamp(_paths);
        Assert.NotEqual(later, withSave);
        Assert.True(withSave.SaveExists);
        Assert.Equal(new FileInfo(_paths.SaveFile).Length, withSave.SaveLength);
        Assert.Equal(later.TrophiesWriteUtc, withSave.TrophiesWriteUtc);
        File.SetLastWriteTimeUtc(_paths.SaveFile, withSave.SaveWriteUtc.AddSeconds(2));
        AchievementStamp savedAgain = Achievements.Stamp(_paths);
        Assert.NotEqual(withSave, savedAgain);
        Assert.Equal(savedAgain, Achievements.Stamp(_paths));
        File.Delete(_paths.SaveFile);
        Assert.Equal(later, Achievements.Stamp(_paths));
        File.Delete(_paths.TrophiesFile);
        Assert.Equal(none, Achievements.Stamp(_paths));

        // other files of the profile are not the page's business
        File.WriteAllText(_paths.SettingsFile, "vsync=off\n");
        File.WriteAllText(Path.Combine(_paths.DataDirectory, "save.dat.bak"), "x");
        Assert.Equal(none, Achievements.Stamp(_paths));
    }

    [Fact]
    public void ReadingAndStampingNeverWriteIntoTheProfile()
    {
        // the pages poll every two seconds while the game may be writing: not a probe file, not a repaired record, not a touched time
        WriteLog((1, Earned1), (9, null));
        WriteSave(done: Lessons, seen: Lessons);
        File.WriteAllText(_paths.SettingsFile, "trophy_notice=off\n");
        Directory.CreateDirectory(_paths.BackupsDirectory);
        string[] before = ProfileListing();

        for (int i = 0; i < 25; i++)
        {
            Achievements.Stamp(_paths);
            Achievements.Read(_paths, null);
            Achievements.Read(_paths, _root);
            if (i % 5 == 0)
            {
                Achievements.Forget();
            }
        }

        Assert.Equal(before, ProfileListing());

        // nor when the record is damaged, or when the save is
        File.WriteAllBytes(_paths.TrophiesFile, new byte[64]);
        File.WriteAllBytes(_paths.SaveFile, new byte[64]);
        before = ProfileListing();
        for (int i = 0; i < 5; i++)
        {
            Achievements.Forget();
            Assert.True(Achievements.Read(_paths, null).Damaged);
            Achievements.Stamp(_paths);
        }

        Assert.Equal(before, ProfileListing());
        Assert.Equal(new byte[64], File.ReadAllBytes(_paths.TrophiesFile));
    }

    [Fact]
    public void TheRecordLiesBesideTheSaveUnderItsOwnName()
    {
        Assert.Equal("trophies.ini", TrophyLog.FileName);
        Assert.Equal(Path.Combine(_paths.DataDirectory, "trophies.ini"), _paths.TrophiesFile);
        Assert.Equal(Path.GetDirectoryName(_paths.SaveFile), Path.GetDirectoryName(_paths.TrophiesFile));
        Assert.Throws<ArgumentNullException>(() => Achievements.Read(null!, null));
        Assert.Throws<ArgumentNullException>(() => Achievements.Stamp(null!));
    }

    // -- backups ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ABackupCarriesTheRecord()
    {
        WriteSave(done: [], seen: []);
        File.WriteAllText(_paths.SettingsFile, "prompts=xbox\n");
        WriteLog((3, Earned1), (4, null));
        byte[] record = File.ReadAllBytes(_paths.TrophiesFile);

        BackupInfo backup = SaveTools.Backup(_paths, new DateTime(2026, 11, 11, 15, 30, 5))!;
        Assert.Equal(["save.dat", "settings.ini", "trophies.ini"], backup.Files);
        Assert.Equal(record, File.ReadAllBytes(Path.Combine(backup.Path, "trophies.ini")));
        Assert.Equal(record, File.ReadAllBytes(_paths.TrophiesFile));

        // a profile that holds nothing but the record is still worth a backup
        File.Delete(_paths.SaveFile);
        File.Delete(_paths.SettingsFile);
        BackupInfo alone = SaveTools.Backup(_paths, new DateTime(2026, 11, 11, 15, 31, 0))!;
        Assert.Equal(["trophies.ini"], alone.Files);
        Assert.Equal(record, File.ReadAllBytes(Path.Combine(alone.Path, "trophies.ini")));
    }

    [Fact]
    public void RestoringAnOlderSaveLeavesTheRecordAsItIs()
    {
        // an old backup: little progress, a short record
        WriteSave(done: [], seen: [533]);
        WriteLog((1, Earned1));
        BackupInfo old = SaveTools.Backup(_paths, new DateTime(2026, 11, 1, 8, 0, 0))!;
        byte[] oldSave = File.ReadAllBytes(_paths.SaveFile);

        // later play: more progress, a longer record
        WriteSave(done: Lessons, seen: Lessons);
        WriteLog((1, Earned1), (2, Earned2), (28, Earned2), (29, Earned2));
        byte[] record = File.ReadAllBytes(_paths.TrophiesFile);
        DateTime recordTime = File.GetLastWriteTimeUtc(_paths.TrophiesFile);

        BackupInfo undo = SaveTools.Restore(_paths, old, new DateTime(2026, 11, 11, 9, 0, 0))!;

        Assert.Equal(oldSave, File.ReadAllBytes(_paths.SaveFile));
        Assert.Equal(record, File.ReadAllBytes(_paths.TrophiesFile));               // achievements only grow: the older record is not put back
        Assert.Equal(recordTime, File.GetLastWriteTimeUtc(_paths.TrophiesFile));
        Assert.Equal(record, File.ReadAllBytes(Path.Combine(undo.Path, "trophies.ini")));
        Assert.Equal([1, 2, 28, 29], EarnedIds(Read()));

        // a backup that holds a record and no save restores nothing
        string folder = Path.Combine(_paths.BackupsDirectory, "20260101-000000");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "trophies.ini"), "version=1\n5=earlier\n");
        BackupInfo hollow = SaveTools.List(_paths).Single(b => b.Name == "20260101-000000");
        Assert.Throws<FileNotFoundException>(() => SaveTools.Restore(_paths, hollow, DateTime.Now));
        Assert.Equal(record, File.ReadAllBytes(_paths.TrophiesFile));
    }

    [Fact]
    public void ImportingAPs4SaveLeavesTheRecordAsItIs()
    {
        WriteSave(done: Lessons, seen: Lessons);
        WriteLog((1, Earned1), (28, Earned2), (29, Earned2));
        byte[] record = File.ReadAllBytes(_paths.TrophiesFile);
        DateTime recordTime = File.GetLastWriteTimeUtc(_paths.TrophiesFile);
        string image = Path.Combine(_root, "savedata000");
        File.WriteAllBytes(image, SaveFile.ExportPs4Image(Store(done: [], seen: [], slots: 2)));

        (BackupInfo? backup, int slots) = SaveTools.ImportPs4(_paths, image, new DateTime(2026, 11, 11, 10, 0, 0));

        Assert.Equal(2, slots);
        Assert.NotNull(backup);
        Assert.Contains("trophies.ini", backup.Files);
        Assert.Equal(record, File.ReadAllBytes(_paths.TrophiesFile));
        Assert.Equal(recordTime, File.GetLastWriteTimeUtc(_paths.TrophiesFile));     // the replaced save implied nothing the record lacks: not written at all

        // the imported save has no lesson progress; what was earned stays earned
        File.SetLastWriteTimeUtc(_paths.SaveFile, DateTime.UtcNow.AddSeconds(4));
        Assert.Equal([1, 28, 29], EarnedIds(Read()));
    }

    // -- a restore or an import never removes an achievement ------------------------------------------------------------
    // What the page shows from save.dat alone is recorded before save.dat is replaced, and a restore adds the backup's record. The tests say "no game runs"
    // themselves (a game the owner of this machine plays would otherwise make every one of them leave the record alone).

    private static readonly Func<bool> NoGame = () => false;

    private TrophyLog Record()
    {
        TrophyLog record = TrophyLog.ReadFile(_paths.TrophiesFile, out TrophyFileState state);
        Assert.Equal(TrophyFileState.Read, state);
        return record;
    }

    private string WriteImage(SaveStore store)
    {
        string image = Path.Combine(_root, "savedata000");
        File.WriteAllBytes(image, SaveFile.ExportPs4Image(store));
        return image;
    }

    private string[] TemporaryFiles() => [.. Directory.EnumerateFiles(_paths.DataDirectory, "*.tmp").Select(Path.GetFileName)!];

    [Fact]
    public void ImportingAPs4SaveFirstRecordsWhatTheReplacedSaveImplied()
    {
        // a profile from before v0.1.9: all 25 lessons done and seen, no record yet; the page shows 28 and 29 from the save alone
        WriteSave(done: Lessons, seen: Lessons);
        Assert.Equal([28, 29], EarnedIds(Read()));
        string image = WriteImage(Store(done: [], seen: [], slots: 2));

        (BackupInfo? backup, int slots) = SaveTools.ImportPs4(_paths, image, new DateTime(2026, 11, 11, 10, 0, 0), null, NoGame);

        // the imported save implies nothing, and what the page showed stays: recorded without a date
        Assert.Equal(2, slots);
        Assert.Equal(["save.dat"], backup!.Files);                                  // the backup is of the profile as it was, before the record was written
        File.SetLastWriteTimeUtc(_paths.SaveFile, DateTime.UtcNow.AddSeconds(4));
        Assert.Equal([28, 29], EarnedIds(Read()));
        TrophyLog record = Record();
        Assert.Equal([28, 29], record.Ids);
        Assert.All(new[] { 28, 29 }, id => Assert.Null(record.EarnedUtc(id)));
        Assert.EndsWith("\nversion=1\n28=earlier\n29=earlier\n", File.ReadAllText(_paths.TrophiesFile), StringComparison.Ordinal);

        // the same import again: the replaced save implies nothing the record lacks, so the record is not written
        byte[] bytes = File.ReadAllBytes(_paths.TrophiesFile);
        DateTime time = File.GetLastWriteTimeUtc(_paths.TrophiesFile);
        SaveTools.ImportPs4(_paths, image, new DateTime(2026, 11, 11, 10, 5, 0), null, NoGame);
        Assert.Equal(bytes, File.ReadAllBytes(_paths.TrophiesFile));
        Assert.Equal(time, File.GetLastWriteTimeUtc(_paths.TrophiesFile));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void RestoringAnOlderSaveFirstRecordsWhatTheReplacedSaveImpliedAndThePlatinumItCompletes()
    {
        // an old backup: no progress, no record
        WriteSave(done: [], seen: []);
        BackupInfo old = SaveTools.Backup(_paths, new DateTime(2026, 11, 1, 8, 0, 0))!;

        // later: the game recorded the 27 chapters with their dates; the lessons are in the save only (the game has not started since)
        WriteLog([.. Enumerable.Range(1, 27).Select(id => (id, (DateTime?)Earned1.AddHours(id)))]);
        WriteSave(done: Lessons, seen: Lessons);
        Assert.Equal(Enumerable.Range(0, 30), EarnedIds(Read()));

        BackupInfo undo = SaveTools.Restore(_paths, old, new DateTime(2026, 11, 11, 9, 0, 0), null, NoGame)!;

        Assert.Equal(File.ReadAllBytes(Path.Combine(old.Path, "save.dat")), File.ReadAllBytes(_paths.SaveFile));
        Assert.Equal(Enumerable.Range(0, 30), EarnedIds(Read()));                  // nothing the page showed is gone
        TrophyLog record = Record();
        Assert.Equal(Enumerable.Range(0, 30), record.Ids);
        for (int id = 1; id <= 27; id++)
        {
            Assert.Equal(Earned1.AddHours(id), record.EarnedUtc(id));
        }

        Assert.All(new[] { 0, 28, 29 }, id => Assert.Null(record.EarnedUtc(id)));

        // the backup of the replaced save holds the record as it was before
        Assert.Equal([.. Enumerable.Range(1, 27)], TrophyLog.Read(Path.Combine(undo.Path, TrophyLog.FileName), out _).Ids);
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void ASaveChangeThatTakesNothingAwayWritesNoRecord()
    {
        // saves whose bits are the lessons' neighbours and nothing else: they imply no achievement, so a profile without a record does not get one
        WriteSave(done: [532], seen: [558]);
        BackupInfo old = SaveTools.Backup(_paths, new DateTime(2026, 11, 1, 8, 0, 0))!;
        WriteSave(done: [532, 558], seen: [532]);
        SaveTools.Restore(_paths, old, new DateTime(2026, 11, 11, 9, 0, 0), null, NoGame);
        Assert.False(File.Exists(_paths.TrophiesFile));
        SaveTools.ImportPs4(_paths, WriteImage(Store(done: [558], seen: [], slots: 1)), new DateTime(2026, 11, 11, 9, 5, 0), null, NoGame);
        Assert.False(File.Exists(_paths.TrophiesFile));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void WhileAGameRunsARestoreOrAnImportLeavesTheRecordToTheGame()
    {
        // the backup holds a record; the profile has none, and its save implies 28 and 29
        WriteSave(done: [], seen: []);
        WriteLog((5, Earned1));
        BackupInfo old = SaveTools.Backup(_paths, new DateTime(2026, 11, 1, 8, 0, 0))!;
        File.Delete(_paths.TrophiesFile);
        WriteSave(done: Lessons, seen: Lessons);

        // a running game writes the record itself (and recorded what its save implied when it started): neither is added by the launcher
        int asked = 0;
        bool GameRuns()
        {
            asked++;
            return true;
        }

        SaveTools.Restore(_paths, old, new DateTime(2026, 11, 11, 9, 0, 0), null, GameRuns);
        Assert.Equal(File.ReadAllBytes(Path.Combine(old.Path, "save.dat")), File.ReadAllBytes(_paths.SaveFile));
        Assert.False(File.Exists(_paths.TrophiesFile));
        WriteSave(done: Lessons, seen: Lessons);
        SaveTools.ImportPs4(_paths, WriteImage(Store(done: [], seen: [], slots: 1)), new DateTime(2026, 11, 11, 9, 5, 0), null, GameRuns);
        Assert.False(File.Exists(_paths.TrophiesFile));
        Assert.Equal(2, asked);
    }

    [Fact]
    public void RestoringABackupAddsItsRecordToTheProfilesAndKeepsTheEarlierDate()
    {
        // the backup: a save that implies nothing and a record with dates
        WriteSave(done: [], seen: []);
        WriteLog((5, Earned1), (6, Earned2));
        BackupInfo backup = SaveTools.Backup(_paths, new DateTime(2026, 11, 1, 8, 0, 0))!;
        var when = new DateTime(2026, 11, 11, 9, 0, 0);

        // the profile's record was lost: the backup's comes back with its dates
        File.Delete(_paths.TrophiesFile);
        SaveTools.Restore(_paths, backup, when, null, NoGame);
        TrophyLog record = Record();
        Assert.Equal([5, 6], record.Ids);
        Assert.Equal(Earned1, record.EarnedUtc(5));
        Assert.Equal(Earned2, record.EarnedUtc(6));

        // the game rebuilt the record from the save without dates, and earned one more since: the dates come back and nothing is lost
        WriteLog((5, null), (7, Earned2));
        SaveTools.Restore(_paths, backup, when, null, NoGame);
        record = Record();
        Assert.Equal([5, 6, 7], record.Ids);
        Assert.Equal(Earned1, record.EarnedUtc(5));
        Assert.Equal(Earned2, record.EarnedUtc(6));
        Assert.Equal(Earned2, record.EarnedUtc(7));

        // of two dates the earlier one is kept, whichever record holds it
        WriteLog((5, Earned2), (6, Earned1));
        SaveTools.Restore(_paths, backup, when, null, NoGame);
        record = Record();
        Assert.Equal([5, 6], record.Ids);
        Assert.Equal(Earned1, record.EarnedUtc(5));
        Assert.Equal(Earned1, record.EarnedUtc(6));

        // nothing to add: the record is not written
        byte[] bytes = File.ReadAllBytes(_paths.TrophiesFile);
        DateTime time = File.GetLastWriteTimeUtc(_paths.TrophiesFile);
        SaveTools.Restore(_paths, backup, when, null, NoGame);
        Assert.Equal(bytes, File.ReadAllBytes(_paths.TrophiesFile));
        Assert.Equal(time, File.GetLastWriteTimeUtc(_paths.TrophiesFile));

        // a backup whose record is not a record adds nothing (and is not taken for an empty one)
        BackupInfo Made(string name, string text)
        {
            string folder = Path.Combine(_paths.BackupsDirectory, name);
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(backup.Path, "save.dat"), Path.Combine(folder, "save.dat"));
            File.WriteAllText(Path.Combine(folder, TrophyLog.FileName), text);
            return SaveTools.List(_paths).Single(b => b.Name == name);
        }

        SaveTools.Restore(_paths, Made("20260101-000000", "not a record\n"), when, null, NoGame);
        Assert.Equal(bytes, File.ReadAllBytes(_paths.TrophiesFile));
        Assert.Equal(time, File.GetLastWriteTimeUtc(_paths.TrophiesFile));

        // and two records that together hold the other 29 earn the platinum
        WriteLog([.. Enumerable.Range(1, 20).Select(id => (id, (DateTime?)Earned1))]);
        SaveTools.Restore(_paths, Made("20260102-000000", "version=1\n" + string.Concat(Enumerable.Range(18, 12).Select(id => $"{id}=earlier\n"))), when, null, NoGame);
        record = Record();
        Assert.Equal(Enumerable.Range(0, 30), record.Ids);
        Assert.Null(record.EarnedUtc(0));
        Assert.Equal(Earned1, record.EarnedUtc(18));
        Assert.Null(record.EarnedUtc(21));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void ARecordThatCouldNotBeOpenedIsNeverWrittenOver()
    {
        if (!OperatingSystem.IsWindows())
        {
            // elsewhere another program's handle never makes an open fail
            return;
        }

        // (Through SaveTools a record held like this already stops the restore at its backup, which cannot copy it; the two steps are tried directly.)
        WriteLog((5, Earned1));
        byte[] bytes = File.ReadAllBytes(_paths.TrophiesFile);
        DateTime time = File.GetLastWriteTimeUtc(_paths.TrophiesFile);
        WriteSave(done: Lessons, seen: Lessons);
        string backup = Path.Combine(_root, "backup");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, TrophyLog.FileName), "version=1\n6=2026-11-12T13:03:11Z\n");

        // Another program holds the record without sharing and lets go as soon as a writer's temporary file appears beside it (or once the step has returned,
        // when none does): a write that came after the failed reading would then go through, over what nobody could read.
        foreach ((string what, Func<bool> change) in new (string, Func<bool>)[]
        {
            ("the replaced save's achievements", () => Achievements.KeepBeforeSaveChange(_paths, null).Count > 0),
            ("the backup's record", () => Achievements.UniteBackupRecord(_paths, backup)),
        })
        {
            bool wrote = HeldUntilAWriteBegins(change, out bool writeBegan);
            Assert.False(wrote, what);
            Assert.False(writeBegan, $"{what}: a write was begun over a record nobody could read");
            Assert.Equal(bytes, File.ReadAllBytes(_paths.TrophiesFile));
            Assert.Equal(time, File.GetLastWriteTimeUtc(_paths.TrophiesFile));
        }

        // once nothing holds it, both add what they have to what it holds
        Assert.Equal([28, 29], Achievements.KeepBeforeSaveChange(_paths, null));
        Assert.True(Achievements.UniteBackupRecord(_paths, backup));
        TrophyLog record = Record();
        Assert.Equal([5, 6, 28, 29], record.Ids);
        Assert.Equal(Earned1, record.EarnedUtc(5));
        Assert.Empty(TemporaryFiles());
    }

    // runs change while another thread holds trophies.ini without sharing; the holder lets go when a temporary file of a write appears, or once change has
    // returned (after a last look for one). Not after a fixed time: a change on a loaded machine can take longer than any, read the record after the holder let
    // go, and rightly write it. The bound only keeps a change that never returns from holding the file for ever
    private bool HeldUntilAWriteBegins(Func<bool> change, out bool writeBegan)
    {
        var bound = TimeSpan.FromSeconds(60);
        var held = new FileStream(_paths.TrophiesFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        int began = 0;
        int returned = 0;
        var holder = new Thread(() =>
        {
            try
            {
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    if (Directory.EnumerateFiles(_paths.DataDirectory, TrophyLog.FileName + ".*.tmp").Any())
                    {
                        Volatile.Write(ref began, 1);
                        break;
                    }

                    if (Volatile.Read(ref returned) == 1 || clock.Elapsed > bound)
                    {
                        break;
                    }

                    Thread.Sleep(1);
                }
            }
            finally
            {
                held.Dispose();
            }
        });
        holder.Start();
        bool wrote;
        try
        {
            wrote = change();
        }
        finally
        {
            Volatile.Write(ref returned, 1);
        }

        Assert.True(holder.Join(bound), "the holder never let go");
        writeBegan = Volatile.Read(ref began) == 1;
        return wrote;
    }

    [Fact]
    public void AFolderOfTheRecordsNameIsLeftAloneAndTheSaveChangeGoesThrough()
    {
        WriteSave(done: [], seen: []);
        BackupInfo old = SaveTools.Backup(_paths, new DateTime(2026, 11, 1, 8, 0, 0))!;
        WriteSave(done: Lessons, seen: Lessons);
        Directory.CreateDirectory(_paths.TrophiesFile);

        SaveTools.Restore(_paths, old, new DateTime(2026, 11, 11, 9, 0, 0), null, NoGame);
        Assert.Equal(File.ReadAllBytes(Path.Combine(old.Path, "save.dat")), File.ReadAllBytes(_paths.SaveFile));
        WriteSave(done: Lessons, seen: Lessons);
        (_, int slots) = SaveTools.ImportPs4(_paths, WriteImage(Store(done: [], seen: [], slots: 3)), new DateTime(2026, 11, 11, 9, 5, 0), null, NoGame);
        Assert.Equal(3, slots);

        Assert.True(Directory.Exists(_paths.TrophiesFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_paths.TrophiesFile));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void ARecordThatCannotBeWrittenDoesNotStopARestoreOrAnImport()
    {
        if (!OperatingSystem.IsWindows())
        {
            // elsewhere a rename over a file somebody holds goes through: nothing refuses the write
            return;
        }

        WriteSave(done: [], seen: []);
        WriteLog((5, Earned1));
        BackupInfo old = SaveTools.Backup(_paths, new DateTime(2026, 11, 1, 8, 0, 0))!;
        byte[] record = File.ReadAllBytes(_paths.TrophiesFile);

        // the replaced save implies 28 and 29, which the record lacks, and another program holds the record and does not let it be replaced: its write fails
        // (a record that is only marked read-only is written since FIXES D11 G-3: TrophyLog takes the mark off)
        WriteSave(done: Lessons, seen: Lessons);
        using (new FileStream(_paths.TrophiesFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            SaveTools.Restore(_paths, old, new DateTime(2026, 11, 11, 9, 0, 0), null, NoGame);
            Assert.Equal(File.ReadAllBytes(Path.Combine(old.Path, "save.dat")), File.ReadAllBytes(_paths.SaveFile));
            WriteSave(done: Lessons, seen: Lessons);
            (_, int slots) = SaveTools.ImportPs4(_paths, WriteImage(Store(done: [], seen: [], slots: 2)), new DateTime(2026, 11, 11, 9, 5, 0), null, NoGame);
            Assert.Equal(2, slots);
            Assert.Equal(record, File.ReadAllBytes(_paths.TrophiesFile));
            Assert.Empty(TemporaryFiles());
        }
    }

    // -- the settings key ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("trophy_notice=off", false)]
    [InlineData("trophy_notice=on", true)]
    [InlineData("TROPHY_NOTICE = Off ", false)]
    [InlineData("trophy_notice=true", true)]
    [InlineData("trophy_notice=0", false)]
    [InlineData("trophy_notice=maybe", null)]
    [InlineData("trophy_notice=", null)]
    [InlineData("# trophy_notice=off", null)]
    [InlineData("trophynotice=off", null)]
    [InlineData("screenshot_notice=off", null)]
    [InlineData("trophy_sound=on", null)]
    [InlineData("", null)]
    public void TheNoticeKeyReadsOnOrOffAndIsOffWhenAbsent(string text, bool? expected)
    {
        // v0.1.9, the owner's choice: a file that does not say means no notice (the achievement is recorded all the same)
        AppSettingsData data = AppSettings.Parse(text);
        Assert.Equal(expected, data.TrophyNotice);
        Assert.Equal(expected ?? false, data.TrophyNoticeOn);
    }

    [Theory]
    [InlineData("trophy_sound=off", false)]
    [InlineData("trophy_sound=on", true)]
    [InlineData("TROPHY_SOUND = On ", true)]
    [InlineData("trophy_sound=yes", true)]
    [InlineData("trophy_sound=0", false)]
    [InlineData("trophy_sound=loud", null)]
    [InlineData("trophy_sound=", null)]
    [InlineData("# trophy_sound=on", null)]
    [InlineData("trophysound=on", null)]
    [InlineData("trophy_notice=on", null)]
    [InlineData("", null)]
    public void TheSoundKeyReadsOnOrOffAndIsOffWhenAbsent(string text, bool? expected)
    {
        // read exactly as trophy_notice is; its own key, and off when the file does not say
        AppSettingsData data = AppSettings.Parse(text);
        Assert.Equal(expected, data.TrophySound);
        Assert.Equal(expected ?? false, data.TrophySoundOn);
    }

    [Fact]
    public void TheTwoSwitchesWordsSayWhatTheyDoAndThatTheSoundNeedsTheNotice()
    {
        // v0.1.9: the notice switch and, under it, the sound switch; both off until the player turns them on, so neither line may promise an "on" default.
        // The owner's words (FIXES D9): "Notify", not "Notice", on the card and in both rows; the sound's line names the switch it needs by its label
        Assert.Equal("NOTIFY", LauncherTexts.AchievementsNoticeCaps);
        Assert.Equal("Notify in the game", LauncherTexts.AchievementsNoticeLabel);
        Assert.Equal("Sound when notified", LauncherTexts.AchievementsSoundLabel);
        Assert.Equal(
            "Shows a short message at the top right of the game when you earn an achievement. Achievements are recorded whether it is on or off.",
            LauncherTexts.AchievementsNoticeDescription);
        Assert.Equal("Plays a soft chime with the message. Needs \"Notify in the game\" to be on.", LauncherTexts.AchievementsSoundDescription);
        Assert.Contains($"\"{LauncherTexts.AchievementsNoticeLabel}\"", LauncherTexts.AchievementsSoundDescription, StringComparison.Ordinal);
        foreach (string words in new[] { LauncherTexts.AchievementsNoticeCaps, LauncherTexts.AchievementsNoticeLabel, LauncherTexts.AchievementsSoundLabel })
        {
            Assert.DoesNotContain("notice", words, StringComparison.OrdinalIgnoreCase);
        }

        foreach (string words in new[] { LauncherTexts.AchievementsNoticeDescription, LauncherTexts.AchievementsSoundDescription })
        {
            Assert.DoesNotContain("notice", words, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("default", words, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("silently", words, StringComparison.OrdinalIgnoreCase);     // "silent" is the sound's business now
            Assert.EndsWith(".", words, StringComparison.Ordinal);
            Assert.DoesNotContain("  ", words, StringComparison.Ordinal);
        }

        // one line each on the page: short enough for the switch row's description
        Assert.InRange(LauncherTexts.AchievementsSoundDescription.Length, 20, LauncherTexts.AchievementsNoticeDescription.Length);
        Assert.NotEqual(LauncherTexts.AchievementsNoticeLabel, LauncherTexts.AchievementsSoundLabel);
    }

    [Fact]
    public void NothingInTheFileMeansNoNoticeAndNoSound()
    {
        Assert.False(new AppSettingsData().TrophyNoticeOn);
        Assert.False(new AppSettingsData().TrophySoundOn);
        Assert.False(AppSettings.Load(_paths.SettingsFile).TrophyNoticeOn);          // no file at all
        Assert.False(AppSettings.Load(_paths.SettingsFile).TrophySoundOn);

        // each switch is its own: the sound key says nothing of the notice, and the other way round
        AppSettingsData soundOnly = AppSettings.Parse("trophy_sound=on\n");
        Assert.Equal((false, true), (soundOnly.TrophyNoticeOn, soundOnly.TrophySoundOn));
        AppSettingsData noticeOnly = AppSettings.Parse("trophy_notice=on\n");
        Assert.Equal((true, false), (noticeOnly.TrophyNoticeOn, noticeOnly.TrophySoundOn));

        // the comment the file carries says so too, in the guide's word for what appears (FIXES D10 Q-1: "message", not "notice")
        string text = AppSettings.Format(new AppSettingsData(TrophyNotice: true, TrophySound: true));
        Assert.Contains("# trophy_notice: on shows a message in the game when an achievement is earned; off (the default)", text, StringComparison.Ordinal);
        Assert.Contains("# trophy_sound: on plays a short chime as an achievement's message appears, only while trophy_notice is on; off (the default)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("on (the default) shows a message", text, StringComparison.Ordinal);
        Assert.DoesNotContain("on (the default) shows a notice", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trophy_notice")]
    [InlineData("trophy_sound")]
    public void EachKeyRoundTripsAndStaysOutOfTheFileWhileNobodySetIt(string key)
    {
        string path = _paths.SettingsFile;
        Func<bool, AppSettingsData> make = key == "trophy_notice" ? v => new AppSettingsData(TrophyNotice: v) : v => new AppSettingsData(TrophySound: v);
        Func<AppSettingsData, bool?> get = key == "trophy_notice" ? d => d.TrophyNotice : d => d.TrophySound;
        Assert.DoesNotContain(key, AppSettings.Format(new AppSettingsData()), StringComparison.Ordinal);
        foreach (bool value in new[] { false, true })
        {
            AppSettingsData data = make(value);
            Assert.Contains("\n" + key + "=" + (value ? "on" : "off") + "\n", AppSettings.Format(data), StringComparison.Ordinal);
            Assert.Equal(value, get(AppSettings.Parse(AppSettings.Format(data))));
            Assert.True(AppSettings.Save(path, data));
            Assert.Equal(value, get(AppSettings.Load(path)));
        }

        // the keys are their own: not the screenshot notice's, and not each other's
        Assert.Null(get(AppSettings.Parse(AppSettings.Format(new AppSettingsData(ScreenshotNotice: false)))));
        Assert.Null(AppSettings.Parse(AppSettings.Format(make(false))).ScreenshotNotice);
        AppSettingsData both = AppSettings.Parse(AppSettings.Format(new AppSettingsData(TrophyNotice: false, TrophySound: true)));
        Assert.Equal((false, true), (both.TrophyNotice, both.TrophySound));
        both = AppSettings.Parse(AppSettings.Format(new AppSettingsData(TrophyNotice: true, TrophySound: false)));
        Assert.Equal((true, false), (both.TrophyNotice, both.TrophySound));

        // the sound's line follows the notice's
        string text = AppSettings.Format(new AppSettingsData(TrophyNotice: true, TrophySound: true));
        Assert.True(text.IndexOf("\ntrophy_notice=on\n", StringComparison.Ordinal) < text.IndexOf("\ntrophy_sound=on\n", StringComparison.Ordinal));
    }

    [Fact]
    public void AWriterThatChangesAnotherKeyKeepsTheNoticeKey()
    {
        // the launcher's Display page, the game's own prompts key: each reads the file, changes its key and writes it back
        string path = _paths.SettingsFile;
        File.WriteAllText(path, "prompts=xbox\n# a comment\ntrophy_notice=off\ntrophy_sound=on\nvsync=on\n");
        Assert.True(AppSettings.Update(path, s => s with { VSync = false }));
        Assert.True(AppSettings.Update(path, s => s with { ScreenshotNotice = true }));
        Assert.True(AppSettings.Update(path, s => s with { Fullscreen = true, WindowWidth = 1280, WindowHeight = 720 }));
        AppSettingsData now = AppSettings.Load(path);
        Assert.False(now.TrophyNotice);
        Assert.True(now.TrophySound);
        Assert.False(now.VSync);
        Assert.True(now.ScreenshotNotice);
        Assert.Contains("trophy_notice=off", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("trophy_sound=on", File.ReadAllText(path), StringComparison.Ordinal);

        // and the other way round: the page's switches keep everybody else's keys, and each other's
        Assert.True(AppSettings.Update(path, s => s with { TrophyNotice = true }));
        AppSettingsData after = AppSettings.Load(path);
        Assert.Equal(now with { TrophyNotice = true }, after);
        Assert.True(AppSettings.Update(path, s => s with { TrophySound = false }));
        Assert.Equal(after with { TrophySound = false }, AppSettings.Load(path));

        // a file that never had the keys does not gain them from another writer
        File.WriteAllText(path, "prompts=xbox\n");
        Assert.True(AppSettings.Update(path, s => s with { VSync = false }));
        Assert.Null(AppSettings.Load(path).TrophyNotice);
        Assert.Null(AppSettings.Load(path).TrophySound);
        Assert.DoesNotContain("trophy_notice", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("trophy_sound", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
