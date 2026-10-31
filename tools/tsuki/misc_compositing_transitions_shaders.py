"""Engine-side tables and CPU effects of the compositing / transition pipeline, read from the user's
eboot.bin (CUSA47999 v01.01).  The pixel-shader math lives in tsuki.compose (numpy, no game data);
this module adds what needs the eboot:

* ``read_stec_tables`` / ``stec_apply_bgra`` / ``stec_apply_rgba``: the CPU recolour 0xD36F0 that
  ``_STEC`` (colour effect id 1..81), ``_SNX2`` (brightness) and ``_SNX3`` (contrast) apply to a
  picture when its texture is built (bit-exact against the emulated engine function, see
  scratch/misc_compositing_transitions_shaders/stec_emu.py);
* ``read_engine_tables``: the blend-state table, the draw-effect tables, the STBR->effect table of
  the masked-layer path, the 0x100-flag bit set and the unused mask-name table, so a port (and our
  tests) can check tsuki.compose's copies against the binary;
* ``render_samples``: the acceptance renders in samples/compose/ (rule wipe, STBR scenes, sepia,
  STEC).

Docs: docs/formats/compositing.md (short pointer: docs/formats/misc_compositing_transitions_shaders.md).

CLI:
    python -m tsuki.misc_compositing_transitions_shaders tables [--out FILE.json]
    python -m tsuki.misc_compositing_transitions_shaders render [--out DIR]
"""
from __future__ import annotations

import json
import struct
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Tuple, Union

import numpy as np

__all__ = [
    "StecTables", "read_stec_tables", "stec_luts", "stec_apply_bgra", "stec_apply_rgba",
    "stec_test_pattern",
    "STEC_VA", "EngineTables", "read_engine_tables", "render_samples", "rule_name",
    "MASK_TOP_NAME",
]

PathLike = Union[str, Path]

# ---------------------------------------------------------------------------------------------
# _STEC / _SNX2 / _SNX3 CPU recolour (VA 0xD36F0)
# ---------------------------------------------------------------------------------------------

STEC_VA = {
    "function": 0xD36F0,          # stec(pixels BGRA8, count, id, bright i16, contrast i8, R, G, B)
    "flags": 0x2D5A78,            # i32[81]: 0x100 per-channel LUT, 0x200 luma -> LUT, 0x400 fill
    "lut_p0": 0x42C590,           # u8*[81] (relocated pointers): LUT for byte 0 (blue)
    "lut_p1": 0x42C308,           # u8*[81]: LUT for byte 1 (green)
    "lut_p2": 0x42C080,           # u8*[81]: LUT for byte 2 (red)
    "w_p0": 0x2D5D00,             # i32[81]: luma weight of byte 0
    "w_p2": 0x2D5BBC,             # i32[81]: luma weight of byte 2 (byte 1 weight is 548 = 0x224)
    "luma_k": 0x2D4008,           # f32[2] = (382.5, 765.0)
}
_DEFAULT_W = (161, 56)            # weights used for an id outside 1..81 (no effect anyway)


@dataclass
class StecTables:
    flags: np.ndarray             # (81,) int32
    w0: np.ndarray                # (81,) int32, weight of byte 0 (B)
    w2: np.ndarray                # (81,) int32, weight of byte 2 (R)
    luts: np.ndarray              # (81, 3, 256) uint8, [id-1][byte][value]; zeros when absent
    k0: float = 382.5
    k1: float = 765.0


