"""Tests for tsuki.vm_vars (script-VM variable operands and their read/write semantics).

Synthetic tests always run.  The engine tests execute the eboot's own handlers under
unicorn (tsuki.misc_vm_variables_errata.VarEmu) and skip when eboot.bin or unicorn is
missing; the corpus test decodes the 549 scripts once and skips without the game.
Runtime: about 6 s with the game (corpus decode, 1,000 fuzz cases, the SEL* handler runs).
Run:  cd D:/TsukihimePort/tools && ../.venv/Scripts/python.exe -m pytest tests/test_vm_vars.py -q
"""
from __future__ import annotations

import collections
import faulthandler
import importlib.util
import sys
from pathlib import Path
from typing import Iterator

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tsuki import game_dir, image_dir  # noqa: E402
from tsuki import vm_vars as vv  # noqa: E402
from tsuki.vm_vars import Role, VarState, execute_text, parse_operand  # noqa: E402

_GD = game_dir()
EBOOT = (_GD / "eboot.bin") if _GD is not None else None
HAVE_EMU = importlib.util.find_spec("unicorn") is not None and importlib.util.find_spec("capstone") is not None
needs_emu = pytest.mark.skipif(EBOOT is None or not EBOOT.exists() or not HAVE_EMU,
                               reason="eboot.bin or unicorn/capstone not available")
needs_game = pytest.mark.skipif(image_dir() is None, reason="game data not found")

F823 = vv.VAR_F + 2 * vv.MSG_INDEX_VAR


def F(i: int) -> int:
    return vv.VAR_F + 2 * i


def G(i: int) -> int:
    return vv.VAR_G + 2 * i


def _state(**words: int) -> VarState:
    st = VarState()
    for k, v in words.items():
        st.mem.poke(F(int(k[1:])) if k[0] == "F" else G(int(k[1:])), v)
    return st


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------

def test_operand_parser_roles() -> None:
    assert str(parse_operand("C847", Role.WK_TARGET)) == "F[F823+847]"
    assert parse_operand("C847", Role.VALUE) == vv.Operand("lit", 847, "C847")       # C only as a target
    assert parse_operand("C847", Role.OR_TARGET).kind == "none"                        # WKOR/WKOF: no C
    assert parse_operand("F0012", Role.VALUE) == vv.Operand("F", 12, "F0012")
    assert parse_operand("F1200", Role.WK_TARGET).address() == vv.VAR_Z                # no bounds check
    assert parse_operand("Z12", Role.WK_TARGET).kind == "none"                         # Z never addressed
    assert parse_operand("Z12", Role.VALUE).kind == "lit"
    assert parse_operand("f12", Role.WK_TARGET).kind == "none"                         # case sensitive
    assert parse_operand("_D000", Role.VALUE) == vv.Operand("F", 0, "_D000")
    assert parse_operand("_D650", Role.VALUE) == vv.Operand("G", 50, "_D650")          # 600..700 -> G
    assert parse_operand("_D650", Role.CHOICE) == vv.Operand("F", 650, "_D650")        # choice: always F
    assert parse_operand("_X012", Role.CHOICE).kind == "none"                          # 'G' kind: not stored
    assert parse_operand("_D1000", Role.VALUE).index == 1000
    assert parse_operand("_D1000", Role.GOTS).index == 100                             # 3 digit bytes only
    assert parse_operand("F12", Role.GOTS).kind == "none"
    assert parse_operand("_D000", Role.BO_TARGET).kind == "none"                       # WKBO: F and G only
    assert parse_operand("`001:F12", Role.VALUE) == vv.Operand("F", 12, "`001:F12")    # tag skipped
    assert parse_operand("70000", Role.VALUE).index == 4464                            # i16 wrap
    assert vv.arg_texts(b"_IF__(F1,eq,0,L);", 6) == [b"F1", b"eq", b"0", b"L"]


