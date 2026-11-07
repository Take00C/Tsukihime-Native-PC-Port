// The run log of a Linux or macOS start: without --log it goes to the console and to last_run.log in the profile (GameApp.DefaultLogFile), with --log to that file
// (GameApp.LogFileWriter). The file is best effort: a full disk or a quota (ENOSPC, EDQUOT) must not end the game, so a write that fails drops the file.
using System.Text;

namespace Tsuki.App.Play;

/// <summary>
/// Writes every line to the run's log file and to the console (<paramref name="echo"/>; false: the console gets the lines only once the file is gone); disposing it
/// closes only the file. A write, flush or close of the file that fails closes the file, says so once on the console, and the run goes on with the console.
/// </summary>
internal sealed class TeeWriter(TextWriter console, TextWriter file, string path, bool echo = true) : TextWriter
{
    private readonly Encoding _encoding = file.Encoding;
    private TextWriter? _file = file;

    /// <inheritdoc/>
    public override Encoding Encoding => _encoding;

    /// <summary>Whether the lines still reach the file (false once it failed or was closed).</summary>
    public bool WritesFile => Volatile.Read(ref _file) is not null;

    /// <inheritdoc/>
    public override void Write(char value) => Both(value, static (w, v) => w.Write(v));

    /// <inheritdoc/>
    public override void Write(string? value) => Both(value, static (w, v) => w.Write(v));

    /// <inheritdoc/>
    public override void Write(char[] buffer, int index, int count) => Both((buffer, index, count), static (w, v) => w.Write(v.buffer, v.index, v.count));

    /// <inheritdoc/>
    public override void WriteLine(string? value) => Both(value, static (w, v) => w.WriteLine(v));

    /// <inheritdoc/>
    public override void Flush()
    {
        console.Flush();
        ToFile(0, static (w, _) => w.Flush());
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            console.Flush();
            if (Interlocked.Exchange(ref _file, null) is { } f)
            {
                try
                {
                    f.Dispose();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    console.WriteLine(FailureLine(path, e));
                }
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>The console line of a log file that could not be written.</summary>
    internal static string FailureLine(string path, Exception e) => $"the log file {path} could not be written: {e.Message}; the log goes to the console only";

    private void Both<T>(T value, Action<TextWriter, T> write)
    {
        if (echo)
        {
            write(console, value);
        }

        if (!ToFile(value, write) && !echo)
        {
            write(console, value);
        }
    }

    // false when the file is gone, or failed now (it is dropped)
    private bool ToFile<T>(T value, Action<TextWriter, T> write)
    {
        TextWriter? f = Volatile.Read(ref _file);
        if (f is null)
        {
            return false;
        }

        try
        {
            write(f, value);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (Interlocked.CompareExchange(ref _file, null, f) == f)
            {
                try
                {
                    f.Dispose();
                }
                catch (Exception d) when (d is IOException or UnauthorizedAccessException)
                {
                    // the bytes the writer still holds cannot be written either; the file is closed all the same
                }

                console.WriteLine(FailureLine(path, e));
            }

            return false;
        }
    }
}
