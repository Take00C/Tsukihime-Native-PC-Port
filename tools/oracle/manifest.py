"""Golden-data areas: writer, manifest schema and verification.

Layout of one area (``samples/golden/<area>/``)::

    manifest.json              area manifest: every file of every part (the entry point)
    <part>/manifest.json       the same records restricted to one part (written by that part)
    <part>/<files>             raw little-endian binaries and JSON headers

Manifest (``schema`` = ``tsuki-golden-manifest/1``), all paths POSIX and relative to the
area directory::

    {
      "schema", "area", "description", "created_utc",
      "generator": {module, function, args, python, numpy, platform, ffmpeg,
                    tool_commit (null until the repository exists), tool_digest, tool_sources,
                    tool_used, reads_tracked (part manifests only)},
      "game": {"dir", "eboot": {"size", "sha256"}},
      "conventions": {format name -> description},
      "parts": {name: {"manifest", "description", "files", "bytes", "created_utc"}},
      "files": [{"path", "part", "size", "sha1", "format",
                 "produced_by": {"module", "function", "args", "uses"}, "meta"}],
      "inputs": [{"path", "size", "sha256" | null, "hash": "sha256" | "size-only"}],
      "input_entries": [{"archive", "index", "name", "offset", "size", "sha1"}],
      "counts": {name: value},
      "checks": [{"name", "expected", "actual", "ok", "source"}],
      "findings": [{"name", "detail", "data"}]
    }

``checks`` are hard cross-checks of the exported data against the documents (a failed check
fails :func:`verify_area`); ``findings`` record discrepancies that are not errors of the data,
e.g. a reference-library constant that differs from the eboot bytes the export used.

``inputs`` are the files an export read.  ``path`` has one of three forms:

* relative to the game folder (``image/allscr.mrg``, ``eboot.bin``): a file of the user's
  CUSA47999 folder (``game.dir`` names the folder used);
* ``project:<path relative to the repository root>`` (e.g.
  ``project:samples/misc_layer_state_and_motions/motion_loops.json``): a file of this repository
  the export depends on, such as a research result or a spike's selection list.  The prefix keeps
  these apart from game files; resolve them against the repository root
  (``TOOLS_ROOT.parent``);
* an absolute POSIX path: anything else (not used by the current exporters).

Small files are hashed with SHA-256; the multi-GB archives are recorded by size only
(``"hash": "size-only"``, ``sha256`` null) and every entry actually read from them is listed in
``input_entries`` with the SHA-1 of its stored bytes.  ``archive`` names the archive file in the
game's ``image/`` folder in one of two forms: a stem (``allpac0``, ``allscr``, ``vse``: the slice
and audio areas), whose bytes are in ``image/<archive>.mrg``, or a file name that already ends
in ``.mrg`` (``allscr.mrg``, ``ft_en.mrg``: the image area), which is used as it is
(:func:`entry_archive`).  ``offset`` is the byte offset of the stored bytes in that file and
``size`` their length, so an entry can be re-read without the archive readers.

When a part is written inside :meth:`PartWriter.track_reads` (``generator.reads_tracked`` true),
its ``inputs`` also list every file of the game folder or of the repository (not Python code,
not the virtual environment, not the area being written) that the exporting process opened for
reading while the part ran, so a part's inputs do not depend on the exporter remembering to
record them.

Provenance (``generator``): ``tool_sources`` maps every Python source of tools/tsuki,
tools/eboot and tools/oracle (path relative to tools/) to its SHA-1 at export time;
``tool_digest`` is the SHA-1 over that map; ``tool_used`` lists the sources that were loaded in
the exporting process when the part manifest was written: the modules the export can have used
(an over-approximation, never an under-approximation).  An exporter that runs each part in its
own process (``oracle.export_slice`` under ``oracle.run``) makes this the modules of that one part;
in a process that exported several parts it also lists what the earlier parts loaded.  The area
manifest's ``tool_used`` is the union of its parts' lists.  Part manifests carry their own
generator, so an area whose parts were exported at different times keeps the provenance of each
part.

:func:`verify_area` re-hashes every file and reports missing, changed and stray files and failed
checks.  :func:`provenance_problems` reports drift: a used tool source that changed since its
part was exported (``tool_used``), and game or project inputs and archive entries whose size or
hash no longer match.  Manifests written before ``tool_used`` existed cannot say which of the
recorded sources an export used; their changed sources are listed as notes, not problems,
unless ``strict_legacy`` is set (runner: ``--strict-provenance``).  ``python -m oracle.run <area>
--verify`` runs both.
"""
from __future__ import annotations

import contextlib
import datetime as _dt
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Iterator, Mapping, Optional, Union

