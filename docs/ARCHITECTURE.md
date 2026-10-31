# Architecture of the TSUKIHIME Windows port

Decided 2026-10-25. This document fixes the route and the design of the runtime. Every
implementation stream builds against it, and `docs/ROADMAP.md` orders the work. References
like `layers 4.1` mean `docs/formats/layers.md` section 4.1. `RR` is
`docs/RUNTIME_REQUIREMENTS.md`, and `own-engine`, `renpy` and `krkrz` are the three strategy
documents in `docs/design/`.

Aligned with the M0 spikes and the lead's decisions on 2026-10-25 (package L2). Statements that
come from a measurement or a decision cite their source: `S1.md` .. `S4.md` are
`docs/status/spikes/S1.md` .. `S4.md`, `spikes.md` is `docs/status/spikes.md`, `decision N` is
item N of `docs/status/decisions.md`. Section 14 lists every change.

---

## 0. Decision

### 0.1 Chosen route

We are building **a small single-game Windows runtime in C# on .NET 10 ("Tsuki")**. It reads
the user's extracted `CUSA47999` folder directly at run time and interprets the 549 HuneX
scripts itself, the way the PS4 eboot does.

- **Window, input, audio, dialogs.** SDL3 through `ppy.SDL3-CS` gives the window, keyboard,
  mouse, gamepads, the audio device and the folder dialog.
- **Graphics.** OpenGL 3.3 core, through the `Silk.NET.OpenGL` bindings only.
- **Media.** FFmpeg 9.0 LGPL shared DLLs, through `FFmpeg.AutoGen`, decode the H.264/AAC
  movies and the ATRAC9 audio.
- **What it does not use:** no conversion step, no transpiler, no third-party game engine, no
  emulator, no PS4 code and no C/C++ compiler.
- **Base design.** The base is `docs/design/strategy_own_engine.md`, changed by the grafts in
  0.4.

