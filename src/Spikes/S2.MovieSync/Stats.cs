// Spike S2: distributions and the JSON report (spikes.md rules: p50 / p95 / p99 / max, not means alone).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace S2.MovieSync;

/// <summary>A growable sample list with nearest-rank percentiles.</summary>
internal sealed class Series(string unit)
{
    private readonly List<double> _values = [];

    public string Unit { get; } = unit;

    public int Count => _values.Count;

    public void Add(double v) => _values.Add(v);

    public void Clear() => _values.Clear();

    public double Percentile(double p)
    {
        if (_values.Count == 0)
        {
            return double.NaN;
        }

        var sorted = _values.ToArray();
        Array.Sort(sorted);
        int rank = (int)Math.Ceiling(p / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    public double Max => _values.Count == 0 ? double.NaN : _values.Max();

    public double Min => _values.Count == 0 ? double.NaN : _values.Min();

    public double Mean => _values.Count == 0 ? double.NaN : _values.Average();

    public double MaxAbs => _values.Count == 0 ? double.NaN : _values.Max(Math.Abs);

    public int CountAbove(double limit) => _values.Count(v => v > limit);

    /// <summary>Summary object for the JSON report.</summary>
    public Dictionary<string, object> Summary()
    {
        return new Dictionary<string, object>
        {
            ["unit"] = Unit,
            ["n"] = Count,
            ["mean"] = Round(Mean),
            ["min"] = Round(Min),
            ["p50"] = Round(Percentile(50)),
            ["p95"] = Round(Percentile(95)),
            ["p99"] = Round(Percentile(99)),
            ["max"] = Round(Max),
            ["max_abs"] = Round(MaxAbs),
        };
    }

    public string Short() => string.Create(CultureInfo.InvariantCulture,
        $"n={Count} mean={Mean:F3} p50={Percentile(50):F3} p95={Percentile(95):F3} p99={Percentile(99):F3} max={Max:F3} {Unit}");

    public static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 4) : double.NaN;
}

/// <summary>Timing and report helpers.</summary>
internal static class Report
{
    public static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly JsonSerializerOptions JsonIndented = new(JsonOptions) { WriteIndented = true };

    public static double Ms(long ticks) => ticks * TicksToMs;

    public static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;

    public static void WriteJson(string path, Dictionary<string, object?> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonIndented), new UTF8Encoding(false));
    }

    public static string F(double v, int digits = 3) => v.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
