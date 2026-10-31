using Tsuki.App.Play;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Media.Mixer;
using Tsuki.Media.Sound;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Package R1: a script voice reaches the mixer. The sound model asks <c>is_playing</c> inside the host's step, after the script queued the voice's play and before
/// the runtime performed it; with the real player's answer (false) the model closed the stream in the same step and the pending play was cancelled, so every voice
/// of the game was silent and every <c>_VCWT</c> wait skipped. The services now hand the queued commands to the player before answering.
/// </summary>
public sealed class VoicePlaybackTests
{
    private const string Voiced = "_WKST(F822,eq,1);_WKAD(F823,1);_VPLY(A00_00_0001,00001);_ZM00101($000001@e);_VCWT();_WKST(F11,eq,1);_END_();";

    private static AudioClip Clip(AudioBank bank, int index) => new(bank, index, 1, AudioMixer.SampleRate, new float[AudioMixer.SampleRate * 2], null);   // two seconds

    private sealed class Rt
    {
        private readonly float[] _buffer = new float[AudioMixer.SampleRate / 60 * AudioMixer.Channels];
        private long _n;

        public Rt(string script)
        {
            Mixer = new AudioMixer();
            Player = new AudioCommandPlayer(Clip, Mixer);
            Rig = new AppRig(script, audio: Player);
            Rig.Rig.Line(1, "hello");
            Ui = new UiRoot(UiTestKit.Data(), Rig.Services);
            Runtime = new AppRuntime(Rig.Services, Ui, Player, null);
        }

        public AudioMixer Mixer { get; }

        public AudioCommandPlayer Player { get; }

        public AppRig Rig { get; }

        public UiRoot Ui { get; }

        public AppRuntime Runtime { get; }

        public GameHost Host => Rig.Host;

        /// <summary>One 60 Hz step of the runtime, and the mixer pulled for the step's 800 frames (as the app's muted loop does).</summary>
        public void Step()
        {
            Mixer.Render(_buffer);
            Runtime.Step(new InputFrame { Frame = ++_n }, _n * 16_666_667L, new DisplayList(2048));
        }
    }

    [Fact]
    public void TheVoiceOfAMessageReachesTheMixerAndVcwtWaitsForItsEnd()
    {
        var rt = new Rt(Voiced);
        rt.Rig.Services.StartScene(100);
        int started = -1;
        for (int i = 0; i < 40 && started < 0; i++)
        {
            rt.Step();
            if (rt.Host.Sound.VoiceState != 0)
            {
                started = i;
            }
        }

        Assert.True(started >= 0, "the voice never started");
        int stream = rt.Host.Sound.LastVoice >= 0 ? Enumerable.Range(2, 4).First(s => rt.Player.IsPlaying(s) || s == 5) : -1;
        Assert.True(Enumerable.Range(2, 4).Any(rt.Player.IsPlaying), "no voice stream is playing on the player");
        Assert.Equal(1, rt.Rig.Services.VoicePlaysPerformed);
        Assert.Equal(0, rt.Rig.Services.VoicePlaysDropped);
        Assert.Equal(1, rt.Player.PlaysPosted);
        Assert.NotEqual(-1, stream);

        // _VCWT holds the script while the two-second clip plays: the voice state returns to 0 only once the mixer has consumed the clip (about 120 steps), not in
        // the same step, and the script stands at the wait the whole time
        int waitingAt = rt.Host.Machine.Cursor;
        int frames = 0;
        while (rt.Host.Sound.VoiceState != 0 && frames < 400)
        {
            Assert.Equal(waitingAt, rt.Host.Machine.Cursor);
            rt.Step();
            frames++;
        }

        Assert.InRange(frames, 90, 160);
        Assert.Equal(0, rt.Host.Sound.VoiceState);
        rt.Step();
        Assert.NotEqual(waitingAt, rt.Host.Machine.Cursor);                 // the wait released the script
    }

    [Fact]
    public void TheSelfCheckCountsAPlayThePlayerDidNotTake()
    {
        // a Close right after the Play in the same batch is the click that stopped the voice, not a drop: nothing is counted
        var rt = new Rt(Voiced);
        rt.Rig.Services.StartScene(100);
        for (int i = 0; i < 40 && rt.Host.Sound.VoiceState == 0; i++)
        {
            rt.Step();
        }

        Assert.Equal(1, rt.Rig.Services.VoicePlaysPerformed);
        Assert.Equal(0, rt.Rig.Services.VoicePlaysDropped);

        // a decode failure is not a drop either: the player records it, the self-check sees the failure and stays quiet
        var failing = new AudioCommandPlayer((_, _) => throw new InvalidDataException("no such clip"), new AudioMixer());
        var rig = new AppRig(Voiced, audio: failing);
        rig.Rig.Line(1, "hello");
        var runtime = new AppRuntime(rig.Services, new UiRoot(UiTestKit.Data(), rig.Services), failing, null);
        rig.Services.StartScene(100);
        for (int i = 1; i <= 40 && failing.Failures.Count == 0; i++)
        {
            runtime.Step(new InputFrame { Frame = i }, i * 16_666_667L, new DisplayList(2048));
        }

        Assert.Single(failing.Failures);
        Assert.Equal(1, rig.Services.VoicePlaysPerformed);
        Assert.Equal(0, rig.Services.VoicePlaysDropped);
    }
}
