"""Tests for tsuki.misc_layer_state_and_motions (docs/formats/layers.md).

Synthetic tests always run.  Real-data tests skip without the game folder; the engine
cross-check skips without unicorn / keystone (Windows x64) as well.  Expected engine values
were captured with tsuki.misc_layer_state_and_motions_emu (scratch/misc_layer_state_and_motions/xf).
"""
from __future__ import annotations

import math
import struct

import pytest

from tsuki import game_dir
from tsuki.misc_layer_state_and_motions import (
    Affine, SceneModel, SceneState, MotionTable, commit, ease, layer_matrix, layer_world,
    motion_value, ms_to_frames, script_x, script_y, sprite_matrix, stec_mode, steady_record,
    tween_state, tween_value, wrap_angle, SCENE_LEN, apply_motion, load_motion_loops,
    canvas_to_frame, motion_frames, motion_time_ms, motion_record, motion_clear, motion_copy,
    PictureNames, colour_block, edit_motion, anchor_residual, PX_Y,
)

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None or not (GAME / "eboot.bin").exists(), reason="game folder not found")


# -- synthetic -----------------------------------------------------------------
def test_units() -> None:
    assert script_x(240) == 320 and script_y(136) == 181
    assert script_x(-50) == -66 and script_y(-50) == -66          # C division truncates to zero
    assert wrap_angle(40000) == 4000 and wrap_angle(-18001) == 17999 and wrap_angle(18000) == 18000
    assert ms_to_frames(600) == 36 and ms_to_frames(1000) == 60 and ms_to_frames(50) == 3


def test_stec_modes() -> None:
    assert stec_mode(1, "0xFF8913") == (1, 0xFF8913)
    assert stec_mode(44, "0x123456") == (44, 0xFFFFFF)
    assert stec_mode(82, "0") == (None, 0) and stec_mode(-1, "0") == (None, 0)


def test_easing_endpoints() -> None:
    for eid in list(range(0, 42)) + [50]:
        assert abs(ease(eid, 0.0, 10.0, 5.0, 100.0) - 10.0) < 0.02, eid
        assert abs(ease(eid, 100.0, 10.0, 5.0, 100.0) - 15.0) < 0.02, eid
    assert ease(0, 30.0, 0.0, 1.0, 60.0) == 0.5                    # linear
    assert abs(ease(3, 30.0, 0.0, 1.0, 60.0) - 0.75) < 1e-9        # quad out


def test_tween_counter() -> None:
    assert tween_state(48, 0) == 4096 and tween_state(48, 1) == 4096 - 85
    assert tween_state(48, 49) == 0
    assert tween_value((0, 4096, -85)) == 1.0 and tween_value((0, 0, 0)) == 0.0
    assert abs(tween_value((0, 2048, -85)) - 0.5) < 1e-6


def test_sprite_matrix_rotation_and_pivot() -> None:
    m = sprite_matrix(0x30, 100.0, 50.0, (0.5, 0.25), 16384, (0, 0), (1, 1))
    x, y = m.apply(10, 0)                                           # +90 deg: x axis -> down
    assert abs(x - 100.0) < 1e-4 and abs(y - 55.0) < 1e-4
    m = sprite_matrix(0x50, 100.0, 50.0, (1, 1), 0, (100.0, 50.0), (2.0, 3.0))
    assert m.apply(1, 1) == pytest.approx((102.0, 53.0))
    f = canvas_to_frame()
    assert f.apply(640, 362.6666564941406) == pytest.approx((1920.0, 1080.0))


def test_affine_compose() -> None:
    m = Affine.translate(3, 4) @ Affine.rotate(math.pi / 2) @ Affine.scale(2, 1)
    assert m.apply(1, 0) == pytest.approx((3.0, 6.0))


