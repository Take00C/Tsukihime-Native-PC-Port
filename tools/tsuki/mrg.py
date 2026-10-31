"""HED/NAM/MRG triplet archives (allpac*, bgm, vse, vag, parts, voice, voice2).

``<base>.hed`` is the entry table, ``<base>.mrg`` the data (2048-byte
sectors, entries stored back to back in index order), ``<base>.nam`` an
optional name table (see tsuki.nam).  Two HED layouts exist on PS4:

HED8 (allpac0/1/1_2/2/3/4, bgm, vse, vag, parts) - 8-byte records::

    +0 u32 offset_field : bits 0..15  = sector offset bits 0..15
                          bits 16..27 = always 0
                          bits 28..31 = sector offset bits 16..19
    +4 u16 size_sectors : ceil(exact size / 0x800)
    +6 u16 size_lo      : exact entry size in bytes & 0xFFFF

HED4 (voice, voice2) - 4-byte records::

    +0 u16 offset_lo    : sector offset bits 0..15
    +2 u16 packed       : bits 0..11  = size in sectors
                          bits 12..15 = sector offset bits 16..19

The HED4 sector count is floor(exact / 0x800) + 1 (one more than ceil when the
exact entry size is a multiple of 0x800: voice entries 3028, 4044, 13042,
13595), and no exact size is stored; the MZX stream inside defines it.

Engine readers (eboot.bin text VA): HED8 offset lookup with banks 0xE5CE8
(allpac*), plain HED8 sector accessor 0xE7040 (bgm/vse/vag, 8-byte stride,
returns 0 when word0 == 0xFFFF), HED4 readers 0xE5FB0 and 0xE6480 (4-byte
stride, sector = w0 | (w1 & 0xF000) << 4, bytes = (w1 & 0xFFF) << 11; a
record is empty only when BOTH words are 0xFFFF - voice.hed entry 7850 is
``ff ff 1b 30`` = sector 0x3FFFF, a real entry).

Both store only 20 bits of the sector offset (2 GiB reach).  Archives larger
than that (allpac0/1/1_2) wrap around; the engine (eboot.bin, table builder at
text VA 0xE5538, offset lookup at VA 0xE5CE8) recovers the missing bits with
"banks": scanning the records in order, every record whose nibble (bits
16..19 of the sector offset) is 0 while the previous record's nibble was
non-zero starts a new bank (at most 11 bank switches are stored, so 12 banks
= 24 GiB).  full_sector = stored20 | (bank << 20).  On every retail file this
equals the running sum of size_sectors and the last entry ends exactly at the
.mrg file size.

Terminator: 16 bytes of 0xFF (two HED8 or four HED4 records), absent in
bgm.hed; parsing stops at the first record that is entirely 0xFF.  Layout
originally from mangetsu (MIT, include/mg/data/mrg.hpp), which knows HED8 only
and treats the offset field as a plain u32 sector number (wrong as soon as an
archive exceeds 0x10000 sectors = 128 MiB, where bits 28..31 become non-zero);
everything here was re-derived from the PS4 data.
"""
from __future__ import annotations

import mmap
import os
import struct
from dataclasses import dataclass
from enum import Enum
from pathlib import Path
from typing import Iterator, Optional, Union

from .mzp import SECTOR, decode_size
from .nam import NamTable, load_nam

__all__ = [
    "SECTOR",
    "HedFormat",
    "HedEntry",
    "HedTable",
    "MrgEntry",
    "MrgArchive",
    "MrgError",
    "parse_hed",
    "detect_hed_format",
]

Buffer = Union[bytes, bytearray, memoryview]
OFFSET_BITS = 20
OFFSET_MASK = (1 << OFFSET_BITS) - 1
ENGINE_MAX_BANK_SWITCHES = 11


class MrgError(ValueError):
    pass


class HedFormat(str, Enum):
    HED8 = "hed8"
    HED4 = "hed4"


@dataclass(frozen=True)
class HedEntry:
    index: int
    raw: bytes                 # the record bytes as stored
    stored_offset: int         # the 20 stored bits of the sector offset
    sector_offset: int         # full reconstructed sector offset
    size_sectors: int
    size_lo: Optional[int]     # HED8 only

    @property
    def offset(self) -> int:
        return self.sector_offset * SECTOR

    @property
    def size_exact(self) -> bool:
        return self.size_lo is not None

    @property
    def size(self) -> int:
        """Exact byte size when known (HED8), else the sector-rounded size."""
        if self.size_lo is None:
            return self.size_sectors * SECTOR
        return decode_size(self.size_sectors, self.size_lo)


@dataclass
class HedTable:
    format: HedFormat
    entries: list[HedEntry]
    terminator: bytes          # raw terminator bytes (b"" if absent)
    trailing: bytes            # anything after the terminator
    contiguous: bool           # every entry starts where the previous ended
    bank_starts: list[int]     # entry index where bank 1, 2, ... begins (engine rule)


def _rec_is_ff(rec: bytes) -> bool:
    return rec == b"\xff" * len(rec)


