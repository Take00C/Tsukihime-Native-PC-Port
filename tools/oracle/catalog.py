"""Area ``catalog``: the SELF/ELF reader, the eboot tables and the picture-name lookups, from
``eboot.selfelf``, ``eboot.uitext``, ``tsuki.catalog_eboot``, ``tsuki.image_catalog`` and ``tsuki.catalog``
(ROADMAP M1 acceptance 2 and the catalog / name / ui_text part of acceptance 5).

The C# ports (``Tsuki.Data.Eboot``, ``Tsuki.Data.Catalog``) must produce the same values on the real
eboot.bin and the real ``.nam`` files.  Three parts (directories of ``samples/golden/catalog/``):

``eboot``    the SELF segments, the ELF program headers, the SCE dynamic table, modules, libraries, symbols,
             relocations and imports of ``eboot.selfelf`` (``self.json``, ``imports.tsv``), plus the five
             ``ui_text_<lang>.tsv`` files exactly as ``python -m eboot.uitext`` writes them
             (5 x 1,499 entries; byte for byte the files of ``samples/engine`` when those exist).
``catalog``  the engine image table (14,744 records + 31 empty slots, ``image_catalog.tsv`` with the language
             bands and the source / thumbnail rectangles of every language), the two picture-name lists
             (14,744 + 1 and 9,958 + 1 records), the movie table (134 records + terminator), the 21 UI
             texture registration tables (220 records), the language suffixes and the prologue remap.
``lookup``   what the engine does with those tables: the NAM file-name search (``nam_queries.tsv``), the
             script image id resolution with the STCH a9 / a10 suffix (``script_queries.tsv``), the name
             grammar of every picture name (``classify.tsv``) and the small rules (movie remap, UI archive
             names, child clamp, system language) in ``rules.json``.

All files are local (``samples/golden/``, never committed): they are derived from the user's eboot.bin.
Run: ``python -m oracle.run catalog`` (or ``python -m oracle.catalog``, which registers the area itself).
Queries are written as lower-case hex of their bytes, so the TSV files are plain ASCII/UTF-8.
"""
from __future__ import annotations

import hashlib
import random
import struct
import sys
import time
from pathlib import Path
from typing import Any, Callable, Iterable, Optional, Sequence

from eboot import uitext
from eboot.selfelf import Eboot as SelfEboot
from eboot.selfelf import decode_id
from tsuki import catalog as tcat
from tsuki import catalog_eboot as ceb
from tsuki import image_catalog as icat
from tsuki.nam import load_nam

from .manifest import AreaWriter, ManifestError, PartWriter, Producer

__all__ = [
    "AREA", "MODULE", "PARTS", "DESCRIPTION", "SEED", "tsv", "hexs", "nam_query_bytes", "script_queries",
    "classify_names", "run", "main",
]

AREA = "catalog"
MODULE = "oracle.catalog"
PARTS: tuple[str, ...] = ("eboot", "catalog", "lookup")
DESCRIPTION = ("SELF/ELF reader, eboot tables (image catalog, picture names, movies, UI texture tables, ui_text) and "
               "the NAM / script-image lookups for the C# catalog port")
SEED = 20261026

ENGINE_MD = "docs/formats/engine.md 2.1"
IMAGES_MD = "docs/formats/images.md 8"
ARCHIVES_MD = "docs/formats/archives_ui_movies.md"
RR = "RUNTIME_REQUIREMENTS 3.2-3.3"

SEARCHED = tcat.IMAGE_SEARCH_ORDER               # allpac4, allpac3, allpac1_2, allpac1, allpac0
NAM_ARCHIVES = ("allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4")
LANG_COUNT = 5


# ---------------------------------------------------------------------------
# small helpers
# ---------------------------------------------------------------------------

def _cell(v: Any) -> str:
    if v is None:
        return "-"
    s = str(v)
    if "\t" in s or "\n" in s or "\r" in s:
        raise ManifestError(f"TSV cell with a control character: {s!r}")
    return s


def tsv(columns: Sequence[str], rows: Iterable[Sequence[Any]]) -> bytes:
    """UTF-8, LF, one header line; ``None`` is written as ``-``."""
    lines = ["\t".join(columns)]
    for r in rows:
        if len(r) != len(columns):
            raise ManifestError(f"row has {len(r)} cells for {len(columns)} columns: {r!r}")
        lines.append("\t".join(_cell(v) for v in r))
    return ("\n".join(lines) + "\n").encode("utf-8")


def hexs(b: bytes) -> str:
    """Lower-case hex; the empty string is written as ``-`` so that no TSV cell is empty."""
    return b.hex() if b else "-"


def _producer(function: str, args: dict[str, Any], uses: tuple[str, ...]) -> Producer:
    return Producer(MODULE, function, args, uses)


def _digest(lines: Iterable[str]) -> str:
    h = hashlib.sha1()
    for ln in lines:
        h.update(ln.encode("utf-8"))
        h.update(b"\n")
    return h.hexdigest()


# ---------------------------------------------------------------------------
# part eboot: SELF / ELF and ui_text
# ---------------------------------------------------------------------------

