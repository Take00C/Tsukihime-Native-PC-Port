# ADV message window and main text box

Scope: how TSUKIHIME -A piece of blue glass moon- (PS4, CUSA47999, app 01.01) turns a displayed
message into glyphs on the screen. This covers the box parameters, automatic line breaking
(kinsoku and English word wrap), ruby placement, the block draw (reveal, outline, shadow), how
the messages of one page are stacked, the message-window pictures, the reveal and fade timing,
and the wait-related script commands. It continues `fonts.md` (glyph lookup, section 7) and
`text.md` (strings, markup, `$` expansion). It also answers fonts.md open questions 1-3 and
text.md open questions 3, 4, 5 and 7.

Python reference: `tools/tsuki/textbox.py`. Tests: `tools/tests/test_textbox.py`.
Samples: `samples/textbox/`. Scratch tools and logs: `scratch/textbox/`.

Confidence labels:

- **[EMU-all]**: the game's own function was run under unicorn (`scratch/textbox/emu.py`) on
  every script line (4 x 43,969) and compared bit for bit with `tsuki.textbox`. There were 0
  differences.
- **[EMU-n]**: the same comparison on n cases.
- **[E]**: read from the disassembly (eboot text VA; file offset = VA + 0x2280; data VA =
  file offset - 0x365FA0 + 0x600000).
- **[V-movie]**: measured on frames of `image/movie/prologue[_en].mp4`.
- **[D]**: observed in the script data, with no engine proof.
- **[H]**: hypothesis.

All floats are IEEE float32. The engine's operation order matters for the wrap decision
(section 5.5), and `tsuki.textbox` reproduces it with `numpy.float32`.

---

## 1. Overview

```
_ZM(msg) / _MSAD(msg)            script commands (text.md 5.2): copy + $-expand into VA 0x16534E0
   |  Text_Layout(block, text, width 474, height 220, spacing 0, leading 9, colour, flags;
   |              sx 0.74, sy 0.74)                                  VA 0xC6C20
   v
text block (0x190-byte header VA 0x177A210 + 0x190*block, plus one 0x30-byte record per element)
   |  pass 1: break class per element (byte array VA 0x178C100, 1200 elements)
   |  pass 2: glyphs, pen, automatic wrap, ruby, line heights / widths
   v
HUD process 0xF (VA 0x53CF0), every frame:
   window picture (DISP_PARTS child, VA 0x540A5..0x54492)
   previous messages of the page, re-laid out from the history ring (blocks 79..89)
   current message: block_draw(block, flags, x, y, alpha, reveal head, reveal tail) VA 0xC8F50
```

Coordinates: **text space = 640 x 360 units**, 3 px per unit on the 1920 x 1080 screen [E,
section 2]. The ordinary message is a full-screen page. Its first line starts at (87, 49)
units (ja/zc/zt) or (30, 35) (English). The text is drawn directly over the scene, with no
frame. Speech-bubble windows (DISP_PARTS children 22..51) are used only where a script sets
F1105 >= 3, and only the 25 scripts QA_01..QA_25 do that [E, D].

---

## 2. Coordinate spaces

| space | used for | to 1920x1080 | evidence |
|---|---|---|---|
| text space 640 x 360 | glyph positions, text origins, `@x` | px = 3 x, py = 3 y | bubble text anchors are converted with `x * 640/1280, y * 360/720` from the same 1280x720 table as the bubble pictures (VA 0x55829..0x558A3) [E] |
| window canvas 640 x 448 | window pictures (`0xDCBE0(640, 448)`, VA 0x53DE2) | px = 3 x, py = y * 1080/448 | pictures are drawn with texel scale (w/1920, h/1080) = 1 texel per pixel [E] |
| glyph quads | `0xD9DD0(rec.sx, rec.sy)` per glyph | w * sx * 3 x h * sy * 3 px | glyph shapes in the movie are isotropic, about 3.0 px per unit [V-movie, median 3.00-3.07 over 125 glyphs] |

The layout divides its scale arguments by 3 (VA 0xC6D29, constant 1/3 at 0x2D2F94). With
`sx = sy = 0.74` a glyph texel is 0.24667 units, which is 0.74 screen pixels. A 73-texel cell
is 54 px tall and wide.

Canvas command `0xDCBE0(w, h, x, y)` (render command type 0xC, executed at VA 0xD9E52) sets
`scale = (1920/w, 1080/h)` and `offset = (x - 960, -1080*y/272 - 540)` (centred
coordinates) [E]. The text list (display list 0x1E, VA 0x544A1) issues no canvas command of its
own. Which canvas command is in effect when it runs could not be traced statically [H: 640x360
via the per-texture vertical factor 1.24444 = 448/360 that the quad routine VA 0xE1F5F
applies to textures flagged mode 2].

**Prologue movie discrepancy [V-movie].** `prologue_en.mp4` / `prologue.mp4` were rendered by
the engine. Line and message pitch there is **81.0 px** (5 lines, both languages), but the
layout gives 26.02 / 26.0 units, which is 78.06 / 78.0 px at 3 px/unit. The positions fit
`py = 3.1154 y + c` exactly, with c about +2 px for en and +6 px for ja. Horizontal positions
fit `px = 3.0 x` (fitted 2.990-2.991). Glyph sizes fit about 3.0 in both directions. Line
breaks, ruby placement and x positions match the engine exactly (section 10.1). This document
treats 3 px/unit as the engine value and the movie's vertical spacing as a difference in the
movie build [H]. `tsuki.textbox.PROLOGUE_TRANSFORM` is the fitted movie mapping.

---

## 3. The message call chain [E]

### 3.1 `_ZM` handler (VA 0x92380)

1. **Text fade.** If the fade step (VA 0x1653E24) is <= 0: when the text alpha T (VA 0x86AA1C)
   is below 1, or F1103 ("window was closed", set by `_CLO2`/`_CLO3`) is non-zero, it sets
   T = 0 and step = 1/18 per frame (0x20B2EC = 0.0555556). Otherwise T = 1 and step = 0. Then
   F1103 = 0. T only scales the **outline** alpha (section 6.3).
2. It copies the argument into the raw buffer VA 0x16537E0. End mode: `)` gives 0, `@n` gives
   1, `@e` gives 2 (text.md 5.2). It then `$`-expands the buffer into VA 0x16534E0.
