# Format map: TSUKIHIME -A piece of blue glass moon- (PS4 CUSA47999, app 01.01)

This is the entry point to the reverse-engineering results. It indexes the per-format documents
in `docs/formats/`, gives the status of every data format and engine table, summarises each one
in a paragraph, shows how the pieces reference each other, and lists the open problems in
priority order. The runtime-side view (subsystems, codecs, vertical slice) is in
[`RUNTIME_REQUIREMENTS.md`](RUNTIME_REQUIREMENTS.md).

Snapshot: 2026-10-25. First written about 12:00 local time, while seven follow-up ("mop-up")
investigations were still running; revised the same day after all seven delivered their
documents (section 1.2 now lists none outstanding). Review and re-check findings against those
new documents that their authors did not apply are collected in section 6.9; follow 6.9 where
it contradicts a document.

Conventions used here:

- **Status**: *decoded* = byte layout and algorithm complete and checked on all retail data;
  *partial* = structure known, some semantics open; *code-only* = a Python module and passing
  tests exist but the format document was not delivered; *open* = not analysed.
- **Confidence**: high = every claim backed by engine code (eboot VA) and/or a full-data census;
  medium = core verified, details hypothetical; low = mostly hypotheses.
- Addresses are eboot.bin VAs: text VA = file offset - 0x2280; RELRO VA 0x400000 = file 0x32C210;
  data VA = file offset - 0x365FA0 + 0x600000.
- Module paths are relative to `tools/`; doc paths relative to `docs/`; sample paths relative to
  the project root.

---

## 1. Index

### 1.1 Delivered documents

| document | scope | Python reference | status |
|---|---|---|---|
| [formats/containers.md](formats/containers.md) | HED8 / HED4 indexes, NAM32 / NAM8, MRG data, mrgd00 (MZP), MZX0, LV voice wrapper, standalone mrgd00 files, inventory | `tsuki/mrg.py`, `nam.py`, `mzp.py`, `mzx.py`, `sniff.py`, `inventory.py` | decoded, repaired after review |
| [formats/images.md](formats/images.md) | image MZP header, PAL8 / PAL4 / HEP8 / RGB565+LO+A, tiles and borders, tile flags, sprite layers, the eboot image catalog, language bands, picture-name tables | `tsuki/image.py`, `image_catalog.py` | decoded, repaired |
| [formats/text.md](formats/text.md) | script_text.mrg, `$` references, `_ZM`/`_MSAD`/`_SELR`/`_ZD`, `$`-expansion, markup byte rules, ruby, icons, text shown as images | `tsuki/text.py`, `text_refs.py` | decoded, repaired |
| [formats/script.md](formats/script.md) | allscr.mrg storage, script language, both argument readers, dispatch, markers, command catalogue, coordinates, cross references, entry point and scene graph | `tsuki/script.py`, `script_tables.py`, `script_emu.py` | decoded, repaired; command semantics now superseded per area by layers / compositing / sound_runtime / vm_variables / adv_state / system_flow (errata 6.3) |
| [formats/audio.md](formats/audio.md) | ATRAC9 RIFF (bgm / vse / voice), HE-VAG (vag), se_nam.mrg, naming, loops, engine playback | `tsuki/audio.py`, `audio_hevag.py` | decoded; the six review findings are applied (its section "Corrections"); playback semantics are in sound_runtime.md, which lists further errata (6.6) |
| [formats/fonts.md](formats/fonts.md) | ft_<lang>.mrg pages, glyph lookup / rectangle tables in eboot, tags, joined dashes, pass-2 element loop | `tsuki/font.py`, `font_eboot.py` | decoded, repaired |
| [formats/textbox.md](formats/textbox.md) (alias [misc_adv_textbox_layout.md](formats/misc_adv_textbox_layout.md)) | message window and main text box: `Text_Layout` pass 1 and 2, kinsoku / English wrap, ruby placement, block draw, page stacking, window pictures, reveal / fade / wait model | `tsuki/textbox.py` (`misc_adv_textbox_layout.py` re-exports it) | decoded (layout bit-exact), one unresolved vertical-scale question |
| [formats/archives_ui_movies.md](formats/archives_ui_movies.md) | archive roles, NAM name search, script image lookup, image table, allpac2, allpac3 UI sets, parts.mrg, UI texture registration tables, canvases, loose PNGs, movies and language remap | `tsuki/catalog.py`, `catalog_eboot.py` | data decoded; per-screen UI layout is code (partial) |
| [formats/engine.md](formats/engine.md) | eboot.bin SELF/ELF, imports and NIDs, threads, timing, renderer and shaders, script VM tables, prefetcher, variables, history ring, archive slots, save image, boot, config, input, audio ports, UI text table | `eboot/*.py` | partial; the two major review errors are fixed in place (5.4, 5.5, also 7.2); remaining errata in 6.5. Boot (8.1), config (8.4), save (7), history (5.6) and shaders (4) are superseded by system_flow / adv_state / compositing |
| [formats/vm_variables.md](formats/vm_variables.md) (alias [misc_vm_variables_errata.md](formats/misc_vm_variables_errata.md)) | variable arrays, operand forms per command and argument position (`F` `G` `C` `_D` `Z`, literals, tags), read / write order of `_WK*` / `_IF__` / `_GOTS` / `_SELR` / `_SELD` / `_SELA` / `_SELS` and the choice result stores, the F1118 save-prompt gate, scan-mode behaviour, which F values a history record keeps, corpus census | `tsuki/vm_vars.py` (model), `misc_vm_variables_errata.py` (unicorn harness) | decoded, repaired after review; the re-check found the `_SELD` per-line loop unmodelled (6.9) |
| [formats/layers.md](formats/layers.md) (alias [misc_layer_state_and_motions.md](formats/misc_layer_state_and_motions.md)) | scene_state map, argument -> field map of every retail layer command, commit, per-frame update, tweens, motions (27 keyframe fields, easing, picture variants, loops), projection to the frame | `tsuki/misc_layer_state_and_motions.py` (+ `_emu`, `_vex`) | partial (quake / shake / swing, crop / mask pass, STRA open), repaired; the re-check found the quad formula wrong for motion-animated anchors (6.9) |
| [formats/compositing.md](formats/compositing.md) (alias [misc_compositing_transitions_shaders.md](formats/misc_compositing_transitions_shaders.md)) | the 35 pixel shaders decoded from GCN code, 14 blend states, draw-effect words, `_STBR`, display lists, `_FADS` timing / capture surfaces / rule wipe, `_FADE`, the `_STEC` / `_SNX2` / `_SNX3` CPU recolour | `tsuki/compose.py`, `gcn.py`, `misc_compositing_transitions_shaders.py` (+ `_render`) | decoded, repaired; minor re-check notes (6.9) |
| [formats/system_flow.md](formats/system_flow.md) (alias [misc_boot_title_choice_flow.md](formats/misc_boot_title_choice_flow.md)) | boot states and notice screens, title init / state machine / background rule / return table / logo fade, the `_SELR` / `_SELD` choice screen (phases, input, draw list) | `tsuki/misc_boot_title_choice_flow.py` | decoded, repaired; `_SELS` / `_SELA` prompt layout open |
| [formats/sound_runtime.md](formats/sound_runtime.md) (alias [misc_audio_runtime_semantics.md](formats/misc_audio_runtime_semantics.md)) | channel model and records, every audio command and tag, the deferred-SE queue, per-frame update, gains and fades, voice pipeline and stop-on-advance, voice group -> config slot table, BGM ducking, system SEs / vag triggers, se_nam use | `tsuki/misc_audio_runtime_semantics.py` (+ `_emu`, `_vex`, `_verify`) | decoded; 8 minor review findings not applied (6.9) |
| [formats/adv_state.md](formats/adv_state.md) (alias [misc_adv_system_state.md](formats/misc_adv_system_state.md)) | history record, the 59 push sites and record kinds, `_ATSV`, History_Restore, ring, rewind, previous / next scene, scene stack, backlog, auto / skip / fast-forward, save slots and the load path, config field map, the 27 input actions | `tsuki/misc_adv_system_state.py` (+ `_emu`) | decoded (medium: no retail save checked), repaired; minor re-check notes (6.9) |
| [formats/gallery.md](formats/gallery.md) (alias [misc_gallery_extras_flowchart.md](formats/misc_gallery_extras_flowchart.md)) | CG gallery, flowchart, music player, movie gallery, ending list, unlock bit sets, gallery replay path, dead legacy tables | `tsuki/misc_gallery_extras_flowchart.py` (+ `_emu`, `_sheets`) | decoded; 8 minor review findings not applied (6.9) |
| [RUNTIME_REQUIREMENTS.md](RUNTIME_REQUIREMENTS.md) | what the native runtime must implement, codecs, output, vertical slice | - | merged from the engine draft and all format findings, including the seven follow-up documents |
| [../samples/inventory/summary.md](../samples/inventory/summary.md) | per-archive inventory (generated by `tools/inventory.py --deep`) | `inventory.py` | regenerated 2026-10-25 07:19 |

The engine agent's draft `docs/RUNTIME_REQUIREMENTS.draft.md` is superseded by
`RUNTIME_REQUIREMENTS.md` and kept only as history.

### 1.2 Documents not delivered

None. The seven documents that were outstanding at the first snapshot arrived on 2026-10-25;
they are indexed in 1.1, the coverage matrix (3) and the summaries (4) use them, and section 7
is re-prioritised. Their code follows the agent brief's `misc_<slug>` naming instead of the file
names in the gap statements (`scratch/rerun/critic_gaps.json`); nothing in the repository imports
the requested names, so a reader looking for them should use this table:

| gap | document | modules (`tools/tsuki/`) | tests (`tools/tests/`, count) | requested by the gap statement, not created |
|---|---|---|---|---|
| vm_variables_errata | vm_variables.md | `vm_vars.py`, `misc_vm_variables_errata.py` | `test_vm_vars.py` (12), `test_misc_vm_variables_errata.py` (15) | - |
| layer_state_and_motions | layers.md | `misc_layer_state_and_motions.py`, `_emu.py`, `_vex.py` | `test_misc_layer_state_and_motions.py` (18) | `tsuki/layers.py`, `tests/test_layers.py`, `samples/layers/` |
| compositing_transitions_shaders | compositing.md | `compose.py`, `gcn.py`, `misc_compositing_transitions_shaders.py`, `_render.py` | `test_compose.py` (8), `test_gcn.py` (6), `test_misc_compositing_transitions_shaders.py` (7) | - |
| boot_title_choice_flow | system_flow.md | `misc_boot_title_choice_flow.py` | `test_misc_boot_title_choice_flow.py` (20) | `tsuki/sysflow.py`, `tests/test_sysflow.py`, `samples/sysflow/` |
| audio_runtime_semantics | sound_runtime.md | `misc_audio_runtime_semantics.py`, `_emu.py`, `_vex.py`, `_verify.py` | `test_misc_audio_runtime_semantics.py` (17) | `tsuki/sound_runtime.py`, `tests/test_sound_runtime.py` |
| adv_system_state | adv_state.md | `misc_adv_system_state.py`, `_emu.py` | `test_misc_adv_system_state.py` (26) | `tools/eboot/advstate.py`, `tests/test_advstate.py`, `samples/adv_state/` |
| gallery_extras_flowchart | gallery.md | `misc_gallery_extras_flowchart.py`, `_emu.py`, `_sheets.py` | `test_misc_gallery_extras_flowchart.py` (14) | `tools/eboot/gallery.py`, `tests/test_gallery.py`, `samples/gallery/` |

Samples are in `samples/misc_<slug>/` (compositing renders also in `samples/compose/`), scratch
work in `scratch/misc_<slug>/`.

---

## 2. Where everything lives (file -> format)

| game file(s) | container | payload | documents |
|---|---|---|---|
| `image/allpac0,1,1_2,4.{hed,nam,mrg}` | HED8 + NAM32 + MRG (bank rule) | full-size image MZPs: scenes, CGs, `_hh_vv` variants, sprite bodies `_B` and faces | containers 2, images, archives_ui_movies 1 |
| `image/allpac2.*` | HED8 + NAM32 | 1/12.8 thumbnails, entry k = image-catalog record k | images 8, archives_ui_movies 4.1 |
| `image/allpac3.*` | HED8 + NAM32 | 71 UI-part archives (nested mrgd00 of images), 49 `RULE_nn` masks, 12 caution screens, placeholders | archives_ui_movies 5 |
| `image/parts.{hed,mrg}` | HED8, no NAM | 24 system-screen images, by index | archives_ui_movies 6 |
| `image/bgm.{hed,mrg}` | HED8 (no terminator) | 132 RIFF ATRAC9 stereo tracks | audio 3 |
| `image/vse.{hed,mrg}` + `se_nam.mrg` | HED8; names in a standalone mrgd00 | 1,327 RIFF ATRAC9 sound effects | audio 4 |
| `image/vag.{hed,mrg}` | HED8 | 19 VAGp HE-VAG system sounds | audio 6 |
| `image/voice.{hed,nam,mrg}`, `voice2.*` | HED4 + NAM8 | 16,464 (+2) LV + MZX0 -> RIFF ATRAC9 voice lines | containers 3, 6; audio 5 |
| `image/allscr.mrg` | standalone mrgd00 | NAM32 names, nested label tables, 1 image, 549 inverted-MZX scripts | script 2 |
| `image/script_text.mrg` | standalone mrgd00 | 5 x (BE u32 offset table, UTF-8 blob): ja, en, zc, zt, ko | text 2 |
| `image/ft_{ja,en,zc,zt,ko}.mrg` | standalone mrgd00 | 65 font-page image MZPs | fonts 2-3 |
| `image/movie/*.mp4` | MP4 | 134 H.264 1080p movies, 43 with AAC | archives_ui_movies 9 |
| `data/if_{ja,en,zc,zt}.png`, `image/corp.png` | PNG | save-data icon; screenshot watermark | archives_ui_movies 8 |
| `eboot.bin` | fake-signed SELF / ELF64 | code **and the data tables that exist nowhere else** (section 5.4) | engine, images 8, fonts 5, script 6, archives_ui_movies 2.4/4/7/9 |
| `sce_sys/*`, `sce_module/*.prx` | PS4 system files | not needed by a port | engine 2.2, archives_ui_movies 10 |

Totals [V-all]: 12 triplet archives with 49,620 entries, 8 standalone mrgd00 files with 891
sections, 1,104,762 MZX0 streams (41.7 GB in, 142.6 GB out), 35,630 image MZPs, 17,944 audio
entries, 134 movies, 549 scripts, 5 x 43,969 text lines; 0 parse failures anywhere.

---

## 3. Coverage matrix