def read_stec_tables(eboot: PathLike) -> StecTables:
    """Read the 81-entry colour-effect tables from eboot.bin (relocated pointer arrays in RELRO)."""
    from eboot.selfelf import Eboot
    eb = Eboot.open(Path(eboot))
    try:
        ptr: Dict[int, int] = {}
        for r in eb.rela():
            if r.type == 8:                                   # R_X86_64_RELATIVE
                ptr[r.offset] = r.addend
        flags = np.array(struct.unpack("<81i", eb.read_va(STEC_VA["flags"], 81 * 4)), np.int32)
        w0 = np.array(struct.unpack("<81i", eb.read_va(STEC_VA["w_p0"], 81 * 4)), np.int32)
        w2 = np.array(struct.unpack("<81i", eb.read_va(STEC_VA["w_p2"], 81 * 4)), np.int32)
        k0, k1 = struct.unpack("<2f", eb.read_va(STEC_VA["luma_k"], 8))
        luts = np.zeros((81, 3, 256), np.uint8)
        for c, key in enumerate(("lut_p0", "lut_p1", "lut_p2")):
            for k in range(81):
                p = ptr.get(STEC_VA[key] + 8 * k)
                if p:
                    luts[k, c] = np.frombuffer(eb.read_va(p, 256), np.uint8)
        return StecTables(flags, w0, w2, luts, float(k0), float(k1))
    finally:
        eb.close()


