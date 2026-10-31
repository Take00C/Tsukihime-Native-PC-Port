"""Tests for tsuki.misc_boot_title_choice_flow (boot sequence, title screen, choice menu).

Synthetic tests check the state-machine models and the draw-list geometry; real-data tests
re-read every hard-coded table from eboot.bin, recount the UI-table lookups (695 / 4 / 8),
render a title frame and a choice frame and compare a title still with its movie.  They skip
when the game folder is missing.
"""
from __future__ import annotations

import shutil

import numpy as np
import pytest

from tsuki import game_dir
from tsuki import misc_boot_title_choice_flow as sf

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None, reason="game folder not found (TSUKI_GAME_DIR)")


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------
def test_fade_frame_counts() -> None:
    # process 0: +-0.025 per frame to >= 1.1 / <= -0.1 (float32: 44 steps give 1.0999995, so 45);
    # damaged notice +-0.05
    assert sf.fade_frames(0.0, 0.025, 1.1, up=True) == 45
    assert sf.fade_frames(1.0, -0.025, -0.1, up=False) == 45
    assert sf.fade_frames(0.0, 0.05, 1.1, up=True) == 22
    assert sf.fade_frames(0.0, -0.025, -0.1, up=False) == 4
    # title covers: 1/60 per frame, strict '> 1.0'
    assert sf.title_fade_frames(1 / 60, 0.0, 1.0, up=True) == 61
    assert sf.title_fade_frames(1 / 120, 0.0, 1.0, up=True, strict=False) == 121


def test_title_state_rule() -> None:
    assert sf.title_progression([0x230, 0x17F, 0x232]) == [1, 3, 5, 5]
    assert sf.title_progression([0x17F, 0x232]) == [0, 0, 0, 0]          # needs 560 first
    assert sf.title_progression([0x230, 0x232]) == [1, 1, 1, 1]
    assert sf.advance_title_state(2, [0x230]) == 0 and sf.advance_title_state(4, []) == 0
    assert sf.advance_title_state(7, []) == 0
    kids = [sf.title_background(1, g).record0_child for g in range(6)]
    assert kids == [10, 11, 12, 12, 11, 13]
    assert [sf.title_background(1, g).movie_index for g in range(6)] == [0, 1, 2, 2, 1, 3]
    assert sf.title_background(1, 5).bgm == 72 and sf.title_background(1, 0).bgm == 2
    assert sf.title_background(0, 3) == sf.TitleBackground(3, 9, -1, 2)


def test_title_menu_geometry() -> None:
    assert sf.title_rows(False) == {0: -1, 1: 0, 2: 1, 3: 2, 4: -1, 5: 3, 6: -1}
    assert sf.title_rows(True) == {0: 0, 1: 1, 2: 2, 3: 3, 4: -1, 5: 4, 6: -1}
    d = {x.note.split(" widget")[0]: x for x in sf.title_draw_list(sf.TitleView(lang=1, g2=0, cursor=1))
         if x.kind == "widget"}
    assert set(d) == {"NEW GAME", "LOAD GAME", "CONFIG", "GALLERY"}
    assert tuple(round(v) for v in d["NEW GAME"].pixel_rect()) == (816, 753, 296, 72)
    assert tuple(round(v) for v in d["GALLERY"].pixel_rect()) == (816, 921, 296, 72)
    assert d["NEW GAME"].rect == (336, 24, 632, 96)          # highlighted column
    assert d["LOAD GAME"].rect == (23, 112, 319, 184)
    hl = [x for x in sf.title_draw_list(sf.TitleView(lang=1, cursor=1)) if x.slot == 128][0]
    assert tuple(round(v) for v in hl.pixel_rect()) == (798, 800, 336, 8)
    cont = [x for x in sf.title_draw_list(sf.TitleView(lang=0, cursor=0, continue_available=True))
            if x.slot == 128][0]
    assert round(cont.pixel_rect()[1]) == 767
    # G[10] == 0: no menu, PRESS ANY BUTTON and the glow layer
    first = sf.title_draw_list(sf.TitleView(g10=0))
    assert [x.slot for x in first] == [125, 127, 127, 127, 130, 136, 129]
    assert tuple(round(v) for v in first[0].pixel_rect()) == (-375, -110, 2670, 1266)


