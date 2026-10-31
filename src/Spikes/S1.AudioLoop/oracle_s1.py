"""Spike S1 oracle helper (ROADMAP M0 scope 8, stream E). Throwaway, deleted with the spike.

Uses only the Python reference library (tools/tsuki) to produce and check the data the C#
spike is compared against. Everything is written under samples/spikes/s1/ (git-ignored).

    python oracle_s1.py export [--out DIR] [--ffmpeg EXE] [--tag NAME] [--raw]
        bgm #33 -> DIR/bgm_00033.at9 (RIFF stream as stored), DIR/oracle_<tag>_trim.f32
        (tsuki.audio.decode_at9 trim=True float_output=True: the playable range
        decoded[256 : 256 + fact[0]], interleaved f32le), optionally DIR/oracle_<tag>_raw.f32
        (trim=False: all superframes * 1024 decoder samples) and DIR/oracle_<tag>.json.

    python oracle_s1.py verify-wav WAV [--oracle F32] [--loops 2] [--start FRAME]
        Compares a float32 WAV written by the C# spike with
        tsuki.audio.render_loop(oracle pcm, loops) (intro + body * (loops + 1)), sample by sample,
        and checks each loop seam window. Prints one JSON object; exit 0 iff everything is equal.

    python oracle_s1.py compare F32 F32
        Bit-level comparison of two interleaved stereo f32le files (decoder parity).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
import subprocess
import sys
import time
from pathlib import Path

import numpy as np

from tsuki import audio as taudio

REPO = Path(__file__).resolve().parents[3]
OUT_DEFAULT = REPO / "samples" / "spikes" / "s1"
BGM_INDEX = 33
EXPECTED_LOOP = (962000, 8257999)          # ROADMAP M0 spike S1, audio.md 2.5


def _sha1(b: bytes) -> str:
    return hashlib.sha1(b).hexdigest()


def _ffmpeg_version(exe: str) -> str:
    p = subprocess.run([exe, "-version"], capture_output=True, text=True)
    return p.stdout.splitlines()[0] if p.stdout else "?"


def cmd_export(a: argparse.Namespace) -> int:
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    exe = a.ffmpeg or taudio.ffmpeg_path()
    if exe is None:
        print("ffmpeg not found", file=sys.stderr)
        return 2
    with taudio.AudioBank("bgm") as b:
        e = b.entry(BGM_INDEX)
        stream = b.stream(BGM_INDEX)
    (out / "bgm_00033.at9").write_bytes(stream)
    t0 = time.perf_counter()
    pcm = taudio.decode_at9(stream, trim=True, ffmpeg=exe, float_output=True)
    t_trim = time.perf_counter() - t0
    x = np.ascontiguousarray(pcm.samples, dtype="<f4")
    blob = x.tobytes()
    (out / f"oracle_{a.tag}_trim.f32").write_bytes(blob)
    rec = {
        "bank": "bgm", "index": BGM_INDEX, "ffmpeg": exe, "ffmpeg_version": _ffmpeg_version(exe),
        "stream": {"size": len(stream), "sha1": _sha1(stream), "archive_offset": e.archive_offset},
        "fact": list(e.wave.fact) if e.wave else None,
        "smpl": [[lp.start, lp.end] for lp in e.wave.loops] if e.wave else None,
        "loop": [pcm.loop.start, pcm.loop.end] if pcm.loop else None,
        "loop_matches_roadmap": (pcm.loop.start, pcm.loop.end) == EXPECTED_LOOP if pcm.loop else False,
        "superframes": e.wave.superframes if e.wave else None,
        "trim": {"file": f"oracle_{a.tag}_trim.f32", "frames": int(x.shape[0]), "channels": int(x.shape[1]),
                 "sha1": _sha1(blob), "decode_seconds": t_trim,
                 "peak": float(np.abs(x).max()), "rms": float(np.sqrt(np.mean(x.astype(np.float64) ** 2)))},
    }
    if a.raw:
        t0 = time.perf_counter()
        raw = taudio.decode_at9(stream, trim=False, ffmpeg=exe, float_output=True)
        t_raw = time.perf_counter() - t0
        r = np.ascontiguousarray(raw.samples, dtype="<f4")
        rb = r.tobytes()
        (out / f"oracle_{a.tag}_raw.f32").write_bytes(rb)
        rec["raw"] = {"file": f"oracle_{a.tag}_raw.f32", "frames": int(r.shape[0]), "sha1": _sha1(rb),
                      "decode_seconds": t_raw,
                      "trim_equals_raw_slice": bool(np.array_equal(r[256:256 + x.shape[0]].view(np.uint32),
                                                                     x.view(np.uint32)))}
    (out / f"oracle_{a.tag}.json").write_text(json.dumps(rec, indent=1))
    print(json.dumps(rec, indent=1))
    return 0


def _read_f32_wav(path: Path) -> tuple[np.ndarray, int, int]:
    d = path.read_bytes()
    if d[:4] != b"RIFF" or d[8:12] != b"WAVE":
        raise ValueError("not a RIFF/WAVE file")
    o = 12
    fmt = None
    data = None
    while o + 8 <= len(d):
        cid, size = d[o:o + 4], struct.unpack_from("<I", d, o + 4)[0]
        if cid == b"fmt ":
            fmt = struct.unpack_from("<HHIIHH", d, o + 8)
        elif cid == b"data":
            data = d[o + 8:o + 8 + size]
        o += 8 + size + (size & 1)
    if fmt is None or data is None:
        raise ValueError("fmt or data chunk missing")
    tag, ch, rate, _abps, _align, bits = fmt
    if tag != 3 or bits != 32:
        raise ValueError(f"expected IEEE float32 WAV, got tag {tag}, {bits} bits")
    return np.frombuffer(data, dtype="<f4").reshape(-1, ch), ch, rate


def _load_oracle(path: Path) -> np.ndarray:
    return np.fromfile(path, dtype="<f4").reshape(-1, 2)


def _seam_j(x: np.ndarray, p: int) -> float:
    """scratch/audio/seam_jump.py J at output frame p (first frame after the seam), int16 units:
    |x[p] - (2 x[p-1] - x[p-2])| / (median |diff| of x[p-1001 : p] + 1), max over channels."""
    y = x.astype(np.float64) * 32768.0
    ee = p - 1
    step = np.median(np.abs(np.diff(y[ee - 1000:ee + 1], axis=0)), axis=0) + 1.0
    pred = 2 * y[ee] - y[ee - 1]
    return round(float(np.max(np.abs(y[p] - pred) / step)), 3)


def cmd_verify_wav(a: argparse.Namespace) -> int:
    got, ch, rate = _read_f32_wav(Path(a.wav))
    oracle = _load_oracle(Path(a.oracle))
    pcm = taudio.Pcm(oracle, 48000, taudio.LoopInfo(EXPECTED_LOOP[0], EXPECTED_LOOP[1], False), True)
    ref = taudio.render_loop(pcm, a.loops)          # the oracle's own loop renderer
    ref = ref[a.start:]
    res: dict[str, object] = {"wav": str(a.wav), "channels": ch, "rate": rate,
                              "frames_got": int(got.shape[0]), "frames_ref": int(ref.shape[0])}
    n = min(got.shape[0], ref.shape[0])
    g, r = got[:n], ref[:n]
    neq = g != r                                     # numeric equality: -0.0 == +0.0
    bit_neq = g.view(np.uint32) != r.view(np.uint32)
    res["samples_compared"] = int(n * ch)
    res["samples_not_equal"] = int(neq.sum())
    res["samples_not_bit_identical"] = int(bit_neq.sum())
    res["max_abs_diff"] = float(np.abs(g.astype(np.float64) - r.astype(np.float64)).max()) if n else 0.0
    lp = pcm.loop
    assert lp is not None
    seams = []
    for k in range(a.loops):
        p = lp.start + (k + 1) * lp.length - a.start   # output frame that holds playable[loop.start] again
        lo, hi = max(p - 4800, 0), min(p + 4800, n)
        win_neq = int((g[lo:hi] != r[lo:hi]).sum())
        seams.append({"seam": k + 1, "output_frame": int(p),
                      "J_got": _seam_j(g, p), "J_oracle_render": _seam_j(r, p),
                      "last_before": [float(v) for v in g[p - 1]] if 0 < p <= n else None,
                      "first_after": [float(v) for v in g[p]] if 0 <= p < n else None,
                      "oracle_end": [float(v) for v in oracle[lp.end]],
                      "oracle_start": [float(v) for v in oracle[lp.start]],
                      "window_frames": [int(lo), int(hi)], "window_samples_not_equal": win_neq})
    res["seams"] = seams
    res["first_mismatch_frame"] = int(np.argmax(neq.any(axis=1))) if neq.any() else None
    ok = got.shape[0] == ref.shape[0] and not neq.any() and all(s["window_samples_not_equal"] == 0 for s in seams)
    res["pass"] = bool(ok)
    print(json.dumps(res, indent=1))
    return 0 if ok else 1


def cmd_compare(a: argparse.Namespace) -> int:
    x = np.fromfile(a.a, dtype="<f4")
    y = np.fromfile(a.b, dtype="<f4")
    n = min(x.size, y.size)
    d = np.abs(x[:n].astype(np.float64) - y[:n].astype(np.float64))
    res = {"a": a.a, "b": a.b, "samples_a": int(x.size), "samples_b": int(y.size),
           "not_bit_identical": int((x[:n].view(np.uint32) != y[:n].view(np.uint32)).sum()),
           "max_abs_diff": float(d.max()) if n else 0.0}
    print(json.dumps(res, indent=1))
    return 0 if x.size == y.size and res["not_bit_identical"] == 0 else 1


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("export")
    p.add_argument("--out", default=str(OUT_DEFAULT))
    p.add_argument("--ffmpeg", default=None)
    p.add_argument("--tag", default="gyan")
    p.add_argument("--raw", action="store_true")
    p = sub.add_parser("verify-wav")
    p.add_argument("wav")
    p.add_argument("--oracle", default=str(OUT_DEFAULT / "oracle_gyan_trim.f32"))
    p.add_argument("--loops", type=int, default=2)
    p.add_argument("--start", type=int, default=0)
    p = sub.add_parser("compare")
    p.add_argument("a")
    p.add_argument("b")
    a = ap.parse_args()
    return {"export": cmd_export, "verify-wav": cmd_verify_wav, "compare": cmd_compare}[a.cmd](a)


if __name__ == "__main__":
    raise SystemExit(main())
