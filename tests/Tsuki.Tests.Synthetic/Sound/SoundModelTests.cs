using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Sound;

namespace Tsuki.Tests.Synthetic.Sound;

/// <summary>
/// The sound model's rules on hand-written statements (docs/formats/sound_runtime.md). The step-for-step equality with the Python
/// model is in Tsuki.Tests.Data (SoundModelTraceTests); here are the behaviours the engine builds on: what each command emits, the
/// voice pipeline the Python model does not model (start, stream rotation, stop on advance, <c>_VPL2</c>), the system sounds and
/// the ducking arithmetic.
/// </summary>
public sealed class SoundModelTests
{
    /// <summary>A model whose library reports every stream playing (the reference traces' assumption) or none (a quiet start).</summary>
    private static SoundModel Make(SoundConfig? cfg = null, bool playing = true) => new(cfg, seLookup: (n, f) => n switch
    {
        "MOON_SE03028" => 598,
        "MOON_SE00001" => 1,
        "MOON_SE00002" => 2,
        _ => null,
    }, isPlaying: playing ? null : _ => false);

    private static string[] Take(SoundModel m) => [.. m.TakeCommands().Select(Describe)];

    /// <summary>The shape of a command; the float arguments are asserted separately with the model's own float arithmetic.</summary>
    private static string Describe(AudioCommand c) => c.Op switch
    {
        AudioOp.Play => $"play {c.Stream} {c.Bank} {c.Entry} loop={c.Arg0}",
        AudioOp.SetVolume => $"vol {c.Stream}",
        AudioOp.SetPan => $"pan {c.Stream}",
        AudioOp.SetLoop => $"loop {c.Stream} {c.Arg0} {c.Arg1}",
        AudioOp.SetStartSample => $"start {c.Stream} {c.Arg0}",
        AudioOp.PlaySystem => $"sys {c.Stream} {c.Entry} {c.Arg0}",
        AudioOp.OpenFile => $"open_file {c.Stream}",
        AudioOp.OpenHandle => $"open_handle {c.Stream} {c.Bank}",
        AudioOp.Close => $"close {c.Stream}",
        AudioOp.Stop => $"stop {c.Stream}",
        AudioOp.SetVolume0 => $"vol0 {c.Stream}",
        AudioOp.SetFlag6 => $"flag6 {c.Stream} {c.Arg0}",
        _ => c.Op.ToString(),
    };

    // 0.01f is not exact, so x * 0.01f is not the decimal one expects (40 * 0.01f is 0.39999998f); the expectations use the same float product.
    private static float Pct(float percent) => percent * 0.01f;

    [Fact]
    public void ANewTrackStartsOnTheOtherSlotWithTheBgmVolumeApplied()
    {
        SoundModel m = Make(playing: false);
        Assert.Equal(0, m.Run("_MPLY(33,,`017:100);"));
        // slot 0 is current at boot: set_loop goes to it (the old slot), the new track plays on slot 1
        AudioCommand[] c = m.TakeCommands();
        Assert.Equal(["loop 0 1 -1", "start 1 0", "open_file 1", "play 1 Bgm 33 loop=1", "flag6 1 0"], c.Select(Describe));
        Assert.Equal(Pct(40f), c.Single(x => x.Op == AudioOp.Play).Value0);              // BGM volume byte 40 at script volume 100
        Assert.Equal(1, m.CurrentSlot);
        Assert.Equal(33, m.CurrentTrack);
        Assert.Equal(1u, m.Record(1).State);
        Assert.Equal(100f, m.Record(1).Volume);
    }

    [Fact]
    public void ThePlayingTrackAgainIsIgnoredAndAnotherOneCrossesOver()
    {
        SoundModel m = Make();
        m.Run("_MPLY(33,,`017:100);");
        m.TakeCommands();
        m.Run("_MPLY(33,,`017:100);");
        Assert.Empty(m.TakeCommands());
        m.Run("_MPLY(34,,`017:100);");
        string[] c = Take(m);
        Assert.Contains("play 0 Bgm 34 loop=1", c);
        Assert.Equal(2u, m.Record(1).State);                 // the old slot fades out at 4 points a frame
        Assert.Equal(4f, m.Record(1).VolumeStep);
    }

