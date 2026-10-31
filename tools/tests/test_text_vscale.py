"""Tests for tsuki.text_vscale (text-space units -> 1920 x 1080 pixels; FORMATS.md 7 P0 item 1).

Synthetic tests check the mapping model against vertex values captured from the engine's own code
run under unicorn (scratch/text_vscale/hud_emu.py, choice_emu.py).  Real-data tests (skipped
without the game folder) check the eboot constants / instruction operands, re-run the HUD and the
choice draw under unicorn (about 15 s each, skipped without unicorn) and re-measure the
pre-rendered prologue movie frames in samples/textbox.  Nothing is written to disk.
"""
from __future__ import annotations

import contextlib
import faulthandler
import sys
from typing import Iterator

import numpy as np
import pytest

from tsuki import PROJECT_ROOT, game_dir
from tsuki import text_vscale as tv

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None, reason="game folder not found (TSUKI_GAME_DIR)")
F32 = np.float32


def _emu_ok() -> bool:
    try:
        from tsuki.misc_layer_state_and_motions_emu import available
    except ImportError:
        return False
    return bool(available())


@contextlib.contextmanager
def _quiet_faulthandler() -> Iterator[None]:
    """unicorn's mem_map raises a handled SEH exception on Windows; keep faulthandler from printing
    it as "fatal" while the emulator is built (same as test_eboot / test_misc_gallery_extras_flowchart)."""
    was = faulthandler.is_enabled()
    faulthandler.disable()
    try:
        yield
    finally:
        if was:
            faulthandler.enable(file=sys.__stderr__ or sys.stderr)


needs_emu = pytest.mark.skipif(GAME is None or not _emu_ok(), reason="game folder or unicorn/keystone missing")
SAMPLES = PROJECT_ROOT / "samples" / "textbox"
needs_frames = pytest.mark.skipif(GAME is None or not (SAMPLES / "prologue_en_25s.png").is_file(),
                                  reason="game folder or prologue sample frames missing")


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------
def test_hud_scale_constants() -> None:
    assert tv.KX_HUD == 3.0
    assert tv.HUD_TEXT_SCALE == (1.0, float(F32(1.3)))
    assert tv.KY_HUD == pytest.approx(1.3 * 1080 / 448, rel=1e-7)
    assert tv.KY_HUD == pytest.approx(3.1339285, abs=1e-6)
    assert tv.TEXT_SPACE_H == pytest.approx(344.6154, abs=1e-3)
    m = tv.MAIN_TEXT
    assert (m.kx, m.ky, m.ox, m.oy) == (3.0, tv.KY_HUD, 0.0, 0.0)
    assert m.text_space_height == pytest.approx(344.6154, abs=1e-3)
    # line pitch 17.02 + 9 units: 81.55 px (the "78 px" of the old 3.0 model)
    assert m.ky * 26.02 == pytest.approx(81.545, abs=1e-3)
    assert 3.0 * 26.02 == pytest.approx(78.06)


def test_choice_and_backlog_canvas() -> None:
    k = 1080.0 / float(F32(344.7))
    for m in (tv.CHOICE_SCREEN, tv.BACKLOG):
        assert (m.kx, m.ky, m.ox, m.oy) == (3.0, pytest.approx(k, rel=1e-9), 0.0, 0.0)
    assert tv.KY_CHOICE == pytest.approx(3.1331592, abs=1e-6)
    # the choice canvas and the HUD's nested scale describe the same text space within 0.03 %
    assert abs(tv.KY_CHOICE / tv.KY_HUD - 1) < 3e-4


def test_ui_canvases() -> None:
    assert (tv.UI_640x448.kx, tv.UI_640x448.ky) == (3.0, pytest.approx(1080 / 448))
    assert (tv.TITLE_960x540.kx, tv.TITLE_960x540.ky) == (2.0, 2.0)
    c = tv.Canvas(640.0, 362.6666564941406, 0.0, 44.0)      # the HUD's first canvas record (VA 0x53D9E)
    assert tv.mapping_for(c).oy == pytest.approx(-1080 * 44 / 272)


@pytest.mark.parametrize("xy, px", [
    # list-0x1E vertex (text units) -> screen pixel, captured from the emulated HUD 0x53CF0 + executor
    ((30.0, 35.0), (90.0, 109.6875)),                                   # en first glyph, top-left
    ((37.64666748046875, 52.76000213623047), (112.94000244140625, 165.34607)),   # bottom-right
    ((30.0, 61.02000045776367), (90.0, 191.23232)),                     # en line 2 (26.02 units lower)
    ((87.0, 49.0), (261.0, 153.5625)),                                  # ja first glyph
    ((154.21600341796875, 41.95999908447266), (462.64801025390625, 131.49964)),   # ja ruby (dy -7.04)
])
def test_engine_vertex_px_matches_emulation(xy: tuple[float, float], px: tuple[float, float]) -> None:
    got = tv.engine_vertex_px(*xy)
    assert got == (pytest.approx(px[0], abs=2e-3), pytest.approx(px[1], abs=2e-3))
    assert tv.MAIN_TEXT.point(*xy) == (pytest.approx(px[0], abs=2e-3), pytest.approx(px[1], abs=2e-3))


