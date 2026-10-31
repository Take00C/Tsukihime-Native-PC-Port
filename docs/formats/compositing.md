# Compositing: shaders, blend modes, draw effects, transitions and colour effects

Scope: how TSUKIHIME -A piece of blue glass moon- (PS4, CUSA47999, app 01.01) turns the layers of a
scene into pixels. This covers the 35 embedded pixel shaders (decoded from the GCN machine code), the
fixed-function blend states, the draw-effect word that `_STBR` and the engine select, straight vs
premultiplied alpha, the `_FADS` transitions (cross-fade and rule wipe, timing and mask source),
`_FADE`, and the CPU colour effects of `_STEC` / `_SNX2` / `_SNX3`. It resolves the conflict between
script.md 6.4, engine.md 4.4 and archives_ui_movies.md 5.4 about where rule masks come from.

Python reference:

- `tools/tsuki/compose.py`: numpy formulas (shaders, blend states, effects, transitions);
- `tools/tsuki/gcn.py`: the GCN decoder and interpreter used to derive and check them;
- `tools/tsuki/misc_compositing_transitions_shaders.py`: the eboot tables and the bit-exact STEC
  recolour;
- `tools/tsuki/misc_compositing_transitions_shaders_render.py`: the acceptance renders.

Tests: `tools/tests/test_compose.py`, `test_gcn.py`, `test_misc_compositing_transitions_shaders.py`.
Samples: `samples/compose/` (renders), `samples/misc_compositing_transitions_shaders/`
(`engine_tables.json`, `stec_golden.json`). Scratch tools and dumps: `scratch/compose/` (first
attempt, `disasm/` = all 67 blobs disassembled, `verify_ps.py`, `symexec.py`) and
`scratch/misc_compositing_transitions_shaders/` (`dlprobe*.py`, `stec_emu.py`, `probe_words*.py`,
function dumps `f_*.txt`; after the review also `dlprobe_capture.py` + `.log`, which emulates the
capture surfaces of the transitions, and `imgrec_refs.py` / `imgrec_const_calls.py`, which find the
image-record users). Section 15 lists what the review changed.

Confidence labels:

- **[ISA]**: formula derived from the shader's GCN code and checked by running that code in
  `tsuki.gcn` on random inputs (35 / 35 shaders, max error 9.7e-4, which is the fp16 export rounding).
- **[EMU]**: the engine's own x86 code run under unicorn. `scratch/misc_compositing_transitions_shaders/`
  uses the layer agent's machine `tsuki.misc_layer_state_and_motions_emu`, read-only. It runs VEX
  instructions natively and can feed whole script statements through ScriptStep.
- **[E]**: read in the disassembly. Addresses are eboot text VAs: file offset = VA + 0x2280; data VA =
  file offset - 0x365FA0 + 0x600000.
- **[D]**: counted in the script data (`tsuki.script.AllScr`, all 549 scripts of allscr.mrg).
- **[V]**: rendered and looked at.
- **[L]**: learned from public sources (AMD ISA guide, Mesa radeonsi). No code was copied.
- **[H]**: hypothesis.

---

## 1. Overview

```
script: _STCH/_STBR/_STEC/_SNX2/_SNX3 ...  -> scene_state 0x16500C0 (pending, per layer)
_FADS(type, ms, rule)  (0x9B06D -> 0x81520)
   |  0x7DD10: commit scene_state -> render layers B (0xDE2650 + 0x2C8*i):
   |           +0x18 picture (0x49F60), +0x1C _STBR mode (0x49F90),
   |           +0x1F _STEC id, +0x20 _SNX2, +0x22 _SNX3 (0x49FB0), +0x1E flags (STBR bit 0x80)
   |  0x42480(type, ms): transition request; 0x6B460(rule-1): rule picture -> texture slot 0x4DD
   v
per frame 0x57980: start of transition -> 0x6B910 (capture, timer); layers B -> A (0xDDB5E8)
   |  picture (re)load: 0x4BB50 / 0x4B3C0 set STEC/SNX globals -> 0xD8B80 -> texture build 0xD6900
   |                    -> CPU recolour 0xD36F0 (section 9)
   v
display lists (0x1DE83E0 + 24*n, records of 0x70 bytes):
   layer draw 0x4F320 -> 0xC2620 -> 0xEB780(slot, prio, WORD) -> [type 2: WORD][type 1: quad]
   transition process 0xC draw 0x67820 (prio 0x32) -> rule mask / old-frame capture quads
   v
executor 0xD9E10 (lists 1, 0, 10, 11 per frame, 0xDB670)
   type 2 (0xD9F91): effect e = WORD & 0xFF (< 0x44), flag = WORD bit 0x100 for some e
   type 1 (0xD9F00): bind texture (0xD7E70 -> 0xEF960), quad draw 0xD2BE0:
        copy the 0x700-byte DisplaySurface template, set 4 vertices,
        +0x518 blend state, +0x608 shader mode, +0x60C strength  (effect tables, section 5)
   v
DisplaySurface_Draw 0xF12F0: SelectShader 0xF0E70, constant buffers, textures, blend state
   -> GCN pixel shader (section 2) -> CB_BLEND0_CONTROL (section 4) -> render target
```

A port can implement compositing as: for each quad, `out = blend_state(shader(S, D, C, P), RT)` with
the tables below. Here S is the layer texel, D the render target under the pixel, C the vertex colour
(alpha = layer opacity) and P the constant buffer.

---

## 2. The embedded pixel shaders [ISA]

### 2.1 Decoder

`tsuki.gcn` decodes the Sea Islands (GCN 2, PS4 "Liverpool") microcode formats used by the 67 blobs:
SOP1/SOP2/SOPK/SOPC/SOPP, SMRD, VOP1/VOP2/VOPC/VOP3 (a and b), VINTRP, MIMG and EXP. It also parses
the Gnmx `Shdr` header (register block, input-usage slots, semantics). Sources: AMD "Sea Islands
Series Instruction Set Architecture" (2013), chapters 12 and 13 [L]. shadPS4's decoder (GPL) was read
only to cross-check opcode numbers.

Measured on all 67 blobs [V-all]: 1,910 instructions, 36 distinct mnemonics (42
encoding/opcode pairs), no unknown encoding, and every blob ends in `s_endpgm` within its declared
code size. Per stage (`test_gcn.py` asserts all of these):

| stage | blobs | instructions | `image_sample` | `exp` |
|---|---:|---:|---:|---:|
| PS | 35 | 1,403 | 58 | 35 (one MRT0 export each) |
| VS | 32 | 507 | 0 | 119 (position and parameter exports) |
| all | 67 | 1,910 | 58 | 154 |

The VALU set is v_mul, v_mac, v_mad, v_madak, v_madmk, v_add, v_sub, v_subrev, v_min, v_max,
v_med3, v_rcp, v_log, v_exp, v_fma, v_cndmask, v_cmp_gt/le, v_cvt_pkrtz_f16_f32 and v_mov.

The interpreter (`gcn.Machine`) is SIMT over N lanes in numpy (VGPR = uint32 bit patterns, lane
masks for VCC/EXEC/SGPR pairs). It evaluates float ops in float32. Resources are abstract: a T# carries
a texture id, and `image_sample` asks a callback for texels. MRT0 is exported packed fp16
(`compr`, round toward zero), so a port can match the PS4 output to within 1 fp16 ulp.

### 2.2 Inputs, constant buffers and the backdrop

| name | what | where it comes from |
|---|---|---|
| S | `texture`, sampled at `m_texcoord` | the picture of the quad (+0x648 = surface source texture +0x2C8) |
| D | `dst_texture`, sampled at `m_transition` | the render target as it is before this draw (0xF133F..0xF1370 obtain the current target through 0x2085188 vfunc +0x148 after 0xF53D0) [E; H: same surface or a copy] |
| C | `m_color` | the vertex colour bytes / 255 (0xD2BE0, scale vector 0x2D4260); C.a = layer opacity |
| P | `CommonFragmentParamsConstants` (blend PS) | surface +0x5FC..+0x60B = (1 - strength, strength, P2, mode byte) |
| P | `TransitionParamsConstants` (transition PS) | surface +0x5D0..+0x5DC = (lo, hi, inv, -), copied from the texture's +0xF0 (0xFB0D0) by SelectShader 0xF12C6 |
| Q | `SepiaConstants` | (Cb, Cr, blend) clamped to [-0.5, 0.5] x 2 and [0, 1] (0x133D20) |

`m_transition` comes from the blend vertex shader #66 (shared by all 24 blend shaders):
`m_transition = (0.5 * W * (pos.x + 1), 0.5 * H * (1 - pos.y))` with `(W, H) = texture_size`
(`s_buffer_load_dwordx2` of the VS constant buffer). The draw sets `texture_size` from surface
+0x5F4 = (1.0, 1.0) (ctor 0xEF4F1, constant 0x2D7BA0), so D is sampled at the pixel's normalised
screen position.

The constant buffers are uploaded by the shader class's vfunc +0x50. For blend shaders this is
0x10A350, which calls 0x105250 twice: buffer 0 (VS) from the given pointer and buffer 1 (PS) from
pointer + 8. 0x105250 memcpy's the size stored in the class's buffer descriptors (+0x80, 24-byte
records). So the blend PS reads `P.y` = surface +0x600 and `P.z` = +0x604. Shader #64 reads only
`s_buffer_load_dword s0, ..., 0x4`, which is P.y [E].

### 2.3 Formulas

Notation: s = S.rgb, a = S.a, d = D.rgb, Da = D.a, c = C.rgb, alpha = C.a (layer opacity), k* as
defined in each row, `sat` = clamp to [0, 1]. All values are float32. Every row was checked against the
interpreted ISA (section 2.5).

Basic, transition, sepia and movie shaders:

| PS blob | class key(s) | output RGBA |
|---|---|---|
| #2 | basic 2DTex, basic VtxColTex, capture shader | S * C (all four channels) |
| #3, #5 | basic 2D, basic VtxCol / clear | C |
| #10 | dummy | (S.r*C.r, 0, 0, S.a*C.a) |
| #4 | basic YCbCr (movies, NV12) | y = 1.1644*Y.r - 0.072775; r = y + 1.79270*Cr - 0.896352; g = y - 0.21330*Cb + 0.106650 - 0.53290*Cr + 0.266450; b = y + 2.11240*Cb - 1.056200; a = 1 (BT.709 limited range; Cb = CbCr.r, Cr = CbCr.g) |
| #0 | transition 2DTex | f = sat(\|P.z - smoothstep(P.x, P.y, S.r)\|); out = C * f. The texture is the rule picture; only its red channel is read (dmask 0x1) |
| #1 | transition 2D | out = C * smoothstep(P.x, P.y, M.r) (`transition_tex` at `m_transition`) |
| #8 | sepia 2DTex | x = S*C; rgb = sepia(x.rgb), a = x.a |
| #9 | sepia 2D | rgb = sepia(C.rgb), a = C.a |
| #6 | sepia transition 2DTex | x = S*C; rgb = sepia(x.rgb) (not multiplied by the mask), a = x.a * smoothstep(P.x, P.y, M.r) |
| #7 | sepia transition 2D | x = C * smoothstep(P.x, P.y, M.r); rgb = sepia(x.rgb), a = x.a |

