# Pace: what M0 took, and what M1, M2 and M6 will take

Written by L at the M0 review on 2026-10-25, about 18:00-18:20 local time (UTC-4). Every
duration below comes from a log, a file timestamp or a command that was run. Estimates say
which measured rate they use. Re-measure after wave 2 and correct this file: the estimates rest
on one milestone of evidence.

## 1. Sources

| source | what it gives | precision |
|---|---|---|
| m0-prep workflow log (the task output of the `tsuki-m0-prep` run) | per agent: `startedAt`, `durationMs`, tokens, tool calls | exact |
| main M0 workflow | its output file was created at 16:09:40 and stays empty until the run ends, so there are no per-agent times | start time only |
| file times of each stream's folders and outputs (`src/...`, `samples/spikes/s1..s4`, `samples/golden_data`, `scratch/*.tsv`), first creation to last write | per-stream span | +-3 min; the start is the first artefact, so the agent's reading time is not in it |
| git | `cd94425` 16:08:09 (baseline), `3851949` 17:08:13 (m0-prep commit) | exact |
| L's shell | review commands and their wall times | exact |
| `wc -l` over `.cs` / `.py` / `.ps1` (no `bin/`, `obj/`) | lines written, with comments and blank lines | exact |

Wave-1 launch time: about 16:28 (inferred). The scaffold's last file is 16:27:29 and the first
wave-1 artefact appeared at 16:37:05.

## 2. M0 timeline (2026-10-25, local time)

| time | event |
|---|---|
| 16:01:43 | m0-prep workflow starts its `oracle-export` and `setup-scripts` agents |
| 16:08:09 | baseline commit `cd94425` (research, Python oracle, design) |
| 16:09:40 | main M0 workflow starts; scaffold agent |
| 16:20:15-16:27:29 | scaffold files (props, slnx, 16 csproj, contracts, rule tests, FFmpeg copy) |
| about 16:28 | wave 1 launched: data, S1, S2, S3, S4 |
| 16:36:04-16:36:17 | golden slice exported (87 files, 511 MB) |
| 16:37:05-16:41:11 | first artefact of every wave-1 agent (S4 16:37:05, data 16:37:54, S1 16:38:00, S2 16:39:01, S3 16:40:42) |
| 16:37:32 / 16:42:22 | m0-prep `oracle-export` / `setup-scripts` done; `verify-oracle` starts 16:42:30 |
| 17:07:10 | m0-prep `verify-oracle` done; commit `3851949` at 17:08:13 |
| 17:07:23 | S3 report final |
| 17:08:41 | data stream's last output (golden_data manifest) |
| 17:19:27 | S1 report final |
| 17:28:41 | S2 last source change (report 17:28:30) |
| 17:34:44 | S4 report final: **all M0 streams done** |
| 17:35-18:20 | M0 review (this file, `M0.md`, `spikes.md`); 12 min of it machine time |

**M0 took 1 h 33 min of agent wall-clock** (16:01:43 to 17:34:44), plus a 45-minute review.
ROADMAP planned "days 1-3". Two acceptance items are still open (`M0.md`): the app skeleton
and the hook gaps. They are not in that figure.

## 3. Work done per stream

