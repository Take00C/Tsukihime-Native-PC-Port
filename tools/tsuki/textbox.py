"""ADV message window and main text box of TSUKIHIME -A piece of blue glass moon- (PS4 01.01).

Full description and evidence: docs/formats/textbox.md.  This module is the Python reference
for:

* :func:`classify` - layout pass 1 of ``Text_Layout`` (eboot VA 0xC6D80..0xC7375): one
  break class per *element* (glyph or control) in a 1200-element budget, plus the English
  word-length table.
* :func:`layout_box` - layout pass 2 (VA 0xC76C0..0xC8DE1): glyph placement, automatic
  wrapping (kinsoku / English word wrap), ruby (furigana) placement, the markup codes, line
  heights and widths.  It returns the engine's per-element glyph records (``GlyphRec``,
  identical to the 0x30-byte records the engine writes) - verified byte for byte against the
  game's own function under unicorn (scratch/textbox/verify_layout.py).
* :func:`block_quads` - what the block draw (VA 0xC8F50) does with a block: line y offsets,
  reveal window, outline / fill / drop-shadow quads in canvas units.
* :func:`main_box_params` / :func:`page_positions` - the parameters the ``_ZM`` handler
  (VA 0x92380) and the HUD (VA 0x53CF0) pass for the ordinary ADV message and how the messages
  of one page are stacked.
* :func:`render_page` - a PIL renderer that puts it all together at 1920x1080.

All float arithmetic of the layout is done in IEEE float32 in the engine's operation order
(numpy.float32), because the line-break decision compares accumulated pen positions with the
box width.
"""
from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from typing import Optional, Sequence, Union

import numpy as np

from .font import CELL, LANGS, Font, Glyph
from .font_eboot import TAGS

__all__ = [
    "ELEMENT_BUDGET", "MAX_LINE_INDEX", "SCALE_TABLE", "FONT_METRIC_TABLE", "COLOR_TABLE",
    "STYLE_FLAGS", "GlyphRec", "TextBlock", "classify", "is_wide", "layout_box",
    "BoxParams", "main_box_params", "line_offsets", "block_quads", "Quad",
    "CanvasTransform", "ENGINE_TRANSFORM", "PROLOGUE_TRANSFORM", "page_positions", "WINDOW_TABLE",
    "window_for", "WindowDraw", "bubble_text_origin", "disp_child_for_slot", "last_line_offset",
    "layout_message", "main_box_width", "render_quads", "render_page", "break_lines", "rgb565_bytes",
]

F32 = np.float32

# ---------------------------------------------------------------------------
# constants (eboot 01.01 VAs; text VA = file offset - 0x2280)
# ---------------------------------------------------------------------------
ELEMENT_BUDGET = 0x4B0            # 1200 elements: pass 1 loop bound (VA 0xC6D95), class array 0x178C100
MAX_LINE_INDEX = 31               # '^' / wrap on line 31 ends the text (VA 0xC7FCD, 0xC8BD0)
# @h / @w, VA 0x2D3730: ten float32 values read from the eboot bytes.  Entries 8 and 9 are
# written as the exact float32 patterns 0x3E92AAAA / 0x3EA15556 (the earlier decimals 0.28645834 /
# 0.31510416 were 1 ulp above / below them).
SCALE_TABLE = (0.34375, 0.37239584, 0.40104166, 0.4296875, 0.45833334, 0.48697916,
               0.22916667, 0.2578125, 0.2864583134651184, 0.3151041865348816)
FONT_METRIC_TABLE = (480, 536, 524, 0)                                  # @f, VA 0x2D3758
COLOR_TABLE = (0x632C, 0xF800, 0x345F, 0x1082, 0xAD55, 0x001F)          # @c0..5 words, VA 0x2D34C0
STYLE_FLAGS = {2: 0x100000, 3: 0x200000, 4: 0x400000, 5: 0x600000}     # VA 0x2D34E0
_SX_DIV = F32(0.33333334)         # VA 0x2D2F94: sx, sy arguments are divided by 3
_RUBY_DY, _RUBY_SX, _RUBY_SY = F32(-7.04), F32(0.1), F32(0.11)          # VA 0x2D31A0, 0xC7C9A
_RUBY_W = F32(0.1)                # VA 0x2D2FD8: reading pen advance = w * 0.1
_P_INIT = F32(-40.0)              # VA 0x2D2F98: initial ruby overlap guard
_K_MINUS160 = F32(-160.0)         # VA 0x2D2FB4 dash line_end margin
_K_EMPTY = F32(20.0)              # VA 0x2D2FB0: height of an empty line = 20 * sy
_K_XBREAK = F32(69.0)             # VA 0x2D2FA8: '@x' wrap sets the line height to 69 * sy
_K_X555 = F32(555.0)              # VA 0x2D2FA4 (English '@x' wrap threshold)


def _f(x: float) -> np.float32:
    return F32(x)


def is_wide(key: int) -> bool:
    """VA 0x11AB0: a 'wide' character (break class 1, ruby unit 2)."""
    if key < 0x100:
        return False
    if key <= 0xFFFF:
        return key in (0xC2B1, 0xC397, 0xC3B7)          # ± × ÷
    if key <= 0xFFFFFF:
        return not (0xEFBDA1 <= key <= 0xEFBE9F)        # halfwidth katakana are narrow
    return True


def _decode(b: Union[bytes, bytearray], p: int) -> tuple[int, int]:
    """VA 0x11B00 + the length rule of pass 1: (key, bytes); invalid lead -> (0, 1)."""
    c = b[p]
    if c < 0x80:
        return c, 1
    if c & 0xE0 == 0xC0:
        return (c << 8) | b[p + 1], 2
    if c & 0xF0 == 0xE0:
        return (c << 16) | (b[p + 1] << 8) | b[p + 2], 3
    if c & 0xF8 == 0xF0:
        return (c << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3], 4
    return 0, 1


def _bit(mask: int, k: int) -> bool:
    return 0 <= k < 64 and (mask >> k) & 1 == 1


def _wide_class(key: int) -> int:
    """Pass-1 class of a wide character (VA 0xC6EF3..0xC7051, 0xC70C9..0xC7211)."""
    if key > 0xE38282:
        k = key - 0xE38283
        if k <= 0x26 and _bit(0x5540000815, k):          # small kana ゃゅょゎァィゥェォ
            return 0x0D
        k = key - 0xE38383
        if 0 <= k <= 0x39 and _bit(0x30C081500000001, k):  # ッャュョヮヵヶ・ー
            return 0x0D
        k = key - 0xEFBC81
        if 0 <= k <= 0x1E:
            if _bit(0x6002900, k):                       # ），．：；
                return 0x71
            if _bit(0x40000001, k):                      # ！？
                return 0x0D
            if k == 7:                                   # （
                return 0x0E
        return 1
    k = key - 0xE38081
    if 0 <= k <= 0x0E:
        if _bit(0x5412, k):                              # 、。〉》」』】
            return 0x71
        if _bit(0x2800, k):                              # 「『
            return 0x0E
        if k == 0:                                       # U+3000 ideographic space
            return 0x0D
    k = key - 0xE38181
    if 0 <= k <= 0x22 and _bit(0x400000155, k):          # ぁぃぅぇぉっ
        return 0x0D
    k = key - 0xE28098
    if 0 <= k <= 0x0F:                                   # jump table VA 0x2D3280
        if k in (0, 4):                                  # ‘ “
            return 0x0E
        if k in (1, 5):                                  # ’ ”
            return 0x71
        if k == 15:                                      # ‧
            return 0x0D
    return 1


_WORD_PUNCT = (0x22, 0x27, 0x2E)        # mask 0x408400000000: '"' "'" '.'


def _after_dot_ok(c: int) -> bool:
    return c <= 0x2E and c in _WORD_PUNCT


def _tag_len(b: bytes, p: int) -> int:
    for t in TAGS:
        tb = t.encode("ascii")
        if b[p:p + len(tb)] == tb:
            return len(tb)
    return 0