def detect_hed_format(data: Buffer) -> HedFormat:
    """HED8 iff the size is a multiple of 8 and, for every non-terminator
    record, bits 16..27 of the first u32 are zero; otherwise HED4."""
    data = bytes(data)
    if len(data) % 8 == 0:
        ok = True
        for off in range(0, len(data), 8):
            rec = data[off:off + 8]
            if _rec_is_ff(rec):
                break
            if struct.unpack_from("<I", rec)[0] & 0x0FFF0000:
                ok = False
                break
        if ok:
            return HedFormat.HED8
    if len(data) % 4 == 0:
        return HedFormat.HED4
    raise MrgError(f"HED size {len(data)} matches no known layout")


def parse_hed(data: Buffer, fmt: Optional[HedFormat] = None) -> HedTable:
    data = bytes(data)
    if fmt is None:
        fmt = detect_hed_format(data)
    rsize = 8 if fmt == HedFormat.HED8 else 4
    if len(data) % rsize:
        raise MrgError(f"HED size {len(data)} is not a multiple of {rsize}")
    entries: list[HedEntry] = []
    term_at = len(data)
    prev_end = 0
    contiguous = True
    bank = 0
    bank_starts: list[int] = []
    prev_nibble_nonzero = False
    for idx, off in enumerate(range(0, len(data), rsize)):
        rec = data[off:off + rsize]
        if _rec_is_ff(rec):
            term_at = off
            break
        if fmt == HedFormat.HED8:
            field, ss, lo = struct.unpack("<IHH", rec)
            if field & 0x0FFF0000:
                raise MrgError(f"HED8 record {idx}: reserved offset bits set (0x{field:08x})")
            stored = (field & 0xFFFF) | ((field >> 28) << 16)
            size_lo: Optional[int] = lo
        else:
            lo16, packed = struct.unpack("<HH", rec)
            stored = lo16 | ((packed >> 12) << 16)
            ss = packed & 0x0FFF
            size_lo = None
        # engine bank rule: nibble back to 0 after a non-zero nibble = new bank
        nibble = stored >> 16
        if nibble:
            prev_nibble_nonzero = True
        else:
            if prev_nibble_nonzero:
                bank += 1
                bank_starts.append(idx)
            prev_nibble_nonzero = False
        full = stored | (bank << OFFSET_BITS)
        if full != prev_end:
            contiguous = False
        entries.append(HedEntry(idx, rec, stored, full, ss, size_lo))
        prev_end = full + ss
    if bank > ENGINE_MAX_BANK_SWITCHES:
        raise MrgError(f"{bank} bank switches; the engine supports at most {ENGINE_MAX_BANK_SWITCHES}")
    tail = data[term_at:]
    # terminator = the run of 0xFF records
    tlen = 0
    while tlen < len(tail) and _rec_is_ff(tail[tlen:tlen + rsize]) and len(tail[tlen:tlen + rsize]) == rsize:
        tlen += rsize
    return HedTable(fmt, entries, tail[:tlen], tail[tlen:], contiguous, bank_starts)


@dataclass(frozen=True)
class MrgEntry:
    index: int
    name: Optional[str]
    offset: int                # byte offset in the .mrg
    size: int                  # exact size (HED8) or sector-rounded size (HED4)
    size_exact: bool
    sector_offset: int
    size_sectors: int
    size_lo: Optional[int]
    hed_raw: bytes

    @property
    def end(self) -> int:
        return self.offset + self.size

    @property
    def span(self) -> int:
        return self.size_sectors * SECTOR