This is "Route B" from the precedent research: a focused runtime for one game that runs that
game's own scripts from the user's own files. That is what the closest prior art does
(david419kr's Mahoyo HuneX reimplementation), and so do the fan ports that actually finished
(tsukiweb, rlvm).

### 0.2 Why

Three judges each scored the routes through one lens. The weights below follow the task's
order: time 0.5, fidelity 0.3, player experience 0.2.

| route | time to the whole game | fidelity | player experience | weighted |
|---|---:|---:|---:|---:|
| **own C# runtime** | 6.5 | **9** | **8.5** | **7.65** |
| Ren'Py conversion | **7.5** | 7 | 6.5 | 7.15 |
| KiriKiri Z conversion | 4 | 5.5 | 4.5 | 4.55 |

The reasons, in priority order:

1. **Time to a complete game, with confidence that nothing is missing.**
   - The hard part of a VN port is knowing exactly what the engine does, and that is done.
     Every format is decoded. The VM, variables, layers and motions, the 35 shaders, the sound
     runtime, the ADV state, boot, title, choices and the gallery all exist as Python models
     checked against the game's own code under emulation. That is about 29,700 lines and 317
     test functions.
   - So porting to C# is mostly translation, and every module gets a differential test against
     its Python twin. Agents do this kind of work fastest and can verify it best.
   - The engine that ships also runs headless in virtual time as the explorer that walks every
     branch. The proof that the game plays "start to finish" therefore comes from the shipping
     code itself, not from a second implementation.
   - Ren'Py does not remove this core. Its own proposal says the Stage compositor, the glyph
     text and the sound rules are custom code inside Ren'Py, "roughly the same engine core the
     own-engine route writes" (renpy 0).
   - What Ren'Py does save is the shell. In exchange it adds:
     - a transpiler;
     - a 60-80 GB, 1.5-3 hour conversion with VP9 and Opus re-encodes;
     - integration with a save/rollback model built around statements, which is not how
       HuneX works;
     - seven spikes in its M0 that can each kill the route.

     Its 4-week estimate holds only if all seven pass, and the time judge called its lead
     narrow (7.5 vs 6.5).
   - Under the time lens, the own route's weak points were late first pixels and missing menus.
     Two grafts remove them (0.4):
     - a golden-data bootstrap lets the renderer, VM and text streams start on day 1, alongside
       the C# data layer;
     - functional menus are built before faithful ones.
2. **Fidelity.**
   - The original H.264 movies and ATRAC9 audio are played untouched, and loops are
     sample-exact.
   - Text uses the game's own glyph pages with the bit-exact layout and furigana. This matters
     most, because the user reads in Japanese.
   - Compositing has the PS4's structure: straight alpha, backdrop copies, and transitions that
     capture the whole render target.
   - Rewind, backlog jump and load behave record for record like the PS4, because state is kept
     in the PS4's byte layouts.
3. **Player experience and legitimacy.**
   - The player unzips about 250 MB, picks the folder and is playing about 2 s later.
   - No 60-80 GB derived copy of the game is left on disk that could run without the original.
   - Compositing runs on the GPU with cheap backdrop blits, and SDL3 handles gamepads.
4. **Tie-break.** Everything up to "what is drawn and played" is deterministic C# that runs
   headless. It can be checked byte for byte against the Python oracle with no GUI in the loop.
   Agents know C#, SDL and OpenGL well.
5. **Precedent, which the user asked us to learn from.**
   - Conversions paid off only when the target engine already implemented the game's macros.
     VitaScenariosTool's FHA/FSN port is the example. The remake's opcodes are HuneX primitives
     with no such library behind them (krkrz 0).
   - Generic or forked engines turned into years-long efforts: impacto, and Umineko's
     ONScripter-RU fork.
   - Focused single-game runtimes finished.

### 0.3 Runner-up, and why not

**The runner-up is the Ren'Py 8.5.3 conversion** (`strategy_renpy.md`). It could reach a
playable game a few weeks sooner, but only if every one of its spikes passes. We do not take it
for these reasons:

- It rebuilds the same HuneX core inside a foreign execution model: creator-defined
  statements, `ui.interact` roll-forward, soft and hard checkpoints, revertable store data.
  The subtle bugs would live in that interplay.
- Ren'Py's desktop FFmpeg has no H.264, AAC or ATRAC9 decoder (checked in renpy-build
  `tasks/ffmpeg.py`), so every movie and sound must be re-encoded. Its `loop` prefix is not
  documented to be sample-accurate.
- The user would convert 60-80 GB over 1.5-3 hours, and the result is a complete game that runs
  without the original files.
- Its performance is unproven: a per-frame CPython Stage, plus one 1080p render-to-texture per
  backdrop-blend layer.
- It depends on pinned Ren'Py internals: rollback identifiers, history entries, roll-forward.
- The Stage, the text and the sound would run in Ren'Py, while hxref would explore separately.
  That means two implementations that can drift apart.

**KiriKiri Z is third.** Its own document says it is "probably no faster than an own runtime".

- The maintained Wamsoft fork has no binaries, so it needs VS2022 and vcpkg.
- The whole core would be rewritten in TJS, which agents know poorly.
- It replaces the game's glyph pages with a TTF font.
- Its universal transition is a linear ramp, not HuneX's smoothstep, unless a C++ plugin is
  built.
- CPU compositing at 1080p is the route's own kill risk.

### 0.4 Ideas grafted from the other proposals and the judges

| idea | from | where |
|---|---|---|
| Golden-data bootstrap: one data interface with two sources, so pixels and the VM appear in week 1-2 while the C# decoders are still being ported | time judge | 3.3; ROADMAP M2 |
| Functional-first menus: every screen of engine 8.3 exists in a plain layout drawn with the game's own art, before faithful layouts are extracted | Ren'Py (default screens) | 9.2 |
| Numbered M0 spikes with written thresholds and a planned fallback for each | Ren'Py M0 | ROADMAP M0 |
| Audio reconciler as an invariant after load, rewind and backlog jump: loops come back and one-shots are never replayed | Ren'Py 4.3 | 7.4 |
| Player-shell conventions: wheel backlog, Ctrl skip, H hides the window, F5/F9, Alt+Enter, confirm on quit, rebinding, save metadata, atomic saves | Ren'Py | 9.4 |
| Prologue-first work order; the static census and its allow-list run on every build | all routes | 10.2, ROADMAP |
| Cache of the static layers below the first animated or blend layer (dirty-driven redraw) | krkrz 4.3 | 6.3 |
| krkrz `blend_function.cpp` / `blend_functor_c.h` as a second, independent reference for the blend formulas | krkrz 4.4 | 10.5 |
| Local HTML page showing the worst 50 visual diffs side by side, for human review | krkrz 10.3 | 10.5 |
| JSON command channel into the running app (dev builds), after Wamsoft's `-replfile` | krkrz | 3.1 `Tsuki.DevBridge`, 10 |
| The explorer writes real save files at every route start and before every ending, so the human can spot-play endings without reading tens of hours | new (from the round-trip idea) | 10.3 |
| ANGLE fallback in the first release; a GLSL subset compatible with GLSL ES 3.00 from day 1 | experience judge | 6.5 |
| 60 Hz or exact-multiple present mode on high-refresh monitors | fidelity judge | 6.2 |
| `IMovieDecoder` with a Media Foundation fallback | experience judge, krkrz | 7.5 |
| Opt-in local decoded-texture cache, only for prefetch misses on slower PCs | experience judge | 4.3 |
| The UI-layout extraction stream starts in week 1 | all judges | 9.3; ROADMAP |
| A list of PS4 captures the user takes on their console: the only ground truth for menus and for misunderstandings shared by the Python and C# code | fidelity judge | 10.6 |

### 0.5 What the user must install

Two installs, both through winget:

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e
winget install --id BtbN.FFmpeg.LGPL.Shared.9.0 -e
```

- **.NET 10 SDK** (winget lists 10.0.402; LTS until 2028-11). The installed .NET 9 SDK
  (9.0.315) reaches end of support on 2026-11-10.
- **FFmpeg 9.0.1, LGPL v3, shared DLLs.**
  - This is BtbN's `win64-lgpl-shared-9.0` zip; the winget installer SHA-256 is
    `83a824f0...68aa0`. It matches `FFmpeg.AutoGen` 9.0.1.x. The DLLs in use are build
    `n9.0.1-11-ge47273f4d9` (avcodec 63.1.101).
  - **The licence is LGPL version 3, not 2.1.** It is a `--enable-version3` build:
    `avcodec_license()` returns "LGPL version 3 or later" (S1.md 1 and 10 #2; `spikes.md` Machine
    table; decision 10), and the package's own `LICENSE.txt`, copied to
    `third_party/ffmpeg/LICENSE.txt`, is the GNU LGPL version 3 text. The licence notices and
    every other document must say LGPL v3. It is still a build without GPL components (RR 5).
    Whether the DLLs ship in the release stays an M8 question (13; ROADMAP M8).
  - `tools/setup/ffmpeg_dlls.ps1` copies the DLLs from the winget package folder
    (`%LOCALAPPDATA%\Microsoft\WinGet\Packages\BtbN.FFmpeg.LGPL.Shared.9.0_*`) into
    `third_party/ffmpeg/bin/`. That folder is local and never committed. The engine loads
    FFmpeg only from there.
  - The package also registers `ffmpeg`/`ffprobe` command aliases. Gyan's static FFmpeg 9.0.2,
    which the Python tools use, is already installed. If winget warns that an alias exists or
    replaces it, both outcomes are harmless:
    - both builds contain the `atrac9` decoder, and they decode bgm #33 to the same bits (S1.md
      2: 0 of 16,516,096 samples differ between the Gyan CLI, the BtbN CLI and the C# decode);
    - `TSUKI_FFMPEG` can pin the Python tools to Gyan's full path.

**Not needed:** Visual Studio or Build Tools, any C/C++ compiler, Ren'Py, KiriKiri, an
emulator, Mesa.

**Already present and used:** Python 3.11 with `.venv` (numpy, pillow, pytest, numba,
capstone, unicorn), Gyan FFmpeg 9.0.2, git 2.55 and winget 1.29.

**NuGet packages** are restored by `dotnet restore`, pinned in `Directory.Packages.props` **at
the repository root** (decision 6; not under `src/`, because `tests/` is outside `src/` and both
inherit it): `ppy.SDL3-CS` 2026.1002.1 (SDL 3.5.0), `Silk.NET.OpenGL` 2.23.0, `FFmpeg.AutoGen`
9.0.1.1, `Iced` 1.21.0 (x86-64 decoder, for 9.3) and xUnit v3 (`xunit.v3.mtp-off`, so that
`dotnet test <project>` keeps the classic runner). The shared build settings are in the root
`Directory.Build.props` (3.1).

**Actions only the user can take** (approvals, PS4 captures, playtests) are listed in ROADMAP
section 5.

**End users of the finished port need:**

- Windows 10 or 11 x64 and a GPU with OpenGL 3.3. ANGLE is the fallback.
- Their own extracted `CUSA47999` folder whose `eboot.bin` is the analysed build: 12,767,802
  bytes, SHA-256 `c2843415...b918` (RR 3.3).
- About 250 MB for the app folder.

There is no installer, no admin right and no conversion.

### 0.6 Status of the other documents

- **`docs/design/strategy_*.md`** are the evidence for this decision, not plans to implement.
  Where they disagree with this document, this document wins.
  - The Ren'Py and KiriKiri strategies are superseded.
  - `strategy_own_engine.md` stays as detailed background for sections 3-12 below.
- **No older `proposal_*.md` files** exist in `docs/design/` (checked 2026-10-25), so nothing
  else there is superseded. `RUNTIME_REQUIREMENTS.draft.md` was already marked superseded in
  the README.
- **Spec precedence.** The subsystem documents in `docs/formats/` are current: layers,
  compositing, sound_runtime, adv_state, system_flow, gallery, vm_variables, textbox and the
  rest.
  - The "pending research" markers in RR section 8 and FORMATS.md section 7 are older snapshots,
    and those topics have since been delivered.
  - The README's "implementation language has not been chosen" is answered here.

---

## 1. Design principles

1. **One game, one build.** We support CUSA47999 with the analysed eboot and nothing else.
   There is no engine abstraction for other games and no per-game profiles.
2. **The user's folder is read-only and is the only source of game data.** Nothing derived
   from it is shipped or committed (4.4).
3. **The PS4 is the spec, and the Python models are its executable form.** Where a model is
   verified (`[V]`, `[V-emu]`), C# matches it bit for bit. Where it is a hypothesis (`[H]`),
   C# follows the documented stand-in, and the gap stays listed (section 11).
4. **One implementation.** The shipping `Tsuki.Engine` is also the headless explorer, the
   trace generator and the save-round-trip subject.
5. **Determinism.**
   - Logic advances in fixed 60 Hz steps on a frame counter and never reads the wall clock.
   - There is no iteration over unordered collections in logic, and the culture is invariant.
   - Float32 arithmetic follows the engine's operation order, with no implicit FMA.
6. **The state is the PS4's state.** It is kept as byte blocks in the documented layouts
   (8.1).
7. **Logic emits data, never side effects.**
   - Drawing is an immutable display list; sound and movies are command queues.
   - `Tsuki.Engine` never touches GL, audio devices or files other than through interfaces.
8. **Harness before features.**
   - No milestone is accepted on "it compiles" or on a prose description of a screenshot.
   - A human judges feel, not correctness.
9. **Functional before faithful** for every UI screen.
10. **IDs everywhere.** Text is keyed by `$NNNNNN`, voices by HED4 index, pictures by catalog
    index, scenes by scene id. There is no fuzzy matching.
11. **Windows robustness.**
    - .NET UTF-16 paths everywhere, `InvariantGlobalization`, no code-page APIs.
    - Native DLLs are loaded only from the app folder.
    - Tested under an English-locale account and from a non-ASCII path (the FSN REMASTERED
      launch bug).

---

## 2. Spec sources and oracles

| subsystem | spec | Python oracle (`tools/tsuki`, `tools/eboot`) | status |
|---|---|---|---|
| archives, containers, MZX0, LV | containers, archives_ui_movies | `mrg`, `nam`, `mzx`, `sniff` | V |
| pictures, catalog, `_hh_vv`, faces, bands | images, archives_ui_movies 2-3 | `image`, `image_catalog`, `catalog`, `catalog_eboot` | V (SHA-1 decodes) |
| text lines, markup, `$` references | text | `text`, `text_refs` | V |
| script front end, dispatch, readers | script, engine 5 | `script`, `script_tables`, `script_emu` | V (1,083,168 segments) |
| variables, operands, choices' result stores | vm_variables | `vm_vars`, `misc_vm_variables_errata` | V-emu (61,000 fuzz cases) |
| fonts, glyph lookup, text box layout | fonts, textbox | `font`, `font_eboot`, `textbox` | V (175,876 lines, 6,743 messages, 601 golden layouts) |
| scene_state, commit, motions, projection | layers | `misc_layer_state_and_motions` | V-emu, with listed [H] fields |
| blend shaders, transitions, rule slot, STEC/SNX | compositing | `compose`, `gcn`, `misc_compositing_transitions_shaders` | V (35/35 shaders, max error 9.7e-4; 756 STEC cases) |
| ATRAC9 RIFF, loops, HE-VAG | audio | `audio`, `audio_hevag` | V (17,925 streams; 19/19 vag) |
| channels, tags, fades, ducking, voice gate | sound_runtime | `misc_audio_runtime_semantics.SoundModel` | V-emu (0 differences on the audio corpus) |
| history ring, rewind, backlog, saves, auto/skip, config, input actions | adv_state | `misc_adv_system_state` | V-emu |
| boot, title, choice screen | system_flow | `misc_boot_title_choice_flow` | V-emu |
| gallery, flowchart, unlock bits, ARCHIVE replay | gallery | `misc_gallery_extras_flowchart` | V-emu |
| eboot tables, build check | engine, RR 3.2-3.3, FORMATS 5.4 | `tools/eboot/*`, `catalog_eboot`, `font_eboot` | V |

---

## 3. Components

### 3.1 Projects (one owner stream each, see ROADMAP 1)

```
Directory.Build.props      (repository root) net10.0, Nullable, TreatWarningsAsErrors,
                           InvariantGlobalization, deterministic build; defines TSUKI_DEV in every
                           configuration except Release; the TsukiUsesFFmpeg opt-in that copies
                           third_party/ffmpeg/bin/*.dll next to a project's output
Directory.Packages.props   (repository root) pinned package versions (0.5)
src/
  Tsuki.slnx       the solution: the projects below, the three test projects and the spikes
  Tsuki.Data       pure C#. Ports of the tsuki readers: HED8/HED4/NAM32/NAM8/MRG/mrgd00, MZX0
                   (+ XOR 0xFF scripts, LV wrapper), MZP (PAL8/PAL4/HEP8/RGB565+LO+A, borders,
                   tile flags), catalog + picture names + NAM search, script_text, allscr +
                   labels, font pages, SELF/ELF reader, every eboot-only table (RR 3.2), ui_text,
                   UI-layout extractor (9.3), build check (RR 3.3)
  Tsuki.Engine     pure C#, deterministic, headless. Script VM + 94 command handlers + markers,
                   variables, scene_state/records/tweens/motions/projection, text expansion +
                   layout + reveal, SoundModel, ADV state (history ring, scene stack, slots,
                   rewind, backlog, auto/skip, read tracking, config), boot/title/choice state
                   machines, gallery/flowchart/unlock model. Output per 60 Hz step: DisplayList +
                   AudioCommand[] + MovieCommand[]
  Tsuki.Media      FFmpeg.AutoGen: ATRAC9 RIFF decode from memory (no demuxer and no custom AVIO:
                   the `atrac9` decoder takes the 12-byte extradata and one 512-byte superframe
                   per packet, S1.md 1), MP4 demux + H.264 + AAC on a thread; HE-VAG decoder
                   (port of audio_hevag.py); float mixer
  Tsuki.Render     Silk.NET.OpenGL: texture cache, display-list executor, GLSL shaders, blend
                   states, capture surfaces, rule wipes, YCbCr pass, glyph quads, offscreen
                   capture to PNG
  Tsuki.Platform   SDL3: window + GL context, events -> PS4 button word + pointer, audio stream,
                   folder dialog, message boxes, user folders
  Tsuki.Ui         screens: state machines + draw lists (9); the small UI kit for tier-1 screens
  Tsuki.App        Program.cs: first-run setup, main loop, error handling, crash log
  Tsuki.Cli        verify | dump | trace | explore | render-checkpoint | decode-all | save-check
  Tsuki.DevBridge  dev builds only: JSON command channel into the running app (10.1)
  Spikes/          S1.AudioLoop, S2.MovieSync, S3.Blend1080, S4.BigPicture: the throwaway M0
                   spikes (reports in docs/status/spikes/). They are part of the solution build
                   (0 warnings) but not of the shipping build, they stay as the working
                   prototypes the wave-2 packages lift code from, and they are deleted once M2
                   is accepted (decision 6)
tests/
  Tsuki.Tests.Synthetic   no game data: CI anywhere
  Tsuki.Tests.Data        needs TSUKI_GAME_DIR + samples/golden; skips cleanly otherwise
  Tsuki.Tests.Render      needs a GL context (the local GPU)
tools/oracle/      Python: writes golden files from the user's copy (10.1); replays C# traces
tools/setup/       PowerShell: FFmpeg DLL copy, environment check, hook install
third_party/ffmpeg LOCAL ONLY (gitignored)
```

Dependency rules, checked by `ProjectRulesTests` in `Tsuki.Tests.Synthetic`, which reads the
project references and packages:

- `Tsuki.Data` and `Tsuki.Engine` reference no native package and make no P/Invoke.
  `Tsuki.Data` may set `AllowUnsafeBlocks` for memory-mapped access and the MZX0 hot loop
  (decision 5); that adds no P/Invoke and no native package.
- `Tsuki.Engine` depends on `Tsuki.Data` only through the contracts in 3.2.
- Only `Tsuki.App`, `Tsuki.Cli`, the test projects and, until they are deleted, the spikes
  reference `Tsuki.Render`, `Tsuki.Media` or `Tsuki.Platform`.
- Each native package belongs to one library: `ppy.SDL3-CS` to `Tsuki.Platform`, `Silk.NET.*`
  to `Tsuki.Render`, `FFmpeg.AutoGen` to `Tsuki.Media`. Test projects and spikes are exempt.
  A package that needs a seam in another owner's library (for example shader compilation, or
  FFmpeg loading) agrees a small interface with that owner.
- Projects do not override the settings of the two root props files (checked by the same test).

### 3.2 Contracts (written in M0 by the lead, changed only by the lead)

| contract | shape |
|---|---|
| `IGameData` | Build info; eboot tables; `Script(k)` (raw bytes + segment offsets); `TextLine(lang, id)`; `FontPage(lang, page)`; `Picture(archive, entry)` -> decoded tiles (RGBA + rects + flags); `Audio(kind, index)` -> float PCM + `fact`/`smpl` loop; `Movie(id)` -> path; `UiSheet(set, lang, child)` |
| `Statement`, `IHost`, `StepResult` | the VM decodes `Statement { name, offset, raw args }` and calls `IHost.Dispatch`, which returns `Continue`, `Yield` or `Halt`; resuming continues after the statement, so no command runs twice (Mahoyo port design) |
| `DisplayList` | immutable, double-buffered list of `DrawCmd { kind, target, texture ref (slot / tile / glyph page / capture), 4 vertices + UVs, colour, effect word, blend state, clip }` in PS4 order |
| `AudioCommand`, `MovieCommand` | start, stop, gain/pan ramps and loop info per stream, as SoundModel outputs them; movie open/stop/clock |
| state blocks | `SceneState` (0x1A08 bytes), `HistoryRecord` (0x3A10), `SoundChannelRecord` (0x34), `ConfigBlock` (0x140), `SystemBlock` (0x18AC), `Vars` (F[1200], Z[1400], G[2000]), each a byte array with typed accessors generated from the field maps |
| `InputFrame` | PS4 button word, edges, auto-repeat, pointer in 640 x 448 space, plus our own extra actions (9.4) |

### 3.3 Two data sources behind `IGameData`

- **`GameFolderSource`** ships. It reads the CUSA47999 folder with the C# decoders.
- **`GoldenSource`** exists in dev builds only. It reads what `tools/oracle` exported to
  `samples/golden/`: raw RGBA tiles with JSON headers, float PCM, segment lists, text,
  layouts and tables.

`GoldenSource` lets the VM, text, render and audio streams work from day 1, before the C#
data layer exists. While both exist, `Tsuki.Tests.Data` runs every consumer test against both
sources, and they must agree. `GoldenSource` is compiled out of release builds.

### 3.4 Threads and the frame loop

```
main thread   SDL events -> InputFrame
              fixed 60 Hz logic (accumulator): Engine.Step()
                 ADV: ScriptStep = up to 1000 segments, stop at the first yield (engine 5.2);
                      fast-forward = repeated ScriptStep calls in one step (adv_state 6)
                 timers: reveal, window fades, tweens, motions, shakes, waits, FADS progress
                 SoundModel.Update(1) + ducking -> AudioCommand queue
              -> newest DisplayList -> GL executor -> present (6.2)
workers       picture decode (prefetch queue), voice/SE decode, save writes
movie thread  FFmpeg demux/decode -> frame queue (Y, U, V) ; AAC -> mixer
audio thread  SDL3 stream callback -> Mixer.Render(<= 1024 frames, float32 stereo, 48 kHz)
```

`Engine.Step()` is a pure function of state and input, so the explorer and the tests run it
as fast as the CPU allows ("virtual time").

**Tick time and the cadence lock (spike S2; S2.md 7 and 9 #3).** Anything that samples a clock
at a 60 Hz tick, which in practice is the movie frame choice reading the audio clock (7.5),
follows two rules:

1. **The accumulator gives each tick its nominal time.** The loop is
   `while (now >= nextTick) { Tick(nextTick); nextTick += 1/60 s; }`, and the tick reads the
   clock at `nextTick`, not at `now`. If it read the clock when the present loop reached it,
   the sample would lag its grid time by 0 to 7 ms, in a 5-tick pattern on a 144 Hz monitor
   (144 / 60 = 2.4). Whenever a frame boundary fell inside that lag, a 30 fps frame got 1 or 3
   ticks instead of 2: judder with no dropped frame. Measured with the "now" design: 89 of 299
   frames in the worst run, 27 of 899 and 13 of 779 in others. The nominal time leaves only
   the clock's own jitter, about +-0.6 ms.
2. **A cadence lock of 4 ms.** A frame that has had one tick keeps a second one when the next
   frame became due less than 4 ms ago. A frame that has had two ticks gives way up to 4 ms
   early. Without the lock a boundary is still crossed within a run: the audio device clock
   runs about 50 ppm fast against the system timer, which moves the phase by 19 ms over the
   388.7 s prologue, more than one tick.

Result on `prologue_en.mp4` (nominal time plus the lock): every frame had exactly 2 ticks (1,348
in the first 45 s and 1,308 in the last 43.7 s, plus the first frame, which waits for the audio
start); 0 dropped, 0 late; the audio clock stayed within 1.10 ms of the shown frame's interval;
the last frame was -2.08 ms from the clock (L's re-run: -0.87 ms; `spikes.md` S2). Both rules are
pure functions of (frame pts list, clock, ticks shown so far), so `Tsuki.Media` tests them
with a synthetic clock and no GPU. The frame choice is shell code (`Tsuki.Media`, driven by
`Tsuki.App`). `Engine.Step()` neither selects frames nor reads the audio clock: it gets the
`MovieClock` report (playing, ended, position) once per step, and `Ended` releases `_TOMW`
(the explorer ends a movie as soon as it is opened, 10.3).

---

## 4. Reading the user's data (no conversion)

### 4.1 First run

1. The game folder comes from `TSUKI_GAME_DIR`, or else a native SDL folder dialog asks for
   the CUSA47999 folder. The folder is only ever opened read-only. No build carries a
   hard-coded game path: every program, tool and test reads `TSUKI_GAME_DIR` (or its `--game`
   argument) and says so when it is missing (decision 3; the earlier dev-only default path was
   removed before publication, `docs/PUBLISHING.md` P1-P6).
2. Build check (RR 3.3).
   - The eboot must be 12,767,802 bytes with the expected SHA-256 (about 30 ms).
   - The structural probes and the FORMATS 2 counts must match: 12 triplet archives with
     49,620 entries, allscr.mrg with 552 sections, script_text with 5 x 43,969 lines, 16,464
     voices and 134 movies.
   - On any mismatch, show a clear message (ja + en) through a native message box, and stop.
     We never guess addresses.
3. Extract the eboot-only tables (RR 3.2) and the UI draw lists (9.3) into memory. This takes
   well under a second.
   - No cache is written: the tables are small and the extraction is redone on every start.
     (The original plan allowed an optional cache under `%LOCALAPPDATA%`; nothing uses one.)
4. Locations, as built (the original plan said `%APPDATA%\TsukihimePort\settings.json` and
   `%USERPROFILE%\Saved Games\TsukihimePort\`; the README's "Saves and settings" is the reference):
   - the 140 save slots, the system data and the config in one file, `%APPDATA%\Tsuki\save.dat`
     (the previous generation as `save.dat.bak`; `--save-dir <dir>` chooses another folder);
   - the PC port's own choices in `%APPDATA%\Tsuki\settings.ini` beside it, the remembered game
     folder in `game_dir.txt`, the run-time icons in `icons\`, the logs `last_run.log` and `crash.log`.

Every later start reaches the first boot screen in about 1-2 s.

### 4.2 What is read when

| data | access | cost |
|---|---|---|
| HED/NAM indexes | loaded whole at start | a few MB |
| `allscr.mrg` | current script section: MZX0, then XOR 0xFF, segment offsets precomputed | about 1 ms per script |
| `script_text.mrg` | offset table + blob for the current language (plus ja for dual display) | about 2.5 MB per language |
| `ft_<lang>.mrg` | 65 pages uploaded as 1024 x 1024 textures at start and on a language change | about 130 MB VRAM |
| pictures | positional read of one entry -> MZP -> MZX0 tiles -> RGBA -> one texture per tile, on workers; STEC/SNX recolour applied on the CPU at build time, as on the PS4 (compositing 9), with the effect in the cache key | about 10-20 ms for a 2,070 x 1,230 backdrop. Measured (S4.md 4, 6): a typical scene (backdrop + 3 sprites) is about 3 ms of read and 6-14 ms of decode on 16 workers; the largest picture (11,220 x 7,650, 343 MB RGBA) takes 32 ms to read and decode but 22 frames to upload, so it must be prefetched (4.3) |
| voice | HED4 entry -> LV + MZX0 -> RIFF -> FFmpeg atrac9 -> float | under 10 ms |
| SE | decoded on first use, LRU | small |
| BGM | decoded to float in the background on `_MPLY`; two slots resident | under about 100 MB per track |
| movies | streamed from `image/movie/*.mp4` | a fraction of one core |

### 4.3 Prefetch, caches and memory

- **Prefetcher.** A port of the PS4's look-ahead `Script_Prefetch` (43 entries, whose V case
  accepts only `VPLY`/`VPL2`; engine 5.4). It scans ahead and queues picture and voice decodes.
  It never changes script state.
  - **Its bound is decoded bytes and frames of lead time, not the PS4's byte budget.** The PS4
    prefetch budget is 12,000,000 bytes of compressed data. That covers a p90 typical scene
    (14.3 MB compressed) only partly and IMG1481 (47.3 MB) not at all (S4.md 6). The port caps
    the decoded bytes in flight (the staging pool of rule 5 below plus the textures waiting
    for upload) and queues a scene early enough for its upload to finish before the first draw.
  - **Lead time needed**, with the upload rules below, at 144 Hz measured and at 60 Hz
    computed as frames x 16.7 ms (S4.md 6):

    | scene | upload | frames | 144 Hz (ms) | 60 Hz (ms) |
    |---|---:|---:|---:|---:|
    | median typical (backdrop + 3 sprites) | 99.1 MiB, 259 tiles | 8 | 48.8 | 133 |
    | p90 typical | 149.9 MiB, 220 tiles | 11 | 69.8 | 183 |
    | IMG1481 alone | 330 MiB | 22 | 146.4 | 367 |
    | heaviest scene | 622 MiB | 40 | 271.6 | 667 |

    The prefetcher must therefore queue a scene 8-40 frames before its first draw. Nothing in
    these runs blocked a frame; the constraint is lead time only. The M4 prefetcher is built
    to this bound.
- **GPU texture cache.** LRU by bytes, default cap 3 GB. A typical scene holds 100-150 MiB of
  tile textures, IMG1481 330 MiB and the heaviest scene 622 MiB, so the cap holds about 20-30
  typical scenes or about 4 heavy ones.
  - Tiles are separate textures, sampled on their inner rectangle, so the tile borders act as
    a filter apron.
  - **Upload rules** of the `Tsuki.Render` upload scheduler. Each is backed by a measurement
    in S4.md 5.2-5.3, and the variants that break one failed the 16.7 ms frame threshold:
    1. **Decode workers: at most the physical core count (8 on the test machine), at
       below-normal priority, on dedicated threads.** 14 workers with 4 tiles per frame
       produced frames of 34.5 and 66.8 ms on a busy machine. With 8 there were none.
    2. **Cap each frame's slice in bytes at about 16 MiB, and always upload at least one
       tile.** 32 MiB per frame still had no frame over 16.7 ms, but reached 11.3 ms. A cap in
       CPU time does not work: 4 ms of CPU admitted 21-29 MiB, and the driver then spent
       25-38 ms in the swap, because it defers the work to the flush.
    3. **Upload straight from client memory** (`glTexImage2D` for a new texture,
       `glTexSubImage2D` for a recycled one). No PBO ring: on this driver it was slower (10 of
       110 load frames over 16.7 ms, 11-16 ms in the swap).
    4. **Never allocate many textures in one frame.** Allocate each tile's texture when it is
       uploaded, or recycle textures from the cache. Creating all 330 textures of IMG1481 in
       one frame stalled the next swap for 24-37 ms.
    5. **Use a bounded staging pool of about 64 MiB of pre-touched tile buffers that the
       workers wait on.** The load time is unchanged (146 ms for IMG1481, 272 ms for the
       heaviest scene); the peak working set falls from 359-603 MB to 241-279 MB.
  - **Measured with the rules** (S4.md 5.2, 7): 170 runs, 5,103 load frames, 0 over 16.7 ms,
    worst frame 8.95 ms, against a 6.94 ms vsync interval at 144 Hz; with the staging pool 0 of
    2,423. Without the staging pool an earlier build had 4 of 805 load frames over 16.7 ms, so
    T7 (10.2) measures the residual hitch rate over hours. The judging metric is frame time
    and swap time, not `GL_TIME_ELAPSED`: the copies are asynchronous DMA, so timer queries
    read 0.05-0.1 ms for recycled textures. The results are for one GPU and driver (AMD RX 9070
    XT, GL 3.3 core `26.10.1`); other vendors and GLES are untested (M8).
- **Opt-in decoded-texture cache** in `%LOCALAPPDATA%` (a few GB, LRU, never shipped). It holds
  only pictures that missed prefetch, for slower PCs. It is off by default. S4 did not need it
  or a longer prefetch distance on the test machine (S4.md 9).
- **No GC pressure in the frame loop:** pooled arrays, struct display-list entries, and
  `GCSettings.LatencyMode = SustainedLowLatency` during ADV.

### 4.4 Data policy

- The repository and the release zip contain no game data: no archives, pictures, audio,
  video, text, fonts, eboot bytes, extracted tables, extracted UI layouts or golden files.
- Ours to commit: code, documents, our own hand-designed tier-1 layouts (9.2), and the
  recipes that tell the extractors where to look (addresses and patterns, like the documents).
- `samples/`, `scratch/`, `artifacts/`, `third_party/` and every cache are local only.
- A git pre-commit hook and the release scan enforce this (10.8).

---

## 5. Script interpretation

### 5.1 Why interpret and not translate

- The eboot's VM is small and fully documented (script 3-4, engine 5, vm_variables).
- Interpreting keeps every code address equal to the PS4 offset. History records, save slots
  and the scene stack can then be the PS4 records unchanged.
- Every trace line maps straight to `python -m tsuki.script dump`.
- Switching language needs no code changes, because text is resolved through `$NNNNNN` at
  display time.

A transpiler would add a second representation that must be kept in sync with the PS4
semantics, and buys nothing here.

### 5.2 VM front end (byte-exact port, RR items 4-6)

- **Script k** is section k + 3 of allscr.mrg, decoded with MZX0 and then XOR 0xFF.
- **Segmenting.**
  - A segment ends at `;` within 0x300 bytes.
  - The name is the 4 bytes after the first `_`. Its first letter must be `A..Z`, and the
    letters B D H K L N O Q U Y are skipped.
  - Every `J*` is a JUMP, and every `Z*` is a marker.
  - At most **1000 segments run per ScriptStep call**.
- **Both argument readers.**
  - The inline reader: a sum of decimal terms; other bytes are ignored; empty gives 0 and
    missing gives -1; 5-byte backquote tags.
  - The object parser for the 52 `OBJ_COMMANDS`, with its right-associative evaluator
    (`242.25` = 242).
  - Keyword tags `` `NNN: `` are filed by number.
- **Variables** (vm_variables).
  - `F[1200]`, `Z[1400]`, `G[2000]`, with operand dispatch on the first byte for each position.
  - `C<n>` is `F[F823+n]` as a write target and the literal n as a value.
  - `_D<n>`, and the three-digit selectors of `GOTS`/`SEL*`.
  - Out-of-range indices are rejected; the retail scripts never use them.
- **Markers** `_Z<k><bbb><ss>`: lower-case hex. Read tracking updates `readmax[block]` when the
  next marker is reached, and blocks above 0xFB3 are never recorded.

### 5.3 Host, yields and language binding

The host's yield set is the PS4's (script 4):

- these always yield: CLO2-5, END_, FADS, MSAD, SELA, SELD, SELS, STCF, TOMW, WTSV, WTTM;
- these yield conditionally: ATSV, GOTO, IF__, J*, MFAD, PGST, RDED, RTM_, VCWT, VPLY, Z*, and
  others.

Messages are `$NNNNNN` references, expanded at display time against the current language table
(ja 0, en 1, zc 2, zt 3). This is the same idea as the Mahoyo port's `rebindModuleStrings`, and
here it comes for free.

### 5.4 Command families

Counts are statements in the 549 retail scripts. For each family, the C# handler must match the
oracle on the full corpus.

| family | commands | spec | oracle |
|---|---|---|---|
| flow, scenes, script end | JUMP (1,741), GOTO, IF__ (1,194), GOTS, IFNX (PS4 branch taken), IFP4 (no-op), IFSC, IFUR, END_, RTM_, RTMN, RTM2, PGST (17,202), XSTP/XRSM; markers ZZ, ZY | script 3.7, 4.1, 7; vm_variables 4 | `vm_vars`, `script` |
| variables | WKST (487,857), WKAD (45,679), WKOR | vm_variables 3-5 | `vm_vars` |
| bookkeeping, unlocks | RDST, RTST, RDAS, RDED, STCG, ATSV (398), WTSV, TRPY (logged only) | adv_state 2.5, 7.2; gallery 2 | `misc_adv_system_state`, `misc_gallery_extras_flowchart` |
| messages | ZM (42,590), ZD, MSAD, WNTY, CLO2..CLO5, MSQK, FCSP, FCLN | text 4-6; textbox 2-8 | `textbox` |
| choices | SELR (824), SELD (170), SELA, SELS | vm_variables 4.5-4.6; system_flow 5 | `misc_boot_title_choice_flow`, `vm_vars` |
| pictures, layers | STCH (54,494), SCH2, STCC (63,261), STCP, STZ4, STRT, STRR, STRA, STPV, SOXY, STGS, STBR (14,940), STEC (12,900), STCF, STMA (24,333), SGMA, SPMA, SNX2, SNX3, STNS, STTR, SACL, SBAS, STCL, SPFC, SPRD, SPRC | layers 3-9; images 7-8 | `misc_layer_state_and_motions` |
| transitions, screen effects | FADS (34,040), FADE, SQK2/SQS2, SHK2/SHS2, SSW2/SSS2, SHOK, SHAF | compositing 5, 7-8; layers 4.4 | `compose`, `misc_compositing_transitions_shaders` |
| sound | VPLY (16,689), VPL2, VCWT, SEPL (14,543), SEFD, SEVL, SESP, MPLY (1,171), MVOL, MFAD | sound_runtime 2-11 | `SoundModel` |
| movies | TOMP (48), TOMW (48) | archives_ui_movies 9; script 6.5 | `catalog` |
| waits | WTTM (14,828): `frames = (ms*3)/50`, skippable flag | RR 4.18 | - |
| no-ops | SNX4, FANX, IFP4, DMMY, STQK, NOP_ | script 3.6 | dispatch table |

### 5.5 The census gate

The dispatch table is generated at build time from `tsuki.script_tables` (command names
only) and checked by a test:

- all 98 names in use have a handler (94 commands plus the marker kinds ZM, ZD, ZZ, ZY);
- an unknown name fails the build;
- an unknown name met at run time is a hard error in dev builds and a logged skip in release
  builds.

---

## 6. Rendering

### 6.1 Display lists

The PS4 draws through display lists of fixed-size records (compositing 6). `Tsuki.Engine`
produces the same kind of list, in PS4 order: scene layers sorted by z, then the message
window and HUD, the choice screen and the UI. `Tsuki.Render` only executes it.

This split is the main testing lever:

- what is drawn is checked without a GPU against the Python models;
- the GPU executor is checked separately against `tsuki.compose` on synthetic lists (10.5).

### 6.2 Frame, scaling and presentation

- The internal frame is 1920 x 1080, because every layout constant is for 1080p. It is scaled
  to the window with 16:9 letterboxing.
- Logic runs at 60 Hz. The present mode is chosen in config:
  - default: vsync at the monitor rate, showing the newest 60 Hz frame;
  - option: an exact multiple, or 60 Hz, so that slow pans and zooms on 144 Hz monitors avoid
    2-3 cadence judder.
- Measured on the 144 Hz test monitor (S2.md 4 #3; `spikes.md` Machine): vsync interval p50
  6.94 ms, and a 30 fps movie frame is presented 4 or 5 times (272 and 1,074 frames; 144 / 30 =
  4.8). **60 Hz output was only computed, never measured**, because changing the display mode
  is a system setting; it is measured once the user allows it (`PACE.md` section 6 risk 4).
  The tick rules of 3.4 hold at any monitor rate.

### 6.3 Compositor

- **Alpha and blending.**
  - Pictures use straight alpha; the normal draw is `SRC_ALPHA, ONE_MINUS_SRC_ALPHA`
    (compositing 4.2).
  - The 14 fixed-function blend states come from the table at VA 0x2D8030 and map to
    `glBlendFuncSeparate` / `glBlendEquationSeparate`.
  - Colour is processed in its encoded values, with no `GL_FRAMEBUFFER_SRGB`.
- **The 35 pixel shaders are rewritten in GLSL** from the `compose.py` formulas: basic 2D,
  YCbCr, clear, capture, transition, the 24 blend shaders and the unreachable sepia variants.
- **Backdrop-reading blends.**
  - Before a quad whose effect reads the destination, the affected rectangle of the current
    target is blitted into a backdrop texture (`glBlitFramebuffer`).
  - **Measured cost: 0.013 ms per full 1080p blit** on the RX 9070 XT (S3.md 3: 12 blits and
    24.88 Mpx per frame cost 0.161 ms). The earlier estimate of 0.1 ms was about 8 times too
    high.
  - Blitting only the quad's bounds instead of the whole target gave a byte-identical frame
    (max diff 0) at the same cost (0.151 ms for 12 blits), because full-screen layers copy 93 %
    of the pixels anyway. The bounds blit is the default, and the full-target blit is the
    reference (S3.md 3, 4.3).
  - Quads never overlap themselves (compositing 14.1), so this matches the PS4.
- **Cost of the compositor (S3, 1920 x 1080, 600 frames, `GL_TIME_ELAPSED`).** The 10-layer
  test (modes 11, 17, 19, 23 and 24, two layers each, backdrop-reading) costs p99 0.52 ms; the
  full test scene (those 10 layers, 2 partial-screen sprite layers and a continuous rule wipe:
  15-16 quads and 12 blits) p50 0.496 / p99 0.680 / max 0.737 ms; 30 full-screen blend layers
  p99 1.33 ms. The limits were 4 ms at p99 and 8 ms at the maximum (`spikes.md` S3). L's re-run
  on a quieter machine gave p99 0.573 ms.
- **Static-layer cache (from krkrz).**
  - Layers below the first animated or backdrop-blend layer are composed once into a cached
    texture.
  - The cache is invalidated by any change to those layers' records.
  - Static pages cost one textured quad.
  - S3 did not need it (the margin to the limit is about 6x) and did not build it. It is an
    optimisation for T7 or a slower GPU to ask for, not a requirement.
- **Colour export.** The PS4 exports colours through fp16 round-toward-zero (compositing 2.1),
  and a float GLSL pipeline does not. S3 measured 99.556 % of an aligned 13-layer frame within
  2 levels against `tsuki.compose` as it is, and 99.970 % with the export modelled, against the
  99.9 % that T5 (b) needs (S3.md 3 #6, 6 #1). The export model is therefore added to
  `tsuki.compose` and `compositing.md` first (ROADMAP 6 rule 8, wave-2 package D1), and T5 (b)
  and the oracle renderer use it. Even without it, every shader mode and all 14 blend states
  are within 1 level of `compose` (the two-pass rule wipe within 2) on the 419 test cases
  (S3.md 4).
- **Captures and transitions.**
  - There are 16 capture surfaces of 1920 x 1080, used for transitions, thumbnails and the
    backlog.
  - `_FADS` captures the whole render target into surfaces 0 and 15, exactly as the PS4 does,
    text window included.
  - Type 0 is a cross-fade with `w = trunc(255p)/255`, type 1 a cut, and type 2 a rule wipe with
    `RULE_nn` through the shared rule slot 0x4DD.
- **Screen effects and masks.** The 51 screen-effect presets (compositing 5.3), and the masked
  or offscreen path for crop and mask layers (layers 9, partly [H]).

### 6.4 Text

- Text is drawn from the game's glyph pages.
- Layout is the bit-exact `Text_Layout` port (`tsuki.textbox`). It covers kinsoku, the English
  word table, and ruby: scale 0.1 x 0.11, dy -7.04, switched off in English.
- Each glyph is drawn as the engine draws it:
  1. an optional drop shadow (+1, +1 units, alpha 0.66);
  2. the outline (the same rectangle 512 px lower on the page);
  3. the fill.
- Furigana and every Japanese line break therefore come out as on the PS4.
- C# `float` is IEEE float32, and RyuJIT does not contract `a*b+c` into an FMA. The layout code
  stays scalar.
- On every build the layout runs the 601 golden cases; the data suite runs all 175,876 lines.

### 6.5 Backends and drivers

- The main path is OpenGL 3.3 core on the SDL3 window.
- All shaders keep to a GLSL subset that also compiles as GLSL ES 3.00, so ANGLE (GLES 3 over
  D3D11) can run them unchanged. S3 compiled all 27 programs both as GLSL 3.30 core and as
  GLSL ES 3.00 on the AMD driver (27 of 27 each); that does not replace a run on ANGLE itself,
  which stays open for M8 (S3.md 6 #3).
- The release ships an ANGLE fallback. It is selected automatically when GL context creation or
  a shader compile fails, or by a launch flag.
  - Where we get prebuilt ANGLE DLLs without a compiler (a NuGet natives package) is confirmed
    in M8.
- Plan B is a D3D11 backend (Vortice) behind the same display-list executor, about 1-2 weeks.
  It is used only if ANGLE fails.
- Render tests run on the local RX 9070 XT.

---

## 7. Audio and movies

### 7.1 Mixer

- One float32 stereo mix at 48 kHz, the PS4 rate, in periods of at most 1024 frames through an
  SDL3 audio stream. SDL resamples if the device rate differs.
- The streams are the PS4's:
  - voice on streams 2..5;
  - SE channels 0..19 on streams 6..25;
  - two alternating BGM slots;
  - the HE-VAG system sounds (`SystemSE_Play` table, sound_runtime 10.1);
  - the movie AAC.
- Gains arrive as final values from the SoundModel; for example BGM gain is
  `trunc(config*vol*0.01)`.
- The pan law is `gL = min(2(1-p),1)`, `gR = min(2p,1)`, with linear ramps.
- **Nothing is clipped before the device.** Decoded SE peaks reach 1.878 of full scale, and
  645 of 1,327 SEs exceed 1.0.
- **Decoding.** The ATRAC9 decoder needs no demuxer: the `atrac9` decoder is opened with
  `block_align` 512 and the 12 bytes of extradata at fmt + 0x28, and each 512-byte superframe
  goes in as one packet. Its float output is bit-identical to the Python decode. One core
  decodes about 1,500 times faster than real time (a 172 s BGM in about 0.11 s), so a whole BGM
  can be decoded on a worker before playback (S1.md 1, 8 #1).
- **Device and callback.** WASAPI opens F32LE 2 ch 48,000 Hz with a 480-frame (10 ms) period,
  and `SDL_HINT_AUDIO_DEVICE_SAMPLE_FRAMES` does not change it. Rendering the mix in the SDL
  callback from a decoded buffer never ran short in 801 s of playback (4 runs, 0 underruns);
  the callback took 1.67 ms at worst for one BGM voice. The full mixer (2 BGM, 20 SE, voice)
  still has to be measured in T7. End-to-end latency after SDL's postmix point is unknown (S1.md
  8 #3, 10 #3).
- **Test boundary.** Sample-exact audio tests are taken at the mixer output (`Mixer.Render`),
  at the SDL postmix capture, or through an SDL audio stream in **pull mode** (the stream plus
  the same get-callback, pulled with `SDL_GetAudioStreamData` in 480-frame chunks). **Never
  through SDL's `disk` driver:** SDL 3.5.0 opens it at S16LE 2 ch 44,100 Hz whatever the
  stream or the format hints ask for, so it resamples and quantises (its callbacks ask for
  1,114 or 1,115 frames per 1,024-frame buffer). The same holds for any device whose mix format
  is not F32 48 kHz: only the content up to SDL can be compared. Headless tests therefore use
  pull mode (0 samples differ from the oracle over 22,850,000 frames, S1.md 7, 8 #2).

### 7.2 Loops

- The playable range is `decoded[256 : 256 + fact[0]]`.
- The loop is `[smpl.start-256, smpl.end-256]`, with the end inclusive, applied to the decoded
  buffer. It is sample-exact by construction.
- Spike S1 proves this on bgm #33, loop `[962000, 8257999]`: the intro plus 3 loops render to
  22,850,000 samples per channel, 0 of 45,700,000 samples differ from the ideal concatenation,
  and the seam positions are off by 0 frames (S1.md 2, 3; re-run by L, `spikes.md` S1).
- **The seam stays engine-exact.** bgm #33 has a step in its waveform at the loop seam (the
  linear-extrapolation error is 15.6 times the local median step, against 0.5-0.8 for the
  natural continuations). It is in the game data, and the PS4 plays it too, so the port adds no
  crossfade by default and before M7 (decision 1; S1.md 5, 10 #1). A 2-5 ms crossfade could
  come later as a non-default setting (ROADMAP M7).

### 7.3 Sound model

`SoundModel` is ported one to one:

- the channel records, the tag scan (`001` fade, `017` volume, `018` pan, `049`, `068`
  deferred queue, `069` start offset, `070` voice character);
- fade to stop, and a same-track `_MPLY` being ignored;
- the voice: `_VPLY` makes it pending, the next `_ZM` starts it through the per-character gate,
  and it stops on advance when config +0x4E is set;
- BGM ducking.

The test (T2) replays the model's whole audio corpus.

### 7.4 Reconciler invariant (from Ren'Py)

After every load, rewind, backlog jump and previous/next-scene, a reconciler compares the
desired state with what the mixer is playing and corrects it. The desired state is both BGM
slots and every looping SE, read from the restored channel records.

- Loops come back at their recorded gain.
- One-shots are never replayed, which matches the PS4 rewind's "without replaying sounds".
- A T4 test asserts the invariant at every round-trip point.

### 7.5 Movies

- `IMovieDecoder` has two implementations:
  - the default is FFmpeg (`mov` demuxer, `h264`, `aac`). It is one library for audio and
    video, it is deterministic, and the research already decoded and probed all 134 movies
    with it;
  - a Windows Media Foundation decoder is an optional fallback (P3). It would let a public
    build avoid shipping FFmpeg's H.264 decoder.
- Frames arrive as Y, U and V planes. A BT.709 limited-range shader converts them; the 55
  untagged files are treated the same way. The decoded planes are byte-identical to the Gyan
  CLI's, and the GL read-back is within 1 level of `tsuki.compose.ps_ycbcr` (S2.md 4 #11).
- The clock is the movie's audio, or the frame clock when the track is silent.
  - **Audio clock** = the media frames the mixer callback has taken, plus the time since that
    callback, minus one device buffer (480 frames = 10 ms). The extrapolation is clamped to one
    period until the stream has drained, the clock never runs backwards, and after the drain
    the frame clock takes over (S2.md 9 #2).
  - **Frame choice** is made by the 60 Hz tick at its nominal time with the 4 ms cadence lock
    (3.4). That is a requirement of this design, not a tuning option: the spike found that
    reading the clock when the present loop reached the tick gave 1 or 3 ticks per frame.
- **Threads (S2.md 9 #1):** one thread demuxes and decodes the AAC into a lock-free ring (4 s of
  float stereo 48 kHz); one decodes H.264 with FFmpeg frame threads (`thread_count` 0) into 8
  pinned frame buffers; the main thread picks the frame. While playing, all movie threads
  together used 0.15-0.25 cores (the limit was 1.0), and the whole process 0.25-0.32 cores.
- **Decoding is software by default** (1,274 frames/s headless, 42.5 times real time).
  `d3d11va` with automatic threads works with copy-back and cuts the movie threads to 0.04
  cores, so it may be an optional switch for weak CPUs. **Do not use `d3d12va` with
  `thread_count` 1**: it fails after frame 2,143 in BtbN 9.0.1. Zero-copy D3D11-to-GL interop
  was not tried (S2.md 5, 9 #4).
- **Upload** is direct `glTexSubImage2D` from pinned memory, three R8 textures for yuv420p:
  0.38 ms CPU and 0.02 ms GPU per frame. A PBO ring gains nothing for movies. The PS4's NV12
  path is the same shader with an RG8 chroma texture, and both were verified (S2.md 6, 9 #5).
- **Movie end** (releases `_TOMW`): video EOF, an empty frame queue and a clock at least one
  frame period past the last pts. The event came 19-28 ms after the last frame first appeared
  (S2.md 9 #6).
- **Measured on `prologue_en.mp4`** (S2.md 4; L's re-runs in `spikes.md`): 11,660 of 11,660
  frames decoded; 0 dropped, 0 late; A/V offset at the last frame -2.08 ms (limit 33 ms);
  AAC 18,655,997 samples per channel, equal to the stream's `duration_ts`, with no resampling
  and 0 underruns.
- **Presentation risk.** Under heavy machine load `SDL_GL_SwapWindow` blocked for 20-38 ms in 2
  of 23 windowed runs and dropped one movie frame in each (S2.md 8). A swap stall longer than
  one movie frame drops a frame in any player; T7 measures this on an idle machine.
- The language remap: prologue 4 becomes 4, 46, 47 or 106; phase titles add +40, +67 or +99;
  openings add +97 or +99. Movie id 7 is skipped during fast-forward.
- `titlebg1..4` are looping backgrounds chosen by G[2] and the title state (system_flow 4).

### 7.6 HE-VAG

The 19 system sounds are decoded by a C# port of `audio_hevag.py`. FFmpeg's `adpcm_psx`
decodes them wrongly (audio 6).

---

## 8. ADV features

### 8.1 State blocks

`Tsuki.Engine` keeps the PS4's documented blocks as byte arrays with typed accessors:

- `scene_state` (0x1A08 bytes);
- the history record (0x3A10), in a ring of 800;
- the sound channel records (0x34 each);
- config (0x140);
- the system block (0x18AC);
- F / Z / G.

A few C# objects hold what the PS4 keeps elsewhere: render records, texture handles and decoded
audio.

Save, rewind, backlog jump and previous/next scene are therefore all the same mechanism. They
copy records the way `History_Push`, `History_Restore`, `Slot_Store` and `Slot_Load` do
(adv_state 2-7), with no hand-written serialiser.

### 8.2 Behaviour

| feature | behaviour (PS4) | ref |
|---|---|---|
| history ring | 800 records; push kinds 0..13 before the command runs | adv_state 2, 4 |
| rewind (hold) | undo records newest first down to the previous page start, re-display, continue; no sounds replayed | adv_state 5.1, 3.2 |
| next / previous scene | run without waits until `_ATSV`, a choice or unread text; restore the scene-stack top (or the one below within the first two messages) | adv_state 5.2-5.3 |
| backlog | open, scroll, replay voice from the stored entry, jump = restore the entry's record and run its command again; entries re-rendered in the current language | adv_state 5.4-5.5 |
| save | slot = page-start record (state A) + scene-start record (state B); 0..99 manual, 100..138 auto FIFO, 139 quick; manual and auto saves set F1118 | adv_state 7.1-7.4 |
| load | `History_Restore` of state A, then the page's first message statement runs again from its offset | adv_state 7.3 |
| Continue | loads the newest slot (system block "latest slot") | adv_state 7.2 |
| read tracking | `readmax[4020]`; blocks > 0xFB3 never recorded | adv_state 6 |
| auto | countdown `trunc(ms/1000*60)` with config +0x9E on the last message of a page, else +0x2E, after the reveal and voice | adv_state 6 |
| skip / fast-forward | latched skip and hold-to-fast-forward (step 64); "read text only" stops at unread lines; stop at choices | adv_state 6 |
| config | every option with a menu item (adv_state 9.2), defaults from data 0x66C578 | adv_state 9 |
| language | 0 ja, 1 en, 2 zc, 3 zt; a change reloads text, fonts and UI sheets and lays out the current page again | RR 4.19 |

### 8.3 Save container

- One versioned file per profile holds a header, the system block and the 140 slot records in
  PS4 layout.
- Our extras sit beside them: a PNG thumbnail per slot (a screenshot at first; the allpac2
  composite later), the app version, play time and a scene title for the slot list.
- Files are written atomically (temporary file, then rename), and versions migrate forward.
- `check_save_image` (Python) validates our records in T4.
- Importing a real PS4 `savedata000` becomes nearly free (P3).

---

## 9. UI and menus

### 9.1 Screen model

- Each screen is a small C# state machine plus a draw list: texture slot, archive, child,
  sub-rectangle, position, scale, anchor, alpha and effect.
- It is drawn with the game's own UI sheets (allpac3, parts.mrg) and with `ui_text` strings,
  rendered through the game's glyph pages.
- Input reaches screens as the PS4 actions (adv_state 10), plus the mouse pointer.

### 9.2 Tier 1: functional (required for M6, "entire game playable")

Every screen of engine 8.3 exists and works:

- the system menu;
- save and load (slot grid, pages, overwrite, copy and delete confirmations);
- the backlog with voice replay and jump;
- the `_SELA` and `_SELS` prompts;
- the confirm dialogs, quick save and quick load;
- previous and next scene;
- save system data and return to title.

These screens are built from a small UI kit: panel, button, list, slot grid, slider, toggle and
dialog. The layouts are our own hand-placed design, using the game's sheets and strings. They
are close in spirit to the PS4 but not pixel-identical.

These screens are faithful from the start, because their layouts are decoded:

- boot, caution and logo;
- the title and its state machine;
- the choice screen;
- the HUD, the message window and the bubbles;
- config (system_flow 3-5, textbox 7, adv_state 9).

Gallery and flowchart are not part of tier 1 (the user wants them later).

### 9.3 Tier 2: faithful (M7)

- **The extractor.** The PS4 layouts are code constants in the screens' entry functions
  (RR 4.16). `scratch/misc_boot_title_choice_flow/calls.py`, the 113-line linear call decoder
  that recovered the title and choice draw lists, works by constant-propagating int and float
  arguments into calls, including tail calls.
  1. It becomes a tested module, `tools/eboot/uicalls.py`.
  2. It is ported to C# on the `Iced` x86-64 decoder (MIT, NuGet) as
     `Tsuki.Data.UiLayoutExtractor`.
- **How it runs.** The C# extractor runs at first run on the user's eboot. The repository holds
  only the recipe (entry VAs, callee VAs, argument registers). The output is checked against
  the Python decoder for every screen.

| screen | entry VA | today | effort (agent-days) |
|---|---|---|---|
| system menu, mode panel | 0x6758E / 0x6CA83 / 0x6E683 | logic partly known | 3-5 |
| save / load | 0x6F423 | logic decoded (adv_state 7) | 3-5 |
| backlog | 0x3BFB0 | logic decoded (adv_state 5.4-5.5) | 3-4 |
| `_SELA` / `_SELS` prompts | widgets 0xD5 / 0xD6 | open (system_flow 8.4) | 1-2 |
| gallery: CG, music, voices, movies, ending list, extra scenarios | 0x12680 and the classes of gallery 1 | data, unlocks, replay decoded (gallery 2-8) | 6-10 |
| flowchart | 0x20960 | columns, nodes, edges, filter decoded; line drawing not (gallery 5, 13.1) | 4-6 |
| menu transitions 18, 20..24, 68..70 | - | not decoded (cross-fade stand-in) | 2-3 |

If extraction stalls on a screen, its tier-1 layout stays, and the gap is listed in section 11.
The user's PS4 captures (10.6) are the reference either way.

### 9.4 Player shell and input

Defaults follow the PS4 action map on the gamepad, honouring the enter-button setting. On
keyboard and mouse they follow common VN conventions. Everything can be rebound.

| action | keyboard / mouse | gamepad |
|---|---|---|
| advance / decide | left click, Enter, Space, wheel down | PS4 decide |
| menu / cancel | right click, Esc | PS4 cancel / options |
| backlog | wheel up, Page Up | PS4 preset |
| rewind (hold) | Backspace (hold) | PS4 preset |
| fast-forward (hold) / skip (toggle) | Ctrl (hold) / Tab | PS4 preset |
| auto | A | PS4 preset |
| hide window | H | PS4 preset |
| quick save / quick load | F5 / F9 | PS4 preset |
| next / previous scene | N / B | PS4 preset |
| replay voice | V | PS4 preset |
| fullscreen | Alt+Enter, F11 | - |

Shell behaviour:

- Confirm on quit.
- The window size and position are remembered.
- The mouse cursor hides while reading.
- The pointer maps into the 640 x 448 space only inside screens that need it.
- Gamepads hot-plug, with PlayStation button labels (`SDL_GetGamepadButtonLabel`).
- Rumble is off: the PS4 option "vibration" is kept but does nothing on PC unless the user
  enables it.

### 9.5 First-run and error UI

- Before the game folder is known there are no game fonts. First-run and error screens
  therefore use native SDL dialogs and message boxes (system font, Unicode, ja + en), so no
  font is bundled.
- If a later in-engine screen needs characters the glyph pages lack (for example file paths),
  an OFL font such as BIZ UDMincho is added for those strings only, never for game text.
- A crash writes `%LOCALAPPDATA%\TsukihimePort\crash\<time>.txt` with the script position
  (script, offset, statement) and the last 200 trace lines.

---

## 10. Verification against the Python oracle

### 10.1 Oracle export and the dev channel

**The oracle export.** `tools/oracle/*.py`, built on `tools/tsuki`, writes golden data to
`samples/golden/<area>/`, with a `manifest.json` per area (tool commit, input hashes, counts).
It is local only. The areas are:

- archive entry tables;
- SHA-1s of decoded streams and pictures;
- text;
- segment lists with both readers' argument values;
- eboot tables;
- font lookups;
- layouts;
- float PCM hashes and loop points;
- model traces: `vm_vars` fuzz cases, layer-model statement traces, the SoundModel corpus, ADV
  push/restore/slot cases, choice and title draw lists, gallery unlock cases;
- synthetic `compose` renders.

The data stream's Python generators (`gen_golden_data.py`, which writes the data-layer
expectations to `samples/golden_data/`, and `all_stream_hashes.py`, the hash of every MZX0
stream) are written under `tests/Tsuki.Tests.Data/Oracle/` today. They move to
`tools/oracle/data.py` and run through the oracle runner, and `tests/Tsuki.Tests.Data/Oracle/`
keeps only thin callers (decision 2; wave-2 package A1). Real-data tests clean up the export
folders they create and leave no game-derived output in `%TEMP%`. The golden slice of the first
scene (`samples/golden/slice/`, manifest schema `tsuki-golden-manifest/1`, 87 files) exists
since M0; `python -m oracle.run slice --verify` gives 0 problems (`M0.md` acceptance 5).

**Trace replay.** For long sequences, the C# engine writes a trace: statement, pre-state digest,
and full blocks every N statements and at every yield. `tools/oracle/replay.py` replays that
trace through the Python models and reports the first divergence, with both states dumped.

**`Tsuki.DevBridge`** (dev builds only, the idea behind Wamsoft's `-replfile`). Run with
`--dev-bridge <dir>`, the app polls `<dir>/in/*.json` once per frame and writes answers to
`<dir>/out/`. The commands:

- dump state (variables, layers, sound, script position, screen);
- inject input;
- warp to `scene:label` or load a slot file;
- run until the next message, choice or yield;
- set the time scale;
- capture the frame as a PNG.

`Tsuki.Cli` exposes the same commands headless.

### 10.2 Test layers

| layer | what | pass criterion |
|---|---|---|
| T0 static census | tokenize all 549 scripts in C#; resolve every reference | segment lists byte-equal to `tsuki.script` (1,083,168); 0 unknown names; 10,962 of 10,967 picture references resolve (the 5 known script bugs plus 3 missing sprite bodies are an explicit allow-list and draw nothing, as on the PS4); 16,705 voice refs, 14,543 `_SEPL` names, all `_MPLY` tracks, 40 movie ids, 1,741 jumps, 629 labels, 43,902 text ids resolve |
| T1 data differential | C# decoders against the goldens | 49,620 entries with equal sizes; MZX0 equal (sampled per build, all streams nightly); picture SHA-1s equal; 5 x 43,969 text lines; eboot tables (catalog 14,744, motions 24,192 / 71,905 keyframes, fonts 17,423, ui_text 5 x 1,499); 19/19 HE-VAG |
| T2 model differential | C# engine on each model's corpus, plus trace replay (10.1) | state bytes equal after every statement: `vm_vars`, layer model, SoundModel, ADV push/restore/slot, choice/title draw lists, `textbox` (601 golden + 175,876 lines) |
| T3 explorer | the whole game headless (10.3) | 10.3 |
| T4 round trips | at every page start on explored paths: save -> load -> save byte-identical; continuing 2,000 statements after a load gives the uninterrupted trace hash; rewind, backlog jump and previous/next scene on a 1 % sample; the reconciler invariant (7.4) | 0 differences |
| T5 render | (a) display lists against the Python models (no GPU); (b) GPU executor against `compose` renders on synthetic lists for every blend mode, state and transition; (c) game checkpoints against the oracle renderer and PS4 ground truth (10.5) | (a) equal; (b) max abs diff <= 2/255 on 99.9 % of pixels, against `compose` with its fp16 export model (6.3: 99.970 % with it, 99.556 % without, S3); (c) 10.5 |
| T6 media | decode every referenced asset once: 17,925 ATRAC9 streams, 19 vag, 134 movies to the last frame | 0 errors; loop seams sample-exact (join test on every looping BGM/SE); prologue A/V within one frame |
| T7 performance | opening at real time plus a 1-hour scripted soak | no logic step over budget; p99 frame <= 16.7 ms; 0 audio underruns; no presented frame waits on a decode |
| T8 human | feel: text speed, reveal, auto timing, transitions, voice/BGM balance, menus | sign-off per milestone; a prose description of a screenshot never counts |

- `Tsuki.Tests.Synthetic` needs no game data and runs on every change. It covers the readers on
  hand-written statements, MZX0 on synthetic streams, mixer maths, layout on synthetic strings
  and the shaders on procedural textures.
- The data tests skip cleanly without `TSUKI_GAME_DIR`, using one central attribute
  (`[GameDataFact]` / `[GameDataTheory]` in `tests/Tsuki.Tests.Data/GameData.cs`, which skip
  only when the game folder is missing and say why). That is the only skip the hooks allow.
- **Measurements run one at a time.** A GPU or timing measurement (the T7 soak, the S3-style
  GPU timings, upload hitch counts) holds the lock file `artifacts/gpu.lock`: create it
  exclusively, delete it when done, wait and retry if it exists, and treat a lock older than 30
  minutes as stale. Functional GPU tests that assert pixels, not timings, do not need the lock
  (decision 4). The reason is measured: swap stalls of 20-38 ms from other jobs dropped movie
  frames in 2 of 23 S2 runs (S2.md 8), and S4 and the build locks collided (`PACE.md` 4, R8).
- **Parallel builds use per-agent output folders:** `dotnet build` and `dotnet test` with
  `--artifacts-path artifacts/<package-id>`, so that agents do not lock each other's binaries,
  and no built app, spike or test host is left running (decision 7).

### 10.3 The explorer: every script, every branch

`Tsuki.Cli explore` runs the shipping engine with a null renderer and a null mixer, in virtual
time. The SoundModel still runs. Waits end at once, messages auto-advance, and movies "end"
once opened and probed.

- **Branching.**
  - At `_SELD`, `_SELA` and `_SELS` it snapshots the state blocks and queues one branch per
    enabled option.
  - Locked options are skipped, as the choice model does.
  - It deduplicates on a hash of (script, offset, F, the G range, the scene bit sets).
- **Campaigns for persistent unlocks.**
  - The system block of a finished playthrough carries into the next New Game, through the real
    boot and title logic (G[10], G[2], bit 485), until coverage stops growing.
  - Gallery replays (ARCHIVE, scene 3000, with each G[8]) and the 25 ending-list scenarios are
    explored the same way.
- **Pass criterion.**
  - 100 % of the story scripts and extras reachable from the retail flow are covered, along with
    every `ZM` marker on those paths, all 206 story choice options, every ending and every
    `RDED` bit the scripts can set.
  - Unreached statements are allowed only on a reviewed exclusion list: Switch branches behind
    `IFNX`/`IFP4`, `ZD`/`DMMY`, and the debug scripts (`TEST_SCRIPT`, `JUMPxx`).
- **Failures.**
  - an unknown command or a handler exception;
  - an out-of-range index;
  - a missing scene or label;
  - an unresolved picture outside the allow-list;
  - a missing voice, SE, BGM or movie;
  - a text id out of range;
  - a layout over 1,200 elements;
  - a wait that can never end (`VCWT` with no voice, `TOMW` with no movie);
  - a livelock: N frames without a message, choice or script end.
- **Determinism.** Two runs give identical trace hashes.
- **Languages.** It runs in all four languages, which checks text resolution and layout
  overflow per language.
- **Save sampler.** It writes real save files, in the shipping container, at the start of every
  route and before every ending. The human loads them to spot-play endings, and they double as
  save-compatibility fixtures.

The scripts hold about 1.08 M segments. A headless C# VM should run well over 1 M statements
per second per thread, so thousands of branch combinations finish in minutes on 16 threads.

### 10.4 State round trips

All of T4 compares bytes, not "logical equivalence": state is kept in PS4 layouts (8.1).

### 10.5 Visual verification

- **Checkpoints** are chosen by script position, about 2,000 frames:
  - the first page of every script;
  - every choice screen;
  - the first use of each `STBR` mode, each `STEC` mode and each `RULE_nn`;
  - each motion easing;
  - each transition with the window visible;
  - each window type;
  - the first and last frame of each movie;
  - the title, config and every menu.
- **Each checkpoint is rendered offscreen** and compared with three references:
  1. The oracle render of the same dumped state (`tools/oracle/render.py`: layer projection,
     catalog and `compose`). The threshold is PSNR >= 45 dB, with max abs diff <= 2/255 on
     99.9 % of pixels. This catches renderer bugs.
  2. PS4 ground truth (10.6), compared by SSIM >= 0.98 plus human review. This catches
     misunderstandings that the Python and the C# code share.
  3. Approved local goldens, for regressions.
- **Second formula reference.** For blend and transition formulas still marked [H] (the dodge
  black guard, the nexl alpha family), the krkrz `blend_function.cpp` / `blend_functor_c.h`
  formulas are an independent cross-check. Where they disagree with `compose.py`, PS4 ground
  truth decides.
- **Review page.** `artifacts/review/index.html` (local) shows the worst 50 diffs side by side
  for the human, with reference, ours, the diff heatmap and the script position.

### 10.6 PS4 ground truth

- **What exists now:** the prologue movie frames that show page 1 of `00_02` (25 s, 60 s), the
  title still (frame 241) and the burned-in pages of the phase-title movies.
- **What the user is asked for** (ROADMAP 5): short Share-button screenshots or videos from
  their console, at a listed set of checkpoints, before M4 and M7:
  - rule wipes;
  - psdodge5 glows;
  - STEC scenes;
  - a transition with the text window visible;
  - quake, shake and swing;
  - the backlog, save and load, the system menu, the SELA prompt;
  - the gallery and the flowchart.

  The captures stay local, in `samples/ps4/`.

### 10.7 Definition of "entire game playable start to finish" (gate M6)

All of the following must hold, with the evidence kept in `docs/status/M6.md` (numbers only)
and `artifacts/M6/` (local):

1. **T0-T2 are green**, and the census allow-list is unchanged.
2. **The explorer passes 10.3** with the shipping engine in all four languages, with 0
   failures and deterministic results.
3. **T6 media decode-all is green.**
4. **T4 round trips are green.**
   - Continue works.
   - Config and system data survive a restart.
   - The language can be switched on any page.
5. **T5 (a) and (b) are green.** T5 (c) passes on all checkpoints, or each failure is listed
   with its reason and accepted by the user.
6. **T7 is green** on this machine.
7. **Every tier-1 screen (9.2) is reachable and works.** A DevBridge smoke script proves it:
   it opens each screen, saves, loads, overwrites, deletes, changes every config option, uses
   the backlog jump and voice replay, quick-saves and quick-loads, and uses previous and next
   scene.
8. **Human checks:**
   - the user reads New Game through the end of chapter 1 in Japanese;
   - the user loads the explorer's save before every ending and plays each ending through the
     credits;
   - the user spot-checks English on 3 pages.
9. **The release-candidate zip** (self-contained publish) starts from the folder picker on a
   second Windows account with an English locale.

### 10.8 Rules enforced by hooks, not prompts

- **Claude Code project hooks** (`.claude/settings.json`, installed with the user's approval)
  block:
  - edits under `samples/golden/**` and to generated files (`src/**/Generated/**`);
  - new test skips other than the central data attribute;
  - deleting test files.
- **A git pre-commit hook** allows only our paths and extensions:
  - `src/`, `tests/`, `tools/**/*.py|ps1`, `docs/**/*.md`, project files;
  - it rejects binaries, files over 1 MB, and anything under `samples/`, `scratch/`, `ref/`,
    `artifacts/` and `third_party/`.
- **The release scan** (M8) lists every file in the zip against an allow-list.

**State after M0** (`M0.md` "Hooks", acceptance 4: 3 of 4 cases blocked). The installed hooks
block an edit under `samples/golden/`, `samples/inventory/`, `ref/` and the game folder; a new
`Skip =` on a test, an unconditional `pytest` skip, anything under `samples/` and game-data
file types at commit time. They do not yet block a `.png` or another binary under `docs/`,
`tools/`, `src/` or `tests/`, `Assert.Skip(...)`, `pytest.skip()` or `Skip =` at edit time,
edits to `src/**/Generated/**`, or deleting test files; the size limit is 5 MiB, not 1 MB; and
`tools/setup/install_hooks.ps1` does not exist. The list above stays the target. Changing the
hooks is a configuration change that needs the user's consent, and it waits until wave 2 has
finished so that no running agent is blocked mid-task (decision 8; `PACE.md` 7.1 L-4).

---

## 11. Fidelity gaps (accepted or open)

| # | gap | plan |
|---:|---|---|
| 1 | tier-2 menu layouts not yet extracted (9.3); tier-1 layouts differ visibly | extractor + PS4 captures (M7) |
| 2 | text vertical scale: 3.0 px per unit from the engine constants vs 3.115 in the prologue movie (78 vs 81 px line pitch) | calibrate at M2 on movie frames and captures (RR 9.1) |
| 3 | `@g @z @b @a` styles, outline colour table 0xDB7BB0, RGB565 vs BGR565 order, previous-message dimming | decide on PS4 captures |
| 4 | quake / shake / swing per-frame formula, crop / mask rasterisation and feather (a16/a17), the `STCF` reader, `FADE` effect 4, easing id 70, title particles | documented stand-ins, then decode from the disassembly (stream D) |
| 5 | engine-only menu transitions 18, 20..24, 68..70 | cross-fade stand-in, then decode (M7) |
| 6 | about 1 LSB numeric differences (GLSL float vs PS4 fp16 export, filtering) | the oracle models the fp16 round-toward-zero export (6.3); tolerances in T5; the PS4's blender rounding on RGBA8 targets is unknown until PS4 captures (S3.md 6 #2) |
| 7 | save previews are screenshots, not the allpac2 composite | switchable later (P2) |
| 8 | no trophies (log only), PS4 dialogs, light bar or Share; keyboard and mouse bindings are our design | none needed |
| 9 | only the analysed eboot build is supported | clear refusal; pattern-based locator P3 |
| 10 | Korean data are stubs (as on the PS4); LV lip sync is a placeholder in retail | none |
| 11 | movies decoded in software by FFmpeg instead of the PS4 hardware decoder | expected to be invisible; T6 frame checks. S2: the planes are byte-identical to the Gyan CLI's, and the colour is within 1-2 levels of swscale and of `compose` |
| 12 | bgm #33's loop seam has a waveform step (15.6 times the local median step). It is in the game data and the PS4 plays it | engine-exact by default, no crossfade before M7 (decision 1; 7.2); an optional non-default crossfade may follow |
| 13 | 60 Hz display output and end-to-end audio latency after SDL's postmix point were not measured (the test monitor is 144 Hz; the WASAPI engine and the device add buffering we cannot see) | measure at 60 Hz once the user allows the display setting; a WASAPI loopback capture for latency (6.2, 7.1) |

---

## 12. Risks and fallbacks

| id | risk | likelihood | mitigation / fallback |
|---|---|---|---|
| R1 | menu layout extraction slower than planned | medium-high | stream G starts in week 1; tier-1 menus make M6 independent of it; per-screen fallback stays tier 1 |
| R2 | shared misunderstanding: C# matches Python but both differ from the PS4 | medium | PS4 ground truth (10.6); krkrz second reference for formulas; human review of the worst 50 |
| R3 | native interop crashes (FFmpeg.AutoGen pointers, SDL callbacks) | medium early | thin wrapper layer with its own tests; spikes S1/S2 in M0; nightly T6; crash log with script position |
| R4 | OpenGL driver problems on other PCs | low here, medium elsewhere | GLES-compatible GLSL; ANGLE fallback in the release; D3D11 plan B |
| R5 | explorer state space grows on flag combinations | low-medium | hash deduplication; liveness-limited hashing; campaigns; the 206-option structure is small |
| R6 | large-picture decode stalls | low | prefetch, parallel tiles, sliced uploads under the five rules of 4.3. S4 passed: read + decode 32 ms for the largest picture, 0 of 5,103 load frames over 16.7 ms; the residual hitch rate (4 of 805 without the staging pool) is measured by T7 |
| R7 | timing "feels off" although the numbers are the engine's | low-medium | human sign-off at M2, M4 and M6 |
| R8 | FFmpeg H.264 distribution concerns if the build is ever shared | low | `IMovieDecoder` Media Foundation fallback; the user's private use is unaffected |
| R9 | .NET 9 end of support during the project | certain, trivial | target .NET 10 from day 1 |
| R10 | game-derived data leaks into git or the zip | low | hooks, pre-commit allow-list, release scan (10.8) |

There is no planned switch of route. Each spike in M0 has an in-route fallback (ROADMAP M0).
All four spikes passed (S4 with the five upload rules of 4.3) and the lead's re-runs confirmed
them, and no fallback was triggered (`spikes.md` Summary).
If M2 slips by more than a week **because of the shell** (window, GL, audio or movie interop)
rather than engine logic, the lead re-plans the shell: D3D11, Media Foundation or other
bindings. Everything else is kept.

---

## 13. Size

Estimated at about 45-50k C# lines, more than half of it translated from tested Python:

- `Tsuki.Data` about 9k;
- `Tsuki.Engine` about 15k;
- `Tsuki.Render` about 4k plus 1.5k GLSL;
- `Tsuki.Media` about 3k;
- `Tsuki.Platform` about 1.5k;
- `Tsuki.Ui` about 7k;
- tests and tools about 8k.

The release folder is about 250 MB: our code, the self-contained .NET runtime, SDL3.dll, the
FFmpeg LGPL v3 DLLs, the ANGLE DLLs and the licence notices (our MIT, FFmpeg LGPL v3, SDL
zlib, ANGLE BSD). The FFmpeg build in use is LGPL v3 (`avcodec_license()` = "LGPL version 3 or
later", 0.5; decision 10). Whether the DLLs are shipped at all stays an M8 question; the
fallback is `IMovieDecoder` on Media Foundation (7.5, R8).

---

## 14. Revisions after the M0 spikes (package L2, 2026-10-25)

Every change made when this document was aligned with the spike reports and the lead's
decisions. `S1.md` .. `S4.md` are in `docs/status/spikes/`, `spikes.md` and `decisions.md` in
`docs/status/`. The same changes for the other two documents are in the L2 status file
(`docs/status/wave2/L2.md`).

| # | change | sections | source |
|---:|---|---|---|
| 1 | The 60 Hz tick reads the clock at its nominal grid time, with a 4 ms cadence lock; measured cadence and the 50 ppm clock drift | 3.4, 7.5 | S2.md 7, 9 #3; `spikes.md` S2 and Summary finding 1 |
| 2 | The five upload rules of the render upload scheduler, the measured frame results, and the GL timer caveat | 4.3, R6 | S4.md 5.2, 5.3, 7; `spikes.md` Summary finding 2 |
| 3 | Prefetch is bounded by decoded bytes and frames of lead time (8-40 frames), not by the PS4's 12,000,000-byte budget; texture-cache capacity in scenes | 4.2, 4.3 | S4.md 6; `spikes.md` Summary finding 2 |
| 4 | Backdrop blit measured at 0.013 ms per 1080p blit (estimate was 0.1 ms); compositor cost; bounds blit equals full blit; the static-layer cache is optional | 6.3 | S3.md 3, 4.3; `spikes.md` S3 |
| 5 | The fp16 round-toward-zero export model belongs in the oracle for T5 (b) | 6.3, 10.2, 11 #6 | S3.md 3 #6, 6 #1; `spikes.md` S3 open point |
| 6 | Audio test boundary: mixer output, SDL postmix or SDL pull mode, never the SDL `disk` driver; decoder shape; device and callback numbers | 3.1, 7.1 | S1.md 1, 7, 8 #2, 10 #4; `spikes.md` Summary finding 5 |
| 7 | bgm #33's loop seam stays engine-exact, no crossfade before M7; gap 12 | 7.2, 11 | S1.md 5, 10 #1; decision 1 |
| 8 | Movie player: clock formula, threads, software decode by default, no `d3d12va` with 1 thread, direct upload, end rule, presentation stall risk | 7.5 | S2.md 5, 6, 8, 9 |
| 9 | FFmpeg is LGPL v3 (`avcodec_license()`); notices say v3; shipping the DLLs is an M8 question | 0.5, 13 | S1.md 1, 10 #2; decision 10 |
| 10 | `Directory.Build.props` and `Directory.Packages.props` at the repository root; solution `src/Tsuki.slnx`; `TSUKI_DEV` and `TsukiUsesFFmpeg` | 0.5, 3.1 | decision 6; the root props files |
| 11 | The spikes live in `src/Spikes/`, are in the solution build, not in the shipping build, and are deleted after M2 | 3.1 | decision 6; `spikes.md` header |
| 12 | `Tsuki.Data` may use `AllowUnsafeBlocks`; the no-P/Invoke and no-native-package rules stay; the checks of `ProjectRulesTests` | 3.1 | decision 5; `ProjectRulesTests.cs` |
| 13 | `GameFolderLocator`'s default path is for dev builds only | 4.1 | decision 3 |
| 14 | The data stream's Python generators move to `tools/oracle/data.py` | 10.1 | decision 2 |
| 15 | GPU and timing measurements one at a time under `artifacts/gpu.lock`; per-agent `--artifacts-path` | 10.2 | decisions 4, 7 |
| 16 | Hooks: the state after M0, and tightening waits until wave 2 has finished | 10.8 | decision 8; `M0.md` Hooks |
| 17 | 60 Hz output and audio latency are unmeasured (gap 13); the 144 Hz presentation numbers | 6.2, 11 #13 | S2.md 4 #3, 10; S1.md 10 #3 |
| 18 | ANGLE: 27 of 27 programs compile as GLSL ES 3.00; an ANGLE run is still open | 6.5 | S3.md 6 #3; `spikes.md` S3 |
