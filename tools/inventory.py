"""Walk every container of the PS4 release and write an inventory.

Usage (from the repository's tools/ folder):
    ../.venv/Scripts/python.exe inventory.py [--game DIR] [--out DIR] [--deep] [--only NAME ...]

Writes <out>/<archive>.jsonl (one JSON object per entry / section) plus
<out>/summary.md and <out>/summary.json.  Default out dir:
samples/inventory under the repository root (git-ignored).

--deep additionally decompresses EVERY MZX stream (image tiles, scripts,
voices) and checks that each stream decodes to its declared size and consumes
exactly its container section (multi-threaded, needs numba to be quick).

JSONL record fields (triplet archives):
  archive, index, name, offset, size, size_exact, sector_offset, size_sectors,
  size_lo, hed_raw, head (first 16 raw bytes, hex), format (sniff label),
  [mzp]   {count, data_start, end, packed_size, kind, nested, issues,
           sections: [[offset, size, label, mzx_size|null], ...],
           children: {section_index: <mzp summary>}}       (nested MZPs)
  [image] {w, h, tile_w, tile_h, tiles_x, tiles_y, type}    (image MZPs)
  decoded_head / decoded_format / decoded_size              (first MZX payload)
  [riff] [vag] [lv] format specific details, checks, errors, [deep]
  voice: lv {header, n, payload}, mzx_consumed, exact_size (= 4 + n + consumed),
         checks incl. hed4_sectors_eq_floor_plus_1
  deep / mzx_deep: {streams, ok, bad, in_bytes, out_bytes, overshoot_sum,
         overshoot_max, overshoot_hist {bytes past declared size: streams}, failures}
Standalone mrgd00 files get one record per top-level section (archive, index,
name, role, offset, size, head, format, ...); script_text offset tables also
carry ffffffff_at, last_values and pad_after.
"""
from __future__ import annotations

import argparse
import collections
import json
import mmap
import os
import struct
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Any, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))

from tsuki import game_dir  # noqa: E402
from tsuki.mrg import MrgArchive  # noqa: E402
from tsuki.mzp import MzpArchive, MZP_MAGIC, pad_after  # noqa: E402
from tsuki.mzx import (MZX_MAGIC, MzxError, decompress_ex, decompress_lv, mzx_size,  # noqa: E402
                       HAVE_NUMBA)
from tsuki.nam import parse_nam32  # noqa: E402
from tsuki.sniff import (LV_HEADER, classify_mzp, image_header, is_be32_offset_table,  # noqa: E402
                         riff_info, sniff, vag_info)

TRIPLETS = ["allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4",
            "bgm", "vse", "vag", "parts", "voice", "voice2"]
STANDALONE = ["allscr", "script_text", "ft_en", "ft_ja", "ft_ko", "ft_zc", "ft_zt", "se_nam"]
DEFAULT_OUT = Path(__file__).resolve().parents[1] / "samples" / "inventory"
MAX_NEST = 4


def hx(b: bytes) -> str:
    return bytes(b).hex()


# ---------------------------------------------------------------------------
# MZP helpers
# ---------------------------------------------------------------------------

def section_label(arc: MzpArchive, i: int) -> str:
    head = arc.head(i, 8)
    if head[:4] == MZX_MAGIC:
        return "mzx"
    if head[:6] == MZP_MAGIC:
        return "mzp"
    s = arc.sections[i]
    if (head[:4] == b"\0\0\0\0" and s.size % 4 == 0 and 16 <= s.size <= 1 << 20
            and is_be32_offset_table(arc.read(i))):
        return "be32-offsets"
    return sniff(arc.head(i, 512), s.size)


def mzp_summary(arc: MzpArchive, depth: int = 0) -> dict[str, Any]:
    secs: list[list[Any]] = []
    children: dict[str, Any] = {}
    nested = 0
    for s in arc.sections:
        lab = section_label(arc, s.index)
        msize: Optional[int] = None
        if lab == "mzx":
            msize = mzx_size(arc.head(s.index, 8))
        secs.append([s.offset, s.size, lab, msize])
        if lab == "mzp":
            nested += 1
            if depth < MAX_NEST:
                children[str(s.index)] = mzp_summary(arc.child(s.index), depth + 1)
    out: dict[str, Any] = {
        "count": arc.count, "data_start": arc.data_start, "end": arc.end,
        "packed_size": arc.packed_size, "kind": classify_mzp(arc), "nested": nested,
        "issues": arc.validate(),
        "sections": secs,
    }
    if children:
        out["children"] = children
    hdr = image_header(arc.head(0, 16)) if arc.count else None
    if hdr is not None and out["kind"].startswith("image"):
        out["image"] = hdr
    return out