| # | format | status | conf. | module(s) | doc | samples | verification (summary) |
|---:|---|---|---|---|---|---|---|
| 1 | HED8 index (allpac*, parts, bgm, vse, vag) | decoded | high | `tsuki/mrg.py` | containers 2 | `samples/inventory/*.jsonl` | 33,154 records, bank rule (VA 0xE5538 / 0xE5CE8), extents gapless and inside every .mrg |
| 2 | HED4 index (voice, voice2) | decoded | high | `tsuki/mrg.py` | containers 3 | `samples/inventory/voice*.jsonl` | 16,466 records; empty only if both words 0xFFFF (entry 7850 is real); sectors = exact//0x800+1 |
| 3 | NAM32 / NAM8 name tables | decoded | high | `tsuki/nam.py` | containers 7 | inventory | counts = entries for all archives; NAM8 = 54 voice-group prefixes |
| 4 | MRG data files | decoded | high | `tsuki/mrg.py` | containers 2.4 | - | entries sector-aligned, padding ignored |
| 5 | mrgd00 (MZP) container | decoded | high | `tsuki/mzp.py` | containers 4 | `samples/inventory` | 1,128,516 section records; size -0x10000 correction; one nesting level (4,026 nested) |
| 6 | MZX0 compression (+ inverted scripts) | decoded | high | `tsuki/mzx.py` | containers 5 | `samples/containers/` | engine routine 0xE7080; clear-counter reset; 1,104,762 / 1,104,762 streams; overrun up to 127 B |
| 7 | LV voice wrapper | decoded | high | `tsuki/mzx.py` (`parse_lv`, `decompress_lv`) | containers 6, audio 5.1 | - | 16,466 entries `4c 56 03 00 00 09 00`; payload = lip-sync track (placeholder in retail) |
| 8 | Image MZP pixel formats (PAL8, PAL4, HEP8, RGB565+LO+A) | decoded | high | `tsuki/image.py` | images 1-6, 9 | `samples/images/` (52 samples, 72 PNG) | 35,630 headers; 19,823 full decodes; engine 0xD8B80 / 0xD6900 / 0xD5970 |
| 9 | Image catalog + picture-name tables (eboot) | decoded (some fields open) | high | `tsuki/image_catalog.py`, `catalog_eboot.py` | images 8, archives_ui_movies 2.4, 4; layers 7 (anchor, scale_class); gallery 2.2 | `samples/ui_misc/eboot_image_table.csv` | 14,744 records + 31 empty slots (bound 0x39B6); record k = allpac2 entry k = name-list entry k; anchor = picture pivot, scale_class factors 1 / 0.5 / 0.25 / 0.125 [V-emu, layers]; the second name list 0x7A8B10 is used by the gallery (errata 6.1) |
| 10 | Sprites, `_hh_vv` variants, language bands | decoded | high | `tsuki/image_catalog.py`, `catalog.py` | images 7, 8.1-8.2; archives_ui_movies 2.4, 3.3 | `samples/images/catalog_*` | body + face at (0,0); variants replace the base; 166 four-band pictures (VA 0xC2620 stride ceil(h/254)*254) |
| 11 | script_text.mrg | decoded | high | `tsuki/text.py` | text 2-4 | `samples/text/` | 5 x 43,969 lines, strict UTF-8, CR LF, one end mark per table |
| 12 | Message markup (`@` codes, ruby, `^`, icons) | decoded (byte rules); look of some styles open | high | `tsuki/text.py`, `font.py`, `textbox.py` | text 6, fonts 6-7, textbox 4-5 | `samples/text/markup_catalogue.*`, `samples/fonts/` | layout loop emulated; 175,876 lines identical tokenizer vs layout |
| 13 | allscr.mrg storage and script language (tokenizer, readers, dispatch, markers, labels) | decoded | high | `tsuki/script.py`, `script_tables.py`, `script_emu.py` | script 1-3 | `samples/script/` | 549 scripts re-tokenize byte-exact; readers and dispatch emulated (0 mismatches) |
| 14 | Script command semantics (98 names used) | mostly decoded (waits, shakes, STCF / STRA / SPFC / SPRD / SPRC effects open) | high (layer, sound, variable, choice, history commands) / medium (waits) | `tsuki/script.py` + the per-area models below | script 4 (errata 6.3); layers 4; compositing 5, 7-9; sound_runtime 3; vm_variables 4; adv_state 2.5; system_flow 5 | `samples/script/command_stats.json` | handler VAs known for all; each area's commands were run through ScriptStep under unicorn and compared with a model (rows 15, 22, 28, 33, 34) |
| 15 | Script-VM variables and operands (F / G / C / `_D`, Z) | decoded (except the `_SELD` per-line loop, 6.9) | high | `tsuki/vm_vars.py`, `misc_vm_variables_errata.py` | vm_variables (engine 5.5 now fixed) | `samples/misc_vm_variables_errata/` | engine vs model under unicorn, 0 mismatches: 61,000 fuzz cases, 959 distinct retail choice statements x F1118 x scan x gate (7,672 runs), 912-cell operand table, 9,108 + 1,008 first-byte sweep, 28,800 history comparisons; the re-check showed the harness never ran `_SELD` with pending `_SELR` lines (all 170 retail `_SELD` have 1..10) |
| 16 | Engine script tables: scene ids, movie table, motions | decoded | high | `tsuki/script_tables.py`, `misc_layer_state_and_motions.py` (`MotionTable`) | script 6.1, 6.5, 6.6; layers 8 (all 27 keyframe fields, flags 0x245130, loop switch 0xC1C70, easing) | `samples/script/scene_ids.tsv`, `samples/misc_layer_state_and_motions/motion_loops.json` | 549 scene ids; 134 movies + terminator; 24,192 motions / 71,905 keyframes; 0xBE4C0 compared on 82,562 engine calls, 0 mismatches |
| 17 | Fonts: ft_<lang>.mrg + eboot glyph tables | decoded | high | `tsuki/font.py`, `font_eboot.py` | fonts | `samples/fonts/` | 325 pages; 17,423 lookup entries; get_glyph / char_glyph / dashes emulated (0 mismatches) |
| 18 | Text box layout (Text_Layout, wrap, ruby, block draw, page stacking, windows, reveal) | decoded (vertical canvas scale open) | high | `tsuki/textbox.py` | textbox | `samples/textbox/` (`engine_layout_golden.json`) | 175,876 lines + 6,743 messages + 15,749 bubble + 6,301 width-600 + 2,772 synthetic + 672 draw cases, 0 mismatches |
| 19 | ATRAC9 RIFF (bgm, vse, voice) | decoded | high | `tsuki/audio.py` | audio 2-5 | `samples/audio/` | 17,925 streams decode to superframes*1024; delay 256; inclusive loop end (VA 0x1130B0 / 0x10FCB0) |
| 20 | HE-VAG (vag) | decoded | high | `tsuki/audio_hevag.py` | audio 6 | `samples/audio/vag_*` | own decoder (vgmstream table); cross-checked against ATRAC9 copies |
| 21 | se_nam.mrg | decoded | high | `tsuki/audio.py` (`se_lookup`) | audio 4.1-4.2, containers 8.4, sound_runtime 10.2 | - | engine lookup 0xCC790; 14,543 `_SEPL` names resolve in section 2; all 8 callers set flag bit 0, so section 0 is never searched and section 1 never read (unused); section 3 is searched only by UI callers and matches no script name |
| 22 | Audio runtime semantics (channels, tags, fades, ducking, voice gating) | decoded | high | `tsuki/misc_audio_runtime_semantics.py` (+ `_emu`, `_vex`, `_verify`) | sound_runtime (audio.md corrections applied) | `samples/misc_audio_runtime_semantics/` | engine vs model on the whole corpus: 22,037 audio statements in 514 scripts + 32,551 frame updates, 0 mismatches (records byte for byte, globals, queue, library calls); skip / fast-forward replays 0 mismatches; 54 / 54 voice groups through the real `_VPLY`; scan state, VPL2 gate and out-of-corpus inputs differ (6.9) |
| 23 | Movies (MP4, movie table, language remap) | decoded | high | `tsuki/catalog.py` | archives_ui_movies 9, script 6.5, engine 8.7 | `samples/ui_misc/movies.csv`, `movie_language_matrix.csv` | 134 files probed; table 0xDC8C70; remap 0x5E280 |
| 24 | UI part sets (allpac3), parts.mrg, UI texture registration tables | decoded (data); per-screen layout = code (title, boot notices, choice screen now decoded) | high (data) / medium (layout) | `tsuki/catalog.py`, `catalog_eboot.py`, `misc_boot_title_choice_flow.py` | archives_ui_movies 5-7, system_flow 3-5 | `samples/ui_misc/`, `samples/misc_boot_title_choice_flow/` | 21 tables, 220 records; title record 0 rewritten by the G[2] rule (system_flow 4.2, 6.4); SELECTTXT_PARTS is loaded but never drawn; parts #1 and #19..23 are used only by dead boot states |
| 25 | Loose PNGs (save icon, watermark) | decoded (roles) | high | - | archives_ui_movies 8 | - | VA 0x41AD0 / sceSaveDataSaveIcon; VA 0xEE550 |
| 26 | eboot.bin container, imports, NIDs, symbols | decoded | high | `eboot/selfelf.py`, `nids.py`, `imports.py`, `disasm.py`, `symbols.py`, `strings.py` | engine 1-2 | `samples/engine/imports.*`, `funcmap.tsv`, `symbols.tsv` | 337 / 337 NIDs resolved; 39,836 RELA entries |
| 27 | UI text table `ui_text[5][1499]` | decoded | high | `eboot/uitext.py` | engine 9 | `samples/engine/ui_text_*.tsv` | 7,495 pointers equal their relocations |
| 28 | Shaders, blend modes, draw effects, transitions, colour effects | decoded (engine-only transition types, `_FADE` effect 4, masked-layer selection open) | high | `eboot/shaders.py`; `tsuki/compose.py`, `gcn.py`, `misc_compositing_transitions_shaders.py` | compositing (engine 4.2-4.4 superseded) | `samples/engine/shaders/`, `samples/compose/`, `samples/misc_compositing_transitions_shaders/` | 35 / 35 pixel-shader formulas equal the interpreted GCN code (max error 9.7e-4, fp16 export); mutation tests fail as expected; 14 / 14 blend states; 68 draw-effect ids; `_FADS` capture surfaces emulated through the engine's own init; `_STEC` / `_SNX2` / `_SNX3` 756 / 756 bit-exact |
| 29 | Save image (savedata000, 4 MiB) | decoded (layout) / unchecked on a retail save | medium | `eboot/savedata.py`, `misc_adv_system_state.py` (`check_save_image`, CLI `save`) | engine 7 (errata 6.5), adv_state 7 | `samples/misc_adv_system_state/engine_made_save.md` | slots 0..99 manual, 100..138 auto-save FIFO, 139 quick; Slot_Store, QuickSave_Store, SaveImage_Commit and Slot_Load -> History_Restore emulated on game-made images, 0 mismatches; **no retail save exists on this machine** |
| 30 | Input (pad bits, pointer, action masks) | decoded (some action roles [H]) | medium | `misc_adv_system_state.py` (`actions.json`) | engine 8.5, adv_state 8, 10 | `samples/misc_adv_system_state/actions.json` | pad bit map and 640x448 pointer clamp; game input word 0xE3A40; the 27 actions of the three preset tables named from their users (17 [E], 9 [H], 2 unused); preset 0 always used (config +0x42 has no writer) |
| 31 | System flow: boot, notices, title, choice screen | decoded (`_SELS` / `_SELA` prompt layout, title particles open) | high | `tsuki/misc_boot_title_choice_flow.py` | system_flow (engine 8.1, archives_ui_movies 7.2 superseded) | `samples/misc_boot_title_choice_flow/` | boot list for 4 player states with frame counts; title return table 0x209DC0 all 34 entries; G[2] background rule; choice draw list 0xA6E10 in engine order; census 824 SELR / 170 SELD / 26 SELA / 1 SELS; renders viewed |
| 32 | Gallery, flowchart, extras, unlock data | decoded | high | `tsuki/misc_gallery_extras_flowchart.py` (+ `_emu`, `_sheets`) | gallery (archives_ui_movies 7.6 corrected) | `samples/misc_gallery_extras_flowchart/` | 458 CG groups / 1,972 entries / 105 aliases; 277 flowchart columns / 1,286 nodes / 630 edges; 95 music tracks; 3 movies; 25 ending-list entries; column filter 0x21430 equal to the engine on 42 states; review minors (6.9) |
| 33 | ADV system state: history, rewind, backlog, auto / skip, config fields | decoded | medium | `tsuki/misc_adv_system_state.py` (+ `_emu`) | adv_state (engine 5.6, 7, 8.3-8.4; script 3.6 superseded) | `samples/misc_adv_system_state/` | all 0x3A10 record bytes mapped; History_Push 41 seeds and History_Restore every kind with both `from_history` values, 0 mismatches; 59 / 59 push sites (38 live); 398 `_ATSV` classified; slot loads use History_Restore, not scan mode |
| 34 | Layer state, motions, projection | partial (quake / shake / swing formula, crop / mask pass, STRA open) | high (field map, motions) / medium (projection, 6.9) | `tsuki/misc_layer_state_and_motions.py` (+ `_emu`, `_vex`) | layers (script 4.3-4.5, 6.6 corrected) | `samples/misc_layer_state_and_motions/` | scene_state compared byte for byte after every statement: 16,610 retail + 24,674 cover + 1,501 synthetic + the reviewer's 8,253, 0 mismatches; commit 36,000 fields; 0xBE4C0 82,562 calls; projection fuzz worst 1.63 canvas units for motion-animated anchors (6.9) |

Test-suite state (2026-10-25, after all follow-up documents): `pytest tools/tests -q` ->
**368 passed, 2 skipped** in 97..98 s (two runs). Both skips wait for a retail `savedata000` that does not
exist on this machine (`test_eboot.py` real-save test, `test_misc_adv_system_state.py::test_real_save_if_present`).
The first snapshot had 221 passed, 1 skipped.

---

## 4. One-paragraph summaries

**1. HED8.** 8-byte records `u16 sector_lo, u16 sector_hi, u16 size_sectors, u16 size_lo`.
`sector_hi` bits 12..15 are sector bits 16..19; the remaining high bits come from a per-archive
bank table that the engine builds at load time: a new bank starts at every entry whose high
nibble returns to 0 after being non-zero (VA 0xE5538), so `sector = stored20 | bank << 20`
(allpac0 2 bank switches, allpac1 11 = the engine maximum, allpac1_2 4). Exact size =
`(size_sectors*0x800 & ~0xFFFF) | size_lo`, minus 0x10000 if that exceeds the sector span. A
16-byte 0xFF terminator ends every HED8 except bgm.hed. mangetsu / deepLuna read the offset as
a plain u32 and lack the -0x10000 correction; do not copy them. (containers 2)

