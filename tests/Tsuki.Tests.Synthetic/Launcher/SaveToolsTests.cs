using Tsuki.App.Services;
using Tsuki.Engine.Save;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Synthetic.Save;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>Package K17: the saves page (summary, backup, restore, PS4 import) over a temporary %APPDATA%\Tsuki.</summary>
public sealed class SaveToolsTests : IDisposable
{
    private static readonly SlotDate T0 = new(2026, 10, 27, 9, 0, 0);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k17-saves-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;

    public SaveToolsTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "Tsuki"));
        Directory.CreateDirectory(_paths.DataDirectory);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private static SaveStore Store(int manualSlots, short language = 1, bool fullscreen = false)
    {
        var store = new SaveStore();
        for (int i = 0; i < manualSlots; i++)
        {
            store.SaveManual(i, SaveStoreTests.Staging((ulong)(0x40 + i)), T0 with { Minute = i });
        }

        store.QuickSave(SaveStoreTests.Staging(0x70), T0 with { Minute = 30 });
        store.SystemData.Config.Language = language;
        store.SystemData.Config.Bytes[0xA9] = (byte)(fullscreen ? 1 : 0);
        return store;
    }

    private void WriteSave(int slots, short language = 1, bool fullscreen = false) =>
        SaveFile.WriteAtomic(_paths.SaveFile, Store(slots, language, fullscreen));

    [Fact]
    public void ASummaryCountsSlotsAndReadsTheConfig()
    {
        Assert.False(SaveTools.Summarize(_paths.SaveFile).Exists);
        Assert.Contains("No saves yet", SaveTools.Summarize(_paths.SaveFile).Describe(), StringComparison.Ordinal);

        WriteSave(3, language: 0, fullscreen: true);
        SaveSummary s = SaveTools.Summarize(_paths.SaveFile);
        Assert.True(s.Exists);
        Assert.Null(s.Error);
        Assert.Equal(4, s.Slots);                      // three manual and the quick slot
        Assert.Equal(3, s.ManualSlots);
        Assert.Equal(0, s.ConfigLanguage);
        Assert.Equal(1, s.WindowMode);
        Assert.NotNull(s.LastWrite);
        Assert.Equal("4 save slots", s.Describe());
    }

    [Fact]
    public void ADamagedFileIsReportedNotThrown()
    {
        File.WriteAllBytes(_paths.SaveFile, new byte[200]);
        SaveSummary s = SaveTools.Summarize(_paths.SaveFile);
        Assert.True(s.Exists);
        Assert.NotNull(s.Error);
        Assert.Contains("cannot be read", s.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABackupCopiesSaveAndSettingsIntoATimestampFolder()
    {
        WriteSave(2);
        File.WriteAllText(_paths.SettingsFile, "prompts=xbox\n");
        BackupInfo? backup = SaveTools.Backup(_paths, new DateTime(2026, 10, 27, 15, 30, 5));
        Assert.NotNull(backup);
        Assert.Equal("20261027-153005", backup.Name);
        Assert.Equal(Path.Combine(_paths.BackupsDirectory, "20261027-153005"), backup.Path);
        Assert.Equal(["save.dat", "settings.ini"], backup.Files);
        Assert.Equal(new DateTime(2026, 10, 27, 15, 30, 5), backup.Created);
        Assert.Equal(File.ReadAllBytes(_paths.SaveFile), File.ReadAllBytes(Path.Combine(backup.Path, "save.dat")));
        Assert.Equal("prompts=xbox\n", File.ReadAllText(Path.Combine(backup.Path, "settings.ini")));
        Assert.Equal(3, backup.Save.Slots);
        Assert.True(backup.Bytes > 0);
    }

    [Fact]
    public void ABackupInTheSameSecondGetsASuffixAndNothingIsOverwritten()
    {
        WriteSave(1);
        var t = new DateTime(2026, 10, 27, 15, 30, 5);
        BackupInfo a = SaveTools.Backup(_paths, t)!;
        BackupInfo b = SaveTools.Backup(_paths, t)!;
        Assert.NotEqual(a.Path, b.Path);
        Assert.EndsWith("-2", b.Name, StringComparison.Ordinal);
        Assert.Equal(2, SaveTools.List(_paths).Count);
    }

    [Fact]
    public void WithNothingToBackUpNothingIsMade()
    {
        Assert.Null(SaveTools.Backup(_paths, DateTime.Now));
        Assert.False(Directory.Exists(_paths.BackupsDirectory));
        Assert.Empty(SaveTools.List(_paths));
    }

    [Fact]
    public void BackupsAreListedNewestFirst()
    {
        WriteSave(1);
        SaveTools.Backup(_paths, new DateTime(2026, 10, 1, 8, 0, 0));
        SaveTools.Backup(_paths, new DateTime(2026, 10, 27, 8, 0, 0));
        SaveTools.Backup(_paths, new DateTime(2026, 10, 15, 8, 0, 0));
        Assert.Equal(["20261027-080000", "20261015-080000", "20261001-080000"], SaveTools.List(_paths).Select(b => b.Name));
    }

    [Fact]
    public void RestoringPutsTheOldSaveBackAndBacksUpTheCurrentOneFirst()
    {
        WriteSave(2, language: 0);
        BackupInfo old = SaveTools.Backup(_paths, new DateTime(2026, 10, 1, 8, 0, 0))!;
        WriteSave(7, language: 3);                              // later play
        byte[] current = File.ReadAllBytes(_paths.SaveFile);

        BackupInfo? undo = SaveTools.Restore(_paths, old, new DateTime(2026, 10, 27, 9, 0, 0));

        Assert.NotNull(undo);                                   // the replaced save is in a backup of its own
        Assert.Equal(current, File.ReadAllBytes(Path.Combine(undo.Path, "save.dat")));
        SaveSummary now = SaveTools.Summarize(_paths.SaveFile);
        Assert.Equal(3, now.Slots);
        Assert.Equal(0, now.ConfigLanguage);
        Assert.Equal(File.ReadAllBytes(Path.Combine(old.Path, "save.dat")), File.ReadAllBytes(_paths.SaveFile));
        Assert.False(File.Exists(_paths.SaveFile + ".restore.tmp"));
        Assert.Equal(2, SaveTools.List(_paths).Count);

        // and a restore can be undone
        SaveTools.Restore(_paths, undo, new DateTime(2026, 10, 27, 9, 5, 0));
        Assert.Equal(current, File.ReadAllBytes(_paths.SaveFile));
    }

    [Fact]
    public void RestoringKeepsTheSettingsAsTheyAre()
    {
        WriteSave(1);
        File.WriteAllText(_paths.SettingsFile, "prompts=keyboard\n");
        BackupInfo old = SaveTools.Backup(_paths, new DateTime(2026, 10, 1, 8, 0, 0))!;
        File.WriteAllText(_paths.SettingsFile, "prompts=xbox\n");
        SaveTools.Restore(_paths, old, DateTime.Now);
        Assert.Equal("prompts=xbox\n", File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public void ARestoreFromABackupWithoutASaveFails()
    {
        WriteSave(1);
        string folder = Path.Combine(_paths.BackupsDirectory, "20260101-000000");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.ini"), "x");
        BackupInfo hollow = SaveTools.List(_paths).Single();
        Assert.Throws<FileNotFoundException>(() => SaveTools.Restore(_paths, hollow, DateTime.Now));
        Assert.Single(SaveTools.List(_paths));                  // nothing was backed up or replaced on the way
    }

    // -- PS4 images ------------------------------------------------------------------------------------------------------

    private string WriteImage(string folder, string name, SaveStore store)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, SaveFile.ExportPs4Image(store));
        return path;
    }

    [Fact]
    public void APs4ImageReplacesTheSaveAfterABackupAndIsOnlyRead()
    {
        WriteSave(1, language: 1);
        string image = WriteImage(Path.Combine(_root, "shad"), "savedata000", Store(5, language: 3));
        File.SetAttributes(image, FileAttributes.ReadOnly);       // a write would throw
        byte[] imageBefore = File.ReadAllBytes(image);
        DateTime stamp = File.GetLastWriteTimeUtc(image);
        byte[] saveBefore = File.ReadAllBytes(_paths.SaveFile);

        (BackupInfo? backup, int slots) = SaveTools.ImportPs4(_paths, image, new DateTime(2026, 10, 27, 10, 0, 0));

        Assert.NotNull(backup);
        Assert.Equal(saveBefore, File.ReadAllBytes(Path.Combine(backup.Path, "save.dat")));
        Assert.Equal(6, slots);
        SaveSummary now = SaveTools.Summarize(_paths.SaveFile);
        Assert.Equal(6, now.Slots);
        Assert.Equal(3, now.ConfigLanguage);
        Assert.Equal(imageBefore, File.ReadAllBytes(image));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(image));
        File.SetAttributes(image, FileAttributes.Normal);
    }

    [Fact]
    public void ANotAnImageFileIsRefusedAndTheSaveStaysUntouched()
    {
        WriteSave(2);
        byte[] before = File.ReadAllBytes(_paths.SaveFile);
        string junk = Path.Combine(_root, "junk.bin");
        File.WriteAllBytes(junk, new byte[1000]);
        Assert.Throws<SaveFormatException>(() => SaveTools.ImportPs4(_paths, junk, DateTime.Now));
        string empty = Path.Combine(_root, "empty.bin");
        File.WriteAllBytes(empty, new byte[SaveFile.Ps4ImageSize]);                // right size, no system block
        Assert.Throws<SaveFormatException>(() => SaveTools.ImportPs4(_paths, empty, DateTime.Now));
        Assert.Equal(before, File.ReadAllBytes(_paths.SaveFile));
        Assert.Empty(SaveTools.List(_paths));
    }

    [Fact]
    public void ShadPs4sImageIsFoundByItsSizeUnderTheTitleFolder()
    {
        string appData = Path.Combine(_root, "Roaming");
        Assert.Null(SaveTools.FindShadPs4Image(appData));
        string folder = Path.Combine(appData, "shadPS4", "home", "1000", "savedata", "CUSA47999", "savedata000");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "other.bin"), new byte[10]);
        Assert.Null(SaveTools.FindShadPs4Image(appData));
        string image = WriteImage(folder, "sdimg_data", Store(1));
        Assert.Equal(image, SaveTools.FindShadPs4Image(appData));
        Assert.Null(SaveTools.FindShadPs4Image(appData, "CUSA00000"));
    }

    // -- the game folder file and the logs ---------------------------------------------------------------------------------

    [Fact]
    public void TheGameFolderIsRememberedInTheFileTheGameReads()
    {
        Assert.Null(_paths.ReadGameDirectory());
        Assert.True(_paths.WriteGameDirectory(@"D:\games\CUSA47999"));
        Assert.Equal(@"D:\games\CUSA47999", File.ReadAllText(Path.Combine(_paths.DataDirectory, "game_dir.txt")));
        Assert.Equal(@"D:\games\CUSA47999", _paths.ReadGameDirectory());
        File.WriteAllText(_paths.GameDirFile, "  \n");
        Assert.Null(_paths.ReadGameDirectory());
    }

    [Fact]
    public void TheLogToShowIsTheLastRunsThenTheCrashLog()
    {
        Assert.Null(_paths.LogToShow());
        File.WriteAllText(_paths.CrashLog, "x");
        Assert.Equal(_paths.CrashLog, _paths.LogToShow());
        File.WriteAllText(_paths.LastRunLog, "y");
        Assert.Equal(_paths.LastRunLog, _paths.LogToShow());
    }

    [Fact]
    public void SettingsWrittenByTheLauncherLandBesideTheSaveFile()
    {
        Assert.Equal(Path.Combine(_paths.DataDirectory, AppSettings.FileName), _paths.SettingsFile);
        Assert.Equal(Path.Combine(_paths.DataDirectory, "save.dat"), _paths.SaveFile);
    }
}
