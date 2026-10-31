"""ADV wait model of TSUKIHIME -A piece of blue glass moon- (PS4 CUSA47999, app 01.01).

Reference implementation of how the engine decides, frame by frame, whether the script may run:
message end modes (``)`` / ``@n`` / ``@e`` / ``@k@e``), the click wait, auto mode, skip /
fast-forward, ``_WTTM`` / ``_WTSV`` timers and their second argument, ``_WNTY``, ``_VCWT``,
``_FADS`` / ``_STCF`` / choice / system-state waits, and the movie skip input of ``_TOMP`` /
``_TOMW``.  Documentation: docs/formats/wait_model.md.  Tests: tools/tests/test_wait_model.py.

The two per-frame functions of the engine are reproduced statement by statement.  The ADV
input flow 0x60710 calls them in this order every frame (:func:`frame`):

1. :func:`adv_update` = ``Adv_Update`` VA 0xA0B70 (call at 0x61AED, reached by
   ``0x61A3A je 0x61ADA`` when the backlog is not shown): every wait test and, when nothing
   waits, the call of ``Script_Step`` (VA 0x95E60, at 0xA1DAD), which runs statements up to
   the next yield;
2. :func:`tick_reveal` = VA 0xA2630 (call at 0x61A92, reached by ``0x61D2F jmp 0x61A5D``):
   text reveal, click-to-complete, ``_WTTM`` countdown and click cancel, auto countdown, the
   "message revealed" transition (wait bit 0x40).

So a message started by the script in a frame gets its first reveal step in the same frame,
and a wait that 0xA2630 ends (reveal done, timer at 0, timer cancelled by a click, auto
countdown at 0) is acted on by the next frame's Adv_Update.

Both run on a :class:`WaitState` whose fields are the engine globals (``FIELDS`` maps each one to
its VA); :func:`emu_check` runs the game's own two functions under unicorn on random states and
compares every field with this model, and :func:`emu_check_frames` runs the game's 0x60710
itself (with the real 0xA0B70 and 0xA2630 inside it) over multi-frame scenarios and compares
every frame with :func:`frame` (development aids; need ``unicorn``, ``capstone`` and the
user's ``eboot.bin``).  Higher-level helpers turn a script into a list of wait operations
(:func:`wait_plan`) and simulate it with an input policy (:class:`Simulator`).

Paths that are present in the code but can never run in this build (their state is never set by
any code: wait bits 0x80 / 0x800 / 0x1000 / 0x2000 / 0x10000 / 0x20000 and the timers 0x1653E0A /
0x1653E0C / 0x1653E10 / 0x1653E1C / 0x1653E28 / 0x1653E68, the reveal-all byte 0x174DE00) are
not modelled; :func:`dead_state_ok` asserts that a state does not use them.

CLI::

    python -m tsuki.wait_model census                    # end modes / waits in all scripts
    python -m tsuki.wait_model plan 00_00 [--lang en]    # wait operations of one script
    python -m tsuki.wait_model timeline 00_02 --policy click|auto|skip|none [--lang en] [--limit N]
    python -m tsuki.wait_model emu [--seeds N] [--frames M]   # compare with the eboot under unicorn
"""
from __future__ import annotations

import argparse
import math
import random
import struct
import sys
from dataclasses import dataclass, field, fields, replace
from pathlib import Path
from typing import Any, Callable, Dict, Iterable, List, Optional, Sequence, Tuple

import numpy as np

F32 = np.float32

# ---------------------------------------------------------------------------
# addresses (CUSA47999 v01.01; text VA = file offset - 0x2280, data VA = file offset -
# 0x365FA0 + 0x600000)
# ---------------------------------------------------------------------------
VA_ADV_INPUT = 0x60710        # ADV input flow: calls Adv_Update, then tick_reveal (frame order)
VA_ADV_INPUT_END = 0x61F10   # (EH function table)
VA_ADV_UPDATE = 0xA0B70       # Adv_Update: wait tests, then Script_Step (called first, 0x61AED)
VA_TICK_REVEAL = 0xA2630      # per-frame reveal / timers (called second, 0x61A92)
CALL_ADV_UPDATE = 0x61AED     # the only call of 0xA0B70 in the text (returns to 0x61AF2)
CALL_TICK_REVEAL = 0x61A92    # the only call of 0xA2630 in the text (returns to 0x61A97)
SKIP_TABLE = 0x2014F0         # i16 step per mode word (latched skip): {1, 1, 64, 64, 64}
VA_SCRIPT_STEP = 0x95E60
VA_ZM = 0x92380               # _ZM message display
VA_MSAD = 0x92B60             # _MSAD append
VA_STOP_VOICE = 0x8EA0        # stops voice streams 2..5
VA_VOICE_STATE = 0x9210       # returns word 0xDD8E50 (0 idle, 1 loading, 2 playing)
VA_REVEAL_DONE = 0xCA070      # "last record of the block fully visible" test
VA_STCF_PENDING = 0x4AB80     # per-layer condition of _STCF
VA_FADE_BUSY = 0x42680        # screen fade state 0xDF0774 in {1, 2}
VA_MOVIE_UPDATE = 0x5E530     # system state 8 update (skip input)
VA_MOVIE_ENTER = 0x5E300
VA_MOVIE_EXIT = 0x5F6F0

ACTION_DECIDE = 0x20          # action 0 of preset 0 (adv_state.md 10.2): the decide button
PAD_CANCEL = 0x40
PAD_OPTIONS = 0x800
PAD_DOWN = 0x4000             # d-pad down: 0xDF29E0 = bit 14 of the newly-pressed word (0x609EB)
PAD_R2 = 0x2

# wait bits of the u64 0x1653E00 ("E00")
W_REVEAL = 0x1                # message text is being revealed (copied from 0x1653E70 bit 0)
W_VOICE = 0x4                 # wait for the voice (E70 bit 2 copied by _ZM/_MSAD, or _VCWT)
W_FADS = 0x8                  # _FADS transition running (cleared by 0x57980 at its end)
W_CHOICE = 0x10               # choice menu open (_SELD/_SELS, cleared by the choice code)
W_STCF = 0x20                 # _STCF layer condition (mask 0x1653E30 / bytes 0x1653E40)
W_SHOWN = 0x40                # message fully revealed, waiting to be dismissed
W_MESSAGE = 0x100             # a message is displayed (dismissing it starts a new page slot)
W_SYSTEM = 0x200              # a system state (fade screen, menu, movie) is entering / running
W_CLICK = 0x400               # end mode 0: wait for a click (or auto / skip)
LIVE_BITS = W_REVEAL | W_VOICE | W_FADS | W_CHOICE | W_STCF | W_SHOWN | W_MESSAGE | W_SYSTEM | W_CLICK
DEAD_BITS = 0x80 | 0x800 | 0x1000 | 0x2000 | 0x10000 | 0x20000

# end modes of _ZM / _MSAD (text.md 5.2, textbox.md 3.1)
MODE_CLICK = 0                # ")": click wait (bit 0x400)
MODE_CONTINUE = 1             # "@n": no wait; the next _MSAD appends
MODE_NOWAIT = 2               # "@e", or ")" when "Display End of Page Instantly" applies

# float constants of the reveal code (eboot data)
SPEED_INSTANT = F32(768.0)    # 0x20B340 / 0x1A71BC: head value meaning "all revealed"
HEAD_ALL = F32(76800.0)       # 0x47960000: head after a click or a skip-reveal
F988_STEP = F32(0.675)        # 0x20B33C
FAST_MULT = F32(60.0)         # 0x20D638 (step >= 61)

# ---------------------------------------------------------------------------
# configuration, input, mode
# ---------------------------------------------------------------------------


@dataclass
class Config:
    """Config block fields (0x66C578 + offset) read by the wait code; boot values
    (adv_state.md 9.2)."""
    text_speed_unread: int = 35     # +0x20  ms per reveal counter (Unseen Text Display Speed)
    text_speed_read: int = 15       # +0x22  (Seen Text Display Speed)
    auto_wait_ms: int = 2200        # +0x2E  Auto-Mode Wait Time (F799 = 0)
    ff_read_only: int = 1           # +0x30  Fast-Forward: read text only
    interrupt_voice: int = 1        # +0x4E  Interrupt Voices on Advancing Text
    page_instant_unread: int = 0    # +0x9C  Display End of Page Instantly (unseen)
    page_instant_read: int = 0      # +0x9D  Display End of Page Instantly (seen)
    auto_page_ms: int = 3000        # +0x9E  Auto-Mode Advance Page Speed (F799 = 1)

    OFFSETS = {"text_speed_unread": (0x20, "h"), "text_speed_read": (0x22, "h"),
               "auto_wait_ms": (0x2E, "h"), "ff_read_only": (0x30, "h"),
               "interrupt_voice": (0x4E, "h"), "page_instant_unread": (0x9C, "B"),
               "page_instant_read": (0x9D, "B"), "auto_page_ms": (0x9E, "h")}


@dataclass
class FrameInput:
    """What the ADV input flow 0x60710 hands to the two functions for one frame."""
    held: int = 0          # game input word +0x10 (pad bits, adv_state.md 10.1)
    newly: int = 0         # game input word +0x20 (newly pressed)
    mouse: int = 0         # 0x178EC54 mouse flags: bit 0 left held, bit 1 left newly pressed
    wheel: int = 0         # 0x178EC5C: sum of the mouse wheel deltas read this frame
    adv_key: int = 0       # 0xDF29E0: d-pad down newly pressed and no HUD menu open (0x609EB)

    @classmethod
    def press(cls, button: int = ACTION_DECIDE, held: bool = True) -> "FrameInput":
        """A button pressed this frame (the edge and the held bit)."""
        return cls(held=button if held else 0, newly=button,
                   adv_key=1 if button & PAD_DOWN else 0)

    @classmethod
    def hold(cls, button: int = ACTION_DECIDE) -> "FrameInput":
        return cls(held=button)


@dataclass
class AdvMode:
    """ADV mode words (adv_state.md 6) and the per-frame step."""
    mode_word: int = 0     # 0x17DB230: bit 0 auto mode, bit 1 latched skip
    skip_active: int = 0   # 0x17DB232: fast-forward active this frame (latched skip or R2 hold)
    rewind: int = 0        # 0x17DB234
    log_shown: int = 0     # 0x17DB236
    step: int = 1          # frame multiplier (0x78AF80): 1, or 64 while skipping (R2 analog 1..64)
    vsync: int = 1         # 0xDC7BE0: vsyncs since the last update (0xD47C0 = min(vsync, 3))

    @property
    def frames(self) -> int:
        """Frame count passed to both functions: min(vsync, 3) * step (0x61A81, 0x61ADA)."""
        return max(0, min(self.vsync, 3)) * self.step

    @classmethod
    def normal(cls) -> "AdvMode":
        return cls()

    @classmethod
    def auto(cls) -> "AdvMode":
        return cls(mode_word=1)

    @classmethod
    def skip(cls) -> "AdvMode":
        """Latched skip (R1): 0x6120E sets skip_active = 1 and step = {1,1,64,64,64}[mode_word]."""
        return cls(mode_word=2, skip_active=1, step=64)

    @classmethod
    def from_mode_word(cls, mode_word: int, vsync: int = 1) -> "AdvMode":
        """What 0x60710 derives when neither fast-forward nor R2 is held (0x6120E..0x61261):
        mode word >= 2 (latched skip) -> skip active, step = table 0x2014F0[mode word]; else
        skip off, step 1."""
        mw = mode_word & 0xFFFF
        if mw < 2:
            return cls(mode_word=mw, skip_active=0, step=1, vsync=vsync)
        if mw > 4:
            raise ValueError(f"mode word {mw:#x} is outside the step table 0x2014F0")
        return cls(mode_word=mw, skip_active=1, step=(1, 1, 64, 64, 64)[mw], vsync=vsync)


# ---------------------------------------------------------------------------
# state
# ---------------------------------------------------------------------------
# (field, VA, struct format)  -- everything the two functions read or write that is modelled
FIELDS: Tuple[Tuple[str, int, str], ...] = (
    ("e00", 0x1653E00, "Q"), ("e08", 0x1653E08, "h"), ("e0e", 0x1653E0E, "h"),
    ("e18", 0x1653E18, "i"), ("e24", 0x1653E24, "f"), ("e30", 0x1653E30, "Q"),
    ("e70", 0x1653E70, "Q"), ("e7c", 0x1653E7C, "i"), ("head", 0x1653E80, "f"),
    ("tail", 0x1653E84, "f"), ("width", 0x1653E88, "f"), ("frame_ctr", 0x1653DF8, "h"),
    ("adv_key", 0xDF29E0, "i"), ("new_page", 0x86AA24, "i"), ("window_target", 0xDF0734, "i"),
    ("window_alpha", 0xDF0730, "f"), ("read_sel", 0x781AB0, "i"), ("voice_state", 0xDD8E50, "H"),
    ("fade_state", 0xDF0774, "i"), ("f799", 0x164B80E, "h"), ("f823", 0x164B83E, "h"),
    ("f988", 0x164B988, "h"), ("f1103", 0x164BA6E, "h"), ("f1123", 0x164BA96, "h"),
    ("blocks", 0x16541D8, "i"), ("scene_jump", 0x16541F8, "i"),
)
VA_E40 = 0x1653E40            # u8[40] _STCF condition masks
VA_DIM = 0x164B886            # F859..: dimming timers of the previous messages (C859)
VA_CONFIG = 0x66C578