def classify(text: Union[str, bytes], lang: int, flags: int = 0,
             english: Optional[bool] = None) -> tuple[int, bytearray, list[int]]:
    """Layout pass 1 (VA 0xC6D80..0xC7375).

    Returns ``(n, classes, wordlen)``: the number of elements (at most 1200), the break class
    of every element (byte array VA 0x178C100) and the English word-length table (u16 array
    VA 0x178C5B0, entry i-1 = length of the word that starts at element i).
    ``english`` is the is-English predicate VA 0x41A20 (defaults to ``lang == 1``).
    """
    b = (text.encode("utf-8") if isinstance(text, str) else bytes(text)) + b"\0" * 300
    if english is None:
        english = lang == 1
    cls = bytearray(ELEMENT_BUDGET)
    wl = [0] * ELEMENT_BUDGET
    p = 0
    i = 0
    budget = ELEMENT_BUDGET
    prev = 0
    ruby = 0
    while True:
        c = b[p]
        if c == 0:
            break
        q = p + 1
        cur = c
        if c >= 0x80:
            key, ln = _decode(b, p)
            q = p + ln
            if is_wide(key):
                cls[i] = _wide_class(key)
            elif not (prev <= 0x2E and prev in (0x20, 0x2C, 0x2E)):
                cls[i] = 2
                if key != 0xC2B7 and i != 0:
                    a = cls[i - 1]
                    if (a | 2) == 3 or a >= 5:
                        cls[i] = 0
        elif lang != 1 and c == 0x3C:
            ruby = 1
        elif c == 0x7C and ruby > 0:
            if ruby == 1:
                ruby = 2
            if flags & 0x2000:
                while True:
                    cur = b[q]
                    q += 1
                    if cur == 0 or cur == 0x3E:
                        break
                ruby = 0
        elif lang != 1 and c == 0x3E:
            ruby = 0
        elif c == 0x40:                                   # '@' code (VA 0xC710B)
            d = b[p + 1]
            e = d - 0x61
            if 0 <= e <= 0x16 and (_bit(0x4000A4, e) or e == 0):   # a c f h w
                q = p + 3
            elif d & 0xFE == 0x78:                        # x y: 0..3 digits
                if not (0x30 <= b[p + 2] <= 0x39):
                    q = p + 2
                elif not (0x30 <= b[p + 3] <= 0x39):
                    q = p + 3
                else:
                    q = p + 5 if 0x30 <= b[p + 4] <= 0x39 else p + 4
            elif b[p + 2] == 0x28:                        # '@X(' group: up to 255 bytes
                q = p + 2
                r = p + 4
                for _ in range(0xFF):
                    d2 = b[r - 1]
                    if d2 == 0:
                        break
                    if d2 == 0x29:
                        q = r
                        break
                    r += 1
            else:
                q = p + 2
        elif c == 0x5E:                                   # '^'
            pass
        elif c == 0x5B:                                   # '[' tag
            tl = _tag_len(b, p)
            if tl:
                q = p + tl
                cls[i] = 1
        else:                                             # other ASCII (VA 0xC713C)
            wordstart = (prev == 0x20 or (prev == 0x2C and c != 0x20)
                         or (prev == 0x2E and not _after_dot_ok(c)))
            if wordstart:
                if english and i != 0:
                    wl[i - 1] = 1
                    k = 0
                    sc = c
                    while True:
                        d = b[q + k]
                        if d == 0 or d == 0x5E or sc == 0x20:
                            break
                        if sc == 0x2C and d != 0x20:
                            break
                        if sc == 0x2E and not _after_dot_ok(d):
                            break
                        wl[i - 1] = k + 2
                        k += 1
                        if budget == k:
                            break
                        sc = d
            elif c & 0xFD == 0x2C:                        # ',' '.'
                cls[i] = 3
            elif prev in (0x21, 0x2E, 0x3F) and c in (0x27, 0x22):
                cls[i] = 3
            else:
                cls[i] = 2
        i += 1
        budget -= 1
        prev = cur
        p = q
        if i == ELEMENT_BUDGET:
            break
    return i, cls, wl


# ---------------------------------------------------------------------------
# pass 2
# ---------------------------------------------------------------------------
@dataclass
class GlyphRec:
    """One 0x30-byte glyph record of a text block (record i = element i)."""
    index: int
    flags: int            # bit0 used, bit1 ruby reading, bit2 style 1, bit3 @a, bit16 @b, bits 20..22 style
    color: int            # RGB565 word
    ext: int              # @c6..@c9 value (outline colour selector, VA 0xC9DA7)
    line: int
    tex: int
    x: int
    y: int
    x2: int               # x + w - 1
    y2: int               # y + h - 1
    ctr: int              # reveal counter (typewriter position)
    dx: float             # x in the block (canvas units)
    dy: float             # y offset from the line top (ruby: -7.04)
    sx: float
    sy: float
    glyph: Optional[Glyph] = None

    @property
    def w(self) -> int:
        return self.x2 - self.x + 1

    @property
    def h(self) -> int:
        return self.y2 - self.y + 1

    @property
    def ruby(self) -> bool:
        return bool(self.flags & 2)


@dataclass
class TextBlock:
    """Result of one layout call (the 0x190-byte block VA 0x177A210 + 0x190 * block)."""
    n: int                         # elements (pass 1)
    width_arg: int                 # +0x02 width argument
    height: int                    # +0x04 height limit (draw stops below y + height)
    flags: int                     # +0x08 caller flags
    spacing: int                   # +0x16
    leading: int                   # +0x18 extra space between lines
    box_width: int                 # effective W (VA 0x538F0 or '@f')
    sx: float                      # +0x70 base glyph scale (argument / 3)
    sy: float                      # +0x74
    nlines: int                    # +0x14
    last_glyph_line: int           # +0x20
    line_glyphs: list[int]         # +0x22 u16[32]
    line_width: list[float]        # +0x80 f32[32]
    line_height: list[float]       # +0x100 f32[32]
    records: list[GlyphRec]
    classes: bytes
    wordlen: list[int]
    end_ctr: int                   # typewriter counter after the text (VA 0x178BB50[block])
    y_field: int = 0               # +0x1E ('@y')
    empty: bool = False            # True: no element; the engine returned 0, block untouched


def main_box_width(lang: int, width: int = 474, window: int = 0, block: int = 4) -> int:
    """VA 0x538F0(block, width): the width the layout really uses.

    For blocks 4..8 and 79..89 (the message slots and the page history): 600 when the window
    index (F1105, or F1109 in English if > 0) is 42; else 574 when the language is English and
    width == 474; else width unchanged.
    """
    if not (4 <= block <= 8 or 79 <= block <= 89):
        return width
    if window == 42:
        return 600
    if lang == 1 and width == 474:
        return 574
    return width


