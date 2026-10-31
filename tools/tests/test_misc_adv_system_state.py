"""Tests for tsuki.misc_adv_system_state (docs/formats/adv_state.md).

Table checks run without the game; eboot checks skip when the game folder is missing;
emulation checks also need unicorn and capstone.  Total < 60 s.
"""
from __future__ import annotations

import struct

import pytest

from tsuki import game_dir
from tsuki import misc_adv_system_state as m

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None, reason="game folder not found")


def _has(*mods: str) -> bool:
    try:
        for x in mods:
            __import__(x)
    except ImportError:
        return False
    return True


needs_capstone = pytest.mark.skipif(GAME is None or not _has("capstone"), reason="game or capstone missing")
needs_emu = pytest.mark.skipif(GAME is None or not _has("capstone", "unicorn"), reason="game or unicorn missing")


# ---------------------------------------------------------------- tables (no game)
def test_history_fields_tile_record() -> None:
    assert m.check_history_tiling() == []
    assert sum(f.size for f in m.HISTORY_FIELDS) == m.RECORD_SIZE == 0x3A10


def test_every_history_field_has_writer_and_restore_text() -> None:
    for f in m.HISTORY_FIELDS:
        assert f.writer and f.restore and f.conf in ("V", "E", "H"), f.name


def test_scene_state_fields_tile_block() -> None:
    assert m.check_scene_state_tiling() == []
    kinds = [f.kind for f in m.SCENE_STATE_FIELDS]
    assert (kinds.count("array"), kinds.count("global"), kinds.count("gap")) == (51, 29, 4)
    assert sum(f.size for f in m.SCENE_STATE_FIELDS if f.kind == "gap") == 8
    for f in m.SCENE_STATE_FIELDS:                     # every field: a writer, or marked as having none
        assert f.writers and f.note, f.name
        if f.kind == "array":
            assert f.size % 40 == 0, f.name


def test_scene_state_arrays_match_layer_work() -> None:
    """Reconcile with the layer work's array map (tsuki.misc_layer_state_and_motions)."""
    lay = pytest.importorskip("tsuki.misc_layer_state_and_motions")
    arrays = [f for f in m.SCENE_STATE_FIELDS if f.kind == "array"]
    assert [(f.off, f.size, f.name) for f in arrays] == \
        [(a.off, a.stride * lay.NUM_LAYERS, a.name) for a in lay.SCENE_ARRAYS]
    assert lay.SCENE_LEN == m.SCENE_STATE_SIZE


def test_push_site_table() -> None:
    assert len(m.PUSH_SITES) == 59 and len({p.site for p in m.PUSH_SITES}) == 59
    by = {p.site: p for p in m.PUSH_SITES}
    assert by[0x97EA4].kinds == (6, 7) and by[0x97EA4].live           # _ATSV(n): 6 | (n & 3 == 1)
    live_kinds = {k for p in m.PUSH_SITES if p.live for k in p.kinds}
    assert live_kinds == {0, 1, 2, 3, 6, 7, 9, 12, 13}                # 4, 5, 8, 11 never pushed
    assert [p.site for p in m.PUSH_SITES if p.live and 0 in p.kinds] == [0x9291F]
    dead = [p for p in m.PUSH_SITES if not p.live]
    assert (len(dead), len({p.func for p in dead})) == (21, 19)        # 21 sites in 19 dead functions
    assert set(m.HISTORY_MODES) >= live_kinds


def test_atsv_paths() -> None:
    assert m.atsv_path(0, 0) == (0x9853E, 6)            # _ATSV()
    assert m.atsv_path(1, 1) == (0x97EA4, 7)            # _ATSV(1): before the choices
    assert m.atsv_path(1, 8) == (0x9788F, 6)            # _ATSV(8): scene stack + auto save
    assert m.atsv_path(1, 0) == (0x97EA4, 6) and m.atsv_path(1, 2) == (0x9853E, 6)


def test_config_fields_tile_block() -> None:
    pos = 0
    for c in m.CONFIG_FIELDS:
        assert c.off == pos, hex(c.off)
        pos += c.size
    assert pos == m.CONFIG_SIZE


def test_actions_named() -> None:
    assert [a.index for a in m.ACTIONS] == list(range(27))
    assert all(a.name for a in m.ACTIONS)


def test_scene_stack_ends_at_its_index_word() -> None:
    assert m.SCENE_STACK + m.SCENE_STACK_COUNT * m.RECORD_SIZE == m.SCENE_STACK_TOP


