"""Tests for tools/oracle: the golden-data manifest framework, the runner and the M2 slice
exporter (oracle.export_slice).

The synthetic tests need no game data.  The real-data tests export small subsets of the slice
into a temporary directory (never into samples/golden) that is deleted when the test ends
(``export_tmp``: game-derived files must not stay in %TEMP%), and skip when the game folder is
missing; the last test re-verifies the local samples/golden/slice export when it exists.
Assertions use numbers, offsets and hashes only, never game text.
"""
from __future__ import annotations

import dataclasses
import hashlib
import json
import shutil
from pathlib import Path
from typing import Any, Iterator

import numpy as np
import pytest

from oracle import GOLDEN_ROOT, SCHEMA
from oracle import export_slice as es
from oracle import manifest as om
from oracle import run as orun
from tsuki import game_dir
from tsuki.script import Label, parse_script

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None, reason="game folder not found (TSUKI_GAME_DIR)")


@pytest.fixture
def export_tmp(tmp_path: Path) -> Iterator[Path]:
    """A temporary export root for real-data tests, deleted (with everything the export wrote)
    when the test ends, pass or fail."""
    d = tmp_path / "export"
    d.mkdir()
    try:
        yield d
    finally:
        shutil.rmtree(d)
        assert not d.exists()


def _prod(fn: str, **args: Any) -> om.Producer:
    return om.Producer("tests.test_oracle", fn, args)


def _load(p: Path) -> Any:
    return json.loads(p.read_text(encoding="utf-8"))


def _demo_area(root: Path) -> om.AreaWriter:
    area = om.AreaWriter(root, "demo", _prod("demo"), description="synthetic")
    p = area.part("bin", "binary files")
    p.write_bytes("a.bin", b"\x00\x01\x02", "bytes", _prod("a"))
    p.write_chunks("pcm.f32", [np.arange(4, dtype="<f4").tobytes(), memoryview(np.ones(2, "<f4").tobytes())], "f32le",
                   _prod("pcm", frames=6), {"frames": 6, "channels": 1})
    p.write_json("h.json", {"sx": om.f32_json(np.float32(0.74))}, _prod("h"))
    p.count("files", 3)
    p.check("one", 1, 1, "test")
    p.finish()
    q = area.part("other", "")
    q.write_bytes("b.bin", b"x", "bytes", _prod("b"))
    q.finding("note", "something observed", [1, 2])
    q.finish()
    area.finish()
    return area


# ---------------------------------------------------------------------------
# synthetic: manifest framework
# ---------------------------------------------------------------------------

def test_manifest_records_and_verify(tmp_path: Path) -> None:
    _demo_area(tmp_path)
    d = tmp_path / "demo"
    man = om.load_manifest(d / om.MANIFEST_NAME)
    assert man["schema"] == SCHEMA and man["area"] == "demo"
    assert [f["path"] for f in man["files"]] == ["bin/a.bin", "bin/h.json", "bin/pcm.f32", "other/b.bin"]
    rec = next(f for f in man["files"] if f["path"] == "bin/a.bin")
    assert rec["sha1"] == hashlib.sha1(b"\x00\x01\x02").hexdigest() and rec["size"] == 3
    assert rec["produced_by"] == {"module": "tests.test_oracle", "function": "a", "args": {}, "uses": []}
    pcm = next(f for f in man["files"] if f["path"] == "bin/pcm.f32")
    assert pcm["size"] == 24 and pcm["format"] == "f32le" and pcm["meta"]["frames"] == 6
    assert set(man["parts"]) == {"bin", "other"} and man["parts"]["bin"]["files"] == 3
    assert man["counts"] == {"bin.files": 3}
    assert man["checks"][0]["name"] == "bin.one" and man["checks"][0]["ok"]
    assert man["findings"] == [{"name": "other.note", "detail": "something observed", "data": [1, 2]}]
    assert "f32le" in man["conventions"] and man["generator"]["tool_digest"]
    assert _load(d / "bin" / "h.json")["sx"] == {"value": float(np.float32(0.74)), "bits": 0x3F3D70A4}
    assert om.verify_area(d) == []