**2. HED4.** voice / voice2: 4-byte records `w0` = sector bits 0..15, `w1` bits 0..11 = sector
count, bits 12..15 = sector bits 16..19. A record is empty only when both words are 0xFFFF
(entry 7850 `ffff 1b30` is a real entry at sector 0x3FFFF). No byte size is stored: the entry
ends where the LV header plus the MZX stream end; the packer counts `exact // 0x800 + 1`
sectors. (containers 3)

**3. NAM32 / NAM8.** NAM32 = 32-byte records, NUL-padded ASCII name, bytes 30..31 CR LF (bytes
28..29 hold a 2-letter tag only in se_nam sections 0, 1, 3), ended by an all-zero record; record i
names entry i. NAM8 (voice) = 8-byte records with only the 4-character speaker-group prefix
(54 groups), plus a 2-byte zero tail; the full voice name exists only in the scripts.
(containers 7)

**4. MRG data.** Entries back to back in index order, each padded to 0x800-byte sectors with
packer garbage (a repeating 16-byte pattern), never needed. Files reach 24.3 GB, so readers must
seek or memory-map. (containers 2.4)

**5. mrgd00 (MZP).** `"mrgd00"`, u16 N, N 8-byte section records with the same field layout as
HED8, then the data area: `offset = data_start + sector*0x800 + (byte_offset & 0xFFF)`.
`size_sectors` counts sectors touched, sections are separated by 1..8 bytes of 0xFF so the next
starts 8-byte aligned, and the container size equals the HED size. Kind must be decided from the
content (`DUMMY.MRG` and `CGMODE_TOP.MRG` are 16x16 images). Exactly one nesting level exists.
(containers 4)

**6. MZX0.** 8-byte header (`"MZX0"`, u32 output size) then a stream of 16-bit-word commands:
RLE, back-reference (distance 2..512 bytes), 64-entry ring buffer, literal. A clear counter
(0x1000, decremented by n per command, by 1 for ring-buffer reads) resets the `last` word at
every 0x2000-byte output boundary; mangetsu and deepLuna omit it and produce garbage pixels.
The size test runs only between commands, so the last command overruns by up to 127 bytes:
allocate `size + 128` or clamp writes. Scripts are decoded plain and then XORed with 0xFF.
(containers 5)

**7. LV voice wrapper.** `'L' 'V' u16 n, n payload bytes`, then an MZX0 body (or raw data if
the next byte is not `'M'`). The payload is a lip-sync amplitude track read by the mouth
animation (VA 0x9690); every retail entry carries the 3-byte placeholder `00 09 00`.
(containers 6, audio 5.1)

**8. Image MZP.** Section 0 = 16-byte header (`width, height, tile_w, tile_h, tiles_x, tiles_y,
u16 bmp_type, u8 depth, u8 attr`), an optional palette (only `bmp_type` 1: 16 or 256 RGBA
entries, 256-colour palettes stored in PS2 CSM1 order, alpha `0x80 -> 0xFF else 2a`), and one
flag byte per tile (0 = empty, never drawn). Sections 1..N are plain MZX0 tiles, row-major.
Formats: PAL8 (allpac2 thumbnails, UI, fonts), PAL4 (only empty 16x16 placeholders, linear
nibble stream), HEP8 (`bmp_type` 12: per-tile 32-byte "HEP" header + indices + own palette,
alpha `2*min(a,0x7F)`, a 1-pixel border on every tile, shown size = stored - 2*tiles), and
planar RGB565 + low bits + alpha (`bmp_type` 11). Output is straight-alpha RGBA8; no GPU
swizzle, no BCn. Images reach 11,776 x 1,280 / 1,536 x 11,264 / 11,264 x 7,680 stored, so a
runtime must keep tiles as separate textures. (images 1-6, 9-10)

**9. Image catalog and picture names (eboot).** 0x48-byte records at data VA 0x671900 (file
0x3D78A0), 14,744 used + 31 empty slots. Record k: `name0` (base image or sprite body `_B`),
`name1` (face layer, drawn over name0 at layer offset (0,0)), runtime file-index caches, the
size in the 640x362 engine space (`floor(w/3)`, `floor(h*362/1080)`), logical width / height,
anchor, flag bytes, `char_id`, `scale_class`. The embedded name list at data VA 0x7F6800
(14,744 upper-case names without extension) maps a script picture id to k; k is also the
allpac2 index (the 1/12.8 thumbnail). Pointers are text VAs; names compare case-insensitively.
The anchor is the picture's pivot for scale, zoom and rotation; `scale_class` (L / M / S)
multiplies child positions and zoom under a parent layer and `_STPV` anchors by 0.5 / 0.25 /
0.125 (layers 7, 4.2). The second name list (data 0x7A8B10, 9,958 names) is not dead: the gallery
looks every CG name up in it (gallery 4). (images 8, archives_ui_movies 2.4, 4; errata 6.1)

**10. Sprites, variants, language bands.** Paired sprites (3,529 records) draw the `_B` body
and then the face at the same origin; 1,221 sprites are standalone. `_hh_vv` names (7,288,
`pic_flags` bit 0) are complete alternative (pre-blurred) renderings selected by `_STCH`
arguments 9/10 and replace the base. 166 records (`pic_flags` bit 1) store ja / en / zc / zt
stacked vertically; the engine draws rows `[L*S, L*S + h)` with `S = ceil(h/254)*254`. Three
bodies are missing from the data (e.g. `NOE_L_60_03_N00_B`): the engine silently skips them.
(images 7-8, archives_ui_movies 2.4, 3.3, script 6.4)

**11. script_text.mrg.** mrgd00 with 10 uncompressed sections: section 2k = big-endian u32
offset table (43,969 offsets, then blob size twice, then one 0xFFFFFFFF), section 2k+1 = UTF-8
blob; k = 0 ja, 1 en, 2 zc, 3 zt, 4 ko (every ko line is U+3000). String i =
`blob[off[i]:off[i+1]]`, always ending in CR LF; the engine copies at most 767 bytes and gives ""
for strings shorter than 3 bytes. Line id i is the same message slot in every language (in the
QA corner English splits sentences differently, 6.3). (text 2-4)

**12. Message markup.** Interpreted byte by byte on the expanded UTF-8 message by the layout
function (VA 0xC6C20): `^` line break, `<base|reading>` ruby (disabled in English, where `<`
`>` `|` are glyphs), 39 `[icon]` tags, and `@` codes with fixed byte lengths (`@a @c @f @h @w`
take one parameter byte, `@x @y` up to 3 digits, `@s` a number up to `,` / `)`, `@1` / `@2`
escapes, any other byte skips 2 bytes or a `(...)` group). Codes work inside ruby groups.
Styles 0..5 select glyph sets (`@g`, `@z`, `@i` italics in English). What `@g/@z/@b/@a` look
like on screen is not established. (text 6, fonts 6-7)

**13. allscr.mrg and the script language.** Section 0 = NAM32 names, section 1 = nested label
tables (32-byte text records `name, block hex, offset hex`), section 2 = a white 128x96 image,
section k+3 = script k (MZX0 decoded then XOR 0xFF). A script is one ASCII line of
`_NAME(args);` statements interpreted directly (VA 0x95E60): segments end at `;` within 0x300
bytes, the 4 bytes after the first `_` are the name, unknown names are skipped, at most 1000
segments per call. Arguments are read either by an inline reader (sum of decimal terms, other
bytes ignored, empty = 0, missing = -1) or, for 52 commands, by an object parser with a C-like
right-associative expression evaluator (`{}` grouping, stops at the first unknown byte, so
`242.25` = 242). Markers `_Z<k><bbb><ss>(arg)` (k = M message, Z label, Y local label, D
disabled) carry message display and read tracking (lower-case hex only). (script 1-3)

**14. Script command semantics.** 110 names + `J*` (JUMP) + `Z*` (markers) are handled
(dispatch recovered by emulation over 1.3 M names); 6 used names are no-ops on PS4 (`SNX4 FANX
IFP4 DMMY STQK NOP_`). Flow control, variables, messages, choices, pictures (STCH backend
0x81760), sound, movies and waits are catalogued with handler VAs and frame behaviour
(continue / yield). The follow-up documents give engine-verified semantics per area, each
checked by running the handlers through ScriptStep under unicorn against a model: layer
commands (STCH / SCH2 field map, `_STCP` = z order, `_STPV` = anchor, timed `_STGS` / `_STZ4` /
`_STRT` / `_STCL`, `_STMA` / `_SGMA` / `_SPMA`, `_STTR`, `_SOXY`, `_STEC`, `_SNX2/3`, `_STNS`,
`_SBAS`; layers 4), `_STBR` / `_FADS` / `_FADE` and the colour effects (compositing 5, 7-9),
every audio command and tag (sound_runtime 3), variable and choice commands (vm_variables 4,
system_flow 5), `_ATSV` and the history pushes (adv_state 2.4-2.5). Picture placement: the
catalog anchor lands on the layer position and scale, zoom and rotation act about it; one
picture pixel = one frame pixel, one script unit = 4 px. Still [H] or open: the wait semantics
(`_WTTM` second argument, `_WNTY`, `@k`), the per-frame quake / shake / swing formula (`SQK2
SHK2 SSW2 SHOK MSQK`), the reader of `_STCF`'s globals, `_STRA`'s visible effect, and `SPFC /
SPRD / SPRC` (globals written, meaning [H]). (script 4, errata 6.3)

**15. Script-VM variables.** Storage is three u16 arrays: `var_F` (VA 0x164B1D0, 1200),
`var_Z` (0x164BB30, 1400), `var_G` (0x164C620, 2000); no index is range-checked (`F1200` is
Z[0]). Every operand is decoded from its first byte (after an optional 5-byte backtick tag) with
the inline-reader value of the whole argument as index; which first bytes select a variable
depends on the command and argument position (vm_variables 3.2, 5). `C<n>` is a write target of
`_WKST` / `_WKAD` only: `F[F823 + n]`, F823 = number of the current message on the page, so
`C847` / `C859` are per-message slots (88,546 commands have a `C` operand); in value positions
it is the literal n.
`_D<n>` = F[n] (G[n - 600] for 600..700). `Z<n>` is never a script variable. `_IF__` jumps when
its condition is **false**. `_SELD(_D000, N)` records F[0] as the result target and the choice
UI later writes the chosen `_SELR` argument 0 there; `_SELS` stores the button index 0 / 1;
`_SELA` is the "save your progress?" prompt: it opens only when F1118 = 0, always clears F1118
and stores nothing. In scan mode the three choice commands change no choice state. A history
record keeps 356 of the 1200 F values in three storage classes; G[0..499] are system data.
Not in the model (6.9): `_SELD`'s per-line loop (F799 written and a kind-0xC history snapshot
taken per pending `_SELR` line), and the quick save does not set the F1118 bit. (vm_variables)

**16. Engine script tables.** Scene-id table (text VA 0x216130, 549 i16: 110 START_SCRIPT, 240
00_00, 260 00_01, 270 00_02, 300..790 chapters, 1000.. QA, 3000 ARCHIVE, 8000.. debug); movie
table (data VA 0xDC8C70, 134 x 0x30 records + terminator at 0xDCA590); motion tables for
`_STMA` / `_SGMA`: durations (text VA 0x274538) and end indices (0x2A3938), i64[24192] indexed by
id - 1; flag words u64 (0x245130) indexed by id; keyframes (data VA 0x873FF0), 72-byte records =
u64 time (ms), fields 1..7 i16, 2 bytes padding, field 8 i64, fields 9..27 i16, 2 bytes padding;
-32767 = not animated, -32766 = hold. Fields: 1 / 2 easing and interpolation type, 3 / 4
position, 5 alpha, 6 / 7 anchor, 8 rotation, 9 / 10 zoom, 11..14 crop, 15 / 16 edge parameters,
17 blend mode, 18..21 colour key and RGB, 22 / 23 picture variant, 24 / 25 the `_SNX2` / `_SNX3`
values, 26 `_STNS`, 27 z; a field applies only when its flag-word mask is set. Loop points are a
code switch (VA 0xC1C70; 2,027 looping motions). Motions are absolute values. (script 6,
layers 8)

**17. Fonts.** ft_<lang>.mrg holds only pictures: 65 image MZPs (section k = texture slot
0x21+k), 1024x1024 PAL8 pages with glyphs in rows 0..511 and their dilated outlines 512 px lower.
All code-point maps live in eboot: characters are keyed by their UTF-8 bytes packed
big-endian, binary-searched in 16 lookup arrays (data VA 0x626C10.., 17,423 entries), then either
a 73-px wide grid (3/4-byte characters in style 0) or 17 rectangle tables (text VA 0x177370..).
Tags, joined dashes (`―` `─` `■` runs, raw-pointer prev / next rule) and redaction bars are
special-cased. Korean tables and pages are stubs. Table VAs are fixed for 01.01. (fonts)

**18. Text box.** `_ZM` / `_MSAD` call `Text_Layout(block, text, 474, 220, spacing 0, leading 9,
colour, flags; sx = sy = 0.74)` (VA 0xC6C20); the width becomes 574 in English (VA 0x538F0).
Pass 1 gives every element a break class (kinsoku classes 0x0D / 0x0E / 0x71, English word
table, budget 1200 elements); pass 2 places glyphs (`pen += adj + (w-6)*sx`), wraps (margin W -
96*sx, English W - 64*sx with look-ahead) and places ruby (scale 0.1 x 0.11, dy -7.04). Text
space is 640 x 360 units at 3 px / unit; the first message of a page starts at (87, 49) (ja / zc /
zt) or (30, 35) (en), later ones stack below. The block draw reveals glyphs at 1000/(60*speed)
counters per frame and draws shadow, outline and fill. The prologue movie places lines 81 px apart
against the engine-derived 78 px; that cause is open. (textbox)

**19. ATRAC9 RIFF.** `RIFF/WAVE` with `fmt ` (0x34, WAVE_FORMAT_EXTENSIBLE, ATRAC9 GUID, config
word), `fact` (samples, 256, 256 = encoder delay), `smpl` (one loop, bgm / vse only), `data`.
48 kHz; superframes of 512 B (bgm, 192 kbps stereo), 384 B (vse / voice), 160 B (dummies).
Playable samples are `decoded[256 : 256 + fact[0]]`; loop = `[smpl.start - 256, smpl.end - 256]`,
end inclusive (engine VA 0x1130B0 / 0x10FCB0). Every bgm / vse file has a loop; whether it loops
is the caller's decision (`_SEPL` argument 2; BGM by default). FFmpeg's `atrac9` decodes all
17,925 streams. (audio 2-5)

**20. HE-VAG.** vag.mrg entries are big-endian `VAGp` headers (version 0x00020001, 48 kHz,
name at 0x20) followed by 16-byte frames using a 4-tap, 128-entry predictor (HE-VAG); FFmpeg's
`adpcm_psx` decodes them wrongly. The decoder in `tsuki/audio_hevag.py` follows vgmstream's
psv_decoder table. 19 entries: a dummy plus 9 stereo sounds stored as `_L` / `_R` mono pairs.
(audio 6)

