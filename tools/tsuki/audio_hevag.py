"""Sony VAG / HE-VAG (High Efficiency VAG) ADPCM decoder.

vag.mrg entries are "VAGp" files whose frames use the HE-VAG extension of
PS-ADPCM: a 4-tap predictor chosen from a 128-entry table (plain PS-ADPCM is
the subset with predictor index 0..4 and two taps).  The game itself decodes
them on the CPU (libSceAudiodecCpuHevag, the "hevag decode thread" in the
eboot), so no system library is needed for a port.

Frame (16 bytes, 28 samples, mono)::

    byte 0: bits 0..3 = shift, bits 4..7 = predictor index bits 0..3
    byte 1: bits 0..3 = flag,  bits 4..7 = predictor index bits 4..7
    bytes 2..15: 28 signed 4-bit codes, low nibble first

    code   = signed_nibble << 12 >> shift              (arithmetic shift)
    pred   = trunc_to_int(float32(h1*c0 + h2*c1 + h3*c2 + h4*c3))
    sample = code + pred                               (history keeps this value)
    out    = clamp(sample, -32768, 32767)
    flag >= 7 -> the frame decodes to silence (sample = 0, still enters the history)

The coefficient table and the arithmetic follow vgmstream's
``src/coding/psv_decoder.c`` (ISC-style licence; the table was reverse
engineered from Sony's PC tools, credited there to daemon1).  The table is
stored here as the exact integer numerators over 8192 that the float literals
in vgmstream represent (every value * 8192 is an integer; checked at import).
"""
from __future__ import annotations

import os
import struct
from dataclasses import dataclass
from typing import Optional

import numpy as np

__all__ = ["HEVAG_COEFS_8192", "HEVAG_COEFS", "VagHeader", "VagError", "parse_vag",
           "frame_params", "decode_frames", "decode_vag", "FRAME_BYTES", "FRAME_SAMPLES"]

FRAME_BYTES = 16
FRAME_SAMPLES = 28
VAG_HEADER_SIZE = 0x30