with `smoothstep(lo, hi, m) = t*t*(3 - 2t)`, `t = sat((m - lo) * rcp(hi - lo))`. The code computes
`v_rcp_f32(hi - lo)` and then multiplies with clamp. `sepia(rgb)`: Y = 0.299 r + 0.587 g + 0.114 b
(float bits 0x3E991687, 0x3F1645A2, 0x3DE978D5). The target is (Y + 1.402 Cr, Y - 0.34414 Cb -
0.71414 Cr, Y + 1.772 Cb), and the result is rgb + Q.z * (target - rgb), with Cb = Q.x, Cr = Q.y.

The 24 blend shaders (surface shader mode 1..25; mode 3 = basic 2DTex). Two families differ in
output alpha. The "nexl" family outputs alpha = 1 + P.y (Da - 1) and does the opacity mix itself. The
"PS" family outputs alpha = alpha a + P.y (Da - alpha a) and leaves the opacity mix to the blend state.
k = alpha * max(P.z, a) (P.z = 1 for normal draws, so k = alpha; P.z = 0 with the `_STBR` flag, so
k = alpha a).

| mode | key (PS blob) | family | rgb |
|---:|---|---|---|
| 1 | add (#64) | nexl | min(1, d + alpha s) |
| 2 | addalpha (#63) | nexl | min(1, alpha s + d (1 - alpha a)) |
| 4 | darken (#19) | nexl | d + alpha (min(d, s) - d) |
| 5 | dodge (#21) | nexl | min(d / (1 - s k), d < 3/255 ? 0 : 1) |
| 6 | lighten (#22) | nexl | d + alpha (max(d, s) - d) |
| 7 | multiplicative (#23) | nexl | d + k (s d - d) |
| 8 | opaque (#24) | nexl | d + alpha (s - d) |
| 9 | screen (#40) | nexl | 1 - (1 - d)(1 - s k) |
| 10 | sub (#41) | nexl | max(0, d + alpha (s - 1)) (subtracts the inverse of S) |
| 11 | ps_add (#25) | PS | min(1, d + c s) (uses the vertex colour rgb, not alpha) |
| 12 | ps_burn (#26) | PS | max(0, (d - 1) / s + 1) |
| 13 | ps_darken (#27) | PS | min(d, s) |
| 14 | ps_diff (#29) | PS | \|d - s\| |
| 15 | ps_diff5 (#28) | nexl | \|d - s alpha a\| |
| 16 | ps_dodge (#31) | PS | min(1, d / (1 - s)) |
| 17 | ps_dodge5 (#30) | nexl | min(d / (1 - s alpha a), d < 3/255 ? 0 : 1) |
| 18 | ps_exclusion (#32) | PS | s + d - 2 d s |
| 19 | ps_hardlight (#33) | PS | x = c s; x >= 0.5 ? 1 - 2(1 - d)(1 - x) : 2 x d |
| 20 | ps_lighten (#34) | PS | max(d, s) |
| 21 | ps_multi (#35) | PS | d s |
| 22 | ps_overlay (#36) | PS | d >= 0.5 ? 1 - 2(1 - d)(1 - s) : 2 d s |
| 23 | ps_screen (#37) | PS | s + d - d s |
| 24 | ps_softlight (#38) | PS | 2^(log2\|d\| * e), e = s >= 0.5 ? 1 / (2 s) : 2 (1 - s) |
| 25 | ps_sub (#39) | PS | max(0, d + s - 1) (linear burn) |

The "5" variants (diff5, dodge5) are the plain modes with the source pre-multiplied by
`alpha * a`. Dodge (modes 5 and 17) forces pure black to stay black: 3/255 is the literal
0x3C40C0C1, compared with `v_cmp_gt_f32`. The min/max ops are `v_min_f32` / `v_max_f32`, so a NaN
(e.g. 0 * inf in dodge with s * k = 1) gives the other operand. compose.py uses `np.fmin`/`np.fmax`
for this. Soft light is the power form `d^e`, not the W3C formula, and hard light applies the vertex
colour to S first.

### 2.4 Worked example: PS #0, transition 2DTex (VA 0xDCA6E0, code at +0x4C)

```
0028 image_sample v[5..], v[2..], s[0:7], s[8:11] dmask:0x1   ; v5 = S.r (the rule picture)
0034 v_mov_b32 v2, s12 ; 0038 v_sub_f32 v2, s13, v2           ; P.y - P.x
003c v_rcp_f32 v6, v2                                          ; 1 / (hi - lo)
0064 v_subrev_f32 v1, s12, v5                                  ; m - lo
0068 v_mul_f32 v5, v6, v1 clamp                                ; t
0070 v_madak_f32 v1, -2.0, v5, 0x40400000 (3)                  ; 3 - 2t
0078 v_mul_f32 v5, -v5, v5 ; 0080 v_mad_f32 v1, v5, v1, s14    ; P.z - t^2 (3 - 2t)
0088 v_med3_f32 v1, 1.0, |v1|, 0                               ; sat(|...|)
0090..009c v_mul_f32 v4/v3/v2/v0 = C.x/y/z/w * v1              ; out = C * f
00ac exp mrt0, v1, v0, v0, v0 compr done vm
```

PS #64 (add) in full is: `D.rgba` at `m_transition`; `S.rgb` at `m_texcoord` (dmask 0x7, so S.a is
not even fetched); `v_mac_f32 d += alpha*s`; `v_min_f32 1.0`; and alpha = `v_mad_f32(P.y, Da - 1, 1)`.
Every other listing is in `scratch/compose/disasm/NN_PS.txt`.

### 2.5 Verification

`test_compose.py::test_all_pixel_shaders_match_isa` loads each PS from eboot.bin and checks its
shadPS4 hash (blob +4, `compose.PS_HASH`). It runs 4,096 lanes with 4 parameter vectors through
`isa_reference` (the interpreter) and through `ps_reference` (the numpy formula). It compares with
`err = |isa - ref| / max(1, |ref|)` and requires < 1/255 for every shader. The measured maximum is
9.7e-4 (PS #4 YCbCr, large magnitudes rounded to fp16).

The textures are procedural: each resource is a fixed function `f(u, v)` of the exact float32
coordinates that the shader's `image_sample` passes. The reference evaluates S, Y and CbCr at
`m_texcoord` and D (`dst_texture`) and M (`transition_tex`) at `m_transition`. About 6 % of lanes
are snapped to 0, 0.25, .., 1, so black, white and d < 3/255 are also hit. A wrong interpolant or a
wrong S/D binding therefore changes the texels and fails the test. Mutation checks
(`ps_isa_errors(..., swap_*)` in the test file):

- evaluating D and M at `m_texcoord` fails 27 of 35 shaders, exactly those that read D or M
  (the 24 blend shaders, #1, #6 and #7);
- swapping S and D fails 29 of 35, exactly those that read S or D (the passing six, #1, #3, #4,
  #5, #7 and #9, read neither);
- adding 0.01 to the overlay formula fails on PS #36 only.

So every PS samples API slot 0 (`texture`, S) at `m_texcoord` and slot 1 (`dst_texture` or
`transition_tex`) at `m_transition`, and YCbCr reads both planes at `m_texcoord` [ISA].
`scratch/compose/symexec.py` is a second, independent path. It executes the same decoded
instructions symbolically (sympy) and printed the formulas of section 2.3, including the YCbCr and
sepia constants.

### 2.6 The MRT0 colour export: fp16 round toward zero [ISA, V]

Every PS ends with `v_cvt_pkrtz_f16_f32` and an `exp mrt0 ... compr`: the four float32 results are
packed as IEEE binary16 **rounded toward zero**, and the colour block converts that to the UNORM8
target, clamping the source to [0, 1] (NaN -> 0) before blending. The model, in this order:

```
x   = shader output (float32)
x   = 0 if NaN; clamp(x, 0, 1)
x   = rtz16(x)                  # nearest fp16 at or below |x|, sign kept (0.5 stays 0.5; 1/3 -> 0.33325195)
dst = blend(x, dst, state)      # 4.1, then the UNORM8 store: round to nearest (compose.to_u8)
```

`compose.rtz16` is the conversion, `compose.export_mrt0` the whole export, and `compose.EXPORTS`
names the two models: `"rtz16"` (the PS4) and `"f32"` (the shader result straight into the blend,
which is what compose did before this section). `compose.draw(..., export=...)` takes either (default
`"f32"`, so older callers are unchanged); `compose.shade` and `compose.execute_list` (6.1) default to
`"rtz16"`.

Why it matters: a value just above k + 0.5 levels in float32 is cut below it by the fp16 step (2^-11
near 1.0, 0.12 levels), so the store rounds down instead of up. Of the 255 midpoints (k + 0.5) / 255,
`to_u8` rounds 127 up in float32 and 1 after `export_mrt0`.

Measured (D1, 2026-10-25):

- **Against the ISA** (the `test_compose.py` inputs: 35 PS x 4 parameter vectors x 4,096 lanes,
  2,293,760 finite channels): the formulas equal the interpreter's export bit for bit on 10.22 % of
  the channels as float32 and on **99.98 %** after `rtz16`. The worst remaining difference is one fp16
  ulp (relative 4.9e-4), from float32 operation order. So the 9.7e-4 of 2.5 is the export rounding.
- **Against a GPU** (AMD RX 9070 XT, GL 3.3 core; `tests/Tsuki.Tests.Render/Compositor/`): the AMD GL
  driver happens to export RGBA8 colour the same way (a plain float32 shader output, without the
  shader export below, followed `rtz16` on 1,384 of 1,384 values where the two models give different
  levels: the mutation run of `ExportModelTests`). With the model, 500 synthetic lists (every mode, word,
  effect, state, fill, rule mask, rule wipe, cross-fade and `_FADS` capture path) are within 1 level
  everywhere, with 98.90 % of 32,768,000 pixels exact, and the rule mask is bit-exact. The aligned
  13-layer frame with a rule wipe (S3.md 4.3) is within 2 levels on **99.970 %** of its pixels with the
  model and on 99.556 % without it (exact: 97.396 % / 85.627 %).
- The remaining 1-level differences are rounding ties of the blender (fractions 0.45..0.52 of a
  level). The PS4 blender's precision on RGBA8 targets is not known (14 #9).

**The port does the export itself; it does not rely on the driver.** How a driver turns a float32
fragment output into an RGBA8 target is the driver's choice: AMD's goes through fp16 round toward
zero (as above), while a driver that converts float32 straight to UNORM8 (typical of other vendors;
not measured here) would give compose `"f32"`, 99.556 % within 2 levels on the aligned frame, below
the 99.9 % bar. So every compositor fragment shader ends in `exportMrt0` (`src/Tsuki.Render/Shaders/
CompositorShaders.cs`): NaN -> 0 found from the bits, clamp to [0, 1], then for x >= 2^-14 the low 13
mantissa bits are cleared and below it x is floored to a multiple of 2^-24 (the fp16 subnormal step).
The values are exact fp16 values, so a driver's own fp16 export leaves them unchanged.

- `ExportModelTests` draws 65,536 values (every fp16 value in [0, 1] with its float32 neighbours,
  level midpoints, NaN, infinities, out-of-range, subnormal) through the basic program into an
  **RGBA32F** target, where no driver conversion takes place: 65,536 of 65,536 equal
  `compose.export_mrt0`, in GLSL 3.30 core and in GLSL ES 3.00. Into the RGBA8 frame, 65,536 of 65,536
  equal `to_u8(export_mrt0)`.
- Mutation check: with `o_color = r` instead of `exportMrt0(r)`, 50,128 of the 65,536 float exports
  differ and the test fails, while the RGBA8 path still matches on AMD (the driver rounds by itself).
  That is why the RGBA8 check alone cannot prove the shader does the export.
- On AMD the explicit export is byte-identical: the 500 synthetic lists give the same 32,407,203 exact
  pixels with and without it, and the aligned frame the same 99.970 %.

Golden frame comparisons (T5 (b), ARCHITECTURE 10.5) use `"rtz16"`.

---

## 3. Surface state and shader selection (SelectShader 0xF0E70) [E]

The `DisplaySurface` (0x700 bytes; vtables 0x42D308 / 0x42D3B8; ctor 0xEF3F0) holds the shader table +0x2D8
(index 0 basic 2DTex, 1 basic 2D, 2 transition 2DTex, 3 transition 2D, 4 sepia 2DTex, 5 sepia 2D,
6 sepia transition 2DTex, 7 sepia transition 2D, 11 dummy, 12..35 the blend shaders; checked against
the crc32 keys stored at 0xEF50A..0xEF817). It also holds these fields:

| field | meaning | written by |
|---|---|---|
| +0x2C8 | source texture | 0xEF960 (texture bind) |
| +0x2D0 | "mask" = the source texture itself when that texture carries transition parameters (texture +0xFC != 0), else 0 | 0xEF960 at 0xEFB13; 0xF06B0 (only from the dead 0xED440) |
| +0x518 | blend state index (section 4) | 0xF0830 (from 0xD2BE0), 1 by default |
| +0x51C | extra shader slot 0..3 (index 7 + n) | 0xF0770 |
| +0x5BC, +0x5C0 | sepia Cb, Cr (default -0.1, +0.1) | ctor 0xEF81E; 0xF0630 (dead caller) |
| +0x5C4 | sepia blend (default 1.0) | ctor 0xEF826; 0xF0660 (dead caller) |
| +0x5C8 | transition VS constants (mask uv scale) | 0xF06B0 |
| +0x5D0..+0x5DB | transition PS constants (lo, hi, inv) | 0xF12C6 from the texture's +0xF0 |
| +0x5F4 | blend VS constants texture_size (1, 1) | ctor |
| +0x5FC, +0x600, +0x604 | blend P = (1 - strength, strength, P2) | 0xF145D..0xF1490 at draw time |
| +0x608 | shader mode byte 0..25 | 0xF0690 (from 0xD2BE0) |
| +0x60C | strength | 0xF06A0 (from 0xD2BE0) |
| +0x610 / +0x614 | flag count (5) and a 32-bit flag set, bit k-1 counted from the MSB | see below |

The flag set holds five flags:

- k = 1: enabled. Set on every texture bind (0xEF960).
- k = 2: textured. Set by 0xEF960 when a texture is bound; cleared by 0xF0570 (untextured quad
  0xD27E0).
- k = 3: set on every bind.
- k = 4: culled. 0xF0AE0 sets it when every vertex alpha <= 0 or +0x240 <= 0; while it is set,
  submit 0xF0DC0 skips the draw.
- k = 5: sepia enable. Its only setter, 0xF05D0, is called only from 0xED440, which has no callers,
  no data pointers and no raw rel32 references.

Selection:

```
if textured and +0x2C8 != 0:
    switch +0x608 (jump table 0x2D7CAC):
      0:          if sepia flag and +0x5C4 > 0: index 4 (6 with mask)    # unreachable, see 10
                  elif mask: index 2 (transition 2DTex)                    # rule pictures
                  elif +0x51C: index 7 + +0x51C
                  else: index 0 (basic 2DTex)
      3, > 25:    index 0 (basic 2DTex, even with a mask)
      1..25 (not 3): the blend shader of that mode (2.3)
    +0x648 = source texture
else:
    index 1 / 3 / 5 / 7 = basic 2D (+2 mask, +4 sepia); +0x648 = 0
```

`DisplaySurface_Draw` 0xF12F0 binds the shader. For mode != 0 or the dummy shader it gets the
backdrop texture (D). If a mask is present it uploads the transition constants from +0x5C8 and binds
slot 1. For mode != 0 it computes `+0x5FC = 1.0 - strength`, `+0x600 = strength`,
`+0x604 = (mode in {5, 7, 9, 10} and +0x518 == 0) ? 0 : 1` (bit test of 0x6A0 at 0xF147C) and
uploads the blend constants from +0x5F4. For mode 0 it uploads nothing, so the basic shaders take no
constants. It then binds the source texture to slot 0 and draws through vfunc +0x40,
with the constant block 0x2D7C80 = (6, 4, 4, 2) and the vertex buffers.

---

## 4. Fixed-function blend states and straight alpha

### 4.1 The table (VA 0x2D8030, 14 x 28 bytes) [E]

0xF77E0(state) reads record `state` (any id >= 14 uses record 1). It sets BLEND_ENABLE (bit 30)
and SEPARATE_ALPHA_BLEND (bit 29) from byte +0x18 (0x14E300, 0x14E360). It packs COLOR_SRCBLEND
(+0), COMB_FCN (+8), COLOR_DESTBLEND (+4) (0x14E310) and ALPHA_SRCBLEND (+0xC), ALPHA_COMB_FCN
(+0x14), ALPHA_DESTBLEND (+0x10) (0x14E330). 0x13C2D0 writes PM4 `SET_CONTEXT_REG` 0xC0016900, reg
0x1E0 = CB_BLEND0_CONTROL, value & 0xFFFF1FFF. Multiplier codes: 0 zero, 1 one, 4 src alpha,
5 1 - src alpha, 6 dst alpha, 7 1 - dst alpha, 9 1 - dst colour. Combine codes: 0 add, 1 src - dst,
2 min, 3 max.

| state | colour (src, dst, fn) | alpha (src, dst, fn) | effect |
|---:|---|---|---|
| 0 | blending off | | rgba = shader output |
| 1 | (sa, 1-sa, add) | (sa, 1-sa, add) | straight "over"; a = sa^2 + da(1 - sa) |
| 2 | (sa, 1, add) | (sa, 1, add) | additive: rgb = s sa + d |
| 3 | (0, 1-sa, add) | (sa, 1-sa, add) | erase: rgb = d (1 - sa) |
| 4 | (0, 1, max) | (1, 0, max) | **alpha := sa**, rgb kept |
| 5 | (0, 1, max) | (1, 1, min) | alpha := min(sa, da) |
| 6 | (da, 1-da, add) | (0, 1, max) | rgb = s Da + d (1 - Da), alpha kept |
| 7 | (da, 1, add) | (0, 1, max) | rgb = s Da + d |
| 8 | (sa, 1, src-dst) | (sa, 1, src-dst) | rgb = s sa - d |
| 9 | (1-d, 0, add) | (0, 1, max) | rgb = s (1 - d) |
| 10 | (0, 1, max) | (0, 0, min) | alpha := 0 |
| 11 | (0, 1, max) | (1, 1, max) | alpha := max(sa, da) |
| 12 | (0, 1, add) | (sa, 0, add) | alpha := sa^2 |
| 13 | (da, 1-da, add) | (sa, 1-da, add) | rgb = s Da + d (1 - Da), a = sa^2 + da(1 - da) |

MIN and MAX are taken of the multiplied terms, `max(src * Fs, dst * Fd)` [L]. Mesa's radeonsi
treats `func(src*F, dst*ONE)` as commutative for MIN/MAX only when F does not depend on dst
(`si_blend_check_commutativity`), and Mesa's GL state tracker forces both factors to ONE for
GL_MIN/GL_MAX. Both only make sense if GCN applies the factors. The engine's states 4, 5, 10 and 11
also only make sense this way: with colour factors (ZERO, ONE) they are alpha-channel edits. The rule
wipe (7.4) relies on state 4. In compose.py this is `apply_blend_state` / `_comb`.

### 4.2 Straight alpha [E, V]

Layer pictures use **straight (non-premultiplied) alpha**:

- The basic 2DTex shader outputs `S * C` without multiplying rgb by alpha.
- The normal draw uses state 1 = SRC_ALPHA / ONE_MINUS_SRC_ALPHA on colour.
- The decoded pictures are straight. tsuki.image outputs the bytes the engine uploads. On real
  pictures many partially transparent pixels have rgb > alpha (AKI_A_30_01_N01: 202,892 of
  7,257,820; IMG0795: all 13,977,035), which is impossible for premultiplied data.

The render-target alpha is used as a mask channel (states 4..7, 10, 11). It is not a coverage value
that a port must present.

---

## 5. Draw effects (the draw-mode word)

### 5.1 Decoding (executor case 2, 0xD9F91, and the quad draw 0xD2BE0) [E]

```
word = record +4 (i16), written by 0xD9D90(word)
low  = word & 0xFF;  e = low < 0x44 ? low : 0                              -> 0x1DBD0A0
flag = (word & 0x100) and (word & ~0x100) <= 0x2B and bit (word & ~0x100) of 0xA1A800286A0
                                                                            -> 0x1DBD09C
quad draw 0xD2BE0:
  blend state +0x518 = flag ? 0 : EFFECT_BLEND_STATE[e]      (dwords 0x2D55E0, index e)
  if 1 <= e <= 51: +0x608 = EFFECT_MODE[e-1] (bytes 0x2D5978), +0x60C = EFFECT_STRENGTH[e-1]
                   (floats 0x2D59AC)
  else:            +0x608 = 0, +0x60C = 0
```

FLAG_EFFECTS = {5, 7, 9, 10, 15, 17, 31, 33, 35, 36, 41, 43} (dodge, multiplicative, screen, sub,
ps_diff5, ps_dodge5 and their 26+ copies).

| e | shader mode | strength | blend state | named use |
|---|---|---|---|---|
| 0 | 0 (basic) | 0 | 1 over | normal draw, cross-fade |
| 1..25 | e (3 -> 0) | 0 | 1 | ordinary layer with `_STBR` mode e |
| 26 | 0 | 0 | 6 through dst alpha | `0xD24D0(0)`: masked layer, old frame in the rule wipe |
| 27..51 | e - 26 (29 -> 0) | 1.0 (29: 0) | 1 (29: 6) | masked layer with `_STBR` mode e - 26 |
| 52 | 0 | 0 | 2 additive | rule wipe (0x679B1), overridden by 58 before any quad |
| 53 | 0 | 0 | 7 rgb += s Da | `0xD24D0(0x34)`: second picture of a two-texture slot (0xEB968), layer paths 0x4FBF2 / 0x50845 |
| 54, 60..62, 64, 66, 67 | 0 | 0 | 1 | |
| 55 | 0 | 0 | 10 alpha := 0 | masked layer setup (0x503B5) |
| 56 | 0 | 0 | 11 alpha := max | transition start (0x6B9B9) |
| 57 | 0 | 0 | 5 alpha := min | masked layer mask (0x4F9F9) |
| 58 | 0 | 0 | 4 alpha := src | rule mask (0x67A0B), masked layer mask (0x502E4) |
| 59 | 0 | 0 | 9 | |
| 63 | 0 | 0 | 2 | |
| 65 | 0 | 0 | 13 | |

With strength 1 (effects 27..51), the blend shader's output alpha is Da. Through blend state 1 this
means `rgb = shader * Da + d * (1 - Da)`, so the effect is applied only where an earlier pass wrote
the layer's mask into the render-target alpha.

### 5.2 From `_STBR` to the effect [E + EMU]

- `_STBR(layer, mode [, x, flag])` (handler 0x990C2 -> 0x830D0, 14,940 statements [D]) writes `mode`
  (default 0) to scene_state +0x54 + 2*layer (0x831A4). If the 4th argument is 1 it ORs 0x80 into
  +0xA4 + layer (0x831C9). The 3rd argument is not read: the handler fetches arguments 0, 1 and 3
  only. Layer > 39 is ignored.
  - **The handler only sets the flag; it never clears it** [E]: 0x831C9 is the only store to +0xA4
    in 0x830D0 (the test checks this). `_STBR(l, m)` without the 4th argument keeps an earlier flag.
    The mode and the flag are both reset to 0 only by the layer resets: the `_STCH` backend
    0x81760 (mode 0x826FB, flag 0x82686; script.md: `_STCH` resets the layer, `_SCH2` does not),
    `_STCC` 0x82D30 (0x82FCA, 0x82F23) and the engine's scene reset 0x789F0 (0x78B8B, 0x78CDA;
    called from 0x605A2, 0x60672, 0xA30D8). `_SCH2` with a source layer copies both from it
    (0x81B1C, 0x823DB). A port must keep the flag until one of these resets.
  - Before the first layer change since the last commit, the handler itself snapshots
    scene_state (0x830FD..0x83137): if word 0x164B98E == 0 it calls 0x7AF90([0x16543B0], 1), sets
    0x164B98E = 1 and memcpy's 0x1A08 bytes from 0x16500C0 to 0x1651AC8 (adv_state.md, kind 1).
    The shared helper 0x7F410 is used by other commands, not by `_STBR`.
- `_FADS` commits it: 0x7DD10 calls 0x49F90(i, mode), which sets render layer B +0x1C (u16), and copies
  +0xA4 to +0x1E.
- The layer draw 0x4F320 builds the word. For an ordinary layer (render-layer +0x1C0 == 0) it is
  `word = (flag ? 0x100 : 0) | mode` (0x5085E -> 0xC2620 -> 0xEB780).
  - **Emulated** [EMU, `probe_words*.py`]: `_STCH(1,img1685,...)` + `_STBR(1,17)` -> word 0x11.
  - `_STBR(1,17,,1)` -> 0x111. `_STBR(2,23)` -> 0x17 (and the same word for the sprite's face slot
    0x111 + layer).
  - No `_STBR` -> 0. Opacity 128 -> vertex alpha 0.502.
- The other layer paths (+0x1C0 set, kinds 1 and 3, cell sprites with +0x2B8) draw in two passes. The
  first pass uses effect 55/57/58 and writes the mask into the render-target alpha. The second uses
  `0xD24D0(mode)` (table 0x2D5860: mode m in 1..25 -> 26 + m; 0, > 61 -> 26; 52..61 -> 53, 53, 54..58,
  26, 26, 54) [E]. Which layers take this path belongs to the layers document.

Result for an ordinary layer over an opaque backdrop d (strength 0, P2 = 1 unless flagged).
alpha = `_STCH` opacity / 255 and a = picture alpha:

- modes 0 and 3: `lerp(d, s, alpha a)`.
- PS family (11..14, 16, 18..25): `lerp(d, f(s, d), alpha a)` with f from 2.3.
- nexl family (1, 2, 4..10, 15, 17): the shader rgb of 2.3 replaces the pixel (output alpha 1). The
  picture alpha a is ignored by add, darken, lighten, opaque and sub, and by dodge, multiplicative and
  screen unless `_STBR`'s 4th argument is 1.
- The flag (4th argument 1) acts on the effects of FLAG_EFFECTS only (modes 5, 7, 9, 10, 15, 17).
  For all six it turns blending off (blend state 0). It also sets P2 = 0 for 5, 7, 9 and 10 (0x6A0
  mask at 0xF147C), but only 5, 7 and 9 read P2, so only their colour changes: k = alpha a.
  - Modes 10, 15 and 17 look exactly the same with and without the flag [ISA]. Sub (#41) never
    reads P.z, and diff5 / dodge5 (#28, #30) always use alpha a. With strength 0 a nexl shader
    outputs alpha 1, so blend state 1 (over: rgb = shader, alpha = 1) and state 0 (rgb = shader,
    alpha = 1) give the same pixel.
  - Proof: interpreting PS #21, #23, #40, #41, #28 and #30 with P.z = 0, 0.5 and 1 on 2,048 random
    lanes changes the output of #21, #23 and #40 by up to 0.93 and of #41, #28 and #30 by exactly 0.
    The output alpha is 1.000 on every lane.

Census [D] (14,940 statements; flagged = 4th argument 1):

- 17: 3,473 (538 flagged); 11: 3,156 (2); 23: 1,909 (1); 24: 1,388; 22: 1,369 (2).
- 7: 863 (56); 10: 629 (64); 19: 486; 5: 284 (178); 1: 236 (1); 16: 231 (5); 15: 145 (16).
- 25: 114; 21: 106; 12: 104; 9: 95 (16); 18: 74 (1); 0: 57; 20: 53; 13: 48; 14: 42; 6: 30;
  4: 24 (3); 2: 13; 8: 10.
- 28: 1 (`_STBR(36,28)` in 10_01_CIEL10_6 #331).

883 statements are flagged in all. The single mode-28 statement sits in an `_IFNX` branch (its
`_IFP4` twin at #327 uses `_STBR(36,1)`). Were it executed, word 28 would select effect 28: addalpha,
strength 1, state 1. 37 statements carry a 3rd argument, which the handler ignores: `(1, 1, 11)` 9x,
`(1, 1, 28)` 8x, `(.., .., 0)` 9x and 11 others.

### 5.3 Screen-effect quads 0xD2BE0 [E]

0xD2BE0 is the textured-quad draw of the executor, not a separate screen-effect system.
It is called for every type-1 record (0xD9F87) and by the nested-list cases 0xDA4FB / 0xDAA36 /
0xDAE74 / 0xDB372. It allocates a 0x700-byte copy of the surface template *0x20850B8 from the pool
0x1DBD088 (12 MiB, 0xD4F04). It converts the record's four vertices (uv as is, RGBA8 x 1/255,
position x projection scale + offset as in section 6, z += 0x2D3FF8 per draw) and applies the
effect fields above. The dword passed to 0xF0830 is the
blend-state index (+0x518). The draw is queued through vfunc +0x68 (0xF0DC0).

---

## 6. Display lists [E, EMU]

These are the records that matter for compositing (record = 0x70 bytes; +1 filter tag, +2 type, +4 a,
+6 b, +8 payload, +0x68 next):

| type | builder | executor | meaning |
|---:|---|---|---|
| 1 | 0xDD6D0 / 0xDF760 (picture), via 0xEB780 | 0xD9F00 | textured quad: a = texture id; payload 4 x (u f32, v f32, rgba8, x f32, y f32), stride 0x18 (0xD2BE0 reads uv at +0, colour at +8, position at +0xC) |
| 2 | 0xD9D90(word) | 0xD9F91 | draw-mode word (5.1) |
| 6 | 0xDCCC0(n), 0xDCD20 | 0xD9FCF | **capture**: copy the current render target into nexl_Capture[b] (b = 15 directly, 0x20850C0 + 0x78; b < count - 1 through the pointer [0x435200] = 0x20850C0); then the projection is reset to the 640 x 448 default |
| 7 | 0xDD1C0 | 0xDA023 -> 0xD27E0 | untextured quad (fills): 4 x (rgba8, x f32, y f32, pad), stride 0x10 |
| 9 | 0xDCEC0(list, ...) | 0xDA04C | draw nested list a |
| 12 | 0xDCBE0(w, h, ox, oy) | 0xD9E52 | projection: virtual screen of w x h units (+0x2C, +0x30), offset (+0x14, +0x18) |

The current list is 0x2009068 (select 0xDC740, push 0xDC760, pop 0xDC790). 0xD9B30 resets all lists
every frame except the persistent ones flagged at 0x1DF8D80. The frame runs lists 1, 0, 10, 11 in
that order (0xDB670). Scene layers and the transition process both append to list 0, in
process-priority order.

**Projection** (type 12, executor 0xD9E52; constants 0x2D4034..0x2D4044) [E, EMU]. A vertex
(x, y) in virtual units lands on the 1920 x 1080 screen at

```
pixel_x = x * 1920 / w + ox
pixel_y = y * 1080 / h - 1080 * oy / 272
```

(the executor stores scale 1920/w, 1080/h and offsets ox - 960, -1080 oy/272 - 540 in
0xDB9458..0xDB9464 for the centred clip space; 0xD2BE0 applies them). The default is 640 x 448
(scale 3.0 / 2.4107, the values a type-6 record restores). 0xD9DD0(sx, sy) is a separate picture
scale (0xDC7BA4/A8) that 0xDD6D0 applies to draws with flag 0x10. 0xDCB10(n) sets the filter tag
byte 0x1FC645C copied into record +1.

**Capture** (type 6) [E]. The capture object's vfunc +0x30 (0xF24C0, nexl_Capture vtable
0x42D500) asks the device (0x2085188 vfunc +0x148) for the current render target, takes its full
size as source rectangle and queues itself in the draw queue 0x2085178 (vfunc +0x10). DisplaySurface
draws use the same queue (0xF0DC0), so the copy happens at that point of the frame. When the queue
runs it, vfunc +0x18 (0xF2670) draws the target's texture into the capture surface and restores
the target. **It does not change the render target**: the draws after it still go to the same
target.

**Render-target images** [E, EMU]. 0xC4C50 (from the display init at 0xD20ED) creates image
records 1..16 as 1920 x 1080 images (0xD7A70(n, 0x901, 0x780, 0x438)). Records 1..15 get a
texture id, stored in 0x1FC6B90[n - 1]. 0xC4DF0 then recreates record 0x10 with 0xD7C90, which
stores its texture id in 0xDC7B9C, and creates record 0x17 (960 x 544). When the executor binds a
texture (0xD7E70):

- id == [0xDC7B9C] binds nexl_Capture[15] (0x20850C0 + 0x78);
- id == 0x1FC6B90[k] binds nexl_Capture[k] (k = 0..14).

So **image record n (1..15) shows nexl_Capture[n - 1], and image record 0x10 shows
nexl_Capture[15]**. The capture surfaces are the 16 1920 x 1080 `nexl_Capture` objects created by
task_game (engine.md 4.1).

### 6.1 Executing a list: the reference executor and the GPU executor [V]

`compose.execute_list(ops, textures, width, height, export="rtz16", target=None, captures=None)` runs
an **aligned** display list: every quad is an axis-aligned rectangle at integer pixels whose texels
map 1:1 onto the pixels (nearest sampling, no scaling), which the reference can model exactly. It
returns the target (uint8, rows top-down) and the capture surfaces. Capture surfaces persist across
frames, as `nexl_Capture` does (a `_FADS` start captures in one frame and the later frames read it):
pass the dict of an earlier call as `captures` to continue from it. A surface never captured reads as
zeros, as on the GPU, which creates each surface cleared to 0. Each quad:

1. is skipped when its vertex alpha is 0 (surface flag k = 4, 0xF0AE0, section 3; applied to fills
   too [H]);
2. selects its shader as SelectShader does (`compose.select_shader`): untextured -> basic 2D (PS #3);
   mode 0 with transition parameters on the texture -> transition 2DTex (PS #0); mode 0, 3 or > 25
   -> basic 2DTex (PS #2); else the blend shader of the mode, whose backdrop D is the target before
   the quad;
3. takes P2 from the blend state actually used (0xF147C tests surface +0x518), so a list may carry a
   state other than the word's;
4. is drawn only inside its optional `clip` rectangle (the scissor of `DrawCmd.Clip`);
5. goes through the export model (2.6), the blend state (4.1) and the UNORM8 store before the next
   quad reads the target.

Ops are JSON-compatible: `clear`, `load` (test setup), `quad` (`tex`, or None for a fill; `dst`,
`src`, `color` bytes, `word`, optional `state`, `transition` = (lo, hi, inv) and `clip` = [x, y, w,
h]) and `capture` (record type 6). `"capture:n"` names capture surface n (0..15) as a texture.
Without `state`, compose takes the blend state from the word (5.1). The GPU tests pass `state` only
where a list sets a state on purpose, so the C# builders' word decoding is checked by the comparison
itself.
`python -m tsuki.compose run <folder>` executes `<folder>/lists.json` and writes `ref.rgba`; the GPU
executor's tests write that folder.

The GPU executor (`src/Tsuki.Render/Compositor/`, ARCHITECTURE 6.3) runs the engine contract's
`DisplayList`s the same way:

- `DrawCmd.EffectWord` selects shader, strength and P2, and `DrawCmd.BlendState` the blend state;
- `TextureKind.Capture` reads a capture surface, and record type 6 copies the target;
- before every blend-shader quad it copies the quad's bounds of the target into a backdrop texture
  (the full-target copy gives the identical frame);
- transition parameters live on the texture, as on the PS4 (texture +0xF0..+0xFC, set every frame by
  0xD73B0), so the texture cache holds them per `TextureRef`;
- `FadsCommands` builds the commands of 7.2 and 7.4. The start is the effect-56 fill, capture 0 and,
  for the CAPTURE15 types, capture 15. Each frame is the rule mask (58) and the capture through the
  target alpha (26), or the cross-fade with alpha trunc(255 p).

---

## 7. Transitions: `_FADS(type, ms, rule [, mode])`

### 7.1 Command [E; D]

- The handler (0x9B06D) calls 0x81520. It reads type (arg 0), ms (arg 1), rule (arg 2) and arg 3
  (default -1).
- Message window [E; meaning H]: it sets the word 0x164BA6E, which `_ZM` 0x92380, `_MSAD` 0x92B60
  and the wait code read, when
  - arg 3 == 1, or
  - arg 3 is absent, the config byte 0x66C578+0xA5 == 0, ms >= 2 and the float 0x1653E24 == 0, or
  - arg 3 == 2, ms >= 2, the config byte == 1 and the float == 0.
- Rules 49..52 (`rule - 0x31` <= 3) play UI sound 0x19, unless in fast-forward (word 0x120A6D2) or
  0x17DB232 is set.
- `rule > 0` and no fast-forward: 0x6B460(rule - 1) loads the rule picture (7.3). Otherwise the
  slot is left as it is (0x81687..0x81697), so `_FADS(2, ms, 0)` and a fast-forwarded `_FADS(2, ms,
  n)` reuse the rule already in the slot (7.3).
- 0x7DD10(type, ms) commits the layers and calls 0x42480(type, ms). That sets type 0xDF0750, ms
  0x781154, request 0xDF0754 = 1.
- Finally it copies scene_state to 0x1651AC8.
- Script census: type 1 (immediate) 20,853, type 0 (cross-fade) 12,240, type 2 (rule wipe) 947. Rule
  ids 1..49 occur except 46; the most frequent are 26 (147), 27 (141), 19 (84), 17 (76), 28 (60).
  Rule 0 occurs twice with type 2. Other types do not occur in scripts. Types up to 70 exist for the
  engine's own screens (0x67820 jump table 0x209C50).

### 7.2 Timing [E]

- On the first frame of the request, 0x57980 (calls at 0x58AA5 / 0x58BB8) runs **0x6B910(type,
  ms)**, which captures the screen [E + EMU, `dlprobe_capture.py`]:
  - **list 4**: effect 56 (blend state 11: alpha := max(sa, da)), colour (1, 1, 1, 1), projection
    10 x 10, a full-screen fill 0xDD1C0(0, 0, 10, 10). This sets the render-target alpha to 1 and
    leaves rgb alone. Then effect 0 and the default 640 x 448 projection.
    - Before that, if one of the floats 0xE20CE0 / 0xE20CE4 (setters 0x674E0 / 0x674F0) is below
      1.0 and bit 1 of 0xE20CC4 is set, it also draws the scene layers into list 4 (0x56DE0, with
      0xE20CD4 = 1 and the shake offsets 0xDF0720/24 set to 0 meanwhile) [E; meaning H: a zoomed
      scene is redrawn unzoomed before the capture].
  - **list 1** (runs first in the frame): `[9: draw list 4] [2: effect 0]
    [6: capture -> nexl_Capture[0]]` (0xDCCC0(1)).
  - For the types of jump table 0x209FF0 (index type - 2), i.e. **2, 12..24, 29..32, 46, 49, 52, 53
    and 70**, also `[6: capture -> nexl_Capture[15]]` (0xDCD20) and 0xE20CDC = 1. 0xE20CDC is
    decremented at the end of the transition (0x68B07, 0x69610). Types 0 and 1 (index -2 / -1,
    `ja`) and the other types get only the nexl_Capture[0] copy.
  - Both copies are taken from the render target as list 1 finds it, before list 0 draws the new
    scene: the frame as it was left by the previous frame, with its alpha forced to 1.
    nexl_Capture[0] and nexl_Capture[15] therefore hold the same picture.
  - stores `0xE23770 = type` and sets progress `0xE20CD0 = 1.0`.
  - sets `step 0xE20D64 = -1 / (ms / 16.666)` (constant 0x209ADC), or -1 for ms <= 0. Types 12 and 49
    add 100 ms first.
- Each update (0x68A70 at 0x69578..0x695DB):

  ```
  progress += speed * min(3, frames per update) * step
  if progress <= 0: progress = 0, step = 0
  if progress >= 1: progress = 1, step = -1
  ```

  speed 0x7A6180 is 1 normally (the fast-forward multiplier).
- At 60 Hz a transition of ms milliseconds therefore lasts ms / 16.666 frames. 1000 ms is
  60 frames, and the 61st update reaches 0 (`compose.fads_step`, `fads_progress`). The draw runs
  while progress > 0.

### 7.3 The rule picture and the mask-source conflict [E, V]

- `_FADS(2, ms, n)` takes its rule picture from 0x6B460(n - 1):
  - it caches n - 1 in 0x7A67E0;
  - it computes `id = find("mask_top.mzp") + 1` (string at VA 0x20A11A, cached in 0x7A67E4);
  - it loads NAM id `id + (n - 1)` into **texture slot 0x4DD** (0xEB190).
  - A negative argument frees the slot (0xEB540). An argument equal to the cached 0x7A67E0 returns
    at once.
- **The slot is shared and keeps its rule** [E] (`compose.RuleSlot`):
  - `_FADS` loads only for rule > 0 outside fast-forward (7.1). `_FADS(2, ms, 0)` (2 statements
    [D]) and fast-forward leave the slot alone, and the rule wipe then uses the rule already loaded.
  - `_STTR(layer, n [, x])` (handler 0x99947, 111 statements [D]) stores `(x << 12) | (n - 1)`
    (0xFFFF for n <= 0) in scene_state +0x9B8 + 2*layer. It then calls 0x6B460(n - 1) at 0x99A26,
    so it loads rule n into the same slot, or frees the slot for n <= 0. The layer side of `_STTR`
    belongs to layer_state_and_motions.
  - 0x6B460(-1) frees the slot in the script-end cleanup 0x7F1A0 (`_END_`, `_RTM_`, `_RTMN` and
    the title code 0x6E1DD; tail call at 0x7F3FC) and at 0x79490, 0x7CB90, 0x9B4F4 and 0xA34A0
    [E; their contexts H].
  - Engine screens load rules 26 and 27 directly (0x1E3CE, 0x1E43E).
  - The cross-fade fallback of type 2 (7.4) happens only when the slot is empty or 0x7A67E0 < 0,
    i.e. no rule was loaded since the last free.
- In allpac3, MASK_TOP.MZP is entry 85 and RULE_01..RULE_49 are entries 86..134, so the rule is
  RULE_nn. The test checks this for all 49.
- The 33-name table at VA 0x869B30 (file 0x5CFAD0) holds DEFAULT, MASK001..MASK016,
  MASK020..MASK022, MASK070, MASK088..MASK099 in 32-byte records. **It is unused.**
  - Its only reader is 0xA3530, a case-insensitive search that returns the index or -1.
  - 0xA3530 has no caller, no relocation pointer and no rel32 reference anywhere in .text. The
    numpy scan in the test finds 0 hits for 0xA3530 and exactly 1 for 0x869B30, the `lea` inside
    0xA3530.

So script.md 6.4 is right. engine.md 4.4 (searched by 0xA3530) and archives_ui_movies.md 5.4
("MASK001..MASK091 at file 0x5CFAF0") describe this dead table; section 12 has the errata.

### 7.4 Per-frame drawing (process 0xC, draw callback 0x67820, priority 0x32) [E + EMU]

```
type = 0xE23770; p = progress 0xE20CD0 (skip if p <= 0); 0x45A30 (adds [2:0][12]);
switch type - 1 (table 0x209C50; type 0 and > 70 take the default 0x67A30):
type 1 (and 49): nothing (0x6837D)                     -> the new scene is shown at once
type 2 (0x67958): tex = slot 0x4DD (0xEB740); if (tex | 0x7A67E0) < 0: fallback 0x682D6 (below)
    0xD73B0(tex, a = 256 - trunc(p * 256.0), b = 256): on every sub-texture of the rule picture
        0xFB090(lo = max(-1, a/128 - b/256), hi = min(2, a/128), inv = 0), texture +0xFC = 1
    tag 1; effect 52; colour (1, 1, 1, 1); projection 960 x 544; picture scale (0.5, 0.50370)
    0xEB780(0x4DD, prio 0x10, word 0x3A)   -> effect 58 (state 4) replaces 52
         rule quad, full screen (the RULE pictures are 1920 x 1080), transition 2DTex:
         RT.alpha := f = sat(|0 - smoothstep(lo, hi, rule.r)|)
    0xC45F0(0xD24D0(0)) -> effect 26 (state 6)
    0xDD6D0(image record 0x10, flags 0x10, shake offset) -> nexl_Capture[15], full screen:
         rgb = old * f + new * (1 - f)
  fallback 0x682D6 (empty rule slot): tag 0, projection 960 x 544, effect 0, colour (1, 1, 1, p),
    scale (0.5, 0.50370), then the same 0xDD6D0(0x10, ...) -> nexl_Capture[15]:
         rgb = old * w + new * (1 - w),  w = trunc(255 p) / 255
type 0 and the other default types (0x67A30): tag 0, effect 0, colour (1, 1, 1, p),
    projection 480 x 272, picture scale (0.25, 0.25185),
    0xDD6D0(image record 1, flags 0x10, shake offset) -> nexl_Capture[0], full screen:
         rgb = old * w + new * (1 - w),  w = trunc(255 p) / 255     (cross-fade)
types 18, 20..24, 68..70: own cases (0x67A7C, 0x67AF5, 0x67895, 0x67CB2, 0x67CC4, 0x67E90):
    zoom / slide / second rule variants, not used by scripts, not decoded
```

- **Which capture.** The cross-fade (type 0) draws image record 1 = nexl_Capture[0]. The rule wipe
  and its fallback (type 2) draw image record 0x10 = nexl_Capture[15]. Both captures were copied
  from the same render target at the start of the transition (7.2), so for a port they are one
  "old frame" picture. Engine types 12..17, 19, 29..32, 46, 52 and 53 take the default path too
  and so draw nexl_Capture[0], even though 0x6B910 also filled nexl_Capture[15] for them
  (`compose.fads_capture`).
- **Geometry.** Both capture quads cover exactly 0..1920 x 0..1080 with uv 0..1 (a 1920 x 1080
  image times 0.25 in a 480-unit screen, or times 0.5 in a 960-unit screen). The two paths differ
  only in their units.
- **Shake offset.** Unless the screen-effect mode 0xDF07B4 (0x439C0) is 1, the quad is moved by
  the screen-effect offset (0xDF0720, 0xDF0724) through 0x44DC0 (type 0) or 0x44DE0 (type 2). The
  offset is in virtual units: 4 px per unit for type 0 and 2 px per unit for type 2 [E].
- **Weight quantisation.** The vertex colour is stored as bytes: 0xD9D20 computes
  trunc(max(0, a) * 255.0) in float32, and 0xD2BE0 multiplies the bytes by 1/255 (0x2D4260). So
  the cross-fade weight is trunc(255 p) / 255 (`compose.fads_vertex_alpha`; 127 / 255 at p = 0.5).
  The capture's alpha is 1 (the effect-56 fill), so blend state 1 mixes with w alone.

Emulated records [EMU] (`scratch/misc_compositing_transitions_shaders/dlprobe_capture.py`, log
`dlprobe_capture.log`). The probe runs the engine's own init 0xD4D80, 0xC4C50 and 0xC4DF0 (the
engine heap 0xE3CB0 is replaced by the emulator heap), makes image record 0x20 the rule picture of
slot 0x4DD, runs 0x6B910(type, 1000), resets the lists and runs 0x67820 at p = 0.5. It maps the
texture ids back through 0xD7E70's rule:

| case | list 1 | list 0 (transition draw) |
|---|---|---|
| type 0 | `[9:4][2:0][6:cap0]` | `[2:0][12 480x272]` quad tex 0 = image record 1 -> nexl_Capture[0], rgba (255, 255, 255, 127), pixels 0..1920 x 0..1080 |
| type 2, rule loaded | `[9:4][2:0][6:cap0][6:cap15]` | `[2:52][12 960x544][2:58]` rule quad (image record 0x20, tag 1, alpha 255), `[2:26]` quad tex 15 = image record 0x10 -> nexl_Capture[15], alpha 255, full screen |
| type 2, empty slot | same as above | `[12 960x544][2:0]` quad image record 0x10 -> nexl_Capture[15], alpha 127, full screen |
| type 12 (engine) | `[9:4][2:0][6:cap0][6:cap15]` | as type 0: image record 1 -> nexl_Capture[0] |

List 4 is `[2:56][12 10x10]` fill rgba (255, 255, 255, 255) over 0..1920 x 0..1080, then `[2:0]` and
`[12 640x448]`. 0xE20CDC is 0 after type 0 and 1 after types 2 and 12. The earlier probe
`dlprobe_fads.py` prepared only image record 0x10, so the type-0 capture quad never reached its
list. The record dump in `dlprobe.py` also labelled the vertex fields in the wrong order (fixed); the
layout in section 6 is the corrected one.

The rule parameters give a soft, full-range dissolve. For remaining progress p:
- lo = 1 - 2p rounded to 1/128;
- hi = lo + 1;
- the old frame's weight at a pixel with rule value r is `f = smoothstep(lo, lo + 1, r)`.

| p (remaining) | lo, hi | f(r = 0) | f(r = 0.5) | f(r = 1) |
|---|---|---|---|---|
| 1.0 (start) | -1, 0 | 1 | 1 | 1 |
| 0.75 | -0.5, 0.5 | 0.5 | 1 | 1 |
| 0.5 | 0, 1 | 0 | 0.5 | 1 |
| 0.25 | 0.5, 1.5 | 0 | 0 | 0.5 |
| 0 (end) | 1, 2 | 0 | 0 | 0 |

Dark rule pixels switch to the new picture first. `compose.rule_params`, `rule_factor`, `rule_wipe`,
`crossfade`, `fads_vertex_alpha`, `fads_capture`, `RuleSlot` and `transition_frame` implement this.

Port recipe: at the first frame of a transition, before drawing anything, copy the frame as it
was left by the previous frame, with alpha set to 1 (this is what list 1 does with the
render target). Keep the copy for the whole transition. Each frame, draw the new scene, then the
copy with the quantised weight (type 0, or type 2 with an empty rule slot), or the rule pass and
the copy (type 2). That the render target still holds the previous frame when list 1 runs (no
clear before it) is read from the list order, not from the GPU-side frame setup [H].

---

## 8. `_FADE(kind [, a, b])` (0x97973 -> 0x7F7E0) [E; D]

`_FADE` is a screen-effect command, not a layer fade. It dispatches on kind 0..41 (table 0x20BE5C).
Its time argument becomes frames as `arg * 60 / 1000`.

- Colour fades: kinds 3, 4, 5 call 0x425C0(r, g, b, frames), with kind 4 taking the colour from
  the arguments. Kinds 11..14 call 0x425C0 with a fixed count: 11 white, 12 red (5), 13 white,
  14 red (9). Kind 10 calls 0x428E0(frames).
- Kinds 0, 1, 2, 7 select effect 0x433F0(n).
- Kinds 15..19 set the effect mode word 0x164BA68 and call 0x42DB0.
- Kinds 30..35 call 0x44AB0 / 0x44B40 / 0x44BB0 / 0x44C20 / 0x44C80.
- **Kind 40** sets mode 4 (0x164BA68 = 4), angle 0x164BA90 = 127 + a and preset 0x164BA92 = b,
  then calls 0x42AB0(4, first). This stores the angle as a float (0xDF0794) and selects one of
  16 speed presets (table 0x1A7860).
- **Kind 41** sets mode 0 and calls 0x42DB0(5), which stops the effect.
- In fast-forward (word 0x120A6D2) only kinds 15, 16, 40 and 41 run (bit set 0x30000018000);
  the rest return at once.

Scripts use only 40 and 41 (61 statements): `_FADE(41)` 33 times; `_FADE(40)` 10 times; and
`_FADE(40, angle, n)` 18 times with angles 0, -20, -80, -90, -220, -250 and presets 1..16. Drawing
of effect 4 (the 0x42xxx screen-effect process) was not traced. It is an open question for the
layers / screen-effects work.

---

## 9. Colour effects: `_STEC`, `_SNX2`, `_SNX3` (CPU, at texture build) [E + EMU-756]

These are not shaders. They are applied to the decoded picture's pixels on the CPU each time its
texture is built.

1. Script side:
   - `_STEC(layer, id [, 0xRRGGBB])` (0x9991B -> 0x10390): id 0..81 is stored in scene_state
     +0xCC + layer. Id 1 takes `strtol(arg 2, 0)` as colour; all other ids use white. Ids > 81 and
     layers > 39 are ignored (`_STEC(…, 82)` occurs twice).
   - `_SNX2(layer, v)` (0x98289 -> 0x10610): i16 brightness at +0xF4 + 2*layer.
   - `_SNX3(layer, v)` (0x9986C -> 0x106E0): i8 contrast at +0x144 + layer.
   - The commit 0x49FB0 copies the three into render layer +0x1F / +0x20 / +0x22. The colour is
     stored as a colour tween (0xF7A0: R at +5, G at +6, B at +7).
2. Load side: the picture loaders reset the globals to (id 0, brightness 0, contrast 0, colour
   white) and then set them from the render layer before loading:
   - setters: 0xD31F0 id, 0xD3200 brightness, 0xD3210 contrast, 0xD3220(R, G, B) colour (taken
     from the colour tween through 0xF8B0);
   - **0x4BB50** (called from 0xA3870) sets all four. If the global colour-mode word 0xDE2658 is
     2 it forces id 2 (grey); if it is 3 it forces id 3 (sepia).
   - **0x4B3C0** (called from 0x86362) sets only the id when the id byte is >= 0, so brightness,
     contrast and colour stay neutral. It sets all four only when the byte is negative. With
     mode word 2 it forces id 2.
   - The mode word 0xDE2658 is scene_state +2 (u16), committed by 0x7DD10 through 0x49C30. Which
     script command writes it was not traced.
   - 0xD8B80 copies the globals into the image record (0x1DBD460 + 0x58*i): +0x4A id,
     +0x4C brightness, +0x4E contrast, +0x4F R, +0x50 G, +0x51 B.
   - Which loader runs when (picture change at `_STCH` versus parameter change at the commit) is
     the layers document's [H].
3. Texture build 0xD6900 calls **0xD36F0(pixels, count, id, bright, contrast, R, G, B)** on the
   tile's pixels. The pixels are in byte order B, G, R, A (two call sites swap bytes 0 and 2 of a
   copy first). Alpha is never changed.

Algorithm (bit-exact, `stec_apply_bgra`):

```
if 1 <= id <= 81: k = id-1; flags = i32 0x2D5A78[k]; T0/T1/T2 = 256-byte LUTs through the relocated
                  pointers 0x42C590 / 0x42C308 / 0x42C080 [k] (bytes B, G, R); wB = 0x2D5D00[k],
                  wR = 0x2D5BBC[k]
else:             flags = 0 (no recolour)
LUT1 = clamp(i + bright, 0, 255)                         (identity if bright == 0)
LUT2 = contrast c (signed byte):
        c > 0: 0 for i < c; (i - c) * 255 // (255 - 2c) for c <= i < 255 - c; 255 above
        c < 0: (trunc(i * (255 + 2c) / 255) - c) & 0xFF
mode = flags | (bright != 0 ? 0x10 : 0) | (c != 0 ? 1 : 0)
base (mode & ~0xFF):
  0x100: (B, G, R) = (T0[B], T1[G], T2[R])                          per-channel grade
  0x200: Y = trunc((B*wB + G*548 + R*wR + 382.5f) / 765.0f)         float32
         (B, G, R) = (T0[Y], T1[Y], T2[Y])                          tone from luma
  0x400: (B, G, R) = (colour B, G, R)                               solid fill
  0x000: no base step
then, per channel: if mode & 0x10: x = LUT1[x]; if mode & 1: x = LUT2[x]
(any other mode value: no change)
```

Table contents (`engine_tables.json` has all 81 entries):
- **id 1**: solid colour, a silhouette (3,626 uses; colours 0x000000 954x, 0xFFFFFF 410x, 356 distinct).
- **2**: grey (luma -> identity LUT; 1,338).
- **3**: sepia tone (luma -> B darker, R brighter; 502).
- **4**: negative (81).
- **5 / 6 / 7**: luma into the B / G / R channel only.
- **8**: dark tone, luma into 0..50.
- **9..76**: per-channel colour grades: night, evening, and similar looks. Ids 14, 33, 38, 41, 46, 47,
  51, 52, 54, 58, 60 and 62 are identity (183 uses).
- **77..81**: repeat 2, 3, 5, 6, 7 with the luma weights the right way round (B 56, R 161 = BT.709
  0.0722 / 0.2126). Ids 2..7 use B 161, R 56, i.e. the weights swapped (id 8 has them right).

Census: 12,900 `_STEC` statements over 83 distinct ids [D]. `_SNX2`: 1,678 statements, values
-255..255 (32 125x, 255 109x, -32 95x, -255 94x). `_SNX3`: 845 statements, values -32..127 (32 106x,
64 95x, 40 91x).

Verification [EMU]: `stec_emu.py` ran the engine function 0xD36F0 (with VEX ops executed natively)
on 3,072 pixels: grey and single-channel ramps plus 2,048 random colours. It covered ids 0..83 x 9
(brightness, contrast) pairs, including clamping extremes (-200 / 90, 300 / 127, 0 / -128). That is
756 cases with **0 mismatches** against `stec_apply_bgra`. 336 of them are frozen as SHA-1 digests
in `stec_golden.json` for the test.

---

## 10. Sepia shaders are unreachable in the shipped game [E]

The sepia shaders (#6..#9) are selected only when surface flag k = 5 is set and +0x5C4 > 0 (section
3). The only setter of that flag (0xF05D0), the only setter of +0x5C4 (0xF0660) and of the sepia
Cb/Cr (0xF0630) are called only from 0xED440. That function builds three test surfaces with a mask
and the sepia constants (-0.1, 0.1, 1.0), and nothing references it. The transition-2D path through
0xF06B0 is reachable only from there as well. The sepia look of the scenes comes from `_STEC` 3 / 78
(section 9). `compose.ps_sepia_*` are kept for completeness; `samples/compose/sepia_shader_img1685.png`
shows them with the ctor defaults (Cb -0.1, Cr 0.1, blend 1).

---

## 11. Verification and acceptance

| check | how | result |
|---|---|---|
| ISA vs formula, 35 PS | `test_compose.py` (4,096 lanes x 4 parameter sets, coordinate-dependent procedural textures) + `scratch/compose/verify_ps.py` (20,000 lanes) | max 9.7e-4 < 1/255 [ISA]; slot / interpolant binding checked, mutations fail (2.5) |
| decoder census | `test_gcn.py` | 67 blobs, 1,910 instructions (PS 1,403 / VS 507), no unknown encoding; PS: 58 image_sample, 35 exp; VS: 119 exp |
| engine tables | `test_compose.py::test_engine_tables_match_compose` | blend states, effect tables, STBR table, flag set, sepia clamps equal compose.py |
| `_STBR` flag | interpreting PS #21/#23/#40/#41/#28/#30 with P.z = 0, 0.5, 1 | only 5, 7, 9 depend on P.z; 10, 15, 17 do not (5.2) |
| `_STBR` -> word | `probe_words.py`, `probe_words2.py` [EMU] | 17 -> 0x11, `(…,17,,1)` -> 0x111, 23 -> 0x17, none -> 0, opacity 128 -> 0.502 |
| transition records | `dlprobe_fads.py` [EMU] | rule wipe `[2:52][2:58]..[2:26]`; step -0.016666 for 1000 ms (its type-0 run missed the capture quad, see 7.4) |
| capture surfaces | `dlprobe_capture.py` [EMU] + `test_misc_..::test_transition_capture_paths` | type 0 draws image record 1 = nexl_Capture[0] (alpha 127 at p = 0.5); type 2 draws image record 0x10 = nexl_Capture[15]; list 1 captures into 0 always and into 15 for the 23 types of table 0x209FF0 |
| rule source | `test_misc_..::test_mask_name_table_is_dead_and_rules_come_from_mask_top` | RULE_nn = MASK_TOP + n for 49 rules; 0xA3530 has 0 references |
| STEC / SNX | `stec_emu.py` [EMU] + golden test | 756 / 756 bit-exact |
| export model | `rtz16` of the formulas vs the ISA export, on the `test_compose.py` inputs (2.6) | 99.98 % of 2,293,760 channels bit-exact (10.22 % without) |
| GPU executor, synthetic | `tests/Tsuki.Tests.Render/Compositor/ComposeDifferentialTests` (510 aligned lists, 256 x 256, vs `execute_list` with `rtz16`; 8 with a clip, 2 read a never-captured surface) | max 1 level in every list; 98.92 % of pixels exact (6.1) |
| GPU export | `ExportModelTests` (65,536 values into RGBA32F and RGBA8, both GLSL dialects) | RGBA32F 65,536 / 65,536 equal `export_mrt0`; without the shader's export 50,128 differ (2.6) |
| C# tables | `OracleTableTests` (`DrawEffect.Decode` for 98,304 words, `ForRule` / cross-fade byte on 9,116 p, `Fads.Step` 6,424 values, `_FADS` capture tables for types -2..72) | 0 mismatches against compose |
| GPU executor, aligned frame | `AlignedFrameTests` (13 layers and the RULE_29 wipe, 1920 x 1080) | 99.970 % within 2 levels (99.556 % with `f32`); bounds blit = full blit |
| tests | `pytest tools/tests/test_compose.py test_gcn.py test_misc_compositing_transitions_shaders.py` | 21 tests in about 1 s; without the game 12 pass, 9 skip |

Renders [V], `python -m tsuki.misc_compositing_transitions_shaders render`, files in
`samples/compose/`. Pictures are the catalog's logical pictures placed by their anchor (script.md
4.5).

- `rule01_wipe_{25,50,75}.png`, `rule01_wipe_sheet.png`: RULE_01 (noise brighter towards the
  bottom) from IMG1685 (moonlit room) to IMG1692 (bright room with red cracks).
  - At 25 % done, the top third is speckled with the new room and the rest is the old one.
  - At 50 % the frame is an even noisy mix, new at the top and old at the bottom.
  - At 75 % almost all of it is new, with old specks left near the floor.
  - There are no seams; the transition is a soft dissolve along the rule gradient, as section 7.4
    predicts.
- `crossfade_50.png`: the two rooms superimposed at about half strength (weight 127/255 since the
  repair; re-rendered and viewed: the moonlit room's curtains and window and the bright room's red
  cracks are both visible, neither dominates).
- `stbr17_ciel13_7b.png` / `stbr17_sheet.png`: 13_01_02_CIEL13_7B statements 1481..1486. IMG2182
  (black) is overlaid by IMG1519 with `_STBR(25,15)` (diff5, giving flat grey), then IMG0795 (rain
  streaks) at opacity 144 with `_STBR(26,17)`. With mode 17 the streaks are brighter and higher in
  contrast than with a normal alpha-over, and the grey is lifted only where streaks are.
  - `stbr17_demo_img0795_on_img1685.png` applies the same layer to the night room: streaks light up
    the lit window and floor, while the black frame stays black (dodge keeps d < 3/255 at 0).
- `stbr11_arc05_5.png` / `stbr11_sheet.png`: 05_00_ARC05_5 statement 3498. IMG2162 (warm white
  glow) and IMG2230 (cyan glow) with `_STBR(5,11)`. The additive result has a saturated white core
  and a cyan rim, where a normal over shows only the cyan glow.
- `sepia_stec_sheet.png`: IMG1685 in five versions:
  - the sepia shader: brown, with black lifted to dark red, which is the formula's 1.402*Cr offset;
  - `_STEC` 78 and 3: brownish sepia; on this bluish picture 3 is slightly brighter, because its
    blue is weighted 161 instead of 56;
  - 2: neutral grey;
  - 4: negative.

---

## 12. Errata for other documents

### Errata for engine.md

- **4.1 and 4.4, surface fields.** The fields are not threshold, progress and 1 - progress.
  - +0x5C4 = sepia blend, a gate for the sepia shaders.
  - +0x60C = effect strength (0 / 1).
  - +0x5FC = 1 - strength; +0x600 = strength.
  - +0x604 = P2 = 0 only for shader modes 5, 7, 9 and 10 when the blend state is 0 (the flagged
    `_STBR` case). The bit mask 0x6A0 at 0xF147C tests blend-shader modes, not transition kinds.
  - The transition parameters live on the texture (+0xF0 lo, +0xF4 hi, +0xF8 inv, +0xFC valid,
    0xFB090). SelectShader copies them to +0x5D0 (sections 3, 7.4).
- **4.3, "+0x2D0 = transition (rule) mask texture".** +0x2D0 is the bound texture itself when it
  carries transition parameters (0xEF960 at 0xEFB0F). The gate bit tested at 0xF0EB2 is the
  "textured" flag (k = 2), and bit 4 is the sepia flag. Proof: section 3.
- **4.3, "The dword is passed to 0xF0830 (meaning open)".** It is the blend-state index (+0x518),
  used by 0x105160 to pick the CB_BLEND0_CONTROL record of table 0x2D8030 (section 4.1).
- **4.3, "STBR -> +0x608 is [H]".** Ordinary layers emit word = flag | mode, so +0x608 = mode
  (3 -> 0), strength 0, state 1 [EMU].
  - The single mode-28 use is in an `_IFNX` (Switch) branch; on PS4 its twin uses mode 1.
  - The 3rd argument (e.g. the 8 statements `_STBR(1,1,28)`) is not read by handler 0x830D0.
- **4.3, "Recommended port formulas (standard Photoshop) [H]" and "PS_Diff5 / PS_Dodge5 unknown".**
  Replace them with section 2.3 [ISA]. The differences from Photoshop are:
  - the "nexl" modes 1..10 mix with the layer opacity themselves;
  - ps_add and ps_hardlight use the vertex colour rgb;
  - soft light is `d^(1/(2s))` / `d^(2(1-s))`;
  - dodge keeps near-black at 0;
  - the "5" variants pre-multiply S by opacity × alpha.
- **4.2, "transition 2DTex ... 1 sample of the layer image texture".** The texture it samples is the
  rule picture. Its output is vertex colour × mask factor and is used to write the mask into the
  render-target alpha (7.4). `transition 2D` and the sepia shaders are reachable only from the dead
  test routine 0xED440 (section 10).
- **4.4, "a 33-name table ... is searched case-insensitively by 0xA3530".** True, but 0xA3530 is
  never called, so the table is unused. Rule pictures are `find("mask_top.mzp") + n` (section 7.3).
- **4.4, "_FADE may [yield]; main users of the transition path".** `_FADE` drives screen effects
  (section 8); only `_FADS` uses the transition process.
- **4.1, "16 `nexl_Capture` surfaces ... used for screen captures (transitions from the previous
  picture, ...) [H for the uses]".** The transition use is confirmed and placed [E + EMU]:
  - image records 1..15 show nexl_Capture[0..14] and image record 0x10 shows nexl_Capture[15]
    (0xC4C50, 0xC4DF0, 0xD7E70; section 6);
  - display-list record type 6 copies the current render target into nexl_Capture[b]; it does not
    switch the render target;
  - every transition start copies the frame into nexl_Capture[0], and 23 types (table 0x209FF0)
    into nexl_Capture[15] as well. The cross-fade draws [0], the rule wipe [15] (7.2, 7.4).

### Errata for script.md

- **Command table, `_STBR`.** It is "(layer, mode [, _, 1]) Blend / brightness mode 0..28 [H]". The
  correct reading:
  - mode 0..25 selects the blend shader of section 2.3 (5.2). Mode 28 occurs once, in a Switch-only
    `_IFNX` branch; it would select effect 28;
  - arg 2 is ignored;
  - arg 3 == 1 sets a per-layer flag that turns blending off for modes 5, 7, 9, 10, 15 and 17 and
    makes 5, 7 and 9 use the picture alpha (k = alpha a). For 10, 15 and 17 it has no visible
    effect (5.2);
  - the flag is never cleared by `_STBR`; `_STCH`, `_STCC` and the scene reset clear it (5.2).
- **`_STEC` and `_SNX2` / `_SNX3`** ("H meaning", "Per-layer parameters [H]"). They are the CPU
  recolour of section 9: colour-effect id, brightness offset and contrast.
- **`_FADE`** "(layer, [deg, n]) Layer fade effect [H]" is a screen-effect command (kind, angle,
  preset). Scripts use kind 40 (start effect 4) and 41 (stop) (section 8).
- **`_FADS`** is confirmed: type 1 / 0 / 2 and the rule = MASK_TOP + n mapping. Additions:
  - the rule is not loaded in fast-forward, and `_FADS(2, ms, 0)` reuses the rule left in texture
    slot 0x4DD by the last `_FADS` or `_STTR` (7.3);
  - arg 3 controls the message-window flag 0x164BA6E (7.1);
  - duration = ms / 16.666 frames (7.2);
  - the cross-fade draws nexl_Capture[0], the rule wipe nexl_Capture[15]; both are copies of the
    frame at the transition start (7.2, 7.4).
- **`_STTR`** (layer, rule [, x]) also loads rule `rule` into the `_FADS` rule slot 0x4DD, or frees
  it for rule <= 0 (0x99A26, 7.3).

### Errata for archives_ui_movies.md

- **5.4 and open question 12.** The text says "MASK001..MASK091 name list in data (file 0x5CFAF0)
  [H: unused]". In fact:
  - the table starts at VA 0x869B30 (file 0x5CFAD0) with `DEFAULT`;
  - it holds 32 MASK names that are not contiguous: 001..016, 020..022, 070, 088..099;
  - MASK001 is the record at file 0x5CFAF0;
  - it is unused, because its only reader 0xA3530 has no references (section 7.3, tested).

### Errata for FORMATS.md

- **1.2.** `formats/compositing.md` is now delivered (this document), with tests `test_compose.py`,
  `test_gcn.py` and `test_misc_compositing_transitions_shaders.py`.
- **7 P0-3.** The formulas are no longer candidates. The blend modes are ISA-derived and the
  transition math is traced. What remains open is listed in section 14.

---

## 13. Python API

```python
from tsuki import compose as cp
cp.ps_blend(mode, S, D, C, P)          # blend shader output (2.3), arrays (..., 4) float32
cp.apply_blend_state(src, dst, state)  # CB blend (4.1)
cp.effect(word) -> Effect              # word -> shader mode, strength, blend state, P2 (5.1)
cp.layer_word(mode, flag)              # ordinary layer (5.2); cp.masked_layer_word for pass 2
cp.draw(dst, S, C, word); cp.draw_layer(dst, S, opacity, mode, flag)
cp.rule_params(p); cp.rule_factor(rule_r, p); cp.rule_wipe(old, new, rule, p)
cp.crossfade(old, new, p); cp.transition_frame(kind, old, new, p, rule)
cp.fads_vertex_alpha(p)                # trunc(255 p) / 255, the cross-fade weight on the GPU (7.4)
cp.fads_capture(kind)                  # nexl_Capture index the draw reads (0, 15) or None (7.4)
cp.RuleSlot()                          # texture slot 0x4DD: .fads(n, fast_forward), .sttr(n), .free()
cp.fads_step(ms); cp.fads_progress(ms, frames)
cp.isa_reference(blob, names, textures, interp, cbuffers)   # run a PS blob in tsuki.gcn;
                                       # textures may be callables f(u, v) of the sample coords
cp.rtz16(x); cp.export_mrt0(x)        # fp16 round-toward-zero MRT0 export (2.6); cp.EXPORTS
cp.draw(dst, S, C, word, export="rtz16")
cp.select_shader(textured, mode, has_transition); cp.shade(dst, S, C, word, state, transition)
cp.execute_list(ops, textures, w, h, export="rtz16", target=None, captures=None) -> (target u8, captures)   # 6.1
cp.levels_error(gpu_u8, ref_u8)        # max / exact / <= 1 / <= 2 levels (the T5 (b) metric)
# CLI: python -m tsuki.compose run <folder>   (lists.json -> ref.rgba)

from tsuki import misc_compositing_transitions_shaders as M
t = M.read_stec_tables(eboot)          # 81 colour effects
M.stec_apply_rgba(rgba_u8, id, bright, contrast, (R, G, B), t)
M.read_engine_tables(eboot)            # tables of sections 4, 5, 7.3; capture15_types, transition_draw
M.rule_name(n)                         # "RULE_nn.MZP"
# CLI: python -m tsuki.misc_compositing_transitions_shaders {tables,render}
```

---

## 14. Open questions

1. **Backdrop D.** It is not settled whether the blend shaders read the bound render target itself
   (after the flush 0xF53D0) or a copy. Either way D = the target before the quad, because quads do
   not overlap themselves [H].
2. **Old-frame capture: frame start.** The surfaces and the copy are now traced and emulated
   (7.2, 7.4). What is not traced is the GPU-side frame setup before list 1, i.e. that the
   render target still holds the previous frame when list 1 copies it (no clear first). It follows
   from the design (otherwise every transition would start from black) [H]. Also unknown is
   what the floats 0xE20CE0 / 0xE20CE4 are that make 0x6B910 redraw the layers into list 4 before
   the copy (a scene zoom?) [H]. Whether the message window is part of the copy depends on which
   list it is drawn in (boot_title_choice_flow / textbox).
3. **Masked layers.** It is open which layers take the two-pass masked path: render-layer +0x1C0,
   kinds 1 and 3, +0x2B8 cell sprites. The pass-1 effects (55/57/58) and pass-2 words are listed in
   5.2. A lead: `_STTR(layer, rule, x)` stores a per-layer rule in scene_state +0x9B8 and loads the
   rule picture into slot 0x4DD (7.3). The rest belongs to layer_state_and_motions.
4. **Draw order.** Draw order between layers (the 7th `_STCH` argument looks like a z / priority),
   and the order of the transition process relative to the message window and HUD processes in list
   0, belong to layer_state_and_motions and boot_title_choice_flow.
5. **`_FADE` effect 4.** It is open what screen effect 4 draws (angle, 16 presets, rand-based helper
   effects 1..3).
6. **Flag 0x164BA6E.** The meaning of the word set by `_FADS` arg 3 and the config byte
   0x66C578+0xA5 for the message window belongs to adv_system_state / textbox.
7. **Fast-forward.** In fast-forward the rule is not loaded and speed 0x7A6180 may exceed 1. The
   resulting look of a skipped transition was not rendered.
8. **Engine transition types.** Types 18, 20..24 and 68..70 have their own draw cases (zoom,
   slide and second-rule variants); they are used by engine screens, not scripts, and were not
   decoded. The other engine types take the cross-fade path (7.4).
9. **Blender precision.** The rounding of the PS4 colour block on RGBA8 targets is not known. The
   AMD GL blender rounds about like float with round-to-nearest, which leaves 1-level ties (2.6).
   This needs PS4 captures (`docs/status/ps4_captures.md`). The cull of fills with vertex alpha 0
   (6.1) is [H].

---

## 15. Changes after review

The review of 2026-10-25 found one major and five minor issues. All six were confirmed and fixed;
none of them was a reviewer error.

| issue | was | now | proof |
|---|---|---|---|
| major: type-0 capture | 7.4 / open question 2 said type 0 draws "the captured frame" = image record 0x10 | type 0 draws image record 1 = nexl_Capture[0] (projection 480 x 272, scale 0.25); only type 2 draws record 0x10 = nexl_Capture[15]; type 6 records copy the target and do not switch it; 0x6B910 adds the copy into [15] for 23 types only (7.2, 7.4, section 6) | `dlprobe_capture.py` [EMU] with the engine's own image-record init; `test_transition_capture_paths` (jump table 0x209FF0, code at 0x6BA6B, 0x6813D, 0x6836E, 0xC4C54.., 0xD7E9C) |
| minor: exp count | "PS blobs use exp 154 times" | PS 35 exp, VS 119, all 154 (2.1) | recount with `gcn.decode`; `test_gcn.py` asserts per-stage counts |
| minor: dead assertion | `x == b[:0] + x` in `test_transition_constants_in_code` | asserts the real bytes `66 41 89 44 5f 54` (0x831A4) and `42 80 8c 3b a4 00 00 00 80` (0x831C9), and that 0x830D0 has a single +0xA4 store | test file |
| minor: ISA test blind to coordinates | `isa_reference` ignored the sample coordinates | textures may be callables of the coordinates; the test uses procedural textures; mutations (D/M at texcoord, S <-> D) fail 27 and 29 of 35 shaders (2.5) | `ps_isa_errors(..., swap_*)` |
| minor: `_STBR` flag overstated | flag "uses the picture alpha for 5, 7, 9, 10, 15, 17" | turns blending off for all six, changes the colour only for 5, 7, 9; inert for 10, 15, 17 (5.2, script.md erratum) | P.z sweep of PS #21/#23/#40/#41/#28/#30 |
| minor: flag lifetime / snapshot | silent on clearing; snapshot "0x7F410" | handler only sets; cleared by `_STCH` / `_STCC` / scene reset, copied by `_SCH2 src`; snapshot inside the handler 0x830FD..0x83137 (5.2) | stores at 0x82686, 0x82F23, 0x78CDA, 0x823DB, 0x826FB, 0x82FCA, 0x78B8B, 0x81B1C |
| minor: type 2 without a rule | "falls back to a cross-fade" | the fallback needs an empty slot; `_FADS(2, ms, 0)` and fast-forward reuse the slot, which `_STTR` also loads (7.1, 7.3; `compose.RuleSlot`) | 0x81687, 0x6B460, 0x99A26, 0x7F3FC, 0x67962 |

Found while fixing (not in the review):

- the cross-fade weight is quantised to trunc(255 p) / 255 (`compose.fads_vertex_alpha`, now
  used by `crossfade`; `crossfade_50.png` re-rendered);
- the display-list vertex layout is (u, v, rgba8, x, y) for type 1 and (rgba8, x, y) for type 7,
  not (x, y, rgba8, u, v);
- the capture quads move with the screen-effect offset 0xDF0720/24 (7.4);
- `read_engine_tables` now returns `capture15_types` and `transition_draw`
  (`engine_tables.json` regenerated).
