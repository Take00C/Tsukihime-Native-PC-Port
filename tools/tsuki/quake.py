"""Quake / shake / swing (layer slots), MSQK message-window quake and SHOK screen shock:
the per-frame formulas of the PS4 engine and the libc ``rand()`` they draw from.

Reference: docs/formats/quake.md.  Every address is a text VA of CUSA47999 app 01.01.

The engine computes all of this in float32 (SSE scalar ops, one rounding per operation); this
module reproduces that bit for bit by rounding to float32 after every operation (``_f``).
``sinf`` is the game's own libc.prx routine (FreeBSD msun ``s_sinf.c`` with double kernels),
ported exactly as ``ps4_sinf``; it differs from a correctly rounded float sine by 1 ulp for
some arguments (quake.md 2.5), so ``math.sin`` / ``MathF.Sin`` are not bit-exact.

Components
----------
``Ps4Rand``          libc.prx ``rand()`` / ``srand()`` (64-bit LCG, 30-bit output, state 1 at boot).
``Rnd2``             the engine's second generator 0xE8200 (state 0xDC7BF0) used by SHK2 flicker.
``rnd_range``        0xC2F90: uniform value in [lo, hi] on a 2^-20 grid (MSQK).
``ShakeSlot``        one per-layer slot of the drawn record (slot 0 quake, slot 1 shake / swing).
``ShakeLayers``      40 layers x 2 slots: start (0x84670), stop (0x847F0), STCH / SCH2 reset
                     (0x4A770), per-frame update (0x57F4D..0x5889A inside 0x57980), draw terms:
                     slot offsets (0x51A22..0x51B71) and the mask-layer term (0x516DE..0x51746
                     -> 0x49A60).
``ps4_sinf``         libc.prx sinf (libc VA 0x4AF10), bit-exact.
``sqk2_start`` ...   argument decoding of _SQK2 / _SHK2 / _SSW2 (0xB6B20 / 0xB6FA0 / 0xB7400).
``MessageQuake``     _MSQK (0x53BE0 start; update at the top of 0x57980), offsets for the HUD.
``ScreenShock``      _SHOK (0x7FC80 -> 0x438C0; update 0x439D0 kind 1), offsets for every layer.
"""
from __future__ import annotations

import math
import struct
from dataclasses import dataclass, field
from typing import Callable, Iterable, Optional, Sequence

__all__ = [
    "F32_2PI", "F32_INV3", "F32_Y_CANVAS", "F32_2M30", "F32_Y_PX", "F32_SHOCK_Y_MASK",
    "Ps4Rand", "Rnd2", "RND2_INIT",
    "title_seed", "rnd_range", "frames", "ShakeSlot", "ShakeLayer", "ShakeLayers",
    "SlotStart", "sqk2_start", "shk2_start", "ssw2_start", "stop_layers", "sqk2_targets",
    "shk2_targets", "MessageQuake", "ScreenShock", "f32", "cvtt", "i16", "ps4_sinf",
    "sinf_correctly_rounded",
]

M64 = (1 << 64) - 1


# ---------------------------------------------------------------------------
# float32 helpers
# ---------------------------------------------------------------------------
_PK = struct.Struct("<f")


def f32(x: float) -> float:
    """Round a Python float to the nearest float32 (overflow -> +-inf)."""
    try:
        return _PK.unpack(_PK.pack(x))[0]
    except OverflowError:
        return math.copysign(math.inf, x)


_f = f32


def _div(a: float, b: float) -> float:
    """IEEE float32 division (x/0 -> +-inf, 0/0 -> nan)."""
    if b == 0.0:
        if a == 0.0 or math.isnan(a):
            return math.nan
        return math.copysign(math.inf, a) * math.copysign(1.0, b)
    return _f(a / b)


# -- libc.prx sinf (libc VA 0x4AF10, 787 bytes; NID Q4rRL34CEeE) -----------------------------
# A port of FreeBSD msun s_sinf.c + k_sinf.c + k_cosf.c + e_rem_pio2f.c (BSD licence, Sun
# Microsystems 1993 / Bruce D. Evans), checked against the disassembly: every constant below is
# read from libc.prx data 0xD9290..0xD9360 by tools/tests/test_quake.py, the operation order
# follows the packed SSE code (quake.md 2.5).  All intermediate values are doubles (Python
# floats); the result is rounded to float32 once (cvtsd2ss).
_S1 = float.fromhex("-0x1.5555554cbac77p-3")     # libc 0xD92A8 / 0xD9320
_S2 = float.fromhex("0x1.11110896efbb2p-7")      # 0xD9298 / 0xD9318
_S3 = float.fromhex("-0x1.a00f9e2cae774p-13")    # 0xD92A0 / 0xD9310
_S4 = float.fromhex("0x1.6cd878c3b46a7p-19")     # 0xD9290 / 0xD9308
_C0 = float.fromhex("-0x1.ffffffd0c5e81p-2")     # 0xD9338
_C1 = float.fromhex("0x1.55553e1053a42p-5")      # 0xD9348
_C2 = float.fromhex("-0x1.6c087e80f1e27p-10")    # 0xD9330
_C3 = float.fromhex("0x1.99342e0ee5069p-16")     # 0xD9328
_PIO2_1 = float.fromhex("0x1.921fb54442d18p+0")  # 0xD9360 (s1pio2), -: 0xD9368
_PIO2_2 = float.fromhex("0x1.921fb54442d18p+1")  # 0xD92D8 (s2pio2 = pi), -: 0xD92D0
_PIO2_3 = float.fromhex("0x1.2d97c7f3321d2p+2")  # 0xD9350 (s3pio2), -: 0xD9358
_PIO2_4 = float.fromhex("0x1.921fb54442d18p+2")  # 0xD92C0 (s4pio2 = 2 pi), -: 0xD92C8
_INVPIO2 = float.fromhex("0x1.45f306dc9c883p-1")  # 0xD92E0
_TOINT = float.fromhex("0x1.8p52")               # 0xD92E8 / -: 0xD92F0
_RPIO2_1 = float.fromhex("0x1.921fb5p+0")        # -0xD92F8 (pio2_1, 33 bits)
_RPIO2_1T = float.fromhex("0x1.110b4611a6263p-26")  # -0xD9300 (pio2_1t)


