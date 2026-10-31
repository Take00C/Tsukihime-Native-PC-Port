"""Tests for tsuki.text (script_text.mrg + markup) and tsuki.text_refs.

Synthetic tests always run; real-data tests skip when the game folder is
missing (set TSUKI_GAME_DIR to override the default location).
Run:  cd D:/TsukihimePort/tools && ../.venv/Scripts/python.exe -m pytest tests/test_text.py -q
"""
from __future__ import annotations

import struct
import sys
from collections import Counter
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tsuki import image_dir, text_refs  # noqa: E402
from tsuki.text import (  # noqa: E402
    CHOICE_NUMBER_BASE, CHOICE_NUMBER_OVERFLOW, COLOR_RGB565, ENGINE_EXPAND_MAX, ICON_NAMES, LANGS,
    Code, Icon, Newline, Ref, Ruby, ScriptText, Style, Text, TextFormatError, TextTable,
    choice_number_id, choice_slot_text, effective_languages, expand_refs, glyph_flags,
    iter_tokens, markup_census, parse_offset_table, plain_text, ruby_enabled, to_runs, tokenize,
)

IMG = image_dir()
needs_game = pytest.mark.skipif(IMG is None, reason="game data not found")
N_LINES = 43969


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------

def _table(strings: list[bytes], end_marks: int = 1) -> tuple[bytes, bytes]:
    blob = b"".join(s + b"\r\n" for s in strings)
    offs, pos = [], 0
    for s in strings:
        offs.append(pos)
        pos += len(s) + 2
    offs += [pos, pos] + [0xFFFFFFFF] * end_marks
    return struct.pack(f">{len(offs)}I", *offs), blob


def test_offset_table_and_lines() -> None:
    tab, blob = _table([b"abc", b"", "日本".encode(), b"x"])
    offs = parse_offset_table(tab)
    t = TextTable("ja", offs, blob)
    assert t.count == 4
    assert [t.line(i) for i in range(4)] == ["abc", "", "日本", "x"]
    assert t.raw(1) == b"\r\n"
    assert t.engine_line(1) == b""            # len 2 < 3 -> nothing copied
    assert t.engine_line(3) == b"x"           # len 3 -> 1 byte
    assert t.engine_line(4) == b""            # guard entries give nothing
    assert t.engine_line(5) == b""
    assert t.engine_line(-1) == b""           # negative id: engine writes NUL, returns 0
    with pytest.raises(IndexError):
        t.engine_line(6)
    assert t.validate() == []
    with pytest.raises(IndexError):
        t.line(4)


def test_engine_line_cap() -> None:
    tab, blob = _table([b"a" * 1000])
    t = TextTable("en", parse_offset_table(tab), blob)
    assert len(t.engine_line(0)) == 0x2FF     # min(len, 0x301) - 2


def test_offset_table_errors_and_mangetsu_layout() -> None:
    with pytest.raises(TextFormatError):
        parse_offset_table(struct.pack(">3I", 0, 5, 5))           # no end mark
    with pytest.raises(TextFormatError):
        parse_offset_table(struct.pack(">3I", 0xFFFFFFFF, 0, 0))  # data after the end mark
    with pytest.raises(TextFormatError):
        parse_offset_table(b"\0\0\0")
    tab3, blob = _table([b"ab", b"c"], end_marks=3)                 # mangetsu repack writes three
    offs = parse_offset_table(tab3)
    assert TextTable("en", offs, blob).count == 2 and TextTable("en", offs, blob).validate() == []
    with pytest.raises(TextFormatError):
        parse_offset_table(tab3, strict=True)
    assert parse_offset_table(_table([b"ab"])[0], strict=True) == (0, 4, 4)


