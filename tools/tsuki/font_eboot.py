"""Glyph-map tables embedded in eboot.bin (TSUKIHIME PS4 CUSA47999, app 01.01).

The ft_<lang>.mrg files hold only glyph *pictures*; the code point -> glyph
mapping and the per-glyph rectangles live in the executable.  This module reads
them straight out of the user's own eboot.bin (fake-signed SELF or a plain
ELF), so nothing derived from the game has to be shipped.

Everything here was read from the game code (see docs/formats/fonts.md, all
addresses are virtual addresses of eboot 01.01):

* VA 0x10810  ``lookup(lang, code, style) -> glyph index``: binary search in
  sorted arrays of 16-byte records ``{u64 key, s16 index, u16 0, u32 0}``.
  ``key`` is the character's UTF-8 bytes packed big-endian into an integer
  ("A" = 0x41, "e-acute" = 0xC3A9, "kanji ue" = 0xE4B88A).
* VA 0x11150  ``get_glyph`` turns the index into a texture slot + rectangle,
  either arithmetically (wide glyph grid) or through arrays of 10-byte records
  ``{u16 tex_slot, u16 x, u16 y, s16 w, u16 h}``.
* VA 0x11750  decodes one UTF-8 character / ``[tag]`` and calls the above.
* VA 0x11b80 / 0x11ed0  joined dash pieces for runs of U+2015, U+2500, U+25A0.

Table addresses are fixed for this build.  :meth:`EbootFontTables.verify`
checks that the code still references every table at the expected
instruction (so another eboot version fails loudly instead of rendering junk).
"""
from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Optional, Union

__all__ = [
    "EbootImage",
    "EbootFontTables",
    "LookupTable",
    "RecordTable",
    "Rect",
    "pack_utf8",
    "TAGS",
    "SELF_MAGIC",
]

SELF_MAGIC = b"\x4f\x15\x3d\x1d"
ELF_MAGIC = b"\x7fELF"


def pack_utf8(ch: str) -> int:
    """The engine's character key: the UTF-8 bytes of ``ch`` as a big-endian integer."""
    v = 0
    for b in ch.encode("utf-8"):
        v = (v << 8) | b
    return v


