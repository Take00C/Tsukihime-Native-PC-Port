// ATRAC9 through FFmpeg.AutoGen's libavcodec "atrac9", fed with whole superframes from the RIFF data chunk:
// no demuxer, no AVIO, 12 bytes of extradata, one non-refcounted packet per superframe (ARCHITECTURE 7.1;
// S1.md 1). The FLTP planes are interleaved by plain copies, as the ffmpeg CLI's planar-to-packed float
// conversion does, so the float output is bit-identical to tools/tsuki/audio.py decode_at9 (S1.md 3 #4).
// Lifted from spike S1 FfmpegAtrac9.
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Tsuki.Media.Ffmpeg;

namespace Tsuki.Media.Audio;

/// <summary>One libavcodec ATRAC9 decoder context for one stream. Not thread-safe; one per worker.</summary>
public sealed unsafe class Atrac9Decoder : IDisposable
{
    private readonly RiffAtrac9 _h;
    private AVCodecContext* _ctx;
    private AVPacket* _pkt;
    private AVFrame* _frame;

    /// <summary>Opens a decoder for the stream described by <paramref name="header"/>. Loads FFmpeg from the app folder if needed.</summary>
    public Atrac9Decoder(RiffAtrac9 header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (!FfmpegLibrary.IsLoaded)
        {
            FfmpegLibrary.Load();
        }

        _h = header;
        AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_ATRAC9);
        if (codec == null)
        {
            throw new InvalidOperationException("libavcodec has no atrac9 decoder");
        }

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null)
        {
            throw new InvalidOperationException("avcodec_alloc_context3 failed");
        }

        try
        {
            _ctx->sample_rate = header.SampleRate;
            _ctx->block_align = header.BlockAlign;
            ffmpeg.av_channel_layout_default(&_ctx->ch_layout, header.Channels);
            ReadOnlySpan<byte> extra = header.Extradata.Span;
            _ctx->extradata = (byte*)ffmpeg.av_mallocz((ulong)(extra.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
            _ctx->extradata_size = extra.Length;
            extra.CopyTo(new Span<byte>(_ctx->extradata, extra.Length));
            Check(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2");
            _pkt = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            if (_pkt == null || _frame == null)
            {
                throw new InvalidOperationException("av_packet_alloc / av_frame_alloc failed");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decodes superframes [<paramref name="first"/>, <paramref name="first"/> + <paramref name="count"/>) of
    /// <paramref name="stream"/> (the whole RIFF) into <paramref name="dst"/>, interleaved; frame 0 of the output
    /// is the first sample of superframe <paramref name="first"/>. Returns the frames written.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> stream, int first, int count, Span<float> dst)
    {
        ObjectDisposedException.ThrowIf(_ctx == null, this);
        if (first < 0 || count < 0 || (long)first + count > _h.Superframes)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"superframes [{first}, {(long)first + count}) outside [0, {_h.Superframes})");
        }

        // The header says where the superframes are; the bytes handed in must hold them. Native code copies BlockAlign bytes from every packet pointer, so a
        // header that does not belong to this stream (or a stream cut short) must never reach the pointer arithmetic below.
        long end = (long)_h.DataOffset + (((long)first + count) * _h.BlockAlign);
        if (_h.DataOffset < 0 || _h.BlockAlign <= 0 || end > stream.Length)
        {
            throw new ArgumentException($"superframes [{first}, {(long)first + count}) end at byte {end}, but the stream has {stream.Length} bytes", nameof(stream));
        }

        int written = 0;
        fixed (byte* p = stream)
        fixed (float* d = dst)
        {
            for (int s = first; s < first + count; s++)
            {
                // Not reference-counted (buf == null): avcodec_send_packet copies the bytes into its own padded buffer.
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

    /// <summary>End of stream: the frames the decoder still held (atrac9 holds none), then a reset.</summary>
    public int Finish(Span<float> dst)
    {
        ObjectDisposedException.ThrowIf(_ctx == null, this);
        int written;
        fixed (float* d = dst)
        {
            Check(ffmpeg.avcodec_send_packet(_ctx, null), "avcodec_send_packet(flush)");
            written = Drain(d, 0, dst.Length);
        }

        ffmpeg.avcodec_flush_buffers(_ctx);
        return written;
    }

    /// <summary>Decoder reset, as on a loop wrap or a seek (engine: sceAudiodecClearContext, audio.md 2.3).</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_ctx == null, this);
        ffmpeg.avcodec_flush_buffers(_ctx);
    }

    /// <summary>Decodes every superframe of <paramref name="stream"/>: <see cref="RiffAtrac9.DecodedFrames"/> frames, delay included.</summary>
    public static float[] DecodeAll(RiffAtrac9 header, ReadOnlySpan<byte> stream)
    {
        ArgumentNullException.ThrowIfNull(header);
        float[] decoded = new float[(long)header.DecodedFrames * header.Channels];
        using var dec = new Atrac9Decoder(header);
        int n = dec.Decode(stream, 0, header.Superframes, decoded);
        n += dec.Finish(decoded.AsSpan(n * header.Channels));
        if (n != header.DecodedFrames)
        {
            throw new InvalidDataException($"atrac9 produced {n} frames, expected superframes * {header.SamplesPerBlock} = {header.DecodedFrames}");
        }

        return decoded;
    }

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
            try
            {
                if ((AVSampleFormat)_frame->format != AVSampleFormat.AV_SAMPLE_FMT_FLTP)
                {
                    throw new InvalidDataException($"atrac9 produced {(AVSampleFormat)_frame->format}, expected FLTP");
                }

                int ch = _frame->ch_layout.nb_channels;
                int n = _frame->nb_samples;
                if (ch != _h.Channels)
                {
                    throw new InvalidDataException($"atrac9 produced {ch} channels, the header says {_h.Channels}");
                }

                long baseIndex = (long)(writtenFrames + frames) * ch;
                if (baseIndex + ((long)n * ch) > capacitySamples)
                {
                    throw new ArgumentException("output buffer too small");
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
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
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
        throw new InvalidDataException($"{what} failed: {Marshal.PtrToStringUTF8((nint)buf)} ({r})");
    }

    /// <inheritdoc />
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
