# Gallery, extras, flowchart and unlock data

Scope: everything behind the title screen's GALLERY item and the flowchart of TSUKIHIME -A
piece of blue glass moon- (PS4, CUSA47999, app 01.01): the CG gallery, music player, movie
gallery, ending list ("Teach Me, Miss Ciel" extra scenarios), the flowchart, the persistent
unlock bits that drive them, how a gallery entry replays a scene, and which gallery-looking
data in the eboot is dead. All lists live in eboot.bin; the pictures are the allpac3 UI sets
`GALLERY_PARTS_*` (archives_ui_movies.md 7).

Python reference: `tools/tsuki/misc_gallery_extras_flowchart.py` (extractors, unlock model,
flowchart filter), `tools/tsuki/misc_gallery_extras_flowchart_emu.py` (the engine's column
filter run under unicorn), `tools/tsuki/misc_gallery_extras_flowchart_sheets.py` (contact
sheets). Tests: `tools/tests/test_misc_gallery_extras_flowchart.py` (14 tests, about 1.5 s).
Samples: `samples/misc_gallery_extras_flowchart/` (CSV tables, two contact sheets). Scratch
dumps and notes: `scratch/misc_gallery_extras_flowchart/`. Alias:
[misc_gallery_extras_flowchart.md](misc_gallery_extras_flowchart.md).

Confidence labels:

- **[E]**: read from the disassembly (text VA = file offset - 0x2280; RELRO VA 0x400000 =
  file 0x32C210; data VA = file offset - 0x365FA0 + 0x600000).
- **[V-all]**: checked on every record of the retail data.
- **[EMU-n]**: the game's own function was run under unicorn on n inputs and compared with the
  Python model, 0 differences.
- **[D]**: observed in the data, with no engine proof.
- **[H]**: hypothesis.

---

## 1. Overview

```
title menu "GALLERY"  --title action 7-->  0x67410: menu mode 0xDF2A50 = 15
   system state machine 0x68A70 (state 4) -> 0x62790 -> Gallery_Update 0x12680
      cGallery (0x12840): builds the unlocked-CG list once, owns the screens
      cGalleryMain (0x16560) menu, cursor 0..5 (GALLERY_PARTS_COMMON child 1):
         0 CG Gallery    -> cGalleryCG      0x18FB0  (main 0x1C5C0 / sub 0x1E750 / photo 0x1C730)
         1 Music Player  -> cGalleryMusic   0x2C220  (list 0x1A47D0)
         2 Flowchart     -> cFlowchart      0x20960  (columns 0x418770; needs scene_seen(1))
         3 Movie         -> cGalleryMovie   0x2A970  (3 movies, player 0x2B860)
         4 Ending List   -> cGalleryEnding  0x1E890  (25 extra scenarios, table 0x18F810)
         5 Title         -> result 1: back to the title
   gallery result r >= 2 ("play script scene r-2"):
      0x68CE0: G[8] = r - 2; start script scene 3000 (ARCHIVE), replay flag F024 = 1
      ARCHIVE jumps on G008 (directly or through JUMP00..JUMP15 / JUMPQA)
```