def _self_json(eb: SelfEboot, size: int) -> dict[str, Any]:
    segs = [{"index": s.index, "flags": s.flags, "offset": s.offset, "comp_size": s.comp_size, "dec_size": s.dec_size,
             "phdr_id": s.phdr_id, "blocked": s.blocked, "deflated": s.deflated, "encrypted": s.encrypted}
            for s in eb.self_segments]
    phdrs = [{"index": p.index, "type": p.type, "type_name": p.type_name, "flags": p.flags, "perms": p.perms,
              "offset": p.offset, "vaddr": p.vaddr, "paddr": p.paddr, "filesz": p.filesz, "memsz": p.memsz,
              "align": p.align, "file_offset": eb.phdr_file_offset(p)} for p in eb.phdrs]
    mods = [{"id": m.id, "name": m.name, "major": m.version_major, "minor": m.version_minor}
            for m in eb.modules().values()]
    libs = [{"id": lb.id, "name": lb.name, "version": lb.version, "attr": lb.attr} for lb in eb.libraries().values()]
    name, ver = eb.module_info()
    syms = eb.symbols()
    sym_lines = [f"{s.index}|{s.name}|{s.info}|{s.other}|{s.shndx}|{s.value}|{s.size}" for s in syms]
    jmp = eb.jmprel()
    rel = eb.rela()
    by_type: dict[str, int] = {}
    for r in rel:
        by_type[str(r.type)] = by_type.get(str(r.type), 0) + 1
    imps = eb.imports()
    plt_found = eb.find_plt(imps)
    # VA probes: mapped, bss, unmapped and boundary addresses
    probes = []
    for va in (0, 1, 0x80, 0x2DA510, 0x329DC7, 0x329DC8, 0x3FFFFF, 0x400000, 0x41D5F0, 0x435ECF, 0x435ED0,
               0x5FFFFF, 0x600000, 0x671900, 0x7F6800, 0xDC8C70, 0xDD66DF, 0xDD66E0, 0xDD66E1, 0x1000000,
               0x2085790, 0x2085791):
        ph = eb.segment_of(va)
        probes.append({"va": va, "file": eb.va_to_file(va), "segment": None if ph is None else ph.index})
    reads = []
    for va, n in ((0x2DA510, 22), (0x41D5F0, 16), (0x600000, 8), (0xDD66D8, 16), (0xDD66E0, 8), (0xDD7000, 4),
                  (0x671900 + 8, 8), (0x400000, 4)):
        reads.append({"va": va, "n": n, "hex": eb.read_va(va, n).hex()})
    cstrs = []
    for va, limit in ((0x2DA510, 4096), (0x2DA510, 5), (0x1AF489, 4096), (0xDD66E0, 4096), (0x1000000, 4096),
                      (0x435448, 3)):
        cstrs.append({"va": va, "limit": limit, "hex": eb.cstr(va, limit).hex()})
    return {
        "file_size": size, "elf_off": eb.elf_off, "entry": eb.entry,
        "self_segments": segs, "phdrs": phdrs,
        "dynamic": [[t, v] for t, v in eb.dynamic],
        "modules": mods, "libraries": libs, "module_info": [name, ver],
        "symbols": {"count": len(syms), "sha1": _digest(sym_lines), "first": sym_lines[:3], "last": sym_lines[-2:]},
        "jmprel": {"count": len(jmp), "sha1": _digest(f"{r.offset}|{r.sym}|{r.type}|{r.addend}" for r in jmp)},
        "rela": {"count": len(rel), "by_type": by_type,
                 "sha1": _digest(f"{r.offset}|{r.sym}|{r.type}|{r.addend}" for r in rel)},
        "imports": {"count": len(imps), "plt_found": plt_found},
        "text_index": next(p.index for p in eb.phdrs if p.type == 1 and p.flags & 1),
        "data_index": next(p.index for p in eb.phdrs if p.type == 1 and p.flags & 2),
        "relro_index": None if eb.relro is None else eb.relro.index,
        "va_probes": probes, "reads": reads, "cstrs": cstrs,
        "decode_id": {s: decode_id(s) for s in ("A", "B", "U", "Z", "a", "z", "0", "9", "+", "-", "AA", "BA", "U-", "ZZ", "-+")},
    }


def _imports_tsv(eb: SelfEboot) -> bytes:
    imps = eb.imports()
    eb.find_plt(imps)
    cols = ("sym_index", "nid", "library", "module", "lib_id", "mod_id", "sym_type", "got_va", "reloc_type", "plt_va")
    return tsv(cols, ((i.sym_index, i.nid, i.library, i.module, i.lib_id, i.mod_id, i.sym_type, i.got_va,
                       i.reloc_type, i.plt_va) for i in imps))


def export_eboot(part: PartWriter, game: Path, log: Optional[Callable[[str], None]]) -> None:
    path = game / "eboot.bin"
    part.add_input(path)
    eb = SelfEboot.open(path)
    try:
        info = _self_json(eb, path.stat().st_size)
        part.write_json("self.json", info, _producer("export_eboot", {"file": "self.json"},
                                                     ("eboot.selfelf.Eboot",)))
        imports_tsv = _imports_tsv(eb)
        part.write_bytes("imports.tsv", imports_tsv, "bytes", _producer("export_eboot", {"file": "imports.tsv"},
                                                                       ("eboot.selfelf.Eboot.imports",)),
                         {"encoding": "tsv", "rows": info["imports"]["count"]})
        # engine.md 2.1 counts [V-all]
        part.check("segments", 10, len(info["self_segments"]), "RUNTIME_REQUIREMENTS 3.3 item 2")
        part.check("segments_plain", True,
                   all(not (s["deflated"] or s["encrypted"]) for s in info["self_segments"]),
                   "docs/formats/engine.md 1.2")
        part.check("dynamic_symbols", 339, info["symbols"]["count"], ENGINE_MD)
        part.check("jmprel_relocations", 334, info["jmprel"]["count"], ENGINE_MD)
        part.check("rela_entries", 39836, info["rela"]["count"], ENGINE_MD)
        part.check("rela_relative", 39459, info["rela"]["by_type"].get("8", 0), ENGINE_MD)
        part.check("rela_64", 377, info["rela"]["by_type"].get("1", 0), ENGINE_MD)
        part.check("needed_modules", 27, len(info["modules"]), ENGINE_MD)
        part.check("import_libraries", 27, len(info["libraries"]), ENGINE_MD)
        part.check("distinct_nids", 337, len({ln.split("\t")[1] for ln in imports_tsv.decode().splitlines()[1:]}),
                   ENGINE_MD)
        part.check("plt_stubs", 334, info["imports"]["plt_found"], "docs/formats/engine.md 1.3")
        part.count("imports", info["imports"]["count"])
        part.count("symbols", info["symbols"]["count"])

        table = uitext.read_table(eb)
        compared = 0
        samples = Path(__file__).resolve().parents[2] / "samples" / "engine"
        for lang in uitext.LANGS:
            rows = table[lang]
            body = ["id\tva\ttext"] + [f"{u.id}\t{u.va:08x}\t{uitext.escape(u.text)}" for u in rows]
            data = ("\n".join(body) + "\n").encode("utf-8")
            part.write_bytes(f"ui_text_{lang}.tsv", data, "bytes",
                             _producer("export_eboot", {"file": f"ui_text_{lang}.tsv", "lang": lang},
                                       ("eboot.uitext.read_table", "eboot.uitext.escape")),
                             {"encoding": "tsv", "columns": ["id", "va", "text"], "rows": len(rows)})
            part.check(f"ui_text_{lang}_entries", 1499, len(rows), "RUNTIME_REQUIREMENTS 1 item 3")
            ref = samples / f"ui_text_{lang}.tsv"
            if ref.is_file():
                same = ref.read_bytes() == data
                part.check(f"ui_text_{lang}_equals_samples_engine", True, same, "RUNTIME_REQUIREMENTS 1 item 3")
                compared += 1
        part.count("ui_text_compared_with_samples_engine", compared)
        part.check("ui_text_ja_id0", "NULL", table["ja"][0].text, RR)
        part.count("ui_text_entries", {k: len(v) for k, v in table.items()})
        part.count("ui_text_id0", {k: v[0].text for k, v in table.items()})
    finally:
        eb.close()


