"""Scenario layers of TSUKIHIME (PS4 CUSA47999 v01.01): per-layer state, the layer commands,
the ``_FADS`` commit, tweens and motions, and the 2D projection of a layer.

Engine-faithful model of the HuneX scenario renderer.  Reference document:
``docs/formats/layers.md``.  Everything here was derived from the eboot code (addresses in
the comments) and checked against the engine's own code run under emulation
(``tsuki.misc_layer_state_and_motions_emu``; the checks are in
``scratch/misc_layer_state_and_motions/xf`` and ``tools/tests``).

Pipeline
--------
script statement --(handler, :class:`SceneModel`)--> ``scene_state`` (VA 0x16500C0, 0x1A08
bytes: per-layer arrays, :data:`SCENE_ARRAYS`)
   ``_FADS`` --(commit VA 0x7DD10, :func:`commit`)--> render array B (VA 0xDE2650, 40 x 0x2C8)
   per-frame update (VA 0x57980) moves B to the drawn array A (VA 0xDDB5E8) and runs tweens,
   motions (:func:`apply_motion` = VA 0xBE4C0, :func:`motion_value`) and shakes; ``_STMA``
   starts a motion (:func:`motion_record` + ``apply_motion(dt=0)``), ``_SGMA`` edits the
   keyframe table from the drawn record (:func:`edit_motion`)
   draw (VA 0x56DE0 -> 0x51060 -> 0xEB780 -> 0xDD6D0) --(:func:`layer_matrix`)--> 2x3 affine
   map from picture pixels to the 640 x 362.667 scene canvas, x 3 / x 2.978 to 1920 x 1080.
"""
from __future__ import annotations

import math
import struct
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Optional, Sequence, Union

__all__ = [
    "SCENE_VA", "SCENE_LEN", "NUM_LAYERS", "RECORD_SIZE", "ARRAY_A", "ARRAY_B", "ARRAY_C",
    "SceneArray", "SCENE_ARRAYS", "SCENE_GLOBALS", "SceneState", "SceneModel", "LAYER_COMMANDS",
    "script_x", "script_y", "wrap_angle", "ms_to_frames", "stec_mode",
    "ease", "EASE_FAMILIES", "UNSET", "HOLD", "Motion", "MotionTable", "motion_value", "KEY_FIELDS",
    "FIELD_MASKS", "ANGLE_BIT", "FOUND_FIELDS", "motion_frames", "motion_time_ms", "motion_keytime",
    "motion_found", "motion_record", "motion_clear", "motion_copy", "apply_motion", "load_motion_loops",
    "PictureNames", "colour_block", "record_bytes", "edit_motion", "EASE_EXACT",
    "RENDER_FIELDS", "RenderRecord", "commit", "Affine", "CANVAS", "PX_X", "PX_Y", "sprite_matrix",
    "canvas_to_frame", "layer_world", "layer_zoom", "layer_rot", "layer_matrix", "frame_matrix",
    "anchor_residual",
    "zoom_tween", "tween_start", "tween_state", "tween_value", "compose_frame", "steady_record",
]

SCENE_VA = 0x16500C0
SCENE_LEN = 0x1A08
NUM_LAYERS = 40
ARRAY_A, ARRAY_B, ARRAY_C = 0xDDB5E8, 0xDE2650, 0xDE96B8
RECORD_SIZE = 0x2C8


# ---------------------------------------------------------------------------
# 1. scene_state layout (VA 0x16500C0).  Every per-layer field is an array of 40 entries.
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class SceneArray:
    name: str
    off: int            # offset of entry 0 in scene_state
    fmt: str            # struct format of one entry (little endian)
    default: object     # value after STCC / at scene start
    writers: str        # commands that write it (handler evidence in layers.md)
    commit: str         # where the commit (VA 0x7DD10) puts it in the render record

    @property
    def stride(self) -> int:
        return struct.calcsize("<" + self.fmt)

    def addr(self, layer: int) -> int:
        return self.off + self.stride * layer


SCENE_ARRAYS: tuple[SceneArray, ...] = (
    SceneArray("pic", 0x004, "h", -1, "STCH SCH2 STCC", "+0x18 i32 (0x49F60: -1 if >= 0x59D8)"),
    SceneArray("blend", 0x054, "h", 0, "STBR", "+0x1C (0x49F90)"),
    SceneArray("flags", 0x0A4, "B", 0, "STBR a3=1 -> |0x80, SACL -> 0x40", "+0x1E"),
    SceneArray("fx_mode", 0x0CC, "b", 0, "STEC (and STCH from the global word 0x164B97C)", "+0x1F (0x49FB0)"),
    SceneArray("fx_a", 0x0F4, "h", 0, "SNX2", "+0x20 (0x49FB0)"),
    SceneArray("fx_b", 0x144, "b", 0, "SNX3", "+0x22 (0x49FB0)"),
    SceneArray("x", 0x16C, "h", 0, "STCH SCH2 STCL", "+0x30 f32 (0x49FD0)"),
    SceneArray("y", 0x1BC, "h", 0, "STCH SCH2 STCL", "+0x34 f32 (0x49FD0)"),
    SceneArray("alpha", 0x20C, "h", 255, "STCH SCH2 STGS", "+0x2C f32 = alpha/255 (0x4A010)"),
    SceneArray("state", 0x25C, "b", 0, "STCH SCH2 (0), engine", "+0x24 i32 (0x4A040)"),
    SceneArray("u284", 0x284, "B", 0, "copied by STCH src", "-"),
    SceneArray("u2ac", 0x2AC, "B", 0, "copied by STCH src", "-"),
    SceneArray("u2d4", 0x2D4, "B", 0, "copied by STCH src", "-"),
    SceneArray("z", 0x2FC, "h", 0, "STCH SCH2 STCP", "draw order (0x4A180 sorts)"),
    SceneArray("rot", 0x350, "q", 0, "STRT STPV(a3) STCH(reset 0)", "+0x58 i64 (0x4B270)"),
    SceneArray("scale", 0x490, "h", 1000, "STCH SCH2", "+0x70 (0x4B1A0)"),
    SceneArray("anchor_x", 0x4E0, "h", 0, "STCH SCH2 STPV", "+0x76 (0x4B1A0)"),
    SceneArray("anchor_y", 0x530, "h", 0, "STCH SCH2 STPV", "+0x78 (0x4B1A0)"),
    *(SceneArray(f"u{0x580 + 0x50 * k:x}", 0x580 + 0x50 * k, "h", 0, "copied by STCH src", "-")
      for k in range(10)),
    SceneArray("zoom_x", 0x8A0, "h", 1000, "STZ4 a3; STCH SCH2 reset to 1000", "A +0xAA via 0x49930"),
    SceneArray("zoom_y", 0x8F0, "h", 1000, "STZ4 a4; STCH SCH2 reset to 1000", "A +0xAC via 0x49930"),
    SceneArray("zoom_pivot", 0x940, "b", 0, "STZ4 a7; STCH SCH2 reset to 0", "A +0xB0 via 0x49930"),
    SceneArray("mask", 0x968, "h", -1, "STCH a11", "+0x120 i32"),
    SceneArray("sttr", 0x9B8, "h", -1, "STTR", "+0x12C i32"),
    SceneArray("parent", 0xA08, "i", -1, "SBAS", "+0x128 i32"),
    SceneArray("soxy_x", 0xAA8, "b", 0, "SOXY a1", "+0x130"),
    SceneArray("soxy_y", 0xAD0, "b", 0, "SOXY a2", "+0x131"),
    SceneArray("crop", 0xAF8, "4h", (0, 0, 0, 0), "STCH a12..a15", "+0x132 4 x i16"),
    SceneArray("a16", 0xC38, "H", 0, "STCH a16", "+0x13A"),
    SceneArray("a17", 0xC88, "B", 0, "STCH a17", "+0x13C"),
    SceneArray("angle", 0xCB0, "h", 0, "STCH a19, STRR", "+0x15C"),
    SceneArray("motion", 0xD00, "24s", bytes(24), "STMA SPMA", "+0x160 (0xBA8E0)"),
    SceneArray("shake_kind", 0x10C0, "2B", (0, 0), "SQK2 (slot 0) SHK2/SSW2 (slot 1)", "+0x178"),
    SceneArray("shake_fade", 0x1110, "2h", (0, 0), "SQK2 SHK2 SSW2 a5", "+0x17A"),
    SceneArray("shake_time", 0x11B0, "2h", (0, 0), "SQK2 SHK2 SSW2 a4", "+0x17E"),
    SceneArray("shake_par", 0x1250, "12h", (0,) * 12, "SQK2 SHK2 SSW2", "+0x182 / +0x18E"),
    SceneArray("stra", 0x1610, "h", 0, "STRA", "+0x2C8"),
    SceneArray("stns", 0x1660, "B", 0, "STNS", "+0x1D0"),
    SceneArray("stns_b", 0x1688, "B", 0, "STNS", "+0x1D1"),
    SceneArray("ext", 0x16B0, "18s", bytes(18), "STCH STPV STEC", "+0x50 (off), +0x1F0 colour, +0x2A8"),
    SceneArray("u19ab", 0x19AB, "B", 0, "copied by STCH src", "-"),
    SceneArray("u19d3", 0x19D3, "B", 0, "copied by STCH src", "-"),
)
_ARR = {a.name: a for a in SCENE_ARRAYS}

# 18-byte "ext" record per layer (+0x16B0): +0 u8 (0xE98D0), +1 u8, +2 i16 off_x, +4 i16 off_y,
# +6..+0xD colour block (0xF760 -> render +0x1F0; bytes +6..+8 = R, G, B of STEC mode 1),
# +0xE..+0x11 (0xF2F0 -> render +0x2A8).
EXT_OFF_X, EXT_OFF_Y, EXT_COLOUR = 2, 4, 6

SCENE_GLOBALS = {
    0x0000: "i16 scene-level picture id (pic seen bit + 0x49C20 at commit)",
    0x0002: "i16 (0x49C30 at commit)",
    0x1980: "i16 (0x1651A40) index into the 12-byte table VA 0x873050 -> 0x493E0 at commit",
    0x1986: "i16 x2 (0x1651A46/48) -> 0x49620 at commit",
    0x198A: "i16 x3 (0x1651A4A/4C/4E) -> 0x495F0 at commit",
    0x19AA: "i8 (0x1651A6A) -> 0x56CE0 at commit",
    0x19FB: "end of the per-layer byte arrays 0x19AB / 0x19D3",
}


def script_x(x: int) -> int:
    """STCH x -> engine units: ``trunc(x * 640 / 480)`` (VA 0x82941, C division)."""
    return _cdiv(x * 640, 480)


def script_y(y: int) -> int:
    """STCH y -> engine units: ``trunc(y * 480 / 360)`` (VA 0x829A6)."""
    return _cdiv(y * 480, 360)


def _cdiv(a: int, b: int) -> int:
    q = abs(a) // abs(b)
    return q if (a >= 0) == (b > 0) else -q


def _i16(v: int) -> int:
    v &= 0xFFFF
    return v - 0x10000 if v & 0x8000 else v


def _i32(v: int) -> int:
    v &= 0xFFFFFFFF
    return v - 0x100000000 if v & 0x80000000 else v


def wrap_angle(v: int) -> int:
    """STCH a19 / STRR: ``r = v % 36000`` (C remainder) moved into [-18000, 18000]
    (VA 0x821DF..0x82215, 0x845F2..0x8462E)."""
    r = abs(v) % 36000 * (1 if v >= 0 else -1)
    if r <= -18001:
        r += 36000
    elif r >= 18001:
        r -= 36000
    return r


def ms_to_frames(ms: int) -> int:
    """Durations of SQK2/SHK2/SSW2/STRA: ``trunc(3 * ms / 50)`` 60 Hz frames (VA 0x846B6)."""
    return _cdiv(3 * ms, 50)


class SceneState:
    """The 0x1A08-byte script-side layer state, byte-compatible with the engine block."""

    def __init__(self, data: Optional[bytes] = None) -> None:
        if data is None:
            self.b = bytearray(SCENE_LEN)
            for l in range(NUM_LAYERS):
                self.reset_layer(l)
        else:
            if len(data) != SCENE_LEN:
                raise ValueError("scene_state is 0x1A08 bytes")
            self.b = bytearray(data)

    def get(self, name: str, layer: int) -> Any:
        a = _ARR[name]
        v = struct.unpack_from("<" + a.fmt, self.b, a.addr(layer))
        return v[0] if len(v) == 1 else v

    def set(self, name: str, layer: int, value: Any) -> None:
        a = _ARR[name]
        if isinstance(value, (tuple, list)):
            struct.pack_into("<" + a.fmt, self.b, a.addr(layer), *value)
        elif isinstance(value, (bytes, bytearray)):
            struct.pack_into("<" + a.fmt, self.b, a.addr(layer), bytes(value))
        else:
            c = a.fmt[-1]
            bits = {"b": 8, "B": 8, "h": 16, "H": 16, "i": 32, "q": 64}[c]
            v = int(value) & ((1 << bits) - 1)
            if c.islower() and v >> (bits - 1):
                v -= 1 << bits
            struct.pack_into("<" + a.fmt, self.b, a.addr(layer), v)

    def ext(self, layer: int) -> bytearray:
        a = _ARR["ext"]
        return self.b[a.addr(layer):a.addr(layer) + 18]

    def set_ext(self, layer: int, pos: int, fmt: str, *vals: Any) -> None:
        struct.pack_into("<" + fmt, self.b, _ARR["ext"].addr(layer) + pos, *vals)

    def get_ext(self, layer: int, pos: int, fmt: str) -> int:
        return int(struct.unpack_from("<" + fmt, self.b, _ARR["ext"].addr(layer) + pos)[0])

    def reset_layer(self, layer: int) -> None:
        for a in SCENE_ARRAYS:
            self.set(a.name, layer, a.default)

    def copy_layer(self, src: int, dst: int, names: Iterable[str]) -> None:
        for n in names:
            a = _ARR[n]
            s, d = a.addr(src), a.addr(dst)
            self.b[d:d + a.stride] = self.b[s:s + a.stride]

    def layer_dict(self, layer: int) -> dict[str, Any]:
        return {a.name: self.get(a.name, layer) for a in SCENE_ARRAYS}

    def diff(self, other: "SceneState") -> list[tuple[str, int, object, object]]:
        """(array or 'global+off', layer, self value, other value) for every difference."""
        out: list[tuple[str, int, object, object]] = []
        for a in SCENE_ARRAYS:
            for l in range(NUM_LAYERS):
                x, y = self.get(a.name, l), other.get(a.name, l)
                if x != y:
                    out.append((a.name, l, x, y))
        covered = bytearray(SCENE_LEN)
        for a in SCENE_ARRAYS:
            covered[a.off:a.off + a.stride * NUM_LAYERS] = b"\1" * (a.stride * NUM_LAYERS)
        for i in range(SCENE_LEN):
            if not covered[i] and self.b[i] != other.b[i]:
                out.append((f"global+{i:#x}", -1, self.b[i], other.b[i]))
        return out


