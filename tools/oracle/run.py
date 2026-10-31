"""Golden-data runner.

    python -m oracle.run --list
    python -m oracle.run slice [--parts eboot,scripts,...] [--out DIR] [--game DIR] [--quick]
    python -m oracle.run slice --verify [--game DIR] [--quick] [--no-provenance] [--strict-provenance]

Exports an area to ``samples/golden/<area>/`` (default; local only, never committed) and then
verifies the result: every file listed in the manifest must exist with its size and SHA-1, no
unlisted file may be present, and every cross-check the exporter recorded must have passed
(``manifest.verify_area``).  Then the provenance (``manifest.provenance_problems``): no tool
source a part used may have changed since that part was exported, and every input file and
archive entry must still have its recorded size and hash (game inputs only when the game folder
is found; ``--quick`` re-hashes only inputs up to 64 MB; ``--no-provenance`` skips this step).
A part manifest written before ``tool_used`` existed cannot say which tool sources it used; its
changed sources are printed as notes ("unattributed") unless ``--strict-provenance`` is given.
Exit status 0 = export and verification clean, 1 = problems (printed), 2 = usage error.

An area module provides ``AREA``, ``PARTS``, ``DESCRIPTION`` and
``run(root, game, *, parts, quick, log) -> manifest``.
"""
from __future__ import annotations

import argparse
import importlib
import sys
import time
from pathlib import Path
from types import ModuleType
from typing import Optional, Sequence

from . import GOLDEN_ROOT
from .manifest import provenance_problems, verify_area

AREAS: dict[str, str] = {
    "slice": "oracle.export_slice",
}


def area_module(name: str) -> ModuleType:
    if name not in AREAS:
        raise KeyError(f"unknown area {name!r}; known: {', '.join(sorted(AREAS))}")
    return importlib.import_module(AREAS[name])


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m oracle.run", description=__doc__.split("\n")[0])
    ap.add_argument("area", nargs="?", help="area to export (see --list)")
    ap.add_argument("--list", action="store_true", help="list the areas and their parts")
    ap.add_argument("--parts", help="comma-separated subset of the area's parts (default: all)")
    ap.add_argument("--out", default=str(GOLDEN_ROOT), help="golden root (default: samples/golden)")
    ap.add_argument("--game", help="CUSA47999 folder (default: TSUKI_GAME_DIR or the tsuki default)")
    ap.add_argument("--verify", action="store_true", help="only verify an existing export")
    ap.add_argument("--quick", action="store_true", help="skip slow optional hashing (movie files)")
    ap.add_argument("--no-provenance", action="store_true",
                    help="verify files and checks only, not tool-source / input drift")
    ap.add_argument("--strict-provenance", action="store_true",
                    help="count changed tool sources of manifests without tool_used as problems")
    a = ap.parse_args(argv)
    reconf = getattr(sys.stdout, "reconfigure", None)
    if reconf is not None:
        reconf(encoding="utf-8")
    if a.list:
        for name in sorted(AREAS):
            m = area_module(name)
            print(f"{name}: {m.DESCRIPTION}")
            print(f"    parts: {', '.join(m.PARTS)}")
        return 0
    if not a.area:
        ap.print_usage(sys.stderr)
        return 2
    try:
        mod = area_module(a.area)
    except KeyError as e:
        print(e, file=sys.stderr)
        return 2
    root = Path(a.out)
    area_dir = root / a.area
    if not a.verify:
        from tsuki import game_dir
        game = Path(a.game) if a.game else game_dir()
        if game is None or not (game / "image").is_dir():
            print("game folder not found (pass --game or set TSUKI_GAME_DIR)", file=sys.stderr)
            return 2
        parts = [p for p in a.parts.split(",") if p] if a.parts else None
        t0 = time.perf_counter()
        man = mod.run(root, game, parts=parts, quick=a.quick, log=print)
        n = len(man["files"])
        size = sum(int(f["size"]) for f in man["files"])
        print(f"[{a.area}] wrote {n} files, {size:,} bytes in {time.perf_counter() - t0:.1f} s -> {area_dir}")
    t1 = time.perf_counter()
    problems = verify_area(area_dir)
    if not a.no_provenance and (area_dir / "manifest.json").is_file():
        notes: list[str] = []
        problems += provenance_problems(area_dir, game=Path(a.game) if a.game else None,
                                        hash_inputs=not a.quick, strict_legacy=a.strict_provenance,
                                        notes=notes)
        for line in notes:
            print(f"[{a.area}] provenance: {line}")
    for p in problems:
        print(f"PROBLEM: {p}")
    print(f"[{a.area}] verify: {len(problems)} problems ({time.perf_counter() - t1:.1f} s)")
    return 0 if not problems else 1


if __name__ == "__main__":
    raise SystemExit(main())
