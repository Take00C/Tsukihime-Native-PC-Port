"""Tests for tsuki.misc_compositing_transitions_shaders (STEC / SNX2 / SNX3 CPU recolour, engine
tables, rule-mask source) - docs/formats/compositing.md.

The STEC expectations are the engine's own function 0xD36F0 run under unicorn
(scratch/misc_compositing_transitions_shaders/stec_emu.py: 756 cases, 0 mismatches); 336 of
them are frozen as SHA-1 digests in samples/misc_compositing_transitions_shaders/stec_golden.json
so this test needs neither unicorn nor much time.  Real-data tests skip without the game.
"""
from __future__ import annotations

import hashlib
import json
import struct

import numpy as np
import pytest

from eboot import eboot_path
from tsuki import PROJECT_ROOT, game_dir
from tsuki import misc_compositing_transitions_shaders as M

EBOOT = eboot_path()
needs_game = pytest.mark.skipif(EBOOT is None, reason="game folder not found (TSUKI_GAME_DIR)")
GOLDEN = PROJECT_ROOT / "samples" / "misc_compositing_transitions_shaders" / "stec_golden.json"
TEXT_FILE = 0x2280


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------
def test_brightness_and_contrast_luts() -> None:
    l1, l2 = M.stec_luts(0, 0)
    assert np.array_equal(l1, np.arange(256)) and np.array_equal(l2, np.arange(256))
    l1, _ = M.stec_luts(-50, 0)
    assert l1[0] == 0 and l1[50] == 0 and l1[255] == 205
    _, l2 = M.stec_luts(0, 40)                         # stretch: 40 -> 0, 215 -> 255
    assert l2[39] == 0 and l2[40] == 0 and l2[214] == 253 and l2[215] == 255
    _, l2 = M.stec_luts(0, -40)                        # compress into [40, 215]
    assert l2[0] == 40 and l2[255] == 215


def test_rule_name() -> None:
    assert M.rule_name(1) == "RULE_01.MZP" and M.rule_name(49) == "RULE_49.MZP"
    with pytest.raises(ValueError):
        M.rule_name(0)


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------
@needs_game
def test_stec_matches_engine_golden() -> None:
    tabs = M.read_stec_tables(EBOOT)
    px = M.stec_test_pattern()
    cases = json.loads(GOLDEN.read_text())["cases"]
    assert len(cases) == 336
    for c in cases:
        out = M.stec_apply_bgra(px, c["id"], c["bright"], c["contrast"], c["colour"], tabs)
        assert hashlib.sha1(out.tobytes()).hexdigest() == c["sha1"], c
        assert int((out != px).any(axis=1).sum()) == c["changed"]


@needs_game
def test_stec_tables_shape() -> None:
    t = M.read_stec_tables(EBOOT)
    assert t.flags.tolist().count(0x100) == 69 and t.flags.tolist().count(0x200) == 11
    assert t.flags[0] == 0x400                                        # id 1: solid colour
    assert (t.k0, t.k1) == (382.5, 765.0)
    assert np.all(t.w0 + 548 + t.w2 == 765)
    assert np.array_equal(t.luts[3, 0], 255 - np.arange(256))       # id 4: negative
    assert np.array_equal(t.luts[1, 2], np.arange(256))             # id 2: grey
    # ids 77..81 repeat 2, 3, 5, 6, 7 with the BT.709 weights in the right place (B 56, R 161)
    for a, b in ((77, 2), (78, 3), (79, 5), (80, 6), (81, 7)):
        assert np.array_equal(t.luts[a - 1], t.luts[b - 1])
        assert (t.w0[a - 1], t.w2[a - 1]) == (56, 161) and (t.w0[b - 1], t.w2[b - 1]) == (161, 56)