def test_c_operands_are_message_relative() -> None:
    st = _state(F823=5)
    execute_text("_WKST(C847,eq,63);", st)
    assert st.mem.writes() == [(F(852), 63)] and st.mem.reads() == [F823]
    st.mem.log.clear()
    execute_text("_WKAD(C847,1);", st)
    assert st.mem.writes() == [(F(852), 64)]
    st.mem.poke(F823, 0)
    st.mem.log.clear()
    execute_text("_WKST(C847,eq,0);", st)                 # F823 = 0: the F847 alias seen before
    assert st.mem.writes() == [(F(847), 0)]
    st.mem.log.clear()
    execute_text("_WKST(C847,eq,0);", st, prefetch=True)  # Script_Prefetch has no C case
    assert st.mem.writes() == []


def test_value_dispatch_and_operator_slot() -> None:
    st = _state(F3=0x1234, G3=0x0042)
    execute_text("_WKST(F12,eq,G3);", st)
    execute_text("_WKST(F13,eq,C3);", st)                 # C in a value slot is the literal 3
    execute_text("_WKST(F14,7);", st)                     # no 'e' operator: argument 1 is the value
    execute_text("_WKST(F15,ne,7);", st)                  # 'ne' is not checked: value int('ne') = 0
    execute_text("_WKST(F16,eq);", st)                    # missing value: -1
    execute_text("_WKOR(G21,2);", st)
    execute_text("_WKOF(F3,0x4);", st)                    # 0x4 -> 4 (letters ignored)
    execute_text("_WKBO(F17,3);", st)
    w = dict(st.mem.writes())
    assert w[F(12)] == 0x42 and w[F(13)] == 3 and w[F(14)] == 7 and w[F(15)] == 0
    assert w[F(16)] == 0xFFFF and w[G(21)] == 2 and w[F(3)] == 0x1230
    assert w[F(17)] == 8


def test_if_jumps_when_condition_is_false() -> None:
    for fv, op, v, jump in ((0, "eq", 0, False), (1, "eq", 0, True), (0, "ne", 0, True),
                            (5, "gt", 4, False), (5, "gt", 5, True), (5, "ge", 5, False),
                            (-2, "lt", 3, False), (0xFFFF, "eq", 65535, False), (3, "le", -2, True),
                            (0, "xx", 0, True), (0, "", 0, True), (0, "e", 0, False)):
        st = _state(F26=fv)
        eff, _ = execute_text(f"_IF__(F26,{op},{v},LV00000);", st)
        assert (eff.goto == b"LV00000") == jump, (fv, op, v)
    eff, _ = execute_text("_IF__(F26,eq,1);", _state(F26=0))  # no label: falls through
    assert eff.goto is None


def test_gots_choice_and_selr() -> None:
    st = _state(F823=2, F929=1)
    for k, val in enumerate((7, 8)):
        execute_text(f"_SELR({val},$00000{k});", st)
    assert st.choice_count == 2 and st.choice_values == [7, 8]
    assert (F(849), 1) in st.mem.writes()                 # first SELR line only: C847 += 1
    eff, _ = execute_text("_SELD(_D000,12);", st)
    assert (st.choice_kind, st.choice_index, st.choice_id, st.choice_mode) == (0x46, 0, 12, 1)
    assert eff.yielded and eff.choice_ui and eff.choice_point == 12
    assert vv.choice_store(st, 1) == F(0) and st.mem.peek(F(0)) == 8     # list: values[cursor]
    st.mem.poke(F(0), 1)
    eff, _ = execute_text("_GOTS(_D000,SEL0_0,SEL0_1);", st)
    assert eff.goto == b"SEL0_1"
    st.mem.poke(F(0), 0xFFFF)                            # v + 1 <= 0: argument 0 itself
    assert execute_text("_GOTS(_D000,A,B);", st)[0].goto == b"_D000"
    st.mem.poke(F(0), 5)                                 # past the last label: empty text
    assert execute_text("_GOTS(_D000,A,B);", st)[0].goto == b""
    assert execute_text("_GOTS(F000,A,B);", st)[0].goto is None
    st2 = VarState(choice_kind=0x46, choice_index=0)
    execute_text("_SELS(_G005,);", st2)
    assert (st2.choice_kind, st2.choice_mode, st2.choice_values) == (0x47, 2, [0, 1])
    assert vv.choice_store(st2, 1) is None                # kind 'G': never stored
    st3 = VarState()
    for line in ("_SELR(0,$042569);", "_SELR(1,$042570,1);", "_SELR(2,$042571,0);"):
        execute_text(line, st3)
    assert st3.choice_values == [0, 1, 2] and st3.choice_locked == [False, True, False]


