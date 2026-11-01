// The crash log and the one-game-per-profile guard (package R1). Both are needed before the window, the log file or SDL exist, so they live apart from GameApp:
// Program.Main's last-resort handlers use them too.
using System.Security.Cryptography;
using System.Text;
using Tsuki.App.Services;

namespace Tsuki.App.Play;

/// <summary>
/// <c>crash.log</c>: appended to in the profile folder (<c>--save-dir</c>, else <c>%APPDATA%\Tsuki</c>), so a verification run's crash never lands in the user's
/// real profile; when that folder cannot be written the folder of the <c>--log</c> file is tried, then <c>%APPDATA%\Tsuki</c>.
/// </summary>
internal static class CrashLog
{
    /// <summary>The file name.</summary>
    public const string FileName = "crash.log";

    /// <summary>Appends an exception; returns the file written, or null when no folder took it.</summary>
    public static string? Write(string? dataDirectory, string? logFile, Exception e) => Append(dataDirectory, logFile, (e ?? throw new ArgumentNullException(nameof(e))).ToString());

    /// <summary>Appends a line of text; returns the file written, or null when no folder took it.</summary>
    public static string? Append(string? dataDirectory, string? logFile, string text)
    {
        foreach (string dir in Candidates(dataDirectory, logFile))
        {
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, FileName);
                File.AppendAllText(path, $"{DateTime.Now:s}  {text}{Environment.NewLine}{Environment.NewLine}");
                return path;
            }
            catch (Exception io) when (io is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // the next folder
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string? dataDirectory, string? logFile)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            yield return dataDirectory;
        }

        string? logDir = null;
        try
        {
            logDir = logFile is null ? null : Path.GetDirectoryName(Path.GetFullPath(logFile));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // a log path that is no path at all
        }

        if (!string.IsNullOrEmpty(logDir))
        {
            yield return logDir;
        }

        yield return ProfileDirectory.ForUser();
    }
}

/// <summary>
/// One game per profile: two games on the same save folder would die on the shared log file or, worse, each hold every slot in memory and overwrite the other's
/// <c>save.dat</c> at quit. The guard is a named mutex derived from the profile folder, so verification runs on their own <c>--save-dir</c> never collide.
/// </summary>
internal static class SingleInstance
{
    /// <summary>Takes the profile's mutex; null when another game holds it. Dispose the result when the run ends.</summary>
    public static Mutex? TryAcquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var mutex = new Mutex(true, MutexName(dataDirectory), out bool createdNew);
        if (createdNew)
        {
            return mutex;
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>The mutex name of a profile folder (the same folder spelt differently gives the same name).</summary>
    public static string MutexName(string dataDirectory)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = dataDirectory;
        }

        if (OperatingSystem.IsWindows())
        {
            full = full.ToUpperInvariant();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return "Local\\Tsukihime." + Convert.ToHexString(hash.AsSpan(0, 12));
    }
}