def _ksin(x: float) -> float:
    """__kernel_sindf (inlined at libc 0x4AF4E / 0x4B036 / 0x4B1D3), double in, double out."""
    z = x * x
    w = z * z
    r = _S3 + z * _S4
    s = z * x
    return (x + s * (_S1 + z * _S2)) + (s * w) * r


def _kcos(x: float) -> float:
    """__kernel_cosdf (inlined at libc 0x4B135 / 0x4B18E)."""
    z = x * x
    w = z * z
    r = _C2 + z * _C3
    return ((1.0 + z * _C0) + w * _C1) + (z * w) * r


def _pi_scaled(bits: int) -> int:
    """floor(pi * 2^bits) (Machin's formula in integers, 64 guard bits)."""
    g = bits + 64

    def atan_inv(n: int) -> int:
        tot, k, p = 0, 0, (1 << g) // n
        n2 = n * n
        while p:
            t = p // (2 * k + 1)
            tot += -t if k & 1 else t
            p //= n2
            k += 1
        return tot
    return (16 * atan_inv(5) - 4 * atan_inv(239)) >> 64


_PI_BITS = 420
_PI_FIX = _pi_scaled(_PI_BITS)


def _rem_pio2f(x: float, hx: int, ix: int) -> tuple[int, float]:
    """__ieee754_rem_pio2f: (n, y) with x = n * pi/2 + y.  The medium path (|x| <= 0x4DC90FDA,
    libc 0x4B091..0x4B0C5) is exact msun; the large path (libc 0x4B0D5 -> __kernel_rem_pio2
    0x4CCD0, prec 0) is replaced by an exact reduction rounded to double [H: equal to libc on
    every argument tested, quake.md 2.5; not reachable from any shake]."""
    if ix <= 0x4DC90FDA:
        fn = (x * _INVPIO2 + _TOINT) - _TOINT
        n = int(fn)
        return n, (x + fn * -_RPIO2_1) + fn * -_RPIO2_1T
    from fractions import Fraction
    fx = Fraction(abs(x))
    half_pi = Fraction(_PI_FIX, 1 << (_PI_BITS + 1))
    n = int(fx / half_pi + Fraction(1, 2))
    y = float(fx - n * half_pi)
    return (-n, -y) if hx < 0 else (n, y)


def ps4_sinf(x: float) -> float:
    """sinf of the game's libc.prx (libc VA 0x4AF10) for a float32 argument, bit-exact."""
    x = _f(x)
    hx = struct.unpack("<i", struct.pack("<f", x))[0]
    ix = hx & 0x7FFFFFFF
    if ix <= 0x3F490FDA:                                     # |x| <= pi/4
        if ix < 0x39800000 and cvtt(x) == 0:                 # |x| < 2^-12: x itself
            return x
        return _f(_ksin(x))
    if ix <= 0x407B53D1:                                     # |x| <= 5 pi/4
        if ix <= 0x4016CBE3:                                 # |x| <= 3 pi/4
            return _f(_kcos(x - _PIO2_1)) if hx > 0 else -_f(_kcos(x + _PIO2_1))
        return _f(_ksin((_PIO2_2 if hx > 0 else -_PIO2_2) - x))
    if ix <= 0x40E231D5:                                     # |x| <= 9 pi/4
        if ix <= 0x40AFEDDF:                                 # |x| <= 7 pi/4
            return -_f(_kcos(x - _PIO2_3)) if hx > 0 else _f(_kcos(x + _PIO2_3))
        return _f(_ksin(x + (-_PIO2_4 if hx > 0 else _PIO2_4)))
    if ix >= 0x7F800000:                                     # inf, NaN: x - x
        return x - x
    n, y = _rem_pio2f(x, hx, ix)
    q = n & 3
    if q == 0:
        return _f(_ksin(y))
    if q == 1:
        return _f(_kcos(y))
    if q == 2:
        return _f(_ksin(-y))
    return -_f(_kcos(y))


def sinf_correctly_rounded(x: float) -> float:
    """float32(sin(x)) (what the unicorn harness's default sinf stub and MathF.Sin give);
    differs from ``ps4_sinf`` by 1 ulp for some arguments.  Kept for comparisons."""
    if math.isnan(x) or math.isinf(x):
        return math.nan
    return _f(math.sin(x))


_sinf = ps4_sinf


def cvtt(x: float) -> int:
    """cvttss2si to a 32-bit register: truncate toward zero; NaN / out of range -> INT_MIN."""
    if math.isnan(x) or not (-2147483648.0 <= x < 2147483648.0):
        return -0x80000000
    return int(x)


def i16(v: int) -> int:
    return ((v + 0x8000) & 0xFFFF) - 0x8000


def _i32(v: int) -> int:
    return ((v + 0x80000000) & 0xFFFFFFFF) - 0x80000000


def _cdiv(a: int, b: int) -> int:
    """C integer division (truncates toward zero)."""
    q = abs(a) // abs(b)
    return q if (a >= 0) == (b > 0) else -q


