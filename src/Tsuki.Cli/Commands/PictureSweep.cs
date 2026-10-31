using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Tsuki.Data.Containers;
using Tsuki.Data.Images;

namespace Tsuki.Cli.Commands;

/// <summary>
/// The all-pictures run of <c>Tsuki.Cli decode-all --pictures</c> (ROADMAP M1 acceptance 4, nightly): every image MZP of the
/// image archives and of the standalone files allscr.mrg and ft_*.mrg is decoded and the SHA-1 of its displayed RGBA8 is
/// recorded in the format of <c>oracle.image</c>'s <c>pictures.tsv</c>. With an expectation file (the oracle's TSV) every
/// listed picture must give the listed SHA-1 and header facts.
/// </summary>
internal sealed class PictureSweep
{
    private static readonly string[] Triplets = ["allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4", "parts"];
    private static readonly string[] Standalone = ["allscr.mrg", "ft_en.mrg", "ft_ja.mrg", "ft_ko.mrg", "ft_zc.mrg", "ft_zt.mrg"];

    /// <summary>The columns of the oracle's TSV.</summary>
    internal const string TsvHeader =
        "label\tname\tformat\twidth\theight\tshown_width\tshown_height\ttile_w\ttile_h\ttiles_x\ttiles_y\tattr\tlive_tiles\tsha1\tstratum";

    private readonly string _imageFolder;
    private readonly int _threads;
    private readonly Dictionary<string, Expected>? _expect;
    private readonly bool _complete;
    private readonly HashSet<string>? _sources;
    private readonly TextWriter _output;

    /// <summary>Only the pictures listed in the expectation are decoded unless the run has to be complete.</summary>
    private readonly HashSet<string>? _labels;

    internal PictureSweep(string imageFolder, int threads, Dictionary<string, Expected>? expect, bool complete, HashSet<string>? sources, TextWriter output)
    {
        _imageFolder = imageFolder;
        _threads = threads;
        _expect = expect;
        _complete = complete;
        _sources = sources;
        _output = output;
        _labels = expect is not null && !complete ? new HashSet<string>(expect.Keys, StringComparer.Ordinal) : null;
    }

    private bool Wants(string source) =>
        _sources is null || _sources.Contains(source) || _sources.Contains(Path.GetFileNameWithoutExtension(source));

    /// <summary>What the oracle recorded for one picture.</summary>
    internal sealed record Expected(string Name, string Format, int Width, int Height, int ShownWidth, int ShownHeight, int TileW, int TileH, int TilesX, int TilesY, int Attr, int LiveTiles, string Sha1);

    /// <summary>What this run found for one picture.</summary>
    internal sealed record Found(int SourceIndex, int Entry, ImmutableArray<int> Path, string Label, string Name, ImageHeader Header, int LiveTiles, string Sha1);

