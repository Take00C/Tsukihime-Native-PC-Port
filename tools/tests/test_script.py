"""Tests for tsuki.script / tsuki.script_tables (allscr.mrg scenario scripts).

Synthetic tests always run; real-data tests skip when the game folder is
missing (TSUKI_GAME_DIR overrides the default location).  The full-corpus
checks decode and parse all 549 scripts once (module fixture, ~10 s; ~45 s with
TSUKI_NO_NUMBA=1).  The engine-emulation test runs the eboot's own argument
readers and dispatch under unicorn (skipped when unicorn or eboot.bin is
missing).
Run:  cd D:/TsukihimePort/tools && ../.venv/Scripts/python.exe -m pytest tests/test_script.py -q
"""
from __future__ import annotations

import collections
import faulthandler
import importlib.util
import random
import sys
from pathlib import Path
from typing import Iterator

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tsuki import game_dir, image_dir  # noqa: E402
from tsuki.script import (AllScr, AssetResolver, Command, ExprUndefined, LayerTracker,  # noqa: E402
                          command_assets, engine_int, inline_int, marker_hex, obj_expr, obj_split,
                          obj_strict_int, parse_labels, parse_script, picture_key, script_edges, text_refs)
from tsuki.script_tables import (ENGINE_ALIASES, ENGINE_MAIN, MOVIE_FILES, OBJ_COMMANDS, SCENE_IDS,  # noqa: E402
                                 engine_handler, read_motions, read_movie_files, read_scene_ids, scene_index)

IMG = image_dir()
needs_game = pytest.mark.skipif(IMG is None, reason="game data not found")
_GD = game_dir()
EBOOT = (_GD / "eboot.bin") if _GD is not None else None
needs_eboot = pytest.mark.skipif(EBOOT is None or not EBOOT.exists(), reason="eboot.bin not found")
HAVE_UNICORN = importlib.util.find_spec("unicorn") is not None and importlib.util.find_spec("capstone") is not None

NOOPS = ("SNX4", "FANX", "IFP4", "DMMY", "STQK", "NOP_")      # used by the scripts, ignored on PS4


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------

def test_inline_reader() -> None:
    assert engine_int("258-18") == 240
    assert engine_int("154-19") == 135
    assert engine_int("-1") == -1
    assert engine_int("") == 0
    assert engine_int("`017:100") == 100
    assert engine_int("1+2-3+40") == 40
    assert engine_int("242.25") == 24225          # '.' is ignored by the inline reader
    assert engine_int("0x000000") == 0
    assert engine_int("40000") == 40000 - 65536   # i16 reader wraps
    assert engine_int("40000", bits=32) == 40000
    assert engine_int("12345678901234567890") == engine_int("1234567890123456")  # 16-byte cap
    # missing vs empty, on whole statements (VA 0x76690 skip loop)
    st = parse_script(b"_ATSV();")[0]
    assert (st.int_arg(0), st.int_arg(1)) == (0, -1)   # '()' index 0 is empty, not missing
    st = parse_script(b"_STCC(5);")[0]
    assert (st.int_arg(0), st.int_arg(1)) == (5, -1)
    st = parse_script(b"_AAAA(,5);")[0]
    assert (st.int_arg(0), st.int_arg(1), st.present(0), st.present(1), st.present(2)) == (0, 5, False, True, False)
    assert inline_int(b"_AAAA(`a,12345);", 6, 0) == 345   # the tag skip is blind (5 bytes, past the ',')


