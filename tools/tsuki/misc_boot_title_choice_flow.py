"""System flow of TSUKIHIME -A piece of blue glass moon- (PS4 CUSA47999, app 01.01).

Boot sequence (system save, notice screens), title screen (intro, background state, menu) and
the in-game choice menu (``_SELR`` / ``_SELD``), modelled as data-driven state machines and
per-screen draw lists.  Documentation: ``docs/formats/system_flow.md``.

Every constant below was read from eboot.bin (text VA = file offset - 0x2280, data VA = file
offset - 0x365FA0 + 0x600000); :func:`read_tables` re-reads them from the user's eboot and the
tests compare both.  Coordinates: a draw happens on a *virtual canvas* ``(cw, ch)`` set by VA
0xDCBE0; one canvas unit is ``1920 / cw`` x ``1080 / ch`` output pixels.  A texture is drawn by
VA 0xEB780 (whole sprite) or 0xEB890 / 0xEB9C0 (sub-rectangle ``(x0, y0, x1, y1)`` in texels);
draw flag bit 0x10 multiplies the texel size by the current scale (VA 0xD9DD0), otherwise one
texel is one canvas unit; ``flags & 0xF`` is the anchor (0..8 = TL, TC, TR, ML, C, MR, BL, BC,
BR; factor tables VA 0x2D5800 / 0x2D5830); bit 0x20 enables rotation (VA 0xDD6D0).

CLI::

    python -m tsuki.misc_boot_title_choice_flow boot [--first-run] [--damaged] [--g10 0|1]
    python -m tsuki.misc_boot_title_choice_flow title-state [--bits 560,383,562]
    python -m tsuki.misc_boot_title_choice_flow tables              # eboot tables vs constants
    python -m tsuki.misc_boot_title_choice_flow render-title out.png --lang en --g2 0 --cursor 1
    python -m tsuki.misc_boot_title_choice_flow render-choice out.png --lang ja [--cursor 1]
    python -m tsuki.misc_boot_title_choice_flow render-boot out_dir --lang en
"""
from __future__ import annotations

import argparse
import math
import struct
import sys
from dataclasses import dataclass, field, replace
from pathlib import Path
from typing import TYPE_CHECKING, Iterable, Optional, Sequence

import numpy as np

if TYPE_CHECKING:  # pragma: no cover
    from .catalog_eboot import Eboot

F32 = np.float32
LANG_CODES = ("ja", "en", "zc", "zt", "ko")

# ---------------------------------------------------------------------------
# draw model
# ---------------------------------------------------------------------------
ANCHOR_FX = (0.0, 0.5, 1.0, 0.0, 0.5, 1.0, 0.0, 0.5, 1.0)     # VA 0x2D5800
ANCHOR_FY = (0.0, 0.0, 0.0, 0.5, 0.5, 0.5, 1.0, 1.0, 1.0)     # VA 0x2D5830

BLEND_NORMAL = 0
BLEND_PS_ADD = 0xB        # draw-effect id 11 -> surface mode 11 'ps_add' (engine.md 4.3) [H formula]
BLEND_GLOW = 0x34         # title moon glow; id 52 is outside the 1..51 effect table [H: additive]


@dataclass(frozen=True)
class Draw:
    """One draw call of a screen.

    ``kind``: 'tex' (texture slot), 'fill' (VA 0xDD1C0 rectangle), 'movie' (AvPlayer texture),
    'text' (a laid-out text block, VA 0xC8F50) or 'widget' (UI widget frame, VA 0xCFC90).
    ``source`` names the image: ('allpac3', '<SET>_<LANG>.MRG' or 'SELECT_PARTS.MRG', child),
    ('allpac3', 'CAUTION_EN.MZP', -1), ('parts', index, -1) or ('movie', file, frame time).
    ``rect`` is the sub-rectangle (x0, y0, x1, y1) in texels (None = whole image).
    ``pos`` / ``canvas`` / ``scale`` / ``flags`` follow the engine call; ``alpha`` is the vertex
    alpha (SetColor), ``rgb`` the vertex colour; ``size`` the texel size of the image (needed for
    the anchor and the pixel rectangle; filled by the caller when known)."""
    kind: str
    slot: int
    source: tuple[str, object, int]
    canvas: tuple[float, float]
    pos: tuple[float, float]
    flags: int = 0x10
    scale: tuple[float, float] = (1.0, 1.0)
    rect: Optional[tuple[int, int, int, int]] = None
    size: Optional[tuple[int, int]] = None
    alpha: float = 1.0
    rgb: tuple[float, float, float] = (1.0, 1.0, 1.0)
    blend: int = BLEND_NORMAL
    rotation: float = 0.0          # radians (flag 0x20)
    va: int = 0                    # call site (evidence)
    note: str = ""

    def texel_size(self) -> tuple[int, int]:
        if self.rect is not None:
            return self.rect[2] - self.rect[0], self.rect[3] - self.rect[1]
        if self.size is None:
            raise ValueError(f"draw at {self.va:#x}: image size unknown")
        return self.size

    def pixel_rect(self) -> tuple[float, float, float, float]:
        """(x, y, w, h) on the 1920x1080 frame (before rotation)."""
        cw, ch = self.canvas
        kx, ky = 1920.0 / cw, 1080.0 / ch
        if self.kind == "fill":
            assert self.size is not None
            return self.pos[0] * kx, self.pos[1] * ky, self.size[0] * kx, self.size[1] * ky
        w, h = self.texel_size()
        sx, sy = self.scale if self.flags & 0x10 else (1.0, 1.0)
        a = self.flags & 0xF
        fx, fy = (ANCHOR_FX[a], ANCHOR_FY[a]) if a < 9 else (0.0, 0.0)
        # VA 0xDD883..0xDD8A6: anchor offset = trunc(factor * size) texels, times the scale
        ax, ay = math.trunc(fx * w), math.trunc(fy * h)
        x = (self.pos[0] - ax * sx) * kx
        y = (self.pos[1] - ay * sy) * ky
        return x, y, w * sx * kx, h * sy * ky


# ---------------------------------------------------------------------------
# 1. boot sequence
# ---------------------------------------------------------------------------
TASK_GAME_BOOT_TABLE_VA = 0x2D66A4        # 5 states, task_game VA 0xEF11A
BOOT_PROCESS_VA = 0xE8280                 # process 0 update (registered by VA 0xD1F40)
BOOT_STATE_VA = 0x2057CDC                 # u16 state of process 0
BOOT_ALPHA_VA = 0x2057CE0                 # f32 fade
BOOT_WHITE_VA = 0x2057CE4                 # u8 white background flag
BOOT_DAMAGED_VA = 0x2057CD8               # i32 "system data damaged" (VA 0xE8240 / 0xE8250)
BOOT_JUMP_TABLE_VA = 0x2D6060             # 0x35 entries
BOOT_FADE_STEP = 0.025                    # VA 0x2D5FF8 / -0.025 at 0x2D5FF0
BOOT_FADE_STEP_FAST = 0.05                # VA 0x2D6008 / 0x2D6004 (damaged-data notice)
BOOT_FADE_TOP = 1.1                       # VA 0x2D5FFC
BOOT_FADE_BOTTOM = -0.1                   # VA 0x2D5FF4

# process-0 states that some code path writes (every other jump-table entry is either the
# shared "do nothing" target 0xE8DB9 or a case no instruction ever stores) [E, VA 0xE8280..]
BOOT_LIVE_STATES = (0x00, 0x0F, 0x1C, 0x1D, 0x1E, 0x30, 0x31, 0x32, 0x33, 0x34)
BOOT_DEAD_STATES = (0x06, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15,
                    0x16, 0x17, 0x18)


@dataclass(frozen=True)
class Screen:
    """A full-screen phase: its asset(s), timing in 60 Hz frames, skip rule and evidence."""
    key: str
    assets: tuple[str, ...]
    fade_in_frames: int
    hold_min: int
    hold_max: Optional[int]           # None = waits for input / an event
    fade_out_frames: int
    skip: str
    states: str
    va: str
    live: bool = True
    note: str = ""

    @property
    def max_frames(self) -> Optional[int]:
        if self.hold_max is None:
            return None
        return self.fade_in_frames + self.hold_max + self.fade_out_frames


def fade_frames(start: float, step: float, limit: float, *, up: bool) -> int:
    """Frames a float32 fade needs: per frame ``a += step`` and the state ends on the frame where
    ``a >= limit`` (``up``) or ``a <= limit`` (process 0 uses ``vucomiss`` + ``jb``)."""
    a = F32(start)
    s = F32(step)
    lim = F32(limit)
    n = 0
    while True:
        a = F32(a + s)
        n += 1
        if (up and a >= lim) or (not up and lim >= a):
            return n
        if n > 100000:
            raise ValueError("fade does not terminate")


def title_fade_frames(step_per_frame: float, start: float, limit: float, *, up: bool,
                      strict: bool = True) -> int:
    """Title covers: ``a += dt * (+-1/60)`` with dt = 1 frame; the state ends when ``a > limit``
    (``up``, ``jbe`` on ``vucomiss a, limit``) or ``0 > a`` / ``a <= 0`` (down)."""
    a = F32(start)
    s = F32(step_per_frame)
    lim = F32(limit)
    n = 0
    while True:
        a = F32(a + s)
        n += 1
        if up and (a > lim if strict else a >= lim):
            return n
        if not up and (lim > a if strict else lim >= a):
            return n
        if n > 100000:
            raise ValueError("fade does not terminate")


_IN = fade_frames(0.0, BOOT_FADE_STEP, BOOT_FADE_TOP, up=True)          # 45 (float32)
_OUT = fade_frames(1.0, -BOOT_FADE_STEP, BOOT_FADE_BOTTOM, up=False)    # 45
_IN2 = fade_frames(0.0, BOOT_FADE_STEP_FAST, BOOT_FADE_TOP, up=True)    # 22
_OUT2 = fade_frames(1.0, -BOOT_FADE_STEP_FAST, BOOT_FADE_BOTTOM, up=False)
_ZERO_OUT = fade_frames(0.0, -BOOT_FADE_STEP, BOOT_FADE_BOTTOM, up=False)  # 4 (state 0x0F from 0)

SKIP_BOOT = "game input bits 0x860 (word +0x20 of *0xDC7BC0) or pointer click (0x178EC20+0x34 bit 1)"
SKIP_TITLE = "any button (word +0x20 of *0xDC7BC0 & 0xFFF) or pointer click (0x178EC20+0x34 bit 1)"

# process 0 (VA 0xE8280): the screens in the order a live boot can show them
BOOT_SCREENS: tuple[Screen, ...] = (
    Screen("damaged_notice", ("parts.mrg #2+system_lang (save data damaged, ja/en/zc/zt/ko)",),
           _IN2, 0, 480, _OUT2, SKIP_BOOT, "0x00 -> 0x31 -> 0x32 -> 0x33 -> 0x0F",
           "0xE831C..0xE835F, 0xE8BD0..0xE8CE3", True,
           "only when task_game set the damaged flag (VA 0xEF148: 0xE8240(1)); language from "
           "the system setting (VA 0x407F0), not the game option"),
    Screen("autosave_notice", ("parts.mrg #8+lang (autosave notice)", "parts.mrg #13+lang ('Saving...')",
                               "parts.mrg #7 (spinner)", "parts.mrg #18 (glow)"),
           _IN, 0, 240, _OUT, SKIP_BOOT, "0x0F -> 0x1C -> 0x1D -> 0x1E -> 0x34",
           "0xE85E1..0xE86F7, 0xE8A5B..0xE8BA5; draw 0xE8EDE..0xE903E", True,
           "shown on every boot; state 0x0F first spends a few frames fading the (empty) previous "
           "screen out"),
    Screen("typemoon_logo", ("parts.mrg #1 on white",), _IN, 0, 180, _OUT, SKIP_BOOT,
           "0x06 -> 0x0A -> 0x0B -> 0x0C", "0xE8364..0xE8471", False,
           "dead: no instruction stores 6 into the state word"),
    Screen("parts_caution", ("parts.mrg #19+lang (fiction disclaimer)",), _IN, 0, 140, _OUT, SKIP_BOOT,
           "0x0C -> 0x0D -> 0x0E -> 0x0F", "0xE8476..0xE85DC", False, "dead (only reachable from 0x0A)"),
    Screen("system_voice_greeting", ("system voice 0xA5DC0(0x47|1 / 0x49|0x48, config+0x56, 4)",),
           _IN, 0, 140, _OUT, SKIP_BOOT, "0x10 .. 0x18", "0xE86FC..0xE8A56", False,
           "dead: no store of 0x10/0x13; would play a title call voice chosen by G[1] & 0xF"),
)


@dataclass(frozen=True)
class BootStep:
    key: str
    detail: str
    frames_min: Optional[int]
    frames_max: Optional[int]
    va: str


