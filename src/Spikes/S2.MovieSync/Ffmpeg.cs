// Spike S2: FFmpeg.AutoGen demux + H.264 (software or d3d11va / dxva2 / d3d12va) + AAC decode
// (ARCHITECTURE 7.5; archives_ui_movies 9.1: MP4, H.264 1920x1080 yuv420p, AAC-LC 48 kHz stereo).
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace S2.MovieSync;

/// <summary>Loads the FFmpeg DLLs that the build copied next to the executable (ARCHITECTURE 1.11).</summary>
internal static unsafe class FfmpegLib
{
    public static string RootPath { get; private set; } = "";

    public static string Init()
    {
        RootPath = AppContext.BaseDirectory;
        ffmpeg.RootPath = RootPath;
        DynamicallyLoadedBindings.Initialize();
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        return Version(ffmpeg.avcodec_version());
    }

    public static string Version(uint v) => $"{v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}";

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

    /// <summary>Hardware device types FFmpeg was built with (av_hwdevice_iterate_types).</summary>
    public static List<string> HwDeviceTypes()
    {
        var list = new List<string>();
        var t = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        while ((t = ffmpeg.av_hwdevice_iterate_types(t)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            list.Add(ffmpeg.av_hwdevice_get_type_name(t));
        }

        return list;
    }

    /// <summary>Hardware configs the h264 decoder advertises (device type and pixel format).</summary>
    public static List<string> H264HwConfigs()
    {
        var list = new List<string>();
        AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* cfg = ffmpeg.avcodec_get_hw_config(codec, i);
            if (cfg == null)
            {
                break;
            }

            list.Add($"{ffmpeg.av_hwdevice_get_type_name(cfg->device_type)}:{ffmpeg.av_get_pix_fmt_name(cfg->pix_fmt)}:methods=0x{cfg->methods:X}");
        }

        return list;
    }
}

/// <summary>An opened MP4 with its video and audio stream indexes.</summary>
internal sealed unsafe class Demuxer : IDisposable
{
    private AVFormatContext* _fmt;