    internal static Dictionary<string, Expected> LoadExpected(string path)
    {
        var map = new Dictionary<string, Expected>(StringComparer.Ordinal);
        using var reader = new StreamReader(path, Encoding.UTF8);
        string? header = reader.ReadLine();
        if (header is null || !header.StartsWith("label\t", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{path}: not an oracle pictures.tsv (no header)");
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            string[] f = line.Split('\t');
            if (f.Length < 14)
            {
                throw new InvalidOperationException($"{path}: row with {f.Length} columns: {line}");
            }

            int I(int i) => int.Parse(f[i], CultureInfo.InvariantCulture);
            map[f[0]] = new Expected(f[1], f[2], I(3), I(4), I(5), I(6), I(7), I(8), I(9), I(10), I(11), I(12), f[13]);
        }

        return map;
    }

    internal static string FormatName(PixelFormat f) => f switch
    {
        PixelFormat.Pal4 => "pal4",
        PixelFormat.Pal8 => "pal8",
        PixelFormat.Hep8 => "hep8",
        _ => "rgb565lo+a",
    };

    /// <summary>Runs the sweep; returns the number of failures (decode errors, differences, listed but missing pictures).</summary>
    internal long Run(StreamWriter? tsv)
    {
        var all = new List<Found>();
        long failures = 0;
        long totalPixels = 0;
        long totalTiles = 0;
        long totalBytes = 0;
        var sw = Stopwatch.StartNew();
        _output.WriteLine($"{"source",-12} {"pictures",9} {"tiles",10} {"RGBA MB",10} {"fail",6} {"seconds",8} {"Mpx/s",8}");

        var errors = new ConcurrentQueue<string>();
        void Report(string name, List<Found> found, long tiles, long pixels, long bytes, long fail, double seconds)
        {
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name,-12} {found.Count,9:N0} {tiles,10:N0} {bytes / 1e6,10:N0} {fail,6} {seconds,8:F1} {pixels / seconds / 1e6,8:F0}"));
            all.AddRange(found);
            failures += fail;
            totalPixels += pixels;
            totalTiles += tiles;
            totalBytes += bytes;
        }

        for (int s = 0; s < Triplets.Length; s++)
        {
            string name = Triplets[s];
            if (!Wants(name))
            {
                continue;
            }

            var t0 = Stopwatch.StartNew();
            using var archive = MrgArchive.Open(Path.Combine(_imageFolder, name));
            var found = new ConcurrentBag<Found>();
            long fail = 0, tiles = 0, pixels = 0, bytes = 0;
            int sourceIndex = s;
            Parallel.ForEach(
                Partitioner.Create(0, archive.Count, Math.Max(1, archive.Count / (_threads * 16))),
                new ParallelOptions { MaxDegreeOfParallelism = _threads },
                () => new PictureHasher(),
                (range, _, hasher) =>
                {
                    for (int e = range.Item1; e < range.Item2; e++)
                    {
                        string? entryName = archive.Entries[e].Name;
                        Process(archive.OpenContainer(e), name, sourceIndex, e, entryName ?? "-", hasher, found, errors, ref fail, ref pixels);
                    }

                    return hasher;
                },
                hasher =>
                {
                    Interlocked.Add(ref tiles, hasher.TilesDecoded);
                    Interlocked.Add(ref bytes, hasher.BytesHashed);
                    hasher.Dispose();
                });
            Report(name, [.. found], tiles, pixels, bytes, fail, t0.Elapsed.TotalSeconds);
        }

        for (int s = 0; s < Standalone.Length; s++)
        {
            string name = Standalone[s];
            if (!Wants(name))
            {
                continue;
            }

            var t0 = Stopwatch.StartNew();
            using var mrg = StandaloneMrg.Open(Path.Combine(_imageFolder, name));
            var found = new ConcurrentBag<Found>();
            long fail = 0, pixels = 0;
            using var hasher = new PictureHasher();
            for (int sec = 0; sec < mrg.Container.Count; sec++)
            {
                if (mrg.Container.IsNested(sec))
                {
                    Process(mrg.Container.Child(sec), name, Triplets.Length + s, sec, "-", hasher, found, errors, ref fail, ref pixels);
                }
            }

            Report(name, [.. found], hasher.TilesDecoded, pixels, hasher.BytesHashed, fail, t0.Elapsed.TotalSeconds);
        }

        all.Sort((a, b) => Compare(a, b));
        double seconds = sw.Elapsed.TotalSeconds;
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{"total",-12} {all.Count,9:N0} {totalTiles,10:N0} {totalBytes / 1e6,10:N0} {failures,6} {seconds,8:F1} {totalPixels / seconds / 1e6,8:F0}"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"displayed RGBA hashed: {totalBytes / 1e9:F1} GB in {seconds:F1} s = {totalBytes / seconds / 1e9:F2} GB/s on {_threads} threads"));
        foreach (string line in errors.Take(10))
        {
            _output.WriteLine("    " + line);
        }

        if (tsv is not null)
        {
            tsv.Write(TsvHeader);
            tsv.Write('\n');
            foreach (var f in all)
            {
                tsv.Write(Row(f));
                tsv.Write('\n');
            }

            tsv.Flush();
        }

        if (_expect is not null)
        {
            failures += Compare(all);
        }

        return failures;
    }

