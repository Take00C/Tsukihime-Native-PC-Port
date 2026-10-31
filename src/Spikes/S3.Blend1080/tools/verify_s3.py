"""Spike S3: compare the C# / GLSL renders with the Python oracle (tsuki.compose).

Inputs (samples/spikes/s3/, written by export_s3.py and `S3.Blend1080 verify`):
  pattern/{S,D,R}.rgba, pattern/cases.json, verify/gpu_cases.rgba, verify/gpu_meta.json,
  check/scene.json, check/gpu_full.rgba, check/gpu_bounds.rgba, pictures/*.
Outputs: verify/report.json, check/*.png previews (and bench/*.png for bench snapshots).

Error metric: per channel |GPU byte - to_u8(reference)| in UNORM8 levels (1 level = 1/255), where
the reference is the compose result stored the way an RGBA8 target stores it (compose.to_u8:
clamp, round to nearest).  Multi-pass references store each pass to UNORM8 too.  Two export
models of the shader output are evaluated:
  f32     compose as it is (float32 shader output straight into the blend);
  f16rtz  the shader output rounded to fp16 toward zero first: the PS4's packed MRT0 export
          (v_cvt_pkrtz_f16_f32, compositing.md 2.1), which the AMD GL driver also uses for RGBA8.

  $env:PYTHONPATH = "D:\\TsukihimePort\\tools"
  .venv\\Scripts\\python.exe src\\Spikes\\S3.Blend1080\\tools\\verify_s3.py
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path
from typing import Callable, Dict

import numpy as np

from tsuki import compose as cp

F = np.float32
REPO = Path(__file__).resolve().parents[4]
DEFAULT_DATA = REPO / "samples" / "spikes" / "s3"
W, H = 1920, 1080


def rule_params_v(progress: float, vague: int):
    """compose.rule_params with the vague width b of 0xD73B0(tex, a, b) as a parameter (the engine
    passes b = 256; for b = 256 this is exactly compose.rule_params)."""
    a = 256 - int(F(progress) * F(256))
    x0 = F(a) * F(0.0078125)
    x1 = x0 + F(vague) * F(-0.00390625)
    return float(max(F(-1), x1)), float(min(F(2), x0)), 0.0


def q(x: np.ndarray) -> np.ndarray:
    """Store to an RGBA8 target and read back."""
    return cp.to_float(cp.to_u8(x))


def rtz16(x: np.ndarray) -> np.ndarray:
    """float32 -> fp16 rounded toward zero -> float32."""
    x = np.nan_to_num(np.asarray(x, np.float32))
    y = x.astype(np.float16)
    over = np.abs(y.astype(np.float32)) > np.abs(x)
    return np.where(over, np.nextafter(y, np.float16(0)), y).astype(np.float32)


EXPORTS: Dict[str, Callable[[np.ndarray], np.ndarray]] = {
    "f32": lambda x: np.asarray(x, np.float32),
    "f16rtz": lambda x: rtz16(np.clip(x, 0, 1)),
}


def draw_ref(D: np.ndarray, S: np.ndarray, C: np.ndarray, word: int, export: str) -> np.ndarray:
    """compose.draw with the shader output passed through the export model ("f32" = compose.draw)."""
    eff = cp.effect(word)
    if eff.shader_mode == 0:
        out = cp.ps_basic_2dtex(S, C)
    else:
        out = cp.ps_blend(eff.shader_mode, S, D, C, cp.common_ps_params(eff))
    return cp.apply_blend_state(EXPORTS[export](out), D, eff.blend_state)


def colour(c) -> np.ndarray:
    return np.array(c, np.float32) / F(255)


def err_levels(gpu: np.ndarray, ref: np.ndarray, rgb_only: bool = False) -> np.ndarray:
    e = np.abs(gpu.astype(np.int16) - cp.to_u8(ref).astype(np.int16))
    return e[..., :3] if rgb_only else e


def stats(e: np.ndarray) -> dict:
    px = e.reshape(-1, e.shape[-1]).max(axis=1)
    return {"max": int(e.max()), "px_exact": float((px == 0).mean()), "px_le1": float((px <= 1).mean()),
            "px_le2": float((px <= 2).mean()), "n_px": int(px.size)}


def verify_cases(data: Path) -> dict:
    pdir = data / "pattern"
    doc = json.loads((pdir / "cases.json").read_text(encoding="utf-8"))
    w, h = doc["width"], doc["height"]

    def load(k: str) -> np.ndarray:
        return np.fromfile(pdir / f"{k}.rgba", np.uint8).reshape(h, w, 4)

    S, D, R = load("S"), load("D"), load("R")
    Sf, Df, Rf = cp.to_float(S), cp.to_float(D), cp.to_float(R)
    old = S.copy()
    old[..., 3] = 255
    oldf = cp.to_float(old)
    ones = np.ones(4, np.float32)
    cases = doc["cases"]
    gpu = np.fromfile(data / "verify" / "gpu_cases.rgba", np.uint8)
    assert gpu.size == len(cases) * w * h * 4, (gpu.size, len(cases))
    gpu = gpu.reshape(len(cases), h, w, 4)
    for p in (1.0, 0.9, 0.75, 0.5, 0.25, 0.02):
        assert rule_params_v(p, 256) == cp.rule_params(p), p

    rows = []
    with np.errstate(all="ignore"):
        for cs, g in zip(cases, gpu):
            C = colour(cs["color"])
            k = cs["kind"]
            row: dict = {"id": cs["id"], "kind": k, "color": cs["color"]}
            for ex, X in EXPORTS.items():
                if k == "draw":
                    ref = draw_ref(Df, Sf, C, cs["word"], ex)
                elif k == "state":
                    ref = cp.apply_blend_state(X(cp.ps_basic_2dtex(Sf, C)), Df, cs["state"])
                elif k == "rulemask":
                    P = rule_params_v(cs["progress"], cs["vague"])
                    ref = cp.apply_blend_state(X(cp.ps_transition_2dtex(Rf, C, P)), Df, 4)
                elif k == "rulewipe":
                    P = rule_params_v(cs["progress"], cs["vague"])
                    mask = q(cp.apply_blend_state(X(cp.ps_transition_2dtex(Rf, C, P)), Df, 4))
                    ref = cp.apply_blend_state(X(cp.ps_basic_2dtex(oldf, ones)), mask, 6)
                elif k == "crossfade":
                    wgt = cp.fads_vertex_alpha(cs["progress"])
                    ref = cp.apply_blend_state(X(cp.ps_basic_2dtex(oldf, np.array([1, 1, 1, wgt], np.float32))), Df, 1)
                else:
                    raise ValueError(k)
                row[ex] = stats(err_levels(g, ref))
            if k == "draw":
                eff = cp.effect(cs["word"])
                row.update(word=cs["word"], shader_mode=eff.shader_mode, blend_state=eff.blend_state,
                           strength=eff.strength, p2=eff.p2, flag=eff.flag)
            elif k == "state":
                row["blend_state"] = cs["state"]
            else:
                row["progress"] = cs["progress"]
                if "vague" in cs:
                    row["vague"] = cs["vague"]
                    row["lo_hi"] = rule_params_v(cs["progress"], cs["vague"])[:2]
            if k == "rulewipe" and cs["vague"] == 256:
                # compose.rule_wipe itself: float mask, no UNORM8 store of the mask in between
                row["vs_rule_wipe_rgb"] = stats(err_levels(g, cp.rule_wipe(oldf, Df, Rf, cs["progress"]), True))
            if k == "crossfade":
                row["vs_crossfade_rgb"] = stats(err_levels(g, cp.crossfade(oldf, Df, cs["progress"]), True))
            rows.append(row)

    def summarise(rs: list) -> dict:
        out = {"cases": len(rs)}
        for ex in EXPORTS:
            out[ex] = {"max": max(r[ex]["max"] for r in rs), "px_exact_min": min(r[ex]["px_exact"] for r in rs),
                       "px_le1_min": min(r[ex]["px_le1"] for r in rs),
                       "px_exact_mean": float(np.mean([r[ex]["px_exact"] for r in rs]))}
        return out

    names = {0: "basic 2DTex", **{m: n for m, (n, _b) in cp.BLEND_SHADER_NAMES.items()}}
    by_mode: dict = defaultdict(list)
    for r in rows:
        if r["kind"] == "draw" and r["word"] & 0xFF < 52:
            by_mode[r["shader_mode"]].append(r)
    modes = {m: {"name": names.get(m, "?"), "words": sorted({r["word"] for r in by_mode[m]}), **summarise(by_mode[m])}
             for m in sorted(by_mode)}
    groups = {}
    sel = {
        "effects 52..67": [r for r in rows if r["kind"] == "draw" and r["word"] >= 52],
        "blend states 0..13": [r for r in rows if r["kind"] == "state"],
        "rule mask (PS #0, state 4)": [r for r in rows if r["kind"] == "rulemask"],
        "rule wipe (2 passes)": [r for r in rows if r["kind"] == "rulewipe"],
        "cross-fade": [r for r in rows if r["kind"] == "crossfade"],
    }
    for name, rs in sel.items():
        groups[name] = summarise(rs)
    for name, key in (("rule wipe rgb vs compose.rule_wipe", "vs_rule_wipe_rgb"), ("cross-fade rgb vs compose.crossfade", "vs_crossfade_rgb")):
        rs = [r[key] for r in rows if key in r]
        groups[name] = {"cases": len(rs), "max": max(r["max"] for r in rs), "px_le1_min": min(r["px_le1"] for r in rs),
                        "px_exact_min": min(r["px_exact"] for r in rs)}
    return {"pattern": f"{w}x{h}", "cases": len(rows),
            "max_all": {ex: max(r[ex]["max"] for r in rows) for ex in EXPORTS},
            "by_shader_mode": modes, "groups": groups, "rows": rows}


def load_pictures(data: Path) -> dict:
    man = json.loads((data / "pictures" / "manifest.json").read_text(encoding="utf-8"))
    return {name: np.fromfile(data / "pictures" / e["file"], np.uint8).reshape(e["height"], e["width"], 4)
            for name, e in man["pictures"].items()}


def compose_check(scene: dict, pics: dict, quantise: bool, export: str) -> dict:
    """The check frame composed by compose: per-pass UNORM8 store (``quantise``) or a float
    pipeline, with the given export model.  Returns the frame and its intermediates."""
    X = EXPORTS[export]
    store = q if quantise else (lambda x: np.clip(x, 0, 1).astype(np.float32))
    ones = np.ones(4, np.float32)

    def layers(rt: np.ndarray, ls: list) -> None:
        for l in ls:
            pic = pics[l["picture"]]
            ph, pw = pic.shape[:2]
            x, y = l["x"], l["y"]
            x0, y0, x1, y1 = max(0, x), max(0, y), min(W, x + pw), min(H, y + ph)
            if x1 <= x0 or y1 <= y0:
                continue
            S = cp.to_float(pic[y0 - y:y1 - y, x0 - x:x1 - x])
            rt[y0:y1, x0:x1] = store(draw_ref(rt[y0:y1, x0:x1], S, colour(l["color"]), l["word"], export))

    with np.errstate(all="ignore"):
        rt = np.zeros((H, W, 4), np.float32)
        layers(rt, scene["old"])
        rt = store(cp.apply_blend_state(X(ones), rt, 11))          # effect 56 fill: alpha := max(1, da)
        capture = rt.copy()
        rt = np.zeros((H, W, 4), np.float32)
        layers(rt, scene["new"])
        new = rt.copy()
        tr = scene["transition"]
        P = rule_params_v(tr["progress"], tr["vague"])
        rule = cp.to_float(pics[tr["rule"]])
        rt = store(cp.apply_blend_state(X(cp.ps_transition_2dtex(rule, ones, P)), rt, 4))
        rt = store(cp.apply_blend_state(X(cp.ps_basic_2dtex(capture, ones)), rt, 6))
    return {"frame": rt, "new": new, "capture": capture, "rule": rule}


def save_png(rgba_u8: np.ndarray, path: Path) -> None:
    from PIL import Image
    Image.fromarray(np.ascontiguousarray(rgba_u8[..., :3]), "RGB").save(path)


def verify_check(data: Path) -> dict:
    pics = load_pictures(data)
    scene = json.loads((data / "check" / "scene.json").read_text(encoding="utf-8"))
    refs = {f"{ex}_u8passes": compose_check(scene, pics, True, ex) for ex in EXPORTS}
    refs["f32_float_pipeline"] = compose_check(scene, pics, False, "f32")
    base = refs["f32_float_pipeline"]
    rw = cp.rule_wipe(base["capture"], base["new"], base["rule"], scene["transition"]["progress"])
    save_png(cp.to_u8(refs["f16rtz_u8passes"]["frame"]), data / "check" / "ref_compose_f16rtz.png")
    out: dict = {}
    gpus = {}
    for mode in ("full", "bounds"):
        g = np.fromfile(data / "check" / f"gpu_{mode}.rgba", np.uint8).reshape(H, W, 4)
        gpus[mode] = g
        out[mode] = {name: stats(err_levels(g, r["frame"])) for name, r in refs.items()}
        out[mode]["rgb_vs_compose_rule_wipe_float"] = stats(err_levels(g, rw, True))
        save_png(g, data / "check" / f"gpu_{mode}.png")
        e = err_levels(g, refs["f16rtz_u8passes"]["frame"]).max(axis=-1)
        diff = np.clip(e.astype(np.int32) * 64, 0, 255).astype(np.uint8)
        save_png(np.stack([diff] * 3 + [diff], -1), data / "check" / f"diff_{mode}_f16rtz_x64.png")
    out["full_vs_bounds_identical"] = bool(np.array_equal(gpus["full"], gpus["bounds"]))
    out["full_vs_bounds_max"] = int(np.abs(gpus["full"].astype(np.int16) - gpus["bounds"].astype(np.int16)).max())
    # reference-model spread: how far the two compose models are from each other on this frame
    out["model_spread_f32_vs_f16rtz"] = stats(np.abs(cp.to_u8(refs["f32_u8passes"]["frame"]).astype(np.int16)
                                                     - cp.to_u8(refs["f16rtz_u8passes"]["frame"]).astype(np.int16)))
    return out


def bench_previews(data: Path) -> list:
    done = []
    bdir = data / "bench"
    for f in sorted(bdir.glob("*.rgba")) if bdir.is_dir() else []:
        g = np.fromfile(f, np.uint8)
        if g.size == W * H * 4:
            save_png(g.reshape(H, W, 4), f.with_suffix(".png"))
            done.append(f.with_suffix(".png").name)
    return done


def pct(x: float) -> str:
    return f"{100 * x:.3f}%"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--data", type=Path, default=DEFAULT_DATA)
    a = ap.parse_args()
    rep = {"cases": verify_cases(a.data), "check": verify_check(a.data), "bench_previews": bench_previews(a.data)}
    meta = a.data / "verify" / "gpu_meta.json"
    if meta.exists():
        rep["gpu_meta"] = json.loads(meta.read_text(encoding="utf-8"))
    (a.data / "verify" / "report.json").write_text(json.dumps(rep, indent=1), encoding="utf-8")

    c = rep["cases"]
    print(f"pattern {c['pattern']}, {c['cases']} cases; max error over all cases (levels): {c['max_all']}")
    print(f"{'mode':>4} {'shader':<15} {'n':>3} | {'f32 max':>7} {'exact min':>10} {'exact mean':>10} | {'f16rtz max':>10} {'exact min':>10} {'exact mean':>10}")
    for m, r in c["by_shader_mode"].items():
        a32, a16 = r["f32"], r["f16rtz"]
        print(f"{m:>4} {r['name']:<15} {r['cases']:>3} | {a32['max']:>7} {pct(a32['px_exact_min']):>10} {pct(a32['px_exact_mean']):>10} | "
              f"{a16['max']:>10} {pct(a16['px_exact_min']):>10} {pct(a16['px_exact_mean']):>10}")
    for k, r in c["groups"].items():
        if "f32" in r:
            print(f"{k:<36} n {r['cases']:>3} | f32 max {r['f32']['max']} exact min {pct(r['f32']['px_exact_min'])} "
                  f"| f16rtz max {r['f16rtz']['max']} exact min {pct(r['f16rtz']['px_exact_min'])} <=1 min {pct(r['f16rtz']['px_le1_min'])}")
        else:
            print(f"{k:<36} n {r['cases']:>3} | max {r['max']} <=1 min {pct(r['px_le1_min'])} exact min {pct(r['px_exact_min'])}")
    for mode in ("full", "bounds"):
        for name, s in rep["check"][mode].items():
            print(f"check frame ({mode:>6} blits) vs {name:<34}: max {s['max']}, exact {pct(s['px_exact'])}, "
                  f"<=1 {pct(s['px_le1'])}, <=2 {pct(s['px_le2'])}")
    print("full vs bounds identical:", rep["check"]["full_vs_bounds_identical"], "max", rep["check"]["full_vs_bounds_max"])
    s = rep["check"]["model_spread_f32_vs_f16rtz"]
    print(f"model spread f32 vs f16rtz on the check frame: max {s['max']}, <=2 {pct(s['px_le2'])}")
    print("bench previews:", rep["bench_previews"])
    print("wrote", a.data / "verify" / "report.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