def test_verify_detects_changes(tmp_path: Path) -> None:
    _demo_area(tmp_path)
    d = tmp_path / "demo"
    (d / "bin" / "a.bin").write_bytes(b"\x00\x01\x03")                 # same size, other bytes
    (d / "bin" / "stray.bin").write_bytes(b"?")
    (d / "other" / "b.bin").unlink()
    probs = om.verify_area(d)
    assert any(p.startswith("bin/a.bin: SHA-1") for p in probs)
    assert any(p.startswith("bin/stray.bin: not listed") for p in probs)
    assert any(p.startswith("other/b.bin: missing") for p in probs)
    assert om.verify_area(tmp_path / "nothing") == [f"{tmp_path / 'nothing' / om.MANIFEST_NAME} does not exist"]


def test_failed_check_fails_verify(tmp_path: Path) -> None:
    area = om.AreaWriter(tmp_path, "c", _prod("c"))
    p = area.part("p")
    p.write_bytes("x.bin", b"1", "bytes", _prod("x"))
    assert not p.check("bad", [1, 2], (1, 3), "test")
    p.finish()
    area.finish()
    assert om.verify_area(tmp_path / "c") == ["check p.bad failed: expected [1, 2], got [1, 3]"]


def test_reexport_of_one_part_keeps_the_others(tmp_path: Path) -> None:
    _demo_area(tmp_path)
    area = om.AreaWriter(tmp_path, "demo", _prod("again"))
    p = area.part("bin")
    p.write_bytes("c.bin", b"new", "bytes", _prod("c"))
    p.finish()
    man = area.finish()
    assert [f["path"] for f in man["files"]] == ["bin/c.bin", "other/b.bin"]
    assert not (tmp_path / "demo" / "bin" / "a.bin").exists()           # stale part files removed
    assert om.verify_area(tmp_path / "demo") == []


def test_writer_rejects_bad_input(tmp_path: Path) -> None:
    area = om.AreaWriter(tmp_path, "r", _prod("r"))
    p = area.part("p")
    for bad in ("../x.bin", "sub/x.bin", om.MANIFEST_NAME, "x.tmp", ""):
        with pytest.raises(om.ManifestError):
            p.write_bytes(bad, b"", "bytes", _prod("bad"))
    with pytest.raises(om.ManifestError):
        p.write_bytes("x.bin", b"", "no-such-format", _prod("bad"))
    p.write_bytes("x.bin", b"", "bytes", _prod("x"))
    with pytest.raises(om.ManifestError):
        p.write_bytes("x.bin", b"", "bytes", _prod("x"))
    with pytest.raises(om.ManifestError):
        area.part("p")
    with pytest.raises(om.ManifestError):
        om.f32_bits(0.74)                                                # not a float32 value
    with pytest.raises(om.ManifestError):
        area.finish()                                                    # part p not finished


def test_runner_list_and_usage(capsys: pytest.CaptureFixture[str]) -> None:
    assert orun.main(["--list"]) == 0
    out = capsys.readouterr().out
    assert "slice:" in out and all(p in out for p in es.PARTS)
    assert orun.main(["no-such-area"]) == 2
    assert orun.main([]) == 2


# ---------------------------------------------------------------------------
# synthetic: statements and the tracer
# ---------------------------------------------------------------------------

_SRC = (b"_ZZ00101(START);_WKST(F823,eq,0);_IF__(F026,ne,0,LV0);_STCC(-1);_WKST(F026,eq,0);"
        b"_ZY00201(LV0);_WKAD(F823,1);_WKAD(C847,1);_WNTY(1);_ZM00301($000003);_PGST(1);_END_();"
        b"_WKST(F001,eq,9);")