def test_f_values_restore_order() -> None:
    lists = ([5, 7], [7, 9], [5])
    rec = bytearray(m.RECORD_SIZE)
    struct.pack_into("<HH", rec, 0x34AA, 0x1234, 0x5678)
    rec[0x3501] = 0xFE                       # s8 list wins over u16 for F5
    rec[0x34FA] = 0b10                        # bool list wins for F9 and F7 (bit 0 = 0)
    f = m.history_f_values(bytes(rec), lists=lists)
    assert f == {5: 0xFFFE, 7: 0, 9: 1}


# ---------------------------------------------------------------- eboot data
@needs_game
def test_history_lists() -> None:
    u16, bl, s8 = m.read_history_lists()
    assert len(u16) == 39 and len(bl) == 43 and len(s8) == 276
    assert u16[-1] == 1122 and 823 in s8 and s8.index(823) == 32 and 1118 in bl
    assert 991 in u16 and 991 in bl and 967 in u16 and 967 in s8      # restored twice (adv_state.md 2.1)


@needs_game
def test_action_tables() -> None:
    t = m.read_action_tables()
    assert [t[0][0], t[1][0], t[2][0]] == [0x20, 0x220, 0x220]          # decide (+ L3)
    assert [t[0][12], t[2][12]] == [0x8, 0x800]                         # skip latch toggle: R1 / OPTIONS
    assert [t[0][13], t[0][19], t[0][20]] == [0x4, 0x2, 0x1]            # auto L1, fast-forward R2, rewind L2
    assert t[0][14] == 0x800 and t[0][15] == 0x100                      # quick save OPTIONS, quick load unbound
    assert m.read_data(None, m.ACTION_PTR, 8) == struct.pack("<Q", m.ACTION_TABLES)


@needs_game
def test_static_config_matches_table() -> None:
    st = m.read_static_config()
    for c in m.CONFIG_FIELDS:
        if c.size <= 8 and c.name != "unused":
            v = int.from_bytes(st[c.off:c.off + c.size], "little")
            assert v == c.static, (hex(c.off), v, c.static)
    assert struct.unpack_from("<19H", st, 0x76) == (50,) * 19


@needs_game
def test_config_pages() -> None:
    p0, p1 = m.read_config_pages()
    assert (p0.first, p0.last, len(p0.rows)) == (52, 78, 16)
    assert (p1.first, p1.last, len(p1.rows)) == (79, 190, 26)
    assert [r.max_value for r in p0.rows[:4]] == [250, 250, 10000, 10000]
    assert p0.rows[0].items == [52, 55, 53, 54] and p0.rows[0].types == [3, 4, 5, 2]
    assert p1.rows[7].items == [96, 100, 97, 98, 99]
    assert all(r.items[1] == 100 + 5 * (i - 7) for i, r in enumerate(p1.rows) if i >= 7)


@needs_capstone
def test_push_sites_match_eboot() -> None:
    """Byte scan of every E8 call of History_Push with the kind decoded from the code."""
    assert m.scan_push_sites() == [(p.site, p.kinds) for p in m.PUSH_SITES]


@needs_capstone
def test_scene_state_global_writers_decode() -> None:
    """Every listed writer instruction of a scalar global is a store; rip-relative ones hit
    the field (absolute check of the static scan)."""
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    eb = GAME / "eboot.bin"                                   # type: ignore[operator]
    n_rip = 0
    for f in m.SCENE_STATE_FIELDS:
        if f.kind != "global" or not f.writers.startswith("0x"):
            continue
        for va in (int(x, 16) for x in f.writers.split(", ")):
            ins = next(md.disasm(m.read_text(eb, va, 16), va))
            dst = ins.operands[0]
            assert dst.type == capstone.x86_const.X86_OP_MEM, (f.name, hex(va), ins.mnemonic, ins.op_str)
            if ins.reg_name(dst.mem.base) == "rip":
                t = ins.address + ins.size + dst.mem.disp - m.SCENE_STATE
                assert t < f.off + f.size and t + dst.size > f.off, (f.name, hex(va), hex(t))
                n_rip += 1
    assert n_rip >= 40


@needs_game
def test_atsv_forms_in_scripts() -> None:
    """The three `_ATSV` forms of the scripts and the kinds they push."""
    import collections
    import re
    from tsuki.script import AllScr, inline_int
    got: collections.Counter = collections.Counter()
    rx = re.compile(rb"(?:^|;)[^_;]*_ATSV\(([^;]*)\);")
    with AllScr() as scr:
        for i in range(len(scr)):
            raw = scr.raw(i)
            for mt in rx.finditer(raw):
                body = mt.group(1)
                argc = 0 if body == b"" else 1 + body.count(b",")
                n = inline_int(raw, mt.start(1) - 6, 0) if argc else 2
                got[(body.decode(), m.atsv_path(argc, n))] += 1
    assert got == {("", (0x9853E, 6)): 313, ("1", (0x97EA4, 7)): 83, ("8", (0x9788F, 6)): 2}


