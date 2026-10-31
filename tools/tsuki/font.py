"""Fonts of TSUKIHIME -A piece of blue glass moon- (PS4): ft_<lang>.mrg + eboot glyph tables.

Format summary (details and evidence: docs/formats/fonts.md)

ft_{ja,en,zc,zt,ko}.mrg
    Standalone mrgd00 with 65 sections.  Section k is a nested image MZP that
    the font loader (eboot VA 0xC6980) uploads as texture slot 0x21 + k.
    Section 0 is a blank 256x256 page; sections 1..64 are 1024x1024 pages.
    Image MZP = 2 sections:
      s0 (1041 bytes): u16 w, h, tile_w, tile_h, tiles_x, tiles_y, type_a(=1), type_b(=1);
                       256 x RGBA8888 palette in PS2 "CSM1" order (index bits 3 and 4
                       swapped), alpha 0..0x80 = 0..1.0; 1 trailing byte (1 = page has
                       glyphs, 0 = blank page).
      s1: MZX0 (plain) -> w*h 8-bit palette indices, row-major, no swizzle.
    Every non-transparent palette entry is white; only alpha carries the glyph.
    A page is two halves: rows 0..511 hold the glyphs ("fill"), rows 512..1023
    hold the same glyphs dilated by a few pixels ("outline"), at +512 in y.
    Glyph cells are 73x73 px (14 columns x 7 rows = 98 per half) for the
    fixed grids; proportional glyphs use arbitrary rectangles inside the half.

eboot.bin (see tsuki.font_eboot)
    Code point -> (texture slot, x, y, w, h) tables, per language and style.

Rendering (engine rules, eboot VA 0xC6C20 / 0xC9xxx):
    pen starts at 0; for every glyph: draw_x = pen + adj;
    quad = atlas[y:y+h, x:x+w] scaled by (sx, sy); outline quad = same rect + 512 in y,
    drawn first in the outline colour, then the fill quad;
    pen = draw_x + (w - 6) * sx + spacing.

Markup (layout pass 2, VA 0xC76C0..0xC8DE1; see :meth:`Font.layout`):
    NUL ends the text; '@x' codes; '^' = line break; '<base|ruby>' only when the
    text language is not English (VA 0xC6D58 / 0xC7700: English draws '<' '|' '>'
    as ordinary glyphs).

Public API: :func:`render_text`, :class:`Font`, :class:`FontPages`.
CLI: ``python -m tsuki.font {pages,render,charset,info} ...``
"""
from __future__ import annotations

import argparse
import mmap
import struct
import sys
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Iterator, Optional, Sequence, Union

import numpy as np
from PIL import Image

from . import game_dir, image_dir
from .font_eboot import EbootFontTables, TAGS, pack_utf8
from .mzp import MzpArchive
from .mzx import decompress

__all__ = [
    "LANGS",
    "STYLE_NAMES",
    "CELL",
    "FIRST_SLOT",
    "OUTLINE_DY",
    "FontPage",
    "FontPages",
    "Glyph",
    "PlacedGlyph",
    "Font",
    "render_text",
    "substitute_redaction_frames",
    "unswizzle_csm1",
    "decode_page_header",
    "alpha_8bit",
    "markup_end",
    "MAX_LINE_INDEX",
]

# Engine language index = position in the ft file-name table at eboot VA 0x2D352C.
LANGS: tuple[str, ...] = ("ja", "en", "zc", "zt", "ko")

# Meaning of the style number (eboot global VA 0x178BE24, set by the text markup
# '@g' -> 1, '@z' -> 2, '@i' -> 0/1/2 -> 3/4/5 (English only, other languages -> 1),
# '@r' -> 3/4/5 -> 0/1/2 and anything else -> 0; text boxes with flag bit 20 start at 2).
STYLE_NAMES: dict[int, str] = {
    0: "main",            # ja: mincho grid + mincho ASCII page; en: sans
    1: "alt (@g)",        # ja: gothic; en: serif
    2: "system (@z)",     # ja: gothic (2nd set); en: compact sans
    3: "italic main (@i)",
    4: "italic alt",
    5: "italic system",
}

CELL = 73                 # glyph cell size of the fixed grids
GRID_COLS = 14
GRID_ROWS = 7
PER_PAGE = GRID_COLS * GRID_ROWS   # 98
OUTLINE_DY = 512          # outline copy of every glyph sits 512 px lower
FIRST_SLOT = 0x21         # texture slot of ft section 0 (eboot VA 0xC69F4)
PAGE_SIZE = 1024
SECTION_COUNT = 65
ADVANCE_TRIM = 6          # pen advance = (w - 6) * sx  (eboot VA 0xC7CFE / 0xC7D5D: (w-3)*sx - 3*sx)
FALLBACK_SLOT = 0x22      # page 1, cell (0,0) = blank (eboot VA 0x11A2D)

# halfwidth katakana special case (eboot VA 0x1127E, Japanese wide grid only)
_HWKANA_LO, _HWKANA_HI = 0xEFBDA1, 0xEFBE9F
# dash-join characters (eboot VA 0x11B80): key -> variant base
_DASH_BASE = {0xE296A0: 0, 0xE28095: 3, 0xE29480: 6}   # U+25A0, U+2015, U+2500


def unswizzle_csm1(i: int) -> int:
    """PS2 CSM1 palette order: swap bits 3 and 4 of the index."""
    return (i & 0xE7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1)


_CSM1 = np.array([unswizzle_csm1(i) for i in range(256)], dtype=np.intp)


def decode_page_header(hdr: bytes) -> tuple[tuple[int, ...], np.ndarray, int]:
    """Section 0 of a font page -> (8 header u16, 256x4 palette in pixel-index order, flag byte)."""
    if len(hdr) < 16 + 1024:
        raise ValueError(f"font page header too short ({len(hdr)} bytes)")
    fields = struct.unpack_from("<8H", hdr, 0)
    pal = np.frombuffer(hdr[16:16 + 1024], dtype=np.uint8).reshape(256, 4)[_CSM1]
    flag = hdr[1040] if len(hdr) > 1040 else -1
    return fields, pal, flag


