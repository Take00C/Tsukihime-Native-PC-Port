using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

public sealed class PortableProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-portable-" + Guid.NewGuid().ToString("N"));
    private readonly string _program;
    private readonly string _profile;

    public PortableProfileTests()
    {
        _program = Path.Combine(_root, "program");
        _profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(_program);
        Directory.CreateDirectory(Path.Combine(_profile, "nested"));
        File.WriteAllBytes(Path.Combine(_profile, "save.dat"), [.. Enumerable.Range(0, 256).Select(x => (byte)x)]);
        File.WriteAllText(Path.Combine(_profile, "settings.ini"), "language=en\n");
        File.WriteAllText(Path.Combine(_profile, "nested", "note.txt"), "keep me");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void SwitchingOnAndOffCopiesVerifiesAndKeepsBackups()
    {
        var current = new LauncherPaths(_profile);
        DateTime now = new(2026, 11, 25, 12, 0, 0);
        PortableProfileResult on = PortableProfile.Set(true, current, _program, now, () => false, perUserDirectory: _profile);
        string portable = Path.Combine(_program, "save");
        Assert.True(on.Changed, on.Message);
        Assert.True(on.RestartRequired);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_profile, "save.dat")), File.ReadAllBytes(Path.Combine(portable, "save.dat")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(portable, "nested", "note.txt")));
        Assert.True(File.Exists(Path.Combine(_profile, "nested", "note.txt")));
        string backup = Assert.Single(Directory.GetDirectories(current.BackupsDirectory));
        Assert.True(File.Exists(Path.Combine(backup, "save.dat")));

        PortableProfileResult off = PortableProfile.Set(false, new LauncherPaths(portable), _program, now.AddSeconds(1), () => false, perUserDirectory: _profile);
        Assert.True(off.Changed, off.Message);
        Assert.True(off.RestartRequired);
        Assert.False(Directory.Exists(portable));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_profile, "nested", "note.txt")));
        string parked = Assert.Single(Directory.GetDirectories(_program, "save-backup-*"));
        Assert.Equal(File.ReadAllBytes(Path.Combine(_profile, "save.dat")), File.ReadAllBytes(Path.Combine(parked, "save.dat")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(parked, "nested", "note.txt")));
    }

    [Fact]
    public void TheRunningLaunchersLockIsLeftBehindAndDoesNotStopTheSwitch()
    {
        // the Linux / macOS launcher holds <profile>/launcher.lock with FileShare.None while its window runs (UnixLauncherInstance), which .NET makes an
        // exclusive lock that no other open can share, and the game's game.lock (CrashLog) belongs to its run: neither is profile data to copy
        File.WriteAllText(Path.Combine(_profile, "game.lock"), string.Empty);
        string held = Path.Combine(_profile, "launcher.lock");
        DateTime now = new(2026, 11, 27, 22, 0, 0);
        string portable = Path.Combine(_program, "save");
        using (new FileStream(held, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            PortableProfileResult on = PortableProfile.Set(true, new LauncherPaths(_profile), _program, now, () => false, perUserDirectory: _profile);
            Assert.True(on.Changed, on.Message);
        }

        Assert.Equal(File.ReadAllBytes(Path.Combine(_profile, "save.dat")), File.ReadAllBytes(Path.Combine(portable, "save.dat")));
        Assert.False(File.Exists(Path.Combine(portable, "launcher.lock")));
        Assert.False(File.Exists(Path.Combine(portable, "game.lock")));
        Assert.Empty(Directory.GetDirectories(_program, "save-incomplete-*"));

        // and back, with the Linux launcher now holding the portable profile's lock (the Windows launcher holds no file in the profile, and Windows could not
        // park a folder with an open file in it, which Linux can)
        using (OperatingSystem.IsWindows() ? null : new FileStream(Path.Combine(portable, "launcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            PortableProfileResult off = PortableProfile.Set(false, new LauncherPaths(portable), _program, now.AddSeconds(1), () => false, perUserDirectory: _profile);
            Assert.True(off.Changed, off.Message);
        }

        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_profile, "nested", "note.txt")));
    }

    [Fact]
    public void SwitchingOffKeepsTheDestinationBackupOfThePreviousSave()
    {
        var current = new LauncherPaths(_profile);
        DateTime onTime = new(2026, 11, 25, 12, 0, 0);
        PortableProfileResult on = PortableProfile.Set(true, current, _program, onTime, () => false, perUserDirectory: _profile);
        Assert.True(on.Changed, on.Message);
        string portable = Path.Combine(_program, "save");
        byte[] previous = File.ReadAllBytes(Path.Combine(_profile, "save.dat"));
        File.WriteAllBytes(Path.Combine(portable, "save.dat"), [9, 9, 9]);
        DateTime offTime = onTime.AddSeconds(1);
        PortableProfileResult off = PortableProfile.Set(false, new LauncherPaths(portable), _program, offTime, () => false, perUserDirectory: _profile);
        Assert.True(off.Changed, off.Message);
        string backup = Path.Combine(_profile, "backups", "20261125-120001-destination", "save.dat");
        Assert.True(File.Exists(backup));
        Assert.Equal(previous, File.ReadAllBytes(backup));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Combine(_profile, "backups", "20261125-120001", "save.dat")));

        // v0.1.11 review: the message names where the save it replaced is kept, not the portable folder's own backup, whose path moved with that folder
        Assert.Contains("the save it replaced is backed up in " + Path.Combine(_profile, "backups", "20261125-120001-destination"), off.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.Combine(portable, "backups"), off.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunningGameRefusesTheSwitchBeforeAnythingIsWritten()
    {
        PortableProfileResult result = PortableProfile.Set(true, new LauncherPaths(_profile), _program, gameRunning: () => true, perUserDirectory: _profile);
        Assert.False(result.Changed);
        Assert.Contains("Close the game", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_program, "save")));
        Assert.False(Directory.Exists(Path.Combine(_profile, "backups")));
    }

    [Fact]
    public void AFailedFirstCopyDoesNotActivateAPartialPortableProfile()
    {
        string portable = Path.Combine(_program, "save");
        PortableProfileResult result = PortableProfile.Set(true, new LauncherPaths(_profile), _program, gameRunning: () => false, perUserDirectory: _profile, beforeCopy: file =>
        {
            if (Path.GetFileName(file) == "note.txt") { throw new IOException("locked source file (test)"); }
        });
        Assert.False(result.Changed);
        Assert.False(Directory.Exists(portable), "an incomplete save folder would become the game's profile");
        Assert.True(File.Exists(Path.Combine(_profile, "nested", "note.txt")));
        Assert.Single(Directory.GetDirectories(_program, "save-incomplete-*"));
    }

    [Fact]
    public void AnUnwritableProgramFolderIsRefusedClearly()
    {
        PortableProfileResult result = PortableProfile.Set(true, new LauncherPaths(_profile), _program, gameRunning: () => false, writableProbe: _ => "access denied (test)", perUserDirectory: _profile);
        Assert.False(result.Changed);
        Assert.Contains("program folder cannot be written", result.Message, StringComparison.Ordinal);
        Assert.Contains("access denied", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_program, "save")));
        Assert.False(Directory.Exists(Path.Combine(_profile, "backups")));
    }

    [Fact]
    public void AnOverlappingDataFolderIsRefused()
    {
        PortableProfileResult result = PortableProfile.Set(true, new LauncherPaths(_program), _program, gameRunning: () => false, perUserDirectory: _profile);
        Assert.False(result.Changed);
        Assert.Contains("overlap", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_program, "save")));
    }

    [Fact]
    public void AnExplicitDataProfileStillWinsOverAnExistingPortableFolder()
    {
        string portable = Path.Combine(_program, "save");
        Directory.CreateDirectory(portable);
        var current = new LauncherPaths(_profile);
        Assert.False(PortableProfile.InUse(current, _program));
        PortableProfileResult result = PortableProfile.Set(false, current, _program, gameRunning: () => false, perUserDirectory: _profile);
        Assert.False(result.Changed);
        Assert.True(Directory.Exists(portable));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_profile, "nested", "note.txt")));
    }
}
