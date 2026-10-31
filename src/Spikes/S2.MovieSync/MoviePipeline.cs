// Spike S2: the movie thread(s): demux + AAC decode on one thread, H.264 decode + plane copy on another
// (ARCHITECTURE 3.4 movie thread; 7.5 IMovieDecoder default = FFmpeg mov + h264 + aac).
using System.Diagnostics;
using FFmpeg.AutoGen;

namespace S2.MovieSync;

/// <summary>Demuxes and decodes one movie into a <see cref="FrameQueue"/> and a <see cref="PcmRing"/>.</summary>
internal sealed unsafe class MoviePipeline : IDisposable
{
    private readonly Demuxer _dmx;
    private readonly VideoDecoder _vdec;
    private readonly AudioDecoder? _adec;
    private readonly PacketQueue _vpq = new();
    private readonly long _startIndex;
    private readonly long _startSample;
    private Thread? _demuxThread;
    private Thread? _decodeThread;
    private volatile bool _stop;
    private long _nextSample;
    private Exception? _error;

    public MoviePipeline(string path, string hw, int threads, double startSeconds, int frameBuffers)
    {
        _dmx = new Demuxer(path);
        AVStream* vs = _dmx.Video;
        Width = vs->codecpar->width;
        Height = vs->codecpar->height;
        AVRational fr = vs->avg_frame_rate.num != 0 ? vs->avg_frame_rate : vs->r_frame_rate;
        Fps = ffmpeg.av_q2d(fr);
        VideoTimeBase = vs->time_base;
        NbFrames = vs->nb_frames;
        VideoDurationSeconds = vs->duration * ffmpeg.av_q2d(vs->time_base);
        ColorTags = $"{ffmpeg.av_color_primaries_name(vs->codecpar->color_primaries)}/{ffmpeg.av_color_transfer_name(vs->codecpar->color_trc)}/{ffmpeg.av_color_space_name(vs->codecpar->color_space)}/{ffmpeg.av_color_range_name(vs->codecpar->color_range)}";
        PixFmt = ffmpeg.av_get_pix_fmt_name((AVPixelFormat)vs->codecpar->format);
        Profile = $"{ffmpeg.avcodec_profile_name(vs->codecpar->codec_id, vs->codecpar->profile)} {vs->codecpar->level / 10.0:F1}";

        var before = Native.ThreadIds();
        _vdec = new VideoDecoder(vs, hw, threads);
        var after = Native.ThreadIds();
        after.ExceptWith(before);
        FfmpegThreadIds = after;

        if (_dmx.AudioIndex >= 0)
        {
            AVStream* aud = _dmx.Audio;
            _adec = new AudioDecoder(aud);
            AudioDurationSeconds = aud->duration * ffmpeg.av_q2d(aud->time_base);
        }

        StartSeconds = startSeconds;
        _startIndex = (long)Math.Round(startSeconds * Fps);
        _startSample = (long)Math.Round(startSeconds * AudioDecoder.OutRate);
        if (startSeconds > 0)
        {
            _dmx.SeekBackward(startSeconds);
        }

        Frames = new FrameQueue(frameBuffers, Width, Height);
        Pcm = new PcmRing(AudioDecoder.OutRate * 4);
    }

    public int Width { get; }

    public int Height { get; }

    public double Fps { get; }

    public AVRational VideoTimeBase { get; }

    public long NbFrames { get; }

    public double VideoDurationSeconds { get; }

    public double AudioDurationSeconds { get; }

    public string ColorTags { get; }

    public string PixFmt { get; }

    public string Profile { get; }

    public string FormatName => _dmx.FormatName;

    public VideoDecoder Video => _vdec;

    public AudioDecoder? Audio => _adec;

    public bool HasAudio => _adec != null;

    public double StartSeconds { get; }

    public FrameQueue Frames { get; }

    public PcmRing Pcm { get; }

    /// <summary>OS thread ids created by avcodec_open2 (the H.264 frame/slice worker threads).</summary>
    public HashSet<int> FfmpegThreadIds { get; }

    public int DemuxThreadId { get; private set; }

    public int DecodeThreadId { get; private set; }

    public double DemuxThreadCpuAtExit { get; private set; } = double.NaN;

    public double DecodeThreadCpuAtExit { get; private set; } = double.NaN;

    /// <summary>Time in avcodec_send_packet / avcodec_receive_frame per output frame (ms).</summary>
    public Series DecodeCallMs { get; } = new("ms");

    public Series TransferMs { get; } = new("ms");

    public Series CopyMs { get; } = new("ms");

    public Series PoolWaitMs { get; } = new("ms");

    public long FramesDecoded { get; private set; }

    public long FramesBeforeStart { get; private set; }

    public long LastVideoIndex { get; private set; } = -1;

    /// <summary>Stream position (48 kHz samples) of the first sample written to the ring, or -1.</summary>
    public long FirstAudioSample { get; private set; } = -1;

