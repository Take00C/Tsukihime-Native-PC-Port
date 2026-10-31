"""Compositing reference: the engine's pixel shaders, blend states, draw-effect tables and the
_FADS transitions, as numpy functions.  Full description: docs/formats/compositing.md.

Everything here was derived from eboot.bin (CUSA47999 v01.01):

* the 35 PSSL pixel shaders were decoded with tsuki.gcn and their output translated into the
  functions below; ``isa_reference`` runs the real GCN code through the tsuki.gcn interpreter so
  the tests compare both on random inputs (max error 9.7e-4 < 1/255 on all 35; the error is the
  fp16 export rounding, v_cvt_pkrtz_f16_f32);
* the fixed-function blend states come from the 14-record table at VA 0x2D8030 (read by
  0xF77E0, written to CB_BLEND0_CONTROL by 0x13C2D0);
* the per-draw effect tables (shader mode 0x2D5978, strength 0x2D59AC, blend state 0x2D55E0),
  the STBR-mode -> effect table 0x2D5860 (0xD24D0, masked layers only) and the 0x100-flag bit
  set 0xA1A800286A0 (0xD9F91) are copied verbatim; tsuki.misc_compositing_transitions_shaders
  re-reads them from the eboot for the tests;
* the transition timing / rule parameters come from 0x6B910, 0x68A70, 0x67820 and 0xD73B0;
  which captured frame each transition draws back (nexl_Capture[0] = image record 1 for the
  cross-fade, nexl_Capture[15] = image record 0x10 for the rule wipe) was emulated with
  scratch/misc_compositing_transitions_shaders/dlprobe_capture.py.

Colours are float32 RGBA in [0, 1] (straight alpha, as decoded by tsuki.image), arrays of shape
(..., 4).  Names used throughout:
    S  layer texel   (``texture``     sampled at m_texcoord)
    D  backdrop texel (``dst_texture``: the render target before this draw, sampled at the
       screen position m_transition)
    C  vertex colour (r, g, b, a); the engine's draw colour, alpha = layer opacity
    P  common_ps_params / transition_params float4; Q SepiaConstants (Cb, Cr, blend, -)
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, List, Optional, Sequence, Tuple

import numpy as np

__all__ = [
    "BLEND_SHADER_NAMES", "BLEND_STATES", "BlendState", "EFFECT_MODE", "EFFECT_STRENGTH",
    "EFFECT_BLEND_STATE", "STBR_TO_EFFECT", "FLAG_EFFECTS", "Effect", "effect", "layer_word",
    "masked_layer_word", "draw_layer", "fads_step", "fads_progress", "rule_factor",
    "transition_frame", "BLEND_STATE_NAMES", "common_ps_params", "isa_reference",
    "PS_KEYS", "PS_HASH", "ps_reference",
    "smoothstep_rule", "transition_factor", "rule_params", "ps_basic_2dtex", "ps_basic_2d",
    "ps_ycbcr", "ps_dummy", "ps_transition_2dtex", "ps_transition_2d", "sepia", "ps_sepia_2d",
    "ps_sepia_2dtex", "ps_sepia_transition_2d", "ps_sepia_transition_2dtex", "ps_blend",
    "apply_blend_state", "draw", "rule_wipe", "crossfade", "f32", "to_float", "to_u8",
    "CAPTURE15_TYPES", "fads_capture", "fads_vertex_alpha", "RuleSlot",
    "rtz16", "export_mrt0", "EXPORTS", "select_shader", "shade", "execute_list", "levels_error",
]

F = np.float32


def f32(x: object) -> np.ndarray:
    return np.asarray(x, dtype=np.float32)


def to_float(rgba_u8: np.ndarray) -> np.ndarray:
    return rgba_u8.astype(np.float32) / F(255)


def to_u8(rgba: np.ndarray) -> np.ndarray:
    """Float -> UNORM8 the way a render target stores it (clamp, round to nearest)."""
    return np.clip(np.rint(np.nan_to_num(rgba) * 255), 0, 255).astype(np.uint8)


# --------------------------------------------------------------------------------------
# MRT0 colour export (compositing.md 2.6)
# --------------------------------------------------------------------------------------

def rtz16(x: object) -> np.ndarray:
    """float32 -> IEEE binary16 rounded toward zero -> float32 (v_cvt_pkrtz_f16_f32).

    numpy converts to fp16 with round-to-nearest-even; where that rounded away from zero the
    result is moved one fp16 ulp back towards zero.  Overflow gives +-65504 (toward zero), an
    infinity stays infinite and NaN stays NaN."""
    x = f32(x)
    with np.errstate(all="ignore"):
        y = x.astype(np.float16)
        over = np.abs(y.astype(np.float32)) > np.abs(x)
        y = np.where(over, np.nextafter(y, np.float16(0)), y)
    return y.astype(np.float32)


def export_mrt0(x: object) -> np.ndarray:
    """What a pixel-shader output becomes on its way into a UNORM8 render target (2.6): the PS
    exports MRT0 as packed fp16 rounded toward zero (``compr``, v_cvt_pkrtz_f16_f32); the colour
    block clamps the source of a UNORM target to [0, 1] (NaN -> 0) before blending.  The result
    feeds ``apply_blend_state``; the store itself is ``to_u8``."""
    x = np.nan_to_num(f32(x), nan=0.0, posinf=1.0, neginf=0.0)
    return rtz16(np.clip(x, 0, 1))


# Export models: "f32" = the shader result straight into the blend (compose before 2.6, kept for
# comparison; also what a GPU driver that converts float32 straight to UNORM8 would give); "rtz16" =
# the PS4 export (2.6). The GPU executor emulates "rtz16" explicitly at the end of every fragment
# shader (Tsuki.Render CompositorShaders exportMrt0), so it does not depend on the driver's export.
EXPORTS = {
    "f32": lambda x: f32(x),
    "rtz16": export_mrt0,
}


def levels_error(gpu_u8: np.ndarray, ref_u8: np.ndarray) -> dict:
    """Per-pixel error in UNORM8 levels (max over the channels) between two RGBA8 images:
    max, and the share of pixels within 0 / 1 / 2 levels (the T5 (b) metric)."""
    e = np.abs(np.asarray(gpu_u8, np.int16) - np.asarray(ref_u8, np.int16))
    px = e.reshape(-1, e.shape[-1]).max(axis=1)
    return {"max": int(px.max()) if px.size else 0, "exact": float((px == 0).mean()),
            "le1": float((px <= 1).mean()), "le2": float((px <= 2).mean()), "pixels": int(px.size)}


# --------------------------------------------------------------------------------------
# shader constants (float32 literals from the ISA)
# --------------------------------------------------------------------------------------

def _lit(u: int) -> np.float32:
    return np.array([u], dtype=np.uint32).view(np.float32)[0]


K_Y = (_lit(0x3E991687), _lit(0x3F1645A2), _lit(0x3DE978D5))      # 0.299, 0.587, 0.114
K_CB_G, K_CR_G = _lit(0xBEB0331E), _lit(0xBF36D1E1)                # -0.34414, -0.71414
K_CR_R, K_CB_B = _lit(0x3FB374BC), _lit(0x3FE2D0E5)                # 1.402, 1.772
DODGE_BLACK = _lit(0x3C40C0C1)                                     # 3/255 = 0.0117647
# BT.709 limited range (basic YCbCr, PS #4)
YUV_Y_MUL, YUV_Y_ADD = _lit(0x3F950B0F), _lit(0xBD950B0F)          # 1.1644, -0.072775
YUV = {"g_cb": (_lit(0xBE5A6B51), _lit(0x3DDA6B51)), "g_cr": (_lit(0xBF086C22), _lit(0x3E886C22)),
       "r_cr": (_lit(0x3FE57732), _lit(0xBF657732)), "b_cb": (_lit(0x40073190), _lit(0xBF873190))}


def _sat(x: np.ndarray) -> np.ndarray:
    return np.where(np.isnan(x), F(0), np.clip(x, 0, 1)).astype(np.float32)


# --------------------------------------------------------------------------------------
# basic / transition / sepia pixel shaders
# --------------------------------------------------------------------------------------

def ps_basic_2dtex(S: np.ndarray, C: np.ndarray) -> np.ndarray:
    """PS #2 (basic 2DTex, basic VtxColTex, capture): S * C, all four channels."""
    return (f32(S) * f32(C)).astype(np.float32)