3. **Read check.** `read = (block <= 0xFB3 ? line <= readmax[block] : true)` (VA 0x924F5..),
   stored at VA 0x16541E8.
4. **Wait latch.** On the first message of a page (F823 <= 1), 0x56270 stores
   `option == 1` in byte VA 0xDF0EF0. The option is config byte +0x9D if the message was read,
   else +0x9C.
5. **End mode.** `mode = 2` if F799 != 1 (WNTY 0) and [0xDF0EF0] and the copy ended on `)`.
   Otherwise mode = copy mode (VA 0x92574..0x9259B).
6. **Colour word.** If config +0x2A == 1 and read: `0xDC6D` in the full-screen window, or
   `0xFFFF` in bubbles (`(normal_window - 1) | 0xDC6D`). If +0x2A == 2: table VA 0x86A8B0
   indexed by the message id. Otherwise `0xFFFF` (the 0x53860 result is discarded).
7. On a page (F822 == 1) it saves per message k = min(F823, 11): F[799+k] = colour high byte,
   F[810+k] = colour low byte, F[824+k] = F31, F[835+k] = F32 (VA 0x92602..0x9264F).
8. **Layout.** If F1102 != 0: `Text_Layout(slot+4, buf, 474, 220, 0, 9, 0xFFFF, 0x80; 0.74,
   0.74)` (VA 0x92751). Otherwise `0x7C340(1)`, `0xC6BB0(saved_x)`, then
   `Text_Layout(slot+4, buf, 474, 220, 0, 9, colour, flags; sx, sy)` (VA 0x9280B), with
   `sx = 0.74 * F31/24` and `sy = 0.74 * F32/24` when F31/F32 > 0 (scripts never set them, so
   both are 0.74), and `flags = (!normal_window) << 20`.
9. Voice and message flags: if a voice is queued (VA 0x16541FC >= 0) and enabled for the
   character, it is started (0xC3A80) and, if mode != 1, VA 0x1653E70 |= 4. Then E70 |= 1. If
   mode == 1: continue byte VA 0x16541F4 = 1. E00 |= 0x100, and E00 |= 0x400 if mode == 0 (VA 0x92AE6..0x92B2F). In the HUD, flag 0x400
   makes the click-wait icon alpha (VA 0xDF08E4) rise by 0.1 per frame (VA 0x53FD5..0x5400C).

`normal_window` is VA 0x53860: `w = F1105; if English and F1109 > 0: w = F1109; return w < 3`.

### 3.2 `_MSAD` (VA 0x92B60)

F1103 = 0. Mode on `)` = `2 * ([0xDF0EF0] && F799 != 1)` (the "0x56140 condition" of
text.md 5.2: 0x56140 returns byte 0xDF0EF0). It appends at the stored length (no append once
the length is > 0x2FF) and re-expands the **whole** buffer. It picks the colour as in 3.1.6
and raises the reveal head and tail to `0xC9F40(slot+4) + 1` (largest counter of the last 8
records) so the old part stays revealed. It then lays out again with the same arguments as
3.1.8 (VA 0x92DBE).

### 3.3 Text slots and blocks

| block | content | laid out by |
|---|---|---|
| 4 / 5 | current message (slot = VA 0x16541D4, toggled 0/1 by 0x75CD0) | _ZM, _MSAD, restore (0x7B790, 0x7C6D0, 0x75DC0), language reload 0xA3CB0 |
| 6, 8 (slot+6, slot+8) | extra blocks drawn 28 / 56 units lower when bits 1 / 2 of VA 0x16541D8 are set (VA 0x54C34..0x54CAC) [E; producer not traced] |
| 14 | name plate from VA 0x164FD40 (width 150, height 20, spacing 2, leading 0, colour 0xF6FD, scale 0.5) | _ZM (0x926B1); the writers 0x930C0 / 0x937C0 have no callers, so it is empty in retail [E/H] |
| 0x14 + k | choice texts in the HUD (VA 0x55051) | HUD |
| 0x46 + j / 0x4F + j | name plate / text of the j-th previous message of the page | HUD history loop |

`0x75CD0(clear)` ("new page"): it saves the reveal head/tail to VA 0x1653E8C/90 and zeroes
them, moves VA 0x16541D8 to 0x16541DC, resets the fade width (0x561D0) and clears the message
flags VA 0x1653E70. With `clear`: it empties the expanded buffer, sets VA 0x16541E0 = 1 when
the window is fully open, toggles the slot, and frees the new slot's blocks +4, +6, +8 and
block 14.

---

## 4. `Text_Layout` (VA 0xC6C20)

### 4.1 Arguments and result [E, EMU-all]

| reg | meaning |
|---|---|
| edi | block 0..0xB3 (> 0xB3: return -1) |
| rsi | NUL-terminated UTF-8 text (modified in place by `@1` / `@2`) |
| edx | width argument (stored at +0x02). The width really used is `W = 0x538F0(block, edx)` (4.4) or `@f` |
| ecx | height (+0x04): the draw stops below y + height |
| r8d | spacing (+0x16): extra pen advance per glyph, in units |
| r9d | leading (+0x18): extra space between lines, in units |
| [rsp+8] | colour word (RGB565, the default for `@c0` / `@r`) |
| [rsp+16] | flags (u64, stored at +0x08 and OR-ed into the draw flags) |
| xmm0, xmm1 | sx, sy (divided by 3) |

It returns the element count n, or 0 for an empty string. With n == 0 the block is not
touched and not marked allocated (VA 0xC7375).

### 4.2 Block header (0x190 bytes at VA 0x177A210 + 0x190 * block)

| off | type | field |
|---:|---|---|
| 0x00 | u16 | n: number of elements = number of records |
| 0x02 | u16 | width argument |
| 0x04 | u16 | height |
| 0x08 | u64 | caller flags |
| 0x14 | u16 | number of lines |
| 0x16 | u16 | spacing |
| 0x18 | u16 | leading |
| 0x1E | u16 | `@y` value |
| 0x20 | u16 | line of the last base glyph |
| 0x22 | u16[32] | base glyphs per line |
| 0x68 | u64 | draw mode (0xC9E50; 0x39 = outline only) |
| 0x70 / 0x74 | f32 | base sx / sy (argument / 3) |
| 0x80 | f32[32] | line width = pen after the last glyph of the line |
| 0x100 | f32[32] | line height (max (h-4)*sy of the line; empty line 20*sy) |
| 0x180 | ptr | records (`memalign(16, n * 0x30)`) |