def test_object_parser_and_expressions() -> None:
    # values checked against the engine (VA 0xDA50) with tsuki.script_emu
    cases = {"258-18": 240, "242.25": 242, "2000.0": 2000, "0x000000": 0, "img1728": 0, "`017:100": 0,
             "10-3-2": 9, "{10-3}-2": 5, "2*-3": -6, "1<<4|1": 17, "": 0, "-1": -1, "--5": 5, "5-": 5,
             "+5": 5, "1+2*3": 7, "-7/2": -3, "-7%3": -1, "!0": 1, "~0": -1, "5!": 5, "{1+2}*3": 9,
             "12345678901234567890": -1, "1 2": 1, "3<5": 1, "1==1": 1, "2!=2": 0, "1&&0": 0, "0||3": 1}
    for text, value in cases.items():
        assert obj_expr(text) == value, text
    with pytest.raises(ExprUndefined):
        obj_expr("1/0")
    assert obj_strict_int("-12") == -12 and obj_strict_int(" 7") == 7
    assert obj_strict_int("242.25") is None and obj_strict_int("5 ") is None and obj_strict_int("") is None
    assert obj_split(b'_STCH(1,"a,b",c);', 5) == [(6, 7), (9, 12), (14, 15)]
    assert obj_split(b"_STCH();", 5) == [] and len(obj_split(b"_STCH(" + b"1," * 30 + b"1);", 5)) == 20
    c = parse_script(b"_STCH(18,img1728,240,156,1000,242.25,2,,CC,,,);")[0]
    assert c.key in OBJ_COMMANDS
    assert (c.value(5), c.int_arg(5), c.value(7), c.int_arg(7)) == (242, 24225, None, 0)
    assert parse_script(b"_STCC();")[0].obj_args == ()


def test_picture_key() -> None:
    c = parse_script(b"_STCH(4,img1696,240,136,1000,255,1910,,CC,11,11,);")[0]
    assert picture_key(c) == "IMG1696_11_11"
    c = parse_script(b"_STCH(4,img1696,240,136,1000,255,1910,,CC,5,,);")[0]
    assert picture_key(c) == "IMG1696_05_00"
    c = parse_script(b"_SCH2(23,img0249,655,205,,0,4054,,CC,,,12,,,,,,,22);")[0]
    assert picture_key(c) == "IMG0249" and picture_key(c, "IMG0230_02_02") == "IMG0249_02_02"
    c = parse_script(b"_SCH2(21,sie_a_12_03_n13,,,,,,,CC,0,0,,,,,,,,18);")[0]
    assert picture_key(c, "SIE_A_00_04_N03_08_08") == "SIE_A_12_03_N13"   # explicit 0,0 overrides
    tr = LayerTracker()
    for c in parse_script(b"_STCH(22,img0230,1,1,1000,255,2,,CC,2,2);_SCH2(23,img0249,,,,,,,CC,,,,,,,,,,22);"):
        if c.key == "SCH2":
            assert picture_key(c, tr.src_name(c)) == "IMG0249_02_02"
        tr.update(c)


def test_parse_basic_and_markers() -> None:
    src = (b"_ZZ00a01();_ZZ00b01(START);_WKST(F823,eq,0);_STCH(0,img2345,258-18,154-19,1000,255,2,,CC,,);"
           b"_SEPL(0,1,MOON_SE03001,,`001:4000,,`017:50);_ZM01801($000107@k@e);_JUMP(240,START);")
    cmds = parse_script(src)
    assert [c.name for c in cmds] == ["ZZ", "ZZ", "WKST", "STCH", "SEPL", "ZM", "JUMP"]
    assert "".join(c.raw for c in cmds).encode() == src
    z = cmds[1]
    assert (z.key, z.block, z.sub, z.args[0].value) == ("ZZ00", 0x00B, 1, "START")
    assert cmds[0].args == () and cmds[0].block == 0x00A
    w = cmds[2]
    assert [a.raw for a in w.args] == ["F823", "eq", "0"] and w.args[0].var == ("F", 823)
    st = cmds[3]
    assert len(st.args) == 11 and st.int_arg(2) == 240 and st.int_arg(3) == 135
    assert st.value(2) == 240 and st.value(3) == 135
    assert st.int_arg(7) == 0 and st.args[7].empty and st.int_arg(20) == -1
    se = cmds[4]
    vol, fade = se.tagged(17), se.tagged(1)
    assert vol is not None and fade is not None and vol.to_int() == 50 and fade.to_int() == 4000
    assert se.tagged(68) is None
    assert cmds[5].text_ids == [107]
    assert cmds[6].offset == src.index(b"_JUMP") and cmds[6].end == len(src)
    # marker ids are decoded like the engine: only lower-case hex is meaningful
    assert marker_hex(b"00A") == 0x11 and parse_script(b"_ZZ00A01();")[0].block == 0x11
    assert marker_hex(b"f82") == 0xF82