def test_ruby_depends_on_language() -> None:
    assert ruby_enabled("ja") and ruby_enabled("zc") and ruby_enabled("zt") and ruby_enabled("ko")
    assert not ruby_enabled("en")
    ja = tokenize("なれた<畳|くさ>と", "ja")
    assert ja == [Text("なれた"), Ruby("畳", "くさ", "<畳|くさ>"), Text("と")]
    en = tokenize('"<...It\'s cold.>"', "en")
    assert en == [Text('"<...It\'s cold.>"')]
    assert plain_text("<障|か><子|み>", "ja") == "障子"
    assert plain_text("<障|か><子|み>", "ja", ruby="paren") == "障(か)子(み)"


def test_ruby_malformed_follows_engine_states() -> None:
    assert tokenize("<abc>d", "ja") == [Ruby("abc", "", "<abc>"), Text("d")]
    assert tokenize("<a|b|c>", "ja") == [Ruby("a", "bc", "<a|b|c>")]   # extra '|' swallowed
    assert tokenize("x>y", "ja") == [Text("x"), Code(">", None, ">"), Text("y")]
    assert tokenize("x>y", "en") == [Text("x>y")]
    assert tokenize("a|b", "ja") == [Text("a|b")]                       # '|' outside ruby = glyph
    # a new '<' restarts the group (state 1); the string end closes an open group
    two = tokenize("<a<b|c>", "ja")
    assert two == [Ruby("a", "", "<a"), Ruby("b", "c", "<b|c>")] and not two[0].closed
    assert tokenize("<a|b", "ja") == [Ruby("a", "b", "<a|b")]


def test_ruby_inner_markup_is_processed() -> None:
    toks = tokenize("<a@c1b^|x[heart]>c", "ja")
    assert len(toks) == 2 and isinstance(toks[0], Ruby)
    r = toks[0]
    assert (r.base, r.ruby, r.raw) == ("ab", "x", "<a@c1b^|x[heart]>")
    assert Code("c", "1", "@c1") in r.base_tokens and Newline() in r.base_tokens
    assert Icon("heart", 0, "[heart]") in r.ruby_tokens
    assert [type(t).__name__ for t in iter_tokens(toks)][:3] == ["Ruby", "Text", "Code"]
    runs = to_runs(toks, "ja")
    assert runs[0].kind == "ruby" and runs[0].style.color is None
    assert runs[-1].text == "c" and runs[-1].style.color == COLOR_RGB565[1]   # @c1 inside the group
    assert markup_census(["<a@c1b|x>"], "ja")["in ruby: @c1"]["count"] == 1


def test_codes_and_params() -> None:
    toks = tokenize("@g@b@c5abc@r^@t[heart2]@k", "ja")
    assert toks == [Code("g", None, "@g"), Code("b", None, "@b"), Code("c", "5", "@c5"), Text("abc"),
                    Code("r", None, "@r"), Newline(), Code("t", None, "@t"),
                    Icon("heart2", 1, "[heart2]"), Code("k", None, "@k")]
    assert tokenize("@x12z", "ja") == [Code("x", "12", "@x12"), Text("z")]
    assert tokenize("@y1234", "ja") == [Code("y", "123", "@y123"), Text("4")]
    assert tokenize("@y", "ja") == [Code("y", None, "@y")]
    assert tokenize("@s-64,a", "ja") == [Code("s", "-64", "@s-64,"), Text("a")]
    assert tokenize("@q(1,2)z", "ja") == [Code("q", "(1,2)", "@q(1,2)"), Text("z")]
    assert tokenize("@1x^", "ja") == [Code("1", None, "@1"), Text("^", "x"), Newline()]
    assert tokenize("[nope]", "ja") == [Text("[nope]")]
    assert tokenize("ab@", "ja") == [Text("ab"), Code("", None, "@")]     # engine overruns here
    assert len(ICON_NAMES) == 39 and ICON_NAMES[7] == "ber00" and ICON_NAMES[38] == "zap00"


