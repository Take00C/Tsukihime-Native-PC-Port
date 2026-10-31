"""Reader for the PS4 save image (`savedata000/savedata000`, 4 MiB).

No retail save existed on this machine when the layout was recovered, so every
offset below comes from the game's own code (text VAs given; see
docs/formats/engine.md section 7).  The parser validates the magic words and
reports anything unexpected instead of guessing.

Save directory / file (VA 0x126140, 0x121030):
    dirName  = "%s%03d" % ("savedata", 0)      -> "savedata000"
    file     = <mount point> + "/" + dirName   -> savedata000/savedata000
    size     = 0x400000 bytes, written in 0x2000-byte chunks (sceKernelWrite)
    sce_sys/icon0.png = /app0/data/if_<lang>.png (VA 0x41AD0)

Image layout (VA 0xEACF0 writes, 0xEAC80 reads, 0x6FEC0 commits):
    0x000000  system block, 0x18AC bytes used (u16 magic 0x2AF8 at +0)
    0x001A00  slot[i] at 0x1A00 + i*0x7140, 0x7108 bytes each, i = 0..139
    0x3E0B80  .. 0x400000 unused (zero)

All integers little-endian.
"""
from __future__ import annotations

import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import List, Optional

SAVE_SIZE = 0x400000
MAGIC = 0x2AF8                 # 11000; written at the start of every stored block
SYSTEM_OFF = 0x0
SYSTEM_SIZE = 0x18AC
SLOT_BASE = 0x1A00
SLOT_STRIDE = 0x7140
SLOT_SIZE = 0x7108
NUM_SLOTS = 140                # loop bound 0x8C at VA 0x6FF0B
MENU_SLOTS = 139               # save/load menu accepts slot ids 0..0x8A (VA 0x70473)
SNAPSHOT_SIZE = 0x3A10         # one "history" state (also the rewind ring entry size)
SNAPSHOT_B_SIZE = 0x36A8

# system block fields: (offset, size, RAM source, meaning).  Together they cover every
# byte of the 0x18AC-byte block (checked by tests): SaveImage_Commit (VA 0x6FEC0) copies
# the staging block 0x1201660 after filling the listed parts; SaveImage_LoadSystem
# (VA 0x6F470) reads them back.
SYSTEM_FIELDS = [
    (0x0000, 2, None, "u16 magic 0x2AF8 (stamped by SaveImage_WriteBlock)"),
    (0x0002, 2, None, "u16 = 1 (written by 0x6FEC0; the loader requires != 0)"),
    (0x0004, 0x140, 0x66C578, "config block (volumes, text speed, language at +0x24 ...)"),
    (0x0144, 0x3E8, 0x164C620, "G variables G[0..499]: 500 x u16 (persistent script flags)"),
    (0x052C, 0x20C, 0x164D5C0, "unknown block (RAM 0x164D5C0, right after var_G); the loader ORs "
                               "+0x52C..+0x72B (0x200 bytes) into RAM and reads +0x72C..+0x737 bytewise"),
    (0x0738, 0xFB4, 0x17DA278, "readmax[4020]: highest read line per text block (engine.md 5.6)"),
    (0x16EC, 2, None, "not written by the commit (zero)"),
    (0x16EE, 2, 0x1202D4E, "u16 index of the most recent slot (for 'Continue'; <= 0x8A when used)"),
    (0x16F0, 2, None, "not written by the commit (zero)"),
    (0x16F2, 2, 0x1202F16, "u16 (copied from RAM 0x1202F16; meaning unknown)"),
    (0x16F4, 0x118, 0x164FBC0, "unknown block (RAM 0x164FBC0); the loader ORs +0x16F4..+0x1803 "
                               "(0x110 bytes) into RAM and reads +0x1804..+0x180B bytewise"),
    (0x180C, 0xA0, None, "not written by any code found (zero in a game-made image)"),
]

# slot record fields (offset, size, meaning); together they cover all 0x7108 bytes (tests)
SLOT_FIELDS = [
    (0x0000, 2, "u16 magic 0x2AF8"),
    (0x0002, 2, "u16 used = 1 (Slot_Store writes the dword 0x00012AF8 at +0)"),
    (0x0004, 4, "i32 has state B: 1, else <= 0 (Staging_SetSnapshot 0x73980)"),
    (0x0008, 0x3A10, "state A: one history record (engine.md 5.6)"),
    (0x3A18, 0x36A8, "state B: first 0x36A8 bytes of a second record, present when +4 = 1"),
    (0x70C0, 2, "u16 year"),
    (0x70C2, 5, "u8 month (1..12), day, hour, minute, second (Rtc_LocalTime 0xEAD50)"),
    (0x70C7, 1, "padding (copied from the staging record, never written)"),
    (0x70C8, 2, "u16 end marker 0x2AF8"),
    (0x70CA, 0x3E, "not written by any code found (zero)"),
]


