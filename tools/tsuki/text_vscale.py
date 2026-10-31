"""Text vertical scale: text-space units -> 1920 x 1080 output pixels (PS4 CUSA47999 v01.01).

Answers FORMATS.md 7 P0 item 1 ("78 px vs 81 px line pitch"); the write-up with all evidence is
``docs/research/text_vertical_scale.md``.  Summary:

* The HUD (process 0xF, VA 0x53CF0) draws the page text into display list 0x1E with no canvas of
  its own, pops back to its parent list, sets the 640 x 448 canvas (VA 0x554DC) and appends a
  **type-9 "draw nested list" record with scale (1.0, 1.3)** (VA 0x5559C..0x555C1 ->
  0xDCE30).  The executor (0xD9E10 -> 0xDB0E0 / 0xDA780 / 0xDA1B0) multiplies every vertex of
  the nested list by (1.0, 1.3) and adds the record offset, then the 640 x 448 projection
  (0xD2BE0) maps canvas units to pixels.  Hence, for everything in list 0x1E (message text,
  previous messages, name plate, click-wait icon):

      px = 3 * (x + ox)                       ox = trunc(shake_x) + trunc(quake_x)
      py = (1080 / 448) * (1.3 * y + oy)      oy = trunc(shake_y) + trunc(quake_y) [+ slide]
                                                   [+ 302.8148 when F822 != 1]

  i.e. **3.0 px per unit horizontally and 1.3 * 1080 / 448 = 3.1339286 px per unit
  vertically**, for glyph positions *and* glyph quad heights.  The text space is therefore
  640 x 344.615 (= 448 / 1.3) units, not 640 x 360.  Line pitch 26.02 units = 81.55 px.
* The choice screen (VA 0xA6E10) and the backlog (0x3C360 / 0x3C4B0) draw directly under the
  canvas 640 x 344.7: py = y * 1080 / 344.7 = 3.1331592 y, px = 3 x (glyph heights too: font
  pages have texture size mode 0, so 0xE1DC0 applies no extra factor).
* UI screens draw text directly under their own canvas (mostly 640 x 448: 3.0 x 2.4107 px per
  unit; their layout sy arguments carry the 448/360 compensation, e.g. config 0.74 / 0.9176).
* The prologue movie is **not** an engine render of this path: its line positions advance
  3.115 px / unit and its glyph shapes are 3.0 px / unit in both directions (measured below), while
  the PS4 code stretches glyphs by 1.3 * 1080/448 / 3 = 1.0446 vertically.

Verified by running the engine's own HUD (0x53CF0), choice draw (0xA6E10), block draw (0xC8F50),
glyph quad (0xE1DC0) and display-list executor (0xD9E10) under unicorn
(:func:`emulate_hud_text`, scratch/text_vscale/*_emu.py).

CLI::

    python -m tsuki.text_vscale table                 contexts and their mapping
    python -m tsuki.text_vscale point main 30 35      one text-space point -> pixels
    python -m tsuki.text_vscale verify                static eboot checks (constants, instructions)
    python -m tsuki.text_vscale emulate [en|ja]       run the HUD under unicorn (about 15 s)
    python -m tsuki.text_vscale movie                 per-glyph measurement of the prologue frames
"""
from __future__ import annotations

import argparse
import math
import struct
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Optional, Sequence, Union

import numpy as np

__all__ = [
    "SCREEN_W", "SCREEN_H", "HUD_TEXT_LIST", "HUD_POPUP_LIST", "HUD_CANVAS", "HUD_TEXT_SCALE",
    "HUD_BOTTOM_WINDOW_DY", "CHOICE_CANVAS", "BACKLOG_CANVAS", "TEXT_SPACE_W", "TEXT_SPACE_H",
    "KX_HUD", "KY_HUD", "KY_CHOICE", "Canvas", "NestedDraw", "TextMapping", "GlyphQuadPx",
    "TextContext", "MovieModel", "mapping_for", "engine_vertex_px", "hud_text_offsets",
    "hud_text_mapping", "MAIN_TEXT", "CHOICE_SCREEN", "BACKLOG", "UI_640x448", "TITLE_960x540",
    "TEXT_CONTEXTS", "MOVIE_PROLOGUE", "context", "verify_eboot", "emulate_hud_text",
    "emulate_choice_text", "half_max_edges", "movie_glyph_edges", "fit_movie_vertical",
    "fit_movie_horizontal", "PROLOGUE_FRAMES", "EdgeSample", "residuals_vs",
]

F32 = np.float32

# ---------------------------------------------------------------------------------------------
# engine constants (CUSA47999 01.01 text VAs; float32 bit patterns in the comments)
# ---------------------------------------------------------------------------------------------
SCREEN_W = 1920.0                 # projection constants 0x2D4034 / 0x2D4064 / 0x2D4094
SCREEN_H = 1080.0                 # 0x2D4038 (+ -960 0x2D403C, -272 0x2D4040, -540 0x2D4044)
PROJ_OY_DIV = 272.0               # canvas y offset unit: py -= 1080 * oy / 272

HUD_TEXT_LIST = 0x1E              # display list of the page text (VA 0x5449C / 0x544A1)
HUD_POPUP_LIST = 0x23             # HUD pop-up text list of 0x53200 (VA 0x53297), same scale
HUD_WINDOW_LIST = 0x1D            # window pictures (VA 0x54078), drawn nested with (1, 1)
HUD_CANVAS = (640.0, 448.0)       # 0xDCBE0 at VA 0x554DC (0x1A70CC 0x44200000, 0x1A70D0 0x43E00000)
HUD_TEXT_SCALE = (1.0, float(F32(1.3)))   # VA 0x5559C xmm3 = 0x1A70D8 (1.0), 0x555A4 xmm4 = 0x1A71A8 (0x3FA66666)
HUD_BOTTOM_WINDOW_DY = float(F32(302.8148193359375))   # 0x1A7188 (0x4397684C), added when F822 != 1
HUD_SLIDE_PER_FADE = 20.0         # 0x1A7184: (1 - window fade) * 20 when F822 != 1 and [0xDF074C] == 2
CHOICE_CANVAS = (640.0, float(F32(344.7)))   # 0xDCBE0 at VA 0xA6F3C (0x2168BC, 0x2168CC 0x43AC599A)
BACKLOG_CANVAS = (640.0, float(F32(344.7)))  # VA 0x3C3C2 (0x1A6448 / 0x1A644C) and 0x3C57F