def test_tokenizer_counts_bytes() -> None:
    # a one-byte parameter takes only the lead byte of a multi-byte character
    toks = tokenize("@cé!", "ja")
    assert toks[0] == Code("c", "\udcc3", "@c\udcc3") and toks[1] == Text("\udca9!")
    # '(...)' window is 255 bytes: 127 x 'é' (254 bytes) fits, 128 (256 bytes) does not
    ok = tokenize("@q(" + "é" * 127 + ")z", "ja")
    assert ok[0].code == "q" and ok[0].arg is not None and ok[1] == Text("z")  # type: ignore[union-attr]
    no = tokenize("@q(" + "é" * 128 + ")z", "ja")
    assert no[0] == Code("q", None, "@q") and isinstance(no[1], Text) and no[1].text.startswith("(")
    # @s reads at most 256 bytes without a terminator
    s = tokenize("@s" + "1" * 300, "ja")
    assert s[0] == Code("s", "1" * 256, "@s" + "1" * 256) and s[1] == Text("1" * 44)


def test_script_level_tokens() -> None:
    toks = tokenize("@x$000123^$000124@k@e trailing", "ja", script=True)
    assert toks[:5] == [Code("x", None, "@x"), Ref(123, "$000123"), Newline(), Ref(124, "$000124"),
                        Code("k", None, "@k")]
    assert toks[5] == Code("e", None, "@e")
    assert toks[6] == Text(" trailing")          # the engine discards text after @e/@n
    assert tokenize("$12", "ja", script=True) == [Text("$12")]
    assert tokenize("$000001", "ja") == [Text("$000001")]  # not script level


def test_expand_refs_engine_semantics() -> None:
    lines = {5: b"five", 7: b"seven"}
    assert expand_refs("a$000005^$000007b", lambda i: lines[i]) == b"afive^sevenb"
    assert expand_refs("x$-00001y", lambda i: b"?") == b"xy"     # negative id inserts nothing
    assert expand_refs(b"ab\0cd", lambda i: b"") == b"ab"
    # output cap: the engine stops once more than 0x2FB bytes are written -> 0x2FC max
    assert ENGINE_EXPAND_MAX == 0x2FC
    assert len(expand_refs("$000005" * 400, lambda i: lines[i])) == 0x2FC
    assert len(expand_refs("x" * 1000, lambda i: b"")) == 0x2FC
    # src_len bounds the source; mode 0 is a plain copy without expansion
    assert expand_refs("abc$000005", lambda i: lines[i], src_len=2) == b"ab"
    assert expand_refs("abc$000005", lambda i: lines[i], src_len=4) == b"abcfive"   # '$' at i=3 < 4
    assert expand_refs("a$000005", lambda i: lines[i], expand=False) == b"a$000005"
    assert expand_refs("abc", lambda i: b"", src_len=0) == b""
    # a '$' near the end reads past the message (NUL here) -> negative id
    assert expand_refs("ab$1", lambda i: b"?") == b"ab"           # 4,900,000 - 5,333,328 < 0
    seen: list[int] = []
    assert expand_refs("ab$12", lambda i: seen.append(i) or b"?") == b"ab?" and seen == [66672]


def test_languages_and_choices() -> None:
    assert effective_languages(0xFFFF, 0) == (0, 0)       # shipped default -> system language
    assert effective_languages(0xFFFF, 11) == (2, 2)
    assert effective_languages(0xFFFF, 10) == (3, 3)
    assert effective_languages(0xFFFF, 1) == (1, 1)
    assert effective_languages(0xFFFF, None) == (1, 1)    # system call failed -> English
    assert effective_languages(1, 0) == (1, 1)
    assert effective_languages(4, 0) == (4, 0)            # edited save: ko text, ja layout
    assert choice_number_id(0) == CHOICE_NUMBER_BASE and choice_number_id(8) == 43968
    assert choice_number_id(9) == choice_number_id(12) == CHOICE_NUMBER_OVERFLOW
    assert choice_slot_text(1, "$042600") == "$043961$042600"