F32_2PI = f32(2 * math.pi)          # 0x40C90FDB (VA 0x1A72AC, 0x1A69D0)
F32_INV3 = f32(1 / 3)               # 0x3EAAAAAB (VA 0x1A72B0): 1920-px units -> 640 canvas
F32_Y_CANVAS = f32(362 / 1080)      # 0x3EAB9D65 (VA 0x1A72B4): 1080-px units -> canvas y
F32_2M30 = 2.0 ** -30               # 0x30800000 (VA 0x1A72A4, 0x1A7770): rand() -> [0, 1)
F32_Y_PX = f32(1080 / 362)          # 0x403EF071 (VA 0x1A6DB4): canvas y -> 1080-px units (0x49A60)
F32_SHOCK_Y_MASK = f32(1080 / 448)  # 0x401A4925 (VA 0x1A6DBC): shock y in the mask term (0x49A60)
assert struct.pack("<f", F32_Y_CANVAS) == bytes.fromhex("659dab3e")
assert struct.pack("<f", F32_Y_PX) == bytes.fromhex("71f03e40")
assert struct.pack("<f", F32_SHOCK_Y_MASK) == bytes.fromhex("25491a40")


def frames(ms: int) -> int:
    """Shake durations: ``trunc(3 * ms / 50)`` (C division; 32-bit multiply)."""
    return _cdiv(_i32(3 * ms), 50)


# ---------------------------------------------------------------------------
# random number generators
# ---------------------------------------------------------------------------
class Ps4Rand:
    """libc ``rand()`` of the game's own sce_module/libc.prx (exports cpCOXWMgha0 = rand at
    libc VA 0x1C290, VPbJwTCgME0 = srand at 0x1C310; one global 64-bit state at libc data
    0x123280, initial value 1)::

        state = state * 0x5851F42D4C957F2D + 1   (mod 2^64)
        return (state >> 32) & 0x3FFFFFFF        (RAND_MAX = 2^30 - 1)
        srand(seed): state = (uint32) seed

    The engine seeds it twice: at boot 0xD212E (one uninitialised stack byte: the clock calls
    there are stubs) and at every title-screen init 0xAFE80 with ``title_seed``.
    """

    MUL = 0x5851F42D4C957F2D

    def __init__(self, state: int = 1) -> None:
        self.state = state & M64
        self.calls = 0

    def srand(self, seed: int) -> None:
        self.state = seed & 0xFFFFFFFF

    def rand(self) -> int:
        self.state = (self.state * self.MUL + 1) & M64
        self.calls += 1
        return (self.state >> 32) & 0x3FFFFFFF

    __call__ = rand


def title_seed(minute: int, second: int) -> int:
    """Seed of the title init 0xAFE80: sceRtcGetCurrentClockLocalTime (via 0xEAD50), then
    ``srand(movbe16(minute, second))`` = ``minute * 256 + second`` of the local clock."""
    return ((minute & 0xFF) << 8) | (second & 0xFF)


RND2_INIT = 0x6F37          # u64 at data VA 0xDC7BF0 in the eboot image


class Rnd2:
    """0xE8200: ``s = (s * 0xF131 + 0x11E79431) mod 2^64 >> 1``; returns ``(i64)(int)rand() + s``
    (mod 2^64).  Also advanced by the title update (0xB06E8)."""

    def __init__(self, rng: Ps4Rand, state: int = RND2_INIT) -> None:
        self.rng = rng
        self.state = state & M64

    def __call__(self) -> int:
        self.state = (((self.state * 0xF131) & M64) + 0x11E79431 & M64) >> 1
        return (self.rng.rand() + self.state) & M64


def rnd_range(rng: Callable[[], int], a: float, b: float) -> float:
    """0xC2F90(a, b): ``lo + (rand() % (trunc((hi - lo) * 2^20) + 1)) * 2^-20`` (one rand call)."""
    hi = a if a > b else b                       # vmaxss xmm2, xmm0, xmm1
    lo = b if b < a else a                       # vminss xmm0, xmm1, xmm0
    r = rng()
    n = _i32(cvtt(_f(_f(hi - lo) * 1048576.0)) + 1)
    if n == 0:
        raise ZeroDivisionError("0xC2F90 range overflow (idiv by 0)")
    rem = int(math.fmod(_i32(r), n))             # idiv: remainder takes the dividend's sign
    return _f(lo + _f(float(rem) * 2.0 ** -20))


# ---------------------------------------------------------------------------
# layer slots
# ---------------------------------------------------------------------------
@dataclass
class ShakeSlot:
    """One slot of a drawn layer record (record + offset, slot s = 0 / 1)."""
    kind: int = 0                                   # +0x178+s   u8: 1 quake, 2 shake, 3 swing
    fade: int = 0                                   # +0x17A+2s  i16: start delay, frames
    time: int = 0                                   # +0x17E+2s  i16: duration frames, -1 = until stopped
    par: list[int] = field(default_factory=lambda: [0] * 6)   # +0x182+12s 6 x i16
    elapsed: int = 0                                # +0x19A+2s  i16 (saturates at 30000)
    phase: int = 0                                  # +0x19E+2s  i16
    x: float = 0.0                                  # +0x1A4+4s  f32 output, canvas units
    y: float = 0.0                                  # +0x1AC+4s  f32 output
    w: list[int] = field(default_factory=lambda: [0] * 7)     # +0x1B4+14s 7 x i16 (swing)

    def clear(self) -> None:
        self.kind = self.fade = self.time = self.elapsed = self.phase = 0
        self.par = [0] * 6
        self.w = [0] * 7
        self.x = self.y = 0.0

    def copy(self) -> "ShakeSlot":
        return ShakeSlot(self.kind, self.fade, self.time, list(self.par), self.elapsed,
                         self.phase, self.x, self.y, list(self.w))


