# Text vertical scale: text units to 1920 x 1080 pixels

Scope: FORMATS.md section 7, P0 item 1 ("the prologue movie spaces lines 81 px apart against 78 px
from the engine constants"), and the same question for the 640 x 344.7 choice canvas
(system_flow 5.6) and the other canvases that draw text. TSUKIHIME -A piece of blue glass moon-,
PS4 CUSA47999, app 01.01.

Python reference: `tools/tsuki/text_vscale.py`. Tests: `tools/tests/test_text_vscale.py` (20
tests, about 18 s with the game; 16 pass and 4 skip without it). Scratch tools, logs and pictures:
`scratch/text_vscale/`.

Confidence labels (as in agent_brief):

- **[V-emu]**: the game's own code was run under unicorn on real inputs and the result read back.
- **[E]**: read in the disassembly (text VA; file offset = VA + 0x2280).
- **[V-movie]**: measured on frames of `image/movie/prologue[_en].mp4`.
- **[H]**: hypothesis.

---

## 1. Answer

The page text is **not** drawn at 3 px per unit vertically. The HUD draws it into display list
0x1E and then draws that list as a nested list with the scale **(1.0, 1.3)** under the 640 x 448
canvas. Every vertex of the list (glyph positions and glyph quad corners) is mapped by

```
px = 3 * (x + ox)                          3.0 px per unit
py = (1080 / 448) * (1.3 * y + oy)         1.3 * 1080 / 448 = 3.1339286 px per unit
```

with `ox = oy = 0` on a still screen (section 3 gives the offsets) [V-emu]. The text space is
therefore **640 x 344.615 units** (448 / 1.3), not 640 x 360.

| text context | display list, canvas | x px / unit | y px / unit | evidence |
|---|---|---:|---:|---|
| ADV page text: current and previous messages, name plate, extra blocks, everything else the HUD puts into list 0x1E (for example the click-wait icon) | list 0x1E, nested (1.0, 1.3) under 640 x 448 | 3.0 | **3.1339286** | [V-emu] full HUD 0x53CF0 run |
| HUD pop-up of 0x53200 (blocks 0x1D, 9) | list 0x23, nested (1.0, 1.3) under 640 x 448 | 3.0 | 3.1339286 | [E] 0x5348A, constant 0x1A7080 |
| alternative choice list 0xA6D00 (choice mode != 1) | inside list 0x1E | 3.0 | 3.1339286 | [E] called at 0x550B6, before the pop |
| choice screen 0xA6E10 (`_SELR` menu) | canvas 640 x 344.7, not nested | 3.0 | **3.1331592** (1080 / 344.7f) | [V-emu] 0xA6E10 run |
| backlog 0x3C360 / 0x3C4B0 | canvas 640 x 344.7 | 3.0 | 3.1331592 | [E] 0x3C3C2, 0x3C57F |
| config labels, save / load, system menu, gallery text, HUD helpers 0xA7CD0 / 0xA8250 | canvas 640 x 448 | 3.0 | 2.4107143 | [E] (their layout `sy` arguments already carry a x1.24 compensation) |
| title text 0xB34C0 | canvas 960 x 540 | 2.0 | 2.0 | [E] 0xB34DC |
| UI widgets 0x3A030, dialogs 0xD0190, 0x6BBF0, debug 0xCECB0 | the calling screen's canvas | - | - | [E] no canvas call in the function |
| toasts 0x685B0 / 0x6AF30 | own lists drawn nested with an animated uniform zoom (s, s) | - | - | [E] 0x686EA, 0x6B103 |

Consequences:

- Line pitch of the main box: 26.02 units = **81.55 px** (the "78 px" was 26.02 x 3.0). A
  message step of 26 units is 81.48 px.
- Glyphs are scaled by the same transform. A 73-texel glyph cell at sx = sy = 0.74 / 3 spans
  54.0 px horizontally and 56.4 px vertically: **PS4 page text is 4.46 % taller than wide** (1.3 x 1080 / 448
  / 3 = 1.04464).
- The choice canvas 640 x 344.7 is the same text space written as a canvas: 1080 / 344.7 differs
  from 1.3 x 1080 / 448 by 0.025 % (0.19 px at y = 243 units).
- The prologue movie is not a render of this code path (section 7): its line positions advance
  3.115 px per unit and its glyphs are 3.0 px per unit in both directions. A port that follows
  the engine differs from the movie by about 1 px (en) to 5 px (ja) vertically.

---

## 2. The draw chain of the main text [V-emu, E]

### 2.1 HUD process 0xF (VA 0x53CF0, passed to Process_Register 0xECE30 at 0x56DAE, priority 0x50)

```
... window pictures, page layout ...
0xDCB10(1); 0xDC760()                      push the current (parent) list         VA 0x5406E, 0x54073
0xDC740(0x1D)                              window pictures                        VA 0x5407D
...
0xD9DD0(1/3, 0.4148148)                    picture scale (glyphs override it)    VA 0x5448D
0xDCB10(1); 0xDC740(0x1E)                  page text list                        VA 0x54497, 0x544A1
   block draws 0xC8F50 of blocks 14, 4/5, 6, 8, 0x4F+j, ...  (no canvas call in this range)
   0xA6D00(alpha)                          alternative choice list, mode != 1    VA 0x550B6
0xDC790()                                  pop: back to the parent list           VA 0x550C2
0xDCE30(0x1D, a * 256, ox, oy, 0, 1, 1)    window pictures, nested (1, 1)         VA 0x55809 (via 0x557E4)
0xA6E10(alpha)                             choice screen, if [0x1653E00] & 0x10   VA 0x5511D
0xDCBE0(480, 272, 0, 0)                    face area                              VA 0x5513A
0xD9D20(1, 1, 1, 1); 0xDCB10(1)                                                   VA 0x554B5, 0x554BF
0xDCBE0(640, 448, 0, 0)                    canvas for the text list               VA 0x554DC
0xDCE30(0x1E, a * 256, ox, oy, 0, 1.0, 1.3)   type 9 record: draw list 0x1E nested VA 0x555C1
```

The constants: 640 / 448 at 0x1A70CC / 0x1A70D0; sx = 1.0 from 0x1A70D8 into xmm3 at
0x5559C; **sy = 1.3f (0x3FA66666) from 0x1A71A8 into xmm4 at 0x555A4** (the only reference to
that constant); edi = 0x1E at 0x555AC. `a` = 2 max(0, T - 0.5) x window fade (0xDF0730) x HUD
alpha (0x781148) on story pages (VA 0x55225..0x55598), truncated after x 256 (VA 0x555B1..0x555B9).

The parent list is the list current when the HUD process runs (list 0 in the emulation). The
parent records the HUD emits, read back from the emulated run of 0x53CF0 (en, message
`$000003`; ja gives the same records) [V-emu]:

```
type 12  canvas 640 x 362.667, offset (0, 44)      VA 0x53D9E (scene canvas; v = 128 -> trunc(128*3/1024*118) = 44)
type 12  canvas 640 x 448                           VA 0x53DE2
type  9  list 0x1D, alpha 256, scale (1, 1),   offset (0, 0, 0)
type 12  canvas 480 x 272                           VA 0x5513A
type 12  canvas 640 x 448                           VA 0x554DC
type  9  list 0x1E, alpha 256, scale (1, 1.3f), offset (0, 0, 0), colour ff ff ff
```

### 2.2 Type 9 record "draw nested list" (builders 0xDCE30 and 0xDCEC0)

| off | field |
|---:|---|
| +0x02 | u16 type = 9 |
| +0x04 | i16 list index (refused if it is the current list or > 0x1F3) |
| +0x06 | i16 alpha x 256 (0xDCEC0 stores 0x100) |
| +0x08, +0x0C | f32 sx, sy (xmm3, xmm4) |
| +0x10..+0x12 | colour bytes, set to ff ff ff by both builders |
| +0x14, +0x18, +0x1C | f32 x, y, z (xmm0..xmm2) + the global screen offset 0x1DBD3EC / 0x1DBD3F0 / 0x1DBD3F4 (0 normally) |

Executor 0xD9E10, case 9 (VA 0xDA04C) [E]: colour not white -> 0xDA1B0; alpha <= 255 ->
0xDA780; scale (1, 1) and offset 0 -> 0xDACB0 (plain); otherwise -> **0xDB0E0**. Every variant
transforms the nested list's vertices the same way: for type 1 (textured quad) and type 7 (fill)
records, each vertex `v' = v * (sx, sy) + (x, y)` in float32 (`vmulps` then `vaddps`, 0xDB0E0 VA
0xDB325..0xDB357; 0xDA780 VA 0xDA9AB..; 0xDA1B0 VA 0xDA454..), then the textured-quad draw
0xD2BE0 applies the projection in effect. Type 12 records inside a nested list set the
projection as at top level; type 6 captures restore the 640 x 448 default.

On the alpha path 0xDA780 every vertex alpha byte of the nested quads becomes
`(alpha_byte * alpha) >> 8` (integer, VA 0xDA9BF..0xDAA11) [E].

Nested inside nested (VA 0xDB4DC..0xDB624): the inner record's scale is multiplied by the outer
scale, but its offset is **added** to the outer offset without being scaled by it [E]. This
matters only for nested lists inside a scaled nested list; the text lists are one level deep.

### 2.3 Projection (type 12, 0xDCBE0 -> executor 0xD9E52; nested executors 0xDA849, 0xDB1A1)

```
scale  = (1920 / w, 1080 / h)                        float32 divisions, constants 0x2D4034.. (also 0x2D4064.., 0x2D4094..)
offset = (ox - 960, -1080 * oy / 272 - 540)          centred clip coordinates
centred = v' * scale + offset   (0xD2BE0 VA 0xD2CCA..0xD2D16)      screen pixel = centred + (960, 540)
```

So list 0x1E text lands at `px = 3 x`, `py = 2.4107143 x 1.3 y = 3.1339285 y` [V-emu].

### 2.4 Glyph quads (0xE1DC0) [V-emu]

The block draw 0xC8F50 sets the picture scale to the record's (sx, sy) (0xD9DD0 at VA 0xC9A66)
and calls 0xE1DC0(texture slot, flags 0x10 when scaled, rect, colour; x, y). For the font
pages, 0xE1DC0 emits one type 1 record per glyph with

```
corners (x, y) .. (x + (u1 - u0) * sx, y + (v1 - v0) * sy)       in text units (the list's units)
uv      (u0, v0) / 1024 .. (u1, v1) / 1024                       u1, v1 = the record's "x+w-1, y+h-1"
```

so a quad covers (u1 - u0) x (v1 - v0) texels and the last column and row of the record's
rectangle are not drawn (they are blank on the font pages). Example from the emulation: the
first record of `$000003` (en), rect (0, 0, 31, 72), sx = sy = 0.2466667, at (30, 35): list 0x1E
vertices (30, 35)..(37.646667, 52.76); after the nested scale (30, 45.5)..(37.646667, 68.588);
on screen (90.0, 109.6875)..(112.94, 165.346) px.

**Texture size modes do not apply to font pages.** 0xE1DC0 multiplies the quad width / height by
4/3 / 1.24444 (0x2D4214 / 0x2D4220) only for slots whose mode byte +6 / +7 is 2. 0xD8B80 sets
those bytes from bits 5..7 of the image header `depth` byte (VA 0xD8CD1..0xD8D8E: 0x20 -> both
x2, 0x40 -> width x2, 0x60 -> height x2, 0x80 -> 4/3 and 448/360, otherwise 0). Font pages
have depth 0x01 (fonts.md 3.1), so both bytes are 0 and the quad is drawn at its record scale
[E: 0xD8B80; the emulations use font slot records built from that reading, mode bytes 0]. This
closes the "texture mode byte at 0xE1F4C"
lead of textbox.md 12.1 and system_flow 5.6.

---

## 3. Offsets of the text list (HUD VA 0x53F33..0x53F70, 0x551E9..0x555C1) [E]

The type 9 record's (x, y), in 640 x 448 canvas units, added **after** the 1.3 scale:

```
(sx, sy) = shake (0xDF0720, 0xDF0724), or (0, 0) when bit 0 of byte 0xDF07C8 | 0xDF07CC is set
(qx, qy) = message-window quake: x 0xDF08B8, y 0xDF08B4 (0x52F80, sin-based; mode 0xDF08BC:
           1 = x only, 2 = y only, 3 = both with x times 0.4 (0x1A7044))
ox = trunc(sx) + trunc(qx)                                           cvttps2dq: toward zero
F822 == 1 (story page):  oy = trunc(sy) + trunc(qy)
F822 != 1:               oy = trunc(sy) + 302.8148 (0x1A7188) + slide + trunc(qy)
                         slide = (1 - window fade) * 20 (0x1A7184) when dword 0xDF074C == 2, else 0
```

`tsuki.text_vscale.hud_text_offsets()` implements this. An offset of 1 canvas unit moves the
text 3 px horizontally or 2.4107 px vertically (it is not multiplied by 1.3). The window
pictures (list 0x1D) get the same (ox, oy) with scale (1, 1).

---

## 4. Other text contexts [E, census]

`scratch/text_vscale/text_sites.py` lists, for each of the 22 functions that call the block draw
0xC8F50 (44 call sites), the ordered canvas / list / nested-draw / picture-scale calls with their
constant arguments (`text_sites.json`). Summary:

- **Choice screen 0xA6E10** (system_flow 5.3): `SetCanvas(640, 448)` at 0xA6ED7 for the
  vignette, then **`SetCanvas(640, 344.7)` at 0xA6F3C** (0x2168CC = 0x43AC599A), then all its
  block draws, then it restores the caller's canvas (0xDCC50 / 0xDD010 at entry). No list change
  and no nested draw, so its text maps at 1080 / 344.7 = 3.1331592 px per unit, glyph heights
  included [V-emu: an emulated 0xA6E10 run emits canvases 640 x 448, 640 x 344.7, 480 x 272 and
  glyph quads with projection scale (3.0, 3.1331592); the first glyph at y = 34.934246 lands at
  109.455 px and is 55.65 px tall].
- **Backlog 0x3C360 / 0x3C4B0**: canvas 640 x 344.7 at 0x3C3C2 (0x1A644C) and 0x3C57F, then the
  block draw, then the caller's canvas back.
- **640 x 448 screens** (config 0x40C30 under 0x3F140, save / load 0x70340, menu 0x6CB80,
  0x2A530, HUD helpers 0xA7CD0 / 0xA8250): text in canvas units, 3.0 x 2.4107 px per unit. Their
  layout calls pass `sy / sx` of about 1.24 (config 0.74 / 0.9176, save / load 0.3294 / 0.4085,
  0xA7CD0 0.37 / 0.4604; textbox.md 4.5), which makes the glyphs nearly square (1.24 against
  448 / 360 = 1.2444).
- **Title 0xB34C0**: canvas 960 x 540 (2.0 px per unit).

The float 1.3 occurs four times in the text segment with code references: 0x1A71A8 (HUD list
0x1E, VA 0x555A4), 0x1A7080 (pop-up list 0x23, VA 0x53455), and two uses unrelated to text
(0x1A6D64 in 0x48220, 0x22AB68 in the motion code 0xBC460).
The speech-bubble text origin uses the factor 0.9578 (0x1A7104, VA 0x54B29 / 0x55877) on the
1280 x 720 y offsets, which is close to 344.8 / 360 [H: a hand-tuned correction for the
344.6-unit text space].

---

## 5. Display list 0x1E and VA 0xDCB20 (record type 0xB)

The open problem pointed at `0xDCB20` as the "parent list command". It is not:

- 0xDCB20(a, b) appends a type 0xB record (+4 = a, +6 = b). Its only callers are the scene
  draw 0x59C30 (VA 0x59D8E `(1, 2)` and 0x59DE9 `(2, 1)`, around two layer quads) and four sites
  in 0xCE4FC..0xCE825.
- **Type 0xB is a no-op in every executor**: the jump tables of 0xD9E10 (0x2D53F8), 0xDA780
  (0x2D5458), 0xDB0E0 (0x2D54B8) and 0xDB6D0 (0x2D54E8) all send index 10 (type 11) to their
  "next record" label [E]. It looks like a leftover of a stencil / layer-mask command.
- List 0x1E is drawn by the type 9 record of section 2.

---

## 6. Worked examples [V-emu]

`$000003` ("  The familiar scent ... hangs / in the air.", en, origin (30, 35)) and the ja page:

| item | text units | old model (3.0) | engine (this document) |
|---|---|---:|---:|
| en line 1 top | y 35 | 105.00 px | **109.69 px** |
| en line 2 top | 35 + 26.02 = 61.02 | 183.06 | **191.23** |
| en message 4 (`$000004`) | 87 | 261.00 | 272.65 |
| en message 5 lines | 113, 139.02 | 339.00, 417.06 | 354.13, 435.68 |
| ja line tops | 49, 75, 101 | 147, 225, 303 | 153.56, 235.04, 316.52 |
| ja ruby (dy -7.04) | 41.96 | 125.88 | 131.50 |
| glyph cell, 73 texels at 0.74 / 3 | 72 x 0.24667 = 17.76 (quad) | 53.28 | 55.66 |
| choice line 2 of `01_00_ARC01_1` (en, system_flow 5.5) | 217.06 | - | 680.09 (canvas 344.7) |

The emulated runs print exactly these: `python -m tsuki.text_vscale emulate en` and
`scratch/text_vscale/hud_emu.py ja` (glyph tops 109.688 / 191.232 px en; 153.562 px and ruby
131.500 px ja), `choice_emu.py` (109.455 px).

---

## 7. The prologue movie is not an engine render [V-movie]

`movie_glyph_edges()` measures, for every glyph of the four known frames
(`samples/textbox/prologue_{en,ja}_{25,150}s.png`, 00_02 messages 3..5 and 37..38), the
half-maximum ink edges in the movie (luma, full resolution in the 4:2:0 stream; pixel j centre at
j + 0.5) and the same edges on the font atlas (texel k centre at k + 0.5), with windows limited
to the glyph's own ink so that neighbours and ruby do not leak in. A robust least-squares fit
(3-MAD rejection) then gives:

| fit | en (276 vertical edges) | ja (85) |
|---|---|---|
| position scale k_pos, glyph scale free | 3.1147, k_glyph 3.0299, c +1.84 px (rms 0.33) | 3.1158, k_glyph 3.0385, c +5.96 (rms 0.19) |
| k_glyph fixed 3.0 | k_pos 3.1152, c +2.07 (rms 0.25) | k_pos 3.1144, c +6.39 (rms 0.40) |
| k_glyph fixed 3.1339 (engine) | k_pos 3.1148, c +0.85 (rms 0.65) | k_pos 3.1151, c +5.19 (rms 0.98) |
| horizontal (256 edges) | k 2.9958, residual at k = 3: +0.36 px | k 2.9956, +0.87 px |
| per-glyph height ratio (median) | 3.000 / 3.001 (150 s / 25 s) | 3.015 / 3.017 |

Median movie-minus-engine residuals per line (top edge / bottom edge, engine mapping of
section 1): en 25 s +0.66 / -0.64, +0.18 / -0.45, -0.41 / -1.41, -0.64 / -2.16, -1.21 / -1.84 px
for lines at y 35, 61.02, 87, 113, 139.02; ja 25 s +4.7 / +3.6, +4.3 / +3.2, +4.2 / +2.5 px.
The old 3.0 model is +9.0 (en) and +14.9 (ja) px off.

So the movie places lines with a vertical scale of 3.115 px per unit (81.0 px pitch), one value
for both languages and both line and message steps, but draws the glyphs undistorted at
3.0 px per unit, and the ja page sits 4 px lower than the en page. No linear vertex transform
does that, and the PS4 path of section 2 stretches glyphs by 4.46 %, so the movie was made by
another renderer [H: the KiriKiri master the game was built on (engine_lineage.md); 81 px is
then the master's line pitch, which HuneX approximated with the 1.3 factor: 26.02 x 3.134 =
81.55 px]. `tsuki.text_vscale.MOVIE_PROLOGUE` records the movie model (kx 3.0, ky_pos 3.1152,
ky_glyph 3.0, oy +2.07 en / +6.39 ja). `tsuki.textbox.PROLOGUE_TRANSFORM` (ky 3.1154, kgy 3.0,
oy 0) is the same model without the per-language offset.

Side-by-side masks: `scratch/text_vscale/sbs_{en,ja}_25s.png` (top: old 3.0 model, bottom:
engine mapping; red = movie only, blue = render only, white = both). Viewed: with 3.0 the render
rises above the movie line by line (about 20 px by the fifth en line; 13 to 15 px on every ja
line); with the engine mapping the en lines overlap and the ja glyphs show a 3 to 4 px red
fringe at their bottoms. White-mask overlap in the text area: en 5,362 -> 12,814 px, ja 1,821
-> 4,428 px.

---

## 8. What `tools/tsuki/textbox.py` and the C# port must change

`tools/tsuki/textbox.py` (owned by the textbox port; not edited here):

1. `ENGINE_TRANSFORM = CanvasTransform()` (3.0 / 3.0) is wrong for the PS4. Use
   `CanvasTransform(kx=3.0, ky=1.3 * 1080 / 448, ox=0.0, oy=0.0, kgy=1.3 * 1080 / 448)`, which
   is `text_vscale.MAIN_TEXT.canvas_transform()`. `render_page()` and anything that turns page
   text into pixels must use it; it is the transform of list 0x1E, so it also applies to the
   name plate and the previous messages of the page.
2. The documentation strings "text space 640 x 360, 3 px per unit" become "640 x 344.615 units;
   3.0 px per unit in x, 3.1339286 in y (positions and glyph sizes)". `PROLOGUE_TRANSFORM` stays
   as a movie-only model; its comment should say that the movie is not an engine render.
3. Minor: `render_quads()` draws `u1 - u0 + 1` x `v1 - v0 + 1` texels; the engine draws
   `u1 - u0` x `v1 - v0` (2.4). The texel-to-pixel mapping is the same, only the blank last
   row and column differ.
4. The HUD offsets (shake, window quake, the F822 != 1 bottom offset 302.8148) are added after
   the scale, in canvas units, each truncated toward zero (section 3).

`tools/tsuki/misc_boot_title_choice_flow.py` (not edited here): the choice lines use
`CanvasTransform(kx=3.0, ky=1080 / 344.7, kgy=3.0)` at line 1387; `kgy` must be `1080 / 344.7`
too (`text_vscale.CHOICE_SCREEN`), and the page text behind the choice must use the HUD mapping
of point 1.

C# port:

- **Package D1 (display-list executor, `src/Tsuki.Render`)**: implement record type 9 as in 2.2:
  every vertex of the nested list becomes `v * (sx, sy) + (x, y)` (float32, multiply then add),
  then the current projection; alpha +6 (256 = opaque) and colour +0x10..0x12 multiply the
  nested quads; nested-in-nested scales multiply and offsets add unscaled. Type 0xB records are
  ignored. Projection type 12: `scale = (1920 / w, 1080 / h)`, `offset = (ox, -1080 oy / 272)`.
- **Package B (`src/Tsuki.Engine/Text`)**: `BlockDraw.Quad` stays in text units (correct).
  Replace "640 x 360 text space" in `MessageWindow.cs` comments; quad size `(U1 - U0) * Sx` by
  `(V1 - V0) * Sy` (2.4).
- **The HUD / ADV message window package (after B and D1)**: emit the page text into its own
  list and draw it with `NestedDraw(1.0, 1.3f)` under canvas 640 x 448 with the section 3
  offsets, exactly as 0x53CF0 does; or, if text is drawn directly, map
  `px = 3 x + 3 ox`, `py = 3.1339286 y + 2.4107143 oy` and scale glyph quads by the same factors.
- **Package G (`uicalls`, choice)**: choice text under canvas 640 x 344.7, glyph heights at
  1080 / 344.7.
- **Package H3 (`tools/oracle/render.py`)**: the page-1 oracle render must use the HUD mapping;
  the comparison with `prologue_en.mp4` must allow the movie's own offsets (en within 2.2 px,
  ja within 5 px at the glyph tops and bottoms; or compare against `MOVIE_PROLOGUE`).
