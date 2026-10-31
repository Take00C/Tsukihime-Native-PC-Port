# Roadmap: from setup to the whole game playable start to finish

Written 2026-10-25 for the route fixed in `docs/ARCHITECTURE.md`: a single-game C# runtime that
reads the user's CUSA47999 folder directly. Section numbers like "A 10.3" point into
ARCHITECTURE.md.

Milestones are **gated by proof, not by dates**. A milestone is done when every acceptance
check passes and its evidence exists (section 3). The calendar figures assume:

- 4-6 agents writing code in parallel, plus a lead who reviews and integrates;
- the user available for 30-60 minutes a day, with longer review sessions at M2, M4, M6 and M7.

**State after the M0 review (2026-10-25).** Aligned with the spike reports and the lead's
decisions by package L2. `docs/status/M0.md` is the M0 review, `docs/status/spikes.md` and
`docs/status/spikes/S1.md` .. `S4.md` hold the spike numbers, `docs/status/decisions.md` the
lead's decisions (cited as "decision N"), and `docs/status/PACE.md` section 7 the wave-2
packages with their sizes and launch order.

- M0 acceptance: items 1, 2, 5 and 6 pass on the lead's re-run, item 3 (`Tsuki.App --smoke`)
  fails because the app skeleton is not built yet (wave-2 package H1), and item 4 (hooks) blocks
  3 of 4 cases (package L-4, after wave 2, decision 8). M0 is therefore not accepted yet.
- **All four spikes passed with no fallback triggered**; the findings that changed the plan are
  in the M0 spike table below and in the sections they name.
- `docs/ARCHITECTURE.md` carries the same findings with their citations, and its section 14
  lists the changes made in this alignment.

---

## 0. Overview

| M | name | expected (calendar) | gate in one line |
|---|---|---|---|
| M0 | setup, harness, spikes | days 1-3 | toolchain and hooks work; build check accepts the user's copy; spikes S1-S4 measured |
| M1 | data layer | weeks 1-2 | T0 + T1: the C# decoders equal the Python oracle on the real data |
| M2 | vertical slice | weeks 1-2 | RR 7.5 checks 1-9 in ja and en: prologue movie, then page 1 of `00_02` with text, voices, SE and blends |
| M3 | headless engine + explorer v1 | weeks 2-3 | every story script, choice option and ending reached headless, with 0 failures |
| M4 | full play loop | weeks 3-5 | every command real; models equal; the opening through chapter 1 reads correctly in Japanese |
| M5 | ADV features + functional menus | weeks 4-6 | saves, rewind, backlog, auto/skip, config and language work and round-trip byte for byte; every menu reachable |
| **M6** | **entire game playable start to finish (RC1)** | **end of week 6-7 (range 5-10)** | A 10.7 checklist green |
| M7 | faithful menus, gallery, flowchart, extras | weeks 6-10 | extracted PS4 layouts; gallery, music, voices, movies, ending list, extra scenarios, flowchart replay |
| M8 | release 1.0 | weeks 9-11 (bound about 16) | ANGLE fallback, English-locale and non-ASCII-path tests, packaging scan, final playthrough |

The order follows the long-tail lesson from impacto and Umineko:

1. the play loop first;
2. then the systems;
3. then the faithful UI and the extras.

M6, "the entire game playable", does not depend on the menu-layout extraction. The functional
tier-1 menus (A 9.2) cover it.

---

## 1. Streams and ownership

Each stream owns its folders, and nobody else edits them. Each stream also writes its own oracle
exporter in `tools/oracle/<area>.py`, and keeps `docs/status/<stream>.md` current: done, next,
blockers, measured numbers, never game text.

| stream | owns | oracle it tests against | first deliverable |
|---|---|---|---|
| **L** lead | `src/Tsuki.Engine/Contracts/`, the root `Directory.Build.props` and `Directory.Packages.props` (decision 6), `docs/status/overview.md`, milestone reviews | - | contracts (A 3.2), M0 review |
| **H** harness | `tools/oracle/` framework (runner, manifest, `replay.py`, `render.py`), `tools/setup/`, test infrastructure, hooks, `Tsuki.DevBridge`, `Tsuki.Cli` shell, packaging | all | oracle runner + `export_slice.py`; hooks |
| **A** data | `Tsuki.Data` (except the UI extractor) | `mrg`, `nam`, `mzx`, `image`, `catalog*`, `text`, `script`, `font*`, `audio*`, `tools/eboot` | build check + `Tsuki.Cli verify`, then containers and MZX0 |
| **B** text | `Tsuki.Engine/Text`, the glyph path in `Tsuki.Render/Text` | `textbox`, `font`, `text_refs` | `Text_Layout` port equal on the 601 golden cases |
| **C** VM + explorer | `Tsuki.Engine/Vm`, `Flow`, `Choices`, `Tsuki.Cli explore` | `script`, `script_emu`, `vm_vars`, `misc_boot_title_choice_flow` (choice) | segmenting and both readers equal to `tsuki.script` |
| **D** stage + compositor | `Tsuki.Engine/Stage`, `Tsuki.Render` (except text) | `misc_layer_state_and_motions`, `compose`, `misc_compositing_transitions_shaders` | display-list executor + spike S3/S4 |
| **E** audio + movies | `Tsuki.Media`, `Tsuki.Engine/Sound`, `Tsuki.Engine/Movie` | `audio`, `audio_hevag`, `SoundModel` | spikes S1/S2, then the mixer |
| **F** ADV state | `Tsuki.Engine/Adv`, `Tsuki.Engine/Config`, the save container | `misc_adv_system_state`, `misc_gallery_extras_flowchart` (unlock bits) | history push/restore equal on the oracle cases |
| **G** UI | `tools/eboot/uicalls.py`, `Tsuki.Data/UiLayoutExtractor`, `Tsuki.Ui` | `misc_boot_title_choice_flow`, `uicalls.py`, PS4 captures | `uicalls.py` as a tested module, then the tier-1 UI kit |

