# ADV wait model: end modes, click / auto / skip, timers, voice and movie waits

Scope: how TSUKIHIME -A piece of blue glass moon- (PS4, CUSA47999, app 01.01) decides, every
60 Hz frame, whether the scenario script may run: what each message end mode (`)`, `@n`,
`@e`, `@k@e`) waits for, the click wait, the auto-mode countdown, skip / fast-forward, the
`_WTTM` / `_WTSV` timers and `_WTTM`'s second argument, `_WNTY`, `_VCWT` and `_VPL2`, the
`_FADS` / `_STCF` / choice / system-state waits, and the skip input of script movies
(`_TOMP` / `_TOMW`). It answers FORMATS.md section 7 P0 item 2, textbox.md open question 3,
RUNTIME_REQUIREMENTS 4.11 "skip input during script movies", and the reader of `_STCF` (FORMATS
P1 item 8, layers.md open question 9).

Python reference: `tools/tsuki/wait_model.py`. Tests: `tools/tests/test_wait_model.py` (29
tests, about 8 s with the game, unicorn and ffprobe; without the game 21 pass and 8 skip).
Scratch: `scratch/wait_model/` (`dump.py` disassembly helper, `dis/*.asm`, `cov_emu.py`,
`frame_order.py`, `frames_emu.py`, `worked.py`, `census.py`, `logs/`).

Confidence labels:

- **[V-emu]**: the game's own function was run under unicorn on random states (every live bit,
  timer, input and mode combination) and every global it writes was compared with
  `tsuki.wait_model`: 0 mismatches. 0xA2630 and 0xA0B70: 3,000 random cases, every
  instruction of their live paths executed (5.5). 0xCA070: 400 synthetic text blocks.
  0x5E530 (movie update): 1,500 random records.
- **[V-frames]**: the game's ADV input flow 0x60710 itself, with the real 0xA0B70 and 0xA2630
  inside it, was run frame after frame on 300 random 160-frame scenarios (48,000 frames:
  script of `_ZM` / `_WTTM` / `_WTSV` / `_VCWT` / `_WNTY`, random decide / d-pad / mouse /
  wheel input, auto and latched skip switched mid-run, 1..3 vsyncs, random config) and every
  modelled global was compared after every frame with `tsuki.wait_model.frame`: 0 mismatches
  (section 1.1). This covers every statement below about which frame something happens in.
- **[E]**: read from the disassembly (text VA; file offset = VA + 0x2280; data VA = file
  offset - 0x365FA0 + 0x600000).
- **[V-data]**: counted on all 549 scripts (`python -m tsuki.wait_model census`).
- **[SIM]**: frame numbers produced by `tsuki.wait_model.Simulator`: the two per-frame
  functions and their order are the [V-emu] / [V-frames] ones; voice, movie and transition
  latencies around them are [H].
- **[H]**: hypothesis.

**Revision after review (this version):** the first version ran 0xA2630 before Adv_Update in
every frame. The eboot does the opposite (1.1). The per-function models were right and are
unchanged. The frame-level statements in sections 1, 5.2, 5.4, 6, 8, 9 and 12 are now corrected
and tagged [V-frames]. Two smaller fixes: the movie start SE id set (11.2) and the `_STCF`
mask 0x20 condition (10.2). Section 17 lists what changed.

---

## 1. Overview

The ADV process (0x68A70) runs once per 60 Hz frame. Its part that decides about waiting is the
ADV input flow 0x60710 (adv_state.md 8), which calls two functions in a fixed order:

```
0x60710 (per frame)
  input words, mouse flags, 0xDF29E0 = d-pad down newly pressed (no HUD menu open)
  auto / skip toggles, fast-forward hold -> step (1, or 64 while skipping) -> 0x78AF80
  read-only rule before the frame (0x612D0)
  n = min(vsync count, 3) * step
  1. 0xA0B70(n, mouse_flags, input)   Adv_Update: every wait test; when nothing waits,
                                       Script_Step (0x95E60, called at 0xA1DAD) runs
                                       statements up to a yield
     read-only rule after the update (0x61B04)
  2. 0xA2630(n, mouse_flags, input)   "tick_reveal": text reveal, click-to-complete,
                                       _WTTM countdown and click cancel, auto countdown,
                                       message revealed -> wait bit 0x40
0x57980                                layer / transition update (ends the _FADS wait)
0x534A0                                window fade and text fade (T, step 0x1653E24)
```

Everything that blocks the script is in a handful of globals (section 2). The script runs only
when the wait word 0x1653E00 is 0, the `_WTTM` counter and the auto countdown are 0, and no
screen fade is in state 1 or 2 (0xA1356 -> 0x42680) [V-emu].

Because Adv_Update runs first, a statement run by the script starts its wait in the same frame.
For a `_ZM` that is the first reveal step; for a `_WTTM` it is the first countdown step. A
wait that 0xA2630 ends is acted on by the next frame's Adv_Update. That covers a reveal that
completes, a timer that reaches 0 or is cancelled by a click, and an auto countdown that
reaches 0 [V-frames].

**One message, one click (default config, 60 Hz):** `_ZM` sets bits 0x1 (reveal), 0x100
(displayed), 0x400 (click wait), and 0x4 when a voice started. 0xA2630 advances the reveal head
by 1000 / (60 * 35) = 0.476 counters per frame, starting in the `_ZM`'s own frame. When the last
glyph is fully visible, it replaces bit 0x1 by 0x40 and arms the auto countdown: 2200 ms = 132
frames, or 3000 ms = 180 frames on the last message of a page. A decide press in a later frame
is seen by that frame's Adv_Update. The update clears 0x441 and 0x100 and runs the script. If
the next statement is a `_ZM`, the 0xA2630 of the same frame already makes that message's
first reveal step [V-frames].

### 1.1 Frame order: evidence [E + V-frames]

- **Static:** each function has exactly one reference in the text: `call 0xA0B70` at 0x61AED
  and `call 0xA2630` at 0x61A92 (linear sweep of the whole text, `scratch/wait_model/
  frame_order.py`). On the normal path, 0x61A1E tests the backlog word 0x17DB236 and
  `0x61A3A je 0x61ADA` leads to the Adv_Update call at 0x61AED. Execution continues at 0x61AF2,
  where the read-only rule runs (0x61B04..0x61B32). It then passes the window-hide and
  click-wait HUD tests (0x61B37..0x61D1C), reaches `0x61D21 .. 0x61D2F jmp 0x61A5D`, makes the
  0xA2630 call at 0x61A92, and returns at 0x61AD9.
- **Emulation of the order:** `wm.emu_frame_order()` runs the eboot's 0x60710 with the real
  0xA0B70 and 0xA2630. The small helpers they share with 0x60710 (0xD47C0, 0x423D0, 0x5C660,
  0xE3A10) run natively; every other direct callee is stubbed. In normal, auto and latched-skip
  mode, the code hooks on the two entry points record 0xA0B70 first (returning to 0x61AF2),
  then 0xA2630 (returning to 0x61A97).
- **Emulation of whole frames:** `wm.emu_check_frames()` drives that emulated 0x60710 over
  multi-frame scenarios. Script_Step runs a small statement interpreter (`MiniScript`: `_ZM`
  with end mode, glyph count, voice and read flag; `_WTTM`; `_WTSV`; `_VCWT`; `_WNTY`) on the
  emulator's memory, and 0xCA070 answers from the message's glyph records. The same
  interpreter runs on the model state through `wm.frame`. All 300 scenarios (48,000 frames,
  `scratch/wait_model/frames_emu.py`, 349 s) give 0 mismatching frames across every modelled
  global, the Script_Step and voice-stop calls, and the call order. They include 2,087 click,
  1,913 no-click, 1,666 skip and 127 auto advances, 659 reveals completed by a click, 97 timers
  cancelled by a click, and 418 read-only skip stops. **Negative control:** the same harness
  with the first version's order (0xA2630, then Adv_Update and the script) mismatches in
  1,993 of 9,600 frames (60 scenarios). The first mismatch is in frame 0 of scenario 0: a timer
  that the script starts, whose countdown the eboot's 0xA2630 already runs in that frame (e08:
  model 6, eboot 0). Results: `logs/frames_emu.json`.
