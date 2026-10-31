"""Tests for tsuki.compose (pixel-shader formulas, blend states, draw effects, _FADS math).

The central check runs every one of the 35 pixel shaders embedded in eboot.bin through the
tsuki.gcn interpreter on random inputs and compares the MRT0 export with the numpy formula
(acceptance: max error < 1/255; measured 9.7e-4, the fp16 export rounding).  Real-data tests
skip without the game folder.
"""
from __future__ import annotations

import numpy as np
import pytest

from eboot import eboot_path
from tsuki import compose as cp

EBOOT = eboot_path()
needs_game = pytest.mark.skipif(EBOOT is None, reason="game folder not found (TSUKI_GAME_DIR)")


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------
def test_blend_state_alpha_over_is_straight_alpha() -> None:
    src = np.array([[0.8, 0.4, 0.2, 0.25]], np.float32)
    dst = np.array([[0.1, 0.2, 0.3, 1.0]], np.float32)
    out = cp.apply_blend_state(src, dst, 1)
    assert np.allclose(out[0, :3], src[0, :3] * 0.25 + dst[0, :3] * 0.75)
    assert np.isclose(out[0, 3], 0.25 * 0.25 + 1.0 * 0.75)


def test_min_max_states_edit_alpha_only() -> None:
    src = np.array([[0.9, 0.9, 0.9, 0.3]], np.float32)
    dst = np.array([[0.1, 0.5, 0.7, 0.6]], np.float32)
    for state, alpha in ((4, 0.3), (5, 0.3), (10, 0.0), (11, 0.6), (12, 0.09)):
        out = cp.apply_blend_state(src, dst, state)
        assert np.allclose(out[0, :3], dst[0, :3]), state
        assert np.isclose(out[0, 3], alpha), state
    # state 6 (effect 26): colour through the destination alpha, alpha kept
    out = cp.apply_blend_state(src, dst, 6)
    assert np.allclose(out[0, :3], src[0, :3] * 0.6 + dst[0, :3] * 0.4) and np.isclose(out[0, 3], 0.6)


def test_effect_words() -> None:
    e = cp.effect(0)
    assert (e.shader_mode, e.strength, e.blend_state, e.p2) == (0, 0.0, 1, 1.0)
    e = cp.effect(cp.layer_word(17))
    assert (e.shader_mode, e.strength, e.blend_state) == (17, 0.0, 1)
    e = cp.effect(cp.layer_word(5, True))           # dodge + 4th _STBR argument
    assert (e.flag, e.blend_state, e.p2) == (True, 0, 0.0)
    e = cp.effect(cp.layer_word(23, True))          # flag ignored outside FLAG_EFFECTS
    assert (e.flag, e.blend_state) == (False, 1)
    e = cp.effect(cp.layer_word(3))                 # mode 3 = the normal shader
    assert e.shader_mode == 0
    assert cp.masked_layer_word(0) == 26 and cp.masked_layer_word(17) == 43
    e = cp.effect(43)
    assert (e.shader_mode, e.strength, e.blend_state) == (17, 1.0, 1)
    assert [cp.effect(w).blend_state for w in (52, 55, 56, 57, 58)] == [2, 10, 11, 5, 4]
    assert cp.effect(0x44).e == 0                   # ids >= 0x44 -> 0
    assert cp.FLAG_EFFECTS == {5, 7, 9, 10, 15, 17, 31, 33, 35, 36, 41, 43}


def test_blend_modes_photoshop_identities() -> None:
    rng = np.random.default_rng(3)
    S = rng.random((500, 4), dtype=np.float32)
    D = rng.random((500, 4), dtype=np.float32)
    C = np.ones(4, np.float32)
    P = (1.0, 0.0, 1.0, 0.0)
    s, d = S[:, :3], D[:, :3]
    assert np.allclose(cp.ps_blend(21, S, D, C, P)[:, :3], d * s)                       # multiply
    assert np.allclose(cp.ps_blend(23, S, D, C, P)[:, :3], 1 - (1 - d) * (1 - s), atol=1e-6)
    assert np.allclose(cp.ps_blend(14, S, D, C, P)[:, :3], np.abs(d - s))                 # difference
    ov = np.where(d >= 0.5, 1 - 2 * (1 - d) * (1 - s), 2 * d * s)
    assert np.allclose(cp.ps_blend(22, S, D, C, P)[:, :3], ov, atol=1e-6)               # overlay
    # ps family output alpha = C.a * S.a with strength 0 -> fixed-function over
    assert np.allclose(cp.ps_blend(21, S, D, C, P)[:, 3], S[:, 3])
    # nexl family output alpha = 1 with strength 0, D.a with strength 1
    assert np.allclose(cp.ps_blend(17, S, D, C, P)[:, 3], 1.0)
    assert np.allclose(cp.ps_blend(17, S, D, C, (0.0, 1.0, 1.0, 0.0))[:, 3], D[:, 3])


