"""MZP containers (magic "mrgd00").

Self-describing archive used for every allpac*/parts entry, inside allpac3
entries (nested), and by the standalone files allscr.mrg, script_text.mrg,
ft_*.mrg and se_nam.mrg.  Layout learned from mangetsu (MIT,
include/mg/data/mzp.hpp); the size decoding here fixes a 64 KiB wrap bug in
mangetsu's ``entry_data_size`` (see docs/formats/containers.md).

All integers little-endian::

    0x00  char[6]  "mrgd00"
    0x06  u16      section count N
    0x08  N x 8-byte section records:
          +0 u16 sector_offset   (units of 0x800 bytes)
          +2 u16 byte_offset     (bits 0..11: byte offset, 0..0x7FF observed;
                                  bits 12..15: sector_offset bits 16..19 per
                                  the engine reader at eboot VA 0xE6DA0 - never
                                  non-zero in retail data)
          +4 u16 size_sectors    (= ceil((byte_offset + size) / 0x800), i.e. the
                                  number of sectors touched counting from
                                  sector_offset)
          +6 u16 size_lo         (= size & 0xFFFF)
    0x08+8N        data area; section i starts at
                   data_start + sector_offset*0x800 + byte_offset

Exact size: ``s = (size_sectors*0x800 & ~0xFFFF) | size_lo``; if
``s > size_sectors*0x800`` then ``s -= 0x10000``.

Packing rule observed on every PS4 file: sections are stored in index order,
section 0 at data_start, every following section at the next 8-byte boundary
*strictly after* the previous section's end (1..8 bytes of 0xFF padding), and
the container itself ends with the same 1..8-byte 0xFF pad.
"""
from __future__ import annotations

import mmap
import struct
from dataclasses import dataclass
from typing import Iterator, Optional, Union

__all__ = [
    "MZP_MAGIC",
    "SECTOR",
    "MzpError",
    "MzpSection",
    "MzpArchive",
    "is_mzp",
    "decode_size",
    "pad_after",
]

MZP_MAGIC = b"mrgd00"
SECTOR = 0x800

Buffer = Union[bytes, bytearray, memoryview, mmap.mmap]


class MzpError(ValueError):
    pass


def decode_size(size_sectors: int, size_lo: int) -> int:
    """Exact byte size from (sector count, low 16 bits).  Shared with HED8."""
    cap = size_sectors * SECTOR
    size = (cap & ~0xFFFF) | size_lo
    if size > cap:
        size -= 0x10000
    return size


def pad_after(end: int) -> int:
    """Number of 0xFF padding bytes the packer puts after a section ending at
    ``end`` (relative to the container start): 1..8, up to an 8-byte boundary."""
    return 8 - (end % 8)


def is_mzp(data: Buffer, offset: int = 0) -> bool:
    return len(data) >= offset + 8 and bytes(data[offset:offset + 6]) == MZP_MAGIC


@dataclass(frozen=True)
class MzpSection:
    index: int
    sector_offset: int
    byte_offset: int
    size_sectors: int
    size_lo: int
    offset: int        # byte offset of the data relative to the container start
    size: int          # exact size in bytes

    @property
    def in_sector_offset(self) -> int:
        return self.byte_offset & 0x0FFF

    @property
    def end(self) -> int:
        return self.offset + self.size

    @property
    def raw_record(self) -> bytes:
        return struct.pack("<4H", self.sector_offset, self.byte_offset,
                           self.size_sectors, self.size_lo)