def test_motion_divisor_quantises_time() -> None:
    """STMA a3 (VA 0xBA8B0): frames - frames % n, not frames / n [engine: probe_fix.py]."""
    assert [motion_frames(f, 3) for f in (0, 1, 2, 3, 10, 17)] == [0, 0, 0, 3, 9, 15]
    assert motion_frames(17, 2) == 16 and motion_frames(17, 1) == 17 and motion_frames(17, 0) == 17
    assert motion_frames(17, -2) == 17
    assert motion_time_ms(10, 3) == pytest.approx(150.0) and motion_time_ms(10, 0) == pytest.approx(166.6666717)


def test_motion_record_semantics() -> None:
    old = struct.pack("<hhiqB7s", 77, 3, 0, 120, 1, bytes(7))
    r = motion_record(old, 12, 2)                       # id > 0: id, div, frames 0, pause 0
    assert struct.unpack_from("<hhxxxxqB", r) == (12, 2, 0, 0)
    assert struct.unpack_from("<h", motion_record(old, 12, -4), 2)[0] == 0
    r = motion_record(old, 0, 9)                         # stop: id 0, pause 0, the rest kept
    assert struct.unpack_from("<hhxxxxqB", r) == (0, 3, 120, 0)
    assert struct.unpack_from("<h", motion_record(old, 101960, 0))[0] == -29112
    c = motion_clear(old)                                # self-source STCH/SCH2: clear, then copy
    assert motion_copy(c, c) == c and struct.unpack_from("<hhxxxxqB", c) == (0, 0, 0, 0)
    assert struct.unpack_from("<hhxxxxqB", motion_copy(c, old)) == (77, 3, 120, 1)


def test_ease_exact_float32() -> None:
    """linear and ids 2..11, 14, 15, 18..20, 26, 27, 34, 35 are bit-exact ports of VA 0xBC460
    (ease_exact_check.py 30000 7: 24,982 / 24,982 samples)."""
    third = struct.unpack("<f", struct.pack("<f", 1 / 3))[0]
    assert ease(3, 30.0, 0.0, 1.0, 60.0) == 0.75 and ease(0, 1.0, 0.0, 1.0, 3.0) == third
    for eid in list(range(0, 12)) + [14, 15, 18, 19, 20, 26, 27, 34, 35]:
        v = ease(eid, 17.0, 0.0, 1.0, 59.0)
        assert v == struct.unpack("<f", struct.pack("<f", v))[0], eid        # a float32 value
    # engine bit patterns of 0xBC460 (scratch/misc_layer_state_and_motions/xf/ease_vectors.py)
    eng = {(3, 0): 0x3EFC8B25, (3, 1): 0x43DAC8A3, (4, 0): 0x3E2A0780, (4, 1): 0x435910AE,
           (5, 1): 0x439F8477, (7, 1): 0x44065505, (9, 1): 0x43A40793, (10, 0): 0x3BE1DBEC,
           (11, 1): 0x4415026B, (26, 1): 0xC10DADED, (27, 0): 0x3F5D7A79, (34, 1): 0xC2B3D708,
           (35, 0): 0x3F634148, (35, 1): 0x4430C51A, (0, 1): 0x43860666}
    args = ((17.0, 0.0, 1.0, 59.0), (1234.5, -20.0, 700.0, 3000.0))
    for (eid, j), want in eng.items():
        got = struct.unpack("<I", struct.pack("<f", ease(eid, *args[j])))[0]
        assert got == want, (eid, j, hex(got))