def test_logo_bands() -> None:
    en = [x for x in sf.title_draw_list(sf.TitleView(lang=1)) if x.slot == 127]
    assert [tuple(round(v) for v in x.pixel_rect()) for x in en] == [(35, 222, 1, 1), (35, 223, 1850, 329),
                                                                      (35, 552, 1850, 110)]
    ja = [x for x in sf.title_draw_list(sf.TitleView(lang=0)) if x.slot == 127]
    assert [round(x.pixel_rect()[1]) for x in ja] == [72, 357, 642]


def test_choice_geometry() -> None:
    assert sf.choice_first_y(1, f823=3, icon_y=139.0) == pytest.approx(191.04, abs=1e-4)
    assert sf.choice_first_y(0, f823=0, icon_y=None) == pytest.approx(49.1517, abs=1e-4)
    assert sf.choice_first_y(1, f823=2, icon_y=0.0) == pytest.approx(34.9342 + 52.04, abs=1e-3)
    v = sf.ChoiceView(lang=1, count=3, cursor=1, y0=191.04)
    ys = sf.choice_line_ys(v)
    assert ys[1] - ys[0] == pytest.approx(26.02, abs=1e-4)
    draws = sf.choice_draw_list(v)
    tex = [d for d in draws if d.kind == "tex"]
    assert [d.slot for d in tex] == [96, 93, 95]
    under = tex[1].pixel_rect()
    assert under[0] == pytest.approx(41.0, abs=0.01)
    assert under[1] == pytest.approx((ys[1] + 8.936667) * 1080 / 344.7, abs=0.01)
    assert len(sf.choice_glow_offsets()) == 8
    assert sf.choice_width(1) == 574 and sf.choice_width(0) == 474
    # engine order per line: text, glow copies, underline, flare, hit widget (VA 0xA70BC..0xA7086)
    order = [(d.kind, d.slot) for d in draws]
    assert order[:3] == [("tex", 96), ("text", 0x14), ("hit", 0xD5)]
    assert order[3:15] == [("text", 0x15)] + [("text", 0x64 + j) for j in range(8)] + \
        [("tex", 93), ("tex", 95), ("hit", 0xD6)]
    none = sf.choice_draw_list(sf.ChoiceView(cursor=-1))
    assert [d.slot for d in none if d.kind == "tex"] == [96]
    assert [d.slot for d in none if d.kind == "text"] == [0x14, 0x15, 0x16]
    p3 = sf.choice_draw_list(sf.ChoiceView(cursor=0, phase=3))
    assert [d.slot for d in p3 if d.kind == "tex"] == [96, 93, 95, 94, 91, 92]


def test_choice_decided_phase_hides_other_lines() -> None:
    """Review fix: in phase 3 only the cursor line's text block is drawn (VA 0xA70EE..0xA7105);
    the glow copies switch to colour 0xC7FF; the hit widgets stay."""
    d = sf.choice_draw_list(sf.ChoiceView(lang=1, count=3, cursor=1, phase=3, y0=191.04))
    assert {x.source[1] for x in d if x.kind == "text"} == {1}
    assert [x.slot for x in d if x.kind == "text"][0] == 0x15
    assert {x.source[2] for x in d if x.kind == "text" and x.slot >= 0x64} == {0xC7FF}
    assert [x.slot for x in d if x.kind == "hit"] == [0xD5, 0xD6, 0xD7]
    # the text block alpha argument is the constant 1.0 (VA 0xA7111), so the chosen line stays
    # opaque for the whole decided phase
    assert [x.alpha for x in d if x.kind == "text" and x.slot < 0x64] == [1.0]
    assert sf.choice_decision_frames() == {"alpha_zero": 36, "phase_end": 51}
    a, f = sf.choice_decision_state(18)
    assert a == pytest.approx(0.496, abs=1e-5) and f == pytest.approx(0.64, abs=1e-5)
    assert sf.choice_decision_state(40)[0] == 0.0
    (sx, sy), ang = sf.choice_ring_transform(1.0)
    assert (sx, sy, ang) == (pytest.approx(0.1 / 3), pytest.approx(0.1 * 344.7 / 1080), 0.0)
    (sx, sy), ang = sf.choice_ring_transform(0.0)
    assert sx == pytest.approx(1.5 / 3) and ang == pytest.approx(2 * np.pi * 65535 / 65536)


