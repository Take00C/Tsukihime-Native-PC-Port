"""Tests for tsuki.quake (docs/formats/quake.md).

Synthetic tests always run; the expected floats were produced by the engine's own code under
unicorn (scratch/quake/verify_emu.py: 0 mismatches) and are repeated here as constants.
Real-data tests read the game's eboot.bin / sce_module/libc.prx and skip without the game
folder; the engine cross-check also needs unicorn / capstone (Windows x64) and runs the
engine's ScriptStep and per-frame update 0x57980 on unused layers (no pictures needed, ~10 s).
"""
from __future__ import annotations

import math
import struct

import pytest

from tsuki import game_dir
from tsuki.quake import (F32_2M30, F32_2PI, F32_INV3, F32_SHOCK_Y_MASK, F32_Y_CANVAS, F32_Y_PX,
                         MessageQuake, Ps4Rand, Rnd2, ScreenShock, ShakeLayer, ShakeLayers,
                         ShakeSlot, SlotStart, f32, frames, ps4_sinf, rnd_range, shk2_start,
                         shk2_targets, sinf_correctly_rounded, sqk2_start, sqk2_targets,
                         ssw2_start, stop_layers, title_seed)

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None or not (GAME / "eboot.bin").exists(), reason="game folder not found")


def bits(x: float) -> int:
    return struct.unpack("<I", struct.pack("<f", x))[0]


# -- RNG -----------------------------------------------------------------------
def test_ps4_rand_sequence() -> None:
    r = Ps4Rand()
    assert [r.rand() for _ in range(4)] == [408024109, 11635919, 196474438, 117649705]
    r.srand(title_seed(12, 34))
    assert title_seed(12, 34) == 0xC22
    assert [r.rand() for _ in range(3)] == [307531163, 808178116, 741941749]
    r.srand(-1)                                  # srand stores the seed zero-extended
    assert r.state == 0xFFFFFFFF
    assert all(0 <= r.rand() <= 0x3FFFFFFF for _ in range(1000))


def test_rnd2_and_range() -> None:
    r = Ps4Rand()
    g = Rnd2(r)
    v = g()
    s = (((0x6F37 * 0xF131) + 0x11E79431) % (1 << 64)) >> 1
    assert g.state == s and v == 408024109 + s and r.calls == 1
    r = Ps4Rand()
    x = rnd_range(r, -2.5, 2.5)                  # 408024109 % (5 * 2^20 + 1) on a 2^-20 grid
    assert x == -2.5 + (408024109 % (5 * 1048576 + 1)) * 2.0 ** -20
    assert rnd_range(Ps4Rand(), -0.0, 0.0) == 0.0


def test_constants() -> None:
    assert bits(F32_2PI) == 0x40C90FDB and bits(F32_INV3) == 0x3EAAAAAB
    assert bits(F32_Y_CANVAS) == 0x3EAB9D65 and bits(F32_2M30) == 0x30800000
    assert frames(600) == 36 and frames(1000) == 60 and frames(16) == 0 and frames(-50) == -3


# -- argument decoding ---------------------------------------------------------------
def test_sqk2_arguments() -> None:
    l, s = sqk2_start([-1, 1, 4, 0, 600, 0, 0, 0, 0])            # 00_00 statement 215
    assert l == -1 and s.kind == 1 and s.par == (1, 4, 2, 0, 36, 0) and s.dur_ms == 600
    assert sqk2_start([None])[1].par == (15, 15, 2, 0, 0, 0)       # all defaults, dur -1
    assert sqk2_start([0, 1, 1, 50, -1])[1].par == (1, 1, 4, 0, 0, 0)
    assert sqk2_start([0, 1, 1, 16])[1].par[2] == 2 and sqk2_start([0, 1, 1, 17])[1].par[2] == 2
    assert sqk2_start([0, 1, 1, -5])[1].par[2] == 1
    assert sqk2_start([0, 1, 1, 0, 600, 0, 0, 17])[1].par[3:5] == (1, 0)   # a7 > 16: no decay
    assert sqk2_start([40]) is None


