# knife's executable tables and where the PS4 eboot keeps them

Package K1b. knife (`ref/knife`, commit b12433c) hard-codes tables that it copied out of the Switch executable
(`motion_loops.rs`, `flow_table.rs`, `gallery_table.rs`, `save_tables.rs` and constant arrays in `ui.rs`, `game.rs`,
`game/*.rs`, `stage.rs`, `grade.rs`, `fx.rs`, `gallery.rs`, `text.rs`, plus the binary bundle `knife/data/exe_tables_overseas.bin`
that `exe.rs` serves). The port must not copy them (knife_port.md 1.4): it reads the same data from the user's PS4 eboot.
This document lists every table knife uses for Tsukihime (overseas build), says where the PS4 keeps it and how well the two
agree, and names the reader.

Scope: Tsukihime, `Build::Overseas`. Skipped: everything Witch-only (`WITCH_*`, `witch*.rs`, `ui/witch/`, `teatime.rs`, the
`sel_win` / `WIK_*` tables of the Witch choice screens), the touch UI, and single layout constants (one pixel position, one
speed, `CONF_VIEW`, `GAL_CLIP`, `MUSIC_CTL`, title and system-menu slot positions and the like).

## 1. Result

| status | meaning | tables |
|---|---|---|
| (a) | already decoded here | 24 |
| (b) | found in the PS4 eboot (data table, or code run by the tiny interpreter) | 34 |
| (c) | not found, or the PS4 stores something else | 15 |

(73 rows in section 3. Rows with several knife constants of one family count once. Counts and cross-checks come from
`python -m tsuki.engine_tables crosscheck`, section 6.)

How the tables were located: knife's values were encoded in the record layout and searched in all three loaded segments of
the eboot (text, relro, data; both executables are little-endian), pointer fields masked where a record had them; the
Switch binary tables were taken from knife's own bundle. The hits were then compared field by field with the whole knife
table (the "match" column). Two tables are not data at all, they are compiled switches; those are read by running the
function in `TinyX86` (`tools/tsuki/engine_tables.py`, `src/Tsuki.Data/Eboot/Tables/TinyX86.cs`), an interpreter for the
dozen instruction forms the two functions use that throws on anything else.

## 2. Conventions

- VAs are eboot CUSA47999 v01.01 VAs (agent_brief.md: text VA = file offset - 0x2280, relro 0x400000 -> file 0x32C210,
  data 0x600000 -> file 0x365FA0). "text" tables are read-only data inside the text segment.
- **SYSMES numbers.** knife's message numbers are the Japanese SYSMES numbers. The overseas table inserts one message at 217,
  so the PS4 `ui_text` id is `n + 1` for `n >= 217` and `n` below (`ps4_sysmes_id`, `DialogMessageTable.Ps4SysmesId`).
  Checked on 1,286 flowchart nodes x 2 fields, 95 music titles and 4 x 95 credits, 25 ending prompts and 24 dialogs: 0 exceptions.
- **Languages** (code 0x3ED90 returns 0 ja, 1 en, 2 zc, 3 zt, 4 ko). The PS4 code picks: savetitle and flow-title cells: English table
  for 1, the other table for the rest (0x70340, 0x21430); phase-title cells: ja table for 0, en for 1, the third ("zh") table for
  2, 3 and 4 (0x6CB80); button-guide word cells: block `language` of 27 cells, 0xD8 bytes apart (0x6A765).
- **Readers.** Python: `tools/tsuki/engine_tables.py` (tests `tools/tests/test_engine_tables.py`). C#:
  `src/Tsuki.Data/Eboot/Tables/` (tests `tests/Tsuki.Tests.Data/EbootTables/`). The C# readers equal the Python ones table by
  table: both build canonical bytes per table and the SHA-256 of 50 of them is in
  `tests/Tsuki.Tests.Data/EbootTables/engine_tables_digests.json` (a digest proves equality, it is not game data).
  Rows marked "(a)" cite the older decoder; where C# had no reader K1b added one (marked "+C#"). `EngineTables` (C#) holds every reader of this
  package as a lazily built, shared set over one `EbootReader`.

## 3. The tables

Status letters a / b / c. "match" = how many of knife's records equal the PS4 ones. "Reader" = Python function / C# class.

### 3.1 Binary tables of `exe.rs` (the Switch bundle)

