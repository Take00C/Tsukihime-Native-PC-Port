"""Inner-format labelling for container payloads (used by tools/inventory.py).

Labels are short strings: "mzp", "mzx", "lv-mzx", "riff-wave/atrac9",
"riff-wave/pcm", "vag", "png", "nam32", "be32-offsets", "img-header",
"script-ascii", "text-utf8", "text-ascii", "zeros", "bin", ...
"""
from __future__ import annotations

import struct
from typing import Any, Optional, Union

from .mzp import MzpArchive, MZP_MAGIC
from .mzx import MZX_MAGIC, MzxError, parse_lv
from .nam import looks_like_nam32

__all__ = [
    "LV_HEADER",
    "ATRAC9_GUID",
    "sniff",
    "riff_info",
    "vag_info",
    "image_header",
    "classify_mzp",
    "is_be32_offset_table",
]

Buffer = Union[bytes, bytearray, memoryview]

LV_HEADER = b"LV\x03\x00\x00\x09\x00"      # the 7-byte LV chunk of every retail voice entry (n = 3)
ATRAC9_GUID = bytes.fromhex("d242e147ba368d4d88fc61654f8c836c")  # {47E142D2-36BA-4D8D-88FC-61654F8C836C}

_MAGICS: list[tuple[bytes, str]] = [
    (MZP_MAGIC, "mzp"),
    (MZX_MAGIC, "mzx"),
    (b"VAGp", "vag"),
    (b"\x89PNG\r\n\x1a\n", "png"),
    (b"OggS", "ogg"),
    (b"NXGX", "nxgx"),
    (b"NXCX", "nxcx"),
    (b"\x1f\x8b", "gzip"),
    (b"DDS ", "dds"),
    (b"GIF8", "gif"),
    (b"\xff\xd8\xff", "jpeg"),
    (b"BM", "bmp?"),
    (b"HUNEXGGEFA10", "hfa"),
    (b"ftyp", "mp4?"),
]


def riff_info(data: Buffer) -> Optional[dict[str, Any]]:
    """Parse a RIFF/WAVE header (needs >= 0x3c bytes)."""
    b = bytes(data[:0x80])
    if len(b) < 12 or b[:4] != b"RIFF" or b[8:12] != b"WAVE":
        return None
    info: dict[str, Any] = {"riff_size": struct.unpack_from("<I", b, 4)[0] + 8}
    if b[12:16] == b"fmt " and len(b) >= 36:
        tag, ch, rate, byte_rate, align, bits = struct.unpack_from("<HHIIHH", b, 20)
        info.update(fmt_tag=tag, channels=ch, rate=rate, block_align=align, bits=bits)
        codec = {1: "pcm", 2: "msadpcm", 0x11: "imaadpcm", 0x166: "xma2"}.get(tag, f"0x{tag:04x}")
        if tag == 0xFFFE and len(b) >= 60:
            guid = b[44:60]
            codec = "atrac9" if guid == ATRAC9_GUID else "ext:" + guid.hex()
        info["codec"] = codec
    return info


def vag_info(data: Buffer) -> Optional[dict[str, Any]]:
    """PS VAG header: big-endian fields, 48-byte header, name at 0x20."""
    b = bytes(data[:48])
    if len(b) < 48 or b[:4] != b"VAGp":
        return None
    version, _res, data_size, rate = struct.unpack_from(">IIII", b, 4)
    return {"version": version, "data_size": data_size, "total_size": data_size + 48,
            "rate": rate, "channels_hint": b[0x1E], "name": b[0x20:0x30].split(b"\0")[0].decode("ascii", "replace")}