@dataclass
class ShakeLayer:
    """The drawn-record fields the shakes read.  ``used`` / ``z`` are read by the SQK2 follow
    rule as they are *before* the B -> A transfer of the same update (quake.md 4.2); the other
    fields are only read by the draw terms (section 5) and belong to the layer runtime."""
    slots: list[ShakeSlot] = field(default_factory=lambda: [ShakeSlot(), ShakeSlot()])
    used: bool = False          # drawn record +0x10 == 1
    z: int = 0                  # committed z, i32 at VA 0xDE2538 + 4 * layer (A + 0x6F50)
    parent: int = -1            # +0x128 (_SBAS)
    mask: int = -1              # +0x120 (_STCH a11)
    sacl: bool = False          # +0x1E & 0x40 (_SACL): the draw skips the mask-layer term
    crop: tuple[int, int, int, int] = (0, 0, 0, 0)   # +0x132 4 x i16 (STCH a12..a15)
    crop_ease: tuple[float, float] = (0.0, 0.0)       # f32 0x49400(+0x158, +0x150) * +0x140 / +0x144
    offscreen: bool = False     # +0x124 == 1 (set by the offscreen pass 0x5A5E0)
    no_shock: bool = False      # catalog draw_flags (+0x3D) bit 1 of the picture (+0x18): colour fills


SLOT_OF_KIND = {1: 0, 2: 1, 3: 1}       # table VA 0x216848 (indexed by kind - 1)


@dataclass(frozen=True)
class SlotStart:
    """Arguments of 0x84670(layer, params, dur_ms, fade_ms, kind)."""
    layer: int
    kind: int
    par: tuple[int, int, int, int, int, int]
    dur_ms: int
    fade_ms: int


