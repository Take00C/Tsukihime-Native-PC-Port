using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Tsuki.Data.Compression;
using Tsuki.Data.Containers;

namespace Tsuki.Cli.Commands;

/// <summary>
/// <c>Tsuki.Cli decode-all [--game DIR] [--only NAMES] [--threads N] [--hashes FILE]</c>: decodes every MZX0 stream
/// of the image archives, the voice archives and the standalone files on all cores, and checks what the format
/// documents promise for each: the stream ends exactly at the end of its section (containers.md 5.1) and, for
/// voices, the RIFF size equals the declared size and the HED4 sector count is <c>exact / 0x800 + 1</c>
/// (containers.md 3.1, 6). With <c>--hashes</c> it also writes one SHA-256 per stream for a comparison with the
/// Python oracle. Reads the game folder only.
/// <para>
/// <c>--pictures</c> switches to the all-pictures run (ROADMAP M1 acceptance 4): every image MZP is decoded to its displayed RGBA8
/// and hashed with SHA-1 (<see cref="PictureSweep"/>). <c>--picture-hashes FILE</c> writes the result in the format of the
/// oracle's <c>pictures.tsv</c>; <c>--expect FILE</c> compares it with that file (<c>--complete</c>: the sets of pictures must be equal).
/// </para>
/// </summary>
public sealed class DecodeAllCommand : ICliCommand
{
    private static readonly string[] Triplets = ["allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4", "parts"];
    private static readonly string[] Standalone = ["allscr.mrg", "ft_en.mrg", "ft_ja.mrg", "ft_ko.mrg", "ft_zc.mrg", "ft_zt.mrg"];

    /// <inheritdoc />
    public string Name => "decode-all";

    /// <inheritdoc />
    public string Summary => "decode every MZX0 stream and check sizes; --pictures: every picture's SHA-1; --bench-pictures: decoder throughput per stage; options: --game DIR, --only a,b, --threads N, --hashes FILE, --picture-hashes FILE, --expect FILE, --complete";

