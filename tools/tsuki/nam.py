"""Name tables (.nam files and NAM-style sections inside MZP containers).

Two record layouts exist on the PS4 release (see docs/formats/containers.md):

* NAM32 - 32-byte records (allpac*.nam, allscr.mrg section 0, se_nam.mrg
  sections).  Bytes 0..: ASCII name, NUL padded; bytes 30..31 = "\\r\\n".
  Some tables (se_nam.mrg sections 0, 1, 3) carry a 2-byte tag at bytes 28..29
  equal to the lower-cased first two characters of the name (e.g. "SE_A0018"
  -> "se").  The table ends with one all-zero record (terminator).
  Learned from mangetsu (MIT, src/data/nam.cpp); independent implementation.

* NAM8 - 8-byte records (voice.nam, voice2.nam): ASCII name, NUL padded, no
  CRLF; the file ends with 2 zero bytes (16464*8 + 2 = 131714 bytes).  The
  names are *group prefixes* such as "A00_" shared by many entries, not unique
  file names.

Record i of a table names entry i of the matching archive.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Union

__all__ = [
    "NamRecord",
    "NamTable",
    "parse_nam32",
    "parse_nam8",
    "load_nam",
    "looks_like_nam32",
    "NamError",
]

Buffer = Union[bytes, bytearray, memoryview]


class NamError(ValueError):
    pass


@dataclass(frozen=True)
class NamRecord:
    index: int
    name: str
    raw: bytes
    tag: str = ""          # NAM32 only: optional 2-char tag at bytes 28..29


@dataclass
class NamTable:
    record_size: int                     # 32 or 8
    records: list[NamRecord] = field(default_factory=list)
    terminator: bool = False             # trailing all-zero record present (NAM32)
    trailing: bytes = b""                # bytes after the last full record (NAM8: b"\0\0")

    @property
    def names(self) -> list[str]:
        return [r.name for r in self.records]

    def __len__(self) -> int:
        return len(self.records)

    def __getitem__(self, i: int) -> str:
        return self.records[i].name


def _decode_name(b: bytes) -> str:
    try:
        return b.decode("ascii")
    except UnicodeDecodeError as e:
        raise NamError(f"non-ASCII name bytes {b!r}") from e


def looks_like_nam32(data: Buffer) -> bool:
    """True if ``data`` is a sequence of 32-byte CRLF-terminated name records
    (optionally ending with one all-zero record)."""
    n = len(data)
    if n == 0 or n % 32:
        return False
    mv = memoryview(data)
    for off in range(0, n, 32):
        rec = bytes(mv[off:off + 32])
        if rec[30:32] == b"\r\n":
            continue
        if off == n - 32 and not any(rec):
            continue
        return False
    return True


def parse_nam32(data: Buffer) -> NamTable:
    """Parse a 32-byte-record name table.

    A record is: name (ASCII, terminated by the first NUL inside bytes 0..29),
    optional 2-byte tag at bytes 28..29 (only when bytes name_end..27 are all
    zero), CR LF at bytes 30..31.  An all-zero record ends the table; anything
    after it is kept in ``trailing``.
    """
    data = bytes(data)
    if len(data) % 32:
        raise NamError(f"NAM32 size {len(data)} is not a multiple of 32")
    table = NamTable(record_size=32)
    for idx, off in enumerate(range(0, len(data), 32)):
        rec = data[off:off + 32]
        if not any(rec):
            table.terminator = True
            table.trailing = data[off + 32:]
            break
        if rec[30:32] != b"\r\n":
            raise NamError(f"record {idx} at 0x{off:x} does not end with CRLF: {rec!r}")
        body = rec[:30]
        nul = body.find(b"\0")
        name_b = body if nul < 0 else body[:nul]
        tag = ""
        if nul >= 0 and nul <= 28 and not any(body[nul:28]) and any(body[28:30]):
            tag = _decode_name(body[28:30])
        table.records.append(NamRecord(idx, _decode_name(name_b), rec, tag))
    return table


def parse_nam8(data: Buffer) -> NamTable:
    """Parse an 8-byte-record name table (voice.nam / voice2.nam)."""
    data = bytes(data)
    full = len(data) // 8 * 8
    table = NamTable(record_size=8, trailing=data[full:])
    for idx, off in enumerate(range(0, full, 8)):
        rec = data[off:off + 8]
        if not any(rec):
            table.terminator = True
            table.trailing = data[off:]
            break
        name_b = rec.split(b"\0", 1)[0]
        table.records.append(NamRecord(idx, _decode_name(name_b), rec))
    return table


def load_nam(path: Union[str, Path]) -> NamTable:
    """Load a .nam file, auto-detecting the record layout."""
    data = Path(path).read_bytes()
    if looks_like_nam32(data):
        return parse_nam32(data)
    return parse_nam8(data)


def _main(argv: list[str] | None = None) -> int:
    import argparse

    ap = argparse.ArgumentParser(prog="python -m tsuki.nam", description="Print a NAM table")
    ap.add_argument("path")
    args = ap.parse_intermixed_args(argv)
    t = load_nam(args.path)
    print(f"# record_size={t.record_size} records={len(t)} terminator={t.terminator} "
          f"trailing={t.trailing.hex()}")
    for r in t.records:
        print(f"{r.index}\t{r.name}" + (f"\t[{r.tag}]" if r.tag else ""))
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
