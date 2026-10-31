# Quake, shake, swing, message quake and screen shock (and the RNG behind them)

Game: TSUKIHIME -A piece of blue glass moon- (PS4 CUSA47999, app 01.01). Engine: HuneX
library in `eboot.bin`; `rand()` comes from the game's own `sce_module/libc.prx`. Every
address is a text / data VA of this build (text VA = file offset - 0x2280; data VA 0x600000 =
file 0x365FA0); libc addresses are libc.prx VAs.

Reference implementation: `tools/tsuki/quake.py` (pure Python, float32-exact). Tests:
`tools/tests/test_quake.py` (synthetic + real data + a live engine cross-check, ~10 s).
Research scripts and logs: `scratch/quake/` (`verify_emu.py` / `.log`, `worked_0000.py` / `.log`,
`commit_check*.py` / `.log`, `census.py` / `.json`, disassembly dumps `f*.txt`).

This document answers FORMATS.md 7 P1 item 6 (the per-frame formula at VA 0x58206..0x58827,
the MSQK / SHOK screen quakes, and the RNG). Storage and argument decoding were already in
layers.md 4.4; this document repeats them where the formulas need them and corrects them
(section 12).

Confidence tags: **[V-emu]** compared bit for bit with the engine's own code run under
unicorn (section 10); **[V-all]** checked on all retail data; **[E]** read in the
disassembly, not separately executed; **[H]** hypothesis.

---

## 1. Overview

```
_SQK2 / _SHK2 / _SSW2 --0x84670--> drawn record A (VA 0xDDB5E8 + 0x2C8*l) +0x178.. slot 0 / 1   [3]
                                   + scene_state mirror (kind, delay, time, params)
per-frame update 0x57980: for l in 0..39: ... motion ... slot 0, slot 1 (0x57F4D..0x5889A)       [4]
        kind 1 quake: new random (x, y) every p2 frames            2 rand() per new offset
        kind 2 shake: sine of period p2 frames                     0 rand() (1 with the flicker flag)
        kind 3 swing: x and y sines, random amplitude / period     2..3 rand() per axis per period
layer draw 0x51060 (VA 0x51A22..0x51B71): position += slot0 offset (parent / mask rule) + slot1   [5]

_MSQK --0x53BE0--> 0xDF0894..; update at the top of 0x57980: 2 rand() per frame -> HUD 0x53CF0   [6]
_SHOK --0x438C0--> screen effect kind 1 (0xDF07B4..); update 0x439D0: damped sine, no rand()
        -> every scene layer (VA 0x5182B) except the 7 colour fills; not the HUD                 [7]

rand(): libc 64-bit LCG, RAND_MAX 2^30-1, one global state; srand(minute*256 + second) at every
title screen init (0xAFE80)                                                                      [2]
```

* All arithmetic is float32 (SSE scalar / packed ops, one rounding per operation). The model
  rounds after every operation and reproduces the engine bit for bit (section 10).
* Offsets are translations in **canvas units** (640 x 362.667 scene canvas; the HUD uses the
  640 x 448 UI canvas). None of these effects rotates or zooms a layer.
* Because the RNG is seeded from the wall clock and shared with other systems (2.4), a PS4
  run cannot be reproduced exactly; the port reproduces the *distribution and timing* exactly
  and the exact values for a given seed and call sequence.

## 2. The random number generator

### 2.1 `rand()` / `srand()` (libc.prx) [V-emu, E]

The game ships its own `sce_module/libc.prx` (a plaintext SELF; NIDs `cpCOXWMgha0` = rand at
libc VA 0x1C290, 38 bytes; `VPbJwTCgME0` = srand at 0x1C310, 13 bytes). The state is one
global u64 at libc data 0x123280, initial value 1 (not per thread).

```
rand():                                     libc 0x1C290
    state = state * 0x5851F42D4C957F2D + 1          (mod 2^64)
    return (state >> 32) & 0x3FFFFFFF               RAND_MAX = 2^30 - 1 = 0x3FFFFFFF
srand(seed):                                libc 0x1C310
    state = (uint32) seed                           (zero-extended)
```

The engine's constant 2^-30 (VA 0x1A72A4, 0x1A7770) turns a rand() value into [0, 1).
First values from state 1: 408024109, 11635919, 196474438, 117649705.

### 2.2 Seeding [E]

| VA | when | seed |
|---|---|---|
| 0xD212E (boot init 0xD1F40) | once at boot | `srand(byte [rbp-0x37])`: the two clock calls before it (0xC2D90 returns 1, 0xC2D80 is `ret`) never fill the buffer, so the seed is an uninitialised stack byte (0..255, unspecified) |
| 0xAFFB1 (title init 0xAFE80, system_flow mode 0) | every title-screen start | `sceRtcGetCurrentClockLocalTime` via 0xEAD50, then `srand(movbe16(bytes minute, second))` = **minute * 256 + second** of the local time (`tsuki.quake.title_seed`) |

So every playthrough begins from one of 60 x 60 seeds chosen by the clock at the title
screen (e.g. 12:34 -> 0x0C22). There is no other srand call.

### 2.3 Helpers built on rand() [V-emu]

* **0xC2F90 `rnd_range(a, b)`** (MSQK, other screen effects): `hi = max(a, b)`, `lo = min(a, b)`;
  `n = trunc((hi - lo) * 1048576) + 1`; `r = rand() % n` (signed 32-bit idiv);
  result `lo + r * 2^-20`. One rand() call.
* **0xE8200 `rnd2()`** (SHK2 flicker; also the title update 0xB06E8): its own state, u64 at
  data 0xDC7BF0, initial 0x6F37 in the image:
  `s = ((s * 0xF131 + 0x11E79431) mod 2^64) >> 1` (logical); returns `(i64)(int)rand() + s`
  (mod 2^64). One rand() call.