@dataclass
class WaitState:
    """The engine globals of the wait logic (names: see FIELDS)."""
    e00: int = 0           # wait bits
    e08: int = 0           # _WTTM / _WTSV frames left (i16)
    e0e: int = 0           # auto countdown in frames (i16)
    e18: int = 0           # 1: the _WTTM wait ignores clicks (second argument != 0)
    e24: float = 0.0       # text fade step (f32; > 0 while the text fades in)
    e30: int = 0           # _STCF layer mask (bit l)
    e40: bytes = bytes(40) # _STCF condition per layer
    e70: int = 0           # message flags: bit 0 reveal, bit 2 voice wait
    e7c: int = 0           # 1 after a click advance until decide / left button are released
    head: float = 0.0      # reveal head (counter units, f32)
    tail: float = 0.0      # reveal tail (f32)
    width: float = 0.0     # fade width head - tail (f32)
    frame_ctr: int = 0     # 0x1653DF8 free-running frame counter (i16; only the 3-argument _WTTM)
    adv_key: int = 0       # 0xDF29E0 (consumed by a click)
    new_page: int = 0      # 0x86AA24: the next _ZM starts a new message slot (0x75CD0(1))
    window_target: int = 0 # 0xDF0734 (1 = window open)
    window_alpha: float = 1.0  # 0xDF0730 window fade
    read_sel: int = 0      # 0x781AB0[0] = 1: current message already read (speed +0x22)
    voice_state: int = 0   # 0xDD8E50
    fade_state: int = 0    # 0xDF0774 (screen fade; 1 / 2 block the script)
    f799: int = 0          # _WNTY: 1 = last message of the page
    f823: int = 0          # message counter of the page
    f988: int = 0          # fixed reveal step 0.675 when != 0
    f1103: int = 0         # "window was closed" (set by _CLO2/_CLO3)
    f1123: int = 0         # script switch: keep the voice on advance
    blocks: int = 1        # 0x16541D8 (bits 1 / 2: extra blocks slot+6 / slot+8)
    scene_jump: int = 0    # 0x16541F8 (1 = next-scene request, -1 = 0xA23F0 request)
    dim: List[int] = field(default_factory=lambda: [0] * 12)   # F859.. (-1 = dimmed)

    def copy(self) -> "WaitState":
        return replace(self, dim=list(self.dim))


@dataclass
class Effects:
    """Calls the two functions make, for the model / emulation comparison."""
    stop_voice: int = 0            # calls of 0x8EA0
    script_step: int = 0           # calls of Script_Step
    reveal_tests: List[int] = field(default_factory=list)   # blocks passed to 0xCA070
    choice_update: int = 0         # 0xA6180 (choice open) calls
    choice_idle: int = 0           # 0xA60D0 calls
    advanced: str = ""             # "click", "auto", "skip", "nowait", "restored" ("" = none)
    reveal_clicked: bool = False   # a click completed the reveal (head = 76800)
    timer_clicked: bool = False    # a click cancelled the _WTTM wait


def dead_state_ok(st: WaitState) -> bool:
    """True when the state uses none of the paths that cannot occur in v01.01."""
    return not (st.e00 & DEAD_BITS) and st.scene_jump not in (1, -1)


# ---------------------------------------------------------------------------
# helpers with exact engine arithmetic
# ---------------------------------------------------------------------------

def _i16(v: int) -> int:
    v &= 0xFFFF
    return v - 0x10000 if v & 0x8000 else v


def _i32(v: int) -> int:
    v &= 0xFFFFFFFF
    return v - 0x100000000 if v & 0x80000000 else v


def _trunc(x: np.floating) -> int:
    """cvttss2si (out-of-range gives 0x80000000)."""
    if not np.isfinite(x) or abs(float(x)) >= 2 ** 31:
        return -0x80000000
    return int(float(x))


def ms_to_frames_f32(ms: int) -> int:
    """trunc(ms / 1000.0f * 60.0f), the auto-countdown conversion (0xA2968, 0xA2A8F)."""
    x = F32(F32(F32(_i16(ms)) / F32(1000.0)) * F32(60.0))
    return _trunc(x)


def wait_frames(ms: int) -> int:
    """_WTTM / _WTSV / _WTTR: (3 * ms) / 50 with C truncation, at least 1 (0x9ACF7, 0x9AD98)."""
    t = 3 * _i32(ms)
    q = int(t / 50) if t >= 0 else -int(-t / 50)
    return q if q >= 2 else 1


def reveal_speed(cfg: Config, read_sel: int, vsync: int = 1) -> np.float32:
    """0x56160(0): counters per reveal step = min(vsync, 3) / 60 * 1000 / v (768 when v <= 0)."""
    v = _i16(cfg.text_speed_read if read_sel == 1 else cfg.text_speed_unread)
    if v <= 0:
        return SPEED_INSTANT
    d = F32(_i16(min(vsync, 3)))
    return F32(F32(F32(d / F32(60.0)) * F32(1000.0)) / F32(v))


def reveal_width(cfg: Config, read_sel: int, vsync: int = 1) -> np.float32:
    """0x561D0(0): fade width = speed * min(0.5, v * 0.1 / 35) * 60 (0 when v <= 0 or the
    speed is exactly 768)."""
    v = _i16(cfg.text_speed_read if read_sel == 1 else cfg.text_speed_unread)
    if v <= 0:
        return F32(0.0)
    d = F32(_i16(min(vsync, 3)))
    sp = F32(F32(F32(d / F32(60.0)) * F32(1000.0)) / F32(v))
    if sp == SPEED_INSTANT:
        return F32(0.0)
    x = F32(F32(F32(v) * F32(0.1)) / F32(35.0))
    x = F32(0.5) if F32(0.5) < x else x                  # vminss xmm0, xmm2(0.5), xmm0
    return F32(F32(sp * x) * F32(60.0))


def reveal_done(records: Sequence[Tuple[int, int]], head: float, tail: float) -> bool:
    """0xCA070 result: the alpha of the LAST record with non-zero flags (ruby readings
    included) equals 1.0.  ``records`` = (flags, ctr) per glyph record in record order.
    alpha = 0 if ctr > head; 1 if tail > ctr or head - tail <= 0; else (head - ctr) /
    (head - tail).  An empty block (layout returned 0, block not allocated) counts as done."""
    h, t = F32(head), F32(tail)
    alpha: Optional[np.float32] = None
    for flags, ctr in records:
        if flags == 0:
            continue
        c = F32(ctr)
        if c > h:
            alpha = F32(0.0)
        elif t > c:
            alpha = F32(1.0)
        else:
            d = F32(h - t)
            alpha = F32(1.0) if F32(0.0) >= d else F32(F32(h - c) / d)
    return True if alpha is None else bool(alpha == F32(1.0))


def stcf_pending(mask: int, fields_: Dict[str, float]) -> bool:
    """0x4AB80(layer, mask): the _STCF condition of one layer (drawn layer record 0xDDB5E8 +
    0x2C8 * l).  ``fields_`` holds the record values: 'r150' f32 +0x150 (_STRC); for mask
    0x20 the motion at +0x160: 'ba910' = 0xBA910(rec+0x160) (motion time still below its
    length) and 'ba890' = 0xBA890(rec+0x160) (byte +0x170); the layer is pending when
    ba910 != 0 AND ba890 == 0 (0x4ABDC..0x4ABF6: ``test cl, cl; je -> return 1``); 'w86',
    'w8c', 'w92' (_STRT), 'w98' (_STGS alpha fade) as i16."""
    if mask & 0x10 and fields_.get("r150", 0.0) > 0.0:
        return True
    if mask & 0x20 and fields_.get("ba910", 0) and not (int(fields_.get("ba890", 0)) & 0xFF):
        return True
    for bit, key in ((1, "w86"), (2, "w8c"), (4, "w92"), (8, "w98")):
        if mask & bit and int(fields_.get(key, 0)) > 0:
            return True
    return False