def test_parse_engine_quirks() -> None:
    # missing '(' (retail 14_01A_CIEL14A_2), junk after ')', no '_' at all, lower-case name
    src = b"_STTR,0,26);_STZ4(5,498,498,498,498,0,0,0)gb);xyz;_abcd(1);_DMMY(0);_IFP4(,LV00100);_RTM2();_VCLR();"
    cmds = parse_script(src)
    sttr, stz, junk, low, dmmy, ifp4, rtm2, vclr = cmds
    assert sttr.name == "STTR" and [a.raw for a in sttr.args] == ["0", "26"] and sttr.handled
    assert sttr.obj_args == ("0", "26")
    assert stz.trailer == "gb)" and len(stz.args) == 8
    assert junk.name == "" and not junk.handled
    assert not low.handled
    assert not dmmy.handled and not ifp4.handled          # no handler in the PS4 interpreter
    assert rtm2.handled and vclr.handled                  # keep-BGM flag / two stores
    assert "".join(c.raw for c in cmds).encode() == src


def test_text_refs() -> None:
    assert text_refs("$000107") == [107]
    assert text_refs("@x$001397^$001398@n") == [1397, 1398]
    assert text_refs("$12345") == []


def test_parse_labels_synthetic() -> None:
    rec = b"START".ljust(21) + b"015,0000b\r\n" + b"LBL_000".ljust(21) + b"016,0002e\r\n"
    labs = parse_labels(rec)
    assert [(lab.name, lab.block, lab.offset) for lab in labs] == [("START", 0x15, 0xB), ("LBL_000", 0x16, 0x2E)]


def test_engine_tables() -> None:
    assert len(SCENE_IDS) == 549 and len(set(SCENE_IDS)) == 549
    assert SCENE_IDS[:4] == (110, 240, 260, 270) and SCENE_IDS[-1] == 8017
    assert scene_index(240) == 1 and scene_index(1024) == 519 and scene_index(9000) is None
    assert len(MOVIE_FILES) == 134 and MOVIE_FILES[4] == "prologue.mp4" and MOVIE_FILES[39] == "credit00.mp4"
    assert len(ENGINE_MAIN) == 112                       # 110 names + J* + Z*
    assert engine_handler("JUMP") == engine_handler("JXYZ")
    assert engine_handler("ZM01") == ENGINE_MAIN["Z*"]
    assert set(ENGINE_ALIASES) == {"SEFI", "VPL2", "WT__"}
    for alias, base in ENGINE_ALIASES.items():
        assert engine_handler(alias) == engine_handler(base)
    assert engine_handler("RTM2") == (0x97FBC, "continue") and engine_handler("VCLR") == (0x97277, "continue")
    assert engine_handler("RTMN") != engine_handler("RTM_")    # RTMN sets the keep-BGM flag first
    for name in NOOPS + ("ABCD", "RTM3"):
        assert engine_handler(name) is None, name


# ---------------------------------------------------------------------------
# engine emulation (unicorn): Python readers == the eboot's own code
# ---------------------------------------------------------------------------