- RUNTIME_REQUIREMENTS 9.1 item 1 and the M2 / slice criterion that excused "81 vs 78 px":
  the engine value is now 81.55 px per line; differences against the movie are the movie's.

---

## 9. Evidence (commands run)

All with `PYTHONPATH=tools`, `.venv/Scripts/python.exe`, game folder read-only.

- Disassembly of 0xDCB20, 0xDCB10, 0xD9E10 (`exec_d9e10.asm`, jump table 0x2D53F8 decoded:
  type 11 -> 0xD9EC0), 0xDCE30, 0xDCEC0, 0xDB0E0, 0xDA780, 0xDA1B0, 0xDACB0, 0xD2BE0, 0xE1DC0,
  0xD8B80, 0x59C30, 0x53200, 0x5CBF0, 0xA6D00, 0xA6E10, 0xDB6D0 (files `scratch/text_vscale/f_*.asm`);
  the HUD listing `scratch/textbox/hud_53cf0.asm`; float constants read at the VAs cited.
- `scratch/text_vscale/emu_chain.py`: the HUD call sequence around list 0x1E (0xDC740,
  0xD9DD0, 0xDCB10, 0xDC760, 0xC6C20 layout, 0xC8F50 block draw with the real 0xE1DC0,
  0xDC790, 0xD9D20, 0xDCBE0, 0xDCE30) and the executor 0xD9E10, with only 0xD2BE0 / 0xD7E70 /
  0xD27E0 / 0xD47C0 stubbed. Output `emu_chain_1.json`: list 0 = [type 12 640 x 448, type 9 (30,
  256, 1.0, 1.3)], 211 records in list 0x1E, 140 quads projected with scale (3.0, 2.41071).
