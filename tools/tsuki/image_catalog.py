"""The engine's image catalog: a table compiled into eboot.bin that maps every displayable
picture id (one record per allpac2 entry, same order) to the full-size MZP layer(s) it is drawn
from, its logical size, anchor and draw flags.  Full description: docs/formats/images.md 8.

Record (0x48 bytes, little-endian).  The engine indexes the table as ``0x671900 + id*0x48``
(data VA, eboot v01.01; file offset 0x3D78A0; e.g. text VA 0x41BA0..0x41BB7)::

    +0x00 u64       0                     [14744/14744]
    +0x08 ptr       name0   base / full image MZP name (text-segment VA, file = VA + 0x2280)
    +0x10 ptr       name1   face layer drawn over name0, or 0
    +0x18 i32,i32   layer_x, layer_y      position of name1 relative to name0 (engine VA 0x4B715
                                          converts it to float); (0, 0) in all 14744 records
    +0x20 i32       -1      runtime cache: file index of name0 (filled by the engine, VA 0x4B5E4)
    +0x24 i32       -1      runtime cache: file index of name1
    +0x28 u32       0
    +0x2C u16       w3      floor(width / 3)            [14744/14744]
    +0x2E u16       h3      floor(height * 362 / 1080)  [14744/14744]
    +0x30 u16       width   logical picture width (one language band, see below)
    +0x32 u16       height  logical picture height
    +0x34 i32       anchor_x  (w3/h3 scale)
    +0x38 i32       anchor_y
    +0x3C u8        kind        0x0B in all records
    +0x3D u8        draw_flags  bits 0..2 tested by the engine (VA 0x517F7, 0x49B9B, 0x4B66E); 0 or 3
    +0x3E u8        draw_param  passed to VA 0xE9910 when draw_flags bit 2 is set; 0 in all records
    +0x3F u8        pic_flags   bit 0: name ends in _HH_VV, a variant of another picture (VA 0x957B0)
                                bit 1: four language bands ja/en/zc/zt stacked vertically (VA 0xC2620)
    +0x40 u16       char_id     character id of a sprite record (1 SIK, 2 ARU, 3 SIE, ... 30 Y00): one
                                code per character, 0 for the _HH_VV variants and non-sprites
                                (data only; engine use not traced)
    +0x42 i16       scale_class sprite size letter A/L/M/S -> 0/1/2/3 [14743/14744]; the engine
                                multiplies a position by 1, 0.5, 0.25, 0.125 for it (VA 0x526A3)
    +0x44 u32       0

The used records are followed by empty slots (name0 = name1 = 0, +0x20 = -1); the engine's
bound is id <= 0x39B6 (14,775 slots: 14,744 used + 31 empty).

Language bands (pic_flags bit 1, 166 records): the engine's draw helper VA 0xC2620 selects
rows ``[L*S, L*S + height)`` and columns ``[0, width)`` of the full-size picture, where L is the
language index (0 ja, 1 en, 2 zc, 3 zt; VA 0x3ED90) and ``S = ceil(height / 254) * 254``; the
thumbnail helper VA 0xC2730 uses ``S = int(height * 0.078125 + 0.5)`` (= height / 12.8) and
width ``int(width * 0.078125 + 0.5)`` on the allpac2 thumbnail.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING, Optional, Union

if TYPE_CHECKING:  # pragma: no cover
    import numpy as np

__all__ = ["CatalogEntry", "CatalogResolver", "load_catalog", "find_catalog", "load_picture_names",
           "lang_index", "crop_rect", "LANGS", "BAND_ALIGN", "THUMB_SCALE", "TEXT_FILE_OFFSET",
           "RECORD_SIZE", "FULL_SIZE_ARCHIVES"]

TEXT_FILE_OFFSET = 0x2280
TEXT_SIZE = 0x329DC8
RECORD_SIZE = 0x48
NAME0_OFFSET = 0x08            # name0 pointer inside a record
# archives holding the full-size layers named by the catalog (every name is in exactly one)
FULL_SIZE_ARCHIVES = ("allpac0", "allpac1", "allpac1_2", "allpac4")
THUMB_ARCHIVE = "allpac2"
# language band order = engine language index (VA 0x3ED90: 0 ja, 1 en, 2 zc, 3 zt)
LANGS = ("ja", "en", "zc", "zt")
BAND_ALIGN = 254               # full-size band stride is rounded up to a multiple of this (VA 0xC26A1)
THUMB_SCALE = 0.078125         # 1/12.8, thumbnail scale used by VA 0xC2730 / 0xADADF

Rect = tuple[int, int, int, int]   # x0, y0, x1, y1 (exclusive)


def lang_index(lang: Union[str, int]) -> int:
    """'ja' / 'en' / 'zc' / 'zt' (or 0..3) -> engine language index."""
    if isinstance(lang, int):
        if not 0 <= lang < len(LANGS):
            raise ValueError(f"language index {lang} not in 0..3")
        return lang
    try:
        return LANGS.index(lang.lower())
    except ValueError:
        raise ValueError(f"unknown language {lang!r} (expected one of {LANGS})") from None


@dataclass(frozen=True)
class CatalogEntry:
    index: int
    file_offset: int        # file offset of the 0x48-byte record in eboot.bin
    name0: str
    name1: Optional[str]
    layer_x: int            # +0x18: name1 position relative to name0
    layer_y: int
    w3: int
    h3: int
    width: int
    height: int
    anchor_x: int
    anchor_y: int
    kind: int               # +0x3C
    draw_flags: int         # +0x3D
    draw_param: int         # +0x3E
    pic_flags: int          # +0x3F
    char_id: int            # +0x40
    scale_class: int        # +0x42

    @property
    def thumb_name(self) -> str:
        """The allpac2 NAM name of this record's entry."""
        return (self.name1 or self.name0).upper()

    @property
    def layers(self) -> list[str]:
        return [n.upper() for n in (self.name0, self.name1) if n]

    @property
    def flags_a(self) -> int:
        """u32 at +0x3C (kind | draw_flags<<8 | draw_param<<16 | pic_flags<<24), as in older notes."""
        return self.kind | self.draw_flags << 8 | self.draw_param << 16 | self.pic_flags << 24

    @property
    def flags_b(self) -> int:
        """u32 at +0x40 (char_id | scale_class<<16), as in older notes."""
        return self.char_id | (self.scale_class & 0xFFFF) << 16

    @property
    def is_variant(self) -> bool:
        """pic_flags bit 0: an ``<base>_HH_VV`` variant (7,288 records)."""
        return bool(self.pic_flags & 1)

    @property
    def has_language_bands(self) -> bool:
        """pic_flags bit 1: the picture stores ja/en/zc/zt versions stacked vertically."""
        return bool(self.pic_flags & 2)

    @property
    def band_stride(self) -> int:
        """Row distance between language bands in the full-size picture (0 without bands)."""
        return -(-self.height // BAND_ALIGN) * BAND_ALIGN if self.has_language_bands else 0

    def source_rect(self, lang: Union[str, int] = "en") -> Rect:
        """Part of the full-size picture (name0 + name1, displayed pixels) the engine draws.
        Without language bands this is the logical size from the top-left; the rest of the
        tile-rounded MZP is padding."""
        if not self.has_language_bands:
            return 0, 0, self.width, self.height
        y0 = lang_index(lang) * self.band_stride
        return 0, y0, self.width, y0 + self.height

    def thumb_rect(self, lang: Union[str, int] = "en") -> Rect:
        """Part of the allpac2 thumbnail the engine draws (VA 0xC2730)."""
        tw = int(self.width * THUMB_SCALE + 0.5)
        th = int(self.height * THUMB_SCALE + 0.5)
        if not self.has_language_bands:
            return 0, 0, tw, th
        y0 = lang_index(lang) * th
        return 0, y0, tw, y0 + th


def crop_rect(rgba: "np.ndarray", rect: Rect) -> "np.ndarray":
    """Copy ``rect`` out of an (H, W, 4) image; parts outside the image are transparent."""
    import numpy as np
    x0, y0, x1, y1 = rect
    out = np.zeros((max(0, y1 - y0), max(0, x1 - x0), 4), np.uint8)
    H, W = rgba.shape[:2]
    sx0, sy0, sx1, sy1 = max(x0, 0), max(y0, 0), min(x1, W), min(y1, H)
    if sx1 > sx0 and sy1 > sy0:
        out[sy0 - y0:sy1 - y0, sx0 - x0:sx1 - x0] = rgba[sy0:sy1, sx0:sx1]
    return out


def _cstr(data: bytes, va: int) -> str:
    if not 0 < va < TEXT_SIZE:
        raise ValueError(f"name pointer {va:#x} outside the text segment")
    o = TEXT_FILE_OFFSET + va
    end = data.find(b"\0", o, o + 96)
    if end < 0:
        raise ValueError(f"unterminated name at text VA {va:#x}")
    return data[o:end].decode("ascii")


_REC = struct.Struct("<QQQiiiiIHHHHiiBBBBHhI")
assert _REC.size == RECORD_SIZE


def _is_record_start(data: bytes, off: int) -> bool:
    """Cheap test used while searching: zero +0x00, name0 -> '*.mzp', -1/-1 at +0x20."""
    if off < 0 or off + RECORD_SIZE > len(data):
        return False
    f = _REC.unpack_from(data, off)
    if f[0] != 0 or f[5] != -1 or f[6] != -1 or not f[1]:
        return False
    try:
        return _cstr(data, f[1]).upper().endswith(".MZP")
    except (ValueError, UnicodeDecodeError):
        return False


def find_catalog(data: bytes) -> tuple[int, int, int]:
    """(file offset of record 0, used records, empty slots after them).

    Located by content: record 0's name0 is ``BLACK.mzp`` and record 1's is ``BLUE.mzp``.
    Records end at the first slot whose name0 pointer is 0 (the empty slots have name0 = name1 = 0
    and -1 at +0x20); a slot that is neither a record nor empty raises ValueError."""
    s = data.find(b"BLACK.mzp\0", TEXT_FILE_OFFSET, TEXT_FILE_OFFSET + TEXT_SIZE)
    start = -1
    while s >= 0 and start < 0:
        ptr = struct.pack("<Q", s - TEXT_FILE_OFFSET)
        pos = data.find(ptr, TEXT_FILE_OFFSET + TEXT_SIZE)
        while pos >= 0:
            rec = pos - NAME0_OFFSET
            if _is_record_start(data, rec) and _is_record_start(data, rec + RECORD_SIZE):
                n1 = _REC.unpack_from(data, rec + RECORD_SIZE)[1]
                if _cstr(data, n1).upper() == "BLUE.MZP":
                    start = rec
                    break
            pos = data.find(ptr, pos + 1)
        s = data.find(b"BLACK.mzp\0", s + 1, TEXT_FILE_OFFSET + TEXT_SIZE)
    if start < 0:
        raise ValueError("image catalog not found (no BLACK.mzp/BLUE.mzp record pair)")
    n = 0
    while True:
        off = start + n * RECORD_SIZE
        if off + RECORD_SIZE > len(data):
            raise ValueError("image catalog runs past the end of the file")
        f = _REC.unpack_from(data, off)
        if f[1] == 0:
            break
        if not _is_record_start(data, off):
            raise ValueError(f"malformed catalog record {n} at file {off:#x}")
        n += 1
    empty = 0
    while True:
        off = start + (n + empty) * RECORD_SIZE
        if off + RECORD_SIZE > len(data):
            break
        f = _REC.unpack_from(data, off)
        if f[1] or f[2] or f[5] != -1:
            break
        empty += 1
    if empty == 0:
        raise ValueError(f"no empty terminator slot after catalog record {n - 1}")
    return start, n, empty


def load_catalog(eboot: Union[str, Path, bytes]) -> list[CatalogEntry]:
    """Locate and parse the catalog.  Raises ValueError if it cannot be found or is malformed."""
    data = eboot if isinstance(eboot, bytes) else Path(eboot).read_bytes()
    start, n, _ = find_catalog(data)
    out: list[CatalogEntry] = []
    for i in range(n):
        off = start + i * RECORD_SIZE
        (_, p0, p1, lx, ly, _c0, _c1, _z, w3, h3, w, h, ax, ay,
         kind, dfl, dpar, pfl, a40, scl, _z44) = _REC.unpack_from(data, off)
        out.append(CatalogEntry(i, off, _cstr(data, p0), _cstr(data, p1) if p1 else None, lx, ly,
                                w3, h3, w, h, ax, ay, kind, dfl, dpar, pfl, a40, scl))
    return out


def load_picture_names(eboot: Union[str, Path, bytes]) -> list[str]:
    """Picture names in catalog order as embedded at data VA 0x7F6800 (32-byte NUL-padded
    entries ending in CRLF, like a NAM file; == allpac2 names without ``.MZP``)."""
    data = eboot if isinstance(eboot, bytes) else Path(eboot).read_bytes()
    _, n, _ = find_catalog(data)
    pat = b"BLACK".ljust(30, b"\0") + b"\r\n" + b"BLUE".ljust(30, b"\0") + b"\r\n"
    o = data.find(pat)
    while o >= 0:
        names: list[str] = []
        while True:
            r = data[o + 32 * len(names):o + 32 * len(names) + 32]
            if len(r) < 32 or r[30:] != b"\r\n" or r[0] == 0:
                break
            names.append(r[:30].rstrip(b"\0").decode("ascii"))
        if len(names) == n:
            return names
        o = data.find(pat, o + 1)
    raise ValueError(f"no picture name table with {n} entries found")


class CatalogResolver:
    """Find catalog layer names in the full-size archives and render records.

    >>> r = CatalogResolver(image_dir())                    # doctest: +SKIP
    >>> rgba = r.render(cat[11473], crop_to_logical=True)    # base + face, straight RGBA
    >>> en = r.render(cat[2438], crop_to_logical=True, lang="en")   # English band only
    """

    def __init__(self, image_dir: Union[str, Path], archives: tuple[str, ...] = FULL_SIZE_ARCHIVES) -> None:
        from .mrg import MrgArchive
        self.image_dir = Path(image_dir)
        self.archives: dict[str, MrgArchive] = {n: MrgArchive(self.image_dir / n) for n in archives}
        self._thumbs: Optional[MrgArchive] = None
        self.where: dict[str, tuple[str, int]] = {}
        for an, a in self.archives.items():
            for e in a.entries:
                if e.name is not None:
                    self.where.setdefault(e.name.upper(), (an, e.index))

    def close(self) -> None:
        for a in self.archives.values():
            a.close()
        if self._thumbs is not None:
            self._thumbs.close()

    def __enter__(self) -> "CatalogResolver":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    def locate(self, name: str) -> tuple[str, int]:
        """(archive, entry index) of a layer name; KeyError if it exists nowhere (3 retail bases)."""
        return self.where[name.upper()]

    def render(self, entry: CatalogEntry, crop_to_logical: bool = False,
               lang: Union[str, int] = "en") -> "np.ndarray":
        """Decode name0 and draw name1 over it at (layer_x, layer_y).  With ``crop_to_logical``
        return exactly ``entry.source_rect(lang)`` (width x height; for language-band records
        the band of ``lang``), padded with transparency where the MZP is smaller."""
        from . import image as timg
        an, idx = self.locate(entry.name0)
        rgba = timg.load(self.archives[an], idx).rgba
        if entry.name1:
            bn, bidx = self.locate(entry.name1)
            rgba = timg.compose(rgba, timg.load(self.archives[bn], bidx).rgba, (entry.layer_x, entry.layer_y))
        if crop_to_logical:
            rgba = crop_rect(rgba, entry.source_rect(lang))
        return rgba

    def thumbnail(self, entry: CatalogEntry, lang: Optional[Union[str, int]] = None) -> "np.ndarray":
        """The record's allpac2 thumbnail (already composited); with ``lang`` only the part the
        engine draws (``entry.thumb_rect(lang)``)."""
        from . import image as timg
        from .mrg import MrgArchive
        if self._thumbs is None:
            self._thumbs = MrgArchive(self.image_dir / THUMB_ARCHIVE)
        rgba = timg.load(self._thumbs, entry.index).rgba
        return rgba if lang is None else crop_rect(rgba, entry.thumb_rect(lang))


def _main(argv: Optional[list[str]] = None) -> int:
    import argparse

    from . import game_dir

    ap = argparse.ArgumentParser(prog="python -m tsuki.image_catalog", description="Dump the eboot image catalog")
    ap.add_argument("--eboot", help="path to eboot.bin (default: <game>/eboot.bin)")
    ap.add_argument("--grep", help="only records whose names contain this text")
    ap.add_argument("--bands", action="store_true", help="only records with language bands")
    ap.add_argument("--render", type=int, metavar="INDEX", help="render this record to --out")
    ap.add_argument("--lang", default="en", choices=LANGS, help="language band to render (default en)")
    ap.add_argument("--full", action="store_true", help="with --render: do not crop to the logical rect")
    ap.add_argument("--out", default="catalog.png")
    args = ap.parse_args(argv)
    path = Path(args.eboot) if args.eboot else (game_dir() or Path(".")) / "eboot.bin"
    data = path.read_bytes()
    start, n, empty = find_catalog(data)
    cat = load_catalog(data)
    print(f"# {n} records + {empty} empty slots at file 0x{start:x}")
    if args.render is not None:
        from PIL import Image

        from . import image_dir
        idir = image_dir()
        if idir is None:
            raise SystemExit("game image folder not found")
        with CatalogResolver(idir) as r:
            e = cat[args.render]
            rgba = r.render(e, crop_to_logical=not args.full, lang=args.lang)
            Image.fromarray(rgba, "RGBA").save(args.out)
            what = "full" if args.full else f"rect {e.source_rect(args.lang)}"
            print(f"record {e.index}: {e.layers} {what} -> {args.out} ({rgba.shape[1]}x{rgba.shape[0]})")
        return 0
    for e in cat:
        if args.grep and args.grep.upper() not in " ".join(e.layers):
            continue
        if args.bands and not e.has_language_bands:
            continue
        print(f"{e.index}\t{e.name0}\t{e.name1 or '-'}\t{e.width}x{e.height}\tw3h3={e.w3}x{e.h3}\t"
              f"anchor=({e.anchor_x},{e.anchor_y})\tpic_flags={e.pic_flags}"
              f"{' bands' if e.has_language_bands else ''}{' variant' if e.is_variant else ''}\t"
              f"draw_flags={e.draw_flags}\tchar_id={e.char_id}\tscale_class={e.scale_class}")
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