**21. se_nam.mrg.** Standalone mrgd00 with 4 NAM32 sections: 0 = `S00..S07`, 1 = `RANKING`,
`NEKO02`, 2 = the 1,327 vse names (record i = vse entry i), 3 = 67 `SE_xNNNN` names (index
10000 + i, no data on PS4). The engine lookup (VA 0xCC790) upper-cases, cuts at `.` or 28
chars and searches sections 0, 3, 2 depending on flags. Every caller sets flag bit 0, so
section 0 is never searched and section 1 is never read (both unused); section 3 is searched
only by UI callers and matches no script name. (audio 4.1-4.2, sound_runtime 10.2)

**22. Audio runtime.** Three float AudioOut ports at 48 kHz (stereo 1024-sample mixer, 8-channel
mixer for an absent 5.1 BGM set, stereo 256-sample HE-VAG port) plus movie audio. 26 sound
streams: 0 / 1 = two BGM slots that alternate on every new track (the old one fades), 2..5 =
voice (rotating; one voice at a time), 6..25 = script SE channels 0..19; HE-VAG voices for the
system SEs. Each has a 0x34-byte channel record (sound_runtime 2.2). Keyword tags are found by
number anywhere in the statement: `` `001 `` fade / ramp ms, `` `011 `` old-track fade (`_MPLY`) or
wait switch (`_MFAD`), `` `017 `` volume, `` `018 `` pan 0..128 (64 = centre), `` `049 `` loop (BGM
default 1 = forever), `` `068 `` delay in ms (queued as a deferred command), `` `069 `` start sample,
`` `070 `` config slot + 1 (a voice-like SE gated by that character's voice option). The
per-frame update ramps volume and pan linearly (`points * 1000 / 60 / ms` per frame), stops
faded channels and runs the deferred queue. BGM and voice gains are integer percentages
(`trunc(cur * vol * 0.01)`), SE gains float. `_VPLY` only stores the voice; the next `_ZM` starts
it if that character's voice is enabled (skip / fast-forward rules in sound_runtime 7), and
Adv_Update stops it when the text advances (config +0x4E, default on, unless F1123 is set).
Optional ducking halves the BGM in 32 frames while a voice plays (off by default). The 54 voice
groups map to 19 config slots (young Shiki shares Shiki's). The 19 vag sounds are system SEs,
played only by SystemSE_Play ids 0..9. Pan law `gL = min(2(1-p),1)`, `gR = min(2p,1)`.
(sound_runtime; audio 3, 4, 7; errata 6.6, 6.9)

**23. Movies.** 134 MP4 files, H.264 (High 4.0/4.2, Main 4.1, Constrained Baseline 4.1)
1920x1080 yuv420p at 30 or 29.97 fps, BT.709; 43 have AAC-LC 48 kHz stereo, 31 of those are
digital silence. The movie table maps id -> path; `_TOMP(id)` passes a language-neutral id that
VA 0x5E280 remaps (prologue via table 0x200F70; phase titles +40 en / +67 zc / +99 zt; openings
+97 en / +99 zc, also used for zt). Ids 0..3 (title loops), 7 (phaseout) and 38 (TM_CI) are
played by engine code. (archives_ui_movies 9)

**24. UI parts, parts.mrg, UI registration tables.** allpac3's 71 `*.MRG` entries are nested
mrgd00 archives of image MZPs, one per screen and language (`CONF_PARTS_EN.MRG`, ...). The
engine registers them through 0x18-byte records (`state, tex_slot, name, child, child2`, 21 tables,
220 records): the localising loader turns `conf_parts.mrg` into `conf_parts_<lang>.mrg` (suffix
table relro 0x42C030), the child index is clamped to the archive's last child. parts.mrg holds
24 system screens addressed by index (logo, save warnings, autosave notice, disclaimer, ja / en /
zc / zt / ko groups). Button positions, atlas sub-rectangles and animations are code constants in
each screen's functions; only the config screen is decoded. (archives_ui_movies 5-7)

**25. Loose PNGs.** `data/if_<lang>.png` (228x128) is the PS4 save-data icon (path built at VA
0x41AD0); `image/corp.png` (131x48) is the watermark the system overlays on user screenshots. A
port needs neither for gameplay. (archives_ui_movies 8)

**26. eboot.bin.** Fake-signed SELF (10 segments, all plain) with an embedded ELF64: text + rodata
at VA 0 (file 0x2280), RELRO at 0x400000, data + bss at 0x600000 (memsz 0x1A85790). 337 imported
NIDs (all resolved), 39,459 RELATIVE relocations whose addends are already stored in place, Gnmx
statically linked. The engine is Hunex's "nexl" library (module `devXXX`). (engine 1-2)

**27. UI text.** `char *ui_text[5][1499]` at RELRO 0x41D5F0 (getter 0xC29B0: id 1..1498, index =
lang*1499 + id); ja / en / zc / zt plus a Korean placeholder column. Holds menu, dialog, config,
music-room, chapter / scene titles and flowchart synopses that are not in script_text.mrg. A port
must extract it from the user's eboot. (engine 9)

**28. Shaders and compositing.** 67 PSSL binaries (35 PS, 32 VS) at data VA 0xDCA6E0..0xDD66B0.
Every quad is `out = blend_state(shader(S, D, C, P), RT)`: S = layer texel (straight alpha),
D = render target before the quad, C = vertex colour (alpha = layer opacity), P = per-draw
constants. The 35 pixel-shader formulas are derived from the GCN code and checked by running it
(compositing 2.3): basic, YCbCr (BT.709 limited range), transition (smoothstep of the rule's red
channel), sepia (unreachable in the shipped game), and 24 blend modes 1..25 in two families
("nexl" modes 1..10 and 15 / 17 mix the opacity in the shader, "PS" modes 11..25 leave it to the
blend state). Soft light is the power form `d^e`, hard light and ps_add apply the vertex colour
first, dodge keeps pure black black. `_STBR(l, mode, , flag)` selects the layer's draw-effect
word; flag 1 makes the weight opacity x picture alpha for modes 5 / 7 / 9 and turns blending off
for 5 / 7 / 9 / 10 / 15 / 17 (for ordinary layers). `_FADS(type, ms, rule)`: on its first frame
the previous frame is copied (alpha forced to 1) into capture surfaces; type 1 is a cut, type 0
a cross-fade with weight `trunc(255 p) / 255`, type 2 a rule wipe with old-frame weight
`smoothstep(lo, lo + 1, rule.r)`, `lo = 1 - 2p` (p = remaining progress, falling by
`1 / (ms / 16.666)` per frame). The rule picture sits in one shared texture slot (0x4DD) that
`_STTR` also loads; `_FADS(2, ms, 0)` and fast-forward reuse the loaded rule. `_STEC` / `_SNX2` /
`_SNX3` are CPU recolours of the picture at texture build (0xD36F0). The 33-name mask table (data
0x869B30) is dead code. (compositing; engine 4.2-4.4 superseded; errata 6.5, 6.9)

**29. Save image.** One 4 MiB file: system block 0x18AC bytes at 0 (config 0x140, G[0..499],
the OR-merged unlock blocks +0x52C = picture-seen / BGM-heard bits and +0x16F4 = choice-taken
bits, `readmax[4020]`, latest slot), then 140 slot records of 0x7108 bytes at `0x1A00 +
i*0x7140`; the area ends at 0x3E0900. A slot holds state A (the newest page-start history
record), state B (the scene-stack top record) and a date; SaveImage_Commit stamps the end
marker 0x2AF8 on every slot block. Slots 0..99 are manual, 100..138 an auto-save FIFO, 139 the
quick save. Manual and auto saves set the F1118 bit in state A, the quick save does not. The
layout and the load path are emulated on images made by the game's own code; no retail save has
been examined (`check_save_image` is ready for one). A port should use its own format with the
same logical content. (adv_state 7, engine 7; errata 6.5)

**30. Input.** The pad manager builds an internal 32-bit mask (decide / cancel follow
ENTER_BUTTON_ASSIGN, sticks as directions, edges and auto-repeat); mouse and touch move a
pointer in a 640x448 space (clamped 0..639 x 0..447). Game code builds a game input word (VA
0xE3A40) and tests it with 27 action masks from one of three preset tables (data VA 0xDB84F0;
preset 0 is always used, config +0x42 has no writer). The actions are named from their users
(adv_state 10.2): 0 confirm / advance, 1 cancel, 10 backlog, 12 skip latch, 13 auto, 14 quick
save, 17 system menu, 19 fast-forward (hold; analog R2), 20 rewind (hold), 23 / 24 next /
previous scene, 25 hide the window, plus choice, list and dialog actions (some [H]). (engine
8.5, adv_state 8, 10)

**31. System flow.** Boot: system-save states 0..4, Game_OpenArchives (one frame), the
save-damaged notice (damaged save only) and the autosave notice (parts.mrg 8 + language), then
the title init. There is no logo screen; parts #1 and #19..23 are used only by dead states. On a
first boot (prologue bit 485 clear) the title is never drawn and New Game starts scene 260
(00_01, prologue movie) at once. After the prologue (G[10] = 0) the title shows PRESS ANY
BUTTON, then `caution_<lang>` and `caution1_<lang>`, then scene 110 (START_SCRIPT -> 00_00).
A returning player gets the TM_CI movie (once per power-on), `caution2_<lang>`, then the title
menu. Title background: TITLE_PARTS child 9 + T[G[2]], T = {1,2,3,3,2,4} (6 x i32 at text
0x218A40), with `titlebg<T>.mp4` looping by one seek to 8.0333 s per wrap; G[2] steps 0 -> 1 ->
3 -> 5 (at most one step per title entry) as three route bits are reached. The title returns
only 0, 2 (New Game), 3 (continue a loaded save) and 7 (gallery). Choice screen: `_SELR` slots
are prefixed with line 43960 + k ("1." .. "9.") or 43959 ("0.") for a 10th line (debug scripts
only); `_SELD(var, N)`: N is the choice-point number (SELECTTXT child, taken bit, skip stop),
not a default cursor (the cursor starts at -1); phases fade-in (25 frames), reveal, select
(locked lines skipped), decided (51 frames, only the chosen line drawn). Choices are always font
text: SELECTTXT_PARTS is loaded but never drawn. (system_flow; engine 8.1 and
archives_ui_movies 7.2 superseded)

**32. Gallery / flowchart / extras.** The title's GALLERY item opens a six-item menu (CG
Gallery, Music Player, Flowchart, Movie, Ending List, Title); there is no voice gallery. Lists
[V-all]: 458 CG groups / 1,972 entries (RELRO 0x400830, 0x405240..), 105 CG aliases (0x404170),
277 flowchart columns (RELRO 0x418770) with 1,286 64-byte nodes (495 scene nodes, FC_TH child =
scene - 1) and 630 edges (text 0x2218D0), 95 music-player tracks (text 0x1A47D0), 3 movies, 25
ending-list entries (text 0x18F810, "Teach Me, Miss Ciel"). Unlock state: G[110..145]
scene_seen, G[300..335] scene_done, G[400..443] edge_seen (set by `_JUMP` and `_SELD`), G[5]
movie_seen, and system blocks +0x52C (picture_seen bits 0..3199, bgm_heard 3200..3719) and
+0x16F4 (choice_taken). A CG entry unlocks when its category is open and its alias or picture
was shown or scene 557 (QA_25) is done; the music room uses bgm_heard (set by `_MPLY`); a gallery
entry replays through script ARCHIVE (scene 3000) with G[8] = scene and F024 = 1. The extra_parts
screens, the legacy CG list 0x78C810 and the music table 0x209510 are unreachable. (gallery;
archives_ui_movies 7.6 corrected; errata 6.9)

**33. ADV state.** History: a ring of 800 records of 0x3A10 bytes (script, command offset, kind,
BGM / SE state, selected F values, the 0x1A08-byte scene_state and a second, committed copy;
808 allocated). 59 call sites push records (38 live): kind 0 at every `_ZM`, 1 once per batch of
layer changes (F991), 9 at the next commit point, 6 / 7 from `_ATSV()` / `_ATSV(1)` (scene start /
choice save point; `_ATSV(8)` also auto-saves), 2 / 3 audio, 12 / 13 choices. Rewind undoes
records newest-first back to the previous page start; a slot load, previous scene and backlog
jump restore a record without re-display and run the saved `_ZM` statement again. Slot loads use
History_Restore; scan mode is only the fallback for a slot without the 0x2AF8 end marker. Auto
mode waits config +0x2E ms (+0x9E on the last message of a page, F799 = 1); skip advances 64
frames per frame and by default stops at unread text (config +0x30); the backlog lists the
ring's message and choice records, minus the current page. The 0x140-byte config block is
mapped field by field, with 37 menu items. Read tracking: `readmax[block]` updated when the next
marker is reached. (adv_state; engine 5.6, 7; script 3.6, 3.7 superseded; textbox 8)

**34. Layer state.** 40 layers (0..39). Statements write `scene_state` (VA 0x16500C0, 0x1A08
bytes: 51 per-layer arrays plus globals); `_FADS` commits it to record array B (0xDE2650, 40 x
0x2C8) and the per-frame update moves B to the drawn array A (0xDDB5E8) and runs tweens, motions
and shakes. Timed `_STGS` / `_STZ4` / `_STRT` / `_STCL`, `_STMA` key 0 and `_STPV` also act on A at
once. `_STCH` resets picture, position, scale, alpha, z (default 10 x layer) and angle, not
flags, parent, offsets, mask, crop or effects; with a source layer (arg 18) it copies that
layer's motion and re-points mask / parent references; `_STCH` and `_SCH2` reset `_STTR` to -1.
A layer's quad is an affine map: picture texels about the anchor, scale x zoom (negative =
mirrored), rotation (1/100 degree, clockwise), the parent chain (`_SBAS`, children scaled by the
parent's size class), then canvas 640 x 362.667 -> 1920 x 1080. `_STMA(l, id, wait, n)` applies
keyframe fields with the engine's easing (bit-exact float32 for 99.8 % of eased keys); n
quantises time (`frames - frames % n`) and does not slow the motion; `_SGMA` writes the layer's
drawn state into the in-memory keyframe table. Open: quake / shake / swing formula, crop / mask
rasterisation, `_STRA`'s effect, and the quad residual when motion fields 6 / 7 move the anchor
(6.9). (layers; script 4.3-4.5, 6.6 corrected)

---

## 5. Cross-reference map

### 5.1 How scripts reach every other asset

```
title code / _JUMP(scene_id[,label])
   -> SCENE_IDS (eboot text 0x216130): scene id -> script index k   (unknown id -> START_SCRIPT)
   -> allscr.mrg section k+3 -> MZX0 -> XOR 0xFF -> script text
   -> label -> allscr section 1 child k (32-byte records: name, block, byte offset)

_ZM / _MSAD / _ZD / _SELR(..., "$NNNNNN" ...)
   -> $-expansion (VA 0x76040, cap 0x2FC bytes): line NNNNNN of language L
   -> script_text.mrg section 2L (BE u32 offsets) + 2L+1 (UTF-8 blob)
   -> markup + Text_Layout (VA 0xC6C20) -> glyph keys -> eboot glyph tables -> ft_<lang>.mrg page
   (_SELR adds "$0439NN" itself: the choice number line 43960+k = "1." .. "9.", or 43959 = "0."
    for k >= 9; choices are always font text, SELECTTXT_PARTS is never drawn: system_flow 5)

_STCH / _SCH2 / _SPFC / _SPRD / _STCG(name)
   -> + "_%02d_%02d" from args 9/10 (or inherited from source layer arg 18)   [STCH/SCH2 only]
   -> key = upper(name) up to '.', 32 bytes (VA 0x78410)
   -> picture-name list (eboot data 0x7F6800) -> k (not found: -1, nothing drawn)
   -> image catalog record k (eboot data 0x671900 + 0x48k): name0 [+ name1 face]
   -> NAM file search (VA 0xC3270): allpac4, allpac3, allpac1_2, allpac1, allpac0 (28-byte compare incl. NUL)
   -> packed id (archive << 16 | entry) -> HED8 (+bank rule) -> MZP -> header + MZX0 tiles -> RGBA
   -> language bands (pic_flags bit 1): source rows [L*S, L*S+h)
   k also = allpac2 entry k (thumbnail; save/load-slot previews)

_FADS(2, ms, n)          -> NAM id of "mask_top.mzp" + n = allpac3 #85+n = RULE_nn (n = 1..49)
                            loaded into the shared texture slot 0x4DD (also loaded / freed by _STTR);
                            n = 0 or fast-forward reuses the rule already loaded (compositing 7.3)
_STMA / _SGMA(layer, m)  -> eboot motion tables (durations 0x274538 and end index 0x2A3938 by m-1,
                            flags 0x245130 by m, keyframes 0x873FF0, loop switch 0xC1C70; layers 8)
                            _SGMA writes the drawn layer state into keyframe 'key' of motion m (in RAM)
_VPLY / _VPL2(name, hex) -> voice.hed entry int(hex,16) (HED4) -> LV + MZX0 -> RIFF ATRAC9 (mono 48 kHz)
                            check: voice.nam[entry] is the name's 4-char group prefix
_SEPL / _SEFI(ch, loop, name, ...) -> se_nam.mrg lookup (VA 0xCC790; section 2 record i)
                            -> vse.hed entry i -> RIFF ATRAC9; SE channel ch plays on stream ch+6
_MPLY(n, ...)            -> bgm.hed entry n (n+131 with the 5.1 option: entries absent) -> RIFF ATRAC9, streamed
_TOMP(id)                -> language remap (VA 0x5E280) -> movie table (data 0xDC8C70) -> image/movie/*.mp4
_TRPY(n)                 -> trophy n (optional on PC)
_RDST/_RDED(n)           -> scene_seen G[110..145] / scene_done G[300..335] (system data) -> gallery, title, New Game choice
_RTST(n)                 -> per-save copies F[201..272] / F[600..687] (+ F[110] for QA scenes / replays)
_JUMP, _SELD(var, N)     -> edge_seen G[400..443] (flowchart edges, table 0x2218D0 / choice flags 0x2214E0)
_STCG / picture display  -> picture_seen bits (system +0x52C, seen-index map data 0x86AA30) -> CG gallery
_MPLY(n)                 -> bgm_heard bit 0xC80 + n -> music player (gallery 2.1)
```

### 5.2 Reference table

| referencing side | field | resolves through | target | verified |
|---|---|---|---|---|
| script `_ZM/_MSAD/_SELR/_ZD` | `$NNNNNN` | language section pair | script_text line | 43,930 refs, 43,902 ids, 0 out of range |
| script `_SELR` | slot counter k | handler 0x9936D | line 43960+k / 43959 | 10 choice-number lines |
| script picture commands | name (+ variant digits) | name list 0x7F6800 -> catalog -> NAM search | allpac0/1/1_2/4 entries (+ allpac2 thumbnail) | 10,967 effective names, 10,962 resolve, 5 script bugs |
| catalog record | name0 / name1 | NAM search | body + face layers | 3 bodies missing on disk (skipped) |
| script `_FADS` type 2 | rule n | `find("mask_top.mzp") + n` | allpac3 RULE_nn | rules 0..49 used except 46 |
| script `_STMA/_SGMA` | motion id | eboot motion tables | keyframes | 24,349 in range, 92 zero, 1 too large (empty motion) |
| script `_VPLY/_VPL2` | hex index | voice.hed | voice entry | 16,705 / 16,705 prefix-consistent |
| script `_SEPL` | name | se_nam section 2 | vse entry | 14,543 calls, 1,314 names |
| script `_MPLY` | track n | bgm.hed | bgm entry | tracks 2..123 |
| script `_TOMP` | movie id | remap + movie table | mp4 | 40 ids, all files exist |
| script `_JUMP` | scene id, label | SCENE_IDS + label table | script + offset | 1,741 edges, 629 labels |
| script window variables | F1104 (hide), F1105 / F1109 (window index) | HUD 0x53CF0 | DISP_PARTS children 11..13 (overlays), 22..51 (bubbles) | layout emulated |
| engine UI code | registration tables | localised name -> NAM search (allpac3) -> clamped child | texture slots | 707 lookups (695 direct, 4 clamped, 8 rewritten at run time) |
| engine UI code | ui_text id | `ui_text[lang][id]` | UTF-8 string | 7,495 entries |
| engine system screens | parts index | parts.hed | parts.mrg image | 24 entries |
| engine font loader | language | `ft_<lang>.mrg` -> slots 0x21..0x61 | glyph pages | 325 pages |
| engine save code | language | `/app0/data/if_<lang>.png` | save icon | - |

### 5.3 Language selection across assets

The language option is a u16 at data VA 0x66C59C (config + 0x24). It ships as 0xFFFF and is
replaced at load by the system language: 0 -> 0 ja, 11 -> 2 zc, 10 -> 3 zt, anything else ->
1 en (VA 0x407F0). The options menu cycles 0..3; index 4 (ko) is never produced. The getter
VA 0x3ED90 (used by layout, fonts, UI, bands, movies) returns the stored value if < 4, else the
system mapping; `$`-expansion and `UiText_Get` read the raw word.

| asset | how the language picks it |
|---|---|
| script text | section pair 2L / 2L+1 of script_text.mrg |
| fonts | `ft_<lang>.mrg` (VA 0xC6980) |
| ruby | disabled when the layout language is English |
| text box width | 574 units in English, 474 otherwise (VA 0x538F0); first-message origin (30,35) en / (87,49) others |
| UI sheets | `<base>_<suffix>.mrg`, suffix table relro 0x42C030 (`ja en zc zt ko`) |
| four-band pictures | band L of the composited picture (VA 0xC2620) |
| caution screens | `caution_<lang>.mzp`; `caution1[_xx]`, `caution2[_xx]` (Japanese suffix-less) |
| parts.mrg | index groups ja, en, zc, zt, ko (ko = copy of en) |
| movies | VA 0x5E280 (no `_zt` openings: zt plays `_zc`) |
| UI strings | `ui_text[lang][id]` |
| save icon | `data/if_<lang>.png` |
| choice pictures | none: choices are font text in every language; SELECTTXT_PARTS is loaded but never drawn (system_flow 5.4) |
| title background | not language-dependent: TITLE_PARTS child 9 + T[G[2]] and `titlebg1..4` (system_flow 4.2) |

### 5.4 Data that exists only in eboot.bin

A port must read these from the user's eboot.bin (v01.01 addresses; verify the build first, see
RUNTIME_REQUIREMENTS 3.3). The Python modules named are working extractors.

