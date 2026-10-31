"""Read-only extraction of engine tables from eboot.bin (CUSA47999, app 01.01).

The PS4 executable carries several data tables that tie the archives together.
They are not stored in any archive, so a reimplementation must either read
them from the user's eboot.bin at runtime (this module shows how) or ship the
exported copies (``python -m tsuki.catalog export``).

Address map (the SELF wraps one ELF; segment table at file 0x20):

    segment  VA        file offset  size
    text     0x000000  0x002280     0x329dc8   (code + read-only strings)
    relro    0x400000  0x32c210     0x035ed0
    data     0x600000  0x365fa0     0x7d66e0   (memsz 0x1a85790; the rest is .bss)

Pointers inside the data segment are stored as absolute VAs (image base 0) in
the file.  Every R_X86_64_RELATIVE relocation of the image (39,459 of them, in
PT_SCE_DYNLIBDATA) has an addend equal to the value already stored in place, so
the pointers can be followed without applying relocations.

Tables (all verified on the retail v01.01 eboot, see docs/formats/archives_ui_movies.md):

* image table      data VA 0x671900, 14,775 slots of 0x48 bytes (bounds check
  ``cmp edi, 0x39b6`` at text VA 0x41BA2); slots 0..14,743 are used, 14,744..14,774
  are empty.  Record k describes allpac2 entry k (same order, 14,744/14,744 names).
* image name list  data VA 0x7F6800: 32-byte records "NAME\\0...\\r\\n" (no
  extension, upper case) terminated by a 0xFF byte; 14,744 names + 1 empty record.
  Name k == image-table record k.  The script image lookup (text VA 0x78410)
  maps a script name to k through this list.
* old name list    data VA 0x7A8B10: same format, 9,958 names = the first 9,958
  entries of the list above; only used by the unreachable "BG_ERROR" fallback and
  by the unused _PLDB command.
* movie table      data VA 0xDC8C70, 134 records of 0x30 bytes + terminator (0xDCA590).
* UI texture registration tables (21 tables, 220 records of 0x18 bytes).
* language suffix table  relro VA 0x42C030: 5 x char* -> "ja","en","zc","zt","ko".
* prologue movie table   text VA 0x200F70: 4 x u32 -> 4, 46, 47, 106.

Everything here only reads the file; nothing is written.
"""
from __future__ import annotations

import mmap
import re
import struct
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional, Union

__all__ = [
    "EbootError",
    "Eboot",
    "ImageRecord",
    "MovieRecord",
    "UiTexRecord",
    "UiTexTable",
    "SEGMENTS",
    "IMAGE_TABLE_VA",
    "IMAGE_TABLE_SLOTS",
    "IMAGE_TABLE_RECORDS",
    "IMAGE_NAMES_VA",
    "OLD_IMAGE_NAMES_VA",
    "MOVIE_TABLE_VA",
    "LANG_SUFFIX_VA",
    "PROLOGUE_TABLE_VA",
    "UI_TABLE_VAS",
    "UI_TABLES_UNREFERENCED",
    "default_eboot_path",
]

# (VA, file offset, size)
SEGMENTS: tuple[tuple[int, int, int], ...] = (
    (0x000000, 0x002280, 0x329DC8),
    (0x400000, 0x32C210, 0x035ED0),
    (0x600000, 0x365FA0, 0x7D66E0),
)
TEXT_END = 0x329DC8

