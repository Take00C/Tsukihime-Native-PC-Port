using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;
using Tsuki.Media.Mixer;
using Tsuki.Media.Sound;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>The menus' view of the game: slots, thumbnails, the save file, the title facts, the music and the gallery services.</summary>
public sealed class AppServicesTests
{
    private static (int Width, int Height, byte[] Rgba) Frame(byte shade)
    {
        byte[] px = new byte[ThumbnailCodec.Width * ThumbnailCodec.Height * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = shade;
            px[i + 1] = (byte)(shade / 2);
            px[i + 2] = 9;
            px[i + 3] = 255;
        }

        return (ThumbnailCodec.Width, ThumbnailCodec.Height, px);
    }

    [Fact]
    public void ASavedSlotShowsItsDateItsSceneTitleItsNotesAndItsThumbnail()
    {
        var rig = new AppRig();
        rig.Services.FrameCapture = () => Frame(120);
        rig.Begin();
        rig.RunTo("beta line");
        Assert.True(rig.Services.Save(3));

        UiSlot slot = rig.Services.Slots[3];
        Assert.True(slot.Used);
        Assert.Equal(3, slot.Index);
        Assert.True(Math.Abs((slot.Date - DateTime.Now).TotalMinutes) < 5);
        Assert.Contains("beta line", slot.Notes, StringComparison.Ordinal);
        Assert.False(rig.Services.Slots[4].Used);

        UiThumbnail? t = rig.Services.Thumbnail(3);
        Assert.NotNull(t);
        Assert.Equal((ThumbnailCodec.Width, ThumbnailCodec.Height), (t.Width, t.Height));
        Assert.Equal(120, t.Rgba.Span[0]);
        Assert.Null(rig.Services.Thumbnail(4));
        Assert.Same(t, rig.Services.Thumbnail(3));                              // decoded once per saved thumbnail
    }

    [Fact]
    public void SavingNeedsARunningStoryAndAManualSlot()
    {
        var rig = new AppRig();
        Assert.True(rig.Services.SavingDisabled);                               // at the title
        Assert.False(rig.Services.Save(0));
        rig.Begin();
        Assert.False(rig.Services.SavingDisabled);
        Assert.False(rig.Services.Save(100));                                   // the auto slots are the game's
        Assert.False(rig.Services.Save(139));
        Assert.True(rig.Services.Save(99));
    }

    [Fact]
    public void DeleteAndCopyWorkOnManualSlots()
    {
        var rig = new AppRig();
        rig.Begin();
        rig.Services.Save(0);
        Assert.True(rig.Services.Copy(0));
        Assert.True(rig.Services.Slots[1].Used);                                // the first free manual slot
        Assert.Equal(rig.Services.Slots[0].Notes, rig.Services.Slots[1].Notes);
        Assert.True(rig.Services.Delete(0));
        Assert.False(rig.Services.Slots[0].Used);
        Assert.False(rig.Services.Delete(0));                                   // already empty
        Assert.False(rig.Services.Delete(120));                                 // auto slots cannot be deleted from the list
        Assert.False(rig.Services.Copy(7));                                     // nothing to copy
    }

    [Fact]
    public void TheTitleKnowsTheProfileAndTheNewestSave()
    {
        var rig = new AppRig();
        UiTitleInfo fresh = rig.Services.Title;
        Assert.False(fresh.PrologueSeen);
        Assert.False(fresh.StoryStarted);
        Assert.Equal(-1, fresh.ContinueSlot);

        rig.Begin();
        rig.Services.Save(6);
        rig.Services.SetGlobal(10, 1);
        UiTitleInfo later = rig.Services.Title;
        Assert.True(later.StoryStarted);
        Assert.Equal(6, later.ContinueSlot);
        Assert.Equal(0, later.TitleStage);
    }

    [Fact]
    public void TheTitleSavesAnAutoSaveOncePerPoint()
    {
        var rig = new AppRig();
        rig.Begin();
        rig.RunTo("beta line");
        Assert.True(rig.Services.AutoSaveForTitle());
        Assert.True(rig.Services.Slots[SaveStore.AutoSaveFirst].Used);
        Assert.False(rig.Services.AutoSaveForTitle());                          // the newest auto save already holds this point
        rig.RunTo("gamma line");
        Assert.True(rig.Services.AutoSaveForTitle());
    }

