// The FFmpeg side of the movie thread (ARCHITECTURE 7.5; archives_ui_movies.md 9.1: MP4, H.264 1920x1080 yuv420p, AAC-LC
// 48 kHz stereo): the demuxer, the software H.264 decoder with frame threads and the AAC decoder with swresample to float32
// interleaved stereo at 48 kHz (the mixer format, ARCHITECTURE 7.1). Lifted from spike S2 (Ffmpeg.cs), software decode only:
// d3d12va with one thread fails after frame 2,143 (S2.md 5), so no hardware path is carried over.
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Tsuki.Media.Movie;

/// <summary>Error text and the checked-call helper for the FFmpeg wrappers.</summary>
internal static unsafe class Av
{
    public static string Err(int code)
    {
        byte* buf = stackalloc byte[256];
        ffmpeg.av_strerror(code, buf, 256);
        return Marshal.PtrToStringAnsi((nint)buf) ?? code.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static void Check(int ret, string what)
    {
        if (ret < 0)
        {
            throw new InvalidOperationException($"{what}: {Err(ret)} ({ret})");
        }
    }
}

/// <summary>An opened MP4 with its video and audio stream indexes.</summary>
internal sealed unsafe class Demuxer : IDisposable
{
    private AVFormatContext* _fmt;

    public Demuxer(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
        {
            throw new ArgumentException("movies require a local file path", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("movie file not found", fullPath);
        }

        AVFormatContext* fmt = null;
        AVDictionary* options = null;
        try
        {
            AVInputFormat* mov = ffmpeg.av_find_input_format("mov");
            if (mov == null)
            {
                throw new InvalidOperationException("the MP4 demuxer is unavailable");
            }

            Av.Check(ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0), "movie protocols");
            Av.Check(ffmpeg.av_dict_set(&options, "format_whitelist", "mov", 0), "movie formats");
            Av.Check(ffmpeg.av_dict_set(&options, "codec_whitelist", "h264,aac", 0), "movie codecs");
            Av.Check(ffmpeg.av_dict_set(&options, "enable_drefs", "0", 0), "movie data references");
            Av.Check(ffmpeg.av_dict_set(&options, "use_absolute_path", "0", 0), "movie absolute references");
            Av.Check(ffmpeg.avformat_open_input(&fmt, "file:" + fullPath, mov, &options), $"avformat_open_input({Path.GetFileName(path)})");
            _fmt = fmt;
            Av.Check(ffmpeg.avformat_find_stream_info(_fmt, null), "avformat_find_stream_info");
            VideoIndex = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            AudioIndex = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, VideoIndex, null, 0);
            Av.Check(VideoIndex, "no video stream");
            if (Video->codecpar->codec_id != AVCodecID.AV_CODEC_ID_H264
                || (Audio != null && Audio->codecpar->codec_id != AVCodecID.AV_CODEC_ID_AAC))
            {
                throw new InvalidDataException("movies require H.264 video and optional AAC audio");
            }

            FormatName = Marshal.PtrToStringAnsi((nint)_fmt->iformat->name) ?? "?";
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }
    }

    public string FormatName { get; } = "?";

    public int VideoIndex { get; }

    /// <summary>Audio stream index, or a negative value when the file has no audio.</summary>
    public int AudioIndex { get; }

    public AVStream* Video => _fmt->streams[VideoIndex];

    public AVStream* Audio => AudioIndex >= 0 ? _fmt->streams[AudioIndex] : null;

    public double DurationSeconds => _fmt->duration / (double)ffmpeg.AV_TIME_BASE;

    /// <summary>Seeks to the key frame at or before <paramref name="seconds"/> (both streams follow).</summary>
    public void SeekBackward(double seconds)
    {
        long ts = (long)Math.Round(seconds * ffmpeg.AV_TIME_BASE);
        Av.Check(ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD), "av_seek_frame");
    }

    /// <summary>Reads the next packet; returns false at end of file.</summary>
    public bool Read(AVPacket* pkt)
    {
        int ret = ffmpeg.av_read_frame(_fmt, pkt);
        if (ret == ffmpeg.AVERROR_EOF)
        {
            return false;
        }

        Av.Check(ret, "av_read_frame");
        return true;
    }

    public void Dispose()
    {
        if (_fmt != null)
        {
            AVFormatContext* f = _fmt;
            ffmpeg.avformat_close_input(&f);
            _fmt = null;
        }
    }
}