- **Paths not covered:** the frame check runs only the normal path of 0x60710: backlog closed,
  no rewind, no HUD menu, no fast-forward hold, no R2 analog, mode words 0..3.
  - While the backlog is shown (0x17DB236 != 0), Adv_Update is not called at all. 0xA2630
    still runs, with n = min(vsync, 3) * (0x3CFE0() <= 0.0) (0x61A40..0x61A50).
  - While rewinding (0x17DB234 != 0), Adv_Rewind 0xA2020 replaces Adv_Update and 0xA2630
    still follows (0x61A17 -> 0x61A54 -> 0x61A5D) [E].
- **Read-only rule between the calls (0x61B04..0x61B32) [E, V-frames]:** the rule stops skip
  when skip is active, config +0x30 != 0, the current line is unread (0x75C90 returns 0) and
  the R2 analog value is 0. It clears 0x17DB232 and sets r15w = 1. Normally that happens when
  Script_Step has just started an unread `_ZM`. In that frame 0xA2630 therefore gets
  n = min(vsync, 3) and skip active 0. The step global 0x78AF80, which 0xA2630 reads for its
  fast path (0xA264F, 0xA2AC4), is still 64, so the new message's first reveal iteration
  advances 60 x speed (28.6 counters at the default speed) (`test_read_only_rule_after_update`).
- **Why the e7c latch exists:** a click advance sets e7c = 1 (0xA10F6 path). The next `_ZM`
  or `_WTTM(ms,0)` runs in the same Adv_Update, and the 0xA2630 that follows in the same frame
  sees the same newly-pressed edge. Its reveal-click test (e7c compared at 0xA27BF / 0xA27E4)
  and timer-cancel test (0xA28C8) both require e7c == 0. So the press that dismissed a message
  neither completes the next one nor cancels the next timer. e7c is cleared by the first Adv_Update in which neither
  decide nor the left mouse button is held (0xA0BA0) [V-frames,
  `test_frame_order_click_advance_starts_next_message`].

---

## 2. State [E; all fields round-trip in the emulation, V-emu]

| global | type | name in `WaitState` | meaning |
|---|---|---|---|
| 0x1653E00 | u64 | `e00` | wait bits (section 3) |
| 0x1653E08 | i16 | `e08` | `_WTTM` / `_WTSV` frames left |
| 0x1653E0E | i16 | `e0e` | auto countdown (frames) |
| 0x1653E18 | i32 | `e18` | 1 = the timer ignores clicks (`_WTTM` 2nd argument != 0) |
| 0x1653E24 | f32 | `e24` | text fade step (> 0 while the text fades in; `_ZM`, `_CLO4`, `_CLO5`) |
| 0x1653E30 / 0x1653E40 | u64 / u8[40] | `e30`, `e40` | `_STCF` layer mask and per-layer condition mask |
| 0x1653E70 | u64 | `e70` | message flags: bit 0 reveal, bit 2 voice wait (ORed into e00 by `_ZM` / `_MSAD`) |
| 0x1653E7C | i32 | `e7c` | "advance button still held": set by a click advance, cleared when neither decide nor the left mouse button is held |
| 0x1653E80 / 84 / 88 | f32 | `head`, `tail`, `width` | reveal head, tail, fade width (textbox.md 8.1) |
| 0x1653DF8 | i16 | `frame_ctr` | free-running counter += n (only the unused 3-argument `_WTTM`) |
| 0xDF29E0 | i32 | `adv_key` | d-pad down newly pressed this frame (cleared at 0x60804, set at 0x609F1 to bit 14 of the newly-pressed word when 0x6EAB0() == 0 [meaning of 0x6EAB0 H: no HUD menu open]) |
| 0x86AA24 | i32 | `new_page` | set when a message is dismissed; the next `_ZM` then calls 0x75CD0(1) (new block slot) |
| 0xDF0734 / 0xDF0730 | i32 / f32 | `window_target`, `window_alpha` | message window target / fade |
| 0x781AB0[0] | i32 | `read_sel` | 1 = current line already read (reveal speed config +0x22 instead of +0x20) |
| 0xDD8E50 | u16 | `voice_state` | 0 idle, 1 loading, 2 playing (0x9210) |
| 0xDF0774 | i32 | `fade_state` | screen fade (`0x426A0`); 1 / 2 block the script (0x42680) |
| F799 (0x164B80E) | i16 | `f799` | `_WNTY`: 1 = last message of the page |
| F823, F859.. | i16 | `f823`, `dim` | message counter of the page; dimming timers of earlier messages |
| F988 | i16 | `f988` | != 0: fixed reveal step 0.675 |
| F1103 | i16 | `f1103` | window was closed (`_CLO2` / `_CLO3`); reset by `_ZM` |
| F1123 | i16 | `f1123` | script switch: keep the voice when the text advances |
| 0x17DB230 / 232 / 234 / 236 | u16 | `AdvMode` | auto (bit 0) / latched skip (bit 1); skip active; rewind; backlog shown |
| 0x78AF80 | i32 | `AdvMode.step` | frame multiplier (64 while skipping) |
| config 0x66C578 | | `Config` | +0x20 / +0x22 text speeds, +0x2E / +0x9E auto times, +0x30 FF read-only, +0x4E interrupt voice, +0x9C / +0x9D page option |

Mouse flags (esi, = 0x178EC54): bit 0 left button held, bit 1 left button newly pressed.
Mouse wheel 0x178EC5C = sum of the wheel deltas read this frame (Pointer_Update 0xCF240,
`sceMouseRead` record +0x18) [E].

---

## 3. Wait bits of 0x1653E00 [E writers by exhaustive reference scan; V-emu readers]

| bit | set by | cleared by | effect |
|---|---|---|---|
| 0x1 | `_ZM` / `_MSAD` (copy of E70 bit 0), History_Restore | 0xA2630 when the reveal is done (replaced by 0x40) | reveal runs |
| 0x4 | `_ZM` / `_MSAD` when a voice plays and the end mode is not `@n` (E70 bit 2); `_VCWT` while a voice is loading / playing (0x9AEC2) | Adv_Update when the voice is idle (0xA0BBB), on a click or skip (0xA1045, 0xA119B) | auto / no-click advance waits for the voice; plain voice wait |
| 0x8 | `_FADS` (0x81707) | 0x57980 when the transition progress (0xE20CD0) reaches 0 (0x58EE8) | script blocked during the transition |
| 0x10 | `_SELD` (0x9B73E), `_SELS` / `_SELA` (0xB83C9, 0xB84E9) | choice code 0xA6180 / 0xA6940 / 0xA6AD0 | choice menu open (system_flow.md 5) |
| 0x20 | `_STCF` (0x84106, 0x84F12, 0x85E56) | Adv_Update when every flagged layer's condition is false (0xA0C9F) | layer wait (section 10.2) |
| 0x40 | 0xA2630 (0xA2AB2) | Adv_Update on advance | message revealed: waiting to be dismissed |
| 0x100 | `_ZM` / `_MSAD`, History_Restore | Adv_Update on advance (then E70 = 0, `new_page` = 1) | a message is displayed |
| 0x200 | the enter callbacks of system states 0, 1, 2, 7, 9, 10, 12 (0x5CED0, 0x5D300, 0x5C030, 0x5F880, 0x5F980, 0x5F7B0, 0x5D400) and 0x5DD50 / 0x5E010 / 0x5FEF0, History_Restore (0x7D53F), 0x7EBCB / 0x7EC8C | their update / exit callbacks, `_FADS`, `_FADE` (0x7F7E0) | script blocked while a system screen or fade state runs (the movie state 8 does not set it) |
| 0x400 | `_ZM` / `_MSAD` with end mode 0; History_Restore | Adv_Update on advance | click wait; arms the auto countdown; HUD click icon |

**Dead in v01.01** (tested by Adv_Update / 0xA2630 but no code sets them; every write to 0x1653E00..0x1653E03, 0x1653E0A, 0x1653E0C, 0x1653E10, 0x1653E1C, 0x1653E28 and 0x1653E68 is a clear, found by the reference scan `scratch/wait_model/refs_e00.txt` plus the `lea`-based accessors `leause.py`): 0x80, 0x800 (with 0x8: "skip ends the fade"), 0x1000 (a movie wait with a cancel skip gated by F961), 0x2000 (a click / auto wait with voice), 0x10000 (wait for layer motions, 0xEA070 per layer), 0x20000 (wait for a movie to start, 240-frame timeout 0x1653E28), the timer 0x1653E10 (deadline on timer 5), the voice timeout 0x1653E1C (0x96F0 elapsed-ms test), the pair 0x1653E0A / 0x1653E0C, the reveal-all byte 0x174DE00 and 0xA0B10 (an auto countdown setter with no caller). F953 / F955 / F961, read by the dead movie path, have no writer. A port does not need any of these. `tsuki.wait_model.dead_state_ok` checks that a state does not use them.