def first_mzx_payload(arc: MzpArchive, invert: bool = False) -> Optional[tuple[int, bytes, int, str]]:
    """(section index, first 256 decompressed bytes, declared size, label) of
    the first MZX section, searching nested containers depth-first.  Inside
    image containers the payload is labelled "pixels" (no sniffing)."""
    kind = classify_mzp(arc)
    for s in arc.sections:
        head = arc.head(s.index, 8)
        if head[:4] == MZX_MAGIC:
            res = decompress_ex(arc.read(s.index), invert, max_output=256)
            dsz = mzx_size(head)
            if kind.startswith("image"):
                label = "pixels(zeros)" if not any(res.data) else "pixels"
            else:
                label = sniff(res.data, dsz)
            return s.index, res.data, dsz, label
        if head[:6] == MZP_MAGIC:
            r = first_mzx_payload(arc.child(s.index), invert)
            if r is not None:
                return r
    return None


class DeepStats:
    def __init__(self) -> None:
        self.streams = 0
        self.ok = 0
        self.out_bytes = 0
        self.in_bytes = 0
        self.failures: list[str] = []
        self.overshoot = 0            # sum of bytes written past the declared size
        self.overshoot_max = 0
        self.overshoot_hist: collections.Counter[int] = collections.Counter()

    def merge(self, o: "DeepStats") -> None:
        self.streams += o.streams
        self.ok += o.ok
        self.out_bytes += o.out_bytes
        self.in_bytes += o.in_bytes
        self.overshoot += o.overshoot
        self.overshoot_max = max(self.overshoot_max, o.overshoot_max)
        self.overshoot_hist.update(o.overshoot_hist)
        room = 50 - len(self.failures)
        if room > 0:
            self.failures.extend(o.failures[:room])

    def as_dict(self) -> dict[str, Any]:
        return {"streams": self.streams, "ok": self.ok, "bad": self.streams - self.ok,
                "in_bytes": self.in_bytes, "out_bytes": self.out_bytes,
                "overshoot_sum": self.overshoot, "overshoot_max": self.overshoot_max,
                "overshoot_hist": {str(k): v for k, v in sorted(self.overshoot_hist.items())},
                "failures": self.failures[:20]}

    def add_overshoot(self, n: int) -> None:
        self.overshoot += n
        self.overshoot_max = max(self.overshoot_max, n)
        self.overshoot_hist[n] += 1


def check_stream(data: bytes, invert: bool, tag: str, expect_consume: Optional[int]) -> DeepStats:
    st = DeepStats()
    st.streams = 1
    st.in_bytes = len(data)
    try:
        res = decompress_ex(data, invert)
    except MzxError as e:
        st.failures.append(f"{tag}: {e}")
        return st
    st.out_bytes = len(res.data)
    st.add_overshoot(res.overshoot)
    want = len(data) if expect_consume is None else expect_consume
    if res.consumed != want:
        st.failures.append(f"{tag}: consumed {res.consumed} of {want}")
        return st
    st.ok = 1
    return st


def deep_mzp(arc: MzpArchive, tag: str, pool: ThreadPoolExecutor, invert: bool = False) -> DeepStats:
    jobs = []
    stack: list[tuple[MzpArchive, str]] = [(arc, tag)]
    while stack:
        a, t = stack.pop()
        for s in a.sections:
            head = a.head(s.index, 8)
            if head[:4] == MZX_MAGIC:
                jobs.append(pool.submit(check_stream, a.read(s.index), invert, f"{t}/{s.index}", None))
            elif head[:6] == MZP_MAGIC:
                stack.append((a.child(s.index), f"{t}/{s.index}"))
    st = DeepStats()
    for j in jobs:
        st.merge(j.result())
    return st


# ---------------------------------------------------------------------------
# triplet archives
# ---------------------------------------------------------------------------