def test_layer_matrix_anchor_residual() -> None:
    """layers.md 7.2: scale / zoom / rotation act about the integer anchor (+0x76/+0x78); the
    residual offset + anchor (left by motion fields 6/7) is added untransformed, and with the
    pivot flag before the screen-space zoom (engine: scratch/layer_anchor/synth_check.py)."""
    base = {"x": 320.0, "y": 180.0, "anchor_x": 100, "anchor_y": 50, "off_x": -100.4, "off_y": -50.25,
            "zoom_x": -1000, "zoom_y": 1000}
    recs = [steady_record(base)]
    assert anchor_residual(recs[0]) == pytest.approx((-0.4, -0.25), abs=1e-5)
    m = layer_matrix(recs, 0, [])
    ax_u, ay_v = 100 * 3.0, 50 / PX_Y                       # the anchor in texels
    assert m.apply(ax_u, ay_v) == pytest.approx((319.6, 179.75), abs=1e-4)
    assert m.apply(0, 0) == pytest.approx((419.6, 129.75), abs=1e-4)   # old formula: (420.4, 129.75)
    recs = [steady_record(dict(base, rot=9000))]                         # 90 deg clockwise
    m = layer_matrix(recs, 0, [])
    assert m.apply(ax_u, ay_v) == pytest.approx((319.6, 179.75), abs=1e-4)
    assert m.apply(0, 0) == pytest.approx((369.6, 279.75), abs=1e-4)
    recs = [steady_record(dict(base, zoom_x=2000, zoom_y=2000, zoom_pivot=1))]
    # pivot (no catalog picture) = position + anchor = (420, 230); zoom 2 about it, after the residual
    assert layer_matrix(recs, 0, []).apply(ax_u, ay_v) == pytest.approx((219.2, 129.5), abs=1e-4)
    assert layer_matrix(recs, 0, [], (1.0, -2.0)).apply(ax_u, ay_v) == pytest.approx((221.2, 125.5), abs=1e-4)
    steady = steady_record({"anchor_x": 345, "anchor_y": 206, "off_x": -345.0, "off_y": -206.0})
    assert anchor_residual(steady) == (0.0, 0.0)


def test_layer_matrix_unused_parent_rotation() -> None:
    """0x51574..0x515F6: the draw adds the direct parent's rotation even when the parent is not
    in use; its position and zoom are not applied (0x525F6, 0x52969).  Engine: a pictureless
    parent running motion 10436 (scratch/layer_anchor/unused_parent_probe.py)."""
    recs = [steady_record({"x": 100.0, "y": 100.0, "parent": 1}),
            steady_record({"x": 500.0, "y": 300.0, "rot": 9000, "zoom_x": 2000, "zoom_y": 2000}, used=False)]
    m = layer_matrix(recs, 0, [])
    assert m.apply(0, 0) == pytest.approx((100.0, 100.0), abs=1e-4)
    assert m.apply(3, 0) == pytest.approx((100.0, 101.0), abs=1e-4)     # 90 deg clockwise, no zoom


def test_colour_block() -> None:
    b = colour_block(bytes((0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6)) + bytes(6), 0x102030)
    assert b[5:8] == bytes((0x10, 0x20, 0x30)) and b[0xB:0xE] == bytes((1, 2, 3)) and b[8:0xB] == bytes(3)


# -- real data -----------------------------------------------------------------
@pytest.fixture(scope="module")
def cat():
    from tsuki.image_catalog import load_catalog
    return load_catalog(GAME / "eboot.bin")


@pytest.fixture(scope="module")
def names():
    from tsuki.image_catalog import load_picture_names
    return load_picture_names(GAME / "eboot.bin")


@pytest.fixture(scope="module")
def motions():
    return MotionTable(GAME / "eboot.bin")


@needs_game
def test_scene_model_stch_and_commit(cat, names) -> None:
    m = SceneModel(cat, names)
    m.step("_STCH(7,img0597,100,50,800,200,518,,CC,,,,608,-219,1152,1512,120,3,,9000);")
    s = m.s
    assert s.get("pic", 7) == 604 and s.get("x", 7) == 133 and s.get("y", 7) == 66
    assert (s.get("anchor_x", 7), s.get("anchor_y", 7)) == (345, 206)
    assert struct.unpack_from("<hh", s.ext(7), 2) == (-345, -206)
    assert s.get("crop", 7) == (608, -219, 1152, 1512) and s.get("a16", 7) == 120 and s.get("a17", 7) == 3
    assert s.get("angle", 7) == 9000 and s.get("z", 7) == 518 and s.get("alpha", 7) == 200
    m.step("_STCP(7,9250);")
    m.step("_STZ4(7,800,900,1100,1200,0,0,1);")
    m.step("_SBAS(7,3);")
    c = commit(s, 7)
    assert c["z" if "z" in c else "pic"] is not None and s.get("z", 7) == 9250
    assert c["zoom_x"] == 1100 and c["zoom_y"] == 1200 and c["zoom_pivot"] == 1 and c["parent"] == 3
    alpha = c["alpha"]
    assert c["off_x"] == -345.0 and isinstance(alpha, float) and abs(alpha - 200 / 255) < 1e-6
    # SCH2 with a source layer re-points children of the source to the new layer
    m.step("_SBAS(9,7);")
    m.step("_SCH2(8,img1685,,,,,,,CC,,,,,,,,,,7,);")
    assert s.get("parent", 9) == 8 and s.get("x", 8) == 133
    assert len(bytes(s.b)) == SCENE_LEN