---

## 4. Message end modes

### 4.1 Syntax and counts [E VA 0x92458..0x924B0, 0x92BC0..0x92C11; V-data]

The `_ZM` / `_MSAD` copy loop (text.md 5.2) stops at `)`, `@n` or `@e` (or a full 0x300-byte
buffer). `@n` / `@e` and what follows are dropped. Then:

```
mode(_ZM)   = 1 if "@n";  2 if "@e";  ')' or full: 2 if (page_latch and F799 != 1) else 0
mode(_MSAD) = the same, with the latch value of the page (not re-latched)
page_latch  = latched by _ZM when F823 <= 1:  config[+0x9D if read else +0x9C] == 1   (0x56270)
```

| argument end | count | mode | e00 bits set by `_ZM` | waits for |
|---|---:|---:|---|---|
| `)` (WNTY 0 or 1, page option off) | 42,121 (26,389 / 15,732) | 0 | 0x1, 0x100, 0x400 (+0x4 with voice) | a click; auto / skip after the countdown |
| `)`, page option on, WNTY 0 | | 2 | 0x1, 0x100 (+0x4) | nothing: reveal end and voice end |
| `@e` (always as `@k@e`) | 469 (464 WNTY 0, 5 WNTY 1) | 2 | 0x1, 0x100 (+0x4) | reveal end and voice end |
| `@n` | 678 | 1 | 0x1, 0x100 (never 0x4) | reveal end only; sets the continue byte 0x16541F4 |

[V-data] What follows: the next message after an `@n` is an `_MSAD` in 677 / 678 cases (the
other is `09_01_CIEL09_2_1` statement 1876 `_ZM6c450($022928^@n)`, followed by a `_ZM` at
1971); `_MSAD` appends to the same buffer and block, so `@n` + `_MSAD` is one message revealed
in parts with statements (STGS 151, FADS 143, WTTM 101, ...) in between. The next message
after `@k@e` is a `_ZM` (388), a choice (80) or the script end (1); the statement right after
it is a `_WTTM` (199), a message (123), a choice (75) or a `_VCWT` (47).

### 4.2 The `_ZM` / `_MSAD` wait part [E 0x92380, 0x92B60; V-emu through 0xA2630 / 0xA0B70]

```
_ZM:
  if new_page: 0x75CD0(1)  (save head/tail, E70 = 0, toggle the block slot); new_page = 0
  text fade: if e24 <= 0: (T < 1 or F1103) ? (T = 0, e24 = 1/18) : (T = 1, e24 = 0); F1103 = 0
  continue byte 0x16541F4 = 0; reveal-all byte 0x174DE00 = 0
  read = readmax test (textbox.md 3.1.3); 0x56150(0, read) -> read_sel; latch on F823 <= 1
  head = tail = 0
  voice: if pending and allowed (sound_runtime 7): VoiceRequest; if mode != 1: e70 |= 4
  e70 |= 1;  if mode == 1: continue byte = 1
  e00 |= (mode == 0 ? 0x400 : 0) | 0x100;  if e70: e00 |= e70
_MSAD:
  continue byte = 0; append (no new slot); head = tail = (largest counter of the old block) + 1
  voice: if this _MSAD started one, or the message's _ZM did (0x86AA28 >= 0), and mode != 1:
         e70 |= 4
  e70 |= 1; same e00 update as _ZM
```

So a voiced `_ZM(...@n)` + `_MSAD(...)` waits for the voice at the end of the `_MSAD` part
(0x92E93).

### 4.3 "Display End of Page Instantly" (config +0x9C unseen, +0x9D seen)

With the option on, every message that is not the last of its page (`_WNTY(0)`) ends in
mode 2: no click. The next message follows in the first frame after this one is revealed and
its voice has ended [V-frames]. The last message of the page (`_WNTY(1)`) still waits for a click. The menu label is the
adv_state.md 9.2 mapping (V-emu of Config_FromMenu); the effect is [V-emu]. Worked example in
12.1.

### 4.4 `@k`

`@k` is ignored by the layout and by the wait logic: it is not a wait. All 469 uses are
`$N@k@e`, i.e. "no click" messages. Its only reader is the HUD page history (0x55D01 /
0x55EEF): when a logged page message ends in `@k`, the dimming of earlier messages (textbox.md
5.8) skips over it to the next message that does not end in `@k` [E; visual result H].

### 4.5 `_WNTY(n)` [E 0x972F5 -> 0x53600]

`F799 = (n != 0)`, also word 0xDF08DC (stored in history records, 0x7B17D). F799 = 1 marks the
last message of a page. It selects (a) whether the page option of 4.3 applies (F799 != 1) and
(b) the auto countdown: config +0x9E "Auto-Mode Advance Page Speed" (3000 ms) when F799 == 1,
else +0x2E "Auto-Mode Wait Time" (2200 ms) (0xA2947, 0xA2A6E). The ADV process start resets it
(0x68ED0, 0x68F34). `_SELD` sets F799 per choice line (0x9B281).

---

## 5. Per-frame algorithm

### 5.1 Click sources [V-emu]

| test | used by | sources |
|---|---|---|
| reveal / message / plain voice wait | 0xA27ED, 0xA10E5 / 0xA1CB2, 0xA0FF9 / 0xA1F45 | `adv_key` (d-pad down), mouse left newly pressed, action 0 (decide) newly pressed, mouse wheel < 0; the voice wait also accepts skip active |
| timer cancel | 0xA28D1 / 0xA2B6E | `adv_key`, mouse left newly pressed, decide newly pressed (no wheel) |

0xE3A10 (`& 0x200`, also tested everywhere) is a stub returning 0. 0xCF130 (called on every
click) is an empty function. Decide = the action-0 mask of preset 0 (0x20, adv_state.md 10.2).

### 5.2 0xA2630 (`tick_reveal`) [V-emu]

```
fast = step >= 61;  mult = fast ? 60.0 : 1.0;  frame_ctr += n
repeat n times:
    if !(e70 & 1) and !(e00 & 1): continue
    if window_target and !log_shown and (e24 > 0 or window_alpha < 1) and F1103 == 0:
        continue                                   # wait for the window / text fade-in
    window_target = 1
    if F988 == 0:
        sp = speed(cfg, read_sel); width = fade_width(...)        # textbox.md 8.1, float32
        if sp >= 768: head = tail = 768; goto click_test
    else: sp = 0.675
    if head < 768: head = sp * mult + head
    if head - width > tail: tail = head - width
  click_test:
    if e7c == 0 and click: head = 76800; if F823 >= 2: F859..F[859+F823-2] = -1 (dim at once)
if e08 > 0:
    e08 = max(0, e08 - n)
    if e18 <= 0 and e7c == 0 and timer_click: e08 = 0
if skip_active > 0 or latched skip:  e0e = min(e0e, 60)
if e0e > 0:  e0e = max(0, min(e0e, auto_frames(F799)) - n)
done = 0xCA070(slot+4) [& 0xCA070(slot+6) if 0x16541D8 & 2] [& 0xCA070(slot+8) if & 4]
if done:
    if e00 & 1:
        if e00 & 0x400: e0e = auto_frames(F799)       # trunc(ms / 1000.0f * 60.0f)
        e00 = (e00 & ~0x41) | 0x40
    e70 &= ~1
if fast and (rewind or skip_active > 0 or latched skip): e0e = 0
choice open (e00 & 0x10) ? (window_target = 1; 0xA6180(n)) : 0xA60D0()
```

`auto_frames`: 2200 ms -> 132, 3000 ms -> 180 frames. A reveal step happens n times per frame:
in skip mode (step 64) 64 iterations at 60 x speed finish any message in the first frame.

**Reveal done (0xCA070)** [V-emu, 400 blocks]: the alpha of the **last** record of the block
with non-zero flags (ruby readings included) is exactly 1.0, where alpha = 0 if ctr > head,
1 if tail > ctr or head - tail <= 0, else (head - ctr) / (head - tail). An empty text (block
not allocated) returns -1 = done. A message of n plain glyphs is therefore done in the first
frame where tail = head - 2.857 >= n (35 ms per counter): `ceil((n + 2.857) / 0.47619)` frames
of reveal, for example 48 frames for 20 glyphs.

