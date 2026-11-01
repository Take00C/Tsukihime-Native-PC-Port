// The movie thread(s) (ARCHITECTURE 3.4, 7.5; S2.md 9 #1): one thread demuxes and decodes the AAC into the PCM ring (4 s of
// float stereo 48 kHz), one decodes H.264 (FFmpeg frame threads) into 8 pinned frame buffers; the main thread picks the frame
// (FrameSelector) and the mixer pulls the ring (MovieAudioSource). Lifted from spike S2 MoviePipeline.cs, software decode only,
// plus the loop of the title backgrounds (system_flow.md 4.2).
using FFmpeg.AutoGen;
using Tsuki.Media.Ffmpeg;

namespace Tsuki.Media.Movie;

/// <summary>Options of a <see cref="MoviePipeline"/>.</summary>
/// <param name="StartSeconds">Start the movie here (a seek to the key frame at or before it; earlier frames and samples are trimmed).</param>
/// <param name="FrameBuffers">Pinned frame buffers (8, S2.md 9 #1).</param>
/// <param name="VideoThreads">H.264 decoder threads; 0 = automatic (FFmpeg frame threads).</param>
/// <param name="Loop">At the end of the file seek to <paramref name="LoopPointSeconds"/> and go on; the timeline continues.</param>
/// <param name="LoopPointSeconds">Where a looping movie restarts (the title backgrounds: 8.0333 s, system_flow.md 4.2).</param>
/// <param name="PcmRingFrames">Capacity of the audio ring in frames (4 s).</param>
public sealed record MoviePipelineOptions(
    double StartSeconds = 0, int FrameBuffers = 8, int VideoThreads = 0, bool Loop = false,
    double LoopPointSeconds = MoviePipelineOptions.TitleLoopPointSeconds, int PcmRingFrames = 4 * 48_000)
{
    /// <summary>The loop point of the title backgrounds, 8.0333 s (f32 at eboot VA 0x2186E4, system_flow.md 4.2).</summary>
    public const double TitleLoopPointSeconds = 8.0333;
}

/// <summary>Demuxes and decodes one movie into a <see cref="FrameQueue"/> and a <see cref="PcmRing"/>.</summary>
public sealed unsafe class MoviePipeline : IDisposable
{
    private readonly Demuxer _dmx;
    private readonly VideoDecoder _vdec;
    private readonly AudioStreamDecoder? _adec;
    private readonly PacketQueue _vpq = new();
    private readonly Lock _lifetimeLock = new();
    private readonly MoviePipelineOptions _options;
    private readonly double _frameRate;
    private Thread? _demuxThread;
    private Thread? _decodeThread;
    private volatile bool _stop;
    private bool _disposed;
    private bool _endQueued;
    private readonly double _loopPoint;
    private readonly long _loopStartIndex;
    private long _startIndex;      // decode thread: file frame index below which frames are trimmed
    private long _trimSample;      // demux thread: stream position below which audio is trimmed
    private long _nextSample;      // demux thread
    private long _framesDecoded;
    private long _framesBeforeStart;
    private long _lastVideoIndex = -1;
    private long _firstAudioSample = -1;
    private long _audioSamplesDecoded;
    private long _audioSamplesOut;
    private long _loops;
    private long _videoPacketErrors;
    private volatile Exception? _error;
    private volatile bool _demuxEnded;
    private volatile bool _discardAudio;

