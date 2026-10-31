"""Test helper of AlignedFrameTests (D1): decode the pictures of the aligned check frame with the Python
oracle and write them as raw RGBA8 (straight alpha, rows top-down) plus manifest.json into a folder the
C# test owns and deletes afterwards (the data is game-derived and never stays in %TEMP%).

The pictures are those of spike S3's check frame (docs/status/spikes/S3.md 4.3): logical pictures of
the opening montage (catalog records, tsuki.catalog / tsuki.image_catalog) and RULE_29 from allpac3
as stored.

  $env:PYTHONPATH = "D:\\TsukihimePort\\tools"
  .venv\\Scripts\\python.exe tests\\Tsuki.Tests.Render\\Compositor\\export_pictures.py <folder>
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

import numpy as np

PICTURES = ["img0597", "img2345", "img2182", "img2183", "img2230", "img0813", "img2439", "img0758",
            "img1685", "img0784", "img2163", "img2232", "img0700_06_06"]
RULE = ("allpac3", "RULE_29.MZP")


def main(out: Path) -> int:
    from tsuki.misc_compositing_transitions_shaders_render import Pictures

    t0 = time.perf_counter()
    pics = Pictures()
    out.mkdir(parents=True, exist_ok=True)
    man: dict = {"pictures": {}}
    for name in PICTURES:
        rgba, anchor = pics.script(name)
        rgba = np.ascontiguousarray(rgba, np.uint8)
        (out / f"{name}.rgba").write_bytes(rgba.tobytes())
        man["pictures"][name] = {"file": f"{name}.rgba", "width": int(rgba.shape[1]), "height": int(rgba.shape[0]),
                                 "anchor": [int(anchor[0]), int(anchor[1])]}
    rule = np.ascontiguousarray(pics.entry(*RULE), np.uint8)
    (out / "rule29.rgba").write_bytes(rule.tobytes())
    man["pictures"]["rule29"] = {"file": "rule29.rgba", "width": int(rule.shape[1]), "height": int(rule.shape[0]),
                                 "anchor": [int(rule.shape[1]) // 2, int(rule.shape[0]) // 2]}
    (out / "manifest.json").write_text(json.dumps(man, indent=1), encoding="utf-8")
    print(f"export_pictures: {len(man['pictures'])} pictures in {time.perf_counter() - t0:.1f} s -> {out}")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
    sys.exit(main(Path(sys.argv[1])))