- `scratch/text_vscale/hud_emu.py en|ja`: the whole HUD draw 0x53CF0 for one message
  (`hud_emu_{en,ja}.json`); parent records as in 2.1; glyph tops 109.688 / 191.232 px (en),
  153.562 px and ruby 131.500 px (ja).
- `scratch/text_vscale/choice_emu.py en`: the choice draw 0xA6E10 for `$042570`
  (`choice_emu_en.json`): canvases 480 x 272, 640 x 448, 640 x 344.7, 480 x 272; projection scale
  y 3.1331592; first glyph 109.455 px.
- `scratch/text_vscale/text_sites.py` -> `text_sites.json`: the 22-function census of section 4.
- `scratch/text_vscale/measure.py`, `measure2.py`, `fit_split.py`, `measure_h.py`: the movie
  measurement of section 7 (`measure2_rows.json`, `measure_report.json`); the module versions
  are `movie_glyph_edges`, `fit_movie_vertical`, `fit_movie_horizontal`
  (`python -m tsuki.text_vscale movie` prints the table of section 7).
- `scratch/text_vscale/sbs.py` -> `sbs_{en,ja}_25s.png`, viewed (section 7).
- `python -m tsuki.text_vscale verify`: 11 / 11 static checks OK (projection constants, HUD
  canvas and nested scale operands, list ids, type 9 field stores, the vmulps / vaddps of
  0xDB0E0, choice and backlog canvases, 302.8148).