Also: allocated flag `dword[0x178BE30 + 4*block]`, end counter `dword[0x178BB50 + 4*block]`.

Worked example (en id 3 start, `"  The familiar scent"`, from the emulated block):

```
header: 14 00 da 01 dc 00 00 00 | 00.. flags 0 | +0x14 01 00 (1 line) 00 00 | +0x18 09 00 | +0x22 14 00 (20 glyphs)
        +0x70 30 96 7c 3e 30 96 7c 3e  (sx = sy = 0.2466667)   +0x80 66 e6 2d 43 (width 173.9)
record 2 ('T'):
01 00 00 00 00 00 00 00 | ff ff | 00 00 | 00 00 | 22 00 | e3 02 db 00 19 03 23 01 | 03 00 | cd cd | 08 3a 4d 41 | 00 00 00 00 | 30 96 7c 3e 30 96 7c 3e | cd..
flags=1    colour     ext    line   tex 0x22 rect (739,219)-(793,291)  ctr 3   (unused) dx 12.8267   dy 0      sx, sy
```

### 4.3 Glyph record (0x30 bytes, record i = element i) [E, EMU-all]

| off | type | field |
|---:|---|---|
| 0x00 | u64 | flags: 1 used; 2 ruby reading; 4 style 1; 8 `@a` animation; 0x10000 `@b`; 0x100000 / 0x200000 / 0x400000 / 0x600000 styles 2..5 |
| 0x08 | u16 | colour word |
| 0x0A | u16 | ext (`@c6..9`: outline colour entry ext-6, VA 0xC9DA7) |
| 0x0C | u16 | line |
| 0x0E | u16 | texture slot |
| 0x10 | u16 x 4 | atlas rect x, y, x+w-1, y+h-1 |
| 0x18 | u16 | reveal counter ("ctr") |
| 0x1C | f32 | dx: x in the block |
| 0x20 | f32 | dy: y offset from the line top (ruby -7.04) |
| 0x24 / 0x28 | f32 | sx / sy of this glyph (ruby 0.1 / 0.11) |

Control elements (codes, `^`, `<`, `|`, `>`) leave their record zeroed, so they are not drawn.

### 4.4 Width rule VA 0x538F0 [E, EMU-all]

For blocks 4..8 and 79..89: `600` if the window index (F1105, or F1109 in English if > 0) is
42; else `574` if English and `width == 474`; else `width`. So **W = 474 units (1422 px) for
ja/zc/zt and 574 (1722 px) for English**. Other blocks use the argument. `@fN` overrides W
with `dword[0x2D3758 + 4*(N-'0')]` = 480 / 536 / 524 (no range check).

### 4.5 Caller contexts (52 call sites in 30 functions) [E]

Census: `scratch/textbox/callers.py` -> `callers.log`. The colour is the first stack word and
the flags the second.

| context (function) | block | W, h, spacing, leading | sx, sy | colour | flags |
|---|---|---|---|---|---|
| message `_ZM` 0x92380, `_MSAD` 0x92B60, restore 0x75DC0 / 0x7B790 / 0x7C6D0, language reload 0xA3CB0 | slot+4 | 474, 220, 0, 9 | 0.74 (*F31/24), 0.74 (*F32/24) | 0xFFFF / read colour | **bit 20** = bubble window (style 2). 0x80 when F1102 != 0 |
| HUD page history 0x53CF0 (VA 0x5599B) | 0x4F+j | 474, 220, 0, 9 | 0.74 * C824/24, 0.74 * C835/24 | saved per message | bit 20 per message |
| HUD choices (VA 0x55051), Script_Step choice redraw 0x9B71D | 0x14+k | 474/574, 220, 0, 9 | 0.74 | 0xFFFF | bit 20 when the window index >= 3 |
| name plates 0x926B1 / 0x560B3 / 0x7DA45 / 0xA3E50 | 14, 0x46+j | 150, 20, 2, 0 | 0.5 | 0xF6FD | bit 20 |
| backlog 0x3C4B0 | var | 474/574, 220, 0, 9 (and 480, 160, 0, 5 at sx = sy = 1) | 0.74 * logged factors | 0xFFFF | bit 20 of the logged message (0x53850 on the logged F1105) |
| choice screen 0xA6E10 | 100 + 8k | var, 220, 0, 9 | 0.74 | 0xC7FF selected / 0x77FF | 0 |
| save/load slot preview 0x714D0 (VA 0x716C0) | 0x26 | 130, 46, 0, 2 | 0.3294, 0.4085 | 0 | **0x22040 \| bit 20** (0x20000 English hang rule, 0x2000 hide ruby, 0x40 ignore `@a @b @x`) |
| save/load other 0x714D0 | 0x22+, 0x27 | 163, 8 / 256, 28 | 0.35 / 0.5 | 0 | 0x2040 / 0x1020 |
| UI text widgets 0x39C80 / 0x39D60 / 0x39E90 (gallery 0x29470, 0x2ED60, 0x36420) | widget +0x448 | widget fields: spacing = trunc(+0x454 * scale) (VA 0x39CFD), leading = trunc(+0x458 * scale) | widget scale * 1.13 | widget +0x464 | widget +0x466 |
| config 0x40C30 | 0x3A, 0x3B | 200, 20, 0, 0 | 0.74, 0.9176 | 0 / 0xFFFF | 0 |
| dialogs 0xCFE30, 0xD05B0 | 2 | 390, 90, 0, 1 | 0.39, 0.444 | 0xFFFF | 0 |
| menus 0x69F80, 0x6ACC0 | const | 270, 50, 0, 4 | 0.5, 0.66 | 0xFFFF | 0x100 (centred lines) |
| 0xA7CD0, 0xA8250 (HUD) | 0x14 | 474, 220, 0, 9 | 0.37, 0.4604 | 0xFFFF | 0x100 |
| others (0x6BBF0, 0x6CB80, title 0xB34C0, debug 0xCEC10, 0x2A530) | const | see log | see log | 0xFFFF / 0xF800 / 0xFFEA | 0 |