def test_trace_script_follows_if_and_records_messages() -> None:
    cmds = parse_script(_SRC)
    labels = [Label("START", 1, cmds[0].offset), Label("LV0", 2, cmds[5].offset)]
    tr = es.trace_script(cmds, labels)
    # _IF__ jumps when its condition is false (vm_variables.md 4.3): F026 == 0 skips 3..4
    assert [s.index for s in tr.steps] == [0, 1, 2, 5, 6, 7, 8, 9, 10, 11]
    assert tr.end == "END_" and tr.steps[2].goto == "LV0"
    assert tr.steps[4].writes == [("F[823]", 1)] and tr.steps[5].writes == [("F[848]", 1)]
    zm = tr.steps[7]
    assert zm.name == "ZM" and zm.vars is not None
    assert (zm.vars["F823"], zm.vars["C846"]) == (1, 0)   # C846 = F[846 + F823] = F847
    st = es.trace_script(cmds, labels, stop="_ZM00301($000003);")
    assert st.end == "stop statement" and st.steps[-1].index == 9
    with pytest.raises(es.SliceFlowError):
        es.trace_script(cmds, labels, stop="_NOPE();")
    with pytest.raises(es.SliceFlowError):
        es.trace_script(parse_script(b"_GOTO(LV0);"), [])
    with pytest.raises(es.SliceFlowError):
        es.trace_script(parse_script(b"_IF__(F001,eq,1,NOWHERE);"), [])


def test_statement_json_both_readers() -> None:
    j = es.statement_json(parse_script(b"_STCH(0,img0597,258-18,154-19,1000,255,2,,CC,,);")[0])
    assert j["reader"] == "object" and j["handled"] and j["frame"] is not None
    assert j["inline"][2]["i16"] == 240 and j["object"]["args"][2]["expr"] == 240
    assert j["object"]["args"][1]["expr"] == 0 and j["object"]["args"][7]["has"] is False
    assert j["values"][:4] == [0, 0, 240, 135] and j["values"][7] is None
    w = es.statement_json(parse_script(b"_WTTM(242.25,1);")[0])
    assert w["reader"] == "inline"
    assert w["inline"][0]["i16"] == 24225 and w["object"]["args"][0]["expr"] == 242 and w["values"] == [24225, 1]
    e = es.statement_json(parse_script(b"_END_();")[0])
    assert e["inline"] == [{"i16": 0, "i32": 0, "present": False}, {"i16": -1, "i32": -1, "present": False}]
    t = es.statement_json(parse_script(b"_SEPL(7,1,X,,`001:8000,,`017:30);")[0])
    assert t["tags"] == [None, None, None, None, 1, None, 17] and t["inline"][4]["i16"] == 8000
    z = es.statement_json(parse_script(b"_ZM01d04($000003);")[0])
    assert z["marker"] == {"block": 0x1D, "sub": 4} and z["text_ids"] == [3]


# ---------------------------------------------------------------------------
# real data (subsets exported into export_tmp, deleted afterwards)
# ---------------------------------------------------------------------------