| stream (agent) | span (min) | delivered | lines written | verified |
|---|---:|---|---|---|
| m0-prep `oracle-export` | 35.8 (exact) | `tools/oracle` runner, manifest, slice exporter; 87-file golden slice | 2,070 + 314 test | 178 cross-checks, re-export identical, `--verify` 0 problems (L re-ran it). 471k tokens, 142 tool calls |
| m0-prep `setup-scripts` | 40.6 (exact) | check_env, ffmpeg_dlls, capture targets, 4 status docs | 1,570 (+ docs) | check_env 0 missing / 0 failed. 465k tokens, 126 tool calls |
| m0-prep `verify-oracle` | 24.7 (exact) | independent review, `followups.md` | - | 362k tokens, 106 tool calls |
| scaffold | about 18 | 16 projects (12 + 4 spike skeletons), central props, contracts, rule tests, FFmpeg wiring | 1,209 contracts + 487 tests + project files | build 0 warnings; 19 tests |
| data (A) | about 41 | HED/NAM/mrgd00/MRG, MZX0 + LV, build check, `verify`, `decode-all`, `bench-mzx` | 3,304 production C# + about 2,600 test C# + 700 Python generators | **all 1,104,762 MZX0 streams equal to the oracle** (L re-ran: 27.9 s); verify accepts and rejects (L re-ran); 92 data + 68 synthetic tests |
| S1 (E) | about 51 | ATRAC9 decode, mixer, SDL output, seam analysis | 1,910 C# + 202 Python | bit-identical PCM; 0 seam error; 4 device runs, 0 underruns (L re-ran headless) |
| S2 (E) | about 61 | movie pipeline, YCbCr shader, A/V clock, hw-decode survey | 3,031 C# + 96 Python | 0 dropped, -2.08 ms at the end; 23 windowed runs (L re-ran 2) |
| S3 (D) | about 39 | 27 GLSL programs, compositor, rule wipe, 419-case verifier | 1,451 C# + 536 Python | every mode within 1 level; GPU p99 0.68 ms (L re-ran) |
| S4 (D) | about 67 | MZX0 + MZP prototype (4 formats), upload scheduler, 216 upload runs | 1,890 C# + 475 Python/PowerShell | 154 / 154 SHA-1; 0 of 5,103 frames over 16.7 ms (L re-ran 3 runs) |
| review (L) | about 45 | re-runs, these three status files | - | `M0.md` |

Totals:

- **Code:** about 21,800 lines written in about 6.3 agent-hours of stream work, about 3,500 lines
  per agent-hour on average. That counts the spikes and the m0-prep tools; docs are excluded.
- **Concurrency:** up to 8 agents ran at once, from 16:30 to 16:42.
- **Slot use in wave 1:** 259 agent-minutes of work in a 67-minute wave across 5 slots, so 77 %.
  S3 finished 27 minutes before S4 and its slot sat idle.

## 4. Rates the estimates use

| id | rate | value | evidence |
|---|---|---|---|
| R1 | oracle-backed port, best case | **about 1,400 lines of Python oracle per agent-hour**: about 4,700 production C# lines/h, or 9,400 lines/h with tests | data stream: `mrg.py` 376 + `nam.py` 161 + `mzx.py` 459 = 996 oracle lines, plus RR 3.3, became 3,304 production C# lines plus tests in about 41 min, equal on 100 % of the real data. The formats were fully specified and self-contained |
| R2 | build + measure (spike-like work) | **2,100-3,200 lines per agent-hour** | S1-S4: 1,987-3,127 lines in 39-67 min, most of it spent measuring |
| R3 | ramp-up per package | **8-13 min** from launch to the first file, 15-30 % of a 40-65 min package | the first-artefact times of the five wave-1 agents |
| R4 | package length | **25-67 min**; the 3 m0-prep agents each finished on attempt 1, and all 5 wave-1 reports came back done (retries in the main workflow are not visible yet) | m0-prep log; the file spans above; the stream reports |
| R5 | wave cycle | **about 2 h**: the slowest package (67 min) plus a review (45 min) plus launch slack | M0 |
| R6 | review machine time | 12 min: full build x 3, 3 test projects, pytest 98 s, verify, 4 spike re-runs, decode-all 28 s + Python hashes 33 s | L log, `M0.md` |
| R7 | context per agent | 362k-471k tokens and 106-142 tool calls for a 25-41 min agent | m0-prep log |
| R8 | machine contention | Code-writing agents did not slow each other measurably. Measurements did: S2 lost 1 frame each in 2 of 23 runs to swap stalls under load. Builds did too: 3 streams hit MSB3021/3026/3027 file locks on the outputs of running spikes | S1, S2 and data reports |

What M0 does **not** measure: integration (every M0 package was independent of the others),
debugging a mismatch against the oracle (the data stream had none), and work that has no oracle
(UI, ADV features, the explorer). The estimates add explicit allowances for these.

## 5. Estimates (continuous parallel agent work)

Assumptions:

- 6 code-writing agents plus L, on this machine.
- Packages are sized like M0's, at most about 1 h of work.
- A dependent wave launches as soon as its inputs are reviewed, and queued packages take free
  slots at once.
- One review of about 45-60 min sits between dependent waves.
- **Time spent waiting for the user is not included**; it is listed separately.

Each estimate is computed twice: wave count along the dependency chain x R5, and the volume of
oracle to port at R1 (and at half of R1 for harder code). The larger of the two governs.

