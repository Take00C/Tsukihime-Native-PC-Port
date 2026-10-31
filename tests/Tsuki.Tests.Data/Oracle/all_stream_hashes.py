#!/usr/bin/env python
"""SHA-256 of the decoded output of EVERY MZX0 stream (the nightly of ROADMAP M1 acceptance 3). Thin caller of ``tools/oracle/data.py``.

    D:/TsukihimePort/.venv/Scripts/python.exe -I tests/Tsuki.Tests.Data/Oracle/all_stream_hashes.py
        --out scratch/all_hashes_py.tsv [--procs 12] [--only allpac2,parts]
    D:/TsukihimePort/.venv/Scripts/python.exe -I tests/Tsuki.Tests.Data/Oracle/all_stream_hashes.py
        --compare scratch/all_hashes_py.tsv scratch/decode_all_hashes_cs.tsv

One line per stream in the format of ``Tsuki.Cli decode-all --hashes``. The code moved to ``oracle.data`` (lead decision 2).
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))

from oracle.data import hashes_main  # noqa: E402

if __name__ == "__main__":
    raise SystemExit(hashes_main())