    private void Process(
        MzpContainer top, string source, int sourceIndex, int entry, string name, PictureHasher hasher,
        ConcurrentBag<Found> found, ConcurrentQueue<string> errors, ref long fail, ref long pixels)
    {
        long localFail = 0;
        long localPixels = 0;
        PictureWalker.Walk(top, (path, container) =>
        {
            string label = PictureWalker.Label(source, entry, path);
            if (_labels is not null && !_labels.Contains(label))
            {
                return;
            }

            try
            {
                var image = ImageMzp.Open(container);
                string sha1 = hasher.HashHex(image);
                int live = 0;
                foreach (byte f in image.Header.TileFlags)
                {
                    if (f != 0)
                    {
                        live++;
                    }
                }

                found.Add(new Found(sourceIndex, entry, path, label, name, image.Header, live, sha1));
                localPixels += (long)image.Header.DisplayWidth * image.Header.DisplayHeight;
            }
            catch (ContainerFormatException e)
            {
                localFail++;
                errors.Enqueue($"{label}: {e.Message}");
            }
        });
        Interlocked.Add(ref fail, localFail);
        Interlocked.Add(ref pixels, localPixels);
    }

    private static int Compare(Found a, Found b)
    {
        int c = a.SourceIndex.CompareTo(b.SourceIndex);
        if (c != 0)
        {
            return c;
        }

        c = a.Entry.CompareTo(b.Entry);
        if (c != 0)
        {
            return c;
        }

        for (int i = 0; i < Math.Min(a.Path.Length, b.Path.Length); i++)
        {
            c = a.Path[i].CompareTo(b.Path[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return a.Path.Length.CompareTo(b.Path.Length);
    }

    private static string Row(Found f)
    {
        var h = f.Header;
        return string.Create(CultureInfo.InvariantCulture,
            $"{f.Label}\t{f.Name}\t{FormatName(h.Format)}\t{h.Width}\t{h.Height}\t{h.DisplayWidth}\t{h.DisplayHeight}\t{h.TileWidth}\t{h.TileHeight}\t{h.TilesX}\t{h.TilesY}\t{h.Attr}\t{f.LiveTiles}\t{f.Sha1}\t-");
    }

    private long Compare(List<Found> all)
    {
        var expect = _expect!;
        long equal = 0, different = 0, unlisted = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<string>();
        foreach (var f in all)
        {
            seen.Add(f.Label);
            if (!expect.TryGetValue(f.Label, out var want))
            {
                unlisted++;
                continue;
            }

            var h = f.Header;
            var got = new Expected(f.Name, FormatName(h.Format), h.Width, h.Height, h.DisplayWidth, h.DisplayHeight, h.TileWidth, h.TileHeight, h.TilesX, h.TilesY, h.Attr, f.LiveTiles, f.Sha1);
            // the name column is informational (NAM name of the top-level entry)
            if (want with { Name = got.Name } == got)
            {
                equal++;
            }
            else
            {
                different++;
                if (messages.Count < 10)
                {
                    messages.Add(want.Sha1 != got.Sha1
                        ? $"{f.Label}: SHA-1 {got.Sha1} != oracle {want.Sha1}"
                        : $"{f.Label}: header facts differ: {got} != oracle {want}");
                }
            }
        }

        long missing = expect.Keys.Count(k => Wants(k[..k.IndexOf('#', StringComparison.Ordinal)]) && !seen.Contains(k));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"oracle: {expect.Count:N0} listed; equal {equal:N0}, different {different:N0}, listed but not decoded {missing:N0}, decoded but not listed {unlisted:N0}"));
        foreach (string m in messages)
        {
            _output.WriteLine("    " + m);
        }

        long fail = different + missing;
        if (_complete)
        {
            fail += unlisted;
        }

        return fail;
    }
}
