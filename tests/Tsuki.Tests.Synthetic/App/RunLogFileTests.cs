using System.Text;
using Tsuki.App;
using Tsuki.App.Play;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// deck-evidence-3: a Linux or macOS start without --log (from Steam, the file manager) leaves its log in the profile under the launcher's name, last_run.log,
/// and the console still gets every line. unix-2: the file is best effort; a write the disk refuses (ENOSPC on a full Steam Deck, EDQUOT) drops it, says so on
/// the console and the game goes on, with or without --log.
/// </summary>
public sealed class RunLogFileTests
{
    private const string LastRun = "/home/deck/.config/Tsuki/last_run.log";

    [Fact]
    public void OffWindowsAStartWithoutLogWritesTheLaunchersRunLogInTheProfile()
    {
        AppArgs Parse(params string[] a)
        {
            Assert.True(AppArgs.TryParse(a, out AppArgs? parsed, out string? error), error);
            return parsed!;
        }

        string profile = Path.Combine(Path.GetTempPath(), "tsuki-profile");
        Assert.Equal(Path.Combine(profile, "last_run.log"), GameApp.DefaultLogFile(Parse(), profile, windows: false));
        Assert.Equal(Path.Combine(profile, "last_run.log"), GameApp.DefaultLogFile(Parse("--hidden", "--mute"), profile, windows: false));
        Assert.Null(GameApp.DefaultLogFile(Parse(), profile, windows: true));                            // Windows: the console, as v0.1.6
        Assert.Null(GameApp.DefaultLogFile(Parse("--log", "/tmp/run.log"), profile, windows: false));     // the launcher's PLAY names it itself
        Assert.Null(GameApp.DefaultLogFile(Parse("--no-save"), profile, windows: false));                 // a run that writes nothing in the profile
        Assert.Equal("last_run.log", GameApp.LastRunLogName);
    }

    [Fact]
    public void ARunLogGoesToTheConsoleAndTheFileAndOnlyTheFileIsClosed()
    {
        var console = new StringWriter();
        var file = new StringWriter();
        using (var tee = new TeeWriter(console, file, LastRun))
        {
            tee.WriteLine("window mode: fullscreen (Steam Deck default)");
            tee.Write('x');
            tee.Write("yz");
            tee.Write(['1', '2', '3'], 1, 2);
            Assert.Equal(file.Encoding, tee.Encoding);
        }

        string expected = "window mode: fullscreen (Steam Deck default)" + Environment.NewLine + "xyz23";
        Assert.Equal(expected, console.ToString());
        Assert.Equal(expected, file.ToString());
        console.Write("still open");
        Assert.Throws<ObjectDisposedException>(() => file.Write('!'));
    }

    [Fact]
    public void TheTeeFlushesBothWriters()
    {
        using var consoleBytes = new MemoryStream();
        using var fileBytes = new MemoryStream();
        var console = new StreamWriter(consoleBytes, new UTF8Encoding(false));
        var file = new StreamWriter(fileBytes, new UTF8Encoding(false));
        var tee = new TeeWriter(console, file, LastRun);
        tee.Write("a");
        tee.Flush();
        Assert.Equal(1, consoleBytes.Length);
        Assert.Equal(1, fileBytes.Length);
        tee.Dispose();
    }

    [Fact]
    public void AWriteTheFullDiskRefusesDropsTheFileAndTheConsoleGoesOn()
    {
        // a StreamWriter as GameApp opens it, over a disk that fills up after the first line
        var disk = new FullDisk();
        var console = new StringWriter();
        var tee = new TeeWriter(console, new StreamWriter(disk, new UTF8Encoding(false)) { AutoFlush = true }, LastRun);
        tee.WriteLine("first");
        Assert.True(tee.WritesFile);
        disk.Full = true;
        tee.WriteLine("[    120] second");
        Assert.False(tee.WritesFile);
        int attempts = disk.Attempts;
        tee.WriteLine("third");
        tee.Write('x');
        tee.Write("yz");
        tee.Write(['1', '2', '3'], 1, 2);
        tee.Flush();
        tee.Dispose();
        Assert.Equal(attempts, disk.Attempts);                       // the dropped file is never tried again
        Assert.True(disk.Closed);
        Assert.Equal("first" + Environment.NewLine, Encoding.UTF8.GetString(disk.Written.ToArray()));
        string failure = $"the log file {LastRun} could not be written: No space left on device; the log goes to the console only";
        Assert.Equal(string.Join(Environment.NewLine, "first", "[    120] second", failure, "third", "xyz23"), console.ToString());
    }

