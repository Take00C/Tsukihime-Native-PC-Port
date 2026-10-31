using System.Text.Json;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Sound;
using Tsuki.Media.Audio;
using Tsuki.Media.Mixer;
using Tsuki.Media.Sound;
using Tsuki.Platform;

namespace Tsuki.Tests.Data.Media;

/// <summary>
/// The whole sound chain on real clips (package K1c): script statements -> <see cref="SoundModel"/> -> <see cref="AudioCommand"/>s ->
/// <see cref="AudioCommandPlayer"/> -> <see cref="AudioMixer"/> -> an SDL audio stream in pull mode (never a device).
/// </summary>
[Collection(SdlAudioCollection.Name)]
public sealed class SoundChainTests(ITestOutputHelper output)
{
    private static GameFolderSource Open() => GameFolderSource.Open(GameFolder.Require(), new MediaAudioDecoder());

    private static Func<string, int, int?> Lookup(GameFolderSource data) => (n, f) => data.SoundEffectNames.Lookup(n, f)?.Index;

    [GameDataFact]
    public void EverySeLookupTheReferenceTraceRecordedIsTheDataLayersLookup()
    {
        using GameFolderSource data = Open();
        using JsonDocument doc = JsonDocument.Parse(AudioGolden.ReadVerified("sound/sound_trace.json"));
        int checkedLookups = 0;
        foreach (JsonProperty p in doc.RootElement.GetProperty("se_lookup").EnumerateObject())
        {
            int bar = p.Name.LastIndexOf('|');
            string name = p.Name[..bar];
            int flags = int.Parse(p.Name[(bar + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            int? want = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetInt32();
            Assert.Equal(want, data.SoundEffectNames.Lookup(name, flags)?.Index);
            checkedLookups++;
        }

        Assert.True(checkedLookups > 500, $"only {checkedLookups} lookups");
        output.WriteLine($"{checkedLookups} recorded (name, flags) lookups equal SeNames.Lookup");
    }

    [GameDataFact]
    public void AnSeFadeInThroughTheModelThePlayerAndTheMixerEqualsTheHandBuiltMixerRun()
    {
        using GameFolderSource data = Open();
        var mixer = new AudioMixer();
        var player = new AudioCommandPlayer(data, mixer);
        var model = new SoundModel(seLookup: Lookup(data)) { IsPlaying = player.IsPlaying };

        // the reference: a second mixer that gets the same clip and the gains the model produced, as hand-written mixer commands
        var reference = new AudioMixer();
        AudioClip clip = data.Audio(AudioBank.Se, 598);
        Assert.NotNull(clip.Loop);

        const string Statement = "_SEPL(7,1,MOON_SE03028,,`001:8000,,`017:30);";
        model.Run(Statement);
        AudioCommand[] first = model.TakeCommands();
        player.Perform(first);
        Assert.Contains(first, c => c.Op == AudioOp.Play && c.Stream == 13 && c.Entry == 598);
        reference.Post(MixerCommand.Play(MixerVoices.Stream(13), clip, loop: true, gain: 0f, pan: 0f));

        int frames = 560;
        float[] a = new float[800 * 2];
        float[] b = new float[800 * 2];
        long differing = 0;
        float lastGain = 0;
        for (int step = 0; step < frames; step++)
        {
            mixer.Render(a);
            reference.Render(b);
            for (int i = 0; i < a.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
                {
                    differing++;
                }
            }

            model.Update(1);
            AudioCommand[] cmds = model.TakeCommands();
            player.Perform(cmds);
            foreach (AudioCommand c in cmds)
            {
                if (c.Op == AudioOp.SetVolume && c.Stream == 13)
                {
                    reference.Post(MixerCommand.SetGain(13, c.Value0));
                    lastGain = c.Value0;
                }
            }
        }

        output.WriteLine($"{frames} steps x 800 frames of vse #598: {differing} samples differ from the hand-built run; final gain {lastGain}");
        Assert.Equal(0, differing);
        Assert.Equal(30f, model.Record(13).Volume);
        // SE volume byte 40: 40 * 30 * 0.01 = 12 percent
        Assert.Equal(0.12f, lastGain, 1e-6f);
        Assert.Empty(player.Failures);
        Assert.True(player.IsPlaying(13));
    }

    [GameDataFact]
    public void BgmSeVoiceAndASystemSoundPlayTogetherThroughAnSdlPullStream()
    {
        using GameFolderSource data = Open();
        var mixer = new AudioMixer();
        var player = new AudioCommandPlayer(data, mixer);
        var cfg = new SoundConfig { Duck = 1 };
        var model = new SoundModel(cfg, Lookup(data)) { IsPlaying = player.IsPlaying };

        // bgm #33 with a 2 s fade-in, a looping SE with a pan, a voice (voice.hed entry 15,616 = 0x3D00, 105,536 frames)
        model.Run("_MPLY(33,,`001:2000,,`017:100);");
        model.Run("_SEPL(4,1,MOON_SE03028,,`017:60,`018:100);");
        model.Run("_VPLY(A00_06_00_0029_0,03d00);");
        Assert.True(model.StartVoiceAtMessage());
        model.SystemSePlay(0);
        player.Perform(model.TakeCommands());
        Assert.Empty(player.Failures);

        float peak = 0;
        double voiceEndedAtSeconds = -1;
        int steps = 0;
        float[] chunk = new float[480 * 2];
        using (var stream = AudioOutput.CreatePullOnly(mixer.Render))
        {
            long pulled = 0, nextStep = 800;
            while (pulled < 48_000 * 6)
            {
                int n = stream.Pull(chunk);
                Assert.Equal(480, n);
                pulled += n;
                foreach (float v in chunk)
                {
                    peak = Math.Max(peak, Math.Abs(v));
                }

                while (pulled >= nextStep)
                {
                    nextStep += 800;
                    model.DuckUpdate();
                    model.Update(1);
                    player.Perform(model.TakeCommands());
                    steps++;
                    if (voiceEndedAtSeconds < 0 && model.VoiceState == 0)
                    {
                        voiceEndedAtSeconds = pulled / 48_000.0;
                    }
                }
            }

            Assert.Null(stream.CallbackError);
        }

        output.WriteLine($"{steps} sound steps, peak {peak:F3}, the voice ended after {voiceEndedAtSeconds:F2} s; plays posted {player.PlaysPosted}");
        Assert.True(peak > 0.05f && peak < 1.5f, $"peak {peak}");
        Assert.InRange(voiceEndedAtSeconds, 105_536 / 48_000.0 - 0.1, 105_536 / 48_000.0 + 0.3);  // the clip is 2.2 s long
        Assert.True(player.IsPlaying(model.CurrentSlot), "the BGM plays");
        Assert.True(player.IsPlaying(10), "the looping SE plays");
        Assert.Equal(4, player.PlaysPosted);                                                    // bgm, se, voice, one system pair
        Assert.Empty(player.Failures);
        Assert.Equal(100f, model.Record(model.CurrentSlot).Volume, 0.5f);                       // the 2 s fade-in finished
    }
}