def alpha_8bit(a: np.ndarray) -> np.ndarray:
    """PS2-style alpha -> 0..255 exactly like the engine's in-place palette conversion
    (eboot VA 0xD90B0..0xD90CE): 0x80 -> 0xFF, anything else -> (2*a) & 0xFF.
    Font pages only use 0x00..0x7F (-> 0..0xFE)."""
    a16 = a.astype(np.uint16)
    return np.where(a16 == 0x80, 0xFF, (a16 * 2) & 0xFF).astype(np.uint8)


# ---------------------------------------------------------------------------
# atlas pages
# ---------------------------------------------------------------------------

@dataclass
class FontPage:
    section: int
    width: int
    height: int
    header: tuple[int, ...]
    flag: int
    palette: np.ndarray          # 256 x RGBA (PS2 alpha), pixel-index order
    indices: np.ndarray          # h x w uint8

    @property
    def slot(self) -> int:
        return FIRST_SLOT + self.section

    @property
    def alpha(self) -> np.ndarray:
        """h x w uint8 coverage (0..255)."""
        return alpha_8bit(self.palette[:, 3])[self.indices]

    def rgba(self) -> np.ndarray:
        pal = self.palette.copy()
        pal[:, 3] = alpha_8bit(pal[:, 3])
        return pal[self.indices]

    def image(self, background: Optional[tuple[int, int, int]] = None) -> Image.Image:
        im = Image.fromarray(self.rgba(), "RGBA")
        if background is None:
            return im
        bg = Image.new("RGBA", im.size, background + (255,))
        return Image.alpha_composite(bg, im).convert("RGB")


class FontPages:
    """Lazy reader for one ft_<lang>.mrg (65 texture pages)."""

    def __init__(self, path: Union[str, Path]) -> None:
        self.path = Path(path)
        self._f = open(self.path, "rb")
        self._mm = mmap.mmap(self._f.fileno(), 0, access=mmap.ACCESS_READ)
        self.archive = MzpArchive(self._mm)
        if len(self.archive) != SECTION_COUNT:
            raise ValueError(f"{self.path.name}: {len(self.archive)} sections, expected {SECTION_COUNT}")
        self._cache: dict[int, FontPage] = {}

    @classmethod
    def for_lang(cls, lang: str, game: Optional[Path] = None) -> "FontPages":
        img = (game / "image") if game else image_dir()
        if img is None:
            raise FileNotFoundError("game directory not found (set TSUKI_GAME_DIR)")
        return cls(img / f"ft_{lang}.mrg")

    def close(self) -> None:
        self._cache.clear()
        self.archive = None  # type: ignore[assignment]
        self._mm.close()
        self._f.close()

    def __enter__(self) -> "FontPages":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    def __len__(self) -> int:
        return SECTION_COUNT

    def page(self, section: int) -> FontPage:
        p = self._cache.get(section)
        if p is not None:
            return p
        child = self.archive.child(section)
        if len(child) != 2:
            raise ValueError(f"page {section}: {len(child)} sections, expected 2")
        fields, pal, flag = decode_page_header(child.read(0))
        w, h, tw, th, tx, ty = fields[:6]
        if (tw, th, tx, ty) != (w, h, 1, 1):
            raise ValueError(f"page {section}: unexpected tiling {fields}")
        raw = decompress(child.read(1))
        if len(raw) != w * h:
            raise ValueError(f"page {section}: {len(raw)} pixel bytes for {w}x{h}")
        idx = np.frombuffer(raw, dtype=np.uint8).reshape(h, w)
        p = FontPage(section, w, h, fields, flag, pal, idx)
        self._cache[section] = p
        return p

    def page_for_slot(self, slot: int) -> FontPage:
        sec = slot - FIRST_SLOT
        if not 0 <= sec < SECTION_COUNT:
            raise ValueError(f"texture slot 0x{slot:x} is not a font page")
        return self.page(sec)


# ---------------------------------------------------------------------------
# glyph lookup (mirrors eboot VA 0x10810 / 0x11150 / 0x11750 / 0x11B80 / 0x11ED0)
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Glyph:
    text: str                 # source text consumed (one character or a [tag])
    consumed: int             # UTF-8 bytes consumed
    key: int                  # packed UTF-8 key (or tag index for tags)
    tex: int                  # texture slot (page = tex - 0x21)
    x: int
    y: int
    w: int
    h: int
    adj: float                # pen/draw x adjustment (already multiplied by scale)
    # 'wide' | 'record' | 'tag' | 'dash' | 'fallback', or for consumed == 0:
    # 'nul' (string ends here: engine tex 0xFFFF, rect (0,0,73,73)) and
    # 'invalid' (invalid UTF-8 lead byte: the engine returns 0 and leaves its
    # outputs untouched; tsuki fills tex 0xFFFF and an empty rect)
    source: str
    index: Optional[int] = None
    table: Optional[str] = None
    in_range: bool = True     # False: record index past its table (engine reads neighbouring data)

    @property
    def page(self) -> int:
        return self.tex - FIRST_SLOT


@dataclass(frozen=True)
class PlacedGlyph:
    glyph: Glyph
    x: float                  # draw position (top-left of the quad), pixels
    y: float
    sx: float
    sy: float