### 2.4 Other rand() consumers (why exact PS4 sequences cannot be matched) [E]

145 call sites in 25 functions share the one state. Within the layer update 0x57980 the
order is: MSQK (2 calls), then layers 0..39 x slot 0, slot 1 (section 4), then 0xA8AD0 (an
overlay effect at A + 0x7022, 3 sites, only while its state byte is non-zero). Outside it:
text layout 0xC6C20 (6 sites) and glyph draw 0xC8F50 (4 sites; text display animations),
transitions 0x42AB0 (72 sites; rule / noise setup), `_SEPL` random SE 0x3CFF0, title
particles 0xB2C60 / 0xB3160, 0x6B510, 0x6C810, 0xA8510, 0xA86A0, 0xD8450, 0xD8A50 and others
(`scratch/quake/randsites.py`). Since the seed is the clock (2.2) and text drawing draws from
the same state, a port cannot (and need not) match a particular PS4 run; it matches the
engine exactly for a given state and call order.

### 2.5 `sinf` (libc.prx, libc VA 0x4AF10) [V-emu]

The shake code calls the game's own `sinf` (NID `Q4rRL34CEeE`, 787 bytes). It is FreeBSD msun's `s_sinf.c` / `k_sinf.c` / `k_cosf.c` /
`e_rem_pio2f.c` with double-precision kernels; `tsuki.quake.ps4_sinf` ports it, constants read from libc.prx data 0xD9290..0xD9360. It
differs from a correctly rounded float32 sine by one ulp for some arguments, so neither `math.sin` nor `MathF.Sin` is bit-exact:

| range of the argument | result |
|---|---|
| `\|x\| < 2^-12` | `x` |
| `\|x\| <= pi/4` | `(float) ksin(x)` |
| `<= 3 pi/4` | `+-(float) kcos(x -+ pi/2)` |
| `<= 5 pi/4` | `(float) ksin(+-pi - x)` |
| `<= 7 pi/4` | `-+(float) kcos(x -+ 3 pi/2)` |
| `<= 9 pi/4` | `(float) ksin(x -+ 2 pi)` |
| above | `__ieee754_rem_pio2f` (medium path: `fn = (x * 2/pi + 1.5 * 2^52) - 1.5 * 2^52`, `y = (x - fn pi/2_1) - fn pi/2_1t`), then ksin / kcos by `n & 3`; arguments above 4.2e8 rad use `__kernel_rem_pio2` (not reachable from a shake) |

`ksin(x) = (x + s (S1 + z S2)) + (s w)(S3 + z S4)` with `z = x x`, `w = z z`, `s = z x`; `kcos(x) = ((1 + z C0) + w C1) + (z w)(C2 + z C3)`
(all in double). Check (`scratch/quake/sinf_check.py`, log `sinf_check.log`, engine = the libc code under unicorn, 11.4 M VEX operations in the
large-argument path): 92,162 retail SHK2 / SSW2 / SHOK arguments, 199,169 random float32 bit patterns, 50,000 uniform in +-1e5 and 46 edge
values: `ps4_sinf` 0 mismatches, `f32(math.sin)` 78. The first version of this document assumed a correctly rounded sine (old 4.6, 13.1);
that is replaced by this section.

## 3. Layer slots: storage, start, stop

### 3.1 Fields (drawn record A; per layer l at 0xDDB5E8 + 0x2C8 l; slot s = 0 / 1) [V-emu]

| off | type | name | meaning |
|---|---|---|---|
| +0x178 + s | u8 | kind | 0 none, 1 quake (SQK2, slot 0), 2 shake (SHK2, slot 1), 3 swing (SSW2, slot 1); table VA 0x216848 = {0, 1, 1} by kind-1 |
| +0x17A + 2s | i16 | delay | start delay in frames (layers.md calls it "fade"; it is a delay, 4.1) |
| +0x17E + 2s | i16 | time | duration in frames, -1 = until stopped, 0 = never runs |
| +0x182 + 12s | 6 x i16 | p0..p5 | parameters (3.3) |
| +0x19A + 2s | i16 | elapsed | frames since start (after the delay), saturates at 30000 |
| +0x19E + 2s | i16 | phase | per-kind phase counter |
| +0x1A4 + 4s | f32 | x | output offset, canvas units |
| +0x1AC + 4s | f32 | y | output offset, canvas units |
| +0x1B4 + 14s | 7 x i16 | w0..w6 | swing runtime: w0 / w1 amplitude x / y, w2 / w3 period x / y, w4 / w5 sign x / y, w6 phase y |

scene_state mirrors kind (0x10C0 + 2l + s), delay (0x1110 + 4l + 2s), time (0x11B0 + 4l + 2s)
and the parameters (0x1250 + 24l + 12s); the update writes the delay countdown and the
clearing at the end into the mirror as well. Runtime fields exist only in the drawn record.

### 3.2 Start 0x84670(l, p[6], dur_ms, delay_ms, kind) [V-emu]

```
if l not in 0..39 or kind not in 1..3: return
s = slot of kind
kind[s] = kind;  delay = trunc(3 * delay_ms / 50) (i16);  time = dur_ms >= 0 ? trunc(3 * dur_ms / 50) : -1
p0..p5 = low 16 bits of the six ints;  elapsed = phase = 0;  x = y = 0;  w0..w6 = 0
(scene mirror gets kind, delay, time, params; the handler then sets word 0x164B998 = 1)
```

`frames(ms) = trunc(3 * ms / 50)` (C division toward zero; 600 ms -> 36, 1000 -> 60, 16 -> 0).

### 3.3 Argument decoding (object parser; "absent" = missing or empty) [V-emu]