import numpy as np

from . import SCHEMA, TOOLS_ROOT

__all__ = [
    "MANIFEST_NAME", "FORMATS", "Producer", "FileRecord", "Check", "AreaWriter", "PartWriter",
    "ManifestError", "dump_json", "f32_json", "f32_bits", "sha1_bytes", "sha1_file", "sha256_file",
    "tool_sources", "tool_used", "ffmpeg_version", "load_manifest", "verify_area", "provenance_problems",
    "resolve_input", "entry_archive", "SIZE_ONLY_LIMIT",
]

MANIFEST_NAME = "manifest.json"
SIZE_ONLY_LIMIT = 64 * 1024 * 1024     # input files larger than this are recorded by size only
_CHUNK = 8 * 1024 * 1024

Chunk = Union[bytes, bytearray, memoryview]

FORMATS: dict[str, str] = {
    "json": "UTF-8 JSON without BOM. A float that is an engine float32 value is written as "
            "{\"value\": decimal, \"bits\": IEEE-754 binary32 pattern as an unsigned integer}; "
            "byte strings are lower-case hex.",
    "bytes": "Opaque bytes exactly as decoded (e.g. a script section after MZX0 and XOR 0xFF).",
    "rgba8-tiles": "8-bit RGBA, straight (non-premultiplied) alpha, row-major. The stored tiles of "
                   "one picture (tile_w x tile_h, border included) one after another in tile "
                   "order; empty tiles (flag 0) are absent. Offsets and copy rectangles are in "
                   "the picture's JSON header.",
    "a8-pages": "8-bit coverage (alpha 0..255), row-major. Font pages one after another; "
                "offsets and sizes are in the JSON header.",
    "f32le": "32-bit IEEE float little-endian PCM, full scale 1.0, not clipped, channels "
             "interleaved (frames x channels).",
}


class ManifestError(ValueError):
    pass


# ---------------------------------------------------------------------------
# hashing and JSON helpers
# ---------------------------------------------------------------------------

def sha1_bytes(data: Chunk) -> str:
    return hashlib.sha1(data).hexdigest()


def _hash_file(path: Path, algo: str) -> str:
    h = hashlib.new(algo)
    with open(path, "rb") as f:
        while True:
            b = f.read(_CHUNK)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def sha1_file(path: Path) -> str:
    return _hash_file(path, "sha1")


def sha256_file(path: Path) -> str:
    return _hash_file(path, "sha256")


F32Like = Union[float, np.floating[Any]]


def f32_bits(x: F32Like) -> int:
    """IEEE-754 binary32 bit pattern of ``x`` (which must already be a float32 value)."""
    v = np.float32(x)
    if float(v) != float(x) and not (np.isnan(v) and np.isnan(x)):
        raise ManifestError(f"{x!r} is not exactly representable as float32")
    return int(np.array([v], dtype="<f4").view("<u4")[0])


def f32_json(x: F32Like) -> dict[str, Any]:
    """A float32 engine value for JSON: the decimal value and its bit pattern."""
    return {"value": float(np.float32(x)), "bits": f32_bits(x)}


def _json_default(o: object) -> Any:
    if isinstance(o, np.integer):
        return int(o)
    if isinstance(o, np.floating):
        return float(o)
    if isinstance(o, (bytes, bytearray, memoryview)):
        return bytes(o).hex()
    if isinstance(o, Path):
        return o.as_posix()
    if isinstance(o, (set, frozenset)):
        return sorted(o)
    raise TypeError(f"not JSON serialisable: {type(o).__name__}")


def dump_json(obj: Any) -> bytes:
    """Deterministic UTF-8 JSON (key order as built, one-space indent, no NaN)."""
    return (json.dumps(obj, ensure_ascii=False, indent=1, allow_nan=False, default=_json_default)
            + "\n").encode("utf-8")