TEXT_SPACE_W = 640.0
TEXT_SPACE_H = HUD_CANVAS[1] / 1.3          # 344.615 text units span the 1080 output lines

KX_HUD = SCREEN_W / HUD_CANVAS[0]                          # 3.0
KY_HUD = HUD_TEXT_SCALE[1] * SCREEN_H / HUD_CANVAS[1]      # 3.1339285 (float64 of the float32 1.3)
KY_CHOICE = SCREEN_H / CHOICE_CANVAS[1]                    # 3.1331592

FONT_PAGE = 1024                  # font atlas pages are 1024 x 1024 (fonts.md 3), uv = texel / 1024


# ---------------------------------------------------------------------------------------------
# mapping model
# ---------------------------------------------------------------------------------------------
@dataclass(frozen=True)
class Canvas:
    """Projection record (render command type 12, builder 0xDCBE0, executor 0xD9E52)."""
    w: float
    h: float
    ox: float = 0.0               # pixel x offset (stored as ox - 960)
    oy: float = 0.0               # y offset in 1/272 of the screen height (stored as -1080 oy/272 - 540)

    @property
    def scale(self) -> tuple[float, float]:
        return SCREEN_W / self.w, SCREEN_H / self.h

    @property
    def offset_px(self) -> tuple[float, float]:
        return self.ox, -SCREEN_H * self.oy / PROJ_OY_DIV


@dataclass(frozen=True)
class NestedDraw:
    """Type-9 record (0xDCE30 / 0xDCEC0): the nested list's vertices become v * (sx, sy) + (dx, dy)
    in the parent's canvas units (0xDB0E0 VA 0xDB325..0xDB357; 0xDA780 and 0xDA1B0 likewise)."""
    sx: float = 1.0
    sy: float = 1.0
    dx: float = 0.0
    dy: float = 0.0


@dataclass(frozen=True)
class GlyphQuadPx:
    """A glyph quad on the screen: corners in pixels and the sampled atlas rectangle (texels)."""
    x0: float
    y0: float
    x1: float
    y1: float
    u0: float
    v0: float
    u1: float
    v1: float


@dataclass(frozen=True)
class TextMapping:
    """Affine text-space -> pixel map: px = kx * x + ox, py = ky * y + oy.  Glyph quads use the
    same kx / ky for their size (every vertex goes through the same transform)."""
    name: str
    kx: float
    ky: float
    ox: float = 0.0
    oy: float = 0.0
    canvas: Canvas = field(default_factory=lambda: Canvas(*HUD_CANVAS))
    nest: NestedDraw = field(default_factory=NestedDraw)

    def point(self, x: float, y: float) -> tuple[float, float]:
        return self.kx * x + self.ox, self.ky * y + self.oy

    def glyph_quad(self, x: float, y: float, u0: int, v0: int, u1: int, v1: int,
                   sx: float, sy: float) -> GlyphQuadPx:
        """The quad 0xE1DC0 emits for an atlas rect (u0, v0)-(u1, v1) (the record's inclusive
        corners) drawn at (x, y) with scale (sx, sy): it spans (u1 - u0) x (v1 - v0) texels, i.e.
        the far column / row is not drawn, and samples uv = (u0, v0) / 1024 .. (u1, v1) / 1024
        [V-emu: list 0x1E vertices (30, 35)-(37.6467, 52.76) for rect (0, 0, 31, 72) at sx 0.24667]."""
        px0, py0 = self.point(x, y)
        px1, py1 = self.point(x + (u1 - u0) * sx, y + (v1 - v0) * sy)
        return GlyphQuadPx(px0, py0, px1, py1, float(u0), float(v0), float(u1), float(v1))

    @property
    def text_space_height(self) -> float:
        """Text units covered by the 1080 output lines (344.615 for the HUD)."""
        return SCREEN_H / self.ky

    def canvas_transform(self) -> Any:
        """The equivalent ``tsuki.textbox.CanvasTransform`` (kx, ky, ox, oy, kgy = ky)."""
        from tsuki.textbox import CanvasTransform
        return CanvasTransform(kx=self.kx, ky=self.ky, ox=self.ox, oy=self.oy, kgy=self.ky)


def mapping_for(canvas: Canvas, nest: NestedDraw = NestedDraw(), name: str = "") -> TextMapping:
    """Text units drawn into a list that is executed under ``canvas`` (optionally as a nested list
    with ``nest``): px = (x * nest.sx + nest.dx) * 1920 / w + ox, likewise for y."""
    kx, ky = canvas.scale
    ox, oy = canvas.offset_px
    return TextMapping(name=name or f"{canvas.w:g}x{canvas.h:g}", kx=nest.sx * kx, ky=nest.sy * ky,
                       ox=nest.dx * kx + ox, oy=nest.dy * ky + oy, canvas=canvas, nest=nest)