class ShakeLayers:
    """40 layers x 2 slots and their per-frame update, sharing one ``rand()``."""

    def __init__(self, rng: Optional[Ps4Rand] = None, rnd2: Optional[Rnd2] = None) -> None:
        self.rng = rng if rng is not None else Ps4Rand()
        self.rnd2 = rnd2 if rnd2 is not None else Rnd2(self.rng)
        self.layers = [ShakeLayer() for _ in range(40)]
        self.scene_dirty = False        # word 0x164B998 (set by every start)

    # -- 0x84670 / 0x847F0 ---------------------------------------------------
    def start(self, st: SlotStart) -> None:
        if not (0 <= st.layer <= 39) or st.kind not in SLOT_OF_KIND:
            return
        s = self.layers[st.layer].slots[SLOT_OF_KIND[st.kind]]
        s.kind = st.kind
        s.fade = i16(frames(st.fade_ms))
        s.time = i16(frames(st.dur_ms)) if st.dur_ms >= 0 else -1
        s.par = [i16(v) for v in st.par]
        s.elapsed = s.phase = 0
        s.x = s.y = 0.0
        s.w = [0] * 7
        self.scene_dirty = True

    def stop(self, layer: int, kind: int) -> None:
        """0x847F0(layer, kind): layer < 0 = all 40; clears the kind's slot (SHS2 and SSS2 both
        clear slot 1).  layer > 39: nothing."""
        if layer > 39 or kind not in SLOT_OF_KIND:
            return
        rng = range(40) if layer < 0 else (layer,)
        for l in rng:
            self.layers[l].slots[SLOT_OF_KIND[kind]].clear()

    def reset_layer(self, layer: int) -> None:
        """Every ``_STCH`` and ``_SCH2`` with layer 0..39 (any picture, empty name, with or
        without a source layer) resets the drawn record at the statement itself: the backend
        0x81760 calls 0x4A770(layer) on both paths (VA 0x81A9C for STCH, 0x81ABB for SCH2),
        which zeroes both slots (+0x178..+0x1CF).  A source layer's scene slots are copied to
        the scene copy and start fresh at the next commit (``merge_committed``)."""
        if 0 <= layer <= 39:
            for s in self.layers[layer].slots:
                s.clear()

    def adv_start(self) -> None:
        """Layer-array initialisation 0x57590 (ADV-mode entry 0x68C25 / 0x692DD and the scan
        load 0xA3050): every slot of every layer is cleared (VA 0x577CC..0x5781D)."""
        for L in self.layers:
            for s in L.slots:
                s.clear()

    def merge_committed(self, layer: int, committed: Sequence[ShakeSlot]) -> list[bool]:
        """B -> A transfer (VA 0x595B2..0x59779, in the first update after a commit, after
        the slot updates): a committed slot replaces the drawn one only when its kind is
        non-zero and differs from the running kind (a running effect of the same kind
        continues).  ``committed`` is B's slots as the commit wrote them (scene kind / fade /
        time / params; runtime fields as B held them, normally 0).  Returns, per slot,
        whether it was copied (the engine then clears B's kind byte)."""
        out = []
        for s in (0, 1):
            b = committed[s]
            a = self.layers[layer].slots[s]
            copy = b.kind != a.kind and b.kind != 0
            if copy:
                self.layers[layer].slots[s] = b.copy()
            out.append(copy)
        return out

    # -- per-frame update (0x57F4D..0x5889A) -----------------------------------
    def update(self, dt: int) -> None:
        """One call of the layer update with ``dt`` 60-Hz frames (VA 0x57A87:
        ``trunc(arg * frames_per_update)``).  Layers 0..39 in index order, slot 0 then 1."""
        dtf = float(dt)
        for l in range(40):
            for si in (0, 1):
                self._slot(l, si, dtf)

    def _slot(self, l: int, si: int, dtf: float) -> None:
        s = self.layers[l].slots[si]
        if s.fade > 0:                                           # 0x57F20: start delay
            v = cvtt(_f(float(s.fade) - dtf))
            s.fade = i16(v) if i16(v) > 0 else 0
            return
        if s.time == 0:
            return
        el = cvtt(_f(float(s.elapsed) + dtf))
        if i16(el) >= 30000:
            el = 30000
        s.elapsed = i16(el)
        if s.time > 0 and s.time < s.elapsed:                    # 0x57FD6: finished
            s.clear()
        if s.kind == 1:
            self._quake(l, si, s, dtf)
        elif s.kind == 2:
            self._shake(s, dtf)
        elif s.kind == 3:
            self._swing(s, dtf)
        else:
            s.x = s.y = 0.0

    def _quake(self, l: int, si: int, s: ShakeSlot, dtf: float) -> None:
        """Kind 1 (_SQK2): a new random offset every p2 frames."""
        p = s.par
        s.phase = i16(cvtt(_f(float(s.phase) + dtf)))
        if p[2] > s.phase:
            return
        s.phase = 0
        if l >= 2 and p[5] != 0:                                 # 0x5810F: follow layer 0 / 1
            x = y = 0.0
            for k in (0, 1):
                lk = self.layers[k]
                if lk.used and lk.z != 0:
                    x, y = lk.slots[si].x, lk.slots[si].y
            s.x, s.y = x, y
            return
        ax, ay = float(p[0]), float(p[1])                        # 0x5843A: amplitude ramp
        el = float(s.elapsed)
        if float(p[3]) > el:
            r = _div(el, float(p[3]))
            ax, ay = _f(ax * r), _f(ay * r)
        elif s.time > 0:
            t0 = _f(float(s.time) - float(p[4]))
            if el > t0:
                r = _f(1.0 - _div(_f(el - t0), float(p[4])))
                ax, ay = _f(ax * r), _f(ay * r)
        r1 = self.rng.rand()
        r2 = self.rng.rand()
        ux = _f(_f(float(r1)) * F32_2M30)
        uy = _f(_f(float(r2)) * F32_2M30)
        vx = _f(ax * ux)
        vy = _f(ay * uy)
        s.x = _f(_f(_f(vx + vx) - ax) * F32_INV3)
        s.y = _f(_f(_f(vy + vy) - ay) * F32_Y_CANVAS)

    def _shake(self, s: ShakeSlot, dtf: float) -> None:
        """Kind 2 (_SHK2): sine of period p2 frames."""
        p = s.par
        per = p[2] if p[2] != 0 else 60
        m = abs(per) & 0xFFFF if per != -32768 else 32768
        e = cvtt(_f(float(s.phase) + dtf))
        while True:                                              # 0x581E0: phase mod period
            d = i16(e)
            e = _i32(e - m)
            if not (m <= d):
                break
        s.phase = i16(m + e)
        v = _sinf(_div(_f(float(s.phase) * F32_2PI), float(per)))
        if p[4] > 0 and s.elapsed < p[4]:                        # fade in over p4 frames
            v = _f(v * _div(float(s.elapsed), float(p[4])))
        if p[3] != 0 and s.time > 0:                             # linear decay over the duration
            t = float(s.time)
            e2 = float(s.elapsed)
            v = _f(v * _div(_f(t - (t if t < e2 else e2)), t))
        if p[5] >= 2:                                            # flicker: 1 frame in 5 (Rnd2)
            if self.rnd2() % 5 == 0:
                v = _f(v * _div(1.0, float(p[5])))
            else:
                v = 0.0
        s.x = _f(_f(v * float(p[0])) * F32_INV3)
        s.y = _f(_f(v * float(p[1])) * F32_Y_CANVAS)

    def _swing(self, s: ShakeSlot, dtf: float) -> None:
        """Kind 3 (_SSW2): x and y sines with random amplitude [p0, p1) and period [p2, p3)
        frames, re-drawn at the end of every period; random sign once per start."""
        p, w = s.par, s.w
        s.phase = i16(cvtt(_f(float(s.phase) + dtf)))
        if not (w[2] >= s.phase):
            s.phase = 0
            w[0] = i16(cvtt(_f(_f(_f(_f(float(self.rng.rand())) * F32_2M30) * float(_i32(p[1] - p[0]))) + float(p[0]))))
            w[2] = i16(cvtt(_f(_f(_f(_f(float(self.rng.rand())) * F32_2M30) * float(_i32(p[3] - p[2]))) + float(p[2]))))
            if w[4] == 0:
                w[4] = 1 if _f(_f(float(self.rng.rand())) * F32_2M30) < 0.5 else -1
        s1 = _sinf(_div(_f(float(s.phase) * F32_2PI), float(w[2])))
        w[6] = i16(cvtt(_f(float(w[6]) + dtf)))
        if not (w[3] >= w[6]):
            w[6] = 0
            w[1] = i16(cvtt(_f(_f(_f(_f(float(self.rng.rand())) * F32_2M30) * float(_i32(p[1] - p[0]))) + float(p[0]))))
            w[3] = i16(cvtt(_f(_f(_f(_f(float(self.rng.rand())) * F32_2M30) * float(_i32(p[3] - p[2]))) + float(p[2]))))
            if w[5] == 0:
                w[5] = 1 if _f(_f(float(self.rng.rand())) * F32_2M30) < 0.5 else -1
        p4 = float(p[4])
        a = _f(_f(_f(float(w[4]) * float(w[0])) * p4) * s1)
        b = _f(_f(float(w[5]) * float(w[1])) * p4)
        s2 = _sinf(_div(_f(float(w[6]) * F32_2PI), float(w[3])))
        s.x = _f(a * F32_INV3)
        s.y = _f(_f(s2 * b) * F32_Y_CANVAS)

    # -- draw (0x51060) ---------------------------------------------------------
    def mask_axis(self, m: int, axis: int, shock: tuple[float, float] = (0.0, 0.0),
                  shakes: bool = True) -> float:
        """0x49A60(A[m] + 0x10, axis), axis 0 = x, 1 = y: layer m's crop origin (with its crop
        tween) plus its shake outputs in 1920 x 1080 units, plus the screen shock when m has a
        crop rectangle and is an offscreen target.  The shake outputs are read from
        A[m.mask] when m itself has a mask, else from m (VA 0x49A8F).  ``shock`` is
        (0xDF0720, 0xDF0724) when byte 0xDF07C8 is 0, else (0, 0); ``shakes=False`` computes
        the value with every slot kind 0 (used by ``offset``)."""
        L = self.layers
        M = L[m]
        src = L[M.mask] if M.mask >= 0 else M
        sh = 0.0
        if shakes:
            for s in src.slots:
                if s.kind != 0:
                    sh = _f(sh + (s.x if axis == 0 else s.y))
        sh = _f(sh * (3.0 if axis == 0 else F32_Y_PX))            # 0x1A6DB8 = 3.0, 0x1A6DB4
        v = _f(_f(M.crop_ease[axis]) + _f(sh + float(M.crop[axis])))
        if any(M.crop) and M.offscreen:                           # VA 0x49B1C..0x49B6F
            sx, sy = (0.0, 0.0) if M.no_shock else shock          # 0x49B71..0x49BDA
            v = _f(_f(sx * 3.0) + v) if axis == 0 else _f(_f(sy * F32_SHOCK_Y_MASK) + v)
        return v

    def mask_offset(self, l: int, shock: tuple[float, float] = (0.0, 0.0),
                    shakes: bool = True) -> tuple[float, float]:
        """Mask-layer term of layer l's draw (VA 0x516DE..0x5174A): when l has a mask and not
        the _SACL flag, ``(0x49A60(mask, 0) * 640 / 1920, 0x49A60(mask, 1) * 360 / 1080)`` is
        added to the layer *position* (so it also moves the pivot-zoom point P)."""
        r = self.layers[l]
        if r.mask < 0 or r.sacl:
            return 0.0, 0.0
        vx = self.mask_axis(r.mask, 0, shock, shakes)
        vy = self.mask_axis(r.mask, 1, shock, shakes)
        return _f(_f(vx * 640.0) / 1920.0), _f(_f(vy * 360.0) / 1080.0)

    def offset(self, l: int, shock: tuple[float, float] = (0.0, 0.0)) -> tuple[float, float]:
        """Total canvas-unit translation that the shakes and the screen shock add to layer l's
        draw: ``slot_offset`` + the layer's own shock term (``shock`` unless its picture is a
        colour fill) + the shake / shock part of ``mask_offset`` (exact when the mask has no
        crop; with a crop, float32 rounding of the crop origin applies).  A renderer adds
        ``mask_offset`` to the position and ``slot_offset`` + shock to the translation column
        instead (quake.md 5)."""
        r = self.layers[l]
        sx, sy = (0.0, 0.0) if r.no_shock else shock
        ox, oy = self.slot_offset(l)
        mx, my = self.mask_offset(l, shock, True)
        bx, by = self.mask_offset(l, (0.0, 0.0), False)
        return (_f(_f(ox + sx) + _f(mx - bx)), _f(_f(oy + sy) + _f(my - by)))

    def slot_offset(self, l: int) -> tuple[float, float]:
        """Canvas-unit translation the layer draw adds for the two slots (VA 0x51A22..0x51B71;
        added to the translation column t, so the STZ4 pivot zoom scales it).  Slot 0 of a
        quake follows the outermost quaking ancestor, or else the mask layer's quake."""
        L = self.layers
        r = L[l]
        dx = dy = 0.0
        k0 = r.slots[0].kind
        if k0 != 0:
            ox, oy = r.slots[0].x, r.slots[0].y
            if k0 == 1:
                p = r.parent
                if p >= 0:
                    while p >= 0:                    # the outermost quaking ancestor wins
                        if L[p].slots[0].kind != 0:
                            ox, oy = L[p].slots[0].x, L[p].slots[0].y
                        p = L[p].parent
                elif r.mask >= 0 and L[r.mask].slots[0].kind != 0:
                    ox, oy = L[r.mask].slots[0].x, L[r.mask].slots[0].y
            dx, dy = _f(dx + ox), _f(dy + oy)
        if r.slots[1].kind != 0:
            dx, dy = _f(dx + r.slots[1].x), _f(dy + r.slots[1].y)
        return dx, dy