def task_game_boot_states() -> list[tuple[int, str, str]]:
    """The five task_game boot states (jump table VA 0x2D66A4, loop VA 0xEF0CF..0xEF17C) [E]."""
    return [
        (0, "0xEF12A", "SaveSystem_CreateInitial (VA 0xEA390: nexl_SaveData save of 'savedata'/'bin', "
                       "4 MiB image) -> 0: leave the loop; else state 1"),
        (1, "0xEF159", "poll 0xEA490: <0 stay; ==5 -> 0xEAF90(5), state 3; else state 2"),
        (2, "0xEF189", "SaveImage_Ptr (0xC2F60), leave the loop"),
        (3, "0xEF13A", "0xEAFB0 (= 0x120760 on the save object) != 0 stay; else 0xE8240(1) "
                       "(damaged flag 0x2057CD8 = 1), state 4"),
        (4, "0xEF0DA", "run Game_FrameUpdate each frame until process 0 clears the damaged flag"),
    ]


def boot_sequence(*, damaged: bool = False, bit485: bool = True, g10: bool = True,
                  pending_start: int = -1, intro_seen_this_session: bool = False) -> list[BootStep]:
    """Ordered screens from power-on to the first interactive state [E; durations V by float
    emulation of the fade constants].

    ``damaged``: the system save failed its check (task_game state 3).  ``bit485``: persistent
    'scene reached' bit 485 (set by the first lines of 00_01 / 00_02, i.e. the prologue has been
    seen).  ``g10``: G[10] != 0 (set by `_WKST(G010,eq,1)` at the start of 00_00, the first story
    script).  ``pending_start``: VA 0x2A1B0 result (>= 0 = a start scene is pending)."""
    steps: list[BootStep] = [
        BootStep("main", "SystemService_Init, sysmodules, VideoOut, scheduler (engine.md 3.1)", None, None,
                 "0xED210"),
        BootStep("task_game", "display surfaces, captures, mouse, SaveImage_Clear, "
                              "sceSystemServiceHideSplashScreen", None, None, "0xEEE50..0xEF0CA"),
        BootStep("system_save", "task_game boot states 0..4 (system save mount / check)", None, None,
                 "0xEF0CF..0xEF17E"),
        BootStep("open_archives", "Game_Init registers process 0 = Game_OpenArchives (one shot): "
                                  "archive slots, parts.mrg in slot 0x18, fonts; then process 0 = 0xE8280",
                 1, 1, "0xE90E0, 0xD1F40, 0xD22ED"),
    ]
    d, a = BOOT_SCREENS[0], BOOT_SCREENS[1]
    if damaged:
        # the damaged path never copies the system block (G[], config) out of the save image
        # (VA 0xE8D58 runs only when the flag is clear), so G[10] and bit 485 read as 0 [H: G
        # starts zeroed]
        bit485, g10 = False, False
        # fade in, hold 1..480 frames (input is tested from the first hold frame), fade out
        steps.append(BootStep(d.key, d.assets[0], d.fade_in_frames + 1 + d.fade_out_frames,
                              d.max_frames, d.va))
    else:
        steps.append(BootStep("load_system_data", "copy system block from the save image (state 0 -> 0x30)",
                              2, 2, "0xE8D58..0xE8DB4, 0xE8BAA"))
    steps.append(BootStep(a.key, "; ".join(a.assets), _ZERO_OUT + a.fade_in_frames + 1 + a.fade_out_frames,
                          _ZERO_OUT + (a.max_frames or 0), a.va))
    steps.append(BootStep("start_processes", "state 0x34: Game_StartProcesses, close parts.mrg slot 0x18, "
                                             "process 0 = 0xD23B0 (system state machine 0x68A70)",
                          1, 1, "0xE8CE8 -> 0xD2330"))
    steps += title_entry_steps(bit485=bit485, g10=g10, pending_start=pending_start,
                               intro_seen_this_session=intro_seen_this_session)
    return steps


# ---------------------------------------------------------------------------
# 2. title
# ---------------------------------------------------------------------------
TITLE_STATE_VA = 0x17621FC                 # i32 title state (jump table VA 0x21893C, 0x3B entries)
TITLE_JUMP_TABLE_VA = 0x21893C
TITLE_T_VA = 0x218A40                      # 6 x i32 {1,2,3,3,2,4}
TITLE_T = (1, 2, 3, 3, 2, 4)
TITLE_RULE_TABLES = {0x218868: 0xAFC30, 0x218880: 0xAFCE0, 0x2188D0: 0xAFE80, 0x218924: 0xB0480}
TITLE_RECORD0_VA = 0x873670               # child of title table record 0 (table 0x873660)
TITLE_MOVIE_INDEX_VA = 0x873650           # T - 1 (0..3) or -1 -> movie table id (titlebg1..4)
G_BASE_VA = 0x164C620                     # var_G (u16 words)
SCENE_BIT_BASE = 0xDC                      # VA 0xB81A0: bit n of the u16 words at var_G + 0xDC
TITLE_LOOP_TIME = 8.033333778381348        # VA 0x2186E4: titlebgN.mp4 loop point (s)
TITLE_MOVIE_IDS = (0, 1, 2, 3)             # movie table 0xDC8C70: titlebg1..4.mp4
TM_CI_MOVIE_ID = 38                        # TM_CI.mp4 (VA 0xB104A, loop 0)

# per-state rule (jump tables above, identical in all four functions): state -> (bit, next)
TITLE_RULES: dict[int, Optional[tuple[int, int]]] = {
    0: (0x230, 1),      # bit 560: _RDST(560) in 14_00_ARC14_1
    1: (0x17F, 3),      # bit 383: _RDST(383) in 05_01_CIEL05_3
    2: None,            # unconditional reset to 0 (state never produced)
    3: (0x232, 5),      # bit 562: _RDST(562) in 15_01B_CIEL15B_1
    4: None,            # unconditional reset to 0 (state never produced)
    5: (-1, 5),         # final
}
TITLE_BITS = {0x230: "14_00_ARC14_1", 0x17F: "05_01_CIEL05_3", 0x232: "15_01B_CIEL15B_1",
              0x1E5: "00_01 / 00_02 (prologue)", 0x231: "15_01A_CIEL15A_1"}


def scene_bit(words: Sequence[int], n: int) -> bool:
    """VA 0xB81A0: bit n (< 0x240) of the persistent 'scene reached' set (var_G + 0xDC)."""
    if not 0 <= n < 0x240:
        return False
    return bool((words[n >> 4] >> (n & 15)) & 1)


def advance_title_state(g2: int, bits: Iterable[int]) -> int:
    """One evaluation of the title-state rule (VA 0xAFCF4..0xAFD5A, run on every title entry
    and UI reload while G[10] != 0).  At most one step per call."""
    have = set(bits)
    if not 0 <= g2 <= 5:
        return 0
    rule = TITLE_RULES[g2]
    if rule is None:
        return 0
    bit, nxt = rule
    if bit < 0:
        return g2
    return nxt if bit in have else g2


@dataclass(frozen=True)
class TitleBackground:
    g2: int
    record0_child: int        # TITLE_PARTS child written to data VA 0x873670
    movie_index: int          # data VA 0x873650: -1 = no movie, else movie table id
    bgm: int                  # VA 0xB0670: BGM track (2, or 72 in state 5)


def title_background(g10: int, g2: int) -> TitleBackground:
    """VA 0xAFCE0 / 0xAFE80 / 0xB0480 / 0xAFC30: record-0 child and movie id [E, V frames]."""
    if g10 == 0:
        return TitleBackground(g2, 9, -1, 2 if g2 != 5 else 0x48)
    t = TITLE_T[g2] if 0 <= g2 < 6 else 0
    m = t - 1
    return TitleBackground(g2, 9 + t, m if 0 <= m < 4 else -1, 0x48 if g2 == 5 else 2)


def title_progression(bits: Iterable[int], visits: int = 4, g2: int = 0) -> list[int]:
    """G[2] after each title entry (a convenience over :func:`advance_title_state`)."""
    out = []
    b = list(bits)
    for _ in range(visits):
        g2 = advance_title_state(g2, b)
        out.append(g2)
    return out


# --- title return value -> system state machine (jump table VA 0x209DC0) ---------------------
TITLE_RETURN_TABLE_VA = 0x209DC0          # 0x22 x i32 offsets, index ret - 1 (dispatch VA 0x68DAF)
TITLE_RETURN_DEFAULT = 0x69182            # shared case: ret 10..31 -> scene 8990 + ret, else no-op
# every entry of the table (ret 1..34); re-read by read_tables / test_title_return_table
TITLE_RETURN_TARGETS: tuple[int, ...] = (
    (0x68DCB, 0x690EE, 0x69154, 0x69182, 0x691B9, 0x691C0, 0x691C7, 0x69182, 0x691F8)
    + (0x69182,) * 23 + (0x69229, 0x69280))
# values the title update VA 0xB06B0 can return: ebx writes 0, 1 (case 0x16 only), 2, 3 and
# state - 0x20 for the shared case 0x25..0x28 (only 0x27 is ever stored -> 7)
TITLE_RETURNS_REACHABLE = (0, 2, 3, 7)


@dataclass(frozen=True)
class TitleReturn:
    ret: int
    handler: int                 # case VA in the system state machine 0x68A70
    mode: Optional[int]          # new mode word 0xE20CC8 (None = unchanged: stay on the title)
    start_scene: Optional[int]   # dword 0xE20CF8 for mode 2 (None = not set here)
    reachable: bool              # can VA 0xB06B0 return this value in 01.01?
    note: str


def title_return(ret: int) -> TitleReturn:
    """What the system state machine does with title return value ``ret`` (VA 0x68DAF..0x694AA)
    [E].  Scene ids 1800, 190 and 9000..9021 are not in the scene table (script.md 6.1), so the
    loader would fall back to START_SCRIPT; none of them is reachable from the title."""
    reach = ret in TITLE_RETURNS_REACHABLE
    if not 1 <= ret <= 0x22:
        return TitleReturn(ret, TITLE_RETURN_DEFAULT, None, None, ret == 0,
                           "out of range -> shared case -> no change (stay on the title)")
    h = TITLE_RETURN_TARGETS[ret - 1]
    if ret == 1:
        return TitleReturn(ret, h, 1, None, False, "mode 1 (attract); only dead title state 0x16 returns 1")
    if ret == 2:
        return TitleReturn(ret, h, 2, None, True, "New Game: scene = 0x2A1B0() if >= 0 else "
                                                  "(bit 485 ? 110 : 260) (VA 0x6913D, 0x6946A)")
    if ret == 3:
        return TitleReturn(ret, h, 2, None, True, "resume the loaded save (byte 0xE20CF4 = 1)")
    if ret in (5, 6, 7):
        screen = {5: 0x656D0, 6: 0x66380, 7: 0x67410}[ret]
        return TitleReturn(ret, h, 4, None, reach, f"extras screen {screen:#x} (7 = GALLERY)")
    if ret == 9:
        return TitleReturn(ret, h, 2, 0x708, False, "mode 2, scene 1800 (not a scene id)")
    if 10 <= ret <= 31:
        return TitleReturn(ret, h, 2, 8990 + ret, False, f"mode 2, scene {8990 + ret} (VA 0x6918E: ret + 0x231E;"
                                                         " not a scene id)")
    if ret == 33:
        return TitleReturn(ret, h, 2, 0xBE, False, "mode 2, scene 190 (not a scene id), word 0x1653AE0 = 0")
    if ret == 34:
        return TitleReturn(ret, h, 6, None, False, "mode 6: the abmp_logo object (VA 0x692A8)")
    return TitleReturn(ret, h, None, None, False, "shared case 0x69182 with ret - 10 > 0x15 -> no change")


@dataclass(frozen=True)
class MenuItem:
    index: int                # cursor value 0..6 (data VA 0x873904)
    name: str
    widget: int               # UI widget id 0xE4 + index (definition VA 0x873780)
    sheet_row: int            # row of TITLE_PARTS child 0 (frames at VA 0x873C00..)
    action: str
    va: str


TITLE_ITEMS: tuple[MenuItem, ...] = (
    MenuItem(0, "CONTINUE", 0xE4, 4, "confirm 'Loading...' (ui_text 184) -> load last save -> return 3",
             "0xB13F2"),
    MenuItem(1, "NEW GAME", 0xE5, 0, "SE 5, BGM fade 1.5 s, state 0x0B -> return 2 (New Game, VA 0x690EE)",
             "0xB1647"),
    MenuItem(2, "LOAD GAME", 0xE6, 1, "save/load screen (VA 0x6F450 / 0x71C90), state 0x17", "0xB1791"),
    MenuItem(3, "CONFIG", 0xE7, 2, "config screen (VA 0x3F110 / 0x40860), state 0x18", "0xB17D2"),
    MenuItem(4, "(hidden CONFIG copy)", 0xE8, 2, "buzzer SE 1 only; never visible (row -1)", "0xB1813"),
    MenuItem(5, "GALLERY", 0xE9, 3, "if PlayGo complete (0xD1CD0(1)): state 0x27 -> return 7 "
                                    "(mode 4 gallery, VA 0x67410); else ui_text 242", "0xB1824"),
    MenuItem(6, "(hidden GALLERY copy)", 0xEA, 3, "no action (decide ignored)", "-"),
)
# widget frame records (VA 0x873C00 + 0x30*k): texture slot 126 = TITLE_PARTS child 0,
# rect (x0,y0,x1,y1) = column c, sheet row r: (24 + 312c, 24 + 88r, 320 + 312c, 96 + 88r)
TITLE_WIDGET_FRAME_COLUMNS = (0, 1, 0, 1, 2)   # widget states 0..4 -> sheet column (VA 0x8739D0)