def test_nested_vertex_float32() -> None:
    # 0xDB0E0 output (canvas units) as captured: v * 1.3f in float32
    for y, y13 in ((35.0, 45.5), (52.76000213623047, 68.58799743652344), (61.02000045776367, 79.32599639892578),
                   (49.0, 63.69999694824219)):
        assert F32(F32(y) * F32(1.3)) == F32(y13)


def test_choice_vertex_matches_emulation() -> None:
    c = tv.Canvas(*tv.CHOICE_CANVAS)
    got = tv.engine_vertex_px(30.0, 34.93424606323242, c, None)
    assert got == (90.0, pytest.approx(109.45455, abs=2e-3))
    assert tv.CHOICE_SCREEN.point(30.0, 34.93424606323242)[1] == pytest.approx(109.45455, abs=2e-3)


def test_hud_offsets() -> None:
    assert tv.hud_text_offsets() == (0.0, 0.0)
    # each source truncated toward zero before the sum (cvttps2dq)
    assert tv.hud_text_offsets(shake=(2.7, -1.6), quake=(-0.9, 3.99)) == (2.0, 2.0)
    assert tv.hud_text_offsets(shake=(5.0, 5.0), shake_suppressed=True) == (0.0, 0.0)
    dy = tv.hud_text_offsets(story_page=False)[1]
    assert dy == pytest.approx(302.8148, abs=1e-3)
    assert tv.hud_text_offsets(story_page=False, window_slide=True, window_fade=0.5)[1] == pytest.approx(312.8148, abs=1e-3)
    # the slide only exists outside story pages
    assert tv.hud_text_offsets(story_page=True, window_slide=True, window_fade=0.0) == (0.0, 0.0)
    m = tv.hud_text_mapping(shake=(1.0, 2.0))
    assert m.point(0.0, 0.0) == (3.0, pytest.approx(2.0 * 1080 / 448))   # offsets are not scaled by 1.3
    assert m.point(0.0, 10.0)[1] - m.point(0.0, 0.0)[1] == pytest.approx(10 * tv.KY_HUD)


def test_glyph_quad_geometry() -> None:
    s = float(F32(0.74) / F32(3.0))
    q = tv.MAIN_TEXT.glyph_quad(30.0, 35.0, 0, 0, 31, 72, s, s)
    # (u1 - u0) x (v1 - v0) texels, as 0xE1DC0 emits them (emulated: 22.94 x 55.66 px)
    assert (q.x0, q.y0) == (90.0, pytest.approx(109.6875, abs=1e-3))
    assert q.x1 - q.x0 == pytest.approx(22.94, abs=1e-3)
    assert q.y1 - q.y0 == pytest.approx(165.34607 - 109.6875, abs=1e-3)
    assert (q.u0, q.v0, q.u1, q.v1) == (0.0, 0.0, 31.0, 72.0)
    # glyphs are 1.3 * 1080 / 448 / 3 = 4.46 % taller than wide on the PS4
    assert (q.y1 - q.y0) / (72 * s) / ((q.x1 - q.x0) / (31 * s)) == pytest.approx(1.0446, abs=1e-4)


def test_canvas_transform() -> None:
    tr = tv.MAIN_TEXT.canvas_transform()
    assert (tr.kx, tr.ky, tr.kgy, tr.ox, tr.oy) == (3.0, tv.KY_HUD, tv.KY_HUD, 0.0, 0.0)
    tr = tv.CHOICE_SCREEN.canvas_transform()
    assert tr.kgy == tr.ky == tv.KY_CHOICE


def test_contexts_table() -> None:
    keys = [c.key for c in tv.TEXT_CONTEXTS]
    assert len(keys) == len(set(keys))
    assert tv.context("main").mapping is tv.MAIN_TEXT and tv.context("main").confidence == "V-emu"
    assert tv.context("choice").mapping is tv.CHOICE_SCREEN
    popup = tv.context("popup").mapping
    assert popup is not None and popup.ky == tv.KY_HUD
    with pytest.raises(KeyError):
        tv.context("nope")


def test_movie_model_is_not_the_engine() -> None:
    mm = tv.MOVIE_PROLOGUE
    assert mm.kx == tv.KX_HUD
    assert abs(mm.ky_pos - tv.KY_HUD) > 0.015 and mm.ky_glyph == 3.0
    assert mm.oy_for("en") < mm.oy_for("ja")


