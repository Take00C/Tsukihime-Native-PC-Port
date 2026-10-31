"""Image MZPs: header, pixel formats, tile assembly, sprite layering.

Every picture in the allpac* archives (and the nested allpac3 UI sets, parts.mrg,
the ft_*.mrg font pages and allscr.mrg section 2) is an "mrgd00" container
(tsuki.mzp) whose section 0 is an image header and whose sections 1..N are MZX0
streams (tsuki.mzx, plain), one per tile, in row-major order.  Full description
with byte tables, engine addresses and measured counts: docs/formats/images.md.

Section 0 (all little-endian)::

    0x00 u16 width        stored width  (includes 2 px per tile column when attr bit 0 is set)
    0x02 u16 height       stored height (includes 2 px per tile row    when attr bit 0 is set)
    0x04 u16 tile_w       stored tile width  (incl. border)
    0x06 u16 tile_h
    0x08 u16 tiles_x      = ceil(width / tile_w)
    0x0A u16 tiles_y      = ceil(height / tile_h)
    0x0C u16 bmp_type     1 = palette in this header, 11 = planar RGB565+lo+alpha,
                          12 = per-tile "HEP" block with its own palette
    0x0E u8  depth        low nibble = pixel size code (0: 4 bpp, 1: 8 bpp, 4: 32 bpp)
    0x0F u8  attr         bit 0: every tile carries a 1-pixel border copied from its
                          neighbours (displayed size = stored - 2*tiles)
    0x10 ...              bmp_type 1/2 only: palette, 16 x RGBA (depth code 0) or 256 x RGBA
                          (depth code 1 or 7, PS2 CSM1 order); no palette for other codes
    ...  u8[tiles_x*tiles_y]  tile flags: 0 = empty (engine builds no texture for the tile),
                          1 = "may contain transparency", 2 = "opaque" (packer hints; see
                          docs/formats/images.md 2.3 for how well they hold)

Pixel conversions follow the game's loader (eboot.bin text VA 0xD8B80 header +
palette fix-up, 0xD6900 tile upload, 0xD5970 planar RGB converter):

* type 1 palettes: CSM1 unswizzle (entries 8..15 <-> 16..23 of every 32) for
  256-colour palettes; alpha a -> 0xFF if a == 0x80 else (2*a) & 0xFF.
* type 12 ("HEP") tiles: 32-byte header, pixels, then the palette in plain order;
  alpha a -> 2*min(a, 0x7F).
* type 11 tiles: u16 RGB565 plane, u8 low-bit plane (b:3 g:2 r:3, LSB first),
  u8 alpha plane (alpha used as is).

Output is straight (non-premultiplied) RGBA8, exactly the values the engine uploads.

Layered sprites: ``XXX_<size>_<pose>_<var>_N<nn>_B.MZP`` is the body ("base") and
``XXX_<size>_<pose>_<var>_N<nn>.MZP`` etc. are face/expression layers on the
same tile grid, drawn at offset (0, 0) on top of the base (see compose()).  Which layers
form a picture, the rectangle the engine shows and, for the 166 pictures that stack four
language versions (ja/en/zc/zt) vertically, which band to show, come from the engine's image
catalog: tsuki.image_catalog (CatalogEntry.source_rect, CatalogResolver.render).
"""
from __future__ import annotations

import struct
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path
from typing import TYPE_CHECKING, Iterable, Optional, Sequence, Union

import numpy as np

from . import mzx
from .mzp import MzpArchive
from .sniff import classify_mzp

if TYPE_CHECKING:  # pragma: no cover
    from PIL import Image as PILImage

    from .mrg import MrgArchive

__all__ = [
    "ImageError",
    "PixelFormat",
    "ImageHeader",
    "DecodedImage",
    "TileFlag",
    "HEP_MAGIC",
    "HEP_HEADER_SIZE",
    "parse_header",
    "csm1_unswizzle_table",
    "palette_type1_rgba",
    "palette_hep_rgba",
    "parse_hep_header",
    "decode_tile",
    "decode_tile_indexed",
    "assemble",
    "decode_image",
    "is_image",
    "compose",
    "split_sprite_name",
    "load",
]

HEP_MAGIC = b"HEP\x00"
HEP_HEADER_SIZE = 0x20


class ImageError(ValueError):
    """Malformed or unsupported image MZP."""