def stgs_fade_updates(ms: int) -> int:
    """Number of layer updates until the _STGS alpha counter (+0x98 = 4096, step +0x9A =
    trunc(-4096 / frames), frames = trunc(ms * 60 / 1000)) reaches 0 (0x84AAD, 0x4A66B).
    0 when frames <= 0 (the counter is cleared at once)."""
    frames = int(ms * 60 / 1000) if ms >= 0 else -int(-ms * 60 / 1000)
    if frames <= 0:
        return 0
    step = int(4096.0 / frames)
    return -(-4096 // max(step, 1))


def fads_updates(ms: int, kind: int = 0, speed: int = 1) -> int:
    """Transition updates until the _FADS progress reaches 0 (compositing.md 7.2: step
    -1 / (ms / 16.666) in float32, kinds 12 / 49 add 100 ms)."""
    from tsuki.compose import fads_step
    p = F32(1.0)
    st = fads_step(ms, kind)
    n = 0
    while True:
        n += 1
        p = F32(p + F32(speed) * st)
        if p <= 0:
            return n
        if n > 1_000_000:
            raise ValueError("transition never ends")


# ---------------------------------------------------------------------------
# script commands (the part of each handler that touches the wait state)
# ---------------------------------------------------------------------------

def zm_end_mode(terminator: str, f799: int, page_latch: bool) -> int:
    """End mode of a _ZM / _MSAD argument (0x92574..0x9259B, 0x92B76..0x92C07).
    ``terminator``: ')' (the argument ended normally), '@n' or '@e'."""
    if terminator == "@n":
        return MODE_CONTINUE
    if terminator == "@e":
        return MODE_NOWAIT
    return MODE_NOWAIT if (page_latch and f799 != 1) else MODE_CLICK


def page_latch_value(cfg: Config, read: bool) -> bool:
    """0x56270(option == 1), stored by _ZM on the first message of a page (F823 <= 1):
    option = config +0x9D (read) / +0x9C (unread)."""
    return (cfg.page_instant_read if read else cfg.page_instant_unread) == 1


def zm_apply(st: WaitState, mode: int, voice_started: bool, read: bool = False) -> None:
    """Wait-state part of _ZM (0x92380): new message slot if the last one was dismissed,
    reveal from 0, voice-wait flag, continue byte, end-mode flags."""
    if st.new_page:
        st.new_page = 0                    # 0x75CD0(1): new slot, E70 = 0 (0x75D34)
        st.e70 = 0
    st.read_sel = 1 if read else 0        # 0x56150(0, read)
    st.head = 0.0                          # 0x92659
    st.tail = 0.0
    if voice_started and mode != MODE_CONTINUE:
        st.e70 |= W_VOICE                  # 0x928ED
    st.e70 |= W_REVEAL                     # 0x928FB
    st.e00 |= (W_CLICK if mode == MODE_CLICK else 0) | W_MESSAGE
    if st.e70:
        st.e00 |= st.e70                   # 0x92B31


def msad_apply(st: WaitState, mode: int, voice_started: bool, last_voice: bool,
               old_end: Optional[float] = None) -> None:
    """Wait-state part of _MSAD (0x92B60): append (no new slot); the reveal continues from
    the old end (head = tail = old end counter + 1, 0x92CB5); the voice wait is set when this
    _MSAD started a voice or the message's _ZM did (0x86AA28 >= 0, 0x92E93)."""
    if old_end is not None:
        st.head = st.tail = float(F32(old_end))
    if mode != MODE_CONTINUE and (voice_started or last_voice):
        st.e70 |= W_VOICE
    st.e70 |= W_REVEAL
    st.e00 |= (W_CLICK if mode == MODE_CLICK else 0) | W_MESSAGE
    if st.e70:
        st.e00 |= st.e70


def wttm_apply(st: WaitState, ms: int, second: Optional[int]) -> None:
    """_WTTM(ms [, n]) (0x9AC1A): frames = max(1, 3*ms/50); E18 = (n != 0) when the second
    argument is present and not empty, else 0.  A third argument (unused by the scripts)
    makes the wait relative to the free-running counter 0x1653DF8."""
    st.e08 = _i16(wait_frames(ms))
    st.e18 = 1 if (second is not None and second != 0) else 0


def wtsv_apply(st: WaitState, ms: Optional[int] = None, second: Optional[int] = None) -> None:
    """_WTSV([ms [, n]]) (0x9AF36): 6 frames when ms is absent; E18 defaults to 1 (no click)."""
    st.e08 = _i16(6 if ms is None else wait_frames(ms))
    st.e18 = 1 if second is None else (1 if second != 0 else 0)


def vcwt_apply(st: WaitState) -> bool:
    """_VCWT (0x96504): wait bit 4 when a voice is loading or playing (then yields).
    Returns True when the command yields."""
    if st.voice_state != 0:
        st.e00 |= W_VOICE                  # 0x9AEC2
        return True
    return False


def wnty_apply(st: WaitState, n: int) -> None:
    """_WNTY(n) (0x972F5 -> 0x53600): F799 = (n != 0); also word 0xDF08DC."""
    st.f799 = 1 if n != 0 else 0


def fads_apply(st: WaitState) -> None:
    """_FADS (0x81520): starts the transition, clears the system bit, sets wait bit 8."""
    st.e00 &= ~W_SYSTEM
    st.e00 |= W_FADS


def stcf_apply(st: WaitState, layers: Iterable[int], mask: int = 0x3F) -> None:
    """_STCF(l [, mask]) (0x83E50): E30 = layers, E40[l] = mask, wait bit 0x20."""
    st.e30 = 0
    e40 = bytearray(st.e40)
    for l in layers:
        if 0 <= l < 40:
            st.e30 |= 1 << l
            e40[l] = mask & 0xFF
    st.e40 = bytes(e40)
    st.e00 |= W_STCF


# ---------------------------------------------------------------------------
# 0xA2630: reveal, timers, auto countdown
# ---------------------------------------------------------------------------

def _click_reveal(st: WaitState, inp: FrameInput) -> bool:
    """Click test of the reveal (0xA27ED..0xA289A): 0xDF29E0, left click, decide, wheel down."""
    return bool(st.adv_key or inp.mouse & 2 or inp.newly & ACTION_DECIDE or inp.wheel < 0)


def _click_timer(st: WaitState, inp: FrameInput) -> bool:
    """Click test of the _WTTM cancel (0xA28D1, 0xA2B6E): no wheel."""
    return bool(st.adv_key or inp.mouse & 2 or inp.newly & ACTION_DECIDE)


def tick_reveal(st: WaitState, inp: FrameInput, cfg: Config, mode: AdvMode,
                done: Callable[[WaitState, int], int] = lambda s, b: 1,
                n: Optional[int] = None) -> Effects:
    """VA 0xA2630 for one frame.  ``done(state, block)`` returns 0xCA070's value for the
    block (slot+4, +6, +8): 1 = the last record is fully visible, 0 = not yet, -1 = block not
    allocated.  ``n`` = the frame-count argument (edi); default ``mode.frames``.  It differs
    from ``mode.frames`` only in the frame where the read-only rule stops skipping after
    Adv_Update (0x61B2D: n = min(vsync, 3) while the step global 0x78AF80, which decides the
    fast path here, is still 64).  Mutates ``st``."""
    eff = Effects()
    n = mode.frames if n is None else n
    fast = mode.step >= 0x3D
    st.frame_ctr = _i16(st.frame_ctr + n)
    mult = FAST_MULT if fast else F32(1.0)
    for _ in range(n if n > 0 else 0):
        if not (st.e70 & W_REVEAL) and not (st.e00 & W_REVEAL):
            continue
        if st.window_target != 0 and mode.log_shown == 0:
            if (F32(st.e24) > 0 or F32(st.window_alpha) < F32(1.0)) and st.f1103 == 0:
                continue                   # wait for the window / text fade-in (0xA26EB)
        st.window_target = 1               # 0x423F0(1)
        head = F32(st.head)
        if st.f988 == 0:
            sp = reveal_speed(cfg, st.read_sel, mode.vsync)
            st.width = float(reveal_width(cfg, st.read_sel, mode.vsync))
            if sp >= SPEED_INSTANT:
                st.head = float(SPEED_INSTANT)
                st.tail = float(SPEED_INSTANT)
                head = None                # type: ignore[assignment]
        else:
            sp = F988_STEP
        if head is not None:
            if SPEED_INSTANT > head:
                head = F32(F32(sp * mult) + head)
                st.head = float(head)
            x = F32(F32(st.head) - F32(st.width))
            if x > F32(st.tail):
                st.tail = float(x)
        if st.e7c == 0 and _click_reveal(st, inp):
            st.head = float(HEAD_ALL)      # 0xA2803
            eff.reveal_clicked = True
            k = st.f823
            if k >= 2:                     # dim the previous messages at once (memset 0xFF)
                for j in range(k - 1):
                    if j < len(st.dim):
                        st.dim[j] = -1
    # _WTTM countdown and click cancel (0xA289F)
    if st.e08 > 0:
        st.e08 = max(0, _i16(st.e08 - n))
        if not (st.e18 > 0) and st.e7c == 0 and _click_timer(st, inp):
            st.e08 = 0
            eff.timer_clicked = True
    # skip clamps the auto countdown (0xA28EA)
    if mode.skip_active > 0 or mode.mode_word & 2:
        if st.e0e >= 0x3D:
            st.e0e = 0x3C
    # auto countdown (0xA293B)
    if st.e0e > 0:
        lim = ms_to_frames_f32(cfg.auto_page_ms if st.f799 == 1 else cfg.auto_wait_ms)
        v = st.e0e
        if lim < v:
            v = lim
        v = _i16(v - n)
        st.e0e = v if v > 0 else 0
    # reveal finished? (0xA2996)
    eff.reveal_tests.append(4)
    r = done(st, 4)
    if st.blocks & 2:
        eff.reveal_tests.append(6)
        r &= done(st, 6)
    if st.blocks & 4:
        eff.reveal_tests.append(8)
        r &= done(st, 8)
    if r != 0:
        if st.e00 & W_REVEAL:
            # 0xA2A52: the dead voice-timeout 0x1653E1C is always 0 here
            if st.e00 & W_CLICK:
                st.e0e = _i16(ms_to_frames_f32(cfg.auto_page_ms if st.f799 == 1 else cfg.auto_wait_ms))
            st.e00 = (st.e00 & ~(W_REVEAL | W_SHOWN)) | W_SHOWN
        st.e70 &= ~W_REVEAL
    # skip / rewind at full speed: no auto countdown (0xA2AC4)
    if fast and (mode.rewind != 0 or mode.skip_active > 0 or mode.mode_word & 2):
        st.e0e = 0
    if st.e00 & W_CHOICE:
        st.window_target = 1
        eff.choice_update += 1             # 0xA6180 (when no menu / log request is open)
    else:
        eff.choice_idle += 1               # 0xA60D0
    return eff


# ---------------------------------------------------------------------------
# 0xA0B70: Adv_Update
# ---------------------------------------------------------------------------

def _click_full(st: WaitState, inp: FrameInput) -> bool:
    """0xA10E5 / 0xA1CB2 click: 0xDF29E0, left click, decide, wheel down."""
    return bool(st.adv_key or inp.mouse & 2 or inp.newly & ACTION_DECIDE or inp.wheel < 0)


def _stop_voice_cond(st: WaitState, cfg: Config, mode: AdvMode) -> bool:
    return (st.f1123 == 0 and cfg.interrupt_voice != 0) or mode.skip_active != 0


def adv_update(st: WaitState, inp: FrameInput, cfg: Config, mode: AdvMode,
               stcf: Callable[[int, int], bool] = lambda layer, mask: False,
               voice_state: Optional[Callable[[], int]] = None) -> Effects:
    """VA 0xA0B70 for one frame (live paths).  ``stcf(layer, mask)`` = 0x4AB80;
    ``voice_state()`` defaults to ``st.voice_state``.  Mutates ``st``; ``Effects.script_step``
    tells whether Script_Step ran (the caller then executes statements up to the next yield)."""
    eff = Effects()
    vs = voice_state or (lambda: st.voice_state)
    if not (inp.mouse & 1) and not (inp.held & ACTION_DECIDE):
        st.e7c = 0                                             # 0xA0BA0
    if st.e00 & W_VOICE and vs() == 0:                         # 0xA0BBB
        st.e00 &= ~W_VOICE
        if st.e0e < 0:
            st.e0e = 0
    if st.e00 & W_STCF:                                        # 0xA0C9F
        m = st.e30
        for l in range(40):
            if m >> l & 1 and not stcf(l, st.e40[l]):
                m &= ~(1 << l)
        st.e30 = m
        if m == 0:
            st.e00 &= ~W_STCF
    # plain voice wait (_VCWT, or a voice bit left over by a skip advance) (0xA0FE0)
    if not (st.e00 & W_REVEAL) and not (st.e70 & W_REVEAL) and st.e00 & W_VOICE:
        if (st.adv_key or inp.newly & ACTION_DECIDE or inp.mouse & 2 or mode.skip_active != 0
                or inp.wheel < 0):
            if _stop_voice_cond(st, cfg, mode):
                eff.stop_voice += 1
            st.e70 &= ~W_VOICE
            st.e00 &= ~W_VOICE
    # message wait (0xA10BF)
    if st.e00 & W_SHOWN and st.e08 == 0:
        if _click_full(st, inp):                               # 0xA10F6
            st.adv_key = 0
            st.e0e = 0
            if st.e00 & W_MESSAGE:
                st.e00 &= ~W_MESSAGE
                st.e70 = 0
                st.new_page = 1
            cl = _stop_voice_cond(st, cfg, mode)
            if st.e00 & W_VOICE:
                if cl:
                    eff.stop_voice += 1
                st.e70 &= ~W_VOICE
                st.e00 &= ~W_VOICE
            elif cl:
                eff.stop_voice += 1
            st.e00 &= ~(W_REVEAL | W_SHOWN | W_CLICK)
            st.e7c = 1
            eff.advanced = "click"
        else:
            go = False
            if st.e00 & W_CLICK and not (mode.mode_word & 1) and not (mode.skip_active > 0):
                go = False                                     # 0xA1FBC with dx == 0
            elif not (st.e00 & W_VOICE) and st.e0e == 0:        # 0xA1CFF
                go = True
            else:
                go = mode.skip_active != 0 and st.e0e == 0     # 0xA1D13 -> 0xA1FBC
            if go:                                             # 0xA1FCE
                had_click = st.e00 & W_CLICK
                if st.e00 & W_MESSAGE:
                    st.e00 &= ~W_MESSAGE
                    st.e70 = 0
                    st.new_page = 1
                st.e00 &= ~(W_REVEAL | W_SHOWN | W_CLICK)
                eff.advanced = ("nowait" if not had_click else "skip" if mode.skip_active
                                else "auto")
    # a restored page (History_Restore sets 0x500, 0x7DBF0) or a bare 0x100 (0xA11BD)
    if st.e00 == (W_CLICK | W_MESSAGE):
        if st.adv_key or inp.mouse & 2 or inp.newly & ACTION_DECIDE or inp.wheel < 0:
            st.adv_key = 0
            st.e00 &= ~(W_CLICK | W_MESSAGE)
            eff.advanced = "restored"
        elif (mode.skip_active > 0 or mode.mode_word & 2
              or (mode.mode_word & 1 and st.e0e <= 0)):
            st.e00 &= ~(W_CLICK | W_MESSAGE)
            eff.advanced = "restored"
    elif st.e00 == W_MESSAGE:
        st.e00 = 0
        st.new_page = 1
    if inp.held & PAD_R2 and st.f1123 == 0 and cfg.interrupt_voice != 0:   # 0xA123D
        eff.stop_voice += 1
    # run the script when nothing waits (0xA1356)
    if st.e00 == 0 and st.e08 == 0 and st.e0e == 0:
        if st.fade_state not in (1, 2):                        # 0x42680
            eff.script_step = 1
    return eff


# ---------------------------------------------------------------------------
# one frame of the ADV input flow 0x60710 (frame order)
# ---------------------------------------------------------------------------

@dataclass
class FrameResult:
    update: Effects                # Adv_Update 0xA0B70 (runs first)
    reveal: Effects                # tick_reveal 0xA2630 (runs second)
    skip_stopped: bool = False     # the read-only rule stopped skipping after Adv_Update


def frame(st: WaitState, inp: FrameInput, cfg: Config, mode: AdvMode,
          done: Callable[[WaitState, int], int] = lambda s, b: 1,
          stcf: Callable[[int, int], bool] = lambda layer, mask: False,
          voice_state: Optional[Callable[[], int]] = None,
          script: Optional[Callable[[], None]] = None,
          line_read: Optional[Callable[[], bool]] = None,
          r2_analog: int = 0) -> FrameResult:
    """One update of the ADV input flow 0x60710 on its normal path (backlog not shown, no
    rewind), in the eboot's order [V-emu: :func:`emu_check_frames`]:

    1. 0x60804 / 0x609F1: ``adv_key`` = d-pad down newly pressed (set by the caller);
    2. Adv_Update 0xA0B70 (0x61AED): :func:`adv_update`; when it lets the script run, Script_Step
       (called inside it at 0xA1DAD) executes statements up to the next yield: ``script()``;
    3. 0x61B04..0x61B32: read-only rule after the update: when skipping, config +0x30 != 0, the
       current line is unread (``line_read()`` = 0x75C90 is False) and the R2 analog value
       (input +0x30) is 0, skip active is cleared and r15w = 1, so the following 0xA2630 gets
       n = min(vsync, 3) and skip_active 0, while the step global 0x78AF80 (its fast path) is
       still the frame's step;
    4. tick_reveal 0xA2630 (0x61A92, after ``0x61D2F jmp 0x61A5D``): :func:`tick_reveal`.

    The caller derives ``mode`` from the mode words as 0x60710 does before the calls
    (:meth:`AdvMode.from_mode_word` when no fast-forward / R2 is held).  Mutates ``st``."""
    e2 = adv_update(st, inp, cfg, mode, stcf=stcf, voice_state=voice_state)
    if e2.script_step and script is not None:
        script()
    mode_r, n_r, stopped = mode, None, False
    if (mode.skip_active != 0 and cfg.ff_read_only != 0 and line_read is not None
            and not line_read() and r2_analog == 0):
        mode_r = replace(mode, skip_active=0)
        n_r = max(0, min(mode.vsync, 3))
        stopped = True
    e1 = tick_reveal(st, inp, cfg, mode_r, done, n=n_r)
    return FrameResult(e2, e1, stopped)


# ---------------------------------------------------------------------------
# movie skip (system state 8, 0x5E530)
# ---------------------------------------------------------------------------
# movie id -> movie_seen bit (G[5]) that must be set before the movie can be skipped
# (jump table 0x200F80, read by 0xB7CA0); other ids are always skippable
MOVIE_SKIP_BIT = {4: 2, 5: 0, 6: 1, 35: 3, 36: 4, 37: 5}
MOVIE_SKIP_BUTTONS = PAD_CANCEL | PAD_OPTIONS    # raw input +0x20 & 0x840 (0x5E7C3, 0x5E8D1)
MOVIE_FADE_STEP = 1.0 / 60.0                     # +0x40 = 0x3C888889 (0x5E8E7)


def movie_skippable(movie_id: int, movie_seen_bits: int) -> bool:
    """r14b of 0x5E530: ids 4, 5, 6, 35, 36, 37 need their movie_seen bit; others always."""
    b = MOVIE_SKIP_BIT.get(movie_id)
    return True if b is None else bool(movie_seen_bits >> b & 1)


def movie_skip_pressed(inp: FrameInput) -> bool:
    return bool(inp.newly & MOVIE_SKIP_BUTTONS)


def movie_params(movie_id: int, lang: int = 0) -> Tuple[float, float, float]:
    """(fade-in rate per frame, fade-out start frame, fade-out rate) of 0x5E5AB..0x5E656
    (jump table 0x201008 for ids 4..37; ids 39..45: start 618; others: rate 1/48, start 420)."""
    one, slow = 1.0, float(F32(0.0208333358168602))
    table = {4: (one, 23340.0 if lang == 1 else 23040.0, one), 5: (one, 6420.0, one),
             6: (one, 6300.0, one), 7: (slow, 312.0, slow), 13: (one, 540.0, one),
             35: (one, 18960.0, one), 36: (one, 8880.0, one), 37: (one, 15240.0, one)}
    if movie_id in table:
        return table[movie_id]
    if 39 <= movie_id <= 45:
        return (one, 618.0, one)
    return (slow, 420.0, one)


# 0x5E490 (UI SE moon_se00013) at playback start (phase 1, 0x5E6D3..0x5E6EE): `bt rcx
# (0x3FF8000020FF), id; jae 0x5E968` jumps to the call when the id's bit is CLEAR, and ids > 45
# take it too (`ja 0x5E968`).  So the SE plays for every id NOT in MOVIE_NO_START_SE; id 13 plays
# it later instead, at frame 210 of phase 3 (0x5E84D..0x5E859).
MOVIE_NO_START_SE = frozenset(i for i in range(46) if 0x3FF8000020FF >> i & 1)  # 0..7, 13, 35..45


def movie_start_se(movie_id: int) -> bool:
    """True when the movie state calls 0x5E490 as playback starts (phase 1)."""
    return movie_id not in MOVIE_NO_START_SE


# ids 0..45 for which 0x5E490 runs when playback starts (8..12, 14..34; ids > 45 as well)
MOVIE_START_HOOK = frozenset(i for i in range(46) if movie_start_se(i))


@dataclass
class MovieRec:
    """Fields of the system-state record of state 8 used by the update (0x5E530)."""
    movie_id: int = 4         # +0x18 (language-neutral id passed to _TOMP)
    phase: int = 5            # +0x30
    frame: int = 0            # +0x34 frames since playback started
    alpha: float = 1.0        # +0x3C picture alpha
    fade: float = 0.0         # +0x40 fade-out step after a skip (1/60)
    skip: int = 0             # +0x48 skip requested in phase 3
    aux: float = 0.0          # +0x4C second fade value (rises 1/96 per frame)


def movie_se_calls(rec: MovieRec, vsync: int = 1, countdown: int = 0, ready: bool = True) -> int:
    """Calls of 0x5E490 (UI SE) made by one update of 0x5E530: at playback start (phase 1,
    player ready) for ids outside MOVIE_NO_START_SE, and for id 13 in phase 3 once the frame
    reaches 210 (0x5E84D)."""
    if countdown > 1:
        return 0
    d = max(0, min(vsync, 3))
    if rec.phase == 1:
        return 1 if ready and movie_start_se(rec.movie_id) else 0
    if rec.phase == 3 and rec.movie_id == 13 and rec.frame + d >= 210:
        return 1
    return 0


def movie_update(rec: MovieRec, inp: FrameInput, playing: bool, seen_bits: int, lang: int = 0,
                 vsync: int = 1, countdown: int = 0, ready: bool = True) -> Tuple[MovieRec, str]:
    """Phases 1..7 of the movie state update 0x5E530 (phase 0 only calls 0xE4F00 / 0x88F0 and
    moves to phase 1).  ``countdown`` = 0xDF293C before the call (the update does nothing while
    it is > 1); ``ready`` = 0xEE880 (player ready, tested in phase 1).  Returns (record,
    action) with action "" / "stop" (0xEE8C0: stop request) / "end" (0x5C080: the state ends ->
    exit 0x5F6F0 clears F36, so _TOMW continues).  The SE calls are :func:`movie_se_calls`."""
    r = replace(rec)
    if countdown > 1:
        return r, ""
    skippable = movie_skippable(r.movie_id, seen_bits) if 4 <= r.movie_id <= 37 else True
    fin, fstart, fout = movie_params(r.movie_id, lang)
    d = max(0, min(vsync, 3))
    r.frame += d
    fd = F32(d)
    third = F32(0.0104166679084301)
    if r.phase == 1:                                   # 0x5E6A6: wait for the player
        if not ready:
            return r, ""
        r.frame, r.alpha, r.aux, r.skip = 0, 0.0, 0.0, 0   # +0x34 = 0, +0x38 = 1, +0x3C, +0x4C, +0x48
        r.phase = 2                                    # then the phase-2 code in the same update
    if r.phase == 2:
        a = F32(F32(fd * F32(fin)) + F32(r.alpha))
        a = F32(1.0) if F32(1.0) < a else a
        r.alpha = float(a)
        if a >= F32(0.5):
            x = F32(F32(fd * third) + F32(r.aux))
            r.aux = 1.0 if x > F32(1.0) else float(x)
        if r.frame >= 60:
            r.phase = 3
        return r, ""
    if r.phase == 7:
        x = F32(F32(fd * third) + F32(r.aux))
        r.aux = float(F32(1.0) if F32(1.0) < x else x)
        a = F32(F32(r.alpha) - F32(fd * F32(r.fade)))
        r.alpha = float(a)
        if F32(0.0) >= a:
            r.alpha = 0.0
            r.phase = 6
            return r, "stop"
        return r, ""
    if r.phase == 3:
        mid = r.movie_id
        if mid == 13:
            if r.frame < 210:
                return r, ""
            r.phase = 4
        elif mid == 4:
            lim = 21900.0 if lang == 1 else 21600.0
            if not (F32(r.frame) >= F32(lim)):
                if not (skippable and movie_skip_pressed(inp)):
                    return r, ""
                r.skip = 1
            r.phase = 4
        else:
            r.phase = 4
    if r.phase == 4:
        if r.movie_id == 13 and r.frame < 330:
            return r, ""
        r.phase = 5
    if r.phase == 5:
        if playing:
            if skippable and (r.skip or movie_skip_pressed(inp)):
                r.frame = _trunc(F32(fstart))
                r.phase = 7
                r.fade = float(F32(MOVIE_FADE_STEP))
                return r, ""
        else:
            r.phase = 6
    if r.phase in (5, 6):
        x = F32(F32(fd * third) + F32(r.aux))
        r.aux = float(F32(1.0) if F32(1.0) < x else x)
        if F32(r.frame) >= F32(fstart):
            a = F32(F32(r.alpha) - F32(fd * F32(fout)))
            r.alpha = 0.0 if F32(0.0) > a else float(a)
        return r, ("" if playing else "end")
    return r, ""


# ---------------------------------------------------------------------------
# wait plan of a script
# ---------------------------------------------------------------------------

@dataclass
class WaitOp:
    index: int                    # statement index
    kind: str                     # message, wnty, wttm, wtsv, vcwt, fads, stcf, stgs, movie,
                                  # movie_wait, choice, voice, fade, clo, end
    text: str = ""                # statement text
    mode: int = -1                # messages: end mode with the page option off
    terminator: str = ""          # ')', '@n', '@e'
    keep: bool = False            # message text ends in '@k' (backlog keeps it on the page)
    msad: bool = False
    f799: int = 0                 # F799 in effect (last _WNTY)
    voice: Optional[Tuple[str, int]] = None   # pending voice (name, index) for the message
    ms: int = 0
    second: Optional[int] = None
    arg: Optional[int] = None     # TOMP id, STCF layer, FADS type, CLO kind ...
    mask: int = 0
    text_ids: Tuple[int, ...] = ()
    raw_arg: str = ""             # message argument up to the terminator

    @property
    def yields(self) -> bool:
        return self.kind in ("message", "wttm", "wtsv", "fads", "stcf", "movie", "movie_wait",
                             "choice", "clo", "end", "fade")


def _message_terminator(arg: str) -> Tuple[str, str]:
    """(raw text, terminator) as the _ZM / _MSAD copy loop sees the argument (text.md 5.2)."""
    i = 0
    while i < len(arg):
        if arg[i] == "@" and i + 1 < len(arg) and arg[i + 1] in "en":
            return arg[:i], "@" + arg[i + 1]
        i += 1
    return arg, ")"


def wait_plan(cmds: Sequence[Any]) -> List[WaitOp]:
    """Wait-relevant operations of a parsed script (``tsuki.script.Command`` list), in
    statement order (no control flow is followed)."""
    ops: List[WaitOp] = []
    f799 = 0
    voice: Optional[Tuple[str, int]] = None
    for c in cmds:
        name = c.name
        raw = str(c)
        if name in ("ZM", "MSAD"):
            arg = c.args[0].raw if c.args else ""
            if len(c.args) > 1:                      # a ',' is message text for the copy loop
                arg = ",".join(a.raw for a in c.args)
            body, term = _message_terminator(arg)
            ops.append(WaitOp(c.index, "message", raw, zm_end_mode(term, f799, False), term,
                              body.endswith("@k"), name == "MSAD", f799, voice,
                              text_ids=tuple(c.text_ids), raw_arg=body))
            voice = None
        elif name == "WNTY":
            f799 = 1 if c.int_arg(0) != 0 else 0
            ops.append(WaitOp(c.index, "wnty", raw, arg=f799))
        elif name in ("WTTM", "WT__"):
            second = c.int_arg(1) if c.present(1) else None
            ops.append(WaitOp(c.index, "wttm", raw, ms=c.int_arg(0, 32), second=second))
        elif name == "WTSV":
            ops.append(WaitOp(c.index, "wtsv", raw, ms=c.value(0) if c.obj_has(0) else -1,
                              second=c.value(1) if c.obj_has(1) else None))
        elif name == "VCWT":
            ops.append(WaitOp(c.index, "vcwt", raw))
        elif name in ("VPLY", "VPL2"):
            from tsuki.audio import parse_voice_ref
            a = ",".join(x.raw for x in c.args[:2])
            try:
                v: Optional[Tuple[str, int]] = parse_voice_ref(a)
            except (ValueError, IndexError):
                v = None
            if name == "VPL2":                       # plays at once (0x9A05D..0x9A111)
                ops.append(WaitOp(c.index, "voice_now", raw, voice=v))
                voice = None
            else:                                    # queued for the next _ZM / _MSAD
                voice = v
                ops.append(WaitOp(c.index, "voice", raw, voice=v))
        elif name == "FADS":
            ops.append(WaitOp(c.index, "fads", raw, arg=c.value(0) or 0, ms=c.value(1) or 0,
                              second=c.value(2)))
        elif name == "STCF":
            m = c.value(1)
            ops.append(WaitOp(c.index, "stcf", raw, arg=c.value(0) or 0, mask=0x3F if m is None else m))
        elif name == "STGS":
            ops.append(WaitOp(c.index, "stgs", raw, arg=c.value(0) or 0, ms=c.value(3) or 0))
        elif name == "TOMP":
            ops.append(WaitOp(c.index, "movie", raw, arg=c.value(0) or 0))
        elif name == "TOMW":
            ops.append(WaitOp(c.index, "movie_wait", raw))
        elif name in ("SELD", "SELS", "SELA"):
            ops.append(WaitOp(c.index, "choice", raw))
        elif name in ("CLO2", "CLO3", "CLO4", "CLO5"):
            ops.append(WaitOp(c.index, "clo", raw, arg=int(name[3]), ms=c.value(0) or 0))
        elif name == "FADE":
            ops.append(WaitOp(c.index, "fade", raw))
        elif name in ("END_", "RTM_", "RTMN") or name.startswith("J"):
            ops.append(WaitOp(c.index, "end", raw))
    return ops


# ---------------------------------------------------------------------------
# simulation
# ---------------------------------------------------------------------------

@dataclass
class Env:
    """What the wait logic waits for, in frames, and the glyph records of each message."""
    reveal_records: Callable[[WaitOp, Optional[WaitOp]], List[Tuple[int, int]]] = (
        lambda op, first: [(1, i + 1) for i in range(20)])
    voice_frames: Callable[[Tuple[str, int]], int] = lambda v: 120
    movie_frames: Callable[[int], int] = lambda mid: 600
    voice_load_frames: int = 1          # [H] frames in voice state 1 before playback starts
    page_option: bool = False           # config +0x9C / +0x9D == 1 ("Display End of Page Instantly")
    read: bool = False                  # treat every line as already read


@dataclass
class Event:
    frame: int
    index: int
    what: str

    def __str__(self) -> str:
        return f"{self.frame:6d}  #{self.index:<5d} {self.what}"


MOVIE_START_FRAMES = 20 + 4 + 1        # [H] 0xDF293C countdown 20, player ready latch 4, exit 1
MOVIE_SKIP_FADE = 60                    # alpha 1 -> 0 at 1/60 per frame after a skip (phase 7)


class Simulator:
    """Runs a wait plan frame by frame with an input policy.

    Order of one frame (ADV process 0x68A70, input flow 0x60710 = :func:`frame`): input ->
    :func:`adv_update` (and the statements up to the next yield when it lets the script run)
    -> :func:`tick_reveal` -> transition / layer update 0x57980 (clears the _FADS bit) ->
    window and text fade 0x534A0.  The mode of the frame start is used for both calls (the
    read-only skip stop between them, :func:`frame`, cannot happen: the ``skip`` policy treats
    every line as read).
    Policies: ``click`` (decide pressed ``react`` frames after a click wait is reached, and
    cancel during skippable movies when ``skip_movies``), ``auto`` (auto mode), ``skip``
    (latched skip, all text read), ``none`` (no input)."""

    def __init__(self, plan: Sequence[WaitOp], cfg: Optional[Config] = None,
                 env: Optional[Env] = None, policy: str = "click", react: int = 0,
                 skip_movies: bool = False, movie_seen: int = 0) -> None:
        self.plan = list(plan)
        self.cfg = cfg or Config()
        self.env = env or Env()
        self.policy = policy
        self.react = react
        self.skip_movies = skip_movies
        self.movie_seen = movie_seen
        self.st = WaitState(window_target=0, window_alpha=0.0)
        self.text_alpha = 1.0           # 0x86AA1C
        self.fast_close = 0             # 0xDF0738
        self.events: List[Event] = []
        self.pc = 0
        self.frame = 0
        self.voice_left = 0
        self.voice_play = 0
        self.fads_left = 0
        self.layer_left: Dict[int, int] = {}
        self.movie_left = 0
        self.movie_id = -1
        self.records: List[Tuple[int, int]] = []
        self.first_msg: Optional[WaitOp] = None
        self.shown_since = -1
        self.last_voice = False
        self.done = False
        self.messages: List[Dict[str, Any]] = []

    # -- policy ---------------------------------------------------------------
    def mode(self) -> AdvMode:
        if self.policy == "auto":
            return AdvMode.auto()
        if self.policy == "skip":
            # _TOMP clears 0x17DB232 and the movie state clears the latched-skip bit until its exit
            return AdvMode.skip() if self.movie_left <= 0 else AdvMode()
        return AdvMode.normal()

    def _input(self) -> FrameInput:
        st = self.st
        if self.policy != "click":
            return FrameInput()
        if self.movie_left > 0 and self.skip_movies:
            return FrameInput.press(PAD_CANCEL)
        if st.e00 & W_SHOWN and st.e00 & W_CLICK and st.e08 == 0:
            if self.shown_since < 0:
                self.shown_since = self.frame
            if self.frame - self.shown_since >= self.react:
                return FrameInput.press()
        else:
            self.shown_since = -1
        return FrameInput()

    # -- environment ------------------------------------------------------------
    def _done(self, s: WaitState, block: int) -> int:
        if block != 4:
            return 1
        if not self.records:
            return -1
        return 1 if reveal_done(self.records, s.head, s.tail) else 0

    def _voice_state(self) -> int:
        if self.voice_left <= 0:
            return 0
        return 1 if self.voice_left > self.voice_play else 2

    def log(self, idx: int, what: str) -> None:
        self.events.append(Event(self.frame, idx, what))

    def _zm_text_fade(self) -> None:
        """Start of _ZM (0x92391..0x923F5): fade the text in after a closed window."""
        st = self.st
        if not F32(st.e24) > F32(0.0):
            if F32(1.0) > F32(self.text_alpha) or st.f1103 != 0:
                self.text_alpha, st.e24 = 0.0, float(F32(0.0555555522441864))
            else:
                self.text_alpha, st.e24 = 1.0, 0.0
        st.f1103 = 0

    def _fade_update(self, mode: AdvMode) -> None:
        """0x534A0: window alpha towards its target (0.06 / frame, _CLO3 fast close -0.2604),
        text alpha T by the step E24 (both clamped, the step cleared at the ends)."""
        st = self.st
        k = max(1, mode.step)
        a = F32(st.window_alpha)
        if self.fast_close and st.window_target == 0:
            if a > 0:
                a = F32(F32(F32(k) * F32(-0.2604166567325592)) + a)
                if not F32(0.0) < a:
                    a, self.fast_close = F32(0.0), 0
            else:
                self.fast_close = 0
        else:
            if st.window_target != 0 and mode.log_shown == 0:
                if F32(1.0) > a:
                    a = F32(F32(F32(k) * F32(0.06)) + a)
                    if a > F32(1.0):
                        a = F32(1.0)
            elif a > 0:
                a = F32(a - F32(F32(k) * F32(0.06)))
                if F32(0.0) > a:
                    a = F32(0.0)
            self.fast_close = 0
        st.window_alpha = float(a)
        if F32(st.e24) != 0:
            t = F32(F32(F32(st.e24) * F32(k)) + F32(self.text_alpha))
            if F32(0.0) > t:
                t, st.e24 = F32(0.0), 0.0
            elif t > F32(1.0):
                t, st.e24 = F32(1.0), 0.0
            self.text_alpha = float(t)

    # -- script ---------------------------------------------------------------
    def _message(self, op: WaitOp, mode: AdvMode) -> None:
        st = self.st
        read = self.env.read or self.policy == "skip"
        m = zm_end_mode(op.terminator, st.f799, self.env.page_option)
        skip = mode.skip_active != 0
        started = op.voice is not None and not (skip and (read or self.cfg.ff_read_only == 0))
        if not op.msad:
            self._zm_text_fade()
            if started and op.voice is not None:
                self._start_voice(op.voice)
            self.first_msg = op
            self.records = self.env.reveal_records(op, None)
            zm_apply(st, m, started, read)
            self.last_voice = started
        else:
            if started and op.voice is not None:
                self._start_voice(op.voice)
            old = max((c for _f, c in self.records), default=0)
            self.records = self.env.reveal_records(op, self.first_msg)
            msad_apply(st, m, started, self.last_voice, old_end=old + 1)
        names = {MODE_CLICK: "click wait", MODE_CONTINUE: "continue (@n)", MODE_NOWAIT: "no click"}
        last = max((c for f, c in self.records if f), default=0)
        self.messages.append({"index": op.index, "start": self.frame, "mode": m, "f799": st.f799,
                              "voice": self.voice_play if started else 0, "counters": last,
                              "revealed": None, "advanced": None, "how": "", "arg": op.raw_arg})
        self.log(op.index, f"{'MSAD' if op.msad else 'ZM  '} {op.raw_arg:<16s} {names[m]}, "
                           f"{last} counters{', voice %d f' % self.voice_play if started else ''}"
                           f", F799={st.f799}")

    def _start_voice(self, v: Tuple[str, int]) -> None:
        self.voice_play = self.env.voice_frames(v)
        self.voice_left = self.voice_play + self.env.voice_load_frames
        self.st.voice_state = self._voice_state()

    def _run_script(self, mode: AdvMode) -> None:
        st = self.st
        while self.pc < len(self.plan):
            op = self.plan[self.pc]
            self.pc += 1
            k = op.kind
            if k == "wnty":
                wnty_apply(st, op.arg or 0)
            elif k == "voice_now":
                if op.voice is not None and mode.skip_active == 0:
                    self._start_voice(op.voice)
                    self.log(op.index, f"VPL2 voice {self.voice_play} f")
            elif k == "message":
                self._message(op, mode)
                return
            elif k == "wttm":
                wttm_apply(st, op.ms, op.second)
                sec = "" if op.second is None else f",{op.second}"
                eff = -(-st.e08 // max(1, mode.frames))
                self.log(op.index, f"WTTM({op.ms}{sec}) {st.e08} frames"
                                   f"{f' ({eff} at x{mode.frames})' if mode.frames > 1 else ''}, clicks "
                                   f"{'ignored' if st.e18 else 'end it'}")
                return
            elif k == "wtsv":
                wtsv_apply(st, None if op.ms < 0 else op.ms, op.second)
                self.log(op.index, f"WTSV {st.e08} frames")
                return
            elif k == "vcwt":
                st.voice_state = self._voice_state()
                if vcwt_apply(st):
                    self.log(op.index, f"VCWT waits {self.voice_left} frames for the voice")
                    return
            elif k == "fads":
                fads_apply(st)
                self.fads_left = fads_updates(op.ms, op.arg or 0, max(1, mode.step)) + 1
                self.log(op.index, f"FADS({op.arg},{op.ms}) {self.fads_left} frames")
                return
            elif k == "stgs":
                self.layer_left[op.arg or 0] = stgs_fade_updates(op.ms)
            elif k == "stcf":
                stcf_apply(st, [op.arg or 0], op.mask)
                self.log(op.index, f"STCF({op.arg},{op.mask}) {self.layer_left.get(op.arg or 0, 0)} frames")
                return
            elif k == "movie":
                mid = op.arg or 0
                self.movie_id = mid
                full = self.env.movie_frames(mid)
                can = movie_skippable(mid, self.movie_seen) if 4 <= mid <= 37 else True
                if self.policy == "click" and self.skip_movies and can:
                    full = min(full, MOVIE_START_FRAMES + 1 + MOVIE_SKIP_FADE + 1)
                self.movie_left = full
                self.log(op.index, f"TOMP({mid}) {full} frames, cancel / OPTIONS "
                                   f"{'skips' if can else 'ignored (not seen yet)'}")
                return                            # _TOMP yields after starting state 8 (0x9AC15)
            elif k == "movie_wait":
                if self.movie_left > 0:
                    self.pc -= 1                  # _TOMW runs again next frame while F36 != 0
                return
            elif k == "choice":
                st.e00 |= W_CHOICE
                self.log(op.index, "choice (not simulated)")
                self.done = True
                return
            elif k == "clo":
                self._clo(op)
                return
            elif k == "fade":
                st.e08 = 1                        # 0x7F94F [H]
                return
            elif k == "end":
                self.log(op.index, "end of script")
                self.done = True
                return
        self.done = True

    def _clo(self, op: WaitOp) -> None:
        st = self.st
        kind, ms = op.arg, op.ms
        if kind == 2:                             # 0x9A887
            st.new_page, st.e70 = 0, 0
            st.window_target, st.window_alpha, st.f1103 = 0, 0.0, 1
        elif kind == 3:                           # 0x9A8B8
            if st.window_target != 0:
                st.window_target, self.fast_close, st.f1103 = 0, 1, 1
        elif kind == 4:                           # 0x9A8E7: text fade-out
            if ms > 0:
                if F32(st.e24) > 0:
                    self.text_alpha = 1.0
                    st.e24 = float(F32(F32(-16.66666603088379) / F32(ms)))
                elif F32(self.text_alpha) == F32(1.0):
                    st.e24 = float(F32(F32(-16.66666603088379) / F32(ms)))
                else:
                    self.text_alpha, st.e24 = 0.0, 0.0
            else:
                self.text_alpha, st.e24 = 0.0, 0.0
        elif kind == 5:                           # 0x9A939: text fade-in
            if ms > 0 and (F32(st.e24) < 0 or F32(self.text_alpha) == 0 or st.f1103 == 1):
                self.text_alpha = 0.0
                st.e24 = float(F32(F32(16.66666603088379) / F32(ms)))
            else:
                self.text_alpha, st.e24 = 1.0, 0.0
            st.f1103, st.window_alpha, st.window_target = 0, 1.0, 1
        self.log(op.index, f"CLO{kind}({ms if kind in (4, 5) else ''})")

    # -- frame ----------------------------------------------------------------
    def step(self) -> None:
        """One frame in the eboot's order (:func:`frame`): Adv_Update and the statements it
        lets run, then tick_reveal, then the transition / layer / window updates."""
        st = self.st
        mode = self.mode()
        inp = self._input()
        st.adv_key = inp.adv_key                  # 0x60804 / 0x609F1
        st.voice_state = self._voice_state()
        cur = self.messages[-1] if self.messages else None
        e2 = adv_update(st, inp, self.cfg, mode,
                        stcf=lambda l, m: self.layer_left.get(l, 0) > 0 and bool(m & 8),
                        voice_state=self._voice_state)            # 0x61AED
        if e2.stop_voice and self.voice_left > 0:
            self.voice_left = 0
        if e2.advanced and cur is not None:
            cur["advanced"], cur["how"] = self.frame, e2.advanced
            self.log(cur["index"], f"advance by {e2.advanced}")
        if e2.script_step:
            self._run_script(mode)                # Script_Step inside Adv_Update (0xA1DAD)
        cur = self.messages[-1] if self.messages else None
        before = st.e00
        e1 = tick_reveal(st, inp, self.cfg, mode, self._done)      # 0x61A92
        if e1.reveal_clicked and cur is not None:
            self.log(cur["index"], "click completes the reveal")
        if e1.timer_clicked:
            self.log(self.plan[self.pc - 1].index if self.pc > 0 else -1,
                     "click ends the timer; the script continues next frame")
        if not (before & W_SHOWN) and st.e00 & W_SHOWN and cur is not None:
            cur["revealed"] = self.frame
            self.log(cur["index"], f"revealed, auto countdown {st.e0e} f" if st.e00 & W_CLICK
                     else "revealed")
        if self.voice_left > 0:
            self.voice_left -= 1
        for l in list(self.layer_left):
            if self.layer_left[l] > 0:
                self.layer_left[l] = max(0, self.layer_left[l] - max(1, mode.step))
        if st.e00 & W_FADS:
            self.fads_left -= 1
            if self.fads_left <= 0:
                st.e00 &= ~W_FADS
        if self.movie_left > 0:
            self.movie_left -= 1
        self._fade_update(mode)
        self.frame += 1

    def run(self, max_frames: int = 2_000_000, stop_index: Optional[int] = None) -> List[Event]:
        while not self.done and self.frame < max_frames:
            if (stop_index is not None and self.pc < len(self.plan)
                    and self.plan[self.pc].index > stop_index and self.st.e00 == 0):
                break
            self.step()
        return self.events


def movie_duration_frames(movie_id: int, lang: int, game: Optional[Path] = None) -> int:
    """Frames from _TOMP to the end of the movie state at 60 Hz: the mp4 duration (ffprobe)
    plus MOVIE_START_FRAMES [H]."""
    import subprocess
    from tsuki import game_dir
    from tsuki.catalog import movie_file_for_language
    g = game or game_dir()
    if g is None:
        raise FileNotFoundError("game folder not found")
    f = Path(g) / "image" / "movie" / movie_file_for_language(movie_id, lang)
    out = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of",
                          "csv=p=0", str(f)], capture_output=True, text=True, check=True)
    return MOVIE_START_FRAMES + int(math.ceil(float(out.stdout.strip()) * 60))


