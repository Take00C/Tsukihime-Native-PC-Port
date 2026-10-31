"""Spike S3 data export (ROADMAP M0 scope 8, docs/status/spikes/S3.md).

Writes, under samples/spikes/s3/ (git-ignored, local only):

* pictures/<name>.rgba + pictures/manifest.json: real pictures decoded by the Python oracle
  (tsuki.catalog / tsuki.image_catalog logical pictures, RULE_29 from allpac3 as stored), RGBA8
  straight alpha, rows top-down;
* pattern/{S,D,R}.rgba + pattern/cases.json: the 256 x 256 test pattern and the list of draws the
  C# spike renders for the per-shader check (verify_s3.py computes the tsuki.compose reference);
* check/scene.json: an aligned (integer offsets, scale 1) multi-layer frame plus a rule wipe that
  both sides compose, for the whole-frame check.

Run from the repo root:
  $env:PYTHONPATH = "D:\\TsukihimePort\\tools"
  .venv\\Scripts\\python.exe src\\Spikes\\S3.Blend1080\\tools\\export_s3.py [--out samples\\spikes\\s3]
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
import time
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[4]
DEFAULT_OUT = REPO / "samples" / "spikes" / "s3"

# Pictures of the opening montage (script 3, statements 93..127 and 299..1896) and the rule of
# its only rule wipe _FADS(2,1500,29) (statement 1593).  name -> where it is used in script 3.
PICTURES = {
    "img2345": "layer 12 backdrop (2070 x 1230)",
    "img0597": "layer 5 / 0 backdrop (2070 x 1230)",
    "img2182": "layer 4, _STBR(4,24) ps_softlight",
    "img2183": "layer 13, _STBR(13,17) ps_dodge5; layer 23 opacity 200",
    "img2230": "layer 14, _STBR(14,11) ps_add",
    "img0813": "layer 15, _STBR(15,17) ps_dodge5",
    "img2232": "layer 16, _STBR(16,11) ps_add, rotated 90 deg",
    "img0700_06_06": "layer 17, _STBR(17,17) ps_dodge5",
    "img2439": "layer 18, _STBR(18,19) ps_hardlight",
    "img2163": "layer 19, _STBR(19,13) ps_darken, mirrored",
    "img0784": "layer 23, _STBR(23,22) ps_overlay",
    "img0758": "layer 25, _STBR(25,19) ps_hardlight",
    "img1685": "layer 6 (zoom 1200)",
}
RULE = ("allpac3", "RULE_29.MZP")

W, H = 1920, 1080
PAT = 256


def sha1(b: bytes) -> str:
    return hashlib.sha1(b).hexdigest()


def export_pictures(out: Path) -> dict:
    from tsuki.misc_compositing_transitions_shaders_render import Pictures
    pics = Pictures()
    d = out / "pictures"
    d.mkdir(parents=True, exist_ok=True)
    man: dict = {"generator": "src/Spikes/S3.Blend1080/tools/export_s3.py", "game": str(pics.game),
                 "layout": "raw RGBA8, straight alpha, rows top-down, width*height*4 bytes",
                 "pictures": {}}
    for name, use in PICTURES.items():
        rgba, anchor = pics.script(name)
        rgba = np.ascontiguousarray(rgba, np.uint8)
        b = rgba.tobytes()
        (d / f"{name}.rgba").write_bytes(b)
        man["pictures"][name] = {"file": f"{name}.rgba", "width": int(rgba.shape[1]),
                                 "height": int(rgba.shape[0]), "anchor": [int(anchor[0]), int(anchor[1])],
                                 "sha1": sha1(b), "use": use, "source": "logical picture (catalog)"}
    rule = np.ascontiguousarray(pics.entry(*RULE), np.uint8)
    b = rule.tobytes()
    (d / "rule29.rgba").write_bytes(b)
    man["pictures"]["rule29"] = {"file": "rule29.rgba", "width": int(rule.shape[1]),
                                 "height": int(rule.shape[0]), "anchor": [int(rule.shape[1]) // 2, int(rule.shape[0]) // 2],
                                 "sha1": sha1(b), "use": "_FADS(2,1500,29) rule (statement 1593)",
                                 "source": f"{RULE[0]} {RULE[1]} as stored"}
    (d / "manifest.json").write_text(json.dumps(man, indent=1), encoding="utf-8")
    return man


def make_pattern(seed: int = 20261025) -> dict:
    """S (layer), D (backdrop) and R (rule) 256 x 256 RGBA8.  Rows 0..127: systematic grid
    (per channel every s level against every second d level); rows 128..255: random, with ~6 % of
    the channels snapped to edge values (0, 1, 2, 3, 4, 127, 128, 254, 255)."""
    rng = np.random.default_rng(seed)
    S = rng.integers(0, 256, (PAT, PAT, 4), dtype=np.int64)
    D = rng.integers(0, 256, (PAT, PAT, 4), dtype=np.int64)
    R = rng.integers(0, 256, (PAT, PAT, 4), dtype=np.int64)
    edges = np.array([0, 1, 2, 3, 4, 127, 128, 254, 255])
    for A in (S, D, R):
        m = rng.random(A.shape) < 0.06
        A[m] = edges[rng.integers(0, len(edges), int(m.sum()))]
    y, x = np.mgrid[0:128, 0:PAT]
    S[:128, :, 0] = x
    S[:128, :, 1] = 255 - x
    S[:128, :, 2] = x
    D[:128, :, 0] = 2 * y
    D[:128, :, 1] = 2 * y + 1
    D[:128, :, 2] = 255 - 2 * y
    S[:128, :, 3] = (x * 7 + y * 3) & 255
    D[:128, :, 3] = (x * 5 + y * 11) & 255
    # S alpha: a quarter of the random half fully opaque, a tenth fully transparent
    r = rng.random((128, PAT))
    S[128:, :, 3] = np.where(r < 0.25, 255, np.where(r < 0.35, 0, S[128:, :, 3]))
    # R: smooth ramp plus noise in the top half (rule-like), random below
    R[:128, :, 0] = R[:128, :, 1] = R[:128, :, 2] = (x + y) & 255
    R[..., 3] = 255
    return {k: v.astype(np.uint8) for k, v in (("S", S), ("D", D), ("R", R))}


COLOURS = [(255, 255, 255, 255), (255, 255, 255, 144), (128, 192, 64, 200), (200, 100, 255, 0)]
PROGRESS = [1.0, 0.9, 0.75, 0.6, 0.5, 0.3, 0.25, 0.1, 0.02]


def make_cases() -> list:
    cases = []

    def add(**kw: object) -> None:
        kw["id"] = len(cases)
        cases.append(kw)

    # every _STBR mode as an ordinary layer, flagged (4th argument 1) and as the masked second pass
    for mode in range(26):
        words = [mode, 0x100 | mode, 26 + mode if 1 <= mode <= 25 else 26]
        for w in dict.fromkeys(words):
            for c in COLOURS:
                add(kind="draw", word=w, color=list(c))
    # the engine's named effects 52..67 (basic shader, blend states 2, 7, 10, 11, 5, 4, 9, 13)
    for w in range(52, 68):
        for c in COLOURS[:3]:
            add(kind="draw", word=w, color=list(c))
    # every fixed-function blend state behind the basic shader (3, 8, 12 are not used by any effect)
    for st in range(14):
        add(kind="state", state=st, color=list(COLOURS[2]))
    # transition 2DTex (PS #0) on the rule texture R with blend state 4 (effect 58), and the full
    # two-pass rule wipe (old = S with alpha 1, new = D), and the cross-fade
    for vague in (256, 64):
        for p in PROGRESS:
            add(kind="rulemask", progress=p, vague=vague, color=[255, 255, 255, 255])
            add(kind="rulewipe", progress=p, vague=vague, color=[255, 255, 255, 255])
    for p in PROGRESS:
        add(kind="crossfade", progress=p, color=[255, 255, 255, 255])
    return cases


def export_pattern(out: Path) -> dict:
    d = out / "pattern"
    d.mkdir(parents=True, exist_ok=True)
    pat = make_pattern()
    for k, v in pat.items():
        (d / f"{k}.rgba").write_bytes(v.tobytes())
    cases = make_cases()
    doc = {"width": PAT, "height": PAT, "files": {k: f"{k}.rgba" for k in pat},
           "sha1": {k: sha1(v.tobytes()) for k, v in pat.items()}, "cases": cases}
    (d / "cases.json").write_text(json.dumps(doc), encoding="utf-8")
    return doc


def centred_offset(man: dict, name: str, cx: int = W // 2, cy: int = H // 2) -> list:
    p = man["pictures"][name]
    return [cx - p["anchor"][0], cy - p["anchor"][1]]


def export_check(out: Path, man: dict) -> dict:
    """Aligned check frame: picture placed at an integer offset (top-left in frame pixels), scale 1,
    no rotation, nearest sampling, word = _STBR mode (ordinary layer), colour bytes."""
    def layer(name: str, mode: int, alpha: int = 255, dx: int = 0, dy: int = 0) -> dict:
        x, y = centred_offset(man, name)
        return {"picture": name, "x": x + dx, "y": y + dy, "word": mode, "color": [255, 255, 255, alpha]}

    scene = {
        "description": "previous frame = old layers; transition start (alpha := 1, capture); new layers; "
                       "rule pass (PS #0, state 4) and capture pass (state 6) at the given progress",
        "old": [layer("img0597", 0)],
        "new": [
            layer("img2345", 0),
            layer("img2182", 24),
            layer("img2183", 17, 200),
            layer("img2230", 11),
            layer("img0813", 17),
            layer("img2439", 19),
            layer("img0758", 19, 255, 300, 120),
            layer("img1685", 23, 160),
            layer("img0597", 24),
            layer("img0784", 22),
            layer("img2163", 13, 255, 0, 400),
            layer("img2232", 11, 128),
            layer("img0700_06_06", 17, 255, -200, 0),
        ],
        "transition": {"rule": "rule29", "progress": 0.5, "vague": 256},
    }
    d = out / "check"
    d.mkdir(parents=True, exist_ok=True)
    (d / "scene.json").write_text(json.dumps(scene, indent=1), encoding="utf-8")
    return scene


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--skip-pictures", action="store_true")
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    t = time.perf_counter()
    if a.skip_pictures:
        man = json.loads((a.out / "pictures" / "manifest.json").read_text(encoding="utf-8"))
    else:
        man = export_pictures(a.out)
    t1 = time.perf_counter()
    doc = export_pattern(a.out)
    export_check(a.out, man)
    t2 = time.perf_counter()
    total = sum(p["width"] * p["height"] * 4 for p in man["pictures"].values())
    print(f"pictures: {len(man['pictures'])}, {total:,} bytes RGBA, {t1 - t:.1f} s")
    print(f"pattern: {doc['width']}x{doc['height']}, {len(doc['cases'])} cases; check scene; {t2 - t1:.1f} s")
    print(f"out: {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