| id | knife table | holds, layout, count | st | PS4 evidence | reader |
|---|---|---|---|---|---|
| 1.1 | `image_names` | `[u8;32]` x 14,744 image names | a | names list data 0x7F6800 (32-byte records ending CR LF); match 14,744 / 14,744 | `catalog_eboot`; `ImageCatalog` |
| 1.2 | `image_info` | 72-byte records x 14,744: size f32 x2, anchor f32 x2 (+44..+56), flag bytes +65 / +67 / +68, level u16 +70 | a | image table data 0x671900, 0x48-byte records but another layout (images.md 8). Size: knife f32 = PS4 logical size / 3 and x 362/1080, 14,713 / 14,744 (the 31 others: IMG1840, IMG1841, IMG1481_20_00, IMG6000..IMG6026, IMG5030); knife +67 / +68 / +70 = PS4 +0x3F / +0x40 / +0x42, 14,744 / 14,744; knife +65 bits 0..1 = PS4 +0x3D bits 0..1, 14,609 / 14,744 | `catalog_eboot`; `ImageCatalog` |
| 1.3 | `scene_ids` | `u16` x 549 scene ids of the allscr scripts | a | text 0x216130, match 549 / 549 | `script_tables.read_scene_ids`; `SceneTable` |
| 1.4 | `haze` | STRA parameters, 10 f32 per id, ids 1..557, entry 0 filler (40 B) | b | text 0x219910, stride 40, 558 entries; accessor 0xB3630 (`lea ecx,[rdi-0x22E]`, ids 1..0x22D). Found by the bytes of entries 1, 2, 100; match 542 / 558 (differing ids 55, 260, 266, 292, 293, 296, 321, 322, 481, 483, 507, 508, 513, 527, 529, 557) | `read_haze`; `HazeTable` |
| 1.5 | `motion_keys` | 64-byte STMA keyframes x 71,905 | a | data 0x873FF0, 72-byte records (i64 time, i16 fields, i64 field 8: layers.md 8.1); 71,859 / 71,905 identical (46 keys of 33 motions differ: ids 918, 1722, 2228, 2308..2311, 2880, 2881, 3418, 5497, 5498, 7415, 7697, 8334, 8594, 9860, 9861, 10004, 10459, 10595, 10654, 10655, 11449, 12365, 15823, 15828, 15829, 15906, 15999, 23475, 23476, 23794) | `script_tables.read_motions`; `MotionTables` |
| 1.6 | `motion_ends` | `u32` x 24,192 key end index per motion | a | text 0x2A3938, i64; 24,192 / 24,192 | same |
| 1.7 | `motion_lens` | `u32` x 24,192 motion length ms | a | text 0x274538, i64; 24,192 / 24,192 | same |
| 1.8 | `motion_masks` | `u64` x 24,193 field-enable masks (index = motion id) | a | text 0x245130; 24,193 / 24,193. +C#: `MotionTables` skips the masks | `MotionTable.flags`; `read_motion_field_masks`; `MotionFieldMasks` |
| 1.9 | `line_break_classes` | nn::font UAX #14: 1,986 ranges (u16 first, u8 len-1, u8 class) | c | not in the eboot (searched the 4-byte record sequences, 16 and 64 bytes, in all segments). The PS4 breaks lines with its own kinsoku and English wrap code (textbox.md 5, `TextRules.cs`) | none |
| 1.10 | `line_break_pairs` | 30 x 30 `i32` class pair table | c | not found as i32, i16, u8, nor transposed. Same alternative as 1.9 | none |

### 3.2 Stage tables