def real_env(lang: str = "en") -> Env:
    """Environment from the game data: glyph records from tsuki.textbox (the whole composite
    buffer for _MSAD), voice lengths from voice.mrg / voice2.mrg (ceil(seconds * 60)), movie
    lengths from the mp4 files."""
    from tsuki.audio import AudioBank
    from tsuki.font import Font
    from tsuki.text import ScriptText
    from tsuki import textbox as tb
    li = {"ja": 0, "en": 1, "zc": 2, "zt": 3}[lang]
    st = ScriptText()
    font = Font(lang)
    params = tb.main_box_params(li)
    banks: Dict[str, Any] = {}
    cache: Dict[str, List[Tuple[int, int]]] = {}
    mcache: Dict[int, int] = {}

    def records(op: WaitOp, first: Optional[WaitOp]) -> List[Tuple[int, int]]:
        raw = op.raw_arg if (not op.msad or first is None) else first.raw_arg + op.raw_arg
        if raw not in cache:
            text = st.expand(raw, lang)
            b = tb.layout_message(text, li, params, font=font)
            cache[raw] = [] if b.empty else [(r.flags, r.ctr) for r in b.records]
        return cache[raw]

    def voice(v: Tuple[str, int]) -> int:
        idx = v[1]
        bank = "voice" if idx <= 19999 else "voice2"
        if bank not in banks:
            banks[bank] = AudioBank(bank)
        e = banks[bank].entry(idx if bank == "voice" else idx - 20000)
        return int(math.ceil(e.duration * 60))

    def movie(mid: int) -> int:
        if mid not in mcache:
            mcache[mid] = movie_duration_frames(mid, li)
        return mcache[mid]

    return Env(reveal_records=records, voice_frames=voice, movie_frames=movie)