**A click during the reveal** sets head = 76800 after that iteration's tail update, so the
tail is still behind and the message is done one reveal step later. At 60 Hz (one iteration
per frame), a press in frame c completes the text in the 0xA2630 of frame c + 1, and a second
press dismisses it. With n >= 2 iterations per update (30 Hz updates, vsync 2), the next
iteration of the same 0xA2630 completes the text and bit 0x40 is set in frame c. Adv_Update has
already run in that frame, though, and in frame c + 1 the press is no longer newly pressed. **One
press therefore never both completes and dismisses a message**; a second press is always needed
[V-frames, `test_one_press_at_30hz_only_completes_the_reveal`]. While skipping, the reveal
finishes in the `_ZM`'s own frame without any press (section 9).

### 5.3 0xA0B70 (`Adv_Update`) [V-emu]

```
if !(mouse & 1) and !(decide held): e7c = 0
if e00 & 4 and voice_state == 0: e00 &= ~4; if e0e < 0: e0e = 0
if e00 & 0x20:                                   # _STCF
    for l in 0..39 with e30 bit l: if !0x4AB80(l, e40[l]): clear bit l
    if e30 == 0: e00 &= ~0x20
# plain voice wait (_VCWT, or the voice bit left by a skip advance)
if !(e00 & 1) and !(e70 & 1) and e00 & 4:
    if click or skip_active:
        if (F1123 == 0 and cfg+0x4E) or skip_active: stop voice (0x8EA0)
        e70 &= ~4; e00 &= ~4
# message wait
if e00 & 0x40 and e08 == 0:
    if click:                                    # 0xA10F6
        adv_key = 0; e0e = 0
        if e00 & 0x100: e00 &= ~0x100; e70 = 0; new_page = 1
        if (F1123 == 0 and cfg+0x4E) or skip_active: stop voice
        e00 &= ~4 (and e70 &= ~4) if e00 & 4
        e00 &= ~0x441; e7c = 1
    elif e00 & 0x400 and !auto and !(skip_active > 0): keep waiting
    elif (!(e00 & 4) and e0e == 0) or (skip_active and e0e == 0):    # 0xA1CFF / 0xA1FBC
        if e00 & 0x100: e00 &= ~0x100; e70 = 0; new_page = 1
        e00 &= ~0x441                            # no e7c, no voice stop here
if e00 == 0x500:   advance on click, skip, latched skip, or auto with e0e <= 0   (0xA11E6)
elif e00 == 0x100: e00 = 0; new_page = 1                                         (0xA11CD)
if R2 held and F1123 == 0 and cfg+0x4E: stop voice                               (0xA123D)
[next-scene request 0x16541F8 == 1: adv_state 5.2]
if e00 == 0 and e08 == 0 and e0e == 0 and fade_state not in (1, 2): Script_Step  (0xA1356)
```

The two exact-value cases are defensive: History_Restore always ORs E70 bit 0 in (0x7DBCC),
so a restored page arrives as 0x501 / 0x141 and follows the normal path.

### 5.4 Message state machine [V-emu per function, V-frames for the frame numbers]

Frame f is the frame whose Adv_Update runs the `_ZM`; r is the frame whose 0xA2630 sets 0x40.

| state | e00 (besides 0x100) | each frame | leaves when |
|---|---|---|---|
| **R** revealing | 0x1 [+0x4] [+0x400] | n reveal steps in 0xA2630, the first in frame f itself (stalled while the window opens or the text fades in); a click jumps the head to the end | last glyph fully visible in the 0xA2630 of frame r -> **S** / **N** (and e0e = auto time if 0x400) |
| **S** shown, click wait | 0x40, 0x400 [+0x4] | Adv_Update tests the wait first; then 0xA2630 counts e0e down; HUD click icon alpha += 0.1 per frame up to 1 while 0xCA690 reports the text complete and 0x400 is set, else 0 (0x53FC4..0x5400C) | click (the Adv_Update of the press frame, at the earliest r + 1) -> **A**; auto: Adv_Update sees e0e == 0 and no voice bit, at the earliest frame r + countdown + 1 -> **A**; skip: e0e is 0 already at the end of frame r (full speed), so frame r + 1 -> **A** |
| **N** shown, no click (`@e`, `@n`, page option) | 0x40 [+0x4] | nothing | click -> **A**; e0e == 0 and no voice bit -> **A**: frame r + 1 when there is no voice (`test_nowait_advances_the_frame_after_the_reveal`: a 20-glyph `@e` message started in frame 0 is revealed in frame 47 and advanced in frame 48) |
| **A** advance | - | clears 0x441, 0x100 (E70 = 0, new_page = 1); a click also stops the voice (option / skip) and sets e7c | at once: the script runs in the same Adv_Update if nothing else waits, and a `_ZM` started there makes its first reveal step in the same frame (**R**) |
| **V** voice tail | 0x4 only | | voice idle, click, or skip (skip / option stop the voice) |

**V** occurs after a skip or auto advance of a voiced message (they do not stop the voice in
the advance itself) and after `_VCWT`. In skip mode, the plain voice wait and the advance happen
in the same Adv_Update: the voice is stopped first, in frame r + 1 [V-emu, V-frames,
`test_skip_mode`].

### 5.5 Emulation coverage

`scratch/wait_model/cov_emu.py` (2,500 random cases) records every instruction executed in
0xA0B70 and 0xA2630. Never executed: exactly the dead paths of section 3 (0xA0C20..0xA0C99,
0xA0D20..0xA0FD6, 0xA105E..0xA10B4, 0xA1217..0xA1236, 0xA13AB..0xA13D0, 0xA1C72..0xA1CAD,
0xA1E92..0xA1F40, 0xA284D..0xA2857, 0xA2921..0xA2936), the next-scene loop (0xA127C..0xA1351,
0xA13D5..0xA1C6D, adv_state 5.2), the post-step `F997 == 1` call (0xA1DC3), the 0xA23F0 request
(0xA2B40) and `__stack_chk_fail` (`logs/emu_coverage.log`). The per-function check calls each
function on its own; their composition inside 0x60710 is the [V-frames] check of 1.1.

---

## 6. Timers: `_WTTM`, `_WTSV`, `_WTTR` [E 0x9AC1A, 0x9AF36, 0x9AFD1; V-emu countdown]

| command | count | frames (e08) | e18 (1 = clicks ignored) |
|---|---:|---|---|
| `_WTTM(ms)` | 8 | max(1, (3 * ms) / 50) (C truncation; = ms * 60 / 1000) | 0 |
| `_WTTM(ms, 0)` | 57 | same | 0 |
| `_WTTM(ms, 1)` | 14,763 | same | 1 |
| `_WTTM(ms, n, x)` | 0 | max(1, frames - frame_ctr) relative to 0x1653DF8 when x is not empty | from n |
| `_WTSV()` | 28 | 6 (default 100 ms) | 1 (default) |
| `_WTTR(ms, n)` | 0 | max(1, 3 * ms / 50) | n != 0 |

**`_WTTM`'s second argument is "clicks ignored", not "skippable":**

- With 1 (14,763 of 14,828 statements), a click does nothing.
- With 0 or no second argument, a click ends the wait. The click can be decide, d-pad down or a
  left click, but not the wheel, and it is ignored while e7c is set. The cancel happens in
  0xA2630 (e08 = 0), after that frame's Adv_Update has already tested e08, so a press in frame
  c lets the script continue in frame c + 1 [V-emu, V-frames, `test_wttm_second_argument`].
- The countdown runs in 0xA2630, after Adv_Update, and therefore also in the frame of the
  `_WTTM` itself. `_WTTM(1000)` executed by the script in frame t sets e08 = 60; the 0xA2630 of
  frame t makes it 59, the one of frame t + 59 makes it 0, and the Adv_Update of frame t + 60
  runs the next statement: 60 frames [V-frames].
- Skip multiplies the decrement by the step. While skipping, `_WTTM(1000)` reaches 0 in its own
  frame (60 - 64), so the next statement runs in frame t + 1. `_WTTM(2200,1)` goes 132 -> 68 ->
  4 -> 0 and lets the next statement run in frame t + 3.
- Auto mode does not shorten timers.