# 128 x 4 predictor coefficients, in units of 1/8192 (= vgmstream hevag_coefs_f * 8192).
HEVAG_COEFS_8192: tuple[tuple[int, int, int, int], ...] = (
    (0, 0, 0, 0), (7680, 0, 0, 0), (14720, -6656, 0, 0), (12544, -7040, 0, 0),
    (15616, -7680, 0, 0), (14731, -7059, 0, 0), (14507, -7366, 0, 0), (13920, -7522, 0, 0),
    (13133, -7680, 0, 0), (12028, -7680, 0, 0), (10764, -7680, 0, 0), (9359, -7680, 0, 0),
    (7832, -7680, 0, 0), (6201, -7680, 0, 0), (4488, -7680, 0, 0), (2717, -7680, 0, 0),
    (910, -7680, 0, 0), (-910, -7680, 0, 0), (-2717, -7680, 0, 0), (-4488, -7680, 0, 0),
    (-6201, -7680, 0, 0), (-7832, -7680, 0, 0), (-9359, -7680, 0, 0), (-10764, -7680, 0, 0),
    (-12028, -7680, 0, 0), (-13133, -7680, 0, 0), (-13920, -7522, 0, 0), (-14507, -7366, 0, 0),
    (-14731, -7059, 0, 0), (5376, -9216, 3328, -3072), (-6400, -7168, -3328, -2304), (-10496, -7424, -3584, -1024),
    (-167, -2722, -494, -541), (-7430, -2221, -2298, 424), (-8001, -3166, -2814, 289), (6018, -4750, 2649, -1298),
    (3798, -6946, 3875, -1216), (-8237, -2596, -2071, 227), (9199, 1982, -1382, -2316), (13021, -3044, -3792, 1267),
    (13112, -4487, -2250, 1665), (-1668, -3744, -6456, 840), (7819, -4328, 2111, -506), (9571, -1336, -757, 487),
    (10032, -2562, 300, 199), (-4745, -4122, -5486, -1493), (-5896, 2378, -4787, -6947), (-1193, -9117, -1237, -3114),
    (2783, -7108, -1575, -1447), (-7334, -2062, -2212, 446), (6127, -2577, -315, -18), (9457, -1858, 102, 258),
    (7876, -4483, 2126, -538), (-7172, -1795, -2069, 482), (-7358, -2102, -2233, 440), (-9170, -3509, -2674, -391),
    (-2638, -2647, -1929, -1637), (1873, 9183, 1860, -5746), (9214, 1859, -1124, -2427), (13204, -3012, -4139, 1370),
    (12437, -4792, -256, 622), (-2653, -1144, -3182, -6878), (9331, -1048, -828, 507), (1642, -620, -946, -4229),
    (4246, -7585, -533, -2259), (-8988, -3891, -2807, 44), (-2562, -2735, -1730, -1899), (3182, -483, -714, -1421),
    (7937, -3844, 2821, -1019), (10069, -2609, 314, 195), (8400, -3297, 1551, -155), (-8529, -2775, -2432, -336),
    (9477, -1882, 108, 256), (75, -2241, -298, -6937), (-9143, -4160, -2963, 5), (-7270, -1958, -2156, 460),
    (-2740, 3745, 5936, -1089), (8993, 1948, -683, -2704), (13101, -2835, -3854, 1055), (9543, -1961, 130, 250),
    (5272, -4270, 3124, -3157), (-7696, -3383, -2907, -456), (7309, 2523, 434, -2461), (10275, -2867, 391, 172),
    (10940, -3721, 665, 97), (24, -310, -1262, 320), (-8122, -2411, -2311, -271), (-8511, -3067, -2337, 163),
    (326, -3846, 419, -933), (8895, 2194, -541, -2880), (12073, -1876, -2017, -601), (8729, -3423, 1674, -169),
    (12950, -3847, -3007, 1946), (10038, -2570, 302, 198), (9385, -2757, 1008, 41), (-4720, -5006, -2852, -1161),
    (7869, -4326, 2135, -501), (2450, -8597, 1299, -2780), (10192, -2763, 360, 181), (11313, -4213, 833, 53),
    (10154, -2716, 345, 185), (9638, -1417, -737, 482), (3854, -4554, 2843, -3397), (6699, -5659, 2249, -1074),
    (11082, -3908, 728, 80), (-1026, -9810, -805, -3462), (10396, -3746, 1367, -96), (10287, 988, -1915, -1437),
    (7953, 3878, -764, -3263), (12689, -3375, -3354, 2079), (6641, 3166, 231, -2089), (-2348, -7354, -1944, -4122),
    (9290, -4039, 1885, -246), (4633, -6403, 1748, -1619), (11247, -4125, 802, 61), (9807, -2284, 219, 222),
    (9736, -1536, -706, 473), (8440, -3436, 1562, -176), (9307, -1021, -835, 509), (1698, -9025, 688, -3037),
    (10214, -2791, 368, 179), (8390, 3248, -758, -2989), (7201, 3316, 46, -2614), (-88, -7809, -538, -4571),
    (6193, -5189, 2760, -1245), (12325, -1290, -3284, 253), (13064, -4075, -2824, 1877), (5333, 2999, 775, -1132),
)

if len(HEVAG_COEFS_8192) != 128:  # pragma: no cover - static data
    raise RuntimeError("HE-VAG table must have 128 rows")

HEVAG_COEFS = np.array(HEVAG_COEFS_8192, dtype=np.float64) / 8192.0
HEVAG_COEFS_F32 = HEVAG_COEFS.astype(np.float32)


class VagError(ValueError):
    pass