# ---------------------------------------------------------------------------
# census
# ---------------------------------------------------------------------------

def census(scr: Any = None) -> Dict[str, Any]:
    """Counts over all scripts: message end modes, _WNTY values before messages, what follows
    '@k@e' messages and '@n' messages, _WTTM argument shapes."""
    from collections import Counter
    from tsuki.script import AllScr
    own = scr is None
    scr = scr or AllScr()
    out: Dict[str, Counter] = {k: Counter() for k in (
        "terminator", "terminator_by_f799", "after_ke", "after_n", "wttm_second", "wttm_ms",
        "keep_terminator", "vcwt_prev", "next_message_after_n", "next_message_after_e")}
    try:
        for k in range(len(scr)):
            plan = wait_plan(scr.commands(k))
            for i, op in enumerate(plan):
                if op.kind == "message":
                    out["terminator"][op.terminator] += 1
                    out["terminator_by_f799"][(op.terminator, op.f799)] += 1
                    if op.keep:
                        out["keep_terminator"][op.terminator] += 1
                    nxt = next((p.kind for p in plan[i + 1:] if p.kind not in ("voice", "wnty")), "none")
                    if op.keep and op.terminator == "@e":
                        out["after_ke"][nxt] += 1
                    if op.terminator in ("@n", "@e"):
                        nm = next((p for p in plan[i + 1:] if p.kind in ("message", "choice", "end")), None)
                        key = "none" if nm is None else ("MSAD" if nm.kind == "message" and nm.msad
                                                         else "ZM" if nm.kind == "message" else nm.kind)
                        out["next_message_after_" + op.terminator[1]][key] += 1
                    if op.terminator == "@n":
                        nx = plan[i + 1] if i + 1 < len(plan) else None
                        out["after_n"]["MSAD" if nx is not None and nx.kind == "message" and nx.msad
                                       else (nx.kind if nx else "none")] += 1
                elif op.kind == "wttm":
                    out["wttm_second"][op.second] += 1
                    out["wttm_ms"][op.ms] += 1
                elif op.kind == "vcwt":
                    prev = next((p.kind for p in reversed(plan[:i]) if p.kind != "wnty"), "none")
                    out["vcwt_prev"][prev] += 1
    finally:
        if own:
            scr.close()
    return {k: dict(v) for k, v in out.items()}