def _decode_utf8_key(b: Union[bytes, bytearray], pos: int) -> tuple[int, int]:
    """Engine UTF-8 decode (VA 0x11B00 / 0x11750): returns (key, length); length 0 = invalid lead."""
    c = b[pos]
    if c < 0x80:
        return c, 1
    if c & 0xE0 == 0xC0:
        n = 2
    elif c & 0xF0 == 0xE0:
        n = 3
    elif c & 0xF8 == 0xF0:
        n = 4
    else:
        return c, 0
    seg = bytes(b[pos:pos + n])
    if len(seg) < n:
        seg = seg + b"\0" * (n - len(seg))
    return int.from_bytes(seg, "big"), n


def _key3_at(b: Union[bytes, bytearray], pos: Optional[int]) -> int:
    """Dash-join key read of VA 0x11BDD / 0x11C11: the 3-byte key at ``pos`` if the
    byte there is a 3-byte UTF-8 lead (0xE0..0xEF), else 0.  ``None`` = NULL pointer.
    Bytes past the end read as NUL (the engine's strings are NUL-terminated)."""
    if pos is None or pos >= len(b) or b[pos] & 0xF0 != 0xE0:
        return 0
    seg = bytes(b[pos:pos + 3]).ljust(3, b"\0")
    return int.from_bytes(seg, "big")


MAX_LINE_INDEX = 31       # '^' on line 31 ends the text (VA 0xC7FCD: cmp esi, 0x1e; jg end)
_DIGIT_CODES = frozenset(b"acfhw")


def markup_end(b: Union[bytes, bytearray], pos: int) -> int:
    """End offset of the '@' code at ``b[pos]`` under the engine's layout pass 2.

    Jump table at VA 0x2D32C0 (index = byte - 0x31, '1'..'z'; everything else
    takes the default handler at VA 0xC8625):
      * '@a' '@c' '@f' '@h' '@w': 3 bytes (one digit parameter, not checked);
      * '@s': parameter runs to ',' / ')' / NUL, which is consumed; at most
        256 bytes are examined (VA 0xC8836);
      * '@x' '@y': 0..3 decimal digits follow;
      * '@1' '@2' and the no-parameter codes b g i k l o p r t u z T: 2 bytes;
      * any other byte: if a '(' follows, the group up to the first ')' within
        the next 255 bytes is skipped as well (no ')' / NUL first: 2 bytes).
    The end of ``b`` counts as the NUL terminator.
    """
    n = len(b)
    op = b[pos + 1] if pos + 1 < n else 0
    j = pos + 2
    if op in _DIGIT_CODES:
        return min(j + 1, n)
    if op == 0x73:                                     # 's'
        k = j
        while k < n and k - j < 256 and b[k] not in (0x2C, 0x29, 0x00):
            k += 1
        if k - j >= 256:
            return j + 256
        return min(k + 1, n)
    if op in (0x78, 0x79):                             # 'x', 'y'
        k = j
        while k < n and k - j < 3 and 0x30 <= b[k] <= 0x39:
            k += 1
        return k
    if op in b"12bgiklioprtuzT":
        return min(j, n)
    if j < n and b[j] == 0x28:                         # '(' group
        for k in range(j + 1, min(n, j + 1 + 255)):
            if b[k] == 0x00:
                break
            if b[k] == 0x29:
                return k + 1
    return min(j, n)


