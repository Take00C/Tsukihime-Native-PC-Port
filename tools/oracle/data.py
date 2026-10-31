"""Area ``data``: the expectations of the data layer (``Tsuki.Data``) from the Python reference library.

The generators of the data stream (M0 package A, ROADMAP M1 acceptance 1, 3 and 8) live here since lead decision 2
(docs/status/decisions.md): they were ``tests/Tsuki.Tests.Data/Oracle/gen_golden_data.py`` and ``all_stream_hashes.py``, which
are now thin callers of this module.

Two ways to run it, same code:

``python -m oracle.data golden [--game DIR] [--out DIR] [--parts entries,standalone,build,containers,mzx,bench]``
    writes the layout the C# data tests read, by default ``samples/golden_data/`` (``GoldenData.Dir`` in
    ``tests/Tsuki.Tests.Data``): plain TSV or JSON files, LF line ends, deterministic (fixed seed).
    ``python -m oracle.data hashes --out FILE`` / ``--compare A B`` is the nightly of M1 acceptance 3: the SHA-256 of
    EVERY MZX0 stream (1,104,762), in the format of ``Tsuki.Cli decode-all --hashes``.
``python -m oracle.run data`` (or ``python -m oracle.data run``)
    the same files in the oracle layout, ``samples/golden/data/``, with an oracle manifest that
    ``python -m oracle.run data --verify`` checks (every file's size and SHA-1).

Files of the golden layout::

  archives.tsv             one row per triplet archive (HED/NAM/MRG facts)
  entries/<archive>.tsv    one row per HED entry (49,620 rows over the 12 archives)
  standalone_files.tsv     the 8 standalone mrgd00 files
  standalone_sections.tsv  every section of those files (891 rows)
  containers.tsv           every mrgd00 container: HED entries, nested ones, with a digest of the section table
  standalone_containers.tsv  the same for the standalone files
  nam_sections.tsv, nam_names.tsv   NAM32 tables inside allscr.mrg and se_nam.mrg
  scripttext.tsv           the 5 offset tables of script_text.mrg
  mzx_samples.tsv          stratified MZX0 streams with the oracle's output hash
  bench/streams.bin        the MZX0 streams of the throughput benchmark (shared by Python and C#)
  bench/python.json        the oracle's throughput on exactly those streams (timings: differ between runs)
  buildcheck.json          eboot size and SHA-256 and every FORMATS 2 count
  manifest.json            what was written and from which game files (golden layout only)

The game folder is only read.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import mmap
import multiprocessing as mp
import random
import struct
import sys
import time
from pathlib import Path
from typing import Any, Callable, Optional, Sequence

from tsuki import game_dir
from tsuki import mzx
from tsuki.mrg import MrgArchive
from tsuki.mzp import MzpArchive
from tsuki.nam import parse_nam32

from . import GOLDEN_ROOT
from .manifest import Producer, dump_json, sha1_file

__all__ = [
    "AREA", "MODULE", "PARTS", "DESCRIPTION", "LEGACY_DIR", "SEED", "generate", "run", "golden_main", "hashes_main",
    "compare_hashes", "hash_jobs", "hash_work", "main",
]

AREA = "data"
MODULE = "oracle.data"
PARTS: tuple[str, ...] = ("entries", "standalone", "build", "containers", "mzx", "bench")
DESCRIPTION = ("data-layer expectations of Tsuki.Data: archive tables, containers, MZX0 samples, build counts, "
               "benchmark pack (moved from tests/Tsuki.Tests.Data/Oracle)")
LEGACY_DIR = GOLDEN_ROOT.parent / "golden_data"     # what GoldenData.Dir reads today

SEED = 20261025
TRIPLETS = ["allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4",
            "parts", "bgm", "vse", "vag", "voice", "voice2"]
STANDALONE = ["allscr.mrg", "script_text.mrg", "ft_en.mrg", "ft_ja.mrg", "ft_ko.mrg",
              "ft_zc.mrg", "ft_zt.mrg", "se_nam.mrg"]
IMAGE_ARCHIVES = ["allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4", "parts"]
LANGS = ["ja", "en", "zc", "zt", "ko"]

# the sets of the all-streams hash run (M1 acceptance 3, nightly)
HASH_IMAGE = ["allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4", "parts"]
HASH_VOICE = ["voice", "voice2"]
HASH_STANDALONE = ["allscr.mrg", "ft_en.mrg", "ft_ja.mrg", "ft_ko.mrg", "ft_zc.mrg", "ft_zt.mrg"]

Log = Callable[[str], None]


# ---------------------------------------------------------------------------
# small helpers
# ---------------------------------------------------------------------------

def write_tsv(path: Path, header: list[str], rows) -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    n = 0
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\t".join(header) + "\n")
        for r in rows:
            f.write("\t".join(str(x) for x in r) + "\n")
            n += 1
    return n


def digest_sections(arc: MzpArchive) -> str:
    text = ";".join(f"{s.offset}:{s.size}" for s in arc.sections)
    return hashlib.sha1(text.encode("ascii")).hexdigest()


def container_row(source: str, entry: int, path: str, arc: MzpArchive) -> list:
    return [source, entry, path, arc.count, arc.data_start, arc.end, arc.packed_size, digest_sections(arc)]


CONTAINER_HEADER = ["source", "entry", "path", "count", "data_start", "end", "packed_size", "digest"]


def walk_containers(arc: MzpArchive, source: str, entry: int, path: str, rows: list) -> None:
    rows.append(container_row(source, entry, path, arc))
    for i in range(arc.count):
        if arc.is_nested(i):
            walk_containers(arc.child(i), source, entry, f"{path}/{i}" if path else str(i), rows)


def systematic(n: int, k: int, rng: random.Random) -> list[int]:
    """k indices spread evenly over range(n) with a random position inside every stratum."""
    if k >= n:
        return list(range(n))
    step = n / k
    return sorted({min(n - 1, int(step * i + rng.random() * step)) for i in range(k)})


# ---------------------------------------------------------------------------
# parts
# ---------------------------------------------------------------------------

def part_entries(out: Path, image: Path, log: Log) -> dict:
    arch_rows = []
    total = 0
    counts: dict[str, int] = {}
    for name in TRIPLETS:
        with MrgArchive(image / name) as a:
            hed_bytes = a.hed_path.stat().st_size
            nam = a.nam
            arch_rows.append([
                name, a.format.value, len(a), hed_bytes, a.hed.terminator.hex(), a.hed.trailing.hex(),
                int(a.hed.contiguous), ",".join(str(b) for b in a.hed.bank_starts),
                nam.record_size if nam else 0, len(nam) if nam else 0,
                int(nam.terminator) if nam else 0, nam.trailing.hex() if nam else "",
                (image / (name + ".nam")).stat().st_size if nam else 0,
                a.file_size, len(a.validate()),
            ])
            rows = ([e.index, e.name or "", e.offset, e.size, int(e.size_exact), e.sector_offset,
                     e.size_sectors, -1 if e.size_lo is None else e.size_lo, e.hed_raw.hex()]
                    for e in a.entries)
            n = write_tsv(out / "entries" / f"{name}.tsv",
                          ["index", "name", "offset", "size", "size_exact", "sector_offset",
                           "size_sectors", "size_lo", "hed_raw"], rows)
            counts[name] = n
            total += n
            log(f"  entries/{name}.tsv: {n} rows ({a.format.value}, banks {a.hed.bank_starts})")
    write_tsv(out / "archives.tsv",
              ["name", "format", "entries", "hed_bytes", "terminator_hex", "trailing_hex", "contiguous",
               "bank_starts", "nam_record_size", "nam_records", "nam_terminator", "nam_trailing_hex",
               "nam_bytes", "mrg_bytes", "validate_issues"], arch_rows)
    log(f"  archives.tsv: {len(arch_rows)} rows; {total} entries in total")
    return {"entries_total": total, "per_archive": counts}


def part_standalone(out: Path, image: Path, log: Log) -> dict:
    import mmap
    file_rows, sec_rows, cont_rows, nam_rows, name_rows = [], [], [], [], []
    for fname in STANDALONE:
        path = image / fname
        with open(path, "rb") as f:
            mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
            try:
                arc = MzpArchive(mm)
                file_rows.append([fname, path.stat().st_size, arc.count, arc.data_start, arc.end,
                                  arc.packed_size, len(arc.validate())])
                for s in arc.sections:
                    sec_rows.append([fname, s.index, s.sector_offset, s.byte_offset, s.size_sectors,
                                     s.size_lo, s.offset, s.size, int(arc.is_nested(s.index)),
                                     arc.head(s.index, 8).hex()])
                walk_containers(arc, fname, -1, "", cont_rows)
                if fname in ("allscr.mrg", "se_nam.mrg"):
                    secs = [0] if fname == "allscr.mrg" else range(arc.count)
                    for k in secs:
                        t = parse_nam32(arc.read(k))
                        nam_rows.append([fname, k, t.record_size, len(t), int(t.terminator), t.trailing.hex()])
                        for r in t.records:
                            name_rows.append([fname, k, r.index, r.name, r.tag])
            finally:
                mm.close()
    write_tsv(out / "standalone_files.tsv", ["file", "bytes", "count", "data_start", "end", "packed_size",
                                             "validate_issues"], file_rows)
    n_sec = write_tsv(out / "standalone_sections.tsv",
                      ["file", "section", "sector_offset", "byte_offset", "size_sectors", "size_lo",
                       "offset", "size", "nested", "head8"], sec_rows)
    write_tsv(out / "standalone_containers.tsv", CONTAINER_HEADER, cont_rows)
    write_tsv(out / "nam_sections.tsv", ["file", "section", "record_size", "count", "terminator",
                                         "trailing_hex"], nam_rows)
    write_tsv(out / "nam_names.tsv", ["file", "section", "index", "name", "tag"], name_rows)
    # script_text.mrg offset tables
    st_rows = []
    with open(image / "script_text.mrg", "rb") as f:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            arc = MzpArchive(mm)
            for k, lang in enumerate(LANGS):
                tab = arc.read(2 * k)
                n = len(tab) // 4
                vals = struct.unpack(f">{n}I", tab)
                end = vals.index(0xFFFFFFFF)
                st_rows.append([lang, n, end - 2, n - end, arc.sections[2 * k + 1].size, len(tab)])
        finally:
            mm.close()
    write_tsv(out / "scripttext.tsv", ["lang", "table_entries", "count", "end_marks", "blob_bytes",
                                       "table_bytes"], st_rows)
    log(f"  standalone: {len(file_rows)} files, {n_sec} sections, {len(cont_rows)} containers, "
        f"{len(nam_rows)} NAM tables, {len(name_rows)} names")
    return {"standalone_sections": n_sec, "files": len(file_rows)}


def part_containers(out: Path, image: Path, log: Log) -> dict:
    rows: list = []
    t0 = time.perf_counter()
    for name in IMAGE_ARCHIVES:
        with MrgArchive(image / name) as a:
            before = len(rows)
            for e in a.entries:
                arc = MzpArchive(a.mm, e.offset, e.size)
                walk_containers(arc, name, e.index, "", rows)
            log(f"  {name}: {len(rows) - before} containers ({time.perf_counter() - t0:.0f} s)")
    n = write_tsv(out / "containers.tsv", CONTAINER_HEADER, rows)
    sections = sum(int(r[3]) for r in rows)
    log(f"  containers.tsv: {n} containers, {sections} section records")
    return {"containers": n, "section_records": sections}


def eboot_info(game: Path) -> dict:
    h = hashlib.sha256()
    size = 0
    with open(game / "eboot.bin", "rb") as f:
        while True:
            b = f.read(1 << 20)
            if not b:
                break
            h.update(b)
            size += len(b)
    return {"size": size, "sha256": h.hexdigest()}


def part_build(out: Path, game: Path, log: Log) -> dict:
    image = game / "image"
    info = {"eboot": eboot_info(game)}
    triplets = {}
    for name in TRIPLETS:
        with MrgArchive(image / name) as a:
            triplets[name] = {"entries": len(a), "format": a.format.value, "bank_starts": a.hed.bank_starts,
                              "mrg_bytes": a.file_size, "nam": len(a.nam) if a.nam else None}
    info["triplets"] = triplets
    info["triplet_total"] = sum(t["entries"] for t in triplets.values())
    import mmap
    stand = {}
    for fname in STANDALONE:
        with open(image / fname, "rb") as f:
            mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
            try:
                stand[fname] = MzpArchive(mm).count
            finally:
                mm.close()
    info["standalone"] = stand
    info["standalone_total"] = sum(stand.values())
    with open(image / "allscr.mrg", "rb") as f:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            arc = MzpArchive(mm)
            info["scripts"] = len(parse_nam32(arc.read(0)))
        finally:
            mm.close()
    with open(image / "script_text.mrg", "rb") as f:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            arc = MzpArchive(mm)
            lines = {}
            for k, lang in enumerate(LANGS):
                vals = struct.unpack(f">{arc.sections[2 * k].size // 4}I", arc.read(2 * k))
                lines[lang] = vals.index(0xFFFFFFFF) - 2
            info["text_lines"] = lines
        finally:
            mm.close()
    info["voices"] = triplets["voice"]["entries"]
    info["voice2"] = triplets["voice2"]["entries"]
    movies = sorted(p.name for p in (image / "movie").iterdir() if p.is_file())
    info["movie_files"] = len(movies)
    info["movie_mp4"] = sum(1 for m in movies if m.lower().endswith(".mp4"))
    (out / "buildcheck.json").write_text(json.dumps(info, indent=2), encoding="utf-8", newline="\n")
    log(f"  buildcheck.json: eboot {info['eboot']['size']} {info['eboot']['sha256'][:12]}.., "
        f"{info['triplet_total']} triplet entries, {info['standalone_total']} standalone sections, "
        f"{info['scripts']} scripts, text {info['text_lines']}, voices {info['voices']}, movies {info['movie_mp4']}")
    return info


# ---------------------------------------------------------------------------
# MZX samples
# ---------------------------------------------------------------------------

def mzx_paths(arc: MzpArchive, prefix: tuple[int, ...] = ()) -> list[tuple[int, ...]]:
    """Section paths of every MZX0 stream below ``arc`` (recursing into nested containers)."""
    res = []
    for i in range(arc.count):
        if arc.is_nested(i):
            res.extend(mzx_paths(arc.child(i), prefix + (i,)))
        elif arc.head(i, 4) == b"MZX0":
            res.append(prefix + (i,))
    return res


def resolve(arc: MzpArchive, path: tuple[int, ...]) -> bytes:
    for i in path[:-1]:
        arc = arc.child(i)
    return arc.read(path[-1])


class SampleWriter:
    def __init__(self) -> None:
        self.rows: list[list] = []
        self.seen: set[tuple] = set()

    def add_section(self, source: str, entry: int, path: tuple[int, ...], kind: str, data: bytes,
                    invert: bool) -> bool:
        key = (source, entry, path)
        if key in self.seen:
            return False
        self.seen.add(key)
        res = mzx.decompress_ex(data, invert)
        assert res.consumed == len(data), (source, entry, path, res.consumed, len(data))
        self.rows.append([len(self.rows), source, entry, "/".join(map(str, path)), kind, int(invert),
                          len(res.data), res.consumed, res.commands, res.overshoot,
                          hashlib.sha256(res.data).hexdigest()])
        return True

    def add_voice(self, a: MrgArchive, source: str, index: int) -> bool:
        key = (source, index, ("lv",))
        if key in self.seen:
            return False
        self.seen.add(key)
        entry = a.read_sectors(index)
        d = mzx.decompress_lv(entry)
        assert d.compressed and d.mzx is not None
        self.rows.append([len(self.rows), source, index, "lv", "voice", 0, len(d.data), d.consumed,
                          d.mzx.commands, d.mzx.overshoot, hashlib.sha256(d.data).hexdigest()])
        return True


QUOTAS = {"allpac0": 300, "allpac1": 300, "allpac1_2": 250, "allpac2": 250, "allpac3": 200,
          "allpac4": 100, "parts": 100, "voice": 400}


def part_mzx(out: Path, image: Path, log: Log, bench: Optional[list] = None) -> dict:
    rng = random.Random(SEED)
    w = SampleWriter()
    # 1. image archives: systematic over entries, then random streams inside the chosen entries
    for name in IMAGE_ARCHIVES:
        quota = QUOTAS[name]
        with MrgArchive(image / name) as a:
            n = len(a)
            chosen = systematic(n, min(n, quota), rng)
            for forced in (0, n - 1):
                if forced not in chosen:
                    chosen.append(forced)
            per_entry = max(1, math.ceil(quota / len(chosen)))
            taken = 0
            for ei in sorted(chosen):
                e = a.entries[ei]
                arc = MzpArchive(a.mm, e.offset, e.size)
                paths = mzx_paths(arc)
                if not paths:
                    continue
                picks = {paths[0], paths[-1]} if ei in (0, n - 1) else set()
                picks.update(rng.sample(paths, min(len(paths), per_entry)))
                for p in sorted(picks):
                    if w.add_section(name, ei, p, "tile", resolve(arc, p), False):
                        taken += 1
            log(f"  {name}: {taken} streams from {len(chosen)} entries")
    # the two documented overrun examples (containers.md 5.2)
    for name, ei, sec in (("allpac4", 0, 1), ("allpac0", 13, 4)):
        with MrgArchive(image / name) as a:
            e = a.entries[ei]
            arc = MzpArchive(a.mm, e.offset, e.size)
            if w.add_section(name, ei, (sec,), "tile", arc.read(sec), False):
                log(f"  {name} #{ei} s{sec}: documented overrun example added")
    # 2. voice
    with MrgArchive(image / "voice") as a:
        n = len(a)
        idx = set(systematic(n, QUOTAS["voice"], rng))
        idx.update([0, 1, 2, 7850, 3028, 4044, 13042, 13595, n - 1])
        for i in sorted(idx):
            w.add_voice(a, "voice", i)
    with MrgArchive(image / "voice2") as a:
        for i in range(len(a)):
            w.add_voice(a, "voice2", i)
    log(f"  voice: {sum(1 for r in w.rows if r[1] == 'voice')} streams, voice2: "
        f"{sum(1 for r in w.rows if r[1] == 'voice2')}")
    # 3. standalone files
    import mmap
    for fname in STANDALONE:
        if fname in ("script_text.mrg", "se_nam.mrg"):
            continue
        with open(image / fname, "rb") as f:
            mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
            try:
                arc = MzpArchive(mm)
                before = len(w.rows)
                if fname == "allscr.mrg":
                    for k in range(3, arc.count):
                        w.add_section(fname, -1, (k,), "script", arc.read(k), True)
                    w.add_section(fname, -1, (2, 1), "tile", arc.child(2).read(1), False)
                else:
                    pages = systematic(arc.count, 40, rng)
                    for k in pages:
                        child = arc.child(k)
                        for p in mzx_paths(child):
                            w.add_section(fname, -1, (k,) + p, "font", child.read(p[-1]), False)
                log(f"  {fname}: {len(w.rows) - before} streams")
            finally:
                mm.close()
    n = write_tsv(out / "mzx_samples.tsv",
                  ["id", "source", "entry", "path", "kind", "invert", "declared", "consumed", "commands",
                   "overshoot", "sha256"], w.rows)
    by_src: dict[str, int] = {}
    for r in w.rows:
        by_src[r[1]] = by_src.get(r[1], 0) + 1
    odd = sum(1 for r in w.rows if r[6] % 2)
    log(f"  mzx_samples.tsv: {n} streams; odd sizes {odd}; max overshoot {max(r[9] for r in w.rows)}; "
        f"declared bytes {sum(r[6] for r in w.rows):,}")
    return {"mzx_samples": n, "by_source": by_src}


# ---------------------------------------------------------------------------
# benchmark pack
# ---------------------------------------------------------------------------

def part_bench(out: Path, image: Path, log: Log) -> dict:
    rng = random.Random(SEED + 1)
    recs: list[tuple[int, int, int, bytes]] = []   # kind, invert, declared, bytes
    # tiles: 40 entries from each of the five big image archives, 1 random tile each
    for name in ("allpac0", "allpac1", "allpac1_2", "allpac2", "allpac4"):
        with MrgArchive(image / name) as a:
            for ei in systematic(len(a), 36, rng):
                e = a.entries[ei]
                arc = MzpArchive(a.mm, e.offset, e.size)
                paths = mzx_paths(arc)
                if paths:
                    data = resolve(arc, rng.choice(paths))
                    recs.append((0, 0, mzx.mzx_size(data), data))
    # voices: 60 body streams (what the oracle's decompress_lv decodes)
    with MrgArchive(image / "voice") as a:
        for i in systematic(len(a), 60, rng):
            entry = a.read_sectors(i)
            hdr = mzx.parse_lv(entry)
            body = entry[hdr.body_offset:]
            recs.append((1, 0, mzx.mzx_size(body), body))
    # scripts: all 549
    import mmap
    with open(image / "allscr.mrg", "rb") as f:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            arc = MzpArchive(mm)
            for k in range(3, arc.count):
                data = arc.read(k)
                recs.append((2, 1, mzx.mzx_size(data), data))
        finally:
            mm.close()
    # font pages: 20 from ft_ja
    with open(image / "ft_ja.mrg", "rb") as f:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            arc = MzpArchive(mm)
            for k in systematic(arc.count, 20, rng):
                child = arc.child(k)
                for p in mzx_paths(child):
                    data = child.read(p[-1])
                    recs.append((3, 0, mzx.mzx_size(data), data))
        finally:
            mm.close()
    bdir = out / "bench"
    bdir.mkdir(parents=True, exist_ok=True)
    with open(bdir / "streams.bin", "wb") as f:
        f.write(b"MZXB" + struct.pack("<I", len(recs)))
        for kind, inv, declared, data in recs:
            f.write(struct.pack("<BBHII", kind, inv, 0, declared, len(data)))
            f.write(data)
    names = {0: "tile", 1: "voice", 2: "script", 3: "font"}
    result: dict = {"streams": len(recs), "classes": {}}
    for kind in (0, 1, 2, 3):
        sub = [r for r in recs if r[0] == kind]
        out_bytes = sum(r[2] for r in sub)
        in_bytes = sum(len(r[3]) for r in sub)
        entry = {"streams": len(sub), "compressed_bytes": in_bytes, "decompressed_bytes": out_bytes}
        for label, use_numba, subset in (("numba", True, sub), ("python", False, sub[:12] if kind == 0 else sub[:6])):
            if not subset or (use_numba and not mzx.HAVE_NUMBA):
                continue
            ob = sum(r[2] for r in subset)
            # warm-up (numba compile / caches), then best of 3
            for r in subset[:2]:
                mzx.decompress_ex(r[3], bool(r[1]), use_numba=use_numba)
            best = None
            for _ in range(3):
                t = time.perf_counter()
                for r in subset:
                    mzx.decompress_ex(r[3], bool(r[1]), use_numba=use_numba)
                dt = time.perf_counter() - t
                best = dt if best is None or dt < best else best
            entry[label] = {"streams": len(subset), "decompressed_bytes": ob, "seconds": best,
                            "mb_per_s": ob / best / 1e6}
        result["classes"][names[kind]] = entry
    total_numba_out = sum(c["numba"]["decompressed_bytes"] for c in result["classes"].values() if "numba" in c)
    total_numba_s = sum(c["numba"]["seconds"] for c in result["classes"].values() if "numba" in c)
    result["numba_total_mb_per_s"] = total_numba_out / total_numba_s / 1e6 if total_numba_s else None
    result["have_numba"] = bool(mzx.HAVE_NUMBA)
    (bdir / "python.json").write_text(json.dumps(result, indent=2), encoding="utf-8", newline="\n")
    log(f"  bench/streams.bin: {len(recs)} streams; python numba {result['numba_total_mb_per_s']:.0f} MB/s; "
        + ", ".join(f"{k}: numba {v.get('numba', {}).get('mb_per_s', 0):.0f} / python {v.get('python', {}).get('mb_per_s', 0):.0f} MB/s"
                    for k, v in result["classes"].items()))
    return result


# ---------------------------------------------------------------------------
# the nightly: SHA-256 of the decoded output of EVERY MZX0 stream
# ---------------------------------------------------------------------------
#
# One line per stream: ``<label> TAB <sha256>``. Labels: ``allpac0#12/3/7`` (HED entry, section path), ``voice#7850/lv``,
# ``allscr.mrg#300`` (top-level section of a standalone file), ``ft_en.mrg#4/1``. Scripts (allscr sections >= 3) are decoded
# inverted, everything else plain.

def walk_hash(arc: MzpArchive, label: str, path: str, out: list, invert: bool = False) -> None:
    for i in range(arc.count):
        p = f"{path}/{i}" if path else str(i)
        if arc.is_nested(i):
            walk_hash(arc.child(i), label, p, out, invert)
        elif arc.head(i, 4) == b"MZX0":
            data = mzx.decompress(arc.read(i), invert)
            out.append(f"{label}/{p}\t{hashlib.sha256(data).hexdigest()}")


def hash_work(job: tuple) -> list[str]:
    kind, game, name, lo, hi = job
    image = Path(game) / "image"
    out: list[str] = []
    if kind == "image":
        with MrgArchive(image / name) as a:
            for e in range(lo, hi):
                ent = a.entries[e]
                walk_hash(MzpArchive(a.mm, ent.offset, ent.size), f"{name}#{e}", "", out)
    elif kind == "voice":
        with MrgArchive(image / name) as a:
            for e in range(lo, hi):
                d = mzx.decompress_lv(a.read_sectors(e))
                out.append(f"{name}#{e}/lv\t{hashlib.sha256(d.data).hexdigest()}")
    else:
        with open(image / name, "rb") as f:
            mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
            try:
                arc = MzpArchive(mm)
                for s in range(lo, hi):
                    if arc.is_nested(s):
                        walk_hash(arc.child(s), f"{name}#{s}", "", out)
                    elif arc.head(s, 4) == b"MZX0":
                        data = mzx.decompress(arc.read(s), name == "allscr.mrg" and s >= 3)
                        out.append(f"{name}#{s}\t{hashlib.sha256(data).hexdigest()}")
            finally:
                mm.close()
    return out


def hash_jobs(game: Path, only: set[str] | None) -> list[tuple]:
    image = game / "image"
    result: list[tuple] = []
    for name in HASH_IMAGE + HASH_VOICE:
        if only and name not in only:
            continue
        with MrgArchive(image / name) as a:
            n = len(a)
        step = max(1, n // 64)
        for lo in range(0, n, step):
            result.append(("image" if name in HASH_IMAGE else "voice", str(game), name, lo, min(n, lo + step)))
    for name in HASH_STANDALONE:
        if only and name not in only and name.removesuffix(".mrg") not in only:
            continue
        with open(image / name, "rb") as f:
            mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
            try:
                n = MzpArchive(mm).count
            finally:
                mm.close()
        result.append(("standalone", str(game), name, 0, n))
    return result


def compare_hashes(a_path: str, b_path: str) -> int:
    def load(p: str) -> dict[str, str]:
        d: dict[str, str] = {}
        with open(p, encoding="utf-8") as f:
            for line in f:
                k, _, v = line.rstrip("\n").partition("\t")
                if k in d:
                    print(f"duplicate label {k} in {p}")
                d[k] = v
        return d

    a, b = load(a_path), load(b_path)
    only_a = sorted(set(a) - set(b))
    only_b = sorted(set(b) - set(a))
    diff = sorted(k for k in set(a) & set(b) if a[k] != b[k])
    print(f"{a_path}: {len(a):,} streams; {b_path}: {len(b):,} streams; same label & hash: {len(a) - len(only_a) - len(diff):,}")
    print(f"only in first: {len(only_a)} {only_a[:3]}; only in second: {len(only_b)} {only_b[:3]}; different hash: {len(diff)} {diff[:3]}")
    return 0 if not (only_a or only_b or diff) and a else 1




# ---------------------------------------------------------------------------
# generate: the golden layout
# ---------------------------------------------------------------------------

def generate(out: Path, game: Path, parts: Sequence[str] = PARTS, log: Log = print) -> dict:
    """Write ``parts`` of the golden layout to ``out`` (other parts already there are kept) and update ``manifest.json``."""
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    image = Path(game) / "image"
    manifest: dict = {"seed": SEED, "numba": bool(mzx.HAVE_NUMBA), "parts": {}}
    t0 = time.perf_counter()
    for p in parts:
        t = time.perf_counter()
        log(f"[{p}]")
        if p == "entries":
            manifest["parts"][p] = part_entries(out, image, log)
        elif p == "standalone":
            manifest["parts"][p] = part_standalone(out, image, log)
        elif p == "containers":
            manifest["parts"][p] = part_containers(out, image, log)
        elif p == "mzx":
            manifest["parts"][p] = part_mzx(out, image, log)
        elif p == "bench":
            manifest["parts"][p] = part_bench(out, image, log)
        elif p == "build":
            manifest["parts"][p] = part_build(out, Path(game), log)
        else:
            raise ValueError(f"unknown part {p!r}; choose from {list(PARTS)}")
        log(f"[{p}] {time.perf_counter() - t:.1f} s")
    mpath = out / "manifest.json"
    old = json.loads(mpath.read_text(encoding="utf-8")) if mpath.exists() else {"parts": {}}
    old["seed"] = SEED
    old["numba"] = bool(mzx.HAVE_NUMBA)
    old.setdefault("parts", {}).update(manifest["parts"])
    mpath.write_text(json.dumps(old, indent=2), encoding="utf-8", newline="\n")
    log(f"done in {time.perf_counter() - t0:.1f} s -> {out}")
    return old


def golden_main(argv: Optional[Sequence[str]] = None) -> int:
    """The command line of the old ``gen_golden_data.py`` (the thin caller in tests/Tsuki.Tests.Data/Oracle runs it)."""
    ap = argparse.ArgumentParser(prog="python -m oracle.data golden", description="data-layer expectations for Tsuki.Data")
    ap.add_argument("--game", help="CUSA47999 folder (default: TSUKI_GAME_DIR or the tsuki default)")
    ap.add_argument("--out", default=str(LEGACY_DIR))
    ap.add_argument("--parts", default=",".join(PARTS))
    a = ap.parse_args(argv)
    game = Path(a.game) if a.game else game_dir()
    if game is None or not (game / "image").is_dir():
        print("game folder not found (pass --game or set TSUKI_GAME_DIR)", file=sys.stderr)
        return 2
    parts = [p.strip() for p in a.parts.split(",") if p.strip()]
    bad = [p for p in parts if p not in PARTS]
    if bad:
        print(f"unknown part {bad[0]!r}", file=sys.stderr)
        return 2
    generate(Path(a.out), game, parts, lambda s: print(s, flush=True))
    return 0


# ---------------------------------------------------------------------------
# hashes: the command line of the old all_stream_hashes.py
# ---------------------------------------------------------------------------

def hashes_main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m oracle.data hashes",
                                 description="SHA-256 of the decoded output of EVERY MZX0 stream")
    ap.add_argument("--game")
    ap.add_argument("--out")
    ap.add_argument("--procs", type=int, default=max(1, (mp.cpu_count() or 4) - 2))
    ap.add_argument("--only")
    ap.add_argument("--compare", nargs=2, metavar=("A", "B"))
    a = ap.parse_args(argv)
    if a.compare:
        return compare_hashes(*a.compare)
    game = Path(a.game) if a.game else game_dir()
    if game is None or not a.out:
        print("need a game folder and --out", file=sys.stderr)
        return 2
    only = set(a.only.split(",")) if a.only else None
    t0 = time.perf_counter()
    all_jobs = hash_jobs(game, only)
    total = 0
    with mp.Pool(a.procs) as pool, open(a.out, "w", encoding="utf-8", newline="\n") as f:
        for lines in pool.imap_unordered(hash_work, all_jobs):
            for line in lines:
                f.write(line + "\n")
            total += len(lines)
    print(f"{total:,} streams hashed in {time.perf_counter() - t0:.1f} s with {a.procs} processes -> {a.out}")
    return 0


# ---------------------------------------------------------------------------
# the oracle area: samples/golden/data/
# ---------------------------------------------------------------------------

_PART_OF: dict[str, str] = {
    "archives.tsv": "entries", "standalone_files.tsv": "standalone", "standalone_sections.tsv": "standalone",
    "standalone_containers.tsv": "standalone", "nam_sections.tsv": "standalone", "nam_names.tsv": "standalone",
    "scripttext.tsv": "standalone", "containers.tsv": "containers", "buildcheck.json": "build",
    "mzx_samples.tsv": "mzx", "bench/streams.bin": "bench", "bench/python.json": "bench",
}


def _part_of(rel: str) -> Optional[str]:
    if rel.startswith("entries/"):
        return "entries"
    return _PART_OF.get(rel)


def run(root: Path, game: Path, *, parts: Optional[Sequence[str]] = None, quick: bool = False,
        log: Optional[Callable[[str], None]] = None) -> dict[str, Any]:
    """Entry point used by ``oracle.run``: the golden layout in ``root / 'data'`` and an oracle manifest listing every file.
    ``quick`` leaves out the benchmark pack, which is the slow part (its ``bench/python.json`` holds timings that differ between
    runs, so it is listed with the hash of the run that wrote it)."""
    from . import SCHEMA
    sel = list(parts) if parts else [p for p in PARTS if not (quick and p == "bench")]
    say = log if log is not None else (lambda s: None)
    area_dir = Path(root) / AREA
    legacy = generate(area_dir, Path(game), sel, say)
    files = []
    for p in sorted(area_dir.rglob("*")):
        if not p.is_file() or p.name == "manifest.json":
            continue
        rel = p.relative_to(area_dir).as_posix()
        part = _part_of(rel)
        if part is None:
            raise ValueError(f"{rel}: not a file of any part")
        producer = Producer(MODULE, "generate", {"part": part, "seed": SEED}, ("tsuki.mrg", "tsuki.mzp", "tsuki.mzx", "tsuki.nam"))
        files.append({
            "path": rel, "part": part, "size": p.stat().st_size, "sha1": sha1_file(p),
            "format": "json" if p.suffix == ".json" else "bytes",
            "produced_by": producer.to_json(),
            "meta": {"encoding": "tsv" if p.suffix == ".tsv" else p.suffix.lstrip(".")},
        })
    manifest = {
        "schema": SCHEMA, "area": AREA, "description": DESCRIPTION,
        "generator": Producer(MODULE, "run", {"parts": sel, "game": Path(game).as_posix()}).to_json(),
        "game": {"dir": Path(game).as_posix()},
        "parts": {},    # no per-part manifests: the files carry their part, and verify_area lists every file
        "files": files, "inputs": [], "input_entries": [], "counts": {"files": len(files), "parts": {p: sum(1 for f in files if f["part"] == p) for p in PARTS},
                   "legacy_manifest": legacy},
        "checks": [], "findings": [],
    }
    (area_dir / "manifest.json").write_bytes(dump_json(manifest))
    return manifest


def main(argv: Optional[Sequence[str]] = None) -> int:
    """``python -m oracle.data <golden|hashes|run> ...``"""
    args = list(sys.argv[1:] if argv is None else argv)
    cmd = args.pop(0) if args else ""
    if cmd == "golden":
        return golden_main(args)
    if cmd == "hashes":
        return hashes_main(args)
    if cmd == "run":
        from . import run as runner
        runner.AREAS.setdefault(AREA, MODULE)
        return runner.main([AREA, *args])
    print("usage: python -m oracle.data golden|hashes|run [options]", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