There is no voice gallery in this build: the menu sheet (GALLERY_PARTS_COMMON child 1: "CG
Gallery, Music Player, Flowchart, Movie, Ending List, Title", three states each) and the screen
factory 0x18390 (cases 0..4, default = none) have exactly these six items [E, V]. The ui strings
226 "Listen to voices." and 228 "Play extra scenarios." exist [H: help-line leftovers; 228 fits
the Ending List].

Unlock state is a handful of persistent bit sets in var_G and in two RAM blocks that the
system save stores (section 2). Every gallery screen tests them with tiny leaf functions;
the script commands `_RDST`, `_RTST`, `_RDED`, `_STCG`, `_MPLY`, `_SELD`, `_JUMP` and the
picture display code set them.

Counts [V-all]:

| list | where | records | used by |
|---|---|---|---|
| CG groups | RELRO 0x400830 | 458 x 32 B | 0x12AB0, 0x1C620 |
| CG entries | RELRO 0x405240..0x4181C0 | 1,972 x 32 B (458 lists, NULL-terminated) | 0x12AB0, 0x1B950, 0x1C730 |
| CG aliases | RELRO 0x404170 | 105 x 0x28 | 0x12840, 0x13AB0 |
| four-frame CG names | RELRO 0x4051E0 | 11 x char* | 0x1BC4C, 0x1C3B0 |
| picture seen-index map | data 0x86AA30 | 2,144 x 16 B | 0x95A60 |
| flowchart columns | RELRO 0x418770 | 277 x 32 B | 0x21430 |
| flowchart nodes | text 0x18FC90..0x1A3E10 | 1,286 x 64 B (495 scene nodes) | 0x21430, 0x22100, 0x29470 |
| flowchart edges | text 0x2218D0 | 630 x 48 B | 0xBA3C0 (`_JUMP`) |
| choice edge flags | text 0x2214E0 | 83 x 3 x i32 | 0xB8620 (`_SELD`) |
| music player | text 0x1A47D0 | 95 x 0x38 | 0x2CBA0 |
| ending list | text 0x18F810 | 25 x 20 B | 0x1F690, 0x1FB40, 0x1FB80 |
| help messages | text 0x2D2D40 | 55 x i32 | 0xC2A20 |
| trophy scenes | text 0x221400 | 27 x 8 B | 0xB7CC0 |

---

## 2. Unlock state

### 2.1 Bit sets [E]

| set | RAM | save | bit for id n | setters | test |
|---|---|---|---|---|---|
| scene_seen | G[110..145] (0x164C620 + 0xDC) | system +0x144 + 0xDC | `G[110 + n/16] >> n%16`, n <= 0x23F | `_RDST(n)` 0xB8110, 0xB85A0, 0xB80E0 | 0xB81A0 |
| scene_done | G[300..335] (+0x258) | system +0x39C | `G[300 + n/16] >> n%16`, n <= 0x23F | `_RDED(n)` 0xB7E40, 0xB85A0, 0xB7E00, choice cases 0xB86FC.. | 0xB7B50 |
| edge_seen | G[400..443] (+0x320) | system +0x464 | `G[400 + (n-564)/16] >> (n-564)%16`, 564 <= n <= 1267 | 0xB8520, 0xB8620 (`_SELD`), 0xBA3C0 (`_JUMP`) | 0xB7B80 |
| movie_seen | G[5] (+0xA) bits 0..15 | system +0x14E | bit b | 0xB7C80 from 0x5F6F0 (movie end) | 0xB7CA0 |
| picture_seen | byte array 0x164D5C0 bits 0..3199 | system +0x52C | `byte[i/8] >> i%8`, i = seen index (2.2) | 0x95CA0 (`_STCG`), 0x7DD10 (twice), 0x85F90 (display paths) | 0x95D60 |
| bgm_heard | same array, bits 3200..3719 | system +0x52C | bit 0xC80 + track, track <= 0x207 | `_MPLY` 0x87250, 0x87630 (from 0x81E0, 0x2C2D4, 0x2CC93) | 0x95E20 |
| choice_taken | byte array 0x164FBC0 | system +0x16F4 | `byte[choice_id] >> option` | choice screen 0xA6647 | `_SELD` 0x9B626 |
| per-save copies | F[201..272] (+0x192, 8 bits per u16), F[600..687] (+0x4B0, 8 bits per u16) | slot state | as above with 8 bits per word | `_RTST` 0xB8220, 0xB8560, 0xBA3C0 | 0xB7C10, 0xB7C40 |

- The three G sets are plain G words: `_WKST(G110,..)` would alias them (vm_variables work).
  Their maxima: scene ids 0..575 (`cmp edi, 0x23F`), edge ids 564..1267 (`lea eax, [rdi-0x4F4];
  cmp eax, -0x2C0`).
- Combined tests: 0xB7B00(n) = scene_done(n) for 1..562, edge_seen(n) for 565..1194; 0xB7BC0
  the per-save version.
- **Load merge** (`SaveImage_LoadSystem` 0x6F470) [E]: G[0..499] is copied from the system
  block (the "OR these G words" index list at data 0x86AA14 is empty, first value -1); the
  picture/BGM block is ORed into RAM (0x200 bytes with `vorps`, bytes +0x200..+0x20B one by
  one); the choice block is ORed for 0x110 bytes, the bytes +0x1804..+0x180B of the save are
  config copies (0x164FCD8 / 0x164FCE8, written by 0x419D0, adv_system_state). The readmax
  array is max-merged (`vpmaxub`).
- `_RTST(n)` additionally stores F[110] = n when n is 533..557 (the QA scenes) or F[24] != 0
  (replay); `_RDED(n)` sets scene_done(n) only if `n == F[110]` and the per-save bit n is set,
  then runs the trophy check 0xB7CC0. In normal play F[110] is written by the `_JUMP` edge code
  (0xBA67F: `F[110] = to`), so a scene entered by `_JUMP` can be completed by its `_RDED` [E].
  Scripts never write F110 [V-all].
- `_RDAS(n)` (0x98118): ignored during a gallery replay (0x13610: [0x66AD00] == 2); otherwise
  0xB8020 calls 0x61F80(1) when the scene is already done, fast-forward is off and config +0xA0
  is set [E; meaning H: "offer to skip a completed scene", adv_system_state].

### 2.2 Picture seen index [E, V-all]

`_STCG(name)` resolves `name` to a picture id (0x82CE0: NameTable_Lookup in names_allpac2
0x7F6800) and calls 0x95CA0; the display paths do the same with the layer's picture id.

```
seen_index(pid):
    if image_info[pid].+0x3F & 1:                  # an "_HH_VV" variant (Image_ResolveSprite 0x957B0)
        pid = lookup(names_allpac2, name[pid] without its last 6 chars)   # -1 if absent
    if pid >= 0xA8D:                               # 2,701
        pid = seen_map.get(pid, -1)                # std::map 0x174DE30 built by 0x95A60
    return pid if 0 <= pid <= 0xC7F else -1        # 3,200 picture bits
```

`seen_map` comes from the 16-byte records at data 0x86AA30 `{char* name, i32 id (written at
run time), i32 seen_index}`, ended by a NULL name. 0x95A60 looks each name up in names_allpac2
and, when found, does `map[id] = record` (operator[]: a later duplicate wins). 2,144 records,
718 names resolve, seen indices 2701..2917 (217 values: one bit per character pose group;
sprite variants that share a pose share the bit) [V-all]. Pictures 0..2700 are their own bit.

names_sprites (data 0x7A8B10, 9,958 x 32 B, `NameTable_Lookup` cache 0x16543A0) equals the first
9,958 names of names_allpac2 [V-all]. It is not dead: the gallery looks CG names up in it
(0xA04B0), so a CG whose picture index is >= 9,958 can never be found directly.

### 2.3 Unlock rules by screen

| item | unlocked when | code |
|---|---|---|
| gallery menu item Flowchart | scene_seen(1) (01_00_ARC01_1 started); else message 1497 "You cannot use the flowchart yet." | 0x18973 -> 0x26430(1) -> 0xB81A0; 0x16CD8 |
| CG category c (1..19) | any scene_seen of the category's scenes (table 4.4); category 0: always | 0x1C460 |
| CG entry | category ok and (alias seen or picture_seen(entry) or scene_done(557)) | 0x13630 |
| CG group tile (main grid) | at least one entry unlocked | 0x1C620 |
| flowchart node | scene_done(scene) or edge_seen(link) | 0x21558.. |
| flowchart column | column filter (5.4) | 0x21430 |
| music track | bgm_heard(track) or bgm_heard(any variant) or a special rule (6.2) | 0x2CBA0 |
| movie 1, 2 | movie_seen bit 0 (op1_arc watched), bit 1 (op2_ciel watched); movie 0 always | 0x2B610 |
| ending-list entry i < 24 | scene_done(unlock_a) or scene_done(unlock_b) | 0x1FB40, 0x1FB80 |
| ending-list entry 24 (QA_25) | all 24 other entries unlocked | 0x1FB80 (esi == 0x15) |

---

## 3. Entering the gallery and replaying scenes

### 3.1 Reachability [E]

- `Title_Update` (0xB06B0) returns `ebx`, which is 0 except: 1 (0xB16D1), 2 (0xB12EE), 3
  (0xB0B84) and `state - 0x20` in the fade case of the state switch (jump table 0x21893C, case
  0xB077E for states 0x25..0x28). The only one of those states ever stored in the state word
  0x17621FC is 0x27 (0xB185B); the computed store at 0xB0977 writes `4 * byte[0x176221C]` and
  that byte is only ever 0 or 1 (0xB177E). So the title returns {0, 1, 2, 3, 7}.
- The dispatcher 0x68DAA (jump table 0x209DC0, `action - 1`): 1 -> 0x68DCB, 2 New Game, 3 ->
  0x69154, **5 -> 0x656D0 and 6 -> 0x66380 (the `extra_parts.mrg` screens)**, **7 -> 0x67410
  (gallery)**, 9 -> start scene 0x708, 4/8/10..32 -> 0x69182 (start scene `action + 8990` for
  10..31, i.e. 9000..9021), 33 -> start scene 0xBE, 34 -> state 6.
- Consequently actions 5, 6, 9 and 10..34 cannot happen in this build: the extra_parts screens,
  the legacy lists they read (section 9) and the 9000+n entries are dead. No script has a scene
  id >= 9000 (the highest is JUMPQA = 8017) [V-all], so even a forced 9000+n would not find a
  script (the start fallback belongs to boot_title_choice_flow).
- `Gallery_Update` (0x12680) is also called by title state 0x19 (0xB0CD9; result 1 -> back to
  title state 6), but no store of 0x19 into the title state word was found [H: unused path].

### 3.2 Gallery result and replay [E]

`cGallery` (0x12840, 0xA8 bytes, global 0xDD96B0) keeps a result at +0x10 that
`Gallery_Update` returns once the gallery has finished:

- 1: back to the title (menu item 5, 0x13360(gallery, 1)).
- `scene + 2`: replay. 0x13570(gallery, script_scene, fc_scene, extra) stores the result,
  `0x66AD04 = fc_scene` (or script_scene when fc_scene < 0), `0x66AD08 = extra` and F[24] = 1.
  Callers: flowchart (0x22F15, 0x2407B: node +0x0C, node +0x00, screen +0x48) and ending list
  (0x1F758: record +0x00, -1, -1).

The system state machine (0x68C59 -> 0x68CE0) then: `G[8] = result - 2`; trophy 0x1C if
`result - 1002` is 0..24 (a QA scene); main state 2 (game) with start scene 0xBB8 = **3000**.

Script `ARCHIVE` (scene 3000, allscr index 528) [V]: clears F823 / F847..F869, sets F822 = 1,
then `_IFSC`, F024 = 1, F025 = 1, F028 = 1, F026 = G008 and dispatches:

```
G008 in 2000..2007          -> _JUMP(G008)            (scripts 12_01_CIEL12_3C_FLAG0 .. 13_01_01_CIEL13_2_FLAG1)
1000 <= G008 <= 1024        -> _JUMP(8017, ARCHIVEJUMP)  JUMPQA: _JUMP(1000..1024) = QA_01..QA_25
G008 >= 789 / 750 / 712 / 687 / 672 / 618 / 577 / 535 / 493 / 465 / 411 / 377 / 356 / 331 / 300 / 240
                            -> _JUMP(8016 / 8015 / ... / 8001, ARCHIVEJUMP)   JUMP15 .. JUMP00
```

The JUMPxx scripts set route flags with `_SELR`/`_SELD` blocks and jump to the scene
(script.md). `QACALL` (3100) is the in-story entry to the Q&A scenes (F023 = 37, F022 =
492..516, F027 = 1..25).

---

## 4. CG gallery

### 4.1 Records [E: 0x12AB0, 0x1B950, 0x1C730; V-all]

CG group (RELRO 0x400830 + 32*g, g < 458 = `0x1C530: mov eax, 0x1CA`; accessors 0x1C550 /
0x1C570):

| off | type | field |
|---:|---|---|
| 0x00 | ptr | entries (RELRO, 32-byte records) |
| 0x08 | i32 | category 0..19 (4.4) |
| 0x0C | 20 B | zero |

CG entry (32 bytes, list ends at a NULL name):

| off | type | field | use |
|---:|---|---|---|
| 0x00 | char* | name with a 1-char prefix, e.g. `" IMG1691.MZP"`, `"-IMG3864.MZP"` | every reader uses `name + 1` (0x12BCC NAM search, 0x1365B / 0x13B73 lookups, 0x1BB17) |
| 0x08 | i32 | GALLERY_PARTS_CG_TH child | 0x1BB13 -> `0xEB350(slot, cg_th_archive, child, -1)` |
| 0x0C | i32 | x | packed to u16 with y (0x1CF1E) into the photo viewer [H: initial view offset] |
| 0x10 | i32 | y | |
| 0x14 | i32 | w | photo viewer image size: pivot (w/2, h/2), scale 1920/w, 1080/h (0x1CF0E..0x1CFF9) |
| 0x18 | i32 | h | |
| 0x1C | i32 (byte read) | flag | 6 entries (IMG0850..0854, IMG1740): viewer widget vfunc +0xC0(1) and thumbnail `0x33210(.., 2, ..)` (0x1BBED, 0x1CFA8) [H: pan / scroll mode] |

Worked example, group 4 and its first two entries:

```
004008b0: 6053 4000 0000 0000 0000 0000 0000 0000   entries -> 0x405360, category 0
00405360: c585 1800 0000 0000 fc02 0000 0000 0000   " IMG1691.MZP", CG_TH child 764, x 0
00405370: 0000 0000 3a08 0000 f204 0000 0000 0000   y 0, w 2106, h 1266, flag 0
00405380: d285 1800 0000 0000 ff02 0000 ...         " IMG1694.MZP", CG_TH child 767
```

The prefix is never compared by any code [E: no `cmp ..., 0x2D/0x20` in 0x12000..0x30000; all
readers add 1]. Data: ' ' 1,146, '-' 826; 822 of the '-' names are gallery-only pictures
IMG3000..IMG4193 (allpac1_2, no allpac2 copy) and 4 are ordinary pictures; 198 ' ' names are
gallery-only too [D: an authoring mark, probably "composite / difference picture"].

Category counts (groups): 0: 270, 1: 2, 2: 4, 3: 14, 4: 28, 5: 16, 6: 1, 7: 3, 8: 2, 9: 3,
10: 2, 11: 8, 12: 4, 13: 2, 14: 10, 15: 21, 16: 2, 17: 61, 18: 1, 19: 4.

Alias record (RELRO 0x404170 + 0x28*i, i < 105 = `0x1C540: mov eax, 0x69`): `char* key;
char* alias[4]` (NULL-padded). The cGallery constructor inserts `std::map<string, rec*>[key]`
(0x12980). All 105 keys are CG entry names (96 '-' and 9 ' '; 97 are gallery-only pictures)
[V-all]. Example: `IMG3757.MZP -> AOK_A_30_00_N00.MZP, BOY_A_30_00_N00.MZP`.

Four-frame names (RELRO 0x4051E0, 11 x char*): IMG1699, 1700, 1701, 1702, 1736, 1737, 2581,
3819, 3821, 3822, 4026. Their CG_TH children are the only 231x520 ones (four 231x130 frames);
the other 1,968 are 231x130 [V-all]. The thumbnail (0x1BC4C..) and viewer (0x1C3B0) pass
"is one of these" to 0x33230 [H: frame animation].

### 4.2 Thumbnails [E, V-all]

`0x1B950` loads, for tile i of the current list, child `entry.cg_th` of
`gallery_parts_cg_th.mrg` into texture slot `base + i` (0x51B = 1307 for the main grid,
0x70F = 1807 for a group's sub-grid) and places it at
`x = 392 + 240*(i % 6), y = 109 + 139*(i / 6)` (6 columns, 1920x1080 space).

All 1,972 `cg_th` values are distinct (0..1978); the 7 CG_TH children 335, 337, 911, 1264,
1266, 1269 and 1270 are never referenced [V-all]. Every entry name is found by the NAM search
(allpac0/1/1_2) [V-all, `cg_entries.csv` column full_image]. Contact sheet
`samples/misc_gallery_extras_flowchart/cg_contact_sheet.png`: 20 entries, thumbnail next to the
full picture; each thumbnail is the top-left `w x h` area of the full picture scaled to 231x130
(the full MZPs are tile-padded, e.g. 2550x1270 for 2106x1266) [V-20, viewed].

### 4.3 Unlock test (0x13630) [E]

```
cg_unlocked(entry, category):
    if not category_ok(category): return False                  # 0x1C460
    name = entry.name[1:]
    if alias_seen(name): return True                            # 0x13AB0
    if picture_seen(sprite_index(name)): return True            # 0xA04B0 -> 0x95D60
    return scene_done(557)                                      # 0xB7B50(0x22D): QA_25 finished

sprite_index(n)  = NameTable_Lookup(n, names_sprites)           # -1 for gallery-only pictures
alias_seen(name):
    rec = alias_map.find(name);  if not rec: return False
    for a in rec.alias[0..3]:
        if a is NULL: return False
        i = sprite_index(a)
        if i >= 0 or len(a) < 6: return picture_seen(i)         # first resolvable alias decides
        for L in "ALMS":                                        # size letter at index 4
            if picture_seen(lookup(names_allpac2, a[:4] + L + a[5:])): return True
    return False
```

The map lookup and the alias walk are the code at 0x13B05..0x13C9D; the size-letter retry
writes `L` at `min(len, 5) - 1` of the std::string copy. `picture_seen(-1)` is false
(0x95D60 bounds).

Data consequences [V-all]: 952 entries are found in names_sprites and unlock when their
picture is shown; 105 have an alias (8 of them also direct); **923 entries (the gallery-only
pictures without alias) unlock only through scene_done(557)**, i.e. after QA_25 (the last
"Teach Me, Miss Ciel"), and then only in unlocked categories. All 109 alias names resolve (62
directly, 47 through a size letter).

`cGallery` builds the unlocked list once in its constructor (0x12AB0): for every group g and
entry k that passes 0x13630 it pushes `{entry*, category, g, k, running index, NAM id of name}`
to the vector +0x58 and indexes it by (g, k) in the map +0x78 (0x13A20 looks it up).

### 4.4 Categories (0x1C460, jump table 0x188380) [E, V]

| category | scene_seen | first script with `_RDST(n)` |
|---|---|---|
| 1 | 2 | 02_00_ARC02_1 |
| 2 | 3 | 03_00_ARC03_1 |
| 3 | 4 | 04_00_ARC04_1 |
| 4 | 5 | 05_00_ARC05_1 |
| 5 | 383 | 05_01_CIEL05_3 |
| 6 | 208 | 07_01_CIEL07_1 |
| 7 | 7 | 07_00_ARC07_1 |
| 8 | 8 | 08_00_ARC08_1 |
| 9 | 9 | 09_00_ARC09_1 |
| 10 | 226 | 09_01_CIEL09_1 |
| 11 | 10 | 10_00_ARC10_1 |
| 12 | 11 | 11_00_ARC11_1 |
| 13 | 12 | 12_00_ARC12_1 |
| 14 | 13 | 13_00_ARC13_1 |
| 15 | 275 or 276 | 13_01_01_CIEL13_1 |
| 16 | 14 | 14_00_ARC14_1 |
| 17 | 297 | 14_01_CIEL14_1 |
| 18 | 300 | 15_01A_CIEL15A_1 |
| 19 | 309 | 15_01B_CIEL15B_1 |
| 0, other | always | |

The extractor decodes this table from the code (`_decode_category_flags`).

### 4.5 Views [E]

- Main grid (`cGalleryCGMain`, vtable 0x418210, ctor 0x1C5C0 -> base 0x19F00(this, flag,
  slot 0x51B, -1, 25)): list builder 0x1C620 pushes, for every group with at least one unlocked
  entry, `{first unlocked entry, its index, g, unlocked count}`. Locked groups are not shown.
- Sub grid (`cGalleryCGSub`, vtable 0x418478, ctor 0x1E750(this, g), slot 0x70F, 26): builder
  0x1E7C0 pushes every unlocked entry of group g.
- Photo viewer (`cGalleryCGPhoto`, ctor 0x1C730(this, g, k), created by 0x13370) shows the full
  picture found by the NAM search of `name + 1`.

---

## 5. Flowchart

### 5.1 Column record (RELRO 0x418770 + 32*c, c < 0x115 = 277) [E: 0x214C7..0x21519; V-all]

| off | type | field |
|---:|---|---|
| 0x00 | i32 | column index (== c) |
| 0x04 | i32 | group 0..15; the look-ahead of 5.4 stops at a column with a higher group [H: chapter band] |
| 0x08 | i32 | node count (1..9; 87 columns have 5, 70 have 6, 35 have 1) |
| 0x10 | ptr | node array (text segment, 64-byte records) |
| 0x18 | 8 B | zero |

### 5.2 Node record (64 bytes) [E: 0x21430, 0x22100, 0x29470, 0x22F0B; V-all]

| off | type | field | values |
|---:|---|---|---|
| 0x00 | i32 | scene: > 0 flowchart scene id (lit by scene_done); < 0 marker for scene -n; 0 empty | 495 scene nodes = scenes 1..495 each once; 146 markers (61 ids, 33 of them 498..530 are engine-only pseudo scenes); 645 empty |
| 0x04 | i32 | connector code, -1 none | 3 (331), 5 (166), 9 (96), 6 (81), 11 (34), 8 (33), 7 (22), 4 (12), 10 (1) [H: line shape bits] |
| 0x08 | i32 | link: edge id 565..1194 (edge_seen) or -1 | the 630 link ids are exactly the 630 edge-table ids |
| 0x0C | i32 | script scene started by a replay (`_JUMP` target through ARCHIVE) | 240..790, and 2000..2007 for 8 scenes that start inside a script |
| 0x10 | i32 | chapter label: rect index into 0x1A3EE0 (ja/zc/zt, 200x32) or 0x1A3FD0 (en, 440x30), texture slot 0x4F4 = GALLERY_PARTS_FC child 6 ("0/A Glass Moon" .. "15/Cloudless Blue", index 28 "Prologue") | 0..27 |
| 0x14 | i32 | unknown, never read by the flowchart code | 0..519 |
| 0x18 | i32 | GALLERY_PARTS_FC_TH child | **= scene - 1 for all 495 scene nodes; all 495 children used** |
| 0x1C | i32 | title, ui_text id | 397..887; = 396 + the script's F022 for 491 of 495 |
| 0x20 | i32 | synopsis, ui_text id | 914..1408 |
| 0x24 | 28 B | zero; run-time fields (+0x30 = (column, row) packed, +0x3C state bits 0x80000000 / 0x40000000) | |

Worked example, column 5 and its first node:

```
00418810: 0500 0000 0100 0000 0300 0000 0000 0000   col 5, group 1, 3 nodes
00418820: d0fd 1800 0000 0000 0000 0000 0000 0000   nodes -> 0x18FDD0
0018fdd0: 3000 0000 0500 0000 5d02 0000 4401 0000   scene 48, conn 5, link 605, script scene 324
0018fde0: 0100 0000 0200 0000 2f00 0000 a701 0000   chapter label 1, +0x14 = 2, FC_TH 47, title 423
0018fdf0: ac03 0000 0000 0000 ...                   synopsis 940
```

Node thumbnails: 491 FC_TH children are 430x243, 4 (107, 114, 188, 481) are 430x972 (four
bands) [V-all]. Flowchart node table: `flowchart_nodes.csv` (title and synopsis in English),
contact sheet `flowchart_contact_sheet.png` (24 scene nodes, thumbnail and title; titles and
pictures agree, e.g. scene 183 "Eat in the Classroom", scene 362 "Sitting Down for
Breakfast") [V-24, viewed]. **0 unresolved thumbnails.**

### 5.3 Edge flags (the node link ids) [E]

Two writers set edge_seen:

- `_JUMP` (handler `_J***` 0x961DC -> 0xBA690): `from = 0xB8840(arg0)` (table text 0x228EF0,
  i32 indexed by id - 1 for 1..0x20F), `to = 0xB8870(arg1)` (switch 0x21F084); 0xB9460(from, to)
  handles special transitions with a 500-case switch (jump table 0x220C24; it marks pseudo
  scenes through 0xB85A0 = seen + read + done + trophy check); otherwise, if `to > 0` and
  `from` is 1..0x212, 0xBA3C0(from, to) scans the **edge table** text 0x2218D0, 630 records of
  0x30 bytes `{i32 from, i32 to, i32 g[5], i32 f[5]}` (loop `rax = -0x7620 .. 0`, first match
  wins), ORs `g` into edge_seen and `f` into the per-save F[600..] set (ids <= 0 end the list;
  only record 455 uses the fifth slot, special-cased at 0xBA4F0 / 0xBA638), and stores F[110] =
  to. `g == f` in every record [V-all].
- `_SELD` (0x9B5F3..0x9B60D): after a choice, 0xB8620(choice id 1..82) ORs the three ids of
  text 0x2214E0 + 12*id (-1 = none) into edge_seen; choice ids 57, 67, 72, 73 also mark scenes
  512, 513, 523, 517 seen, read and done and set extra edge bits (0xB86FC..0xB87F6) [H: bad-end
  branches shown as markers -512, -513, -523, -517 in columns 165, 184, 211, 221].

In 380 of the 494 scene nodes that have a link, the first edge record carrying the link id has
`to == scene` [D]: the link is the edge drawn into the node.

### 5.4 Column filter (cFlowchartScene build 0x21430, loop 0x2149E..0x21774) [E, EMU-42]

```
kept = []
for c in 0..276:
    count = col[c].count; rows = max(count, 1)
    vis = diff = 0
    for j in 0..rows-1:
        a, link = node(c, j).scene, node(c, j).link
        if not ((a > 0 and scene_done(a)) or (link > 0 and edge_seen(link))): continue
        vis += 1
        if a > 0: diff += 1; continue
        if a == 0: continue
        # marker for scene -a
        if c == 0 or j > col[c-1].count: diff += 1; continue        # prev = previous static column
        pa = node(c-1, j).scene
        if scene_done(-a):
            if |pa| != |a|: diff += 1; continue
            if c > 0x113: continue
            for k in c+1 .. 276:                                     # look ahead
                if j > col[k].count: diff += 1; break
                if col[k].group > col[c].group: break
                if node(k, j).scene != a or node(k, j).link != link: diff += 1; break
        else:
            diff += (pa + a != 0)
    if (vis >= 2 and diff != 0) or (vis == 1 and (diff == 1 or count == 1)):
        kept.append(c)            # its nodes are copied (0x253D0) and the column pushed (0x254C0)
```

Note the `j > count` comparisons: the engine reads row `j == count` of a neighbouring column,
which is the first record of the next node array (node arrays are contiguous); a port must read
the same bytes (FlowRaw keeps them). Verified by running the engine's loop under unicorn with
the vector pushes stubbed on the empty state, the full state and 40 random states (scene and
edge densities 0..1, prefixes of the scene range): kept columns identical in all 42 [EMU-42;
`misc_gallery_extras_flowchart_emu.compare`]. Examples: full state 277 columns, all edges
without scenes 276, scenes 1..100 only (random) 71.

After the filter the build lays the kept columns out (per row `438 + 96 * row`, 0x21D46; the
column x advance is accumulated from widths 8 / 32 / 56 at 0x217E0..0x21D0C [E, partly read]), adds a
chapter label when `node.chapter_label` increases and the node's edge is lit (0x21A44..0x21B6E),
and sets node state bits via 0x25950 / 0x25F60 / 0x260D0 [E; layout constants only partly read].

### 5.5 Preview and launch [E]

- Selecting a node calls 0x22100 -> 0x29470(fc_th, title_ui, synopsis_ui): FC_TH child into
  slot 0x4FA (+1 alternating), title (ui_text, font size 0x30) and synopsis at fixed
  positions; titles 0x251, 0x254, 0x25E and 0x1EB get a variant layout (0x296A2).
- Confirming calls 0x13570(gallery, node.script_scene, node.scene, screen +0x48) (3.2).
- The in-game flowchart (system menu "View the flowchart", `cGameFlowchart` 0x299D0/0x29AE0,
  UI table 0x66BA80) uses the same scene class; its "rewind to this scene" (ui 1498) goes
  through 0x2A080, which stores F[26], F[110] and a snapshot of selected F values (lists text
  0x1A4250, 0x1A42A0, 0x1A4500) [E; rewind semantics: adv_system_state].

---

## 6. Music player

### 6.1 Record (text 0x1A47D0 + 0x38*i, i < 95; `cmp edi, 0x5E` at 0x2CBAB) [E, V-all]

| off | type | field |
|---:|---|---|
| 0x00 | i32 | BGM track (bgm.hed index), 1..126, 95 distinct |
| 0x04 | i32 | title ui_text id (256..357) |
| 0x08 | i32 | vocals credit ui id or -1 (250 "ReoNa") |
| 0x0C | i32 | lyrics credit ui id or -1 |
| 0x10 | i32 | composition credit ui id (251 Hideyuki Fukusawa, 252 Keita Haga, 253 kegani) |
| 0x14 | i32 | arrangement credit ui id or -1 |
| 0x18 | i32 | length in ms (shown) |
| 0x1C | i32 | loop start sample (48 kHz) = the RIFF `smpl` loop start in 94 of 95 |
| 0x20 | i32 | loop end sample = `smpl` loop end in 88 of 95 |
| 0x24 | 5 x i32 | variant tracks that also unlock the entry (-1 = none); 17 records have 1..4 |

Labels "Vocals:", "Lyrics:", "Comp:", "Arr:" are ui 246..249; 245 "???" is the locked title.

### 6.2 Unlock (0x2CBA0, returns the record or NULL) [E]

```
if bgm_heard(track) or bgm_heard(any variant): unlocked
elif track == 80  (Lifeline -Game size-):  movie_seen bit 0 (op1_arc watched)
elif track == 82  (Lost -Game size-):      scene_seen(0x230 = 560, set in 14_00_ARC14_1)
elif track == 124 (Juvenile -Game size-):  movie_seen bit 1 (op2_ciel watched)
elif track == 126 (Believer -Game size-):  scene_seen(0x232 = 562, set in 15_01B_CIEL15B_1)
when unlocked through a variant or a special rule, 0x87630(track) also sets bgm_heard(track)
```

bgm_heard is set by `_MPLY` (0x87250 at 0x875E2, `bit 0xC80 + track` when track <= 0x207) and
by 0x81E0 when its third argument is true (engine BGM, e.g. 0xC95F) [E].

---

## 7. Movie gallery [E]

`cGalleryMovie` (0x2A970, vtable 0x41AC58) has three items; the player
(`cGalleryMoviePlayer` 0x2B860, play 0x2BCA0) plays `movie_index(item + 4)` (0x2BD72, then
0x5E280 language remap, volume = config byte 0x17DA273 / 100): item 0 = movie 4 (prologue,
always available), 1 = movie 5 (op1_arc) if movie_seen bit 0, 2 = movie 6 (op2_ciel) if bit 1
(0x2B610). GALLERY_PARTS_MOVIE child 0 holds three 230x130 cards in three states (Arcueid,
Ciel, moon) [D; card-to-item order H].

movie_seen bits are set when a movie ends (0x5F6F0, jump table 0x2010B0 by movie id - 4):
movie 5 -> bit 0, 6 -> bit 1, 4 -> bit 2, 35 -> bit 3, 36 -> bit 4, 37 -> bit 5 [E, V].

---

## 8. Ending list (extra scenarios "Teach Me, Miss Ciel") [E, V-all]

Record (text 0x18F810 + 20*i, 25 records):

| off | field |
|---:|---|
| 0x00 | script scene 1000..1024 (QA_01..QA_25) passed to 0x13570 |
| 0x04 | the QA script's `_RDST` id (533..557 = script scene - 467) |
| 0x08 | unlock scene A (scene_done) |
| 0x0C | unlock scene B or -1 (only QA_15: 257 or 250) |
| 0x10 | help-message index 30..54 -> 0x2D2D40 -> ui 1472..1496 `Play "Teach Me, Miss Ciel n"?` (0x69F40 -> 0xC2A70 -> 0xC2A20) |

Layout (GALLERY_PARTS_ENDING): row 0 has three ending cards (child 3 Moon Princess, 4 Midnight
Rainbow, 5 Cloudless Blue; child 6 the locked card) = records 0..2 (QA_12 unlocked by scene 14
= 14_00_ARC14_1, QA_23 by 300 = 15_01A_CIEL15A_1, QA_24 by 309 = 15_01B_CIEL15B_1); rows 1..3
are "BAD END No.01..22" (child 0) = records 3..24 with `index = column + 8*row - 5` (0x1F6E5).
Record 24 (QA_25, BAD END 22 position) has `unlock_a = 557`, but its test is "all other 24
unlocked" (0x1FB80 with esi == 0x15). Selecting an entry first shows its message (0x1F703)
and then calls 0x13570(gallery, script_scene, -1, -1) (0x1F758). The constructor maps an
incoming scene id 1000..1024 back to a cursor position (0x1E9FA, jump table 0x18F6B0).

Bad-end unlock scenes (record -> script with `_RDST(unlock_a)`): QA_01 187 04_00_ARC04_6_1,
QA_02 31 04_00_ARC04_9_2, QA_03 316, QA_04 317, QA_05 92, QA_06 94 (05_00_ARC05_*), QA_07 138,
QA_08 151, QA_09 319, QA_10 170, QA_11 171 (Arcueid route), QA_13 190, QA_14 321, QA_15
257/250, QA_16 328, QA_17 283, QA_18 285, QA_19 288, QA_20 291, QA_21 304, QA_22 308 (Ciel
route); full list in `ending_list.csv`.

Trophy hook 0xB7CC0 (after any scene_done): scenes 533..557 all done -> trophy 0x1D; else, if
F[24] == 0, a scene listed in text 0x221400 (27 x `{scene, -1}`) -> trophy (index + 1)
[E; trophies P3].

---

## 9. Chapter and scene titles (ui_text) [V]

| ui ids | content | key |
|---|---|---|
| 245..255 | music credits ("???", "Vocals:", .., names) | music record +0x08..+0x14 |
| 256..357 | music titles | music record +0x04 |
| 359..395 | chapter names ("Prologue", "00 A Glass Moon", .., "Teach Me, Miss Ciel") | 358 + script F023 (QA: F023 = 37) |
| 397..887 | flowchart scene titles | node +0x1C = 396 + script F022 (save/load list 0x714D0 and system menu 0x6CB80 use `F022 + 0x18C`) |
| 888..912 | "Teach Me, Miss Ciel 1..25" | 396 + F022 (QACALL F022 = 492..516) |
| 914..1408 | flowchart synopses | node +0x20 |
| 1472..1496 | ending-list confirmation messages | help-message table |
| 1497 | "You cannot use the flowchart yet." | gallery menu |

---

## 10. Dead and legacy gallery data [E]

| data | readers | status |
|---|---|---|
| `extra_parts.mrg` table data 0x78B2C0 (15 records, slots 1247..) | 0x62200, 0x624C0, 0x656D0, 0x66380 | only reachable from title actions 5/6, never returned (3.1); the archive is absent on disk |
| legacy CG list: RELRO 0x41C530 (444 list pointers, pages of 20) -> data 0x78C810.. 56-byte records `{char* name, i32 w, i32 h, f32 sx, f32 sy, 32 zero bytes}`, 1,314 records | 0x62830 (menu modes 1..9), 0x656D0 | dead (menu modes 1..9 are entered only from 0x656D0) |
| legacy music table text 0x209510, 55 x `{bgm, page, position, group}` | 0x64F90, 0x66080, 0x66190, 0x66450, 0x66680 (menu modes 10..14) | dead (entered only from 0x66380); the live music player is 6 |
| `names_bgm` data 0x7A8820 (LLBGM01..19, LL_INS, LL_OP, LL_ED; 32-byte records) | lookup 0xA3A40 scans 0x6F records | 0xA3A40 has no caller [E] |
| SDTA0414..SDTA0518, SDTB0405..SDTB0524 (63 names, data 0x869F80) | none | no code reference; the mask lookup 0xA3530 scans 96 records from 0x869B30 and so reaches this area, but 16 bytes out of phase (names start at +0x10 of its records), so no name can match [E] |
| `ed1_parts` tables (5), `roll_cg_*`, `eye_*` | none | archives_ui_movies.md 7.2 |
| title actions 10..31 -> scene 9000..9021 | 0x69182 | never returned by the title (3.1) |

Menu modes (0xDF2A50, dispatcher 0x62790): 1..9 -> 0x62830 (legacy CG), 10..14 -> 0x63EB0
(legacy music), 15..16 -> Gallery_Update. 15 is set by 0x67410 (title action 7), 16 by 0x673D0
(called from 0x68F40) [E].

---

## 11. Python API

```python
from tsuki import misc_gallery_extras_flowchart as gx
d = gx.load()                       # GalleryData: cg_groups, cg_aliases, cg_special, category_flags,
                                    # seen_map, columns, music, extras, choice_flags, edges, ...
s = gx.UnlockState()                # or UnlockState.from_system_block(block_0x18AC)
u = gx.Unlocks(d, s)
u.cg_unlocked(d.cg_entries[0]); u.cg_main_tiles(); u.music_unlocked(d.music[0])
u.extra_unlocked(24); u.movie_unlocked(2); u.seen_index(picture_id)
raw = gx.FlowRaw.from_eboot(eboot_path); gx.flow_filter(raw, s)    # kept column indices
```

CLI: `python -m tsuki.misc_gallery_extras_flowchart summary | export [--out DIR] [--sheets]`;
`python -m tsuki.misc_gallery_extras_flowchart_emu [--trials N]` (engine vs model).

Samples (`samples/misc_gallery_extras_flowchart/`): cg_groups.csv (458), cg_entries.csv (1,972;
prefix, CG_TH child, rect, flag, category, alias, names_sprites index, seen index, archive of
the full picture), cg_aliases.csv (105), seen_map.csv (2,144), flowchart_columns.csv (277),
flowchart_nodes.csv (1,286, with English title and synopsis), flowchart_edges.csv (630),
choice_flags.csv (82), music_room.csv (95), ending_list.csv (25), legacy_music_0x209510.csv,
legacy_cg_0x78C810.csv, misc_names.csv (SDT, names_bgm, four-frame CG names),
cg_contact_sheet.png, flowchart_contact_sheet.png.

---

## 12. Verification

- All tables extracted with layouts read from their reader code; record counts from loop bounds
  (0x1C530, 0x1C540, 0x214B3, 0x2CBAB, 0xBA3C0) or terminators [E]; pointers checked against
  the R_X86_64_RELATIVE relocations (a non-relocated non-zero slot raises) [V-all].
- Category flags decoded from the jump-table cases with capstone (test_cg_lists).
- CG: unique thumbnail children, 7 unused, the 11 four-frame thumbnails (test reads all 1,979
  CG_TH headers), unlock classes 952 / 105 / 923, model checks (nothing unlocked on an empty
  state, everything with scene 557 + category flags, picture and size-letter alias unlocks).
- Flowchart: 495 scene nodes = scenes 1..495, FC_TH child = scene - 1, all 495 children used;
  link ids = edge ids; column filter vs engine under unicorn on 42 states (test runs 12).
- Music loop points compared with the RIFF `smpl` chunks of bgm.mrg (94 / 88 of 95).
- Movie bits decoded from the jump table 0x2010B0; music special scenes from the immediates at
  0x2CC3D / 0x2CC58.
- Contact sheets viewed: 20 CG thumbnails match their full pictures; 24 flowchart thumbnails
  match their scene titles.

---

## 13. Open questions

1. Node +0x14 (0..519) and the connector codes +0x04: not read by the flowchart code found;
   the line drawing (0x25950 / 0x25F60 / 0x260D0) is not decoded.
2. Column group +0x04 meaning beyond "look-ahead boundary" [H: chapter band].
3. CG entry x/y and the +0x1C flag: exact viewer behaviour (0x1C730 class) [H].
4. `_RDAS` / 0x61F80 (skip offer for completed scenes) and config +0xA0: adv_system_state.
5. Which scripts reach which `_JUMP` special case in 0xB9460 (500-case switch) and the id maps
   0x228EF0 / 0x21F084: needed only to reproduce pseudo scenes 498..530 exactly; the generic edge
   table is decoded.
6. Title state 0x19 (gallery from the title without action 7) and menu mode 16 (0x673D0): who
   enters them [H: unused].
7. Card order of the movie gallery (which card is item 0).
8. Which help-line ids the gallery menu shows (0xCFE20 / 0xCFE30 use a 0x78-byte record table at
   0x178F070) and whether ui 226 "Listen to voices." is referenced at all.

---

## 14. Corrections to other documents

### Errata for archives_ui_movies.md (7.6, 7.2, 12)

- 7.6 swaps the lists. **relro 0x404170** is not the flowchart: it is the CG **alias** table, 105
  records of 0x28 bytes (`key + 4 aliases`, count `0x1C540: mov eax, 0x69`), followed by the 11
  four-frame names at 0x4051E0 and the 1,972 CG entry records (32 bytes) of the 458 CG groups
  whose table is relro **0x400830** (count 0x1C530). The range 0x404170..0x4181C0 therefore holds
  aliases + CG entries, not "2197 pointers in 0x28-byte records". The flowchart is relro
  **0x418770** (277 columns) with 64-byte node arrays at text 0x18FC90.. (section 5).
- 7.6 "CG gallery: 1314 IMGnnnn.MZP ... data 0x78C810 ... read by 0x656D0 [H: the CG list]":
  that is the legacy list of the dead extra screen (section 10); the live CG list is 0x400830.
- 7.2 / 12 open question 7 ("whether the extra_parts cases can be reached"): settled, they
  cannot (3.1).

### Errata for engine.md (7.3, 5.5, 13)

- 7.3 +0x052C (0x20C, RAM 0x164D5C0) "unknown": picture_seen bits 0..3199 and bgm_heard bits
  3200..3719 (2.1); the whole 0x20C bytes are ORed on load (0x200 with `vorps` and +0x200..
  +0x20B bytewise), not "ORs 0x200 and reads +0x72C..+0x737".
- 7.3 +0x16F4 (0x118, RAM 0x164FBC0) "unknown": `choice_taken[choice id]`, bit = chosen option
  (set 0xA6647, read by `_SELD` 0x9B626); 0x110 bytes ORed; +0x1804.. are config copies.
- 7.3: G is replaced on load (the OR index list 0x86AA14 is empty).
- 5.5 / FORMATS 5.4 "names_sprites (0x7A8B10) ... unreachable fallback / unused": the gallery
  uses it (0xA04B0) for every CG name and alias; it equals the first 9,958 names of
  names_allpac2.
- 13 item 13: SDTA/SDTB confirmed unreferenced; not reachable through the mask lookup either
  (section 10).

### Errata for audio.md (music mode)

- "Music mode (sound gallery) track table text VA 0x209510, 55 records ... 0x664C0": this is the
  legacy table of the dead extra screen. The music player is text **0x1A47D0**, 95 records of
  0x38 bytes (section 6), whose +0x1C/+0x20 are the BGM loop points.

### Errata for FORMATS.md (5.4, 7 P2 item 14)

- Row "gallery / flowchart lists | data 0x78C810; relro 0x404170..0x4181C0": replace with CG
  groups 0x400830 (458), CG entries 0x405240.. (1,972), aliases 0x404170 (105), flowchart
  0x418770 (277 columns, 1,286 nodes), edges 0x2218D0 (630), music player 0x1A47D0 (95),
  ending list 0x18F810 (25). Row "music-mode table 0x209510": legacy, dead.
- Row "second name list 0x7A8B10 ... unreachable fallback / unused command": used by the gallery.

### Errata for images.md (8, picture-seen bit)

- Detail for the "picture seen" sentence: the bit array is 3,200 bits; ids >= 0xA8D are mapped
  through the 2,144-record table data 0x86AA30 (seen indices 2701..2917), ids below are their own
  bit, and `_HH_VV` variants first resolve to their base picture (2.2).