    public Demuxer(string path)
    {
        AVFormatContext* fmt = null;
        FfmpegLib.Check(ffmpeg.avformat_open_input(&fmt, path, null, null), "avformat_open_input");
        _fmt = fmt;
        FfmpegLib.Check(ffmpeg.avformat_find_stream_info(_fmt, null), "avformat_find_stream_info");
        VideoIndex = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        AudioIndex = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, VideoIndex, null, 0);
        FfmpegLib.Check(VideoIndex, "no video stream");
        FormatName = Marshal.PtrToStringAnsi((nint)_fmt->iformat->name) ?? "?";
    }

    public string FormatName { get; }

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
        FfmpegLib.Check(ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD), "av_seek_frame");
    }

    /// <summary>Reads the next packet; returns false at end of file.</summary>
    public bool Read(AVPacket* pkt)
    {
        int ret = ffmpeg.av_read_frame(_fmt, pkt);
        if (ret == ffmpeg.AVERROR_EOF)
        {
            return false;
        }

        FfmpegLib.Check(ret, "av_read_frame");
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

/// <summary>H.264 decoder: software (frame threads) or a hwaccel with copy-back to NV12.</summary>
internal sealed unsafe class VideoDecoder : IDisposable
{
    private static AVPixelFormat s_hwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
    private static int s_getFormatCalls;
    private static int s_hwRefused;

    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVFrame* _sw;
    private AVBufferRef* _device;

    /// <summary>Extra frames in the hwaccel surface pool (AVCodecContext.extra_hw_frames); d3d12va needs some.</summary>
    public static int ExtraHwFrames { get; set; }

    public VideoDecoder(AVStream* st, string hw, int threads)
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
        if (codec == null)
        {
            throw new InvalidOperationException("no decoder for the video stream");
        }

        CodecName = Marshal.PtrToStringAnsi((nint)codec->name) ?? "?";
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        FfmpegLib.Check(ffmpeg.avcodec_parameters_to_context(_ctx, st->codecpar), "avcodec_parameters_to_context");
        _ctx->pkt_timebase = st->time_base;
        TimeBase = st->time_base;
        HwName = hw;

        if (hw != "none")
        {
            AVHWDeviceType type = ffmpeg.av_hwdevice_find_type_by_name(hw);
            if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                throw new InvalidOperationException($"hw device type '{hw}' is not in this FFmpeg build");
            }

            s_hwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
            for (int i = 0; ; i++)
            {
                AVCodecHWConfig* cfg = ffmpeg.avcodec_get_hw_config(codec, i);
                if (cfg == null)
                {
                    break;
                }

                if ((cfg->methods & 0x01) != 0 && cfg->device_type == type) // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
                {
                    s_hwPixFmt = cfg->pix_fmt;
                    break;
                }
            }

            if (s_hwPixFmt == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                throw new InvalidOperationException($"h264 has no hw config for {hw}");
            }

            AVBufferRef* dev = null;
            long t0 = Stopwatch.GetTimestamp();
            FfmpegLib.Check(ffmpeg.av_hwdevice_ctx_create(&dev, type, null, null, 0), $"av_hwdevice_ctx_create({hw})");
            DeviceCreateMs = Report.Ms(Stopwatch.GetTimestamp() - t0);
            _device = dev;
            _ctx->hw_device_ctx = ffmpeg.av_buffer_ref(_device);
            _ctx->get_format = new AVCodecContext_get_format_func
            {
                Pointer = (nint)(delegate* unmanaged[Cdecl]<AVCodecContext*, AVPixelFormat*, AVPixelFormat>)&GetFormat,
            };
            // 1 = one frame in flight; 0 = auto frame threads (several frames in flight on the GPU)
            _ctx->thread_count = threads;
            _ctx->thread_type = ffmpeg.FF_THREAD_FRAME;
            _ctx->extra_hw_frames = ExtraHwFrames;
            HwPixFmtName = ffmpeg.av_get_pix_fmt_name(s_hwPixFmt);
        }
        else
        {
            _ctx->thread_count = threads; // 0 = auto (one per logical core, FFmpeg caps it)
            _ctx->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
        }

        FfmpegLib.Check(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2(video)");
        ThreadCount = _ctx->thread_count;
        ActiveThreadType = _ctx->active_thread_type;
        _frame = ffmpeg.av_frame_alloc();
        _sw = ffmpeg.av_frame_alloc();
    }

    public string CodecName { get; }

    public string HwName { get; }

    public string? HwPixFmtName { get; }

    public double DeviceCreateMs { get; }

    public int ThreadCount { get; }

    public int ActiveThreadType { get; }

    public AVRational TimeBase { get; }

    public static int GetFormatCalls => s_getFormatCalls;

    public static int HwRefused => s_hwRefused;

    /// <summary>Copy-back time of the last hardware frame (ms), 0 in software.</summary>
    public double LastTransferMs { get; private set; }

    public int Send(AVPacket* pkt) => ffmpeg.avcodec_send_packet(_ctx, pkt);

    /// <summary>Receives one frame into a software frame (yuv420p or NV12). Returns 0, EAGAIN or EOF.</summary>
    public int Receive(out AVFrame* frame)
    {
        frame = null;
        ffmpeg.av_frame_unref(_frame);
        int ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
        if (ret < 0)
        {
            return ret;
        }

        if (_frame->hw_frames_ctx != null)
        {
            ffmpeg.av_frame_unref(_sw);
            long t0 = Stopwatch.GetTimestamp();
            FfmpegLib.Check(ffmpeg.av_hwframe_transfer_data(_sw, _frame, 0), "av_hwframe_transfer_data");
            LastTransferMs = Report.Ms(Stopwatch.GetTimestamp() - t0);
            _sw->best_effort_timestamp = _frame->best_effort_timestamp;
            _sw->pts = _frame->pts;
            frame = _sw;
        }
        else
        {
            LastTransferMs = 0;
            frame = _frame;
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static AVPixelFormat GetFormat(AVCodecContext* ctx, AVPixelFormat* fmts)
    {
        Interlocked.Increment(ref s_getFormatCalls);
        for (AVPixelFormat* p = fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == s_hwPixFmt)
            {
                return *p;
            }
        }

        Interlocked.Increment(ref s_hwRefused);
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    public void Dispose()
    {
        if (_frame != null)
        {
            AVFrame* f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }

        if (_sw != null)
        {
            AVFrame* f = _sw;
            ffmpeg.av_frame_free(&f);
            _sw = null;
        }

        if (_ctx != null)
        {
            AVCodecContext* c = _ctx;
            ffmpeg.avcodec_free_context(&c);
            _ctx = null;
        }

        if (_device != null)
        {
            AVBufferRef* d = _device;
            ffmpeg.av_buffer_unref(&d);
            _device = null;
        }
    }
}

/// <summary>AAC decoder with swresample to float32 interleaved stereo at 48 kHz (the mixer format, ARCHITECTURE 7.1).</summary>
internal sealed unsafe class AudioDecoder : IDisposable
{
    public const int OutRate = 48000;
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private SwrContext* _swr;
    private float[] _out = new float[2 * 8192];

    public AudioDecoder(AVStream* st)
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
        if (codec == null)
        {
            throw new InvalidOperationException("no decoder for the audio stream");
        }

        CodecName = Marshal.PtrToStringAnsi((nint)codec->name) ?? "?";
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        FfmpegLib.Check(ffmpeg.avcodec_parameters_to_context(_ctx, st->codecpar), "avcodec_parameters_to_context(audio)");
        _ctx->pkt_timebase = st->time_base;
        FfmpegLib.Check(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2(audio)");
        TimeBase = st->time_base;
        InRate = _ctx->sample_rate;
        InChannels = _ctx->ch_layout.nb_channels;
        InFormat = ffmpeg.av_get_sample_fmt_name(_ctx->sample_fmt);

        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_default(&outLayout, 2);
        SwrContext* swr = null;
        FfmpegLib.Check(ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, OutRate,
            &_ctx->ch_layout, _ctx->sample_fmt, _ctx->sample_rate, 0, null), "swr_alloc_set_opts2");
        _swr = swr;
        FfmpegLib.Check(ffmpeg.swr_init(_swr), "swr_init");
        _frame = ffmpeg.av_frame_alloc();
    }

    public string CodecName { get; }

    public int InRate { get; }

    public int InChannels { get; }

    public string InFormat { get; }

    public AVRational TimeBase { get; }

    public bool Resampling => InRate != OutRate;

    /// <summary>
    /// Sends one packet (null flushes) and converts every frame it yields. <paramref name="sink"/> gets the
    /// interleaved stereo samples and the stream position (in output samples) of the first one.
    /// </summary>
    public void Decode(AVPacket* pkt, Action<ReadOnlySpan<float>, long> sink)
    {
        int ret = ffmpeg.avcodec_send_packet(_ctx, pkt);
        if (ret < 0 && ret != ffmpeg.AVERROR_EOF)
        {
            FfmpegLib.Check(ret, "avcodec_send_packet(audio)");
        }

        while (true)
        {
            ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            FfmpegLib.Check(ret, "avcodec_receive_frame(audio)");
            long pts = _frame->best_effort_timestamp;
            long startSample = pts == ffmpeg.AV_NOPTS_VALUE
                ? -1
                : ffmpeg.av_rescale_q(pts, TimeBase, new AVRational { num = 1, den = OutRate });
            int maxOut = (int)ffmpeg.swr_get_out_samples(_swr, _frame->nb_samples);
            if (_out.Length < maxOut * 2)
            {
                _out = new float[maxOut * 2];
            }

            int got;
            fixed (float* o = _out)
            {
                byte* op = (byte*)o;
                got = ffmpeg.swr_convert(_swr, &op, maxOut, (byte**)&_frame->data, _frame->nb_samples);
            }

            FfmpegLib.Check(got, "swr_convert");
            sink(_out.AsSpan(0, got * 2), startSample);
            ffmpeg.av_frame_unref(_frame);
        }
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