| cmd (handler) | layer | parameters p0..p5 | dur / delay |
|---|---|---|---|
| `_SQK2` (0xB6B20) | a0, absent 0; > 39 ignored; < 0 = targets (3.4) | p0 = a1 (15), p1 = a2 (15), p2 = interval: a3 absent 2, a3 < 0: 1, 3 a3 < 50: 2, else frames(a3) + 1; p3 = frames(a7) (a7 absent 0) = ramp-in; p4 = (dur >= 0 and a7 <= 16) ? frames(dur) : 0 = ramp-out; p5 = a6 (0) = follow flag | a4 (-1) / a5 (0) |
| `_SHK2` (0xB6FA0) | a0, absent 0; -1..39 (others ignored); -1 = targets | p0 = a1 (0), p1 = a2 (15), p2 = a3 present ? frames(a3) : 18 = period, p3 = a6 (0) = decay flag, p4 = a7 present ? frames(a7) : 0 = ramp-in, p5 = a8 (1) = flicker divisor | a4 (-1) / a5 (0) |
| `_SSW2` (0xB7400) | a0, absent 0; 0..39 only | p0 = a1 (7), p1 = a2 (15) amplitude range, p2 = a3 present ? frames(a3) : 60, p3 = a4 present ? frames(a4) : 120 period range, p4 = a5 (1) gain, p5 = 0 | a6 (-1) / a7 (0) |

Scan mode: if `dur >= 0` and the scan-mode byte 0x174DE48 (0x7B4B0, the re-execution after a
slot load; vm_variables.md) is set, a start does nothing. Endless effects (dur < 0) still
start, so a loaded game shows them.

### 3.4 Layer -1 targets [V-emu]

* `_SQK2(-1, ...)`: layers 0 and 1 if in use (A +0x10 == 1); layers 2..39 if (in use and committed
  z > 0) or scene_state picture (+0x4 + 2l) != -1. `tsuki.quake.sqk2_targets`.
* `_SHK2(-1, ...)`: the same, but layers 0 and 1 also need z > 0. `shk2_targets`.
* "committed z" is the i32 array at VA 0xDE2538 + 4l (A + 0x6F50, copied from B + 0x6F50 by the
  commit, 0x4A180); it is the STCH z argument (index 6).

Example (00_00 statement 215, `_SQK2(-1,1,4,0,600,0,0,0,0)`): targets 1..7 (layer 7 has a
scene picture but is not in use) [V-emu, `worked_0000.log`].

### 3.5 Stops and other commands [V-emu]

* `_SQS2(n)` / `_SHS2(n)` / `_SSS2(n)` (0xB6EF0 / 0xB7350 / 0xB7670 -> 0x847F0(n, kind)): n
  absent = 0, > 39 ignored, < 0 = all 40 layers. Clears every field of the kind's slot
  (outputs included) in A and the scene mirror. `_SHS2` and `_SSS2` clear the same slot 1, so
  either stops a shake or a swing.
* A slot also stops by itself (4.1).
* **`_STCH` and `_SCH2` both** reset the drawn record's slots at the statement itself: the shared backend 0x81760 calls 0x4A770(layer) on
  both paths (VA 0x81A9C for `_STCH`, which first calls 0x49570 for the rotation words; VA 0x81ABB for `_SCH2`), and 0x4A770 zeroes both slots
  (+0x178..+0x1CF; outputs and runtime fields included) together with the other per-record effect state (layers.md 4.1). It happens for every form:
  a new picture, the same picture, a source layer (`src` = another layer or the layer itself) and the empty name. The scene copy is zeroed (without
  a source) or takes the source's slots; a source layer's slots start fresh at the next commit (below). The first version of this document said
  "`_STCH` (not `_SCH2`)", which was wrong [V-emu, review: `scratch/review_quake/r20_sch2.py`, `r21_sch2_when.py`; after the fix
  `scratch/quake/replay_491_rows1100_seed491.log` etc., section 10.1]. Retail has 4,675 `_SCH2`; a conservative count finds 54 of them in 22 scripts that
  hit a layer with an endless shake. The stopped quake no longer calls `rand()`, so every later random value of the run depends on this rule.
* `_STCC(l)` clears the scene copy only; the drawn effect keeps running (the layer usually has
  no picture after the commit, so nothing is seen).
* Commit (`_FADS`) and the B -> A transfer (VA 0x595B2..0x59779, in the first update after the
  commit, after the slot updates): per slot, if B.kind != 0 and B.kind != A.kind, B's slot
  (kind / delay / time / params from the scene, runtime fields as B holds them, normally 0)
  replaces A's and B.kind is cleared; otherwise A continues unchanged. So `_FADS` in the middle
  of a quake does not restart it, and a stale B slot is not resurrected at the end of a
  transition. `ShakeLayers.merge_committed`.

## 4. Per-frame update (0x57F4D..0x5889A inside 0x57980)

### 4.1 Common part [V-emu]

`dt` = frames since the last update (`trunc(arg * frames_per_update)`, VA 0x57A87; 1 at 60 Hz,
up to 3). `d = (float) dt`. The loop runs for **every** layer 0..39 in index order (not z
order, used or not), slot 0 then slot 1:

```
if delay > 0:                       delay = trunc(delay - d); if <= 0: 0;  (mirror too); next slot
if time == 0:                       next slot (outputs keep their values)
elapsed = trunc(elapsed + d);  if (i16) elapsed >= 30000: elapsed = 30000
if time > 0 and time < elapsed:     clear the slot (all fields, A and mirror)  -> kind 0
switch kind: 1 quake, 2 shake, 3 swing, else x = y = 0
```

The whole layer update (motions, tweens, slots, SHOK) is skipped while the system-menu
fraction 0x6F430() (float 0x1202F0C) is >= 1.0 (VA 0x57A4E); MSQK runs before that test.