The `_WTTM` handler first commits a pending layer change (F991 -> history kind 9, adv_state 2.3).

ms values [V-data]: 400 (4,407), 1000 (1,806), 200 (1,320), 800 (1,156), 600, 2000, ...; the
largest are 300,000 (3), 30,000 (1), 12,000 (1).

---

## 7. Voice waits [E handlers; V-emu Adv_Update parts]

- **Message voice** (`_VPLY(name, hex)` queues; the next `_ZM` / `_MSAD` starts it,
  sound_runtime.md 7): bit 0x4 unless the end mode is `@n`. Click-wait messages do not need it
  for a click; auto mode and no-click messages wait until the voice is idle.
- **`_VPL2`** (16 uses: 12 in `00_02`, one each in scripts 315, 492, 494, 514): plays the voice **at once** (0x9A05D..0x9A111,
  same gate as `_ZM`, also blocked by skip and fast-forward) and clears the pending voice; it is
  not an alias of `_VPLY` at run time (sound_runtime.md 3.6 has this; script.md 4.4 does not).
- **`_VCWT`** (75): if the voice state is not 0, bit 0x4 and yield; the plain voice wait of 5.3
  ends when the voice ends, on a click (which stops the voice when Interrupt Voices is on and
  F1123 == 0) or when skip is active (always stops it). The statement before a `_VCWT` is a
  message (53), a `_VPL2` (12), a `_FADS` (6) or a `_WTTM` (4) [V-data].
- **Stop on advance**: every click advance stops the voice when `(F1123 == 0 and config +0x4E)`
  or skip is active; holding R2 stops it too (0xA123D); auto and no-click advances never stop it
  (they wait for it instead). F1123 is set by 2 statements (`_WKST(F1123,...)`) [V-data].
- Voice state 1 (loading) counts as busy, so a wait also covers the load latency.

---

## 8. Auto mode [V-emu, V-frames]

Auto (0x17DB230 bit 0, action 13 = L1) changes only the message wait: a click-wait message
advances when its countdown e0e is 0 and no voice bit is set.

- **The countdown** starts at the reveal end, whether or not auto is on. Its length comes from
  F799: 132 frames (2200 ms) inside a page, 180 frames (3000 ms) at the page end;
  `trunc(ms / 1000.0f * 60.0f)`, config max 10,000 ms.
- **When the advance comes:** the countdown is armed at the end of the 0xA2630 of the reveal
  frame r and reaches 0 in the 0xA2630 of frame r + countdown. The Adv_Update of frame
  r + countdown + 1 is the first to see 0. The countdown also runs while the voice plays, and
  the voice bit is cleared by the first Adv_Update that sees the voice state 0. The advance is
  therefore in frame `max(r + countdown + 1, first frame whose Adv_Update sees the voice idle)`
  [V-frames; `test_auto_mode`: 20 glyphs, no voice, advance 133 frames after the reveal frame;
  examples in 12.1 and 12.3].
- Turning auto on during a click wait advances as soon as the already running countdown is 0.
- Timers, `_VCWT`, `_FADS`, `_STCF` and movies are not shortened. A click still advances at
  once.
- Restored pages (0x500) advance when e0e <= 0.

---

## 9. Skip and fast-forward [V-emu and V-frames for the wait parts; adv_state.md 6 for the toggles]

While skip is active (latched skip R1, or R2 held; 0x17DB232 > 0) the step is 64 (R2 analog:
1..64):

- **one message per frame** [V-frames]:
  - The `_ZM` runs in the Adv_Update of frame f. The 0xA2630 of the same frame reveals the whole
    message (64 iterations x 60 x speed), clamps e0e to 60 and, at full speed (step global
    0x78AF80 >= 61), sets it to 0.
  - The Adv_Update of frame f + 1 advances the message ("skip" advance; with a voice bit, the
    plain-voice block stops the voice first, in the same update). It runs the next `_ZM`, which
    is revealed in that frame's 0xA2630.
  - In `logs/00_00_skip.log`, `$000107` is started and revealed at 17 and advanced at 18, when
    `$000108` is started and revealed.
- `_WTTM` / `_WTSV`, `_FADS` (transition speed 0x7A6180 = step) and the layer counters run 64
  times faster; `_WTTM(n, 1)` is not "skipped", it just ends 64 times sooner;
- voices are not started while skipping when the line is read or FF is not read-only
  (sound_runtime 7); `_VPL2` never starts one while skipping;
- the read-only rule (config +0x30) stops skipping on an unread line before the frame
  (0x612D0) and after Adv_Update when R2 is not held (0x61B0D) (adv_state 6). In the second
  case, the 0xA2630 of that frame runs with n = min(vsync, 3) and skip active 0, but with the
  step global still 64 (1.1) [V-frames];
- **movies are not skipped by skip mode**: `_TOMP` clears 0x17DB232 (0x9ABBD, except
  `phaseout` id 7) and the movie state's enter callback clears the latched-skip bit and saves
  0x17DB230 (0xDF2998), which its exit restores (0x5E35C, 0x5F796). Skip resumes after the
  movie.

---

## 10. Other waits

### 10.1 `_FADS` [E 0x81520, 0x58CC1..0x58EE8; H about the frame of the clear]

`_FADS(type, ms, rule)` starts the transition, clears bit 0x200 and sets bit 0x8; 0x57980 clears
0x808 once the progress 0xE20CD0 is 0 (compositing.md 7.2: step -1 / (ms / 16.666), +100 ms for
types 12 / 49, speed 0x7A6180 = step). Order in a frame: Adv_Update (runs `_FADS`), 0xA2630
(does not touch bit 0x8), 0x57980, progress update (0x69578). A transition reaching 0 after K updates therefore releases the
script about K + 1 frames after the `_FADS` [H +-1 frame]: `_FADS(1,0,0)` 2 frames,
`_FADS(0,1500,0)` 92 frames. Clicks never shorten it.

### 10.2 `_STCF(l [, mask])` [E 0x83E50, 0xA0C9F, 0x4AB80]

Sets bit l of 0x1653E30, byte 0x1653E40 + l = mask (absent: 0x3F) and bit 0x20, and yields.
Adv_Update keeps bit l while 0x4AB80(l, mask) is true; for the drawn layer record 0xDDB5E8 +
0x2C8 * l:

| mask bit | pending while | written by |
|---|---|---|
| 0x10 | f32 +0x150 > 0 | `_STRC` (0x84190 -> 0x4A540) |
| 0x20 | the motion record at +0x160: `0xBA910(m) != 0` **and** `0xBA890(m) == 0` (0x4ABDC..0x4ABF6: `test cl, cl; je -> return 1`). 0xBA910 = the motion time +8 (scaled) is still below the motion length 0xC2150(id) (and 0xC1C70(id) == 0); 0xBA890 = byte m+0x10 (+0x170) [names H: "playing" and "loops"] | `_STMA` |
| 0x1 / 0x2 | i16 +0x86 / +0x8C > 0 | +0x8C: 0xF73D0 (vtable) [meaning H] |
| 0x4 | i16 +0x92 > 0 | `_STRT` (0x83940 -> 0x4A4B0) |
| 0x8 | i16 +0x98 > 0 | `_STGS` (0x84960 -> 0x4A620): 4096, step trunc(-4096 / frames), frames = trunc(ms * 60 / 1000) |

All 5,179 retail uses are `_STCF(l, 8)` or `_STCF(l)` (layers.md 9): wait for the layer's `_STGS`
alpha fade. Clicks do not end it. The model's `stcf_pending` is the [E] predicate (mask 0x20 through the keys `ba910` / `ba890`);
`stgs_fade_updates(ms)` = ceil(4096 / trunc(4096 / frames)) updates [H about the update rate].

### 10.3 Choices, system states, screen fade

- Choice (bit 0x10): set by `_SELD` / `_SELS` / `_SELA`; while it is set 0xA2630 calls the
  choice update 0xA6180 every frame (system_flow.md 5.2) and the fast-forward hold is blocked
  (0x611DB).
- System bit 0x200: set by the enter callbacks of system states 0, 1, 2, 7, 9, 10, 12 (state
  table 0x781DE0, stride 0x88: +0x68 enter, +0x70 update, +0x78 draw, +0x80 exit) and cleared by
  their update / exit or by `_FADS`.
- Screen fade 0xDF0774 in {1, 2} (started by 0x426A0 from History_Restore and 0x7F510 /
  0x7F680) blocks Script_Step (0x42680) without a wait bit.
