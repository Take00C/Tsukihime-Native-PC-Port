// Spike S3: distribution summaries (docs/status/spikes.md asks for p50 / p95 / p99 / max).
namespace Tsuki.Spikes.S3;

internal sealed record Summary(int N, double Mean, double P50, double P95, double P99, double Max, double Min)
{
    /// <summary>Nearest-rank percentiles over the samples.</summary>
    public static Summary Of(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0)
            return new Summary(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        var s = xs.OrderBy(x => x).ToArray();
        double Rank(double p) => s[Math.Clamp((int)Math.Ceiling(p / 100.0 * s.Length) - 1, 0, s.Length - 1)];
        return new Summary(s.Length, s.Average(), Rank(50), Rank(95), Rank(99), s[^1], s[0]);
    }

    public override string ToString() =>
        FormattableString.Invariant($"mean {Mean:F3}  p50 {P50:F3}  p95 {P95:F3}  p99 {P99:F3}  max {Max:F3}  (n {N})");
}
