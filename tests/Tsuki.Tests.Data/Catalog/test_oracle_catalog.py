"""Tests of ``tools/oracle/catalog.py`` (package A2).

    $env:PYTHONPATH = "D:\\TsukihimePort\\tools"
    D:\\TsukihimePort\\.venv\\Scripts\\python.exe -m pytest tests\\Tsuki.Tests.Data\\Catalog -q -p no:cacheprovider

The writers and the query generators are tested on synthetic data; the real-data tests skip only when the game folder is missing and
write nothing derived from the game (everything stays in memory or in a pytest ``tmp_path`` that holds no game text).
"""
from __future__ import annotations

import random
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))

from oracle import catalog as ocat  # noqa: E402
from oracle.manifest import ManifestError, verify_area  # noqa: E402
from tsuki import catalog as tcat  # noqa: E402
from tsuki import game_dir  # noqa: E402
from tsuki.nam import parse_nam32  # noqa: E402

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None, reason="game data missing: set TSUKI_GAME_DIR to the CUSA47999 folder")


def test_tsv_writes_a_header_none_as_dash_and_rejects_control_characters() -> None:
    data = ocat.tsv(("a", "b", "c"), [(1, None, "x"), ("", 2, "y")]).decode("utf-8")
    assert data == "a\tb\tc\n1\t-\tx\n\t2\ty\n"
    with pytest.raises(ManifestError):
        ocat.tsv(("a",), [("x\ty",)])
    with pytest.raises(ManifestError):
        ocat.tsv(("a",), [("x\n",)])
    with pytest.raises(ManifestError):
        ocat.tsv(("a", "b"), [(1,)])


def test_hexs_is_a_dash_for_empty_bytes_and_lower_case_hex_otherwise() -> None:
    assert ocat.hexs(b"") == "-"
    assert ocat.hexs(b"\x00\xaB") == "00ab"


def _names() -> dict[str, list[str]]:
    base = [f"IMG{i:04d}.MZP" for i in range(60)] + [f"AKI_A_00_{i:02d}_N01_B.MZP" for i in range(40)]
    return {a: list(base) for a in ocat.NAM_ARCHIVES}


def test_nam_queries_are_deterministic_unique_and_cover_the_boundary_cases() -> None:
    names = _names()
    a = ocat.nam_query_bytes(names, random.Random(ocat.SEED))
    b = ocat.nam_query_bytes(names, random.Random(ocat.SEED))
    assert a == b
    assert len(a) == len(set(a))
    assert b"IMG0001.MZP" in a and b"img0001.mzp" in a and b"dir\\IMG0001.MZP" in a
    assert b"" in a and b"\\" in a and b"A" * 29 in a
    assert any(len(q) > 256 for q in a)                      # backslash scan limit
    assert any(q.startswith(b"\x82") for q in a)             # Shift-JIS lead byte
    assert any(len(q) == 28 for q in a) and any(len(q) == 29 for q in a)


def test_script_queries_are_deterministic_and_include_variants_and_named_cases() -> None:
    class Rec:  # the two fields script_queries reads
        def __init__(self, index: int) -> None:
            self.index = index

    names = [f"IMG{i:04d}" for i in range(30)] + ["IMG0001_03_04", "IMG0002_10_00", "AKI_A_00_00_N01"]
    table = [Rec(i) for i in range(len(names))]
    a = ocat.script_queries(names, table, random.Random(1))
    b = ocat.script_queries(names, table, random.Random(1))
    assert a == b and len(a) == len(set(a))
    assert (b"img0001", 3, 4) in a and (b"img0002", 10, 0) in a
    assert (b"img2511", 0, 0) in a and (b"img1696", 11, 11) in a
    assert (b"img0001", -1, 5) in a and (b"img0001", 123, 456) in a


def test_the_area_registers_three_parts_and_a_runner_name() -> None:
    assert ocat.AREA == "catalog" and ocat.PARTS == ("eboot", "catalog", "lookup")
    from oracle import run as runner
    runner.AREAS.setdefault(ocat.AREA, ocat.MODULE)
    assert runner.area_module("catalog") is ocat


def test_classify_names_rows_have_the_columns_of_the_file() -> None:
    rows = ocat.classify_names(["AKI_A_00_00_N01.MZP", "IMG0001_02_03.MZP", "whatever"])
    assert [r[1] for r in rows] == ["sprite", "scene_layer", "unknown"]
    assert all(len(r) == 10 for r in rows)
    assert rows[0][4:8] == ["AKI", 8, "Akiha Tohno", "A"]
    assert rows[1][8] == "02_03" and rows[2][2] is None


def test_the_python_nam_search_and_the_dictionary_agree_on_a_synthetic_set() -> None:
    def nam(names: list[str]) -> bytes:
        return b"".join(n.encode().ljust(30, b"\0") + b"\r\n" for n in names) + b"\0" * 32

    nams = {"allpac0": parse_nam32(nam(["A.MZP", "B.MZP"])), "allpac1": parse_nam32(nam(["B.MZP"])),
            "allpac1_2": parse_nam32(nam([])), "allpac3": parse_nam32(nam([])), "allpac4": parse_nam32(nam(["C.MZP"]))}
    idx = tcat.ImageNameIndex(nams)
    for q in (b"a.mzp", b"B.MZP", b"c.mzp", b"d.mzp", b"dir\\b.mzp", b""):
        for avail in (True, False):
            assert tcat.find_image(q, nams, avail) == idx.find(q, avail)


@needs_game
def test_the_real_golden_area_verifies_when_it_exists() -> None:
    area = ROOT / "samples" / "golden" / "catalog"
    if not (area / "manifest.json").is_file():
        pytest.fail("samples/golden/catalog is missing: run `python -m oracle.catalog` first")
    assert verify_area(area) == []


@needs_game
def test_the_exporter_reproduces_itself() -> None:
    """A second export is byte-identical to the first (the manifests differ in their timestamps only). The copy is made in a
    temporary folder that is removed afterwards, so no game-derived output stays in %TEMP%."""
    import tempfile
    from oracle.manifest import load_manifest
    first = load_manifest(ROOT / "samples" / "golden" / "catalog" / "manifest.json")
    with tempfile.TemporaryDirectory(prefix="tsuki-a2-") as tmp:
        r = subprocess.run([sys.executable, "-m", "oracle.catalog", "--out", tmp], capture_output=True, text=True,
                           cwd=str(ROOT), env={"PYTHONPATH": str(ROOT / "tools"), **_env()}, check=False)
        assert r.returncode == 0, r.stdout + r.stderr
        second = load_manifest(Path(tmp) / "catalog" / "manifest.json")
    assert {f["path"]: f["sha1"] for f in first["files"]} == {f["path"]: f["sha1"] for f in second["files"]}
    assert all(c["ok"] for c in second["checks"])


def _env() -> dict[str, str]:
    import os
    keep = ("SYSTEMROOT", "PATH", "TEMP", "TMP", "TSUKI_GAME_DIR", "USERPROFILE", "LOCALAPPDATA", "APPDATA")
    return {k: v for k, v in os.environ.items() if k in keep}