# Image table as the engine indexes it: record k at 0x671900 + 0x48*k (57 code
# references, e.g. text VA 0x4A075 ``lea rdx, [rip+...]`` -> 0x671900).
IMAGE_TABLE_VA = 0x671900
IMAGE_RECORD_SIZE = 0x48
IMAGE_TABLE_SLOTS = 0x39B6 + 1          # 14,775: bound used by VA 0x41BA0 (size_class getter)
IMAGE_TABLE_RECORDS = 14744             # used slots in v01.01 (== allpac2 entries minus DUMMY.MRG)
IMAGE_NAMES_VA = 0x7F6800
OLD_IMAGE_NAMES_VA = 0x7A8B10
NAME_RECORD_SIZE = 0x20
MOVIE_TABLE_VA = 0xDC8C70
MOVIE_RECORD_SIZE = 0x30
LANG_SUFFIX_VA = 0x42C030
PROLOGUE_TABLE_VA = 0x200F70
UI_RECORD_SIZE = 0x18
# Start VA of every UI texture registration table (header-less arrays of
# 0x18-byte records, terminated by a record whose tex_slot is negative).
# Found by scanning the data segment for pointers to "*_parts*.mrg" strings
# (212 pointers + the 8-record menu_parts table at 0x7A6B20) and confirmed by
# the loaders at text VA 0xC2AF0 / 0xEBCB0, which receive these addresses.
UI_TABLE_VAS: tuple[int, ...] = (
    0x66AD10, 0x66BA80, 0x66BBC0, 0x66C9A0, 0x780C90, 0x781B70,
    0x782700, 0x784290, 0x785E20, 0x7879B0, 0x789540, 0x78B2C0,
    0x7A6090, 0x7A67F0, 0x7A6B20, 0x7A7680, 0x7A8300, 0x8730F0,
    0x8731A0, 0x873660, 0xDB9320,
)
# The five ed1_parts.mrg tables have no code reference at all (rip-relative
# disp32 scan of the whole text segment).  extra_parts.mrg (0x78B2C0) IS
# referenced (text VA 0x62257, 0x624D5, 0x659DF, 0x663F2) although the archive
# does not exist on disk.
UI_TABLES_UNREFERENCED: tuple[int, ...] = (0x782700, 0x784290, 0x785E20, 0x7879B0, 0x789540)

Buffer = Union[bytes, bytearray, memoryview, mmap.mmap]

_FILE_NAME_RE = re.compile(r"[A-Za-z0-9_]+\.(?:mzp|MZP|mrg|MRG)")


class EbootError(ValueError):
    pass


def default_eboot_path() -> Optional[Path]:
    from . import game_dir
    g = game_dir()
    if g is None:
        return None
    p = g / "eboot.bin"
    return p if p.is_file() else None


@dataclass(frozen=True)
class ImageRecord:
    """One record of the engine image table (data VA 0x671900 + 0x48*k).

    Offsets are the engine's (as used by the code):

    +0x00 char* name     0 in the file; only read by VA 0x49D50, which has no caller
    +0x08 char* file     image drawn first: a scene/layer, a standalone sprite, or
                         the "_B" body of a paired sprite.  Resolved with the NAM
                         search (VA 0xC3270) by VA 0x4A060, cached at +0x20
    +0x10 char* overlay  face drawn over ``file`` (paired sprites only) else 0;
                         resolved by VA 0x4A110, cached at +0x24
    +0x18 8 bytes 0
    +0x20 i32 file id cache (-1 in the file), +0x24 i32 overlay id cache (-1)
    +0x28 u32 0
    +0x2C u16 a = floor(w*640/1920), u16 b = floor(h*362/1080)   (size in the
          640x362.667 canvas; verified on all 14,744 records)
    +0x30 u16 w, u16 h   logical full-size image size in pixels
    +0x34 u32 c, +0x38 u32 d   anchor in a/b units: (a//2, b//2) for scenes;
          a per-sprite point for sprites (read by VA 0xBF0B1)
    +0x3C u8 0x0B
    +0x3D u8 flags       bit field; 3 (bits 0+1) only for the 7 solid colours.
                         Code tests &1 (VA 0x517F7, 0x82B88), &2 (0x49B9B,
                         0x51824), &4 (0x4B66E, 0x4C00B, 0x57DB9, 0x592ED)
    +0x3E u8 0           read by VA 0x4B676, 0x4C28F, 0x4F3D2 (runtime value)
    +0x3F u8 layer_kind  0 base, 1 layer, 2 4x-tall scene, 3 layer of a kind-2
                         scene; bit 0 tested by VA 0x957F4
    +0x40 u16 char_id (sprites only), +0x42 u16 size_class (A/L/M/S -> 0/1/2/3,
          read by VA 0x41BA0)
    +0x44 u32 0
    """
    index: int
    va: int
    file: str
    overlay: Optional[str]
    a: int
    b: int
    w: int
    h: int
    c: int
    d: int
    b3c: int
    flags: int
    b3e: int
    layer_kind: int
    char_id: int
    size_class: int

    @property
    def name(self) -> str:
        """Logical (script-visible) name = overlay if present else file.
        Equals the allpac2.nam name of entry ``index``."""
        return self.overlay if self.overlay else self.file

    @property
    def paired(self) -> bool:
        """True when the record draws a body (``file``) plus a face (``overlay``)."""
        return self.overlay is not None