def layout_box(text: Union[str, bytes], lang: int, *, font: Optional[Font] = None,
               width: int = 474, height: int = 220, spacing: int = 0, leading: int = 9,
               color: int = 0xFFFF, flags: int = 0, sx: float = 0.74, sy: float = 0.74,
               block: int = 4, window: int = 0, saved_x: float = 0.0,
               normal_window: Optional[bool] = None, fonts: Optional[dict[int, Font]] = None,
               ) -> TextBlock:
    """``Text_Layout(block, text, width, height, spacing, leading, color, flags; sx, sy)``.

    Mirrors eboot VA 0xC6C20 (both passes) for one block.  ``window`` is the F1105/F1109
    value (selects the 600-unit width at 42 and the 'normal window' predicate VA 0x53860 =
    window < 3); ``saved_x`` is the '@x' continue position (global VA 0x178BE20).
    ``font`` must be the Font of ``lang`` (glyph tables).
    """
    if font is None:
        if fonts is not None and lang in fonts:
            font = fonts[lang]
        else:
            font = Font(LANGS[lang])
    raw = text.encode("utf-8") if isinstance(text, str) else bytes(text)
    n, cls, wl = classify(raw, lang, flags)
    if n == 0:
        # VA 0xC7375: no element -> return 0; the block is not (re)initialised, not marked
        # allocated, and the end counter VA 0x178BB50[block] is not written.
        return TextBlock(n=0, width_arg=width, height=height, flags=flags, spacing=spacing,
                         leading=leading, box_width=main_box_width(lang, width, window, block),
                         sx=float(F32(_SX_DIV * F32(sx))), sy=float(F32(_SX_DIV * F32(sy))),
                         nlines=0, last_glyph_line=0, line_glyphs=[0] * 32, line_width=[0.0] * 32,
                         line_height=[0.0] * 32, records=[], classes=bytes(cls), wordlen=wl,
                         end_ctr=-1, empty=True)
    b = bytearray(raw + b"\0" * 300)
    if normal_window is None:
        normal_window = window < 3
    W = main_box_width(lang, width, window, block)
    english = lang == 1

    sxa, sya = F32(sx), F32(sy)
    base_sx = F32(_SX_DIV * sxa)
    base_sy = F32(_SX_DIV * sya)
    cur_sx, cur_sy = base_sx, base_sy
    style = (flags >> 19) & 2
    anim = 0
    flag_b = False
    literal_caret = False
    spacing_f = F32(spacing)
    bit17 = (flags >> 17) & 1

    recs: list[GlyphRec] = []
    line_glyphs = [0] * 32
    line_width = [F32(0.0)] * 32
    line_height = [F32(0.0)] * 32
    nlines = 0
    last_glyph_line = 0
    y_field = 0

    ctr = 1
    saved_ctr = 0
    ext = 0
    colw = color & 0xFFFF
    hangcap = 0            # [rbp-0x5c]
    hang = 0               # [rbp-0x80]
    nowrap = 0             # [rbp-0x88]
    kind = 0               # [rbp-0x54]
    pen = F32(0.0)         # [rbp-0x4c]
    maxh = F32(0.0)        # [rbp-0x84]
    line = 0               # [rbp-0x68]
    P = _P_INIT            # [rbp-0x140] ruby overlap guard
    OFF = F32(0.0)         # xmm2 / [rbp-0xc0] ruby offset
    rmin = F32(0.0)        # [rbp-0x94]
    rpen = F32(0.0)        # [rbp-0x9c]
    r_ctr = 0              # [rbp-0x10c]
    r_line = 0             # [rbp-0xe4]
    r_line_next = 0        # [rbp-0x124]
    bu = 0                 # [rbp-0x90] base width units
    ru = 0                 # [rbp-0xd8] reading width units
    just_wrapped = 0       # edi
    W_f = F32(W)           # [rbp-0xa0] (refreshed from W at each glyph)
    prev: Optional[int] = None
    p = 0
    i = 0
    stale: Optional[Glyph] = None

    def new_line(set_empty_height: bool = True) -> None:
        nonlocal line, pen, maxh, hang, nlines
        nlines += 1
        if set_empty_height and line_height[line] == 0:
            line_height[line] = F32(cur_sy * _K_EMPTY)
        line += 1
        hang = 0
        pen = F32(0.0)
        maxh = F32(0.0)

    while i < n:
        c = b[p]
        q = p + 1
        if c < 0x80 and c == 0x40:                         # '@' (VA 0xC77D0)
            op = b[p + 1]
            q = p + 2
            if op in (0x54, 0x6B):                         # T k
                pass
            elif op == 0x31:                               # '1'
                b[p + 2] = 0x5E
                literal_caret = True
            elif op == 0x32:                               # '2'
                b[p + 2] = 0x29
            elif op == 0x61:                               # a
                q = p + 3
                if not flags & 0x40:
                    anim = (b[p + 2] - 0x30) & 0xFFFFFFFF
                    if anim >= 0x80000000:
                        anim -= 1 << 32
            elif op == 0x62:                               # b
                if not flags & 0x40:
                    flag_b = True
            elif op == 0x63:                               # c
                q = p + 3
                v = (b[p + 2] - 0x30) & 0xFF
                if v == 0:
                    colw = color & 0xFFFF
                    ext = 0
                elif v <= 5:
                    colw = COLOR_TABLE[v]
                elif v < 10:
                    ext = v
            elif op == 0x66:                               # f
                q = p + 3
                v = (b[p + 2] - 0x30) & 0xFF
                W = FONT_METRIC_TABLE[v] if v < len(FONT_METRIC_TABLE) else 0
            elif op in (0x68, 0x77):                       # h w
                q = p + 3
                v = (b[p + 2] - 0x30) & 0xFF
                sc = F32(SCALE_TABLE[v]) if v < len(SCALE_TABLE) else F32(0.0)
                if op == 0x68:
                    cur_sy = F32(sya * sc)
                else:
                    cur_sx = F32(sxa * sc)
            elif op == 0x69:                               # i
                if english:
                    style = {1: 4, 2: 5}.get(style, 3)
                else:
                    style = 1
            elif op == 0x67:                               # g
                style = 1
            elif op == 0x7A:                               # z
                style = 2
            elif op == 0x6C:                               # l
                ctr = saved_ctr
            elif op == 0x75:                               # u
                saved_ctr = ctr
            elif op == 0x74:                               # t
                ctr = 0
            elif op == 0x6F:                               # o
                nowrap = 1
            elif op == 0x70:                               # p
                nowrap = 0
            elif op == 0x72:                               # r (VA 0xC87B9)
                anim = 0
                flag_b = False
                ext = 0
                cur_sx, cur_sy = base_sx, base_sy
                style = 1 if style == 4 else (2 if style == 5 else 0)
                colw = color & 0xFFFF
            elif op == 0x73:                               # s (VA 0xC8836)
                sign = 1
                val = 0
                r = p + 3
                ended = False
                for _ in range(0x100):
                    d = b[r - 1]
                    if d == 0 or d == 0x29 or d == 0x2C:
                        ended = True
                        break
                    if d == 0x2D:
                        sign = -1
                    elif 0x30 <= d <= 0x39:
                        val = (val * 10 + d - 0x30) & 0xFFFFFFFF
                    r += 1
                q = r if ended else p + 0x102
                v = (sign * val) & 0xFFFFFFFF
                if v >= 0x80000000:
                    v -= 1 << 32
                cur_sx = cur_sy = F32(F32(v) * F32(0.015625))
            elif op == 0x78:                               # x (VA 0xC88E1)
                d = b[p + 2]
                if 0x30 <= d <= 0x39:
                    if flags & 0x40:
                        q = p + 3
                        if 0x30 <= b[p + 3] <= 0x39:
                            q = p + 4
                            if 0x30 <= b[p + 4] <= 0x39:
                                q = p + 5
                    else:
                        x0 = F32(0.0)
                        x1 = F32(F32(x0 * F32(10.0)) + F32(d - 0x30))
                        q = p + 3
                        if 0x30 <= b[p + 3] <= 0x39:
                            x1 = F32(F32(x1 * F32(10.0)) + F32(b[p + 3] - 0x30))
                            q = p + 4
                            if 0x30 <= b[p + 4] <= 0x39:
                                x1 = F32(F32(x1 * F32(10.0)) + F32(b[p + 4] - 0x30))
                                q = p + 5
                        pen = x1
                elif not flags & 0x40:
                    sxv = F32(saved_x)
                    if sxv > F32(F32(cur_sx * F32(-64.0)) + F32(W)) or (english and sxv > _K_X555):
                        if not sxv > F32(F32(cur_sx * F32(-64.0)) + F32(W)):
                            pen = sxv
                        # wrap onto a new line (VA 0xC896F)
                        line_height[line] = F32(cur_sy * _K_XBREAK)
                        hang = 0
                        just_wrapped = 1
                        nlines += 1
                        line += 1
                        pen = F32(0.0)
                        maxh = F32(0.0)
                    else:
                        pen = sxv
            elif op == 0x79:                               # y (VA 0xC89E4)
                y_field = 0
                d = b[p + 2]
                if 0x30 <= d <= 0x39:
                    y_field = d - 0x30
                    q = p + 3
                    if 0x30 <= b[p + 3] <= 0x39:
                        y_field = y_field * 10 + b[p + 3] - 0x30
                        q = p + 4
                        if 0x30 <= b[p + 4] <= 0x39:
                            y_field = y_field * 10 + b[p + 4] - 0x30
                            q = p + 5
            else:                                          # default (VA 0xC8625)
                if b[p + 2] == 0x28:
                    r = p + 4
                    for _ in range(0xFF):
                        d = b[r - 1]
                        if d == 0:
                            break
                        if d == 0x29:
                            q = r
                            break
                        r += 1
            prev = p
            p = q
            i += 1
            continue
        if c == 0x5E and not literal_caret:                # '^' (VA 0xC7F3D)
            if just_wrapped:
                just_wrapped = 0
                nowrap = 0
            else:
                brk = not (flags & 0x4000) and (not (flags & 0x8000) or pen != 0)
                if brk:
                    nlines += 1
                    if line_height[line] == 0:
                        line_height[line] = F32(cur_sy * _K_EMPTY)
                    if line > 30:
                        break
                    line += 1
                    hang = 0
                    pen = F32(0.0)
                    maxh = F32(0.0)
                OFF = F32(0.0)
                nowrap = 0
                just_wrapped = 0
            prev = p
            p = q
            i += 1
            continue
        if c == 0:
            break
        if c < 0x80 and c != 0x5E:
            if lang != 1 and c == 0x3C:                    # '<' ruby start (VA 0xC7717)
                r_line_next = line + 1
                k = 0
                bu = 0
                base = p + 1
                while True:
                    d = b[base + k]
                    if d <= 0x5C:
                        if d == 0 or d == 0x29:
                            break
                    else:
                        if d == 0x5D:                      # ']' resets the count to 2
                            k += 1
                            bu = 2
                            continue
                        if d == 0x7C:
                            k += 1
                            break
                    key, _ln = _decode(b, base + k)
                    if key < 0x100:
                        k += 1
                    elif key <= 0xFFFF:
                        k += 2
                    elif key <= 0xFFFFFF:
                        k += 3
                    else:
                        k += 4
                    bu += int(is_wide(key)) + 1
                ru = 0
                while True:
                    d = b[base + k]
                    if d in (0, 0x29, 0x3E):
                        break
                    key, _ln = _decode(b, base + k)
                    if key < 0x100:
                        k += 1
                    elif key <= 0xFFFF:
                        k += 2
                    elif key <= 0xFFFFFF:
                        k += 3
                    else:
                        k += 4
                    ru += int(is_wide(key)) + 1
                S = F32(F32(F32(cur_sx * F32(32.0)) * F32(bu)) + F32(F32(ru) * F32(-3.392)))
                half = F32(S * F32(0.5))
                if F32(P - half) > 0:
                    OFF = P
                    P = F32(P - S)
                else:
                    OFF = half
                    P = F32(-half)
                kind = 1
                rmin = F32(0.0)
                rpen = pen
                r_ctr = ctr
                r_line = line
                prev = p
                p = q
                i += 1
                continue
            if c == 0x7C and kind > 0:                     # '|' (VA 0xC7EE7)
                if kind == 1:
                    kind = 2
                    if flags & 0x2000:
                        while True:
                            d = b[q]
                            q += 1
                            if d == 0 or d == 0x3E:
                                break
                        kind = 0
                prev = p
                p = q
                i += 1
                continue
            if lang != 1 and c == 0x3E:                    # '>' (VA 0xC82D2)
                kind = 0
                prev = p
                p = q
                i += 1
                continue
        if c == 0x5E:
            literal_caret = False
        # ---- glyph (VA 0xC7824) ----
        W_f = F32(W)
        line_end = False
        if pen >= F32(F32(cur_sx * _K_MINUS160) + W_f):
            m = _cmod10(cls[i + 1] if i + 1 < ELEMENT_BUDGET else 0)
            if m != 2:
                line_end = m != 3 and nowrap == 0 and kind < 2
        line_start = bool(F32(0.0) >= pen)
        v = font.dash_variant_at(b, p, prev, line_start=line_start, line_end=line_end, flag_b=flag_b)
        if v >= 0:
            g = font.dash_glyph(v, style, kind=kind, scale=float(cur_sx),
                                text=bytes(b[p:p + 3]).decode("utf-8", "replace"), consumed=3)
        else:
            g = font.char_glyph(b, p, style, kind=kind, scale=float(cur_sx))
            if g.consumed == 0:                           # invalid lead: outputs untouched
                g = stale if stale is not None else Glyph("", 0, 0, 0xFFFF, 0, 0, CELL, CELL, 0.0, "invalid")
                g = Glyph(g.text, 0, g.key, g.tex, g.x, g.y, g.w, g.h, g.adj, "invalid")
        stale = g
        adj = F32(g.adj)
        fl = 1 | (4 if style == 1 else 0) | STYLE_FLAGS.get(style, 0)
        if v < 0 and anim != 0:
            fl |= 8
        if flag_b:
            fl |= 0x10000
        w, h = g.w, g.h
        x2 = (g.x + w - 1) & 0xFFFF
        y2 = (g.y + h - 1) & 0xFFFF
        if kind >= 2:                                      # ruby reading (VA 0xC7BA1)
            rctr = int(bu / 3) + 1 + r_ctr
            x = F32(F32(rpen + OFF) + adj)
            if x > F32(W_f + F32(-6.4)):
                OFF = F32(0.0)
                r_line = r_line_next
                x = F32(F32(rmin + F32(0.0)) + adj)
                base_x = rmin
                P = _P_INIT
            else:
                base_x = rpen
            recs.append(GlyphRec(i, fl | 2, colw, ext, r_line, g.tex & 0xFFFF, g.x, g.y, x2, y2,
                                 rctr & 0xFFFF, float(x), float(_RUBY_DY), float(_RUBY_SX),
                                 float(_RUBY_SY), g))
            rpen = F32(F32(F32(F32(w) * _RUBY_W) + adj) + base_x)
            nxt_cls = cls[i + 1] if i + 1 < ELEMENT_BUDGET else 0
            wrap_i = i
        else:
            if kind == 1:
                rmin = pen if pen < rmin else rmin
            dx = F32(pen + adj)
            recs.append(GlyphRec(i, fl, colw, ext, line, g.tex & 0xFFFF, g.x, g.y, x2, y2, ctr & 0xFFFF,
                                 float(dx), 0.0, float(cur_sx), float(cur_sy), g))
            adv = F32(F32(F32(w - 3) * cur_sx) + spacing_f)
            pen = F32(F32(adv + dx) + F32(cur_sx * F32(-3.0)))
            line_width[line] = pen
            hh = F32(F32(h - 4) * cur_sy)
            if hh > maxh:
                line_height[line] = hh
                maxh = hh
            ctr += 1
            last_glyph_line = line
            line_glyphs[line] = (line_glyphs[line] + 1) & 0xFFFF
            if kind == 0:
                P = F32(P - adv)
            nxt_cls = cls[i + 1] if i + 1 < ELEMENT_BUDGET else 0
            wrap_i = i
            if kind == 1:
                # last base glyph followed by '|': look past the reading (VA 0xC7DF2..0xC7EDF)
                nb = g.consumed if g.consumed else 0
                lead = b[p]
                nb = 1 if lead < 0x80 else (2 if lead & 0xE0 == 0xC0 else
                                            (3 if lead & 0xF0 == 0xE0 else (4 if lead & 0xF8 == 0xF0 else 0)))
                if b[p + nb] == 0x7C and n - i >= 3:
                    k = p + nb + 1
                    cnt = 0
                    limit = n - 2 - i
                    found = False
                    while cnt != limit:
                        d = b[k]
                        if d < 0x80:
                            if d == 0x3E:
                                found = True
                                break
                            k += 1
                        elif d & 0xE0 == 0xC0:
                            k += 2
                        elif d & 0xF0 == 0xE0:
                            k += 3
                        elif d & 0xF8 == 0xF0:
                            k += 4
                        cnt += 1
                    if found:
                        wrap_i = cnt + 2 + i
                        idx = cnt + 3 + i
                        nxt_cls = cls[idx] if idx < ELEMENT_BUDGET else 0
        # ---- automatic wrap (VA 0xC7FFD..0xC8BD0) ----
        consumed = g.consumed
        prev = p
        p = p + consumed
        i += 1
        res = _wrap(i_elem=i - 1, i_look=wrap_i, nxt=nxt_cls, cls=cls, wl=wl, n=n, pen=pen, W=W_f, sx=cur_sx,
                    english=english, nowrap=nowrap, kind=kind, hang=hang, hangcap=hangcap,
                    flags=flags, bit17=bit17, normal_window=normal_window)
        brk, hang, hangcap, just_wrapped = res
        if brk:
            nlines += 1
            if line_height[line] == 0:
                line_height[line] = F32(cur_sy * _K_EMPTY)
            line += 1
            hang = 0
            pen = F32(0.0)
            maxh = F32(0.0)
        if line > MAX_LINE_INDEX:
            break
    nlines += 1
    return TextBlock(n=n, width_arg=width, height=height, flags=flags, spacing=spacing,
                     leading=leading, box_width=W, sx=float(base_sx), sy=float(base_sy),
                     nlines=nlines, last_glyph_line=last_glyph_line, line_glyphs=line_glyphs,
                     line_width=[float(x) for x in line_width],
                     line_height=[float(x) for x in line_height], records=recs,
                     classes=bytes(cls), wordlen=wl, end_ctr=ctr, y_field=y_field)