The spacing computed at VA 0x39CFD / 0x39E1C belongs to these UI widgets. The main message
box uses spacing 0 (VA 0x927FC `xor r8d, r8d`). The fonts.md 7.2 note is corrected here.

Flags tested by the layout: 0x40 (ignore `@a`, `@b`, `@x`), 0x2000 (skip ruby readings),
0x4000 (`^` ignored), 0x8000 (`^` ignored when the pen is at 0), 0x20000 (English uses the
kinsoku hang rule, 5.5) and bit 20 (start in style 2). Flags read by the draw are listed in 6.1.
No caller passes 0x4000 or 0x8000 [E].

---

## 5. Layout algorithm (pass 1 and pass 2) [E, EMU-all]

### 5.1 Pass 1: elements and break classes (VA 0xC6D80..0xC7375)

An **element** is one glyph or one control. Pass 1 walks the string, writes `class[i]` (byte
array VA 0x178C100, zeroed) and the English word table `wordlen[i]` (u16 VA 0x178C5B0), and
counts n. **At most 1200 elements (0x4B0)**. Pass 2 lays out only these n elements, so text
beyond them is dropped.

```
i = 0; prev = 0 (previous element's first byte); ruby = 0
while byte c = s[p] != 0 and i < 1200:
    q = p + 1; cur = c
    if c >= 0x80:
        key = utf8 key (2/3/4 bytes; invalid lead -> key 0, 1 byte); q = p + len
        if is_wide(key): class[i] = wide_class(key)                         (table 5.2)
        elif prev not in {' ', ',', '.'}:
            class[i] = 2
            if key != U+00B7 and i > 0 and (class[i-1] in {1, 3} or class[i-1] >= 5): class[i] = 0
    elif lang != en and c == '<': ruby = 1
    elif c == '|' and ruby > 0:
        ruby = 2 if ruby == 1 else ruby
        if flags & 0x2000: skip bytes up to and including '>' or NUL (cur = last byte read); ruby = 0
    elif lang != en and c == '>': ruby = 0
    elif c == '@': q = code end (5.3, PASS-1 rules); class stays 0
    elif c == '^': class 0
    elif c == '[' and a [tag] matches: q = p + len(tag); class[i] = 1
    else (ASCII):
        word_start = prev == ' ' or (prev == ',' and c != ' ') or (prev == '.' and c not in {'"', '\'', '.'})
        if word_start:
            class stays 0
            if English and i > 0: wordlen[i-1] = length of the word starting here + 1 (scan below)
        elif c in {',', '.'}: class[i] = 3
        elif prev in {'!', '.', '?'} and c in {'\'', '"'}: class[i] = 3
        else: class[i] = 2
    i += 1; prev = cur; p = q
n = i

word scan (VA 0xC7156..0xC71F4): wordlen[i-1] = 1; k = 0; sc = c
    loop: d = s[q+k]; stop if d in {0, '^'} or sc == ' ' or (sc == ',' and d != ' ')
          or (sc == '.' and d not in {'"','\'','.'}); wordlen[i-1] = k + 2; k += 1;
          stop if k == remaining budget; sc = d
```

So an English line can break only before a word start (class 0). The space after a word has
class 2 and stays at the end of the line. Example: `"  The cat, sat."` gives classes
`2 0 0 2 2 2 0 2 2 3 2 0 2 2 3` and wordlen `[1, 4, 0, 0, 0, 5, ...]`.

### 5.2 Kinsoku classes (`wide_class`, VA 0xC6EF3..0xC7211 and jump table VA 0x2D3280)

`is_wide(key)` (VA 0x11AB0): key >= 0x100, and either a 2-byte `± × ÷`, any 3-byte character
except halfwidth katakana U+FF61..U+FF9F, or any 4-byte character.

| class | meaning (as `class % 10` in 5.5) | characters |
|---:|---|---|
| 1 | ordinary wide character | everything else wide, including U+3000 and `…` |
| 0x0D (13 -> 3) | must not start a line | `‧ 、 ぁぃぅぇぉっゃゅょゎ ァィゥェォッャュョヮヵヶ ・ ー ！ ？` (U+2027 U+3001 U+3041.. U+FF01 U+FF1F) |
| 0x0E (14 -> 4) | must not end a line | `‘ “ 「 『 （` |
| 0x71 (113 -> 3) | must not start a line, longer hang allowed | `’ ” 。 々 》 」 』 ） ， ． ： ；` |
| 2 / 3 | narrow: inside a word / `,` `.` and closing quotes after `!.?` | ASCII and other narrow |
| 0 | break allowed before | word starts, controls, narrow multibyte after a wide character |

Bit masks as in the code: 0x5540000815 on key-0xE38283, 0x30C081500000001 on key-0xE38383,
0x6002900 / 0x40000001 / ==7 on key-0xEFBC81, 0x5412 / 0x2800 / ==0 on key-0xE38081,
0x400000155 on key-0xE38181 (`tsuki.textbox._wide_class`).

### 5.3 `@` code lengths: pass 1 differs from pass 2

| | pass 1 (VA 0xC710B) | pass 2 (jump table VA 0x2D32C0) |
|---|---|---|
| `@a @c @f @h @w` | 3 bytes | 3 bytes |
| `@x @y` | 2 + up to 3 digits | same |
| `@s` | **2 bytes** (the number is counted as ASCII elements) | to `,` `)` NUL inclusive, at most 256 bytes |
| other `@X` | 2, or the whole `(...)` group (255-byte scan) | same |

The mismatch only matters for `@s`, which no script uses: pass 2's element index i would then
read other elements' classes [E].

### 5.4 Pass 2: element loop (VA 0xC76C0..0xC8DE1)

State: pen = 0, line = 0, ctr = 1, kind = 0 (1 ruby base, 2 reading), style =
`(flags >> 19) & 2`, sx = sx_arg/3, sy = sy_arg/3, W, nowrap = 0, hang = hangcap = 0,
just_wrapped = 0, ruby guard P = -40, ruby offset OFF = 0.

