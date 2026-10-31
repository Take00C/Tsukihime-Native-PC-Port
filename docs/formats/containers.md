# Container formats: HED / NAM / MRG, mrgd00 (MZP), MZX, LV

Scope: everything between "a file on disk" and "the payload bytes of one asset" for
TSUKIHIME -A piece of blue glass moon- (PS4, CUSA47999, v01.01). Image pixel formats,
script bytecode, text tables and ATRAC9 audio are covered in other documents. This
document is meant to be enough to write a C# or C++ reader without looking at the
Python reference (`tools/tsuki/{nam,mrg,mzp,mzx,sniff}.py`).

Confidence labels used below:

- **[V-all]**: verified on every record/stream in the retail data (counts given).
- **[V-n]**: verified on n samples.
- **[E]**: read directly from the game's own code (eboot.bin, text VA given; text VA = file offset - 0x2280).
- **[H]**: hypothesis, not proven.

All integers are little-endian unless stated otherwise. `SECTOR = 0x800` bytes.

Section 13 lists the corrections made after independent review (what changed and the
proof for each).

---

## 1. Overview

```
image/<arc>.hed  -- entry table (HED8 or HED4)
image/<arc>.nam  -- optional name table (NAM32 or NAM8), record i names entry i
image/<arc>.mrg  -- data: entries back to back, each padded to a 0x800 sector
        |
        +-- entry payload is one of:
              mrgd00 container (MZP)  -> sections: image header, MZX tiles, nested mrgd00, NAM, text...
              "LV" chunk + MZX0 stream -> RIFF/WAVE (ATRAC9)          (voice, voice2)
              RIFF/WAVE (ATRAC9)                                      (bgm, vse)
              VAGp (PS ADPCM)                                         (vag)

image/allscr.mrg, script_text.mrg, ft_*.mrg, se_nam.mrg
        -- standalone mrgd00 containers (no .hed/.nam)
```

| file(s) | container | entries | payload |
|---|---|---:|---|
| allpac0 / allpac1 / allpac1_2 / allpac4 | HED8 + NAM32 | 2449 / 9968 / 4317 / 37 | image MZPs (full-size; the four name sets are disjoint apart from `DUMMY.MRG`) |
| allpac2 | HED8 + NAM32 | 14745 | image MZPs: small versions of the same names (2207 names shared with allpac0, 9719 with allpac1, 2784 with allpac1_2, 37 with allpac4) |
| allpac3 | HED8 + NAM32 | 136 | by name: 72 `*.MRG` + 64 `*.MZP`. By content: 71 archives (the `*.MRG` UI-part sets per language, each an mrgd00 of image MZPs) + 65 images (the 64 `*.MZP` such as CAUTION*, RULE_xx, plus #135 `DUMMY.MRG`, which is a 16x16 one-tile image despite its extension) [V-all] |
| parts | HED8, no NAM | 24 | image MZPs |
| bgm | HED8, no NAM, no terminator | 132 | RIFF WAVE, ATRAC9, 2 ch 48 kHz |
| vse | HED8, names in se_nam.mrg section 2 | 1327 | RIFF WAVE, ATRAC9 (1316 stereo, 11 mono) |
| vag | HED8, no NAM | 19 | VAGp |
| voice | HED4 + NAM8 | 16464 | LV + MZX0 (plain) -> RIFF WAVE ATRAC9 48 kHz (16462 mono, 2 stereo) |
| voice2 | HED4 + NAM8 | 2 | same as voice entries 0-1 ("DU" dummies) |
| allscr.mrg | mrgd00 | 552 sections | NAM32 + label tables + 1 image + 549 inverted-MZX scripts |
| script_text.mrg | mrgd00 | 10 sections | 5 x (BE u32 offset table, UTF-8 string blob) |
| ft_en/ja/ko/zc/zt.mrg | mrgd00 | 65 sections each | each section an image MZP (font pages) |
| se_nam.mrg | mrgd00 | 4 sections | 4 NAM32 tables (section 2 = vse names) |

**Route by content, not by extension.** `DUMMY.MRG` is byte-identical in allpac0 #2448,
allpac1_2 #4316, allpac2 #14744, allpac3 #135 and allpac4 #36 (128 bytes, a 16x16 one-tile
image MZP), and allpac1_2 #3291 `CGMODE_TOP.MRG` is the same 128-byte blob [V-all: these 6
are the only `*.MRG`-named entries that are not archives; the other 71 are the allpac3 UI-part sets]. A reader must decide "nested
archive vs. image" from the section heads (4.5).

The eboot contains the base names it opens: `allpac0.hed ... allpac4.hed`, `allpac1_2.hed`,
`voice.hed`, `voice2.hed`, `parts.hed`, `se_nam.mrg`, `bgm.hed`, `vse.hed`, `vag.hed` (file
offset 0x2D60D4 onward), plus asset names like `conf_parts.mrg`, `gallery_parts_cg.mrg`,
`face_010103.mrg`, `caution_en.mzp`, that correspond to NAM entries such as
`CONF_PARTS_EN.MRG` / `CAUTION_EN.MZP` (upper-cased, language suffix appended) [H: exact
lookup rule belongs to the engine/UI work].

Engine archive slots (set up at VA 0xD2084..0xD21AF, strings at VA 0x2D3F26..) [E]:
voice.hed/.mrg = slot 0x13, voice2 = 0x15, parts = 0x18, se_nam.mrg = 0x17,
bgm.hed = 0x1A, vse.hed = 0x1B, vse.mrg = 0x1C, vag.mrg = 0x1D, vag.hed = 0x1E.

---

## 2. HED8 (allpac*, parts, bgm, vse, vag)

### 2.1 Record layout (8 bytes per entry)

| off | size | type | name | meaning |
|---:|---:|---|---|---|
| 0 | 2 | u16 | `sector_lo` | sector offset bits 0..15 |
| 2 | 2 | u16 | `sector_hi` | bits 12..15 = sector offset bits 16..19; bits 0..11 = byte offset inside the sector (always 0 in HED files) |
| 4 | 2 | u16 | `size_sectors` | ceil(size / 0x800) [V-all, including the 125 entries whose size is an exact multiple of 0x800] |
| 6 | 2 | u16 | `size_lo` | exact byte size & 0xFFFF |