### 4.2 Kind 1: quake (`_SQK2`) [V-emu]

```
phase = trunc(phase + d)
if p2 > phase: return                                   # keep the current offset
phase = 0
if l >= 2 and p5 != 0:                                  # follow the background (VA 0x5810F)
    (x, y) = (0, 0)
    if A[0].used and z[0] != 0: (x, y) = A[0].slot[s].(x, y)
    if A[1].used and z[1] != 0: (x, y) = A[1].slot[s].(x, y)    # layer 1 wins; used and z as before the B -> A transfer of this update
    return                                              # no rand() call
ax, ay = (float) p0, (float) p1
if p3 > elapsed:              r = elapsed / p3;                         ax *= r; ay *= r   # ramp in
elif time > 0:  t0 = time - p4;  if elapsed > t0:  r = 1 - (elapsed - t0) / p4;  ax *= r; ay *= r   # ramp out
r1 = rand(); r2 = rand()
ux = (float) r1 * 2^-30;  uy = (float) r2 * 2^-30          # (float) rounds the 30-bit value to 24 bits
vx = ax * ux;  vy = ay * uy
x = ((vx + vx) - ax) * (float)(1/3)                         # 0x3EAAAAAB
y = ((vy + vy) - ay) * (float)(362/1080)                    # 0x3EAB9D65
```

So a quake jumps to a new uniform random offset in [-p0, p0) x [-p1, p1) (1920 x 1080 pixel
units) every p2 frames, holding it in between. With `a7 <= 16` and a duration the amplitude
decays linearly to 0 over the whole duration (p4 = time); with `a7 > 16` it ramps up over p3
frames and does not decay. 2 rand() calls per new offset per layer (x first).

### 4.3 Kind 2: shake (`_SHK2`) [V-emu]

```
per = p2 != 0 ? p2 : 60;  m = |per|
e = trunc(phase + d);  phase = e mod m                  # loop at VA 0x581E0 (16-bit compare)
v = sinf(phase * 2pi / per)                             # 2pi = 0x40C90FDB
if p4 > 0 and elapsed < p4:    v *= elapsed / p4         # ramp in
if p3 != 0 and time > 0:       v *= (time - min(time, elapsed)) / time   # linear decay over the duration
if p5 >= 2:                                             # flicker (1 retail statement)
    v = (rnd2() % 5 == 0) ? v * (1 / p5) : 0            # 1 rand() per frame
x = (v * p0) * (1/3);   y = (v * p1) * (362/1080)
```

A smooth sine of period p2 frames (default 18 frames = 300 ms) on both axes in phase, with
amplitudes p0 / p1 (default 0 / 15: vertical). The phase starts at 0 and the first frame
shows `sin(2 pi d / per)`.

### 4.4 Kind 3: swing (`_SSW2`) [V-emu]

```
phase = trunc(phase + d)
if not (w2 >= phase):                                   # x period over (w2 = 0 on the first frame)
    phase = 0
    w0 = trunc(u() * (p1 - p0) + p0)                    # u() = (float) rand() * 2^-30
    w2 = trunc(u() * (p3 - p2) + p2)
    if w4 == 0: w4 = u() < 0.5 ? 1 : -1                 # sign chosen once per start
s1 = sinf(phase * 2pi / w2)
w6 = trunc(w6 + d)
if not (w3 >= w6):                                      # y period over
    w6 = 0
    w1 = trunc(u() * (p1 - p0) + p0)
    w3 = trunc(u() * (p3 - p2) + p2)
    if w5 == 0: w5 = u() < 0.5 ? 1 : -1
x = (((w4 * w0) * p4) * s1) * (1/3)
y = (sinf(w6 * 2pi / w3) * ((w5 * w1) * p4)) * (362/1080)
```

Each axis runs whole sine periods; at the end of a period it draws a new amplitude in
[p0, p1) and a new period in [p2, p3) frames (defaults 7..15 px, 60..120 frames). The first
frame draws 6 values (x amplitude, x period, x sign, y amplitude, y period, y sign); after
that 2 per axis per period. The two axes drift independently. Despite the name a swing does
not rotate the layer.

### 4.5 rand() calls per update (shake code only) [V-emu]

```
calls = 2 x (active MSQK)                                              # before the layer loop
      + sum over layers 0..39 in order, slot 0 then 1:
          quake: 2 when its phase reaches p2 and it does not follow layer 0/1
          shake: 1 if p5 >= 2 (rnd2)
          swing: 2 per axis whose period ended (+1 for each sign still 0, i.e. the first frame)
```

00_00's `_SQK2(-1,1,4,0,600,...)` on 7 layers: 14 calls every second frame, 252 in total; the
decaying amplitude reaches 0 at frame 36 (its last 14 calls produce zero offsets) and the slot
clears at frame 37 (elapsed 37 > time 36) [V-emu, `worked_0000.json`].

### 4.6 Edge cases [V-emu]

