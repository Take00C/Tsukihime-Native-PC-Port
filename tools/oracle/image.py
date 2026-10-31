"""Area ``image``: the SHA-1 of every picture, from ``tsuki.image`` (ROADMAP M1 acceptance 4).

The C# decoder (``Tsuki.Data.Images``) must produce, for every image MZP, the same displayed
straight-alpha RGBA8 as ``tsuki.image.decode_image`` (row-major ``out_height x out_width x 4``,
tiles flagged 0 left transparent).  This module records that picture as a SHA-1 and the header
facts the C# parser is checked against.

Parts (each a directory of ``samples/golden/image/``):

``sha1``  2,000 pictures chosen by stratified sampling (the per-build differential test).  The strata
          are source x pixel format x tile height x size class; every stratum has at least a few
          members, the extremes (largest, widest, tallest, smallest of every format) and the pictures
          the documents call out are always in.  The choice is deterministic (fixed seed, stable
          label order), so the same 2,000 pictures are tested on every build.
``all``   every image MZP of the retail data (the nightly run; about 35,630 pictures, minutes on
          all cores).  Not part of ``--quick`` runs.

Each part is one TSV file (``pictures.tsv``, UTF-8, LF, one header line)::

    label  name  format  width  height  shown_width  shown_height  tile_w  tile_h
    tiles_x  tiles_y  attr  live_tiles  sha1  stratum

``label`` is ``<source>#<entry>[/<section>...]``: ``allpac0#12`` is HED entry 12 of allpac0,
``allpac3#5/17`` is section 17 of the container in entry 5, ``ft_en.mrg#4/1`` is section 1 of top-level
section 4 of ft_en.mrg (the labels of the MZX0 stream hashes, ``oracle.data``, minus the tile section).
``name`` is the NAM name of the top-level entry (``-`` when the source has none), ``format`` the
``tsuki.image.PixelFormat`` value, ``width``/``height`` the stored size, ``shown_*`` the displayed
size, ``live_tiles`` the number of tiles with a non-zero flag, ``sha1`` the lower-case hex SHA-1 of
the displayed RGBA bytes and ``stratum`` the sampling stratum (``all`` rows carry ``-``).

Run: ``python -m oracle.run image --parts sha1`` (or ``python -m oracle.image --parts sha1``, which
registers this area with the runner itself).  Everything goes to the local ``samples/golden/image/``.
"""
from __future__ import annotations

import hashlib
import mmap
import multiprocessing as mp
import os
import random
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Iterator, Optional, Sequence

from tsuki import image as timage
from tsuki.mrg import MrgArchive
from tsuki.mzp import MzpArchive
from tsuki.sniff import classify_mzp

from .manifest import AreaWriter, ManifestError, PartWriter, Producer

__all__ = [
    "AREA", "MODULE", "PARTS", "DESCRIPTION", "TRIPLETS", "STANDALONE", "STRATIFIED_COUNT", "EXPECTED_PICTURES",
    "EXPECTED_FORMATS", "PictureRef", "census", "select_stratified", "export_sha1", "export_all", "run", "main",
    "pictures_tsv", "decode_sha1",
]

AREA = "image"
MODULE = "oracle.image"
PARTS: tuple[str, ...] = ("sha1", "all")
DESCRIPTION = ("picture SHA-1s of tsuki.image (displayed RGBA8) for the C# image decoder: 2,000 stratified "
               "pictures per build (sha1) and every image MZP nightly (all)")

TRIPLETS: tuple[str, ...] = ("allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4", "parts")
STANDALONE: tuple[str, ...] = ("allscr.mrg", "ft_en.mrg", "ft_ja.mrg", "ft_ko.mrg", "ft_zc.mrg", "ft_zt.mrg")
STRATIFIED_COUNT = 2000
SEED = 20261026

# docs/formats/images.md 1: image MZPs per pixel format (retail data, header parsed by tsuki.image).
EXPECTED_PICTURES = 35_630
EXPECTED_FORMATS = {"pal8": 18_599, "pal4": 20, "hep8": 16_443, "rgb565lo+a": 568}
IMAGES_MD = "docs/formats/images.md 1"

COLUMNS = ("label", "name", "format", "width", "height", "shown_width", "shown_height", "tile_w", "tile_h",
           "tiles_x", "tiles_y", "attr", "live_tiles", "sha1", "stratum")

