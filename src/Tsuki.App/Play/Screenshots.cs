// The screenshot key (package K21): F12 saves the frame the window shows, at the window's own size, as a PNG in the player's Pictures folder (Print Screen was taken out
// in package K24: it belongs to Windows, whose own screenshot must not also take the game's and show its notice). The read-back
// of the back buffer happens on the GL thread (GameApp); the PNG encoding and the file write, which take tens of milliseconds for a 1440p frame, run on the thread
// pool and report back through a queue the loop drains, so the game never waits for the disk.
using System.Collections.Concurrent;
using System.Globalization;

namespace Tsuki.App.Play;

/// <summary>The outcome of one screenshot: the file written, or the error.</summary>
/// <param name="Path">The full path of the file (also when the write failed: the name it would have had).</param>
/// <param name="Width">The picture's width.</param>
/// <param name="Height">The picture's height.</param>
/// <param name="Error">The operating system's words when the file could not be written, else null.</param>
internal sealed record ScreenshotResult(string Path, int Width, int Height, string? Error)
{
    /// <summary>True when the file was written.</summary>
    public bool Saved => Error is null;

    /// <summary>The file name alone (what the toast shows).</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>Writes screenshots into a folder, off the main thread, and hands the results back.</summary>
internal sealed class ScreenshotWriter
{
    /// <summary>The file name prefix.</summary>
    public const string Prefix = "Tsukihime_";

    private readonly ConcurrentQueue<ScreenshotResult> _results = new();
    private readonly Func<DateTime> _now;
    private string? _lastName;
    private int _lastSuffix;
    private int _pending;

    /// <summary>Creates a writer for <paramref name="folder"/> (created at the first screenshot).</summary>
    /// <param name="folder">The folder; <see cref="DefaultFolder"/> for the player's.</param>
    /// <param name="now">The clock (the tests pass a fixed one).</param>
    public ScreenshotWriter(string folder, Func<DateTime>? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = folder;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>The player's Pictures folder with a <c>Tsukihime</c> subfolder.</summary>
    public static string DefaultFolder
    {
        get
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify);
            if (!Path.IsPathFullyQualified(pictures))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
                if (!Path.IsPathFullyQualified(home))
                {
                    throw new IOException("The user profile has no absolute path. Use --screenshot-dir to choose a screenshot folder.");
                }

                pictures = Path.Combine(home, "Pictures");
            }

            return Path.Combine(pictures, "Tsukihime");
        }
    }

    /// <summary>Where the files go.</summary>
    public string Folder { get; }

    /// <summary>Screenshots handed to the thread pool and not yet reported.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>
    /// The path of the next file: <c>Tsukihime_yyyy-MM-dd_HH-mm-ss.png</c>, with <c>-2</c>, <c>-3</c>, ... appended when the second already has a file (two shots in one second,
    /// or a file left from an earlier run).
    /// </summary>
    public string NextPath()
    {
        string stamp = Prefix + _now().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        if (stamp == _lastName)
        {
            _lastSuffix++;
        }
        else
        {
            _lastName = stamp;
            _lastSuffix = 1;
        }

        string path = Path.Combine(Folder, stamp + (_lastSuffix > 1 ? "-" + _lastSuffix.ToString(CultureInfo.InvariantCulture) : string.Empty) + ".png");
        while (File.Exists(path))
        {
            _lastSuffix++;
            path = Path.Combine(Folder, stamp + "-" + _lastSuffix.ToString(CultureInfo.InvariantCulture) + ".png");
        }

        return path;
    }

    /// <summary>Encodes <paramref name="rgba"/> (rows top-down, alpha ignored) and writes it on the thread pool; the result comes through <see cref="TryTakeResult"/>.</summary>
    public string Save(int width, int height, byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException($"{width} x {height} RGBA needs {width * height * 4} bytes, not {rgba.Length}", nameof(rgba));
        }

        string path = NextPath();
        Interlocked.Increment(ref _pending);
        Task.Run(() =>
        {
            try
            {
                for (int a = 3; a < rgba.Length; a += 4)
                {
                    rgba[a] = 255;
                }

                PngWriter.Write(path, width, height, rgba);
                _results.Enqueue(new ScreenshotResult(path, width, height, null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _results.Enqueue(new ScreenshotResult(path, width, height, e.Message));
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        });
        return path;
    }

    /// <summary>Takes the next finished screenshot, if any (the loop calls it once per pass).</summary>
    public bool TryTakeResult(out ScreenshotResult result) => _results.TryDequeue(out result!);

    /// <summary>Waits for the screenshots in flight (the end of the run, the tests).</summary>
    public bool WaitAll(TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Pending > 0)
        {
            if (sw.Elapsed > timeout)
            {
                return false;
            }

            Thread.Sleep(5);
        }

        return true;
    }
}