# ---------------------------------------------------------------------------
# part catalog: the engine tables
# ---------------------------------------------------------------------------

CATALOG_COLUMNS = ("slot", "used", "va", "file_offset", "name0", "name1", "layer_x", "layer_y", "cache0", "cache1",
                   "w3", "h3", "width", "height", "anchor_x", "anchor_y", "kind", "draw_flags", "draw_param",
                   "pic_flags", "char_id", "scale_class", "band_stride", "src_ja", "src_en", "src_zc", "src_zt",
                   "thumb_ja", "thumb_en", "thumb_zc", "thumb_zt", "logical_name", "paired")


def _rect(r: tuple[int, int, int, int]) -> str:
    return ",".join(str(v) for v in r)


def _catalog_rows(data: bytes, start: int, used: int, empty: int, cat: list[Any],
                  table: list[Any]) -> list[list[Any]]:
    """One row per slot; the two Python readers (image_catalog / catalog_eboot) must agree on every field."""
    rows: list[list[Any]] = []
    for slot in range(used + empty):
        off = start + slot * icat.RECORD_SIZE
        f = icat._REC.unpack_from(data, off)
        va = ceb.IMAGE_TABLE_VA + ceb.IMAGE_RECORD_SIZE * slot
        if ceb.Eboot.va_to_offset(va) != off:
            raise ManifestError(f"slot {slot}: VA 0x{va:x} does not map to file 0x{off:x}")
        if slot < used:
            e = cat[slot]
            r = table[slot]
            same = (e.name0 == r.file and e.name1 == r.overlay and e.w3 == r.a and e.h3 == r.b and e.width == r.w
                    and e.height == r.h and e.anchor_x == (r.c if r.c < 1 << 31 else r.c - (1 << 32))
                    and e.anchor_y == (r.d if r.d < 1 << 31 else r.d - (1 << 32)) and e.kind == r.b3c
                    and e.draw_flags == r.flags and e.draw_param == r.b3e and e.pic_flags == r.layer_kind
                    and e.char_id == r.char_id and e.scale_class == r.size_class and e.index == r.index == slot
                    and r.va == va and e.file_offset == off)
            if not same:
                raise ManifestError(f"slot {slot}: image_catalog and catalog_eboot disagree")
            rows.append([slot, 1, f"0x{va:x}", f"0x{off:x}", e.name0, e.name1, e.layer_x, e.layer_y, f[5], f[6],
                         e.w3, e.h3, e.width, e.height, e.anchor_x, e.anchor_y, e.kind, e.draw_flags, e.draw_param,
                         e.pic_flags, e.char_id, e.scale_class, e.band_stride,
                         *(_rect(e.source_rect(la)) for la in icat.LANGS),
                         *(_rect(e.thumb_rect(la)) for la in icat.LANGS), r.name, int(r.paired)])
        else:
            if f[1] or f[2]:
                raise ManifestError(f"slot {slot} is not empty")
            rows.append([slot, 0, f"0x{va:x}", f"0x{off:x}", None, None, f[3], f[4], f[5], f[6], f[8], f[9], f[10],
                         f[11], f[12], f[13], f[14], f[15], f[16], f[17], f[18], f[19], None, None, None, None,
                         None, None, None, None, None, None, 0])
    return rows