def image_header(data: Buffer) -> Optional[dict[str, Any]]:
    """Section 0 of an image MZP starts with 8 u16: width, height, tile_w,
    tile_h, tiles_x, tiles_y, type_a, type_b (meaning of type_* is the image
    agent's job).  Returns None if the numbers are not self-consistent."""
    b = bytes(data[:16])
    if len(b) < 16:
        return None
    w, h, tw, th, tx, ty, ta, tb = struct.unpack("<8H", b)
    if not (w and h and tw and th and tx and ty):
        return None
    if tx != -(-w // tw) or ty != -(-h // th):
        return None
    return {"w": w, "h": h, "tile_w": tw, "tile_h": th, "tiles_x": tx, "tiles_y": ty,
            "type": [ta, tb]}


def is_be32_offset_table(data: Buffer) -> bool:
    """script_text.mrg even sections: big-endian u32 offsets, non-decreasing,
    ending with one 0xFFFFFFFF entry (any 0xFF bytes after the section are the
    mrgd00 inter-section pad, not part of the table)."""
    n = len(data)
    if n < 16 or n % 4:
        return False
    vals = struct.unpack(f">{n // 4}I", bytes(data))
    body = [v for v in vals if v != 0xFFFFFFFF]
    return vals[-1] == 0xFFFFFFFF and all(a <= b for a, b in zip(body, body[1:]))


def _printable_ratio(b: bytes) -> float:
    if not b:
        return 0.0
    ok = sum(1 for c in b if 32 <= c < 127 or c in (9, 10, 13))
    return ok / len(b)


def sniff(data: Buffer, total_size: Optional[int] = None) -> str:
    """Label a payload from its first bytes (pass as much as convenient;
    256+ bytes recommended).  ``total_size`` helps table detection."""
    b = bytes(data[:512])
    if not b:
        return "empty"
    if b[:2] == b"LV" and len(b) >= 4:
        try:
            lv = parse_lv(b)
        except MzxError:
            lv = None
        if lv is not None and b[lv.body_offset:lv.body_offset + 4] == MZX_MAGIC:
            return "lv-mzx"
    if b[:4] == b"RIFF":
        ri = riff_info(b)
        return f"riff-wave/{ri.get('codec', '?')}" if ri else "riff"
    for magic, label in _MAGICS:
        if b.startswith(magic):
            return label
    if b[4:8] == b"ftyp":
        return "mp4"
    if len(b) >= 32 and b[30:32] == b"\r\n" and looks_like_nam32(b[:len(b) // 32 * 32]):
        return "nam32"
    if not any(b):
        return "zeros"
    if b[:3] == b"_ZZ" or (b[:1] == b"_" and _printable_ratio(b) > 0.98):
        return "script-ascii"
    if _printable_ratio(b) > 0.97:
        return "text-ascii"
    try:
        b.decode("utf-8")
        is_utf8 = True
    except UnicodeDecodeError as e:
        is_utf8 = e.start >= len(b) - 3  # cut in the middle of a char
    if is_utf8 and any(c >= 0x80 for c in b) and _printable_ratio(bytes(c for c in b if c < 0x80)) > 0.9:
        return "text-utf8"
    if total_size is not None and total_size % 4 == 0 and len(b) >= 16 and b[:4] == b"\0\0\0\0":
        vals = struct.unpack(f">{len(b) // 4}I", b[:len(b) // 4 * 4])
        if all(a <= c for a, c in zip(vals, vals[1:])):
            return "be32-offsets"
    if image_header(b) is not None:
        return "img-header"
    return "bin"


def classify_mzp(arc: MzpArchive) -> str:
    """Coarse kind of an MZP container:

    * "image"    section 0 is a self-consistent image header and every other
                 section is MZX, count == 1 + tiles_x*tiles_y
    * "archive"  every section is itself an MZP
    * "mixed"    anything else (e.g. allscr.mrg)

    The kind comes from the content, not the NAM extension: allpac3 #135
    "DUMMY.MRG" (also in allpac0/1_2/2/4) is a 16x16 one-tile *image*.
    """
    n = len(arc)
    if n == 0:
        return "empty"
    if all(arc.is_nested(i) for i in range(n)):
        return "archive"
    hdr = image_header(arc.head(0, 16))
    if hdr is not None and all(arc.head(i, 4) == MZX_MAGIC for i in range(1, n)):
        if n == 1 + hdr["tiles_x"] * hdr["tiles_y"]:
            return "image"
        return "image?"
    return "mixed"