def _cmod10(c: int) -> int:
    """Signed C remainder of the (signed char) class value by 10."""
    s = c - 256 if c >= 128 else c
    r = abs(s) % 10
    return -r if s < 0 else r


def _wrap(*, i_elem: int, i_look: int, nxt: int, cls: bytearray, wl: list[int], n: int, pen: np.float32,
          W: np.float32, sx: np.float32, english: bool, nowrap: int, kind: int, hang: int,
          hangcap: int, flags: int, bit17: int, normal_window: bool) -> tuple[bool, int, int, int]:
    """Automatic wrap decision after a glyph (VA 0xC7FFD..0xC8BD0).

    Returns ``(break, hang, hangcap, just_wrapped)``.  ``i_elem`` is the glyph's element index
    (class / word-length lookups, [rbp-0x48]); ``i_look`` is r13, the start of the look-ahead
    (the '>' of a ruby group after its last base glyph, else ``i_elem``); ``nxt`` is the class of
    the element after ``i_look``.
    """
    enabled = nowrap == 0 and kind < 2
    if not enabled:
        return False, hang, hangcap, 0
    if english:
        lim0 = F32(F32(sx * F32(-64.0)) + W)
        lim1 = F32(F32(sx * F32(-192.0)) + lim0)
        wlen = wl[i_elem] if i_elem < ELEMENT_BUDGET else 0
        if wlen != 0:
            x2 = F32(F32(F32(F32(wlen + 1) * F32(-32.0)) * sx) + W)
            if x2 < lim0:
                lim1 = x2
            lim0 = x2 if x2 < lim0 else lim0
    else:
        lim0 = F32(F32(sx * F32(-96.0)) + W)
        lim1 = lim0
    nxt_s = nxt - 256 if nxt >= 128 else nxt
    look_x: np.float32
    if pen > lim1:
        if pen > lim0:
            # past the margin: kinsoku / word rules (VA 0xC80D8, 0xC8A94)
            cap = 1
            al = 1
            if flags & 0x20000:
                al = bit17
            elif english:
                al = bit17
            else:
                cur = cls[i_elem] if i_elem < ELEMENT_BUDGET else 0
                cap = 7
                if not (cur == 0x71 or nxt == 0x71):
                    if cur == 0x0E:
                        cap = 6
                    elif cur == 0x0D:
                        cap = 1 if hangcap >= 7 else 0
                    else:
                        cap = 7 if (cur == 2 and nxt == 2) else 1
            hangcap = cap
            m_next = _cmod10(nxt)
            if m_next in (2, 3):
                if (not al) or cap > hang:
                    return _after_hang(hang + 1, hangcap, i_look, nxt, cls, n, pen, W, sx, english,
                                       normal_window)
                return True, hang, hangcap, 1
            cur = cls[i_elem] if i_elem < ELEMENT_BUDGET else 0
            if _cmod10(cur) != 4:
                return True, hang, hangcap, 1
            if al and cap <= hang:
                return True, hang, hangcap, 1
            return _after_hang(hang + 1, hangcap, i_look, nxt, cls, n, pen, W, sx, english, normal_window)
        # English: lim1 < pen <= lim0 (VA 0xC82A7)
        if nxt_s > 1:
            if (nxt_s - 4) % 10 == 0 and nxt_s >= 4:
                if pen > F32(F32(sx * F32(-128.0)) + lim0):
                    return True, hang, hangcap, 1
            look_x = pen
        else:
            step0 = F32(F32(sx * F32(32.0)) + F32(3.0)) if nxt_s == 0 else F32(sx * F32(64.0))
            x4 = F32(step0 + pen)
            if i_look + 2 < n:
                step = F32(F32(sx * F32(32.0)) + F32(3.0))
                k = i_look + 2
                for _ in range(n - 2 - i_look):
                    d = cls[k] if k < ELEMENT_BUDGET else 0
                    if _cmod10(d) != 2:
                        break
                    if x4 > lim0:
                        return True, hang, hangcap, 1
                    x4 = F32((step if d == 2 else F32(sx * F32(64.0))) + x4)
                    k += 1
            look_x = x4
        return _english_lookahead(look_x, hang, hangcap, i_look, nxt, cls, n, W, sx, english,
                                  normal_window)
    return _english_lookahead(pen, hang, hangcap, i_look, nxt, cls, n, W, sx, english, normal_window)