def title_widget_rect(row: int, column: int) -> tuple[int, int, int, int]:
    """Sub-rectangle of TITLE_PARTS child 0 for sheet ``row`` / ``column`` (data VA 0x873C00)."""
    x0 = 24 + 312 * column - (1 if row == 1 else 0)
    y0 = 24 + 88 * row
    return (x0, y0, x0 + 296, y0 + 72)


def title_rows(continue_available: bool) -> dict[int, int]:
    """Item -> row (data VA 0x1762180, written by VA 0xAFDA0 / 0xAFFD6..0xB00F1); -1 = hidden."""
    c = 1 if continue_available else 0
    rows = {0: 0 if c else -1, 1: c, 2: c + 1, 3: c + 2, 4: -1, 5: c + 3, 6: -1}
    return rows


def title_row_count(continue_available: bool) -> int:
    return 5 if continue_available else 4


# position tables (canvas 640x448 for widgets, 960x540 for the highlight bar)
WIDGET_POS = ((0.0, 0.0),) * 6 + ((272.0, 312.3555603), (272.0, 335.5851746), (272.0, 358.8148193),
                                  (272.0, 382.0444336), (272.0, 298.6666565), (272.0, 321.8962708),
                                  (272.0, 345.1259155), (272.0, 368.3555603), (272.0, 391.5851746))
HL_INDEX = (0, 1, 2, 3, -1, 4, -1)                       # VA 0x218B80
HL_POS = ((399.0, 1168.0, -250.0, 383.5), (399.0, 1168.0, 400.0, 411.5), (399.0, 1168.0, 428.0, 439.5),
          (399.0, 1168.0, 455.5, 467.0), (399.0, 1168.0, 484.0, 495.5))   # VA 0x218BA0, same per lang
LOGO_RECTS = {0: ((0, 0, 560, 285), (0, 285, 560, 570), (0, 570, 560, 648)),
              1: ((0, 0, 1, 1), (0, 1, 1850, 330), (0, 330, 1850, 440))}   # VA 0x873910 (+24/lang)
LOGO_POS = {0: (340.0, 36.26666641), 1: (17.5, 111.8222198)}               # VA 0x218B20 (+8/lang)
GLOW_POS = {0: (360.0, 278.0444336), 1: (360.0, 196.9481506)}              # VA 0x218B50
BG_REDRAW_Y = {0: 680, 1: 519}                                            # VA 0x218DE0 (u16/lang)
PRESS_POS = (362.0, 410.0148010)                                          # VA 0x21873C
COPYRIGHT_POS = (436.0, 507.7333374)                                      # VA 0x218744
INTRO_GLOW_POS = (-187.5, -55.40740585)                                   # VA 0x2186E8
HL_RECT = (24, 24, 360, 32)                                               # VA 0xB24D0 immediate


def _lang_key(lang: int) -> int:
    return 1 if lang == 1 else 0     # zc / zt / ko use the ja entries in every table above


@dataclass(frozen=True)
class TitleView:
    """Parameters of one frame of the title screen (steady state = all fades complete).

    ``logo_alpha`` / ``logo_fading`` are the values *after* this frame's update
    (:func:`title_logo_step`).  ``logo_factor_prev`` is f32 0x1762210 left by the previous frame;
    it is used for the menu only when the logo is skipped this frame (alpha 0 or
    ``logo_hidden``)."""
    lang: int = 1
    g10: int = 1
    g2: int = 0
    cursor: int = 1
    continue_available: bool = False
    logo_alpha: float = 1.0          # 0x176220C
    logo_fading: bool = False        # this frame took the +1/120 branch (bands 1 / 2 use la^2 / la^3)
    logo_hidden: bool = False        # byte 0x1762208 (set at VA 0xB161D when CONTINUE starts loading)
    logo_factor_prev: float = 1.0    # 0x1762210 from the previous frame
    menu_alpha: float = 1.0          # 0x17621AC
    menu_exit: float = 0.0           # 0x17621F8 (0 after init 0xB01B5; 1 only in the 0xB0480 path)
    highlight_grow: float = 1.0      # 0x1762220 (bar width 0..1, +0.1 per frame)
    glow_phase: float = 1.0          # 0x17621DC (moon glow, bounces 0..1 at 1/120 per frame)
    press_alpha: float = 1.0         # 0x17621F0 (PRESS ANY BUTTON pulse 0.1..1)
    movie_time: Optional[float] = None   # draw the titlebg movie frame at this time instead of the still


def title_logo_factor(v: TitleView) -> float:
    """f32 0x1762210 as the menu sees it (VA 0xB2190..0xB2198): la^3 while fading in, else la;
    the previous frame's value when the logo is skipped (alpha 0 or byte 0x1762208)."""
    la = v.logo_alpha
    if la <= 0.0 or v.logo_hidden:
        return v.logo_factor_prev
    return la ** 3 if v.logo_fading else la


LOGO_STEP_IN = 0.008333333767950535       # VA 0x2186F0 (+1/120 per frame)
LOGO_STEP_OUT = -0.01666666753590107      # VA 0x2186F4 (-1/60 per frame)


def title_logo_step(la: float, cover: float, *, movie_paused: bool, overlay: float) -> tuple[float, bool]:
    """One frame of the logo alpha f32 0x176220C (VA 0xB20BF..0xB2151) [E].

    ``cover`` is the black cover f32 0x17621E8.  ``movie_paused`` is 0xEE9D0() = AvPlayer bytes
    +0x84 (started) and +0x85 (paused): the title movie is paused by VA 0x69E70 while a system
    screen (LOAD / CONFIG / gallery dialogs) fades its overlay f32 0xE2377C in; ``overlay`` is
    0x6C5D0() = that value.  Returns (new alpha, fading_in).  Snaps: 0 in title state 0 (VA
    0xB07EC) and after caution2 (0xB1288); 1 on every menu input (0xB13D0, 0xB163D, 0xB1A81,
    0xB1D26).  Alpha 0 (or byte 0x1762208) skips the logo, the moon glow and the particles."""
    a = F32(la)
    fading = False
    if F32(0.5) > F32(cover):
        if movie_paused or overlay > 0.0:
            a = F32(a + F32(LOGO_STEP_OUT))
        else:
            a = F32(a + F32(LOGO_STEP_IN))
            fading = True
    if a < 0.0:
        a = F32(0.0)
    elif a > 1.0:
        a = F32(1.0)
    return float(a), fading


def title_logo_schedule(frames: int, cover: Sequence[float], *, start: float = 0.0,
                        paused: Sequence[bool] = ()) -> list[float]:
    """Logo alpha per frame for a cover curve (convenience for :func:`title_logo_step`)."""
    out = []
    la = start
    for i in range(frames):
        c = cover[i] if i < len(cover) else cover[-1]
        p = paused[i] if i < len(paused) else False
        la, _ = title_logo_step(la, c, movie_paused=p, overlay=0.0)
        out.append(la)
    return out


@dataclass
class TitleMovieState:
    """Bytes 0x17621B9 (time has passed the loop point once) and 0x17621BA (a seek is due)."""
    passed: bool = False
    seek_due: bool = False


def title_background_source(time_s: Optional[float], st: TitleMovieState) -> tuple[int, bool]:
    """Which slot VA 0xB2013..0xB20BA draws as the background this frame and whether it seeks
    to the loop point.  ``time_s`` = 0xEE960() (movie time), None when no movie frame exists
    (byte 0x17621B8 != 1 or 0xAFB10() false).  Returns (slot, seek_issued); slot 0x87 = the
    AvPlayer texture, 0x7D (125) = the still TITLE_PARTS child 9 + T [E].

    After the movie wraps (time < 8.0333 after having passed it) the still is drawn on every
    frame until the seek to 8.0333 s takes effect; the seek itself is issued once."""
    loop = F32(TITLE_LOOP_TIME)
    if time_s is None:
        return 0x7D, False
    t = F32(time_s)
    if t > loop:
        st.passed = True
        st.seek_due = True
        return 0x87, False
    if not (loop > t) or not st.passed:
        return 0x87, False
    seek = False
    if st.seek_due:
        seek = True
        st.seek_due = False
    return 0x7D, seek


TITLE_SLOTS = {125: "record 0 (child 9 + T)", 126: 0, 127: 1, 128: 2, 129: 3, 130: 4, 131: 5, 132: 6,
               133: 7, 134: 8, 136: 14}       # table 0x873660; 135 = movie texture, 137 = caution


def title_draw_list(v: TitleView, sizes: Optional[dict[int, tuple[int, int]]] = None) -> list[Draw]:
    """Draw calls of VA 0xB1F30 (title screen, called by the title draw process 0xB02B0 when the
    title state is outside 0x2C..0x3A and 0x17621A8 > 0) [E].  Particles (VA 0xB2F50 / 0xB3310,
    TITLE_PARTS children 5..7, rand()-driven) and the background re-draw over them (VA 0xB2AC5,
    rows >= 0x218DE0[lang]) are not listed."""
    L = _lang_key(v.lang)
    sz = sizes or {}
    tp = f"TITLE_PARTS_{LANG_CODES[v.lang].upper()}.MRG"
    out: list[Draw] = []
    bg = title_background(v.g10, v.g2)
    c544, s544 = (960.0, 544.0), (0.5, 0.5037037)
    if v.g10:
        if v.movie_time is not None and bg.movie_index >= 0:
            out.append(Draw("movie", 135, ("movie", f"titlebg{bg.movie_index + 1}.mp4", int(v.movie_time * 1000)),
                            c544, (0.0, 0.0), 0x10, s544, size=(1920, 1080), va=0xB25F6,
                            note="AvPlayer texture while the movie is before 8.0333 s or past it"))
        else:
            out.append(Draw("tex", 125, ("allpac3", tp, bg.record0_child), c544, (0.0, 0.0), 0x10, s544,
                            size=sz.get(bg.record0_child, (1920, 1080)), va=0xB20BA,
                            note="still = movie frame at the loop point"))
    else:
        out.append(Draw("tex", 125, ("allpac3", tp, 9), c544, INTRO_GLOW_POS, 0x10, s544,
                        size=sz.get(9, (2670, 1266)), va=0xB20BA, note="G[10] == 0: glow layer child 9"))
    # logo (slot 127 = child 1) in three bands; band k > 0 shifted by its own y0 (texels * 544/1080)
    la = v.logo_alpha
    lf = title_logo_factor(v)          # f32 0x1762210: also multiplies the glow, underline and widgets
    if la > 0.0 and not v.logo_hidden:
        # VA 0xB21F6..0xB2333: bands 1 / 2 use la^2 / la^3 only while the logo is fading in
        band_alpha = (la, la ** 2, la ** 3) if v.logo_fading else (la, la, la)
        lx, ly = LOGO_POS[L]
        for k, r in enumerate(LOGO_RECTS[L]):
            dy = 0.0 if k == 0 else float(F32(F32(F32(r[1]) * F32(544.0)) / F32(1080.0)))
            out.append(Draw("tex", 127, ("allpac3", tp, 1), c544, (lx, ly + dy), 0x10, s544, rect=r,
                            alpha=band_alpha[k], va=(0xB223D, 0xB2316, 0xB236D)[k], note=f"logo band {k}"))
        gx, gy = GLOW_POS[L]
        out.append(Draw("tex", 130, ("allpac3", tp, 4), c544, (gx, gy), 0x10, s544, size=sz.get(4, (488, 256)),
                        alpha=v.glow_phase * lf, blend=BLEND_GLOW, va=0xB2403, note="moon glow"))
    if v.g10:
        n = title_row_count(v.continue_available)
        rows = title_rows(v.continue_available)
        r15 = (n - 2) * (n - 3) // 2 + 2 * n - 3
        cur = v.cursor
        show_hl = cur in (1, 2, 3, 5) or (cur == 0 and v.continue_available)
        if show_hl and v.menu_alpha >= 1.0:
            hx, _w, y4, y5 = HL_POS[HL_INDEX[cur]]
            hy = y5 if v.continue_available else y4
            # VA 0xB269D..0xB26C9: grow x (menu alpha - 0x17621F8) x 0x1762210.  (The earlier
            # SetColor with 0x17621EC at 0xB24CB is overridden before any draw.)
            out.append(Draw("tex", 128, ("allpac3", tp, 2), (960.0, 540.0), (hx + 84.0, hy), 0x11,
                            (v.highlight_grow * 0.5, 0.5), rect=HL_RECT,
                            alpha=v.highlight_grow * (v.menu_alpha - v.menu_exit) * lf, va=0xB2734,
                            note="cursor underline, grows from the centre"))
        for item in TITLE_ITEMS:
            row = rows[item.index]
            if row < 0:
                continue
            k = row + 1
            # VA 0xB28F4..0xB2917 (state 0x21 / 0x23 variants are dead): staggered fade x 0x1762210
            a = min(1.0, max(0.0, 2.0 * v.menu_alpha - k / n)) * lf
            col = 1 if item.index == cur else 0
            x, y = WIDGET_POS[r15 + row]
            out.append(Draw("widget", 126, ("allpac3", tp, 0), (640.0, 448.0), (x, y), 0x10,
                            (1.0 / 3.0, 0.4148148), rect=title_widget_rect(item.sheet_row, col),
                            alpha=a, va=0xB27B9, note=f"{item.name} widget {item.widget:#x} state {col}"))
    else:
        out.append(Draw("tex", 136, ("allpac3", tp, 14), c544, PRESS_POS, 0x10, s544, size=sz.get(14, (472, 88)),
                        alpha=max(0.0, min(1.0, v.menu_alpha * v.press_alpha)), va=0xB25D8,
                        note="PRESS ANY BUTTON"))
    out.append(Draw("tex", 129, ("allpac3", tp, 3), c544, COPYRIGHT_POS, 0x10, s544, size=sz.get(3, (184, 64)),
                    va=0xB29A6, note="(C)TYPE-MOON"))
    return out


