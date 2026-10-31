# System flow: boot sequence, notice screens, title screen and choice menu

Scope: what TSUKIHIME -A piece of blue glass moon- (PS4, CUSA47999, app 01.01) shows from power-on
to the first interactive state, how the title screen picks its background and handles its menu,
and how an in-game choice (`_SELR` / `_SELD`) is drawn and answered. Each screen is given as a
state machine plus a *draw list*: (texture slot, archive, child, sub-rectangle, canvas
position, scale, anchor, alpha, blend). From these a port can redraw each screen without the
eboot. This document replaces the `[H order]` boot description of engine.md 8.1 and the
title / choice parts of archives_ui_movies.md 7 (errata in section 9).

Python reference: `tools/tsuki/misc_boot_title_choice_flow.py` (state machines, draw lists,
renderer, eboot table reader). Tests: `tools/tests/test_misc_boot_title_choice_flow.py`.
Samples: `samples/misc_boot_title_choice_flow/`. Scratch (disassembly dumps, call decoder):
`scratch/misc_boot_title_choice_flow/`. `docs/formats/misc_boot_title_choice_flow.md` points
here.

Confidence labels:

- **[E]**: read from the disassembly (text VA = file offset - 0x2280; data VA = file offset -
  0x365FA0 + 0x600000). Constant arguments were recovered with the call decoder
  `scratch/misc_boot_title_choice_flow/calls.py`. It uses the method of archives_ui_movies.md
  7.4, extended to tail-call `jmp`, register copies and callee-saved registers.
- **[V]**: checked on real data: a table re-read and compared in a test, a render that was
  looked at, a census, or a movie frame comparison.
- **[F]**: frame counts from float32 emulation of the engine's fade arithmetic
  (`fade_frames`, `title_fade_frames`).
- **[H]**: hypothesis.

All times are 60 Hz frames (one game step per displayed frame, engine.md 3.3).

---

## 1. Overview

```
main 0xED210 -> task_game 0xEEE50
   boot states 0..4 (system save, jump table 0x2D66A4)                         3.1
   Game_Init 0xE90E0: process 0 = Game_OpenArchives 0xD1F40 (one frame)          3.2
       -> process 0 = boot screens 0xE8280 (state word 0x2057CDC)                 3.3
            [save-damaged notice]  ->  autosave notice  ->  state 0x34
       -> 0xD2330: Game_StartProcesses, process 0 = 0xD23B0 -> system state machine 0x68A70
mode 0 (title, word 0xE20CC8 = 0): init 0xAFE80, update 0xB06B0, draw process 0xB02B0     4
   prologue never seen  -> New Game at once (scene 260 = 00_01, prologue movie)
   G[10] == 0           -> title + PRESS ANY BUTTON -> caution -> caution1 -> New Game (scene 110)
   G[10] != 0           -> TM_CI movie -> caution2 -> title menu
mode 2 (ADV): _SELR / _SELD -> choice menu (setup 0xA5DF0, input 0xA6180, draw 0xA6E10)   5
```

G[n] is the persistent u16 array at data VA 0x164C620 (vm_vars: `G<n>`). Three flags drive
the flow [E, V scripts]:

| flag | meaning | set by |
|---|---|---|
| scene bit 485 (`0xB81A0(0x1E5)`, bit 485 of the words at G + 0xDC) | prologue seen | `_RDST(485)` at the start of `00_01` / `00_02` |
| G[10] (word G + 0x14) | a story was started | `_WKST(G010,eq,1)` at the start of `00_00` (the only writer in 549 scripts) |
| G[2] (word G + 4) | title state 0..5 | title code only (no script writes G002) |

---

## 2. Draw model [E]

| call | VA | arguments |
|---|---|---|
| SetCanvas | 0xDCBE0 | (w, h, x0, y0): one canvas unit = 1920/w x 1080/h output pixels |
| SetScale | 0xD9DD0 | (sx, sy): texel -> canvas unit factor used by draw flag 0x10 |
| SetColor | 0xD9D20 | (a, r, g, b) vertex colour |
| SetBlend | 0xD9D90 | (id): render command 2 with a draw-effect id [H: the engine.md 4.3 effect-id space] |
| SetRot | 0xD9DF0 | (angle): 65536 = one turn (factor 9.5873e-05 = 2*pi/65536 at 0x2D4128) |
| FillRect | 0xDD1C0 | (x, y, w, h, z) in canvas units, current colour |
| TexDraw | 0xEB780 | (slot, flags, blend, x, y): texture slot record 0x2077400 + 0x1C*slot (+8 sprite id, +0xC optional mask sprite) |
| TexDrawRect | 0xEB890 / 0xEB9C0 | (slot, flags, blend, rect*, x, y): sub-rectangle `(x0, y0, x1, y1)` i16 texels |
| Sprite | 0xDD6D0 / 0xDF760 / 0xDEC90 | sprite-id level draws that TexDraw ends in |
| widget draw | 0xCFC90 | (widget, x, y, alpha): UI widget whose frame (slot, flags, rect) comes from its definition (0xCF900) |

Sprite placement (VA 0xDD6D0..0xDD9DB) [E, V renders]:

```
a        = flags & 0xF                 # anchor 0..8: TL TC TR / ML C MR / BL BC BR
(fx, fy) = (0x2D5800[a], 0x2D5830[a])  # {0, .5, 1, 0, .5, 1, 0, .5, 1} / {0, 0, 0, .5, .5, .5, 1, 1, 1}
(sx, sy) = current scale if flags & 0x10 else (1, 1)
(w, h)   = sprite or rectangle size in texels
top_left = (x - trunc(fx*w)*sx, y - trunc(fy*h)*sy)          # canvas units
size     = (w*sx, h*sy)
pixels   = canvas units * (1920/cw, 1080/ch)
flags & 0x20: rotate by the SetRot angle around the anchor
```

The most common pair is canvas 640 x 448 with scale (1/3, 448/1080), or 960 x 544 with (0.5,
544/1080). Both make one texel one output pixel. Blend ids used here: 0 = normal alpha; 0xB
= effect 11 = surface mode 11 `ps_add`; 0x34 = the title moon glow. Id 52 lies outside the
1..51 effect table. The sheet is an opaque black rectangle with a blue glow, so only an
additive or screen mode makes sense there [H]. The renderer draws 0xB and 0x34 as `dst +
src*alpha` [H].

---

## 3. Boot sequence

### 3.1 task_game boot states (jump table 0x2D66A4, loop 0xEF0CF..0xEF17E) [E]

| state | VA | action |
|---:|---|---|
| 0 | 0xEF12A | `SaveSystem_CreateInitial` 0xEA390. It starts a nexl_SaveData operation on 'savedata' / 'bin', 4 MiB, titles 'Title' / 'subtitle' / 'detail' (0x120110). Result 0: leave the loop. Otherwise go to state 1 |
| 1 | 0xEF159 | poll 0xEA490. < 0: stay. == 5: 0xEAF90(5), go to state 3. Otherwise go to state 2 |
| 2 | 0xEF189 | SaveImage_Ptr, leave the loop |
| 3 | 0xEF13A | 0xEAFB0 (= 0x120760 on the save object) != 0: stay. Otherwise `0xE8240(1)` sets the **save-damaged flag** (dword 0x2057CD8), go to state 4 |
| 4 | 0xEF0DA | run `Game_FrameUpdate` every frame while 0xE8250() (the damaged flag) != 0. Process 0 shows the damaged notice and clears the flag |

After the loop: Scheduler_Wait, the trophy context loop (0x12B980 / 0x12BAB0), then the main
loop (engine.md 3.1). The meaning of result 5 is [H: "data corrupted"]. Its visible result,
the notice in 3.3, is [E].

### 3.2 Process 0 chain [E]

`Game_Init` 0xE90E0 (called from 0xEEA00) registers process 0 = **Game_OpenArchives** 0xD1F40.
This one-shot process opens the archive slots (parts.mrg into slot 0x18 at VA 0xD2111) and
the fonts, then re-registers process 0 = **0xE8280** (VA 0xD22ED, after resetting the boot
state with 0xE8260). The boot process ends in state 0x34, which calls **0xD2330**:
Game_StartProcesses, close parts slot 0x18 (0xE5610), and process 0 = 0xD23B0 (calls the
system state machine 0x68A70), process 1 = 0xD2400, process 2 = 0xD2450.

### 3.3 Boot screens: process 0 (VA 0xE8280, state u16 0x2057CDC, jump table 0x2D6060, 0x35 entries) [E]

Variables: fade `A` f32 0x2057CE0, white-background flag `W` u8 0x2057CE4, spinner angle i16
0x2057CEC (+500 per frame), "Saving..." pulse 0xDC8A48 (bounces 0.3..1.0 by +-0xDC8A4C).
Every state ends a fade on the frame where `A >= 1.1` (in, +0.025 per frame) or `A <= -0.1`
(out, -0.025). In float32 that is **45 frames** each way [F]: 44 steps give 1.0999995. The
damaged notice fades +-0.05, which takes 22 frames. Holds use timer 4 (0xD4750 run / 0xD4730
reset / 0xD4720 frames). In a hold state the skip input is checked before the timer:

- skip = game input bits 0x860 in word +0x20 of `*0xDC7BC0`, or pointer flag bit 1 (left
  click newly pressed, word 0x178EC54).
- The game-level bit space and the action names are adv_system_state's subject.

| state(s) | screen | asset (parts.mrg = slot 0x18, loaded by 0xE5680 into sprite 0x1E..) | hold | live |
|---|---|---|---:|---|
| 0x00 | decide | damaged flag set -> load parts **2 + system language** (0x407F0, not the game option), go to 0x31. Clear -> state 0x30: copy the system block (0x18AC bytes), G[] (0x3E8 bytes) and config (0x140) from the save image | - | yes |
| 0x31 -> 0x32 -> 0x33 | save-damaged notice ("Your save file has been corrupted...") | parts 2..6 (ja, en, zc, zt, ko) | 1..480 | yes, only when damaged |
| 0x30 | 0xD1DF0(0, 0xD1D90()) [H: restore the volume of audio channel 0; the dead logo / voice states set v/2 and 2v/3], A = 0 | - | - | yes |
| 0x0F | fade the previous screen out (from A = 0: **4 frames**), then load the autosave set | - | - | yes |
| 0x1C -> 0x1D -> 0x1E | **autosave notice** | parts **8 + lang** (text), 7 (spinner), 13 + lang ("Saving..."), 18 (glow) | 1..240 | yes, every boot |
| 0x34 | free sprites, go to 0xD2330 | - | - | yes |
| 0x06, 0x0A..0x0E | TYPE-MOON logo on white, then the fiction disclaimer | parts **1** (W = 1), parts **19 + lang** | 180 / 140 | **no** |
| 0x10..0x18 | system-voice greeting | voice 0xA5DC0(0x47 or 1, then 0x49 or 0x48, chosen by G[1] & 0xF; config +0x56; 4) | 140, 314 | **no** |