    [Fact]
    public void AFadeInTrackRisesAtTheRateOfItsMilliseconds()
    {
        SoundModel m = Make();
        m.Run("_MPLY(33,,`001:3000,,`017:80);");
        SoundChannelRecord r = m.Record(1);
        Assert.Equal(0f, r.Volume);
        Assert.Equal(80f, r.VolumeTarget);
        float eighty = 80f;
        Assert.Equal(eighty * 1000f / 60f / 3000f, r.VolumeStep);
        for (int i = 0; i < 182; i++)
        {
            m.Update(1);
        }

        Assert.Equal(80f, r.Volume, 0.5f);
    }

    [Fact]
    public void MfadFadesOutAndReturnsTheWaitInSixteenMillisecondUnits()
    {
        SoundModel m = Make();
        m.Run("_MPLY(33,,`017:100);");
        m.TakeCommands();
        Assert.Equal(1, m.Run("_MFAD(,,`001:4000);"));
        Assert.Equal(4000 >> 4, m.Wait16);
        Assert.Equal(2u, m.Record(1).State);
        Assert.Equal(-1, m.CurrentTrack);
        Assert.Equal(0, m.Run("_MFAD(,,`001:4000,,`011:0);"));   // `011:0 = do not wait
    }

    [Fact]
    public void ASePlayUsesStream6PlusTheChannelAndTheSeVolumeByte()
    {
        SoundModel m = Make();
        m.Run("_SEPL(3,0,MOON_SE00001,,`017:50);");
        // SE volume byte 40: gain 40 * 50 / 100 = 20 -> 20 * 0.01f
        AudioCommand[] c = m.TakeCommands();
        Assert.Equal(["open_handle 9 Se", "vol0 9", "play 9 Se 1 loop=0", "pan 9"], c.Select(Describe));
        Assert.Equal(Pct(20f), c[2].Value0);
        Assert.Equal(c[1].Value0, c[2].Value0);
        Assert.Equal(0f, c[3].Value0);
        SoundChannelRecord r = m.Record(9);
        Assert.Equal((1u, 1u, (ushort)1, (ushort)1), (r.State, r.VolumeClass, r.Started, r.Entry));
        Assert.Equal(50f, r.Volume);
    }

    [Fact]
    public void AnEightSecondFadeInTakesFourHundredEightyFramesToThirtyPercent()
    {
        SoundModel m = Make();
        m.Run("_SEPL(7,1,MOON_SE03028,,`001:8000,,`017:30);");
        m.TakeCommands();
        SoundChannelRecord r = m.Record(13);
        Assert.Equal(0.0625f, r.VolumeStep);
        int frames = 0;
        while (r.Volume != r.VolumeTarget)
        {
            m.Update(1);
            frames++;
            Assert.True(frames <= 481);
        }

        Assert.Equal(480, frames);
        Assert.Equal(30f, r.Volume);
    }

    [Fact]
    public void ADeferredSeWaitsDelayTimesThreeFiftiethsFramesAndThenPlays()
    {
        SoundModel m = Make();
        m.Run("_SEPL(2,0,MOON_SE00002,,`017:100,`068:500);");
        Assert.Empty(m.TakeCommands());
        Assert.Equal(0u, m.Record(8).State);
        for (int i = 0; i < 29; i++)
        {
            m.Update(1);
            Assert.DoesNotContain(m.TakeCommands(), c => c.Op == AudioOp.Play);
        }

        m.Update(1);                                          // the 30th frame (500 * 3 / 50)
        Assert.Contains(m.TakeCommands(), c => c.Op == AudioOp.Play && c.Stream == 8 && c.Entry == 2);
        Assert.Equal(1u, m.Record(8).State);
    }