| table | location | size | used for | extractor |
|---|---|---|---|---|
| image catalog | data 0x671900 (file 0x3D78A0) | 14,775 x 0x48 | picture layers, sizes, anchors, bands | `tsuki.image_catalog.load_catalog`, `tsuki.catalog_eboot.Eboot.image_table` |
| picture-name list | data 0x7F6800 (file 0x55C7A0) | 14,744 + 1 x 32 B | script picture id -> record | `tsuki.image_catalog.load_picture_names` |
| second name list (names_sprites) | data 0x7A8B10 | 9,958 x 32 B | CG name lookup of the gallery (0xA04B0; equals the first 9,958 names of the picture-name list) | `tsuki.misc_gallery_extras_flowchart` |
| scene ids | text 0x216130 | 549 x i16 | `_JUMP`, title code | `tsuki.script_tables.read_scene_ids` |
| movie table | data 0xDC8C70 | 134 x 0x30 + terminator | `_TOMP`, engine movies | `tsuki.script_tables.read_movie_files` |
| prologue language table | text 0x200F70 | 4 ids | movie remap | `tsuki.catalog.movie_index_for_language` (constants) |
| motion tables | text 0x274538, 0x2A3938 (by id - 1), 0x245130 (flag words, by id); data 0x873FF0; loop points = code switch 0xC1C70 | 24,192 motions, 71,905 keyframes, 2,027 loops | `_STMA` / `_SGMA` | `tsuki.script_tables.read_motions`, `tsuki.misc_layer_state_and_motions.MotionTable`, `samples/misc_layer_state_and_motions/motion_loops.json` |
| font lookup arrays | data 0x626C10..0x66ACB0 | 16 arrays, 17,423 x 16 B | glyph lookup | `tsuki.font_eboot.EbootFontTables` |
| font rectangle tables, wide bases, tag names | text 0x177370..0x187E2E, 0x187C80 | 17 tables, 6,771 x 10 B | glyph rectangles | same |
| layout tables | text 0x2D34C0 (colours), 0x2D34E0 (style flags), 0x2D3580 (icons), 0x2D3730 (scales), 0x2D3758 (`@f` widths), 0x2D32C0 (`@` jump table) | small | markup | `tsuki.textbox` constants |
| window-picture tables | text 0x2006C0, 0x200960, 0x200BB0, 0x200C0C, 0x2005B0, 0x200620, 0x200B20 | small | message windows, bubbles | `tsuki.textbox.WINDOW_TABLE` |
| UI text | RELRO 0x41D5F0 | 5 x 1,499 pointers | all UI strings | `eboot.uitext` |
| UI texture registration tables | data 0x66AD10, 0x66BA80, 0x66BBC0, 0x66C9A0, 0x780C90, 0x781B70, 0x7A6090, 0x7A6B20, 0x7A67F0, 0x7A7680, 0x7A8300, 0x8730F0, 0x8731A0, 0x873660, 0xDB9320, 0x78B2C0 (+5 dead) | 21 tables, 220 records | UI sheets -> texture slots | `tsuki.catalog_eboot.UI_TABLE_VAS` |
| title background table | text 0x218A40 | 6 x i32 {1,2,3,3,2,4}, indexed by G[2] | title record-0 child and titlebg movie (system_flow 4.2) | `tsuki.misc_boot_title_choice_flow` |
| title return table | text 0x209DC0 | 0x22 entries | title result -> mode / start scene (only 0, 2, 3, 7 reachable) | same |
| music player | text 0x1A47D0 | 95 x 0x38 | music room: track, title / credit ui ids, length, loop points, 5 variant tracks | `tsuki.misc_gallery_extras_flowchart` |
| legacy music-mode table, BGM names | text 0x209510; data 0x7A8820 | 55 x 16 B; 22 names | dead (extra_parts screen unreachable; name lookup 0xA3A40 has no caller) | same |
| rule-mask names | data 0x869B30 | 33 x 32 B | dead: the only reader 0xA3530 has no caller (compositing 7.3) | - |
| blend / effect / transition tables | text 0x2D7CAC (blend jump table), 0x2D5978 / 0x2D59AC / 0x2D55E0 (effects), 0x2D5860 (STBR -> effect), 0x2D8030 (blend states), 0x209FF0 (second-capture types), 0x209C50 (transition draw cases) | small | compositing | `tsuki.compose`, `tsuki.misc_compositing_transitions_shaders.read_engine_tables` |
| config defaults and menu pages | data 0x66C578 (initial image, 0x140 B) + code 0x3EAC0; menu pages 0x66E890 (stride 0x9D0) | 0x140 B; 2 pages | first-run settings, config screen | `tsuki.misc_adv_system_state` (`config_fields.json`, `config_pages.json`) |
| action mask presets | data 0xDB84F0 | 3 x 27 x u64 | input actions | same (`actions.json`) |
| history F-index lists | text 0x2160E0, 0x216580, 0x2165F0 | 39 / 43 / 300 indices (276 used) | which F values a history record keeps | `tsuki.vm_vars.history_storage` |
| voice groups, character masks, system SEs | text 0x215EA0 (16-byte records, sentinel `ZZZ` at 0x215E90); data 0x86A850 (u32[24]); text 0x2D3770 + data 0x6241F0 | 3-letter group -> character -> config slot; system SE id -> vag pair | voice gating, system SEs | `tsuki.misc_audio_runtime_semantics` |
| gallery / flowchart lists | RELRO 0x400830 (458 CG groups), 0x405240.. (1,972 CG entries), 0x404170 (105 aliases), 0x4051E0 (11 four-frame names), 0x418770 (277 flowchart columns); text 0x18FC90.. (1,286 nodes), 0x2218D0 (630 edges), 0x2214E0 (choice edge flags), 0x18F810 (25 ending-list entries); data 0x86AA30 (2,144 seen-index records) | see gallery 1 | gallery, flowchart, unlocks | `tsuki.misc_gallery_extras_flowchart` |
| legacy CG list | data 0x78C810 via RELRO 0x41C530 | 1,314 x 56 B | dead (extra_parts screen) | same |
| shader binaries | data 0xDCA6E0..0xDD66B0 | 67 blobs | reference only (port writes its own shaders) | `eboot.shaders`, `tsuki.gcn` |

### 5.5 Coordinate spaces

| space | used by | to the 1920x1080 frame |
|---|---|---|
| script space 480 x 272 | STCH x / y, FCLN | stored x*4/3, y*4/3 (engine space); 1 unit = 4 px |
| engine picture space 640 x 362.67 | catalog `w3/h3`, anchors, scenario renderer (VA 0x4F320) | 1 unit = 3 px |
| text space 640 x 360 | glyph positions, text origins, `@x` | 3 px per unit (prologue movie: 3.115 vertically, open) |
| UI canvases (640x448 most common; 960x540, 960x544, 480x272, 640x344.7, 1920x1080, 1x1, ...) | each UI module via VA 0xDCBE0 + scale 0xD9DD0 | `px = x / sx` |
| pointer 640 x 448 | mouse / touch | x*3, y*1080/448 |
| picture pixels | decoded MZP | 1:1 at scale 1000 and zoom 1000 |