@dataclass(frozen=True)
class MovieRecord:
    """Movie table record (data VA 0xdc8c70 + 0x30*k).

    +0x00 i32 movie_id (== k; -1 terminates)   +0x04 u32 0
    +0x08 char* path ("/app0/image/movie/<name>.mp4")
    +0x10 u32 0, u32 960, u32 544, u32 0, f32 0.0, f32 1.0, f32 1.0, u32 0
    (runtime defaults of the player object; 960x544 is presumably a
    half-resolution target; the fields are not needed to play a file).
    """
    movie_id: int
    va: int
    path: str
    raw_fields: tuple[int, ...]

    @property
    def file_name(self) -> str:
        return self.path.rsplit("/", 1)[-1]


@dataclass(frozen=True)
class UiTexRecord:
    """UI texture registration record (0x18 bytes).

    +0x00 i32 state (0 in the file; VA 0xC2AF0 requires 1 in the first record
              unless called with force=1; VA 0xEBCB0 sets it to 1 when loaded)
    +0x04 i32 tex_slot (engine texture slot; < 0 terminates the table)
    +0x08 char* name (base name such as "conf_parts.mrg"; for *.mrg names the
               localising loader inserts "_<lang>" before the extension)
    +0x10 i32 child (section index inside the archive; VA 0xEB350 clamps it
               to n_children - 1)
    +0x14 i32 child2 (-1 = none: a second child loaded, unclamped, into the
               slot's second texture handle by VA 0xEB427..0xEB448; -1 in every
               live record, 1 in the unreferenced ed1_parts tables)
    """
    va: int
    state: int
    tex_slot: int
    name: str
    child: int
    child2: int


@dataclass
class UiTexTable:
    va: int
    records: list[UiTexRecord] = field(default_factory=list)
    terminator: bytes = b""


