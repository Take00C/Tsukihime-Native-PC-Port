using System.Diagnostics;
using System.Globalization;
using Tsuki.Data.Compression;
using Tsuki.Data.Containers;
using Tsuki.Data.Images;

namespace Tsuki.Cli.Commands;

/// <summary>
/// <c>Tsuki.Cli decode-all --bench-pictures [--reps N] [--threads N]</c>: single-thread throughput of the picture decoder on the largest
/// picture of each pixel format (HEP8 <c>IMG1481</c>, RGB565+LO+A <c>IMG1481_20_00</c>, PAL8 <c>BLACK</c>), stage by stage:
/// MZX0 only, pixel conversion only (for RGB565+LO+A with every vector width), a whole <see cref="Tsuki.Engine.Contracts.Picture"/>-building
/// decode on one thread and on all, and the streaming SHA-1. Best of <c>--reps</c> (default 7). Reads the game folder only; hold
/// <c>artifacts/gpu.lock</c> while timing (lead decision 4).
/// </summary>
internal static class ImageBench
{
    private static readonly (string Archive, string Name, string Label)[] Subjects =
    [
        ("allpac1", "IMG1481.MZP", "HEP8"),
        ("allpac1", "IMG1481_20_00.MZP", "RGB565+LO+A"),
        ("allpac0", "BLACK.MZP", "PAL8"),
    ];

    /// <summary>Runs the benchmark against the game folder <paramref name="folder"/>.</summary>
    public static int Run(string folder, int reps, int threads, TextWriter output, TextWriter error)
    {
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"best of {reps}; vector widths accelerated here: 128 = {System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}, 256 = {System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}; threads for the parallel rows: {threads}"));
        output.WriteLine($"{"picture",-14} {"stage",-26} {"ms",9} {"Mpx/s",9} {"MB/s out",9}");
        foreach (var (archiveName, name, label) in Subjects)
        {
            using var archive = MrgArchive.Open(Path.Combine(folder, "image", archiveName));
            int index = archive.Find(name);
            if (index < 0)
            {
                error.WriteLine($"decode-all --bench-pictures: {archiveName} has no {name}");
                return ExitCode.Usage;
            }

            var image = ImageMzp.Open(archive.OpenContainer(index));
            var h = image.Header;
            var live = Enumerable.Range(0, h.Tiles).Where(h.IsLive).ToArray();
            long pixels = (long)live.Length * h.TilePixels;
            string title = $"{label} {name}";
            output.WriteLine($"{title}: {h.Describe()}, {live.Length} live tiles, {pixels * 4 / 1e6:F0} MB of stored RGBA");

            // stage 1: MZX0 only
            var raw = new byte[live.Length][];
            double mzx = Best(reps, () =>
            {
                for (int k = 0; k < live.Length; k++)
                {
                    raw[k] = Mzx.Decompress(image.Container.Section(1 + live[k]).Span).ToArray();
                }
            });
            Row(output, label, "MZX0 only", mzx, pixels, pixels * 4);

            // stage 2: conversion only, per vector width where it matters
            byte[] rgba = new byte[h.TileRgbaBytes];
            var levels = h.Format == PixelFormat.Rgb565LoA
                ? new[] { SimdLevel.Scalar, SimdLevel.Vector128, SimdLevel.Vector256 }
                : new[] { SimdLevel.Auto };
            foreach (var level in levels)
            {
                double conv = Best(reps, () =>
                {
                    for (int k = 0; k < live.Length; k++)
                    {
                        TileConverter.Convert(h, raw[k], rgba, level);
                    }
                });
                Row(output, label, "convert " + level, conv, pixels, pixels * 4);
            }

            // stage 3: whole decodes
            double one = Best(reps, () => image.ToPicture(1));
            Row(output, label, "ToPicture, 1 thread", one, pixels, pixels * 4);
            double many = Best(reps, () => image.ToPicture(threads));
            Row(output, label, $"ToPicture, {threads} threads", many, pixels, pixels * 4);
            using var hasher = new PictureHasher();
            long shown = (long)h.DisplayWidth * h.DisplayHeight;
            double sha = Best(reps, () => hasher.Hash(image));
            Row(output, label, "stream SHA-1, 1 thread", sha, shown, shown * 4);
        }

        return ExitCode.Ok;
    }

    private static double Best(int reps, Action action)
    {
        action();   // warm-up: JIT, page-in of the mapped file, first-touch of buffers
        double best = double.MaxValue;
        for (int i = 0; i < reps; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            action();
            best = Math.Min(best, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }

        return best;
    }

    private static void Row(TextWriter output, string picture, string stage, double ms, long pixels, long bytes) =>
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{picture,-14} {stage,-26} {ms,9:F2} {pixels / ms / 1e3,9:F0} {bytes / ms / 1e3,9:F0}"));
}