### 5.6 Worked chain: the opening line

`00_02` (scene 270), page 1:
`_STCH(6,img1685,240,136,1000,255,162,,CC,,,);` -> name list -> record 1692 -> name0
`IMG1685.MZP` -> NAM search -> allpac0 #270 (dark tatami room) ->
`_VPLY(B90_00_01_0001_0,03d04);` -> voice.hed entry 0x3D04 = 15,620 (`voice.nam[15620] = "B90_"`)
-> LV + MZX0 -> ATRAC9 mono 2.25 s ->
`_ZM01d04($000003);` -> marker block 0x01d, sub 4 -> line 3 -> en (two spaces, then a
68-character sentence; the ja line is U+3000 plus 25 characters with three ruby groups, `text.md`
2.3) -> Text_Layout at width 574 (en) -> glyphs from
`ft_en.mrg`. The same page is burned into `prologue_en.mp4` (frame at 25 s), which is what
New Game actually shows (00_01 plays the movie; 00_02 is reached only from the debug menu).

---

## 6. Corrections not yet applied to the format documents

Independent re-checks confirmed the findings below after the documents were last revised.
Implementers should follow these, not the original text. Items marked **Applied** have since been
corrected in the document itself. Where a follow-up document proved errata for an older document
(its own "Errata for ..." section, not applied there), they are listed at the end of that
document's subsection; 6.9 holds the open review findings against the follow-up documents
themselves.

### 6.1 images.md

- About 0.2 % of records (5 of 2,948 checked) show the art at a different offset in the
  engine-made allpac2 thumbnail than the (0,0) framing assumed by `CatalogResolver.render`
  (ARU_L_90_00_N02 / N06 / N07 y -54, KRO_L_31_03_N00 x -94 y -62, KUR_M_00_00_N00 x -26,
  Z20_L_11_00_N00 x -36 y -12). No known catalog field explains it; this is also the likely
  explanation of the KRO size anomaly (8.4). **Resolved** (layers 10): it is not an engine rule;
  the draw places the MZP texture at position - anchor and never reads the catalog width /
  height, so the offsets exist only in the engine-made thumbnails.
- 7 / 8 / 11 (layers 14): the `scale_class` factors 0.5 / 0.25 / 0.125 (L / M / S) multiply
  child positions and zoom under a parent, `_STPV` anchors and motion anchor fields 6 / 7 (those
  use the scene picture's class); `anchor_x / y` is the picture pivot; the STCH test of catalog
  +0x3C bit 7 is dead code for retail data (every record has kind 0x0B).
- 8, picture-seen sentence (gallery 2.2, 14): the bit array holds 3,200 picture bits; ids >=
  0xA8D are mapped through the 2,144-record table at data 0x86AA30 (seen indices 2701..2917) and
  `_hh_vv` variants first resolve to their base picture (Image_ResolveSprite 0x957B0).
- 8 anchor_x row: "the centre for 6,333 non-sprite records" cannot be right (there are 6,191
  non-sprite records; 6,336 records in total have the exact floor centre).
- 2.3: the tile-0 pre-decompress runs only for `bmp_type` in bitmask 0x19CA (1, 3, 6, 7, 8,
  11, 12); all retail types are in it.
- `samples/images/index.json`: the alpha bbox of `catalog_IMG2431_language_bands` does not
  describe its files. CLI: `python -m tsuki.image allpac3 54 --info` (an archive entry without
  `--child`) and `image_catalog --render 11574` (missing base) end in raw tracebacks.

### 6.2 text.md

- "Line id i is the same sentence in every language" is too strong: in the tips / QA corner
  English splits sentences across ids differently (ids 43563..43568).
- 5.7: the zc bands of IMG2459 / 2460 / 2461 differ from the script_text zc lines (leading
  `――`, no quotes / full stop, IMG2460 also different wording); ja / en / zt match.
- 3: the ja longest line (590 bytes) is a tie between ids 35103 and 38530.
- 2.1: the whole table is only non-decreasing (the two guard entries are equal); offsets are
  strictly increasing over the N+1 string boundaries.
- 5.3: `ScriptText.expand('$12')` raises IndexError (out-of-range id) instead of returning text;
  `text_refs where` hides the `_SELR` locked flag.

### 6.3 script.md

- 6.4: STCH / SCH2 produce 10,924 distinct effective keys; the 10,967 total also counts 43 ids
  used only by SPFC / SPRD / STCG.
- 4.5: by value (not literal string) STCH x = 240 occurs 24,422 times and y = 136 14,852 times;
  4,238 of the 4,302 z = 2 backdrops at (240,136) are 2070x1230 (64 are 1920x1080).
- 4.3a arg 19: stored as an i16 at layer +0xCB0 after `r = v % 36000` (C remainder), +36000 if
  r <= -18001, -36000 if r >= 18001 (range [-18000, 18000]).
- 7: the prologue movie is only approximately 00_02 (the 60 s frame shows lines 12..15 as
  text; 00_02 shows those ids as image cards).
- 4.1 script-end cleanup: when the keep-BGM flag is clear it also writes 0xFFFF to word VA
  0x16547D0 (0x7F277) before calling 0x9AE0.
- `tsuki/script_emu.py`: after two idiv-by-zero exceptions unicorn stops faulting and returns
  the dividend; the engine would crash. Published comparisons skipped those cases.
- Several [D] counts reproduce only to within definition differences (e.g. WNTY(1) page end
  15,673 / 15,759 vs 15,663); mypy needs `--ignore-missing-imports` for capstone; `dump 549`
  and `dump #99999` raise raw tracebacks.

Errata proved by the follow-up documents (not applied to script.md; each names its proof):

- **Layer commands** (layers 14): `_STCP(l, v)` sets the layer's z / draw order (scene +0x2FC,
  absent v = 0), not a "per-layer parameter". `_STZ4` a7 is a pivot flag (zoom in screen space
  after rotation); start values, time and easing drive only the live tween, the scene keeps the
  end values; `_STRT(l, r0, r1, ms, e, a5, a6)` likewise; `_STRR` sets an angle (+0xCB0 / +0x15C)
  that no draw code reads. `_STPV`: anchor = `trunc(x*640/480*size class)`, `trunc(y*480/360*size
  class)` plus the variant anchor difference; a3 sets the rotation. `_SOXY`: only a1 / a2 (x / 3
  and y * 448/1080 canvas units). `_STEC`: modes 0..81, colour stored only for mode 1. `_SNX2` /
  `_SNX3` = keyframe fields 24 / 25. `_STNS`: n >= 13 -> 0. `_STTR`: stores `(n-1) | m << 12`;
  the layer's next `_STCH` / `_SCH2` resets it to -1. `_SPMA`: pause = 1 only for an argument of
  exactly 1.
- **`_STMA(l, id, w, n)`** (layers 8.2): n is a time-quantisation step (`frames - frames % n`),
  not a speed divisor; id <= 0 stops the motion (divisor and counter kept); key 0 is applied at
  once and motion values are written into scene_state, including a switch of the scene picture
  to a `_hh_vv` variant (fields 22 / 23). **`_SGMA(l, id, key)`** writes the layer's drawn x / y,
  alpha, rotation, zoom and crop into keyframe `key` of motion id (in-memory table), so the next
  `_STMA` starts from the current state; it is not a "motion variant".
- **4.3a** (layers 4.1): `_STCH` does not reset flags, parent, `_SOXY`, mask, crop, a16, a17,
  angle, `_STRA` or `_STNS`; `_STCH` and `_SCH2` reset `_STTR` to -1; with a source layer they copy
  its motion record (a self-source statement stops the motion) and re-point mask and parent
  references; an empty picture name clears the layer; arg 11 = mask layer, args 12..15 = crop
  rectangle in picture pixels, 16 / 17 = edge parameters.
- **6.6** (layers 8.1): keyframe record = u64 time, fields 1..7 i16 at +8, padding, field 8 i64 at
  +0x18, fields 9..27 i16 at +0x20; duration / end tables by id - 1, flags by id; fields 1 / 2 are
  segment easing and interpolation type; motions are absolute. 4.5's open items (SBAS pivots,
  size-class factors, projection) are resolved in layers 7.
- **`_STBR`** (compositing 12): arg 2 is ignored; arg 3 == 1 sets a per-layer flag (weight =
  opacity x picture alpha for modes 5 / 7 / 9, blending off for 5 / 7 / 9 / 10 / 15 / 17; see 6.9
  for masked layers), never cleared by `_STBR` itself (`_STCH`, `_STCC` and the scene reset clear
  it, `_SCH2` with a source layer copies it). Modes are 0..25, plus one mode 28 in an `_IFNX`
  branch.
- **`_FADS`** (compositing 7): the rule is not loaded in fast-forward and `_FADS(2, ms, 0)`
  reuses the rule in slot 0x4DD; arg 3 controls the message-window word 0x164BA6E; duration =
  ms / 16.666 frames; cross-fade weight `trunc(255 p) / 255`. **`_STTR(layer, rule [, x])`** also
  loads rule `rule` into that slot (0x99A26), or frees it for rule <= 0. **`_STEC`** is a CPU
  recolour of the picture at texture build (81 ids), **`_SNX2`** a brightness offset and
  **`_SNX3`** a contrast (0xD36F0); **`_FADE`** is a screen-effect command (kinds 40 / 41 in
  scripts).
- **Choices** (vm_variables 11, system_flow 9.4): SELA / SELS row (line 564): `_SELA` is mode 3,
  the "Do you want to save your progress?" prompt (ui_text 216): it opens only when F1118 = 0,
  always sets F1118 = 0 and stores no result. `_SELS` is mode 2 (prompt ui_text 1446 + F[27])
  and stores the button index 0 / 1. In scan mode neither opens. SELD row: the stored value is
  argument 0 of the chosen `_SELR` line; argument 1 is the choice-point number N (SELECTTXT
  child, taken bit u8 0x164FBC0[N], skip stop, flowchart marking 0xB8620), not a cursor default
  (the cursor starts at -1). A 10th `_SELR` of a block is numbered "0." (line 43959). Open
  question 9 is answered: the title actions that set scenes 9000+n, 190 and 1800 are entries of
  jump table 0x209DC0 that the title never returns (system_flow 4.1).