# ---------------------------------------------------------------------------
# emulation check (development aid)
# ---------------------------------------------------------------------------

class _Emu:
    """The eboot under unicorn with every VEX instruction executed by
    tsuki.misc_audio_runtime_semantics_vex.VexCpu (unicorn 2.1 runs VEX 3-operand forms
    wrongly).  Built on tsuki.misc_adv_system_state_emu.AdvEmu (mapping, stubs, imports)."""

    INPUT = 0x40000000 + 0x700000
    ACT = 0x40000000 + 0x700100

    def __init__(self, eboot: Path) -> None:
        import capstone
        from tsuki.misc_adv_system_state_emu import TEXT_LEN, TEXT_OFF, AdvEmu
        from tsuki.misc_audio_runtime_semantics_vex import VexCpu
        self.emu = AdvEmu(eboot)
        self.vcpu = VexCpu(self.emu.uc)
        self.vcpu.code = self.emu.image[TEXT_OFF:TEXT_OFF + TEXT_LEN]
        self.patched: Dict[int, int] = {}
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        for lo, hi in ((0xA0B70, 0xA2020), (0xA2630, 0xA2BB0), (0x56160, 0x56270),
                       (0x83E50, 0x84190)):
            code = self.emu.image[lo + TEXT_OFF:hi + TEXT_OFF]
            for a, size, _mn, _ops in md.disasm_lite(code, lo):
                if code[a - lo] in (0xC4, 0xC5):
                    self.patched[a] = size
                    self.emu.uc.mem_write(a, b"\x0f\x0b")
        self.calls: Dict[int, int] = {}
        for va in (VA_SCRIPT_STEP, VA_STOP_VOICE, 0xA6180, 0xA60D0, 0xA23F0, 0x5CB10, 0x7DD10,
                   0xA0740, 0x9B760, 0x7ECB0, 0x75A60, 0xCBD20, 0x7EFC0, 0x5B1B0, 0x5D500,
                   0x5D4E0, 0x5C6C0, 0xEA070):
            self.emu.stub(va, self._counter(va))
        self.done_value: Callable[[int], int] = lambda block: 1
        self.stcf_value: Callable[[int, int], int] = lambda l, m: 0
        self.emu.stub(VA_REVEAL_DONE, lambda e: self.done_value(e.r("edi")) & 0xFFFFFFFF)
        self.emu.stub(VA_STCF_PENDING, lambda e: self.stcf_value(e.r("edi"), e.r("esi") & 0xFF))

    def _counter(self, va: int) -> Callable[[Any], int]:
        def f(e: Any) -> int:
            self.calls[va] = self.calls.get(va, 0) + 1
            return 0x174D640 if va == VA_SCRIPT_STEP else 0
        return f

    def call(self, va: int, *args: int) -> int:
        from unicorn import UcError
        from unicorn.unicorn_const import UC_ERR_INSN_INVALID
        from tsuki.misc_adv_system_state_emu import RET, STACK, STACK_SIZE
        e = self.emu
        sp = STACK + STACK_SIZE - 0x2000
        e.wr(sp, struct.pack("<Q", RET))
        e.w("rsp", sp)
        e.w("rbp", sp + 0x1000)
        for name, v in zip(("rdi", "rsi", "rdx", "rcx", "r8", "r9"), args):
            e.w(name, v)
        pc = va
        while True:
            try:
                e.uc.emu_start(pc, RET, count=2_000_000)
            except UcError as ex:
                rip = e.r("rip")
                if ex.errno == UC_ERR_INSN_INVALID and rip in self.patched:
                    pc = rip + self.vcpu.execute(rip)
                    continue
                raise RuntimeError(f"{ex} at {rip:#x}") from None
            break
        if e.r("rip") != RET:
            raise RuntimeError(f"stopped at {e.r('rip'):#x}")
        return e.r("rax")

    # state <-> memory -------------------------------------------------------
    def store(self, st: WaitState) -> None:
        """Write the modelled globals of ``st`` into the emulator memory."""
        e = self.emu
        for name, va, fmt in FIELDS:
            v = getattr(st, name)
            e.wr(va, struct.pack("<" + fmt, float(v) if fmt == "f" else int(v) & ((1 << (8 * struct.calcsize(fmt))) - 1)
                                 if fmt in "QHB" else int(v)))
        e.wr(VA_E40, bytes(st.e40))
        e.wr(VA_DIM, struct.pack("<12h", *[_i16(x) for x in st.dim]))

    def load(self, st: WaitState, cfg: Config, mode: AdvMode, inp: FrameInput) -> None:
        e = self.emu
        self.store(st)
        for name, (off, fmt) in Config.OFFSETS.items():
            e.wr(VA_CONFIG + off, struct.pack("<" + fmt, getattr(cfg, name)))
        e.wr(0x17DB230, struct.pack("<HhhH", mode.mode_word, mode.skip_active, mode.rewind, mode.log_shown))
        e.wr(0x78AF80, struct.pack("<i", mode.step))
        e.wr(0xDC7BE0, struct.pack("<H", mode.vsync))
        e.wr(0x178EC5C, struct.pack("<i", inp.wheel))
        e.wr(self.INPUT, bytes(0x40))
        e.wr(self.INPUT + 0x10, struct.pack("<Q", inp.held))
        e.wr(self.INPUT + 0x20, struct.pack("<Q", inp.newly))
        tab = struct.pack("<27Q", ACTION_DECIDE, PAD_CANCEL, *([0] * 25))
        e.wr(self.ACT, tab)
        e.wr(0xDB8778, struct.pack("<Q", self.ACT))
        # zero the dead globals so their paths stay closed
        e.wr(0x1653E0A, bytes(4))
        e.wr(0x1653E10, bytes(8))
        e.wr(0x1653E1C, bytes(4))
        e.wr(0x1653E28, bytes(4))
        e.wr(0x1653E68, bytes(8))
        e.wr(0x174DE00, b"\0")
        e.wr(0x174DE48, b"\0")
        e.wr(0x120A6D6, b"\0\0")
        e.wr(0x16541D4, struct.pack("<i", 0))
        self.calls = {}

    def read(self) -> WaitState:
        e = self.emu
        st = WaitState()
        for name, va, fmt in FIELDS:
            v = struct.unpack("<" + fmt, e.rd(va, struct.calcsize(fmt)))[0]
            setattr(st, name, float(v) if fmt == "f" else int(v))
        st.e40 = e.rd(VA_E40, 40)
        st.dim = list(struct.unpack("<12h", e.rd(VA_DIM, 24)))
        return st


def _random_case(rng: random.Random) -> Tuple[WaitState, Config, AdvMode, FrameInput]:
    def pick(*xs: Any) -> Any:
        return rng.choice(xs)
    bits = 0
    for b in (W_REVEAL, W_VOICE, W_FADS, W_CHOICE, W_STCF, W_SHOWN, W_MESSAGE, W_SYSTEM, W_CLICK):
        if rng.random() < (0.5 if b in (W_REVEAL, W_SHOWN, W_MESSAGE, W_CLICK, W_VOICE) else 0.12):
            bits |= b
    if rng.random() < 0.15:
        bits = pick(W_CLICK | W_MESSAGE, W_MESSAGE, 0, W_VOICE)
    head = float(F32(pick(0.0, rng.uniform(0, 40), 768.0, 76800.0, rng.uniform(0, 3))))
    st = WaitState(
        e00=bits, e08=pick(0, 0, 1, rng.randrange(1, 200), -3), e0e=pick(0, 0, rng.randrange(1, 300), 61, 60, -2),
        e18=pick(0, 1, 0), e24=float(F32(pick(0.0, 0.0, 0.05, -0.1))), e30=rng.getrandbits(40) if rng.random() < 0.5 else 0,
        e40=bytes(rng.choice((8, 0x3F, 1, 0x10, 0x20)) for _ in range(40)),
        e70=pick(0, 1, 4, 5, rng.getrandbits(3)), e7c=pick(0, 1), head=head,
        tail=float(F32(head - pick(0.0, 2.857, rng.uniform(0, 5), -1.0))),
        width=float(F32(pick(2.857143, 0.0, 1.5))), frame_ctr=rng.randrange(-0x8000, 0x8000),
        adv_key=pick(0, 0, 1), new_page=pick(0, 1), window_target=pick(0, 1, 1),
        window_alpha=float(F32(pick(1.0, 1.0, 0.5))), read_sel=pick(0, 1), voice_state=pick(0, 0, 1, 2),
        fade_state=pick(0, 0, 1, 2, 3), f799=pick(0, 1), f823=pick(0, 1, 2, 5, 11), f988=pick(0, 0, 0, 1),
        f1103=pick(0, 1), f1123=pick(0, 0, 1), blocks=pick(1, 1, 3, 5), scene_jump=pick(0, 2),
        dim=[rng.randrange(0, 300) for _ in range(12)])
    cfg = Config(text_speed_unread=pick(35, 0, 100, 250, 15), text_speed_read=pick(15, 0, 35),
                 auto_wait_ms=pick(2200, 0, 10000, 500), ff_read_only=pick(0, 1),
                 interrupt_voice=pick(0, 1), page_instant_unread=pick(0, 1), page_instant_read=pick(0, 1),
                 auto_page_ms=pick(3000, 1000, 0))
    mode = pick(AdvMode.normal(), AdvMode.auto(), AdvMode.skip(), AdvMode(mode_word=3, skip_active=1, step=64),
                AdvMode(skip_active=1, step=rng.randrange(1, 65)), AdvMode(rewind=1, step=64),
                AdvMode(mode_word=pick(0, 2), step=64), AdvMode(log_shown=1),
                AdvMode(vsync=pick(2, 3, 5)))
    btn = pick(0, 0, ACTION_DECIDE, PAD_DOWN, PAD_R2)
    inp = FrameInput(held=btn | pick(0, ACTION_DECIDE), newly=pick(0, btn), mouse=pick(0, 0, 1, 2, 3),
                     wheel=pick(0, 0, -1, 1), adv_key=pick(0, 0, 1))
    return st, cfg, mode, inp


def _diff(a: WaitState, b: WaitState) -> List[str]:
    out = []
    for f in fields(WaitState):
        x, y = getattr(a, f.name), getattr(b, f.name)
        if isinstance(x, float) or isinstance(y, float):
            if F32(x).tobytes() != F32(y).tobytes():
                out.append(f"{f.name}: model {x!r} emu {y!r}")
        elif f.name == "e40":
            if bytes(x) != bytes(y):
                out.append("e40")
        elif f.name == "dim":
            if [_i16(v) for v in x] != [_i16(v) for v in y]:
                out.append(f"dim: model {x} emu {y}")
        elif int(x) != int(y):
            out.append(f"{f.name}: model {x:#x} emu {y:#x}" if isinstance(x, int) else f"{f.name}")
    return out


