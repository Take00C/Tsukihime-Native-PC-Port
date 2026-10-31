# R1-engine: the engine fixer's round (STCF waits, skip-end sound, auto-save commit, rewind BGM, movie latch, read flag, screen effects)

Fixer for the engine area (label `fix-engine`), run on 2026-10-30 on top of 38cc3af (K18) with the K19 display work in the tree. Seven verified findings of the
R1 review, all fixed; none skipped. Nothing is committed. Builds and tests used `--artifacts-path artifacts/fix-engine` (shared tree) and
`.../artifacts/fix-engine-head` (a copy of HEAD plus my files, `scratch/fix-engine/head`, made while the app fixer's half-finished `Tsuki.App` edits broke the
shared build; the shared tree built again at the end). No app window was opened, no game folder, `%APPDATA%` or `artifacts/play*` / `dist` touched. Logs and
probe outputs are in `scratch/fix-engine/`.

Edits outside my folders (strictly needed, minimal): `src/Tsuki.App/Services/AppServices.cs` (4 lines in `AttachHost`: the two commit subscriptions of
finding 3) and `tests/Tsuki.Tests.Synthetic/App/AppServicesTests.cs` (one test, the file on disk after `_ATSV(8)`). Not mine but in my folder: another agent
("R1-app") changed `src/Tsuki.Engine/Save/SaveFile.cs` (`ReadWithFallback` reports the backup's fault too) during this run; left as it is.

## 1. `_STCF` waited for every layer ever named (critical) - fixed

Root cause as the review said: `StageCommands.cs` accumulated the layer bits and masks in `StageRuntime.StcfBits` / `StcfValues`, nothing cleared them, and
`GameHost.Stage.cs` copied the whole set into the wait at every `_STCF`, so `_STCF(0,8)` also waited for a layer an earlier `_STCF(l)` (mask 0x3F) had named
and whose minute-long motion still ran. wait_model.md 10.2 / 5.3: there is one word 0x1653E30; `_STCF` sets bit l and the byte, Adv_Update clears bit l when the
condition ends.

Fix: `GameHost.Stage.cs` `case "STCF"` reads the statement's own layer (arg 0) and mask (arg 1, absent 0x3F) and does `Wait.StcfLayers |= 1UL << l;
Wait.StcfMask[l] = mask; Wait.Bits |= Stcf; yield` (an out-of-range layer registers nothing and still yields). `StageRuntime.StcfBits` / `StcfValues` are
removed; the stage's `STCF` arm is a no-op (the name stays a stage command). `WaitModel.AdvUpdate` is unchanged: it clears the bits of the one word.

Tests: `ChoiceStageSoundTests.StcfWaitsOnlyForItsOwnLayerNotForALayerAnEarlierStcfNamed` (`_STCF(5)` after a 100 ms fade of layer 5, then a 10 s fade on
layer 5 without a wait, then `_STGS(0,...,800)` + `_STCF(0,8)`: the wait word holds layer 0 only with mask 8 and the script resumes after 40..60 frames; on
the old code the second wait is the 10 s fade) and `StcfWithoutAMaskWaitsForEveryTweenOfItsLayerAndAnOutOfRangeLayerYieldsOnce`; `StageCommandTests.
StcfIsAcceptedAsAStageCommandButChangesNothingOnTheStage` replaces the test of the removed properties. Data: `GamePlayTests.NoStcfWaitOfTheArcueidClickSweep
OutlivesItsOwnLayersCondition` replays the K15 Arcueid list headless (`play --policy list:2,1,0,... --mode click --lang zc --games 2`) with a trace and asserts
no `_STCF` yield is followed by the next statement more than 600 frames later and the run stays under 1,000,000 frames.

Evidence (`scratch/fix-engine/cli/arc_click.out`, `t_arc_click.txt`, the gap script's output in this session): **682,177 frames** (was 1,442,117), the same
172,404 statements, 59 choices, 188 scenes, 16,215 messages, 5.5 s; 1,318 `_STCF` yields holding **70,236 frames (10.3 %)** instead of 830,124 (58 %); the
longest **456 frames** (`F00678550 k454 @0x15a4c`, a layer's own motion), none above 600, 41 above 150 (hence the 600 ceiling, as the verifier advised). The
data test prints the same line (`data2.log`: `682177 frames, 1318 _STCF waits holding 70236 frames in all, the longest 456 frames`). Not re-run: the K15 app
sweep itself (screenshots); the headless run is the same interpreter on the same route.

## 2. A next-scene skip left the new scene's BGM and loop SEs silent (major) - fixed

`SoundModel.ResyncScriptState()` (new, `SoundModel.Restore.cs`) is the port of **0x7E620**, which I disassembled for this (`scratch/fix-engine/dis_7E620.txt`,
`tools/eboot/disasm.py func 0x7E620`): if the script track 0x16545D0 is not negative: `BgmSetLoop(1)`, `BgmStart(track, volume)` (its early-out 0x98C0 keeps a
stream that already plays the track), `BgmSetLoop(1)`, **0x9CE0(volume)** (the slot's volume and target at once, step 100, `set_volume(vol * BGM byte / 100)`:
new private `BgmSetVolume`); else `BgmStop` (0x9A90). Then for each of the 20 SE channels: a channel without a script-side loop (0x1654240 < 0 or 0x16542A0 <= 0)
is **stopped** (0xAAB0), a channel with one is started again (loop flag 1 = 0xAA80, its character 0xAD60, 0xA300 at the script volume, 0xA9E0 at the script pan,
0x1654210 = the loop entry). This differs from the review's suggested fix in two points the eboot settles: one-shot channels are stopped at the skip's end, and a
loop channel is restarted whether or not it is idle. The fresh model's "track 0 at volume 0" means no music (the RestoreScriptState rule) and gives no start.

`GameHost.EndSceneSkip(resync = true)` calls it after `Sound.FastForward = false`; the frame's `UpdateSound` drains the commands. Every end site goes through
EndSceneSkip (`_ATSV`, the unread line, `_JUMP`, `_TOMP`, the choices, the script end: adv_state.md 5.2 lists 0x96CBD, 0x9AA49, 0x9AA7A, 0x9ABC4, 0x9B08C, 0x9B20C
and the SELS / SELA helpers 0xB82E0 / 0xB8400 as callers of 0x7E620, which the disassembly's caller list confirms). `ApplyRestore` passes `resync: false`: its
own resync (0x7E4E0 -> `RestoreScriptState`) follows on a new model. `SceneSkipEnded` is still raised, after the resync.

Tests: `SkipRestoreStateTests.ANextSceneSkipStartsTheMusicAndTheLoopEffectTheScriptAskedForDuringIt` (`_MPLY(5); _SEPL(3,1,SE0001)` inside the skip: a BGM
Play of 5 and a looping Play on SE channel 3 at the skip's end, `CurrentTrack` 5 inside the event) and `ANextSceneSkipOverTheMusicsStopLeavesItSilentAndATrackThat
PlaysOnIsNotRestarted` (an `_MFAD` inside the skip stops the stream the skip left playing; a scene without music commands leaves the playing track without a
Stop or Play). Harness probe B re-run on this build (`scratch/fix-engine/probes2.txt`, `Harness nextscene-bgm 60`): **44 skips, 0 mismatches**; jump 3 `BGM
script-side -1->8, stream -1->8, bgm Play cmds after skip 1`, jump 12 `-1->35, stream -1->35`, jump 48 `-1->57, stream -1->57` (were `stream -1->-1`).

## 3. The `_ATSV(8)` auto save and the system block were not written to disk (major) - fixed

`AdvHistorySink` (`GameHost.History.cs`) gets two events: `AutoSaved`, raised when `store.AutoSave(...)` returned true, and `SceneStackPushed`, raised by every
`SceneStackPush()` (an `_ATSV` scene start). `AppServices.AttachHost` subscribes both to `Commit()` (outside edit, 4 lines): the auto save is committed at
once (adv_state.md 7.2: AutoSave -> SaveImage_Commit(2)), and every scene start writes the file, so read marks, picture / BGM / choice unlocks and the G words of
a session survive a crash (the review's "consider also"). The file is small (the user's 2-slot `save.dat` is 37 KB), a scene start is a natural checkpoint, and
`Commit` already runs on the game thread for manual saves. The host rebuilt by a language change re-subscribes through the same `AttachHost`.

Tests: `SkipRestoreStateTests.TheAutoSaveOfAtsv8ReachesTheStoreAndTellsTheAppSoDoesEverySceneStart` (events `scene, auto` after `_ATSV(8)` with slot 100
present inside the event, then `scene` at the next `_ATSV`); `AppServicesTests.TheAutoSaveOfASceneStartIsWrittenToTheFileAtOnce` (an `AppRig` with a save path:
no file before `Begin()`, after it the file reads back with slot 100 as its only slot, before any manual save, exit or return to the title). Not re-run: the
real-app kill test of the verifier (the shared `Tsuki.App` was mid-edit for most of this run); the synthetic app test exercises the same `AppServices.Commit`.

## 4. Every rewind step restarted the BGM (major) - fixed

`ApplyRestore` (`GameHost.Restore.cs`) decides `keepBgm = kind == Rewind && Sound.CurrentTrack == live.BgmTrack` (knife game.rs `restore_with`: a rewind,
sub_5D710, keeps the BGM when it is the same track and stops only the SEs and the voice; a load, the previous scene and a backlog jump stop every sound).
`ResetRuntime(keepBgm)` (`GameHost.cs`) still creates a fresh `SoundModel` but lets it `AdoptBgm(previous)` (new, `SoundModel.Restore.cs`: the two slot
records, the loop flags, the current slot and track, the ducking level), and `VirtualAudio.Reset(fromStream)` keeps the BGM streams' clock; `StopEverythingOutside
(fromStream)` / `SoundModel.StopEverything(fromStream)` stop only streams from `FirstVoice` on. `RestoreScriptState` skips `BgmSetLoop` / `BgmStart` when the
model's track already is the record's (the 0x98C0 early-out), and still sets the script-side words.

Tests: `SkipRestoreStateTests.ARewindOntoARecordWithTheSameTrackKeepsTheMusicPlayingAndADifferentTrackIsRestarted` (no Stop / Play / Close on streams 0..1, the
slot and its state 1 kept, the voices and SEs stopped and the record's loop SE restarted; then a rewind over a track change cuts and restarts). Harness probe H
(`probes2.txt`, `Harness rewind-bgm`): `RewindStep -> True; restored record BGM track 52; BGM stream commands:` **(none)**, was `Stop(s0) Close(s0) Stop(s1)
Close(s1) SetLoop(s0) OpenFile(s1) Play(s1,track 52) SetFlag6(s1)`.

## 5. The skip latch was cleared by a story movie and never restored (minor) - fixed

`MovieRecord.SavedModeWord` (`GameHost.Movie.cs`): `CmdTomp` saves `_modeWord` before clearing bit 1 (the state's enter callback, 0xDF2998), `EndMovie` does
`_modeWord = (_modeWord & ~2) | (saved & 2)` (the exit, 0x5E35C / 0x5F796: wait_model.md 9 "skip resumes after the movie"). `ComputeMode` no longer clears the
latch every frame while the movie runs; it still forces `SkipActive = 0, Step = 1` then. A toggle during the movie does not outlive it, as on the console.

Tests: `SkipRestoreStateTests.TheSkipLatchComesBackWhenAStoryMovieEnds` (latch off and `SkipActive` 0 during the movie, latch on and `SkipActive` 1 in the first
frame after it, the script going on) and `AMovieStartedWithoutTheLatchEndsWithoutIt`. Harness probe A (`Harness movie-latch`): `movie ended at frame 393: SkipLatch
now True`, `10 frames later: SkipLatch True, Mode.SkipActive 1` (was False / 0).

## 6. A re-displayed line kept the previous line's read flag (minor) - fixed

`MarkerState.Restore(block, sub)` (`Flow/MarkerState.cs`) now sets `Seen` and recomputes `IsRead` with the same readmax rule as `Enter` (shared `ReadNow`):
adv_state.md 6, 0x75C90 computes "read" from the current ids each time. `GameHost.LineRead` is unchanged and so is the read-only rule.

Test: `SkipRestoreStateTests.ALineReDisplayedByARewindIsReadWhenReadmaxSaysSoAndKeepsTheSkipLatch` (readmax 2 after three markers, a rewind onto sub 2:
`Markers.IsRead`, `LineRead` and the page's `Read` all true, Tab keeps the latch with `FastForwardReadOnly = 1`). Harness probe C (`Harness rewind-read`): all
three rewinds `Markers.IsRead=True, host.LineRead=True` and `Tab pressed on the re-displayed line: SkipLatch True, SkipStoppedByReadRule False` (were False /
True).

## 7. The `_FADE(40)` ghost and the focus effect were not re-applied after a restore (minor) - fixed, the focus part too

`GameHost.RestoreEffects()` (`GameHost.Effects.cs`), called by `ApplyRestore` after the stage and the sound (History_Restore's common part, adv_state.md 3.1):
`F1100 == 4` -> `GhostEffect.Restore(F1120 - 127, F1121)` (new: `SetMode(4, p, q)`, alpha 1, wobble wrapped, as knife `restore_with` does with
`ghost.set_mode(F1100, ...)` plus `wrapped = true`; alpha 1 because the effect was on when the record was pushed), then `StartFocus(Stage.Scene.Effect19A2, 0)`.
The focus effect needed no new record field: the PS4 keeps it in the scene state byte **+0x19A2** (`effect_19a2`, written by the FLNT / FLOP / FLSP / FCLN / FCSP
handlers 0xB7720..0xB79C0 and re-applied by `0xA88B0(byte, 0)` at the restore, adv_state.md's scene-state table; knife's "stage byte 6569"), which our
`SceneState` already carries through every record and save. `CmdFocus` now writes that byte (FCLN -> arg 5, FLNT 1, FLOP 2 / 3, FCSP / FLSP / FCCL 0) before
`StartFocus`, so older saves (byte 0) restore no effect, as before.

Test: `SkipRestoreStateTests.ARewindBringsBackTheGhostAndFocusEffectsOfItsRecord` (effects started between two pages and stopped after the second: a rewind onto
the second page gives `GhostMode` 4, alpha 1, wrapped, `FocusEffect` 10 fading in; a further rewind onto the first page leaves both off). Harness probe K
(`Harness ghost-restore`): `RewindStep -> True: F1100 4 F1120 127 F1121 0 GhostMode 4 GhostAlpha 1.00` (was `GhostMode 0 GhostAlpha 0.00`).

## Proof

| check | result |
|---|---|
| `dotnet build src/Tsuki.slnx -c Release -warnaserror` on the HEAD copy + my files (`scratch/fix-engine/build_head5.log`, `build_head6.log`) and on the shared tree once the app fixer's edits compiled (`build3.log`) | 0 warnings, 0 errors, both |
| `dotnet test tests/Tsuki.Tests.Synthetic` on the HEAD copy (`synthetic3.log`) | **1056 / 1056** (1045 of K18 + 11 new or reshaped: `SkipRestoreStateTests` 8, `ChoiceStageSoundTests` +2, `StageCommandTests` 1 replaced, `AppServicesTests` +1) |
| the same on the shared tree with every agent's changes (`synthetic_shared.log`) | **1104 / 1104** |
| `dotnet test tests/Tsuki.Tests.Data --filter Game\|Save\|Stage\|Vm\|Flow\|Text\|Effects` (`TSUKI_GAME_DIR` set, `data2.log`) | **133 / 133**, among them the new STCF sweep test (its output line above) |
| the new tests against the engine files reverted to HEAD in the copy (`before.log`) | **7 of 12 fail** (the STCF own-layer test and the six restore / skip / movie tests), 5 pass (the mask default, the stage no-op, the no-latch movie, the auto-save events whose file was kept, the existing STCF test) |
| review harness rebuilt over this engine, probes A, B, C, H, K (`probes2.txt`) | all five show the console behaviour (sections 2, 4, 5, 6, 7) |
| headless Arcueid click sweep, zc (`cli/arc_click.out`, `cli/t_arc_click.txt`) | 682,177 frames, longest `_STCF` wait 456 frames, 0 above 600 (section 1) |

## Open

1. The K15 app sweep (screenshots) and the verifier's real-app kill test were not re-run; the headless interpreter run and the synthetic app test cover the
   same code paths (`GameHost`, `AppServices.Commit`).
2. The resync's unconditional restart of a loop SE at a skip's end (0x7E620's loop body) restarts a loop that already played before the skip from its start; that
   is the console's behaviour and a next-scene skip is a deliberate action, so it is kept.
3. The scene-start commit writes the whole save file at every `_ATSV` scene start (a few KB per slot, synchronous, on the game thread). If a profile with many
   thumbnails ever shows a hitch at a scene start, commit only the system block there.
4. `scratch/fix-engine/head` is a `git archive` of HEAD with my files copied over; its `third_party` junction to the real folder was removed at the end of the run,
   so a rebuild there needs it again (`New-Item -ItemType Junction`).