* A swing whose period range contains 0 (`frames(a3) <= 0 < frames(a4)`, or both 0) divides
  0 / 0: x / y become NaN; the engine draws with them (a NaN vertex position; the layer is
  presumably not visible [H]). No retail statement does this (the only `_SSW2` with a zero
  range, `_SSW2(9,0,0,0,0,0,0,0)`, has duration 0 and never runs) [V-all]. The model returns NaN;
  NaN payloads are not compared (the emulator's sinf stub and Python differ in NaN sign).
* time == 0 (duration 0; 13 retail `_SQK2`): the slot stays in place with kind != 0 and zero
  output until stopped.
* `sinf` is libc.prx's (NID Q4rRL34CEeE, libc VA 0x4AF10), ported bit-exactly as `ps4_sinf` (2.5). A correctly rounded sine
  differs from it by one ulp for about 0.04 % of the arguments, invisible after the canvas-to-pixel scaling.

## 5. Draw: how the offsets reach the screen (0x51060) [V-emu]

The layer draw computes the layer's screen position (layers.md 7) and then adds, in canvas
units, as a pure translation (after scale, zoom and rotation):

```
pos += shock(x, y)            if byte 0xDF07C8 == 0 and not (catalog draw_flags & 2)   (VA 0x51808..0x51833)
pos += off0 + off1            (VA 0x51A22..0x51B71)
off0 = 0 if kind[0] == 0, else A[l].slot0.(x, y), and if kind[0] == 1:
         if parent >= 0: walk p = parent, A[p].parent, ...: every ancestor with kind[0] != 0
                         replaces off0 by its slot-0 offset (the outermost quaking ancestor wins)
         elif mask >= 0 and A[mask].kind[0] != 0: off0 = A[mask].slot0.(x, y)
off1 = A[l].slot1.(x, y) if kind[1] != 0 else 0          (no parent rule: kind 1 never sits in slot 1)
```

* A child layer therefore moves with its quaking parent (both must have a quake; `_SQK2(-1)`
  gives one to every used layer), and a masked layer with its mask.
* **Second path for masked layers (the mask-layer term).** A layer whose record has `mask >= 0` (STCH a11, 6,351 retail statements) and no `_SACL`
  flag (+0x1E & 0x40) also adds the mask's own outputs to its *position*: VA 0x516DE..0x51746 calls 0x49A60(A[mask] + 0x10, axis) for x and y and adds
  `(v_x * 640 / 1920, v_y * 360 / 1080)` to the layer position X, Y (so the term also moves the pivot-zoom point P of layers.md 7.2). The helper 0x49A60
  (`ShakeLayers.mask_axis`) returns, in 1920 x 1080 pixel units:

  ```
  src = A[M.mask] if M.mask >= 0 else M                      # VA 0x49A8F: a mask that is itself masked reads its own mask's slots
  sh  = sum of src.slot[s].(x | y) over the slots with kind != 0
  sh  = sh * 3.0            (x)   |   sh * (1080/362 = 0x403EF071)   (y)
  v   = M.crop[axis] + sh                                      # crop origin (crop_ease, f32 0x49400, is 0 in every run)
  if M has a crop rectangle and +0x124 == 1 (offscreen target):  v += shock * 3.0 (x) | shock * (1080/448 = 0x401A4925) (y)   # not for colour fills
  ```

  So a masked layer's total slot offset is its slot-rule offset (above, which already follows the mask's quake) **plus** the mask term: with the
  mask quaking the masked layer moves by (+1.4862, +1.2637) although it has no quake of its own, and with both layers quaking it moves by about
  twice the mask's offset (-1.2319, +0.4002 against -0.6160, +0.2007). The y term uses 360 / 1080 where the slot rule uses 362 / 1080, hence the
  small difference (1.2637 against 1.2707). The first version of this section had only the slot rule and so missed the second term [V-emu, review:
  `scratch/review_quake/r24_mask.py`]. `ShakeLayers.mask_offset` / `mask_axis` / `offset` model it; `+0x124 == 1` holds only while the mask is
  drawn into its offscreen target (0x5AA7F..0x5AAE1), never when the masked layer's position term reads it, so the shock part of the term is
  absent for the position (`offscreen` stays false in the model).

* The 7 colour fills (BLACK BLUE GREEN ORANGE RED WHITE YELLOW, draw_flags 3) ignore the screen
  shock; the other 14,737 pictures have draw_flags 0 [V-all]. Slot offsets apply to all.
* Check: sprite-call position with the shakes minus the same draw with all slot kinds and the
  shock zeroed = model offset + shock offset, within 1e-3 canvas units (float32 rounding of
  position + offset), for every drawn layer after every update of 6 scenarios (`verify_emu.py`
  draw_check, 1,782 layer draws).
* Pixels: x canvas units x 3, y x 2.97794 (layers.md 2). A quake amplitude of 4 is +-4 frame
  pixels (x) or +-3.99 (y).

Correction: the draw does not read the slots through 0xFE50 / 0xF390 (layers.md 4.4). 0xFE50
reads a per-layer i16 x / y offset from the 32-byte table at [0xDD96A8] (+0x0C, valid when
+0x18 != 0) and 0xF390 dispatches on the type word of the record block +0x2A8 (table 0x4000F0)
for four scale / rotation terms (default 0x1A7610); both belong to other effects [E].

## 6. `_MSQK`: message-window quake [V-emu]

`_MSQK(ms = 1000, a1 = 5, a2 = 5)` (object parser; handler VA 0x962D3 -> 0x53BE0(ms, a2, a1)):

```
start:   dur = (float)((uint32) ms < 10001 ? ms : 1000)       0xDF0894 (ms; 0 = off)
         t = 0                                                 0xDF0898
         amp_y = a2 * 448 / 720,  amp_x = a1 * 640 / 1280      0xDF089C, 0xDF08A0 (float32 ops)
         x = y = 0                                             0xDF08A4, 0xDF08A8
update (top of 0x57980, every update, also while the layer update is frozen):
         if dur > 0:
             t += (arg * frames_per_update) * 1000 / 60
             if t >= dur: dur = 0; x = y = 0
             else:        x = rnd_range(-amp_x, amp_x);  y = rnd_range(-amp_y, amp_y)     # 2 rand()
```

