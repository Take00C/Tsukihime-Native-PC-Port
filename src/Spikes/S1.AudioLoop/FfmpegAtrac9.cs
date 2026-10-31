using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace S1.AudioLoop;

// ATRAC9 decoding through FFmpeg.AutoGen (libavcodec "atrac9"), fed directly with whole
// superframes from the RIFF data chunk: no demuxer, one packet = one superframe of block_align
// bytes (audio.md 7, step 1). Output is the raw decoder output, superframes * 1024 frames,
// encoder delay included, interleaved float32 (the decoder's FLTP planes are interleaved here
// by plain copies, as the ffmpeg CLI's planar -> packed float conversion does).
internal sealed unsafe class FfmpegAtrac9 : IDisposable
{
    private readonly RiffAtrac9 _h;
    private AVCodecContext* _ctx;
    private AVPacket* _pkt;
    private AVFrame* _frame;

    public static void Init(string? root = null)
    {
        ffmpeg.RootPath = root ?? AppContext.BaseDirectory;
    }

    public static string VersionInfo()
    {
        uint v = ffmpeg.avcodec_version();
        return $"FFmpeg {ffmpeg.av_version_info()}, libavcodec {v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}, license {ffmpeg.avcodec_license()}";
    }

    public FfmpegAtrac9(RiffAtrac9 h)
    {
        _h = h;
        AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_ATRAC9);
        if (codec == null)
        {
            throw new InvalidOperationException("libavcodec has no atrac9 decoder");
        }

        CodecName = Marshal.PtrToStringUTF8((IntPtr)codec->name) ?? "?";
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->sample_rate = h.SampleRate;
        _ctx->block_align = h.BlockAlign;
        ffmpeg.av_channel_layout_default(&_ctx->ch_layout, h.Channels);
        _ctx->extradata = (byte*)ffmpeg.av_mallocz((ulong)(h.Extradata.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        _ctx->extradata_size = h.Extradata.Length;
        h.Extradata.CopyTo(new Span<byte>(_ctx->extradata, h.Extradata.Length));
        Check(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2");
        _pkt = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        OutputFormat = ffmpeg.av_get_sample_fmt_name(_ctx->sample_fmt) ?? "?";
    }

    public string CodecName { get; }

    public string OutputFormat { get; }

    public int Channels => _h.Channels;

    // Decodes superframes [first, first + count) of the stream into dst (interleaved, frame 0 of
    // the output = first sample of superframe `first`). Returns frames written.
    public int Decode(ReadOnlySpan<byte> stream, int first, int count, Span<float> dst)
    {
        int written = 0;
        fixed (byte* p = stream)
        fixed (float* d = dst)
        {
            for (int s = first; s < first + count; s++)
            {
                // Not reference-counted (buf == null): avcodec_send_packet copies the bytes into a
                // padded buffer of its own, so pointing into the managed array is safe.
                _pkt->data = p + _h.DataOffset + ((long)s * _h.BlockAlign);
                _pkt->size = _h.BlockAlign;
                Check(ffmpeg.avcodec_send_packet(_ctx, _pkt), "avcodec_send_packet");
                written += Drain(d, written, dst.Length);
            }
        }

        _pkt->data = null;
        _pkt->size = 0;
        return written;
    }

    // End of stream: returns the frames the decoder still held (atrac9 has no delay: expect 0),
    // then resets the decoder so it can be fed again.
    public int Finish(Span<float> dst)
    {
        int written;
        fixed (float* d = dst)
        {
            Check(ffmpeg.avcodec_send_packet(_ctx, null), "avcodec_send_packet(flush)");
            written = Drain(d, 0, dst.Length);
        }

        ffmpeg.avcodec_flush_buffers(_ctx);
        return written;
    }

    // Decoder reset as on a loop wrap / seek (engine: sceAudiodecClearContext, audio.md 2.3).
    public void Flush() => ffmpeg.avcodec_flush_buffers(_ctx);

    private int Drain(float* d, int writtenFrames, int capacitySamples)
    {
        int frames = 0;
        while (true)
        {
            int r = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF)
            {
                return frames;
            }

            Check(r, "avcodec_receive_frame");
            if ((AVSampleFormat)_frame->format != AVSampleFormat.AV_SAMPLE_FMT_FLTP)
            {
                throw new InvalidDataException($"atrac9 produced {(AVSampleFormat)_frame->format}, expected FLTP");
            }

            int ch = _frame->ch_layout.nb_channels;
            int n = _frame->nb_samples;
            if (ch != _h.Channels)
            {
                throw new InvalidDataException($"atrac9 produced {ch} channels, header says {_h.Channels}");
            }

            long baseIndex = (long)(writtenFrames + frames) * ch;
            if (baseIndex + ((long)n * ch) > capacitySamples)
            {
                throw new InvalidOperationException("output buffer too small");
            }

            for (int c = 0; c < ch; c++)
            {
                float* src = (float*)_frame->extended_data[c];
                float* o = d + baseIndex + c;
                for (int i = 0; i < n; i++)
                {
                    o[i * ch] = src[i];
                }
            }

            frames += n;
            ffmpeg.av_frame_unref(_frame);
        }
    }

    private static void Check(int r, string what)
    {
        if (r >= 0)
        {
            return;
        }

        const int Size = 256;
        byte* buf = stackalloc byte[Size];
        ffmpeg.av_strerror(r, buf, Size);
        throw new InvalidOperationException($"{what} failed: {Marshal.PtrToStringUTF8((IntPtr)buf)} ({r})");
    }

    public void Dispose()
    {
        if (_frame != null)
        {
            AVFrame* f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }

        if (_pkt != null)
        {
            AVPacket* p = _pkt;
            ffmpeg.av_packet_free(&p);
            _pkt = null;
        }

        if (_ctx != null)
        {
            AVCodecContext* c = _ctx;
            ffmpeg.avcodec_free_context(&c);
            _ctx = null;
        }
    }
}