Codes (pass-2 lengths): `@k @T` nothing; `@1`/`@2` write `^`/`)` over the next byte (`@1`
also sets literal-caret); `@a` anim = N (unless flags & 0x40); `@b` flag_b (unless 0x40);
`@c0` colour = default, ext = 0; `@c1..5` colour = `0x2D34C0[N]` = F800, 345F, 1082, AD55,
001F; `@c6..9` ext = N; `@f` W = table; `@h`/`@w` sy/sx = **sy_arg/sx_arg (not divided) *
SCALE[N]** (0x2D3730); `@s` sx = sy = int/64; `@i` English 1->4, 2->5, else 3 / other
languages 1; `@g` 1; `@z` 2; `@r` style 4->1, 5->2, else 0, plus anim, flag_b, ext, scales and
colour reset; `@o`/`@p` nowrap 1/0; `@t` ctr = 0; `@u` saved = ctr; `@l` ctr = saved; `@y`
block+0x1E = digits; `@x` 5.7. The others skip.

`^` (VA 0xC7F3D): if just_wrapped, it only clears just_wrapped and nowrap. Otherwise, unless
`flags & 0x4000` or (`flags & 0x8000` and pen == 0): `lines++`; empty line height = 20*sy;
line > 30 ends the text; `line++`, `pen = maxh = hang = 0`. Then OFF = 0, nowrap = 0,
just_wrapped = 0.

`<` (non-English, VA 0xC7717): measure the base (wide char = 2 units, narrow = 1; `]` sets the
count to 2; it stops at `|`, `)` or NUL), then the reading (it stops at `>`, `)` or NUL).
`S = 32*sx*base_units - 3.392*reading_units`, `half = S/2`. If `P - half > 0`: OFF = P,
P = P - S. Else OFF = half, P = -half. Then kind = 1, rpen = pen, r_ctr = ctr, r_line = line,
r_line_next = line+1, rmin = 0. `|` with kind 1 gives kind 2 (with flags & 0x2000 it skips to
after `>` and sets kind 0). `>` (non-English) gives kind 0.

Glyph (VA 0xC7824). Dash variant / `char_glyph` as in fonts.md 5.4-5.6, with `line_start =
pen <= 0` and `line_end = pen >= W - 160*sx and class[i+1] % 10 not in {2, 3} and nowrap ==
0 and kind < 2`. Then:

```
kind 0/1:  dx = pen + adj; record(dx, dy 0, sx, sy, line, ctr)
           adv = (w-3)*sx + spacing;  pen = (adv + dx) + (-3*sx)          # = pen + adj + (w-6)sx + spacing
           width[line] = pen; height[line] = max(height[line], (h-4)*sy); ctr++
           if kind == 0: P -= adv
kind 2 (reading, VA 0xC7BA1):
           x = rpen + OFF + adj
           if x > W - 6.4: OFF = 0; r_line = r_line_next; x = rmin + adj; base = rmin; P = -40  # reading wraps
           else base = rpen
           record(dx = x, dy = -7.04, sx = 0.1, sy = 0.11, line = r_line, ctr = base_units/3 + 1 + r_ctr, flags|2)
           rpen = (w*0.1 + adj) + base                      # no -6 trim
```

The record flags are in 4.3. Reading glyphs do not change pen, line height or ctr.

### 5.5 Automatic wrap (after every kind 0/1 glyph, VA 0xC7FFD..0xC8BD0)

`i` is the glyph's element index. For the last base glyph of a ruby group (the next byte is
`|`), the look-ahead index `r` is the group's `>` and `next` is the class of the element after
`>`. Otherwise `r = i` and `next = class[i+1]`. Wrapping is off when nowrap or kind == 2.

```
English:   lim0 = W - 64*sx; lim1 = lim0 - 192*sx
           if wordlen[i]: x2 = W - 32*sx*(wordlen[i]+1); if x2 < lim0: lim0 = lim1 = x2
other:     lim0 = lim1 = W - 96*sx
if pen > lim1 and pen > lim0:                        # past the margin
    if flags & 0x20000:  al = 1, cap = 1
    elif English:        al = 0, cap = 1
    else:                al = 1, cap = 7 if class[i] or next is 0x71
                                     else 6 if class[i] == 0x0E
                                     else (1 if hangcap >= 7 else 0) if class[i] == 0x0D
                                     else 7 if class[i] == next == 2 else 1
    hangcap = cap
    if next % 10 in {2, 3}:  BREAK if al and cap <= hang else (hang++, go to LOOKAHEAD)
    elif class[i] % 10 != 4: BREAK
    elif al and cap <= hang: BREAK
    else: hang++, go to LOOKAHEAD
elif pen > lim1:                                     # English, lim1 < pen <= lim0
    if next > 1:
        if next % 10 == 4 and pen > lim0 - 128*sx: BREAK
        x = pen
    else:
        x = pen + (32*sx + 3 if next == 0 else 64*sx)
        for k = r+2 .. n-1 while class[k] % 10 == 2:
            if x > lim0: BREAK
            x += 32*sx + 3 if class[k] == 2 else 64*sx
    go to LOOKAHEAD with x
else: LOOKAHEAD with x = pen
LOOKAHEAD (VA 0xC817F): only for English, in the normal window, when x > 0.75*W and next == 0 and r+2 < n:
    acc = 32*sx + x + 3; step = 32*sx + 3; for k = r+2 ..: class 1 or > 5: acc += 64 sx;
    class 0: stop (no break); else acc += step; if acc > 2*step + W: BREAK
BREAK: lines++; empty line height = 20*sy; line++; pen = maxh = hang = 0; just_wrapped = 1
```

The glyph that triggered the check stays on its line, so a break happens **after** it. A
`line > 31` ends the text. This is the whole rule set. 0 differences on 175,876 lines (section
10.3).

### 5.6 Line metrics

The line height is `max (h-4)*sy` = 69 * 0.2467 = **17.02 units**. Ruby readings do not add
height. The line pitch is `17.02 + leading 9` = **26.02 units = 78.06 px**. One full-width
glyph advances `(73-6)*0.2467` = 16.527 units (49.6 px), so a ja line holds 28 glyphs including
the U+3000 indent. English uses proportional glyphs with W = 574.

### 5.7 `@x` (VA 0xC88E1)