def _after_hang(hang: int, hangcap: int, i_cur: int, nxt: int, cls: bytearray, n: int,
                pen: np.float32, W: np.float32, sx: np.float32, english: bool,
                normal_window: bool) -> tuple[bool, int, int, int]:
    return _english_lookahead(pen, hang, hangcap, i_cur, nxt, cls, n, W, sx, english, normal_window)


def _english_lookahead(x: np.float32, hang: int, hangcap: int, i_cur: int, nxt: int,
                       cls: bytearray, n: int, W: np.float32, sx: np.float32, english: bool,
                       normal_window: bool) -> tuple[bool, int, int, int]:
    """VA 0xC817F: in the normal (full-screen) window, English text past 3/4 of the box breaks
    before a word whose estimated width would end beyond W + 2*(32*sx + 3)."""
    if not normal_window:
        return False, hang, hangcap, 0
    if not x > F32(F32(W * F32(3.0)) * F32(0.25)):
        return False, hang, hangcap, 0
    if not english:
        return False, hang, hangcap, 0
    if nxt != 0:
        return False, hang, hangcap, 0
    if i_cur + 2 >= n:
        return False, hang, hangcap, 0
    s32 = F32(sx * F32(32.0))
    acc = F32(F32(s32 + x) + F32(3.0))
    step = F32(s32 + F32(3.0))
    lim = F32(F32(step + step) + W)
    k = i_cur + 2
    for _ in range(n - 2 - i_cur):
        d = cls[k] if k < ELEMENT_BUDGET else 0
        if d == 1 or (d != 0 and d > 5):
            acc = F32(F32(sx * F32(64.0)) + acc)
        elif d == 0:
            return False, hang, hangcap, 0
        else:
            acc = F32(step + acc)
        if acc > lim:
            return True, hang, hangcap, 1
        k += 1
    return False, hang, hangcap, 0


def break_lines(block: TextBlock) -> list[list[GlyphRec]]:
    """Glyph records (base glyphs only) grouped by line."""
    out: list[list[GlyphRec]] = [[] for _ in range(block.nlines)]
    for r in block.records:
        if not r.ruby and r.line < len(out):
            out[r.line].append(r)
    return out


# ---------------------------------------------------------------------------
# block draw (VA 0xC8F50)
# ---------------------------------------------------------------------------
DRAW_SHADOW = 0x10        # drop shadow (config +0x28 'text shadow')
DRAW_ALWAYS = 0x20        # set by the HUD for every message block
DRAW_CENTER = 0x100       # line x = x + (width_arg - line_width) / 2
DRAW_RIGHT = 0x200        # line x = x + width_arg - line_width
DRAW_VCENTER = 0x400      # y += (height - total) / 2
DRAW_BOTTOM = 0x800       # y += height - total
DRAW_OUTLINE = 0x1000     # outline pass (also needs config +0x26 'text outline' != 0)
REVEAL_ALL = 768.0        # head/tail value that shows a whole block (VA 0x1A7130)
MIRROR_X = (463.0, 565.0)  # '@b' mirrored x origin, non-English / English (VA 0x2D3260)
OUTLINE_RGB = (0, 0, 0)   # 0xDB7BB0 entry 0: bytes ff 00 00 00 (byte 0 is not alpha) [E; meaning H]


def rgb565_bytes(c: int) -> tuple[int, int, int]:
    """Fill colour block bytes 1..3 built from the record colour word (VA 0xC9C87..0xC9D06)."""
    return (c >> 8) & 0xF8, (c >> 3) & 0xF8, (c << 3) & 0xF8


@dataclass
class Quad:
    kind: str                 # 'shadow' | 'outline' | 'fill'
    tex: int                  # texture slot (font page = tex - 0x21)
    u0: int                   # atlas rect, inclusive corners as in the record (outline: v + 512)
    v0: int
    u1: int
    v1: int
    x: float                  # text-space units (top-left of the quad)
    y: float
    sx: float                 # text-space units per texel
    sy: float
    rgb: tuple[int, int, int]
    alpha: float
    rec: Optional[GlyphRec] = None


def line_offsets(block: TextBlock, x: float, y: float, draw_flags: int = 0x20
                 ) -> tuple[list[float], list[float], float]:
    """Per-line x and y origins of a block drawn at (x, y) (VA 0xC8FE8..0xC9401).

    y_i = y + sum_{j<i}(leading + line_height_j) (float32); x_i = x, or centred / right-aligned
    on ``width_arg`` (the raw width argument, block +0x02) by draw flags 0x100 / 0x200;
    draw flags 0x400 / 0x800 centre / bottom-align the block inside ``height``.
    Returns (xs, ys, total_height).
    """
    nl = block.nlines
    lead = F32(block.leading)
    acc = F32(0.0)
    ys: list[float] = []
    for i in range(nl):
        ys.append(float(F32(acc + F32(y))))
        acc = F32(acc + F32(lead + F32(block.line_height[i] if i < 32 else 0.0)))
    flags = draw_flags | block.flags
    if flags & DRAW_CENTER:
        wa = F32(block.width_arg)
        xs = [float(F32(F32(F32(wa - F32(block.line_width[i])) * F32(0.5)) + F32(x))) for i in range(nl)]
    elif flags & DRAW_RIGHT:
        x0 = F32(F32(block.width_arg) + F32(x))
        xs = [float(F32(x0 - F32(block.line_width[i]))) for i in range(nl)]
    else:
        xs = [float(F32(x))] * nl
    if flags & (DRAW_VCENTER | DRAW_BOTTOM):
        off = F32(F32(block.height) - acc)
        if flags & DRAW_VCENTER:
            off = F32(off * F32(0.5))
        ys = [float(F32(off + F32(v))) for v in ys]
    return xs, ys, float(acc)