The data stream's Python generators (`gen_golden_data.py`, `all_stream_hashes.py`) move from
`tests/Tsuki.Tests.Data/Oracle/` to `tools/oracle/data.py` and run through the oracle runner;
the test folder keeps only thin callers (decision 2, package A1). `docs/status/PACE.md` section
7.2 gives each wave-2 package its owner folders.

---

## 2. What runs in parallel

At most 6 code-writing agents run at once, plus L. A stream that runs out of work hands its slot
to the next stream in the list below.

| phase | active streams | why this set |
|---|---|---|
| M0 (days 1-3) | L, H, A, D, E | toolchain, hooks, oracle runner, build check, and the four spikes |
| M1 + M2 (weeks 1-2) | L + A, B, C, D, E, G | `GoldenSource` (A 3.3) decouples B, C, D and E from A; G starts the layout extractor early because it is the long tail |
| M3 + M4 (weeks 2-5) | L + C, D, E, F, G, H | A and B hand over once T1 and the layout tests are green; F starts on pure engine code against its oracle; H builds trace replay, the oracle renderer and the review page |
| M5 (weeks 4-6) | L + F, G, C, D, E, H | F and G drive the systems and tier-1 menus; D and E finish the effects and media |
| M6 gate (weeks 6-7) | L + H, C, F, plus fixers from any stream | the full explorer, the round trips, visual sampling and the RC build |
| M7 (weeks 6-10) | L + G (two agents: menus; gallery and flowchart), D (menu transitions, effect [H] items), F, H | can start as soon as M5 is green; it does not block M6 |
| M8 (weeks 9-11) | L + H, D (ANGLE), G | release work |

Dependencies that cannot be parallelised:

- **M0 comes first.** The contracts, hooks and oracle runner come before any stream code.
- **The layer model and motions (package D2) start only after** the concurrent layer-motions
  research (`docs/formats/layers.md`, `misc_layer_state_and_motions` and its Python model) has
  landed and been committed, because D2 ports it (decision 9).
- **Hook tightening waits until wave 2 has finished** (decision 8), so that no running agent is
  blocked mid-task by a new rule. It is the first item after the wave-2 review.
- **M2** needs B, C, D and E. It runs on `GoldenSource` first (M2a) and on the real folder once
  A's M1 is green.
- **M3** needs C only. Its segment lists can come from `GoldenSource`.
- **M4** needs A (for T6 decode-all), C and D (real handlers) and E.
- **M5** needs F, plus G's tier-1 UI kit.
- **M6** needs everything above.

---

## 3. Milestones

Every milestone records its evidence in `docs/status/M<n>.md`. That file is committed and
contains numbers, test names and commands, but no game text and no images. Frames, diffs and
traces go to `artifacts/M<n>/`, which is local only. **L accepts a milestone only after
re-running its commands, not by reading a report.**

### M0: setup, harness, spikes (days 1-3)

**Scope**

1. **Environment.**
   - `tools/setup/check_env.ps1` verifies the .NET 10 SDK, the FFmpeg DLLs in
     `third_party/ffmpeg/bin`, the `.venv` packages and `TSUKI_GAME_DIR`.
   - `tools/setup/ffmpeg_dlls.ps1` copies the DLLs from the winget package folder.
2. **Repository hygiene.**
   - Initialise a local git repository (no remote).
   - Extend `.gitignore` with `bin/`, `obj/`, `.vs/`, `*.user`, `artifacts/`, `third_party/` and
     `TestResults/`.
   - Add the pre-commit allow-list hook and the Claude Code project hooks (A 10.8).
3. **Solution skeleton.**
   - All projects of A 3.1.
   - `Directory.Build.props`: `net10.0`, `Nullable`, `TreatWarningsAsErrors`,
     `InvariantGlobalization`, `Deterministic`.
   - `Directory.Packages.props` with pinned versions.
   - Both props files sit **at the repository root**, not under `src/`, so that `tests/`
     inherits them too (decision 6). The solution file is `src/Tsuki.slnx`.
   - A test that enforces the project-reference rules (`ProjectRulesTests`).
4. **The contracts of A 3.2.**
5. **The oracle runner** (manifest, local output to `samples/golden/`) and `export_slice.py`.
   It exports everything RR 7.3 lists for the slice:
   - scripts 2 and 3 as segment lists with argument values from both readers;
   - text lines 0..7 in ja and en, and their layouts;
   - the **18** slice pictures of allpac0 as raw RGBA tiles: the 17 that `00_02` displays plus
     `BLACK` of `00_01` (the earlier "17" counted only `00_02`), and the four `CAUTION_*`
     screens of allpac3 (`tools/oracle/export_slice.py`, RR 7.3, `M0.md` "Slice export");
   - voices 0x3D00..0x3D08, vse #598 and bgm #33 as float PCM with their loops;
   - the font pages;
   - the eboot table subset.