# pictures the documents name (images.md 10, spikes S4): always part of the stratified sample when present
NAMED_ANCHORS = ("IMG1481.MZP", "IMG1936.MZP", "IMG3885.MZP", "IMG1481_20_00.MZP", "IMG2091.MZP", "IMG1732.MZP",
                 "IMG1733.MZP", "DUMMY.MRG", "CGMODE_TOP.MRG", "BLACK.MZP", "IMG1685.MZP", "ANIME_TOP.MZP",
                 "CAUTION_JA.MZP", "CAUTION_EN.MZP")


# ---------------------------------------------------------------------------
# the census: every picture and its header
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class PictureRef:
    source: str                    # archive base name, or the standalone file name
    entry: int                     # HED entry, or top-level section of a standalone file
    path: tuple[int, ...]          # sections to descend inside that entry
    name: Optional[str]            # NAM name of the entry
    fmt: str                       # tsuki.image.PixelFormat value
    width: int
    height: int
    shown_w: int
    shown_h: int
    tile_w: int
    tile_h: int
    tiles_x: int
    tiles_y: int
    attr: int
    depth: int
    bmp_type: int
    live: int                      # tiles with a non-zero flag
    flag2: int                     # tiles flagged 2
    order: int                     # position in the stable label order

    @property
    def label(self) -> str:
        return f"{self.source}#{self.entry}" + "".join(f"/{p}" for p in self.path)

    @property
    def area(self) -> int:
        return self.shown_w * self.shown_h

    @property
    def group(self) -> str:
        return "ft" if self.source.startswith("ft_") else self.source.removesuffix(".mrg")


def _walk_images(arc: MzpArchive, path: tuple[int, ...] = ()) -> Iterator[tuple[tuple[int, ...], MzpArchive]]:
    """Every image MZP inside ``arc`` (arc itself, or the containers nested in it)."""
    if classify_mzp(arc) == "image":
        yield path, arc
        return
    for i in range(arc.count):
        if arc.is_nested(i):
            yield from _walk_images(arc.child(i), path + (i,))


def _ref(source: str, entry: int, path: tuple[int, ...], name: Optional[str], arc: MzpArchive, order: int) -> PictureRef:
    h = timage.parse_header(arc.read(0))
    return PictureRef(source, entry, path, name, h.format.value, h.width, h.height, h.out_width, h.out_height,
                      h.tile_w, h.tile_h, h.tiles_x, h.tiles_y, h.attr, h.depth, h.bmp_type,
                      sum(1 for f in h.tile_flags if f != 0), sum(1 for f in h.tile_flags if f == 2), order)


def census(game: Path, log: Optional[Callable[[str], None]] = None) -> list[PictureRef]:
    """Every image MZP of the game folder in stable label order (triplets in the order of
    :data:`TRIPLETS`, then the standalone files; entries and sections ascending)."""
    image = Path(game) / "image"
    out: list[PictureRef] = []
    for name in TRIPLETS:
        t0 = time.perf_counter()
        n0 = len(out)
        with MrgArchive(image / name) as a:
            for e in a.entries:
                top = MzpArchive(a.mm, e.offset, e.size)
                for path, arc in _walk_images(top):
                    out.append(_ref(name, e.index, path, e.name, arc, len(out)))
        if log is not None:
            log(f"[image] census {name}: {len(out) - n0:,} pictures ({time.perf_counter() - t0:.1f} s)")
    for name in STANDALONE:
        n0 = len(out)
        with open(image / name, "rb") as f:
            mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
            try:
                top = MzpArchive(mm)
                for s in range(top.count):
                    if top.is_nested(s):
                        for path, arc in _walk_images(top.child(s)):
                            out.append(_ref(name, s, path, None, arc, len(out)))
            finally:
                mm.close()
        if log is not None:
            log(f"[image] census {name}: {len(out) - n0:,} pictures")
    return out


# ---------------------------------------------------------------------------
# stratified selection
# ---------------------------------------------------------------------------

def _size_class(area: int) -> str:
    if area <= 64 * 64:
        return "tiny"
    if area <= 512 * 512:
        return "small"
    if area <= 1920 * 1080:
        return "medium"
    if area <= 16_000_000:
        return "large"
    return "huge"


def stratum(r: PictureRef) -> str:
    return f"{r.group}/{r.fmt}/t{r.tile_h}/{_size_class(r.area)}"


def _systematic(n: int, k: int, rng: random.Random) -> list[int]:
    """k positions spread evenly over range(n), one at a random place inside every stratum cell."""
    if k >= n:
        return list(range(n))
    step = n / k
    return sorted({min(n - 1, int(step * i + rng.random() * step)) for i in range(k)})


