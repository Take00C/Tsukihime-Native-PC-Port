# Strategy: convert TSUKIHIME (PS4) into a Ren'Py game on the user's machine

Design proposal, 2026-10-25. Route under review: **CONVERT TO REN'PY**. A Python converter reads the
user's own `CUSA47999` folder and writes a Ren'Py 8.5 project: transpiled `.rpy` scripts, converted
assets and data tables. A small `hx` runtime package inside Ren'Py supplies the HuneX behaviour that
Ren'Py does not have. Ren'Py's built-in save/load, rollback, history, auto/skip and preferences are
reused and restyled with the game's own UI art.

Inputs read for this plan: `README.md`, `docs/FORMATS.md`, `docs/RUNTIME_REQUIREMENTS.md` (RR),
`docs/formats/*.md` (script, layers, compositing, textbox, sound_runtime, adv_state, system_flow,
gallery, vm_variables), `docs/research/engine_lineage.md`, the `tools/tsuki` modules, and the
sibling plan `docs/design/strategy_own_engine.md`. Ren'Py facts come from the official
documentation and source code (section 15). Numbers marked **[measured]** were measured on this
machine for this plan. **[doc]** means taken from our format documents, **[src]** read in Ren'Py's
source or docs, and **[H]** is an estimate or hypothesis that a milestone-0 spike must confirm.

---

## 0. Verdict

- **The route is feasible and reaches a complete game.** Ren'Py 8.5.3 (released 15 May 2026)
  provides the whole application shell:
  - window, fullscreen, letterboxing, a GL2 renderer with custom GLSL shaders, and
    render-to-texture through `sampler2D` uniforms that accept a Render;
  - automatic tiling of pictures larger than the GPU texture limit, and an image cache with
    background prediction;
  - audio channels, mixers and fades, VP9/AV1 movies;
  - save slots, quick save and FIFO autosaves with thumbnails and JSON metadata, persistent data,
    rollback and roll-forward, a history list with rollback identifiers and voice replay;
  - preferences, keyboard/mouse/gamepad bindings, a screen language for menus, scene replay;
  - lint, warp, a test framework and a Windows packager.
- **Ren'Py does not remove the hard parts.** Three HuneX behaviours have no Ren'Py equivalent.
  - The 40-layer HuneX stage. Changes commit at `_FADS`; 24 blend shaders read a copy of the
    backdrop; motions animate 26 keyframe fields with Penner easing; layers can have masks, crops
    and parents; `_STEC` applies CPU recolour LUTs. Ren'Py's `show`/ATL/`with` model cannot
    express this.
  - Text, which must use HuneX's own glyphs, layout and reveal.
  - The HuneX sound rules: two BGM slots, 20 SE channels with tags, ducking and voice gating.

  So the port contains a custom **Stage** displayable, a **message renderer** and an **audio
  model**, all running inside Ren'Py. That is roughly the same engine core the own-engine route
  writes. Ren'Py saves us the menus framework, saves, rollback, preferences, input, playback and
  packaging, and the per-route application plumbing.
- **This route can keep the verified models.** It is not limited to Ren'Py's own text or blend
  model.
  - Text layout is precomputed offline with the bit-exact `tsuki.textbox`.
  - The 24 blend formulas of `compositing.md` 2.3 port line by line to GLSL.
  - Ren'Py runs CPython 3.12 and imports pure-Python packages, so the pure-Python models run
    inside the game almost unchanged: the layer/motion model `tsuki.misc_layer_state_and_motions`
    and the variable model `tsuki.vm_vars`. The sound model `misc_audio_runtime_semantics` needs
    a numpy-free float32 shim first.

  This corrects the remark in `strategy_own_engine.md` section 18 that Ren'Py "throws away" the
  bit-exact layout and shader math.
- **A one-time conversion is mandatory**, because Ren'Py cannot read any of the formats.
  - Ren'Py's FFmpeg build enables no H.264, AAC or ATRAC9 decoder [src renpy-build `tasks/ffmpeg.py`].
  - Ren'Py imports only pure-Python packages, so numpy and numba are not available
    [src python.html]. Our pure-Python MZX0 decoder runs at about 36 MB/s [doc README].
  - On-the-fly decoding of the 123-gigapixel picture set inside Ren'Py would therefore stall
    frames.
  - Measured on this machine: lossless WebP stores the pictures in **about 53-69 GB**, encoded in
    well under 2 hours on 14 worker processes. Movies need a VP9 re-encode (about 4,000 s of
    1080p video, roughly 1 hour), and audio an Opus transcode (minutes). Total disk is
    **about 60-80 GB** and total time **about 1.5-3 hours**, resumable. The order puts the
    prologue and chapter 1 first, so the user can start reading while the rest converts.
- **Nothing derived from game data is distributed.**
  - The shipped package is the Ren'Py runtime, our `hx` code, our screens and the converter.
  - The converter generates the `.rpy` scripts, data tables and assets on the user's PC, outside
    any build or repository.
- **Time.** We expect all routes to be playable start to finish, with saves and backlog, in about
  **4 weeks**, and a faithful game with restyled menus, gallery and flowchart in about
  **7-8 weeks**. The range is 5-12 weeks. This assumes 4-6 parallel agents and a human reviewing
  a few hours a week (section 12).
- **Kill criteria.** Milestone 0 runs spikes on blend performance, script size, loop accuracy,
  movie playback and bot execution, and each has a pass/fail threshold. If one fails, the
  project switches to the own-engine route. The converter, IR, reference explorer, oracle
  renderer and tests are route-independent and carry over.

---

## 1. Lessons from precedents applied to this route

- **Conversions work only when the target already implements the macros.** VitaScenariosTool
  maps HuneX Vita opcodes onto TYPE-MOON's existing KiriKiri macro library, and any opcode
  without a mapping is emitted as a comment. Here no Ren'Py library for HuneX semantics exists,
  so the `hx` runtime **is** that macro library. We write it once, in Python and GLSL, against
  our verified models.
- **Hand-written Ren'Py remakes stall.** FatefulRemake has 6 commits and no release. Only a
  fully automatic converter is worth doing, so nothing in this plan is written per scene.
- **Decode offline into standard formats.** tsukiweb uses a 7-step asset builder with
  precondition and result checks. We do the same, with a manifest.
- **Separate decoding from execution.** In the Mahoyo Android port, the VM hands each command to
  the host, which returns Continue, Yield or Halt. We apply that split at transpile time. Runs of
  commands that continue become one batch, and every command that can yield becomes its own
  Ren'Py statement. Ren'Py's statement-level saves and rollback then fall exactly on the HuneX
  yield points.
- **Use IDs, not fuzzy matching.** Text (`$NNNNNN`), voices (`_VPLY` hex index) and pictures
  (catalog index) are referenced explicitly, so the backlog, voice replay and language switching
  key on those IDs.
- **Avoid known Windows-port bugs.** Paths are UTF-8 and never depend on the code page. Codecs
  are pinned because they are inside Ren'Py's own FFmpeg. We test on an English-locale Windows.
- **Build the harness first** (section 9). Never accept "it compiles" as proof.

---

## 2. Architecture

