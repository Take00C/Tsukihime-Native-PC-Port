"""Extract NUL-terminated strings from eboot.bin with their VAs.

The eboot has no separate .rodata program header: read-only data (strings, jump
tables, float constants) lives at the end of the R-X text segment (VA 0x2C....),
plus RELRO (VA 0x400000) and the RW data segment (VA 0x600000).

CLI:  python -m eboot.strings [--min 4] [--grep REGEX] [--out file.tsv]
TSV columns: va (hex), segment, length, text (escaped with \\n \\t).
"""
from __future__ import annotations

import re
import sys
from pathlib import Path
from typing import Iterator, List, Optional, Tuple

from eboot.selfelf import Eboot


def all_strings(eb: Eboot, min_len: int = 4) -> Iterator[Tuple[int, str, str]]:
    for ph in eb.load_segments():
        seg = {1: "text"}.get(ph.type, "")
        if ph.type == 1 and ph.flags & 2:
            seg = "data"
        elif ph.type == 0x61000010:
            seg = "relro"
        for va, s in eb.iter_strings(ph, min_len):
            yield va, seg, s.decode("utf-8")


def _esc(s: str) -> str:
    return s.replace("\\", "\\\\").replace("\n", "\\n").replace("\t", "\\t").replace("\r", "\\r")


def _main(argv: List[str]) -> int:
    import argparse

    from eboot import eboot_path

    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--eboot")
    ap.add_argument("--min", type=int, default=4)
    ap.add_argument("--grep")
    ap.add_argument("--out")
    a = ap.parse_args(argv)
    p = Path(a.eboot) if a.eboot else eboot_path()
    if p is None:
        print("game not found", file=sys.stderr)
        return 2
    eb = Eboot.open(p)
    rx: Optional[re.Pattern[str]] = re.compile(a.grep) if a.grep else None
    out = open(a.out, "w", encoding="utf-8", newline="\n") if a.out else sys.stdout
    n = 0
    for va, seg, s in all_strings(eb, a.min):
        if rx and not rx.search(s):
            continue
        out.write(f"{va:08x}\t{seg}\t{len(s.encode('utf-8'))}\t{_esc(s)}\n")
        n += 1
    if a.out:
        out.close()
        print(f"{n} strings -> {a.out}")
    eb.close()
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