- `_FADE` (0x7F7E0) sets e08 = 1 on its waiting path (0x7F94F) [E, effect H].
- `_CLO2` / `_CLO3` / `_CLO4` / `_CLO5` set no wait bit; they change the window target / fade and
  the text fade step, which stall the next reveal (5.2 window test): after `_CLO2` / `_CLO3` the
  next `_ZM` fades the text in over 18 frames (step 1/18) and the window opens at 0.06 per frame.

---

## 11. Movies: `_TOMP` / `_TOMW` and the skip input [V-emu 0x5E530, 1,500 records]

### 11.1 Script side [E 0x96F4C, 0x9AB56]

`_TOMP(id)` (48): unless id is 7 (`phaseout`) while a next-scene jump runs (skipped), it clears
skip active and the jump flag (id != 7), sets F1103 = 1 and **F36 = 1**, and starts system state
8 on process 2: `0x5C380(2, 8, id, id == 7, 0, 0)`, whose enter callback runs at once; `_TOMP`
then yields (0x9AC15). `_TOMW` (48) yields every frame and re-runs itself while F36 != 0 (`cmovne rbx, r12`).
F36 is cleared by the state's exit callback 0x5F6F0 (0x5F75C), which also sets the movie_seen
bit (gallery.md 7) and restores 0x17DB23C / 0x17DB230 / 0x17DB23A. Statements between them run
normally: `00_01` plays the prologue with `_TOMP(4); _WTTM(2000,1); ...; _TOMW()`.

### 11.2 Movie state update 0x5E530 (record: +0x18 id, +0x30 phase, +0x34 frame, +0x3C alpha, +0x40 fade step, +0x48 skip flag, +0x4C second fade)

The enter callback 0x5E300 sets the countdown 0xDF293C = 20; the update does nothing while it
is > 1 (the first 19 updates). Then each update adds min(vsync, 3) to +0x34 and runs the phase:

| phase | does | next |
|---|---|---|
| 0 | 0xE4F00, 0x88F0 | 1 (same update) |
| 1 | wait until the player is ready (0xEE880); frame = 0, +0x38 = 1, alpha = second fade = 0, skip flag = 0; **0x5E490 (UI SE) for every id NOT in {0..7, 13, 35..45}**, i.e. 8..12, 14..34 and > 45 (0x5E6D6 `ja 0x5E968` for id > 45; 0x5E6EA `bt 0x3FF8000020FF, id`; 0x5E6EE `jae 0x5E968` = bit clear -> `call 0x5E490`); id 13 plays it instead at frame 210 of phase 3 (0x5E84D..0x5E859); then the phase-2 code in the same update | 2 |
| 2 | alpha += frames x fade-in rate (min 1); second fade += frames / 96 once alpha >= 0.5 | 3 at frame 60 |
| 3 | id 13: wait to frame 210; **id 4: until frame 21,600 (ja) / 21,900 (en) a skip press sets +0x48**; others: on | 4 |
| 4 | id 13: wait to frame 330; id 4: 0x98B0(2), 0x9EA0(1) | 5 |
| 5 | while playing: **skip press (or +0x48) -> frame = fade-out start, phase 7, step 1/60**; when the player stops: phase 6 | 7 / 6 |
| 6 | from the fade-out start frame alpha -= frames x fade-out rate; when the player has stopped: end the state (0x5C080) | end |
| 7 | alpha -= frames / 60; at <= 0: phase 6 and stop request (0xEE8C0) | 6 |

**Skip input:** cancel (0x40) or OPTIONS (0x800) newly pressed in the raw game input word
(`*0xDC7BC0 + 0x20`), not the decide button and not an action mask. **Gate:** the prologue (4)
and the openings / endings 5, 6, 35, 36, 37 can be skipped only once their movie_seen bit (G[5]
bits 2, 0, 1, 3, 4, 5; read by 0xB7CA0) is set; every other id (phase titles, `phaseout`, the
`credit00..06` cards 39..45 of `00_02`) is always skippable. A skip fades the picture out in 61
updates (60 x float32 1/60 leaves a remainder), stops the player and ends the state. The exit
callback sets F36 = 0, and `_TOMW` continues at its next run. Whether that is the same frame or
the next one depends on the order of the system-state process and the ADV process in a frame,
which was not traced [H]; the simulator assumes the next frame. Fade parameters (0x5E5AB..0x5E656, table 0x201008): id 4
fade-out start 23,040 (ja) / 23,340 (en) frames, 5: 6,420, 6: 6,300, 7: 312 with rate 1/48,
13: 540, 35: 18,960, 36: 8,880, 37: 15,240, 39..45: 618, others 420 with fade-in rate 1/48.

The prologue file lasts 384.34 s (ja) / 388.67 s (en): 23,060 / 23,320 frames, so in Japanese
the fade-out starts 20 frames before the end and in English the file ends first [V ffprobe].

`wm.emu_check_movie` compares, besides the record fields and the stop / end calls, the number of
0x5E490 calls (`wm.movie_se_calls`) for random records of phases 1..7. 1,500 records give 0
mismatches, 51 SE calls and 80 phase 1 -> 2 transitions [V-emu].

---

## 12. Worked examples (English, default config) [SIM]

Logs: `scratch/wait_model/logs/*.log` (one line per event: frame, statement, what) and
`summary.json`, produced by `scratch/wait_model/worked.py` (`python -m tsuki.wait_model
timeline <script> --policy click|auto|skip|none`), re-run with the eboot frame order of 1.1.
Glyph counters come from `tsuki.textbox`, voice lengths from voice.mrg (ceil(s x 60)), and
movie lengths from ffprobe plus 25 frames [H]. The "click" policy presses decide in the first
frame the message is shown and clickable: the frame after the reveal frame, the earliest a
press can dismiss it. A frame number is the frame whose Adv_Update runs the statement; the
`_ZM`'s first reveal step is in the same frame.

### 12.1 `00_00` (prologue narration, no voices on page 1)

| frame | stmt | event |
|---:|---:|---|
| 0 | 19 | `_FADS(1,0,0)`: 2 frames (also 53, 57) |
| 4 | 55 | `_STCF(0,8)`: the `_STGS(0,,255,1,0)` before it has 0 frames, so 1 frame (yield) |
| 7 | 58 | `_WTTM(1,1)`: 1 frame |
| 8 | 59 | `_WTTM(1000,0)`: 60 frames (a click would end it; the script would continue the frame after the click) |
| 68 | 76 | `_FADS(0,1500,0)`: 92 frames |
| 162 | 83 | `_WTTM(2200,1)`: 132 frames, clicks ignored |
| 294 | 89 | `_WNTY(1); _ZM01801($000107)`: 45 counters, click wait, F799 = 1; first reveal step in frame 294 |
| 410 | 89 | revealed, 116 frames after the `_ZM` frame: the step of frame 294, 16 frames stalled while the window opens from alpha 0, then 100 more steps (101 in all = ceil((45 + 2.857) / 0.47619)); auto countdown 180 |
| 411 | 89 | click -> advance; `_WTTM(1000,1)` runs in the same Adv_Update: 60 frames |
| 471 | 125 | `_ZM01802($000108)`: 38 counters, F799 = 0, revealed at 556 (86 steps, the first in 471), countdown 132; click at 557 |

The same page with other policies:

- **auto** advances `$000107` at 591 (= 410 + 180 + 1) and `$000108` at 869 (= 736 + 132 + 1).
  `$000108` starts at 651 and is revealed at 736 (`logs/00_00_auto.log`).
- **skip** (all read) starts and reveals `$000107` at frame 17 and advances it at 18; from
  then on, one message per frame. `_WTTM(2200,1)` takes 3 frames (14 -> 17), and
  `_FADS(0,1500,0)` takes 3.
- **none** stays on `$000107` for ever.
- **page option on** (`--page-option`): `$000108` (471, revealed 556, advanced 557) and
  `$000109` (557, 663, 664) are WNTY 0 and advance in the frame after their reveal frame.
  `$000110` (WNTY 1) waits for the click (revealed 787, click 788)
  (`logs/00_00_click_pageoption_head.log`).