def test_choice_commands_flags() -> None:
    """vm_variables.md 4.5 / 4.6: SELA's F1118 gate, scan mode, the snapshot gate."""
    st = _state(F1118=1)                                  # scene-start block ran: no prompt
    eff, _ = execute_text("_SELA(_D000,);", st)
    assert st.mem.log == [("r", F(1118), 1), ("w", F(1118), 0)]
    assert eff.yielded and not eff.choice_ui and st.choice_mode == 0
    st.mem.log.clear()
    eff, _ = execute_text("_SELA(_D000,);", st)          # F1118 now 0: the prompt opens
    assert eff.choice_ui and (st.choice_mode, st.choice_kind, st.choice_count) == (3, 0x46, 2)
    assert vv.choice_store(st, 1) is None                 # the save prompt stores nothing
    sc = VarState(scan_mode=True, choice_kind=0x11, choice_index=0x2222, choice_id=0x3333)
    for text in ("_SELA(_D000,);", "_SELS(_D000,);", "_SELD(_D005,7);"):
        eff, _ = execute_text(text, sc)
        assert eff.yielded and not eff.choice_ui, text
    assert (sc.choice_kind, sc.choice_index, sc.choice_id, sc.choice_mode) == (0x11, 0x2222, 0x3333, 0)
    two = VarState(choice_kind=0x46, choice_index=4, choice_mode=2, choice_values=[0, 1])
    assert vv.choice_store(two, 1) == F(4) and two.mem.peek(F(4)) == 1
    assert vv.choice_store(two, 2) is None and vv.choice_store(two, 0xFFFF) is None
    # History_Snapshot gate of the first SELR line: F929, then F1102 (kind 0xD), block byte
    for f929, f1102, block, scan, snap, reads in ((0, 0, 0, False, True, [929, 1102, 823]),
                                                  (0, 1, 0, False, False, [929, 1102, 823]),
                                                  (0, 0, 1, False, False, [929, 1102, 823]),
                                                  (1, 0, 0, False, False, [929, 823]),
                                                  (0, 0, 0, True, False, [823])):
        s = _state(F929=f929, F1102=f1102)
        s.snapshot_block, s.scan_mode = block, scan
        eff, _ = execute_text("_SELR(1,$000001);", s)
        assert eff.snapshot == snap and s.mem.reads() == [F(i) for i in reads] and not eff.yielded


def test_history_storage_classes() -> None:
    assert vv.history_storage(0) == "u16" and vv.history_roundtrip(0, 0xBEEF) == 0xBEEF
    assert vv.history_storage(848) == "s8" and vv.history_roundtrip(848, 0x80) == 0xFF80
    assert vv.history_storage(929) == "bool" and vv.history_roundtrip(929, 0x100) == 1
    assert vv.history_storage(1102) is None and vv.history_roundtrip(1102, 9) == 0
    assert vv.history_after_rewind(1102, 9, 4) == 4                 # not kept: rewind leaves it
    assert vv.history_after_rewind(848, 0x1FF, 4) == 0xFFFF         # kept as s8


# ---------------------------------------------------------------------------
# engine (unicorn)
# ---------------------------------------------------------------------------

@pytest.fixture(scope="module")
def emu() -> Iterator[object]:
    from tsuki.misc_vm_variables_errata import VarEmu
    was = faulthandler.is_enabled()
    faulthandler.disable()          # unicorn handles access violations internally
    try:
        assert EBOOT is not None
        yield VarEmu(EBOOT)
    finally:
        if was and sys.__stderr__ is not None:
            faulthandler.enable(file=sys.__stderr__)


FORMS = ["F12", "F0012", "F1200", "F2600", "F-3", "F32768", "F65535", "F70000", "F1+2", "F",
         "G12", "G070", "G2000", "G-1", "C847", "C859", "C-5", "Z12", "_D000", "_X012", "_000",
         "_D599", "_D600", "_D700", "_D701", "_D1000", "D12", "X12", "f12", "12", "-12",
         "`001:F12", " F12", ""]