def stec_luts(bright: int, contrast: int) -> Tuple[np.ndarray, np.ndarray]:
    """The two 256-entry tables 0xD36F0 builds first [E VA 0xD3756..0xD3C02]:
    LUT1 (VA 0x1DBD200, brightness, ``_SNX2``): clamp(i + bright, 0, 255);
    LUT2 (VA 0x1DBD100, contrast, ``_SNX3``, signed byte c):
      c > 0: 0 below c, (i - c) * 255 // (255 - 2c) up to 255 - c, 255 above;
      c < 0: trunc(i * (255 + 2c) / 255) - c, stored as the low byte."""
    i = np.arange(256, dtype=np.int64)
    b = int(np.int16(bright))
    lut1 = np.clip(i + b, 0, 255).astype(np.uint8) if b else i.astype(np.uint8)
    c = int(np.int8(contrast))
    if c > 0:
        lut2 = np.empty(256, np.int64)
        lut2[:c] = 0
        hi = 255 - c
        j = np.arange(max(0, hi - c))
        lut2[c:c + len(j)] = (255 * j) // (255 - 2 * c)
        lut2[c + len(j):] = 255
    elif c < 0:
        x = i * (255 + 2 * c)
        q = np.sign(x) * (np.abs(x) // 255)                 # signed division, truncation toward 0
        lut2 = (q - c) & 0xFF
    else:
        lut2 = i
    return lut1, lut2.astype(np.uint8)


def stec_apply_bgra(px: np.ndarray, effect: int, bright: int = 0, contrast: int = 0,
                    colour: Sequence[int] = (255, 255, 255), tabs: Optional[StecTables] = None
                    ) -> np.ndarray:
    """Bit-exact model of 0xD36F0 on a (..., 4) uint8 array in the engine's byte order B, G, R, A.

    ``effect`` = the layer's _STEC id (record +0x4A), ``bright`` = _SNX2 value, ``contrast`` =
    _SNX3 value, ``colour`` = (R, G, B) of _STEC(layer, 1, 0xRRGGBB) (white otherwise).
    Byte 3 (alpha) is never changed."""
    if tabs is None:
        raise ValueError("pass the tables from read_stec_tables(eboot)")
    out = np.array(px, dtype=np.uint8, copy=True)
    flat = out.reshape(-1, 4)
    e = int(effect)
    if 1 <= e <= 81:
        k = e - 1
        flags, w0, w2, luts = int(tabs.flags[k]), int(tabs.w0[k]), int(tabs.w2[k]), tabs.luts[k]
    else:
        flags, (w0, w2), luts = 0, _DEFAULT_W, np.zeros((3, 256), np.uint8)
    lut1, lut2 = stec_luts(bright, contrast)
    mode = flags | (0x10 if int(np.int16(bright)) else 0) | (1 if int(np.int8(contrast)) else 0)
    sub = mode & 0xFF
    base = mode & ~0xFF
    if base == 0 and sub not in (1, 0x10, 0x11):
        return out
    if base not in (0, 0x100, 0x200, 0x400) or sub not in (0, 1, 0x10, 0x11):
        return out                                           # 0xD3C3B: other values -> no change
    p = flat[:, :3].astype(np.int64)
    if base == 0x100:
        p = np.stack([luts[c][p[:, c]] for c in range(3)], 1).astype(np.int64)
    elif base == 0x200:
        s = p[:, 0] * w0 + p[:, 1] * 0x224 + p[:, 2] * w2
        y = ((s.astype(np.float32) + np.float32(tabs.k0)) / np.float32(tabs.k1)).astype(np.int64)
        p = np.stack([luts[c][y] for c in range(3)], 1).astype(np.int64)
    elif base == 0x400:
        r, g, b = (int(v) & 0xFF for v in colour)
        p = np.broadcast_to(np.array([b, g, r], np.int64), p.shape).copy()
    if sub & 0x10:
        p = lut1[p].astype(np.int64)
    if sub & 1:
        p = lut2[p].astype(np.int64)
    flat[:, :3] = p.astype(np.uint8)
    return out


def stec_test_pattern() -> np.ndarray:
    """3072 BGRA pixels: grey / single-channel ramps and 2048 random colours (seed 7); the
    pattern used for samples/misc_compositing_transitions_shaders/stec_golden.json."""
    rng = np.random.default_rng(7)
    i = np.arange(256, dtype=np.uint8)
    z = np.zeros(256, np.uint8)
    rows = [np.stack([i, i, i, i], 1), np.stack([i, z, z, i], 1), np.stack([z, i, z, i], 1),
            np.stack([z, z, i, i], 1), rng.integers(0, 256, (2048, 4), dtype=np.uint8)]
    return np.concatenate(rows).astype(np.uint8)


def stec_apply_rgba(rgba: np.ndarray, effect: int, bright: int = 0, contrast: int = 0,
                    colour: Sequence[int] = (255, 255, 255), tabs: Optional[StecTables] = None
                    ) -> np.ndarray:
    """``stec_apply_bgra`` for an RGBA array as tsuki.image decodes it (bytes swapped in and out,
    as the texture builder 0xD6900 does around the call at 0xD6B9C / 0xD7192)."""
    bgra = np.asarray(rgba, np.uint8)[..., [2, 1, 0, 3]]
    return stec_apply_bgra(bgra, effect, bright, contrast, colour, tabs)[..., [2, 1, 0, 3]]


# ---------------------------------------------------------------------------------------------
# engine tables used by tsuki.compose
# ---------------------------------------------------------------------------------------------

@dataclass
class EngineTables:
    blend_states: List[Tuple[int, int, int, int, int, int, int]]   # 0x2D8030, 14 x 28 bytes
    effect_mode: List[int]           # 0x2D5978 (51 bytes, index e - 1)
    effect_strength: List[float]     # 0x2D59AC (51 floats, index e - 1)
    effect_blend_state: List[int]    # 0x2D55E0 (68 dwords, index e)
    stbr_to_effect: List[int]        # 0x2D5860 (61 dwords, index mode - 1), used by 0xD24D0
    flag_effects: List[int]          # bits of the 64-bit immediate at 0xD9FB9
    mask_names: List[str]            # 0x869B30, 32-byte records (dead: only reader 0xA3530 is unreferenced)
    sepia_clamp: Tuple[float, ...]   # 0x2DAA50 (lo), 0x2DAA60 (hi), 0x2DAA70 (blend max)
    surface_defaults: Dict[str, float]
    capture15_types: List[int]       # 0x6B910 table 0x209FF0: types that also capture into nexl_Capture[15]
    transition_draw: Dict[int, int]  # 0x67820 table 0x209C50: _FADS type -> draw case VA (1..70)


MASK_TOP_NAME = "mask_top.mzp"       # VA 0x20A11A; rule n = find(MASK_TOP_NAME) + n (0x6B460)


def rule_name(n: int) -> str:
    """allpac3 picture used by _FADS(2, ms, n): NAM index find('mask_top.mzp') + n = RULE_nn."""
    if not 1 <= n <= 49:
        raise ValueError("rule ids are 1..49 (0 = no mask)")
    return f"RULE_{n:02d}.MZP"


def read_engine_tables(eboot: PathLike) -> EngineTables:
    from eboot.selfelf import Eboot
    eb = Eboot.open(Path(eboot))
    try:
        raw = eb.read_va(0x2D8030, 28 * 14)
        states = [tuple(struct.unpack_from("<6IB", raw, 28 * i)) for i in range(14)]
        mode = list(eb.read_va(0x2D5978, 51))
        strength = [round(x, 6) for x in struct.unpack("<51f", eb.read_va(0x2D59AC, 51 * 4))]
        bstate = list(struct.unpack("<68i", eb.read_va(0x2D55E0, 68 * 4)))
        s2e = list(struct.unpack("<61i", eb.read_va(0x2D5860, 61 * 4)))
        # movabs rdx, imm64 at VA 0xD9FB9 (48 BA imm64)
        code = eb.read_va(0xD9FB9, 10)
        if code[:2] != b"\x48\xba":
            raise ValueError("unexpected code at 0xD9FB9 (not app 01.01?)")
        imm = struct.unpack_from("<Q", code, 2)[0]
        flag = [i for i in range(64) if (imm >> i) & 1]
        names = []
        for i in range(96):
            rec = eb.read_va(0x869B30 + 32 * i, 32)
            s = rec.split(b"\0")[0]
            if not s:
                break
            names.append(s.decode("latin1"))
        sep = struct.unpack("<4f", eb.read_va(0x2DAA50, 16)) + struct.unpack(
            "<4f", eb.read_va(0x2DAA60, 16)) + struct.unpack("<f", eb.read_va(0x2DAA70, 4))
        d: Dict[str, float] = {}
        d["texture_size"] = struct.unpack("<f", eb.read_va(0x2D7BA0, 4))[0]
        # 0x6BA75: lea eax, [r14 - 2]; cmp eax, 0x44; ja 0x6BA9D; jump table 0x209FF0 whose
        # 0x6BA8E entries call 0xDCD20 (capture into nexl_Capture[15]) and set 0xE20CDC = 1
        if eb.read_va(0x6BA75, 10) != bytes.fromhex("418d46fe83f844771f48"):
            raise ValueError("unexpected code at 0x6BA75 (not app 01.01?)")
        jt = struct.unpack("<69i", eb.read_va(0x209FF0, 69 * 4))
        cap15 = [t + 2 for t, rel in enumerate(jt) if 0x209FF0 + rel == 0x6BA8E]
        # 0x6787A: dec eax; cmp eax, 0x45; ja 0x67A30; table 0x209C50 (type 0, > 70: 0x67A30)
        jd = struct.unpack("<70i", eb.read_va(0x209C50, 70 * 4))
        draw = {t + 1: 0x209C50 + rel for t, rel in enumerate(jd)}
        return EngineTables([tuple(int(v) for v in s) for s in states], mode, strength, bstate,
                            s2e, flag, names, tuple(round(v, 6) for v in sep), d, cap15, draw)
    finally:
        eb.close()


# ---------------------------------------------------------------------------------------------
# acceptance renders
# ---------------------------------------------------------------------------------------------

def render_samples(out_dir: PathLike, game: Optional[PathLike] = None) -> List[Path]:
    """Write the acceptance renders (docs/formats/compositing.md section 11)."""
    from tsuki import misc_compositing_transitions_shaders_render as R
    return R.render_all(Path(out_dir), game)


def _main(argv: Sequence[str]) -> int:
    import argparse
    from eboot import eboot_path
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    t = sub.add_parser("tables")
    t.add_argument("--out")
    r = sub.add_parser("render")
    r.add_argument("--out", default="samples/compose")
    a = ap.parse_args(list(argv))
    if a.cmd == "tables":
        et = read_engine_tables(eboot_path())
        st = read_stec_tables(eboot_path())
        doc = {k: v for k, v in et.__dict__.items()}
        doc["stec"] = [{"id": k + 1, "flags": int(st.flags[k]), "w_b": int(st.w0[k]),
                        "w_r": int(st.w2[k]),
                        "lut_b": st.luts[k, 0].tolist(), "lut_g": st.luts[k, 1].tolist(),
                        "lut_r": st.luts[k, 2].tolist()} for k in range(81)]
        text = json.dumps(doc, separators=(",", ":"))
        if a.out:
            Path(a.out).write_text(text)
        else:
            print(text[:4000])
        return 0
    for p in render_samples(a.out):
        print(p)
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