Whole script (394 messages: 380 `)` `_ZM`, 6 `)` `_MSAD`, 7 `@n`, 1 `@k@e`): click 63,843
frames (1,064 s), auto 129,207 frames (2,153 s), skip 1,254 frames (20.9 s). The 8 `nowait`
advances are the 7 `@n` parts and the `@k@e` message. The first version's frame order gave
64,134 / 129,136 / 1,254. Click is shorter now because every new message gets its first reveal
step in its own frame. Auto is longer because every countdown ends one frame later.

`@n` example (statements 318..320): `_WNTY(1); _ZM0180d($000119^@n); _MSAD($000120)`.

- The first part (8 counters) starts at 1730, is revealed at 1752 (23 steps) and advances at
  1753 without a click.
- In that Adv_Update, `_MSAD` appends `$000120` and continues the reveal from counter 9
  (head = tail = 9); the 0xA2630 of 1753 makes its first step.
- The click wait (71 counters in all, F799 = 1) is revealed at 1889 (137 steps, 1753..1889) and
  clicked at 1890.

### 12.2 `00_01` (prologue movie)

1. `_CLO2; _FADS(1,0,0) x 2; _STCF(0,8); _FADS(1,0,0); _WTTM(1000,0)` take frames 0..67.
2. `_TOMP(4)` runs at frame 68 and yields. `_WTTM(2000,1)` runs at 69, in parallel with the
   movie, until 188.
3. `_TOMW` holds until the movie state ends. The script ends at frame 23,414 (390.2 s, en).

On a first playthrough, cancel and OPTIONS do nothing (movie_seen bit 2 clear). Once the
prologue has been seen, a cancel press ends the movie after the 61-update fade, and the movie
state is over by frame 155. The script still ends only at frame 190 in the simulation. The
parallel `_WTTM(2000,1)` lasts until 188; `_TOMW` runs at 189 and yields once even with F36 = 0;
the end statement runs at 190.

### 12.3 `00_02` (opening scene: voices, `_VPL2` / `_VCWT`, credit cards)

| frame | stmt | event |
|---:|---:|---|
| 8 | 81 | `_WTTM(1000,0)` |
| 68 | 86, 90 | `_VPL2(B90_00_01_0000_0,03d00)`: voice of 132 frames starts at once; `_VCWT` waits 133 frames |
| 201 | 132 | `_FADS(0,10,0)` (2), `_FADS(1,0,0)` (2), `_WTTM(4000,1)` at 205 (240), `_CLO5(2000)` at 445, `_WTTM(2000,1)` at 446 (120) |
| 566 | 170 | (auto) `_ZM($000000)`: 75 counters, voice 285 frames; revealed 729; countdown 132 reaches 0 in the 0xA2630 of 861; the voice is idle from frame 852 -> advance 862 (= 729 + 132 + 1) |
| 1872 | 229 | (auto) `$000005`: voice 283 frames, revealed 2020; the countdown would allow 2153; the voice is idle only from frame 2156 -> advance 2156 |
| 4523 | 539 | (click) `_TOMP(39)` `credit00` (8.0 s; the movie state lasts 505 frames [SIM]): `_TOMW` holds and the next statement (`_FADS(0,300,0)`, 547) runs at 5029; with a cancel press (credit cards are always skippable) it runs at 4611 |

Totals (`logs/summary.json`):

| policy | frames | seconds | note |
|---|---:|---:|---|
| click | 19,906 | 331.8 | each click stops the voice |
| auto | 34,034 | 567.2 | |
| skip | 3,448 | 57.5 | the 7 credit movies are not skipped by skip mode |
| click + cancel during movies | 17,424 | 290.4 | |

The first version gave 19,989 / 34,021 / 3,441 / 17,507.

---

## 13. What a port should do (C#)

- Keep the globals of section 2 as one `WaitState` and port `tick_reveal` and `adv_update`
  statement by statement (`tsuki.wait_model`; about 200 lines). **Call them in the eboot's order
  every 60 Hz tick** (`tsuki.wait_model.frame`, [V-frames]):
  1. input (adv_key = d-pad down newly pressed; the mode words give skip active and the step);
  2. the read-only rule before the frame;
  3. `adv_update`, and the VM until it yields when `adv_update` lets the script run;
  4. the read-only rule after the update. If it stops skip, the next call gets n = vsync and
     skip active 0, but keeps the step global for its fast path;
  5. `tick_reveal`;
  6. transition / layer update (clears 0x8 at the end of a `_FADS`);
  7. window / text fade.

  A message started by the VM gets its first reveal step in the same tick. A reveal, timer or
  countdown that `tick_reveal` finishes is acted on in the next tick. Running `tick_reveal`
  first shifts every message by a frame (12.1: 291 frames over `00_00` in click mode). It also
  lets one press both complete and dismiss a message at 30 Hz, which the game never does.
  Use float32 for head, tail, width and the speed formulas; reveal-done is the 0xCA070 rule on
  the block's records.
- While the backlog is shown, do not call `adv_update`; `tick_reveal` still runs (1.1). While
  rewinding, the rewind update replaces `adv_update`.
- Give the VM handlers their wait side effects: `_ZM` / `_MSAD` (4.2), `_WTTM` / `_WTSV` (6),
  `_WNTY` (4.5), `_VCWT` and `_VPL2` (7), `_FADS` (bit 0x8), `_STCF` (10.2), `_SEL*` (bit 0x10),
  `_TOMP` (F36 = 1, clear skip active) and `_TOMW` (repeat while F36 != 0).
- Input mapping: "click" = decide, d-pad down, left mouse press, wheel down (the timer cancel
  ignores the wheel). Keep the e7c latch: the press that dismisses a message is still "newly
  pressed" when `tick_reveal` runs in the same tick, right after the VM has started the next
  message or timer (1.1). Movie skip = cancel or OPTIONS (PC: the keys / buttons bound to them),
  gated by movie_seen for ids 4, 5, 6, 35, 36, 37.
- Do not implement the dead bits and timers of section 3.
- The slice stand-in of FORMATS 7 ("decide / click advances, `_WTTM` waits its time") is
  replaced by:
  - `_WTTM(ms,1)` ignores clicks;
  - `_WTTM(ms,0)` / `_WTTM(ms)` end on a click, and the script continues on the next tick;
  - `@e`, `@n` and the page option need no click; the message advances on the tick after its
    reveal;
  - auto waits for the countdown (+1 tick) and for the voice.
- `tools/oracle/export_slice.py` (`wttm_trace`, `reveal_trace`) calls `tick_reveal` first, right
  after `wttm_apply` / `zm_apply`, and then alternates. Seen from the statement, that is the
  eboot order: the statement runs inside the frame's Adv_Update and 0xA2630 follows. The
  exported counts are therefore right: `_WTTM(1000)` resumes at frame 60, and a click resumes
  at frame 1 when the second argument is 0. `shown_at_frame` counts the `_ZM`'s own frame as
  frame 1. No change is needed there.

---

## 14. Verification (commands actually run)

