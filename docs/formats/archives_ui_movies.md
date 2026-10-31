# Archive roles, UI data, loose files and movies

Scope: what every archive, archive entry, loose file and movie of TSUKIHIME -A piece of
blue glass moon- (PS4 CUSA47999 v01.01) is **for**, how the engine finds it (both the
archive-level name search and the script-level image lookup), and the non-pixel UI data
that exists as data. Containers (HED/NAM/MRG, mrgd00, MZX) are in `containers.md`. Pixel
formats, script bytecode, text and audio codecs are in the documents of those agents.

Reference code: `tools/tsuki/catalog.py` (classification, engine lookup rules, script image
resolution, movie selection, export CLI) and `tools/tsuki/catalog_eboot.py` (read-only
extraction of the engine tables from `eboot.bin`). Tables generated from the real data are
in `samples/ui_misc/` (section 13). Section 14 lists what changed after the independent
review and the proofs.

Confidence labels:

- **[V-all]** verified on every item of the retail data (count given); **[V-n]** on n items.
- **[E]** read from the game's own code. VAs are eboot.bin virtual addresses:
  text VA = file offset - 0x2280, relro VA 0x400000 = file 0x32C210, data VA 0x600000 = file 0x365FA0.
- **[VIS]** confirmed by looking at rendered images (contact sheets in `samples/ui_misc/previews/`).
- **[H]** hypothesis.

---

## 1. Summary