def test_shk2_ssw2_stop_arguments() -> None:
    assert shk2_start([3, 15, 15, 100, -1, 0, 0, 0, 10])[1].par == (15, 15, 6, 0, 0, 10)
    assert shk2_start([None])[1].par == (0, 15, 18, 0, 0, 1)
    assert shk2_start([-2]) is None and shk2_start([-1]) is not None
    assert ssw2_start([7, 7, 15, 1000, 2000, 1, -1, 0])[1].par == (7, 15, 60, 120, 1, 0)
    assert ssw2_start([None])[1].par == (7, 15, 60, 120, 1, 0)
    assert ssw2_start([-1]) is None
    assert stop_layers([-1]) == -1 and stop_layers([None]) == 0 and stop_layers([40]) is None


def test_targets() -> None:
    L = [ShakeLayer() for _ in range(40)]
    L[0].used, L[0].z = True, 0
    L[1].used, L[1].z = True, 10
    L[2].used, L[2].z = True, 0
    L[3].used, L[3].z = True, 5
    pic = [-1] * 40
    pic[9] = 123
    assert sqk2_targets(L, pic) == [0, 1, 3, 9]
    assert shk2_targets(L, pic) == [1, 3, 9]


# -- per-frame formulas (engine values) ------------------------------------------
def run(parse, args, n, layer=0):
    sl = ShakeLayers(Ps4Rand())
    l, s = parse(args)
    sl.start(SlotStart(layer, s.kind, s.par, s.dur_ms, s.fade_ms))
    k = 0 if s.kind == 1 else 1
    out = []
    for _ in range(n):
        sl.update(1)
        out.append((sl.layers[layer].slots[k].x, sl.layers[layer].slots[k].y))
    return sl, out


def test_quake_frames() -> None:
    sl, out = run(sqk2_start, [0, 3, 3, 0, 1000, 0, 0, 0], 6)
    assert out[0] == (0.0, 0.0)
    assert out[1] == out[2] == (-0.2319960594177246, -0.9509695172309875)
    assert out[5] == (0.462343692779541, 0.027990251779556274)
    assert sl.rng.calls == 6                                     # 2 per new offset, every p2 = 2 frames


def test_shake_and_swing_frames() -> None:
    _, out = run(shk2_start, [0, 4, 6, 500, 1000], 6)
    assert out[0] == (0.2772156000137329, 0.41813352704048157)
    assert out[5] == (1.2680754661560059, 1.9126805067062378)
    sl, out = run(ssw2_start, [0, 7, 15, 1000, 2000, 1, 2000], 6)
    assert out[0] == (0.0, -0.0) and out[5] == (1.6666667461395264, -0.6915830373764038)
    assert sl.layers[0].slots[1].w == [10, 7, 60, 105, 1, -1, 5] and sl.rng.calls == 6


def test_slot_lifecycle() -> None:
    sl = ShakeLayers(Ps4Rand())
    sl.start(SlotStart(4, 1, (3, 3, 2, 0, 0, 0), 100, 50))         # 6 frames, 3 frames delay
    s = sl.layers[4].slots[0]
    assert (s.fade, s.time) == (3, 6)
    for _ in range(3):
        sl.update(1)
    assert s.fade == 0 and s.elapsed == 0 and sl.rng.calls == 0
    for _ in range(6):
        sl.update(1)
    assert s.kind == 1 and s.elapsed == 6
    sl.update(1)                                                  # elapsed 7 > 6: cleared
    assert s.kind == 0 and (s.x, s.y) == (0.0, 0.0)
    sl.start(SlotStart(4, 3, (7, 15, 60, 120, 1, 0), -1, 0))
    sl.stop(-1, 2)                                                # SHS2 stops the swing too
    assert sl.layers[4].slots[1].kind == 0


def test_commit_merge_and_stch_reset() -> None:
    sl = ShakeLayers(Ps4Rand())
    sl.start(SlotStart(3, 1, (5, 5, 2, 0, 0, 0), -1, 0))
    for _ in range(5):
        sl.update(1)
    running = sl.layers[3].slots[0].copy()
    committed = [ShakeSlot(kind=1, time=-1, par=[5, 5, 2, 0, 0, 0]), ShakeSlot()]
    assert sl.merge_committed(3, committed) == [False, False]     # same kind keeps running
    assert sl.layers[3].slots[0].elapsed == running.elapsed == 5
    assert sl.merge_committed(4, committed) == [True, False]      # new on layer 4: fresh copy
    assert sl.layers[4].slots[0].kind == 1 and sl.layers[4].slots[0].elapsed == 0
    sl.reset_layer(3)
    assert sl.layers[3].slots[0].kind == 0