@needs_eboot
@pytest.mark.skipif(not HAVE_UNICORN, reason="unicorn/capstone not installed")
def test_readers_match_engine_emulation() -> None:
    from tsuki.script_emu import EngineEmu
    assert EBOOT is not None
    # unicorn's first mem_map raises a first-chance access violation that it handles itself;
    # pytest's faulthandler would print it as "Windows fatal exception", so pause it here
    was_enabled = faulthandler.is_enabled()
    faulthandler.disable()
    try:
        emu = EngineEmu(EBOOT)
    finally:
        if was_enabled:
            faulthandler.enable()
    rng = random.Random(7)
    exprs = ["258-18", "242.25", "109.286", "2000.0", "0x000000", "img1728", "`017:100", "10-3-2", "{10-3}-2",
             "2*-3", "1<<4|1", "-1", "--5", "5-", "+5", "1+2*3", "-7/2", "-7%3", "!0", "~0", "5!", "{1+2}*3",
             "12345678901234567890", "1 2", "a5b6", "3>=3", "8>>1", "1^3", "6&3", "=5", "5=="]
    alpha = "0123456789+-*/%<>=!&|^~{} .x`a"
    exprs += ["".join(rng.choice(alpha) for _ in range(rng.randint(1, 9))) for _ in range(150)]
    n = 0
    for x in exprs:
        try:
            v = obj_expr(x)
        except ExprUndefined:
            continue
        assert emu.obj_expr(b"_STCH(" + x.encode() + b");", 0) == v, x
        n += 1
    assert n > 120
    stmts = [b"_ATSV();", b"_STCC(5);", b"_AAAA(,5);", b"_MPLY(`017:100);", b"_STTR,0,26);", b"_AAAA(1,2)x;",
             b"_AAAA(`a,12345);", b'_STCH(1,"a,b",c);', b"_SEPL(0,1,X,,`001:4000,,`017:50);"]
    for _ in range(80):
        body = "".join(rng.choice("0123456789-+,`)( a") for _ in range(rng.randint(0, 12)))
        stmts.append(b"_ABCD(" + body.encode() + b");")
    for st in stmts:
        cmd = parse_script(st)[0]
        for k in range(3):
            for bits in (16, 32):
                assert emu.inline_int(st, k, bits) == cmd.int_arg(k, bits), (st, k, bits)
            assert emu.inline_present(st, k) == cmd.present(k), (st, k)
        assert emu.obj_split(st) == obj_split(st, 5), st
    for ids in (b"00a01", b"f8201", b"00A01", b"zz_xx", b"ABCDE"):
        st = b"_ZM" + ids + b"($000001);"
        c = parse_script(st)[0]
        assert emu.marker_ids(st) == (c.block, c.sub), ids
    for name, (va, _) in ENGINE_MAIN.items():
        assert emu.dispatch(name.replace("*", "QQQ")) == va, name
    for name in NOOPS + ("RTM3", "VCLS", "ABCD", "C0LO"):
        assert emu.dispatch(name) is None, name


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------

@pytest.fixture(scope="module")
def scr() -> Iterator[AllScr]:
    if IMG is None:
        pytest.skip("game data not found")
    s = AllScr()
    yield s
    s.close()


@pytest.fixture(scope="module")
def corpus(scr: AllScr) -> dict[int, list[Command]]:
    return {i: scr.commands(i) for i in range(len(scr))}


@needs_eboot
def test_tables_match_eboot() -> None:
    assert EBOOT is not None
    assert read_scene_ids(EBOOT) == SCENE_IDS
    assert read_movie_files(EBOOT) == MOVIE_FILES
    m = read_motions(EBOOT)
    assert len(m) == 24192 and sum(len(x.keyframes) for x in m) == 71905
    assert m[0].duration == 2800 and [k.time for k in m[0].keyframes] == [0, 2800]
    assert m[0].keyframes[0].fields[4] == 255 and m[0].keyframes[1].fields[4] == 0     # alpha 255 -> 0
    regular = [x for x in m if x.keyframes and x.keyframes[0].time == 0 and x.keyframes[-1].time == x.duration
               and [k.time for k in x.keyframes] == sorted(k.time for k in x.keyframes)]
    assert len(regular) == 24135