- `pytest tools/tests/test_text_vscale.py`: 20 passed in 17.3 s; with
  `TSUKI_GAME_DIR=/nonexistent` 16 passed, 4 skipped.

---

## 10. Errata

### Errata for textbox.md

- Section 1 and 2, "text space = 640 x 360 units, 3 px per unit" and the table row "px = 3 x,
  py = 3 y": vertically it is 3.1339286 px per unit (1.3 x 1080 / 448), for positions and glyph
  sizes; the text space is 640 x 344.615 units. Proof: sections 2.1..2.4 [V-emu].
- Section 2, "Which canvas command is in effect when it runs could not be traced statically [H:
  640x360 via the per-texture vertical factor 1.24444 ...]": the canvas is 640 x 448 (VA
  0x554DC) and list 0x1E is drawn nested with scale (1.0, 1.3) (VA 0x555C1). The 1.24444 factor
  belongs to texture size mode 2, which font pages never have (2.4).
- Section 2, "This document treats 3 px/unit as the engine value and the movie's vertical
  spacing as a difference in the movie build [H]": the engine value is 3.134; the movie differs
  from the engine in a different way (section 7).
- Section 2, glyph quads "w * sx * 3 x h * sy * 3 px": `(u1 - u0) * sx * 3` by
  `(v1 - v0) * sy * 3.134` px (2.4).