# ---------------------------------------------------------------------------
# command arguments (object parser: None = absent or empty)
# ---------------------------------------------------------------------------
Args = Sequence[Optional[int]]


def _a(args: Args, i: int) -> Optional[int]:
    return args[i] if i < len(args) else None


def _d(args: Args, i: int, default: int) -> int:
    v = _a(args, i)
    return default if v is None else v


def sqk2_start(args: Args) -> Optional[tuple[int, SlotStart]]:
    """_SQK2(l, a1, a2, a3, dur, fade, a6, a7, _) -> (layer, start); None = statement ignored
    (layer > 39).  The caller applies the skip rule (``dur >= 0`` and skip mode -> nothing)
    and, for layer < 0, ``sqk2_targets``."""
    l = _d(args, 0, 0)
    if l > 39:
        return None
    dur = _d(args, 4, -1)
    fade = _d(args, 5, 0)
    a3 = _a(args, 3)
    if a3 is None:
        p2 = 2
    elif a3 < 0:
        p2 = 1
    elif ((3 * a3 + 0x31) & 0xFFFFFFFF) < 0x63:
        p2 = 2
    else:
        p2 = frames(a3) + 1
    a7 = _d(args, 7, 0)
    p4 = frames(dur) if dur >= 0 and a7 <= 16 else 0
    par = (_d(args, 1, 15), _d(args, 2, 15), p2, frames(a7), p4, _d(args, 6, 0))
    return l, SlotStart(l, 1, par, dur, fade)


def shk2_start(args: Args) -> Optional[tuple[int, SlotStart]]:
    """_SHK2(l, a1, a2, a3, dur, fade, a6, a7, a8); layer -1..39 accepted (-1 = targets)."""
    l = _d(args, 0, 0)
    if not (-1 <= l <= 39):
        return None
    a3, a7 = _a(args, 3), _a(args, 7)
    par = (_d(args, 1, 0), _d(args, 2, 15), 18 if a3 is None else frames(a3),
           _d(args, 6, 0), 0 if a7 is None else frames(a7), _d(args, 8, 1))
    return l, SlotStart(l, 2, par, _d(args, 4, -1), _d(args, 5, 0))