@needs_game
def test_parent_chain_and_class_factor(cat) -> None:
    """Engine values (sprite call of layer 7): child of an L sprite at (266,133)."""
    pl = next(i for i, e in enumerate(cat) if e.thumb_name == "ARU_L_82_01_N03.MZP")
    e = cat[pl]
    recs = [steady_record({}, used=False) for _ in range(40)]
    recs[6] = steady_record({"pic": pl, "x": 266.0, "y": 133.0, "anchor_x": e.anchor_x, "anchor_y": e.anchor_y,
                             "off_x": -float(e.anchor_x), "off_y": -float(e.anchor_y), "scale": 700,
                             "zoom_x": 1500, "zoom_y": 1200})
    recs[7] = steady_record({"pic": 604, "x": 133.0, "y": 66.0, "anchor_x": 345, "anchor_y": 206,
                             "off_x": -345.0, "off_y": -206.0, "parent": 6})
    assert layer_world(recs, 7, cat) == pytest.approx((179.75, -90.2), abs=1e-3)
    m = layer_matrix(recs, 7, cat)
    assert m.apply(0, 0) == pytest.approx((-79.0, -213.8), abs=1e-3)
    assert (m.a, m.e) == pytest.approx((0.25, 0.20148149), abs=1e-6)


@needs_game
def test_motion_tables(motions) -> None:
    assert len(motions.motions) == 24192 and len(motions.times) == 71905
    m = motions.get(1)
    assert m.duration == 2800 and motions.flags(1) == 0x8804001
    s, e = m.start, m.end - 1
    vals = [motion_value(motions, 5, s, e, s + (1 if t > 0 else 0), t, 0.0) for t in (0, 700, 1400, 2100, 2800)]
    assert vals == pytest.approx([255.0, 191.25, 127.5, 63.75, 0.0])
    m = motions.get(13963)          # HOLD keys: the value after the last defined key is kept
    k = next(i for i in range(m.start, m.end) if motions.val(i, 0) >= 671)
    assert motion_value(motions, 5, m.start, m.end - 1, k, 671.0, 123.0) == 0.0
    assert motions.get(0) is None and motions.get(24193) is None
    assert len(load_motion_loops()) == 2027


@needs_game
def test_apply_motion_starts_at_key0(motions, cat) -> None:
    rec = steady_record({"pic": 604, "x": 320.0, "y": 181.0, "anchor_x": 345, "anchor_y": 206,
                         "off_x": -345.0, "off_y": -206.0})
    rec["motion"] = struct.pack("<hh", 1, 0) + bytes(20)
    sc = SceneState()
    sc.set("pic", 7, 604)
    sc.set("z", 7, 100)
    apply_motion(motions, rec, 7, 0, cat, sc)
    assert rec["alpha"] == 1.0 and sc.get("z", 7) == 2178
    for _ in range(84):                               # 84 frames = 1400 ms
        apply_motion(motions, rec, 7, 1, cat, sc)
    assert abs(rec["alpha"] * 255 - 127.5) < 1e-3