def engine_vertex_px(x: float, y: float, canvas: Canvas = Canvas(*HUD_CANVAS),
                     nest: Optional[NestedDraw] = NestedDraw(1.0, HUD_TEXT_SCALE[1])) -> tuple[float, float]:
    """Pixel position of one list vertex computed in float32 in the engine's order:
    nested list v' = v * s + d (vmulps, vaddps; 0xDB0E0), projection
    c = v' * (1920 / w, 1080 / h) + (ox - 960, -1080 oy / 272 - 540) (0xD9E52 / 0xD2BE0); the
    returned screen pixel is c + (960, 540) (top-left origin, y down)."""
    vx, vy = F32(x), F32(y)
    if nest is not None:
        vx = F32(F32(vx * F32(nest.sx)) + F32(nest.dx))
        vy = F32(F32(vy * F32(nest.sy)) + F32(nest.dy))
    sx = F32(F32(SCREEN_W) / F32(canvas.w))
    sy = F32(F32(SCREEN_H) / F32(canvas.h))
    ox = F32(F32(canvas.ox) + F32(-960.0))
    oy = F32(F32(F32(SCREEN_H) * F32(canvas.oy)) / F32(-PROJ_OY_DIV)) + F32(-540.0)
    cx = F32(F32(vx * sx) + ox)
    cy = F32(F32(vy * sy) + F32(oy))
    return float(cx) + 960.0, float(cy) + 540.0


def _trunc(v: float) -> float:
    """cvttps2dq + cvtdq2ps: float32 truncated toward zero."""
    return float(math.trunc(float(F32(v))))


def hud_text_offsets(*, shake: tuple[float, float] = (0.0, 0.0), quake: tuple[float, float] = (0.0, 0.0),
                     shake_suppressed: bool = False, story_page: bool = True,
                     window_slide: bool = False, window_fade: float = 1.0) -> tuple[float, float]:
    """(dx, dy) of the HUD's type-9 record for list 0x1E, in 640 x 448 canvas units
    (VA 0x53F33..0x53F70 and 0x551E9..0x555C1):

    * shake = screen shake (0xDF0720, 0xDF0724); zero when bit 0 of byte 0xDF07C8 | 0xDF07CC is set;
    * quake = message-window quake (x 0xDF08B8, y 0xDF08B4; 0x52F80);
    * each is truncated to an integer (cvttps2dq) before the sum;
    * story_page = F822 == 1; otherwise + 302.8148 (0x1A7188) and, when ``window_slide``
      ([0xDF074C] == 2), + (1 - window_fade) * 20 (0x1A7184).

    The offsets are added after the 1.3 scale, so they move the text by 3 px / 2.4107 px per unit.
    """
    sx, sy = (0.0, 0.0) if shake_suppressed else shake
    dx = _trunc(sx) + _trunc(quake[0])
    dy = _trunc(sy)
    slide = float(F32(F32(1.0) - F32(window_fade)) * F32(HUD_SLIDE_PER_FADE)) if (window_slide and not story_page) else 0.0
    if story_page:
        dy = float(F32(F32(dy + slide) + F32(_trunc(quake[1]))))
    else:
        dy = float(F32(F32(F32(dy + HUD_BOTTOM_WINDOW_DY) + slide) + F32(_trunc(quake[1]))))
    return dx, dy


def hud_text_mapping(**kw: Any) -> TextMapping:
    """Mapping of display list 0x1E (main text box) for the given HUD state (see
    :func:`hud_text_offsets` for the keywords)."""
    dx, dy = hud_text_offsets(**kw)
    return mapping_for(Canvas(*HUD_CANVAS), NestedDraw(HUD_TEXT_SCALE[0], HUD_TEXT_SCALE[1], dx, dy),
                       name="HUD list 0x1E")


MAIN_TEXT = hud_text_mapping()
CHOICE_SCREEN = mapping_for(Canvas(*CHOICE_CANVAS), name="choice screen 640x344.7")
BACKLOG = mapping_for(Canvas(*BACKLOG_CANVAS), name="backlog 640x344.7")
UI_640x448 = mapping_for(Canvas(640.0, 448.0), name="UI 640x448")
TITLE_960x540 = mapping_for(Canvas(960.0, 540.0), name="title 960x540")


@dataclass(frozen=True)
class TextContext:
    """One text-drawing context: where its block draws go and what maps them to pixels."""
    key: str
    what: str
    functions: str                 # function VAs with block draws (0xC8F50 call sites)
    blocks: str
    mapping: Optional[TextMapping]  # None: canvas of the calling screen
    evidence: str
    confidence: str                # V-emu / E / H