# ---------------------------------------------------------------- emulation
@needs_emu
def test_history_push_field_map() -> None:
    from tsuki import misc_adv_system_state_emu as e
    assert e.verify_push(seeds=range(1, 14)) == []


@needs_emu
def test_history_restore_copy_map() -> None:
    from tsuki import misc_adv_system_state_emu as e
    assert e.verify_restore(seeds=(11, 12), modes=(1, 2, 3, 6, 7, 8, 9, 12, 13)) == []


@needs_emu
def test_history_restore_kind0_both_paths() -> None:
    """Kind 0 (slot loads, rewind targets): seed 2 restores F931 >= 0, seed 31 F931 < 0."""
    from tsuki import misc_adv_system_state_emu as e
    assert e.verify_restore(seeds=(2, 31), modes=(0,), from_history=(0, 1)) == []


@needs_emu
def test_recommended_settings_equal_boot_defaults() -> None:
    from tsuki import misc_adv_system_state_emu as e
    rec = e.config_recommended()
    assert rec == m.read_static_config()
    for c in m.CONFIG_FIELDS:
        if c.recommended is not None and c.size <= 4:
            assert int.from_bytes(rec[c.off:c.off + c.size], "little") in (c.recommended, c.static), hex(c.off)


@needs_emu
def test_config_from_menu_map() -> None:
    from tsuki import misc_adv_system_state_emu as e
    assert sorted(e.config_from_menu()) == sorted(m.FROM_MENU)


@needs_emu
def test_engine_made_slot_decodes() -> None:
    from eboot import savedata
    from tsuki import misc_adv_system_state_emu as e
    si = e.engine_slot_image(slot=7, with_state_b=True)
    img, rec = si.image, si.record
    p = savedata.parse(img)
    present = [s for s in p.slots if s.present]
    assert [s.index for s in present] == [7]
    s = present[0]
    assert str(s.time) == "2026-10-25 12:34:56"
    assert (s.script_index, s.script_offset) == (si.script_no, si.stmt_offset - 1)
    slot = img[s.offset:s.offset + savedata.SLOT_SIZE]
    a, b = m.slot_states(slot)
    assert b is not None and b == si.record_b[:0x36A8]                 # type: ignore[index]
    _u16, bl, _s8 = m.read_history_lists()
    k = bl.index(1118)
    diff = [i for i in range(m.RECORD_SIZE) if a[i] != rec[i]]
    assert diff == [0x34FA + (k >> 3)]                                  # only the F1118 bit (F1118 was 0)
    assert (a[0x34FA + (k >> 3)] >> (k & 7)) & 1 == 1
    # every slot block carries the magic after a commit; empty ones have used = 0
    assert all(struct.unpack_from("<H", img, 0x1A00 + i * 0x7140)[0] == 0x2AF8 for i in range(140))
    assert sum(1 for x in p.slots if x.used) == 1
    assert m.check_save_image(img) == []                                # acceptance checks of 7.7


@needs_emu
def test_quick_save_has_no_f1118_bit() -> None:
    from eboot import savedata
    from tsuki import misc_adv_system_state_emu as e
    si = e.engine_slot_image(seed=23, quick=True)
    p = savedata.parse(si.image)
    assert [s.index for s in p.slots if s.present] == [139]
    a, _b = m.slot_states(si.image[p.slots[139].offset:p.slots[139].offset + savedata.SLOT_SIZE])
    assert a == si.record


@needs_emu
def test_engine_made_slot_loads_through_history_restore() -> None:
    """Slot_Load 0xA2D10 -> 0x7E790 -> History_Restore on the engine-made image (adv_state.md 7.3)."""
    from tsuki import misc_adv_system_state_emu as e
    si = e.engine_slot_image(slot=7, with_state_b=True)
    assert e.verify_slot_load(None, si.image, 7) == []
    si = e.engine_slot_image(slot=42, seed=17)                         # no state B (n = 0)
    assert e.verify_slot_load(None, si.image, 42) == []


def test_real_save_if_present() -> None:
    """Acceptance checks on the user's own save (adv_state.md 7.7) once one exists."""
    from eboot import savedata
    path = savedata.default_save_path()
    if path is None or GAME is None:
        pytest.skip("no savedata000 (shadPS4 user 1000) or game")
    buf = path.read_bytes()                       # read-only
    assert m.check_save_image(buf) == []
