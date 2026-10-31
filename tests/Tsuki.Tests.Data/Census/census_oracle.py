"""Thin caller of the Python oracle for census T0 (package K1a): every reference the 549 scripts make, resolved the way the Python
reference library does (``tsuki.script`` AllScr / AssetResolver / LayerTracker, ``tsuki.script_tables``, ``tsuki.audio``).

    python -I tests/Tsuki.Tests.Data/Census/census_oracle.py refs|glyphs

``refs``    one tab-separated line per reference, in script and statement order:
            ``kind \\t script \\t offset \\t command \\t key \\t result``
            kind: image voice se bgm movie scene label text motion rule.  The C# census (``Tsuki.Data.Census.ReferenceCensus``)
            prints the same lines from the same scripts through ``GameFolderSource`` and the test compares them one by one.
``glyphs``  the characters of the text lines the scripts reference, per language: how many distinct characters, and which
            have no entry in the glyph lookup tables (``tsuki.font_eboot``), as JSON lines.

Nothing is written to disk.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))

from tsuki import game_dir, image_dir  # noqa: E402
from tsuki import script as S  # noqa: E402
from tsuki import script_tables as ST  # noqa: E402
from tsuki.nam import load_nam  # noqa: E402

IMAGE_COMMANDS = ("STCH", "SCH2", "SPFC", "SPRD", "STCG")
LABEL_JUMPS = S.LABEL_JUMPS
TEXT_COMMANDS = ("ZM", "ZD", "MSAD", "SELR")
TEXT_LINES = 43969


def emit(*fields: object) -> None:
    sys.stdout.write("\t".join(str(f) for f in fields) + "\n")


def image_result(ref: S.AssetRef) -> str:
    layers = "+".join(f"{a}#{i}" for a, i in ref.layers) or "-"
    return f"{layers};m={len(ref.missing)}"


def mode_refs() -> None:
    res = S.AssetResolver()
    allpac3 = load_nam(image_dir() / "allpac3.nam").names
    mask_base = allpac3.index("MASK_TOP.MZP")
    with S.AllScr() as scr:
        for i in range(len(scr)):
            tracker = S.LayerTracker()
            labels = scr.label_map(i)
            for c in scr.commands(i):
                n = c.name
                if not n:
                    continue
                if n in IMAGE_COMMANDS:
                    for r in S.command_assets(c, res, tracker):
                        emit("image", i, c.offset, n, r.key.upper().split(".", 1)[0], image_result(r))
                elif n in ("VPLY", "VPL2"):
                    r = res.voice(c)
                    idx = "-" if r.index is None else r.index
                    emit("voice", i, c.offset, n, r.key, f"{idx}|{0 if r.note else 1}")
                elif n in ("SEPL", "SEFI") and len(c.args) > 2:
                    r = res.se(c.args[2].value)
                    emit("se", i, c.offset, n, r.key.upper(), "-" if r.index is None else r.index)
                elif n == "MPLY" and c.args:
                    r = res.bgm(c.int_arg(0))
                    emit("bgm", i, c.offset, n, r.key, "-" if r.index is None else r.index)
                elif n == "TOMP" and c.args:
                    m = c.value(0)
                    r = res.movie(m if m is not None else 0)
                    emit("movie", i, c.offset, n, r.key, r.note or "-")
                elif n.startswith("J") and c.args:
                    sid = c.int_arg(0)
                    target = ST.scene_index(sid)
                    emit("scene", i, c.offset, n, sid, "-" if target is None else target)
                    lab = c.args[1].value if len(c.args) > 1 and c.args[1].value else None
                    if lab is not None:
                        if target is None:
                            state = "noscript"
                        else:
                            state = "found" if lab in scr.label_map(target) else "missing"
                        emit("label", i, c.offset, n, f"{sid}:{lab}", state)
                elif n in LABEL_JUMPS and len(c.args) > LABEL_JUMPS[n]:
                    lab = c.args[LABEL_JUMPS[n]].value
                    emit("label", i, c.offset, n, lab, "found" if lab in labels else "missing")
                elif n == "GOTS":
                    for a in c.args[1:]:
                        emit("label", i, c.offset, n, a.value, "found" if a.value in labels else "missing")
                if n in ("STMA", "SGMA"):
                    m = c.expr_arg(1)
                    state = "none" if m is None else "zero" if m == 0 else "ok" if 1 <= m <= ST.MOTION_COUNT else "out"
                    emit("motion", i, c.offset, n, "-" if m is None else m, state)
                if n == "FADS" and c.expr_arg(0) == 2:
                    rule = c.expr_arg(2)
                    if rule is not None:
                        k = mask_base + rule
                        emit("rule", i, c.offset, n, rule, allpac3[k] if 0 <= k < len(allpac3) else "-")
                if n in TEXT_COMMANDS:
                    for a in c.args:
                        for t in S.text_refs(a.raw):
                            emit("text", i, c.offset, n, t, "ok" if 0 <= t < TEXT_LINES else "range")
                tracker.update(c)


def mode_glyphs() -> None:
    from tsuki import text as T
    from tsuki.font_eboot import EbootFontTables, pack_utf8

    ft = EbootFontTables.open(game_dir() / "eboot.bin")
    st = T.ScriptText()
    ids: set[int] = set()
    with S.AllScr() as scr:
        for i in range(len(scr)):
            for c in scr.commands(i):
                if c.name in TEXT_COMMANDS:
                    for a in c.args:
                        ids.update(S.text_refs(a.raw))
    ids = {t for t in ids if 0 <= t < TEXT_LINES}
    narrow = ft.lookups["narrow"]
    for lang in ("ja", "en", "zc", "zt"):
        wide = ft.lookups[f"{lang}_wide"]
        chars: dict[int, int] = {}
        queue = sorted(ids)
        seen = set(queue)
        while queue:
            t = queue.pop()
            line = st.get_line(lang, t)
            for tok in T.iter_tokens(T.tokenize(line, lang)):
                if isinstance(tok, T.Text):
                    for ch in tok.text:
                        key = pack_utf8(ch)
                        chars[key] = chars.get(key, 0) + 1
                elif isinstance(tok, T.Ref) and 0 <= tok.line_id < TEXT_LINES and tok.line_id not in seen:
                    seen.add(tok.line_id)
                    queue.append(tok.line_id)
        missing = {}
        for key, n in chars.items():
            tbl = wide if key >= 0x10000 else narrow
            if tbl.get(key) is None:
                missing[key] = n
        sys.stdout.write(json.dumps({
            "k": "glyphs", "lang": lang, "lines": len(seen), "distinct": len(chars), "occurrences": sum(chars.values()),
            "missing": [[f"{k:06x}", missing[k]] for k in sorted(missing)],
        }, separators=(",", ":")) + "\n")


def main() -> int:
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "refs":
        mode_refs()
    elif mode == "glyphs":
        mode_glyphs()
    else:
        sys.stderr.write("usage: census_oracle.py refs|glyphs\n")
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