def export_catalog(part: PartWriter, game: Path, log: Optional[Callable[[str], None]]) -> None:
    path = game / "eboot.bin"
    part.add_input(path)
    data = path.read_bytes()
    start, used, empty = icat.find_catalog(data)
    cat = icat.load_catalog(data)
    with ceb.Eboot(path) as eb:
        table = eb.image_table()
        names = eb.image_names()
        old_names = eb.name_list(ceb.OLD_IMAGE_NAMES_VA)
        movies = eb.movie_table()
        suffixes = eb.lang_suffixes()
        prologue = eb.prologue_ids()
        ui_tables = eb.ui_tables()
        end_mark = eb.read(ceb.IMAGE_NAMES_VA + 32 * len(names), 1)
        old_end_mark = eb.read(ceb.OLD_IMAGE_NAMES_VA + 32 * len(old_names), 1)
        movie_term = eb.i32(0xDCA590)
        name_crlf = all(eb.read(ceb.IMAGE_NAMES_VA + 32 * k + 30, 2) == b"\r\n" for k in range(len(names) - 1))
        raw_zero = all(struct.unpack_from("<Q", data, start + k * 0x48)[0] == 0
                       and struct.unpack_from("<I", data, start + k * 0x48 + 0x28)[0] == 0
                       and struct.unpack_from("<I", data, start + k * 0x48 + 0x44)[0] == 0 for k in range(used))
    rows = _catalog_rows(data, start, used, empty, cat, table)
    part.write_bytes("image_catalog.tsv", tsv(CATALOG_COLUMNS, rows), "bytes",
                     _producer("export_catalog", {"file": "image_catalog.tsv"},
                               ("tsuki.image_catalog.load_catalog", "tsuki.catalog_eboot.Eboot.image_table")),
                     {"encoding": "tsv", "columns": list(CATALOG_COLUMNS), "rows": len(rows)})
    part.check("catalog_file_offset", 0x3D78A0, start, IMAGES_MD)
    part.check("catalog_used", 14744, used, IMAGES_MD)
    part.check("catalog_empty", 31, empty, IMAGES_MD)
    part.check("catalog_slots", ceb.IMAGE_TABLE_SLOTS, used + empty, IMAGES_MD)
    part.check("catalog_zero_fields", True, raw_zero, IMAGES_MD)
    part.check("catalog_first_two", ["BLACK.MZP", "BLUE.MZP"], [cat[0].name0.upper(), cat[1].name0.upper()], RR)
    part.check("catalog_paired", 3529, sum(1 for e in cat if e.name1), IMAGES_MD)
    part.check("catalog_bands", 166, sum(1 for e in cat if e.has_language_bands), IMAGES_MD)
    part.check("catalog_variants", 7288, sum(1 for e in cat if e.is_variant), IMAGES_MD)
    part.count("catalog_records", used)
    part.count("catalog_empty_slots", empty)

    name_rows = [[k, n] for k, n in enumerate(names)]
    part.write_bytes("image_names.tsv", tsv(("index", "name"), name_rows), "bytes",
                     _producer("export_catalog", {"file": "image_names.tsv"}, ("tsuki.catalog_eboot.Eboot.image_names",)),
                     {"encoding": "tsv", "rows": len(name_rows), "empty_name_is": "empty cell"})
    old_rows = [[k, n] for k, n in enumerate(old_names)]
    part.write_bytes("old_image_names.tsv", tsv(("index", "name"), old_rows), "bytes",
                     _producer("export_catalog", {"file": "old_image_names.tsv"}, ("tsuki.catalog_eboot.Eboot.name_list",)),
                     {"encoding": "tsv", "rows": len(old_rows)})
    part.check("names_count", 14745, len(names), ARCHIVES_MD + " 2.4")
    part.check("names_first_last", ["BLACK", ""], [names[0], names[-1]], ARCHIVES_MD + " 2.4")
    part.check("names_end_mark", "ff", end_mark.hex(), ARCHIVES_MD + " 2.4")
    part.check("names_crlf", True, name_crlf, IMAGES_MD + " 8.3")
    part.check("names_equal_records", True,
               all(names[r.index] == r.name.rsplit(".", 1)[0].upper() for r in table), ARCHIVES_MD + " 2.4")
    part.check("old_names_prefix", True, old_names[:9958] == names[:9958] and len(old_names) == 9959, ARCHIVES_MD + " 2.4")
    part.check("old_names_end_mark", "ff", old_end_mark.hex(), ARCHIVES_MD + " 2.4")
    part.check("picture_names_loader", True, icat.load_picture_names(data) == names[:-1], IMAGES_MD + " 8.3")

    movie_cols = ("movie_id", "va", "path", "f0", "f1", "f2", "f3", "f4", "f5", "f6", "f7")
    movie_rows = [[m.movie_id, f"0x{m.va:x}", m.path, *m.raw_fields] for m in movies]
    part.write_bytes("movies.tsv", tsv(movie_cols, movie_rows), "bytes",
                     _producer("export_catalog", {"file": "movies.tsv"}, ("tsuki.catalog_eboot.Eboot.movie_table",)),
                     {"encoding": "tsv", "columns": list(movie_cols), "rows": len(movie_rows)})
    part.check("movies_count", 134, len(movies), ARCHIVES_MD + " 9")
    part.check("movies_ids", list(range(134)), [m.movie_id for m in movies], ARCHIVES_MD + " 9")
    part.check("movies_terminator", -1, movie_term, RR)
    part.check("movies_names", list(tcat.MOVIE_NAMES), [m.file_name[:-4] for m in movies], ARCHIVES_MD + " 9")
    part.check("movies_first_path", "/app0/image/movie/titlebg1.mp4", movies[0].path, RR)

    ui_json = []
    n_records = 0
    for t in ui_tables:
        n_records += len(t.records)
        ui_json.append({"va": t.va, "terminator": t.terminator.hex(),
                        "records": [[r.va, r.state, r.tex_slot, r.name, r.child, r.child2] for r in t.records]})
    part.write_json("ui_tables.json", {"table_vas": list(ceb.UI_TABLE_VAS), "tables": ui_json},
                    _producer("export_catalog", {"file": "ui_tables.json"}, ("tsuki.catalog_eboot.Eboot.ui_tables",)))
    part.check("ui_tables", 21, len(ui_tables), ARCHIVES_MD + " 7")
    part.check("ui_records", 220, n_records, ARCHIVES_MD + " 7")

    locate = icat.find_catalog(data)
    part.write_json("tables.json", {
        "lang_suffixes": suffixes, "prologue_ids": prologue,
        "find_catalog": {"file_offset": locate[0], "used": locate[1], "empty": locate[2]},
        "image_table_va": ceb.IMAGE_TABLE_VA, "image_table_slots": ceb.IMAGE_TABLE_SLOTS,
        "image_names_va": ceb.IMAGE_NAMES_VA, "old_image_names_va": ceb.OLD_IMAGE_NAMES_VA,
        "movie_table_va": ceb.MOVIE_TABLE_VA, "lang_suffix_va": ceb.LANG_SUFFIX_VA,
        "prologue_table_va": ceb.PROLOGUE_TABLE_VA, "segments": [list(s) for s in ceb.SEGMENTS],
        "ui_table_vas": list(ceb.UI_TABLE_VAS), "ui_tables_unreferenced": list(ceb.UI_TABLES_UNREFERENCED),
        "movie_terminator_va": 0xDCA590,
    }, _producer("export_catalog", {"file": "tables.json"}, ("tsuki.catalog_eboot", "tsuki.image_catalog.find_catalog")))
    part.check("lang_suffixes", ["ja", "en", "zc", "zt", "ko"], suffixes, RR)
    part.check("prologue_ids", [4, 46, 47, 106], prologue, RR)


