// Spike S2: the movie thread's queues (ARCHITECTURE 3.4: "FFmpeg demux/decode -> frame queue (Y, U, V); AAC -> mixer").
using FFmpeg.AutoGen;

namespace S2.MovieSync;

/// <summary>Plane layout of a decoded frame: yuv420p from software H.264, NV12 from a hwaccel copy-back.</summary>
internal enum PlaneLayout
{
    Yuv420P,
    Nv12,
}

/// <summary>One decoded picture in pinned buffers, ready for glTexSubImage2D.</summary>
internal sealed class VideoFrame
{
    public VideoFrame(int width, int height)
    {
        Width = width;
        Height = height;
        Y = GC.AllocateUninitializedArray<byte>(width * height, pinned: true);
        // U and V for yuv420p; for NV12 the interleaved CbCr plane uses C0 with twice the width.
        C0 = GC.AllocateUninitializedArray<byte>(width * height / 2, pinned: true);
        C1 = GC.AllocateUninitializedArray<byte>(width * height / 4, pinned: true);
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Y { get; }

    public byte[] C0 { get; }

    public byte[] C1 { get; }

    public PlaneLayout Layout { get; set; }

    /// <summary>Source frame index (pts / frame duration).</summary>
    public long Index { get; set; }

    public double Pts { get; set; }

    /// <summary>Stopwatch timestamp when the frame entered the ready queue.</summary>
    public long ReadyTicks { get; set; }

    /// <summary>Number of 60 Hz ticks that selected this frame.</summary>
    public int TicksShown { get; set; }

    /// <summary>Number of swaps that showed this frame.</summary>
    public int PresentsShown { get; set; }

    /// <summary>Copies the planes of a software frame (tight rows).</summary>
    public unsafe void CopyFrom(AVFrame* f)
    {
        var fmt = (AVPixelFormat)f->format;
        int w = Width, h = Height;
        CopyPlane(f->data[0u], f->linesize[0u], Y, w, h);
        if (fmt == AVPixelFormat.AV_PIX_FMT_YUV420P || fmt == AVPixelFormat.AV_PIX_FMT_YUVJ420P)
        {
            Layout = PlaneLayout.Yuv420P;
            CopyPlane(f->data[1u], f->linesize[1u], C0, w / 2, h / 2);
            CopyPlane(f->data[2u], f->linesize[2u], C1, w / 2, h / 2);
        }
        else if (fmt == AVPixelFormat.AV_PIX_FMT_NV12)
        {
            Layout = PlaneLayout.Nv12;
            CopyPlane(f->data[1u], f->linesize[1u], C0, w, h / 2);
        }
        else
        {
            throw new InvalidOperationException($"unexpected pixel format {ffmpeg.av_get_pix_fmt_name(fmt)}");
        }
    }

    private static unsafe void CopyPlane(byte* src, int stride, byte[] dst, int rowBytes, int rows)
    {
        fixed (byte* d = dst)
        {
            if (stride == rowBytes)
            {
                Buffer.MemoryCopy(src, d, dst.Length, (long)rowBytes * rows);
                return;
            }

            for (int r = 0; r < rows; r++)
            {
                Buffer.MemoryCopy(src + (long)r * stride, d + (long)r * rowBytes, rowBytes, rowBytes);
            }
        }
    }
}

/// <summary>A fixed pool of frame buffers plus the ready queue the main thread pulls from.</summary>
internal sealed class FrameQueue
{
    private readonly object _lock = new();
    private readonly Queue<VideoFrame> _ready = new();
    private readonly Stack<VideoFrame> _free = new();
    private bool _closed;

    public FrameQueue(int buffers, int width, int height)
    {
        for (int i = 0; i < buffers; i++)
        {
            _free.Push(new VideoFrame(width, height));
        }

        Capacity = buffers;
    }

    public int Capacity { get; }

    /// <summary>Set by the decoder thread after its last frame.</summary>
    public volatile bool EndOfStream;

    public int ReadyCount
    {
        get
        {
            lock (_lock)
            {
                return _ready.Count;
            }
        }
    }