def _substitute_bars(recs: Sequence[GlyphRec], head: float) -> list[tuple[int, int, int, int, int]]:
    """Redaction-bar pictures (VA 0xC9401..0xC9732): (tex, u0, v0, u1, v1) for every record."""
    count = sum(1 for r in recs if r.tex >= 0x5F and F32(r.ctr) <= F32(head))
    n = min(count, 21)
    base = (n - 2) * (n - 3) // 2 + 2 * n - 3 if n >= 2 else 0
    out = []
    k_i = 0
    for r in recs:
        t, u0, v0, u1, v1 = r.tex, r.x, r.y, r.x2, r.y2
        if r.tex >= 0x61 and r.y >= 0xDB:
            c = k_i % 14
            t, u0, v0, u1, v1 = 0x61, c * 68, 0xDB, c * 68 + 0x43, 0x123
            k_i = (k_i + 1) % 21
        elif r.tex >= 0x5F:
            k = base + k_i
            t = 0x5F + k // 98
            u0 = (k % 14) * 68
            v0 = (k % 98 // 14) * 73
            u1, v1 = u0 + 0x43, v0 + 0x48
            k_i = (k_i + 1) % 21
        out.append((t, u0, v0, u1, v1))
    return out


def block_quads(block: TextBlock, x: float, y: float, *, draw_flags: int = 0x1020,
                alpha: float = 1.0, head: float = REVEAL_ALL, tail: float = REVEAL_ALL,
                text_fade: float = 1.0, outline_option: bool = True, instant_option: bool = False,
                lang: int = 0, outline_rgb: tuple[int, int, int] = OUTLINE_RGB) -> list[Quad]:
    """The quads VA 0xC8F50 emits for a laid-out block drawn at (x, y) (text-space units).

    ``head`` / ``tail`` are the reveal positions (VA 0x1653E80 / 0x1653E84): a glyph whose
    counter is > head is not drawn, < tail fully, otherwise with alpha
    (head - ctr) / (head - tail) (1 when head - tail <= 0, and 1 for every drawn glyph when
    ``instant_option`` = config +0xA3 == 1).  Order per glyph: drop shadow (draw flag 0x10:
    black, alpha 0.66 * a, at +1/+1 unit), outline (draw flag 0x1000 and ``outline_option``
    = config +0x26: atlas v + 512, alpha 2*max(0, text_fade - 0.5) * a_r * alpha^2), fill
    (record colour, alpha a_r * alpha).  The engine stops at the first glyph whose y is below
    y + height (returns 1).
    """
    flags = draw_flags | block.flags
    xs, ys, _total = line_offsets(block, x, y, flags)
    subs = _substitute_bars(block.records, head)
    out: list[Quad] = []
    want_outline = bool(flags & DRAW_OUTLINE) and outline_option
    h_f, t_f = F32(head), F32(tail)
    for r, (tex, u0, v0, u1, v1) in zip(block.records, subs):
        if v0 > 0x1FF:
            continue
        c = F32(r.ctr)
        if c > h_f:
            continue
        if t_f > c:
            a_r = F32(1.0)
        elif not F32(h_f - t_f) > 0:
            a_r = F32(1.0)
        else:
            a_r = F32(F32(h_f - c) / F32(h_f - t_f))
        if instant_option and a_r > 0:
            a_r = F32(1.0)
        if not 0 <= r.line < len(ys):
            continue
        line = r.line
        if r.flags & 0x10000:                           # '@b': mirrored placement (VA 0xC9A9D)
            wq = F32(u1 - u0)
            if (u1 - u0) == 0x43 and tex == 0x5E:
                wq = F32(wq + F32(-5.0))
            gx = F32(F32(F32(MIRROR_X[1 if lang == 1 else 0]) - F32(r.dx)) - F32(wq * F32(r.sx)))
            gx = F32(gx + F32(xs[line]))
        else:
            gx = F32(F32(xs[line]) + F32(r.dx))
        gy = F32(F32(r.dy) + F32(ys[line]))
        if gy > F32(F32(block.height) + F32(y)):
            break
        fa = float(a_r * F32(alpha))
        if flags & DRAW_SHADOW:
            out.append(Quad("shadow", tex, u0, v0, u1, v1, float(gx) + 1.0, float(gy) + 1.0, r.sx, r.sy,
                            (0, 0, 0), fa * 0.66, r))
        if want_outline:
            oa = 2.0 * max(0.0, text_fade - 0.5) * float(a_r) * alpha * alpha
            out.append(Quad("outline", tex, u0, v0 + 0x200, u1, v1 + 0x200, float(gx), float(gy),
                            r.sx, r.sy, outline_rgb, oa, r))
        out.append(Quad("fill", tex, u0, v0, u1, v1, float(gx), float(gy), r.sx, r.sy,
                        rgb565_bytes(r.color), fa, r))
    return out


# ---------------------------------------------------------------------------
# the ADV main text box (HUD VA 0x53CF0, _ZM VA 0x92380)
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class BoxParams:
    width: int = 474          # edx (VA 0x538F0 turns 474 into 574 in English)
    height: int = 220         # ecx: drawing stops below y + 220
    spacing: int = 0          # r8d
    leading: int = 9          # r9d
    sx: float = 0.74          # xmm0 = 0.74 (* F31 / 24 when F31 > 0), VA 0x20B2F4
    sy: float = 0.74          # xmm1 = 0.74 (* F32 / 24 when F32 > 0)
    color: int = 0xFFFF       # first stack argument
    flags: int = 0            # second stack argument: bit 20 = start in style 2 (bubble windows)
    x: float = 87.0           # text origin in the 640x360 text space (VA 0x5465C / 0x54673)
    y: float = 49.0
    window: int = 0           # F1105 (F1109 in English when > 0)


def main_box_params(lang: int, *, window: int = 0, read: bool = False, read_color_mode: int = 0,
                    f31: int = 0, f32: int = 0) -> BoxParams:
    """Arguments of the _ZM layout call (VA 0x9280B) and the HUD origin of the first message.

    ``window`` = F1105 (F1109 overrides it in English when > 0); 0..2 = the ordinary
    full-screen page, 3..55 = a speech bubble (style 2 via flag bit 20, see :func:`window_for`).
    ``read_color_mode`` = config +0x2A: 1 -> already-read messages get colour word 0xDC6D in
    the full-screen window (0xFFFF in bubbles).
    """
    normal = window < 3
    sx = 0.74 if f31 <= 0 else float(F32(F32(F32(f31) / F32(24.0)) * F32(0.74)))
    sy = 0.74 if f32 <= 0 else float(F32(F32(F32(f32) / F32(24.0)) * F32(0.74)))
    color = 0xFFFF
    if read_color_mode == 1 and read:
        color = 0xDC6D if normal else 0xFFFF
    flags = 0 if normal else 1 << 20
    x, y = (30.0, 35.0) if lang == 1 else (87.0, 49.0)
    if not normal:
        x, y = bubble_text_origin(window)
    return BoxParams(color=color, flags=flags, sx=sx, sy=sy, x=x, y=y, window=window)


def layout_message(text: Union[str, bytes], lang: int, params: Optional[BoxParams] = None, *,
                   font: Optional[Font] = None, block: int = 4) -> TextBlock:
    """layout_box with the arguments of the ordinary ADV message (``main_box_params``)."""
    p = params or main_box_params(lang)
    return layout_box(text, lang, font=font, width=p.width, height=p.height, spacing=p.spacing,
                      leading=p.leading, color=p.color, flags=p.flags, sx=p.sx, sy=p.sy,
                      block=block, window=p.window)


# window table (VA 0x2006C0, 56 x {i32 slot, f32 x, f32 y} in a 1280x720 space) and the text
# anchor offsets (VA 0x200960, 56 x {f32 dx, f32 dy}); slot s -> DISP_PARTS child s - 3
# (s >= 25) or 11 + (s - 22) (s = 22..24), registration table VA 0x780C90.
_WT = [(22, 0, 0), (23, 0, 0), (24, 0, 0), (25, 525, 414), (26, 525, 414), (27, 328, 393),
       (28, 328, 393), (29, 90, 388), (30, 90, 388), (31, 525, 44), (32, 525, 44), (33, 328, 34),
       (34, 328, 34), (35, 90, 13), (36, 90, 13), (37, 94, 414), (38, 94, 414), (39, 79, 393),
       (40, 79, 393), (41, 63, 388), (42, 63, 388), (43, 94, 44), (44, 94, 44), (45, 79, 34),
       (46, 79, 34), (47, 63, 13), (48, 63, 13), (49, 338, 229), (50, 338, 229), (51, 196, 210),
       (52, 196, 210), (53, 71, 198), (54, 71, 198), (27, 318, 393), (27, 318, 393), (29, 70, 388),
       (29, 90, 378), (29, 250, 378), (30, 300, 388), (33, 268, 34), (33, 98, 34), (34, 198, 34),
       (35, -5, 13), (35, 90, 13), (35, 105, 23), (36, 90, 13), (39, -101, 393), (41, 43, 388),
       (41, 63, 388), (45, 29, 34), (47, -47, 13), (47, -17, 13), (48, 18, 13), (53, 21, 198),
       (53, 61, 198), (54, 11, 198)]
_TOFF = [(0, 0), (0, 0), (0, 0), (97, 62.67), (97, 62.67), (99, 96), (99, 96), (99, 119.34),
         (99, 119.34), (97, 66.33), (97, 66.33), (99, 82.66), (99, 82.66), (99, 89), (99, 89),
         (78, 62.67), (78, 62.67), (89, 96), (89, 96), (112, 119.34), (112, 119.34), (78, 66.33),
         (78, 66.33), (89, 82.66), (89, 82.66), (112, 89), (112, 89), (97, 58.33), (97, 58.33),
         (99, 56), (99, 56), (99, 80.33), (99, 80.33), (69, 96), (79, 88), (80, 119.34), (99, 109.34),
         (90, 119.34), (99, 110), (64, 82.66), (60, 82.66), (85, 82.66), (45, 89), (65, 89),
         (95, 65), (99, 69), (85, 96), (98, 119.34), (103, 104), (89, 82.66), (103, 65), (93, 89),
         (103, 89), (70, 80.33), (82, 55), (70, 80.33)]
# image size in the 640x448 window canvas for indices 33..55 (VA 0x200BB0 / 0x200C0C)
# Widths [2], [4] and [21] are the exact float32 values of the eboot (0x442B3334, 0x442B3334,
# 0x44299999); the earlier decimals 684.8 / 684.8 / 678.4 were 1 ulp off in float32.
_WSIZE = [(716.8, 448.0), (716.8, 488.32), (684.800048828125, 448.0), (640.0, 488.32),
          (684.800048828125, 448.0),
          (640.0, 448.0), (710.4, 448.0), (896.0, 448.0), (729.6, 448.0), (729.6, 448.0),
          (646.4, 448.0), (640.0, 448.0), (640.0, 448.0), (646.4, 448.0), (704.0, 448.0),
          (640.0, 448.0), (832.0, 448.0), (716.8, 448.0), (716.8, 448.0), (710.4, 448.0),
          (710.4, 448.0), (678.3999633789062, 448.0), (710.4, 448.0)]
_XOFF = [0.0, 100.0, 120.0, 150.0, 20.0, 200.0, 250.0, 30.0, 40.0, 50.0, 500.0, 60.0, 70.0, 80.0,
         -100.0, -120.0, -150.0, -200.0, -250.0, -30.0, -300.0, -40.0, -50.0, -60.0, -70.0, -80.0,
         400.0]                                                            # F1106 -> VA 0x2005B0
_YOFF = [0.0, 10.0, 100.0, 15.0, 150.0, 20.0, 200.0, 220.0, 230.0, 240.0, 25.0, 250.0, 270.0,
         30.0, 300.0, 35.0, 50.0, 65.0, -10.0, -100.0, -15.0, -150.0, -170.0, -180.0, -190.0,
         -20.0, -200.0, -210.0, -220.0, -230.0, -30.0, -300.0, -40.0, -45.0, -50.0, -55.0,
         -60.0]                                                            # F1107 -> VA 0x200620
WINDOW_TABLE = _WT


def disp_child_for_slot(slot: int) -> int:
    """DISP_PARTS child that registration table VA 0x780C90 loads into texture ``slot``."""
    if 22 <= slot <= 24:
        return 11 + (slot - 22)
    if 25 <= slot <= 55:
        return slot - 3
    raise ValueError(f"slot {slot} is not a message-window slot")


@dataclass(frozen=True)
class WindowDraw:
    index: int                # F1105 (or F1109) value
    slot: int
    disp_child: int
    x_px: float               # top-left on the 1920x1080 screen
    y_px: float
    scale_x: float            # screen pixels per texel
    scale_y: float
    alpha: float
    bubble: bool


def window_for(index: int, *, opacity: int = 255, fade: float = 1.0, xoff_idx: int = 0,
               yoff_idx: int = 0) -> Optional[WindowDraw]:
    """Message-window picture drawn by the HUD (VA 0x540A5..0x54492; F822 == 1, F1104 == 0).

    index 0..2: full-screen 1920x1080 overlay (DISP_PARTS child 11 / 12 / 13) with alpha
    opacity / 255 * fade (config +0x10 'window opacity', default 255; fade = window fade
    VA 0xDF0730).  index 3..55: speech-bubble picture with alpha 0.784314 * fade.  Position =
    table (x, y) + F1106 / F1107 offsets in a 1280x720 space, drawn in the 640x448 canvas with
    the texel scale (w / 1920, h / 1080), i.e. 1 texel = 1 pixel except indices 33..55.
    """
    if not 0 <= index <= 0x37:
        return None
    slot, tx, ty = _WT[index]
    xo = _XOFF[xoff_idx] if 0 <= xoff_idx <= 0x1A else 0.0
    yo = _YOFF[yoff_idx] if 0 <= yoff_idx <= 0x24 else 0.0
    bubble = index >= 3
    cw, ch = _WSIZE[index - 0x21] if 0x21 <= index <= 0x37 else (640.0, 448.0)
    x_canvas = (tx + xo) * 640.0 / 1280.0
    y_canvas = (ty + yo) * 448.0 / 720.0
    a = (0.784314 if bubble else opacity / 255.0) * fade
    return WindowDraw(index, slot, disp_child_for_slot(slot), x_canvas * 3.0, y_canvas * 1080.0 / 448.0,
                      cw / 640.0, ch / 448.0, a, bubble)


def bubble_text_origin(index: int, xoff_idx: int = 0, yoff_idx: int = 0) -> tuple[float, float]:
    """Text origin of speech bubble ``index`` in the 640x360 text space (VA 0x546CF..0x558B8)."""
    _slot, tx, ty = _WT[index]
    dx, dy = _TOFF[index]
    xo = _XOFF[xoff_idx] if 0 <= xoff_idx <= 0x1A else 0.0
    yo = _YOFF[yoff_idx] * 0.9578 if 0 <= yoff_idx <= 0x24 else 0.0
    return (float(F32((tx + dx + xo) * 640.0 / 1280.0)),
            float(F32((ty + dy + yo) * 360.0 / 720.0)))


def last_line_offset(block: TextBlock) -> int:
    """Second output of VA 0xCAD10 as the HUD uses it: int(y of the last line in the block)."""
    if block.nlines <= 1:
        return 0
    acc = F32(0.0)
    for i in range(block.nlines - 1):
        acc = F32(acc + F32(F32(block.leading) + F32(block.line_height[i])))
    return int(acc)


def page_positions(blocks: Sequence[TextBlock], lang: int, *, blank_before: Sequence[int] = (),
                   scale_y: Sequence[float] = (), origin: Optional[tuple[float, float]] = None
                   ) -> list[tuple[float, float]]:
    """Origins of the messages of one full-screen page (HUD VA 0x54733..0x54C2F, 0x558BD..0x55A6B).

    Message j (0-based) is drawn at y_j = y0 + sum_{i<j}(last_line_offset_i + 12*s_i + 14)
    + sum_{i<=j} C_i * (12*s_i + 14), where C_i = ``blank_before[i]`` (script variable C847 as
    seen by message i; `_WKAD(C847,1)` after a message adds one empty line before the next one;
    values >= 0x21 mean C - 64, clamped to y >= 49.82 / 35.41) and s_i = ``scale_y[i]``
    (C836 / 24, default 1).  x = x0 for every message.
    """
    x0, y0 = origin if origin is not None else ((30.0, 35.0) if lang == 1 else (87.0, 49.0))
    out = []
    y = F32(y0)
    for j, b in enumerate(blocks):
        s = F32(scale_y[j] if j < len(scale_y) else 1.0)
        step = F32(F32(s * F32(12.0)) + F32(14.0))
        c = blank_before[j] if j < len(blank_before) else 0
        if c > 0:
            if c >= 0x21:
                y = F32(F32(F32(c - 64) * step) + y)
                floor_ = F32(35.411766) if lang == 1 else F32(49.823528)
                y = y if y >= floor_ else floor_
            else:
                y = F32(F32(F32(c) * step) + y)
        out.append((float(x0), float(y)))
        y = F32(F32(F32(F32(last_line_offset(b)) + F32(s * F32(12.0))) + F32(14.0)) + y)
    return out


# ---------------------------------------------------------------------------
# text space -> screen pixels, rendering
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class CanvasTransform:
    """Text space (units) -> 1920x1080 pixels: px = kx * x + ox, py = ky * y + oy; a glyph quad is
    w * sx * kx by h * sy * kgy pixels."""
    kx: float = 3.0
    ky: float = 3.0
    ox: float = 0.0
    oy: float = 0.0
    kgy: float = 3.0


ENGINE_TRANSFORM = CanvasTransform()
# Fitted on samples/textbox/prologue_{en,ja}_25s.png (pre-rendered movie, see textbox.md 9):
# line positions advance 3.115 px per unit vertically there, not 3.0.
PROLOGUE_TRANSFORM = CanvasTransform(kx=3.0, ky=3.1154, ox=0.0, oy=0.0, kgy=3.0)


def render_quads(quads: Sequence[Quad], font: Font, size: tuple[int, int] = (1920, 1080),
                 transform: CanvasTransform = ENGINE_TRANSFORM,
                 background: Optional["np.ndarray"] = None) -> "np.ndarray":
    """Composite glyph quads (straight alpha, bilinear) over ``background`` (H x W x 3 float 0..1
    or None = black).  Returns an H x W x 3 float32 image."""
    from PIL import Image
    W, H = size
    img = np.zeros((H, W, 3), np.float32) if background is None else background.astype(np.float32).copy()
    S = 4
    for q in quads:
        if q.alpha <= 0:
            continue
        page = font.page_alpha(q.tex)
        tw, th = q.u1 - q.u0 + 1, q.v1 - q.v0 + 1
        if tw <= 0 or th <= 0:
            continue
        a = page[q.v0:q.v0 + th, q.u0:q.u0 + tw]
        wpx = tw * q.sx * transform.kx
        hpx = th * q.sy * transform.kgy
        px = q.x * transform.kx + transform.ox
        py = q.y * transform.ky + transform.oy
        iw, ih = max(1, int(round(wpx * S))), max(1, int(round(hpx * S)))
        big = np.asarray(Image.fromarray(a).resize((iw, ih), Image.Resampling.BILINEAR), np.float32) / 255.0
        X0, Y0 = int(round(px * S)), int(round(py * S))
        bw = (X0 % S + iw + S - 1) // S * S
        bh = (Y0 % S + ih + S - 1) // S * S
        canvas = np.zeros((bh, bw), np.float32)
        canvas[Y0 % S:Y0 % S + ih, X0 % S:X0 % S + iw] = big
        small = canvas.reshape(bh // S, S, bw // S, S).mean((1, 3)) * q.alpha
        bx, by = X0 // S, Y0 // S
        x0, y0 = max(0, bx), max(0, by)
        x1, y1 = min(W, bx + small.shape[1]), min(H, by + small.shape[0])
        if x1 <= x0 or y1 <= y0:
            continue
        sub = small[y0 - by:y1 - by, x0 - bx:x1 - bx][..., None]
        col = np.array(q.rgb, np.float32) / 255.0
        img[y0:y1, x0:x1] = img[y0:y1, x0:x1] * (1.0 - sub) + col * sub
    return img


def render_page(messages: Sequence[Union[str, bytes]], lang: int, *, font: Optional[Font] = None,
                background: Optional["np.ndarray"] = None, overlay: Optional["np.ndarray"] = None,
                overlay_alpha: float = 1.0, blank_before: Sequence[int] = (),
                transform: CanvasTransform = ENGINE_TRANSFORM, draw_flags: int = 0x1020,
                params: Optional[BoxParams] = None) -> tuple["np.ndarray", list[TextBlock], list[tuple[float, float]]]:
    """Render one full-screen page: background, window overlay (RGBA, e.g. DISP_PARTS child 11)
    and the messages stacked like the HUD does.  Returns (image float32 HxWx3, blocks, origins)."""
    f = font if font is not None else Font(LANGS[lang])
    p = params or main_box_params(lang)
    blocks = [layout_message(m, lang, p, font=f, block=79 + j if j else 4) for j, m in enumerate(messages)]
    origins = page_positions(blocks, lang, blank_before=blank_before, origin=(p.x, p.y))
    img = np.zeros((1080, 1920, 3), np.float32) if background is None else background.astype(np.float32)
    if overlay is not None:
        a = overlay[..., 3:4].astype(np.float32) / 255.0 * overlay_alpha
        img = img * (1.0 - a) + overlay[..., :3].astype(np.float32) / 255.0 * a
    quads: list[Quad] = []
    for b, (ox, oy) in zip(blocks, origins):
        quads += block_quads(b, ox, oy, draw_flags=draw_flags, lang=lang)
    img = render_quads(quads, f, transform=transform, background=img)
    return img, blocks, origins


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def _main(argv: Optional[list[str]] = None) -> int:
    from .text import ScriptText

    ap = argparse.ArgumentParser(prog="python -m tsuki.textbox",
                                 description="ADV text box: engine-exact layout, page placement, renders")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p1 = sub.add_parser("layout", help="lay out script line(s) or --text and print the lines")
    p1.add_argument("lang", choices=LANGS[:4])
    p1.add_argument("ids", nargs="*", type=int)
    p1.add_argument("--text")
    p1.add_argument("--window", type=int, default=0, help="F1105 value (>= 3: speech bubble)")
    p1.add_argument("--glyphs", action="store_true", help="print every glyph record")
    p2 = sub.add_parser("render", help="render a full-screen page (messages stacked) to PNG")
    p2.add_argument("lang", choices=LANGS[:4])
    p2.add_argument("out")
    p2.add_argument("ids", nargs="+", type=int)
    p2.add_argument("--blank", default="", help="comma list: empty lines before each message (C847)")
    p3 = sub.add_parser("window", help="print the window picture / text origin of an F1105 index")
    p3.add_argument("index", type=int)
    a = ap.parse_args(argv)
    if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
        sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[union-attr]
    if a.cmd == "window":
        print(window_for(a.index))
        if a.index >= 3:
            print("text origin (640x360 units):", bubble_text_origin(a.index))
        return 0
    lang = LANGS.index(a.lang)
    st = ScriptText()
    font = Font(a.lang)
    if a.cmd == "layout":
        texts = [a.text] if a.text is not None else [st.get_line(a.lang, i) for i in a.ids]
        for t in texts:
            p = main_box_params(lang, window=a.window)
            b = layout_message(t, lang, p, font=font)
            print(f"{t!r}: {b.n} elements, {b.nlines} line(s), box width {b.box_width}")
            for k, ln in enumerate(break_lines(b)):
                s = "".join(r.glyph.text if r.glyph else "?" for r in ln)
                print(f"  line {k}: width {b.line_width[k]:.3f} height {b.line_height[k]:.3f}  {s!r}")
            if a.glyphs:
                for r in b.records:
                    print(f"    elem {r.index:4d} line {r.line} tex {r.tex:#x} rect ({r.x},{r.y},{r.w},{r.h}) "
                          f"dx {r.dx:.4f} dy {r.dy:.3f} s ({r.sx:.4f},{r.sy:.4f}) ctr {r.ctr} flags {r.flags:#x}")
        return 0
    blank = [int(x) for x in a.blank.split(",")] if a.blank else []
    from PIL import Image
    img, blocks, origins = render_page([st.get_line(a.lang, i) for i in a.ids], lang, font=font,
                                       blank_before=blank)
    Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8)).save(a.out)
    print(f"{a.out}: origins {origins}, lines {[b.nlines for b in blocks]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
