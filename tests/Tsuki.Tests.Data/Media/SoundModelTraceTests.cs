using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Sound;

namespace Tsuki.Tests.Data.Media;

/// <summary>
/// The C# <see cref="SoundModel"/> against the Python reference model (package E2 / K1c): the SEPL trace of the slice that
/// package H3a exported (the 8,000 ms fade-in to 30 %, M2 check 7) and the whole audio corpus of the 549 scripts (22,037 statements
/// replayed with random frame updates), the silent states, two other configs, the voice gate and the BGM ducking
/// (<c>tools/oracle/audio.py</c> part <c>sound</c>). Every step compares the sound library calls, the return value, the 26 channel
/// records and the integer state.
/// </summary>
public sealed class SoundModelTraceTests(ITestOutputHelper output)
{
    private const string SliceSepl = "models/sound_sepl.json";

    /// <summary>Reads a file of the H3a slice export after checking its SHA-1 against the slice manifest.</summary>
    private static byte[] ReadSlice(string relative)
    {
        string dir = Path.Combine(GoldenData.RepoRoot, "samples", "golden", "slice");
        byte[] data = File.ReadAllBytes(Path.Combine(dir, relative));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "manifest.json")));
        JsonElement record = manifest.RootElement.GetProperty("files").EnumerateArray().First(f => f.GetProperty("path").GetString() == relative);
        Assert.Equal(record.GetProperty("size").GetInt64(), data.Length);
        Assert.Equal(record.GetProperty("sha1").GetString(), Convert.ToHexStringLower(SHA1.HashData(data)));
        return data;
    }

    [GameDataFact]
    public void TheSlicePlayOfMoonSe03028FadesInToThirtyPercentInFourHundredEightyFrames()
    {
        using JsonDocument doc = JsonDocument.Parse(ReadSlice(SliceSepl));
        JsonElement root = doc.RootElement;
        string statement = root.GetProperty("statement").GetString()!;
        int ch = root.GetProperty("channel").GetInt32();
        var model = new SoundModel(seLookup: (n, f) => n == "MOON_SE03028" && f == 3 ? 598 : null);
        Assert.Equal(root.GetProperty("wait").GetInt32(), model.Run(statement));

        JsonElement after = root.GetProperty("after_statement");
        Assert.Equal(Calls(after.GetProperty("calls")), TakeStrings(model));
        AssertRecord(after.GetProperty("record"), model.Record(6 + ch));

        int reached = 0;
        foreach (JsonElement f in root.GetProperty("frames").EnumerateArray())
        {
            int n = f.GetProperty("frame").GetInt32();
            model.Update(1);
            SoundChannelRecord r = model.Record(6 + ch);
            Assert.True(Bits(f.GetProperty("vol")) == BitConverter.SingleToInt32Bits(r.Volume), $"frame {n}: volume {r.Volume}");
            Assert.Equal(f.GetProperty("state").GetInt32(), (int)r.State);
            Assert.Equal(Calls(f.GetProperty("calls")), TakeStrings(model));
            if (reached == 0 && r.Volume == r.VolumeTarget)
            {
                reached = n;
            }
        }

        AssertRecord(root.GetProperty("final_record"), model.Record(6 + ch));
        Assert.Equal(root.GetProperty("target_reached_frame").GetInt32(), reached);
        Assert.Equal(480, reached); // 8,000 ms at 60 frames per second
        string[] records = [.. root.GetProperty("records_after").EnumerateArray().Select(e => e.GetString()!)];
        byte[] all = model.RecordBytes();
        for (int s = 0; s < AudioStreams.Count; s++)
        {
            Assert.Equal(records[s], Convert.ToHexStringLower(all.AsSpan(s * SoundChannelRecord.Size, SoundChannelRecord.Size)));
        }

        output.WriteLine($"{statement}: volume 0 -> 30 in {reached} frames at {model.Record(6 + ch).VolumeStep}/frame; 600 frames and 26 records equal");
    }

    [GameDataFact]
    public void EveryStepOfTheReferenceTraceIsReproduced()
    {
        using JsonDocument doc = JsonDocument.Parse(AudioGolden.ReadVerified("sound/sound_trace.json"));
        JsonElement root = doc.RootElement;
        Dictionary<string, int?> lookup = [];
        foreach (JsonProperty p in root.GetProperty("se_lookup").EnumerateObject())
        {
            lookup[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetInt32();
        }

        long steps = 0, statements = 0, commands = 0;
        var byKind = new SortedDictionary<string, (int Scenarios, long Steps)>();
        foreach (JsonElement sc in root.GetProperty("scenarios").EnumerateArray())
        {
            string name = sc.GetProperty("name").GetString()!;
            var h = new TraceHarness(sc, lookup);
            JsonElement stepsJson = sc.GetProperty("steps");
            JsonElement expect = sc.GetProperty("expect");
            Assert.Equal(stepsJson.GetArrayLength(), expect.GetArrayLength());
            for (int i = 0; i < stepsJson.GetArrayLength(); i++)
            {
                JsonElement step = stepsJson[i];
                JsonElement exp = expect[i];
                (string[] calls, string ret, string digest) = h.Step(step);
                string where = $"{name} step {i} {step.GetRawText()}";
                string[] want = [.. exp[0].EnumerateArray().Select(e => e.GetString()!)];
                if (!calls.SequenceEqual(want))
                {
                    Assert.Fail($"{where}: calls differ\n  got  {string.Join(" | ", calls)}\n  want {string.Join(" | ", want)}");
                }

                if (exp[1].GetRawText() != ret)
                {
                    Assert.Fail($"{where}: return value {ret} but the reference returned {exp[1].GetRawText()}");
                }

                if (exp[2].GetString() != digest)
                {
                    Assert.Fail($"{where}: state digest {digest} but the reference has {exp[2].GetString()}\n  words: {string.Join(",", h.Model.StateWords())}");
                }

                steps++;
                commands += calls.Length;
                if (step[0].GetString() == "r")
                {
                    statements++;
                }
            }

            string kind = name.Contains(':') ? name[..name.IndexOf(':')] : name;
            byKind[kind] = byKind.TryGetValue(kind, out var k) ? (k.Scenarios + 1, k.Steps + stepsJson.GetArrayLength()) : (1, stepsJson.GetArrayLength());
        }

        foreach ((string kind, (int n, long st)) in byKind)
        {
            output.WriteLine($"{kind}: {n} scenarios, {st} steps");
        }

        output.WriteLine($"{steps} steps ({statements} statements), {commands} library calls: all equal to the reference model");
        Assert.True(statements >= 22_037, "the corpus has 22,037 statements");
    }

    private static string[] TakeStrings(SoundModel m) => [.. m.TakeCommands().Select(SoundTrace.Describe)];

    private static string[] Calls(JsonElement calls) =>
        [.. calls.EnumerateArray().Select(c => string.Join(' ', c.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt64().ToString(CultureInfo.InvariantCulture) : x.GetString())))];

    private static int Bits(JsonElement f32) => unchecked((int)f32.GetProperty("bits").GetUInt32());

    private static void AssertRecord(JsonElement j, SoundChannelRecord r)
    {
        Assert.Equal(j.GetProperty("state").GetInt32(), (int)r.State);
        Assert.Equal(j.GetProperty("kind").GetInt32(), (int)r.VolumeClass);
        Assert.Equal(j.GetProperty("w8").GetInt32(), (int)r.Started);
        Assert.Equal(j.GetProperty("idx").GetInt32(), (int)r.Entry);
        Assert.Equal(Bits(j.GetProperty("vol")), BitConverter.SingleToInt32Bits(r.Volume));
        Assert.Equal(Bits(j.GetProperty("vol_to")), BitConverter.SingleToInt32Bits(r.VolumeTarget));
        Assert.Equal(Bits(j.GetProperty("vol_rate")), BitConverter.SingleToInt32Bits(r.VolumeStep));
        Assert.Equal(j.GetProperty("char").GetInt32(), (int)r.Character);
        Assert.Equal(j.GetProperty("bytes").GetString(), Convert.ToHexStringLower(r.Bytes));
    }
}