def test_draw_offset_parent_and_mask() -> None:
    sl = ShakeLayers(Ps4Rand())
    for l in (3, 7, 8):
        sl.layers[l].slots[0] = ShakeSlot(kind=1, time=-1, x=float(l), y=-float(l))
    sl.layers[7].parent = 3
    sl.layers[8].mask = 3
    sl.layers[9].slots[0] = ShakeSlot(kind=1, time=-1, x=9.0, y=9.0)
    sl.layers[9].parent = 2                                       # parent without a quake: own offset
    sl.layers[8].slots[1] = ShakeSlot(kind=2, time=-1, x=0.5, y=0.25)
    assert sl.slot_offset(7) == sl.offset(7) == (3.0, -3.0)
    assert sl.slot_offset(8) == (3.5, -2.75)                      # slot rule: the mask's quake + own slot 1
    assert sl.offset(9) == (9.0, 9.0)
    assert sl.offset(0) == (0.0, 0.0)
    # mask-layer term (0x516DE -> 0x49A60): the mask's slot outputs again, x * 3 * 640 / 1920 and
    # y * (1080/362) * 360 / 1080 (float32), added to the position
    my = f32(f32(f32(-3.0 * F32_Y_PX) * 360.0) / 1080.0)
    assert sl.mask_offset(8) == (3.0, my) and my != -3.0
    assert sl.offset(8) == (f32(3.5 + 3.0), f32(-2.75 + my))      # about 2x the mask's quake
    sl.layers[8].slots = [ShakeSlot(), ShakeSlot()]               # the masked layer itself does not shake
    sl.layers[3].slots[1] = ShakeSlot(kind=2, time=-1, x=0.5, y=0.0)   # a shake on the mask (slot 1)
    assert sl.slot_offset(8) == (0.0, 0.0) and sl.offset(8)[0] == f32(f32(f32(3.5 * 3.0) * 640.0) / 1920.0)
    sl.layers[8].sacl = True                                      # _SACL: no mask term
    assert sl.offset(8) == (0.0, 0.0)
    sl.layers[8].sacl = False
    # shock: the own term always (unless a colour fill); in the mask term only for a cropped
    # offscreen mask, with y scaled by 1080/448 instead of 1080/362
    sl.layers[3].slots = [ShakeSlot(), ShakeSlot()]
    assert sl.offset(8, (1.0, 2.0)) == (1.0, 2.0)
    sl.layers[3].crop, sl.layers[3].offscreen = (10, 20, 300, 400), True
    ox, oy = sl.offset(8, (1.0, 2.0))
    assert abs(ox - 2.0) < 1e-6 and abs(oy - (2.0 + 2.0 * (1080 / 448) / 3)) < 1e-5
    assert sl.mask_offset(8, (0.0, 0.0), False) == (f32(f32(10.0 * 640.0) / 1920.0), f32(f32(20.0 * 360.0) / 1080.0))
    sl.layers[8].no_shock = True                                  # a colour fill skips its own shock term only
    assert abs(sl.offset(8, (1.0, 2.0))[0] - (ox - 1.0)) < 1e-6
    assert bits(F32_Y_PX) == 0x403EF071 and bits(F32_SHOCK_Y_MASK) == 0x401A4925


def test_resets() -> None:
    """_STCH / _SCH2 (0x4A770) clear both slots of a layer; 0x57590 clears every slot and MSQK."""
    sl = ShakeLayers(Ps4Rand())
    sl.start(SlotStart(3, 1, (5, 5, 2, 0, 0, 0), -1, 0))
    sl.start(SlotStart(3, 3, (7, 15, 60, 120, 1, 0), -1, 0))
    sl.start(SlotStart(4, 2, (0, 15, 18, 0, 0, 1), -1, 0))
    sl.update(1)
    sl.reset_layer(3)
    assert [s.kind for s in sl.layers[3].slots] == [0, 0] and sl.layers[4].slots[1].kind == 2
    sl.reset_layer(40)                                            # out of range: nothing
    sl.adv_start()
    assert all(s.kind == 0 for L in sl.layers for s in L.slots)
    mq = MessageQuake()
    mq.start([3000, 5, 5])
    mq.update(Ps4Rand())
    mq.reset()
    assert (mq.dur, mq.x, mq.y) == (0.0, 0.0, 0.0)
    sh = ScreenShock()
    sh.start(0, 3, 100, 10)
    sh.update(1)
    sh.adv_start()
    assert (sh.x, sh.y) == (0.0, 0.0) and sh.remaining == 59 and sh.layer_offset(True) == (0.0, 0.0)


