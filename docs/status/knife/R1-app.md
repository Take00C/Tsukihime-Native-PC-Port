# R1-app: the app, platform and launcher fixes of the R1 polish review

Fixer "fix-app" (owner of `src/Tsuki.App/**`, `src/Tsuki.Platform/**`, `src/Tsuki.Launcher/**`, `README.md` and the App / Launcher / Platform tests), run on 2026-10-30 on top of
commit 38cc3af plus the uncommitted K19 display package. Builds in `artifacts/fix-app` (Release, `-warnaserror`, 0 warnings), temporary profiles under `scratch/fix-app/`, every
game run hidden and muted with its own `--save-dir`. Nothing is committed. Other fixers worked in the same tree at the same time (engine, core, media); `AppServices.cs` carries their
`sink.AutoSaved` / `sink.SceneStackPushed` hooks and `GameApp.cs` their `StartupFailure` / `PlayerMessage` sentences, which this package kept and built on.

Edits outside the owned folders, each the smallest that the fix needed: `src/Tsuki.Engine/Save/SaveFile.cs` (`ReadWithFallback` reports the primary file's fault together with the
backup's), `Directory.Build.props` (only the four FFmpeg DLLs the game loads are copied), this file.

## Proof (commands run here)

| check | result |
|---|---|
| `dotnet build src/Tsuki.slnx -c Release -warnaserror --artifacts-path artifacts/fix-app` | 0 warnings, 0 errors |
| `dotnet test tests/Tsuki.Tests.Synthetic` | **1112 / 1112** (1104 of the tree at the start, 8 new or extended here: `App/VoicePlaybackTests` 2, `App/HostRebuildTests` 3, `App/AutoSkipTests` 3, `App/SaveCommitFailureTests` 2, `App/VideoMemoryTests` 2 (7 cases), `Launcher/ProfileWriteCheckTests` 3, `Launcher/GameLauncherTests` 2 extended) |
| `dotnet test tests/Tsuki.Tests.Data --filter App\|Launcher` (`TSUKI_GAME_DIR` set; the real program hidden) | **27 / 27** (`scratch/fix-app/data_tests.txt`) |
| `dotnet test tests/Tsuki.Tests.Render --filter PlatformSmokeTests\|WindowPlacementSmokeTests` | **14 / 14** (a real hidden SDL window: the pump with and without a gamepad set, the placement) |
| `TsukiLauncher.exe --selftest` on a fresh temp profile (`scratch/fix-app/launcher/selftest.txt`) | 16 / 16 ok |
| `dotnet publish src/Tsuki.Launcher -c Release -r win-x64 --self-contained -o scratch/fix-app/pub` | 275 files, 235 MB (was 284 files, 271 MB): `licenses\` with `FFmpeg-LGPL.txt`, `FFmpeg-SOURCE.txt`, `SDL3-zlib.txt`, `knife-MPL-2.0.txt`, `THIRD_PARTY.md`; `README.md`; no `*.xml`; only `avcodec-63`, `avformat-63`, `avutil-61`, `swresample-7` of FFmpeg |

## The findings, most severe first

### 1. Script voices never reached the mixer (critical) — fixed

Root cause as the verifier said: `GameHost.Step` runs the script (which queues `OpenHandle` + `Play` of the voice in the host) and then `Sound.Update` → `EndOfVoice`, which asks the
external `is_playing`; `AudioCommandPlayer.IsPlaying` answered false because the runtime performed the host's commands only after the step, so the model emitted `Close` in the same
step and `StopStream` cancelled the pending play before `Pump`. Every voice of the game, on every start path, went this way (the title's New Game too).

Fix: `AppServices.AttachHost` now wires `is_playing` to `IsStreamPlaying`, which first hands the host's queued commands to the player (`PerformHostAudio`, the same method the runtime
calls after the step) and then asks the player; the sound library's "true from the play call on" holds again, as it does on the virtual-audio path. Self-check: `CheckVoicePlays`
counts every voice `Play` that no `Stop` / `Close` of its batch cancelled (`VoicePlaysPerformed`) and the ones the player did not take without recording a decode failure
(`VoicePlaysDropped`, logged once as `verification: audio dead ...`); a hidden run with a drop exits with code 5 (`GameApp.ExitAudioDead`) and the summary line says `voice plays N
(M dropped)`.

Files: `src/Tsuki.App/Services/AppServices.cs`, `src/Tsuki.App/Play/AppRuntime.cs` (uses `Services.PerformHostAudio()`), `src/Tsuki.App/Play/GameApp.cs` (exit code, summary).
Test: `tests/Tsuki.Tests.Synthetic/App/VoicePlaybackTests.cs` (a real `AudioCommandPlayer` over an `AudioMixer` pulled 800 frames per step through the app runtime: the voice is on a
stream of the player, `PlaysPosted` 1, `_VCWT` holds the script for 90..160 steps of a 2 s clip and releases it; a `Close` in the same batch and a decode failure are not counted as
drops). Before the fix the first test fails at `VoicePlaysPerformed == 1`.

Evidence in the real program (`artifacts/fix-app`, `--hidden --mute --lang en --movies real`):
* the reviewer's scene 302 script (`scratch/fix-app/vc302/run.log`): `plays 0 → 1 → 2 → 3 → 4` at the five logs (was `plays 0` at every one), `mixer peak 0.422` (was 0.000),
  summary `voice plays 4 (0 dropped)`, exit 0.
* the 00_02 auto trace (`scratch/fix-app/t270/run.log`, `--fast --movies virtual`, scene 270, A at 120, `--exit-after-ends 1`): `story ended: ReturnToTitle` at step **31,507**
  (wait_model.md 12.3 expects about 34,000 with every voice waited; the reviewer's runs skipped every `_VCWT`), `voice plays 108 (0 dropped)`, exit 0.
* K15's `movies.py` set was not re-run (its 27 jobs are an hour of paced runs on `scratch/K15/app`); the two runs above cover the code path it would exercise, and the self-check
  now fails any hidden run that regresses.

### 2. Language change in the first second of NEW GAME leaves a dead screen (major) — fixed

`AppServices.RebuildHost`: when the story is running and the staging is empty (nothing to carry), the new host starts the old host's scene over (`BeginStory(old.SceneId)`, or
`BeginReplay(G008)` for a gallery replay); it stayed `NotStarted` before. Safety net in `AppRuntime.Step`: a story whose host is `NotStarted` is sent back to the title
(`Services.ReturnToTitle()`, logged `story host not started: back to the title`), and the pending title is now kept until the menus have closed (`!Ui.Active &&
Services.TakePendingTitle()`; it used to be consumed and lost while a menu was up).
Files: `src/Tsuki.App/Services/AppServices.Host.cs`, `src/Tsuki.App/Play/AppRuntime.cs`. Test: `tests/Tsuki.Tests.Synthetic/App/HostRebuildTests.cs` (a scene waiting in `_WTTM`
with an empty staging: after the language change the host is Running on the same scene; a page on show is still carried; the safety net puts the title up after a failed replay
restart). The first test fails before the fix (`Phase NotStarted`).

### 3. "Auto Skip Cleared Scenes" and "Show Confirmation Message" did nothing (major) — fixed

`AppRuntime` subscribes to `GameHost.AutoSkipOffered` (on the first host and on every `HostReplaced`) and answers it as knife's `ui.rs` 6177-6184 does: with `Config.SkipConfirm`
on, `ConfirmDialog(ConfirmKind.NextScene)` over the story (yes → `Services.NextScene()`); off, `NextScene()` after a 4-frame countdown. The offer waits while a menu is up and is
dropped for a replay. Files: `src/Tsuki.App/Play/AppRuntime.cs`. Test: `tests/Tsuki.Tests.Synthetic/App/AutoSkipTests.cs` (`_RTST(540);_RDED(540)` then `_RDAS(540)`: option
off → nothing; confirmation on → the dialog and the story standing still; confirmation off → the next unread page without a click after the countdown).

### 4. A save folder that cannot be written lost every save silently (major) — fixed

`AppServices.Commit` returns false when the write threw (true without a save file); `Save`, `QuickSave`, `Copy`, `Delete` and `AutoSaveForTitle` return that result, so the save
screen shows its failure text (sysmes 189) and the quick-save banner stays away; the slot stays in memory. The first failure raises `SaveFailed(path, error)` once; `GameApp` shows
it in a message box (not hidden) naming the path and the OS message; every failure is logged with the path. Launcher: `SaveTools.ProbeWritable` creates and deletes `.write_test`
in the profile folder (`LauncherProfile.Read` fills `SaveSummary.WriteError`), the ready check's Saves row becomes a warning with Fix → Saves ("The save folder cannot be written:
..."), and a `save.dat` that is a folder is said so. Files: `src/Tsuki.App/Services/AppServices.cs`, `AppServices.Save.cs`, `src/Tsuki.App/Play/GameApp.cs`,
`src/Tsuki.Launcher/Core/SaveTools.cs`, `LauncherProfile.cs`, `ReadyCheck.cs`. Tests: `App/SaveCommitFailureTests.cs` (save.dat as a folder: every save reports false, the slot
stays, one `SaveFailed`, no `.tmp` left), `Launcher/ProfileWriteCheckTests.cs`.

### 5 and 7. No single-instance guard (two majors) — fixed

Game: `GameApp.Run` takes a named mutex derived from the profile folder (`SingleInstance.TryAcquire`, `Local\Tsuki.App.<hash of the full path>`, so parallel verification runs on
their own `--save-dir` never collide); a second game on the same profile prints the reason to stderr, shows "TSUKIHIME is already running." (not hidden) and exits with code 3
(`GameApp.ExitAlreadyRunning`). Launcher: `GameLauncher.IsGameRunning()` (any `Tsuki.App` process) makes PLAY toast "The game is already running." instead of starting a second copy;
exit code 3 toasts "already running in another window" instead of "closed with an error"; a second launcher brings the first one's window forward (`Local\Tsuki.Launcher`
mutex, `SetForegroundWindow`) and leaves. Files: `src/Tsuki.App/Play/CrashLog.cs` (new: `CrashLog`, `SingleInstance`), `GameApp.cs`, `src/Tsuki.Launcher/Core/GameLauncher.cs`,
`LauncherModel.cs`, `LauncherApp.xaml.cs`. Evidence: two hidden instances on `scratch/fix-app/twice/save` 4 s apart: the second `exit 3` with `Tsuki.App: the game is already
running on the profile ...` (`scratch/fix-app/twice/second.stderr.txt`), the first ran to its `done:` line.

### 6. The title's background movie kept decoding behind the whole story (major) — fixed

`StopTitleMovie()` is called where a story takes the screen: `AppRuntime.Step`'s "a story that started under the title takes the screen" branch (next to `Ui.Clear()`),
`AppServices.StartScene`, `Load` and `FlowchartJump`. The title screen's `FadeIn` starts it again when the story ends. Evidence: the state line now carries `movies N` (open movie
slots); a returning profile with CONTINUE at 1900 (`scratch/fix-app/title/continue.log`, `--movies real`, paced): `movies 1` at 1200 under the title, `movies 0` at 2100 and 2500 in
the story. Files: `src/Tsuki.App/Play/AppRuntime.cs`, `Services/AppServices.Story.cs`, `AppServices.Save.cs`, `GameApp.cs` (state line).

### 8. Launcher window minimum did not fit 1080p at 150 % (major) — fixed

`MainWindow.xaml`: `MinWidth 960`, `MinHeight 600`; the constructor clamps the initial size to the work area minus 24 DIP. Render check: `TsukiLauncher --render --height 600 --page
play` (`scratch/fix-app/launcher/r600/play.png`, read as `play_half.png`): the footer with the status line, Log and PLAY is drawn at the bottom of the 600 px picture.

### 9. A language change drops the history ring (minor) — skipped

Keeping the ring across the rebuild needs an engine-side in-place restore (`AttachHistory` over an existing ring plus a restore of the current record without the temp-slot
round trip, which replaces the ring by design); that is engine work outside this package's folders and not a small change. The behaviour is unchanged.

### 10. Silent exit when the --log file cannot be opened; crash.log location (minor + polish) — fixed

`GameApp.Run` opens the log inside a try: on failure the run goes on with the console, the reason is written to `crash.log` and shown (not hidden). `CrashLog` writes into the
profile folder (`--save-dir`, else `%APPDATA%\Tsuki`), then the `--log` file's folder, then `%APPDATA%\Tsuki`; `Program.Main`'s catch writes it too and shows the box unless
`--hidden`, so no exit path is silent. Evidence: `--log` into a missing folder (`scratch/fix-app/logtest/`): exit 0, the first line of stdout says the log could not be opened, the
run completes, `scratch/fix-app/logtest/save/crash.log` holds the line (not the user's `%APPDATA%`). Launcher side: the ready check's write probe (finding 4) warns before PLAY.
Not done: redirecting the child's stderr into the launcher's toast.

### 11. Damaged save.dat replaced without notice (minor) — fixed in the game, launcher listing skipped

`LoadStore` quarantines the damaged file as `save.dat.broken-<yyyyMMdd-HHmmss>` (a name of its own), also when the backup saved the profile (so the next commit does not turn the
damaged file into the `.bak`), logs both errors and shows a message box (not hidden): what happened, where the file went, that the launcher's Saves page restores backups.
`SaveFile.ReadWithFallback` reports `"<primary>; the backup: <backup error>"` (the one engine edit). Evidence: `scratch/fix-app/corrupt1/run.log` (`moved to ...save.dat.broken-20261030-031342`),
`corrupt3/run.log` (`the file is too short (0 bytes); the backup: not a Tsuki save file (bad magic)`; the damaged `.bak` stays in place). The launcher's Saves page does not yet
list `.bak` / `.broken-*` as restore candidates (a page and model change beyond this round).

### 12. No process-wide unhandled-exception handler (minor) — fixed

`Program.Main` registers `AppDomain.UnhandledException` (crash.log + message box unless hidden) and `TaskScheduler.UnobservedTaskException` (crash.log, observed); the trim thread
catches every exception, stops trimming and the loop logs `page trimming stopped: ...` once. Files: `Program.cs`, `GameApp.cs`.

### 13. Direct start: a remembered folder that became incomplete (minor) — fixed

`GameApp.Play` opens the game folder in a loop: on `FileNotFound` / `DirectoryNotFound` / `InvalidData` (not hidden) the remembered `game_dir.txt` is deleted when it named that
folder, the core fixer's sentence is shown and the folder dialog asks again (hidden runs still fail with the `StartupFailure`). `LocateGame` accepts the folder itself, its
`CUSA47999` subfolder and the parent of `image` / `data` / `sce_sys`, as the launcher's `GameFolderCheck.Resolve` does, for every candidate and the dialog's choice. Moving
`Resolve` into `Tsuki.Data` was not done (outside the folders; the two copies are 15 lines each).

### 14. Thumbnail grab stall (perf minor) — skipped

The fix is a PBO + fence read-back in `GpuCompositor` (Tsuki.Render) or a change of the thumbnail rule; neither is a small edit in this package's folders.

### 15. Start-up: 1.1 s gamepad init before the first frame (perf minor) and 16. black window for 1.6 s (polish) — fixed

`PresentBlank()` clears, swaps and pumps once right after the GL context exists and between the set-up phases (a black, responsive window at once); the gamepad set is created
behind the first presented frame (`EventPump.Gamepads` and `PcInput.Pads` are set then; the keyboard and mouse work from the first step; `FixedStepClock`'s lag cap keeps the
stall from bursting steps). Evidence (`scratch/fix-app/pads/run.log`, hidden run with real input): `first frame presented at 643 ms` (the reviewer's phase stamps had the loop at
1793-1958 ms), then `gamepads: 0 connected, subsystem ready in 1166 ms`. Files: `GameApp.cs`, `src/Tsuki.Platform/EventPump.cs`, `Input/PcInput.cs`.

### 17. Movie YUV conversion on the CPU (perf minor) — partly

The decoder threads are now `clamp(ProcessorCount / 4, 2, 6)` per movie instead of FFmpeg's default of up to 16 (`MovieHostOptions.VideoThreads`); the title movie played 268
frames in the check run. The shader-side conversion (or a SIMD rewrite of `YuvConverter`) is left for a dedicated change.

### 18. Save commit flush on the main thread (perf minor) — skipped

A background writer cannot return the write's failure to the save screen, which finding 4 now relies on; dropping `flushToDisk` is an engine change that trades durability. Left as is
(7..9 ms per commit on this machine).

### 19. Texture cache cap fixed at 1536 MiB (perf minor) — fixed

`Tsuki.Platform/VideoMemory.cs` (new) reads the adapters' `HardwareInformation.qwMemorySize` from the display class registry key (the adapter whose `DriverDesc` occurs in the
GL_RENDERER string, else the largest); `GameApp.OpenRenderer` sets the cap to a third of it between 512 and 1536 MiB and the spare pool to a sixth of the cap, and logs it:
`texture cache: 1536 MiB (adapter "AMD Radeon RX 9070 XT" has 16304 MiB dedicated)` on this machine (unchanged there; 1365 MiB on a 4 GB card, 683 on 2 GB). Test:
`App/VideoMemoryTests.cs`.

### 20. Published folder: no licences, dev files, unused DLLs (minor) — fixed

`Tsuki.Launcher.csproj` publishes `licenses\FFmpeg-LGPL.txt` (the package's LGPL v3 text), `FFmpeg-SOURCE.txt` (build, package, source URLs; new file `Licenses/`), `knife-MPL-2.0.txt`
(from `ref/knife/LICENSE`, when present), `SDL3-zlib.txt` (new), `THIRD_PARTY.md` and `README.md`, and deletes the `*.xml` doc files after the publish (the `.pdb` files stay:
crash.log keeps its line numbers). `Directory.Build.props` copies only the four DLLs `FfmpegLibrary.RequiredDlls` names. `HelpPage.xaml` names the four libraries and the
`licenses` folder. Proof: the publish row of the table above.

### 21. Confirm overlay does not trap the keyboard (minor) — fixed

`Shell.xaml`: the content grid is disabled by a `DataTrigger` on `ConfirmVisible`, the overlay has `KeyboardNavigation.TabNavigation="Cycle"`, the buttons are named, and
`Shell.OnModelChanged` focuses Cancel when the question opens (Enter answers through `IsDefault`, Esc through `IsCancel`).

### 22. Invisible keyboard focus, unnamed switches (minor) — fixed

`Theme.xaml`: `IsKeyboardFocused` triggers on the AccentButton (glow), LinkButton (underline), PlayButton (`Rim` border), CaptionButton, the NavList, Segmented, MonitorList and
TileList items (`#9CD2FF` border) and the Switch (track border and glow); `AutomationProperties.Name` on the Fullscreen / VSync switches, the window-size, monitor, game-language and
button-prompt lists (`DisplayPage.xaml`, `PlayPage.xaml`).

### 23. A rejected folder leaves a stale Ready check (minor) — fixed

`LauncherModel.SetGameFolderAsync` keeps the remembered folder as the model's folder when the typed one is not the game (only `FolderText` and `FolderMessage` change), so the
Ready check, the banner and PLAY keep describing the folder the game would start from. The self-test's "a folder that is not the game is not remembered" still passes.

### 24. Window-size presets larger than the monitor (minor) — already done by K19

K19 lists only the presets that fit the chosen monitor's work area (`LauncherModel.WindowSizes`, `WindowFit.PresetsFor`) and the game clamps the size (`WindowPlacement.Place`);
`SettingsTests.TheLauncherOffersOnlyThePresetsThatFitTheMonitor` covers it. No change here.

### 25. README stale text, layout, FFmpeg setup (minor) — fixed

The opening paragraph says the port is playable and what `src/` holds; the layout block lists `src/` (one line per project), `tests/`, `third_party/ffmpeg/` and `artifacts/`; the
"How to run" block starts with `tools\setup\ffmpeg_dlls.ps1` and names `TSUKI001`.

### 26. Key table gaps (minor) — fixed

`KeyTable.Rows` gains H, "Mouse wheel / Page Up / Page Down", M and Delete; the README's input table gains the H row (the other keys were there). `GameLauncherTests` now also asserts
that every `InputMapper.Bindings` entry that is not a `Mouse:` or `Gamepad` row is on the Controls page (`KeyTable.IsKeyboardBinding`).

### 27. Launcher headless usage MessageBox (polish) — fixed

`LauncherApp.OnStartup` parses every argument first; with `--render` or `--selftest` an unknown argument goes to stderr and to `render_error.txt` / the self-test report, exit 64,
no box (`scratch/fix-app/launcher/bad/render_error.txt`: `TsukiLauncher --render ... --bogus` exited 64 at once); `--selftest` is in the usage line.

### 28. Hold busy-spin and present storm (polish) — fixed

While a frame is held for textures the loop waits a millisecond (`Pacing.WaitUntil`) instead of `Thread.Yield`, and the held picture is re-presented at most once per 60 Hz step.

### 29. Frame cadence on 120 / 240 Hz panels (polish) — skipped

A swap interval of `rate / 60` changes nothing at 144 or 165 Hz (the title's cases) and a wrong refresh-rate reading would halve the presents; left for a measured change with a
visible window.

### 30. Banner chip "game folder not set" for an incomplete folder (polish) — fixed

`LauncherModel.GameChip` follows the folder state: NotSet / Missing → "game folder not set", NotTheGame → "not the game folder", else the title id and the version ("unknown" when
param.sfo is missing).

### 31. Launcher comes back Normal after the game (polish) — fixed

`MainWindow` remembers the state before PLAY and restores it; `Activate()` only when the exit code is not 0.

### 32. `--data` PLAY starts the game on the real profile (polish) — fixed

`GameLauncher.Plan` adds `--save-dir <profile>` when the launcher's profile is not the user's (`LauncherPaths.IsUserProfile`); `LauncherApp.Report` writes `launcher_crash.log` into
the `--data` folder. `GameLauncherTests` checks both plans.

## Open points for the lead

1. Findings 9 (history ring across a language change), 14 (thumbnail read-back), 18 (commit flush) and 29 (swap interval) are left as they were, for the reasons above; 11's launcher
   half (listing `.bak` / `.broken-*` on the Saves page) and 10's launcher half (the child's stderr in the toast) are not done.
2. The voice fix changes when the host's audio commands reach the player (inside the step for the batches that precede an `is_playing` question, after it for the rest); the
   order of the commands is unchanged. The K15 `movies.py` set and the full K15 node tours were not re-run in this round.
3. The gamepad set is now created after the first frame: a pad pressed within the first ~1.2 s is not seen, and the loop's clock resyncs once (`FixedStepClock` lag cap).