class PixelFormat(str, Enum):
    PAL4 = "pal4"            # bmp_type 1, depth&0xF == 0: 16 x RGBA palette in the header, 4 bpp
    PAL8 = "pal8"            # bmp_type 1, depth&0xF == 1: 256 x RGBA palette (CSM1 order), 8 bpp
    HEP8 = "hep8"            # bmp_type 12, depth&0xF == 1: per-tile HEP block (header, 8 bpp, palette)
    RGB565_LO_A = "rgb565lo+a"  # bmp_type 11, depth&0xF == 4: planar u16 RGB565 / u8 low bits / u8 alpha


class TileFlag:
    # Packer hints (docs/formats/images.md 2.3).  Measured on decoded alpha:
    EMPTY = 0        # no texture is built (engine VA 0xD9330); pixels invisible (max alpha <= 8)
    TRANSLUCENT = 1  # "may contain transparency": usually mixed; 25% of HEP8 (4% of PAL8) ones are opaque
    OPAQUE = 2       # "opaque": 99.8% fully opaque; a few tiles have some alpha 216..254 pixels


@dataclass(frozen=True)
class ImageHeader:
    width: int                 # stored width (u16 @0)
    height: int                # stored height (u16 @2)
    tile_w: int
    tile_h: int
    tiles_x: int
    tiles_y: int
    bmp_type: int              # u16 @0x0C as stored
    depth: int                 # u8 @0x0E
    attr: int                  # u8 @0x0F
    palette: bytes             # raw palette bytes (type 1 only, else b"")
    tile_flags: bytes          # one byte per tile
    header_size: int           # size of section 0

    # -- derived ------------------------------------------------------------
    @property
    def ntiles(self) -> int:
        return self.tiles_x * self.tiles_y

    @property
    def border(self) -> int:
        """1 when every tile carries a 1-pixel border (attr bit 0), else 0."""
        return self.attr & 1

    @property
    def inner_w(self) -> int:
        """Displayed pixels per tile column."""
        return self.tile_w - 2 * self.border

    @property
    def inner_h(self) -> int:
        return self.tile_h - 2 * self.border

    @property
    def out_width(self) -> int:
        """Displayed width (engine VA 0xD8C4F: w -= 2*tiles_x when attr bit 0 and w >= 8)."""
        if self.border and self.width >= 8:
            return self.width - 2 * self.tiles_x
        return self.width

    @property
    def out_height(self) -> int:
        if self.border and self.height >= 8:
            return self.height - 2 * self.tiles_y
        return self.height

    @property
    def bpp_code(self) -> int:
        return self.depth & 0x0F

    @property
    def format(self) -> PixelFormat:
        t = self.bmp_type & 0xFF
        if t == 1 and self.bpp_code == 0:
            return PixelFormat.PAL4
        if t == 1 and self.bpp_code == 1:
            return PixelFormat.PAL8
        if t == 12 and self.bpp_code == 1:
            return PixelFormat.HEP8
        if t == 11 and self.bpp_code == 4:
            return PixelFormat.RGB565_LO_A
        raise ImageError(f"unsupported image type: bmp_type={self.bmp_type:#x} depth={self.depth:#x}")

    @property
    def tile_pixels(self) -> int:
        return self.tile_w * self.tile_h

    @property
    def tile_bytes(self) -> int:
        """Expected decompressed size of one tile stream."""
        f = self.format
        n = self.tile_pixels
        if f == PixelFormat.PAL4:
            return n // 2
        if f == PixelFormat.PAL8:
            return n
        if f == PixelFormat.HEP8:
            return HEP_HEADER_SIZE + n + 256 * 4
        return 4 * n

    def tile_origin(self, t: int) -> tuple[int, int]:
        """Top-left of tile t's displayed area in the output image (x, y)."""
        ty, tx = divmod(t, self.tiles_x)
        return tx * self.inner_w, ty * self.inner_h

    def describe(self) -> str:
        return (f"{self.width}x{self.height} (shown {self.out_width}x{self.out_height}) "
                f"tiles {self.tiles_x}x{self.tiles_y} of {self.tile_w}x{self.tile_h} "
                f"type={self.bmp_type:#x} depth={self.depth:#04x} attr={self.attr:#04x} "
                f"format={self.format.value} border={self.border} "
                f"flags={{0:{self.tile_flags.count(0)},1:{self.tile_flags.count(1)},2:{self.tile_flags.count(2)}}}")


