using Tsuki.Engine.Contracts;
using Tsuki.Media.Mixer;

namespace Tsuki.Tests.Synthetic.Media;

/// <summary>The float mixer's rules on generated clips (ARCHITECTURE 7.1, 7.2); the oracle renders are in Tests.Data.</summary>
public sealed class MixerTests
{
    private static AudioClip Mono(float[] s, AudioLoop? loop = null) => new(AudioBank.Se, 0, 1, 48_000, s, loop);

    private static AudioClip Stereo(float[] s, AudioLoop? loop = null) => new(AudioBank.Se, 0, 2, 48_000, s, loop);

    private static float[] Render(AudioMixer m, int frames)
    {
        float[] o = new float[frames * 2];
        m.Render(o);
        return o;
    }

    [Theory]
    [InlineData(-1f, 1f, 0f)]
    [InlineData(0f, 1f, 1f)]
    [InlineData(1f, 0f, 1f)]
    [InlineData(0.5f, 0.5f, 1f)]
    [InlineData(-0.5f, 1f, 0.5f)]
    public void TheEnginePanLawGivesBothSidesFullGainAtTheCentre(float pan, float left, float right)
    {
        var m = new AudioMixer();
        m.Post(MixerCommand.Play(6, Mono([1f, 1f]), loop: false, gain: 1f, pan: pan));
        float[] o = Render(m, 2);
        Assert.Equal([left, right, left, right], o);
    }

    [Fact]
    public void ALoopPlaysIntroThenTheBodyRepeatsPlusOneTimesAndNeverPastTheLoopEnd()
    {
        float[] s = [.. Enumerable.Range(0, 8).Select(i => (float)i)];
        var m = new AudioMixer();
        m.Post(MixerCommand.Play(0, Mono(s, new AudioLoop(2, 5)), loop: true, gain: 1f, pan: -1f, repeats: 2));
        float[] o = Render(m, 20);
        float[] left = [.. Enumerable.Range(0, 20).Select(i => o[2 * i])];
        Assert.Equal([0, 1, 2, 3, 4, 5, 2, 3, 4, 5, 2, 3, 4, 5, 0, 0, 0, 0, 0, 0], left);
        Assert.False(m.IsPlaying(0));
    }

    [Fact]
    public void WithoutTheLoopFlagTheClipPlaysToItsEnd()
    {
        var m = new AudioMixer();
        m.Post(MixerCommand.Play(0, Mono([1, 2, 3, 4], new AudioLoop(0, 1)), loop: false, gain: 1f, pan: -1f));
        float[] o = Render(m, 6);
        Assert.Equal([1f, 2, 3, 4, 0, 0], Enumerable.Range(0, 6).Select(i => o[2 * i]));
    }

    [Fact]
    public void TheMasterGainFadesTheWholeMixOutAndBackInOverHalfASecond()
    {
        // package K21: Mute in Background sets the target; the audio thread glides to it one step per frame, so a voice at full gain reaches silence after MasterFadeFrames
        float[] ones = [.. Enumerable.Repeat(1f, AudioMixer.SampleRate * 4)];
        var m = new AudioMixer();
        m.Post(MixerCommand.Play(0, Mono(ones), loop: false, gain: 1f, pan: 0f));
        float[] o = Render(m, 800);
        Assert.Equal(1f, o[0]);
        Assert.Equal(1f, o[^1]);
        Assert.Equal(1f, m.MasterGain);

        m.MasterGainTarget = 0f;
        o = Render(m, AudioMixer.MasterFadeFrames);
        Assert.InRange(o[0], 0.99f, 1f);                                            // the first frame has moved one step
        Assert.InRange(o[AudioMixer.MasterFadeFrames], 0.49f, 0.51f);              // the left sample of the middle frame: half way
        Assert.InRange(o[^2], 0f, 0.0001f);                                        // the last frame: silence (the float sum of the steps may need one frame more to land on 0)
        Assert.InRange(m.MasterGain, 0f, 0.0001f);
        o = Render(m, 800);
        Assert.Equal(0f, m.MasterGain);
        Assert.All(o.Skip(4), v => Assert.Equal(0f, v));                           // and it stays silent, the voice still playing
        Assert.True(m.IsPlaying(0));

        m.MasterGainTarget = 1f;
        o = Render(m, AudioMixer.MasterFadeFrames / 2);
        Assert.InRange(o[^1], 0.49f, 0.51f);                                       // half way up after half the fade
        Assert.InRange(m.MasterGain, 0.49f, 0.51f);
        o = Render(m, (AudioMixer.MasterFadeFrames / 2) + 800);
        Assert.Equal(1f, m.MasterGain);
        Assert.Equal(1f, o[^1]);
        m.MasterGainTarget = 7f;                                                   // clamped
        Assert.Equal(1f, m.MasterGainTarget);
    }