    [Theory]
    [MemberData(nameof(FileOperations))]
    public void EveryWriteFlushAndCloseOfTheFileIsBestEffort(string operation, bool access)
    {
        var console = new StringWriter();
        var file = new BrokenWriter(access);
        var tee = new TeeWriter(console, file, LastRun);
        Operate(tee, operation);
        Assert.False(tee.WritesFile);
        tee.WriteLine("after");
        tee.Dispose();
        Assert.Equal(1, file.Disposed);
        string text = console.ToString();
        string failure = $"the log file {LastRun} could not be written: {(access ? "Access to the path is denied." : "Disk quota exceeded")}; the log goes to the console only";
        Assert.Equal(1, text.Split(failure).Length - 1);
        Assert.EndsWith("after" + Environment.NewLine, text, StringComparison.Ordinal);
    }

    public static TheoryData<string, bool> FileOperations()
    {
        var data = new TheoryData<string, bool>();
        foreach (string op in new[] { "char", "string", "chars", "line", "flush", "dispose" })
        {
            data.Add(op, false);
            data.Add(op, true);
        }

        return data;
    }

    [Fact]
    public void OffWindowsALogFileGoesToTheConsoleOnlyOnceItFails()
    {
        var console = new StringWriter();
        var disk = new FullDisk();
        var file = new StreamWriter(disk, new UTF8Encoding(false)) { AutoFlush = true };
        Assert.Same(file, GameApp.LogFileWriter(file, "/tmp/run.log", windows: true, console));      // Windows: the file itself, as v0.1.6

        TextWriter log = GameApp.LogFileWriter(file, "/tmp/run.log", windows: false, console);
        log.WriteLine("Tsukihime 0.1.7");
        Assert.Equal(string.Empty, console.ToString());                                                // --log: the file alone while it takes the lines
        disk.Full = true;
        log.WriteLine("window: 1600 x 900");
        log.WriteLine("done");
        log.Dispose();
        Assert.Equal("Tsukihime 0.1.7" + Environment.NewLine, Encoding.UTF8.GetString(disk.Written.ToArray()));
        Assert.Equal(
            string.Join(Environment.NewLine, "the log file /tmp/run.log could not be written: No space left on device; the log goes to the console only", "window: 1600 x 900", "done", string.Empty),
            console.ToString());
    }

    [Fact]
    public void TheGameOpensItsLogFilesThroughTheBestEffortWriters()
    {
        string code = string.Concat(File.ReadAllText(Path.Combine(Repo.Src, "Tsuki.App", "Play", "GameApp.cs")).Where(c => !char.IsWhiteSpace(c)));
        Assert.Contains("log=LogFileWriter(newStreamWriter(path,false,newUTF8Encoding(false)){AutoFlush=true},path,OperatingSystem.IsWindows(),Console.Out);", code, StringComparison.Ordinal);
        Assert.Contains("log=newTeeWriter(Console.Out,newStreamWriter(lastRun,false,newUTF8Encoding(false)){AutoFlush=true},lastRun);", code, StringComparison.Ordinal);
    }

    private static void Operate(TextWriter w, string operation)
    {
        switch (operation)
        {
            case "char":
                w.Write('a');
                break;
            case "string":
                w.Write("a");
                break;
            case "chars":
                w.Write(['a', 'b'], 0, 2);
                break;
            case "line":
                w.WriteLine("a");
                break;
            case "flush":
                w.Flush();
                break;
            default:
                w.Dispose();
                break;
        }
    }

    // a disk that takes bytes until it is full, then refuses every write as ENOSPC does
    private sealed class FullDisk : Stream
    {
        public MemoryStream Written { get; } = new();

        public bool Full { get; set; }

        public int Attempts { get; private set; }

        public bool Closed { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !Closed;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Attempts++;
            if (Full)
            {
                throw new IOException("No space left on device");
            }

            Written.Write(buffer);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    // a file every write, flush and close of which fails: a quota (IOException) or a revoked permission (UnauthorizedAccessException)
    private sealed class BrokenWriter(bool access) : TextWriter
    {
        public int Disposed { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => throw Failure();

        public override void Write(string? value) => throw Failure();

        public override void Write(char[] buffer, int index, int count) => throw Failure();

        public override void WriteLine(string? value) => throw Failure();

        public override void Flush() => throw Failure();

        protected override void Dispose(bool disposing)
        {
            Disposed++;
            throw Failure();
        }

        private Exception Failure() => access ? new UnauthorizedAccessException("Access to the path is denied.") : new IOException("Disk quota exceeded");
    }
}
