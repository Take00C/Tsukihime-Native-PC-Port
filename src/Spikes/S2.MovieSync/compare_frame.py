"""Spike S2, spikes.md S2 #11: YCbCr correctness of one movie frame.

Compares what S2.MovieSync wrote for one frame (decoded planes + the GL read-back of the PS #4 pass)
with (1) the same frame decoded by the ffmpeg CLI (Gyan 9.0.2, the oracle's ffmpeg), (2) the oracle
formula tsuki.compose.ps_ycbcr applied to those planes with GL-style bilinear chroma (centre-sited
texel lookup, clamp to edge), and (3) swscale's BT.709 limited-range RGB conversion.

Usage (PYTHONPATH=D:/TsukihimePort/tools):
  python compare_frame.py <ours.yuv|ours.nv12> <ours_gl.rgb> <ref_ffmpeg.yuv> <ref_ffmpeg.rgb> [W H]
Prints JSON. Numbers only; no pixels leave samples/.
"""
from __future__ import annotations

import json
import sys

import numpy as np

from tsuki import compose


def load_planes(path: str, w: int, h: int):
    raw = np.fromfile(path, np.uint8)
    y = raw[: w * h].reshape(h, w)
    rest = raw[w * h:]
    if path.endswith(".nv12"):
        uv = rest.reshape(h // 2, w // 2, 2)
        return y, uv[..., 0], uv[..., 1]
    q = (w // 2) * (h // 2)
    return y, rest[:q].reshape(h // 2, w // 2), rest[q: 2 * q].reshape(h // 2, w // 2)


def gl_bilinear_upsample(c: np.ndarray, w: int, h: int) -> np.ndarray:
    """Sample a (h/2, w/2) R8 texture at the centres of a (h, w) target, GL_LINEAR + CLAMP_TO_EDGE."""
    ch, cw = c.shape
    cf = c.astype(np.float32) / np.float32(255)

    def axis(n_out: int, n_in: int):
        t = (np.arange(n_out, dtype=np.float64) + 0.5) * n_in / n_out - 0.5
        i0 = np.floor(t).astype(np.int64)
        f = (t - i0).astype(np.float32)
        return np.clip(i0, 0, n_in - 1), np.clip(i0 + 1, 0, n_in - 1), f

    y0, y1, fy = axis(h, ch)
    x0, x1, fx = axis(w, cw)
    top = cf[y0][:, x0] * (1 - fx) + cf[y0][:, x1] * fx
    bot = cf[y1][:, x0] * (1 - fx) + cf[y1][:, x1] * fx
    return (top * (1 - fy)[:, None] + bot * fy[:, None]).astype(np.float32)


def psnr(a: np.ndarray, b: np.ndarray) -> float:
    mse = float(np.mean((a.astype(np.float64) - b.astype(np.float64)) ** 2))
    return float("inf") if mse == 0 else 10 * np.log10(255.0 ** 2 / mse)


def diff_stats(a: np.ndarray, b: np.ndarray) -> dict:
    d = np.abs(a.astype(np.int32) - b.astype(np.int32))
    return {
        "max_abs": int(d.max()),
        "mean_abs": round(float(d.mean()), 5),
        "share_le_1": round(float(np.mean(d <= 1)), 6),
        "share_le_2": round(float(np.mean(d <= 2)), 6),
        "count_gt_1": int(np.count_nonzero(d > 1)),
        "psnr_db": round(psnr(a, b), 3),
    }


def main() -> int:
    ours_planes, ours_rgb, ref_yuv, ref_rgb = sys.argv[1:5]
    w, h = (int(sys.argv[5]), int(sys.argv[6])) if len(sys.argv) > 6 else (1920, 1080)
    oy, ou, ov = load_planes(ours_planes, w, h)
    ry, ru, rv = load_planes(ref_yuv, w, h)
    gl = np.fromfile(ours_rgb, np.uint8).reshape(h, w, 3)
    sws = np.fromfile(ref_rgb, np.uint8).reshape(h, w, 3)

    Y = (oy.astype(np.float32) / np.float32(255))[..., None]
    CbCr = np.stack([gl_bilinear_upsample(ou, w, h), gl_bilinear_upsample(ov, w, h)], -1)
    oracle = compose.ps_ycbcr(Y, CbCr)[..., :3]
    oracle_u8 = np.round(np.clip(oracle, 0, 1) * 255).astype(np.uint8)

    out = {
        "planes_vs_ffmpeg_cli": {
            "y_bytes_differing": int(np.count_nonzero(oy != ry)),
            "u_bytes_differing": int(np.count_nonzero(ou != ru)),
            "v_bytes_differing": int(np.count_nonzero(ov != rv)),
        },
        "gl_vs_oracle_ps_ycbcr": diff_stats(gl, oracle_u8),
        "gl_vs_ffmpeg_swscale_rgb24": diff_stats(gl, sws),
        "oracle_vs_ffmpeg_swscale_rgb24": diff_stats(oracle_u8, sws),
    }
    print(json.dumps(out, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
