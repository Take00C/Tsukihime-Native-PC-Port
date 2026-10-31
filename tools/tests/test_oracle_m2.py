"""Tests for the M2 additions to tools/oracle (package H3a): the cross-script tracer
(``export_slice.trace_path``), manifest provenance (``manifest.tool_used``,
``manifest.provenance_problems``, the runner's drift report) and the slice parts ``retail``,
``models`` and ``s4``.

The synthetic tests need no game data.  The real-data tests export one part into a temporary
directory that is deleted when the test ends (``export_tmp``) and skip only when the game folder
is missing.  Assertions use numbers, offsets and hashes only, never game text.
"""
from __future__ import annotations

import dataclasses
import json
import shutil
from pathlib import Path
from typing import Any, Iterator, Sequence

import numpy as np
import pytest

from oracle import TOOLS_ROOT
from oracle import export_slice as es
from oracle import manifest as om
from oracle import run as orun
from tsuki import game_dir
from tsuki.script import Command, Label, parse_script

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None, reason="game folder not found (TSUKI_GAME_DIR)")


@pytest.fixture
def export_tmp(tmp_path: Path) -> Iterator[Path]:
    """A temporary export root, deleted with everything in it when the test ends."""
    d = tmp_path / "export"
    d.mkdir()
    try:
        yield d
    finally:
        shutil.rmtree(d)
        assert not d.exists()


def _load(p: Path) -> Any:
    return json.loads(p.read_text(encoding="utf-8"))


def _prod(fn: str) -> om.Producer:
    return om.Producer("tests.test_oracle_m2", fn, {})


# ---------------------------------------------------------------------------
# synthetic: trace_path follows _JUMP
# ---------------------------------------------------------------------------

_S0 = b"_ZZ00a01();_WKST(F001,eq,5);_JUMP(240,LB);_WKST(F002,eq,1);"
_S1 = b"_ZZ00101(START);_WKST(F003,eq,1);_ZZ00201(LB);_WKAD(F001,1);_IF__(F001,eq,6,NX);_WKST(F004,eq,1);" \
      b"_ZZ00301(NX);_END_();"


def _scripts() -> tuple[dict[int, tuple[list[Command], list[Label]]], es.ScriptLoader]:
    c0, c1 = parse_script(_S0), parse_script(_S1)
    lab1 = [Label("START", 1, c1[0].offset), Label("LB", 2, c1[2].offset), Label("NX", 3, c1[6].offset)]
    table = {0: (c0, []), 1: (c1, lab1)}
    return table, (lambda k: table[k])


def test_trace_path_follows_jump_across_scripts() -> None:
    table, load = _scripts()
    c1 = table[1][0]
    tr = es.trace_path(load, 0, (1, c1[7].offset), scene_ids=(110, 240))
    assert [(s.script, s.index) for s in tr.steps] == [(0, 0), (0, 1), (0, 2), (1, 2), (1, 3), (1, 4), (1, 5), (1, 6),
                                                       (1, 7)]
    jump = tr.steps[2]
    assert jump.name == "JUMP" and jump.jump == {"scene": 240, "script": 1, "label": "LB"}
    assert tr.steps[4].writes == [("F[1]", 6)]                   # variables carry over the jump
    assert tr.steps[5].goto is None             # _IF__ jumps when false; F001 == 6 is true (vm_variables 4.3)
    assert tr.end == "stop statement"
    # a jump without a label continues at statement 0 of the target
    t2 = es.trace_path(lambda k: (parse_script(b"_JUMP(7);"), []) if k == 0 else table[1], 0, (1, c1[1].offset),
                       scene_ids=(5, 7))
    assert [(s.script, s.index) for s in t2.steps] == [(0, 0), (1, 0), (1, 1)]
    assert t2.steps[0].jump is not None and t2.steps[0].jump["label"] is None
    with pytest.raises(es.SliceFlowError):                       # no script with that scene id
        es.trace_path(load, 0, (1, c1[7].offset), scene_ids=(110, 999))
    with pytest.raises(es.SliceFlowError):                       # label missing in the target
        es.trace_path(lambda k: (parse_script(b"_JUMP(240,NOPE);"), []) if k == 0 else table[1], 0, (1, 0),
                      scene_ids=(110, 240))
    with pytest.raises(es.SliceFlowError):                       # stop never reached
        es.trace_path(load, 0, (0, 99999), scene_ids=(110, 240))
    with pytest.raises(es.SliceFlowError):                       # the single-script tracer still refuses JUMP
        es.trace_script(*table[0])