TEXT_CONTEXTS: tuple[TextContext, ...] = (
    TextContext("main", "ADV page text: current message, previous messages, extra blocks, name plate, "
                "click-wait icon (everything in list 0x1E)", "0x53CF0 (0x5484F 0x549B6 0x54C2F 0x54C71 "
                "0x54CAC 0x559D5 0x560DD)", "4/5, 6, 8, 14, 0x46+j, 0x4F+j", MAIN_TEXT,
                "list 0x1E (0x544A1); canvas 640x448 (0x554DC); nested (1.0, 1.3) (0x555C1)", "V-emu"),
    TextContext("alt_choice", "choice list drawn inside list 0x1E (0xA6D00, mode != 1; blocks at (12, 5.8074 + 28k), 0x21689C / 0x2168A0)",
                "0xA6D00 (0xA6D6E)", "0x14+k", MAIN_TEXT, "called at 0x550B6 before the pop 0x550C2", "E"),
    TextContext("popup", "HUD pop-up (0x53200: pictures 10, 11, 20, 21 and blocks 0x1D, 9)", "0x53200 (0x53400 0x53431)",
                "0x1D, 9", mapping_for(Canvas(*HUD_CANVAS), NestedDraw(1.0, HUD_TEXT_SCALE[1]), "HUD list 0x23"),
                "list 0x23 (0x5329C), nested (1.0, 1.3) at 0x5348A; parent canvas 640x448 from 0x554DC [H: the "
                "offsets are animated]", "E"),
    TextContext("choice", "choice screen (_SELR menu): lines, 8 glow copies", "0xA6E10 (0xA7128..0xA758D)",
                "0x14+k, 0x64..0x73", CHOICE_SCREEN, "canvas 640x344.7 at 0xA6F3C, no nested scale", "V-emu"),
    TextContext("backlog", "backlog / history screen", "0x3C360 (0x3C426), 0x3C4B0 (0x3C80F)", "var",
                BACKLOG, "canvas 640x344.7 at 0x3C3C2 / 0x3C57F", "E"),
    TextContext("config", "config screen labels", "0x40C30 (8 sites)", "0x3A, 0x3B", UI_640x448,
                "canvas 640x448 set by the caller 0x3F140 (0x3F17C); layout sy 0.9176 = 0.74 x 1.24", "E"),
    TextContext("hud_0x14", "HUD helper texts 0xA7CD0 / 0xA8250 (sx 0.37, sy 0.4604)", "0xA7CD0, 0xA8250",
                "0x14", UI_640x448, "canvas 640x448 at 0xA7D39 / 0xA82B9", "E"),
    TextContext("saveload", "save / load slot texts", "0x70340 (0x7059F, 0x70C64)", "0x26, 0x22.., 0x27",
                UI_640x448, "canvas 640x448 at 0x703AE; layout sy / sx = 0.4085 / 0.3294 = 1.24", "E"),
    TextContext("menu", "system menu texts", "0x6CB80 (0x6CD95)", "60", UI_640x448, "canvas 640x448 at 0x6CC06", "E"),
    TextContext("gallery", "gallery / extras text (0x2A530)", "0x2A530 (0x2A6B8)", "50", UI_640x448,
                "canvas 640x448 at 0x2A55D", "E"),
    TextContext("title", "title screen text", "0xB34C0 (0xB3627)", "65", TITLE_960x540, "canvas 960x540 at 0xB34DC", "E"),
    TextContext("toast", "animated pop-up lists 0x1F/0x20 (0x685B0) and 0x6AF30: uniform zoom s x s",
                "0x685B0, 0x6AF30", "0xA+k", None, "nested (s, s) with s animated (0x686EA); canvas of the caller", "E"),
    TextContext("widgets", "UI text widgets / dialogs (0x3A030, 0x6BBF0, 0xD0190, 0xCECB0 debug)",
                "0x3A030, 0x6BBF0, 0xD0190, 0xCECB0", "widget / 2 / 19", None,
                "no canvas in the function: the calling screen's canvas applies", "E"),
)


def context(key: str) -> TextContext:
    for c in TEXT_CONTEXTS:
        if c.key == key:
            return c
    raise KeyError(key)


# ---------------------------------------------------------------------------------------------
# the prologue movie (measured; not an engine mapping)
# ---------------------------------------------------------------------------------------------
@dataclass(frozen=True)
class MovieModel:
    """How the pre-rendered ``prologue[_en].mp4`` places the same layout (per-glyph half-maximum
    edges on 4 frames, 361 edges; :func:`fit_movie_vertical`).  Positions: py = ky_pos * y + oy;
    glyph shapes ky_glyph; x as the engine."""
    kx: float = 3.0
    ky_pos: float = 3.1152
    ky_glyph: float = 3.0
    oy: tuple[tuple[str, float], ...] = (("en", 2.07), ("ja", 6.39))

    def oy_for(self, lang: str) -> float:
        return dict(self.oy)[lang]


MOVIE_PROLOGUE = MovieModel()

PROLOGUE_FRAMES: tuple[tuple[str, str, tuple[int, ...], tuple[int, ...]], ...] = (
    # (lang, file under samples/textbox, message ids of 00_02, blank lines before each message)
    ("en", "prologue_en_25s.png", (3, 4, 5), (0, 0, 0)),
    ("ja", "prologue_ja_25s.png", (3, 4, 5), (0, 0, 0)),
    ("en", "prologue_en_150s.png", (37, 38), (0, 0)),
    ("ja", "prologue_ja_150s.png", (37, 38), (0, 0)),
)


# ---------------------------------------------------------------------------------------------
# static eboot checks
# ---------------------------------------------------------------------------------------------
def _f32_at(eb: Any, va: int) -> float:
    return struct.unpack("<f", eb.read_va(va, 4))[0]