```
user's CUSA47999 folder (read-only)
        |
        v
hxconvert  (Python 3.11 + numpy/numba/Pillow + tools/tsuki + ffmpeg; ships as code)
   step 0  verify build: eboot SHA-256 + structural probes + file counts (RR 3.3)
   step 1  eboot tables  -> data/tables/*.json  (catalog, names, scene ids, movie table + remap,
                            motions, font + layout + window tables, ui_text, STEC LUTs,
                            config defaults, gallery / flowchart / music / ending tables)
   step 2  scripts       -> IR (tsuki.script tokens, both argument readers, operands per
                            vm_variables.md) -> game/hx_gen/sNNN_<name>.rpy + data/ops/sNNN.bin
   step 3  text          -> data/text/{ja,en,zc,zt}.json; layouts -> data/layout/<lang>/*.bin
   step 4  pictures      -> assets/img/... .webp (alpha-cropped, offset in manifest),
                            thumbnails, UI parts, RULE_nn, font pages, parts.mrg screens
   step 5  audio         -> assets/{bgm,se,voice,sys}/*.opus + loops.json + gain.json
   step 6  movies        -> assets/movie/*.webm (VP9 + Opus)
   manifest.json: inputs, outputs, SHA-1s, tool versions, step status (resumable; step checks)
        |
        v
Ren'Py 8.5.3 project ("Tsukihime.exe" after `launcher distribute`)
   game/hx/      OUR CODE (shipped)
       vm.rpy        state, operands, command handlers, creator-defined statements (CDS)
       stage.rpy     Stage displayable, layer model, motions, shakes, transitions
       shaders.rpy   hx.blend (modes 1..25), hx.rule, hx.stec, hx.mask
       text.rpy      message renderer (glyph quads), pages, windows, reveal, read tracking
       audio.rpy     sound model (2 BGM slots, 20 SE, voice, system SE), reconciler
       movie.rpy     _TOMP/_TOMW, language remap, title loops
       system.rpy    boot flow, title state, ATSV, next/previous scene, rewind, config mapping
       screens_*.rpy title, game menu, save/load, config, backlog, choice, dialogs, gallery,
                     music, movie, ending list, flowchart
       bot.rpy       bot mode, trace, checkpoints, screenshots (developer only)
   game/hx_gen/  GENERATED LOCALLY (never shipped, excluded via build.classify and .gitignore)
   converted asset root (outside game/, added to the search path at init)
```

**State model.** All game state lives in one store object, `default hx = HxState()`, using value
semantics. Each statement mutates a working copy and then stores immutable values back: `bytes`
for the 0x1A08-byte `scene_state` (RR 4.5) and tuples for the variable arrays. Mutations inside
a `bytearray` are invisible to Ren'Py's rollback, and Ren'Py treats data created in plain Python
as non-revertable. Freezing into immutable values makes every save and rollback exact by
construction. It costs about 7 KB per checkpoint for the scene, and 4.8 KB for F[1200] of u16
only when it changed.

- `hx.vm`: F[1200] (tuple of u16), the C slots (`C<n>` = `F[F823+n]`), the SELR slots, keep-BGM
  and the other flags, and the current script, label and page.
- `persistent.hx`: G[0..499] and the other system-data values; the scene bit sets `_RDST` /
  `_RDED` (G area) and `_RTST` (F area); the picture-seen set; `readmax[4020]`; the config block;
  the language.
- `hx.stage`: `scene_state` bytes for pending changes and committed bytes; the transition record
  (type, ms, rule, commit frame); global quake state.
- `hx.text`: the messages of the current page (line IDs and end modes); window state (F1104,
  F1105, F1109); the pending voice.
- `hx.audio`: the desired state of both BGM slots and of looping SEs.

Display objects (Stage, MessageView) hold no state of their own. They render from `hx` at the
current time, so after load or rollback they are correct automatically. Ren'Py forbids storing
Render objects on displayables, which suits this design. `HxState` carries a version number;
`after_load` migrations keep old saves loadable when the runtime changes.

**Frame time.** Ren'Py renders at the monitor rate with a variable `st`. HuneX logic is a fixed
60 Hz step (RR 4.18). The Stage and the MessageView convert the time since the statement
started into whole 60 Hz frames (`frames = floor(t*60)`). They evaluate tweens, motions, reveal
counters and transition progress with the engine's integer and float32 formulas, so the
quantisation matches the PS4: `trunc(255p)/255` cross-fade weights, `(ms*3)/50` waits, and
1000/(60v) reveal counters.

---

## 3. Script translation

### 3.1 Why we transpile, and with batches

| option | what it is | verdict |
|---|---|---|
| A: Ren'Py-native statements | `show`/`scene`/`with`/`play`/`nvl` per HuneX command | **Rejected.** Changes are visible as soon as a `show` runs, while HuneX changes are invisible until the commit. A `with` blocks the script, while `_FADS` yields one frame. Ren'Py has 5 built-in blend names, and HuneX has 26 modes that read a backdrop. ATL restarts on load. HuneX motions use keyframe tables in the eboot, not ATL. |
| B: interpreter in Ren'Py | one label that loops `$ hx.step()` over the original statements | **Fallback only.** Saves capture state at the start of a statement, so a step that runs many commands and then interacts replays from its start on load. Lint, warp, prediction and asset checks per statement are lost. |
| **C: transpile control flow and yield points, batch the rest** | each HuneX label becomes a Ren'Py label; JUMP, GOTO, IF and GOTS become `jump`/`if`; every command that can yield becomes one creator-defined statement; runs of continue-only commands become one `hx ops` batch | **Chosen.** Native saves, rollback, lint, warp and prediction fall on the HuneX yield points. |

Statement budget [doc, measured from `samples/script/command_stats.json`]:

- The corpus has 1,083,168 statements in 98 names. `_WKST` alone is 487,857, and the
  bookkeeping before every message is `_WKAD(F823,1);_WKST(C847,...);_WKST(C859,...)`.
- One Ren'Py node per HuneX statement would exceed 1 million nodes.
- Commands that yield or affect control flow total about 150,000: `ZM` 42,590, `FADS` 34,040,
  `WTTM` 14,828, `CLO3` 12,208, the labels, `IF__` 1,194, `JUMP` 1,741, `PGST` 17,202, and others.
- With batches the port has **about 150,000-200,000 Ren'Py statements**, which is a large but
  ordinary Ren'Py game. Ren'Py 8.4 changed the script representation so that loading took about
  50 % less time for one large game [src changelog].
- M0 measures load time and memory with a synthetic 250,000-statement script. The kill
  threshold is 30 s or 2 GB on this machine.

### 3.2 Generated form

One `.rpy` file per script (549). The opening of `00_02` (script 3, scene 270) would look like
this:

```renpy
# generated by hxconvert from allscr.mrg section 6 (00_02); do not edit, never distribute
label hx_s003:
label hx_s003_START:                      # _ZZ01a01(START)
    hx ops 3 0     # @000b..0239  PGST(-1) PGST(0) ZZ(LBL_000) PGST(10000) RDST/RTST/RDAS(485) 27x WKST WNTY(1)
    hx clo2        # @0242
    hx ops 3 1     # @024d        WKST
    if hx.cmp("F026", "ne", 0):           # @025c _IF__(F026,ne,0,LV00000)
        jump hx_s003_LV00000
    hx ops 3 2     # @0275        STCC(-1)
    hx fads 1 0 0  # @027f
    hx ops 3 3     # @028c        27x WKST
label hx_s003_LV00000:                    # _ZY01c01(LV00000)
    hx ops 3 4     # @046a        STCP(1,1) STCH(0,img0597,...) STRT STGS STCP(0,9250)
    hx fads 1 0 0  # @04d5
    hx ops 3 5     # @04e2        STGS(0,,255,1,0)
    hx stcf 0 8    # @04f4        (STCF always yields)
    hx ops 3 6     # @04ff        STCC(1) STCP(0,2)
    hx fads 1 0 0  # @0513
    hx wait 1000 0 # @0520
    hx ops 3 7     # @052e        4x WKAD(C847,1) VPL2(B90_00_01_0000_0,03d00) STCH(2,img2458,...) STMA(2,22523)
    hx vcwt        # @05cb
```

- Each `hx ...` line is one creator-defined statement (`renpy.register_statement`). It has
  `parse`, `execute`, `predict` (returns the pictures, rules and font pages the next commit will
  need, so Ren'Py preloads them), `lint` (checks every referenced asset with
  `renpy.loadable`, and every text ID, motion ID and label) and `warp`.
- Batches are stored as compact op tuples in `data/ops/sNNN.bin` and loaded once. The `.rpy`
  holds only `(script, batch)`, so it stays small and readable. Every line carries the source
  offset, so a failing trace points straight to `script:offset` in `tsuki.script dump`.
- Labels:
  - HuneX labels (`ZZ` and `ZY` markers, label table section 1) become `hx_sNNN_<label>`.
  - `_JUMP(scene, label)` becomes `jump hx_sKKK_<label>`, with the scene ID resolved through
    `SCENE_IDS` at convert time. An unknown scene falls back to START_SCRIPT, as the engine does.
  - `_GOTS` becomes an `if/elif` chain on `_D000`.
  - `_IFNX` and `_IFP4` are platform branches. They are resolved statically to the PS4 path, and
    the Switch code is dropped and listed in the convert report.