def test_runs_styles_and_flags() -> None:
    en = to_runs(tokenize("@iA@rB@g@iC@rD", "en"), "en")
    assert [(r.text, r.style.style) for r in en] == [("A", 3), ("B", 0), ("C", 4), ("D", 1)]
    ja = to_runs(tokenize("@iA@rB", "ja"), "ja")
    assert [(r.text, r.style.style) for r in ja] == [("A", 1), ("B", 0)]
    col = to_runs(tokenize("@c1a@c7b@c0c", "ja"), "ja")
    assert [(r.text, r.style.color, r.style.color_ext) for r in col] == [
        ("a", COLOR_RGB565[1], 0), ("b", COLOR_RGB565[1], 7), ("c", None, 0)]
    assert to_runs(tokenize("@c1a@cxb", "ja"), "ja")[1].style == Style(color=COLOR_RGB565[1])  # @cx ignored
    seq = to_runs(tokenize("ab^@tcd", "ja"), "ja")
    assert [(r.kind, r.seq) for r in seq] == [("text", 1), ("newline", 3), ("seq", 0), ("text", 0)]
    assert glyph_flags(Style(style=1, anim=2)) == 1 | 4 | 8
    assert glyph_flags(Style(style=1, anim=2), ligature_piece=True) == 1 | 4
    assert glyph_flags(Style(style=5, flag_b=True)) == 1 | 0x600000 | 0x10000


def test_census_counts() -> None:
    c = markup_census(["@g<a|b>x[heart]", "^@t", '"<x>"'], "ja")
    assert c["@g"]["count"] == 1 and c["ruby <base|ruby>"]["lines"] == 2   # '<x>' is ruby state in ja
    e = markup_census(['"<x>"'], "en")
    assert "ruby <base|ruby>" not in e and e["literal '<'"]["count"] == 1
    assert c["[heart] (icon 0)"]["first_ids"] == [0]
    assert c["^ (newline)"]["count"] == 1 and c["@t"]["first_ids"] == [1]


def test_text_commands_synthetic() -> None:
    src = ("_VPLY(A,00001);_ZM00001($000001@n);_MSAD();_SELR(1,$042600,1);"
           "_SELR(0,`001:label);_ZD00002(@x$000002@k@e);_VPL2(B,00002);_VPLY(C,00003);_ZM00003($000003)")
    cmds = list(text_refs.iter_text_commands([("t", src)]))
    assert [c.op for c in cmds] == ["ZM", "MSAD", "SELR", "SELR", "ZD", "ZM"]
    zm, msad, sel, sel2, zd, _ = cmds
    assert (zm.message, zm.end_mode, zm.refs) == ("$000001", 1, [1])
    assert (msad.message, msad.end_mode) == ("", 0)
    assert (sel.choice, sel.message, sel.extra, sel.locked, sel.end_mode) == (1, "$042600", ("1",), True, None)
    assert sel.choice_slot(1) == "$043961$042600"
    assert (sel2.message, sel2.locked) == ("label", False)       # 5-byte '`' prefix skipped
    assert (zd.message, zd.end_mode, zd.refs) == ("@x$000002@k", 2, [2])
    vt = text_refs.voice_text_counts([("t", src)])
    assert vt["VPLY"] == Counter({5: 1, 1: 1}) and vt["VPL2"] == Counter({0: 1})


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------

@pytest.fixture(scope="module")
def st() -> ScriptText:
    return ScriptText()


@pytest.fixture(scope="module")
def scripts() -> list[tuple[str, str]]:
    return text_refs.load_scripts()


@pytest.fixture(scope="module")
def cmds(scripts: list[tuple[str, str]]) -> list[text_refs.TextCommand]:
    return list(text_refs.iter_text_commands(scripts))


@needs_game
def test_real_counts_and_validation(st: ScriptText) -> None:
    assert st.count == N_LINES
    for lg in LANGS:
        t = st.table(lg)
        assert t.count == N_LINES
        assert len(t.offsets) + st.end_marks[lg] == 43972     # N offsets, size, size, 0xFFFFFFFF
        assert st.end_marks[lg] == 1
    assert st.validate() == []
    assert len(st.table("ja").blob) == 3410336 and len(st.table("en").blob) == 2724070