@needs_game
def test_stch_resets_sttr_and_self_source_clears_motion(cat, names) -> None:
    """Engine (probe_fix.py, rr_scene_diff / rr_src_diff): STCH/SCH2 set sttr = -1 (0x82041);
    a self-source SCH2 clears the motion record (0xBA800 before 0xBA8E0)."""
    m = SceneModel(cat, names)
    m.step("_STCH(9,img0597,240,136,1000,255,2,,CC,,,);")
    m.step("_STTR(9,3,1);")
    assert m.s.get("sttr", 9) == 2 | 1 << 12
    m.step("_SCH2(9,IMG1685,,,,,,,CC,,,);")
    assert m.s.get("sttr", 9) == -1
    m.step("_STMA(9,7);")
    assert struct.unpack_from("<h", m.s.get("motion", 9))[0] == 7
    m.step("_SCH2(10,IMG1685,,,,,,,CC,,,,,,,,,,9,);")       # other source: the motion is copied
    assert struct.unpack_from("<h", m.s.get("motion", 10))[0] == 7
    m.step("_SCH2(9,IMG1685,,,,,,,CC,,,,,,,,,,9,);")        # itself as source: cleared
    assert m.s.get("motion", 9)[:17] == bytes(17)


@needs_game
def test_divisor_motion_alpha(motions, cat) -> None:
    """motion 21128 (alpha 0 -> 255 over 800 ms) with a3 = 3 / 2: engine values (probe_fix.py)."""
    for div, want in ((3, {1: 0.0, 2: 0.0, 3: 15.9375, 10: 47.8125, 17: 79.6875}),
                      (2, {1: 0.0, 2: 10.625, 3: 10.625, 17: 85.0})):
        rec = steady_record({"pic": 604, "alpha": 1.0})
        rec["motion"] = motion_record(bytes(24), 21128, div)
        sc = SceneState()
        sc.set("pic", 23, 604)
        apply_motion(motions, rec, 23, 0, cat, sc)
        for fr in range(1, 18):
            apply_motion(motions, rec, 23, 1, cat, sc)
            if fr in want:
                assert rec["alpha"] * 255 == pytest.approx(want[fr], abs=1e-3), (div, fr)
        assert struct.unpack_from("<q", sc.get("motion", 23), 8)[0] == 17 - 17 % div


@needs_game
def test_variant_motion_switches_scene_picture(motions, cat, names) -> None:
    """Fields 22/23: _STMA(7,2) on IMG0143 switches the scene picture to IMG0143_11_11 (id 4339,
    anchor + variant difference) at t = 0; the drawn record keeps IMG0143 until the next commit
    (engine: probe_fix.py / the reviewer's probe_variant.py)."""
    pn = PictureNames(names)
    m = SceneModel(cat, names)
    m.step("_STCH(7,IMG0143,240,136,1000,255,500,,CC,,,);")
    base = pn.lookup("IMG0143")
    rec = steady_record({k: v for k, v in commit(m.s, 7).items() if k not in ("zoom_x", "zoom_y", "zoom_pivot")})
    for kind, l, p in m.step("_STMA(7,2);"):
        rec["motion"] = motion_record(rec["motion"], p["id"], p["n"])
    apply_motion(motions, rec, 7, 0, cat, m.s, 0, pn)
    assert m.s.get("pic", 7) == pn.lookup("IMG0143_11_11") == 4339
    assert (m.s.get("anchor_x", 7), m.s.get("anchor_y", 7)) == (648, 370)
    assert struct.unpack_from("<hh", m.s.ext(7), 2) == (-648, -370)
    for _ in range(4):
        apply_motion(motions, rec, 7, 1, cat, m.s, 0, pn)
        assert rec["pic"] == base and (rec["anchor_x"], rec["anchor_y"]) == (640, 362)
    no_names = steady_record({"pic": base})
    no_names["motion"] = motion_record(bytes(24), 2, 0)
    sc = SceneState()
    sc.set("pic", 7, base)
    apply_motion(motions, no_names, 7, 0, cat, sc)          # without names: variants skipped
    assert sc.get("pic", 7) == base