- No-ops are removed at convert time and counted: `_SNX4 _FANX _IFP4 _DMMY _STQK _NOP_` and
  `ZD`.
- Ren'Py's lint then checks every jump target, and `lint --error-code` fails the build on a
  missing label.

### 3.3 Command families

The handlers are written once in `game/hx/vm.rpy`. Where a pure-Python tsuki model exists, it is
imported from `game/python-packages/tsuki_rt/`, a trimmed copy of the runtime-relevant modules,
or ported with a float32 shim.

| family | commands (count) | Ren'Py form | implementation | reuse / status |
|---|---|---|---|---|
| variables | WKST 487,857, WKAD 45,679, WKOR 21 | inside `hx ops` | operand forms F/G/C/`_D`/literal per position, u16 wrap, out-of-range rejected | `tsuki.vm_vars` is pure Python and emulation-checked: import it |
| flow | JUMP 1,741, GOTO 101, IF__ 1,194, GOTS 170, IFSC 2, IFUR 1 | Ren'Py `jump` / `if` | static targets, conditions through `hx.cmp` with the engine's comparison semantics | targets V-all |
| scene bookkeeping | PGST 17,202, RDST 530, RTST 527, RDAS 530, RDED 550, STCG 39, ATSV 398, WTSV 28, TRPY 1 | `hx pgst` (page start: hard rollback checkpoint), `hx atsv`; the rest batched | bit sets in `persistent.hx` / `hx.vm`; ATSV means scene start, autosave and scene-stack identifier (adv_state 2.5) | adv_state, gallery 2 |
| script end | END_ 519, RTM_ 545, RTMN 1, RTM2 1 | `hx end` / `hx rtm` | cleanup of RR 4.8 (stop 20 SE channels, BGM unless keep-BGM), then `MainMenu(confirm=False)`; the keep-BGM flag survives into the title | script 4.1 |
| messages | ZM 42,590, MSAD 678, WNTY 58,568, CLO2..5 13,787, MSQK 30, FCSP/FCLN 57 | `hx msg <block> <sub> <args>`, `hx msad`, `hx clo3`...; WNTY batched | MessageView (4.2): line IDs `$`-expanded per language at display time; page stacking; window fades; full-screen and framed narration | layouts precomputed with `tsuki.textbox` (bit-exact, 175,876 lines) |
| voice | VPLY 16,689, VPL2 16, VCWT 75 | batched pending voice; `hx vcwt` | the voice starts with the next message (sound_runtime 7), `renpy.voice(file, tag=slot)` for per-character volume; stop on advance per config +0x4E and F1123 | sound_runtime 7-8 |
| pictures and layers | STCH 54,494, SCH2 4,675, STCC 63,261, STCP 51,568, STZ4, STRT, STRR, STRA, STPV, SOXY, STGS, STBR 14,940, STEC 12,900, SNX2/3, STMA 24,333, SGMA, SPMA, STNS, STTR, SACL, SBAS, STCL, SPFC, SPRD, SPRC, SQK2/SQS2, SHK2/SHS2, SSW2/SSS2, SHOK, SHAF, FADE | inside `hx ops`, except STCF (yields) | `SceneModel.step()` on the pending `scene_state`; live operations (timed STGS and others, layers 4.3) applied to the drawn record at once | `tsuki.misc_layer_state_and_motions` is pure Python and emulation-checked: import it |
| transitions | FADS 34,040 | `hx fads type ms rule [mode]` | commit (layers 5): pending bytes become committed, the picture-seen bits are set, and the transition record is stored; it yields one frame; whether the next message waits for the transition follows the wait model (M1 checks it against PS4 timing) | compositing 7 |
| BGM | MPLY 1,171, MVOL 780, MFAD 724 | inside `hx ops` | sound model: two alternating slots, same-track request ignored, negative track stops, `trunc(config*vol*0.01)` | sound_runtime 3.2-3.3 |
| SE | SEPL 14,543, SEFD 3,516, SEVL 1,285, SESP 18 | inside `hx ops` | 20 channels; tags `001` fade, `017` volume, `018` pan, `049`, `068` deferred queue, `069` start offset, `070` voice character | sound_runtime 3.4-5 |
| movies | TOMP 48, TOMW 48 | `hx tomp id` (continues), `hx tomw` (waits) | language remap (aum 9.3), Movie displayable on layer `hxmovie` | aum 9 |
| waits | WTTM 14,828 | `hx wait ms skippable` | 60 Hz frame count `(ms*3)/50`; skippable by decide when the second argument is 1 | RR 4.18 |
| choices | SELR 824, SELD 170, SELA 26, SELS 1 | Ren'Py `menu:` with a custom choice screen where the SELR...SELD block is static (80 of the 83 story blocks); `hx seld` otherwise | `_D000 = k` in every branch; locked items through `menu` conditions and `config.menu_include_disabled` | system_flow 5, vm_variables 4.5-4.6 |
| system state | XSTP/XRSM 2+2 | batched | system-state change around one CIEL13_7B scene; implemented as in system_flow | [H] |

98 of 98 statement names in use are mapped: 88 implemented, 6 no-ops, and 4 marker kinds. The
converter fails on any name outside this table, so an unknown command can never reach the game.

### 3.4 Rollback granularity and the HuneX features built on it

| HuneX behaviour (adv_state) | Ren'Py mechanism |
|---|---|
| History ring: one record per state-changing command, 800 records | One rollback log entry per Ren'Py statement. `config.rollback_length` and `hard_rollback_limit` are raised (to about 1,000). The batch state is in the store, so restoring a statement restores everything before it. |
| Rewind one **page** at a time (5.1) | `hx pgst` and the page's first `hx msg` are hard checkpoints; later messages on the page are soft checkpoints (`renpy.checkpoint(hard=False)`), so the rollback key stops at page starts. M4 decides between this and a custom rollback count. |
| Save = page-start record, load = rerun the page's first message (7.3) | Ren'Py saves at the start of the current statement; loading reruns it. This matches the PS4's "re-run the message statement" on load. |
| Backlog jump: restore the entry's record and run its command again (5.5) | `RollbackToIdentifier` with the identifier stored in our history entry. |
| Next scene (5.2): run without waits until `_ATSV`, a choice or unread text | Skip mode with a flag that `hx atsv` clears. |
| Previous scene (5.3) | Roll back to the checkpoint recorded by the previous `hx atsv` (`renpy.get_identifier_checkpoints` + `renpy.rollback`). |
| Slots: 0..99 manual, 100..138 auto FIFO, 139 quick | 100 manual slots on file pages, `config.autosave_slots = 39`, `config.quicksave_slots = 1`; `hx atsv` calls `renpy.force_autosave()` when "auto save at choices / scene start" is on. |

---

## 4. The `hx` runtime inside Ren'Py

### 4.1 Stage (40 layers, compositing, transitions)

- A creator-defined displayable at 1920 x 1080 on the `master` layer. Its `render()` works as
  follows:
  1. Take the committed record and apply the live operations, tweens, motions (`motion_value`
     with the engine's Penner `ease`), quake, shake and swing for the current 60 Hz frame. These
     are pure-Python functions of `layers.md` 6-8.
  2. Sort the layers by z.
  3. Compute each layer's frame matrix (`layer_matrix` / `frame_matrix`, layers 7: parent chains,
     zoom and rotation pivots, size-class factors).
  4. Draw the picture through `Transform(matrixtransform=...)`. A body and its face are two
     draws at the same origin (catalog record), and language-band pictures use their pre-cut
     band.
- **Normal and fixed-function modes** (mode 0 normal, 8 opaque, the alpha paths) blit directly
  with `gl_blend_func` set per render.
