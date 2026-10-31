# Strategy: convert TSUKIHIME (PS4) to KiriKiri Z / KAG3

Route under study: a converter turns the remake's HuneX scripts into KAG3 scenario files, the
assets into formats KiriKiri Z (krkrz) loads, and krkrz's own blend modes, universal transition,
bookmarks, history, auto/skip and sound/movie services run the game. Menus are built in KAG/TJS.

Date: 2026-10-25. Inputs: `README.md`, `docs/FORMATS.md`, `docs/RUNTIME_REQUIREMENTS.md`,
`docs/formats/*.md`, `docs/research/engine_lineage.md`, the precedent and engine surveys in the
task, and the primary sources in section 15 (krkrz, KAG3 and Wamsoft sources and docs read for
this document). Numbers marked **[measured]** were measured for this document on the research
copy, in memory only (no files written).

---

## 0. Verdict

**The route works, but it buys less than the KiriKiri lineage suggests.** Use it only if we
value the KiriKiri ecosystem (KAG3's save/history/auto/skip system, an engine that the Wamsoft
fork keeps actively maintained) more than a runtime written in the language our agents test best.

- **What the lineage really gives us.** HuneX's compositor copied KiriKiri. krkrz has all 25
  non-normal blend modes the remake uses, including the two Photoshop 5 variants. Its
  soft-light, colour-dodge and burn tables use the same formulas as HuneX's GCN shaders (checked
  in `visual/gl/blend_function.cpp`, section 4.4). Its universal transition has the same
  rule-image design.
- **What it does not give us.** TYPE-MOON's KAG macro library for the remake was never
  released. Unlike the Vita *Fate* scripts (`KIMG`->`@image`, `KLD0`->`@ld` in
  VitaScenariosTool), the remake's scripts are **not** renamed KAG macros. They are HuneX
  primitives:
  - `_STCH` with up to 20 positional arguments on 40 numbered layers;
  - 24,333 `_STMA` calls into 24,192 keyframe motions compiled into `eboot.bin`;
  - 12,900 `_STEC` colour-LUT ids.

  HuneX compiled the macros away, so the conversion cannot get them back. Every primitive has
  to be re-implemented in TJS: the layer model, motions, compositing order, the text box and
  the whole UI.
- **Net effect.** In practice this is "Route B written in TJS on top of KiriKiri services". We
  save the generic VN plumbing. We still build the HuneX-specific core. On top of that we take
  on a C++ toolchain (VS2022 + vcpkg for the maintained fork), a CPU-compositing performance
  risk at 1080p, and a less familiar language.
- **Hedge.** The converter front end, the asset pipeline, the reference VM explorer and the
  test traces (sections 3, 6 and 10) do not depend on the route. If this route fails its M1
  gate (section 11), they carry over unchanged.

Realistic time to a game that is fully playable from start to finish (all routes, ja/en, voices,
movies, save/load, backlog, auto/skip, config): **about 6-9 weeks of calendar time with 4-6
agents in parallel**. Gallery, flowchart and the fidelity polish take **8-12 weeks** in total
(section 13).

---

## 1. Engine choice

| | **Wamsoft krkrz v2.6.5** (recommended) | official krkrz 1.4.0 (fallback for M0/M1 only) |
|---|---|---|
| status | tags v2.2.0..v2.6.5, pushed 2026-10-20/22 | last release 2017-12-24 (`krkrz_20171225r2.7z`, 4.3 MB); repo pushed 2024-03 |
| binaries | **none**: build from source with VS 2022 17.14 + vcpkg + CMake/Ninja (`cmake --preset=x64-windows`) | prebuilt; the release is laid out as `win32`, and x64 exists only as a project configuration (64-bit binary not confirmed) |
| bitness | x64 (x86 preset also exists) | most likely x86: 2-4 GB address space against pictures up to 343 MB as RGBA |
| audio | miniaudio; WAV, Ogg Vorbis, Opus built in; `.sli` loop files | DirectSound era; Vorbis/Opus through the `wuvorbis` / `wuopus` plugins |
| movies | MP4/WMV through Media Foundation (IMFMediaEngine, or SourceReader to BGRA); WebM VP8/VP9 with alpha; MPEG-1 (pl_mpeg); modes overlay / **layer** (with segment loops) / mixer | `krmovie`: DirectShow WMV/MPEG-1 in overlay/layer/mixer; MP4 through Media Foundation **only in the EVR overlay mode** (`vomMFEVR`, `MFPlayer.cpp`), so nothing can be drawn over an MP4 |
| GPU path | OpenGL ES via ANGLE: `OGLDrawDevice`, `Canvas`, `Texture`, `Offscreen`, `ShaderProgram` (GLSL) | none (CPU tvpgl + D3D9 present) |
| agent tooling | `-repl`, `-replfile=<dir>` (JSON responses, for external agents), `-replweb`; `Agent` class (input injection, `captureScreen`) in the SDL3 build; **bundled Claude Code skills** (`krkrz`, `tjs2`, `krkrz-repl`, ...) | none; GUI and console only |
| docs | Japanese + English (wamsoft.github.io/krkrz_dev) | Japanese |
| licence | engine BSD-style plus per-component licences (bundled licence list on the docs site) | BSD-style + Apache 2.0 + zlib (GitHub shows NOASSERTION) |

**Decision:** build the Wamsoft fork for x64. Then the MP4 movies, including the 120 s title
loops that the title menu draws over, play in place in layer mode. Opus/Vorbis need no
plugins, and agents can drive the engine through `-replfile`. Use the official 1.4.0 binary only
if the build is blocked. It is good enough for M1 (one scene) but not for the whole game: it is
32-bit, and MP4 can only play as an overlay.

The KAG3 system scripts come from `krkrz/kag3` (also bundled as a submodule of `krkrz_dev`).

---

## 2. Architecture

```
user's CUSA47999 (read-only)
   |  converter (Python 3.11, tools/tsuki + new tools/krkr/*)       -- one time, on the user's PC
   |    verify eboot SHA-256 (RUNTIME_REQUIREMENTS 3.3) -> extract eboot tables
   |    scripts  -> scenario/<lang>/sNNNN.ks  (549 x 4 languages)   + labels.json, manifest.json
   |    pictures -> image/**.png (body/face kept separate), rule/RULE_nn.png, ui/**.png
   |    audio    -> sound/{bgm,se,voice}/*.ogg + *.sli loop files, sound/sys/*.wav (HE-VAG)
   |    movies   -> nothing (played in place from <game>/image/movie via an auto path)
   |    tables   -> hxdata/*.bin|*.tjs (motions, catalog/name map, movie table, UI tables,
   |                ui_text[5][1499], config defaults, voice groups, gallery/flowchart lists)
   v
D:/TsukihimeKR/data/        (local only, never shared; ~55 GB)
D:/TsukihimeKR/krkrz.exe    (Wamsoft x64 build) + plugin64/ (+ ANGLE DLLs if the GPU path is used)
           system/          KAG3 (unmodified where possible) + Config.tjs (1920x1080; KAG character
                            layers unused, KAG BGM/SE objects unused: hx owns all sound buffers)
           hx/              our TJS runtime "hx" (a KAGPlugin, section 5)
           native/          optional tiny C++ plugins: hxlut (STEC), hxrule (HuneX rule wipe)
```

