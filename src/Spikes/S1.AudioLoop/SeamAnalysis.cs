using System.Text.Json.Nodes;

namespace S1.AudioLoop;

// Inside the namespace, so that the type Tsuki.Engine.Contracts.AudioLoop wins over the namespace S1.AudioLoop.
using Tsuki.Engine.Contracts;

// Click analysis of a loop seam with the two metrics of docs/formats/audio.md 2.4
// (scratch/audio/seam_jump.py and seam_test.py), computed in int16 units (x * 32768) like the
// originals, on the actual rendered output around the seam.
//   J  = |x[p] - (2 x[p-1] - x[p-2])| / (median |diff| over the 1000 steps before p-1, + 1),
//        max over channels: linear-extrapolation error in units of the local step.
//   HF = energy above 19.5 kHz of the 512-frame Hann window centred on the splice, relative to
//        the median of the same measure on 3 unspliced windows at end-4096/-8192/-12288.
//        ATRAC9 is band-limited (~18 kHz) here, so HF energy can only come from a discontinuity.
internal static class SeamAnalysis
{
    private const int W = 512;

    // rendered: the mixer output; seamFrame: its first frame after the seam; raw: all decoder
    // output (delay + playable + padding), used for the natural continuation after the loop end,
    // which runs past fact[0] into the never-played padding of the last superframe.
    public static JsonObject Analyse(ReadOnlySpan<float> rendered, long seamFrame, AudioClip clip, ReadOnlySpan<float> raw, int delay)
    {
        AudioLoop lp = clip.Loop ?? throw new ArgumentException("clip has no loop", nameof(clip));
        ReadOnlySpan<float> x = clip.Samples.Span;
        int p = checked((int)seamFrame);
        int s = lp.Start, e = lp.End;

        // Windows as contiguous stereo arrays: [p - 1100, p + 300) of the rendered output, the
        // same span of the natural continuation (no splice) around the loop end and around the
        // loop start.
        const int Pre = 1100, Post = 256;
        float[] spliced = rendered.Slice((p - Pre) * 2, (Pre + Post) * 2).ToArray();
        float[] naturalEnd = raw.Slice((e + 1 + delay - Pre) * 2, (Pre + Post) * 2).ToArray();
        float[] naturalStart = x.Slice((s - Pre) * 2, (Pre + Post) * 2).ToArray();
        float[] xfade = (float[])spliced.Clone();
        const int Xf = 128;                  // 2.67 ms linear crossfade, informational only
        for (int i = 0; i < Xf; i++)
        {
            float w = (i + 0.5f) / Xf;
            for (int c = 0; c < 2; c++)
            {
                float a = x[((e - Xf + 1 + i) * 2) + c];
                float b = x[((s - Xf + i) * 2) + c];
                xfade[((Pre - Xf + i) * 2) + c] = ((1 - w) * a) + (w * b);
            }
        }

        double[] refs = [Hf(x, e + 1 - 4096), Hf(x, e + 1 - 8192), Hf(x, e + 1 - 12288)];
        double hfRef = Median(refs) + 1e-3;
        var o = new JsonObject
        {
            ["seam_output_frame"] = p,
            ["last_before_seam"] = Pair(rendered, p - 1),
            ["first_after_seam"] = Pair(rendered, p),
            ["clip_loop_end_sample"] = Pair(x, e),
            ["clip_loop_start_sample"] = Pair(x, s),
            ["step_at_seam_dbfs"] = StepDb(rendered, p - 1, p),
            ["natural_step_after_end_dbfs"] = StepDb(raw, e + delay, e + delay + 1),
            ["natural_step_into_start_dbfs"] = StepDb(x, s - 1, s),
            ["J_seam"] = J(spliced, Pre),
            ["J_natural_after_end"] = J(naturalEnd, Pre),
            ["J_natural_into_start"] = J(naturalStart, Pre),
            ["J_with_128_frame_crossfade"] = J(xfade, Pre),
            ["HF_ratio_seam"] = Hf(spliced, Pre) / hfRef,
            ["HF_ratio_natural_end"] = Hf(naturalEnd, Pre) / hfRef,
            ["HF_ratio_natural_start"] = Hf(naturalStart, Pre) / hfRef,
            ["HF_ratio_with_128_frame_crossfade"] = Hf(xfade, Pre) / hfRef,
            // share of the window's energy above 19.5 kHz, in dB (the codec leaves that band empty)
            ["HF_share_db_seam"] = HfShareDb(spliced, Pre),
            ["HF_share_db_natural_end"] = HfShareDb(naturalEnd, Pre),
            ["HF_share_db_natural_start"] = HfShareDb(naturalStart, Pre),
            ["HF_share_db_with_128_frame_crossfade"] = HfShareDb(xfade, Pre),
            ["HF_share_db_reference_windows"] = new JsonArray(HfShareDb(x, e + 1 - 4096), HfShareDb(x, e + 1 - 8192), HfShareDb(x, e + 1 - 12288)),
        };
        return o;
    }