| id | knife table | holds, layout, count | st | PS4 evidence | reader |
|---|---|---|---|---|---|
| 2.1 | `MOTION_LOOPS` (`motion_loops.rs`) | (motion id, loop point) x 2,027: 1 = loop whole motion, n > 1 = from n ms | b | **code, not data.** `int loop_point(int id)` at text 0xC1C70 (called by 0xBA91C and 0xBE600): a binary search over id ranges with 15 jump tables (`lea ecx,[rdi-N]; cmp ecx,K; ja; movsxd; add; jmp rcx`) whose targets are `mov eax,imm; ret`. `TinyX86` runs it for ids 1..24,192 in 1.7 s (Python) / 0.09 s (C#); match 2,027 / 2,027 with knife and with the older unicorn extraction in `samples/misc_layer_state_and_motions/motion_loops.json` | `read_motion_loops`; `MotionLoopTable` |
| 2.2 | `TABLE` (`grade.rs`) | STEC grades: 83 modes x 20 f32 (gain, levels low / high per channel, flag, two tint layers); shader `pre_alter` | c | **differs.** The PS4 runs the grade on the CPU (0xD36F0) with evaluated tables: flags i32[81] at 0x2D5A78, three 256-byte LUT pointers per mode (0x42C590 / 0x42C308 / 0x42C080), luma weights (compositing.md 9; `read_stec_tables`, already decoded). The mode numbers are the same: of the 69 per-channel modes (PS4 flag 0x100), the 23 without tint layers equal the plain levels / gamma curve `low + (high - low) * (x/255)^(1/gain)` of knife's parameters exactly for x >= 1 (23 / 23; the PS4 forces x = 0 to 0), and the 46 with one or two tint layers agree with a one-off evaluation of knife's tint blend modes (2, 3, 5, 8, 14, 17, 18) to within 4 levels (<= 1: 3 modes, <= 2: 27, <= 4: 46; that evaluation re-implements knife's formulas and is deliberately not kept in the repo). The 11 luma modes (0x200: ids 2, 3, 5, 6, 7, 8, 77..81) are exactly knife's flag 1 modes, mode 1 (0x400) is knife's flag 2 fill. Use the PS4 LUTs | `misc_compositing_transitions_shaders.read_stec_tables` |
| 2.3 | `RADII` (`stage.rs`) | soft-clip texture radii, 6 values (10, 32, 40, 64, 128, 256) | b | text 0x200590 as f32 (knife's is i32); match 6 / 6 | `read_soft_clip_radii`; `SoftClipRadii` |
| 2.4 | `LINES` (`game/focus.rs`) | FCLN focus-line keys: 30 effects x 4 keys x 9 i32 | b | text 0x216BC0, stride 36; match 118 / 120 | `read_fcln_lines`; `FclnLineTable` |
| 2.5 | `NOTES`, `NOTE_V`, `NOTE_JITTER`, `NOTE_FLOOR` | falling-note kinds of FCLN 1..3: frames, size, step, accel and constants | b | data 0x8734A0, 3 records of 0x58 bytes (u16 image, u16 frames, u16[24] frame order, -1, i16 w / h / step, f32 vx vy jitter jitter drift accel, i32 floor); frames, size, step, accel 3 / 3 | `read_fcln_notes`; `FclnNoteTable` |
| 2.6 | `STRIPS`, `SHARDS`, `DOTS`, logo spawn areas (`fx.rs`) | menu background strips (8), logo particle rects (3 + 3), spawn areas per language | c | not found as f32 / i16 / i32 rows or as single values in sequence | none |

STRA haze is row 1.4. Quake and shake: knife has no table for them (`game/shake.rs` and the per-layer quakes are formulas), so there is
nothing to read; the formulas are `tools/tsuki/quake.py` / quake.md.

### 3.3 Game and flow tables

| id | knife table | holds, layout, count | st | PS4 evidence | reader |
|---|---|---|---|---|---|
| 3.1 | `SE_NAMES` (`save_tables.rs`) | 1,291 sound effect names; a saved looping SE is an index | c | no SE names in the eboot (searched "FATE_SE001", "MOON_SE05003" in any case). The PS4 resolves names through `se_nam.mrg` section 2: 1,327 names = knife's 1,291 (all present, case-insensitive) + 36 (MIX_SE006..MIX_SE038 and two DUMMY); the vse index is the entry number (audio.md 4). A port saves the vse index in its own save file | `tsuki.audio.load_se_names` |
| 3.2 | `SCENE_NODE` | flowchart node per scene number (`i16` x 527, -1 none) | b | text 0x228EF0 as **i32** x 527; match 527 / 527 (`SCENE_NODE[n]` is the node of the script with allscr index n + 1). Also gives `SCENE_ID_NODE` (4.3) | `read_scene_node`; `SceneNodeTable` |
| 3.3 | `SEEN_ALIAS` | picture name -> seen bit (>= 2701), 2,144 | a | data 0x86AA30, 16-byte records {char*, i32, i32 seen index} (gallery.md 2.2); 2,144 / 2,144. +C# | `misc_gallery_extras_flowchart`; `GalleryTables.SeenMap` |
| 3.4 | `F_WORDS`, `JUMP_KEEP_WORDS` | F words saved whole in a history record / kept through a flowchart jump (38 / 39) | b | two -1-terminated `i16` lists, 39 entries: text 0x2160E0 (history copy, used by 0x76610, 0x7AF90, 0x7C6D0, 0xA3050) and 0x1A4250 (flow-jump copy, used by 0x2A080, 0x2A1D0). Contain all of knife's F_WORDS in the same order plus F967; knife's `JUMP_KEEP_WORDS` extra F026 is not in the PS4 list. 38 / 39 | `read_flow_keep_lists`; `FlowKeepLists` |
| 3.5 | `F_BYTES`, `JUMP_KEEP_BYTES` | F words kept as signed bytes (274) | b | text 0x2165F0 / 0x1A42A0, 276 entries: knife's 274 in order + F501, F967 | same |
| 3.6 | `F_BITS`, `JUMP_KEEP_BITS` | F words kept as flags (42) | b | text 0x216580 / 0x1A4500, 43 entries: knife's 42 in order + F500 | same |
| 3.7 | `JUMP_CLEARS` (`game.rs`, sub_95800) | F words zeroed by flowchart column (19 (last column, word) pairs) | b | **code** at text 0xBA700 (`cmp edi,0xCB; jg ret`, stores `mov word [rax+0x8A],0` ...; one qword store covers F065..F068). `TinyX86` runs it for columns 0..276; the result is 19 pairs, equal to knife's with F062 as (160, 62) where knife writes (161, 62) plus the exception "not at row 161". Called from 0x23190 (the flowchart jump), which then calls 0x2A080 (= sub_178C8) | `read_jump_clears`; `JumpClearTable` |
| 3.8 | flow-jump special pairs (`game/flow_jump.rs`, sub_93D10) | which scene transitions need hidden nodes / extra flags | c | **code, no table.** The PS4 function is text 0xB9460 (called from 0xBA6B0, the `_JUMP` edge code; jump table 0x220C24 over from-node 24..523); two spot checks equal knife's case arms (from-node 24 accepts only to-node 371: `cmp esi,0x173` at 0xB9531; from-node 51 with to-node 69 marks node 519 = 0x207: 0xB953F). It calls 0xBA3C0 (= sub_954B0, the edge lookup in the table 4.2) and 0xB85A0 (node mark). Port the logic from knife's `jump_special` and verify it against 0xB9460 | none |
| 3.9 | `WINDOWS` / `EN_WINDOWS` | message window types: slot + origin in 1280x720 units, 33 / 56 | b | text 0x2006C0, 56 x {u32 slot, f32 x, f32 y}; (x, y) 56 / 56 with `EN_WINDOWS`, 33 / 33 with `WINDOWS`; the slot numbers are the PS4's own (another texture list). Also decoded in textbox.md 7 (`textbox.py`) | `read_window_types`; `MessageWindowTable.Types` |
| 3.10 | `WINDOW_DX`, `WINDOW_DY` | window x offsets by F1106 (27), y offsets by F1107 (37), f32 | b | text 0x2005B0 / 0x200620; 27 / 27 and 37 / 37 | `read_window_offsets`; `MessageWindowTable.Dx/Dy` |
| 3.11 | `BUBBLE_TEXT` / `EN_BUBBLE_TEXT` | bubble text origin per window type (15 / 56) | a | text 0x200960, 56 x {f32, f32} (textbox.md 7, `textbox.py bubble_text_origin`). A different quantity: the PS4 value is added to the window origin inside its own formula; 16 / 56 equal knife's pairs, e.g. (97, 62.67) against knife's (98, 80). Use the PS4 formula | `textbox.py` |
| 3.12 | `BUBBLE_WIDTH` / `EN_BUBBLE_WIDTH` | bubble text width per type (3 / 40 f32, 299.83 / 405.57 / 509.77 ...) | c | not found as f32 or i32 triples; the PS4 computes the bubble text width in its layout code | none |
| 3.13 | `TEST_SES` | config SE test list, 14 names | b | text 0x1A6774, 14 inline 13-byte names; 14 / 14 | `read_test_se_names`; `TestSeNames` |
| 3.14 | `SYS_SOUNDS` | 9 BFSAR item ids of the system sounds | c | Switch sound archive ids. The PS4 plays 9 HE-VAG pairs from `vag.mrg` (sound_runtime.md 10) | `audio_hevag` |
| 3.15 | `VOICE_SPEAKERS` | voice name prefix -> speaker, 55 | b | text 0x215E90, 36 x 16-byte records {char key[8] (prefix **reversed**: "00A" for A00), i32 speaker, i32 0}; 55 / 55 (knife's other 19 entries, D00..D92 and M04..M10, are speaker 0, the default) | `read_voice_speakers`; `VoiceSpeakerTable` |
| 3.16 | `SPEAKER_ROW_BIT` | speaker -> config voice row bit, 24 | b | data 0x86A850, i32 x 24; 24 / 24 | `read_speaker_row_bits`; `VoiceSpeakerTable.RowBits` |
| 3.17 | `VOICE_ROW_WORDS` | config voice row -> progress F word, 19 | b | text 0x1A6850, i32 x 19; 19 / 19 | `read_voice_row_words`; `VoiceSpeakerTable.RowWords` |
| 3.18 | `voice_ids.txt` | voice file name -> id (dumped from the exe's voice tables) | c | the PS4 ids are the entries of `voice.hed` / `voice.nam` (audio.md) | `tsuki.audio` |

### 3.4 Flowchart, gallery and music

| id | knife table | holds, layout, count | st | PS4 evidence | reader |
|---|---|---|---|---|---|
| 4.1 | `ROWS` (`flow_table.rs`) | 277 columns {phase, nodes} with 1,286 nodes of 9 i32 | a | relro 0x418770 (277 x 32 B) + text 0x18FC90.. (64-byte nodes) (gallery.md 5). Fields 0..6 equal for 1,286 / 1,286; fields 7 and 8 (SYSMES ids) equal with the +1 rule 1,286 / 1,286; column group / count 277 / 277. +C# | `misc_gallery_extras_flowchart`; `FlowchartTables.Columns` |
| 4.2 | `EDGES` | 629 x {from, to, up to 5 edge flag ids} | a | text 0x2218D0, 630 x 0x30 B (flags also for the playthrough words). 628 / 629 equal: PS4 has (87, 88, [661, 662]) + (87, 89, [662]) where knife has (87, 88, [662]). +C# | same; `FlowchartTables.Edges` |
| 4.3 | `SCENE_ID_NODE` | scene id -> node, 500 pairs | b | derived: `scene_ids[n + 1]` with `SCENE_NODE[n]` (3.2); 500 / 500. (The node table's `script_scene` field alone gives 495 of them.) | `read_scene_node`; `SceneNodeTable` + `SceneTable` |
| 4.4 | `TITLES` | flow_phasetitle cells (x, y, w, h), 30 | b | text 0x1A3EE0, i16 x4, stride 8; 29 / 30 (row 29, knife's separate Prologue cell, is zero; row 28 is the Prologue) | `read_flow_title_cells`; `UiCellTables.ReadFlowTitleCells` |
| 4.5 | `TITLES_EN` | English flow_phasetitle cells, 30 | b | text 0x1A3FD0; 29 / 30 | same |
| 4.6 | `LINES` | flow_line_merge connector cells, 13 grey + 13 lit | b | text 0x1A40C0, 12 cells (kind 0..11, indexed by the connector at node +0x04, code 0x26AC0); grey kinds 11 / 12 (kind 10 is y 281 against 280); the lit half and cell 12 are not in the table | `read_flow_line_cells`; `UiCellTables.ReadFlowLineCells` |
| 4.7 | `CG_GROUPS` | 458 groups {condition id, entries (name, gallery-only, thumbnail, w, h)}, 1,977 entries | a | relro 0x400830 (458 x 32 B) with 1,972 entries (relro 0x405240.., 32 B each) (gallery.md 4). With knife's `CG_GROUPS_OVERSEAS` applied: name, thumbnail, size and category 1,972 / 1,972 (the PS4 build is the overseas one). knife's "gallery-only" flag is not the PS4 +0x1C byte (1,140 / 1,972 agree). +C# | `misc_gallery_extras_flowchart`; `GalleryTables.Groups` |
| 4.8 | `CG_GROUPS_OVERSEAS` | 3 groups (222, 285, 327) that replace the Japanese ones | a | the PS4 table already is the overseas one; groups equal 3 / 3 | same |
| 4.9 | `CG_ORIGINS` | 19 view offsets {name, x, y} | a | the entry fields +0x0C / +0x10; 19 / 19 | same |
| 4.10 | `CG_LANGUAGE_STRIPS` | 11 four-language-band CG names | a | relro 0x4051E0, 11 pointers (sorted differently); same set 11 / 11 | `GalleryTables.Special` |
| 4.11 | `CG_ALIASES` | 105 {name, alias names} | a | relro 0x404170, 105 x 0x28 B; 105 / 105 | `GalleryTables.Aliases` |
| 4.12 | `MUSIC` | 95 tracks {bgm number, title SYSMES, length ms, up to 5 variants} | a | text 0x1A47D0, 95 x 0x38 B (gallery.md 6.1); bgm / title (+1 rule) / variants 95 / 95; length 84 / 95. +C# | `misc_gallery_extras_flowchart`; `GalleryTables.Music` |
| 4.13 | `MUSIC_LOOP` | 95 x {loop start, end} samples | a | record fields +0x1C / +0x20; start 95 / 95; knife's end = PS4 end + 1 in 87 / 95 | same |
| 4.14 | `MUSIC_CREDITS` | 95 x 4 credit SYSMES ids | a | record fields +0x08..+0x14 (+1 rule); 95 / 95 | same |
| 4.15 | `MOVIES` (`gallery.rs`) | 3 gallery movies {movie index, viewing flag} | a | no table: `MOVIE_ITEMS` constants in the decoder, code 0x2B610 (gallery.md 7) | `misc_gallery_extras_flowchart.MOVIE_ITEMS` |
| 4.16 | `ENDINGS` | 25 {RDST node bit, scene id, prompt SYSMES} | a | text 0x18F810, 25 x 20 B (gallery.md 8); scene, node, prompt (+1 rule) 25 / 25. +C# | `GalleryTables.Extras` |
| 4.17 | `EN_ENDING_DONE` | 24 {completed node 1, node 2} | a | same records, fields +0x08 / +0x0C; 24 / 24 | same |

### 3.5 UI tables

| id | knife table | holds, layout, count | st | PS4 evidence | reader |
|---|---|---|---|---|---|
| 5.1 | `DIALOG_SYSMES` | SYSMES message per confirm dialog, 24 | b | text 0x2D2D40, i32 x 55, -1 = none (the same table gallery.md 1 calls "help messages"); 24 / 24 with the +1 rule and two PS4-only dialogs after knife's dialog 20 (PS4 index k + 2 for k >= 21) | `read_dialog_messages`; `DialogMessageTable` |
| 5.2 | `BANNER_SRC` | mode banner cells {x0, y0, x1, y1}, 4 | b | data 0x781B10 and 0x781B20 (i16 x4 + 2 words); 2 / 4: the PS4 build has two banners | `read_banner_rects`; `UiCellTables.ReadBannerCells` |
| 5.3 | `BANNER_KEYS` | banner pop-in scale keys, 10 x {f32, f32} | b | data 0x781AC0; 10 / 10 (used by 0x564A0) | `read_banner_keys`; `UiCellTables.ReadBannerKeys` |
| 5.4 | `FACE_RECTS` | conf_faceicon cells per voice row, 20 | b | data 0x66E6B0, i16 x4; 20 / 20 | `read_voice_face_rects`; `UiCellTables.ReadVoiceFaceCells` |
| 5.5 | `NAME_RECTS` | conf_name cells, 20 | b | data 0x66E750; 20 / 20 | `read_voice_name_rects`; `ReadVoiceNameCells` |
| 5.6 | `NAME_RECTS_EN` | English conf_name cells, 20 | b | data 0x66E7F0; 20 / 20 | same |
| 5.7 | `RADIO_CELLS` | btn_base1 rows (y, width) for 6 radio pairs | c | not found (i16 / i32 / f32 rows of 4 and 12 values). The PS4 config art is another sheet | none |
| 5.8 | `SAVE_TITLES` | savetitle cells by phase F023, 38 | b | data 0x7A85C0, i16 x4; 36 / 38 against knife's Japanese table; the two others are phases 16 and 18 widened to x1 571, which knife's comment names for the overseas non-English table, so 38 / 38 against that | `read_save_title_cells`; `UiCellTables.ReadSaveTitleCells` |
| 5.9 | `SAVE_TITLES_EN` | English savetitle cells, 38 | b | data 0x7A86F0; 38 / 38 | same |
| 5.10 | `INFO_ROUTE` | route of phases 11..36 for the save info panel, 26 | c | not found (u8 / i16 / i32, whole and in 8..20-entry pieces) | none |
| 5.11 | `PHASE_TITLES` | system_phasetitle cells by phase, 38 | b | data 0x7A6E70; 38 / 38 | `read_phase_title_cells`; `UiCellTables.ReadPhaseTitleCells` |
| 5.12 | `PHASE_TITLES_EN` | English cells, 38 | b | data 0x7A6FA0; 38 / 38 | same |
| 5.13 | `PHASE_TITLES_ZH` | Chinese cells, 38 | b | data 0x7A70D0; 38 / 38 (the PS4 uses it for ko as well) | same |
| 5.14 | `GUIDE_SEQ` | button guide: per guide index a list of (icon / word, cell) pairs, 12 lists | c | **built inline.** Function 0x6A3C0 (switch of 12 cases) copies the pairs from rodata chunks 0x209BE0..0x209FC0 onto the stack as i32 (kind, cell) pairs, -1 terminated, word before icon (knife: icon before word); no contiguous table | none |
| 5.15 | `GUIDE_ICONS` | help_key icon cells, 23 | b | data 0x7A6190, i16 x4, 23 cells then a zero cell; 16 / 23 (rows 13..22 are the PS4 pad icons) | `read_guide_icon_cells`; `UiCellTables.ReadGuideIconCells` |
| 5.16 | `GUIDE_WORDS` | help_txt word cells, 27 (Japanese; per language in CONTHELP_TXT_UV.BIN) | b | data 0x7A6250, 5 language blocks of 27 cells (0xD8 B); Japanese 26 / 27 (cell 19: x 76..144 against 72..156) | `read_guide_word_cells`; `ReadGuideWordCells` |
| 5.17 | config layout (`conf_layout_at`, `dword_46CDB0` + 628 * tab) | positions of the config controls (640x448), slider maxima, the cursor grid; inlined as floats in knife | b | data 0x66E890, 2 pages of 0x9D0 B = 628 i32 (adv_state.md 9.1): item grid +0x280, type grid +0x78, positions +0x488 (5 x {f32 x, f32 y} per row, 40 B), current +0x900, maxima +0x968. knife's control coordinates equal the page floats (System tab 245.0 / 120.667 / 452.0 / 475.0 columns, y 100.8 .. 677.393); the radio rows carry +6.637 in knife. 16 + 26 rows; the Sound tab's rows 7..25 hold -9999 (placed at run time, knife: 370.01 + 50.607 n) | `read_config_pages_layout`; `ConfigPageTable` |
| 5.18 | `fill_colour` (`text.rs`) | `@c1..@c5` fill colours | a | text 0x2D34C0, u16 x6 RGB565 (word 0 is the default colour) (text.md 6); 5 / 5 within 8 levels per channel. +C#: `TextColourTable` | `read_text_colours`; `TextColourTable` |
| 5.19 | `edge_colour` | `@c6..@c9` edge colours, 4 | c | not found; text.md 6 leaves `@c6..9` undecoded | none |
| 5.20 | `EX_TAGS`, `SYMBOLS` | inline glyph tag names -> exfont characters, 10 | a | tag name list at text 0x2D358C (two copies, 0x187C8C) (text.md 6.4) | `text.py` |
| 5.21 | `RUBY_SCALE` | ruby scale per language, 5 f32 | c | not found as f32 (also 4 languages); ruby uses the fixed scale of fonts.md 7.4 | none |
| 5.22 | `CONFIG_DEFAULTS` (`savedata.rs`) | default config block, 104 bytes | c | not found (whole, or the 40-byte head). The PS4 config block is 0x140 bytes with the boot values set by code (adv_state.md 9.2) | `misc_adv_system_state` |

### 3.6 Skipped

`SEL_WIN`, `WIK_RECTS`, `WIK_REGIONS`, `WIK_POS`, `WIK_ORDER` (Witch-only choice screens), `WITCH_*`, `teatime.rs`, `touch.rs`,
`SE_STATE_IDLE` (runtime layout of a save record), `OVERSEAS_TITLES` (control.nacp), `OUTLINE` (the Switch glyph-cache outline sub-sets; the PS4 uses pre-rendered glyph pages), button-bit remap 0x3C9C3C (Switch pad), and the
layout constants named in the scope.

## 4. What the stage package needs

- **Motion loops** (open question closed). The PS4 has no table; `read_motion_loops` / `MotionLoopTable.Read` run the compiled
  switch and give the 2,027 looping motions, identical to knife's list and to the unicorn extraction of layers.md 8.1. Use
  `LoopPoint(id)`: 0 none, 1 whole motion, n > 1 loops from n ms. The result takes 1.7 s in Python and 0.09 s in
  C#; read it once at start-up (or lazily per id: `TinyX86.Run(0xC1C70, id)` costs about 4 microseconds).
- **STRA haze.** `HazeTable.TryGetEntry(id)`: ids 1..557, 10 floats each, same meaning as knife's `Haze::params` (sub_362A0:
  fields 0 / 3 scale, 2 / 5 amplitude factors, 6 amplitude, 7 period term, 9 fade-in ms). 16 entries were retuned for the PS4:
  take the values from this table, not from knife's. layers.md 4.3 notes that the PS4 draw of the phase was not traced, so what the PS4 does with the ten floats is [H]
  (knife's meaning) until a capture settles it.
- **STEC grades.** The PS4 grade tables are the evaluated LUTs of `read_stec_tables` (compositing.md 9). knife's parameters are
  the same looks (same mode ids; exact for the 23 tint-free modes, within 4 levels for the tinted ones, PS4 forces LUT[0] = 0): do not use knife's `TABLE`.
- **Quakes / shake.** knife has no table; `quake.md` formulas stand.
- **Field masks and key data.** `MotionFieldMasks` (new C#) next to `MotionTables`; 46 of 71,905 PS4 keyframes (33 motions, list
  in 1.5) differ from the Switch: the PS4 data wins.
- **Flowchart jump keep lists / clears** (game package): `FlowKeepLists` (PS4 adds F967, F501, F500), `JumpClearTable`.

## 5. PS4 versus Switch (candidates for knife_port.md 3 and docs errata)

1. SYSMES numbering: PS4 = knife + 1 from 217 (the overseas "deleted" popup); the confirm-dialog table has two more entries after dialog 20.
2. 16 haze entries, 46 motion keyframes, 1 flow edge pair, 11 music lengths, 8 music loop ends, 2 FCLN keys and 3 button-guide rows differ; all listed with counts in section 3.
3. The keep lists carry three PS4-only variables (F967 in words and bytes, F501 in bytes, F500 in bits); the history copy and the flow-jump copy are identical.
4. `SCENE_NODE` is `i32` here, `i16` in knife; `JUMP_CLEARS` is code on both; STEC is LUTs on the PS4 and parameters on the Switch; SE and voice names live in archives.
5. Possible erratum for adv_state.md 9.1: the config type codes 3 / 4 / 5 are called label / value / slider there. knife places its minus button, slider and plus button at the coordinates of codes 3, 4 and 5 [H, to check on a render].
6. Phase-title language rule: Korean uses the "zh" table on the PS4 (knife only names the Chinese languages).

## 6. How to re-run

```powershell
$env:PYTHONPATH = "tools"
.venv\Scripts\python.exe -m tsuki.engine_tables                 # census of what every reader returns
.venv\Scripts\python.exe -m tsuki.engine_tables crosscheck      # the match counts of this document (needs ref\knife)
.venv\Scripts\python.exe -m tsuki.engine_tables digests         # SHA-256 per table; --write PATH rewrites the proof file
.venv\Scripts\python.exe -m pytest tools\tests\test_engine_tables.py -q
dotnet test tests\Tsuki.Tests.Data\Tsuki.Tests.Data.csproj --artifacts-path artifacts\K1b --filter "FullyQualifiedName~EbootTables"
```

## 7. Open issues

- The flow-jump special pairs (3.8) and the button-guide sequences (5.14) are code on the PS4; they need a transcription, not a table reader.
- Not searched beyond content matching: the Witch-only tables, `fx.rs` menu strips (2.6; the PS4 title screen may use other art entirely).
- The meaning of haze field 7 and of the first-column `Order` field of flow nodes is unconfirmed on the PS4 (docs list them [H]).