@needs_game
def test_allscr_layout(scr: AllScr) -> None:
    assert len(scr) == 549
    assert scr.names[:4] == ["START_SCRIPT", "00_00", "00_01", "00_02"] and scr.names[-1] == "JUMPQA"
    assert scr.raw(0).startswith(b"_ZZ00a01();_ZZ00b01(START);_WKST(F823,eq,0);")
    assert scr.raw(0).endswith(b"_JUMP(240,START);")
    assert scr.index("#270") == 3 and scr.index("00_02") == 3


@needs_game
def test_corpus_parse(scr: AllScr, corpus: dict[int, list[Command]]) -> None:
    total = 0
    names: collections.Counter[str] = collections.Counter()
    unhandled: collections.Counter[str] = collections.Counter()
    for i, cmds in corpus.items():
        assert "".join(c.raw for c in cmds).encode("utf-8", "surrogateescape") == scr.raw(i)
        total += len(cmds)
        for c in cmds:
            assert c.name, (i, c.raw)
            names[c.name] += 1
            if not c.handled:
                unhandled[c.name] += 1
    assert total == 1_083_168
    assert len(names) == 98
    assert names["WKST"] == 487_857 and names["ZM"] == 42_590 and names["STCH"] == 54_494
    assert names["JUMP"] == 1_741 and names["VPLY"] == 16_689
    assert unhandled == {"SNX4": 8758, "FANX": 2741, "IFP4": 203, "DMMY": 71, "STQK": 3, "NOP_": 1}
    assert names["RTM2"] == 1 and names["RTMN"] == 1


@needs_game
def test_labels_point_at_markers(scr: AllScr, corpus: dict[int, list[Command]]) -> None:
    n = 0
    for i, cmds in corpus.items():
        by_off = {c.name_offset: c for c in cmds}
        named = {c.args[0].value for c in cmds if c.name in ("ZZ", "ZY") and c.args and c.args[0].value}
        labs = scr.labels(i)
        assert {lab.name for lab in labs} == named
        for lab in labs:
            c = by_off[lab.offset]
            assert c.name in ("ZZ", "ZY") and c.block == lab.block and c.args[0].value == lab.name
            n += 1
    assert n == 3654


@needs_game
def test_marker_ids(corpus: dict[int, list[Command]]) -> None:
    last_block = -1
    backwards = []
    top = 0
    for i in sorted(corpus):
        ids = [(c.block, c.sub) for c in corpus[i] if c.block is not None and c.sub is not None]
        assert all(c.raw[3:8] == c.raw[3:8].lower() for c in corpus[i] if c.block is not None)   # lower-case hex only
        assert ids == sorted(set(ids)), i                 # strictly increasing within a script
        if ids[0][0] < last_block:                        # block ids are reused only by variants
            backwards.append(i)
        last_block = ids[-1][0]
        top = max(top, last_block)
        subs = collections.defaultdict(list)
        for b, s in ids:
            subs[b].append(s)
        assert all(v == list(range(1, len(v) + 1)) for v in subs.values()), i
    # 00_02 re-uses 00_01's header ids; JUMP01..JUMPQA all start with JUMP00's header (0xED8)
    assert backwards == [3] + list(range(533, 549))
    assert top == 0xF82 < 0xFB4                           # engine read-tracking table limit (VA 0x96639)


@needs_game
def test_control_flow_targets_resolve(scr: AllScr, corpus: dict[int, list[Command]]) -> None:
    kinds: collections.Counter[str] = collections.Counter()
    labmaps = {i: scr.label_map(i) for i in corpus}
    for i, cmds in corpus.items():
        for e in script_edges(i, cmds):
            kinds[e.kind] += 1
            if e.kind == "end":
                continue
            assert e.dst is not None, e
            assert e.label is None or e.label in labmaps[e.dst], e
    assert kinds == {"jump": 1741, "if": 1194, "end": 1065, "select": 817, "platform": 436,
                     "goto": 101, "cond": 3}