def select_stratified(pics: Sequence[PictureRef], total: int = STRATIFIED_COUNT, seed: int = SEED,
                      min_per_stratum: int = 3) -> tuple[list[PictureRef], dict[str, int]]:
    """``total`` pictures by stratified sampling; returns them in label order with the strata sizes.

    1. anchors: the largest, widest, tallest and smallest picture of every format, the first picture of
       every (bmp_type, depth, attr) variant, the pictures with the most tiles and with flag-2 tiles, and
       the named pictures of :data:`NAMED_ANCHORS`;
    2. every stratum keeps at least ``min_per_stratum`` members, and a stratum of up to 20 pictures is
       taken whole;
    3. the rest is shared out in proportion to the stratum sizes (largest remainder) and drawn
       systematically in label order with a seeded random offset.
    """
    rng = random.Random(seed)
    strata: dict[str, list[PictureRef]] = {}
    for r in pics:
        strata.setdefault(stratum(r), []).append(r)
    chosen: dict[int, PictureRef] = {}

    def pick(r: Optional[PictureRef]) -> None:
        if r is not None:
            chosen[r.order] = r

    by_fmt: dict[str, list[PictureRef]] = {}
    for r in pics:
        by_fmt.setdefault(r.fmt, []).append(r)
    for rows in by_fmt.values():
        pick(max(rows, key=lambda r: (r.area, -r.order)))
        pick(max(rows, key=lambda r: (r.shown_w, -r.order)))
        pick(max(rows, key=lambda r: (r.shown_h, -r.order)))
        pick(min(rows, key=lambda r: (r.area, r.order)))
        pick(max(rows, key=lambda r: (r.tiles_x * r.tiles_y, -r.order)))
        pick(next((r for r in rows if r.flag2 > 0), None))
        pick(next((r for r in rows if r.live < r.tiles_x * r.tiles_y), None))
    seen: set[tuple[int, int, int, int, int]] = set()
    for r in pics:
        key = (r.bmp_type, r.depth, r.attr, r.tile_w, r.tile_h)
        if key not in seen:
            seen.add(key)
            pick(r)
    named = {n.upper() for n in NAMED_ANCHORS}
    for r in pics:
        if r.name is not None and r.name.upper() in named:
            pick(r)

    remaining = total - len(chosen)
    if remaining < 0:
        raise ManifestError(f"{len(chosen)} anchors exceed the sample size {total}")
    free: dict[str, list[PictureRef]] = {k: [r for r in v if r.order not in chosen] for k, v in strata.items()}
    have = {k: sum(1 for r in v if r.order in chosen) for k, v in strata.items()}
    # a rare stratum (up to 20 pictures: the PAL4 placeholders, the parts sheets, ...) is taken whole
    want = {k: len(free[k]) if len(strata[k]) <= 20 else max(0, min(len(free[k]), min_per_stratum - have[k]))
            for k in strata}
    remaining -= sum(want.values())
    if remaining < 0:
        raise ManifestError("the minimum per stratum exceeds the sample size")
    cap = {k: len(free[k]) - want[k] for k in strata}
    cap_total = sum(cap.values())
    if remaining > cap_total:
        raise ManifestError(f"only {cap_total} pictures left to share out, {remaining} wanted")
    shares = {k: remaining * cap[k] / cap_total for k in strata} if cap_total else {k: 0.0 for k in strata}
    take = {k: int(shares[k]) for k in strata}
    left = remaining - sum(take.values())
    for k in sorted(strata, key=lambda k: (-(shares[k] - take[k]), k)):
        if left == 0:
            break
        if take[k] < cap[k]:
            take[k] += 1
            left -= 1
    for k in sorted(strata):
        members = free[k]
        n = want[k] + take[k]
        if n:
            # spread the minimum and the share evenly over the stratum, in label order
            for i in _systematic(len(members), n, rng):
                pick(members[i])
    # systematic sampling can collide on a rounding step; top up from the largest strata, in label order
    k_order = sorted(strata, key=lambda k: (-len(strata[k]), k))
    while len(chosen) < total:
        for k in k_order:
            cand = next((r for r in strata[k] if r.order not in chosen), None)
            if cand is not None:
                pick(cand)
                if len(chosen) == total:
                    break
        else:
            break
    out = sorted(chosen.values(), key=lambda r: r.order)
    if len(out) != total:
        raise ManifestError(f"selected {len(out)} pictures, wanted {total}")
    return out, {k: len(v) for k, v in strata.items()}


# ---------------------------------------------------------------------------
# decoding (runs in worker processes)
# ---------------------------------------------------------------------------

_OPEN: dict[str, Any] = {}      # per process: open archives / mappings by source name


