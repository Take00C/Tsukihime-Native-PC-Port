"""oracle - golden-data exporters built on the Python reference readers (tools/tsuki, tools/eboot).

Every exporter writes one *area* under ``samples/golden/<area>/`` (local only: the files are
derived from the user's own copy of the game and are never committed or shared; see
docs/ARCHITECTURE.md 4.4 and 10.1).  An area holds plain binary files (little-endian, raw,
row-major) and JSON headers, plus a ``manifest.json`` that lists every file with its size,
SHA-1, format and the module + function + arguments that produced it, the input files and
archive entries it was read from (with their hashes), measured counts and the cross-checks
the exporter ran.  The manifest is the only thing a consumer has to read first: the C#
``GoldenSource`` (ARCHITECTURE 3.3) and the data tests need no Python.

Modules
-------
``oracle.manifest``      the area / part writer, the manifest schema and ``verify_area``
``oracle.export_slice``  area ``slice``: the M2 vertical-slice data of RUNTIME_REQUIREMENTS 7.3
``oracle.run``           CLI runner: ``python -m oracle.run <area> [--parts ...] [--verify]``

Run with ``PYTHONPATH=D:/TsukihimePort/tools``.
"""
from __future__ import annotations

from pathlib import Path

__all__ = ["GOLDEN_ROOT", "SCHEMA", "TOOLS_ROOT"]

TOOLS_ROOT = Path(__file__).resolve().parents[1]
GOLDEN_ROOT = TOOLS_ROOT.parent / "samples" / "golden"
SCHEMA = "tsuki-golden-manifest/1"