def verify_eboot(eboot: Union[str, Path]) -> dict[str, bool]:
    """Static checks of the facts this module relies on (constants and instruction operands)."""
    import capstone
    from eboot.selfelf import Eboot
    eb = Eboot.open(Path(eboot))
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)

    def ins(va: int) -> str:
        i = next(md.disasm(eb.read_va(va, 16), va))
        return f"{i.mnemonic} {i.op_str}"

    def rip_target(va: int) -> int:
        i = next(md.disasm(eb.read_va(va, 16), va))
        disp = int(i.op_str.split("rip + ")[1].split("]")[0], 16)
        return va + i.size + disp

    out: dict[str, bool] = {}
    out["projection constants 1920, 1080, -960, -272, -540"] = all(
        [_f32_at(eb, b + 4 * k) for k in range(5)] == [1920.0, 1080.0, -960.0, -272.0, -540.0]
        for b in (0x2D4034, 0x2D4064, 0x2D4094))
    out["HUD canvas 640x448 at 0x554DC"] = (ins(0x554DC) == "call 0xdcbe0" and rip_target(0x554C4) == 0x1A70CC
                                           and rip_target(0x554CC) == 0x1A70D0
                                           and (_f32_at(eb, 0x1A70CC), _f32_at(eb, 0x1A70D0)) == (640.0, 448.0))
    out["HUD nested scale x = 1.0 (xmm3, 0x5559C)"] = (ins(0x5559C).startswith("vmovss xmm3")
                                                       and rip_target(0x5559C) == 0x1A70D8
                                                       and _f32_at(eb, 0x1A70D8) == 1.0)
    out["HUD nested scale y = 1.3f (xmm4, 0x555A4)"] = (ins(0x555A4).startswith("vmovss xmm4")
                                                        and rip_target(0x555A4) == 0x1A71A8
                                                        and eb.read_va(0x1A71A8, 4) == bytes.fromhex("6666a63f"))
    out["HUD draws list 0x1E nested at 0x555C1"] = ins(0x555AC) == "mov edi, 0x1e" and ins(0x555C1) == "call 0xdce30"
    out["HUD text list selected at 0x544A1"] = ins(0x5449C) == "mov edi, 0x1e" and ins(0x544A1) == "call 0xdc740"
    out["type-9 builder stores sx, sy at +8, +0xC"] = (ins(0xDCE81) == "vmovss dword ptr [rax + 8], xmm3"
                                                       and ins(0xDCE86) == "vmovss dword ptr [rax + 0xc], xmm4")
    out["nested draw scales vertices (0xDB325 vmulps, 0xDB329 vaddps)"] = (
        ins(0xDB325) == "vmulps xmm0, xmm0, xmm4" and ins(0xDB329) == "vaddps xmm0, xmm0, xmm5")
    out["choice canvas 640x344.7 at 0xA6F3C"] = (ins(0xA6F3C) == "call 0xdcbe0" and rip_target(0xA6F24) == 0x2168CC
                                                 and eb.read_va(0x2168CC, 4) == bytes.fromhex("9a59ac43"))
    out["backlog canvas 640x344.7 at 0x3C3C2"] = (ins(0x3C3C2) == "call 0xdcbe0" and rip_target(0x3C3B2) == 0x1A644C
                                                  and eb.read_va(0x1A644C, 4) == bytes.fromhex("9a59ac43"))
    out["F822 != 1 text offset 302.8148"] = eb.read_va(0x1A7188, 4) == bytes.fromhex("4c689743")
    return out


# ---------------------------------------------------------------------------------------------
# emulation (research aid; needs unicorn, capstone, keystone and the user's eboot)
# ---------------------------------------------------------------------------------------------
_LISTS = 0x1DE83E0
_SLOTS = 0x1DBD460
_TEXTAB = 0x1DF9550
_FVARS = 0x164B1D0


def _emu_setup(eboot: Union[str, Path], lang: int) -> tuple[Any, list[dict[str, Any]], int]:
    from tsuki.misc_layer_state_and_motions_emu import BUF, LayerEmu
    E = LayerEmu(eboot)
    # display-list headers and record pool as 0xD4D80 leaves them (VA 0xD4E27..0xD4ECC)
    sent = 0x1DEB2C0
    for n in range(500):
        h = _LISTS + 24 * n
        E.wr(h, struct.pack("<IIQQ", 0, 0, sent + 0x70 * n, sent + 0x70 * n))
        E.wr(sent + 0x70 * n, bytes([1, 0, 0, 0]))
    pool = 0x1EB4D50
    E.wr(0x1FC645C, bytes(1))
    E.wr(0x1FC6460, struct.pack("<QQ", pool, pool + 0x70 * 9999))
    E.wr(pool, b"".join(bytes(0x68) + struct.pack("<Q", pool + 0x70 * (k + 1) if k < 9999 else 0)
                        for k in range(10000)))
    # font texture slots 0x21..0x61 as load_font / 0xD8B80 leave them for 1024x1024 1-tile depth-1 pages
    for slot in range(0x21, 0x62):
        base = _SLOTS + 0x58 * slot
        flags, ids, tex_id = BUF + 0x1000 + 16 * slot, BUF + 0x2000 + 16 * slot, 0x100 + slot
        E.wr(base, bytes([1, 0, 0, 0, 0, 1]) + bytes(0x52))
        E.wr(base + 0x20, struct.pack("<8h", 1024, 1024, 1024, 1024, 1, 1, 1, 0))
        E.wr(base + 0x30, struct.pack("<QQ", flags, ids))
        E.wr(flags, bytes([1]))
        E.wr(ids, struct.pack("<h", tex_id))
        E.wr(_TEXTAB + 32 * tex_id, bytes([1]))
        E.wr(_TEXTAB + 32 * tex_id + 0x18, struct.pack("<Q", 1))
    E.wr(0x66C578 + 0x24, struct.pack("<h", lang))
    E.wr(0x66C578 + 0x26, struct.pack("<hh", 1, 0))      # outline on, shadow off
    captured: list[dict[str, Any]] = []

    def d2be0() -> int:
        p = E.r("RDI")
        xy = [struct.unpack("<2f", E.rd(p + 0x18 * k + 0xC, 8)) for k in range(4)]
        uv = [struct.unpack("<2f", E.rd(p + 0x18 * k, 8)) for k in range(4)]
        proj = struct.unpack("<4f", E.rd(0xDB9458, 16))
        captured.append({"xy": xy, "uv": uv, "proj": proj, "tex": E.i32(0x1FC6458),
                         "px": [(float(F32(F32(x * proj[0]) + F32(proj[2]))) + 960.0,
                                 float(F32(F32(y * proj[1]) + F32(proj[3]))) + 540.0) for x, y in xy]})
        return 0

    E.stub(0xD2BE0, d2be0)          # GPU submission: record the projected vertices
    E.stub(0xD7E70, 1)              # texture bind
    E.stub(0xD27E0, 0)              # untextured quads
    E.stub(0xD47C0, 1)              # frame delta
    return E, captured, BUF


def _layout(E: Any, buf: int, block: int, text: bytes) -> int:
    E.wr(buf, text + b"\0" * 16)
    E.wr(0x178BE30 + 4 * block, bytes(4))
    E.wr(0x177A210 + block * 0x190, bytes(0x190))
    return int(E.call(0xC6C20, {"RDI": block, "RSI": buf, "RDX": 474, "RCX": 220, "R8": 0, "R9": 9},
                      {0: 0.74, 1: 0.74}, stack_args=[0xFFFF, 0]))


