using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Tsuki.Data.Compression;

namespace Tsuki.Cli.Commands;

/// <summary>
/// <c>Tsuki.Cli bench-mzx [--pack FILE] [--rounds N]</c>: single-thread MZX0 throughput on the stream pack that
/// <c>tests/Tsuki.Tests.Data/Oracle/gen_golden_data.py</c> wrote, next to the Python oracle's numbers for the same
/// streams (ROADMAP M1 acceptance 8: at least 500 MB/s on one thread).
/// </summary>
public sealed class MzxBenchCommand : ICliCommand
{
    private static readonly string[] ClassNames = ["tile", "voice", "script", "font"];

    /// <inheritdoc />
    public string Name => "bench-mzx";

    /// <inheritdoc />
    public string Summary => "single-thread MZX0 throughput on samples/golden_data/bench/streams.bin; options: --pack FILE, --rounds N";

    /// <inheritdoc />
    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? pack = null;
        int rounds = 7;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pack" when i + 1 < args.Length:
                    pack = args[++i];
                    break;
                case "--rounds" when i + 1 < args.Length && int.TryParse(args[i + 1], out int r) && r > 0:
                    rounds = r;
                    i++;
                    break;
                default:
                    error.WriteLine($"bench-mzx: unexpected argument '{args[i]}'");
                    return ExitCode.Usage;
            }
        }

        pack ??= FindPack();
        if (pack is null || !File.Exists(pack))
        {
            error.WriteLine("bench-mzx: stream pack not found; run tests/Tsuki.Tests.Data/Oracle/gen_golden_data.py --parts bench or pass --pack FILE");
            return ExitCode.Usage;
        }

        var streams = Load(pack);
        output.WriteLine($"{streams.Count} streams from {pack}; best of {rounds} rounds, 1 thread, {Environment.ProcessorCount} logical CPUs");
        JsonElement? python = LoadPython(Path.Combine(Path.GetDirectoryName(pack)!, "python.json"));

        long totalOut = 0;
        double totalReuse = 0;
        double totalAlloc = 0;
        for (int kind = 0; kind < ClassNames.Length; kind++)
        {
            var sub = streams.Where(s => s.Kind == kind).ToList();
            if (sub.Count == 0)
            {
                continue;
            }

            long outBytes = sub.Sum(s => (long)s.Declared);
            long inBytes = sub.Sum(s => (long)s.Data.Length);
            byte[] buffer = new byte[Mzx.BufferSize(sub.Max(s => s.Declared))];

            double reuse = Best(rounds, () =>
            {
                foreach (var s in sub)
                {
                    Mzx.Decode(s.Data.Span, buffer, s.Invert);
                }
            });
            double alloc = Best(rounds, () =>
            {
                foreach (var s in sub)
                {
                    Mzx.Decompress(s.Data.Span, s.Invert);
                }
            });
            totalOut += outBytes;
            totalReuse += reuse;
            totalAlloc += alloc;
            string py = PythonText(python, ClassNames[kind]);
            output.WriteLine(
                $"  {ClassNames[kind],-7} {sub.Count,4} streams  {inBytes / 1e6,8:F1} MB in -> {outBytes / 1e6,9:F1} MB out   " +
                $"C# reuse buffer {outBytes / reuse / 1e6,7:F0} MB/s   C# allocating {outBytes / alloc / 1e6,7:F0} MB/s{py}");
        }

        output.WriteLine(
            $"  all     {streams.Count,4} streams  {totalOut / 1e6,9:F1} MB out   C# reuse buffer {totalOut / totalReuse / 1e6,7:F0} MB/s   " +
            $"C# allocating {totalOut / totalAlloc / 1e6,7:F0} MB/s");
        return ExitCode.Ok;
    }

    private static double Best(int rounds, Action work)
    {
        work(); // warm-up: JIT and page cache
        double best = double.MaxValue;
        for (int r = 0; r < rounds; r++)
        {
            long t = Stopwatch.GetTimestamp();
            work();
            best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalSeconds);
        }

        return best;
    }

    private static string PythonText(JsonElement? python, string cls)
    {
        if (python is not { } root || !root.TryGetProperty("classes", out var classes) || !classes.TryGetProperty(cls, out var c))
        {
            return string.Empty;
        }

        string text = string.Empty;
        if (c.TryGetProperty("numba", out var nb))
        {
            text += $"   python numba {nb.GetProperty("mb_per_s").GetDouble():F0} MB/s";
        }

        if (c.TryGetProperty("python", out var py))
        {
            text += $"   python pure {py.GetProperty("mb_per_s").GetDouble():F0} MB/s";
        }

        return text;
    }

    private static JsonElement? LoadPython(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.Clone();
    }

    private static List<BenchStream> Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual("MZXB"u8))
        {
            throw new InvalidOperationException($"{path} is not a stream pack");
        }

        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var list = new List<BenchStream>(count);
        int pos = 8;
        for (int i = 0; i < count; i++)
        {
            int kind = data[pos];
            bool invert = data[pos + 1] != 0;
            int declared = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 8));
            pos += 12;
            list.Add(new BenchStream(kind, invert, declared, data.AsMemory(pos, len)));
            pos += len;
        }

        return list;
    }

    private static string? FindPack()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "samples", "golden_data", "bench", "streams.bin");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private sealed record BenchStream(int Kind, bool Invert, int Declared, ReadOnlyMemory<byte> Data);
}