def test_choice_previous_cursor_glow() -> None:
    """Review fix: the previous cursor line (u16 0x17506A4) keeps 8 glow copies, blocks
    0x6C..0x73, alpha (1 - highlight); the new cursor line gets blocks 0x64..0x6B, alpha
    highlight (VA 0xA713F..0xA7223)."""
    v = sf.ChoiceView(lang=1, count=3, cursor=2, prev_cursor=1, highlight=0.3, y0=191.04)
    d = sf.choice_draw_list(v)
    prev = [x for x in d if x.kind == "text" and 0x6C <= x.slot <= 0x73]
    cur = [x for x in d if x.kind == "text" and 0x64 <= x.slot <= 0x6B]
    assert [x.source[1] for x in prev] == [1] * 8 and [x.source[1] for x in cur] == [2] * 8
    g = lambda h: float(np.float32(np.float32(np.float32(h) * 192) / 255)) / 8   # noqa: E731
    assert prev[0].alpha == pytest.approx(g(0.7), rel=1e-5) and cur[0].alpha == pytest.approx(g(0.3), rel=1e-5)
    # cross-fade done: the previous line has no glow left; underline/flare follow the cursor only
    done = sf.choice_draw_list(sf.ChoiceView(count=3, cursor=2, prev_cursor=1, highlight=1.0, y0=191.04))
    assert not [x for x in done if x.kind == "text" and x.slot >= 0x6C]
    # the underline and flare are drawn even when the highlight is 0 (VA 0xA718D -> 0xA759C)
    z = sf.choice_draw_list(sf.ChoiceView(count=3, cursor=0, prev_cursor=1, highlight=0.0, y0=191.04))
    u = [x for x in z if x.slot == 93][0]
    assert u.scale[0] == pytest.approx(0.1 / 3)
    assert not [x for x in z if x.kind == "text" and 0x64 <= x.slot <= 0x6B]
    assert len([x for x in z if x.kind == "text" and x.slot >= 0x6C]) == 8


def test_choice_prefix_line() -> None:
    assert [sf.choice_prefix_line(k) for k in (0, 1, 8, 9)] == [43960, 43961, 43968, 43959]
    from tsuki.text import choice_number_id
    assert all(choice_number_id(k) == sf.choice_prefix_line(k) for k in range(12))


def test_title_returns() -> None:
    assert len(sf.TITLE_RETURN_TARGETS) == 0x22
    assert sf.title_return(9).start_scene == 1800 and sf.title_return(33).start_scene == 190
    assert [sf.title_return(r).start_scene for r in (10, 31)] == [9000, 9021]
    assert sf.title_return(34).mode == 6 and sf.title_return(1).mode == 1
    assert [sf.title_return(r).mode for r in (4, 8, 32, 0, 35)] == [None] * 5
    assert [r for r in range(0, 0x23) if sf.title_return(r).reachable] == [0, 2, 3, 7]


def test_title_logo_alpha() -> None:
    """Review fix: logo alpha 0x176220C schedule (VA 0xB20BF..0xB2151) and its use as the
    factor 0x1762210 for the moon glow, the underline and the menu widgets."""
    up = sf.title_logo_schedule(130, [0.0])
    assert up[0] == pytest.approx(1 / 120, rel=1e-6) and up.index(1.0) == 120   # float32: 121st frame
    assert sf.title_logo_schedule(10, [0.5]) == [0.0] * 10                 # cover >= 0.5: frozen
    down = sf.title_logo_schedule(70, [0.0], start=1.0, paused=[True] * 70)
    assert down.index(0.0) == 60 and down[-1] == 0.0                         # 61st frame (float32)
    la, fading = sf.title_logo_step(0.5, 0.2, movie_paused=False, overlay=0.3)
    assert la == pytest.approx(0.5 - 1 / 60) and not fading
    v = sf.TitleView(lang=1, g2=0, cursor=1, logo_alpha=0.5, logo_fading=True)
    d = sf.title_draw_list(v)
    assert [round(x.alpha, 6) for x in d if x.slot == 127] == [0.5, 0.25, 0.125]
    assert [x.alpha for x in d if x.slot == 130] == [pytest.approx(0.125)]
    assert {round(x.alpha, 6) for x in d if x.kind == "widget"} == {0.125}
    assert [x.alpha for x in d if x.slot == 128] == [pytest.approx(0.125)]
    hidden = sf.title_draw_list(sf.TitleView(lang=1, logo_alpha=0.0, logo_factor_prev=0.75))
    assert not [x for x in hidden if x.slot in (127, 130)]
    assert {round(x.alpha, 6) for x in hidden if x.kind == "widget"} == {0.75}