6. **Build check and `Tsuki.Cli verify`** (A 4.1, RR 3.3).
7. **App skeleton.**
   - An SDL3 window with a GL 3.3 core context, clear and present, and an Alt+Enter fullscreen
     toggle.
   - Gamepad hot-plug events and the native folder dialog.
   - FFmpeg DLLs loaded from `third_party`.
   - `Tsuki.App --smoke` runs all of this and exits.
8. **Spikes.** Each spike has a written threshold and a planned fallback:

| spike | test | pass | fallback if it fails | result (M0 review; `docs/status/spikes.md`) |
|---|---|---|---|---|
| S1 loop | bgm #33 decoded through FFmpeg.AutoGen into our mixer; loop `[962000, 8257999]` inclusive; 3 loops rendered offline | seam sample-exact; C# float PCM equals the Python decode within 1e-6 | check decoder version parity (BtbN 9.0.1 vs Gyan 9.0.2); else LibAtrac9 behind the same interface | **PASS.** C# float PCM bit-identical to the oracle (0 of 16,516,096 samples differ; BtbN 9.0.1 = Gyan 9.0.2); 3-loop render 0 of 45,700,000 differ, seam error 0 frames; 0 underruns in 4 device runs (801 s); decode 1,518x real time. S1.md |
| S2 movie | `prologue_en.mp4` (388.7 s, H.264 + AAC) decoded on a thread, YCbCr shader, synced to the audio clock | A/V offset at the end <= 1 frame (33 ms); 0 dropped frames at 60 Hz; decode < 1 core | Media Foundation behind `IMovieDecoder` | **PASS.** 11,660 of 11,660 frames; last-frame offset -2.08 ms (L re-run -0.87 ms); 0 dropped, 0 late; 0.15-0.25 cores. Needs the nominal tick clock and the 4 ms cadence lock (A 3.4, 7.5). S2.md |
| S3 compositor | 10 full-screen layers at 1920 x 1080, each with a backdrop-reading blend (modes 11, 17, 19, 23, 24 of the opening) | <= 4 ms GPU per frame (hard limit 8 ms) | backdrop blit limited to the quad bounds + static-layer cache (A 6.3) | **PASS.** GPU p99 0.680 ms, max 0.737 ms (L re-run p99 0.573 ms); one 1080p backdrop blit costs 0.013 ms, not 0.1 ms; every blend mode within 1 level of `compose`; fallback not needed (A 6.3). S3.md |
| S4 giant picture | the largest picture (11,220 x 7,650 shown, 343 MB RGBA) through a C# MZX0 + MZP prototype, parallel tiles, sliced upload | decode <= 500 ms on workers; no presented frame > 16.7 ms during the upload | earlier prefetch distance; opt-in decoded cache (A 4.3) | **PASS with the five upload rules** (A 4.3). Read + decode 32 ms with 16 workers (L re-run 21.9 ms); 0 of 5,103 load frames over 16.7 ms; 154 of 154 picture SHA-1s equal. The prefetcher is bounded in decoded bytes and frames of lead time (8-40 frames). S4.md |

**Findings that changed the plan** (`docs/status/spikes.md` Summary; each is in ARCHITECTURE at
the section named):

1. **S2:** the 60 Hz logic tick reads the audio clock at its nominal tick time and applies a 4 ms
   cadence lock, or 30 fps frames get 1 or 3 ticks (judder; up to 89 of 299 frames in one run).
   The movie work of M2 and the loop of the app skeleton (H1) follow it (A 3.4, 7.5).
2. **S4:** the five upload rules belong in the `Tsuki.Render` upload scheduler, and the
   prefetcher is bounded in decoded bytes and frames of lead time, not by the PS4's
   12,000,000-byte budget, which IMG1481 alone exceeds (A 4.3).
3. **S3:** the measured backdrop blit is 0.013 ms (A 6.3), and the oracle needs the fp16
   round-toward-zero export model for T5 (b): 99.970 % of the aligned frame within 2 levels
   with it, 99.556 % without, against the 99.9 % required.
4. **S1:** the loop seam of bgm #33 has a step that the PS4 plays too, so the default stays
   engine-exact (decision 1, A 7.2), and the BtbN FFmpeg DLLs are an **LGPL v3** build, so the
   licence notices must say v3 (decision 10, A 0.5).
5. **S1 and S2:** headless sample-exact audio tests use the mixer output, SDL's postmix capture
   or SDL stream pull mode, never SDL's `disk` driver (A 7.1). Movies decode in software by
   default and never with `d3d12va` on one thread (A 7.5).

The spike code is in `src/Spikes/` (decision 6): in the solution build with 0 warnings, not in
the shipping build, and **deleted once M2 is accepted**. The wave-2 packages lift code from it
(`docs/status/PACE.md` 7.2).

**Acceptance**

1. `dotnet build src -warnaserror` and `dotnet test tests/Tsuki.Tests.Synthetic` are green, and
   so is the project-reference test.
2. `Tsuki.Cli verify --game <dir>`:
   - accepts the user's folder (eboot 12,767,802 bytes, SHA-256 `c2843415...b918`) and prints
     the FORMATS 2 counts: 49,620 triplet entries, allscr 552 sections, 5 x 43,969 text lines,
     16,464 voices, 134 movies;
   - rejects, with distinct messages, an in-memory eboot with one flipped byte and a folder view
     with one archive missing.