TEMPLATES = ["_WKST({x},eq,7);", "_WKST(F5,eq,{x});", "_WKST(F5,{x},3);", "_WKAD({x},7);",
             "_WKAD(F5,{x});", "_WKOR({x},6);", "_WKOR(F5,{x});", "_WKOF({x},6);", "_WKBO({x},6);",
             "_IF__({x},eq,0,LBL);", "_IF__(F5,ne,{x},LBL);", "_GOTS({x},L0,L1);", "_SELD({x},3);"]


@needs_emu
def test_every_operand_form_in_every_reader(emu) -> None:  # type: ignore[no-untyped-def]
    """The operand table of vm_variables.md: each form in each reader (main interpreter
    and, for WK*/IF__, Script_Prefetch) gives identical reads, writes, label, argument
    buffer and choice globals in the emulated engine and in tsuki.vm_vars."""
    from tsuki.misc_vm_variables_errata import compare_one
    bad = []
    n = 0
    for tpl in TEMPLATES:
        for x in FORMS:
            text = tpl.format(x=x)
            for pre in ((False, True) if tpl[1:5] in vv.HANDLED_PREFETCH else (False,)):
                n += 1
                d = compare_one(emu, text, pre, 5, bytes(range(0x30, 0x70)))
                if d:
                    bad.append((text, pre, d))
    assert n > 700 and not bad, bad[:3]


@needs_emu
def test_fuzzed_statements_match_engine(emu) -> None:  # type: ignore[no-untyped-def]
    """1,000 random cases (all variable commands including SELA / SELS, every operand form,
    missing and tagged arguments, both interpreters, random F823, argument buffer, F1118,
    snapshot gate words, scan mode, skip counter and block byte): 0 mismatches in reads,
    writes, label, buffer, choice globals, yield, snapshot gate, UI set-up, choice point."""
    from tsuki.misc_vm_variables_errata import fuzz
    st = fuzz(emu, 1000, seed=20261025)
    assert st.run == 1000 and not st.mismatches, st.mismatches[:3]
    kinds = {k.split(".")[0] for k in st.per_command}
    assert kinds >= {"WKST", "WKAD", "WKOR", "WKOF", "WKBO", "IF__", "GOTS", "SELD", "SELR", "SELA", "SELS"}
    assert st.flagged["scan"] > 100 and st.flagged["skip"] > 50 and st.flagged["block"] > 50
    for ev in ("yield", "snapshot", "choice_point", "choice_ui.SELA", "choice_ui.SELS", "choice_ui.SELD"):
        assert st.events[ev] >= 5, ev
    assert st.per_command["SELA"] > st.events["choice_ui.SELA"]   # some SELA runs keep the prompt shut


@needs_emu
def test_choice_setters_and_store(emu) -> None:  # type: ignore[no-untyped-def]
    """SELA / SELS / SELD through ScriptStep's own handlers (helpers included, F1118 = 0
    and 1, scan mode on and off), and both result stores of the choice UI."""
    from tsuki.misc_vm_variables_errata import Flags, choice_store_check, compare_one
    for tgt in ("_D000", "_D012", "_G005", "_X123", "F000", "_D650", "`001:_D007", "_D0001"):
        for name in ("SELA", "SELS", "SELD"):
            for f1118 in (0, 1):
                for scan in (False, True):
                    d = compare_one(emu, f"_{name}({tgt},3);", False, 5, bytes(0x40), {F(1118): f1118},
                                    Flags(scan=scan), choice0=(0x11, 0x2222))
                    assert not d, (name, tgt, f1118, scan, d)
    assert choice_store_check(emu) == []


# ---------------------------------------------------------------------------
# corpus
# ---------------------------------------------------------------------------