# -- libc sinf -----------------------------------------------------------------------
# (argument bits, libc.prx sinf bits) where f32(math.sin) is 1 ulp off: retail SSW2 periods of
# 144..476 frames and SHOK phases (scratch/quake/sinf_check.log; libc emulated under unicorn)
SINF_CASES = [(0x3F5175C9, 0x3F3ADBF9), (0x3F7885DD, 0x3F53490F), (0x3F8BA058, 0x3F631324),
              (0x407ED595, 0xBF3EA963), (0x40B1D47B, 0xBF29F42A), (0x41A93A27, 0x3F3E3E8E),
              (0x42B7D75A, 0xBF3A3237), (0x451D3087, 0x3F7B7CC5)]


def ff(b: int) -> float:
    return struct.unpack("<f", struct.pack("<I", b))[0]


def test_ps4_sinf() -> None:
    for xb, yb in SINF_CASES:
        assert bits(ps4_sinf(ff(xb))) == yb
        assert abs(bits(sinf_correctly_rounded(ff(xb))) - yb) == 1
    assert ps4_sinf(f32(1e-5)) == f32(1e-5) and bits(ps4_sinf(-0.0)) == 0x80000000
    assert ps4_sinf(math.inf) != ps4_sinf(math.inf) and ps4_sinf(math.nan) != ps4_sinf(math.nan)
    for x in (0.5, 1.0, 2.5, 3.0, 4.5, 6.0, 7.5, 100.0, -1.0, -5.0, 12345.678, 1e6):
        assert abs(ps4_sinf(f32(x)) - math.sin(f32(x))) < 2e-7


def test_msqk_and_shok() -> None:
    mq = MessageQuake()
    mq.start([300, 5, 5])
    r = Ps4Rand()
    mq.update(r)
    assert (mq.amp_x, mq.amp_y) == (2.5, 3.1111111640930176)
    assert (mq.x, mq.y) == (1.622039794921875, 1.763542652130127)
    for _ in range(16):
        mq.update(r)
    assert mq.dur == 300.0 and r.calls == 34                     # 17 active frames
    mq.update(r)                                                 # f32 sum of 18 x 16.666666 = 300.0
    assert mq.dur == 0.0 and (mq.x, mq.y) == (0.0, 0.0) and r.calls == 34
    sh = ScreenShock()
    sh.start(*ScreenShock.parse([0, 3, 100, 10]))
    assert (sh.remaining, sh.period, sh.count) == (60, 6.0, 10.0)
    sh.update(1)
    assert (sh.x, sh.y) == (0.0, 3.4063665866851807)
    for _ in range(59):
        sh.update(1)
    assert sh.remaining == 0 and sh.kind == 0 and (sh.x, sh.y) == (0.0, 0.0)
    sh.start(0, 3, 100, 10, 40)                                   # delayed: 40 ms = 3 updates
    sh.update(1)
    sh.update(1)
    assert sh.remaining == 0 and sh.delay == 8
    sh.update(1)
    assert sh.remaining == 59 and sh.delay == 0


# -- real data ---------------------------------------------------------------------
@needs_game
def test_libc_rand_is_the_modelled_lcg() -> None:
    """The game's own libc.prx: rand = imul by 0x5851F42D4C957F2D, inc, >> 32, & 0x3FFFFFFF."""
    from eboot.selfelf import Eboot
    eb = Eboot.open(GAME / "sce_module" / "libc.prx")
    syms = {s.name.split("#")[0]: s for s in eb.symbols() if s.value}
    rand, srand = syms["cpCOXWMgha0"], syms["VPbJwTCgME0"]
    code = eb.read_va(rand.value, rand.size)
    assert code[:2] == b"\x48\xb8" and struct.unpack_from("<Q", code, 2)[0] == Ps4Rand.MUL
    assert b"\x48\xff\xc0" in code and b"\x48\xc1\xe8\x20" in code and b"\x25\xff\xff\xff\x3f" in code
    state_va = rand.value + 0x12 + struct.unpack_from("<i", code, 0x0E)[0]
    assert struct.unpack("<Q", eb.read_va(state_va, 8))[0] == 1       # initial state
    sc = eb.read_va(srand.value, srand.size)
    assert state_va == srand.value + 7 + struct.unpack_from("<i", sc, 3)[0]


