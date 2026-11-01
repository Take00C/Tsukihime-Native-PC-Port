// The movie thread's queues (ARCHITECTURE 3.4: "FFmpeg demux/decode -> frame queue (Y, U, V); AAC -> mixer"; S2.md 9 #1):
// a fixed pool of pinned frame buffers with a ready queue, and the video packet queue between the demux and decode threads.
// Lifted from spike S2 Queues.cs; the PCM ring is Tsuki.Media.Movie.PcmRing (E1).
using FFmpeg.AutoGen;

namespace Tsuki.Media.Movie;

/// <summary>
/// One decoded picture in pinned buffers, ready for <c>glTexSubImage2D</c> (three R8 textures, ARCHITECTURE 7.5): the planes
/// of a yuv420p frame with tight rows. The renderer converts BT.709 limited range (the 55 untagged files are treated the same).
/// </summary>
public sealed class VideoFrame
{
    /// <summary>Creates a frame of <paramref name="width"/> x <paramref name="height"/> (both even).</summary>
    public VideoFrame(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((width | height) % 2 != 0)
        {
            throw new ArgumentException("yuv420p needs even dimensions");
        }

        long pixels = (long)width * height;
        if (pixels > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "video plane does not fit an array");
        }

        Width = width;
        Height = height;
        Y = GC.AllocateUninitializedArray<byte>((int)pixels, pinned: true);
        U = GC.AllocateUninitializedArray<byte>((int)(pixels / 4), pinned: true);
        V = GC.AllocateUninitializedArray<byte>((int)(pixels / 4), pinned: true);
    }

    /// <summary>Luma width.</summary>
    public int Width { get; }

    /// <summary>Luma height.</summary>
    public int Height { get; }

    /// <summary>Luma plane, <c>Width x Height</c> bytes.</summary>
    public byte[] Y { get; }

    /// <summary>Cb plane, <c>Width / 2 x Height / 2</c> bytes.</summary>
    public byte[] U { get; }

    /// <summary>Cr plane, <c>Width / 2 x Height / 2</c> bytes.</summary>
    public byte[] V { get; }

    /// <summary>Frame number on the movie timeline, from 0 (continues across a loop).</summary>
    public long Index { get; set; }

    /// <summary>Presentation time in seconds on the movie timeline.</summary>
    public double Pts { get; set; }

    /// <summary>Copies the planes of a software yuv420p frame (tight rows).</summary>
    internal unsafe void CopyFrom(AVFrame* f)
    {
        var fmt = (AVPixelFormat)f->format;
        if (fmt is not (AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P))
        {
            throw new InvalidOperationException($"unexpected pixel format {ffmpeg.av_get_pix_fmt_name(fmt)}");
        }

        if (f->width != Width || f->height != Height)
        {
            throw new InvalidOperationException($"frame {f->width}x{f->height} does not fit the {Width}x{Height} buffer");
        }

        CopyPlane(f->data[0u], f->linesize[0u], Y, Width, Height);
        CopyPlane(f->data[1u], f->linesize[1u], U, Width / 2, Height / 2);
        CopyPlane(f->data[2u], f->linesize[2u], V, Width / 2, Height / 2);
    }

    private static unsafe void CopyPlane(byte* src, int stride, byte[] dst, int rowBytes, int rows)
    {
        if (src == null || Math.Abs((long)stride) < rowBytes || (long)rowBytes * rows > dst.LongLength)
        {
            throw new InvalidDataException("invalid video plane bounds");
        }

        fixed (byte* d = dst)
        {
            if (stride == rowBytes)
            {
                Buffer.MemoryCopy(src, d, dst.Length, (long)rowBytes * rows);
                return;
            }

            for (int r = 0; r < rows; r++)
            {
                Buffer.MemoryCopy(src + ((long)r * stride), d + ((long)r * rowBytes), rowBytes, rowBytes);
            }
        }
    }
}

/// <summary>A fixed pool of frame buffers plus the ready queue the main thread pulls from.</summary>
public sealed class FrameQueue
{
    private readonly object _lock = new();
    private readonly Queue<VideoFrame> _ready = new();
    private readonly Stack<VideoFrame> _free = new();
    private bool _closed;