    [Fact]
    public void SefdFadesAndSespStops()
    {
        SoundModel m = Make();
        m.Run("_SEPL(1,1,MOON_SE00001,,`017:100);");
        m.Run("_SEPL(2,1,MOON_SE00002,,`017:100);");
        m.TakeCommands();
        m.Run("_SEFD(1,,,`001:1000);");
        Assert.Equal(2u, m.Record(7).State);
        float hundred = 100f;
        Assert.Equal(hundred * 1000f / 60f / 1000f, m.Record(7).VolumeStep);
        Assert.Equal(1u, m.Record(8).State);
        m.Run("_SEFD(-1,,,`001:0);");                         // every channel, at once
        Assert.Equal(0u, m.Record(8).State);
        Assert.Contains(m.TakeCommands(), c => c.Op == AudioOp.Stop && c.Stream == 8);
        m.Run("_SESP(1);");
        Assert.Equal(0u, m.Record(7).State);
    }

    [Fact]
    public void ACharacterTaggedSeFollowsTheCharactersVoiceGate()
    {
        var cfg = new SoundConfig { CharEnable = 0x3FFFFFFF & ~2u };
        SoundModel m = Make(cfg);
        m.Run("_SEPL(4,0,MOON_SE00001,,`017:100,`070:2);");  // character 2 = config slot 1, switched off
        AudioCommand play = m.TakeCommands().Single(c => c.Op == AudioOp.Play);
        Assert.Equal(0f, play.Value0);
        SoundModel on = Make();
        on.Run("_SEPL(4,0,MOON_SE00001,,`017:100,`070:2);"); // char gain 50 * 100 / 100 = 50% of the script volume
        Assert.Equal(Pct(50f), on.TakeCommands().Single(c => c.Op == AudioOp.Play).Value0);
    }

    [Fact]
    public void TheSePlayHookRunsWhenTheConfigAsksForIt()
    {
        var calls = new List<(int, int)>();
        SoundModel m = Make(new SoundConfig { SeCaption = 1 });
        m.SeStarted = (c, id) => calls.Add((c, id));
        m.Run("_SEPL(4,0,MOON_SE00001,,`017:100);");
        Assert.Equal([(4, 1)], calls);
    }

    [Fact]
    public void FastForwardAndScanOnlyUpdateTheScriptSideState()
    {
        SoundModel m = Make();
        m.FastForward = true;
        m.Run("_MPLY(33,,`017:100);");
        m.Run("_SEPL(1,1,MOON_SE00001,,`017:100);");
        Assert.Empty(m.TakeCommands());
        Assert.Equal(33, m.ScriptTrack);
        Assert.Equal(1, m.ScriptSe.LoopEntry(1));
        Assert.Equal(1 << (33 & 7), m.MusicBits[(33 + 0xC80) >> 3]);   // bit 3200 + 33: the track counts as heard
    }

    // -- the voice pipeline (not in the Python model: sound_runtime.md 7 and 3.6) -------------------

    [Fact]
    public void AVoiceIsPendingUntilTheMessageStartsItThroughTheGate()
    {
        SoundModel m = Make(playing: false);
        m.Run("_VPLY(A10_07_00_0147_1,01b40,0);");
        Assert.Equal(0x1B40, m.PendingVoice);
        Assert.Equal(2, m.PendingVoiceCharacter);            // group A10
        Assert.Empty(m.TakeCommands());
        Assert.True(m.StartVoiceAtMessage());
        Assert.Equal(-1, m.PendingVoice);
        // stream 2 + (++n & 3) = 3; the gain is char volume 50 * voice 100 / 100 = 50 -> 0.5
        AudioCommand[] c = m.TakeCommands();
        Assert.Equal(["open_handle 3 Voice", "play 3 Voice 6976 loop=0"], c.Select(Describe));
        Assert.Equal(Pct(50f), c[1].Value0);
        Assert.Equal(2, m.VoiceState);
        Assert.Equal(6976, m.LastVoice);
        Assert.Equal(50, m.VolumeByte(0xD));
    }