A new uniform offset every frame (2^-20 grid), no decay. a1 / a2 are 1280 x 720 pixel units
(x 1.5 on the 1080p frame); the offsets are 640 x 448 HUD canvas units and are added by the
HUD 0x53CF0 to the message window and text positions (VA 0x542D8, 0x54BE8, 0x559A4).
`_MSQK(300,5,5)` is active for 17 frames (18 x 16.666666 reaches 300.0 in float32). The
duplicate update 0x53C30 has no callers. A new `_MSQK` restarts (offsets 0 until the next
update); `_MSQK(0)` stops at once (dur 0, offsets zeroed by the start). `_SHAF` does not touch
MSQK.

## 7. `_SHOK`: screen shock (screen-effect kind 1) [V-emu]

`_SHOK(ax, ay, ms = 300, n = 3, delay = 0)` — inline readers (missing or empty = default; i16).
Handler 0x98C44: nothing while a next-scene skip runs (word 0x120A6D2) or in scan mode (byte
0x174DE48); otherwise 0x7FC80 -> 0x438C0(1, ax, ay, ms, n, delay):

```
start (0x438C0, or later from the delay):
    delay_ms = delay                                           0xDF0818 (always written)
    if delay > 0: remember (1, ax, ay, ms, n) in 0xDF0820..0xDF0850; return
    kind = 1                                                   0xDF07B4
    phase = 0;  amp = 1                                        0xDF07E8, 0xDF07EC
    remaining = trunc(3 * (ms * n) / 50)  (frames; < 0 endless, 0 off)     0xDF07D4
    period = ms * 3 / 50  (float; 1.0 if 0)                    0xDF07D8
    count = n >= 2 ? n : 1                                     0xDF07DC
    AX, AY = ax, ay                                            0xDF07E0, 0xDF07E4
    (byte 0xDF07C8 = 0: layers shake; byte 0xDF07CC = 1: the HUD does not)
update 0x439D0(dt), called from 0x57980 at VA 0x599B8 after the layer loop:
    if delay_ms > 0: delay_ms -= trunc(dt * 1000 / 60) (16 per frame); if <= 0: start the remembered shock
    if remaining == 0: x = y = 0; return
    if kind != 1: (other screen effects)
    if remaining > 0: remaining -= dt; if <= 0: clear everything, x = y = 0, return
    phase += dt * 2pi / period
    amp = max(0, amp - dt / period / count)
    s = sinf(phase)
    x = (((s * AX) * amp) * 640) / 480                         0xDF0720
    y = (((amp * (s * AY)) * 480) / 360                        0xDF0724
```

A sine with `n` periods of `ms` each, decaying linearly to 0, no rand(). Amplitudes are
480 x 272 script units (x 4/3 to canvas units, i.e. about x 4 frame pixels): `_SHOK(0,3,100,10)`
(the commonest form) moves the scene up to 12 px vertically, 6-frame period, 60 frames.
`_SHAF()` (0x80AE0 -> 0x42DC0(0,0,0,0)) sets kind -1 and remaining 0, so the offsets are 0 from
the next update; a pending delayed shock is not cancelled [E]. Offsets go to every scene
layer (section 5); 0x53CF0 (HUD) skips them because byte 0xDF07CC is set. Other references
to 0xDF0720 / 0xDF0724: 0x49A60, 0x57080, 0x57590 (layer init), 0x59C30 and the other effect
kinds in 0x439D0 / 0x44440..0x44E40 (not traced).

## 8. Worked examples (engine values, bit-exact with the model)

`_SQK2(0,3,3,0,1000,0,0,0)` on layer 0, rand state 1 (`test_quake_frames`): p = (3, 3, 2, 0, 60, 0).
Frame 1: phase 1 < 2, offset (0, 0). Frame 2: phase 2 -> rand 408024109, 11635919; ramp-out
r = 1 - 2/60 = 0.96667, amp 2.9; x = (2 * 2.9 * 0.380 - 2.9) / 3 = **-0.2319960594177246**,
y = **-0.9509695172309875**. Frame 6: (0.462343692779541, 0.027990251779556274). 6 calls.

`_SHK2(0,4,6,500,1000)`: p = (4, 6, 30, 0, 0, 1); frame 1 x = sin(2 pi / 30) * 4 / 3 =
0.2772156000137329, y = 0.41813352704048157.

`_SSW2(0,7,15,1000,2000,1,2000)`, state 1: frame 1 draws w = (10, 7, 60, 105, 1, -1); after 6
frames w = (10, 7, 60, 105, 1, -1, 5), (x, y) = (1.6666667461395264, -0.6915830373764038).

00_00 (`worked_0000.log`): `_SQK2(-1,1,4,0,600,0,0,0,0)` with the layers of statements 0..214,
seed `title_seed(12, 34)` = 0xC22: targets 1..7; frame 2 offsets L1 (-0.1345, +0.6399),
L2 (+0.1203, -0.8417), L3 (+0.0538, +1.2383) ... (each layer independent: a6 = 0); 14 rand()
calls every 2 frames; all 7 x 40 frame values identical between engine and model.

`_MSQK(300,5,5)`, state 1: amp (2.5, 3.1111112); frame 1 (1.622039794921875, 1.763542652130127).
`_SHOK(0,3,100,10)`: remaining 60, period 6.0, count 10; frame 1 y = 3.4063665866851807.

## 9. Retail census (from `scratch/quake/census.py`) [V-all]

| command | statements | notes |
|---|---:|---|
| `_SQK2` | 1,236 | 617 all-layer (-1), 619 single layer; 571 endless, 13 duration 0; interval p2 = 2 in 964, 4 in 186; 638 decay over the duration, 49 ramp-in (a7 > 16); 149 follow layer 0/1 (a6 != 0); 104 with a start delay |
| `_SHK2` | 125 | 12 all-layer; 99 endless; 71 vertical only (x amplitude 0); 15 decay; 1 ramp-in; 1 flicker (`_SHK2(3,15,15,100,-1,0,0,0,10)`) |
| `_SSW2` | 200 | 197 endless; 6 with p2 > p3 (180..120 frames, fine); 18 with gain p4 != 1; no NaN case (4.6) |
| `_SQS2` / `_SHS2` / `_SSS2` | 532 / 400 / 398 | 524 / 398 / 398 with -1 |
| `_SHOK` | 870 | 748 vertical only, 54 horizontal only, 66 both; delayed (a4 > 0) about 258 |
| `_MSQK` | 30 | `(300,5,5)` x 14, `(600,5,5)` x 4, the rest single |
| `_SHAF` | 397 | always `()` |