    [Fact]
    public void AGainRampIsLinearOverItsFramesAndNothingIsClipped()
    {
        float[] ones = [.. Enumerable.Repeat(1f, 12)];
        var m = new AudioMixer();
        m.Post(MixerCommand.Play(0, Mono(ones), loop: false, gain: 0f, pan: -1f));
        m.Post(MixerCommand.SetGain(0, 4f, rampFrames: 4, frame: 2));
        float[] o = Render(m, 8);
        Assert.Equal([0f, 0f, 1f, 2f, 3f, 4f, 4f, 4f], Enumerable.Range(0, 8).Select(i => o[2 * i]));
    }

    [Fact]
    public void ARetargetStartsFromTheValueOfThePreviousFrame()
    {
        var m = new AudioMixer();
        m.Post(MixerCommand.Play(0, Mono([.. Enumerable.Repeat(1f, 10)]), loop: false, gain: 0f, pan: -1f));
        m.Post(MixerCommand.SetGain(0, 8f, rampFrames: 8, frame: 0)); // 1, 2, 3, ...
        m.Post(MixerCommand.SetGain(0, 0f, rampFrames: 3, frame: 3)); // from 3: 2, 1, 0
        float[] o = Render(m, 8);
        Assert.Equal([1f, 2f, 3f, 2f, 1f, 0f, 0f, 0f], Enumerable.Range(0, 8).Select(i => o[2 * i]));
    }

    [Fact]
    public void TheDefaultRampIsOneSixtiethOfASecond()
    {
        Assert.Equal(800, AudioMixer.DefaultRampFrames);
        var m = new AudioMixer();
        m.Post(MixerCommand.Play(0, Mono([.. Enumerable.Repeat(1f, 2000)]), loop: false, gain: 0f, pan: -1f));
        m.Post(MixerCommand.SetGain(0, 1f));
        float[] o = Render(m, 1000);
        Assert.Equal(1f / 800, o[0]);
        Assert.Equal(399f / 800, o[2 * 398], 6);
        Assert.Equal(1f, o[2 * 799]);
        Assert.Equal(1f, o[2 * 999]);
    }

    [Fact]
    public void TheRenderDoesNotDependOnHowTheOutputIsChunked()
    {
        var rng = new Random(7);
        float[] a = [.. Enumerable.Range(0, 2 * 5000).Select(_ => (float)((rng.NextDouble() * 2) - 1))];
        float[] b = [.. Enumerable.Range(0, 3000).Select(_ => (float)((rng.NextDouble() * 3) - 1.5))];
        MixerCommand[] cmds =
        [
            MixerCommand.Play(0, Stereo(a, new AudioLoop(1000, 3999)), loop: true, gain: 0.7f, pan: 0.1f, frame: 0),
            MixerCommand.Play(9, Mono(b), loop: false, gain: 1.3f, pan: -0.4f, frame: 333),
            MixerCommand.SetPan(9, 0.9f, 2000, frame: 700),
            MixerCommand.SetGain(0, 0.1f, frame: 1500),
            MixerCommand.SetGain(0, 2f, 5, frame: 1810),
            MixerCommand.Stop(0, frame: 9000),
            MixerCommand.Play(0, Mono(b), loop: false, gain: 1f, pan: 0f, startFrame: 2500, frame: 9100),
        ];
        float[] whole = Chunked(cmds, 12_000, 12_000);
        foreach (int chunk in new[] { 1, 3, 480, 1024, 1025 })
        {
            float[] c = Chunked(cmds, 12_000, chunk);
            Assert.True(whole.AsSpan().SequenceEqual(c), $"chunk {chunk}");
        }

        Assert.Contains(whole, v => Math.Abs(v) > 1f); // never clipped
    }

