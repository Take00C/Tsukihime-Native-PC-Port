# Runtime requirements (draft)

What the native PC runtime has to implement to replace `eboot.bin`, derived from the engine
analysis in `docs/formats/engine.md` (section numbers below refer to it) and the format
documents. Each line lists the user data it consumes, where the PS4 behaviour is documented,
how well it is understood today, and a concrete acceptance check.

Priorities: **P0** = needed to boot and read the opening scene; **P1** = needed for a complete,
faithful playthrough; **P2** = menus/extras completeness; **P3** = optional platform extras.

Knowledge: **known** (code + data verified), **partial** (structure known, details open),
**open** (not yet analysed).

All game data is read at runtime from the user's own copy (the `CUSA47999` folder, including
`eboot.bin`, which holds tables that exist nowhere else). Nothing is redistributed.

---

## 1. Checklist

| # | subsystem | P | consumes | reference | knowledge | acceptance check |
|---:|---|:-:|---|---|---|---|
| 1 | Archive layer (HED8/HED4, NAM32/NAM8, MRG, mrgd00/MZP, MZX0, LV) | P0 | `image/*.hed/.nam/.mrg`, standalone `allscr/script_text/ft_*/se_nam.mrg` | containers.md | known | every entry of the 12 archives and 891 sections decodes (container tests) |
| 2 | Archive slots and name lookup | P0 | NAM files, embedded name tables in eboot (allpac2 names 0x7F6800, sprite names 0x7A8B10) | engine 5.5, 6 | known (lookup), partial (which archive per index) | `img2511`, `WHITE`, `SIE_A_59_00_N00` resolve like `NameTable_Lookup` (upper-case, cut at '.') |
| 3 | eboot table extractor | P0 | `eboot.bin`: UI string table `ui_text[5][1499]` (RELRO 0x41D5F0, strings in data), image table 0x671900 (images.md 8), BGM names, mask names, effect tables | engine 9, 5.5, 4.3 | known (layouts) | the C# extractor reproduces `samples/engine/ui_text_<lang>.tsv` (all 5 x 1,499 entries, nothing filtered) byte for byte and the image-table checks of `test_image_table_fields` |
| 4 | Script VM front end | P0 | `allscr.mrg` sections 3.. (script n = section n+3), inverted MZX | engine 5.1-5.2 | known | `_WKST(F823,eq,0);` dispatches to WKST; segments split at `;` (0x300-byte window; 1,083,168 segments in the 549 scripts), name = 4 bytes after the first `_` (so `_STTR,0,26);` runs STTR); unknown names and B/D/H/K/L/N/O/Q/U/Y skip; at most 1000 segments per ScriptStep call (one call per frame, except the fast-forward loop of engine 3.3) |
| 5 | Script commands (112 handled) | P0/P1 | scripts, script_text.mrg | engine 5.3 + script-command catalogue (other agent); variable commands: vm_variables.md 4 | partial (names, handlers, yield class); variable commands known | all 94 names used by the scripts are handled; `_SNX4 _FANX _IFP4 _DMMY _STQK _NOP_` are no-ops; `_IF__` jumps to its label when the condition is false; `_SELR`/`_SELD`/`_GOTS` choice flow as in vm_variables.md 4.4-4.6 |
| 6 | Variables and conditions | P0 | - | vm_variables.md (`tsuki/vm_vars.py`), engine 5.5 | known (emulation-tested) | F[1200], Z[1400], G[2000] u16; operand by first byte: targets `C F G _` (`_WKST`/`_WKAD`), `F G _` (`_WKOR`/`_WKOF`), `F G` (`_WKBO`); values `F G _`, everything else literal (`C3`, `Z3` = 3); **`C<n>` target = F[F823 + n]** (85,471 `_WKST` + 3,075 `_WKAD`); `_?ddd` = F[n] (G[n-600] for 600..700), `_D000` = choice result F[0] (`_SELD` value, `_SELS` button 0 / 1); `_SELA` save prompt only when F1118 = 0, then F1118 = 0; G[0..499] persisted in system data; history / save slots keep 356 listed F values; PS4 accesses are unchecked (port: reject out-of-range); `tsuki.vm_vars` reproduces 61,000 fuzzed cases of the engine exactly (scan mode, skip counter and snapshot gate included) |
| 7 | Text display | P0 | `script_text.mrg` (per language), `ft_<lang>.mrg` | engine 4.5, 5.6; text/font docs | partial | the first page's line (script_text id 3, `text.md` 2.3) renders over the dark room background |
| 8 | Read-text tracking | P1 | - (saved in system data) | engine 5.6 | known (algorithm) | `readmax[block] = max(line)`; skip-read-only stops at the first unread `_Z` label |
| 9 | Auto / skip / fast-forward | P1 | config | engine 8.3-8.4 | partial | auto speed and skip modes (all / read only, stop at choices) behave as configured |
| 10 | History ring, rewind, previous/next scene | P1 | - | engine 5.6 | partial (800 x 0x3A10 records restored directly by History_Restore) | hold-to-rewind returns to earlier lines and restores the screen from the record without replaying sounds |
| 11 | Backlog | P1 | script_text, voice | engine 8.3 | open (storage) | log opens, scrolls, replays voices |
| 12 | Renderer: canvas and layers | P0 | images from allpac*/parts | engine 4.1 | partial | 1920x1080 virtual canvas scaled to the window; 40 layers with position/scale/alpha |
| 13 | Blend modes | P1 | `_STBR` | engine 4.3 | partial (table known, formulas [H]) | modes 0..25 implemented with `dst_texture` backdrop; visual match on scenes using modes 17/11/23 |
| 14 | Transitions / rule masks / sepia / screen effects | P1 | `RULE_xx` MZPs (allpac3), masks DEFAULT/MASK0xx | engine 4.3-4.4 | partial | `_FADS` fades and rule wipes match PS4 captures; sepia = Cb/Cr tint (`SepiaConstants`) |
| 15 | Image decoding (MZP image format, palettes, tiles) | P0 | allpac0/1/1_2/2/3/4, parts | image doc (other agent) | per image doc | backgrounds, sprites, UI parts decode |
| 16 | Audio decode: ATRAC9 | P0 | `bgm`, `vse`, `voice`(LV+MZX) RIFF WAVE | engine 8.6, audio doc | known (ffmpeg decodes) | BGM loops, voices play at 48 kHz |
| 17 | Audio decode: HEVAG | P1 | `vag.mrg` (VAGp, 19 entries) | engine 8.6, audio.md (`tsuki.audio_hevag`) | per audio doc | system SEs play |
| 18 | Mixer and volumes | P0/P1 | config | engine 8.4, 8.6 | partial | channels BGM, voice, SE, system SE, movie; BGM ducking during voice; per-character voice volume/mute; 2ch / 5.1 BGM option |
| 19 | Movies | P1 | `image/movie/*.mp4` (134: H.264 1080p 30/29.97 fps, 43 with AAC 48 kHz stereo) | engine 8.7 | known (codecs) | prologue plays on New Game in the selected language variant; looping title backgrounds |
| 20 | Input | P0 | - | engine 8.5 | partial (pad bits known, action map open) | decide/cancel follow the enter-button setting; auto-repeat; stick = d-pad; mouse/touch pointer clamped to 0..639 x 0..447 (640x448 space) |
| 21 | Timing | P0 | - | engine 3.3 | known | fixed 60 Hz logic step, independent of monitor refresh; no frame-rate-dependent speed |
| 22 | Save / load (slots, quick save, system data) | P1 | own save file; optional PS4 `savedata000` import | engine 7 | partial (layout known, state record partly) | 139 menu slots + quick save; date stamps; "Continue" picks the newest slot |
| 23 | Config screen and persistence | P1 | UI parts (`conf_parts_<lang>.mrg`), UI text | engine 8.4 | partial (options known, field mapping open) | all options from 8.4 present; defaults = recommended settings |
| 24 | Title / boot flow | P0 | caution images, logo, title parts, title movies | engine 8.1 | partial | caution -> logo -> title -> New Game -> prologue -> first script |
| 25 | Menus: save/load, system menu, flowchart | P1 | UI parts (allpac3 *.MRG sets), UI text, flowchart tables in eboot | engine 8.2-8.3 | partial | every menu entry from 8.3 reachable |
| 26 | Gallery: CG, music, voices, extras | P2 | gallery parts, CG lists, BGM names/titles, unlock flags | engine 8.2, 9 | partial | CG viewer, music room with titles/credits, voice room, Ciel-sensei and hint corners |
| 27 | Localisation | P1 | per-language files (`*_en/_zc/_zt`), fonts, UI text blocks, movie variants | engine 4.5, 9 | known (selection rule) | language 0 ja / 1 en / 2 zc / 3 zt, ">= 4 follow OS locale" |
| 28 | Platform extras | P3 | trophy data (sce_sys) | engine 2.2, 8.8 | known (calls) | optional achievements; screenshot watermark not required |