def inventory_triplet(name: str, img: Path, out: Path, deep: bool, pool: ThreadPoolExecutor,
                      vse_names: Optional[list[str]]) -> dict[str, Any]:
    t0 = time.perf_counter()
    summ: dict[str, Any] = {"archive": name, "type": "hed/nam/mrg"}
    fmt_hist: collections.Counter[str] = collections.Counter()
    kind_hist: collections.Counter[str] = collections.Counter()
    ext_kind_hist: collections.Counter[str] = collections.Counter()
    sec_hist: collections.Counter[str] = collections.Counter()
    dec_hist: collections.Counter[str] = collections.Counter()
    errors: list[str] = []
    checks: collections.Counter[str] = collections.Counter()
    deep_total = DeepStats()
    with MrgArchive(img / name) as a, open(out / f"{name}.jsonl", "w", encoding="utf-8") as fo:
        summ.update(hed_format=a.format.value, entries=len(a), mrg_size=a.file_size,
                    hed_terminator=hx(a.hed.terminator), hed_trailing=hx(a.hed.trailing),
                    contiguous=a.hed.contiguous,
                    nam_records=(len(a.nam) if a.nam else None),
                    nam_record_size=(a.nam.record_size if a.nam else None),
                    nam_terminator=(a.nam.terminator if a.nam else None),
                    nam_trailing=(hx(a.nam.trailing) if a.nam else None),
                    names_aligned=a.names_aligned())
        issues = a.validate()
        summ["validation_issues"] = issues
        extents_ok = all(e.offset + e.span <= a.file_size for e in a.entries)
        summ["extents_inside_mrg"] = extents_ok
        name_src = "nam" if a.nam else None
        if name == "vse" and vse_names is not None and len(vse_names) == len(a):
            name_src = "se_nam.mrg#2"
        summ["name_source"] = name_src
        for e in a.entries:
            rec: dict[str, Any] = {
                "archive": name, "index": e.index,
                "name": e.name if e.name is not None else (vse_names[e.index] if name_src == "se_nam.mrg#2" and vse_names else None),
                "offset": e.offset, "size": e.size, "size_exact": e.size_exact,
                "sector_offset": e.sector_offset, "size_sectors": e.size_sectors, "size_lo": e.size_lo,
                "hed_raw": hx(e.hed_raw),
            }
            if a.nam is not None and a.nam.records[e.index].tag:
                rec["nam_tag"] = a.nam.records[e.index].tag
            head512 = a.head(e.index, 512)
            rec["head"] = hx(head512[:16])
            fmt = sniff(head512, e.size)
            rec["format"] = fmt
            fmt_hist[fmt] += 1
            ecks: dict[str, Any] = {}
            try:
                if fmt == "mzp":
                    arc = MzpArchive(a.mm, e.offset, e.size)
                    ms = mzp_summary(arc)
                    rec["mzp"] = ms
                    if "image" in ms:
                        rec["image"] = ms.pop("image")
                    kind_hist[ms["kind"]] += 1
                    if e.name:
                        ext_kind_hist[f"{e.name.rsplit('.', 1)[-1]}:{ms['kind']}"] += 1
                    for sec in ms["sections"]:
                        sec_hist[sec[2]] += 1
                    ecks["mzp_packed_size_eq_hed_size"] = arc.packed_size == e.size
                    ecks["mzp_layout_ok"] = not ms["issues"]
                    fp = first_mzx_payload(arc)
                    if fp is not None:
                        si, dec, dsz, dlab = fp
                        rec["decoded_from_section"] = si
                        rec["decoded_head"] = hx(dec[:16])
                        rec["decoded_size"] = dsz
                        rec["decoded_format"] = dlab
                        dec_hist[rec["decoded_format"]] += 1
                    if deep:
                        st = deep_mzp(arc, f"{name}#{e.index}", pool)
                        rec["deep"] = st.as_dict()
                        deep_total.merge(st)
                elif fmt == "lv-mzx":
                    raw = a.read_sectors(e.index)
                    lvd = decompress_lv(raw)
                    lvh = lvd.header
                    rec["lv"] = {"header": hx(raw[:lvh.body_offset]), "n": lvh.length,
                                 "payload": hx(lvh.payload),
                                 "header_is_standard": raw[:lvh.body_offset] == LV_HEADER}
                    if lvd.mzx is None:
                        raise MzxError("LV body is not an MZX0 stream")
                    res = lvd.mzx
                    msz = mzx_size(raw[lvh.body_offset:lvh.body_offset + 8])
                    rec["decoded_head"] = hx(res.data[:16])
                    rec["decoded_size"] = msz
                    rec["decoded_format"] = sniff(res.data[:512], msz)
                    dec_hist[rec["decoded_format"]] += 1
                    rec["mzx_consumed"] = res.consumed
                    rec["exact_size"] = lvd.consumed
                    ri = riff_info(res.data)
                    if ri:
                        rec["riff"] = ri
                        ecks["riff_size_eq_mzx_size"] = ri["riff_size"] == msz
                    ecks["mzx_fits_in_sectors"] = lvd.consumed <= e.span
                    ecks["mzx_output_size_ok"] = len(res.data) == msz
                    # HED4 sector count = floor(exact / 0x800) + 1 (not ceil)
                    ecks["hed4_sectors_eq_floor_plus_1"] = e.size_sectors == lvd.consumed // 0x800 + 1
                    ecks["hed4_sectors_eq_ceil"] = e.size_sectors == -(-lvd.consumed // 0x800)
                    st = DeepStats()
                    st.streams, st.ok, st.in_bytes, st.out_bytes = 1, int(len(res.data) == msz), res.consumed, len(res.data)
                    st.add_overshoot(res.overshoot)
                    deep_total.merge(st)
                elif fmt.startswith("riff"):
                    ri = riff_info(head512)
                    rec["riff"] = ri
                    if ri:
                        ecks["riff_size_eq_hed_size"] = ri["riff_size"] == e.size
                elif fmt == "vag":
                    vi = vag_info(head512)
                    rec["vag"] = vi
                    if vi:
                        ecks["vag_size_eq_hed_size"] = vi["total_size"] == e.size
            except Exception as ex:  # record and continue; counted as an error
                errors.append(f"entry {e.index}: {type(ex).__name__}: {ex}")
                rec["error"] = f"{type(ex).__name__}: {ex}"
            if ecks:
                rec["checks"] = ecks
                for k, v in ecks.items():
                    checks[f"{k}:{'ok' if v else 'FAIL'}"] += 1
            fo.write(json.dumps(rec, separators=(",", ":")) + "\n")
    summ.update(format_hist=dict(fmt_hist), mzp_kind_hist=dict(kind_hist),
                name_ext_kind_hist=dict(ext_kind_hist),
                section_label_hist=dict(sec_hist), decoded_format_hist=dict(dec_hist),
                checks=dict(checks), errors=errors[:50], error_count=len(errors),
                seconds=round(time.perf_counter() - t0, 1))
    if deep or name.startswith("voice"):
        summ["mzx_deep"] = deep_total.as_dict()
    return summ


# ---------------------------------------------------------------------------
# standalone mrgd00 files
# ---------------------------------------------------------------------------

def inventory_standalone(name: str, img: Path, out: Path, deep: bool,
                         pool: ThreadPoolExecutor) -> dict[str, Any]:
    t0 = time.perf_counter()
    path = img / f"{name}.mrg"
    summ: dict[str, Any] = {"archive": name, "type": "mrgd00", "file_size": os.path.getsize(path)}
    fmt_hist: collections.Counter[str] = collections.Counter()
    dec_hist: collections.Counter[str] = collections.Counter()
    errors: list[str] = []
    deep_total = DeepStats()
    with open(path, "rb") as f, open(out / f"{name}.jsonl", "w", encoding="utf-8") as fo:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            arc = MzpArchive(mm)
            issues = arc.validate()
            summ.update(sections=arc.count, data_start=arc.data_start, end=arc.end,
                        packed_size=arc.packed_size, packed_size_eq_file_size=arc.packed_size == len(mm),
                        trailing_after_pad=len(mm) - arc.packed_size, validation_issues=issues,
                        extents_inside_file=all(s.end <= len(mm) for s in arc.sections),
                        kind=classify_mzp(arc))
            names: Optional[list[str]] = None
            if name == "allscr":
                names = parse_nam32(arc.read(0)).names
                summ["script_names"] = len(names)
                summ["script_sections"] = sum(1 for s in arc.sections if s.index >= 3)
                summ["names_aligned"] = len(names) == arc.count - 3
            for s in arc.sections:
                rec: dict[str, Any] = {"archive": name, "index": s.index, "name": None, "role": None,
                                       "offset": s.offset, "size": s.size,
                                       "raw_record": hx(s.raw_record)}
                head = arc.head(s.index, 512)
                rec["head"] = hx(head[:16])
                lab = section_label(arc, s.index)
                rec["format"] = lab
                fmt_hist[lab] += 1
                invert = False
                if name == "allscr":
                    if s.index == 0:
                        rec["role"] = "script-names"
                    elif s.index == 1:
                        rec["role"] = "label-tables"   # child k <-> script name k
                    elif s.index == 2:
                        rec["role"] = "image"
                    else:
                        rec["role"] = "script"
                        rec["name"] = names[s.index - 3] if names and s.index - 3 < len(names) else None
                        invert = True
                elif name == "script_text":
                    rec["role"] = "string-offsets" if s.index % 2 == 0 else "strings"
                    rec["pair"] = s.index // 2
                    if s.index % 2 == 0:
                        rec["offset_count"] = s.size // 4
                        vals = struct.unpack(f">{s.size // 4}I", arc.read(s.index))
                        rec["ffffffff_at"] = [k for k, v in enumerate(vals) if v == 0xFFFFFFFF]
                        rec["last_values"] = [f"0x{v:x}" for v in vals[-3:]]
                        rec["pad_after"] = hx(mm[s.end:s.end + pad_after(s.end)])
                elif name.startswith("ft_"):
                    rec["role"] = "font-page?"
                elif name == "se_nam":
                    rec["role"] = "name-table"
                try:
                    if lab == "nam32":
                        t = parse_nam32(arc.read(s.index))
                        rec["nam"] = {"records": len(t), "terminator": t.terminator,
                                      "first": t.names[:5], "last": t.names[-3:],
                                      "tags": sorted({r.tag for r in t.records if r.tag})}
                        if name == "se_nam" and len(t) == 1327:
                            rec["links_to"] = "vse.mrg (1327 entries)"
                    elif lab == "mzp":
                        child = arc.child(s.index)
                        ms = mzp_summary(child)
                        rec["mzp"] = ms
                        if name == "allscr" and s.index == 1 and names is not None:
                            rec["children_named_by"] = "allscr section 0 (child k <-> name k)"
                        fp = first_mzx_payload(child)
                        if fp is not None:
                            rec["decoded_from_section"] = fp[0]
                            rec["decoded_head"] = hx(fp[1][:16])
                            rec["decoded_size"] = fp[2]
                            rec["decoded_format"] = fp[3]
                            dec_hist[rec["decoded_format"]] += 1
                        if deep:
                            st = deep_mzp(child, f"{name}/{s.index}", pool)
                            rec["deep"] = st.as_dict()
                            deep_total.merge(st)
                    elif lab == "mzx":
                        data = arc.read(s.index)
                        res = decompress_ex(data, invert)
                        rec["invert"] = invert
                        rec["decoded_head"] = hx(res.data[:16])
                        rec["decoded_size"] = len(res.data)
                        rec["decoded_format"] = sniff(res.data[:512], len(res.data))
                        rec["mzx_consumed_all"] = res.consumed == len(data)
                        dec_hist[rec["decoded_format"]] += 1
                        st = DeepStats()
                        st.streams, st.in_bytes, st.out_bytes = 1, len(data), len(res.data)
                        st.add_overshoot(res.overshoot)
                        st.ok = int(res.consumed == len(data) and len(res.data) == mzx_size(data))
                        if not st.ok:
                            st.failures.append(f"{name}/{s.index}: consumed {res.consumed}/{len(data)}")
                        deep_total.merge(st)
                    elif lab.startswith("text"):
                        rec["text_sample"] = bytes(head[:96]).decode("utf-8", "replace")
                except Exception as ex:
                    errors.append(f"section {s.index}: {type(ex).__name__}: {ex}")
                    rec["error"] = f"{type(ex).__name__}: {ex}"
                fo.write(json.dumps(rec, separators=(",", ":"), ensure_ascii=False) + "\n")
        finally:
            mm.close()
    summ.update(format_hist=dict(fmt_hist), decoded_format_hist=dict(dec_hist),
                errors=errors[:50], error_count=len(errors), seconds=round(time.perf_counter() - t0, 1),
                mzx_deep=deep_total.as_dict())
    return summ


# ---------------------------------------------------------------------------
# summary
# ---------------------------------------------------------------------------

def _hist(h: dict[str, int]) -> str:
    return ", ".join(f"{k} {v}" for k, v in sorted(h.items(), key=lambda kv: -kv[1])) or "-"


def write_summary(out: Path, results: list[dict[str, Any]], deep: bool, game: Path) -> None:
    (out / "summary.json").write_text(json.dumps(results, indent=1), encoding="utf-8")
    L: list[str] = []
    L.append("# Container inventory\n")
    L.append(f"Generated by `tools/inventory.py{' --deep' if deep else ''}` on {time.strftime('%Y-%m-%d %H:%M')} "
             f"from `{game}` (numba={'yes' if HAVE_NUMBA else 'no'}).\n")
    L.append("One JSONL file per archive sits next to this file; field meanings are in the "
             "docstring of tools/inventory.py and in docs/formats/containers.md.\n")
    L.append("## Triplet archives (.hed/.nam/.mrg)\n")
    L.append("| archive | HED | entries | .mrg bytes | NAM names | names aligned | extents in .mrg | contiguous | validation issues | errors |")
    L.append("|---|---|---:|---:|---:|---|---|---|---:|---:|")
    for r in results:
        if r.get("type") != "hed/nam/mrg":
            continue
        L.append(f"| {r['archive']} | {r['hed_format']} | {r['entries']} | {r['mrg_size']} | "
                 f"{r['nam_records'] if r['nam_records'] is not None else '-'} | "
                 f"{r['names_aligned'] if r['nam_records'] is not None else ('via ' + r['name_source'] if r.get('name_source') else '-')} | "
                 f"{r['extents_inside_mrg']} | {r['contiguous']} | {len(r['validation_issues'])} | {r['error_count']} |")
    L.append("")
    for r in results:
        if r.get("type") != "hed/nam/mrg":
            continue
        L.append(f"### {r['archive']}\n")
        L.append(f"- entry formats: {_hist(r['format_hist'])}")
        if r["mzp_kind_hist"]:
            L.append(f"- MZP kinds: {_hist(r['mzp_kind_hist'])}")
            if r.get("name_ext_kind_hist"):
                L.append(f"- NAM extension : content kind: {_hist(r['name_ext_kind_hist'])}")
            L.append(f"- MZP section labels: {_hist(r['section_label_hist'])}")
        if r["decoded_format_hist"]:
            L.append(f"- first decompressed payload: {_hist(r['decoded_format_hist'])}")
        if r["checks"]:
            L.append(f"- checks: {_hist(r['checks'])}")
        if "mzx_deep" in r:
            d = r["mzx_deep"]
            L.append(f"- MZX full decode: {d['ok']}/{d['streams']} streams ok, "
                     f"{d['in_bytes']} -> {d['out_bytes']} bytes, "
                     f"max overshoot {d['overshoot_max']} B"
                     + (f", failures: {d['failures'][:5]}" if d["failures"] else ""))
        if r["errors"]:
            L.append(f"- errors: {r['errors'][:5]}")
        L.append("")
    L.append("## Standalone mrgd00 files\n")
    L.append("| file | bytes | sections | kind | packed size == file size | validation issues | errors |")
    L.append("|---|---:|---:|---|---|---:|---:|")
    for r in results:
        if r.get("type") != "mrgd00":
            continue
        L.append(f"| {r['archive']}.mrg | {r['file_size']} | {r['sections']} | {r['kind']} | "
                 f"{r['packed_size_eq_file_size']} | {len(r['validation_issues'])} | {r['error_count']} |")
    L.append("")
    for r in results:
        if r.get("type") != "mrgd00":
            continue
        L.append(f"### {r['archive']}.mrg\n")
        L.append(f"- section formats: {_hist(r['format_hist'])}")
        if r["decoded_format_hist"]:
            L.append(f"- decompressed payloads: {_hist(r['decoded_format_hist'])}")
        d = r["mzx_deep"]
        if d["streams"]:
            L.append(f"- MZX decode: {d['ok']}/{d['streams']} streams ok, {d['in_bytes']} -> {d['out_bytes']} bytes, "
                     f"max overshoot {d['overshoot_max']} B"
                     + (f", failures: {d['failures'][:5]}" if d["failures"] else ""))
        if "script_names" in r:
            L.append(f"- {r['script_names']} script names for {r['script_sections']} script sections "
                     f"(aligned: {r['names_aligned']})")
        if r["errors"]:
            L.append(f"- errors: {r['errors'][:5]}")
        L.append("")
    tot_entries = sum(r.get("entries", 0) for r in results)
    tot_err = sum(r["error_count"] for r in results)
    tot_issues = sum(len(r["validation_issues"]) for r in results)
    L.append("## Totals\n")
    L.append(f"- triplet entries: {tot_entries}; standalone sections: "
             f"{sum(r.get('sections', 0) for r in results if r.get('type') == 'mrgd00')}")
    L.append(f"- validation issues: {tot_issues}; entries/sections that failed to parse: {tot_err}")
    ds = [r["mzx_deep"] for r in results if "mzx_deep" in r]
    if ds:
        L.append(f"- MZX streams decoded: {sum(d['streams'] for d in ds)}, ok {sum(d['ok'] for d in ds)}, "
                 f"bad {sum(d['bad'] for d in ds)}")
        oh: collections.Counter[int] = collections.Counter()
        for d in ds:
            oh.update({int(k): v for k, v in d.get("overshoot_hist", {}).items()})
        if oh:
            n_all = sum(oh.values())
            L.append(f"- MZX last-command overshoot (bytes written past the declared size): max {max(oh)}, "
                     f"0 B in {oh[0]}/{n_all} streams, >= 2 B in {sum(v for k, v in oh.items() if k >= 2)}, "
                     f">= 64 B in {sum(v for k, v in oh.items() if k >= 64)}")
    (out / "summary.md").write_text("\n".join(L) + "\n", encoding="utf-8")


def main(argv: Optional[list[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", type=Path, default=None, help="CUSA47999 folder")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--deep", action="store_true", help="decompress every MZX stream")
    ap.add_argument("--only", nargs="*", help="archive base names to process")
    ap.add_argument("--threads", type=int, default=max(1, (os.cpu_count() or 2) - 2))
    args = ap.parse_args(argv)
    game = args.game or game_dir()
    if game is None:
        print("game folder not found (set TSUKI_GAME_DIR)", file=sys.stderr)
        return 2
    img = game / "image"
    out: Path = args.out
    out.mkdir(parents=True, exist_ok=True)
    sel = set(args.only) if args.only else None
    vse_names: Optional[list[str]] = None
    with open(img / "se_nam.mrg", "rb") as f:
        se = MzpArchive(f.read())
        if se.count > 2:
            vse_names = parse_nam32(se.read(2)).names
    results: list[dict[str, Any]] = []
    with ThreadPoolExecutor(args.threads) as pool:
        for name in STANDALONE + TRIPLETS:
            if sel is not None and name not in sel:
                continue
            t = time.perf_counter()
            if name in TRIPLETS:
                r = inventory_triplet(name, img, out, args.deep, pool, vse_names)
            else:
                r = inventory_standalone(name, img, out, args.deep, pool)
            results.append(r)
            print(f"{name}: {r.get('entries', r.get('sections'))} items, errors {r['error_count']}, "
                  f"issues {len(r['validation_issues'])}, {time.perf_counter() - t:.1f}s", flush=True)
    if sel is None:
        write_summary(out, results, args.deep, game)
    else:
        # merge into an existing summary.json if present
        sj = out / "summary.json"
        old: list[dict[str, Any]] = json.loads(sj.read_text(encoding="utf-8")) if sj.exists() else []
        names = {r["archive"] for r in results}
        merged = [r for r in old if r["archive"] not in names] + results
        order = {n: i for i, n in enumerate(STANDALONE + TRIPLETS)}
        merged.sort(key=lambda r: order.get(r["archive"], 99))
        write_summary(out, merged, args.deep, game)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