    /// <summary>Takes a free buffer, waiting while the main thread holds them all. Null when closed.</summary>
    public VideoFrame? Rent(out double waitedMs)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_lock)
        {
            while (_free.Count == 0 && !_closed)
            {
                Monitor.Wait(_lock, 50);
            }

            waitedMs = Report.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            return _closed ? null : _free.Pop();
        }
    }

    public void Push(VideoFrame f)
    {
        lock (_lock)
        {
            f.ReadyTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            f.TicksShown = 0;
            f.PresentsShown = 0;
            _ready.Enqueue(f);
        }
    }

    public VideoFrame? Peek()
    {
        lock (_lock)
        {
            return _ready.Count > 0 ? _ready.Peek() : null;
        }
    }

    public VideoFrame Dequeue()
    {
        lock (_lock)
        {
            return _ready.Dequeue();
        }
    }

    public void Return(VideoFrame f)
    {
        lock (_lock)
        {
            _free.Push(f);
            Monitor.PulseAll(_lock);
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            Monitor.PulseAll(_lock);
        }
    }
}

/// <summary>Video packets from the demuxer to the decoder thread (AVPacket* as nint; 0 = end of stream).</summary>
internal sealed unsafe class PacketQueue
{
    private readonly object _lock = new();
    private readonly Queue<nint> _q = new();
    private bool _closed;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _q.Count;
            }
        }
    }

    public int MaxCount { get; private set; }

    public void Add(AVPacket* pkt)
    {
        lock (_lock)
        {
            _q.Enqueue((nint)pkt);
            MaxCount = Math.Max(MaxCount, _q.Count);
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Waits for a packet. Returns false when closed; <paramref name="pkt"/> is null for end of stream.</summary>
    public bool Take(out AVPacket* pkt)
    {
        lock (_lock)
        {
            while (_q.Count == 0 && !_closed)
            {
                Monitor.Wait(_lock, 50);
            }

            if (_q.Count == 0)
            {
                pkt = null;
                return false;
            }

            pkt = (AVPacket*)_q.Dequeue();
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>Waits up to <paramref name="ms"/> for a change (used by the demuxer's flow control).</summary>
    public void WaitForChange(int ms)
    {
        lock (_lock)
        {
            if (!_closed)
            {
                Monitor.Wait(_lock, ms);
            }
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            while (_q.Count > 0)
            {
                var p = (AVPacket*)_q.Dequeue();
                if (p != null)
                {
                    ffmpeg.av_packet_free(&p);
                }
            }

            Monitor.PulseAll(_lock);
        }
    }
}

/// <summary>Single-producer single-consumer float32 stereo ring between the movie thread and the audio callback.</summary>
internal sealed class PcmRing(int capacityFrames)
{
    private readonly float[] _buf = GC.AllocateArray<float>(capacityFrames * 2, pinned: true);
    private long _written; // frames
    private long _read;    // frames

    public int CapacityFrames { get; } = capacityFrames;

    public long Written => Volatile.Read(ref _written);

    public long ReadFrames => Volatile.Read(ref _read);

    public int BufferedFrames => (int)(Written - ReadFrames);

    public int FreeFrames => CapacityFrames - BufferedFrames;

    /// <summary>Producer: writes as many frames as fit; returns the count written.</summary>
    public int Write(ReadOnlySpan<float> interleaved)
    {
        int frames = Math.Min(interleaved.Length / 2, FreeFrames);
        long w = _written;
        for (int i = 0; i < frames;)
        {
            int pos = (int)((w + i) % CapacityFrames);
            int run = Math.Min(frames - i, CapacityFrames - pos);
            interleaved.Slice(i * 2, run * 2).CopyTo(_buf.AsSpan(pos * 2, run * 2));
            i += run;
        }

        Volatile.Write(ref _written, w + frames);
        return frames;
    }

    /// <summary>Consumer: reads up to <paramref name="dst"/>.Length / 2 frames; returns the count read.</summary>
    public int Read(Span<float> dst)
    {
        long r = _read;
        int frames = (int)Math.Min(dst.Length / 2, Volatile.Read(ref _written) - r);
        for (int i = 0; i < frames;)
        {
            int pos = (int)((r + i) % CapacityFrames);
            int run = Math.Min(frames - i, CapacityFrames - pos);
            _buf.AsSpan(pos * 2, run * 2).CopyTo(dst.Slice(i * 2, run * 2));
            i += run;
        }

        Volatile.Write(ref _read, r + frames);
        return frames;
    }
}