Dead states [E, V test]. No instruction writes 6 or 0x10 into the state word, so the
logo / parts-caution cluster and the voice cluster are unreachable in 01.01. The evidence:

- The immediate stores in 0xE8240..0xE9080 are 0, 0x0A..0x0F, 0x11..0x15, 0x17, 0x18, 0x1C..0x1E
  and 0x30..0x34 (re-read by `read_tables`).
- The two register stores (VA 0xE8D4F) write 0x16 and 0x34.
- The routine 0xE8260 resets the state to 0.

parts.mrg #1 (logo) and #19..23 (disclaimer) are therefore never shown by this build.

Draw list (VA 0xE8DB9..0xE903E) [E, V renders]:

| draw | canvas / scale | slot / sprite | position (units) | pixels |
|---|---|---|---|---|
| white background (only if W) | 480 x 272 | FillRect(0, 0, 640, 480), colour 1 | - | full screen |
| page | 640 x 448, (1/3, 0.41481) | sprite 0x1E, flags 0x10, colour (min(A, 1), 1, 1, 1) | (0, 0) | 1920 x 1080 |
| spinner (states 0x1C..0x1E) | 960 x 540, (0.25, 0.25) | sprite 0x1D (parts 7), flags 0x34 (centre, rotate) | (895, 497.5) | centre (1790, 995), 80 x 80 |
| "Saving..." | same | sprite 0x1F (parts 13 + lang), flags 0x10, alpha A x pulse | (881, 491.5) | (1762, 983), 64 x 28 |
| glow | same | sprite 0x1B (parts 18), blend 0xB | (875, 477.5) | (1750, 955), 80 x 80 |

### 3.4 Ordered boot list (acceptance a) [E; frame counts F]

`boot_sequence()` produces these steps. `samples/misc_boot_title_choice_flow/boot_sequences.tsv`
lists all four cases with VAs.

| # | step | frames | VA | returning player | first boot | after prologue (G[10] = 0) | damaged save |
|---:|---|---|---|---|---|---|---|
| 1 | splash hidden, surfaces, mouse | - | 0xEF0C5..0xEF0CA | yes | yes | yes | yes |
| 2 | system save states 0..4 | - | 0xEF0CF.. | yes | yes | yes | yes, ends in state 4 |
| 3 | Game_OpenArchives | 1 | 0xD1F40 | yes | yes | yes | yes |
| 4 | save-damaged notice (parts 2 + sys lang) | 45..524 | 0xE831C, 0xE8BD0..0xE8CE3 | - | - | - | yes |
| 5 | autosave notice (parts 8 + lang) | 95..334 | 0xE85E1, 0xE8A5B..0xE8BA5 | yes | yes | yes | yes |
| 6 | title init (G[2] rule, titlebg movie, BGM) | 1 | 0xAFE80 | yes | skipped (state 0x0C) | yes | skipped [H] |
| 7 | white cover in | 61 | 0xB0FA1 | yes | - | - | - |
| 8 | **TM_CI.mp4** (movie 38, 6.0 s, no loop, any button skips) | <= 360 | 0xB0FF8..0xB1107 | yes | - | - | - |
| 9 | **caution2_<lang>** (allpac3 CAUTION2[_xx]) | 123..602 | 0xB1131..0xB1253 | yes | - | - | - |
| 10 | title fades in (120 frames), BGM starts | 121 | 0xB1549 | yes | - | yes (PRESS ANY BUTTON, no menu) | - |
| 11 | title menu / PRESS ANY BUTTON | input | 0xB08D8 / 0xB15BA | menu | - | button | - |
| 12 | **caution_<lang>** then **caution1_<lang>** | 424..903, 363..842 | 0xB0D6F..0xB0F97 | - | - | yes | - |
| 13 | New Game | - | 0xB0982 -> 0x690EE | from the menu | at once -> scene 260 (00_01, prologue movie) | white fade 73 frames -> scene 110 (START_SCRIPT -> 00_00) | at once -> 00_01 [H] |

- "Returning player" means bit 485 set and G[10] != 0. "First boot" means no system data, so
  bit 485 is clear.
- A damaged save never copies G[] out of the save image, so it behaves like a first boot
  [H: G starts zeroed].
- The prologue (`00_01`) ends with `_RTMN` (back to the title, BGM kept). The second visit
  then takes the "after prologue" path.
- The TM_CI movie plays only once per power-on: flag 0x1762214, set in title state 0.

Cautions (title states 0x2B..0x31 and 0x36..0x3A):

- A black cover (f32 0x17621E8) rises 0 -> 1 at 1/60 per frame. The caution is loaded into
  texture slot 0x89 (137) by `Nam_Find` + `TexLoadId`. The cover falls 1 -> 0 to reveal it.
- A countdown (i32 0x17621A0) holds it, then the cover rises again.
- First-run cautions: timer 720. Input is ignored while the old value is > 480, so the hold is
  241..720 frames.
- caution2: timer 480, skippable at once, so 1..480 frames.
- Names come from text tables 0x218ED0 / 0x218EE0 / 0x218EF0 (`caution_ja/en/zc/zt.mzp`,
  `caution1[_en|_zc|_zt].mzp`, `caution2[...]`). A language index > 3 takes the `_en` file
  (VA 0xB0DA4).
- Draw: canvas 960 x 544, scale (0.5, 0.50370), `TexDraw(0x89, 0x10, 0, 0, 0)`: full screen
  (VA 0xB02F1..0xB0350).

### 3.5 Names that are not boot screens [E]

- `abmp_logo.mrg` (missing on disk): loaded only by methods of the object created at 0xC0D0.
  The system state machine's mode 6 (0x68CA9) creates it. Mode 6 is reached from title return
  value 34 (0x69280), which no title code returns. It is not part of the boot.
- `BG_TOP.MZP`: only a relro pointer (0x42C070), never read. `bg_top.mzp` (HUD 0x56D06) is a
  NAM search that fails (-1 + 1 = 0).
- parts.mrg #1 (logo) and #19..23 (fiction disclaimer): dead states only (3.3). The disclaimer
  the game shows is allpac3 `CAUTION_xx`.

---

## 4. Title screen

### 4.1 Entry [E]

The system state machine 0x68A70 dispatches on the mode word 0xE20CC8 (jump table 0x209D7C):
0 title, 1 attract, 2 ADV, 4 gallery / extras, 5 (0xA9720), 6 abmp_logo object. Mode 0, sub
0 calls the **title init 0xAFE80**:

1. Apply the G[2] rule (4.2) and write title record 0 (data 0x873670) and the movie index
   (data 0x873650).
2. Load the title table 0x873660 (localised, force = 1).
3. Start `titlebg<N>.mp4` in texture slot 0x87 (135) with loop = 1. The volume is the byte at
   0x17DA273 / 100 [H: movie volume option].
4. Seed rand() from the RTC.
5. Create widgets 0xE4..0xEA (definitions 0x873780) and reset the menu tables.
6. If `0x2A1B0() >= 0` (pending start scene) or bit 485 is clear: title state 0x0C, title not
   drawn, so New Game follows at once. Otherwise title state 0 with drawing enabled
   (0x1762200 = 1).
7. Register the draw process 0x14 = 0xB02B0.

Each frame, mode 0 calls the title update **0xB06B0**. Its return value `ret` goes through
jump table 0x209DC0 (0x22 entries, index `ret - 1`, dispatch VA 0x68DAF: `lea ecx,[rax-1];
cmp ecx,0x21; ja 0x69182`). Every entry is listed below; `title_return(ret)` models it and
`test_tables_match_constants` re-reads all 34 targets [E, V test]:

| ret | handler | effect | title returns it? |
|---:|---|---|---|
| 0, > 34 | 0x69182 (out of range) | shared case, `ret - 10 > 0x15` -> 0x69495: no mode change, stay on the title | yes (0 = stay) |
| 1 | 0x68DCB | mode 1 (attract). The 60-second idle counter 0x1762204 is decremented but never used | no: only title state 0x16 returns 1, and nothing stores 0x16 |
| 2 | 0x690EE | **New Game**: mode 2. Start scene = 0x2A1B0() if >= 0, else bit 485 ? 110 : 260 (script.md 7) | yes |
| 3 | 0x69154 | mode 2, resume the loaded save (0xE20CF4 = 1) | yes |
| 4, 8, 32 | 0x69182 | shared case, `ret - 10` out of 0..0x15 -> no change | no |
| 5 / 6 / 7 | 0x691B9 / 0x691C0 / 0x691C7 -> 0x656D0 / 0x66380 / 0x67410, mode 4 | extras screens | only 7 (GALLERY, state 0x27); states 0x25 / 0x26 / 0x28 are never stored |
| 9 | 0x691F8 | mode 2, start scene **0x708 = 1800** | no |
| 10..31 | 0x69182 | mode 2, start scene `ret + 0x231E` = **9000..9021** (VA 0x6918E) | no |
| 33 | 0x69229 | 0x75CD0(1), 0x423F0(0), 0x42400(0.0); mode 2, start scene **0xBE = 190**, word 0x1653AE0 = 0 | no |
| 34 | 0x69280 | same calls; **mode 6** (abmp_logo object, 3.5) | no |

The title update writes its return register only as 0, 1 (case 0x16), 2, 3 and `state -
0x20` in the shared case 0x25..0x28, so the reachable values are **0, 2, 3, 7** [E: every
`ebx` write in 0xB06B0..0xB1D80]. Scenes 1800, 190 and 9000..9021 are not in the scene table
(script.md 6.1: unknown ids fall back to START_SCRIPT); they belong to unreachable (debug)
entries. A port needs only 0, 2, 3 and 7.

