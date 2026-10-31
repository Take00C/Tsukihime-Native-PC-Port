"""Thin caller of the Python oracle for package A3a: allscr names, label tables and scripts, scene ids, movie files, motions and the
engine command tables (``tsuki.script`` AllScr / parse_labels and ``tsuki.script_tables``).

JSON lines on stdout, nothing on disk. ``tests/Tsuki.Tests.Data/Scripts/*.cs`` run it once per test process and compare.

    python -I tests/Tsuki.Tests.Data/Scripts/script_tables_oracle.py allscr|eboot|engine

``allscr``  the 549 names, per script the SHA-256 of the decoded bytes and of the raw label table, the parsed labels, and the section layout.
``eboot``   scene ids (constants and ``read_scene_ids``), movie files (constants and ``read_movie_files``) and all 24,192 motions with their
            71,905 keyframes (a 16-digit hash per motion).
``engine``  ENGINE_MAIN, ENGINE_ALIASES, OBJ_COMMANDS, KEEP_BGM_FLAG_VA and ``engine_handler`` over a grid of names.
"""
from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))

from tsuki import game_dir  # noqa: E402
from tsuki import script as S  # noqa: E402
from tsuki import script_tables as ST  # noqa: E402


def emit(obj: object) -> None:
    sys.stdout.write(json.dumps(obj, separators=(",", ":")) + "\n")


def h16(s: str) -> str:
    return hashlib.sha1(s.encode("utf-8")).digest()[:8].hex()


def mode_allscr() -> None:
    with S.AllScr() as scr:
        n = len(scr)
        emit({"k": "layout", "scripts": n, "sections": len(scr.arc), "names": list(scr.names), "label_tables": len(scr._labels_arc)})
        for i in range(n):
            raw = scr.raw(i)
            labels = scr.labels(i)
            lab_raw = bytes(scr._labels_arc.view(i))
            emit({"k": "script", "i": i, "name": scr.names[i], "len": len(raw), "sha256": hashlib.sha256(raw).hexdigest(),
                  "labels_sha256": hashlib.sha256(lab_raw).hexdigest(), "labels_len": len(lab_raw),
                  "labels": [[lab.name, lab.block, lab.offset] for lab in labels]})
        emit({"k": "end"})


def mode_eboot() -> None:
    eboot = game_dir() / "eboot.bin"
    emit({"k": "scenes", "constants": list(ST.SCENE_IDS), "eboot": list(ST.read_scene_ids(eboot)),
          "index": [[sid, ST.scene_index(sid)] for sid in (110, 240, 260, 270, 300, 790, 1000, 1024, 2000, 8017, 5, 99999, -1)]})
    emit({"k": "movies", "constants": list(ST.MOVIE_FILES), "eboot": list(ST.read_movie_files(eboot))})
    motions = ST.read_motions(eboot)
    hs = []
    total = 0
    for m in motions:
        total += len(m.keyframes)
        body = f"{m.id}|{m.duration}|" + ";".join(f"{k.time},{','.join(str(x) for x in k.fields)}" for k in m.keyframes)
        hs.append(h16(body))
    emit({"k": "motions", "count": len(motions), "keyframes": total, "constants": [ST.MOTION_COUNT, ST.MOTION_DURATION_VA, ST.MOTION_END_VA,
          ST.MOTION_KEYS_VA, ST.MOTION_KEY_SIZE, ST.MOTION_MAX_KEY, ST.MOTION_NULL], "h": "".join(hs),
          "lengths": [len(m.keyframes) for m in motions]})
    m1 = motions[0]
    emit({"k": "first", "id": m1.id, "duration": m1.duration, "keys": [[k.time, list(k.fields)] for k in m1.keyframes],
          "last": [motions[-1].id, motions[-1].duration, [[k.time, list(k.fields)] for k in motions[-1].keyframes]]})
    emit({"k": "end"})


def mode_engine() -> None:
    emit({"k": "main", "entries": [[k, v[0], v[1]] for k, v in sorted(ST.ENGINE_MAIN.items())]})
    emit({"k": "aliases", "entries": sorted(ST.ENGINE_ALIASES.items())})
    emit({"k": "obj", "names": sorted(ST.OBJ_COMMANDS)})
    emit({"k": "keep", "va": ST.KEEP_BGM_FLAG_VA})
    names = [k for k in ST.ENGINE_MAIN if "*" not in k] + ["JAAA", "ZZZZ", "JUMP", "Z000", "DMMY", "NOP_", "IFP4", "SNX4", "xxxx", "",
                                                           "J", "Z", "SNX1", "FANX", "STQK", "RTM2", "_ZM0"]
    rows = []
    for n in names:
        h = ST.engine_handler(n)
        rows.append([n, None if h is None else h[0], None if h is None else h[1], ST.is_engine_command(n)])
    emit({"k": "handlers", "entries": rows})


MODES = {"allscr": mode_allscr, "eboot": mode_eboot, "engine": mode_engine}

if __name__ == "__main__":
    if len(sys.argv) != 2 or sys.argv[1] not in MODES:
        raise SystemExit(f"usage: script_tables_oracle.py {'|'.join(MODES)}")
    MODES[sys.argv[1]]()