Runtime frame:

```
KAG3 conductor reads sNNNN.ks -> built-in tags (text, [l]/[p], [jump], [trans], [wait] ...)
                              -> hx macros ([hx_stch], [hx_stma], [hx_sepl] ...) -> hx.tjs
hx state (F/G/Z arrays, 40 HuneX layers: pending scene_state + drawn records, sound, window)
   _FADS -> hx commits pending -> renders the back page's HxCanvas -> KAG [trans] fore<->back
per frame (System continuous handler, 60 Hz logic step): motions, tweens, shakes, sound fades,
   dirty HxCanvas -> redraw with native Layer.operateAffine/operateRect(mode = omPs*/om*)
KAG message layer on top (game font substitute, ruby), history layer, UI screens (TJS layers)
KAG bookmarks at savable page labels; hx.onStore/onRestore serialises all hx state
```

The central design choice is **one composited scene image per KAG page (HxCanvas)**, not 40
KAG character layers. HuneX draws its layers in z order into one render target, and each blend
shader reads the backdrop (`compositing.md` 1-2). KiriKiri's
`Layer.operateAffine(src, ..., mode, opacity)` does the same thing natively: it blends a source
into a destination image with any `om*` / `omPs*` mode. So a TJS loop over the 40 HuneX layers,
sorted by z, can reproduce HuneX's order, anchors, scale, zoom, rotation, crop and blend exactly.
KAG3's character layers could not, because they have no anchor-relative zoom or rotation, no
parent chains and no per-layer z values in the 2..9114 range. KAG's fore/back pages and
`[trans]` then match HuneX's "changes become visible at the next `_FADS`" commit model directly.

---

## 3. Script translation

### 3.1 Approach

A **static transpiler** in Python reads each script with `tsuki.script` (the tokenizer, the
inline reader and the object-expression parser already match the engine on the 35,474-expression
corpus). It writes one `.ks` file per script and language:

- **One KAG tag or hx macro per HuneX statement.** The converter **fails** on any statement name
  it has no rule for. That is the lesson from VitaScenariosTool, which silently commented out
  every unmapped macro (`WKST`, `MSAD`, `TRPY`, movies), which left gaps. All 98 statement names
  in use get a rule (1,083,168 statements in the corpus [measured from
  `samples/script/command_stats.json`]).
- **Arguments are evaluated at conversion time** wherever they are constant (almost everywhere).
  The converter emits integers; `242.25` becomes `242` as in the engine. Variable operands become
  TJS expressions on `f.F[...]` / `sf.G[...]`. The engine's quirks, `C<n>` = `F[F823+n]` as a
  write target and `_D<n>`, are applied in the converter where static, and in `hx.var()` at run
  time otherwise (`vm_variables.md`).
- **Platform branches are resolved for the PS4.** `_IFP4` is a no-op and `_IFNX(,L)` always jumps.
  The converter emits only the PS4 path and drops the Switch code. `_SNX4`, `_FANX`, `_DMMY`,
  `_NOP_`, `_STQK` and `_IFP4` become comments.
- **Text is inlined per language.** `scenario/ja/` and `scenario/en/` (also `zc/` and `zt/`)
  hold the same labels in the same files. The language is chosen by the storage search path, and
  switching language jumps back to the current page label. `$NNNNNN` is expanded at conversion,
  with the engine rules (`text.md`; `#` line splits kept as they are).
- **Labels.** Every `_ZZ`/`_ZY` label (the jump targets of the label tables in `allscr.mrg`
  section 1) becomes `*LBL_n`. Every `_PGST(p)` becomes a **savable label**
  `*p<script>_<p>|<chapter title>`. This is where KAG3 bookmarks save, and it is also where the
  PS4 resumes a slot load: the page-start record, then the page's first message statement is run
  again (`adv_state.md` 7.3, "History_Restore, not scan mode").
- **Runs of bookkeeping are folded.** For example, the page-end block of 20 or more
  `_WKST(F8xx,eq,0)` and the message prologue
  `_WKAD(F823,1);_WKST(C847,eq,0);_WKST(C859,eq,0);_WNTY(x)` become one macro each. `WKST`
  alone is 487,857 statements.

### 3.2 Command families

**Flow, variables, scenes** (`script.md` 4.1, `vm_variables.md`):

| HuneX | KAG / hx | notes |
|---|---|---|
| `_JUMP(scene[,label])` | `[jump storage="sNNNN.ks" target=*label]` | scene id -> script index via the 549-entry table (unknown -> START_SCRIPT) |
| `_GOTO(label)` | `[jump target=*label]` | |
| `_IF__(var,op,val,label)` | `[jump target=*label cond="hx.v('F123')==1"]` | ops eq/ne/ge/lt/gt/le |
| `_GOTS(sel,L0,L1..)` | `[jump target=*L0 cond="f.D[0]==0"]...` | computed goto unrolled |
| `_WKST/_WKAD/_WKOR` | `[eval exp="..."]`, or folded macros | u16 arrays `f.F[1200]`, `f.Z[1400]`, G: `sf.G[0..499]` (system data), rest per vm_variables 6.3 |
| `_RDST/_RTST/_RDAS/_RDED` | `[hx_rdst n=..]` ... | scene bit sets (reached / visited / completed) in sf / f |
| `_ATSV([n])` | `[hx_atsv n=..]` | history snapshot + auto save (slots 100..138 FIFO, `adv_state.md` 11) |
| `_END_/_RTM_/_RTMN/_RTM2` | `[hx_end]`, `[hx_rtm keepbgm=1]` | script-end cleanup: stop the 20 SE channels, stop the BGM unless the keep flag is set, free the rule slot |
| `_IFSC/_IFUR/_TRPY/_XSTP/_XRSM/_WTSV` | hx functions | replay mode, QA completion, achievement log, system state; `_WTSV` = no-op |