# ---------------------------------------------------------------------------
# part lookup: NAM search, script image ids, name grammar, rules
# ---------------------------------------------------------------------------

def nam_query_bytes(names_by_archive: dict[str, list[str]], rng: random.Random) -> list[bytes]:
    """The NAM-search queries: every name of the five searched archives as given, lower-cased and behind a
    backslash path, plus boundary cases around the 28-byte key, Shift-JIS bytes, NULs and misses."""
    out: list[bytes] = []
    seen: set[bytes] = set()

    def add(q: bytes) -> None:
        if q not in seen:
            seen.add(q)
            out.append(q)

    pool: list[str] = []
    for arc in SEARCHED:
        for n in names_by_archive[arc]:
            pool.append(n)
            add(n.encode("ascii"))
            add(n.lower().encode("ascii"))
            add(("dir\\" + n).encode("ascii"))
    for n in names_by_archive["allpac2"][:200]:
        add(n.encode("ascii"))                 # allpac2 is never searched: misses, or hits in another archive
    # boundary cases around the 28-byte key
    long_names = [n for n in pool if len(n) >= 24]
    for n in long_names[:150]:
        for k in (1, 3, 5, 8):
            add((n + "Z" * k).encode("ascii"))
        add(n[:27].encode("ascii"))
        add(n[:28].encode("ascii"))
        add((n[:28] + "!!").encode("ascii"))
    for n in rng.sample(pool, min(400, len(pool))):
        stem = n.rsplit(".", 1)[0]
        add(stem.encode("ascii"))                       # script ids carry no extension: always a miss
        add((stem + ".").encode("ascii"))
        add(n.swapcase().encode("ascii"))
        add((" " + n).encode("ascii"))
        add((n + " ").encode("ascii"))
        add((n + "\0junk").encode("ascii"))
        add(("a\\b\\c\\" + n).encode("ascii"))
        add((n + "\\").encode("ascii"))
    # fixed cases
    for q in (b"", b"\\", b"\0", b"A", b"a\\", b"IMG2511", b"IMG2511.MZP", b"img2511.mzp", b"Img2511.Mzp",
              b"BLACK.mzp", b"black.MZP", b"WHITE.MZP", b"DUMMY.MRG", b"dummy.mrg", b"conf_parts_en.mrg",
              b"CONF_PARTS_EN.MRG", b"conf_parts_ja.mrg", b"title_parts_zt.mrg", b"mask_top.mzp", b"MASK_TOP.MZP",
              b"x" * 300 + b"\\img2511.mzp", b"x" * 100 + b"\\img2511.mzp", b"x" * 255 + b"\\" + b"img2511.mzp",
              b"x" * 256 + b"\\" + b"img2511.mzp", b"\x82\xa0.mzp", b"img\x82\xa0.mzp", b"\x82a" + b"b.mzp",
              b"x\x81bc", b"\x81\x82\x83\x84", b"IMG2511.MZP\x82", b"a" * 40, b"A" * 28, b"A" * 29, b"Q" * 28 + b"x"):
        add(q)
    return out


def _nam_row(q: bytes, avail: bool, nams: dict[str, Any], idx: tcat.ImageNameIndex) -> list[Any]:
    r = idx.find(q, avail)
    return [hexs(q), int(avail), -1 if r is None else r]


def classify_names(names: Sequence[str]) -> list[list[Any]]:
    rows = []
    for n in names:
        i = tcat.classify_name(n)
        rows.append([n, i.category.value, i.lang or None, i.base or None, i.char_code or None, i.char_id,
                     i.char_name or None, i.size_class or None, i.layer or None, i.detail or None])
    return rows