    /// <summary>Creates a pool of <paramref name="buffers"/> frames (8 in the movie thread, S2.md 9 #1).</summary>
    public FrameQueue(int buffers, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(buffers, 2);
        for (int i = 0; i < buffers; i++)
        {
            _free.Push(new VideoFrame(width, height));
        }

        Capacity = buffers;
    }

    /// <summary>Number of buffers in the pool.</summary>
    public int Capacity { get; }

    /// <summary>Set by the decoder thread after its last frame.</summary>
    public volatile bool EndOfStream;

    /// <summary>Frames decoded and waiting.</summary>
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

    /// <summary>Decoder: takes a free buffer, waiting while the consumer holds them all. Null when closed.</summary>
    public VideoFrame? Rent()
    {
        lock (_lock)
        {
            while (_free.Count == 0 && !_closed)
            {
                Monitor.Wait(_lock, 50);
            }

            return _closed ? null : _free.Pop();
        }
    }

    /// <summary>Decoder: queues a filled buffer.</summary>
    public void Push(VideoFrame f)
    {
        lock (_lock)
        {
            _ready.Enqueue(f);
        }
    }

    /// <summary>Consumer: the next frame, or null.</summary>
    public VideoFrame? Peek()
    {
        lock (_lock)
        {
            return _ready.Count > 0 ? _ready.Peek() : null;
        }
    }

    /// <summary>Consumer: copies the stamps of up to <c>stamps.Length</c> ready frames, oldest first; returns the count.</summary>
    public int PeekStamps(Span<FrameStamp> stamps)
    {
        lock (_lock)
        {
            int n = 0;
            foreach (VideoFrame f in _ready)
            {
                if (n == stamps.Length)
                {
                    break;
                }

                stamps[n++] = new FrameStamp(f.Index, f.Pts);
            }

            return n;
        }
    }

    /// <summary>Consumer: removes the oldest ready frame.</summary>
    public VideoFrame Dequeue()
    {
        lock (_lock)
        {
            return _ready.Dequeue();
        }
    }

    /// <summary>Consumer: gives a buffer back to the pool.</summary>
    public void Return(VideoFrame f)
    {
        lock (_lock)
        {
            _free.Push(f);
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Wakes the decoder and makes <see cref="Rent"/> return null.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            Monitor.PulseAll(_lock);
        }
    }
}

/// <summary>What a <see cref="PacketQueue"/> entry means.</summary>
internal enum PacketKind : byte
{
    /// <summary>A video packet.</summary>
    Packet,

    /// <summary>End of the stream: the decoder drains and stops.</summary>
    End,

    /// <summary>The demuxer looped: the decoder drains, flushes and starts a new pass (<see cref="PacketQueue.Marker"/>).</summary>
    Loop,
}

/// <summary>Video packets from the demuxer to the decoder thread (<c>AVPacket*</c> as nint).</summary>
internal sealed unsafe class PacketQueue
{
    private readonly object _lock = new();
    private readonly Queue<(nint Packet, PacketKind Kind)> _q = new();
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

    /// <summary>Takes ownership of a packet, freeing it if the queue is closed.</summary>
    public void Add(AVPacket* pkt)
    {
        lock (_lock)
        {
            if (_closed)
            {
                if (pkt != null)
                {
                    ffmpeg.av_packet_free(&pkt);
                }

                return;
            }

            _q.Enqueue(((nint)pkt, PacketKind.Packet));
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Adds an end or loop marker.</summary>
    public void Marker(PacketKind kind)
    {
        lock (_lock)
        {
            _q.Enqueue((0, kind));
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Waits for an entry. Returns false when closed and empty.</summary>
    public bool Take(out AVPacket* pkt, out PacketKind kind)
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
                kind = PacketKind.End;
                return false;
            }

            (nint p, kind) = _q.Dequeue();
            pkt = (AVPacket*)p;
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>Waits up to <paramref name="ms"/> for a change (the demuxer's flow control).</summary>
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

    /// <summary>Closes the queue and frees the packets still in it.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            while (_q.Count > 0)
            {
                (nint p, PacketKind kind) = _q.Dequeue();
                if (kind == PacketKind.Packet && p != 0)
                {
                    var pkt = (AVPacket*)p;
                    ffmpeg.av_packet_free(&pkt);
                }
            }

            Monitor.PulseAll(_lock);
        }
    }
}