- **Backdrop modes** (`_STBR` 1..25 except the fixed-function ones) use one Model render per
  layer. The layer goes in `tex0`, and the uniform `u_backdrop` holds the Render of everything
  drawn below it. The shader `hx.blend` evaluates the formula of compositing 2.3: nexl family
  vs PS family alpha, `k = alpha*max(P.z, a)`, the dodge black guard `d < 3/255`, power-form
  soft light, and NaN-safe min/max. It un-premultiplies Ren'Py's premultiplied inputs and
  premultiplies its output again. A `sampler2D` uniform that takes a Render is documented
  [src model.html]. The cost is one 1080p render-to-texture per backdrop-blend layer per frame,
  and M0 measures it. The opening uses modes 11, 17, 19, 23 and 24.
- **Colour effects** `_STEC`/`_SNX2`/`_SNX3` (compositing 9). The engine applies these on the
  CPU when it builds a texture: 81 LUT sets, luma weights, brightness and contrast LUTs. We
  apply them in a shader instead: one 256 x 3 x 81 LUT texture, plus the exact integer formulas
  emulated with `floor()`. 756 golden cases exist for the test.
- **Masks, crops and STTR** (layers 9): the masked layer is rendered off-screen into a Render
  and combined in `hx.mask`. The feather parameters a16/a17 are [H] and are checked against the
  PS4 at M3.
- **Transitions** (compositing 7.4):
  - At commit, the old frame is reproduced by rendering the previous committed state frozen at
    the commit frame. It is re-rendered each frame during the transition, which keeps it correct
    after a load.
  - Type 0: `rgb = old*w + new*(1-w)` with `w = trunc(255p)/255`.
  - Type 2: a three-texture shader `hx.rule` (old, new, `RULE_nn`) with
    `f = smoothstep(lo, lo+1, r)` and `lo = 1-2p` rounded to 1/128.
  - Type 1: a cut.
  - The shared rule slot keeps its rule across `_FADS(2,ms,0)` and fast-forward, as the PS4
    does (`compose.RuleSlot`).
  - The capture contains only the stage, while the PS4 copies the whole render target. The two
    differ only if the text window is visible during a transition [H].
- Large pictures (up to 11,220 x 7,650 shown) work: Ren'Py's GL2 texture loader splits any
  surface larger than the maximum texture size into a grid with 1-pixel borders [src
  gl2texture.pyx]. `config.image_cache_size_mb` is raised from the default 300 to about 2,048,
  and `predict` on every `hx` statement preloads pictures one commit ahead.

### 4.2 Message renderer (glyph-exact text with furigana)

- **Offline**: `hxconvert` step 3 runs `tsuki.textbox.layout_message` for every message of the
  corpus (42,590 `ZM` plus `MSAD` continuations and choice texts) in each language and stores
  the glyph records: page, glyph rectangle, pen x/y, kind, ruby, icon, and joined-dash or
  redaction-bar substitutions. The layout depends on float32 arithmetic in the engine's
  operation order, which needs numpy, so it stays offline and bit-exact.
- **Runtime**: a MessageView displayable draws, for every glyph, the optional drop shadow
  (+1,+1 units, alpha 0.66), the outline (the same rectangle 512 px lower on the font page) and
  the fill, using the converted `ft_<lang>` pages (65 x 1024 x 1024 per language) as textures
  (textbox 6). On top of that it handles:
  - page stacking: first message at (87,49) units for ja/zc/zt and (30,35) for en, later
    messages below;
  - the window pictures DISP_PARTS 11..13 and the 33..55 bubbles;
  - the click-wait icon;
  - the reveal: `1000/(60*v)` counters per frame, with the last ~3 glyphs fading in;
  - the fades: window 0.06 per frame, `_CLO3` -0.2604, `_CLO4`/`_CLO5` in ms.
- **Interaction**: `hx msg` updates `hx.text`, takes a checkpoint and runs
  `ui.interact(roll_forward=renpy.roll_forward_info())` with the MessageView as the event
  receiver.
  - A click or decide first completes the reveal, then advances.
  - Auto mode uses Ren'Py's `afm_enable` toggle but HuneX timing: config +0x2E ms after the
    reveal and the voice end, or +0x9E ms at the end of a page (adv_state 6).
  - Skip honours Ren'Py's skip state. "Read text only" is the HuneX rule
    `readmax[block] >= sub`, stored in `persistent`.
- **History**: our entry holds the line IDs, voice file, rollback identifier and page flag. The
  backlog screen re-renders it in the **current** language from the same layouts.
- **Language switch** ja/en/zc/zt: re-expand the IDs, select the font pages, ruby on or off
  (off in English), box width 474 or 574, and the origin. No code address changes.
- Rejected alternatives:
  - Ren'Py BMFont registration of the glyph pages. Ren'Py would lay out the text itself, the
    docs describe no outline support, and they discourage image fonts [src text.html].
  - Ren'Py `Text` with a TTF font as the main path. Line breaks and ruby would differ from the
    PS4, and pages could overflow.
- **Fallback T1** (kept for M1 debugging and for UI help lines): Ren'Py `Text` with a bundled
  OFL Japanese font (for example BIZ UDMincho), `{rb}{/rb}{rt}{/rt}`, `gui.language`
  "japanese-strict", and the HuneX box geometry, with overflow logged through
  `config.debug_text_overflow`.

### 4.3 Audio

- Channels registered with `renpy.music.register_channel`: `bgm0` and `bgm1` (the alternating
  slots, so that one can fade while the other starts), `se0..se19`, `voice`, `sys` (HE-VAG system
  sounds) and `movie`. Mixers: music, sfx, voice, plus custom `system` and `movie` mixers for
  the PS4 config sliders.
- The sound model ports `tsuki.misc_audio_runtime_semantics.SoundModel`, which already runs
  under emulation-checked rules, with a struct-based float32 shim in place of numpy. Its outputs
  (start, stop, gain, pan and ramps per stream) drive `renpy.music.play/stop/set_volume/set_pan`
  with fade times. `relative_volume` carries the per-file gain correction.
- Loops: `loops.json` holds the sample-exact loop (`[smpl.start-256, smpl.end-256]`, end
  inclusive). It becomes a `<from 0 loop L>` prefix in seconds. If M0 finds the loop
  restart not sample-accurate (Ren'Py's docs do not promise it), the converter splits each
  looping file into an intro file and a loop file and queues them instead.
- Headroom: float ATRAC9 decodes reach 1.878 of full scale (vse #288, 645 of 1,327 SEs above
  1.0) [doc audio 6.6 d]. The converter scales each such file to a peak of at most 1.0 and records
  the gain g. At play time `relative_volume = min(1, volume*g)`. The PS4 only clips where the
  mix itself exceeds 1.
- Voice: `renpy.voice(file, tag=slot)` with 23 HuneX character slots, mapped from the 54 voice
  groups through the table of sound_runtime 8, and `SetCharacterVolume` per slot. BGM ducking
  during voice (sound_runtime 9) runs as a ramp in the per-frame updater.
