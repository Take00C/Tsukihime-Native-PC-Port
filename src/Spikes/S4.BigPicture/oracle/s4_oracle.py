"""Spike S4 oracle: picks the S4 input pictures and records the Python reference decode.

Run (PowerShell, from D:\\TsukihimePort):

    $env:PYTHONPATH = "D:\\TsukihimePort\\tools"
    .venv\\Scripts\\python.exe src\\Spikes\\S4.BigPicture\\oracle\\s4_oracle.py

Writes samples/spikes/s4/oracle.json (local, git-ignored): for every picture the C# spike
decodes, the SHA-1 of ``tsuki.image.decode_image(...).rgba`` (displayed RGBA8, row-major,
flag-0 tiles transparent) plus the Python decode time; and the scene sets used for the
scene-load extrapolation.

Picture choice:
  * largest pictures by decoded size, from samples/inventory/*.jsonl (shown w*h*4 and
    MZX output bytes), as in docs/formats/images.md section 10;
  * one or more pictures per pixel format (PAL8, PAL4, HEP8 with 512- and 256-row tiles,
    RGB565+LO+A) for the format check;
  * "typical scene change" sets: one backdrop + 3 sprites (base + face layers), drawn with
    usage weights from every STCH/SCH2 statement of the 549 scripts (tsuki.script census),
    seed fixed so the set is reproducible.

Numbers only are printed; no game text or pixels leave samples/.
"""
from __future__ import annotations

import hashlib
import json
import random
import sys
import time
from collections import Counter
from pathlib import Path

import numpy as np

from tsuki import PROJECT_ROOT, game_dir
from tsuki import image as timg
from tsuki.image_catalog import load_catalog
from tsuki.mrg import MrgArchive
from tsuki.script import PICTURE_COMMANDS, AllScr, AssetResolver, LayerTracker, command_assets

OUT = PROJECT_ROOT / "samples" / "spikes" / "s4" / "oracle.json"
INV = PROJECT_ROOT / "samples" / "inventory"
ARCHIVES = ("allpac0", "allpac1", "allpac1_2", "allpac2", "allpac4")
SEED = 4
N_SCENES = 24


def shown_size(im: dict) -> tuple[int, int]:
    attr = im["type"][1] >> 8
    b = attr & 1
    w = im["w"] - 2 * im["tiles_x"] if b and im["w"] >= 8 else im["w"]
    h = im["h"] - 2 * im["tiles_y"] if b and im["h"] >= 8 else im["h"]
    return w, h


def inventory() -> dict[tuple[str, int], dict]:
    rows: dict[tuple[str, int], dict] = {}
    for arc in ARCHIVES:
        with open(INV / f"{arc}.jsonl", encoding="utf-8") as f:
            for line in f:
                r = json.loads(line)
                if r.get("image"):
                    rows[(arc, r["index"])] = r
    return rows


def usage_census(res: AssetResolver) -> Counter:
    c: Counter = Counter()
    with AllScr() as s:
        for i in range(len(s)):
            lt = LayerTracker()
            for cmd in s.commands(i):
                if cmd.name in PICTURE_COMMANDS:
                    for a in command_assets(cmd, res, lt):
                        if a.catalog is not None and a.layers:
                            c[(a.catalog, a.layers)] += 1
                lt.update(cmd)
    return c


