"""Tests for tsuki.font / tsuki.font_eboot (fonts: ft_<lang>.mrg + eboot glyph tables).

Real-data tests skip when the game folder is missing.  Expected glyph values
were produced by emulating the game's own lookup code (eboot VA 0x11150 /
0x11750 / 0x11ED0 under unicorn, scratch/fonts/verify_emu.py: 334,800 +
146,040 + 1,620 + 1,440 cases, 0 mismatches) and are pinned here.
"""
from __future__ import annotations

import mmap
import struct
from typing import Iterator

import numpy as np
import pytest

from tsuki import game_dir, image_dir
from tsuki.font import (
    CELL, LANGS, MAX_LINE_INDEX, OUTLINE_DY, Font, FontPages, PlacedGlyph, Glyph,
    alpha_8bit, decode_page_header, markup_end, render_text, substitute_redaction_frames,
    unswizzle_csm1,
)
from tsuki.font_eboot import TAGS, EbootFontTables, pack_utf8, unpack_utf8

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None, reason="game folder not found (TSUKI_GAME_DIR)")


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------

def test_unswizzle_csm1_is_bit_3_4_swap() -> None:
    assert unswizzle_csm1(0x08) == 0x10 and unswizzle_csm1(0x10) == 0x08
    assert unswizzle_csm1(204) == 212 and unswizzle_csm1(255) == 255
    perm = [unswizzle_csm1(i) for i in range(256)]
    assert sorted(perm) == list(range(256))
    assert all(unswizzle_csm1(unswizzle_csm1(i)) == i for i in range(256))


def test_pack_utf8_keys() -> None:
    assert pack_utf8("A") == 0x41
    assert pack_utf8("é") == 0xC3A9
    assert pack_utf8("上") == 0xE4B88A            # kanji "ue"
    assert pack_utf8("\U0001F600") == 0xF09F9880
    for ch in ("A", "é", "あ", "―", "ﾞ"):
        assert unpack_utf8(pack_utf8(ch)) == ch


def test_page_header_parse_synthetic() -> None:
    pal = bytearray()
    for i in range(256):
        pal += bytes((255, 255, 255, i >> 1))
    hdr = struct.pack("<8H", 1024, 1024, 1024, 1024, 1, 1, 1, 1) + bytes(pal) + b"\x01"
    fields, p, flag = decode_page_header(hdr)
    assert fields[:2] == (1024, 1024) and flag == 1
    # stored order is CSM1-swizzled: pixel index 8 reads stored entry 16
    assert p[8, 3] == 16 >> 1 and p[16, 3] == 8 >> 1


def test_alpha_expansion_matches_engine() -> None:
    # engine VA 0xD90B0: 0x80 -> 0xFF, else (2a) & 0xFF
    a = np.array([0, 1, 0x40, 0x7F, 0x80, 0x81, 0xFF], dtype=np.uint8)
    assert alpha_8bit(a).tolist() == [0, 2, 0x80, 0xFE, 0xFF, 0x02, 0xFE]


def test_markup_end_pass2_rules() -> None:
    def end(s: str) -> int:
        return markup_end(s.encode(), 0)
    assert end("@c5x") == 3 and end("@h0x") == 3 and end("@w9") == 3 and end("@a") == 2
    assert end("@s12,x") == 5 and end("@s-3)x") == 5 and end("@s12") == 4
    assert end("@x123x") == 5 and end("@x1234") == 5 and end("@xA") == 2 and end("@y7") == 3
    for op in "12bgiklioprtuzT":
        assert end("@" + op + "abc") == 2, op
    assert end("@q(abc)z") == 7 and end("@q(abc") == 2 and end("@e") == 2
    assert end("@q(" + "a" * 254 + ")") == 3 + 254 + 1         # ')' within 255 bytes
    assert end("@q(" + "a" * 255 + ")") == 2                   # too far: group not skipped


