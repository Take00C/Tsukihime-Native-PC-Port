"""Export the UI text table embedded in eboot.bin.

The system/menu strings (config help lines, dialog messages, music-room titles,
chapter and flowchart scene titles, flowchart synopses) are not in script_text.mrg.
The engine keeps them in one table of string pointers in RELRO:

    ui_text[5][1499]  at VA 0x41D5F0 (7,495 x u64 pointers, prelinked in the file and
                      also covered by R_X86_64_RELATIVE relocations)
    language index    0 ja, 1 en, 2 zc (Simplified), 3 zt (Traditional), 4 ko
    string id         1..1498; id 0 is a placeholder ("NULL" / "NULL ") in every language

and reads it through UiText_Get (text VA 0xC29B0):

    if not 1 <= id <= 0x5DA: return NULL
    i = config.language (i16 at 0x66C578 + 0x24) * 0x5DB + id
    if i > 0x1D47: return NULL
    return ui_text_flat[i]

(UiText_GetLang 0xC29F0 is the same with the language as a second argument.)  The
strings themselves are NUL-terminated UTF-8 in the data segment, packed per language
(JA 0xD63F38.., EN 0xD7BFCE.., ZC 0xD8DF2C.., ZT 0xDA0AAA.., KO 0xDB3CE7..); the Korean
column holds only placeholders ("한글 system message N", "Text") because this build has
no Korean UI.  '^' is the line-break marker used by the text renderer.

Every one of the 5 x 1499 entries is exported; nothing is filtered.  A pointer that
does not resolve or a string that is not valid UTF-8 raises ValueError.

CLI:  python -m eboot.uitext --out DIR      -> DIR/ui_text_<lang>.tsv
      columns: id, string VA (hex), text; the text escapes '\\' as '\\\\', TAB as '\\t',
      LF as '\\n' and CR as '\\r' (none of these occur in v01.01).
"""
from __future__ import annotations

import struct
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Tuple

from eboot.selfelf import R_X86_64_RELATIVE, Eboot

UI_TABLE_VA = 0x41D5F0       # RELRO: char *ui_text[5][1499]
UI_GETTER_VA = 0xC29B0       # UiText_Get(id)
UI_COUNT = 1499              # entries per language (0x5DB); valid ids 1..1498
LANGS: Tuple[str, ...] = ("ja", "en", "zc", "zt", "ko")


@dataclass
class UiString:
    lang: str
    id: int
    va: int
    text: str


def read_table(eb: Eboot) -> Dict[str, List[UiString]]:
    """All 5 x 1499 entries, indexed [lang][id] (id 0 = placeholder)."""
    rel = {r.offset: r.addend for r in eb.rela() if r.type == R_X86_64_RELATIVE}
    raw = eb.read_va(UI_TABLE_VA, 8 * UI_COUNT * len(LANGS))
    out: Dict[str, List[UiString]] = {}
    for li, lang in enumerate(LANGS):
        rows: List[UiString] = []
        for i in range(UI_COUNT):
            slot = UI_TABLE_VA + 8 * (li * UI_COUNT + i)
            ptr = struct.unpack_from("<Q", raw, 8 * (li * UI_COUNT + i))[0]
            if rel.get(slot) != ptr:
                raise ValueError(f"ui_text[{lang}][{i}] at 0x{slot:x}: file value 0x{ptr:x}, "
                                 f"relocation {rel.get(slot)}")
            b = eb.cstr(ptr)
            try:
                s = b.decode("utf-8")
            except UnicodeDecodeError as e:
                raise ValueError(f"ui_text[{lang}][{i}] at 0x{ptr:x}: {e}") from e
            rows.append(UiString(lang, i, ptr, s))
        out[lang] = rows
    return out


def iter_block(eb: Eboot, lang: str) -> List[Tuple[int, str]]:
    """(VA, text) of ids 1..1498 of one language (compatibility helper)."""
    return [(u.va, u.text) for u in read_table(eb)[lang][1:]]


def escape(s: str) -> str:
    return s.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r")


def _main(argv: List[str]) -> int:
    import argparse

    from eboot import eboot_path

    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--out", required=True)
    a = ap.parse_args(argv)
    p = eboot_path()
    if p is None:
        print("game not found", file=sys.stderr)
        return 2
    eb = Eboot.open(p)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    tab = read_table(eb)
    for lang, rows in tab.items():
        with open(out / f"ui_text_{lang}.tsv", "w", encoding="utf-8", newline="\n") as f:
            f.write("id\tva\ttext\n")
            for u in rows:
                f.write(f"{u.id}\t{u.va:08x}\t{escape(u.text)}\n")
        lo = min(u.va for u in rows)
        hi = max(u.va + len(u.text.encode()) + 1 for u in rows)
        print(f"{lang}: {len(rows)} entries (ids 0..{len(rows) - 1}), strings 0x{lo:x}..0x{hi:x}, "
              f"id0={rows[0].text!r}")
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