### 4.2 Title state G[2] and the background (correction of archives_ui_movies.md 7.2) [E, V]

Four functions apply one rule step, using identical jump tables:

- 0xAFCE0 (UI reload 0xC2CE0, called by the language option 0x406B0 / 0x403C0 and by
  SaveImage_LoadSystem 0x6F470), table 0x218880;
- 0xAFE80 (title init), table 0x2188D0;
- 0xB0480, table 0x218924 (no static caller);
- 0xAFC30, table 0x218868 (no caller).

```
if G[10] == 0:  record0.child = 9;  movie = -1                         # glow layer, no movie
else:
    s = G[2];  if s > 5: G[2] = 0
    case 0: if bit(0x230) G[2] = 1        # bit 560: _RDST(560) in 14_00_ARC14_1
    case 1: if bit(0x17F) G[2] = 3        # bit 383: _RDST(383) in 05_01_CIEL05_3
    case 3: if bit(0x232) G[2] = 5        # bit 562: _RDST(562) in 15_01B_CIEL15B_1
    case 2, 4: G[2] = 0                    # never produced
    case 5: keep
    T = {1,2,3,3,2,4}[G[2]]                # 6 x i32 at text VA 0x218A40
    record0.child = 9 + T                  # data 0x873670 -> TITLE_PARTS child 10..13
    movie = T - 1 if T - 1 < 4 else -1     # data 0x873650 -> movie table id 0..3 = titlebg1..4.mp4
```

Each call advances by at most one step. A player with all three bits reaches state 5 only
after three title entries or reloads. Reachable values are 0 -> 1 -> 3 -> 5:

| G[2] | T | record-0 child | movie | BGM (VA 0xB0670: 0x98B0) | entered when |
|---:|---:|---:|---|---:|---|
| 0 | 1 | 10 | titlebg1.mp4 | 2 | start |
| 1 | 2 | 11 | titlebg2.mp4 | 2 | Arcueid route end reached |
| 3 | 3 | 12 | titlebg3.mp4 | 2 | Ciel route chapter 5 reached |
| 5 | 4 | 13 | titlebg4.mp4 | 72 | Ciel ending B reached |

- The loop point is **8.0333 s** (f32 at 0x2186E4), i.e. frame 241 at 30 fps (the movies
  start at pts 0; 120 s, 30 fps, 3,600 frames) [E, ffprobe].