    public long AudioSamplesOut { get; private set; }

    public long AudioSamplesDecoded { get; private set; }

    public int VideoPacketQueueMax => _vpq.MaxCount;

    public bool AudioEnded { get; private set; }

    /// <summary>Called by the demux thread when the audio track is finished.</summary>
    public Action? OnAudioEnd { get; set; }

    public Exception? Error => _error;

    public void Start()
    {
        _demuxThread = new Thread(DemuxLoop) { Name = "s2-demux", IsBackground = true };
        _decodeThread = new Thread(DecodeLoop) { Name = "s2-vdec", IsBackground = true };
        _demuxThread.Start();
        _decodeThread.Start();
    }

    private void DemuxLoop()
    {
        DemuxThreadId = (int)Native.GetCurrentThreadId();
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        try
        {
            while (!_stop)
            {
                // flow control (ffplay-like): wait while both queues hold enough, or the ring is full
                while (!_stop && ((_vpq.Count >= 45 && (!HasAudio || Pcm.BufferedFrames >= AudioDecoder.OutRate)) || _vpq.Count >= 900 || Pcm.FreeFrames < 8192))
                {
                    _vpq.WaitForChange(5);
                }

                if (_stop)
                {
                    break;
                }

                if (!_dmx.Read(pkt))
                {
                    _vpq.Add(null); // end of stream for the decoder
                    if (_adec != null)
                    {
                        _adec.Decode(null, OnPcm);
                    }

                    break;
                }

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
            AudioEnded = true;
            OnAudioEnd?.Invoke();
            DemuxThreadCpuAtExit = Native.CurrentThreadCpuSeconds();
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
        AudioSamplesDecoded += frames;
        if (startSample + frames <= _startSample)
        {
            return;
        }

        int skip = startSample < _startSample ? (int)(_startSample - startSample) : 0;
        if (FirstAudioSample < 0)
        {
            FirstAudioSample = startSample + skip;
        }

        var rest = samples[(skip * 2)..];
        while (rest.Length > 0 && !_stop)
        {
            int n = Pcm.Write(rest);
            AudioSamplesOut += n;
            rest = rest[(n * 2)..];
            if (rest.Length > 0)
            {
                Thread.Sleep(2);
            }
        }
    }

    private void DecodeLoop()
    {
        DecodeThreadId = (int)Native.GetCurrentThreadId();
        double tb = ffmpeg.av_q2d(VideoTimeBase);
        long callTicks = 0;
        try
        {
            bool eof = false;
            while (!eof && !_stop)
            {
                if (!_vpq.Take(out AVPacket* pkt))
                {
                    break;
                }

                long t0 = Stopwatch.GetTimestamp();
                int ret = _vdec.Send(pkt);
                callTicks += Stopwatch.GetTimestamp() - t0;
                if (pkt != null)
                {
                    ffmpeg.av_packet_free(&pkt);
                }

                if (ret < 0 && ret != ffmpeg.AVERROR_EOF)
                {
                    FfmpegLib.Check(ret, "avcodec_send_packet(video)");
                }

                while (!_stop)
                {
                    long t1 = Stopwatch.GetTimestamp();
                    ret = _vdec.Receive(out AVFrame* f);
                    callTicks += Stopwatch.GetTimestamp() - t1;
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        break;
                    }

                    if (ret == ffmpeg.AVERROR_EOF)
                    {
                        eof = true;
                        break;
                    }

                    FfmpegLib.Check(ret, "avcodec_receive_frame(video)");
                    FramesDecoded++;
                    DecodeCallMs.Add(Report.Ms(callTicks));
                    callTicks = 0;
                    if (_vdec.HwName != "none")
                    {
                        TransferMs.Add(_vdec.LastTransferMs);
                    }

                    double pts = f->best_effort_timestamp * tb;
                    long index = (long)Math.Round(pts * Fps);
                    if (index < _startIndex)
                    {
                        FramesBeforeStart++;
                        continue;
                    }

                    VideoFrame? buf = Frames.Rent(out double waited);
                    if (buf == null)
                    {
                        return;
                    }

                    PoolWaitMs.Add(waited);
                    long t2 = Stopwatch.GetTimestamp();
                    buf.CopyFrom(f);
                    CopyMs.Add(Report.Ms(Stopwatch.GetTimestamp() - t2));
                    buf.Index = index;
                    buf.Pts = pts;
                    LastVideoIndex = index;
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
            Frames.EndOfStream = true;
            DecodeThreadCpuAtExit = Native.CurrentThreadCpuSeconds();
        }
    }

    public void Stop()
    {
        _stop = true;
        Frames.Close();
        _vpq.Close();
        _demuxThread?.Join(2000);
        _decodeThread?.Join(2000);
    }

    public void Dispose()
    {
        Stop();
        _vdec.Dispose();
        _adec?.Dispose();
        _dmx.Dispose();
    }
}