def unpack_utf8(key: int) -> str:
    """Inverse of :func:`pack_utf8` (raises on keys that are not one UTF-8 character)."""
    n = max(1, (key.bit_length() + 7) // 8)
    s = key.to_bytes(n, "big").decode("utf-8")
    if len(s) != 1:
        raise ValueError(f"key 0x{key:x} is not a single character")
    return s


# ---------------------------------------------------------------------------
# eboot image (SELF or ELF) -> virtual address reader
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class _Segment:
    va: int
    file_off: int
    filesz: int
    memsz: int


class EbootImage:
    """Read-only virtual-address view of eboot.bin.

    SELF: the segment table at 0x20 (32-byte entries ``{u64 flags, u64 offset,
    u64 filesz, u64 memsz}``; flags bit 0x800 = data block, bits 20..31 = ELF
    program-header index) maps each PT_LOAD program header of the embedded ELF
    (right after the table) to its bytes in the file.  A plain ELF uses
    p_offset directly.  Bytes past p_filesz (bss) read as zero.
    """

    def __init__(self, data: bytes) -> None:
        self.data = data
        self.segments: list[_Segment] = []
        if data[:4] == SELF_MAGIC:
            nseg = struct.unpack_from("<H", data, 0x18)[0]
            elf = 0x20 + 0x20 * nseg
            if data[elf:elf + 4] != ELF_MAGIC:
                raise ValueError("SELF without embedded ELF header")
            blocks: dict[int, int] = {}
            for i in range(nseg):
                flags, off, _fs, _ms = struct.unpack_from("<4Q", data, 0x20 + 0x20 * i)
                if flags & 0x800:
                    blocks[(flags >> 20) & 0xFFF] = off
            self._load_phdrs(elf, blocks)
        elif data[:4] == ELF_MAGIC:
            self._load_phdrs(0, None)
        else:
            raise ValueError("eboot.bin is neither a SELF nor an ELF")
        if not self.segments:
            raise ValueError("no loadable segments found")

    def _load_phdrs(self, elf: int, blocks: Optional[dict[int, int]]) -> None:
        d = self.data
        phoff = struct.unpack_from("<Q", d, elf + 0x20)[0]
        phentsize, phnum = struct.unpack_from("<HH", d, elf + 0x36)
        for i in range(phnum):
            p_type, _flags, p_off, p_va, _pa, p_fs, p_ms = struct.unpack_from(
                "<IIQQQQQ", d, elf + phoff + phentsize * i)
            if p_type not in (1, 0x61000010):    # PT_LOAD, PT_SCE_RELRO
                continue
            if blocks is None:
                off = p_off
            elif i in blocks:
                off = blocks[i]
            else:
                continue
            self.segments.append(_Segment(p_va, off, p_fs, p_ms))

    @classmethod
    def open(cls, path: Union[str, Path]) -> "EbootImage":
        return cls(Path(path).read_bytes())

    def read(self, va: int, n: int) -> bytes:
        for s in self.segments:
            if s.va <= va and va + n <= s.va + s.memsz:
                rel = va - s.va
                have = max(0, min(n, s.filesz - rel))
                out = self.data[s.file_off + rel:s.file_off + rel + have]
                return out + b"\0" * (n - have)
        raise ValueError(f"VA 0x{va:x}+{n} is not mapped")

    def u16(self, va: int) -> int:
        return int(struct.unpack("<H", self.read(va, 2))[0])

    def rip_target(self, insn_va: int, insn_len: int = 7) -> int:
        """Target of a ``lea reg, [rip+disp32]`` (disp32 in the last 4 bytes)."""
        disp = struct.unpack("<i", self.read(insn_va + insn_len - 4, 4))[0]
        return int(insn_va + insn_len + disp)


# ---------------------------------------------------------------------------
# table descriptions (eboot 01.01)
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class _LookupSpec:
    name: str
    va: int
    count: int
    lea_site: int          # VA of a 'lea r64, [rip+disp32]' that loads this table


@dataclass(frozen=True)
class _RecordSpec:
    name: str
    va: int
    count: int             # records that belong to this table (see docs: two tables are short)
    lea_site: int


# 16-byte lookup tables (VA in the data segment).  Counts = immediate "hi" index + 1
# from the binary-search setup in lookup() at VA 0x10810.
LOOKUP_SPECS: tuple[_LookupSpec, ...] = (
    _LookupSpec("narrow", 0x626C10, 109, 0x108C8),   # style 0, code < 0x10000, all languages
    _LookupSpec("ja_s1", 0x6272E0, 701, 0x10C91),
    _LookupSpec("en_s1", 0x629EB0, 111, 0x10D9E),    # en styles 1,3,4,5 (and >5)
    _LookupSpec("zc_s1", 0x62A5A0, 769, 0x10CEE),
    _LookupSpec("zt_s1", 0x62D5B0, 1631, 0x10D49),
    _LookupSpec("ko_s1", 0x633BA0, 1, 0x10C29),
    _LookupSpec("ja_s2", 0x633BB0, 1174, 0x10848),
    _LookupSpec("en_s2", 0x638510, 82, 0x10DFE),
    _LookupSpec("zc_s2", 0x638A30, 1434, 0x10930),
    _LookupSpec("zt_s2", 0x63E3D0, 1631, 0x109A1),
    _LookupSpec("ko_s2", 0x6449C0, 1, 0x10A0C),
    _LookupSpec("ja_wide", 0x6449D0, 2795, 0x10F7E),  # style 0, code >= 0x10000
    _LookupSpec("en_wide", 0x64F880, 6, 0x110F7),
    _LookupSpec("zc_wide", 0x64F8E0, 3403, 0x10FDE),
    _LookupSpec("zt_wide", 0x65CD90, 3570, 0x1103E),
    _LookupSpec("ko_wide", 0x66ACB0, 5, 0x1109E),
)

# 10-byte record tables (VA in the text segment), selected in get_glyph (VA 0x11150).
# lea sites are 7-byte 'lea rcx, [rip+disp32]'.
RECORD_SPECS: tuple[_RecordSpec, ...] = (
    _RecordSpec("ja_s0", 0x177370, 104, 0x1146E),   # NOTE: lookup "narrow" has 109 entries
    _RecordSpec("ja_s2", 0x177780, 1174, 0x11315),
    _RecordSpec("ja_s1", 0x17A560, 701, 0x114A3),
    _RecordSpec("en_s0", 0x17C0D0, 109, 0x11479),
    _RecordSpec("en_s2", 0x17C520, 82, 0x113B5),
    _RecordSpec("en_s3", 0x17C860, 111, 0x114CB),
    _RecordSpec("en_s4", 0x17CCC0, 111, 0x114D6),
    _RecordSpec("en_s5", 0x17D120, 111, 0x114E1),
    _RecordSpec("en_s1", 0x17D580, 111, 0x114C0),
    _RecordSpec("zc_s0", 0x17D9E0, 109, 0x11484),
    _RecordSpec("zc_s2", 0x17DE30, 1434, 0x113D8),
    _RecordSpec("zc_s1", 0x181640, 769, 0x114AC),
    _RecordSpec("zt_s0", 0x183450, 109, 0x1148F),
    _RecordSpec("zt_s1", 0x1838A0, 1631, 0x113F1),  # used for every zt style != 0
    _RecordSpec("ko_s0", 0x187860, 103, 0x1149A),   # NOTE: lookup "narrow" has 109 entries
    _RecordSpec("ko_s2", 0x187C66, 1, 0x11414),
    _RecordSpec("ko_s1", 0x187C70, 1, 0x114B5),
)

WIDE_BASE_VA = 0x187E2E          # u16[5]: first texture slot of the wide grid per language
WIDE_BASE_SITE = 0x111F7         # lea rdx, [rip+...] -> WIDE_BASE_VA
TAGS_VA = 0x187C80               # 39 x char[11] "[heart]" ... "[zap00]"
TAGS_SITE = 0x117B1              # lea r15, [rip+...] -> TAGS_VA
TAG_STRIDE = 11
TAG_COUNT = 39

TAGS: tuple[str, ...] = (
    ("[heart]", "[heart2]", "[star]", "[star2]", "[edia]", "[dummy]", "[onpu]")
    + tuple(f"[ber{i:02d}]" for i in range(31)) + ("[zap00]",)
)


@dataclass(frozen=True)
class Rect:
    tex: int      # texture slot (0x21 + ft section index)
    x: int
    y: int
    w: int        # signed in the engine (s16)
    h: int


class LookupTable:
    """Sorted ``{u64 key -> s16 glyph index}`` array (engine binary search)."""

    def __init__(self, name: str, va: int, raw: bytes, count: int) -> None:
        self.name = name
        self.va = va
        self.count = count
        self.keys: list[int] = []
        self.index: dict[int, int] = {}
        for i in range(count):
            key, idx, pad16, pad32 = struct.unpack_from("<QhHI", raw, 16 * i)
            self.keys.append(key)
            self.index[key] = idx

    def get(self, key: int) -> Optional[int]:
        """Engine semantics: a found key whose index is negative counts as missing."""
        idx = self.index.get(key)
        if idx is None or idx < 0:
            return None
        return idx

    def chars(self) -> list[tuple[str, int]]:
        """(character, glyph index) in key order."""
        return [(unpack_utf8(k), self.index[k]) for k in self.keys]


class RecordTable:
    """10-byte glyph records ``{u16 tex, u16 x, u16 y, s16 w, u16 h}``.

    The engine indexes these without a bounds check, so :meth:`get` reads
    straight from the executable image like the game does (an index past
    ``count`` returns whatever follows: the engine's own behaviour).
    """

    def __init__(self, name: str, va: int, count: int, image: EbootImage) -> None:
        self.name = name
        self.va = va
        self.count = count
        self._image = image

    def get(self, idx: int) -> Rect:
        tex, x, y, w, h = struct.unpack("<HHHhH", self._image.read(self.va + 10 * idx, 10))
        return Rect(tex, x, y, w, h)

    def in_range(self, idx: int) -> bool:
        return 0 <= idx < self.count


class EbootFontTables:
    """All glyph-map data the text renderer needs, read from eboot.bin."""

    def __init__(self, image: EbootImage) -> None:
        self.image = image
        self.lookups: dict[str, LookupTable] = {
            s.name: LookupTable(s.name, s.va, image.read(s.va, 16 * s.count), s.count)
            for s in LOOKUP_SPECS
        }
        self.records: dict[str, RecordTable] = {
            s.name: RecordTable(s.name, s.va, s.count, image) for s in RECORD_SPECS
        }
        self.wide_base: tuple[int, ...] = tuple(image.u16(WIDE_BASE_VA + 2 * i) for i in range(5))
        tags = []
        for i in range(TAG_COUNT):
            raw = image.read(TAGS_VA + TAG_STRIDE * i, TAG_STRIDE)
            tags.append(raw.split(b"\0")[0].decode("ascii"))
        self.tags: tuple[str, ...] = tuple(tags)

    @classmethod
    def open(cls, eboot_path: Union[str, Path]) -> "EbootFontTables":
        return cls(EbootImage.open(eboot_path))

    def verify(self) -> list[str]:
        """Return a list of problems (empty = this eboot matches the documented layout)."""
        issues: list[str] = []
        img = self.image
        for ls in LOOKUP_SPECS:
            t = img.rip_target(ls.lea_site)
            if t != ls.va:
                issues.append(f"lookup {ls.name}: lea at 0x{ls.lea_site:x} -> 0x{t:x}, expected 0x{ls.va:x}")
            lt = self.lookups[ls.name]
            if lt.keys != sorted(lt.keys) or len(set(lt.keys)) != lt.count:
                issues.append(f"lookup {ls.name}: keys not strictly sorted")
            if sorted(lt.index.values()) != list(range(lt.count)):
                issues.append(f"lookup {ls.name}: indices are not a permutation of 0..{lt.count - 1}")
        for rs in RECORD_SPECS:
            t = img.rip_target(rs.lea_site)
            if t != rs.va:
                issues.append(f"records {rs.name}: lea at 0x{rs.lea_site:x} -> 0x{t:x}, expected 0x{rs.va:x}")
        if img.rip_target(WIDE_BASE_SITE) != WIDE_BASE_VA:
            issues.append("wide base table reference moved")
        if img.rip_target(TAGS_SITE) != TAGS_VA:
            issues.append("tag table reference moved")
        if self.tags != TAGS:
            issues.append(f"tag names differ: {self.tags}")
        if self.wide_base != (59, 59, 59, 57, 59):
            issues.append(f"unexpected wide texture bases {self.wide_base}")
        return issues