/// <summary>The textual form of a command that the reference trace uses (<c>" ".join(call)</c> of the Python model's call tuples).</summary>
public static class SoundTrace
{
    private static string Bank(AudioBank b) => b switch
    {
        AudioBank.Bgm => "bgm",
        AudioBank.Se => "vse",
        AudioBank.Voice => "voice",
        AudioBank.Voice2 => "voice2",
        _ => "vag",
    };

    private static string Bits(float v) => ((uint)BitConverter.SingleToInt32Bits(v)).ToString(CultureInfo.InvariantCulture);

    /// <summary>One command as the reference trace writes it.</summary>
    public static string Describe(AudioCommand c)
    {
        string s = c.Stream.ToString(CultureInfo.InvariantCulture);
        return c.Op switch
        {
            AudioOp.OpenFile => $"open_file {s}",
            AudioOp.OpenHandle => $"open_handle {s}",
            AudioOp.Close => $"close {s}",
            AudioOp.Play => $"play {s} {Bank(c.Bank)} {c.Entry.ToString(CultureInfo.InvariantCulture)} {c.Arg0.ToString(CultureInfo.InvariantCulture)} {Bits(c.Value0)}",
            AudioOp.Stop => $"stop {s} {c.Arg0.ToString(CultureInfo.InvariantCulture)}",
            AudioOp.SetVolume => $"set_volume {s} {Bits(c.Value0)} {Bits(c.Value1)}",
            AudioOp.SetVolume0 => $"set_volume0 {s} {Bits(c.Value0)}",
            AudioOp.SetPan => $"set_pan {s} {Bits(c.Value0)}",
            AudioOp.SetLoop => $"set_loop {s} {c.Arg0.ToString(CultureInfo.InvariantCulture)} {c.Arg1.ToString(CultureInfo.InvariantCulture)}",
            AudioOp.SetStartSample => $"set_start_sample {s} {((uint)c.Arg0).ToString(CultureInfo.InvariantCulture)}",
            AudioOp.SetFlag6 => $"set_flag6 {s} {c.Arg0.ToString(CultureInfo.InvariantCulture)}",
            _ => throw new InvalidOperationException($"the reference model makes no {c.Op} call"),
        };
    }
}