def parse_header(sec0: Union[bytes, bytearray, memoryview]) -> ImageHeader:
    """Parse section 0 of an image MZP."""
    b = bytes(sec0)
    if len(b) < 16:
        raise ImageError(f"image header too short ({len(b)} bytes)")
    w, h, tw, th, tx, ty, bt, dw = struct.unpack_from("<8H", b)
    if not (w and h and tw and th and tx and ty):
        raise ImageError(f"zero dimension in image header {b[:16].hex()}")
    if tx != -(-w // tw) or ty != -(-h // th):
        raise ImageError(f"tile counts {tx}x{ty} inconsistent with {w}x{h} / {tw}x{th}")
    depth, attr = dw & 0xFF, dw >> 8
    pal_len = 0
    # engine VA 0xD8DC7..0xD8DFB: a header palette exists only for bmp_type 1/2; depth code 0 ->
    # 16 colours, codes 1 and 7 -> 256 colours, any other code -> none (flags follow at 0x10)
    if bt & 0xFF in (1, 2):
        code = depth & 0x0F
        pal_len = 4 * (16 if code == 0 else 256 if code in (1, 7) else 0)
    need = 16 + pal_len + tx * ty
    if len(b) != need:
        raise ImageError(f"header is {len(b)} bytes, expected 16 + {pal_len} + {tx * ty} = {need}")
    return ImageHeader(w, h, tw, th, tx, ty, bt, depth, attr,
                       b[16:16 + pal_len], b[16 + pal_len:need], len(b))


# ---------------------------------------------------------------------------
# palettes
# ---------------------------------------------------------------------------

def csm1_unswizzle_table() -> np.ndarray:
    """Index map for a PS2 CSM1 256-colour CLUT: logical entry i is stored at
    table[i] (entries 8..15 and 16..23 of each block of 32 are exchanged)."""
    i = np.arange(256)
    swap = ((i >> 3) ^ (i >> 4)) & 1
    return (i ^ (swap * 0x18)).astype(np.intp)


_CSM1 = csm1_unswizzle_table()


def _alpha_type1(a: np.ndarray) -> np.ndarray:
    # engine VA 0xD90B0: a' = 0xFF if a == 0x80 else (a + a) & 0xFF
    a2 = (a.astype(np.uint16) * 2) & 0xFF
    return np.where(a == 0x80, 0xFF, a2).astype(np.uint8)


def palette_type1_rgba(raw: bytes, ncolors: int) -> np.ndarray:
    """Header palette of a bmp_type 1 image -> (ncolors, 4) RGBA uint8 as the engine
    uses it (CSM1 unswizzle for 256 colours, alpha fix-up)."""
    if len(raw) != 4 * ncolors:
        raise ImageError(f"palette is {len(raw)} bytes, expected {4 * ncolors}")
    pal = np.frombuffer(raw, np.uint8).reshape(ncolors, 4).copy()
    if ncolors == 256:
        pal = pal[_CSM1]
    pal[:, 3] = _alpha_type1(pal[:, 3])
    return pal


def palette_hep_rgba(raw: bytes) -> np.ndarray:
    """Palette of a HEP tile -> RGBA, alpha a' = 2*min(a, 0x7F) (engine VA 0xD70CE)."""
    n = len(raw) // 4
    pal = np.frombuffer(raw, np.uint8, 4 * n).reshape(n, 4).copy()
    pal[:, 3] = np.minimum(pal[:, 3], 0x7F) * 2
    return pal


@dataclass(frozen=True)
class HepHeader:
    magic: bytes
    total_size: int     # == decompressed tile size
    tex0: int           # 64-bit PS2 GS TEX0 register (PSM 0x13 = PSMT8, TW/TH = log2 dims, TCC=1)
    unk_10: int         # 0x10 on all retail tiles
    width: int
    height: int
    unk_1c: int         # 1: opaque tile, all palette alpha 0x80; 2: alpha in 0..0x7F (ignored by the engine)

    @property
    def psm(self) -> int:
        return (self.tex0 >> 20) & 0x3F

    @property
    def tw_log2(self) -> int:
        return (self.tex0 >> 26) & 0xF

    @property
    def th_log2(self) -> int:
        return (self.tex0 >> 30) & 0xF


def parse_hep_header(data: Union[bytes, memoryview]) -> HepHeader:
    if len(data) < HEP_HEADER_SIZE:
        raise ImageError("HEP block shorter than its header")
    magic, total, tex0, u10, w, h, u1c = struct.unpack_from("<4sIQIIII", bytes(data[:HEP_HEADER_SIZE]))
    return HepHeader(magic, total, tex0, u10, w, h, u1c)


# ---------------------------------------------------------------------------
# tiles
# ---------------------------------------------------------------------------

def _check_size(hdr: ImageHeader, data: bytes) -> None:
    if len(data) < hdr.tile_bytes:
        raise ImageError(f"tile data is {len(data)} bytes, {hdr.format.value} needs {hdr.tile_bytes}")


def decode_tile_indexed(hdr: ImageHeader, data: bytes, strict: bool = True) -> tuple[np.ndarray, np.ndarray]:
    """Palette formats only: (indices (tile_h, tile_w) uint8, palette (n, 4) RGBA)."""
    fmt = hdr.format
    _check_size(hdr, data)
    tw, th = hdr.tile_w, hdr.tile_h
    n = tw * th
    if fmt == PixelFormat.PAL8:
        idx = np.frombuffer(data, np.uint8, n).reshape(th, tw)
        return idx, palette_type1_rgba(hdr.palette, 256)
    if fmt == PixelFormat.PAL4:
        # engine VA 0xD7040: the tile is one linear stream of n//2 bytes; byte i gives texel 2i
        # (low nibble) and texel 2i+1 (high nibble), regardless of row boundaries.  For an odd
        # texel count the engine leaves the last texel unwritten; we use index 0 there.
        m = n // 2
        packed = np.frombuffer(data, np.uint8, m)
        flat = np.zeros(n, np.uint8)
        flat[0:2 * m:2] = packed & 0x0F
        flat[1:2 * m:2] = packed >> 4
        return flat.reshape(th, tw), palette_type1_rgba(hdr.palette, 16)
    if fmt == PixelFormat.HEP8:
        if strict:
            hh = parse_hep_header(data)
            if hh.magic != HEP_MAGIC or hh.total_size != len(data) or (hh.width, hh.height) != (tw, th):
                raise ImageError(f"HEP header mismatch: {hh}")
        idx = np.frombuffer(data, np.uint8, n, HEP_HEADER_SIZE).reshape(th, tw)
        pal = palette_hep_rgba(bytes(data[HEP_HEADER_SIZE + n:HEP_HEADER_SIZE + n + 1024]))
        return idx, pal
    raise ImageError(f"{fmt.value} is not a palette format")


def decode_tile(hdr: ImageHeader, data: bytes, strict: bool = True) -> np.ndarray:
    """Decompressed tile bytes -> (tile_h, tile_w, 4) RGBA uint8 (stored tile, border included)."""
    fmt = hdr.format
    tw, th = hdr.tile_w, hdr.tile_h
    if fmt == PixelFormat.RGB565_LO_A:
        _check_size(hdr, data)
        n = tw * th
        w = np.frombuffer(data, "<u2", n).astype(np.uint16)
        lo = np.frombuffer(data, np.uint8, n, 2 * n)
        a = np.frombuffer(data, np.uint8, n, 3 * n)
        out = np.empty((n, 4), np.uint8)
        # engine VA 0xD5CC0: byte0 = B, byte1 = G, byte2 = R of a BGRA texel
        out[:, 0] = ((w >> 11) << 3) | (lo >> 5)                    # R: 5 high + 3 low bits
        out[:, 1] = (((w >> 5) & 0x3F) << 2) | ((lo >> 3) & 0x03)   # G: 6 high + 2 low bits
        out[:, 2] = ((w & 0x1F) << 3) | (lo & 0x07)                 # B: 5 high + 3 low bits
        out[:, 3] = a
        return out.reshape(th, tw, 4)
    idx, pal = decode_tile_indexed(hdr, data, strict)
    return pal[idx]


# ---------------------------------------------------------------------------
# whole images
# ---------------------------------------------------------------------------

@dataclass
class DecodedImage:
    header: ImageHeader
    rgba: np.ndarray                     # (out_height, out_width, 4) uint8, straight alpha
    name: Optional[str] = None
    skipped_tiles: list[int] = field(default_factory=list)   # flag-0 tiles not decoded

    @property
    def size(self) -> tuple[int, int]:
        return self.rgba.shape[1], self.rgba.shape[0]

    def to_pil(self) -> "PILImage.Image":
        from PIL import Image
        return Image.fromarray(self.rgba, "RGBA")


def assemble(hdr: ImageHeader, tiles: dict[int, np.ndarray]) -> np.ndarray:
    """Place decoded stored tiles (with border) into the displayed image.
    Missing tiles stay transparent (0, 0, 0, 0)."""
    W, H = hdr.out_width, hdr.out_height
    out = np.zeros((H, W, 4), np.uint8)
    b = hdr.border
    iw, ih = hdr.inner_w, hdr.inner_h
    for t, px in tiles.items():
        x0, y0 = hdr.tile_origin(t)
        if x0 >= W or y0 >= H:
            continue
        cw, ch = min(iw, W - x0), min(ih, H - y0)
        out[y0:y0 + ch, x0:x0 + cw] = px[b:b + ch, b:b + cw]
    return out


def is_image(arc: MzpArchive) -> bool:
    return classify_mzp(arc) == "image"


def decode_image(arc: MzpArchive, *, name: Optional[str] = None, decode_empty: bool = False,
                 strict: bool = True, use_numba: Optional[bool] = None,
                 only_tiles: Optional[Iterable[int]] = None) -> DecodedImage:
    """Decode an image MZP into displayed RGBA.

    Tiles flagged 0 are skipped unless ``decode_empty``: the engine builds no texture for them
    (VA 0xD9330; it does MZX-decompress section 1 = tile 0 before the flag loop whatever its flag,
    VA 0xD91A4, but never displays it).  ``only_tiles`` restricts decoding (others stay
    transparent)."""
    hdr = parse_header(arc.read(0))
    if arc.count != 1 + hdr.ntiles:
        raise ImageError(f"{arc.count} sections for {hdr.ntiles} tiles")
    want = set(range(hdr.ntiles)) if only_tiles is None else set(only_tiles)
    tiles: dict[int, np.ndarray] = {}
    skipped: list[int] = []
    for t in sorted(want):
        if hdr.tile_flags[t] == TileFlag.EMPTY and not decode_empty:
            skipped.append(t)
            continue
        raw = mzx.decompress(arc.view(1 + t), use_numba=use_numba)
        tiles[t] = decode_tile(hdr, raw, strict)
    return DecodedImage(hdr, assemble(hdr, tiles), name, skipped)


def compose(base: np.ndarray, layer: np.ndarray, offset: tuple[int, int] = (0, 0)) -> np.ndarray:
    """Straight-alpha "over" of ``layer`` onto ``base`` at ``offset`` (x, y); returns a new
    array the size of ``base``.  Sprite face layers use offset (0, 0)."""
    out = base.astype(np.float32) / 255.0
    lay = layer.astype(np.float32) / 255.0
    x, y = offset
    H, W = base.shape[:2]
    h = max(0, min(layer.shape[0], H - y))
    w = max(0, min(layer.shape[1], W - x))
    if h == 0 or w == 0:
        return base.copy()
    src = lay[:h, :w]
    dst = out[y:y + h, x:x + w]
    sa = src[..., 3:4]
    da = dst[..., 3:4]
    oa = sa + da * (1.0 - sa)
    rgb = np.where(oa > 0, (src[..., :3] * sa + dst[..., :3] * da * (1.0 - sa)) / np.maximum(oa, 1e-8), 0.0)
    out[y:y + h, x:x + w, :3] = rgb
    out[y:y + h, x:x + w, 3:4] = oa
    return np.clip(out * 255.0 + 0.5, 0, 255).astype(np.uint8)


# ---------------------------------------------------------------------------
# names
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class SpriteName:
    char: str          # e.g. "ARU"
    size: str          # A / L / M / S
    pose: str          # two digits
    variant: str       # two digits
    face: str          # e.g. "N00" / "A01"
    suffix: tuple[str, ...]   # remaining "_xx" parts (e.g. ("04", "04")) or ("B",)

    @property
    def is_base(self) -> bool:
        return self.suffix == ("B",)

    @property
    def group(self) -> str:
        """Name shared by a base and its face layers: CHAR_SIZE_POSE_VAR."""
        return f"{self.char}_{self.size}_{self.pose}_{self.variant}"


def split_sprite_name(name: str) -> Optional[SpriteName]:
    """Parse ``CHR_S_PP_VV_Xnn[_...].MZP``; None for non-sprite names (IMG0001.MZP ...)."""
    stem = name.rsplit(".", 1)[0]
    p = stem.split("_")
    if len(p) < 5 or len(p[0]) != 3 or len(p[1]) != 1 or len(p[4]) != 3:
        return None
    return SpriteName(p[0], p[1], p[2], p[3], p[4], tuple(p[5:]))


# ---------------------------------------------------------------------------
# convenience
# ---------------------------------------------------------------------------

def load(archive: Union[str, Path, "MrgArchive"], key: Union[int, str], child: Optional[int] = None,
         **kw: object) -> DecodedImage:
    """Decode entry ``key`` (index or NAM name) of a HED/NAM/MRG archive; ``child`` selects a
    nested image inside an archive entry (allpac3 UI sets).  ``archive`` may be a base path
    such as ``<game>/image/allpac2`` or an open MrgArchive."""
    from .mrg import MrgArchive
    a: MrgArchive
    own = not isinstance(archive, MrgArchive)
    if isinstance(archive, MrgArchive):
        a = archive
    else:
        a = MrgArchive(archive)
    try:
        idx: int = key if isinstance(key, int) else a.find(key).index
        e = a.entries[idx]
        arc = MzpArchive(a.read(idx))
        if child is not None:
            arc = arc.child(child)
        return decode_image(arc, name=e.name, **kw)  # type: ignore[arg-type]
    finally:
        if own:
            a.close()


def _main(argv: Optional[Sequence[str]] = None) -> int:
    import argparse
    import mmap

    from . import image_dir
    from .mrg import MrgArchive

    ap = argparse.ArgumentParser(prog="python -m tsuki.image",
                                 description="Inspect / export image MZPs as PNG")
    ap.add_argument("source", help="archive base name (allpac0, parts, ...) or path; "
                                   "or a standalone .mrg (ft_en.mrg, allscr.mrg)")
    ap.add_argument("entry", nargs="?", help="entry index or NAM name (for a standalone .mrg: section index)")
    ap.add_argument("--child", type=int, help="nested image index (allpac3 sets, ft pages)")
    ap.add_argument("-o", "--out", help="write PNG here")
    ap.add_argument("--base", help="compose ENTRY (a face layer) over this base entry (name or index) first")
    ap.add_argument("--info", action="store_true", help="print the header only")
    ap.add_argument("--list", action="store_true", help="list image headers of all entries")
    args = ap.parse_args(argv)

    src = Path(args.source)
    if not src.exists() and image_dir() is not None:
        src = image_dir() / args.source  # type: ignore[operator]

    if src.suffix.lower() == ".mrg" and not src.with_suffix(".hed").exists():
        with open(src, "rb") as f:
            mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
            try:
                top = MzpArchive(mm)
                sel = range(top.count) if args.entry is None else [int(args.entry)]
                for i in sel:
                    if not top.is_nested(i):
                        continue
                    arc = top.child(i)
                    if args.child is not None:
                        arc = arc.child(args.child)
                    if not is_image(arc):
                        continue
                    hdr = parse_header(arc.read(0))
                    print(f"{src.name}#{i}: {hdr.describe()}")
                    if args.out and not args.info and args.entry is not None:
                        decode_image(arc).to_pil().save(args.out)
                        print(f"wrote {args.out}")
            finally:
                mm.close()
        return 0

    with MrgArchive(src) as a:
        if args.list or args.entry is None:
            for e in a.entries:
                arc = MzpArchive(a.read(e.index))
                if not is_image(arc):
                    print(f"{e.index}\t{e.name}\t({classify_mzp(arc)}, {arc.count} sections)")
                    continue
                print(f"{e.index}\t{e.name}\t{parse_header(arc.read(0)).describe()}")
            return 0
        key: Union[int, str] = int(args.entry) if args.entry.isdigit() else args.entry
        img = load(a, key, args.child)
        print(f"{img.name}: {img.header.describe()}  skipped={len(img.skipped_tiles)}")
        if args.info or not args.out:
            return 0
        rgba = img.rgba
        if args.base is not None:
            bkey: Union[int, str] = int(args.base) if args.base.isdigit() else args.base
            base = load(a, bkey)
            rgba = compose(base.rgba, rgba, (0, 0))
            print(f"composed over {base.name}: {base.header.describe()}")
        from PIL import Image
        Image.fromarray(rgba, "RGBA").save(args.out)
        print(f"wrote {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