def _utc_now() -> str:
    return _dt.datetime.now(_dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


# ---------------------------------------------------------------------------
# provenance
# ---------------------------------------------------------------------------

def tool_sources() -> dict[str, str]:
    """SHA-1 of every Python source the exporters can use (tools/tsuki, tools/eboot,
    tools/oracle), keyed by the path relative to tools/.  Stands in for the tool commit until
    the repository exists."""
    out: dict[str, str] = {}
    for pkg in _TOOL_PACKAGES:
        d = TOOLS_ROOT / pkg
        for p in sorted(d.glob("*.py")):
            out[f"{pkg}/{p.name}"] = sha1_file(p)
    return out


_TOOL_PACKAGES = ("tsuki", "eboot", "oracle")


def tool_used() -> list[str]:
    """Keys of :func:`tool_sources` (``pkg/module.py``) of the tool modules loaded in this
    process: what an export written now can have used."""
    out: set[str] = set()
    root = TOOLS_ROOT.resolve()
    for mod in list(sys.modules.values()):
        f = getattr(mod, "__file__", None)
        if not f:
            continue
        p = Path(f).resolve()
        if p.suffix != ".py" or not p.is_relative_to(root):
            continue
        rel = p.relative_to(root)
        if len(rel.parts) == 2 and rel.parts[0] in _TOOL_PACKAGES:
            out.add(rel.as_posix())
    return sorted(out)


# Files opened for reading while a part tracks its reads (PartWriter.track_reads), observed with
# the interpreter's "open" audit event (builtins.open, io.open, os.open; numpy.memmap opens
# through open as well).  Audit hooks cannot be removed, so one hook is installed on first use
# and does nothing while no part is tracking.
_TRACKING: list["PartWriter"] = []
_HOOK_INSTALLED = False
_WRITE_FLAGS = os.O_WRONLY | os.O_RDWR | os.O_APPEND | os.O_CREAT | os.O_TRUNC


def _norm(p: str) -> str:
    return os.path.normcase(os.path.abspath(p))


_REPO = _norm(str(TOOLS_ROOT.parent))
_SKIP_DIRS = tuple(_norm(str(TOOLS_ROOT.parent / d)) + os.sep for d in (".venv", ".git"))
_CODE_SUFFIXES = (".py", ".pyc", ".pyd", ".pyi")


def _audit(event: str, args: tuple[Any, ...]) -> None:
    if event != "open" or not _TRACKING:
        return
    try:
        path, _mode, flags = args
        if isinstance(path, int) or (isinstance(flags, int) and flags & _WRITE_FLAGS):
            return
        raw = os.fsdecode(path)
        norm = _norm(raw)
        for pw in _TRACKING:
            pw._saw(norm, raw)
    except Exception:                             # an audit hook must never break the open itself
        return


def _install_hook() -> None:
    global _HOOK_INSTALLED
    if not _HOOK_INSTALLED:
        sys.addaudithook(_audit)
        _HOOK_INSTALLED = True


def ffmpeg_version() -> Optional[str]:
    exe = os.environ.get("TSUKI_FFMPEG") or shutil.which("ffmpeg")
    if exe is None:
        return None
    p = subprocess.run([exe, "-version"], capture_output=True, text=True, check=False)
    if p.returncode != 0 or not p.stdout:
        return None
    return p.stdout.splitlines()[0].strip()


def _environment() -> dict[str, Any]:
    src = tool_sources()
    digest = hashlib.sha1("".join(f"{k}\0{v}\n" for k, v in sorted(src.items())).encode()).hexdigest()
    return {
        "python": sys.version.split()[0],
        "numpy": np.__version__,
        "platform": platform.platform(),
        "ffmpeg": ffmpeg_version(),
        "tool_commit": None,
        "tool_digest": digest,
        "tool_sources": src,
    }


# ---------------------------------------------------------------------------
# records
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Producer:
    """How a file was made: the oracle function, its arguments and the reference-library
    calls it is built on (``uses``)."""
    module: str
    function: str
    args: Mapping[str, Any] = field(default_factory=dict)
    uses: tuple[str, ...] = ()

    def to_json(self) -> dict[str, Any]:
        return {"module": self.module, "function": self.function, "args": dict(self.args),
                "uses": list(self.uses)}


@dataclass(frozen=True)
class FileRecord:
    path: str              # relative to the area directory, POSIX
    part: str
    size: int
    sha1: str
    format: str
    producer: Producer
    meta: Mapping[str, Any] = field(default_factory=dict)

    def to_json(self) -> dict[str, Any]:
        return {"path": self.path, "part": self.part, "size": self.size, "sha1": self.sha1,
                "format": self.format, "produced_by": self.producer.to_json(), "meta": dict(self.meta)}


@dataclass(frozen=True)
class Check:
    name: str
    expected: Any
    actual: Any
    ok: bool
    source: str            # the document / statement the expectation comes from

    def to_json(self) -> dict[str, Any]:
        return {"name": self.name, "expected": self.expected, "actual": self.actual, "ok": self.ok,
                "source": self.source}


# ---------------------------------------------------------------------------
# writers
# ---------------------------------------------------------------------------

class AreaWriter:
    """Writes one area.  Parts are written through :meth:`part`; :meth:`finish` writes the area
    manifest from the part manifests on disk, so re-exporting a subset of the parts keeps the
    others."""

    def __init__(self, root: Path, area: str, generator: Producer, *, description: str = "",
                 game_dir: Optional[Path] = None) -> None:
        if not area or "/" in area or "\\" in area:
            raise ManifestError(f"bad area name {area!r}")
        self.area = area
        self.dir = Path(root) / area
        self.generator = generator
        self.description = description
        self.game_dir = Path(game_dir) if game_dir is not None else None
        self.environment = _environment()
        self.created = _utc_now()
        self._input_cache: dict[Path, dict[str, Any]] = {}
        self.parts: dict[str, PartWriter] = {}
        self.dir.mkdir(parents=True, exist_ok=True)

    def part(self, name: str, description: str = "") -> "PartWriter":
        if name in self.parts:
            raise ManifestError(f"part {name!r} opened twice")
        if not name or "/" in name or "\\" in name or name.startswith("."):
            raise ManifestError(f"bad part name {name!r}")
        pw = PartWriter(self, name, description)
        self.parts[name] = pw
        return pw

    # -- inputs ----------------------------------------------------------------
    def input_path(self, path: Path) -> str:
        """The ``inputs`` path of a file (module docstring: game-relative, ``project:`` or absolute)."""
        path = Path(os.path.abspath(path))
        if self.game_dir is not None and path.is_relative_to(Path(os.path.abspath(self.game_dir))):
            return path.relative_to(Path(os.path.abspath(self.game_dir))).as_posix()
        if path.is_relative_to(TOOLS_ROOT.parent):
            return "project:" + path.relative_to(TOOLS_ROOT.parent).as_posix()
        return path.as_posix()

    def _input_record(self, path: Path, full_hash: Optional[bool]) -> dict[str, Any]:
        path = Path(path)
        key = Path(_norm(str(path)))
        rec = self._input_cache.get(key)
        if rec is not None:
            return rec
        size = path.stat().st_size
        do_hash = size <= SIZE_ONLY_LIMIT if full_hash is None else full_hash
        rec = {"path": self.input_path(path), "size": size, "sha256": sha256_file(path) if do_hash else None,
               "hash": "sha256" if do_hash else "size-only"}
        self._input_cache[key] = rec
        return rec

    def game_json(self) -> dict[str, Any]:
        out: dict[str, Any] = {"dir": self.game_dir.as_posix() if self.game_dir else None}
        if self.game_dir is not None and (self.game_dir / "eboot.bin").exists():
            r = self._input_record(self.game_dir / "eboot.bin", True)
            out["eboot"] = {"size": r["size"], "sha256": r["sha256"]}
        return out

    # -- finish ----------------------------------------------------------------
    def finish(self) -> dict[str, Any]:
        for name, pw in self.parts.items():
            if not pw.finished:
                raise ManifestError(f"part {name!r} was not finished")
        parts: dict[str, Any] = {}
        files: list[dict[str, Any]] = []
        inputs: dict[str, dict[str, Any]] = {}
        entries: dict[tuple[str, int], dict[str, Any]] = {}
        counts: dict[str, Any] = {}
        checks: list[dict[str, Any]] = []
        findings: list[dict[str, Any]] = []
        used: Optional[set[str]] = set()
        for sub in sorted(p for p in self.dir.iterdir() if p.is_dir()):
            mpath = sub / MANIFEST_NAME
            if not mpath.exists():
                continue
            pm = load_manifest(mpath)
            name = sub.name
            pu = (pm.get("generator") or {}).get("tool_used")
            if used is not None and pu is not None:
                used.update(pu)
            else:
                used = None                       # a part without tool_used: fall back to this process
            parts[name] = {"manifest": f"{name}/{MANIFEST_NAME}", "description": pm.get("description", ""),
                           "files": len(pm["files"]), "bytes": sum(int(f["size"]) for f in pm["files"]),
                           "created_utc": pm.get("created_utc")}
            files.extend(pm["files"])
            for r in pm["inputs"]:
                old = inputs.get(r["path"])
                if old is not None and old != r:
                    raise ManifestError(f"input {r['path']} recorded differently by two parts")
                inputs[r["path"]] = r
            for r in pm["input_entries"]:
                entries[(r["archive"], int(r["index"]))] = r
            for k, v in pm["counts"].items():
                key = f"{name}.{k}"
                counts[key] = v
            checks.extend(dict(c, name=f"{name}.{c['name']}") for c in pm["checks"])
            findings.extend(dict(f, name=f"{name}.{f['name']}") for f in pm.get("findings", []))
        man = {
            "schema": SCHEMA,
            "area": self.area,
            "description": self.description,
            "created_utc": self.created,
            "generator": dict(self.generator.to_json(), **self.environment,
                              tool_used=sorted(used) if used else tool_used()),
            "game": self.game_json(),
            "conventions": FORMATS,
            "parts": parts,
            "files": sorted(files, key=lambda f: f["path"]),
            "inputs": sorted(inputs.values(), key=lambda r: r["path"]),
            "input_entries": sorted(entries.values(), key=lambda r: (r["archive"], int(r["index"]))),
            "counts": counts,
            "checks": checks,
            "findings": findings,
        }
        _write_atomic(self.dir / MANIFEST_NAME, [dump_json(man)])
        return man


class PartWriter:
    """Writes the files of one part into ``<area>/<name>/`` (the directory is emptied first)."""

    def __init__(self, area: AreaWriter, name: str, description: str) -> None:
        self.area = area
        self.name = name
        self.description = description
        self.dir = area.dir / name
        self.files: list[FileRecord] = []
        self.inputs: dict[str, dict[str, Any]] = {}
        self.entries: dict[tuple[str, int], dict[str, Any]] = {}
        self.counts: dict[str, Any] = {}
        self.checks: list[Check] = []
        self.findings: list[dict[str, Any]] = []
        self.finished = False
        self.created = _utc_now()
        self._reads: dict[str, str] = {}             # normalised path -> path as opened
        self._tracked = False
        self.dir.mkdir(parents=True, exist_ok=True)
        for p in self.dir.iterdir():                 # stale files of an earlier export
            if p.is_file():
                p.unlink()
            else:
                raise ManifestError(f"unexpected directory {p} inside part {name!r}")

    # -- files -------------------------------------------------------------------
    def _rel(self, rel: str) -> str:
        if not rel or rel.startswith("/") or "\\" in rel or ".." in rel.split("/") or "/" in rel:
            raise ManifestError(f"bad file name {rel!r} (parts are flat)")
        if rel == MANIFEST_NAME or rel.endswith(".tmp"):
            raise ManifestError(f"reserved file name {rel!r}")
        return rel

    def write_chunks(self, rel: str, chunks: Iterable[Chunk], fmt: str, producer: Producer,
                     meta: Optional[Mapping[str, Any]] = None) -> FileRecord:
        if fmt not in FORMATS:
            raise ManifestError(f"unknown format {fmt!r}")
        rel = self._rel(rel)
        if any(f.path == f"{self.name}/{rel}" for f in self.files):
            raise ManifestError(f"{self.name}/{rel} written twice")
        size, digest = _write_atomic(self.dir / rel, chunks)
        rec = FileRecord(f"{self.name}/{rel}", self.name, size, digest, fmt, producer, dict(meta or {}))
        self.files.append(rec)
        return rec

    def write_bytes(self, rel: str, data: Chunk, fmt: str, producer: Producer,
                    meta: Optional[Mapping[str, Any]] = None) -> FileRecord:
        return self.write_chunks(rel, [data], fmt, producer, meta)

    def write_json(self, rel: str, obj: Any, producer: Producer,
                   meta: Optional[Mapping[str, Any]] = None) -> FileRecord:
        return self.write_chunks(rel, [dump_json(obj)], "json", producer, meta)

    # -- provenance --------------------------------------------------------------
    @contextlib.contextmanager
    def track_reads(self) -> Iterator["PartWriter"]:
        """While the block runs, every file of the game folder or of the repository that this
        process opens for reading becomes an input of the part (recorded by :meth:`finish`, with
        the default hashing, unless the exporter recorded it itself).  Python code, the virtual
        environment, .git and the area being written are left out."""
        _install_hook()
        self._tracked = True
        _TRACKING.append(self)
        try:
            yield self
        finally:
            _TRACKING.remove(self)

    def _saw(self, norm: str, raw: str) -> None:
        if norm in self._reads or norm.endswith(_CODE_SUFFIXES):
            return
        game = self.area.game_dir
        if game is not None and norm.startswith(_norm(str(game)) + os.sep):
            self._reads[norm] = raw
            return
        if not norm.startswith(_REPO + os.sep) or norm.startswith(_SKIP_DIRS) or f"{os.sep}__pycache__{os.sep}" in norm:
            return
        if norm.startswith(_norm(str(self.area.dir)) + os.sep):
            return
        self._reads[norm] = raw

    def add_input(self, path: Path, *, full_hash: Optional[bool] = None) -> dict[str, Any]:
        """Record a game file this part read (hashed in full unless it is bigger than
        :data:`SIZE_ONLY_LIMIT` or ``full_hash`` says otherwise)."""
        rec = self.area._input_record(Path(path), full_hash)
        self.inputs[rec["path"]] = rec
        return rec

    def add_entry(self, archive: str, index: int, name: Optional[str], offset: int, data: Chunk) -> dict[str, Any]:
        """Record one archive entry read by this part (SHA-1 of its stored bytes)."""
        rec = {"archive": archive, "index": int(index), "name": name, "offset": int(offset),
               "size": len(data), "sha1": sha1_bytes(data)}
        old = self.entries.get((archive, int(index)))
        if old is not None and old != rec:
            raise ManifestError(f"entry {archive}#{index} recorded with different bytes")
        self.entries[(archive, int(index))] = rec
        return rec

    def count(self, name: str, value: Any) -> None:
        self.counts[name] = value

    def check(self, name: str, expected: Any, actual: Any, source: str) -> bool:
        """A hard cross-check of the exported data; a failure makes :func:`verify_area` fail."""
        ok = _jsonable(expected) == _jsonable(actual)
        self.checks.append(Check(name, _jsonable(expected), _jsonable(actual), ok, source))
        return ok

    def finding(self, name: str, detail: str, data: Any = None) -> None:
        """Something the export found that is not an error of the exported data (e.g. a
        reference-library constant that disagrees with the eboot bytes it was typed from)."""
        self.findings.append({"name": name, "detail": detail, "data": _jsonable(data)})

    # -- finish ------------------------------------------------------------------
    def finish(self) -> dict[str, Any]:
        if self.finished:
            raise ManifestError(f"part {self.name!r} finished twice")
        for norm in sorted(self._reads):
            p = Path(self._reads[norm])
            if self.area.input_path(p) not in self.inputs and p.is_file():
                self.add_input(p)
        man = {
            "schema": SCHEMA,
            "area": f"{self.area.area}/{self.name}",
            "description": self.description,
            "created_utc": self.created,
            "generator": dict(self.area.generator.to_json(), **self.area.environment, tool_used=tool_used(),
                              reads_tracked=self._tracked),
            "game": self.area.game_json(),
            "conventions": {k: FORMATS[k] for k in sorted({f.format for f in self.files})},
            "files": [f.to_json() for f in sorted(self.files, key=lambda f: f.path)],
            "inputs": sorted(self.inputs.values(), key=lambda r: r["path"]),
            "input_entries": sorted(self.entries.values(), key=lambda r: (r["archive"], r["index"])),
            "counts": self.counts,
            "checks": [c.to_json() for c in self.checks],
            "findings": self.findings,
        }
        _write_atomic(self.dir / MANIFEST_NAME, [dump_json(man)])
        self.finished = True
        return man


def _jsonable(x: Any) -> Any:
    """Normalise a value the way it will appear in the manifest (tuples -> lists ...)."""
    return json.loads(dump_json(x))


def _write_atomic(path: Path, chunks: Iterable[Chunk]) -> tuple[int, str]:
    tmp = path.with_name(path.name + ".tmp")
    h = hashlib.sha1()
    size = 0
    with open(tmp, "wb") as f:
        for c in chunks:
            mv = memoryview(c).cast("B")
            h.update(mv)
            f.write(mv)
            size += mv.nbytes
    os.replace(tmp, path)
    return size, h.hexdigest()


# ---------------------------------------------------------------------------
# reading and verification
# ---------------------------------------------------------------------------

_REQUIRED = ("schema", "area", "files", "inputs", "input_entries", "counts", "checks")


def load_manifest(path: Path) -> dict[str, Any]:
    with open(path, "rb") as f:
        man = json.loads(f.read().decode("utf-8"))
    if not isinstance(man, dict):
        raise ManifestError(f"{path}: not a JSON object")
    if man.get("schema") != SCHEMA:
        raise ManifestError(f"{path}: schema {man.get('schema')!r}, expected {SCHEMA!r}")
    missing = [k for k in _REQUIRED if k not in man]
    if missing:
        raise ManifestError(f"{path}: missing keys {missing}")
    return man


def _walk(d: Path) -> Iterator[Path]:
    for p in sorted(d.rglob("*")):
        if p.is_file():
            yield p


def verify_area(area_dir: Path, *, hash_files: bool = True) -> list[str]:
    """Problems found in an exported area (empty list = consistent): every listed file exists
    with its size and SHA-1, every part manifest agrees with the area manifest, no unlisted
    file is present, and every recorded cross-check passed."""
    area_dir = Path(area_dir)
    problems: list[str] = []
    mpath = area_dir / MANIFEST_NAME
    if not mpath.exists():
        return [f"{mpath} does not exist"]
    try:
        man = load_manifest(mpath)
    except (ManifestError, json.JSONDecodeError, UnicodeDecodeError) as e:
        return [str(e)]
    listed: dict[str, dict[str, Any]] = {}
    for f in man["files"]:
        path = f["path"]
        if path in listed:
            problems.append(f"{path}: listed twice")
        listed[path] = f
        for k in ("part", "size", "sha1", "format", "produced_by"):
            if k not in f:
                problems.append(f"{path}: record has no {k!r}")
        pb = f.get("produced_by", {})
        if not (pb.get("module") and pb.get("function") and isinstance(pb.get("args"), dict)):
            problems.append(f"{path}: produced_by lacks module / function / args")
        p = area_dir / path
        if not p.is_file():
            problems.append(f"{path}: missing")
            continue
        size = p.stat().st_size
        if size != f.get("size"):
            problems.append(f"{path}: size {size} != manifest {f.get('size')}")
            continue
        if hash_files:
            d = sha1_file(p)
            if d != f.get("sha1"):
                problems.append(f"{path}: SHA-1 {d} != manifest {f.get('sha1')}")
    for name, info in man.get("parts", {}).items():
        pp = area_dir / info["manifest"]
        if not pp.is_file():
            problems.append(f"part {name}: {info['manifest']} missing")
            continue
        try:
            pm = load_manifest(pp)
        except (ManifestError, json.JSONDecodeError, UnicodeDecodeError) as e:
            problems.append(str(e))
            continue
        mine = sorted((f["path"], f["sha1"]) for f in man["files"] if f.get("part") == name)
        theirs = sorted((f["path"], f["sha1"]) for f in pm["files"])
        if mine != theirs:
            problems.append(f"part {name}: part manifest lists different files than the area manifest")
    manifests = {MANIFEST_NAME} | {info["manifest"] for info in man.get("parts", {}).values()}
    for p in _walk(area_dir):
        rel = p.relative_to(area_dir).as_posix()
        if rel not in listed and rel not in manifests:
            problems.append(f"{rel}: not listed in the manifest")
    for c in man["checks"]:
        if not c.get("ok"):
            problems.append(f"check {c.get('name')} failed: expected {c.get('expected')!r}, got {c.get('actual')!r}")
    return problems


# ---------------------------------------------------------------------------
# provenance: tool-source and input drift
# ---------------------------------------------------------------------------

PROJECT_PREFIX = "project:"


def resolve_input(rel: str, game_dir: Optional[Path]) -> Optional[Path]:
    """The file an ``inputs`` path names on this machine (module docstring: game-relative,
    ``project:`` or absolute); None when it is game-relative and no game folder is known."""
    if rel.startswith(PROJECT_PREFIX):
        return TOOLS_ROOT.parent / rel[len(PROJECT_PREFIX):]
    p = Path(rel)
    if p.is_absolute() or (len(rel) >= 3 and rel[1] == ":" and rel[2] in "/\\"):
        return p
    return None if game_dir is None else Path(game_dir) / rel


def entry_archive(archive: str) -> str:
    """The game-relative file of an ``input_entries`` archive name (module docstring): a name
    that already ends in ``.mrg`` is the file name itself, any other name is a stem of
    ``image/<archive>.mrg``."""
    return f"image/{archive}" if archive.lower().endswith(".mrg") else f"image/{archive}.mrg"


def _short(h: Optional[str]) -> str:
    return h[:12] if h else "none"


def _game_for(man: dict[str, Any], game: Optional[Path]) -> Optional[Path]:
    if game is not None:
        return Path(game) if (Path(game) / "image").is_dir() else None
    d = (man.get("game") or {}).get("dir")
    if d and (Path(d) / "image").is_dir():
        return Path(d)
    try:
        from tsuki import game_dir as _gd
    except ImportError:                           # pragma: no cover - tools/ always has tsuki
        return None
    g = _gd()
    return Path(g) if g is not None and (Path(g) / "image").is_dir() else None


def provenance_problems(area_dir: Path, *, game: Optional[Path] = None, tools: bool = True,
                        inputs: bool = True, hash_inputs: bool = True, strict_legacy: bool = False,
                        notes: Optional[list[str]] = None) -> list[str]:
    """Drift between an exported area and what produced it (empty list = none found).

    * ``tools``: every tool source a part used (its manifest's ``generator.tool_used``) must
      still have the recorded SHA-1.  A manifest without ``tool_used`` (written before it
      existed) cannot say which recorded sources its export used: its changed sources are
      listed in ``notes`` ("unattributed"), and are problems only with ``strict_legacy``;
    * ``inputs``: every input must exist with its size, and its SHA-256 when one is recorded and
      ``hash_inputs`` is true (false: only inputs up to :data:`SIZE_ONLY_LIMIT` are re-hashed);
      every archive entry must still have its SHA-1 at its offset in its archive file
      (:func:`entry_archive`).  Game inputs need the game folder (``game``, else the manifest's
      ``game.dir``, else ``tsuki.game_dir()``); without one they are not checked and a note
      says so.

    Lines that are not problems (what was checked) are appended to ``notes``."""
    area_dir = Path(area_dir)
    note = notes.append if notes is not None else (lambda _s: None)
    mpath = area_dir / MANIFEST_NAME
    try:
        man = load_manifest(mpath)
    except (OSError, ManifestError, json.JSONDecodeError, UnicodeDecodeError) as e:
        return [f"provenance: {e}"]
    problems: list[str] = []
    if tools:
        gens: list[tuple[str, dict[str, Any]]] = []
        for name, info in sorted(man.get("parts", {}).items()):
            try:
                pm = load_manifest(area_dir / info["manifest"])
            except (OSError, ManifestError, json.JSONDecodeError, UnicodeDecodeError):
                continue                          # verify_area reports unreadable part manifests
            gens.append((name, pm.get("generator") or {}))
        if not gens:
            gens.append(("(area)", man.get("generator") or {}))
        current: dict[str, Optional[str]] = {}
        drift: dict[str, tuple[str, Optional[str], list[str]]] = {}
        legacy: dict[str, tuple[str, Optional[str], list[str]]] = {}
        legacy_parts: list[str] = []
        checked: set[str] = set()
        for name, gen in gens:
            recorded = gen.get("tool_sources") or {}
            used = gen.get("tool_used")
            if used is None:
                legacy_parts.append(name)
            keys = sorted(recorded) if used is None else list(used)
            for k in keys:
                checked.add(k)
                if k not in current:
                    f = TOOLS_ROOT / k
                    current[k] = sha1_file(f) if f.is_file() else None
                old = recorded.get(k)
                if old != current[k]:
                    (drift if used is not None or strict_legacy else legacy).setdefault(
                        k, (old or "", current[k], []))[2].append(name)
        for k, (old, new, names) in sorted(drift.items()):
            what = "was removed" if new is None else f"changed ({_short(old)} -> {_short(new)})"
            problems.append(f"drift: tool source {k} {what} since the export of part(s) {', '.join(names)}; "
                            "re-export them")
        note(f"tool sources: {len(checked)} checked, {len(drift)} drifted"
             + (f", {len(legacy)} unattributed" if legacy else ""))
        if legacy_parts and not strict_legacy:
            note(f"part(s) {', '.join(legacy_parts)} predate tool_used: a changed recorded source is a note, not a "
                 "problem (--strict-provenance makes it one; re-exporting records tool_used)")
        for k, (old, new, names) in sorted(legacy.items()):
            what = "was removed" if new is None else f"changed ({_short(old)} -> {_short(new)})"
            note(f"unattributed: tool source {k} {what} since the export of part(s) {', '.join(names)}")
    if inputs:
        g = _game_for(man, game)
        n_in = n_hashed = n_entries = 0
        skipped_game = 0
        for r in man.get("inputs", []):
            rel = str(r.get("path"))
            p = resolve_input(rel, g)
            if p is None:
                skipped_game += 1
                continue
            n_in += 1
            if not p.is_file():
                problems.append(f"drift: input {rel} is missing ({p})")
                continue
            size = p.stat().st_size
            if size != r.get("size"):
                problems.append(f"drift: input {rel} has size {size}, recorded {r.get('size')}")
                continue
            sha = r.get("sha256")
            if sha and (hash_inputs or size <= SIZE_ONLY_LIMIT):
                n_hashed += 1
                d = sha256_file(p)
                if d != sha:
                    problems.append(f"drift: input {rel} has SHA-256 {_short(d)}, recorded {_short(sha)}")
        if g is not None:
            for ent in man.get("input_entries", []):
                arc = g / entry_archive(str(ent["archive"]))
                n_entries += 1
                if not arc.is_file():
                    problems.append(f"drift: archive {entry_archive(str(ent['archive']))} of entry "
                                    f"{ent['archive']}#{ent['index']} is missing")
                    continue
                with open(arc, "rb") as f:
                    f.seek(int(ent["offset"]))
                    b = f.read(int(ent["size"]))
                if len(b) != int(ent["size"]) or sha1_bytes(b) != ent.get("sha1"):
                    problems.append(f"drift: entry {ent['archive']}#{ent['index']} at offset {ent['offset']} no longer "
                                    f"has the recorded {ent['size']} bytes (SHA-1 {_short(ent.get('sha1'))})")
        else:
            skipped_game += len(man.get("input_entries", []))
            note("game inputs and archive entries not checked: game folder not found "
                 "(pass --game or set TSUKI_GAME_DIR)")
        note(f"inputs: {n_in} checked by size, {n_hashed} by SHA-256, {n_entries} archive entries re-read"
             + (f", {skipped_game} not checked" if skipped_game else ""))
    return problems