class Font:
    """Glyph lookup + layout + rasterisation for one language."""

    def __init__(self, lang: str, *, game: Optional[Path] = None,
                 tables: Optional[EbootFontTables] = None,
                 pages: Optional[FontPages] = None, check: bool = True) -> None:
        if lang not in LANGS:
            raise ValueError(f"unknown language {lang!r}; expected one of {LANGS}")
        self.lang = lang
        self.lang_index = LANGS.index(lang)
        g = game or game_dir()
        if (tables is None or pages is None) and g is None:
            raise FileNotFoundError("game directory not found (set TSUKI_GAME_DIR)")
        if tables is None:
            assert g is not None
            tables = EbootFontTables.open(g / "eboot.bin")
            if check:
                issues = tables.verify()
                if issues:
                    raise ValueError("eboot.bin does not match the 01.01 font tables: " + "; ".join(issues))
        self.tables = tables
        self.pages = pages if pages is not None else FontPages.for_lang(lang, g)
        self._alpha: dict[int, np.ndarray] = {}

    def close(self) -> None:
        self.pages.close()

    def __enter__(self) -> "Font":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    # -- tables ---------------------------------------------------------------
    def lookup_table(self, key: int, style: int, lang: Optional[int] = None) -> Optional[str]:
        """Name of the 16-byte table the engine searches (VA 0x10810)."""
        lg = self.lang_index if lang is None else lang
        name = LANGS[lg] if 0 <= lg < 5 else None
        if name is None:
            return None
        if style == 0:
            return f"{name}_wide" if key >= 0x10000 else "narrow"
        if style == 2:
            return f"{name}_s2"
        return f"{name}_s1"     # styles 1, 3, 4, 5 (and anything else)

    def lookup(self, key: int, style: int, lang: Optional[int] = None) -> Optional[int]:
        t = self.lookup_table(key, style, lang)
        return None if t is None else self.tables.lookups[t].get(key)

    @staticmethod
    def record_table(lang: int, style: int) -> str:
        """Name of the 10-byte record table for (lang, style) (VA 0x112E7 jump table)."""
        name = LANGS[lang]
        if lang == 0:
            return "ja_s0" if style == 0 else ("ja_s2" if style == 2 else "ja_s1")
        if lang == 1:
            if style == 0:
                return "en_s0"
            return {2: "en_s2", 3: "en_s3", 4: "en_s4", 5: "en_s5"}.get(style, "en_s1")
        if lang == 3:
            return "zt_s0" if style == 0 else "zt_s1"
        return f"{name}_s0" if style == 0 else (f"{name}_s2" if style == 2 else f"{name}_s1")

    # -- single glyphs ----------------------------------------------------------
    def tag_glyph(self, k: int, text: str, consumed: int, kind: int, scale: float) -> Glyph:
        """``[tag]`` glyph k (0..38) (VA 0x118B2 / 0x11A61; same rule as the lang-5 path at 0x112C1)."""
        adj_far = scale * (5.0 if kind < 2 else -0.5)       # floats at VA 0x177258 / 0x17725C
        if k >= 38:
            return Glyph(text, consumed, k, 0x61, 0, 219, 68, 73, adj_far, "tag", k)
        if k >= 7:
            j = k - 7
            return Glyph(text, consumed, k, 0x5F, (j % 14) * 68, (j // 14) * 73, 68, 73, adj_far, "tag", k)
        if k == 4:
            return Glyph(text, consumed, k, 0x5E, 307, 0, 46, 73, 0.0, "tag", k)
        return Glyph(text, consumed, k, 0x5E, 73 * k, 0, 73, 73, 0.0, "tag", k)

    def get_glyph(self, key: int, style: int, *, kind: int = 0, scale: float = 1.0,
                  text: str = "", consumed: int = 0) -> Optional[Glyph]:
        """Mirror of VA 0x11150 for one packed key; None = not found (engine returns -1)."""
        lang = self.lang_index
        idx = self.lookup(key, style)
        if style != 0 and idx is None:          # fall back to the main style
            idx = self.lookup(key, 0)
            style = 0
        if idx is None:
            return None
        if key >= 0x10000 and style == 0:       # fixed 14x7 grid of 73 px cells
            tex = self.tables.wide_base[lang] + idx // PER_PAGE
            x = (idx % GRID_COLS) * CELL
            y = (idx % PER_PAGE // GRID_COLS) * CELL
            w = CELL
            adj = 0.0
            if lang == 0 and _HWKANA_LO <= key <= _HWKANA_HI:
                x += 15
                w = 42
                if key & ~1 == 0xEFBE9E:        # U+FF9E / U+FF9F (VA 0x11298)
                    adj = -2.0 * scale
            return Glyph(text, consumed, key, tex, x, y, w, CELL, adj, "wide", idx,
                         self.lookup_table(key, 0))
        rtab = self.record_table(lang, style)
        rt = self.tables.records[rtab]
        r = rt.get(idx)
        adj = 0.0
        if lang == 1 and style in (3, 4, 5):    # italic: keep the upright advance (VA 0x11531)
            w2: Optional[int] = None
            if style == 3:
                i2 = self.tables.lookups["en_wide" if key >= 0x10000 else "narrow"].get(key)
                if i2 is not None:
                    w2 = CELL if key > 0xFFFF else self.tables.records["en_s0"].get(i2).w
            elif style == 4:
                i2 = self.tables.lookups["en_s1"].get(key)
                if i2 is not None:
                    w2 = self.tables.records["en_s1"].get(i2).w
            else:
                i2 = self.tables.lookups["en_s2"].get(key)
                if i2 is not None:
                    w2 = self.tables.records["en_s2"].get(i2).w
            if w2 is not None:
                adj = float(w2 - r.w) * scale
        return Glyph(text, consumed, key, r.tex, r.x, r.y, r.w, r.h, adj, "record", idx, rtab,
                     rt.in_range(idx))

    def char_glyph(self, b: Union[bytes, bytearray], pos: int, style: int, *, kind: int = 0,
                   scale: float = 1.0) -> Glyph:
        """Mirror of VA 0x11750: one character (or [tag]) at byte offset ``pos``.

        Like the engine it returns ``consumed == 0`` for a NUL byte (or the end
        of ``b``: source 'nul', tex 0xFFFF, rect (0, 0, 73, 73), VA 0x117FA) and
        for an invalid lead byte 0x80..0xBF / 0xF8..0xFF (source 'invalid'; the
        engine leaves its outputs untouched, VA 0x118ED).  Callers must stop or
        skip on ``consumed == 0``.
        """
        if pos >= len(b) or b[pos] == 0:
            return Glyph("", 0, 0, 0xFFFF, 0, 0, CELL, CELL, 0.0, "nul")
        if b[pos] == 0x5B:                          # '[' -> tag table
            for k, tag in enumerate(TAGS):
                tb = tag.encode("ascii")
                if bytes(b[pos:pos + len(tb)]) == tb:
                    return self.tag_glyph(k, tag, len(tb), kind, scale)
        key, n = _decode_utf8_key(b, pos)
        if n == 0:
            return Glyph("", 0, key, 0xFFFF, 0, 0, 0, 0, 0.0, "invalid")
        text = bytes(b[pos:pos + n]).decode("utf-8", errors="replace")
        g = self.get_glyph(key, style, kind=kind, scale=scale, text=text, consumed=n)
        if g is not None:
            return g
        w = 42 if n <= 2 else CELL                  # VA 0x11A28 / 0x11A2D
        return Glyph(text, n, key, FALLBACK_SLOT, 0, 0, w, CELL, 0.0, "fallback")

    def dash_variant_at(self, b: Union[bytes, bytearray], pos: int, prev_pos: Optional[int], *,
                        line_start: bool, line_end: bool = False, flag_b: bool = False) -> int:
        """Exact mirror of VA 0x11B80(cur, prev, line_start, line_end, flag_b) on raw bytes.

        ``pos`` = the current element; ``prev_pos`` = start of the previous
        *parse element* of the layout loop (VA 0xC76A4 ``mov rsi, rbx``; NULL =
        None for the first element), which may be a markup code, '^', '<', '|'
        or '>'.  The engine compares 3-byte keys only: prev = 3 bytes at
        ``prev_pos``, next = the 3 bytes right after the current character
        (``pos + 3``, VA 0x11C11), each 0 unless its first byte is a 3-byte
        UTF-8 lead.  So '―@r―' does not join, '――' does.
        """
        return self.dash_variant(_key3_at(b, pos), _key3_at(b, prev_pos), _key3_at(b, pos + 3),
                                 line_start=line_start, line_end=line_end, flag_b=flag_b)

    def dash_variant(self, key: int, prev_key: int, next_key: int, *, line_start: bool,
                     line_end: bool = False, flag_b: bool = False) -> int:
        """Mirror of VA 0x11B80 on keys: joined-dash piece 0..8, or -1 (draw the normal glyph).

        ``prev_key`` / ``next_key`` must follow the engine's reads (see
        :meth:`dash_variant_at`): the 3-byte key of the neighbouring element
        or 0.  ``line_start`` = pen x <= 0; ``line_end`` = the right-margin
        test of the caller (VA 0xC7824..0xC78A0, False without automatic wrapping).
        """
        base = _DASH_BASE.get(key)
        if base is None:
            return -1
        has_prev = (prev_key == key) and not line_start
        has_next = (next_key == key) and not line_end
        if has_prev and has_next:
            return base + 1
        if has_prev:
            return base + (0 if flag_b else 2)
        if has_next:
            return base + (2 if flag_b else 0)
        return -1

    def dash_glyph(self, variant: int, style: int, *, kind: int = 0, scale: float = 1.0,
                   text: str = "", consumed: int = 3) -> Glyph:
        """Mirror of VA 0x11ED0: rectangle of a joined-dash piece on page 61 (slot 0x5E)."""
        lang = self.lang_index
        if lang == 0:
            y = 146 if style == 2 else 73
        elif lang == 2:
            y = 219
        elif lang == 3:
            y = 292
        else:
            y = 73
        xb = 511 if lang in (1, 4) and style in (3, 4, 5) else 0
        part = variant % 3
        x = xb + (variant // 3) * 146 + (0, 41, 78)[part]
        w = (73, 68, 68)[part]
        adj = 0.0 if part == 0 else scale * (5.0 if kind < 2 else -0.5)
        return Glyph(text, consumed, variant, 0x5E, x, y, w, 73, adj, "dash", variant)

    # -- layout -----------------------------------------------------------------
    @property
    def ruby_enabled(self) -> bool:
        """Engine rule: ruby markup is parsed unless the text language is English
        (VA 0xC6D58 stores get_language() in [rbp-0xC4]; 0xC7700 / 0xC82D2 skip the
        '<' and '>' handlers when it is 1)."""
        return self.lang_index != 1

    def layout(self, text: str, *, style: int = 0, scale: float = 1.0,
               scale_y: Optional[float] = None, spacing: float = 0.0,
               line_pitch: Optional[float] = None, kind: int = 0,
               markup: bool = False, join_dashes: bool = True,
               ruby: Optional[bool] = None, show_ruby: bool = True,
               ruby_scale: float = 0.5, engine_oob: bool = False) -> list[PlacedGlyph]:
        """Place glyphs for ``text``, following the engine's layout pass 2 (VA 0xC76C0..0xC8DE1).

        Element by element (each element is one glyph or one control):

        * NUL ends the text (VA 0xC76F7).
        * ``markup=True``: '@x' codes are consumed as in :func:`markup_end`;
          '@g' '@z' '@i' '@r' switch the style (VA 0xC877E / 0xC8A4A / 0xC872B /
          0xC87B9), '@b' sets the dash flag_b and '@r' clears it, '@1X' / '@2X'
          overwrite X with a literal '^' / ')'; '^' is a forced line break
          (VA 0xC76EA -> 0xC7F3D), at most 31 breaks (a '^' on line 31 ends the
          text).  The other codes (colour, scale, wrap, typewriter) do not
          change glyph choice and are skipped.  ``markup=False`` draws '@' and
          '^' as glyphs.
        * Ruby ``<base|ruby>``: ``ruby=None`` applies the engine rule (parsed
          for ja/zc/zt/ko, **not for English**, where '<' '|' '>' are glyphs);
          True / False force it.  '<' sets kind 1, the first '|' kind 2 (the
          reading), further '|' are swallowed, '>' returns to kind 0 and is
          swallowed even outside a group.  ``show_ruby=False`` mirrors caller
          flag 0x2000: the reading is skipped up to and including '>'.  Ruby
          glyphs are centred over the base at ``ruby_scale`` (tsuki's choice:
          the engine's ruby placement at VA 0xC7BA1 is not traced).
        * Joined dashes: :meth:`dash_variant_at` with the engine's raw-pointer
          prev/next rule (``line_end`` False: no automatic wrapping here).
        * tsuki conveniences (not engine behaviour): '\\n' starts a new line,
          '\\r' is ignored.

        A record index past its table (``Glyph.in_range`` False: ja style 0
        U+00B0/U+00E2/U+00E4/U+00EA/U+00EB, most of the Korean stub) is replaced
        by the blank fallback glyph unless ``engine_oob`` asks for the engine's
        out-of-bounds read.  An invalid UTF-8 lead byte (only reachable through
        '@1' before a multi-byte character) raises ValueError: the engine would
        consume 0 bytes and stall.
        """
        sx = scale
        sy = scale if scale_y is None else scale_y
        ruby_on = self.ruby_enabled if ruby is None else ruby
        b = bytearray(text.encode("utf-8"))          # '@1' / '@2' write into it, like the engine
        rs = sx * ruby_scale
        band = (CELL - 4) * rs if (ruby_on and show_ruby and 0x3C in b) else 0.0
        pitch = line_pitch if line_pitch is not None else (CELL - 4) * sy + 12 * sy + band
        out: list[PlacedGlyph] = []
        pos = 0
        prev_pos: Optional[int] = None    # start of the previous element (VA 0xC76A4; NULL at first)
        pen = 0.0
        line = 0
        cur_style = style
        cur_kind = kind
        flag_b = False
        caret_literal = False             # byte VA 0x178CF10, set by '@1'
        ruby_x0 = ruby_x1 = 0.0
        pending: list[Glyph] = []         # ruby glyphs of the open group

        def flush_ruby() -> None:
            if pending:
                out.extend(self._place_ruby(pending, ruby_x0, ruby_x1, line * pitch, rs))
                pending.clear()

        while pos < len(b):
            c = b[pos]
            start = pos
            if c == 0:
                break
            nxt: Optional[int] = None          # set for control elements
            if c == 0x0A:                       # tsuki convenience
                line += 1
                pen = 0.0
                nxt = pos + 1
            elif c == 0x0D:
                nxt = pos + 1
            elif markup and c == 0x40:          # '@'
                if pos + 1 >= len(b):
                    break                        # engine would read past the NUL terminator
                op = chr(b[pos + 1])
                if op == "g":
                    cur_style = 1
                elif op == "z":
                    cur_style = 2
                elif op == "i":
                    if self.lang_index == 1:
                        cur_style = {1: 4, 2: 5}.get(cur_style, 3)
                    else:
                        cur_style = 1
                elif op == "r":
                    cur_style = {4: 1, 5: 2}.get(cur_style, 0)
                    flag_b = False
                elif op == "b":
                    flag_b = True
                elif op in "12" and pos + 2 < len(b):
                    b[pos + 2] = 0x5E if op == "1" else 0x29
                    if op == "1":
                        caret_literal = True
                nxt = markup_end(b, pos)
            elif markup and c == 0x5E and not caret_literal:   # '^' forced line break
                if line >= MAX_LINE_INDEX:
                    break
                line += 1
                pen = 0.0
                nxt = pos + 1
            elif ruby_on and c == 0x3C:         # '<' ruby base starts
                flush_ruby()
                cur_kind = 1
                ruby_x0 = pen
                nxt = pos + 1
            elif c == 0x7C and cur_kind > 0:    # '|' (VA 0xC7EE7: any language, kind > 0)
                nxt = pos + 1
                if cur_kind == 1:
                    ruby_x1 = pen
                    cur_kind = 2
                    if not show_ruby:           # caller flag 0x2000: skip the reading
                        while nxt < len(b) and b[nxt] != 0:
                            nxt += 1
                            if b[nxt - 1] == 0x3E:
                                break
                        cur_kind = 0
            elif ruby_on and c == 0x3E:         # '>' (swallowed even outside a group)
                cur_kind = 0
                flush_ruby()
                nxt = pos + 1
            if nxt is not None:
                prev_pos = start
                pos = nxt
                continue
            if c == 0x5E:
                caret_literal = False           # the literal '^' is drawn as a glyph
            gs = rs if cur_kind >= 2 else sx
            g: Optional[Glyph] = None
            if join_dashes:
                v = self.dash_variant_at(b, pos, prev_pos, line_start=pen <= 0.0, flag_b=flag_b)
                if v >= 0:
                    g = self.dash_glyph(v, cur_style, kind=cur_kind, scale=gs,
                                        text=bytes(b[pos:pos + 3]).decode("utf-8"), consumed=3)
            if g is None:
                g = self.char_glyph(b, pos, cur_style, kind=cur_kind, scale=gs)
                if g.consumed == 0:
                    raise ValueError(f"invalid UTF-8 lead byte 0x{b[pos]:02x} at offset {pos}: "
                                     "the engine would consume 0 bytes here")
                if not g.in_range and not engine_oob:
                    g = replace(g, tex=FALLBACK_SLOT, x=0, y=0, w=42 if g.consumed <= 2 else CELL,
                                h=CELL, adj=0.0, source="fallback")
            if cur_kind >= 2:
                pending.append(g)
            else:
                draw_x = pen + g.adj
                out.append(PlacedGlyph(g, draw_x, line * pitch + band, sx, sy))
                pen = draw_x + (g.w - ADVANCE_TRIM) * sx + spacing
            prev_pos = start
            pos += g.consumed
        flush_ruby()
        return out

    def _place_ruby(self, glyphs: Sequence[Glyph], x0: float, x1: float, y: float,
                    rs: float) -> list[PlacedGlyph]:
        """Ruby (furigana) glyphs centred over [x0, x1) in the band above the line.

        Placement is this library's choice; the engine's own ruby placement
        (kind 2 path at VA 0xC7BA1) was not traced.
        """
        width = sum((g.w - ADVANCE_TRIM) * rs + g.adj for g in glyphs) + ADVANCE_TRIM * rs
        pen = x0 + ((x1 - x0) - width) / 2.0
        placed = []
        for g in glyphs:
            dx = pen + g.adj
            placed.append(PlacedGlyph(g, dx, y, rs, rs))
            pen = dx + (g.w - ADVANCE_TRIM) * rs
        return placed

    # -- rasterisation ------------------------------------------------------------
    def page_alpha(self, slot: int) -> np.ndarray:
        a = self._alpha.get(slot)
        if a is None:
            a = self.pages.page_for_slot(slot).alpha
            self._alpha[slot] = a
        return a

    def glyph_alpha(self, g: Glyph, outline: bool = False) -> np.ndarray:
        """h x w coverage of a glyph (or of its outline copy)."""
        a = self.page_alpha(g.tex)
        y0 = g.y + (OUTLINE_DY if outline else 0)
        w = max(0, g.w)
        crop = a[y0:y0 + g.h, g.x:g.x + w]
        if crop.shape != (g.h, w):          # clamp at the page edge like a texture sampler would
            pad = np.zeros((g.h, w), dtype=np.uint8)
            pad[:crop.shape[0], :crop.shape[1]] = crop
            crop = pad
        return crop

    def render(self, placed: Sequence[PlacedGlyph], *,
               color: tuple[int, int, int] = (255, 255, 255),
               outline_color: Optional[tuple[int, int, int]] = (0, 0, 0),
               outline_alpha: float = 1.0,
               background: Optional[tuple[int, int, int]] = None,
               padding: int = 8, redaction_frames: bool = True) -> Image.Image:
        """Rasterise placed glyphs: per glyph, outline copy first, then the fill (engine order).

        ``redaction_frames`` applies the draw-time picture substitution of the
        [berNN]/[zap00] glyphs (see :func:`substitute_redaction_frames`).
        """
        placed = [p for p in placed
                  if 0 < p.glyph.w <= PAGE_SIZE and 0 < p.glyph.h <= OUTLINE_DY
                  and FIRST_SLOT <= p.glyph.tex < FIRST_SLOT + SECTION_COUNT]
        if not placed:
            return Image.new("RGBA", (2 * padding + 1, 2 * padding + 1), (0, 0, 0, 0))
        min_x = min(p.x for p in placed)
        max_x = max(p.x + max(0, p.glyph.w) * p.sx for p in placed)
        max_y = max(p.y + p.glyph.h * p.sy for p in placed)
        ox = padding - min(0.0, min_x)
        W = int(np.ceil(max_x + ox + padding))
        H = int(np.ceil(max_y + 2 * padding))
        rgb = np.zeros((H, W, 3), dtype=np.float32)
        acc = np.zeros((H, W), dtype=np.float32)
        if background is not None:
            rgb[:] = np.array(background, dtype=np.float32) / 255.0
            acc[:] = 1.0
        layers: list[tuple[bool, tuple[int, int, int], float]] = []
        if outline_color is not None and outline_alpha > 0:
            layers.append((True, outline_color, outline_alpha))
        layers.append((False, color, 1.0))
        if redaction_frames:
            placed = substitute_redaction_frames(placed)
        for p in placed:
            g = p.glyph
            if g.w <= 0 or not FIRST_SLOT <= g.tex < FIRST_SLOT + SECTION_COUNT:
                continue        # garbage record (engine reads past a table) -> nothing sensible to draw
            dw = max(1, int(round(g.w * p.sx)))
            dh = max(1, int(round(g.h * p.sy)))
            x0 = int(round(p.x + ox))
            y0 = int(round(p.y + padding))
            for is_outline, col, opacity in layers:
                cov = self.glyph_alpha(g, outline=is_outline)
                if (dw, dh) != (g.w, g.h):
                    cov = np.asarray(Image.fromarray(cov).resize((dw, dh), Image.Resampling.BILINEAR))
                a = cov.astype(np.float32) / 255.0 * opacity
                ys, xs = slice(max(0, y0), min(H, y0 + dh)), slice(max(0, x0), min(W, x0 + dw))
                sub = a[ys.start - y0:ys.stop - y0, xs.start - x0:xs.stop - x0]
                c = np.array(col, dtype=np.float32) / 255.0
                rgb[ys, xs] = rgb[ys, xs] * (1.0 - sub[..., None]) + c * sub[..., None]
                acc[ys, xs] = acc[ys, xs] * (1.0 - sub) + sub
        # rgb holds premultiplied colour; convert to straight alpha
        out = np.zeros((H, W, 4), dtype=np.uint8)
        nz = acc > 1e-6
        straight = np.zeros_like(rgb)
        straight[nz] = rgb[nz] / acc[nz][:, None]
        out[..., :3] = np.clip(straight * 255.0 + 0.5, 0, 255).astype(np.uint8)
        out[..., 3] = np.clip(acc * 255.0 + 0.5, 0, 255).astype(np.uint8)
        im = Image.fromarray(out, "RGBA")
        return im.convert("RGB") if background is not None else im

    # -- charset ------------------------------------------------------------------
    def charset(self, style: int) -> list[tuple[str, Glyph]]:
        """Every character the engine can find for this language and style (key order)."""
        names = []
        if style == 0:
            names = ["narrow", f"{self.lang}_wide"]
        else:
            names = [self.lookup_table(0x41, style) or ""]
        out = []
        for n in names:
            for ch, _idx in self.tables.lookups[n].chars():
                g = self.get_glyph(pack_utf8(ch), style, text=ch, consumed=len(ch.encode()))
                if g is not None:
                    out.append((ch, g))
        return out


# ---------------------------------------------------------------------------
# convenience
# ---------------------------------------------------------------------------

REDACTION_MAX = 21        # eboot VA 0xC9451: count clamped to 0x15


def substitute_redaction_frames(placed: Sequence[PlacedGlyph]) -> list[PlacedGlyph]:
    """Draw-time picture choice for [berNN] / [zap00] glyphs (eboot VA 0xC9401..0xC9732).

    The tag only fixes the layout box (68x73).  When drawing a text block the
    engine counts the special glyphs (texture slot >= 0x5F) already revealed,
    n = min(count, 21), and gives the i-th special glyph (i counted over the
    block, modulo 21):
      * [zap00] (slot 0x61, y >= 219): page 64, x = (i % 14) * 68, y = 219;
      * a [ber] glyph: cell k = n*(n-1)/2 + i of the 68x73 grid that runs over
        pages 62, 63, 64 (98 cells per page): slot 0x5F + k // 98,
        x = (k % 14) * 68, y = (k % 98 // 14) * 73.
    So a run of n bars shows the n-segment bar pre-drawn at cells T(n-1).. of
    the grid (T = triangular numbers), not the picture of its own tag number.
    Here every glyph counts as revealed (a fully displayed text).
    """
    specials = [p for p in placed if p.glyph.tex >= 0x5F and p.glyph.source == "tag"]
    n = min(len(specials), REDACTION_MAX)
    base = n * (n - 1) // 2 if n >= 2 else 0
    out: list[PlacedGlyph] = []
    i = 0
    for p in placed:
        g = p.glyph
        if g.tex >= 0x5F and g.source == "tag":
            if g.tex >= 0x61 and g.y >= 219:
                ng = replace(g, tex=0x61, x=(i % 14) * 68, y=219, w=68, h=73)
            else:
                k = base + i
                ng = replace(g, tex=0x5F + k // 98, x=(k % 14) * 68, y=(k % 98 // 14) * 73, w=68, h=73)
            out.append(replace(p, glyph=ng))
            i = (i + 1) % REDACTION_MAX
        else:
            out.append(p)
    return out


_FONTS: dict[tuple[str, str], Font] = {}


def _font(lang: str, game: Optional[Path]) -> Font:
    g = game or game_dir()
    if g is None:
        raise FileNotFoundError("game directory not found (set TSUKI_GAME_DIR)")
    k = (lang, str(g))
    f = _FONTS.get(k)
    if f is None:
        f = Font(lang, game=g)
        _FONTS[k] = f
    return f


def render_text(lang: str, text: str, *, style: int = 0, scale: float = 0.5,
                spacing: float = 0.0, color: tuple[int, int, int] = (255, 255, 255),
                outline_color: Optional[tuple[int, int, int]] = (0, 0, 0),
                outline_alpha: float = 1.0,
                background: Optional[tuple[int, int, int]] = None,
                markup: bool = False, ruby: Optional[bool] = None, show_ruby: bool = True,
                padding: int = 8, game: Optional[Path] = None) -> Image.Image:
    """Render ``text`` with the game's own font for ``lang`` ('ja', 'en', 'zc', 'zt', 'ko').

    ``scale`` = on-screen pixels per atlas pixel (the engine's sx = sy).
    ``markup`` / ``ruby`` / ``show_ruby`` as in :meth:`Font.layout` (``ruby=None``:
    engine rule, i.e. no ruby parsing for English).
    Returns RGBA (or RGB when ``background`` is given).
    """
    f = _font(lang, game)
    placed = f.layout(text, style=style, scale=scale, spacing=spacing, markup=markup,
                      ruby=ruby, show_ruby=show_ruby)
    return f.render(placed, color=color, outline_color=outline_color,
                    outline_alpha=outline_alpha, background=background, padding=padding)


def iter_pages(lang: str, game: Optional[Path] = None) -> Iterator[FontPage]:
    with FontPages.for_lang(lang, game) as fp:
        for i in range(SECTION_COUNT):
            yield fp.page(i)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _main(argv: Optional[list[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m tsuki.font", description=__doc__.splitlines()[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("pages", help="export atlas pages as PNG")
    p.add_argument("lang", choices=LANGS)
    p.add_argument("outdir")
    p.add_argument("--sections", default="", help="comma list (default: all non-blank)")
    p.add_argument("--background", default="", help="R,G,B to flatten onto (default: RGBA)")
    r = sub.add_parser("render", help="render a string")
    r.add_argument("lang", choices=LANGS)
    r.add_argument("text")
    r.add_argument("out")
    r.add_argument("--style", type=int, default=0)
    r.add_argument("--scale", type=float, default=0.5)
    r.add_argument("--background", default="40,60,110")
    r.add_argument("--markup", action="store_true",
                   help="engine markup: @ codes, '^' line breaks, @1/@2 escapes")
    r.add_argument("--ruby", choices=("auto", "on", "off"), default="auto",
                   help="<base|ruby> parsing (auto = engine rule: off for English)")
    r.add_argument("--hide-ruby", action="store_true", help="skip ruby readings (caller flag 0x2000)")
    c = sub.add_parser("charset", help="print the characters of a language/style")
    c.add_argument("lang", choices=LANGS)
    c.add_argument("--style", type=int, default=0)
    i = sub.add_parser("info", help="page and table summary")
    i.add_argument("lang", choices=LANGS)
    a = ap.parse_args(argv)
    if sys.stdout.encoding and sys.stdout.encoding.lower() not in ("utf-8", "utf8"):
        sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[union-attr]

    def rgb(s: str) -> Optional[tuple[int, int, int]]:
        if not s:
            return None
        v = tuple(int(x) for x in s.split(","))
        if len(v) != 3:
            raise SystemExit("colour must be R,G,B")
        return (v[0], v[1], v[2])

    if a.cmd == "pages":
        out = Path(a.outdir)
        out.mkdir(parents=True, exist_ok=True)
        want = {int(x) for x in a.sections.split(",") if x}
        bg = rgb(a.background)
        with FontPages.for_lang(a.lang) as fp:
            for k in range(SECTION_COUNT):
                if want and k not in want:
                    continue
                pg = fp.page(k)
                if not want and pg.flag == 0:
                    continue
                pg.image(bg).save(out / f"ft_{a.lang}_{k:02d}.png")
                print(f"page {k:2d} slot 0x{pg.slot:02x} {pg.width}x{pg.height} flag {pg.flag}")
        return 0
    if a.cmd == "render":
        ruby_opt = {"auto": None, "on": True, "off": False}[a.ruby]
        im = render_text(a.lang, a.text, style=a.style, scale=a.scale,
                         background=rgb(a.background), markup=a.markup,
                         ruby=ruby_opt, show_ruby=not a.hide_ruby)
        im.save(a.out)
        print(f"{a.out}: {im.size[0]}x{im.size[1]}")
        return 0
    f = Font(a.lang)
    try:
        if a.cmd == "charset":
            cs = f.charset(a.style)
            print(f"{a.lang} style {a.style}: {len(cs)} characters")
            print("".join(ch for ch, _ in cs))
            return 0
        print(f"language {a.lang} (engine index {f.lang_index})")
        for st in range(6):
            cs = f.charset(st)
            pages = sorted({g.page for _, g in cs})
            bad = sum(1 for _, g in cs if not g.in_range)
            print(f"  style {st} ({STYLE_NAMES[st]}): {len(cs)} chars, pages {pages}"
                  + (f", {bad} records out of range" if bad else ""))
        return 0
    finally:
        f.close()


if __name__ == "__main__":
    raise SystemExit(_main())