def test_redaction_substitution_triangular() -> None:
    def tag(k: int) -> PlacedGlyph:
        j = k - 7
        g = Glyph(TAGS[k], 7, k, 0x5F, (j % 14) * 68, (j // 14) * 73, 68, 73, 5.0, "tag", k)
        return PlacedGlyph(g, 0.0, 0.0, 1.0, 1.0)
    plain = PlacedGlyph(Glyph("a", 1, 0x61, 0x22, 0, 0, 40, 73, 0.0, "record", 0, "en_s0"), 0, 0, 1, 1)
    out = substitute_redaction_frames([plain, tag(7), tag(7), tag(7)])
    assert out[0] is plain
    # n = 3 -> cells 3, 4, 5 of the page-62 grid (T(2) = 3)
    assert [(p.glyph.tex, p.glyph.x, p.glyph.y) for p in out[1:]] == [
        (0x5F, 3 * 68, 0), (0x5F, 4 * 68, 0), (0x5F, 5 * 68, 0)]
    out = substitute_redaction_frames([tag(7)] * 21)
    k_last = 20 * 21 // 2 + 20                          # 230 -> page 64
    assert out[-1].glyph.tex == 0x5F + k_last // 98 == 0x61


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------

@pytest.fixture(scope="module")
def tables() -> EbootFontTables:
    assert GAME is not None
    return EbootFontTables.open(GAME / "eboot.bin")


@pytest.fixture(scope="module")
def fonts(tables: EbootFontTables) -> Iterator[dict[str, Font]]:
    out = {lang: Font(lang, tables=tables) for lang in LANGS}
    yield out
    for f in out.values():
        f.close()


@needs_game
def test_eboot_image_segments(tables: EbootFontTables) -> None:
    img = tables.image
    vas = sorted(s.va for s in img.segments)
    assert vas[:3] == [0x0, 0x400000, 0x600000]
    assert img.read(0x2D352C, 9) == b"ft_ja.mrg"           # file-name table (text segment)
    assert img.read(0x2D3580, 7) == b"[heart]"


@needs_game
def test_eboot_tables_verify(tables: EbootFontTables) -> None:
    assert tables.verify() == []
    counts = {n: t.count for n, t in tables.lookups.items()}
    assert counts["narrow"] == 109 and counts["ja_wide"] == 2795
    assert counts["zc_wide"] == 3403 and counts["zt_wide"] == 3570
    assert counts["ja_s1"] == 701 and counts["ja_s2"] == 1174
    assert tables.wide_base == (59, 59, 59, 57, 59)
    assert tables.tags == TAGS
    assert sum(counts.values()) == 17423
    assert len(set().union(*(t.keys for t in tables.lookups.values()))) == 5571
    assert sum(r.count for r in tables.records.values()) == 6771
    # ja_s0 / ko_s0 end exactly where the next table starts: 104 / 103 records, not 109
    assert tables.records["ja_s2"].va - tables.records["ja_s0"].va == 104 * 10
    assert tables.records["ko_s2"].va - tables.records["ko_s0"].va == 103 * 10


@needs_game
def test_page_layout_all_languages() -> None:
    expect_nonblank = {"ja": 55, "en": 16, "zc": 64, "zt": 60, "ko": 0}
    for lang in LANGS:
        with FontPages.for_lang(lang) as fp:
            nonblank = 0
            for k in range(65):
                pg = fp.page(k)
                assert (pg.width, pg.height) == ((256, 256) if k == 0 else (1024, 1024))
                assert pg.header[6:8] == (1, 1)
                used = np.flatnonzero(np.bincount(pg.indices.ravel(), minlength=256))
                a = pg.palette[used, 3].astype(int)
                assert np.all(np.diff(a) >= 0), (lang, k)       # alpha ramp after CSM1 unswizzle
                rgb = pg.palette[used][a > 0, :3]
                assert np.all(rgb == 255), (lang, k)              # glyph colour is always white
                unused = np.setdiff1d(np.arange(256), used)
                fill = (0, 0, 0, 0x7F) if (pg.flag == 1 or k == 0) else (0, 0, 0, 0)
                assert np.all(pg.palette[unused] == fill), (lang, k)
                if pg.flag == 1:
                    nonblank += 1
                    assert pg.alpha.max() > 0
                else:
                    assert pg.alpha.max() == 0
            assert nonblank == expect_nonblank[lang], lang


@needs_game
def test_known_glyphs(fonts: dict[str, Font]) -> None:
    ja, en, zc, zt = fonts["ja"], fonts["en"], fonts["zc"], fonts["zt"]

    def r(f: Font, s: str, style: int, kind: int = 0) -> tuple:
        g = f.char_glyph(s.encode(), 0, style, kind=kind, scale=1.0)
        return (g.consumed, g.tex, g.x, g.y, g.w, g.h, round(g.adj, 3), g.source)

    assert r(ja, "A", 0) == (1, 34, 376, 146, 51, 73, 0.0, "record")
    assert r(ja, "上", 0) == (3, 72, 511, 219, 73, 73, 0.0, "wide")     # kanji, page 39
    assert r(ja, "上", 1) == (3, 38, 0, 0, 73, 73, 0.0, "record")       # gothic page 5
    assert r(ja, "。", 1) == (3, 36, 219, 292, 73, 73, 0.0, "record")
    assert r(en, "A", 0) == (1, 34, 371, 146, 61, 73, 0.0, "record")
    assert r(en, "A", 3) == (1, 40, 371, 146, 69, 73, -8.0, "record")       # italic adj
    assert r(en, "A", 4) == (1, 38, 372, 146, 66, 73, -7.0, "record")
    assert r(en, "A", 5) == (1, 42, 366, 146, 74, 73, -3.0, "record")
    assert r(en, "上", 0) == (3, 34, 0, 0, 73, 73, 0.0, "fallback")
    assert r(en, "ÿ", 0) == (2, 34, 0, 0, 42, 73, 0.0, "fallback")
    assert r(zc, "上", 0)[1:3] == (66, 657)
    assert r(zt, "上", 0)[1:4] == (67, 0, 219)
    assert r(en, "[heart]", 0) == (7, 94, 0, 0, 73, 73, 0.0, "tag")
    assert r(en, "[edia]", 0) == (6, 94, 307, 0, 46, 73, 0.0, "tag")
    assert r(en, "[ber14]", 0) == (7, 95, 0, 73, 68, 73, 5.0, "tag")
    assert r(en, "[ber14]", 0, kind=2)[6] == -0.5
    assert r(en, "[zap00]", 0) == (7, 97, 0, 219, 68, 73, 5.0, "tag")
    assert r(en, "[foo]", 0)[0] == 1                                          # plain '['
    # engine quirk: Japanese style-0 records stop at 104; U+00B0 and U+00E2.. (index 104..108) read past them
    g = ja.char_glyph("â".encode(), 0, 0)
    assert (g.index, g.in_range, g.tex, g.x) == (105, False, 44, 94)


@needs_game
def test_dash_joins(fonts: dict[str, Font]) -> None:
    f = fonts["ja"]
    k = pack_utf8("―")
    assert f.dash_variant(k, -1, k, line_start=False) == 3
    assert f.dash_variant(k, k, k, line_start=False) == 4
    assert f.dash_variant(k, k, -1, line_start=False) == 5
    assert f.dash_variant(k, k, -1, line_start=False, flag_b=True) == 3
    assert f.dash_variant(k, k, k, line_start=True) == 3
    assert f.dash_variant(k, -1, -1, line_start=False) == -1
    g = f.dash_glyph(4, 0)
    assert (g.tex, g.x, g.y, g.w, g.h, g.adj) == (0x5E, 187, 73, 68, 73, 5.0)
    assert fonts["zt"].dash_glyph(8, 0).y == 292
    assert fonts["en"].dash_glyph(0, 3).x == 511
    placed = f.layout("――――", scale=1.0)
    assert [p.glyph.source for p in placed] == ["dash"] * 4
    assert [p.glyph.index for p in placed] == [3, 4, 4, 5]


@needs_game
def test_dash_prev_is_previous_element(fonts: dict[str, Font]) -> None:
    """Engine prev pointer = start of the previous parse element (VA 0xC76A4), markup included."""
    f = fonts["ja"]
    p = f.layout("――@r――", markup=True, scale=1.0)
    assert [(q.glyph.source, q.glyph.index) for q in p] == [("dash", 3), ("dash", 5)] * 2
    p = f.layout("―@r―", markup=True, scale=1.0)
    assert [q.glyph.source for q in p] == ["wide", "wide"]
    b = "―@r―".encode()
    assert f.dash_variant_at(b, 5, 3, line_start=False) == -1      # prev element = '@r'
    assert f.dash_variant_at(b, 5, 0, line_start=False) == 5       # (if prev were the dash)
    assert f.dash_variant_at("――".encode(), 0, None, line_start=True) == 3


@needs_game
def test_char_glyph_nul_and_invalid_lead(fonts: dict[str, Font]) -> None:
    f = fonts["en"]
    g = f.char_glyph(b"\0A", 0, 0)
    assert (g.consumed, g.source, g.tex, g.x, g.y, g.w, g.h) == (0, "nul", 0xFFFF, 0, 0, 73, 73)
    assert f.char_glyph(b"", 0, 0).source == "nul"
    for lead in (0x80, 0xBF, 0xF8, 0xFF):
        g = f.char_glyph(bytes([lead, 0x41]), 0, 0)
        assert (g.consumed, g.source) == (0, "invalid")
    with pytest.raises(ValueError):
        f.layout("@1あ", markup=True)        # '^' over the lead byte leaves 2 continuation bytes
    assert [q.glyph.text for q in f.layout("AB\0CD")] == ["A", "B"]     # NUL ends the text


@needs_game
def test_ruby_and_caret_rules(fonts: dict[str, Font]) -> None:
    en, ja = fonts["en"], fonts["ja"]
    s = '"<I shall paint the walls.>"'
    assert "".join(q.glyph.text for q in en.layout(s, markup=True)) == s     # English: '<' '>' are glyphs
    assert "".join(q.glyph.text for q in en.layout(s, ruby=True)) == s.replace("<", "").replace(">", "")
    assert "".join(q.glyph.text for q in en.layout("a|b")) == "a|b"
    p = ja.layout("な<畳|くさ>と", scale=1.0)
    assert [q.glyph.text for q in p] == ["な", "畳", "く", "さ", "と"]
    assert [q.sx for q in p] == [1.0, 1.0, 0.5, 0.5, 1.0]
    assert [q.glyph.text for q in ja.layout("な<畳|くさ>と", show_ruby=False)] == ["な", "畳", "と"]
    assert [q.glyph.text for q in ja.layout("a>b")] == ["a", "b"]           # stray '>' swallowed
    # '^' forced line break (markup), literal with markup=False or after '@1'
    p = en.layout("A^B", markup=True, scale=1.0)
    assert [q.glyph.text for q in p] == ["A", "B"] and p[1].y > p[0].y and p[1].x == 0.0
    assert [q.glyph.text for q in en.layout("A^B")] == ["A", "^", "B"]
    assert [q.glyph.text for q in en.layout("A@1xB", markup=True)] == ["A", "^", "B"]
    assert [q.glyph.text for q in en.layout("A@2xB", markup=True)] == ["A", ")", "B"]
    p = en.layout("^" * (MAX_LINE_INDEX + 3) + "Z", markup=True)
    assert p == []                                   # a '^' on line 31 ends the text
    # style codes: English @i/@r italics, other languages @i = style 1
    p = en.layout("A@iA@gA@iA@rA@rA", markup=True)
    assert [q.glyph.table for q in p] == ["en_s0", "en_s3", "en_s1", "en_s4", "en_s1", "en_s0"]
    assert ja.layout("@iA", markup=True)[0].glyph.table == "ja_s1"


@needs_game
def test_placeholder_and_empty_cells(fonts: dict[str, Font]) -> None:
    """Table entries that exist but draw a placeholder or nothing (game data, not decoding)."""
    def crop(f: Font, ch: str) -> np.ndarray:
        g = f.char_glyph(ch.encode(), 0, 0)
        assert g.source == "wide" or g.source == "record", (f.lang, ch, g.source)
        return f.glyph_alpha(g)
    en, zc, zt, ja = fonts["en"], fonts["zc"], fonts["zt"], fonts["ja"]
    tofu = [crop(en, c) for c in "☆♡♥♪"]
    assert all(np.array_equal(tofu[0], t) for t in tofu) and tofu[0].max() > 0
    xbox = [crop(zc, c) for c in "¶âäë"]
    assert all(np.array_equal(xbox[0], t) for t in xbox) and xbox[0].max() > 0
    for c in "・―♡♥♪":
        assert crop(zt, c).max() == 0, c
    assert crop(ja, "⁉").max() == 0
    # zt row of the joined-dash pieces: '―' pieces (3..5) are empty, '■' / '─' pieces are inked
    assert [int(zt.glyph_alpha(zt.dash_glyph(v, 0)).max() > 0) for v in range(9)] == [1, 1, 1, 0, 0, 0, 1, 1, 1]
    assert ja.char_glyph("─".encode(), 0, 0).source == "fallback"      # lone '─' not in ja tables
    assert all(ja.glyph_alpha(ja.dash_glyph(v, 0)).max() > 0 for v in range(9))


@needs_game
def test_script_layout_matches_tokenizer() -> None:
    """layout(markup=True) draws exactly the glyphs that the independent markup tokenizer
    (tsuki.text.tokenize) yields, on every script line that contains markup."""
    text_mod = pytest.importorskip("tsuki.text")
    st = text_mod.ScriptText()
    assert st.count == 43969
    for lang in ("ja", "en", "zc", "zt"):
        f = Font(lang)
        try:
            n = 0
            for i in range(st.count):
                s = st.get_line(lang, i)
                if not any(c in s for c in "<>|^@["):
                    continue
                n += 1
                exp: list[str] = []
                for tok in text_mod.tokenize(s, lang):
                    if isinstance(tok, text_mod.Text):
                        exp.extend(tok.text)
                    elif isinstance(tok, text_mod.Ruby):
                        exp.extend(tok.base)
                        exp.extend(tok.ruby)
                    elif isinstance(tok, text_mod.Icon):
                        exp.append(tok.raw)
                assert [p.glyph.text for p in f.layout(s, markup=True)] == exp, (lang, i, s)
            assert n > 100, lang
        finally:
            f.close()


@needs_game
def test_charset_sizes(fonts: dict[str, Font]) -> None:
    sizes = {(lang, st): len(fonts[lang].charset(st)) for lang in LANGS for st in (0, 1, 2)}
    assert sizes[("ja", 0)] == 2904 and sizes[("ja", 1)] == 701 and sizes[("ja", 2)] == 1174
    assert sizes[("en", 0)] == 115 and sizes[("en", 1)] == 111 and sizes[("en", 2)] == 82


@needs_game
def test_ink_fully_referenced_en(fonts: dict[str, Font]) -> None:
    """Every glyph pixel on the English pages 1..26 lies in a rectangle the tables reference,
    and the outline half contains the fill (dilated copy at +512)."""
    f = fonts["en"]
    cover: dict[int, np.ndarray] = {}
    for st in range(6):
        for _ch, g in f.charset(st):
            m = cover.setdefault(g.page, np.zeros((512, 1024), dtype=bool))
            m[g.y:g.y + g.h, g.x:g.x + g.w] = True
    for page in range(1, 27):
        a = f.pages.page(page).alpha
        top = a[:OUTLINE_DY] > 0
        if not top.any():
            continue
        assert not (top & ~cover[page]).any(), page
        assert not (top & ~(a[OUTLINE_DY:] > 0)).any(), page


@needs_game
def test_script_text_coverage() -> None:
    """All Japanese and English script strings (script_text.mrg) map to real glyphs."""
    img = image_dir()
    assert img is not None
    from tsuki.mzp import MzpArchive
    chars: dict[int, set[str]] = {}
    with open(img / "script_text.mrg", "rb") as fh, \
            mmap.mmap(fh.fileno(), 0, access=mmap.ACCESS_READ) as mm:
        arc = MzpArchive(mm)
        for k in (0, 1):          # section 1 = Japanese blob, section 3 = English blob
            chars[k] = set(arc.read(2 * k + 1).decode("utf-8")) - set("\r\n<>|@[]")
        del arc
    for k, lang in ((0, "ja"), (1, "en")):
        f = Font(lang)
        try:
            missing = [c for c in chars[k] if f.char_glyph(c.encode(), 0, 0).source == "fallback"]
            assert missing == [], (lang, missing[:20])
        finally:
            f.close()


@needs_game
def test_render_text_images() -> None:
    im = render_text("en", "The familiar scent of straw mats and paper screens", scale=0.5)
    assert im.mode == "RGBA" and im.size[1] == int(np.ceil(CELL * 0.5)) + 16
    a = np.asarray(im)[..., 3]
    assert a.max() == 255 and (a > 0).mean() > 0.05
    im2 = render_text("ja", "月姫 -A piece of blue glass moon-", scale=0.5,
                      background=(0, 0, 0), outline_color=None)
    assert im2.mode == "RGB"
    assert np.asarray(im2).max() >= 250          # PS2 alpha 0x7f -> 254
    # layout: pen advance = (w - 6) * scale
    f = Font("en")
    try:
        placed = f.layout("AA", scale=1.0)
        assert placed[1].x - placed[0].x == placed[0].glyph.w - 6
    finally:
        f.close()
