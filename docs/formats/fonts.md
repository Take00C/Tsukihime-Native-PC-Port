# Fonts: ft_<lang>.mrg pages + the glyph tables in eboot.bin

Scope: how TSUKIHIME -A piece of blue glass moon- (PS4, CUSA47999, app 01.01) stores and
draws text glyphs. Containers (mrgd00, MZX0) are in `containers.md`; this document starts at
"one font page = one image MZP". It is meant to be enough to write the C# / C++ text
renderer without reading the Python reference (`tools/tsuki/font.py`,
`tools/tsuki/font_eboot.py`).

Confidence labels:

- **[V-all]**: verified on every page / record / string in the retail data (counts given).
- **[V-n]**: verified on n cases.
- **[E]**: read from the game code (eboot.bin, virtual address given). Text VA = file
  offset - 0x2280; data VA = file offset - 0x365FA0 + 0x600000.
- **[EMU]**: the game code itself was run under an x86-64 emulator (unicorn) and compared
  with our reimplementation; 0 differences.
- **[H]**: hypothesis / this library's own choice.

All integers are little-endian unless stated otherwise.

---

## 1. Overview

There are **no metrics or character maps in the font files**. They hold only pictures:

```
image/ft_<lang>.mrg   mrgd00, 65 sections; section k = texture slot 0x21 + k
     section k = image MZP: header + 256-colour palette, one MZX0 tile of 8-bit indices
     1024x1024 page = upper half (y 0..511): glyphs, lower half (y 512..1023): the same
                      glyphs dilated ("outline"), exactly 512 px lower

eboot.bin             code point -> (texture slot, x, y, w, h) for every language and style,
                      as sorted binary-search arrays + rectangle arrays (section 5)
```

The renderer turns a UTF-8 string into quads: for each character it looks up a rectangle
on one of the 65 textures, draws the outline copy (rect + 512 in y) and then the glyph
(rect), and advances the pen by `(w - 6) * scale`.

| lang | file | bytes | engine index | used by the game |
|---|---|---:|---:|---|
| Japanese | ft_ja.mrg | 9,014,472 | 0 | system language 0 |
| English | ft_en.mrg | 1,212,184 | 1 | every other system language |
| Simplified Chinese | ft_zc.mrg | 10,798,496 | 2 | system language 11 |
| Traditional Chinese | ft_zt.mrg | 10,120,552 | 3 | system language 10 |
| Korean | ft_ko.mrg | 596,576 | 4 | never (all 65 pages blank, tables are stubs) |

Engine index = position in the file-name table at VA 0x2D352C (`ft_ja.mrg ft_en.mrg
ft_zc.mrg ft_zt.mrg ft_ko.mrg`, 10-byte stride, reached through the PIC offset table at
0x2D3560) [E]. The current text language is the u16 at data VA 0x66C59C (= 0x66C578 + 0x24),
read by the glyph code at 0x11750 and by `get_language()` 0x3ED90 [E].

---

## 2. Font loading [E]

`load_font(lang)` at VA 0xC6980: if `lang >= 5` it asks the system language; it opens
`ft_<lang>.mrg` as archive slot 3 (0xE4FA0), then for k = 0..64 creates texture slot
`0x21 + k` from section k (0xD8B80, loop `ebx = 0x22 .. 0x61`). So:

```
slot 0x21           = section 0   (blank 256x256 page)
slot 0x22 .. 0x61   = sections 1 .. 64 (1024x1024 pages)
page number used below = section number = slot - 0x21
```

---

## 3. Page container (one section of ft_<lang>.mrg)

Each of the 65 sections is a nested mrgd00 with exactly 2 sections [V-all, 325 pages].

### 3.1 Section 0: header + palette (1041 bytes)

This is the generic image header of `images.md` section 2; the field names below are the ones
used there. Values are those of all 325 font pages [V-all].

| off | size | type | name (images.md) | value / meaning |
|---:|---:|---|---|---|
| 0x00 | 2 | u16 | `width` | 1024 (256 for page 0) |
| 0x02 | 2 | u16 | `height` | 1024 (256 for page 0) |
| 0x04 | 2 | u16 | `tile_w` | = width |
| 0x06 | 2 | u16 | `tile_h` | = height |
| 0x08 | 2 | u16 | `tiles_x` | 1 |
| 0x0A | 2 | u16 | `tiles_y` | 1 |
| 0x0C | 2 | u16 | `bmp_type` | 1 = 8-bit (or 4-bit) indexed with a palette in this header |
| 0x0E | 1 | u8 | `depth` | 0x01 = 8 bpp (low nibble), no deferred-decode bit |
| 0x0F | 1 | u8 | `attr` | 0 (no tile border) |
| 0x10 | 1024 | 256 x u8[4] | palette | R G B A, **stored in PS2 CSM1 order** |
| 0x410 | 1 | u8 | tile flags (`tiles_x*tiles_y` = 1 byte) | 1 = tile has non-opaque pixels (page has glyphs), 0 = empty tile (blank page; images.md 2.3: never decoded or drawn) [V-all: flag = presence of ink on 325/325 pages] |