The "2,431 statements" of FORMATS.md 7 are SQK2 + SHK2 + SSW2 + SHOK (1,236 + 125 + 200 + 870).

## 10. Verification (what was run)

* `scratch/quake/verify_emu.py` (unicorn harness `tsuki.misc_layer_state_and_motions_emu.Rig`,
  libc rand installed at the rand PLT stub, real retail pictures for layers 0, 1, 3, 4, 5, 7 with
  z 0 / 10 / 2178 / 1910 / 0 / 30 and `_SBAS(7,3)`): 11 (later 12) fixed scenarios (00_00's statement, the
  commonest retail shapes, flicker, swing with delay, SHOK with delay + SHAF, MSQK) and random
  statement sequences (random arguments including absent, negative and > 16 values, layers
  -2..40, dt 1..3, stops, `_FADS`, `_STCH`, `_SHAF`). After every update all 40 x 2 slots
  (every field, floats bitwise), the MSQK globals (24 bytes), the SHOK globals and the rand
  state are compared, and every rand() return address is checked to come from the shake
  code / 0xC2F90 / 0xE8200. Results in 10.1.
* Draw: sprite-call deltas vs `ShakeLayers.offset` + shock after every update of 6 fixed
  scenarios (1,782 layer draws, section 5).
* `scratch/quake/worked_0000.py`: 00_00 statements 0..215 (layer commands) through the engine;
  targets and 40 frames of 7 layers identical.
* `scratch/quake/commit_check*.py`: `_FADS(1,0,0)` / `_FADS(0,500,0)` / `_FADS(0,2000,0)` during a
  quake, `_STCH` same layer, `_STCH` with a source layer, `_STCC`, `_SQS2` + `_FADS` (3.5).
* libc.prx disassembly of rand / srand (`scratch/quake/libc_rand.py`); the test re-reads the
  bytes and the initial state.
* `tools/tests/test_quake.py`: 15 tests (~10 s), including a live engine run of 96 updates on
  layers 5..9 with five statements, MSQK and SHOK.

### 10.1 Runs

| run (log in scratch/quake/) | updates | slot comparisons | mismatching updates | notes |
|---|---:|---:|---:|---|
| 1: 11 fixed + 150 random (seed 4242) | 5,285 | 422,800 | 178 | every difference was a NaN against a NaN with another payload (swing with a period range through 0); comparison made NaN-aware afterwards |
| 2: 11 fixed + 200 random with `_FADS` / `_STCH` (seed 777, `verify_emu_run2_seed777.log`) | 6,993 | 559,440 | 0 | |
| 3: 11 fixed + 200 random (seed 31337, `verify_emu_run3_seed31337.log`) | 7,491 | 599,280 | 0 | 6,394 engine rand() calls, all from the shake / MSQK code; 4,973 non-zero slot outputs; MSQK active 1,002 updates, SHOK 590; 126 NaN values; 29 commits, 27 STCH resets |
| 4: 12 fixed incl. commit copies (`verify_emu_fixed.log`) | 826 | 66,080 | 0 | 4 slots copied by the B -> A transfer (STCH with a source + `_FADS`, twice); 1,782 draw checks |

Every comparison covers all 40 x 2 slots (all fields), the MSQK block and the SHOK block, and
the rand state after the update.

### 10.2 Runs of the second version (after the review; `scratch/quake/verify_emu.py` v2, `replay_retail.py`)

The v2 harness resets the slots at `_SCH2` like `_STCH`, reads layer used / z before the engine update, has masked layers (a mask without a crop,
a mask with a crop rectangle, `_SACL`), installs `ps4_sinf` at the engine's sinf stub, and checks the draw position deltas of every drawn layer
(masked and cropped ones included) against `ShakeLayers.offset`.

| run (log in scratch/quake/) | updates | slot comparisons | mismatches (updates + draws) |
|---|---:|---:|---:|
| 30 fixed cases (`verify_emu_v2_fixed.log`) | 1,030 | 82,400 | 0 |
| 30 fixed + 120 random, seed 4242 / 777 (`verify_emu_v2_seed*.log`) | 4,140 / 4,137 | 331,200 / 330,960 | 0 / 0 |
| negative controls: `_SCH2` without the reset / draw offset without the mask term / layer state read after the update (`verify_emu_v2_neg_*.log`) | 1,030 each | 82,400 | 232 / 74 / detected (1 update) |
| retail replays through engine and model, script 491 rows 0..1100 (404 updates), 0..2421 (592), 467 rows 0..5378 (1,365), 458 rows 0..2341 (574), 494 rows 0..4591 (963), 79 (286) | 4,184 | all slots | 0 mismatching updates, 0 draw mismatches (1,403 draw checks, 195 masked) |

The review's own scripts, rerun against the v2 harness (2026-10-26, `docs/status/knife/K2.md`): `r20_sch2.py` and `r21_sch2_when.py` (all four `_SCH2`
forms clear slot 0 and 1 and the scene mirror at the statement: model = engine), `r24_mask.py` (six masked-layer cases: engine deltas (+1.4862, +1.2637),
(-1.2319, +0.4002), (+1.9817, +0.9909) ... equal the model); the replays of scripts 467 and 491 reproduce 0 mismatches (they gave 151 of 525 and 15 + 8 draw
mismatches before the fix).

