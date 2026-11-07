// One launcher per profile and a record of its crashes (the Windows launcher has a named mutex and launcher_crash.log, LauncherApp.xaml.cs). A named mutex does
// not reach across sessions on macOS and Linux, so the lock is a file of the profile folder held open without sharing: a second launcher on the same profile
// cannot open it while the first runs, and it is free again the moment the first one ends, also after a crash.
using System.Globalization;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher.Avalonia;

/// <summary>The profile's launcher.lock, held open while the launcher window runs.</summary>
public sealed class UnixLauncherLock : IDisposable
{
    /// <summary>The file name in the profile folder.</summary>
    public const string FileName = "launcher.lock";

    private readonly string _path;
    private FileStream? _stream;

    private UnixLauncherLock(string path, FileStream? stream)
    {
        _path = path;
        _stream = stream;
    }

    /// <summary>True while this process holds the lock.</summary>
    public bool Held => _stream is not null;

    /// <summary>
    /// Takes the lock of <paramref name="paths"/>, trying for up to <paramref name="wait"/> (a launcher an update restarted into waits for the one it replaces).
    /// Null when another launcher holds it. A profile folder that cannot be made or written gives a lock that holds nothing (the launcher still opens).
    /// </summary>
    public static UnixLauncherLock? TryAcquire(LauncherPaths paths, TimeSpan wait = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string path = Path.Combine(paths.DataDirectory, FileName);
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new UnixLauncherLock(path, null);
        }

        DateTime until = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new UnixLauncherLock(path, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (UnauthorizedAccessException)
            {
                return new UnixLauncherLock(path, null);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(200);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>Lets the lock go (before the launcher an update installed is started: it takes the lock).</summary>
    public void Release()
    {
        _stream?.Dispose();
        _stream = null;
    }

    /// <summary>Takes the lock again after a restart that did not happen; false when another launcher took it meanwhile.</summary>
    public bool Retake()
    {
        if (_stream is not null) { return true; }
        try
        {
            _stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Release();
}

/// <summary>launcher_crash.log in the profile folder: what an error nothing else caught was, for the player and for support.</summary>
public static class UnixLauncherCrash
{
    /// <summary>The file name in the profile folder (the same as the Windows launcher's).</summary>
    public const string FileName = "launcher_crash.log";

    /// <summary>Appends the error; the log's path, or null when it could not be written.</summary>
    public static string? Write(LauncherPaths paths, Exception error)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            string log = Path.Combine(paths.DataDirectory, FileName);
            File.AppendAllText(log, $"{DateTime.Now.ToString("s", CultureInfo.InvariantCulture)}  {error}{Environment.NewLine}{Environment.NewLine}");
            return log;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>What the player is told.</summary>
    public static string Message(Exception error, string? log) =>
        "The launcher hit an error: " + error.Message.TrimEnd('.') + "." + (log is null ? string.Empty : " Details are in " + log + ".");
}