def test_title_background_source() -> None:
    """Review fix: after the wrap the still (slot 125) is drawn until the seek lands; one seek."""
    st = sf.TitleMovieState()
    seq = [None, 1.0, 8.0, 8.1, 119.9, 0.05, 0.1, 8.04, 9.0, 0.02]
    got = [sf.title_background_source(t, st) for t in seq]
    assert got == [(125, False), (135, False), (135, False), (135, False), (135, False), (125, True),
                   (125, False), (135, False), (135, False), (125, True)]


def test_boot_sequences() -> None:
    keys = [s.key for s in sf.boot_sequence()]
    assert keys[-6:] == ["title_init", "white_in", "tm_ci_movie", "caution2", "title_fade_in", "title_menu"]
    assert "damaged_notice" not in keys and "autosave_notice" in keys
    first = [s.key for s in sf.boot_sequence(bit485=False, g10=False)]
    assert first[-2:] == ["title_skipped", "new_game"]
    after = [s.key for s in sf.boot_sequence(bit485=True, g10=False)]
    assert after[-4:] == ["title_press_any_button", "caution_0", "caution_1", "new_game"]
    dmg = [s.key for s in sf.boot_sequence(damaged=True)]
    assert dmg.index("damaged_notice") < dmg.index("autosave_notice") and dmg[-1] == "new_game"
    auto = [s for s in sf.boot_sequence() if s.key == "autosave_notice"][0]
    assert (auto.frames_min, auto.frames_max) == (4 + 45 + 1 + 45, 4 + 45 + 240 + 45)
    assert all(not s.live for s in sf.BOOT_SCREENS[2:]) and all(s.live for s in sf.BOOT_SCREENS[:2])


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------
@pytest.fixture(scope="module")
def tables() -> sf.Tables:
    assert GAME is not None
    from tsuki.catalog_eboot import Eboot
    with Eboot(GAME / "eboot.bin") as eb:
        return sf.read_tables(eb)


@needs_game
def test_tables_match_constants(tables: sf.Tables) -> None:
    t = tables
    assert t.title_t == sf.TITLE_T
    for tab, rules in t.title_rules.items():
        got = {s: (b, n) for s, (_c, b, n) in enumerate(rules)}
        for s, rule in sf.TITLE_RULES.items():
            if rule is None or rule[0] < 0:
                assert got[s] == (None, None), (hex(tab), s)
            else:
                assert got[s] == rule, (hex(tab), s)
    for lang in range(5):
        k = 1 if lang == 1 else 0
        assert t.logo_rects[lang] == sf.LOGO_RECTS[k]
        assert t.logo_pos[lang] == pytest.approx(sf.LOGO_POS[k])
        assert t.glow_pos[lang] == pytest.approx(sf.GLOW_POS[k])
        assert [list(r) for r in t.hl_pos[lang]] == [pytest.approx(list(r)) for r in sf.HL_POS]
        assert t.bg_redraw_y[lang] == sf.BG_REDRAW_Y[k]
    assert [v for p in t.widget_pos for v in p] == pytest.approx([v for p in sf.WIDGET_POS for v in p])
    assert t.hl_index == sf.HL_INDEX
    rows = {it.widget: it.sheet_row for it in sf.TITLE_ITEMS}
    for wid, frames in t.widgets:
        assert all(s == 126 and f == 0x10 for s, f, _r in frames)
        assert [r for _s, _f, r in frames] == [sf.title_widget_rect(rows[wid], c)
                                              for c in sf.TITLE_WIDGET_FRAME_COLUMNS]
    assert t.floats[0x2186E4] == pytest.approx(sf.TITLE_LOOP_TIME)
    assert t.floats[0x2168CC] == pytest.approx(sf.SEL_CANVAS[1])
    assert t.floats[0x2168D4] == pytest.approx(sf.SEL_Y_GAP) and t.floats[0x216920] == pytest.approx(sf.SEL_PITCH)
    assert (t.floats[0x2168E8], t.floats[0x216918]) == pytest.approx(sf.SEL_UNDERLINE)
    assert (t.floats[0x2168EC], t.floats[0x21691C]) == pytest.approx(sf.SEL_FLARE)
    assert t.floats[0x2D5FFC] == pytest.approx(1.1) and t.floats[0x2D5FF8] == pytest.approx(0.025)
    # review fixes: logo steps, choice fades, ring constants, title return table, _SELR prefix
    assert (t.floats[0x2186F0], t.floats[0x2186F4]) == (sf.LOGO_STEP_IN, sf.LOGO_STEP_OUT)
    assert (t.floats[0x216878], t.floats[0x21687C]) == pytest.approx((-0.028, -0.02))
    assert t.floats[0x216864] == pytest.approx(0.1) and t.floats[0x216924] == pytest.approx(1.4)
    assert t.sel_ring_k == sf.SEL_RING_K
    assert t.title_return_targets == sf.TITLE_RETURN_TARGETS
    assert t.sel_prefix == (sf.SEL_PREFIX_LINE, sf.SEL_PREFIX_OVERFLOW, 9)