/// <summary>Drives a <see cref="SoundModel"/> the way <c>oracle.audio._SoundHarness</c> drives the Python model.</summary>
internal sealed class TraceHarness
{
    private readonly bool _defaultPlaying;
    private readonly HashSet<int> _played = [];
    private readonly List<(int At, string Text)> _hook = [];

    public TraceHarness(JsonElement scenario, Dictionary<string, int?> lookup)
    {
        _defaultPlaying = scenario.GetProperty("default_playing").GetBoolean();
        var cfg = new SoundConfig();
        Apply(cfg, scenario.GetProperty("cfg"));
        Model = new SoundModel(cfg, (n, f) => lookup.TryGetValue($"{n}|{f}", out int? v) ? v : throw new KeyNotFoundException($"no recorded lookup for {n}|{f}"))
        {
            SeStarted = (c, id) => _hook.Add((Model!.PendingCommands.Count, $"se_caption {c} {id}")),
        };
        Model.IsPlaying = s => _defaultPlaying || _played.Contains(s) || Model.PendingCommands.Any(c => c.Op == AudioOp.Play && c.Stream == s);
        JsonElement f = scenario.GetProperty("flags");
        SetFlags(f[0].GetInt32(), f[1].GetInt32(), f[2].GetInt32(), f[3].GetInt32());
    }

    public SoundModel Model { get; }

    private void SetFlags(int ff, int scan, int skip, int vsyncs)
    {
        Model.FastForward = ff != 0;
        Model.Scan = scan != 0;
        Model.Skip = skip != 0;
        Model.Vsyncs = vsyncs;
    }