def title_entry_steps(*, bit485: bool, g10: bool, pending_start: int = -1,
                      intro_seen_this_session: bool = False) -> list[BootStep]:
    """What the title code does on entry (init VA 0xAFE80, state machine VA 0xB06B0) [E]."""
    out: list[BootStep] = []
    if pending_start >= 0 or not bit485:
        out.append(BootStep("title_skipped", "init state 0x0C -> 0x0B: return 2 at once (nothing drawn)",
                            1, 2, "0xB01E9, 0xB0A00, 0xB0982 -> 0xB12CE"))
        out.append(BootStep("new_game", f"New Game: start scene {pending_start if pending_start >= 0 else 260} "
                                        "(00_01: prologue movie 4, then _RTMN back to the title)", None, None,
                            "0x690EE..0x6949B"))
        return out
    out.append(BootStep("title_init", "init: G[2] rule, record-0 child, titlebgN movie (loop), BGM", 1, 1,
                        "0xAFE80"))
    if not g10:
        fi = title_fade_frames(1.0 / 120.0, 0.0, 1.0, up=True, strict=False)
        out.append(BootStep("title_press_any_button", "G[10] == 0: title fades in (state 1, 1/120 per frame), "
                                                      "glow layer child 9, PRESS ANY BUTTON, no menu", fi, None,
                            "0xB07D2 -> 0xB152E, 0xB1549, 0xB0861 -> 0xB15BA"))
        out += _caution_steps(("caution_<lang>.mzp", "caution1_<lang>.mzp"), first=True)
        w = title_fade_frames(1.0 / 60.0, 0.0, 1.2, up=True, strict=True)
        out.append(BootStep("new_game", "state 0x0B: white cover 0 -> 1.2, return 2 -> New Game (scene 110 "
                                        "START_SCRIPT -> 00_00, which sets G[10] = 1)", w, w, "0xB0982..0xB09F4"))
        return out
    if not intro_seen_this_session:
        w = title_fade_frames(1.0 / 60.0, 0.0, 1.0, up=True, strict=True)
        out.append(BootStep("white_in", "state 0x32: white cover 0 -> 1 (1/60 per frame)", w, w, "0xB0FA1"))
        out.append(BootStep("tm_ci_movie", "state 0x33..0x35: TM_CI.mp4 (movie 38, no loop, volume config "
                                           "byte +0xB / 100); white cover fades 1 -> 0 over the first 60 frames; "
                                           "any button / click stops it", None, None, "0xB0FF8..0xB1107"))
        out += _caution_steps(("caution2_<lang>.mzp",), first=False)
    fi = title_fade_frames(1.0 / 120.0, 0.0, 1.0, up=True, strict=False)
    out.append(BootStep("title_fade_in", "state 1: black cover 1 -> 0 over 120 frames, then state 4 -> 5 "
                                         "(menu, cursor = CONTINUE if available else NEW GAME)", fi, fi,
                        "0xB1549, 0xB0882"))
    out.append(BootStep("title_menu", "state 8: menu input (VA 0xB08D8, 0xB131F..)", None, None, "0xB08D8"))
    return out


def _caution_steps(names: Sequence[str], first: bool) -> list[BootStep]:
    """Cautions: black cover 0 -> 1 (> 1.0, 1/60 per frame) hides the previous picture, the
    caution is loaded into slot 0x89, the cover goes 1 -> 0 (< 0) to reveal it, a countdown
    timer (VA 0x17621A0) holds it, then the cover rises again.  First run (states 0x2B..0x31):
    timer 720, input ignored while the old timer value is > 480 -> 241..720 frames.  Returning
    player (caution2, states 0x36..0x3A): timer 480, input accepted at once -> 1..480 frames."""
    out = []
    up = title_fade_frames(1.0 / 60.0, 0.0, 1.0, up=True, strict=True)
    down = title_fade_frames(-1.0 / 60.0, 1.0, 0.0, up=False, strict=True)
    for i, n in enumerate(names):
        hold = (241, 720) if first else (1, 480)
        tab = (0x218ED0, 0x218EE0)[i] if first else 0x218EF0
        lead = up if (first and i == 0) else 0
        out.append(BootStep(f"caution_{i}" if first else "caution2",
                            f"{n} (allpac3, name table VA {tab:#x}; lang > 3 -> *_en); reveal {down} f, "
                            f"hold {hold[0]}..{hold[1]} f, hide {up} f",
                            lead + down + hold[0] + up, lead + down + hold[1] + up,
                            "0xB0D6F..0xB0F97" if first else "0xB1131..0xB1253"))
    return out


# ---------------------------------------------------------------------------
# 3. choice menu (_SELR / _SELD)
# ---------------------------------------------------------------------------
SEL_SLOTS_VA = 0x174FB70          # 0xFA bytes per slot: "$0439NN" + _SELR argument 1
SEL_SLOT_SIZE = 0xFA
SEL_IDS_VA = 0x1750630            # u16[k] = _SELR argument 0 (value stored into the result)
SEL_COUNT_VA = 0x1750646          # u16 number of _SELR since the last menu
SEL_RESULT_VAR_VA = 0x1750648     # u8 'F' / 'G' + u16 0x175064A index (from _SELD arg 0)
SEL_LOCKED_VA = 0x175064C         # u8[k] = (_SELR argument 2 == 1)
SEL_N_VA = 0x1750698              # u16 = _SELD argument 1 (choice point number), -1 if absent
SEL_MODE_VA = 0x17506B0           # 1 = choice menu, 2 / 3 = system yes/no prompts
SEL_PHASE_VA = 0x17506A0          # 0 fade in, 1 text reveal, 2 select, 3 decided
SEL_CURSOR_VA = 0x17506A2         # -1 at start (nothing highlighted)
SEL_TAKEN_VA = 0x164FBC0          # u8[N] |= 1 << choice id when decided (seen-choice bits)
SEL_CANVAS = (640.0, 344.70001220703125)          # VA 0xA6F3C
SEL_X = {0: 87.0, 1: 30.0}                         # VA 0x2168DC / 0x2168D8
SEL_Y_DEFAULT = {0: 49.15166854858398, 1: 34.93424606323242}   # immediates 0x42449B4F / 0x420BBCAB
SEL_Y_GAP = 52.04000091552734                     # VA 0x2168D4 (added when F823 > 0)
SEL_PITCH = 26.02000045776367                     # VA 0x216920
SEL_GLOW_DX, SEL_GLOW_DY = 0.8999999761581421, 0.699999988079071
SEL_GLOW_COLOR = 0x77FF                            # 0xC7FF while decided (phase 3)
SEL_UNDERLINE = (-16.33333396911621, 8.936667442321777)   # VA 0x2168E8 / 0x216918
SEL_FLARE = (-153.3333282470703, -23.61833381652832)      # VA 0x2168EC / 0x21691C
SEL_SY = 0.3191666901111603                               # VA 0x216914 (= 344.7 / 1080)
SELECT_SLOTS = {91: 0, 92: 1, 93: 2, 94: 3, 95: 4, 96: 5}   # table 0x8730F0 (SELECT_PARTS.MRG)


SEL_PREV_CURSOR_VA = 0x17506A4    # u16 cursor before the last change (-1 at setup 0xA5E0F; 0xA6874)
SEL_HIGHLIGHT_VA = 0x17506B4      # f32 0 -> 1, +0.1 per frame (VA 0xA61BB); 0 on a cursor change
SEL_FLASH_VA = 0x17506AC          # f32 1 at the decision (0xA6686), -0.02 per frame; phase 3 ends at <= 0
SEL_PREFIX_LINE = 43960           # "$0439NN": line 43960 + k for k < 9 ("1. ".."9. "), else 43959 ("0. ")
SEL_PREFIX_OVERFLOW = 43959       # VA 0x993E0..0x9940A: 0xABB8 + k, cmovae 0xABB7 when k >= 9
SEL_GLOW_COLOR_DECIDED = 0xC7FF   # VA 0xA7209 / 0xA723A: colour of the glow copies in phase 3
SEL_RING_K = -1.5707963267948966  # f64 VA 0x216A50: ring s = sin(flash x HUD alpha x K) + 1


def choice_prefix_line(k: int) -> int:
    """Script-text line of the number put in front of choice slot ``k`` (VA 0x993E0) [E, V]:
    "1. ".."9. " (ja "１．".."９．") for k = 0..8; the 10th line (k = 9) gets line 43959 = "0. "
    ("０．").  Only debug scripts (JUMP*, TEST_SCRIPT) have 10-line blocks."""
    if k < 0:
        raise ValueError("k must be >= 0")
    return SEL_PREFIX_LINE + k if k < 9 else SEL_PREFIX_OVERFLOW


@dataclass(frozen=True)
class ChoiceView:
    lang: int = 1
    count: int = 3
    cursor: int = -1
    prev_cursor: int = -1       # u16 0x17506A4: the cursor before the last change (its glow fades out)
    phase: int = 2
    alpha: float = 1.0          # a = HUD alpha x (1 - 0x6F430()) x 0x17506A8
    highlight: float = 1.0      # 0x17506B4 (0 -> 1 at +0.1 per frame after the cursor moves)
    flare_pulse: float = 1.0    # 0x17506B8 (bounces 0.333..1 at 1/90 per frame)
    flash: float = 1.0          # 0x17506AC x HUD alpha x (1 - 0x6F430()) (phase 3: ring scale, rotation)
    y0: Optional[float] = None  # first line y (text units); None = default constant
    shadow: bool = False        # config +0x28 != 0 -> text draw flag 0x10 (VA 0xA6F50)


def choice_first_y(lang: int, *, f823: int, icon_y: Optional[float]) -> float:
    """VA 0xA6F41..0xA6FAB: y of the first choice line.  ``icon_y`` is the click-icon y the HUD
    stored at VA 0xDF0890 (VA 0x54CC7: message y + int(y of the revealed text's last line))."""
    y = F32(icon_y) if icon_y is not None else F32(0.0)
    if not y > 0:
        y = F32(SEL_Y_DEFAULT[_lang_key(lang)])
    if f823 > 0:
        y = F32(y + F32(SEL_Y_GAP))
    return float(y)


def choice_line_ys(v: ChoiceView) -> list[float]:
    y = F32(v.y0 if v.y0 is not None else SEL_Y_DEFAULT[_lang_key(v.lang)])
    out = []
    for _ in range(v.count):
        out.append(float(y))
        y = F32(y + F32(SEL_PITCH))
    return out


def choice_glow_alpha(v: ChoiceView, k: int) -> float:
    """Per-copy alpha of the 8 glow copies of line ``k`` (VA 0xA7157..0xA71CD) [E]: base =
    highlight for the cursor line, 1 - highlight for the previous cursor line (k == prev wins),
    g = base x a x 192 / 255, each copy g / 8.  0 for other lines."""
    if k != v.cursor and k != v.prev_cursor:
        return 0.0
    h = F32(v.highlight)
    base = F32(F32(1.0) - h) if k == v.prev_cursor else h
    g = F32(F32(F32(base * F32(v.alpha)) * F32(192.0)) / F32(255.0))
    return float(F32(g * F32(0.125))) if g > 0 else 0.0


def choice_ring_transform(flash: float) -> tuple[tuple[float, float], float]:
    """Phase-3 ring (VA 0xA76BF..0xA772C): s = sin(flash x K) + 1 with K = -pi/2 (f64 0x216A50),
    scale ((1.4 s + 0.1) x 640/1920, (1.4 s + 0.1) x 344.7/1080), angle trunc(s x 65535) / 65536
    turn.  ``flash`` = 0x17506AC x HUD alpha x (1 - 0x6F430()) (VA 0xA6EA2, 0xA7690): 1 at
    the decision (s = 0, scale 0.033, angle 0), 0 at the end (s = 1, scale 0.5 x 0.479, one
    turn)."""
    x = F32(flash)
    s = F32(F32(math.sin(F32(float(x) * SEL_RING_K))) + F32(1.0))
    m = F32(F32(s * F32(1.4)) + F32(0.1))
    sx = float(F32(F32(m * F32(640.0)) / F32(1920.0)))
    sy = float(F32(F32(m * F32(SEL_CANVAS[1])) / F32(1080.0)))
    turns = math.trunc(float(F32(s * F32(65535.0))))
    return (sx, sy), turns * 2.0 * math.pi / 65536.0