def _list_records(E: Any, ln: int) -> list[dict[str, Any]]:
    h = _LISTS + 24 * ln
    cnt = struct.unpack("<i", E.rd(h, 4))[0]
    rec = struct.unpack("<Q", E.rd(h + 8, 8))[0]
    rows: list[dict[str, Any]] = []
    for _ in range(max(cnt, 0)):
        rec = struct.unpack("<Q", E.rd(rec + 0x68, 8))[0]
        b = E.rd(rec, 0x70)
        typ, a, bb = struct.unpack_from("<Hhh", b, 2)
        row: dict[str, Any] = {"type": typ, "a": a, "b": bb}
        if typ == 1:
            row["xy"] = [struct.unpack_from("<2f", b, 8 + 0x18 * k + 12) for k in range(4)]
        elif typ == 9:
            row["scale"] = struct.unpack_from("<2f", b, 8)
            row["offset"] = struct.unpack_from("<3f", b, 0x14)
        elif typ == 12:
            row["canvas"] = struct.unpack_from("<2f", b, 0x2C)
            row["offset"] = struct.unpack_from("<2f", b, 0x14)
        rows.append(row)
    return rows


def emulate_hud_text(eboot: Union[str, Path], text: Union[str, bytes], lang: int = 1) -> dict[str, Any]:
    """Run the HUD process draw 0x53CF0 for one message (block 4, F822 = 1, F823 = 1, window and
    text fully faded in, reveal 768) and the executor 0xD9E10 on the parent list 0.  Returns the
    parent list records, list 0x1E records and the glyph quads as projected by 0xD2BE0."""
    E, captured, buf = _emu_setup(eboot, lang)
    for va in (0xA9310, 0xEB540, 0x75CD0, 0xA6D00, 0xA6E10, 0xA7A40, 0x53200, 0xA7CD0, 0xA8250, 0xEB780):
        E.stub(va, 0)
    E.stub(0xC3270, 0xFFFFFFFF)
    t = text.encode("utf-8") if isinstance(text, str) else text
    n = _layout(E, buf, 4, t)
    E.wr(_FVARS + 2 * 822, struct.pack("<hh", 1, 1))       # F822 = 1 (story page), F823 = 1
    E.wr(0xDF0730, struct.pack("<f", 1.0))                  # window fade
    E.wr(0x781148, struct.pack("<f", 1.0))                  # HUD alpha
    E.wr(0x86AA1C, struct.pack("<f", 1.0))                  # text fade
    E.wr(0x1653E80, struct.pack("<ff", 768.0, 768.0))       # reveal head / tail
    E.wr(0x16541D4, struct.pack("<i", 0))                   # text slot 0 -> block 4
    E.wr(0x120A6EC, struct.pack("<hhhh", 0, 0, 0, 2))       # history ring indices: no previous message
    E.call(0xDC740, {"RDI": 0})
    E.call(0x53CF0)
    parent = _list_records(E, 0)
    text_list = _list_records(E, HUD_TEXT_LIST)
    E.wr(0x1FC6458, struct.pack("<i", -1))
    E.call(0xD9E10, {"RDI": 0})
    return {"elements": n, "parent": parent, "text_list": text_list,
            "glyph_quads": [c for c in captured if c["tex"] >= 0x100]}


def emulate_choice_text(eboot: Union[str, Path], text: Union[str, bytes], lang: int = 1) -> dict[str, Any]:
    """Run the choice draw 0xA6E10 for one choice line (block 0x14; mode 1, phase 1, no cursor)
    under the HUD's last canvas (480 x 272) and execute the parent list."""
    E, captured, buf = _emu_setup(eboot, lang)

    def fade0() -> int:            # 0x6F430: menu fade, returns 0.0 in xmm0
        E.set_xmm_f(0, 0.0)
        return 0
    E.stub(0x6F430, fade0)
    E.stub(0xEB780, 0)
    E.stub(0xCFC90, 0)
    t = text.encode("utf-8") if isinstance(text, str) else text
    n = _layout(E, buf, 0x14, t)
    E.wr(0x17506B0, struct.pack("<i", 1))
    E.wr(0x17506A0, struct.pack("<hhh", 1, -1, -1))         # phase 1, cursor -1, previous -1
    E.wr(0x1750646, struct.pack("<h", 1))                   # one line
    E.wr(0x17506A8, struct.pack("<ff", 1.0, 1.0))
    E.wr(0x1750660, struct.pack("<f", 768.0))
    E.call(0xDC740, {"RDI": 0})
    E.call(0xDCBE0, None, {0: 480.0, 1: 272.0, 2: 0.0, 3: 0.0})
    E.call(0xA6E10, None, {0: 1.0})
    parent = _list_records(E, 0)
    E.wr(0x1FC6458, struct.pack("<i", -1))
    E.call(0xD9E10, {"RDI": 0})
    return {"elements": n, "parent": parent, "glyph_quads": [c for c in captured if c["tex"] >= 0x100]}


# ---------------------------------------------------------------------------------------------
# movie measurement (research aid)
# ---------------------------------------------------------------------------------------------
def half_max_edges(p: np.ndarray, centres: np.ndarray) -> Optional[tuple[float, float]]:
    """First rising and last falling crossing of (min + max) / 2, linear between samples; None for
    a flat profile or one that touches the window ends."""
    lo, hi = float(p.min()), float(p.max())
    if hi - lo < 0.25 * max(hi, 1e-6):
        return None
    t = 0.5 * (lo + hi)
    idx = np.nonzero(p >= t)[0]
    i0, i1 = int(idx[0]), int(idx[-1])
    if i0 == 0 or i1 == len(p) - 1:
        return None
    a = centres[i0 - 1] + (t - p[i0 - 1]) / (p[i0] - p[i0 - 1]) * (centres[i0] - centres[i0 - 1])
    b = centres[i1] + (p[i1] - t) / (p[i1] - p[i1 + 1]) * (centres[i1 + 1] - centres[i1])
    return float(a), float(b)