    private static void Apply(SoundConfig c, JsonElement fields)
    {
        foreach (JsonProperty p in fields.EnumerateObject())
        {
            switch (p.Name)
            {
                case "bgm": c.Bgm = p.Value.GetInt32(); break;
                case "voice": c.Voice = p.Value.GetInt32(); break;
                case "se": c.Se = p.Value.GetInt32(); break;
                case "sys_se": c.SysSe = p.Value.GetInt32(); break;
                case "duck": c.Duck = p.Value.GetInt32(); break;
                case "alt_bgm": c.AltBgm = p.Value.GetInt32(); break;
                case "se_caption": c.SeCaption = p.Value.GetInt32(); break;
                case "skip_voice": c.SkipVoice = p.Value.GetInt32(); break;
                case "voice_on2": c.VoiceOn2 = p.Value.GetInt32(); break;
                case "char_enable": c.CharEnable = p.Value.GetUInt32(); break;
                case "stop_on_advance": c.StopOnAdvance = p.Value.GetInt32(); break;
                case "movie": c.Movie = p.Value.GetInt32(); break;
                case "mute_bgm": c.MuteBgm = p.Value.GetInt32(); break;
                case "mute_se": c.MuteSe = p.Value.GetInt32(); break;
                case "mute_sys": c.MuteSys = p.Value.GetInt32(); break;
                case "char_vol":
                    int i = 0;
                    foreach (JsonElement v in p.Value.EnumerateArray())
                    {
                        c.CharVol[i++] = v.GetInt32();
                    }

                    break;
                default:
                    throw new InvalidDataException($"unknown config field {p.Name}");
            }
        }
    }

    /// <summary>Runs one step; returns the calls (the commands with the SE hook calls in place), the return value as JSON and the state digest.</summary>
    public (string[] Calls, string Ret, string Digest) Step(JsonElement step)
    {
        string ret = "null";
        switch (step[0].GetString())
        {
            case "r":
                ret = Model.Run(step[1].GetString()!).ToString(CultureInfo.InvariantCulture);
                break;
            case "u":
                Model.Update(step[1].GetInt32());
                break;
            case "apply":
                Model.ApplyConfig();
                break;
            case "cfg":
                Apply(Model.Config, step[1]);
                break;
            case "flags":
                SetFlags(step[1].GetInt32(), step[2].GetInt32(), step[3].GetInt32(), step[4].GetInt32());
                break;
            case "gate":
                ret = Model.VoiceGate() is { } v ? $"[{v.Entry},{v.Slot},{v.Gain}]" : "null";
                break;
            case "duck":
                ret = Model.DuckUpdate(step[1].ValueKind == JsonValueKind.True) is { } d ? d.ToString(CultureInfo.InvariantCulture) : "null";
                break;
            case "adv":
                ret = Model.ShouldStopVoiceOnAdvance(step[1].GetInt32()) ? "true" : "false";
                break;
            default:
                throw new InvalidDataException(step[0].GetString());
        }

        AudioCommand[] commands = Model.TakeCommands();
        foreach (AudioCommand c in commands)
        {
            if (c.Op == AudioOp.Play)
            {
                _played.Add(c.Stream);
            }
        }

        var merged = new List<string>();
        int h = 0;
        for (int i = 0; i <= commands.Length; i++)
        {
            while (h < _hook.Count && _hook[h].At == i)
            {
                merged.Add(_hook[h++].Text);
            }

            if (i < commands.Length)
            {
                merged.Add(SoundTrace.Describe(commands[i]));
            }
        }

        string[] calls = [.. merged];
        _hook.Clear();
        return (calls, ret, Digest());
    }

    private string Digest()
    {
        int[] words = Model.StateWords();
        byte[] w = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, w, 0, w.Length);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        h.AppendData(Model.RecordBytes());
        h.AppendData(w);
        return Convert.ToHexStringLower(h.GetHashAndReset())[..12];
    }
}