def ssw2_start(args: Args) -> Optional[tuple[int, SlotStart]]:
    """_SSW2(l, a1, a2, a3, a4, a5, dur, fade); layer 0..39 only."""
    l = _d(args, 0, 0)
    if not (0 <= l <= 39):
        return None
    a3, a4 = _a(args, 3), _a(args, 4)
    par = (_d(args, 1, 7), _d(args, 2, 15), 60 if a3 is None else frames(a3),
           120 if a4 is None else frames(a4), _d(args, 5, 1), 0)
    return l, SlotStart(l, 3, par, _d(args, 6, -1), _d(args, 7, 0))


def sqk2_targets(layers: Sequence[ShakeLayer], scene_pic: Sequence[int]) -> list[int]:
    """Layers that _SQK2(-1, ...) starts (0xB6E08): 0 and 1 if in use; 2..39 if (in use and
    committed z > 0) or the scene picture (scene_state +0x4) is not -1."""
    out = [k for k in (0, 1) if layers[k].used]
    out += [k for k in range(2, 40) if (layers[k].used and layers[k].z > 0) or scene_pic[k] != -1]
    return out


def shk2_targets(layers: Sequence[ShakeLayer], scene_pic: Sequence[int]) -> list[int]:
    """Layers that _SHK2(-1, ...) starts (0xB7250): as SQK2 but layers 0 / 1 also need z > 0."""
    out = [k for k in (0, 1) if layers[k].used and layers[k].z > 0]
    out += [k for k in range(2, 40) if (layers[k].used and layers[k].z > 0) or scene_pic[k] != -1]
    return out


def stop_layers(args: Args) -> Optional[int]:
    """_SQS2 / _SHS2 / _SSS2 (n): layer (absent 0; < 0 = all); None = ignored (> 39)."""
    l = _d(args, 0, 0)
    return None if l > 39 else l


# ---------------------------------------------------------------------------
# _MSQK: message-window quake (globals 0xDF0894..0xDF08A8)
# ---------------------------------------------------------------------------
@dataclass
class MessageQuake:
    """_MSQK(ms = 1000, a1 = 5, a2 = 5).  Offsets (``x``, ``y``) are added by the HUD 0x53CF0
    to the message window and text positions (640 x 448 UI canvas units)."""
    dur: float = 0.0        # 0xDF0894 ms (0 = inactive)
    t: float = 0.0          # 0xDF0898 elapsed ms
    amp_y: float = 0.0      # 0xDF089C = a2 * 448 / 720
    amp_x: float = 0.0      # 0xDF08A0 = a1 * 640 / 1280
    x: float = 0.0          # 0xDF08A4
    y: float = 0.0          # 0xDF08A8

    def start(self, args: Args) -> None:
        """0x962D3 -> 0x53BE0(ms, a2, a1)."""
        ms = _d(args, 0, 1000)
        a1, a2 = _d(args, 1, 5), _d(args, 2, 5)
        self.dur = float(ms if (ms & 0xFFFFFFFF) < 10001 else 1000)
        self.t = 0.0
        self.amp_y = _f(_f(_f(float(a2)) * 448.0) / 720.0)
        self.amp_x = _f(_f(_f(float(a1)) * 640.0) / 1280.0)
        self.x = self.y = 0.0

    def update(self, rng: Callable[[], int], arg: int = 1, fpu: int = 1) -> None:
        """Top of 0x57980 (before the layer loop; also runs while the layer update is
        frozen): two rand() calls per active frame, x first."""
        if not self.dur > 0.0:
            return
        self.t = _f(_f(_f(_f(float(arg) * float(fpu)) * 1000.0) / 60.0) + self.t)
        if self.t >= self.dur:
            self.dur = 0.0
            self.x = self.y = 0.0
            return
        self.x = rnd_range(rng, -self.amp_x, self.amp_x)
        self.y = rnd_range(rng, -self.amp_y, self.amp_y)

    def reset(self) -> None:
        """0x57590 (ADV-mode entry / scan load) zeroes all six globals (VA 0x5795D, 0x57965):
        a running _MSQK is cancelled.  Script jumps inside ADV mode do not call it."""
        self.dur = self.t = self.amp_y = self.amp_x = self.x = self.y = 0.0