@needs_game
def test_eboot_constants() -> None:
    from eboot.selfelf import Eboot
    eb = Eboot.open(GAME / "eboot.bin")
    f = lambda va: struct.unpack("<I", eb.read_va(va, 4))[0]
    assert f(0x1A72AC) == bits(F32_2PI) and f(0x1A72B0) == bits(F32_INV3)
    assert f(0x1A72B4) == bits(F32_Y_CANVAS) and f(0x1A72A4) == bits(F32_2M30)
    assert struct.unpack("<3q", eb.read_va(0x216848, 24)) == (0, 1, 1)        # kind -> slot
    assert struct.unpack("<Q", eb.read_va(0xDC7BF0, 8))[0] == 0x6F37          # Rnd2 state


def _engine_available() -> bool:
    if GAME is None or not (GAME / "eboot.bin").exists():
        return False
    try:
        from tsuki.misc_layer_state_and_motions_emu import available
    except ImportError:
        return False
    return available()


@pytest.mark.skipif(not _engine_available(), reason="game folder or unicorn harness not available")
def test_engine_cross_check() -> None:
    """ScriptStep + 0x57980 under unicorn vs the model, bitwise, on unused layers 5..9."""
    import faulthandler
    from tsuki.misc_layer_state_and_motions_emu import ARR_A, LAYER_SIZE, Frames, LayerEmu, Stepper
    from tsuki.script import parse_script
    on = faulthandler.is_enabled()
    faulthandler.disable()          # unicorn's mem_map raises (handled) first-chance faults on Windows
    try:
        E = LayerEmu(GAME / "eboot.bin")
    finally:
        if on:
            faulthandler.enable()
    F = Frames(E, Stepper(E))
    state = [0xC22]

    def rnd() -> int:
        state[0] = (state[0] * Ps4Rand.MUL + 1) & ((1 << 64) - 1)
        return (state[0] >> 32) & 0x3FFFFFFF
    E._imp_rand = rnd
    E._imp_sinf = lambda: E._fret(ps4_sinf(E.xmm_f(0)))      # libc.prx sinf (test_libc_sinf_emulated)
    rng = Ps4Rand(0xC22)
    sl, mq, sh = ShakeLayers(rng), MessageQuake(), ScreenShock()
    stmts = [("_SQK2(5,6,12,0,800,0,0,0,0);", sqk2_start), ("_SHK2(6,15,15,100,-1,0,0,0,10);", shk2_start),
             ("_SHK2(7,0,20,100,1200,0,1,200);", shk2_start), ("_SSW2(8,7,15,1000,2000,1,3000,100);", ssw2_start),
             ("_SQK2(9,1,1,50,-1,0,0,40,0);", sqk2_start)]
    for st, parse in stmts:
        assert F.step(st) == "continue"
        c = parse_script(st.encode())[0]
        sl.start(parse([c.value(i) for i in range(9)])[1])
    F.step("_MSQK(600,5,3);")
    mq.start([600, 5, 3])
    F.step("_SHOK(2,3,100,8);")
    sh.start(2, 3, 100, 8, 0)
    dts = [1, 1, 2, 1, 3, 1, 1, 2] * 12
    for i, dt in enumerate(dts):
        E.wr(0xDC7BE0, struct.pack("<H", dt))
        F.update(1)
        mq.update(rng, dt, 1)
        sl.update(dt)
        sh.update(dt)
        for l in range(5, 10):
            a = ARR_A + LAYER_SIZE * l
            for s in (0, 1):
                m = sl.layers[l].slots[s]
                eng = (E.u8(a + 0x178 + s), E.i16(a + 0x19A + 2 * s), E.i16(a + 0x19E + 2 * s),
                       E.rd(a + 0x1A4 + 4 * s, 4), E.rd(a + 0x1AC + 4 * s, 4),
                       list(struct.unpack("<7h", E.rd(a + 0x1B4 + 14 * s, 14))))
                mod = (m.kind, m.elapsed, m.phase, struct.pack("<f", m.x), struct.pack("<f", m.y), m.w)
                assert eng == mod, (i, l, s, eng, mod)
        assert E.rd(0xDF08A4, 8) == struct.pack("<2f", mq.x, mq.y), i
        assert E.rd(0xDF0720, 8) == struct.pack("<2f", sh.x, sh.y), i
        assert state[0] == rng.state, i
    assert rng.calls > 100


