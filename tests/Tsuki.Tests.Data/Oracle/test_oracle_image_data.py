"""Tests of ``tools/oracle/image.py`` and ``tools/oracle/data.py`` (package A1).

    $env:PYTHONPATH = "D:\\TsukihimePort\\tools"
    D:\\TsukihimePort\\.venv\\Scripts\\python.exe -m pytest tests\\Tsuki.Tests.Data\\Oracle -q -p no:cacheprovider

The stratified selection is tested on a synthetic census; the census and the hashing on the real game folder, which skips only
when the folder is missing. Nothing is written outside pytest's ``tmp_path``, and the real-data tests write nothing derived from the game.
"""
from __future__ import annotations

import hashlib
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))

from oracle import data as odata  # noqa: E402
from oracle import image as oimage  # noqa: E402
from tsuki import game_dir  # noqa: E402
from tsuki import image as timage  # noqa: E402

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None, reason="game data missing: set TSUKI_GAME_DIR to the CUSA47999 folder")


def fake_census() -> list[oimage.PictureRef]:
    """A census with the shape of the real one: many small PAL8, fewer HEP8, a few of everything else."""
    pics: list[oimage.PictureRef] = []

    def add(source: str, fmt: str, n: int, w: int, h: int, tile: int, name_prefix: str, flag2: bool = False) -> None:
        for i in range(n):
            ww, hh = w + (i % 7) * 3, h + (i % 5) * 2
            pics.append(oimage.PictureRef(
                source, i, (), f"{name_prefix}{i:04d}.MZP", fmt, ww, hh, ww, hh, tile, tile, -(-ww // tile), -(-hh // tile),
                1 if fmt == "hep8" else 0, 0x11 if fmt == "hep8" else 0x01, 12 if fmt == "hep8" else 1,
                -(-ww // tile) * -(-hh // tile) - (i % 3 == 0), 1 if (flag2 and i % 11 == 0) else 0, len(pics)))

    add("allpac2", "pal8", 3000, 160, 90, 256, "TH")
    add("allpac1", "hep8", 2000, 1920, 1080, 512, "IMG", flag2=True)
    add("allpac0", "hep8", 300, 800, 600, 256, "BG")
    add("allpac0", "rgb565lo+a", 80, 640, 480, 512, "FX")
    add("allpac4", "pal4", 20, 16, 16, 16, "DUMMY")
    add("parts", "pal8", 12, 64, 64, 64, "PART")
    pics.append(oimage.PictureRef("allpac1", 99999, (), "IMG1481.MZP", "hep8", 11264, 7680, 11220, 7650, 512, 512, 22, 15, 1, 0x11, 12, 330, 0, len(pics)))
    return pics


def test_the_stratified_selection_has_the_requested_size_and_is_deterministic() -> None:
    pics = fake_census()
    a, sizes = oimage.select_stratified(pics, 500)
    b, _ = oimage.select_stratified(pics, 500)
    assert len(a) == 500 and len({p.order for p in a}) == 500
    assert [p.order for p in a] == [p.order for p in b]
    assert [p.order for p in a] == sorted(p.order for p in a)
    assert sum(sizes.values()) == len(pics)


def test_the_selection_keeps_the_anchors_and_every_stratum() -> None:
    pics = fake_census()
    chosen, sizes = oimage.select_stratified(pics, 500)
    got = {p.order for p in chosen}
    by_name = {p.name: p for p in pics}
    assert by_name["IMG1481.MZP"].order in got                          # named anchor and the largest area
    assert all(p.order in got for p in pics if p.fmt == "pal4")         # a rare stratum is taken whole
    for k in sizes:
        assert any(oimage.stratum(p) == k for p in chosen), k           # no stratum is empty
    largest = max((p for p in pics if p.fmt == "rgb565lo+a"), key=lambda p: (p.area, -p.order))
    assert largest.order in got                                         # the extremes of every format
    assert any(p.flag2 for p in chosen if p.fmt == "hep8")


def test_a_sample_larger_than_the_census_is_refused() -> None:
    with pytest.raises(Exception):
        oimage.select_stratified(fake_census()[:50], 500)


def test_the_picture_tsv_has_the_documented_columns() -> None:
    pics = fake_census()[:3]
    text = oimage.pictures_tsv(pics, ["a" * 40] * 3, ["s"] * 3).decode("utf-8")
    lines = text.split("\n")
    assert lines[0].split("\t") == list(oimage.COLUMNS)
    assert lines[1].split("\t")[0] == "allpac2#0"
    assert all(len(line.split("\t")) == len(oimage.COLUMNS) for line in lines[:-1])
    assert text.endswith("\n") and "\r" not in text
    assert oimage.pictures_tsv(pics, ["b" * 40] * 3, None).decode().split("\n")[1].endswith("\t-")


@needs_game
def test_the_census_has_the_counts_of_images_md() -> None:
    pics = oimage.census(GAME)
    assert len(pics) == oimage.EXPECTED_PICTURES
    counts: dict[str, int] = {}
    for p in pics:
        counts[p.fmt] = counts.get(p.fmt, 0) + 1
    assert counts == oimage.EXPECTED_FORMATS
    assert [p.order for p in pics] == list(range(len(pics)))
    nested = [p for p in pics if p.source == "allpac3" and p.path]
    assert len(nested) == 3699 and sum(1 for p in pics if p.source == "allpac3") == 3764
    assert pics[0].label == "allpac0#0" and pics[0].name == "BLACK.MZP"
    assert len({p.label for p in pics}) == len(pics)


@needs_game
def test_hashing_in_worker_processes_equals_decoding_in_this_one() -> None:
    pics = oimage.census(GAME)
    small = [p for p in pics if p.area < 200_000][::997][:12]
    assert len(small) >= 8 and {p.fmt for p in small} >= {"pal8"}
    digests = oimage._hash_all(GAME, small, procs=2, log=None)
    for p, d in zip(small, digests):
        arc = oimage._container(str(GAME), p.source, p.entry, p.path)
        assert d == hashlib.sha1(timage.decode_image(arc).rgba).hexdigest(), p.label
        assert d == oimage.decode_sha1(str(GAME), p.source, p.entry, p.path)


@needs_game
def test_the_golden_sample_on_disk_is_the_selection_of_the_census() -> None:
    # the sha1 part, if it was exported, lists exactly select_stratified(census) with the digests of the oracle
    tsv = ROOT / "samples" / "golden" / "image" / "sha1" / "pictures.tsv"
    if not tsv.exists():
        pytest.fail(f"{tsv} is missing; run: python -m oracle.image --parts sha1")
    rows = [line.split("\t") for line in tsv.read_text(encoding="utf-8").split("\n")[1:] if line]
    chosen, _ = oimage.select_stratified(oimage.census(GAME))
    assert [r[0] for r in rows] == [p.label for p in chosen]
    for r in rows[::211]:
        src, entry, *path = r[0].replace("#", "/").split("/")
        digest = oimage.decode_sha1(str(GAME), src, int(entry), tuple(int(x) for x in path))
        assert digest == r[13], r[0]


def test_every_golden_data_file_belongs_to_a_part() -> None:
    # the files of the golden layout that the area manifest has to list
    expected = {
        "archives.tsv": "entries", "entries/allpac0.tsv": "entries", "standalone_files.tsv": "standalone",
        "containers.tsv": "containers", "buildcheck.json": "build", "mzx_samples.tsv": "mzx", "bench/streams.bin": "bench",
        "bench/python.json": "bench", "nam_names.tsv": "standalone", "scripttext.tsv": "standalone",
    }
    for rel, part in expected.items():
        assert odata._part_of(rel) == part, rel
    assert odata._part_of("surprise.bin") is None


def test_the_hash_comparison_reports_differences(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    a = tmp_path / "a.tsv"
    b = tmp_path / "b.tsv"
    a.write_text("x#1\taa\nx#2\tbb\nx#3\tcc\n", encoding="utf-8")
    b.write_text("x#1\taa\nx#2\tXX\nx#4\tdd\n", encoding="utf-8")
    assert odata.compare_hashes(str(a), str(b)) == 1
    out = capsys.readouterr().out
    assert "same label & hash: 1" in out and "different hash: 1" in out
    b.write_text("x#1\taa\nx#2\tbb\nx#3\tcc\n", encoding="utf-8")
    assert odata.compare_hashes(str(a), str(b)) == 0


def test_the_thin_callers_forward_to_the_oracle_module() -> None:
    here = Path(__file__).resolve().parent
    for script in ("gen_golden_data.py", "all_stream_hashes.py"):
        text = (here / script).read_text(encoding="utf-8")
        assert "from oracle.data import" in text and len(text.splitlines()) < 40, script
        done = subprocess.run([sys.executable, "-I", str(here / script), "--help"], capture_output=True, text=True, check=False)
        assert done.returncode == 0 and "usage:" in done.stdout, (script, done.stderr)