- Reconciler: at the start of every interaction, the BGM slots and looping SEs that are actually
  playing are compared with `hx.audio` and corrected. After load or rollback, music and ambient
  loops come back. One-shots are not replayed, which matches the PS4 rewind ("without replaying
  sounds").

### 4.4 Movies

- `hx tomp id` applies the language remap (prologue 4 becomes 4/46/47/106; phase titles +40 en,
  +67 zc, +99 zt; openings +97/+99) and shows `Movie(play=..., loop=False)` on the `hxmovie`
  layer, which sits above the stage and below the UI. `hx tomw` interacts until the movie channel
  stops; decide can skip it.
- The title backgrounds `titlebg1..4` are looping `Movie` displayables in the main-menu screen.
  The choice among them follows G[2] and the title state (system_flow 4.2).
- Format: **VP9 + Opus in WebM**. Ren'Py decodes VP9 with FFmpeg's native decoder but AV1 only
  through libaom [src renpy-build], so VP9 is the cheaper choice to decode. The encode keeps the
  original frame rate, 30 or 29.97 fps, and yuv420p (YUV444 is not accelerated [src movie.html]).

### 4.5 Input

Decide/cancel follow the enter-button option (RR 4.13). The 27 HuneX actions (adv_state 10.2)
map onto Ren'Py keymap and `config.pad_bindings` entries:

- advance: click, Enter, Space, pad decide;
- menu: right click, Escape;
- backlog: wheel up, pad L1;
- rewind: hold PageUp or L2;
- skip: Ctrl or Tab, pad R1;
- auto: A, pad R2;
- hide window: H, pad Y;
- next or previous scene, quick save and quick load.

Mouse coordinates are Ren'Py's virtual 1920 x 1080 space, so the 640 x 448 pointer space is
needed only inside screens transcribed from the eboot.

---

## 5. Asset pipeline

### 5.1 On the fly or one time?

| asset | on the fly inside Ren'Py | verdict |
|---|---|---|
| pictures (35,630 MZPs) | MZX0 in pure Python at about 36 MB/s [doc README]; HEP8 palette expansion per pixel in Python; no numpy | **infeasible**: hundreds of milliseconds to seconds of GIL time per background or sprite, so frames would stall |
| BGM, SE, voice (ATRAC9) | Ren'Py's FFmpeg has no ATRAC9 decoder [src renpy-build] | **infeasible** |
| HE-VAG (19) | a pure-Python decoder would work but is pointless | convert |
| movies (H.264/AAC) | not decodable by Ren'Py at all [src movie.html, renpy-build] | **infeasible** |
| scripts, text, eboot tables | feasible (small) | convert anyway: lint, prediction and the bot need them as data |

A hybrid that decodes pictures in a helper process in the background was considered and
rejected. It would add IPC, a cache and its own failure modes, only to save a 1-2 hour
conversion.

### 5.2 Conversion table

Input sizes come from `samples/inventory` and `samples/ui_misc/{entries,movies}.csv`. The WebP
figures are **[measured]** on 8 decoded samples (20.8 MP) with Pillow on this machine. Some
samples are half-size previews, so treat the ratios as indicative.

| asset | count | input | output | tool | size | time |
|---|---:|---|---|---|---:|---|
| full-size pictures (allpac0/1/1_2/4) | 16,771 entries; about 123 gigapixels shown, about 490 GB as raw RGBA | HEP8 / PAL8 / RGB565 tiles | lossless WebP, cropped to the alpha bounding box with the offset in the manifest; borders removed; flag-0 tiles transparent | tsuki.image (numba) + Pillow WebP in 14 processes | 0.43 B/px (method 4) to 0.56 B/px (method 0): **53-69 GB** | encoders: method 0 at 25 MP/s per thread, about 10-20 min; method 4 at 3.1 MP/s per thread, about 1-1.5 h; plus decode and I/O |
| language bands | 166 records | four stacked bands | 4 cut images | same | included | - |
| thumbnails (allpac2) | 14,745 | PAL8 | WebP | same | about 0.5 GB | minutes |
| UI sets, rules, caution, parts | 136 + 3,699 children, 49 rules, 24 parts | PAL8 / RGB565 | PNG or WebP named `<set>/<lang>/<child>` | same | < 1 GB | minutes |
| font pages | 4 x 65 | PAL8 1024 x 1024 | PNG (glyphs + outlines) | tsuki.font | about 0.1 GB | seconds |
| BGM | 132 (about 6.2 h) | ATRAC9 stereo, smpl loops | Opus 192 kb/s (FLAC if M0 requires) + `loops.json` + `gain.json` | ffmpeg `atrac9` -> float -> libopus | about 0.5 GB (FLAC about 2.5 GB) | minutes |
| SE | 1,327 | ATRAC9 | Opus 128 kb/s + loops + gain | same | about 0.1 GB | minutes |
| voice | 16,464 (about 20 h) | LV + MZX0 + ATRAC9 mono | Opus 64 kb/s mono, `voice/<hex>.opus` | tsuki unwraps, pipe to ffmpeg | about 0.6 GB | about 5-10 min (16 parallel) |
| system sounds (vag) | 19 (9 stereo pairs + dummy) | HE-VAG | Opus or WAV | `tsuki.audio_hevag` | tiny | seconds |
| movies | 134 files, 3,999.6 s total; 52 at 30 fps, 82 at 29.97; 43 with AAC (31 silent) | H.264 1080p | VP9 (CRF about 20, `-row-mt 1`, good / cpu-used 2-4) + Opus; silent tracks dropped | ffmpeg libvpx-vp9 + libopus, 2-3 encodes in parallel | about 4-6 GB [H] | about 30-60 min [H] |
| text | 4 x 43,969 lines | UTF-8 tables | JSON per language | tsuki.text | about 40 MB | seconds |
| layouts | about 45,000 messages x 4 languages | - | glyph records (binary) | tsuki.textbox | about 50-100 MB [H] | about 5-20 min [H] |
| scripts | 549 | inverted MZX0 | `.rpy` + `ops/*.bin` | tsuki.script + transpiler | about 30-60 MB | about 1-2 min |
| eboot tables | about 25 tables | eboot.bin (verified hash) | JSON | tsuki / eboot extractors | < 50 MB | seconds |

**Total: about 60-80 GB and about 1.5-3 hours** on the Ryzen 7 5800X3D. D: has about 870 GB
free. Ren'Py's first launch then compiles the generated scripts, which takes minutes [H, measured
in M0].

- **Compact mode** (optional, not the default): lossy WebP at quality 90 measured 0.06 B/px,
  about 7 GB for all pictures. It is visually near-lossless but not pixel-exact, so the visual
  tests use the lossless set.
- **Conversion order**: boot and title assets, `00_00`..`00_02`, chapter 1, then the rest in
  scene-graph order. The game is playable as soon as the scripts it reaches are converted, and
  the CDS lint marks the scripts that are not ready yet.
- **Step checks** (tsukiweb style):
  - preconditions: eboot hash and file counts (RR 3.3);
  - per-output checks: every lossless WebP decodes back to the tsuki RGBA (SHA-1 of the pixels,
    which is cheap at decode speed);
  - audio: duration equals `fact[0]` samples and the loop points match;
  - movies: the frame count matches and PSNR against the source is at least 40 dB on 10 sampled
    frames.

  A failed step leaves the manifest entry "failed" and is retried; nothing is half-written,
  because every output is written to a temporary file and renamed.

---

## 6. Menus, save/load, backlog and configuration

| feature | Ren'Py gives | we add | source of the layout |
|---|---|---|---|
| boot: save notice, caution, logo, TM_CI (system_flow 3) | `splashscreen` label, `renpy.pause`, Movie | the exact order, durations and skip rules of system_flow 3.4 | decoded |
| title: G[2] states, TITLE_PARTS backgrounds and glow, titlebg movies, PRESS ANY BUTTON, menu | `main_menu` screen, actions Start/Load/Preferences/Quit | the title state machine (system_flow 4.3), New Game rules (scene 260 vs 110, bit 485, G[10]) | decoded (system_flow 4) |
| choice screen | `menu` statement, `choice` screen, autosave on choice, `skip_after_choices` | glyph-rendered items (SELECTTXT is loaded but never drawn), locked items, cursor default | decoded (system_flow 5) |
| in-game menu | `game_menu` framework, `HideInterface`, confirm dialogs | MENU_PARTS art, the entries of engine 8.3 | partial: code constants to transcribe |
| save/load | FileSave/FileLoad/FilePage/FileSlotName, JSON metadata, quick save, FIFO autosave | 100 manual slots (the PS4 shows 4 per page), 39 auto, 1 quick; thumbnail = allpac2 thumbnail of the shown background (catalog index stored with `config.save_json_callbacks`) or a Ren'Py screenshot | partial |
| backlog | `_history_list`, `config.history_callbacks`, `RollbackToIdentifier`, voice replay | our entries (line IDs, voice, identifier), page grouping by F799, re-render in the current language | partial (adv_state 5.4) |
| auto / skip / fast-forward | `Preference("auto-forward")`, Skip, `config.skipping` | HuneX timing and the read-only rule | decoded (adv_state 6) |
| rewind, next scene, previous scene | rollback, roll-forward | page checkpoints, skip to ATSV, scene checkpoints (3.4) | decoded |
| config (System / Sound / Controls tabs) | screen widgets (bar, slider, imagebutton), mixers, voice tags | every option of adv_state 9.2 stored in `persistent.hx.config`; volumes go to mixers and voice tags; text speed, auto speeds, window opacity, outline and shadow, read colour, language, skip mode, stop skip at choices, auto save options | decoded (config pages 0x66E890) |
| CG gallery, music player, movie gallery, ending list | Gallery, MusicRoom, Replay helpers (optional) | gallery.md records: 458 CG groups / 1,972 entries, 95 music records, 3 movies, 25 endings; unlock bit sets | decoded (gallery) |
| flowchart | `viewport` with draggable scrolling, imagebuttons | 277 columns, 1,286 nodes, 630 edges, thumbnails FC_TH; replay through ARCHIVE (G[8], scene 3000, F024 = 1, gallery 3.2) | decoded (gallery 5) |
| language ja / en / zc / zt | screen language, our preference | text, fonts, bands, movies, UI sheet suffix, `ui_text` | decoded (RR 4.19) |

Weaknesses:

- Only the config, title and choice screens have decoded layouts. The others (menu, save/load,
  backlog, HUD buttons) are code constants in eboot entry functions (RR 4.16). They need either
  the call decoder of the own-engine plan (shared work, route-independent) or matching against
  reference captures.
- Ren'Py's screen language makes rebuilding a screen fast, about hours per screen once the
  layout is known. Discovering the layout is the long tail on every route.
- Ren'Py's `Replay` disables saving, while the PS4 replays ARCHIVE scenes through the normal
  path. We use the ARCHIVE route and keep `Replay` only if it matches.

---

## 7. Free versus built

**Free from Ren'Py 8.5.3** (configured, not written):

- the main loop, window, fullscreen, 16:9 letterboxing, vsync and a 1920 x 1080 virtual screen;
- the GL2 renderer: shaders (`register_shader`, Model), `sampler2D` uniforms from a
  Render/Displayable, per-render `gl_blend_func`, automatic texture tiling, the image cache and
  prediction;
- audio: channels, mixers, fades, pan, relative volume, loop prefixes, voice channel, voice
  tags, per-character volume, voice replay;
- movies: VP9/AV1/Opus WebM, fullscreen or as a sprite, looping;
- save/load with thumbnails and JSON, quick save, FIFO autosave, persistent data, rollback and
  roll-forward, history and rollback-to-line;
- preferences, the skip and auto toggles, keyboard/mouse/gamepad bindings;
- the screen language and actions, confirm dialogs, viewports, Gallery, MusicRoom, Replay;
- lint (`--error-code`), compile, warp, the console and displayable inspector, the test
  framework, Windows packaging (`launcher distribute`);
- self-voicing, if the MessageView exposes `_tts`.

**Reused from our research** (already verified):

- `tsuki.*` decoders for the converter;
- `tsuki.textbox` (offline layouts, bit-exact);
- `tsuki.misc_layer_state_and_motions` and `tsuki.vm_vars` (pure Python, imported at run time);
- `SoundModel` (ported with a float32 shim);
- `compose.py` formulas (ported to GLSL, and the oracle for tests);
- the system_flow, adv_state and gallery tables and rules.

**Built for this route**:

- `hxconvert`: build check, table extraction, picture, audio and movie conversion, the manifest
  and resumable runs.
- The transpiler and IR: segmentation, batches, labels and flow, menu recognition, platform
  branch resolution, no-op removal, CDS lint metadata.
- `hx` runtime:
  - VM state and command handlers for 88 commands;
  - the Stage: projection, motions, tweens, shakes, masks/crops/parents, the backdrop blend,
    STEC, rule and cross-fade shaders, the rule slot;
  - the MessageView: glyph quads, pages, windows, reveal, auto/skip, read tracking, history;
  - choices, the audio model and reconciler, movies and the remap;
  - boot and title flow, ATSV, next and previous scene, page rewind, the config mapping and
    language switching.
- Screens: title, game menu, save/load, config, backlog, choice, dialogs, gallery, music, movie,
  ending list, flowchart; all styled with the allpac3 UI art.
- The test harness (section 9): the reference explorer, bot mode, trace diff, save and rollback
  checks, the oracle renderer and visual diff, performance logs.
- First-run setup: point at `CUSA47999`, show progress, resume after interruption.

---

## 8. Fidelity gaps versus the PS4 original

| gap | cause | severity | mitigation |
|---|---|---|---|
| movies re-encoded (VP9) | Ren'Py has no H.264 decoder | low (one generation at CRF about 20) | high bitrate; PSNR check; no visible change expected |
| audio re-encoded (Opus) | Ren'Py has no ATRAC9 decoder | low (ATRAC9 is lossy already; Opus 64-192 kb/s is transparent) | FLAC option for BGM |
| loop restart may not be sample-exact | Ren'Py's docs do not promise sample-accurate `loop` | medium until tested | M0 test on bgm #33 `[962000, 8257999]`; intro + loop split fallback |
| blend results within about 1/255, not bit-exact | float GLSL on a PC GPU against GCN fp16 export rounding; premultiplied textures | low | the oracle comparison has thresholds; the shader formulas are the verified ones |
| transition capture covers only the stage | we re-render the old state instead of copying the render target | low [H] | the text window is normally closed during transitions |
| render timing | Ren'Py draws at a variable rate; we quantise to 60 Hz frames | low | 60 Hz frame emulation in the displayables |
| rewind, backlog and save semantics | Ren'Py rollback per statement instead of the history ring | low-medium | page checkpoints, identifiers, ATSV checkpoints (3.4) |
| menus and HUD not pixel-identical | layouts are code constants, partly decoded | medium (the visible long tail) | transcribe constants per screen; reference captures |
| text vertical scale | open question (81 px line pitch in the movie vs 78 px from the constants, RR 9.1) | low | calibrate at M1 against the prologue movie frame |
| `@g @z @b @a` styles, outline colour, previous-message dimming | open (RR 9.2) | low | same handling as any route; checked against PS4 captures |
| trophies, vibration, PS4 dialogs | platform | none for gameplay | optional achievements; no rumble |
| prefetcher, LV lip sync | optional or placeholder data | none | not needed |
| Korean | stubs in the data | none (as on the PS4) | - |
| one eboot build only | addresses are build-specific | as in every route | clear refusal message |

---

## 9. Proving the whole game is playable

The evidence is layered. Each layer is automated except the last, and each runs locally because
it needs the user's data. The synthetic unit tests (shaders on procedural textures, VM operand
cases, the transpiler on fabricated scripts) need no game data and can run anywhere.