def choice_draw_list(v: ChoiceView, sizes: Optional[dict[int, tuple[int, int]]] = None) -> list[Draw]:
    """Every draw of VA 0xA6E10 for one frame, in engine order [E].

    ``kind == 'text'`` entries are text blocks drawn by VA 0xC8F50: ``slot`` = block id,
    ``source = ('text', line k, colour word)``; ``kind == 'hit'`` are the pointer widgets
    0xD5 + k (no image).  Per line k:

    1. text block 0x14 + k (alpha argument 1.0, flags 0x1020 | shadow) - skipped in phase 3
       unless k is the cursor (VA 0xA70EE..0xA7105);
    2. if k is the cursor or the previous cursor: 8 glow copies, blocks 0x64.. (cursor) or
       0x6C.. (previous cursor), alpha :func:`choice_glow_alpha` (skipped when 0);
    3. if k is the cursor: underline (slot 93) and flare (slot 95), whatever the highlight;
    4. the hit widget.

    Phase 3 then adds the ring (94), the blue bands (91) and the lens flare (92)."""
    sz = sizes or {}
    sp = "SELECT_PARTS.MRG"
    out: list[Draw] = []
    out.append(Draw("tex", 96, ("allpac3", sp, 5), (640.0, 448.0), (0.0, 0.0), 0x10,
                    (1.0 / 3.0, 0.4148148), size=sz.get(5, (1929, 1083)), alpha=v.alpha, va=0xA6F1F,
                    note="dimming vignette over the scene and the page text"))
    x0 = SEL_X[_lang_key(v.lang)]
    ys = choice_line_ys(v)
    col = SEL_GLOW_COLOR_DECIDED if v.phase == 3 else SEL_GLOW_COLOR
    for k in range(v.count):
        y = ys[k]
        if k == v.cursor or v.phase != 3:
            out.append(Draw("text", 0x14 + k, ("text", k, 0xFFFF), SEL_CANVAS, (x0, y),
                            0x1020 | (0x10 if v.shadow else 0), alpha=1.0, va=0xA7128, note=f"line {k}"))
        ga = choice_glow_alpha(v, k)
        if ga > 0:
            base = 0x6C if k == v.prev_cursor else 0x64
            who = "previous cursor" if k == v.prev_cursor else "cursor"
            for j, (dx, dy) in enumerate(choice_glow_offsets()):
                out.append(Draw("text", base + j, ("text", k, col), SEL_CANVAS,
                                (float(F32(F32(x0) + F32(dx))), float(F32(F32(y) + F32(dy)))), 0, alpha=ga,
                                va=0xA7299 if j == 0 else 0xA758D, note=f"line {k} glow {j} ({who})"))
        if k == v.cursor:
            sx = float(F32(F32(F32(F32(0.9) * F32(v.highlight)) + F32(0.1)) * F32(640.0)) / F32(1920.0))
            out.append(Draw("tex", 93, ("allpac3", sp, 2), SEL_CANVAS,
                            (x0 + SEL_UNDERLINE[0], y + SEL_UNDERLINE[1]), 0x10, (sx, SEL_SY),
                            size=sz.get(2, (1455, 48)), alpha=v.alpha, blend=BLEND_PS_ADD, va=0xA7618,
                            note="underline, width animated by the highlight fade"))
            out.append(Draw("tex", 95, ("allpac3", sp, 4), SEL_CANVAS,
                            (x0 + SEL_FLARE[0], y + SEL_FLARE[1]), 0x10, (1.0 / 3.0, SEL_SY),
                            size=sz.get(4, (930, 243)), alpha=v.alpha * v.flare_pulse, blend=BLEND_PS_ADD,
                            va=0xA7678, note="flare behind the selected line"))
        out.append(Draw("hit", 0xD5 + k, ("widget", 0xD5 + k, -1), (640.0, 448.0),
                        (x0, float(F32(F32(F32(y) * F32(448.0)) / F32(SEL_CANVAS[1])))), 0, size=(466, 29),
                        alpha=v.alpha, va=0xA7086, note=f"pointer widget for line {k} (no frames)"))
    if v.phase == 3 and 0 <= v.cursor < v.count:
        y_first = ys[0]
        rsc, ang = choice_ring_transform(v.flash)
        out.append(Draw("tex", 94, ("allpac3", sp, 3), SEL_CANVAS,
                        (86.0, y_first + 18.830833435058594 + v.cursor * SEL_PITCH), 0x34, rsc,
                        size=sz.get(3, (982, 985)), alpha=v.alpha, blend=BLEND_PS_ADD, rotation=ang, va=0xA776C,
                        note="ring, grows and turns once while the flash runs down"))
        out.append(Draw("tex", 91, ("allpac3", sp, 0), SEL_CANVAS, (0.0, 0.0), 0x10,
                        (1.0 / 3.0, SEL_SY), size=sz.get(0, (1920, 1080)), alpha=v.alpha, blend=BLEND_PS_ADD,
                        va=0xA77BE, note="blue bands"))
        out.append(Draw("tex", 92, ("allpac3", sp, 1), SEL_CANVAS,
                        (5.0, y_first - 290.7608642578125 + v.cursor * SEL_PITCH), 0x10, (1.0 / 3.0, SEL_SY),
                        size=sz.get(1, (1920, 1920)), alpha=v.alpha, blend=BLEND_PS_ADD, va=0xA782D,
                        note="lens flare on the chosen line"))
    return out


def choice_decision_frames() -> dict[str, int]:
    """Float32 frame counts of the decided phase (VA 0xA648D..0xA64DC) [F]: alpha 0x17506A8
    falls 0.028 per frame to 0, the flash 0x17506AC falls 0.02 per frame and the phase (and the
    chosen line, drawn at alpha 1 throughout) ends on the frame where the flash is <= 0."""
    def run(step: float) -> int:
        a, n = F32(1.0), 0
        while True:
            a = F32(a + F32(step))
            n += 1
            if not F32(0.0) < a:
                return n
    return {"alpha_zero": run(-0.028), "phase_end": run(-0.02)}


def choice_decision_state(frame: int) -> tuple[float, float]:
    """(alpha 0x17506A8, flash 0x17506AC) ``frame`` frames after the decision (both 1.0 at the
    decision, VA 0xA6686; float32 like VA 0xA6494..0xA64CE, alpha clamped at 0)."""
    a, f = F32(1.0), F32(1.0)
    for _ in range(frame):
        a = F32(a + F32(-0.028))
        f = F32(f + F32(-0.02))
        if not F32(0.0) < a:
            a = F32(0.0)
    return float(a), float(f)


def choice_glow_offsets() -> list[tuple[float, float]]:
    """The 8 offset copies of the highlighted line (blocks 0x64..0x6B, VA 0xA7270..0xA758D)."""
    dx, dy = SEL_GLOW_DX, SEL_GLOW_DY
    return [(-dx, 0.0), (dx, 0.0), (0.0, -dy), (0.0, dy), (-dx, -dy), (dx, -dy), (-dx, dy), (dx, dy)]


def choice_width(lang: int) -> int:
    """Text_Layout width argument (VA 0x9B6F2 / 0xA71DC): 574 in English, else 474."""
    return 0x23E if lang == 1 else 0x1DA


# ---------------------------------------------------------------------------
# eboot tables
# ---------------------------------------------------------------------------
@dataclass
class Tables:
    title_t: tuple[int, ...] = ()
    title_rules: dict[int, list[tuple[int, Optional[int], Optional[int]]]] = field(default_factory=dict)
    logo_rects: dict[int, tuple[tuple[int, int, int, int], ...]] = field(default_factory=dict)
    logo_pos: dict[int, tuple[float, float]] = field(default_factory=dict)
    glow_pos: dict[int, tuple[float, float]] = field(default_factory=dict)
    widget_pos: tuple[tuple[float, float], ...] = ()
    hl_index: tuple[int, ...] = ()
    hl_pos: dict[int, tuple[tuple[float, ...], ...]] = field(default_factory=dict)
    widgets: list[tuple[int, list[tuple[int, int, tuple[int, int, int, int]]]]] = field(default_factory=list)
    boot_cases: dict[int, int] = field(default_factory=dict)
    boot_states_written: set[int] = field(default_factory=set)
    title_states_written: set[int] = field(default_factory=set)
    bg_redraw_y: tuple[int, ...] = ()
    floats: dict[int, float] = field(default_factory=dict)
    title_return_targets: tuple[int, ...] = ()
    sel_ring_k: float = 0.0
    sel_prefix: tuple[int, int, int] = (0, 0, 0)     # (base line, overflow line, threshold k)


def _f(eb: "Eboot", va: int) -> float:
    return struct.unpack("<f", eb.read(va, 4))[0]


def _rule_from_case(eb: "Eboot", case_va: int) -> tuple[int, Optional[int], Optional[int]]:
    """Decode one title-rule case: ``mov edi, bit; call 0xB81A0; mov cx, next`` or a bare store."""
    code = eb.read(case_va, 24)
    if code[0] == 0xBF and code[5] == 0xE8:                  # mov edi, imm32; call rel32
        bit = struct.unpack_from("<I", code, 1)[0]
        tgt = case_va + 10 + struct.unpack_from("<i", code, 6)[0]
        if tgt != 0xB81A0 or code[10:12] != b"\x66\xb9":
            raise ValueError(f"unexpected title rule at {case_va:#x}")
        return (case_va, bit, struct.unpack_from("<H", code, 12)[0])
    return (case_va, None, None)


def _rip_stores(eb: "Eboot", lo: int, hi: int, target: int, width: int) -> set[int]:
    """Immediate values stored by ``mov dword/word ptr [rip+d], imm`` to ``target`` in [lo, hi)."""
    code = eb.read(lo, hi - lo)
    out: set[int] = set()
    op = b"\xc7\x05" if width == 4 else b"\x66\xc7\x05"
    n = len(op)
    i = code.find(op)
    while i >= 0:
        disp = struct.unpack_from("<i", code, i + n)[0]
        ins_len = n + 4 + width
        if lo + i + ins_len + disp == target:
            imm = int.from_bytes(code[i + n + 4:i + n + 4 + width], "little", signed=True)
            out.add(imm)
        i = code.find(op, i + 1)
    return out


def read_tables(eb: "Eboot") -> Tables:
    """Re-read every table this module hard-codes (tests compare both)."""
    t = Tables()
    t.title_t = struct.unpack("<6i", eb.read(TITLE_T_VA, 24))
    for tab, func in TITLE_RULE_TABLES.items():
        cases = [tab + eb.i32(tab + 4 * i) for i in range(6)]
        t.title_rules[tab] = [_rule_from_case(eb, c) for c in cases]
    for lang in range(5):
        raw = struct.unpack("<12h", eb.read(0x873910 + 24 * lang, 24))
        t.logo_rects[lang] = tuple(tuple(raw[4 * k:4 * k + 4]) for k in range(3))  # type: ignore[misc]
        t.logo_pos[lang] = (_f(eb, 0x218B20 + 8 * lang), _f(eb, 0x218B24 + 8 * lang))
        t.glow_pos[lang] = (_f(eb, 0x218B50 + 8 * lang), _f(eb, 0x218B54 + 8 * lang))
        t.hl_pos[lang] = tuple(tuple(_f(eb, 0x218BA0 + 80 * lang + 16 * k + 4 * j) for j in range(4))
                               for k in range(5))
    t.widget_pos = tuple((_f(eb, 0x218DF0 + 8 * i), _f(eb, 0x218DF4 + 8 * i)) for i in range(15))
    t.hl_index = struct.unpack("<7i", eb.read(0x218B80, 28))
    t.bg_redraw_y = struct.unpack("<5H", eb.read(0x218DE0, 10))
    for k in range(7):
        rec = 0x873780 + 0x30 * k
        wid = eb.i32(rec)
        states = eb.u64(rec + 0x20)
        frames = []
        for s in range(5):
            fr = eb.u64(states + 16 * s + 8)
            slot, flags = struct.unpack("<ii", eb.read(fr, 8))
            rect = struct.unpack("<4h", eb.read(fr + 0x14, 8))
            frames.append((slot, flags, rect))
        t.widgets.append((wid, frames))   # type: ignore[arg-type]
    for i in range(0x35):
        t.boot_cases[i] = BOOT_JUMP_TABLE_VA + eb.i32(BOOT_JUMP_TABLE_VA + 4 * i)
    t.boot_states_written = _rip_stores(eb, 0xE8240, 0xE9080, BOOT_STATE_VA, 2)
    t.title_states_written = _rip_stores(eb, 0xAFA30, 0xB1D80, TITLE_STATE_VA, 4)
    for va in (0x2D5FF0, 0x2D5FF4, 0x2D5FF8, 0x2D5FFC, 0x2D6004, 0x2D6008, 0x2186E4, 0x2186E8, 0x2186EC,
               0x21873C, 0x218740, 0x218744, 0x218748, 0x2168CC, 0x2168D0, 0x2168D4, 0x2168D8, 0x2168DC,
               0x216920, 0x2168E4, 0x216904, 0x2168E8, 0x216918, 0x2168EC, 0x21691C, 0x216914,
               0x2186F0, 0x2186F4, 0x216864, 0x216878, 0x21687C, 0x216924, 0x216930, 0x216938):
        t.floats[va] = _f(eb, va)
    t.title_return_targets = tuple(TITLE_RETURN_TABLE_VA + eb.i32(TITLE_RETURN_TABLE_VA + 4 * i)
                                   for i in range(0x22))
    t.sel_ring_k = struct.unpack("<d", eb.read(0x216A50, 8))[0]
    # VA 0x993E0 'mov eax, imm32' / 0x99400 'add edx, imm32' / 0x99406 'cmp r15, imm8'
    code = eb.read(0x993E0, 0x2D)
    if code[0] != 0xB8 or code[0x20:0x22] != b"\x81\xc2" or code[0x26:0x29] != b"\x49\x83\xff":
        raise ValueError("unexpected _SELR prefix code at 0x993E0")
    t.sel_prefix = (struct.unpack_from("<I", code, 0x22)[0], struct.unpack_from("<I", code, 1)[0], code[0x29])
    return t