@needs_game
def test_text_and_voice_links(scr: AllScr, corpus: dict[int, list[Command]]) -> None:
    from tsuki.text import ScriptText
    txt = ScriptText()
    ids = [t for cmds in corpus.values() for c in cmds for t in c.text_ids]
    assert len(ids) == 43_930 and max(ids) < txt.count
    cmds = corpus[scr.index("00_02")]
    k = next(j for j, c in enumerate(cmds) if 3 in c.text_ids)
    assert cmds[k].raw == "_ZM01d04($000003);"
    assert "familiar scent of straw mats" in txt.expand(cmds[k].args[0].raw, "en")
    res = AssetResolver()
    voice = next(c for c in reversed(cmds[:k]) if c.name in ("VPLY", "VPL2"))
    r = command_assets(voice, res)[0]
    assert (r.archive, r.index, r.note) == ("voice", 15620, "")
    bad = [c.raw for cs in corpus.values() for c in cs if c.name in ("VPLY", "VPL2") and command_assets(c, res)[0].note]
    assert bad == []


@needs_game
def test_obj_args_and_decimals(corpus: dict[int, list[Command]]) -> None:
    decimals: collections.Counter[str] = collections.Counter()
    for cmds in corpus.values():
        for c in cmds:
            if c.key in OBJ_COMMANDS:
                assert len(c.obj_args) <= 20 and all('"' not in a for a in c.obj_args)
                for a in c.obj_args:
                    if "." in a and a.replace(".", "").lstrip("-").isdigit():
                        decimals[c.key] += 1
                        assert obj_expr(a) == int(a.split(".")[0])   # the evaluator stops at '.'
    assert decimals == {"STCH": 24, "STGS": 3, "SSW2": 1}


@needs_game
@needs_eboot
def test_asset_links(corpus: dict[int, list[Command]]) -> None:
    res = AssetResolver()
    seen: dict[tuple[str, str], object] = {}
    written: set[str] = set()
    for cmds in corpus.values():
        tr = LayerTracker()
        for c in cmds:
            if c.name in ("STCH", "SCH2", "SPFC", "SPRD", "STCG", "SEPL", "MPLY", "TOMP"):
                if c.name in ("STCH", "SCH2") and len(c.obj_args) > 1 and c.obj_args[1]:
                    written.add(c.obj_args[1])
                elif c.name in ("SPFC", "SPRD", "STCG"):
                    a = c.arg(0 if c.name == "STCG" else 1)
                    if a is not None and a.value and not a.value.lstrip("-").isdigit():
                        written.add(a.value)
                for r in command_assets(c, res, tr):
                    seen[(r.kind, r.key.upper() if r.kind == "image" else r.key)] = r
            tr.update(c)
    assert len(written) == 7330 and len({w.upper() for w in written}) == 7329
    imgs = [r for (k, _), r in seen.items() if k == "image"]
    assert len(imgs) == 10967                                                 # incl. _hh_vv variants
    unresolved = sorted(r.key for r in imgs if r.index is None)               # type: ignore[attr-defined]
    assert unresolved == ["IMG1637_28_28", "IMG5007_00_27", "IMG5007_02_02", "SIK_A_32_00_N00_02_30",
                          "Z20_M_61_00_A00_10_00"]
    missing = [(r.key, r.missing) for r in imgs if r.missing]                 # type: ignore[attr-defined]
    assert missing == [("NOE_L_60_03_N01", ("NOE_L_60_03_N00_B.MZP",))]
    ses = [r for (k, _), r in seen.items() if k == "se"]
    assert len(ses) == 1314 and all(r.index is not None for r in ses)         # type: ignore[attr-defined]
    assert all(r.index is not None for (k, _), r in seen.items() if k in ("bgm", "movie"))  # type: ignore[attr-defined]
    assert res.image("img1685").archive == "allpac0" and res.image("img1685").index == 270
    assert res.image("BLACK").index == 0 and res.se("MOON_SE03028").index == 598