3. `Tsuki.App --smoke` exits 0 after it has:
   - created the GL 3.3 context;
   - compiled one shader as GLSL 3.30 and as GLSL ES 3.00 source;
   - opened FFmpeg and found the `atrac9`, `h264` and `aac` decoders.
   - *State:* not built at the M0 review (`Program.cs` is the scaffold placeholder). Wave-2
     package H1 builds it, reusing the spikes' window, shader and decoder-lookup code, and its
     60 Hz accumulator uses the nominal tick time of A 3.4.
4. Each hook is shown to block its case: an edit under `samples/golden/`, a new `Skip =` in a
   test, and committing a `.png` or a file under `samples/`.
   - *State:* 3 of the 4 cases are blocked; a `.png` outside `samples/` is accepted
     (`M0.md` "Hooks"). Closing the gaps changes persistent configuration, needs the user's
     consent and waits until wave 2 has finished (decision 8; package L-4).
5. `samples/golden/slice/manifest.json` exists, with counts and input hashes.
6. S1-S4 are measured, with numbers and verdicts in `docs/status/spikes.md`. A failed spike
   triggers its fallback, which is measured again within 2 days.
   - *State:* all four pass and no fallback was triggered.

**Owners:** L, H, A, D (S3, S4), E (S1, S2).

### M1: data layer (weeks 1-2, parallel)

**Scope:** all of `Tsuki.Data` except the UI extractor:

- HED8/HED4, NAM32/NAM8, MRG and mrgd00, standalone sections;
- MZX0 with the XOR scripts and LV;
- MZP in all its formats, with borders and tile flags;
- the catalog, picture names and NAM search, `_hh_vv` variants, faces and language bands;
- script_text, allscr and its label tables, the font pages;
- the SELF/ELF reader and every eboot-only table (RR 3.2), ui_text;
- `GameFolderSource`.

**Acceptance (T0 + T1, A 10.2)**

1. All 49,620 triplet entries and 891 standalone sections are located, with sizes equal to the
   payloads' own size fields. Bank starts are as in RR item 1.
2. The lookups of RR item 2 resolve as documented, including `img2511` -> record 2518 ->
   allpac1 #2157 and `img1696` with args 9/10.
3. MZX0 output equals the oracle on a stratified per-build sample, and on all streams in the
   nightly run.
4. Picture SHA-1s equal `tsuki.image`: on 2,000 stratified pictures per build, and on every
   picture nightly.
5. Text: 5 x 43,969 lines equal. Eboot tables equal: catalog 14,744 + 31 empty, name list
   14,744 + 1, scene ids 549, movie table 134, motions 24,192 / 71,905 keyframes, font lookups
   17,423, ui_text 5 x 1,499.
6. 19/19 HE-VAG decodes are equal, and the ATRAC9 float PCM is equal on all 17,925 streams
   (nightly).
7. The static census T0 is green, with the allow-list of 5 script bugs and 3 missing sprite
   bodies.
8. No code path reads a whole archive into memory, which a test enforces. Single-thread MZX0
   runs at >= 500 MB/s; numba reaches 868 MB/s.

**Owner:** A.

### M2: vertical slice (weeks 1-2)

**Scope**

- RR 7.2: caution stand-in, title stub, New Game, `00_01`, the prologue movie in the selected
  language, then the developer entry into `00_02` (scene 270) through page 1.
- RR 7.6: the retail path, second New Game, START_SCRIPT, then `00_00` up to its first voiced
  line (`_VPLY(D10_00_00_0000_0,03fb5)`).
- The commands of RR 7.4 and the 7.6 additions.
- The interim **M2a** runs the same flow on `GoldenSource` (target: end of week 1). **M2**
  itself runs on `GameFolderSource`.

**Acceptance (RR 7.5, in ja and en)**

1. The build check passes and the archive counts are reproduced.
2. The executed statement sequence of `00_01`, and of `00_02` up to `_PGST(2)`, equals the
   executed trace of the golden slice (`trace.steps` of `script_0002.json` / `script_0003.json`:
   99 and 240 steps), with the same argument values from both readers. It is **not** compared
   with the full statement list of `tsuki.script dump`: on a normal New Game `_IF__` jumps when
   its condition is false and F026 is 0, so both scripts skip statements 40..68, and `00_01`
   ends at `_RTMN` (offset 0x7D4), so its `_END_` never runs (RR 7.2, 7.5 check 2).
3. The prologue plays in the selected language variant (ids 4 / 46 / 47 / 106) to its last
   frame. Audio stays in sync within one frame, and `_TOMW` returns after the last frame.
   - The movie frame is chosen by the 60 Hz tick at its nominal time with the 4 ms cadence lock
     (A 3.4, 7.5). A test with a synthetic clock asserts exactly 2 ticks per 30 fps frame
     (S2: 1,348 and 1,308 frames at 2 ticks, 0 dropped, 0 late).
   - The spike played the movie in windowed segments (0-45 s, 55-65 s, 295-305 s and 345 s to
     the end) and decoded the whole file headless. A full 388.7 s windowed run was left for
     this check (S2.md 10).
4. img1685 resolves to catalog 1692, then allpac0 #270, and decodes pixel-identical to the
   oracle. The anchor lands on (240, 136) in script space.
