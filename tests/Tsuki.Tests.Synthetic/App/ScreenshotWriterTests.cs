using System.Buffers.Binary;
using System.IO.Compression;
using Tsuki.App.Play;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>Package K21: the screenshot writer names its files by the clock, never overwrites one, writes a PNG of the window's size off the calling thread, and reports a failure instead of throwing.</summary>
public sealed class ScreenshotWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-k21-shots-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>The IHDR size and the first pixel of a PNG written by the app's writer (one IDAT, zlib, filter 0 on every row).</summary>
    internal static (int Width, int Height, byte[] Rgba) ReadPng(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, file[..8]);
        int pos = 8;
        int width = 0;
        int height = 0;
        using var idat = new MemoryStream();
        while (pos + 8 <= file.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            ReadOnlySpan<byte> data = file.AsSpan(pos + 8, len);
            if (type == "IHDR")
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);
                Assert.Equal(6, data[9]);
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }

            pos += 12 + len;
        }

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new byte[height * ((width * 4) + 1)];
        z.ReadExactly(raw);
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, raw[y * ((width * 4) + 1)]);
            Array.Copy(raw, (y * ((width * 4) + 1)) + 1, rgba, y * width * 4, width * 4);
        }

        return (width, height, rgba);
    }

    [Fact]
    public void TheFileIsNamedByTheClockAndASecondOneInTheSameSecondGetsASuffix()
    {
        var now = new DateTime(2026, 10, 30, 21, 15, 2);
        var w = new ScreenshotWriter(_dir, () => now);
        Assert.Equal(Path.Combine(_dir, "Tsukihime_2026-10-30_21-15-02.png"), w.NextPath());
        Assert.Equal(Path.Combine(_dir, "Tsukihime_2026-10-30_21-15-02-2.png"), w.NextPath());
        Assert.Equal(Path.Combine(_dir, "Tsukihime_2026-10-30_21-15-02-3.png"), w.NextPath());
        now = now.AddSeconds(1);
        Assert.Equal(Path.Combine(_dir, "Tsukihime_2026-10-30_21-15-03.png"), w.NextPath());

        // a file left from an earlier run with the same name is never overwritten
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Tsukihime_2026-10-30_21-15-04.png"), "old");
        File.WriteAllText(Path.Combine(_dir, "Tsukihime_2026-10-30_21-15-04-2.png"), "old");
        now = now.AddSeconds(1);
        Assert.Equal(Path.Combine(_dir, "Tsukihime_2026-10-30_21-15-04-3.png"), w.NextPath());
    }

    [Fact]
    public void SaveWritesThePictureAtItsOwnSizeOffTheCallingThreadAndReportsIt()
    {
        var w = new ScreenshotWriter(_dir, () => new DateTime(2026, 1, 2, 3, 4, 5));
        const int width = 320;
        const int height = 180;
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 10;
            rgba[i + 1] = 20;
            rgba[i + 2] = 30;
            rgba[i + 3] = 0;                                             // the back buffer's alpha: thrown away
        }

        rgba[(((5 * width) + 7) * 4) + 0] = 250;                         // one red pixel at (7, 5), rows top-down
        string path = w.Save(width, height, rgba);
        Assert.True(w.WaitAll(TimeSpan.FromSeconds(10)));
        Assert.True(w.TryTakeResult(out ScreenshotResult r));
        Assert.True(r.Saved, r.Error);
        Assert.Equal(path, r.Path);
        Assert.Equal("Tsukihime_2026-01-02_03-04-05.png", r.FileName);
        Assert.Equal((width, height), (r.Width, r.Height));
        Assert.False(w.TryTakeResult(out _));

        (int pw, int ph, byte[] px) = ReadPng(path);
        Assert.Equal((width, height), (pw, ph));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, px[..4]);
        Assert.Equal(new byte[] { 250, 20, 30, 255 }, px[(((5 * width) + 7) * 4)..((((5 * width) + 7) * 4) + 4)]);
    }

    [Fact]
    public void AFolderThatCannotBeWrittenIsReportedNotThrown()
    {
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocker, "a file where the folder should be");
        var w = new ScreenshotWriter(blocker, () => new DateTime(2026, 1, 2, 3, 4, 5));
        w.Save(4, 4, new byte[64]);
        Assert.True(w.WaitAll(TimeSpan.FromSeconds(10)));
        Assert.True(w.TryTakeResult(out ScreenshotResult r));
        Assert.False(r.Saved);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        Assert.Equal(0, w.Pending);
        Assert.Throws<ArgumentException>(() => w.Save(4, 4, new byte[3]));
    }
}
