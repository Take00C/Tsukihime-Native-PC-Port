using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tsuki.Media.Ffmpeg;
using Tsuki.Media.Movie;

namespace Tsuki.Tests.Synthetic.Media;

public sealed class MovieInputSecurityTests : IDisposable
{
    private const string NativeNeeded = "FFmpeg 9 shared libraries are required.";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-movie-input-" + Guid.NewGuid().ToString("N"));

    public MovieInputSecurityTests() => Directory.CreateDirectory(_dir);

    public static bool NativeAvailable
    {
        get
        {
            try
            {
                FfmpegLibrary.Load();
                return true;
            }
            catch (FfmpegMissingException)
            {
                return false;
            }
        }
    }

    [Fact(Skip = NativeNeeded, SkipUnless = nameof(NativeAvailable))]
    public void SyntheticH264AndAacMp4StillDecodes()
    {
        string path = Write("local.mp4", Mp4);
        using var pipeline = new MoviePipeline(path, new MoviePipelineOptions(FrameBuffers: 2, VideoThreads: 1));
        Assert.Equal((16, 16), (pipeline.Width, pipeline.Height));
        Assert.True(pipeline.HasAudio);
        pipeline.Start();
        long frames = 0;
        long audio = 0;
        float[] pcm = new float[4096];
        var wait = Stopwatch.StartNew();
        while (!pipeline.Frames.EndOfStream || pipeline.Frames.ReadyCount != 0 || !pipeline.Pcm.Completed || pipeline.Pcm.BufferedFrames != 0)
        {
            Assert.Null(pipeline.Error);
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(10), "synthetic MP4 decode timed out");
            if (pipeline.Frames.Peek() is not null)
            {
                VideoFrame frame = pipeline.Frames.Dequeue();
                Assert.Equal(256, frame.Y.Length);
                Assert.All(frame.Y, value => Assert.Equal(16, value));
                pipeline.Frames.Return(frame);
                frames++;
            }

            audio += pipeline.Pcm.Read(pcm);
            Thread.Sleep(1);
        }

        Assert.Null(pipeline.Error);
        Assert.Equal(4, frames);
        Assert.True(audio > 0);
    }

    [Fact(Skip = NativeNeeded, SkipUnless = nameof(NativeAvailable))]
    public void AMatroskaFileDisguisedAsMp4IsRejected()
    {
        string path = Write("disguised.mp4", Matroska);
        Assert.Throws<InvalidOperationException>(() => new MoviePipeline(path));
    }

    [Fact(Skip = NativeNeeded, SkipUnless = nameof(NativeAvailable))]
    public void HlsDisguisedAsMp4CannotOpenANetworkResource()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string path = Path.Combine(_dir, "playlist.mp4");
        File.WriteAllText(path, $"#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nhttp://127.0.0.1:{port}/segment.ts\n#EXT-X-ENDLIST\n");
        Assert.Throws<InvalidOperationException>(() => new MoviePipeline(path));
        Assert.False(listener.Pending());
    }

    [Fact(Skip = NativeNeeded, SkipUnless = nameof(NativeAvailable))]
    public void NetworkUrlsNeverReachTheProtocolLayer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Throws<ArgumentException>(() => new MoviePipeline($"http://127.0.0.1:{port}/movie.mp4"));
        Assert.False(listener.Pending());
    }

    [Fact(Skip = NativeNeeded, SkipUnless = nameof(NativeAvailable))]
    public void MetadataSignaturesInsideMediaDataDoNotBlockPlayback()
    {
        string path = Path.Combine(_dir, "media-data.mp4");
        File.WriteAllBytes(path, [.. Convert.FromBase64String(Mp4), .. Box("mdat", "psshcmov"u8.ToArray())]);
        using var pipeline = new MoviePipeline(path, new MoviePipelineOptions(FrameBuffers: 2, VideoThreads: 1));
        Assert.Equal((16, 16), (pipeline.Width, pipeline.Height));
        Assert.True(pipeline.HasAudio);
    }

    [Fact(Skip = NativeNeeded, SkipUnless = nameof(NativeAvailable))]
    public void ExtendedBoxesAndMediaDataToEndOfFileAreAccepted()
    {
        byte[] extended = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(extended, 1);
        "free"u8.CopyTo(extended.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(extended.AsSpan(8), 16);
        byte[] media = Box("mdat", "pssh"u8.ToArray());
        BinaryPrimitives.WriteUInt32BigEndian(media, 0);
        string path = Path.Combine(_dir, "extended.mp4");
        File.WriteAllBytes(path, [.. Convert.FromBase64String(Mp4), .. extended, .. media]);
        using var pipeline = new MoviePipeline(path, new MoviePipelineOptions(FrameBuffers: 2, VideoThreads: 1));
        Assert.Equal(16, pipeline.Width);
    }

    private static byte[] Box(string tag, byte[] payload)
    {
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(box.AsSpan(4));
        payload.CopyTo(box.AsSpan(8));
        return box;
    }

    private string Write(string name, string fixture)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Convert.FromBase64String(fixture));
        return path;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // One second of black H.264 video and silent AAC, made from FFmpeg's synthetic sources.
    private const string Mp4 = "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";
    private const string Matroska = "GkXfo6NChoEBQveBAULygQRC84EIQoKIbWF0cm9za2FCh4EEQoWBAhhTgGcBAAAAAAACIhFNm3TAv4To9DVbTbuLU6uEFUmpZlOsgaFNu4tTq4QWVK5rU6yBzE27jFOrhBJUw2dTrIIBaU27jFOrhBxTu2tTrIICBuwBAAAAAAAAUwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFUmpZqa/hFqKZDUq17GDD0JATYCETGF2ZldBhExhdmZEiYhAj0AAAAAAABZUrmtAl7+EnjRz3K4BAAAAAAAAiNeBAXPFiAAAAAAAAAABnIEAIrWcg3VuZIaPVl9NUEVHNC9JU08vQVZDg4EBI+ODhA7msoDglLCBELqBEJqBAlWwiFW3gQFVuIECVe6BAOwBAAAAAAAAAgAAY6KsAWQACv/hABdnZAAKrNlewEQAAAMABAAAAwAgPEiWWAEABmjr48siwP34+AASVMNnu7+EPmYu6nNzsmPAi2PFiAAAAAAAAAABZ8ihRaOIRFVSQVRJT05Eh5MwMDowMDowMS4wMDAwMDAwMDAAH0O2ddi/hA/6LaLngQCjl4EAAIAAAAAPZYiEABL//vet34FNwys9o5CBAu4AAAAACEGaI2xBD/7go5CBAPoAAAAACEGeQXiCP6aBo5CBAfQAAAAACAGeYmpBD6uAHFO7a5e/hHz98wi7j7OBALeK94EB8YIBqfCBCQ==";
}