# ---------------------------------------------------------------------------
# 2. Command model: script statement -> scene_state writes (handler VAs in LAYER_COMMANDS).
#    Only the script-side block is modelled here; the immediate ("live") effects some
#    handlers also have on the drawn record (tweens, STMA keyframe 0, STPV anchor, ...) are
#    returned as LiveOp tuples for the frame model.
# ---------------------------------------------------------------------------
LAYER_COMMANDS: dict[str, str] = {
    "STCH": "0x99AB7 / 0x81760 (edi=1): reset layer, set picture, place",
    "SCH2": "0x98226 / 0x81760 (edi=0): change picture, keep the rest",
    "STCC": "0x97EED / 0x82D30: clear layer (-1 = all)",
    "STCP": "0x99B03 / 0x83850: z (draw order) of the layer",
    "STBR": "0x990C2 / 0x830D0: blend mode; a3=1 sets flag 0x80",
    "STEC": "0x9991B / 0x10390: colour-effect mode (+ colour for mode 1)",
    "SNX2": "0x98289 / 0x10610: colour-effect parameter A (i16)",
    "SNX3": "0x9986C / 0x106E0: colour-effect parameter B (i8)",
    "STZ4": "0x99038 / 0x83B70: zoom (end values; start/time/easing -> live tween)",
    "STRT": "0x99555 / 0x83940: rotation (end value; start/time/easing -> live tween)",
    "STRR": "0x99542 / 0x84520: extra angle (+0x15C)",
    "STRA": "0x97264 / 0x86EE0: periodic effect value + duration (+0x2C8)",
    "STPV": "0x983AB / 0x86440 (inline args): anchor (pivot) in the picture",
    "SOXY": "0x99885 inline handler: pixel offset x, y",
    "STGS": "0x99065 / 0x84960: alpha (end value; start/time/easing -> live tween)",
    "STMA": "0x9969F / 0xC0330: start motion",
    "SPMA": "0x9929C / 0xC0570: pause (1) / resume (0) all motions",
    "STNS": "0x996B8 / 0x87100: noise effect mode",
    "STTR": "0x99947 inline: per-layer transition rule",
    "SACL": "0x99591 inline: flag 0x40",
    "SBAS": "0x99609 inline: parent layer",
    "STCL": "0x99AF0 / 0x83350: move (end position; start/time/easing -> live tween)",
    "SQK2": "0x992C7 / 0xB6B20: quake (slot 0)",
    "SHK2": "0x9982D / 0xB6FA0: shake (slot 1)",
    "SSW2": "0x99D4B / 0xB7400: swing (slot 1)",
    "SQS2": "0x992E0 / 0xB6EF0: stop quake",
    "SHS2": "0x99673 / 0xB7350: stop shake/swing slot",
    "SSS2": "0x986CF / 0xB7670: stop shake/swing slot",
    "SGMA": "0x99568 / 0xC23B0: write the layer's drawn state into keyframe a2 of a motion",
}

LiveOp = tuple  # (kind: str, layer: int, params: dict)

_STCC_RESET: tuple[tuple[str, object], ...] = (
    ("pic", -1), ("scale", 1000), ("anchor_y", 0), ("anchor_x", 0), ("rot", 0), ("z", 0),
    ("x", 0), ("y", 0), ("alpha", 255), ("zoom_y", 1000), ("zoom_x", 1000),
    ("zoom_pivot", 0), ("mask", -1), ("parent", -1), ("soxy_x", 0), ("soxy_y", 0),
    ("flags", 0), ("crop", (0, 0, 0, 0)), ("a16", 0), ("a17", 0), ("angle", 0),
    ("sttr", -1), ("motion", bytes(24)), ("blend", 0), ("fx_mode", 0), ("fx_a", 0),
    ("fx_b", 0), ("shake_kind", (0, 0)), ("shake_fade", (0, 0)), ("shake_time", (0, 0)),
    ("shake_par", (0,) * 12), ("stra", 0), ("stns", 0), ("stns_b", 0))
# STCH with src: fields copied from the source layer before the new picture is set (0x81B16..)
_SRC_COPY: tuple[str, ...] = (
    "blend", "fx_mode", "fx_a", "fx_b", "x", "y", "alpha", "state", "u284", "u2ac", "u2d4",
    "z", "rot", "scale", "anchor_x", "anchor_y") + tuple(f"u{0x580 + 0x50 * k:x}" for k in range(10))


class _Args:
    """has(i) / expr(i) of the object parser (VA 0xD8C0 / 0xDA50) for one statement."""

    def __init__(self, stmt: str) -> None:
        from .script import parse_script
        cmds = [c for c in parse_script(stmt.encode("utf-8", "surrogateescape")) if c.name_offset >= 0]
        if len(cmds) != 1:
            raise ValueError(f"one statement expected: {stmt!r}")
        self.cmd = cmds[0]
        self.key: str = self.cmd.key

    def has(self, i: int) -> bool:
        return bool(self.cmd.obj_has(i))

    def expr(self, i: int, default: int = 0) -> int:
        v = self.cmd.expr_arg(i)
        return default if v is None else _i32(v)

    def str(self, i: int) -> str:
        a = self.cmd.obj_args
        return str(a[i]) if i < len(a) else ""