- Section 10.1 table: the "engine 3 px/unit" rows describe the old model; with the engine
  mapping the en baselines are within about 2 px of the movie and ja about 4 px (section 7).
- Section 12.1: resolved; type 0xB (0xDCB20) is a no-op record (section 5).
- Sections 3.1 item 1 and 8.2, "T only scales the **outline** alpha": the HUD also passes
  `trunc(256 x 2 max(0, T - 0.5) x window fade x HUD alpha)` as the alpha of the type 9 record of
  list 0x1E (VA 0x55540..0x555B9), and 0xDA780 multiplies every vertex alpha of the list by it
  (section 2.2). So T (above 0.5) and the window fade A also fade the fill and the outline of
  the page text as a whole; the outline additionally keeps its own `2 max(0, T - 0.5)` factor
  from the block draw [E; found while tracing, not emulated with T < 1].

### Errata for system_flow.md

- 5.6 "The page text keeps the textbox model (3.0 px per unit)": it is 3.1339 px per unit.
  "The glyph height in the renders is kept at 3 px per unit, because it depends on the font-page
  texture mode byte (+7, VA 0xDD82C ...). That byte was not traced for font pages": font pages
  have mode 0 (depth 0x01, VA 0xD8CD1); choice glyph heights are 3.1331592 px per unit. The
  emulated 0xA6E10 confirms the canvas (section 4).