### M1: data layer (T0 + T1)

- **Already green**, re-run by L: M1 acceptance 1 (49,620 entries, 891 sections, sizes, bank
  starts), 3 (MZX0 on the sample and on all streams) and 8 (no whole-archive reads; single
  thread at or above 500 MB/s in every class).
- **Remaining:** acceptance 2, 4, 5, 6 and 7, plus `GameFolderSource`. The Python to port is
  about 7,400 lines:
  - `image` + `mzp` 826 (S4's prototype already gets 154 / 154 SHA-1s);
  - `selfelf` + `uitext` 619;
  - `catalog` + `image_catalog` + `catalog_eboot` 1,838;
  - `text` + `text_refs` 1,334;
  - `font` + `font_eboot` 1,322;
  - `script_tables` 377;
  - `audio_hevag` 268 plus the RIFF / `fact` parsing;
  - the census, about 500.
- **Volume:** 7,400 / 1,400 = 5.3 agent-hours at R1, or 10.6 at half rate.
- **Chain:** 2 waves (A1 + A2, then A3 + the audio part of E1) and 2 reviews.
- **Estimate: 4-6 h after the wave-2 launch** if a second agent joins stream A after the first
  hour, as the queue below does (range 3-9 h); **6-9 h** with one A agent throughout.
- **Not yet measured:** the all-pictures SHA-1 sweep over 35,375 containers on the Python side,
  and the ATRAC9 comparison on 17,925 streams. Package A1 must time both first.

### M2: first playable slice (RR 7.5 checks 1-9, ja and en)

- **Python on the critical path:** about 11,300 lines:
  - B: `textbox` 1,441, plus about 500 of text expansion;
  - C: `script` 1,220, `vm_vars` 768, `misc_vm_variables_errata` 1,608;
  - D: `compose` 765, transitions 293, `misc_layer_state_and_motions` 2,330;
  - E: `audio` 808, `audio_hevag` 268, `misc_audio_runtime_semantics` 1,295.

  On top of that comes code with no oracle: the main loop and host glue, `GoldenSource`, and the
  oracle renderer for page 1. The spikes give working prototypes for the shell parts: decoder and
  mixer (S1), movie thread (S2), shaders and compositor (S3), upload scheduler (S4).
- **Volume:** 8-16 agent-hours of porting plus 3-5 of new code, so 11-21 agent-hours. Over 6
  slots that is 2-3.5 h, shorter than the chain.
- **Chain:** 3 waves.
  1. Wave 2: the packages below.
  2. Wave 3: M2a integration on `GoldenSource` (the RR 7.4 commands, layer model, SoundModel,
     reveal, oracle renderer).
  3. Wave 4: switch to `GameFolderSource` (needs M1), run checks 1-9 in both languages, fix.

  3 cycles x 2 h = 6 h, plus 30-60 % for integration rework, which M0 could not measure.
- **Estimate: 7-10 h of agent work after the wave-2 launch (range 6-14 h)**, plus the user's
  30-minute review of the opening (ROADMAP M2 human check).

### M6: entire game playable start to finish (RC1)

- **Volume.**
  - ARCHITECTURE 13 sizes the port at 45-50k production C#. 3.3k is done, and the spikes hold
    8.3k lines of reusable prototypes.
  - That leaves about 42-47k production lines plus tests, at the data stream's ratio of 0.8, so
    76-85k lines.
  - At R1's 9,400 lines/h that is 8-9 agent-hours; at R2's 2,100-3,200 lines/h it is 24-40
    agent-hours. Over 6 slots that is 1.5-7 h of writing.
  - Writing is therefore not the bottleneck.
- **Chain** (ROADMAP 2 dependencies), from now:

  | milestone | wave cycles |
  |---|---:|
  | M1 / M2 | 3 |
  | M3 (VM complete, explorer v1, 1 M statements/s) | 2 |
  | M4 (94 handlers by family, effect stand-ins, oracle renderer, trace replay, T5 through chapter 1, T7) | 3-4 |
  | M5 (history, rewind, backlog, 140 slots, auto / skip, config, language, tier-1 screens, input map, T4) | 3 |
  | M6 (4-language explorer at 100 %, gap closing, RC1) | 2 |

  That is 13-14 cycles x 2 h = 26-28 h. The reviews grow later on (explorer campaigns up to
  30 min, the 1-hour T7 soak, render checkpoints) and fixes recur, so add 20-60 %.
- **Estimate: about 30-45 h of continuous agent work after the wave-2 launch (range 25-60 h),
  or 1.5-2 days around the clock**, not counting user time.
- **User-gated, outside that figure:**
  - M2: 30 min of the opening in ja and en;
  - the P1 PS4 captures before M4 (`ps4_captures.md`);
  - M4: reading New Game through the end of chapter 1 in Japanese;
  - M5: 30 min with every menu;
  - M6: every ending played through its credits from the explorer's saves, an English spot
    check, and RC1 on a second Windows account that only the user can create.

  At ROADMAP's assumption of 30-60 min of user time a day, these alone take **1-2 weeks of
  calendar**, and they, not the agents, set the M6 date. With a few hours a day from the user,
  M6 is 3-5 calendar days.

**Confidence: low to medium.** The evidence is a single milestone with no integration work.
Re-plan triggers for these numbers: a wave-2 package that runs over 90 min, or a review that
sends more than 1 of 6 packages back. Recompute from the wave-2 times then.

## 6. Main risks

1. **Integration and debugging were not measured.** M0's packages were independent and matched
   the oracle on the first try. M2-M6 are chained, and each fix-up round costs about one wave
   cycle (2 h).
2. **Oracle gaps on the M2 path.** M2 checks 7-9 have no golden data yet: the SoundModel trace,
   `_WTTM`, reveal timing, motion samples and the page-1 render. `tools/oracle/render.py` does
   not exist, and the retail path is not exported (`followups.md`). `compose` lacks the fp16
   export model that T5 (b) needs (S3). Until H3 delivers, M2 check 9 and every T5 (c) check
   are blocked.
3. **Long tail without an oracle.** These are documented stand-ins, to be decoded from the
   disassembly: quake / shake / swing, crop and mask feather, `STCF`, `FADE` effect 4, easing 70,
   the title particles (A 11 #4), and the engine-only menu transitions. The tier-1 UI (about 7k
   lines) and the ADV features have the thinnest oracle coverage.
4. **One machine, one GPU, one 144 Hz monitor.**
   - GPU and timing tests disturbed each other in M0 (S2 swap stalls, S4 on a busy machine).
   - Builds collided on locked spike outputs.
   - 60 Hz behaviour was only computed, never measured.

   Mitigations: run GPU and timing jobs one at a time, delete the spikes after M2 as planned, and
   measure 60 Hz on a 60 Hz mode once the user allows the display setting.
5. **The lead is a serial step.**
   - Contracts change only through L, and the state blocks are still missing; C, D, E and F
     need them.
   - Every dependent wave waits for a review.
   - Only L commits, and all of M0 is still uncommitted.
   - A concurrent workstream has uncommitted edits in `docs/formats/layers.md` and
     `tools/tsuki/misc_layer_state_and_motions.py`, which D's layer model ports.
6. **User-gated steps** (section 5) and the PS4 ground truth for shared misunderstandings
   (ARCHITECTURE R2). Without the P1 captures, M4 acceptance 7 cannot be closed.
7. **Agent context.** A 25-41 min agent used 362k-471k tokens. Packages much larger than M0's
   risk running out of context mid-task, so keep them at about 1 h, as below.
8. **Tooling gaps that can let bad work in:**
   - the hooks accept `Assert.Skip`, `pytest.skip()` and binaries under allowed folders;
   - `Tsuki.Tests.Render` has no tests yet;
   - `TSUKI_GAME_DIR` is not set in the agents' processes, so every tool relies on its fallback
     path.

## 7. Wave 2: work packages to launch in parallel

### 7.1 L's own items (not a code slot; do L-0 and L-1 first)

| id | item | where | why now |
|---|---|---|---|
| L-0 | Commit the M0 work after reading `M0.md`. Leave out the five layer-motion files of the other workstream | git | gives wave 2 a clean base; `M0.md` lists the files |
| L-1 | State-block contracts (A 3.2): `SceneState` 0x1A08, `HistoryRecord` 0x3A10, `SoundChannelRecord` 0x34, `ConfigBlock` 0x140, `SystemBlock` 0x18AC, `Vars` F[1200] / Z[1400] / G[2000]. Each is a byte array with typed accessors generated from the field maps (layers 3, adv_state 2 and 9, sound_runtime 2.2, vm_variables 2), plus contract tests | `src/Tsuki.Engine/Contracts/` (generated part under `Contracts/Generated/`) | C (vars), D2 (scene state), E2 (sound channels) and later F need them |
| L-2 | ARCHITECTURE updates from the spikes | `docs/ARCHITECTURE.md`, `docs/ROADMAP.md` | the S2 tick-clock rule and cadence lock (3.4, 7.5); the S4 upload rules and decoded-byte prefetch bound (4.3); the measured blit cost of 0.013 ms (6.3); FFmpeg LGPL v3 (0.5, 13); the audio test boundary of pull mode or postmix (7.1); the spike folder; the `followups.md` doc items (RR 7.2 correction, 17 vs 18 pictures) |
| L-3 | Decisions | `M0.md` / here | Keep the S1 seam engine-exact with no crossfade option before M7 (recommended). Move the data stream's generators to `tools/oracle/data.py` or keep them (recommended: move them in A1). Keep `GameFolderLocator`'s default path for dev builds only. Run GPU and timing jobs one at a time |
| L-4 | Hook gaps, **with the user's consent** | `tools/setup/hooks/` | reject binaries and `.png` outside the allow-list; reject `Assert.Skip` and `pytest.skip(`; check `Skip =` at edit time; guard `src/**/Generated/**` and test deletion; size limit 1 MB per A 10.8; add `install_hooks.ps1`. Then repeat the four demonstrations (M0 acceptance 4) |

### 7.2 Code slots

Six run at once (ROADMAP 2). Packages 1-6 launch now; 7-11 take the first free slot, in
order. Sizes are Python oracle lines at R1 (1,400 per agent-hour), rounded up for new code.
"Done" means a green differential test on the real data, not compiled code (ROADMAP 6 rule 2).
Every stream writes its own exporter as `tools/oracle/<area>.py` through H's runner, and keeps
`docs/status/<stream>.md`.

| # | id, stream | package | owner folders (write) | done when | depends on | size |
|---|---|---|---|---|---|---|
| 1 | **H1**, H, then H2 in the same slot | App skeleton (M0 acceptance 3): SDL3 window + GL 3.3 core context, Alt+Enter fullscreen, gamepad hot-plug events, native folder dialog, FFmpeg loaded from the app folder, `Tsuki.App --smoke`. Reuse S2 `Player.cs` / S3 `GlWindow.cs` / S1 `FfmpegAtrac9.Init`. The 60 Hz accumulator uses S2's nominal tick time | `src/Tsuki.Platform/`, `src/Tsuki.App/`; small seams agreed with D in `src/Tsuki.Render/Gl/` (shader compile) and with E in `src/Tsuki.Media/Ffmpeg/` (load, decoder lookup), because native packages may only be used in their owner library | `--smoke` exits 0 after the GL 3.3 context, one shader as GLSL 3.30 and ES 3.00, and finding `atrac9`, `h264` and `aac`; a GPU test in `tests/Tsuki.Tests.Render` | - | about 1 agent-h |
| 1b | **H2**, H | `GoldenSource` (A 3.3, ROADMAP step 6): `IGameData` over `samples/golden/slice` (segment offsets, text, font pages, picture tiles, PCM with loops, eboot subset), dev builds only (`#if TSUKI_DEV`) | `src/Tsuki.Data/Golden/`, `tests/Tsuki.Tests.Data/Golden/` | consumer tests read the slice through `IGameData`; values equal to the manifest | H1 done; the slice export | about 1-1.5 agent-h |
| 2 | **A1**, A, then A2 in the same slot | MZP images: port `image.py` + `mzp.py` (826 lines) from S4's prototype (all 4 formats, borders, tile flags, `Picture`); SIMD for RGB565+LO+A (494 Mpx/s scalar in S4); stratified picture-SHA-1 exporter (2,000 per build) and an all-pictures nightly in `Tsuki.Cli decode-all` | `src/Tsuki.Data/Images/`, `tools/oracle/image.py`, `tests/Tsuki.Tests.Data/Images/`, `src/Tsuki.Cli/Commands/` | M1 acceptance 4: 2,000 stratified SHA-1s equal, plus the all-pictures nightly (time it) | - | about 1 agent-h |
| 2b | **A2**, A | SELF/ELF and the catalog: `selfelf` + `uitext` (619), `catalog` + `image_catalog` + `catalog_eboot` (1,838). Catalog 14,744 + 31 empty; name list 14,744 + 1; NAM search; `_hh_vv` variants; faces; language bands; RR item 2 lookups (`img2511` -> record 2518 -> allpac1 #2157; `img1696` with args 9/10); ui_text 5 x 1,499. Also the eboot probes the build check left out | `src/Tsuki.Data/Eboot/`, `src/Tsuki.Data/Catalog/`, `tools/oracle/catalog.py`, tests in `tests/Tsuki.Tests.Data/Catalog/` | M1 acceptance 2, and the catalog / name / ui_text part of 5 | A1 | about 2 agent-h; split at SELF/ELF + catalog \| the rest if it runs long |
| 3 | **B**, B | `Text_Layout` port: `textbox.py` (1,441). The 601 golden cases first, then the 175,876 lines (4 x 43,969) by hash. Spec first: correct the 1-ulp constants in `tsuki.textbox` to the eboot float32 values (`followups.md`) | `src/Tsuki.Engine/Text/`, `tools/oracle/textbox.py`, `tests/Tsuki.Tests.Synthetic/Text/`, `tests/Tsuki.Tests.Data/Text/` | 601 / 601 equal, then every line equal | font tables from the golden eboot export (A2 later) | about 1.5-2 agent-h |
| 4 | **C**, C | Segmenting and both argument readers (`script.py` 1,220), equal on the slice segment lists of scripts 2 and 3, then on all 549 scripts by per-script hash. Then `vm_vars` (768) + errata (1,608) with the 61,000 fuzz cases. Then flow (jumps, `_IF__`, markers), reproducing the executed traces of `00_01` / `00_02` | `src/Tsuki.Engine/Vm/` (then `Flow/`), `tools/oracle/script.py`, `tests/Tsuki.Tests.Synthetic/Vm/`, `tests/Tsuki.Tests.Data/Vm/` | segments and arguments equal for 549 scripts; 61,000 / 61,000 fuzz cases; slice traces equal | `Vars` from L-1 (the readers can start without it); allscr sections (done) | about 2.5 agent-h, as 2 sequential packages |
| 5 | **D1**, D | Display-list executor: lift S3's `Shaders.cs` / `Compositor.cs` / `Effects.cs` into `Tsuki.Render` behind `DisplayList`, with the 14 blend states, the 24 blend shaders, basic / solid / transition, and the `_FADS` 0/1/2 capture path. Texture cache and upload scheduler with S4's five rules. Spec first: add the fp16 round-toward-zero export model to compositing.md and `tsuki.compose` | `src/Tsuki.Render/` (`Compositor/`, `Shaders/`, `Textures/`), `tests/Tsuki.Tests.Render/`, `tools/tsuki/compose.py` + `docs/formats/compositing.md` (D owns that oracle) | GPU tests: every mode and state equal to `compose` within 1 level on synthetic lists; the aligned frame at or above 99.9 % within 2 levels with the export model; upload with 0 frames over 16.7 ms | H1's GL seam | about 1.5-2 agent-h |
| 6 | **E1**, E | Media: S1's ATRAC9 decoder as `IAudioDecoder`; the float mixer (pan law, ramps, loops, periods of at most 1024 frames) rendered in the SDL callback; HE-VAG port (`audio_hevag` 268, 19 / 19); S2's movie thread with the nominal tick clock and the 4 ms cadence lock, tested with a synthetic clock; nightly ATRAC9 equality on all 17,925 streams (M1 acceptance 6). No d3d12va with 1 thread. Headless tests use SDL pull mode | `src/Tsuki.Media/` (`Audio/`, `Mixer/`, `Movie/`), `src/Tsuki.Engine/Movie/`, `tools/oracle/audio.py`, tests in `tests/Tsuki.Tests.Synthetic/Media/` and `tests/Tsuki.Tests.Data/Media/` | HE-VAG 19 / 19 and ATRAC9 17,925 / 17,925 equal; mixer render sample-exact against the oracle; cadence test green | H1's audio-stream seam | about 2 agent-h |
| 7 | **G**, G (queued 1st; launch at once if a 7th agent is allowed) | `uicalls.py` as a tested module, from `scratch/misc_boot_title_choice_flow/calls.py` (113) and `misc_boot_title_choice_flow.py` (1,763): reproduce the title and choice draw lists, then run it on the system-menu entry 0x6758E | `tools/eboot/uicalls.py`, `tools/tests/test_uicalls.py`, `docs/status/G.md` | the title and choice draw lists equal the existing oracle lists | - | about 1-1.5 agent-h; Python only, no GPU |
| 8 | **H3**, H (queued 2nd) | Oracle gaps for M2 (`followups.md`): catalog records for the 23 `_STCG` names of `00_01`; the retail path (RR 7.6, jump handling in the slice tracer); traces for `_SEPL(7,1,MOON_SE03028,...)`, `_WTTM(4000,1)` and reveal speed; sampled motion values; the page-1 oracle render (`tools/oracle/render.py`); manifest provenance; tmp_path cleanup; S4's 154 picture SHA-1s as golden data | `tools/oracle/`, `tools/tests/test_oracle*.py` | `oracle.run slice --verify` 0 problems with the new parts; `render.py` reproduces the page-1 frame from `compose` | D1's export model for `render.py` | about 2 agent-h, as 2 packages |
| 9 | **E2**, E (queued 3rd) | SoundModel: `misc_audio_runtime_semantics.py` (1,295), with `Update`, ducking and the voice gate producing `AudioCommand`s | `src/Tsuki.Engine/Sound/`, tests in `tests/Tsuki.Tests.Synthetic/Sound/` | trace equal to H3's SoundModel export, including the 8,000 ms fade-in to 30 % (M2 check 7) | L-1 `SoundChannelRecord`; H3 trace | about 1.5 agent-h |
| 10 | **A3**, A (queued 4th) | Text, scripts, fonts and the rest of the eboot: `text` + `text_refs` (1,334), `font` + `font_eboot` (1,322), `script_tables` (377), allscr label tables, scene ids 549, movie table 134, motions 24,192 / 71,905 keyframes, font lookups 17,423, `GameFolderSource`, census T0 with its allow-list (5 script bugs, 3 missing sprite bodies) | `src/Tsuki.Data/Text/`, `Scripts/`, `Fonts/`, `src/Tsuki.Data/GameFolderSource.cs`, tests in `tests/Tsuki.Tests.Data/` | M1 acceptance 5 and 7; `GameFolderSource` agrees with `GoldenSource` on the slice | A2 (SELF/ELF) | about 2.5 agent-h, as 2 packages |
| 11 | **D2**, D (queued 5th) | Layer model and motions: `misc_layer_state_and_motions.py` (2,330) + transitions (293). scene_state, records, tweens, motions and projection produce `DisplayList`s | `src/Tsuki.Engine/Stage/`, tests in `tests/Tsuki.Tests.Synthetic/Stage/` | traces and display lists equal to the oracle on the slice | L-1 `SceneState`; **wait until the concurrent layer-motions workstream lands its uncommitted changes** | about 2-2.5 agent-h |

Where the spikes' findings went:

| spike | finding | package |
|---|---|---|
| S1 | decoder shape (no demuxer, 12-byte extradata, one packet per superframe) | E1 |
| S1 | sample-exact test boundary (pull mode or postmix, never the SDL disk driver) | E1, L-2 |
| S1 | LGPL v3 wording | L-2 |
| S1 | seam crossfade decision | L-3 |
| S2 | nominal tick clock plus a 4 ms cadence lock | H1 (loop), E1 (movie), L-2 |
| S2 | software decode by default, no d3d12va with 1 thread | E1 |
| S3 | the shaders and compositor themselves | D1 |
| S3 | fp16 RTZ export model | D1 (oracle side), H3 (`render.py`) |
| S4 | MZX0 / MZP prototype and SIMD target | A1 |
| S4 | the five upload rules | D1 |
| S4 | prefetch bounded by decoded bytes or frames | L-2, then the prefetcher in M4 |
| S4 | the 154 SHA-1s as golden data | H3 |

Launch order if slots are short: H1, A1, C, D1, E1, B first, then G, H2, H3, E2, A2, A3 and
D2 as slots free up. H2 and A2 follow H1 and A1 in the same slot. This keeps the M2 chain
(H1 -> H2 -> M2a) and the M1 chain (A1 -> A2 -> A3) moving at the same time.
