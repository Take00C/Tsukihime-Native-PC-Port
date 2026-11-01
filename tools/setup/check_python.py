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
from pathlib import Path
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
MIN_PYTHON = (3, 14, 10)


def security_errors() -> list[str]:
    errors: list[str] = []
    if (sys.version_info[:2] != MIN_PYTHON[:2] or sys.version_info[:3] < MIN_PYTHON
            or sys.version_info[3] != "final"):
        errors.append("use Python 3.14.10 or a newer 3.14 security release")
    if sys.prefix == sys.base_prefix:
        errors.append("create an isolated .venv without --system-site-packages")
    else:
        config = Path(sys.prefix) / "pyvenv.cfg"
        if not config.is_file():
            errors.append("virtual environment has no pyvenv.cfg")
        else:
            settings = dict(line.lower().strip().split("=", 1) for line in
                            config.read_text(encoding="utf-8").splitlines() if "=" in line)
            settings = {k.strip(): v.strip() for k, v in settings.items()}
            if settings.get("include-system-site-packages") != "false":
                errors.append("recreate .venv without --system-site-packages")
    root = Path(__file__).resolve().parents[2]
    requirements = root / "requirements-dev.txt"
    required = {line.partition("==")[0] for line in
                (root / "requirements.txt").read_text(encoding="utf-8").splitlines() if "==" in line}
    for line in requirements.read_text(encoding="utf-8").splitlines():
        pin, _, marker = line.partition(";")
        if marker.strip() == "sys_platform == 'win32'" and sys.platform != "win32":
            continue
        if marker.strip() == "platform_python_implementation != 'PyPy'" and sys.implementation.name == "pypy":
            continue
        name, sep, expected = pin.strip().partition("==")
        if not sep:
            continue
        try:
            actual = md.version(name)
        except md.PackageNotFoundError:
            if name in required:
                errors.append(f"missing required package {name}")
            continue
        if actual != expected:
            errors.append(f"{name} {actual} differs from the reviewed version {expected}")
    return errors


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
    errors = security_errors()
    if "--security-only" in sys.argv or errors:
        print(json.dumps({"python": sys.version.split()[0], "security_errors": errors}))
        return 1 if errors else 0
    project = {mod: probe_import(mod, None) for mod in PROJECT_MODULES}
    report: dict[str, object] = {
        "python": sys.version.split()[0],
        "executable": sys.executable,
        "prefix_is_venv": sys.prefix != sys.base_prefix,
        "pythonpath": os.environ.get("PYTHONPATH", ""),
        "security_errors": errors,
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