### Errata for FORMATS.md

- Section 7, P0 item 1: answered by this document. The coordinate table row "text space 640 x
  360 | ... | 3 px per unit (prologue movie: 3.115 vertically, open)" becomes "text space 640 x
  344.6 (list 0x1E nested (1, 1.3) under 640 x 448) | 3.0 x 3.1339 px per unit; choice and
  backlog canvas 640 x 344.7: 3.0 x 3.1332".

### Errata for compositing.md

- Section 6 table, type 9 "draw nested list a": add the scale (+8, +0xC), offset (+0x14..+0x1C),
  alpha (+6) and colour (+0x10) semantics of 2.2 and the nested composition rule. Type 11
  (0xDCB20) is accepted by no executor.

### Errata for RUNTIME_REQUIREMENTS.md

- 9.1 item 1 / line 1198 and the slice note "vertical difference (81 px movie line pitch vs 78 px
  engine) is not a slice failure": the engine gives 81.55 px; the remaining difference to the
  movie is the movie's (section 7).

---

## 11. Open questions

1. A PS4 capture (docs/status/ps4_captures.md T04, T10) would confirm the 4.46 % vertical
   stretch on the console: predicted for `$000003` (en) glyph cells 54.0 px wide x 56.4 px tall,
   first line top 109.69 px, line pitch 81.55 px; on the choice screen 3.1332 px per unit.
2. The renderer of the prologue movie (KiriKiri master or another tool) and why its ja page is
   4 px lower than its en page [H].
3. The F822 != 1 path (bottom window, +302.8148) and the pop-up 0x53200 were read, not
   emulated. Its window-slide term and the animated pop-up offsets (0xDF08CC / 0xDF08D0) are [E].
4. GPU rasterisation details (pixel-centre convention, bilinear half-texel) decide sub-pixel
   placement; the movie's horizontal +0.4 to +0.9 px offset at k = 3.0 may come from them or from
   the movie's renderer.
5. Draw order [E]: the parent list receives the window list 0x1D, then the choice screen 0xA6E10
   (vignette, choice lines), then the type 9 record of list 0x1E. The page text is therefore
   drawn after (over) the choice vignette; system_flow 5.3 / 5.5 describe the vignette as dimming
   the page text. Whether the page text alpha is lowered during choices by another path was not
   checked [H].
6. 0x5CBF0 draws 14 script objects (0xDF1830, stride 0x134) through lists 0x50 + k with the nested
   scale (1.0, 0.93333) (= 448 / 480, 0x200C80); not text, noted for the layer port.