def main() -> int:
    if game_dir() is None:
        print("game folder not found (set TSUKI_GAME_DIR)", file=sys.stderr)
        return 2
    t0 = time.perf_counter()
    inv = inventory()

    # 1. largest pictures by decoded size (inventory)
    by_rgba = sorted(inv.values(), key=lambda r: -(shown_size(r["image"])[0] * shown_size(r["image"])[1]))
    by_mzx = sorted(inv.values(), key=lambda r: -(r.get("deep", {}).get("out_bytes") or 0))
    largest = []
    for r in by_rgba[:4]:
        w, h = shown_size(r["image"])
        largest.append({"arc": r["archive"], "index": r["index"], "rank": "rgba", "rgba_bytes": w * h * 4,
                        "mzx_out_bytes": r["deep"]["out_bytes"], "entry_bytes": r["size"]})
    r = by_mzx[0]
    w, h = shown_size(r["image"])
    largest.append({"arc": r["archive"], "index": r["index"], "rank": "mzx_out", "rgba_bytes": w * h * 4,
                    "mzx_out_bytes": r["deep"]["out_bytes"], "entry_bytes": r["size"]})

    # 2. format coverage
    def first(pred, arcs=ARCHIVES):
        for (a, i), rr in sorted(inv.items()):
            if a in arcs and pred(rr["image"]):
                return (a, i)
        raise LookupError("no picture for predicate")

    formats = {
        "pal8_allpac0": first(lambda im: im["type"][0] == 1 and im["type"][1] & 0xF == 1, ("allpac0",)),
        "pal8_thumb": ("allpac2", 332),
        "pal4": first(lambda im: im["type"][0] == 1 and im["type"][1] & 0xF == 0),
        "rgb565": ("allpac0", 181),
        "hep8_256rows": first(lambda im: im["type"][0] == 12 and im["tile_h"] == 256, ("allpac1_2",)),
    }
    rng0 = random.Random(SEED)
    thumbs = sorted(k for k in inv if k[0] == "allpac2")
    for j, k in enumerate(rng0.sample(thumbs, 6)):
        formats[f"pal8_thumb_r{j}"] = k

    # 3. scene sets from script usage
    res = AssetResolver()
    cat = load_catalog(game_dir() / "eboot.bin")
    census = usage_census(res)
    bgs: list[tuple[int, tuple]] = []
    bg_w: list[int] = []
    spr: list[tuple[int, tuple]] = []
    spr_w: list[int] = []
    for (ci, layers), n in census.items():
        e = cat[ci]
        if e.char_id:
            spr.append((ci, layers)); spr_w.append(n)
        elif e.width * e.height >= 1920 * 1080:
            bgs.append((ci, layers)); bg_w.append(n)
    rng = random.Random(SEED)
    scenes = []
    for s in range(N_SCENES):
        bg = rng.choices(bgs, bg_w)[0]
        sp = rng.choices(spr, spr_w, k=3)
        scenes.append({"bg": {"catalog": bg[0], "layers": [list(x) for x in bg[1]]},
                       "sprites": [{"catalog": c, "layers": [list(x) for x in L]} for c, L in sp]})

    def layer_bytes(layers) -> int:
        tot = 0
        for a, i in layers:
            w, h = shown_size(inv[(a, i)]["image"])
            tot += w * h * 4
        return tot

    # heavy scene: the largest used backdrop + the 3 largest used sprites (by decoded RGBA)
    hb = max(bgs, key=lambda x: layer_bytes(x[1]))
    hs = sorted(spr, key=lambda x: -layer_bytes(x[1]))[:3]
    heavy = {"bg": {"catalog": hb[0], "layers": [list(x) for x in hb[1]]},
             "sprites": [{"catalog": c, "layers": [list(x) for x in L]} for c, L in hs]}

    usage = {
        "picture_statements": sum(census.values()),
        "distinct_pictures": len(census),
        "backdrop_candidates": len(bgs), "backdrop_statements": sum(bg_w),
        "sprite_candidates": len(spr), "sprite_statements": sum(spr_w),
        "seed": SEED,
    }
    # usage-weighted size distribution of backdrops and sprites (decoded RGBA bytes)
    def wq(items, weights, qs=(0.5, 0.9, 0.99)):
        v = np.repeat(np.array([layer_bytes(x[1]) for x in items], dtype=np.int64), weights)
        return {f"p{int(q * 100)}": int(np.quantile(v, q)) for q in qs} | {"max": int(v.max())}
    usage["backdrop_rgba_bytes"] = wq(bgs, bg_w)
    usage["sprite_rgba_bytes"] = wq(spr, spr_w)

    # 4. Python reference decode of every picture the spike uses
    want: dict[tuple[str, int], set] = {}
    for x in largest:
        want.setdefault((x["arc"], x["index"]), set()).add("largest")
    for k, v in formats.items():
        want.setdefault(tuple(v), set()).add(k)
    for sc in scenes + [heavy]:
        for L in [sc["bg"]["layers"]] + [s["layers"] for s in sc["sprites"]]:
            for a, i in L:
                want.setdefault((a, i), set()).add("scene")
    pics = []
    archives = {a: MrgArchive(game_dir() / "image" / a) for a in ARCHIVES}
    try:
        for (a, i) in sorted(want):
            t = time.perf_counter()
            img = timg.load(archives[a], i)
            dt = (time.perf_counter() - t) * 1000
            rgba = np.ascontiguousarray(img.rgba)
            h = img.header
            pics.append({"arc": a, "index": i, "name": img.name, "roles": sorted(want[(a, i)]),
                         "format": h.format.value, "stored": [h.width, h.height],
                         "tile": [h.tile_w, h.tile_h], "tiles": [h.tiles_x, h.tiles_y],
                         "shown": [rgba.shape[1], rgba.shape[0]],
                         "skipped_tiles": len(img.skipped_tiles),
                         "sha1": hashlib.sha1(rgba.tobytes()).hexdigest(), "py_ms": round(dt, 1)})
            del img, rgba
    finally:
        for m in archives.values():
            m.close()

    OUT.parent.mkdir(parents=True, exist_ok=True)
    doc = {"schema": "tsuki-s4-oracle/1", "largest": largest,
           "formats": {k: list(v) for k, v in formats.items()},
           "scenes": scenes, "heavy_scene": heavy, "usage": usage, "pictures": pics,
           "elapsed_s": round(time.perf_counter() - t0, 1)}
    OUT.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    print(f"pictures {len(pics)}, scenes {len(scenes)} + heavy, {doc['elapsed_s']} s -> {OUT}")
    print("usage", json.dumps(usage))
    for x in largest:
        print("largest", x)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