/// <summary>Software H.264 decoder with frame and slice threads (no hardware path).</summary>
internal sealed unsafe class VideoDecoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVFrame* _frame;

    /// <param name="st">The video stream.</param>
    /// <param name="threads">Decoder threads; 0 = one per logical core (FFmpeg caps it).</param>
    public VideoDecoder(AVStream* st, int threads)
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
        if (codec == null)
        {
            throw new InvalidOperationException("no decoder for the video stream");
        }

        CodecName = Marshal.PtrToStringAnsi((nint)codec->name) ?? "?";
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        try
        {
            Av.Check(ffmpeg.avcodec_parameters_to_context(_ctx, st->codecpar), "avcodec_parameters_to_context(video)");
            _ctx->pkt_timebase = st->time_base;
            _ctx->thread_count = threads;
            _ctx->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
            Av.Check(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2(video)");
            ThreadCount = _ctx->thread_count;
            _frame = ffmpeg.av_frame_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string CodecName { get; }

    public int ThreadCount { get; }

    /// <summary>Sends a packet (null drains the decoder).</summary>
    public int Send(AVPacket* pkt) => ffmpeg.avcodec_send_packet(_ctx, pkt);

    /// <summary>Receives one frame (valid until the next call). Returns 0, EAGAIN or EOF.</summary>
    public int Receive(out AVFrame* frame)
    {
        frame = null;
        ffmpeg.av_frame_unref(_frame);
        int ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
        if (ret < 0)
        {
            return ret;
        }

        frame = _frame;
        return 0;
    }

    /// <summary>Drops everything in flight so the decoder takes a fresh key frame (after a seek or a drain).</summary>
    public void Flush() => ffmpeg.avcodec_flush_buffers(_ctx);

    public void Dispose()
    {
        if (_frame != null)
        {
            AVFrame* f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }

        if (_ctx != null)
        {
            AVCodecContext* c = _ctx;
            ffmpeg.avcodec_free_context(&c);
            _ctx = null;
        }
    }
}

/// <summary>AAC decoder with swresample to float32 interleaved stereo at 48 kHz.</summary>
internal sealed unsafe class AudioStreamDecoder : IDisposable
{
    /// <summary>The mixer rate.</summary>
    public const int OutRate = 48_000;

    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private SwrContext* _swr;
    private float[] _out = new float[2 * 8192];

    public AudioStreamDecoder(AVStream* st)
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
        if (codec == null)
        {
            throw new InvalidOperationException("no decoder for the audio stream");
        }

        CodecName = Marshal.PtrToStringAnsi((nint)codec->name) ?? "?";
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        try
        {
            Av.Check(ffmpeg.avcodec_parameters_to_context(_ctx, st->codecpar), "avcodec_parameters_to_context(audio)");
            _ctx->pkt_timebase = st->time_base;
            Av.Check(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2(audio)");
            TimeBase = st->time_base;
            InRate = _ctx->sample_rate;
            InChannels = _ctx->ch_layout.nb_channels;
            _frame = ffmpeg.av_frame_alloc();
            CreateResampler();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string CodecName { get; }

    public int InRate { get; }

    public int InChannels { get; }

    public AVRational TimeBase { get; }

    public bool Resampling => InRate != OutRate;

    /// <summary>Packets the decoder refused as invalid data and the pipeline dropped.</summary>
    public long PacketErrors { get; private set; }

    private void CreateResampler()
    {
        if (_swr != null)
        {
            SwrContext* old = _swr;
            ffmpeg.swr_free(&old);
            _swr = null;
        }

        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_default(&outLayout, 2);
        SwrContext* swr = null;
        Av.Check(ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, OutRate,
            &_ctx->ch_layout, _ctx->sample_fmt, _ctx->sample_rate, 0, null), "swr_alloc_set_opts2");
        _swr = swr;
        Av.Check(ffmpeg.swr_init(_swr), "swr_init");
    }

    /// <summary>
    /// Sends one packet (null drains) and converts every frame it yields. <paramref name="sink"/> gets the interleaved stereo
    /// samples and the stream position (in output samples) of the first one, or -1 when the frame has no timestamp.
    /// </summary>
    public void Decode(AVPacket* pkt, Action<ReadOnlySpan<float>, long> sink)
    {
        int ret = ffmpeg.avcodec_send_packet(_ctx, pkt);
        if (ret == ffmpeg.AVERROR_INVALIDDATA)
        {
            PacketErrors++;                  // a corrupt packet is dropped, as ffplay does
            return;
        }

        if (ret < 0 && ret != ffmpeg.AVERROR_EOF)
        {
            Av.Check(ret, "avcodec_send_packet(audio)");
        }

        while (true)
        {
            ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            Av.Check(ret, "avcodec_receive_frame(audio)");
            long pts = _frame->best_effort_timestamp;
            long startSample = pts == ffmpeg.AV_NOPTS_VALUE
                ? -1
                : ffmpeg.av_rescale_q(pts, TimeBase, new AVRational { num = 1, den = OutRate });
            int maxOut = ffmpeg.swr_get_out_samples(_swr, _frame->nb_samples);
            Av.Check(maxOut, "swr_get_out_samples");
            int samples = checked(maxOut * 2);
            if (_out.Length < samples)
            {
                _out = new float[samples];
            }

            int got;
            fixed (float* o = _out)
            {
                byte* op = (byte*)o;
                got = ffmpeg.swr_convert(_swr, &op, maxOut, _frame->extended_data, _frame->nb_samples);
            }

            Av.Check(got, "swr_convert");
            sink(_out.AsSpan(0, got * 2), startSample);
            ffmpeg.av_frame_unref(_frame);
        }
    }

    /// <summary>Forgets everything in flight (after a seek): the codec and a fresh resampler.</summary>
    public void Reset()
    {
        ffmpeg.avcodec_flush_buffers(_ctx);
        CreateResampler();
    }

    public void Dispose()
    {
        if (_frame != null)
        {
            AVFrame* f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }

        if (_swr != null)
        {
            SwrContext* s = _swr;
            ffmpeg.swr_free(&s);
            _swr = null;
        }

        if (_ctx != null)
        {
            AVCodecContext* c = _ctx;
            ffmpeg.avcodec_free_context(&c);
            _ctx = null;
        }
    }
}