- **Audio** (sound_runtime 14.4): 3.4 tag table: `` `011 `` = `_MFAD` wait switch / `_MPLY`
  old-track fade time; `` `018 `` = pan; `` `049 `` = loop (1 forever, else once); `` `068 `` =
  delay (deferred command); `` `070 `` = character (config slot + 1), not a repeat count.
  `_VCWT` waits on the voice state 0xDD8E50 (confirmed).
- **3.6 and the ATSV row** (adv_state 12): scan mode is used only by the fallback load 0xA3050,
  for a slot without the 0x2AF8 end marker; a normal load restores the saved record through
  0x7E790 / History_Restore. `_ATSV()` marks a scene start (history kind 6 + scene stack),
  `_ATSV(1)` a choice save point (kind 7); only `_ATSV(8)` auto-saves.

### 6.4 archives_ui_movies.md (one major)

- **Title table record 0 is rewritten before every load** (major). Function 0xAFCE0 (which
  contains the title entry 0xAFD8C) stores `child = 9 + T[state]` into data 0x873670 with `T =
  {1,2,3,3,2,4}` at text VA 0x218A40 and the state from flag checks 0x230 / 0x17F / 0x232 via
  0xB81A0 (child 9 when word 0x164C634 == 0), and stores `T - 1` (0..3, else -1) at 0x873650,
  most likely the titlebg1..4 choice. The same pattern is at 0xAFC73 / 0xAFCC2, 0xAFEEF /
  0xAFF02 and 0xB04C3 / 0xB0512. Correct lookup split: **695 direct, 4 clamped, 8 rewritten**
  (not 699 / 4 / 4). TITLE_PARTS children 10..13 are four 1920x1080 moon / sky backgrounds,
  child 9 is a 2670x1266 glow layer. A port that follows 7.2 shows the wrong title background.
  **Decoded in full** by system_flow 4.2: the state is G[2], advanced one step per call by the
  route bits 560 / 383 / 562; T is 6 x i32; the split 695 / 4 / 8 is confirmed, and SELECTTXT
  record 1 is rewritten the same way (system_flow 9.1).
- 7.5 canvas census misses 7 tail-call sites (`jmp 0xDCBE0` x6, `jmp 0xD9DD0` x1), e.g. three
  more 640x448 canvases.
- 4: five code sites reach the image table through the GOT slot 0x435238 and were missed:
  the STCH backend tests `byte [rec+0x3C] & 0x80` and reads `[rec+0x3E]` (0x828D8..0x82902);
  the save-thumbnail code (0xAC9AC, 0xAD798) passes records to 0xC2730.
- 4: in the 31 empty slots 0xFFFFFFFF sits at +0x38 (anchor d), not +0x3C.
- `ScriptImageResolver`: the engine map is built with operator[] (last duplicate wins) and
  includes the empty record 14,744, so the engine's lookup("") returns 14744; no retail effect.
- 2.4: on the a18 path the backend also replaces the new layer's anchor (layer +0x4E0 / +0x530)
  with the new record's (c, d) when layer a18's anchor equals its current image's (0x81C52..).

Errata proved by the follow-up documents (not applied to archives_ui_movies.md):

- 7.5 (system_flow 9.1): the scale sites of the 640 x 344.7 choice canvas are 0xA75EF
  (underline), 0xA764F (flare), 0xA7716 (phase-3 ring), 0xA77A2 (blue bands) and 0xA77ED (lens
  flare).
- 5.2 (system_flow 5.4): SELECTTXT is never drawn in any language, Japanese included; choices are
  always font text; record 1's child N is `_SELD` argument 1.
- 6, parts.mrg (system_flow 3.3, 9.1): #1 and #19..23 are used only by dead boot states; #2..6
  are the save-damaged notice (system language); #7 / #8..12 / #13..17 / #18 the autosave notice.
- 7.6 (gallery 14): RELRO 0x404170 is not the flowchart but the CG alias table (105 x 0x28),
  followed by the 11 four-frame CG names (0x4051E0) and the 1,972 32-byte CG entries of the 458
  CG groups at RELRO 0x400830; the flowchart is RELRO 0x418770 (277 columns) with 64-byte nodes
  at text 0x18FC90... Data 0x78C810 (1,314 x 56 B via RELRO 0x41C530) is the legacy CG list of the
  dead extra_parts screen.
- 7.2 / open question 7 (gallery 14, system_flow 4.1): the extra_parts cases 0x656D0 / 0x66380
  are title actions 5 / 6, which the title update never returns: unreachable.
- 5.4 / open question 12 (compositing 12): the mask table is DEFAULT plus 32 non-contiguous MASK
  names at VA 0x869B30 (file 0x5CFAD0), and it is unused.

### 6.5 engine.md (two major, now applied)

- **Script_Prefetch's V case accepts only `VPLY` and `VPL2`** (major; 0x9BD0F `cmp r13,'PLY'
  / 'PL2'` with `setne`): the table has 43 entries (41 names + `J***` + `ZY**`), not 42 with a
  `V***` wildcard; "uses" = 16,705. **Applied** by the vm_variables work to engine.md 5.4,
  `tools/eboot/scriptvm.py`, `samples/engine/vm_commands.json` and `test_eboot.py`; sections 11
  (line 1343) and 12 (line 1381) of engine.md still say 42 entries, and 10 item 12 (line 1327)
  should read `_VPLY` / `_VPL2` / `_PLDV` instead of `_V***` / `_PLDV` (vm_variables 11).
- **The `C<n>` operand is missing** (major): 85,471 `_WKST` commands write `C847` / `C859`.
  `C<n>` as a write target addresses `F[F823 + n]`; in value position it is the literal n.
  **Applied** in engine.md 5.5 (vm_variables 2.3 has the proof).
- 7.2: the slot area ends at 0x3E0900, so the unused area is 0x3E0900..0x400000 (0x1F700
  bytes), not 0x3E0B80 / 0x1F480. **Applied** in engine.md 7.2.
- 5.3: VM segmentation gives 1,083,168 segments (each script ends with `;`), not 1,083,717.
  Still unapplied in 5.3 (line 782) and 11 (line 1348); `samples/engine/script_cmd_counts.json`
  (`_segments`) has the same counting error.
- 5.5: image-table names match allpac2.nam only case-insensitively (exact match 1,221).
- 5.4 prose: the loop continues while count <= target; the early exit passes ebx = 0xFFFFFFFF
  to 0xC3B00.
- `eboot/nids.py` docstring shows an 8-byte suffix; the code correctly uses 16 bytes.
- `python -m eboot.savedata` on a missing path or a directory gives a raw traceback.

Errata proved by the follow-up documents (not applied to engine.md):

- 5.6 (vm_variables 11): the i8 F list 0x2165F0 has 276 used entries (it ends at the first
  negative entry); the bit list's first index 998 is also an immediate in the code.
- 4.1 / 4.4 (compositing 12): DisplaySurface +0x5C4 is the sepia blend, +0x60C the effect
  strength, +0x5FC = 1 - strength, +0x600 = strength, +0x604 = P2 (0 for modes 5 / 7 / 9 / 10 with
  blend state 0); transition parameters live on the texture (+0xF0..+0xFC). The 16 nexl_Capture
  surfaces: image records 1..15 show nexl_Capture[0..14], record 0x10 shows nexl_Capture[15]; a
  type-6 display-list record copies the current render target into nexl_Capture[b]; every
  transition copies into [0], 23 types also into [15]; the cross-fade draws [0], the rule wipe
  [15].
- 4.3 (compositing 12): +0x2D0 is the bound texture when it carries transition parameters; the
  dword passed to 0xF0830 is the blend-state index (+0x518); `_STBR` -> +0x608 is resolved
  (ordinary layers emit word = (flag << 8) | mode); the Photoshop formulas are superseded by the
  ISA-derived formulas (compositing 2.3). 4.2 / 4.4: "transition 2DTex" samples the rule
  picture and writes the mask into render-target alpha; the 33-name DEFAULT / MASK table is dead
  (its only reader 0xA3530 has no references).
- 8.1 (system_flow 9.2): there is no abmp_logo screen and BG_TOP.MZP is never read. The order is
  system save -> [save-damaged notice] -> autosave notice -> title intro (TM_CI + caution2, or
  prologue -> PRESS ANY BUTTON -> caution -> caution1). 8.7: title-background looping is
  confirmed: MoviePlay(loop = 1) plus one seek to 8.0333 s per wrap; the still (slot 125) is drawn
  until the seek lands.
- 8.4 (sound_runtime 14.3, adv_state 12): there are 19 (not 20) u16 per-character voice
  volumes at +0x76..+0x9B (16 from 0x1A66A0 + 3 x 50); +0x9C / +0x9D are the two "Display End of
  Page Instantly" bytes (default 0); +0x16 is the "Play All Voices" switch (100 / 0); +0x54 is a
  copy of +0x14 (movie volume); +0x1C = lower BGM during voice; +0x4E = stop voice on advance;
  +0xA6 / +0xA7 / +0xA8 = mutes; config +0x40 has no effect (its only test cannot be reached).
  8.6: the HE-VAG player is used only for system SEs. 8.5: the action masks test the game input
  word built at 0xE3A40, not the pad bits.
- 5.6 / 7 (adv_state 12): 808 history records are allocated (0xB74280 bytes, VA 0x753E0), the
  ring uses 800; record +0x24 is the kind, +0x26 the BGM track, +0x28..+0x4F one i16[20] loop-SE
  id array, a second scene copy sits at +0x1A60 (0x1651AC8), +0x36BC is never written by the push
  (engine bug). Rewind undoes every record back to the previous page start; a load restores
  without re-display and runs the saved command again. `_ATSV` (0x95F5D) is the scene-start and
  choice-save-point command (`_ATSV()` kind 6 + scene stack, `_ATSV(1)` kind 7, `_ATSV(8)` kind 6
  + auto save).
- 7.2 / 7.3 / 7.4 (adv_state 12): SaveImage_Commit stamps 0x2AF8 on all 140 slot blocks; slots
  0..99 manual, 100..138 auto-save FIFO, 139 quick save; state B is the scene-stack top record;
  the quick save sets no F1118 bit; +0x16F2 is only round-tripped; commit mode 0 (quick save)
  leaves +0x16EE unchanged.
- 7.3 (gallery 14): system +0x52C (RAM 0x164D5C0) holds picture_seen bits 0..3199 and bgm_heard
  bits 3200..3719, OR-merged on load; +0x16F4 (RAM 0x164FBC0) is choice_taken[choice id] (bit =
  option), OR-merged; G is not merged (the OR index list 0x86AA14 is empty). See 6.9 for the
  exact merge length and when G is copied at all.
- 5.5 (gallery 14): names_sprites 0x7A8B10 is used by the gallery (0xA04B0) and equals the first
  9,958 names of names_allpac2. 13: SDTA / SDTB (0x869F80) is confirmed unreferenced.
- `eboot/savedata.py` (adv_state 12): `parse()` reports "end magic 0x0000" for every empty slot of
  a game-made image (139 false issues); only slots with used > 0 should be checked.

### 6.6 audio.md (the six review findings are applied)

Findings (a)-(f) are now corrected in place in audio.md, with a "Corrections" table (proof:
sound_runtime 14.1):

- (a) `_VPLY(A10_07_00_0147_1,01b40,0)` has a third argument: voice #6976 is referenced, giving
  16,705 refs and 16,459 distinct names.
- (b) The BGM start (VA 0x98C0) stops the BGM on a negative track (`js 0x9A31`) and ignores a
  request for the track already playing (u32 0x62437C).
- (c) The gain is `trunc(config * vol * 0.01) * 0.01` for BGM (and voice).
- (d) Float ATRAC9 decode overshoots up to 1.878 full scale (645 of 1,327 vse), not 1.30.
- (e) The RIFF table row at offset 0x64 is 8 bytes (two u32), not 4.
- (f) The HE-VAG copies are polarity-inverted with a 28-sample lag against their ATRAC9 copies;
  aligned SNR 22.4..26.1 dB.

Further errata, not applied to audio.md (sound_runtime 14.2, gallery 14):

- 5.1: vtable +0x70 is *stop*; the voice plays on stream `2 + (n & 3)`, rotating over 2..5 (VA
  0x8DEC..0x8E8F), not "stream 2 via +0x70".
- 4.2: the `_SEPL` lookup uses flags 3 (or 2 * loop + 1 without `` `017 ``), never 0, so section 0
  is never searched; `` `018 `` = pan 0..128 (64 centre), `` `068 `` = delay in ms (deferred
  command), `` `070 `` = character slot + 1.
- 2.4 / 6.3: BGM loops by default (`` `049 `` defaults to 1; the only exception is
  `_MPLY(122,,`017:100,,`049:0)`); the vag sounds are triggered only by SystemSE_Play 0xCC670.
- Music mode: text 0x209510 (55 records) is the legacy table of the dead extra screen; the live
  music player is text 0x1A47D0, 95 x 0x38 `{bgm, title_ui, 4 credit ui ids, length_ms,
  loop_start, loop_end, 5 variant tracks}`; its loop fields equal the RIFF smpl loop points after
  subtracting the 256-sample encoder delay (94 / 95 start, 88 / 95 end; 6.9).
- Open questions 3, 4, 5, 7 of audio.md 10 are answered by sound_runtime (sections 0 / 1 unused,
  vag = system SEs, tags, voice group -> config slot table in sound_runtime 8).

### 6.7 fonts.md

- 7.1: pass 2 is also bounded by the pass-1 element count (max 1200), and pass 1 has its own
  `@` length rules, so `@X(` groups after a no-parameter code drop the tail of the string
  (28 of 2,032 synthetic cases; none in the data). textbox.md 5.1 / 5.3 now documents both.
- 5.4: an invalid lead byte (or `@` before a multi-byte character) does not hang: each
  zero-length element re-emits the previous quad until the budget runs out.
- get_language() maps index 4 (ko) to the system language, so ko layout follows the system
  language's English rules; ko is never selected.
- 5.5: in language 5 every key >= 39 returns the [zap00] rectangle (tsuki matches).
- Emulation trap: unicorn 2.1.4 executes VEX 3-operand SSE instructions as 2-operand forms;
  emulating 0xC6C20 needs the `vexfix` workaround (textbox.md 10.3 uses it).

### 6.8 textbox.md vs fonts.md / text.md

textbox.md section 13 corrects fonts.md 7.2 (spacing 0x39CFD belongs to UI widgets; the main box
uses spacing 0), fonts.md 7.4 (ruby placement is now known) and text.md 6.2 (`@h` / `@w` scale the
undivided argument), and answers text.md open question 4 (`@f` = box widths, `@x` digits =
absolute pen position).

Errata for textbox.md from the follow-up documents (not applied there):

- 5.8 "C<n> is `F[n + F823]` [H]": verified by emulating the `_WKST` / `_WKAD` handlers, for
  write targets only; in a value slot `C<n>` is the literal n (vm_variables 2.3, 11).
- 8.3: 0x41AB0 is the voice-enabled test (config +0x16 != 0 and +0x36 != 0), not auto mode;
  8.1: config +0x20 / +0x22 are the Unseen / Seen Text Display Speed options (adv_state 9.2, 12).

### 6.9 Review and re-check findings not applied to the follow-up documents

Each follow-up document was reviewed, and five of them were repaired and re-checked. The
findings below were confirmed by their reviewers (with the scripts named) but are not yet in the
documents or models. A port should follow them. "Re-check" = the second review after the repair.

**vm_variables.md** (re-check: the repair's "every reader covered" claim is refuted by one major
finding):

- **(major) `_SELD`'s per-line loop is not modelled.** 0x9B228..0x9B430 runs once per pending
  `_SELR` line k: it writes F[799] = (k == count - 1) (0x9B273..0x9B281), calls History_Snapshot
  with kind 0xC (0x9B288), and, when byte 0x174D9A8 == 0, copies the line record (0xFA bytes at
  0x174FB70 + k * 0xFA) into the current history record and may call Staging_SetSnapshot
  0x73980. F799 is missing from the doc's F tables (2.1, 6.3), and section 8's script-4 example
  omits the three F799 writes and kind-0xC snapshots. The harness restores the eboot image before
  every statement, so the line count was always 0 when `_SELD` ran; all 170 retail `_SELD` have
  1..10 pending lines (824 in total). Proof: `scratch/review_vm_variables_errata/r2/rv2_seld_lines.py`,
  `rv2_chain.py`, `r2dis.py 9B23A:9B540`, `seld_pending.py`.
- The quick save 0x73A70 (slot 139) never calls 0x76500, so it keeps the staging's F1118
  (normally 0) and a quick load can reopen a `_SELA` prompt; "every slot store sets F1118 = 1"
  holds for Slot_Store only. adv_state 7.2 states this correctly.
- The choice line count 0x1750646 returns to 0 only in the choice UI (0xA6518, 0xA6A4A,
  0xA6BDF); `choice_store` leaves it unchanged, so a model would treat a second choice group on a
  page as a continuation (no snapshot, no C847 += 1). system_flow 5.2 has the reset.
- `_SELA` / `_SELS` decode short targets (`_D`, `_D1`) from the object parser's 16-byte stack
  buffer, not from the argument buffer (doc 3.5 is inaccurate); retail always uses `_D000`.
- Conflict with adv_state: vm_variables 1 item 8 calls scan mode "the re-execution after a slot
  load"; adv_state 7.3 (emulated) shows a normal load uses History_Restore and scan mode is only
  the fallback 0xA3050 for a slot without the end marker.

**layers.md** (re-check: the projection claim is refuted by one major finding):

- **(major) 7.2 quad formula.** The engine rotates, scales and zooms about the integer anchor
  (+0x76 / +0x78, read at 0x5183D / 0x5186C) and then adds the residual (off + anchor) without
  transforming it (offset +0x50 added at 0x51A1C). The documented M is right only while off ==
  -anchor; motion fields 6 / 7 set anchor = trunc(X) and off = -X on every frame, so 453 motions
  used by 488 retail `_STMA` statements are drawn up to 1.63 canvas units (4.9 frame px) off;
  mirrored sprites s307#228 (motion 8776) and s351#10 (motion 10421) are 2 px off for the whole
  motion. The corrected rule matches the engine within 0.0002 units, except with the STZ4 pivot
  flag (1.35 off). Proof: `scratch/review_layer_state_and_motions/` `proj_fuzz.py`,
  `proj_dbg5.py`, `proj_retail_anchor2.py`. The slice's motions 22523..22526 (and 00_00's motions
  1..5) do not animate fields 6 / 7 [V: flag words checked].
- 8.5: for the tween call shape (d = -1, t in [-1, 0]) the in-out / out-in split `t < d/2` of
  the non-ported easing ids is wrong (relative errors up to 14.9); no retail effect, since retail
  tweens use only ported ids.
- 6: the per-frame update passes dt = trunc(arg * frames) with arg = dword 0x7A6180 (default 1)
  and negates it while word 0x17DB234 != 0, so motions then run backwards (the model handles
  negative dt; 3,335 emulated calls, 0 mismatches).
- `_FADE`, `_SPFC`, `_SPRD`, `_SPRC` get no argument table (only their globals) and are missing
  from the open questions.