Equivalently: `u32 field = sector_lo | sector_hi << 16`; bits 16..27 are 0 in all 33,154
HED8 records [V-all]. mangetsu treats the u32 as a plain sector offset, which is wrong as
soon as an archive exceeds 0x10000 sectors (128 MiB), because the sector bits 16..19 sit in
bits 28..31 of that u32.

The record format is the same 8-byte record the engine uses for MZP sections (section 4.2):
the engine's MZP section reader at VA 0xE6DA0 decodes `sector_hi` as `(hi & 0xF000) << 4`
sector bits plus `hi & 0x0FFF` bytes, and the HED offset reader at VA 0xE5CE8 uses
`sector_lo | (hi & 0xF000) << 4` (plus the bank, 2.2) [E].

The small audio archives use a second, bank-less accessor at VA 0xE7040 (8-byte stride:
`shl edi, 2` then `word [rsi + rax*2]`; called from 0xC058 with slot 0x1A = bgm.hed, 0xC098
with 0x1B = vse.hed and 0xD21E4 with 0x1E = vag.hed) [E]:
`sector = sector_lo | (sector_hi & 0xF000) << 4`, and it returns sector 0 when
`sector_lo == 0xFFFF`. No HED8 record in the game has `sector_lo == 0xFFFF` [V-all], so the
quirk never triggers.

Terminator: one all-0xFF record pair (16 bytes 0xFF) after the last entry in every HED8
except bgm.hed, which simply ends [V-all]. Records after the terminator: none. A reader stops
at the first record that is entirely 0xFF, or at the end of the file.

### 2.2 Full sector offset: the bank rule

Only 20 bits of the sector offset are stored (reach 2 GiB). allpac0 (6.3 GB), allpac1
(24.3 GB) and allpac1_2 (9.1 GB) are larger. The engine rebuilds the missing bits with a
per-archive *bank table* built once at load time (builder at VA 0xE5538, lookup at VA
0xE5CE8) [E]:

```
banks = []                       # entry index where bank 1, 2, ... starts
prev_nonzero = false
for i, rec in enumerate(records until terminator):
    nibble = rec.sector_hi >> 12
    if nibble != 0: prev_nonzero = true
    else:
        if prev_nonzero: banks.append(i)      # engine stores at most 11
        prev_nonzero = false

bank(i)          = number of entries b in banks with b <= i
stored20(i)      = rec.sector_lo | (rec.sector_hi >> 12) << 16
sector_offset(i) = stored20(i) | bank(i) << 20          # the engine ORs 0x100000*k
byte_offset(i)   = sector_offset(i) * 0x800
```

Measured bank starts [V-all]: allpac0 `[474, 992]`; allpac1
`[311, 799, 1403, 2212, 3449, 3725, 4197, 5033, 6199, 7280, 9115]` (11 = the engine maximum,
so 12 banks = 24 GiB is the hard limit of the format); allpac1_2 `[2503, 3417, 3681, 4221]`;
all other archives none.

On every archive this gives a gapless layout: `sector_offset(i) = sum(size_sectors[0..i-1])`
and the last entry ends exactly at the .mrg file size [V-all: 33,154 HED8 + 16,466 HED4 entries]. A reader may
therefore also compute offsets as a running sum, but should use the rule above to match the
engine.

### 2.3 Exact size

```
cap  = size_sectors * 0x800
size = (cap & ~0xFFFF) | size_lo
if size > cap: size -= 0x10000
```

Unique because `cap - 0x800 < size <= cap`. Verified against the payloads' own size fields
[V-all]: every MZP entry's packed size (section 4.3) equals the HED size (31,676 entries);
RIFF `size+8` equals the HED size (132 bgm + 1327 vse); VAG `data_size+48` equals the HED
size (19).

Engine quirk [E]: the engine's own size routine (VA 0xE6E80 / 0xE6F30) computes
`ss*0x800 + (size_lo & 0x7FF) - 0x1000 + (0x800 if (size_lo & 0x7FF) + (sector_hi & 0xFFF) < 0x800 else 0)`,
which is 0x800 too small when `byte_offset + size` is an exact multiple of 0x800 (125 HED8
entries, 494 MZP sections). The formula above is the correct one; do not copy the engine
routine.

### 2.4 Data area

Entry i occupies `[byte_offset(i), byte_offset(i) + size_sectors*0x800)`. Bytes after `size`
up to the sector end are packer garbage (mostly a repeating 16-byte pattern
`00 00 00 00 00 00 00 00 0c 00 00 00 00 00 00 00`), never 0xFF-filled; ignore them.

### 2.5 Worked example (allpac0.hed)

```
00000000: 0000 0000 0b00 1856  0b00 0000 0c00 005f   entry 0, entry 1
...
00000ec8: 20fa 00f0 6b07 1851  8b01 0000 e306 c010   entry 473, entry 474
00000ed8: 6e08 0000 c407 501b                        entry 475
...
00004c80: 784a 00f0 0100 8000  ffff ffff ffff ffff   entry 2448, terminator
00004c90: ffff ffff ffff ffff
```

| entry | name | sector_lo | sector_hi | bank | sector offset | size_sectors | size_lo | size |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 0 | BLACK.MZP | 0x0000 | 0x0000 | 0 | 0x0 | 11 | 0x5618 | 0x5618 |
| 1 | BLUE.MZP | 0x000B | 0x0000 | 0 | 0xB | 12 | 0x5F00 | 0x5F00 |
| 473 | HIS_A_00_01_N01_30_30.MZP | 0xFA20 | 0xF000 | 0 | 0xFFA20 | 0x76B | 0x5118 | 0x3B5118 |
| 474 | HIS_A_20_00_N00_01_01.MZP | 0x018B | 0x0000 | 1 | 0x10018B | 0x6E3 | 0x10C0 | 0x3710C0 |
| 2448 | DUMMY.MRG | 0x4A78 | 0xF000 | 2 | 0x2F4A78 | 1 | 0x0080 | 0x80 |

(0xFFA20 + 0x76B = 0x10018B: entry 474 starts right after 473. 0x2F4A78 + 1 = 0x2F4A79
sectors = 6,347,278,336 bytes = the allpac0.mrg size.)

---

## 3. HED4 (voice.hed, voice2.hed)