@dataclass(frozen=True)
class EdgeSample:
    lang: str
    frame: str
    axis: str                     # 'v' or 'h'
    origin: float                 # glyph origin (text units): line y + dy, or line x + dx
    offset: float                 # atlas edge from the rect origin * scale (text units)
    pixel: float                  # movie edge, continuous pixel coordinate (pixel j centre = j + 0.5)
    first: bool                   # top / left edge
    under_ruby: bool


def _luma(png: Union[str, Path]) -> np.ndarray:
    from PIL import Image
    a = np.asarray(Image.open(png).convert("RGB"), np.float32) / 255.0
    return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]


def movie_glyph_edges(png: Union[str, Path], lang: str, ids: Sequence[int], blank: Sequence[int] = (),
                      margin_px: float = 5.0) -> list[EdgeSample]:
    """Half-maximum ink edges of every glyph of a known page in a movie frame (luma), paired with
    the same edges on the font atlas.  Windows: the glyph's predicted ink extent under both the
    3.0 and the 3.134 vertical models +- ``margin_px``; ruby readings and '@b' glyphs are skipped;
    top edges of ruby bases are flagged ``under_ruby``."""
    from tsuki import textbox as tb
    from tsuki.font import Font
    from tsuki.text import ScriptText
    li = {"ja": 0, "en": 1}[lang]
    font = Font(lang)
    st = ScriptText()
    p = tb.main_box_params(li)
    blocks = [tb.layout_message(st.get_line(lang, i), li, p, font=font, block=4 if j == 0 else 79 + j)
              for j, i in enumerate(ids)]
    origins = tb.page_positions(blocks, li, blank_before=list(blank), origin=(p.x, p.y))
    Y = _luma(png)
    name = Path(png).name
    out: list[EdgeSample] = []
    for b, (ox, oy) in zip(blocks, origins):
        xs, ys, _ = tb.line_offsets(b, ox, oy, 0x1020)
        rub = [(r.line, xs[r.line] + r.dx, xs[r.line] + r.dx + (r.x2 - r.x) * r.sx)
               for r in b.records if r.flags & 3 == 3]
        for r in b.records:
            if not r.flags & 1 or r.flags & 0x10012 or r.y > 0x1FF or r.y2 <= r.y or r.x2 <= r.x:
                continue
            a = font.page_alpha(r.tex)[r.y:r.y2 + 1, r.x:r.x2 + 1].astype(np.float32) / 255.0
            if a.max() < 0.6:
                continue
            gx, gy = xs[r.line] + r.dx, ys[r.line] + r.dy
            x_r = gx + (r.x2 - r.x) * r.sx
            under = any(ln == r.line and not (x2 < gx or x1 > x_r) for ln, x1, x2 in rub)
            colp, rowp = a.max(0), a.max(1)
            ink_c = np.nonzero(colp >= 0.5)[0]
            ink_r = np.nonzero(rowp >= 0.5)[0]
            ve = half_max_edges(rowp, np.arange(len(rowp)) + 0.5)
            he = half_max_edges(colp, np.arange(len(colp)) + 0.5)
            if ve and len(ink_c) >= 3 and ve[1] - ve[0] >= 20:
                c0 = ink_c[0] + (ink_c[-1] - ink_c[0]) * 0.2
                c1 = ink_c[0] + (ink_c[-1] - ink_c[0]) * 0.8
                X0 = int(np.floor(3.0 * (gx + (c0 + 0.5) * r.sx)))
                X1 = int(np.ceil(3.0 * (gx + (c1 + 0.5) * r.sx)))
                Y0 = int(np.floor(min(3.0, KY_HUD) * (gy + ve[0] * r.sy) - margin_px))
                Y1 = int(np.ceil(max(3.0, KY_HUD) * (gy + ve[1] * r.sy) + margin_px))
                prof = Y[Y0:Y1, X0:X1 + 1].max(1)
                if prof.max() >= 0.7 and prof[0] <= 0.35 and prof[-1] <= 0.35:
                    me = half_max_edges(prof, np.arange(Y0, Y1) + 0.5)
                    if me:
                        out += [EdgeSample(lang, name, "v", gy, ve[k] * r.sy, me[k], k == 0, under) for k in (0, 1)]
            if he and len(ink_r) >= 4 and he[1] - he[0] >= 6:
                r0 = ink_r[0] + (ink_r[-1] - ink_r[0]) * 0.2
                r1 = ink_r[0] + (ink_r[-1] - ink_r[0]) * 0.8
                Ya = int(np.floor(KY_HUD * (gy + (r0 + 0.5) * r.sy)))
                Yb = int(np.ceil(KY_HUD * (gy + (r1 + 0.5) * r.sy)))
                Xa = int(np.floor(3.0 * (gx + he[0] * r.sx) - 4))
                Xb = int(np.ceil(3.0 * (gx + he[1] * r.sx) + 4))
                prof = Y[Ya:Yb + 1, Xa:Xb].max(0)
                if prof.max() >= 0.7 and prof[0] <= 0.35 and prof[-1] <= 0.35:
                    me = half_max_edges(prof, np.arange(Xa, Xb) + 0.5)
                    if me:
                        out += [EdgeSample(lang, name, "h", gx, he[k] * r.sx, me[k], k == 0, False) for k in (0, 1)]
    return out


def _robust_lstsq(A: np.ndarray, y: np.ndarray) -> tuple[np.ndarray, float, int]:
    keep = np.ones(len(y), bool)
    p = np.zeros(A.shape[1])
    res = y.copy()
    for _ in range(5):
        p, *_ = np.linalg.lstsq(A[keep], y[keep], rcond=None)
        res = y - A @ p
        mad = float(np.median(np.abs(res[keep] - np.median(res[keep])))) * 1.4826
        keep = np.abs(res) < max(3.0 * mad, 0.5)
    return p, float(np.sqrt((res[keep] ** 2).mean())), int(keep.sum())


