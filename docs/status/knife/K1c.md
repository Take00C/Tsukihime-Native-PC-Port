# K1c: movie thread, SoundModel (E2) and the AudioCommand -> mixer translation

Package K1c of the knife-reference wave (E1 left these three items open). Run on 2026-10-26. Every number below comes from a
command in section 3, run in this session. Nothing is committed (the lead commits after review).

## 1. Result: done

| goal | result |
|---|---|
| 1. Movie thread: S2's FFmpeg pipeline in `Tsuki.Media/Movie`, host for `MovieCommand`s, `MovieClock` | **Done.** `prologue_en.mp4`: **11,660 / 11,660 frames, 18,655,997 AAC samples per channel**; 584 frames equal the ffmpeg CLI's MD5 (every 20th, first, last), the whole audio SHA-1 equals the CLI's; 18.5-19.0 s headless (630 fps while hashing). |
| 2. SoundModel (E2): `misc_audio_runtime_semantics.py` in `Tsuki.Engine/Sound` | **Done.** **100,177 steps and 1,363,213 sound library calls equal the Python model**, with the 26 channel records and the integer state compared on every step; includes H3a's slice trace (8,000 ms fade-in to 30 % in exactly 480 frames, 600 frames, 26 records). |
| 3. AudioCommand -> mixer translation, tested with the pull-mode harness | **Done.** `AudioCommandPlayer`: stream numbers, open/close, volume0, loops, start sample, `PlaySystem` L/R pair, clips from `IGameData.Audio`; 16 synthetic tests; real-clip chain through an SDL pull stream. |

## 2. What was built