### 3.1 Record layout (4 bytes per entry)

| off | size | type | name | meaning |
|---:|---:|---|---|---|
| 0 | 2 | u16 | `w0` | sector offset bits 0..15 |
| 2 | 2 | u16 | `w1` | bits 0..11 = size in sectors; bits 12..15 = sector offset bits 16..19 |

```
sector_offset = w0 | (w1 & 0xF000) << 4
span_bytes    = (w1 & 0x0FFF) << 11            # size_sectors * 0x800
empty record  = (w0 == 0xFFFF AND w1 == 0xFFFF)
```

Engine readers [E]: the function at VA 0xE5FB0 reads the record with a 4-byte stride
(0xE606C `add ecx, ecx`, then `word [rax+rcx*2]` / `word [rax+rcx*2+2]`), returns "no entry"
only when **both** words are 0xFFFF (0xE6086..0xE6093), allocates
`(w1 & 0xFFF) << 11` bytes (0xE60AA, 0xE60D1) and seeks to sector
`((w1 & 0xF000) << 4) | w0` (0xE6160, 0xE6274). A second variant at VA 0xE6480 does the same
(record read at 0xE653D..0xE655E, sector at 0xE66C7). Testing only `w0 == 0xFFFF` is wrong:
voice.hed entry 7850 is stored as `ff ff 1b 30` = sector 0x3FFFF, 27 sectors, a valid LV+MZX
entry [V: the only HED4 record with w0 = 0xFFFF].

No exact byte size is stored. The exact entry size is defined by its content:
`exact = 4 + n + (bytes consumed by the MZX decoder)` (section 6; = 7 + consumed on retail data).
The sector count follows a different rule from HED8 [V-all, 16,466 entries]:

```
size_sectors = exact // 0x800 + 1        # NOT ceil(exact / 0x800)
```

The two rules differ only when `exact` is a multiple of 0x800: voice entries 3028 (exact
94,208, 47 sectors), 4044 (18,432, 10), 13042 (26,624, 14) and 13595 (18,432, 10) carry one
extra sector that contains only the packer pattern `0c 00 00 00 ...`. Readers are unaffected
(MZX stops on its output size); a repacker must use `exact // 0x800 + 1` to reproduce the
retail files.

voice.mrg is 0x9D6DE sectors, so no bank switch occurs (the bank rule of 2.2 is harmless and
gives the same result).

Terminator: 16 bytes 0xFF (four empty records) [V-all]. voice.hed: 16464 entries +
terminator = 65,872 bytes; voice2.hed: 2 entries + terminator = 24 bytes.

Detection: a HED is HED8 if its size is a multiple of 8 and bits 16..27 of every u32 at
offset 8k (before the first all-0xFF 8-byte record) are zero, otherwise HED4 [V-all: picks the
right layout for all 12 files]. (The game knows by file name.)

### 3.2 Worked example (voice.hed)

```
00000000: 0000 0100 0100 0100 0200 2f00 3100 1d00
          ^e0       ^e1       ^e2       ^e3
00007aa8: ffff 1b30                                   entry 7850
00010140: ffff ffff ffff ffff ffff ffff ffff ffff     terminator
```