def emu_check(eboot: Optional[Path] = None, seeds: Iterable[int] = range(200),
              verbose: bool = False) -> Dict[str, Any]:
    """Run 0xA2630 and 0xA0B70 from the eboot on random states and compare with
    :func:`tick_reveal` / :func:`adv_update`.  Returns counts and the first mismatches."""
    if eboot is None:
        from tsuki import game_dir
        g = game_dir()
        if g is None:
            raise FileNotFoundError("game folder not found")
        eboot = g / "eboot.bin"
    em = _Emu(Path(eboot))
    res: Dict[str, Any] = {"cases": 0, "tick_mismatch": 0, "update_mismatch": 0, "examples": [],
                           "script_step": 0, "advanced": {}}
    for seed in seeds:
        rng = random.Random(seed)
        st, cfg, mode, inp = _random_case(rng)
        done_map = {4: rng.choice((1, 0, -1)), 6: rng.choice((1, 0)), 8: rng.choice((1, 0))}
        pend = {l: rng.random() < 0.3 for l in range(40)}
        # --- 0xA2630
        model = st.copy()
        eff1 = tick_reveal(model, inp, cfg, mode, lambda s, b: done_map[b])
        em.load(st, cfg, mode, inp)
        em.done_value = lambda b: done_map.get(b - 0, 1)
        em.call(VA_TICK_REVEAL, mode.frames & 0xFFFFFFFF, inp.mouse, em.INPUT)
        got = em.read()
        d = _diff(model, got)
        calls1 = (em.calls.get(0xA6180, 0), em.calls.get(0xA60D0, 0))
        if calls1 != (eff1.choice_update, eff1.choice_idle):
            d.append(f"choice calls model {(eff1.choice_update, eff1.choice_idle)} emu {calls1}")
        res["cases"] += 1
        if d:
            res["tick_mismatch"] += 1
            if len(res["examples"]) < 8:
                res["examples"].append(("tick", seed, d))
        # --- 0xA0B70 on the state the emulator produced
        st2 = got
        model2 = st2.copy()
        eff2 = adv_update(model2, inp, cfg, mode, stcf=lambda l, m: pend[l])
        em.load(st2, cfg, mode, inp)
        em.stcf_value = lambda l, m: 1 if pend[l] else 0
        em.call(VA_ADV_UPDATE, mode.frames & 0xFFFFFFFF, inp.mouse, em.INPUT)
        got2 = em.read()
        d2 = _diff(model2, got2)
        c2 = (em.calls.get(VA_STOP_VOICE, 0), em.calls.get(VA_SCRIPT_STEP, 0))
        if c2 != (eff2.stop_voice, eff2.script_step):
            d2.append(f"calls (stop_voice, script_step) model {(eff2.stop_voice, eff2.script_step)} emu {c2}")
        res["script_step"] += eff2.script_step
        if eff2.advanced:
            res["advanced"][eff2.advanced] = res["advanced"].get(eff2.advanced, 0) + 1
        if d2:
            res["update_mismatch"] += 1
            if len(res["examples"]) < 8:
                res["examples"].append(("update", seed, d2))
        if verbose and (d or d2):
            print(seed, d, d2)
    return res


def emu_check_reveal_done(eboot: Optional[Path] = None, cases: int = 300, seed: int = 7) -> Dict[str, Any]:
    """Run 0xCA070 on synthetic text blocks (records with random flags / counters) and random
    head / tail; compare the return value with :func:`reveal_done`."""
    if eboot is None:
        from tsuki import game_dir
        g = game_dir()
        if g is None:
            raise FileNotFoundError("game folder not found")
        eboot = g / "eboot.bin"
    import capstone
    from tsuki.misc_adv_system_state_emu import TEXT_OFF
    em = _Emu(Path(eboot))
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    lo, hi = 0xCA070, 0xCA690
    code = em.emu.image[lo + TEXT_OFF:hi + TEXT_OFF]
    for a, size, _mn, _ops in md.disasm_lite(code, lo):
        if code[a - lo] in (0xC4, 0xC5):
            em.patched[a] = size
            em.emu.uc.mem_write(a, b"\x0f\x0b")
    del em.emu.stubs[VA_REVEAL_DONE]
    em.emu.stub(0x3ED90, 1)          # Lang_Index (only for '@b' records)
    rng = random.Random(seed)
    block = 4
    hdr = 0x177A210 + 0x190 * block
    recs_va = 0x40000000 + 0x600000
    bad = []
    for i in range(cases):
        n = rng.randrange(1, 40)
        recs: List[Tuple[int, int]] = []
        ctr = 1
        for k in range(n):
            fl = rng.choice((1, 1, 1, 1, 0, 3))
            if fl == 0:
                recs.append((0, 0))
                continue
            c = ctr if fl == 1 else max(1, ctr - rng.randrange(0, 3))
            recs.append((fl, c))
            if fl == 1:
                ctr += 1
        head = float(F32(rng.choice((rng.uniform(0, ctr + 4), float(ctr), ctr + 2.857, 768.0, 76800.0))))
        tail = float(F32(head - rng.choice((2.857143, 0.0, rng.uniform(0, 4), -0.5))))
        e = em.emu
        e.wr(hdr, bytes(0x190))
        e.wr(hdr, struct.pack("<H", n))
        e.wr(hdr + 0x14, struct.pack("<H", 1))
        e.wr(hdr + 0x100, struct.pack("<f", 17.0))
        e.wr(hdr + 0x180, struct.pack("<Q", recs_va))
        e.wr(0x178BE30 + 4 * block, struct.pack("<i", 1))
        buf = bytearray(0x30 * n)
        for k, (fl, c) in enumerate(recs):
            struct.pack_into("<QHHHH4HH", buf, 0x30 * k, fl, 0xFFFF, 0, 0, 0x22, 0, 0, 10, 10, c)
            struct.pack_into("<4f", buf, 0x30 * k + 0x1C, float(k), 0.0, 0.25, 0.25)
        e.wr(recs_va, bytes(buf))
        out = 0x40000000 + 0x680000
        e.vex.y[0] = bytearray(F32(head).tobytes() + bytes(28))
        from unicorn import x86_const as ux
        e.uc.reg_write(ux.UC_X86_REG_XMM0, struct.unpack("<I", F32(head).tobytes())[0])
        e.uc.reg_write(ux.UC_X86_REG_XMM1, struct.unpack("<I", F32(tail).tobytes())[0])
        r = em.call(0xCA070, block, 0x30, out, out + 8) & 0xFFFFFFFF
        want = reveal_done(recs, head, tail)
        if (r == 1) != want:
            bad.append((i, head, tail, recs[-3:], r, want))
    return {"cases": cases, "mismatch": len(bad), "examples": bad[:5]}


def emu_check_movie(eboot: Optional[Path] = None, cases: int = 400, seed: int = 11) -> Dict[str, Any]:
    """Run the movie state update 0x5E530 (phases 2..7) on random records and compare with
    :func:`movie_update` (skip gate, skip buttons, phase changes, fades, stop / end calls)."""
    if eboot is None:
        from tsuki import game_dir
        g = game_dir()
        if g is None:
            raise FileNotFoundError("game folder not found")
        eboot = g / "eboot.bin"
    import capstone
    from tsuki.misc_adv_system_state_emu import TEXT_OFF
    em = _Emu(Path(eboot))
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    lo, hi = 0x5E530, 0x5E970
    code = em.emu.image[lo + TEXT_OFF:hi + TEXT_OFF]
    for a, size, _mn, _ops in md.disasm_lite(code, lo):
        if code[a - lo] in (0xC4, 0xC5):
            em.patched[a] = size
            em.emu.uc.mem_write(a, b"\x0f\x0b")
    state = {"playing": 1, "lang": 0, "ready": 1}
    for va in (0x5E490, 0xE4F00, 0x88F0, 0x98B0, 0x9EA0, 0x5C080):
        em.emu.stub(va, em._counter(va))
    em.emu.stub(0xEE880, lambda e: state["ready"])
    em.emu.stub(0xEE860, lambda e: state["playing"])
    em.emu.stub(0x3ED90, lambda e: state["lang"])
    rng = random.Random(seed)
    rec_va = 0x40000000 + 0x690000
    inp_va = 0x40000000 + 0x6A0000
    bad = []
    stats: Dict[str, int] = {}
    ids = [4, 5, 6, 7, 8, 13, 20, 35, 36, 37, 38, 39, 42, 45, 46]
    se_ids = ids + [0, 1, 2, 3, 9, 12, 14, 34, 40, 44, 47, 50]
    for i in range(cases):
        phase = rng.choice((1, 2, 3, 3, 4, 5, 5, 5, 6, 7))
        rec = MovieRec(movie_id=rng.choice(se_ids if phase == 1 else ids), phase=phase,
                       frame=rng.choice((0, 10, 59, 60, 200, 209, 329, 330, 400, 419, 420, 21599,
                                         21899, 22000, 6419, 6420)),
                       alpha=float(F32(rng.choice((0.0, 0.3, 0.5, 1.0, 0.01)))),
                       fade=float(F32(rng.choice((0.0, 1 / 60.0)))), skip=rng.choice((0, 0, 1)),
                       aux=float(F32(rng.choice((0.0, 0.5, 0.995, 1.0)))))
        seen = rng.choice((0, 0x3F, rng.getrandbits(6)))
        lang = rng.choice((0, 1, 2))
        vsync = rng.choice((1, 1, 2, 3))
        newly = rng.choice((0, PAD_CANCEL, PAD_OPTIONS, ACTION_DECIDE, PAD_CANCEL | ACTION_DECIDE))
        playing = rng.choice((1, 1, 0))
        countdown = rng.choice((0, 0, 0, 1, 5))
        ready = rng.choice((1, 1, 0))
        inp = FrameInput(newly=newly)
        want, act = movie_update(rec, inp, bool(playing), seen, lang, vsync, countdown, bool(ready))
        want_se = movie_se_calls(rec, vsync, countdown, bool(ready))
        e = em.emu
        state["playing"], state["lang"], state["ready"] = playing, lang, ready
        buf = bytearray(0x50)
        struct.pack_into("<i", buf, 0x18, rec.movie_id)
        struct.pack_into("<iiiff", buf, 0x30, rec.phase, rec.frame, 1, rec.alpha, rec.fade)
        struct.pack_into("<B", buf, 0x48, rec.skip)
        struct.pack_into("<f", buf, 0x4C, rec.aux)
        e.wr(rec_va, bytes(buf))
        e.wr(inp_va, bytes(0x40))
        e.wr(inp_va + 0x20, struct.pack("<Q", newly))
        e.wr(0xDC7BC0, struct.pack("<Q", inp_va))
        e.wr(0xDC7BE0, struct.pack("<H", vsync))
        e.wr(0xDF293C, struct.pack("<i", countdown))
        e.wr(0x164C620 + 0xA, struct.pack("<h", seen))
        e.wr(0x2085164, b"\0")
        em.calls = {}
        em.call(VA_MOVIE_UPDATE, rec_va)
        b = e.rd(rec_va, 0x50)
        got = MovieRec(movie_id=rec.movie_id, phase=struct.unpack_from("<i", b, 0x30)[0],
                       frame=struct.unpack_from("<i", b, 0x34)[0],
                       alpha=struct.unpack_from("<f", b, 0x3C)[0], fade=struct.unpack_from("<f", b, 0x40)[0],
                       skip=b[0x48], aux=struct.unpack_from("<f", b, 0x4C)[0])
        gact = "end" if em.calls.get(0x5C080) else ("stop" if e.rd(0x2085164, 1)[0] else "")
        d = []
        for f in ("phase", "frame", "skip"):
            if getattr(got, f) != getattr(want, f):
                d.append(f"{f} model {getattr(want, f)} emu {getattr(got, f)}")
        for f in ("alpha", "fade", "aux"):
            if F32(getattr(got, f)).tobytes() != F32(getattr(want, f)).tobytes():
                d.append(f"{f} model {getattr(want, f)} emu {getattr(got, f)}")
        if gact != act:
            d.append(f"action model {act!r} emu {gact!r}")
        gse = em.calls.get(0x5E490, 0)
        if gse != want_se:
            d.append(f"0x5E490 calls model {want_se} emu {gse}")
        stats["se"] = stats.get("se", 0) + gse
        key = f"{rec.phase}->{got.phase}{'/' + gact if gact else ''}"
        stats[key] = stats.get(key, 0) + 1
        if d:
            bad.append((i, rec, seen, lang, vsync, newly, playing, countdown, d))
    return {"cases": cases, "mismatch": len(bad), "transitions": stats, "examples": bad[:5]}


# ---------------------------------------------------------------------------
# frame-level emulation: the eboot's 0x60710 with the real 0xA0B70 / 0xA2630 inside
# ---------------------------------------------------------------------------

def _eboot_or_default(eboot: Optional[Path]) -> Path:
    if eboot is not None:
        return Path(eboot)
    from tsuki import game_dir
    g = game_dir()
    if g is None:
        raise FileNotFoundError("game folder not found")
    return g / "eboot.bin"


@dataclass
class MiniOp:
    """A statement of an :func:`emu_check_frames` scenario (only its wait-state part)."""
    kind: str                     # "zm", "wttm", "wtsv", "vcwt", "wnty"
    mode: int = MODE_CLICK        # zm: end mode
    glyphs: int = 20              # zm: plain glyph records 1..glyphs
    voice: int = 0                # zm: voice length in frames (0 = no voice)
    read: bool = True             # zm: line already read (0x75C90; reveal speed +0x22)
    ms: int = 0                   # wttm
    second: Optional[int] = None  # wttm
    arg: int = 0                  # wnty


class MiniScript:
    """Interpreter of a :class:`MiniOp` list: what Script_Step does to the wait state until
    the next yield.  The model side and the eboot side of :func:`emu_check_frames` each run
    their own instance on their own state."""

    def __init__(self, ops: Sequence[MiniOp]) -> None:
        self.ops = list(ops)
        self.pc = 0
        self.records: List[Tuple[int, int]] = []
        self.line_read = True
        self.voice_left = 0
        self.voice_play = 0
        self.started: List[int] = []         # pc of every zm started

    def voice_state(self) -> int:
        if self.voice_left <= 0:
            return 0
        return 1 if self.voice_left > self.voice_play else 2

    def done(self, head: float, tail: float) -> int:
        if not self.records:
            return -1
        return 1 if reveal_done(self.records, head, tail) else 0

    def run(self, st: WaitState) -> None:
        while self.pc < len(self.ops):
            op = self.ops[self.pc]
            self.pc += 1
            if op.kind == "wnty":
                wnty_apply(st, op.arg)
            elif op.kind == "zm":
                started = op.voice > 0
                if started:
                    self.voice_play, self.voice_left = op.voice, op.voice + 1
                    st.voice_state = self.voice_state()
                self.records = [(1, i + 1) for i in range(op.glyphs)]
                self.line_read = op.read
                zm_apply(st, op.mode, started, op.read)
                self.started.append(self.pc - 1)
                return
            elif op.kind == "wttm":
                wttm_apply(st, op.ms, op.second)
                return
            elif op.kind == "wtsv":
                wtsv_apply(st)
                return
            elif op.kind == "vcwt":
                if vcwt_apply(st):
                    return
            else:
                raise ValueError(op.kind)

    def end_frame(self, stopped: bool) -> None:
        if stopped:
            self.voice_left = 0
        if self.voice_left > 0:
            self.voice_left -= 1