@needs_game
def test_choice_prefix_text_and_census() -> None:
    """Line 43959 is '0. ' (ja '０．'): the 10th line of a block shows '0.'; such blocks exist
    only in the debug JUMP / TEST scripts.  Census of the choice commands over all scripts."""
    assert GAME is not None
    from tsuki.text import ScriptText
    st = ScriptText(GAME / "image" / "script_text.mrg")
    assert [st.get_line("en", sf.choice_prefix_line(k)) for k in (0, 8, 9)] == ["1. ", "9. ", "0. "]
    assert [st.get_line("ja", sf.choice_prefix_line(k)) for k in (0, 9)] == ["１．", "０．"]
    c = sf.choice_census(GAME)
    assert c["counts"] == {"SELR": 824, "SELR_2args": 818, "SELR_locked": 6, "SELD": 170, "SELA": 26, "SELS": 1}
    assert c["block_sizes"] == {1: 1, 2: 64, 3: 36, 4: 5, 5: 4, 6: 7, 7: 4, 8: 4, 9: 5, 10: 40}
    ten: dict[str, int] = c["ten_line_blocks"]  # type: ignore[assignment]
    assert sum(ten.values()) == 40 and all(n.startswith(("JUMP", "TEST_SCRIPT")) for n in ten)


@needs_game
def test_dead_states(tables: sf.Tables) -> None:
    t = tables
    noop = t.boot_cases[1]
    assert noop == 0xE8DB9
    live_cases = {s for s, a in t.boot_cases.items() if a != noop}
    assert live_cases == set(sf.BOOT_LIVE_STATES) | set(sf.BOOT_DEAD_STATES)
    # the dead clusters are entered only through 0x06 and 0x10, which nothing stores
    # (the two register stores at 0xE8D4F write 0x16 / 0x34)
    assert 0x06 not in t.boot_states_written and 0x10 not in t.boot_states_written
    assert {0x0F, 0x1C, 0x1D, 0x1E, 0x30, 0x31, 0x32, 0x33} <= t.boot_states_written
    # title: cases with no store anywhere (idle timeout 0x16, gallery returns 0x25/0x26/0x28, ...)
    for s in (0x16, 0x19, 0x21, 0x23, 0x25, 0x26, 0x28):
        assert s not in t.title_states_written
    assert 0x27 in t.title_states_written and 0x32 in t.title_states_written


@needs_game
def test_selecttxt_never_drawn() -> None:
    """No widget frame record names texture slot 97/98 (SELECTTXT) while 21 name slot 126."""
    assert GAME is not None
    from tsuki.catalog_eboot import Eboot
    with Eboot(GAME / "eboot.bin") as eb:
        assert eb.mm is not None
        data = bytes(eb.mm[0x365FA0:0x365FA0 + 0x7D66E0])
    for slot, n in ((97, 0), (98, 0), (126, 21)):
        assert data.count(slot.to_bytes(4, "little") + (0x10).to_bytes(4, "little")) == n


@needs_game
def test_ui_lookup_split() -> None:
    assert GAME is not None
    s = sf.ui_lookup_split(GAME)
    assert (s["direct"], s["clamped"], s["rewritten"]) == (695, 4, 8)
    assert s["rewritten_title"] == 4 and s["rewritten_selecttxt"] == 4