    private static float[] Chunked(MixerCommand[] cmds, int frames, int chunk)
    {
        var m = new AudioMixer();
        foreach (MixerCommand c in cmds)
        {
            m.Post(c);
        }

        float[] o = new float[frames * 2];
        for (int f = 0; f < frames; f += chunk)
        {
            m.Render(o.AsSpan(f * 2, Math.Min(chunk, frames - f) * 2));
        }

        Assert.Equal(frames, m.FramesRendered);
        return o;
    }

    [Fact]
    public void AStampedCommandAppliesAtItsFrameInsideAPeriod()
    {
        var m = new AudioMixer();
        m.Post(MixerCommand.Play(3, Mono([.. Enumerable.Repeat(1f, 100)]), loop: false, gain: 1f, pan: -1f, frame: 10));
        m.Post(MixerCommand.Stop(3, frame: 15));
        float[] o = Render(m, 20);
        Assert.Equal([0f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0], Enumerable.Range(0, 20).Select(i => o[2 * i]));
    }

    [Fact]
    public void VoicesAddUpInVoiceOrderAndStopAllSilencesThem()
    {
        var m = new AudioMixer();
        m.Post(MixerCommand.Play(1, Mono([.. Enumerable.Repeat(0.25f, 10)]), loop: false, gain: 1f, pan: 0f));
        m.Post(MixerCommand.Play(0, Stereo([.. Enumerable.Repeat(0.5f, 20)]), loop: false, gain: 1f, pan: 0f));
        m.Post(MixerCommand.StopAll(frame: 4));
        float[] o = Render(m, 6);
        Assert.Equal([0.75f, 0.75f, 0.75f, 0.75f, 0.75f, 0.75f, 0.75f, 0.75f, 0, 0, 0, 0], o);
    }

    [Fact]
    public void AStreamVoiceReadsItsSourceAndEndsWhenItIsDrained()
    {
        var src = new ListSource([1f, -1f, 2f, -2f, 3f, -3f]);
        var m = new AudioMixer();
        m.Post(MixerCommand.PlayStream(20, src, 0.5f));
        float[] o = Render(m, 5);
        Assert.Equal([0.5f, -0.5f, 1f, -1f, 1.5f, -1.5f, 0, 0, 0, 0], o);
        Assert.False(m.IsPlaying(20));
    }

    [Fact]
    public void BadCommandsAreRejectedWhenPosted()
    {
        var m = new AudioMixer();
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Post(MixerCommand.Stop(64)));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Post(MixerCommand.Play(0, Mono([1, 2, 3]), false, 1f, 0f, startFrame: 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Post(MixerCommand.Play(0, Mono([1, 2, 3, 4], new AudioLoop(0, 1)), true, 1f, 0f, startFrame: 2)));
        Assert.Throws<ArgumentException>(() => m.Render(new float[3]));
    }

    private sealed class ListSource(float[] stereo) : IMixerSource
    {
        private int _pos;

        public int Channels => 2;

        public bool Ended => _pos * 2 >= stereo.Length;

        public int Read(Span<float> interleaved)
        {
            int n = Math.Min(interleaved.Length / 2, (stereo.Length / 2) - _pos);
            stereo.AsSpan(_pos * 2, n * 2).CopyTo(interleaved);
            _pos += n;
            return n;
        }
    }
}