class SceneModel:
    """Script-side layer state driven by statements, like the engine handlers.

    ``catalog`` / ``names``: :func:`tsuki.image_catalog.load_catalog` /
    :func:`load_picture_names` (picture id = catalog index).  ``step(stmt)`` returns the
    live operations of the statement (see :data:`LiveOp`)."""

    def __init__(self, catalog: Sequence, names: Sequence[str], state: Optional[SceneState] = None) -> None:
        self.cat = catalog
        self.names = list(names)
        self.index = {n.upper(): i for i, n in enumerate(self.names)}
        self.s = state if state is not None else SceneState()
        self.flags: dict[str, int] = {}          # global words the handlers set
        self.fx_default = 0                      # word 0x164B97C (written by 0x86A30)
        # layers whose drawn record is in use (A +0x10 == 1); SQK2/SHK2 with layer -1 apply
        # to these (VA 0xB6E08..0xB6EC8).  Maintained by the caller (frame model).
        self.used: set[int] = set()

    def lookup(self, name: str) -> int:
        """Picture id of a name (map VA 0x78410): upper-case up to the first '.', 32 bytes."""
        key = name.split(".", 1)[0][:32].upper()
        return self.index.get(key, -1)

    @staticmethod
    def _layers(l: int) -> range:
        return range(NUM_LAYERS) if l < 0 else range(l, l + 1)

    def step(self, stmt: str) -> list:
        a = _Args(stmt)
        f = getattr(self, "_c_" + a.key, None)
        if f is None:
            return []
        return list(f(a) or [])

    # -- STCH / SCH2 (VA 0x81760) -------------------------------------------------
    def _c_STCH(self, a: _Args, reset: bool = True) -> list:
        """Backend 0x81760 (edi = 1 for STCH, 0 for SCH2).  ``reset`` only resets the
        drawn record (0x49570, 0x4A770); the scene-side differences STCH/SCH2 are the
        anchor / rotation / alpha / z defaults."""
        s = self.s
        l = a.expr(0) if a.has(0) else -1
        if not 0 <= l <= 39:
            return []
        src = a.expr(18) if a.has(18) else -1
        name = a.str(1)[:255]
        has_src = 0 <= src <= 39
        h = v = 0
        if has_src:
            p = int(s.get("pic", src))  # type: ignore[call-overload]
            if 0 <= p < len(self.names):
                n = self.names[p]
                if len(n) >= 7 and n[-6] == "_" and n[-3] == "_" and (n[:3].upper() == "IMG" or len(n) >= 15):
                    h, v = _digits2(n[-5:-3]), _digits2(n[-2:])
        if a.has(9):
            h = a.expr(9)
        if a.has(10):
            v = a.expr(10)
        if h | v:
            name += "_%02d_%02d" % (h % 100 if h > 0 else 0, v % 100 if v > 0 else 0)
        live: list = [("reset" if reset else "keep", l, {"src": src})]
        s.set("zoom_y", l, 1000)                    # 0x81AC9
        s.set("zoom_x", l, 1000)
        s.set("zoom_pivot", l, 0)
        pic = self.lookup(name)
        if has_src:                                 # 0x81AF6..0x82612
            s.copy_layer(src, l, _SRC_COPY)
            sp = int(s.get("pic", src))  # type: ignore[call-overload]
            ca = (int(s.get("anchor_x", src)), int(s.get("anchor_y", src)))  # type: ignore[call-overload]
            if 0 <= sp < len(self.cat) and (self.cat[sp].anchor_x, self.cat[sp].anchor_y) == ca:
                cp = self.cat[pic] if 0 <= pic < len(self.cat) else None
                # an unknown name reads the bytes before the table (256, 255); the empty name
                # an empty slot after it (0, -1) [V-emulated edge cases, not in retail]
                dflt = (0, -1) if name == "" else (256, 255)
                s.set("anchor_x", l, cp.anchor_x if cp else dflt[0])
                s.set("anchor_y", l, cp.anchor_y if cp else dflt[1])
            s.copy_layer(src, l, ("zoom_x", "zoom_y", "zoom_pivot", "mask"))
            for k in range(NUM_LAYERS):             # references to src now point to l
                if int(s.get("mask", k)) == src:  # type: ignore[call-overload]
                    s.set("mask", k, l)
            s.copy_layer(src, l, ("parent",))
            for k in range(NUM_LAYERS):
                if int(s.get("parent", k)) == src:  # type: ignore[call-overload]
                    s.set("parent", k, l)
            s.copy_layer(src, l, ("soxy_x", "soxy_y", "flags", "crop", "a16", "a17", "angle", "u19ab",
                                  "u19d3"))
            # motion record: 0xBA800 clears the destination, then 0xBA8E0 copies the source
            # (0x8250E / 0x8252A).  With src == l the source is the record just cleared, so a
            # self-source STCH/SCH2 stops the layer's motion [V-emu, rev_src_diff]
            s.set("motion", l, motion_clear(s.get("motion", l)))
            s.set("motion", l, motion_copy(s.get("motion", l), s.get("motion", src)))
            s.copy_layer(src, l, ("shake_kind", "shake_fade", "shake_time", "shake_par", "stra"))
            ax, ay = int(s.get("anchor_x", l)), int(s.get("anchor_y", l))  # type: ignore[call-overload]
            e_s, d = s.ext(src), _ARR["ext"].addr(l)
            s.b[d:d + 18] = e_s
            s.set_ext(l, EXT_OFF_X, "hh", _i16(-ax), _i16(-ay))
        else:                                       # 0x81F8D
            for nm in ("shake_kind", "shake_fade", "shake_time", "shake_par"):
                s.set(nm, l, _ARR[nm].default)
        if name == "":                              # 0x82618: no picture name -> clear
            for n, val in _STCC_RESET:
                if n not in ("x", "y", "alpha", "z", "zoom_x", "zoom_y", "zoom_pivot", "stra", "stns", "stns_b"):
                    s.set(n, l, val)
            s.set_ext(l, EXT_COLOUR, "8s", _WHITE_BLOCK)
            return live + [("clear", l, {})]
        if a.has(11):
            s.set("mask", l, a.expr(11))
        s.set("sttr", l, -1)                        # 0x82041: unconditional (STTR is per picture)
        crop = list(s.get("crop", l))  # type: ignore[call-overload]
        for k in range(4):
            if a.has(12 + k):
                crop[k] = _i16(a.expr(12 + k))
        s.set("crop", l, tuple(crop))
        if a.has(16):
            s.set("a16", l, a.expr(16) & 0xFFFF)
        if a.has(17):
            s.set("a17", l, a.expr(17) & 0xFF)
        if a.has(19):
            s.set("angle", l, wrap_angle(a.expr(19)))
        if src < 0:                                 # 0x827BA
            s.set("motion", l, bytes(24))
            for nm in ("blend", "fx_mode", "fx_a", "fx_b"):
                s.set(nm, l, 0)
            s.set_ext(l, EXT_COLOUR, "8s", _WHITE_BLOCK)
            if int(s.get("pic", l)) < 0:  # type: ignore[call-overload]   # 0x8284B
                s.set("alpha", l, 0)
        s.set("pic", l, pic)
        if a.has(2):
            s.set("x", l, script_x(a.expr(2)))
        if a.has(3):
            s.set("y", l, script_y(a.expr(3)))
        if reset:
            if pic >= 0:
                s.set("anchor_x", l, self.cat[pic].anchor_x)
                s.set("anchor_y", l, self.cat[pic].anchor_y)
            s.set("rot", l, 0)
        if a.has(4):
            s.set("scale", l, a.expr(4))
        if reset or a.has(5):
            s.set("alpha", l, max(0, min(a.expr(5), 255)) if a.has(5) else 255)
            s.set("state", l, 0)
        if reset or a.has(6):
            s.set("z", l, a.expr(6) if a.has(6) else 10 * l)
        if not has_src:                             # 0x82B07
            ax, ay = (self.cat[pic].anchor_x, self.cat[pic].anchor_y) if pic >= 0 else (256, 255)
            s.set_ext(l, EXT_OFF_X, "hh", _i16(-ax), _i16(-ay))
            if pic >= 0:
                s.set("anchor_x", l, ax)
                s.set("anchor_y", l, ay)
        if self.fx_default > 0 and (pic < 0 or not self.cat[pic].draw_flags & 1):
            s.set("fx_mode", l, self.fx_default)
        return live

    def _c_SCH2(self, a: _Args) -> list:
        return self._c_STCH(a, reset=False)

    # -- simple setters ------------------------------------------------------------
    def _c_STCC(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else 0
        if l > 39:
            return []
        for k in self._layers(l):
            for n, v in _STCC_RESET:
                self.s.set(n, k, v)
        return [("clear", l, {})]

    def _c_STCP(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else 0
        if 0 <= l <= 39:
            self.s.set("z", l, a.expr(1))
        return []

    def _c_STBR(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else 0
        if 0 <= l <= 39:
            self.s.set("blend", l, a.expr(1))
            if a.has(3) and a.expr(3) == 1:
                self.s.set("flags", l, int(self.s.get("flags", l)) | 0x80)  # type: ignore[call-overload]
        return []

    def _c_SNX2(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else -1
        if 0 <= l <= 39:
            self.s.set("fx_a", l, a.expr(1))
        return []

    def _c_SNX3(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else -1
        if 0 <= l <= 39:
            self.s.set("fx_b", l, a.expr(1))
        return []

    def _c_STEC(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else -1
        mode = a.expr(1) if a.has(1) else 0
        if not 0 <= l <= 39:
            return []
        m, colour = stec_mode(mode, a.str(2))
        if m is None:
            return []
        self.s.set("fx_mode", l, m)
        self.s.set_ext(l, EXT_COLOUR, "BBB", (colour >> 16) & 0xFF, (colour >> 8) & 0xFF, colour & 0xFF)
        return [("colour", l, {"mode": m, "rgb": colour})]

    def _c_STZ4(self, a: _Args) -> list:
        s = self.s
        l = a.expr(0) if a.has(0) else 0
        if not 0 <= l <= 39:
            return []
        zx0 = a.expr(1) if a.has(1) else int(s.get("zoom_x", l))  # type: ignore[call-overload]
        zy0 = a.expr(2) if a.has(2) else int(s.get("zoom_y", l))  # type: ignore[call-overload]
        if a.has(3):
            s.set("zoom_x", l, a.expr(3))
        if a.has(4):
            s.set("zoom_y", l, a.expr(4))
        frames = _cdiv(a.expr(5) * 60, 1000) if a.has(5) else 0
        piv = a.expr(7) if a.has(7) else int(s.get("zoom_pivot", l))  # type: ignore[call-overload]
        s.set("zoom_pivot", l, piv)
        return [("zoom", l, {"x0": _i16(zx0), "y0": _i16(zy0), "x1": s.get("zoom_x", l),
                             "y1": s.get("zoom_y", l), "frames": _i16(frames), "pivot": piv & 0xFF,
                             "ease": a.expr(6) if a.has(6) else 0})]

    def _c_STRT(self, a: _Args) -> list:
        s = self.s
        l = a.expr(0) if a.has(0) else 0
        if not 0 <= l <= 39:
            return []
        r0 = a.expr(1) if a.has(1) else _i32(int(s.get("rot", l)))  # type: ignore[call-overload]
        r1 = a.expr(2) if a.has(2) else 0
        s.set("rot", l, r1)
        frames = _cdiv(a.expr(3) * 60, 1000) if a.has(3) else 0
        return [("rotate", l, {"r0": r0, "r1": r1, "frames": _i16(frames), "a5": a.expr(5), "a6": a.expr(6),
                               "ease": a.expr(4) if a.has(4) else 0})]

    def _c_STRR(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else 0
        if not 0 <= l <= 39:
            return []
        v = 0
        if a.has(1):
            r = a.expr(1)
            v = -32768 if r == -100001 else wrap_angle(r)
        self.s.set("angle", l, v)
        return []

    def _c_STRA(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else 0
        if not -1 <= l <= 39:
            return []
        n = a.expr(1) if a.has(1) else 0
        frames = ms_to_frames(a.expr(2)) if a.has(2) else 0
        if l == -1:
            if n <= 0:
                self.s.b[0x1610:0x1670] = bytes(0x60)    # 0x16516D0..0x1651730 (STRA + STNS bytes)
            return [("stra_all", -1, {"n": n})]
        self.s.set("stra", l, n)
        return [("stra", l, {"n": _i16(n), "frames": _i16(frames)})]

    def _c_STPV(self, a: _Args) -> list:
        """Inline-argument command: its own layer parser (VA 0x86478) and 0x76690 readers."""
        c = a.cmd
        l = _stpv_layer(c.raw)
        if not 0 <= l <= 39:
            return []
        s = self.s
        pic = int(s.get("pic", l))  # type: ignore[call-overload]
        fac = _class_factor(self.cat[pic].scale_class) if 0 < pic < len(self.cat) else 1.0
        live: dict = {}
        if c.present(1):
            v = _f32(_f32(_f32(float(c.int_arg(1)) * 640.0) / 480.0) * fac)
            s.set("anchor_x", l, int(v))
            live["ax"] = int(v)
        if c.present(2):
            v = _f32(_f32(_f32(float(c.int_arg(2)) * 480.0) / 360.0) * fac)
            s.set("anchor_y", l, int(v))
            live["ay"] = int(v)
        # 0x86716..0x868C9: on a `_hh_vv` variant (hh_vv != 00_00) the anchor is given in the
        # base picture's frame: add the difference of the two catalog anchors
        if 0 <= pic < len(self.names):
            n = self.names[pic]
            if (len(n) >= 7 and n[-6] == "_" and n[-3] == "_" and (n[:3].upper() == "IMG" or len(n) >= 15)
                    and n[-5:-3] + n[-2:] != "0000"):
                base = self.lookup(n[:-6])
                if base >= 0:
                    if c.present(1):
                        s.set("anchor_x", l, int(s.get("anchor_x", l)) + self.cat[pic].anchor_x - self.cat[base].anchor_x)  # type: ignore[call-overload]
                    if c.present(2):
                        s.set("anchor_y", l, int(s.get("anchor_y", l)) + self.cat[pic].anchor_y - self.cat[base].anchor_y)  # type: ignore[call-overload]
        s.set_ext(l, EXT_OFF_X, "hh", _i16(-int(s.get("anchor_x", l))), _i16(-int(s.get("anchor_y", l))))  # type: ignore[call-overload]
        if c.present(3):
            s.set("rot", l, c.int_arg(3))
        return [("pivot", l, live)]

    def _c_SOXY(self, a: _Args) -> list:
        """Inline handler 0x99885: two byte stores without a layer bound check (layer 40
        writes soxy_y[0] and the first byte of crop[0], as two retail statements do)."""
        l = a.expr(0) if a.has(0) else 0
        for base, v in ((_ARR["soxy_x"].off, a.expr(1)), (_ARR["soxy_y"].off, a.expr(2))):
            if 0 <= base + l < SCENE_LEN:
                self.s.b[base + l] = v & 0xFF
        return []

    def _c_STGS(self, a: _Args) -> list:
        s = self.s
        l = a.expr(0) if a.has(0) else 0
        if not 0 <= l <= 39:
            return []
        start = min(a.expr(1), 255) if a.has(1) else min(int(s.get("alpha", l)), 255)  # type: ignore[call-overload]
        end = min(a.expr(2), 255) if a.has(2) else 0
        s.set("alpha", l, end)
        frames = _cdiv(a.expr(3) * 60, 1000) if a.has(3) else 0
        return [("alpha", l, {"a0": start, "a1": end, "frames": _i16(frames), "ease": a.expr(4) if a.has(4) else 0})]

    def _c_STMA(self, a: _Args) -> list:
        """Backend 0xC0330.  The same record change is made to the drawn record (live op
        ``motion``: apply :func:`motion_record` to ``rec['motion']``, then
        ``apply_motion(..., dt=0)``)."""
        l = a.expr(0) if a.has(0) else 0
        if not 0 <= l <= 39:
            return []
        mid = a.expr(1) if a.has(1) else 0
        n = _i16(a.expr(3)) if a.has(3) else 0
        self.s.set("motion", l, motion_record(self.s.get("motion", l), mid, n))
        if a.has(2) and a.expr(2) in (1, 2):
            self.flags["motion_wait"] = 1
        return [("motion", l, {"id": mid, "n": n, "wait": a.expr(2)})]

    def _c_SGMA(self, a: _Args) -> list:
        """Backend 0xC23B0: no scene_state write; the live op ``motion_edit`` must be applied
        with :func:`edit_motion` to the motion table, from the layer's drawn record."""
        l = a.expr(0) if a.has(0) else 0
        if not 0 <= l <= 39 or not a.has(1) or a.expr(1) <= 0:
            return []
        self.flags["scene_dirty"] = 1                       # word 0x164B998 = 1
        return [("motion_edit", l, {"id": a.expr(1), "key": a.expr(2) if a.has(2) else 0})]

    def _c_SPMA(self, a: _Args) -> list:
        """Backend 0xC0570: pause byte = 1 if the argument is exactly 1, else 0 (0xC05D4)."""
        v = 1 if a.has(0) and a.expr(0) == 1 else 0
        for k in range(NUM_LAYERS):
            m = bytearray(self.s.get("motion", k))  # type: ignore[arg-type]
            m[16] = v
            self.s.set("motion", k, bytes(m))
        return [("motion_pause", -1, {"v": v})]

    def _c_STNS(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else 0
        if not 0 <= l <= 39:
            return []
        n = a.expr(1) & 0xFFFF if a.has(1) else 0
        if n >= 13:
            n, b = 0, 20
        else:
            b = 20 if n < 3 else 12
        self.s.set("stns", l, n)
        self.s.set("stns_b", l, b)
        return [("noise", l, {"n": n, "b": b})]

    def _c_STTR(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else 0
        n = a.expr(1) if a.has(1) else 0
        m = a.expr(2) if a.has(2) else 0
        if not 0 <= l <= 39:
            return []
        v = ((n - 1) | (m << 12)) & 0xFFFF if n > 0 else 0xFFFF
        self.s.set("sttr", l, v)
        return [("rule", l, {"value": _i16(v), "rule": n - 1})]

    def _c_SACL(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else 0
        if not 0 <= l <= 39:
            return []
        f = int(self.s.get("flags", l))  # type: ignore[call-overload]
        self.s.set("flags", l, f | 0x40 if a.has(1) and a.expr(1) != 0 else f & ~0x40)
        return []

    def _c_SBAS(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else 0
        if 0 <= l <= 39:
            self.s.set("parent", l, a.expr(1) if a.has(1) else -1)
        return []

    def _c_STCL(self, a: _Args) -> list:
        """STCL(0, layer, x0, y0, x1, y1, ms, easing) (backend 0x83350): start defaults to
        the current position, end to the start."""
        l = a.expr(1) if a.has(1) else 0
        if not 0 <= l <= 39:
            return []
        s = self.s
        x0 = script_x(a.expr(2)) if a.has(2) else int(s.get("x", l))  # type: ignore[call-overload]
        y0 = script_y(a.expr(3)) if a.has(3) else int(s.get("y", l))  # type: ignore[call-overload]
        x1 = script_x(a.expr(4)) if a.has(4) else x0
        y1 = script_y(a.expr(5)) if a.has(5) else y0
        s.set("x", l, x1)
        s.set("y", l, y1)
        frames = _cdiv(a.expr(6) * 60, 1000) if a.has(6) else 0
        return [("move", l, {"x0": x0, "y0": y0, "x1": x1, "y1": y1, "frames": frames, "ease": a.expr(7)})]

    # -- quake / shake / swing (VA 0x84670 store, 0x847F0 stop) ------------------------
    def _shake(self, l: int, kind: int, par: Sequence[int], dur: int, fade: int) -> list:
        slot = 0 if kind == 1 else 1
        s = self.s
        if l >= 0:
            targets: Iterable[int] = (l,)
        else:
            # 0xB6E08: layers 0, 1 if in use; others if in use (and z slot > 0) or if the
            # scene has a picture for them
            targets = [k for k in range(NUM_LAYERS)
                       if k in self.used or (k >= 2 and int(s.get("pic", k)) != -1)]  # type: ignore[call-overload]
        for k in targets:
            sk = list(s.get("shake_kind", k))  # type: ignore[call-overload]
            sk[slot] = kind
            s.set("shake_kind", k, tuple(sk))
            sf = list(s.get("shake_fade", k))  # type: ignore[call-overload]
            sf[slot] = _i16(ms_to_frames(fade))
            s.set("shake_fade", k, tuple(sf))
            st = list(s.get("shake_time", k))  # type: ignore[call-overload]
            st[slot] = _i16(ms_to_frames(dur)) if dur >= 0 else -1
            s.set("shake_time", k, tuple(st))
            sp = list(s.get("shake_par", k))  # type: ignore[call-overload]
            sp[6 * slot:6 * slot + 6] = [_i16(v) for v in par]
            s.set("shake_par", k, tuple(sp))
        return [("shake", l, {"kind": kind, "par": tuple(par), "dur": dur, "fade": fade})]

    def _c_SQK2(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else 0
        if l > 39:
            return []
        dur = a.expr(4) if a.has(4) else -1
        fade = a.expr(5) if a.has(5) else 0
        q = 2
        if a.has(3):
            v = a.expr(3)
            q = 1 if v < 0 else (2 if 3 * v + 0x31 < 0x63 else _cdiv(3 * v, 50) + 1)
        a7 = a.expr(7) if a.has(7) else 0
        par = (a.expr(1) if a.has(1) else 15, a.expr(2) if a.has(2) else 15, q, ms_to_frames(a7),
               ms_to_frames(dur) if dur >= 0 and a7 <= 16 else 0, a.expr(6) if a.has(6) else 0)
        return self._shake(l, 1, par, dur, fade)

    def _c_SHK2(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else 0
        if l > 39:
            return []
        dur = a.expr(4) if a.has(4) else -1
        fade = a.expr(5) if a.has(5) else 0
        par = (a.expr(1) if a.has(1) else 0, a.expr(2) if a.has(2) else 15,
               ms_to_frames(a.expr(3)) if a.has(3) else 18, a.expr(6) if a.has(6) else 0,
               ms_to_frames(a.expr(7)) if a.has(7) else 0, a.expr(8) if a.has(8) else 1)
        return self._shake(l, 2, par, dur, fade)

    def _c_SSW2(self, a: _Args) -> list:
        l = a.expr(0) if a.has(0) else 0
        if l > 39:
            return []
        dur = a.expr(6) if a.has(6) else -1
        fade = a.expr(7) if a.has(7) else 0
        par = (a.expr(1) if a.has(1) else 7, a.expr(2) if a.has(2) else 15,
               ms_to_frames(a.expr(3)) if a.has(3) else 60, ms_to_frames(a.expr(4)) if a.has(4) else 120,
               a.expr(5) if a.has(5) else 1, 0)
        return self._shake(l, 3, par, dur, fade)

    def _stop(self, a: _Args, kind: int) -> list:
        l = a.expr(0) if a.has(0) else 0
        if l > 39:
            return []
        slot = 0 if kind == 1 else 1
        for k in self._layers(l):
            for nm in ("shake_kind", "shake_fade", "shake_time"):
                v = list(self.s.get(nm, k))  # type: ignore[call-overload]
                v[slot] = 0
                self.s.set(nm, k, tuple(v))
            sp = list(self.s.get("shake_par", k))  # type: ignore[call-overload]
            sp[6 * slot:6 * slot + 6] = [0] * 6
            self.s.set("shake_par", k, tuple(sp))
        return [("shake_stop", l, {"kind": kind})]

    def _c_SQS2(self, a: _Args) -> list:
        return self._stop(a, 1)

    def _c_SHS2(self, a: _Args) -> list:
        return self._stop(a, 2)

    def _c_SSS2(self, a: _Args) -> list:
        return self._stop(a, 3)


_WHITE_BLOCK = bytes((255, 255, 255, 0, 0, 0, 0, 0))   # ext +6..+0xD after 0xFA10


def _digits2(s: str) -> int:
    """Two name characters read as decimal digits without validation (VA 0x81900)."""
    return (ord(s[0]) - 48) * 10 + (ord(s[1]) - 48)


def _f32(x: float) -> float:
    return float(struct.unpack("<f", struct.pack("<f", x))[0])


def _class_factor(scale_class: int) -> float:
    """Sprite size class A/L/M/S (catalog +0x42) -> 1, 0.5, 0.25, 0.125 (VA 0x526A3, 0x865A2)."""
    return {1: 0.5, 2: 0.25, 3: 0.125}.get(scale_class, 1.0)


def _stpv_layer(raw: str) -> int:
    """STPV's own layer parser (VA 0x86478): decimal digits of the first argument; '-' makes
    the number negative (jump table 0x20C4B8 over ')'..';')."""
    i = raw.index("(") + 1 if "(" in raw else 6
    if raw[i:i + 1] == "`":
        i += 5
    v, sign = 0, 1
    for ch in raw[i:i + 16]:
        if ch in ",);":
            break
        if ch == "-":
            sign = -1
        elif ch.isdigit():
            v = v * 10 + int(ch)
    return _i16(v * sign)


def stec_mode(mode: int, colour_arg: str) -> tuple[Optional[int], int]:
    """STEC (VA 0x10390): stored mode and colour for a mode argument (None = nothing stored).
    Modes 0..81 are stored as given (10..76 directly, 0..9 and 77..81 through the jump table
    at VA 0x175718, which stores the same value); mode 1 stores the colour argument
    (``strtol(s, NULL, 0)``), every other mode white 0xFFFFFF; modes < 0 or > 81 are ignored
    [V-emulated: sweep -2..89]."""
    if not 0 <= mode <= 81:
        return None, 0
    if mode == 1:
        return 1, _strtol0(colour_arg) & 0xFFFFFF
    return mode, 0xFFFFFF


def _strtol0(s: str) -> int:
    """strtol(s, NULL, 0) of the colour argument (hex with 0x, octal with 0, else decimal)."""
    import re
    m = re.match(r"\s*([+-]?)(0[xX][0-9a-fA-F]+|0[0-7]*|[1-9][0-9]*)", s)
    if not m:
        return 0
    g = m.group(2)
    v = int(g, 16) if g[:2].lower() == "0x" else (int(g, 8) if g.startswith("0") and len(g) > 1 else int(g))
    return -v if m.group(1) == "-" else v



# ---------------------------------------------------------------------------
# 3. Easing (VA 0xBC460; the tween copy VA 0x48220 is the same function) and keyframe
#    interpolation (VA 0xBDD70 with the spline helper VA 0xBD5E0).
# ---------------------------------------------------------------------------
def _bounce_out(t: float, b: float, c: float, d: float) -> float:
    t /= d
    if t < 1 / 2.75:
        return c * (7.5625 * t * t) + b
    if t < 2 / 2.75:
        t -= 1.5 / 2.75
        return c * (7.5625 * t * t + 0.75) + b
    if t < 2.5 / 2.75:
        t -= 2.25 / 2.75
        return c * (7.5625 * t * t + 0.9375) + b
    t -= 2.625 / 2.75
    return c * (7.5625 * t * t + 0.984375) + b


def _ease_in(fam: str, t: float, b: float, c: float, d: float) -> float:
    if fam == "quad":
        t /= d
        return c * t * t + b
    if fam == "cubic":
        t /= d
        return c * t * t * t + b
    if fam == "quart":
        t /= d
        return c * t ** 4 + b
    if fam == "quint":
        t /= d
        return c * t ** 5 + b
    if fam == "sine":
        return -c * math.cos(t / d * (math.pi / 2)) + c + b
    if fam == "expo":
        return b if t == 0 else c * 2 ** (10 * (t / d - 1)) + b - c * 0.001
    if fam == "circ":
        t /= d
        return -c * (math.sqrt(max(0.0, 1 - t * t)) - 1) + b
    if fam == "elastic":
        if t == 0:
            return b
        t /= d
        if t == 1:
            return b + c
        p = d * 0.3
        s = p / 4
        t -= 1
        return -(c * 2 ** (10 * t) * math.sin((t * d - s) * (2 * math.pi) / p)) + b
    if fam == "back":
        s = 1.70158
        t /= d
        return c * t * t * ((s + 1) * t - s) + b
    if fam == "bounce":
        return c - _bounce_out(d - t, 0, c, d) + b
    raise ValueError(fam)


def _ease_out(fam: str, t: float, b: float, c: float, d: float) -> float:
    if fam == "quad":
        t /= d
        return -c * t * (t - 2) + b
    if fam == "cubic":
        t = t / d - 1
        return c * (t * t * t + 1) + b
    if fam == "quart":
        t = t / d - 1
        return -c * (t ** 4 - 1) + b
    if fam == "quint":
        t = t / d - 1
        return c * (t ** 5 + 1) + b
    if fam == "sine":
        return c * math.sin(t / d * (math.pi / 2)) + b
    if fam == "expo":
        return b + c if t == d else c * 1.001 * (-(2 ** (-10 * t / d)) + 1) + b
    if fam == "circ":
        t = t / d - 1
        return c * math.sqrt(max(0.0, 1 - t * t)) + b
    if fam == "elastic":
        if t == 0:
            return b
        t /= d
        if t == 1:
            return b + c
        p = d * 0.3
        s = p / 4
        return c * 2 ** (-10 * t) * math.sin((t * d - s) * (2 * math.pi) / p) + c + b
    if fam == "back":
        s = 1.70158
        t = t / d - 1
        return c * (t * t * ((s + 1) * t + s) + 1) + b
    if fam == "bounce":
        return _bounce_out(t, b, c, d)
    raise ValueError(fam)


def _ease_inout(fam: str, t: float, b: float, c: float, d: float) -> float:
    if fam == "expo":
        if t == 0:
            return b
        if t == d:
            return b + c
        t /= d / 2
        if t < 1:
            return c / 2 * 2 ** (10 * (t - 1)) + b - c * 0.0005
        t -= 1
        return c / 2 * 1.0005 * (-(2 ** (-10 * t)) + 2) + b
    if fam == "elastic":
        if t == 0:
            return b
        t /= d / 2
        if t == 2:
            return b + c
        p = d * (0.3 * 1.5)
        s = p / 4
        t -= 1
        if t < 0:
            return -0.5 * (c * 2 ** (10 * t) * math.sin((t * d - s) * (2 * math.pi) / p)) + b
        return c * 2 ** (-10 * t) * math.sin((t * d - s) * (2 * math.pi) / p) * 0.5 + c + b
    if fam == "back":
        s = 1.70158 * 1.525
        t /= d / 2
        if t < 1:
            return c / 2 * (t * t * ((s + 1) * t - s)) + b
        t -= 2
        return c / 2 * (t * t * ((s + 1) * t + s) + 2) + b
    if t < d / 2:
        return _ease_in(fam, t * 2, b, c / 2, d)
    return _ease_out(fam, t * 2 - d, b + c / 2, c / 2, d)


def _ease_outin(fam: str, t: float, b: float, c: float, d: float) -> float:
    if t < d / 2:
        return _ease_out(fam, t * 2, b, c / 2, d)
    return _ease_in(fam, t * 2 - d, b + c / 2, c / 2, d)


EASE_FAMILIES = ("quad", "cubic", "quart", "quint", "sine", "circ", "expo", "elastic", "back", "bounce")


def ease(eid: int, t: float, b: float, c: float, d: float) -> float:
    """Easing function ``eid`` (Penner form: time t, begin b, change c, duration d).

    0, 1 and 42..69, 71+ (and every negative id): linear ``c * t / d + b``.
    2..41: family ``EASE_FAMILIES[(eid - 2) // 4]`` in the variant ``(eid - 2) % 4`` =
    in, out, in-out, out-in.  Expo has the engine's 0.001 / 1.001 / 0.0005 / 1.0005
    correction terms.  Id 70 is a separate jump-table entry that is not modelled (not used
    by any retail motion or command) [V-emulated against VA 0xBC460: max relative error
    3e-7 over ids 0..71 at 21 times x 3 durations x 2 ranges]."""
    if 10 <= eid <= 41 and eid not in EASE_EXACT:
        fam = EASE_FAMILIES[(eid - 2) // 4]
        return (_ease_in, _ease_out, _ease_inout, _ease_outin)[(eid - 2) % 4](fam, t, b, c, d)
    return _ease_exact(eid, t, b, c, d)


EASE_EXACT = frozenset(range(2, 12)) | {14, 15, 18, 19, 20, 26, 27, 34, 35}


def _ease_exact(eid: int, t: float, b: float, c: float, d: float) -> float:
    """Bit-exact port of VA 0xBC460 for linear (every id outside 2..41 except 70), quad / cubic
    / quart (ids 2..11), quint in / out (14, 15), sine in / out / in-out (18..20), expo in / out
    (26, 27) and back in / out (34, 35), with the engine's float32 / float64 operation order:
    99.8 % of the eased keyframes and every easing the retail STGS / STZ4 / STRT tweens use
    (0, 2, 3, 6, 10, 35).  ``F`` = round to float32; plain
    Python arithmetic is float64 like the engine's ``*sd`` instructions; ``pow`` / ``exp2`` are
    the libm calls of the engine (math.pow, 2.0 ** x)."""
    F = _f32
    t, b, c, d = F(t), F(b), F(c), F(d)
    k1, k2 = F(2.70158), F(1.70158)                         # 0x22AB2C, 0x22AB38 (0x22AB30 = -k2)
    if eid == 10:                                           # 0xBC5DF: quart in
        x = F(t / d)
        return F(math.pow(x, 3.0) * F(x * c) + b)
    if eid == 11:                                           # 0xBC601: quart out
        x = F(F(t / d) - 1.0)
        return F(b - (math.pow(x, 3.0) * x - 1.0) * c)
    if eid == 14:                                           # 0xBC6FD: quint in
        x = F(t / d)
        return F(math.pow(x, 4.0) * F(x * c) + b)
    if eid == 15:                                           # 0xBC729, 0xBC7CA: quint out
        x = F(F(t / d) - 1.0)
        return F((math.pow(x, 4.0) * x + 1.0) * c + b)
    if eid == 18:                                           # 0xBC7FC, 0xBD08E: sine in
        x = F(t / d)
        return F((c - c * math.cos(x * (math.pi / 2))) + b)
    if eid == 19:                                           # 0xBC80F, 0xBC87B: sine out
        x = F(t / d)
        return F(math.sin(x * (math.pi / 2)) * c + b)
    if eid == 20:                                           # 0xBC815: sine in-out
        return F(b + (math.cos(t * math.pi / d) - 1.0) * (c * -0.5))
    if eid == 26:                                           # 0xBC9A3, 0xBD366: expo in
        if t == 0.0:
            return b
        x = F(F(F(t / d) - 1.0) * 10.0)
        return F((c * 2.0 ** x + b) + c * -0.001)
    if eid == 27:                                           # 0xBC9BC, 0xBD30B: expo out
        if t == d:
            return F(b + c)
        x = F(F(t * -10.0) / d)
        return F((1.0 - 2.0 ** x) * (c * 1.001) + b)
    if eid == 34:                                           # 0xBCC80: back in
        x = F(t / d)
        return F(F(F(x * F(x * c)) * F(F(x * k1) - k2)) + b)
    if eid == 35:                                           # 0xBCCA1: back out
        x = F(F(t / d) - 1.0)
        return F(F(c * F(F(F(x * x) * F(F(x * k1) + k2)) + 1.0)) + b)
    if eid == 2:                                            # 0xBC48C
        x = F(t / d)
        return F(F(F(x * c) * x) + b)
    if eid == 3:                                            # 0xBC499
        x = F(t / d)
        return F(b - F(F(x * c) * F(x - 2.0)))
    if eid == 4:                                            # 0xBC4B6, 0xBCE58
        x = F(t / (d * 0.5))
        if 1.0 > x:
            return F((c * 0.5) * (x * x) + b)
        x = F(x - 1.0)
        return F(b + (c * -0.5) * F(F(x * F(x - 2.0)) - 1.0))
    if eid == 5:                                            # 0xBC4F2, 0xBCE91
        x = F(t + t)
        if d * 0.5 > t:
            x = F(x / d)
            return F(F(F(F(c * -0.5) * x) * F(x - 2.0)) + b)
        b2 = F(c * 0.5 + b)
        x = F(F(x - d) / d)
        return F(F(F(F(c * 0.5) * x) * x) + b2)
    if eid == 6:                                            # 0xBC52D
        x = F(t / d)
        return F(F(x * c) * (x * x) + b)
    if eid == 7:                                            # 0xBC546
        x = F(F(t / d) - 1.0)
        return F(c * (x * (x * x) + 1.0) + b)
    if eid == 8:                                            # 0xBC558, 0xBCEC2
        x = F(t / (d * 0.5))
        if 1.0 > x:
            return F(math.pow(x, 3.0) * (c * 0.5) + b)
        x = F(x - 2.0)
        return F((c * 0.5) * (x * (x * x) + 2.0) + b)
    if eid == 9:                                            # 0xBC590, 0xBCEEF
        x = F(t + t)
        if d * 0.5 > t:
            x = F(F(x / d) - 1.0)
            return F(F(c * 0.5) * (x * (x * x) + 1.0) + b)
        b2 = F(c * 0.5 + b)
        x = F(F(x - d) / d)
        return F(F(F(c * 0.5) * x) * (x * x) + b2)
    return F(F(F(t * c) / d) + b)                           # 0xBCE1A: linear


UNSET, HOLD = -32767, -32766          # i16 keyframe fields: "not animated", "hold"
UNSET8, HOLD8 = -100001, -100000      # the i64 field 8 (rotation)


def _q(f: int, v: float) -> float:
    """Fields 3 / 4 (x / y in script units) are snapped to whole engine units:
    ``trunc(v * 640/480)`` (as i16) ``* 480/640`` (y: 480/360)."""
    if f == 3:
        return _f32(_f32(float(_i16(int(_f32(_f32(v * 640.0) / 480.0)))) * 480.0) / 640.0)
    if f == 4:
        return _f32(_f32(float(_i16(int(_f32(_f32(v * 480.0) / 360.0)))) * 360.0) / 480.0)
    return v


@dataclass
class Motion:
    """One motion: keyframe records [start, end) of the global table."""
    id: int
    duration: int
    start: int
    end: int


class MotionTable:
    """Keyframe tables of eboot.bin (``tsuki.script_tables.read_motions``) in the engine's
    global indexing: ``val(i, f)`` is field f of keyframe record i (f = 0: time), like the
    accessor VA 0xC2190; ``flags(id)`` the per-motion field mask (VA 0xC1C40, table
    0x245130); ``loops(id)`` VA 0xC1C70."""

    def __init__(self, eboot: Union[str, Path]) -> None:
        from .script_tables import read_motions
        data = Path(eboot).read_bytes()
        self.times: list[int] = []
        self.vals: list[tuple[int, ...]] = []
        self.motions: dict[int, Motion] = {}
        for m in read_motions(eboot):
            s = len(self.times)
            for k in m.keyframes:
                self.times.append(k.time)
                self.vals.append(tuple(k.fields))
            self.motions[m.id] = Motion(m.id, m.duration, s, len(self.times))
        self._flags = struct.unpack_from("<24193Q", data, 0x245130 + 0x2280)

    def val(self, i: int, f: int) -> int:
        return self.times[i] if f == 0 else self.vals[i][f - 1]

    def set_val(self, i: int, f: int, v: int) -> None:
        """Change field f (1..27) of keyframe record i, as ``_SGMA`` does in the engine's
        in-memory table (:func:`edit_motion`)."""
        row = list(self.vals[i])
        row[f - 1] = v
        self.vals[i] = tuple(row)

    def flags(self, mid: int) -> int:
        return self._flags[mid] if 1 <= mid <= 24192 else 0

    def get(self, mid: int) -> Optional[Motion]:
        """None for ids outside 1..24192 (the accessors return 0 for them: no motion)."""
        return self.motions.get(mid)


def _spline_ab(T: MotionTable, f: int, s: int, e: int, k: int, cur: float) -> Optional[tuple[float, float]]:
    """VA 0xBD5E0: second-derivative pair (a, b) of the segment [k-1, k] of a "natural"
    cubic spline through the motion's keys with unit knot spacing, or None.  The engine's
    forward elimination subtracts ``diag * (1/diag)`` (= 1) where the textbook Thomas step
    subtracts ``rhs * (1/diag)``; this port keeps that (0xBDC70..0xBDCA9)."""
    if s == k or e - s + 1 < 3:
        return None
    is8 = (f & ~8) == 0
    hold, unset = (HOLD8, UNSET8) if is8 else (HOLD, UNSET)

    def defined_back(i: int) -> float:
        j = i
        v = T.val(j, f)
        while v in (hold, unset) and j > s:
            j -= 1
            v = T.val(j, f)
        return cur if v in (hold, unset) else float(v)

    v0 = T.val(s, f)
    Y = [cur if v0 in (hold, unset) else float(v0)]
    kmap = {0: 0}
    prev = v0
    i = s + 1
    while i < e:
        raw, nxt = T.val(i, f), T.val(i + 1, f)
        if raw == hold or (prev == raw and raw == nxt):
            kmap[i - s] = len(Y) - 1
        else:
            kmap[i - s] = len(Y)
            Y.append(defined_back(i))
        prev = raw
        i += 1
    raw_e = T.val(e, f)
    if prev != raw_e and raw_e != unset:
        kmap[e - s] = len(Y)
        Y.append(defined_back(e))
    n = len(Y)
    if n < 3:
        return None
    D = [0.0] * n
    for j in range(n - 1, 0, -1):
        D[j] = _f32(Y[j] - Y[j - 1])
    M = [0.0] * (n + 1)
    M[1] = _f32(D[2] - D[1])
    diag = [0.0] * n
    diag[1] = 4.0
    x3 = 4.0
    for j in range(2, n - 1):
        w = _f32(1.0 / x3)
        M[j] = _f32(_f32(D[j + 1] - D[j]) - _f32(x3 * w))
        x3 = _f32(4.0 - w)
        diag[j] = x3
    M[n - 2] = _f32(M[n - 2] - M[n - 1])
    for j in range(n - 1, 1, -1):
        M[j - 1] = _f32(_f32(M[j - 1] - M[j]) / diag[j - 1])
    idx = kmap.get(k - s - 1, 0)
    return M[idx], M[idx + 1]


def motion_value(T: MotionTable, f: int, s: int, e: int, k: int, t: float, cur: float,
                 whole: bool = False) -> float:
    """VA 0xBDD70: value of keyframe field ``f`` at motion time ``t`` (ms) for keys
    [s, e] (e inclusive), ``k`` = first key with time >= t, ``cur`` = the layer's current
    value (returned when the field is not animated), ``whole`` = motion 9999 (easing
    applied by the caller: segment easing forced to linear)."""
    is8 = (f & ~8) == 0
    hold, unset = (HOLD8, UNSET8) if is8 else (HOLD, UNSET)
    if float(T.val(s, 0)) > t:
        return cur
    r = k
    while True:
        vn = T.val(r, f)
        tn = float(T.val(r, 0))
        if vn == hold and r < e:
            r += 1
            continue
        break
    tp = 0.0
    if k != s and not t >= tn:
        j = k
        while True:
            j -= 1
            vp = T.val(j, f)
            if vp == hold and j > s:
                continue
            break
        tp = float(T.val(j, 0))
        if vn != unset and vp == unset and j > s:
            i = j
            while True:
                i -= 1
                vp = T.val(i, f)
                if vp not in (hold, unset) or i <= s:
                    break
    else:
        if r < s:
            vp = 0
        else:
            i = r
            while True:
                vp = T.val(i, f)
                if vp not in (hold, unset) or i <= s:
                    break
                i -= 1
    if vn == unset:
        j = k
        while True:
            v = T.val(j, f)
            if v not in (hold, unset) or j <= s:
                break
            j -= 1
        if v in (unset, hold):
            return cur
        return _q(f, float(v))
    if vp == unset:
        return cur
    if vn == hold:
        j = e - 1
        while j >= s:
            v = T.val(j, f)
            if v != hold:
                return float(v)
            j -= 1
        return cur
    if vp == hold:
        return cur
    if tn > tp:
        if whole:
            eid = 0
        elif k == s:
            eid = T.val(s, 1)
        else:
            eid = UNSET
            j = k
            while j > s:
                j -= 1
                if T.val(j, f) == hold:
                    continue
                eid = T.val(j, 1)
                if eid not in (UNSET, HOLD):
                    break
        if eid in (UNSET, HOLD):
            eid = 0
        p = _f32(ease(eid, _f32(t - tp), 0.0, 1.0, _f32(tn - tp)))
    else:
        p = 1.0
    if 17 <= f <= 27 or f == 15:
        return float(vp)
    vn_f, vp_f = _q(f, float(vn)), _q(f, float(vp))
    if k == s:
        it = T.val(s, 2)
    elif k > s:
        j = k
        while True:
            j -= 1
            it = T.val(j, 2)
            if it not in (UNSET, HOLD) or j <= s:
                break
    else:
        it = 0
    if it == 2:
        return vp_f
    if it == 1:
        ab = _spline_ab(T, f, s, e, k, cur)
        if ab is not None:
            a, b = ab
            x = _f32(_f32(_f32(_f32(b - a) * p) + _f32(a * 3.0)) * p)
            x = _f32(x + _f32(_f32(vn_f - vp_f) - _f32(_f32(a + a) + b)))
            return _f32(_f32(x * p) + vp_f)
    return _f32(_f32(_f32(vn_f - vp_f) * p) + vp_f)


# ---------------------------------------------------------------------------
# 4. Render records (arrays A/B/C, 40 x 0x2C8 bytes) and the commit.
# ---------------------------------------------------------------------------
RENDER_FIELDS: tuple[tuple[int, str, str, str], ...] = (
    # (offset, struct format, name, meaning / writer)
    (0x10, "i", "used", "1 while the layer is drawn (set when B is moved to A)"),
    (0x14, "i", "face", "1 while the picture's face layer (catalog name1) is bound to slot 0x111+l"),
    (0x18, "i", "pic", "picture id (catalog index), -1 none"),
    (0x1C, "h", "blend", "blend mode (STBR, motion field 17)"),
    (0x1E, "B", "flags", "0x40 SACL (skip mask offset), 0x80 STBR a3"),
    (0x1F, "b", "fx_mode", "colour-effect mode (STEC, motion field 18)"),
    (0x20, "h", "fx_a", "colour-effect parameter A (SNX2, motion field 24)"),
    (0x22, "b", "fx_b", "colour-effect parameter B (SNX3, motion field 25)"),
    (0x24, "i", "state", "scene state byte (engine)"),
    (0x2C, "f", "alpha", "alpha 0..1"),
    (0x30, "f", "x", "position x, engine units (640-wide canvas)"),
    (0x34, "f", "y", "position y"),
    (0x48, "f", "dx", "extra offset x"),
    (0x4C, "f", "dy", "extra offset y"),
    (0x50, "f", "off_x", "picture offset x = -anchor_x (canvas units at scale 1)"),
    (0x54, "f", "off_y", "picture offset y"),
    (0x58, "q", "rot", "rotation, 1/100 degree"),
    (0x70, "h", "scale", "scale, per mille"),
    (0x76, "h", "anchor_x", "anchor (pivot) x in canvas units"),
    (0x78, "h", "anchor_y", "anchor y"),
    (0x7A, "h", "rot_delta", "rotation tween start - end"),
    (0x7C, "h", "rot_cur", "current rotation tween offset = trunc(r(0x90) * rot_delta)"),
    (0x84, "3h", "tw_move", "move tween (STCL): easing, counter Q12, step"),
    (0x8A, "3h", "tw_scale", "scale tween: easing, counter, step"),
    (0x90, "3h", "tw_rot", "rotation tween (STRT)"),
    (0x96, "3h", "tw_alpha", "alpha tween (STGS)"),
    (0x9C, "3h", "tw_zoom", "zoom tween (STZ4)"),
    (0xAA, "h", "zoom_x", "zoom x per mille (negative = mirrored)"),
    (0xAC, "h", "zoom_y", "zoom y per mille"),
    (0xB0, "B", "zoom_pivot", "STZ4 a7: zoom in screen space about the layer position"),
    (0xCC, "f", "alpha_cur", "current alpha tween offset = r(0x96) * alpha_delta"),
    (0xE0, "f", "scale_delta", "scale tween start - end"),
    (0xE4, "f", "scale_cur", "current scale tween offset"),
    (0xFC, "f", "zoom_dx", "zoom tween start - end, x (zoom units / 1000)"),
    (0x100, "f", "zoom_dy", "zoom tween start - end, y"),
    (0x104, "f", "move_dx", "move tween start - end, x"),
    (0x108, "f", "move_dy", "move tween start - end, y"),
    (0x110, "f", "alpha_delta", "alpha tween start - end"),
    (0x120, "i", "mask", "STCH a11 (mask / clip layer), -1 none"),
    (0x128, "i", "parent", "SBAS parent layer, -1 none"),
    (0x12C, "i", "sttr", "STTR rule value, -1 none"),
    (0x130, "b", "soxy_x", "SOXY x (1920-px units, x 1/3)"),
    (0x131, "b", "soxy_y", "SOXY y (x 448/1080)"),
    (0x132, "4h", "crop", "STCH a12..a15"),
    (0x13A, "H", "a16", "STCH a16"),
    (0x13C, "B", "a17", "STCH a17"),
    (0x15C, "h", "angle", "STCH a19 / STRR"),
    (0x160, "24s", "motion", "motion record: i16 id, i16 speed divisor, i64 time (frames), u8 pause"),
    (0x178, "2B", "shake_kind", "quake / shake slots"),
    (0x1D0, "B", "stns", "STNS"),
    (0x1D1, "B", "stns_b", "STNS"),
    (0x1F0, "17s", "colour", "colour block (0xF7A0): +0 i16 0, +2 i16, +5..+7 RGB, +8..+0xA second "
                             "RGB, +0xB..+0x10 the previous +5..+0xA"),
    (0x2C8, "h", "stra", "STRA value (offsets >= 0x2C8 overlap the next record's +0x0..+0x9)"),
)


@dataclass
class RenderRecord:
    """Decoded drawn record (array A) or committed record (array B)."""
    raw: bytes
    f: dict = field(default_factory=dict)

    @classmethod
    def from_bytes(cls, b: bytes) -> "RenderRecord":
        d: dict = {}
        for off, fmt, name, _ in RENDER_FIELDS:
            if off + struct.calcsize("<" + fmt) <= len(b):
                v = struct.unpack_from("<" + fmt, b, off)
                d[name] = v[0] if len(v) == 1 else v
        return cls(bytes(b), d)

    def __getattr__(self, name: str) -> object:
        try:
            return self.__dict__["f"][name]
        except KeyError:
            raise AttributeError(name) from None


def record_bytes(fields: dict, raw: bytes) -> bytes:
    """Encode the :data:`RENDER_FIELDS` of a record dict back into a copy of ``raw`` (the
    record's original bytes); keys that are not render fields (``colour_rgb``,
    ``z_order``) are ignored."""
    out = bytearray(raw)
    for off, fmt, name, _ in RENDER_FIELDS:
        if name not in fields or off + struct.calcsize("<" + fmt) > len(out):
            continue
        v = fields[name]
        if isinstance(v, (tuple, list)):
            struct.pack_into("<" + fmt, out, off, *v)
        elif isinstance(v, (bytes, bytearray)):
            struct.pack_into("<" + fmt, out, off, bytes(v))
        elif fmt in ("f",):
            struct.pack_into("<f", out, off, float(v))
        else:
            bits = struct.calcsize("<" + fmt) * 8
            iv = int(v) & ((1 << bits) - 1)
            if fmt.islower() and iv >> (bits - 1):
                iv -= 1 << bits
            struct.pack_into("<" + fmt, out, off, iv)
    return bytes(out)


def commit(scene: SceneState, layer: int) -> dict[str, object]:
    """Fields the commit (VA 0x7DD10, per layer 0x7E107..0x7E1DD and 0x7DE60..0x7E05C) writes
    into the committed record B for one layer, as {render field name: value}.  Zoom and the
    pivot flag go to the drawn record A directly (0x49930 at 0x7E29D)."""
    g = scene.get
    l = layer
    pic = int(g("pic", l))  # type: ignore[call-overload]
    ex = scene.ext(l)
    return {
        "pic": pic if pic < 0x59D8 else -1,
        "blend": g("blend", l), "fx_mode": g("fx_mode", l), "fx_a": g("fx_a", l), "fx_b": g("fx_b", l),
        "x": float(g("x", l)), "y": float(g("y", l)),  # type: ignore[arg-type]
        "off_x": float(struct.unpack_from("<h", ex, 2)[0]), "off_y": float(struct.unpack_from("<h", ex, 4)[0]),
        "alpha": _f32(float(g("alpha", l)) * _f32(1 / 255)),  # type: ignore[arg-type]
        "state": g("state", l), "scale": g("scale", l), "anchor_x": g("anchor_x", l),
        "anchor_y": g("anchor_y", l), "rot": g("rot", l), "mask": g("mask", l), "parent": g("parent", l),
        "sttr": g("sttr", l), "soxy_x": g("soxy_x", l), "soxy_y": g("soxy_y", l), "flags": g("flags", l),
        "crop": g("crop", l), "a16": g("a16", l), "a17": g("a17", l), "angle": g("angle", l),
        "stns": g("stns", l), "stns_b": g("stns_b", l),
        "zoom_x": g("zoom_x", l), "zoom_y": g("zoom_y", l), "zoom_pivot": g("zoom_pivot", l),
    }


# ---------------------------------------------------------------------------
# 5. Projection: picture texels -> scene canvas (640 x 362.667) -> 1920 x 1080 frame.
# ---------------------------------------------------------------------------
CANVAS = (640.0, 362.6666564941406)      # VA 0x1A7710 (normal scene canvas, 0x56E5A)
PX_X = 1.0 / 3.0                         # texel -> canvas, x (global scale at scale 1, zoom 1)
PX_Y = _f32(362.6666564941406 / 1080.0)  # 0.3358025 (y)


@dataclass(frozen=True)
class Affine:
    """x' = a*u + b*v + c ; y' = d*u + e*v + f."""
    a: float
    b: float
    c: float
    d: float
    e: float
    f: float

    def __matmul__(self, o: "Affine") -> "Affine":
        return Affine(self.a * o.a + self.b * o.d, self.a * o.b + self.b * o.e, self.a * o.c + self.b * o.f + self.c,
                      self.d * o.a + self.e * o.d, self.d * o.b + self.e * o.e, self.d * o.c + self.e * o.f + self.f)

    def apply(self, u: float, v: float) -> tuple[float, float]:
        return self.a * u + self.b * v + self.c, self.d * u + self.e * v + self.f

    @staticmethod
    def translate(x: float, y: float) -> "Affine":
        return Affine(1, 0, x, 0, 1, y)

    @staticmethod
    def scale(sx: float, sy: float) -> "Affine":
        return Affine(sx, 0, 0, 0, sy, 0)

    @staticmethod
    def rotate(rad: float) -> "Affine":
        c, s = math.cos(rad), math.sin(rad)
        return Affine(c, -s, 0, s, c, 0)

    def max_diff(self, o: "Affine", w: float = 1.0, h: float = 1.0) -> float:
        """Largest corner distance of the two maps over a w x h texel rectangle."""
        return max(math.dist(self.apply(u, v), o.apply(u, v)) for u in (0, w) for v in (0, h))


ANCHOR_FRACTIONS = ((0, 0), (0.5, 0), (1, 0), (0, 0.5), (0.5, 0.5), (1, 0.5), (0, 1), (0.5, 1), (1, 1))


def sprite_matrix(flags: int, x: float, y: float, scale: tuple[float, float], angle: int,
                  pivot: tuple[float, float], pivot_zoom: tuple[float, float],
                  screen: tuple[float, float] = (0.0, 0.0), w: float = 0.0, h: float = 0.0,
                  origin9: tuple[float, float] = (0.0, 0.0)) -> Affine:
    """The quad VA 0xDD6D0 builds for one picture draw (entry VA 0xEB780(slot, flags, ...)):
    flags & 0xF anchor code (tables VA 0x2D5800 / 0x2D5830, 9 = point 0x2009154), 0x10 use
    the global scale 0xDC7BA4, 0x20 rotate by the global angle 0x2009150 (1/65536 turn),
    0x40 zoom by 0xDC7BAC about the global pivot 0x2009148 after the rotation; the global
    screen offset 0x1DBD3EC is added to (x, y)."""
    code = flags & 0xF
    if code == 9:
        ax, ay = origin9
    elif code < 9:
        fx, fy = ANCHOR_FRACTIONS[code]
        ax, ay = float(int(fx * w)), float(int(fy * h))
    else:
        ax = ay = 0.0
    sx, sy = scale if flags & 0x10 else (1.0, 1.0)
    m = Affine.translate(x + screen[0], y + screen[1])
    if flags & 0x20:
        # sincosf(-angle * 9.5873e-05f) in float32 (VA 0xDD76D..0xDD77E)
        m = m @ Affine.rotate(-_f32(float(-angle) * _f32(2 * math.pi / 65536)))
    m = m @ Affine.scale(sx, sy) @ Affine.translate(-ax, -ay)
    if flags & 0x40:
        px, py = pivot
        m = Affine.translate(px, py) @ Affine.scale(pivot_zoom[0], pivot_zoom[1]) @ Affine.translate(-px, -py) @ m
    return m


def canvas_to_frame(canvas: tuple[float, float] = CANVAS) -> Affine:
    """Canvas command VA 0xDCBE0(w, h, 0, 0): frame pixel = canvas unit * (1920/w, 1080/h)."""
    return Affine.scale(1920.0 / canvas[0], 1080.0 / canvas[1])


def _tween_remaining(tw: Sequence[int]) -> float:
    """VA 0x49390 / 0x528DC: fraction of a tween still to go: counter (Q12, counts down
    from 4096) -> ``1 - ease(id, p - 1, 0, 1, -1)`` = 1 - ease(1 - p) (0 when finished)."""
    eid, counter = int(tw[0]), int(tw[1])
    p = _f32(counter * _f32(1 / 4096))
    if not p > 0:
        return 0.0
    return _f32(1.0 - _f32(ease(eid, _f32(p - 1.0), 0.0, 1.0, -1.0)))


def zoom_tween(rec: dict) -> tuple[float, float]:
    """Current own zoom (per mille) of a record: end + delta * remaining (VA 0x528C0 /
    0x529F0 without the parent factor)."""
    r = _tween_remaining(rec["tw_zoom"])  # type: ignore[arg-type]
    zx = _f32(_f32(_f32(rec["zoom_dx"] / _f32(0.001)) * r) + rec["zoom_x"])  # type: ignore[operator]
    zy = _f32(_f32(_f32(rec["zoom_dy"] / _f32(0.001)) * r) + rec["zoom_y"])  # type: ignore[operator]
    return zx, zy


def _layer_ok(recs: Sequence[dict], p: int) -> bool:
    return 0 <= p < len(recs) and recs[p].get("used", 0) != 0


def layer_zoom(recs: Sequence[dict], l: int, catalog: Sequence) -> tuple[float, float]:
    """VA 0x528C0 / 0x529F0: zoom per mille including the parents: z * zp/1000 * class(parent)."""
    zx, zy = zoom_tween(recs[l])
    p = int(recs[l]["parent"])  # type: ignore[call-overload]
    if _layer_ok(recs, p):
        pzx, pzy = layer_zoom(recs, p, catalog)
        zx = _f32(zx * _f32(pzx * _f32(0.001)))
        zy = _f32(zy * _f32(pzy * _f32(0.001)))
        pic = int(recs[p]["pic"])  # type: ignore[call-overload]
        if 0 <= pic <= 0x39B6 and pic < len(catalog):
            fac = _class_factor(catalog[pic].scale_class)
            zx, zy = _f32(zx * fac), _f32(zy * fac)
    return zx, zy


def layer_rot(recs: Sequence[dict], l: int) -> float:
    """VA 0x4C3E0: rotation (1/100 degree) summed over the parent chain."""
    r = float(int(recs[l]["rot"]))  # type: ignore[call-overload]
    p = int(recs[l]["parent"])  # type: ignore[call-overload]
    return _f32(layer_rot(recs, p) + r) if 0 <= p < 40 and _layer_ok(recs, p) else r


def layer_world(recs: Sequence[dict], l: int, catalog: Sequence) -> tuple[float, float]:
    """VA 0x525B0 / 0x52740: position of the layer in canvas units.  A child's (x, y) is a
    point in its parent's picture: relative to the parent's anchor, times the size-class
    factor of the parent's picture (A 1, L 0.5, M 0.25, S 0.125), times the parent's zoom,
    rotated by the parent's rotation, plus the parent's position."""
    r = recs[l]
    x, y = float(r["x"]), float(r["y"])  # type: ignore[arg-type]
    p = int(r["parent"])  # type: ignore[call-overload]
    if not _layer_ok(recs, p):
        return x, y
    px, py = layer_world(recs, p, catalog)
    zx, zy = layer_zoom(recs, p, catalog)
    zx, zy = _f32(zx * _f32(0.001)), _f32(zy * _f32(0.001))
    th = _f32(_f32(_f32(layer_rot(recs, p) * _f32(math.pi)) / 180.0) / -100.0)
    pic = int(recs[p]["pic"])  # type: ignore[call-overload]
    fac = _class_factor(catalog[pic].scale_class) if 0 <= pic <= 0x39B6 and pic < len(catalog) else 1.0
    dx = _f32(_f32(_f32(x * fac) - float(recs[p]["anchor_x"])) * zx)  # type: ignore[arg-type]
    dy = _f32(_f32(float(recs[p]["anchor_y"]) - _f32(y * fac)) * zy)  # type: ignore[arg-type]
    c, s = math.cos(th), math.sin(th)
    return _f32(px + dx * c - dy * s), _f32(py - dx * s - dy * c)


def anchor_residual(rec: dict) -> tuple[float, float]:
    """Picture offset (+0x50 / +0x54, float canvas units) plus the integer anchor (+0x76 /
    +0x78): the part of the offset the draw adds **untransformed** (VA 0x519FF..0x51A1C).
    0 after every commit (offset = -anchor); motion fields 6 / 7 set anchor = trunc(X) and
    offset = -X (0xBE4C0, layers.md 8.3), which leaves trunc(X) - X in (-1, 1)."""
    return (_f32(float(rec["off_x"]) + float(int(rec["anchor_x"]))),  # type: ignore[arg-type, call-overload]
            _f32(float(rec["off_y"]) + float(int(rec["anchor_y"]))))  # type: ignore[arg-type, call-overload]


def layer_matrix(recs: Sequence[dict], l: int, catalog: Sequence,
                 extra: tuple[float, float] = (0.0, 0.0)) -> Affine:
    """Map from picture texels (u, v) of layer ``l`` to scene-canvas units, as the draw
    (VA 0x51060 -> 0xEB780 -> 0xDD6D0) computes it for a layer without shake / crop / mask.
    ``recs`` are decoded A records (``RenderRecord.f`` dicts) of all 40 layers.

    Scale, zoom and rotation act about the **integer anchor** (+0x76 / +0x78, read at
    0x5183D / 0x5186C); the anchor residual (offset + anchor, :func:`anchor_residual`) is
    added to the translation untransformed (0x51A1C), like SOXY, and ``extra`` (canvas
    units): the screen-effect offset 0xDF0720 / 0xDF0724 that 0x51060 adds at 0x51A13 unless
    byte 0xDF07C8 is set or the picture has catalog draw_flags bit 1, and the shake offsets
    (0x51A22..0x51B71), neither of which this model computes.  With the STZ4 pivot flag all
    of these move the picture before the screen-space zoom (the pivot does not move)."""
    r = recs[l]
    wx, wy = layer_world(recs, l, catalog)
    tw = _tween_remaining(r["tw_move"])  # type: ignore[arg-type]
    X = _f32(wx + float(r["dx"]) + _f32(float(r["move_dx"]) * tw))  # type: ignore[arg-type]
    Y = _f32(wy + float(r["dy"]) + _f32(float(r["move_dy"]) * tw))  # type: ignore[arg-type]
    zx, zy = layer_zoom(recs, l, catalog)
    s = _f32(float(r["scale"]) * _f32(0.001) + float(r["scale_cur"]))  # type: ignore[arg-type]
    # 0x51574..0x515F6: angle = 0x4C3E0(parent) + (rot + rot tween value); the DIRECT parent's
    # rotation is added whenever its index is 0..39, without the in-use test (+0x10) that
    # 0x4C3E0 applies to the parents above it and world / zoom apply (0x525F6, 0x52969)
    p = int(r["parent"])  # type: ignore[call-overload]
    own = float(int(r["rot"]) + int(r["rot_cur"]))  # type: ignore[call-overload]
    rot = _f32(layer_rot(recs, p) + own) if 0 <= p < len(recs) else own
    ang = int(_f32(_f32(rot * 32768.0) / 18000.0))
    th = ang * 2 * math.pi / 65536
    soxy = (float(r["soxy_x"]) * PX_X, float(r["soxy_y"]) * _f32(448.0 / 1080.0))  # type: ignore[arg-type]
    if int(r["zoom_pivot"]):  # type: ignore[call-overload]
        local = Affine.scale(s * PX_X, s * PX_Y)
        screen_zoom = Affine.scale(zx * 0.001, zy * 0.001)
    else:
        local = Affine.scale(s * zx * 0.001 * PX_X, s * zy * 0.001 * PX_Y)
        screen_zoom = Affine.scale(1, 1)
    # scale / zoom / rotation act about the integer anchor (canvas units at scale 1, applied
    # in texel space); the residual offset + anchor is a plain translation
    ax, ay = float(int(r["anchor_x"])), float(int(r["anchor_y"]))  # type: ignore[call-overload]
    res = anchor_residual(r)
    off = Affine.translate(-ax / PX_X, -ay / PX_Y)
    m = Affine.translate(X, Y) @ Affine.rotate(th) @ local @ off
    tx = soxy[0] + res[0] + float(extra[0])
    ty = soxy[1] + res[1] + float(extra[1])
    if int(r["zoom_pivot"]):  # type: ignore[call-overload]
        # 0x51FF5..0x52087: pivot = position + anchor - catalog anchor of the picture (equal
        # to the position unless STPV moved the anchor)
        pic = int(r["pic"])  # type: ignore[call-overload]
        ca = (catalog[pic].anchor_x, catalog[pic].anchor_y) if 0 <= pic < len(catalog) else (0, 0)
        px = _f32(X + ax - ca[0])
        py = _f32(Y + ay - ca[1])
        # SOXY, the residual and ``extra`` move the picture before this zoom (the pivot does not move)
        return Affine.translate(px, py) @ screen_zoom @ Affine.translate(-px + tx, -py + ty) @ m
    return Affine.translate(tx, ty) @ m


def steady_record(fields: dict, used: bool = True) -> dict:
    """A drawn record (dict as ``RenderRecord.f``) with no tween, motion or shake running,
    from ``commit()`` output (or any subset of render fields): what A holds once the commit
    has been moved to it and every tween has ended."""
    r: dict = {"used": 1 if used else 0, "face": 0, "pic": -1, "blend": 0, "flags": 0, "fx_mode": 0, "fx_a": 0,
               "fx_b": 0, "state": 0, "alpha": 1.0, "x": 0.0, "y": 0.0, "dx": 0.0, "dy": 0.0,
               "off_x": 0.0, "off_y": 0.0, "rot": 0, "scale": 1000, "anchor_x": 0, "anchor_y": 0,
               "rot_delta": 0, "rot_cur": 0, "tw_move": (0, 0, 0), "tw_scale": (0, 0, 0),
               "tw_rot": (0, 0, 0), "tw_alpha": (0, 0, 0), "tw_zoom": (0, 0, 0), "zoom_x": 1000,
               "zoom_y": 1000, "zoom_pivot": 0, "alpha_cur": 0.0, "scale_delta": 0.0, "scale_cur": 0.0,
               "zoom_dx": 0.0, "zoom_dy": 0.0, "move_dx": 0.0, "move_dy": 0.0, "alpha_delta": 0.0,
               "mask": -1, "parent": -1, "sttr": -1, "soxy_x": 0, "soxy_y": 0, "crop": (0, 0, 0, 0),
               "a16": 0, "a17": 0, "angle": 0, "motion": bytes(24), "stns": 0, "stns_b": 0,
               "colour": bytes((0, 0, 0, 0, 0, 255, 255, 255)) + bytes(9)}
    r.update(fields)
    return r


def frame_matrix(recs: Sequence[dict], l: int, catalog: Sequence, canvas: tuple[float, float] = CANVAS,
                 extra: tuple[float, float] = (0.0, 0.0)) -> Affine:
    """Picture texels -> 1920 x 1080 frame pixels (``extra``: see :func:`layer_matrix`)."""
    return canvas_to_frame(canvas) @ layer_matrix(recs, l, catalog, extra)


def tween_start(frames: int) -> tuple[int, int]:
    """Counter and per-frame step a timed STGS / STZ4 / STRT / STCL tween starts with
    (setters VA 0x4A620, 0x49930, 0x4A4B0, 0x83350): counter 0x1000 (Q12 = 1.0), step
    ``-trunc(4096 / frames)``; ``frames = trunc(ms * 60 / 1000)``; no tween for 0 frames."""
    if frames <= 0:
        return 0, 0
    return 0x1000, -(0x1000 // frames)


def tween_state(frames: int, n: int, per_update: int = 1) -> int:
    """Counter after ``n`` per-frame updates (VA 0x57B2D..0x57E50: counter += step x frames
    of the update, truncated; <= 0 ends the tween)."""
    c, st = tween_start(frames)
    for _ in range(n):
        if c <= 0 or st >= 0:
            return 0
        c = int(_f32(float(c) + _f32(float(st) * per_update)))
        if c <= 0:
            return 0
    return c


def tween_value(tw: Sequence[int]) -> float:
    """Remaining fraction of a tween slot (easing id, counter, step): 1 at the start, 0 at
    the end.  The drawn value is ``end + (start - end) * tween_value``."""
    return _tween_remaining(tw)


# ---------------------------------------------------------------------------
# 6. Motion application (VA 0xBE4C0).  Callers: STMA with dt = 0 (0xC04FB), the per-frame
#    update with dt = frames (0x57EED) and the move of B to A with dt = 0 (0x59957).
# ---------------------------------------------------------------------------
# keyframe field -> (enable mask in the per-motion flag word, layer field it drives)
KEY_FIELDS: dict[int, tuple[int, str]] = {
    1: (0, "easing id of the segment that starts at this key (ease())"),
    2: (0, "interpolation of that segment: 0 eased, 1 cubic spline, 2 step"),
    3: (0, "x (script units; drawn x = v * 640/480; scene x = trunc)"),
    4: (0, "y (script units; drawn y = v * 480/360)"),
    5: (0, "alpha 0..255 (drawn alpha = v/255)"),
    6: (0, "anchor x: (v * 640/480 + variant anchor difference) * size class; off_x = -anchor"),
    7: (0, "anchor y: (v * 480/360 + variant anchor difference) * size class"),
    8: (0, "rotation 1/100 degree (rot; the +0x15C angle only with flag bit 34)"),
    9: (0, "zoom x per mille"),
    10: (0, "zoom y per mille"),
    11: (0, "crop x (STCH a12)"),
    12: (0, "crop y (a13)"),
    13: (0, "crop w (a14)"),
    14: (0, "crop h (a15)"),
    15: (0, "a17 (step; max 0)"),
    16: (0, "a16 (max 0)"),
    17: (0, "blend mode (step)"),
    18: (0, "colour key (step): value 1 sets the colour of fields 19..21; the mode is not written"),
    19: (0, "colour R (step)"),
    20: (0, "colour G (step)"),
    21: (0, "colour B (step)"),
    22: (0, "variant digit hh of the picture name (step): switches the picture to <base>_hh_vv"),
    23: (0, "variant digit vv (step)"),
    24: (0, "colour-effect parameter A = SNX2 (step)"),
    25: (0, "colour-effect parameter B = SNX3 (step)"),
    26: (0, "noise mode = STNS (step)"),
    27: (0, "z / draw order (step)"),
}

# enable bits per field: the literal masks the code tests (single bits confirmed under
# emulation, motion_masks.py).  Fields 11/13 and 12/14 share a mask.  The found-scans of
# fields 3 and 4 clear their whole mask, which includes bit 7 (also in the 11/13 mask) and
# bit 26 (also in 12/14).
FIELD_MASKS: dict[int, int] = {
    3: 0x40880, 4: 0x5100000, 5: 0x4000, 6: 0x10002, 7: 0x20004, 8: 0x88000,
    9: 0x80200000, 10: 0x100400000, 11: 0x10000080, 12: 0x4000400, 13: 0x10000080,
    14: 0x4000400, 15: 0x10, 16: 0x20, 17: 0x2000000, 18: 0x200, 19: 0x200000000,
    20: 0x200000000, 21: 0x200000000, 22: 0x20000000, 23: 0x40000000, 24: 0x40, 25: 0x100,
    26: 0x1000, 27: 0x1,
}
ANGLE_BIT = 1 << 34                         # 0xBF6B8: field 8 also drives the +0x15C angle
FOUND_FIELDS = (3, 4, 5, 22, 23, 9, 10)     # the fields with a found-scan (motion_found)
for _f, _m in FIELD_MASKS.items():
    KEY_FIELDS[_f] = (_m, KEY_FIELDS[_f][1])


def motion_frames(frames: int, divisor: int) -> int:
    """VA 0xBA8B0: the record's frame counter quantised by the STMA divisor (a3, signed i16):
    ``frames - frames % divisor`` (C remainder) for ``divisor >= 2``, else ``frames``.  The
    motion keeps its normal speed and advances in steps of ``divisor`` frames; it is **not**
    slowed down [V-emu: probe_div, apply_check]."""
    if divisor >= 2:
        r = abs(frames) % divisor * (1 if frames >= 0 else -1)
        return frames - r
    return frames


def motion_time_ms(frames: int, divisor: int) -> float:
    """VA 0xBE5A8: motion time in ms = ``motion_frames(frames, divisor) * 50 / 3`` (float32)."""
    return _f32(_f32(float(motion_frames(frames, divisor)) * 50.0) / 3.0)


def motion_clear(rec: bytes) -> bytes:
    """VA 0xBA800 on a 24-byte motion record: id = divisor = 0, frames = 0, pause = 0
    (bytes +4..+7 and +0x11.. are not touched)."""
    b = bytearray(rec)
    b[0:4] = bytes(4)
    b[8:17] = bytes(9)
    return bytes(b)


def motion_copy(dst: bytes, src: bytes) -> bytes:
    """VA 0xBA8E0(dst, src): id, divisor and pause byte are copied; the frame counter only
    when the source id is > 0 and the destination counter is not larger."""
    d = bytearray(dst)
    d[0:2] = src[0:2]
    if (struct.unpack_from("<q", d, 8)[0] <= struct.unpack_from("<q", src, 8)[0]
            and struct.unpack_from("<h", src, 0)[0] > 0):
        d[8:16] = src[8:16]
    d[2:4] = src[2:4]
    d[16] = src[16]
    return bytes(d)


def motion_record(rec: bytes, mid: int, n: int) -> bytes:
    """Record change of ``_STMA(l, mid, w, n)`` (VA 0xC0330), made to the scene record and to
    the drawn record.  ``mid`` (the 32-bit argument) > 0: id = i16(mid) and pause = 0
    (0xBA820), frames = 0 (0xBA830), divisor = n if i16(n) > 0 else 0 (0xBA840).  Otherwise
    (absent, 0, negative): only id = 0 and pause = 0; divisor and frame counter are kept."""
    b = bytearray(rec)
    struct.pack_into("<h", b, 0, _i16(mid) if mid > 0 else 0)
    b[16] = 0
    if mid > 0:
        struct.pack_into("<q", b, 8, 0)
        struct.pack_into("<h", b, 2, max(_i16(n), 0))
    return bytes(b)


def edit_motion(T: MotionTable, rec: dict, mid: int, key: int = 0) -> Optional[int]:
    """``_SGMA(l, mid, key)`` (VA 0xC23B0): write the layer's current drawn state (record
    ``rec``) into keyframe ``key`` of motion ``mid`` in the (in-memory, mutable) table, so that
    a following ``_STMA`` starts from where the layer is.  Index = first key of the motion
    (``end(i16(mid - 1))``, 0 for an invalid id) + key.  Written: field 3 = x * 480 / 640 and
    field 4 = y * 360 / 480 (x * 480 and y * 360 truncated in float32, then divided by 640 /
    480 rounding away from zero), field 5 = trunc((alpha + alpha tween value) * 255), field 8 =
    rot, fields 9/10 = zoom, fields 11..14 = crop only when crop x != 0.  Returns the keyframe
    index written (None when the statement does nothing)."""
    if mid <= 0:
        return None
    k = _i16(mid - 1)
    m = T.motions.get(k)
    i = _i32((m.end if m is not None and 1 <= k <= 24192 else 0) + key)
    if not 0 <= i < len(T.times):
        return None

    def away(v: int, d: int) -> int:                # trunc(v / d) + (v % d != 0 ? sign(v) : 0)
        q = _cdiv(v, d)
        return q if v - q * d == 0 else q + (1 if v >= 0 else -1)

    xi = int(_f32(float(rec["x"]) * 480.0))
    yi = int(_f32(float(rec["y"]) * 360.0))
    T.set_val(i, 3, _i16(away(xi, 640)))
    T.set_val(i, 4, _i16(away(yi, 480)))
    T.set_val(i, 5, _i16(int(_f32(_f32(float(rec["alpha"]) + float(rec["alpha_cur"])) * 255.0))))
    T.set_val(i, 8, int(rec["rot"]))  # type: ignore[call-overload]
    T.set_val(i, 9, _i16(int(rec["zoom_x"])))  # type: ignore[call-overload]
    T.set_val(i, 10, _i16(int(rec["zoom_y"])))  # type: ignore[call-overload]
    crop = rec["crop"]
    if _i16(int(crop[0])) != 0:  # type: ignore[index]
        for f, v in zip((11, 12, 13, 14), crop):  # type: ignore[call-overload]
            T.set_val(i, f, _i16(int(v)))
    return i


def motion_keytime(T: MotionTable, m: Motion, t: float, loops: int = 0) -> Optional[float]:
    """Time used for the keys (VA 0xBE5D3..0xBE63E): None before the first key (nothing
    applied); clamped to the duration, or wrapped for looping motions (``loops`` =
    VA 0xC1C70(id): 0 = no loop)."""
    if float(T.val(m.start, 0)) > t:
        return None
    dur = float(m.duration)
    if t > dur:
        if loops == 0:
            return dur
        span = m.duration if (m.duration >= 3 or m.duration > loops) else 2
        lo = loops if m.duration > loops else 1
        if t >= float(span):
            it = int(t)
            frac = _f32(t - float(it))
            if lo == 1:                          # 0xBED8F: idiv by the span
                return _f32(float(it % span) + frac)
            return _f32(float(((it - lo) % (span - lo)) + lo) + frac)
    return t


def motion_found(T: MotionTable, m: Motion, k: int, f: int) -> bool:
    """The found-scan (VA 0xBE770..0xBE7D0, same code for each field of
    :data:`FOUND_FIELDS`): true if some key with a non-zero time defines field ``f`` (any
    value but -32767 / -100001; HOLD counts), or the current key ``k`` has time 0.  When it is
    false the field's whole mask is cleared from the flag word."""
    found = False
    for i in range(m.start, m.end):
        if T.val(i, 0) == 0:
            found = found or i == k
            continue
        v = T.val(i, f)
        if v not in (UNSET, UNSET8):
            return True
    return found


class PictureNames:
    """Picture names in catalog order with the engine's name read (VA 0x82BD0: the 32-byte
    entry of the table at data VA 0x7F6800, NUL-terminated at byte 32) and name lookup
    (VA 0x82CE0 -> 0x78410: upper case up to the first '.', 32 bytes; -1 if unknown)."""

    def __init__(self, names: Sequence[str]) -> None:
        self.names = list(names)
        self.index = {n.upper(): i for i, n in enumerate(self.names)}

    def name(self, pic: int) -> str:
        if not 0 <= pic < len(self.names):
            return ""
        n = self.names[pic]
        return n if len(n) < 30 else (n.ljust(30, "\0") + "\r\n")[:32]

    def lookup(self, name: str) -> int:
        return self.index.get(name.split(".", 1)[0][:32].upper(), -1)


_NAMES_CACHE: dict[int, tuple[object, PictureNames]] = {}


def _picture_names(names: Union[None, Sequence[str], PictureNames]) -> Optional[PictureNames]:
    if names is None or isinstance(names, PictureNames):
        return names
    hit = _NAMES_CACHE.get(id(names))
    if hit is None or hit[0] is not names or len(hit[1].names) != len(names):
        hit = (names, PictureNames(names))
        _NAMES_CACHE[id(names)] = hit
    return hit[1]


def _variant_digits(n: str) -> tuple[int, int]:
    """(hh, vv) of a ``<base>_hh_vv`` picture name (VA 0xBEB9C..0xBEC2E; STCH 0x81900): length
    >= 7, '_' at len-6 and len-3, prefix ``IMG`` or length >= 15; digits not validated."""
    if len(n) >= 7 and n[-6] == "_" and n[-3] == "_" and (n[:3] == "IMG" or len(n) >= 15):
        return _digits2(n[-5:-3]), _digits2(n[-2:])
    return 0, 0


def _variant_name(name: str, hh: int, vv: int, nh: int, nv: int) -> str:
    """VA 0xBEE91..0xBF03F: the picture name for new digits (nh, nv).  (0, 0) cuts the
    ``_hh_vv`` suffix off; a name without digits gets ``_00_00`` appended; then the units
    digits are written, and the tens digit only for values >= 10 (a quirk: 12 -> 5 gives 15)."""
    b = bytearray(name.encode("latin-1"))
    if nh | nv == 0:
        return b[:len(b) - 6].decode("latin-1")
    if hh | vv == 0:
        b += b"_00_00"
    n = len(b)
    b[n - 4] = 48 + nh % 10
    if nh >= 10:
        b[n - 5] = 48 + nh // 10 % 10
    b[n - 1] = 48 + nv % 10
    if nv >= 10:
        b[n - 2] = 48 + nv // 10 % 10
    return b.decode("latin-1")


def _cat_anchor(catalog: Sequence, pic: int) -> tuple[int, int]:
    """Catalog anchor (+0x34 / +0x38) of a picture; id -1 reads the bytes before the table,
    whose low halves are (256, 255)."""
    if 0 <= pic < len(catalog):
        return int(catalog[pic].anchor_x), int(catalog[pic].anchor_y)
    return 256, 255


def _scale_class(catalog: Sequence, pic: int) -> int:
    """VA 0x41BA0: catalog +0x42 for ids 0..0x39B6, else 0."""
    return int(catalog[pic].scale_class) if 0 <= pic <= 0x39B6 and pic < len(catalog) else 0


def _i8(v: int) -> int:
    v &= 0xFF
    return v - 0x100 if v & 0x80 else v


def colour_block(block: bytes, rgb: int, rgb2: int = 0, par: int = 0) -> bytes:
    """VA 0xF7A0 on a drawn colour block (record +0x1F0, 17 bytes): +0 i16 = 0, +2 i16 = par,
    the old +5..+7 / +8..+0xA move to +0xB..+0xD / +0xE..+0x10, then +5..+7 = rgb and
    +8..+0xA = rgb2 (bytes R, G, B)."""
    b = bytearray(block.ljust(17, b"\0"))
    b[0:2] = bytes(2)
    struct.pack_into("<h", b, 2, _i16(par))
    b[0xB:0xE] = b[5:8]
    b[5:8] = bytes(((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF))
    b[0xE:0x11] = b[8:0xB]
    b[8:0xB] = bytes(((rgb2 >> 16) & 0xFF, (rgb2 >> 8) & 0xFF, rgb2 & 0xFF))
    return bytes(b)


def _ext_colour(scene: SceneState, layer: int, rgb: int) -> None:
    """VA 0xFA90(ext + 6, rgb, 0): scene ext bytes +6..+8 = R, G, B and +9..+0xB = 0."""
    scene.set_ext(layer, EXT_COLOUR, "6B", (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, 0, 0, 0)


def apply_motion(T: MotionTable, rec: dict, layer: int, dt: float, catalog: Sequence,
                 scene: Optional[SceneState] = None, loops: int = 0,
                 names: Union[None, Sequence[str], PictureNames] = None) -> Optional[float]:
    """One call of VA 0xBE4C0 for ``layer`` on its drawn record ``rec`` (``RenderRecord.f``
    dict, updated in place; ``rec['motion']`` is the 24-byte motion record): advance the
    motion by ``dt`` frames, evaluate the enabled fields at the new time and write them to the
    record and, like the engine, to ``scene`` (scene writes need a scene picture, except the
    motion time and the colour key, see the code).  ``names`` (picture names or
    :class:`PictureNames`) enables the picture-variant fields 22/23 and the variant anchor
    difference of fields 6/7; without it they are skipped.  Without ``scene`` the size class
    of fields 6/7 is taken from ``rec['pic']``.  Returns the key time used (ms) or None when
    nothing was applied.  Pseudo-fields set in ``rec``: ``colour_rgb`` (last colour set),
    ``z_order`` (the draw-order slot VA 0xDE2538 + 4*layer)."""
    if not 0 <= layer <= 39:
        return None
    mrec = bytearray(rec["motion"])  # type: ignore[arg-type]
    mid, div = struct.unpack_from("<hh", mrec, 0)
    m = T.get(mid)
    if mid <= 0 or m is None or m.end <= m.start or m.duration <= 0:
        return None
    idt = int(dt)
    if idt and not mrec[16]:                    # VA 0xBA860: advance unless paused
        frames = struct.unpack_from("<q", mrec, 8)[0] + idt
        struct.pack_into("<q", mrec, 8, frames if frames >= 2 else 1)
        rec["motion"] = bytes(mrec)
    qf = motion_frames(struct.unpack_from("<q", mrec, 8)[0], div)
    t0 = _f32(_f32(float(qf) * 50.0) / 3.0)
    t = motion_keytime(T, m, t0, loops)
    if t is None:
        return None
    if scene is not None:                       # 0xBE663: the scene record keeps the quantised time
        sm = bytearray(scene.get("motion", layer))  # type: ignore[arg-type]
        struct.pack_into("<q", sm, 8, qf)
        scene.set("motion", layer, bytes(sm))
    whole = mid == 9999
    if whole:                                   # 0xBE676..0xBE6DE: ease the whole time line
        eid = T.val(m.start, 1)
        last = float(T.val(m.end - 1, 0))
        t = _f32(ease(0 if eid in (UNSET, HOLD) else eid, t, 0.0, last, last))
    k = next((i for i in range(m.start, m.end) if float(T.val(i, 0)) >= t), None)
    if k is None:
        return None
    s, e = m.start, m.end - 1
    flags = T.flags(mid)
    pn = _picture_names(names)
    tt: float = t

    def val(f: int, cur: float) -> float:
        return motion_value(T, f, s, e, k, tt, cur, whole)

    def spic() -> int:
        return int(scene.get("pic", layer)) if scene is not None else -1  # type: ignore[call-overload]

    def sput(name: str, v: Any) -> None:        # scene writes test the scene picture each time
        if scene is not None and spic() != -1:
            scene.set(name, layer, v)

    def found(f: int) -> bool:
        return motion_found(T, m, k, f)         # type: ignore[arg-type]

    # 3, 4: position (0xBE7D2.., 0xBE912..)
    for f, key, num, den in ((3, "x", 640.0, 480.0), (4, "y", 480.0, 360.0)):
        if not found(f):
            flags &= ~FIELD_MASKS[f]
        if flags & FIELD_MASKS[f]:
            v = val(f, _f32(_f32(den * float(rec[key])) / num))
            rec[key] = _f32(_f32(v * num) / den)
            sput(key, int(rec[key]))
    # 5: alpha (0xBEA47..); the running alpha-tween value is folded in only when dt == 0
    if not found(5):
        flags &= ~FIELD_MASKS[5]
    if flags & FIELD_MASKS[5]:
        a = float(rec["alpha"])
        if float(dt) == 0.0:
            a = _f32(a + float(rec["alpha_cur"]))
            rec["alpha_cur"] = 0.0
        v = val(5, _f32(a * 255.0))
        c = 0.0 if 0.0 > v else (v if v <= 255.0 else 255.0)
        rec["alpha"] = _f32(c / 255.0)
        sput("alpha", int(c))
    # 22, 23: picture variant (0xBEB37..0xBF314); the name comes from the scene picture at
    # t == 0 and from the drawn picture otherwise
    src_pic = int(rec["pic"])  # type: ignore[call-overload]
    if t == 0.0 and scene is not None:
        src_pic = spic()
    name = pn.name(src_pic) if pn is not None and src_pic >= 0 else ""
    hh, vv = _variant_digits(name)
    for f in (22, 23):
        if not found(f):
            flags &= ~FIELD_MASKS[f]
    if flags & 0x60000000 and int(rec["pic"]) != -1 and pn is not None:  # type: ignore[call-overload]
        nh = max(int(val(22, float(hh))), 0) if flags & FIELD_MASKS[22] else hh
        nv = max(int(val(23, float(vv))), 0) if flags & FIELD_MASKS[23] else vv
        if (nh, nv) != (hh, vv):
            n2 = _variant_name(name, hh, vv, nh, nv)
            nid = pn.lookup(n2)
            if nid >= 0:
                if scene is not None:
                    sp = spic()
                    if t == 0.0 or int(rec["pic"]) == sp:  # type: ignore[call-overload]
                        ca = _cat_anchor(catalog, nid)
                        if sp == -1:
                            ax, ay = ca
                        else:
                            cs = _cat_anchor(catalog, sp)
                            ax = int(scene.get("anchor_x", layer)) + ca[0] - cs[0]  # type: ignore[call-overload]
                            ay = int(scene.get("anchor_y", layer)) + ca[1] - cs[1]  # type: ignore[call-overload]
                        scene.set("anchor_x", layer, ax)
                        scene.set("anchor_y", layer, ay)
                        scene.set("pic", layer, nid)
                        scene.set_ext(layer, EXT_OFF_X, "hh", _i16(-ax), _i16(-ay))
                if t > 0.0:                     # 0xBF144..: the drawn record switches only for t > 0
                    ax, ay = _cat_anchor(catalog, nid)
                    rec["pic"] = nid
                    rec["anchor_x"], rec["anchor_y"] = _i16(ax), _i16(ay)
                    rec["off_x"], rec["off_y"] = float(-_i16(ax)), float(-_i16(ay))
                    rec["face"] = 1 if 0 <= nid < len(catalog) and catalog[nid].name1 else 0
                name, hh, vv = n2, nh, nv
    # anchor difference of a variant picture to its base (0xBF31B..0xBF389)
    diff = (0, 0)
    if (hh | vv) and pn is not None:
        bid = pn.lookup(name[:len(name) - 6])
        a1, b1 = _cat_anchor(catalog, int(rec["pic"])), _cat_anchor(catalog, bid)  # type: ignore[call-overload]
        diff = (_i16(a1[0] - b1[0]), _i16(a1[1] - b1[1]))
    # 6, 7: anchor (0xBF391.., 0xBF4EA..); no found-scan
    for f, key, num, den, d, ext in ((6, "x", 640.0, 480.0, diff[0], EXT_OFF_X),
                                     (7, "y", 480.0, 360.0, diff[1], EXT_OFF_Y)):
        if flags & FIELD_MASKS[f]:
            cur = float(_cdiv(int(rec["anchor_" + key]) * int(den), int(num)))  # type: ignore[call-overload]
            v = val(f, cur)
            x = _f32(_f32(_f32(v * num) / den) + float(d))
            sp = spic() if scene is not None else int(rec["pic"])  # type: ignore[call-overload]
            if sp != -1:
                x = _f32(x * _class_factor(_scale_class(catalog, sp)))
            rec["anchor_" + key] = _i16(int(x))
            rec["off_" + key] = -x
            if scene is not None and spic() != -1:
                scene.set("anchor_" + key, layer, int(x))
                scene.set_ext(layer, ext, "h", _i16(int(-x)))
    # 8: rotation (0xBF648); the +0x15C angle only with flag bit 34 (0xBF6B8)
    if flags & FIELD_MASKS[8]:
        v = val(8, _f32(float(int(rec["rot"]))))  # type: ignore[call-overload]
        rec["rot"] = int(v)
        sput("rot", int(v))
    if flags & ANGLE_BIT:
        v = val(8, _f32(float(int(rec["rot"]))))  # type: ignore[call-overload]
        rec["angle"] = _i16(int(v))
        sput("angle", _i16(int(v)))
    # 9, 10: zoom.  0xBF724..0xBF754 first ends the zoom tween (delta 0, counter 0) on every
    # call; a fractional value is stored rounded away from zero with the fraction in that
    # tween (delta = (v - zoom) * 0.001, counter 4096), so the drawn zoom is exactly v
    rec["zoom_dx"] = rec["zoom_dy"] = 0.0
    rec["tw_zoom"] = (rec["tw_zoom"][0], 0, rec["tw_zoom"][2])  # type: ignore[index]
    for f in (9, 10):
        if not found(f):
            flags &= ~FIELD_MASKS[f]
    for f, key, dkey in ((9, "zoom_x", "zoom_dx"), (10, "zoom_y", "zoom_dy")):
        if flags & FIELD_MASKS[f]:
            v = val(f, float(int(rec[key])))  # type: ignore[call-overload]
            zi = int(v)
            if float(_i16(zi)) != v:
                zi += 1 if v >= 0.0 else -1
                rec[dkey] = _f32(_f32(v - float(_i16(zi))) * _f32(0.001))
                rec["tw_zoom"] = (rec["tw_zoom"][0], 4096, rec["tw_zoom"][2])  # type: ignore[index]
            rec[key] = _i16(zi)
            sput(key, _i16(zi))
    # 11..14: crop rectangle (0xBFA7A..); cur = the scene value when it differs from the drawn one
    for mask_f, pair in ((11, ((11, 0), (13, 2))), (12, ((12, 1), (14, 3)))):
        if not flags & FIELD_MASKS[mask_f]:
            continue
        for f, j in pair:
            crop = list(rec["crop"])  # type: ignore[call-overload]
            cur = crop[j]
            if scene is not None and spic() != -1:
                sv = scene.get("crop", layer)[j]  # type: ignore[index]
                cur = crop[j] if sv == crop[j] else sv
            crop[j] = _i16(int(val(f, float(cur))))
            rec["crop"] = tuple(crop)
            if scene is not None and spic() != -1:
                sc = list(scene.get("crop", layer))  # type: ignore[call-overload]
                sc[j] = crop[j]
                scene.set("crop", layer, tuple(sc))
    # 15, 16: a17 / a16 (max 0)
    if flags & FIELD_MASKS[15]:
        v = max(0.0, val(15, float(_i8(int(rec["a17"])))))  # type: ignore[call-overload]
        rec["a17"] = int(v) & 0xFF
        sput("a17", int(v) & 0xFF)
    if flags & FIELD_MASKS[16]:
        v = max(0.0, val(16, float(_i16(int(rec["a16"])))))  # type: ignore[call-overload]
        rec["a16"] = int(v) & 0xFFFF
        sput("a16", int(v) & 0xFFFF)
    # 17: blend (written when it changes)
    if flags & FIELD_MASKS[17]:
        iv = int(val(17, float(_i16(int(rec["blend"])))))  # type: ignore[call-overload]
        if _i16(int(rec["blend"])) != iv:  # type: ignore[call-overload]
            rec["blend"] = _i16(iv)
            sput("blend", _i16(iv))

    def rgb() -> int:
        return ((int(val(19, 0.0)) << 16) + (int(val(20, 0.0)) << 8) + int(val(21, 0.0))) & 0xFFFFFFFF

    # 18: colour key (0xBFDF0..): value 1 sets the colour; the scene colour is set when the
    # scene fx_mode byte is not 0xFF (no picture test)
    if flags & FIELD_MASKS[18] and int(val(18, float(_i8(int(rec["fx_mode"]))))) == 1:  # type: ignore[call-overload]
        c = rgb()
        rec["colour"] = colour_block(rec.get("colour", bytes(17)), c)  # type: ignore[arg-type]
        rec["colour_rgb"] = c & 0xFFFFFF
        if scene is not None and int(scene.get("fx_mode", layer)) & 0xFF != 0xFF:  # type: ignore[call-overload]
            _ext_colour(scene, layer, c)
    # 19..21: colour (bit 33, 0xBFF57..)
    if flags & FIELD_MASKS[19]:
        c = rgb()
        rec["colour"] = colour_block(rec.get("colour", bytes(17)), c)  # type: ignore[arg-type]
        rec["colour_rgb"] = c & 0xFFFFFF
        if scene is not None and spic() != -1:
            _ext_colour(scene, layer, c)
    # 24, 25: colour-effect parameters; 26: noise
    if flags & FIELD_MASKS[24]:
        iv = int(val(24, float(_i16(int(rec["fx_a"])))))  # type: ignore[call-overload]
        rec["fx_a"] = _i16(iv)
        sput("fx_a", _i16(iv))
    if flags & FIELD_MASKS[25]:
        iv = int(val(25, float(_i8(int(rec["fx_b"])))))  # type: ignore[call-overload]
        if _i8(int(rec["fx_b"])) != iv:  # type: ignore[call-overload]
            rec["fx_b"] = _i8(iv)
            sput("fx_b", _i8(iv))
    if flags & FIELD_MASKS[26]:
        iv = int(val(26, float(_i8(int(rec["stns"])))))  # type: ignore[call-overload]
        if _i8(int(rec["stns"])) != iv:  # type: ignore[call-overload]
            nb = 20 if iv < 3 else 12
            rec["stns"], rec["stns_b"] = iv & 0xFF, nb
            sput("stns", iv & 0xFF)
            sput("stns_b", nb)
    # 27: z order (only when v != 0 and the scene z is not 0)
    if flags & FIELD_MASKS[27] and scene is not None:
        v = val(27, 0.0)
        if v != 0.0 and int(scene.get("z", layer)) != 0:  # type: ignore[call-overload]
            rec["z_order"] = int(v)
            sput("z", int(v))
    return t


# ---------------------------------------------------------------------------
# 7. Composition helper (reference renderer for checks; normal "over" blending only).
# ---------------------------------------------------------------------------
def compose_frame(draws: Sequence[tuple], size: tuple[int, int] = (1920, 1080)) -> "object":
    """Composite pictures into a frame.  ``draws``: (rgba uint8 array H x W x 4, Affine
    texel -> frame pixel, alpha 0..1, crop or None) in draw order (back to front).  ``crop``
    = (x, y, w, h, feather) in texels: only that rectangle of the picture is shown, with a
    linear alpha ramp of ``feather`` texels inside its border [H: shape of the engine's
    crop pass].  Blend modes (STBR) and colour effects are not applied.  Returns a PIL
    RGBA image."""
    import numpy as np
    from PIL import Image
    W, H = size
    acc = np.zeros((H, W, 3), np.float32)
    acc_a = np.zeros((H, W), np.float32)
    for rgba, m, alpha, crop in draws:
        if alpha <= 0:
            continue
        src = np.asarray(rgba, np.float32) / 255.0
        if crop is not None:
            cx, cy, cw, ch, fe = crop
            hh, ww = src.shape[:2]
            ys, xs = np.mgrid[0:hh, 0:ww].astype(np.float32)
            dx = np.minimum(xs - cx, cx + cw - 1 - xs)
            dy = np.minimum(ys - cy, cy + ch - 1 - ys)
            d = np.minimum(dx, dy)
            mask = np.clip((d + 1) / max(1.0, float(fe)), 0, 1) if fe > 0 else (d >= 0).astype(np.float32)
            src = src.copy()
            src[..., 3] *= mask
        pre = src.copy()
        pre[..., :3] *= pre[..., 3:4]
        det = m.a * m.e - m.b * m.d
        if abs(det) < 1e-12:
            continue
        ia, ib, id_, ie = m.e / det, -m.b / det, -m.d / det, m.a / det
        ic = -(ia * m.c + ib * m.f)
        if_ = -(id_ * m.c + ie * m.f)
        layers = []
        for ch in range(4):
            im = Image.fromarray(pre[..., ch], mode="F")
            layers.append(np.asarray(im.transform((W, H), Image.Transform.AFFINE, (ia, ib, ic, id_, ie, if_),
                                                  resample=Image.Resampling.BILINEAR), np.float32))
        a = layers[3] * alpha
        for ch in range(3):
            acc[..., ch] = layers[ch] * alpha + acc[..., ch] * (1 - a)
        acc_a = a + acc_a * (1 - a)
    out = np.zeros((H, W, 4), np.uint8)
    safe = np.maximum(acc_a, 1e-6)
    out[..., :3] = np.clip(acc / safe[..., None] * 255 + 0.5, 0, 255).astype(np.uint8)
    out[..., 3] = np.clip(acc_a * 255 + 0.5, 0, 255).astype(np.uint8)
    return Image.fromarray(out, "RGBA")


def load_motion_loops(path: Union[str, Path, None] = None) -> dict[int, int]:
    """Loop point per motion id (VA 0xC1C70, a compiled switch; 0 = no loop): runs
    ``[first id, last id, value]`` extracted by emulating the function for every id
    (``samples/misc_layer_state_and_motions/motion_loops.json``; 2,027 looping motions)."""
    import json
    p = Path(path) if path is not None else Path(__file__).resolve().parents[2] /         "samples" / "misc_layer_state_and_motions" / "motion_loops.json"
    out: dict[int, int] = {}
    for a, b, v in json.loads(p.read_text()):
        for i in range(a, b + 1):
            out[i] = v
    return out


def _main(argv: Optional[Sequence[str]] = None) -> int:
    import argparse
    ap = argparse.ArgumentParser(description="TSUKIHIME scenario layer model (docs/formats/layers.md)")
    ap.add_argument("--arrays", action="store_true", help="print the scene_state array table")
    ap.add_argument("--motion", type=int, help="print the keyframes and sampled values of a motion")
    ap.add_argument("--eboot", default=None, help="eboot.bin (default: game folder)")
    a = ap.parse_args(argv)
    if a.arrays:
        for arr in SCENE_ARRAYS:
            print(f"+{arr.off:#06x} {arr.fmt:>5s} x40  {arr.name:12s} default {arr.default!r:10.10s}  "
                  f"{arr.writers}  -> {arr.commit}")
    if a.motion is not None:
        from . import game_dir
        gd = game_dir()
        if a.eboot is None and gd is None:
            print("game folder not found (set TSUKI_GAME_DIR or pass --eboot)")
            return 1
        eb = Path(a.eboot) if a.eboot else gd / "eboot.bin"  # type: ignore[operator]
        T = MotionTable(eb)
        m = T.get(a.motion)
        if m is None:
            print("no such motion (ids 1..24192)")
            return 1
        print(f"motion {m.id}: duration {m.duration} ms, keys {m.start}..{m.end - 1}, flags {T.flags(m.id):#x}")
        for i in range(m.start, m.end):
            print("  t=%5d " % T.val(i, 0) + " ".join(f"{f}:{T.val(i, f)}" for f in range(1, 28)
                                                    if T.val(i, f) not in (UNSET, UNSET8)))
        for frac in (0.0, 0.25, 0.5, 0.75, 1.0):
            t = _f32(m.duration * frac)
            k = next((i for i in range(m.start, m.end) if T.val(i, 0) >= t), m.end - 1)
            vals = {f: round(motion_value(T, f, m.start, m.end - 1, k, t, 0.0), 3) for f in range(3, 28)
                    if T.flags(m.id) & FIELD_MASKS[f] and (f not in FOUND_FIELDS or motion_found(T, m, k, f))}
            print(f"  t={t:7.1f} ms: {vals}")
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
