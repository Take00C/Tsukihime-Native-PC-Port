"""Tests for tsuki.textbox (ADV text box: layout pass 1 / pass 2, block draw, page placement).

Expected values come from the game's own code run under unicorn (scratch/textbox/emu.py):
the full verification (scratch/textbox/verify_layout.py) compared every glyph record of all
4 x 43,969 script lines bit for bit; samples/textbox/engine_layout_golden.json freezes 601 of
those engine results so these tests need neither unicorn nor much time.  Real-data tests skip
when the game folder is missing.
"""
from __future__ import annotations

import json
import struct
from pathlib import Path

import pytest

from tsuki import PROJECT_ROOT, game_dir
from tsuki import textbox as tb

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None, reason="game folder not found (TSUKI_GAME_DIR)")
GOLDEN = PROJECT_ROOT / "samples" / "textbox" / "engine_layout_golden.json"


def f32bits(x: float) -> int:
    return struct.unpack("<I", struct.pack("<f", x))[0]


# ---------------------------------------------------------------------------
# synthetic (no game data)
# ---------------------------------------------------------------------------
def test_is_wide_rule() -> None:
    assert not tb.is_wide(0x41) and not tb.is_wide(0xC3A9)            # 'A', 'é'
    assert tb.is_wide(0xC397) and tb.is_wide(0xC2B1)                    # ×, ±
    assert tb.is_wide(0xE38182)                                         # あ
    assert not tb.is_wide(0xEFBDB1)                                     # halfwidth ｱ
    assert tb.is_wide(0xF09F9880)                                       # 4-byte


def test_kinsoku_classes() -> None:
    def cls(ch: str) -> int:
        return tb._wide_class(int.from_bytes(ch.encode(), "big"))
    assert all(cls(c) == 0x71 for c in "。」』），．：；々》’”")
    assert all(cls(c) == 0x0E for c in "「『（‘“")
    assert all(cls(c) == 0x0D for c in "、ぁっゃァッャー・！？‧")
    assert cls("あ") == 1 and cls("漢") == 1 and cls("　") == 1


def test_classify_english_words() -> None:
    n, cls, wl = tb.classify("  The cat, sat.", 1)
    assert n == 15
    # elements: ' ',' ','T','h','e',' ','c','a','t',',',' ','s','a','t','.'
    # 0 = a word starts here (break allowed before it), 2 = inside a word / a space after a
    # letter, 3 = ',' '.' ; the space before a word stays at the end of the line
    assert list(cls[:15]) == [2, 0, 0, 2, 2, 2, 0, 2, 2, 3, 2, 0, 2, 2, 3]
    # wordlen[i-1] = length + 1 of the word starting at element i ('The', 'cat,', 'sat.')
    assert wl[:15] == [1, 4, 0, 0, 0, 5, 0, 0, 0, 0, 4, 0, 0, 0, 0]


def test_classify_budget_and_ruby_skip() -> None:
    n, _cls, _wl = tb.classify("あ" * 1300, 0)
    assert n == tb.ELEMENT_BUDGET == 1200
    n1, _c, _w = tb.classify("<漢|かん>字", 0)
    n2, _c, _w = tb.classify("<漢|かん>字", 0, flags=0x2000)
    assert (n1, n2) == (7, 4)                  # flag 0x2000 skips the reading in pass 1
    n3, _c, _w = tb.classify("@s12,AB@x123C@q(zz)D", 0)
    assert n3 == 10                            # pass 1 knows no '@s' parameter


def test_main_box_width_rule() -> None:
    assert tb.main_box_width(0) == 474 and tb.main_box_width(1) == 574
    assert tb.main_box_width(1, window=42) == 600 and tb.main_box_width(0, window=42) == 600
    assert tb.main_box_width(1, block=14) == 474 and tb.main_box_width(1, 480) == 480


def test_window_table_and_bubbles() -> None:
    assert len(tb.WINDOW_TABLE) == 56
    w0 = tb.window_for(0)
    assert w0 is not None and (w0.disp_child, w0.x_px, w0.y_px, w0.alpha) == (11, 0.0, 0.0, 1.0)
    assert tb.window_for(2).disp_child == 13 and tb.window_for(3).disp_child == 22   # type: ignore[union-attr]
    w5 = tb.window_for(5, opacity=128)
    assert w5 is not None and w5.bubble and w5.disp_child == 24
    assert (w5.x_px, w5.y_px) == (492.0, 589.5) and abs(w5.alpha - 0.784314) < 1e-6
    w33 = tb.window_for(33)
    assert w33 is not None and abs(w33.scale_x - 1.12) < 1e-9
    assert tb.window_for(56) is None
    assert tb.bubble_text_origin(5) == (213.5, 244.5)
    assert tb.main_box_params(0, window=5).flags == 1 << 20
    assert tb.main_box_params(1).x == 30.0 and tb.main_box_params(0).y == 49.0
    assert tb.main_box_params(0, read=True, read_color_mode=1).color == 0xDC6D


def _fake_block(lines: int, h: float = 17.02) -> tb.TextBlock:
    return tb.TextBlock(n=1, width_arg=474, height=220, flags=0, spacing=0, leading=9, box_width=474,
                        sx=0.24666667, sy=0.24666667, nlines=lines, last_glyph_line=lines - 1,
                        line_glyphs=[1] * 32, line_width=[100.0] * 32, line_height=[h] * 32,
                        records=[], classes=b"", wordlen=[], end_ctr=2)