@needs_game
def test_slice_scripts_text_layouts(export_tmp: Path) -> None:
    assert GAME is not None
    es.export(export_tmp, GAME, parts=["scripts", "text", "layouts"])
    d = export_tmp / "slice"
    assert om.verify_area(d) == []
    s2, s3 = _load(d / "scripts" / "script_0002.json"), _load(d / "scripts" / "script_0003.json")
    assert (s2["statement_count"], s2["trace"]["end"], s2["trace"]["last_offset"]) == (129, "RTMN", 0x7D4)
    assert (s3["trace"]["end"], s3["trace"]["last_offset"]) == ("stop statement", 0x13A5)
    assert s2["trace"]["skipped"] == s3["trace"]["skipped"] == [[40, 68]]
    assert hashlib.sha1((d / "scripts" / "script_0003.bin").read_bytes()).hexdigest() == s3["decoded"]["sha1"]
    stch = next(s for s in s3["statements"] if s["offset"] == 0x686)          # _STCH(6,img1685,...)
    assert stch["key"] == "STCH" and stch["values"][:4] == [6, 0, 240, 136]
    assert [m["text_ids"][0] for m in s3["messages"]] == list(range(8))
    assert [m["blank_before"] for m in s3["messages"]] == [0, 1, 0, 0, 0, 0, 1, 0]
    assert {u["key"] for u in s3["assets"]["pictures"]} >= {"IMG1685", "IMG0700_06_06"}
    lines = _load(d / "text" / "lines.json")
    assert lines["languages"]["en"]["count"] == 43969 and len(lines["languages"]["ja"]["lines"]) == 8
    en = _load(d / "layouts" / "layout_en.json")
    assert [m["block"]["nlines"] for m in en["messages"]] == [2, 1, 1, 2, 1, 2, 1, 1]
    ys = [[o[1]["value"] for o in p["origins"]] for p in en["pages"]]
    assert ys == [[35.0, 113.0, 139.0], [35.0, 87.0, 113.0, 191.0, 217.0]]
    ja = _load(d / "layouts" / "layout_ja.json")
    assert any(r["flags"] & 2 for m in ja["messages"] for r in m["records"])      # ruby readings
    assert all(q["kind"] in ("outline", "fill") for m in ja["messages"] for q in m["quads"]["list"])


@needs_game
def test_slice_pictures_subset(export_tmp: Path) -> None:
    from tsuki import image as timage
    from tsuki import image_dir
    assert GAME is not None
    spec = dataclasses.replace(es.DEFAULT_SPEC, pictures=("IMG1685", "IMG2458"), extra_pictures=(("allpac3", 72),))
    es.export(export_tmp, GAME, parts=["pictures"], spec=spec)
    d = export_tmp / "slice" / "pictures"
    assert om.verify_area(export_tmp / "slice") == []
    h = _load(d / "allpac0_00270.json")
    assert h["catalog"]["index"] == 1692 and h["catalog"]["anchor"] == [351, 212]
    assert [(u["x"], u["y"]) for u in h["uses"]] == [(240, 136)]
    ref = timage.load(image_dir() / "allpac0", 270).rgba                  # type: ignore[operator]
    assert h["assembled"]["sha1"] == hashlib.sha1(ref.tobytes()).hexdigest()
    blob = (d / h["tiles_file"]).read_bytes()
    img = np.zeros((h["header"]["out_height"], h["header"]["out_width"], 4), np.uint8)
    for t in h["tiles"]:
        if t["data"] is None:
            continue
        px = np.frombuffer(blob, np.uint8, t["data"]["length"], t["data"]["offset"]).reshape(
            t["data"]["height"], t["data"]["width"], 4)
        (sx, sy), (dx, dy), (w, hh) = t["copy"]["src"], t["copy"]["dst"], t["copy"]["size"]
        img[dy:dy + hh, dx:dx + w] = px[sy:sy + hh, sx:sx + w]
    assert np.array_equal(img, ref)
    band = _load(d / "allpac0_00358.json")["catalog"]["logical"]             # four language bands
    assert band["ja"]["rect"][1] == 0 and band["en"]["rect"][1] > 0 and band["ja"]["sha1"] != band["en"]["sha1"]
    assert (d / "allpac3_00072.rgba").stat().st_size == 20 * 512 * 256 * 4


@needs_game
def test_slice_audio_voice(export_tmp: Path) -> None:
    from tsuki.audio import AudioBank
    assert GAME is not None
    spec = dataclasses.replace(es.DEFAULT_SPEC, voices=(15620,), se_names=(), bgm=())
    es.export(export_tmp, GAME, parts=["audio"], spec=spec)
    d = export_tmp / "slice" / "audio"
    h = _load(d / "voice_15620.json")
    assert (h["channels"], h["rate"], h["delay"], h["loop"]) == (1, 48000, 256, None)
    assert abs(h["frames"] / 48000 - 2.25) < 0.01
    x = np.fromfile(d / h["pcm_file"], dtype="<f4")
    assert x.size == h["frames"]
    with AudioBank("voice", GAME / "image") as b:
        ref = b.decode(15620, float_output=True).samples.reshape(-1)
    assert np.array_equal(x, ref)