    /// <inheritdoc />
    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? game = null;
        string? hashes = null;
        bool pictures = false;
        bool benchPictures = false;
        int reps = 7;
        bool complete = false;
        string? pictureHashes = null;
        string? expect = null;
        HashSet<string>? only = null;
        int threads = Environment.ProcessorCount;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--game" when i + 1 < args.Length:
                    game = args[++i];
                    break;
                case "--only" when i + 1 < args.Length:
                    only = new HashSet<string>(args[++i].Split(','), StringComparer.OrdinalIgnoreCase);
                    break;
                case "--threads" when i + 1 < args.Length && int.TryParse(args[i + 1], out int t) && t > 0:
                    threads = t;
                    i++;
                    break;
                case "--hashes" when i + 1 < args.Length:
                    hashes = args[++i];
                    break;
                case "--pictures":
                    pictures = true;
                    break;
                case "--complete":
                    complete = true;
                    break;
                case "--bench-pictures":
                    benchPictures = true;
                    break;
                case "--reps" when i + 1 < args.Length && int.TryParse(args[i + 1], out int r) && r > 0:
                    reps = r;
                    i++;
                    break;
                case "--picture-hashes" when i + 1 < args.Length:
                    pictureHashes = args[++i];
                    break;
                case "--expect" when i + 1 < args.Length:
                    expect = args[++i];
                    break;
                default:
                    error.WriteLine($"decode-all: unexpected argument '{args[i]}'");
                    return ExitCode.Usage;
            }
        }

        if (!pictures && (complete || pictureHashes is not null || expect is not null))
        {
            error.WriteLine("decode-all: --picture-hashes, --expect and --complete need --pictures");
            return ExitCode.Usage;
        }

        string? folder = GameFolderLocator.Resolve(game);
        if (folder is null || !Directory.Exists(Path.Combine(folder, "image")))
        {
            error.WriteLine("decode-all: no game folder; pass --game <dir> or set TSUKI_GAME_DIR");
            return ExitCode.Usage;
        }

        string image = Path.Combine(folder, "image");
        if (benchPictures)
        {
            return ImageBench.Run(folder, reps, threads, output, error);
        }

        if (pictures)
        {
            Dictionary<string, PictureSweep.Expected>? expected;
            try
            {
                expected = expect is null ? null : PictureSweep.LoadExpected(expect);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or FormatException)
            {
                error.WriteLine($"decode-all: {e.Message}");
                return ExitCode.Usage;
            }

            using var tsv = pictureHashes is null ? null : new StreamWriter(pictureHashes, false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
            long failed = new PictureSweep(image, threads, expected, complete, only, output).Run(tsv);
            return failed == 0 ? ExitCode.Ok : ExitCode.Failed;
        }

        using var hashWriter = hashes is null ? null : new StreamWriter(hashes, false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads };
        long failures = 0;
        var total = new Tally();
        var sw = Stopwatch.StartNew();
        output.WriteLine($"{"source",-16} {"streams",10} {"compressed",15} {"decompressed",16} {"max over",8} {"fail",6} {"seconds",8} {"MB/s out",9}");

        void Report(string name, Tally t, double seconds)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name,-16} {t.Streams,10:N0} {t.Compressed,15:N0} {t.Decompressed,16:N0} {t.MaxOvershoot,8} {t.Failures,6} {seconds,8:F1} {t.Decompressed / seconds / 1e6,9:F0}"));
            foreach (string s in t.Samples)
            {
                output.WriteLine("    " + s);
            }

            failures += t.Failures;
            total.Merge(t);
        }

        foreach (string name in Triplets)
        {
            if (only is not null && !only.Contains(name))
            {
                continue;
            }

            var t0 = Stopwatch.StartNew();
            using var archive = MrgArchive.Open(Path.Combine(image, name));
            var tally = new Tally();
            Parallel.ForEach(
                Partitioner.Create(0, archive.Count, Math.Max(1, archive.Count / (threads * 8))),
                options,
                () => new Worker(name, hashWriter),
                (range, _, w) =>
                {
                    for (int e = range.Item1; e < range.Item2; e++)
                    {
                        var container = archive.OpenContainer(e);
                        w.Container(container, $"{name}#{e}", string.Empty, invert: false);
                    }

                    return w;
                },
                w => tally.Merge(w.Tally));
            Report(name, tally, t0.Elapsed.TotalSeconds);
        }

        foreach (string name in new[] { "voice", "voice2" })
        {
            if (only is not null && !only.Contains(name))
            {
                continue;
            }

            var t0 = Stopwatch.StartNew();
            using var archive = MrgArchive.Open(Path.Combine(image, name));
            var tally = new Tally();
            Parallel.ForEach(
                Partitioner.Create(0, archive.Count, Math.Max(1, archive.Count / (threads * 8))),
                options,
                () => new Worker(name, hashWriter),
                (range, _, w) =>
                {
                    for (int e = range.Item1; e < range.Item2; e++)
                    {
                        w.Voice(archive.Entries[e], archive.SectorMemory(e).Span);
                    }

                    return w;
                },
                w => tally.Merge(w.Tally));
            Report(name, tally, t0.Elapsed.TotalSeconds);
        }

        foreach (string file in Standalone)
        {
            if (only is not null && !only.Contains(file) && !only.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                continue;
            }

            var t0 = Stopwatch.StartNew();
            using var mrg = StandaloneMrg.Open(Path.Combine(image, file));
            var tally = new Tally();
            var w = new Worker(file, hashWriter);
            bool scripts = file == "allscr.mrg";
            for (int s = 0; s < mrg.Container.Count; s++)
            {
                if (mrg.Container.IsNested(s))
                {
                    w.Container(mrg.Container.Child(s), $"{file}#{s}", string.Empty, invert: false);
                }
                else if (Mzx.IsMzx(mrg.Container.Head(s, 8)))
                {
                    w.Stream(mrg.Container.Section(s).Span, $"{file}#{s}", invert: scripts && s >= 3);
                }
            }

            tally.Merge(w.Tally);
            Report(file, tally, t0.Elapsed.TotalSeconds);
        }

        hashWriter?.Flush();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{"total",-16} {total.Streams,10:N0} {total.Compressed,15:N0} {total.Decompressed,16:N0} {total.MaxOvershoot,8} {total.Failures,6} {sw.Elapsed.TotalSeconds,8:F1} {total.Decompressed / sw.Elapsed.TotalSeconds / 1e6,9:F0}"));
        return failures == 0 ? ExitCode.Ok : ExitCode.Failed;
    }

    private sealed class Tally
    {
        public long Streams;
        public long Compressed;
        public long Decompressed;
        public int MaxOvershoot;
        public long Failures;
        public List<string> Samples { get; } = [];

        public void Merge(Tally o)
        {
            Streams += o.Streams;
            Compressed += o.Compressed;
            Decompressed += o.Decompressed;
            MaxOvershoot = Math.Max(MaxOvershoot, o.MaxOvershoot);
            Failures += o.Failures;
            foreach (string s in o.Samples.Take(Math.Max(0, 5 - Samples.Count)))
            {
                Samples.Add(s);
            }
        }

        public void Fail(string message)
        {
            Failures++;
            if (Samples.Count < 5)
            {
                Samples.Add(message);
            }
        }
    }

    private sealed class Worker(string source, StreamWriter? hashes)
    {
        private byte[] _buffer = new byte[1 << 20];

        public Tally Tally { get; } = new();

        public void Container(MzpContainer c, string label, string path, bool invert)
        {
            for (int i = 0; i < c.Count; i++)
            {
                string p = path.Length == 0 ? i.ToString(CultureInfo.InvariantCulture) : path + "/" + i.ToString(CultureInfo.InvariantCulture);
                if (c.IsNested(i))
                {
                    Container(c.Child(i), label, p, invert);
                }
                else if (Mzx.IsMzx(c.Head(i, 8)))
                {
                    Stream(c.Section(i).Span, $"{label}/{p}", invert);
                }
            }
        }

        public void Stream(ReadOnlySpan<byte> section, string label, bool invert)
        {
            try
            {
                int declared = Mzx.DeclaredSize(section);
                if (_buffer.Length < Mzx.BufferSize(declared))
                {
                    _buffer = new byte[Mzx.BufferSize(declared)];
                }

                var stats = Mzx.Decode(section, _buffer, invert);
                Tally.Streams++;
                Tally.Compressed += section.Length;
                Tally.Decompressed += declared;
                Tally.MaxOvershoot = Math.Max(Tally.MaxOvershoot, stats.Overshoot);
                if (stats.Consumed != section.Length)
                {
                    Tally.Fail($"{label}: consumed {stats.Consumed} of {section.Length} section bytes");
                }

                WriteHash(label, _buffer.AsSpan(0, declared));
            }
            catch (ContainerFormatException e)
            {
                Tally.Fail($"{label}: {e.Message}");
            }
        }

        public void Voice(MrgEntry entry, ReadOnlySpan<byte> span)
        {
            string label = $"{source}#{entry.Index}";
            try
            {
                var lv = Lv.Parse(span);
                var body = span[lv.BodyOffset..];
                if (!Mzx.IsMzx(body))
                {
                    Tally.Fail($"{label}: no MZX0 body");
                    return;
                }

                int declared = Mzx.DeclaredSize(body);
                if (_buffer.Length < Mzx.BufferSize(declared))
                {
                    _buffer = new byte[Mzx.BufferSize(declared)];
                }

                var stats = Mzx.Decode(body, _buffer);
                long exact = lv.BodyOffset + stats.Consumed;
                Tally.Streams++;
                Tally.Compressed += stats.Consumed;
                Tally.Decompressed += declared;
                Tally.MaxOvershoot = Math.Max(Tally.MaxOvershoot, stats.Overshoot);
                if (entry.SizeSectors != (exact / Mzp.Sector) + 1)
                {
                    Tally.Fail($"{label}: HED4 sectors {entry.SizeSectors} != exact {exact} / 0x800 + 1");
                }

                var data = _buffer.AsSpan(0, declared);
                if (declared < 12 || !data[..4].SequenceEqual("RIFF"u8) || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) + 8 != declared)
                {
                    Tally.Fail($"{label}: RIFF size does not equal the declared MZX size {declared}");
                }

                WriteHash(label + "/lv", data);
            }
            catch (ContainerFormatException e)
            {
                Tally.Fail($"{label}: {e.Message}");
            }
        }

        private void WriteHash(string label, ReadOnlySpan<byte> data)
        {
            if (hashes is null)
            {
                return;
            }

            string line = label + "\t" + Convert.ToHexStringLower(SHA256.HashData(data));
            lock (hashes)
            {
                hashes.WriteLine(line);
            }
        }
    }
}
