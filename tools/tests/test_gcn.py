"""Tests for tsuki.gcn (GCN / Sea Islands decoder and SIMT interpreter).

Synthetic tests use hand-assembled instruction words (encodings from the AMD Sea Islands ISA
guide); the real-data tests decode all 67 PSSL blobs of eboot.bin and skip without the game.
"""
from __future__ import annotations

import collections
import struct

import numpy as np
import pytest

from eboot import eboot_path
from tsuki import gcn

EBOOT = eboot_path()
needs_game = pytest.mark.skipif(EBOOT is None, reason="game folder not found (TSUKI_GAME_DIR)")


def _w(*words: int) -> bytes:
    return struct.pack(f"<{len(words)}I", *words)


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------
def test_decode_words_from_blob0() -> None:
    # first instructions of PS #0 (VA 0xDCA760): s_mov_b32 vcc_hi, 0x1a ; s_mov_b32 m0, s16
    ins = gcn.decode(_w(0xBEEB03FF, 0x1A, 0xBEFC0310, 0xBF810000))
    assert [i.name for i in ins] == ["s_mov_b32", "s_mov_b32", "s_endpgm"]
    assert ins[0].lit == 0x1A and ins[0].size == 8 and ins[1].f["ssrc0"] == 16
    assert ins[1].text() == "s_mov_b32 m0, s16"


def test_vop2_vop3_semantics() -> None:
    m = gcn.Machine(4)
    a = np.array([0.25, -1.5, 2.0, 0.5], np.float32)
    b = np.array([0.5, 0.5, -3.0, 4.0], np.float32)
    m.v[1], m.v[2] = gcn.Machine.U(a), gcn.Machine.U(b)
    # v_mul_f32 v3, v1, v2   (VOP2 op 8: src0 = 257 (v1), vsrc1 = 2, vdst = 3)
    # v_mad_f32 v4, -v1, |v2|, 1.0 clamp  (VOP3 op 321)
    vop2 = (8 << 25) | (3 << 17) | (2 << 9) | 257
    vop3_0 = (0x34 << 26) | (321 << 17) | (1 << 11) | (2 << 8) | 4        # clamp, abs src1
    vop3_1 = 257 | (258 << 9) | (242 << 18) | (1 << 29)                     # neg src0, src2 = 1.0
    ins = gcn.decode(_w(vop2, vop3_0, vop3_1, 0xBF810000))
    assert [i.name for i in ins] == ["v_mul_f32", "v_mad_f32", "s_endpgm"]
    m.run(ins)
    assert np.array_equal(gcn.Machine.F(m.v[3]), (a * b).astype(np.float32))
    exp = np.clip(-a * np.abs(b) + 1, 0, 1).astype(np.float32)
    assert np.allclose(gcn.Machine.F(m.v[4]), exp)


def test_cndmask_and_compare() -> None:
    m = gcn.Machine(3)
    m.v[0] = gcn.Machine.U(np.array([0.2, 0.7, 0.5], np.float32))
    m.v[1] = gcn.Machine.U(np.array([10, 20, 30], np.float32))
    m.v[2] = gcn.Machine.U(np.array([1, 2, 3], np.float32))
    # v_cmp_gt_f32 vcc, 0.5, v0  (VOPC op 4, src0 = 240 (0.5)) -> 0.5 > v0
    vopc = (0x3E << 25) | (4 << 17) | (0 << 9) | 240
    # v_cndmask_b32 v3, v1, v2, vcc  -> vcc ? v2 : v1
    cnd = (0 << 25) | (3 << 17) | (2 << 9) | 257
    m.run(gcn.decode(_w(vopc, cnd, 0xBF810000)))
    assert gcn.Machine.F(m.v[3]).tolist() == [1.0, 20.0, 30.0]


def test_f16_rtz_export() -> None:
    x = np.array([1 / 3, 0.999, 65519.0, -0.1], np.float32)
    h = gcn.f16_to_f32(gcn._f16_rtz(x))
    assert np.all(np.abs(h) <= np.abs(x)) and np.allclose(h, x, rtol=1e-3)


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------
@needs_game
def test_all_67_blobs_decode() -> None:
    from eboot.selfelf import Eboot
    from eboot.shaders import find_blobs
    eb = Eboot.open(EBOOT)
    try:
        blobs = find_blobs(eb)
        assert len(blobs) == 67
        ops: collections.Counter = collections.Counter()
        by_stage: dict = {"PS": collections.Counter(), "VS": collections.Counter()}
        total = 0
        for b in blobs:
            h = gcn.parse_shader(eb.read_va(b.va, b.size))
            assert h.stage == b.stage
            ins = gcn.decode(h.code)
            assert ins[-1].name == "s_endpgm"
            assert sum(i.size for i in ins) <= h.code_size
            for i in ins:
                assert not any(i.name.startswith(p) for p in ("vop", "sop", "smrd_", "mimg_")), i.name
                ops[i.name] += 1
                by_stage[b.stage][i.name] += 1
            total += len(ins)
        assert total == 1910
        assert len(ops) == 36                     # distinct mnemonics (42 encoding/opcode pairs)
        assert ops["image_sample"] == 58 and ops["exp"] == 154
        # per stage: the 35 PS hold all 58 samples and 35 exports (one MRT0 each), the 32 VS the
        # other 119 exports (position + parameters); 1,403 PS + 507 VS instructions
        assert by_stage["PS"]["image_sample"] == 58 and by_stage["PS"]["exp"] == 35
        assert by_stage["VS"]["exp"] == 119 and by_stage["VS"]["image_sample"] == 0
        assert sum(by_stage["PS"].values()) == 1403 and sum(by_stage["VS"].values()) == 507
    finally:
        eb.close()


@needs_game
def test_ps0_listing_matches_hand_reading() -> None:
    """PS #0 (transition 2DTex): the 9-instruction core read by hand in compositing.md 3.2."""
    from eboot.selfelf import Eboot
    from eboot.shaders import find_blobs
    eb = Eboot.open(EBOOT)
    try:
        b = find_blobs(eb)[0]
        text = gcn.disasm(gcn.decode(gcn.parse_shader(eb.read_va(b.va, b.size)).code))
    finally:
        eb.close()
    for line in ("v_rcp_f32 v6, v2", "v_mul_f32 v5, v6, v1 clamp",
                 "v_madak_f32 v1, -2.0, v5, 0x40400000 (3)", "v_med3_f32 v1, 1.0, |v1|, 0",
                 "image_sample v[5..], v[2..], s[0:7], s[8:11] dmask:0x1"):
        assert line in text