@needs_game
def test_bgm33_header_loop() -> None:
    assert GAME is not None
    with es.SliceContext(GAME) as ctx:
        hdr, stored, stream = es.audio_header(ctx, "bgm", 33)
    assert (hdr["loop"]["start"], hdr["loop"]["end"], hdr["delay"], hdr["channels"]) == (962000, 8257999, 256, 2)
    assert hdr["smpl"][0]["start"] - hdr["loop"]["start"] == 256 and stream[:4] == b"RIFF"
    assert hdr["stream"]["size"] == len(stream) == len(stored)


@needs_game
def test_slice_eboot_part(export_tmp: Path) -> None:
    assert GAME is not None
    es.export(export_tmp, GAME, parts=["eboot"], spec=dataclasses.replace(es.DEFAULT_SPEC, hash_movies=False))
    d = export_tmp / "slice"
    man = om.load_manifest(d / om.MANIFEST_NAME)
    assert [c["name"] for c in man["checks"] if not c["ok"]] == []
    assert man["counts"]["eboot.triplet_entries"] == 49620 and man["counts"]["eboot.motion_keyframes"] == 71905
    cat = _load(d / "eboot" / "catalog.json")
    assert {r["key"]: r["name_index"] for r in cat["records"]}["IMG0700_06_06"] == 5330
    mot = _load(d / "eboot" / "motions.json")
    assert [m["id"] for m in mot["motions"]] == [22523, 22524, 22525, 22526]
    ft = _load(d / "eboot" / "font_tables.json")
    assert sum(t["count"] for t in ft["lookups"]) == 17423
    mv = _load(d / "eboot" / "movies.json")
    assert mv["remap"]["ids"]["4"] == [4, 46, 47, 106] and all(f["sha256"] is None for f in mv["files"])
    # the 39 _STCG names of 00_01 (followups.md: 23 of them had no record)
    seen = cat["seen_records"]
    assert len(seen) == 39 and all(r["record"] is not None and r["file_id"] is not None for r in seen)
    assert sorted(r["key"] for r in seen if not r["displayed"]) == list(es.EXPECTED_STCG_UNDISPLAYED)
    assert len(es.EXPECTED_STCG_UNDISPLAYED) == 23
    assert {r["key"]: r["name_index"] for r in seen}["IMG0700"] == 707
    assert all(r["picture_id"] == r["name_index"] == r["seen_index"] for r in seen)     # all < 0xA8D
    assert {r["key"] for r in cat["records"]} == {r["key"] for r in seen if r["displayed"]} | {
        "BLACK", "IMG0700_06_06"}
    assert om.verify_area(d) == []
    assert om.provenance_problems(d) == []


@needs_game
def test_slice_fonts_en(export_tmp: Path) -> None:
    from tsuki.font import FontPages
    assert GAME is not None
    es.export(export_tmp, GAME, parts=["fonts"], spec=dataclasses.replace(es.DEFAULT_SPEC, font_langs=("en",)))
    d = export_tmp / "slice" / "fonts"
    h = _load(d / "ft_en.json")
    assert len(h["pages"]) == 65 and h["pages"][0]["width"] == 256 and h["pages"][1]["width"] == 1024
    blob = (d / h["pages_file"]).read_bytes()
    assert len(blob) == sum(p["length"] for p in h["pages"])
    p = h["pages"][1]
    with FontPages(GAME / "image" / "ft_en.mrg") as fp:
        assert blob[p["offset"]:p["offset"] + p["length"]] == fp.page(1).alpha.tobytes()


@pytest.mark.skipif(not (GOLDEN_ROOT / "slice" / om.MANIFEST_NAME).exists(),
                    reason="golden slice not exported (python -m oracle.run slice)")
def test_local_golden_slice_is_consistent() -> None:
    assert om.verify_area(GOLDEN_ROOT / "slice") == []
