using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tsuki.Engine.Save;
using Tsuki.Engine.Trophies;
using Tsuki.Launcher.Avalonia;
using Tsuki.Launcher.Avalonia.Views;
using Tsuki.Launcher.Core;
using Path = System.IO.Path;
using Shape = Avalonia.Controls.Shapes.Path;

namespace Tsuki.Tests.Avalonia;

/// <summary>
/// The Achievements page of the macOS / Linux launcher as the player sees it: the window is shown, the profile holds a sample trophies.ini, and the tests look at
/// the real controls. The record and the pictures are made here; nothing of the game is used: the game folder is an empty stand-in, and the icons of the
/// achievements come from <see cref="IconDesk"/>, which gives each id a flat picture of its own and counts who asked. A grade is shown by its emblem, a cup the
/// launcher's core draws in the tiles' frame.
/// </summary>
public sealed class AchievementsPageTests : IDisposable
{
    // 12 earned: the four bronze ones and three silver ones from progress made before achievements were recorded, five more with their dates
    private const string Sample =
        "# sample record of the launcher tests\nversion=1\n1=earlier\n2=earlier\n3=earlier\n4=earlier\n5=earlier\n6=earlier\n7=earlier\n" +
        "8=2026-11-09T18:41:07Z\n9=2026-11-09T20:02:55Z\n10=2026-11-10T19:15:30Z\n11=2026-11-11T08:03:11Z\n28=2026-11-10T21:47:02Z\n";

    private readonly string _root = TestFolders.Temp("tsuki-v019-achievements-");
    private readonly LauncherPaths _paths;