@needs_game
def test_real_known_line(st: ScriptText) -> None:
    # Line 3 (the fourth message of the opening script) is checked by its shape, not its wording:
    # the repository quotes no story text (docs/formats/text.md 2.3 gives the same facts).
    en = st.get_line("en", 3)
    assert len(en) == 70 and en.startswith("  Th") and en.endswith(".")
    assert st.find("en", en[2:12]) == [3]                           # its first words occur in no other line
    ja = st.get_line("ja", 3)
    assert len(ja) == 26 and len(ja.encode("utf-8")) == 60
    assert ja[0] == "　" and ja.endswith("。")
    kinds = [type(t).__name__ for t in tokenize(ja, "ja")]
    assert kinds == ["Text", "Ruby", "Text", "Ruby", "Ruby", "Text"]  # three ruby groups
    rubies = [t for t in tokenize(ja, "ja") if isinstance(t, Ruby)]
    assert [(len(r.base), len(r.ruby)) for r in rubies] == [(1, 2), (1, 1), (1, 1)]
    assert len(plain_text(ja, "ja")) == 13
    assert len(plain_text(ja, "ja", ruby="paren")) == 23
    zc, zt = st.get_line("zc", 3), st.get_line("zt", 3)
    assert (len(zc), len(zc.encode("utf-8"))) == (19, 57) and zc[0] == "　"   # simplified
    assert (len(zt), len(zt.encode("utf-8"))) == (16, 48) and zt[0] == "　"   # traditional
    assert zc[1:3] == zt[1:3]                                        # both variants open with the same two characters
    en0 = st.get_line("en", 0)
    assert len(en0) == 75 and en0.startswith("Wh") and not en0.startswith(" ")   # string 0 has no indent


@needs_game
def test_real_ko_is_placeholder(st: ScriptText) -> None:
    assert all(line == "\u3000" for line in st.lines("ko"))


@needs_game
def test_real_markup_census(st: ScriptText) -> None:
    census = {lg: markup_census(st.lines(lg), lg) for lg in LANGS}
    ja, en = census["ja"], census["en"]
    assert ja["ruby <base|ruby>"]["count"] == 3476
    assert ja["@g"]["count"] == 127 and en["@g"]["count"] == 128
    assert en["@i"]["count"] == 1288 and en["@r"]["count"] == 1288
    assert ja["@t"]["first_ids"] == [9328, 23613, 36678]
    assert en["^ (newline)"]["count"] == 175
    assert "ruby <base|ruby>" not in en and en["literal '<'"]["count"] == 236
    assert ja["[ber00] (icon 7)"]["count"] == 52
    # no unknown codes, nothing inside ruby groups, no unclosed group anywhere
    known = {"@g", "@b", "@t", "@k", "@i", "@r", "@o", "@c5"}
    for lg in LANGS:
        keys = set(census[lg])
        assert {k for k in keys if k.startswith("@")} <= known, lg
        assert not any(k.startswith("in ruby") or "not closed" in k for k in keys), lg


@needs_game
def test_real_tokens_roundtrip(st: ScriptText) -> None:
    for lg in ("ja", "en"):
        for i in range(0, N_LINES, 7):
            line = st.get_line(lg, i)
            assert "".join(t.raw for t in tokenize(line, lg)) == line, (lg, i)


@needs_game
def test_real_choice_numbers(st: ScriptText) -> None:
    assert st.get_line("ja", CHOICE_NUMBER_BASE) == "１．"
    assert st.get_line("en", CHOICE_NUMBER_OVERFLOW) == "0. "
    assert [st.get_line("en", choice_number_id(n)) for n in range(9)] == [f"{n + 1}. " for n in range(9)]
    assert st.expand(choice_slot_text(1, "$042600"), "en", src_len=0xFA).startswith("2. ...No, this is")