**Text and voice** (`text.md` 5-6, `textbox.md`, `sound_runtime.md` 7):

| HuneX | KAG / hx |
|---|---|
| `_VPLY(name,hex)` / `_VPL2` | `[hx_vo id=0x3d04]`: voice pending; started by the next message (the PS4's gate); `_VCWT` = `[hx_vcwt]` (wait for the voice) |
| `_ZM<k><bbb><ss>($N)` | `[hx_zm blk=01d04]` (marks the previous marker read: `readmax`), then the text, then `[l][r]` (`WNTY(0)`) or `[p]` (`WNTY(1)`, last message of the page); extra `[r]` for `C847` blank lines |
| markup `^` | `[r]` |
| `<base\|reading>` (ja/zc/zt only) | `[ruby text="reading"][ch text="base"]`: KAG3's `[ruby]` covers only the next character, but `[ch text=...]` is drawn as one unit, so the reading centres over the whole base (checked in `MessageLayer.processCh`) |
| `@k@e`, `@n`, `@cN`, `@x`, `@r`, `[icon]` | `[l]`+end of message, no wait, `[font color=...]`, hx flags, inline `[graph]` pictures |
| `_MSAD($N)` | text appended to the current message |
| `_CLO2/_CLO3/_CLO4(ms)/_CLO5(ms)` | `[hx_clo ...]`: clear / close / fade the window (opacity tween on the message layer) |
| `_FCSP/_FCLN`, `_MSQK` | hx text-window modes, window shake |
| history | each message is recorded inside `[hact exp="hx.replayVoice(0x3d04)"] ... [endhact]`, KAG3's built-in clickable history action, the usual KiriKiri way to replay voices from the backlog |

**Pictures and layers** (`script.md` 4.3, `layers.md`): all go to hx, never to KAG character layers.

| HuneX | hx |
|---|---|
| `_STCH`, `_SCH2` (with arg 18 source copy), `_STCC` | 40-entry pending `scene_state`; picture name + `_hh_vv` suffix -> name map -> catalog record -> body PNG + face PNG; anchor placement; script units x4/3 |
| `_STZ4`, `_STRT`, `_STRR`, `_STPV`, `_SOXY`, `_STCL`, `_STGS` | zoom, rotation, pivot, offset and move tweens with Penner easing (`layers.md` 8.5) |
| `_STMA`, `_SGMA`, `_SPMA` | motion engine: exact port of `tsuki.misc_layer_state_and_motions` (27 fields, HOLD/UNSET sentinels, the cubic-spline quirk, the 2,027 looping motions) |
| `_SBAS`, `_SACL`, STCH a11 mask, a12..a15 crop | parent chains, mask layers drawn through an offscreen image (HuneX's two-pass "masked layer", effects 27..51), crop source rectangle |
| `_SQK2/_SQS2`, `_SHK2/_SHS2`, `_SSW2/_SSS2`, `_SHOK`, `_SHAF` | quake / shake / swing offsets applied in the HxCanvas draw |
| `_STCG` | "picture seen" bit only |
| `_SPFC`, `_SPRD/_SPRC`, `_STNS`, `_STRA`, `_STTR`, `_STCP`, `_STCF` | per `layers.md` 4 (the [H] parts get stand-ins and are logged) |

**Transitions, blends, colour effects** (`compositing.md`):

| HuneX | krkrz |
|---|---|
| `_FADS(1,0,0)` cut | hx commits pending, redraws the back HxCanvas, fore/back exchange without a transition |
| `_FADS(0,ms,0)` cross-fade (12,240 uses) | `[trans method=crossfade time=ms][wt]` (HuneX's weight is `trunc(255p)/255`, so effectively identical) |
| `_FADS(2,ms,n)` rule wipe (947 uses) | default `[trans method=universal rule="RULE_nn" vague=255 time=ms][wt]`; exact: native `hxrule` transition plugin computing `f = smoothstep(lo, lo+1, r)`, `lo = 1-2p` (section 9); rule-slot reuse (`_FADS(2,ms,0)`, `_STTR`) is tracked in hx |
| `_STBR(layer,mode[,,1])` (14,940 uses) | blend byte -> `operateAffine` mode: 1 `omAdditive`, 2 `omAddAlpha`, 4 `omDarken`, 5 `omDodge`, 6 `omLighten`, 7 `omMultiplicative`, 8 `omOpaque`, 9 `omScreen`, 10 `omSubtractive`, 11..25 `omPsAdditive` ... `omPsSubtractive` incl. `omPsColorDodge5` (17, the most used) and `omPsDifference5` (15); the flag (4th argument) is honoured per `compositing.md` 5.2 |
| `_STEC(layer,id[,rgb])`, `_SNX2`, `_SNX3` | CPU recolour when the texture is built: native `hxlut` plugin = bit-exact port of `stec_apply_bgra` (81 LUT sets + brightness/contrast); without a compiler, pre-bake the (picture, id, brightness, contrast) combinations the reference VM finds |
| `_FADE(40/41)` screen effect (61 uses) | hx stand-in (the effect drawing was not traced by the research either) |

**Sound** (`sound_runtime.md`): hx owns 2 BGM `WaveSoundBuffer`s (alternating slots), 20 SE
buffers, 1 voice buffer and the system-SE buffer. KAG3's own BGM/SE objects are not used, so
the store/restore logic is not duplicated.

| HuneX | hx |
|---|---|
| `_MPLY(n,,tags)` | same track ignored, negative stops; gain `trunc(config*script*0.01)`; fade-in through `WaveSoundBuffer.fade()`; the old slot fades out |
| `_MVOL`, `_MFAD`, `_MSTP` | `fade()` with the documented curves |
| `_SEPL(ch,loop,name,,tags)` | `buffer.flags[0] = loop`, and the `.sli` link carries `Condition` on flag 0, so one file serves both looped and one-shot use; the `` `068 `` delayed queue and the `` `069 `` start offset are ported |
| `_SEFD`, `_SEVL`, `_SESP` | fades and stop |
| voice gate, stop on advance, BGM ducking, 23 per-character volumes | port of `SoundModel` (`tsuki.misc_audio_runtime_semantics`), already checked against the engine |

**Movies** (`archives_ui_movies.md` 9, `script.md` 6.5): `_TOMP(id)` -> `[hx_tomp id=4]`, which
remaps the id to the language variant (4 -> 4/46/47/106 ...) and plays the MP4 **from the user's
movie folder** in layer mode (`[video mode=layer]`, `[openvideo]`, `[playvideo]`). `_TOMW` ->
`[wv canskip=true]`. The title loops (ids 0..3) use layer mode with a segment loop, so the title
UI can be drawn over them.

**Choices** (`system_flow.md` 5, `vm_variables.md` 4.5-4.6): `_SELR(k,$N[,locked])` ->
`[hx_selr k=.. text=".." locked=..]`. Then `_SELD/_SELA/_SELS` -> `[hx_seld var=D000 default=..]`
`[s]`. The hx choice screen draws the 640 x 344.7 choice canvas with the game's SELECT parts,
stores k in `f.D[0]` and jumps to the next label. Locked choices are skipped by the cursor.

### 3.3 Example (page 1 of `00_02`; game text not reproduced)

```
*p0003_1|
[hx_pgst n=1][wait time=400 canskip=true]
[hx_msg c847=0 c859=0]                                  ; WKAD F823 + WKST C847/C859
[hx_vo id=0x3d04]                                       ; _VPLY(B90_00_01_0001_0,03d04)
[hx_zm blk=01d04][hact exp="hx.replayVoice(0x3d04)"]<line 3 of the current language>[endhact][l][r]
...
[hx_vo id=0x3d08][hx_zm blk=01d08][hact exp="hx.replayVoice(0x3d08)"]<line 7>[endhact][p]
[hx_pageend]                                             ; folded >=20 x _WKST(F8xx,eq,0)
```

---

## 4. Rendering design (hx compositor)

### 4.1 Placement and projection

Use exactly `layers.md` 7: catalog anchor -> position, zoom/scale about the anchor, rotation in
1/100 degree, parent chains. Script units x4/3 -> canvas 640 x 362.667 -> frame x3 / x2.978.
Since at scale 1000 one picture pixel is one frame pixel, the 1920 x 1080 HxCanvas is drawn in
frame pixels.

### 4.2 Draw

For each layer in z order: `operateAffine(src, ..., mode, opacity)` from the body image, then
the face image (catalog `name1`), into HxCanvas. Rotation-free and zoom-free layers use
`operateRect` (much cheaper). Masked and cropped layers go through an offscreen image first.
Pictures are straight alpha, as in HuneX (`compositing.md` 4.2). KiriKiri layers are
straight-alpha too, so no premultiplication step is needed.

### 4.3 Dirty-driven redraw

A static page costs nothing. Animated pages (motions, tweens, shakes) redraw the whole HxCanvas
each 60 Hz step.

### 4.4 Formula parity with HuneX (evidence)

| mode | KiriKiri Z source | HuneX GCN (`compositing.md` 2.3) |
|---|---|---|
| soft light | `pow(d, 128/s)` for s >= 128, else `pow(d, (1-s/255)/0.5)` (8-bit table, `blend_function.cpp`) | `2^(log2 d * e)`, `e = s >= 0.5 ? 1/(2s) : 2(1-s)` (same function) |
| colour dodge | `(255-s) <= d ? 255 : d*255/(255-s)` | `min(1, d/(1-s))` |
| colour burn | `s <= 255-d ? 0 : 255 - (255-d)*255/s` | `max(0, (d-1)/s + 1)` |
| dodge5 / diff5 | "Photoshop 5.x compatible" functors (`blend_functor_c.h`) | source pre-multiplied by alpha*a, no final alpha mix |
| universal rule | linear ramp of width `vague` over 8-bit rule values (`TVPInitUnivTransBlendTable_c`) | `smoothstep(lo, lo+1, r)`, a full-range soft band |

The residual differences are 8-bit tables against float32/fp16, HuneX's `d < 3/255` black guard
in the two dodge modes, the "nexl" alpha family (output alpha 1) and the rule-wipe curve. Each
is covered by a synthetic per-mode test against `tsuki.compose` (section 10).

### 4.5 Performance gate

CPU compositing is the main technical risk of this route. Measure it in M1 on `00_02` (a
2070 x 1230 backdrop zooming from 1200 to 912 per mille over 70 s, plus a vignette in
`psscreen`/`pssoftlight` and up to 20 set-up layers). Pass: >= 60 fps, or >= 45 fps with no
visible judder. If it fails, try in order:

1. cache layers that do not animate into one pre-composited "under" image;
2. use the engine's multi-threaded drawing, if the build has it;
3. move HxCanvas to the Wamsoft GLES `Canvas` + `ShaderProgram` path and port the 35
   pixel-shader formulas from `tsuki.compose` to GLSL. This needs backdrop reads through
   `Offscreen` ping-pong, which the docs do not confirm. Prototype it, max 3 days.
4. If none of these works, apply the kill criterion (section 14).

---

## 5. The hx runtime library (TJS)

`hx` is registered as a `KAGPlugin`. KAG3 calls its `onStore`, `onRestore`, `onCopyLayer`,
`onExchangeForeBack` and `onSaveSystemVariables` hooks (`system/Plugin.tjs`), so all hx state
travels in KAG bookmarks.

| module | contents | port of |
|---|---|---|
| `hx/vars.tjs` | F/Z/G arrays, `C<n>`/`_D<n>`, range checks, scene bit sets, picture-seen bits, `readmax` | `tsuki.vm_vars`, `vm_variables.md` |
| `hx/data.tjs` | loads the converted tables (catalog/name map 14,744, motions 24,192 / 71,905 keys, movie table, UI tables, ui_text, config defaults, voice groups) | `script_tables`, `image_catalog`, `catalog_eboot` |
| `hx/layer.tjs` | 40 HuneX layers: pending/committed/drawn records, commands of `layers.md` 4 | `misc_layer_state_and_motions` |
| `hx/motion.tjs` | motions, tweens, easing, spline quirk, loops | `layers.md` 8 |
| `hx/canvas.tjs` | HxCanvas draw (section 4), masks, crop, shakes, STEC calls | `compose.py`, `layers.md` 7, 9 |
| `hx/trans.tjs` | `_FADS` mapping, rule slot | `compositing.md` 7 |
| `hx/sound.tjs` | BGM/SE/voice/system SE, tags, fades, ducking, gates, config volumes | `SoundModel` |
| `hx/text.tjs` | message page model, window pictures (DISP_PARTS 11..13, bubbles), reveal speed/fade, text-window modes, read colour | `textbox.md` |
| `hx/choice.tjs` | choice screen | `system_flow.md` 5 |
| `hx/movie.tjs` | `_TOMP/_TOMW`, language remap, title loops | `aum` 9 |
| `hx/history.tjs` | rewind ring, previous/next scene, backlog jump | `adv_state.md` 5 |
| `ui/*.tjs`, `ui/*.ks` | boot, title, system menu, save/load, backlog skin, config, dialogs, gallery, flowchart, music room, extras | `system_flow.md`, `adv_state.md` 9, `gallery.md`, `aum` 7 |
| `hx/test.tjs` | test mode (zero waits, scripted choices, trace log, state dumps, scene capture, exit codes) | section 10 |

---

## 6. Asset pipeline (one-time conversion on the user's PC)

On-the-fly reading of the HuneX archives would need C++ storage-media and image-loader plugins:
HED/NAM/MZX/MZP/HEP and ATRAC9 decoders in native code. **Choose a one-time conversion with the
already-tested Python readers.** It is simpler, every step can be checked, and krkrz then only
loads standard files. Each step writes a manifest with counts and SHA-256 sums, checks its
preconditions and verifies its results, as in tsukiweb's 7-step builder.

| step | input | output | count | size | time (8C/16T, NVMe) |
|---|---|---|---:|---:|---|
| 0 verify | `eboot.bin`, archive counts | build id, abort on mismatch | - | - | seconds |
| 1 tables | eboot data/text tables | `hxdata/*` | ~20 tables | ~50 MB | < 1 min |
| 2 pictures | allpac0, 1, 1_2, 4 (+ allpac3 rules, caution, parts) | PNG (zlib 6), straight RGBA; body and face separate; 166 language-band pictures split per language; flag-0 tiles transparent | ~16,800 pictures + ~3,700 UI children | **~52 GB** [measured: PNG = 1.24-1.42x the MZP entry size, 10-15% of raw RGBA, on 32 size-stratified pictures; raw RGBA would be ~450 GB] | ~2.1 CPU-hours [measured] -> **~15-30 min** with 14 worker processes |
| 3 UI | allpac3 nested UI sets (`*_PARTS[_lang]`), ui_text | PNG atlases + layout tables | 71 sets | < 1 GB | minutes |
| 4 audio | bgm 536 MB, vse 138 MB, voice 1.32 GB (ATRAC9 RIFF) | Ogg Vorbis (BGM/SE q5), voice Ogg Vorbis q3 or Opus 64k; trim to `decoded[256:256+fact[0]]`; `.sli` with `Link{From=end+1;To=start;Smooth=False;Condition=ne;RefValue=0;CondVar=0}` | 132 + 1,327 + 16,464 | ~1-1.5 GB | ~10-30 min (ffmpeg `atrac9` decoder, piped, parallel) |
| 5 system SE | vag.mrg (HE-VAG) | WAV via `tsuki.audio_hevag` (FFmpeg's `adpcm_psx` decodes them wrongly) | 19 | 2 MB | seconds |
| 6 scripts | allscr.mrg + script_text.mrg | `scenario/{ja,en,zc,zt}/*.ks` + `labels.json` | 549 x 4 | ~50-100 MB per language (estimate) | < 5 min |
| 7 movies | `image/movie/*.mp4` | none: an auto path to the user's folder (case-insensitive lookup) | 134 | 0 (4.75 GB stay in place) | - |
| 8 audit | manifest | every asset referenced by any `.ks` exists; every converted file is referenced or listed as debug-only | - | - | < 1 min |

**Total: about 55 GB and 0.5-1.5 hours, once.** Allpac2 (14,745 save thumbnails, 1/12.8 scale)
is skipped: KAG3 makes its own save thumbnails (`saveThumbnail`), and the gallery uses the
CG_TH/FC_TH thumbnails in GALLERY_PARTS. Pre-baked STEC variants (only without the `hxlut`
plugin) add a few GB; the reference VM counts the combinations first.

Plan B (official 1.4.0 binary): also transcode the 4 title-loop movies plus their language
variants to MPEG-1 or WMV for layer mode, because its MP4 path is overlay-only.

---

## 7. Menus, save, backlog, config

| feature | free in KAG3 / krkrz | we build |
|---|---|---|
| save/load | bookmarks at the last savable label; `saveBookMark(num)` / `loadBookMark`; screenshot thumbnails; `[record]`; `[tempsave]`; `sf` system variables; storage of `f` at label pass | 139 slots (0..99 manual, 100..138 auto FIFO) + quick save; date stamps; the save/load screens in the game's art; hx state via `onStore/onRestore`; the "Continue" entry; `F1118` rules |
| resume semantics | restart at the savable label with the stored state | labels sit at `_PGST`, which **matches** the PS4's page-start resume |
| backlog | `HistoryLayer` (ring, scrolling, per-page mode); `[hact]` click actions for voice replay | the skin with game art; backlog **jump** (`adv_state.md` 5.5) via hx history records |
| rewind / previous / next scene | `[goback]` (back to the previous label) | the per-message 800-record rewind ring, previous/next scene (actions 20/23/24) in hx |
| auto mode | `autoMode`, page/line waits | HuneX countdown: config +0x2E / +0x9E ms, waits for the voice |
| skip | `skipMode`, read check per label (`getCurrentRead`) | read-only skip at message level via `readmax`; stop at choices; hold-to-fast-forward |
| config | `sf`/`scflags` persistence, `[bgmopt]`/`[seopt]` global volumes | every option of `engine.md` 8.4 / `adv_state.md` 9.2: 5 volume classes, 23 per-character voice entries, text speed (unread/read), auto speed, window opacity, outline/shadow, read-text colour, window design, language, skip rules, auto quick save, vibration -> the config screen layout from CONF_PARTS |
| input | keyboard, mouse, wheel; gamepad (Wamsoft gamepad support / plugin) | decide/cancel abstraction with the enter-button setting; the 27 actions of `adv_state.md` 10 |
| title, boot | KAG scenario flow | caution -> logo -> title state machine (`G[2]` selects the titlebg) -> New Game rule (scene 260 vs 110, bit 485) |
| gallery, flowchart, music, extras | - | per `gallery.md` (P2) |

---

## 8. Reuse versus build

**Free (exists and is tested elsewhere):**

- **Engine services.** A Windows application shell with fullscreen scaling, input, gamepad,
  PNG/JPEG/TLG decoding and 32-bit layer bitmaps. Native blend operations, including all 25
  non-normal HuneX modes. Affine and stretch drawing, crossfade and universal transitions,
  `[quake]`.
- **Sound.** Ogg Vorbis/Opus/WAV decoding with **sample-accurate `.sli` loops** (conditional
  links on buffer flags), volume/pan and native fades.
- **Movies.** H.264/AAC MP4 through Media Foundation in layer mode with segment loops; WebM;
  MPEG-1.
- **Text.** FreeType/GDI text with `Font.addFont` for a bundled font. KAG3 message layers with
  kinsoku tables, ruby, `[link]`/`[button]` and `[graph]`.
- **KAG3 system.** The scenario parser (C++ KAGParser), macros, `cond`, `&` expressions,
  call/return, labels, `f`/`sf`/`tf`. Bookmarks with thumbnails, the history layer with `[hact]`,
  auto/skip/read tracking, `[goback]`, the plugin hooks.
- **Agent tooling.** The Wamsoft REPL / `-replfile` / `Agent` input and capture, and bundled
  Claude Code skills for TJS2 and the krkrz API.
- **Our own work.** Every decoder and engine model in `tools/tsuki` (230+ tests): the converter
  and the reference oracles.

**Must build:** the converter (scripts, tables, assets, manifests), the whole hx library
(section 5), every UI screen with the game's art, the test harness (section 10), and two tiny
optional native plugins (`hxlut`, `hxrule`). In effort this is most of a port, because the
hardest parts are the ones HuneX hard-coded: the layer and motion model, the text box, and menus
whose layouts are code constants in the eboot.

---

## 9. Fidelity gaps against the PS4

| # | gap | severity | mitigation |
|---:|---|---|---|
| 1 | **Font**: a TTF (Windows Yu Mincho / BIZ UDMincho, or a bundled OFL serif) instead of the game's glyph pages | medium (the look) | ja: emit the PS4 line breaks from `tsuki.textbox` (bit-exact with the game metrics); full-width glyphs keep the same pitch. en: let KAG wrap, or tune the size. Stretch: feed the game glyph pages through krkrz's prerendered-font callback (`PreRenderedFontImage`: alpha + metrics per glyph) [H] |
| 2 | **Text reveal** and styles: HuneX fades in the last ~3 glyphs; the outline/shadow look; `@g/@z/@b/@a` | low | custom `processCh` override in hx/text.tjs |
| 3 | **Ruby layout**: KAG draws ruby at its own offset and size; HuneX uses scale 0.1 x 0.11, dy -7.04 | low | set `rubysize`/`rubyoffset`; multi-character bases through `[ch]` |
| 4 | **Rule-wipe curve**: krkrz's universal transition is linear over `vague`; HuneX uses smoothstep over the full range (weights differ by up to ~10 % mid-wipe) | low (947 uses) | `hxrule` native transition plugin, ~100 lines C++ |
| 5 | **Blend precision**: 8-bit tables against float32/fp16; dodge black guard; nexl alpha family | very low | per-mode tests; HxCanvas special cases where they differ |
| 6 | **STEC recolour** without a native plugin: pre-baked combinations only; motion-driven SNX2/SNX3 changes (fields 24/25) approximate | low-medium | `hxlut` plugin (bit-exact) |
| 7 | **Frame rate** of heavy animated scenes on CPU compositing | medium (risk) | section 4.5 gate |
| 8 | `_FADE(40)` screen effect, `_STNS` noise, crop feathering (a16/a17): not fully understood by the research | low | stand-ins, logged |
| 9 | Rewind granularity and backlog jump differ unless hx history is complete | low | hx history ring (M4) |
| 10 | Movie timing through Media Foundation (frame pacing, AAC silent tracks); Windows "N" editions need the Media Feature Pack | low | test all 134 files open; doc requirement |
| 11 | Timing: KiriKiri waits in ms against HuneX's 60 Hz frame counts (`(ms*3)/50`) | negligible | hx uses frames x 1000/60 |
| 12 | No PS4 save import, trophies only as a log, no touch pad or light bar | low | P3 |
| 13 | UI screens are re-implementations from code constants: animations and positions approximate until each screen's eboot code is transcribed | medium (P1/P2 polish) | per-screen reference renders + human review |

---

## 10. Proving that the whole game is playable

Three layers of proof. Each runs from one command and gives a pass/fail result plus a coverage
report. Tests that need game data run locally only; their outputs and goldens are never
committed.

### 10.1 Static checks (Python, headless, minutes)

- **Converter completeness.** All 98 statement names have a rule. Zero statements fall through.
  The converter test suite uses synthetic scripts (no game data, CI-safe), plus the real corpus
  locally.
- **KAG lint.** A Python KAG3 tokenizer over all emitted `.ks` files checks that:
  - every tag is a KAG3 built-in or a defined macro, with only known attributes;
  - every `storage`/`target` exists;
  - labels are unique;
  - every referenced picture, rule, Ogg/`.sli`, movie and UI file exists in the manifest;
  - each `.sli` loop matches `tsuki.audio`'s `smpl` loop to the sample.
- **Reference VM explorer.** A headless Python model of the HuneX VM: control flow, variables,
  choices and scene bits from `tsuki.script_emu` / `vm_vars`; layer and sound state from the
  layers and sound models. It explores the scene graph across sequential playthroughs, modelling
  the persistent `G[0..499]` and scene bits that unlock routes and extras. Its outputs:
  - a **covering set of choice paths**: every one of the 206 story choices taken at least once,
    every `_IF__` outcome reached or proven infeasible, every story script (300..790) entered,
    every label reached;
  - per path, a **golden trace**: page ids, text ids, voice ids, committed picture ids per
    layer, BGM/SE ids and movie ids in order;
  - coverage of assets: the 16,459 voices in use, 10,962 resolvable picture names, 1,314 SE
    names, BGM 2..123, 40 movie ids.

### 10.2 Runtime autoplay (krkrz on the user's PC, needs a window, no human)

- Start krkrz with `-replfile=<dir>`, or with a startup flag that loads `hx/test.tjs`.
- **Test mode:** all waits, transitions and movies take zero time; voices are logged, not
  played; each choice is answered from the path file; for every page the same trace format is
  written to a file.
- Any TJS exception, KAG error dialog or missing storage aborts the run with a non-zero exit code.
- **Pass:**
  - every covering path finishes;
  - the trace equals the reference VM's golden trace event for event;
  - every story script, label and choice edge appears in the union of the traces.
- To save time, branch points start from saved states, so the long shared prefixes are not
  replayed. Throughput is measured in M1. The budget for a full corpus pass is under one hour.

### 10.3 Save round trips and visual sampling

- **Save round-trip.** On every path, save at ~50 random pages, then load (also after a process
  restart), and dump hx + KAG state. The dumps must be byte-identical. Language switching is
  round-tripped at a page label too.
- **Visual sampling.**
  - At ~500 sampled commit points, hx dumps the drawn per-layer state as JSON and saves HxCanvas
    as a PNG (`Layer.saveLayerImage`, no screen grab needed).
  - The Python reference renderer (`tsuki.image_catalog` + `misc_layer_state_and_motions`
    projection + `tsuki.compose` formulas) renders the same state. Compare max/mean difference
    and SSIM, with thresholds per blend mode.
  - Ground truth from the PS4 itself: the prologue movie frames, which show page 1 of `00_02`
    (`RUNTIME_REQUIREMENTS` 7.5), plus any PS4 captures the user adds.
  - A local HTML page shows the worst 50 diffs side by side for human review.
- **Audio.**
  - Decode every BGM's Ogg + `.sli` and check sample continuity at the loop seam against
    `tsuki.audio`.
  - Check that the voice id set over all traces = the 16,459 used ids.
- **Human sign-off.** Text speed, transition timing, movie sync and the UI feel are judged by
  the user. Prose descriptions of screenshots do not count as verification.

---

## 11. Milestones and acceptance criteria

| M | scope | acceptance |
|---|---|---|
| **M0** toolchain + harness skeleton | build Wamsoft krkrz x64 (VS2022 Build Tools 17.14, vcpkg, CMake/Ninja); KAG3 hello world at 1920x1080; `-replfile` round trip; converter skeleton with manifest; KAG lint; reference VM explorer prototype on 00_00..01_xx | krkrz.exe runs a KAG sample and answers a TJS expression through `-replfile`; converter + lint pass on 3 scripts; explorer lists the choice edges of chapter 1. **Fallback:** official 1.4.0 binary if the build fails within 2 days |
| **M1** first scene | `00_02` page 1 (RUNTIME_REQUIREMENTS 7): img1685 with the motion-22526 zoom, vignette `_STBR(4,24)`, motions 22523..22526, `_FADS(0,10,0)`, `MOON_SE03028` looping with fade-in to 30 %, messages 3..7 with voices 0x3D04..0x3D08, ja ruby and en | statement trace = `tsuki.script dump`; picture pixel-identical to `tsuki.image`; HxCanvas vs reference render within threshold; SE loop seamless; **performance gate of 4.5 passed**; `_WTTM(4000)` = 4.0 s |
| **M2** opening + core | boot stand-in -> title stub -> New Game -> `00_01` (prologue MP4 in the chosen language, `_TOMW`, `_STCG` x39, `_RTMN` keeping the BGM) -> START_SCRIPT -> `00_00`; every layer, transition, sound and text command implemented; all 549 scripts convert and lint clean | traces equal the reference VM for 00_00..chapter 1; zero unknown commands; zero missing assets across all 549 scripts |
| **M3** whole game, skip-play | all routes and extras (QA_01..25, ARCHIVE, STAFFROLL), choices, scene bits, persistent unlocks; ja + en (zc/zt generated) | autoplay of the full covering set: 100 % of story scripts, labels and choice edges; zero runtime errors; traces equal the goldens; voice/picture/SE coverage reports complete |
| **M4** system features (overlaps M3) | message window art, reveal/auto/skip rules, backlog with voice replay and jump, rewind, save/load (139 + quick + auto at `_ATSV`), thumbnails, config (all options persisted), system menu, choice screen art, title state machine with titlebg movies, language switch | save round-trip suite passes; every menu entry of `engine.md` 8.3 reachable; UI screens reviewed against reference renders by the user |
| **M5** fidelity | `hxrule`, `hxlut` (or pre-bake), per-mode blend tests, masked/cropped layers, `_FADE` stand-in review, sampled visual comparison, ducking/gating/per-character volumes, movie variants | visual sampling thresholds met; worst-50 page reviewed; audio seam suite green |
| **M6** extras | CG gallery, music room, movie gallery, flowchart, ending list, extra scenarios | unlock rules per `gallery.md`; each screen reviewed |

---

## 12. Install requirements

**Development (agents, once):**

- Windows 11 x64 (present).
- **Visual Studio 2022 Build Tools 17.14** with the C++ workload. Its bundled CMake and Ninja are
  enough; Wamsoft says other versions are untested.
- **vcpkg** (git clone + bootstrap, `VCPKG_ROOT`). The first build of its dependencies takes a
  while.
- git (present).
- ANGLE `libEGL.dll` / `libGLESv2.dll`, only if the GPU path is used (e.g. mmozeiko/build-angle
  releases).
- Python 3.11 + venv with numpy, pillow, numba (present).
- ffmpeg with the `atrac9` decoder and libvorbis/libopus (present: FFmpeg 9).
- About 20 GB for the toolchain and the vcpkg cache.

**User (to play):**

- Windows 10/11 x64, not an "N" edition (or with the Media Feature Pack, needed for the Media
  Foundation H.264/AAC decoders).
- The krkrz.exe we build, plugins, `system/` + `hx/` + `ui/` scripts, plus the licence notices.
- Their own `CUSA47999` folder (verified by the eboot SHA-256).
- Python 3.11 with the converter for the one-time conversion. A frozen converter .exe can come
  later.
- **About 60 GB free disk** (55 GB converted data; movies stay in place).
- A Japanese serif font: Windows' Yu Mincho / BIZ UDMincho, or a bundled OFL font.

No game data, eboot tables or converted assets are ever distributed. The converter writes only
to the user's local data folder.

---

## 13. Time estimate (AI agents in parallel)

Work packages and agents (cap 5-6 concurrent, each with its own files and a status file;
hooks block skipped tests and edits to generated files):

| package | agents | weeks (calendar) |
|---|---:|---|
| A toolchain, krkrz build, native plugins | 1 | 0.5 (M0) + 0.5 later |
| B script converter + KAG lint | 1-2 | 1-2 |
| C asset/table pipeline + audit | 1 | 1 |
| D reference VM explorer, traces, test harness | 1 | 1.5-2 |
| E hx layers/motions/canvas/transitions | 1-2 | 2-3 |
| F hx sound + movies | 1 | 1-1.5 |
| G text box + choices | 1 | 1.5 |
| H UI screens (title, menus, save/load, backlog, config) | 2 | 2-3 (from week 3) |
| I extras (gallery, flowchart, music, endings) | 1 | 1-2 (after M4) |

Critical path: M0 (~0.5-1 week) -> M1 (~0.5-1) -> M2 (~1.5-2) -> M3 (~1-2), with M4 running
alongside M3. That is **about 6-9 weeks** to a game playable from start to finish with save,
backlog, auto/skip and config. Allow **8-12 weeks** for M5 + M6.

Agent coding speed is not the bottleneck; the research phase produced the whole format corpus in
about a day. The bottlenecks are:

- the toolchain build;
- TJS debugging and GUI-bound verification, which the REPL channel only partly removes;
- the performance gate;
- human review of feel and UI.

This route is unlikely to be faster than a runtime written directly in C#/Python, because the
bespoke core is the same size, and KiriKiri's free services mainly shorten package H.

---

## 14. Risks, ranked, with kill criteria

1. **CPU compositing performance at 1080p.** Animated backdrops, 20+ layers, affine resampling.
   - Gate: M1 (4.5).
   - **Kill:** if neither the cached CPU path nor a 3-day GLES prototype reaches the gate, stop
     this route. Move the converter, pipeline, explorer and harness to the own-runtime route.
2. **Toolchain.** The Wamsoft build needs VS2022 17.14 + vcpkg and has no binaries. A build
   failure means the 2017 x86 binary with overlay-only MP4 and no agent REPL.
   - Kill: none needed for M1. The whole game needs the fork (x64 memory, layer-mode MP4).
3. **TJS/KAG proficiency and debuggability.** These are niche languages (55 kirikiri-topic
   repos against thousands for Ren'Py).
   - Mitigation: Wamsoft's bundled skills, `-replfile`, TJS kept small and mechanical, most
     logic decided by the Python converter and tested there.
4. **KAG3 interplay.** KAG's own BGM/SE/character-layer store must be kept out of hx's way.
   Bookmark restore order, `[trans]` with the message layers as children, and the history at
   `[p]` all need attention.
   - Mitigation: an early save round-trip test (M2).
5. **Memory.** Pictures up to 85.8 MP (343 MB RGBA). Mitigation: x64, an explicit picture cache
   with LRU purge, loading only the visible region when cropped.
6. **Fidelity of text** (font, layout) and of UI screens rebuilt from code constants.
   Mitigation: section 9, human review.
7. **Unresolved engine details** (`_FADE(40)`, `STNS`, crop feathering). Mitigation: stand-ins,
   logged; they would be the same for any route.
8. **Disk** (~55 GB) and conversion time (0.5-1.5 h). Acceptable on this machine (870 GB free).
   Native MZP/ATRAC9 plugins could remove it later.

---

## 15. Sources

Primary, read for this document:

- krkrz releases (1.4.0r2, 2017-12-24, single 4.3 MB asset; GitHub API):
  https://github.com/krkrz/krkrz/releases
- krkrz site (requirements; H.264 needs Windows 7+, WMV needs WMP9): https://krkrz.github.io/
- krkrz movie code: Media Foundation player in EVR overlay mode (`vomMFEVR`), DirectShow
  layer/mixer: https://github.com/krkrz/krkrz/blob/master/movie/win32/MFPlayer.cpp ,
  https://github.com/krkrz/krkrz/blob/master/visual/win32/VideoOvlImpl.cpp
- krkrz project configurations (Win32 + x64):
  https://github.com/krkrz/krkrz/blob/master/vcproj/tvpwin32.vcxproj
- krkrz universal transition table (linear ramp):
  https://github.com/krkrz/krkrz/blob/master/visual/tvpgl.c (`TVPInitUnivTransBlendTable_c`)
- krkrz Photoshop blend tables (soft light, dodge, burn):
  https://github.com/krkrz/krkrz/blob/master/visual/gl/blend_function.cpp , `blend_functor_c.h`
- krkrz loop-info parser (`#2.00`, `Link{From;To;Smooth;Condition}`, `Label`):
  https://github.com/krkrz/krkrz/blob/master/sound/WaveLoopManager.cpp
- krkrz org repositories (wuvorbis, wuopus, kag3, gamepad, KAGParser...): https://github.com/krkrz
- KAG3 system scripts (KAGPlugin hooks, `saveThumbnail`, `numSEBuffers`, `hact`, `goback`, `ruby`
  for the next character, `processCh`): https://github.com/krkrz/kag3 (`data/system/Plugin.tjs`,
  `MainWindow.tjs`, `MessageLayer.tjs`, `HistoryLayer.tjs`)
- KAG3 tag reference (English): `[image] mode` values incl. `psdodge5`/`psdiff5`; `[trans]`
  universal/crossfade/scroll; `[ruby]` one character; `[xchgbgm]`; `[video] mode` overlay/layer;
  savable labels and `[record]`: https://kirikirikag.sourceforge.net/contents/Tags.html
- Wamsoft integrated repo (build with VS2022 17.14 + vcpkg, presets, ANGLE DLLs, krkrz_tools incl.
  loop tuner, REPL, bundled Claude Code skills): https://github.com/wamsoft/krkrz_dev ;
  engine https://github.com/wamsoft/krkrz (tags v2.2.0..v2.6.5)
- Wamsoft docs: REPL/agent channels https://wamsoft.github.io/krkrz_dev/topics/core/repl/ ;
  Agent class https://wamsoft.github.io/krkrz_dev/reference/Agent/ ; movie spec (MF, WebM,
  MPEG-1, layer mode segment loops) https://wamsoft.github.io/krkrz_dev/specification/movie/ ;
  sound spec (WAV/Vorbis/Opus, miniaudio, `.sli`) https://wamsoft.github.io/krkrz_dev/specification/sound/ ;
  GPU drawing (OGLDrawDevice, Canvas, Offscreen, ShaderProgram)
  https://wamsoft.github.io/krkrz_dev/guide/Graphic3DSystem/ ,
  https://wamsoft.github.io/krkrz_dev/reference/ShaderProgram/ ; fonts (`Font.addFont`,
  rasterizers) https://wamsoft.github.io/krkrz_dev/guide/FontSystem/ ; prerendered glyph callback
  https://wamsoft.github.io/krkrz_dev/reference/PreRenderedFontImage/
- VitaScenariosTool macro map (FHA opcodes = renamed KAG macros; unmapped ones commented out):
  https://github.com/Hintay/VitaScenariosTool/blob/master/macroslist.py

Project documents: `docs/RUNTIME_REQUIREMENTS.md`; `docs/formats/script.md` (command catalogue,
census), `compositing.md` (shader formulas, transitions, STEC), `layers.md` (layer model,
motions), `adv_state.md` (save/resume, backlog, auto/skip, config), `sound_runtime.md`,
`system_flow.md`, `textbox.md`, `vm_variables.md`, `gallery.md`;
`docs/research/engine_lineage.md` (25/25 blend-mode match, KiriKiri authoring credits).

Precedents used: tsukiweb's step-checked offline asset builder
(https://github.com/requinDr/tsukiweb-public); the Mahoyo Android port's synthetic,
data-free tests and per-effect modules (https://github.com/david419kr/mahoyo-steam-android-port);
Carter Appleton's harness-first lesson (https://carterappleton.com/blog/porting-zeus-to-the-browser/).