Section size = 16 + 1024 + 1 = 1041 [V-all]. (An earlier version of this document called
0x0C/0x0E "type_a/type_b" as two u16 = 1/1 and byte 0x410 a "page has glyphs" flag; the bytes
are the same, the names above are the engine's, see images.md.)

**Palette order (CSM1).** Pixel index `i` uses stored entry `csm1(i)`:

```
csm1(i) = (i & 0xE7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1)     # swap bits 3 and 4
```

Evidence [V-all, 325 pages]: with the swap, the alpha of the palette entries that pixels
actually use is non-decreasing in index order on all 325 pages (without the swap on only
130), and page 64's palette becomes the exact ramp `alpha(i) = i >> 1`.

**Colour.** Every palette entry with alpha > 0 that pixels use is white (FF FF FF)
[V-all]. Entries no pixel uses are `00 00 00 7F` on the 195 non-blank pages and on the five page-0 copies, but
`00 00 00 00` on the 125 blank 1024x1024 pages (ja 23-25, 55-60; en 12-25, 27-60; zt 20-23;
ko 1-64) [V-all, test_font.py::test_page_layout_all_languages]. Neither is ever referenced.

**Alpha.** PS2-style, 0x80 = 1.0; the highest value present on font pages is 0x7F. The engine
converts the palette in place once when the image is loaded (VA 0xD90B0..0xD90CE) [E]:

```
A' = 0xFF if A == 0x80 else (2*A) & 0xFF        # font pages: 0x00..0x7F -> 0x00..0xFE
```

tsuki's `alpha_8bit` implements exactly this. Glyphs are pure coverage masks; colour comes
from the renderer.

### 3.2 Section 1: pixels

MZX0 stream (plain, not inverted), decompressed size = width*height, 8-bit palette
indices, row-major, top row first, no swizzle/tiling [V-all: 325 streams decode to exactly
w*h; glyphs render upright].

### 3.3 Worked example: ft_en.mrg section 1 (page 1)

```
ft_en.mrg
00000000: 6d 72 67 64 30 30 41 00  00 00 00 00 01 00 48 06   "mrgd00", 65 sections; s0
00000010: 00 00 50 06 2c 00 78 58  ...                         s1 = (0, 0x650, 0x2C, 0x5878)
section 1 at 0x860, 88,184 bytes:
00000860: 6d 72 67 64 30 30 02 00  00 00 00 00 01 00 11 04    "mrgd00", 2 sections, s0 1041 B
00000870: 00 00 18 04 2c 00 46 54  00 04 00 04 00 04 00 04    s1 at +0x418; w h tile_w tile_h = 1024
00000880: 01 00 01 00 01 00 01 00  00 00 00 7f 00 00 00 7f    tiles 1x1, bmp_type 1, depth 01 attr 00;
                                                               palette[0] = 00 00 00 7F
... palette tail: ff ff ff 78  ff ff ff 79  ff ff ff 7e  ff ff ff 7f | 01  (tile flag = 1, at file offset 0xC88)
pixel stream: 4d 5a 58 30 00 00 10 00 ...   "MZX0", 0x100000 bytes = 1024*1024
```

### 3.4 Page geometry

```
upper half  y   0..511 : glyph "fill" images
lower half  y 512..1023: the same images dilated (outline), same x, y + 512
```

[V-all] For every non-blank page of every language, every pixel with alpha > 0 in the upper
half also has alpha > 0 at +512 (the lower half is a superset). The engine draws the outline
with UV `(x, y + 0x200) .. (x2, y2 + 0x200)` (VA 0xC9D4C..0xC9D9B) [E].

Fixed grids use **73 x 73 px cells**: 14 columns x 7 rows = 98 cells per half, cell (c, r)
at `(73c, 73r)`; 14*73 = 1022 and 7*73 = 511, so columns 1022..1023 and row 511 of each half stay empty. Proportional
glyphs (section 5.3) use arbitrary rectangles, always 73 px high and inside the upper half
[V-all, all 6,771 in-range records of the 17 tables, pages 1..64]. The 6,666 ja/en/zc/zt records
point at non-blank pages; the 105 ko records (ko_s0/s1/s2) point at ft_ko pages, which are all
blank. Special pages 62..64 use a 68 x 73 grid (14 x 7).

### 3.5 What is on which page

| pages | ja | en | zc | zt |
|---|---|---|---|---|
| 0 | blank 256x256 (identical in all 5 files) | same | same | same |
| 1-2 | style 0 narrow (ASCII + Latin-1, mincho, 104) | style 0 narrow: sans (109) | style 0 narrow (109) | style 0 narrow (109) |
| 3-10 | style 1 (gothic, 701) | 3-4 style 1 serif; 5-6 style 4 serif italic; 7-8 style 3 sans italic; 9-10 style 5 italic (111 each) | style 1 (769) | 3-19: styles 1 and 2 (1631) |
| 11-22/25 | 11-22 style 2 (gothic, 1174) | 11 style 2 compact sans (82) | 11-25 style 2 (1434) | |
| wide grid | 26-54 style 0 wide (mincho, 2795) | 26: 6 symbols | 26-60 (3403) | 24-60 (3570) |
| blank | 0, 23-25, 55-60 | 0, 12-25, 27-60 | 0 | 0, 20-23 |
| 61 | specials: tag pictures, joined-dash pieces | same bytes | same | same |
| 62-64 | redaction-bar frames, [zap00] scribble | same bytes | same | same |

Non-blank pages per file [V-all]: ja 55, en 16, zc 64, zt 60, ko 0. Sections 0 and 61..64
are byte-identical in ja/en/zc/zt (SHA-1 compared). Within each page the upper half holds the
glyphs, the lower half their outlines.

**Placeholder pictures and empty cells (game data, not a decoding error)** [V-all,
`scratch/fonts/repair/placeholders.py`, sheet `placeholders.png` Read]. Some table entries
exist (so the character is "found" and no fallback happens) but draw a placeholder or nothing:

| lang, style 0 | characters | what is drawn |
|---|---|---|
| en (`en_wide`, page 26 cells 2..5) | ☆ ♡ ♥ ♪ (U+2606, U+2661, U+2665, U+266A) | the same boxed "?" tofu picture |
| zc (`narrow` -> `zc_s0`, page 2) | ¶ â ä ë | the same crossed-box tofu |
| zc (`zc_wide`, page 26) | ♡ ♥ ♪ | the same crossed-box tofu |
| zt (`narrow` -> `zt_s0`, page 2) | ¶ â ä ë | all four share one record (page 2, x 0, y 0): a "-" glyph |
| zt (`zt_wide`, page 24 indices 2, 4, 7, 8, 9) | ・ ― ♡ ♥ ♪ (U+30FB, U+2015, U+2661, U+2665, U+266A) | empty cell (no ink) |
| ja (`ja_wide`, page 29 index 295) | ⁉ (U+2049) | empty cell |

None of these characters occurs in script_text in that language (0 occurrences, including ruby
readings). Related dash quirks [V-all, all 9 pieces x 3 styles x 4 languages checked for ink]:

- zt: `―` draws **nothing at all**: its wide cell is empty *and* the zt row of the joined-dash
  pieces (page 61, y = 292, variants 3..5, 5.6) has no ink. zt writes its dashes with `─`
  (U+2500, 9,164x in script_text; 0 `―`), whose pieces (variants 6..8) and wide glyph have ink.
- ja / en / zc: `─` is in none of their tables, so a lone `─` falls back to the blank cell
  (5.4), but runs of two or more `─` are drawn with the joined pieces (5.6), which have ink in
  every row. These languages use `―` (ja 15,637x, en 1,091x, zc 1,457x) and never `─`.
- `■` has ink as a single glyph and as pieces in all four languages.

Separately, the ja Roman numerals Ⅰ Ⅴ Ⅹ reuse the cells of fullwidth Ｉ Ｖ Ｘ (intentional,
same shape).

Atlas order (informational, the tables give the index explicitly): record-based sets are in
Unicode order (e.g. ja style 1: 700/700 consecutive pairs increasing); the ja wide grid is in
Shift-JIS order (2769/2791 increasing pairs among encodable characters: punctuation 、。，．…
first, then symbols, digits, Latin, kana, kanji); zc/zt wide grids put their Shift-JIS-encodable
characters in Shift-JIS order too (2187/2196 and 3140/3152) [V].

---

## 4. Character keys

The engine never converts UTF-8 to code points. A character's **key** is its UTF-8 bytes
packed big-endian into an integer (VA 0x11B00, 0x11750) [E]:

```
lead < 0x80          : key = b0                                  (1 byte)
lead & 0xE0 == 0xC0  : key = b0<<8  | b1                         (2 bytes)
lead & 0xF0 == 0xE0  : key = b0<<16 | b1<<8 | b2                 (3 bytes)
lead & 0xF8 == 0xF0  : key = b0<<24 | b1<<16 | b2<<8 | b3        (4 bytes)
otherwise            : invalid lead 0x80..0xBF / 0xF8..0xFF (the glyph routine returns 0 bytes
                       consumed, 5.4)
```

Continuation bytes are not checked; a sequence cut short by the end of the string reads the NUL
terminator and possibly the bytes after it as its trailing bytes (tsuki reads zeros there).

Examples: "A" = 0x41, "é" = 0xC3A9, "上" = 0xE4B88A, "―" (U+2015) = 0xE28095.
`key >= 0x10000` means "3- or 4-byte character" (U+0800 and up); this test selects the
wide grid (5.2).

---

## 5. Glyph tables in eboot.bin

### 5.1 Lookup arrays (16-byte records, data segment)

`lookup(lang, key, style) -> index` at VA 0x10810 [E] is a binary search over:

| off | size | type | meaning |
|---:|---:|---|---|
| 0 | 8 | u64 | key (section 4) |
| 8 | 2 | s16 | glyph index |
| 10 | 6 | - | zero |

Keys are strictly increasing; in every array the indices are a permutation of 0..n-1
[V-all]. Search: `lo = 0, hi = n-1; while hi >= lo: mid = (lo+hi)/2; equal -> return
index; key < wanted -> lo = mid+1 else hi = mid-1`; not found -> -1.

| name | VA | file offset | n | referenced at |
|---|---|---|---:|---|
| `narrow` | 0x626C10 | 0x38CBB0 | 109 | 0x108C8 (also 0x10A7E, 0x10AEB, 0x10B5B, 0x10BCD) |
| `ja_s1` | 0x6272E0 | 0x38D280 | 701 | 0x10C91 |
| `en_s1` | 0x629EB0 | 0x38FE50 | 111 | 0x10D9E (+3 more) |
| `zc_s1` | 0x62A5A0 | 0x390540 | 769 | 0x10CEE |
| `zt_s1` | 0x62D5B0 | 0x393550 | 1631 | 0x10D49 |
| `ko_s1` | 0x633BA0 | 0x399B40 | 1 | 0x10C29 |
| `ja_s2` | 0x633BB0 | 0x399B50 | 1174 | 0x10848 |
| `en_s2` | 0x638510 | 0x39E4B0 | 82 | 0x10DFE |
| `zc_s2` | 0x638A30 | 0x39E9D0 | 1434 | 0x10930 |
| `zt_s2` | 0x63E3D0 | 0x3A4370 | 1631 | 0x109A1 |
| `ko_s2` | 0x6449C0 | 0x3AA960 | 1 | 0x10A0C |
| `ja_wide` | 0x6449D0 | 0x3AA970 | 2795 | 0x10F7E |
| `en_wide` | 0x64F880 | 0x3B5820 | 6 | 0x110F7 |
| `zc_wide` | 0x64F8E0 | 0x3B5880 | 3403 | 0x10FDE |
| `zt_wide` | 0x65CD90 | 0x3C2D30 | 3570 | 0x1103E |
| `ko_wide` | 0x66ACB0 | 0x3D0C50 | 5 | 0x1109E |

The arrays are contiguous (each ends where the next begins) and hold **17,423 entries in
total, 5,571 distinct keys** [V-all]. `zt_s1` and `zt_s2` have identical contents.
`ko_s1`/`ko_s2` hold only U+3000; `ko_wide` holds `　・―글한`.
n = the `hi` immediate + 1 of each search; the "referenced at" addresses are
`lea r64, [rip+disp32]` instructions, which `EbootFontTables.verify()` checks to detect a
different eboot build.

Dispatch [E, EMU]:

```
lookup(lang, key, style):
  lang 0..4:  if style == 0: table = (key >= 0x10000) ? <lang>_wide : narrow
              elif style == 2: table = <lang>_s2
              else:            table = <lang>_s1        # styles 1, 3, 4, 5 (and > 5)
  lang 5:     return key                                # "tag language", see 5.5
  other:      return -1
```

`narrow` (109 keys: ASCII 0x20..0x7E, `°±¶·×àâäèéêëó÷`) is shared by every language's
style 0; its index selects a record in the language's own style-0 record table (5.3).

Example (`narrow`, VA 0x626C10):

```
00626c10: 20 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00    ' '  -> 0
00626c20: 21 00 00 00 00 00 00 00  01 00 00 00 00 00 00 00    '!'  -> 1
00626e20: 41 00 00 00 00 00 00 00  21 00 00 00 00 00 00 00    'A'  -> 33
ja_wide:
00645880: 8a b8 e4 00 00 00 00 00  2b 05 00 00 00 00 00 00    '上' (0xE4B88A) -> 1323
```

### 5.2 Wide grid (style 0, key >= 0x10000)

`get_glyph(lang, key, &tex, &adj, &rect, style, kind)` at VA 0x11150 [E, EMU]:

```
rect = (0, 0, 73, 73); tex = 0xFFFF; adj = 0
if lang >= 5: tag path (5.5) or fail
idx = lookup(lang, key, style)
if style != 0 and idx < 0:  idx = lookup(lang, key, 0); style = 0      # fall back to style 0
if idx < 0: return -1
if key >= 0x10000 and style == 0:
    tex = WIDE_BASE[lang] + idx / 98            # u16[5] at VA 0x187E2E = 59, 59, 59, 57, 59
    x = (idx % 14) * 73
    y = (idx % 98 / 14) * 73
    w = h = 73
    if lang == 0 and 0xEFBDA1 <= key <= 0xEFBE9F:   # halfwidth katakana U+FF61..U+FF9F
        x += 15; w = 42
        if (key & ~1) == 0xEFBE9E: adj = -2.0 * scale   # U+FF9E/FF9F (float at VA 0x177268)
    return 1
```

So the wide grid starts at page 26 (slot 59) for ja/en/zc/ko and page 24 (slot 57) for zt.
(The halfwidth-katakana branch is dead on retail data: no halfwidth katakana is in `ja_wide`.)

Example: ja "上" -> index 1323 -> slot 59 + 13 = 72 (page 39), cell 1323 % 98 = 49 ->
column 7, row 3 -> rect (511, 219, 73, 73).

### 5.3 Rectangle records (10 bytes, text segment)

All other glyphs (key < 0x10000 in style 0, every key in styles 1..5):

| off | size | type | meaning |
|---:|---:|---|---|
| 0 | 2 | u16 | texture slot (page = slot - 0x21) |
| 2 | 2 | u16 | x (left edge of the crop, pixels) |
| 4 | 2 | u16 | y (top of the cell, upper half) |
| 6 | 2 | s16 | w (crop width = advance basis) |
| 8 | 2 | u16 | h (always 73) |

`record = table + index * 10`, **no bounds check** [E]. Table choice (jump table at VA
0x177274, second-level 0x177288) [E, EMU]:

| lang | style 0 | style 2 | style 1 / others | italics |
|---|---|---|---|---|
| ja | `ja_s0` 0x177370 (104) | `ja_s2` 0x177780 (1174) | `ja_s1` 0x17A560 (701) | - |
| en | `en_s0` 0x17C0D0 (109) | `en_s2` 0x17C520 (82) | `en_s1` 0x17D580 (111) | style 3 `en_s3` 0x17C860, 4 `en_s4` 0x17CCC0, 5 `en_s5` 0x17D120 (111 each) |
| zc | `zc_s0` 0x17D9E0 (109) | `zc_s2` 0x17DE30 (1434) | `zc_s1` 0x181640 (769) | - |
| zt | `zt_s0` 0x183450 (109) | `zt_s1` 0x1838A0 (1631) | `zt_s1` | - |
| ko | `ko_s0` 0x187860 (103) | `ko_s2` 0x187C66 (1) | `ko_s1` 0x187C70 (1) | - |

File offset = VA + 0x2280. **Counts.** A record table has no count of its own; the index
comes from the lookup array, so a table's size is the size of the lookup array(s) that feed
it (`narrow` = 109 for every `<lang>_s0`, `<lang>_s1/_s2` sizes for the others). The tables are
laid out back to back, mostly 16-byte aligned with 4..14 zero bytes of padding after the last
record (so "distance to the next table / 10" is *not* the count: 702.4 for ja_s1, 110.4 for
en_s0, 112.0 for en_s1/3/4/5, ...). Two tables are shorter than their index range: `ja_s0`
ends exactly 1040 bytes = 104 records after its start, where `ja_s2` begins, and `ko_s0`
ends 1030 bytes = 103 records later, where `ko_s2` begins [V-all,
`scratch/fonts/repair/record_gaps.py`]. That is the out-of-range bug below.

All 6,771 records (17 tables) have h = 73, w in 15..74, y + h <= 512 and x + w <= 1024, on
pages 1..64 [V-all]. The 6,666 ja/en/zc/zt records point at non-blank pages; the 105 ko
records point at ft_ko pages, which are all blank.

`get_glyph` loads each table with a 7-byte `lea r64, [rip+disp32]`; `EbootFontTables.verify()`
checks that every site still resolves to the documented VA:

| table | lea site | table | lea site | table | lea site |
|---|---|---|---|---|---|
| `ja_s0` | 0x1146E | `en_s3` | 0x114CB | `zc_s1` | 0x114AC |
| `ja_s2` | 0x11315 | `en_s4` | 0x114D6 | `zt_s0` | 0x1148F |
| `ja_s1` | 0x114A3 | `en_s5` | 0x114E1 | `zt_s1` | 0x113F1 |
| `en_s0` | 0x11479 | `en_s1` | 0x114C0 | `ko_s0` | 0x1149A |
| `en_s2` | 0x113B5 | `zc_s0` | 0x11484 | `ko_s2` | 0x11414 |
| | | `zc_s2` | 0x113D8 | `ko_s1` | 0x114B5 |

Further verified references: `WIDE_BASE` (u16[5] at VA 0x187E2E) via `lea rdx` at 0x111F7;
the tag-name table (VA 0x187C80) via `lea r15` at 0x117B1 [E].

**English italics (adj)** [E, EMU]: for lang 1 and style 3/4/5 the routine also looks the key
up in the matching upright set and stores `adj = (w_upright - w) * scale`:

```
style 3: upright = narrow/en_wide + en_s0 records   (w_upright = 73 if key > 0xFFFF)
style 4: upright = en_s1 lookup + en_s1 records
style 5: upright = en_s2 lookup + en_s2 records
(if the key is missing upright, adj stays 0)
```

Example records:

```
en_s0[33] 'A' at 0x17C21A: 22 00 73 01 92 00 3d 00 49 00  -> slot 0x22 (page 1), x 371, y 146, w 61, h 73
ja_s0[33] 'A' at 0x1774BA: 22 00 78 01 92 00 33 00 49 00  -> slot 0x22, x 376, y 146, w 51, h 73
en 'A' style 3 -> en_s3: slot 0x28 (page 7) (371, 146, 69, 73), adj = 61 - 69 = -8
```

**Engine bug, out-of-range records** [E, EMU, V]: `narrow` has 109 entries but `ja_s0` only
104 records and `ko_s0` only 103. Japanese style 0 `° â ä ê ë` (indices 104..108) read the
first `ja_s2` records instead (e.g. "â" -> slot 0x2C (94, 0, 31, 73), a page-11 glyph). None
of these characters occurs in the Japanese script (0 of 1,104,925 characters). tsuki
exposes `Glyph.in_range` and, by default, draws such glyphs as the blank fallback.

### 5.4 Character decoding and fallback

`char_glyph(str, &tex, &adj, &rect, style, kind)` at VA 0x11750 [E, EMU], language from the
global u16 at VA 0x66C59C:

```
if str[0] == 0:                                           # VA 0x117FA
     tex = 0xFFFF, rect = (0, 0, 73, 73), adj = 0; return 0
if str[0] == '[': compare with the 39 tag names (char[11] each at VA 0x187C80, strlen +
     strncmp):
     [heart] [heart2] [star] [star2] [edia] [dummy] [onpu] [ber00]..[ber30] [zap00]
     match k -> tag glyph k (5.5), return strlen(tag)
     no match -> '[' is an ordinary character
decode the key (section 4), n = 1..4 bytes
if the lead byte is invalid (0x80..0xBF, 0xF8..0xFF):     # VA 0x118E0..0x118ED
     return 0 and leave tex / adj / rect untouched
if get_glyph(lang, key, style, kind) fails:
     tex = 0x22 (page 1), rect = (0, 0, w, 73), adj = 0, with w = 42 for 1-2 byte
     characters and 73 for 3-4 byte characters (page 1 cell (0,0) is the blank space glyph)
return n
```

A caller that gets 0 back makes no progress: the layout loop (7.1) stops at NUL before calling
this, but an invalid lead byte would make it spin on the same byte until its element budget
runs out. tsuki's `char_glyph` returns `consumed == 0` in both cases exactly like the engine
(`Glyph.source` = `'nul'` with the engine's outputs, or `'invalid'`, where tsuki fills tex
0xFFFF and an empty rect because the engine leaves its outputs unchanged) [EMU: 2,190 cases,
0 differences]; `Font.layout` raises ValueError on an invalid lead (only reachable through
`@1` in front of a multi-byte character, 7.1).

### 5.5 Tag glyphs (page 61 / 62 / 64) [E, EMU]

| k | tag | slot | rect (x, y, w, h) | adj |
|---:|---|---|---|---|
| 0..3, 5, 6 | [heart] [heart2] [star] [star2] [dummy] [onpu] | 0x5E (page 61) | (73k, 0, 73, 73) | 0 |
| 4 | [edia] (ë) | 0x5E | (307, 0, 46, 73) | 0 |
| 7..37 | [ber00]..[ber30], j = k-7 | 0x5F (page 62) | ((j%14)*68, (j/14)*73, 68, 73) | +5*scale (kind < 2), -0.5*scale (kind >= 2) |
| 38 | [zap00] | 0x61 (page 64) | (0, 219, 68, 73) | same as ber |

The adj factor is `F[kind >= 2]` (a `setge` on the kind, so every kind >= 2 behaves like the
ruby kind 2) from a float pair {5.0, -0.5}. `char_glyph` reads the pair at VA 0x177258 /
0x17725C (0x119BF..0x119D2); the identical "language 5" branch of get_glyph (VA 0x112C1, key =
tag index) reads a duplicate pair at VA 0x177250 / 0x177254 (0x1137B) [E]. The floats at
0x177250.. are 5.0, -0.5, 5.0, -0.5, 5.0, -0.5, -2.0, 5.0, so both give the same result. The
dash pieces (5.6) use the same rule.

**Redaction bars are substituted at draw time** [E: VA 0xC9401..0xC9732]. The [berNN] rect
above only reserves layout space. When a text block is drawn, the engine counts the quads
with slot >= 0x5F that are already revealed, `n = min(count, 21)`, `base = n(n-1)/2`, and
gives the i-th such quad (i counted over the block, wrapping at 21):

```
[zap00] (slot 0x61, y >= 219): slot 0x61, x = (i % 14) * 68, y = 219, 68 x 73
[berNN]:                       k = base + i
                               slot = 0x5F + k / 98, x = (k % 14) * 68, y = (k % 98 / 14) * 73
```

So pages 62, 63, 64 (rows 0..2) store, for every bar length n = 1..21, an n-segment
hand-drawn bar at cells T(n-1) .. T(n-1)+n-1 (231 cells in all), and successive [zap00]
glyphs walk along the scribble on page 64 row 3. The tag number is irrelevant to the picture.
Rendered: `samples/fonts/en_redaction_bars.png` (n = 1, 2, 3, 5, 8 and 8x [zap00]: one
continuous bar / scribble each). Script usage: [ber00] 52x, [heart2] 30-31x, [heart] 18x,
[star2] 14-17x, [onpu] 9-11x, [zap00] 8x (ja / en script_text) [V].

### 5.6 Joined dashes (page 61) [E, EMU]

Runs of U+2015 `―`, U+2500 `─` and U+25A0 `■` are drawn as continuous strokes.
`dash_variant(cur_ptr, prev_ptr, line_start, line_end, flag_b)` (VA 0x11B80) works on **raw
string pointers** and compares 3-byte keys only:

```
key3(p)  = (p != NULL and (p[0] & 0xF0) == 0xE0) ? p[0]<<16 | p[1]<<8 | p[2] : 0
cur      = key3(cur_ptr)                   # must be ―/─/■ (else return -1)
prev     = key3(prev_ptr)                  # VA 0x11BDD
next     = key3(cur_ptr + 3)               # VA 0x11C11: the bytes right after cur
base     = {■ 0xE296A0: 0, ― 0xE28095: 3, ─ 0xE29480: 6}[cur]
has_prev = (prev == cur) and not line_start
has_next = (next == cur) and not line_end
both -> base+1 (middle); prev only -> base+2 (end), or base+0 if flag_b;
next only -> base+0 (start), or base+2 if flag_b; neither -> -1 (draw the normal glyph)
```

The caller (layout loop, VA 0xC7824..0xC78BB) passes [E]:

- `cur_ptr` = the current element, `prev_ptr` = **the start of the previous parse element**
  (`mov rsi, rbx` at VA 0xC76A4 at the end of every element; NULL before the first element,
  `xor esi, esi` at 0xC7508). An element is a glyph *or* a control: a markup code `@x`, `^`,
  `<`, `|`, `>`. So a code between two dashes breaks the run: `―@r―` draws two plain ― glyphs,
  `――@r――` two separate joined pairs (`samples/fonts/ja_dash_prev_rule.png`). `next` is read
  from the raw bytes, so `―@r` has no next either.
- `line_start` = `pen <= 0` (`0 >= pen`, VA 0xC78A2..0xC78B8).
- `line_end` = `pen >= box_width - 160*sx` **and** the pass-1 break class of the next element
  (byte array at VA 0x178C100, value % 10) is neither 2 nor 3 **and** automatic wrapping is on
  (no `@o`) **and** kind < 2 (VA 0xC7824..0xC789E; box_width = local [rbp-0xE8] from 0x538F0 or
  `@f`, float -160.0 at 0x2D2FB4). It is false for any line that does not reach the right
  margin; tsuki has no text box and passes false.
- `flag_b` = the byte at VA 0x178BE2C, set to 1 by `@b` (0xC8686, ignored when caller flag
  0x40 is set) and cleared by `@r`. The engine also sets bit 16 of each glyph's quad flags while
  it is set (0xC7B39); what `@b` means visually is not established [H: mirrored text].

tsuki: `Font.dash_variant_at(b, pos, prev_pos, line_start, line_end, flag_b)` is this exact
function; `Font.dash_variant(key, prev_key, next_key, ...)` is the same on pre-read keys
[EMU: 307,012 raw-pointer cases on random strings of dashes, kana, ASCII, markup, tags and
4-byte characters, prev = NULL included, 0 differences].

Piece rectangle (VA 0x11ED0), slot 0x5E:

```
part = v % 3
x = xb + (v / 3) * 146 + {0, 41, 78}[part];   w = {73, 68, 68}[part];   h = 73
y  = ja: 146 if style == 2 else 73;  zc: 219;  zt: 292;  en, ko: 73
xb = 511 for en/ko styles 3..5 (slanted pieces), else 0
adj = 0 for part 0, else +5*scale (kind 0, 1) / -0.5*scale (kind 2)   [EMU: kinds 0..2]
```

Example: `――――` in Japanese -> variants 3, 4, 4, 5 -> one unbroken line
(`samples/fonts/ja_tags_dashes.png`).

### 5.7 Verification of sections 5.1-5.6

`scratch/fonts/verify_emu.py` maps the eboot segments into unicorn (strlen/strncmp stubs for
the two libc imports) and calls the game's functions directly:

- `get_glyph` 0x11150: 5 languages x 6 styles x 2 scales x 5,580 keys (every key of every
  table plus 16 absent ones) = 334,800 calls, **0 differences** from `Font.get_glyph`.
- `char_glyph` 0x11750: 146,040 strings (characters, all 39 tags, `[foo]`, `[`), kinds 0
  and 2, **0 differences**.
- dash pieces 0x11ED0: 1,620 (lang x style x kind x variant x scale), 0 differences;
  dash variants 0x11B80: 1,440 combinations of cur/prev/next/line flags, 0 differences.
- `scratch/fonts/repair/verify_emu_edge.py` (after review): `char_glyph` on NUL and on every
  invalid lead byte 0x80..0xBF / 0xF8..0xFF, 5 languages x 3 styles x 2 kinds = 2,190 cases
  (engine: 0 consumed; NUL -> tex 0xFFFF, rect (0,0,73,73); invalid -> outputs untouched),
  0 differences; `dash_variant` 0x11B80 with raw pointers into 3,000 random strings (307,012
  calls, prev = any earlier offset or NULL), 0 differences from `Font.dash_variant_at`.
  Re-run of `verify_emu.py` after the repair: 334,800 / 146,040 / 1,620 / 1,440, 0 differences
  (`scratch/fonts/repair/verify_emu_rerun.log`).

Ink check [V-all]: on every page 1..60 of ja, en, zc, zt, every pixel with alpha > 0 in the
upper half lies inside a rectangle that the tables can produce (0 uncovered pixels), i.e.
there are no further glyph tables. Pages 61..64 contain ink that only the draw-time
substitution (5.5) and the other languages' dash rows reach.

Script coverage [V-all, `scratch/fonts/repair/script_check.py`, log `script_check.log`]: every
one of the **43,969 lines per language** of script_text.mrg (split with `tsuki.text.ScriptText`)
laid out with `Font.layout(markup=True)` (engine markup and ruby rules, section 7.1). "base" =
glyph elements on the line, "ruby" = reading glyphs; a tag ([heart] ...) counts as one glyph,
a dash piece as one glyph.

| lang | base glyphs | ruby glyphs | distinct chars | wide grid | records | dash pieces | tags | fallback (missing) | out-of-range |
|---|---:|---:|---:|---:|---:|---:|---:|---|---:|
| ja | 1,097,625 | 7,432 | 2,719 | 1,077,318 (+7,383 ruby) | 4,317 (+49) | 15,854 | 136 | 0 | 0 |
| en | 2,626,556 | 0 | 85 | 1,070 | 2,624,873 | 481 | 132 | 0 | 0 |
| zc | 849,490 | 831 | 3,434 | 843,858 (+825) | 3,822 | 1,669 (+6) | 135 | 6: `ｂ`x3, `ａ`x2, `鲽`x1 | 0 |
| zt | 878,933 | 1,511 | 3,558 | 865,469 (+1,300) | 3,954 (+205) | 9,373 (+6) | 136 | 1: `鰈`x1 | 0 |
| ko slot | 43,969 | 0 | 1 (U+3000, blank page 26) | 43,969 | 0 | 0 | 0 | 0 | 0 |

The same run checks the layout against the independent markup tokenizer of the text agent
(`tsuki.text.tokenize`): the sequence of drawn glyphs (base, then reading, tags as one item)
equals the tokenizer's text / ruby / icon tokens on all 175,876 lines (0 mismatches). English
draws 236 `<` and 237 `>` as glyphs; `^` breaks: ja 4, en 175 (170 lines), zc 6, zt 5. No
placeholder or empty-cell character of 3.5 occurs. (These numbers relate to the earlier
"characters" count of this document, 43,970 strings with markup removed by regex, as: base +
ruby = old count + tags - `^` (+ `<` + `>` for en) (- 1 stray digit of `@c5` for zc/zt); the
earlier string count included one empty guard entry.)

---

## 6. Styles and markup

The style is a global (VA 0x178BE24) [E], set to `(caller_flags >> 19) & 2` at the start of
every layout call (0xC6C63; text boxes with flag bit 20 start in style 2):

| style | set by | ja | en | zc / zt |
|---:|---|---|---|---|
| 0 | default | mincho (wide grid p26-54) + mincho monospace ASCII (p1-2) | sans (p1-2) | wide grid + ASCII |
| 1 | `@g` | gothic set (p3-10) | serif (p3-4) | style-1 set |
| 2 | `@z`, or caller flag bit 20 | gothic set 2 (p11-22) | compact sans (p11) | style-2 set |
| 3 | `@i` from 0 | = style 1 tables | sans italic (p7-8) | = style 1 |
| 4 | `@i` from 1 | = style 1 | serif italic (p5-6) | = style 1 |
| 5 | `@i` from 2 | = style 1 | italic of style 2 (p9-10) | = style 1 |

- `@i` (VA 0xC872B): if `get_language() == 1` (English): 1 -> 4, 2 -> 5, anything else -> 3;
  in every other language it selects style 1.
- `@r` (VA 0xC87B9): 4 -> 1, 5 -> 2, anything else -> 0; also clears the `@a` value
  (0x178BE28) and `flag_b` (0x178BE2C) and restores colour and scales.
- `@g` -> 1 (0xC877E), `@z` -> 2 (0xC8A4A), `@b` -> flag_b = 1 (5.6).
- A style whose table lacks the character falls back to style 0 (5.2).
- Usage in script_text: en `@i` 1,288, `@r` 1,288, `@g` 128; ja `@g` 127 (no `@i`) [V].
- The other codes do not change which glyph is drawn: `@c<n>` colour (RGB565 table at
  0x2D34C0), `@h<n>`/`@w<n>`/`@s<n>` scales (float table at 0x2D3730), `@f<n>` box width,
  `@a<n>` animation, `@o`/`@p` automatic wrap off/on, `@t`/`@u`/`@l` typewriter counter,
  `@x`/`@y` position, `@k`/`@T` no-ops. Their byte lengths are in 7.1; their effects are
  described in `text.md` section 6.

---

## 7. Layout and drawing

The layout function (VA 0xC6C20) makes two passes over the string; pass 1 (0xC6D80..0xC7375)
only classifies line-break opportunities into the byte array at VA 0x178C100. Pass 2
(0xC76C0..0xC8DE1) walks the string **element by element** and places the glyphs [E].

### 7.1 Element loop (pass 2)

```
lang  = get_language()            # 0xC6D58 -> local [rbp-0xC4]; 1 = English
kind  = 0                         # local [rbp-0x54], reset at 0xC754F: 0 normal, 1 ruby base,
                                  #   2 ruby reading; passed to char_glyph / dash piece as "kind"
prev  = NULL                      # rsi (0xC7508)
literal_caret = 0                 # byte VA 0x178CF10
p = start of the string
loop:
    c = *p
    if c >= 0x80:                         goto GLYPH                      # 0xC76DA
    if c == '@':                          q = code_end(p) (table below); apply the code; goto NEXT
    if c == '^':                                                         # 0xC76EA -> 0xC7810
        if literal_caret: literal_caret = 0; goto GLYPH                  # drawn as a '^' glyph
        LINE BREAK (rules below); q = p + 1; goto NEXT
    if c == 0:                            end of text                     # 0xC76F7
    if lang != 1 and c == '<':            measure base and reading ahead; kind = 1; q = p + 1; goto NEXT
    if c == '|' and kind > 0:                                            # 0xC7EE7 (any language)
        q = p + 1
        if kind == 1:
            kind = 2
            if caller flag 0x2000:       skip bytes up to and including '>' (or NUL); kind = 0
        goto NEXT                         # kind 2: a further '|' is swallowed
    if lang != 1 and c == '>':            kind = 0; q = p + 1; goto NEXT  # 0xC82D2 (also outside a group)
GLYPH:
    v = dash_variant(p, prev, line_start, line_end, flag_b)              # 5.6
    if v >= 0:  (tex, rect, adj) = dash_piece(v, style, kind, sx); n = 3
    else:       n = char_glyph(p, &tex, &adj, &rect, style, kind, sx)    # 5.4
    place the glyph (7.2; kind 2 = ruby reading); q = p + n
NEXT:
    prev = p; p = q                                                       # 0xC76A4
```

So **in English `<`, `>` and `|` are ordinary glyphs** (`|` only does something when kind > 0,
which only `<` can set) [E: 0xC6D58/0xC6D65 store the language, 0xC7700 and 0xC82D2 skip the
`<` and `>` handlers when it is 1; V: 236 `<` and 237 `>` in en script_text, e.g. line 6570
(a quoted foreign-language sentence of 46 characters, `"<I shall ...>"`) and the 15 `<->` placeholders,
`samples/fonts/en_line6570_angle.png`, `en_line2564_angle.png`]. In ja/zc/zt/ko, `>` is
swallowed even outside a group.

`@` codes, bytes consumed (jump table at VA 0x2D32C0, index = byte - 0x31 for '1'..'z'; any
other byte takes the default handler 0xC8625) [E]:

| code | consumed | note |
|---|---|---|
| `@a` `@c` `@f` `@h` `@w` | 3 | one parameter byte, not checked for being a digit |
| `@s` | up to and including the first `,` `)` or NUL among the next 256 bytes; 258 if none | number with optional `-` |
| `@x` `@y` | 2 + 0..3 decimal digits | |
| `@1` | 2 | writes `^` over the next byte and sets literal_caret (0xC77F4): that `^` is drawn as a glyph |
| `@2` | 2 | writes `)` over the next byte (0xC861B) |
| `@b` `@g` `@i` `@k` `@l` `@o` `@p` `@r` `@t` `@u` `@z` `@T` | 2 | |
| any other byte X | 2, or if `p[2] == '('` and a `)` occurs in `p[3] .. p[257]` before any NUL: up to and including that `)` | |

`@1` overwrites a single byte: in front of a multi-byte character it leaves continuation bytes
behind, which are invalid lead bytes (5.4: 0 consumed, the loop stalls). Nothing in the data
uses `@1`/`@2`.

**Line break `^`** (0xC7F3D..0xC7FF4, 0xC8430..0xC845D) [E]: the line index goes up by one
and the pen x returns to 0 (an empty line gets a default height). If the current line index is
already 31, the text ends there (`cmp esi, 0x1e; jg end`). The `^` is consumed without a
break when caller flag 0x4000 is set, when caller flag 0x8000 is set and the pen is at x = 0,
or when the previous glyph has just wrapped the line automatically (local flag set at 0xC85F9 /
0xC8BA8 / `@x` 0xC8995 and cleared for every other glyph at 0xC8027 / 0xC809D) [E; the
"just wrapped" reading of that flag is H]. Every `^` also re-enables automatic wrapping
(clears the `@o` local [rbp-0x88]). Usage: ja 4, en 175, zc 6, zt 5 [V], e.g. ja 9328, a
two-item choice of the shape `１．…。　^@t２．…。` (31 characters; `samples/fonts/ja_line9328_caret.png`) and
en 1901 (`en_line1901_caret.png`).

tsuki: `Font.layout(text, markup=True)` implements this loop (with `ruby=None` = the language
rule above; `ruby=True/False` forces it; `show_ruby=False` = caller flag 0x2000;
`markup=False` draws `@` and `^` as glyphs). Not modelled: automatic wrapping (and therefore
`line_end` and the "just wrapped" `^` rule), the caller flags 0x4000/0x8000/0x40, the effects of
the scale/colour/position codes. tsuki additionally treats `\n` as a line break and ignores
`\r` (the engine never sees them: the line copier strips CR LF, `text.md`).

### 7.2 Placing a glyph

Per glyph of a normal line (kind 0 or 1) [E]:

```
draw_x  = pen + adj
quad    = rect (x, y, w, h), drawn at (draw_x, line_y), size (w*sx, h*sy)
          UV max stored as (x+w-1, y+h-1) (0xC7B77, constant -1 at 0x2D31C0)
pen     = draw_x + (w - 3)*sx + spacing - 3*sx  =  pen + adj + (w - 6)*sx + spacing
line height contribution = (h - 4) * sy = 69 * sy  (running maximum, 0xC7D8F)
```

- `sx`, `sy` = the layout's glyph scales (locals [rbp-0x50] / [rbp-0x6C]); `@w`/`@h`/`@s`
  change them and `@r` restores them. `spacing` is an integer layout argument (0 at most call
  sites; computed for the main text box at 0x39CFD/0x39E1C) [E, value not traced].
- Kind 2 glyphs (ruby reading) take the separate placement path at 0xC7BA1 (7.4).

### 7.3 Drawing order

Draw order per glyph (loop at 0xC9570) [E]: (a) only when the text-box flag 0x10 is set,
a drop shadow = the fill rect drawn at (+1.0, +1.0) (float 1.0 at 0x2D3010) in black with
alpha 0.66 x the text alpha (0x2D302C) via 0xDF760; (b) the **outline quad** = same rect
+ 512 in y, colour from the 16-byte entries at data VA 0xDB7BB0 (runtime-filled; index =
quad colour id - 6 when 0..3, else entry 0), alpha `2 * max(0, g + -0.5) * ...` from the
global float at 0x86AA1C (0xC9D29..0xC9D86), drawn by 0xE1DC0; (c) the **fill quad** with
the RGB565 text colour expanded into the 4-corner colour block at 0x1777920 (0xC9C87), drawn
by 0xE1DC0 at 0xC954D. Colours/alphas are runtime values; tsuki draws a black outline and a
white fill by default (both parameters) and no drop shadow. Redaction-bar pictures are
substituted at this stage (5.5).

### 7.4 Ruby

`<base|reading>` in ja/zc/zt/ko only (7.1). The base glyphs are laid out normally (kind 1:
same adj constants as kind 0); the reading glyphs get kind 2 (adj constants 5.5/5.6) and are
placed by the path at 0xC7BA1. On `<` the engine measures the base and the reading ahead
(width units from 0x11AB0, constants 32.0 at 0x2D2FF0, -3.392 at 0x2D2FFC and 0.5 at
0x2D2FBC, 0xC7717..0xC83E8) to centre one over the other; the exact formula and the reading
scale were not traced. tsuki draws the reading at half scale, centred over the base, in a band
above the line [H]. Caller flag 0x2000 hides readings (7.1).

Line breaking (automatic wrap), centring and the reveal animation belong to the text-box
document.

---

## 8. Reference implementation (tools/tsuki)

- `tsuki.font_eboot`: `EbootImage` (SELF segment table -> ELF program headers -> VA reads;
  also accepts a plain ELF), `EbootFontTables` (all lookup/record tables, tag names, wide
  bases; `verify()` checks every `lea` site listed in 5.1 and 5.3 and the table invariants),
  `pack_utf8`, `TAGS`.
- `tsuki.font`: `FontPages` (lazy page decoding: header, CSM1 palette, MZX pixels,
  `alpha_8bit` = the engine's alpha conversion), `Font` (`lookup`, `get_glyph`, `char_glyph`,
  `tag_glyph`, `dash_variant`, `dash_variant_at`, `dash_glyph`, `ruby_enabled`, `layout`,
  `render`, `charset`), `markup_end` (7.1 table), `substitute_redaction_frames`, and
  `render_text(lang, text, style=0, scale=0.5, markup=False, ruby=None, show_ruby=True, ...)
  -> PIL.Image`.
- CLI: `python -m tsuki.font pages <lang> <outdir>`, `render <lang> "<text>" out.png
  [--style N --scale S --markup --ruby auto|on|off --hide-ruby]`, `charset <lang> [--style N]`,
  `info <lang>`.
- Tests: `tools/tests/test_font.py` (20 tests, about 20 s; 6 run and 14 skip without the game).
  Real-data tests cover pages and palettes, table totals, known glyphs, dash joins and the
  previous-element rule, NUL / invalid lead, English `<`/`>` and `^`, placeholder cells, and
  the layout-vs-tokenizer comparison on every script line containing markup.

Character counts per (language, style) from `Font.charset` [V-all]: ja 2904 / 701 / 1174;
en 115 / 111 / 82 (styles 3..5: 111); zc 3512 / 769 / 1434; zt 3679 / 1631 / 1631.

---

## 9. Samples (samples/fonts)

- `pages/ft_<lang>_<NN>.png`: every non-blank page of ja (55), en (16), zc (64), zt (60),
  flattened on dark blue.
- `ja_title.png` (月姫 -A piece of blue glass moon-), `en_familiar_scent.png`,
  `{ja,en,zc,zt}_line0003.png` (script string 3, the opening line; ja with its three ruby groups),
  `en_line0069_italic.png` (`@iSplash.@r`), `{ja,en}_line4478_alt.png` (`@g` lines),
  `en_styles.png` (styles 0-5), `ja_styles.png` (0-2), `ja_tags_dashes.png`,
  `en_tags_dashes.png`, `en_redaction_bars.png`, `{ja,en}_line12509_ber.png`,
  `ja_title_nooutline.png`.
- After review: `en_line6570_angle.png` and `en_line2564_angle.png` (English `<` `>` drawn),
  `en_line1901_caret.png` and `ja_line9328_caret.png` (`^` line breaks),
  `ja_line0003_ruby_hidden.png` (caller flag 0x2000), `zt_line0156_ruby.png`,
  `ja_dash_prev_rule.png` (`――――`, `――@r――`, `―@1^―`).

---

## 10. Open questions

1. Default outline colour and alpha (runtime table at 0xDB7BB0, global at 0x86AA1C). (The
   palette alpha expansion is resolved: 3.1.)
2. Main-text-box parameters: sx/sy, letter spacing (computed at 0x39CFD), line pitch, box
   width (`@f` table at 0x2D3758: 480/536/524 px), and therefore when `line_end` (5.6) and
   automatic wrapping happen.
3. Ruby reading scale and placement (7.4: measurement at 0xC7717..0xC83E8, kind-2 path at
   0xC7BA1, not fully traced).
4. Purpose of page 0 (blank 256x256 slot 0x21, tile flag 0 = never drawn) and of page 61's
   unused cells; the engine never references slot 0x21 in the glyph code.
5. What `@b` / flag_b (quad flag bit 16) looks like on screen; the meaning of the local flag
   that makes a `^` right after an automatic wrap a no-op (7.1).
6. Korean: the tables and pages are stubs and the system-language mapping never selects ko;
   whether a later patch fills them is unknown.
7. Table addresses are fixed for eboot 01.01; `verify()` fails loudly on another build. A
   pattern-based locator would be needed to support patches.

---

## 11. Revision notes (independent review)

Corrections made after two independent reviews (reproduce / spec):

- English ruby: an earlier version said `<` always starts a ruby group, and tsuki parsed ruby
  in English by default, which swallowed `<` on 235 English lines. Fixed: 7.1, `Font.ruby_enabled`,
  `ruby=None` default [E + V on all script lines].
- `^` was not documented and was drawn as a glyph; it is the forced line break (7.1).
- Dash join `prev` / `next` are raw element pointers (5.6); tsuki used the previous drawn glyph.
- `char_glyph` on NUL / invalid lead now mirrors the engine (5.4); it used to treat NUL as a
  character and raise on an invalid lead.
- Corrected counts: 17,423 lookup entries / 5,571 distinct keys (5.1); 43,969 script lines (5.7);
  record-table sizes are the lookup sizes, not the address gap / 10 (5.3); ko records point at
  blank pages (3.4, 5.3); unused palette entries of blank pages are 00 00 00 00 (3.1).
- Added: placeholder / empty cells (3.5), header field names aligned with images.md (3.1), the
  engine's exact alpha conversion (3.1), record-table and other lea sites (5.3), the duplicate
  adj float pair and `kind >= 2` (5.5).

Every reviewer finding was confirmed with a concrete check before fixing (logs in
`scratch/fonts/repair/`); none was found to be wrong.
