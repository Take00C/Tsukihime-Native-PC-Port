"""The PS4 save / load list and its Delete Data (save/load screen 0x71C90), read from eboot.bin (CUSA47999 v01.01).

Package K45 (issue #10, docs/formats/adv_state.md 7.8).  Two checks:

* :func:`save_list` runs the list builder 0x711F0 in the unicorn harness of
  ``tsuki.misc_adv_system_state_emu`` on a synthetic slot array and returns the list it
  builds (0x1202F20, count 0x120A2F0).  It scans slots 0..99 only: auto saves (100..138)
  and the quick save (139) never enter either list.
* :func:`delete_path` disassembles TRIANGLE's branch in state 2 (0x73107 -> 0x732E6) up to
  the dialog (0x7332E) and the slot clear of state 23 (0x726E3..0x7274F), so a test can
  check that the delete tests only the entry's used word, never its slot number.

CLI: ``python -m tsuki.save_list_emu`` prints both lists for slots 3, 50, 99, 100, 120, 138, 139.
"""
from __future__ import annotations

import calendar
import struct
from pathlib import Path
from typing import List, Optional, Sequence, Tuple, Union

from tsuki import misc_adv_system_state_emu as emu_mod

SLOTS, STRIDE = 0xE24600, 0x7108
LIST, COUNT, USED, MODE = 0x1202F20, 0x120A2F0, 0x120A2F4, 0x1202F10
BUILDER = 0x711F0
_MKTIME, _NEW_NOTHROW = 0x173740, 0x173510
# the builder, its sort (0x73C30 and the helpers it calls): VEX forms to patch
_FUNCS = (0x711F0, 0x73C30, 0x73E40, 0x74097)

Date = Tuple[int, int, int, int, int, int]


class _ListEmu(emu_mod.AdvEmu):
    """The harness plus the two imports the builder uses: mktime (UTC, enough to order dates) and operator new(nothrow)."""

    def _plt(self, uc: object, addr: int, size: int, x: object) -> None:
        if addr == _MKTIME:
            tm = struct.unpack("<6i", self.rd(self.r("rdi"), 24))
            sec, mi, h, d, mo, y = tm
            self._return(calendar.timegm((y + 1900, mo + 1, d, h, mi, sec)))
            return
        if addr == _NEW_NOTHROW:
            a = (self._heap + 15) & ~15
            self._heap = a + max(self.r("rdi"), 16)
            self._return(a)
            return
        super()._plt(uc, addr, size, x)


def save_list(used: Sequence[Tuple[int, Date]], save: bool, eboot: Union[str, Path, None] = None) -> List[int]:
    """The list 0x711F0 builds when ``used`` (slot, date) are the stored slots: the save list (``save``, mode 1:
    the first free slot first) or the load list (mode 0), newest first."""
    e = _ListEmu(emu_mod._eboot_path(eboot))
    e.patch_functions(list(_FUNCS))
    e.stub(0xEA970, 1)                                   # the save data is present
    e.wr(SLOTS, bytes(140 * STRIDE))
    for slot, (y, mo, d, h, mi, s) in used:
        base = SLOTS + slot * STRIDE
        e.wr(base, struct.pack("<HH", 0x2AF8, 1))
        e.wr(base + 0x70C0, struct.pack("<HBBBBB", y, mo, d, h, mi, s))
        e.wr(base + 0x70C8, struct.pack("<H", 0x2AF8))
    e.wr(MODE, struct.pack("<i", 1 if save else 0))
    e.call(BUILDER)
    n = e.i32(COUNT)
    return [e.i32(LIST + 4 * k) for k in range(n)]


def delete_path(eboot: Union[str, Path, None] = None) -> Tuple[List[Tuple[int, str, str]], List[Tuple[int, str, str]]]:
    """(TRIANGLE's branch 0x732E6..0x73333, state 23's clear 0x726E3..0x7274F) as (va, mnemonic, operands)."""
    from eboot.disasm import disasm_range
    from eboot.selfelf import Eboot
    eb = Eboot.open(emu_mod._eboot_path(eboot))
    branch = [(i.addr, i.mnem, i.ops) for i in disasm_range(eb, 0x732E6, 0x73333)]
    clear = [(i.addr, i.mnem, i.ops) for i in disasm_range(eb, 0x726E3, 0x7274F)]
    return branch, clear


EXAMPLE: Sequence[Tuple[int, Date]] = (
    (3, (2026, 11, 1, 12, 0, 0)), (50, (2026, 11, 5, 12, 0, 0)), (99, (2026, 11, 2, 12, 0, 0)),
    (100, (2026, 11, 9, 12, 0, 0)), (120, (2026, 11, 8, 12, 0, 0)), (138, (2026, 11, 7, 12, 0, 0)),
    (139, (2026, 11, 10, 12, 0, 0)),
)


def main(argv: Optional[Sequence[str]] = None) -> int:
    print("stored:", [s for s, _ in EXAMPLE])
    print("load list (mode 0):", save_list(EXAMPLE, save=False))
    print("save list (mode 1):", save_list(EXAMPLE, save=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