class Eboot:
    """mmap-backed read-only view of eboot.bin with VA helpers."""

    def __init__(self, path: Union[str, Path, None] = None) -> None:
        if path is None:
            path = default_eboot_path()
            if path is None:
                raise EbootError("eboot.bin not found (set TSUKI_GAME_DIR)")
        self.path = Path(path)
        self._f = open(self.path, "rb")
        self.mm: Optional[mmap.mmap] = mmap.mmap(self._f.fileno(), 0, access=mmap.ACCESS_READ)
        if self.mm[0x160:0x164] != b"\x7fELF":
            raise EbootError("no embedded ELF header at 0x160 (not the expected SELF layout)")

    def close(self) -> None:
        if self.mm is not None:
            self.mm.close()
            self.mm = None
        self._f.close()

    def __enter__(self) -> "Eboot":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    # -- VA helpers ---------------------------------------------------------
    @staticmethod
    def va_to_offset(va: int) -> int:
        for v, o, n in SEGMENTS:
            if v <= va < v + n:
                return o + (va - v)
        raise EbootError(f"VA 0x{va:x} is not file-backed")

    def read(self, va: int, n: int) -> bytes:
        if self.mm is None:
            raise EbootError("eboot closed")
        o = self.va_to_offset(va)
        return bytes(self.mm[o:o + n])

    def u32(self, va: int) -> int:
        return int(struct.unpack("<I", self.read(va, 4))[0])

    def i32(self, va: int) -> int:
        return int(struct.unpack("<i", self.read(va, 4))[0])

    def u64(self, va: int) -> int:
        return int(struct.unpack("<Q", self.read(va, 8))[0])

    def cstr(self, va: int, limit: int = 256) -> str:
        return self.read(va, limit).split(b"\0", 1)[0].decode("latin-1")

    def is_text_ptr(self, p: int) -> bool:
        return 0 < p < TEXT_END

    # -- tables -------------------------------------------------------------
    def lang_suffixes(self) -> list[str]:
        out = [self.cstr(self.u64(LANG_SUFFIX_VA + 8 * i), 8) for i in range(5)]
        if out != ["ja", "en", "zc", "zt", "ko"]:
            raise EbootError(f"unexpected language suffix table {out} (different eboot version?)")
        return out

    def prologue_ids(self) -> list[int]:
        return [self.u32(PROLOGUE_TABLE_VA + 4 * i) for i in range(4)]

    def _image_slot(self, k: int) -> Optional[ImageRecord]:
        va = IMAGE_TABLE_VA + IMAGE_RECORD_SIZE * k
        raw = self.read(va, IMAGE_RECORD_SIZE)
        pname, pfile, pov = struct.unpack_from("<3Q", raw, 0)
        if pfile == 0:
            return None
        if pname != 0:
            raise EbootError(f"image slot {k}: name pointer is not 0 (different eboot version?)")
        if not self.is_text_ptr(pfile) or (pov and not self.is_text_ptr(pov)):
            raise EbootError(f"image slot {k}: pointer outside the text segment")
        fname = self.cstr(pfile, 48)
        ov = self.cstr(pov, 48) if pov else None
        for n in (fname, ov):
            if n is not None and not _FILE_NAME_RE.fullmatch(n):
                raise EbootError(f"image slot {k}: unexpected file name {n!r}")
        a, b, w, h = struct.unpack_from("<4H", raw, 0x2C)
        c, d = struct.unpack_from("<II", raw, 0x34)
        b3c, flags, b3e, kind = raw[0x3C], raw[0x3D], raw[0x3E], raw[0x3F]
        cid, scls = struct.unpack_from("<HH", raw, 0x40)
        return ImageRecord(k, va, fname, ov, a, b, w, h, c, d, b3c, flags, b3e, kind, cid, scls)

    def image_table(self, expected: Optional[int] = IMAGE_TABLE_RECORDS) -> list[ImageRecord]:
        """All used slots of the image table.  Slots are read up to the capacity
        (14,775); the used slots must be contiguous from 0, the rest empty.
        ``expected`` (default 14,744 for v01.01) is checked unless None."""
        out: list[ImageRecord] = []
        empty_seen = False
        for k in range(IMAGE_TABLE_SLOTS):
            rec = self._image_slot(k)
            if rec is None:
                empty_seen = True
                continue
            if empty_seen:
                raise EbootError(f"image slot {k} is used after an empty slot")
            out.append(rec)
        if not out or out[0].file != "BLACK.mzp":
            raise EbootError("image table not found at the v01.01 address")
        if expected is not None and len(out) != expected:
            raise EbootError(f"image table has {len(out)} records, expected {expected}")
        return out

    def name_list(self, va: int = IMAGE_NAMES_VA, limit: int = 0x8000) -> list[str]:
        """Embedded name list (32-byte NUL-padded records ending in CR LF,
        terminated by a 0xFF byte).  Returns every record before the 0xFF,
        including the final empty one, so index k == engine index k."""
        out: list[str] = []
        for k in range(limit):
            rec = self.read(va + NAME_RECORD_SIZE * k, NAME_RECORD_SIZE)
            if rec[0] == 0xFF:
                return out
            out.append(rec.split(b"\0", 1)[0].decode("latin-1"))
        raise EbootError(f"name list at 0x{va:x} has no 0xFF terminator within {limit} records")

    def image_names(self) -> list[str]:
        """The script-name list at data VA 0x7F6800 (14,745 records; the last is empty)."""
        names = self.name_list(IMAGE_NAMES_VA)
        if not names or names[0] != "BLACK":
            raise EbootError("image name list not found at the v01.01 address")
        return names

    def movie_table(self) -> list[MovieRecord]:
        out: list[MovieRecord] = []
        va = MOVIE_TABLE_VA
        while True:
            mid = self.i32(va)
            if mid < 0:
                break
            path = self.cstr(self.u64(va + 8), 96)
            raw = struct.unpack("<8I", self.read(va + 0x10, 0x20))
            out.append(MovieRecord(mid, va, path, tuple(int(x) for x in raw)))
            va += MOVIE_RECORD_SIZE
        if not out or not out[0].path.endswith("titlebg1.mp4"):
            raise EbootError("movie table not found at the v01.01 address")
        return out

    def ui_table(self, va: int) -> UiTexTable:
        t = UiTexTable(va)
        k = 0
        while True:
            r = va + UI_RECORD_SIZE * k
            state, tex = struct.unpack("<ii", self.read(r, 8))
            p = self.u64(r + 8)
            if tex < 0 or not self.is_text_ptr(p):
                t.terminator = self.read(r, UI_RECORD_SIZE)
                break
            child, child2 = struct.unpack("<ii", self.read(r + 16, 8))
            t.records.append(UiTexRecord(r, state, tex, self.cstr(p, 64), child, child2))
            k += 1
        return t

    def ui_tables(self) -> list[UiTexTable]:
        return [self.ui_table(va) for va in UI_TABLE_VAS]