    private static JsonArray Pair(ReadOnlySpan<float> x, int frame) => [x[frame * 2], x[(frame * 2) + 1]];

    private static JsonArray StepDb(ReadOnlySpan<float> x, int a, int b)
    {
        var r = new JsonArray();
        for (int c = 0; c < 2; c++)
        {
            double d = Math.Abs((double)x[(b * 2) + c] - x[(a * 2) + c]);
            r.Add(d > 0 ? Math.Round(20 * Math.Log10(d), 2) : -999.0);
        }

        return r;
    }

    // seam_jump.py J(s, ee) with x[ee] = w[p-1], x[ee-1] = w[p-2], next = w[p]
    private static double J(float[] w, int p)
    {
        double best = 0;
        for (int c = 0; c < 2; c++)
        {
            var steps = new double[1000];
            for (int i = 0; i < 1000; i++)
            {
                int t = p - 1 - 1000 + i;
                steps[i] = Math.Abs(((double)w[((t + 1) * 2) + c] - w[(t * 2) + c]) * 32768.0);
            }

            double step = Median(steps) + 1.0;
            double pred = (2.0 * w[((p - 1) * 2) + c]) - w[((p - 2) * 2) + c];
            best = Math.Max(best, Math.Abs((w[(p * 2) + c] - pred) * 32768.0) / step);
        }

        return Math.Round(best, 3);
    }

    // seam_test.py hf_energy on the 512-frame Hann window [centre - 256, centre + 256) of x.
    private static double Hf(ReadOnlySpan<float> x, int centre) => Spectrum(x, centre, 209);

    private static double HfShareDb(ReadOnlySpan<float> x, int centre)
    {
        double hf = Spectrum(x, centre, 209), all = Spectrum(x, centre, 0);
        return all > 0 && hf > 0 ? Math.Round(10 * Math.Log10(hf / all), 2) : -999.0;
    }

    // Sum over both channels of |X_k|^2 for rfft bins k = kMin .. 256 (bin k = k * 93.75 Hz;
    // k >= 209 is above 19.5 kHz), int16 units, Hann window as numpy.hanning(512).
    private static double Spectrum(ReadOnlySpan<float> x, int centre, int kMin)
    {
        double total = 0;
        int a0 = centre - (W / 2);
        for (int c = 0; c < 2; c++)
        {
            for (int k = kMin; k <= W / 2; k++)
            {
                double re = 0, im = 0;
                for (int n = 0; n < W; n++)
                {
                    double hann = 0.5 - (0.5 * Math.Cos(2 * Math.PI * n / (W - 1)));
                    double v = x[((a0 + n) * 2) + c] * 32768.0 * hann;
                    double ang = -2 * Math.PI * k * n / W;
                    re += v * Math.Cos(ang);
                    im += v * Math.Sin(ang);
                }

                total += (re * re) + (im * im);
            }
        }

        return total;
    }

    private static double Median(double[] v)
    {
        double[] s = (double[])v.Clone();
        Array.Sort(s);
        int n = s.Length;
        return n % 2 == 1 ? s[n / 2] : 0.5 * (s[(n / 2) - 1] + s[n / 2]);
    }
}