## 11. What the C# runtime must do

1. Implement `Ps4Rand` exactly (u64 LCG, `(state >> 32) & 0x3FFFFFFF`) as **one** generator
   shared by every random consumer (shakes, MSQK, text animations, transitions, SE choice,
   particles). Seed it at each title-screen init with `minute * 256 + second` of local time
   (or a fixed seed in a deterministic test mode). Keep `Rnd2` (state 0x6F37) for SHK2 flicker.
2. Do all shake arithmetic in `float` (System.Single) with one rounding per operation and in
   the documented operation order. C# `float` arithmetic is IEEE single on x64 (RyuJIT SSE, no
   automatic FMA contraction), so `a * b` etc. match. Use the exact libc `sinf` port (`Ps4Math.Sinf`, 2.5), not `MathF.Sin`. Truncation must
   reproduce cvttss2si: NaN or |x| >= 2^31 -> int.MinValue, else toward zero. Do not rely on `(int)f`
   for the NaN / overflow cases (.NET 9 made float -> int conversions saturating: NaN -> 0);
   write a helper (`StageMath.Cvtt`), then wrap to 16 bits with an unchecked `(short)` where the engine stores
   an i16.
3. Per update: MSQK first (2 calls), then for layers 0..39 slot 0 then 1, then the commit
   transfer, then SHOK. Run the slot update for all 40 layers, used or not.
4. Draw: add `shock` (unless the picture is a colour fill or byte 0xDF07C8 is set) and the slot
   offsets (parent / mask rule for slot 0) to the layer position as a translation; add MSQK's
   offsets to the message window and text.
5. Honour the start rules: scan mode suppresses timed starts and SHOK; `_STCH` **and `_SCH2`** stop a layer's
   slots at the statement; `_STCC` does not stop the drawn effect; `_FADS` does not restart a running one.
6. Add the mask-layer term (5) to the position of every masked layer without `_SACL`.

The C# port is `src/Tsuki.Engine/Stage` (`Ps4Random.cs`, `ShakeEngine.cs`, `ScreenEffects.cs`); its proof is in `docs/status/knife/K2.md`.

Wave-2 / 3 owners: the layer runtime (layers / compose port) takes sections 3..5; the HUD /
textbox port takes MSQK (6); the screen-effect / transition port takes SHOK (7); the core
runtime takes the shared RNG (2) and its seeding in the title flow (system_flow mode 0).

## 12. Errata

### Errata for layers.md 4.4

* "fade = frames(fade ms)": the field is a **start delay**: while it is > 0 the slot only
  counts it down (no elapsed time, no output change) [V-emu].
* "The per-frame offsets ... are read by the draw through 0xFE50 (...) and 0xF390 (...)" and
  "shakes can move, zoom and rotate a layer (a swing rotates)": wrong. The draw reads the slot
  outputs directly (VA 0x51A22..0x51B71) and adds them as a translation; no shake rotates or
  zooms. 0xFE50 / 0xF390 serve other effects (section 5) [V-emu draw deltas, E].
* "Layer -1 (SQK2/SHK2): layers 0, 1 if in use, others if in use or with a scene picture":
  others need (in use **and** committed z > 0) or a scene picture; for `_SHK2` layers 0 / 1 also
  need z > 0 (3.4) [V-emu].
* "SHOK (870), MSQK (30) and SHAF (397) act on globals (0xDF0818.., 0xDF0894.. screen quake
  floats, 0xDF07B4)": MSQK's offsets go to the HUD (message window), not the scene; SHOK is
  screen-effect kind 1 (0xDF07B4 = 1) with outputs 0xDF0720 / 0xDF0724; 0xDF0818 is SHOK's start
  delay (sections 6, 7).
* Section 11 "the shake formula is open (13.1)": closed by this document.

### Errata for script.md (command table)

* `SHOK | (layer, a, ms, n [, x]) | Shock effect [H]`: the arguments are **(x amplitude,
  y amplitude, period ms = 300, periods n = 3, delay ms = 0)** in 480 x 272 script units; there
  is no layer argument (section 7).
* `MSQK (O) | (ms, amp, n) | Shake the message window [H]`: confirmed as the message-window
  quake; arguments **(ms = 1000, x amplitude = 5, y amplitude = 5)** in 1280 x 720 pixels
  (section 6).
* `SHK2 ... Layer shake start / stop [H]`, `SSW2 ... Layer swing [H]`: confirmed (sections 4.3,
  4.4); `SHS2` and `SSS2` both stop slot 1.

### Errata for FORMATS.md 7 P1 item 6

Closed: formula, RNG and screen quakes are decoded and verified (this document). Remaining
items are in section 13.

## 13. Open questions

1. Closed: `sinf` is ported exactly (2.5). Remaining: `__kernel_rem_pio2` (arguments above 4.2e8 rad) is replaced by an exact
   reduction in Python and by a correctly rounded sine in C#; no shake reaches it.
2. What the GPU does with a NaN layer position (only reachable with non-retail `_SSW2`
   arguments) [H].
3. The boot seed (uninitialised stack byte, 2.2) only affects rand() consumers before the first
   title screen (boot notices / first-boot path): their exact values are unknowable.
4. Screen-effect kinds other than 1 in 0x439D0 (0xDF0804 cosine shake with 0xC2F90 noise at
   0x43A2C..0x43C11, kinds 2..13 at 0x43E3C..) belong to other commands (`_SHAK` etc., adv_state
   +0x19FC) and are not modelled here.
5. The other readers of 0xDF0720 / 0xDF0724 (0x49A60, 0x57080, 0x57590, 0x59C30) were not
   traced; the scene layers' path (0x51060) is verified.