entry 0: sector 0, 1 sector; entry 1: sector 1, 1 sector; entry 2: sector 2, 0x2F sectors;
entry 3: sector 0x31, 0x1D sectors. Entry 7850: `ffff 1b30` = sector 0xFFFF | 3 << 16 =
0x3FFFF, 0x01B = 27 sectors (exact 53,403 bytes, 53,403 // 0x800 + 1 = 27). Last entry 16463:
`c7d6 1790` = sector 0x9D6C7 (0xD6C7 | 9 << 16), 0x17 sectors, ending at 0x9D6DE = file
size / 0x800.

---

## 4. mrgd00 containers (MZP)

Used for every allpac*/parts entry, nested inside allpac3 `*.MRG` entries, by ft_*.mrg
(nested), allscr.mrg (sections 1 and 2 nested) and the standalone files. In the engine the
magic test is only `u16 == 'mr'` (VA 0xE6F35) [E].

### 4.1 Header

| off | size | type | meaning |
|---:|---:|---|---|
| 0 | 6 | char[6] | `"mrgd00"` |
| 6 | 2 | u16 | section count N |
| 8 | 8N | records | section table (4.2) |
| 8+8N | ... | data area (`data_start`) |

### 4.2 Section record (8 bytes)

| off | size | type | name | meaning |
|---:|---:|---|---|---|
| 0 | 2 | u16 | `sector_offset` | sector bits 0..15, relative to data_start |
| 2 | 2 | u16 | `byte_offset` | bits 0..11: byte offset (0..0x7FF observed); bits 12..15: sector bits 16..19 [E, never non-zero in data] |
| 4 | 2 | u16 | `size_sectors` | `ceil((byte_offset + size) / 0x800)` = sectors touched, counted from `sector_offset` |
| 6 | 2 | u16 | `size_lo` | exact size & 0xFFFF |

```
offset = data_start + sector_offset*0x800 + (byte_offset & 0xFFF) + ((byte_offset & 0xF000) << 15)
size   = decode as in 2.3 with cap = size_sectors*0x800
```

- `size_sectors` rule: holds for all 1,128,516 section records in the game (including
  nested ones); `ceil(size/0x800)` (what mangetsu writes) is wrong for 478,001 of them
  [V-all]. The decode formula is correct under either definition.
- mangetsu's `entry_data_size()` omits the `if size > cap: size -= 0x10000` step and
  over-reports by 64 KiB for sections whose size is just below a 64 KiB multiple
  (e.g. 3,585 sections in allpac0 alone; 20,981 in the game). deepLuna copies the same bug.

### 4.3 Packing rule [V-all]

Sections are stored in index order. Section 0 starts at data_start (8-byte aligned since
8+8N is). Each later section starts at `prev_end + pad`, `pad = 8 - (prev_end % 8)`
(so 1..8 bytes, never 0), and the pad bytes are 0xFF. The container ends with the same
1..8-byte 0xFF pad: `packed_size = end + 8 - (end % 8)` where `end` = end of the last
section. For every HED entry `packed_size == HED size` and for the standalone files
`packed_size == file size` [V-all]. (This also confirms the size decoding.)

### 4.4 Nesting

A section whose first 6 bytes are `mrgd00` is a nested container; offsets inside it are
relative to its own start. There is exactly one level of nesting in the retail data: a
top-level container (a HED entry or a standalone file) may hold containers, but those never
hold containers themselves [V-all: 4,026 nested containers = 3,699 image MZPs inside the 71
allpac3 archives + 325 font pages in ft_*.mrg + allscr sections 1 and 2]. A reader should
still recurse generally.

### 4.5 Kinds of MZP seen

Classify by content (the NAM extension is not reliable, see `DUMMY.MRG`):

- **image** (31,605 HED entries + 3,699 nested + 325 font pages + 1 in allscr): section 0
  starts with 8 u16 `width, height, tile_w, tile_h, tiles_x, tiles_y, type_a, type_b`
  (+ palette etc.), with `tiles_x = ceil(w/tile_w)`, `tiles_y = ceil(h/tile_h)`, followed
  by exactly `tiles_x*tiles_y` MZX0 sections (one per tile, plain MZX). Pixel formats:
  see the image document.
- **archive**: every section is itself an mrgd00 (the 71 allpac3 UI-part sets, ft_*.mrg).
- **other layouts**, only in the standalone files: allscr.mrg (NAM32, nested containers,
  inverted MZX scripts; 8.1) and script_text.mrg (offset tables and string blobs; 8.2).
  se_nam.mrg holds only NAM32 sections (8.4).

### 4.6 Worked example: allpac0 entry 0 (BLACK.MZP, 0x5618 bytes)

```
00000000: 6d72 6764 3030 1500  0000 0000 0100 2404   "mrgd00", N=21; s0
00000010: 0000 2804 0200 0804  0100 3800 0100 0804   s1, s2
...
000004d0: .... .... ffff ffff  4d5a 5830 0000 0200   end of s0 + 4 pad bytes, s1 = "MZX0" size 0x20000
000004e0: fcfc fcfc ...
```

data_start = 8 + 21*8 = 0xB0.
s0 = (0, 0, 1, 0x0424): offset 0xB0, size 0x424 (ends 0x4D4 -> pad 4 -> next at 0x4D8).
s1 = (0, 0x428, 2, 0x0408): offset 0xB0+0x428 = 0x4D8, size 0x408; 0x428+0x408 = 0x830 so
it touches 2 sectors.
s2 = (1, 0x38, 1, 0x0408): offset 0xB0+0x800+0x38 = 0x8E8.
Last section s20 ends at 0x5610; pad 8 -> packed size 0x5618 = HED size.
s0 header: `8007 3804 0002 0001 0400 0500 0100 1100` = 1920x1080, tiles 512x256, 4x5,
type (1, 0x11); 20 tiles follow.

---

## 5. MZX0 compression

### 5.1 Stream header

| off | size | type | meaning |
|---:|---:|---|---|
| 0 | 4 | char[4] | `"MZX0"` |
| 4 | 4 | u32 | decompressed size in bytes |
| 8 | ... | command stream |

There is no compressed-size field; the decoder stops when the output is full. On all
retail data the stream then ends exactly at the end of its container section (no trailing
bytes) [V-all, 1,104,762 streams].

### 5.2 Decoder (matches the engine's routine at VA 0xE7080 [E])

State: `out` (byte array), `last` (one 16-bit word, kept as two bytes), `ring[64]` (words),
`ring_pos` (0..63), `counter` (int). `INIT = 0x0000` (plain streams; see 5.4 for inverted).

```
out = new byte[size + 128]                 # see "Output overrun" below
init: counter = 0; ring_pos = 0; last = INIT; op = 0
while op < size:                           # the ONLY bounds test, between commands
    if counter <= 0:                       # "clear counter"
        counter = 0x1000
        last = INIT
    c   = next byte
    cmd = c & 3
    n   = (c >> 2) + 1                     # 1..64
    if cmd == 2: counter -= 1 else: counter -= n
    switch cmd:
      0 RLE:     repeat n times: out[op..op+1] = last; op += 2
      1 BACKREF: d = next byte; dist = 2*(d+1) bytes
                 repeat n times: w = out[op-dist .. op-dist+1]; out[op..op+1] = w; op += 2; last = w
                 (overlapping copies must be done word by word, front to back)
      2 RINGBUF: last = ring[c >> 2]; out[op..op+1] = last; op += 2      # exactly one word
      3 LITERAL: repeat n times: w = next 2 bytes; out[op..op+1] = w; op += 2; last = w
                                 ring[ring_pos] = w; ring_pos = (ring_pos + 1) & 63
result = out[0 .. size)                    # drop the overrun
```

**Output overrun** [V-all, E]. Each command is executed in full; the "output full?" test
happens only before the next command (the engine loop at 0xE70E0..0xE70E6 tests the remaining
byte count `rax` only between commands, and the literal loop at 0xE71A0 has no bound). The
encoder relies on this and ends most streams with a command that runs past the declared
size. The overrun is up to 63 words + 1 byte = **127 bytes** (a 64-word command starting
when 1 byte is still missing on an odd size). Measured over all 1,104,762 retail streams:
see the table in 5.5 (maximum 127 bytes, allscr.mrg section 294: declared 7,313 bytes, odd;
e.g. allpac4 #0 section 1 overruns by 6 bytes, allpac0 #13 section 4 by 24). A port must
either allocate `size + 128` bytes and truncate, or clamp each write to `size` while still
consuming the whole command (input position and ring buffer must advance exactly as if the
writes happened). Allocating `size` or `size + 1` bytes overflows on most image tiles. The
engine itself decodes into large static buffers (e.g. scripts: the call at VA 0x791B5 decodes into a
fixed buffer at VA 0x16545E0), so it never notices.

Engine register mapping (VA 0xE70BE onward): r8d = 0x1000, r10d = counter (reset by
`cmovle r10d, r8d`), r11b = "counter was <= 0" flag that zeroes `bx` (= last) via
`cmovne ebx, 0`; ring buffer on the stack at rbp-0xB0 (64 x u16), index r9 `& 0x3F`;
literal and ringbuf words are moved as u16; rax = remaining output bytes [E].

Facts that matter for a reimplementation:

- **The clear-counter reset is required** [E + V]. mangetsu (its counter is never
  decremented) and deepLuna (no counter) do not implement it and produce wrong pixels.
  Note that the size checks (output size, consumed == section size) cannot detect a wrong
  reset rule: with or without the reset every stream decodes to its size and consumes
  exactly its section (6,676/6,676 in the sample below). The evidence is:
  1. the engine code (above) [E];
  2. the encoder signature: with the engine rule (RINGBUF counts 1 word) the counter is
     **exactly 0 at every one of 71,865 non-initial resets** in a deterministic sample of
     6,676 tile streams (allpac2 every 50th entry, all of allpac4 and parts, allpac0 every
     100th; `scratch/containers/reset_experiment.py`): no command ever straddles a
     0x1000-word boundary. With the variant "RINGBUF counts n words" 94,001 resets would
     see a negative counter and 4,774 streams would contain straddling commands; with no
     counter the property has no meaning. The encoder evidently places command boundaries
     on the engine's 0x1000-word grid;
  3. the output: in that sample the no-reset decoder differs on 212 streams. A vertical
     roughness score prefers the reset output for 179, the no-reset output for 21, and is
     undefined for 12. All 21 "no-reset looks smoother" cases were inspected: in every one
     the reset decode produces zero bytes (palette index 0 with alpha 0 = fully transparent
     background, or all-zero RGBA) exactly where the no-reset decode produces non-zero
     data; in 6 of the 16 paletted cases the no-reset pixels are opaque (visible garbage).
     The proxy prefers the garbage only because it is smooth;
  4. visual check, allpac2 #9325 `VRV_A_39_02_A00_00_01.MZP` tile 0 (512x512, 8-bit
     indexed, 256 x RGBA palette at section 0 + 16): with the reset a clean sword on black;
     without it coloured horizontal bands start at row 48 (output byte 0x6000, a 0x2000
     boundary) and the background turns grey (`samples/containers/reset_9325_*.png`).
- **Encoder invariant:** because the counter is exactly 0 at every reset, `last` is reset
  to INIT at exactly every output offset that is a multiple of 0x2000 bytes (equivalent
  formulation for a decoder that tracks output position instead of a counter) [V-71,865
  resets; also 2,126 resets in the 549 allscr scripts].
- **Ring buffer is never read before written** (0 of 119,305 streams), and the engine does
  not initialise it; any initial value works [V-119305, E].
- Backref distance is 2..512 bytes in steps of 2; the source is always inside the output
  (checked by the decoder, 0 failures).

### 5.3 Worked example (voice entry 0, MZX starts at entry offset 7)

```
4d5a5830 44070000 | 23 52494646 3c070000 57415645 666d7420 3400 | 0e | 0b feff 0100 80bb | 0e | 03 4c1d | 0e | ...
"MZX0"   size=0x744
```

- `23`: literal, n = 9 words: `RIFF` `<\x07\x00\x00` `WAVE` `fmt ` `4\x00` (ring 0..8)
- `0e`: ringbuf slot 3 = `00 00` -> fmt chunk size 0x34
- `0b`: literal, 3 words `feff 0100 80bb` -> WAVE_FORMAT_EXTENSIBLE, 1 channel, rate low word 0xBB80
- `0e`: slot 3 = `00 00` -> rate 48000
- `03`: literal, 1 word `4c1d`; `0e` -> byte rate 0x1D4C

### 5.4 Inverted streams

The stream does not say whether it is inverted. For **allscr.mrg script sections** the game
decodes the stream normally and then XORs every output byte with 0xFF (call sites VA
0x791A0, 0x7CA9D, 0xA33B5: `mzx_size`, `mzx_decompress`, then a vector XOR with all-ones
built by `vcmptrueps`) [E]. That is the reference behaviour: **decode plain, then XOR**.

An equivalent single-pass decoder XORs each literal byte with 0xFF and uses
`INIT = 0xFFFF` **both at the start and at every counter reset** (every state variable is then
the bitwise complement of the plain decoder's, so the output is too). `tsuki.mzx.decompress(...,
invert=True)` does exactly this. Using 0xFFFF at the start but 0x0000 at resets is *not*
equivalent in general (it happens to give the same bytes on all retail scripts because no
script uses RLE as the first command after a reset: 0 of 2,126 resets,
`scratch/containers/rle_after_reset.py`). Verified: plain-then-XOR == `invert=True` for all 549
scripts, which cross 2,126 reset boundaries [V-all].

Everything else (image tiles, font pages, voice) is plain.

| use | invert | evidence |
|---|---|---|
| allscr.mrg sections 3..551 (scripts) | yes | `_ZZ00a01();...` ASCII only when inverted; engine XOR |
| image MZP tiles (allpac*, parts, ft_*, allscr s2) | no | IMG5004 decodes to white "END / Arcueid Brunestud Route Ending" text over transparent padding only when plain |
| voice / voice2 | no | yields RIFF/WAVE headers |

### 5.5 Where MZX is used, measured [V-all]

| archive | streams | compressed bytes | decompressed bytes | max overrun (B) |
|---|---:|---:|---:|---:|
| allpac0 | 157,162 | 6,342,556,795 | 18,449,138,688 | 126 |
| allpac1 | 654,094 | 24,237,987,782 | 80,323,700,832 | 126 |
| allpac1_2 | 230,377 | 9,053,359,109 | 38,052,933,056 | 126 |
| allpac2 | 15,777 | 361,818,805 | 1,384,505,472 | 2 |
| allpac3 | 25,099 | 347,692,514 | 2,026,909,440 | 4 |
| allpac4 | 2,738 | 56,054,300 | 395,779,808 | 120 |
| parts | 2,174 | 776,552 | 35,602,560 | 0 |
| voice / voice2 | 16,464 / 2 | 1,303,554,952 / 906 | 1,545,322,624 / 3,720 | 126 / 0 |
| allscr.mrg | 549 scripts + 1 tile | 9,433,876 | 19,633,657 | 127 |
| ft_en/ja/ko/zc/zt | 65 each | 1.1 MB / 8.9 MB / 0.5 MB / 10.7 MB / 10.0 MB | 67,174,400 each | 0 |
| **total** | **1,104,762** | **41,744,622,348** | **142,569,401,857** | **127** |

Overrun distribution over all streams: 0 bytes in 309,443 of 1,104,762 streams, 1 byte in 121, 2 or more bytes in 795,198 (72 %), 64 or more in 10,499, 126 or 127 in 211 (`summary.md` / `summary.json` `overshoot_hist`).

Every stream decoded to exactly its declared size and consumed exactly its section;
0 failures (`tools/inventory.py --deep`, about 100 s with 14 threads).

No other compression appears anywhere in the containers: no zlib/gzip, NXGX/NXCX (Switch),
LZ4, or HFA. (Movies are plain MP4 files outside the archives.)

### 5.6 Performance of the reference decoder

Measured on 207 random image-tile streams (22.2 MB output): pure Python
(`use_numba=False`) 36 MB/s; numba kernel 868 MB/s; a straightforward per-word Python
loop (like deepLuna's) 17 MB/s. Both paths give identical output (tests compare them).
(Reviewer re-measurement on 158 allpac2 streams: 33 MB/s and 948 MB/s.)

---

## 6. "LV" voice wrapper

Every voice.mrg / voice2.mrg entry is:

| off | size | type | meaning |
|---:|---:|---|---|
| 0 | 1 | char | `'L'` (the engine checks only this byte) |
| 1 | 1 | char | `'V'` |
| 2 | 2 | u16 | n = payload length |
| 4 | n | bytes | payload, copied to a global buffer by the engine (purpose unknown [H: lip-sync / volume data]) |
| 4+n | ... | body: MZX0 stream (plain) -> RIFF WAVE ATRAC9 |

Engine code at VA 0x8BF8 [E]:

```
p = entry
if p[0] == 'L':
    n = u16 at p+2
    copy min(n, 600) + 4 bytes from p to the global LV buffer     # 0x258 clamp at 0x8C16
    p += 4 + n                                                     # unclamped n (0x8D0B)
if p[0] == 'M': audio = mzx_decompress(p)                          # 0x8D10, call 0xE7080
else:           audio = p (used as is, uncompressed)               # 0x8D51
```

In all 16,466 entries the header is exactly `4c 56 03 00 00 09 00` (n = 3, payload
`00 09 00`) and the body is MZX [V-all]. The decompressed RIFF size field (+8) equals the MZX
declared size for every entry [V-all]. Exact entry size = `4 + n + consumed` (used by the
HED4 sector rule, 3.1).

Reference API: `tsuki.mzx.parse_lv(entry) -> LvHeader(present, magic, length, payload,
body_offset)` and `tsuki.mzx.decompress_lv(entry) -> LvDecoded(header, data, compressed,
consumed, mzx)`; pass the whole sector span (`MrgArchive.read_sectors(i)`). CLI:
`python -m tsuki.mzx voice.mrg out.wav --offset 0x1000 --lv`.

---

## 7. NAM name tables

### 7.1 NAM32 (allpac*.nam; allscr.mrg section 0; se_nam.mrg sections)

| off | size | meaning |
|---:|---:|---|
| 0 | <=30 | ASCII name, NUL-terminated/padded (longest observed: 27 chars) |
| 28 | 2 | optional tag, only in se_nam.mrg sections 0, 1, 3: lower-cased first two characters of the name (`"S00"` -> `s0`, `"RANKING"` -> `ra`, `"SE_A0018"` -> `se`) |
| 30 | 2 | `"\r\n"` |

The table ends with one all-zero 32-byte record [V-all]: allpac0.nam is 2450 records for
2449 entries (78,400 bytes), etc. Record i names entry i. Names are unique within an
archive.

```
allpac0.nam
00000000: 424c 4143 4b2e 4d5a 5000 0000 0000 0000  BLACK.MZP.......
00000010: 0000 0000 0000 0000 0000 0000 0000 0d0a  ................
```

### 7.2 NAM8 (voice.nam, voice2.nam)

8-byte records: ASCII name NUL-padded, no CRLF, no terminator record, followed by 2 zero
bytes (voice.nam = 16464*8 + 2 = 131,714 bytes; voice2.nam = 2*8 + 2 = 18) [V-all]. The
names are **group prefixes**, not unique: `DU` (2, the dummies), `A00_` (5818), `A10_`
(2802), ... `M12_` (1); 54 distinct values in 54 contiguous runs, i.e. voice.mrg is sorted
by group. How the script addresses a voice line (global index vs. prefix + number) is for
the script/audio documents to settle.

```
voice.nam
00000000: 4455 0000 0000 0000 4455 0000 0000 0000  DU......DU......
00000010: 4130 305f 0000 0000 4130 305f 0000 0000  A00_....A00_....
```

### 7.3 Name sources per archive

| archive | names |
|---|---|
| allpac0..4 | own .nam (NAM32), counts = entries [V-all] |
| voice, voice2 | own .nam (NAM8), counts = entries [V-all] |
| vse | se_nam.mrg section 2 (1327 names = 1327 entries; `DUMMY` at both ends, matching the two identical 0x1EA8-byte dummy entries) [V-all count, H mapping] |
| bgm, vag, parts | none on disk (vag entries carry a name inside the VAG header at 0x20, e.g. `moon_se00001_L`) |
| allscr scripts | allscr.mrg section 0: name k <-> section k+3 (engine: section = idx < 600 ? idx+3 : 3, VA 0x7917A) [E, V-all] |

---

## 8. Standalone files

### 8.1 allscr.mrg (9,585,000 bytes, 552 sections)

| section | content |
|---|---|
| 0 | NAM32, 549 names + terminator (`START_SCRIPT`, `00_00`, `00_01`, ..., `JUMPQA`) |
| 1 | nested mrgd00, 549 plain-text sections: label tables (`START                00b,0000b\r\n...`), child k <-> name k |
| 2 | nested image MZP, 128x96, 1 tile |
| 3..551 | MZX, **inverted**, script k = section k+3, decompressed ASCII bytecode (`_ZZ00a01();_ZZ00b01(START);...`) |

### 8.2 script_text.mrg (12,601,040 bytes, 10 sections)

Sections 2k (k = 0..4): big-endian u32 offset tables, 43,972 entries (175,888 bytes) each
[V-all]. Each table holds exactly **one** 0xFFFFFFFF, as its last entry (index 43,971); the
two entries before it both equal the size of the matching string blob (e.g. table 0 ends
`..., 0x340998, 0x3409A0, 0x3409A0, 0xFFFFFFFF` with blob size 0x3409A0). The 8 bytes of 0xFF
that a hex dump shows after the table are the mrgd00 inter-section pad (175,888 is a multiple
of 8, so the pad is 8 bytes, 4.3), not table entries. Compare mangetsu
`repack_script_text_translation.cpp`.

Sections 2k+1: UTF-8 string blobs (all five decode as strict UTF-8). Observed language order
by content: 0/1 Japanese, 2/3 English, 4/5 Simplified Chinese, 6/7 Traditional Chinese,
8/9 mostly full-width blank lines (probably the Korean slot) [H]. Not compressed.

### 8.3 ft_{en,ja,ko,zc,zt}.mrg

65 sections each, every section an image MZP: 64 pages of 1024x1024 with a 1041-byte
header+palette section and a 1 MiB tile, plus one 256x256 page with a 64 KiB tile
[V-all; H: 8-bit paletted]: font glyph atlases. Content is for the font/text document.

### 8.4 se_nam.mrg

4 NAM32 sections: 0 = `S00..S07` (8, tag `s0`), 1 = `RANKING`, `NEKO02` (tags), 2 = 1327
vse names, 3 = 67 `SE_xNNNN` names (tag `se`). The purpose of sections 0, 1 and 3 is not
established [H].

---

## 9. Audio entries (outer headers only)

- bgm, vse: `RIFF` / `WAVE`, `fmt ` chunk of 0x34 bytes, format tag 0xFFFE
  (WAVE_FORMAT_EXTENSIBLE) with SubFormat GUID `{47E142D2-36BA-4D8D-88FC-61654F8C836C}`
  = ATRAC9, 48 kHz; followed by `fact`, `smpl`, `data` chunks [V-all]. ffmpeg decodes
  ATRAC9.
- vag: `VAGp`, big-endian header: version 0x00020001 at +4, data size at +12, rate at
  +16 (48000), name at +0x20 (16 bytes). `data_size + 48 == HED size` [V-all].

---

## 10. Inventory (samples/inventory)

`tools/inventory.py` writes one JSONL per archive (`allpac0.jsonl`, ..., `voice.jsonl`,
`allscr.jsonl`, `script_text.jsonl`, `ft_*.jsonl`, `se_nam.jsonl`), `summary.md` and
`summary.json`. Triplet records carry: `index, name, offset, size, size_exact,
sector_offset, size_sectors, size_lo, hed_raw, head, format`, plus for MZPs
`mzp.{count,data_start,end,packed_size,kind,nested,issues,sections[[offset,size,label,mzx_size]],children}`,
`image{w,h,tile_w,tile_h,tiles_x,tiles_y,type}`, `decoded_head/decoded_size/decoded_format`
(first MZX payload; image tiles are labelled `pixels`), `deep` (per-entry full-decode
stats incl. `overshoot_max` and `overshoot_hist`); for voice `lv{header,n,payload},
mzx_consumed, exact_size, riff`; for bgm/vse `riff`; for vag `vag`; and `checks` with every
size cross-check (voice: `hed4_sectors_eq_floor_plus_1`, `hed4_sectors_eq_ceil`). The
summary per triplet also has `name_ext_kind_hist` (NAM extension vs. content kind).
script_text offset-table records carry `ffffffff_at`, `last_values`, `pad_after`.

Totals from the last run (`inventory.py --deep`):

| archive | entries | entry formats | checks |
|---|---:|---|---|
| allpac0 | 2449 | mzp/image 2449 | packed size == HED size 2449/2449, layout ok 2449/2449 |
| allpac1 | 9968 | mzp/image 9968 | 9968/9968, 9968/9968 |
| allpac1_2 | 4317 | mzp/image 4317 | 4317/4317, 4317/4317 |
| allpac2 | 14745 | mzp/image 14745 | 14745/14745, 14745/14745 |
| allpac3 | 136 | mzp: archive 71 (all `*.MRG`), image 65 (64 `*.MZP` + `DUMMY.MRG`) | 136/136, 136/136 |
| allpac4 | 37 | mzp/image 37 | 37/37, 37/37 |
| parts | 24 | mzp/image 24 | 24/24, 24/24 |
| bgm | 132 | riff-wave/atrac9 132 | RIFF size == HED size 132/132 |
| vse | 1327 | riff-wave/atrac9 1327 | 1327/1327 |
| vag | 19 | vag 19 | VAG size == HED size 19/19 |
| voice | 16464 | lv-mzx 16464 -> riff-wave/atrac9 | RIFF size == MZX size 16464/16464, fits in sectors 16464/16464, HED4 sectors == exact//0x800+1 16464/16464 (== ceil: 16460) |
| voice2 | 2 | lv-mzx 2 | 2/2, 2/2, 2/2 |
| allscr.mrg | 552 sections | mzx 549, mzp 2, nam32 1 | 549 names aligned |
| script_text.mrg | 10 sections | be32-offsets 5, text 5 | one 0xFFFFFFFF per table, at index 43,971 |
| ft_*.mrg | 65 sections each | mzp (image) | |
| se_nam.mrg | 4 sections | nam32 4 | |

Unparseable entries/sections: **0**. Validation issues (extent outside file, gaps, size
mismatches, bad padding, misaligned names): **0**.

---

## 11. Evidence (commands actually run)

- `python -m tsuki.mrg <image>/<arc> --limit 0` for all 12 triplets: 0 validation issues,
  contiguous, bank starts as listed in 2.2.
- `python tools/inventory.py --deep`: all numbers in sections 5.5 and 10 (log:
  `scratch/containers/deep3.log`).
- `pytest tools/tests/test_containers.py`: 37 tests pass in about 1.6 s (also with TSUKI_NO_NUMBA=1; with a bogus TSUKI_GAME_DIR 11 synthetic tests pass and 26 real-data tests skip).
- Size-rule census over all 1,128,516 MZP section records (sectors-touched rule: 100%;
  ceil(size) rule: 650,515 / 1,128,516).
- `scratch/containers/census.py` (log `census.log`): HED4 sector rule floor+1 holds for
  16,464/16,464 voice + 2/2 voice2 entries, ceil for 16,460 (exceptions 3028, 4044, 13042,
  13595, extra sector starts `0c 00 00 00`); the only HED4 record with w0 = 0xFFFF is voice
  #7850 (`ffff1b30`, sector 0x3FFFF, 27 sectors); 0 HED8 records with sector_lo = 0xFFFF;
  script_text tables: 43,972 u32 each, single 0xFFFFFFFF at index 43,971, followed by 8 pad
  bytes 0xFF; allpac3 (extension, kind) = (MRG, archive) 71, (MZP, image) 64, (MRG, image) 1;
  DUMMY.MRG SHA-1 identical in allpac0/1_2/2/3/4; plain-then-XOR == `invert=True` for 549/549
  scripts (2,126 reset boundaries).
- `scratch/containers/reset_experiment.py` (log `reset_experiment.log`), the
  `proxy_outliers.py`, `outlier_alpha.py` and `outlier_zero.py` follow-ups, and
  `render_9325.py` / `render_outliers.py` (PNGs in `samples/containers/`, viewed): see 5.2.
  This replaces the earlier, inconsistent "15 of 157" / "114 differing streams" figures.
- `scratch/containers/overshoot_examples.py`: allpac4 #0 s1 overrun 6 (numba and Python),
  allpac0 #13 s4 overrun 24, allscr s294 overrun 127 (declared 7,313).
- voice entry 2 decoded with `python -m tsuki.mzx voice.mrg out.wav --offset 0x1000 --lv`
  (= the earlier `--offset 0x1007` without `--lv`): ffprobe reports ATRAC9 mono 48 kHz,
  5.93 s; ffmpeg-decoded PCM has a speech-like envelope (100 ms RMS bursts up to 9329
  separated by near-silence).
- Visual checks: allpac2 #9325 tile 0 (sword, clean with reset, banded without:
  `samples/containers/reset_9325_{reset,noreset,diff}.png`), the four largest
  roughness-proxy outliers (`samples/containers/outlier_*.png`: reset | no reset | diff
  mask; identical once alpha is applied, differences only in transparent background),
  allpac1 #2271 IMG5004 (white "END / Arcueid Brunestud Route Ending" caption, plain MZX).
- eboot disassembly (capstone, `scratch/containers/ebdis.py`, `callers.py`, `strat.py`) of
  VA 0xE5538, 0xE5CE8, 0xE5FB0, 0xE6480, 0xE6DA0, 0xE6E80, 0xE7040, 0xE7080, 0x8BF8,
  0x7917A, 0xC040, 0xC080, 0xD2070..0xD2200.

## 12. Open questions

1. Meaning of the LV payload `00 09 00` (n = 3 in every entry).
2. Purpose of se_nam.mrg sections 0, 1, 3 and of the 2-byte tags.
3. The image header `type_a/type_b` values and palettes (image document).
4. How script commands address voice entries (global voice.hed index vs. group prefix);
   the NAM8 names alone are not unique.
5. Language of script_text.mrg pair 4 (sections 8/9) [H: Korean placeholder].
6. The exact rule the engine uses to turn asset names in scripts/eboot
   (`conf_parts.mrg`, `face_010103.mrg`, ...) into NAM lookups (case, language suffix,
   which allpac is searched first; allpac2 holds a reduced copy of almost every image).
7. Whether the engine's short size routine (2.3: 0x800 too small for 125 HED8 entries and
   494 MZP sections) matters anywhere at runtime.
8. Why the HED4 packer counts `exact // 0x800 + 1` sectors (perhaps its notion of the entry
   size includes one more byte than the LV+MZX data).