With digits: pen = the number (units), no break. Without digits: continue at `saved_x`
(global VA 0x178BE20, set by `0xC6BB0`; the HUD sets it to `end_x + 16.5267` of the previous
message, VA 0x55A32). If `saved_x > W - 64*sx`, or English and `saved_x > 555`, it starts a new
line (line height = 69*sy, just_wrapped = 1). Scripts write `_WKST(C847,eq,63)` before 291 of
these messages. In 5.8, 63 means "minus one line", so the message continues on the last line of
the previous one [E, D].

### 5.8 Messages on a page (HUD VA 0x54651..0x54C2F, history loop 0x558BD..0x55A6B) [E]

Origin of the first message: **(87, 49) units for ja/zc/zt, (30, 35) for English** (VA 0x5465C
/ 0x54673; F824 receives 49 / 35). For message j (0-based) of the page:

```
y = y0
for each message j:
    s = C835_j / 24 (1 if 0);  step = 12*s + 14
    C = C846 of j (the 'C847' that the previous message's _WKAD(C847,1) incremented)
    if C > 0: y += (C - 64 if C >= 0x21 else C) * step   (negative: clamp y >= 49.8235 / 35.4118)
    draw message j at (x0, y)
    y += int(y of its last line in the block) + step     # 0xCAD10 output, e.g. 26 for 2 lines
```

C<n> is `F[n + F823]` [H, consistent with every HUD read]. The script resets `C847`/`C859`
before each message and adds `_WKAD(C847,1)` after a message to leave an empty line before
the next one (3,075 uses) [D]. Previous messages are re-laid out from the history ring (record
+0x38FE, expanded again) into blocks 0x4F+j, with sx/sy from their saved C824/C835.

**Dimming of previous messages** [E; not seen in the movie]: alpha =
`1 - (255 - t)/255 * timer/limit`, with t = 100 (full-screen) or 255 (bubbles, F1111 >= 3),
`limit = (C870 + 128) * 1.2` frames. The timer C859 advances by 0xD47C0() per frame while T == 1.
At the limit it becomes -1, giving alpha t/255 = 0.39. Config +0xA4 == 1 makes it -1 at once.

---

## 6. Block draw VA 0xC8F50 [E, EMU-672]

`block_draw(block, flags, x, y, alpha, head, tail)`. The flags are OR-ed with the block flags.

### 6.1 Placement

```
y_line[i] = y + sum_{j<i}(leading + height[j])                       (float32 accumulate)
x_line[i] = x                       | flags 0x100: x + (width_arg - width[i]) * 0.5
                                    | flags 0x200: (x + width_arg) - width[i]
flags 0x400: y_line += (height_arg - total)/2;  0x800: y_line += height_arg - total
glyph x = x_line[line] + dx         | '@b' (record flag 0x10000): x_line + (463 or 565 (en) - dx - w'*sx)
glyph y = y_line[line] + dy;  stop (return 1) at the first glyph with y > y + height_arg
quad    = atlas rect (x, y)-(x2, y2) drawn at (glyph x, glyph y) with scale (rec.sx, rec.sy)
```

`w' = x2 - x` (minus 5 for the 68-wide pieces on page 61). Draw flags used by the HUD:
`0x20 | (shadow option) << 4 | (outline option) << 12`, plus `0x1000` in bubble windows. 0x10 =
drop shadow, 0x1000 = outline (also needs config +0x26).

### 6.2 Reveal

`head` and `tail` are the counter positions (VA 0x1653E80 / 0x1653E84; 768 = all). A glyph
with `ctr > head` is not drawn. `ctr < tail` gives alpha 1. Otherwise alpha =
`(head - ctr)/(head - tail)` (1 when head - tail <= 0). Config +0xA3 == 1 forces 1. Redaction
bars are substituted at this point (fonts.md 5.5).

### 6.3 Quads per glyph (in order)

1. Shadow (draw flag 0x10): the fill rect at (+1, +1) units, black, alpha `0.66 * a`, via
   0xDF760.
2. Outline (draw flag 0x1000 and config +0x26 != 0): atlas v + 0x200, colour entry 0xDB7BB0
   (`ff 00 00 00` for all four entries, so black [E; colour order H]), alpha
   `2*max(0, T - 0.5) * a_r * alpha^2`, with T the text fade VA 0x86AA1C.
3. Fill: the colour word expanded to bytes `(c>>8)&0xF8, (c>>3)&0xF8, (c<<3)&0xF8`
   (0xFFFF -> 248,248,248; 0xDC6D -> 216,136,104 read text [channel order H]), alpha
   `a_r * alpha`. Draw mode 0x39 (block+0x68) skips the fill.

Defaults (config data VA 0x66C578): +0x10 window opacity 255, +0x12 window design 0, +0x20 text
speed 35, +0x22 second speed 15, +0x26 outline on (1), +0x28 shadow off (0), +0x2A read colour
mode 0, +0xA3 0, +0xA4 0 [E]. The movie shows a dark outline and no shadow [V-movie].

---

## 7. Window pictures (HUD VA 0x53F33..0x54492) [E; positions V on renders]

Drawn only when F822 == 1 (set at the start of story scripts) and F1104 == 0 (`_WKST(F1104,eq,1)`
hides it, 185 uses, for example the 00_02 prologue). Index `w = F1105` (English: F1109 if > 0);
a second window F1111 (with F1112/F1113) draws the same way.

| w | picture | position | alpha |
|---|---|---|---|
| 0, 1, 2 | slots 22/23/24 = DISP_PARTS children **11/12/13** (1920x1080 dark overlays, alpha 23..188 / 25..255 / 24..103) | (0,0) | window opacity/255 * window fade |
| 3..32 | slot w+22 = child w+19: speech bubbles (odd/even pairs: rounded / spiky, 1014x394..1707x487) | table VA 0x2006C0 (1280x720 space) | 0.784314 * fade |
| 33..55 | slots 27..54 again, stretched to (w, h) from tables VA 0x200BB0 / 0x200C0C (e.g. 716.8x448 -> x1.12) | table | 0.784314 * fade |