# ---------------------------------------------------------------------------
# _SHOK: screen shock, screen-effect kind 1 (globals 0xDF07B4.., 0xDF0818..)
# ---------------------------------------------------------------------------
@dataclass
class ScreenShock:
    """_SHOK(ax, ay, ms = 300, n = 3, delay = 0) (inline readers; empty = default): a damped
    sine, no rand().  ``x``, ``y`` (0xDF0720 / 0xDF0724, canvas units) are added to every
    scene layer (not to pictures with catalog draw_flags bit 1) and not to the HUD."""
    kind: int = 0           # 0xDF07B4 (1 = shock; other kinds belong to other effects)
    remaining: int = 0      # 0xDF07D4 frames (< 0: endless)
    period: float = 0.0     # 0xDF07D8 frames
    count: float = 0.0      # 0xDF07DC
    ax: float = 0.0         # 0xDF07E0
    ay: float = 0.0         # 0xDF07E4
    phase: float = 0.0      # 0xDF07E8 rad
    amp: float = 0.0        # 0xDF07EC
    x: float = 0.0          # 0xDF0720
    y: float = 0.0          # 0xDF0724
    delay: int = 0          # 0xDF0818 ms
    pending: tuple[int, int, int, int, int] = (0, 0, 0, 0, 0)   # 0xDF0820 kind, 0xDF0830.. args

    @staticmethod
    def parse(raw_args: Sequence[Optional[int]]) -> tuple[int, int, int, int, int]:
        """Inline values (None = missing or empty) -> (ax, ay, ms, n, delay) with defaults."""
        v = [0, 0, 300, 3, 0]
        for i in range(min(5, len(raw_args))):
            r = raw_args[i]
            if r is not None:
                v[i] = i16(r)
        return v[0], v[1], v[2], v[3], v[4]

    def start(self, ax: int, ay: int, ms: int, n: int, delay: int = 0) -> None:
        """0x438C0(1, ax, ay, ms, n, delay)."""
        self.delay = delay
        if delay > 0:
            self.pending = (1, ax, ay, ms, n)
            return
        self._begin(1, ax, ay, ms, n)

    def _begin(self, kind: int, ax: int, ay: int, ms: int, n: int) -> None:
        self.kind = kind
        self.phase = 0.0
        self.amp = 1.0
        t = _f(_f(float(ms) * 3.0) / 50.0)
        self.remaining = _cdiv(_i32(3 * _i32(ms * n)), 50)
        self.period = 1.0 if t == 0.0 else t
        self.count = float(n if n >= 2 else 1)
        self.ax, self.ay = float(ax), float(ay)

    def stop(self) -> None:
        """_SHAF() (0x80AE0 -> 0x42DC0(0,0,0,0)): kind -1, remaining 0 -> offsets 0 next
        update.  A pending delayed shock (0xDF0818) is not cancelled."""
        self.kind = -1
        self.remaining = 0

    def adv_start(self) -> None:
        """0x57590 zeroes only the outputs 0xDF0720 / 0xDF0724 (VA 0x578BB / 0x578C5); the
        effect state (kind, remaining, delay) is not reset [E]."""
        self.x = self.y = 0.0

    def layer_offset(self, no_shock: bool = False) -> tuple[float, float]:
        """The shock term of a scene layer's draw (VA 0x51808..0x51833): (x, y), or (0, 0)
        for a picture with catalog draw_flags bit 1 (the 7 colour fills).  Byte 0xDF07C8 is
        0 while a shock runs (0x438C0)."""
        return (0.0, 0.0) if no_shock else (self.x, self.y)

    def update(self, dt: int) -> None:
        """0x439D0(dt), kind 1 only (other kinds are other commands' effects)."""
        if self.delay > 0:
            v = self.delay - _cdiv(dt * 1000, 60)
            self.delay = v if v > 0 else 0
            if v <= 0:
                k, ax, ay, ms, n = self.pending
                self._begin(k, ax, ay, ms, n)
        if self.remaining == 0:
            self.x = self.y = 0.0
            return
        if self.kind != 1:
            return
        if self.remaining > 0:
            v = self.remaining - dt
            self.remaining = v if v > 0 else 0
            if v <= 0:
                self.kind = 0
                self.period = self.ax = self.ay = self.phase = self.amp = 0.0
                self.x = self.y = 0.0
                return
        fr = float(dt)
        dph = _f(_f(fr * F32_2PI) / self.period)
        dam = _f(_f(fr / self.period) / self.count)
        self.phase = _f(dph + self.phase)
        a = _f(self.amp - dam)
        self.amp = 0.0 if 0.0 > a else a
        s = _sinf(self.phase)
        self.x = _f(_f(_f(_f(s * self.ax) * self.amp) * 640.0) / 480.0)
        self.y = _f(_f(_f(self.amp * _f(s * self.ay)) * 480.0) / 360.0)


def run_frames(sl: ShakeLayers, n: int, dt: int = 1) -> Iterable[list[tuple[float, float, float, float]]]:
    """Convenience: yield, after each of ``n`` updates, the (x0, y0, x1, y1) slot outputs of all
    40 layers."""
    for _ in range(n):
        sl.update(dt)
        yield [(L.slots[0].x, L.slots[0].y, L.slots[1].x, L.slots[1].y) for L in sl.layers]


def _main(argv: Optional[Sequence[str]] = None) -> int:
    """``python -m tsuki.quake SQK2 -1,1,4,0,600,0,0,0,0 [frames] [seed]``: print per-frame
    offsets of layer 0 for one start statement (synthetic: layer in use, z = 1)."""
    import sys
    a = list(argv if argv is not None else sys.argv[1:])
    if len(a) < 2:
        print(_main.__doc__)
        return 2
    cmd, raw = a[0].upper(), a[1]
    n = int(a[2]) if len(a) > 2 else 40
    rng = Ps4Rand()
    if len(a) > 3:
        rng.srand(int(a[3], 0))
    args = [None if t.strip() == "" else int(t) for t in raw.split(",")]
    if cmd == "MSQK":
        mq = MessageQuake()
        mq.start(args)
        for f in range(n):
            mq.update(rng)
            print(f"{f + 1:4d} x={mq.x:+.6f} y={mq.y:+.6f}")
        return 0
    if cmd == "SHOK":
        sh = ScreenShock()
        sh.start(*ScreenShock.parse(args))
        for f in range(n):
            sh.update(1)
            print(f"{f + 1:4d} x={sh.x:+.6f} y={sh.y:+.6f}")
        return 0
    parse = {"SQK2": sqk2_start, "SHK2": shk2_start, "SSW2": ssw2_start}[cmd]
    sl = ShakeLayers(rng)
    sl.layers[0].used, sl.layers[0].z = True, 1
    res = parse(args)
    if res is None:
        print("statement ignored")
        return 1
    st = res[1]
    sl.start(SlotStart(0, st.kind, st.par, st.dur_ms, st.fade_ms))
    for f, outs in enumerate(run_frames(sl, n)):
        dx, dy = sl.offset(0)
        print(f"{f + 1:4d} dx={dx:+.6f} dy={dy:+.6f}  rand calls {rng.calls}")
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