def _top(game: str, source: str) -> Any:
    """The open archive of ``source`` in this process: a MrgArchive, or (file, mmap, MzpArchive)."""
    handle = _OPEN.get(source)
    if handle is None:
        image = Path(game) / "image"
        if source in STANDALONE:
            f = open(image / source, "rb")
            mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
            handle = (f, mm, MzpArchive(mm))
        else:
            handle = MrgArchive(image / source)
        _OPEN[source] = handle
    return handle


def _container(game: str, source: str, entry: int, path: tuple[int, ...]) -> MzpArchive:
    handle = _top(game, source)
    if source in STANDALONE:
        arc = handle[2].child(entry)
    else:
        e = handle.entries[entry]
        arc = MzpArchive(handle.mm, e.offset, e.size)
    for p in path:
        arc = arc.child(p)
    return arc


def _entry_bytes(game: str, source: str, entry: int) -> tuple[Optional[str], int, bytes]:
    """(NAM name, offset in the file, stored bytes) of a HED entry or a top-level section."""
    handle = _top(game, source)
    if source in STANDALONE:
        top = handle[2]
        return None, top.sections[entry].offset, top.read(entry)
    e = handle.entries[entry]
    return e.name, e.offset, handle.read(entry)


def decode_sha1(game: str, source: str, entry: int, path: tuple[int, ...]) -> str:
    """SHA-1 of the displayed RGBA8 of one picture (the oracle value)."""
    arc = _container(game, source, entry, path)
    img = timage.decode_image(arc)
    return hashlib.sha1(img.rgba.data).hexdigest()


def _job(args: tuple[int, str, str, int, tuple[int, ...]]) -> tuple[int, str]:
    i, game, source, entry, path = args
    return i, decode_sha1(game, source, entry, path)