def fit_movie_vertical(samples: Sequence[EdgeSample], glyph_scale: Optional[float] = None) -> dict[str, float]:
    """Fit pixel = k_pos * origin + k_glyph * offset + c over vertical edges (top edges of ruby
    bases excluded); ``glyph_scale`` fixes k_glyph.  Returns k_pos, k_glyph, c, rms, n."""
    rows = [s for s in samples if s.axis == "v" and not (s.first and s.under_ruby)]
    if glyph_scale is None:
        A = np.array([[s.origin, s.offset, 1.0] for s in rows])
        y = np.array([s.pixel for s in rows])
        p, rms, n = _robust_lstsq(A, y)
        return {"k_pos": float(p[0]), "k_glyph": float(p[1]), "c": float(p[2]), "rms": rms, "n": float(n)}
    A = np.array([[s.origin, 1.0] for s in rows])
    y = np.array([s.pixel - glyph_scale * s.offset for s in rows])
    p, rms, n = _robust_lstsq(A, y)
    return {"k_pos": float(p[0]), "k_glyph": float(glyph_scale), "c": float(p[1]), "rms": rms, "n": float(n)}


def fit_movie_horizontal(samples: Sequence[EdgeSample]) -> dict[str, float]:
    rows = [s for s in samples if s.axis == "h"]
    A = np.array([[s.origin + s.offset, 1.0] for s in rows])
    y = np.array([s.pixel for s in rows])
    p, rms, n = _robust_lstsq(A, y)
    r3 = y - 3.0 * A[:, 0]
    return {"k": float(p[0]), "c": float(p[1]), "rms": rms, "n": float(n), "median_resid_at_3": float(np.median(r3))}


def residuals_vs(samples: Sequence[EdgeSample], mapping: TextMapping, glyph_k: Optional[float] = None) -> dict[str, float]:
    """Median pixel residual (movie - model) of the top and bottom edges for a mapping."""
    gk = mapping.ky if glyph_k is None else glyph_k
    top = [s.pixel - (mapping.ky * s.origin + gk * s.offset + mapping.oy) for s in samples
           if s.axis == "v" and s.first and not s.under_ruby]
    bot = [s.pixel - (mapping.ky * s.origin + gk * s.offset + mapping.oy) for s in samples
           if s.axis == "v" and not s.first]
    return {"top": float(np.median(top)) if top else math.nan, "bottom": float(np.median(bot)) if bot else math.nan,
            "n_top": float(len(top)), "n_bottom": float(len(bot))}


# ---------------------------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------------------------
def _main(argv: Optional[list[str]] = None) -> int:
    import sys
    from tsuki import PROJECT_ROOT, game_dir
    ap = argparse.ArgumentParser(prog="python -m tsuki.text_vscale", description=__doc__.split("\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("table", help="text contexts and their pixel mapping")
    p = sub.add_parser("point", help="map one text-space point")
    p.add_argument("context")
    p.add_argument("x", type=float)
    p.add_argument("y", type=float)
    sub.add_parser("verify", help="static eboot checks")
    e = sub.add_parser("emulate", help="run the HUD / choice draw under unicorn")
    e.add_argument("lang", nargs="?", default="en", choices=["en", "ja"])
    sub.add_parser("movie", help="measure the prologue frames in samples/textbox")
    a = ap.parse_args(argv)
    sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[union-attr]
    if a.cmd == "table":
        for c in TEXT_CONTEXTS:
            m = c.mapping
            k = f"kx {m.kx:.4f} ky {m.ky:.7f} ox {m.ox:g} oy {m.oy:g}" if m else "canvas of the caller"
            print(f"{c.key:10s} [{c.confidence}] {k}\n           {c.what}; {c.functions}; {c.evidence}")
        return 0
    if a.cmd == "point":
        c = context(a.context)
        if c.mapping is None:
            print("no fixed mapping for", a.context)
            return 1
        print(c.mapping.point(a.x, a.y), "engine float32:", engine_vertex_px(a.x, a.y, c.mapping.canvas,
                                                                             c.mapping.nest))
        return 0
    g = game_dir()
    if g is None:
        print("game folder not found (TSUKI_GAME_DIR)")
        return 2
    if a.cmd == "verify":
        ok = verify_eboot(g / "eboot.bin")
        for k, v in ok.items():
            print(("OK  " if v else "FAIL"), k)
        return 0 if all(ok.values()) else 1
    if a.cmd == "emulate":
        from tsuki.text import ScriptText
        li = {"ja": 0, "en": 1}[a.lang]
        r = emulate_hud_text(g / "eboot.bin", ScriptText().get_line(a.lang, 3), li)
        for row in r["parent"]:
            print("parent list:", row)
        for q in r["glyph_quads"][:4]:
            print("glyph quad px", [tuple(round(v, 3) for v in pt) for pt in q["px"]])
        return 0
    if a.cmd == "movie":
        base = PROJECT_ROOT / "samples" / "textbox"
        samples: list[EdgeSample] = []
        for lang, png, ids, blank in PROLOGUE_FRAMES:
            if (base / png).is_file():
                samples += movie_glyph_edges(base / png, lang, ids, blank)
        for lang in ("en", "ja"):
            s = [x for x in samples if x.lang == lang]
            print(lang, "free", fit_movie_vertical(s), "\n   glyph 3.0", fit_movie_vertical(s, 3.0),
                  "\n   horizontal", fit_movie_horizontal(s),
                  "\n   engine residuals", residuals_vs(s, MAIN_TEXT))
        return 0
    return 1


if __name__ == "__main__":
    raise SystemExit(_main())
