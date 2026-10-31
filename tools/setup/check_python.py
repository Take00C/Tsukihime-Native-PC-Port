"""Report the Python side of the M0 environment as JSON (used by tools/setup/check_env.ps1).

Prints one JSON object: the interpreter, the version and import result of each package the
oracle needs (numpy, pillow, pytest, numba, capstone, unicorn), whether the project packages
``tsuki`` and ``eboot`` import from ``tools/`` and which game folder ``tsuki.game_dir()``
resolves.  It reads no game data and writes nothing.

    PYTHONPATH=D:/TsukihimePort/tools .venv/Scripts/python.exe tools/setup/check_python.py
"""
from __future__ import annotations

import importlib
import importlib.metadata as md
import json
import os
import sys
from typing import Optional, TypedDict

# (distribution name, import name)
PACKAGES: tuple[tuple[str, str], ...] = (
    ("numpy", "numpy"),
    ("pillow", "PIL"),
    ("pytest", "pytest"),
    ("numba", "numba"),
    ("capstone", "capstone"),
    ("unicorn", "unicorn"),
)
PROJECT_MODULES: tuple[str, ...] = ("tsuki", "eboot")


class ImportResult(TypedDict):
    version: Optional[str]
    imported: bool
    error: Optional[str]


def probe_import(module: str, dist: Optional[str]) -> ImportResult:
    version: Optional[str] = None
    if dist is not None:
        try:
            version = md.version(dist)
        except md.PackageNotFoundError:
            version = None
    try:
        importlib.import_module(module)
    except (ImportError, OSError) as e:  # OSError: a native library (llvmlite, unicorn) failed to load
        return {"version": version, "imported": False, "error": f"{type(e).__name__}: {e}"[:300]}
    return {"version": version, "imported": True, "error": None}


def main() -> int:
    project = {mod: probe_import(mod, None) for mod in PROJECT_MODULES}
    report: dict[str, object] = {
        "python": sys.version.split()[0],
        "executable": sys.executable,
        "prefix_is_venv": sys.prefix != sys.base_prefix,
        "pythonpath": os.environ.get("PYTHONPATH", ""),
        "packages": {dist: probe_import(mod, dist) for dist, mod in PACKAGES},
        "project": project,
    }
    game: Optional[str] = None
    if project["tsuki"]["imported"]:
        from tsuki import game_dir

        g = game_dir()
        game = str(g) if g is not None else None
    report["tsuki_game_dir"] = game
    report["env_TSUKI_GAME_DIR"] = os.environ.get("TSUKI_GAME_DIR")
    report["env_TSUKI_FFMPEG"] = os.environ.get("TSUKI_FFMPEG")
    print(json.dumps(report))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