1. **Static gates** (every convert run and every code change):
   - 549/549 scripts transpile;
   - 0 unknown statement names; the 98-name table covers the corpus and the converter fails
     otherwise;
   - `renpy.sh <project> lint --error-code` passes: every jump target exists, and every CDS lint
     function confirms that each referenced picture (including `_hh_vv` variants, faces and
     bands), voice, SE, BGM, rule, movie, motion ID and text ID in 4 languages is loadable;
   - the converter manifest reports 0 failed steps;
   - the five known script bugs (missing variants such as `IMG5007_02_02`) and the three missing
     sprite bodies are on an allow-list and draw nothing, as on the PS4.
2. **Reference explorer** (`hxref`, Python 3.11 over the IR, using the same tsuki models):
   - It runs every script from New Game with an exhaustive choice search: breadth-first over
     (position, F, G) states, deduplicated by hash, with persistent state carried across
     playthroughs, because the prologue bit, G[10] and the scene bits change later starts.
   - It fails on any out-of-range variable, missing label, unknown scene ID or missing asset.
   - Output: the reachable-statement set, a list of unreachable statements with the reason (debug
     scripts, Switch branches), and a **covering set of choice paths** in which every story
     choice option (206 options in 83 blocks) is taken at least once, every ending and every
     QA/"Teach Me, Miss Ciel" scenario is reached, and every reachable statement is executed.
   - If the state space grows too large, deduplication is limited to the variables that are read
     later (a liveness analysis).