@needs_game
def test_first_choice_block() -> None:
    assert GAME is not None
    en = sf.find_choice_scene("01_00_ARC01_1", 0, 1, GAME)
    ja = sf.find_choice_scene("01_00_ARC01_1", 0, 0, GAME)
    assert en.choice_ids == [0, 1, 2] and en.n == 1 and en.f823 == 3 and not any(en.locked)
    assert en.choice_texts[0] == "1. ...can't think of a single thing to say."
    assert ja.choice_texts[1].startswith("２．")
    assert len(en.page_messages) == 3 and en.page_messages[2].startswith("  I...")


@needs_game
def test_render_title_and_choice() -> None:
    assert GAME is not None
    cache = sf.AssetCache(GAME / "image")
    try:
        img = sf.render_draws(sf.title_draw_list(sf.TitleView(lang=1, g2=0, cursor=1)), cache)
        still = cache.image(("allpac3", "TITLE_PARTS_EN.MRG", 10))[..., :3]
        assert np.abs(img[5:60, 5:60] - still[5:60, 5:60]).max() < 1e-6     # untouched corner = still
        under = img[801:806, 900:1030]                                         # NEW GAME underline
        assert under[..., 1].mean() > img[812:818, 900:1030, 1].mean() - 0.5
        assert (img[1015:1060, 880:1050].max(axis=(0, 1)) > 0.5).all()        # (C)TYPE-MOON
        sc = sf.find_choice_scene("01_00_ARC01_1", 0, 1, GAME)
        frame, info = sf.render_choice(GAME, sc, sf.ChoiceView(lang=1, cursor=1), cache=cache)
        assert info["icon_y"] == 139.0 and info["y0"] == pytest.approx(191.04, abs=1e-4)
        line_ys: list[float] = info["line_ys"]  # type: ignore[assignment]
        ys = [round(y * 1080 / 344.7) for y in line_ys]                        # 599, 680, 762
        for y in ys:
            assert frame[y + 5:y + 40, 90:600].max() > 0.9                     # bright choice text
        assert frame[ys[1] + 10:ys[1] + 40, 200:500, 0].mean() < frame[ys[1] + 10:ys[1] + 40, 200:500, 2].mean()
        # decided (phase 3, 18 frames after the decision): only the chosen line remains
        a, f = sf.choice_decision_state(18)
        frame3, _ = sf.render_choice(GAME, sc, sf.ChoiceView(lang=1, cursor=1, phase=3, alpha=a, flash=f),
                                     cache=cache)
        assert frame3[ys[1] + 5:ys[1] + 40, 300:1100, 0].max() > 0.9                # chosen line, white
        for y in (ys[0], ys[2]):
            assert frame3[y + 5:y + 40, 300:1100, 0].max() < 0.3                   # other lines gone
    finally:
        cache.close()


@needs_game
@pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="ffmpeg not on PATH")
def test_still_is_loop_frame() -> None:
    assert GAME is not None
    cache = sf.AssetCache(GAME / "image")
    try:
        still = cache.image(("allpac3", "TITLE_PARTS_EN.MRG", 10))[..., :3]
    finally:
        cache.close()
    at_loop = np.abs(sf.movie_frame(GAME, "titlebg1.mp4", sf.TITLE_LOOP_TIME) - still).mean() * 255
    at_zero = np.abs(sf.movie_frame(GAME, "titlebg1.mp4", 0.0) - still).mean() * 255
    assert at_loop < 2.0 < at_zero
    # review fix: frame-level scan with the motion-sensitive high-pass metric.  Its minimum lies
    # within 2 frames of the seek target (8.0333 s = frame 241), and frames 4..5 away are
    # clearly worse, so the still is the loop-point frame to +-2 frames.
    curve = sf.still_match_curve(GAME, 1, 234, 13)            # frames 234..246
    hp = {fr: h for fr, _m, h in curve}
    best = min(hp, key=hp.__getitem__)
    assert 239 <= best <= 243
    assert hp[234] > 1.1 * hp[best] and hp[246] > 1.1 * hp[best]
    far = sf.still_match_curve(GAME, 1, 0, 1)[0][2]
    assert far > 4.0 * hp[best]
