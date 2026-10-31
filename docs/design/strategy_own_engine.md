# Strategy: our own lightweight C# engine that runs the PS4 data directly

Route under evaluation: a small, single-game visual-novel player written in C# on .NET. It
reads the user's extracted `CUSA47999` folder **as it is** through a C# port of `tools/tsuki`,
and it interprets the 549 HuneX scripts natively, the way the PS4 eboot does. Existing libraries
supply the window, input, audio device, OpenGL and media decoding. There is no conversion step,
no transpiler, no emulator and no PS4 code.

Written 2026-10-25 against the research state of `docs/` (FORMATS.md, RUNTIME_REQUIREMENTS.md,
`docs/formats/*.md` including the delivered `layers.md`, `compositing.md`, `sound_runtime.md`,
`adv_state.md`, `system_flow.md`, `gallery.md`, `vm_variables.md`, and
`research/engine_lineage.md`). Library facts were checked on their project pages on the same day
(sources in section 18). References like `layers 4.1` mean `docs/formats/layers.md` section 4.1.

---

## 0. Verdict in one paragraph

This route fits our situation unusually well, because the hard part of a VN port, knowing exactly
what the engine does, is already done. Every format is decoded, and the behaviour of the VM,
variables, layers, motions, shaders, sound runtime, history/save, auto/skip, config, boot, title
and choice screen exists as **Python models that were checked against the game's own code under
emulation**. An own engine can port those models one to one and test every C# module against
its Python twin. No other route reuses that work. Converting to Ren'Py or KiriKiri throws away the
bit-exact text layout and shader math and adds a transpiler plus a conversion of about 35,600
pictures, 17,944 audio streams and 4.75 GB of video. The price is that we **write and own the
player-facing parts ourselves**: compositor, mixer, movie sync and, above all, the menus (save/load,
backlog, system menu, gallery, flowchart). Their layouts are code constants in the eboot and only
partly decoded. Realistic target with 4-6 parallel agents and a human reviewing daily: one route
playable end to end in about 5-6 weeks, the complete game with save/load, backlog, auto/skip and
config in about 8 weeks, gallery, flowchart and polish by week 10-12. The risk-adjusted upper
bound is about 16 weeks, driven by menu reverse engineering and visual sign-off.

---

## 1. What we copy from the ports that worked

