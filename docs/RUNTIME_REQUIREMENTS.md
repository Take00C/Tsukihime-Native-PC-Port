# Runtime requirements: native PC runtime for TSUKIHIME -A piece of blue glass moon-

What the native PC runtime has to implement to replace `eboot.bin` of the PS4 release (CUSA47999),
derived from every format and engine document in `docs/formats/` and from the format map
[`FORMATS.md`](FORMATS.md). This file supersedes the engine agent's draft
`RUNTIME_REQUIREMENTS.draft.md` (kept as history; section 10 lists what changed).

The runtime reads the user's own copy of the game at run time and ships no game data (3.4). The
implementation language and the graphics / audio APIs are **not chosen** here; every requirement is
stated so that any language can meet it.

Snapshot: 2026-10-25. The first version marked seven topics **pending research** because their
follow-up investigations were still running. All seven documents have since arrived (vm_variables,
layers, compositing, system_flow, sound_runtime, adv_state, gallery; [FORMATS.md 1.1 / 1.2](FORMATS.md#11-delivered-documents)),
and this revision replaces every pending marker with the behaviour they document, with its
priority for the first vertical slice. Review findings that their authors did not apply are in
FORMATS 6.9; where they matter here they are named.

---

## 0. Conventions

- **Priority**, as used in FORMATS.md section 7:
  **P0** = needed for the first vertical slice (boot -> prologue movie -> first scene with
  background, text and voice; section 7); **P1** = complete, faithful playthrough; **P2** = menus
  and extras completeness; **P3** = optional platform extras.
- **Knowledge**: **known** = algorithm and data verified (engine code and/or full-data census,
  usually by running the engine's own code under emulation against a model); **partial** =
  structure known, details open; **open** = not analysed. No topic is pending research any more.
- Claim labels where they matter: [E] read from engine code, [V] verified on retail data or by
  emulation, [H] hypothesis.
- References: `engine 5.3` = `docs/formats/engine.md` section 5.3; likewise `containers`, `images`,
  `text`, `script`, `audio`, `fonts`, `textbox`, `archives_ui_movies` (short: `aum`),
  `vm_variables`, `layers`, `compositing`, `system_flow`, `sound_runtime`, `adv_state`, `gallery`.
  `FORMATS 6.x` = `docs/FORMATS.md` section 6.x (corrections not applied to the format
  documents; follow them, not the original text); `FORMATS 6.9` = review findings against the
  follow-up documents.
- Addresses are eboot.bin VAs of the analysed build (3.3): text VA = file offset - 0x2280; RELRO VA
  0x400000 = file 0x32C210; data VA = file offset - 0x365FA0 + 0x600000.
- Coordinate spaces are tabulated in FORMATS 5.5; the output frame is 1920 x 1080.

---

## 1. Checklist

Items 1-28 keep the draft's numbering (engine.md cites "item 20"); items 29-36 are new.

| # | subsystem | P | consumes | reference | knowledge | acceptance check |
|---:|---|:-:|---|---|---|---|
| 1 | Archive layer: HED8 / HED4, NAM32 / NAM8, MRG data, mrgd00 (MZP), standalone files | P0 | `image/*.hed/.nam/.mrg`; `allscr.mrg`, `script_text.mrg`, `ft_<lang>.mrg`, `se_nam.mrg` | containers 2-4, 7-8 | known | all 49,620 triplet entries and 891 standalone sections located; sizes equal the payloads' own size fields; bank starts allpac0 `[474, 992]`, allpac1 11 starts, allpac1_2 `[2503, 3417, 3681, 4221]` |
| 2 | Archive slots and picture name lookup | P0 | NAM files; eboot picture-name list (data 0x7F6800) and image catalog (0x671900) | aum 2.1-2.4, engine 6 | known | `img2511` -> record 2518 -> allpac1 #2157; `WHITE` -> 5 -> allpac0 #5; `boy_a_00_00_n01` -> 11680 -> allpac0 #1838 + #1840; `img1696` with args 9/10 = 11 -> 6218 -> allpac0 #1121 |
| 3 | eboot table extractor | P0 | `eboot.bin` | FORMATS 5.4, engine 9, 3.2-3.3 below | known (layouts) | build check 3.3 passes; extracted tables reproduce: ui_text 5 x 1,499 (`samples/engine/ui_text_<lang>.tsv` byte for byte), catalog 14,744 + 31 empty, name list 14,744 + 1, scene ids 549, movie table 134 + terminator, motions 24,192 / 71,905 keyframes, font lookup 17,423 entries |
| 4 | Script VM front end (segmenting, dispatch, argument readers, markers) | P0 | `allscr.mrg` sections 3.. | engine 5.1-5.3, script 3 | known | dispatch of all 112 entries + the 6 no-ops equals `ENGINE_MAIN`; inline / object readers equal the engine on the 35,474 corpus expressions; `_STTR,0,26);` runs STTR; at most 1000 segments per call |
| 5 | Script commands (98 statement names in use: 94 commands + 4 marker kinds) | P0 / P1 | scripts, script_text | script 4, engine 5.3 | partial | every used name dispatched; `_SNX4 _FANX _IFP4 _DMMY _STQK _NOP_` are no-ops; `_RTM2` sets the keep-BGM flag; layer / effect / sound semantics per items 12-14, 18, 31 |
| 6 | Variables, operands, conditions | P0 (`_WKST` / `_WKAD` / `_IF__` of the slice) / P1 (choice commands) | - | vm_variables, engine 5.5, FORMATS 6.9 | known (`_SELD` per-line loop missing from the model, FORMATS 6.9) | `tools/tests/test_vm_vars.py` and `test_misc_vm_variables_errata.py` equivalence (engine vs model under emulation); `C<n>` as a write target = `F[F823+n]`, literal n as a value; `_IF__` jumps when its condition is false; `_SELA` opens only when F1118 = 0 and always clears it; Z never written; out-of-range indices rejected |
| 7 | Message display: `$` expansion, markup, layout, block draw | P0 | `script_text.mrg`, `ft_<lang>.mrg`, eboot layout and glyph tables | text 4-6, fonts, textbox | known (layout bit-exact), vertical scale open | layouts equal `samples/textbox/engine_layout_golden.json` (601 cases) record for record; en line 3 breaks as `... hangs / in the air.` |
| 8 | Read-text tracking | P1 | - (persisted in system data) | engine 5.6, script 3.7 | known | `readmax[block]` updated when the next marker is reached; blocks > 0xFB3 never recorded; upper-case hex decodes the engine's way |
| 9 | Auto / skip / fast-forward | P1 | config | adv_state 6, 10; engine 3.3 | known | auto waits config +0x2E ms (+0x9E on a page's last message, F799 = 1) after the reveal and the voice; skip / fast-forward multiply the frame step (64; analog R2 for held fast-forward); with config +0x30 = 1 (default) skipping stops at unread text; next scene runs until `_ATSV`, a choice, a jump or an unread line |
| 10 | History ring, rewind, previous / next scene | P1 | - | adv_state 2-5; engine 5.6 | known | 800 records of 0x3A10 bytes pushed with the kinds of adv_state 2.3; hold-to-rewind undoes records newest-first back to the previous page start without re-executing commands; previous scene restarts the current scene (or the previous one within its first two messages) |
| 11 | Backlog | P1 | script_text, voice | adv_state 5.4-5.5; textbox 4.5 (backlog draw 0x3C4B0) | known | entries = ring records of kinds 0 / 8 / 11 / 12 minus the current page; a voice replays from record +0x3630; a jump restores the entry's record and runs its command again |
| 12 | Renderer: canvas and the 40 layers | P0 | decoded pictures | layers 3-7; engine 4.1, script 4.3-4.5 | known except quake / shake / swing, the crop / mask pass and `_STRA` (FORMATS 7); projection note FORMATS 6.9 | scene_state equals `tsuki.misc_layer_state_and_motions.SceneModel` after every statement of the slice; the three composed frames `samples/script/xref/compose_*_H4.png` and the prologue frame (section 7.5) are reproduced |
| 13 | Blend modes (`_STBR`) | P0 (modes 11, 13, 17, 19, 24 of `00_02`) / P1 | - | compositing 2-5 | known | the formulas of compositing 2.3 equal `tsuki.compose` on random inputs for modes 0..25; straight alpha; the backdrop D is the target before the quad |
| 14 | Transitions, rule masks, sepia, screen effects | P0 (`_FADS` 0/1/2) / P1 | `RULE_nn` (allpac3 #86..#134) | compositing 7-10 | known (engine-only transition types, `_FADE` effect 4 open) | `_FADS(t, 1000, ..)` lasts 60 frames; cross-fade weight 127/255 at p = 0.5; rule-wipe weights of compositing 7.4; `_FADS(2, ms, 0)` reuses the loaded rule |
| 15 | Image decoding (MZP header, PAL8 / PAL4 / HEP8 / RGB565+LO+A, borders, tile flags) | P0 | allpac0..4, parts, ft_* | images 1-6, 9 | known | decodes equal `tsuki.image` (SHA-1 checks of `tests/test_image.py`); flag-0 tiles skipped |
| 16 | Audio decode: ATRAC9 | P0 | `bgm`, `vse`, `voice` (LV + MZX) RIFF | audio 2-5 | known | decoded length = superframes x 1024; playable = `decoded[256 : 256 + fact[0]]`; bgm #33 loop `[962000, 8257999]` inclusive |
| 17 | Audio decode: HE-VAG | P1 | `vag.mrg` (19 entries) | audio 6 | known | 19/19 decode; length = 28 x index of the first frame with flag >= 7 |
| 18 | Mixer, channels, volumes, fades | P0 (`_SEPL` fade-in, `_VPLY` / `_VPL2`, `_MPLY`) / P1 | config | sound_runtime; audio 3, 4, 7 | known (`_VPL2` gate and scan state: FORMATS 6.9) | the records, gains and ramps of `tsuki.misc_audio_runtime_semantics.SoundModel` (engine-equal on 22,037 corpus statements) or the worked examples of sound_runtime 4; pan law of 4.10; voice on streams 2..5, SE channel c on stream c + 6, two alternating BGM slots |
| 19 | Movies | P0 | `image/movie/*.mp4` (134) | aum 9, engine 8.7, script 6.5 | known (codecs, table, remap) | prologue plays in the selected language variant (ids 4 / 46 / 47 / 106) with audio in sync; `_TOMW` returns at its end |
| 20 | Input (pad, mouse, keyboard) | P0 (decide / cancel) / P1 (all actions) | - | engine 8.5, adv_state 8, 10 | known (some action roles [H]) | decide / cancel follow the enter-button setting; auto-repeat; sticks act as directions; pointer clamped to 0..639 x 0..447 (640 x 448 space); the 27 actions of preset 0 (adv_state 10.2) reachable from pad and keyboard |
| 21 | Timing | P0 | - | engine 3.3 | known | fixed 60 Hz logic step; `_WTTM(1000)` = 60 frames via `(ms*3)/50` |
| 22 | Save / load (slots, quick save, system data) | P1 | own save files; optional PS4 `savedata000` import | adv_state 7; engine 7 | known (layout emulated on game-made images; no retail save checked) | slots 0..99 manual, 100..138 auto-save FIFO, 139 quick save; a slot = page-start record + scene-start record + date; load = restore without re-display and run the page's `_ZM` again; Continue loads the newest slot |
| 23 | Configuration and its persistence | P0 (defaults) / P1 | `conf_parts_<lang>.mrg`, ui_text | adv_state 9; sound_runtime 2.4; textbox 6.3 | known | the 37 menu items of adv_state 9.2 present; first-run values = the boot values (= Recommended Settings); fields without a menu item are constants |
| 24 | Boot and title flow | P0 (first-boot path) / P1 | parts.mrg, caution images, TITLE_PARTS, title movies | system_flow 3-4 | known (first-run save dialog, title particles open) | the boot list of system_flow 3.4 for its four cases: first boot = autosave notice -> 00_01 at once; after the prologue = PRESS ANY BUTTON -> caution -> caution1 -> 00_00; returning player = TM_CI -> caution2 -> title menu; background per G[2] |
| 25 | Menus: system menu, save / load, flowchart | P1 | allpac3 UI sets, ui_text, flowchart tables | engine 8.2-8.3, aum 5, 7; adv_state 9-10; gallery 5 | partial (config, title, boot notices and choice screen decoded; system menu, save / load, backlog and HUD layouts are code to port) | every menu entry of engine 8.3 reachable; the actions of adv_state 10.2 open the same screens |
| 26 | Gallery: CG, music, flowchart, movies, ending list | P2 | GALLERY_PARTS_*, eboot gallery / flowchart / music / ending tables, unlock bits | gallery | known (flowchart line drawing open; review notes FORMATS 6.9) | the six-item gallery menu (no voice gallery); unlock rules of gallery 2.3; a gallery entry replays through ARCHIVE (scene 3000) with G[8] = scene |
| 27 | Localisation | P0 (selection) / P1 | per-language files, fonts, ui_text, movie variants | FORMATS 5.3 | known | language 0 ja / 1 en / 2 zc / 3 zt; "follow system" maps the OS locale like VA 0x407F0 |
| 28 | Platform extras | P3 | `sce_sys/trophy` (optional) | engine 2.2, 8.8 | known (calls) | optional achievements; screenshot watermark not required |
| 29 | Picture resolution and composition: `_hh_vv` variants, body + face, language bands, missing layers | P0 | catalog, name list, allpac0/1/1_2/4 | images 7-8, aum 2.4, 3.3 | known | body + face composited at (0, 0) match the allpac2 copies (MAE about 1); IMG2431 en band = rows 2794..5353 |
| 30 | Fonts and glyph lookup | P0 | `ft_<lang>.mrg` pages (texture slots 0x21..0x61), eboot glyph tables | fonts 2-6 | known | glyph lookups equal `tsuki.font` (emulation-verified, 0 mismatches); `EbootFontTables.verify()` passes |
| 31 | Layer motions (`_STMA` / `_SGMA` keyframes) | P0 (the opening uses them) | eboot motion tables | layers 8; script 6.6 | known | `apply_motion` equivalence (82,562 engine calls, 0 mismatches); motion 1 fades layer 3 from alpha 255 to 0 over 2,800 ms (00_00); motion 22526 zooms 1200 -> 912 per mille over 70 s with easing 3 (1,074 at 17.5 s) |
| 32 | Choice screen (`_SELR` / `_SELD` / `_SELA` / `_SELS`) | P1 | script_text 43959..43968, SELECT_PARTS, DISP_PARTS | system_flow 5; vm_variables 4.5-4.6; text 5.2, 5.4 | known (`_SELS` / `_SELA` prompt layout open; `_SELD` per-line loop FORMATS 6.9) | 206 story choices render and select with the draw list of system_flow 5.3; locked choices skipped by the cursor; the chosen `_SELR` value lands in the `_SELD` target; `_SELA` prompts only at chapter starts reached by play |
| 33 | Message window pictures and HUD | P0 | DISP_PARTS (allpac3) | textbox 7 | known (pictures, positions); F822 = 0 path open | windows 0..2 = DISP_PARTS children 11..13 at window opacity; bubbles 3..55 (QA scripts) |
| 34 | Asset prefetcher | P3 | - | engine 5.4, FORMATS 6.5 | known | optional: no effect on script state |
| 35 | UI framework: canvases, texture slots, registration tables | P1 (P0: none needed beyond a title stub) | allpac3 UI sets | aum 7.2-7.5 | known (data), per-screen layout partial | the 707 table lookups resolve (695 direct, 4 clamped, 8 rewritten at run time) |
| 36 | Save resume (History_Restore; scan mode only as a fallback) | P1 (P3: scan fallback) | - | adv_state 3, 7.3; script 3.6 | known | a slot load restores state A (F lists, scene_state, sound) without re-display, runs the page's `_ZM` again and reaches the saved page with the same screen and variables; scan mode (replay to `_PGST`) is needed only for a slot without the 0x2AF8 end marker |

---

## 2. Architecture overview

```
startup
  locate the user's CUSA47999 folder ---- verify the build (3.3)
  extract eboot-only tables (3.2) into memory (or a local cache, 3.4)
  open archives (4.1): HED/NAM in memory, MRG data by positional reads / memory map
  load or create system data (4.12) -> config (4.14), language (4.19)
  boot flow (4.15) -> title -> New Game / Continue -> ADV mode

every 1/60 s (fixed step, 4.18)
  input  -> internal button mask, edges, auto-repeat, pointer (4.13)
  update -> current process (boot screen / title / menu / ADV)
            ADV: ScriptStep: up to 1000 segments, stops at the first yield (4.8)
                 [fast-forward: repeated calls in one frame]
            timers: text reveal, text / window fades, layer motions, waits, transitions, movie clock
  audio  -> commands to the mixer (start / stop / fade / volume per channel)
  draw   -> layers 0..39 (z order) into the display surface, transitions with captured surfaces,
            HUD (window picture, page messages), UI screens, movie frame (YCbCr -> RGB)
present at the display's own rate (the last 60 Hz frame is shown until the next one exists)

audio thread: 48 kHz float mix (two BGM slots, voice, 20 SE channels, HE-VAG, movie audio)
```

The PS4 engine runs its logic exactly once per displayed 60 Hz frame (engine 3.3); the port keeps
that 60 Hz step independent of the monitor rate.

---

## 3. Game data the runtime reads

### 3.1 Files

All paths are relative to the user's `CUSA47999` folder. The folder is opened read-only; the runtime
never writes into it. File-name case differs between the eboot strings and the disc
(`/app0/image/movie/titlebg1.mp4`, `CONF_PARTS_EN.MRG` vs `conf_parts_en.mrg`), so lookups on
case-sensitive file systems must be case-insensitive.

| file(s) | size | used by | access | P |
|---|---|---|---|:-:|
| `eboot.bin` | 12,767,802 B | eboot-only tables (3.2) | read once at start | P0 |
| `image/allpac0.{hed,nam,mrg}` | mrg 6.3 GB | full-size pictures (archive id 0, searched last) | positional reads | P0 |
| `image/allpac1.{hed,nam,mrg}`, `allpac1_2.*` | 24.3 GB, 9.1 GB | full-size pictures (ids 1, 2). The PS4 opens them only when their PlayGo chunks are installed; the port treats them as always present | positional reads | P0 |
| `image/allpac3.*` | | UI sets (nested mrgd00), `RULE_nn`, caution screens, placeholders | positional reads | P0 |
| `image/allpac4.*` | | late additions, searched first | positional reads | P0 |
| `image/allpac2.*` | | 1/12.8 thumbnails, entry k = catalog record k (save-slot previews) | positional reads | P1 |
| `image/parts.{hed,mrg}` | | 24 system screens by index (logo, save warnings, autosave notice, disclaimer) | small | P0 (boot) |
| `image/allscr.mrg` | 9,585,000 B | 549 scripts, label tables | read sections on demand | P0 |
| `image/script_text.mrg` | 12,601,040 B | text, 5 language pairs | keep the current language pair in memory | P0 |
| `image/ft_{ja,en,zc,zt}.mrg` | 1.2-10.8 MB | font pages (only the current language; ft_ko is never selected) | load all 65 pages at language change | P0 |
| `image/voice.{hed,nam,mrg}`, `voice2.*` | voice.mrg about 1.32 GB | 16,464 voice lines | positional reads, whole entry per line | P0 |
| `image/vse.{hed,mrg}`, `image/se_nam.mrg` | | 1,327 SEs + their names | positional reads | P0 |
| `image/bgm.{hed,mrg}` | | 132 BGM tracks (bgm.hed has no terminator record) | streamed | P0 |
| `image/vag.{hed,mrg}` | 1.8 MB | 19 HE-VAG system sounds | memory-resident | P1 |
| `image/movie/*.mp4` | 134 files, 4.75 GB | movies | streamed by the movie decoder | P0 (prologue) |
| `data/if_<lang>.png`, `image/corp.png` | | PS4 save icon, screenshot watermark | not needed | - |
| `sce_sys/*`, `sce_module/*`, `sce_discmap.plt` | | PS4 system files (trophy data optional, P3) | not needed | - |

The research copy also contains a zero-byte `eboot.fself` (an extractor artefact); ignore it.
Archives must never be read whole into memory: allpac1.mrg alone is 24,257,312,768 bytes.

### 3.2 Tables that exist only in eboot.bin

The complete list with locations, sizes and Python extractors is FORMATS 5.4; this section adds the
runtime rules.

- The runtime extracts at start (P0): picture-name list (data 0x7F6800), image catalog
  (0x671900), scene ids (text 0x216130), movie table (data 0xDC8C70) and the prologue remap table
  (text 0x200F70), motion tables (text 0x274538, 0x2A3938, flag words 0x245130; data 0x873FF0;
  the loop points are not a table but a compiled switch at VA 0xC1C70, which the research tools
  extracted by emulating it into `samples/misc_layer_state_and_motions/motion_loops.json`; a port
  needs its own decoder of that switch, or must run the same extraction locally), font lookup and
  rectangle tables, layout tables (text 0x2D32C0..0x2D3758), window-picture tables (text
  0x2005B0..0x200C0C), UI texture registration tables, blend / effect tables, config defaults
  (data 0x66C578), history F-index lists (text 0x2160E0, 0x216580, 0x2165F0), the voice-group
  table (text 0x215EA0) and character mask table (data 0x86A850). P1 adds `ui_text[5][1499]`
  (RELRO 0x41D5F0; all menu, dialog, config, chapter and music-room strings, none of which are in
  script_text.mrg), the title-background table (text 0x218A40, 6 x i32), the action-mask presets
  (data 0xDB84F0), the config menu pages (data 0x66E890) and the system-SE tables (text 0x2D3770,
  data 0x6241F0); P2 the gallery, flowchart, music-player and ending-list tables (FORMATS 5.4,
  gallery 1). The rule-mask names (data 0x869B30), the legacy music-mode table (text 0x209510),
  the BGM names (data 0x7A8820) and the legacy CG list (data 0x78C810) are dead and not needed.
- Address mapping: data VA -> file `VA - 0x600000 + 0x365FA0`; RELRO VA -> `VA - 0x400000 +
  0x32C210`; text VA -> `VA + 0x2280`. Pointers stored inside these tables are prelinked VAs (the
  values are already in the file; each also has an R_X86_64_RELATIVE relocation with the same
  addend), so they can be followed with the same mapping (engine 9).
- Catalog name pointers are text VAs; names compare case-insensitively (engine 5.5: only 1,221
  match exactly).
- Shader binaries (data 0xDCA6E0..0xDD66B0) are reference material only; the port writes its own
  shaders (4.5, 4.6).

### 3.3 Verifying the user's build

Every eboot address in the documents belongs to one build. The runtime must confirm it has that
build before it reads any table, and refuse to start with a clear message otherwise. It must not
guess addresses on another build.

1. **Identity (primary check).** The analysed eboot.bin is 12,767,802 bytes with SHA-256
   `c2843415fdfda5ebbc84f4d55c6cca044a7476436cf0cfdfccac83116beeb918` [V: computed on the
   research copy, 2026-10-25]. A match means every address in the documents applies.
2. **Structural probes** (for diagnostics when the hash differs, and as a self-test when it
   matches); each is a documented invariant:
   - SELF magic `4F 15 3D 1D`, 10 segments, all plain, with the segment table of engine 1.2
     (text at file 0x2280, RELRO at 0x32C210, data at 0x365FA0);
   - the string "Hunex Consumer Library" at text VA 0x2DA510;
   - image catalog: record 0 `name0` -> `BLACK.mzp`, record 1 -> `BLUE.mzp` (data 0x671900 + 0x08,
     stride 0x48), 14,744 used records followed by 31 empty slots (`name0` = 0);
   - picture-name list at data 0x7F6800: entry 0 `BLACK`, 14,744 names + one empty record, then
     a 0xFF byte;
   - movie table: record 0 id 0 -> `/app0/image/movie/titlebg1.mp4`, terminator id -1 at data
     0xDCA590;
   - scene-id table at text 0x216130 starts 110, 240, 260, 270;
   - `ui_text[ja][0]` (RELRO 0x41D5F0) -> `"NULL"`;
   - the font table self-check (`EbootFontTables.verify()` in `tools/tsuki/font_eboot.py`,
     fonts 10.7).
3. **Data files.** Entry and section counts match FORMATS section 2 (12 triplet archives with
   49,620 entries, allscr.mrg 552 sections, script_text 5 x 43,969 lines, voice 16,464 entries,
   134 movies). A mismatch means a different data release: warn, and stop if a table that the
   runtime indexes by number (catalog k = allpac2 entry k, scene ids, movie ids) disagrees.
4. **Version string.** The research copy's `sce_sys/param.sfo` reports `APP_VER` 01.00 and
   `VERSION` 01.00 (`CONTENT_ID` HP3279-CUSA47999_00-GAME000TSUKIHIME), while the documents call
   the build "app 01.01" [V: read from param.sfo, 2026-10-25]. This disagreement is unresolved;
   identify the build by the eboot hash, not by a version string.

Supporting other builds (patches, other regions) needs a pattern-based table locator; that is P3
(FORMATS 7, item 19).

### 3.4 Data policy

- The runtime and its repository contain no game data: no archives, pictures, audio, video, text,
  fonts, eboot.bin or tables extracted from it. Everything listed in 3.1 and 3.2 is read from the
  user's own legitimately owned copy at run time.
- Anything the runtime derives from that copy (extracted eboot tables, decoded-texture caches,
  thumbnails) stays in a local, per-user cache and is never uploaded or bundled.
- Golden files for tests (section 7.5) are generated locally from the user's copy by the Python
  reference tools and are never committed. The repository's `samples/` folder is local research
  output of the same kind and must never be shared (see `README.md`).

---

## 4. Subsystems

### 4.1 File system and archives (items 1, 2) - P0, known

Consumes: every `.hed`, `.nam`, `.mrg` and standalone mrgd00 file of 3.1.

Must do (containers 2-4, 7-8; aum 2):

- **HED8** (allpac*, parts, bgm, vse, vag): 8-byte records `sector_lo, sector_hi, size_sectors,
  size_lo`. Sector = `sector_lo | (sector_hi >> 12) << 16 | bank << 20`, with the bank table built
  by the engine rule (a new bank starts where the high nibble returns to 0 after being non-zero,
  at most 11 switches). Exact size = `(size_sectors*0x800 & ~0xFFFF) | size_lo`, minus 0x10000 if
  that exceeds the sector span. A 16-byte 0xFF record ends every HED8 except bgm.hed. Do **not**
  copy mangetsu / deepLuna offsets or sizes (no bank rule, no -0x10000 correction).
- **HED4** (voice, voice2): `sector = w0 | (w1 & 0xF000) << 4`, span = `(w1 & 0xFFF) << 11`; a
  record is empty only when both words are 0xFFFF (entry 7850 `ffff 1b30` is real).
- **NAM32 / NAM8**: record i names entry i; NAM8 holds only the 4-character voice group.
- **mrgd00**: `offset = data_start + sector*0x800 + (byte_offset & 0xFFF)`, size decoded like
  HED8; one nesting level in retail data (recurse generally anyway); decide nested-archive vs
  image by content, not by extension (`DUMMY.MRG` is an image).
- **Packed picture ids** `archive << 16 | entry`; archive ids 0 allpac0, 1 allpac1, 2 allpac1_2,
  3 allpac2, 4 allpac3, 5 allpac4.
- **NAM file search** (VA 0xC3270) for names with extension coming from eboot data: start after
  the last backslash, copy exactly 28 bytes upper-casing a..z (no upper-casing after a byte >=
  0x80), search allpac4, allpac3, allpac1_2, allpac1, allpac0 in that order, compare 28 bytes
  including the NUL. Not found -> -1 (draw nothing, never fail).
- Access pattern: HED / NAM files are small and loaded whole; MRG data is read with positional
  reads or memory maps, one entry at a time.

### 4.2 Decompression (item 1) - P0, known

Consumes: MZX0 streams in picture tiles, scripts, font pages and voice entries.

- **MZX0** (containers 5): 16-bit-word commands RLE / back-reference (2..512 bytes) / 64-entry
  ring buffer / literal. The **clear counter** (0x1000, minus n per command, minus 1 for a
  ring-buffer read) resets `last` at every reset; without it pictures decode to garbage. The size
  test runs only between commands: allocate `size + 128` bytes or clamp writes while consuming the
  whole command (overrun up to 127 bytes).
- **Scripts** (allscr sections 3..551): decode plain, then XOR every byte with 0xFF (a one-pass
  variant must use `last = 0xFFFF` both at the start and at every reset).
- **LV voice wrapper**: `'L' 'V' u16 n`, n payload bytes (lip-sync placeholder `00 09 00` in all
  16,466 entries; parse and skip), then MZX0 if the next byte is `'M'`, else raw data.
- No other compression exists (no zlib, LZ4, NXGX/NXCX) [V-all].

### 4.3 Image decode and texture upload (item 15) - P0, known

Consumes: image MZPs from allpac0..4, parts, allpac3 children, font pages.

- Header (images 2): `width, height, tile_w, tile_h, tiles_x, tiles_y, u16 bmp_type, u8 depth,
  u8 attr`, palette only for `bmp_type` 1 (16 or 256 RGBA entries), then one flag byte per tile.
- Formats (images 3-5): **PAL8** (256-entry palette in PS2 CSM1 order: entry i stored at
  `i ^ 0x18` when bits 3 and 4 differ; alpha `0x80 -> 0xFF`, else `2a & 0xFF`), **PAL4** (linear
  nibble stream, low nibble first; only empty placeholders), **HEP8** (`bmp_type` 12: per tile a
  32-byte `HEP` header, indices, own 256-entry palette in plain order, alpha `2*min(a, 0x7F)`),
  **RGB565+LO+A** (`bmp_type` 11: planes u16 RGB565, u8 low bits, u8 alpha).
- **Tile border** (`attr & 1`, all HEP8): every stored tile has a 1-pixel border copied from its
  neighbours; only the inner `(tile_w-2) x (tile_h-2)` pixels are shown, at `(tx*inner_w,
  ty*inner_h)`; shown size = stored - 2 x tiles. When drawing tiles separately, the border is a
  ready-made filter apron: sample the inner rectangle and seams stay invisible under bilinear
  filtering.
- **Tile flags**: skip flag-0 tiles (never drawn; allpac0 BLUE / GREEN / ORANGE / YELLOW are
  therefore invisible); blend every other tile with its alpha, including flag-2 tiles.
- Output is straight (non-premultiplied) RGBA8 with sRGB-encoded values; premultiply before
  bilinear filtering (fully transparent pixels often carry colour).
- **Texture upload**: keep tiles as separate textures (or a tile atlas) and stream them; pictures
  reach 11,730 x 1,270 / 1,530 x 11,220 / 11,220 x 7,650 shown pixels (up to 85.8 MP, 343 MB as
  RGBA8), far beyond a single texture's limits. The PS4 converts each tile to a B8G8R8A8 texture
  on the CPU (VA 0xD6900); no GPU block compression is involved.
- **Texture slots**: the engine addresses textures by slot number: font pages 0x21..0x61, UI
  sheets by the registration tables (e.g. config children 0..19 -> slots 361..380, aum 7.2), and
  UI code places them by immediate slot numbers. A slot-indexed texture registry with the engine's
  numbering lets UI code be transcribed directly (4.16).

### 4.4 Picture resolution and sprite composition (items 2, 29) - P0, known

Consumes: script picture ids, eboot name list + catalog, allpac0/1/1_2/4 entries.

- Script ids (`_STCH`, `_SCH2`, `_SPFC`, `_SPRD`, `_STCG`) are resolved by: `_hh_vv` suffix
  (STCH / SCH2 only: args 9 / 10, or inherited from the source layer, arg 18; digits written only
  for values > 0) -> key = upper-case up to the first `.`, at most 32 bytes -> name list index k
  (not found: -1, draw nothing) -> catalog record k (aum 2.4, script 4.3a).
- Record k draws `name0`, then `name1` (face) over it at the record's layer offset, which is
  (0, 0) in all records; both are resolved with the NAM search (4.1). 3,529 paired sprites,
  1,221 standalone.
- `_hh_vv` names (7,288, `pic_flags` bit 0) are complete alternative (pre-blurred) renderings
  that replace the base; they are not overlays.
- **Language bands** (`pic_flags` bit 1, 166 records): draw rows `[L*S, L*S + h)` with
  `S = ceil(h/254)*254` and L = layout language 0..3; never the whole picture (images 8.1).
- Missing files (3 sprite bodies such as `NOE_L_60_03_N00_B`, 5 script names such as
  `IMG5007_02_02`): the layer gets nothing; do not fail (script 6.4).
- Every displayed picture sets its base id's bit in the "picture seen" set (VA 0x164D5C0; 3,200
  bits; variants map to their base, ids >= 0xA8D go through the seen-index map data 0x86AA30;
  `_STCG` sets a bit without drawing, but only for ids < 0xA8D; gallery 2.2, FORMATS 6.9) - this
  feeds the CG gallery (4.17).
- Logical size and anchor come from the catalog (`w, h`, anchor `c, d` in the 640 x 362.67
  canvas), not from the MZP canvas, which includes transparent padding.

### 4.5 Layers and compositing (items 12, 31) - P0, known (shakes, crop / mask pass, `_STRA` open)

Consumes: decoded pictures, motion tables, script layer commands.

Reference: layers (state, commands, commit, update, projection, motions; every claim checked
against the engine's own code under emulation, layers 11); script 4.3-4.5 and engine 4.1 are
superseded where they differ (FORMATS 6.3). Python model: `tsuki.misc_layer_state_and_motions`
(`SceneModel`, `commit`, `apply_motion`, `layer_matrix`).

- **State.** 40 layers (0..39); commands with a layer > 39 do nothing. Statements write the
  0x1A08-byte `scene_state` (VA 0x16500C0; 51 per-layer arrays and globals, layers 3; saved in
  every history record). `_FADS` commits it to record array B (0xDE2650, 40 x 0x2C8; layers 5)
  and starts the transition; the per-frame update moves B to the drawn array A (0xDDB5E8) and runs
  tweens, motions and shakes (layers 6). So layer changes appear at the next `_FADS`, except the
  live effects that also act on A at once: timed `_STGS` / `_STZ4` / `_STRT` / `_STCL` tweens,
  `_STMA` key 0 and `_STPV` (layers 4.3).
- **`_STCH` / `_SCH2`** (layers 4.1): `_STCH` resets the drawn record and sets picture,
  `x' = trunc(x*640/480)`, `y' = trunc(y*480/360)` (480 x 272 script space -> 640 x 362.67 canvas),
  scale (per mille, arg 4), alpha (arg 5, clamped 0..255, default 255), z (arg 6, default 10 x
  layer), anchor = catalog anchor, rotation 0, angle (arg 19). It does not reset flags, parent,
  `_SOXY`, mask, crop, a16 / a17, `_STRA` or `_STNS`. `_SCH2` is the same without the reset. Both
  reset `_STTR` to -1, take a `_hh_vv` variant from args 9 / 10 (or the source layer), arg 11 =
  mask layer, 12..15 = crop rectangle in picture pixels, 16 / 17 = edge parameters; with a source
  layer (arg 18) they copy its fields and motion and re-point mask / parent references (a
  self-source statement stops the motion). An empty picture name clears the layer.
- **Other setters** (layers 4.2-4.3): `_STCP(l, v)` = z / draw order; `_STPV(l, x, y[, r])` =
  anchor (scaled by the picture's size class, plus the variant anchor difference) and rotation;
  `_STZ4` zoom per mille (negative = mirrored) with an optional pivot flag (zoom in screen space
  after rotation); `_STRT` rotation in 1/100 degree (clockwise); `_STGS` alpha ramp; `_STCL`;
  `_SOXY` offsets (x / 3, y * 448/1080 canvas units); `_STEC` colour-effect mode 0..81 (colour
  stored only for mode 1); `_SNX2` / `_SNX3` brightness / contrast; `_STNS` noise (n >= 13 -> 0);
  `_STTR` per-layer rule `(n-1) | m << 12`; `_STCC` clears (-1 = all); `_SBAS` sets a parent layer;
  `_SACL` sets flag 0x40; `_SPMA` pauses a motion (only for an argument of exactly 1). Timed
  values: the scene keeps the end value, the drawn record animates (frames = `trunc(ms*60/1000)`).
- **Projection** (layers 7): the picture's catalog anchor lands on the layer position; scale x
  zoom and rotation act about the anchor; a parent (`_SBAS`) carries its children in its own frame,
  with child positions and zoom multiplied by the parent picture's size class (1 / 0.5 / 0.25 /
  0.125); then the 640 x 362.667 canvas maps onto 1920 x 1080 (x 3, x 2.978), so at scale and zoom
  1000 one picture pixel is one frame pixel and one script unit is 4 frame pixels. The quad
  builder places the texture at position - anchor and never reads the catalog width / height.
  Use the corrected anchor rule of FORMATS 6.9 for layers whose motion animates fields 6 / 7 (not
  needed by the slice).
- **Motions** (layers 8): `_STMA(l, id, wait, n)` starts keyframe motion id on the layer (id <= 0
  stops it; key 0 is applied at once); the per-frame update advances its frame counter and applies
  every enabled field (flag word 0x245130) with the interpolation and easing of layers 8.4 / 8.5
  (port the float32 formulas: 99.8 % of eased keys are bit-exact); n >= 2 quantises time to
  `frames - frames % n` (the speed is kept); looping motions follow the switch 0xC1C70
  (`samples/misc_layer_state_and_motions/motion_loops.json`); fields 22 / 23 switch the picture to
  a `_hh_vv` variant; `_SGMA(l, id, key)` writes the layer's drawn state into keyframe `key` of
  motion id for the rest of the session. Motions are absolute values.
- **Draw order**: layers sorted by z (0x4A180); the scene draw 0x56DE0 runs before the HUD and
  UI processes (layers 1; compositing 6).

Open (FORMATS 7): the per-frame quake / shake / swing formula (`_SQK2` `_SHK2` `_SSW2` `_SHOK`
`_MSQK`; the storage is decoded, layers 4.4), the crop / mask rasterisation and offscreen pass,
`_STRA`'s visible effect, the reader of `_STCF`'s globals (stored without visible effect). The
slice (`00_02` to `_PGST(2)`) avoids all of them: it has no shake, mask, crop, source-layer,
`_SBAS`, `_STTR` or `_STRA` statement, and its only motions are 22523..22526 (fields 1, 2, 5, 9,
10, 27) [V: `tsuki.script` scan of script 3 and `python -m tsuki.misc_layer_state_and_motions
--motion 2252x`].

### 4.6 Transitions, blend modes, screen effects (items 13, 14) - P0 (opening) / P1, known

Consumes: `RULE_01..RULE_49` (allpac3 #86..#134, 1920 x 1080 greyscale), layer and surface state.

Reference: compositing (formulas derived from the shaders' GCN code and checked by running it;
engine 4.2-4.4 superseded). Python model: `tsuki.compose` (`draw`, `crossfade`, `rule_wipe`,
`RuleSlot`, `transition_frame`).

- **Draw model** (compositing 1, 4): every quad is `out = blend_state(shader(S, D, C, P), RT)`
  with S = layer texel, D = render target before the quad, C = vertex colour (C.a = layer
  opacity), P = per-draw constants. Layer pictures use straight alpha (normal draw = SRC_ALPHA /
  ONE_MINUS_SRC_ALPHA); render-target alpha is a mask channel, not presented. The PS4 computes on
  the sampled 8-bit values of A8R8G8B8 sRGB-encoded surfaces; `tsuki.compose` does the same, and
  a port should blend on the encoded values unless a comparison shows otherwise [H].
- **Blend modes** (compositing 2.3, 5): `_STBR(layer, mode, , flag)` (14,940 uses) sets the
  layer's draw-effect word `(flag << 8) | mode`; mode 0 normal, 3 basic, 1..25 the 24 blend
  shaders. "nexl" modes (1 add, 2 addalpha, 4 darken, 5 dodge, 6 lighten, 7 multiplicative, 8
  opaque, 9 screen, 10 sub, 15 ps_diff5, 17 ps_dodge5) mix the opacity inside the shader; "PS"
  modes 11..25 (ps_add, burn, darken, diff, dodge, exclusion, hardlight, lighten, multi, overlay,
  screen, softlight, sub) leave it to the blend state. Soft light is `d^e` (not W3C), hard light
  and ps_add use the vertex colour, dodge keeps pure black. Flag 1 weights modes 5 / 7 / 9 by
  opacity x picture alpha and turns blending off for 5 / 7 / 9 / 10 / 15 / 17 (ordinary layers;
  masked layers: FORMATS 6.9). The flag is cleared only by `_STCH`, `_STCC` and the scene reset.
- **`_FADS(type, ms, rule[, mode])`** (compositing 7; type 1 = cut 20,853 uses, type 0 =
  cross-fade 12,240, type 2 = rule wipe 947; always yields): on the first frame of the request the
  frame as the previous frame left it is copied (alpha forced to 1) and kept as the "old frame";
  progress p starts at 1 and falls by `speed * min(3, frames) / (ms / 16.666)` per update (1000 ms
  = 60 frames). Each frame the new scene is drawn, then the old frame on top: type 0 with weight
  `w = trunc(255 p) / 255`; type 2 with per-pixel weight `f = smoothstep(lo, lo + 1, rule.r)`,
  `lo = 1 - 2p` rounded to 1/128 (dark rule pixels switch first), or the type-0 weight when the
  rule slot is empty. The rule picture (`find("mask_top.mzp") + n` = allpac3 #85 + n) sits in one
  shared slot that `_STTR` also loads or frees; `_FADS(2, ms, 0)` and fast-forward reuse the loaded
  rule; script end frees it. Rules 49..52 also play UI sound 0x19. Arg 3 sets the message-window
  word 0x164BA6E (meaning [H]).
- **`_FADE(kind)`** (compositing 8): a screen-effect command (kinds 40 / 41 in scripts; effect 4
  not traced). **`_STEC` / `_SNX2` / `_SNX3`** (compositing 9): CPU recolour of the picture when its
  texture is built (81 colour-effect ids, brightness offset, contrast; 0xD36F0, bit-exact in
  `tsuki.misc_compositing_transitions_shaders`).
- **Other shaders**: YCbCr -> RGB for movies is BT.709 limited range (compositing 2.3, PS #4);
  the sepia shaders are unreachable in the shipped game (compositing 10). The PS4 keeps 16 capture
  surfaces of 1920 x 1080; a port needs one old-frame copy for transitions plus its own captures
  for thumbnails.

Open (FORMATS 7, 6.9): which layers take the two-pass masked path, the engine-only transition
types 18, 20..24, 68..70, `_FADE` effect 4, and the [H] that the target still holds the previous
frame when the transition copies it (no clear first). The slice needs modes 11 / 13 / 17 / 19 / 24
and `_FADS` types 0 and 1.

### 4.7 Text: strings, markup, fonts, text box (items 7, 30, 33) - P0, known

Consumes: `script_text.mrg` (sections 2L / 2L+1), `ft_<lang>.mrg`, eboot glyph / layout / window
tables, DISP_PARTS.

- **Strings** (text 4): `blob[off[i] .. off[i+1])`, strip CR LF, at most 767 bytes, "" for
  strings shorter than 3 bytes; ids beyond the table are rejected by the port.
- **Message assembly** (text 5.2-5.3): `_ZM` copies its argument into a 0x300-byte buffer, stopping
  at `)` (end mode 0), `@n` (mode 1) or `@e` (mode 2); `_MSAD` appends; the whole buffer is
  `$`-expanded (6 decimal digits, no digit check) into at most 0x2FC bytes. `_SELR` builds
  `"$0439NN" + msg` slots itself (choice numbers 43960 + k, or 43959 for k >= 9).
- **Markup** (text 6, fonts 6-7, textbox 4-5): byte rules of the `@` codes, `^` line break,
  `<base|reading>` ruby (disabled when the layout language is English, where `<` `>` `|` are
  glyphs), 39 `[icon]` tags, styles 0..5 (`@g`, `@z`, `@i`, `@r`).
- **Glyphs** (fonts 4-5): keys are the UTF-8 bytes packed big-endian; binary search in 16 eboot
  lookup arrays, then the 73-px wide grid (style 0, 3/4-byte characters) or 17 rectangle tables;
  outline quad = same rectangle 512 px lower on the page; joined dashes and redaction bars are
  special-cased.
- **Layout** (textbox 4-5): `Text_Layout(block, text, 474, 220, spacing 0, leading 9, colour,
  flags; sx = sy = 0.74)`; width 574 in English (600 when the window index is 42). Pass 1
  assigns break classes (kinsoku 0x0D / 0x0E / 0x71, English word table, at most 1200 elements);
  pass 2 places glyphs (`pen += adj + (w-6)*sx`), wraps (margin `W - 96*sx`; English `W - 64*sx`
  with look-ahead) and places ruby (scale 0.1 x 0.11, dy -7.04). All float arithmetic is float32
  in the engine's operation order (the wrap decision depends on it).
- **Page geometry** (textbox 2, 5.8): text space 640 x 360 units at 3 px per unit; first message
  of a page at (87, 49) units (ja / zc / zt) or (30, 35) (en); line pitch 26.02 units (78.06 px);
  later messages stack below, with `C847` empty-line offsets.
- **Draw and reveal** (textbox 6, 8): per glyph optional drop shadow (+1, +1 units, alpha 0.66),
  outline (draw flag 0x1000 and config outline on), fill (RGB565 colour word expanded with
  `& 0xF8`; 0xFFFF -> 248, 248, 248; read text 0xDC6D). Reveal head advances `1000 / (60 * v)`
  counters per frame (v = text speed, default 35 -> 28.6 glyphs/s; v <= 0 = instant) and the last
  about 3 glyphs fade in; text fade T over 18 frames after a closed window; window fade 0.06 per
  frame (`_CLO3`: -0.2604 per frame); `_CLO4(ms)` / `_CLO5(ms)` fade out / in.
- **Window pictures** (textbox 7): drawn when F822 == 1 and F1104 == 0; index `w = F1105`
  (English: F1109 if > 0): 0..2 = DISP_PARTS children 11..13 (full-screen dark overlays) at
  window opacity, 3..32 speech bubbles (only the QA scripts), 33..55 stretched bubbles.

Open (none of the follow-up documents covered these): the movie shows 81 px line pitch against
78 px from the engine constants (textbox 2, FORMATS 7 P0 item 1); what `@g`, `@z`, `@b`, `@a` look
like; outline colour (runtime table 0xDB7BB0) and colour channel order; previous-message dimming
(textbox 5.8; FORMATS 7 P1 item 12). Related facts from adv_state 9.2: the reveal speed is config
+0x20 (unseen text) / +0x22 (seen text); +0xA3 / +0xA4 are the "Text Display Animations" options.

### 4.8 Script VM (items 4, 5, 34) - P0, known (front end), partial (semantics)

Consumes: allscr.mrg (section 0 names, section 1 label tables, sections 3.. scripts), scene ids.

- **Loading**: one script resident at a time; scene id -> script index k by linear search of the
  549-entry table (unknown id -> START_SCRIPT); section `k + 3` (k < 600, else 3); labels by
  name from the 32-byte records of section 1 child k (`name, block hex, offset hex`; max 1000
  records; compare up to the first byte <= 0x20).
- **Segmenting and dispatch** (engine 5.2, script 3.1, 3.6): segments end at `;` within 0x300
  bytes; the 4 bytes after the first `_` are the name (separator not checked); first letter
  `A..Z` required; unknown names and the letters B D H K L N O Q U Y skip silently; every `J*`
  is JUMP, every `Z*` a marker; at most **1000 segments per call** (every segment counts). The
  handler either continues or yields (always yield: CLO2-5, END_, FADS, MSAD, SELA, SELD, SELS,
  STCF, TOMW, WTSV, WTTM, WTTR; may yield: ATSV, GOTO, GOTS, IF__, J*, MFAD, PGST, RDED, RTM_,
  RTMN, TOMP, VCWT, VPLY, Z*, ...). An implementation that runs statements one at a time and
  blocks on waits is equivalent for the script [H]; the one visible difference is that history
  records of kinds 1 / 9 store the position where the engine's ScriptStep call started
  (FORMATS 6.9, adv_state).
- **Normal pacing**: one ScriptStep call per frame; skip and fast-forward multiply the frame step
  passed to Adv_Update (4.12). **Next scene** (action 23; adv_state 5.2): while dword 0x16541F8 is
  1 the caller sets word 0x120A6D2 and calls ScriptStep repeatedly in the same frame, with all
  waits cleared, until `_ATSV`, a choice, a jump, `_TOMP`, a script end or (by default) an unread
  line clears the word; about 64 sound and wait sites skip their output meanwhile (engine 3.3;
  sound_runtime 11.1 "ff" state).
- **Argument readers** (script 3.2-3.3a): the inline reader (sum of decimal terms, all other bytes
  ignored, empty = 0, missing = -1, backquote tag skip of 5 bytes, `()` index 0 = 0) for most
  commands; the object parser with a right-associative C-like evaluator (`{}` grouping, stops at
  the first unknown byte, so `242.25` = 242) for the 52 commands in `OBJ_COMMANDS`. Keyword tags
  `` `NNN: `` are filed by number regardless of position (script 3.4).
- **Markers** `_Z<k><bbb><ss>(arg)` (script 3.7): lower-case hex digits decoded as
  `c - 0x57 if c >= 'a' else c - 0x30`; the previous marker is recorded as read
  (`readmax[block] = max(sub)` for block <= 0xFB3) before the new one is processed; `ZM` shows a
  message, `ZZ` / `ZY` are labels, `ZD` is never displayed.
- **Script end** (script 4.1): `END_`, `RTM_`, `RTMN` run the cleanup VA 0x7F1A0 (stop the 20 SE
  channels; stop the BGM unless the keep-BGM flag set by `RTMN` / `RTM2` is set).
- **No-ops**: `_SNX4 _FANX _IFP4 _DMMY _STQK _NOP_` (6 names, other platforms' commands).
- **Prefetcher** (engine 5.4, FORMATS 6.5): the PS4's second interpreter is a look-ahead asset
  prefetcher (43 entries: 41 names + `J***` + `ZY**`; its V case accepts only `VPLY` / `VPL2`).
  Optional for a port (P3); it restores all variables on exit.
- **Scan mode** (script 3.6, adv_state 7.3): replaying a scene from its start up to the target
  `_PGST(n)` with most handlers returning early is used only by the fallback load 0xA3050, for a
  slot without the 0x2AF8 end marker (saves from older builds [H]). Normal slot loads restore the
  saved history record (4.12), so a port needs scan mode only to import such slots (P3). In scan
  mode the choice commands change nothing, `_GOTS` yields and no sound is played (vm_variables 1
  item 8; sound_runtime 11.1).

### 4.9 Variables (item 6) - P0 (slice commands) / P1, known

Consumes: nothing on disk; part of save and system data.

Reference: vm_variables (every reader run under unicorn against `tsuki.vm_vars`, 0 differences;
operand table `samples/misc_vm_variables_errata/operand_table.md`); engine.md 5.5 is now
corrected.

- **Storage**: three u16 arrays `F[1200]` (VA 0x164B1D0), `Z[1400]` (0x164BB30), `G[2000]`
  (0x164C620). Operands are decimal with leading zeros allowed (`G008` = G[8]).
- **Operand decoding** (vm_variables 3, 5): every operand is decoded from its first byte (after
  an optional 5-byte backtick tag), and its index is the inline-reader value of the whole
  argument. Which first bytes select a variable depends on the command and argument position
  (the 912-cell table of vm_variables 5); anything else is a literal in a value position and
  "do nothing" in a target position.
- **Forms**: `F<n>`, `G<n>`; `C<n>` = `F[F823 + n]` as a `_WKST` / `_WKAD` target (F823 = number
  of the current message on the page, so `C847` / `C859` are per-message slots F847..F857 /
  F860..F869) but the literal n in every value position; `_D<n>` = F[n] (G[n-600] for 600..700);
  `Z<n>` is never a script variable (target: nothing happens; value: literal n); `_GOTS` / `_SEL*`
  read three digit bytes.
- **Commands** (vm_variables 4): `_WKST(target, op, value)`, `_WKAD` / `_WKOR` / `_WKOF` / `_WKBO`
  with the read / write order of vm_variables 3.4; `_IF__(lhs, op, rhs, label)` **jumps to label
  when the condition is false** (a structured if); `_GOTS(sel, L0, L1, ...)` jumps to label number
  F[sel]; `_SELD(_D000, N)` records F[0] as the result target and the choice UI later writes the
  chosen `_SELR` line's argument 0 there; `_SELS` stores the button index 0 / 1; `_SELA` (the
  "save your progress?" prompt) opens only when **F1118 = 0**, always sets F1118 = 0 and stores
  nothing. In the retail scripts F1118 is set at every chapter start that is entered from the
  archive (F026 != 0), so the prompt opens only when a chapter is reached by play.
- **Not yet in the model** (FORMATS 6.9, P1): `_SELD` writes F[799] = (k == count - 1) and pushes a
  kind-0xC history snapshot for every pending `_SELR` line k (needed for the backlog, rewind and
  auto mode at choice pages); the choice UI resets the line count when a choice is decided.
- The PS4 does not range-check (`F1200` aliases Z[0], `F-1` the word before var_F); the port
  rejects out-of-range indices. Retail scripts stay in range (F up to F1123, G up to G070).
- **Persistence** (vm_variables 6, adv_state 2.1): a history record (and so a save slot) keeps
  356 of the 1200 F values in three storage classes (u16 list text 0x2160E0, bit list 0x216580,
  i8 list 0x2165F0); every F index the scripts use is among them. G[0..499] is system data. The
  persistent bit sets (gallery 2.1): `_RDST` scene_seen G[110..145], `_RDED` scene_done
  G[300..335], flowchart edge_seen G[400..443], `_RTST` per-save copies F[201..272] /
  F[600..687].

The slice uses `_WKST`, `_WKAD` and `_IF__` only (00_01 / 00_02 skip their statements 40..68
because `_IF__(F026,ne,0,LV00000)` jumps when F026 = 0; 7.2).

### 4.10 Audio: decoding, mixing, loops (items 16-18) - P0 / P1, known

Consumes: `bgm.mrg` (streamed), `vse.mrg` + `se_nam.mrg`, `voice.mrg` / `voice2.mrg`, `vag.mrg`,
movie audio, config volumes.

Codecs (audio 2-6):

- **ATRAC9 RIFF** (bgm, vse, voice): `fmt ` (WAVE_FORMAT_EXTENSIBLE + ATRAC9 GUID + config word),
  `fact` (`samples, 256, 256`), `smpl` (one loop; bgm / vse only), `data`. 48 kHz; superframes of
  512 B (bgm), 384 B (vse, voice), 160 B (dummies), 1024 samples each. Playable samples are
  `decoded[256 : 256 + fact[0]]`. FFmpeg's `atrac9` decoder decodes all 17,925 streams.
- **HE-VAG** (vag): big-endian `VAGp` header, 16-byte frames with a 4-tap, 128-row predictor
  table (audio 6.2). FFmpeg's `adpcm_psx` decodes these files wrongly; the port needs its own
  decoder (about 30 lines). 19 entries: a dummy and 9 stereo sounds stored as `_L` / `_R` mono
  pairs. Register only the real entries (the PS4 registers indices 0..49).
- Decode to float and mix in float: decoded vse peaks reach 1.878 full scale (FORMATS 6.6 d).

Loops (audio 2.3-2.4):

- Loop = `[smpl.start - 256, smpl.end - 256]` in playable samples, **end inclusive**; a loop exists
  iff start != end. Every bgm / vse file has one; whether it loops is the caller's decision:
  `_SEPL` argument 2 for SEs (stored per channel), BGM loops by default (flag table 0x624380),
  voice and vag never loop.
- Seamless loop: seek to superframe `loop_start / 1024`, reset the decoder, discard
  `256 + loop_start % 1024` samples, continue (or jump within a decoded copy). The engine applies
  no crossfade.
- Engine details worth keeping [E]: a `` `069 `` start position starts decoding 1200 samples earlier
  (clamped to 0); a non-looped stream ends 128 samples before `fact[0]` (inaudible).

Channels and mixer (sound_runtime, verified engine-equal on the whole corpus; audio 3, 4, 5, 7
with its corrections; engine 8.6). Python model: `tsuki.misc_audio_runtime_semantics.SoundModel`.

- **Streams** (sound_runtime 2): 0 / 1 = two BGM slots that alternate on every new track (data VA
  0xDD8F28 toggled by `xor 1`), so the old track fades while the new one starts; 2..5 = voice,
  rotating `2 + (n & 3)` with all four stopped first (one voice at a time); 6..25 = script SE
  channels c = 0..19 (`arg0 % 20`); HE-VAG voices 0x2C..0x2F for the system SEs. Each stream has a
  0x34-byte record (state, kind, current / target volume, ramp, pan, loop, character; 2.2).
- **Tags** (sound_runtime 3.1): found by number anywhere in the statement. `` `001 `` = fade-in /
  fade-out / ramp ms (default 0; an SE fades only for >= 17 ms), `` `011 `` = the old track's
  fade-out in `_MPLY`, a wait switch in `_MFAD`, `` `017 `` = volume (default 100, clamped 0..100),
  `` `018 `` = pan 0..128 (64 centre), `` `049 `` = BGM loop (default 1 = forever; one statement
  plays once), `` `068 `` = delay in ms (the command goes into a 20-entry deferred queue; executed
  at once in fast-forward and scan states), `` `069 `` = BGM start sample, `` `070 `` = config
  slot + 1 for voice-like SEs.
- **Commands** (sound_runtime 3): `_MPLY(n)` plays bgm.hed entry n; a negative track stops the
  BGM; a request for the track already playing (u32 0x62437C) is ignored. `_MVOL` / `_MFAD` ramp /
  fade the BGM (`_MFAD` waits ms / 16 frames unless `` `011 `` is 0). `_SEPL(ch, loop, name, ,
  tags)` (= `_SEFI`): names resolve in se_nam section 2 (= vse entry index; upper-cased, cut at
  `.` or 28 characters); loop 1 loops at the RIFF loop; one-shot SEs are dropped in skip mode.
  `_SEFD` fades out (`` `001:0 `` = stop), `_SEVL` ramps the volume, `_SESP` stops.
- **Per-frame update** (0xAD80, sound_runtime 6): all fades and ramps are linear per 60 Hz
  frame, step = `points * 1000 / 60 / ms` (one point = 1 % of the class volume; a step <= 0 gets
  0.2); a BGM replaced without fade fades out at 4.0 per frame; fade-to-stop channels are stopped
  at 0. A port at another frame rate scales the step by the real frame time.
- **Gains** (sound_runtime 6.1): BGM and voice = `trunc(cur * class_volume * 0.01)` (an integer
  percentage) * 0.01; SE = `min(100, g * cur * 0.01) * 0.01` in float. Class volumes (block
  0x17DA268, from config; 2.4): +0xB movie 40, +0xC BGM 40, +0xD voice (character volume x voice
  master / 100), +0xE SE 40, +0xF system SE 10, each times (1 - mute).
- **Voice** (sound_runtime 7): `_VPLY(name, hex)` only stores the entry (voice.hed `int(hex, 16)`;
  voice2.hed for 20,000..59,999) and the character from the name's 3-letter group (table text
  0x215EA0). The next `_ZM` starts it unless fast-forward is on, the voice master or that
  character's voice is off, or skip mode is on without config +0x30 (or with u32 0x16541E8 set);
  the stream plays the whole decompressed entry at `char_volume * master / 100` (default 50 %).
  Adv_Update stops it when the text advances if config +0x4E is set (default) and F1123 = 0, or
  in skip mode. `_VCWT` waits until the voice ends. `_VPL2` plays at once with its own, stricter
  gate (FORMATS 6.9).
- **Voice groups** (sound_runtime 8): the 54 voice.nam groups map to 24 characters and **19
  config slots** (18 named characters plus a catch-all; young Shiki B90 shares Shiki's slot 0);
  `samples/misc_audio_runtime_semantics/voice_groups.tsv`. A voice-like SE (`` `070:c ``) uses
  slot c - 1's gain and is silenced when that voice is off.
- **Ducking** (sound_runtime 9): with config +0x1C on (off by default) the BGM drops to half
  (-6 dB) in 32 frames while a voice plays and recovers in 64 frames.
- **System SEs** (sound_runtime 10): `SystemSE_Play(id)` plays one of 9 stereo HE-VAG sounds
  (ids 0..9; L / R hard left / right) at the system SE volume; the UI uses ids 0, 1, 2 almost
  everywhere. These are the only triggers of the 19 vag entries. se_nam sections 0 and 1 are
  never used.
- **Script end** (script 4.1): `END_`, `RTM_`, `RTMN` stop the 20 SE channels and stop / fade the
  BGM (id set to -1) unless the keep-BGM flag is set.
- **History** (sound_runtime 11): every audio command pushes a history record (BGM track and
  volume, the 20 SE ids / volumes / loop flags / pans, the deferred queue), which rewind and
  loads restore; the PS4 forgets the 20-byte SE character array (store it properly).
- Pan law (stereo mixer, VA 0x118658..0x11867F): `gL = min(2(1 - p), 1)`, `gR = min(2p, 1)`.
- Output on PS4: a 48 kHz float stereo port with 1024-sample periods, an 8-channel float port for
  a 5.1 BGM set that is absent from the data (bgm entries n + 131 do not exist: the port forces
  the 2ch setting), a 256-sample stereo port for HE-VAG and one for movie audio. **A PC port
  needs one 48 kHz float stereo mix**; a period of 1024 samples or less.

The slice needs `_SEPL` with `` `001 `` / `` `017 `` (one looping SE fading in over 8,000 ms to 30 %),
`_VPLY` / `_VPL2` / `_VCWT` and no BGM. Open: the menu label of each voice config slot (the
speaker column of sound_runtime 8 is [H]) and the remaining review notes of FORMATS 6.9.

### 4.11 Movie playback via FFmpeg (item 19) - P0, known

Consumes: `image/movie/*.mp4`, the eboot movie table, the prologue remap table.

- Files (aum 9.1): 134 MP4 (`mp42`), H.264 High 4.0 / 4.2, Main 4.1 and Constrained Baseline 4.1,
  1920 x 1080 yuv420p progressive, 30 fps (52 files) or 29.97 fps (82); 79 tagged BT.709 limited
  range, 55 untagged (treat as BT.709 limited). 43 files carry AAC-LC 48 kHz stereo, 31 of those
  are digital silence (treat a silent track like no track: no voice in the mix, no ducking).
- Decoding: FFmpeg (libavformat `mov` demuxer, libavcodec `h264` and `aac`). The PS4 uploads the
  Y and interleaved CbCr planes as two textures and converts in a shader (`basic YCbCr`); the port
  may do the same with yuv420p planes or convert on the CPU.
- Presentation: frame timing from the stream timestamps against the audio clock; at 60 Hz a 30 fps
  frame spans two output frames; 29.97 fps material follows its timestamps. The 60 Hz tick reads
  the audio clock at its **nominal** tick time and applies a **4 ms cadence lock**, or frames get
  1 or 3 ticks instead of 2 (spike S2, `docs/status/spikes/S2.md` 7; ARCHITECTURE 3.4, 7.5).
  Software H.264 decode is the default (S2.md 5).
- Selection (aum 9.3): `_TOMP(id)` passes a language-neutral id remapped by VA 0x5E280: prologue
  4 -> `[4, 46, 47, 106][lang]`; phase titles 8..34 -> +40 en, +67 zc, +99 zt; openings 5, 6 ->
  +97 en, +99 zc (also zt); others unchanged. Then movie table record -> path.
- Script use: `_TOMP(id)` starts the movie (system state 8; id 7 `phaseout` is skipped while the
  fast-forward word is set); `_TOMW()` waits for its end. Ids 0..3 (title loops, played with
  looping), 7 (phaseout) and 38 (TM_CI logo) are played by engine code (4.15).
- Movie audio has its own volume (class volume byte 0x17DA268 + 0xB = config +0x54, a copy of the
  music volume; sound_runtime 2.4); the title passes byte 0x17DA273 / 100 to the player.
- Engine movies (system_flow 3.4, 4.1-4.2): `TM_CI` (id 38, 6.0 s, no loop, once per power-on)
  is skipped by any button; the title plays `titlebg<T>.mp4` (ids 0..3) with loop = 1 and seeks
  once per wrap to 8.0333 s (frame 241), drawing the still TITLE_PARTS child 9 + T until the seek
  lands.
- Open: skip input during script movies (`_TOMP`), and what the player record fields
  `0, 960, 544, 0` / `0.0, 1.0, 1.0` mean.

### 4.12 Save data and ADV state (items 8-11, 22, 36) - P1, known (no retail save checked)

Consumes: own save files (optionally a PS4 `savedata000` for import).

Reference: adv_state (History_Push / History_Restore / slot store and load run under unicorn on
random RAM and on game-made save images, 0 mismatches); engine 5.6 / 7 superseded where they
differ (FORMATS 6.5). Python: `tsuki.misc_adv_system_state` (field maps, `slot_states`,
`parse_history_record`, `check_save_image`, CLI `save`).

- **History ring** (adv_state 2-4): 800 records of 0x3A10 bytes (808 allocated) with the script,
  command offset, kind, BGM / SE state, the F lists of 4.9, the 0x1A08-byte scene_state and a
  committed copy, window state and the voice to replay (field map adv_state 2.1). A record is
  pushed **before** the command it undoes, from 59 call sites (38 live): kind 0 at every `_ZM`
  (page message), 1 once per batch of layer changes (F991 set), 9 at the next commit point
  (`_VPLY`, markers, `_WTTM`, `_MSAD`), 6 / 7 from `_ATSV()` / `_ATSV(1)` (scene start / choice
  save point, both also push the scene stack; `_ATSV(8)` = kind 6 + auto save), 2 / 3 audio,
  12 / 13 for `_SELD` / `_SELR`. Every push also writes the low byte of F995 to scene_state
  +0x19A3.
- **Restore** (adv_state 3): History_Restore copies the record back (scene_state, F lists in the
  order u16, i8, bit, sound, window) and runs a kind-specific tail; with from_history = 1
  (rewind) the target line is re-displayed and execution continues after it; with 0 (slot load,
  previous scene, backlog jump) nothing is re-displayed and the saved command (`_ZM`) runs again.
- **Rewind** (hold action 20; adv_state 5.1): undo records newest-first back to the first
  message of the previous page (kind 0, no blocking effect, F823 <= 1); nothing is re-executed.
  **Next scene** (action 23): see 4.8. **Previous scene** (action 24): restore the scene-stack
  top (300 records), i.e. restart the current scene, or the scene before within the first two
  messages. **Backlog** (action 10): the ring's records of kinds 0 / 8 / 11 / 12, without the
  current page; a jump restores the entry with from_history = 0.
- **Auto / skip / fast-forward** (adv_state 6): auto mode (bit 0 of 0x17DB230, action 13) waits
  `trunc(ms / 1000 * 60)` frames after the message is revealed and the voice has ended, ms =
  config +0x9E on the page's last message (F799 = 1), else +0x2E; skip latch (action 12) and held
  fast-forward (action 19, analog R2) multiply the frame step (64); with config +0x30 = 1
  (default) skipping stops at an unread line (`readmax[block] != 0` and line <= readmax, or
  block >= 0xFB4). Text speed = config +0x20 (unseen) / +0x22 (seen).
- **Save image** (adv_state 7): one 4 MiB file: system block 0x18AC bytes (config 0x140,
  G[0..499], unlock blocks +0x52C picture-seen / BGM-heard and +0x16F4 choice-taken,
  `readmax[4020]`, latest slot), then 140 slots of 0x7108 bytes at `0x1A00 + i*0x7140` (state A =
  the newest page-start record, state B = the scene-stack top record, date; end marker 0x2AF8).
  Slots 0..99 manual, 100..138 an auto-save FIFO (newest first), 139 the quick save (action 14).
  Manual and auto saves set the F1118 bit in state A (so the `_SELA` prompt stays shut after a
  load); the quick save does not. The slot staging is kept current while playing, so saving is a
  copy. "Continue" uses the most-recent-slot field (must be <= 0x8A).
- **Load** (adv_state 7.3): clear F / Z except the saved lists, rebuild the ring and scene stack,
  restore state A without re-display and continue at its command offset, so the page's `_ZM`
  runs again. G and the unlock blocks are system data, not part of a slot (SaveImage_LoadSystem
  replaces G and OR-merges the unlock blocks; when it does which: gallery 2.1 and FORMATS 6.9).
- **Port design**: its own versioned save format with the same logical content (config,
  G[0..499], unlock bit sets, readmax, 100 manual + 39 auto + 1 quick slot with state records and
  local timestamps); store the 20-byte SE character array the PS4 forgets (adv_state 11). A PS4
  importer reads slots with `slot_states()` / `parse_history_record()` and checks them with
  `check_save_image()`; no retail `savedata000` has been examined yet (FORMATS 7 P1 item 9).

Stand-in for the slice (P0): no saving and no history; read tracking in memory.

### 4.13 Input: pad and mouse (item 20) - P0, known (some action roles [H])

Consumes: OS input devices.

- Internal 32-bit button word (engine 8.5): touch 0x1, L3 0x2, R3 0x4, OPTIONS 0x8, d-pad
  0x10 / 0x20 / 0x40 / 0x80 (up / right / down / left), L2 0x100, R2 0x200, L1 0x400, R1 0x800,
  TRIANGLE 0x1000, **decide 0x2000** (CIRCLE, or CROSS when ENTER_BUTTON_ASSIGN == 1), **cancel
  0x4000** (the other one), SQUARE 0x8000, left stick 0x10000..0x80000 and right stick
  0x100000..0x800000 as directions (dead zone, normalised -1..1). The pad manager keeps current,
  previous, pressed and released edges and auto-repeat (initial delay and period in frames).
- Pointer (mouse; touch pad moves it by delta / 3): a cursor in a **640 x 448** logical space,
  clamped to 0..639 x 0..447 on one path and 1..638 x 1..446 on another; frame mapping `x*3`,
  `y*1080/448`. Mouse buttons set pointer flags (newly pressed / held: left 0x2 / 0x8001, right
  0x8 / 0x4, middle 0x20 / 0x10).
- Game code builds a game input word (VA 0xE3A40: held, pressed-or-repeat, newly pressed) and
  tests it with 27 action masks from one of three preset tables (data VA 0xDB84F0; config +0x42
  selects the preset but has no writer, so preset 0 is always used). The actions (adv_state
  10.2): 0 confirm / advance (decide), 1 cancel (also ends latched skip), 2..4 choice-screen
  checks, 6..9 list paging, 10 open the backlog (SQUARE), 11 dialog cancel, 12 toggle latched skip
  (R1), 13 toggle auto (L1), 14 quick save (OPTIONS), 15 quick load (unbound), 16 replay voice
  (unbound), 17 system menu (TRIANGLE), 18 close the backlog, 19 fast-forward (hold R2, analog),
  20 rewind (hold L2), 21 ADV shortcut, 22 close the save / load menu, 23 / 24 next / previous
  scene (unbound in preset 0; system menu / HUD), 25 hide / show the window (cancel); 5 and 26
  unused. Several roles are [H] (marked in adv_state 10.2). The ADV input flow per frame is
  adv_state 8.
- Keyboard and mouse bindings are a port design decision; keep the decide / cancel abstraction so
  that "enter-button assignment" still works. Vibration (two motors) and the light bar are
  optional.
- Stand-in for P0: decide (pad decide, Enter, Space, left click) advances text and confirms;
  cancel (pad cancel, Escape, right click) closes; `_WTTM` waits are timed. How Adv_Update waits
  per message end mode (`@k`, `@e`) and `_WTTM`'s second argument remain open (FORMATS 7 P0
  item 2).

### 4.14 Configuration (item 23) - P0 (defaults) / P1, known

Consumes: config defaults in eboot (data 0x66C578 initial image + code 0x3EAC0), CONF_PARTS UI,
ui_text help lines.

Reference: adv_state 9 (Config_FromMenu emulated for all 37 menu items; boot values =
"Recommended Settings"), sound_runtime 2.4; engine 8.4 superseded where they differ (FORMATS
6.5).

- **The PS4 config screen** (adv_state 9.1) has three tabs: System ("Text Settings", menu page 0,
  items 52..78, 16 rows), Sound (page 1, items 79..190: Music / SFX / System Effects volume +
  mute + test, Play All Voices, Interrupt Voices on Advancing Text, then one row per character:
  volume, mute, test) and Controls (pictures only). Closing it converts the menu to the block,
  applies the volumes and saves the system data if anything changed. engine 8.4's list (system
  voice, BGM 2ch / 5.1ch, vibration, auto quick save options, ...) includes options this build's
  menu does not offer.
- **Fields with a menu item** (adv_state 9.2; boot value in brackets): +0x14 Music volume [40],
  +0x16 Play All Voices (100 on / 0 off) [100], +0x18 SFX volume [40], +0x1A System Effects
  volume [10], +0x20 Unseen Text Display Speed [35], +0x22 Seen Text Display Speed [15], +0x24
  Language (0 ja, 1 en, 2 zc, 3 zt; shipped 0xFFFF = system language, replaced at load) [system],
  +0x2E Auto-Mode Wait Time ms [2200], +0x30 Fast-Forward: read text only [1], +0x3C
  per-character voice on bits (19 used) [0x3FFFFFFF], +0x4E Interrupt Voices on Advancing Text
  [1], +0x76 per-character voice volume u16[19] [50 each], +0x9C / +0x9D Display End of Page
  Instantly (unseen / seen) [0], +0x9E Auto-Mode Advance Page Speed ms [3000], +0xA0 Auto Skip
  Cleared Scenes [0], +0xA2 Show Confirmation Message [0], +0xA3 Text Display Animations [0],
  +0xA4 Seen Text Display Animations [0], +0xA6 / +0xA7 / +0xA8 Music / SFX / System Effects mute
  [0].
- **Fields without a menu item** are constants for a port with their boot values (+0x10 window
  opacity 255, +0x12 window design 0, +0x1C lower BGM during voice 0, +0x1E BGM set 0 = 2ch, +0x26
  / +0x28 outline / shadow 1 / 0, +0x2A text colour mode 0, +0x32 next / previous scene enabled 1,
  +0x36 second voice gate 1, +0x42 action preset 0, +0x54 = copy of +0x14 (movie volume), ...),
  except +0x04 (the stick in ADV) and +0x5A..+0x75 (two persisted RAM records); config +0x40 has
  no effect.
- The runtime volume bytes are derived from these fields (4.10). The port's own config file keeps
  the logical options; P0 uses the boot values above.

### 4.15 System flow: boot, title, New Game, choices (items 24, 32) - P0 (first-boot path) / P1, known

Consumes: allpac3 `CAUTION_<lang>`, `CAUTION1[_xx]`, `CAUTION2[_xx]`; parts.mrg (2..6 save data
damaged, 7 / 8..12 / 13..17 / 18 autosave notice; #1 and #19..23 are used only by dead states);
TITLE_PARTS; SELECT_PARTS; movies 0..3 (`titlebg1..4`, 120 s loops) and 38 (`TM_CI`).

Reference: system_flow (state machines and draw lists from the code; renders compared by eye);
engine 8.1 and aum 7.2 superseded. Python: `tsuki.misc_boot_title_choice_flow` (`boot_sequence`,
draw lists, renderer, `title_return`).

- **Boot** (system_flow 3): system-save states 0..4 (create / check), Game_OpenArchives (one
  frame), the save-damaged notice (only for a damaged save; parts 2 + system language, 45..524
  frames), the autosave notice (parts 8 + language, 95..334 frames), then the title init. There is
  no logo screen. The "Create save data." first-run dialog of engine 8.1 is unconfirmed (FORMATS
  6.9).
- **Title entry** (system_flow 3.4, 4.1), by player state:
  - first boot (prologue bit 485 clear) or a pending start scene: the title is never drawn and New
    Game follows at once -> scene **260** (`00_01`, prologue movie, which ends with `_RTMN` back to
    the title, BGM kept);
  - after the prologue (G[10] = 0): the title with PRESS ANY BUTTON (no menu), then
    `caution_<lang>` (241..720 frames) and `caution1_<lang>` (363..842), then a white fade (73
    frames) -> scene **110** (`START_SCRIPT` -> `00_00`, which sets G[10] = 1);
  - returning player (G[10] != 0): `TM_CI` (once per power-on, any button skips), `caution2_<lang>`
    (1..480 frames, skippable), the title fades in over 120 frames with its BGM, then the menu.
  Caution pictures are drawn full screen on a 960 x 544 canvas with a black cover fading at
  1/60 per frame; a language > 3 uses the `_en` file.
- **Title background** (system_flow 4.2): if G[10] == 0 record 0 shows child 9 (glow layer) and no
  movie; otherwise the state G[2] advances at most one step per title entry (0 -> 1 when bit 560
  is set, 1 -> 3 at bit 383, 3 -> 5 at bit 562), and record 0 = TITLE_PARTS child 9 + T[G[2]]
  (T = {1,2,3,3,2,4}, 6 x i32 at text 0x218A40) with `titlebg<T>.mp4` (BGM 2, or 72 for state 5).
  The movie loops with one seek to 8.0333 s per wrap.
- **Title menu and results** (system_flow 4.3-4.5): the title returns only 0 (stay), 2 (New Game:
  scene 0x2A1B0() if >= 0, else bit 485 ? 110 : 260), 3 (continue the loaded save) and 7 (gallery,
  4.17). The draw list (logo fade +1/120 per frame in, -1/60 out; menu items, moon glow) is
  system_flow 4.5; the particles and shimmer are not modelled.
- **Choice screen** (system_flow 5; vm_variables 4.5-4.6): `_SELR(id, text[, 1])` adds slot k
  (text = `"$"` + prefix line 43960 + k ("1." .. "9.") or 43959 ("0.") for a 10th line, then the
  message; 1 = locked); `_SELD(var, N)` lays the slots out with Text_Layout (width 574 en / 474) on
  a 640 x 344.7 canvas and opens the menu; N is the choice-point number (taken bit, skip stop,
  flowchart marking), not a default cursor (the cursor starts at -1). Phases: fade-in 25 frames,
  reveal, select (up / down wrap and skip locked lines; pointer hover; decide only with a cursor),
  decided (SE 3, only the chosen line drawn, 51 frames). The chosen `_SELR` id goes to the result
  variable and the taken bit is set. Choices are always font text in every language
  (SELECTTXT_PARTS is loaded but never drawn). `_SELS` (mode 2, prompt ui_text 1446 + F[27]) and
  `_SELA` (mode 3, "Do you want to save your progress?", only when F1118 = 0) are two-button
  prompts whose layout is still open.

Stand-in for P0: the first-boot path exactly as above (autosave notice -> `00_01`); the title
stub, cautions and choice screen are P1.

### 4.16 UI screens (items 25, 35) - P1, partial

Consumes: allpac3 UI sets (`*_PARTS[_lang].MRG`), parts.mrg, ui_text, UI registration tables.

- UI sheets are loaded through 0x18-byte registration records (`state, tex_slot, name, child,
  child2`; 21 tables, 220 records; `tex_slot < 0` in the next record ends a table). The
  localising loader turns `conf_parts.mrg` into `conf_parts_<lang>.mrg` (suffix table RELRO
  0x42C030); the child index is clamped to the archive's last child; TITLE_PARTS record 0 and
  SELECTTXT record 1 are rewritten at run time (title state, choice number; aum 7.2-7.3,
  system_flow 4.2), and SELECTTXT is never drawn (system_flow 5.4).
- Every screen sets a virtual canvas (VA 0xDCBE0, e.g. 640 x 448, 960 x 540, 480 x 272) and a
  texel scale (VA 0xD9DD0), then places texture slots with float constants in canvas units:
  `pixel = x / sx`. Button rectangles, atlas sub-rectangles and animations are code constants, not
  data (aum 7.4-7.5). Decoded so far: the config screen (aum 7, adv_state 9), the boot notices,
  title and choice screen (system_flow 3-5, as draw lists). Porting the rest of the UI means
  porting each screen's code from its entry function (HUD 0x56D40 / 0x5A22C, menu 0x6758E /
  0x6CA83, mode panel 0x6E683, save / load 0x6F423, gallery 0x12946 / 0x29B7B / 0x2A3E5;
  gallery 1 names the screen classes).
- UI strings come from `ui_text[lang][id]` (ids 1..1498; `^` = line break).

### 4.17 Gallery and extras (item 26) - P2, known

Consumes: GALLERY_PARTS_* (CG_TH 1,979 and FC_TH 495 thumbnails, menu and screen sheets), the
eboot gallery tables (FORMATS 5.4), the unlock bit sets, extra scenarios (QA_01..QA_25, QACALL,
ARCHIVE 3000).

Reference: gallery (all lists extracted with layouts read from their reader code; the flowchart
column filter run under unicorn). Python: `tsuki.misc_gallery_extras_flowchart`.

- **Menu** (gallery 1): the title's GALLERY item (result 7) opens six items: CG Gallery, Music
  Player, Flowchart (needs scene_seen(1), else ui_text 1497), Movie, Ending List, Title. There is
  no voice gallery.
- **Unlock state** (gallery 2): scene_seen G[110..145] (`_RDST`), scene_done G[300..335] (`_RDED`,
  only when n == F110 and the per-save bit is set), edge_seen G[400..443] (`_JUMP` edges, `_SELD`
  choices), movie_seen G[5] (set when a movie ends), picture_seen bits 0..3199 and bgm_heard
  3200..3719 (system +0x52C; `_MPLY` sets bgm_heard, the layer display paths set picture_seen
  through the seen-index map), choice_taken (system +0x16F4, set by the choice screen).
- **CG gallery** (gallery 4): 458 groups / 1,972 entries; category 0 is always open and
  categories 1..19 open with a chapter's scene_seen bit; an entry unlocks when its category is open
  and its alias (105) or picture was shown, or scene 557 (QA_25) is done. Each 32-byte entry
  names its full picture (NAM search of name + 1; the one-character prefix is never read), its
  CG_TH thumbnail child (1,972 distinct) and the photo-viewer size; the unlock test looks the name
  up in names_sprites (data 0x7A8B10).
- **Flowchart** (gallery 5): 277 columns of 64-byte nodes (495 scene nodes; FC_TH child = scene -
  1; title ui id = 396 + F022); a node is shown when scene_done(scene) or edge_seen(link); the
  column filter of gallery 5.4 decides which columns appear. Line / state drawing is not decoded.
- **Music player** (gallery 6): 95 tracks (bgm, title and credit ui ids, length, loop points,
  variants); a track is unlocked by bgm_heard of it or any variant, or one of 4 special rules
  (OP / ED songs).
- **Movie gallery** (gallery 7): 3 movies; items 1 / 2 need movie_seen bits 0 / 1; locked movies
  are hidden and the list compacted (FORMATS 6.9).
- **Ending list** (gallery 8): 25 "Teach Me, Miss Ciel" entries (3 ending cards, 22 BAD ENDs),
  each unlocked by scene_done of one of two scenes; QA_25 needs all 24 others.
- **Replay** (gallery 3.2): a gallery result r >= 2 sets G[8] = r - 2 and F024 = 1 and starts
  script ARCHIVE (scene 3000), which jumps on G008 directly or through JUMP00..JUMP15 / JUMPQA.
- Dead data a port can ignore: the extra_parts screens (title actions 5 / 6), the legacy CG list
  0x78C810, the legacy music table 0x209510, names_bgm, SDTA / SDTB.

Nothing to implement before P2.

### 4.18 Timing and the frame loop (item 21) - P0, known

- Fixed 60 Hz logic: one step per 16.667 ms, decoupled from the monitor; render the latest state
  (engine 3.3). The PS4 flips at 60 Hz (`sceVideoOutSetFlipRate(0)`); flip modes 30 / 20 Hz exist
  but the game's pacing is per frame.
- Script milliseconds -> frames with the engine's integer formula `frames = (ms * 3) / 50`
  (signed, truncated) for `_WTTM`, `_WTSV`, `_WTTR`; `_WTTM(ms, 1)` is probably skippable by
  input [H].
- Per-frame counters: text reveal (4.7), text and window fades, layer motions (ms-based keyframes),
  transitions, movie clock.

### 4.19 Localisation (item 27) - P0, known

- Language word at config + 0x24 (data VA 0x66C59C): 0 ja, 1 en, 2 zc, 3 zt; >= 4 (or the shipped
  0xFFFF) follows the system language: 0 -> ja, 11 -> zc, 10 -> zt, anything else -> en (VA
  0x407F0). The menu cycles 0..3; Korean (4) is never produced and its assets are placeholders.
- Two readers differ (text 3): layout, fonts, UI, bands and movies use the getter (VA 0x3ED90);
  `$`-expansion and `UiText_Get` read the raw word. A port that keeps the stored value in 0..3
  avoids the difference.
- Per-asset selection is tabulated in FORMATS 5.3 (script text pair, fonts, ruby off in English,
  text box width 574 / 474 and origin, UI sheet suffixes, picture bands, caution screens, parts
  groups, movies, ui_text, choice pictures).
- A language change reloads text, fonts and UI (VA 0x406B0 calls 0xC6980, 0x7C340, 0xC2AF0,
  0x3EB80).

### 4.20 Platform services (item 28) - P3

Trophies (`_TRPY(n)`), PlayGo (always "installed"), Share Play, live streaming, screenshot
watermark (`image/corp.png`), the PS4 save icon and system dialogs are PS4 features. The port may
offer achievements and its own dialogs; nothing here affects gameplay.

---

## 5. Codecs and output

| data | format | decoder in the port | verified |
|---|---|---|---|
| containers | HED8 / HED4 / NAM / MRG / mrgd00 | own (containers 2-4, 7) | 49,620 entries, 1,128,516 section records |
| compression | MZX0 (+ XOR 0xFF for scripts), LV wrapper | own (containers 5-6) | 1,104,762 / 1,104,762 streams |
| pictures | PAL8, PAL4, HEP8, RGB565+LO+A tiles; straight-alpha RGBA8 out | own (images 3-5) | 35,630 headers, 19,823 full decodes |
| text | UTF-8, CR LF terminated, big-endian offset tables | no conversion: glyph keys are UTF-8 bytes | 5 x 43,969 lines |
| fonts | PAL8 1024 x 1024 pages + eboot tables | own (fonts) | 325 pages, 17,423 lookups |
| BGM / SE / voice | RIFF ATRAC9, 48 kHz, mono / stereo, 1024-sample superframes | FFmpeg libavcodec `atrac9` (or any equivalent ATRAC9 decoder) | 17,925 / 17,925 streams |
| system sounds | VAGp HE-VAG, 48 kHz mono | own (audio 6.2); not FFmpeg `adpcm_psx` | 19 / 19 |
| movies | MP4, H.264 1920 x 1080 yuv420p, 30 / 29.97 fps, BT.709 | FFmpeg libavformat `mov` + libavcodec `h264` | 134 files probed |
| movie audio | AAC-LC 48 kHz stereo (43 files, 31 silent) | FFmpeg libavcodec `aac` | 43 tracks |
| PNG | save icon, watermark | not needed | - |

All FFmpeg components named here are part of FFmpeg's LGPL core (no GPL-only component is
needed). The FFmpeg build in use (BtbN `win64-lgpl-shared-9.0`, avcodec 63.1.101) is an **LGPL
version 3** build: `avcodec_license()` returns "LGPL version 3 or later" (spike S1,
`docs/status/spikes/S1.md` 1; ARCHITECTURE 0.5). Licence notices must say v3, not 2.1.

**Video output**: an internal 1920 x 1080 frame (every layout constant in the eboot is for
1920 x 1080), scaled to the window or screen with the 16:9 aspect kept; 8-bit sRGB-encoded colour
(the PS4 scans out A8R8G8B8 sRGB, triple-buffered); logic at 60 Hz.

**Audio output**: 48 kHz, 32-bit float, stereo; mixing in float with clipping only at the device;
a period of at most 1024 samples (21.3 ms).

---

## 6. Priorities

| P | items | what it buys |
|---|---|---|
| P0 | 1-4, 5 (commands of the slice, 7.4), 6, 7, 12, 13 (opening modes), 14 (`_FADS` 0 / 1 / 2), 15, 16, 18 (basic mixer), 19, 20, 21, 23 (defaults), 24 (first-boot path), 27 (selection), 29, 30, 31, 33 | the vertical slice of section 7 |
| P1 | 5 (all), 8, 9, 10, 11, 13 / 14 (all), 17, 18 (all rules), 20 (all actions), 22, 23, 24 (all paths), 25, 27 (all), 32, 35, 36 | a complete, faithful playthrough with saving, menus and choices |
| P2 | 26 | gallery, flowchart, music room, extras |
| P3 | 28, 34, build independence (3.3), scan-mode load fallback (36), Korean, LV lip-sync | optional |

The prioritised open problems behind these items are FORMATS section 7.

What the follow-up documents add to P0, i.e. what the slice must take from them (the rest of
each document is P1 or later):

| document | P0 for the slice | later |
|---|---|---|
| vm_variables | `_WKST` / `_WKAD` operand rules (`C<n>`), `_IF__` jumping on false (4.9) | choice commands, `_GOTS`, history F lists |
| layers | scene_state, commit at `_FADS`, `_STCH` / `_STCP` / `_STRT` / `_STGS` / `_STZ4` / `_STCC`, motions with easing (22523..22526), projection without parents (4.5) | shakes, masks, crops, parents, variants, loops |
| compositing | blend modes 11 / 13 / 17 / 19 / 24, normal drawing with straight alpha, `_FADS` types 0 and 1 (4.6) | rule wipes, masked layers, `_STEC` / `_SNX2` / `_SNX3`, `_FADE` |
| system_flow | the first-boot path: autosave notice, then New Game -> 00_01 (4.15) | cautions, title, choice screen |
| sound_runtime | `_SEPL` with `` `001 `` / `` `017 `` and the per-frame ramp, `_VPLY` gate at `_ZM`, `_VPL2`, `_VCWT`, stop on advance (4.10) | BGM, deferred SEs, ducking, system SEs |
| adv_state | config boot values for text speed and voice options (4.14) | history, saves, rewind, backlog, auto / skip, actions |
| gallery | nothing | everything (P2) |

---

## 7. First vertical slice

### 7.1 Goal

Prove the whole chain on the opening of the game: locate and verify the user's data -> archives
-> eboot tables -> script VM -> a movie -> a scripted scene with background, text box and voice.
The slice ends when page 1 of `00_02` is on screen with its five messages and their voices.

The scripted scene is `00_02` (scene id 270), the scripted version of what the prologue movie
shows. Reasons: the movie frame at 25 s is a ready reference picture of page 1 (FORMATS 5.6,
textbox 10.1); page 1 has a background, voiced messages and an SE loop; it is the worked chain of
FORMATS 5.6. The retail flow never reaches 00_02 (only the debug menu JUMP00 does), so the slice
enters it with a developer option after the movie; section 7.6 adds the retail path.

### 7.2 Flow

1. **Start**: the user points the runtime at the CUSA47999 folder; build check (3.3); extract the
   eboot tables of 3.2 needed below; open allpac0/1/1_2/3/4, voice, vse + se_nam, bgm, allscr,
   script_text, ft_<lang>. Language from the config file or the system mapping (4.19).
2. **Boot** (4.15; system_flow 3.4 "first boot"): create the system data, show the autosave
   notice (parts.mrg 8 + language text, 7 spinner, 13 + language "Saving...", 18 glow; 45-frame
   fades, a hold of 1..240 frames that the skip input ends: game input bits 0x860 or a new left
   click, system_flow 3.3), then start New Game at once:
   on a first boot the title is never drawn. The cautions and the title belong to later boots
   (P1).
3. **New Game** on fresh system data (bit 485 clear) -> scene 260 = script 2 `00_01`. The script
   has 129 statements; the executed trace has 99 steps (golden `trace.steps`, which follows
   `_IF__` with `tsuki.vm_vars`):
   - the preamble: the markers `_ZZ01901`, `_ZZ01a01(START)`, `_PGST(-1)`, `_PGST(0)`,
     `_ZZ01b01(LBL_000)`, `_PGST(10000)`, `_WKST(F023,eq,2)`, `_WKST(F022,eq,2)`,
     `_RDST/_RTST/_RDAS(485)`, 25 `_WKST` writes (F823 and F847..F869 = 0, F822 = 1),
     `_WNTY(0)`, `_CLO2`, `_WKST(F1104,eq,1)` (statements 0..38);
   - **the skipped block.** Statement 39 is `_IF__(F026,ne,0,LV00000)`. `_IF__` jumps to its
     label when the condition is false (vm_variables 4.3), and F026 is 0 on a normal New Game
     (vm_variables 4.5, the save-prompt block), so execution jumps to the marker
     `_ZY01c01(LV00000)` (statement 69) and **statements 40..68 never run**: `_STCC(-1)`, the
     first `_FADS(1,0,0)`, and the resets `_WKST(F026,eq,0)`, F823, F847..F869,
     `_WKST(F1118,eq,1)` and `_WKST(F028,eq,0)`;
   - then `_STCP(1,1)`, `_STCH(0,img0597,258-18,154-19,...)` (black, allpac0 #166), `_STRT`,
     `_STGS`, `_STCP`, `_FADS(1,0,0)`, `_STGS`, `_STCF(0,8)`, `_STCC(1)`, `_STCP(0,2)`,
     `_FADS(1,0,0)`, `_WTTM(1000,0)` (60 frames), **`_TOMP(4)`** -> `prologue[_en|_zc|_zt].mp4`
     (ids 4 / 46 / 47 / 106; 384.3 s, en 388.7 s; H.264 High 4.0, 30 fps, AAC with sound),
     `_WTTM(2000,1)`, `_STCH(0,BLACK,...)`, `_FANX` (no-op), **`_TOMW()`** (until the movie
     ends), 39 x `_STCG` (picture-seen bits), `_RDED(485)`, **`_RTMN`** (keep-BGM flag + return
     to title; statement 127, offset 0x7D4).
   - **The script's last statement, `_END_` (statement 128), never runs:** `_RTMN` returns to
     the title first (script.md, RTMN row). The executed trace ends at `_RTMN`.
4. **Developer entry**: load scene 270 = script 3 `00_02` and run it from the start:
   - the same `_IF__(F026,ne,0,LV00000)` stands at statement 39 and F026 is 0, so `00_02` skips
     statements 40..68 as well (the same `_STCC(-1)` / `_FADS(1,0,0)` / reset block, 29
     statements). Its executed trace up to and including `_PGST(2)` (statement 268, offset
     0x13A5) has 240 steps, not 269;
   - page 0: black backdrop, the text picture img2458 ("I awake suddenly") faded in and out by
     motions 22523 / 22524 with voice `_VPL2(B90_00_01_0000_0,03d00)` and `_VCWT()`; img2182 on
     layer 4 with `_STBR(4,24)`; img0597 on layer 5 (motion 22525: alpha 255 -> 0 over 14 s);
     **img1685** (dark tatami room, catalog 1692 -> allpac0 #270) on layer 6 with
     `_STZ4(6,1200,...)` and motion 22526 (fields 9 / 10 = zoom 1200 -> 912 per mille over 70 s
     with easing 3, 1,074 at 17.5 s; field 27 = z 162 [V: layers 8.3, `--motion 22526`]); further
     layers 7..19 set up at alpha 0; `_FADS(0,10,0)`; SE loop
     `_SEPL(7,1,MOON_SE03028,,`001:8000,,`017:30)` (vse #598); `_WTTM(4000,1)`; `_CLO5(2000)`;
     messages 0, 1, 2 with voices 0x3D01..0x3D03 (`_VPLY` before each `_ZM`).
   - `_PGST(1)`, `_WTTM(400,1)`, then page 1: messages 3..7 (`$000003` .. `$000007`; line 3 is
     the 70-character opening line of `text.md` 2.3), each preceded by `_VPLY`
     with voice entries 0x3D04..0x3D08 (15,620..15,624); `_WNTY(1)` marks message 7 as the last
     of the page.
   - The window picture is not drawn (00_02 sets F1104 = 1): the text sits directly on the scene.
5. Decide advances each message (stand-in for the wait model, 4.13; the per-end-mode waits are
   still open, FORMATS 7 P0 item 2); the voice of a message stops when the text advances (config
   +0x4E, 4.10).

### 7.3 Data touched

| data | entries |
|---|---|
| eboot | picture-name list, catalog, scene ids, movie table, prologue remap, motion tables (motions 22523..22526), font tables, layout tables, window tables, config defaults |
| allscr.mrg | sections 0 and 1 (names, label tables), 5 (script 2 `00_01`) and 6 (script 3 `00_02`); script k is section k + 3 |
| allpac0 | **18 pictures**: #166 img0597, #0 BLACK, #270 img1685, #358 img2458, #304 img2182, #370..#374 img2629..img2633, #353 img2345, #305 img2183, #315 img2230, #194 img0813, #317 img2232, #920 img0700_06_06 (catalog 5330), #356 img2439, #302 img2163. That is the 17 that `00_02` displays plus `BLACK` of `00_01` (`tools/oracle/export_slice.py`); the 39 `_STCG` names of `00_01` are only picture-seen bits and are not displayed |
| parts.hed / parts.mrg | #7, #8 + language, #13 + language, #18 (autosave notice of the first boot) |
| allpac3 | not needed on the first-boot path (the cautions #71..#74 `CAUTION_{JA,EN,ZC,ZT}` and `caution1` / `caution2` follow later boots, 4.15) |
| script_text.mrg | language pair 2L / 2L+1, lines 0..7 |
| ft_<lang>.mrg | all 65 pages of the selected language |
| voice.hed / voice.mrg | entries 0x3D00..0x3D08 (15,616..15,624), ATRAC9 mono 48 kHz |
| vse.mrg, se_nam.mrg | `MOON_SE03028` -> section 2 record 598 -> vse #598 (stereo, 346,920 bytes) |
| movies | `prologue.mp4` / `_en` / `_zc` / `_zt` |

Section 3 (START_SCRIPT) is needed only for the retail path of 7.6.

### 7.4 Commands the slice must execute

Measured with `tsuki.script` on the retail scripts: the statement list of `00_01` has 25 names
and that of `00_02` up to `_PGST(2)` has 30. **24 of the 25 names of `00_01` execute**: `_END_`
(statement 128) is the one that never runs, because `_RTMN` ends the scene first (7.2 step 3).
All 30 of `00_02` execute. The 35 distinct names that execute in the two scripts are the list
below without `END_`:

- markers and flow: `ZZ`, `ZY`, `ZM`, `PGST`, `IF__`, `END_`, `RTMN`;
- variables and bookkeeping: `WKST`, `WKAD`, `RDST`, `RTST`, `RDAS`, `RDED`, `STCG`;
- window and text: `WNTY`, `CLO2`, `CLO5`;
- pictures: `STCC`, `STCH`, `STCP`, `STRT`, `STGS`, `STCF`, `STZ4`, `STBR`, `STMA`, `FADS`;
- sound and movies: `VPLY`, `VPL2`, `VCWT`, `SEPL`, `TOMP`, `TOMW`;
- waits: `WTTM`;
- no-ops: `FANX`, `SNX4`.

What the slice implements from the follow-up documents (the former stand-ins are no longer
needed): `_STCP` = z / draw order and `_STCF` stored without visible effect (its reader is
unknown; layers 4.5); `_STBR` with the formulas of compositing 2.3 for modes 11, 13, 17, 19, 24;
motions with the interpolation and easing of layers 8.4 / 8.5 (motion 22526 uses easing 3, not a
linear ramp); `_SEPL` tags `` `001 `` = linear per-frame fade-in and `` `017 `` = volume
(sound_runtime 3.4, 6.2); `_VPLY` / `_VPL2` / `_VCWT` per sound_runtime 7. Still a simplification:
waits advance on decide or their timer (FORMATS 7 P0 item 2).

### 7.5 Acceptance checks

Golden data are generated locally from the user's copy with the Python reference tools
(`tools/tsuki`) and never committed (3.4).

1. **Data**: the build check of 3.3 passes; the archive counts of FORMATS 2 are reproduced.
2. **Script**: the executed statement sequence of `00_01` and of `00_02` up to `_PGST(2)` equals the
   **executed trace** of the golden slice, with the same argument values from both readers. The
   trace is `trace.steps` of `samples/golden/slice/scripts/script_0002.json` (99 steps, the last
   one `_RTMN` at offset 0x7D4) and `script_0003.json` (240 steps, the last one `_PGST(2)` at
   offset 0x13A5), written by the oracle runner from `tsuki.vm_vars`, which follows `_IF__` and
   `_GOTO`. **Do not compare against the full statement list** of `tsuki.script`
   (`python -m tsuki.script dump 3 --lang en`; 129 statements in `00_01`, 269 in `00_02` up to
   and including `_PGST(2)`; the dump abbreviates runs of `_WKST`): both scripts skip statements
   40..68 (`_IF__(F026,ne,0,LV00000)` jumps when F026 is 0, 7.2 steps 3 and 4), and `00_01`'s
   `_END_` never runs. The dump remains the source for the statement names, offsets and
   arguments of the steps that do run.
3. **Movie**: the prologue of the selected language plays to its last frame with audio in sync
   (within one frame); `_TOMW` returns after the last frame; changing the language selects ids
   4 / 46 / 47 / 106. Spike S2 measured this on `prologue_en.mp4`: 0 dropped, 0 late, every
   source frame shown for exactly 2 ticks at 60 Hz, offset -2.08 ms at the last frame (4.11;
   ARCHITECTURE 3.4); the M2 run repeats it with the real movie thread.
4. **Pictures**: img1685 resolves to catalog 1692 -> allpac0 #270 and decodes pixel-identical to
   `python -m tsuki.image allpac0 270`; the catalog anchor lands on (240, 136) in script space.
5. **Text**: messages 3, 4, 5 lay out equal to `tsuki.textbox` (records, line breaks
   `... hangs / in the air.` and `... like layers of / grime.` in English); compared with the
   frame of `prologue_en.mp4` at 25 s, glyph x positions are within 3 px (textbox 10.1). The known
   vertical difference (81 px movie line pitch vs 78 px engine) is not a slice failure.
6. **Voice**: `_VPLY(B90_00_01_0001_0,03d04)` plays voice entry 15,620 (`voice.nam[15620]` =
   `B90_`), ATRAC9 mono, about 2.25 s, starting together with message 3.
7. **SE**: `MOON_SE03028` (vse #598) loops seamlessly at its inner or whole loop, fading in over
   8,000 ms to 30 % (a linear ramp of `30 * 1000 / 60 / 8000` points per frame, 480 frames; the
   channel records equal `SoundModel`'s, sound_runtime 4).
8. **Timing**: `_WTTM(4000,1)` lasts 240 frames; the reveal advances 0.476 counters per frame at the
   default text speed 35.
9. **Composition**: scene_state after every statement equals
   `tsuki.misc_layer_state_and_motions.SceneModel`; the frame of page 1 matches the Python
   reference compositor (`tsuki.misc_layer_state_and_motions` projection + `tsuki.compose` blend
   formulas) and, by eye, the 25 s movie frame (room, zoom, vignette). Remaining differences point
   to the [H] items of compositing 14 (FORMATS 7 P0 item 3) or to the text vertical scale.

### 7.6 Next step after the slice: the retail path

`00_01` ends with `_RTMN`: back to the title with the BGM kept. With bit 485 now set and G[10] = 0
the title shows PRESS ANY BUTTON (no menu); after a button come `caution_<lang>` and
`caution1_<lang>`, a 73-frame white fade and New Game -> scene 110 = `START_SCRIPT` (section 3:
about 70 variable initialisations, `WHITE`, `_JUMP(240,START)`) -> `00_00` (hospital; first line
id 107, 43 characters, starts `I wake`; it sets G[10] = 1, so later boots take the
returning-player path of 4.15). Up to its first voiced line (offset 0x2341,
`_VPLY(D10_00_00_0000_0,03fb5)` -> voice #16309) `00_00` uses 37 statement names, adding `ATSV`,
`CLO3`, `MSAD`, `SCH2`, `SEFD`, `SEVL`, `SHAF`, `SHS2`, `SQK2`, `SQS2`, `SSS2` to the slice's set, and
the blur variant img1696_11_11, `_STBR(4,23)` and motions 1..5 (no anchor fields, no loops). The
shake commands need the per-frame quake / shake / swing formula, which is still open (FORMATS 7
P1 item 6); `_ATSV()` pushes the scene-start history record (4.12).

---

## 8. Follow-up research (formerly pending, delivered 2026-10-25)

The seven topics marked pending research in the first version now have their documents (FORMATS
1.1 / 1.2). The runtime sections above use them; this table says what each delivered, how it was
verified and what it left open.

| area | document (Python model) | runtime sections | delivered | verification | left open (FORMATS 7 / 6.9) |
|---|---|---|---|---|---|
| VM variables | `formats/vm_variables.md` (`tsuki/vm_vars.py`) | 4.9, 4.8, 4.12 | operand forms per command and position, `C<n>`, `_D<n>`, `_IF__` / `_GOTS` / `_SEL*` semantics, F1118 save prompt, scan mode, history F lists; engine.md 5.5 fixed | engine vs model under unicorn: 61,000 fuzz cases, every retail choice statement, operand table, first-byte sweep; 0 mismatches | `_SELD` per-line loop (F799, kind-0xC snapshot); quick save F1118; short `_D` targets |
| Layers and motions | `formats/layers.md` (`tsuki/misc_layer_state_and_motions.py`) | 4.5, 7.4 | scene_state map, field map of every layer command, commit, per-frame update, projection, all 27 motion fields with easing, variants, loops | scene_state byte-exact after 50,000+ statements; 82,562 motion calls; commit 36,000 fields | quake / shake / swing formula, crop / mask pass, `_STRA`, `_STCF` reader; projection residual for motion-animated anchors; static loop-switch decoder |
| Compositing | `formats/compositing.md` (`tsuki/compose.py`, `gcn.py`) | 4.6 | 35 shader formulas, blend states, draw effects, `_STBR`, `_FADS` timing / capture / rule wipe / rule slot, `_FADE`, `_STEC` / `_SNX2` / `_SNX3` | shader formulas = interpreted GCN code (35 / 35); capture path emulated; recolour 756 / 756 bit-exact | masked-layer selection, engine-only transition types, `_FADE` effect 4, frame-start [H] |
| System flow | `formats/system_flow.md` (`tsuki/misc_boot_title_choice_flow.py`) | 4.15, 4.11, 4.16 | boot list for four player states with frame counts, title init / background / return table / logo fade, choice screen phases, input and draw list | tables re-read in tests; renders viewed; choice census | `_SELS` / `_SELA` prompt layout, title particles, first-run save dialog |
| Sound runtime | `formats/sound_runtime.md` (`tsuki/misc_audio_runtime_semantics.py`) | 4.10 | channel model and records, every command and tag, deferred queue, ramps, gains, voice pipeline, 19 voice slots, ducking, system SEs; audio.md corrected | engine vs model on the whole corpus (22,037 statements, 32,551 frame updates), skip / fast-forward replays; 0 mismatches | voice slot menu labels; `_VPL2` gate, scan-state History_Push, skip voice condition (model) |
| ADV state | `formats/adv_state.md` (`tsuki/misc_adv_system_state.py`) | 4.12, 4.13, 4.14, 4.8 | history record, push sites and kinds, `_ATSV`, restore, rewind, previous / next scene, backlog, auto / skip / fast-forward, save slots and load path, config map, 27 actions | History_Push / History_Restore / slot store and load emulated, 0 mismatches; 398 `_ATSV` classified | a retail save; kind 1 / 9 record pointers; some action roles [H] |
| Gallery | `formats/gallery.md` (`tsuki/misc_gallery_extras_flowchart.py`) | 4.17 | every gallery list and unlock rule, flowchart, music player, movie gallery, ending list, replay path, dead tables | all records checked on retail data; column filter emulated on 42 states | flowchart line drawing, `_JUMP` special switch; 8 review notes (load merge, movie list, `_STCG` clamp) |

---

## 9. Other open items

1. Text vertical scale: 3.115 px per unit in the prologue movie vs 3.0 from the engine constants
   (trace display list 0x1E, VA 0xDCB20, texture mode byte at 0xE1F4C) - P0 calibration.
2. The wait model per message end mode (`@k`, `@e`), `_WTTM`'s second argument, skip input during
   script movies - P0 stand-in, P1 exact.
3. Appearance of `@g`, `@z`, `@b`, `@a`; RGB565 vs BGR565 channel order of colour words; outline
   colour table 0xDB7BB0; previous-message dimming - P1.
4. Catalog `kind` 0x0B, `draw_flags`, `char_id`, `layer_kind` 2 / 3 (the anchors, `scale_class`
   factors and the thumbnail framing offsets are now explained: layers 7, 10) - P1 / P2.
5. Per-screen UI layout beyond the config, title, boot-notice and choice screens (4.16) - P1.
6. A real `savedata000` (read-only copy) to check the save layout (`python -m
   tsuki.misc_adv_system_state save <copy>`) - P1.
7. The quake / shake / swing formula, the crop / mask pass, the motion loop switch without
   emulation, `_STCF` / `_STRA` / `_SPFC` / `_SPRD` / `_SPRC` effects (FORMATS 7 P1) - P1.
8. The menu label of each of the 19 voice config slots (sound_runtime 8) - P1.
9. The version-string disagreement of 3.3 (param.sfo 01.00 vs "app 01.01" in the documents) - P3.
10. Build independence (pattern-based locator), Korean stubs, LV lip-sync (placeholder only) - P3.

---

## 10. Changes against the draft

- Merged with every format document; added the vertical slice (7), codecs and output (5), the data
  files (3.1), build verification (3.3), data policy (3.4) and per-subsystem requirements (4).
- Item 4: the 1000-segment budget is per ScriptStep call, not per frame; the fast-forward loop calls
  ScriptStep repeatedly in one frame (engine 3.3).
- Prefetcher: 43 entries (41 names + `J***` + `ZY**`), the V case accepts only `VPLY` / `VPL2`
  (FORMATS 6.5); the draft said 42 with a `V***` wildcard. It is optional (item 34).
- Item 5: 98 statement names in use (94 commands + 4 marker kinds; the draft said 94 names), 6
  no-ops; `_RTM2` is handled (keep-BGM flag), `_RTMN` is not a plain alias of `_RTM_`.
- Item 6: the `C<n>` operand (`F[F823+n]` as a write target) and the `_D<n>` forms added
  (FORMATS 6.5); first marked pending research, now known (vm_variables; section 12).
- Items 13, 14: the opening's blend modes and `_FADS` types raised to P0 (the slice needs them).
- Item 18: FORMATS 6.6 applied (same-track `_MPLY` ignored, negative track stops, gain truncation,
  float headroom 1.878, three-argument `_VPLY`).
- Item 19: movies raised to P0 (the slice plays the prologue).
- Item 20: pointer clamp 0..639 x 0..447 (640 x 448) kept from the corrected draft; action masks
  first marked pending research, now named (adv_state 10; section 12).
- New items 29-36 (composition, fonts, motions, choice screen, window pictures, prefetcher, UI
  framework, scan mode).
- The first version marked seven areas pending research with a pointer to FORMATS 1.2; section
  12 records how they were filled in.

## 11. Changes after the M0 review (package L2, 2026-10-25)

- 7.2 step 3 and step 4: `_IF__(F026,ne,0,LV00000)` (statement 39) jumps when F026 is 0, so
  both `00_01` and `00_02` skip statements 40..68 (`_STCC(-1)`, the first `_FADS(1,0,0)`, the
  resets); `00_01` ends at `_RTMN` (offset 0x7D4) and its `_END_` never runs. Sources: the golden
  slice (`trace.skipped` = [[40, 68]], `trace.end` = `RTMN`), vm_variables 4.3 and 4.5,
  `tools/oracle/export_slice.py` (`if_block_skipped`, `script2.end`), `docs/status/followups.md`.
- 7.3: the allpac0 row says 18 pictures (the 17 of `00_02` plus `BLACK` of `00_01`).
- 7.4: `00_01` lists 25 statement names and 24 of them execute.
- 7.5 check 2: compare against the executed trace (`trace.steps`: 99 and 240 steps), not the
  full statement list.
- 7.5 check 3, 4.11: the 60 Hz tick reads the audio clock at its nominal time with a 4 ms cadence
  lock (S2).
- Section 5: the FFmpeg build in use is LGPL v3 (S1, decision 10).

## 12. Changes after the follow-up documents (2026-10-25)

All seven "pending research" topics now have their documents (vm_variables, layers,
compositing, system_flow, sound_runtime, adv_state, gallery; FORMATS 1.1 / 1.2), and every
pending marker is replaced by the documented behaviour:

- Checklist items 6, 9, 10, 11, 12, 13, 14, 18, 20, 22, 23, 24, 26, 31, 32 and 36: references,
  knowledge and acceptance checks now cite the follow-up documents; item 36 is now "save resume
  by History_Restore", because slot loads do not use scan mode (adv_state 7.3).
- 3.2: new tables (motion flag words, voice groups, action presets, config pages, system SEs,
  the gallery tables); the rule-mask names, the legacy music table, the BGM names and the legacy
  CG list are dead; the motion loop points are a compiled switch.
- 4.5 / 4.6: the layer, motion, blend and transition behaviour of layers and compositing
  replaces the stand-ins (linear motion interpolation, Photoshop / W3C formulas).
- 4.8: the "fast-forward" loop is the next-scene skip; scan mode is only a load fallback.
- 4.9, 4.10, 4.12, 4.13, 4.14, 4.15, 4.17: rewritten from vm_variables, sound_runtime, adv_state,
  system_flow and gallery. Corrections against the first version: once the config is applied the
  class volume bytes are 40, 40, 100, 40, 10 (movie, BGM, voice, SE, system SE; +0xD is the voice,
  not the SE volume; the 75 in the data image is overwritten);
  there are 19 per-character voice slots, not 20 or 23; there is no logo screen; the cautions are
  not part of the first boot; choices are text in every language; there is no voice gallery.
- 6: P0 / P1 split per follow-up document; 7.2 step 2 is now the retail first-boot path
  (autosave notice -> `00_01`), 7.3 needs parts.mrg instead of the cautions, 7.4 lists what the
  slice takes from each document, 7.5 checks 7 and 9 compare with the Python models, 7.6 follows
  the retail title path after the prologue.
- 8: the pending-research table is replaced by a table of what each document delivered and left
  open; 9 is renumbered.