@needs_game
def test_real_script_refs(st: ScriptText, cmds: list[text_refs.TextCommand]) -> None:
    idx = text_refs.ref_index(cmds)
    assert len(cmds) == 44109
    assert Counter(c.op for c in cmds) == Counter({"ZM": 42590, "SELR": 824, "MSAD": 678, "ZD": 17})
    assert sum(len(c.refs) for c in cmds) == 43930
    assert len(idx) == 43902 and max(idx) == 43958
    first = next(c for c in cmds if c.name == "00_02" and c.refs)
    assert first.op == "ZM" and first.refs == [0]
    assert st.expand(first.message, "en").startswith("While the world")
    # @e / @n terminate the _ZM/_ZD/_MSAD message argument
    c = next(c for c in cmds if c.arg.endswith("^@n"))
    assert c.end_code == "n" and c.end_mode == 1 and not c.message.endswith("@n")
    # _SELR: the message is argument 1 only; 6 story choices carry a 3rd argument "1"
    selr = [c for c in cmds if c.op == "SELR"]
    assert all("," not in c.message for c in selr)
    assert sorted({c.message for c in selr if c.locked}) == ["$042600", "$042642", "$042751", "$042759"]
    assert sum(1 for c in selr if c.locked) == 6 and sum(1 for c in selr if c.extra) == 6


@needs_game
def test_real_scene_has_no_selr_artefact(st: ScriptText, scripts: list[tuple[str, str]]) -> None:
    rows = text_refs.scene_transcript(st, scripts, "03_00_ARC03_5B", "en")
    choices = [r for r in rows if "> choice" in r]
    assert choices and not any(r.rstrip().endswith(",1") for r in choices)
    assert any("(locked)" in r for r in choices)


@needs_game
def test_real_unreferenced_classes(st: ScriptText, cmds: list[text_refs.TextCommand]) -> None:
    cls = text_refs.classify_unreferenced(st, cmds)
    assert len(cls) == 67
    assert Counter(cls.values()) == Counter({"choice-number": 10, "image-text": 11, "merged-fragment": 22,
                                             "blank": 4, "not-shown": 20})
    assert [i for i, v in cls.items() if v == "image-text"] == sorted(text_refs.IMAGE_TEXT_IDS)
    # the 15 English "<->" placeholders are all merged fragments that no script shows
    ph = [i for i, line in enumerate(st.lines("en")) if line == "<->"]
    assert len(ph) == 15 and all(cls.get(i) == "merged-fragment" for i in ph)


@needs_game
def test_real_expanded_lengths(st: ScriptText, cmds: list[text_refs.TextCommand]) -> None:
    longest = {lg: max(len(st.expand_bytes(c.message, lg)) for c in cmds) for lg in ("ja", "en", "zc", "zt")}
    assert longest == {"ja": 588, "en": 467, "zc": 492, "zt": 567}
    assert max(longest.values()) < ENGINE_EXPAND_MAX


@needs_game
def test_real_combined_messages_and_zd(st: ScriptText, cmds: list[text_refs.TextCommand]) -> None:
    groups = text_refs.message_groups(cmds)
    assert len(groups) == 42590 and max(len(g) for g in groups) == 12
    longest = {lg: max(len(st.expand_bytes("".join(c.message for c in g), lg)) for g in groups)
               for lg in ("ja", "en")}
    assert longest == {"ja": 669, "en": 547} and longest["ja"] < ENGINE_EXPAND_MAX
    assert text_refs.zd_only_ids(cmds) == [7695, 7927, 8662, 8663, 8736, 8737, 8749, 8761, 8912,
                                           9036, 9308, 10483, 10484]


@needs_game
def test_real_voice_links(scripts: list[tuple[str, str]]) -> None:
    vt = text_refs.voice_text_counts(scripts)
    assert sum(vt["VPLY"].values()) == 16689 and sum(vt["VPL2"].values()) == 16
    assert vt["VPLY"][0] == 0                 # every _VPLY is followed by >= 1 text command
    assert vt["VPLY"][1] == 10641 and max(vt["VPLY"]) == 103