def test_collect_assets_records_stcg_names() -> None:
    cmds = parse_script(b"_STCG(img0700);_STCG(`000:img2343);_STMA(3,1);")
    steps = es.trace_script(cmds, []).steps
    a = es.collect_assets(steps, lambda s: (1, cmds[s.index]))
    assert [(u.detail["name"], u.detail["key"]) for u in a.seen] == [("img0700", "IMG0700"), ("img2343", "IMG2343")]
    assert [u.detail["motion"] for u in a.motions] == [1] and a.pictures == []


# ---------------------------------------------------------------------------
# synthetic: provenance
# ---------------------------------------------------------------------------

def test_tool_used_lists_the_loaded_tool_modules() -> None:
    used = om.tool_used()
    assert {"oracle/manifest.py", "oracle/export_slice.py", "tsuki/script.py"} <= set(used)
    assert set(used) <= set(om.tool_sources())


def _fake_game(root: Path) -> Path:
    g = root / "game"
    (g / "image").mkdir(parents=True)
    (g / "image" / "x.mrg").write_bytes(bytes(range(256)) * 4)
    (g / "eboot.bin").write_bytes(b"\x4f\x15\x3d\x1d" + bytes(60))
    return g


def _area_with_inputs(root: Path, game: Path) -> Path:
    area = om.AreaWriter(root, "slice", _prod("area"), game_dir=game)
    p = area.part("p")
    p.write_bytes("a.bin", b"abc", "bytes", _prod("a"))
    p.add_input(game / "image" / "x.mrg")
    data = (game / "image" / "x.mrg").read_bytes()
    p.add_entry("x", 3, "E", 100, data[100:164])
    p.finish()
    area.finish()
    return root / "slice"


def test_part_manifests_record_the_tool_sources_they_used(tmp_path: Path) -> None:
    d = _area_with_inputs(tmp_path, _fake_game(tmp_path))
    pm = _load(d / "p" / om.MANIFEST_NAME)
    gen = pm["generator"]
    assert "oracle/manifest.py" in gen["tool_used"] and set(gen["tool_used"]) <= set(gen["tool_sources"])
    assert _load(d / om.MANIFEST_NAME)["generator"]["tool_used"]
    assert om.verify_area(d) == [] and om.provenance_problems(d) == []


def test_provenance_detects_tool_source_drift(tmp_path: Path) -> None:
    d = _area_with_inputs(tmp_path, _fake_game(tmp_path))
    mp = d / "p" / om.MANIFEST_NAME
    pm = _load(mp)
    pm["generator"]["tool_sources"]["oracle/manifest.py"] = "0" * 40      # as if the file changed since
    mp.write_bytes(om.dump_json(pm))
    probs = om.provenance_problems(d)
    assert len(probs) == 1 and probs[0].startswith("drift: tool source oracle/manifest.py changed (000000000000 ->")
    assert "part(s) p" in probs[0]
    assert om.verify_area(d) == []                       # files and checks are still consistent
    # a used source that no longer exists
    pm["generator"]["tool_used"].append("tsuki/no_such_module.py")
    pm["generator"]["tool_sources"]["tsuki/no_such_module.py"] = "1" * 40
    mp.write_bytes(om.dump_json(pm))
    assert any("tsuki/no_such_module.py was removed" in p for p in om.provenance_problems(d))
    # without tool_used (manifests written before it existed) a changed recorded source cannot be
    # attributed: it is a note, and a problem only with strict_legacy
    del pm["generator"]["tool_used"]
    pm["generator"]["tool_sources"] = {"oracle/manifest.py": om.sha1_file(TOOLS_ROOT / "oracle" / "manifest.py"),
                                       "oracle/run.py": "2" * 40}
    mp.write_bytes(om.dump_json(pm))
    notes: list[str] = []
    assert om.provenance_problems(d, notes=notes) == []
    assert "tool sources: 2 checked, 0 drifted, 1 unattributed" in notes
    assert any(n.startswith("unattributed: tool source oracle/run.py changed (222222222222 ->") for n in notes)
    probs = om.provenance_problems(d, strict_legacy=True)
    assert len(probs) == 1 and "oracle/run.py changed" in probs[0]
    assert om.provenance_problems(d, tools=False, strict_legacy=True) == []