# -- engine cross-check ------------------------------------------------------------
def _emu_ok() -> bool:
    if GAME is None or not (GAME / "eboot.bin").exists():
        return False
    from tsuki.misc_layer_state_and_motions_emu import available
    return available()


@pytest.mark.skipif(not _emu_ok(), reason="game folder or unicorn/keystone (Windows x64) missing")
def test_engine_crosscheck(cat, names) -> None:
    """One emulator session (~25 s): statements through the engine's ScriptStep vs
    SceneModel (scene_state bytes), and the engine's drawn quads vs layer_matrix."""
    from tsuki.misc_layer_state_and_motions import RenderRecord
    from tsuki.misc_layer_state_and_motions_emu import ARR_A, Rig
    import faulthandler
    on = faulthandler.is_enabled()
    faulthandler.disable()          # unicorn's memory mapping raises (handled) first-chance faults
    assert GAME is not None
    try:
        R = Rig(GAME / "eboot.bin")
    finally:
        if on:
            faulthandler.enable()
    m = SceneModel(cat, names, SceneState(R.scene()))
    stmts = ["_STCH(5,ARU_L_82_01_N03,200,100,700,255,500,,CC,,,);", "_STZ4(5,1500,1200,1500,1200,0,0,0);",
             "_STRT(5,1431,1431,0,0);", "_STCH(7,img0597,100,50,1000,200,518,,CC,,,);", "_SBAS(7,5);",
             "_STPV(7,300,120,);", "_SOXY(7,6,-4,0,0);", "_STCH(9,SIE_S_07_00_N00,300,200,800,255,600,,CC,,,);",
             "_STZ4(9,-1000,1300,-1000,1300,0,0,1);", "_STRT(9,-2700,-2700,0,0);", "_STPV(9,100,200,);",
             "_SQK2(9,3,3,0,600,0,0,0,0);", "_STEC(9,1,0x203040);", "_STMA(11,0);"]
    R.reset()
    for st in stmts:
        R.F.step(st)
        m.step(st)
        assert m.s.diff(SceneState(R.scene())) == [], st
    calls = R.run(stmts, frames=1)
    recs = [RenderRecord.from_bytes(R.record(ARR_A, k)).f for k in range(40)]
    seen = 0
    for c in calls:
        l = c["slot"] - 0xE8
        if l in (5, 7, 9):
            em = sprite_matrix(c["flags"], c["x"], c["y"], c["scale"], c["angle"], c["pivot"],
                               c["pivot_zoom"], c["screen"])
            if l == 9:
                continue                                  # quaking layer: shake not modelled
            assert em.max_diff(layer_matrix(recs, l, cat), 1000, 1000) < 0.01, l
            seen += 1
    assert seen >= 2
    _motion_crosscheck(R, cat, names)
    _anchor_motion_crosscheck(R, cat)


def _anchor_motion_crosscheck(R, cat) -> None:
    """Quads of motion-animated anchors (layers.md 7.2): retail motion 10421 (s351#10, mirrored,
    STPV) and motion 8776 (s307#228) with rotation and the STZ4 pivot flag, plus a child of the
    second layer; the engine's drawn quad vs layer_matrix at frames 0, 5 and 20
    (scratch/layer_anchor/test_case_probe.py; the old formula was 0.67 / 0.16..0.25 units off)."""
    from tsuki.misc_layer_state_and_motions import RenderRecord
    from tsuki.misc_layer_state_and_motions_emu import ARR_A
    R.reset()
    for st in ("_STCH(4,img0080,24,-115,1000,255,2178,,CC,,,,,,,,,,,);", "_STPV(4,586,684,);",
               "_STZ4(4,-1000,,-1000,,0,0,0);", "_STCH(6,img0782,405,190,1000,255,7734,,CC,,,);",
               "_STPV(6,148,135,);", "_STRT(6,4500,4500,0,0);", "_STZ4(6,-611,-611,-611,-611,0,0,1);",
               "_STCH(8,SIE_S_07_00_N00,60,40,1000,255,7800,,CC,,,);", "_SBAS(8,6);", "_FADS(1,0,0);"):
        R.F.step(st)
    R.F.update(1)
    R.F.step("_STMA(4,10421);")
    R.F.step("_STMA(6,8776);")
    fr = 0
    for p in (0, 5, 20):
        while fr < p:
            R.F.update(1)
            fr += 1
        calls = R.F.draw()
        recs = [RenderRecord.from_bytes(R.record(ARR_A, k)).f for k in range(40)]
        assert anchor_residual(recs[4]) == pytest.approx((-1 / 3, 0.0), abs=1e-4)
        assert anchor_residual(recs[6]) == pytest.approx((-1 / 3, 0.0), abs=1e-4)
        for l in (4, 6, 8):
            c = next(c for c in calls if c["slot"] == 0xE8 + l)
            em = sprite_matrix(c["flags"], c["x"], c["y"], c["scale"], c["angle"], c["pivot"],
                               c["pivot_zoom"], c["screen"])
            pic = recs[l]["pic"]
            assert em.max_diff(layer_matrix(recs, l, cat), cat[pic].width, cat[pic].height) < 0.002, (p, l)
        if p == 0:
            assert layer_matrix(recs, 4, cat).apply(0, 0) == pytest.approx((812.6667, -1065.0), abs=1e-3)