class MrgArchive:
    """Lazy, mmap-backed reader for a HED/NAM/MRG triplet.

    >>> with MrgArchive("…/image/allpac0") as a:
    ...     a.entries[0].name, a.read(0)[:6]
    ('BLACK.MZP', b'mrgd00')
    """

    def __init__(self, base: Union[str, Path], hed_format: Optional[HedFormat] = None,
                 nam_path: Union[str, Path, None] = None, use_nam: bool = True) -> None:
        base = Path(base)
        if base.suffix.lower() in (".hed", ".mrg", ".nam"):
            base = base.with_suffix("")
        self.base = base
        self.hed_path = base.with_suffix(".hed")
        self.mrg_path = base.with_suffix(".mrg")
        self.hed = parse_hed(self.hed_path.read_bytes(), hed_format)
        self.nam: Optional[NamTable] = None
        npath = Path(nam_path) if nam_path else base.with_suffix(".nam")
        if use_nam and npath.exists():
            self.nam = load_nam(npath)
        self.file_size = os.path.getsize(self.mrg_path)
        self._f = open(self.mrg_path, "rb")
        self._mm: Optional[mmap.mmap] = mmap.mmap(self._f.fileno(), 0, access=mmap.ACCESS_READ)
        names = self.nam.names if self.nam else None
        self.entries: list[MrgEntry] = []
        for e in self.hed.entries:
            name = names[e.index] if names is not None and e.index < len(names) else None
            self.entries.append(MrgEntry(e.index, name, e.offset, e.size, e.size_exact,
                                         e.sector_offset, e.size_sectors, e.size_lo, e.raw))
        self._by_name: Optional[dict[str, int]] = None

    # -- lifecycle ------------------------------------------------------------
    def close(self) -> None:
        if self._mm is not None:
            self._mm.close()
            self._mm = None
        self._f.close()

    def __enter__(self) -> "MrgArchive":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    # -- metadata -------------------------------------------------------------
    @property
    def format(self) -> HedFormat:
        return self.hed.format

    @property
    def mm(self) -> mmap.mmap:
        if self._mm is None:
            raise MrgError("archive is closed")
        return self._mm

    def __len__(self) -> int:
        return len(self.entries)

    def __iter__(self) -> Iterator[MrgEntry]:
        return iter(self.entries)

    def names_aligned(self) -> bool:
        """True if the NAM table has exactly one name per entry."""
        return self.nam is not None and len(self.nam) == len(self.entries)

    def find(self, name: str) -> MrgEntry:
        """Look up an entry by NAM name (case-insensitive; first match)."""
        if self._by_name is None:
            self._by_name = {}
            for e in self.entries:
                if e.name is not None:
                    self._by_name.setdefault(e.name.upper(), e.index)
        try:
            return self.entries[self._by_name[name.upper()]]
        except KeyError:
            raise KeyError(f"{name!r} not in {self.base.name}.nam") from None

    def validate(self) -> list[str]:
        """Structural checks: every extent inside the .mrg, contiguity, exact
        sizes consistent with the sector counts, names aligned."""
        issues: list[str] = []
        prev_end = 0
        for e in self.entries:
            if e.offset + e.span > self.file_size:
                issues.append(f"entry {e.index}: extent 0x{e.offset:x}+0x{e.span:x} beyond file size 0x{self.file_size:x}")
            if e.sector_offset != prev_end:
                issues.append(f"entry {e.index}: sector {e.sector_offset} != previous end {prev_end}")
            if e.size_exact and not (e.span - SECTOR < e.size <= e.span):
                issues.append(f"entry {e.index}: exact size 0x{e.size:x} inconsistent with {e.size_sectors} sectors")
            prev_end = e.sector_offset + e.size_sectors
        if prev_end * SECTOR != self.file_size:
            issues.append(f"last entry ends at 0x{prev_end * SECTOR:x}, file size 0x{self.file_size:x}")
        if self.nam is not None and len(self.nam) != len(self.entries):
            issues.append(f"NAM has {len(self.nam)} names for {len(self.entries)} entries")
        return issues

    # -- data -----------------------------------------------------------------
    def view(self, index: int) -> memoryview:
        """Zero-copy view of the entry's exact bytes (release it before close())."""
        e = self.entries[index]
        return memoryview(self.mm)[e.offset:e.offset + e.size]

    def read(self, index: int) -> bytes:
        e = self.entries[index]
        return self.mm[e.offset:e.offset + e.size]

    def read_sectors(self, index: int) -> bytes:
        """The full sector span (exact data + packer padding)."""
        e = self.entries[index]
        return self.mm[e.offset:e.offset + e.span]

    def head(self, index: int, n: int = 16) -> bytes:
        e = self.entries[index]
        return self.mm[e.offset:e.offset + min(n, e.size)]


def _main(argv: list[str] | None = None) -> int:
    import argparse

    ap = argparse.ArgumentParser(prog="python -m tsuki.mrg",
                                 description="List / extract entries of a HED/NAM/MRG archive")
    ap.add_argument("base", help="path without extension, or the .hed/.mrg path")
    ap.add_argument("--extract", metavar="DIR")
    ap.add_argument("-i", "--index", type=int, action="append",
                    help="only these entry indices (repeatable)")
    ap.add_argument("--limit", type=int, default=None, help="list at most N entries")
    args = ap.parse_intermixed_args(argv)
    with MrgArchive(args.base) as a:
        print(f"# {a.base.name}: format={a.format.value} entries={len(a)} "
              f"terminator={a.hed.terminator.hex() or '-'} contiguous={a.hed.contiguous} "
              f"bank_starts={a.hed.bank_starts} "
              f"nam={'%d names' % len(a.nam) if a.nam else 'none'}")
        issues = a.validate()
        print(f"# validation issues: {len(issues)}")
        for s in issues[:20]:
            print("#  ", s)
        sel = args.index if args.index else range(len(a))
        for k, i in enumerate(sel):
            if args.limit is not None and k >= args.limit:
                break
            e = a.entries[i]
            print(f"{e.index}\t{e.name or ''}\t0x{e.offset:x}\t0x{e.size:x}\t{'exact' if e.size_exact else 'sectors'}"
                  f"\t{a.head(i, 8).hex()}")
            if args.extract:
                os.makedirs(args.extract, exist_ok=True)
                fn = f"{e.index:05d}" + (f"_{e.name}" if e.name else "") + ".bin"
                with open(os.path.join(args.extract, fn), "wb") as o:
                    o.write(a.read(i))
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