    [Fact]
    public void TheVoiceStreamRotatesOverTwoToFive()
    {
        SoundModel m = Make(playing: false);
        var streams = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            m.Run($"_VPLY(A00_06_00_0029_0,{i + 1:x5});");
            Assert.True(m.StartVoiceAtMessage());
            streams.Add(m.TakeCommands().Single(c => c.Op == AudioOp.Play).Stream);
        }

        Assert.Equal([3, 4, 5, 2, 3, 4], streams);
    }

    [Fact]
    public void AVoiceTheGateRefusesNeverStartsAndStillClearsThePendingVoice()
    {
        var cfg = new SoundConfig { Voice = 0 };
        SoundModel m = Make(cfg);
        m.Run("_VPLY(A00_06_00_0029_0,00726);");
        Assert.False(m.StartVoiceAtMessage());
        Assert.Equal(-1, m.PendingVoice);
        Assert.Empty(m.TakeCommands());

        cfg.Voice = 100;
        cfg.CharEnable = 0x3FFFFFFF & ~1u;                    // character A00 -> config slot 0 switched off
        m.Run("_VPLY(A00_06_00_0029_0,00726);");
        Assert.False(m.StartVoiceAtMessage());
        cfg.CharEnable = 0x3FFFFFFF;
        m.Run("_VPLY(A00_06_00_0029_0,00726);");
        m.FastForward = true;
        Assert.False(m.StartVoiceAtMessage());
        m.FastForward = false;
        m.Skip = true;
        cfg.SkipVoice = 0;
        m.Run("_VPLY(A00_06_00_0029_0,00726);");
        Assert.False(m.StartVoiceAtMessage());
        cfg.SkipVoice = 1;
        m.Run("_VPLY(A00_06_00_0029_0,00726);");
        Assert.True(m.StartVoiceAtMessage());
    }

    [Fact]
    public void VoiceEntriesAbove19999LiveInVoice2AndAbove59999AreRejected()
    {
        SoundModel m = Make();
        Assert.True(m.StartVoice(20_001));
        AudioCommand play = m.TakeCommands().Single(c => c.Op == AudioOp.Play);
        Assert.Equal((AudioBank.Voice2, 1), (play.Bank, play.Entry));
        Assert.True(m.StartVoice(19_999));
        Assert.Equal(AudioBank.Voice, m.TakeCommands().Single(c => c.Op == AudioOp.Play).Bank);
        Assert.False(m.StartVoice(60_000));
        Assert.Empty(m.TakeCommands());
    }

    [Fact]
    public void TheNextAdvanceStopsTheVoiceWhenTheConfigSaysSo()
    {
        SoundModel m = Make();
        m.Run("_VPLY(A00_06_00_0029_0,00726);");
        m.StartVoiceAtMessage();
        m.TakeCommands();
        Assert.False(m.StopVoiceOnAdvance(f1123: 1));         // the script keeps the voice
        Assert.Equal(2, m.VoiceState);
        Assert.True(m.StopVoiceOnAdvance(f1123: 0));
        Assert.Equal(0, m.VoiceState);
        Assert.Contains(m.TakeCommands(), c => c.Op == AudioOp.Stop && c.Stream == 3);

        var cfg = new SoundConfig { StopOnAdvance = 0 };
        SoundModel keep = Make(cfg);
        Assert.False(keep.ShouldStopVoiceOnAdvance(0));
        keep.Skip = true;
        Assert.True(keep.ShouldStopVoiceOnAdvance(1));        // skip mode stops it anyway
    }

    [Fact]
    public void AVoiceStateReturnsToZeroWhenTheStreamEndsAndAutoModeWaitsForIt()
    {
        bool playing = true;
        var m = new SoundModel(isPlaying: s => s != 3 || playing);
        m.StartVoice(5);
        m.TakeCommands();
        Assert.Equal(10_000, new SoundModel().AutoModeVoiceMilliseconds);   // no voice: the idle value
        for (int i = 0; i < 10; i++)
        {
            m.Update(1);
        }

        Assert.Equal(2, m.VoiceState);
        Assert.Equal(10 * 17, m.AutoModeVoiceMilliseconds);
        playing = false;
        m.Update(1);
        Assert.Equal(0, m.VoiceState);
        Assert.Contains(m.TakeCommands(), c => c.Op == AudioOp.Close && c.Stream == 3);
    }

    [Fact]
    public void VplTwoPlaysAtOnceAndLeavesTheReplayVoiceAlone()
    {
        SoundModel m = Make();
        m.Run("_VPLY(A00_06_00_0029_0,00001);");
        m.StartVoiceAtMessage();
        m.TakeCommands();
        Assert.Equal(1, m.LastVoice);
        m.Run("_VPL2(A10_06_00_0029_0,00002);");
        Assert.Equal(-1, m.PendingVoice);
        Assert.Equal(1, m.LastVoice);
        AudioCommand play = m.TakeCommands().Single(c => c.Op == AudioOp.Play);
        Assert.Equal((4, 2), (play.Stream, play.Entry));
    }

    [Fact]
    public void AnUnderscoreNameRepeatsThePreviousEntry()
    {
        SoundModel m = Make();
        m.Run("_VPLY(A00_06_00_0029_0,00abc);");
        m.Run("_VPLY(_,00000);");
        Assert.Equal(0xABC, m.PendingVoice);
        Assert.Equal(0, m.PendingVoiceCharacter);             // the name "_" is no group: character 0
    }

    // -- ducking (sound_runtime.md 9) -----------------------------------------------------------

    [Fact]
    public void DuckingFallsToHalfInThirtyTwoFramesAndRecoversInSixtyFour()
    {
        var cfg = new SoundConfig { Duck = 1 };
        SoundModel m = Make(cfg);
        m.Run("_MPLY(33,,`017:100);");
        m.TakeCommands();
        int frames = 0;
        while (m.DuckLevel > 128)
        {
            m.DuckUpdate(voiceActive: true);
            frames++;
        }

        Assert.Equal(32, frames);
        Assert.Equal(20, m.VolumeByte(0xC));                 // BGM 40 at half
        Assert.Contains(m.TakeCommands(), c => c.Op == AudioOp.SetVolume && c.Stream == 1);
        frames = 0;
        while (m.DuckLevel < 256)
        {
            m.DuckUpdate(voiceActive: false);
            frames++;
        }

        Assert.Equal(64, frames);
        Assert.Equal(40, m.VolumeByte(0xC));
    }

    [Fact]
    public void DuckingDoesNothingWhenTheOptionIsOffAndApplyConfigForcesARefresh()
    {
        SoundModel m = Make();
        m.DuckUpdate(voiceActive: true);
        Assert.Equal(256, m.DuckLevel);
        m.Config.Bgm = 70;
        Assert.Equal(40, m.VolumeByte(0xC));
        m.ApplyConfig();
        Assert.Equal(70, m.VolumeByte(0xC));
        Assert.Equal(70, m.DuckUpdate(voiceActive: false));   // applied = -1 forces the refresh on the next frame
        Assert.Null(m.DuckUpdate(voiceActive: false));
    }

    // -- system sounds (sound_runtime.md 10.1) -------------------------------------------------

    [Fact]
    public void SystemSoundsRotateOverTheFourSystemVoices()
    {
        SoundModel m = Make();
        Assert.True(m.SystemSePlay(0));
        Assert.True(m.SystemSePlay(2));
        Assert.False(m.SystemSePlay(10));                    // a dummy id plays nothing but still takes a voice
        Assert.True(m.SystemSePlay(1));
        Assert.True(m.SystemSePlay(7));
        AudioCommand[] c = m.TakeCommands();
        Assert.Equal([0x2C, 0x2D, 0x2F, 0x2C], c.Select(x => x.Stream));
        Assert.Equal([(3, 4), (1, 2), (5, 6), (7, 8)], c.Select(x => (x.Entry, x.Arg0)));
        Assert.All(c, x => Assert.Equal(Pct(10f), x.Value0));    // system SE volume byte 10
    }

    [Fact]
    public void TheSystemSoundTableMatchesTheDocument()
    {
        Assert.Equal((3, 4), SystemSounds.Vags(0));
        Assert.Equal((5, 6), SystemSounds.Vags(1));
        Assert.Equal((1, 2), SystemSounds.Vags(2));
        Assert.Equal((9, 10), SystemSounds.Vags(3));
        Assert.Equal((11, 12), SystemSounds.Vags(4));
        Assert.Equal((13, 14), SystemSounds.Vags(5));
        Assert.Equal((5, 6), SystemSounds.Vags(6));
        Assert.Equal((7, 8), SystemSounds.Vags(7));
        Assert.Equal((15, 16), SystemSounds.Vags(8));
        Assert.Equal((17, 18), SystemSounds.Vags(9));
        for (int id = 10; id < SystemSounds.Count; id++)
        {
            Assert.Equal((0, -1), SystemSounds.Vags(id));
        }
    }

    // -- plumbing ----------------------------------------------------------------------------------

    [Fact]
    public void TheModelReadsTheConfigBlockAndHandlesTheSoundCommandNames()
    {
        ConfigBlock block = ConfigBlock.CreateBootDefaults();
        block.MusicVolume = 77;
        block.SetCharacterVoiceVolume(3, 12);
        block.MusicMute = 1;
        SoundConfig cfg = SoundConfig.FromBlock(block);
        Assert.Equal(77, cfg.Bgm);
        Assert.Equal(12, cfg.CharVol[3]);
        Assert.Equal(100, cfg.Voice);
        Assert.Equal(0x3FFFFFFFu, cfg.CharEnable);
        var m = new SoundModel(cfg);
        Assert.Equal(0, m.VolumeByte(0xC));                  // muted

        foreach (string n in new[] { "MPLY", "MVOL", "MFAD", "MSTP", "SEPL", "SEFI", "SEFD", "SEVL", "SESP", "VPLY", "VPL2", "VCLR" })
        {
            Assert.True(SoundModel.Handles(CommandName.FromAscii(n)), n);
        }

        Assert.False(SoundModel.Handles(CommandName.FromAscii("WTTM")));
        Assert.False(SoundModel.Handles(CommandName.FromAscii("SESX")));
    }

    [Fact]
    public void ExecuteTakesTheArgumentsOfAVmStatement()
    {
        byte[] args = Encoding.Latin1.GetBytes("(33,,`017:100);");
        var st = new Statement(2, 100, 100 + 4 + args.Length, CommandName.FromAscii("MPLY"), args);
        SoundModel m = Make();
        Assert.Equal(0, m.Execute(st));
        Assert.Equal(33, m.CurrentTrack);
        Assert.Contains(m.TakeCommands(), c => c.Op == AudioOp.Play && c.Entry == 33);
    }

    [Fact]
    public void TheRecordsAreTheL1BlocksAndTheStateWordsChangeWithTheState()
    {
        SoundModel m = Make();
        int[] before = m.StateWords();
        byte[] recordsBefore = m.RecordBytes();
        Assert.Equal(26 * 0x34, recordsBefore.Length);
        m.Run("_MPLY(33,,`017:100);");
        Assert.NotEqual(before, m.StateWords());
        Assert.NotEqual(recordsBefore, m.RecordBytes());
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Record(26));
    }

    [Fact]
    public void AnIdleStreamIsOnlyStoppedWhenTheLibraryReportsItPlaying()
    {
        var m = new SoundModel(isPlaying: _ => false);
        m.Update(1);
        Assert.Empty(m.TakeCommands());
        var loud = new SoundModel();                          // the reference traces assume every stream plays
        loud.Update(1);
        Assert.Equal(22 * 2, loud.TakeCommands().Length);      // 2 BGM + 20 SE streams: stop + close each (voices stay quiet)
    }
}