@dataclass
class SlotTime:
    year: int
    month: int
    day: int
    hour: int
    minute: int
    second: int

    def __str__(self) -> str:
        return (f"{self.year:04d}-{self.month:02d}-{self.day:02d} "
                f"{self.hour:02d}:{self.minute:02d}:{self.second:02d}")


@dataclass
class Slot:
    index: int
    offset: int
    magic: int
    used: int
    has_snapshot_b: int
    time: Optional[SlotTime]
    end_magic: int
    script_index: Optional[int] = None    # snapshot A +0x18 (allscr section = index + 3)
    script_offset: Optional[int] = None   # snapshot A +0x08 (byte offset in the decoded script)

    @property
    def present(self) -> bool:
        return self.magic == MAGIC and self.used > 0


@dataclass
class SaveImage:
    size: int
    system_magic: int
    system_version: int
    latest_slot: int
    slots: List[Slot] = field(default_factory=list)
    issues: List[str] = field(default_factory=list)


def parse_slot(buf: bytes, i: int) -> Slot:
    off = SLOT_BASE + i * SLOT_STRIDE
    r = buf[off:off + SLOT_SIZE]
    magic, used, has_b = struct.unpack_from("<HHi", r, 0)
    year, mon, day, hour, minute, sec = struct.unpack_from("<HBBBBB", r, 0x70C0)
    end_magic = struct.unpack_from("<H", r, 0x70C8)[0]
    t = SlotTime(year, mon, day, hour, minute, sec) if magic == MAGIC else None
    s = Slot(i, off, magic, used, has_b, t, end_magic)
    if magic == MAGIC:
        snap = r[8:8 + SNAPSHOT_SIZE]
        s.script_offset = struct.unpack_from("<Q", snap, 0x08)[0]
        s.script_index = struct.unpack_from("<H", snap, 0x18)[0]
    return s


def parse(buf: bytes) -> SaveImage:
    """Parse a save image.  A file of the wrong size is reported in `issues` and parsed
    as if zero-padded/truncated to 4 MiB; `size` is always the real input size."""
    issues: List[str] = []
    real_size = len(buf)
    if real_size != SAVE_SIZE:
        issues.append(f"size 0x{real_size:x} != 0x{SAVE_SIZE:x}")
        buf = buf[:SAVE_SIZE].ljust(SAVE_SIZE, b"\0")
    sm, sv = struct.unpack_from("<HH", buf, 0)
    latest = struct.unpack_from("<H", buf, 0x16EE)[0]
    if sm not in (0, MAGIC):
        issues.append(f"system magic 0x{sm:04x}")
    img = SaveImage(real_size, sm, sv, latest, issues=issues)
    for i in range(NUM_SLOTS):
        s = parse_slot(buf, i)
        if s.magic not in (0, MAGIC):
            issues.append(f"slot {i}: magic 0x{s.magic:04x}")
        if s.magic == MAGIC and s.end_magic != MAGIC:
            issues.append(f"slot {i}: end magic 0x{s.end_magic:04x}")
        img.slots.append(s)
    tail = buf[SLOT_BASE + NUM_SLOTS * SLOT_STRIDE:]
    if any(tail):
        issues.append(f"non-zero bytes after the last slot ({sum(1 for b in tail if b)})")
    return img


def build_blank() -> bytes:
    """A save image as the game creates it on first boot: zero-filled (VA 0xEA5D0
    memsets the 4 MiB buffer) with the system block magic written by the commit."""
    b = bytearray(SAVE_SIZE)
    struct.pack_into("<HH", b, 0, MAGIC, 1)
    return bytes(b)


def default_save_path() -> Optional[Path]:
    import os

    appdata = os.environ.get("APPDATA")
    if not appdata:
        return None
    p = Path(appdata) / "shadPS4/home/1000/savedata/CUSA47999/savedata000/savedata000"
    return p if p.is_file() else None


def _main(argv: List[str]) -> int:
    import argparse

    ap = argparse.ArgumentParser(description="Summarise a TSUKIHIME PS4 save image")
    ap.add_argument("path", nargs="?", help="savedata000 file (default: shadPS4 user 1000)")
    a = ap.parse_args(argv)
    p = Path(a.path) if a.path else default_save_path()
    if p is None:
        print("no save file found", file=sys.stderr)
        return 2
    img = parse(p.read_bytes())
    print(f"{p}: size 0x{img.size:x}, system magic 0x{img.system_magic:04x} "
          f"v{img.system_version}, latest slot {img.latest_slot}")
    for s in img.slots:
        if s.present:
            print(f"  slot {s.index:3d} @0x{s.offset:06x} {s.time} script {s.script_index} "
                  f"offset 0x{s.script_offset or 0:x} snapshotB={s.has_snapshot_b}")
    for i in img.issues:
        print("  issue:", i)
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