def test_provenance_detects_input_drift(tmp_path: Path) -> None:
    g = _fake_game(tmp_path)
    d = _area_with_inputs(tmp_path, g)
    notes: list[str] = []
    assert om.provenance_problems(d, game=g, notes=notes) == []
    assert "inputs: 1 checked by size, 1 by SHA-256, 1 archive entries re-read" in notes
    x = g / "image" / "x.mrg"
    b = bytearray(x.read_bytes())
    b[120] ^= 0xFF                                       # inside the recorded entry, same size
    x.write_bytes(bytes(b))
    probs = om.provenance_problems(d, game=g)
    assert any(p.startswith("drift: input image/x.mrg has SHA-256") for p in probs)
    assert any(p.startswith("drift: entry x#3 at offset 100") for p in probs)
    x.write_bytes(bytes(b[:500]))
    assert any(p.startswith("drift: input image/x.mrg has size 500, recorded 1024") for p in om.provenance_problems(d, game=g))
    x.unlink()
    assert any("image/x.mrg is missing" in p for p in om.provenance_problems(d, game=g))
    # no game folder: game inputs are not checked, and a note says so
    notes = []
    assert om.provenance_problems(d, game=tmp_path / "nowhere", notes=notes) == []
    assert any("not checked: game folder not found" in n for n in notes)


def test_entry_archive_names_with_and_without_suffix(tmp_path: Path) -> None:
    # the image area records archive names with their suffix (allscr.mrg, ft_en.mrg); the slice
    # and audio areas record stems (allpac0, vag): both must be re-read from the same file
    assert om.entry_archive("allpac0") == "image/allpac0.mrg"
    assert om.entry_archive("ft_ko.mrg") == "image/ft_ko.mrg"
    g = _fake_game(tmp_path)
    data = (g / "image" / "x.mrg").read_bytes()
    area = om.AreaWriter(tmp_path, "slice", _prod("area"), game_dir=g)
    p = area.part("p")
    p.write_bytes("a.bin", b"abc", "bytes", _prod("a"))
    p.add_input(g / "image" / "x.mrg")
    p.add_entry("x", 3, "E", 100, data[100:164])
    p.add_entry("x.mrg", 3, "E", 100, data[100:164])
    p.finish()
    area.finish()
    d = tmp_path / "slice"
    notes: list[str] = []
    assert om.provenance_problems(d, game=g, notes=notes) == []
    assert any("2 archive entries re-read" in n for n in notes)
    b = bytearray(data)
    b[130] ^= 0xFF
    (g / "image" / "x.mrg").write_bytes(bytes(b))
    probs = om.provenance_problems(d, game=g)
    assert sum(q.startswith("drift: entry x#3 at offset 100") for q in probs) == 1
    assert sum(q.startswith("drift: entry x.mrg#3 at offset 100") for q in probs) == 1
    assert not any(".mrg.mrg" in q for q in probs)


def test_track_reads_records_the_files_a_part_opens(tmp_path: Path) -> None:
    g = _fake_game(tmp_path)
    (g / "image" / "big.mrg").write_bytes(bytes(1000))
    area = om.AreaWriter(tmp_path, "slice", _prod("area"), game_dir=g)
    p = area.part("p")
    other = tmp_path / "elsewhere.bin"                 # neither game nor repository: not an input
    other.write_bytes(b"z")
    with p.track_reads():
        p.add_input(g / "image" / "x.mrg", full_hash=False)      # recorded by the exporter: kept as it is
        (g / "image" / "x.mrg").read_bytes()
        with open(g / "image" / "big.mrg", "rb") as f:
            f.read(4)
        mm = np.memmap(g / "eboot.bin", dtype="u1", mode="r")
        assert mm[:4].tobytes() == b"\x4f\x15\x3d\x1d"
        del mm
        (TOOLS_ROOT / "oracle" / "__init__.py").read_bytes()     # Python code: not an input
        (TOOLS_ROOT.parent / "docs" / "FORMATS.md").read_bytes()  # a project file
        other.read_bytes()
        (g / "image" / "new.bin").write_bytes(b"w")               # written, not read
        p.write_bytes("a.bin", b"abc", "bytes", _prod("a"))
    (g / "image" / "late.bin").write_bytes(b"late")
    (g / "image" / "late.bin").read_bytes()                       # after the block: not tracked
    man = p.finish()
    area.finish()
    ins = {i["path"]: i for i in man["inputs"]}
    assert sorted(ins) == ["eboot.bin", "image/big.mrg", "image/x.mrg", "project:docs/FORMATS.md"]
    assert ins["image/x.mrg"]["hash"] == "size-only" and ins["image/big.mrg"]["hash"] == "sha256"
    assert man["generator"]["reads_tracked"] is True
    assert om.verify_area(tmp_path / "slice") == []


def test_slice_spec_json_round_trip() -> None:
    assert es.SliceSpec.from_json(es.DEFAULT_SPEC.to_json()) == es.DEFAULT_SPEC
    s = dataclasses.replace(es.DEFAULT_SPEC, scripts=(es.ScriptRange(3, "_PGST(2);"),), pictures=("IMG1685",),
                            extra_pictures=(("allpac3", 72),), voices=(15620,), retail_stop=None, s4_list="x.json")
    assert es.SliceSpec.from_json(json.loads(json.dumps(s.to_json()))) == s