3. **Bot runs in Ren'Py** (`bot.rpy`, developer builds only):
   - The `hx` statements skip their interactions and take choices from the path file. Ren'Py
     still executes the transpiled statements, the batches and the runtime state.
   - Every executed HuneX statement (`script:offset`) and a digest of F, G, `scene_state` and
     `hx.audio` at every message are logged.
   - **Pass = the trace equals the hxref trace for the same path**, with 0 exceptions and 100 % of
     the reference-reachable story statements executed over the whole covering set.
   - Branches start from Ren'Py saves taken at each choice during the main paths, which also
     exercises save/load. A full route of about 150,000 Ren'Py statements should take minutes
     without interactions [H, M0 spike].
4. **Save and rollback round trips**: at sampled points (every choice, plus every 50th page, plus
   every first use of each command family):
   - save, load and continue must give the same trace and digests;
   - roll back k checkpoints and roll forward must give the same digests;
   - quick save and autosave rotation keep 39 autosaves in FIFO order.

   The check compares logical state, not save bytes, because Ren'Py saves are pickles.
5. **Visual sampling**:
   - The bot pauses at about 2,000 checkpoints: one per 50 pages, plus the first occurrence of
     each blend mode, STEC ID, rule, mask/crop pattern, shake type, window type and language. At
     each one it writes a Ren'Py screenshot and the `hx.stage` JSON.
   - Offline, the **oracle renderer** draws the same state with numpy. It is built on
     `compose.py`, the layer projection and the catalog, and it is shared with the other routes.
   - Thresholds: PSNR at least 40 dB per frame and at most 0.1 % of pixels off by more than
     4/255. Failures are triaged as a port bug or an oracle bug.
   - PS4 ground truth: page 1 of `00_02` against the `prologue_en.mp4` / `prologue.mp4` frame at
     25 s (glyph x within 3 px, RR 7.5); the burned-in pages of the phase-title movies; optional
     screenshots from the user's own console at agreed checkpoints.
6. **Audio checks**:
   - every loop point is checked with a rendered 3-loop WAV from the channel against the tsuki
     decode (cross-correlation; the seam at most 1 sample off);
   - gain rules are checked against the `SoundModel` worked examples (sound_runtime 4);
   - the prologue movie's audio sync is within one frame.
7. **Performance**: frame time at every visual checkpoint; p99 under 16.7 ms at 1080p on this
   machine; image prediction misses logged with `--debug-image-cache`; no stall above 50 ms on
   the giant pictures.
8. **Human sign-off** at each milestone: text speed, auto timing, transition feel, menus. A
   description of a screenshot never counts as verification.

Ren'Py's own test framework (`renpy.sh <project> test`, `testcase` blocks, screenshot baselines
with `max_pixel_difference`) needs working OpenGL [src changelog, testcases.html]. It runs here
on the RX 9070 XT and is used for the menu smoke tests: open every screen, save, load, change
every option. The bot covers story coverage, because `testcase` blocks cannot carry ordinary
Ren'Py code.

---

## 10. Milestones and acceptance criteria