def test_half_max_edges() -> None:
    p = np.array([0, 0, 0.2, 1, 1, 1, 0.6, 0, 0], np.float32)
    e = tv.half_max_edges(p, np.arange(9) + 0.5)
    assert e == (pytest.approx(2.5 + 0.3 / 0.8), pytest.approx(6.5 + 0.1 / 0.6))
    assert tv.half_max_edges(np.ones(5, np.float32), np.arange(5) + 0.5) is None


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------
@needs_game
def test_verify_eboot() -> None:
    assert GAME is not None
    res = tv.verify_eboot(GAME / "eboot.bin")
    assert res and all(res.values()), [k for k, v in res.items() if not v]


@needs_emu
def test_emulated_hud_text_mapping() -> None:
    assert GAME is not None
    from tsuki.text import ScriptText
    with _quiet_faulthandler():
        r = tv.emulate_hud_text(GAME / "eboot.bin", ScriptText().get_line("en", 3), 1)
    tail = r["parent"][-2:]
    assert tail[0]["type"] == 12 and tail[0]["canvas"] == (640.0, 448.0)
    assert tail[1]["type"] == 9 and tail[1]["a"] == tv.HUD_TEXT_LIST
    assert tail[1]["scale"] == (1.0, float(F32(1.3))) and tail[1]["offset"] == (0.0, 0.0, 0.0)
    quads = [row for row in r["text_list"] if row["type"] == 1]
    assert len(quads) == len(r["glyph_quads"]) == 140
    for row, q in zip(quads, r["glyph_quads"]):
        for (x, y), (px, py) in zip(row["xy"], q["px"]):
            ex = tv.engine_vertex_px(x, y)
            assert (px, py) == (pytest.approx(ex[0], abs=1e-4), pytest.approx(ex[1], abs=1e-4))
            mx, my = tv.MAIN_TEXT.point(x, y)
            assert abs(px - mx) < 2e-3 and abs(py - my) < 2e-3
    tops = sorted({round(q["px"][0][1], 3) for q in r["glyph_quads"]})
    assert tops == [109.688, 191.232]                     # lines at y 35 and 61.02 units


@needs_emu
def test_emulated_choice_mapping() -> None:
    assert GAME is not None
    from tsuki.text import ScriptText
    with _quiet_faulthandler():
        r = tv.emulate_choice_text(GAME / "eboot.bin", ScriptText().get_line("en", 42570), 1)
    canv = [row["canvas"] for row in r["parent"] if row["type"] == 12]
    assert canv == [(480.0, 272.0), (640.0, 448.0), (640.0, float(F32(344.7))), (480.0, 272.0)]
    assert not [row for row in r["parent"] if row["type"] == 9]
    g = r["glyph_quads"]
    assert g and all(q["proj"][1] == pytest.approx(tv.KY_CHOICE, abs=1e-6) for q in g)
    q = g[0]
    assert q["px"][0][1] == pytest.approx(109.4546, abs=2e-3)
    assert q["px"][3][1] - q["px"][0][1] == pytest.approx(72 * float(F32(0.74) / F32(3.0)) * tv.KY_CHOICE, abs=2e-3)


@needs_frames
def test_prologue_movie_measurement() -> None:
    assert GAME is not None
    s = []
    for lang, png, ids, blank in tv.PROLOGUE_FRAMES[:2]:
        s += tv.movie_glyph_edges(SAMPLES / png, lang, ids, blank)
    en = [x for x in s if x.lang == "en"]
    ja = [x for x in s if x.lang == "ja"]
    assert len(en) > 150 and len(ja) > 40
    for part in (en, ja):
        f = tv.fit_movie_vertical(part)
        assert 3.105 < f["k_pos"] < 3.125                 # movie line positions: about 3.115 px / unit
        assert 2.9 < f["k_glyph"] < 3.08                   # movie glyph shapes: about 3.0, not 3.134
        h = tv.fit_movie_horizontal(part)
        assert abs(h["k"] - 3.0) < 0.01 and abs(h["median_resid_at_3"]) < 1.5
    # the engine mapping is within 1.5 px of the movie for en, about 4 to 5 px for ja (movie offset)
    assert abs(tv.residuals_vs(en, tv.MAIN_TEXT)["top"]) < 1.5
    assert 3.0 < tv.residuals_vs(ja, tv.MAIN_TEXT)["top"] < 6.0
    # the old 3.0 model is 9 to 15 px off
    old = tv.TextMapping("old", 3.0, 3.0)
    assert tv.residuals_vs(en, old)["top"] > 7.0 and tv.residuals_vs(ja, old)["top"] > 12.0