- The still child 9 + T is the movie frame at the loop point **to within 2 frames** [V]:
  - Coarse check: mean absolute RGB difference of child 10..13 against the movie is
    1.29..1.40 at 8.0333 s, 2.97..14.43 at 0 s and 6.87..39.88 at 60 s
    (`title_still_vs_movie.tsv`). A plain difference cannot pin the frame: the clouds move
    slowly, so it stays within 1.07..1.42 for about +-0.3..0.5 s (the review's point).
  - Frame scan (`still_match_curve`, `title_still_frame_scan.tsv`, and a full 0..16 s scan
    with `scratch/misc_boot_title_choice_flow/still_dense.py`). Both images are box-downscaled
    4x, then a 9x9 high-pass (image minus its box blur) is compared. That isolates the cloud
    edges, which move:

    | T | high-pass minimum (frame) | value | frames within 10 % of it | value at frame 0 |
    |---:|---:|---:|---|---:|
    | 1 | 240 | 0.526 | 238..242 | 2.877 |
    | 2 | 240 | 0.268 | 235..247 (slowest clouds) | 0.959 |
    | 3 | 240 | 0.334 | 238..241 | 2.552 |
    | 4 | 239 | 0.657 | 237..242 | 4.761 |

    Over the whole 480-frame scan the minimum is unique and sits at frame 239..240, 1..2
    frames before the seek target 241. Exact frame identity is [H]; the +-2-frame match is
    [V]. The difference does not show, because the engine draws the still only while the
    seek has not landed (next rule). `test_still_is_loop_frame` asserts the minimum for T = 1
    lies in 239..243 and is clearly sharper than its neighbours.
- Background draw rule, per frame (VA 0xB2013..0xB20BA, `title_background_source`) [E]:

  ```
  if G[10] == 0:                         draw slot 125 (child 9) at (-187.5, -55.4074); done
  if byte 0x17621B8 != 1 or not 0xAFB10():   draw slot 125 (still); done   # no movie frame yet
  t = 0xEE960()                          # movie time (AvPlayer vfunc +0xA8)
  if t > 8.0333:  passed = 1 (0x17621B9); seek_due = 1 (0x17621BA); draw slot 0x87 (movie)
  elif t == 8.0333 or not passed:        draw slot 0x87
  else:                                  # wrapped: t < 8.0333 after having passed it
      if seek_due: 0xEE910(8.0333) (seek); seek_due = 0
      draw slot 125 (still)              # every frame until the seek takes effect
  word 0x873908 = the slot drawn (0x7D or 0x87): re-drawn below row 0x218DE0[L] by 0xB2AC5
  ```

  So the first 8.03 s play once, [8.03, 120) loops, and the still covers the frames between
  the wrap and the landed seek. The seek is issued once per wrap.

### 4.3 Title state machine (u32 0x17621FC, jump table 0x21893C, 0x3B entries) [E]

Covers:

- black (0x17621E8) and white (0x17621E4) full-screen rectangles, drawn last by 0xB02B0 on
  640 x 448 (`FillRect(0, 0, 640, 480)`);
- menu alpha 0x17621AC, logo alpha 0x176220C.

Fades use `dt * 1/60` per frame (dt = frames since the last update from 0xD47C0, 1 at 60
Hz [H]). Ends are strict (`> 1`, `< 0`), which gives 61 frames [F].

The draw code also keeps the menu alpha at or above the uncovered part of the screen: every
frame 0xB1F92..0xB1FBE sets `menu_alpha = min(1, max(menu_alpha, 1 - black_cover))` [E].
The logo alpha has its own schedule (4.5.1).

| state | role |
|---|---|
| 0x00 | title start: G[10] == 0 or intro already played -> BGM, state 1. Otherwise -> 0x32 (intro) |
| 0x32..0x36 | white cover in; **TM_CI.mp4** (movie 38) in slot 0x87; white cover out; any button / click stops the movie (0xEE8D0) |
| 0x36..0x3A | white -> black; **caution2** reveal, hold 480 frames (skippable), hide; then state 1 |
| 0x01 | 0x17621A4 += dt/120 (121 frames); black cover = 1 - that |
| 0x04 | G[10] == 0: wait for any button (`word +0x20 & 0xFFF`, or click), then BGM fade 1.5 s, SE 5, state 0x2B. Otherwise state 5 |
| 0x2B..0x31 | first-run **caution**, then **caution1** (720-frame timers, 240 unskippable), then state 0x0B |
| 0x0B | if pending start or bit 485 clear: return 2 now. Otherwise white cover to 1.2 (73 frames), return 2 |
| 0x0C | wait for the first titlebg frame, then 0x0B (title init path for "skip the title") |
| 0x05 | cursor = 5 when returning from the gallery, else 0 (CONTINUE) if available, else 1 (NEW GAME); state 6 |
| 0x06 | stop the stray voice (0xEC080(0x1D)) if flagged; state 8 |
| 0x08 | **menu input** (4.4). PlayGo poll: when the download completes, ui_text 244 "Download/Copy complete." dialog, state 0x10 |
| 0x09 | after cancel: menu alpha = 0, state 4 (so the menu fades in again) |
| 0x10 / 0x11 | black cover to 1.2, rebuild items (0xAFDA0), wait for the dialog, state 0 |
| 0x12 | wait for an error dialog, state 6 |
| 0x13 / 0x15 | Continue: dialog result -> SaveImage_LoadSystem(1), 0x73890(slot), BGM fade 1.5 s, black to 1.2, return 3 |
| 0x17 | save/load screen (0x71C90(0)): 2 = a save was loaded -> BGM fade, state 0x15 (return 3); 3 = closed -> state 6 |
| 0x18 | config screen (0x40860(0) == 1 -> closed): state 6 |
| 0x27 | (shared case 0x25..0x28) black to 1.2, return state - 0x20 = 7 |
| 0x29 / 0x22 | init 0xB0480 path: black cover out, then idle |
| 0x16, 0x19, 0x21, 0x23, 0x25, 0x26, 0x28 | **dead** (no store of these values) [V test] |

### 4.4 Menu [E, V render]

Seven items (cursor i32 0x873904, -1 = none). Each is a UI widget 0xE4 + i defined at
0x873780 (0x30 bytes: id, group 10, flags 0x20, ..., state pointer +0x20). Each state has 5
entries -> frame records at 0x873C00 + 0x30k: texture slot **126** (TITLE_PARTS child 0, the
968 x 472 sheet), flags 0x10, rect `(x0, y0, x1, y1)`. Widget states 0..4 use sheet columns
(0, 1, 0, 1, 2). Column 0 is normal, column 1 is the cursor (cyan), column 2 is the pressed
glow [H for the state numbering].

| i | item | sheet row (y0) | enabled (0x17621C0[i]) | decide action (jump table 0x218A28) |
|---:|---|---:|---|---|
| 0 | CONTINUE | 376 | last save slot valid (system +0x16EE <= 0x8A and slot +2 > 0) | check slot (0x76570), PlayGo (0xD1CD0(1)), save (0xEAC00 / 0xEA970). Fail: buzzer + ui_text 193 "No save data found." / 186 / 242. OK: SE 0, ui_text 184 "Loading...", state 0x13 -> return 3 |
| 1 | NEW GAME | 24 | 1 | SE 5, BGM fade 1.5 s, state 0x0B -> return 2 |
| 2 | LOAD GAME | 112 (x0 = 23) | 1 | SE 0, open save/load (0x6F450, 0xEA0D0(1), 0xECF40), state 0x17 |
| 3 | CONFIG | 200 | 1 | SE 0, open config (0x3F110), state 0x18 |
| 4 | (CONFIG copy) | 200 | 0 | buzzer only, never visible |
| 5 | GALLERY | 288 | 1 | PlayGo complete ? (SE 0, state 0x27 -> return 7, mode 4 gallery 0x67410) : ui_text 242 |
| 6 | (GALLERY copy) | 288 | 0 | none |

- Rows (i32 0x1762180[i], written by 0xAFDA0 and 0xAFFD6..0xB00F1) with continue available:
  CONTINUE 0, NEW GAME 1, LOAD 2, CONFIG 3, GALLERY 4 (5 rows). Without it: NEW GAME 0 ..
  GALLERY 3 (4 rows).
- Input (VA 0xB131F.., action masks via `*0xDB8778`):
  - mask 0 or pointer bit 0x4000: decide;
  - mask 1 or pointer bit 8 (right click): cancel;
  - word +0x18 bit 0x1000 or mask 6: up;
  - bit 0x4000 or mask 7: down;
  - bit 15 or mask 8: first enabled item;
  - 0x2000 or mask 9: last enabled item.
- Up / down wrap over 0..6 and skip disabled items. Pointer hover (0xCFE00) selects widget
  0xE4..0xEA directly.
- A change plays SE 2 and restarts the highlight bar.

### 4.5 Title draw list (VA 0xB1F30, from the draw process 0xB02B0) [E, V renders]

The draw process 0xB02B0 runs only while 0x1762200 == 1:

- In states 0x2C..0x31 and 0x37..0x3A it draws the caution (3.4).
- In states 0x34 / 0x35 it draws TM_CI (0xB1E50).
- Otherwise it calls 0xB1F30.
- It then always draws the black and white covers.

Steady state (all fades done), language `L` (tables have the ja entry for zc / zt / ko):

| # | slot = TITLE_PARTS child | canvas, scale, flags | position (units) | pixels (en) | notes |
|---:|---|---|---|---|---|
| 1 | 125 = record 0 = child 9 + T (or slot 0x87 movie) | 960 x 544, (0.5, 0.50370), 0x10 | (0, 0) | (0, 0) 1920 x 1080 | G[10] == 0: child 9 (2670 x 1266) at (-187.5, -55.4074) = (-375, -110) |
| - | particles: children 5..7, rand()-driven (0xB2F50, 0xB3310, spawner 0xB2C60) | 960 x 540 | - | - | not modelled |
| - | slot of #1 re-drawn below row `0x218DE0[L]` (ja 680, en 519) | 960 x 540, 0.5 | (0, y*0.5) | - | hides particles below the horizon; no visible change when particles are omitted |
| 2..4 | 127 = child 1 (logo), rect bands `0x873910[L]` | 960 x 544, 0x10 | `0x218B20[L]` + (0, band.y0 * 544/1080) | en (35, 222)..: bands (0,0,1,1), (0,1,1850,330), (0,330,1850,440); ja (680, 72): (0,0,560,285), (0,285,560,570), (0,570,560,648) | alpha `la` = 0x176220C (4.5.1). While it fades in, bands 1 / 2 use la^2 / la^3. Skipped (with the glow and the particles) when la <= 0 or byte 0x1762208 != 0 |
| 5 | 130 = child 4 (glow), blend 0x34 | 960 x 544, 0x10 | `0x218B50[L]`: ja (360, 278.04), en (360, 196.95) | en (720, 391) 488 x 256 | alpha = 0x17621DC (bounces 0..1 at 1/120 per frame) x `lf` |
| 6 | 128 = child 2, rect (24, 24, 360, 32), flags 0x11 (top-centre) | 960 x **540**, scale (grow * 0.5, 0.5) | (`0x218BA0[L][hl].x` + 84 = 483, y4 or y5) | NEW GAME 4 rows (798, 800) 336 x 8 | cursor underline. hl = `0x218B80[cursor]` = {0,1,2,3,-1,4,-1}. y4 / y5 = 4 / 5-row column (CONTINUE -250 / 383.5, NEW GAME 400 / 411.5, LOAD 428 / 439.5, CONFIG 455.5 / 467, GALLERY 484 / 495.5). grow 0 -> 1 at +0.1 per frame (reset to 0 when the cursor changes). Alpha = grow x (menu alpha - 0x17621F8) x `lf` (VA 0xB269D..0xB26C9; the SetColor with 0x17621EC at 0xB24CB is overridden before any draw). Drawn only when menu alpha >= 1 |
| 7.. | 126 = child 0 widgets, frame rect by row / state | 640 x 448, (1/3, 0.41481), 0x10 | (272, `0x218DF0[r15 + row]`): 4 rows 312.356 / 335.585 / 358.815 / 382.044, 5 rows 298.667 .. 391.585 (+23.23) | 4 rows: (816, 753 / 809 / 865 / 921) 296 x 72 | r15 = (n-2)(n-3)/2 + 2n - 3. Item alpha = clamp(2*menu_alpha - (row+1)/n) x `lf` (staggered fade, VA 0xB28F4..0xB2917). Drawn by 0xD0BE0(10) after the positions are set. 0x17621F8 is 0 after the title init (0xB01B5); the states 0x21 / 0x23 that use it to slide the items out are dead |
| 7' | 136 = child 14 PRESS ANY BUTTON (G[10] == 0 instead of the menu) | 960 x 544, 0x10 | (362, 410.0148) | (724, 814) 472 x 88 | alpha = menu alpha x pulse 0x17621F0 (0.1..1 at 1/60 per frame) |
| last | 129 = child 3 (C)TYPE-MOON | 960 x 544, 0x10 | (436, 507.7333) | (872, 1008) 184 x 64 | |

Slots: title table 0x873660 records 9(+T), 0..8, 14 -> slots 125..134, 136; 135 = AvPlayer
texture; 137 = caution.

#### 4.5.1 Logo alpha and the factor `lf` (VA 0xB20BF..0xB2198) [E, F]

The draw function updates the logo alpha `la` (f32 0x176220C) once per frame, right after
the background (`title_logo_step`):

```
fading_in = false
if black_cover (0x17621E8) < 0.5:
    if 0xEE9D0() or 0x6C5D0() > 0:   la -= 1/60      # f32 0x2186F4
    else:                            la += 1/120;  fading_in = true   # f32 0x2186F0
# black_cover >= 0.5: la unchanged
if la < 0:  la = 0;  skip the logo, the glow and the particles this frame
elif la > 1: la = 1
if la <= 0 or byte 0x1762208 != 0:  skip them as well (lf keeps last frame's value)
else:
    lf (f32 0x1762210) = la^3 if fading_in else la
    0xB29D0 (particles), logo bands with alphas (la, la^2, la^3) if fading_in else (la, la, la),
    moon glow with 0x17621DC x lf
```

- `0xEE9D0()` is AvPlayer vfunc +0x98 (VA 0x101FC0): `started (byte +0x84) && paused (byte
  +0x85)`. The pause flag is toggled by vfunc +0x90 (0x101F60: sceAvPlayerPause / Resume),
  which is called through 0xEE990 from VA 0x69E70.
- `0x6C5D0()` returns f32 0xE2377C, the overlay fade of the system screens. VA 0x69E70(edi,
  rate) starts that overlay and pauses the movie when the rate is > 0. Its callers include
  the config screen (0x3F870, 0x403C0), save/load (0x6D3B0.., 0x7198A, 0x72A25), extras
  (0x673F6) and the title (0xB184A). The system state machine's title re-entry (0x68B7F)
  sets the rates to -0.02 and resumes the movie.
- In other words: while LOAD GAME / CONFIG covers the title, the logo fades out at 1/60 per
  frame (61 frames in float32). When the screen closes it fades back in at 1/120 per frame (**121
  frames** in float32), with the squared / cubed band alphas.
- Snaps:
  - `la = 0` in title state 0 (VA 0xB07EC) and when caution2 has been hidden (state 0x3A,
    0xB1288, together with lf = 0);
  - `la = 1` on every menu input: decide (0xB13D0), PRESS ANY BUTTON / New Game (0xB163D),
    cursor moves (0xB1A81, 0xB1D26).
- Byte 0x1762208 = 1 once CONTINUE starts loading (0xB161D), 0 in title state 0 (0xB07DB).
- Typical first appearance (returning player, after caution2): state 1 lowers the black cover
  over 121 frames. The cover passes 0.5 after about 61 frames, then the logo fades in over
  121 frames, unless a button press snaps it to 1.
- `lf` multiplies the moon glow, the cursor underline and every menu widget (table above). It
  does not affect PRESS ANY BUTTON or (C)TYPE-MOON.

The review asked for the logo schedule. It described the -1/60 branch as "once the black
cover is > 0.5". The code above is the reverse: with the cover at >= 0.5 nothing changes; the
-1/60 branch needs cover < 0.5 **and** a paused movie or an active overlay (VA 0xB20BF:
`vucomiss 0.5, cover; jbe 0xB2107`).

### 4.6 Renders (acceptance b) [V, viewed]

`render_title` composes the draw list (normal alpha blending, additive for 0xB / 0x34).
Files in `samples/misc_boot_title_choice_flow/`, 1920 x 1080:

| file | content seen |
|---|---|
| `title_en_g2_0.png` | night sky with clouds (child 10), moon behind "TSUKIHIME / A piece of blue glass moon"; NEW GAME cyan with a thin underline, LOAD GAME / CONFIG / GALLERY grey, (C)TYPE-MOON at the bottom centre |
| `title_en_g2_5_continue.png` | daytime blue sky with pink clouds (child 13); 5 rows, CONTINUE highlighted |
| `title_ja_g2_0.png` / `title_ja_g2_5_continue.png` | vertical 月姫 logo over the moon (560 x 648 at (680, 72)), subtitle band ending above the menu |
| `title_en_press_any_button.png` / `title_ja_...` | G[10] == 0: dark-blue gradient with the moon (child 9), logo, PRESS ANY BUTTON at y 814 |
| (scratch) `r/title_en_g1_movie20.png` | titlebg2.mp4 frame at 20 s behind the menu (LOAD GAME highlighted) |

---

## 5. Choice menu

### 5.1 Script side [E, V census]

| command | handler | effect |
|---|---|---|
| `_SELR(id, text[, 1])` | 0x9936D | Slot k = count (u16 0x1750646). The slot text (0xFA bytes at 0x174FB70 + 0xFA*k) is `"$"` + 6 digits of the prefix line + `strcat` of argument 1. Prefix line (VA 0x993E0..0x9940A: `edx = 0xABB8 + k; cmp r15, 9; cmovae edx, 0xABB7`): **43960 + k for k = 0..8** = "1. " .. "9. " (ja "１．" .. "９．"); **43959 for k >= 9** = "0. " (ja "０．"), so a 10th line shows "0." [E, V text]. id -> u16 0x1750630[k]. Argument 2 == 1 -> **locked** byte 0x175064C[k]. 824 uses: 818 with 2 args, 6 locked (03_00_ARC03_5B x2, 07_00_ARC07_3_1, 13_01_01_CIEL13_4, 14_01_CIEL14_1C x2) |
| `_SELD(var[, N])` | 0x9B1DC | Mode dword 0x17506B0 = 1. Result variable from `_D<n>` (byte 0x1750648 = 'F' for `_D`, u16 0x175064A = n). **N** = argument 1 -> u16 0x1750698 (-1 if absent). Then 0xB8620(N) and 0x56150(1, seen), where seen = u8 0x164FBC0[N] != 0 [H: stops skip mode at a choice point not seen before]. Each slot is `$`-expanded and laid out (Text_Layout block 0x14 + k, width 574 en / 474 others, height 220, leading 9, sx = sy = 0.74). Then setup 0xA5DF0 |
| `_SELS(var)` | 0x9B435 -> 0xB82E0 | mode 2, two fixed answers (ids 0, 1), prompt ui_text 1446 + F[27]; QACALL |
| `_SELA(var)` | 0x9B1B6 -> 0xB8400 | mode 3, two answers, prompt ui_text 216 "Do you want to save your progress?" (chapter starts) |

Modes 2 / 3 use the widgets 0xD5 / 0xD6 from 0x8731F0. Their frames are DISP_PARTS child 19
(slot 359). These prompts are not drawn by 0xA6E10 (it returns for `mode & ~1 == 2`) [E];
their draw functions are 0xA7CD0 (mode 2) and 0xA8250 (mode 3), their layout is section 5.7.

The **"1." prefix** is part of the slot text, so it is always shown, in every language [E, V].

Lines per `_SELD` block over all 549 scripts (`choice_census`, `test_choice_prefix_text_and_census`)
[V]:

| lines | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| blocks | 1 | 64 | 36 | 5 | 4 | 7 | 4 | 4 | 5 | 40 |

All 40 ten-line blocks are in the debug scripts (JUMP01..JUMP14 except JUMP11, JUMPQA and
TEST_SCRIPT), whose 10th entry is therefore numbered "0." (like a keypad: 1..9, 0). No block
has more than 10 lines. Story blocks have at most 9 lines and never reach the 43959 line.

`_SELD` argument 1 is **not a default cursor**. The cursor starts at -1 (0xA5E06), so no line is
highlighted until the player moves or hovers. N is the **choice-point number**. It is used for:

- the SELECTTXT texture (record 1 child N - 1, table 0x8731A0, 5.4);
- the "choice taken" bit set u8 0x164FBC0[N] |= 1 << id (0xA6647..0xA6662);
- the skip-stop test in `_SELD`.

### 5.2 Phases and input (u16 0x17506A0, VA 0xA6180) [E]

| phase | behaviour |
|---|---|
| 0 | alpha 0x17506A8 += 0.04 * dt to 1 (25 frames), then clear the per-line "drawn" flags, phase 1 |
| 1 | text reveal: each line's head 0x1750660[k] grows (0x56160) up to 768. Decide (mask 0 / click / 0xE3A10 & 0x100) or all lines drawn -> every head = 768, phase 2, widgets group 13 on |
| 2 | **select** (details below) |
| 3 | decided: SE 3 is already played; the flash 0x17506AC was set to 1 (0xA6686). Per frame (VA 0xA648D..0xA64DC) alpha 0x17506A8 -= 0.028 * dt (clamped at 0) and flash -= 0.02 * dt. When **flash <= 0**: count = 0, flash = 0, free the textures, mode off |

Phase 2 (select). Action masks are read via `*0xDB8778`; pressed = word +0x20 and repeat =
word +0x18 of `*0xDC7BC0`; option = dword 0x17506C4 = (0xDF29EC == 0).

- Pointer hover over widget 0xD5 + k sets the cursor. It is ignored when line k is locked.
- **Decide**, only when the cursor is >= 0: mask 0, left click (pointer bit 1), or 0xE3A10()
  bit 0x100. Then:
  - The chosen `_SELR` id goes into the result variable (F array for `_D`, Z array for `Z`).
  - The taken bit is set and SE 3 plays.
  - Phase 3; the decided index goes to 0x17506A6.
- **Up**: repeat bit 0x1000, mask 2, wheel (pointer +0x3C > 0), or pressed bit 0x1000 when
  the option is set.
- **Down**: repeat bit 0x4000, mask 3, wheel < 0, 0xE3A10() bit 0x200, or pressed bit 0x4000
  with the option.
- Up and down wrap and **skip locked lines**. From -1 either one goes to the first unlocked
  line (VA 0xA66AD).
- **First / last** unlocked line: mask 4 / mask 5 (pressed bits 0x8000 / 0x2000 with the
  option), VA 0xA67AE / 0xA68E4.
- A cursor change (VA 0xA685D..0xA6896, only when the new value differs) does four things:
  - stores the **old cursor in u16 0x17506A4** ("previous cursor"; the setup 0xA5E0F sets it
    to -1);
  - resets the highlight 0x17506B4 to 0;
  - sets the flare pulse 0x17506B8 to 1 and its step 0x17506BC to -0.0111;
  - calls UiSel(0xD5 + cursor).
- Every frame of mode 1, in all phases, VA 0xA61BB runs `highlight = clamp(highlight + 0.1,
  0, 1)` (10 frames to 1) and bounces the pulse between 0.333 and 1 at +-0.0111 per frame.

Frame counts [F]: phase 0 takes 25 frames. In phase 3 the alpha reaches 0 after **36**
frames, and the phase ends on frame **51**, when the flash reaches <= 0 (float32: 50 steps
leave 4e-7). `choice_decision_frames()` gives both.

### 5.3 Choice draw list (VA 0xA6E10, called by the HUD 0x53CF0 at 0x5511D after the page text) [E, V render]

`a` = HUD alpha x (1 - 0x6F430()) x 0x17506A8. `x0` = 87 (ja / zc / zt) or 30 (en); `y_k` =
y0 + 26.02 k (float32 accumulation); `c` = cursor (u16 0x17506A2), `p` = previous cursor (u16
0x17506A4), `h` = highlight (0x17506B4).

```
SetCanvas(640, 448); SetColor(a); SetScale(1/3, 0.41481)
TexDraw(96 = SELECT_PARTS child 5 vignette, 0x10, blend 0, 0, 0)       # dims scene and page text
SetCanvas(640, 344.7)                                                  # VA 0xA6F3C
for k in 0 .. count-1:                                                 # VA 0xA70BC..0xA709F
    d = 0x56160(1); head[k] = 768 if d >= 768 else (head[k] + d if head[k] < 768 else head[k])
    if k == c or phase != 3:                                           # VA 0xA70EE..0xA7105
        drawn[k] = BlockDraw(0x14 + k, 0x1020 | shadow<<4, x0, y_k, alpha 1.0, head[k], head[k]) == 1
    if k == c or k == p:                                               # VA 0xA713F..0xA7151
        g = (k == p ? 1 - h : h) x a x 192 / 255                       # VA 0xA7157..0xA717D
        if g > 0:
            col  = phase == 3 ? 0xC7FF : 0x77FF                        # VA 0xA7209..0xA723A
            base = k == p ? 0x6C : 0x64                                # lea r13d,[rcx*8+0x64]
            for j, (dx, dy) in [(-.9,0),(.9,0),(0,-.7),(0,.7),(-.9,-.7),(.9,-.7),(-.9,.7),(.9,.7)]:
                Text_Layout(base + j, 0x75F00(slot text k) -> 0x17506D0, width 574 en / 474, 220, 0, 9, col, 0; 0.74, 0.74)
                BlockDraw(base + j, 0, x0 + dx, y_k + dy, g / 8, 768, 768)
    if k == c:                                                         # VA 0xA759C, whatever g is
        SetColor(a); SetScale((0.9 h + 0.1) x 640/1920, 0.31917)
        TexDraw(93 = child 2 underline 1455 x 48, 0x10, blend 0xB, x0 - 16.3333, y_k + 8.93667)
        SetColor(a x pulse 0x17506B8); SetScale(1/3, 0.31917)
        TexDraw(95 = child 4 flare 930 x 243, 0x10, blend 0xB, x0 - 153.333, y_k - 23.6183)
    WidgetDraw(0xD5 + k, x0, y_k x 448 / 344.7, a)                     # hit box (0,0,466,29), no frames
if phase == 3:                                                         # VA 0xA7682..0xA782D
    s = sin(flash x HUD alpha x (1 - 0x6F430()) x (-pi/2)) + 1          # f64 0x216A50; flash 1 -> 0
    SetColor(a); SetScale((1.4 s + 0.1) x 640/1920, (1.4 s + 0.1) x 344.7/1080); SetRot(trunc(s x 65535))
    TexDraw(94 = child 3 ring, 0x34 (centre, scale, rotate), blend 0xB, 86, y0 + 18.8308 + 26.02 c)
    SetRot(0); SetColor(a); SetScale(1/3, 0.31917)
    TexDraw(91 = child 0 blue bands, 0x10, blend 0xB, 0, 0)
    SetColor(a); SetScale(1/3, 0.31917)
    TexDraw(92 = child 1 lens flare, 0x10, blend 0xB, 5, y0 - 290.761 + 26.02 c)
restore the caller's canvas and scale
```

What follows from this, all [E]:

- **Decided phase.** Only the chosen line's text is drawn; every other line disappears at
  once (VA 0xA70FD: `cmp word [0x17506A0], 3; je 0xA713F` skips the block draw).
  - The chosen line is drawn with the constant alpha argument 1.0 (VA 0xA7111). It stays
    opaque for all 51 frames while the vignette, glow, underline, flare and flash fade out with
    `a` (0 after 36 frames).
  - Its glow copies use the pale colour 0xC7FF.
  - The ring grows from scale 0.033 to 0.5 (x 1/3) and turns once, as the flash runs from 1
    to 0.
  - The hit widgets are still drawn with `a`.
  - The earlier version of this document drew every line in every phase. The review found this
    at 0xA70EE.
- **Previous cursor cross-fade.** After a move from line p to line c, both lines carry 8
  glow copies: c with alpha `h`, p with `1 - h` (blocks 0x6C..0x73). Because h rises by 0.1
  per frame, the cyan glow moves over 10 frames. The underline and the flare jump to c at once
  (their width and pulse restart).
- The underline and the flare are drawn on the cursor line even when `h = 0`. The underline
  is then 0.1 of its width.
- In phase 3, if the decision came within 10 frames of a move, `1 - h > 0` and the previous
  line's glow is still drawn (in 0xC7FF), although its text is hidden.
- Glyph colour words are expanded as textbox.md 6.3: 0xFFFF -> (248, 248, 248), 0x77FF ->
  (112, 248, 248) cyan, 0xC7FF -> (192, 248, 248).

`choice_draw_list()` returns exactly this sequence. Text blocks have `kind = 'text'` (slot =
block id, source = ('text', k, colour)), the widgets have `kind = 'hit'`. `render_choice`
composites it in this order. Renders: `choice_01_00_ARC01_1_en_decided_f18.png` and
`choice_01_00_ARC01_1_en_crossfade_h05.png` (5.5).

First line y0 (VA 0xA6F41..0xA6FAB):

```
y = f32 at 0xDF0890        # click-icon y stored by the HUD at 0x54CC7:
                           # message y + int(y of the last revealed line), from 0xCA690
if not y > 0:  y = 49.151669 (ja/zc/zt) or 34.934246 (en)     # immediates 0x42449B4F / 0x420BBCAB
if F[823] > 0: y += 52.04                                     # messages on the page: two line pitches below
```

Locked lines are drawn exactly like the others. No code in 0xA6E10 reads 0x175064C [E]. A
port that greys them out departs from the PS4.

### 5.4 SELECTTXT_PARTS is loaded but never drawn [E, V]

The setup 0xA5F8D..0xA5FC1 rewrites table 0x8731A0 record 1 to `(tex 98, child N - 1)` when
1 <= N <= 89, and loads record 0 (child 89, clamped to the 120 x 808 digit column) into slot
97. Nothing draws slots 97 / 98:

- No `TexDraw` / `TexDrawRect` call site passes 97 or 98. A constant-slot census over all
  callers of 0xEB780 / 0xEB890 / 0xEB9C0 finds slots 0x5B..0x60 only in the choice code.
- No instruction anywhere moves 0x61 / 0x62 into `edi` before a call, apart from two
  unrelated calls of 0xB7C10.
- The data segment holds no widget frame record `(97|98, 0x10)`, while the title's
  `(126, 0x10)` occurs 21 times (`test_selecttxt_never_drawn`).

All languages, **including Japanese**, render choices as font text. The 82 pre-rendered
Japanese images are leftovers.

### 5.5 Worked example: the first story choice (acceptance c) [V, viewed]

`01_00_ARC01_1`: `..._ZM02171($001263);..._ZM02172($001264);..._ZM02173($001265@k@e);
_PGST(44);...;_ATSV(1);_SELR(0,$042569);_SELR(1,$042570);_SELR(2,$042571);_SELD(_D000,1);`

| | en | ja |
|---|---|---|
| page messages (F823 = 3) | origins (30, 35), (30, 61), (30, 139) | (87, 49), (87, 75), (87, 127) |
| click-icon y | 139 | 127 |
| y0 = icon + 52.04 | 191.04 | 179.04 |
| lines (units -> px at 1080/344.7) | 191.04, 217.06, 243.08 -> 598.6, 680.1, 761.6 | 179.04, 205.06, 231.08 -> 561.0, 642.5, 724.0 |
| texts | the three choice lines of ids 42569..42571 as the engine numbers them: `1. ...` / `2. ...` / `3. ...` (ASCII digit, full stop, space, then the line; the wording is not reproduced here) | `１．…` / `２．…` / `３．…` (full-width digit and full stop, then the line) |
| N | 1 (SELECTTXT child 0 loaded, unused) | same |

Renders `choice_01_00_ARC01_1_{en,ja}_cursor{none,1}.png`. They are drawn on black, because
the real scene is a layer composite that belongs to the layers gap.

- The three page messages appear dimmed to grey by the vignette.
- The three choice lines appear in white below them, two line pitches lower.
- With cursor 1, the second line is cyan with a soft glow, a thin cyan underline runs from the
  left margin across the line, and a bright spark sits at its left end.
- The ja render uses full-width "１．" prefixes at x = 87 units (261 px).

Review-fix renders (en, same block), viewed:

- `choice_01_00_ARC01_1_en_decided_f18.png`: phase 3, 18 frames after deciding line 2 (a =
  0.496, flash = 0.64). Only "2. ...think of something to ask." remains, in white at y 680.
  Lines 1 and 3 are gone: the maximum red in their rows is 0.024 / 0.051 (only the additive
  flash), against 0.98 on line 2.
  Also visible:
  - the additive blue bands over the whole screen and the vertical lens-flare streak at x 270;
  - a thin cyan ring of radius about 150 px centred at (258, 739) on the chosen line;
  - the half-faded underline and spark.
- `choice_01_00_ARC01_1_en_crossfade_h05.png`: the cursor moved from line 2 to line 3, five
  frames ago (h = 0.5). Lines 2 and 3 both carry a faint cyan glow of equal strength, line 1
  stays white, and the underline (0.55 of full width) and the spark sit under line 3 only.
- Scratch `r2/phase3_en_c1_f0.png` is the decision frame (flash 1). The ring is a dot, and the
  bands and the flare are at full strength.

### 5.6 Choice canvas and the text vertical scale (open, [H])

The choice text is drawn after `SetCanvas(640, 344.7)`, so text-space y maps to y x 3.1332 px.
The page text keeps the textbox model (3.0 px per unit). Line positions use 3.1332. The glyph
height in the renders is kept at 3 px per unit, because it depends on the font-page texture
mode byte (+7, VA 0xDD82C: mode 2 multiplies quad heights by 1.24444). That byte was not
traced for font pages. This is the same open question as textbox.md 12.1. Note: 1080 / 344.7 =
3.133 is close to, but not equal to, the 3.115 px per unit measured on the prologue movie.

### 5.7 The two-button prompts `_SELS` / `_SELA` (VA 0xA6940 / 0xA6AD0 update, 0xA7AA0 / 0xA7FD0 input, 0xA7CD0 / 0xA8250 draw) [E]

Read with `tools/eboot/disasm.py` (package K13, docs/status/knife/K13.md). Setup 0xA5DF0 (mode 2 / 3): phase 0, cursor -1 (`0xFFFF`), previous -1, alpha 0, byte 0x17509D0
(mode 2) or 0x17509E0 (mode 3) = 1, the widget table 0x8731F0 registered (group 13), `0x75CD0(1)` (a new page), F823 = 0, F1103 = 1; the message is
`ui_text(1446 + F27)` (mode 2, pointer 0x17509D8) or `ui_text(216)` (mode 3, pointer 0x17509E8). Buttons: widget 0xD5 = index 0 = **No** (left), 0xD6 = index 1 = **Yes** (right).

Update, `dt` = frames of the step. Mode 2 (0xA6940): phase 0 `alpha += 0.1 dt`, at `alpha >= 1` alpha = 1, phase 2, group 13 on; phase 2 -> input 0xA7AA0 (unless byte
0x120A6D8 is set); phase 3 `alpha -= 0.1 dt`, at `alpha <= 0` the choice ends (alpha 0, wait bit 0x10 off, `0x75CD0(1)` again, count 0, group 13 off). Mode 3
(0xA6AD0, jump table 0x216A30) has the same phases 0 / 2 / 3 and 4..7 for the Yes: 4 `alpha -= 0.1 dt` (left where it falls, below 0), at `alpha <= 0` `0x6F450(1)`,
`0xEA0D0(1)`, `0x6F460(1)` and phase 5; 5 `UiSel(-1)`, `0xECF40(0x16, 0, 0x100)` (the save menu opens) and phase 6; 6 waits for `0x71C90(0) == 3`, then `0xECF40(0x16, 0x100, 0)`
and phase 7; 7 -> phase 3, which ends at once because the alpha is spent.

Input (both modes; the pad word +0x18 is the repeat set, +0x20 the pressed set): the frame is void when the cancel mask (mask 1) is pressed or the pointer's right button
(flag byte +0x34 & 8) is set. The hovered widget (0xCFE00) selects its button: a change of the cursor plays SE 2 and `UiSel(0xD5 + k)`. Decide (mask 0) or the left
click (+0x34 & 2): a click on no button while the pointer is in use (`[+0xC] > 0`) is void; the cursor must be 0 or 1 (-1 is `0xFFFF`, above 1) or nothing happens;
**SE 0** plays; mode 2 stores the cursor itself in the target (0xA7B99..0xA7BDE), sets the decided index 0x17506A6 and goes to phase 3; mode 3 stores nothing and goes to phase 3
(cursor 0) or 4 (cursor 1). Moves, tested in this order: left (bit 0x8000): cursor > 0 -> cursor - 1, else a **fresh press** gives 1 and a repeat 0; right (0x2000): cursor <= 0 ->
cursor + 1, else a fresh press wraps to 0 and a repeat stays 1; up (0x1000) -> 0 and down (0x4000) -> 1 (SE 2 only when the cursor changes). From no selection, right and up select No; down and a fresh left select Yes (a repeated left selects No).

Draw (0xA7CD0 mode 2, 0xA8250 mode 3 are the same code with their own constants; `a = alpha x HUD alpha`, drawn only when `alpha > 0`, the menu overlay fades 0x67500 /
0x67510 are 0; canvas 640 x 448):

```
SetColor(a, 1, 1, 1); SetScale(1/3, 0.41481)
TexDraw(slot 360 = DISP_PARTS child 21 dialog_BG 1920 x 1080, 0x14 = centre + scale, blend 0, (320, 224))             # at alpha a, not 0.75 a
s = 1 + 0.05 (1 - a) in phase 0, 1 - 0.05 (1 - a) in phase 3, else 1                                                  # mode 3 phase 4 .. 7: 1
SetScale(s / 3, s x 0.41481)
TexDraw(slot 358 = child 20 dialog_win 824 x 416, 0x14, blend 0, (321, 224))                                          # one unit right of the centre
Text_Layout(block 20, message, width 474, height 220, spacing 0, leading 9, colour 0xFFFF, flags 0x100; sx 0.37 s, sy 0.46044 s)
BlockDraw(block 20, flags 0x1000, x 83.3333, y 224 - 42.3111 s, alpha a, head 768, tail 768)                         # 0x100 centres the lines in the 474-wide block, no 0x20
WidgetDraw(0xD5, 241, 238.5185, a); WidgetDraw(0xD6, 327, 238.5185, a); scale (1/3, 0.41481); widget pass group 13
```

The buttons are the 216 x 56 cells of DISP_PARTS child 19 (`dialog_btn_ja`): plain No (224, 67), Yes (224, 0), selected (glow) No (0, 202), Yes (0, 135). The widget hit
box of both records (0x8731F0 + 0x30 k) is the shorts (-10, -10, 60, 45) at +0x0C, added to the widget position (truncated to 241 / 327 and 238) and compared with the pointer in
640 x 448 units, ends included: No x 231..301, Yes x 317..387, y 228..283 (0xD05B0 takes the first widget, in id order). Mode 2 and 3 draw nothing else: no guide line, no history
record (0xB82E0 / 0xB8400 take no snapshot; a save from the prompt's screen is state A, section 7 of adv_state.md).

---

## 6. Python API and CLI

```python
from tsuki import misc_boot_title_choice_flow as sf
sf.boot_sequence(bit485=True, g10=True)          # ordered BootStep list (3.4)
sf.advance_title_state(0, [0x230]); sf.title_background(1, 5)   # 4.2
sf.title_draw_list(sf.TitleView(lang=0, g2=5, cursor=0, continue_available=True))   # Draw records
sf.choice_draw_list(sf.ChoiceView(lang=1, count=3, cursor=1, y0=191.04))
img = sf.render_title(game, sf.TitleView(lang=1, g2=0, cursor=1))
sc = sf.find_choice_scene("01_00_ARC01_1", 0, 1, game); img, info = sf.render_choice(game, sc, sf.ChoiceView(lang=1, cursor=1))
with Eboot(game / "eboot.bin") as eb: t = sf.read_tables(eb)  # every table above, from the eboot
sf.ui_lookup_split(game)                          # {'direct': 695, 'clamped': 4, 'rewritten': 8, ...}
# review fixes
sf.title_return(9)                                # TitleReturn(handler 0x691F8, mode 2, scene 1800, unreachable) 4.1
sf.title_logo_step(la, cover, movie_paused=False, overlay=0.0)   # (new la, fading_in) 4.5.1
sf.title_background_source(t_movie, sf.TitleMovieState())       # (slot 0x87 | 0x7D, seek issued) 4.2
sf.choice_draw_list(sf.ChoiceView(cursor=2, prev_cursor=1, highlight=0.5, phase=2))  # full per-line order 5.3
sf.choice_decision_frames(); sf.choice_decision_state(18); sf.choice_ring_transform(0.64)  # phase 3
sf.choice_prefix_line(9)                          # 43959 ("0. ") 5.1
sf.choice_census(game)                            # SELR / SELD counts, lines per block
sf.still_match_curve(game, 1, 234, 13)            # per-frame still-vs-movie (high-pass metric) 4.2
```

`Draw.pixel_rect()` applies section 2. `render_draws(draws, AssetCache(image_dir))` composites a
list.

CLI (`PYTHONPATH=tools python -m tsuki.misc_boot_title_choice_flow ...`): `boot
[--first-run] [--damaged] [--g10 0]`, `title-state`, `tables`, `lookup-split`, `render-title
out.png --lang ja --g2 5 --cursor 0 --continue [--movie-time 20]`, `render-choice out.png
--lang en --cursor 1 [--prev p --phase 3 --highlight h --alpha a --flash f --script S --block k
--scene]`, `render-boot outdir`, `samples outdir`.

---

## 7. Verification (commands actually run)

- Disassembly with the scratch helpers `dz.py` (index cache `ix.pkl`), `jt.py` (jump-table
  case labels plus decoded call arguments) and `calls.py`. Dumps are in
  `scratch/misc_boot_title_choice_flow/`:
  - `title_b06b0_cases.txt` (title update), `title_afa30.txt` (G[2] rule, init),
    `title_b1f30.txt` / `title_draw_b02b0.txt` / `title_b1d80.txt` / `title_b29d0.txt` (draw),
    `sysmenu_68a70.txt`;
  - `sel_setup_a5df0.txt`, `sel_input_a6180.txt`, `sel_draw_a6d00.txt`;
  - the earlier dumps `e8280.txt`, `eee50.txt`, `eea90.txt`, `seld.txt`.
- Jump tables decoded: 0x2D66A4 (5), 0x2D6060 (0x35), 0x21893C (0x3B), 0x218A28 (6),
  0x218880 / 0x218868 / 0x2188D0 / 0x218924 (6 each), 0x2188E8 (15), 0x209DC0 (0x22),
  0x209D7C (7).
- Reference searches:
  - stores to 0x2057CD0..0x2057CF0, 0x17621FC, 0xE20CC8 and 0xDF088C / 0xDF0890;
  - references to the caution, `abmp_logo`, `BG_TOP` and `bg_top` strings and to 0x1750698 /
    0x175064C / 0x1750646 / 0x17506B0;
  - constant-slot census of the texture draw calls, and a search for `mov edi, 0x61/0x62`.
- Script census (`AllScr` raw scan):
  - G010 written only by `00_00`; no G002 writer;
  - `_RDST(560/561/562/383/485/486)` locations;
  - `_SELR` argument counts (818 with 2, 6 locked).
- `ui_lookup_split`: {direct 695, clamped 4, rewritten 8 (4 title, 4 selecttxt)} [V].
- `still_vs_movie`: ffmpeg frames at 0 / 8.0333 / 60 s against TITLE_PARTS_EN children
  10..13, giving the table in 4.2. ffprobe: TM_CI 6.0 s at 30 fps (180 frames); titlebg1..4
  120 s, 3,600 frames.
- Renders viewed: the six title PNGs, four choice PNGs, five boot screens (damaged notice
  box centred, autosave text with the bottom-right "Saving..." icon at (1790, 995), caution /
  caution1 / caution2) and the titlebg2 movie-frame variant.
- `pytest tools/tests/test_misc_boot_title_choice_flow.py`: 13 passed in 2.2 s. With the game
  missing: 6 passed, 7 skipped. `mypy --ignore-missing-imports`: clean.

Review repair, 2026-10-25 (each check was run; section 10 lists the issues):

- Re-read `sel_draw_a6d00.txt` 0xA6E10..0xA787B line by line:
  - the phase-3 skip at 0xA70EE..0xA7105;
  - the previous-cursor test at 0xA713F..0xA7151 and the `1 - h` alpha at 0xA7157..0xA716C;
  - block base `lea r13d,[rcx*8+0x64]` (0xA7223..0xA74E1);
  - the underline / flare gate at 0xA759C, reached also from the `g <= 0` branch at 0xA718D;
  - the text block alpha immediate 1.0 at 0xA7111;
  - the ring `sin(flash x f64 0x216A50 = -pi/2) + 1` at 0xA76BF..0xA772C.
- Input / setup: the prev-cursor store at 0xA6874 and its setup at 0xA5E0F; the highlight and
  pulse update at 0xA61BB..0xA6237; the phase-3 fades and the end test at 0xA648D..0xA64DC
  (`sel_input_a6180.txt`); the flash set to 1 at 0xA6686.
- `_SELR` prefix: disassembled 0x993C0..0x99440 (0xABB8 + k, `cmovae` 0xABB7 at k >= 9).
  ScriptText en / ja lines 43959..43968 read: "0. ", "1. " .. "9. ", "０．" .. "９．". The census
  over all scripts gives block sizes {1:1, 2:64, 3:36, 4:5, 5:4, 6:7, 7:4, 8:4, 9:5, 10:40}; the
  10-line blocks are in JUMP*/TEST_SCRIPT only.
- Title logo (`title_b1f30.txt` 0xB20BF..0xB2408):
  - AvPlayer object 0x2085148 traced to its constructor 0x101280 (vtable relro 0x42E508);
  - vfuncs +0x70 / +0x98 / +0xA8 / +0xB8 / +0x90 disassembled (0x101F00, 0x101FC0, 0x102030,
    0x1020A0, 0x101F60);
  - 0x6C5D0 -> f32 0xE2377C, written by 0x6BF10, rates set by 0x69E70 and its 23 callers;
  - logo snaps found by a store search for 0x176220C in `title_b06b0_cases.txt`.
- Background: 0xB2013..0xB20BA, 0xB25E2..0xB2605 (flags 0x17621B8 / B9 / BA).
- Title returns: decoded all 0x22 entries of 0x209DC0 (`dz.py`), read the handlers
  0x68DCB..0x692CD, 0x69182 (`ret - 10 <= 0x15` -> scene `ret + 0x231E`) and 0x69495.
- Still frame: `still_dense.py` (all 480 frames of 0..16 s of each titlebg, about 2 min) and
  `still_match_curve` (frames 225..257 -> `title_still_frame_scan.tsv`). ffprobe: start_time 0,
  1/30 time base, first frame pts 0.
- Renders viewed:
  - `r2/phase3_en_c1_f0.png`, `r2/phase3_en_c1_f18.png`, `r2/xfade_en_c2_p1.png`;
  - the regenerated samples (`samples` CLI, 31 s), including `title_en_g2_0.png` (unchanged
    in steady state), `..._decided_f18.png` and `..._crossfade_h05.png`.
- `pytest tools/tests/test_misc_boot_title_choice_flow.py`: **20 passed in 14.7 s** (the census
  scan and the ffmpeg frame scan take most of it). With `TSUKI_GAME_DIR` pointing to a missing
  folder: **12 passed, 8 skipped** in 0.2 s. Together with `test_catalog.py`: 57 passed.
  `mypy --ignore-missing-imports` on the module and the test: clean.

---

## 8. Open questions and dependencies

1. Exact shader math for draw-effect ids 0xB and 0x34 (compositing_transitions_shaders). The
   renders use additive blending.
2. Game input bit space: the 0x860 skip mask, the action-mask indices 0..9 used by the title
   and choice input, and the option at 0xDF29EC that changes the choice decide buttons
   (adv_system_state).
3. Font-page texture mode, which sets the glyph height on the 640 x 344.7 choice canvas (5.6;
   textbox.md 12.1).
4. ~~`_SELS` / `_SELA` prompt layout~~ answered in 5.7 (package K13); the menu
   screens behind LOAD GAME / CONFIG / GALLERY (adv_system_state, gallery_extras_flowchart).
5. Meaning of task_game boot result 5 and of 0xEAFB0 / 0x120760 (likely the save-error dialog).
   The parts damaged notice is certain.
6. Whether G[] starts zeroed when the system save is damaged (3.4 damaged column).
7. The title particle system (0xB2C60 spawner: rand() ranges 0.3..0.75 alpha, 150 frames,
   80..100 degree directions; children 5..7) and the shimmer of 0xB2AF0 are not modelled.
8. The scene behind a choice is a layer composite (layer_state_and_motions); the choice
   renders use black.
9. VM variable namespaces (`F` / `Z` result arrays of `_SELD`, `_D` operands): vm_variables_errata.
10. Audio cues (SE 0/1/2/3/5, title BGM ids 2 / 72, BGM fade 1.5 s, system voice ids
    0x47 / 0x48 / 0x49 in the dead greeting): audio_runtime_semantics.
11. Rotation direction on screen of a positive SetRot angle. The renders rotate
    counter-clockwise. It is invisible for the two rotated sprites here (the choice ring and
    the boot spinner at angle 0).
12. Exact frame of the title still: the high-pass match is best at frame 239..240, the seek
    target is frame 241 (4.2). A port should simply draw the still while the seek lands.

---

## 9. Errata for other documents

### 9.1 Errata for archives_ui_movies.md

- **7.2 lookup split.** The correct split is **695 direct, 4 clamped, 8 rewritten at run
  time**, not 699 / 4 / 4. Title table 0x873660 record 0 (child 9 in the file) is rewritten
  to 9 + T before every load (4.2), in 4 languages. Proof: `ui_lookup_split` /
  `test_ui_lookup_split`, plus the stores at VA 0xAFCC2, 0xAFD72, 0xAFEEF, 0xAFF02, 0xB0512
  and 0xB04C3 (and 0xAFC73 / 0xAFD23 / 0xB04C3 for the G[10] == 0 value 9).
- 7.2 row 0x873660 "title 9,0..8,14 -> 125..134,136": slot 125 receives child 9 only while
  G[10] == 0, otherwise child 10..13 (4.2).
- 5.2 SELECTTXT_PARTS "[H: other languages render choices from text]": all languages,
  Japanese included, render text. The images are never drawn (5.4). The N that selects record
  1 is `_SELD` argument 1, the choice-point number.
- 6 parts.mrg: #1 (TYPE-MOON logo) and #19..23 (disclaimer) are used only by unreachable
  states of the boot process (3.3). #2..6 are the save-damaged notice, shown in the
  **system** language. #7, #8..12, #13..17 and #18 make up the autosave notice shown on every
  boot.
- 7.5 canvas 640 x 344.7 (VA 0xA6E10) is the choice-text canvas. Its scale sites are
  0xA75EF (underline), 0xA764F (flare), 0xA7716 (phase-3 ring), 0xA77A2 (blue bands) and 0xA77ED
  (lens flare) (5.3).
- 9 movies: TM_CI (38) plays once per power-on, only when G[10] != 0. titlebg loops [8.0333 s,
  120 s) after one full first pass (4.2).

### 9.2 Errata for engine.md

- 8.1 boot sequence:
  - There is no logo screen from `abmp_logo.mrg` (loaded only by the mode-6 object, 3.5) and
    `BG_TOP.MZP` is never read.
  - The order is: system save -> [save-damaged notice] -> autosave notice -> title intro.
  - The title intro is TM_CI.mp4 then caution2 for returning players. For first-time players
    it is the prologue movie, then PRESS ANY BUTTON -> caution -> caution1 -> story.
  - The cautions are shown by the title code (0xB06B0), not before the title.
- 8.7 "Looping is used for title backgrounds [H]" is confirmed [E]: MoviePlay(id, 0, loop = 1)
  and the seek to 8.0333 s at the loop.

### 9.3 Errata for FORMATS.md

- 5.4 "title background table text 0x218A40, 6 bytes {1,2,3,3,2,4}": it is **6 x i32** (24
  bytes), indexed by G[2].
- 3 row 31 and 7 P0 #1 / P1 #9: answered by this document (boot order, title background,
  choice screen).

### 9.4 Errata for script.md

- 4 / `SELD (var, [default])` "Arg 2 is the choice set / cursor default": argument 1 is the
  choice-point number N (SELECTTXT image, taken-bit index, skip stop). The cursor always starts
  at -1 (5.1).
- `SELA` = mode 3 "Do you want to save your progress?" (ui_text 216); `SELS` = mode 2, prompt
  ui_text 1446 + F[27] (5.1).
- Open question 9 ("the other title-menu actions that set a scene: ids 9000+n for actions
  10..31, 190, 1800") is answered: they are entries 9, 10..31 and 33 of jump table 0x209DC0
  (4.1), and the title update never returns those values (only 0, 2, 3, 7). They are
  unreachable in 01.01.
- Choice prefix: the 10th `_SELR` of a block (k = 9) is numbered with line 43959 "0. " (ja
  "０．"), not "10." (5.1); this happens only in the debug JUMP / TEST scripts.

### 9.5 Corrections to the task statement

- "the SELD arg-2 default cursor": there is no default cursor (5.1).
- "the fallback is child 9 when word 0x164C634 == 0": confirmed. 0x164C634 is G[10].
- "T at text VA 0x218A40": confirmed. The values are i32, and states 2 and 4 are unreachable.
- File names. The gap entry in `scratch/rerun/critic_gaps.json` names `tools/tsuki/sysflow.py`,
  `tools/tests/test_sysflow.py`, `samples/sysflow/` and `scratch/sysflow/`. The workflow task
  that commissioned this work gives explicit ownership of `misc_boot_title_choice_flow.py`
  (+ helpers), `test_misc_boot_title_choice_flow.py`, `samples/misc_boot_title_choice_flow/`,
  `scratch/misc_boot_title_choice_flow/`, this document and the pointer
  `misc_boot_title_choice_flow.md`. The agent brief also prescribes `test_misc_<slug>.py`.
  - Writing the `sysflow` names as well would write outside the assigned paths, so they were
    not created. Nothing else in the repository imports either name (checked: no
    `tsuki.sysflow` reference).
  - The review rated this a process deviation, not a content error. FORMATS.md 1.2 should
    point to this document and `tsuki.misc_boot_title_choice_flow`.

---

## 10. Review response (repair of 2026-10-25)

Every issue from the review, with what changed and how it was checked. None of the review's
findings was wrong in substance. One detail of issue 4 was reversed, with proof in 4.5.1.

| # | severity | finding | status | change / proof |
|---:|---|---|---|---|
| 1 | major | phase 3 draws only the cursor line's text (0xA70EE..0xA7105) | **fixed** | 5.3 pseudocode; `choice_draw_list` emits text block 0x14 + k only for the cursor in phase 3; `render_choice` composites the draw list in engine order; `test_choice_decided_phase_hides_other_lines` (draw list) and `test_render_title_and_choice` (rows of lines 1 / 3 have red max < 0.3 in a phase-3 render); sample `..._decided_f18.png`. Also corrected: phase 3 lasts 51 frames (flash <= 0), not 36 (that is when the alpha reaches 0); the ring's scale / rotation formula; the chosen line keeps alpha 1 |
| 2 | minor | previous-cursor glow (u16 0x17506A4, blocks 0x6C..0x73, alpha 1 - h) | **fixed** | `ChoiceView.prev_cursor`, `choice_glow_alpha`; 5.2 / 5.3; `test_choice_previous_cursor_glow`; sample `..._crossfade_h05.png`. Also found: underline and flare are drawn even when h = 0 (0xA718D -> 0xA759C), now modelled |
| 3 | minor | line 43959 is "0. ", the 10th line shows "0." | **fixed** | 5.1 wording, `choice_prefix_line`, eboot immediates re-read (`Tables.sel_prefix`), ScriptText lines checked and block-size census (`test_choice_prefix_text_and_census`) |
| 4 | minor | logo-alpha schedule; still drawn every frame after the wrap until the seek lands | **fixed** | 4.5.1 (schedule, `lf` factor applied to glow / underline / widgets, snaps, byte 0x1762208), `title_logo_step`, `title_logo_factor`; 4.2 rule and `title_background_source`; `test_title_logo_alpha`, `test_title_background_source`. The review's "-1/60 once the cover is > 0.5" is reversed in the code: cover >= 0.5 freezes la; -1/60 needs cover < 0.5 and a paused movie or an open overlay (VA 0xB20BF `jbe 0xB2107`; 0xB20D1..0xB20E7) |
| 5 | minor | return table 0x209DC0 has live handlers for 9, 10..31, 33, 34 | **fixed** | 4.1 lists all 34 entries; `TITLE_RETURN_TARGETS` / `title_return`; `test_tables_match_constants` re-reads the table, `test_title_returns` checks the model. The shared case 0x69182 is not a no-op: ret 10..31 start scenes 9000..9021 |
| 6 | minor | sysflow file names | **kept, reasoned** | 9.5: the task's ownership list names the misc_boot_title_choice_flow paths; the sysflow paths were not assigned to this run |
| 7 | minor | the still-vs-movie check cannot pin the frame | **fixed (measured)** | 4.2: frame scan with a motion-sensitive metric; minimum at frame 239..240, within 2 frames of the seek target 241, a sharp minimum (4..6 frames within 10 %); `test_still_is_loop_frame` extended. Exact frame identity stays [H] (open question 12) |

Further fixes made during the repair:

- Underline alpha. It is `grow x (menu_alpha - 0x17621F8) x lf`. The earlier
  `grow x menu_alpha x 0x17621EC` used a SetColor that is overridden before the draw.
- Menu widgets are multiplied by `lf`.
- The menu alpha floor `1 - black cover` (4.3).
- Rotation is now supported in the renderer for centre-anchored sprites.