def test_resolve_input_forms(tmp_path: Path) -> None:
    assert om.resolve_input("project:samples/x.json", None) == TOOLS_ROOT.parent / "samples" / "x.json"
    assert om.resolve_input("image/allscr.mrg", tmp_path) == tmp_path / "image" / "allscr.mrg"
    assert om.resolve_input("image/allscr.mrg", None) is None
    assert om.resolve_input("C:/abs/f.bin", None) == Path("C:/abs/f.bin")


def test_runner_verify_reports_drift(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    g = _fake_game(tmp_path)
    d = _area_with_inputs(tmp_path, g)
    assert orun.main(["slice", "--verify", "--out", str(tmp_path), "--game", str(g)]) == 0
    mp = d / "p" / om.MANIFEST_NAME
    pm = _load(mp)
    pm["generator"]["tool_sources"]["oracle/manifest.py"] = "0" * 40
    mp.write_bytes(om.dump_json(pm))
    capsys.readouterr()
    assert orun.main(["slice", "--verify", "--out", str(tmp_path), "--game", str(g)]) == 1
    out = capsys.readouterr().out
    assert "PROBLEM: drift: tool source oracle/manifest.py" in out and "verify: 1 problems" in out
    assert orun.main(["slice", "--verify", "--out", str(tmp_path), "--no-provenance"]) == 0
    # a manifest without tool_used: a note by default, a problem with --strict-provenance
    del pm["generator"]["tool_used"]
    mp.write_bytes(om.dump_json(pm))
    capsys.readouterr()
    assert orun.main(["slice", "--verify", "--out", str(tmp_path), "--game", str(g)]) == 0
    out = capsys.readouterr().out
    assert "provenance: unattributed: tool source oracle/manifest.py changed" in out and "verify: 0 problems" in out
    assert orun.main(["slice", "--verify", "--out", str(tmp_path), "--game", str(g), "--strict-provenance"]) == 1


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------

@needs_game
def test_slice_retail_part(export_tmp: Path) -> None:
    assert GAME is not None
    es.export(export_tmp, GAME, parts=["retail"])
    d = export_tmp / "slice"
    assert om.verify_area(d) == [] and om.provenance_problems(d) == []
    man = om.load_manifest(d / om.MANIFEST_NAME)
    assert [c["name"] for c in man["checks"] if not c["ok"]] == []
    p = _load(d / "retail" / "path.json")
    assert (p["executed"], p["end"], p["scripts"]) == (577, "stop statement", [0, 1])
    assert p["jumps"] == [{"script": 0, "statement": 68, "offset": 0x484, "from_scene": 110, "scene": 240,
                           "to_script": 1, "label": "START", "edge_record": None}]
    first1 = next(s for s in p["steps"] if s["script"] == 1)
    assert (first1["index"], first1["offset"]) == (1, 0xB)                # continues at the START marker
    last = p["steps"][-1]
    assert (last["script"], last["offset"], last["name"]) == (1, 0x2341, "VPLY")
    assert p["skipped"] == {"0": [], "1": [[18, 46]]}
    assert [m["text_ids"][0] for m in p["messages"]][:3] == [107, 108, 109]
    assert [u["index"] for u in p["assets"]["voices"]] == [16309]
    keys = [u["key"] for u in p["assets"]["pictures"]]
    assert keys[0] == "WHITE" and "IMG1696_11_11" in keys
    s1 = _load(d / "retail" / "script_0001.json")
    assert s1["statement_count"] == 8436 and s1["statements_exported"] == [0, 537] and len(s1["statements"]) == 538
    mot = _load(d / "retail" / "motions.json")
    assert [m["id"] for m in mot["motions"]] == [1, 2, 3, 4, 5]
    v = _load(d / "retail" / "voice_16309.json")
    assert (v["channels"], v["rate"]) == (1, 48000)
    assert np.fromfile(d / "retail" / v["pcm_file"], dtype="<f4").size == v["frames"]
    var = next(c for c in p["catalog"] if c["key"] == "IMG1696_11_11")
    assert var["file_id"] is not None and (d / "retail" / f"{var['file_id']['archive']}_{var['file_id']['index']:05d}.json").is_file()


@needs_game
def test_slice_models_part(export_tmp: Path) -> None:
    assert GAME is not None
    # each part in its own process (as oracle.run exports): text, exported after models, does not
    # list the modules models loaded, and models lists every game file it read
    es.export(export_tmp, GAME, parts=["models", "text"], isolate=True)
    d = export_tmp / "slice"
    assert om.verify_area(d) == [] and om.provenance_problems(d) == []
    pm = _load(d / "models" / om.MANIFEST_NAME)
    tm = _load(d / "text" / om.MANIFEST_NAME)
    assert "tsuki/wait_model.py" in pm["generator"]["tool_used"]
    assert "tsuki/wait_model.py" not in tm["generator"]["tool_used"]
    assert "oracle/run.py" not in pm["generator"]["tool_used"]
    assert pm["generator"]["reads_tracked"] is True
    assert {"eboot.bin", "image/allscr.mrg", "image/script_text.mrg", "image/ft_ja.mrg", "image/ft_en.mrg",
            "image/se_nam.mrg"} <= {i["path"] for i in pm["inputs"]}
    assert {("allscr", 5), ("allscr", 6)} <= {(e["archive"], e["index"]) for e in pm["input_entries"]}
    area = _load(d / om.MANIFEST_NAME)
    used = set(pm["generator"]["tool_used"]) | set(tm["generator"]["tool_used"])
    assert area["generator"]["tool_used"] == sorted(used)
    snd = _load(d / "models" / "sound_sepl.json")
    assert snd["statement"] == es.SEPL_SLICE and (snd["channel"], snd["stream"]) == (7, 13)
    assert snd["after_statement"]["record"]["vol_rate"] == {"value": 0.0625, "bits": 0x3D800000}
    vols = [f["vol"]["value"] for f in snd["frames"]]
    assert vols[0] == 0.0625 and vols[478] < 30.0 and vols[479] == 30.0 and vols[-1] == 30.0
    assert snd["target_reached_frame"] == 480
    w = {x["raw"]["text"]: x for x in _load(d / "models" / "waits.json")["waits"]}
    assert w["_WTTM(4000,1);"]["no_input"]["script_resumes_at_frame"] == 240
    assert w["_WTTM(1000,0);"]["decide_every_frame"]["script_resumes_at_frame"] == 1      # a click cancels
    rv = _load(d / "models" / "reveal.json")
    f = np.float32                                       # engine order: 1 / 60 * 1000 / 35 in float32
    assert rv["speeds"]["unread_vsync1"]["speed"]["bits"] == int(
        np.array([f(f(f(f(1) / f(60)) * f(1000)) / f(35))]).view("<u4")[0]) == 0x3EF3CF3E
    en = rv["messages"]["en"]
    assert [m["text_ids"][0] for m in en] == list(range(8))
    assert all(m["unread"]["shown_at_frame"] > m["read"]["shown_at_frame"] for m in en)
    ms = {m["motion"]: m for m in _load(d / "models" / "motion_samples.json")["motions"]}
    assert set(ms) >= {22523, 22524, 22525, 22526, 1, 2, 3, 4, 5}
    zoom = ms[22526]["samples"]
    assert zoom[1050]["t_ms"] == 17500.0 and round(zoom[1050]["values"]["9"]) == 1074
    assert len(zoom) == 70000 * 3 // 50 + 2


@needs_game
def test_slice_s4_part_on_a_subset(export_tmp: Path) -> None:
    assert GAME is not None
    if not es.S4_ORACLE.is_file():
        pytest.fail(f"{es.S4_ORACLE} is missing: run src/Spikes/S4.BigPicture/oracle/s4_oracle.py")
    full = _load(es.S4_ORACLE)
    pick: Sequence[dict[str, Any]] = [p for p in full["pictures"] if p["format"] in ("pal8", "pal4")][:3]
    pick = list(pick) + [next(p for p in full["pictures"] if p["format"] == "rgb565lo+a" and p["shown"][0] <= 2048)]
    lst = export_tmp / "s4_subset.json"
    lst.write_text(json.dumps({"pictures": pick}), encoding="utf-8")
    es.export(export_tmp, GAME, parts=["s4"], spec=dataclasses.replace(es.DEFAULT_SPEC, s4_list=str(lst)))
    d = export_tmp / "slice"
    assert om.verify_area(d) == [] and om.provenance_problems(d) == []
    rows = _load(d / "s4" / "pictures.json")["pictures"]
    assert [(r["archive"], r["index"]) for r in rows] == [(p["arc"], p["index"]) for p in pick]
    assert all(r["sha1"] == r["spike_sha1"] for r in rows)
    man = om.load_manifest(d / om.MANIFEST_NAME)
    assert any(i["path"] == lst.as_posix() for i in man["inputs"])            # an absolute input path