class MzpArchive:
    """Read-only view of an MZP container living in ``buf[base:base+length]``.

    ``buf`` may be bytes, a memoryview or an mmap; nothing is copied until a
    section is requested.
    """

    def __init__(self, buf: Buffer, base: int = 0, length: Optional[int] = None) -> None:
        self._buf = buf
        self.base = base
        total = len(buf)
        self.length = (total - base) if length is None else length
        if self.length < 8 or base + self.length > total:
            raise MzpError(f"container window base=0x{base:x} length={length} outside buffer")
        head = bytes(buf[base:base + 8])
        if head[:6] != MZP_MAGIC:
            raise MzpError(f"bad magic {head[:6]!r} at 0x{base:x}")
        self.count: int = struct.unpack_from("<H", head, 6)[0]
        self.data_start: int = 8 + 8 * self.count
        if self.data_start > self.length:
            raise MzpError(f"section table ({self.count} records) exceeds container size {self.length}")
        table = bytes(buf[base + 8:base + self.data_start])
        secs: list[MzpSection] = []
        for i in range(self.count):
            so, bo, ss, lo = struct.unpack_from("<4H", table, 8 * i)
            off = self.data_start + so * SECTOR + (bo & 0x0FFF) + ((bo & 0xF000) << 15)
            size = decode_size(ss, lo)
            if off + size > self.length:
                raise MzpError(f"section {i} [0x{off:x}+0x{size:x}] exceeds container size 0x{self.length:x}")
            secs.append(MzpSection(i, so, bo, ss, lo, off, size))
        self.sections: list[MzpSection] = secs

    # -- access -------------------------------------------------------------
    def __len__(self) -> int:
        return self.count

    def __iter__(self) -> Iterator[MzpSection]:
        return iter(self.sections)

    def view(self, i: int) -> memoryview:
        s = self.sections[i]
        start = self.base + s.offset
        return memoryview(self._buf)[start:start + s.size]

    def read(self, i: int) -> bytes:
        s = self.sections[i]
        start = self.base + s.offset
        return bytes(self._buf[start:start + s.size])

    def head(self, i: int, n: int = 16) -> bytes:
        s = self.sections[i]
        start = self.base + s.offset
        return bytes(self._buf[start:start + min(n, s.size)])

    def is_nested(self, i: int) -> bool:
        s = self.sections[i]
        return s.size >= 8 and bytes(self._buf[self.base + s.offset:self.base + s.offset + 6]) == MZP_MAGIC

    def child(self, i: int) -> "MzpArchive":
        """Parse section ``i`` as a nested MZP container (no copy)."""
        s = self.sections[i]
        return MzpArchive(self._buf, self.base + s.offset, s.size)

    @property
    def end(self) -> int:
        """End of the last section's data (relative to the container start)."""
        return max((s.end for s in self.sections), default=self.data_start)

    @property
    def packed_size(self) -> int:
        """Size including the trailing 0xFF pad (what a HED8 size field holds)."""
        e = self.end
        return e + pad_after(e)

    # -- validation ---------------------------------------------------------
    def validate(self, check_padding: bool = True) -> list[str]:
        """Return a list of deviations from the observed packing rule."""
        issues: list[str] = []
        expected = self.data_start
        prev_end = self.data_start
        for s in self.sections:
            if s.in_sector_offset >= SECTOR:
                issues.append(f"section {s.index}: byte_offset 0x{s.byte_offset:x} >= 0x800")
            if s.size_sectors != -(-(s.in_sector_offset + s.size) // SECTOR):
                issues.append(f"section {s.index}: size_sectors {s.size_sectors} != "
                              f"ceil((byte_offset+size)/0x800)")
            if s.offset != expected:
                issues.append(f"section {s.index}: offset 0x{s.offset:x}, expected 0x{expected:x}")
            elif check_padding and s.index > 0:
                pad = bytes(self._buf[self.base + prev_end:self.base + s.offset])
                if pad.strip(b"\xff"):
                    issues.append(f"section {s.index}: padding before it is not 0xFF: {pad.hex()}")
            prev_end = s.end
            expected = s.end + pad_after(s.end)
        if check_padding and self.sections:
            e = self.end
            tail_len = min(pad_after(e), self.length - e)
            tail = bytes(self._buf[self.base + e:self.base + e + tail_len])
            if tail.strip(b"\xff"):
                issues.append(f"trailing pad not 0xFF: {tail.hex()}")
        return issues

    def describe(self) -> str:
        lines = [f"mrgd00 count={self.count} data_start=0x{self.data_start:x} end=0x{self.end:x} "
                 f"length=0x{self.length:x}"]
        for s in self.sections:
            lines.append(f"  [{s.index:4d}] off=0x{s.offset:08x} size=0x{s.size:08x} "
                         f"(sec={s.sector_offset} byte={s.byte_offset} ss={s.size_sectors} lo=0x{s.size_lo:04x}) "
                         f"{self.head(s.index, 8).hex()}")
        return "\n".join(lines)


def _main(argv: list[str] | None = None) -> int:
    import argparse
    import os

    ap = argparse.ArgumentParser(prog="python -m tsuki.mzp",
                                 description="List or extract the sections of an mrgd00 (MZP) file")
    ap.add_argument("path")
    ap.add_argument("--offset", type=lambda s: int(s, 0), default=0)
    ap.add_argument("--extract", metavar="DIR", help="write every section to DIR/<index>.bin")
    args = ap.parse_intermixed_args(argv)
    with open(args.path, "rb") as f:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            arc = MzpArchive(mm, args.offset)
            print(arc.describe())
            issues = arc.validate()
            print(f"# validation issues: {len(issues)}")
            for msg in issues[:20]:
                print("#  ", msg)
            if args.extract:
                os.makedirs(args.extract, exist_ok=True)
                for s in arc:
                    with open(os.path.join(args.extract, f"{s.index:04d}.bin"), "wb") as o:
                        o.write(arc.read(s.index))
        finally:
            mm.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