def ui_lookup_split(game: Path) -> dict[str, int]:
    """Recount the UI registration-table lookups of archives_ui_movies.md 7.2 with the run-time
    rewrites: selecttxt record 1 (VA 0xA5FB2..0xA5FC1) and title record 0 (data VA 0x873670,
    written by VA 0xAFCC2 / 0xAFD72 / 0xAFEEF / 0xAFF02 / 0xB0512 / 0xB04C3 before every load)."""
    from .catalog import (IMAGE_SEARCH_ORDER, ImageNameIndex, localize_parts_name, ui_child_index,
                          unpack_image_id)
    from .catalog_eboot import UI_TABLES_UNREFERENCED, Eboot
    from .mrg import MrgArchive
    from .mzp import MzpArchive
    from .nam import load_nam
    img = Path(game) / "image"
    with Eboot(Path(game) / "eboot.bin") as eb:
        tables = {t.va: t for t in eb.ui_tables()}
    idx = ImageNameIndex({a: load_nam(img / f"{a}.nam") for a in IMAGE_SEARCH_ORDER})
    neutral = {0x781B70, 0x8730F0}
    absent = {0x78B2C0} | set(UI_TABLES_UNREFERENCED)
    out = {"direct": 0, "clamped": 0, "rewritten": 0, "rewritten_title": 0, "rewritten_selecttxt": 0}
    with MrgArchive(img / "allpac3") as a3:
        counts = {e.index: len(MzpArchive(a3.mm, e.offset, e.size)) for e in a3.entries}
        for va, t in tables.items():
            if va in absent:
                continue
            for k, r in enumerate(t.records):
                names = [r.name] if va in neutral else [localize_parts_name(r.name, lang) for lang in range(4)]
                for n in names:
                    pid = idx.find(n)
                    if pid is None:
                        raise ValueError(f"{n} not found")
                    _arc, i = unpack_image_id(pid)
                    if va == 0x873660 and k == 0:
                        out["rewritten"] += 1
                        out["rewritten_title"] += 1
                    elif va == 0x8731A0 and k == 1:
                        out["rewritten"] += 1
                        out["rewritten_selecttxt"] += 1
                    elif 0 <= r.child < counts[i]:
                        out["direct"] += 1
                    elif ui_child_index(r.child, counts[i]) != r.child:
                        out["clamped"] += 1
                    else:
                        raise ValueError(f"unexpected record {va:#x}[{k}]")
    return out


# ---------------------------------------------------------------------------
# rendering
# ---------------------------------------------------------------------------
class AssetCache:
    """Decoded UI images (allpac3 sets, CAUTION screens, parts.mrg), RGBA float32 0..1."""

    def __init__(self, image_dir: Path) -> None:
        self.dir = Path(image_dir)
        self._sets: dict[str, object] = {}
        self._img: dict[tuple[str, object, int], np.ndarray] = {}

    def _allpac3(self):  # type: ignore[no-untyped-def]
        from .mrg import MrgArchive
        if "a3" not in self._sets:
            self._sets["a3"] = MrgArchive(self.dir / "allpac3")
        return self._sets["a3"]

    def image(self, source: tuple[str, object, int]) -> np.ndarray:
        if source in self._img:
            return self._img[source]
        from .image import decode_image
        from .mzp import MzpArchive
        arc_name, key, child = source
        if arc_name == "allpac3":
            a3 = self._allpac3()
            e = a3.find(str(key))
            arc = MzpArchive(a3.read(e.index))
            if child >= 0:
                arc = arc.child(child)
            rgba = decode_image(arc).rgba
        elif arc_name == "parts":
            from .mrg import MrgArchive
            with MrgArchive(self.dir / "parts") as pa:
                rgba = decode_image(MzpArchive(pa.read(int(key)))).rgba   # type: ignore[call-overload]
        else:
            raise ValueError(f"unknown image source {source}")
        arr = rgba.astype(np.float32) / 255.0
        self._img[source] = arr
        return arr

    def size(self, source: tuple[str, object, int]) -> tuple[int, int]:
        a = self.image(source)
        return a.shape[1], a.shape[0]

    def close(self) -> None:
        for v in self._sets.values():
            v.close()  # type: ignore[attr-defined]
        self._sets.clear()


def _resize(img: np.ndarray, w: int, h: int) -> np.ndarray:
    from PIL import Image
    if img.shape[1] == w and img.shape[0] == h:
        return img
    chans = [np.asarray(Image.fromarray(img[..., c]).resize((w, h), Image.Resampling.BILINEAR), np.float32)
             for c in range(img.shape[2])]
    return np.stack(chans, axis=-1)


def composite(frame: np.ndarray, src: np.ndarray, x: float, y: float, w: float, h: float, *,
              alpha: float = 1.0, rgb: tuple[float, float, float] = (1.0, 1.0, 1.0),
              blend: int = BLEND_NORMAL) -> None:
    """Draw RGBA ``src`` (float 0..1) scaled to w x h pixels at (x, y) onto RGB ``frame``.
    Normal = straight-alpha 'over'; ps_add / glow = additive ``d + s * a`` [H for the exact
    shader formulas, see engine.md 4.3]."""
    if alpha <= 0 or w < 0.5 or h < 0.5:
        return
    iw, ih = int(round(w)), int(round(h))
    s = _resize(src, iw, ih)
    X0, Y0 = int(round(x)), int(round(y))
    H, W = frame.shape[:2]
    x0, y0 = max(0, X0), max(0, Y0)
    x1, y1 = min(W, X0 + iw), min(H, Y0 + ih)
    if x1 <= x0 or y1 <= y0:
        return
    sub = s[y0 - Y0:y1 - Y0, x0 - X0:x1 - X0]
    col = sub[..., :3] * np.asarray(rgb, np.float32)
    a = sub[..., 3:4] * alpha
    dst = frame[y0:y1, x0:x1]
    if blend in (BLEND_PS_ADD, BLEND_GLOW):
        frame[y0:y1, x0:x1] = np.clip(dst + col * a, 0.0, 1.0)
    else:
        frame[y0:y1, x0:x1] = dst * (1.0 - a) + col * a


def render_draws(draws: Sequence[Draw], cache: AssetCache, frame: Optional[np.ndarray] = None,
                 movie_frames: Optional[dict[str, np.ndarray]] = None) -> np.ndarray:
    out = np.zeros((1080, 1920, 3), np.float32) if frame is None else frame
    for d in draws:
        if d.kind in ("hit", "text"):
            continue          # pointer widgets draw nothing; text blocks are rendered by tsuki.textbox
        if d.kind == "fill":
            x, y, w, h = d.pixel_rect()
            px = np.ones((1, 1, 4), np.float32)
            composite(out, px, x, y, w, h, alpha=d.alpha, rgb=d.rgb, blend=d.blend)
            continue
        if d.kind == "movie":
            if not movie_frames or str(d.source[1]) not in movie_frames:
                raise ValueError(f"movie frame for {d.source[1]} not supplied")
            src = movie_frames[str(d.source[1])]
            if src.shape[2] == 3:
                src = np.concatenate([src, np.ones(src.shape[:2] + (1,), np.float32)], axis=-1)
            d = replace(d, size=(src.shape[1], src.shape[0]))
        else:
            src = cache.image(d.source)
            if d.size is None or d.rect is None:
                d = replace(d, size=(src.shape[1], src.shape[0]))
        if d.rect is not None:
            x0, y0, x1, y1 = d.rect
            src = src[y0:y1, x0:x1]
        x, y, w, h = d.pixel_rect()
        if d.rotation and d.flags & 0x20:
            src, x, y, w, h = _rotated(src, x, y, w, h, d.rotation, d.flags & 0xF)
        composite(out, src, x, y, w, h, alpha=d.alpha, rgb=d.rgb, blend=d.blend)
    return out


def _rotated(src: np.ndarray, x: float, y: float, w: float, h: float, angle: float,
             anchor: int) -> tuple[np.ndarray, float, float, float, float]:
    """Rotate a centre-anchored sprite (flag 0x20, VA 0xDD6D0) by ``angle`` radians about its
    centre.  Screen direction of positive angles is [H] (the only rotated draws here are a ring
    and a spinner, where it does not show)."""
    from PIL import Image
    if anchor != 4:
        raise NotImplementedError("rotation is modelled for centre-anchored sprites only")
    iw, ih = max(1, int(round(w))), max(1, int(round(h)))
    s = _resize(src, iw, ih)
    im = Image.fromarray((np.clip(s, 0, 1) * 255.0 + 0.5).astype(np.uint8), "RGBA")
    r = im.rotate(math.degrees(angle), resample=Image.Resampling.BILINEAR, expand=True)
    out = np.asarray(r, np.float32) / 255.0
    cx, cy = x + w / 2.0, y + h / 2.0
    return out, cx - r.width / 2.0, cy - r.height / 2.0, float(r.width), float(r.height)


def to_png(frame: np.ndarray, path: Path) -> None:
    from PIL import Image
    Image.fromarray((np.clip(frame, 0, 1) * 255.0 + 0.5).astype(np.uint8), "RGB").save(path)