def ps_basic_2d(C: np.ndarray) -> np.ndarray:
    """PS #3 / #5 (basic 2D, basic VtxCol, clear): the interpolated colour."""
    return f32(C).copy()


def ps_dummy(S: np.ndarray, C: np.ndarray) -> np.ndarray:
    """PS #10 (dummy): (S.r*C.r, 0, 0, S.a*C.a)."""
    S, C = f32(S), f32(C)
    out = np.zeros(np.broadcast_shapes(S.shape, C.shape), np.float32)
    out[..., 0] = S[..., 0] * C[..., 0]
    out[..., 3] = S[..., 3] * C[..., 3]
    return out


def ps_ycbcr(Y: np.ndarray, CbCr: np.ndarray) -> np.ndarray:
    """PS #4 (basic YCbCr, movie frames): BT.709 limited-range NV12 -> RGB, alpha 1.
    Y = luma plane sample (red channel), CbCr = chroma sample (r = Cb, g = Cr)."""
    y = f32(Y)[..., 0] * YUV_Y_MUL + YUV_Y_ADD
    cb, cr = f32(CbCr)[..., 0], f32(CbCr)[..., 1]
    g = y + (cb * YUV["g_cb"][0] + YUV["g_cb"][1]) + (cr * YUV["g_cr"][0] + YUV["g_cr"][1])
    r = y + (cr * YUV["r_cr"][0] + YUV["r_cr"][1])
    b = y + (cb * YUV["b_cb"][0] + YUV["b_cb"][1])
    return np.stack([r, g, b, np.ones_like(r)], -1).astype(np.float32)


def smoothstep_rule(m: np.ndarray, p0: float, p1: float) -> np.ndarray:
    """t = sat((m - p0) / (p1 - p0)); t*t*(3 - 2t)  (v_rcp of p1-p0, then v_mul ... clamp)."""
    t = _sat((f32(m) - F(p0)) * (F(1) / (F(p1) - F(p0))))
    return (t * t * (F(3) - F(2) * t)).astype(np.float32)


def transition_factor(m: np.ndarray, P: Sequence[float]) -> np.ndarray:
    """PS #0 factor: clamp(|P.z - smoothstep(P.x, P.y, m)|, 0, 1)  (P.z = 0 -> plain, 1 -> inverted)."""
    s = smoothstep_rule(m, P[0], P[1])
    return _sat(np.abs(F(P[2]) - s))


def ps_transition_2dtex(mask: np.ndarray, C: np.ndarray, P: Sequence[float]) -> np.ndarray:
    """PS #0 (transition 2DTex): the bound texture IS the rule image; only its red channel
    is read (dmask 0x1).  Output = C * factor; the image colour is not sampled."""
    f = transition_factor(f32(mask)[..., 0], P)
    return (f32(C) * f[..., None]).astype(np.float32)


def ps_transition_2d(mask: np.ndarray, C: np.ndarray, P: Sequence[float]) -> np.ndarray:
    """PS #1 (transition 2D): transition_tex red channel at m_transition; C * smoothstep."""
    f = smoothstep_rule(f32(mask)[..., 0], P[0], P[1])
    return (f32(C) * f[..., None]).astype(np.float32)


def sepia(rgb: np.ndarray, Q: Sequence[float]) -> np.ndarray:
    """Sepia core of PS #6..#9: Y = luma(rgb) (BT.601 weights); target = YCbCr->RGB(Y, Q.x, Q.y);
    result = rgb + Q.z * (target - rgb)."""
    rgb = f32(rgb)
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    y = r * K_Y[0] + g * K_Y[1] + b * K_Y[2]
    cb, cr, k = F(Q[0]), F(Q[1]), F(Q[2])
    tr = y + K_CR_R * cr
    tg = y + K_CB_G * cb + K_CR_G * cr
    tb = y + K_CB_B * cb
    return np.stack([r + k * (tr - r), g + k * (tg - g), b + k * (tb - b)], -1).astype(np.float32)


def ps_sepia_2d(C: np.ndarray, Q: Sequence[float]) -> np.ndarray:
    """PS #9 (sepia 2D)."""
    C = f32(C)
    return np.concatenate([sepia(C[..., :3], Q), C[..., 3:4]], -1)


def ps_sepia_2dtex(S: np.ndarray, C: np.ndarray, Q: Sequence[float]) -> np.ndarray:
    """PS #8 (sepia 2DTex): sepia(S.rgb*C.rgb), alpha S.a*C.a."""
    x = ps_basic_2dtex(S, C)
    return np.concatenate([sepia(x[..., :3], Q), x[..., 3:4]], -1)


def ps_sepia_transition_2d(mask: np.ndarray, C: np.ndarray, P: Sequence[float],
                           Q: Sequence[float]) -> np.ndarray:
    """PS #7 (sepia transition 2D): sepia(C.rgb * s), alpha C.a * s, s = smoothstep(P.x, P.y, mask.r)."""
    x = ps_transition_2d(mask, C, P)
    return np.concatenate([sepia(x[..., :3], Q), x[..., 3:4]], -1)


def ps_sepia_transition_2dtex(S: np.ndarray, mask: np.ndarray, C: np.ndarray, P: Sequence[float],
                              Q: Sequence[float]) -> np.ndarray:
    """PS #6 (sepia transition 2DTex): rgb = sepia(S.rgb*C.rgb) (NOT multiplied by s),
    alpha = S.a * C.a * s with s = smoothstep(P.x, P.y, mask.r)."""
    x = ps_basic_2dtex(S, C)
    s = smoothstep_rule(f32(mask)[..., 0], P[0], P[1])
    return np.concatenate([sepia(x[..., :3], Q), (x[..., 3] * s)[..., None]], -1)


# --------------------------------------------------------------------------------------
# the 24 blend shaders (surface blend mode 1..25; mode 3 = basic 2DTex)
# --------------------------------------------------------------------------------------