5. Messages 3-5 lay out equal to `tsuki.textbox`, including the English breaks
   `... hangs / in the air.` and `... like layers of / grime.`. Against the 25 s frame of
   `prologue_en.mp4`, glyph x positions are within 3 px.
   - The 78 vs 81 px line pitch is measured and recorded for the calibration in A 11 #2.
6. `_VPLY(B90_00_01_0001_0,03d04)` plays voice entry 15,620 (about 2.25 s) together with
   message 3.
7. `MOON_SE03028` (vse #598) loops seamlessly and fades in over 8,000 ms to 30 %.
8. `_WTTM(4000,1)` lasts 240 frames. The reveal advances 0.476 counters per frame at text
   speed 35.
9. The page-1 frame equals the oracle render within the T5 (c) thresholds, and matches the 25 s
   movie frame by eye. It uses the real motion, blend and transition code (layers.md,
   compositing.md), not the stand-ins RR 7.4 allowed.

**Human check:** the user watches the opening in Japanese and in English, 30 minutes, and signs
off on the text look, reveal speed and voice timing.

**Owners:** C, D, B, E (integration by L). A provides `GameFolderSource`.

### M3: headless engine + explorer v1 (weeks 2-3)

**Scope**

- The complete VM: readers, variables, flow, markers and read tracking, choices (SELR, SELD,
  SELA, SELS, with result stores and locked options), script end, ATSV and scene bits.
- Stub handlers for every other command. A stub records its state writes but has no effect yet.
- `Tsuki.Cli explore` with campaigns, deduplication, coverage reports and failure conditions
  (A 10.3).

**Acceptance**

1. T2 is green for `vm_vars` (the 61,000 fuzz cases included) and for the choice model's result
   stores.
2. The explorer reaches all of the following through the real boot and title logic and the
   persistent state:
   - 100 % of the story scripts and extras reachable from the retail flow;
   - all 206 story choice options;
   - every ending;
   - all 25 extra scenarios;
   - ARCHIVE replay for every G[8].
3. The failure conditions of A 10.3 are all at 0, and two runs give equal trace hashes.
4. The exclusion list (Switch branches, debug scripts, `ZD`/`DMMY`) is reviewed and signed by L.
5. Throughput is >= 1 M statements per second per thread, and a whole-game campaign finishes in
   under 30 minutes on 16 threads.

**Owner:** C, with H for the report format.

### M4: full play loop (weeks 3-5)

**Scope**

- **Every handler real:**
  - all layer commands, including masks, crops, parents, STTR, STEC/SNX and SPFC/SPRD/SPRC;
  - `_FADS` and `FADE`;
  - the screen effects;
  - quake, shake and swing;
  - the full SoundModel with ducking and the voice gate;
  - every movie and the title loops;
  - the choice screen;
  - boot and the title state machine;
  - the message window and bubbles, MSQK, FCSP and FCLN.
- **The prefetcher** (A 4.3), bounded in decoded bytes and frames of lead time (8-40 frames
  ahead of the first draw) instead of the PS4's 12,000,000-byte budget, feeding the upload
  scheduler with its five rules (S4).
- **Oracle tools:** the oracle renderer, trace replay and the worst-50 review page (H). The
  renderer and T5 (b) use `compose` with the fp16 round-toward-zero export model (A 6.3).

**Acceptance**

1. T2 is green for the layer model, SoundModel, `textbox` (601 golden cases plus 175,876
   lines), the choice and title draw lists, and trace replay along the explorer paths.
2. T5 (a) and (b) are green: the display lists equal the models, and the GPU executor matches
   `compose` for every blend mode, blend state and transition.
3. Explorer v2, now with the real handlers, still covers 100 % with 0 failures.
4. T6 decode-all is green nightly.
5. T5 (c) passes on the checkpoints from New Game through the end of chapter 1. Every failure is
   triaged as a port bug or an oracle bug.
6. T7 is green on the opening.
7. The PS4 effect captures (section 5) are compared, and every mismatch is triaged. A mismatch
   that the Python and the C# code share is fixed in the spec document first, then in both.

**Human check:** the user reads from New Game through the end of chapter 1 in Japanese, and
signs off on text speed, reveal, auto timing, transitions and the voice/BGM balance.

**Owners:** D, E, B, C, with H for the oracle tools.

### M5: ADV features + functional menus (weeks 4-6)

**Scope**

- **History and navigation:**
  - the history ring (800 records);
  - rewind (hold), previous and next scene;
  - the backlog with voice replay and jump.
- **Saves:** 0..99 manual, 100..138 auto FIFO and 139 quick, plus Continue and the save
  container (A 8.3).
- **Reading modes:** read tracking, auto, and skip/fast-forward with "read text only" and
  "stop at choices".
- **Config:** every option with persistence, on the faithful config screen.
- **Language:** switching on any page.
- **Tier-1 screens** (A 9.2): the system menu, save and load, the backlog, the SELA and SELS
  prompts, the confirm dialogs.
- **Shell:** the keyboard, mouse and gamepad map, with rebinding (A 9.4).
- **The reconciler** (A 7.4).

**Acceptance**

1. T4 shows 0 differences, at every page start on the explored paths:
   - save -> load -> save is byte-identical;
   - continuing 2,000 statements after a load matches the uninterrupted trace;
   - rewind, backlog jump and previous/next scene match on a 1 % sample;
   - the reconciler invariant holds after each.
2. `check_save_image` accepts our slot records, and 39 autosaves rotate in FIFO order.
3. Config and the system data survive a restart, and first-run values equal the engine
   defaults (adv_state 9).
4. The DevBridge menu smoke script passes. It opens every tier-1 screen and uses every action
   of engine 8.3 except the gallery and the flowchart.
5. Switching the language on 20 sampled pages re-lays out the current page equal to
   `tsuki.textbox`.

**Human check:** the user spends 30 minutes using every menu with mouse, keyboard and gamepad.

**Owners:** F, G, with C and E.

### M6: entire game playable start to finish, release candidate 1 (end of week 6-7)

**Scope:** close every gap in the A 10.7 checklist, then build `RC1` with
`dotnet publish -c Release -r win-x64 --self-contained`, plus the FFmpeg DLLs and the notices.

**Acceptance:** all nine points of A 10.7, in short:

1. T0-T2 are green.
2. The explorer covers 100 % in all four languages, with 0 failures and deterministic results.
3. T6 is green.
4. T4 is green.
5. T5 is green or every failure is accepted.
6. T7 is green.
7. The tier-1 menu smoke passes.
8. Human checks:
   - chapter 1 is read in Japanese (done at M4);
   - every ending is played through its credits from the explorer's save sampler;
   - English is spot-checked on 3 pages.
9. RC1 starts from the folder picker on a second Windows account with an English locale.

**Proof:**

- `docs/status/M6.md` with the coverage numbers, test totals and the accepted-failure list;
- `artifacts/M6/` with the coverage reports, the review page and the explorer saves;
- the user's written sign-off in chat.

**Owners:** all streams, with L integrating and H running the gate.

### M7: faithful menus, gallery, flowchart, extras (weeks 6-10)

**Scope**

- **The extractor.** `tools/eboot/uicalls.py` becomes a tested module, then is ported to C# on
  Iced as `Tsuki.Data.UiLayoutExtractor`, which runs at first run.
- **Tier-2 draw lists:** the system menu and mode panel, save and load, the backlog, and the
  SELA and SELS prompts.
- **Menu transitions:** 18, 20..24 and 68..70 are decoded and replace the cross-fade stand-in.
- **The gallery** (gallery.md):
  - CG: 458 groups, 1,972 entries, 105 aliases;
  - the music player: 95 records;
  - the voices, the movie gallery and the ending list (25);
  - the extra scenarios.
- **The flowchart:** 277 columns, 1,286 nodes and 630 edges, the filter and the line drawing,
  and replay through ARCHIVE (scene 3000, G[8], F024 = 1).
- **Effect stand-ins:** the remaining [H] items of A 11 #4 are decoded from the disassembly.
- **Audio option (optional):** a non-default setting that crossfades the loop seam of bgm #33
  over 2-5 ms. The default stays engine-exact (decision 1; S1.md 10 #1; A 7.2).

**Acceptance**

1. The C# extractor output equals `uicalls.py` on every screen, and the extracted title and
   choice draw lists equal the existing oracle draw lists.
2. Unlock behaviour equals gallery.md in tests over constructed persistent states:
   - scene_seen, scene_done, edge_seen, movie_seen;
   - picture_seen, bgm_heard, choice_taken.
3. Every flowchart node that the PS4 allows to be replayed is replayable.
4. Every menu entry of engine 8.2 and 8.3 is reachable.
5. Every screen is compared side by side with the user's PS4 captures, and the user accepts it.
   A screen whose extraction stalled stays tier 1 and is listed in A 11.

**Owners:** G (two agents), D, F, H.

### M8: release 1.0 (weeks 9-11; risk bound about 16)

**Scope**

- **Graphics fallback:** ANGLE, selected automatically on a GL init or shader failure, or by
  flag. The source of the prebuilt DLLs is confirmed here. S3 compiled all 27 programs as GLSL
  ES 3.00 on the AMD driver, but nothing has run on ANGLE itself yet (S3.md 6 #3).
- **Locale and paths:** tests on an English-locale account and from a non-ASCII install path.
- **Performance:** prefetch tuning and the 1-hour soak (T7), which also measures the residual
  upload hitch rate (S4: 0 of 5,103 load frames over 16.7 ms with the five rules, 4 of 805
  without the staging pool) and 60 Hz output (only computed so far, A 6.2).
- **First run:** the verify screen in ja and en.
- **Packaging:** the release allow-list scan (A 10.8); licence notices (our MIT, FFmpeg
  **LGPL v3**, SDL zlib, ANGLE BSD); `RELEASE.md` with setup steps. The FFmpeg build in use
  reports "LGPL version 3 or later", so every notice says v3 (decision 10). Whether the DLLs
  ship at all is decided here; the fallback is the Media Foundation decoder below.
- **Optional (P3):** the Media Foundation movie decoder and the PS4 `savedata000` import.

**Acceptance**

1. The full T0-T8 suite is green.
2. The release scan finds no file outside the allow-list.
3. A clean account plays from the folder picker to the end credits of one route, using the
   zip alone plus the user's folder.
4. The user's final sign-off.

**Owners:** H, D, G, L.

---

## 4. The very first concrete steps

The commands are PowerShell, run from the repository root.

*Status after the M0 review:* steps 1-9 have been carried out (`docs/status/M0.md`) except where
a "Done:" note in steps 3, 5, 6, 8 and 9 below says what differs or is still open. The text
stays as the record of the intended order.

**Step 1 (the user, about 10 minutes).** Install the two tools and set the game folder:

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e
winget install --id BtbN.FFmpeg.LGPL.Shared.9.0 -e
# the folder that holds eboot.bin, image\, data\, sce_sys\ (the tools, tests and programs read this variable; there is no built-in default):
setx TSUKI_GAME_DIR "D:\Games\CUSA47999"
```

Then say in chat that agents may:

- create a local git repository (no remote, nothing pushed);
- install the project hooks of A 10.8.

**Step 2 (L, day 1).** Repository hygiene: `git init`, the `.gitignore` additions of M0
scope 2, the pre-commit allow-list hook and `.claude/settings.json` with the hooks. Then show
`git status` listing nothing under `samples/`, `scratch/` or `ref/`.

**Step 3 (L, day 1).** Create the solution skeleton:

```powershell
dotnet new sln -n Tsuki -o src
'Tsuki.Data','Tsuki.Engine','Tsuki.Media','Tsuki.Render','Tsuki.Platform','Tsuki.Ui','Tsuki.DevBridge' |
  ForEach-Object { dotnet new classlib -n $_ -o "src/$_" -f net10.0 }
'Tsuki.App','Tsuki.Cli' | ForEach-Object { dotnet new console -n $_ -o "src/$_" -f net10.0 }
'Tsuki.Tests.Synthetic','Tsuki.Tests.Data','Tsuki.Tests.Render' |
  ForEach-Object { dotnet new xunit -n $_ -o "tests/$_" -f net10.0 }
Get-ChildItem src, tests -Recurse -Filter *.csproj |
  ForEach-Object { dotnet sln (Get-ChildItem src -Filter 'Tsuki.sln*').FullName add $_.FullName }
```

Then add:

- `Directory.Build.props` (A 3.1);
- `Directory.Packages.props`, pinning `ppy.SDL3-CS` 2026.1002.1, `Silk.NET.OpenGL` 2.23.0,
  `FFmpeg.AutoGen` 9.0.1.1, `Iced`, and xUnit at the current stable versions;
- `Tsuki.App` set to `WinExe`;
- the project-reference test.

*Done:* both props files are at the **repository root**, not in `src/` (decision 6), so that
`tests/` inherits them. The solution is `src/Tsuki.slnx` (the new XML solution format, which
`dotnet build src` finds), it also lists the four spikes, and the xUnit packages are v3
(`xunit.v3.mtp-off`, 4.0.2).

**Step 4 (H, day 1).**

- `tools/setup/ffmpeg_dlls.ps1` copies `*.dll` from the winget package's `bin` folder into
  `third_party/ffmpeg/bin`, and prints the avcodec version.
- `tools/setup/check_env.ps1` checks the .NET 10 SDK, the DLLs, the `.venv` (numpy, pillow,
  pytest, numba, capstone, unicorn) and `TSUKI_GAME_DIR`.
- The existing Python suite must still pass:
  `$env:PYTHONPATH="tools"; .venv\Scripts\python.exe -m pytest tools\tests -q`.

**Step 5 (L, day 1).** Write the contracts of A 3.2 in `src/Tsuki.Engine/Contracts/`:

- `IGameData`;
- `Statement`, `IHost`, `StepResult`;
- `DisplayList` and `DrawCmd`;
- `AudioCommand` and `MovieCommand`;
- `InputFrame`;
- the state-block accessors, generated from the field maps of layers 3, adv_state 2 and 9,
  sound_runtime 2.2, and vm_variables 2.

After this, they change only through L.

*Done:* `IGameData`, the VM contracts, `DisplayList`, `AudioCommand`/`MovieCommand` and
`InputFrame` exist. The state-block accessors were not part of M0; they are lead package L-1 of
wave 2 (`PACE.md` 7.1), which streams C (variables), D (scene state), E (sound channels) and
later F need.

**Step 6 (H, days 1-2).**

- The `tools/oracle/` runner: a common manifest writer (tool commit, input SHA-256, counts) and
  output under `samples/golden/<area>/`.
- `tools/oracle/export_slice.py`, exporting the slice set of M0 scope 5.
- `GoldenSource` in `Tsuki.Data`, compiled only in dev builds, reading that export.

*Done:* the runner and `export_slice.py` (87 files, 0 problems on `--verify`). `GoldenSource`
was not started in M0; it is wave-2 package H2, after the app skeleton H1 (`PACE.md` 7.2).

**Step 7 (A, days 1-2).** `Tsuki.Data.BuildCheck` (RR 3.3) and `Tsuki.Cli verify`, with the
acceptance tests of M0 point 2. Then the containers (HED/NAM/MRG) and MZX0, test-first against
`tools/oracle/containers.py`.

**Step 8 (E and D, days 1-3).** Spikes S1-S4 in `src/Spikes/` (`S1.AudioLoop`, `S2.MovieSync`,
`S3.Blend1080`, `S4.BigPicture`). The folder is part of the solution build (0 warnings) but not
of the shipping build, and it is deleted after M2 is accepted (decision 6). Results go in
`docs/status/spikes.md` and `docs/status/spikes/S1.md` .. `S4.md`.

*Done:* all four passed (see the M0 spike table). The planned top-level `spikes/` folder became
`src/Spikes/` because the skeleton put the spikes under `src/`; the earlier wording "not part of
the solution build" no longer holds.

**Step 9 (L, day 3).**

- Review M0: re-run every acceptance command and write `docs/status/M0.md`.
- Start the second wave with these first tasks:
  - **A:** MZP images, the catalog, then text and scripts.
  - **B:** port `Text_Layout` to C#; the 601 golden cases first, then the 175,876 lines.
  - **C:** segmenting and both argument readers against the slice segment lists, then
    variables (`vm_vars` fuzz cases), then flow.
  - **D:** the display-list executor; the normal and fixed-function blend states; the 24 blend
    shaders against `compose` on synthetic lists; the `_FADS` 0/1/2 capture path.
  - **E:** the mixer (pan law, ramps, loops), voice, SE and BGM playback, the movie thread from
    S2.
  - **G:** turn `scratch/misc_boot_title_choice_flow/calls.py` into `tools/eboot/uicalls.py`
    with tests that reproduce the title and choice draw lists; then run it on the system-menu
    entry 0x6758E.

*Done:* the M0 review is `docs/status/M0.md`. The wave-2 packages as launched, with owner
folders, sizes and the spike findings each one takes, are in `docs/status/PACE.md` section 7.
Package D2 (layer model) waits for the layer-motions research to land (decision 9).

**Step 10 (L, by the end of M2).** Write `docs/status/ps4_captures.md`: the exact list of
console screenshots and short videos the user is asked to take (section 5), each with how to
reach it on the PS4.

---

## 5. What only the user can do

| when | action | why |
|---|---|---|
| day 1 | run the two `winget` commands and the `setx` line of step 1 | toolchain and game location |
| day 1 | approve a local git repository and the project hooks | hooks enforce the rules (A 10.8); changing configuration needs the user's consent |
| end of M2 | 30-minute review of the opening in ja and en | feel: text look, reveal, voice timing |
| before M4 | take the effect captures listed in `docs/status/ps4_captures.md` on the PS4 (Share screenshots / short clips), copied to `samples/ps4/` | the only ground truth against misunderstandings that the Python and C# code would share (A 10.6) |
| M4 | read New Game through the end of chapter 1 in Japanese | feel sign-off on the play loop |
| M5 | 30 minutes with every menu, using mouse, keyboard and gamepad | usability of the tier-1 screens |
| M6 | play each ending from the explorer's saves; spot-check English; start RC1 on a second Windows account with an English locale (only the user can create that account) | the "playable start to finish" gate |
| before M7 | take the menu captures (system menu, save and load, backlog, SELA prompt, gallery, flowchart) | reference for the faithful layouts |
| M7 | accept each screen side by side with the captures | fidelity of the menus |
| M8 | final playthrough from the zip | release sign-off |
| optional, any time | a read-only copy of a real PS4 `savedata000` | save-layout check (RR 9.5) and later save import |

---

## 6. Working rules for agents

1. **One owner per folder.** Changes to another stream's folder go through that stream or L.
   Contract changes go through L only.
2. **Test-first against the oracle.**
   - A module is done when its differential test is green on the real data, not when it
     compiles.
   - Each stream writes the oracle exporter for its own area.
3. **Easiest items first, then by similarity.** Command handlers are grouped by family, and the
   order follows the slice, then the opening, then chapter 1, then the rest.
4. **Non-interactive batches.** Each batch ends by updating `docs/status/<stream>.md` with what
   was done, the next step, blockers and the measured numbers.
5. **No skipped tests, no edits to generated or golden files.** The hooks enforce this. The only
   allowed skip is the central "game data missing" attribute.
6. **No game data in commits.** That covers pictures, audio, text, tables and extracted layouts.
   Frames, traces and diffs go to `artifacts/`. Reports quote numbers, never game text.
7. **Proof means re-running commands.** A prose description of a screenshot is not
   verification. The human judges feel, and the tests judge correctness.
8. **Spec first when the PS4 disagrees.** If a PS4 capture contradicts both the Python and the
   C# code, fix the `docs/formats/` document and the Python model first. Then make the C# code
   follow through the differential test.
9. **GPU and timing measurements run one at a time** (decision 4). A measurement holds the lock
   file `artifacts/gpu.lock`: create it exclusively, delete it when done, wait and retry if it
   exists, and treat a lock older than 30 minutes as stale. Functional GPU tests that assert
   pixels, not timings, do not need the lock. M0 showed why: S2 lost a frame in 2 of 23 runs to
   swap stalls under load, and S4's results depended on machine load (`PACE.md` 4, R8).
10. **Parallel builds use per-agent output folders** (decision 7): build and test with
    `--artifacts-path artifacts/<package-id>`, so that agents never lock each other's
    binaries (three M0 streams hit file locks on running spike outputs). Never leave a built
    app, spike or test host running after a command.

---

## 7. Re-plan triggers

| trigger | response |
|---|---|
| a spike in M0 fails its threshold | apply its listed fallback and measure again within 2 days |
| M2 is more than one week late because of the shell (window, GL, audio, movie interop), not the engine logic | L re-plans the shell (D3D11 via Vortice, Media Foundation, other bindings) and keeps everything else |
| the explorer state space grows beyond 30 minutes per campaign | hash only the variables that are read later (liveness), bound the branches per choice block |
| layout extraction of a screen exceeds twice its estimate (A 9.3) | keep the tier-1 screen, list the gap in A 11, move on |
| a T5 (c) mismatch is shared by Python and C# against a PS4 capture | rule 8 of section 6 |
| a stream's status shows no green test for 2 consecutive batches | L reviews and splits the task or reassigns it |