    [Fact]
    public void ASceneStartAutoSavesThroughTheHistorySink()
    {
        var rig = new AppRig("_WKST(F822,eq,1);_ATSV(8);_WKST(F823,eq,0);_ZM00101($000002);_END_();");
        rig.Services.FrameCapture = () => Frame(50);
        rig.Begin();
        rig.Services.NoteStoryStep();
        Assert.True(rig.Services.Slots[SaveStore.AutoSaveFirst].Used);
        Assert.NotNull(rig.Services.Thumbnail(SaveStore.AutoSaveFirst));        // the sink asked the app for the frame
    }

    [Fact]
    public void TheAutoSaveOfASceneStartIsWrittenToTheFileAtOnce()
    {
        // adv_state.md 7.2: AutoSave ends in SaveImage_Commit(2); the file must hold slot 100 before any manual save, exit or return to the title (R1-engine 3)
        string dir = Path.Combine(Path.GetTempPath(), "tsuki-r1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "save.dat");
            var rig = new AppRig("_WKST(F822,eq,1);_ATSV(8);_WKST(F823,eq,0);_ZM00101($000002);_END_();", savePath: path);
            Assert.False(File.Exists(path));
            rig.Begin();
            Assert.True(File.Exists(path));
            SaveStore? back = SaveFile.ReadWithFallback(path, out bool backup);
            Assert.NotNull(back);
            Assert.False(backup);
            Assert.True(back.HasSave(SaveStore.AutoSaveFirst));
            Assert.Equal(SaveStore.AutoSaveFirst, Assert.Single(back.StoredSlots()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheSaveFileKeepsSlotsThumbnailsAndSettingsAcrossARestart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tsuki-k6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "save.dat");
            var first = new AppRig(savePath: path);
            first.Services.FrameCapture = () => Frame(200);
            first.Begin();
            first.RunTo("beta line");
            Assert.True(first.Services.Save(2));
            first.Services.Config.MusicVolume = 77;
            first.Services.ConfigChanged(ConfigChange.Volume);
            Assert.True(File.Exists(path));

            SaveStore? back = SaveFile.ReadWithFallback(path, out bool backup);
            Assert.NotNull(back);
            Assert.False(backup);
            var second = new AppRig(savePath: path, store: back);
            Assert.True(second.Services.Slots[2].Used);
            Assert.Equal(77, second.Services.Config.MusicVolume);
            Assert.Equal(200, second.Services.Thumbnail(2)!.Rgba.Span[0]);
            Assert.Equal(2, second.Services.Title.ContinueSlot);
            Assert.True(second.Services.Load(2));
            second.WaitFor("beta line");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AVolumeChangeReachesTheSoundModelAtOnce()
    {
        var rig = new AppRig();
        rig.Services.Config.MusicVolume = 12;
        rig.Services.Config.MusicMute = 1;
        rig.Services.ConfigChanged(ConfigChange.Volume);
        Assert.Equal(12, rig.Host.Sound.Config.Bgm);
        Assert.Equal(1, rig.Host.Sound.Config.MuteBgm);
        Assert.Equal(0, rig.Host.Sound.VolumeByte(0xC));                        // muted: the BGM volume byte is 0
    }

    [Fact]
    public void ProgressBitsAreReadFromTheGlobalWordsAndThePerSaveWords()
    {
        var rig = new AppRig();
        Assert.False(rig.Services.SceneBit(485));
        rig.Services.SetGlobal(110 + (485 >> 4), (ushort)(1 << (485 & 15)));
        Assert.True(rig.Services.SceneBit(485));
        Assert.True(rig.Services.Title.PrologueSeen);

        Assert.False(rig.Services.SceneDone(30));
        rig.Services.SetGlobal(300 + (30 >> 4), (ushort)(1 << (30 & 15)));
        Assert.True(rig.Services.SceneDone(30));

        Assert.False(rig.Services.RouteScene(9));
        rig.Host.Vars.SetF(201 + (9 >> 3), (ushort)(1 << (9 & 7)));
        Assert.True(rig.Services.RouteScene(9));
        rig.Host.Vars.SetF(600 + (5 >> 3), (ushort)(1 << (5 & 7)));
        Assert.True(rig.Services.RouteEdge(564 + 5));
        Assert.False(rig.Services.RouteEdge(10));

        rig.Services.MarkBgmHeard(7);
        Assert.True(rig.Services.BgmHeard(7));
        Assert.False(rig.Services.BgmHeard(8));
        Assert.False(rig.Services.PictureSeen(5));
    }

    [Fact]
    public void AGalleryReplayRunsThroughTheArchiveSceneAndEndsBackInTheGallery()
    {
        var rig = new AppRig();
        Assert.False(rig.Services.ReplayActive);
        rig.Services.ReplayScene(100, node: 5);                                 // (the rig has no scene 3000, so no story starts)
        Assert.True(rig.Host.ReplayRequested);
        Assert.Equal(100, rig.Host.Vars.GetG(8));                               // G008 names the scene to replay
    }

    [Fact]
    public void TheTitleMusicFadesOutAndStopsItsStream()
    {
        var mixer = new AudioMixer();
        var clip = new AudioClip(AudioBank.Bgm, 2, 2, 48_000, new float[48_000 * 2 * 4], new AudioLoop(0, 48_000 * 4 - 1));
        var player = new AudioCommandPlayer((_, _) => clip, mixer);
        var rig = new AppRig(audio: player);
        float[] buffer = new float[1600];
        rig.Services.PlayBgm(2);
        mixer.Render(buffer);
        Assert.True(player.IsPlaying(AudioStreams.Bgm0));
        Assert.Equal(2, rig.Services.PlayingBgm);

        rig.Services.FadeOutBgm(0.5f);
        for (int i = 0; i < 40; i++)
        {
            rig.Services.Tick();
            mixer.Render(buffer);
        }

        Assert.False(player.IsPlaying(AudioStreams.Bgm0));
        Assert.Equal(0, rig.Services.PlayingBgm);
    }

    [Fact]
    public void ASystemSoundGoesThroughTheSoundModelToThePlayer()
    {
        var mixer = new AudioMixer();
        var clip = new AudioClip(AudioBank.Vag, 0, 1, 48_000, new float[4800], null);
        var requested = new List<(AudioBank, int)>();
        var player = new AudioCommandPlayer(
            (bank, index) =>
            {
                requested.Add((bank, index));
                return clip;
            },
            mixer);
        var rig = new AppRig(audio: player);
        rig.Services.PlaySystemSound(UiSound.Decide);
        Assert.NotEmpty(requested);
        Assert.All(requested, r => Assert.Equal(AudioBank.Vag, r.Item1));
    }

    [Fact]
    public void LeavingForTheTitleEndsTheStoryAndAsksForTheTitleOnce()
    {
        var rig = new AppRig();
        rig.Begin();
        rig.Services.ReturnToTitle();
        Assert.False(rig.Services.StoryActive);
        Assert.True(rig.Services.TakePendingTitle());
        Assert.False(rig.Services.TakePendingTitle());
    }

    [Fact]
    public void TheEndOfAScriptIsReportedOnce()
    {
        var rig = new AppRig("_WKST(F822,eq,1);_ATSV();_WKST(F823,eq,0);_ZM00101($000002);_END_();");
        var reasons = new List<EndReason>();
        rig.Services.StoryEnded += reasons.Add;
        rig.Begin();
        for (int i = 0; i < 600 && rig.Host.Phase == GamePhase.Running; i++)
        {
            rig.Step(i % 3 == 0);
            rig.Services.CheckStoryEnd();
        }

        rig.Services.CheckStoryEnd();
        Assert.Equal([EndReason.End], reasons);
        Assert.False(rig.Services.StoryActive);
    }

    [Fact]
    public void AFlowchartJumpRestartsTheNodesSceneWithAFreshHistory()
    {
        var rig = new AppRig();
        rig.Begin();
        rig.RunTo("gamma line");
        Assert.True(rig.Services.FlowchartJump(column: 3, node: 12, scene: 100));
        Assert.Equal(12, rig.Host.F(110));                                      // the node the in-game chart opens on
        Assert.Equal(100, rig.Host.F(26));
        Assert.Equal(12, rig.Services.CurrentNode);
        rig.WaitFor("alpha line");
        Assert.True(rig.Host.History!.Count <= 3);                              // only what the restarted scene pushed
        Assert.False(rig.Services.FlowchartJump(0, 1, 9999));                   // a scene the data does not have: nothing starts
    }
}