| check | command | result |
|---|---|---|
| frame order, static | `python scratch/wait_model/frame_order.py` (linear sweep) | one reference each: `call 0xA0B70` at 0x61AED, `call 0xA2630` at 0x61A92 |
| frame order, emulated | same script: `wm.emu_frame_order()` | the real 0x60710 enters 0xA0B70 first (returns to 0x61AF2), then 0xA2630 (returns to 0x61A97), in normal, auto and latched-skip mode |
| whole frames vs eboot 0x60710 | `python scratch/wait_model/frames_emu.py 300` | 300 scenarios, 48,000 frames, 0 mismatching frames (349 s); advances: click 2,087, nowait 1,913, skip 1,666, auto 127; 659 reveal clicks, 97 timer cancels, 418 read-only stops; reversed order (negative control): 1,993 of 9,600 frames mismatch (`logs/frames_emu.json`) |
| model vs eboot, 0xA2630 + 0xA0B70 | `python -c "from tsuki import wait_model as wm; wm.emu_check(seeds=range(3000))"` | 3,000 cases, 0 mismatches in either function (49 s); advances: click 791, restored 159, nowait 95, skip 46, auto 3 (auto and its countdown are also unit-tested); Script_Step 591 |
| instruction coverage | `python scratch/wait_model/cov_emu.py 2500` | only the dead / out-of-scope ranges of 5.5 unexecuted |
| reveal done 0xCA070 | `wm.emu_check_reveal_done(cases=400)` | 400 synthetic blocks (flags 0 / 1 / 3, random head / tail), 0 mismatches |
| movie update 0x5E530 | `wm.emu_check_movie(cases=1500)` | phases 1..7, 0 mismatches including the 0x5E490 call count (51 SE calls); transitions include 1->2 80, 5->7 144, 3->7 101, 7->6/stop 39, 6->6/end 45 |
| census | `python -m tsuki.wait_model census` | numbers of 4.1, 6, 7 (25 s, all 549 scripts) |
| worked examples | `python scratch/wait_model/worked.py` | 11 timelines, `logs/summary.json` (section 12) |
| tests | `pytest tools/tests/test_wait_model.py` | 29 passed (about 8 s), no faulthandler dump (the emulation tests disable faulthandler around unicorn's handled SEH exception); with `TSUKI_GAME_DIR=/nonexistent` 21 passed, 8 skipped; mypy and pyflakes clean |

The emulation runs the eboot with `tsuki.misc_adv_system_state_emu.AdvEmu` (mapping, stubs,
imports), with every VEX instruction (BMI included) executed by
`tsuki.misc_audio_runtime_semantics_vex.VexCpu`.

- In the 0xA2630 / 0xA0B70 runs, Script_Step, the voice stop 0x8EA0, the choice calls and the
  post-step housekeeping are stubbed and counted. 0xCA070 and 0x4AB80 are stubbed with the
  model's values and emulated on their own.
- In the frame runs (`_FrameEmu`), 0x60710 and both functions are real. Script_Step runs the
  `MiniScript` on the emulator memory, and 0xCA070 answers from the current glyph records.

---

## 15. Errata for other documents (not applied there)

### Errata for textbox.md

- 8.3: "How Adv_Update (0xA0B70) waits for input or auto time per mode was not traced [H]" -> sections 4 and 5. Mode 2 is "no click": the message advances in the first frame after it is revealed and its voice has ended (the reveal ends in 0xA2630, which runs after Adv_Update; section 1.1); mode 1 likewise but never waits for the voice. 0x56140 is the page latch of 4.3.
- 8.1: "+0x22 when dword 0x781AB0[0] == 1 [H: skip/auto]": 0x781AB0[0] is the **read** flag stored by `_ZM` (0x9253F: 0x56150(0, read)); +0x22 is the Seen Text Display Speed.
- 8.3 "Auto mode: its timing is open" -> section 8.
- Open question 3 is answered (`@k` has no runtime role besides the HUD dimming of 4.4).

### Errata for adv_state.md

- 6, auto countdown: 0xA0B10 has no caller; 0x1653E1C is never set (the "voice wait has run out" condition is always true). The countdown is armed only for click-wait messages (bit 0x400).
- 6: "Adv_Update ends the wait when bit 0 is set and the countdown is <= 0 (0xA1D78, 0xA1F14)": 0xA1D78 belongs to the restored-page path (e00 == 0x500) and 0xA1F14 to the dead bit-0x2000 path. The live auto advance is 0xA1F92 -> 0xA1CFF: countdown == 0 **and** no voice bit.
- 6: with skip active the countdown is also set to 0 at full speed (0xA2AF0). A skip advance does not stop the voice itself; the plain-voice test 0xA0FE0, which runs before the message wait 0xA10BF in the same Adv_Update, does.
- 8, step 5: Adv_Update (or Adv_Rewind) is the first of two calls. 0x60710 then always calls 0xA2630 (reveal, `_WTTM` countdown, auto countdown; 0x61A92 after `0x61D2F jmp 0x61A5D`), with n = the same `speed * frames`. While the backlog is shown, Adv_Update is not called and 0xA2630 gets `frames * (0x3CFE0() <= 0)`. Between the two calls, the read-only rule can stop skipping (0x61B04) (wait_model.md 1.1).

### Errata for script.md

- 4.4 `WTTM`: "second argument 1 ... probably 'may be skipped by click'" is reversed: 1 = clicks are ignored; 0 or absent = a click ends the wait (section 6). `WTSV`: 6 frames, clicks ignored by default. `TOMW`: re-runs while F36 != 0. `VCWT`: waits only while a voice is loading or playing.
- 4.4 / 3.6: `VPL2` shares `VPLY`'s handler entry but plays the voice at once (0x9A05D); it is not a run-time alias.

### Errata for text.md

- 5.2: "The exact runtime meaning of the modes is [H]: ... mode 2 = end after an explicit `@k` wait": `@k` is not a wait; mode 2 = no click wait (4.1). The `_MSAD` condition "from VA 0x56140, not traced" is the page latch (4.3).

### Errata for sound_runtime.md

- 7 (6): "auto mode uses 0x96F0 = (state 2 ? elapsed * 17 ms : ...)": 0x96F0's only caller (0xA0FC9) is on the dead voice-timeout path; auto mode uses the voice state (0 = done) through wait bit 0x4.

### Errata for FORMATS.md

- 7 P0 item 2 (wait model) is answered by this document; P1 item 8: the reader of `_STCF`'s globals is Adv_Update 0xA0C9F with the conditions of 10.2.
- RUNTIME_REQUIREMENTS 4.11 "Open: skip input during script movies" is answered by 11.

### Errata for vm_variables.md

- F36 = "a script movie is playing" (set by `_TOMP` 0x9AC01 and `_XSTP` 0x965F4, cleared by the movie state exit 0x5F75C); F953 / F955 / F961 are read only by dead code and never written.

---

## 16. Open questions

1. Exact frame of the `_FADS` release (K or K + 1 frames, 10.1) and of the layer counters
   (whether the `_STGS` counter is decremented once per frame or per step): needs the layer
   update 0x57980 emulated across frames.
2. Voice and movie latencies: the frames between `VoiceRequest` and voice state 2 (loading), and
   between `_TOMP` and the player's first frame (0xEE880 ready count 4). The simulator assumes
   1 and 25 frames [H].
3. Fields +0x86 / +0x8C of the drawn layer record tested by `_STCF` masks 1 / 2 (no retail
   `_STCF` uses them).
4. The HUD click icon test 0xCA690 was not emulated (assumed equal to 0xCA070's rule).
5. The `@k` dimming rule of 4.4 has no visual check (the prologue movie shows no dimming,
   textbox.md open question 7).
6. The meaning of 0x6EAB0, which gates the d-pad-down latch 0xDF29E0, is assumed to be "no HUD
   menu open" [H]. The frame check stubs it to 0.
7. Branches of 0x60710 outside its normal path were not emulated frame by frame:
   - backlog shown: 0xA2630 only, with the 0x3CFE0 factor;
   - rewind;
   - fast-forward hold and the R2 analog step;
   - the HUD window-hide and mouse-cursor paths.
   They are [E] (1.1, adv_state.md 6 and 8).

---

## 17. Changes after review

The review of the first version found, and this version fixes:

1. **Frame order (major):** the first version called 0xA2630 before Adv_Update in every frame
   (doc, `Simulator.step`, the test helper). The eboot calls Adv_Update first. The evidence is
   now in 1.1: static call sites, emulation of the real 0x60710, and 48,000 emulated frames with
   a negative control. `wm.frame` is the composed frame; `Simulator.step` and the tests use it.
2. **Frame numbers that depended on the order (major):**
   - the next message starts revealing in the frame of the click, not the next one;
   - `@e` / `@n` / page-option messages advance the frame after their reveal, not in it;
   - a click that cancels `_WTTM(ms,0)` lets the script continue the next frame;
   - at 30 Hz one press only completes the reveal;
   - auto advances at reveal + countdown + 1;
   - the worked totals of 12.

   All were re-run. The new totals equal the reviewer's own re-simulation (00_00 click 63,843,
   auto 129,207, skip 1,254; 00_02 click 19,906, auto 34,034, skip 3,448).
3. **Movie start SE (minor):** 0x5E490 runs for the ids outside {0..7, 13, 35..45}. The first
   version had it inverted. `MOVIE_START_HOOK` is fixed, `movie_start_se` / `movie_se_calls`
   are new, and `emu_check_movie` now also runs phase 1 and compares the SE call count.
4. **`_STCF` mask 0x20 (minor):** the condition is 0xBA910 != 0 and 0xBA890 == 0. The model's
   `stcf_pending` now takes `ba910` / `ba890`.
5. **Test noise (minor):** the emulation tests disable faulthandler while unicorn runs.

None of the per-function results changed: 0xA2630, 0xA0B70, 0xCA070, the end modes, `_WTTM`'s
second argument, `_WNTY`, the dead bits, the movie skip gate and the census.