def script_queries(names: Sequence[str], table: Sequence[Any], rng: random.Random) -> list[tuple[bytes, int, int]]:
    """(script id, a9, a10) triples: every picture name lower-cased, every ``_HH_VV`` variant as its base with
    HH / VV, the pictures named by RUNTIME_REQUIREMENTS 1 item 2 and edge cases of the key and the suffix."""
    out: list[tuple[bytes, int, int]] = []
    seen: set[tuple[bytes, int, int]] = set()

    def add(q: bytes, aa: int = 0, bb: int = 0) -> None:
        t = (q, aa, bb)
        if t not in seen:
            seen.add(t)
            out.append(t)

    for r in table:
        add(names[r.index].lower().encode("ascii"))
    for r in table:
        stem = names[r.index]
        if len(stem) > 6 and stem[-3] == "_" and stem[-6] == "_" and stem[-5:-3].isdigit() and stem[-2:].isdigit():
            add(stem[:-6].lower().encode("ascii"), int(stem[-5:-3]), int(stem[-2:]))
    for r in rng.sample(list(table), min(300, len(table))):
        stem = names[r.index]
        add(stem.encode("ascii"))
        add(stem.title().encode("ascii"))
        add((stem + ".mzp").encode("ascii"))
        add((stem + ".").encode("ascii"))
        add((stem + " ").encode("ascii"))
        add(("x" + stem).encode("ascii"))
    for q, aa, bb in ((b"img2511", 0, 0), (b"WHITE", 0, 0), (b"boy_a_00_00_n01", 0, 0), (b"AKI_A_00_04_N01", 0, 0),
                      (b"img1696", 11, 11), (b"img1696", 9, 10), (b"img1696", 10, 9), (b"img1696", 0, 0),
                      (b"NOE_L_60_03_N01", 0, 0), (b"nosuchimage", 0, 0), (b"img2511.MZP", 0, 0),
                      (b"IMG2511.mzp", 0, 0), (b"img2511.", 0, 0), (b".", 0, 0), (b"", 0, 0), (b"x" * 40, 0, 0),
                      (b"img0342", 4, 4), (b"img0342", 0, 4), (b"img0001", 100, 0), (b"img0001", -1, 5),
                      (b"img0001", 5, -1), (b"img0001", 123, 456), (b"img0001", 0, 99), (b"img0001", 1, 0),
                      (b"img0001", 0, 1), (b"IMG0001", 200, 300), (b"img2277", 0, 20), (b"img1481", 20, 0),
                      (b"BLACK", 0, 0), (b"black", 1, 1), (b"\x82\xa0", 0, 0), (b"img\x82\xa0", 3, 3),
                      (b"IMG6000", 0, 0), (b"img3000", 0, 0), (b"img4193", 0, 0), (b"img5028", 0, 0),
                      (b"aki_a_30_01_n01", 0, 0), (b"img7000", 0, 0), (b"a" * 32 + b"b", 0, 0), (b"a" * 31 + b".b", 0, 0),
                      (b"ARU_M_03_35_N00", 0, 0), (b"IMG5007", 2, 2), (b"IMG5007", 0, 27), (b"IMG1637", 28, 28),
                      (b"Z20_M_61_00_A00", 10, 0)):
        add(q, aa, bb)
    return out


def _script_row(res: tcat.ScriptImageResolver, q: bytes, aa: int, bb: int) -> list[Any]:
    # ASCII and valid cp932 only (the Python resolver decodes bytes as cp932 for the suffix step)
    r = res.resolve(q, aa, bb)

    def pid(v: Optional[int]) -> Any:
        return None if v is None else v

    return [hexs(q), aa, bb, hexs(r.key.encode("latin-1")), r.index, r.file, r.overlay, pid(r.file_id),
            pid(r.overlay_id), pid(r.reduced_id)]


def _rules_json(ui_names: Sequence[str]) -> dict[str, Any]:
    movie_remap = []
    for mid in range(0, 141):
        for lang in range(-1, 7):
            try:
                idx: Any = tcat.movie_index_for_language(mid, lang)
                try:
                    f: Any = tcat.MOVIE_NAMES[idx] + ".mp4" if idx >= 0 else None
                except IndexError:
                    f = None
            except IndexError:
                idx, f = None, None
            movie_remap.append([mid, lang, idx, f])
    infos = [tcat.movie_info(i) for i in range(134)]
    localize = []
    for n in sorted(set(ui_names) | {"a.mrg", ".mrg", "x.mzp", "ABC.MRG", "abcd", "mrg", ""}):
        for lang in range(-1, 8):
            for sysl in (0, 1, 5, 10, 11, 12):
                localize.append([n, lang, sysl, tcat.localize_parts_name(n, lang, sysl)])
    child = [[c, n, tcat.ui_child_index(c, n)] for c in range(-3, 100, 7) for n in (0, 1, 5, 20, 82, 83, 90)]
    child += [[89, 83, 82], [82, 83, 82], [0, 83, 0], [5, 5, 4]]
    nam_cases = []
    for raw in (b"dir\\sub\\img0001.mzp", b"a" * 40, b"Caution_En.mzp", b"\x82ab.mzp", b"x\x81bc", b"q" * 300 + b"\\abc",
                b"ab\0cd", b"", b"\\", b"a\\", b"\\a", b"abc\\def\\ghi\\jkl.mzp", b"\xe0\xa0z", b"\x80z", b"\x7fz"):
        nam_cases.append([hexs(raw), hexs(tcat.nam_key(raw))])
    key_cases = []
    for raw in (b"img2511", b"boy_a_00_00_n01.mzp", b"x" * 40, b"", b".", b"a.b", b"\x82\xa0abc", b"abc\x82", b"a" * 31 + b"b",
                b"a" * 32 + b"b", b"a\0b"):
        key_cases.append([hexs(raw), tcat.script_image_key(raw)])
    stch_cases = [[n, a, b, tcat.stch_image_name(n, a, b)] for n, a, b in (
        ("img1696", 11, 11), ("img2277", 0, 20), ("img0342", 0, 0), ("img0001", 123, 7), ("img0001", -3, 4),
        ("img0001", 100, 100), ("img0001", 0, 100), ("img0001", 99, 99), ("img0001", 1, -1), ("", 5, 5))]
    return {
        "movie_names": list(tcat.MOVIE_NAMES), "prologue_ids": list(tcat.PROLOGUE_IDS),
        "movie_remap": movie_remap, "movie_info": infos,
        "lang_index_from_system": [[s, tcat.lang_index_from_system(s)] for s in range(-3, 41)],
        "localize_parts_name": localize, "ui_child_index": child,
        "nam_key": nam_cases, "script_image_key": key_cases, "stch_image_name": stch_cases,
        "char_codes": {k: list(v) for k, v in tcat.CHAR_CODES.items()}, "size_classes": tcat.SIZE_CLASSES,
        "parts_mrg": [list(p) for p in tcat.PARTS_MRG],
        "ui_parts_sets": {k: list(v) for k, v in tcat.UI_PARTS_SETS.items()},
        "image_search_order": list(tcat.IMAGE_SEARCH_ORDER), "archive_ids": {str(k): v for k, v in tcat.ARCHIVE_IDS.items()},
        "langs": list(tcat.LANGS), "nam_key_len": tcat.NAM_KEY_LEN, "nam_scan_limit": tcat.NAM_SCAN_LIMIT,
        "script_key_len": tcat.SCRIPT_KEY_LEN,
    }