@dataclass(frozen=True)
class VagHeader:
    """48-byte big-endian "VAGp" header (as found in vag.mrg)."""
    magic: bytes            # b"VAGp"
    version: int            # 0x00020001 in every vag.mrg entry
    reserved_08: int
    data_size: int          # bytes of ADPCM data after the header (multiple of 16)
    sample_rate: int        # 48000
    reserved_14: bytes      # 0x14..0x1D (zero)
    channels_byte: int      # byte 0x1E: 1, except 2 for entry 0 ("dummy")
    reserved_1f: int
    name: str               # 0x20..0x2F, NUL padded ASCII, e.g. "moon_se00001_L"

    @property
    def frames(self) -> int:
        return self.data_size // FRAME_BYTES


def parse_vag(data: bytes | bytearray | memoryview, *, header_only: bool = False) -> VagHeader:
    """Parse the 48-byte header; unless ``header_only`` the buffer must also
    hold the ``data_size`` bytes of frames that follow it."""
    b = bytes(data[:VAG_HEADER_SIZE])
    if len(b) < VAG_HEADER_SIZE or b[:4] != b"VAGp":
        raise VagError("not a VAGp stream")
    version, res08, size, rate = struct.unpack_from(">IIII", b, 4)
    if size % FRAME_BYTES:
        raise VagError(f"VAG data size {size} is not a multiple of {FRAME_BYTES}")
    if not header_only and VAG_HEADER_SIZE + size > len(data):
        raise VagError(f"VAG data size {size} runs past the buffer ({len(data)} bytes)")
    name = b[0x20:0x30].split(b"\0", 1)[0].decode("ascii")
    return VagHeader(b[:4], version, res08, size, rate, b[0x14:0x1E], b[0x1E], b[0x1F], name)


def frame_params(frame: bytes) -> tuple[int, int, int]:
    """(predictor index, shift, flag) of one 16-byte frame (index > 127 -> 0)."""
    idx = (frame[1] & 0xF0) | (frame[0] >> 4)
    if idx > 127:
        idx = 0
    return idx, frame[0] & 0x0F, frame[1] & 0x0F


def _decode_py(body: bytes, nframes: int, hist: np.ndarray) -> np.ndarray:
    f32 = np.float32
    coefs = HEVAG_COEFS_F32
    out = np.zeros(nframes * FRAME_SAMPLES, dtype=np.int16)
    h1, h2, h3, h4 = (int(v) for v in hist)
    o = 0
    for f in range(nframes):
        p = f * FRAME_BYTES
        b0 = body[p]
        b1 = body[p + 1]
        idx = (b1 & 0xF0) | (b0 >> 4)
        if idx > 127:
            idx = 0
        shift = b0 & 0x0F
        flag = b1 & 0x0F
        c0, c1, c2, c3 = coefs[idx]
        for i in range(FRAME_SAMPLES):
            if flag < 7:
                byte = body[p + 2 + (i >> 1)]
                nib = (byte >> 4) if (i & 1) else (byte & 0x0F)
                if nib >= 8:
                    nib -= 16
                code = (nib << 12) >> shift
                acc = f32(f32(h1) * c0) + f32(f32(h2) * c1)
                acc = f32(acc) + f32(f32(h3) * c2)
                acc = f32(acc) + f32(f32(h4) * c3)
                s = code + int(acc)           # int() truncates toward zero like a C cast
            else:
                s = 0
            out[o] = 32767 if s > 32767 else (-32768 if s < -32768 else s)
            o += 1
            h4 = h3
            h3 = h2
            h2 = h1
            h1 = s
    hist[:] = (h1, h2, h3, h4)
    return out


