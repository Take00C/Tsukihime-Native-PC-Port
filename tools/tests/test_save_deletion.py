"""Package K45 (issue #10): the PS4's save / load list and its Delete Data (tsuki.save_list_emu, docs/formats/adv_state.md 7.8).

Needs the game folder, capstone and unicorn (skips otherwise).  Total < 10 s.
"""
from __future__ import annotations

import pytest

from tsuki import game_dir

GAME = game_dir()


def _has(*mods: str) -> bool:
    try:
        for x in mods:
            __import__(x)
    except ImportError:
        return False
    return True


needs_capstone = pytest.mark.skipif(GAME is None or not _has("capstone"), reason="game or capstone missing")
needs_emu = pytest.mark.skipif(GAME is None or not _has("capstone", "unicorn"), reason="game or unicorn missing")


@needs_emu
def test_the_ps4_lists_hold_manual_slots_only() -> None:
    from tsuki import save_list_emu as s
    # stored: 3, 50, 99 (manual), 100, 120, 138 (auto), 139 (quick); the auto and quick saves are the newest
    assert s.save_list(s.EXAMPLE, save=False) == [50, 99, 3]
    assert s.save_list(s.EXAMPLE, save=True) == [0, 50, 99, 3]          # the first free slot, then newest first


@needs_capstone
def test_delete_data_checks_the_used_word_not_the_slot_number() -> None:
    from tsuki import save_list_emu as s
    branch, clear = s.delete_path()
    ops = " ; ".join(f"{m} {o}" for _a, m, o in branch)
    assert "dword ptr [rcx + rax*4]" in ops                              # the list entry under the cursor (0x1202F20)
    assert "word ptr [rax + r14 + 2], 0" in ops                          # its used word
    assert "edi, 0x17" in ops                                            # dialog 23
    assert not any(m == "cmp" and ("0x64" in o or "0x63" in o or "0x8a" in o or "0x8b" in o) for _a, m, o in branch)
    stores = [o for _a, m, o in clear if m == "mov" and o.endswith(", 0")]
    assert any("[rax + rcx]" in o for o in stores)                       # magic + used dword
    assert any("0x70c0" in o for o in stores) and any("0x70c3" in o for o in stores)   # the date
    assert not any("0x70c8" in o for o in stores)                        # the end marker stays
    calls = [o for _a, m, o in clear if m == "call"]
    assert calls == ["0x6f470", "0x6fec0", "0xea9d0"]                    # LoadSystem(1), Commit(2), the write
