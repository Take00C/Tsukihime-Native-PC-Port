using System.Diagnostics;
using System.Text.Json;
using Tsuki.Data.Containers;
using Tsuki.Engine.Contracts;
using Tsuki.Media.Audio;
using Tsuki.Media.Mixer;
using Tsuki.Platform;

namespace Tsuki.Tests.Data.Media;

/// <summary>SDL is initialised process-wide; the audio tests that open an SDL stream run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SdlAudioCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "SDL audio (E1), one test at a time";
}

/// <summary>
/// The float mixer against the oracle's reference renders (<c>tools/oracle/audio.py render_mix</c>): sample-exact
/// in any chunking, and through an SDL audio stream in pull mode with the same get-callback the device uses
/// (ARCHITECTURE 7.1 test boundary).
/// </summary>
[Collection(SdlAudioCollection.Name)]
public sealed class MixerOracleTests(ITestOutputHelper output)
{
    public static TheoryData<string, int> Chunkings => new()
    {
        { "synthetic", 0 }, { "synthetic", 1 }, { "synthetic", 7 }, { "synthetic", 480 }, { "synthetic", 1024 }, { "synthetic", 4093 },
        { "real", 0 }, { "real", 480 }, { "real", 1000 },
    };

    [GameDataTheory]
    [MemberData(nameof(Chunkings))]
    public void TheMixerRendersTheOracleBits(string scenario, int chunk)
    {
        var s = Scenario.Load(scenario);
        var mixer = s.NewMixer();
        float[] got = new float[s.Frames * 2];
        var sw = Stopwatch.StartNew();
        if (chunk == 0)
        {
            mixer.Render(got);
        }
        else
        {
            for (int f = 0; f < s.Frames; f += chunk)
            {
                mixer.Render(got.AsSpan(f * 2, Math.Min(chunk, s.Frames - f) * 2));
            }
        }

        output.WriteLine($"{scenario}, chunk {chunk}: {s.Frames} frames in {sw.Elapsed.TotalMilliseconds:F1} ms");
        AssertBits(s.Expected, got, $"{scenario} chunk {chunk}");
    }

    [GameDataTheory]
    [InlineData("synthetic")]
    [InlineData("real")]
    public void TheSdlPullStreamDeliversTheOracleBits(string scenario)
    {
        var s = Scenario.Load(scenario);
        var mixer = s.NewMixer();
        float[] got = new float[s.Frames * 2];
        long callbacks;
        using (var stream = AudioOutput.CreatePullOnly(mixer.Render))
        {
            int f = 0;
            while (f < s.Frames)
            {
                int n = Math.Min(480, s.Frames - f); // the WASAPI period (S1.md 4)
                int read = stream.Pull(got.AsSpan(f * 2, n * 2));
                Assert.Equal(n, read);
                f += read;
            }

            callbacks = stream.Callbacks;
            Assert.Null(stream.CallbackError);
        }

        output.WriteLine($"{scenario}: {s.Frames} frames pulled through SDL in {callbacks} callbacks");
        AssertBits(s.Expected, got, $"{scenario} SDL pull");
    }

    private static void AssertBits(float[] expected, float[] got, string what)
    {
        Assert.Equal(expected.Length, got.Length);
        int differ = 0, first = -1;
        for (int i = 0; i < expected.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(got[i]))
            {
                differ++;
                if (first < 0)
                {
                    first = i;
                }
            }
        }

        Assert.True(differ == 0, $"{what}: {differ} of {expected.Length} samples differ; first at frame {first / 2} ch {first % 2}: oracle {(first >= 0 ? expected[first] : 0)} vs {(first >= 0 ? got[first] : 0)}");
    }

    private sealed class Scenario
    {
        public required int Frames { get; init; }

        public required float[] Expected { get; init; }

        public required List<MixerCommand> Commands { get; init; }

        public static Scenario Load(string name)
        {
            using JsonDocument doc = JsonDocument.Parse(AudioGolden.ReadVerified($"mixer/{name}.json"));
            JsonElement root = doc.RootElement;
            Assert.Equal(AudioMixer.DefaultRampFrames, root.GetProperty("default_ramp").GetInt32());
            var clips = new Dictionary<string, AudioClip>();
            var decoder = new MediaAudioDecoder();
            foreach (JsonProperty c in root.GetProperty("clips").EnumerateObject())
            {
                int channels = c.Value.GetProperty("channels").GetInt32();
                AudioLoop? loop = c.Value.GetProperty("loop") is { ValueKind: JsonValueKind.Array } l ? new AudioLoop(l[0].GetInt32(), l[1].GetInt32()) : null;
                AudioClip clip;
                if (c.Value.TryGetProperty("file", out JsonElement file))
                {
                    clip = new AudioClip(AudioBank.Se, -1, channels, 48_000, AudioGolden.Floats("mixer/" + file.GetString()), loop);
                }
                else
                {
                    AudioBank bank = AudioGolden.Bank(c.Value.GetProperty("bank").GetString()!);
                    int index = c.Value.GetProperty("index").GetInt32();
                    using MrgArchive archive = GameImage.Open(AudioGolden.BankFile(bank));
                    clip = decoder.Decode(bank, index, AudioGolden.Stream(archive, bank, index));
                    Assert.Equal(loop, clip.Loop);
                }

                Assert.Equal(c.Value.GetProperty("frames").GetInt32(), clip.FrameCount);
                Assert.Equal(c.Value.GetProperty("sha1").GetString(), AudioGolden.Sha1(clip.Samples.Span));
                clips[c.Name] = clip;
            }

            var commands = new List<MixerCommand>();
            foreach (JsonElement c in root.GetProperty("commands").EnumerateArray())
            {
                long frame = c.GetProperty("frame").GetInt64();
                int voice = c.GetProperty("voice").GetInt32();
                int ramp = c.TryGetProperty("ramp", out JsonElement r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : -1;
                commands.Add(c.GetProperty("op").GetString() switch
                {
                    "play" => MixerCommand.Play(voice, clips[c.GetProperty("clip").GetString()!], c.GetProperty("loop").GetBoolean(),
                        F32(c.GetProperty("gain")), F32(c.GetProperty("pan")), c.GetProperty("repeats").GetInt32(), c.GetProperty("start").GetInt32(), frame),
                    "stop" => MixerCommand.Stop(voice, frame),
                    "gain" => MixerCommand.SetGain(voice, F32(c.GetProperty("gain")), ramp, frame),
                    "pan" => MixerCommand.SetPan(voice, F32(c.GetProperty("pan")), ramp, frame),
                    var op => throw new InvalidDataException($"unknown op {op}"),
                });
            }

            int frames = root.GetProperty("frames").GetInt32();
            float[] expected = AudioGolden.Floats("mixer/" + root.GetProperty("output").GetString());
            Assert.Equal(frames * 2, expected.Length);
            return new Scenario { Frames = frames, Expected = expected, Commands = commands };
        }

        public AudioMixer NewMixer()
        {
            var m = new AudioMixer();
            foreach (MixerCommand c in Commands)
            {
                m.Post(c);
            }

            return m;
        }

        private static float F32(JsonElement e) => BitConverter.Int32BitsToSingle(unchecked((int)e.GetProperty("bits").GetUInt32()));
    }
}
