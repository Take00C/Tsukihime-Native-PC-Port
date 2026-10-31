// Spike S2: headless decode benchmarks (no window): decode-fps headroom and the CPU cost per frame for
// software H.264 (auto threads / 1 thread) and the hwaccels with copy-back (d3d11va, dxva2, d3d12va).
using System.Diagnostics;
using FFmpeg.AutoGen;

namespace S2.MovieSync;

internal static unsafe class Bench
{
    /// <summary>Decodes the video track as fast as possible (demux + decode + copy into a frame buffer).</summary>
    public static Dictionary<string, object?> Video(string path, string hw, int threads, double limitSeconds)
    {
        using var dmx = new Demuxer(path);
        AVStream* vs = dmx.Video;
        double tb = ffmpeg.av_q2d(vs->time_base);
        var before = Native.ThreadIds();
        long tOpen = Stopwatch.GetTimestamp();
        using var dec = new VideoDecoder(vs, hw, threads);
        double openMs = Report.Ms(Stopwatch.GetTimestamp() - tOpen);
        var created = Native.ThreadIds();
        created.ExceptWith(before);
        var frame = new VideoFrame(vs->codecpar->width, vs->codecpar->height);
        var interval = new Series("ms");
        var transfer = new Series("ms");
        var copy = new Series("ms");
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        long frames = 0;
        double lastPts = double.NaN;
        string layout = "?";
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        double cpu0 = proc.TotalProcessorTime.TotalSeconds;
        var sys0 = Native.SystemCpuSeconds();
        long t0 = Stopwatch.GetTimestamp();
        long tPrev = t0;
        bool eof = false;
        bool inputDone = false;
        string? error = null;
        while (!eof)
        {
            int ret;
            if (!inputDone)
            {
                if (!dmx.Read(pkt))
                {
                    inputDone = true;
                    ret = dec.Send(null);
                }
                else if (pkt->stream_index != dmx.VideoIndex)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }
                else
                {
                    ret = dec.Send(pkt);
                    ffmpeg.av_packet_unref(pkt);
                }

                if (ret < 0 && ret != ffmpeg.AVERROR_EOF)
                {
                    error = $"avcodec_send_packet: {FfmpegLib.Err(ret)} after {frames} frames (pts {lastPts:F3} s)";
                    break;
                }
            }

            while (true)
            {
                ret = dec.Receive(out AVFrame* f);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    break;
                }

                if (ret == ffmpeg.AVERROR_EOF)
                {
                    eof = true;
                    break;
                }

                if (ret < 0)
                {
                    error = $"avcodec_receive_frame: {FfmpegLib.Err(ret)} after {frames} frames (pts {lastPts:F3} s)";
                    eof = true;
                    break;
                }
                long tc = Stopwatch.GetTimestamp();
                frame.CopyFrom(f);
                long tEnd = Stopwatch.GetTimestamp();
                copy.Add(Report.Ms(tEnd - tc));
                if (hw != "none")
                {
                    transfer.Add(dec.LastTransferMs);
                }

                interval.Add(Report.Ms(tEnd - tPrev));
                tPrev = tEnd;
                frames++;
                lastPts = f->best_effort_timestamp * tb;
                layout = frame.Layout.ToString();
                if (limitSeconds > 0 && lastPts >= limitSeconds)
                {
                    eof = true;
                    break;
                }
            }
        }

        double wall = Report.Seconds(Stopwatch.GetTimestamp() - t0);
        proc.Refresh();
        double cpu = proc.TotalProcessorTime.TotalSeconds - cpu0;
        var sys1 = Native.SystemCpuSeconds();
        ffmpeg.av_packet_free(&pkt);
        double fps = frames / wall;
        double cpuPerFrameMs = cpu / frames * 1000;
        double source = ffmpeg.av_q2d(vs->avg_frame_rate);
        var r = new Dictionary<string, object?>
        {
            ["mode"] = "bench-video",
            ["hw"] = hw,
            ["hw_pix_fmt"] = dec.HwPixFmtName,
            ["hw_device_create_ms"] = Series.Round(dec.DeviceCreateMs),
            ["extra_hw_frames"] = VideoDecoder.ExtraHwFrames,
            ["get_format_calls"] = VideoDecoder.GetFormatCalls,
            ["hw_refused"] = VideoDecoder.HwRefused,
            ["decoder"] = dec.CodecName,
            ["threads_requested"] = threads,
            ["thread_count"] = dec.ThreadCount,
            ["active_thread_type"] = dec.ActiveThreadType,
            ["ffmpeg_threads_created"] = created.Count,
            ["open_ms"] = Series.Round(openMs),
            ["frames"] = frames,
            ["error"] = error,
            ["nb_frames"] = vs->nb_frames,
            ["last_pts_s"] = Series.Round(lastPts),
            ["output_layout"] = layout,
            ["wall_s"] = Series.Round(wall),
            ["fps"] = Series.Round(fps),
            ["realtime_factor"] = Series.Round(fps / source),
            ["process_cpu_s"] = Series.Round(cpu),
            ["cores_while_flat_out"] = Series.Round(cpu / wall),
            ["cpu_ms_per_frame"] = Series.Round(cpuPerFrameMs),
            ["cores_at_source_rate"] = Series.Round(cpuPerFrameMs * source / 1000),
            ["frame_interval_ms"] = interval.Summary(),
            ["copy_ms"] = copy.Summary(),
            ["transfer_ms"] = hw != "none" ? transfer.Summary() : null,
            ["peak_working_set_mb"] = Series.Round(Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0),
            ["machine_busy_cores_other_processes"] = Series.Round(((sys1.Busy - sys0.Busy) - cpu) / wall),
        };
        Console.WriteLine($"bench {hw} threads={threads}->{dec.ThreadCount}: {frames} frames in {wall:F2} s = {fps:F1} fps ({fps / source:F1}x), cpu {cpu:F2} s = {cpuPerFrameMs:F2} ms/frame = {cpuPerFrameMs * source / 1000:F3} cores at {source} fps" + (error != null ? " ERROR " + error : ""));
        return r;
    }

    /// <summary>Decodes the whole audio track (AAC -> float32 stereo 48 kHz) and counts samples.</summary>
    public static Dictionary<string, object?> Audio(string path)
    {
        using var dmx = new Demuxer(path);
        if (dmx.AudioIndex < 0)
        {
            return new Dictionary<string, object?> { ["mode"] = "bench-audio", ["audio"] = "none" };
        }

        AVStream* st = dmx.Audio;
        using var dec = new AudioDecoder(st);
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        long samples = 0;
        long firstSample = long.MinValue;
        double peak = 0;
        double sumSq = 0;
        using var proc = Process.GetCurrentProcess();
        double cpu0 = proc.TotalProcessorTime.TotalSeconds;
        long t0 = Stopwatch.GetTimestamp();
        void Sink(ReadOnlySpan<float> s, long start)
        {
            if (firstSample == long.MinValue)
            {
                firstSample = start;
            }

            samples += s.Length / 2;
            foreach (float v in s)
            {
                double a = Math.Abs(v);
                if (a > peak)
                {
                    peak = a;
                }

                sumSq += (double)v * v;
            }
        }

        while (dmx.Read(pkt))
        {
            if (pkt->stream_index == dmx.AudioIndex)
            {
                dec.Decode(pkt, Sink);
            }

            ffmpeg.av_packet_unref(pkt);
        }

        dec.Decode(null, Sink);
        double wall = Report.Seconds(Stopwatch.GetTimestamp() - t0);
        proc.Refresh();
        double cpu = proc.TotalProcessorTime.TotalSeconds - cpu0;
        ffmpeg.av_packet_free(&pkt);
        double duration = st->duration * ffmpeg.av_q2d(st->time_base);
        var r = new Dictionary<string, object?>
        {
            ["mode"] = "bench-audio",
            ["decoder"] = dec.CodecName,
            ["in_rate"] = dec.InRate,
            ["in_channels"] = dec.InChannels,
            ["in_format"] = dec.InFormat,
            ["resampling"] = dec.Resampling,
            ["samples_per_channel"] = samples,
            ["first_sample_pts"] = firstSample,
            ["stream_duration_s"] = Series.Round(duration),
            ["stream_duration_samples"] = (long)Math.Round(duration * AudioDecoder.OutRate),
            ["decoded_duration_s"] = Series.Round(samples / (double)AudioDecoder.OutRate),
            ["peak_abs"] = Series.Round(peak),
            ["peak_dbfs"] = Series.Round(20 * Math.Log10(peak)),
            ["rms_dbfs"] = Series.Round(10 * Math.Log10(sumSq / Math.Max(1, samples * 2))),
            ["wall_s"] = Series.Round(wall),
            ["cpu_s"] = Series.Round(cpu),
            ["realtime_factor"] = Series.Round(samples / (double)AudioDecoder.OutRate / wall),
        };
        Console.WriteLine($"bench audio: {samples} samples/ch ({samples / 48000.0:F4} s) vs stream {duration:F6} s; wall {wall:F2} s, cpu {cpu:F2} s; peak {20 * Math.Log10(peak):F2} dBFS");
        return r;
    }
}