def movie_frame(game: Path, name: str, t: float) -> np.ndarray:
    """One decoded movie frame (ffmpeg on PATH), RGB float 0..1."""
    import subprocess
    p = Path(game) / "image" / "movie" / name
    r = subprocess.run(["ffmpeg", "-v", "error", "-ss", f"{t:.4f}", "-i", str(p), "-frames:v", "1",
                        "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], capture_output=True, check=True)
    a = np.frombuffer(r.stdout, np.uint8)
    if a.size != 1920 * 1080 * 3:
        raise ValueError(f"{name}@{t}: unexpected frame size {a.size}")
    return a.reshape(1080, 1920, 3).astype(np.float32) / 255.0


def render_title(game: Path, v: TitleView, cache: Optional[AssetCache] = None) -> np.ndarray:
    own = cache is None
    c = cache or AssetCache(Path(game) / "image")
    try:
        draws = title_draw_list(v)
        mf = None
        if v.movie_time is not None:
            bg = title_background(v.g10, v.g2)
            if bg.movie_index >= 0:
                n = f"titlebg{bg.movie_index + 1}.mp4"
                mf = {n: movie_frame(game, n, v.movie_time)}
        return render_draws(draws, c, movie_frames=mf)
    finally:
        if own:
            c.close()


# --- choice rendering ---------------------------------------------------------
@dataclass
class ChoiceScene:
    script: str
    lang: int
    page_messages: list[str]          # $-expanded messages of the page shown with the menu
    page_blank_before: list[int]
    choice_texts: list[str]           # expanded slot texts ("1. ..." etc.)
    choice_ids: list[int]
    locked: list[bool]
    n: int                            # _SELD argument 1
    f823: int
    background: Optional[str]         # picture name of the last full-screen STCH before the menu


def find_choice_scene(script: str, block: int, lang: int, game: Optional[Path] = None) -> ChoiceScene:
    """Collect what is on screen when the ``block``-th choice menu of ``script`` opens: the
    messages since the last page reset (`_WKST(F823,eq,0)`), the `_SELR` slots and `_SELD`."""
    from .script import AllScr
    from .text import ScriptText, choice_slot_text
    scr = AllScr(Path(game) / "image" / "allscr.mrg") if game else AllScr()
    st = ScriptText(Path(game) / "image" / "script_text.mrg") if game else ScriptText()
    try:
        cmds = scr.commands(scr.index(script))
    finally:
        scr.close()
    page: list[str] = []
    blanks: list[int] = []
    pending_blank = 0
    f823 = 0
    bg: Optional[str] = None
    sel: list[tuple[int, str, bool]] = []
    seen = 0
    for c in cmds:
        k = c.name
        raw = c.raw
        if k == "WKST" and raw.startswith("_WKST(F823,eq,0)"):
            page, blanks, f823 = [], [], 0
        elif k == "WKAD" and raw.startswith("_WKAD(F823,1)"):
            f823 += 1
        elif k == "WKAD" and raw.startswith("_WKAD(C847,"):
            pending_blank += int(raw.split(",")[1].rstrip(");"))
        elif k == "WKST" and raw.startswith("_WKST(C847,eq,"):
            pending_blank = int(raw.split(",")[2].rstrip(");"))
        elif k == "ZM":
            msg = raw[raw.index("(") + 1:raw.rindex(")")]     # _ZMbbbss(<message>)
            page.append(st.expand(msg, lang))
            blanks.append(pending_blank)
        elif k in ("STCH", "SCH2") and len(c.obj_args) > 3:
            name = c.obj_args[1]
            if name.lower().startswith("img") and (c.expr_arg(2), c.expr_arg(3)) == (240, 136):
                bg = name          # full-screen picture at the script-space centre
        elif k == "SELR":
            locked = len(c.obj_args) > 2 and c.obj_args[2].strip() == "1"
            sel.append((int(c.obj_args[0]), c.obj_args[1], locked))
        elif k == "SELD":
            if seen == block:
                n_arg = c.obj_args[1] if len(c.obj_args) > 1 and c.obj_args[1] else "-1"
                texts = [st.expand(choice_slot_text(i, m), lang, src_len=SEL_SLOT_SIZE)
                         for i, (_cid, m, _l) in enumerate(sel)]
                return ChoiceScene(script, lang, page, blanks, texts, [s[0] for s in sel],
                                   [s[2] for s in sel], int(n_arg), f823, bg)
            seen += 1
            sel = []
    raise ValueError(f"{script}: choice block {block} not found")


def choice_census(game: Optional[Path] = None) -> dict[str, object]:
    """Choice commands over all 549 scripts (raw AllScr scan): `_SELR` argument counts and
    locked lines, `_SELD` / `_SELA` / `_SELS` uses, lines per `_SELD` block and the scripts
    whose blocks reach the 10th line (prefix "0.", :func:`choice_prefix_line`)."""
    from collections import Counter
    from .script import AllScr
    scr = AllScr(Path(game) / "image" / "allscr.mrg") if game else AllScr()
    sizes: Counter[int] = Counter()
    ten: Counter[str] = Counter()
    n = Counter({"SELR": 0, "SELR_2args": 0, "SELR_locked": 0, "SELD": 0, "SELA": 0, "SELS": 0})
    try:
        for i, name in enumerate(scr.names):
            lines = 0
            for c in scr.commands(i):
                if c.name == "SELR":
                    n["SELR"] += 1
                    lines += 1
                    if len(c.obj_args) > 2 and c.obj_args[2].strip() == "1":
                        n["SELR_locked"] += 1
                    elif len(c.obj_args) == 2:
                        n["SELR_2args"] += 1
                elif c.name == "SELD":
                    n["SELD"] += 1
                    sizes[lines] += 1
                    if lines >= 10:
                        ten[name] += 1
                    lines = 0
                elif c.name in ("SELA", "SELS"):
                    n[c.name] += 1
    finally:
        scr.close()
    return {"counts": dict(n), "block_sizes": dict(sorted(sizes.items())), "ten_line_blocks": dict(ten)}


def render_choice(game: Path, scene: ChoiceScene, v: ChoiceView, *, cache: Optional[AssetCache] = None,
                  with_page_text: bool = True, with_scene: bool = False) -> tuple[np.ndarray, dict[str, object]]:
    """Render the choice menu over the scene picture and the page text.

    Text: tsuki.textbox (engine-exact layout); page text with the textbox ENGINE_TRANSFORM,
    choice lines on the 640 x 344.7 canvas (py = y * 1080 / 344.7; glyph height kept at 3 px
    per unit [H, see system_flow.md 5.6])."""
    from . import textbox as tb
    from .font import Font
    own = cache is None
    c = cache or AssetCache(Path(game) / "image")
    info: dict[str, object] = {}
    try:
        lang = scene.lang
        font = Font(LANG_CODES[lang])
        frame = np.zeros((1080, 1920, 3), np.float32)
        if with_scene and scene.background:
            # only the last full-screen picture; the real scene is a layer composite (layers gap)
            frame = _scene_background(game, scene.background)
        icon_y: Optional[float] = None
        if with_page_text and scene.page_messages:
            img, blocks, origins = tb.render_page(scene.page_messages, lang, font=font, background=frame,
                                                  blank_before=scene.page_blank_before)
            frame = img
            last = blocks[-1]
            icon_y = float(F32(F32(tb.last_line_offset(last)) + F32(origins[-1][1])))
            info["page_origins"] = origins
        y0 = choice_first_y(lang, f823=scene.f823 if with_page_text else 0, icon_y=icon_y)
        v = replace(v, count=len(scene.choice_texts), y0=y0, lang=lang)
        info["icon_y"] = icon_y
        info["y0"] = y0
        draws = choice_draw_list(v)
        # composite in engine order (VA 0xA6E10): vignette; per line text block, glow copies,
        # underline, flare; phase-3 flash.  Consecutive text draws share one glyph pass.
        tr = tb.CanvasTransform(kx=3.0, ky=1080.0 / SEL_CANVAS[1], kgy=3.0)
        layouts: dict[tuple[int, int], object] = {}
        quads: list = []
        tex_run: list[Draw] = []
        for d in draws:
            if d.kind == "text":
                if tex_run:
                    frame = render_draws(tex_run, c, frame)
                    tex_run = []
                k, colour = int(d.source[1]), int(d.source[2])  # type: ignore[call-overload]
                key = (k, colour)
                if key not in layouts:
                    layouts[key] = tb.layout_box(scene.choice_texts[k], lang, font=font, width=choice_width(lang),
                                                 height=0xDC, spacing=0, leading=9, color=colour, flags=0,
                                                 sx=0.74, sy=0.74, block=d.slot)
                quads += tb.block_quads(layouts[key], d.pos[0], d.pos[1], draw_flags=d.flags,  # type: ignore[arg-type]
                                        alpha=d.alpha, lang=lang)
            elif d.kind == "hit":
                continue
            else:
                if quads:
                    frame = tb.render_quads(quads, font, transform=tr, background=frame)
                    quads = []
                tex_run.append(d)
        if quads:
            frame = tb.render_quads(quads, font, transform=tr, background=frame)
        if tex_run:
            frame = render_draws(tex_run, c, frame)
        info["line_ys"] = choice_line_ys(v)
        info["draws"] = draws
        return frame, info
    finally:
        if own:
            c.close()


def _scene_background(game: Path, name: str) -> np.ndarray:
    """Centre-crop of a full-screen picture (STCH at the script-space centre 240, 136)."""
    from .catalog import ScriptImageResolver, unpack_image_id
    from .image import load
    r = ScriptImageResolver.from_game(Path(game)).resolve(name)
    if r.file_id is None:
        raise ValueError(f"picture {name} not found")
    arc, idx = unpack_image_id(r.file_id)
    rgba = load(Path(game) / "image" / arc, idx).rgba.astype(np.float32) / 255.0
    h, w = rgba.shape[:2]
    x0, y0 = max(0, (w - 1920) // 2), max(0, (h - 1080) // 2)
    crop = rgba[y0:y0 + 1080, x0:x0 + 1920]
    out = np.zeros((1080, 1920, 3), np.float32)
    out[:crop.shape[0], :crop.shape[1]] = crop[..., :3] * crop[..., 3:4]
    return out


# --- boot screen rendering ------------------------------------------------------
def boot_screen_draws(key: str, lang: int, system_lang: int = 1, alpha: float = 1.0,
                      spinner_angle: float = 0.0) -> list[Draw]:
    """Draw lists of the process-0 screens (VA 0xE8DB9..0xE903E) and of the title cautions
    (VA 0xB02F1..0xB0350) [E]."""
    c448, s448 = (640.0, 448.0), (1.0 / 3.0, 0.4148148)
    if key == "damaged_notice":
        return [Draw("tex", -1, ("parts", 2 + system_lang, -1), c448, (0.0, 0.0), 0x10, s448, alpha=min(1.0, alpha),
                     va=0xE8ED9, note="sprite 0x1E")]
    if key == "autosave_notice":
        c540 = (960.0, 540.0)
        return [
            Draw("tex", -1, ("parts", 8 + lang, -1), c448, (0.0, 0.0), 0x10, s448, alpha=min(1.0, alpha),
                 va=0xE8ED9, note="sprite 0x1E"),
            Draw("tex", -1, ("parts", 7, -1), c540, (895.0, 497.5), 0x34, (0.25, 0.25), alpha=min(1.0, alpha),
                 rotation=spinner_angle, va=0xE8F6B, note="sprite 0x1D spinner, +500/65536 turn per frame"),
            Draw("tex", -1, ("parts", 13 + lang, -1), c540, (881.0, 491.5), 0x10, (0.25, 0.25),
                 alpha=min(1.0, alpha), va=0xE8FB9, note="sprite 0x1F 'Saving...', alpha x pulse 0.3..1"),
            Draw("tex", -1, ("parts", 18, -1), c540, (875.0, 477.5), 0x10, (0.25, 0.25), alpha=min(1.0, alpha),
                 blend=BLEND_PS_ADD, va=0xE8FE6, note="sprite 0x1B glow (blend 0xB)"),
        ]
    if key in ("caution", "caution1", "caution2"):
        names = {"caution": ("CAUTION_JA.MZP", "CAUTION_EN.MZP", "CAUTION_ZC.MZP", "CAUTION_ZT.MZP"),
                 "caution1": ("CAUTION1.MZP", "CAUTION1_EN.MZP", "CAUTION1_ZC.MZP", "CAUTION1_ZT.MZP"),
                 "caution2": ("CAUTION2.MZP", "CAUTION2_EN.MZP", "CAUTION2_ZC.MZP", "CAUTION2_ZT.MZP")}[key]
        nm = names[lang] if 0 <= lang < 4 else names[1]     # VA 0xB0DA4: lang > 3 -> *_en
        return [Draw("tex", 137, ("allpac3", nm, -1), (960.0, 544.0), (0.0, 0.0), 0x10, (0.5, 0.5037037),
                     va=0xB0350, note="slot 0x89")]
    raise KeyError(key)


def render_boot_screen(game: Path, key: str, lang: int, cache: Optional[AssetCache] = None) -> np.ndarray:
    own = cache is None
    c = cache or AssetCache(Path(game) / "image")
    try:
        draws = boot_screen_draws(key, lang)
        return render_draws([d for d in draws if d.rotation == 0.0], c)
    finally:
        if own:
            c.close()


def still_vs_movie(game: Path, times: Sequence[float] = (0.0, TITLE_LOOP_TIME, 60.0)) -> list[tuple[int, float, float]]:
    """Mean absolute RGB difference (0..255) between TITLE_PARTS_EN child 9+T and frames of
    titlebg<T>.mp4: the still is the frame at the loop point VA 0x2186E4."""
    c = AssetCache(Path(game) / "image")
    out = []
    try:
        for t_val in (1, 2, 3, 4):
            still = c.image(("allpac3", "TITLE_PARTS_EN.MRG", 9 + t_val))[..., :3]
            for tm in times:
                fr = movie_frame(game, f"titlebg{t_val}.mp4", tm)
                out.append((t_val, float(tm), float(np.abs(fr - still).mean() * 255.0)))
    finally:
        c.close()
    return out


def _down4(a: np.ndarray) -> np.ndarray:
    h, w = a.shape[0] // 4, a.shape[1] // 4
    return a[:h * 4, :w * 4, :3].reshape(h, 4, w, 4, 3).mean(axis=(1, 3))


def _highpass(a: np.ndarray, k: int = 9) -> np.ndarray:
    """``a`` minus its k x k box blur (edge-padded): keeps the cloud edges, i.e. the motion."""
    p = k // 2
    c = np.cumsum(np.cumsum(np.pad(a, ((p + 1, p), (p + 1, p), (0, 0)), mode="edge"), axis=0), axis=1)
    s = c[k:, k:] - c[:-k, k:] - c[k:, :-k] + c[:-k, :-k]
    return a - s / (k * k)


def still_match_curve(game: Path, t_val: int, first: int, count: int) -> list[tuple[int, float, float]]:
    """Per-frame match of TITLE_PARTS_EN child 9 + T against frames ``first .. first+count-1``
    of titlebg<T>.mp4 (30 fps, first pts 0): (frame, mean abs diff, high-pass mean abs diff),
    both on 4x box-downscaled RGB 0..255.  The high-pass metric is motion-sensitive and has a
    sharp minimum; the plain difference is flat within a few frames of it."""
    import subprocess
    c = AssetCache(Path(game) / "image")
    try:
        st = _down4(c.image(("allpac3", "TITLE_PARTS_EN.MRG", 9 + t_val)) * 255.0)
    finally:
        c.close()
    st_hp = _highpass(st)
    p = Path(game) / "image" / "movie" / f"titlebg{t_val}.mp4"
    r = subprocess.run(["ffmpeg", "-v", "error", "-ss", f"{first / 30.0:.6f}", "-i", str(p), "-frames:v", str(count),
                        "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], capture_output=True, check=True)
    n = len(r.stdout) // (1920 * 1080 * 3)
    if n != count:
        raise ValueError(f"titlebg{t_val}: got {n} frames, expected {count}")
    out = []
    frames = np.frombuffer(r.stdout, np.uint8).reshape(n, 1080, 1920, 3)
    for i in range(n):
        fr = _down4(frames[i].astype(np.float32))
        out.append((first + i, float(np.abs(fr - st).mean()), float(np.abs(_highpass(fr) - st_hp).mean())))
    return out


def export_samples(game: Path, out: Path) -> list[Path]:
    """Write the sample set of samples/misc_boot_title_choice_flow/."""
    import json
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    written: list[Path] = []

    def w(name: str, text: str) -> None:
        p = out / name
        p.write_text(text, encoding="utf-8", newline="\n")
        written.append(p)

    rows = ["case\tkey\tframes_min\tframes_max\tva\tdetail"]
    cases = {"returning_player": dict(), "first_boot": dict(bit485=False, g10=False),
             "after_prologue": dict(bit485=True, g10=False), "damaged_save": dict(damaged=True)}
    for case, kw in cases.items():
        for s in boot_sequence(**kw):   # type: ignore[arg-type]
            rows.append(f"{case}\t{s.key}\t{'' if s.frames_min is None else s.frames_min}\t"
                        f"{'' if s.frames_max is None else s.frames_max}\t{s.va}\t{s.detail}")
    w("boot_sequences.tsv", "\n".join(rows) + "\n")
    rows = ["g2\tT\trecord0_child\tmovie_id\tmovie\tbgm\trule_bit\trule_script\tnext"]
    for g2 in range(6):
        bg = title_background(1, g2)
        rule = TITLE_RULES[g2]
        bit = "" if rule is None or rule[0] < 0 else str(rule[0])
        scr = TITLE_BITS.get(rule[0], "") if rule and rule[0] >= 0 else ""
        nxt = "0 (reset)" if rule is None else str(rule[1])
        rows.append(f"{g2}\t{TITLE_T[g2]}\t{bg.record0_child}\t{bg.movie_index}\ttitlebg{bg.movie_index + 1}.mp4\t"
                    f"{bg.bgm}\t{bit}\t{scr}\t{nxt}")
    w("title_states.tsv", "\n".join(rows) + "\n")
    from .catalog_eboot import Eboot
    with Eboot(Path(game) / "eboot.bin") as eb:
        t = read_tables(eb)
    tj = {"title_t": t.title_t,
          "title_rules": {hex(k): [[hex(c), b, n] for c, b, n in v] for k, v in t.title_rules.items()},
          "logo_rects": {LANG_CODES[k]: v for k, v in t.logo_rects.items()},
          "logo_pos": {LANG_CODES[k]: v for k, v in t.logo_pos.items()},
          "glow_pos": {LANG_CODES[k]: v for k, v in t.glow_pos.items()},
          "widget_pos": t.widget_pos, "hl_index": t.hl_index,
          "hl_pos": {LANG_CODES[k]: v for k, v in t.hl_pos.items()},
          "widgets": [[hex(wid), [[s, f, list(r)] for s, f, r in fr]] for wid, fr in t.widgets],
          "bg_redraw_y": t.bg_redraw_y,
          "boot_cases": {hex(k): hex(v) for k, v in t.boot_cases.items()},
          "boot_states_written_by_immediate": sorted(t.boot_states_written),
          "title_states_written_by_immediate": sorted(t.title_states_written),
          "floats": {hex(k): v for k, v in t.floats.items()},
          "title_returns": {str(r): {"handler": hex(tr.handler), "mode": tr.mode, "start_scene": tr.start_scene,
                                     "reachable": tr.reachable, "note": tr.note}
                            for r in range(0, 0x23) for tr in (title_return(r),)},
          "title_return_table_0x209dc0": [hex(x) for x in t.title_return_targets],
          "sel_ring_k": t.sel_ring_k, "sel_prefix_base_overflow_threshold": t.sel_prefix,
          "choice_decision_frames": choice_decision_frames(),
          "choice_census": choice_census(game),
          "ui_lookup_split": ui_lookup_split(game)}
    w("tables.json", json.dumps(tj, indent=1) + "\n")
    cache = AssetCache(Path(game) / "image")
    try:
        dl: dict[str, list[dict[str, object]]] = {}
        views = {"title_en_g2_0": TitleView(lang=1, g2=0, cursor=1),
                 "title_en_g2_5_continue": TitleView(lang=1, g2=5, cursor=0, continue_available=True),
                 "title_ja_g2_0": TitleView(lang=0, g2=0, cursor=1),
                 "title_ja_g2_5_continue": TitleView(lang=0, g2=5, cursor=0, continue_available=True),
                 "title_en_press_any_button": TitleView(lang=1, g10=0),
                 "title_ja_press_any_button": TitleView(lang=0, g10=0)}
        for name, v in views.items():
            img = render_draws(title_draw_list(v), cache)
            to_png(img, out / f"{name}.png")
            written.append(out / f"{name}.png")
            dl[name] = [_draw_json(d, cache) for d in title_draw_list(v)]
        for lang in (0, 1):
            sc = find_choice_scene("01_00_ARC01_1", 0, lang, game)
            for cur in (-1, 1):
                img, info = render_choice(game, sc, ChoiceView(lang=lang, cursor=cur), cache=cache)
                name = f"choice_01_00_ARC01_1_{LANG_CODES[lang]}_cursor{cur if cur >= 0 else 'none'}"
                to_png(img, out / f"{name}.png")
                written.append(out / f"{name}.png")
                draws_used: list[Draw] = info["draws"]  # type: ignore[assignment]
                dl[name] = [_draw_json(d, cache) for d in draws_used]
                dl[name].append({"kind": "summary", "x": SEL_X[_lang_key(lang)], "line_ys": info["line_ys"],
                                 "canvas": list(SEL_CANVAS), "texts": sc.choice_texts, "icon_y": info["icon_y"],
                                 "page_origins": info.get("page_origins")})
        # review fixes: the decided phase (non-chosen lines hidden, flash) and the glow cross-fade
        sc = find_choice_scene("01_00_ARC01_1", 0, 1, game)
        a18, f18 = choice_decision_state(18)
        extra = {"choice_01_00_ARC01_1_en_decided_f18": ChoiceView(lang=1, cursor=1, phase=3, alpha=a18, flash=f18),
                 "choice_01_00_ARC01_1_en_crossfade_h05": ChoiceView(lang=1, cursor=2, prev_cursor=1, highlight=0.5)}
        for name, cv in extra.items():
            img, info = render_choice(game, sc, cv, cache=cache)
            to_png(img, out / f"{name}.png")
            written.append(out / f"{name}.png")
            draws_used = info["draws"]  # type: ignore[assignment]
            dl[name] = [_draw_json(d, cache) for d in draws_used]
        for key in ("damaged_notice", "autosave_notice", "caution", "caution1", "caution2"):
            img = render_boot_screen(game, key, 1, cache)
            to_png(img, out / f"boot_{key}_en.png")
            written.append(out / f"boot_{key}_en.png")
            dl[f"boot_{key}_en"] = [_draw_json(d, cache) for d in boot_screen_draws(key, 1)]
        w("draw_lists.json", json.dumps(dl, indent=1, ensure_ascii=False) + "\n")
    finally:
        cache.close()
    rows = ["T\ttime_s\tmean_abs_diff_0_255"]
    for tv, tm, dv in still_vs_movie(game):
        rows.append(f"{tv}\t{tm:.4f}\t{dv:.2f}")
    w("title_still_vs_movie.tsv", "\n".join(rows) + "\n")
    rows = ["T\tframe\ttime_s\tmad_down4\thighpass_mad_down4"]
    for tv in (1, 2, 3, 4):
        for fr, mad, hp in still_match_curve(game, tv, 225, 33):
            rows.append(f"{tv}\t{fr}\t{fr / 30.0:.4f}\t{mad:.3f}\t{hp:.3f}")
    w("title_still_frame_scan.tsv", "\n".join(rows) + "\n")
    return written


def _draw_json(d: Draw, cache: AssetCache) -> dict[str, object]:
    if d.kind in ("tex", "widget") and d.size is None and d.rect is None:
        d = replace(d, size=cache.size(d.source))
    try:
        px = [round(v, 2) for v in d.pixel_rect()]
    except ValueError:
        px = []
    return {"kind": d.kind, "slot": d.slot, "source": list(d.source), "rect": d.rect, "canvas": list(d.canvas),
            "pos": [round(d.pos[0], 4), round(d.pos[1], 4)], "flags": hex(d.flags),
            "scale": [round(d.scale[0], 6), round(d.scale[1], 6)], "alpha": round(d.alpha, 4),
            "blend": hex(d.blend), "pixel_rect": px, "va": hex(d.va), "note": d.note}


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def _game() -> Path:
    from . import game_dir
    g = game_dir()
    if g is None:
        raise SystemExit("game folder not found (set TSUKI_GAME_DIR)")
    return g


def _lang(s: str) -> int:
    return LANG_CODES.index(s) if s in LANG_CODES else int(s)


def _main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m tsuki.misc_boot_title_choice_flow", description=__doc__.split("\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("boot", help="ordered boot screens")
    b.add_argument("--damaged", action="store_true")
    b.add_argument("--first-run", action="store_true", help="bit 485 clear (prologue never seen)")
    b.add_argument("--g10", type=int, default=1)
    ts = sub.add_parser("title-state", help="title background per G[2] and the progression")
    ts.add_argument("--bits", default="560,383,562")
    sub.add_parser("tables", help="read the tables from eboot.bin and compare with the constants")
    rt = sub.add_parser("render-title")
    rt.add_argument("out")
    rt.add_argument("--lang", default="en")
    rt.add_argument("--g10", type=int, default=1)
    rt.add_argument("--g2", type=int, default=0)
    rt.add_argument("--cursor", type=int, default=1)
    rt.add_argument("--continue", dest="cont", action="store_true")
    rt.add_argument("--movie-time", type=float)
    rc = sub.add_parser("render-choice")
    rc.add_argument("out")
    rc.add_argument("--lang", default="en")
    rc.add_argument("--script", default="01_00_ARC01_1")
    rc.add_argument("--block", type=int, default=0)
    rc.add_argument("--cursor", type=int, default=-1)
    rc.add_argument("--prev", type=int, default=-1, help="previous cursor (u16 0x17506A4)")
    rc.add_argument("--phase", type=int, default=2, help="0..3 (3 = decided)")
    rc.add_argument("--highlight", type=float, default=1.0, help="f32 0x17506B4")
    rc.add_argument("--alpha", type=float, default=1.0, help="a = HUD x (1 - 0x6F430()) x 0x17506A8")
    rc.add_argument("--flash", type=float, default=1.0, help="f32 0x17506AC (phase 3)")
    rc.add_argument("--scene", action="store_true", help="draw the last full-screen picture behind (approximate)")
    rb = sub.add_parser("render-boot")
    rb.add_argument("outdir")
    rb.add_argument("--lang", default="en")
    sp = sub.add_parser("samples", help="write the sample set (PNGs, TSV, JSON)")
    sp.add_argument("outdir")
    sub.add_parser("lookup-split", help="UI registration-table lookup split (695 / 4 / 8)")
    a = ap.parse_args(argv)
    if a.cmd == "samples":
        for p in export_samples(_game(), Path(a.outdir)):
            print(p)
        return 0
    if a.cmd == "lookup-split":
        print(ui_lookup_split(_game()))
        return 0
    if a.cmd == "boot":
        for s in boot_sequence(damaged=a.damaged, bit485=not a.first_run, g10=bool(a.g10)):
            fr = "" if s.frames_min is None else f"{s.frames_min}..{s.frames_max} f"
            print(f"{s.key:24} {fr:14} {s.va:28} {s.detail}")
    elif a.cmd == "title-state":
        bits = [int(x) for x in a.bits.split(",") if x]
        for g2 in range(6):
            bg = title_background(1, g2)
            print(f"G[2]={g2}: child {bg.record0_child}, movie {bg.movie_index}, BGM {bg.bgm}, "
                  f"next with bits {bits}: {advance_title_state(g2, bits)}")
        print("progression from 0:", title_progression(bits))
    elif a.cmd == "tables":
        from .catalog_eboot import Eboot
        with Eboot(_game() / "eboot.bin") as eb:
            t = read_tables(eb)
        print("T", t.title_t, "rules", {hex(k): [(hex(c), b, n) for c, b, n in v] for k, v in t.title_rules.items()})
        print("logo rects", t.logo_rects[0], t.logo_rects[1])
        print("boot states written", sorted(hex(x) for x in t.boot_states_written))
        print("title states written", sorted(hex(x) for x in t.title_states_written))
        print("widgets", [(hex(w), f[0], f[1][2]) for w, f in t.widgets])
    elif a.cmd == "render-title":
        v = TitleView(lang=_lang(a.lang), g10=a.g10, g2=a.g2, cursor=a.cursor, continue_available=a.cont,
                      movie_time=a.movie_time)
        to_png(render_title(_game(), v), Path(a.out))
    elif a.cmd == "render-choice":
        g = _game()
        sc = find_choice_scene(a.script, a.block, _lang(a.lang), g)
        cv = ChoiceView(lang=_lang(a.lang), cursor=a.cursor, prev_cursor=a.prev, phase=a.phase,
                        highlight=a.highlight, alpha=a.alpha, flash=a.flash)
        img, info = render_choice(g, sc, cv, with_scene=a.scene)
        to_png(img, Path(a.out))
        print({k: v for k, v in info.items() if k != "draws"})
    elif a.cmd == "render-boot":
        g = _game()
        out = Path(a.outdir)
        out.mkdir(parents=True, exist_ok=True)
        for key in ("damaged_notice", "autosave_notice", "caution", "caution1", "caution2"):
            to_png(render_boot_screen(g, key, _lang(a.lang)), out / f"boot_{key}_{a.lang}.png")
    return 0


if __name__ == "__main__":
    sys.exit(_main())