def test_rule_parameters_and_timing() -> None:
    assert cp.rule_params(1.0) == (-1.0, 0.0, 0.0)
    assert cp.rule_params(0.5) == (0.0, 1.0, 0.0)
    assert cp.rule_params(0.0) == (1.0, 2.0, 0.0)
    r = np.linspace(0, 1, 11, dtype=np.float32)
    assert np.allclose(cp.rule_factor(r, 1.0), 1.0)          # all old at the start
    assert np.allclose(cp.rule_factor(r, 0.0), 0.0)          # all new at the end
    f = cp.rule_factor(r, 0.5)
    assert np.allclose(f, r * r * (3 - 2 * r), atol=1e-6)     # smoothstep(0, 1, r) half way
    assert np.isclose(cp.fads_step(1000), -0.016666, atol=1e-7)
    assert cp.fads_step(0) == -1.0
    # 1000 ms = 60 frames at 60 Hz (the 61st update reaches 0)
    assert cp.fads_progress(1000, 59) > 0 and cp.fads_progress(1000, 61) == 0.0
    old = np.zeros((2, 2, 4), np.float32)
    new = np.ones((2, 2, 4), np.float32)
    # vertex alpha bytes: weight trunc(255 p) / 255 (0xD9D20), 127/255 at p = 0.5
    assert cp.fads_vertex_alpha(0.5) == np.float32(127) * np.float32(1 / 255)
    assert cp.fads_vertex_alpha(1.0) == np.float32(1.0) and cp.fads_vertex_alpha(-0.1) == 0.0
    w = cp.fads_vertex_alpha(0.25)                            # 63 / 255
    assert np.allclose(cp.crossfade(old, new, 0.25)[..., :3], 1 - w)
    assert np.allclose(cp.transition_frame(1, old, new, 0.9), new)


def test_transition_capture_surfaces_and_rule_slot() -> None:
    # 0x6B910 jump table 0x209FF0 / 0x67820 jump table 0x209C50 (emulated: dlprobe_capture.py)
    assert cp.fads_capture(0) == 0 and cp.fads_capture(2) == 15 and cp.fads_capture(1) is None
    assert cp.fads_capture(12) == 0 and 12 in cp.CAPTURE15_TYPES and 0 not in cp.CAPTURE15_TYPES
    with pytest.raises(ValueError):
        cp.fads_capture(21)
    slot = cp.RuleSlot()
    assert slot.fads(0) is None                               # nothing loaded yet: fallback
    assert slot.fads(26) == 26
    assert slot.fads(0) == 26                                 # _FADS(2, ms, 0) keeps the rule
    assert slot.fads(5, fast_forward=True) == 26              # not loaded in fast-forward
    assert slot.sttr(0) is None                               # _STTR(l, 0) frees slot 0x4DD
    assert slot.sttr(3) == 3 and slot.fads(0) == 3            # a _STTR rule is used by _FADS too
    slot.free()
    old = np.zeros((1, 1, 4), np.float32)
    new = np.ones((1, 1, 4), np.float32)
    assert np.allclose(cp.transition_frame(2, old, new, 0.5, None), cp.crossfade(old, new, 0.5))


# ---------------------------------------------------------------------------
# real data: ISA interpretation vs formulas, engine tables
# ---------------------------------------------------------------------------
def proc_texture(seed: int):
    """Procedural texture: texel = f(u, v), deterministic in the exact float32 coordinates, so a
    shader that samples a resource at the wrong interpolant (or a formula that binds S / D to the
    wrong resource) gets different texels than the reference.  About 6 % of lanes are snapped to
    0, 0.25, .., 1 so the exact edge cases (black, white, d < 3/255) are hit as well."""
    k = np.arange(4, dtype=np.float64)

    def f(u: np.ndarray, v: np.ndarray) -> np.ndarray:
        x = np.sin(u.astype(np.float64)[:, None] * (12.9898 + 7.0 * seed + k)
                   + v.astype(np.float64)[:, None] * (78.233 + 3.0 * seed + 2.0 * k)) * 43758.5453
        t = (x - np.floor(x)).astype(np.float32)
        snap = u < 0.06
        t[snap] = np.round(t[snap] * 4) / 4
        return t
    return f