def _motion_crosscheck(R, cat, names) -> None:
    """STTR -> STCH, a variant motion, a divisor motion and a self-source SCH2: scene_state
    byte for byte after every statement and every frame (the model's drawn records start from
    the engine's at the commit and are then driven by motion_record + apply_motion only)."""
    from tsuki.misc_layer_state_and_motions import RenderRecord
    from tsuki.misc_layer_state_and_motions_emu import ARR_A
    assert GAME is not None
    T = MotionTable(GAME / "eboot.bin")
    pn = PictureNames(names)
    R.reset()
    for st in ("_STCH(7,IMG0143,240,136,1000,255,500,,CC,,,);", "_STTR(8,20,5);",
               "_STCH(8,SIE_S_07_00_N00,200,150,1000,255,700,,CC,,,);", "_FADS(1,0,0);"):
        R.F.step(st)
    R.F.update(1)
    m = SceneModel(cat, names, SceneState(R.scene()))
    recs = {l: dict(RenderRecord.from_bytes(R.record(ARR_A, l)).f) for l in (7, 8)}
    assert m.s.get("sttr", 8) == -1
    for st in ("_STMA(7,2);", "_STMA(8,21128,,3);"):
        R.F.step(st)
        for kind, l, p in m.step(st):
            recs[l]["motion"] = motion_record(recs[l]["motion"], p["id"], p["n"])
            apply_motion(T, recs[l], l, 0, cat, m.s, 0, pn)
        assert m.s.diff(SceneState(R.scene())) == [], st
    for fr in range(8):
        R.F.update(1)
        for l in (7, 8):
            apply_motion(T, recs[l], l, 1, cat, m.s, 0, pn)
        assert m.s.diff(SceneState(R.scene())) == [], fr
        e = RenderRecord.from_bytes(R.record(ARR_A, 8)).f
        assert abs((e["alpha"] - recs[8]["alpha"]) * 255) < 1e-3, fr
    # SGMA writes the drawn state into the in-memory keyframe table (0x873FF0, 72-byte records)
    st = "_SGMA(8,21128);"
    R.F.step(st)
    for kind, l, p in m.step(st):
        i = edit_motion(T, recs[l], p["id"], p["key"])
        assert i is not None
        eng = R.E.rd(0x873FF0 + 72 * i, 72)
        f = T.vals[i]
        assert struct.unpack_from("<7h", eng, 8) == f[0:7] and struct.unpack_from("<q", eng, 0x18)[0] == f[7]
        assert struct.unpack_from("<19h", eng, 0x20) == f[8:27]
    st = "_SCH2(8,SIE_S_07_00_N00,,,,,,,CC,,,,,,,,,,8,);"
    R.F.step(st)
    m.step(st)
    assert m.s.diff(SceneState(R.scene())) == [], st