---

## 2. Per-subsystem notes

### 2.1 Script VM (items 4-6, 8, 10)

- The eboot has two interpreters on the same parser: `ScriptStep` (112 entries, runs until a
  yielding command or 1000 segments) and `Script_Prefetch` (43 entries, engine 5.4): a
  look-ahead scanner that follows control flow on a saved copy of F/Z/G (restored afterwards)
  and lists the next archive entries (<= 100, byte budget 12,000,000) to preload. A port needs
  only the first; the second can become an optional asset prefetcher with no effect on state.
- Command handler entry points and their first callees are listed in engine 5.3; use them to
  cross-check the command semantics documented by the script work.
- The VM is text based: arguments are re-parsed every time a command executes; keep that
  behaviour (scripts contain backquoted prefixed arguments such as `` `017:100``).
- State to snapshot per text line (history ring, 800 entries): script number + offset, variables,
  layer/scene state, sound channel state, window state (record layout partly known, engine 5.6).

### 2.2 Renderer (items 12-14)

- Work in a 1920x1080 virtual canvas; the PS4 composes into two 1920x1080 display surfaces with 16
  1920x1080 capture surfaces available for transitions and snapshots.
- Shader set to reimplement (engine 4.2): basic 2D/2DTex/VtxCol/VtxColTex, YCbCr (NV12), clear,
  capture, transition 2D (rule mask `transition_tex`) / 2DTex (layer texture only), both with
  `transition_params`, sepia (+ transition) with
  `sepia_Cb/sepia_Cr/sepia_blend`, and 24 blend shaders that read `texture` and `dst_texture`
  (backdrop) with `common_ps_params`. Uniform names come from the embedded reflection data.
- Blend-mode numbering 0..25 and the 51 screen-effect presets are fixed tables in eboot (engine 4.3).

### 2.3 Audio (items 16-18)

- PS4 output: one stereo float port (1024-sample period) plus an 8-channel float port for the
  5.1 BGM option, a 256-sample stereo port for HEVAG system sounds and a 256-sample stereo port for
  movie audio, all at 48 kHz. A PC mixer at 48 kHz float with a 5.1 path (or a downmix) covers this.
- ATRAC9 decode via FFmpeg is verified on the voice data; HE-VAG needs its own decoder (audio.md).

### 2.4 Input (item 20)

- Internal button word (engine 8.5): touch 0x1, L3 0x2, R3 0x4, OPTIONS 0x8, d-pad 0x10..0x80,
  L2 0x100, R2 0x200, L1 0x400, R1 0x800, TRIANGLE 0x1000, decide 0x2000, cancel 0x4000,
  SQUARE 0x8000, left stick 0x10000..0x80000, right stick 0x100000..0x800000; edges and auto-repeat
  are computed by the pad manager.
- Keyboard/mouse defaults for the port are a design decision; keep the decide/cancel abstraction.
- The game's 27 action bindings (three preset tables) are still unmapped (engine 10, item 1).

### 2.5 Save data (item 22)

- PS4 image: 4 MiB = system block (config 0x140, G[500], readmax[4020], latest slot, ...) +
  140 slot records of 0x7108 bytes (two state records + date) at stride 0x7140 (engine 7).
- The port should use its own versioned format but keep the same logical content so a PS4
  importer (`eboot.savedata`) remains possible once the state record is fully understood.

### 2.6 Data that only exists in eboot.bin

UI/config/dialog/music/chapter/flowchart text (the `ui_text[5][1499]` pointer table: ja, en,
zc, zt and a ko placeholder column; engine 9), the allpac2 name list and the sprite name list,
the image table (logical size, size in the 640x362 canvas, anchor, body/face names per allpac2
index; images.md 8), BGM name list, rule-mask name list, screen-effect/blend tables,
CG/gallery/extra lists, the movie path list, and the shader reflection (uniform names). The port
needs an extractor that reads these from the user's `eboot.bin` at first start (VAs in
engine.md and `tools/eboot/symbols.py`; file offset = VA - 0x600000 + 0x365FA0 for the data
segment, VA - 0x400000 + 0x32C210 for RELRO, VA + 0x2280 for text/rodata). Pointers stored in
these tables are prelinked data/text VAs (the file already holds the values; they also appear
as R_X86_64_RELATIVE relocations).

---

## 3. Open items blocking P0/P1

1. Image format details (other agent) - P0.
2. Per-command semantics for the 30 most used commands (`_WKST`, `_STCC`, `_WNTY`, `_STCH`,
   `_STCP`, `_WKAD`, `_FADS`, `_STZ4`, `_STMA`, `_STRT`, `_PGST`, `_VPLY`, `_STBR`, `_WTTM`,
   `_SEPL`, `_STEC`, `_CLO3`, `_STGS`, `_STCF`, `_SCH2`, `_JUMP`, `_SNX2`, `_SEVL`, `_SQK2`, `_IF__`,
   `_MPLY`, `_CLO4`, `_SBAS`, `_SHOK`, `_SNX3`) and the `_Z` text commands - P0.
3. Action-mask mapping and config field mapping - P1.
4. Blend/transition shader formulas (GCN disassembly of 24 + 4 pixel shaders) - P1.
5. Backlog storage and the history record layout - P1.