@pytest.mark.skipif(not _engine_available(), reason="game folder or unicorn harness not available")
def test_engine_draw_mask_and_sch2() -> None:
    """Engine (Rig with real pictures) vs model: a masked layer (6, mask 3), a cropped mask
    (8, retail pattern of script 1) with two masked layers (9; 10 with _SACL), shakes on the
    masks and on the masked layer, a shock; then _SCH2 on a shaking layer resets it at once."""
    import faulthandler
    from tsuki.misc_layer_state_and_motions_emu import ARR_A, LAYER_SIZE, Rig
    from tsuki.script import parse_script
    on = faulthandler.is_enabled()
    faulthandler.disable()
    try:
        rig = Rig(GAME / "eboot.bin")
    finally:
        if on:
            faulthandler.enable()
    E = rig.E
    state = [0x5A5A]

    def rnd() -> int:
        state[0] = (state[0] * Ps4Rand.MUL + 1) & ((1 << 64) - 1)
        return (state[0] >> 32) & 0x3FFFFFFF
    E._imp_rand = rnd
    E._imp_sinf = lambda: E._fret(ps4_sinf(E.xmm_f(0)))
    for st in ("_STCC(-1);", "_STCH(3,img2183,240,136,1000,255,2178,,CC,,,);",
               "_STCH(6,img1696,200,120,1000,255,2500,,CC,,,3);",
               "_STCH(8,img1688,266,82,1000,255,518,,CC,,,,160,-285,888,1650,90,0);",
               "_STCH(9,img2259,207,287,1000,255,638,,CC,,,8);",
               "_STCH(10,img1688,215,151,1000,255,542,,CC,9,9,8);", "_SACL(10,1);", "_FADS(1,0,0);"):
        rig.F.step(st)
    rig.F.update(1)
    rng = Ps4Rand(state[0])
    sl, sh = ShakeLayers(rng), ScreenShock()
    for st, parse in (("_SQK2(3,6,6,0,-1,0,0,0,0);", sqk2_start), ("_SHK2(3,8,4,500,-1,0);", shk2_start),
                      ("_SQK2(6,2,2,0,-1,0,0,0,0);", sqk2_start), ("_SQK2(8,6,6,0,-1,0,0,0,0);", sqk2_start)):
        rig.F.step(st)
        c = parse_script(st.encode())[0]
        sl.start(parse([c.value(i) for i in range(9)])[1])
    rig.F.step("_SHOK(2,3,100,10);")
    sh.start(2, 3, 100, 10, 0)

    def sync() -> None:
        for l in range(40):
            a, L = ARR_A + LAYER_SIZE * l, sl.layers[l]
            L.used, L.z = E.i32(a + 0x10) == 1, E.i32(0xDE2538 + 4 * l)
            L.parent, L.mask, L.sacl = E.i32(a + 0x128), E.i32(a + 0x120), bool(E.u8(a + 0x1E) & 0x40)
            L.crop, L.offscreen = tuple(struct.unpack("<4h", E.rd(a + 0x132, 8))), E.i32(a + 0x124) == 1

    def slots_equal(layers: tuple[int, ...]) -> None:
        for l in layers:
            for s in (0, 1):
                a, m = ARR_A + LAYER_SIZE * l, sl.layers[l].slots[s]
                assert (E.u8(a + 0x178 + s), E.i16(a + 0x19A + 2 * s), E.rd(a + 0x1A4 + 4 * s, 4),
                        E.rd(a + 0x1AC + 4 * s, 4)) == (m.kind, m.elapsed, struct.pack("<f", m.x),
                                                        struct.pack("<f", m.y)), (l, s)

    def draws() -> dict[int, tuple[float, float]]:
        return {c.layer: (c["x"], c["y"]) for c in rig.F.draw() if 0xE8 <= c["slot"] < 0xE8 + 40}

    for _ in range(4):
        sync()
        rig.F.update(1)
        sl.update(1)
        sh.update(1)
    slots_equal((3, 6, 8))
    on_ = draws()
    sync()                                                        # offscreen flags as the draw left them
    saved = [E.rd(ARR_A + LAYER_SIZE * l + 0x178, 2) for l in range(40)]
    shock = E.rd(0xDF0720, 8)
    assert shock == struct.pack("<2f", sh.x, sh.y)
    for l in range(40):
        E.wr(ARR_A + LAYER_SIZE * l + 0x178, b"\0\0")
    E.wr(0xDF0720, bytes(8))
    off_ = draws()
    for l in range(40):
        E.wr(ARR_A + LAYER_SIZE * l + 0x178, saved[l])
    E.wr(0xDF0720, shock)
    # +0x124 is 1 only while the mask itself is drawn offscreen (0x5AA7F .. 0x5AAE1), never when
    # the masked layers' position term reads it: no shock in the mask term
    assert not sl.layers[8].offscreen and sl.layers[10].sacl and sl.layers[6].mask == 3
    for l in (3, 6, 8, 9, 10):
        ex, ey = sl.offset(l, (sh.x, sh.y))
        dx, dy = on_[l][0] - off_[l][0], on_[l][1] - off_[l][1]
        assert abs(dx - ex) < 1e-3 and abs(dy - ey) < 1e-3, (l, dx, dy, ex, ey)
    for l in (6, 9):                                              # the mask term is really there
        mx, my = sl.mask_offset(l, (sh.x, sh.y))
        bx, by = sl.mask_offset(l, (0.0, 0.0), False)
        assert abs(mx - bx) > 0.05 or abs(my - by) > 0.05
    rig.F.step("_SCH2(3,img1696,,,,,,,CC);")                     # resets layer 3 at the statement
    sl.reset_layer(3)
    slots_equal((3, 6, 8))
    assert E.rd(ARR_A + LAYER_SIZE * 3 + 0x178, 2) == b"\0\0"
    for _ in range(3):
        sync()
        rig.F.update(1)
        sl.update(1)
        sh.update(1)
    slots_equal((3, 6, 8))
    assert state[0] == rng.state