    public AchievementsPageTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "profile"));
        Directory.CreateDirectory(_paths.DataDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ThePageShowsTheRecordInItsSummaryItsTabsAndItsRows()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        var desk = new IconDesk();
        UnixLauncherModel model = Model(icons: desk);
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);

            // the summary: the share, the count, the bar and the four grades with "earned / total" under their emblems: each the core's emblem of its grade, at
            // twice the size it is drawn at (made on a worker once the page is shown)
            Assert.Equal("40%", page.PercentText.Text);
            Assert.Equal("12 of 30 earned", page.CountText.Text);
            Assert.Equal((12, 30), (page.Progress.Value, page.Progress.Maximum));
            Control[] tallies = [.. page.GradeList.GetRealizedContainers()];
            Assert.Equal(["0 / 1 Platinum", "0 / 4 Gold", "8 / 21 Silver", "4 / 4 Bronze"], tallies.Select(tally => string.Join(" ", Texts(tally))));
            TrophyGrade[] grades = [TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze];
            for (int i = 0; i < grades.Length; i++)
            {
                Image emblem = EmblemImage(tallies[i]);
                Assert.True(emblem.IsEffectivelyVisible, $"the {grades[i]} has no emblem");
                Assert.Equal((UnixLauncherModel.GradeEmblemSize, UnixLauncherModel.GradeEmblemSize), (emblem.Bounds.Width, emblem.Bounds.Height));
                Assert.Equal(new PixelSize(UnixLauncherModel.EmblemPixels, UnixLauncherModel.EmblemPixels), Emblem(tallies[i]).PixelSize);
                Assert.Equal(EmblemPixels(grades[i]), Pixels(Emblem(tallies[i])));
            }

            Assert.Equal(4, tallies.Select(Emblem).Distinct().Count());
            Assert.Equal(["All (30)", "Earned (12)", "Locked (18)"], Texts(page.Tabs));

            // one row each, the platinum first: name, how it is earned, the grade, and "Locked", "Earned" (no date is known) or the local date and time
            Control[] rows = Rows(page);
            Assert.Equal(30, rows.Length);
            Assert.Equal(["All Achievements", "Earn every other achievement.", "Platinum", "Locked"], Texts(rows[0]));
            Assert.Equal(["Day 1", "Finish day 1.", "Bronze", "Earned"], Texts(rows[1]));
            Assert.Equal(["Arcueid's route, day 8", "Finish day 8 on Arcueid's route.", "Silver", Local(2026, 11, 9, 18, 41)], Texts(rows[8]));
            Assert.Equal(["Arcueid's route, day 12", "Finish day 12 on Arcueid's route.", "Silver", "Locked"], Texts(rows[12]));
            Assert.Equal(["First Lesson", "Attend one of Ciel's lessons.", "Silver", Local(2026, 11, 10, 21, 47)], Texts(rows[28]));
            Assert.Equal(["Every Lesson", "Attend all 25 of Ciel's lessons.", "Gold", "Locked"], Texts(rows[29]));

            // the words of a locked one are dimmed, with a lock for the earned one's check
            Assert.NotEqual(Ink(Name(rows[1])), Ink(Name(rows[0])));
            Assert.Equal(Color.Parse("#E9EDF8"), Ink(Name(rows[1])));
            Assert.NotEqual(StateSign(rows[1]).Data!.Bounds, StateSign(rows[0]).Data!.Bounds);
            Assert.Equal(Color.Parse("#7ED6A8"), ((ISolidColorBrush)StateSign(rows[1]).Stroke!).Color);

            // without a game folder no icon can be made, and nothing is asked for: an earned row shows the emblem of its grade (one bitmap for the rows of a
            // grade, another for another grade), its grade's word in the grade's colour, and the page says where the names and pictures come from
            Control[] earned = [.. rows.Where((_, id) => id is (>= 1 and <= 11) or 28)];
            Assert.Empty(desk.Asked);
            Assert.All(earned, row => Assert.True(TileImage(row).IsEffectivelyVisible, "an earned row has no tile"));
            Assert.Same(Tile(rows[1]), Tile(rows[4]));          // bronze and bronze
            Assert.NotSame(Tile(rows[1]), Tile(rows[8]));       // bronze and silver
            Assert.Same(Tile(rows[8]), Tile(rows[28]));         // silver and silver
            foreach ((int id, TrophyGrade grade) in new[] { (1, TrophyGrade.Bronze), (8, TrophyGrade.Silver), (28, TrophyGrade.Silver) })
            {
                Assert.Equal(TilePixels(Achievements.EmblemIcon(grade)), Pixels(Tile(rows[id])));
            }

            Assert.Equal(Color.Parse("#CB8F62"), Ink(GradeText(rows[1])));
            Assert.Equal(Color.Parse("#C3CBDB"), Ink(GradeText(rows[8])));

            // a locked row shows the padlock tile of its grade, its grade's word dimmed: one picture for the rows of a grade, another ring for another grade
            Control[] locked = [.. rows.Except(earned)];
            Assert.Equal(18, locked.Length);
            Assert.All(locked, row => Assert.True(TileImage(row).IsEffectivelyVisible, "a locked row has no tile"));
            Assert.All(locked, row => Assert.Equal(Color.Parse("#6C7699"), Ink(GradeText(row))));
            Assert.Same(Tile(rows[12]), Tile(rows[13]));
            Assert.NotSame(Tile(rows[12]), Tile(rows[0]));
            Assert.NotSame(Tile(rows[12]), Tile(rows[14]));
            foreach ((int id, TrophyGrade grade) in new[] { (0, TrophyGrade.Platinum), (12, TrophyGrade.Silver), (14, TrophyGrade.Gold) })
            {
                Assert.Equal(TilePixels(Achievements.LockedIcon(grade)), Pixels(Tile(rows[id])));
            }

            // the padlock (its own grey) on the dark ground, inside a ring that is the grade's colour at about a third of its strength; clear corners
            byte[] padlock = Pixels(Tile(rows[12]));
            Assert.True(Count(padlock, (0x80, 0x67, 0x5B, 0xFF)) > 200, "the locked tile has no padlock");
            Assert.Equal((0x2B, 0x1C, 0x16, 0xFF), At(padlock, 8, UnixLauncherModel.TilePixels / 2));
            Assert.InRange(At(padlock, UnixLauncherModel.TilePixels / 2, 1).A, 80, 100);
            Assert.Equal(default, At(padlock, 0, 0));
            Assert.True(page.NoGameNote.IsEffectivelyVisible);
            Assert.False(page.DamagedBox.IsEffectivelyVisible);
            Assert.Equal(LauncherTexts.AchievementsFileNote, page.FileNote.Text);

            // the tabs filter
            page.Tabs.SelectedIndex = 1;
            LauncherHeadlessTests.Pump(window);
            Assert.Equal(12, Rows(page).Length);
            Assert.DoesNotContain(Rows(page), row => Texts(row)[3] == "Locked");
            Assert.Equal(["Day 1", "First Lesson"], new[] { Texts(Rows(page)[0])[0], Texts(Rows(page)[^1])[0] });
            page.Tabs.SelectedIndex = 2;
            LauncherHeadlessTests.Pump(window);
            Assert.Equal(18, Rows(page).Length);
            Assert.All(Rows(page), row => Assert.Equal("Locked", Texts(row)[3]));
            Assert.False(page.EmptyText.IsEffectivelyVisible);

            // the tab is still chosen when the page is left and shown again
            model.Page = LauncherPage.Saves;
            page = Open(window, model);
            Assert.Equal(2, page.Tabs.SelectedIndex);
            Assert.Equal(18, Rows(page).Length);

            // "Game files" goes where the folder is chosen
            Button link = page.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, LauncherTexts.AchievementsOpenGameFiles));
            link.Command!.Execute(link.CommandParameter);
            Assert.Equal(LauncherPage.GameFiles, model.Page);
            window.Close();
        });
    }

    [Fact]
    public async Task AFreshProfileHasNothingEarnedAndADamagedRecordIsSaid()
    {
        UnixLauncherModel model = Model();
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            Assert.Equal("0%", page.PercentText.Text);
            Assert.Equal("0 of 30 earned", page.CountText.Text);
            Assert.Equal(["All (30)", "Earned (0)", "Locked (30)"], Texts(page.Tabs));
            Assert.Equal(30, Rows(page).Length);
            Assert.All(Rows(page), row => Assert.Equal("Locked", Texts(row)[3]));
            Assert.False(page.EmptyText.IsEffectivelyVisible);

            // an empty tab says why it is empty
            page.Tabs.SelectedIndex = 1;
            LauncherHeadlessTests.Pump(window);
            Assert.Empty(Rows(page));
            Assert.True(page.EmptyText.IsEffectivelyVisible);
            Assert.Equal(LauncherTexts.AchievementsNothingEarned, page.EmptyText.Text);

            // a record that cannot be read: the page says so
            Assert.False(page.DamagedBox.IsEffectivelyVisible);
            File.WriteAllBytes(_paths.TrophiesFile, [0, 1, 2, 3, 0xFF, 0xFE, 0, 0]);
            await window.WatchAchievements();
            LauncherHeadlessTests.Pump(window);
            Assert.True(page.DamagedBox.IsEffectivelyVisible);
            Assert.Contains(page.DamagedBox.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == LauncherTexts.AchievementsDamagedNote);
            window.Close();
        });
    }

    [Fact]
    public async Task TheNoticeSwitchWritesTrophyNoticeAndShowsWhatTheGameLeft()
    {
        UnixLauncherModel model = Model();
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);

            // off while settings.ini says nothing (the player turns notices on), and showing the page writes nothing
            Assert.False(page.NoticeSwitch.IsChecked);
            Assert.False(model.TrophyNotice);
            Assert.False(File.Exists(_paths.SettingsFile), "showing the page wrote settings.ini");
            Assert.Contains(page.NoticeCard.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == LauncherTexts.AchievementsNoticeLabel && text.IsEffectivelyVisible);
            page.NoticeSwitch.IsChecked = true;
            LauncherHeadlessTests.Pump(window);
            Assert.Contains("trophy_notice=on", File.ReadAllLines(_paths.SettingsFile));
            Assert.True(model.TrophyNotice);
            page.NoticeSwitch.IsChecked = false;
            LauncherHeadlessTests.Pump(window);
            Assert.Contains("trophy_notice=off", File.ReadAllLines(_paths.SettingsFile));
            Assert.False(model.TrophyNotice);

            // the game's Config row writes the same key: back in front, the switch shows it
            File.WriteAllText(_paths.SettingsFile, "trophy_notice=on\n");
            File.SetLastWriteTimeUtc(_paths.SettingsFile, DateTime.UtcNow.AddMinutes(1));
            model.ReadProfileAgain(onlyIfChanged: true);
            LauncherHeadlessTests.Pump(window);
            Assert.True(page.NoticeSwitch.IsChecked);

            // and a settings.ini the game wrote without the key (one from before 0.1.9) is off again
            File.WriteAllText(_paths.SettingsFile, "prompts=xbox\n");
            File.SetLastWriteTimeUtc(_paths.SettingsFile, DateTime.UtcNow.AddMinutes(2));
            model.ReadProfileAgain(onlyIfChanged: true);
            LauncherHeadlessTests.Pump(window);
            Assert.False(page.NoticeSwitch.IsChecked);
            window.Close();
        });
    }

    [Fact]
    public void BothSwitchesAreOffUnlessSettingsIniSaysOnAndReadingThemWritesNothing()
    {
        // no settings.ini at all
        UnixLauncherModel fresh = Model();
        Assert.Equal((false, false), (fresh.TrophyNotice, fresh.TrophySound));
        Assert.False(File.Exists(_paths.SettingsFile));

        // each switch shows its own key, the other's does not turn it on; what the file says is read as the game reads it, and left as it is
        foreach ((string text, bool notice, bool sound) in new[]
                 {
                     ("prompts=xbox\n", false, false), ("trophy_notice=on\n", true, false), ("trophy_sound=yes\n", false, true),
                     ("trophy_notice=1\ntrophy_sound=on\n", true, true), ("trophy_notice=off\ntrophy_sound=off\n", false, false),
                 })
        {
            File.WriteAllText(_paths.SettingsFile, text);
            UnixLauncherModel model = Model();
            Assert.Equal((text, notice, sound), (text, model.TrophyNotice, model.TrophySound));
            Assert.Equal(text, File.ReadAllText(_paths.SettingsFile));
        }
    }

    [Fact]
    public async Task TheSoundSwitchUnderTheNoticeWritesTrophySoundKeepsTheOtherKeysAndFollowsTheGame()
    {
        // the player's other keys and the notice on; nothing yet about the sound
        File.WriteAllText(_paths.SettingsFile, "prompts=xbox\ntrophy_notice=on\n");
        UnixLauncherModel model = Model();
        await LauncherHeadlessTests.Run(async () =>
        {
            // the test makes the timer's looks itself (WatchAchievements is what the timer calls): the timer of this window does not look by itself
            MainWindow window = LauncherHeadlessTests.Show(model, TimeSpan.FromHours(1));
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            var raised = new List<string?>();
            model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            // a row of its own right under the notice's, with the words both launchers share; off while settings.ini says nothing of it, the notice on
            Assert.True(page.NoticeSwitch.IsChecked);
            Assert.False(page.SoundSwitch.IsChecked);
            Assert.False(model.TrophySound);
            Assert.True(page.SoundRow.IsEffectivelyVisible && page.SoundSwitch.IsEffectivelyVisible);
            Assert.Equal([LauncherTexts.AchievementsNoticeLabel, LauncherTexts.AchievementsNoticeDescription], Texts(page.NoticeRow));
            Assert.Equal([LauncherTexts.AchievementsSoundLabel, LauncherTexts.AchievementsSoundDescription], Texts(page.SoundRow));
            StackPanel card = Assert.IsType<StackPanel>(page.SoundRow.Parent);
            Assert.Same(card, page.NoticeRow.Parent);
            Assert.Equal(card.Children.IndexOf(page.NoticeRow) + 1, card.Children.IndexOf(page.SoundRow));
            Assert.True(page.SoundRow.TranslatePoint(default, page.NoticeRow)!.Value.Y >= page.NoticeRow.Bounds.Height - 0.5, "the sound's row is not under the notice's");

            // turned on: trophy_sound=on, the notice and the prompts as the file had them
            page.SoundSwitch.IsChecked = true;
            LauncherHeadlessTests.Pump(window);
            string[] lines = File.ReadAllLines(_paths.SettingsFile);
            Assert.Contains("trophy_sound=on", lines);
            Assert.Contains("trophy_notice=on", lines);
            Assert.Contains("prompts=xbox", lines);
            Assert.True(model.TrophySound);
            Assert.True(page.NoticeSwitch.IsChecked);
            Assert.Contains(nameof(model.TrophySound), raised);

            // turned off: off is written, the other keys stay
            page.SoundSwitch.IsChecked = false;
            LauncherHeadlessTests.Pump(window);
            lines = File.ReadAllLines(_paths.SettingsFile);
            Assert.Contains("trophy_sound=off", lines);
            Assert.DoesNotContain("trophy_sound=on", lines);
            Assert.Contains("trophy_notice=on", lines);
            Assert.Contains("prompts=xbox", lines);
            Assert.False(model.TrophySound);

            // set on the model rather than by a click: the switch shows it, its name is raised
            raised.Clear();
            model.TrophySound = true;
            LauncherHeadlessTests.Pump(window);
            Assert.True(page.SoundSwitch.IsChecked);
            Assert.Contains("trophy_sound=on", File.ReadAllLines(_paths.SettingsFile));
            Assert.Contains(nameof(model.TrophySound), raised);

            // the game runs beside the window and its Config row "Achievement Sound" writes off, its other keys kept: the timer's look shows it
            File.WriteAllText(_paths.SettingsFile, "prompts=xbox\ntrophy_notice=on\ntrophy_sound=off\n");
            File.SetLastWriteTimeUtc(_paths.SettingsFile, DateTime.UtcNow.AddMinutes(1));
            raised.Clear();
            await window.WatchAchievements();
            LauncherHeadlessTests.Pump(window);
            Assert.False(model.TrophySound);
            Assert.False(page.SoundSwitch.IsChecked);
            Assert.True(page.NoticeSwitch.IsChecked);
            Assert.Contains(nameof(model.TrophySound), raised);

            // a look that finds settings.ini as it was says nothing
            raised.Clear();
            await window.WatchAchievements();
            Assert.DoesNotContain(nameof(model.TrophySound), raised);

            // the game writes on, and the window comes to the front: the switch shows it
            File.WriteAllText(_paths.SettingsFile, "prompts=xbox\ntrophy_notice=on\ntrophy_sound=on\n");
            File.SetLastWriteTimeUtc(_paths.SettingsFile, DateTime.UtcNow.AddMinutes(2));
            raised.Clear();
            model.ReadProfileAgain(onlyIfChanged: true);
            LauncherHeadlessTests.Pump(window);
            Assert.True(page.SoundSwitch.IsChecked);
            Assert.Contains(nameof(model.TrophySound), raised);

            // a settings.ini without the key (the game cleared it, or one from before 0.1.9): off again, on the timer's look
            File.WriteAllText(_paths.SettingsFile, "prompts=xbox\ntrophy_notice=on\n");
            File.SetLastWriteTimeUtc(_paths.SettingsFile, DateTime.UtcNow.AddMinutes(3));
            await window.WatchAchievements();
            LauncherHeadlessTests.Pump(window);
            Assert.False(page.SoundSwitch.IsChecked);
            Assert.True(page.NoticeSwitch.IsChecked);
            window.Close();
        });
    }

    [Fact]
    public async Task ThePageFollowsTheRecordWhileItIsOpenAndKeepsItsPlace()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        UnixLauncherModel model = Model();
        await LauncherHeadlessTests.Run(async () =>
        {
            // the window looks every two seconds; this one faster
            Assert.Equal(TimeSpan.FromSeconds(2), MainWindow.AchievementsInterval);
            var window = new MainWindow(model) { Width = 800, Height = 460, AchievementsWatchInterval = TimeSpan.FromMilliseconds(40) };
            Assert.False(window.WatchesAchievements);
            window.Show();
            LauncherHeadlessTests.Pump(window);
            Assert.True(window.WatchesAchievements);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            page.Tabs.SelectedIndex = 2;
            LauncherHeadlessTests.Pump(window);
            page.Scroll.Offset = new Vector(0, 420);
            LauncherHeadlessTests.Pump(window);
            Assert.Equal(420, page.Scroll.Offset.Y);
            Assert.Equal("Arcueid's route, day 12", Texts(Rows(page)[1])[0]);

            // the game earns one and replaces the record: the page shows it by itself, where it was scrolled to and on the tab that was chosen
            File.WriteAllText(_paths.TrophiesFile, Sample + "12=2026-11-11T09:30:00Z\n");
            await LauncherHeadlessTests.Until(window, () => model.AchievementsEarned == 13);
            Assert.Equal("13 of 30 earned", page.CountText.Text);
            Assert.Equal("43%", page.PercentText.Text);
            Assert.Equal(["All (30)", "Earned (13)", "Locked (17)"], Texts(page.Tabs));
            Assert.Equal(2, page.Tabs.SelectedIndex);
            Assert.Equal(17, Rows(page).Length);
            Assert.Equal("Arcueid's route, day 13", Texts(Rows(page)[1])[0]);
            Assert.Equal(420, page.Scroll.Offset.Y);
            page.Tabs.SelectedIndex = 0;
            LauncherHeadlessTests.Pump(window);
            Assert.Equal(["Arcueid's route, day 12", "Finish day 12 on Arcueid's route.", "Silver", Local(2026, 11, 11, 9, 30)], Texts(Rows(page)[12]));

            // a minimised window does not look; back up, it does
            window.WindowState = WindowState.Minimized;
            File.WriteAllText(_paths.TrophiesFile, Sample + "12=2026-11-11T09:30:00Z\n13=2026-11-11T10:10:00Z\n");
            for (int i = 0; i < 15; i++)
            {
                LauncherHeadlessTests.Pump(window);
                await Task.Delay(20);
            }

            Assert.Equal(13, model.AchievementsEarned);
            window.WindowState = WindowState.Normal;
            await LauncherHeadlessTests.Until(window, () => model.AchievementsEarned == 14);
            Assert.Equal("14 of 30 earned", page.CountText.Text);
            window.Close();
            Assert.False(window.WatchesAchievements);
        });
    }

    [Fact]
    public async Task WhenTheGameEndsThePageShowsWhatItEarned()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        UnixLauncherModel model = Model();
        await LauncherHeadlessTests.Run(async () =>
        {
            // a window whose timer never looks: only the end of the game reads the record again
            var window = new MainWindow(model) { AchievementsWatchInterval = TimeSpan.FromHours(1) };
            window.Show();
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            Assert.Equal("12 of 30 earned", page.CountText.Text);
            using Process game = LauncherHeadlessTests.Exiting(0);
            model.Track(game);
            Assert.Equal(WindowState.Minimized, window.WindowState);
            File.WriteAllText(_paths.TrophiesFile, Sample + "12=2026-11-11T09:30:00Z\n");
            await game.WaitForExitAsync();
            await LauncherHeadlessTests.Until(window, () => model.AchievementsEarned == 13);
            Assert.Equal("13 of 30 earned", page.CountText.Text);
            window.Close();
        });
    }

    [Fact]
    public async Task ARecordThatDidNotChangeIsNotReadAgainAndNothingIsSaidWithoutNews()
    {
        UnixLauncherModel model = Model();
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => { lock (raised) { raised.Add(e.PropertyName); } };

        // nothing changed since the model was made: the look ends at once, without a reading
        Assert.True(model.ReadAchievementsAgainAsync(onlyIfChanged: true).IsCompletedSuccessfully);
        Assert.Empty(raised);

        File.WriteAllText(_paths.TrophiesFile, "version=1\n1=earlier\n");
        await model.ReadAchievementsAgainAsync(onlyIfChanged: true);
        Assert.Equal(1, model.AchievementsEarned);
        Assert.Equal("Earned", model.AchievementRows[1].State);
        lock (raised)
        {
            foreach (string name in new[] { nameof(model.AchievementsPercent), nameof(model.AchievementsCount), nameof(model.AchievementsEarned), nameof(model.AchievementGrades), nameof(model.AchievementsEarnedTab), nameof(model.AchievementsLockedTab) })
            {
                Assert.Contains(name, raised);
            }

            Assert.DoesNotContain(null, raised);
            raised.Clear();
        }

        // written again with the same content: it is read, and nothing is said
        File.SetLastWriteTimeUtc(_paths.TrophiesFile, DateTime.UtcNow.AddMinutes(1));
        Task again = model.ReadAchievementsAgainAsync(onlyIfChanged: true);
        Assert.NotSame(Task.CompletedTask, again);
        await again;
        lock (raised) { Assert.Empty(raised); }

        // the window coming to the front looks too
        File.WriteAllText(_paths.TrophiesFile, "version=1\n1=earlier\n2=2026-11-11T09:30:00Z\n");
        model.ReadProfileAgain(onlyIfChanged: true);
        Assert.True(SpinWait.SpinUntil(() => model.AchievementsEarned == 2, TimeSpan.FromSeconds(10)), "the record was not read again when the window came to the front");
    }

    [Fact]
    public async Task EveryPictureOnThePageIsTheLaunchersOwnTileOrEmblem()
    {
        // the platinum, bronze and silver ones and the first gold one are earned
        File.WriteAllText(_paths.TrophiesFile, Sample + "0=earlier\n14=earlier\n");
        // the launcher's own way to the icons: this stand-in of a game folder has no picture to cut one from
        UnixLauncherModel model = Model(GameFolder());
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);

            // no icon can be made: an earned row shows the emblem of its grade, drawn by the launcher's core, for each of the four grades
            Control[] rows = Rows(page);
            foreach ((int id, TrophyGrade grade) in new[] { (0, TrophyGrade.Platinum), (4, TrophyGrade.Bronze), (5, TrophyGrade.Silver), (14, TrophyGrade.Gold), (28, TrophyGrade.Silver) })
            {
                Assert.Equal(TilePixels(Achievements.EmblemIcon(grade)), Pixels(Tile(rows[id])));
            }

            // a locked row has its padlock tile
            Assert.Equal(TilePixels(Achievements.LockedIcon(TrophyGrade.Silver)), Pixels(Tile(rows[12])));

            // the summary shows the four emblems
            Control[] tallies = [.. page.GradeList.GetRealizedContainers()];
            Assert.Equal(
                new[] { TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze }.Select(EmblemPixels),
                tallies.Select(tally => Pixels(Emblem(tally))));

            // and every picture on the page is a bitmap the launcher made at one of its own sizes (a row's tile, the summary's emblem): on every tab
            foreach (int tab in new[] { 0, 1, 2 })
            {
                page.Tabs.SelectedIndex = tab;
                LauncherHeadlessTests.Pump(window);
                Image[] shown = [.. page.GetVisualDescendants().OfType<Image>().Where(image => image.IsEffectivelyVisible)];
                Assert.NotEmpty(shown);
                Assert.All(shown, image => Assert.Contains(Assert.IsAssignableFrom<Bitmap>(image.Source).PixelSize.Width, new[] { UnixLauncherModel.TilePixels, UnixLauncherModel.EmblemPixels }));
            }

            window.Close();
        });
    }

    [Fact]
    public void NoPictureOfThePs4TrophyFileIsShownAnywhereOnTheLinuxAndMacLauncher()
    {
        // D6 (the owner, 2026-11-12): the PS4's four cup pictures go from every place. This launcher reads nothing of the trophy file and draws no cup of its own
        // on the page: a grade is the emblem the core draws (Achievements.EmblemIcon), a bitmap like the tiles. No test game folder holds a trophy file, so the
        // page's tests could not see a cup picture come back; this is the guard. The core is shared with the Windows launcher and has its own test; this is the
        // rest of this launcher, its project file too (a file of the game's linked into it would be named there).
        string launcher = Path.Combine(RepositoryFolder(), "src", "Tsuki.Launcher.Avalonia");
        string[] files = [.. Directory.EnumerateFiles(launcher, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal) || file.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(file => !Path.GetRelativePath(launcher, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "obj" or "bin"))];
        Assert.Contains(files, file => Path.GetFileName(file) == "AchievementsPage.axaml");
        Assert.Contains(files, file => Path.GetFileName(file) == "UnixLauncherArt.cs");
        Assert.Contains(files, file => Path.GetFileName(file) == "Tsuki.Launcher.Avalonia.csproj");
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            // the names of every way a cup picture came in (the core's CupPngs and NoCups; this launcher's LoadCups, CupLook, ShowsCup and _cups; the Windows
            // one's DecodeCup; the game's TrophyCups; the reader TrophyPack with its PngReader) and the file itself, by its name, its entries or its folder
            foreach (string gone in new[] { "CupPng", "NoCups", "LoadCups", "CupLook", "ShowsCup", "_cups", "DecodeCup", "TrophyCups", "TrophyPack", "PngReader", "DrawnCup", "CupArt", "CupColors", "AchievementCup", "trophy00", ".trp", "TROP0" })
            {
                Assert.False(text.Contains(gone, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} still names {gone}");
            }

            Assert.False(Regex.IsMatch(text, @"sce_sys[""/\\,\s]*""?trophy", RegexOptions.IgnoreCase), $"{Path.GetFileName(file)} names the folder sce_sys/trophy");
        }

        // the page draws no picture of its own (its own cups were paths on a canvas in a box): no canvas, geometry, drawing or brush of a picture, and every
        // picture is a bound bitmap, a grade's emblem and a row's tile
        XDocument page = XDocument.Load(Path.Combine(launcher, "Views", "AchievementsPage.axaml"));
        Assert.DoesNotContain(page.Descendants(), element => element.Name.LocalName is "Canvas" or "Geometry" or "PathGeometry" or "StreamGeometry" or "GeometryGroup"
            or "DrawingImage" or "DrawingGroup" or "GeometryDrawing" or "ImageDrawing" or "DrawingBrush" or "ImageBrush" or "VisualBrush");
        Assert.Equal(["{Binding Emblem}", "{Binding Tile}"], page.Descendants().Where(element => element.Name.LocalName == "Image").Select(image => (string?)image.Attribute("Source")));

        // and the only lines it draws are the small signs beside words (the warning, the note's i, a row's check or padlock), each alone in a box of 14 px
        XElement[] lines = [.. page.Descendants().Where(element => element.Name.LocalName == "Path")];
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Equal(
            ("Viewbox", "14", "14", 1),
            (line.Parent!.Name.LocalName, (string?)line.Parent.Attribute("Width"), (string?)line.Parent.Attribute("Height"), line.Parent.Elements().Count())));
    }

    [Fact]
    public async Task AnEarnedRowAsksForItsIconOnceThePageIsShownAndALockedRowNeverDoes()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        var desk = new IconDesk();
        UnixLauncherModel model = Model(GameFolder(), desk);
        await LauncherHeadlessTests.Run(async () =>
        {
            // the launcher is up and has read the game folder; nobody has looked at the page: nothing was asked of the game's pictures
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            foreach (LauncherPage other in new[] { LauncherPage.GameFiles, LauncherPage.Saves, LauncherPage.Help, LauncherPage.Play })
            {
                model.Page = other;
                LauncherHeadlessTests.Pump(window);
            }

            Assert.Empty(desk.Asked);
            Assert.True(model.AchievementIconWork.IsCompleted);

            // the page is shown: each earned achievement is asked for once, with the game folder, one after the other; no locked one is
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            int[] earned = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 28];
            Assert.Equal(earned, desk.Asked.Select(asked => asked.Id).Order());
            Assert.NotNull(model.Folder.Path);
            Assert.All(desk.Asked, asked => Assert.Equal(model.Folder.Path, asked.Folder));
            Assert.Equal(1, desk.MostAtOnce);

            // the other tabs, a poll, and the page left and shown again ask for nothing more: the locked rows have their padlocks, the earned ones their icons
            foreach (int tab in new[] { 2, 1, 0 })
            {
                page.Tabs.SelectedIndex = tab;
                LauncherHeadlessTests.Pump(window);
            }

            File.SetLastWriteTimeUtc(_paths.TrophiesFile, DateTime.UtcNow.AddMinutes(1));
            await window.WatchAchievements();
            model.Page = LauncherPage.Saves;
            page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            Assert.Equal(earned, desk.Asked.Select(asked => asked.Id).Order());
            Control[] rows = Rows(page);
            Assert.All(earned, id => Assert.Equal(IconDesk.Colour(id), At(Pixels(Tile(rows[id])), 40, 60)));
            Assert.All(Enumerable.Range(0, 30).Except(earned), id => Assert.Equal(TilePixels(Achievements.LockedIcon(model.AchievementRows[id].Row.Grade)), Pixels(Tile(rows[id]))));
            window.Close();
        });
    }

    [Fact]
    public async Task AnEarnedRowShowsTheBlankTileOfItsGradeUntilItsIconArrivesAndThenTheIcon()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        // a slow disk: every picture waits until the door opens
        var desk = new IconDesk();
        desk.Door.Reset();
        UnixLauncherModel model = Model(GameFolder(), desk);
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => desk.Asked.Length > 0);
            Assert.False(model.AchievementIconWork.IsCompleted);

            // nothing has arrived: an earned row shows the blank tile of its grade (the dark ground inside the grade's full ring, clear corners), not the emblem
            Control[] rows = Rows(page);
            Assert.All(rows, row => Assert.True(TileImage(row).IsEffectivelyVisible));
            Assert.All(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 28 }, id => Assert.Equal(TilePixels(Achievements.BlankIcon(model.AchievementRows[id].Row.Grade)), Pixels(Tile(rows[id]))));
            Assert.NotEqual(TilePixels(Achievements.BlankIcon(TrophyGrade.Bronze)), TilePixels(Achievements.EmblemIcon(TrophyGrade.Bronze)));
            foreach ((int id, TrophyGrade grade, (byte, byte, byte, byte) ring) in new[]
                     {
                         (1, TrophyGrade.Bronze, ((byte)0x5A, (byte)0x8D, (byte)0xC9, (byte)0xFF)),
                         (8, TrophyGrade.Silver, ((byte)0xD4, (byte)0xC8, (byte)0xC0, (byte)0xFF)),
                     })
            {
                byte[] blank = Pixels(Tile(rows[id]));
                Assert.Equal(TilePixels(Achievements.BlankIcon(grade)), blank);
                Assert.Equal(ring, At(blank, UnixLauncherModel.TilePixels / 2, 1));
                Assert.Equal((0x2B, 0x1C, 0x16, 0xFF), At(blank, UnixLauncherModel.TilePixels / 2, UnixLauncherModel.TilePixels / 2));
                Assert.Equal(default, At(blank, 0, 0));
                Assert.Equal(0, Count(blank, (0x80, 0x67, 0x5B, 0xFF)));
            }

            // one blank tile for the rows of a grade
            Assert.Same(Tile(rows[1]), Tile(rows[4]));
            Assert.Same(Tile(rows[8]), Tile(rows[28]));

            // the pictures arrive: each earned row shows its own, a bitmap of its own at twice the size it is drawn at; the locked rows keep their padlocks
            desk.Door.Set();
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            rows = Rows(page);
            int[] earned = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 28];
            foreach (int id in earned)
            {
                Bitmap icon = Tile(rows[id]);
                Assert.Equal(new PixelSize(2 * 52, 2 * 52), icon.PixelSize);
                Assert.All(new[] { (2, 2), (52, 52), (101, 101), (0, 103) }, at => Assert.Equal(IconDesk.Colour(id), At(Pixels(icon), at.Item1, at.Item2)));
            }

            Assert.Equal(earned.Length, earned.Select(id => Tile(rows[id])).Distinct().Count());
            Assert.Equal(TilePixels(Achievements.LockedIcon(TrophyGrade.Silver)), Pixels(Tile(rows[12])));
            window.Close();
        });
    }

    [Fact]
    public async Task ARowWhoseIconCannotBeMadeShowsTheEmblemOfItsGrade()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        // the pictures of 3 (bronze) and 8 (silver) are not in this game folder
        var desk = new IconDesk { Has = id => id is not (3 or 8) };
        UnixLauncherModel model = Model(GameFolder(), desk);
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            Control[] rows = Rows(page);
            foreach ((int id, TrophyGrade grade) in new[] { (3, TrophyGrade.Bronze), (8, TrophyGrade.Silver) })
            {
                Assert.True(TileImage(rows[id]).IsEffectivelyVisible, "no tile where no icon can be made");
                Assert.Equal(TilePixels(Achievements.EmblemIcon(grade)), Pixels(Tile(rows[id])));
                Assert.Equal((52, 52), (TileImage(rows[id]).Bounds.Width, TileImage(rows[id]).Bounds.Height));
            }

            // the emblem is not the blank tile the rows show while their icons are made, nor the padlock
            Assert.NotEqual(TilePixels(Achievements.BlankIcon(TrophyGrade.Silver)), Pixels(Tile(rows[8])));
            Assert.NotEqual(TilePixels(Achievements.LockedIcon(TrophyGrade.Silver)), Pixels(Tile(rows[8])));

            // the others have their icons
            Assert.All(new[] { 1, 2, 4, 5, 9, 28 }, id => Assert.Equal(IconDesk.Colour(id), At(Pixels(Tile(rows[id])), 52, 52)));

            // what could not be made is not asked for again by a poll or another tab
            File.WriteAllText(_paths.TrophiesFile, Sample + "12=2026-11-11T09:30:00Z\n");
            await window.WatchAchievements();
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted && desk.Asked.Any(asked => asked.Id == 12));
            page.Tabs.SelectedIndex = 1;
            LauncherHeadlessTests.Pump(window);
            Assert.Equal(1, desk.Asked.Count(asked => asked.Id == 3));
            Assert.Equal(1, desk.Asked.Count(asked => asked.Id == 8));
            Assert.Equal(TilePixels(Achievements.EmblemIcon(TrophyGrade.Bronze)), Pixels(Tile(Rows(page)[2])));
            window.Close();
        });
    }

    [Fact]
    public async Task ARowKeepsItsBitmapAcrossPollsTabsAndVisitsOfThePage()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        var desk = new IconDesk();
        UnixLauncherModel model = Model(GameFolder(), desk);
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            Bitmap icon = Tile(Rows(page)[1]);
            Bitmap padlock = Tile(Rows(page)[13]);
            Assert.Equal(IconDesk.Colour(1), At(Pixels(icon), 52, 52));

            // a poll that finds the record written again with the same content
            File.SetLastWriteTimeUtc(_paths.TrophiesFile, DateTime.UtcNow.AddMinutes(1));
            await window.WatchAchievements();
            LauncherHeadlessTests.Pump(window);
            Assert.Same(icon, Tile(Rows(page)[1]));

            // a poll that finds another one earned: the rows that did not change keep their pictures
            File.WriteAllText(_paths.TrophiesFile, Sample + "12=2026-11-11T09:30:00Z\n");
            await window.WatchAchievements();
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted && desk.Asked.Any(asked => asked.Id == 12));
            Assert.Equal(IconDesk.Colour(12), At(Pixels(Tile(Rows(page)[12])), 52, 52));
            Assert.Same(icon, Tile(Rows(page)[1]));
            Assert.Same(padlock, Tile(Rows(page)[13]));

            // the other tabs and back
            page.Tabs.SelectedIndex = 1;
            LauncherHeadlessTests.Pump(window);
            Assert.Same(icon, Tile(Rows(page)[0]));
            page.Tabs.SelectedIndex = 2;
            LauncherHeadlessTests.Pump(window);
            Assert.Same(padlock, Tile(Rows(page)[1]));
            page.Tabs.SelectedIndex = 0;
            LauncherHeadlessTests.Pump(window);
            Assert.Same(icon, Tile(Rows(page)[1]));

            // another page and back: a new page, the same pictures; a refresh that finds the same game folder keeps them too
            model.Page = LauncherPage.Saves;
            page = Open(window, model);
            Assert.Same(icon, Tile(Rows(page)[1]));
            await model.RefreshAsync();
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            Assert.Same(icon, Tile(Rows(page)[1]));
            Assert.Same(padlock, Tile(Rows(page)[13]));

            // it was made once, and it is still a picture
            Assert.Equal(1, desk.Asked.Count(asked => asked.Id == 1));
            Assert.Equal(13, desk.Asked.Length);
            Assert.Equal(IconDesk.Colour(1), At(Pixels(icon), 52, 52));
            window.Close();
        });
    }

    [Fact]
    public async Task ARowThatBecomesEarnedWhileThePageIsOpenGetsItsIcon()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        var desk = new IconDesk();
        UnixLauncherModel model = Model(GameFolder(), desk);
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            Assert.DoesNotContain(desk.Asked, asked => asked.Id is 12 or 13);
            Assert.Equal(TilePixels(Achievements.LockedIcon(TrophyGrade.Silver)), Pixels(Tile(Rows(page)[12])));

            // the game earns 12, and its picture is slow: the row shows the blank tile, neither the padlock any more nor the emblem
            desk.Door.Reset();
            File.WriteAllText(_paths.TrophiesFile, Sample + "12=2026-11-11T09:30:00Z\n");
            await window.WatchAchievements();
            await LauncherHeadlessTests.Until(window, () => desk.Asked.Any(asked => asked.Id == 12));
            Assert.Equal(TilePixels(Achievements.BlankIcon(TrophyGrade.Silver)), Pixels(Tile(Rows(page)[12])));

            // the picture arrives
            desk.Door.Set();
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            Assert.Equal(IconDesk.Colour(12), At(Pixels(Tile(Rows(page)[12])), 52, 52));
            Assert.Equal(1, desk.Asked.Count(asked => asked.Id == 12));

            // one earned while its row is not on show (the Locked tab loses it) has its icon when the row comes back
            page.Tabs.SelectedIndex = 2;
            LauncherHeadlessTests.Pump(window);
            File.WriteAllText(_paths.TrophiesFile, Sample + "12=2026-11-11T09:30:00Z\n13=2026-11-11T10:10:00Z\n");
            await window.WatchAchievements();
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted && desk.Asked.Any(asked => asked.Id == 13));
            Assert.Equal(1, desk.Asked.Count(asked => asked.Id == 13));
            page.Tabs.SelectedIndex = 0;
            LauncherHeadlessTests.Pump(window);
            Assert.Equal(IconDesk.Colour(13), At(Pixels(Tile(Rows(page)[13])), 52, 52));
            Assert.Equal(14, desk.Asked.Length);
            Assert.Equal(1, desk.MostAtOnce);
            window.Close();
        });
    }

    [Fact]
    public async Task CheckAgainAndAnotherGameFolderForgetTheIconsAndMakeThemAgain()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        var desk = new IconDesk();
        UnixLauncherModel model = Model(GameFolder(), desk);
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            string first = model.Folder.Path!;
            Bitmap icon = Tile(Rows(page)[1]);
            Assert.Equal(12, desk.Asked.Length);

            // "Check again" on the Game files page: the folder is read again, so its icons are made again, once; the old bitmaps are released.
            // (The check ends with a refresh; a refresh after it finds the same folder, as it was, and leaves the icons alone.)
            int checkedAgain = 0;
            model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.Folder)) { checkedAgain++; } };
            model.RecheckCommand.Execute(null);
            await LauncherHeadlessTests.Until(window, () => checkedAgain > 0 && desk.Asked.Length >= 24 && model.AchievementIconWork.IsCompleted);
            await model.RefreshAsync();
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            Assert.Equal(24, desk.Asked.Length);
            Assert.All(Enumerable.Range(1, 11).Append(28), id => Assert.Equal(2, desk.Asked.Count(asked => asked.Id == id)));
            Bitmap again = Tile(Rows(page)[1]);
            Assert.NotSame(icon, again);
            Assert.ThrowsAny<Exception>(() => icon.PixelSize);
            Assert.Equal(IconDesk.Colour(1), At(Pixels(again), 52, 52));

            // another game folder: its pictures are asked for, and the first folder's are released
            string other = GameFolder("CUSA00000");
            model.FolderText = other;
            await model.ApplyFolderText();
            await LauncherHeadlessTests.Until(window, () => desk.Asked.Length >= 36 && model.AchievementIconWork.IsCompleted);
            string second = model.Folder.Path!;
            Assert.NotEqual(first, second);
            Assert.Equal(24, desk.Asked.Count(asked => asked.Folder == first));
            Assert.Equal(12, desk.Asked.Count(asked => asked.Folder == second));
            Assert.Equal(IconDesk.Colour(1, game: 1), At(Pixels(Tile(Rows(page)[1])), 52, 52));
            Bitmap third = Tile(Rows(page)[1]);
            Assert.NotSame(again, third);
            Assert.ThrowsAny<Exception>(() => again.PixelSize);

            // that folder is gone at the next check: nothing can be made, the earned rows show their emblems, the locked ones keep their padlocks
            Bitmap padlock = Tile(Rows(page)[13]);
            Directory.Delete(other, recursive: true);
            await model.RefreshAsync();
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            Assert.Equal(TilePixels(Achievements.EmblemIcon(TrophyGrade.Bronze)), Pixels(Tile(Rows(page)[1])));
            Assert.ThrowsAny<Exception>(() => third.PixelSize);
            Assert.Equal(36, desk.Asked.Length);
            Assert.Same(padlock, Tile(Rows(page)[13]));
            Assert.Equal(new PixelSize(2 * 52, 2 * 52), padlock.PixelSize);
            window.Close();
        });
    }

    [Fact]
    public async Task APictureThatArrivesAfterTheIconsWereForgottenIsDropped()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        var desk = new IconDesk();
        UnixLauncherModel model = Model(GameFolder(), desk);
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            string first = model.Folder.Path!;
            Assert.Equal(12, desk.Asked.Length);

            // the game earns 12, and the disk is slow: its picture is on its way and does not come
            ManualResetEventSlim firstDoor = desk.DoorOf(first);
            File.WriteAllText(_paths.TrophiesFile, Sample + "12=2026-11-11T09:30:00Z\n");
            await window.WatchAchievements();
            await LauncherHeadlessTests.Until(window, () => desk.Asked.Length == 13);
            Assert.Equal((first, 12), desk.Asked[^1]);

            // meanwhile the player chooses another game folder, whose pictures are slow too
            string other = GameFolder("CUSA00000");
            model.FolderText = other;
            await model.ApplyFolderText();
            string second = model.Folder.Path!;
            ManualResetEventSlim secondDoor = desk.DoorOf(second);
            Assert.NotEqual(first, second);

            // now the first folder's picture of 12 arrives. It is not this folder's: the row keeps its blank tile, and the worker goes on to the new folder's
            firstDoor.Set();
            await LauncherHeadlessTests.Until(window, () => desk.Asked.Length == 14);
            Assert.Equal((second, 1), desk.Asked[^1]);
            LauncherHeadlessTests.Pump(window);
            Assert.Equal(TilePixels(Achievements.BlankIcon(TrophyGrade.Silver)), Pixels(Tile(Rows(page)[12])));
            Assert.Equal(TilePixels(Achievements.BlankIcon(TrophyGrade.Bronze)), Pixels(Tile(Rows(page)[1])));

            // and when the new folder's pictures come, they are the ones on show; the first folder was not asked again
            secondDoor.Set();
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            Assert.All(new[] { 1, 12, 28 }, id => Assert.Equal(IconDesk.Colour(id, game: 1), At(Pixels(Tile(Rows(page)[id])), 52, 52)));
            Assert.Equal(13, desk.Asked.Count(asked => asked.Folder == first));
            Assert.Equal(13, desk.Asked.Count(asked => asked.Folder == second));
            Assert.Equal(1, desk.MostAtOnce);
            window.Close();
        });
    }

    [Fact]
    public async Task TheGradesEmblemsAreDrawnOnAWorkerAndThePageIsUpWithoutThemMeanwhile()
    {
        // Drawing an emblem at 240 px takes tens of milliseconds (about a quarter of a second for the four in a debug build): the window's thread must not do it.
        // The core's drawing is held back here like a slow machine, and the test notes on which thread each grade was asked for.
        File.WriteAllText(_paths.TrophiesFile, Sample);
        using var door = new ManualResetEventSlim(initialState: false);
        var asked = new List<(TrophyGrade Grade, bool OnWindowThread)>();
        UnixLauncherModel model = Model(emblems: grade =>
        {
            bool onWindowThread = Dispatcher.UIThread.CheckAccess();
            lock (asked) { asked.Add((grade, onWindowThread)); }
            // on the window's thread the door could never open: there the test fails below instead of hanging
            if (!onWindowThread) { door.Wait(TimeSpan.FromSeconds(30)); }
            return Achievements.EmblemIcon(grade);
        });
        (TrophyGrade Grade, bool OnWindowThread)[] Asked() { lock (asked) { return [.. asked]; } }
        await LauncherHeadlessTests.Run(async () =>
        {
            // nothing is drawn before the page is first shown
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            model.Page = LauncherPage.Saves;
            LauncherHeadlessTests.Pump(window);
            Assert.Empty(Asked());

            // the page is up at once, with its counts, its padlocks and the blank tiles of the earned rows (no game folder: they wait for their grades' emblems)
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => Asked().Length > 0);
            Assert.All(Asked(), ask => Assert.False(ask.OnWindowThread, $"the emblem of the {ask.Grade} was drawn on the window's thread"));
            Assert.False(model.AchievementIconWork.IsCompleted);
            Control[] tallies = [.. page.GradeList.GetRealizedContainers()];
            Assert.Equal(["0 / 1 Platinum", "0 / 4 Gold", "8 / 21 Silver", "4 / 4 Bronze"], tallies.Select(tally => string.Join(" ", Texts(tally))));
            Assert.All(tallies, tally => Assert.Null(EmblemImage(tally).Source));
            Rect[] places = [.. tallies.Select(tally => new Rect(tally.TranslatePoint(default, window)!.Value, tally.Bounds.Size))];
            Point listAt = page.ListCard.TranslatePoint(default, window)!.Value;
            Control[] rows = Rows(page);
            Assert.Equal(TilePixels(Achievements.BlankIcon(TrophyGrade.Bronze)), Pixels(Tile(rows[1])));
            Assert.Equal(TilePixels(Achievements.BlankIcon(TrophyGrade.Silver)), Pixels(Tile(rows[28])));
            Assert.Equal(TilePixels(Achievements.LockedIcon(TrophyGrade.Silver)), Pixels(Tile(rows[12])));

            // the emblems arrive: the summary and the earned rows show them, the locked rows keep their padlocks. The emblems take the squares kept for them
            // (an Image of a set size keeps its place without a picture, though its own bounds are empty until it has one): nothing on the page moves
            door.Set();
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            tallies = [.. page.GradeList.GetRealizedContainers()];
            Assert.Equal(new[] { TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze }.Select(EmblemPixels), tallies.Select(tally => Pixels(Emblem(tally))));
            Assert.All(tallies, tally => Assert.Equal((UnixLauncherModel.GradeEmblemSize, UnixLauncherModel.GradeEmblemSize), (EmblemImage(tally).Bounds.Width, EmblemImage(tally).Bounds.Height)));
            Assert.Equal(places, tallies.Select(tally => new Rect(tally.TranslatePoint(default, window)!.Value, tally.Bounds.Size)));
            Assert.Equal(listAt, page.ListCard.TranslatePoint(default, window)!.Value);
            rows = Rows(page);
            Assert.Equal(TilePixels(Achievements.EmblemIcon(TrophyGrade.Bronze)), Pixels(Tile(rows[1])));
            Assert.Equal(TilePixels(Achievements.EmblemIcon(TrophyGrade.Silver)), Pixels(Tile(rows[28])));
            Assert.Equal(TilePixels(Achievements.LockedIcon(TrophyGrade.Silver)), Pixels(Tile(rows[12])));

            // each grade was drawn once, off the window's thread; the page left and shown again draws nothing more
            model.Page = LauncherPage.Saves;
            page = Open(window, model);
            Assert.Equal(TilePixels(Achievements.EmblemIcon(TrophyGrade.Bronze)), Pixels(Tile(Rows(page)[1])));
            Assert.Equal([TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze], Asked().Select(ask => ask.Grade).Order());
            Assert.DoesNotContain(Asked(), ask => ask.OnWindowThread);
            window.Close();
        });
    }

    [Fact]
    public async Task ATileBitmapIsPremultipliedAndReducedByAreaAverage()
    {
        await LauncherHeadlessTests.Run(() =>
        {
            // 240 x 240, straight alpha: the left half see-through orange, the right half solid blue, under a clear top row
            const int size = 240;
            byte[] rgba = new byte[size * size * 4];
            for (int y = 1; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    (byte r, byte g, byte b, byte a) = x < size / 2 ? ((byte)200, (byte)100, (byte)50, (byte)128) : ((byte)10, (byte)20, (byte)250, (byte)255);
                    int at = ((y * size) + x) * 4;
                    (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = (r, g, b, a);
                }
            }

            using WriteableBitmap bitmap = UnixLauncherArt.TileBitmap(new AchievementIcon(size, rgba), 104);
            Assert.Equal(new PixelSize(104, 104), bitmap.PixelSize);
            Assert.Equal(AlphaFormat.Premul, bitmap.AlphaFormat);
            byte[] pixels = Pixels(bitmap);
            // blue, green, red as the bitmap keeps them, each already multiplied by the alpha beside it
            Assert.Equal((25, 50, 100, 128), At(pixels, 20, 50));
            Assert.Equal((250, 20, 10, 255), At(pixels, 80, 50));
            // the top row of the bitmap lies over the clear row and 1.3 solid ones: its alpha is their share, and its colour is the solid rows' at that alpha
            (byte blue, byte _, byte red, byte alpha) = At(pixels, 80, 0);
            Assert.InRange(alpha, 142, 148);
            Assert.InRange(blue, alpha - 6, alpha);
            Assert.InRange(red, 4, 7);

            // a picture already of that size is taken as it is
            using WriteableBitmap same = UnixLauncherArt.TileBitmap(new AchievementIcon(2, [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 9, 9, 9, 0]), 2);
            Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 0, 0, 0, 0 }, Pixels(same));

            // bytes that are not a picture of the size they claim are refused, not read past
            Assert.ThrowsAny<ArgumentException>(() => UnixLauncherArt.TileBitmap(new AchievementIcon(size, new byte[100]), 104));
            return Task.CompletedTask;
        });
    }

    /// <summary>The height of the window whose Achievements page is just not short: the footer (88), the strip above the page (40) and its margin (6) above it.</summary>
    private const double LeastTallWindow = AchievementsPage.ShortBelow + 134;

    [Theory]
    [InlineData(1200.0, 780.0)]
    [InlineData(800.0, 460.0)]
    [InlineData(800.0, LeastTallWindow)]
    [InlineData(800.0, LeastTallWindow - 1)]
    public async Task TheSwitchesComeRightUnderTheSummaryAboveTheTabsAndTheListOnScreenAndInTabOrder(double width, double height)
    {
        // Both switches are off until the player turns them on, and the release notes send the player to this page for them: they sit right under the summary,
        // not under the 30 rows, in the window the launcher opens with (1200 × 780), in the smallest (800 × 460, a Steam Deck at 150 %) and on both sides of
        // the height where the page becomes short
        File.WriteAllText(_paths.TrophiesFile, Sample);
        UnixLauncherModel model = Model();
        await LauncherHeadlessTests.Run(async () =>
        {
            bool opening = (width, height) == (1200, 780);
            MainWindow window = opening ? new MainWindow(model) : new MainWindow(model) { Width = width, Height = height };
            window.Show();
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            Assert.Equal((width, height), (window.Bounds.Width, window.Bounds.Height));
            bool shortPage = height - 134 < AchievementsPage.ShortBelow;
            Assert.Equal(height - 134, page.Bounds.Height);
            Assert.Equal(shortPage, page.Classes.Contains("Short"));
            Assert.Equal(!shortPage, page.Header.IsEffectivelyVisible);

            // the two cards' spacing, as on Windows: 16 px at the foot of both, 10 under the summary's divider; tighter still in a short page. The page's styles
            // must outrank the theme's Card (20 px at its foot), which a style naming the card alone does not
            Assert.Equal(shortPage ? new Thickness(22, 14, 22, 12) : new Thickness(22, 18, 22, 16), page.SummaryCard.Padding);
            Assert.Equal(shortPage ? new Thickness(0, 8, 0, 8) : new Thickness(0, 11, 0, 10), page.SummaryDivider.Margin);
            Assert.Equal(shortPage ? new Thickness(0, 10, 0, 2) : new Thickness(0, 16, 0, 2), page.Progress.Margin);
            Assert.Equal(shortPage ? new Thickness(0, 14, 0, 0) : new Thickness(0, 18, 0, 0), page.NoticeCard.Margin);
            Assert.Equal(shortPage ? new Thickness(22, 14, 22, 16) : new Thickness(22, 18, 22, 16), page.NoticeCard.Padding);

            // the cards top to bottom: the summary; right under it the switches' card (the notice's switch, the sound's, where the record is kept); the notes
            // on the record and on the game folder when they are shown (here the folder's: there is none); then the tabs and the list
            Panel cards = Assert.IsAssignableFrom<Panel>(page.SummaryCard.Parent);
            Assert.Equal(new[] { page.SummaryCard, page.NoticeCard, page.DamagedBox, page.NoGameNote, page.ListCard }, cards.Children.OfType<Border>());
            Assert.Equal(cards.Children.IndexOf(page.SummaryCard) + 1, cards.Children.IndexOf(page.NoticeCard));
            Assert.False(page.DamagedBox.IsEffectivelyVisible);
            Control[] drawn = [page.SummaryCard, page.NoticeRow, page.SoundRow, page.FileNote, page.NoGameNote, page.Tabs, page.AchievementList];
            Assert.All(drawn, control => Assert.True(control.IsEffectivelyVisible && control.Bounds.Height > 0, $"{control.Name} is not shown"));
            for (int i = 1; i < drawn.Length; i++)
            {
                double above = drawn[i - 1].TranslatePoint(new Point(0, drawn[i - 1].Bounds.Height), window)!.Value.Y;
                double top = drawn[i].TranslatePoint(default, window)!.Value.Y;
                Assert.True(top >= above - 0.5, $"{drawn[i].Name} starts at {top:0.0}, above the end of {drawn[i - 1].Name} at {above:0.0}");
            }

            // the page opens at its top. There, in every window, the NOTIFY heading, the notice's row and its switch are on show without scrolling; in the
            // window the launcher opens with, the whole card is (both switches and where the record is kept)
            Assert.Equal(0, page.Scroll.Offset.Y);
            Control[] inView = opening ? [page.NoticeHeading, page.NoticeRow, page.NoticeSwitch, page.SoundRow, page.SoundSwitch, page.FileNote, page.NoticeCard] : [page.NoticeHeading, page.NoticeRow, page.NoticeSwitch];
            foreach (Control shown in inView)
            {
                double end = shown.TranslatePoint(new Point(0, shown.Bounds.Height), page.Scroll)!.Value.Y;
                Assert.True(end <= page.Scroll.Viewport.Height, $"{shown.Name} ends at {end:0.0}, below the page's {page.Scroll.Viewport.Height:0.0} px on show ({width} × {height})");
            }

            // Tab from the page chosen in the sidebar: the notice's switch, the sound's, the note's Game files link, then the chosen tab, and on out of the page
            // (to the footer); Shift+Tab from there comes back through them the other way
            Button link = page.NoGameNote.GetVisualDescendants().OfType<Button>().Single();
            Control[] stops = [page.NoticeSwitch, page.SoundSwitch, link, page.Tabs.ContainerFromIndex(page.Tabs.SelectedIndex)!];
            Assert.True(window.Nav.ContainerFromIndex(window.Nav.SelectedIndex)!.Focus(NavigationMethod.Tab));
            LauncherHeadlessTests.Pump(window);
            Assert.Equal(stops, TabStops(window, page, back: false));
            Assert.Equal(Enumerable.Reverse(stops), TabStops(window, page, back: true));
            window.Close();
        });
    }

    [Fact]
    public async Task InTheSmallestWindowEveryRowStaysInsideItsCard()
    {
        // 800 × 460 (a Steam Deck at 150 %): the page is 486 px wide; a long name or sentence wraps, and the grade and the date keep their column.
        // Every kind of row is there: icons, padlock tiles, and an emblem (the picture of 9 is not in this game folder).
        File.WriteAllText(_paths.TrophiesFile, Sample + "25=2026-11-11T09:30:00Z\n");
        var desk = new IconDesk { Has = id => id != 9 };
        UnixLauncherModel model = Model(GameFolder(), desk);
        await LauncherHeadlessTests.Run(async () =>
        {
            var window = new MainWindow(model) { Width = 800, Height = 460 };
            window.Show();
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            double pageRight = Right(window.PageHost, window);
            foreach (Border card in new[] { page.SummaryCard, page.NoticeCard, page.NoGameNote, page.ListCard })
            {
                double inside = Right(card, window) - card.Padding.Right;
                Assert.True(Right(card, window) <= pageRight + 0.5, $"a card ends at {Right(card, window):0.0}, past the page at {pageRight:0.0}");
                foreach (TextBlock text in card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
                {
                    Assert.True(Right(text, window) <= inside + 0.5, $"'{text.Text}' ends at {Right(text, window):0.0}, past its card's inside at {inside:0.0}");
                    Assert.True(text.TextLayout.Width <= text.Bounds.Width + 0.5, $"'{text.Text}' needs {text.TextLayout.Width:0.0} px and got {text.Bounds.Width:0.0}");
                }
            }

            // the switches' card under the summary, its two rows, the notice's and under it the sound's: each switch inside the card at the right, its words wrapped
            // beside it
            double noticeInside = Right(page.NoticeCard, window) - page.NoticeCard.Padding.Right;
            foreach ((HeaderedContentControl row, CheckBox toggle) in new[] { (page.NoticeRow, page.NoticeSwitch), (page.SoundRow, page.SoundSwitch) })
            {
                Assert.True(toggle.IsEffectivelyVisible && toggle.Bounds.Width > 0, $"the switch of '{row.Header}' is not shown");
                Assert.True(Right(toggle, window) <= noticeInside + 0.5, $"the switch of '{row.Header}' ends at {Right(toggle, window):0.0}, past its card's inside at {noticeInside:0.0}");
                double toggleLeft = toggle.TranslatePoint(default, window)!.Value.X;
                TextBlock[] words = [.. row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))];
                Assert.Equal(2, words.Length);
                Assert.All(words, text => Assert.True(Right(text, window) <= toggleLeft, $"'{text.Text}' runs into its switch"));
            }

            Assert.True(
                page.SoundSwitch.TranslatePoint(default, window)!.Value.Y >= page.NoticeSwitch.TranslatePoint(default, window)!.Value.Y + page.NoticeSwitch.Bounds.Height,
                "the sound's switch is not under the notice's");

            double listLeft = page.ListCard.TranslatePoint(default, window)!.Value.X + page.ListCard.Padding.Left;
            double listRight = Right(page.ListCard, window) - page.ListCard.Padding.Right;
            foreach (Control row in Rows(page))
            {
                TextBlock[] texts = [.. row.GetVisualDescendants().OfType<TextBlock>()];
                double stateLeft = Math.Min(texts[2].TranslatePoint(default, window)!.Value.X, texts[3].TranslatePoint(default, window)!.Value.X);
                Assert.True(Right(texts[0], window) <= stateLeft && Right(texts[1], window) <= stateLeft, $"'{texts[0].Text}' runs into its grade and date");

                // the tile is a square of the size the model makes it for, at the row's left, clear of the words and inside the row
                Image picture = TileImage(row);
                Assert.True(picture.IsEffectivelyVisible, $"'{texts[0].Text}' has no tile");
                Assert.Equal((52, 52), (picture.Bounds.Width, picture.Bounds.Height));
                Point corner = picture.TranslatePoint(default, window)!.Value;
                Point rowCorner = row.TranslatePoint(default, window)!.Value;
                Assert.True(corner.X >= listLeft - 0.5, $"the tile of '{texts[0].Text}' starts at {corner.X:0.0}, left of its card's inside at {listLeft:0.0}");
                Assert.True(Right(picture, window) + 8 <= texts[0].TranslatePoint(default, window)!.Value.X, $"the tile of '{texts[0].Text}' runs into the name");
                Assert.True(corner.Y >= rowCorner.Y + 4 && corner.Y + picture.Bounds.Height <= rowCorner.Y + row.Bounds.Height - 4, $"the tile of '{texts[0].Text}' leaves its row");
                Assert.True(Right(row, window) <= listRight + 0.5 && Right(row, window) <= pageRight, $"the row of '{texts[0].Text}' ends at {Right(row, window):0.0}, past the page at {pageRight:0.0}");
            }

            Assert.Equal(52, UnixLauncherModel.AchievementTileSize);
            Assert.Equal(TilePixels(Achievements.EmblemIcon(model.AchievementRows[9].Row.Grade)), Pixels(Tile(Rows(page)[9])));
            Assert.True(TileImage(Rows(page)[8]).IsEffectivelyVisible && TileImage(Rows(page)[12]).IsEffectivelyVisible);

            // the longest sentence, beside a date, wrapped instead
            TextBlock longest = Rows(page)[25].GetVisualDescendants().OfType<TextBlock>().ElementAt(1);
            Assert.Equal("Finish day 14 on Ciel's route, on the way to its second ending.", longest.Text);
            Assert.True(longest.TextLayout.TextLines.Count > 1, "the long sentence did not wrap");
            window.Close();
        });
    }

    [Fact]
    public async Task TheRenderChecksPictureOfThePagesEndIsThePageAtItsEndNotTheFrameBefore()
    {
        // The render check (--render) draws the page's list, its Earned and Locked tabs, then its end. In the smallest window with nothing earned the Earned tab's
        // short page holds the scroll back; then the All tab is chosen and the page scrolled to its end with nothing drawn between. One forced frame drew the
        // change that was waiting (the All tab at the held-back scroll) and held the scroll back: achievements_end.png showed the tabs, not the last rows
        UnixLauncherModel model = Model();
        await LauncherHeadlessTests.Run(async () =>
        {
            var window = new MainWindow(model) { Width = 800, Height = 460, AchievementsWatchInterval = TimeSpan.FromHours(1) };
            window.Show();
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            string folder = Path.Combine(_root, "render");
            Directory.CreateDirectory(folder);
            UnixOffscreenRender.DrawAchievementsPage(window, model, folder);
            Assert.Equal(["achievements_earned.png", "achievements_end.png", "achievements_list.png", "achievements_locked.png"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());

            // the page is at its end with the All tab chosen, and the picture is the page as it is: the frame drawn once nothing is left waiting
            Assert.Equal(0, model.AchievementTabIndex);
            double end = page.Scroll.Extent.Height - page.Scroll.Viewport.Height;
            Assert.True(end > 0 && Math.Abs(page.Scroll.Offset.Y - end) < 0.5, $"the page is at {page.Scroll.Offset.Y:0.0}, not at its end {end:0.0}");
            for (int i = 0; i < 20; i++)
            {
                LauncherHeadlessTests.Pump(window);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            string settled = Path.Combine(_root, "settled.png");
            using (WriteableBitmap frame = window.GetLastRenderedFrame() ?? throw new InvalidOperationException("no rendered frame")) { frame.Save(settled); }
            Assert.True(File.ReadAllBytes(settled).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "achievements_end.png"))), "achievements_end.png is not the page at its end");

            // Whether it showed depended on the window's own render timer (1/60 s, run between the window's jobs once it is due): a frame of its between the two
            // changes let the second through, and the picture was right. So the same two changes again right after that timer ran (it is due after the pause and
            // runs after the next job), so that it does not come between them; five times
            for (int run = 0; run < 5; run++)
            {
                model.AchievementTabIndex = 2;
                page.Scroll.Offset = new Vector(0, page.ListCard.Bounds.Top);
                for (int i = 0; i < 5; i++)
                {
                    LauncherHeadlessTests.Pump(window);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }

                Thread.Sleep(40);
                Dispatcher.UIThread.Post(() => { });
                Dispatcher.UIThread.RunJobs();
                model.AchievementTabIndex = 0;
                Dispatcher.UIThread.RunJobs();
                page.Scroll.ScrollToEnd();
                string taken = Path.Combine(_root, $"taken{run}.png");
                using (WriteableBitmap frame = UnixOffscreenRender.Frame(window)) { frame.Save(taken); }
                Assert.True(File.ReadAllBytes(settled).AsSpan().SequenceEqual(File.ReadAllBytes(taken)), $"run {run}: the frame taken is not the page at its end");
            }

            window.Close();
        });

        // every picture the launcher takes of itself (the render check's, the self-tests' --shots) is taken that way, not by one CaptureRenderedFrame
        string launcher = Path.Combine(RepositoryFolder(), "src", "Tsuki.Launcher.Avalonia");
        foreach (string file in Directory.EnumerateFiles(launcher, "*.cs", SearchOption.AllDirectories).Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(file);
            Assert.False(text.Contains("CaptureRenderedFrame(", StringComparison.Ordinal), $"{Path.GetFileName(file)} takes a picture with one CaptureRenderedFrame");
            Assert.True(Path.GetFileName(file) == "UnixOffscreenRender.cs" || !text.Contains("GetLastRenderedFrame(", StringComparison.Ordinal), $"{Path.GetFileName(file)} takes a picture of its own");
        }
    }

    [Fact]
    public async Task WhenEverythingIsEarnedTheSummaryStillFitsAndTheLockedTabSaysSo()
    {
        // all 30, the platinum too: the widest summary ("100%", "21 / 21") in the smallest window
        File.WriteAllText(_paths.TrophiesFile, "version=1\n" + string.Concat(Enumerable.Range(0, 30).Select(id => string.Create(CultureInfo.InvariantCulture, $"{id}=2026-11-{1 + (id % 28):00}T20:15:00Z\n"))));
        UnixLauncherModel model = Model();
        await LauncherHeadlessTests.Run(async () =>
        {
            var window = new MainWindow(model) { Width = 800, Height = 460 };
            window.Show();
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            Assert.Equal("100%", page.PercentText.Text);
            Assert.Equal("30 of 30 earned", page.CountText.Text);
            Assert.Equal((30, 30), (page.Progress.Value, page.Progress.Maximum));
            Control[] tallies = [.. page.GradeList.GetRealizedContainers()];
            Assert.Equal(["1 / 1 Platinum", "4 / 4 Gold", "21 / 21 Silver", "4 / 4 Bronze"], tallies.Select(tally => string.Join(" ", Texts(tally))));
            Assert.Equal(["All (30)", "Earned (30)", "Locked (0)"], Texts(page.Tabs));
            Assert.Equal(["All Achievements", "Earn every other achievement.", "Platinum", Local(2026, 11, 1, 20, 15)], Texts(Rows(page)[0]));
            // no game folder: the earned platinum shows the platinum's emblem, and its grade's word is in the platinum's colour
            Assert.Equal(TilePixels(Achievements.EmblemIcon(TrophyGrade.Platinum)), Pixels(Tile(Rows(page)[0])));
            Assert.Equal(Color.Parse("#BFD9F2"), Ink(GradeText(Rows(page)[0])));

            // the share and the count at the left, the four emblems at the right: neither runs into the other, and nothing leaves the card
            double inside = Right(page.SummaryCard, window) - page.SummaryCard.Padding.Right;
            double wordsEnd = Math.Max(Right(page.PercentText, window), Right(page.CountText, window));
            Assert.True(page.PercentText.TextLayout.Width <= page.PercentText.Bounds.Width + 0.5, "the share is cut off");
            Assert.True(tallies[0].TranslatePoint(default, window)!.Value.X >= wordsEnd, $"the emblems start at {tallies[0].TranslatePoint(default, window)!.Value.X:0.0}, inside the share that ends at {wordsEnd:0.0}");
            Assert.True(Right(tallies[^1], window) <= inside + 0.5, $"the emblems end at {Right(tallies[^1], window):0.0}, past the card's inside at {inside:0.0}");
            Assert.All(tallies, tally => Assert.Equal(tallies[0].Bounds.Width, tally.Bounds.Width));
            Assert.All(tallies, tally => Assert.True(EmblemImage(tally).IsEffectivelyVisible && Right(EmblemImage(tally), window) <= inside + 0.5, "an emblem leaves the card"));

            // nothing is left to earn: the Locked tab says so instead of an empty list
            page.Tabs.SelectedIndex = 2;
            LauncherHeadlessTests.Pump(window);
            Assert.Empty(Rows(page));
            Assert.True(page.EmptyText.IsEffectivelyVisible);
            Assert.Equal(LauncherTexts.AchievementsNothingLocked, page.EmptyText.Text);
            Assert.True(Right(page.EmptyText, window) <= Right(page.ListCard, window) - page.ListCard.Padding.Right + 0.5, "the sentence leaves its card");
            page.Tabs.SelectedIndex = 1;
            LauncherHeadlessTests.Pump(window);
            Assert.Equal(30, Rows(page).Length);
            Assert.False(page.EmptyText.IsEffectivelyVisible);
            window.Close();
        });
    }

    [Fact]
    public async Task AFolderThatWasStillBeingCopiedIsReadAgainOnceItChangedWithoutCheckAgain()
    {
        // the player chose the game folder while it was still being copied: no picture is there yet, and the earned rows show their grades' emblems
        File.WriteAllText(_paths.TrophiesFile, Sample);
        using var copied = new ManualResetEventSlim(initialState: false);
        var desk = new IconDesk { Has = _ => copied.IsSet };
        string game = GameFolder();
        // Each icon is also asked of the core, whose caches the forgetting must clear: what it read of the game folder (the chapter names and scenes, which
        // this stand-in has none of) and the folder it opened for the icons. No test here can read names from a stand-in, but the core counts how often it
        // opened a game folder for icons, and it opens this one again only once it forgot it. The picture shown is the desk's.
        UnixLauncherModel model = Model(game, iconSource: (folder, id) =>
        {
            _ = Achievements.Icon(folder, id);
            return desk.Icon(folder, id);
        });
        int[] earned = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 28];
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted && desk.Asked.Length >= earned.Length);
            Assert.All(earned, id => Assert.Equal(TilePixels(Achievements.EmblemIcon(model.AchievementRows[id].Row.Grade)), Pixels(Tile(Rows(page)[id]))));
            long size = model.Folder.TotalBytes;
            int openings = Achievements.IconFolderOpenings;

            // a refresh that finds the folder as it was (the end of a game, Enter in the box) keeps what was read of it: nothing is asked again, and the core
            // does not look at the folder again
            await model.RefreshAsync();
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted);
            Assert.Equal(earned.Length, desk.Asked.Length);
            Assert.Equal(openings, Achievements.IconFolderOpenings);

            // the copy ends. The next refresh finds the folder changed and reads it again by itself, without "Check again": the core forgot what it read of the
            // folder and opens it again, and the icons are there now
            File.WriteAllBytes(Path.Combine(game, "image", "copied.bin"), new byte[4096]);
            copied.Set();
            await model.RefreshAsync();
            await LauncherHeadlessTests.Until(window, () => model.AchievementIconWork.IsCompleted && desk.Asked.Length >= 2 * earned.Length);
            Assert.NotEqual(size, model.Folder.TotalBytes);
            Assert.True(Achievements.IconFolderOpenings > openings, "the core still had the game folder as it read it before the copy ended");
            Assert.All(earned, id => Assert.Equal(2, desk.Asked.Count(asked => asked.Id == id)));
            Assert.All(earned, id => Assert.Equal(IconDesk.Colour(id), At(Pixels(Tile(Rows(page)[id])), 52, 52)));
            window.Close();
        });
    }

    [Fact]
    public void TheAchievementsAreReadBeforeTheBannerArtAndTheWindowIcon()
    {
        // Until the achievements are read with the game folder, the page has only what can be told without it (the stand-in names, the note that asks for the
        // folder, too few earned from a save of 0.1.8). A refresh reads them first, before the banner art and the window icon, which open the game folder and
        // decode pictures on their own, as the Windows launcher does. (No test can stop a refresh between those steps without a seam; the order is pinned here.)
        string[] lines = File.ReadAllLines(Path.Combine(RepositoryFolder(), "src", "Tsuki.Launcher.Avalonia", "UnixLauncherModel.cs"));
        int start = Array.FindIndex(lines, line => line.Trim() == "public async Task RefreshAsync()");
        Assert.True(start >= 0, "RefreshAsync is not there");
        int end = Array.FindIndex(lines, start + 2, line => line == "    }");
        string[] refresh = lines[start..end];
        // each is a step of its own, so that one that fails does not skip the next (UnixLauncherStateTests.AStepOfTheRefreshThatFailsDoesNotSkipTheOnesAfterItAndIsReportedOnce)
        int achievements = Array.FindIndex(refresh, line => line.Trim() == "await StepAsync(LoadAchievementsAsync);");
        int hero = Array.FindIndex(refresh, line => line.Trim() == "await StepAsync(LoadHeroAsync);");
        int icon = Array.FindIndex(refresh, line => line.Trim() == "await StepAsync(LoadGameIconAsync);");
        Assert.True(achievements >= 0 && hero >= 0 && icon >= 0, "RefreshAsync no longer loads the achievements, the banner art and the window icon");
        Assert.True(achievements < hero && achievements < icon, "RefreshAsync reads the achievements after the banner art or the window icon");
    }

    [Fact]
    public async Task TheTabsChangeWithTheArrowKeysAndHomeAndEnd()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        UnixLauncherModel model = Model();
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            Assert.IsType<NavigableUniformGrid>(page.Tabs.ItemsPanelRoot);

            // the tabs are one Tab stop, on the chosen tab; from there the keys move the choice, and the rows follow
            Assert.True(page.Tabs.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab));
            LauncherHeadlessTests.Pump(window);
            int[] rowsOf = [30, 12, 18];
            foreach ((PhysicalKey key, int tab) in new[]
                     {
                         (PhysicalKey.ArrowRight, 1), (PhysicalKey.ArrowRight, 2), (PhysicalKey.ArrowRight, 2), (PhysicalKey.ArrowLeft, 1), (PhysicalKey.ArrowDown, 2),
                         (PhysicalKey.ArrowUp, 1), (PhysicalKey.Home, 0), (PhysicalKey.End, 2), (PhysicalKey.ArrowLeft, 1),
                     })
            {
                Press(window, key);
                Assert.Equal((key, tab, tab), (key, model.AchievementTabIndex, page.Tabs.SelectedIndex));
                Assert.Equal(rowsOf[tab], Rows(page).Length);
            }

            // the language tiles and the other segmented choices have the same panel and move the same way (here the language, in a profile of the test's own)
            model.Page = LauncherPage.Language;
            LauncherHeadlessTests.Pump(window);
            ListBox tiles = window.PageHost.GetVisualDescendants().OfType<ListBox>().Single(list => list.Classes.Contains("Tiles"));
            Assert.IsType<NavigableUniformGrid>(tiles.ItemsPanelRoot);
            int language = model.LanguageIndex;
            Assert.True(tiles.ContainerFromIndex(tiles.SelectedIndex)!.Focus(NavigationMethod.Tab));
            Press(window, language < 3 ? PhysicalKey.ArrowRight : PhysicalKey.ArrowLeft);
            Assert.Equal(language < 3 ? language + 1 : language - 1, model.LanguageIndex);
            window.Close();
        });
    }

    [Fact]
    public async Task AScreenReaderSaysEachTabAndEachChoiceByTheWordsItShows()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        UnixLauncherModel model = Model();
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model, TimeSpan.FromHours(1));
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);

            // the tabs the arrow keys walk through (VoiceOver, Orca): each is named by its words, its count included, and the name follows the count
            Assert.Equal(["All (30)", "Earned (12)", "Locked (18)"], Enumerable.Range(0, 3).Select(i => SpokenName(page.Tabs.ContainerFromIndex(i)!)));
            File.WriteAllText(_paths.TrophiesFile, Sample + "12=2026-11-11T09:30:00Z\n");
            await window.WatchAchievements();
            LauncherHeadlessTests.Pump(window);
            Assert.Equal(["All (30)", "Earned (13)", "Locked (17)"], Enumerable.Range(0, 3).Select(i => SpokenName(page.Tabs.ContainerFromIndex(i)!)));

            // the language tiles (two lines of words each) are named by the language, the window sizes by what their cells show (not by the record's own name)
            model.Page = LauncherPage.Language;
            LauncherHeadlessTests.Pump(window);
            ListBox tiles = window.PageHost.GetVisualDescendants().OfType<ListBox>().Single(list => list.Classes.Contains("Tiles"));
            Assert.Equal(["Japanese", "English", "Simplified Chinese", "Traditional Chinese"], Enumerable.Range(0, 4).Select(i => SpokenName(tiles.ContainerFromIndex(i)!)));
            model.Page = LauncherPage.Display;
            LauncherHeadlessTests.Pump(window);
            ListBox sizes = window.PageHost.GetVisualDescendants().OfType<ListBox>().Single(list => list.Classes.Contains("Segmented"));
            Assert.Equal(model.WindowSizes.Select(size => size.Label), Enumerable.Range(0, model.WindowSizes.Count).Select(i => SpokenName(sizes.ContainerFromIndex(i)!)));
            window.Close();
        });
    }

    [Fact]
    public async Task AScreenReaderSaysEachSwitchByTheTitleOfItsRow()
    {
        UnixLauncherModel model = Model();
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model, TimeSpan.FromHours(1));
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);

            // the notice's switch and the sound's under it are told apart (VoiceOver, Orca: "Sound when notified, check box, off"), as on Windows: a switch
            // has no words of its own, and its row is no control a screen reader stops on
            Assert.Equal(LauncherTexts.AchievementsNoticeLabel, SpokenName(page.NoticeSwitch));
            Assert.Equal(LauncherTexts.AchievementsSoundLabel, SpokenName(page.SoundSwitch));

            // every switch of every page is named by the title of its row, the words on show beside it
            var named = new List<(LauncherPage Page, string Title)>();
            foreach (LauncherPage shown in Enum.GetValues<LauncherPage>())
            {
                model.Page = shown;
                LauncherHeadlessTests.Pump(window);
                foreach (CheckBox toggle in window.PageHost.GetVisualDescendants().OfType<CheckBox>().Where(box => box.Classes.Contains("Switch") && box.IsEffectivelyVisible))
                {
                    // a setting's row, or (on the Play page) the grid of the title and the switch
                    Visual row = toggle.GetVisualAncestors().OfType<HeaderedContentControl>().FirstOrDefault(ancestor => ancestor.Classes.Contains("SettingRow")) ?? toggle.GetVisualParent()!;
                    string title = row.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("RowTitle")).Text!;
                    Assert.Equal((shown, title), (shown, SpokenName(toggle)));
                    named.Add((shown, title));
                }
            }

            var expected = new List<(LauncherPage Page, string Title)>
            {
                (LauncherPage.Play, "Fullscreen"), (LauncherPage.Display, "Fullscreen"), (LauncherPage.Display, "VSync"),
            };
            if (model.PortableSavesAvailable)
            {
                expected.Add((LauncherPage.Saves, "Keep saves next to the program (portable)"));
            }

            expected.AddRange([
                (LauncherPage.Achievements, LauncherTexts.AchievementsNoticeLabel), (LauncherPage.Achievements, LauncherTexts.AchievementsSoundLabel),
                (LauncherPage.Help, "Check for updates when the launcher starts"),
            ]);
            Assert.Equal(expected, named);
            window.Close();
        });
    }

    [Fact]
    public async Task ACommandClickOnTheChosenTabStillLeavesATabChosenThatTheRowsFollow()
    {
        File.WriteAllText(_paths.TrophiesFile, Sample);
        UnixLauncherModel model = Model();
        await LauncherHeadlessTests.Run(async () =>
        {
            MainWindow window = LauncherHeadlessTests.Show(model);
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            page.Tabs.SelectedIndex = 1;
            LauncherHeadlessTests.Pump(window);
            Assert.Equal(12, Rows(page).Length);

            // the tabs are under the switches' card, which puts them below what the page shows when it opens: the page is scrolled to them first, as the player
            // would, and the pointer is over each where the clicks below go (a click beside them would leave the choice as it was and prove nothing)
            Control chosen = page.Tabs.ContainerFromIndex(1)!;
            chosen.BringIntoView();
            LauncherHeadlessTests.Pump(window);
            foreach (Control tab in new[] { chosen, page.Tabs.ContainerFromIndex(2)! })
            {
                Point centre = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), window)!.Value;
                window.MouseMove(centre, RawInputModifiers.None);
                LauncherHeadlessTests.Pump(window);
                Assert.True(tab.IsPointerOver, $"the pointer at {centre} (the page scrolled by {page.Scroll.Offset.Y:0}) is not over the tab '{Texts(tab).FirstOrDefault()}'");
            }

            // Ctrl+click (Cmd+click on a Mac) on the chosen tab, which in a list of one choice takes the choice away
            Point middle = chosen.TranslatePoint(new Point(chosen.Bounds.Width / 2, chosen.Bounds.Height / 2), window)!.Value;
            RawInputModifiers command = CommandModifier(window);
            window.MouseDown(middle, MouseButton.Left, command);
            window.MouseUp(middle, MouseButton.Left, command);
            LauncherHeadlessTests.Pump(window);

            // a tab is still chosen and marked, the model has the same one, and the rows are that tab's
            Assert.InRange(page.Tabs.SelectedIndex, 0, 2);
            Assert.Equal(page.Tabs.SelectedIndex, model.AchievementTabIndex);
            Assert.True(Assert.IsType<ListBoxItem>(page.Tabs.ContainerFromIndex(model.AchievementTabIndex)).IsSelected, "no tab is marked");
            Assert.Equal(new[] { 30, 12, 18 }[model.AchievementTabIndex], Rows(page).Length);

            // a plain click on another tab still chooses it
            Control locked = page.Tabs.ContainerFromIndex(2)!;
            Point there = locked.TranslatePoint(new Point(locked.Bounds.Width / 2, locked.Bounds.Height / 2), window)!.Value;
            window.MouseDown(there, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(there, MouseButton.Left, RawInputModifiers.None);
            LauncherHeadlessTests.Pump(window);
            Assert.Equal((2, 2, 18), (page.Tabs.SelectedIndex, model.AchievementTabIndex, Rows(page).Length));
            window.Close();
        });
    }

    [Fact]
    public async Task TheNoticeSwitchFollowsTheGameWhileTheWindowStaysInFront()
    {
        var reads = new List<bool>();
        UnixLauncherModel model = Model(settingsRead: () => { lock (reads) { reads.Add(Dispatcher.UIThread.CheckAccess()); } });
        int Reads() { lock (reads) { return reads.Count; } }
        await LauncherHeadlessTests.Run(async () =>
        {
            // the test makes the timer's looks itself (WatchAchievements is what the timer calls): the timer of this window does not look by itself
            MainWindow window = LauncherHeadlessTests.Show(model, TimeSpan.FromHours(1));
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            Assert.False(page.NoticeSwitch.IsChecked);
            var raised = new List<string?>();
            model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            // the game runs beside the window and its Config row writes trophy_notice=on (the player's other keys stay). The window is not activated: the
            // page's look on its two-second timer shows it
            File.WriteAllText(_paths.SettingsFile, "prompts=xbox\ntrophy_notice=on\n");
            File.SetLastWriteTimeUtc(_paths.SettingsFile, DateTime.UtcNow.AddMinutes(1));
            await window.WatchAchievements();
            LauncherHeadlessTests.Pump(window);
            Assert.True(model.TrophyNotice);
            Assert.True(page.NoticeSwitch.IsChecked);
            Assert.Contains(nameof(model.TrophyNotice), raised);

            // a look that finds settings.ini as it was says nothing
            raised.Clear();
            await window.WatchAchievements();
            Assert.DoesNotContain(nameof(model.TrophyNotice), raised);

            // the switch turned off writes off, over what the game wrote, and keeps the game's other keys; the next look changes nothing
            page.NoticeSwitch.IsChecked = false;
            LauncherHeadlessTests.Pump(window);
            string[] lines = File.ReadAllLines(_paths.SettingsFile);
            Assert.Contains("trophy_notice=off", lines);
            Assert.Contains("prompts=xbox", lines);
            raised.Clear();
            int before = Reads();
            await window.WatchAchievements();
            LauncherHeadlessTests.Pump(window);
            Assert.DoesNotContain(nameof(model.TrophyNotice), raised);
            Assert.False(page.NoticeSwitch.IsChecked);
            Assert.Equal(before, Reads());
            Assert.Equal(Tsuki.Ui.PromptSetting.Xbox, (Tsuki.Ui.PromptSetting)model.PromptsIndex);

            // and on again: the launcher's own writes are not read back by the looks
            page.NoticeSwitch.IsChecked = true;
            LauncherHeadlessTests.Pump(window);
            Assert.Contains("trophy_notice=on", File.ReadAllLines(_paths.SettingsFile));
            await window.WatchAchievements();
            Assert.Equal(before, Reads());

            // the game puts the prompts back to Auto and clears its one-start keys: the file it writes holds its comments and nothing but defaults. That is
            // what it holds, and the launcher shows it: the prompts are Auto again and the notice, which the file no longer names, is off; the next look
            // finds the file as it read it and reads nothing
            File.WriteAllText(_paths.SettingsFile, "# settings of the game and the launcher\nprompts=auto\n");
            File.SetLastWriteTimeUtc(_paths.SettingsFile, DateTime.UtcNow.AddMinutes(2));
            raised.Clear();
            await window.WatchAchievements();
            LauncherHeadlessTests.Pump(window);
            Assert.Equal(Tsuki.Ui.PromptSetting.Auto, (Tsuki.Ui.PromptSetting)model.PromptsIndex);
            Assert.Contains(nameof(model.PromptsIndex), raised);
            Assert.False(page.NoticeSwitch.IsChecked);
            Assert.Contains(nameof(model.TrophyNotice), raised);
            before = Reads();
            raised.Clear();
            await window.WatchAchievements();
            Assert.Equal(before, Reads());
            Assert.DoesNotContain(nameof(model.PromptsIndex), raised);
            Assert.DoesNotContain(nameof(model.TrophyNotice), raised);

            // the looks read the file once for each of the game's two writes, on a worker, never on the window's thread
            Assert.Equal(2, Reads());
            lock (reads) { Assert.DoesNotContain(true, reads); }
            window.Close();
        });
    }

    [Fact]
    public async Task ASettingsFileAnotherProgramHoldsIsNotTakenForTheDefaults()
    {
        // Windows, and Linux and macOS through .NET's advisory lock: a program that holds a file without sharing keeps a reader out
        if (!AHoldKeepsReadersOut()) { Assert.Skip(NoFileLocks); }
        File.WriteAllText(_paths.SettingsFile, "prompts=xbox\ntrophy_notice=on\ntrophy_sound=on\n");
        var reads = new List<bool>();
        UnixLauncherModel model = Model(settingsRead: () => { lock (reads) { reads.Add(Dispatcher.UIThread.CheckAccess()); } });
        int Reads() { lock (reads) { return reads.Count; } }
        await LauncherHeadlessTests.Run(async () =>
        {
            // a window whose timer does not look by itself: the test's looks are the only ones
            MainWindow window = LauncherHeadlessTests.Show(model, TimeSpan.FromHours(1));
            await model.RefreshAsync();
            AchievementsPage page = Open(window, model);
            Assert.True(page.NoticeSwitch.IsChecked);
            Assert.True(page.SoundSwitch.IsChecked);
            var raised = new List<string?>();
            model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            // the game writes off, and another program (a sync tool) holds the file without sharing at the moments of the next looks. What cannot be read is
            // not taken for a file of defaults (both switches off): the switches and the prompts keep what they show, each look tries again (on a worker: the
            // window's thread never waits for the holder), and the look after the program let go shows off
            File.WriteAllText(_paths.SettingsFile, "prompts=xbox\ntrophy_notice=off\ntrophy_sound=off\n");
            File.SetLastWriteTimeUtc(_paths.SettingsFile, DateTime.UtcNow.AddMinutes(2));
            using (new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                for (int look = 1; look <= 2; look++)
                {
                    int before = Reads();
                    await window.WatchAchievements();
                    LauncherHeadlessTests.Pump(window);
                    Assert.Equal(before + 1, Reads());
                    Assert.DoesNotContain(nameof(model.TrophyNotice), raised);
                    Assert.DoesNotContain(nameof(model.TrophySound), raised);
                    Assert.DoesNotContain(nameof(model.PromptsIndex), raised);
                    Assert.True(page.NoticeSwitch.IsChecked, "a settings.ini that could not be read was taken for one of defaults");
                    Assert.True(page.SoundSwitch.IsChecked, "a settings.ini that could not be read was taken for one of defaults");
                    Assert.Equal(Tsuki.Ui.PromptSetting.Xbox, (Tsuki.Ui.PromptSetting)model.PromptsIndex);
                }
            }

            await window.WatchAchievements();
            LauncherHeadlessTests.Pump(window);
            Assert.False(page.NoticeSwitch.IsChecked);
            Assert.False(page.SoundSwitch.IsChecked);
            Assert.Contains(nameof(model.TrophyNotice), raised);
            Assert.Contains(nameof(model.TrophySound), raised);
            Assert.Equal(Tsuki.Ui.PromptSetting.Xbox, (Tsuki.Ui.PromptSetting)model.PromptsIndex);
            lock (reads) { Assert.DoesNotContain(true, reads); }
            window.Close();
        });
    }

    [Fact]
    public async Task ARecordAnotherProgramHeldIsReadAgainOnceItIsFreeThoughItDidNotChange()
    {
        // a program that holds a file without sharing keeps every other one out: on Windows by its share mode, on Linux and macOS by the advisory lock .NET
        // takes for FileShare.None, which a reader's open (a shared lock) is refused under
        if (!AHoldKeepsReadersOut()) { Assert.Skip(NoFileLocks); }
        File.WriteAllText(_paths.TrophiesFile, Sample);
        var held = new FileStream(_paths.TrophiesFile, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            // the launcher starts while another program (a sync or backup tool) holds trophies.ini: the page says the record cannot be read
            UnixLauncherModel model = Model();
            await LauncherHeadlessTests.Run(async () =>
            {
                MainWindow window = LauncherHeadlessTests.Show(model);
                await model.RefreshAsync();
                AchievementsPage page = Open(window, model);
                await LauncherHeadlessTests.Until(window, () => page.DamagedBox.IsEffectivelyVisible);
                Assert.True(page.DamagedBox.IsEffectivelyVisible);
                Assert.Equal(0, model.AchievementsEarned);

                // the other program lets go; the file is as it was, its size and time unchanged. The next look reads it again and shows it whole, with its dates
                long length = new FileInfo(_paths.TrophiesFile).Length;
                DateTime time = File.GetLastWriteTimeUtc(_paths.TrophiesFile);
                held.Dispose();
                await window.WatchAchievements();
                LauncherHeadlessTests.Pump(window);
                Assert.Equal((length, time), (new FileInfo(_paths.TrophiesFile).Length, File.GetLastWriteTimeUtc(_paths.TrophiesFile)));
                Assert.False(page.DamagedBox.IsEffectivelyVisible, "the page still says the record cannot be read");
                Assert.Equal(12, model.AchievementsEarned);
                Assert.Equal(Local(2026, 11, 9, 18, 41), Texts(Rows(page)[8])[3]);

                // read whole, it is not read again until it changes
                Assert.True(model.ReadAchievementsAgainAsync(onlyIfChanged: true).IsCompletedSuccessfully);
                window.Close();
            });
        }
        finally
        {
            held.Dispose();
        }
    }

    [Fact]
    public async Task ARestoreOrAnImportFromTheSavesPageKeepsWhatTheReplacedSaveImplied()
    {
        // an old backup with no progress; later all 25 of Ciel's lessons done and seen, and no record yet (a profile from before v0.1.9): the page shows the two
        // lesson achievements from the save alone
        WriteSave(lessons: false);
        BackupInfo old = SaveTools.Backup(_paths, new DateTime(2026, 11, 1, 8, 0, 0))!;
        WriteSave(lessons: true);
        string image = Path.Combine(_root, "savedata000");
        File.WriteAllBytes(image, SaveFile.ExportPs4Image(new SaveStore()));
        UnixLauncherModel model = Model(anyGameRunning: () => false);
        model.QuestionRequested += _ => Task.FromResult(true);
        Assert.Equal([28, 29], EarnedIds(model));

        // restored on the Saves page: what the replaced save implied is recorded first, without a date, and the page keeps it
        await model.RestoreAsync(old);
        Assert.Equal([28, 29], RecordedIds());
        File.SetLastWriteTimeUtc(_paths.SaveFile, DateTime.UtcNow.AddSeconds(-10));
        await model.ReadAchievementsAgainAsync();
        Assert.Equal([28, 29], EarnedIds(model));

        // a PS4 save imported over the lessons' save: the same
        File.Delete(_paths.TrophiesFile);
        WriteSave(lessons: true);
        await model.ImportAsync(image);
        Assert.Equal([28, 29], RecordedIds());

        // while a game runs anywhere on the machine the record is left to that game, which writes it while it runs
        File.Delete(_paths.TrophiesFile);
        WriteSave(lessons: true);
        UnixLauncherModel whileAGameRuns = Model(anyGameRunning: () => true);
        whileAGameRuns.QuestionRequested += _ => Task.FromResult(true);
        await whileAGameRuns.RestoreAsync(old);
        Assert.False(File.Exists(_paths.TrophiesFile), "the launcher wrote the record while a game ran");

        // and both pass the page's game folder, whose scene table tells the chapters the save finished (the stand-in here has none; the Data tests have one)
        string source = File.ReadAllText(Path.Combine(RepositoryFolder(), "src", "Tsuki.Launcher.Avalonia", "UnixLauncherModel.cs"));
        Assert.Contains("SaveTools.Restore(_paths, backup, DateTime.Now, AchievementFolder, AnyGameRunning);", source, StringComparison.Ordinal);
        Assert.Contains("SaveTools.ImportPs4(_paths, imagePath, DateTime.Now, AchievementFolder, AnyGameRunning);", source, StringComparison.Ordinal);
    }

    /// <param name="game">The game folder; null for one that is not there.</param>
    /// <param name="icons">Who gives the icons; null for the launcher's own way to the game's pictures.</param>
    /// <param name="anyGameRunning">Whether a game runs on the machine; null to ask the machine.</param>
    /// <param name="iconSource">Who gives the icons, in place of <paramref name="icons"/>.</param>
    /// <param name="emblems">Who draws the grades' emblems; null for the core.</param>
    /// <param name="settingsRead">Told on the reading thread each time a look reads settings.ini.</param>
    private UnixLauncherModel Model(
        string? game = null, IconDesk? icons = null, Func<bool>? anyGameRunning = null, Func<string, int, AchievementIcon?>? iconSource = null,
        Func<TrophyGrade, AchievementIcon>? emblems = null, Action? settingsRead = null) => new(_paths, AppContext.BaseDirectory, game ?? Path.Combine(_root, "no-game"))
    {
        UpdateSource = UpdateSource.ForLoopback(new Uri("http://127.0.0.1:9/")),
        CurrentVersionOverride = new ReleaseVersion(0, 0, 1),
        MacBundleUpdate = false,
        IconSource = iconSource ?? (icons is null ? Achievements.Icon : icons.Icon),
        EmblemSource = emblems ?? Achievements.EmblemIcon,
        SettingsLookRead = settingsRead,
        AnyGameRunning = anyGameRunning ?? GameLauncher.IsGameRunning,
    };

    /// <summary>
    /// True when a file another program holds open without sharing keeps a reader out: always on Windows, and on Linux and macOS through the advisory lock .NET
    /// takes for FileShare.None (a reader's open takes a shared one and is refused), unless file locking is turned off for .NET.
    /// </summary>
    private static bool AHoldKeepsReadersOut()
    {
        if (OperatingSystem.IsWindows()) { return true; }
        string? variable = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        bool off = variable == "1" || string.Equals(variable, "true", StringComparison.OrdinalIgnoreCase) || (AppContext.TryGetSwitch("System.IO.DisableFileLocking", out bool disabled) && disabled);
        return !off;
    }

    private const string NoFileLocks = "file locking is turned off for .NET (DOTNET_SYSTEM_IO_DISABLEFILELOCKING): a held file does not keep a reader out";

    /// <summary>A folder the launcher takes for an incomplete game (an eboot.bin and image/, nothing in them): no name, no scene and no picture can be read from it.</summary>
    private string GameFolder(string name = "CUSA47999")
    {
        string game = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(game, "image"));
        File.WriteAllBytes(Path.Combine(game, "eboot.bin"), new byte[64]);
        return game;
    }

    /// <summary>
    /// The stand-in for the game's pictures (the model's <see cref="UnixLauncherModel.IconSource"/>): it notes who asks, gives each id a flat picture of its
    /// own colour, or none where <see cref="Has"/> says so, and lets a test hold every answer back like a slow disk.
    /// </summary>
    private sealed class IconDesk
    {
        private readonly object _gate = new();
        private readonly List<(string Folder, int Id)> _asked = [];
        private readonly Dictionary<string, ManualResetEventSlim> _doors = [];
        private int _busy;
        private int _mostAtOnce;

        /// <summary>Closed: whoever asks waits (at most 30 s) until it opens.</summary>
        public ManualResetEventSlim Door { get; } = new(initialState: true);

        /// <summary>A door of its own for the pictures of one game folder, closed when first asked for here.</summary>
        public ManualResetEventSlim DoorOf(string folder)
        {
            lock (_gate)
            {
                if (!_doors.TryGetValue(folder, out ManualResetEventSlim? door)) { _doors[folder] = door = new ManualResetEventSlim(initialState: false); }
                return door;
            }
        }

        /// <summary>Whether the game folder holds the picture of an id.</summary>
        public Func<int, bool> Has { get; init; } = _ => true;

        /// <summary>Every request so far, in the order they came.</summary>
        public (string Folder, int Id)[] Asked
        {
            get { lock (_gate) { return [.. _asked]; } }
        }

        /// <summary>How many requests were being answered at the same moment, at most.</summary>
        public int MostAtOnce
        {
            get { lock (_gate) { return _mostAtOnce; } }
        }

        public AchievementIcon? Icon(string folder, int id)
        {
            ManualResetEventSlim? own;
            int game;
            lock (_gate)
            {
                _asked.Add((folder, id));
                _mostAtOnce = Math.Max(_mostAtOnce, ++_busy);
                _doors.TryGetValue(folder, out own);
                game = _asked.Select(asked => asked.Folder).Distinct().ToList().IndexOf(folder);
            }

            try
            {
                Door.Wait(TimeSpan.FromSeconds(30));
                own?.Wait(TimeSpan.FromSeconds(30));
                if (!Has(id)) { return null; }
                (byte b, byte g, byte r, byte a) = Colour(id, game);
                byte[] rgba = new byte[240 * 240 * 4];
                for (int i = 0; i < rgba.Length; i += 4) { (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, a); }
                return new AchievementIcon(240, rgba);
            }
            finally
            {
                lock (_gate) { _busy--; }
            }
        }

        /// <summary>
        /// The colour of the picture of an id, as a bitmap keeps it: blue, green, red, alpha. The blue tells the game folders apart: <paramref name="game"/>
        /// counts them in the order they were first asked for.
        /// </summary>
        public static (byte B, byte G, byte R, byte A) Colour(int id, int game = 0) => ((byte)(90 + (40 * game)), (byte)(200 - (5 * id)), (byte)(40 + (7 * id)), (byte)255);
    }

    /// <summary>A save.dat whose system block has all 25 of Ciel's lessons (scenes 533..557) done and seen, or none.</summary>
    private void WriteSave(bool lessons)
    {
        var store = new SaveStore();
        foreach (int scene in lessons ? Enumerable.Range(533, 25) : [])
        {
            // scene_done(n) is bit n % 16 of G[300 + n / 16], scene_seen(n) the same bit of G[110 + n / 16]
            foreach (int first in new[] { 300, 110 })
            {
                int word = first + (scene / 16);
                store.SystemData.Block.SetG(word, (ushort)(store.SystemData.Block.GetG(word) | (1 << (scene % 16))));
            }
        }

        SaveFile.WriteAtomic(_paths.SaveFile, store);
        // older than a reading's settle time, as a save the game wrote some time ago is
        File.SetLastWriteTimeUtc(_paths.SaveFile, DateTime.UtcNow.AddSeconds(-10));
    }

    private int[] RecordedIds()
    {
        TrophyLog record = TrophyLog.ReadFile(_paths.TrophiesFile, out TrophyFileState state);
        Assert.Equal(TrophyFileState.Read, state);
        Assert.All(record.Ids, id => Assert.Null(record.EarnedUtc(id)));
        return [.. record.Ids];
    }

    private static int[] EarnedIds(UnixLauncherModel model) => [.. model.AchievementRows.Where(row => row.Earned).Select(row => row.Row.Id)];

    /// <summary>The repository the tests were built from (its src/Tsuki.slnx).</summary>
    private static string RepositoryFolder()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "src", "Tsuki.slnx")))
        {
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)) ?? throw new InvalidOperationException("no repository above the test");
        }

        return root;
    }

    /// <summary>The name a screen reader says for a control (its automation peer's).</summary>
    private static string? SpokenName(Control control) => ControlAutomationPeer.CreatePeerForElement(control).GetName();

    /// <summary>A key pressed and let go on the focused control, and what follows done.</summary>
    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
        LauncherHeadlessTests.Pump(window);
    }

    /// <summary>
    /// Presses Tab (Shift+Tab when <paramref name="back"/>) until the focus has gone into <paramref name="page"/> and out of it again, and gives the controls of
    /// the page it stopped on, in that order (at most 20 presses).
    /// </summary>
    private static Control[] TabStops(MainWindow window, AchievementsPage page, bool back)
    {
        RawInputModifiers modifiers = back ? RawInputModifiers.Shift : RawInputModifiers.None;
        var stops = new List<Control>();
        for (int i = 0; i < 20; i++)
        {
            window.KeyPressQwerty(PhysicalKey.Tab, modifiers);
            window.KeyReleaseQwerty(PhysicalKey.Tab, modifiers);
            LauncherHeadlessTests.Pump(window);
            if (window.FocusManager?.GetFocusedElement() is Control focused && (focused == page || page.IsVisualAncestorOf(focused)))
            {
                stops.Add(focused);
            }
            else if (stops.Count > 0)
            {
                break;
            }
        }

        return [.. stops];
    }

    /// <summary>The modifier a list takes for "add to the choice or take away": Ctrl, or Cmd on a Mac (as the platform says).</summary>
    private static RawInputModifiers CommandModifier(TopLevel window)
    {
        KeyModifiers command = window.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        return (command.HasFlag(KeyModifiers.Control) ? RawInputModifiers.Control : RawInputModifiers.None) | (command.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.None);
    }

    private static AchievementsPage Open(MainWindow window, UnixLauncherModel model)
    {
        model.Page = LauncherPage.Achievements;
        LauncherHeadlessTests.Pump(window);
        return Assert.IsType<AchievementsPage>(window.PageHost.Content);
    }

    private static Control[] Rows(AchievementsPage page) => [.. page.AchievementList.GetRealizedContainers()];

    /// <summary>The words on show inside <paramref name="control"/>, in the order they are drawn.</summary>
    private static string[] Texts(Visual control) =>
        [.. control.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text)).Select(text => text.Text!)];

    /// <summary>The square at a row's left that shows its tile: the icon, the blank tile, the emblem or the padlock.</summary>
    private static Image TileImage(Visual row) => row.GetVisualDescendants().OfType<Image>().Single(image => image.Name == "Tile");

    /// <summary>The bitmap a row's tile shows.</summary>
    private static Bitmap Tile(Visual row)
    {
        Image tile = TileImage(row);
        Assert.True(tile.IsEffectivelyVisible, "the row shows no tile");
        return Assert.IsAssignableFrom<Bitmap>(tile.Source);
    }

    /// <summary>The pixels of a bitmap the model made, row by row: blue, green, red, alpha, the colours multiplied by the alpha.</summary>
    private static byte[] Pixels(Bitmap bitmap)
    {
        using ILockedFramebuffer frame = Assert.IsAssignableFrom<WriteableBitmap>(bitmap).Lock();
        int width = frame.Size.Width;
        byte[] pixels = new byte[width * frame.Size.Height * 4];
        for (int y = 0; y < frame.Size.Height; y++) { Marshal.Copy(frame.Address + (y * frame.RowBytes), pixels, y * width * 4, width * 4); }
        return pixels;
    }

    /// <summary>The pixels the page's bitmap of a core picture must have (the launcher's own conversion, which has a test of its own).</summary>
    private static byte[] TilePixels(AchievementIcon picture)
    {
        using WriteableBitmap bitmap = UnixLauncherArt.TileBitmap(picture, UnixLauncherModel.TilePixels);
        return Pixels(bitmap);
    }

    /// <summary>One pixel of <see cref="Pixels"/> of a square bitmap.</summary>
    private static (byte B, byte G, byte R, byte A) At(byte[] pixels, int x, int y)
    {
        int side = (int)Math.Sqrt(pixels.Length / 4);
        int at = ((y * side) + x) * 4;
        return (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]);
    }

    private static int Count(byte[] pixels, (byte B, byte G, byte R, byte A) colour)
    {
        int count = 0;
        for (int at = 0; at < pixels.Length; at += 4)
        {
            if ((pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) == colour) { count++; }
        }

        return count;
    }

    /// <summary>The picture of a grade in the summary: its emblem.</summary>
    private static Image EmblemImage(Visual tally) => tally.GetVisualDescendants().OfType<Image>().Single(image => image.Name == "Emblem");

    /// <summary>The bitmap of a grade's emblem in the summary.</summary>
    private static Bitmap Emblem(Visual tally) => Assert.IsAssignableFrom<Bitmap>(EmblemImage(tally).Source);

    /// <summary>The pixels the summary's bitmap of a grade's emblem must have (the core's emblem, by the launcher's own conversion).</summary>
    private static byte[] EmblemPixels(TrophyGrade grade)
    {
        using WriteableBitmap bitmap = UnixLauncherArt.TileBitmap(Achievements.EmblemIcon(grade), UnixLauncherModel.EmblemPixels);
        return Pixels(bitmap);
    }

    /// <summary>The grade's word of a row (its third words).</summary>
    private static TextBlock GradeText(Visual row) => row.GetVisualDescendants().OfType<TextBlock>().ElementAt(2);

    /// <summary>The name of a row (its first words).</summary>
    private static TextBlock Name(Visual row) => row.GetVisualDescendants().OfType<TextBlock>().First();

    /// <summary>The sign before a row's state: a check, or a lock.</summary>
    private static Shape StateSign(Visual row) => row.GetVisualDescendants().OfType<Shape>().Last(shape => shape.Stroke is not null);

    private static Color Ink(TextBlock text) => ((ISolidColorBrush)text.Foreground!).Color;

    private static double Right(Visual visual, Visual window) => visual.TranslatePoint(new Point(visual.Bounds.Width, 0), window)!.Value.X;

    /// <summary>A UTC time of 2026 as a row shows it: local, "9 Nov 2026, 18:41".</summary>
    private static string Local(int year, int month, int day, int hour, int minute) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
}
