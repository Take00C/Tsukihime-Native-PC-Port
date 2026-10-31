#!/usr/bin/env python
"""Writes the data-layer expectations of Tsuki.Data from the Python oracle. Thin caller of ``tools/oracle/data.py``.

    D:/TsukihimePort/.venv/Scripts/python.exe -I tests/Tsuki.Tests.Data/Oracle/gen_golden_data.py
        [--game DIR] [--out DIR] [--parts entries,standalone,containers,mzx,bench,build]

The generators moved to ``oracle.data`` (lead decision 2, docs/status/decisions.md); this file only puts ``tools/`` on the path and
forwards the command line, so the command the tests print when a golden file is missing keeps working. Output goes to
``samples/golden_data/`` (local only, git-ignored), which is where ``GoldenData.Dir`` reads. The same files in the oracle layout, with
a manifest, come from ``python -m oracle.run data`` (see ``tools/oracle/data.py``).
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))

from oracle.data import golden_main  # noqa: E402

if __name__ == "__main__":
    raise SystemExit(golden_main())