@needs_game
def test_mask_name_table_is_dead_and_rules_come_from_mask_top() -> None:
    data = EBOOT.read_bytes()
    t = M.read_engine_tables(EBOOT)
    assert len(t.mask_names) == 33 and t.mask_names[0] == "DEFAULT" and t.mask_names[-1] == "MASK099"
    # no call/jmp/lea/rip-relative displacement anywhere in .text reaches 0xA3530 (the only
    # reader of the table, which itself is referenced once, from 0xA35A3)
    text = np.frombuffer(data, np.uint8, count=0x329DC8, offset=TEXT_FILE)
    hits = {0xA3530: 0, 0x869B30: 0}
    for k in range(4):
        n = (len(text) - k - 4) // 4
        d = np.frombuffer(text[k:k + 4 * n].tobytes(), "<i4").astype(np.int64)
        tgt = np.arange(n, dtype=np.int64) * 4 + k + 4 + d
        for va in hits:
            hits[va] += int((tgt == va).sum())
    assert hits == {0xA3530: 0, 0x869B30: 1}
    # 0x6B460: lea rdi, "mask_top.mzp" ; rule n -> NAM index find(mask_top) + 1 + (n - 1)
    off = 0x20A11A + TEXT_FILE
    assert data[off:off + 13] == b"mask_top.mzp\0"
    from tsuki.nam import load_nam
    nam = load_nam(game_dir() / "image" / "allpac3.nam")
    idx = {r.raw.split(b"\0")[0].decode(): r.index for r in nam.records}
    top = idx["MASK_TOP.MZP"]
    for n in range(1, 50):
        assert idx[M.rule_name(n)] == top + n


@needs_game
def test_transition_constants_in_code() -> None:
    data = EBOOT.read_bytes()

    def f32(va: int) -> float:
        return struct.unpack_from("<f", data, va + TEXT_FILE)[0]

    assert np.float32(f32(0x209ADC)) == np.float32(16.666)       # ms per frame (0x6BAD1)
    assert f32(0x2098CC) == 256.0                                 # progress scale (0x67978)
    assert (f32(0x2D4010), f32(0x2D4014), f32(0x2D4018), f32(0x2D401C)) == (0.0078125, -0.00390625, -1.0, 2.0)
    # _STBR backend 0x830D0: mode stored to scene_state + 0x54 + 2*layer (mov [r15+rbx*2+0x54], ax),
    # flag 0x80 ORed into +0xA4 + layer (or byte [rbx+r15+0xa4], 0x80); no other store
    code = data[0x830D0 + TEXT_FILE:0x831F0 + TEXT_FILE]
    assert data[0x831A4 + TEXT_FILE:0x831AA + TEXT_FILE] == bytes.fromhex("664189445f54")
    assert data[0x831C9 + TEXT_FILE:0x831D2 + TEXT_FILE] == bytes.fromhex("42808c3ba400000080")
    assert code.count(bytes.fromhex("a4000000")) == 1


@needs_game
def test_transition_capture_paths() -> None:
    """Which captured frame each _FADS draw reads (compositing.md 7.2 / 7.4; emulated by
    scratch/misc_compositing_transitions_shaders/dlprobe_capture.py)."""
    from tsuki import compose as cp
    data = EBOOT.read_bytes()

    def code(va: int, n: int) -> bytes:
        return data[va + TEXT_FILE:va + TEXT_FILE + n]

    t = M.read_engine_tables(EBOOT)
    assert set(t.capture15_types) == set(cp.CAPTURE15_TYPES)
    assert t.transition_draw[1] == t.transition_draw[49] == 0x6837D       # nothing drawn
    assert t.transition_draw[2] == 0x67958                                # rule wipe
    default = {k for k, v in t.transition_draw.items() if v == 0x67A30}
    assert {3, 12, 46, 52, 53} <= default and 0 not in t.transition_draw  # type 0: ja 0x67A30
    # 0x6B910: 0xDCCC0(1) (capture into nexl_Capture[0]) for every type, before the table
    assert code(0x6BA6B, 10) == bytes.fromhex("bf01000000e84b120700")
    # 0x67820 default path: mov edi, 1 (image record 1) -> 0x68373: mov esi, 0x10; call 0xDD6D0
    assert code(0x6813D, 5) == bytes.fromhex("bf01000000")
    assert code(0x6836E, 10) == bytes.fromhex("bf10000000be10000000")  # rule path: record 0x10
    # 0xC4C50: image records 1..16 = 1920 x 1080 (mov esi, 0x901; mov edi, n; mov edx, 0x780;
    # mov ecx, 0x438; call 0xD7A70)
    for n in range(1, 16):
        a = 0xC4C54 + 25 * (n - 1)
        assert code(a, 20) == bytes.fromhex("be01090000") + bytes([0xBF, n, 0, 0, 0]) +             bytes.fromhex("ba80070000b938040000")
    # 0xD7E70: texture id == [0xDC7B9C] (image record 0x10) -> nexl_Capture[15] (0x20850C0+0x78);
    # == [0x1FC6B90 + 4k] (image record k + 1) -> [0x435200] + 8k = nexl_Capture[k]
    assert code(0xD7E9C, 2) == bytes.fromhex("3905") and code(0xD7EB1, 4) == bytes.fromhex("4883c078")