`src/Tsuki.Media/Movie/` (new files; E1's `FrameSelector`, `AudioClock`, `PcmRing`, `MovieAudioSource` are used as they were):

| file | content |
|---|---|
| `FfmpegMovieIo.cs` | `Demuxer`, software `VideoDecoder` (frame + slice threads, `thread_count` 0; no hardware path), `AudioStreamDecoder` (AAC + swresample to float32 stereo 48 kHz). Lifted from S2 `Ffmpeg.cs`; corrupt packets are dropped, not fatal |
| `MovieQueues.cs` | `VideoFrame` (pinned Y, U, V), `FrameQueue` (8 buffers, ready queue, `PeekStamps`), `PacketQueue` with end and loop markers. From S2 `Queues.cs` |
| `MoviePipeline.cs` | demux + AAC thread and H.264 thread, seek trim (`StartSeconds`), loop (`Loop`, `LoopPointSeconds` = 8.0333 s, a continuous frame index and pts across the wrap), `DiscardAudio()` for a ring nobody reads. From S2 `MoviePipeline.cs` |
| `MoviePlayer.cs` | one open movie: pipeline, mixer voice, `AudioClock`, `FrameSelector` (4 ms cadence lock), preroll, end detection, frame-clock fallback (no mixer, no pulls for 2 s, or a clock that stops for 2 s) |
| `MovieHost.cs` | performs `MovieCommand`s (open replaces a slot, stop, set volume), `Step(nominalTimestamp)` returns one `MovieClock` per open slot; `VirtualTime` mode (explorer: ends at once, FFmpeg not loaded); a movie that fails to open or dies is recorded in `Failures` and reports `Ended` so `_TOMW` never hangs |

`src/Tsuki.Engine/Movie/MovieTable.cs`: the `_TOMP` language remap (archives_ui_movies.md 9.3) and the slot numbers (`FullScreen` 0, `TitleBackground` 1).

`src/Tsuki.Engine/Sound/` (port of `misc_audio_runtime_semantics.py`, 1,295 lines; records are the L-1 `SoundChannelRecord`):

| file | content |
|---|---|
| `SoundModel.cs` | state, backend, BGM primitives, `Update(dt)` (deferred timers, BGM / voice / SE ramps, fade-to-stop, pan sweeps), `DuckUpdate`, `StateWords`, `RecordBytes`, `TakeCommands` |
| `SoundModel.Commands.cs` | `_MPLY _MVOL _MFAD _MSTP _SEPL/_SEFI _SEFD _SEVL _SESP`, the deferred (`` `068 ``) queue; `Execute(in Statement)` for the VM, `Run(string)` for tests, `Handles(CommandName)` |
| `SoundModel.Voice.cs` | `_VPLY/_VPL2/_VCLR`, the voice gate, voice start on streams `2 + (++n & 3)`, stop on advance, `VoiceState`, `AutoModeVoiceMilliseconds`, `SystemSePlay(id)` |
| `SoundConfig.cs`, `SoundMath.cs`, `SoundTags.cs`, `SoundTables.cs` | the config fields (`FromBlock(ConfigBlock)`), the SSE-exact helpers (`cvttss2si`, `minss`, ramps), the tag scan, voice groups and the system sound table |

`src/Tsuki.Media/Sound/AudioCommandPlayer.cs` + `ClipCache.cs`, `src/Tsuki.Media/Mixer/MixerVoices.cs`: the translation. Voice map of the 64 mixer slots:
streams 0..25 keep their numbers, the right clip of a system sound pair is voice 26 + k, system voices 0x2C..0x2F, movie slot s on 0x30 + s.
`AudioMixer` got two additive counters (`CommandsPosted`, `CommandsApplied`) so that `IsPlaying` is true from the `play` command on, even
before the audio thread has applied it.

`tools/oracle/audio.py`: parts `movie` (ffmpeg CLI per-frame MD5 of three movies, AAC digest) and `sound` (the Python model's traces).

Tests: `tests/Tsuki.Tests.Synthetic/Media/` (+36: `AudioCommandPlayerTests` 16, `MovieHostTests` 20) and `Sound/SoundModelTests.cs` (27, in `Sound/`);
`tests/Tsuki.Tests.Data/Media/` (+14: `MovieTests` 9, `SoundModelTraceTests` 2, `SoundChainTests` 3; 28 with E1's 14).

How the pieces plug into the app (M2a): one `AudioMixer`; `AudioOutput.OpenDevice(mixer.Render)`; `new AudioCommandPlayer(data, mixer)`;
`new MovieHost(data, mixer)`; `new SoundModel(SoundConfig.FromBlock(config), name => se lookup) { IsPlaying = player.IsPlaying }`. Per 60 Hz tick (`LogicTick`):
`model.Update(1); model.DuckUpdate(); player.Perform(model.TakeCommands()); movies.Perform(commands); clocks = movies.Step(tick.NominalTimestamp)`.
The VM calls `model.Execute(statement)` for the names `SoundModel.Handles` accepts; `StartVoiceAtMessage()` runs at `_ZM`, `StopVoiceOnAdvance()` at the advance.

## 3. Commands that prove it (from the repository root)

```bash
# oracle (Python), writes samples/golden/audio/ (local only)
PYTHONPATH=tools .venv/Scripts/python.exe -m oracle.audio --parts movie,sound   # movie 49 s (3 checks ok), sound 910 scenarios / 100,177 steps in 19 s (1 check ok)
PYTHONPATH=tools .venv/Scripts/python.exe -m oracle.audio --parts hevag,mixer   # re-export, unchanged bytes
PYTHONPATH=tools .venv/Scripts/python.exe -m oracle.audio --verify              # 0 problems
.venv/Scripts/python.exe -m pytest tools/tests/test_oracle.py -q                                 # 15 passed (PYTHONPATH as above)

# C#
dotnet build src/Tsuki.slnx -warnaserror --artifacts-path artifacts/K1c      # 0 warnings, 0 errors
dotnet test tests/Tsuki.Tests.Data/Tsuki.Tests.Data.csproj --no-build --artifacts-path artifacts/K1c --filter "FullyQualifiedName~Tsuki.Tests.Data.Media"
#   28 / 28 passed, 49 s (E1's 14 + 14 new; the ATRAC9 all-streams test is 14 s of it, the movie tests 37 s)
dotnet test tests/Tsuki.Tests.Synthetic/Tsuki.Tests.Synthetic.csproj --no-build --artifacts-path artifacts/K1c
#   357 / 357 passed (the whole project, other packages' tests included)
```

Results of the 14 new Media data tests:

| test | measured |
|---|---|
| `ThePrologueDecodesToTheOracleBitsAndCounts` | 11,660 frames (584 MD5-checked), 18,655,997 samples, first audio sample 0, audio SHA-1 equal; 18.5-19.0 s, 630 fps, 16 decoder threads |
| `TheTitleBackgroundLoopsAtItsLoopPointWithAContinuousTimeline` | `titlebg1` from 118 s: frame 3599 is followed by index 3600 = file frame 241 (8.0333 s), 58 frames after the wrap equal the CLI's MD5, pts = index / 30 throughout |
| `TheHostPlaysTheLogoMovieOnTheAudioClockAndEndsIt` | `TM_CI` (movie 38) through `MovieHost` + mixer pulled in 10 ms periods, real time: ended after 6.07-6.09 s, **180 of 180 frames shown, 0 holds, 0 early switches, 0 dropped**, final clock 6.005-6.016 s, 18 frames MD5-checked, audio clock used |
| `AMovieWithAudioPlaysOnTheFrameClockToItsEndWhenThereIsNoMixer` | `prologue_en` from 385 s with `mixer: null`: ends after 3.78 s at 388.68 s (before the fix below the demuxer blocked on the full 4 s audio ring and the video froze) |
| `WhenTheMixerNeverPullsTheMovieFallsBackToTheFrameClock...` | no pulls: frame clock after 0.4 s, `TM_CI` ends after 6.49 s |
| `WhenTheAudioDeviceStallsMidMovieTheFrameClockTakesOver` | pulls for 2 s then none: ends after 6.59 s at 6.00 s |
| `ATruncatedMovieEndsInsteadOfHangingTheScript` | first 1.5 MB of `TM_CI`: 1 corrupt packet dropped, 0 failures, ends at 2.72 s |
| `StoppingAMovieMidPlayJoinsBothThreadsPromptly`, `ALoopingMovieNeverEnds...` | stop in 0 ms with both threads waiting; a looping movie reports `Playing` and never `Ended`; opening a slot twice replaces the movie |
| `EveryStepOfTheReferenceTraceIsReproduced` | 910 scenarios, **100,177 steps (36,971 statements), 1,363,213 calls**: corpus 514 scripts / 55,127 steps (22,037 statements of the 549 scripts' audio commands), ff, scan, skip, vsync 3, two non-default configs (65 scripts each), voice gate (5 configs x 4 flag states x 20 groups), ducking (5 configs x 1,477 frames) |
| `TheSlicePlayOfMoonSe03028FadesInToThirtyPercentInFourHundredEightyFrames` | H3a's `slice/models/sound_sepl.json` (SHA-1 checked against the slice manifest): statement calls, volume bits of 600 frames, final record, `target_reached_frame` 480, the 26 records |
| `EverySeLookupTheReferenceTraceRecordedIsTheDataLayersLookup` | the 1,314 (name, flags) lookups the Python model made equal K1a's `SeNames.Lookup` |
| `AnSeFadeInThroughTheModelThePlayerAndTheMixerEqualsTheHandBuiltMixerRun` | real vse #598, 560 steps x 800 frames: 0 samples differ from a mixer driven by hand-written `MixerCommand`s; final gain = 40 x 30 x 0.01 x 0.01 |
| `BgmSeVoiceAndASystemSoundPlayTogetherThroughAnSdlPullStream` | bgm #33 (2 s fade-in), looping SE with pan, voice #15616, system sound 0, SDL pull stream in 480-frame pulls: peak 0.412, voice ended after 2.20 s (clip 2.199 s), 4 plays, no failures |

## 4. Findings

### 4.1 PS4 versus Switch (knife)

| area | knife (Switch) | PS4 (ours, used) |
|---|---|---|
| movie remap (`_TOMP`) | `movie::localized` (sub_41260) | identical to `MovieTable.Remap` (eboot 0x5E280) for all 134 ids x 5 languages: confirmed |
| title background loop point | 240 / 30 s = 8.0 s | **8.0333 s** (f32 at eboot VA 0x2186E4, frame 241): used; verified on `titlebg1` |
| movie sprite fade | alpha +0.05 per frame in, -1/30 per frame out (sub_1EC0) | not researched on the PS4; belongs to the stage / draw code, not the media host |
| `_MPLY` fade of the old track | `1666.7 / (key 11 or key 1)` points per frame: the fade takes that many ms only from full volume | `scr_bgm_vol * 1000 / 60 / ms`: ms at any volume (as knife's Witch build) |
| `_MPLY` fade-in of the new track | to volume `v` takes `v x fade` ms | takes `fade` ms at any volume |
| music-room "heard" bit | tracks <= 0x417 | tracks <= 0x207 (model, verified under emulation) |
| voices | two players, alternating; `_VPL2` leaves the last voice playing | four streams `2 + (++n & 3)`, all four stopped before each voice (VoiceRequest 0x8F60); we stop all four for `_VPL2` too (the PS4 doc is silent) |
| `_VPL2` | plays at once, is not the replay voice | same; docs 3.6: "play immediately, same gate and gain, clear the pending voice" |
| BGM ducking under voice | Witch build only | Tsukihime PS4 config +0x1C (default off): 256 -> 128 at 4 per frame, back at 2 per frame |
| system sounds | `play_sys` over 4 rotating slots | HE-VAG pair on 4 rotating voices 0x2C..0x2F, left hard left, right hard right |
| `_MSTP` (unused by scripts) | Witch: fade at 4.1666665 points per frame | PS4 rate unknown ([H] in the model); the knife number is used (plain constant, named in the comment) |
| movie `hold` (the title movie pauses under a submenu) | `Movie::hold` | no PS4 evidence; not implemented |

No knife file was translated, so `docs/THIRD_PARTY.md` has no new row. The only carried-over value is the plain constant 4.1666665 (`_MSTP`).

### 4.2 Other findings

- **A full audio ring starves the video (new, fixed).** S2 always had a reader. With no mixer, a stalled device or a mixer that never pulls, the 4 s PCM ring fills,
  the demux thread blocks in `PcmRing.Write` and the video packets behind the audio in the file never arrive: any movie with audio over 4 s froze. Fix: `MoviePipeline.DiscardAudio()`
  (set when there is no mixer, or when the audio clock times out); covered by three tests.
- **Corrupt packets.** FFmpeg reports them at `avcodec_receive_frame` with frame threads; they are dropped (counted in `PacketErrors`), as ffplay does.
- **Doc naming.** `ConfigBlock.Word1C` ("constant (movie path)", adv_state.md 9.2) is the ducking switch: the reader 0x68A70 contains the ducking code 0x694B9 (sound_runtime.md 9).
  `SoundConfig.Duck` reads it under that name. Whoever owns the config field map should rename it.
- **Float products are not the decimals one expects.** `0.01f` is 0.00999999977, so `40 * 0.01f` is 0.39999998f and `10 * 0.01f` is 0.099999994f (both are exact ties that round to even).
  The Python model does the same, so the BGM at volume 40 plays at 0.39999998; tests that compare gains use the same float product.
- **The model's `is_playing` default.** The H3a trace calls every idle stream's `stop` + `close` on every update because it reports every stream as playing. The C# model keeps that default
  for the reference traces; a running game passes `AudioCommandPlayer.IsPlaying`, which is false for idle streams.

## 5. Open issues

1. **No device run.** Playing through `AudioOutput.OpenDevice` was not run (no audio device in tests). The path is the same callback as the pull-mode tests; T7 measures the full mix under load.
2. **BGM decode hitch.** `AudioCommandPlayer` decodes on the calling thread by default (a 172 s BGM takes about 0.1 s). `AudioPlayerOptions(AsyncDecode: true)` and `Prefetch` exist and are covered by synthetic tests with a blocking clip source; they were not timed with real BGM in a running game (M2a).
3. **Virtual time and a missing device.** `SoundModel.IsPlaying` says a started voice plays until the audio layer says it stopped. With no device (the mixer is never rendered) or in the explorer's virtual time, the integration must pass `_ => false` or the voice state (`_VCWT`, ducking, auto mode) never returns to 0.
4. **Unmodelled engine details** (the Python model has them open as well): the `_VPLY` scan-mode branch (docs 3.6 "if scan: return (yield)") and the extra skip-mode term `0x16541E8` of the voice gate; `_SEPN`, `_SEP3`, `_SEAC` (no script uses them); the voice "loading" state 1 (the PS4 starts the stream one frame after the request, we start it in the same call); the voice elapsed counter behind `AutoModeVoiceMilliseconds` (`elapsed x 17 ms`, counted in `dt` per update [H]).
5. **Translation limits** (documented on `AudioCommandPlayer`): `set_loop` on an already running voice is not applied (the model sets it on the BGM slot that is about to fade out, so the effect is nil in practice), `stop` with a fade time stops at once (every caller passes 0), `set_flag6` has no equivalent, an SE entry >= 10,000 (section 3 of se_nam, no data on the PS4) is a recorded failure, not a sound.
6. **Looping movies with audio.** The wrap keeps the video timeline continuous; the audio clock counts media frames. They stay in step only if the audio and video of the loop are equally long. The four title loops have no audio track, which is the only use.
7. **Movie pause / hold** (knife's title-movie `hold`) and the movie sprite fade are not part of the host.
8. **Shared files.** I changed only my own paths and the additive counters in `Tsuki.Media/Mixer/AudioMixer.cs`. During the session other packages' in-progress sources broke `dotnet build src/Tsuki.slnx` for about an hour (`Tsuki.Engine/Stage`, new tests in `Tsuki.Tests.Data`); I used a private runner project under `scratch/` until the tree built again, and deleted it. The final tree builds with 0 warnings and 0 errors.
9. **Golden size.** `samples/golden/audio/sound/sound_trace.json` is 28.7 MB (local only). The `movie` part needs the ffmpeg CLI on PATH (49 s); both parts are re-run by `python -m oracle.audio --parts movie,sound`. The nightly ATRAC9 part was not re-run (its manifest notes that `oracle/audio.py` changed since).