## 13. Corrections after independent review

| # | claim before | correction | proof |
|---|---|---|---|
| 1 | 5.2: "an odd size makes the last word overshoot by 1 byte" (also `tsuki.mzx` docstring, `MzxResult.overshoot` "0 or 1") | the last command overruns by up to 127 bytes on any size; allocate `size + 128` or clamp writes | full census (5.5), tests `test_mzx_overshoot`, `test_mzx_overshoot_real`; engine loop tests the count only between commands |
| 2 | 3.1: "HED4 accessor at VA 0xE7040; word0 == 0xFFFF means no entry" | 0xE7040 is an 8-byte-stride HED8 accessor for bgm/vse/vag; HED4 is read at 0xE5FB0 / 0xE6480 and a record is empty only if both words are 0xFFFF | disassembly listed in 2.1/3.1; voice #7850 `ffff1b30` is a real entry (`test_hed4_edge_entries`) |
| 3 | 3.1: HED4 size in sectors (implicitly ceil) | `exact // 0x800 + 1` | 16,466/16,466; 4 entries differ from ceil |
| 4 | 8.2: "then 3 x 0xFFFFFFFF" | one 0xFFFFFFFF per table; the other 8 bytes are the section pad | `test_script_text_tables` |
| 5 | allpac3: "71 *.MRG + 65 *.MZP" | 72 `.MRG` + 64 `.MZP` by name; DUMMY.MRG (and allpac1_2 `CGMODE_TOP.MRG`) is a placeholder image | `test_allpac3_dummy_mrg_is_an_image` |
| 6 | 5.4: "initial last = 0xFFFF ... is equivalent" | equivalent only if resets also use 0xFFFF (what tsuki does); reference = decode plain then XOR | 549/549 scripts; synthetic test across a reset boundary |
| 7 | 4.5: "mixed: ... se_nam.mrg (section 8)" | se_nam is pure NAM32; wording fixed | 8.4 |
| 8 | 5.2 vs 11: "15 of 157" vs "wins all 114" | replaced by one reproducible experiment (6,676 streams) plus the counter-at-reset signature | 5.2 |
| 9 | `mrg.py` docstring: mangetsu wrong "for files > 256 Ki sectors" | 0x10000 sectors (128 MiB) | bits 28..31 carry sector bits 16..19 |
| 10 | no LV API (callers hard-coded `raw[7:]`) | `parse_lv` / `decompress_lv` follow the engine (generic n, raw body if not 'M'); inventory and sniff use them | `test_lv_parse_synthetic`, `test_hed4_edge_entries` |
| 11 | 4.4: "depth at most 2" | exactly one level of nesting below the top-level container | 4,026 nested containers, none containing another |