def ps_isa_errors(raws, swap_dst_coords: bool = False, swap_s_d: bool = False):
    """Worst relative error per PS between the interpreted ISA and ``ps_reference``.  The textures
    are procedural, so the reference must evaluate each resource at the interpolant the shader
    really uses: S, Y, CbCr at m_texcoord; D (dst_texture) and M (transition_tex) at
    m_transition.  ``swap_*`` are mutations used to prove the check is sensitive."""
    rng = np.random.default_rng(1)
    n = 4096
    C = rng.random((n, 4), dtype=np.float32)
    tc = rng.random((n, 2), dtype=np.float32)
    tr = rng.random((n, 2), dtype=np.float32)
    tex = {name: proc_texture(i) for i, name in enumerate(
        ("texture", "dst_texture", "transition_tex", "y_texture", "cbcr_texture"))}
    at_d = tc if swap_dst_coords else tr
    S = tex["texture"](tc[:, 0], tc[:, 1])
    D = tex["dst_texture"](at_d[:, 0], at_d[:, 1])
    M = tex["transition_tex"](at_d[:, 0], at_d[:, 1])
    Y = tex["y_texture"](tc[:, 0], tc[:, 1])
    U = tex["cbcr_texture"](tc[:, 0], tc[:, 1])
    if swap_s_d:
        S, D = D, S
    Q = (0.08, -0.12, 0.7, 0.0)
    worst = {}
    for idx, (raw, names) in raws.items():
        w = 0.0
        for P in ((0.25, 0.75, 0.0, 0.0), (0.0, 1.0, 1.0, 0.0), (-0.4, 0.6, 1.0, 0.3), (0.1, 0.9, 0.0, 1.0)):
            isa = cp.isa_reference(raw, names, tex, {"m_color": C, "m_texcoord": tc, "m_transition": tr},
                                   {"CommonFragmentParamsConstants": P, "TransitionParamsConstants": P,
                                    "SepiaConstants": Q})
            ref = np.clip(cp.ps_reference(idx, S, D, C, M, Y, U, P, Q), -65504, 65504)
            fin = np.isfinite(isa) & np.isfinite(ref)
            same = (np.isnan(isa) & np.isnan(ref)) | (isa == ref)
            err = np.where(fin, np.abs(isa - ref) / np.maximum(1, np.abs(ref)), np.where(same, 0, np.inf))
            w = max(w, float(err.max()))
        worst[idx] = w
    return worst


def load_ps_blobs():
    from eboot.selfelf import Eboot
    from eboot.shaders import find_blobs
    eb = Eboot.open(EBOOT)
    try:
        blobs = [b for b in find_blobs(eb) if b.stage == "PS"]
        return {b.index: (eb.read_va(b.va, b.size), b.names) for b in blobs}
    finally:
        eb.close()


@needs_game
def test_all_pixel_shaders_match_isa() -> None:
    raws = load_ps_blobs()
    assert sorted(raws) == sorted(cp.PS_KEYS)
    for idx, (raw, _names) in raws.items():
        assert int.from_bytes(raw[4:8], "little") == cp.PS_HASH[idx]
    worst = ps_isa_errors(raws)
    bad = {i: (cp.PS_KEYS[i], e) for i, e in worst.items() if not e < 1 / 255}
    assert not bad, bad
    assert max(worst.values()) < 1e-3


@needs_game
def test_engine_tables_match_compose() -> None:
    from tsuki.misc_compositing_transitions_shaders import read_engine_tables
    t = read_engine_tables(EBOOT)
    assert [cp.BlendState(*s[:6], bool(s[6])) for s in t.blend_states] == cp.BLEND_STATES
    assert t.effect_mode == cp.EFFECT_MODE
    assert t.effect_strength == cp.EFFECT_STRENGTH
    assert t.effect_blend_state == cp.EFFECT_BLEND_STATE
    assert t.stbr_to_effect == cp.STBR_TO_EFFECT
    assert set(t.flag_effects) == set(cp.FLAG_EFFECTS)
    assert t.sepia_clamp == (-0.5, -0.5, 0.0, 0.0, 0.5, 0.5, 0.0, 0.0, 1.0)