def test_page_positions_and_lines() -> None:
    blocks = [_fake_block(2), _fake_block(1), _fake_block(2)]
    assert tb.last_line_offset(blocks[0]) == 26 and tb.last_line_offset(blocks[1]) == 0
    pos = tb.page_positions(blocks, 1)
    assert pos == [(30.0, 35.0), (30.0, 87.0), (30.0, 113.0)]
    pos = tb.page_positions(blocks, 0, blank_before=[0, 1, 0])
    assert [y for _x, y in pos] == [49.0, 127.0, 153.0]
    xs, ys, total = tb.line_offsets(blocks[0], 87.0, 49.0)
    assert xs == [87.0, 87.0] and ys[0] == 49.0 and f32bits(ys[1]) == f32bits(75.02000427246094)
    xs, _ys, _t = tb.line_offsets(blocks[0], 10.0, 0.0, draw_flags=0x100)
    assert xs[0] == 10.0 + (474 - 100) / 2


def test_rgb565_bytes() -> None:
    assert tb.rgb565_bytes(0xFFFF) == (0xF8, 0xF8, 0xF8)
    assert tb.rgb565_bytes(0xF800) == (0xF8, 0x00, 0x00)
    assert tb.rgb565_bytes(0xDC6D) == (0xD8, 0x88, 0x68)


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------
@pytest.fixture(scope="module")
def fonts():  # type: ignore[no-untyped-def]
    from tsuki.font import LANGS, Font
    from tsuki.font_eboot import EbootFontTables
    assert GAME is not None
    tables = EbootFontTables.open(GAME / "eboot.bin")
    return {i: Font(LANGS[i], tables=tables) for i in range(4)}


@needs_game
def test_golden_engine_records(fonts) -> None:  # type: ignore[no-untyped-def]
    from tsuki.font import LANGS
    from tsuki.text import ScriptText
    data = json.loads(Path(GOLDEN).read_text())
    st = ScriptText()
    lines = {lg: st.lines(LANGS[lg]) for lg in range(4)}
    bad = []
    for case in data["cases"]:
        lg = case["lang"]
        b = tb.layout_box(lines[lg][case["id"]], lg, font=fonts[lg], flags=case["flags"], window=case["window"])
        got = [[r.index, r.flags, r.color, r.ext, r.line, r.tex, r.x, r.y, r.x2, r.y2, r.ctr,
                f32bits(r.dx), f32bits(r.dy), f32bits(r.sx), f32bits(r.sy)] for r in b.records]
        ok = (got == case["records"] and b.n == case["n"] and b.nlines == case["nlines"]
              and [f32bits(v) for v in b.line_height[:len(case["line_height"])]] == case["line_height"]
              and [f32bits(v) for v in b.line_width[:len(case["line_width"])]] == case["line_width"])
        if not ok:
            bad.append((lg, case["id"], case["flags"]))
    assert len(data["cases"]) == 601
    assert bad == []


@needs_game
def test_prologue_lines(fonts) -> None:  # type: ignore[no-untyped-def]
    from tsuki.text import ScriptText
    st = ScriptText()
    en = tb.layout_message(st.get_line("en", 3), 1, font=fonts[1])
    assert en.box_width == 574 and en.nlines == 2
    assert [len(x) for x in tb.break_lines(en)] == [59, 11]             # "... hangs " / "in the air."
    assert f32bits(en.line_width[0]) == f32bits(543.9000854492188)
    ja = tb.layout_message(st.get_line("ja", 3), 0, font=fonts[0])
    ruby = [r for r in ja.records if r.ruby]
    assert [round(r.dx, 3) for r in ruby] == [67.216, 74.516, 103.661, 120.188]
    assert all(r.dy == pytest.approx(-7.04) and r.sx == pytest.approx(0.1) and r.sy == pytest.approx(0.11)
               for r in ruby)
    assert ja.line_height[0] == pytest.approx(17.02)


@needs_game
def test_wrap_kinsoku_and_caret(fonts) -> None:  # type: ignore[no-untyped-def]
    s = "　" + "あいうえおかきくけこさしすせそたちつてとなにぬねの" * 3
    b = tb.layout_box(s, 0, font=fonts[0])
    assert [len(x) for x in tb.break_lines(b)] == [28, 28, 20]
    # a closing bracket that would start a line hangs on the previous one
    k = tb.layout_box("　" + "あ" * 27 + "」い", 0, font=fonts[0])
    assert [len(x) for x in tb.break_lines(k)] == [29, 1]
    c = tb.layout_box("A^B^^C", 1, font=fonts[1])
    assert c.nlines == 4 and c.line_height[2] == pytest.approx(20 * 0.74 / 3)
    nb = tb.layout_box("A^B", 1, font=fonts[1], flags=0x4000)
    assert nb.nlines == 1


@needs_game
def test_block_quads_reveal_and_outline(fonts) -> None:  # type: ignore[no-untyped-def]
    b = tb.layout_box("ABCDE", 1, font=fonts[1])
    q = tb.block_quads(b, 30.0, 35.0, head=3.5, tail=2.0)
    fills = [x for x in q if x.kind == "fill"]
    assert len(fills) == 3                                     # ctr 4, 5 > head: not drawn
    assert [round(f.alpha, 4) for f in fills] == [1.0, 1.0, round((3.5 - 3) / 1.5, 4)]
    outl = [x for x in q if x.kind == "outline"]
    assert outl[0].v0 == fills[0].v0 + 512
    assert not [x for x in tb.block_quads(b, 0, 0, draw_flags=0x20) if x.kind == "outline"]
    assert len([x for x in tb.block_quads(b, 0, 0, draw_flags=0x30) if x.kind == "shadow"]) == 5