- 5: `commit()` omits the motion record, the colour block (0xF760 transform), shakes and STRA,
  which the commit also writes; the B -> A move is described loosely, and sections 5 ("the next
  update") and 6 ("when a transition starts or ends") disagree.
- Small numbers: 53 (not 57) motions start late; 11.1 says worst 3e-5 where 11.3 says 0;
  `RENDER_FIELDS` still calls +0x162 the "speed divisor"; section 10's art bounds depend on an
  unstated alpha threshold; `_SGMA` writes keyframe `end(id-1) + key` without a bounds check while
  `edit_motion` silently skips out-of-range keys.

**compositing.md** (re-check not refuted; minor):

- The `_STBR` flag is inert for modes 10 / 15 / 17 only on ordinary layers: the masked-layer path
  (0x4F320) also takes it and selects blend state 0 for effects 36 / 41 / 43, which drops the
  render-target-alpha mix (rgb changes up to 0.76).
- 7.3's list of rule-slot frees misses 0x7929A (function 0x79170, tail jump to 0x6B460(-1)).
- `compose.transition_frame` cross-fades for type 49 (the engine draws nothing) and for the
  undecoded types 18, 20..24, 68..70 (where `fads_capture` raises); script types 0..2 are right.
- 6: when byte 0x1DBD0A4 == 1 (set at 0xD2F56 from the ramped float 0xDB9470), 0xDB670 runs
  lists 0 and 10 through a second executor 0xDB6D0 (jump table 0x2D54E8); not examined.
- 7.2: the frame count is max(1, min(3, frames)) (0x69581); whether the update 0x68A70 runs
  before or after the draw 0x67820 within a frame is not pinned down (one-frame ambiguity of the
  first and last transition frame).

**system_flow.md** (re-check not refuted; minor):

- engine.md 8.1's first-run "Create save data." dialog is neither confirmed nor corrected; the
  string sits in a UI-text block with the save / load texts (data 0xD7DC20), so it may not be a
  boot dialog, and no UI or duration is given for task_game boot states 0..1 on a first boot.
- The title draw calls 0xDCB10(1) before the (C)TYPE-MOON line (VA 0xB2952), unexplained; the
  particles (0xB29D0) and the 0xB2AF0 shimmer are not modelled.
- 3.1 state 4: the damaged flag is read before the frame update, so one more update runs after it
  clears (no visible effect).
- vm_variables 11's errata for 5.1 are not applied: `_SELA` tests F1118 (0xA7FA0) and calls
  0xB8400 only when it is 0, then clears it, and mode 3 stores nothing; `_SELS` stores the button
  index (0xA7B99), not values[cursor]; both helpers and `_SELD` do nothing in scan mode.

**sound_runtime.md** (one review, not refuted; 8 minor findings, none applied):

- 3.6 `_VPLY`: the engine copies argument 1 (the hex index) to 0x16543C0 and tests '_' on it, not
  on the name (no corpus statement is affected).
- 11.2: History_Push returns early in scan state, when word 0x164B912 != 0, when byte 0x16541F4 !=
  0 and on a 0x164BA6C / type condition (0x7AF9B..0x7AFD7); in scan state the `` `070 `` array
  keeps its values (a scan-state replay of scripts 166 / 370 / 462 differs in 302 of 1,175 steps,
  all `scr_se_char`).
- 7: the model's voice gate ignores u32 0x16541E8 in skip mode (the doc's pseudocode has it; 6 of
  400 random cases differ).
- 3.6 `_VPL2` (16 statements) has its own gate: any skip mode blocks it, it does not set
  last_voice 0x86AA28, and it zeroes 0x1654208 after copying it to 0x164FCF6 (0x9A05D..0x9A123).
- Out-of-corpus inputs differ: an empty-name `_SEPL` with `` `068 `` queues a stop (kind 2); loop
  argument 2 with `` `068 `` stores 1; an unresolved SE name uses an uninitialised id; channel -1
  writes the record that overlaps BGM slot 1. None occur in retail.
- BGM_Start 0x98C0 and BGM_Stop also call 0xD4730(1) and 0xD4750(1, 1 or 0).
- The HE-VAG pair player 0x8560 has a third caller, 0xCE36C, in the unreferenced function
  0xCE320 (vag 5 / 6).
- Precision: the group table stride is 16 bytes; history +0x3641 is written as a byte and only
  when 0x174D9A8 == 0; the G[73] "character heard" OR is 16-bit (characters 16..23 set no bit);
  645 vse overshoot with AudioBank trimming, 653 in a raw ffmpeg decode.

**adv_state.md** (re-check not refuted; minor):

- 2.1 / 2.3: kind 1 and kind 9 records take both pointers from the global script_ptr 0x16543B0,
  which Script_Step never writes during its run of up to 1000 statements, so their cmd = resume
  = where the current Script_Step call started, not the changing statement. Only the
  Script_Step-internal pushes (kinds 0, 6 / 7, 12, 13) and the audio helpers get the statement
  pointer.
- 7.7: `check_save_image` accepts a cmd_offset whose `_` lies inside an argument; its readmax
  check is global rather than tied to the scripts the slots reached.
- 5.1 rewind: action 20 has the analog speed path of fast-forward (mask exactly L2 and held:
  speed = max(1, (t * 0x44444480) >> 32) for t < 0xF4, else 64); an invalid target or target ==
  cursor calls 0x56730(0); the undo loop compares signed i16, so after a ring wrap no in-between
  record is undone.
- 7.2: AutoSave 0x73B70 also requires 0xEA970(0) != 0 and 0xEABE0(0) != 0 (the quick-save
  gates).
- No retail save has been checked (both real-save tests skip).

**gallery.md** (one review, not refuted; 8 minor findings, none applied):

- 2.1 load merge: G is copied from the system block only when SaveImage_LoadSystem 0x6F470 is
  called with edi == 0 (call sites 0x7024D, 0xA9D91); the other 12 call sites pass 1 and leave RAM
  G unchanged (neither replaced nor merged).
- The choice block OR covers 0x118 bytes: save +0x1804..+0x180B go into RAM 0x164FCD0..0x164FCD7
  (choice block +0x110..+0x117), not into config copies; `merge_loaded` misses those 8 bytes.
- 7 movie gallery: locked movies are hidden and the list is compacted (op2_ciel moves to
  position 1 when bit 0 is clear; 0x2AAFB..0x2ABD6, 0x2B192..0x2B1DD, 0x2B632..0x2B662).
- 2.2: `_STCG` clamps ids >= 0xA8D to -1 before 0x95CA0 (0x99A99..0x99AA7), so it never sets a
  bit through the seen-index map; only the display paths (0x7DD62, 0x7E0F2, 0x8632E) can.
- 6.1: music record +0x1C / +0x20 match the RIFF smpl loop points only after subtracting the
  256-sample encoder delay (raw values match 0 / 95).
- 4.2: thumbnails of non-16:9 CG rectangles (IMG3632, IMG4038, IMG4144) are hand crops, not the
  scaled w x h area.
- 8: the ending-list functions cited (0x1FB40 / 0x1FB80) have no callers; the live tests are
  inlined copies at 0x1ECB1.., 0x1EF7D.., 0x1F973.. and 0x1FAB9.. with the same logic.
- 3.1: Title_Update has a second `ebx = 1` at 0xB0BFE (conclusion unchanged); without the game
  the test file gives 3 passed, 11 skipped (not 10).

---

## 7. Prioritised open problems

Priorities follow RUNTIME_REQUIREMENTS: **P0** = needed for the first vertical slice (boot ->
prologue movie -> first scene with background, text and voice); **P1** = complete faithful
playthrough; **P2** = menus / extras completeness; **P3** = optional.

Answered since the first snapshot (2026-10-25 12:00) by the follow-up documents: the boot and
title flow (old P0 1: system_flow), layer renderer semantics (old P0 2: layers, except the items
below), transitions and blending (old P0 3: compositing), audio playback rules (old P0 5:
sound_runtime; audio.md corrected), the variables document (old P0 6: vm_variables; engine.md
5.5 fixed), save / history / resume mechanics (old P1 8: adv_state, except a retail save), the
choice screen (old P1 9: system_flow), backlog / rewind / auto / skip / fast-forward (old P1 10:
adv_state), sprite anchors, `scale_class` and the framing offsets (old P1 13: layers 7, 10),
gallery records and unlock bits (old P2 14: gallery), se_nam sections, vag triggers and the voice
group -> config slot mapping (old P2 15: sound_runtime 8, 10).

### P0

1. **Text vertical scale** (18): the prologue movie spaces lines 81 px apart (3.115 px / unit)
   against 78 px from the engine constants; trace the canvas in effect for display list 0x1E
   (VA 0xDCB20, texture mode byte at 0xE1F4C) before calibrating the renderer. The 640 x 344.7
   choice canvas raises the same question (system_flow 5.6).
2. **Wait model** (13, 14, 33): the action masks, auto-mode countdown, skip / fast-forward and
   stop-voice-on-advance are now decoded (adv_state 6, 10; sound_runtime 7). Still open: how
   `Adv_Update` (0xA0B70) waits per message end mode (click icon, `@k`, `@e`), `_WTTM`'s second
   argument ("skippable" [H]), `_WNTY`, and skip input during script movies. Slice stand-in:
   decide / click advances, `_WTTM` waits its time.
3. **First end-to-end composition check** (12, 28, 34): no research blocker remains for the
   slice's page 1 (layers 4 / 8 give the field map and motions 22523..22526 with their easing;
   compositing gives `_STBR` modes 11 / 13 / 17 / 19 / 24 and `_FADS` 0 / 1 / 2). Compare a port
   frame with the Python reference (`tsuki.misc_layer_state_and_motions` + `tsuki.compose`) and
   the 25 s frame of `prologue_en.mp4`. This also tests two [H] points of compositing 14: that the
   render target still holds the previous frame when a transition captures it, and that D is the
   target before the quad.

### P1

4. **`_SELD` per-line loop** (15, 33; 6.9): model the F799 write and the kind-0xC history snapshot
   per pending `_SELR` line and the line-record copy; backlog grouping, rewind and auto mode at
   choice pages depend on F799.
5. **Layer projection with motion-animated anchors** (34; 6.9): adopt the corrected rule
   (transform about the integer anchor, add the residual off + anchor untransformed) and settle
   the STZ4 pivot-flag case (1.35 units off); 488 retail `_STMA` statements are affected.
6. **Quake / shake / swing** (34): the per-frame formula (VA 0x58206..0x58827, `rand()`, `sinf`;
   read by the draw through 0xFE50 / 0xF390) and the MSQK / SHOK screen quakes; 2,431 retail
   statements; 00_00 uses `SHAF SHS2 SQK2 SQS2 SSS2` before its first voiced line (the retail path
   of RUNTIME_REQUIREMENTS 7.6). Storage and parameters are decoded (layers 4.4).
7. **Crop / mask pass** (28, 34): the crop window shape (a16 / a17 edge parameters), how a layer
   is combined with its mask layer (STCH arg 11: 6,351 statements; arg 12..17 crop: 2,438), and
   which layers take the two-pass masked path (compositing 14.3, layers 13.2).
8. **Unknown effects of used commands** (14): `_STCF` (5,179; sets bit l of 0x1653E30 and byte
   0x1653E40 + l and yields, reader not traced), `_STRA` (569; periodic value, no draw reader
   found), `_SPFC` / `_SPRD` / `_SPRC` (608; special-picture globals, meaning [H]), `_FADE` effect 4.
9. **Save import and resume check** (29, 33): obtain a real `savedata000` (read-only copy) and run
   `python -m tsuki.misc_adv_system_state save <copy>` (both real-save tests then run); fold in
   the 6.9 notes (kind 1 / 9 record pointers, quick save without the F1118 bit, the G load
   condition, the 0x118-byte merge).
10. **Prompts `_SELS` / `_SELA`** (31): layout of modes 2 / 3 (widgets 0xD5 / 0xD6, DISP_PARTS
    child 19) and what `_SELA`'s second button opens (phases 4..7, menu 0x16: the save screen
    [H]).
11. **Per-screen UI layout** (24): config, title, boot notices and the choice screen are decoded;
    the system menu, save / load, backlog, HUD buttons and the LOAD / CONFIG / GALLERY screens are
    still code constants (decode calls to 0xEB780 / 0xCFC90 / 0xD1410 / 0xDCBE0 / 0xD9DD0 per entry
    function, including tail calls).
12. **Text styles and colours** (12): appearance of `@g`, `@z`, `@b`, `@a`; RGB565 vs BGR565
    colour words; outline colour (0xDB7BB0 runtime table); previous-message dimming.
13. **Audio review notes** (22; 6.9): the `_VPL2` gate, History_Push's early returns in scan
    state, the skip-mode voice condition 0x16541E8; and the menu label of each of the 19 voice
    config slots (config page 1 rows 7..25 hold one row per character; the slot -> row mapping is
    not established).
14. **Motion loop points without emulation** (16, 34): the loop point of each motion is a compiled
    switch (VA 0xC1C70), not a table; the research tools extracted it by emulating the function
    (`samples/misc_layer_state_and_motions/motion_loops.json`, 2,027 looping motions). A runtime
    that reads the user's eboot needs a static decoder of that switch. None of the slice's motions
    loop.

### P2

15. **Gallery details** (32; 6.9): apply the review notes (G load condition, compacted movie list,
    `_STCG` clamp, loop-point delay); the flowchart line / state drawing (0x25950 / 0x25F60 /
    0x260D0), node +0x14 and the connector code, the `_JUMP` special switch 0xB9460 (pseudo scenes
    498..530), CG x / y and the +0x1C photo-viewer flag.
16. **Engine-only paths** (28, 31): transition types 18, 20..24, 68..70 (engine screens), the
    second display-list executor 0xDB6D0, the title particles and shimmer (0xB2C60 / 0xB2F50 /
    0xB3310, 0xB2AF0, 0xDCB10(1)), the 853.333 x 480 scene canvas (layers 6).
17. **Catalog fields** (9): `kind` 0x0B, `draw_flags`, `char_id` use, IMG7000 / IMG7001 oversized
    catalog rects. Remaining codec-level audio rules: "end 128 samples early" and the -1200-sample
    start offset (audio 2.3, 10).

### P3

18. Korean stubs (ft_ko, ko text / UI columns), trophies, PlayGo, screenshot / streaming policy.
19. Build independence: every eboot address here is for app 01.01; a pattern-based locator would
    be needed to support other builds or patches (fonts.md 10.7).
20. LV lip-sync data (placeholder only in this release).
21. Features without effect on PS4: the `_STCH` a19 / `_STRR` / motion bit-34 angle (no draw
    reader), motion flag bits 3 / 23 / 27 (never tested), the "system voice" option (no player;
    se_nam section 0 never searched), the unused `_MSTP` / `_SEPN` / `_SEP3` / `_SEAC`, config +0x40.