@needs_game
def test_corpus_operand_census() -> None:
    from tsuki.script import AllScr
    roles = {("WKST", 0): Role.WK_TARGET, ("WKST", 2): Role.VALUE, ("WKAD", 0): Role.WK_TARGET,
             ("WKAD", 1): Role.VALUE, ("WKOR", 0): Role.OR_TARGET, ("IF__", 0): Role.VALUE,
             ("IF__", 2): Role.VALUE, ("GOTS", 0): Role.GOTS, ("SELD", 0): Role.CHOICE,
             ("SELA", 0): Role.CHOICE, ("SELS", 0): Role.CHOICE}
    kinds: collections.Counter = collections.Counter()
    fidx, gidx = set(), set()
    nseg = 0
    ks: collections.Counter = collections.Counter()
    names = {c for c, _ in roles}
    cache: dict = {}

    def decode(seg: bytes) -> tuple:
        u = seg.find(b"_")
        name = seg[u + 1:u + 5].decode("latin-1") if u >= 0 else ""
        if name not in names:
            return name, [], {}
        args = [a.decode("latin-1") for a in vv.arg_texts(seg, u + 6)]
        return name, args, {k: parse_operand(args[k], r) for (c, k), r in roles.items() if c == name and k < len(args)}
    with AllScr() as al:
        for i in range(len(al)):
            s = al.raw(i)
            f823 = 0
            if i % 50 == 0:              # the fast splitter equals the engine's find loop
                pos, ref = 0, []
                while pos < len(s):
                    end = s.find(b";", pos, pos + 0x300)
                    end = pos + 0x300 if end < 0 else end + 1
                    ref.append(s[pos:end])
                    pos = end
                assert list(vv.segments(s)) == ref
            for seg in vv.segments(s):
                nseg += 1
                if seg not in cache:
                    cache[seg] = decode(seg)
                name, args, ops = cache[seg]
                if name not in names:
                    continue
                for k, o in ops.items():
                    kinds[(name, k, o.kind)] += 1
                    if o.kind == "F":
                        fidx.add(o.index)
                    elif o.kind == "G":
                        gidx.add(o.index)
                    elif o.kind == "C":
                        fidx.add(o.index + f823)
                        ks[o.index, f823] += 1
                t = ops.get(0)
                if name == "WKST" and t is not None and t.kind == "F" and t.index == vv.MSG_INDEX_VAR:
                    f823 = parse_operand(args[2]).index
                if name == "WKAD" and t is not None and t.kind == "F" and t.index == vv.MSG_INDEX_VAR:
                    f823 += parse_operand(args[1]).index
    assert nseg == 1_083_168                               # engine.md 5.3 erratum (d)
    assert kinds[("WKST", 0, "F")] == 402_379 and kinds[("WKST", 0, "C")] == 85_471
    assert kinds[("WKST", 0, "G")] == 7 and kinds[("WKST", 2, "F")] == 20 and kinds[("WKST", 2, "G")] == 1
    assert kinds[("WKAD", 0, "F")] == 42_604 and kinds[("WKAD", 0, "C")] == 3_075
    assert kinds[("IF__", 0, "F")] == 641 and kinds[("IF__", 0, "G")] == 553
    assert kinds[("IF__", 2, "lit")] == 1194 and kinds[("WKOR", 0, "G")] == 21
    for c in ("GOTS", "SELD"):
        assert kinds[(c, 0, "F")] == 170                   # every selector / target is _D000
    assert kinds[("SELA", 0, "F")] == 26 and kinds[("SELS", 0, "F")] == 1
    # message-relative slots: C847 + k (k = F823 = 0..10), C859 + k (k = 1..10)
    assert max(k for (b, k) in ks if b == 847) == 10 and min(k for (b, k) in ks if b == 859) == 1
    assert sum(n for (b, k), n in ks.items() if b == 847) == 42_590 + 291 + 3_075
    # every F index a script touches is kept by the history record; every G is system data
    lists = set(vv.HISTORY_U16) | set(vv.HISTORY_BOOL) | set(vv.HISTORY_S8)
    assert len(fidx) == 78 and fidx <= lists and 0 in vv.HISTORY_U16
    assert len(gidx) == 18 and max(gidx) == 70


@needs_emu
def test_history_index_lists_match_eboot() -> None:
    import struct
    assert EBOOT is not None
    d = EBOOT.read_bytes()

    def rd(va: int, n: int) -> list:
        return list(struct.unpack(f"<{n}h", d[va + 0x2280:va + 0x2280 + 2 * n]))
    assert rd(0x2160E0, 39) == list(vv.HISTORY_U16)
    assert rd(0x216580, 43) == list(vv.HISTORY_BOOL)
    c = rd(0x2165F0, 300)
    assert c[:c.index(-1)] == list(vv.HISTORY_S8)