def _hash_all(game: Path, pics: Sequence[PictureRef], procs: int,
              log: Optional[Callable[[str], None]]) -> list[str]:
    """SHA-1 of every picture in ``pics`` (same order), on ``procs`` processes, biggest first."""
    jobs = sorted(range(len(pics)), key=lambda i: -pics[i].area)
    payload = [(i, str(game), pics[i].source, pics[i].entry, pics[i].path) for i in jobs]
    out: list[str] = [""] * len(pics)
    t0 = time.perf_counter()
    done = 0
    step = max(1, len(pics) // 10)
    with mp.Pool(procs) as pool:
        for i, digest in pool.imap_unordered(_job, payload, chunksize=2):
            out[i] = digest
            done += 1
            if log is not None and done % step == 0:
                log(f"[image] hashed {done:,} of {len(pics):,} ({time.perf_counter() - t0:.0f} s)")
    return out


# ---------------------------------------------------------------------------
# parts
# ---------------------------------------------------------------------------

def _producer(function: str, args: dict[str, Any], uses: tuple[str, ...] = ()) -> Producer:
    return Producer(MODULE, function, args, uses or ("tsuki.image.decode_image",))


def pictures_tsv(pics: Sequence[PictureRef], digests: Sequence[str], strata: Optional[Sequence[str]]) -> bytes:
    lines = ["\t".join(COLUMNS)]
    for i, r in enumerate(pics):
        lines.append("\t".join((
            r.label, r.name if r.name else "-", r.fmt, str(r.width), str(r.height), str(r.shown_w), str(r.shown_h),
            str(r.tile_w), str(r.tile_h), str(r.tiles_x), str(r.tiles_y), str(r.attr), str(r.live), digests[i],
            strata[i] if strata is not None else "-")))
    return ("\n".join(lines) + "\n").encode("utf-8")


def _census_checks(part: PartWriter, pics: Sequence[PictureRef]) -> None:
    part.count("census_pictures", len(pics))
    part.check("census_pictures", EXPECTED_PICTURES, len(pics), IMAGES_MD)
    counts: dict[str, int] = {}
    for r in pics:
        counts[r.fmt] = counts.get(r.fmt, 0) + 1
    for fmt, want in EXPECTED_FORMATS.items():
        part.check(f"census_{fmt}", want, counts.get(fmt, 0), IMAGES_MD)


def _inputs(part: PartWriter, game: Path) -> None:
    image = game / "image"
    for name in TRIPLETS:
        for ext in (".hed", ".nam", ".mrg"):
            if (image / (name + ext)).exists():
                part.add_input(image / (name + ext))
    for name in STANDALONE:
        part.add_input(image / name)


def export_sha1(part: PartWriter, game: Path, pics: Sequence[PictureRef], procs: int,
                log: Optional[Callable[[str], None]] = None) -> None:
    """The stratified part: ``pictures.tsv`` with :data:`STRATIFIED_COUNT` pictures."""
    _census_checks(part, pics)
    chosen, sizes = select_stratified(pics)
    t0 = time.perf_counter()
    digests = _hash_all(game, chosen, procs, log)
    _inputs(part, game)
    done_entries: set[tuple[str, int]] = set()
    for r in chosen:
        if (r.source, r.entry) in done_entries:
            continue
        done_entries.add((r.source, r.entry))
        name, offset, data = _entry_bytes(str(game), r.source, r.entry)
        part.add_entry(r.source, r.entry, name, offset, data)
    strata = [stratum(r) for r in chosen]
    part.write_bytes("pictures.tsv", pictures_tsv(chosen, digests, strata), "bytes",
                     _producer("export_sha1", {"count": STRATIFIED_COUNT, "seed": SEED, "columns": list(COLUMNS)}),
                     {"encoding": "tsv", "columns": list(COLUMNS), "rows": len(chosen),
                      "digest": "sha1 of the displayed RGBA8 (tsuki.image.decode_image(...).rgba)"})
    part.count("pictures", len(chosen))
    part.count("strata", len(sizes))
    part.count("seconds", round(time.perf_counter() - t0, 1))
    part.check("stratified_count", STRATIFIED_COUNT, len(chosen), "ROADMAP M1 acceptance 4")
    counts: dict[str, int] = {}
    for r in chosen:
        counts[r.fmt] = counts.get(r.fmt, 0) + 1
    part.count("formats", counts)
    part.check("every_format_sampled", sorted(EXPECTED_FORMATS), sorted(counts), IMAGES_MD)


def export_all(part: PartWriter, game: Path, pics: Sequence[PictureRef], procs: int,
               log: Optional[Callable[[str], None]] = None) -> None:
    """The nightly part: ``pictures.tsv`` with every image MZP."""
    _census_checks(part, pics)
    t0 = time.perf_counter()
    digests = _hash_all(game, pics, procs, log)
    seconds = time.perf_counter() - t0
    _inputs(part, game)
    part.write_bytes("pictures.tsv", pictures_tsv(pics, digests, None), "bytes",
                     _producer("export_all", {"columns": list(COLUMNS)}),
                     {"encoding": "tsv", "columns": list(COLUMNS), "rows": len(pics),
                      "digest": "sha1 of the displayed RGBA8 (tsuki.image.decode_image(...).rgba)"})
    part.count("pictures", len(pics))
    part.count("processes", procs)
    part.count("seconds", round(seconds, 1))
    part.count("displayed_rgba_bytes", sum(4 * r.area for r in pics))
    if log is not None:
        log(f"[image] all: {len(pics):,} pictures hashed in {seconds:.1f} s on {procs} processes")


def run(root: Path, game: Path, *, parts: Optional[Sequence[str]] = None, quick: bool = False,
        log: Optional[Callable[[str], None]] = None) -> dict[str, Any]:
    """Entry point used by ``oracle.run``.  Default: both parts; ``quick`` (or no ``all`` in ``parts``)
    leaves out the nightly sweep.  ``TSUKI_ORACLE_PROCS`` sets the process count."""
    sel = list(parts) if parts else (["sha1"] if quick else list(PARTS))
    bad = [p for p in sel if p not in PARTS]
    if bad:
        raise ValueError(f"unknown parts {bad}; choose from {list(PARTS)}")
    procs = int(os.environ.get("TSUKI_ORACLE_PROCS", "0")) or max(1, (os.cpu_count() or 4) - 2)
    gen = _producer("run", {"parts": sel, "game": Path(game).as_posix(), "procs": procs})
    area = AreaWriter(Path(root), AREA, gen, description=DESCRIPTION, game_dir=Path(game))
    t0 = time.perf_counter()
    pics = census(Path(game), log)
    if log is not None:
        log(f"[image] census: {len(pics):,} pictures ({time.perf_counter() - t0:.1f} s)")
    for name in sel:
        pw = area.part(name, "2,000 stratified pictures" if name == "sha1" else "every image MZP")
        if log is not None:
            log(f"[image] {name} ...")
        (export_sha1 if name == "sha1" else export_all)(pw, Path(game), pics, procs, log)
        pw.finish()
        if log is not None:
            log(f"[image] {name}: {len(pw.files)} files, "
                f"{sum(1 for c in pw.checks if not c.ok)} failed checks of {len(pw.checks)}")
    return area.finish()


def main(argv: Optional[Sequence[str]] = None) -> int:
    """``python -m oracle.image [runner options]``: the runner with this area registered."""
    from . import run as runner
    runner.AREAS.setdefault(AREA, MODULE)
    return runner.main([AREA, *(sys.argv[1:] if argv is None else argv)])


if __name__ == "__main__":
    raise SystemExit(main())