Position: `((tx + xoff[F1106]) * 640/1280, (ty + yoff[F1107]) * 448/720)` in the 640x448
canvas, plus a shake offset (VA 0xDF08A4/A8). Offset tables are at VA 0x2005B0 (27 floats)
and 0x200620 (37 floats). The bubble text origin is
`((tx + dx + xoff) * 640/1280, (ty + dy + yoff*0.9578) * 360/720)` with (dx, dy) from VA
0x200960. Example: w = 5 places child 24 at pixel (492, 589.5) and the text at (213.5, 244.5)
units = (640.5, 733.5) px (`samples/textbox/bubble_ja_043350.png`).

Worked bytes (text VA 0x2006C0, file 0x202940): `16 00 00 00 00 00 00 00 00 00 00 00` = slot 22
at (0, 0); entry 3 at +0x24: `19 00 00 00 00 40 03 44 00 00 cf 43` = slot 25 at (525, 414).
Text offsets VA 0x200960: entry 3 `00 00 c2 42 14 ae 7a 42` = (97, 62.67).

With F822 == 0 (not used by story scripts) the HUD instead draws slots 13 and 5 with the source
rect `0x200B20[config+0x12 "window design" (0 -> F989), < 5]` at (0, 5.807) [E; textures not
traced].

---

## 8. Timing: reveal, fades, waits

### 8.1 Reveal speed (per 60 Hz frame, VA 0xA2630 / 0x56160 / 0x561D0) [E]

```
v = config +0x20 (text speed, default 35; +0x22 when dword 0x781AB0[0] == 1 [H: skip/auto])
v <= 0 -> head = tail = 768 (instant)
head += 1000 / (60 * v) * frames      (35 -> 0.476 counter per frame = 28.6 glyphs/s)   [F988 != 0: 0.675 fixed]
width = (1000/(60 v)) * min(0.5, v*0.1/35) * 60   (35 -> 2.86 counters)
tail  = max(tail, head - width)                        -> the last ~3 glyphs fade in
```

`_MSAD` and restored pages raise head and tail to the block's end. A new `_ZM` resets both
to 0 (0x92659).

### 8.2 Fades

- **Text fade T** (VA 0x86AA1C; step VA 0x1653E24, applied per frame by 0x534A0, clamped to
  [0, 1]): `_ZM` fades in over 18 frames after a closed or hidden window. `_CLO4(ms)` starts a
  fade-out with step `-16.6667/ms` (instant when ms <= 0 or fast-forward). `_CLO5(ms)` starts a
  fade-in with `+16.6667/ms`. T scales only the outline alpha (6.3) [E].
- **Window fade A** (VA 0xDF0730, target VA 0xDF0734): opening gives +0.06 per frame (16.7
  frames); closing -0.06. `_CLO3` gives -0.2604 per frame (about 4 frames) [E].
- `_CLO2()` (VA 0x9A887): `0x75CD0(1)` (new page), close target, A = 0 at once, message length
  0, F1103 = 1. Used in script prologues.
- `_CLO3()` (VA 0x9A8B8): if open, close with the fast fade (byte 0xDF0738) and F1103 = 1.
  12,208 uses, at page ends [E].

### 8.3 Wait model

- `_WNTY(n)` (VA 0x972F5): reads an integer, then `0x53600(n != 0)` stores word VA 0xDF08DC
  and F799. **F799 = 1 marks the last message of a page** (15,663 of 15,759 WNTY(1) are
  followed by the page-reset block, text.md / script.md) [E, D].
