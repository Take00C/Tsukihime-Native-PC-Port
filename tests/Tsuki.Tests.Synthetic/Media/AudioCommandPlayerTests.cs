using Tsuki.Engine.Contracts;
using Tsuki.Engine.Sound;
using Tsuki.Media.Mixer;
using Tsuki.Media.Sound;

namespace Tsuki.Tests.Synthetic.Media;

/// <summary>The AudioCommand to mixer translation (package K1c) on generated clips, rendered through the mixer's pull interface.</summary>
public sealed class AudioCommandPlayerTests
{
    private static AudioClip Clip(AudioBank bank, int index, int channels, float[] samples, AudioLoop? loop = null) =>
        new(bank, index, channels, 48_000, samples, loop);

    /// <summary>A stereo ramp: left = i / 1000, right = -i / 1000 for frame i.</summary>
    private static AudioClip StereoRamp(int frames, AudioLoop? loop = null, AudioBank bank = AudioBank.Se, int index = 1)
    {
        float[] s = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            s[2 * i] = i / 1000f;
            s[(2 * i) + 1] = -i / 1000f;
        }

        return Clip(bank, index, 2, s, loop);
    }

    private static AudioClip Dc(float value, int frames, AudioBank bank = AudioBank.Se, int index = 2, AudioLoop? loop = null) =>
        Clip(bank, index, 2, [.. Enumerable.Repeat(value, frames * 2)], loop);

    private static float[] Render(AudioMixer m, int frames)
    {
        float[] o = new float[frames * 2];
        m.Render(o);
        return o;
    }

    private static (AudioCommandPlayer Player, AudioMixer Mixer, List<(AudioBank, int)> Requests) Make(Func<AudioBank, int, AudioClip> clips, AudioPlayerOptions? options = null)
    {
        var mixer = new AudioMixer();
        List<(AudioBank, int)> requests = [];
        var player = new AudioCommandPlayer((b, i) =>
        {
            lock (requests)
            {
                requests.Add((b, i));
            }

            return clips(b, i);
        }, mixer, options);
        return (player, mixer, requests);
    }

    [Fact]
    public void APlayStartsTheClipOnTheStreamVoiceAtTheCommandGain()
    {
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) => StereoRamp(500));
        player.Perform([AudioCommand.OpenHandle(6, AudioBank.Se), AudioCommand.SetVolume0(6, 0.5f), AudioCommand.Play(6, AudioBank.Se, 1, loop: false, volume: 0.5f)]);
        Assert.True(player.IsPlaying(6));
        Assert.True(mixer.CommandsPosted >= 1);
        float[] o = Render(mixer, 200);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(0.5f * (i / 1000f), o[2 * i]);
            Assert.Equal(0.5f * (-i / 1000f), o[(2 * i) + 1]);
        }

        Assert.True(mixer.IsPlaying(MixerVoices.Stream(6)));
        Assert.Empty(player.Failures);
    }

    [Fact]
    public void ThePanThatFollowsAPlayInTheSameBatchAppliesFromTheFirstFrame()
    {
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) => Dc(1f, 3000));
        player.Perform([AudioCommand.Play(7, AudioBank.Se, 2, false, 1f), AudioCommand.SetPan(7, 1f)]);
        float[] o = Render(mixer, 4);
        Assert.Equal([0f, 1f, 0f, 1f, 0f, 1f, 0f, 1f], o);

        // a pan in a later batch is a ramp of one 60 Hz step (800 frames)
        player.Perform(AudioCommand.SetPan(7, -1f));
        float[] later = Render(mixer, 900);
        Assert.Equal(1f, later[1]);                        // the right channel is still at full gain on the first frame...
        Assert.True(later[0] > 0f);
        Assert.Equal(0f, later[2 * 799 + 1]);              // ...and the ramp has finished 800 frames after it was posted
        Assert.Equal(1f, later[2 * 799]);
    }

    [Fact]
    public void ASetVolumeOnARunningVoiceRampsOverOneStepAndNothingIsClipped()
    {
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) => Dc(1f, 5000));
        player.Perform(AudioCommand.Play(8, AudioBank.Se, 2, false, 1f));
        Render(mixer, 10);
        player.Perform(AudioCommand.SetVolume(8, 0f));
        float[] o = Render(mixer, 800);
        Assert.Equal(1f + ((0f - 1f) * (1f / 800f)), o[0]);
        Assert.Equal(0f, o[2 * 799]);
        Assert.True(o[0] > o[2 * 400] && o[2 * 400] > o[2 * 799]);
    }

    [Fact]
    public void TheStartSampleRegisterIsConsumedByTheNextPlayOnly()
    {
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) => StereoRamp(1000, bank: AudioBank.Bgm, index: 33));
        player.Perform([AudioCommand.SetStartSample(0, 100), AudioCommand.OpenFile(0, AudioBank.Bgm), AudioCommand.Play(0, AudioBank.Bgm, 33, false, 1f)]);
        float[] first = Render(mixer, 3);
        Assert.Equal(0.1f, first[0]);
        Assert.Equal(0.101f, first[2]);
        player.Perform([AudioCommand.OpenFile(0, AudioBank.Bgm), AudioCommand.Play(0, AudioBank.Bgm, 33, false, 1f)]);
        float[] second = Render(mixer, 2);
        Assert.Equal(0f, second[0]);
        Assert.Equal(0.001f, second[2]);
    }

    [Fact]
    public void AStartSampleBeyondTheClipStartsAtTheBeginningInsteadOfFailing()
    {
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) => StereoRamp(100));
        player.Perform([AudioCommand.SetStartSample(0, 5000), AudioCommand.Play(0, AudioBank.Se, 1, false, 1f)]);
        Assert.Equal(0f, Render(mixer, 1)[0]);
        Assert.Empty(player.Failures);
    }

    [Fact]
    public void TheLoopFlagLoopsAClipWithALoopChunkAndAClipWithoutOnePlaysOnce()
    {
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) => i == 1
            ? StereoRamp(8, new AudioLoop(2, 5), index: 1)
            : StereoRamp(8, loop: null, index: i));
        player.Perform([AudioCommand.Play(6, AudioBank.Se, 1, loop: true, volume: 1f), AudioCommand.SetLoop(6, true, -1)]);
        player.Perform([AudioCommand.Play(7, AudioBank.Se, 2, loop: true, volume: 1f)]);
        float[] o = Render(mixer, 20);
        float[] a = [.. Enumerable.Range(0, 20).Select(i => o[2 * i] * 1000f)];
        // voice 6 loops [2, 5]; voice 7 (a clip without a loop chunk) plays once: 0..7
        float[] expected = [.. Enumerable.Range(0, 20).Select(i => i < 6 ? i : 2 + ((i - 6) % 4))];
        float[] voice7 = [0, 1, 2, 3, 4, 5, 6, 7];
        for (int i = 0; i < 20; i++)
        {
            float want = expected[i] + (i < 8 ? voice7[i] : 0);
            Assert.Equal(want, a[i], 3);
        }
    }

    [Fact]
    public void ALoopFlagOfZeroPlaysThroughOnceEvenWithALoopChunk()
    {
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) => StereoRamp(8, new AudioLoop(2, 5)));
        player.Perform([AudioCommand.Play(6, AudioBank.Se, 1, loop: false, volume: 1f)]);
        float[] o = Render(mixer, 12);
        Assert.Equal(7f / 1000f, o[2 * 7]);
        Assert.Equal(0f, o[2 * 8]);
        Assert.False(player.IsPlaying(6));
    }

    [Fact]
    public void IsPlayingFollowsPlayStopAndTheEndOfAOneShot()
    {
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) => StereoRamp(100));
        Assert.False(player.IsPlaying(6));
        player.Perform(AudioCommand.Play(6, AudioBank.Se, 1, false, 1f));
        Assert.True(player.IsPlaying(6), "playing from the command on, before the audio thread has run");
        Render(mixer, 50);
        Assert.True(player.IsPlaying(6));
        Render(mixer, 100);
        Assert.False(player.IsPlaying(6), "a one-shot has ended");
        player.Perform(AudioCommand.Play(6, AudioBank.Se, 1, false, 1f));
        Render(mixer, 10);
        player.Perform([AudioCommand.Stop(6), AudioCommand.Close(6)]);
        Assert.False(player.IsPlaying(6));
        Assert.Equal(0f, Render(mixer, 4)[0]);
    }

    [Fact]
    public void ReplacingAPlayingStreamRestartsItAndStopAllSilencesEverything()
    {
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) => StereoRamp(1000));
        player.Perform(AudioCommand.Play(6, AudioBank.Se, 1, false, 1f));
        Render(mixer, 100);
        player.Perform(AudioCommand.Play(6, AudioBank.Se, 1, false, 1f));
        Assert.Equal(0f, Render(mixer, 1)[0]);
        player.Perform(AudioCommand.Play(7, AudioBank.Se, 1, false, 1f));
        player.StopAll();
        Assert.All(Render(mixer, 8), v => Assert.Equal(0f, v));
        Assert.False(player.IsPlaying(6) || player.IsPlaying(7));
    }

    [Fact]
    public void ASystemSoundIsAHardLeftHardRightPairAndAMonoOneIsCentred()
    {
        (AudioCommandPlayer player, AudioMixer mixer, List<(AudioBank, int)> requests) = Make((b, i) =>
            Clip(AudioBank.Vag, i, 1, [.. Enumerable.Repeat(i == 3 ? 0.5f : 0.25f, 100)]));
        player.Perform(AudioCommand.PlaySystem(0x2C, 3, 4, 0.1f));
        float[] o = Render(mixer, 4);
        Assert.Equal(0.1f * 0.5f + 0f, o[0]);               // left clip on the left channel only (+ nothing from the right clip)
        Assert.Equal(0.1f * 0.25f, o[1]);                   // right clip on the right channel only
        Assert.Equal([(AudioBank.Vag, 3), (AudioBank.Vag, 4)], requests);

        player.Perform(AudioCommand.PlaySystem(0x2D, 4, -1, 0.2f));    // mono: centred
        float[] mono = Render(mixer, 4);
        Assert.Equal((0.1f * 0.5f) + (0.2f * 0.25f), mono[0]);
        Assert.Equal((0.1f * 0.25f) + (0.2f * 0.25f), mono[1]);

        player.Perform(AudioCommand.PlaySystem(0x2E, 0, -1, 0.2f));    // left <= 0: nothing
        Assert.Equal(2, requests.Count);
        Assert.Empty(player.Failures);
    }

    [Fact]
    public void AMissingEntryIsRecordedAsAFailureAndDoesNotPlay()
    {
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) => i == 999 ? throw new InvalidDataException("no such entry") : StereoRamp(100));
        player.Perform(AudioCommand.Play(6, AudioBank.Se, 999, false, 1f));
        Assert.False(player.IsPlaying(6));
        Assert.Single(player.Failures);
        Assert.Equal((AudioBank.Se, 999), (player.Failures[0].Bank, player.Failures[0].Entry));
        Assert.All(Render(mixer, 4), v => Assert.Equal(0f, v));
        player.Perform(AudioCommand.Play(6, AudioBank.Se, 1, false, 1f));      // the player goes on
        Assert.True(player.IsPlaying(6));
    }

    [Fact]
    public void TheClipCacheDecodesOnceAndPrefetchWarmsIt()
    {
        (AudioCommandPlayer player, AudioMixer mixer, List<(AudioBank, int)> requests) = Make((b, i) => StereoRamp(100, index: i));
        player.Perform(AudioCommand.Play(6, AudioBank.Se, 5, false, 1f));
        player.Perform(AudioCommand.Play(7, AudioBank.Se, 5, false, 1f));
        Assert.Single(requests);
        player.Prefetch(AudioBank.Bgm, 9);
        Assert.True(SpinWait.SpinUntil(() => player.Cache.Bytes > 0 && requests.Count == 2, 5000));
        player.Perform(AudioCommand.Play(0, AudioBank.Bgm, 9, false, 1f));
        Assert.Equal(2, requests.Count);
        Assert.True(player.Cache.Hits >= 2);
        Render(mixer, 1);
    }

    [Fact]
    public void WithAsyncDecodeAPlayWaitsForItsClipAndThePanAfterItStillApplies()
    {
        using var release = new ManualResetEventSlim(false);
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) =>
        {
            release.Wait(10_000);
            return Dc(1f, 4000);
        }, new AudioPlayerOptions(AsyncDecode: true));
        player.Perform([AudioCommand.OpenFile(0, AudioBank.Bgm), AudioCommand.Play(0, AudioBank.Bgm, 33, true, 0f)]);
        Assert.True(player.IsPlaying(0), "a pending play counts as playing");
        player.Perform(AudioCommand.SetVolume(0, 0.25f));            // later steps keep changing the pending play
        player.Perform(AudioCommand.SetPan(0, -1f));
        Assert.All(Render(mixer, 8), v => Assert.Equal(0f, v));
        Assert.Equal(0, mixer.CommandsPosted);

        release.Set();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            player.Pump();
            return mixer.CommandsPosted > 0;
        }, 5000));
        float[] o = Render(mixer, 4);
        Assert.Equal([0.25f, 0f, 0.25f, 0f, 0.25f, 0f, 0.25f, 0f], o);
        Assert.Equal(1, player.PlaysPosted);
    }

    [Fact]
    public void AStopWhileTheClipIsStillDecodingCancelsThePlay()
    {
        using var release = new ManualResetEventSlim(false);
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) =>
        {
            release.Wait(10_000);
            return Dc(1f, 4000);
        }, new AudioPlayerOptions(AsyncDecode: true));
        player.Perform(AudioCommand.Play(0, AudioBank.Bgm, 33, true, 1f));
        player.Perform([AudioCommand.Stop(0), AudioCommand.Close(0)]);
        release.Set();
        Thread.Sleep(100);
        player.Pump();
        Assert.Equal(0, mixer.CommandsPosted);
        Assert.False(player.IsPlaying(0));
    }

    [Fact]
    public void TheSoundModelsEightSecondFadeInReachesTheScriptVolumeThroughTheMixer()
    {
        // _SEPL(7,1,name,,`001:8000,,`017:30): the model ramps the record 0 -> 30 in 480 frames; the SE volume byte 40 makes the
        // stream gain 0.12; the player ramps the voice one step per frame, so the render is a continuous staircase to 0.12.
        (AudioCommandPlayer player, AudioMixer mixer, _) = Make((b, i) => Dc(1f, 700_000, index: i, loop: new AudioLoop(0, 699_999)));
        var model = new SoundModel(seLookup: (n, f) => 598);
        model.IsPlaying = player.IsPlaying;
        model.Run("_SEPL(7,1,MOON_SE03028,,`001:8000,,`017:30);");
        player.Perform(model.TakeCommands());
        var peaks = new List<float>();
        for (int frame = 0; frame < 560; frame++)
        {
            float[] step = Render(mixer, 800);
            peaks.Add(step[^2]);                       // the last frame of the step, left channel (centre pan: gain 1.0)
            model.Update(1);
            player.Perform(model.TakeCommands());
        }

        Assert.True(peaks[0] < 0.01f);
        for (int i = 1; i < peaks.Count; i++)
        {
            Assert.True(peaks[i] >= peaks[i - 1], $"step {i}: {peaks[i]} < {peaks[i - 1]}");
        }

        Assert.Equal(0.12f, peaks[^1], 1e-6f);
        Assert.Equal(peaks[^1], peaks[500]);           // reached after 480 steps and then constant
        Assert.True(peaks[240] > 0.05f && peaks[240] < 0.07f, $"half way: {peaks[240]}");
        Assert.Empty(player.Failures);
        Assert.True(player.IsPlaying(13));

        // _SESP stops it
        model.Run("_SESP(7);");
        player.Perform(model.TakeCommands());
        Assert.False(player.IsPlaying(13));
        Assert.All(Render(mixer, 8), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ABgmCrossFadeFromTheModelKeepsBothSlotsAliveUntilTheOldOneHasFadedOut()
    {
        (AudioCommandPlayer player, AudioMixer mixer, List<(AudioBank, int)> requests) = Make((b, i) => Dc(1f, 2_000_000, AudioBank.Bgm, i, new AudioLoop(0, 1_999_999)));
        var model = new SoundModel();
        model.IsPlaying = player.IsPlaying;
        model.Run("_MPLY(10,,`017:100);");
        player.Perform(model.TakeCommands());
        Render(mixer, 800);
        Assert.True(player.IsPlaying(1));                 // the first track plays on slot 1 (the slots alternate from 0)
        model.Run("_MPLY(11,,`001:2000,,`017:100);");     // cross-fade: slot 0 comes in, slot 1 fades out
        player.Perform(model.TakeCommands());
        Assert.Equal([(AudioBank.Bgm, 10), (AudioBank.Bgm, 11)], requests);
        for (int f = 0; f < 200; f++)
        {
            Render(mixer, 800);
            model.Update(1);
            player.Perform(model.TakeCommands());
        }

        Assert.True(player.IsPlaying(0));
        Assert.False(player.IsPlaying(1), "the old track was stopped when its fade reached 0");
        Assert.Equal(0, model.CurrentSlot);
    }
}