def export_lookup(part: PartWriter, game: Path, log: Optional[Callable[[str], None]]) -> None:
    image = game / "image"
    path = game / "eboot.bin"
    part.add_input(path)
    nams: dict[str, Any] = {}
    names_by_archive: dict[str, list[str]] = {}
    for a in NAM_ARCHIVES:
        part.add_input(image / f"{a}.nam")
        nams[a] = load_nam(image / f"{a}.nam")
        names_by_archive[a] = nams[a].names
    searched = {a: nams[a] for a in SEARCHED}
    idx = tcat.ImageNameIndex(searched)
    with ceb.Eboot(path) as eb:
        names = eb.image_names()
        table = eb.image_table()
        ui_names = sorted({r.name for t in eb.ui_tables() for r in t.records})
    res = tcat.ScriptImageResolver(names, table, idx)
    rng = random.Random(SEED)

    # NAM search --------------------------------------------------------------------
    queries = nam_query_bytes(names_by_archive, rng)
    rows: list[list[Any]] = []
    for q in queries:
        rows.append(_nam_row(q, True, searched, idx))
    for q in queries[: len(queries) // 6]:
        rows.append(_nam_row(q, False, searched, idx))
    # the linear engine scan must agree with the dictionary version (sampled: 20,500 records per archive)
    sample = rng.sample(queries, 40) + [b"IMG2511.MZP", b"BLACK.mzp", b"nope.mzp", b"CAUTION_EN.MZP"]
    for q in sample:
        for avail in (True, False):
            if tcat.find_image(q, searched, avail) != idx.find(q, avail):
                raise ManifestError(f"find_image and ImageNameIndex disagree on {q!r}")
    cols = ("query_hex", "allpac1_open", "packed_id")
    part.write_bytes("nam_queries.tsv", tsv(cols, rows), "bytes",
                     _producer("export_lookup", {"file": "nam_queries.tsv", "seed": SEED},
                               ("tsuki.catalog.ImageNameIndex.find", "tsuki.catalog.find_image")),
                     {"encoding": "tsv", "columns": list(cols), "rows": len(rows)})
    hits = sum(1 for r in rows if r[2] != -1)
    part.count("nam_queries", len(rows))
    part.count("nam_hits", hits)
    part.check("nam_has_hits_and_misses", True, 0 < hits < len(rows), ARCHIVES_MD + " 2.3")
    part.check("nam_every_name_resolves", 0,
               sum(1 for a in SEARCHED for n in names_by_archive[a]
                   if idx.find(n.encode("ascii")) is None), ARCHIVES_MD + " 2.3")
    shadowed = [(a, r.name) for a in SEARCHED for r in nams[a].records
                if idx.find(r.name) != tcat.pack_image_id(a, r.index)]
    part.check("nam_shadowed_only_dummy", ["DUMMY.MRG"] * 3, [n for _, n in shadowed], ARCHIVES_MD + " 2.3")
    part.check("nam_img2511", 0x1086D, idx.find("IMG2511.MZP"), "tools/tests/test_catalog.py")

    # script image ids -----------------------------------------------------------------
    sq = script_queries(names, table, rng)
    srows = [_script_row(res, q, aa, bb) for q, aa, bb in sq]
    scols = ("query_hex", "a9", "a10", "key_hex", "index", "file", "overlay", "file_id", "overlay_id", "reduced_id")
    part.write_bytes("script_queries.tsv", tsv(scols, srows), "bytes",
                     _producer("export_lookup", {"file": "script_queries.tsv", "seed": SEED},
                               ("tsuki.catalog.ScriptImageResolver.resolve",)),
                     {"encoding": "tsv", "columns": list(scols), "rows": len(srows)})
    part.count("script_queries", len(srows))
    # RR item 2 [V]
    def rr(name: str, aa: int = 0, bb: int = 0) -> Any:
        return res.resolve(name, aa, bb)

    def packed(v: Optional[int]) -> Any:
        return None if v is None else tcat.unpack_image_id(v)

    r = rr("img2511")
    part.check("rr_img2511", [2518, ["allpac1", 2157]], [r.index, list(packed(r.file_id))], "RUNTIME_REQUIREMENTS 1 item 2")
    r = rr("WHITE")
    part.check("rr_white", [5, ["allpac0", 5]], [r.index, list(packed(r.file_id))], "RUNTIME_REQUIREMENTS 1 item 2")
    r = rr("boy_a_00_00_n01")
    part.check("rr_boy", [11680, ["allpac0", 1838], ["allpac0", 1840]],
               [r.index, list(packed(r.file_id)), list(packed(r.overlay_id))], "RUNTIME_REQUIREMENTS 1 item 2")
    r = rr("img1696", 11, 11)
    part.check("rr_img1696_11_11", [6218, ["allpac0", 1121]], [r.index, list(packed(r.file_id))],
               "RUNTIME_REQUIREMENTS 1 item 2")
    part.check("script_self_check", [], [n for k, n in enumerate(names[:len(table)]) if res.index_of(n) != k],
               ARCHIVES_MD + " 2.4")
    part.check("table_unresolved", ["ARU_M_03_35_N00_B.MZP", "ARU_S_03_35_N00_B.MZP", "NOE_L_60_03_N00_B.MZP"],
               sorted({n for rec in table for n in (rec.file, rec.overlay) if n and idx.find(n) is None}),
               IMAGES_MD)

    # name grammar ---------------------------------------------------------------------
    all_names: list[str] = []
    seen: set[str] = set()
    for a in NAM_ARCHIVES:
        for n in names_by_archive[a]:
            if n not in seen:
                seen.add(n)
                all_names.append(n)
    extra = ["BLACK.MZP", "IMG0042.MZP", "IMG0042_03_03.MZP", "IMG3864.MZP", "img2511", "AKI_A_00_00_N01.MZP",
             "AKI_A_00_00_N00_B.MZP", "SIE_A_09_01_B00_B.MZP", "ARU_A_18_05_C01_32_32.MZP", "Z20_S_63_04_N00.MZP",
             "RULE_07.MZP", "CAUTION1_ZT.MZP", "CAUTION2.MZP", "CAUTION_JA", "CAUTION.MZP", "CAUTIONX", "CONF_PARTS_EN.MRG",
             "GALLERY_PARTS_COMMON_KO.MRG", "GALLERY_PARTS_FC_TH.MRG", "GALLERY_PARTS_CG_TH.MRG", "BORDER_PARTS.MRG",
             "DUMMY.MRG", "CGMODE_TOP.MRG", "SELMAP_TOP.MZP", "whatever.bin", "", ".", "img0042_03.mzp", "xyz_B",
             "aki_a_00_00_n01", "Aki_A_00_00_N01.Mzp", "IMG12345", "IMG004", "IMG0042_3_3", "ZZZ_A_00_00_N00",
             "Y00_L_01_02_A03_B", "Z11_M_00_00_N00_10_10", "CCC_A_00_00_N00", "MODEPNL_PARTS.MRG",
             "SELECT_PARTS_ZC.MRG", "NOISE_PARTS_JA", "GALLERY_PARTS_ENDING_ZT.MRG"]
    for n in extra:
        if n not in seen:
            seen.add(n)
            all_names.append(n)
    crows = classify_names(all_names)
    ccols = ("name", "category", "lang", "base", "char_code", "char_id", "char_name", "size_class", "layer", "detail")
    # an empty name is written as '-' too: keep the original in a separate column of the C# test via the list order
    part.write_bytes("classify.tsv", tsv(ccols, [[("-" if r[0] == "" else r[0])] + r[1:] for r in crows]), "bytes",
                     _producer("export_lookup", {"file": "classify.tsv"}, ("tsuki.catalog.classify_name",)),
                     {"encoding": "tsv", "columns": list(ccols), "rows": len(crows), "empty_name_is": "-"})
    counts: dict[str, int] = {}
    for a in NAM_ARCHIVES:
        for n in names_by_archive[a]:
            c = tcat.classify_name(n).category.value
            counts[c] = counts.get(c, 0) + 1
    part.count("classify_names", len(crows))
    part.count("classify_counts", counts)
    part.check("classify_unknown", 0, counts.get("unknown", 0), ARCHIVES_MD + " 3")
    part.check("classify_gallery_images", 1024, counts.get("gallery_image", 0), ARCHIVES_MD + " 3")
    part.check("classify_sprite_bodies", 998, counts.get("sprite_body", 0), ARCHIVES_MD + " 3")
    part.check("classify_solid_colors", 14, counts.get("solid_color", 0), ARCHIVES_MD + " 3")

    rules = _rules_json(ui_names)
    part.write_json("rules.json", rules, _producer("export_lookup", {"file": "rules.json"}, ("tsuki.catalog",)))
    part.check("rules_movie_names_134", 134, len(rules["movie_names"]), ARCHIVES_MD + " 9")


# ---------------------------------------------------------------------------
# runner
# ---------------------------------------------------------------------------

_PART_FUNCS: dict[str, Callable[[PartWriter, Path, Optional[Callable[[str], None]]], None]] = {
    "eboot": export_eboot, "catalog": export_catalog, "lookup": export_lookup,
}
_PART_TEXT = {
    "eboot": "SELF/ELF structure, imports, ui_text (5 x 1,499)",
    "catalog": "image catalog (14,744 + 31), picture names, movie table, UI texture tables",
    "lookup": "NAM search, script image ids, name grammar, small rules",
}


def run(root: Path, game: Path, *, parts: Optional[Sequence[str]] = None, quick: bool = False,
        log: Optional[Callable[[str], None]] = None) -> dict[str, Any]:
    """Entry point used by ``oracle.run``.  All three parts take seconds; ``quick`` changes nothing."""
    sel = list(parts) if parts else list(PARTS)
    bad = [p for p in sel if p not in PARTS]
    if bad:
        raise ValueError(f"unknown parts {bad}; choose from {list(PARTS)}")
    gen = _producer("run", {"parts": sel, "game": Path(game).as_posix()}, ())
    area = AreaWriter(Path(root), AREA, gen, description=DESCRIPTION, game_dir=Path(game))
    for name in sel:
        t0 = time.perf_counter()
        pw = area.part(name, _PART_TEXT[name])
        if log is not None:
            log(f"[catalog] {name} ...")
        _PART_FUNCS[name](pw, Path(game), log)
        pw.finish()
        if log is not None:
            failed = sum(1 for c in pw.checks if not c.ok)
            log(f"[catalog] {name}: {len(pw.files)} files, {failed} failed checks of {len(pw.checks)} "
                f"({time.perf_counter() - t0:.1f} s)")
    return area.finish()


def main(argv: Optional[Sequence[str]] = None) -> int:
    """``python -m oracle.catalog [runner options]``: the runner with this area registered."""
    from . import run as runner
    runner.AREAS.setdefault(AREA, MODULE)
    return runner.main([AREA, *(sys.argv[1:] if argv is None else argv)])


if __name__ == "__main__":
    raise SystemExit(main())