BLEND_SHADER_NAMES: Dict[int, Tuple[str, int]] = {
    # mode: (shader key, PS blob index)
    1: ("add", 64), 2: ("addalpha", 63), 3: ("basic 2DTex", 2), 4: ("darken", 19),
    5: ("dodge", 21), 6: ("lighten", 22), 7: ("multiplicative", 23), 8: ("opaque", 24),
    9: ("screen", 40), 10: ("sub", 41), 11: ("ps_add", 25), 12: ("ps_burn", 26),
    13: ("ps_darken", 27), 14: ("ps_diff", 29), 15: ("ps_diff5", 28), 16: ("ps_dodge", 31),
    17: ("ps_dodge5", 30), 18: ("ps_exclusion", 32), 19: ("ps_hardlight", 33),
    20: ("ps_lighten", 34), 21: ("ps_multi", 35), 22: ("ps_overlay", 36), 23: ("ps_screen", 37),
    24: ("ps_softlight", 38), 25: ("ps_sub", 39),
}
# shaders whose output alpha is lerp(1, D.a, P.y) ("nexl" family); the others use
# lerp(C.a*S.a, D.a, P.y) ("PS" family)
_ALPHA_ONE = {1, 2, 4, 5, 6, 7, 8, 9, 10, 15, 17}