- End modes (3.1): mode 0 sets message flag 0x400, which brings in the click-wait icon (VA
  0xDF08E4). Mode 1 (`@n`) sets the continue byte: the next `_MSAD` appends and no icon is
  shown. Mode 2 (`@e`, or `)` with WNTY 0 and the page option [0xDF0EF0] set) sets neither.
  0x56140 = [0xDF0EF0] is latched on the first message of each page from config byte +0x9C
  (unread) / +0x9D (read) == 1 [E; which menu item these bytes are: H, CONF_PARTS "Display End
  of Page Instantly"].
- `@k` is ignored by the layout. 469/469 uses sit in `$N@k@e`, so the message ends with mode 2
  and `@k` stays in the buffer. The backlog test VA 0x55D01 checks whether a logged message
  ends in `@k` (text.md 6.2) [E]. How Adv_Update (0xA0B70) waits for input or auto time per
  mode was not traced [H].
- Auto mode: `0x41AB0` = config +0x16 != 0 and config +0x36 != 0 [E]; its timing is open.

---

## 9. Full-screen narration vs framed (`_FCSP` / `_FCLN`)

`_FCSP` (31 uses, VA 0x982A2) calls 0xB79C0. `_FCLN` (26 uses, VA 0x96D12) calls 0xB7900
[E]. Their bodies were not traced in this pass. The narration layout of chapter 5 does not
depend on them: the ordinary story page is full-screen text. The window index F1105 (0..2
overlay, >= 3 bubble) and F1104 (no window) select the frame [E]. Open question 4.

---

## 10. Verification and acceptance

### 10.1 (a) Prologue frames vs `render_page` [V-movie]

`scratch/textbox/make_acceptance.py` extracts nothing new: it uses
`samples/textbox/prologue_{en,ja}_{25,150}s.png`, which are ffmpeg frames of the read-only game
movies. It renders messages 3,4,5 (25 s) and 37,38 (150 s) over img1685 (catalog #1692, scaled
1.0528 and positioned by correlation; the `_STZ4` zoom is not modelled) or black. There is no
window picture, because 00_02 sets F1104 = 1. It measures each white-text band (left x, median
glyph bottom = baseline) the same way in both images.

| page | transform | left x error (px) | baseline error (px) |
|---|---|---|---|
| en 25 s (5 lines) | engine 3 px/unit | -1, -3, -1, -2, -2 | -7, -9.5, -12, -15, -19 |
| en 25 s | movie fit (ky 3.1154) | same | -3, -2.5, -2, -2, -3 |
| ja 25 s (3 lines + 2 ruby bands) | engine | -2..-1 | -12..-19 |
| ja 25 s | movie fit | -2..-1 | -6..-8 |
| en 150 s (2 lines) | engine / fit | -6 ('F' stem), -2 | -7, -9 / -3, -2 |
| ja 150 s | engine / fit | -1, -1 | -12.5, -15 / -5.5, -6 |

Line breaks (`hangs / in the air.`, `layers of / grime.`), the order of lines and messages, x
positions (within 3 px except the 'F' stem) and the ruby placement (くさ, か/み, ざいく)
match. Vertical positions **do not** meet +-2 px with the engine constants. The movie's lines
are 81 px apart, against 78 px from the engine values, and its first line is 7 to 13 px lower
(section 2). Side-by-side images: `samples/textbox/sbs_<lang>_<t>s_{engine,movie_fit}.png`
(movie | render | red = movie only, blue = render only). Numbers:
`samples/textbox/acceptance.json`.

### 10.2 (b) Framed message

`samples/textbox/bubble_{ja,en}_043350.png`: `$043350^$043351` from the QA segment, with
F1105 = 5 (ja) and F1109 = 33 (en). It uses DISP_PARTS child 24 (en stretched x1.12) at
alpha 0.784, style 2 glyphs (flag bit 20), the English width 574, and the text origin from the
bubble table. Viewed: the text sits inside the bubble with about 150 px margins.

### 10.3 (c) Line-break emulation [EMU-all]

- `scratch/textbox/verify_layout.py`, 16 processes, logs in `scratch/textbox/logs/full_*.log`:
  every script line of ja, en, zc and zt (175,876 layouts). It compares n, the class and word
  tables, lines, line heights/widths/glyph counts, the end counter and every record field
  (float32 bit patterns). Result: **0 mismatches**, except the 9 empty lines (16947, 24958,
  25401 in en/zc/zt) of the first run. Those exposed the empty-text early exit. After the fix
  they match (`logs/empty_lines.log`).
- Displayed messages (`_ZM` + `_MSAD` buffers, expanded, with `@x`, `@k`, `^`, two refs):
  all 6,743 composite messages, 0 mismatches. Bubble mode (F1105 = 5, flag bit 20) on every
  10th message: 15,749, 0 mismatches. Width 600 (F1105 = 42) on every 25th message: 6,301,
  0 mismatches (`logs/msgs_*.log`, `logs/bubble*.log`).
- `verify_synth.py`: 2,772 synthetic cases (all codes, `@s`, `@x` with digits, tags, dashes,
  malformed ruby, 1,300-element strings, long kinsoku and English lines) x flags 0, 0x2000,
  0x20000, 0x4000, 0x8000, 0x40, bit 20 x windows 0/5/42. 0 mismatches.
- `verify_draw.py`: block draw 0xC8F50 against `block_quads`. 672 cases: real lines, ruby,
  `@b` mirroring, redaction bars, flags 0x10/0x100/0x200/0x400/0x800, partial reveal, alpha.
  0 mismatches in texture, rect, position, scale and alpha.
- Harness notes: unicorn 2.1.4 runs VEX 3-operand ops wrongly. `emu.py` runs them in Python,
  extending `scratch/verify_fonts_reproduce/r2/vexfix.py` with packed and compare ops. The six
  `vmovups ymm` zero stores at VA 0xC7556 are done in one hook, because unicorn mis-executes
  them (the per-line glyph counts at +0x22 were garbage before this fix).

### 10.4 (d) Tests

`pytest tools/tests/test_textbox.py`: 12 tests in about 1.1 s. Without the game
(`TSUKI_GAME_DIR=/nonexistent`): 8 pass and 4 skip. `samples/textbox/engine_layout_golden.json`
freezes 601 engine layouts (every 367th line per language, ruby/tag/dash lines, and 80 bubble
layouts), so the real-data test needs no unicorn.

---

## 11. Python API

```python
from tsuki import textbox as tb
from tsuki.font import Font
f = Font("en")
p = tb.main_box_params(1)                    # BoxParams: 474->574, 220, 0, 9, 0.74, origin (30, 35)
b = tb.layout_message("  The familiar scent ...", 1, p, font=f)   # TextBlock (engine records)
tb.break_lines(b)                            # records per line
q = tb.block_quads(b, p.x, p.y, head=12.0, tail=9.0)   # shadow/outline/fill quads
img, blocks, origins = tb.render_page([msg3, msg4, msg5], 1, font=f, background=bg)
tb.window_for(5); tb.bubble_text_origin(5)   # window picture and text origin of F1105 = 5
```

CLI: `python -m tsuki.textbox layout <lang> <id>.. [--text T --window N --glyphs]`,
`render <lang> out.png <id>.. [--blank 0,1,..]`, `window <index>`.

---

## 12. Open questions

1. Text canvas: the canvas command in effect for display list 0x1E was not traced. The
   prologue movie advances 3.115 px per unit vertically, against 3.0 derived here (section 2).
   Next step: trace the parent list command (type 0xB, VA 0xDCB20) and texture mode byte +7
   (VA 0xE1F4C) for font pages.
2. Outline and read-text colour semantics. The bytes are `ff 00 00 00` and the expansion of
   0xDC6D. The vertex-colour byte order belongs to the PSSL shaders (`eboot.shaders`).
3. The input and auto-mode wait in Adv_Update (VA 0xA0B70) per end mode; `@k`'s runtime role
   beyond the backlog test.
4. `_FCSP` / `_FCLN` bodies (VA 0xB79C0 / 0xB7900): their effect on the window variables.
5. Producers of blocks slot+6 / slot+8 (bits 1, 2 of VA 0x16541D8) and flag 0x80 (F1102).
6. C<n> = F[n + F823]: inferred from the HUD; the variable dispatcher VA 0x987C5 was not read.
7. Previous-message dimming (5.8) is in the code but not visible in the movie. A timer
   condition is not understood.
8. F822 == 0 window path (slots 5 / 13, "window design").

---

## 13. Corrections to other documents

- fonts.md 7.2: "spacing computed for the main text box at 0x39CFD/0x39E1C" is wrong. Those
  are UI text widgets (4.5). The main box uses spacing 0.
- fonts.md 7.4: ruby is now known. Readings are placed by the kind-2 path with fixed scale
  0.1 x 0.11 units/texel, dy -7.04, advance w*0.1, centred by the estimate `S` and
  collision-guarded (5.4).
- text.md 6.2 `@h`/`@w`: the scale table multiplies the **undivided** sx/sy argument.
- text.md open question 4: `@f` values are box widths in text units (480/536/524), and `@x`
  digits are absolute pen positions in units.
