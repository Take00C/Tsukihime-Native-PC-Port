using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Tsuki.Data.Containers;
using Tsuki.Engine.Contracts;
using Tsuki.Media.Audio;

namespace Tsuki.Tests.Data.Media;

/// <summary>
/// The C# decoders against the Python oracle on the real data: HE-VAG 19 / 19, ATRAC9 17,925 / 17,925 (ROADMAP M1
/// acceptance 6; PACE 7.2 #6), and the golden slice's float PCM.
/// </summary>
public sealed class DecoderOracleTests(ITestOutputHelper output)
{
    [GameDataFact]
    public void EveryHeVagEntryEqualsTheOracle()
    {
        List<TsvRow> rows = AudioGolden.Tsv("hevag/vag.tsv");
        Assert.Equal(19, rows.Count);
        using MrgArchive vag = GameImage.Open("vag");
        Assert.Equal(19, vag.Count);
        var bad = new Mismatches();
        var decoder = new MediaAudioDecoder();
        foreach (TsvRow r in rows)
        {
            int i = int.Parse(r["index"], CultureInfo.InvariantCulture);
            ReadOnlySpan<byte> data = vag.Span(i);
            VagHeader h = HeVag.ParseHeader(data);
            bad.Equal($"#{i} name", r["name"], h.Name);
            bad.Equal($"#{i} data size", r["data_size"], h.DataSize.ToString(CultureInfo.InvariantCulture));
            bad.Equal($"#{i} rate", r["rate"], h.SampleRate.ToString(CultureInfo.InvariantCulture));
            bad.Equal($"#{i} channels byte", r["channels_byte"], h.ChannelsByte.ToString(CultureInfo.InvariantCulture));
            short[] pcm = HeVag.Decode(data);
            bad.Equal($"#{i} frames", r["frames"], pcm.Length.ToString(CultureInfo.InvariantCulture));
            bad.Equal($"#{i} int16 sha1", r["sha1_i16"], Convert.ToHexStringLower(SHA1.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm.AsSpan()))));
            bad.Equal($"#{i} peak", r["peak"], (pcm.Length == 0 ? 0 : pcm.Max(s => Math.Abs((int)s))).ToString(CultureInfo.InvariantCulture));
            AudioClip clip = decoder.Decode(AudioBank.Vag, i, vag.Memory(i));
            bad.Equal($"#{i} float sha1", r["sha1_f32"], AudioGolden.Sha1(clip.Samples.Span));
            bad.Equal($"#{i} clip channels", 1, clip.Channels);
            bad.Equal($"#{i} loop", null, clip.Loop);
        }

        bad.AssertNone("HE-VAG vs tsuki.audio_hevag");
        output.WriteLine($"HE-VAG: {rows.Count} / 19 equal");
    }

    [GameDataFact]
    public void EveryAtrac9StreamEqualsTheOracle()
    {
        // the nightly part: tools/oracle/audio.py --parts atrac9 (17,925 ffmpeg CLI decodes)
        List<TsvRow> rows = AudioGolden.Tsv("atrac9/streams.tsv");
        Assert.Equal(17_925, rows.Count);
        var bad = new Mismatches();
        var sw = Stopwatch.StartNew();
        long frames = 0;
        int equal = 0;
        foreach (IGrouping<string, TsvRow> group in rows.GroupBy(r => r["bank"]))
        {
            AudioBank bank = AudioGolden.Bank(group.Key);
            using MrgArchive archive = GameImage.Open(AudioGolden.BankFile(bank));
            var decoder = new MediaAudioDecoder();
            var items = group.ToList();
            Assert.Equal(archive.Count, items.Count);
            var results = new ConcurrentBag<(int Index, string? Error, long Frames)>();
            Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, r =>
            {
                int i = int.Parse(r["index"], CultureInfo.InvariantCulture);
                try
                {
                    AudioClip clip = decoder.Decode(bank, i, AudioGolden.Stream(archive, bank, i));
                    string? err = Compare(r, clip);
                    results.Add((i, err, clip.FrameCount));
                }
                catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
                {
                    results.Add((i, e.Message, 0));
                }
            });
            foreach (var (index, error, n) in results.OrderBy(x => x.Index))
            {
                frames += n;
                if (error is null)
                {
                    equal++;
                }
                else
                {
                    bad.Add($"{group.Key} #{index}: {error}");
                }
            }
        }

        output.WriteLine($"ATRAC9: {equal} / {rows.Count} equal, {frames:N0} frames in {sw.Elapsed.TotalSeconds:F1} s");
        bad.AssertNone("ATRAC9 vs tsuki.audio.decode_at9");
        Assert.Equal(17_925, equal);
    }

    [GameDataFact]
    public void TheSliceClipsEqualTheOraclePcm()
    {
        // samples/golden/slice/audio: bgm #33, vse #598, voice #15616..#15624 (the M2 slice)
        string dir = Path.Combine(GoldenData.RepoRoot, "samples", "golden", "slice", "audio");
        string[] headers = Directory.GetFiles(dir, "*_?????.json");
        Assert.True(headers.Length >= 3, $"{headers.Length} audio headers in {dir}");
        var bad = new Mismatches();
        var decoder = new MediaAudioDecoder();
        foreach (string jsonPath in headers.Order(StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
            JsonElement root = doc.RootElement;
            AudioBank bank = AudioGolden.Bank(root.GetProperty("bank").GetString()!);
            int index = root.GetProperty("index").GetInt32();
            using MrgArchive archive = GameImage.Open(AudioGolden.BankFile(bank));
            AudioClip clip = decoder.Decode(bank, index, AudioGolden.Stream(archive, bank, index));
            string what = Path.GetFileNameWithoutExtension(jsonPath);
            bad.Equal($"{what} frames", root.GetProperty("frames").GetInt32(), clip.FrameCount);
            bad.Equal($"{what} channels", root.GetProperty("channels").GetInt32(), clip.Channels);
            bad.Equal($"{what} pcm sha1", root.GetProperty("pcm").GetProperty("sha1").GetString(), AudioGolden.Sha1(clip.Samples.Span));
            if (root.TryGetProperty("loop", out JsonElement l) && l.ValueKind == JsonValueKind.Object)
            {
                int s = l.GetProperty("start").GetInt32(), e = l.GetProperty("end").GetInt32();
                bad.Equal($"{what} loop", s == e ? null : new AudioLoop(s, e), clip.Loop);
            }
            else
            {
                bad.Equal($"{what} loop", null, clip.Loop);
            }

            output.WriteLine($"{what}: {clip.FrameCount} frames x {clip.Channels}, loop {clip.Loop}");
        }

        bad.AssertNone("slice audio vs the C# decoder");
    }

    private static string? Compare(TsvRow r, AudioClip clip)
    {
        var errs = new List<string>();
        void Eq(string what, string expected, string actual)
        {
            if (expected != actual)
            {
                errs.Add($"{what} {actual} != oracle {expected}");
            }
        }

        Eq("channels", r["channels"], clip.Channels.ToString(CultureInfo.InvariantCulture));
        Eq("rate", r["rate"], clip.SampleRate.ToString(CultureInfo.InvariantCulture));
        Eq("frames", r["frames"], clip.FrameCount.ToString(CultureInfo.InvariantCulture));
        string ls = r["loop_start"], le = r["loop_end"];
        AudioLoop? want = ls == "-" || ls == le ? null : new AudioLoop(int.Parse(ls, CultureInfo.InvariantCulture), int.Parse(le, CultureInfo.InvariantCulture));
        Eq("loop", want?.ToString() ?? "none", clip.Loop?.ToString() ?? "none");
        Eq("sha1", r["sha1"], AudioGolden.Sha1(clip.Samples.Span));
        return errs.Count == 0 ? null : string.Join("; ", errs);
    }
}