    /// <summary>Opens <paramref name="path"/> (FFmpeg must be loaded: <see cref="FfmpegLibrary.Load"/>).</summary>
    public MoviePipeline(string path, MoviePipelineOptions? options = null)
    {
        _options = options ?? new MoviePipelineOptions();
        if (!FfmpegLibrary.IsLoaded)
        {
            throw new InvalidOperationException("FfmpegLibrary.Load has not been called");
        }

        _dmx = new Demuxer(path);
        try
        {
            AVStream* vs = _dmx.Video;
            Width = vs->codecpar->width;
            Height = vs->codecpar->height;
            AVRational fr = vs->avg_frame_rate.num != 0 ? vs->avg_frame_rate : vs->r_frame_rate;
            _frameRate = ffmpeg.av_q2d(fr);
            if (!(_frameRate > 0))
            {
                throw new InvalidOperationException($"{Path.GetFileName(path)}: the video stream has no frame rate");
            }

            VideoTimeBase = vs->time_base;
            NbFrames = vs->nb_frames;
            DurationSeconds = _dmx.DurationSeconds;
            _vdec = new VideoDecoder(vs, _options.VideoThreads);
            if (_dmx.AudioIndex >= 0)
            {
                _adec = new AudioStreamDecoder(_dmx.Audio);
            }

            _loopPoint = _options.LoopPointSeconds < DurationSeconds ? Math.Max(0, _options.LoopPointSeconds) : 0;
            _loopStartIndex = (long)Math.Round(_loopPoint * _frameRate);
            _startIndex = (long)Math.Round(_options.StartSeconds * _frameRate);
            _trimSample = (long)Math.Round(_options.StartSeconds * AudioStreamDecoder.OutRate);
            if (_options.StartSeconds > 0)
            {
                _dmx.SeekBackward(_options.StartSeconds);
            }

            Frames = new FrameQueue(_options.FrameBuffers, Width, Height);
            Pcm = new PcmRing(_options.PcmRingFrames);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Luma width.</summary>
    public int Width { get; }

    /// <summary>Luma height.</summary>
    public int Height { get; }

    /// <summary>Frames per second (the stream's average rate).</summary>
    public double Fps => _frameRate;

    /// <summary>Time base of the video stream.</summary>
    public AVRational VideoTimeBase { get; }

    /// <summary>Frames in the file as the container says (0 when unknown).</summary>
    public long NbFrames { get; }

    /// <summary>Container duration in seconds.</summary>
    public double DurationSeconds { get; }

    /// <summary>True when the file has an audio track (a silent track counts: the mixer plays silence).</summary>
    public bool HasAudio => _adec != null;

    /// <summary>The decoded frames.</summary>
    public FrameQueue Frames { get; }

    /// <summary>The decoded audio: float32 stereo 48 kHz.</summary>
    public PcmRing Pcm { get; }

    /// <summary>Video frames the decoder produced (trimmed ones included).</summary>
    public long FramesDecoded => Volatile.Read(ref _framesDecoded);

    /// <summary>Frames dropped by the start trim.</summary>
    public long FramesBeforeStart => Volatile.Read(ref _framesBeforeStart);

    /// <summary>Timeline index of the last frame queued, or -1.</summary>
    public long LastVideoIndex => Volatile.Read(ref _lastVideoIndex);

    /// <summary>Stream position (48 kHz samples) of the first sample written to the ring, or -1.</summary>
    public long FirstAudioSample => Volatile.Read(ref _firstAudioSample);

    /// <summary>Samples per channel the AAC decoder produced (trimmed ones included).</summary>
    public long AudioSamplesDecoded => Volatile.Read(ref _audioSamplesDecoded);

    /// <summary>Samples per channel written to the ring.</summary>
    public long AudioSamplesOut => Volatile.Read(ref _audioSamplesOut);

    /// <summary>Video and audio packets the decoders refused as invalid data; the pipeline drops them and goes on.</summary>
    public long PacketErrors => Volatile.Read(ref _videoPacketErrors) + (_adec?.PacketErrors ?? 0);

    /// <summary>Times a looping movie restarted.</summary>
    public long Loops => Volatile.Read(ref _loops);

    /// <summary>True once the demux thread has read the whole file (and the ring is complete).</summary>
    public bool DemuxEnded => _demuxEnded;

    /// <summary>H.264 decoder threads FFmpeg opened.</summary>
    public int VideoThreadCount => _vdec.ThreadCount;

    /// <summary>The first exception a movie thread died of, or null.</summary>
    public Exception? Error => _error;

    /// <summary>Starts the demux and decode threads.</summary>
    public void Start()
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stop || _demuxThread != null)
            {
                throw new InvalidOperationException("the pipeline is already started or stopped");
            }

            try
            {
                var demux = new Thread(DemuxLoop) { Name = "tsuki-movie-demux", IsBackground = true };
                demux.Start();
                _demuxThread = demux;
                var decode = new Thread(DecodeLoop) { Name = "tsuki-movie-vdec", IsBackground = true };
                decode.Start();
                _decodeThread = decode;
            }
            catch
            {
                Dispose();
                throw;
            }
        }
    }

    private bool QueuesFull()
    {
        bool audioFull = !_discardAudio && Pcm.FreeFrames < 8192;
        bool audioEnough = !HasAudio || _discardAudio || Pcm.BufferedFrames >= AudioStreamDecoder.OutRate;
        return _vpq.Count >= 900 || audioFull || (_vpq.Count >= 45 && audioEnough);
    }

    /// <summary>
    /// Nobody will read the PCM ring (no mixer, or the audio device stalled): the demux thread decodes the audio track and drops it, so a
    /// full ring never blocks the video packets that follow it in the file.
    /// </summary>
    public void DiscardAudio() => _discardAudio = true;

    /// <summary>True after <see cref="DiscardAudio"/>.</summary>
    public bool AudioDiscarded => _discardAudio;

    private void DemuxLoop()
    {
        AVPacket* pkt = null;
        try
        {
            pkt = ffmpeg.av_packet_alloc();
            if (pkt == null)
            {
                throw new OutOfMemoryException("av_packet_alloc failed");
            }

            long packetsThisPass = 0;
            while (!_stop)
            {
                // flow control (ffplay-like): wait while both queues hold enough, or the ring is full
                while (!_stop && QueuesFull())
                {
                    _vpq.WaitForChange(5);
                }

                if (_stop)
                {
                    break;
                }

                if (!_dmx.Read(pkt))
                {
                    if (_options.Loop && packetsThisPass > 0 && !_stop)
                    {
                        // loop: drain both decoders, seek to the loop point, keep the timeline going
                        _adec?.Decode(null, OnPcm);
                        _adec?.Reset();
                        _trimSample = (long)Math.Round(_loopPoint * AudioStreamDecoder.OutRate);
                        _vpq.Marker(PacketKind.Loop);
                        _dmx.SeekBackward(_loopPoint);
                        Interlocked.Increment(ref _loops);
                        packetsThisPass = 0;
                        continue;
                    }

                    _adec?.Decode(null, OnPcm);
                    _vpq.Marker(PacketKind.End); // end of stream for the decoder
                    _endQueued = true;
                    break;
                }

                packetsThisPass++;
                if (pkt->stream_index == _dmx.VideoIndex)
                {
                    AVPacket* v = ffmpeg.av_packet_alloc();
                    ffmpeg.av_packet_move_ref(v, pkt);
                    _vpq.Add(v);
                }
                else if (_adec != null && pkt->stream_index == _dmx.AudioIndex)
                {
                    _adec.Decode(pkt, OnPcm);
                    ffmpeg.av_packet_unref(pkt);
                }
                else
                {
                    ffmpeg.av_packet_unref(pkt);
                }
            }
        }
        catch (Exception e)
        {
            _error ??= e;
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            if (!_endQueued)
            {
                _vpq.Marker(PacketKind.End); // a stopped or failed demuxer still ends the decoder thread
            }

            Pcm.Complete();
            _adec?.Dispose();
            _dmx.Dispose();
            _demuxEnded = true;
        }
    }

    private void OnPcm(ReadOnlySpan<float> samples, long startSample)
    {
        int frames = samples.Length / 2;
        if (startSample < 0)
        {
            startSample = _nextSample;
        }

        _nextSample = startSample + frames;
        Interlocked.Add(ref _audioSamplesDecoded, frames);
        if (startSample + frames <= _trimSample)
        {
            return;
        }

        int skip = startSample < _trimSample ? (int)(_trimSample - startSample) : 0;
        if (Volatile.Read(ref _firstAudioSample) < 0)
        {
            Volatile.Write(ref _firstAudioSample, startSample + skip);
        }

        if (_discardAudio)
        {
            return;
        }

        ReadOnlySpan<float> rest = samples[(skip * 2)..];
        while (rest.Length > 0 && !_stop && !_discardAudio)
        {
            int n = Pcm.Write(rest);
            Interlocked.Add(ref _audioSamplesOut, n);
            rest = rest[(n * 2)..];
            if (rest.Length > 0)
            {
                Thread.Sleep(2);
            }
        }
    }

    private void DecodeLoop()
    {
        double tb = ffmpeg.av_q2d(VideoTimeBase);
        long indexOffset = 0;      // timeline index = file index + indexOffset
        long lastIndex = -1;
        try
        {
            bool eof = false;
            while (!eof && !_stop)
            {
                if (!_vpq.Take(out AVPacket* pkt, out PacketKind kind))
                {
                    break;
                }

                if (kind == PacketKind.Packet)
                {
                    int ret = _vdec.Send(pkt);
                    ffmpeg.av_packet_free(&pkt);
                    if (ret == ffmpeg.AVERROR_INVALIDDATA)
                    {
                        Interlocked.Increment(ref _videoPacketErrors);       // a corrupt packet is dropped, as ffplay does
                    }
                    else if (ret < 0 && ret != ffmpeg.AVERROR_EOF)
                    {
                        Av.Check(ret, "avcodec_send_packet(video)");
                    }
                }
                else
                {
                    Av.Check(_vdec.Send(null), "avcodec_send_packet(video, drain)");
                }

                // drain what the decoder has; after a drain request it ends with EOF
                int consecutiveErrors = 0;
                while (!_stop)
                {
                    int ret = _vdec.Receive(out AVFrame* f);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        break;
                    }

                    if (ret == ffmpeg.AVERROR_INVALIDDATA && ++consecutiveErrors <= 100)
                    {
                        Interlocked.Increment(ref _videoPacketErrors);       // a corrupt frame is dropped, as ffplay does
                        continue;
                    }

                    consecutiveErrors = 0;

                    if (ret == ffmpeg.AVERROR_EOF)
                    {
                        if (kind == PacketKind.Loop)
                        {
                            _vdec.Flush();
                            _startIndex = _loopStartIndex;
                            indexOffset = lastIndex + 1 - _loopStartIndex;
                        }
                        else
                        {
                            eof = true;
                        }

                        break;
                    }

                    Av.Check(ret, "avcodec_receive_frame(video)");
                    Interlocked.Increment(ref _framesDecoded);
                    double pts = f->best_effort_timestamp * tb;
                    long fileIndex = (long)Math.Round(pts * _frameRate);
                    if (fileIndex < _startIndex)
                    {
                        Interlocked.Increment(ref _framesBeforeStart);
                        continue;
                    }

                    VideoFrame? buf = Frames.Rent();
                    if (buf == null)
                    {
                        return;
                    }

                    buf.CopyFrom(f);
                    buf.Index = fileIndex + indexOffset;
                    buf.Pts = pts + (indexOffset / _frameRate);
                    lastIndex = buf.Index;
                    Volatile.Write(ref _lastVideoIndex, lastIndex);
                    Frames.Push(buf);
                }
            }
        }
        catch (Exception e)
        {
            _error ??= e;
        }
        finally
        {
            _vdec.Dispose();
            Frames.EndOfStream = true;
        }
    }

    /// <summary>Stops both threads (waits up to 2 s for each) and wakes anything blocked on the queues.</summary>
    public void Stop()
    {
        lock (_lifetimeLock)
        {
            _stop = true;
            Frames?.Close();
            _vpq.Close();
            _demuxThread?.Join(2000);
            _decodeThread?.Join(2000);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            // Started workers release their native objects only after their last native call.
            if (_decodeThread == null)
            {
                _vdec?.Dispose();
            }

            if (_demuxThread == null)
            {
                _adec?.Dispose();
                _dmx?.Dispose();
            }
        }
    }
}