_decode_nb = None
if not os.environ.get("TSUKI_NO_NUMBA"):
    try:
        import numba  # type: ignore[import-not-found]

        @numba.njit(cache=True)
        def _decode_nb_impl(body, nframes, coefs, hist):  # type: ignore[no-untyped-def]
            out = np.zeros(nframes * 28, dtype=np.int16)
            h1 = np.int64(hist[0]); h2 = np.int64(hist[1]); h3 = np.int64(hist[2]); h4 = np.int64(hist[3])
            o = 0
            for f in range(nframes):
                p = f * 16
                b0 = np.int64(body[p])
                b1 = np.int64(body[p + 1])
                idx = (b1 & 0xF0) | (b0 >> 4)
                if idx > 127:
                    idx = 0
                shift = b0 & 0x0F
                flag = b1 & 0x0F
                c0 = coefs[idx, 0]; c1 = coefs[idx, 1]; c2 = coefs[idx, 2]; c3 = coefs[idx, 3]
                for i in range(28):
                    if flag < 7:
                        byte = np.int64(body[p + 2 + (i >> 1)])
                        nib = (byte >> 4) if (i & 1) else (byte & 0x0F)
                        if nib >= 8:
                            nib -= 16
                        code = (nib << 12) >> shift
                        acc = np.float32(np.float32(h1) * c0) + np.float32(np.float32(h2) * c1)
                        acc = np.float32(acc) + np.float32(np.float32(h3) * c2)
                        acc = np.float32(acc) + np.float32(np.float32(h4) * c3)
                        s = code + np.int64(acc)
                    else:
                        s = np.int64(0)
                    if s > 32767:
                        out[o] = 32767
                    elif s < -32768:
                        out[o] = -32768
                    else:
                        out[o] = s
                    o += 1
                    h4 = h3; h3 = h2; h2 = h1; h1 = s
            hist[0] = h1; hist[1] = h2; hist[2] = h3; hist[3] = h4
            return out

        _decode_nb = _decode_nb_impl
    except ImportError:
        _decode_nb = None

HAVE_NUMBA = _decode_nb is not None


def decode_frames(body: bytes | bytearray | memoryview, nframes: Optional[int] = None,
                  hist: Optional[np.ndarray] = None, *, use_numba: Optional[bool] = None) -> np.ndarray:
    """Decode ``nframes`` 16-byte HE-VAG frames (mono) to int16 PCM.

    ``hist`` (int64[4], newest first) carries the predictor history across
    calls (streaming); it is updated in place.
    """
    body = bytes(body)
    if nframes is None:
        nframes = len(body) // FRAME_BYTES
    if nframes * FRAME_BYTES > len(body):
        raise VagError("frame count exceeds the data")
    if hist is None:
        hist = np.zeros(4, dtype=np.int64)
    if use_numba is None:
        use_numba = HAVE_NUMBA
    if use_numba and _decode_nb is not None:
        arr = np.frombuffer(body, dtype=np.uint8)
        return np.asarray(_decode_nb(arr, nframes, HEVAG_COEFS_F32, hist))
    return _decode_py(body, nframes, hist)


def decode_vag(data: bytes | bytearray | memoryview, *, trim_end: bool = True,
               use_numba: Optional[bool] = None) -> np.ndarray:
    """Decode a whole mono VAGp file to int16 PCM (1-D array).

    With ``trim_end`` the output stops before the first frame whose flag is
    >= 7 (the end marker; such frames decode to silence anyway).  The leading
    all-zero frame that every VAG starts with is kept (28 samples of silence),
    as the hardware would play it.
    """
    hdr = parse_vag(data)
    body = bytes(data[VAG_HEADER_SIZE:VAG_HEADER_SIZE + hdr.data_size])
    n = hdr.frames
    if trim_end:
        for f in range(n):
            if (body[f * FRAME_BYTES + 1] & 0x0F) >= 7:
                n = f
                break
    return decode_frames(body, n, use_numba=use_numba)


def _self_check() -> None:
    # vgmstream's float literals are exact multiples of 1/8192 (spot values)
    assert abs(HEVAG_COEFS[5, 0] - 1.7982178) < 1e-6 and abs(HEVAG_COEFS[127, 3] + 0.13818359) < 1e-7


_self_check()
