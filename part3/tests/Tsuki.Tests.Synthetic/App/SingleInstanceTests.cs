using Tsuki.App.Play;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// critic-1: one game per profile on Linux and macOS too. The named mutex reaches only the games of one Unix session (a terminal tab, a Steam start and a start from
/// the file manager can be three); the profile's lock file holds across sessions. Here another session's game is a second open of the lock file, which is what
/// the lock sees.
/// </summary>
public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "tsuki-guard-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_profile))
        {
            Directory.Delete(_profile, recursive: true);
        }
    }

    private string LockPath => Path.Combine(_profile, SingleInstance.LockFileName);

    [Fact]
    public void OffWindowsAGameOfAnotherSessionHoldingTheLockFileRefusesTheStart()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows keeps v0.1.6's mutex alone (WindowsKeepsTheMutexAloneAndMakesNoLockFile)");
        }

        Directory.CreateDirectory(_profile);
        using (new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None))
        {
            // the mutex of this session is free; the other game's lock is not
            Assert.Null(SingleInstance.TryAcquire(_profile));
        }

        using (IDisposable? first = SingleInstance.TryAcquire(_profile))
        {
            Assert.NotNull(first);
            Assert.True(File.Exists(LockPath));
            Assert.False(SingleInstance.TryLockFile(_profile, out FileStream? second));    // what a game of another session meets
            Assert.Null(second);
        }

        Assert.True(SingleInstance.TryLockFile(_profile, out FileStream? after));          // the lock ends with the game; the file may stay
        after!.Dispose();
        using IDisposable? again = SingleInstance.TryAcquire(_profile);
        Assert.NotNull(again);
    }

    [Fact]
    public void TheLockFileIsMadeInAProfileThatDoesNotExistYet()
    {
        Assert.False(Directory.Exists(_profile));
        Assert.True(SingleInstance.TryLockFile(_profile, out FileStream? held));
        using (held)
        {
            Assert.NotNull(held);
            Assert.True(File.Exists(LockPath));
            Assert.False(SingleInstance.TryLockFile(_profile, out FileStream? other));
            Assert.Null(other);
        }
    }

    [Fact]
    public void WindowsKeepsTheMutexAloneAndMakesNoLockFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("the lock file is Linux's and macOS's");
        }

        Directory.CreateDirectory(_profile);
        using (IDisposable? first = SingleInstance.TryAcquire(_profile))
        {
            Assert.IsType<Mutex>(first);
            Assert.False(File.Exists(LockPath));
        }
    }
}
