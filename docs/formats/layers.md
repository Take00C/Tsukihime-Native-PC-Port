# Scenario layers: per-layer state, layer commands, commit, tweens, motions, projection

Game: TSUKIHIME -A piece of blue glass moon- (PS4 CUSA47999, app 01.01). Engine: HuneX
library in `eboot.bin`. Every address is a text/data VA of this build (text VA = file offset
- 0x2280, data VA 0x600000 = file 0x365FA0).

Reference implementation: `tools/tsuki/misc_layer_state_and_motions.py` (pure Python, no
emulator needed). Ground-truth harness: `tools/tsuki/misc_layer_state_and_motions_emu.py`
(the engine's own code under unicorn; `Rig`). Tests: `tools/tests/test_misc_layer_state_and_motions.py`.
Checks and logs: `scratch/misc_layer_state_and_motions/xf/`. Samples:
`samples/misc_layer_state_and_motions/`. The short pointer `formats/misc_layer_state_and_motions.md`
points here. Names: the gap statement asked for `tsuki/layers.py`, `tests/test_layers.py`,
`samples/layers/` and `scratch/layers/`; the work uses the `misc_layer_state_and_motions` names of
the project brief's test-naming rule (`test_misc_<slug>.py`). `tsuki.misc_layer_state_and_motions`
**is** the `tsuki.layers` module of the gap statement (an alias module `tsuki/layers.py` with
`from tsuki.misc_layer_state_and_motions import *` can be added by whoever owns that path).

Confidence tags: **[V-emu]** the model was compared with the engine's own code run under
emulation (numbers in section 11); **[V-all]** checked on all retail data; **[E]** read in the
disassembly, not separately executed; **[H]** hypothesis. Blend modes, colour effects, rule
transitions and the shader side of masks/crop are documented by the compositing gap
(`formats/compositing.md`); this document only says which fields they read.

---

## 1. Overview

```
script statement --handler--> scene_state (VA 0x16500C0, 0x1A08 bytes, per-layer arrays)  [section 3, 4]
        |                         \-- some handlers also act on the drawn record A at once ("live": tweens, STMA key 0, STPV ...)
     _FADS --commit VA 0x7DD10--> record array B (VA 0xDE2650, 40 x 0x2C8)                  [section 5]
per-frame update VA 0x57980: B -> drawn array A (VA 0xDDB5E8), tweens, motions, shakes        [section 6, 8]
scene draw VA 0x56DE0: layers sorted by z -> 0x59EA0 -> 0x5A5E0 -> 0x51060 -> 0xEB780 -> 0xDD6D0
        picture texels --(2x3 affine, section 7)--> scene canvas 640 x 362.667 --x3, x2.978--> 1920 x 1080
```

* 40 layers, 0..39. Layer changes made by statements become visible at the next `_FADS`,
  except for the live effects listed in 4.3 (a timed `_STGS` fades a visible layer at once).
* Everything a layer can do is a field of its record: picture, position, anchor (pivot), scale,
  zoom (mirror = negative), rotation, alpha, z order, parent layer, mask layer, crop rectangle,
  blend mode, colour effect, motion, quake/shake/swing, noise, rule.
* Picture placement: the catalog anchor of the picture lands on the layer position; scale,
  zoom and rotation act about that point; a parent (`_SBAS`) carries its children in its own
  picture frame (section 7).

## 2. Units

| quantity | unit | to frame pixels |
|---|---|---|
| script x / y (STCH, STPV, STCL, motion fields 3/4/6/7) | 480 x 272 script space | stored as engine units `trunc(x*640/480)`, `trunc(y*480/360)` (C division, VA 0x82941 / 0x829A6) |
| engine / canvas units (records, anchors) | 640 x 362.667 canvas | x 3 = 1920/640; y x 2.97794 = 1080/362.667 |
| picture texels | MZP pixels | at scale 1000 and zoom 1000: 1 texel = 1 frame pixel (global scale 1/3, 0.3358025 canvas units per texel) |
| scale (STCH a4), zoom (STZ4, fields 9/10) | per mille | negative zoom mirrors |
| rotation (STRT, STPV a3, field 8) | 1/100 degree, clockwise on screen | draw angle = `trunc(rot*32768/18000)` in 1/65536 turn |
| SOXY offsets | 1920-pixel units | x / 3, y x 448/1080 canvas units (the y factor is the 640x448 UI canvas's; [V-emu]) |
| alpha | 0..255 in scene_state, 0..1 float in records | |
| durations (STGS, STZ4, STRT, STCL) | ms | frames = `trunc(ms*60/1000)` |
| durations (SQK2/SHK2/SSW2/STRA) | ms | frames = `trunc(3*ms/50)` (same value, different code) |
| motion time | frames in the record | ms = `q * 50 / 3`, q = `frames - frames % n` for an STMA divisor n >= 2, else frames (8.2) |

## 3. scene_state (VA 0x16500C0, 0x1A08 bytes) [V-emu]

Every per-layer field is an array of 40 entries at `0x16500C0 + off + stride*layer`. The
handlers save a copy of the whole block to VA 0x1651AC8 (and call the history snapshot
0x7AF90) before the first change after a commit (word 0x164B98E). Defaults are the values
after `_STCC` / scene start.

| off | type | name | default | written by | committed to (record +off) |
|---:|---|---|---|---|---|
| 0x004 | i16 | pic | -1 | STCH SCH2 (STCC) | +0x18 i32 (0x49F60; -1 if >= 0x59D8) |
| 0x054 | i16 | blend | 0 | STBR, motion f17 | +0x1C |
| 0x0A4 | u8 | flags | 0 | STBR a3=1 sets 0x80; SACL sets/clears 0x40 | +0x1E |
| 0x0CC | i8 | fx_mode | 0 | STEC (and STCH from word 0x164B97C) | +0x1F |
| 0x0F4 | i16 | fx_a | 0 | SNX2, motion f24 | +0x20 |
| 0x144 | i8 | fx_b | 0 | SNX3, motion f25 | +0x22 |
| 0x16C / 0x1BC | i16 | x / y | 0 | STCH SCH2 STCL, motion f3/f4 | +0x30 / +0x34 f32 |
| 0x20C | i16 | alpha | 255 | STCH SCH2 STGS, motion f5 | +0x2C f32 = alpha/255 |
| 0x25C | i8 | state | 0 | STCH SCH2 (0) | +0x24 i32; at the commit states 0x21 and those in mask 0xF053C70 clear the picture, then state = 0 |
| 0x284, 0x2AC, 0x2D4 | u8 x3 | - | 0 | only copied by STCH/SCH2 with a source layer | - |
| 0x2FC | i16 | z | 0 | STCH (default 10*layer) SCH2 STCP, motion f27 | z order (0x4A180 sorts) |
| 0x350 | i64 | rot | 0 | STRT, STPV a3, STCH (0), motion f8 | +0x58 |
| 0x490 | i16 | scale | 1000 | STCH SCH2 a4 | +0x70 |
| 0x4E0 / 0x530 | i16 | anchor_x / anchor_y | 0 | STCH SCH2 (catalog anchor), STPV, motion f6/f7 | +0x76 / +0x78 |
| 0x580 + 0x50k, k=0..9 | i16 | - | 0 | only copied by STCH/SCH2 with a source layer | - |
| 0x8A0 / 0x8F0 | i16 | zoom_x / zoom_y | 1000 | STZ4 a3/a4; STCH and SCH2 reset to 1000 | drawn record +0xAA/+0xAC via 0x49930 |
| 0x940 | i8 | zoom_pivot | 0 | STZ4 a7; STCH/SCH2 reset | drawn record +0xB0 |
| 0x968 | i16 | mask | -1 | STCH a11 | +0x120 i32 |
| 0x9B8 | i16 | sttr | -1 | STTR; set to -1 by every STCH / SCH2 with a picture name (0x82041) | +0x12C i32 |
| 0xA08 | i32 | parent | -1 | SBAS | +0x128 |
| 0xAA8 / 0xAD0 | i8 | soxy_x / soxy_y | 0 | SOXY a1/a2 | +0x130 / +0x131 |
| 0xAF8 | 4 x i16 | crop | 0 | STCH a12..a15, motion f11..f14 | +0x132 |
| 0xC38 | u16 | a16 | 0 | STCH a16, motion f16 | +0x13A |
| 0xC88 | u8 | a17 | 0 | STCH a17, motion f15 | +0x13C |
| 0xCB0 | i16 | angle | 0 | STCH a19, STRR, motion f8 | +0x15C (no reader in the draw path: no visible effect on PS4 [E]) |
| 0xD00 | 24 B | motion | 0 | STMA SPMA, motion time (0xBE663) | +0x160 (0xBA8E0): +0 i16 id, +2 i16 divisor, +4..+7 unused, +8 i64 frame counter (the scene copy holds the quantised count), +0x10 u8 pause, +0x11..+0x17 unused |
| 0x10C0 | 2 x u8 | shake_kind | 0 | SQK2 (slot 0 = 1), SHK2 (slot 1 = 2), SSW2 (slot 1 = 3) | +0x178 |
| 0x1110 | 2 x i16 | shake_fade | 0 | a5 (SQK2/SHK2) / a7 (SSW2), frames | +0x17A |
| 0x11B0 | 2 x i16 | shake_time | 0 | duration frames, -1 = until stopped | +0x17E |
| 0x1250 | 2 x 6 x i16 | shake_par | 0 | parameters (4.4) | +0x182 / +0x18E |
| 0x1610 | i16 | stra | 0 | STRA | +0x2C8 |
| 0x1660 / 0x1688 | u8 | stns / stns_b | 0 | STNS, motion f26 | +0x1D0 / +0x1D1 |
| 0x16B0 | 18 B | ext | 0 | +2/+4 i16 picture offset = -anchor (STCH, STPV, motion f6/f7); +6..+8 RGB colour (STEC mode 1, motion f19..f21; white after STCH/SCH2); +0, +1, +0xE.. copied | +0x50/+0x54 f32 offset (0x49FD0), colour block +0x1F0 (0xF760), +0x2A8 (0xF2F0) |
| 0x19AB / 0x19D3 | u8 | - | 0 | only copied by STCH/SCH2 with a source layer | - |

Globals in the block: +0x0 / +0x2 i16 (commit: 0x49C20 / 0x49C30), +0x1980 i16 (index into the
12-byte table VA 0x873050 -> 0x493E0), +0x1986..+0x198F (0x49620, 0x495F0), +0x19AA (0x56CE0).
Records are 0x2C8 bytes but code touches +0x2C8..+0x2D1 (STRA): these overlap the next record's
+0x0..+0x9, which nothing else uses [E].

## 4. Layer commands: argument -> field map [V-emu unless marked]

Arguments are read with the object parser (`has(i)` VA 0xD8C0 / `expr(i)` 0xDA50, script.md
3.3a) except STPV, SHOK, SHAF, SPRC, STCG (inline readers). "absent" = missing or empty.
Counts are retail statements. The column "layer absent / range" gives what an absent layer
argument means and which values are accepted (others: the statement does nothing).

### 4.1 STCH / SCH2 (54,494 / 4,675; backend VA 0x81760, edi = 1 / 0)

```
l = has(0) ? expr(0) : -1;  if l not in 0..39: return
src = has(18) ? expr(18) : -1;   name = str(1)[:255]
h, v = 0, 0;  if src in 0..39: (h, v) = digits of names[pic[src]] if it ends in _hh_vv
               (len >= 7, '_' at len-6 and len-3, prefix IMG or len >= 15; digits not validated)
if has(9): h = expr(9);  if has(10): v = expr(10)
if h | v: name += "_%02d_%02d" % (h%100 if h > 0 else 0, v%100 if v > 0 else 0)
STCH only: reset the drawn record (0x49570, 0x4A770: tweens, motion, blend, fx, colour, shakes, STRA)
zoom_x = zoom_y = 1000; zoom_pivot = 0                                  (0x81AC9, both commands)
pic' = lookup(name)            (upper case to the first '.', 32 bytes; picture-name map 0x78410)
if src in 0..39:                                                        (0x81AF6..0x82612)
    copy blend fx_mode fx_a fx_b x y alpha state u284 u2ac u2d4 z rot scale anchor u580..u800
    if anchor(src) == catalog anchor of pic[src]: anchor = catalog anchor of pic'
    copy zoom_x zoom_y zoom_pivot mask; every layer whose mask == src now has mask = l
    copy parent;          every layer whose parent == src now has parent = l (children follow)
    copy soxy flags crop a16 a17 angle u19ab u19d3 and the ext block; ext offset = -anchor
    motion[l] = motion_clear(motion[l]) (0xBA800), then motion_copy(motion[l], motion[src])
        (0xBA8E0, 8.1): a self-source statement (src == l) therefore stops the layer's motion
    copy shakes stra
else: shake fields = 0
if name == "": clear the layer (pic -1, scale 1000, anchor 0, rot 0, mask/parent/sttr -1, soxy,
    flags, crop, a16, a17, angle, motion, blend, fx, colour white, shakes) and return
if has(11): mask = expr(11)
sttr = -1                                     (0x82041: always on this path, STCH and SCH2)
crop[k] = expr(12+k) if has(12+k);  a16, a17 if has(16/17)
if has(19): angle = wrap(expr(19))       (v % 36000 moved into [-18000, 18000])
if src < 0: motion = 0; blend = fx_mode = fx_a = fx_b = 0; colour = white;
            if pic[l] < 0: alpha = 0     (a layer without picture starts transparent)
pic = pic'
if has(2): x = trunc(x*640/480);  if has(3): y = trunc(y*480/360)
STCH: anchor = catalog anchor (if pic >= 0); rot = 0
if has(4): scale = expr(4)
STCH or has(5): alpha = clamp(expr(5) or 255 if absent, 0, 255); state = 0
STCH or has(6): z = expr(6), absent: 10*l
if src not in 0..39: ext offset = -(catalog anchor), (256, 255) for an unknown picture;
                     anchor = catalog anchor (pic >= 0)
if word 0x164B97C > 0 and not catalog draw_flags bit 0: fx_mode = that word
```

Not reset by STCH (STCC does reset them): flags, parent, soxy, mask, crop, a16, a17, angle,
STRA, STNS. The per-layer rule `sttr` **is** reset to -1 by every STCH / SCH2 that names a picture
(store at 0x82041, between the arg-11 and arg-12 reads) [V-emu: the reviewer's `probe_sttr.py`
gave -1 in 14/14 STTR -> STCH/SCH2 cases; after the fix `rr_scene_diff.py rand 150 20261025`, the
reviewer's generator that found them, gives 0 mismatches in 2,487 statements]. Retail
scripts never depend on it: in all 65 `STTR(l, n > 0)` statements the layer is cleared (STCC) or
gets a new STTR before its next STCH (reviewer's count).

Image-table test at 0x828D8: if the picture changed and catalog `kind & 0x80` (GOT slot
0x435238 = the catalog 0x671900), call 0xF1F0(layer, draw_param): never taken, every retail
record has kind 0x0B [E + V-all].

Retail use of the optional arguments: a9/a10 (variant) 8,246 / 7,809 STCH and 220 / 204 SCH2;
a11 (mask) 6,029 STCH + 322 SCH2; a12..a17 (crop rectangle x, y, w, h, a16, a17) 2,435 STCH
+ 3 SCH2; a18 (source) 201 STCH + 4,674 SCH2; a19 (angle) 2 STCH.

### 4.2 Simple setters

| cmd | count | args | layer absent / range | effect |
|---|---:|---|---|---|
| STCC | 63,261 | (l) | 0; l > 39 nothing; l < 0 all layers | reset pic -1, scale 1000, anchor 0, rot 0, z 0, x y 0, alpha 255, zoom 1000, pivot 0, mask -1, parent -1, soxy 0, flags 0, crop 0, a16 a17 angle 0, sttr -1, motion, blend, fx, shakes, stra, stns; also clears the drawn record's angle, crop, sttr, motion, colour. Not reset: state, ext block, u-arrays. Word 0x164B99A = 1 |
| STCP | 51,568 | (l, v) | 0; 0..39 | **z = v** (absent 0): the draw order. `STCP(l,0)` before fade-outs and `STCP(l,9250)`/`STCP(0,2)` around background swaps are z changes (script.md said "per-layer parameter") |
| STBR | 14,940 | (l, mode, _, f) | 0; 0..39 | blend = mode (absent 0); f == 1: flags \|= 0x80. Modes 0..28 used; their meaning: compositing.md |
| STEC | 12,900 | (l, mode, colour) | -1; 0..39 | mode 0..81 stored (others ignored); colour = `strtol(a2, NULL, 0)` for mode 1, white for the rest; also sets the drawn colour at once [V-emu sweep -2..89] |
| SNX2 / SNX3 | 1,678 / 845 | (l, n) | -1; 0..39 | fx_a = n (i16) / fx_b = n (i8): colour-effect parameters (keyframe fields 24/25) |
| STRR | 448 | (l, r) | 0; 0..39 | angle = wrap(r) (-100001 -> -32768; absent 0) |
| STRA | 569 | (l, n, ms) | 0; -1..39 | stra = n; drawn record +0x2C8 = n, +0x2CA phase = 0, +0x2CC = frames(ms) (n != 0) or +0x2CE/+0x2D0 = frames (n == 0: fade the effect out). l = -1 and n <= 0: clear all. The update advances the phase by frames mod 6000 (VA 0x588A0) [E]; what it drives is not traced [H] |
| STPV | 733 | (l, x, y, r) inline | own parser (0x86478) | anchor_x = `trunc(x*640/480*f)`, anchor_y = `trunc(y*480/360*f)` with f = size-class factor of the layer's picture (pic > 0); on a `_hh_vv` variant (hh_vv != 00_00) plus (variant anchor - base anchor); ext offset = -anchor; r present: rot = r. Also written to the drawn record at once |
| SOXY | 208 | (l, x, y, _, _) | 0; no bound check (layer 40 writes soxy_y[0] and crop[0], 2 retail statements) | soxy_x = x, soxy_y = y (bytes); a3/a4 ignored |
| STNS | 196 | (l, n) | 0; 0..39 | n >= 13 -> 0; stns = n; stns_b = 20 if n < 3 else 12 |
| STTR | 111 | (l, n, m) | 0; 0..39 | sttr = `(n-1) \| m<<12` (n > 0) else -1; drawn +0x12C too if the layer is in use; loads rule n-1 (0x6B460, as FADS type 2). The next STCH / SCH2 of the layer resets it to -1 (4.1) |
| SACL | 450 | (l, f) | 0 | f != 0: flags \|= 0x40 (skip the mask-layer offset in the draw, 0x516EA) else clear |
| SBAS | 943 | (l, p) | 0; 0..39 | parent = p (absent -1) |
| STCL | 9 | (0, l, x0, y0, x1, y1, ms, e) | a1: 0 | x, y = end (default start, start default current); live move tween |
| STMA | 24,333 | (l, id, w, n) | 0; 0..39 | 4.3 |
| SPMA | 199 | (v) | - | pause byte of every layer's motion record (scene and drawn) = 1 if v == 1 else 0 (0xC05D4; retail uses only 0 and 1) |

### 4.3 Timed commands (scene value = end value; the drawn record animates at once) [V-emu]

| cmd | args | scene | live tween (drawn record) |
|---|---|---|---|
| STGS (8,110) | (l, a0, a1, ms, e) | alpha = min(a1, 255) (absent 0) | start a0 (absent: current scene alpha, min 255) -> a1 over frames(ms), easing e: 0x4A620 / 0x4A340 |
| STZ4 (32,317) | (l, zx0, zy0, zx1, zy1, ms, e, piv) | zoom_x/y = zx1/zy1 if present; pivot = piv if present | start (zx0, zy0) (absent: current scene zoom) -> end over frames(ms), easing e (0x49930, 0x49A30) |
| STRT (21,683) | (l, r0, r1, ms, e, a5, a6) | rot = r1 (absent 0) | r0 (absent: current) -> r1 (0x4A4B0, easing 0x4A300) |
| STCL (9) | see 4.2 | x, y = end | move tween |

Tween slot (5 per record: move +0x84, scale +0x8A, rotation +0x90, alpha +0x96, zoom +0x9C) =
{i16 easing id, i16 counter Q12, i16 step}. Start: counter 0x1000, step `-(4096 // frames)`;
no tween for 0 frames. Each update: `counter = trunc(counter + step*frames_per_update)`; a
result <= 0, or a step >= 0, ends it (counter = step = 0) (VA 0x57B2D..). Remaining fraction
`r = 1 - ease(id, p - 1, 0, 1, -1)` with `p = counter/4096` (= 1 - ease(1-p)), 0 when ended
(VA 0x49390, 0x528DC). Drawn value = end + (start - end) * r, with (start - end) kept in
+0x110 (alpha, /255), +0xFC/+0x100 (zoom, /1000), +0x7A (rotation, applied as
`trunc(r * delta)` in +0x7C), +0x104/+0x108 (move), +0xE0 (scale). Checked frame by frame for
alpha (easings 0, 3, 35), zoom (0, 3), rotation (2) and move (6): max error 7e-5 [V-emu].

STMA(l, id, w, n) (VA 0xC0330; layer absent 0, range 0..39): the same record change is made to
the scene record (+0xD00) and to the drawn record (+0x160) (`motion_record`):

```
id (the 32-bit argument) > 0:  id = i16(id); pause = 0 (0xBA820); frames = 0 (0xBA830);
                               divisor = i16(n) if i16(n) > 0 else 0 (0xBA840, n absent = 0)
                               then 0xBE4C0(l, dt = 0): the motion at t = 0 is applied to the
                               drawn record and to scene_state (section 8)
otherwise (absent, 0, < 0):    id = 0; pause = 0 (0xBA820) - the motion stops; divisor and
                               frame counter are kept (92 retail `_STMA(l,0)`)
```

The committed state therefore holds motion values. n is a **time-quantisation step**, not a
speed divisor: the motion runs at normal speed in steps of n frames (8.2; 15 retail statements
use n = 2 or 3). w: word 0x164BA6E = 1 (the script waits for motions) when w == 1, or w
absent and config byte 0x66C578+0xA5 == 0, or w == 2 and that byte == 1 (both with float
0x1653E24 == 0) [E]. Ids above 24,192 (one retail `_STMA(27,101960)` stores -29112) and 0 do
nothing [E accessors, V-emu].

SGMA(l, id, key) (109 statements; VA 0xC23B0; `edit_motion`) writes the layer's **drawn** state
(record A, not scene_state) into keyframe `key` (absent 0) of motion `id` in the engine's
in-memory keyframe table, so a following STMA of that motion starts from where the layer is.
Layer absent 0, 0..39; id absent or <= 0: nothing. Keyframe index = first key of the motion
(`end(i16(id - 1))`, 0 for an invalid id) + key; written fields:

```
xi = trunc(f32(x * 480));  yi = trunc(f32(y * 360))
field 3 = xi / 640,  field 4 = yi / 480    (C division, then +-1 away from zero if not exact)
field 5 = trunc(f32(f32(alpha + alpha tween value) * 255));  field 8 = rot (i64)
fields 9 / 10 = zoom_x / zoom_y;  fields 11..14 = crop x, y, w, h, only when crop x != 0
word 0x164B998 = 1
```

Retail: 103 statements with key 0, 6 with key 1; in 97 of the 109 the next STMA (within 40
statements) runs the same motion on the same layer. The edit lasts for the session (the table is data, VA 0x873FF0); the
model therefore needs a mutable `MotionTable` per game session [V-emu: `sgma_check.py 150 4`,
150 random layer states + all 109 retail statements, keyframe bytes identical].

### 4.4 Quake / shake / swing (VA 0x84670 store, 0x847F0 stop) [V-emu storage, E/H motion]

Each layer has two slots: slot 0 quake (SQK2), slot 1 shake (SHK2) or swing (SSW2). A start
stores kind, fade = frames(fade ms), time = frames(duration) or -1, and 6 i16 parameters (scene
and drawn record; the drawn per-slot runtime +0x19A.. is zeroed). If the duration is >= 0 and
0x7B4B0() is set (skip / fast-forward [H]), nothing starts. Layer -1 (SQK2/SHK2): layers 0, 1 if in use, others
if in use or with a scene picture. Stops (SQS2/SHS2/SSS2, layer or -1) clear the kind's slot
(SHS2 and SSS2 clear the same slot 1).

| cmd | count | args | parameters (i16 x 6) | duration / fade |
|---|---:|---|---|---|
| SQK2 | 1,236 | (l, a1, a2, a3, dur, fade, a6, a7, _) | a1 (15), a2 (15), a3: absent 2, < 0: 1, 3*a3 < 50: 2, else trunc(3*a3/50)+1; frames(a7); dur >= 0 and a7 <= 16 ? frames(dur) : 0; a6 (0) | a4 ms (-1 = until stopped) / a5 ms |
| SHK2 | 125 | (l, a1, a2, a3, dur, fade, a6, a7, a8) | a1 (0), a2 (15), frames(a3) (18), a6 (0), frames(a7) (0), a8 (1) | a4 / a5 |
| SSW2 | 200 | (l, a1, a2, a3, a4, a5, dur, fade) | a1 (7), a2 (15), frames(a3) (60), frames(a4) (120), a5 (1), 0 | a6 / a7 |

The per-frame offsets come from `rand()` and `sinf` in the update (VA 0x58206..0x58827) and are
read by the draw through 0xFE50 (i16 x/y offsets, 1/1024 zoom offsets) and 0xF390 (rotation /
scale terms) [E]; shakes can move, zoom and rotate a layer (a swing rotates). The formula is not
modelled [open]; the shake frame of section 11 uses the engine's own quads. SHOK (870), MSQK (30)
and SHAF (397) act on globals (0xDF0818.., 0xDF0894.. screen quake floats, 0xDF07B4) [E].

### 4.5 Commands that do not touch the per-layer arrays

STCF (5,179; global bit set 0x1653E30 bit l and byte 0x1653E40+l = a1, yields [V-emu]), SGMA
(109; edits the motion table from the drawn record, 4.3), SPFC (487) / SPRD (36) /
SPRC (85) (special picture globals 0x174D650.. [V-emu writes, H meaning]), FADE (61, globals
0x164BA68.., 0xDF0794..), FADS (34,040, commit), STCG (39, seen bits; script.md).

## 5. Commit (`_FADS`, VA 0x7DD10) and the record arrays [V-emu]

Arrays of 40 records of 0x2C8 bytes: **A** 0xDDB5E8 (drawn), **B** 0xDE2650 (committed, written
by the commit), **C** 0xDE96B8 (previous; used by transitions). Per layer the commit writes B
from scene_state (table of section 3, setters 0x49F60..0x4B270, copies 0x7DE60..0x7E05C), marks
the picture seen, sorts z (0x4A180), starts the transition (0x42480(type, ms)), then for every
layer clears pictures of finished states and calls 0x49930 to put the scene zoom and pivot flag
into **A** directly. The next update moves B to A (VA 0x59700..: everything except zoom, pivot
and the runtime tween / frame fields) and sets A +0x10 (in use). Python: `commit(scene, l)`
returns the B fields; 36,000 field values from 30 retail windows matched the engine.

Drawn record fields used by this document (`RENDER_FIELDS`): +0x10 used, +0x18 pic, +0x1C
blend, +0x1E flags, +0x1F/+0x20/+0x22 fx, +0x24 state, +0x2C alpha, +0x30/+0x34 x/y, +0x48/+0x4C
extra offset, +0x50/+0x54 picture offset (-anchor, canvas units), +0x58 rot, +0x70 scale,
+0x76/+0x78 anchor, +0x7A/+0x7C rotation tween, +0x84..+0xA0 tween slots, +0xAA/+0xAC zoom, +0xB0
pivot flag, +0xCC alpha tween value, +0xE0/+0xE4 scale tween, +0xFC/+0x100 zoom tween delta,
+0x104/+0x108 move tween delta, +0x110 alpha delta, +0x120 mask, +0x128 parent, +0x12C sttr,
+0x130/+0x131 soxy, +0x132 crop, +0x13A a16, +0x13C a17, +0x15C angle, +0x160 motion, +0x178..
shakes, +0x1D0/+0x1D1 noise, +0x1F0 colour block, +0x2C8.. STRA.

## 6. Per-frame update (VA 0x57980)

Per update (`frames` = 1..3, VA 0xD47C0; one 60 Hz frame is 1), in this order per layer
[E order, V-emu values]: tween counters (4.3); alpha tween value +0xCC; colour-effect ticks
(0xE9B60, pictures with catalog draw_flags bit 2); zoom tween; **motion** (0xBE4C0 with
dt = frames, section 8); quake/shake/swing slots (4.4); STRA phase (`(+0x2CA + frames) mod
6000`, ends when it reaches +0x2CC; the fade counter +0x2D0 ends the effect). B is moved to A
when a transition starts or ends (0x59700..) [E]. The scene canvas command of the draw
(0x56E5A) is `0xDCBE0(640, 362.667, 0, trunc(v*3/1024*118))` with v = word 0x781144 (zeroed at
the start of every update, set by screen quakes) when the word 0x164B980 is non-zero or the
mode word 0x66C578+2 == 1; otherwise `0xDCBE0(853.333, 480, x, 0)` with x from the table
VA 0x1A80E0 indexed by 0x66C578+0x4A (a 3/4-size scene) [E; which configuration selects it is
open].

## 7. Projection: picture texels -> frame [V-emu]

### 7.1 World position, zoom and rotation of a layer (parent chain)

For layer L with drawn record r (tween values included), p = r.parent, used(p) = p in 0..39
and A[p] in use:

```
zoom(L)  = (r.zoom + r.zoom_delta * remaining(tw_zoom))                        (per mille, x and y)
           * zoom(p)/1000 * class(pic[p])                    if used(p)        (VA 0x528C0 / 0x529F0)
rot(L)   = r.rot + rot(p)                                    if used(p)        (VA 0x4C3E0, 1/100 deg)
world(L) = (r.x, r.y)                                        if not used(p)    (VA 0x525B0 / 0x52740)
         = world(p) + R(rot(p)) * ((r.x*f - p.anchor_x) * zx(p), (r.y*f - p.anchor_y) * zy(p))
           with f = class(pic[p]), zx(p), zy(p) = zoom(p)/1000, R = rotation (clockwise on screen)
class(pic) = 1, 0.5, 0.25, 0.125 for catalog scale_class 0 (A), 1 (L), 2 (M), 3 (S)
```

A child's x/y is a point in its parent's picture, measured from the parent's anchor in
"A-size" units: positions and zoom of children of an L sprite are halved, of an M sprite
quartered. The parent's **scale** (STCH a4) is not inherited; its zoom, rotation and size class
are. Rotation sums along the chain.

### 7.2 The quad of one layer

Corrected 2026-10-25 (re-check `layer_anchor`; the previous formula and why it was wrong are in
the errata at the end of this section). [V-emu: engine quads compared, numbers below]

```
X, Y  = world(L) + (r.dx, r.dy) + (r.move_dx, r.move_dy) * remaining(tw_move)      (canvas units)
s     = r.scale/1000 + r.scale_cur
z     = zoom(L)/1000                       (x and y; negative = mirrored)
rd    = rot(p) + (r.rot + r.rot_cur)   if p = r.parent is in 0..39, else r.rot + r.rot_cur
        (rot(p) of 7.1 = p's rotation plus that of its in-use ancestors; p itself need NOT be in
        use: 0x51574..0x515F6 call 0x4C3E0(p) without the +0x10 test; world and zoom do test it)
theta = trunc(rd * 32768 / 18000) * 2*pi/65536                                         (clockwise)
A     = (r.anchor_x * 3, r.anchor_y / 0.3358025)   the INTEGER anchor (+0x76 / +0x78, i16) in texels
res   = (r.off_x + r.anchor_x, r.off_y + r.anchor_y)   anchor residual, canvas units, float32
                                           (offset +0x50 / +0x54 is -anchor except while motion fields 6/7 run)
soxy  = (r.soxy_x / 3, r.soxy_y * 448/1080)
fx    = screen-effect offset (f32 0xDF0720, 0xDF0724), or (0, 0) if byte 0xDF07C8 != 0 or
        catalog draw_flags (+0x3D) bit 1 is set (VA 0x51808..0x51833); shake offsets (4.4) add here too
t     = soxy + res + fx                    plain translations: never scaled, zoomed or rotated
no pivot flag:  M = T(t) . T(X, Y) . R(theta) . S(s*z_x/3, s*z_y*0.3358025) . T(-A)
pivot flag:     P = (X + r.anchor_x - cat.anchor_x, Y + r.anchor_y - cat.anchor_y)   (VA 0x51FF5..0x52087)
                M = T(P) . S(z_x, z_y) . T(-P + t) . T(X, Y) . R(theta) . S(s/3, s*0.3358025) . T(-A)
frame = S(1920/640, 1080/362.667) . M . (u, v, 1)    for picture texels u in [0, w], v in [0, h]
```

Engine evidence (0x51060) [E; the resulting M is V-emu]: the anchor is read as i16 at 0x5183D / 0x5186C and converted to
float; the 2x2 part (rotation x scale x zoom) is applied to `-anchor` and the anchor is added
back (0x51883..0x519FB); the picture offset +0x50 / +0x54 is loaded at 0x519FF and added to the
translation **after** that product (0x51A1C), together with the position (+[rsp+0x90], 0x51A0A)
and the screen-effect offset ([rsp+0x220], 0x51A13). So the translation column is
`position + fx + Lin*(-anchor) + (anchor + offset)`: the pivot of scale, zoom and rotation is
the integer anchor, and `anchor + offset` is a translation. The shake offsets are added to the
same column afterwards (0x51A22..0x51B75), SOXY to the x / y passed to 0x4F320 (0x520D3..0x52102);
the pivot flag (0x52096) makes 0xDD6D0 zoom the finished vertices about P, so everything in `t`
is zoomed with the picture while P does not move.

* The picture's catalog anchor lands on the layer position at scale 1, zoom 1; `_STPV` moves the
  anchor, i.e. the point of the picture that sits on the position and about which scale, zoom
  and rotation act.
* `res` is 0 after every commit, STCH / SCH2 / STPV (they store ext offset = -anchor, integers;
  commit copies both, section 3), and then M is the previous formula. Motion fields 6 / 7 (8.3)
  store `anchor = i16(trunc(X))` and `offset = -X` with X a float ((`v*640/480` or `v*480/360`
  plus the variant difference) times the size class of the scene picture), so while such a
  motion is applied
  `res = trunc(X) - X`, in (-1, 0] for X >= 0 and [0, 1) for X < 0 (canvas units: up to 3 frame
  px in x, 2.98 in y). It stays until the motion record is cleared: a finished motion is still
  applied every frame with t = T, and the B -> A move re-applies it (8.2).
* Children: `world()` (0x525B0 / 0x52740, 7.1) uses the parent's **integer** anchor
  (+0x76 at 0x52656, +0x78 at 0x52670) and not its offset: a parent's residual does not move
  its children [V-emu: synth_check, fuzz below].
* Python: `layer_matrix(recs, l, catalog, extra=fx)` (`extra` = fx plus any shake offset, canvas
  units; the model computes neither), `anchor_residual(rec)`.
* Retail impact: 488 `_STMA` statements (453 motions) name motions with field 6 / 7 keys; in
  458 statements (428 motions) the flag word also enables the fields (0x10002 / 0x20004), so
  they animate the anchor; in the other 30 the field-6/7 keys are dead (count_cases.py).
* STZ4's last argument (pivot flag, 55 retail statements) zooms in screen space after the
  rotation, about P; P equals the position unless STPV changed the anchor (then it is offset by
  anchor - catalog anchor, an engine quirk).
* w, h are the texture (MZP) size; the draw path reads only the catalog anchor (+0x34/+0x38),
  draw_flags (+0x3D), draw_param (+0x3E) and scale_class (+0x42), never the catalog width,
  height or w3/h3 (functions 0x51060, 0x4F320, 0x525B0..0x529F0) [E].
* Not computed by the model: the values of the quake/shake/swing offsets (4.4; they go into `t`,
  0x51A22..0x51B75) and of fx (pass them as `extra`); the mask-layer offset (r.mask >= 0 and not
  flags 0x40: + 0x49A60(mask, 0/1) x a constant, VA 0x516DE; it is added to the position X, Y
  at 0x51746, so it also moves P) [E]; the canvas y offset (section 6).

**Verification of 7.2** (scratch/layer_anchor/, commands actually run; "engine" = the quad of
the emulated scene draw 0x56DE0, rebuilt from the captured 0xEB780 call with `sprite_matrix`;
errors are the largest corner distance over the w x h texture; old = the formula before this
re-check, kept in `common.py`):

| check | draws compared | new rule, worst | old formula, worst |
|---|---:|---:|---:|
| `synth_check.py`: offset +0x50/+0x54 poked to -anchor + residual (+-0.125 .. +-0.9 and (-5, 3)) before the draw; 15 set-ups (A / L / M / S pictures, scale 700..1500, zoom up to 3.0, mirrored x / both, rotation, pivot flag) x {plain, STPV, SOXY, both}, 5 parent set-ups (residual on the child, the parent or both) | 70 | 0.00025 units | 10.0 units |
| `fuzz.py 100 20261025`, `100 4242`, `100 777`: random scenes, 1..4 layers of all size classes, running anchor motions (436 enabled motions), scale, zoom / mirror, rotation, pivot flag, STPV, SOXY, parent chains, rotation tweens, a random frame 0..90 (432 draws with a residual, 179 of them with the pivot flag, 161 children of a parent with a residual) | 666 | 0.0041 units = 0.012 frame px (float32 rounding: a pivot-flag child at scale 2 under a rotated, zoomed parent; 7.3's builder alone shows 0.021); all other draws < 0.005 px | 62.6 frame px (254 draws > 1 px) |
| `retail_check.py` (modes `motions`, `rest`, `retry`, `retry2`, `recheck`; merged by `summarize.py` into `summary.json`): all 488 retail `_STMA` statements whose motion has field-6/7 keys (one per motion for all 453 motions, then the other 35). Window: the 30 statements before the STMA (`retry`: from the layer's last STCH / SCH2, up to 400 back; `retry2`: also drawn alpha 1.0 poked into an invisible motion layer before each draw, used by 44 statements), commit, the STMA, then frames 0, 1, 10, T/4, T/2, 3T/4, T, T+3 up to frame 200 (120 for 152 motions and the retries). Every drawn layer is compared, fx read from emulator memory; skipped: offscreen pass (mask / crop / sttr), shakes, non-plain draws. The motion layer was compared in 431 statements (402 motions; residual != 0 in draws of 307 motions); the other 57: not drawn 30, offscreen 18, shake 9. No retail motion-layer draw had the pivot flag (covered by the two rows above). `recheck`: the 4 statements with a draw > 0.01 px under the first version (children of pictureless rotated parents, errata) re-run after that fix | 10,733 (2,017 of the motion layer, 1,585 with a residual; 931 children; 226 with fx) | 0.0046 frame px (0.0015 units); 0 draws > 0.01 px | anchor effect alone (motion layer, residual != 0, no fx, no parent; 1,465 draws): 16.7 frame px, 552 draws > 1 px in 115 statements (110 motions), 267 statements > 0.1 px |
| `unused_parent_probe.py`: child of a pictureless parent running motion 10436 (frame 150); child of a cleared parent | 2 | 0.00004 units | 2.63 units |
| `test_case_probe.py` / test `_anchor_motion_crosscheck`: motion 10421 (mirrored, STPV; retail s351#10) and motion 8776 with rotation and the pivot flag, a child, frames 0 / 5 / 20 | 9 | 0.00014 units | 0.67 units |

`viz_corners.png` (Read and checked): 12 x 12 frame-pixel zooms of the (0, 0) corner of the two
test-case layers at frame 0: the old quad (blue) is 2.0 px right of the engine's (red) for the
mirrored layer and about 0.47 px off for the rotated pivot-flag layer; the new quad (green) lies
on the engine's in both.

#### Errata for 7.2 (re-check `layer_anchor`, 2026-10-25)

* **Old formula** (this section until the re-check): `O = (r.off_x * 3, r.off_y / 0.3358025)`,
  `M = T(soxy) . T(X, Y) . R(theta) . S(s*z_x/3, s*z_y*0.3358025) . T(O)` (pivot flag: the same
  with `S(s/3, ...)` inside `T(P) . S(z) . T(-P + soxy)`): the picture offset +0x50/+0x54 was
  the pivot. It equals the corrected M while offset = -anchor, i.e. everywhere except while
  motion fields 6 / 7 run; then it scaled, zoomed, mirrored and rotated the residual and drew the
  picture `|(Lin - I) * res|` canvas units off (Lin = R(theta) * S(s*z)): a mirrored picture
  `2 |res_x|` (0.667 units = 2 frame px for res_x = -1/3; s351#10 motion 10421 and s307#228
  motion 8776 for the whole motion), a zoom z `(z - 1) |res|` (retail worst 16.7 frame px at
  zoom -4.931 on both axes, residual (-2/3, -2/3): s458#1917, motion 15897; the review's sample
  had seen up to 4.9 px). 115 of the 431 compared retail statements were > 1 frame px off.
* **The review's rule** (FORMATS.md 7 P1 item 5 / review: transform about the anchor, then add
  `res` after M) is right without the pivot flag and 1.35 units off with it: the pivot zoom acts
  on the residual too (`res` is inside `T(-P + t)`, like SOXY).
* **The direct parent's rotation** (7.1 `rot(L) = r.rot + rot(p) if used(p)`): right for
  0x4C3E0 and for the parent chain inside world(), but the draw's own angle (0x51574..0x515F6)
  calls 0x4C3E0(p) for any p in 0..39 without the in-use test, so a child turns with a parent
  that has no picture (`rd` above). Found by the retail check (s353#179: child layer 18 of a
  pictureless layer 17 running motion 10436, 9.3 units off at frame 150; s514#345: 96.6 units);
  in both the parent's picture had been set before the 30-statement window, so whether a full
  playthrough ever draws a child of a rotated pictureless parent is not measured. A cleared
  parent (`_STCC`) has rotation 0 again (unused_parent_probe.py).
* **The review's s456#43** (motion 15355, 3.4 units off under both rules): the screen-effect
  offset fx of a running `_SHOK(0,3,100,11)` (fx = (0, 3.41) units at frame 0, decaying), not
  the anchor; with fx passed as `extra` all 8 sampled draws match within 0.001 frame px, without
  it they are up to 10.2 px off (retail check).
* 11.4's projection fuzz (1,102 draws, worst 0.0004) stays valid for what it covered: no draw
  in it had offset != -anchor. 8.3's fields 6 / 7 (anchor = trunc(X), offset = -X) were right;
  only the projection was wrong.

### 7.3 The quad builder VA 0xDD6D0 (`sprite_matrix`)

Entry 0xEB780(slot, flags, _, x, y) draws texture slot `slot` (scene layer l = slot 0xE8 + l;
its face layer, catalog name1, uses slot + 0x29 with the same transform). 0xDD6D0 builds a
vertex grid at VA 0x2009160 (slot entry 0x1DBD460 + 0x58*slot: +0x20 texture size, +0x24 tile
size, +0x28 tile counts) with, for texel (u, v):

```
(ax, ay) = anchor code flags & 0xF: fractions 0x2D5800/0x2D5830 of the texture size (trunc),
           9 = point 0x2009154
(sx, sy) = global scale 0xDC7BA4 if flags & 0x10 else 1
d        = ((u - ax) * sx, (v - ay) * sy)
p        = (x, y) + global screen offset 0x1DBD3EC + R(angle 0x2009150 * 2pi/65536) d   (R only if flags & 0x20)
flags & 0x40: p = pivot + zoom 0xDC7BAC * (p - pivot)      (pivot 0x2009148)
```

Checked against the real 0xDD6D0 with a synthetic slot: 1,600 corner vertices, max distance
0.021 canvas units (float32 rounding at coordinates up to 2,000) [V-emu].

## 8. Motions (`_STMA`; VA 0xBE4C0, 0xBDD70, 0xBD5E0, 0xBC460)

### 8.1 Tables and records [V-all; accessors E]

| table / record | VA | layout |
|---|---|---|
| duration | 0x274538 (text) | i64[24192] ms; `duration(id)` = entry **id - 1** (0xC2150; 0 for ids outside 1..24192) |
| end index | 0x2A3938 (text) | i64[24192]; `end(id)` = entry id - 1 (0xC2170; 0 outside 1..24192). Motion id uses keyframes `[end(id-1), end(id))` (end(0) = 0); the last value is 71,905 |
| flags | 0x245130 (text) | u64[24193] indexed by **id** (0xC1C40: 0 for ids outside 1..24192): field enable bits (8.3) |
| keyframes | 0x873FF0 (data) | 71,905 records of 72 bytes: +0x00 u64 time (ms); +0x08, +0x0A .. +0x14 fields 1..7 (i16); +0x16 2 bytes padding; +0x18 field 8 (i64); +0x20, +0x22 .. +0x44 fields 9..27 (i16); +0x46 2 bytes padding. `val(i, f)` (0xC2190) returns -32767 for i > 71904 or f > 27 |
| loop point | code, VA 0xC1C70 | switch on the id: 0 no loop, 1 loop the whole motion, n > 1 loop from n ms (2,027 looping motions; extracted table `samples/misc_layer_state_and_motions/motion_loops.json`, runs [first id, last id, value]) |
| motion record | scene +0xD00 + 24*l, drawn +0x160 | +0 i16 id, +2 i16 divisor (STMA a3), +4..+7 unused, +8 i64 frame counter, +0x10 u8 pause (SPMA), +0x11..+0x17 unused |

Record helpers: `motion_clear` (0xBA800: id = divisor = 0, frames = 0, pause = 0), `motion_copy(dst,
src)` (0xBA8E0: id, divisor and pause copied; frames copied only if src id > 0 and dst frames <=
src frames), `motion_record` (STMA, 4.3).

Sentinels: -32767 = not animated at this key, -32766 = hold (for the i64 field 8: -100001 /
-100000). Motion 9999 additionally eases the whole time line (8.2).

### 8.2 One application: VA 0xBE4C0 (`apply_motion`) [V-emu: every call checked, section 11]

Called by STMA with dt = 0 (0xC04FB), by the per-frame update with dt = frames (0x57EED) and
with dt = 0 when record B is moved to A (0x59957). `r` = the drawn record A[l]; `scene` writes
below happen only while the scene picture `pic[l]` (0x16500C4 + 2l) is not -1, tested at each
write, unless noted.

```
if l > 39: return
id = r.motion.id (i16); if id <= 0: return
s = end(id-1); e = end(id) (keys [s, e)); if e <= s: return;  T = duration(id); if T <= 0: return
if trunc(dt) != 0 and r.motion.pause == 0:                        (0xBA860)
    f = r.motion.frames + trunc(dt);  r.motion.frames = f if f >= 2 else 1
n = r.motion.divisor;  q = frames - frames % n if n >= 2 else frames   (0xBA8B0, C remainder)
t = f32(f32(q * 50) / 3)                                          (ms; 0xBE5A8)
if time(s) > t: return                                            (57 motions start late)
if t > T:
    L = loop(id)                                                  (0xC1C70)
    if L == 0: t = T
    else: span = T if (T >= 3 or T > L) else 2;  lo = L if T > L else 1
          if t >= span: i = trunc(t); fr = f32(t - i)
                        t = f32((i % span) + fr)                  if lo == 1
                        t = f32(((i - lo) % (span - lo)) + lo + fr)   otherwise
scene.motion[l].frames = q            (0xBE663; always, without the picture test)
if id == 9999: t = ease(e0, t, 0, Tl, Tl), e0 = easing (field 1) of key s (0 if a sentinel), Tl = time(e-1)
k = first key in [s, e) with time >= t; none: return
flags = flags(id); then the fields of 8.3 in this order
```

The divisor is a **time-quantisation step**, not a speed divisor: with n = 3 the motion keeps its
speed but advances only every 3rd frame (frames 1, 2 show t = 0, frame 10 shows t = 9 frames =
150 ms, frame 17 shows 15 frames). Example `_STMA(23,21128,,3)` (alpha 0 -> 255 over 800 ms):
engine and model alpha 0, 0, 15.94, 47.81, 79.69 at frames 1, 2, 3, 10, 17 (`probe_fix.py`; the
reviewer's `probe_div.py` showed the same engine values); with n = 2 frame 17 shows 85.00 = 16
frames. The scene copy of the record holds q.

### 8.3 Fields [V-emu: apply_check.py, every engine call compared]

`v(f, cur)` = `motion_value(f, s, e-1, k, t, cur, whole = id == 9999)` (8.4); `cur` is what the
interpolation returns when the field is not animated. **found(f)** = some key i in [s, e) with
time(i) != 0 has val(i, f) not -32767 / -100001 (HOLD counts), or the current key k has time 0
(scan 0xBE770..0xBE7D0). Only fields 3, 4, 5, 9, 10, 22, 23 have this scan; when it is false the
field's **whole mask** is cleared from `flags`. The other fields run whenever a bit of their mask
is set. All arithmetic float32 unless noted; `trunc` = C truncation; `i8/i16` = the record field
read as signed.

```
 3  if !found(3): flags &= ~0x40880
    if flags & 0x40880:   x = f32(f32(v(3, f32(f32(480*x)/640)) * 640) / 480);   scene x = trunc(x)
 4  if !found(4): flags &= ~0x5100000
    if flags & 0x5100000: y = f32(f32(v(4, f32(f32(360*y)/480)) * 480) / 360);   scene y = trunc(y)
 5  if !found(5): flags &= ~0x4000
    if flags & 0x4000:    a = alpha;  if dt == 0: a = a + alpha_tween_value (+0xCC); +0xCC = 0
                          c = clamp(v(5, a*255), 0, 255);  alpha = c/255;  scene alpha = trunc(c)
22/23 picture variant: 8.3a (gives the anchor difference (dx, dy))
 6  if flags & 0x10002:   cur = trunc(anchor_x*480/640) (integer division)
                          X = f32(f32(v(6, cur)*640)/480) + dx
                          if scene pic != -1: X = X * class(scene pic)   (0x41BA0: catalog +0x42 for ids <= 0x39B6, else 0 = factor 1)
                          anchor_x = i16(trunc(X)); off_x = -X; scene anchor_x = trunc(X); ext off_x = trunc(-X)
 7  if flags & 0x20004:   the same with cur = trunc(anchor_y*360/480), X = v*480/360 + dy, off_y, ext off_y
 8  if flags & 0x88000:   rot = trunc64(v(8, rot));  scene rot
    if flags & 1<<34:     angle (+0x15C) = i16(trunc(v(8, rot)))  (rot as just written);  scene angle
                          (bit 34 is set in 3 motions only; 0xBF6B8)
    zoom_dx = zoom_dy = 0; tw_zoom.counter = 0        (0xBF724..0xBF754: on every call)
 9 10  if !found(9): flags &= ~0x80200000;  if !found(10): flags &= ~0x100400000
    for (zoom_x, 0x80200000, zoom_dx), (zoom_y, 0x100400000, zoom_dy):
        if flags & mask: z = v(f, zoom); zi = trunc(z)
                         if float(i16(zi)) != z: zi += 1 if z >= 0 else -1
                                                 delta = f32(z - zi) * 0.001; tw_zoom.counter = 4096
                         zoom = i16(zi); scene zoom = i16(zi)
11..14 crop: x (11) then w (13) if flags & 0x10000080; y (12) then h (14) if flags & 0x4000400
        cur = crop[j], or the scene value when scene pic != -1 and the scene value differs
        crop[j] = i16(trunc(v(f, cur)));  scene crop[j]
15  if flags & 0x10:      a17 = trunc(max(0, v(15, i8 a17)));  scene a17
16  if flags & 0x20:      a16 = trunc(max(0, v(16, i16 a16)));  scene a16
17  if flags & 0x2000000: b = trunc(v(17, blend)); if b != blend: blend = b; scene blend
18  if flags & 0x200 and trunc(v(18, i8 fx_mode)) == 1:           ("colour key"; fx_mode itself is never written)
        rgb = trunc(v(19, 0))<<16 + trunc(v(20, 0))<<8 + trunc(v(21, 0))
        drawn colour block (+0x1F0) = colour_block(rgb) (0xF7A0)
        if the scene fx_mode byte (+0xCC) != 0xFF (no picture test): scene ext +6..+0xB = R, G, B, 0, 0, 0 (0xFA90)
19..21 if flags & 1<<33 (8 motions): the same rgb; drawn colour block; scene ext (picture test)
24  if flags & 0x40:      fx_a = i16(trunc(v(24, fx_a)));  scene fx_a
25  if flags & 0x100:     b = trunc(v(25, i8 fx_b)); if b != fx_b: fx_b = b; scene fx_b
26  if flags & 0x1000:    n = trunc(v(26, i8 stns)); if n != stns: stns = n; stns_b = 20 if n < 3 else 12; scene both
27  if flags & 0x1:       z = v(27, 0); if z != 0 and scene z[l] != 0: draw-order slot 0xDE2538 + 4l = trunc(z); scene z
```

`colour_block(rgb)` (0xF7A0) on the 17-byte block: +0 i16 = 0, +2 i16 = 0, the old +5..+7 and
+8..+0xA move to +0xB..+0xD and +0xE..+0x10, +5..+7 = R, G, B, +8..+0xA = 0.

Shared bits: bit 7 is in the masks of field 3 and of crop x/w, bit 26 in field 4 and crop y/h.
19,595 / 19,579 motions set them, so the crop code runs in most motions; with the crop fields not
animated `v` returns `cur`, i.e. the drawn crop takes the scene's crop value when they differ.

Flag-word bits set by retail motions (count of motions): 0: 22,615; 1: 429; 2: 427; 3: 75; 4:
1,060; 5: 652; 6: 830; 7: 19,595; 8: 501; 9: 7,144; 10: 1,132; 11: 14; 12: 6; 14: 14,733; 15:
8,253; 16: 6; 17: 6; 18: 1,118; 19: 254; 20: 1,118; 21: 703; 22: 681; 23: 24,192; 24: 14; 25:
8,784; 26: 19,579; 27: 23,020; 28: 1,133; 29: 5,154; 30: 4,740; 31: 17,642; 32: 17,385; 33: 8;
34: 3. Bits 3, 23 and 27 are tested nowhere in 0xBE4C0 (the only reader of the table) [E]; bit
13 is never set.

Retail use (keys defining the field): 3: 40,747, 4: 41,414, 5: 35,637, 6: 474, 7: 471, 8:
13,283, 9: 37,238, 10: 36,151, 11..14: about 1,450 each, 15: 1,066, 16: 665, 17: 8,915, 18:
7,231, 19..21: 2,035, 22: 5,259, 23: 4,837, 24: 876, 25: 520, 26: 6, 27: 23,091. Motions are
**absolute**: field values are positions / zooms / angles, not offsets.

### 8.3a Picture variants (fields 22 / 23; 0xBEB37..0xBF389) [V-emu]

```
src   = scene pic[l] if t == 0 else r.pic
name  = 32-byte entry of the picture-name table (data VA 0x7F6800) of src ("" if src < 0)
(hh, vv) = variant digits of name: len >= 7, '_' at len-6 and len-3, prefix "IMG" or len >= 15,
           digits read without validation (as STCH, 4.1); else (0, 0)
if !found(22): flags &= ~0x20000000;  if !found(23): flags &= ~0x40000000
if flags & 0x60000000 and r.pic != -1:
    nh = max(trunc(v(22, hh)), 0) if flags & 0x20000000 else hh;   nv likewise with 23
    if (nh, nv) != (hh, vv):
        name2 = name minus its last 6 characters                     if nh|nv == 0
        otherwise name (+ "_00_00" if hh|vv == 0), then name2[len-4] = '0' + nh%10 and
              name2[len-1] = '0' + nv%10; the tens digits [len-5] / [len-2] are replaced only
              for values >= 10 (a quirk: going from 12 to 5 gives 15)
        id = lookup(name2) (0x82CE0: picture-name map, upper case; -1 unknown)
        if id >= 0:
            if t == 0 or r.pic == scene pic[l]:                       (scene side, no picture test)
                scene anchor = cat(id).anchor                                   if scene pic == -1
                             = scene anchor + cat(id).anchor - cat(scene pic).anchor   otherwise
                scene pic = id; ext offset = -scene anchor
            if t > 0:                                                 (drawn side)
                r.pic = id; r.anchor = cat(id).anchor; r.off = -anchor; texture slot 0xE8+l rebound,
                face slot 0x111+l rebound if the picture has a face layer (r +0x14 = 1, else 0)
            name, hh, vv = name2, nh, nv
(dx, dy) = (0, 0); if hh|vv: (dx, dy) = cat(r.pic).anchor - cat(lookup(name minus 6 chars)).anchor (low 16 bits)
```

So at `_STMA` (t = 0) only the **scene** picture switches; the drawn record keeps the old
picture until the next commit moves B to A, unless the motion defines field 22/23 at a key with a
non-zero time (then found() holds later and the drawn picture switches at t > 0). Retail: 5,810
motions have a variant bit (5,785 with a non-zero digit), only 93 of them have a variant key at
t > 0; 5,808 of the 24,240 retail STMA statements with a valid id use a variant motion, 93 a
late-switching one. Example: `_STCH(7,IMG0143,...)`, commit, `_STMA(7,2)`: scene pic 150 ->
4339 (IMG0143_11_11), scene anchor (640, 362) -> (648, 370), ext offset (-648, -370); drawn pic
stays 150 (engine = model, `probe_fix.py`; motions 10 / 23 give 4332 / 4329). `cat(-1)` reads
the bytes before the catalog, whose low halves are (256, 255).

### 8.4 Interpolation VA 0xBDD70 (`motion_value(T, f, s, e, k, t, cur)`) [V-emu exact]

```
HOLD, UNSET = (-100000, -100001) for f in {0, 8} else (-32766, -32767)
if time(s) > t: return cur
r = k; while val(r,f) == HOLD and r < e: r++            ; vn = val(r,f), tn = time(r)
if k != s and t < tn:
    j = k-1, k-2 ... while val(j,f) == HOLD and j > s   ; vp = val(j,f), tp = time(j)
    if vn != UNSET and vp == UNSET and j > s: vp = first value not HOLD/UNSET before j (else the last read)
else: tp = 0; vp = first value not HOLD/UNSET scanning back from r (0 if r < s)
if vn == UNSET: v = first defined value scanning back from k; return cur if none, else quant(v)
if vp == UNSET: return cur
if vn == HOLD: return the first non-HOLD value scanning back from e-1 (cur if none)
if vp == HOLD: return cur
p = ease(eid, t - tp, 0, 1, tn - tp) if tn > tp else 1,
    eid = 0 for motion 9999, val(s,1) if k == s, else val(j,1) of the first earlier key whose f is
          not HOLD and whose easing is not a sentinel (0 if none)
if f in 15, 17..27: return vp
vn, vp = quant(vn), quant(vp)       quant: fields 3/4 snapped to whole engine units (trunc(v*4/3) as i16 * 3/4)
it = val(s,2) if k == s else the last non-sentinel val(j,2) before k (0 if k < s)
it == 2: return vp;  it == 1 and spline(a, b) exists: return ((((b-a)*p + 3a)*p + (vn-vp) - (2a+b))*p + vp
else: return (vn - vp)*p + vp                         (all arithmetic in float32)
```

Spline (VA 0xBD5E0), all float32, `back(i)` = the value of the last key j <= i (j >= s) that is
neither HOLD nor UNSET (cur if none):

```
if k == s or e - s + 1 < 3: no spline
Y = [val(s) (cur if HOLD/UNSET)];  knot(0) = 0
for i = s+1 .. e-1:   if raw(i) == HOLD or raw(i-1) == raw(i) == raw(i+1): knot(i) = len(Y)-1
                      else: knot(i) = len(Y); Y.append(back(i))
if raw(e) != raw(e-1) and raw(e) != UNSET: knot(e) = len(Y); Y.append(back(e))
n = len(Y); if n < 3: no spline
D[j] = Y[j] - Y[j-1]                      (j = 1 .. n-1)
M[0..n] = 0;  M[1] = D[2] - D[1];  g = 4;  diag[1] = 4
for j = 2 .. n-2:  w = 1/g
                   M[j] = (D[j+1] - D[j]) - g*w        <- the engine's quirk (0xBDC70..0xBDCA9):
                   g = 4 - w;  diag[j] = g                textbook Thomas would subtract M[j-1]*w
M[n-2] = M[n-2] - M[n-1]
for j = n-1 down to 2:  M[j-1] = (M[j-1] - M[j]) / diag[j-1]
(a, b) = (M[knot(k-1)], M[knot(k-1) + 1])     (the segment that ends at key k)
```

All 57 spline motions are part of the checks in section 11.

### 8.5 Easing VA 0xBC460 (the tween copy 0x48220 is identical) [V-emu]

`ease(id, t, b, c, d)`: ids 2..41 = Penner families quad, cubic, quart, quint, sine, circ,
expo, elastic, back, bounce (`(id-2)//4`) in the variants in, out, in-out, out-in
(`(id-2)%4`); expo carries the engine's correction terms (in: `- c*0.001`, out: `* 1.001`,
in-out: `- c*0.0005` / `* 1.0005`); elastic period 0.3d (in-out 0.45d), amplitude c; back
s = 1.70158 (in-out 1.70158*1.525). Every other id (0, 1, 42..69, 71+, negative) is linear; id
70 is a separate jump-table case not used by any motion or command (open). Easing ids used:
motions 0..41 (keys: 0: 24,098, 3: 12,126, 2: 2,878, 7: 755, 6: 662, 27: 386, 4: 270, 35: 563,
34: 243, the rest < 230 each); STGS 0, 2, 3, 6, 10, 35; STZ4 0, 3.

Precision: linear, quad, cubic, quart (ids 2..11), quint in / out (14, 15), sine in / out /
in-out (18..20), expo in / out (26, 27) and back in / out (34, 35) are ported **bit-exactly**
with the engine's float32 / float64 operation order (`_ease_exact`, `EASE_EXACT`): 42,668 of the
42,769 keyframes with an easing id (99.8 %; 101 keys use the other ids) and every easing of the
retail tweens (STGS 0, 2, 3, 6, 10, 35; STZ4 0, 3). Examples (F = round to float32, other
operations float64):

```
linear (0xBCE1A)      F(F(F(t*c)/d) + b)
quad out (3)          x = F(t/d);  F(b - F(F(x*c) * F(x - 2)))
quad in-out (4)       x = F(t / (d*0.5));  x < 1: F(c*0.5 * (x*x) + b)
                      else: x = F(x-1);  F(b + c*-0.5 * F(F(x*F(x-2)) - 1))
cubic out (7)         x = F(F(t/d) - 1);  F(c*(x*(x*x) + 1) + b)
quart in (10)         x = F(t/d);  F(pow(x, 3) * F(x*c) + b)
expo out (27)         t == d: F(b + c); else F((1 - exp2(F(F(t*-10)/d))) * (c*1.001) + b)
back out (35)         x = F(F(t/d) - 1);  F(F(c * F(F(F(x*x) * F(F(x*2.70158f) + 1.70158f)) + 1)) + b)
sine in (18)          x = F(t/d);  F((c - c*cos(x*pi/2)) + b)
sine in-out (20)      F(b + (cos(t*pi/d) - 1) * (c*-0.5))
```

`ease_exact_check.py 30000 7`: 24,982 / 24,982 samples of these ids bit-identical with 0xBC460
(random t inside and outside [0, d], random b, c, d, and the motion / tween call shapes). The
other ids use float64 formulas: max relative error 2.2e-6. pow / exp2 are libm calls; under
emulation they are Python's, so a real console could differ in the last float64 bit (invisible
after the float32 rounding in practice) [H].

## 9. Masks, crop and the offscreen pass [E; shape H]

* `mask` (STCH a11, 6,351 statements): layer M draws through layer `mask`. In the draw loop
  (0x59EA0) layers whose record has mask >= 0 are skipped and drawn by the mask layer's pass:
  0x5A5E0 renders a layer into an offscreen target (canvas 0xDCBE0 with 0x1A7330.., blend 8,
  alpha forced to 1, record +0x124 = 1) when another visible layer names it as mask, or when its
  crop w and h are > 0, or when sttr >= 0, then composites. Retail pattern:
  `_STCH(17,img1688,...,160,-285,888,1650,90,0)` (shape layer with a crop rectangle) followed by
  `_STCH(18,img2259,...,17)` and `_STCH(19,img1688,...,9,9,17)`.
* `crop` x, y, w, h (STCH a12..a15, motion f11..f14; 2,438 statements) are in picture pixels and
  select the visible rectangle of the layer's own picture (cut-in frames such as
  `_STCH(12,img0008,710,3,1000,255,1450,,CC,,,,61,-154,964,1389,30,1)` + STRT -1650 + STMA 78).
  `a16` (30, 15, 90, 120 ...) and `a17` (0, 1, 2, 6, 7) shape the edge [H: feather width and
  style]; the rasterisation is shader work (compositing.md).
* `sttr` (STTR) selects a rule picture for the layer's own transition (0x6B460).
* These passes do not change the geometry of section 7: the crop frame of section 11 shows the
  engine drawing the cropped layer with the quad of 7.2.

## 10. The five framing outliers of the image re-check (resolved) [E + V-all]

ARU_L_90_00_N02 / N07 (y -54), KRO_L_31_03_N00 (x -94, y -62), KUR_M_00_00_N00 (x -26) and
Z20_L_11_00_N00 (x -36, y -12): their allpac2 thumbnails frame the art differently from
`CatalogResolver.render` at (0, 0). This is not an engine placement rule. The engine places a
layer picture only by the catalog **anchor**: the drawn quad starts at position - anchor and has
the size of the loaded texture, and the draw path never reads the catalog width / height /
w3 / h3 (7.2). For these records the shipped MZP canvas and the catalog rectangle disagree
(KRO: catalog 2118x2338, MZP 2032x2286, art x 93..2022; KUR_M: catalog 1564x1846, MZP
1638x2032; Z20: catalog 1566x4620, MZP 1778x5100, art x 175..1385, y 325..4595; ARU_L_90:
base MZP 1778x2794, catalog 1770x2621), so the offline thumbnail tool framed them on a canvas
the game does not use. In game they are drawn like every other picture: the MZP canvas at
(0, 0) relative to (position - anchor). Thumbnails of these records are not a placement
reference (sizes measured from the MZPs; offsets from the image re-check's `thumb_shift.json`).

## 11. Verification (commands actually run)

All scripts are in `scratch/misc_layer_state_and_motions/xf/` (logs of the final runs in
`xf/final/`); they drive the engine's own code with `tsuki.misc_layer_state_and_motions_emu.Rig`
(unicorn 2.1, VEX instructions executed natively, libc stubs; the first STCH builds the engine's
picture-name map, about 16 s). The final runs below were made with the final code.

### 11.1 Review fixes (2026-10-25)

The review of the previous version (`scratch/review_layer_state_and_motions/`) found five
model errors; all are fixed and re-checked with the reviewer's own scripts or generators:

| review finding | fix | proof after the fix |
|---|---|---|
| STMA a3 is not a speed divisor: 0xBA8B0 returns `frames - frames % n` | `motion_frames` (8.2); scene record keeps q | `probe_fix.py` (engine = model at frames 0..40 for n absent / 3 / 2, e.g. 47.81 at frame 10 with n = 3); `apply_check.py synth 60 3`: 3,228 engine calls with n >= 2, all identical |
| STCH / SCH2 always set sttr = -1 (0x82041) | `SceneModel` 4.1 | `rr_scene_diff.py rand 150 20261025` (the reviewer's generator that found 14 mismatches): 2,487 statements, 0 |
| fields 22/23 (picture variant) not implemented | 8.3a, `PictureNames`, `apply_motion(names=)` | `probe_fix.py` (IMG0143 + motions 2 / 10 / 23: scene pic 4339 / 4332 / 4329, anchors, ext offsets = engine); `apply_check.py variant` and `switch` (below) |
| angle (+0x15C) written only with flag bit 34 | 8.3 field 8 | `apply_check.py` (scene angle compared in every call) |
| self-source STCH/SCH2 clears the motion | `motion_clear` + `motion_copy` | `rr_src_diff.py 200 606` (reviewer's, 20 mismatches before): 5,766 statements, 0 |
| 0.9999 zoom residual was "the harness" | it was the model: 0xBE4C0 itself ends the zoom tween on every call (0xBF724..0xBF754) | `motion_check2.py 30 17`: worst drawn-field error 3e-5 (was 0.9999) |
| mypy errors in the harness and tests | fixed | mypy --ignore-missing-imports clean on all four files |

Re-reading 0xBE4C0 in full for these fixes showed further differences, also fixed and covered
by the checks below: found-scans exist only for fields 3, 4, 5, 9, 10, 22, 23 (the old model
gated every field); scene writes need a scene picture (the old model wrote alpha / rot / angle
/ anchors without); the alpha-tween value is folded in only for dt = 0; field 6/7's `cur` is an
integer conversion without the size class, the class comes from the scene picture, and variant
pictures add an anchor difference; field 18 is a colour key (the mode is not written; scene
colour condition = scene fx_mode != 0xFF); the colour block layout (0xF7A0); STMA with id <= 0
keeps divisor and counter, a negative n gives divisor 0; SPMA stores 1 only for exactly 1; the
loop span rule for T > L; `_SGMA` edits the keyframe table (`edit_motion`, not modelled before);
easing ids 0..11, 14, 15, 18..20, 26, 27, 34, 35 are bit-exact float32 ports.

### 11.2 (a) Field map, every layer command used by the retail scripts

`scene_diff.py`: each statement is executed by the engine's ScriptStep (VA 0x95E60) and by
`SceneModel.step`; the 0x1A08-byte scene_state is compared byte for byte after every statement.
STMA statements are compared in full (the model applies `motion_record` and
`apply_motion(dt = 0)` to the engine's drawn record before the statement), SGMA statements apply
`edit_motion` to the model's (per-run fresh) keyframe table.

| run | statements compared | mismatches |
|---|---:|---:|
| `scene_diff.py synth`: every argument variation of `fieldmap.py` + edge cases (dirty layer, source layers, re-pointing, empty name, absent arguments) | 1,501 | 0 |
| `scene_diff.py retail 120 7`: 120 random windows of 150 consecutive retail layer statements | 16,610 | 0 |
| `scene_diff.py cover 60 3`: every retail statement of each rare command and 60 random ones of each common command, each after the 15 statements before it | 24,674 | 0 |
| `rr_scene_diff.py rand 150 20261025` (the reviewer's random generator: every modelled command, odd values, variant pictures, empty names, source layers) | 2,487 | 0 |
| `rr_src_diff.py 200 606` (the reviewer's source-layer generator, with STMA) | 5,766 | 0 |
| `sgma_check.py 150 4`: the keyframe record after `_SGMA`, byte for byte (150 random layer states, all 109 retail statements) | 259 | 0 |
| `commit_check.py 30` (earlier run, commit code unchanged): commit() vs record B (and A for zoom / pivot) after `_FADS(1,0,0)`, 30 retail windows x 40 layers | 36,000 field values | 0 |

Per command (statements compared in the retail windows / cover runs; all 0 mismatches): STCH
2,637 / 4,365; STCC 3,367 / 3,596; STCP 2,923 / 1,898; STZ4 1,575 / 2,652; STRT 1,033 / 1,442;
STMA 771 / 2,311; STBR 561 / 1,420; STEC 531 / 1,181; STGS 412 / 458; SCH2 322 / 319; SNX2 75 /
290; SBAS 35 / 285; SQK2 43 / 207; SQS2 17 / 212; SACL 33 / 214; SGMA 6 / 209; SNX3 37 / 177;
STPV 52 / 170; STRA 23 / 167; SOXY 20 / 146; SHS2 13 / 141; SSW2 4 / 122; STRR 25 / 119; SHK2 9
/ 105; STTR 2 / 96; SPMA 8 / 83; SSS2 14 / 82; STNS 11 / 81; STCL 0 / 15 (all 9 retail, some
twice); FADS 2,051 / 2,111. STCF, SPFC, SPRD, SPRC, SHOK, SHAF, MSQK, FADE, STCG write no
per-layer array (their globals are listed in 4.5, observed under emulation, not modelled).
Table: `samples/misc_layer_state_and_motions/command_check_counts.json`.

### 11.3 (b) Motions

* **Every engine call of 0xBE4C0** (`apply_check.py`): code hooks capture the layer's drawn
  record, scene_state, draw-order slot and in-memory keyframes before and after each call made
  by the engine (STMA, per-frame update, B -> A move); `apply_motion` runs on the "before" state
  and is compared with "after": scene_state byte for byte, the record field by field (every
  `RENDER_FIELDS` entry; any other changed byte would be reported as unmodelled), the slot.

  | run | calls with a motion | of them dt = 0 / divisor >= 2 / picture switched | mismatches | worst float difference |
  |---|---:|---|---:|---:|
  | `retail 40 11`: 40 retail windows of 150 statements, 3 frames after each FADS, 1..40 after each STMA | 26,952 | 985 / 0 / 0 | 0 | 0 (bit-identical) |
  | `variant 120 5`: 120 of the 5,798 retail (picture, variant motion) pairs, to the end of the motion + a commit | 25,242 | 240 / 0 / 2 | 0 | 7.3e-6 relative (y; motion 5356 uses easing 36, a float64 formula) |
  | `switch 150 9`: all late-switching variant motions + random variant motions on a base / other variant of a matching family (pictures that really switch) | 24,469 | 256 / 0 / 125 (128 runs) | 0 | 6e-8 relative (alpha; easing 39) |
  | `synth 60 3`: random motions, divisors 2 / 3 / 5 / -2 / 1 / absent, all size classes, layers without picture, running STGS / STZ4 tweens, SPMA pause, self-source SCH2 | 5,899 | 76 / 3,228 / 4 | 0 | 0 (bit-identical) |

  In the retail pairs the STCH usually already names the variant the motion selects, so their
  switches are rare; the `switch` run exercises them.
* `motion_value` vs the engine's 0xBDD70 called directly (`interp_check2.py 1 300`): 357 motions
  (all 57 with spline keys + 300 random), every animated field, 8 times (0, 13, 37, 50, 61, 89,
  100, 120 % of the duration) x 2 `cur` values: **39,472 samples, all bit-identical**.
* `ease` vs 0xBC460 (`ease_exact_check.py 30000 7`): 24,982 / 24,982 samples of the exact ids
  bit-identical; other ids max relative error 2.2e-6 (8.5).
* End to end (`motion_check2.py 30 17`): `_STMA(7,id,,n)` (n = 0 / 2 / 3) on a committed
  picture (L, S, A sprites, IMG CGs including IMG0143), then the per-frame updates of the engine
  and `apply_motion` driven by the model alone; at 5 points (0, 1/4, 1/2, 3/4 of the duration and
  past the end) of 48 motions (one per main field, a spline motion, loops, a loop point, a
  delayed start, motion 1, 4 variant motions and 30 random): **240 points, every compared drawn field identical (worst error 0) and scene_state byte-identical at all 240 points** (x, y, alpha, anchor, rotation, effective zoom, crop, a16, a17, blend, fx_a, fx_b, noise, picture). The earlier `motion_check.py` runs (44 motions, worst 0.9999) showed the missing zoom-tween reset (11.1).
* Field targets and enable masks (earlier runs, `motion_fields.py`, `motion_masks.py`):
  synthetic two-key motions written into the emulator's motion table with one field each,
  flag word all ones and then single bits.
* Tweens (`tween_check.py`): every frame of 7 tweens (alpha easings 0, 3, 35; zoom 0, 3;
  rotation; move), worst 7.2e-5.

### 11.4 (c) Frames

`frames.py`; `samples/misc_layer_state_and_motions/frame_*.png`, `frames.json`: retail layer
statements of a script up to a statement are run through the engine, then `_FADS(1,0,0)` and N
frames; every drawn layer is placed with `layer_matrix` (model) and the frame is composed from
the real pictures (`CatalogResolver`, normal blending, blend modes not applied). Red outline =
the quad the engine drew (captured at 0xEB780 -> `sprite_matrix`), green = model quad, yellow =
crop rectangle. Viewed:

| frame | moment | what it shows | engine vs model (canvas units) |
|---|---|---|---|
| `frame_sbas_parent.png` | script 3 (stmt 1578) | night sky IMG2599 with the moon IMG2600 attached by `_SBAS(16,15)` | 0.00004 |
| `frame_motion_halfway.png` | same, 75 frames (1,250 ms) after the commit | motions 22564..22567 half-way: clouds and moon have moved (the moon about 30 px lower than in the first frame) | 0.00004 |
| `frame_crop.png` | script 4 (stmt 349) | train interior; cut-in layer 12 (img0008, zoom 1.395, rotation -16.5 deg) with its crop rectangle (62,-154,964,1389) outlined in yellow; the cut itself is **not** rendered (the crop / mask pass is open, 13.2), so the frame shows the geometry only | 0.00012 |
| `frame_rotated.png` | script 79 (stmt 2353) | Ciel and the bedroom tilted (STRT 2558 / 2946): rotated sprites and background | 2.476, the same for every layer: the screen shock of an earlier `_SHOK` still running (the replay compresses time); not part of the model |
| `frame_mirrored_zoom.png` | script 65 (stmt 1242) | Hisui mirrored by `_STZ4(9,-1000,,-1000,,0,0,0)` over the staircase | 0.331, uniform: an earlier `_SHOK` (as above) |
| `frame_shake.png` | script 29 (stmt 514), 5 frames into `_SQK2(-1,3,3,0,1000,...)` | burst effect over Ciel, composed with the **engine's** quads (the shake formula is open, 13.1); red (engine, quaking) and green (model, no quake) outlines are offset by the quake | 1..3 units (quake), 65 for a swinging layer |

Projection fuzz (earlier runs; the projection code is unchanged) (`xf_fuzz.py 150 2` and `300 5`): 450 random scenes (1 to 4 layers, all four
size classes, parents, scale, mirrored and plain zoom, pivot flag, rotation, STPV, SOXY):
**1,102 layer draws, worst corner error 0.0004 canvas units** (model from the drawn records vs the
engine's quad). Quad builder (`quad_check.py`): 1,600 vertices, worst 0.021. Tweens
(`tween_check.py`): every frame of 7 tweens, worst 7e-5.

### 11.5 (d) Outliers, (e) tests

**(d) Outliers**: section 10 (catalog fields read by the draw path listed by scanning the
functions 0x51060, 0x4F320, 0x525B0, 0x52740, 0x528C0, 0x529F0, 0x57980 for catalog-relative
operands; MZP sizes and alpha bounds measured).

**(e) Tests**: `tools/tests/test_misc_layer_state_and_motions.py`: 18 tests, about 25 s with the
game (one emulator session, which also checks STTR -> STCH, a variant motion, a divisor motion,
SGMA and a self-source SCH2 against the engine frame by frame); without the game 10 pass and 8
skip in 0.2 s. mypy --ignore-missing-imports is clean on the model, the two harness modules and
the test file.

## 12. Python API (`tsuki.misc_layer_state_and_motions`)

| name | purpose |
|---|---|
| `SCENE_ARRAYS`, `SceneState(bytes)` | scene_state layout; `get/set(name, layer)`, `ext(layer)`, `diff()` |
| `SceneModel(catalog, names, state)` `.step(stmt)` | command model (section 4); returns the live operations (`motion` for STMA: apply `motion_record` + `apply_motion(dt=0)` to the drawn record; `motion_edit` for SGMA: `edit_motion`) |
| `commit(scene, layer)` | fields the commit writes (section 5) |
| `RENDER_FIELDS`, `RenderRecord.from_bytes`, `steady_record(fields)` | drawn records |
| `layer_world/zoom/rot`, `layer_matrix`, `frame_matrix`, `sprite_matrix`, `canvas_to_frame`, `Affine` | projection (section 7) |
| `tween_start/state/value`, `zoom_tween` | tweens (4.3) |
| `MotionTable(eboot)` (`val`, `set_val`, `flags`, `get`), `motion_value`, `apply_motion(T, rec, l, dt, catalog, scene, loops, names)`, `motion_frames`, `motion_time_ms`, `motion_keytime`, `motion_found`, `FIELD_MASKS`, `FOUND_FIELDS`, `ANGLE_BIT`, `KEY_FIELDS`, `load_motion_loops`, `ease`, `EASE_EXACT` | motions (section 8) |
| `motion_record(rec, id, n)`, `motion_clear`, `motion_copy` | the 24-byte motion record (STMA, STCH/SCH2 source copy; 4.3, 8.1) |
| `edit_motion(T, rec, id, key)` | `_SGMA`: drawn state -> keyframe (4.3) |
| `PictureNames(names)` | picture name read / lookup used by the variant fields (8.3a) |
| `colour_block(block, rgb)`, `record_bytes(fields, raw)` | colour block 0xF7A0; re-encode a record dict |
| `compose_frame(draws)` | reference compositor (normal blending) |
| CLI | `python -m tsuki.misc_layer_state_and_motions --arrays`, `--motion ID` |

Emulator harness (`tsuki.misc_layer_state_and_motions_emu`): `LayerEmu`, `Stepper`, `Frames`,
`Rig(eboot).run(stmts, frames)` (returns the engine's sprite calls), `Rig.scene()`, `Rig.record(array, layer)`.

## 13. Open questions

1. Quake / shake / swing per-frame formula (VA 0x58206..0x58827, `rand()`, `sinf`) and how
   0xFE50 / 0xF390 turn the slots into offsets, zoom and rotation terms; MSQK / SHOK screen
   quakes (globals 0xDF0894 / 0xDF0818). The storage of all of them is decoded (4.4).
2. Crop / mask rasterisation: shape of the crop window (a16 feather?, a17 style 0/1/2/6/7) and
   how the offscreen pass combines a layer with its mask layer; FADS/STTR rule transitions
   (owner: compositing_transitions_shaders).
3. STRA (569): the periodic value at +0x2C8 is advanced but no reader in the draw was found.
   STCH a19 / STRR / motion field 8's +0x15C angle has no reader in the draw path either: no
   visible effect on PS4 (Switch-era fields like SNX4) [E].
4. Picture-variant fields 22/23 (8.3a) are modelled and checked call by call, including the
   texture rebinding side (+0x14); what the draw does with the face slot 0x111+l of a switched
   picture is the renderer's (draw path of section 7, face = catalog name1) and was not
   re-checked here. Field 18's colour key and fields 19..21 are checked as bytes (colour block
   and scene ext); how the colour block is used by the colour-effect shader belongs to
   compositing_transitions_shaders.
5. The alternative scene canvas 853.333 x 480 (section 6): which configuration value selects it.
6. Flag-word bits 3 (75 motions), 23 (all 24,192) and 27 (23,020) are set by the motion data
   but tested nowhere in 0xBE4C0, the table's only reader [E]; probably flags of the authoring
   tool. Bit 13 is never set. (Bit 9 = field 18, bit 34 = the field-8 angle write, 8.3.)
7. The global default colour-effect word 0x164B97C (applied by STCH) is written by 0x86A30,
   whose caller was not found.
8. Easing id 70 (separate jump-table case); ids 12, 13, 16, 17, 21..25, 28..33, 36..41 (101
   keys) use float64 formulas, not bit-exact ports (8.5).
9. STCF (5,179, always `STCF(l,8)` or `STCF(l)`): it sets bit l of the 64-bit word 0x1653E30 and
   byte 0x1653E40+l and yields; the reader of these globals was not traced (script.md's "filter").
10. Variables in layer statements: the model evaluates arguments with `obj_expr` (literal
   expressions); statements with `C<n>` / variable operands need the VM model
   (owner: vm_variables_errata).
11. `_SGMA` edits the keyframe table for the rest of the session; whether a save / resume
   restores the edited or the original table (and the motion records' frame counters) is part
   of the save state (owner: adv_system_state).

Dependencies on other gaps: blend modes (STBR), colour effects (STEC/SNX2/SNX3), noise (STNS),
FADS/rule transitions and the crop/mask shader pass belong to compositing_transitions_shaders;
the history copy (0x1651AC8, 0x7AF90) and resume of scene_state to adv_system_state; `_STMA`'s
wait word 0x164BA6E and the config byte 0x66C578+0xA5 to adv_system_state; variable operands to
vm_variables_errata.

## 14. Errata for other documents (proved here)

### Errata for script.md (4.3, 4.3a, 4.5, 6.6)

* **STCP** `(layer, v)` sets the layer's **z (draw order)**, scene +0x2FC (absent v = 0) - not
  a "per-layer parameter". `STCP(l,9250)...STCP(l,2)` move a layer above / below others
  [V-emu, 4,711 statements compared].
* **STZ4** a7 (55 statements with 1) is the pivot flag: zoom in screen space after the
  rotation, about position + (anchor - catalog anchor). Start values, time and easing only drive
  the live tween; the scene keeps the end values [V-emu].
* **STRT** `(l, r0, r1, ms, e, a5, a6)`: rotation r0 -> r1 over ms with easing a4; the scene keeps
  r1 (absent 0) [V-emu].
* **STRR** is not a rotation: it sets the +0xCB0/+0x15C angle (same field as STCH a19), which no
  draw code reads [V-emu + E].
* **STPV** sets the anchor (pivot) in picture coordinates: `trunc(x*640/480 * size class)`,
  `trunc(y*480/360 * size class)`, plus the anchor difference of a `_hh_vv` variant; a3 sets the
  rotation [V-emu].
* **SOXY** uses only a1, a2: a pixel offset x/3, y*448/1080 canvas units; a3/a4 are ignored;
  no layer bound check [V-emu].
* **STGS**: absent a1 = current scene alpha; the end alpha is clamped only from above (min 255).
* **STEC**: modes 0..81 are stored; the colour is stored for mode 1 only (others white); modes
  < 0 or > 81 are ignored [V-emu].
* **STCF** `(l, v)`: global bit set 0x1653E30 bit l and byte 0x1653E40+l = v (absent 0x3F);
  yields [V-emu].
* **SNX2 / SNX3** set the colour-effect parameters A (i16 +0xF4) / B (i8 +0x144), the same
  fields as keyframe fields 24 / 25 [V-emu].
* **STNS** `(l, n)`: n >= 13 stores 0; second byte 20 for n < 3 else 12 [V-emu].
* **STTR** `(l, n, m)`: per-layer rule `(n-1) | m<<12` (-1 for n <= 0) and loads rule n-1 [V-emu].
* **STRA**, **STCL**, **SQK2/SHK2/SSW2**, **SQS2/SHS2/SSS2**, **SPMA** (pause = 1 only for an
  argument of exactly 1): formats in 4.2 to 4.4 [V-emu].
* **STMA** `(l, id, w, n)`: n = time-quantisation step: the motion keeps its speed and advances
  in steps of n frames (`frames - frames % n`, VA 0xBA8B0; n >= 2; 15 retail statements), w =
  wait mode; id <= 0 stops the motion (id = 0, pause = 0, divisor and counter kept); the
  motion's key 0 is applied at once and motion values are written into scene_state, including a
  switch of the scene picture to a `_hh_vv` variant (fields 22/23, 8.3a) [V-emu].
* **SGMA** `(l, id, key)`: writes the layer's drawn x, y, alpha, rotation, zoom (and crop) into
  keyframe `key` of motion `id`: it edits the in-memory motion table so that the next STMA starts
  from the current state (4.3) [V-emu, all 109 retail statements].
* 4.3a: STCH does **not** reset flags, parent, soxy, mask, crop, a16, a17, angle, STRA, STNS;
  both STCH and SCH2 reset the per-layer rule sttr to -1; SCH2 resets blend, colour effect,
  colour and motion when it has no source layer; with a source layer both copy the source's
  motion record (a self-source statement stops the motion) and re-point every layer whose mask
  or parent is the source to the new layer; an empty picture name clears the layer [V-emu].
* 4.3a arg 11 is the **mask layer**, args 12..15 a **crop rectangle** in picture pixels, 16/17
  edge parameters (section 9) [E; H for 16/17].
* 4.5 "Open: rotation and zoom pivots for pictures placed through a source/base layer (SBAS),
  the size-class factors 1, 0.5, 0.25, 0.125 ... and the exact camera/projection code": solved
  in section 7.
* 6.6: keyframe fields 1/2 are the segment's easing and interpolation type (not x/y); the full
  field table is 8.3, the record layout 8.1 (72 bytes, field 8 i64 at +0x18); motions are
  absolute; fields 22/23 switch the picture variant (8.3a).

### Errata for images.md (7, 8, 11)

* `scale_class` (catalog +0x42) multiplies child positions and zoom under a parent layer (0x525B0,
  0x528C0) and STPV anchors (0x865A2): factors 0.5 / 0.25 / 0.125 for L / M / S, 1 for A [V-emu].
* anchor_x/anchor_y are the picture's pivot: the point placed on the layer position and about
  which scale, zoom and rotation act [V-emu].
* The test of catalog +0x3C bit 7 / +0x3E in STCH (0x828D8..0x82902) is dead for retail data
  (kind 0x0B in every record) [E + V-all].
* 11.2 / FORMATS 6.1: the 5 thumbnail framing outliers are explained in section 10 (no engine
  field; placement uses the anchor and the texture size only).

### Errata for layers.md itself (package K2, the C# stage; each line was found by comparing the stage with the engine under emulation, `tests/Tsuki.Tests.Data/Stage/engine_stage.py`)

* **4.1 "STCH only: reset the drawn record (0x49570, 0x4A770)"**: 0x4A770 runs for **STCH and SCH2** (VA 0x81A9C / 0x81ABB); 0x49570 (A rot = 0, rot delta = rot cur = 0, words +0x80 / +0x82 = 0)
  only for STCH. 0x4A770 clears in A: alpha offset (+0xCC), the move and alpha deltas (+0x104..+0x113), the angle (+0x15C), the motion record (0xBA800), blend, fx mode / A / B,
  the shake slots with their runtime fields, STRA (+0x2C8 / +0x2CA) and STNS (+0x1D0 / +0x1D1), and resets the colour block to white with a white history (0xF700). It does **not**
  stop a running alpha / rotation / zoom tween counter; the tweens end because their deltas are 0 (the zoom delta is not cleared: a zoom tween goes on).
* **4.1 live effects of the statement itself on A**: with no source layer (`src < 0`, 0x827BA) the scene, **drawn and committed** motion records are cleared and the drawn colour is
  set white (0xF7A0); if the scene picture was empty the drawn alpha becomes 0 as well as the scene alpha (0x8284B); an empty picture name (0x82618) clears the three motion records
  too. With a source layer, the drawn colour takes the copied ext colour (0xF7A0 after the reset), so a source layer that never had an STCH (ext colour 0, 0, 0) gives a black colour.
* **4.2 STCC** (VA 0x82D30) also clears the committed motion record B and resets the drawn colour block (0xF700 white / white); the drawn blend, fx and slots are not touched.
* **4.2 STRA** (VA 0x86EE0) in full: `n != 0`: scene stra = n, A +0x2C8 = n, +0x2CA = 0, +0x2CC (fade-in) = frames; `n == 0` and frames != 0: scene stra = 0 and only a fade-out
  starts (+0x2CE = +0x2D0 = frames), A +0x2C8 stays; `n == 0` and frames == 0: A +0x2C8 = +0x2CA = 0. `l = -1` and `n <= 0` clears the **STRA array only** (80 bytes at +0x1610,
  not the 16 STNS bytes after it that the Python model clears) and A's STRA words of all layers. The update (0x588A0) adds dt to the phase modulo 6000, ends the fade-in when the
  phase reaches it, counts the fade-out down and clears A +0x2C8 when it ends. The commit copies the scene STRA to B and the move copies it to A (a later `_STCC` + commit stops the effect).
* **4.3 STGS / STZ4 / STRT / STCL live setters** (0x4A620, 0x49930, 0x4A4B0, 0x83350): A alpha / zoom / rotation / position take the **end** value at once; the delta slots hold start minus end
  (alpha: `start/255 - end/255` with a float division; zoom: `-(end*0.001f + start*-0.001f)`, so equal values give -0; rotation: the i16 `r0 - r1`; move: engine units); the tween slot is
  `{ease, 0x1000, -(4096 / frames)}` for frames above 0 and all zero for 0 (a negative count also gives no tween); a count of 0 clears the deltas. The easing id is stored by the handler.
* **5 / 6 commit and B to A (corrected)**: the commit writes B (every field of `commit()` **except** zoom, zoom pivot, which go to A directly, plus the colour block through 0xF7A0 from the
  scene ext colour *with the previous B colours shifted into +0xB..*, the motion record by clear + 0xBA8E0, the shake slots' kind / delay / time / parameters, STRA, and the z array) and
  puts the scene zoom, the pivot flag and an ended zoom tween into A. The z array reaches A (0xDE2538) in the next update, not at the statement. The next update then moves B to A **after**
  the layer loop, per layer: if the layer shows nothing before and after (A unused and B.pic < 0) the texture parameters (blend, fx mode / A / B, the colour block) are left alone, the other
  fields are moved (flags, alpha, x, y, offset, rot, scale, anchor, mask, parent, soxy, crop, a16, a17, angle, STNS, STRA, z); the per-layer rule `sttr` is **not** moved (STTR writes A directly);
  A.used = (B.pic >= 0); the motion record is merged (0xBA8E0), committed shake slots replace running ones of another kind, and `apply_motion(dt = 0)` runs on the moved record (0x59957).
* **6 per-frame order** (0x57980): MSQK; then per layer 0..39: move / scale / rotation / alpha tween counters (`counter = trunc(counter + step * dt)`, ended at or below 0 or for a step of 0 or
  more), `alpha_cur = remaining(alpha) * alpha_delta`, the zoom tween counter, the motion (dt, or -dt while the rewind word 0x17DB234 is set), the two shake slots, the STRA phase; then the B to A move
  of a pending commit and the start of its transition, then the screen shock. The rotation, scale and alpha offsets (+0x7C, +0xE4, +0xCC) are evaluated again at the start of every layer draw (0x51060).
* **9 crop and mask**: the crop rectangle of the layer's own record is a clip window in **1920 x 1080 screen pixels** (the mask-layer term 0x49A60 adds it, scaled by 640 / 1920 and 360 / 1080, to the
  position of the masked layers; knife's clip window is the same), not a rectangle in picture pixels as the text above says [H, from 0x49A60 and knife].

* **9 draw order of window and masked layers** (0x59EA0, 0x5A5E0; found by comparing the engine's sprite-call order with the stage's, `engine_stage.py` step `d`): the draw walks the z-sorted slots and
  skips every layer with `mask >= 0`. A layer takes the **group path** when a visible masked layer (used, `min(1, alpha + alpha offset) != 0`, `mask == l`) follows it **in the slots after it**, or it
  has a crop window (width and height above 0), or it has a rule fade (`sttr >= 0`, even at alpha 0): the layer is drawn at alpha 1 with blend 8 (so it makes its sprite call even at alpha 0), then the layers masked by it (found by scanning the slots after
  it only: **a masked layer that is below its mask layer in z order is never drawn**), then the layer again with effect 58 as the mask pass; the sprite calls read `[W, masked..., W]`. Every other layer takes
  the plain path (0x51010 -> 0x51060). The group path renders offscreen; the C# stage emits the layers' quads with the window as scissor and does not build the group pass (K2.md, open issues).

* **STNS at draw time** (0x4F0E0, called from the layer sprite routine 0x4F320 after the picture): a layer with `stns` 1..12 that is drawn advances `stns_b` by the frames of the step (0xD47C0, at most 3) once
  per draw and, when the byte passes the period (20 for `stns < 3`, else 12), replaces it by the remainder (the dword at +0x1D2 is cleared); `stns_b` therefore cycles 1..period. The layer's own pass has
  no noise when it has a crop window; the layers masked by a window **with** a crop advance in the window's mask pass, those masked by a window without a crop do not (observed with the engine, 0x505BE).
  Layers that are not drawn (alpha 0, z 0, unused) keep the value.
* **What the sprite-call trace shows**: a layer with a running STRA haze (`+0x2C8 != 0`) has no plain sprite call (the haze pass draws it); a picture whose tiles all have flag 0 (BLUE, GREEN, ORANGE,
  YELLOW, a few placeholders) makes no call either.

### Errata for FORMATS.md

* 1.2: `formats/layers.md` now exists (this document; `tsuki/misc_layer_state_and_motions.py`,
  `tests/test_misc_layer_state_and_motions.py`).
* 7 P0 item 2 and P1 item 13 are answered here except shakes and the crop/mask pass (13.1, 13.2).