| M | content | acceptance | expected time |
|---|---|---|---|
| **M0 spikes** | Ren'Py 8.5.3 SDK; 7 spikes: (1) a backdrop-blend shader via a `sampler2D` Render on 10 layers; (2) the rule-wipe shader; (3) the BGM loop on bgm #33, Opus prefix vs intro + loop; (4) VP9 prologue playback with A/V sync; (5) a synthetic 250,000-statement script: load time and RAM; (6) bot execution without interactions, speed and rollback-log memory; (7) WebP throughput on 2 % of allpac1 plus loading the largest picture | written pass/fail per spike. Kill thresholds: 10 backdrop layers > 8 ms per frame; script load > 30 s or > 2 GB; no seamless loop in any format; bot slower than 1,000 statements/s | 2-3 days |
| **M1 vertical slice** | converter for the slice data (RR 7.3); transpiler for the 37-41 commands of 00_00..00_02 and START_SCRIPT; Stage (STCH/SCH2/STCC/STZ4/STRT/STGS/STBR with modes 11/17/19/23/24/STMA/STCP/STCF); FADS 0/1/2; MessageView with reveal; voice, SE loop, BGM; prologue movie; title stub | RR 7.5 checks 1-9 reproduced in Ren'Py: statement sequence equal to `tsuki.script`; img1685 pixel-identical; text layout equal with glyph x within 3 px against the 25 s movie frame; voice entry 15,620 with message 3; MOON_SE03028 loops and fades in 8 s to 30 %; `_WTTM(4000,1)` = 240 frames | about 1 week |
| **M2 whole-game transpile + explorer** | all 549 scripts; batches; menus; lint gates; hxref explorer; covering path set | lint clean; 0 unknown names; the explorer finishes with a covering set; bot traces equal hxref on every path (all routes, endings, QA) | 1-1.5 weeks (parallel with M3) |
| **M3 all commands + full conversion** | every command family (shakes, masks, crops, parents, STEC/SNX, STTR, SPFC/SPRD, FCSP/FCLN, MSQK, FADE), full asset conversion, oracle renderer | the visual sampling of 9.5 passes on all checkpoints; audio checks of 9.6 pass; performance p99 under 16.7 ms | 1.5-2 weeks (parallel with M2) |
| **M4 systems** | save/load (100 + 39 + 1), page rewind, backlog with voice replay and jump, auto, skip, next and previous scene, read tracking, config storage, language switching, input map | the round trips of 9.4 pass; checklist of RR items 8-11, 20, 22, 23, 27, 36 ticked by a human. **All routes playable start to finish** | about 1 week (end of week 4) |
| **M5 faithful UI** | boot flow, title states, choice screen, game menu, save/load, config, backlog on the game's art; remaining screens transcribed | side-by-side against reference captures accepted by the user; every entry of engine 8.3 reachable | 1.5-2 weeks |
| **M6 extras** | CG gallery, music, movie gallery, ending list, flowchart with replay, unlock rules | unlock behaviour equals gallery.md (tests over constructed persistent states); every scene node replayable | about 1 week |
| **M7 release** | `launcher distribute` Windows build, first-run setup UI, convert-resume, English-locale Windows test, licence notices (Ren'Py MIT, LGPL FFmpeg, OFL font) | clean install on a second Windows account (English locale) from the zip plus the user's folder; a full playthrough by the user | 3-5 days |

Agent workstreams with one owner each (concurrency capped at 4-6 agents; status files per
stream; hooks reject skipped tests and edits to `game/hx_gen/` or the converted assets):

- A: `hxconvert` assets;
- B: transpiler + `hxref`;
- C: Stage and shaders;
- D: MessageView and text;
- E: audio and movies;
- F: screens and UI transcription;
- G: harness (bot, oracle, visual diff).

Easy items go first, then items by similarity.

---

## 11. Install requirements

**User (play):**

- Windows 10 or 11, x64; a GPU with OpenGL 3 or DirectX 11 (Ren'Py's requirement); 8 GB RAM
  minimum (16 GB recommended for the 2 GB image cache).
- The user's own extracted `CUSA47999` folder, whose eboot.bin must match the analysed build
  (SHA-256 `c2843415...b918`, RR 3.3).
- About 60-80 GB free for the converted assets. The original folder is kept and only read.
- The shipped zip contains the Ren'Py runtime, `game/hx`, the screens, a bundled OFL Japanese
  UI font with its licence, and the converter.
- Converter runtime, one of two options:
  - a frozen `hxconvert.exe` (PyInstaller) with numpy, numba and Pillow, plus an LGPL FFmpeg
    build that has the `atrac9` decoder, libopus and libvpx;
  - for developers: Python 3.11 + `numpy pillow numba` and the system FFmpeg. The FFmpeg 9.0.2
    on this machine has all three components.
- No C/C++ compiler, no Visual Studio, no PS4 code, no emulator.

**Developer (agents):** the Ren'Py 8.5.3 SDK (Windows zip 155 MB or 7z.exe 116 MB), the
existing `.venv` with pytest, ffmpeg/ffprobe, git. Optional: a second Windows user with an
English locale for M7.

---

## 12. Realistic time to the full game

Assumptions: 4-6 agents in parallel; the user reviews a few hours per week and plays the result;
conversions run unattended. The file timestamps suggest the agent research phase wrote the
format documents and the 230+ tests within about one day (all dated 2026-10-25). Implementation
streams that follow verified models are therefore expected to take days per subsystem [H]. Integration,
visual sign-off and UI layout discovery are slower, and so is the user's reading time: the game
is tens of hours long.

| target | optimistic | expected | pessimistic |
|---|---|---|---|
| M0 + M1: prologue and first scene in Ren'Py | 1 week | 1.5 weeks | 2.5 weeks |
| all routes playable start to finish with saves, backlog, auto/skip (M4) | 3 weeks | **4 weeks** | 7 weeks |
| faithful menus, gallery, flowchart, Windows build (M7) | 5 weeks | **7-8 weeks** | 12 weeks |

The expected total is a little shorter than the own-engine plan (8 weeks to a complete game,
10-12 with gallery and polish). The difference is the menu framework, saves, rollback,
preferences, input, playback and packaging. The engine core (Stage, text, audio model) takes
about the same time on both routes. The pessimistic case covers M0 forcing workarounds (loops,
blend performance, script size) and slow decoding of the uncharted UI screens.

---

## 13. Risks and kill criteria

| id | risk | likelihood | impact | mitigation |
|---|---|---|---|---|
| R1 | backdrop blends as one render-to-texture per layer are too slow, or the `sampler2D` Render path behaves unexpectedly | low-medium | high | M0 spike 1; group consecutive normal layers; render backdrop modes only where the layer covers; kill threshold 8 ms per frame |
| R2 | 150,000-200,000 generated statements are slow to load or use too much memory | medium | high | M0 spike 5; bigger batches (one statement per yield point is the floor); fallback B (interpreter) for low-traffic scripts |
| R3 | loops not sample-accurate; crossfade or clip artefacts | medium | medium | M0 spike 3; intro + loop split; FLAC; gain table |
| R4 | Ren'Py internals we rely on change (rollback identifiers, history entry creation, ui.interact roll-forward) | medium over time | medium | pin 8.5.3; adapter module; tests on upgrade |
| R5 | rollback memory and time with about 1,000 checkpoints of state | low | medium | value-semantics state (about 7 KB per change); measure in M4 |
| R6 | the UI layouts of uncharted screens take long to recover | medium-high | medium | shared call decoder (route-independent); reference captures; Ren'Py screens are fast to adjust once known |
| R7 | headless or bot execution in Ren'Py is harder than expected (needs a display; log growth) | medium | medium | run the bot with the window visible; periodic checkpoints; hxref covers semantics outside Ren'Py |
| R8 | movie transcode quality, A/V drift, a hitch at the title-loop restart | low-medium | low | CRF about 20; sync test; `group=` for seamless swaps; check the loop restart at M5 |
| R9 | two implementations drift (hxref vs `hx` runtime) | low | medium | both import the same tsuki models; the trace diff catches drift at once |
| R10 | save compatibility breaks while the runtime evolves | medium | low (pre-release) | `HxState.version` + migrations; values-only state |
| R11 | generated data leaks into a build or repository | low | high (legal) | `build.classify` excludes; `.gitignore`; a pre-release lint that scans for game-derived files |
| R12 | Windows code page, locale or path problems (as in the FSN REMASTERED launch) | low | medium | UTF-8 everywhere; English-locale test in M7 |

**Kill criteria** (decided at the end of M0): R1, R2 or R3 fail with no workaround inside
Ren'Py, or the bot cannot reach 1,000 statements/s. Switching then costs only the Ren'Py-specific
code: the CDS, displayables and screens. The converter steps for tables, text and layouts, the
IR, hxref, the oracle and the visual and trace harness move to the own-engine route unchanged.

---

## 14. Summary of the honest trade-off

Ren'Py is the fastest way to a **complete, robust application** around this game: menus,
saves, rollback, history, preferences, packaging and playback. It is not a shortcut for the
**engine**. The HuneX stage, the text and the sound rules have to be implemented inside it, as
on any route. The data has to be converted once (60-80 GB, 1.5-3 hours) because Ren'Py can read
none of the formats. In exchange, the runtime can run our verified Python models directly, and
statement-level saves line up exactly with the HuneX yield points. That makes this route a sound
choice for "playable start to finish soon", provided the M0 spikes pass.

---

## 15. Sources

Ren'Py (primary):

- Release 8.5.3 (15 May 2026), SDK sizes: https://www.renpy.org/latest.html
- Model-based rendering, `register_shader`, `sampler2D` uniforms from a Displayable or Render,
  premultiplied alpha, `gl_blend_func`: https://www.renpy.org/doc/html/model.html
- Creator-defined displayables (render, redraw, pickling restriction on Renders):
  https://www.renpy.org/doc/html/cdd.html
- Creator-defined statements (`register_statement`: parse, execute, predict, lint, warp,
  force_begin_rollback): https://www.renpy.org/doc/html/cds.html
- Save, load and rollback (state captured at statement start, checkpoints hard and soft,
  revertable data): https://www.renpy.org/doc/html/save_load_rollback.html
- History (HistoryEntry fields, `rollback_identifier`, callbacks):
  https://www.renpy.org/doc/html/history.html
- Audio (formats, `<from to loop>` prefixes, `relative_volume`, channel options):
  https://www.renpy.org/doc/html/audio.html
- Voice (voice tags, `SetCharacterVolume`, `VoiceReplay`): https://www.renpy.org/doc/html/voice.html
- Movies (codec list, no H.264/AAC, YUV444 not accelerated, Movie parameters):
  https://www.renpy.org/doc/html/movie.html
- Text (ruby, image fonts discouraged, `gui.language`, overflow logging):
  https://www.renpy.org/doc/html/text.html
- Python (pure-Python packages only, Python 3.12 since 8.4): https://www.renpy.org/doc/html/python.html
- Configuration variables (rollback, autosave and quicksave slots, image cache, callbacks):
  https://www.renpy.org/doc/html/config.html
- Rooms (Gallery, MusicRoom, Replay; saving disabled during replay):
  https://www.renpy.org/doc/html/rooms.html
- CLI (lint `--error-code`, test, distribute, `register_command` with dummy drivers):
  https://www.renpy.org/doc/html/cli.html
- Test cases (statements, screenshot comparison): https://www.renpy.org/doc/html/testcases.html
- Changelog (8.4 script-loading speed-up, `sampler2D` displayables, 8.5 test framework needs
  OpenGL, `config.max_texture_size`): https://www.renpy.org/doc/html/changelog.html
- GL2 texture loader that splits oversized surfaces into a grid:
  https://raw.githubusercontent.com/renpy/renpy/master/renpy/gl2/gl2texture.pyx
- Ren'Py's FFmpeg build (enabled decoders: vp8, vp9, libaom_av1, opus, vorbis, flac...; no h264,
  aac, atrac9): https://raw.githubusercontent.com/renpy/renpy-build/master/tasks/ffmpeg.py

Precedents (from the research summary of this workflow):

- Hintay VitaScenariosTool (HuneX opcodes to KAG):
  https://raw.githubusercontent.com/Hintay/VitaScenariosTool/master/macroslist.py
- tsukiweb (offline asset builder, small runtime): https://github.com/requinDr/tsukiweb-public
- Mahoyo Steam Android port (VM with host dispatch, serialisable state):
  https://github.com/david419kr/mahoyo-steam-android-port
- impacto (multi-game engine, UI long tail): https://github.com/CommitteeOfZero/impacto/issues/1
- Umineko Project (8 years on a forked general engine): https://umineko-project.org/en/project-info
- Zeus port (harness first, deterministic tests): https://carterappleton.com/blog/porting-zeus-to-the-browser/
- FSN REMASTERED locale bug thread: https://steamcommunity.com/app/2396980/discussions/0/4513254889540907719

Our own documents: `docs/RUNTIME_REQUIREMENTS.md`, `docs/FORMATS.md`, `docs/formats/{script,
layers, compositing, textbox, sound_runtime, adv_state, system_flow, gallery, vm_variables,
audio, archives_ui_movies}.md`, `docs/research/engine_lineage.md`,
`docs/design/strategy_own_engine.md`.
