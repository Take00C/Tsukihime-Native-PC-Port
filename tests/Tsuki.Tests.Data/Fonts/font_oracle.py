"""Thin caller of the Python oracle for package A3a: the eboot glyph tables (``tsuki.font_eboot``) and the glyph pages (``tsuki.font.FontPages``).

JSON lines on stdout, nothing on disk. ``tests/Tsuki.Tests.Data/Fonts/*.cs`` run it once per test process and compare.

    python -I tests/Tsuki.Tests.Data/Fonts/font_oracle.py tables|pages

``tables``  verify() issues, the 16 lookup tables (17,423 entries) and the 17 record tables (every record of the table plus 40 past its end,
            which the engine reads unchecked), the wide-grid bases and the tag names.
``pages``   for every language (ja en zc zt ko) and every page 0..64: header words, flag, size and SHA-1 of the palette indices, the palette, the
            alpha plane and the RGBA bytes.
"""
from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))

from tsuki import game_dir  # noqa: E402
from tsuki.font import FontPages  # noqa: E402
from tsuki.font_eboot import LOOKUP_SPECS, RECORD_SPECS, EbootFontTables  # noqa: E402

OVERRUN = 40


def emit(obj: object) -> None:
    sys.stdout.write(json.dumps(obj, separators=(",", ":")) + "\n")


def mode_tables() -> None:
    t = EbootFontTables.open(game_dir() / "eboot.bin")
    emit({"k": "verify", "issues": t.verify(), "wide_base": list(t.wide_base), "tags": list(t.tags)})
    total = 0
    for spec in LOOKUP_SPECS:
        lt = t.lookups[spec.name]
        total += lt.count
        emit({"k": "lookup", "name": spec.name, "va": spec.va, "count": lt.count, "lea": spec.lea_site,
              "entries": [[k, lt.index[k]] for k in lt.keys]})
    emit({"k": "lookup_total", "total": total})
    for spec in RECORD_SPECS:
        rt = t.records[spec.name]
        rows = []
        for i in range(spec.count + OVERRUN):
            r = rt.get(i)
            rows.append([r.tex, r.x, r.y, r.w, r.h])
        emit({"k": "records", "name": spec.name, "va": spec.va, "count": spec.count, "lea": spec.lea_site, "entries": rows})
    emit({"k": "end"})


def sha1(b: bytes) -> str:
    return hashlib.sha1(b).hexdigest()


def mode_pages() -> None:
    for lang in ("ja", "en", "zc", "zt", "ko"):
        with FontPages.for_lang(lang) as fp:
            for k in range(len(fp)):
                p = fp.page(k)
                emit({"k": "page", "lang": lang, "section": k, "w": p.width, "h": p.height, "header": list(p.header), "flag": p.flag,
                      "indices_sha1": sha1(p.indices.tobytes()), "palette_sha1": sha1(p.palette.tobytes()),
                      "alpha_sha1": sha1(p.alpha.tobytes()), "rgba_sha1": sha1(p.rgba().tobytes())})
    emit({"k": "end"})


MODES = {"tables": mode_tables, "pages": mode_pages}

if __name__ == "__main__":
    if len(sys.argv) != 2 or sys.argv[1] not in MODES:
        raise SystemExit(f"usage: font_oracle.py {'|'.join(MODES)}")
    MODES[sys.argv[1]]()