| file(s) | entries | what it is | engine use |
|---|---:|---|---|
| allpac0 | 2449 | full-size images that ship in the initial PlayGo chunk [H: the early story]: 378 scene images, 854 scene variants, 652 sprites, 241 sprite bodies, 315 sprite variants, 7 solid colours, ANIME_TOP, DUMMY | archive id 0, always open, searched **last** |
| allpac1 | 9968 | the bulk of the full-size images: 2289 scenes, 2627 scene variants, 1315 sprites, 249 bodies, 3488 sprite variants | id 1, opened only when PlayGo chunks 1 and 2 are installed |
| allpac1_2 | 4317 | continuation of allpac1: 2783 sprites, 508 bodies, plus 1024 gallery-only images IMG3000..IMG4193 | id 2, same PlayGo gate, searched before allpac1 |
| allpac2 | 14745 | **reduced copies (scale 1/12.8)** of every scene, scene variant and sprite (paired sprites composited with their body); entry k = engine image record k | id 3, never searched by name; loaded as `0x30000 | k` by the save-slot layer rebuild (4.1) |
| allpac3 | 136 | 69 per-language/neutral **UI texture sets** (nested mrgd00), 2 gallery thumbnail sets, 49 transition rules, 12 caution screens, 4 placeholders | id 4, searched second |
| allpac4 | 37 | late additions: IMG6000..IMG6026, IMG2637/2638 (+variants), IMG5028..5030, IMG0570_09_09, IMG2607_01_01, DUMMY | id 5, searched **first** |
| parts | 24 | system screens shown outside the scenario (logo, save-data warnings, autosave notice/icon, "Saving..." captions, disclaimer) | slot 0x18, addressed by index |
| data/if_{ja,en,zc,zt}.png | 4 | 228x128 **save-data icon** | path built at VA 0x41AD0, written with sceSaveDataSaveIcon |
| image/corp.png | 1 | 131x48 TYPE-MOON logo, **screenshot watermark** | sceScreenShotSetOverlayImageWithOrigin |
| image/movie/*.mp4 | 134 | FMVs, H.264 1920x1080 | movie table at data VA 0xDC8C70, language remap VA 0x5E280 |

Everything in allpac0..4 and parts is an image MZP or a container of image MZPs: **there
is no layout, button or animation data in any archive** [V-all: 31,605 HED entries, 3,699
nested children; every one is `img-header + MZX tiles`]. The data that ties images
together (image table, script-name list, UI registration tables, movie table) lives in
eboot.bin data (sections 2.4, 4, 7, 9); per-screen UI layout is code (7.4, 7.5).

Two different name lookups exist and must not be confused:

| lookup | input | key | table | result |
|---|---|---|---|---|
| NAM search, VA 0xC3270 (2.3) | a **file name with extension** from the eboot (`BLACK.mzp`, `AKI_A_00_00_N00_B.MZP`, `conf_parts_en.mrg`) | upper-cased, 28 bytes | allpac4/3/1_2/1/0 `.nam` | packed id `archive << 16 | index` |
| script image lookup, VA 0x78410 (2.4) | a **script image id** (`img2511`, `WHITE`, `boy_a_00_00_n01`) | upper-cased up to the first `.`, 32 bytes | name list at data VA 0x7F6800 | image-table index k (then record k's `file`/`overlay` go through the NAM search) |

---

## 2. Image lookup (engine)

### 2.1 Archive ids and slots [E]

| archive id | archive | HED/MRG slot | NAM slot | opened at boot |
|---:|---|---:|---:|---|
| 0 | allpac0 | 0x0A | 4 | yes |
| 1 | allpac1 | 0x0B | 5 | only if PlayGo chunks 1 and 2 are present |
| 2 | allpac1_2 | 0x0C | 6 | same as allpac1 |
| 3 | allpac2 | 0x0D | 7 | yes |
| 4 | allpac3 | 0x0E | 8 | yes |
| 5 | allpac4 | 0x0F | 9 | yes |

Open sequence: VA 0xD1F40 loads all six `.nam` files (slots 4..9), then opens
`allpac0.hed/.mrg`, then calls VA 0xEED80. That function calls
`scePlayGoGetLocus(handle, {1, 2}, 2, loci)` and returns 1 when either chunk is still
`NOT_DOWNLOADED`. Only when it returns 0 are `allpac1` and `allpac1_2` opened, and the flag
byte at data VA 0x17DA260 is set. The helpers VA 0xD1C10, 0xD1C70 and 0xD1CD0 repeat the
check later and open the two archives once the chunks have arrived. `sce_sys/playgo-chunk.dat`
defines three chunks: `Chunk #0`, `alldata` and `alldata2`. Which of the two later chunks
holds which archive is [H]. A PC port has all files and can treat allpac1/1_2 as always
open.

**Why allpac1_2 exists** [E+V]: the HED8 bank table stores at most 11 bank switches, so an
archive can be at most 12 x 2 GiB = 24 GiB. allpac1 already uses all 11 switches
(24,257,312,768 bytes), so the remaining images went into a second archive. Its contents
(bodies and expression sprites in allpac1's numbering ranges, plus the gallery-only IMGs)
confirm that it is an overflow of allpac1, not a separate category.

### 2.2 Packed image id

`id = archive_id << 16 | nam_index` [E: VA 0xC3270 returns it; VA 0xC3520 and 0xC31F0
take it apart with `bextr eax, edi, 0x410` (4 bits at bit 16) and `and 0xFFF0FFFF`].
Ids below 0x10000 are archive 0.

### 2.3 NAM file-name search (VA 0xC3270) [E, V-all]

The input is a **file name including its extension**. Exact algorithm (VA 0xC3280..0xC3508):

```
# 1. start after the last backslash, scanning at most 256 bytes for the NUL (C3280..C32B9)
start = 0
for i in 0 .. 255:
    if name[i] == 0: break
    if name[i] == '\\': start = i + 1
# 2. copy EXACTLY 28 bytes (the copy runs past the NUL; those bytes never matter) (C32BB..C32F7)
prev = 0
for j in 0 .. 27:
    c = name[start + j]
    out = c - 0x20 if 'a' <= c <= 'z' and prev < 0x80 else c   # no upper-casing after a byte >= 0x80
    key[j] = out; prev = out                                   # (Shift-JIS lead byte: test al,al; cmovs)
key[28] = 0
# 3. search (C3303..C3508)
for (archive, nam_slot) in [(5, 9), (4, 8), (2, 6)*, (1, 5)*, (0, 4)]:   # * only if allpac1 is open (VA 0xD1CD0)
    for i in 0 .. 0x5013:                      # at most 20,500 records
        rec = nam[i]                            # 32-byte NAM record
        if rec[0] == 0: break                   # end of this table
        if strncmp(rec, key, 28) == 0:          # the NUL must match too
            return archive << 16 | i
return -1
```

Because the NUL takes part in the compare, `IMG2511` does **not** match `IMG2511.MZP`:
script ids never go through this search directly (2.4). The names fed to it come from the
eboot: image-table `file`/`overlay` strings (`img2511.MZP`, `AKI_A_00_00_N00_B.MZP`), the
localised UI archive names (`conf_parts_en.mrg`, 7.3) and literals such as `mask_top.mzp`.

The search order is therefore **allpac4, allpac3, allpac1_2, allpac1, allpac0**. allpac2 is
never searched by name. Measured over all names [V-all]: every name resolves to its own
entry except the three duplicate `DUMMY.MRG` copies in allpac3, allpac1_2 and allpac0, which
are shadowed by allpac4's copy. All copies are byte-identical, so this has no effect.
`tsuki.catalog.find_image` (reference port), `ImageNameIndex` (dict version) and `nam_key`
implement this; every retail name is ASCII, so the Shift-JIS rule never fires on them.

### 2.4 Script image ids -> image-table record -> archive entries [E, V-all]

Every image argument of `_STCH`, `_SCH2`, `_SPFC`, `_SPRD` and `_STCG` is resolved in three
steps.

**Step 1, key (NameTable_Lookup, VA 0x78410; all 30 call sites pass ecx = 1 = upper-case):**

```
key = ""
for c in name[0 .. 31]:                 # at most 32 bytes
    if c == 0 or c == '.': break
    key += c if c >= 0x80 else toupper(c)   # C-library toupper table (C locale: a..z only)
```

**Step 2, name list (data VA 0x7F6800).** A `std::map<string, u16>` built lazily (VA 0x780D0)
from an embedded list of 32-byte records: NUL-padded upper-case name **without extension**,
bytes 30..31 = `0D 0A`, list ended by a byte 0xFF. It holds 14,744 names plus one empty
record (index 14,744) [V-all]. Name k equals the logical name of image-table record k
without its extension, for 14,744/14,744 records, so **k is the image-table index** (and the
allpac2 index). Not found -> 0xFFFF (-1).

```
0080a2c0 (file 570260): 494d 4732 3531 3100 0000 0000 0000 0000   "IMG2511"   record 2518
0080a2d0 (file 570270): 0000 0000 0000 0000 0000 0000 0000 0d0a
00851c00 (file 5b7ba0): 424f 595f 415f 3030 5f30 305f 4e30 3100   "BOY_A_00_00_N01"   record 11680
00869b00 (file 5cfaa0): 0000 ... 0000 (32 bytes)                    empty record 14744
00869b20 (file 5cfac0): ffff ffff ...                               end of list
```

A second list at data VA 0x7A8B10 (9,958 names + an empty record) is identical to the first
9,958 entries. It is only used by an unreachable fallback (the call sites test the result
with `cmp ax, -2; jg skip`, so the `BG_ERROR` lookup runs only for results <= -2, which never
occur; `BG_ERROR` is in neither list) and by the lookups at VA 0x9BBD3 (in the `_PLDB`
handler, a command no script uses) and VA 0xA04C7.

**Step 3, record -> files.** Image-table record k (section 4) is drawn as `file`, then
`overlay` on top if set. VA 0x4A060(k) resolves `file` with the NAM search (2.3) and caches
the packed id at record +0x20; VA 0x4A110(k) does the same for `overlay` at +0x24. Both
names carry an extension, so the NAM search matches.

**STCH/SCH2 variant suffix (VA 0x81760, shared by both commands):**

```
layer = a0 (must be <= 39);  name = a1
aa = bb = 0
if a18 present and a18 <= 39:            # "take over layer a18"
    cur = name_list[layer_state[a18].image]          # name of the image now on layer a18
    if len(cur) >= 7 and cur[-6] == '_' and cur[-3] == '_' and (cur starts with "IMG" or len(cur) >= 15):
        aa, bb = int(cur[-5:-3]), int(cur[-2:])
    (the layer's other state is also copied from layer a18, VA 0x81AF6..0x81C1F)
if a9 present:  aa = a9
if a10 present: bb = a10
if aa | bb != 0:
    name += "_%02d_%02d" % (aa % 100, bb % 100)      # digits written only for values > 0
k = lookup(name)                                      # steps 1..3; the layer shows record k only
```

So `_STCH(4,img1696,240,136,1000,255,1910,,CC,11,11,)` shows **IMG1696_11_11 instead of
IMG1696**, and `_STCH(8,img0342,...,CC,0,0,)` shows plain IMG0342. The `_aa_bb` images are
complete alternative renderings of their base, not patches: their logical size is equal or
slightly larger than the base in 7,288/7,288 records (e.g. aki_a_00_03_n04_30_30 1798x6453
vs AKI_A_00_03_N04 1702x6357) [V-all], and `previews/repair_variants.png` shows
IMG1696/IMG1696_11_11 (same window scene), IMG0342/IMG0342_04_04 (same classroom, some
semi-transparent pixels), AKI_A_00_03_N04 and its `_30_30` variant (the same sprite,
blurred) [VIS]. The same rule covers scene variants (`IMGnnnn_aa_bb`) and sprite variants
(`CCC_..._Pnn_aa_bb`).

**Census with this rule** (`scratch/ui_misc/script_lookup.py` over the 549 decoded scripts;
`samples/ui_misc/script_image_census.json`) [V-all]:

| command | calls | static name | resolves | missing | name depends on a18 (run time) |
|---|---:|---:|---:|---:|---:|
| `_STCH` | 54,494 | 54,310 | 54,306 | 4 | 184 (base name resolves in 184/184) |
| `_SCH2` | 4,675 | 190 | 190 | 0 | 4,485 (base resolves in 4,485/4,485) |
| `_SPFC` | 487 | 486 | 486 | 0 | (1 call `_SPFC(26,26,...)` has no image argument) |
| `_SPRD` | 36 | 36 | 36 | 0 | |
| `_STCG` | 39 | 39 | 39 | 0 | |

- Of the 54,310 static `_STCH` names, 8,927 carry a suffix (6,811 scene variants, 2,116
  sprite variants) and 45,383 do not (40,395 IMG, 4,925 sprites, 63 colours).
- The 4 missing names are script bugs: `IMG5007_02_02`, `IMG5007_00_27`, `IMG1637_28_28`
  and `Z20_M_61_00_A00_10_00`. The engine gets index -1 for them [E] and presumably shows
  nothing [H].
- 3,816 of the resolved `_STCH` calls draw a paired sprite (body + face); 50,490 draw a
  single image.
- IMG3000..IMG4193 are not in the name list (0 of 1,024), so scripts cannot display them at
  all; they are used only by the gallery code (7.6).

`tsuki.catalog.ScriptImageResolver` (`resolve(name, aa, bb)`), `script_image_key` and
`stch_image_name` are the reference port; `python -m tsuki.catalog image img2511
boy_a_00_00_n01` prints the record and packed ids. Example results [V]:

| script id | k | file -> id | overlay -> id |
|---|---:|---|---|
| `img2511` | 2518 | `img2511.MZP` -> allpac1 #2157 | - |
| `WHITE` | 5 | `WHITE.mzp` -> allpac0 #5 | - |
| `boy_a_00_00_n01` | 11680 | `BOY_A_00_00_N00_B.MZP` -> allpac0 #1838 | `BOY_A_00_00_N01.MZP` -> allpac0 #1840 |
| `AKI_A_00_04_N01` (standalone) | 9981 | `AKI_A_00_04_N01.MZP` -> allpac1 #8424 | - |
| `img1696` with a9 = a10 = 11 | 6218 | `img1696_11_11.MZP` -> allpac0 #1121 | - |
| `NOE_L_60_03_N01` | 12536 | `NOE_L_60_03_N00_B.MZP` -> **-1 (not on disk)** | `NOE_L_60_03_N01.MZP` -> allpac1_2 #1082 |

An unrelated by-name search over image-table field +0x00 exists at VA 0x49D50, but it is
dead code: it has no call, jump or pointer reference, and field +0x00 is 0 in all 14,775
slots with no relocation targeting it (section 14).

### 2.5 Consequences for a port

- Scripts name images without extension; resolve them through the name list and the image
  table, never directly against the NAM files. A port can ship
  `samples/ui_misc/eboot_image_table.csv` (columns `script_name`, `file`, `overlay`) or read
  the two tables from the user's eboot.bin with `tsuki.catalog_eboot`.
- Both keys are upper-cased (a..z only), so names are effectively case-insensitive.
- allpac4 overrides everything in the NAM search, so it can serve as a patch archive. Today
  its names do not collide with the other archives (only DUMMY.MRG does).
- While allpac1/allpac1_2 are closed, VA 0xC31F0 returns a dummy NAM record for ids of
  archives 1 and 2.

---

## 3. Image name grammar and categories

The reference classifier is `tsuki.catalog.classify_name`. Its counts below cover all
31,652 allpac names plus allpac3's 3,699 children [V-all, `samples/ui_misc/category_summary.csv`].
0 names fall into `unknown`.

| category | pattern | example | count (full-size / allpac2) |
|---|---|---|---|
| solid_color | BLACK, BLUE, GREEN, ORANGE, RED, WHITE, YELLOW | `BLACK.MZP` (1920x1080) | 7 / 7 |
| scene | `IMGnnnn` (nnnn not in 3000..4193) | `IMG0042.MZP` | 2699 / 2699 |
| scene_layer (variant) | `IMGnnnn_aa_bb` | `IMG0042_03_03.MZP` | 3485 / 3485 |
| gallery_image | `IMG3000`..`IMG4193` | `IMG3864.MZP` | 1024 / 0 |
| sprite | `CCC_S_XX_YY_Pnn` | `AKI_A_00_00_N01.MZP` | 4750 / 4750 |
| sprite_body | `CCC_S_XX_YY_Pnn_B` | `AKI_A_00_00_N00_B.MZP` | 998 / 0 |
| sprite_layer (variant) | `CCC_S_XX_YY_Pnn_aa_bb` | `SIE_A_00_00_N00_02_02.MZP` | 3803 / 3803 |
| ui_parts / ui_thumbnails | `*_PARTS[_lang].MRG`, `GALLERY_PARTS_{CG,FC}_TH.MRG` | `CONF_PARTS_EN.MRG` | 69 + 2 |
| transition_rule | `RULE_nn` | `RULE_07.MZP` | 49 |
| caution_screen | `CAUTION_{JA,EN,ZC,ZT}`, `CAUTION1[_EN|_ZC|_ZT]`, `CAUTION2[_EN|_ZC|_ZT]` | `CAUTION1_ZT.MZP` | 12 |
| placeholder | `DUMMY.MRG`, `CGMODE_TOP.MRG` (16x16, 128 bytes); `ANIME_TOP`, `CLOCKSTAND_TOP`, `SELMAP_TOP`, `MASK_TOP` .MZP (128x96 white, 1216 bytes) | | 10 entries (5 are DUMMY.MRG copies) |

Caution names [V]: the plain disclaimer always carries a language suffix (`CAUTION_JA`,
allpac3 #71, plus `_EN/_ZC/_ZT` #72..#74); only `CAUTION1` (#75) and `CAUTION2` (#79) use
the suffix-less name for Japanese. The eboot strings match (`caution_ja.mzp`,
`caution1.mzp`, `caution2.mzp`, ...).

Grammar details:

- `CCC` is the character code (table 3.1). Extras use `Z01, Z02, Z11, Z12, Z20` and `Y00`.
- `S` is the size/framing class `A`, `L`, `M` or `S`. The eboot stores it as 0..3 (image
  record +0x42) [E+V-4750]. What each letter means visually is [H]: A and L are full
  bodies at different scales and S is small (see `previews/a2_samples.png`).
- `XX_YY` is the costume/pose group (2 digits each).
- `P` is the variant letter (N in almost all names; A, B and C also occur). `nn` is the
  expression number.
- `_aa_bb` marks a variant selected by STCH/SCH2 arguments a9/a10 (2.4); `_B` a body.

### 3.1 Character codes

| code | char_id [E] | name [H] | | code | char_id | name [H] |
|---|---:|---|---|---|---:|---|
| SIK | 1 | Shiki Tohno | | ARA | 14 | Professor Arach |
| ARU | 2 | Arcueid Brunestud | | AND | 15 | Ando |
| SIE | 3 | Ciel | | KAR | 16 | Karius |
| NOE | 4 | Noel | | GOT | 17 | Gouto Saiki |
| ROA | 5 | Roa | | SAT | 18 | Satsuki Yumizuka |
| HIS | 6 | Hisui | | MIO | 21 | Mio |
| KOH | 7 | Kohaku | | KUR | 23 | ? |
| AKI | 8 | Akiha Tohno | | KRO | 24 | ? |
| ARI | 9 | Arihiko Inui | | Z01/Z02/Z11/Z12/Z20 | 25..29 | extras |
| MAR | 10 | Mario | | Y00 | 30 | extra |
| VRV | 11 | Vlov | | BOY | 13 | minor character |
| AOK | 12 | Aoko Aozaki | | | | |

The char_id comes from image-table +0x40. It is set on all 4,750 sprite records and matches
the code letters in 4,750/4,750, as does size_class (+0x42) with the size letter [V-all].
Sprite variants carry char_id 0, and one scene, img5028 (record 14711), carries char_id 3 /
size class 2. The names are matched from the code letters and the voice-settings name sheet
`CONF_PARTS` child 13 [H].

### 3.2 Scenes and how scripts use them [V]

- All 549 decoded scripts (allscr.mrg, inverted MZX) reference images only through five
  commands: `_STCH` (54,494 calls, arg 1 = id), `_SCH2` (4,675), `_SPFC` (487), `_SPRD`
  (36) and `_STCG` (39, arg 0). Resolution: section 2.4.
- Backgrounds and event CGs are **not distinguished** by name or command: both are
  `IMGnnnn` and both go through `_STCH`. The CG gallery lists (section 7.6) are the only
  place where "CG" is defined.
- Script coordinates are in a 480x272 space, for example `_STCH(1,WHITE,240,136,...)` is
  centred. The engine has a 480x272 canvas mode (section 7.5).
- `IMG3000..IMG4193` (1024 images, allpac1_2 only) are not in the script-name list and have
  no allpac2 copy, but appear in the eboot gallery and flowchart lists (645 of the 1314
  names of the CG list and 1020 of 2033 in the flowchart list). They are gallery-only
  versions [V counts, H purpose].

### 3.3 Sprites: paired (body + face) or standalone [V-all, E]

The image-table record of a sprite decides how it is drawn: **draw `file`, then `overlay`
if it is set** (2.4 step 3).

- **Paired, 3,529 records**: `file = CCC_..._Pnn_B.MZP` (body), `overlay = CCC_..._Pmm.MZP`
  (face). In every one the body ends in `_B` and shares the `CCC_S_XX_YY` prefix with the
  overlay. The face is smaller than the body in 3,504 of the 3,526 pairs whose two files
  exist (for example AKI body 2048x6656, face 1280x1024) and the same size in 22.
- **Standalone, 1,221 records**: `overlay` is NULL and `file` is the sprite itself, a
  complete body-sized image (e.g. AKI_A_00_04_N01, allpac1 #8424, 2048x6656, `previews/
  repair_variants.png`: full figure, hands on hips) [V-all, VIS]. For 33 of them a file
  named `<sprite>_B` exists on disk but the table does not pair it (e.g. ARU_A_00_01_N00,
  ARU_A_00_04_N01, ARU_A_80_00_N00); the engine never draws those bodies for these records.
- No record of any other category has an overlay [V-all].
- Scripts reference the record's logical name (= overlay for paired, file for standalone),
  for example `_SCH2(34,boy_a_00_00_n01,...)`.
- Face placement: the face is drawn at the same origin as the body; both images are
  pre-positioned on one canvas [V-3, VIS]. Check (`scratch/ui_misc/composite_check.py`,
  decoding with `tsuki.image`): body + face composited at (0, 0), cropped to the table's
  w x h and box-downscaled to the allpac2 size differs from the allpac2 copy by a mean
  absolute error of 1.45 (SIE_A_00_00_N01), 0.88 (AKI_A_30_01_N01) and 5.01 (the tiny
  32x58 BOY_A_00_00_N01) on premultiplied 0..255 RGBA, against 5.3..18.3 for the body alone
  and 42..107 for the face alone; the standalone AKI_A_00_04_N01 matches with 0.96.
  `previews/composite_vs_allpac2.png` shows each composite next to the allpac2 copy: the
  pairs are indistinguishable. (The independent reviewer reported 0.8..1.6 on 5 other
  pairs, and the image agent's `scratch/images/aru_comp.png` agrees.)
- allpac2 holds every sprite under its logical name, paired ones pre-composited, and no
  `_B` entries [V-all].
- 3 paired records point to bodies that exist in no archive: `ARU_M_03_35_N00_B`,
  `ARU_S_03_35_N00_B` and `NOE_L_60_03_N00_B` [V]. VA 0x4A060 then caches -1 and only the
  face can be drawn [E for the -1; H for what is shown]. For NOE the face file is
  body-sized (1792x2816), so the sprite still looks complete.
- Sprite variants `CCC_..._aa_bb` (3,803, layer_kind 1, never paired) are selected by
  STCH/SCH2 a9/a10 like scene variants (2.4).

---

## 4. Engine image table (data VA 0x671900) and allpac2

Capacity 14,775 slots of 0x48 bytes at data VA 0x671900 (file 0x3D78A0) [E: the
size-class getter VA 0x41BA0 bounds-checks `cmp edi, 0x39B6`; VA 0x4A060/0x4A110 use a
looser bound 0x59D7]. Slots 0..14,743 are used, 14,744..14,774 are empty (file = 0, both id
caches -1, +0x3C = 0xFFFFFFFF), and a different structure starts at slot 14,775 [V-all].
**Record k describes allpac2 entry k**: its logical name (`overlay` if set, else `file`)
equals `allpac2.nam[k]` for 14,744/14,744 records; allpac2 has one more entry (DUMMY.MRG at
14744) [V-all]. Record k is also script-name list entry k (2.4).

Offsets are the engine's own (every code reference, 56 rip-relative operands, addresses 0x671900):

| off | size | type | field | meaning |
|---:|---:|---|---|---|
| 0x00 | 8 | char* | name | 0 in every slot; read only by the dead VA 0x49D50 |
| 0x08 | 8 | char* | file | drawn first: scene, variant, standalone sprite, or the `_B` body of a paired sprite |
| 0x10 | 8 | char* | overlay | face drawn over `file` (paired sprites only), else 0 (3,529 non-zero) |
| 0x18 | 8 | | | 0 |
| 0x20 | 4 | i32 | file id | -1 in the file; cache of `find(file)` written by VA 0x4A060 |
| 0x24 | 4 | i32 | overlay id | -1 in the file; cache written by VA 0x4A110 |
| 0x28 | 4 | u32 | | 0 |
| 0x2C | 2+2 | u16,u16 | a, b | `a = floor(w*640/1920) = floor(w/3)`, `b = floor(h*362/1080)` [V-all 14,744]: the size in the 640x362.667 canvas (7.5) |
| 0x30 | 2+2 | u16,u16 | w, h | logical full-size width/height (BLACK 1920x1080; AKI_A_00_00 body 1828x6357) |
| 0x34 | 4 | u32 | c | anchor x in a/b units (read with d by VA 0xBF0B1) |
| 0x38 | 4 | u32 | d | anchor y. Scenes and colours: `(c, d) = (a//2, b//2)` in 2,705/2,706 (exception img5028); scene variants: c = a//2 in 3,485/3,485, d = b//2 in 3,329 and b//2 - 1 in 156; sprites: a per-image point, = centre in only 257/4,750 [V-all; H: the sprite's anchor] |
| 0x3C | 1 | u8 | | always 0x0B |
| 0x3D | 1 | u8 | flags | bit field: 3 (bits 0+1) for the 7 solid colours, 0 elsewhere. Code tests `&1` (VA 0x517F7, 0x82B88), `&2` (0x49B9B, 0x51824) and `&4` (0x4B66E, 0x4C00B, 0x57DB9, 0x592ED) [E; meaning H] |
| 0x3E | 1 | u8 | | 0 in the file; read by VA 0x4B676, 0x4C28F, 0x4F3D2 [E] |
| 0x3F | 1 | u8 | layer_kind | 0 base (7,316), 1 variant (7,262), 2 (140 scenes such as img1699, 110 of them scripted), 3 (26 variants of kind-2 scenes). Bit 0 is tested at VA 0x957F4. For kinds 2/3 the allpac2 copy is exactly 4x taller (4.1) [H: 4-frame / animated images] |
| 0x40 | 2 | u16 | char_id | section 3.1 (sprites only) |
| 0x42 | 2 | u16 | size_class | A/L/M/S = 0/1/2/3 (read by VA 0x41BA0) |
| 0x44 | 4 | | | 0 |

Worked example, record 9958 at VA 0x671900 + 9958 x 0x48 = 0x7209B0 (file 0x486950):

```
007209b0: 0000 0000 0000 0000 9976 1d00 0000 0000   name 0 | file -> 0x1D7699 "AKI_A_00_00_N00_B.MZP"
007209c0: af76 1d00 0000 0000 0000 0000 0000 0000   overlay -> 0x1D76AF "AKI_A_00_00_N01.MZP" | 0
007209d0: ffff ffff ffff ffff 0000 0000 6102 5208   file id -1, overlay id -1 | 0 | a=609 b=2130
007209e0: 2407 d518 4401 0000 f603 0000 0b00 0000   w=1828 h=6357 | c=324 | d=1014 | 0b, flags 0, 0, kind 0
007209f0: 0800 0000 0000 0000 ...                   char_id 8 (AKI), size_class 0 (A) | 0
```

Check: 1828 // 3 = 609 and 6357 x 362 // 1080 = 2130. allpac2 entry 9958 and script-name
list entry 9958 are `AKI_A_00_00_N01`.

`tsuki.catalog_eboot.Eboot.image_table()` reads all 14,775 slots, requires the used slots to
be contiguous and 14,744 of them, and raises on any unexpected pointer or name.

### 4.1 What allpac2 is for

- **Content** [V-all]: every scene, scene variant and sprite of the full archives at
  1/12.8 scale (1920 to 150, `BLACK` 1920x1080 to 150x84, `IMG5007` 5760x3240 to 450x253).
  With `R(x) = floor(x/12.8 + 0.5)` applied to the table's logical w, h, the allpac2
  header size is:
  - `(R(w), R(h))` for layer_kind 0/1;
  - `(R(w), 4*R(h))` for the 166 layer_kind 2/3 records, i.e. 4x taller
    (img1699 2838x2560 -> 222x800);
  - 150x84 for `img7000` (1920x10200) and `img7001` (1920x1640), a screen thumbnail.

  This holds for 14,743 of 14,744 records [V-all, `test_allpac2_is_reduced_copy` checks
  every record]. The only exception is img2583 (2758x639 -> 164x200; the rule gives
  215x200). Paired sprites are pre-composited with their bodies. Missing from allpac2 are
  the 998 `_B` bodies and the 1024 gallery-only IMG3000..4193; nothing exists only in
  allpac2. Repeated names such as `BLACK.MZP` are therefore the same asset at reduced
  resolution, not language variants.
- **Use** [E]:
  - VA 0xAB320 draws the layers of one of 16 slot records (0xBFC bytes each at bss VA
    0x17561A0; VA 0xAB880 loops over them). Each layer's image number is the i16 at
    record +4. VA 0xAB3A0 passes it to the archive loader VA 0xC3520 as `0x30000 + n`
    (allpac2 entry n = image-table record n) when the byte at 0x1762164 is non-zero, else
    unchanged.
  - The flag sits right after the 16 records (0x17561A0 + 16 x 0xBFC = 0x1762160) in .bss
    (initially 0). It is only ever written with 1 (VA 0xAA417, 0xAA499) and never cleared.
  - The slot initialiser VA 0xAA160 sets every +4 to -1. The only two stores of an image
    number into +4 (VA 0xAA4AB, 0xAA55C; the latter subtracts 10000 from values >= 10000)
    are in VA 0xAA2D0 on the path that has just executed the store of 1 at VA 0xAA499. So
    whenever +4 holds an image number the flag is 1 and the allpac2 copy is loaded; the
    unflagged branch (raw value = allpac0 index) is never reached with a valid number. It
    looks like a leftover from a build where these layers came from allpac0 [H].
  - VA 0xAA2D0 rebuilds a slot's layer list from a save-data record (fields at +0x58,
    +0x19E2.., +0x19FB of its second argument). So allpac2 is used to re-render **scene
    thumbnails from saved layer state** [H: the save/load screen thumbnails; the 150x84
    size of a 1920x1080 frame fits a save slot; VA 0xAD250, which reads the slot records,
    and VA 0xAB8E0, right after the slot loop 0xAB880, set a 1x1 canvas (7.5)].
  - A port can implement thumbnails by drawing the saved layer list with allpac2 images.

---

## 5. allpac3

### 5.1 Structure [V-all]

`allpac3` entries are HED8/NAM32 entries (`containers.md`). The 71 `*.MRG` entries are
mrgd00 containers whose every section is itself an image MZP: one nesting level, 3,699
children. The 65 `*.MZP` entries (and `DUMMY.MRG`) are plain image MZPs.

Example `CONF_PARTS_EN.MRG` (allpac3 #1, HED record `0c0d0000 4c0d 185a`, 6,969,880 bytes):

```
00000000: 6d72 6764 3030 1400 0000 0000 1300 9893   "mrgd00", 20 sections; s0 = (0, 0, 0x13, 0x9398)
00000010: 1200 a003 0e00 a065 1f00 4801 8f00 8870   s1, s2
...
000000a8: 6d72 6764 3030 1a00 0000 0000 0100 2904   child 0 = "mrgd00" with 26 sections
child 0 section 0: 6002 1001 8000 4000 0500 0500 0100 1100  = 608x272, tiles 128x64, 5x5, type (1,0x11)
```

All children are images: header type (1,0x11) in 3,434 children, (11,20) in 252 and
(1,0x10) in 13. The full-size scene type (12,273) never occurs. Pixel formats belong to
the image agent.

### 5.2 UI sets and their language variants

- Language suffixes are `_JA, _EN, _ZC, _ZT, _KO`, chosen by the loader (section 7.3).
- "identical" counts children that are byte-identical in all languages. "ko=ja" and
  "zt=zc" count children identical between those two languages
  [V-all, `samples/ui_misc/ui_parts.csv`].
- Whole-file duplicates (SHA-1): KO == JA for DISP, MENU, SAVE, TITLE, GALLERY_CG,
  GALLERY_MUSIC, GALLERY_FC and GALLERY_MOVIE. TITLE_ZC == TITLE_ZT. MODEPNL,
  GALLERY_COMMON and SELECTTXT are identical in all 5 languages.

| set (eboot name) | children | identical | ko=ja | zt=zc | content [VIS] |
|---|---:|---:|---:|---:|---|
| CONF_PARTS (`conf_parts.mrg`) | 20 (KO 19) | 7 | 18 | 8 | config screen (table 5.3) |
| DISP_PARTS (`disp_parts.mrg`) | 54 | 46 | 54 | 51 | in-game HUD: message windows (type 11, 1332x451 / 1707x487 / 1014x396 variants), saving icon and caption, control-panel buttons, PS4 button glyphs, quick-menu items (Autosaving/Quicksaving/Auto/Skip) |
| MENU_PARTS | 18 | 17 | 18 | 17 | system menu: Save/Load/Config/FlowChart/Title buttons (3 states), side bar, chapter list sheet (EN 3664x992), backgrounds and band textures |
| MODEPNL_PARTS | 2 | 2 | 2 | 2 | mode panel bar |
| SAVE_PARTS | 10 | 6 | 10 | 6 | save/load: AUTO SAVE tag, 2 digit sheets, 3 route cards 472x712 (Arcueid/Ciel/neutral), SCENE tag, chapter list (EN 2176x496), slot frame, background |
| TITLE_PARTS | 15 | 14 | 15 | 15 | title: menu items (NEW GAME/LOAD GAME/CONFIG/GALLERY/CONTINUE, 3 states), logo (JA 560x648, EN 1850x440), (C)TYPE-MOON, moon glow, PRESS ANY BUTTON, 4 type-11 1920x1080 backgrounds and a 2670x1266 layer |
| SELECT_PARTS | 6 | - | - | - | choice screen frames/backgrounds (no language) |
| SELECTTXT_PARTS | 83 | 83 | 83 | 83 | 82 choice texts pre-rendered at 1416x416 **in Japanese only** (children 0..81) plus a 120x808 digit column "0." .. "9." (child 82) [VIS, `previews/repair_variants.png`]; identical in all languages [H: other languages render choices from text] |
| BORDER_PARTS | 19 | - | | | frame/border textures, 3 styles at 20/64/80/128/256/512 px, type 11 |
| EFFECT_PARTS | 4 | - | | | 3 x 16x16 dummies + 1920x128 strip |
| NOISE_PARTS | 2 | - | | | 2 x 1920x1080 noise (type 11) |
| GALLERY_PARTS_COMMON | 6 | 6 | 6 | 6 | gallery frame, side bar, background |
| GALLERY_PARTS_CG | 2 | 1 | 2 | 1 | CG gallery headers |
| GALLERY_PARTS_CG_TH | 1979 | - | | | CG gallery thumbnails |
| GALLERY_PARTS_MUSIC | 7 | 6 | 7 | 6 | music room |
| GALLERY_PARTS_FC | 12 | 8 | 12 | 9 | flowchart: frame, buttons, "Current Scene", route lines, node card, scene-thumbnail mosaic 1620x1170 |
| GALLERY_PARTS_FC_TH | 495 | - | | | flowchart scene thumbnails |
| GALLERY_PARTS_MOVIE | 2 | 1 | 2 | 1 | movie gallery |
| GALLERY_PARTS_ENDING | 8 | 4 | 7 | 4 | ending list; child 5 differs KO vs JA |

### 5.3 CONF_PARTS children [VIS on EN, sizes V-all]

| child | JA size | EN size | lang | content |
|---:|---|---|---|---|
| 0 | 608x272 | 608x272 | yes | tab labels System/Sound/Controls x 3 states (normal, highlighted, selected) |
| 1 | 352x312 | 352x312 | yes | "Recommended Settings" button x 3 states |
| 2 | 1560x1080 | same | no | panel background (also SAVE #9, GALLERY_COMMON #5) |
| 3 | 1416x72 | same | no | header bar |
| 4 | 760x696 | 1336x696 | yes | option values ON/OFF/All Text/Read Only/Fade-in/None (Display Instantly)/Fade-out/None (Hide Instantly), 3 states |
| 5 | 904x152 | 1192x152 | yes | "Display End of Page Instantly", "Show Confirmation Message" |
| 6 | 936x504 | same | no | glow effects |
| 7 | 168x137 | same | no | slider -/+ buttons, 3 states |
| 8 | 1432x224 | same | no | page frame |
| 9 | 1432x1602 (KO 1432x1336) | same | yes | Text settings page (text speeds, auto mode, skip, fast-forward, animations, **Language**; the KO version lacks the Language row) |
| 10 | 504x128 | same | yes | Mute button x 3 |
| 11 | 408x120 | same | yes | Test button x 3 |
| 12 | 688x1008 | same | no | character face icons for per-character voice volume |
| 13 | 504x496 | 504x624 | yes | character names (Shiki Tohno ... Roa, Others, ????) |
| 14 | 1432x328 | same | yes | Sound page (Music / SFX / System Effects) |
| 15 | 1432x2872 | same | yes | Voice page |
| 16 | 1416x1528 | same | yes | Controls: controller |
| 17 | 1416x1528 | same | yes | Controls: mouse |
| 18 | 320x32 | same | no | digits 0..9 |
| 19 | 760x344 | same | no (absent in KO) | language selector 日本語 / English / 简体中文 / 繁體中文 x 3 states |

The sheets are atlases: button states sit side by side in columns and page labels are
stacked. The sub-rectangles are not stored in the data; they come from code constants
(section 7.5).

### 5.4 Other allpac3 entries

- **RULE_01..RULE_49** [VIS, V-all]: 1920x1080 type (1,0x11), greyscale. They are
  rule/wipe transition masks: radial bursts, stripes, diagonals, circles, a spiral,
  shards and noise (`previews/a3_rules.png`). `_FADS(2, ms, n)` uses NAM id
  `find("mask_top.mzp") + n`, i.e. allpac3 #85 + n = `RULE_nn` (script document 6.4). The
  eboot also has a `MASK001..MASK091` name list in data (file 0x5CFAF0, right after the
  script-name list) [H: unused transition names].
- **CAUTION screens** [VIS], 1920x1080 white text on black:
  - `CAUTION_xx` (`_JA`, `_EN`, `_ZC`, `_ZT`; the Japanese file has the explicit `_JA`
    suffix): "This is a work of fiction ... depictions of violence".
  - `CAUTION1[_xx]`: "The recording and publication of any images, videos ... is
    prohibited" (streaming notice).
  - `CAUTION2[_xx]`: both texts.
  - For CAUTION1 and CAUTION2 the Japanese file has no suffix; `_EN`, `_ZC` and `_ZT`
    exist. There is no KO caution.
  - The eboot names them `caution_ja.mzp`, `caution_en.mzp`, `caution1.mzp`,
    `caution1_en.mzp`, `caution2.mzp`, and so on (text VA 0x218A58..).
- **Placeholders** [V]:
  - `CLOCKSTAND_TOP`, `SELMAP_TOP` and `MASK_TOP` (allpac3), `ANIME_TOP` (allpac0) and
    allscr.mrg section 2 are byte-identical: 1216 bytes, 128x96, one white tile, type (1,1).
  - `DUMMY.MRG` (allpac0/1_2/2/3/4) and `CGMODE_TOP.MRG` (allpac1_2) are one 128-byte
    16x16 transparent image. TITLE_PARTS #8 and EFFECT_PARTS #0..2 are the same image.
  - The eboot loads `anime_top.mzp`, `mask_top.mzp` and `face_top.mzp` by name for
    specific subsystems [E: rip references at VA 0x9C2BE, 0x6B48E, 0x52B35]. `face_top`,
    `bg_top` and `date_top` do not exist on disk.

---

## 6. parts.hed / parts.mrg (no NAM) [VIS, V-all]

24 image MZPs, addressed by index from slot 0x18. Language groups are in engine language
order ja, en, zc, zt, ko, and the ko entry is a byte copy of en (3=6, 9=12, 14=17, 20=23).
zc and zt "Saving..." are identical (15=16).

| index | size | content |
|---:|---|---|
| 0 | 16x16 | transparent dummy (type (1,0), 16-colour) |
| 1 | 1920x1080 | TYPE-MOON logo splash on white |
| 2..6 | 1920x1080 | save data corrupted / cannot be loaded message box (ja, en, zc, zt, ko) |
| 7 | 160x160 | autosave spinner icon |
| 8..12 | 1920x1080 | "This game features an autosave function. Please do not turn off the power while the autosave icon is displayed." |
| 13..17 | 128x56 | "Saving..." caption |
| 18 | 160x160 | glow/flare sprite |
| 19..23 | 1920x1080 | "This is a work of fiction ..." disclaimer (same text as CAUTION) |

`samples/ui_misc/parts_mrg.csv` lists them, `previews/parts.png` shows them.

---

## 7. UI data in eboot.bin

### 7.1 Language index [E]

- VA 0x3ED90 (`get_language`): if the config word at data VA 0x66C59C is below 4, use it.
- Otherwise (and always in VA 0x407F0, the system-only variant) use
  `sceSystemServiceParamGetInt(LANG)`: 0 (Japanese) -> 0 ja, 11 (Chinese
  Simplified) -> 2 zc, 10 (Chinese Traditional) -> 3 zt, anything else -> 1 en.
- VA 0x406B0 is the in-game language option handler: it cycles 0..3 and has a "system
  default" case, stores the word, then reloads text, fonts and UI (calls 0xC6980,
  0x7C340, 0xC2AF0, 0x3EB80).
- The suffix table at relro VA 0x42C030 has 5 entries `ja, en, zc, zt, ko`. VA 0xC2AD0
  accepts index 4, but no code path produces it: the **KO assets are unused** [E]. They
  are mostly copies of JA, and script_text pair 4 is blank.

### 7.2 UI texture registration tables [E, V-all]

The UI screens load their sheets through arrays of 0x18-byte records. The localising
loader VA 0xC2AF0 and the plain loader VA 0xEBCB0 receive the array address; the layout was
established from their code and from VA 0xEB350, which loads one record:

| off | size | type | field | meaning |
|---:|---:|---|---|---|
| 0x00 | 4 | i32 | state | 0 in the file; VA 0xC2AF0 does nothing unless the first record's state is 1 or it is called with force=1; VA 0xEBCB0 sets it to 1 after loading |
| 0x04 | 4 | i32 | tex_slot | engine texture slot that receives the image; **< 0 ends the table** (the loaders test the *next* record's tex_slot) |
| 0x08 | 8 | char* | name | base archive name, for example `conf_parts.mrg` |
| 0x10 | 4 | i32 | child | section index inside the archive; **clamped** by VA 0xEB350: `n = children(archive); c = n - 1; if n > child: c = child` (signed), so an index past the end loads the last child |
| 0x14 | 4 | i32 | child2 | -1 = none; otherwise a second child loaded (unclamped) into the slot's second texture handle (VA 0xEB427..0xEB448). -1 in every record of a live table; 1 in the unreferenced ed1_parts tables |

Loading one record (VA 0xEBCB0 -> 0xEB350): `id = find(name)` (2.3), then the clamped child
of archive `id` goes into texture slot `tex_slot`, then `child2` if >= 0, then `state = 1`.

Worked example, the config table at data VA 0x66C9A0 (file 0x3D2940):

```
0066c9a0: 0000 0000 6901 0000 3e68 1a00 0000 0000   state 0, tex 361, name -> "conf_parts.mrg"
0066c9b0: 0000 0000 ffff ffff 0000 0000 6a01 0000   child 0, child2 -1 | next: state 0, tex 362
0066c9c0: 3e68 1a00 0000 0000 0100 0000 ffff ffff   "conf_parts.mrg", child 1, -1
...
0066cb80: ffff ffff ffff ffff 0000 0000 ...           tex -1 -> end (20 records: children 0..19 -> tex 361..380)
```

21 tables, 220 records (`samples/ui_misc/ui_texture_slots.csv`, `tsuki.catalog_eboot.UI_TABLE_VAS`;
the CSV also gives the resolved allpac3 entry, its child count and the loaded child):

| table VA | loader / code references | records -> texture slots |
|---|---|---|
| 0x66AD10 | localised (VA 0x12946, 0x12D09) | gallery_common 0..5 -> 1247..1252, gallery_cg 0..1 -> 1253..1254, gallery_music 0..6 -> 1255..1261, gallery_fc 0..11 -> 1262..1273, gallery_movie 0..1 -> 1276..1277, gallery_ending 0..7 -> 1278..1285 |
| 0x66BA80, 0x66BBC0 | localised (VA 0x29B7B.., 0x2A417..) | gallery_fc subsets / gallery re-maps |
| 0x66C9A0 | localised (VA 0x3EE1A, 0x3F7B3, 0x40767) | conf 0..19 -> 361..380 |
| 0x780C90 | localised (VA 0x56D49) | disp: 49 records, slots 2..4, 22..55, 76..77, 80..82, 385..388, 404, 405, 407 |
| 0x781B70 | **not localised** (VA 0x56D55 calls 0xEBCB0 directly) | border 0..18 -> 179..197, effect 0..3 -> 57..60, noise 0..1 -> 228..229 |
| 0x7A6090 | localised | disp 20,19,21 -> 358..360 |
| 0x7A6B20, 0x7A67F0 | localised | menu 0..7 -> 320..327, menu 8..17 -> 328..337 |
| 0x7A7680 | localised | modepnl 0..1 -> 382..383 |
| 0x7A8300 | localised | save 8,0..7,9 -> 338..347 |
| 0x8730F0 | not localised [V: resolves only without a suffix; SELECT_PARTS.MRG has none] | select 0..5 -> 91..96 |
| 0x8731A0 | localised, force=1 (VA 0xA5FCC) | selecttxt: record 0 child 89 -> slot 97; record 1 rewritten at run time -> slot 98 (below) |
| 0x873660 | localised | title 9,0..8,14 -> 125..134,136 |
| 0xDB9320 | localised | disp 18 -> slot 0 |
| 0x78B2C0 | localised, **referenced** (VA 0x62257, 0x624D5, 0x659DF, 0x663F2 in functions 0x62200, 0x624C0, 0x656D0, 0x66380) | `extra_parts.mrg` 15 records -> slots 1247.. (the gallery's slots). The archive does not exist on disk, so `find` returns -1 for every record |
| 0x782700, 0x784290, 0x785E20, 0x7879B0, 0x789540 | **no code reference at all** | `ed1_parts.mrg` (1 record each, child2 = 1): dead tables left over from earlier builds (the data also holds `roll_cg_*.mzp`, `eye_*.mrg`, `melty_yu.mzp` and 907 `face_nnnnnn.mrg` name strings) |

**extra_parts** [E]: 0x656D0 and 0x66380 are two cases of the system-menu state machine
VA 0x68A70 (call sites 0x691B9 and 0x691C0, next to the menu itself, 0x67410 at 0x691C7),
which VA 0xD23B0 calls at 0xD23CD (together with 0xA94F0 and 0x6A230; [H] a per-frame
update); 0x62200 is called from 0x6640F. 0x656D0 (VA 0x65819, 0x658D9) and code at VA
0x62D09 and 0x63740 also read the CG list through the relro pointer table 0x41C530
(-> 0x78C810, 0x78C880, ...). Whether any input reaches those two cases is not established [H: an
"extra" screen superseded by the gallery_parts_* screens]. A port that reaches them must
cope with a missing archive (-1 from the NAM search).

**selecttxt** [E]: the file holds record 0 = (tex 97, child 89) and record 1 = (tex 98,
child -1):

```
008731a0: 0000 0000 6100 0000 696a 2100 0000 0000   state 0, tex 97, -> "selecttxt_parts.mrg"
008731b0: 5900 0000 ffff ffff 0000 0000 6200 0000   child 89, -1 | record 1: state 0, tex 98
008731c0: 696a 2100 0000 0000 ffff ffff ffff ffff   "selecttxt_parts.mrg", child -1, -1
008731d0: ffff ffff ffff ffff ...                     terminator
```

Before loading, VA 0xA5F8D..0xA5FC1 rewrites record 1 from the u16 N at bss 0x1750698:
if 1 <= N <= 89 then (state 0, tex 98, child N - 1) else (state -1, tex -1, child -1),
which turns record 1 into the terminator. Record 0 is never written (no store to 0x8731B0).
SELECTTXT_PARTS has 83 children, so child 89 is clamped to child 82, the digit column, which
therefore always lands in slot 97; child N - 1 picks one of the 82 choice texts (or, for
N > 83, the digit column again). The design evidently allowed for 89 texts plus digits.
Check [V]: every record of the 15 live tables resolves to an allpac3 entry (the 13
localised tables for each of ja/en/zc/zt, the 2 neutral ones without a suffix): 707 lookups, of which 699 hit an
existing child directly, 4 (selecttxt record 0) need the clamp and 4 (selecttxt record 1)
are rewritten at run time (`test_ui_texture_tables_resolve`).

### 7.3 Localisation of UI archive names (VA 0xC2AF0) [E]

```
if table == NULL: return
if not force and table[0].state != 1: return
lang = get_language()                      # 7.1; 0..3 in practice
for each record (until the next record's tex_slot < 0):
    n = strlen(name)
    base = name[:-4] if n >= 5 and name[-4:] in (".mrg", ".MRG") else name   # two strcmp calls
    tmp = base + "_" + LANG_SUFFIX[lang if lang < 5 else system_lang()] + ".mrg"   # VA 0xC2C56: >= 5 -> 0x407F0
    record.name = tmp                       # temporarily, 32-byte buffers on the stack
load_textures(table)                        # VA 0xEBCB0 (7.2)
restore the original name pointers
```

`find` (2.3) upper-cases the name, so `conf_parts_en.mrg` matches `CONF_PARTS_EN.MRG`.
`tsuki.catalog.localize_parts_name(name, lang, system_language)` is the port and
`ui_child_index(child, n)` the clamp.

### 7.4 What is *not* in the data

There is no table of button rectangles, widget positions or animation keyframes for the
UI screens in any archive or eboot data table [V: every allpac3 child is a plain image; the
data segment holds only the registration tables above]. Positions, atlas sub-rectangles,
states and animations are hard-coded in the UI functions as immediate texture-slot numbers
plus float constants in .rodata. (Scenario images are different: their size and anchor are
data, image-table fields a, b, c, d, section 4.)

Example, the config screen setup at VA 0x3F17C..0x3F2A0 [E], decoded with
`scratch/ui_misc/uicalls.py`:

```
0xDCBE0(640.0, 448.0, 0, 0)            # set the virtual canvas
0xD9DD0(0.333, 0.415)                  # texel -> canvas scale: 1/3, 448/1080
0xD1410(135.667, 68.444, 603.0, 424.356)  # clip rect = pixels (407,165)-(1809,1023)
0xEB780(363, 16, 0, x=120.0,   y=0.0)    # texture slot 363 = conf child 2 (background)
0xEB780(364, 16, 0, x=133.333, y=39.822) # slot 364 = child 3 (header bar) at pixel (400, 96)
0xCFC90(48, 136.0,   39.822, 1.0)        # sprites 48..51 (tabs) at x = 408/656/888/1536 px
0xCFC90(49, 218.667, 39.822, 1.0) ...
```

### 7.5 Virtual canvases [E]

Each UI module sets a canvas with VA 0xDCBE0(w, h, ...) and a scale with VA 0xD9DD0(sx, sy).
Layout constants are floats in canvas units:

`pixel_x = x / sx`, `pixel_y = y / sy` (1920x1080 output).

Census of every call site (`scratch/ui_misc/canvas_census.py`, which tracks rip-relative
float loads, `vxorps` zeroing and register copies back to the previous call; per-site list
with the containing function in `samples/ui_misc/ui_canvas_calls.csv`):

| canvas (0xDCBE0, 188 sites) | sites | functions (start VA) |
|---|---:|---|
| 640 x 448 | 40 | 32 functions, among them config 0x3F140 and 0x2A530, 0x3BE70, 0x62610 .. 0x70340, 0xA6E10 .. 0xB1F30, 0xD2400 |
| 960 x 540 | 31 | 20 functions (0x451B0, 0x4C720, 0x4DC60, 0x5EAC0, 0xB29D0, 0xE8280, ...) |
| 960 x 544 | 28 | 17 functions (0x20C0, 0x7F00, 0x44520, 0xAFB10, 0xB02B0, ...) |
| 10 x 10 | 15 | 0x451B0, 0x4F320, 0x5A5E0, 0x6B910 [H: normalised effect space] |
| 480 x 272 (scenario / script coordinates) | 14 | 0x45910, 0x45AC0, 0x53CF0, 0x67820, 0xE8280 |
| 640 x 362.667 | 13 | 0x4F320, 0x50890 (0x4F320 also reads the image table; image-table a, b are sizes in this canvas) |
| 640 x 344.7 | 3 | 0x3C360, 0x3C4B0, 0xA6E10 (0xA6E10 is in the select / choice screen code) |
| 1920 x 1080 | 3 | 0x5F590, 0x5F5D0 |
| 1 x 1 | 3 | 0xAB8E0, 0xAD250 [H: save-slot thumbnails, 4.1] |
| 640 x 480 | 1 | 0xB1E50 |
| not constant at the call | 37 | 29 functions |

| scale (0xD9DD0, 291 sites) | sites |
|---|---:|
| 0.3333 x 0.4148 (1/3, 448/1080) | 59 |
| 1 x 1 | 28 |
| 0.5 x 0.5 | 20 |
| 0.5 x 0.5037 (544/1080) | 11 |
| 0.25 x 0.2519 (272/1080) | 8 |
| 0.3333 x 0.3192 (1/3, 344.7/1080; VA 0xA764F, 0xA77A2, 0xA77ED; a 4th site 0xA75EF has a computed x) | 3 |
| 0.333 x 0.333 | 3 |
| 0.5 x 1, 2 x 2, 0.25 x 0.25 | 2 each |
| 18 other constant pairs (0 x 0, 0.125/0.25/2/4/8/32/1.1 x 1, 0.9375, 0.3333, 0.6667, 0.3571, 0.1339, 2.8, 7.4667 and 1000 squared, 1.3333 x 1.2444, 0.6 x 0.4148) | 1 each |
| partly or fully computed | 135 |

(156 sites with both arguments constant + 135 = 291.)

Porting the UI therefore means porting each screen's code (the functions that reference
the registration tables in 7.2 are the entry points: VA 0x3EE10 config, 0x56D40/0x5A22C
HUD, 0x6758E/0x6CA83 menu, 0x6E683 mode panel, 0x6F423 save/load, 0xA5F7F select,
0xAFD8C title, 0x12946/0x29B7B/0x2A3E5 gallery). Only the config screen is decoded here;
the rest is open work (section 12).

### 7.6 Gallery / flowchart lists [E, partly H]

- **CG gallery** ("extra"): 1314 `IMGnnnn.MZP` string literals (text VA 0x204DBE..),
  referenced by 1314 pointers in data VA 0x78C810.. in 56-byte records (401 clusters),
  next to the `extra_parts.mrg` table; read by VA 0x656D0 through relro 0x41C530 [H: the
  CG list, one entry per gallery item with its variants].
- **Flowchart**: relro VA 0x404170..0x4181C0 holds 2197 pointers in 0x28-byte records (5
  name slots) to 2194 literals at text VA 0x1884E7.. (IMG plus sprite names, some prefixed
  with ' ' or '-', e.g. `-IMG3864.MZP` [V; meaning of the prefix H]). It is referenced
  from VA 0x1C596. About 495 records have slot 0 set, which matches the 495
  `GALLERY_PARTS_FC_TH` thumbnails [H: per-flowchart-node layer list].
- `gallery_parts_cg_th.mrg` / `gallery_parts_fc_th.mrg` are opened directly (rip
  references at VA 0x1BA3B, 0x2A469, 0x29499, 0x18FDDB).

---

## 8. Loose image files

| file | format | role | evidence |
|---|---|---|---|
| `data/if_ja.png`, `if_en.png`, `if_zc.png`, `if_zt.png` | PNG 228x128 RGB (zc and zt byte-identical) | **save-data icon**: the title-logo picture shown in the PS4 save manager | [E] VA 0x41AD0 builds `/app0/data/if_` + suffix + `.png` (default en for unknown system languages, 0x41B14..0x41B88). Callers (VA 0xEA41B, 0xEE311) store the path in the save-data object right after title/subtitle/detail (0x120390/0x120410/0x120490); the only `sceSaveDataSaveIcon` call is at VA 0x1214A5. [V] 228x128 is the PS4 save icon size. [VIS] logo on a moonlit sky (EN "TSUKIHIME A piece of blue glass moon"; JA/ZC/ZT with the kanji logo) |
| `image/corp.png` | PNG 131x48, palette + tRNS, Adobe RGB | **watermark that the system overlays on user screenshots** | [E] VA 0xEE550(mode): mode 0 calls `sceScreenShotDisable`, `sceScreenShotSetOverlayImageWithOrigin("/app0/image/corp.png", 0, 0, 3)`, `sceScreenShotEnable`, `sceGameLiveStreamingEnableLiveStreaming(1)`, `sceSharePlaySetProhibition(0)`, `sceVideoRecordingSetInfo(0x0D, &0, 4)`. Other modes disable screenshots and prohibit streaming/recording (spoiler protection). Origin 3 = bottom-right [H]. [VIS] "©TYPE-MOON" logo |

Import names were resolved from the NID table (`scratch/ui_misc/imports.py`,
`nidnames.py`): NID = base64(reverse(SHA1(name + key)[0:8])) with the alphabet `+-`. It
reproduces known NIDs such as strlen `j4ViWNHEgww` and sceSysmoduleLoadModule `g8cM39EUZ6o`;
98 of 334 stubs were named.

---

## 9. Movies

### 9.1 Files [V-all, ffprobe/ffmpeg 9.0.2]

134 files, 4.75 GB, 66.7 min in total. All files share: MP4 (brand `mp42`), H.264,
1920x1080, yuv420p, progressive. 79 files are tagged BT.709 limited range and 55 are
untagged (treat them as BT.709). No HEVC and no other codecs. Full per-file table:
`samples/ui_misc/movies.csv` (columns `audio_peak_db`, `audio_silent` from ffmpeg `astats`).

**Audio**: 43 files have an AAC-LC stereo 48 kHz track, 91 have none. Of the 43 tracks,
**31 are digital silence** (peak -inf dB): all 27 `phasetitle_*_zt`, `TM_CI`,
`phasetitle_09_01_en`, `phasetitle_10_01_en` and `phasetitle_05_01_zc`. Only 12 carry
sound: `prologue` x4 (peaks -0.9 .. -1.6 dB), `op1_arc`/`op2_ciel` and their `_en`/`_zc`
versions (6, peaks -2.8 .. -4.3 dB), `ed01_arc` (+0.16 dB) and
`ed03_ciel` (-2.1 dB) [V-all]; ed01's peak above 0 dBFS is an over in the float
decode. A port can treat a silent track like no track (no audio voice, no ducking of BGM).

| group | ids | files | profile/level | fps | audio | duration |
|---|---|---|---|---|---|---|
| title background loop | 0..3 | titlebg1..4 | High 4.0 | 30 | none | 120.0 s each |
| prologue | 4 / 46 / 47 / 106 | prologue, _en, _zc, _zt | High 4.0 | 30 | AAC, sound | 384.3 s (en 388.7 s) |
| opening | 5, 6 / 102..105 | op1_arc, op2_ciel, _en, _zc | base: High 4.2; en/zc: Constrained Baseline 4.1 | base 30, en/zc 29.97 | AAC, sound | 105.8..107.2 s |
| chapter end | 7 | phaseout | High 4.0 | 30 | none | 7.0 s |
| chapter title card | 8..34 (+_en 48..74, _zc 75..101, _zt 107..133) | phasetitle_NN_MM[a|b] | base High 4.0; en/zc/zt Main 4.1 | base 30; en/zc/zt 29.97 (one file each at 30) | base none; en 2 and zc 1 of 27 with an AAC track; zt all 27 with an AAC track; **every one of these 30 tracks is silent** | 5.0..9.0 s |
| ending | 35..37 | ed01_arc, ed02_ciel, ed03_ciel | High 4.0 | 30 | AAC with sound (ed02: none) | 316.3 / 148.0 / 254.9 s |
| TYPE-MOON logo | 38 | TM_CI | Main 4.1 | 30 | AAC, silent | 6.0 s |
| staff credits | 39..45 | credit00..06 | High 4.0 | 30 | none | 5.6..9.0 s |

Language naming: no suffix = Japanese / language-neutral; `_en`, `_zc` and `_zt` are
variants. Only prologue, the openings and the 27 title cards have variants. The openings
have **no** `_zt` file (zt plays `_zc`). Endings, credits, logo, title loops and phaseout
are shared.

### 9.2 Movie table (data VA 0xDC8C70) [E, V-all]

134 records of 0x30 bytes, terminated by a record with id -1 at data VA **0xDCA590**
(= 0xDC8C70 + 134 x 0x30, file 0xB30530). Record k has id k, and its path equals the file
on disk; the eboot paths and the files match 1:1.

| off | size | type | meaning |
|---:|---:|---|---|
| 0x00 | 4 | i32 | movie id (= index; -1 ends) |
| 0x04 | 4 | u32 | 0 |
| 0x08 | 8 | char* | `/app0/image/movie/<name>.mp4` |
| 0x10 | 4 x 4 | u32 | 0, 960, 544, 0 (runtime defaults of the player object [H]) |
| 0x20 | 3 x 4 | f32 | 0.0, 1.0, 1.0 |
| 0x2C | 4 | u32 | 0 |

```
00dc8c70: 0000 0000 0000 0000 e866 2d00 0000 0000   id 0, path -> "/app0/image/movie/titlebg1.mp4"
00dc8c80: 0000 0000 c003 0000 2002 0000 0000 0000
00dc8c90: 0000 0000 0000 803f 0000 803f 0000 0000
00dc9510: 2e00 0000 0000 0000 526d 2d00 0000 0000   id 46 -> "/app0/image/movie/prologue_en.mp4"
00dca560: 8500 0000 0000 0000 637b 2d00 0000 0000   id 133 (last record)
00dca590: ffff ffff 0000 0000 0000 0000 0000 0000   terminator, id -1
```

Play functions: VA 0xEE6C0 and 0xEE790 take a table index (`path = table[i].path`,
`rax*0x30 + 0xDC8C78`). VA 0xEE650 maps an id to an index.

### 9.3 Language selection (VA 0x5E280, inlined again at 0x5E3D3) [E]

```
lang = get_language()        # 0 ja, 1 en, 2 zc, 3 zt (7.1)
movie_index(id):
    if id == 4:                                      # prologue
        return [4, 46, 47, 106][lang] if lang < 4 else 46   # table at text VA 0x200F70
    if 8 <= id <= 34:                                # phasetitle_*
        if lang == 0: return id
        if lang == 3: return id + 99                 # _zt
        if lang == 2: return id + 67                 # _zc
        return id + 40                               # _en (lang 1 and anything else)
    if id in (5, 6):                                 # op1_arc, op2_ciel
        if lang in (2, 3): return id + 99            # _zc, also used for zt
        return id if lang == 0 else id + 97          # _en
    return id                                        # language-neutral
```

- Script command `_TOMP(id)` passes the language-neutral id. Ids used by the 48
  `_TOMP` calls in allscr.mrg: 4, 5, 6, 8..34, 35..37, 39..45 [V].
- Ids 0..3 (title loops), 7 (phaseout) and 38 (TM_CI) are played by engine code
  [H: title screen, chapter transitions, boot logo].
- The caller at VA 0x2BD94 plays `movie_index(gallery_index + 4)` (movie gallery).
- Ports: `tsuki.catalog.movie_index_for_language` / `movie_file_for_language`; the full
  matrix is in `samples/ui_misc/movie_language_matrix.csv`.
- Dev leftovers not on disk: `/app0/data/video/opening1.mp4` and the `.tga`/`.at9`/`.vag`
  test files (text VA 0x2D6467..0x2D6612).

---

## 10. Inner formats seen in the containers (counts) [V-all]

Container-level census from `iter_catalog` (54,210 rows: 49,620 triplet entries, 3,699
allpac3 children, 891 standalone sections; `samples/ui_misc/inner_formats.csv`):

| content | image header type (a,b) | count | owner / meaning |
|---|---|---:|---|
| mzp/image | 1,0x11 | 18,246 | image agent: 8-bit indexed, global 256-colour palette. allpac2 14,744, allpac3 3,495 (UI children, rules, cautions), allpac0 7 (solid colours) |
| lv-mzx | | 16,466 | voice: LV wrapper + MZX -> RIFF ATRAC9 (audio agent) |
| mzp/image | 12,273 | 16,443 | image agent: full-size scenes and sprites (per-tile header/palette): allpac1 9,892, allpac1_2 4,137, allpac0 2,381, allpac4 33 |
| riff-wave/atrac9 | | 1,459 | bgm 132 + vse 1327 (audio agent) |
| mzp/image | 11,20 | 568 | image agent: 32-bit tiles. 252 UI children (title backgrounds, borders, noise, HUD windows) + 316 full-size images |
| mzx | | 549 | allscr scripts (script agent) |
| mzp/image | 1,1 | 353 | indexed images: 325 font pages (ft_*), 23 parts.mrg screens, 5 white 128x96 placeholders |
| mzp/archive | | 71 | allpac3 UI sets (this document) |
| mzp/image | 1,0x10 | 19 | 16x16 dummies (6 DUMMY/CGMODE_TOP entries + 13 UI children) |
| vag | | 19 | vag.mrg system sounds `moon_seNNNNN_L/_R` (L/R mono pairs) + `dummy` |
| nam32 | | 5 | allscr s0 + se_nam s0..3 |
| be32-offsets / text-utf8 / text-ascii | | 5 / 4 / 1 | script_text.mrg (text agent) |
| mzp/image | 1,0 | 1 | parts #0 (16-colour 16x16) |
| mzp/mixed | | 1 | allscr s1 (nested plain-text label tables) |

Loose files outside the archives: 4 save icons + corp.png (PNG), 134 MP4, eboot.bin
(SELF), `sce_module/*.prx`, `sce_sys/` (param.sfo, icons, `pic0/pic1`, `snd0.at9`,
`playgo-chunk.dat`, `playgo-manifest.xml`, trophy). None of these is needed by the
runtime except the PNGs and MP4s. `sce_discmap.plt` (PLT, 3.2 MB) is the PlayGo disc map,
unused by a port.

---

## 11. Evidence (commands actually run)

- `python -m tsuki.catalog summary`: all archives in about 5 s, 0 `unknown`; per-archive
  counts as in section 1.
- `python -m tsuki.catalog export samples/ui_misc` (2 min 27 s, most of it ffmpeg `astats`
  decoding the 43 audio tracks): writes the tables of section 13. Self-checks printed:
  `name_list_mismatches: 0` (script-name list == image table), `nam_lookup_shadowed: 3`
  (DUMMY copies only), `image_table_unresolved: 3` (the missing bodies),
  `script_name_selfcheck_failures: 0` (every record's script name resolves to itself).
- `python -m tsuki.catalog image img2511 WHITE boy_a_00_00_n01 AKI_A_00_04_N01 nosuchimage`
  and `... image img1696 --aa 11 --bb 11`: the results in the table of 2.4.
- `pytest tools/tests/test_catalog.py`: 37 passed in 1.5 s warm (about 9 s on a cold file cache); with
  `TSUKI_GAME_DIR=/nonexistent`, 28 passed and 9 skipped. mypy and pyflakes are clean on
  catalog.py, catalog_eboot.py and test_catalog.py.
- Script census with the engine rule (`scratch/ui_misc/script_lookup.py` over
  `scratch/ui_misc/allscr_dump.txt`, all 549 scripts, 19.6 MB): table of 2.4.
- eboot work (`scratch/ui_misc/eb.py`, capstone + a numpy disp32 scanner; `reloc.py`
  parses PT_DYNAMIC inside PT_SCE_DYNLIBDATA):
  - Disassembled VA 0xC3270 (2.3), 0x78410, 0x780D0, 0x81760..0x81C60, 0x49D50, 0x4A060,
    0x4A110, 0x41BA0 (2.4, 4), 0xAB320, 0xAA160, 0xAA2D0, 0xAB880 (4.1), 0xC2AD0, 0xC2AF0,
    0xEBCB0, 0xEB350, 0xA5F40..0xA5FD6 (7.2, 7.3), 0x68A70/0x691B9 and 0xD23B0 (extra_parts
    callers), 0xC31F0, 0xC3520, 0xD1C10..0xD1D4C, 0xD1F40..0xD221E, 0xEED80, 0x5E280,
    0x3ED90, 0x407F0, 0x406B0, 0x41AD0, 0xEE550, 0xEE650..0xEE860, 0x3EE10..0x3F6C0 and
    0x56D30.
  - Relocations: 39,836 RELA entries (39,459 R_X86_64_RELATIVE, 377 symbol); every
    RELATIVE addend equals the 8 bytes already stored at its target (0 mismatches), and
    none targets image-table +0x00.
  - Code references of every UI table (rip-relative scan): all 15 live tables referenced
    (2..5 sites each), extra_parts referenced 4 times, the 5 ed1_parts tables 0 times.
  - Canvas/scale census over all 188 + 291 call sites (`canvas_census.py`).
  - Resolved PLT NIDs (`imports.py`, `nidnames.py`); scanned the data/relro segments for the
    image, movie, UI-registration and gallery tables (`imgtab.py`, `texreg.py`).
- Image-table statistics over all 14,744 records (`scratch/ui_misc/repair_checks.py`):
  a = floor(w/3) 14,744; b = floor(h x 362/1080) 14,744 (round() gives only 8,223 and
  floor(h x 362.667/1080) 1,390); 4,750 sprite records = 3,529 paired + 1,221 standalone
  (file == name in 1,221/1,221); 33 standalone sprites with an unpaired `_B` file; no
  overlay outside the sprite category.
- `ffmpeg -i <movie> -vn -af astats` on all 43 audio tracks: 31 silent, 12 with sound.
- Visual checks (contact sheets rendered with the scratch-only previewer
  `scratch/ui_misc/preview.py`, 8-bit palette types only; copies in
  `samples/ui_misc/previews/`):
  - `parts.png`: 24 system screens as described in section 6.
  - `a3_conf_en.png`, `a3_title_en.png`, `a3_menu_en.png`, `a3_save_en.png`,
    `a3_disp_en.png`, `a3_gallery_fc_en.png`: UI sheets as described in section 5.2/5.3.
  - `a3_selecttxt_ja.png`: 82 Japanese choice texts.
  - `a3_rules.png`: 49 grey wipe masks.
  - `a3_caution_top.png`: 12 notices + 3 white 128x96 placeholders + DUMMY.
  - `a2_samples.png`: allpac2 small composited sprites (ARU 120x497 full body, the
    chibi Neco-Arc sprite ARU_L_80_01_N05, SIE 148x484) and scenes (IMG0042 gate 300x169,
    its variant IMG0042_03_03 302x171, IMG0007 train interior, IMG1508 sky).
  - `repair_variants.png` (allpac2 copies): IMG1696 vs IMG1696_11_11 (same window and
    curtains, the variant complete with some semi-transparent pixels); AOK_L_21_00_N00 vs
    its `_03_03` variant (same Aoko figure); AKI_A_00_03_N04 vs `_30_30` (the same full
    Akiha sprite, blurred); standalone AKI_A_00_04_N01 (complete figure, hands on hips) and
    paired AKI_A_00_00_N01 (composited); IMG0342 vs IMG0342_04_04 (same classroom);
    SELECTTXT_PARTS_EN child 82 = a 120x808 column of "0." .. "9."; child 0 = a Japanese
    choice text.
  - `composite_vs_allpac2.png`: body + face composites next to their allpac2 copies (Ciel in
    school uniform, Akiha with raised arm, the tiny BOY sprite) and the standalone Akiha
    sprite; each pair looks identical.
- `data/if_*.png` and `image/corp.png` were viewed directly: logo on a moonlit sky
  228x128; the "©TYPE-MOON" logo.
- ffprobe JSON for all movies: `scratch/ui_misc/movies_probe.json`.

## 12. Open questions

1. UI layout: the positions, atlas sub-rectangles, state changes and animations of every
   screen are code. Sections 7.4/7.5 give the method (canvas + scale + constants), one
   decoded example (config), the complete canvas/scale census and the entry points. Next
   steps: decode each entry function's calls to VA 0xEB780 (texture slot placement),
   0xCFC90 (sprite placement), 0xD1410 (clip) and the input handlers, one screen at a
   time, starting with title (0xAFD8C) and the HUD (0x56D40).
2. Image-table anchor `c, d` of sprites (not the centre) and how the scenario renderer
   (0x4F320, 640x362.667 canvas) combines a, b, c, d with STCH x, y and scale.
3. Image-table `flags` bits (&1, &2, &4) and byte +0x3E: meaning of each test site.
4. `layer_kind` 2 and 3 (140 + 26 records, img1699 and similar): what is special about
   them [H: 4-frame or animated images; their allpac2 copies are 4x taller].
5. Sprite size letters A/L/M/S: exact meaning. Variant letters A/B/C vs N.
6. What the engine shows for the 3 paired sprites whose body is missing and for the 4
   unresolvable script names (index -1).
7. Whether the extra_parts cases of the menu state machine (0x656D0, 0x66380) can be
   reached.
8. Gallery and flowchart record layouts (section 7.6), including the ' '/'-' name prefix.
9. Which PlayGo chunk (alldata / alldata2) holds allpac1 vs allpac1_2. This is irrelevant
   for a PC port.
10. Choices in non-Japanese languages: SELECTTXT images exist only in Japanese, so the
    other languages presumably render choices from script_text (script/text agents).
11. Which title-background loop (titlebg1..4) is shown when (route progress?).
12. `MASK001..MASK091` names in data (file 0x5CFAF0).

## 13. Files in samples/ui_misc

| file | rows | content |
|---|---:|---|
| `archives.csv` | 19 | role of every archive / loose file group |
| `entries.csv` | 54,210 | every triplet entry, allpac3 child and standalone section: category, language, base, variant (full/reduced), content format, size, image type |
| `category_summary.csv` | 51 | counts per archive x level x category |
| `inner_formats.csv` | 16 | section 10 |
| `ui_parts.csv` | 3,699 | allpac3 children with SHA-1 and cross-language identity |
| `parts_mrg.csv` | 24 | section 6 |
| `ui_texture_slots.csv` | 220 | section 7.2, with the resolved allpac3 entry, its child count and the loaded (clamped) child |
| `eboot_image_table.csv` | 14,744 | section 4 (engine VA, script name, file, overlay, paired, w, h, a, b, c, d, flags, kind, char_id, size_class) |
| `movies.csv` | 134 | eboot id + full ffprobe properties + audio peak / silence |
| `movie_language_matrix.csv` | 46 | file played per language for every neutral id |
| `loose_files.csv` | 5 | PNG roles |
| `engine_tables.json` | | suffixes, prologue ids, search order, archive ids, char codes, UI table sizes, sprite pairing counts, lookup self-check results |
| `ui_canvas_calls.csv` | 480 | every call site of the canvas/scale/clip setters with its constant arguments (from `scratch/ui_misc/canvas_census.py`) |
| `script_image_census.json` | | section 2.4 census and the 5 unresolved names |
| `previews/*.png` | 13 | contact sheets used for the visual checks |

## 14. Corrections after independent review

| review finding | resolution |
|---|---|
| 1,221 of 4,750 sprites are standalone, not face layers (major) | Confirmed [V-all]. 3.3 rewritten: draw `file`, then `overlay` if set; classifier detail and tests updated (`test_eboot_image_table_matches_allpac2` asserts 3,529 + 1,221). |
| "7,044 of 7,082 calls resolve" mixed IMG and sprite calls; sprite-variant selection called open (major) | Confirmed and superseded: the engine rule (VA 0x81760) appends `_aa_bb` for IMG and sprite names alike and shows the variant instead of the base. Census redone with that rule (2.4): 54,306 of 54,310 static `_STCH` names resolve. The old "missing" `_00_00` names do not exist in the engine's eyes (aa = bb = 0 adds no suffix). |
| (new, found while tracing the above) | `IMGnnnn_aa_bb` and `CCC_..._aa_bb` are not overlays drawn with their base: the STCH/SCH2 backend loads only the suffixed record, a complete alternative rendering of the base (2.4, `previews/repair_variants.png`). Category comments, classifier details and section 3 updated (category values kept for CSV compatibility). |
| Script ids never match the NAM search; the doc did not say how they become assets (major) | Confirmed; traced: VA 0x78410 + name list 0x7F6800 + image table (2.4), reference port `ScriptImageResolver`. The reviewer's suggested path through VA 0x49D50 is **not** used: that function has 0 `call`/`jmp` references and no relocation or data pointer to it, and the field it scans (+0x00) is 0 in every slot with no RELA entry targeting it, so it always returns -1. |
| Per-screen layout incomplete; canvas census incomplete (major) | Census redone over all 188 + 291 call sites (7.5), adding 10x10, 640x344.7, 1920x1080, 1x1 and 640x480 canvases and the (1/3, 0.3192) scale; per-site CSV published. Counts now 640x448 40, 960x540 31, 960x544 28, 480x272 14, 640x362.667 13 (the spec reviewer's counts for the four it listed). Per-screen layout remains open (12.1); status stays partial. |
| Image table indexed from 0x671900, offsets 8 lower; capacity 14,775; +0x3D is a bit field | Confirmed; section 4 and `ImageRecord` now use engine offsets (`fill_flag` renamed `flags`), `image_table()` reads all 14,775 slots and checks 14,744 used. Nuance: VA 0x957F4 tests `+0x3F & 1` (bit 0 of layer_kind), and VA 0x4A060/0x4A110 bound-check against 0x59D7, not 0x39B6. |
| extra_parts table is referenced, not dead | Confirmed; 7.2 now lists the 4 code references and the menu-state-machine callers; only the 5 ed1_parts tables are unreferenced. |
| selecttxt: 699 of 707, child 89 unexplained | Explained: VA 0xEB350 clamps the child, so child 89 loads child 82 (the digit column, viewed); record 1 is rewritten by VA 0xA5F8D..0xA5FC1. Test asserts 699 direct + 4 clamped + 4 run-time. |
| 0xAB3A0 unflagged branch unexplained | Reconciled in 4.1: every store of an image number into slot +4 follows the store of 1 to the flag in the same path; the flag is never cleared. |
| 31 of 43 audio tracks are silent | Confirmed with astats; 9.1 and `movies.csv` updated. |
| Movie terminator at 0xDCA590 | Fixed (9.2, with hex dump). |
| CAUTION grammar | Fixed (3, 5.4): `CAUTION_JA` has a suffix; only CAUTION1/CAUTION2 are suffix-less for Japanese. |
| Docstring "19 tables" | Fixed (21 tables, 220 records). |
| nam_key misses the Shift-JIS rule, 28-byte copy and 256-byte backslash scan | Ported exactly (2.3, `nam_key`), with synthetic tests. |
| Field b wording; a is floor, not ceil | Fixed: a = floor(w/3) and b = floor(h x 362/1080) on all 14,744 records. |
| `image_table()` stops silently; `localize_parts_name` IndexError for lang >= 5; 1-in-5 allpac2 test; selecttxt skipped in the test | All fixed: strict table reader, system-language fallback (VA 0xC2C56), all 14,744 allpac2 records checked with img2583 as the only exception, selecttxt asserted. |
