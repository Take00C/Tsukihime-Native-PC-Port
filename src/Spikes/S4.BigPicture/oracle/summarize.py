"""Spike S4: aggregate samples/spikes/s4/*.json into summary.json and summary.md (numbers only).

    .venv\\Scripts\\python.exe src\\Spikes\\S4.BigPicture\\oracle\\summarize.py samples\\spikes\\s4
"""
from __future__ import annotations

import json
import math
import re
import sys
from collections import defaultdict
from pathlib import Path


def num(x) -> float:
    """JSON numbers; System.Text.Json writes NaN as the string "NaN"."""
    try:
        return float(x)
    except (TypeError, ValueError):
        return float("nan")


def q(xs: list, p: float) -> float:
    xs = sorted(v for v in map(num, xs) if not math.isnan(v))
    if not xs:
        return float("nan")
    pos = p * (len(xs) - 1)
    lo, hi = math.floor(pos), math.ceil(pos)
    return round(xs[lo] + (xs[hi] - xs[lo]) * (pos - lo), 3)


def main(out: Path) -> int:
    summary: dict = {}
    # decode runs
    dec = {}
    for f in sorted(out.glob("decode_*.json")):
        r = json.loads(f.read_text())["result"]
        w = r["decode_ms_by_workers"]
        dec[f"{r['arc']}#{r['index']}"] = {
            "name": r["name"], "format": r["format"], "entry_bytes": r["entry_bytes"], "shown": r["shown"],
            "tile": r["tile"], "tiles": r["tiles"], "live_tiles": r["live_tiles"],
            "mzx_out_bytes": r["mzx_out_bytes"], "rgba_shown_bytes": r["rgba_shown_bytes"], "rgba_tile_bytes": r["rgba_tile_bytes"],
            "read_ms_first": r["read_ms_first"], "read_ms_cached": r["read_ms_cached_median"],
            "read_ms_uncached": r["read_ms_uncached"], "read_mb_s_uncached": r["read_mb_s_uncached_median"],
            "decode_ms_cold_16w": r["decode_ms_cold_16w"],
            "decode_ms_median": {k: v["median"] for k, v in w.items()},
            "decode_ms_min": {k: v["min"] for k, v in w.items()},
            "st_mzx_ms": r["st_mzx_ms"]["min"], "st_mzx_mb_s_out": r["st_mzx_mb_s_out"], "st_mzx_mb_s_in": r["st_mzx_mb_s_in"],
            "st_convert_ms": r["st_convert_ms"]["min"], "st_convert_mpix_s": r["st_convert_mpix_s"],
            "total_1w": r["total_ms_read_cached_plus_decode_1w"], "total_16w": r["total_ms_read_cached_plus_decode_16w"],
            "total_uncached_16w": r["total_ms_read_uncached_plus_decode_16w"],
            "sha1_equal": r.get("sha1_equal"), "sha1": r["sha1"], "oracle_py_ms": r.get("oracle_py_ms"),
            "peak_ws_mb_after_cold_decode": r["memory_after_cold_decode"]["peak_working_set_mb"],
            "peak_private_mb_after_cold_decode": r["memory_after_cold_decode"]["peak_private_mb"],
            "native_peak_mb": r["memory_after_cold_decode"]["native_peak_mb"],
            "managed_heap_mb_after_cold_decode": r["memory_after_cold_decode"]["managed_heap_mb"],
            "gc_during_decode": r["gc_during_decode"],
            "peak_ws_mb_end": r["memory_end"]["peak_working_set_mb"],
        }
    summary["decode"] = dec

    v = out / "verify.json"
    if v.exists():
        r = json.loads(v.read_text())["result"]
        summary["verify"] = {k: r[k] for k in ("pictures", "sha1_equal", "sha1_different", "by_format")}

    s = out / "scenes.json"
    if s.exists():
        r = json.loads(s.read_text())["result"]
        summary["scenes"] = {"typical": r["typical_aggregate"],
                             "heavy": next(x for x in r["scenes"] if x["label"] == "heavy"),
                             "usage": r["usage"]}

    # upload runs, grouped by config (label without _rN)
    groups: dict[str, list[dict]] = defaultdict(list)
    for f in sorted(out.glob("upload_*.json")):
        m = re.match(r"upload_(.+)_r(\d+)\.json$", f.name)
        if not m:
            continue
        groups[m.group(1)].append(json.loads(f.read_text())["result"])
    up = {}
    for name, runs in groups.items():
        # pooled frame times are not stored per frame; use per-run distributions
        load_max = [x["frame_ms_load"]["max"] for x in runs]
        load_p99 = [x["frame_ms_load"]["p99"] for x in runs]
        load_p50 = [x["frame_ms_load"]["p50"] for x in runs]
        rb = [all(y["equal"] for y in x["readback_verify"]) for x in runs if x.get("readback_verify")]
        up[name] = {
            "runs": len(runs),
            "layers": runs[0]["layers"],
            "tiles": runs[0]["tiles"], "upload_mb": round(runs[0]["upload_bytes"] / 1048576, 1),
            "options": {k: runs[0]["options"].get(k) for k in ("TilesPerFrame", "BudgetMs", "BudgetMb", "Workers", "Prealloc", "Pbo", "TexPool", "StagingMb")},
            "warm_frames_total": sum(x["refresh_ms_warm"]["n"] for x in runs),
            "warm_frames_over_16_7_total": sum(x.get("frames_over_16_7ms_warm", 0) for x in runs),
            "load_frames_over_16_7_total": sum(x["frames_over_16_7ms_load"] for x in runs),
            "runs_with_load_hitch": sum(1 for x in runs if x["frames_over_16_7ms_load"] > 0),
            "hitch_frames": [{k: y[k] for k in ("ms", "Tiles", "upload_cpu_ms", "draw_ms", "swap_ms", "gpu_ms") if k in y}
                             for x in runs for y in x["worst_load_frames"] if y["ms"] > 16.7],
            "draw_ms_load_max": max(num(x.get("draw_ms_load", {}).get("max")) for x in runs),
            "swap_ms_load_p50": q([x.get("swap_ms_load", {}).get("p50") for x in runs], 0.5),
            "vsync_ms_p50": q([x["refresh_ms_warm"]["p50"] for x in runs], 0.5),
            "warm_frame_max_ms": max(x["refresh_ms_warm"]["max"] for x in runs),
            "load_frames": [x["load_frames"] for x in runs],
            "load_frames_total": sum(x["frame_ms_load"]["n"] for x in runs),
            "load_frame_ms_p50_median": q(load_p50, 0.5),
            "load_frame_ms_p99_max": max(load_p99),
            "load_frame_ms_max_per_run": load_max,
            "frames_over_16_7_per_run": [x["frames_over_16_7ms_load"] for x in runs],
            "frames_over_1_5_vsync_per_run": [x["frames_over_1_5_vsync_load"] for x in runs],
            "decode_wall_ms": [x["decode_wall_ms"] for x in runs],
            "load_wall_ms": [x["load_wall_ms"] for x in runs],
            "upload_cpu_ms_per_frame_p50": q([x["upload_cpu_ms_per_frame"]["p50"] for x in runs], 0.5),
            "upload_cpu_ms_per_frame_max": max(x["upload_cpu_ms_per_frame"]["max"] for x in runs) if runs[0]["upload_cpu_ms_per_frame"]["n"] else None,
            "upload_gpu_ms_per_frame_max": max(x["upload_gpu_ms_per_frame"]["max"] for x in runs) if runs[0]["upload_gpu_ms_per_frame"]["n"] else None,
            "upload_cpu_ms_per_tile_p50": q([x["upload_cpu_ms_per_tile"]["p50"] for x in runs], 0.5),
            "upload_cpu_ms_per_tile_p99_max": max(x["upload_cpu_ms_per_tile"]["p99"] for x in runs) if runs[0]["upload_cpu_ms_per_tile"]["n"] else None,
            "upload_cpu_ms_per_tile_max": max(x["upload_cpu_ms_per_tile"]["max"] for x in runs) if runs[0]["upload_cpu_ms_per_tile"]["n"] else None,
            "mb_per_load_frame": q([x["mb_per_load_frame"] for x in runs], 0.5),
            "prealloc_ms": [x["prealloc_ms"] for x in runs] if runs[0]["options"]["Prealloc"] else None,
            "readback_equal_runs": f"{sum(rb)}/{len(rb)}" if rb else None,
            "peak_ws_mb": max(x["memory_after_upload"]["peak_working_set_mb"] for x in runs),
            "peak_private_mb": max(x["memory_after_upload"]["peak_private_mb"] for x in runs),
            "native_peak_mb": max(x["memory_after_upload"]["native_peak_mb"] for x in runs),
            "machine_busy_pct_load": [x.get("cpu_load", {}).get("machine_busy_pct") for x in runs],
            "other_processes_pct_load": [x.get("cpu_load", {}).get("other_processes_pct") for x in runs],
            "machine_busy_pct_warm": [x.get("cpu_warm", {}).get("machine_busy_pct") for x in runs],
            "gl": runs[0].get("gl_renderer"),
        }
    summary["upload"] = up
    (out / "summary.json").write_text(json.dumps(summary, indent=1))

    lines = ["# S4 summary (generated)", ""]
    for k, d in dec.items():
        lines.append(f"- decode {k} {d['name']} {d['format']}: read cached {d['read_ms_cached']} ms, uncached "
                     f"{d['read_ms_uncached']} ms; cold 16w {d['decode_ms_cold_16w']} ms; median by workers {d['decode_ms_median']}; "
                     f"ST MZX0 {d['st_mzx_ms']} ms ({d['st_mzx_mb_s_out']} MB/s out), convert {d['st_convert_ms']} ms; "
                     f"sha1 equal {d['sha1_equal']}; peak WS {d['peak_ws_mb_after_cold_decode']} MB")
    for k, d in up.items():
        lines.append(f"- upload {k}: {d['tiles']} tiles {d['upload_mb']} MB; load frames {d['load_frames']}; "
                     f"frame max/run {d['load_frame_ms_max_per_run']}; >16.7 {d['frames_over_16_7_per_run']} "
                     f"(warm {d['warm_frames_over_16_7_total']}/{d['warm_frames_total']}); "
                     f"decode {d['decode_wall_ms']}; load {d['load_wall_ms']}; readback {d['readback_equal_runs']}; "
                     f"busy {d['machine_busy_pct_load']}")
    (out / "summary.md").write_text("\n".join(lines) + "\n")
    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[4] / "samples" / "spikes" / "s4"))