@needs_game
def test_libc_sinf_constants() -> None:
    """ps4_sinf's constants and range thresholds are the ones in the game's libc.prx sinf."""
    from eboot.selfelf import Eboot
    from tsuki import quake as q
    eb = Eboot.open(GAME / "sce_module" / "libc.prx")
    syms = {s.name.split("#")[0]: s for s in eb.symbols() if s.value}
    s = syms["Q4rRL34CEeE"]
    assert (s.value, s.size) == (0x4AF10, 787)
    d = lambda va: struct.unpack("<d", eb.read_va(va, 8))[0]
    assert (d(0xD9290), d(0xD9298), d(0xD92A0), d(0xD92A8)) == (q._S4, q._S2, q._S3, q._S1)
    assert (d(0xD9308), d(0xD9310), d(0xD9318), d(0xD9320)) == (q._S4, q._S3, q._S2, q._S1)
    assert (d(0xD9328), d(0xD9330), d(0xD9338), d(0xD9340), d(0xD9348)) == (q._C3, q._C2, q._C0, 1.0, q._C1)
    assert (d(0xD9360), d(0xD9368), d(0xD92D8), d(0xD92D0)) == (q._PIO2_1, -q._PIO2_1, q._PIO2_2, -q._PIO2_2)
    assert (d(0xD9350), d(0xD9358), d(0xD92C0), d(0xD92C8)) == (q._PIO2_3, -q._PIO2_3, q._PIO2_4, -q._PIO2_4)
    assert (d(0xD92E0), d(0xD92E8), d(0xD92F8), d(0xD9300)) == (q._INVPIO2, q._TOINT, -q._RPIO2_1, -q._RPIO2_1T)
    code = eb.read_va(s.value, s.size)
    for k in (0x3F490FDA, 0x397FFFFF, 0x407B53D1, 0x4016CBE3, 0x40E231D5, 0x40AFEDDF, 0x7F800000, 0x4DC90FDA):
        assert struct.pack("<I", k) in code