| precedent | what it did | what we take | what we avoid |
|---|---|---|---|
| david419kr/mahoyo-steam-android-port (native C++ reimplementation of HuneX's Mahoyo Steam runtime) | Reads only the user's own files and never runs the original binary. Its `runtime/` has one file per feature (`effects`, `screen_wave`, `global_shake`, `layer_shake`, `focus_line`, `particle_overlay`, `window_fade`, `movie`, `text`, `choices`, `history`, `save_store`, ...) and a `vm/` folder. Third-party notices list iced-x86, so it reads tables out of the game executable as data | The VM/host split (the VM decodes statements, the host executes each command and answers Continue / Yield / Halt), serialisable state, one module per effect, reading executable tables as data | Android-specific UI |
| tsukiweb (fan web port of the 2000 Tsukihime) | Small custom runtime for one game; no assets in the repo | Single-game focus; ship code only | Its offline conversion step, which we do not need (section 4) |
| rlvm, impacto, ONScripter-RU/Umineko | The original behaviour is the spec. Multi-game engines (impacto, 3,190 commits) still have no complete game; the Umineko fork took about 8 years | **One game only**; the long tail is UI, so plan for it from day one | Generic engine abstractions, per-game profiles |
| Zeus browser port (Carter Appleton), Snowboard Kids 2 decomp (Chris Lewis) | AI-built ports. The first Zeus attempt, gated only by the compiler, was unplayable. The second built the harness first: deterministic probes, byte-exact save round trips, pixel checks, file ownership per agent, status files, hooks | **Harness before features**; differential tests against our Python oracles; one owner per folder; hooks that forbid skipped tests and edits to generated or oracle files | Accepting "it compiles" or prose descriptions of screenshots as proof |
| FSN REMASTERED, Mahoyo Steam launch problems | Sprite/voice mismatches unless the system locale was Japanese; video codec failures | UTF-16 paths everywhere, invariant culture, a pinned decoder build, tests on an English-locale Windows and a non-ASCII install path | System code page APIs, OS codec lottery |

---

## 2. Technology stack

All pieces are mainstream, have NuGet packages, need **no C/C++ compiler** and can be driven from
the command line by agents.

| need | choice | version checked 2026-10-25 | licence | why this one |
|---|---|---|---|---|
| language/runtime | C# on **.NET 10 LTS** (`net10.0`), self-contained publish | .NET 9 (installed) reaches end of support on **2026-11-10**; .NET 10 is supported to 2028-11 | MIT | Agents know C# very well. Positional reads on 24 GB files (`RandomAccess.Read`), SIMD (`System.Numerics.Vector`), threads, SHA-256, JSON, xUnit. Installing the .NET 10 SDK is one `winget` command; .NET 9 works for the first weeks if needed |
| window, input, gamepad, audio device, dialogs | **SDL3** via `ppy.SDL3-CS` | 2026.1002.1 (2 Oct 2026); maintained by the osu! team for osu!lazer | MIT bindings, zlib SDL | Native DLLs ship in the package. Gamepad database with PlayStation labels (`SDL_GetGamepadButtonLabel`), hot-plug, rumble; float32 audio streams with resampling; `SDL_ShowOpenFolderDialog` to pick `CUSA47999`; message boxes; high-DPI and fullscreen |
| GPU | **OpenGL 3.3 core** on the SDL3 window through `Silk.NET.OpenGL` bindings | 2.23.0 (23 Jan 2026). Only the generated bindings are used, not Silk.NET windowing (Silk.NET 2.x is maintenance-only while 3.0 is being written) | MIT | GLSL compiled by the driver at run time, so no shader toolchain. Render-to-texture, `glBlitFramebuffer` for backdrop copies, scissor clips. Fallback: the same GLSL subset on ANGLE (GLES 3.0 over D3D11) if a Windows GL driver misbehaves |
| H.264/AAC movies, ATRAC9 audio decode | **FFmpeg 9.0 shared LGPL DLLs** via `FFmpeg.AutoGen` | FFmpeg.AutoGen 9.0.1.1 (16 Aug 2026) targets FFmpeg 9.0.1; LGPL shared builds come from BtbN/FFmpeg-Builds (`lgpl-shared` variant) | MIT bindings, LGPL FFmpeg | The research already decoded all 17,925 ATRAC9 streams and probed all 134 movies with FFmpeg (RUNTIME_REQUIREMENTS 5). The `h264`, `aac`, `atrac9` decoders and the `mov`/`wav` demuxers are LGPL core components. The local `ffmpeg.exe` (gyan.dev 9.0.2, static GPL) is fine for tests but has no DLLs |
| HE-VAG (19 system sounds) | own C# port of `tsuki/audio_hevag.py` (268 lines) | - | ours | FFmpeg's `adpcm_psx` decodes these files wrongly (audio 6) |
| developer overlay (optional) | Dear ImGui via a .NET binding | - | MIT | Layer and variable inspector, script position and timeline scrubber for humans and agents. Not part of the player UI |
| software GL for GPU-less test runs (optional) | Mesa llvmpipe for Windows (pal1000/mesa-dist-win 26.2.4) | certified for OpenGL 4.5 | MIT | Lets render tests run where no GPU is available |
| optional managed ATRAC9 decoder | Thealexbarney/LibAtrac9 (C and C#, MIT, 2018) | no NuGet, 22 commits | MIT | Only a fallback if FFmpeg must be dropped from the audio path; it would have to match FFmpeg on all streams first |

Rejected for this route and why:

- **Raylib-cs** (v8.1.0, raylib 6.0): the smallest API, but it owns the GL state and batching that
  the 35 blend shaders and backdrop copies need to control, and its file and audio helpers use
  C runtime paths. Raw GL on SDL3 costs about 400 more lines and gives exact control.
- **MonoGame/FNA**: their effect toolchains (mgfxc/fxc) and content pipeline add build steps,
  their PCM streaming is 16-bit, and DesktopGL has no video.
- **Direct3D 11 via Vortice**: the most robust driver path on Windows, and WARP would give
  deterministic headless renders. It is a good plan B (section 17, risk R4), but it needs more
  boilerplate, and agents are more fluent in GL. The display-list design of section 6 keeps the
  backend swappable.
- **Windows Media Foundation for movies**: built in and hardware decoded, but COM interop and
  less deterministic, harder-to-test decoding. It stays a fallback behind an `IMovieDecoder`
  interface, for example if distributing FFmpeg's H.264 decoder becomes a concern.

---

## 3. Architecture

### 3.1 Projects (one owner each)

```
src/
  Tsuki.Data      pure C#, no native code. Port of tools/tsuki readers: HED8/HED4/NAM/MRG/mrgd00,
                  MZX0 (+XOR scripts, LV), image MZP (PAL8/PAL4/HEP8/RGB565+LO+A, borders, flags),
                  catalog + picture names + NAM search, script_text, allscr + labels, font pages,
                  eboot SELF/ELF reader and every eboot-only table (FORMATS 5.4), ui_text,
                  build check (eboot SHA-256 + structural probes, RUNTIME_REQUIREMENTS 3.3)
  Tsuki.Engine    pure C#, deterministic, headless. Script VM, 98 command handlers, variables,
                  scene_state + render records + tweens + motions (layers.md), text layout and
                  reveal (textbox.md), sound model (sound_runtime.md), ADV state: history ring, scene
                  stack, slot staging, saves, rewind, backlog, auto/skip, read tracking, config
                  (adv_state.md), system flow: boot/title/choice state machines (system_flow.md),
                  gallery/flowchart/unlock model (gallery.md). Output per 60 Hz step: a display list
                  and a list of audio/movie commands
  Tsuki.Media     FFmpeg (FFmpeg.AutoGen): ATRAC9 RIFF decode via in-memory AVIO, MP4 demux and
                  H.264/AAC decode on a thread; HE-VAG decoder; the float mixer (26 streams + system
                  sounds + movie audio, ramps, pan law, loops)
  Tsuki.Render    Silk.NET.OpenGL: texture cache with tile textures and the engine's slot numbers,
                  display-list executor, 35 pixel shaders + 14 blend states (compositing.md), 16
                  capture surfaces, rule transitions, movie YCbCr pass, glyph quads
  Tsuki.Platform  SDL3: window + GL context, events, keyboard/mouse/gamepad -> the PS4 button word
                  and pointer (engine 8.5), audio device stream, folder dialog, message boxes,
                  user folders
  Tsuki.Ui        the screens as data + small state machines: boot/caution/logo, title, choice, HUD,
                  config, system menu, save/load, backlog, gallery (CG, music, movie, ending list),
                  flowchart. Draw lists live in JSON produced from the eboot by our tools (section 9)
  Tsuki.App       Program.cs: first-run setup, main loop, error screen
  Tsuki.Cli       verify, dump, trace, explore, render-checkpoint, decode-all (test and dev tool)
tests/
  Tsuki.Tests.Synthetic   no game data needed: CI on any machine
  Tsuki.Tests.Data        needs TSUKI_GAME_DIR; compares with golden files the Python oracle wrote
  Tsuki.Tests.Render      needs a GL context (GPU, or Mesa llvmpipe)
tools/oracle/             Python scripts (using tools/tsuki) that write golden JSON/PNG into
                          samples/golden/ (local only, never committed)
```

`Tsuki.Data` and `Tsuki.Engine` contain **no native calls**, so most of the game (everything up to
"what would be drawn and played") runs and is tested without a window, GPU or sound card.

### 3.2 Threads and the frame loop

```
main thread
  SDL events -> input state (PS4 button word, edges, auto-repeat, pointer 640x448)
  fixed 60 Hz logic (accumulator; RUNTIME_REQUIREMENTS 4.18):
     Engine.Step(): current process (boot / title / menu / ADV)
        ADV: ScriptStep = up to 1000 segments, stop at the first yield (engine 5.2)
             fast-forward: repeated ScriptStep in one step (adv_state 6)
        timers: text reveal, window fades, tweens, motions, shakes, waits, FADS progress, movie clock
        sound model Update(1) + ducking step (sound_runtime 6, 9) -> mixer command queue
     -> DisplayList (immutable, double-buffered)
  render the newest DisplayList (GL), present with vsync at the monitor's own rate
worker pool    picture decode (prefetch queue, section 4.3), voice/SE decode, save-file writes
movie thread   FFmpeg demux/decode -> frame queue (Y, U, V planes) + AAC -> mixer
audio thread   SDL3 audio stream callback: Mixer.Render(<= 1024 frames, float32 stereo, 48 kHz)
```

The engine never reads the wall clock. Time is a frame counter, so a test can run the whole game
in "virtual time" as fast as the CPU allows (section 12).

### 3.3 The state is the PS4's state

The PS4 keeps its ADV state in a few documented byte blocks: `scene_state` (0x1A08 bytes,
adv_state 2.6, layers 3), the history record (0x3A10, adv_state 2.1), the sound channel records
(0x34 bytes each, sound_runtime 2.2), config (0x140, adv_state 9.2), the system block (0x18AC) and
the F / Z / G arrays (vm_variables 2). Their field maps were verified byte for byte under
emulation. `Tsuki.Engine` therefore stores these blocks **as byte arrays with typed accessors**,
in the PS4 layout, plus a few C# objects for things the PS4 keeps elsewhere (render records,
texture handles, decoded audio).

Consequences:

- Saving, rewind, the backlog jump, "previous scene" and "next scene" are the same mechanism: copy
  records the way `History_Push`, `History_Restore`, `Slot_Store` and `Slot_Load` do
  (adv_state 2-7), with no hand-written serializer.
- Every Python model (`tsuki.vm_vars`, `misc_layer_state_and_motions`,
  `misc_audio_runtime_semantics.SoundModel`, `misc_adv_system_state`) can check our state bytes
  directly.
- Our saves can be checked with the existing `check_save_image`, and importing a real PS4
  `savedata000` becomes close to free (P3).

---

## 4. Reading the user's data: no conversion

### 4.1 First run

1. SDL folder dialog: "Select your TSUKIHIME folder (CUSA47999)". The game folder is opened
   read-only, ever.
2. Build check (RUNTIME_REQUIREMENTS 3.3): eboot.bin is 12,767,802 bytes with SHA-256
   `c2843415...b918` (about 30 ms), then the structural probes and the archive counts of FORMATS
   2. On a mismatch, show a clear message and do not start. We never guess addresses.
3. Extract the eboot-only tables (catalog 14,744 records, picture names, scene ids, movie table
   and remaps, 24,192 motions with 71,905 keyframes, font lookup and rectangle tables, layout and
   window tables, UI registration tables, ui_text 5 x 1,499, config defaults, history F lists,
   gallery/flowchart/music tables). This takes well under a second. An optional cache file goes
   in `%LOCALAPPDATA%\TsukihimePort\cache` (derived data, local only, never shipped).
4. Store the game path and config in `%APPDATA%\TsukihimePort\settings.json`, and saves in
   `%USERPROFILE%\Saved Games\TsukihimePort\`.

Every later start: about 1-2 s to the first caution screen.

### 4.2 What is read when

| data | access at run time | cost |
|---|---|---|
| HED/NAM indexes | loaded whole at start | a few MB |
| `allscr.mrg` | the current script section (MZX0 -> XOR 0xFF), segment offsets pre-computed at load | about 1 ms per script; 9.5 MB in total |
| `script_text.mrg` | offset table + blob of the current language (plus Japanese for the backlog / dual-language option) | about 2.5 MB per language |
| `ft_<lang>.mrg` | 65 pages of the current language, uploaded as 1024 x 1024 two-channel (coverage, alpha) textures | about 130 MB VRAM; at start and on a language change |
| pictures (allpac0/1/1_2/3/4) | positional read of one entry -> MZP -> MZX0 tiles -> RGBA -> one GL texture per tile, on worker threads | C# MZX0 should reach the order of the 868 MB/s that numba reaches in Python. A typical 2,070 x 1,230 backdrop (10 MB RGBA) takes about 10-20 ms on one core; the largest (11,220 x 7,650 shown, 343 MB RGBA) decodes tiles in parallel in a few hundred ms and must be prefetched |
| `STEC`/`SNX2`/`SNX3` colour effects | applied on the CPU when the texture is built, as on PS4 (compositing 9; the recolour is bit-exact in `misc_compositing_transitions_shaders.py`) | the cache key includes the effect parameters |
| voice | `_VPLY` -> HED4 entry -> LV + MZX0 -> RIFF -> FFmpeg atrac9 -> float buffer | at most 512 KB in, under 10 ms |
| SE | whole entry decoded at first use, LRU-cached | small |
| BGM | decoded to float in the background when `_MPLY` starts; two slots resident | under about 100 MB per track; switch to streaming decode with the loop recipe of audio 2.4 if memory matters |
| movies | streamed from `image/movie/*.mp4` through FFmpeg | hardware-independent software decode; 1080p30 H.264 uses a fraction of one core |

**Disk and conversion cost: none.** No WebP, Ogg or WebM copies, no 142 GB of raw pixels. Extra
disk is the app (about 200-250 MB including FFmpeg and the .NET runtime), saves (a few MB) and the
optional eboot table cache.

### 4.3 Prefetch and memory

- Port the PS4's look-ahead prefetcher (`Script_Prefetch`, 43 entries: 41 names + `J***` + `ZY**`;
  its V case only takes `VPLY` / `VPL2`; engine 5.4, FORMATS 6.5) as a scan of the next N
  statements that queues picture and voice decodes. It never changes script state.
- GPU texture cache: LRU by bytes, default cap 3 GB (the RX 9070 XT has 16 GB). Tiles are separate
  textures (images 9, RUNTIME_REQUIREMENTS 4.3), drawn with the inner rectangle sampled, so tile
  borders act as a filter apron and seams stay invisible.
- No GC pressure in the frame loop: pooled arrays (`ArrayPool`), structs for display-list
  entries, `GCSettings.LatencyMode = SustainedLowLatency` while in ADV.

---

## 5. Interpreting the scripts natively

### 5.1 VM

The scripts are not translated. The VM does what the eboot does (script 3, engine 5.2):

- Section `k + 3` of allscr.mrg, MZX0 then XOR 0xFF. Segments end at `;` within 0x300 bytes, the
  name is the 4 bytes after the first `_`, the first letter must be `A..Z`, and the letters
  B D H K L N O Q U Y are skipped. Every `J*` is JUMP, every `Z*` a marker. At most **1000
  segments per call**.
- Both argument readers are ported byte for byte: the inline reader (sum of decimal terms, other
  bytes ignored, empty = 0, missing = -1, 5-byte backquote tags) and, for the 52 `OBJ_COMMANDS`,
  the object parser with its right-associative evaluator (`242.25` = 242). Keyword tags
  `` `NNN: `` are filed by number (script 3.4).
- Variables per `vm_variables.md`: `F[1200]`, `Z[1400]`, `G[2000]`; first-byte operand dispatch per
  position; `C<n>` = `F[F823+n]` as a write target and the literal n as a value; `_D<n>`; three-digit
  selectors of `GOTS` / `SEL*`. Unlike the PS4, out-of-range indices are rejected (the retail
  scripts stay in range).
- Markers `_Z<k><bbb><ss>`: lower-case hex, read tracking `readmax[block]` updated when the next
  marker is reached (script 3.7).
- Host interface, as in the Mahoyo port: `StepResult Dispatch(ref Statement s)` returns
  `Continue`, `Yield` or `Halt`. Resuming continues after the statement, so no command runs twice.
  The yield set is the PS4's (script 4: always CLO2-5, END_, FADS, MSAD, SELA, SELD, SELS, STCF,
  TOMW, WTSV, WTTM; sometimes ATSV, GOTO, IF__, J*, MFAD, PGST, RDED, RTM_, VCWT, VPLY, Z*, ...).
- Language binding: messages are `$NNNNNN` references expanded at display time against the
  current language table, so switching ja / en / zc / zt changes no code address (the same idea as
  the Mahoyo port's `rebindModuleStrings`, but free here).

### 5.2 Command families

Counts are statements in the 549 retail scripts (script 4). "Oracle" names the Python model that
each C# handler must match on the full statement corpus.

| family | commands | what the C# side does | spec | oracle |
|---|---|---|---|---|
| flow, scenes | `JUMP` (1,741), `GOTO`, `IF__` (1,194), `GOTS`, `IFNX` (always jumps on PS4: we take the PS4 branch), `IFP4` (no-op), `IFSC`, `IFUR`, `END_`, `RTM_`, `RTMN`, `RTM2`, `PGST` (17,202), `XSTP`/`XRSM`; label markers `ZZ`, `ZY` | scene id -> script via the 549-entry table, labels from section 1; script-end cleanup 0x7F1A0 (20 SE channels stop; BGM unless keep-BGM) | script 3.7, 4.1, 7; vm_variables 4 | `tsuki.vm_vars`, `tsuki.script` |
| variables | `WKST` (487,857), `WKAD` (45,679), `WKOR` | operand model | vm_variables 3-5 | `tsuki.vm_vars` (EMU-9108) |
| bookkeeping, unlocks | `RDST`, `RTST`, `RDAS`, `RDED`, `STCG`, `ATSV` (398), `WTSV`, `TRPY` | persistent bit sets (scene reached / visited / completed, picture seen), auto-save point and FIFO slots 100..138; trophies only logged (optional achievements) | script 4.1; adv_state 2.5, 7.2; gallery 2 | `misc_adv_system_state`, `misc_gallery_extras_flowchart` |
| messages | `ZM` (42,590), `ZD`, `MSAD`, `WNTY`, `CLO2`..`CLO5`, `MSQK`, `FCSP`, `FCLN` | `$`-expansion (0x2FC cap), markup, `Text_Layout` in float32 in the engine's operation order, page stacking, reveal at `1000/(60*speed)`, window pictures, fades | text 4-6; textbox 2-8; fonts | `tsuki.textbox` (175,876 lines + 6,743 messages, 0 mismatches) |
| choices | `SELR` (824), `SELD` (170), `SELA`, `SELS` | slot building `"$0439NN" + msg`, locked choices, result store, choice screen phases and draw list | vm_variables 4.5-4.6; system_flow 5 | `misc_boot_title_choice_flow`, `tsuki.vm_vars` |
| pictures and layers | `STCH` (54,494), `SCH2`, `STCC` (63,261), `STCP`, `STZ4`, `STRT`, `STRR`, `STRA`, `STPV`, `SOXY`, `STGS`, `STBR` (14,940), `STEC` (12,900), `STCF`, `STMA` (24,333), `SGMA`, `SPMA`, `SNX2`, `SNX3`, `STNS`, `STTR`, `SACL`, `SBAS`, `STCL`, `SPFC`, `SPRD`, `SPRC`; no-ops `SNX4`, `STQK` | writes to scene_state; commit to records B at `_FADS`; per-frame update B -> A, tweens, motions (field table, interpolation and easing all [V-emu]); projection through parent layers (2x3 affine); picture resolution with `_hh_vv` variants, faces, language bands | layers 3-8; script 4.3, 4.3a, 6.4; images 7-8 | `misc_layer_state_and_motions` |
| transitions and screen effects | `FADS` (34,040), `FADE`, `FANX` (no-op), `SQK2`/`SQS2`, `SHK2`/`SHS2`, `SSW2`/`SSS2`, `SHOK`, `SHAF` | capture surfaces 0 and 15, progress `+= step` with `step = -1/(ms/16.666)`, cross-fade / cut / rule wipe with `RULE_nn`, rules 49..52 play UI SE 0x19; quake / shake / swing storage | compositing 7-8; layers 4.4 | `tsuki.compose` (`fads_step`, `fads_progress`) |
| sound | `VPLY` (16,689), `VPL2`, `VCWT`, `SEPL` (14,543), `SEFD`, `SEVL`, `SESP`, `MPLY` (1,171), `MVOL`, `MFAD` (`SEFI` and the engine-only `MSTP`/`SEPN`/`SEP3`/`SEAC` share handlers but no script uses them) | channel records, tag scan, deferred-SE queue (`` `068 ``), ramps, fade-to-stop, per-character voice gate, voice started by the next `_ZM`, stop on advance, BGM ducking, two alternating BGM slots, same-track `_MPLY` ignored | sound_runtime 2-11 | `SoundModel` (EMU-corpus: 0 differences on every audio statement) |
| movies | `TOMP` (48), `TOMW` (48) | language remap (prologue 4 -> 4/46/47/106, phase titles +40/+67/+99, openings +97/+99), movie table -> mp4; id 7 skipped in fast-forward | aum 9; script 6.5 | `tsuki.catalog` |
| waits | `WTTM` (14,828) | `frames = (ms*3)/50`, skippable flag | RUNTIME_REQUIREMENTS 4.18 | - |
| no-ops | `SNX4`, `FANX`, `IFP4`, `DMMY`, `STQK`, `NOP_` | skipped, as on PS4 | script 3.6 | dispatch table |

Every one of the 98 names in use (94 commands + the marker kinds `ZM`, `ZD`, `ZZ`, `ZY`; checked
against `samples/script/command_stats.json`) has a row. The dispatch table is generated from
`tsuki.script_tables` and checked in a test: a name that is not in it fails the build.

---

## 6. Rendering

### 6.1 Display lists, not "draw calls from game logic"

The PS4 draws through display lists of fixed-size records (effect word, texture slot, quad,
colour, clip; compositing 6). `Tsuki.Engine` produces the same thing: an immutable list of
`DrawCmd { kind, texture (slot or tile handle), 4 vertices + UVs, colour, effect, clip, target }`
in PS4 order (scene layers sorted by z, then HUD/message window, choice screen, UI). `Tsuki.Render`
only executes lists.

This split is the main testing lever: **what is drawn can be checked without a GPU** against the
Python models, which emulate the same lists, and the GPU executor can be checked separately
against `tsuki.compose` on synthetic lists.

### 6.2 Compositor

- Internal frame 1920 x 1080 (every layout constant is for 1080p), scaled to the window with 16:9
  letterboxing. Logic at 60 Hz; the newest frame is shown at the monitor rate.
- **Straight alpha** pictures, normal draw `SRC_ALPHA, ONE_MINUS_SRC_ALPHA` (compositing 4.2). The
  14 fixed-function blend states come from the table at VA 0x2D8030 and map to `glBlendFuncSeparate`
  / `glBlendEquationSeparate`. The render-target alpha is used as a mask channel, as on PS4.
  Colour is processed in the encoded (sRGB) values, as the PS4 shaders do: no `GL_FRAMEBUFFER_SRGB`.
- The **35 pixel shaders** (basic 2D / 2DTex / VtxCol / VtxColTex, YCbCr, clear, capture, transition
  2D / 2DTex, 24 blend shaders, sepia variants, which are unreachable in the shipped game) are
  rewritten in GLSL from the formulas in `compose.py`. Those formulas were derived from the GCN
  code and checked by running it (35 / 35, max error 9.7e-4).
- Blend shaders read a **backdrop copy**: before a quad whose effect reads `dst_texture`, blit the
  affected rectangle of the current target into a backdrop texture (`glBlitFramebuffer`; about
  0.1 ms for a full 1080p frame on this GPU). Quads do not overlap themselves (compositing 14.1), so
  this matches either PS4 hypothesis.
- 16 capture surfaces of 1920 x 1080 (transitions, thumbnails, backlog), rule masks in texture
  slot 0x4DD, screen-effect quads (51 presets, compositing 5.3), the masked / offscreen path for
  crop and mask layers (layers 9; partly [H]).
- Movies: Y, U and V planes as three R8 textures, BT.709 limited-range shader (55 untagged files
  are treated the same), frame timing from timestamps against the audio clock (or the frame clock
  when the track is silent).

### 6.3 Text

The text is drawn from the game's own glyph pages, not from a TTF. The layout is already
bit-exact: `Text_Layout` with its kinsoku classes, the English word table and ruby placement
(scale 0.1 x 0.11, dy -7.04; ruby is off in English), as `tsuki.textbox` does it. Per glyph, the
same quad path as the engine: optional drop shadow (+1, +1 units, alpha 0.66), outline (same
rectangle 512 px lower on the page), fill. Furigana and every Japanese line break therefore come
out exactly as on PS4.

C# `float` is IEEE float32, and RyuJIT does not contract `a*b+c` into FMA unless we call
`MathF.FusedMultiplyAdd`, so the engine's operation order can be kept. A test runs the 601
golden layout cases and all 175,876 lines on every build, because the wrap decision depends on
this.

---

## 7. Audio

- One float32 stereo mix at 48 kHz (the PS4 rate), with a period of at most 1024 frames through an
  SDL3 audio stream. SDL resamples if the device runs at another rate.
- Streams as on PS4: voice 2..5, SE channels 0..19 on streams 6..25, two alternating BGM slots,
  HE-VAG system sounds (`SystemSE_Play` table, sound_runtime 10.1), movie AAC. The gains reaching
  the mixer are final values from the model (`trunc(config*vol*0.01)` for BGM, finding 6.6 c).
- Pan law `gL = min(2(1-p),1)`, `gR = min(2p,1)`. Linear ramps per the model, no clipping until
  the device (decoded SE peaks reach 1.878 full scale).
- Loops are sample-accurate: playable = `decoded[256 : 256 + fact[0]]`, loop
  `[smpl.start-256, smpl.end-256]` with the end inclusive, applied to the decoded buffer.
- `_VPLY` -> pending voice -> started by the next `_ZM` through the character gate -> stopped on
  advance when config +0x4E is set. Backlog replay plays the same entry again.

---

## 8. ADV features: save/load, history, backlog, auto/skip, config, input

All of this is ported from `adv_state.md` and `tsuki.misc_adv_system_state`, which ran the game's
own `History_Push`, `History_Restore`, `Slot_Store`, `QuickSave_Store`, `SaveImage_Commit` and
`Slot_Load` under emulation.

| feature | behaviour to reproduce | reference |
|---|---|---|
| history ring | 800 records of 0x3A10 bytes; push kinds 0..13 before the command runs (0 messages, 1 layer batch, 2/3 audio, 6/7 `_ATSV`, 9 commit, 12/13 choices) | adv_state 2, 4, 11 |
| rewind (hold) | undo records newest first down to the previous page start, re-display the target line and continue after it; no sounds replayed | adv_state 5.1, 3.2 |
| next / previous scene | run without waits until `_ATSV`; restore the scene-stack top (or the one below within the first two messages) | adv_state 5.2, 5.3 |
| backlog | open, scroll, voice replay by stored voice entry, jump = restore the entry's record and run its command again | adv_state 5.4, 5.5 |
| save | slot = the page-start record (state A) + the scene-start record (state B); slots 0..99 manual, 100..138 auto FIFO, 139 quick; manual and auto saves set F1118, the quick save does not; local timestamps | adv_state 7.1-7.4 |
| load | `History_Restore` of state A, then **the page's first message statement runs again** from its command offset (not scan mode) | adv_state 7.3 |
| Continue | loads the newest slot (system block "latest slot") | adv_state 7.2; system_flow 4 |
| read tracking | `readmax[4020]`, blocks > 0xFB3 never recorded | adv_state 6; script 3.7 |
| auto mode | countdown `trunc(ms/1000*60)` with config +0x9E on the last message of the page, else +0x2E, after the reveal and voice wait | adv_state 6 |
| skip / fast-forward | latched skip and hold-to-fast-forward, step 64; "read text only" stops at unread lines; stop at choices | adv_state 6 |
| config | the fields that have menu items (adv_state 9.2), defaults from data 0x66C578; the others stay constants | adv_state 9 |
| input | the 27 actions of preset 0, plus our keyboard/mouse bindings: Enter/Space/left click/wheel down = decide, Esc/right click = cancel or menu, Ctrl hold = fast-forward, wheel up / Page Up = backlog, F5/F9 = quick save / load; the gamepad follows the PS4 preset with the enter-button setting | adv_state 10; engine 8.5 |
| language | 0 ja, 1 en, 2 zc, 3 zt; a change reloads text, fonts and UI and re-lays out the current page | RUNTIME_REQUIREMENTS 4.19 |

Our save file is our own versioned container: a header, the system block and the 140 slot records
in PS4 layout, and our extras (PNG thumbnail per slot, app version). It is written atomically
(temporary file + rename). The PS4 composes save previews from allpac2 thumbnails. We start with
a downscaled screenshot (always correct, slightly different look) and can switch to the PS4 method
later.

---

## 9. Menus and UI screens: the long tail

The UI is where impacto and Umineko lost years, so it is planned as its own work stream from the
start. Each screen = a small C# state machine plus a **draw list in JSON** (texture slot, archive,
child, sub-rectangle, canvas position, scale, anchor, alpha, effect), drawn with the game's own UI
sheets from allpac3 and parts.mrg and with `ui_text` strings. The draw lists are extracted from the
eboot by the call decoder that system_flow.md already used (`scratch/misc_boot_title_choice_flow/
calls.py`, which recovers the constant arguments of SetCanvas / SetScale / SetColor / draw calls,
tail calls included). That extraction, not hand placement, is the default.

| screen | entry VA | state today | plan | effort (agent-days) |
|---|---|---|---|---|
| boot: system save, caution, logo, autosave notice | 0xE8280 | decoded (system_flow 3, frame counts) | port | 1-2 |
| title, background choice, menu | 0xAFD8C | decoded (system_flow 4); particles and shimmer not modelled | port; approximate particles from the documented ranges | 2-3 |
| choice screen | 0xA6E10 | decoded (system_flow 5) | port | 1-2 |
| HUD / message window / bubbles | 0x53CF0 | decoded (textbox 7) | port | 1-2 |
| config | 0x3EE10 | decoded (aum 7.4, adv_state 9) | port | 3-4 |
| `_SELA` / `_SELS` prompts | widgets 0xD5 / 0xD6 | open (system_flow 8.4) | call decoder | 1-2 |
| system menu, mode panel | 0x6758E / 0x6CA83 / 0x6E683 | logic partly known, layout not extracted | call decoder | 3-5 |
| save / load | 0x6F423 | logic decoded (adv_state 7), layout not | call decoder | 3-5 |
| backlog | 0x3BFB0 | logic decoded (adv_state 5.4-5.5), layout not | call decoder | 3-4 |
| gallery: CG, music player, movie, ending list | 0x12680 and the classes of gallery 1 | data, unlock rules and replay decoded (gallery 2-8), layouts not | call decoder | 6-10 |
| flowchart | 0x20960 | columns, nodes, edges and filter decoded; line drawing not (gallery 5, 13.1) | call decoder + own line drawing | 4-6 |
| first-run setup, errors | - | ours | SDL dialogs + one simple screen | 1 |

If the extraction stalls on a screen, the fallback is a hand-made layout from the same sheets:
functional and close, but not pixel-identical. It is then listed as a fidelity gap.

---

## 10. What we get for free and what we build

Free (already done, or provided by libraries):

- Our research: every format reader in Python (`tools/tsuki`, `tools/eboot`, about 29,000 lines)
  with 230+ tests, usable as a reference and as **test oracles**; the bit-exact text layout;
  emulation-verified models of variables, layers and motions, the sound runtime, ADV state
  (history, saves, rewind, backlog, auto/skip, config, input actions), boot / title / choice draw
  lists and gallery / flowchart / unlock data; the 35 shader formulas; the build check; the eboot
  table extractors; the corpus statistics that define the test targets.
- SDL3: window, fullscreen, high-DPI, vsync, keyboard / mouse / gamepad with PlayStation button
  labels and rumble, audio device with resampling, folder dialog, message boxes.
- OpenGL: textures, render targets, blending, shaders, compiled by the driver.
- FFmpeg: MP4 demux, H.264, AAC and ATRAC9 decoders.
- .NET: large-file positional IO, SIMD, threads, SHA-256, JSON, xUnit, self-contained publish.
- Designs from precedents: the Mahoyo port's VM/host split and one module per effect; the
  harness-first agent workflow of the Zeus port.

To build: see `what_we_must_build` in the summary and sections 5-9. Size estimate: Tsuki.Data
about 9k C# lines (mostly translated Python), Tsuki.Engine about 15k, Tsuki.Render about 4k + 1.5k
GLSL, Tsuki.Media about 3k, Tsuki.Platform about 1.5k, Tsuki.Ui about 7k, tests and tools about
8k. That is **about 45-50k lines in total**, more than half of it a translation of existing,
tested Python.

---

## 11. Fidelity gaps against the PS4 original

| # | area | gap | impact | mitigation |
|---:|---|---|---|---|
| 1 | menus | Layouts of system menu, save/load, backlog, gallery, flowchart and the `SELA`/`SELS` prompts are not yet extracted | visible on every menu if done by hand | call-decoder extraction (section 9); human sign-off against the user's own PS4 screenshots |
| 2 | text vertical scale | 3.0 px per unit from the engine constants vs 3.115 in the prologue movie (81 vs 78 px line pitch) | slight vertical spacing difference | calibrate on movie frames and PS4 captures (RUNTIME_REQUIREMENTS 9.1) |
| 3 | text styles | look of `@g`, `@z`, `@b`, `@a`; the outline colour table 0xDB7BB0; RGB565 vs BGR565 order; previous-message dimming | rare styled lines | decide on PS4 captures |
| 4 | layer effects | quake / shake / swing per-frame formula (`rand`, `sinf`), crop / mask rasterisation, `STCF`'s reader, `FADE` effect 4, easing id 70, title particles | small motion differences in effect scenes | port from the disassembly the same way as the rest; until then the documented stand-ins |
| 5 | engine-only transitions | types 18, 20..24, 68..70 used by menus (zoom, slide, second rule) are not decoded | menu transitions | cross-fade stand-in, then decode |
| 6 | numeric precision | GLSL float vs PS4 fp16 export rounding (9.7e-4), bilinear filter details | at most about 1 LSB | tolerance in the visual tests |
| 7 | movie decode | FFmpeg software decode vs the PS4 hardware decoder, BT.709 limited range | invisible | none needed |
| 8 | save previews | screenshot thumbnail instead of the PS4's allpac2 composite | different look of the slot picture | PS4 method later (P2) |
| 9 | platform | trophies, PS4 dialogs, light bar, Share Play, screenshot watermark are absent; keyboard/mouse bindings are our design | none for the story | optional achievements log |
| 10 | build | only the analysed eboot (v01.01 hash) is supported; patches, regions and the Switch build need a pattern-based table locator | users with another build cannot play | clear message; locator is P3 |
| 11 | Korean | assets are placeholders on PS4 too | none | - |
| 12 | lip sync | the LV track is a placeholder in every retail entry | none | - |

---

## 12. Proving that the whole game is playable

The proof is automated, deterministic and repeatable, and it runs before any human playtest. All
golden data is produced locally from the user's copy by `tools/oracle` (Python) and never
committed.

### 12.1 Test layers

| layer | what | runs on | pass criterion |
|---|---|---|---|
| T0 static census | tokenize all 549 scripts in C#; every statement name has a handler; every reference resolves | CPU, data | segment lists byte-equal to `tsuki.script` (1,083,168 segments); 0 unknown names outside the 6 no-ops; pictures 10,962 / 10,967 resolve, and the 5 known script bugs plus 3 missing bodies are an explicit allow-list; 16,705 voice refs, 14,543 `_SEPL` names, all `_MPLY` tracks, 40 movie ids, 1,741 jumps and 629 labels, 43,902 text ids resolve |
| T1 data differential | C# decoders vs Python on real data | CPU, data | 49,620 archive entries located with sizes equal; MZX0 equal on all streams of a nightly run (sampled in the fast suite); image decodes equal to `tsuki.image` SHA-1s; 5 x 43,969 text lines; eboot tables equal (catalog, motions, fonts 17,423 lookups, ui_text 7,495); 19 / 19 HE-VAG |
| T2 model differential | feed each Python model's statement corpus through the C# engine | CPU, data | state bytes equal after every statement: `vm_vars` (EMU fuzz cases), layer model, `SoundModel` (whole audio corpus), ADV push/restore/slot cases, choice model, `tsuki.textbox` (175,876 lines, 6,743 messages, 601 golden layouts) |
| T3 explorer | headless run of the whole game in virtual time (12.2) | CPU, data, 16 threads | section 12.2 |
| T4 state round trips | at every page start along the explored paths: save -> load -> save is byte-identical; continuing N = 2,000 statements from a loaded slot gives the same trace hash as the uninterrupted run; same for rewind and backlog jumps on a 1 % sample | CPU, data | 0 differences |
| T5 render | (a) display lists vs the Python layer / compositing models (GPU-free); (b) GPU executor vs `tsuki.compose` numpy renders on synthetic lists, every blend mode and transition; (c) sampled checkpoints of the real game vs PS4 reference frames | GPU or Mesa llvmpipe | (a) equal; (b) max abs diff <= 2/255 on 99.9 % of pixels; (c) see 12.3 |
| T6 media | decode every referenced asset once: 17,925 ATRAC9 streams, 19 vag, 134 movies to the last frame; A/V sync of the prologue | CPU, data, nightly | 0 decode errors; sync within one frame; loops seamless (sample-exact join test) |
| T7 performance | prologue + one hour of scripted play at real time | GPU | no logic step over budget, no frame over 16.7 ms during transitions, 0 audio underruns |
| T8 human | checklist playtest of the feel: text speed, transition timing, voice timing, menus | human | sign-off per milestone |

Synthetic tests (no game data: VM readers on hand-written statements, MZX0 on synthetic streams,
mixer maths, layout on synthetic strings) run on every commit anywhere. Data tests skip cleanly
without `TSUKI_GAME_DIR`, as the Python suite does.

### 12.2 The explorer: every script and every branch

`Tsuki.Cli explore` runs `Tsuki.Engine` with a null renderer and a null mixer (the sound model
still runs) in virtual time: waits end at once, messages auto-advance, movies "end" after their
file has been opened and probed.

- **Branching**: at `_SELD` / `_SELA` / `_SELS`, snapshot the state (it is already a byte block,
  3.3) and queue one branch per enabled option; locked options are skipped as the choice model
  does. Deduplicate on a hash of (script, command offset, F, the G range, the scene bit sets), so
  re-joining branches collapse.
- **Persistent unlocks**: routes and extras gated by persistent state (scene reached / completed
  sets, G values, picture-seen bits) are explored in "campaigns". The system block of a finished
  playthrough carries into the next New Game, through the real boot / title logic (G[10], G[2],
  bit 485), until coverage stops growing. Gallery replays (scene 3000 ARCHIVE with each G[8]) and
  the 25 Ending List scenarios are explored the same way.
- **Coverage report**: per script, the statements and markers executed. The pass criterion:
  100 % of the story scripts and extras reachable from the retail flow, every `ZM` marker on those
  paths, all 206 story choice options, every ending and every `RDED` bit that the scripts can set.
  Unreached statements are allowed only on an explicit, reviewed exclusion list: Switch branches
  behind `IFNX`/`IFP4`, `ZD`/`DMMY`, debug scripts (`TEST_SCRIPT`, `JUMPxx`) that only the debug
  menu reaches.
- **Failure conditions**: unknown command, handler exception, out-of-range variable index,
  missing scene or label, unresolved picture outside the allow-list, missing voice / SE / BGM /
  movie, text id out of range, layout over 1,200 elements, a wait that can never end (`VCWT`
  without a voice, `TOMW` without a movie), and the livelock rule "N frames without a message,
  choice or script end".
- **Determinism**: two runs give identical trace hashes.

Order of magnitude: the scripts hold about 1.08 M segments in total. A headless C# VM with the
state models should run well over 1 M statements per second, so even thousands of branch
combinations explore in minutes on 16 threads.

### 12.3 Visual sampling

Checkpoints are picked by script position, not by time: the first page of every script, every
choice screen, the first use of each `STBR` mode, each `STEC` mode and each `RULE_nn`, each motion
easing, first and last frames of each movie, title, config and every menu. That is about 2,000
frames. Each is rendered offscreen and compared with:

1. **the Python reference render** of the same engine state (the C# engine dumps the state, and
   `tools/oracle` renders it with the layer model and `tsuki.compose`): PSNR >= 45 dB, max
   abs diff <= 2/255 on 99.9 % of pixels. This catches renderer bugs.
2. **PS4 ground truth**: the prologue movie frames that show `00_02` (25 s, 60 s), the title still
   (frame 241), and screenshots the user takes on their own console at listed checkpoints. They
   are kept local and compared by SSIM >= 0.98 plus human review. This catches shared
   misunderstandings that the Python and C# code would both have.
3. **approved goldens** (local) for regressions.

---

## 13. Milestones and acceptance criteria

| # | milestone | contents | acceptance | weeks (calendar) |
|---|---|---|---|---|
| M0 | harness and skeleton | solution layout, CI on synthetic tests, `tools/oracle` golden export, hooks (no skipped tests, no edits to oracle / generated files), SDL3 window + GL clear, build check | `dotnet test` green on synthetic tests; build check accepts the user's eboot and rejects a modified copy | 0-1 |
| M1 | data layer | port of containers, MZX0, images, catalog, text, scripts, fonts, eboot tables, audio decode | T0 and T1 pass | 1-2 |
| M2 | headless VM and explorer v1 | VM, readers, variables, flow, choices (first option or all), stub handlers that record state for every other command, null renderer | explorer covers 100 % of the story scripts with 0 failure conditions; T2 for `vm_vars` passes | 2-3 |
| M3 | vertical slice | renderer basics (layers, `FADS` 0/1/2, `STBR` of the opening, motions), text box with ruby, voice, SE, BGM, prologue movie | RUNTIME_REQUIREMENTS 7.5 checks 1-9 pass (in both ja and en) | 2-4 |
| M4 | full play loop | every layer command, quake / shake / swing, `STEC` recolour, screen effects, the whole sound model, movies, choice screen, boot and title | T2 for layers, sound and choice passes; T5 (a)/(b) pass; explorer v2 with real handlers at 100 %; a human plays one complete route in Japanese | 4-6 |
| M5 | ADV features | history ring, rewind, backlog with voice replay, save/load (139 + quick + auto FIFO), Continue, auto, skip with read tracking, config logic and persistence, language switch | T4 passes; config survives a restart; explorer passes in all four languages | 5-8 |
| M6 | menus and extras | system menu, save/load and backlog screens, `SELA`/`SELS` prompts, gallery (CG, music, movie, ending list), flowchart | every menu entry of engine 8.3 reachable; gallery unlocks follow the bit sets; flowchart launches scenes; T5 (c) on menus signed off | 7-11 |
| M7 | release | first-run setup, prefetch tuning, T6 and T7, tests on an English-locale Windows and a non-ASCII path, self-contained publish with licence notices (FFmpeg LGPL, SDL, our MIT) | full checklist T0-T8 green; a clean machine plays from the folder picker to the end credits | 9-12 |

---

## 14. Agent workflow

- Streams with one owner each: **A** data layer; **B** text, fonts and text box; **C** VM,
  variables, flow, choices and explorer; **D** layers, motions and compositor (engine model + GL);
  **E** audio and movies; **F** ADV state and config; **G** UI extraction and screens; **H**
  harness, CI, packaging. A lead agent defines the cross-project interfaces in M0 (`DisplayList`,
  `AudioCommand`, `IHost`, state block accessors) and then only reviews.
- Every stream works test-first against an oracle. Translating Python to C# is mechanical, and the
  differential test tells the agent when it is right.
- Status file per stream, non-interactive batches, easiest items first, at most 4-6 agents at
  once. Hooks enforce the rules: no skipped or deleted tests, no edits under `samples/golden`, no
  game data in the repo.
- A human judges feel, not correctness: text speed, transition timing, voice / BGM balance, menu
  look. About 30-60 minutes a day, more around M4 and M6.

---

## 15. Install requirements

End user:

- Windows 10 / 11 x64; a GPU with OpenGL 3.3 (any GPU of the last ten years). The ANGLE fallback
  covers broken GL drivers.
- Their own extracted `CUSA47999` folder with the analysed eboot.bin (v01.01 hash).
- About 250 MB for the app folder: our code, the self-contained .NET runtime, SDL3.dll and the
  FFmpeg LGPL DLLs. Trimming FFmpeg to the needed decoders would make it smaller, but that needs
  a compiler and is optional. No installer is required: unzip and run.
- No conversion, no extra game-data copies, no admin rights.

Developer machine (all present except where marked):

- .NET 10 SDK (**to install**: `winget install Microsoft.DotNet.SDK.10`); the .NET 9 SDK is
  enough for the first weeks.
- Python 3.11 + `.venv` with numpy / pillow / pytest (+ numba) for the oracles (present).
- FFmpeg 9.0 **shared LGPL** DLLs (to download: BtbN `win64-lgpl-shared` 9.0, matching
  FFmpeg.AutoGen 9.0.1); the static `ffmpeg.exe` already present stays for tests.
- NuGet packages: `ppy.SDL3-CS`, `Silk.NET.OpenGL`, `FFmpeg.AutoGen`, xUnit.
- Optional: Mesa llvmpipe (software GL) for GPU-less render tests; a Dear ImGui binding for the
  dev overlay.
- **Not needed**: Visual Studio, VS Build Tools, a C/C++ compiler, Ren'Py or KiriKiri.

---

## 16. Realistic time to the full game

With 4-6 agents in parallel and a human reviewing daily:

- First complete route playable end to end with text, voices, BGM / SE, movies, choices and
  transitions (M4): **about 5-6 weeks**.
- Complete game with save/load, backlog, rewind, auto/skip, read tracking, config and all four
  languages (M5): **about 8 weeks**.
- Gallery, flowchart, menus matching the PS4 and release polish (M6-M7): **about 10-12 weeks**.
- Risk-adjusted upper bound: **about 16 weeks**. The menu extraction (section 9) and visual
  sign-off are the parts most likely to slip. The engine core is not, because it is a
  translation of verified models.

---

## 17. Risks

| id | risk | likelihood | what we do |
|---|---|---|---|
| R1 | menu layouts are slower to extract than planned; menus end up hand-built and visibly different | medium-high | start stream G in week 1; the call decoder already works on the title and choice screens; hand-built fallback per screen |
| R2 | shared misunderstanding: C# matches Python, but both differ from the PS4 (for example the text vertical scale or an [H] layer field) | medium | PS4 ground-truth frames at checkpoints (12.3 item 2); the user's own console captures |
| R3 | the explorer's state space explodes on flags | low-medium | hash deduplication; campaigns; bound branches per choice block; the 206-choice structure is small |
| R4 | Windows OpenGL driver problems on some machines (Umineko had to move to ANGLE) | low on this machine, medium for others | GLSL ES-compatible subset so ANGLE works; display lists keep a D3D11 backend possible (~1-2 weeks) |
| R5 | native interop bugs (FFmpeg.AutoGen pointers, SDL callbacks) crash the app | medium early | a thin wrapper layer with its own tests; nightly decode-all (T6); crash log with the script position |
| R6 | we own all of the code: no community engine fixes our bugs | certain | the size is small (45-50k lines), the code is tested per module, and there is one game to support |
| R7 | only one eboot build is supported | certain | clear refusal message; pattern-based locator later (P3) |
| R8 | timing feel (text reveal, auto-advance, transition curves) is correct by the numbers but "feels off" | low-medium | the numbers are the engine's (frames, float32); human sign-off at M4 |
| R9 | distributing FFmpeg's H.264 decoder raises patent questions in some places | low | `IMovieDecoder` with a Media Foundation fallback that uses Windows' own licensed decoders |
| R10 | GC or decode stalls on very large pictures | low | prefetch, parallel tile decode, pooled buffers, low-latency GC mode; measured in T7 |

---

## 18. Honest comparison with the alternative routes

- **Ren'Py** has save, rollback, history, auto/skip, preferences and packaging for free. It needs
  all movies re-encoded (Ren'Py does not decode H.264/AAC), all audio transcoded, roughly
  35,600 pictures converted, a HuneX-to-`.rpy` transpiler, custom shaders for the
  backdrop-reading blend modes and an exact rule wipe, and its own text layout instead of the
  bit-exact HuneX layout. Its default screens do not look like the PS4 menus, so faithful menus
  are custom work in Ren'Py too. The UI long tail exists in every route; Ren'Py only shortens the
  logic behind it, which we already have as verified models.
- **KiriKiri Z** matches the original authoring lineage, but TYPE-MOON's macro library for the
  remake was never released, the active Wamsoft fork needs VS2022 + vcpkg, and agents know TJS
  poorly.
- **Own engine** (this document): no conversion, the most fidelity per unit of effort, and the
  strongest testing story, because every module has a verified twin. The cost is owning the
  renderer, mixer and menus.

---

## 19. Sources

Project-internal (primary): `docs/FORMATS.md`, `docs/RUNTIME_REQUIREMENTS.md`,
`docs/formats/{script,engine,layers,compositing,textbox,text,fonts,audio,sound_runtime,adv_state,
system_flow,gallery,vm_variables,archives_ui_movies,images,containers}.md`,
`docs/research/engine_lineage.md`, `tools/tsuki/*`.

External (checked 2026-10-25):

- Mahoyo Android port, repository and runtime layout: https://github.com/david419kr/mahoyo-steam-android-port ,
  https://github.com/david419kr/mahoyo-steam-android-port/tree/main/runtime ,
  third-party notices: https://raw.githubusercontent.com/david419kr/mahoyo-steam-android-port/main/THIRD_PARTY_NOTICES.md
- tsukiweb: https://github.com/requinDr/tsukiweb-public
- impacto status: https://github.com/CommitteeOfZero/impacto/issues/1
- Umineko Project history: https://umineko-project.org/en/project-info
- rlvm: https://github.com/eglaysher/rlvm
- Zeus port write-up (harness first): https://carterappleton.com/blog/porting-zeus-to-the-browser/
- FSN REMASTERED locale issue thread: https://steamcommunity.com/app/2396980/discussions/0/4513254889540907719
- SDL3 C# bindings: https://github.com/ppy/SDL3-CS , NuGet https://www.nuget.org/packages/ppy.SDL3-CS
- SDL3 folder dialog: https://wiki.libsdl.org/SDL3/SDL_ShowOpenFolderDialog ; gamepad labels:
  https://wiki.libsdl.org/SDL3/SDL_GetGamepadButtonLabel
- Silk.NET: https://github.com/dotnet/Silk.NET , NuGet https://www.nuget.org/packages/Silk.NET.OpenGL
- FFmpeg.AutoGen: https://github.com/Ruslan-B/FFmpeg.AutoGen , NuGet https://www.nuget.org/packages/FFmpeg.AutoGen/
- FFmpeg LGPL shared builds: https://github.com/BtbN/FFmpeg-Builds
- Sdcb.FFmpeg (alternative bindings, LGPL, newest line 7.0): https://github.com/sdcb/Sdcb.FFmpeg
- LibAtrac9: https://github.com/Thealexbarney/LibAtrac9
- Raylib-cs releases (rejected alternative): https://github.com/raylib-cs/raylib-cs/releases
- Mesa for Windows (llvmpipe): https://github.com/pal1000/mesa-dist-win
- .NET support policy (.NET 9 ends 2026-11-10, .NET 10 LTS to 2028-11):
  https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core ,
  https://devblogs.microsoft.com/dotnet/dotnet-sts-releases-supported-for-24-months/
- Ren'Py movie formats (no H.264 on desktop): https://www.renpy.org/doc/html/movie.html
- KiriKiri Z Wamsoft fork: https://github.com/wamsoft/krkrz