def _min_nan(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    return np.fmin(a, b)      # v_min_f32: a NaN operand yields the other operand


def _max_nan(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    return np.fmax(a, b)


def ps_blend(mode: int, S: np.ndarray, D: np.ndarray, C: np.ndarray, P: Sequence[float]) -> np.ndarray:
    """Output of the blend pixel shader for surface blend mode ``mode`` (1..25).

    The shader does the whole blend against the backdrop copy D; fixed-function blending
    (``apply_blend_state``) is applied to this output afterwards."""
    if mode == 3:
        return ps_basic_2dtex(S, C)
    if mode not in BLEND_SHADER_NAMES:
        raise ValueError(f"blend mode {mode}")
    S, D, C = f32(S), f32(D), f32(C)
    shape = np.broadcast_shapes(S.shape, D.shape, C.shape)
    S, D, C = (np.broadcast_to(x, shape) for x in (S, D, C))
    s, d, ca, sa = S[..., :3], D[..., :3], C[..., 3:4], S[..., 3:4]
    p1, p2 = F(P[1]), F(P[2])
    one = F(1)
    with np.errstate(all="ignore"):
        if mode == 1:                                  # add
            rgb = _min_nan(one, d + ca * s)
        elif mode == 2:                                # addalpha: premultiplied "over", clamped
            rgb = _min_nan(one, ca * s + d * (one - ca * sa))
        elif mode == 4:                                # darken
            rgb = d + ca * (np.fmin(d, s) - d)
        elif mode == 5:                                # dodge
            k = ca * _max_nan(p2, sa)
            rgb = _min_nan(d * (one / (one - s * k)), np.where(d < DODGE_BLACK, F(0), one))
        elif mode == 6:                                # lighten
            rgb = d + ca * (np.fmax(d, s) - d)
        elif mode == 7:                                # multiplicative
            k = ca * _max_nan(p2, sa)
            rgb = d + k * (s * d - d)
        elif mode == 8:                                # opaque
            rgb = d + ca * (s - d)
        elif mode == 9:                                # screen
            k = ca * _max_nan(p2, sa)
            rgb = one - (one - d) * (one - s * k)
        elif mode == 10:                               # sub (subtracts the inverse of S)
            rgb = _max_nan(F(0), d + ca * (s - one))
        elif mode == 11:                               # ps_add (uses C.rgb, not C.a)
            rgb = _min_nan(one, d + C[..., :3] * s)
        elif mode == 12:                               # ps_burn (colour burn)
            rgb = _max_nan(F(0), (one / s) * (d - one) + one)
        elif mode == 13:                               # ps_darken
            rgb = np.fmin(d, s)
        elif mode == 14:                               # ps_diff
            rgb = np.abs(d - s)
        elif mode == 15:                               # ps_diff5: source pre-multiplied by C.a*S.a
            rgb = np.abs(d - s * (ca * sa))
        elif mode == 16:                               # ps_dodge (colour dodge)
            rgb = _min_nan(one, d * (one / (one - s)))
        elif mode == 17:                               # ps_dodge5
            k = ca * sa
            rgb = _min_nan(d * (one / (one - s * k)), np.where(d < DODGE_BLACK, F(0), one))
        elif mode == 18:                               # ps_exclusion
            rgb = s + d - F(2) * d * s
        elif mode == 19:                               # ps_hardlight on X = C.rgb*S
            x = C[..., :3] * s
            rgb = np.where(x >= F(0.5), one - F(2) * (one - d) * (one - x), F(2) * x * d)
        elif mode == 20:                               # ps_lighten
            rgb = np.fmax(d, s)
        elif mode == 21:                               # ps_multi
            rgb = d * s
        elif mode == 22:                               # ps_overlay
            rgb = np.where(d >= F(0.5), one - F(2) * (one - d) * (one - s), F(2) * d * s)
        elif mode == 23:                               # ps_screen
            rgb = s + d - d * s
        elif mode == 24:                               # ps_softlight: |D| ** e
            e = np.where(s >= F(0.5), one / (F(2) * s), F(2) * (one - s))
            rgb = np.exp2(np.log2(np.abs(d)) * e)
        elif mode == 25:                               # ps_sub (= linear burn)
            rgb = _max_nan(F(0), d + s - one)
        else:  # pragma: no cover
            raise ValueError(mode)
        if mode in _ALPHA_ONE:
            a = one + p1 * (D[..., 3:4] - one)
        else:
            k = ca * sa
            a = k + p1 * (D[..., 3:4] - k)
    return np.concatenate([rgb, a], -1).astype(np.float32)


# --------------------------------------------------------------------------------------
# fixed-function blend states (table 0x2D8030, 14 x 28 bytes)
# --------------------------------------------------------------------------------------

@dataclass(frozen=True)
class BlendState:
    """One record of the table at VA 0x2D8030 (read by 0xF77E0 into CB_BLEND0_CONTROL, written
    to the command buffer by 0x13C2D0 as SET_CONTEXT_REG 0x1E0): u32 +0 COLOR_SRCBLEND,
    +4 COLOR_DESTBLEND, +8 COLOR_COMB_FCN, +0xC ALPHA_SRCBLEND, +0x10 ALPHA_DESTBLEND,
    +0x14 ALPHA_COMB_FCN, u8 +0x18 enable (sets both ENABLE and SEPARATE_ALPHA_BLEND)."""
    color_src: int      # BLEND_* multiplier (0 zero, 1 one, 4 src a, 5 1-src a, 6 dst a, 7 1-dst a, 9 1-dst rgb)
    color_dst: int
    color_func: int     # COMB_*: 0 src+dst, 1 src-dst, 2 min, 3 max, 4 dst-src (factors applied in all five)
    alpha_src: int
    alpha_dst: int
    alpha_func: int
    enable: bool


BLEND_STATES: List[BlendState] = [BlendState(*r[:6], bool(r[6])) for r in [
    (1, 1, 0, 1, 1, 0, 0), (4, 5, 0, 4, 5, 0, 1), (4, 1, 0, 4, 1, 0, 1), (0, 5, 0, 4, 5, 0, 1),
    (0, 1, 3, 1, 0, 3, 1), (0, 1, 3, 1, 1, 2, 1), (6, 7, 0, 0, 1, 3, 1), (6, 1, 0, 0, 1, 3, 1),
    (4, 1, 1, 4, 1, 1, 1), (9, 0, 0, 0, 1, 3, 1), (0, 1, 3, 0, 0, 2, 1), (0, 1, 3, 1, 1, 3, 1),
    (0, 1, 0, 4, 0, 0, 1), (6, 7, 0, 4, 7, 0, 1)]]
# what each state does with the multipliers applied (MIN/MAX included, see BlendState):
BLEND_STATE_NAMES = [
    "0 replace (blending off: rgba = src)",
    "1 alpha over: rgb = s*sa + d*(1-sa), a = sa*sa + da*(1-sa)",
    "2 additive: rgb = s*sa + d, a = sa*sa + da",
    "3 erase: rgb = d*(1-sa), a = sa*sa + da*(1-sa)",
    "4 alpha := sa (rgb kept)",
    "5 alpha := min(sa, da) (rgb kept)",
    "6 through dst alpha: rgb = s*da + d*(1-da), a kept",
    "7 add through dst alpha: rgb = s*da + d, a kept",
    "8 subtract: rgb = s*sa - d, a = sa*sa - da",
    "9 rgb = s*(1-d), a kept",
    "10 alpha := 0 (rgb kept)",
    "11 alpha := max(sa, da) (rgb kept)",
    "12 alpha := sa*sa (rgb kept)",
    "13 rgb = s*da + d*(1-da), a = sa*sa + da*(1-da)",
]


def _mult(code: int, src: np.ndarray, dst: np.ndarray, alpha: bool) -> np.ndarray:
    sa, da = src[..., 3:4], dst[..., 3:4]
    one = np.ones_like(sa)
    if alpha:
        colourish = {2: src[..., 3:4], 3: 1 - src[..., 3:4], 8: dst[..., 3:4], 9: 1 - dst[..., 3:4]}
    else:
        colourish = {2: src[..., :3], 3: 1 - src[..., :3], 8: dst[..., :3], 9: 1 - dst[..., :3]}
    table = {0: 0 * one, 1: one, 4: sa, 5: 1 - sa, 6: da, 7: 1 - da,
             10: np.minimum(sa, 1 - da)}
    if code in colourish:
        return colourish[code]
    if code in table:
        return table[code]
    raise ValueError(f"blend multiplier {code} not used by this engine")


def _comb(func: int, s: np.ndarray, d: np.ndarray, ms: np.ndarray, md: np.ndarray) -> np.ndarray:
    """CB combine function with both multipliers applied.  For MIN/MAX this is the GCN
    behaviour (the AMD radeonsi driver treats func(src*F, dst*ONE) as commutative only when F
    does not depend on dst, i.e. it relies on the factors being applied; GL/D3D ignore them,
    which is why Mesa forces ONE for those APIs).  It is also the only reading under which the
    engine's states 4/5/10/11/12 (colour ZERO/ONE) make sense: they edit the alpha channel only."""
    a, b = s * ms, d * md
    if func == 0:
        return a + b
    if func == 1:
        return a - b
    if func == 2:
        return np.minimum(a, b)
    if func == 3:
        return np.maximum(a, b)
    if func == 4:
        return b - a
    raise ValueError(func)


def apply_blend_state(src: np.ndarray, dst: np.ndarray, state: int) -> np.ndarray:
    """Fixed-function blend of a shader output ``src`` onto the render target ``dst``
    (both float RGBA in [0, 1]); the result is clamped to [0, 1] like a UNORM8 target."""
    st = BLEND_STATES[state if 0 <= state < 14 else 1]          # 0xF77E0: id >= 14 -> record 1
    src, dst = f32(src), f32(dst)
    if not st.enable:
        return np.clip(src, 0, 1).astype(np.float32)
    rgb = _comb(st.color_func, src[..., :3], dst[..., :3], _mult(st.color_src, src, dst, False),
                _mult(st.color_dst, src, dst, False))
    a = _comb(st.alpha_func, src[..., 3:4], dst[..., 3:4], _mult(st.alpha_src, src, dst, True),
              _mult(st.alpha_dst, src, dst, True))
    return np.clip(np.concatenate([rgb, a], -1), 0, 1).astype(np.float32)


# --------------------------------------------------------------------------------------
# draw effects: e = draw-mode word & 0xFF (display-list record type 2, executor 0xD9F91),
# turned into surface fields by the per-quad draw 0xD2BE0
# --------------------------------------------------------------------------------------

EFFECT_MODE = [1, 2, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23,
               24, 25, 0, 1, 2, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
               21, 22, 23, 24, 25]                                    # 0x2D5978, index e-1
EFFECT_STRENGTH = [0.0] * 26 + [1.0, 1.0, 0.0] + [1.0] * 22             # 0x2D59AC, index e-1
EFFECT_BLEND_STATE = ([1] * 26 + [6, 1, 1, 6] + [1] * 22 +             # 0x2D55E0, index e
                      [2, 7, 1, 10, 11, 5, 4, 9, 1, 1, 1, 2, 1, 13, 1, 1])
STBR_TO_EFFECT = ([m + 26 for m in range(1, 26)] + list(range(26, 52)) +   # 0x2D5860, index m-1
                  [53, 53, 54, 55, 56, 57, 58, 26, 26, 54])               # (0xD24D0; else 26)
FLAG_EFFECTS = frozenset(i for i in range(64) if (0xA1A800286A0 >> i) & 1)   # 0xD9FB9
# named effects used by engine code (VA of a use in brackets)
EFFECT_NORMAL = 0          # alpha over                                   [0x67A39 crossfade]
EFFECT_THROUGH_DST_A = 26  # basic shader, state 6: rgb = s*Da + d*(1-Da) [0x67A1F rule wipe]
EFFECT_ADD = 52            # state 2                                      [0x679B1, overridden]
EFFECT_ALPHA_CLEAR = 55    # state 10: alpha := 0                         [0x503B5]
EFFECT_ALPHA_UNION = 56    # state 11: alpha := max                       [0x6B9B9 capture]
EFFECT_ALPHA_MIN = 57      # state 5: alpha := min(sa, da)                [0x4F9F9]
EFFECT_ALPHA_SET = 58      # state 4: alpha := sa                         [0x67A0B rule mask, 0x502E4]


@dataclass(frozen=True)
class Effect:
    e: int                  # draw effect id 0..67
    flag: bool              # bit 0x100 of the word was set and e is in FLAG_EFFECTS
    shader_mode: int        # DisplaySurface +0x608 (0 = normal shader selection)
    strength: float         # +0x60C -> common_ps_params.y (P1); P0 = 1 - strength
    blend_state: int        # +0x518 -> BLEND_STATES index
    p2: float               # common_ps_params.z (+0x604)


def effect(word: int) -> Effect:
    """Decode a draw-mode word (record type 2, written by 0xD9D90) into the parameters the quad
    draw 0xD2BE0 and DisplaySurface_Draw 0xF12F0 give the draw."""
    low = word & 0xFF
    e = low if low < 0x44 else 0
    rest = word & 0xFFFFFEFF
    flag = bool(word & 0x100) and rest <= 0x2B and rest in FLAG_EFFECTS
    if 1 <= e <= 51:
        mode, strength = EFFECT_MODE[e - 1], EFFECT_STRENGTH[e - 1]
    else:
        mode, strength = 0, 0.0
    state = 0 if flag else EFFECT_BLEND_STATE[e]
    p2 = 0.0 if (mode in (5, 7, 9, 10) and state == 0) else 1.0      # 0xF146F..0xF1490
    return Effect(e, flag, mode, strength, state, p2)


def layer_word(mode: int, flag: bool = False) -> int:
    """Draw-mode word of an ordinary scene layer with _STBR mode ``mode`` (0..25) and 4th
    argument ``flag``: (flag << 8) | mode (0x4F320 at 0x5085E, emulated: STBR 17 -> 0x11,
    STBR(…,23,,1) -> 0x117)."""
    return (0x100 if flag else 0) | (mode & 0xFFFF)


def masked_layer_word(mode: int, flag: bool = False) -> int:
    """Word of the second pass of a layer drawn through the render-target alpha (0x4F0E0 /
    0x505A3 / 0x4FEB4: effect 0xD24D0(mode) = 26 + mode for 1..25, 26 for mode 0), after a
    first pass with EFFECT_ALPHA_SET or EFFECT_ALPHA_MIN has written the mask into the alpha."""
    e = STBR_TO_EFFECT[mode - 1] if 1 <= mode <= 61 else 26
    return (0x100 if flag else 0) | e


def common_ps_params(eff: Effect) -> Tuple[float, float, float, float]:
    """(+0x5FC, +0x600, +0x604, +0x608) = (1 - strength, strength, P2, mode byte bits); the
    blend shaders read P.y and P.z only."""
    return (1.0 - eff.strength, eff.strength, eff.p2, 0.0)


def draw(dst: np.ndarray, S: np.ndarray, C: np.ndarray, word: int,
         sepia_q: Optional[Sequence[float]] = None, export: str = "f32") -> np.ndarray:
    """One textured quad drawn with draw-mode word ``word`` onto ``dst`` (per pixel; S, C
    already sampled/interpolated, same shape as dst): shader selection (0xF0E70) for a
    texture without transition parameters, shader output, then the blend state.  The backdrop
    D of the blend shaders is ``dst`` itself (the render target as it is before this draw).
    ``export`` selects the colour-export model of 2.6 ("rtz16" = the PS4; the default "f32"
    keeps the float32 shader result, as before 2.6)."""
    eff = effect(word)
    P = common_ps_params(eff)
    if eff.shader_mode == 0:
        out = ps_sepia_2dtex(S, C, sepia_q) if sepia_q is not None else ps_basic_2dtex(S, C)
    else:
        out = ps_blend(eff.shader_mode, S, dst, C, P)
    return apply_blend_state(EXPORTS[export](out), dst, eff.blend_state)


def draw_layer(dst: np.ndarray, S: np.ndarray, opacity: float, mode: int = 0, flag: bool = False,
               tint: Sequence[float] = (1.0, 1.0, 1.0)) -> np.ndarray:
    """An ordinary scene layer: vertex colour (tint, opacity = STCH alpha / 255)."""
    C = np.array([tint[0], tint[1], tint[2], opacity], np.float32)
    return draw(dst, S, C, layer_word(mode, flag))


# --------------------------------------------------------------------------------------
# transitions (_FADS)
# --------------------------------------------------------------------------------------

FRAME_MS = np.float32(16.666)        # 0x209ADC


def fads_step(ms: int, kind: int = 0) -> np.float32:
    """Per-frame progress increment set by 0x6B910 (progress starts at 1.0, VA 0xE20CD0):
    -1 / (ms / 16.666) for ms > 0, else -1 (one frame).  Transition kinds 12 and 49 add 100 ms."""
    if kind in (12, 49) and ms > 0:
        ms += 100
    if ms <= 0:
        return np.float32(-1.0)
    return np.float32(-1.0) / (np.float32(ms) / FRAME_MS)


def fads_progress(ms: int, frames: int, kind: int = 0, speed: int = 1) -> np.float32:
    """Remaining progress after ``frames`` 60 Hz updates (0x68A70 at 0x69578..0x695DB): each
    update adds speed * min(3, frames-per-update) * step in float32 and clamps at 0 (the
    transition ends when it reaches 0).  ``speed`` is the fast-forward multiplier 0x7A6180."""
    p = np.float32(1.0)
    st = fads_step(ms, kind)
    for _ in range(frames):
        p = np.float32(p + np.float32(speed) * st)
        if p <= 0:
            return np.float32(0.0)
    return p


def rule_params(progress: float) -> Tuple[float, float, float]:
    """Transition parameters given to the rule picture for remaining progress ``progress``:
    0x67958 calls 0xD73B0(slot 0x4DD, a = 256 - trunc(progress * 256), b = 256), which stores
    (lo, hi, inv) = (max(-1, a/128 - b/256), min(2, a/128), 0) on every sub-texture
    (0xFB090, texture object +0xF0/+0xF4/+0xF8, flag +0xFC = 1)."""
    a = 256 - int(np.float32(progress) * np.float32(256))
    x0 = np.float32(a) * np.float32(0.0078125)
    x1 = x0 + np.float32(256) * np.float32(-0.00390625)
    return float(max(np.float32(-1), x1)), float(min(np.float32(2), x0)), 0.0


def rule_factor(rule_r: np.ndarray, progress: float) -> np.ndarray:
    """Weight of the OLD picture at each pixel: the transition-2DTex output alpha of the rule
    picture (red channel, white vertex colour) for ``progress``."""
    return transition_factor(f32(rule_r), rule_params(progress))


def rule_wipe(old: np.ndarray, new: np.ndarray, rule: np.ndarray, progress: float) -> np.ndarray:
    """One frame of _FADS(2, ms, n) (process 0xC draw 0x67820, case 0x67958), on top of the new
    scene ``new`` already in the render target:
      1. rule picture, effect 58 (EB780 overrides the 52 set just before; state 4):
         RT.alpha := f, rgb untouched, f = rule_factor(rule.r, progress);
      2. captured old frame, effect 26 (state 6): rgb = old * f + new * (1 - f), alpha kept.
    Emulated record order: [2:0][2:52][2:58]{rule quad}[2:26]{capture quad}."""
    f = rule_factor(f32(rule)[..., 0], progress)[..., None]
    out = f32(new).copy()
    out[..., :3] = f32(old)[..., :3] * f + f32(new)[..., :3] * (1 - f)
    out[..., 3:4] = f
    return out.astype(np.float32)


def fads_vertex_alpha(progress: float) -> np.float32:
    """Cross-fade weight of the old frame as the GPU sees it: 0xD9D20 stores the colour as bytes
    (trunc(max(0, a) * 255.0) in float32) and 0xD2BE0 turns them back into floats with the scale
    vector 0x2D4260 = 1/255.  So the weight is trunc(255 p) / 255 (127/255 at p = 0.5) [EMU]."""
    a = max(F(0), F(progress))
    return F(int(a * F(255.0))) * F(1.0 / 255.0)


def crossfade(old: np.ndarray, new: np.ndarray, progress: float) -> np.ndarray:
    """One frame of _FADS(0, ms) (and of the type-2 fallback without a rule picture): the
    captured old frame is drawn full screen with effect 0 (alpha over) and vertex colour
    (1, 1, 1, p) (0x67A30..0x67A56; fallback 0x682D6): rgb = old * w + new * (1 - w) with
    w = fads_vertex_alpha(p).  The capture's alpha is 1 (the effect-56 fill of 0x6B910), so the
    over blend uses w alone.  Type 0 reads nexl_Capture[0] (image record 1), the fallback
    nexl_Capture[15] (image record 0x10); both hold the frame captured at the transition start
    (``fads_capture``)."""
    w = fads_vertex_alpha(progress)
    out = f32(new).copy()
    out[..., :3] = f32(old)[..., :3] * w + f32(new)[..., :3] * (F(1) - w)
    return out


# 0x6B910 jump table 0x209FF0 (index type - 2): these types also capture the frame into
# nexl_Capture[15] (0xDCD20, display-list record type 6 with b = 15) and set 0xE20CDC = 1.
CAPTURE15_TYPES = frozenset({2, *range(12, 25), 29, 30, 31, 32, 46, 49, 52, 53, 70})
# 0x67820 jump table 0x209C50 (index type - 1): cases other than the default 0x67A30
_DRAW_NOTHING = frozenset({1, 49})                 # 0x6837D
_DRAW_UNDECODED = frozenset({18, 20, 21, 22, 23, 24, 68, 69, 70})   # engine-only screens


def fads_capture(kind: int) -> Optional[int]:
    """Index of the nexl_Capture surface (0x20850C0 + 8 * index) that the transition draw of
    ``kind`` reads back, or None when it draws nothing.  [E + EMU, dlprobe_capture.py]

    Every transition start (0x6B910) captures the current render target into nexl_Capture[0]
    (record ``[6 b=0]``, image record 1); types in CAPTURE15_TYPES capture it into
    nexl_Capture[15] too (image record 0x10).  The rule wipe and its no-rule fallback (type 2,
    0x67958 / 0x682D6) draw image record 0x10; type 0 and every other default-path type
    (0x67A30) draw image record 1."""
    if kind in _DRAW_NOTHING:
        return None
    if kind == 2:
        return 15
    if kind in _DRAW_UNDECODED or not 0 <= kind <= 70:
        raise ValueError(f"_FADS type {kind}: draw path not decoded (engine-only)")
    return 0


class RuleSlot:
    """Texture slot 0x4DD and its cache 0x7A67E0, as the engine maintains them [E]:

    * ``_FADS(type, ms, n)`` with n > 0 and not in fast-forward (0x81687) loads rule n
      (0x6B460(n - 1); a repeat of the cached id is a no-op).  n = 0, a negative n and
      fast-forward leave the slot as it is, so ``_FADS(2, ms, 0)`` reuses the previous rule;
    * ``_STTR(layer, n)`` (0x99947 -> 0x99A26) calls 0x6B460(n - 1) too: n > 0 loads rule n,
      n <= 0 frees the slot (0xEB540);
    * the script-end cleanup 0x7F1A0 (``_END_``, ``_RTM_``, ``_RTMN``, title 0x6E1DD) and the
      resets at 0x79490, 0x7CB90, 0x9B4F4, 0xA34A0 free it (0x6B460(-1)).

    The rule wipe uses the slot whatever loaded it; with an empty slot it falls back to the
    cross-fade (0x67962: tex | 0x7A67E0 < 0)."""

    def __init__(self) -> None:
        self.rule: Optional[int] = None        # rule id 1..49 held in slot 0x4DD

    def fads(self, rule: int, fast_forward: bool = False) -> Optional[int]:
        if rule > 0 and not fast_forward:
            self.rule = rule
        return self.rule

    def sttr(self, rule: int) -> Optional[int]:
        self.rule = rule if rule > 0 else None
        return self.rule

    def free(self) -> None:
        self.rule = None


def transition_frame(kind: int, old: np.ndarray, new: np.ndarray, progress: float,
                     rule: Optional[np.ndarray] = None) -> np.ndarray:
    """_FADS kinds used by the scripts: 0 cross-fade, 1 immediate (no overlay: 0x6837D),
    2 rule wipe.  ``old`` is the frame captured at the transition start (``fads_capture``);
    ``rule`` is the picture currently in texture slot 0x4DD (``RuleSlot``: the last rule
    loaded by _FADS / _STTR, kept by _FADS(2, ms, 0) and fast-forward).  Only an empty slot
    (None) gives the 0x682D6 cross-fade fallback."""
    if kind == 1 or progress <= 0:
        return f32(new).copy()
    if kind == 2 and rule is not None:
        return rule_wipe(old, new, rule, progress)
    return crossfade(old, new, progress)


# --------------------------------------------------------------------------------------
# per-blob reference (index = eboot.shaders blob index, PS only)
# --------------------------------------------------------------------------------------

PS_KEYS: Dict[int, str] = {
    0: "transition 2DTex", 1: "transition 2D", 2: "basic 2DTex / basic VtxColTex / capture",
    3: "basic 2D / basic VtxCol", 4: "basic YCbCr", 5: "clear", 6: "sepia transition 2DTex",
    7: "sepia transition 2D", 8: "sepia 2DTex", 9: "sepia 2D", 10: "dummy",
    **{bi: key for _m, (key, bi) in BLEND_SHADER_NAMES.items() if bi != 2},
}
# u32 at blob +4 (the hash shadPS4 logs), to recognise the blobs of app 01.01
PS_HASH: Dict[int, int] = {
    0: 0xC68DDD18, 1: 0xF2C62854, 2: 0x3EEC2E96, 3: 0x46563F38, 4: 0x6FAE2BB1, 5: 0x46563F38,
    6: 0x148778DA, 7: 0xE62D0125, 8: 0x0EDC4C05, 9: 0xB4F66B70, 10: 0x98DF76AF, 19: 0x2F355F61,
    21: 0x6D6B9B26, 22: 0xF2393DE5, 23: 0xA1BE2C10, 24: 0x03A218B8, 25: 0xD1AD6E88,
    26: 0x4CC1207B, 27: 0x23AF5249, 28: 0xA364FB0C, 29: 0x84827E89, 30: 0x5234C25B,
    31: 0x7E515809, 32: 0x2FA774F8, 33: 0xF21071AD, 34: 0x1EFE72CA, 35: 0x2882318A,
    36: 0x4132D49D, 37: 0xF27D5A28, 38: 0x11E1ECBC, 39: 0x103AB770, 40: 0x44C61613,
    41: 0x4EBDFB07, 63: 0x8281FD08, 64: 0x58B4D46B,
}


def ps_reference(index: int, S: np.ndarray, D: np.ndarray, C: np.ndarray, M: np.ndarray,
                 Y: np.ndarray, U: np.ndarray, P: Sequence[float], Q: Sequence[float]) -> np.ndarray:
    """Numpy formula of PS blob ``index`` with every input it may read: S texture, D dst_texture,
    C vertex colour, M transition_tex, Y / U the movie planes, P common/transition params,
    Q sepia constants (the blob reads only what its reflection lists)."""
    if index == 0:
        return ps_transition_2dtex(S, C, P)
    if index == 1:
        return ps_transition_2d(M, C, P)
    if index == 2:
        return ps_basic_2dtex(S, C)
    if index in (3, 5):
        return ps_basic_2d(C)
    if index == 4:
        return ps_ycbcr(Y, U)
    if index == 6:
        return ps_sepia_transition_2dtex(S, M, C, P, Q)
    if index == 7:
        return ps_sepia_transition_2d(M, C, P, Q)
    if index == 8:
        return ps_sepia_2dtex(S, C, Q)
    if index == 9:
        return ps_sepia_2d(C, Q)
    if index == 10:
        return ps_dummy(S, C)
    mode = next(m for m, (_k, bi) in BLEND_SHADER_NAMES.items() if bi == index)
    return ps_blend(mode, S, D, C, P)


# --------------------------------------------------------------------------------------
# ISA reference (tsuki.gcn) used by the tests
# --------------------------------------------------------------------------------------

def isa_reference(blob: bytes, names: Sequence[str], textures: Dict[str, np.ndarray],
                  interp: Dict[str, np.ndarray], cbuffers: Dict[str, Sequence[float]]) -> np.ndarray:
    """Run a pixel-shader blob through the GCN interpreter.

    ``names``: the blob's reflection names (resources first, then constant buffers, in
    api-slot order, as eboot.shaders.reflection_names lists them).  ``textures``: by resource
    name (``texture``, ``dst_texture``, ``transition_tex``, ``y_texture``, ``cbcr_texture``)
    either per-lane texels (n, 4), returned whatever the coordinates, or a callable
    ``f(u, v) -> (n, 4)`` evaluated at the coordinates the shader's image_sample actually
    passes (so a test can detect a texcoord / transition swap or a wrong slot binding);
    ``interp``: per-lane interpolants (n, k) for ``m_color``, ``m_texcoord``, ``m_transition``
    (passed through v_interp unchanged: I/J weights multiply zero deltas); ``cbuffers``: float4
    by buffer name.  Returns the MRT0 export as float32 (n, 4)."""
    import struct

    from . import gcn

    h = gcn.parse_shader(blob)
    insns = gcn.decode(h.code)
    res_names = [x for x in names if x in ("texture", "dst_texture", "transition_tex", "y_texture",
                                           "cbcr_texture")]
    cb_names = [x for x in names if x.endswith("Constants")]
    in_names = [x.split(".")[1] for x in names if x.startswith("Input.")]
    n = next(iter(interp.values())).shape[0]
    m = gcn.Machine(n)
    eud_addr = 0x4000_0000
    eud = bytearray(256)
    cb_base = 0x1000_0000

    def put_user(start: int, dwords: List[int]) -> None:
        if start < 16:
            for k, v in enumerate(dwords):
                m.s[start + k] = v
        else:
            struct.pack_into(f"<{len(dwords)}I", eud, (start - 16) * 4, *dwords)

    for sl in h.slots:
        if sl.usage == 0x00:      # T#
            put_user(sl.start_reg, gcn.make_tsharp(100 + sl.api_slot))
        elif sl.usage == 0x01:    # S#
            put_user(sl.start_reg, gcn.make_ssharp(sl.api_slot))
        elif sl.usage == 0x02:    # V# constant buffer
            addr = cb_base + 0x100 * sl.api_slot
            vals = cbuffers[cb_names[sl.api_slot]]
            m.mem[addr] = struct.pack("<4f", *vals)
            put_user(sl.start_reg, gcn.make_vsharp(addr, 4, 16))
        elif sl.usage == 0x1B:    # extended user data pointer
            m.s[sl.start_reg], m.s[sl.start_reg + 1] = eud_addr & 0xFFFFFFFF, eud_addr >> 32
        else:
            raise gcn.GcnError(f"unexpected input usage {sl.name}")
    m.mem[eud_addr] = bytes(eud)
    m.s[h.user_sgprs] = 0          # primitive mask (feeds M0)
    rng = np.random.default_rng(12345)
    m.v[0] = gcn.Machine.U(rng.random(n, dtype=np.float32))   # I
    m.v[1] = gcn.Machine.U(rng.random(n, dtype=np.float32))   # J
    zero = np.zeros(n, np.float32)
    for a, nm in enumerate(in_names):
        vals = interp[nm]
        for c in range(vals.shape[1]):
            m.interp[(a, c)] = (vals[:, c].astype(np.float32), zero, zero)

    def sample(tid: int, sid: int, coords: List[np.ndarray], mm: "gcn.Machine", ins: "gcn.Insn") -> np.ndarray:
        api = tid - 100
        name = res_names[0] if len(res_names) == 1 else res_names[api]
        tex = textures[name]
        if callable(tex):
            vals = tex(np.asarray(coords[0], np.float32), np.asarray(coords[1], np.float32))
            return np.asarray(vals, np.float32).T
        return np.asarray(tex, np.float32).T

    m.sample = sample
    m.run(insns)
    vals, en, compr = m.exports[0]
    if compr:
        lo = [gcn.f16_to_f32((vals[k] & 0xFFFF).astype(np.uint16)) for k in range(2)]
        hi = [gcn.f16_to_f32((vals[k] >> 16).astype(np.uint16)) for k in range(2)]
        return np.stack([lo[0], hi[0], lo[1], hi[1]], -1)
    return np.stack([gcn.Machine.F(vals[k]) for k in range(4)], -1)


# --------------------------------------------------------------------------------------
# display-list executor for aligned lists (compositing.md 6.1): the reference the GPU
# executor (Tsuki.Render) is tested against
# --------------------------------------------------------------------------------------

def select_shader(textured: bool, mode: int, has_transition: bool) -> str:
    """SelectShader 0xF0E70 (section 3) for the shaders the game reaches: "basic2d" (PS #3,
    untextured quads), "basic2dtex" (PS #2), "transition2dtex" (PS #0: the bound texture carries
    transition parameters, mode 0 only) or "blend" (modes 1..25 except 3)."""
    if not textured:
        return "basic2d"
    if mode == 0:
        return "transition2dtex" if has_transition else "basic2dtex"
    if mode == 3 or mode > 25:
        return "basic2dtex"
    return "blend"


def shade(dst: np.ndarray, S: Optional[np.ndarray], C: np.ndarray, word: int,
          state: Optional[int] = None, transition: Optional[Sequence[float]] = None,
          export: str = "rtz16") -> np.ndarray:
    """One quad on the pixels it covers: dst = the target before the quad (also the backdrop D),
    S = the sampled texels (None for an untextured fill), C = vertex colour (float RGBA).
    Shader per ``select_shader``, the export model ``export`` (2.6), then blend state ``state``
    (default: the word's).  P2 follows the state actually used (0xF147C tests surface +0x518)."""
    eff = effect(word)
    st = eff.blend_state if state is None else int(state)
    mode = eff.shader_mode
    kind = select_shader(S is not None, mode, transition is not None)
    C = f32(C)
    dst = f32(dst)
    with np.errstate(all="ignore"):
        if kind == "basic2d":
            out = np.broadcast_to(ps_basic_2d(C), dst.shape).astype(np.float32)
        elif kind == "basic2dtex":
            out = ps_basic_2dtex(S, C)
        elif kind == "transition2dtex":
            out = ps_transition_2dtex(S, C, transition)
        else:
            p2 = 0.0 if (mode in (5, 7, 9, 10) and st == 0) else 1.0
            out = ps_blend(mode, S, dst, C, (1.0 - eff.strength, eff.strength, p2, 0.0))
        return apply_blend_state(EXPORTS[export](out), dst, st)


def execute_list(ops: Sequence[dict], textures: Dict[str, np.ndarray], width: int, height: int,
                 export: str = "rtz16", target: Optional[np.ndarray] = None,
                 captures: Optional[Dict[int, np.ndarray]] = None
                 ) -> Tuple[np.ndarray, Dict[int, np.ndarray]]:
    """Run an *aligned* display list on a ``width`` x ``height`` RGBA8 target and return the
    target (uint8, rows top-down) and the capture surfaces (6, 6.1).  ``captures`` = the capture
    surfaces left by earlier lists (they persist across frames, as nexl_Capture does: a _FADS start
    captures in one frame and later frames read it); it is updated in place when given.  A surface
    never captured reads as zeros (the GPU executor creates it cleared to 0).

    Every pass is stored to UNORM8 (``to_u8``) before the next one reads the target, as on the
    GPU.  Aligned means: each quad is an axis-aligned rectangle at integer pixel positions whose
    texels map 1:1 onto the pixels (nearest sampling, no scaling), which is what the reference
    can model exactly.  Ops (JSON-compatible dicts):

    * ``{"op": "clear", "rgba": [r, g, b, a]}`` (bytes);
    * ``{"op": "load", "tex": name}``: the target := texture ``name`` (same size; test setup);
    * ``{"op": "quad", "tex": name | None, "dst": [x, y, w, h], "src": [sx, sy],
      "color": [r, g, b, a], "word": w, "state": s?, "transition": [lo, hi, inv]?}``: a
      textured quad (record type 1) or, with ``tex`` None, an untextured fill (type 7);
      ``state`` defaults to the word's blend state (5.1); ``transition`` = the texture's
      transition parameters (2.2, 7.4), which select PS #0 for mode 0; ``clip`` = [x, y, w, h]
      (optional) = the scissor rectangle in target pixels: only pixels inside it are drawn;
    * ``{"op": "capture", "surface": n}``: copy the target into capture surface n (type 6).

    Texture names ``"capture:n"`` read capture surface n (0..15).  A quad whose vertex alpha is 0
    is skipped (surface flag k = 4, 0xF0AE0, section 3)."""
    rt = (np.zeros((height, width, 4), np.uint8) if target is None
          else np.array(target, dtype=np.uint8, copy=True))
    caps: Dict[int, np.ndarray] = {} if captures is None else captures

    def tex(name: str) -> np.ndarray:
        if name.startswith("capture:"):
            n = int(name[8:])
            if not 0 <= n < 16:
                raise ValueError(f"capture surface {n} is not 0..15")
            if n not in caps:
                caps[n] = np.zeros((height, width, 4), np.uint8)   # never captured: cleared to 0
            return caps[n]
        return textures[name]

    for op in ops:
        kind = op["op"]
        if kind == "clear":
            rt[...] = np.asarray(op["rgba"], np.uint8)
        elif kind == "load":
            rt[...] = tex(op["tex"])
        elif kind == "capture":
            caps[int(op["surface"])] = rt.copy()
        elif kind == "quad":
            if int(op["color"][3]) <= 0:
                continue                                   # culled (0xF0AE0)
            x, y, w, h = (int(v) for v in op["dst"])
            x0, y0, x1, y1 = max(0, x), max(0, y), min(width, x + w), min(height, y + h)
            if op.get("clip") is not None:
                cx, cy, cw, ch = (int(v) for v in op["clip"])
                x0, y0, x1, y1 = max(x0, cx), max(y0, cy), min(x1, cx + cw), min(y1, cy + ch)
            if x1 <= x0 or y1 <= y0:
                continue
            S = None
            if op.get("tex") is not None:
                sx, sy = (int(v) for v in op.get("src", (0, 0)))
                img = tex(op["tex"])
                S = to_float(img[sy + y0 - y:sy + y1 - y, sx + x0 - x:sx + x1 - x])
                if S.shape[:2] != (y1 - y0, x1 - x0):
                    raise ValueError(f"quad {op} reads outside its texture {img.shape}")
            C = f32(op["color"]) / F(255)
            D = to_float(rt[y0:y1, x0:x1])
            out = shade(D, S, C, int(op.get("word", 0)), op.get("state"), op.get("transition"), export)
            rt[y0:y1, x0:x1] = to_u8(out)
        else:
            raise ValueError(f"unknown display-list op {kind!r}")
    return rt, caps


def _run_cli(folder: str) -> int:
    """``python -m tsuki.compose run <folder>``: executes ``<folder>/lists.json`` and writes the
    final target of every list, concatenated, to ``<folder>/ref.rgba`` (the GPU executor's
    differential tests write the inputs; compositing.md 6.1).  lists.json:
    {"width", "height", "export", "textures": {name: {"file", "width", "height"}},
     "lists": [{"id", "ops": [...]}]}; texture files are raw RGBA8, rows top-down."""
    import json
    import time
    from pathlib import Path

    root = Path(folder)
    doc = json.loads((root / "lists.json").read_text(encoding="utf-8"))
    w, h = int(doc["width"]), int(doc["height"])
    textures = {name: np.fromfile(root / t["file"], np.uint8).reshape(int(t["height"]), int(t["width"]), 4)
                for name, t in doc["textures"].items()}
    t0 = time.perf_counter()
    with open(root / "ref.rgba", "wb") as f:
        for lst in doc["lists"]:
            rt, _caps = execute_list(lst["ops"], textures, w, h, doc.get("export", "rtz16"))
            f.write(np.ascontiguousarray(rt).tobytes())
    print(f"compose: {len(doc['lists'])} lists of {w} x {h} in {time.perf_counter() - t0:.2f} s "
          f"(export {doc.get('export', 'rtz16')}) -> {root / 'ref.rgba'}")
    return 0


if __name__ == "__main__":
    import sys
    if len(sys.argv) == 3 and sys.argv[1] == "run":
        sys.exit(_run_cli(sys.argv[2]))
    print("usage: python -m tsuki.compose run <folder>", file=sys.stderr)
    sys.exit(2)
