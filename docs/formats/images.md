# Image format: image MZPs (headers, pixel formats, tiles, sprite layers)

Scope: the pixel data of every picture in TSUKIHIME -A piece of blue glass moon- (PS4,
CUSA47999, v01.01): the image MZPs in allpac0/1/1_2/2/4, the images nested in the allpac3
UI sets, parts.mrg, the ft_*.mrg font pages and allscr.mrg section 2. Containers (HED/NAM/MRG,
`mrgd00`, MZX0) are described in `containers.md`; read that first. This document is meant to be
enough to write a C# or C++ decoder without reading the Python reference
(`tools/tsuki/image.py`, `tools/tsuki/image_catalog.py`).

Confidence labels:

- **[V-all]**: verified on every relevant record in the retail data (count given).
- **[V-n]**: verified on n samples.
- **[E]**: read from the game's own code (eboot.bin; text VA = file offset - 0x2280).
- **[H]**: hypothesis.

All integers are little-endian. "Tile" means one MZX section of an image MZP.

---

## 1. Overview

```
image MZP (mrgd00)
  section 0         image header: 16 bytes + [palette] + 1 flag byte per tile
  section 1..N      one MZX0 stream (plain, not inverted) per tile, row-major,
                    N = tiles_x * tiles_y
```

Four pixel formats exist in the retail data [V-all, 35,630 image MZPs]:

| name used here | bmp_type (u16 @0x0C) | depth (u8 @0x0E) | attr (u8 @0x0F) | tile payload | where |
|---|---|---|---|---|---|
| **PAL8** | 1 | 0x11 or 0x01 | 0 | 8-bit indices; 256 x RGBA palette in the header, stored in PS2 "CSM1" order | allpac2 (all 14,744 thumbnails), allpac3 UI, parts, ft_* fonts, a few allpac0 fills |
| **PAL4** | 1 | 0x10 or 0x00 | 0 | 4-bit indices; 16 x RGBA palette in the header | only the 16x16 empty placeholders (`DUMMY.MRG`, `CGMODE_TOP.MRG`, parts #0, 14 allpac3 children) |
| **HEP8** | 12 (0x0C) | 0x11 | 1 | per-tile "HEP" block: 32-byte header, 8-bit indices, own 256 x RGBA palette | almost all full-size backgrounds, CGs and sprites (16,443 images) |
| **RGB565+LO+A** | 11 (0x0B) | 0x14 | 0 | planar: u16 RGB565, u8 low bits, u8 alpha (4 bytes/pixel) | 568 images: effects, some CGs and sprites, allpac3 noise/gradients |

There is no GNF, no GPU tiling/swizzling of pixels, no DXT/BCn, no NXGX/NXCX: all tiles are
linear row-major bitmaps, expanded on the CPU by the engine to B8G8R8A8 textures [E: VA 0xD6900].

Counts per archive (image MZPs, header parsed by `tsuki.image.parse_header`) [V-all]:

| archive | images | PAL8 | PAL4 | HEP8 | RGB565+LO+A |
|---|---:|---:|---:|---:|---:|
| allpac0 | 2,449 | 8 (7 x depth 0x11, `ANIME_TOP` depth 0x01) | 1 | 2,381 | 59 |
| allpac1 | 9,968 | 0 | 0 | 9,892 | 76 |
| allpac1_2 | 4,317 | 0 | 2 | 4,137 | 178 |
| allpac2 | 14,745 | 14,744 | 1 | 0 | 0 |
| allpac3 | 3,764 (65 top-level + 3,699 nested) | 3,498 (3 x depth 0x01) | 14 | 0 | 252 |
| allpac4 | 37 | 0 | 1 | 33 | 3 |
| parts | 24 | 23 (depth 0x01) | 1 (depth 0x00) | 0 | 0 |
| allscr.mrg s2 | 1 | 1 (depth 0x01) | 0 | 0 | 0 |
| ft_en/ja/ko/zc/zt | 5 x 65 | 325 (depth 0x01) | 0 | 0 | 0 |
| **total** | **35,630** | **18,599** | **20** | **16,443** | **568** |

Header failures: 0. Section count == 1 + tiles: 35,630/35,630.

---

## 2. Image header (section 0)

| off | size | type | name | meaning |
|---:|---:|---|---|---|
| 0x00 | 2 | u16 | `width` | stored width in pixels; with attr bit 0 it includes 2 border pixels per tile column (2.2) |
| 0x02 | 2 | u16 | `height` | stored height |
| 0x04 | 2 | u16 | `tile_w` | stored tile width (including border) |
| 0x06 | 2 | u16 | `tile_h` | stored tile height |
| 0x08 | 2 | u16 | `tiles_x` | `ceil(width / tile_w)` [V-all] |
| 0x0A | 2 | u16 | `tiles_y` | `ceil(height / tile_h)` [V-all] |
| 0x0C | 2 | u16 | `bmp_type` | 1, 11 or 12 in the data (table in section 1). The engine stores only the low byte and sets bit 8 in memory after it has converted the palette in place (VA 0xD90D0) [E]; files never have bit 8 set [V-all] |
| 0x0E | 1 | u8 | `depth` | low nibble = pixel size code: 0 = 4 bpp, 1 = 8 bpp, 4 = 32 bpp. Bit 4 (0x10): engine may defer decoding tiles until drawn (VA 0xD8DBC..0xD8DC4, 0xD91E7) [E, purpose H]; it does not change the pixel format |
| 0x0F | 1 | u8 | `attr` | bit 0: tile border (2.2). Always 1 for HEP8, 0 otherwise [V-all] |
| 0x10 | 0, 64 or 1024 | RGBA x 0/16/256 | palette | only when `bmp_type` is 1 (or 2, unused): 16 entries if `depth & 0xF == 0`, 256 entries if `depth & 0xF` is 1 or 7, **no palette** for any other code (the flags then start at 0x10) [E: VA 0xD8DC7..0xD8DFB]. Retail type-1 images only use codes 0 and 1 [V-all] |
| ... | tiles_x*tiles_y | u8[] | tile flags | 0 = empty, 1 = "may contain transparency", 2 = "opaque" (packer hints, 2.3) |

The section size is exactly `16 + palette + tiles_x*tiles_y` [V-all: 35,630].

Palette rule as the engine applies it (VA 0xD8DC7: `cmp cx, 1; ja 0xD9053` on `bmp_type - 1`, then
`depth & 0xF`: 0 -> 0xD906B (16 colours, flags at +0x50), 7 or 1 -> 0xD8E01 (256 colours, flags at
+0x410), anything else -> 0xD905A (flags pointer = header + 0x10)):

```
pal_entries = 0
if bmp_type & 0xFF in (1, 2):
    code = depth & 0xF
    pal_entries = 16 if code == 0 else 256 if code in (1, 7) else 0
flags_offset = 0x10 + 4 * pal_entries
```

Engine header reader: VA 0xD8B80 copies the eight u16 into a 0x58-byte image slot
(width/height at +0x20/+0x22, tile size +0x24/+0x26, tile counts +0x28/+0x2A, bmp_type low byte
at +2, depth low nibble at +5, attr at +4) [E].

### 2.1 Pixel-size codes the engine knows

From VA 0xD6900 (bytes per stored pixel by `depth & 0xF`): 0 -> 1/2, 1 -> 1, 2/6 -> 2,
3/4 -> 4, 5 -> 1 (+0x100), 7 -> 5. And by `bmp_type`: 1/2 palette in header, 3/12 per-tile
block with palette after the pixels (3: no alpha scaling, 12: alpha scaling), 6, 7 raw copy,
8/9 via VA 0xD6080, 11 planar RGB via VA 0xD5970, 2/4 internal "B640" codec (VA 0xE79F0).
Only the combinations of section 1 occur in the data [V-all]; the others are listed for
completeness [E, not exercised].

Depth bits 5..7 select an engine-side size doubling / 2/3 scaling (VA 0xD8CD1..0xD8D8E)
[E]; they are 0 in every file [V-all].

### 2.2 Tile border (attr bit 0) and displayed size

When `attr & 1` is set, every stored tile carries a 1-pixel border on all four sides that
duplicates the neighbouring tiles' pixels (so bilinear filtering across tile edges is seamless).
Only the inner `(tile_w-2) x (tile_h-2)` pixels of each tile are displayed:

```
border  = attr & 1
inner_w = tile_w - 2*border
inner_h = tile_h - 2*border
out_w   = width  - 2*tiles_x   if border and width  >= 8 else width      # engine VA 0xD8C4F
out_h   = height - 2*tiles_y   if border and height >= 8 else height
tile t (row-major: tx = t % tiles_x, ty = t / tiles_x) is displayed at
        (tx*inner_w, ty*inner_h), source rectangle [border .. border+inner) clipped to out_w/out_h
```

Evidence [E + V]: the engine subtracts `2*tiles_x` / `2*tiles_y` from the stored size when the
flag is set (VA 0xD8C4F..0xD8C6E). On the data, the last stored column of tile (x) matches column
1 of tile (x+1) and column 0 of tile (x+1) matches column tile_w-2 of tile (x): mean RGB
difference 0.3..1.8 (palette quantisation noise), against 1.2..4.6 for the naive pairing
(last column vs. first column), which behaves like a 1-pixel neighbour step
(`scratch/images/crop_test.py`, 5 images; `mass_decode.py`: the border pairing is closer in
28,326 of 31,651 tile pairs, the rest are flat areas where both are equal).

For HEP8 images, `width`/`height` are always exact multiples of the tile size [V-all: 16,443], so
e.g. a 5120x2304 header with 10x9 tiles of 512x256 shows 5100x2286 pixels. The real picture is
usually smaller; the rest is transparent padding. The real size is not in the MZP; it is in the
engine's image catalog (section 8) and can also be read from the alpha bounding box
(IMG0007: catalog 4800x2160, bbox 4800x2160, displayed 5100x2286). Careful: 166 catalog pictures
store four language versions stacked vertically in one MZP; for them the catalog size is the size
of one band and the band offset depends on the game language (8.1).

### 2.3 Tile flags

One byte per tile, row-major [V-all]. The values are hints written by the packer; only flag 0
changes what the engine does:

| value | meaning | engine |
|---:|---|---|
| 0 | empty | no texture is built and the tile is never displayed: slot word 0xFFFF (VA 0xD9330..0xD9359) [E]. Exception to "never decoded": on the non-deferred path the loader MZX-decompresses section 1 (tile 0) before the flag loop whatever its flag (VA 0xD91A4..0xD91DD, `edi=1`, call 0xE7080), then marks tile 0 like any other flag-0 tile; it is still not displayed. Every other flag-0 tile is skipped without decompression [E] |
| 1 | "may contain transparency" | decoded; 0xFFFE "not yet loaded" marker on the deferred path (VA 0xD9210..0xD922C) [E]. Usually mixed, but 25 % of the HEP8 flag-1 tiles (4 % of PAL8) are fully opaque (table below) |
| 2 | "opaque" | decoded like 1 [E]. Almost always fully opaque, but not strictly: 22 HEP8 and 11 PAL8 tiles of the measured set contain pixels with alpha below 0xFE (e.g. allpac1 `IMG0846.MZP` tiles 0, 1, 3, 5, 25, 30, 35, 40, 45, 50, 55: 11 flag-2 tiles, each with 321..1,491 pixels below 0xFE, per-tile minimum alpha 216..236; `IMG0319.MZP` tile 1: 765 pixels, min alpha 236; `NOE_A_72_00_N00_01_01.MZP` tile 15: 83 pixels, min alpha 232) [V] |

How the renderer uses 1 vs 2 is not established. A port must **not** skip blending for flag-2
tiles (blend every tile with its alpha); it may only drop flag-0 tiles.

Measured on 19,823 fully decoded images (section 10), classifying each tile by the alpha of its
displayed area after conversion:

| format | flag 0 | flag 1 | flag 2 |
|---|---|---|---|
| HEP8 | 15,252 all alpha 0; 50 with max alpha <= 8 | 23,905 mixed, 8,149 all opaque, 318 all transparent | 9,689 all opaque, 22 mixed |
| PAL8 | 3,893 all 0; 85 with max alpha <= 8 | 24,534 mixed, 984 all opaque | 10,299 all opaque, 11 mixed |
| RGB565+LO+A | 1,009 all 0; 12 with max alpha <= 8 | 3,442 mixed | 820 all opaque |

So: flag 0 implies "nothing visible" (the packer drops tiles whose alpha never exceeds 8/255 or so);
flag 2 means "opaque" for 99.8 % of the tiles (the exceptions are near-opaque pixels, alpha >= 216);
flag 1 is the conservative middle (25 % of HEP8 flag-1 tiles are fully opaque; presumably decided
on the 8-bit source alpha before quantisation to 7 bits) [H]. A decoder must **skip flag-0 tiles**
to match the game.

Notable consequence [V]: allpac0 `BLUE.MZP`, `GREEN.MZP`, `ORANGE.MZP`, `YELLOW.MZP` (1920x1080
PAL8) have every tile flagged 0 and their stored pixels are white with palette alpha 0xFF; the
engine shows nothing for them. The allpac2 thumbnails of the same names are proper colour fills,
and allpac0 `BLACK`, `RED`, `WHITE` are normal (flag 2).

---

## 3. PAL8 / PAL4 (bmp_type 1)

### 3.1 Palette

Stored at section 0 + 0x10: 256 (8 bpp) or 16 (4 bpp) entries of 4 bytes `R, G, B, A`.
The engine converts it in place once (VA 0xD8E01..0xD90E2) [E]:

```
if depth & 0xF in (1, 7):                          # 256 colours
    stored = copy of the 1024 palette bytes
    for every block of 32 entries b*32 .. b*32+31:  # PS2 CSM1 CLUT layout
        pal[b*32 +  0.. 7] = stored[b*32 +  0.. 7]
        pal[b*32 +  8..15] = stored[b*32 + 16..23]
        pal[b*32 + 16..23] = stored[b*32 +  8..15]
        pal[b*32 + 24..31] = stored[b*32 + 24..31]
    n = 256
elif depth & 0xF == 0:                             # 16 colours: no reordering
    n = 16
for i in 0..n-1:
    a = pal[i].A
    pal[i].A = 0xFF if a == 0x80 else (2*a) & 0xFF # VA 0xD90B0..0xD90CE
```

Equivalently, logical entry `i` is stored at `i ^ 0x18` when bits 3 and 4 of `i` differ, else at
`i`.

Alpha consequences: stored 0x00..0x7F map to 0..0xFE (0x7F -> 0xFE, i.e. 99.2 % opacity: the
engine never reaches 0xFF except via 0x80), 0x80 -> 0xFF. Values above 0x80 wrap (0xFF -> 0xFE).
Observed palette maxima [V-all, 18,619 type-1 images]: 0x7F in 16,804, 0x01..0x7E in 467
(mostly faint effects), 0x80 in 1,198, 0xFF in 5 (allpac0 BLUE/GREEN/ORANGE/YELLOW, parts #1),
0 in 145 (empty placeholders and unused font pages).

Evidence that the reordering is needed even where the engine code is not the only proof [V]:

- allpac2 holds a 1/12.8-scale PAL8 copy of almost every full-size image. Comparing 40 random
  thumbnails with their HEP8/RGB565 originals downscaled by 12.8 (premultiplied RGBA mean absolute
  error): for the 37 plain images 2.5 with the CSM1 order vs. 12.0 with the stored order, CSM1
  better in 37/37; the other 3 are face-layer names whose thumbnails are composites (section 7,
  errors about 50..84 either way) (`scratch/images/thumb_xcheck.py`, log `thumb_xcheck.log`).
- Font pages (ft_*, depth 0x01): the palette alpha is a monotonic ramp 0, 0, 1, 1, ... 0x7F, 0x7F
  only after reordering (`tests/test_image.py::test_font_page_alpha_ramp`).
- Neighbour-roughness test on 16 allpac2 tiles: reordered palette smoother in 14, tie 2
  (`scratch/images/swizzle_test.py`).

### 3.2 Pixels

Tile stream decompressed size = `tile_w*tile_h` (8 bpp) or `floor(tile_w*tile_h/2)` (4 bpp)
[V-all]. Row-major, no header. `rgba = pal[index]`.

4 bpp [E: VA 0xD69BC..0xD69C6 and 0xD7040..0xD706C]: the tile is expanded as **one linear stream**,
not row by row. With `n = tile_w*tile_h` texels the engine reads `n >> 1` bytes; byte `i` gives
texel `2i` from its low nibble and texel `2i+1` from its high nibble (texel `k` is at row
`k / tile_w`, column `k % tile_w`). This equals "low nibble = left pixel" only when `tile_w` is
even. If `n` is odd the engine never writes the last texel (its texture buffer is not
cleared); the reference decoder uses index 0 there. Not testable on the data: the only 4 bpp
tiles are 16x16 and all-zero.

```
for i in 0 .. n/2 - 1:              # integer division
    b = data[i]
    texel[2*i]     = pal[b & 0x0F]
    texel[2*i + 1] = pal[b >> 4]
```

PAL8 images are never bordered and their size is arbitrary (e.g. 375x169); a single tile may
be larger than the image (thumbnail tiles are up to 512x512); pixels outside `width x height`
are not displayed and may hold garbage.

### 3.3 Worked example: allpac2 `IMG0332.MZP` (300x273 thumbnail)

```
section 0: 2c01 1101 0002 0002 0100 0100 0100 1100   300x273, tile 512x512, 1x1, type 1, depth 0x11, attr 0
           02 02 02 80  05 04 04 80  05 05 06 80 ...   palette (RGBA, alpha 0x80 = opaque)
           ... 02                                       1 tile flag: opaque
stored entry 8  = (19, 11, 5, 0x80)   stored entry 16 = (4, 11, 13, 0x80)
logical entry 8 = (4, 11, 13, 0xFF)   logical entry 16 = (19, 11, 5, 0xFF)
```

---

## 4. HEP8 (bmp_type 12)

### 4.1 Tile block

Each tile stream decompresses to `0x20 + tile_w*tile_h + 0x400` bytes [V-all: 1,021,112 HEP
tiles by declared size and header; 57,385 of them fully decoded]:

| off | size | type | name | value / meaning |
|---:|---:|---|---|---|
| 0x00 | 4 | char[4] | magic | `"HEP\0"` [V-all] |
| 0x04 | 4 | u32 | total | block size = decompressed size [V-all] |
| 0x08 | 8 | u64 | tex0 | a PS2 GS `TEX0` register: PSM (bits 20..25) = 0x13 PSMT8, TW (26..29) = log2(tile_w), TH (30..33) = log2(tile_h), TCC (34) = 1 [V-all: 1,021,112; five distinct values 0x61D300000 (128x256), 0x621300000 (256x256), 0x625300000 (512x256), 0x661300000 (256x512), 0x665300000 (512x512)] |
| 0x10 | 4 | u32 | unk_10 | 0x10 [V-all] |
| 0x14 | 4 | u32 | width | = tile_w [V-all] |
| 0x18 | 4 | u32 | height | = tile_h [V-all] |
| 0x1C | 4 | u32 | alpha_mode | 1 = opaque tile: every palette alpha is 0x80 (PS2 "1.0") and the tile flag is 2 [V: 2,442/2,442 sampled tiles; 5,490 tiles with value 1 in the mass decode, all flag 2]. 2 = palette alpha in 0x00..0x7F (flags 0/1/2: 15,302 / 32,372 / 4,221) [V-57,385]. The engine ignores it [E] |
| 0x20 | tile_w*tile_h | u8[] | indices | row-major |
| 0x20+n | 1024 | RGBA x 256 | palette | plain order (no CSM1) |

The engine ignores the HEP header and uses the MZP header's tile size (VA 0xD709F..0xD7117:
palette read at `tile + 0x20 + tile_w*tile_h`, pixels at `tile + 0x20`) [E].

### 4.2 Palette conversion (VA 0xD70CE..0xD7115) [E]

```
for i in 0..255:
    R, G, B, A = pal[i]
    A = 2 * min(A, 0x7F)          # 0x7F -> 0xFE; values >= 0x7F clamp
```
No reordering: confirmed by roughness tests (101 HEP tiles: plain order smoother in all 101,
`swizzle_test.py`) and by the visual results. The clamp matters: in a sample of 43,827 HEP tiles
(every 25th entry of allpac0/1/1_2, all of allpac4; `hep_alpha.py`) 2,442 tiles have palette
alpha 0x80 (all entries; the `alpha_mode` 1 tiles), which the clamp maps to 0xFE like 0x7F.
Every other tile's palette maximum is <= 0x7F.

### 4.3 Worked example: allpac1_2 #0 `ARU_L_81_00_N00.MZP`

```
section 0 (31 bytes):
  00: 8002 0003 8000 0001 0500 0300 0c00 1101    640x768, tile 128x256, 5x3 tiles,
                                                 type 12, depth 0x11, attr 1 (border)
  10: 00 00 00 00 00 00 01 01 01 01 00 00 01 01 01   15 tile flags
  -> displayed 630x762 (640-2*5, 768-2*3), inner tile 126x254
section 8 = tile 7 (tx=2, ty=1), decompressed 0x8420 bytes:
  0000: 48455000 20840000 0000301d 06000000      "HEP\0", 0x8420, tex0 = 0x6_1D300000
  0010: 10000000 80000000 00010000 02000000      0x10, 128, 256, 2
  0020: cd cd cd ...                             indices
  8020: 01 01 01 7f  06 00 00 7f ...             palette (alpha 0x7F)
tex0 = 0x000000061D300000: PSM = 0x13, TW = 7 (128), TH = 8 (256), TCC = 1
stored pixel (0,0) of tile 7: index 0xCD -> palette entry (f9 d7 b1 7f) -> RGBA (249, 215, 177, 254).
It is a border pixel (a copy of the neighbour's pixel); the first displayed pixel of tile 7 is
stored (1,1), shown at output (2*126, 1*254) = (252, 254).
```

---

## 5. RGB565+LO+A (bmp_type 11, depth 0x14)

Each tile decompresses to `4*tile_w*tile_h` bytes [V-all], three planes of `n = tile_w*tile_h`
pixels, row-major:

| off | size | content |
|---:|---:|---|
| 0 | 2n | u16 per pixel: RGB565 (`r5 << 11 | g6 << 5 | b5`) |
| 2n | n | u8 per pixel: low bits, `r3 << 5 | g2 << 3 | b3` |
| 3n | n | u8 per pixel: alpha (0..255, used as is) |

```
R = (word >> 11) << 3        | (lo >> 5)
G = ((word >> 5) & 0x3F) << 2 | ((lo >> 3) & 3)
B = (word & 0x1F) << 3       | (lo & 7)
A = alpha
```

Engine: VA 0xD5970 (scalar loop 0xD5CC0..0xD5D28; output byte 0 = B, 1 = G, 2 = R, 3 = A of
a B8G8R8A8 texel) [E]. A lossless 8-bit RGBA split into planes for better compression.
Channel order confirmed visually (Ciel's blue hair in `SIE_A_90_00_N00`, white
"END / Arcueid Brunestud Route Ending" caption in IMG5004, blue night sky in IMG1537).
These images have no border and arbitrary sizes (e.g. 1717x631).

Worked example: allpac0 #181 `IMG0770.MZP` (1717x631 lens flare), header
`b506 7702 0002 0001 0400 0300 0b00 1400` + flags `01 01 01 01 01 01 01 01 00 01 01 00`;
tile 0 pixel (278,253): word at 0x3F62C = `fb af` = 0xAFFB (r5 21, g6 63, b5 27), lo at
0x5FB16 = 0x3A (r3 1, g2 3, b3 2), alpha at 0x7FB16 = 0x69 -> RGBA (169, 255, 218, 105).

---

## 6. Colour, alpha, output

- Output of the reference decoder: straight (non-premultiplied) RGBA8, exactly the texel values
  the engine uploads (alpha 0xFE for "opaque" PAL8/HEP8 pixels).
- Straight, not premultiplied [V]: 67 % of semi-transparent HEP8 pixels (100.0 M of 149.2 M)
  and 49 % of PAL8 ones have a colour channel larger than alpha, impossible for premultiplied data.
- Fully transparent pixels often carry colour (e.g. 3.3 M HEP8 pixels with alpha 0 and RGB != 0
  in the sample), so a renderer that filters bilinearly should premultiply after decoding.
- Colour space: plain 8-bit sRGB-encoded values (VideoOut is configured as A8R8G8B8 sRGB);
  no gamma tables are applied by the loader.
- The loader can apply an optional colour effect to palettes / pixels (VA 0xD36F0, effect id
  from slot +0x4A, parameters +0x4C..+0x51 copied from globals at VA 0x1DBD0EC..0x1DBD0F4 and
  0xDB948C..0xDB948E) [E]; it is a no-op when the effect id and parameters are 0 (VA 0xD6D25).
  This belongs to the renderer/effects work.

---

## 7. Sprites: bases and face layers

Character sprite names follow `CCC_S_PP_VV_Fnn[_suffix].MZP` (998 `_B` bases: allpac0 241,
allpac1 249, allpac1_2 508; 3,457 face/base name pairs matched by the rule below):

| part | meaning |
|---|---|
| `CCC` | character code [H from the pictures: ARU Arcueid, SIE Ciel, AKI Akiha, HIS Hisui, KOH Kohaku, SIK Shiki, NOE Noel, VRV Vlov, ROA Roa, SAT Satsuki; MAR, MIO, ARA, ARI, AOK, BOY, GOT, AND, KRO, Z01/Z20 ... not identified]. The catalog carries a numeric id per code (`char_id`, section 8) |
| `S` | A, L, M, S: scale class. The catalog stores it as `scale_class` 0/1/2/3 (A/L/M/S) for all 8,553 sprite records [V-all], and the engine multiplies a position by 1, 0.5, 0.25 or 0.125 for it (VA 0x526A3) [E; exact use open]. A sprites are the largest (e.g. AKI_A base 1828x6357 logical), L smaller (ARU_L_81 717x1279), S the smallest |
| `PP_VV` | pose / costume variant |
| `Fnn` | expression (`Nnn`, or `Ann` alternate set) |
| `_B` | base body layer |
| `_HH_VV` | variant of another picture (sprites and `IMGnnnn` alike). The catalog marks exactly these names with `pic_flags` bit 0 (7,288 / 7,288; no other name has it) [V-all]. The engine parses HH and VV as two decimal numbers and maps the variant to its base picture id (the name without `_HH_VV`) when it sets the "picture seen" bits (VA 0x957B0, callers 0x7DD62 and 0x862BE) [E]. What HH/VV control is open: visually they look pre-blurred (`SIK_A_34_00_N00_26_26` is visibly blurred, `ARU_A_00_00_N03_27_27` too, `_04_04` slightly; the thumbnails grow by a few pixels with the suffix value: IMG0002 300x199 -> `_01_01`..`_05_05` 302x201 -> `_10_00` 304x201, consistent with a blur that widens the canvas; `_10_00` horizontal only) [H] |

Composition rule [V]: the face layer `GROUP_Fnn.MZP` is drawn with straight-alpha "over" on top
of the base `GROUP_..._B.MZP` at offset **(0, 0)**, both in displayed (border-removed) pixels;
both use the same tile size, so the face is tile-aligned. The face layer is cropped to the tiles
it needs (e.g. 630x762 face on a 756x1524 base). Base tiles under the face are either holes
(flag 0; 1,845 of 3,457 pairs have every face tile over a base hole) or carry the same pixels.

Evidence: allpac2 thumbnails of face-layer names are pre-composited sprites. For 30 random
face/base pairs (seed 3, log `compose_xcheck.log`), `compose(base, face, (0,0))` downscaled by 12.8 matches the thumbnail with
premultiplied MAE 0.67..4.1, vs. 4.2..50.7 for the base alone and 33..128 for the face alone
(29/30 best = composite; the 30th, `ROA_L_00_00_N01`, is a full-size layer where composite and
face-only tie, 1.67 vs 1.60) (`scratch/images/compose_xcheck.py`). Visual checks:
`samples/images/allpac1_2_ARU_L_81_00_N00_on_B.png`, `allpac0_HIS_A_00_00_N00_on_B_crop508_508.png`.

Which base belongs to which face is given by the engine's image catalog (next section), not by
a name rule (bases are named after one of their faces, e.g. `ARU_A_00_00_N00_B` serves N00..N05,
and `ROA_L_00_00_N01` sits on `ROA_L_00_00_A00_B`).

---

## 8. The engine's image catalog (eboot.bin)

A table of 0x48-byte records in the data segment of eboot.bin v01.01. The engine indexes it as
`0x671900 + id*0x48` (data VA; file offset **0x3D78A0**), e.g. the getter at text VA 0x41BA0:
`cmp edi, 0x39B6; ja ...; lea rcx, [0x671900]; lea rax, [rax+rax*8]; movsx eax, word [rcx+rax*8+0x42]`
[E]. 14,744 records are used, followed by 31 empty slots: the engine's bound `id <= 0x39B6` gives
14,775 slots [V-all]. `tsuki.image_catalog.find_catalog` / `load_catalog` locate the table by
content (record 0's name0 is `BLACK.mzp`, record 1's is `BLUE.mzp`) and stop at the first slot
whose name0 pointer is 0; an empty slot has name0 = name1 = 0, -1 at +0x20/+0x24 and anchor_y = -1.

(An earlier version of this document started records 8 bytes later, at file 0x3D78A8, with
name0 at +0x00. That reading covers the same bytes, but the engine's alignment is the one above;
the old "name2 always 0" field is the layer offset and the old "12 zero bytes" were +0x44..+0x47
plus the next record's +0x00..+0x07.)

| off | size | type | name | meaning |
|---:|---:|---|---|---|
| 0x00 | 8 | u64 | - | 0 [V-all] |
| 0x08 | 8 | ptr | name0 | base or full image MZP name (text-segment VA, file = VA + 0x2280; ASCII, mixed case: compare case-insensitively; 9,994 records are not upper case) |
| 0x10 | 8 | ptr | name1 | face layer drawn over name0, or 0 (3,529 records use it). The engine draws it in a second layer slot (`slot + 0x111`) only when this pointer is not 0 (VA 0x4B68C..0x4B710) [E] |
| 0x18 | 4+4 | i32, i32 | layer_x, layer_y | position of name1 relative to name0; the engine converts the pair to floats and stores it in the face layer's slot (VA 0x4B715 `vmovsd xmm0, [rec+0x18]; vcvtdq2ps`) [E]. (0, 0) in all 14,744 records [V-all] |
| 0x20 | 4 | i32 | - | -1 in the file; runtime cache of name0's file index, filled on first use (VA 0x4B5E4..0x4B607: `if [rec+0x20] < 0 and name0: [rec+0x20] = lookup(name0)`) [E] |
| 0x24 | 4 | i32 | - | -1; same cache for name1 (VA 0x4B6CB) [E] |
| 0x28 | 4 | u32 | - | 0 [V-all] |
| 0x2C | 2 | u16 | w3 | floor(width / 3) [V-all] |
| 0x2E | 2 | u16 | h3 | floor(height * 362 / 1080) [V-all] (so 1920x1080 -> 640x362) |
| 0x30 | 2 | u16 | width | logical picture width; for language-band pictures the width of one band (8.1) |
| 0x32 | 2 | u16 | height | logical picture height; for language-band pictures the height of one band |
| 0x34 | 4 | i32 | anchor_x | in the w3/h3 scale (the centre for 6,333 non-sprite records) [H: sprite pivot]; differences of anchors between two ids are added to slot positions (VA 0x8687C..0x868C0) [E] |
| 0x38 | 4 | i32 | anchor_y | -1 in empty slots |
| 0x3C | 1 | u8 | kind | 0x0B in all records [V-all]; read as a u16 together with +0x3D at VA 0x4A17A / 0x4BAC8 [E, meaning open] |
| 0x3D | 1 | u8 | draw_flags | bits 0..2 tested by the engine (VA 0x517F7 bit 0, 0x49B9B / 0x51824 bit 1, 0x4B66E bit 2: call 0xE9910 with `draw_param`) [E, meaning open]. 3 in records 0..6 (BLACK, BLUE, GREEN, ORANGE, RED, WHITE, YELLOW), 0 elsewhere [V-all] |
| 0x3E | 1 | u8 | draw_param | 0 in all records [V-all] |
| 0x3F | 1 | u8 | pic_flags | bit 0: `_HH_VV` variant of another picture (8.2); bit 1: four language bands (8.1). Counts: 0 -> 7,316, 1 -> 7,262, 2 -> 140, 3 -> 26 [V-all] |
| 0x40 | 2 | u16 | char_id | character id of a sprite record: one value per character code (1 SIK, 2 ARU, 3 SIE, 4 NOE, 5 ROA, 6 HIS, 7 KOH, 8 AKI, 9 ARI, 10 MAR, 11 VRV, 12 AOK, 13 BOY, 14 ARA, 15 AND, 16 KAR, 17 GOT, 18 SAT, 21 MIO, 23 KUR, 24 KRO, 25 Z01, 26 Z02, 27 Z11, 28 Z12, 29 Z20, 30 Y00); 0 for all 3,803 `_HH_VV` sprite variants and for non-sprites (one exception: IMG5028 = 3) [V-all; engine use not traced] |
| 0x42 | 2 | i16 | scale_class | sprite size letter A/L/M/S -> 0/1/2/3 for all 8,553 sprite records; 0 for non-sprites except IMG5028 (2) [V-all]. The engine multiplies a position by 0.5 / 0.25 / 0.125 for 1 / 2 / 3 (VA 0x526A3..0x526DC, constants at VA 0x1A6FD8..0x1A6FE0) [E] |
| 0x44 | 4 | u32 | - | 0 [V-all] |

Pointers are text-segment VAs (file offset = VA + 0x2280).

Worked example, record 2438 (file 0x402650) and record 11473 (file 0x4A1368):

```
record 2438                                    record 11473
+00: 00000000 00000000 89f41a00 00000000       00000000 00000000 7d091e00 00000000
     (0)               name0 = VA 0x1AF489           name0 = VA 0x1E097D "ARU_L_81_00_N00_B.MZP"
                       "img2431.MZP"
+10: 00000000 00000000 00000000 00000000       93091e00 00000000 00000000 00000000
     name1 = 0         layer (0, 0)            name1 = VA 0x1E0993 "ARU_L_81_00_N00.MZP", layer (0,0)
+20: ffffffff ffffffff 00000000 b2035a03       ffffffff ffffffff 00000000 ef00ac01
     caches -1, -1     0  w3 946  h3 858        w3 239  h3 428
+30: 160b000a d9010000 ad010000 0b000002       cd02ff04 7c000000 db000000 0b000000
     2838x2560  anchor (473, 429)  kind 0x0B,   717x1279  anchor (124, 219)  kind 0x0B,
     draw_flags 0, draw_param 0, pic_flags 2    pic_flags 0
+40: 00000000 00000000                         02000100 00000000
     char_id 0, scale_class 0                   char_id 2 (ARU), scale_class 1 (L)
```

**Record i belongs to allpac2 entry i**: the allpac2 NAM name equals name1 if present, else name0,
for 14,744 / 14,744 records (allpac2 has 14,745 entries; the last is `DUMMY.MRG`). So the
engine's picture id space is the allpac2 index, allpac2 holds the thumbnail of each id, and
the catalog says which full-size MZPs to draw. Every layer name is found in exactly one of
allpac0 (2,687 + 587), allpac1 (9,551 + 1,146), allpac1_2 (2,467 + 1,796) or allpac4 (36)
(name0 + name1 counts), except three bases that exist nowhere in the data:
`ARU_M_03_35_N00_B`, `ARU_S_03_35_N00_B`, `NOE_L_60_03_N00_B` (records 11574, 11667, 12536).

### 8.1 Language bands (pic_flags bit 1)

166 records (140 with pic_flags 2, 26 with 3) are pictures that contain text. Their MZP stores the
four language versions stacked vertically in the order of the engine's language index
(VA 0x3ED90: 0 = ja, 1 = en, 2 = zc, 3 = zt; the same mapping builds `if_<lang>.png`), and the
catalog width/height is the size of **one** band [E + V].

Full-size draw, VA 0xC2620 (14 call sites, e.g. 0x4FB9C), called with the record pointer in rcx:

```
if rec != null and (rec[0x3F] & 2):
    L      = language_index()                    # VA 0x3ED90
    h      = rec.height                          # i16 at +0x32
    stride = ceil(h / 254) * 254                 # magic-number division at 0xC26A1..0xC26DB, *254 at 0xC26E1
    src    = (x0 = 0, y0 = L*stride, x1 = rec.width, y1 = L*stride + h)
    draw_with_source_rect(src)                   # call 0xEB9C0
else:
    draw_whole_texture()                         # jmp 0xEB780
```

The stride is the band height rounded up to whole rows of 254 pixels, i.e. whole 256-pixel HEP
tile rows without their border, so every band starts on a tile boundary. Thumbnail draw,
VA 0xC2730 (2 call sites), on the allpac2 picture:

```
tw = int(rec.width  * 0.078125 + 0.5)            # 1/12.8, constants at VA 0x2D2D38 / 0x2D2D3C
th = int(rec.height * 0.078125 + 0.5)
src = (0, L*th, tw, (L+1)*th)                    # no tile rounding
```

Two more helpers take the band size from the caller instead of the catalog: VA 0xC2830
(`src = (0, L*h, w, (L+1)*h)`) and VA 0xC28D0 (offsets a given rect by `L * ceil(h/254)*254`);
both are tail-called from a UI object draw routine (VA 0x39787 / 0x3982C) that selects them by
object flag bits, so UI parts (allpac3) can also be 4-band strips; e.g. allpac3 entry 54
(`GALLERY_PARTS_FC_TH`) child 481 is a 430x972 PAL8 picture of four 243-row bands (a newspaper
headline in ja / en / zc / zt, no padding between bands, i.e. the 0xC2830 layout;
`scratch/images/lang/allpac3_54_481_bg.png`). The UI layout data that gives the band heights is
not decoded [E + V-1].

Verification (`scratch/images/lang/band_census.py`, `band_verify.py`, `anomalies.py`,
`size_stats.py`; log in `band_verify.json`) [V]:

- All 166 name0 MZPs are HEP8 with 256-row tiles (inner 254).
- 139 have content; the other 27 are `IMG6000..IMG6026` in allpac4: 2048x5120 HEP8 placeholders
  with all 80 tiles flagged 0 (catalog 3840x2160; their thumbnails are 4-band 300x676 chapter title
  cards "DAY 1 / Homecoming I" in ja/en/zc/zt; in game the chapter titles are the
  `phasetitle_*.mp4` movies).
- For all 139: shown MZP height == 4 x stride exactly; every band contains visible pixels; in 133
  every visible pixel lies inside `[L*stride, L*stride + height)` of its band, in the other 6
  (IMG1736, IMG1737, IMG2524, IMG2581, IMG2594, IMG2612) the picture fills the whole stride window
  (the engine draws only `height` rows of it); no visible pixel right of `width`.
- Example IMG2431 (record 2438, whiteboard, catalog 2838x2560, MZP 3060x11176): visible rows are
  exactly 0..2559, 2794..5353, 5588..8147, 8382..10941 (stride 2794 = 11 x 254, not 2560). The
  four bands show the same whiteboard with Japanese, English ("Ancestors", "Dead Apostles
  (Lesser)", "Nightmares", "Nightkin", "Undead", "Ghouls", "Zombies"), Simplified and Traditional
  Chinese text (`samples/images/catalog_2438_IMG2431_band_{ja,en,zc,zt}_quarter.png`).
  IMG2587 (record 2594): おしまい / Fin / 完 / 完 (`catalog_2594_IMG2587_band_*.png`).
- Thumbnails: the allpac2 picture size equals `(int(w/12.8+0.5), bands*int(h/12.8+0.5))` for
  14,741 / 14,744 records (bands = 4 or 1); the 3 exceptions are listed in 8.4. Thumbnail band i
  vs. full band j box-downscaled (premultiplied MAE, 139 records x 4 x 4): diagonal median 1.09,
  p90 4.99, max 12.8 (IMG2625, a 1422x102 caption whose thumbnail band is 8 rows); the diagonal is
  strictly the smallest in its row for 540 / 556 band pairs and within 0.03 of the smallest for
  the remaining 16, all of which are bands with (nearly) identical content (zc vs. zt, or
  IMG5002 where ja = en = zt).

A port therefore must draw `source_rect(lang)` of the composited picture, never the whole MZP and
never a top-left crop for these records (that would show the Japanese band in every language).

### 8.2 Variants (pic_flags bit 0)

7,288 records (pic_flags 1 or 3) are exactly the records whose allpac2 name ends in `_HH_VV`
(two 2-digit decimal numbers) [V-all: 7,288 / 7,288, and no record without the bit has such a
name]. VA 0x957B0(id, &base, &hh, &vv): if `rec[0x3F] & 1`, it copies the 32-byte picture name
(8.3), reads the digits at `len-5, len-4` (HH) and `len-2, len-1` (VV), cuts the name at `len-6`
and looks the rest up as a picture name (VA 0x78410) to get the base id [E]. The callers found so
far, all six (VA 0x7DD62, 0x7E07F, 0x862BE, 0x95993, 0x95CC3, 0x95D83), pass null for hh/vv and
use the base id, e.g. to set the "picture seen" bit (bitmap at VA 0x164D5C0, ids >= 0xA8D mapped through a std::map first). For the
`_HH_VV` sprite variants `char_id` is 0.

### 8.3 Picture name tables

The picture names (the allpac2 names without `.MZP`, upper case) are also compiled into the data
segment as NAM-style tables of 32-byte entries (NUL-padded name, bytes 30..31 = CR LF) in record
order [V-all]:

- data VA 0x7F6800 (file 0x55C7A0): 14,744 entries, then an all-zero entry; used by VA 0x957B0
  (`picture_name(id)`); `tsuki.image_catalog.load_picture_names` returns it (== allpac2 names
  without extension for 14,744 / 14,744).
- data VA 0x7A8B10 (file 0x50EAB0): the first 9,958 entries only (records 0..9957); referenced by
  the name lookup at VA 0x958D5 [E, purpose open].

Scripts name pictures by these names (e.g. `img2345`, `aki_a_30_01_n01`); see `script.md` for the
commands.

### 8.4 Size anomalies (catalog rect not inside the name0 MZP)

With the band rule, the catalog rectangle lies inside the composited name0 MZP for 14,710 of the
14,741 records whose name0 exists. The 31 others [V-all]:

| records | catalog size | MZP shown | explanation |
|---|---|---|---|
| 14716..14742 `IMG6000..IMG6026` (allpac4) | 3840x2160, 4 bands | 2040x5080 | empty placeholders: all 80 tiles flag 0 (8.1) |
| 2590 `IMG2583` (allpac1) | 2758x639, 4 bands | 2550x3048 | staff credits; the band stride (762) matches, only the catalog width is larger than the art (thumbnail 164 wide = about 2100 px) |
| 2672 `IMG7000` (allpac1) | 1920x10200 | 1920x1080 | opaque black RGB565+LO+A picture (every pixel (0,0,0,255)), all 12 tiles flag 2; thumbnail 150x84 [open] |
| 2673 `IMG7001` (allpac1) | 1920x1640 | 1920x1080 | same as IMG7000 [open] |
| 12017 `KRO_L_31_03_N00` (allpac1_2) | 2118x2338 | 2032x2286 | sprite larger than its base [open] |

`CatalogResolver.render(..., crop_to_logical=True)` returns exactly the catalog rectangle and pads
with transparency where the MZP is smaller.

### 8.5 Reference API

`tsuki.image_catalog.load_catalog(eboot)` -> list of `CatalogEntry` (fields as in the table;
`flags_a` / `flags_b` give the old u32 views of +0x3C / +0x40); `find_catalog(data)` -> (file
offset, used records, empty slots); `entry.source_rect(lang)` / `entry.thumb_rect(lang)` /
`entry.band_stride`; `CatalogResolver(image_dir).render(entry, crop_to_logical=True, lang="en")`
decodes name0, draws name1 over it at (layer_x, layer_y) and returns the rectangle the engine
draws (record 11473 -> 717x1279 Neco-Arc; record 2438 with `lang="en"` -> the 2838x2560 English
whiteboard); `CatalogResolver.thumbnail(entry, lang=None)`. CLI:
`python -m tsuki.image_catalog --grep ARU_L_81`, `--bands` (list the 166 band records),
`--render 2438 --lang en --out x.png` (`--full` for the uncropped MZP).

---

## 9. Algorithms (pseudocode)

Reference implementation: `tsuki.image.parse_header`, `decode_tile`, `assemble`, `decode_image`,
`compose`, `load(archive, name_or_index, child=None)`; CLI `python -m tsuki.image allpac1_2
ARU_L_81_00_N00.MZP --base ARU_L_81_00_N00_B.MZP -o out.png`, `python -m tsuki.image allpac3 --list`,
`python -m tsuki.image ft_en.mrg 64 -o page.png`.

```
decode_image(mzp):
    h = parse_header(mzp.section(0))                 # section 2 (palette rule: 2, table + code)
    require mzp.count == 1 + h.tiles_x*h.tiles_y
    out = zeroed RGBA image (out_w, out_h)          # section 2.2
    if h.bmp_type == 1: pal = convert_type1_palette(h.palette)   # section 3.1
    for t in 0 .. tiles-1:
        if h.flags[t] == 0: continue                 # engine builds no texture for empty tiles
        data = mzx_decompress(mzp.section(1 + t))    # plain MZX, allocate size+128 (containers.md 5.2)
        switch format:
          PAL8:  tile[y][x] = pal[data[y*tile_w + x]]
          PAL4:  linear stream (3.2): texel k = pal[(data[k/2] >> (4*(k&1))) & 15], k < 2*(n/2)
          HEP8:  n = tile_w*tile_h; p = hep_palette(data[0x20+n .. 0x20+n+1024])   # 4.2
                 tile[y][x] = p[data[0x20 + y*tile_w + x]]
          RGB565+LO+A: section 5
        b = h.attr & 1
        tx = t % tiles_x; ty = t / tiles_x
        copy tile[b .. b+inner_h)[b .. b+inner_w) to out at (tx*inner_w, ty*inner_h), clipped
    return out

picture(record, lang):                               # section 8
    img = decode_image(find_mzp(record.name0))
    if record.name1:
        img = over(img, decode_image(find_mzp(record.name1)), at (record.layer_x, record.layer_y))
    if record.pic_flags & 2:                         # 8.1
        stride = ceil(record.height / 254) * 254
        y0 = lang_index(lang) * stride               # ja 0, en 1, zc 2, zt 3
    else:
        y0 = 0
    return crop(img, x = 0 .. record.width, y = y0 .. y0 + record.height)   # transparent outside img

thumbnail(record, lang):                             # allpac2 entry record.index
    t = decode_image(allpac2[record.index])
    tw = int(record.width * 0.078125 + 0.5); th = int(record.height * 0.078125 + 0.5)
    L = lang_index(lang) if record.pic_flags & 2 else 0
    return crop(t, x = 0 .. tw, y = L*th .. (L+1)*th)
```

---

## 10. Verification performed

All scripts are in `scratch/images/` (language bands and the review fixes in
`scratch/images/lang/`); logs next to them.

1. `census_headers.py` + `census_report.py`: header census of every image MZP in every archive
   (35,630, incl. nested), HEP header of every HEP tile (first 32 bytes, **1,021,112 tiles**: magic,
   total == decompressed size, width/height == tile size, unk_10 == 0x10, TEX0 PSM 0x13 with
   TW/TH == log2 of the tile size and TCC 1, alpha_mode in {1, 2}: all valid) and the declared
   decompressed size of every tile stream (== format size for 35,630/35,630 images).
2. `mass_decode.py 10` (log `mass_decode.log`, results `mass_decode.json`): full decode, including
   flag-0 tiles, of allpac0 every 10th entry (245), allpac1 every 40th (250), allpac1_2 every
   10th (432), all of allpac2 (14,745), allpac3 (3,764), allpac4 (37), parts (24), allscr (1)
   and ft_* (325): **19,823 images, 0 failures** (HEP8 937, RGB565+LO+A 277, PAL8 18,592,
   PAL4 17); 57,385 HEP headers all valid; tile-flag and alpha statistics (2.3, 6); border
   statistics (2.2). Run time about 3 minutes on 13 processes.
3. `flag0_content.py`: every flag-0 tile with any visible pixel has max alpha <= 8 (147 tiles).
4. `swizzle_test.py`, `swizzle_test2.py`: palette order (3.1, 4.2).
5. `thumb_xcheck.py`: 40 thumbnails vs. full images (3.1). `compose_xcheck.py`: 30 face/base
   pairs (7).
6. `crop_test.py`: border duplication (2.2).
7. `sprite_table.py`, `sprite_table_check.py`: catalog (8); `tsuki.image_catalog` and
   `tests/test_image.py::test_image_catalog`.
8. eboot disassembly (`fulldis.py` -> `text.dis`): VA 0xD8B80 (header, palette fix-up, palette
   rule 0xD8DC7), 0xD9100..0xD9480 (tile loop, flag handling, tile 0 at 0xD91A4), 0xD6900 (tile ->
   BGRA texture, 4 bpp at 0xD69BC / 0xD7040), 0xD5970 (planar RGB), 0xE79F0 (B640, unused), 0xD36F0
   (colour effect); catalog users 0x41BA0, 0x4B5DD..0x4B72E, 0x526A3, 0x957B0, 0xC2620, 0xC2730,
   0xC2830, 0xC28D0, language index 0x3ED90 / 0x41AD0.
9. Visual checks (samples/images, previews in samples/images/preview, contact sheets
   `scratch/images/sheet_{a,b,c}.jpg`, `suffix_sheet.jpg`): backgrounds (train interior IMG0007, classroom IMG0002 thumbnails and its `_NN_NN` variants,
   tree IMG1513_02_02, night sky IMG1537, courtyard with "lines of death" IMG0332), CGs (jaguar
   IMG2464, cathedral IMG2637, IMG1481 11220x7650 at 1/8), sprites (Neco-Arc ARU_L_81, Hisui,
   Kohaku, Ciel x3, Arcueid face/base/composite, Vlov, Satsuki), UI (CAUTION_EN text, config /
   menu / title / select-text parts with glow states, rule transitions, noise, font pages), fills;
   language bands (`scratch/images/lang/look_2438.png`: whiteboard x4, `look_2590.png`: staff
   credits x4, `thumb_14716.png`: chapter card x4, `cat_2438_en_q.png`, `img2587_bands.png`).
   No channel swaps, no stride errors, no tile seams, correct transparency.
10. Language bands and catalog layout (8, 8.1..8.4): `lang/band_census.py` (pic_flags census,
    `_HH_VV` names, stride fit), `lang/band_verify.py 8` (decode of all 166 band records, about
    3 minutes on 8 processes; results in `band_verify.json`), `lang/anomalies.py`,
    `lang/size_stats.py` (thumbnail size rule, largest images), `lang/export_band_samples.py`.
11. `tests/test_image.py`: 22 tests (12 synthetic, 10 real-data incl. SHA-1 of two decodes, a
    catalog render, the catalog layout and field census, and a language-band render with a
    thumbnail cross-check), about 2.5 s (5 s with `TSUKI_NO_NUMBA=1`); 12 pass and 10 skip
    without game data.

Largest images (header scan of allpac0/1/1_2/4, `lang/size_stats.py`) [V-all]:

| what | entry | stored | shown | format |
|---|---|---|---|---|
| largest area | allpac1 `IMG1481.MZP`, `IMG1936.MZP` | 11264x7680 | 11220x7650 (343 MB as RGBA8) | HEP8, 512x512 tiles |
| | allpac1_2 `IMG3885.MZP` | 11264x7680 | 11220x7620 (256-row tiles) | HEP8 |
| | allpac1 #4946 `IMG1481_20_00.MZP` | 11272x7524 | 11272x7524 | RGB565+LO+A, no border |
| widest | allpac1 #1808 `IMG2091.MZP` | 11776x1280 | 11730x1270 | HEP8 |
| tallest | allpac1 `IMG1732.MZP`, `IMG1733.MZP` (+ variants) | 1536x11264 | 1530x11220 | HEP8 |

So shown sizes reach 11,730 px wide and 11,220 px tall (stored 11,776 / 11,264), and up to 85.8 MP.
IMG1481 decodes in 1.3 s with numba, IMG1481_20_00 in about 3 s, IMG2091 in 0.6 s. A runtime should upload tiles
as separate textures or stream them rather than build one giant bitmap, and must size buffers
per image (no single "max image" covers both the widest and the tallest).

---

## 11. Open questions

1. How the renderer uses tile flag 1 vs. 2 and HEP `unk_1c` (1 vs. 2). Do not skip blending on
   flag 2 (2.3).
2. Catalog: anchor_x/anchor_y semantics (pivot?), `kind` (0x0B), `draw_flags` bits (set only on
   the 7 colour fills), what the engine does with `char_id`, and the exact use of `scale_class`
   in the positioning code. The 640x362 scale suggests script coordinates in thirds of a pixel.
3. The `_HH_VV` numbers: the engine parses them (8.2) but the callers found ignore them; blur
   strength remains a visual hypothesis.
4. Depth bit 4 (0x10) "deferred tile loading": which images the engine streams and when.
5. The colour effect at VA 0xD36F0 (effect ids 1..0x51) and the renderer's blend mode
   (straight alpha "over" assumed; matches the thumbnails).
6. Whether the engine ever displays the empty-flagged allpac0 BLUE/GREEN/ORANGE/YELLOW (it would
   show nothing).
7. 4 bpp nibble order is from the engine code only; no retail 4 bpp tile has non-zero pixels.
8. Which allpac3 UI parts are 4-band strips and their band heights (UI layout data, VA 0x39787).
9. Catalog sizes larger than the art: IMG7000 (1920x10200) / IMG7001 (1920x1640) over a black
   1920x1080 picture, KRO_L_31_03_N00, IMG2583 width (8.4).

---

## 12. Changes after review (2026-10-25)

| reviewer claim | result | proof |
|---|---|---|
| Catalog sizes are wrong for 166 records with flags_a k = 2/3: four language versions are stacked (ja, en, zc, zt) and `crop_to_logical` returned the Japanese band | **confirmed and fixed**, with one correction: the bands are not `height` apart but `ceil(height/254)*254` (IMG2431: 2794, not 2560; the reviewer's fifth 2560-row window with 4 % coverage, rows 10240..11175, holds the lower part of band 3, which ends at row 10941) | engine VA 0xC2620 / 0xC2730 (8.1); `lang/band_verify.py` on all 166 records; `tests/test_image.py::test_language_band_render` |
| flags_a k meaning unknown | **resolved**: k is the byte at +0x3F, bit 1 = language bands, bit 0 = `_HH_VV` variant | 8.1, 8.2; census 7,288 / 7,288 |
| Catalog terminator not described | **fixed**; also the record alignment is the engine's (file 0x3D78A0, name0 at +0x08), 31 empty slots, bound 0x39B6 | 8; `find_catalog` returns (0x3D78A0, 14744, 31) |
| Largest image is not the widest (IMG1481_20_00 11272 > 11220); IMG3885 shows 11220x7620 | **confirmed and extended**: the widest is IMG2091 (11730 shown), the tallest IMG1732/1733 (11220 shown) | 10, `lang/size_stats.py` |
| Header palette: "else 256" is wrong, codes other than 0/1/7 have no palette | **confirmed and fixed** in doc and `parse_header` | 2; `test_header_palette_rule` |
| PAL4 is one linear stream; odd texel counts broke `decode_tile_indexed` | **confirmed and fixed** | 3.2; `test_pal4_linear_stream_odd_tile` |
| Flag 1/2 labels overstate their meaning | **confirmed**, wording changed, port advice added | 2.3 (re-measured IMG0846, IMG0319, NOE_A_72_00_N00_01_01) |
| Flag 0 "never decoded" is not literal for tile 0 | **confirmed**, documented | 2.3 |
| samples: 48 samples vs. 49 index entries | **confirmed** (counting slip); index now has 52 entries (3 language-band samples added) referencing 72 PNGs | `samples/images/index.json` |