def _vex_available() -> bool:
    if GAME is None or not (GAME / "sce_module" / "libc.prx").exists():
        return False
    try:
        import unicorn  # noqa: F401
        from tsuki.misc_layer_state_and_motions_vex import available
    except ImportError:
        return False
    return available()


@pytest.mark.skipif(not _vex_available(), reason="game folder, unicorn or keystone not available")
def test_libc_sinf_emulated() -> None:
    """The game's libc.prx sinf run under unicorn (VEX ops natively) == ps4_sinf, bitwise, on the
    1-ulp cases, edge values and random float32 patterns (both argument-reduction paths)."""
    import faulthandler
    on = faulthandler.is_enabled()
    faulthandler.disable()          # unicorn raises (handled) first-chance faults on Windows
    try:
        _libc_sinf_emulated()
    finally:
        if on:
            faulthandler.enable()


def _libc_sinf_emulated() -> None:
    import random
    import capstone
    from unicorn import UC_ARCH_X86, UC_ERR_INSN_INVALID, UC_MODE_64, Uc, UcError
    from unicorn import x86_const as X
    from eboot.selfelf import Eboot
    from tsuki.misc_layer_state_and_motions_vex import NativeVex
    lc = Eboot.open(GAME / "sce_module" / "libc.prx")
    uc = Uc(UC_ARCH_X86, UC_MODE_64)
    for p in lc.load_segments():
        lo, hi = p.vaddr & ~0xFFF, (p.vaddr + p.memsz + 0xFFF) & ~0xFFF
        uc.mem_map(lo, hi - lo)
        uc.mem_write(p.vaddr, lc.read_va(p.vaddr, p.filesz))
    guard, stack, ret = 0x20000000, 0x30000000, 0x40000000
    for a in (guard, ret):
        uc.mem_map(a, 0x1000)
    uc.mem_map(stack, 0x10000)
    uc.mem_write(guard, struct.pack("<Q", 0x1234567890))
    uc.mem_write(0x4AF22 + 0xD0736, struct.pack("<Q", guard))     # stack-cookie pointer
    uc.mem_write(ret, b"\xf4")
    nv, md, vex = NativeVex(), capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64), {}
    for lo, hi in ((0x4AF10, 0x4B223), (0x4CCD0, 0x4DC5A), (0x4C6A0, 0x4C6C1)):   # sinf, __kernel_rem_pio2, scalbn
        for ins in md.disasm(lc.read_va(lo, hi - lo), lo):
            if ins.bytes[0] in (0xC4, 0xC5):
                vex[ins.address] = bytes(ins.bytes)
    for a in vex:
        uc.mem_write(a, b"\x0f\x0b")

    def libc_sinf(xb: int) -> int:
        uc.reg_write(X.UC_X86_REG_RSP, stack + 0x8000)
        uc.mem_write(stack + 0x8000, struct.pack("<Q", ret))
        uc.reg_write(X.UC_X86_REG_YMM0, xb)
        pc = 0x4AF10
        while True:
            try:
                uc.emu_start(pc, ret, count=200000)
            except UcError as e:
                rip = uc.reg_read(X.UC_X86_REG_RIP)
                if e.errno != UC_ERR_INSN_INVALID or rip not in vex:
                    raise
                pc = nv.execute(uc, rip, vex[rip])
                if nv.last_store is not None:
                    uc.mem_write(nv.last_store[0], nv.last_store[1])
                continue
            break
        assert uc.reg_read(X.UC_X86_REG_RIP) == ret
        return uc.reg_read(X.UC_X86_REG_XMM0) & 0xFFFFFFFF
    rr = random.Random(5)
    xs = [xb for xb, _ in SINF_CASES] + [0x3F490FDA, 0x3F490FDB, 0x4016CBE4, 0x407B53D2, 0x40AFEDE0,
                                         0x40E231D6, 0x4DC90FDA, 0x4DC90FDB, 0x7F7FFFFF, 0x39800000, 0x80000000]
    xs += [b for b in (rr.getrandbits(32) for _ in range(400)) if (b >> 23) & 0xFF != 0xFF]   # ~40 % large path
    xs += [bits(f32(rr.uniform(-3000.0, 3000.0))) for _ in range(300)]
    for xb in xs:
        assert libc_sinf(xb) == bits(ps4_sinf(ff(xb))), hex(xb)