class _FrameEmu(_Emu):
    """0x60710 under unicorn with the real Adv_Update 0xA0B70 and tick_reveal 0xA2630 (and the
    small helpers they share with it: 0xD47C0, 0x423D0, 0x5C660, 0xE3A10 run natively); every
    other direct callee of 0x60710 is stubbed (0 except 0x75C90 = line read).  Script_Step runs
    a :class:`MiniScript` on the emulator's memory; 0xCA070 answers from its glyph records."""

    NATIVE = frozenset((VA_ADV_UPDATE, VA_TICK_REVEAL, 0xD47C0, 0x423D0, 0x5C660, 0xE3A10))
    MOUSE_FLAGS = 0x178EC54       # [0x178EC20 + 0x34] -> ebx (0x60739)
    INPUT_PTR = 0xDC7BC0          # -> r14, copied to the stack (0x60765..0x60783)

    def __init__(self, eboot: Optional[Path]) -> None:
        import capstone
        from unicorn import UC_HOOK_CODE
        from tsuki.misc_adv_system_state_emu import PLT_HI, PLT_LO, TEXT_OFF
        super().__init__(_eboot_or_default(eboot))
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        lo, hi = VA_ADV_INPUT, VA_ADV_INPUT_END
        code = self.emu.image[lo + TEXT_OFF:hi + TEXT_OFF]
        targets = set()
        for a, size, mn, ops in md.disasm_lite(code, lo):
            if code[a - lo] in (0xC4, 0xC5):
                self.patched[a] = size
                self.emu.uc.mem_write(a, b"\x0f\x0b")
            if mn == "call" and ops.startswith("0x"):
                targets.add(int(ops, 16))
        self.line_read = True
        self.script: Optional[MiniScript] = None
        for t in sorted(targets):
            if t in self.NATIVE or PLT_LO <= t < PLT_HI or lo <= t < hi or t in self.emu.stubs:
                continue
            self.emu.stub(t, (lambda e: 1 if self.line_read else 0) if t == 0x75C90 else 0)
        self.emu.stubs[VA_SCRIPT_STEP] = self._script_step
        self.done_value = self._done
        self.order: List[Tuple[int, int]] = []
        for va in (VA_ADV_UPDATE, VA_TICK_REVEAL):
            self.emu.uc.hook_add(UC_HOOK_CODE, self._enter, begin=va, end=va)

    def _enter(self, uc: Any, addr: int, size: int, _: Any) -> None:
        if addr in (VA_ADV_UPDATE, VA_TICK_REVEAL):
            self.order.append((addr, self.emu.u64(self.emu.r("rsp"))))

    def _script_step(self, e: Any) -> int:
        self.calls[VA_SCRIPT_STEP] = self.calls.get(VA_SCRIPT_STEP, 0) + 1
        if self.script is not None:
            st = self.read()
            self.script.run(st)
            self.store(st)
            self.line_read = self.script.line_read
        return 0x174D640

    def _done(self, block: int) -> int:
        if block != 4:
            return 1
        if self.script is None:
            return 1
        head = struct.unpack("<f", self.emu.rd(0x1653E80, 4))[0]
        tail = struct.unpack("<f", self.emu.rd(0x1653E84, 4))[0]
        return self.script.done(head, tail)

    def begin(self, st: WaitState, cfg: Config) -> None:
        """Initial state (written once; later frames keep whatever the code leaves)."""
        e = self.emu
        self.load(st, cfg, AdvMode(), FrameInput())
        e.wr(self.INPUT_PTR, struct.pack("<Q", self.INPUT))
        e.wr(self.INPUT, bytes(0x60))
        e.wr(0x178EC20, bytes(0x70))                   # pointer record (mouse flags, cursor)
        e.wr(0x17DB230, bytes(16))                     # mode words, backlog, HUD flags
        for va, n in ((0xDF29D0, 0x40), (0x120A6D0, 0x20), (0xE20CC0, 0x14)):
            e.wr(va, bytes(n))

    def run_frame(self, mode_word: int, vsync: int, inp: FrameInput, voice_state: int) -> None:
        e = self.emu
        e.wr(0x17DB230, struct.pack("<H", mode_word))
        e.wr(0xDC7BE0, struct.pack("<H", vsync))
        e.wr(self.INPUT + 0x10, struct.pack("<Q", inp.held))
        e.wr(self.INPUT + 0x20, struct.pack("<Q", inp.newly))
        e.wr(self.MOUSE_FLAGS, struct.pack("<i", inp.mouse))
        e.wr(0x178EC5C, struct.pack("<i", inp.wheel))
        e.wr(0xDD8E50, struct.pack("<H", voice_state))
        self.calls = {}
        self.order = []
        self.call(VA_ADV_INPUT)


def emu_frame_order(eboot: Optional[Path] = None) -> Dict[str, List[Tuple[int, int]]]:
    """Run 0x60710 once in normal, auto and latched-skip mode and return, per mode, the entry
    order of 0xA0B70 / 0xA2630 as (function VA, return address) pairs."""
    fe = _FrameEmu(eboot)
    out: Dict[str, List[Tuple[int, int]]] = {}
    for name, mw in (("normal", 0), ("auto", 1), ("skip", 2)):
        fe.script = MiniScript([MiniOp("zm", glyphs=5)])
        fe.begin(WaitState(window_target=1), Config())
        fe.run_frame(mw, 1, FrameInput(), 0)
        out[name] = list(fe.order)
    return out


def _random_scenario(rng: random.Random) -> Tuple[List[MiniOp], Config, List[int], int, List[FrameInput]]:
    def pick(*xs: Any) -> Any:
        return rng.choice(xs)
    ops: List[MiniOp] = []
    for _ in range(40):
        k = rng.random()
        if k < 0.55:
            ops.append(MiniOp("zm", mode=pick(0, 0, 0, 1, 2), glyphs=rng.randrange(1, 30),
                              voice=pick(0, 0, rng.randrange(5, 120)), read=rng.random() < 0.7))
        elif k < 0.75:
            ops.append(MiniOp("wttm", ms=pick(1, 30, 100, 400), second=pick(None, 0, 1, 1)))
        elif k < 0.8:
            ops.append(MiniOp("wtsv"))
        elif k < 0.9:
            ops.append(MiniOp("vcwt"))
        else:
            ops.append(MiniOp("wnty", arg=pick(0, 1)))
    cfg = Config(text_speed_unread=pick(35, 35, 10, 0), text_speed_read=pick(15, 0),
                 auto_wait_ms=pick(500, 200, 2200), auto_page_ms=pick(800, 3000),
                 ff_read_only=pick(0, 1), interrupt_voice=pick(0, 1))
    frames = 160
    mw0 = pick(0, 0, 1, 2, 3)
    switch = rng.randrange(0, frames)
    mw1 = pick(0, 1, 2, 3)
    mode_words = [mw0 if f < switch else mw1 for f in range(frames)]
    vsync = pick(1, 1, 1, 2, 3)
    inputs: List[FrameInput] = []
    held = 0
    mouse_held = 0
    for _ in range(frames):
        prev, mprev = held, mouse_held
        if rng.random() < 0.12:
            held ^= ACTION_DECIDE
        if rng.random() < 0.04:
            held ^= PAD_DOWN
        if rng.random() < 0.05:
            mouse_held ^= 1
        newly = held & ~prev
        mouse = mouse_held | (2 if mouse_held and not mprev else 0)
        inputs.append(FrameInput(held=held, newly=newly, mouse=mouse,
                                 wheel=-1 if rng.random() < 0.02 else 0,
                                 adv_key=1 if newly & PAD_DOWN else 0))
    return ops, cfg, mode_words, vsync, inputs


def emu_check_frames(eboot: Optional[Path] = None, seeds: Iterable[int] = range(20),
                     verbose: bool = False,
                     model: Callable[..., FrameResult] = frame) -> Dict[str, Any]:
    """Run random multi-frame scenarios (a :class:`MiniScript`, random decide / d-pad /
    mouse / wheel input, auto and latched-skip mode words that may change mid-run, 1..3
    vsyncs, random config) through the eboot's 0x60710 and through :func:`frame`; compare every
    modelled global after every frame, the Script_Step and voice-stop calls, and the call
    order of 0xA0B70 / 0xA2630.  ``model`` replaces :func:`frame` (to show that the check
    rejects another frame order)."""
    fe = _FrameEmu(eboot)
    res: Dict[str, Any] = {"scenarios": 0, "frames": 0, "mismatch_frames": 0, "examples": [],
                           "order": {}, "advanced": {}, "reveal_clicked": 0, "timer_clicked": 0,
                           "skip_stopped": 0, "script_step": 0, "messages": 0}
    for seed in seeds:
        rng = random.Random(seed)
        ops, cfg, mode_words, vsync, inputs = _random_scenario(rng)
        st = WaitState(window_target=1, window_alpha=1.0)
        ms_model, ms_emu = MiniScript(ops), MiniScript(ops)
        fe.script = ms_emu
        fe.line_read = True
        fe.begin(st, cfg)
        res["scenarios"] += 1
        for f, (mw, inp) in enumerate(zip(mode_words, inputs)):
            # --- model: what 0x60710 derives before the calls, then frame()
            mode = AdvMode.from_mode_word(mw, vsync)
            if mode.skip_active and cfg.ff_read_only and not ms_model.line_read:   # 0x612D0
                mode = replace(mode, skip_active=0, step=1)
            st.adv_key = inp.adv_key
            st.voice_state = ms_model.voice_state()
            r = model(st, inp, cfg, mode, done=lambda s, b: ms_model.done(s.head, s.tail) if b == 4 else 1,
                      script=lambda: ms_model.run(st), line_read=lambda: ms_model.line_read)
            # --- eboot
            fe.run_frame(mw, vsync, inp, ms_emu.voice_state())
            got = fe.read()
            d = _diff(st, got)
            calls = (fe.calls.get(VA_STOP_VOICE, 0), fe.calls.get(VA_SCRIPT_STEP, 0))
            if calls != (r.update.stop_voice, r.update.script_step):
                d.append(f"calls (stop_voice, script_step) model {(r.update.stop_voice, r.update.script_step)} emu {calls}")
            seq = tuple(fe.order)
            res["order"][seq] = res["order"].get(seq, 0) + 1
            if seq != ((VA_ADV_UPDATE, CALL_ADV_UPDATE + 5), (VA_TICK_REVEAL, CALL_TICK_REVEAL + 5)):
                d.append(f"call order {[(hex(a), hex(b)) for a, b in seq]}")
            ms_model.end_frame(r.update.stop_voice > 0)
            ms_emu.end_frame(calls[0] > 0)
            res["frames"] += 1
            res["script_step"] += r.update.script_step
            if r.update.advanced:
                res["advanced"][r.update.advanced] = res["advanced"].get(r.update.advanced, 0) + 1
            res["reveal_clicked"] += r.reveal.reveal_clicked
            res["timer_clicked"] += r.reveal.timer_clicked
            res["skip_stopped"] += r.skip_stopped
            if d:
                res["mismatch_frames"] += 1
                if len(res["examples"]) < 8:
                    res["examples"].append((seed, f, d))
                if verbose:
                    print(seed, f, d)
                st = got                                  # continue from the eboot's state
                ms_model.__dict__.update({k: (list(v) if isinstance(v, list) else v)
                                          for k, v in ms_emu.__dict__.items()})
        res["messages"] += len(ms_model.started)
    res["order"] = {" -> ".join(f"{a:#x}@{b:#x}" for a, b in k): v for k, v in res["order"].items()}
    return res


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="tsuki.wait_model")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("census")
    p = sub.add_parser("plan")
    p.add_argument("script")
    t = sub.add_parser("timeline")
    t.add_argument("script")
    t.add_argument("--policy", default="click", choices=("click", "auto", "skip", "none"))
    t.add_argument("--lang", default="en")
    t.add_argument("--limit", type=int, default=0)
    t.add_argument("--react", type=int, default=0)
    t.add_argument("--page-option", action="store_true")
    e = sub.add_parser("emu")
    e.add_argument("--seeds", type=int, default=300)
    e.add_argument("--frames", type=int, default=0,
                   help="also run N multi-frame scenarios through the eboot's 0x60710")
    a = ap.parse_args(argv)
    try:
        sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[union-attr]
    except AttributeError:
        pass
    if a.cmd == "census":
        for k, v in census().items():
            print(k, dict(sorted(v.items(), key=lambda kv: -kv[1])))
        return 0
    if a.cmd == "emu":
        r = emu_check(seeds=range(a.seeds))
        print({k: v for k, v in r.items() if k != "examples"})
        for ex in r["examples"]:
            print(ex)
        rd = emu_check_reveal_done()
        print("reveal_done", rd)
        bad = r["tick_mismatch"] or r["update_mismatch"] or rd["mismatch"]
        if a.frames:
            rf = emu_check_frames(seeds=range(a.frames))
            print("frames", {k: v for k, v in rf.items() if k != "examples"})
            for ex in rf["examples"]:
                print(ex)
            bad = bad or rf["mismatch_frames"]
        return 0 if not bad else 1
    from tsuki.script import AllScr
    with AllScr() as scr:
        plan = wait_plan(scr.commands(scr.index(a.script)))
    if a.cmd == "plan":
        for op in plan:
            if op.kind != "voice":
                print(f"#{op.index:<5d} {op.kind:<10s} {op.text}")
        return 0
    env = real_env(a.lang)
    env.page_option = a.page_option
    sim = Simulator(plan, env=env, policy=a.policy, react=a.react)
    ev = sim.run(stop_index=a.limit or None)
    for x in ev:
        print(x)
    print(f"frames: {sim.frame} ({sim.frame / 60:.1f} s)")
    return 0


if __name__ == "__main__":
    sys.exit(_main())
