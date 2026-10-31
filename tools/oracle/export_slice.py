"""Area ``slice``: golden data of the M2 vertical slice (RUNTIME_REQUIREMENTS 7.3, ROADMAP M0
scope 5), exported from the user's own game folder with the Python reference readers.

Parts (each a directory of ``samples/golden/slice/`` with its own ``manifest.json``):

``eboot``     build identity and the RR 3.3 probes and counts; the eboot-only tables the slice
              needs: catalog records and picture names of the slice pictures, all scene ids,
              the movie table and prologue remap (plus the prologue files' probe data), the
              motions the slice starts, every font lookup / rectangle table, the text-box
              layout and window tables, the config defaults and the compositing tables.
``scripts``   allscr names; per script (2 = ``00_01``, 3 = ``00_02``) the decoded bytes, the
              label table and every statement (segment) with the values of BOTH argument
              readers, plus the executed sequence up to the slice's end (``_PGST(2)`` in
              00_02) with every variable write, the message contexts and the assets used.
``text``      script_text lines 0..7 in ja and en (raw, engine copy, offsets) and the
              expanded ``_ZM`` messages.
``layouts``   the text-box layout of every slice message (engine glyph records, lines, page
              origins and the drawn quads) in ja and en.
``pictures``  every picture the traced statements display (18: the 17 of 00_02 plus BLACK of
              00_01) and the four CAUTION screens of the boot stand-in: stored tiles as raw
              RGBA, a JSON header per picture with tile rectangles, per-tile and whole-picture
              SHA-1s (the SHA-1 of the assembled RGBA equals ``tsuki.image``'s).
``audio``     voices 0x3D00..0x3D08, vse #598 (``MOON_SE03028``) and bgm #33 as float PCM
              (playable range, encoder delay removed) with their loop points.
``fonts``     all 65 pages of ft_ja and ft_en as 8-bit coverage planes.
``retail``    the retail path of RUNTIME_REQUIREMENTS 7.6: START_SCRIPT (script 0, scene 110) through
              ``_JUMP(240,START)`` into ``00_00`` (script 1) up to its first voiced line (offset
              0x2341): both scripts' statements, the executed path across the jump
              (:func:`trace_path`), its pictures (tiles), motions 1..5, voice #16309 and its SEs.
``models``    reference-model traces for M2 acceptance 7 and 8: the SoundModel channel record of
              ``_SEPL(7,1,MOON_SE03028,,`001:8000,,`017:30)`` per frame, every ``_WTTM`` of the
              traced paths (``tsuki.wait_model``), the text reveal of every slice message, and the
              sampled per-frame values of every motion the paths start.
``s4``        spike S4's 154 pictures: SHA-1 of the displayed RGBA (``tsuki.image``) and header
              facts, checked against the spike's own oracle file.

The eboot part's ``catalog.json`` also carries ``seen_records``: the catalog records and
picture-seen bits of the 39 ``_STCG`` names of ``00_01``.

Run: ``python -m oracle.run slice`` (see oracle.run).  Everything goes to the local
``samples/golden/slice/``; nothing here may be committed or shared.
"""
from __future__ import annotations

import dataclasses
import json
import os
import shutil
import struct
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Iterable, Iterator, Optional, Sequence

import numpy as np

from tsuki import audio as taudio
from tsuki import catalog as tcatalog
from tsuki import image as timage
from tsuki import mzx
from tsuki import textbox as tb
from tsuki import vm_vars
from tsuki.catalog_eboot import Eboot as CatEboot
from tsuki.font import FontPages, Font
from tsuki.font_eboot import EbootFontTables, LOOKUP_SPECS, RECORD_SPECS
from tsuki.image_catalog import CatalogEntry, crop_rect, find_catalog, load_catalog
from tsuki.mrg import MrgArchive, parse_hed
from tsuki.mzp import MzpArchive
from tsuki.script import (AllScr, Command, ExprUndefined, Label, LayerTracker, ScriptError,
                          SCRIPT_SECTION_BASE, obj_expr, obj_partial_int, obj_strict_int, picture_key)
from tsuki.script_tables import OBJ_COMMANDS, SCENE_IDS, engine_handler, read_scene_ids
from tsuki.text import LANG_INDEX, ScriptText

from . import TOOLS_ROOT
from .manifest import AreaWriter, ManifestError, PartWriter, Producer, f32_json, sha1_bytes

__all__ = [
    "AREA", "PARTS", "MODULE", "DESCRIPTION", "DEFAULT_SPEC", "EXPORTERS", "SliceSpec", "ScriptRange",
    "SliceContext", "SliceFlowError", "TraceStep", "Trace", "trace_script", "ScriptAnalysis",
    "analyse_script", "statement_json", "export", "run", "export_eboot", "export_scripts", "export_text",
    "export_layouts", "export_pictures", "export_audio", "export_fonts", "picture_tiles", "audio_header",
    "trace_path", "collect_assets", "Assets", "analyse_path", "PathAnalysis", "export_retail", "export_models",
    "export_s4", "sound_trace", "wttm_trace", "reveal_trace", "motion_samples",
]

AREA = "slice"
MODULE = "oracle.export_slice"
PARTS: tuple[str, ...] = ("eboot", "scripts", "text", "layouts", "pictures", "audio", "fonts", "retail", "models",
                          "s4")
DESCRIPTION = ("M2 vertical slice (RUNTIME_REQUIREMENTS 7.3): New Game -> 00_01 -> prologue movie, "
               "then 00_02 (scene 270) through page 1, in ja and en")

# ---------------------------------------------------------------------------
# what the slice touches, and what the documents say about it
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ScriptRange:
    index: int                       # script k = allscr section k + 3
    stop: Optional[str] = None       # raw text of the last statement to run (inclusive); None = END_/RTM_/RTMN


@dataclass(frozen=True)
class SliceSpec:
    """The slice contents.  ``None`` means "derive from the traced statements"."""
    scripts: tuple[ScriptRange, ...] = (ScriptRange(2), ScriptRange(3, "_PGST(2);"))
    text_ids: tuple[int, ...] = tuple(range(8))
    langs: tuple[str, ...] = ("ja", "en")
    pictures: Optional[tuple[str, ...]] = None          # picture keys (IMG1685, IMG0700_06_06, BLACK)
    extra_pictures: tuple[tuple[str, int], ...] = (("allpac3", 71), ("allpac3", 72), ("allpac3", 73),
                                                   ("allpac3", 74))   # CAUTION_{JA,EN,ZC,ZT} (RR 7.2 step 2)
    voices: Optional[tuple[int, ...]] = None            # voice.hed entries
    se_names: Optional[tuple[str, ...]] = None          # vse names (se_nam section 2)
    bgm: tuple[int, ...] = (33,)                       # named by ROADMAP M0 scope 5 / spike S1
    font_langs: Optional[tuple[str, ...]] = None        # None = langs
    motions: Optional[tuple[int, ...]] = None
    movie_ids: Optional[tuple[int, ...]] = None         # _TOMP ids (language-neutral)
    hash_movies: bool = True                           # SHA-256 of the movie files (about 1.9 GB read)
    retail_start: int = 0                              # RR 7.6: START_SCRIPT (scene 110)
    retail_stop: Optional[tuple[int, int]] = (1, 0x2341)   # (script, statement offset): 00_00's first _VPLY
    s4_list: Optional[str] = None                      # None = samples/spikes/s4/oracle.json

    def to_json(self) -> dict[str, Any]:
        return json.loads(json.dumps(dataclasses.asdict(self)))

    @classmethod
    def from_json(cls, d: dict[str, Any]) -> "SliceSpec":
        """The inverse of :meth:`to_json` (JSON lists become tuples again)."""
        def tup(x: Any) -> Any:
            return tuple(tup(v) for v in x) if isinstance(x, list) else x
        kw = {k: tup(v) for k, v in d.items() if k != "scripts"}
        return cls(scripts=tuple(ScriptRange(**r) for r in d["scripts"]), **kw)


DEFAULT_SPEC = SliceSpec()

# RUNTIME_REQUIREMENTS 7.3 / 7.5 and ROADMAP M0 / M2 statements that the export cross-checks.
RR = "RUNTIME_REQUIREMENTS 7.3"
EXPECTED_PICTURES: dict[str, tuple[str, int]] = {
    "IMG0597": ("allpac0", 166), "BLACK": ("allpac0", 0), "IMG1685": ("allpac0", 270),
    "IMG2458": ("allpac0", 358), "IMG2182": ("allpac0", 304), "IMG2629": ("allpac0", 370),
    "IMG2630": ("allpac0", 371), "IMG2631": ("allpac0", 372), "IMG2632": ("allpac0", 373),
    "IMG2633": ("allpac0", 374), "IMG2345": ("allpac0", 353), "IMG2183": ("allpac0", 305),
    "IMG2230": ("allpac0", 315), "IMG0813": ("allpac0", 194), "IMG2232": ("allpac0", 317),
    "IMG0700_06_06": ("allpac0", 920), "IMG2439": ("allpac0", 356), "IMG2163": ("allpac0", 302),
}
EXPECTED_CATALOG = {"IMG1685": 1692, "IMG0700_06_06": 5330}
EXPECTED_VOICES = tuple(range(0x3D00, 0x3D09))                  # 15,616..15,624
EXPECTED_SE = {"MOON_SE03028": 598}
EXPECTED_MOTIONS = (22523, 22524, 22525, 22526)
EXPECTED_BGM_LOOP = {33: (962000, 8257999)}                     # ROADMAP spike S1
EXPECTED_PROLOGUE = (4, 46, 47, 106)                            # RR 7.2 step 3
EXPECTED_EBOOT = (12767802, "c2843415fdfda5ebbc84f4d55c6cca044a7476436cf0cfdfccac83116beeb918")
EXPECTED_COUNTS = {"triplet_entries": 49620, "allscr_sections": 552, "text_lines": 43969,
                   "text_languages": 5, "voices": 16464, "movies": 134}
TRIPLETS = ("allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4", "parts",
            "bgm", "vse", "vag", "voice", "voice2")

# message variables recorded at every _ZM (textbox.md 5.8; C<n> = F[F823 + n] [H])
MESSAGE_VARS = {"F823": 823, "F31": 31, "F32": 32, "F1104": 1104, "F1105": 1105, "F1109": 1109}
MESSAGE_CVARS = {"C846": 846, "C835": 835, "C836": 836}
DRAW_FLAGS = tb.DRAW_ALWAYS | tb.DRAW_OUTLINE      # HUD flags of a message block, no shadow (config +0x28 = 0)


class SliceFlowError(RuntimeError):
    """The traced range contains control flow the linear slice tracer does not model."""


def _producer(function: str, args: Optional[dict[str, Any]] = None, uses: Iterable[str] = ()) -> Producer:
    return Producer(MODULE, function, dict(args or {}), tuple(uses))


def _hex(b: bytes) -> str:
    return bytes(b).hex()


# ---------------------------------------------------------------------------
# game access
# ---------------------------------------------------------------------------

class SliceContext:
    """Open readers on the game folder (read-only), created on first use."""

    def __init__(self, game: Path) -> None:
        self.game = Path(game)
        self.img = self.game / "image"
        self.eboot_path = self.game / "eboot.bin"
        if not self.img.is_dir() or not self.eboot_path.is_file():
            raise FileNotFoundError(f"{self.game} is not a CUSA47999 folder (image/ and eboot.bin)")
        self._allscr: Optional[AllScr] = None
        self._text: Optional[ScriptText] = None
        self._resolver: Optional[tcatalog.ScriptImageResolver] = None
        self._catalog: Optional[list[CatalogEntry]] = None
        self._archives: dict[str, MrgArchive] = {}
        self._banks: dict[str, taudio.AudioBank] = {}
        self._fonts: dict[str, Font] = {}
        self._analyses: dict[tuple[int, Optional[str]], ScriptAnalysis] = {}
        self._paths: dict[tuple[int, tuple[int, int]], PathAnalysis] = {}

    def close(self) -> None:
        for f in self._fonts.values():
            f.close()
        for b in self._banks.values():
            b.close()
        for a in self._archives.values():
            a.close()
        if self._allscr is not None:
            self._allscr.close()
        self._fonts.clear()
        self._banks.clear()
        self._archives.clear()
        self._allscr = None

    def __enter__(self) -> "SliceContext":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    @property
    def allscr(self) -> AllScr:
        if self._allscr is None:
            self._allscr = AllScr(self.img / "allscr.mrg")
        return self._allscr

    @property
    def text(self) -> ScriptText:
        if self._text is None:
            self._text = ScriptText(self.img / "script_text.mrg")
        return self._text

    @property
    def resolver(self) -> tcatalog.ScriptImageResolver:
        if self._resolver is None:
            self._resolver = tcatalog.ScriptImageResolver.from_game(self.game)
        return self._resolver

    @property
    def catalog(self) -> list[CatalogEntry]:
        if self._catalog is None:
            self._catalog = load_catalog(self.eboot_path)
        return self._catalog

    def archive(self, name: str) -> MrgArchive:
        if name not in self._archives:
            self._archives[name] = MrgArchive(self.img / name)
        return self._archives[name]

    def bank(self, name: str) -> taudio.AudioBank:
        if name not in self._banks:
            self._banks[name] = taudio.AudioBank(name, self.img)
        return self._banks[name]

    def font(self, lang: str) -> Font:
        if lang not in self._fonts:
            self._fonts[lang] = Font(lang, game=self.game)
        return self._fonts[lang]

    def analysis(self, r: ScriptRange) -> "ScriptAnalysis":
        key = (r.index, r.stop)
        if key not in self._analyses:
            self._analyses[key] = analyse_script(self, r)
        return self._analyses[key]

    def analyses(self, spec: SliceSpec) -> list["ScriptAnalysis"]:
        return [self.analysis(r) for r in spec.scripts]

    def path(self, start: int, stop: Optional[tuple[int, int]]) -> "PathAnalysis":
        if stop is None:
            raise ManifestError("the spec has no retail path (retail_stop is None)")
        key = (start, (int(stop[0]), int(stop[1])))
        if key not in self._paths:
            self._paths[key] = analyse_path(self, *key)
        return self._paths[key]


# ---------------------------------------------------------------------------
# statements and the linear tracer
# ---------------------------------------------------------------------------

VAR_COMMANDS = frozenset({"WKST", "WKAD", "WKOR", "WKOF", "WKBO", "IF__"})
END_COMMANDS = frozenset({"END_", "RTMN", "RTM_"})          # RTM2 only sets the keep-BGM flag (script.md)
UNMODELLED_FLOW = frozenset({"GOTO", "GOTS", "IFNX", "IFSC", "IFUR", "SELD", "SELA", "SELS", "SELR",
                             "XSTP", "XRSM"})


@dataclass
class TraceStep:
    index: int                                  # statement index in the script
    offset: int
    name: str
    writes: list[tuple[str, int]] = field(default_factory=list)   # (variable, new u16 value)
    goto: Optional[str] = None                  # label taken (IF__ with a true condition)
    vars: Optional[dict[str, int]] = None       # message variables (ZM only)
    script: Optional[int] = None                # script of the statement (trace_path only)
    jump: Optional[dict[str, Any]] = None       # _JUMP taken: {"scene", "script", "label"}


@dataclass
class Trace:
    steps: list[TraceStep]
    end: str                                    # "stop statement" | END_ / RTM_ / RTMN | "end of script"
    state: vm_vars.VarState


def _var(mem: vm_vars.VarMemory, n: int) -> int:
    return mem.peek(vm_vars.VAR_F + 2 * n)


ScriptLoader = Callable[[int], tuple[Sequence[Command], Sequence[Label]]]


def _statement_index(cmds: Sequence[Command], labels: Sequence[Label]) -> tuple[dict[int, int], dict[str, Label]]:
    at: dict[int, int] = {}
    for i, c in enumerate(cmds):
        at.setdefault(c.offset, i)
        if c.name_offset >= 0:
            at.setdefault(c.name_offset, i)
    return at, {lab.name: lab for lab in labels}


def _jump_target(c: Command) -> tuple[int, Optional[str]]:
    """``_JUMP(scene_id [, label])`` (script.md JUMP row, handler VA 0x961DC): the scene id
    (inline reader) and the label, None when absent or empty (= the script start)."""
    label = c.args[1].value if len(c.args) > 1 and c.args[1].value != "" else None
    return c.int_arg(0), label


def _run_trace(load: ScriptLoader, start: int, stop_at: Callable[[int, Command], bool],
               st: vm_vars.VarState, budget: int, scene_script: Optional[Callable[[int], Optional[int]]],
               tag_script: bool) -> tuple[list[TraceStep], str]:
    k = start
    cmds, labels = load(k)
    at, by_label = _statement_index(cmds, labels)
    steps: list[TraceStep] = []
    pc = 0
    end = "end of script"
    while pc < len(cmds):
        if len(steps) >= budget:
            raise SliceFlowError(f"more than {budget} statements: a loop?")
        c = cmds[pc]
        real = c.name_offset >= 0
        is_jump = real and c.key[:1] == "J"         # every name starting with J is JUMP (script.md 3)
        if real and (c.key in UNMODELLED_FLOW or (is_jump and scene_script is None)):
            raise SliceFlowError(f"statement {c.index} at 0x{c.offset:x} ({c.raw}) needs control flow "
                                 "that the slice tracer does not model")
        step = TraceStep(c.index, c.offset, c.name, script=k if tag_script else None)
        if real and c.key in VAR_COMMANDS:
            n0 = len(st.mem.log)
            eff = vm_vars.execute(c.raw_bytes, c.name_offset - c.offset + 1, st)
            step.writes = [(vm_vars.address_name(va), v) for kind, va, v in st.mem.log[n0:] if kind == "w"]
            if eff.goto is not None:
                step.goto = eff.goto.decode("latin-1")
        if c.name == "ZM":
            f823 = _var(st.mem, 823)
            vs = {name: _var(st.mem, n) for name, n in MESSAGE_VARS.items()}
            vs.update({name: _var(st.mem, (n + f823) & 0xFFFF) for name, n in MESSAGE_CVARS.items()})
            step.vars = vs
        target: Optional[tuple[int, Optional[str]]] = None
        if is_jump:
            assert scene_script is not None
            scene, label = _jump_target(c)
            k2 = scene_script(scene)
            if k2 is None:
                raise SliceFlowError(f"statement {c.index} ({c.raw}): no script has scene id {scene}")
            step.jump = {"scene": scene, "script": k2, "label": label}
            target = (k2, label)
        steps.append(step)
        if stop_at(k, c):
            end = "stop statement"
            break
        if real and c.key in END_COMMANDS:
            end = c.key
            break
        if step.goto is not None:
            lab = by_label.get(step.goto)
            if lab is None or lab.offset not in at:
                raise SliceFlowError(f"label {step.goto!r} of statement {c.index} not found")
            pc = at[lab.offset]
            continue
        if target is not None:
            k, label = target
            cmds, labels = load(k)
            at, by_label = _statement_index(cmds, labels)
            if label is None:
                pc = 0
            else:
                lab = by_label.get(label)
                if lab is None or lab.offset not in at:
                    raise SliceFlowError(f"label {label!r} of {c.raw} not found in script {k}")
                pc = at[lab.offset]
            continue
        pc += 1
    return steps, end


def trace_script(cmds: Sequence[Command], labels: Sequence[Label], stop: Optional[str] = None,
                 state: Optional[vm_vars.VarState] = None, budget: int = 1_000_000) -> Trace:
    """Run a script from its first statement the way ScriptStep orders statements, for the
    straight-line part of a scene: variable commands and ``_IF__`` go through
    ``tsuki.vm_vars.execute`` (a true ``_IF__`` jumps to its label), every other command is
    stepped over.  Ends after ``stop`` (raw statement text, inclusive) or at END_ / RTM_ /
    RTMN (return to the title).  Commands whose flow the slice does not need (GOTO, JUMP, choices ...)
    raise :class:`SliceFlowError` instead of being approximated (:func:`trace_path` follows
    JUMP).  All variables start at 0 unless ``state`` is given."""
    st = state if state is not None else vm_vars.VarState()
    steps, end = _run_trace(lambda _k: (cmds, labels), 0,
                            lambda _k, c: stop is not None and c.raw == stop, st, budget, None, False)
    if stop is not None and end != "stop statement":
        raise SliceFlowError(f"stop statement {stop!r} not reached ({end})")
    return Trace(steps, end, st)


def trace_path(load: ScriptLoader, start: int, stop: tuple[int, int], *, state: Optional[vm_vars.VarState] = None,
               scene_ids: Sequence[int] = SCENE_IDS, budget: int = 1_000_000) -> Trace:
    """:func:`trace_script` across scripts: ``_JUMP(scene [, label])`` loads the script whose
    scene id is ``scene`` (``scene_ids[k]`` = scene id of script k; the engine's linear search,
    script.md 6.1) and continues at ``label`` (marker statement included) or at statement 0.
    Variables carry over (one VarState).  Not modelled: the scene-transition edge code of the
    JUMP handler (VA 0xBA3C0 / 0xBA67F: F[110] = to, edge_seen and F[600..] bits; gallery.md
    2.1).  ``stop`` = (script, statement offset), inclusive.  Every step names its script."""
    st = state if state is not None else vm_vars.VarState()
    by_scene: dict[int, int] = {}
    for i, sid in enumerate(scene_ids):
        by_scene.setdefault(int(sid), i)
    steps, end = _run_trace(load, start, lambda k, c: (k, c.offset) == tuple(stop), st, budget,
                            by_scene.get, True)
    if end != "stop statement":
        raise SliceFlowError(f"stop statement at script {stop[0]} offset 0x{stop[1]:x} not reached ({end})")
    return Trace(steps, end, st)


def _ascii(s: str) -> dict[str, Any]:
    """A statement or argument text for JSON: the string when it is plain ASCII, else hex."""
    if s.isascii():
        return {"text": s}
    return {"hex": s.encode("utf-8", "surrogateescape").hex()}


def statement_json(c: Command) -> dict[str, Any]:
    """One statement with both argument readers (script.md 3, engine 5): the inline reader
    (VA 0x76690 i16 / 0x76780 i32, presence VA 0x76860) at every argument index plus the
    first missing one, and the object parser (VA 0xD690 split, VA 0xDA50 expression,
    VA 0xE310 / 0xE3C0 strtol getters), and ``values``: what this command's handler reads
    (``tsuki.script.Command.value``)."""
    real = c.name_offset >= 0
    h = engine_handler(c.key) if real and c.key else None
    out: dict[str, Any] = {
        "index": c.index, "offset": c.offset, "end": c.end, "name_offset": c.name_offset,
        "key": c.key, "name": c.name, "raw": _ascii(c.raw),
        "handled": c.handled, "handler_va": h[0] if h else None, "frame": h[1] if h else None,
        "reader": "object" if c.key in OBJ_COMMANDS else "inline",
        "args": [_ascii(a.raw) for a in c.args],
        "tags": [a.tag for a in c.args],
        "trailer": _ascii(c.trailer) if c.trailer else None,
        "marker": {"block": c.block, "sub": c.sub} if c.block is not None else None,
        "text_ids": c.text_ids,
    }
    n = max(len(c.args), 1) + 1
    out["inline"] = [{"i16": c.int_arg(i, 16), "i32": c.int_arg(i, 32), "present": c.present(i)}
                     for i in range(n)]
    obj: dict[str, Any] = {"args": [], "error": None}
    try:
        oargs = c.obj_args
    except ScriptError as e:
        obj["error"] = str(e)
        oargs = ()
    for a in oargs:
        rec: dict[str, Any] = {"raw": _ascii(a), "has": a != "", "expr": None, "undefined": None,
                               "strict": obj_strict_int(a), "partial": obj_partial_int(a)}
        if a != "":
            try:
                rec["expr"] = obj_expr(a)
            except ExprUndefined as e:
                rec["undefined"] = str(e)
        obj["args"].append(rec)
    out["object"] = obj
    vals: list[Optional[int]] = []
    if real:
        for i in range(len(c.args)):
            try:
                vals.append(c.value(i))
            except ExprUndefined:
                vals.append(None)
    out["values"] = vals
    return out


@dataclass
class AssetUse:
    script: int
    statement: int
    offset: int
    command: str
    detail: dict[str, Any]

    def to_json(self) -> dict[str, Any]:
        return {"script": self.script, "statement": self.statement, "offset": self.offset,
                "command": self.command, **self.detail}


@dataclass
class Message:
    script: int
    statement: int
    offset: int
    block: Optional[int]
    sub: Optional[int]
    arg: str                       # the raw _ZM argument, e.g. "$000003"
    text_ids: list[int]
    vars: dict[str, int]
    page: int                      # page number within the traced range (0 = first)
    j: int                         # message index on its page
    pgst: Optional[int]            # value of the last _PGST before it
    wnty: int                      # value of the last _WNTY before it (1 = last message of the page)

    @property
    def blank_before(self) -> int:
        return self.vars["C846"]

    def to_json(self) -> dict[str, Any]:
        return {"script": self.script, "statement": self.statement, "offset": self.offset,
                "marker": {"block": self.block, "sub": self.sub}, "arg": self.arg,
                "text_ids": self.text_ids, "page": self.page, "j": self.j, "pgst": self.pgst,
                "wnty": self.wnty, "blank_before": self.blank_before, "vars": self.vars}


@dataclass
class Assets:
    """What the traced statements use, in execution order (:func:`collect_assets`)."""
    messages: list[Message] = field(default_factory=list)
    pictures: list[AssetUse] = field(default_factory=list)
    voices: list[AssetUse] = field(default_factory=list)
    ses: list[AssetUse] = field(default_factory=list)
    motions: list[AssetUse] = field(default_factory=list)
    movies: list[AssetUse] = field(default_factory=list)
    seen: list[AssetUse] = field(default_factory=list)          # _STCG picture-seen names

    def to_json(self) -> dict[str, Any]:
        return {"pictures": [u.to_json() for u in self.pictures], "voices": [u.to_json() for u in self.voices],
                "ses": [u.to_json() for u in self.ses], "motions": [u.to_json() for u in self.motions],
                "movies": [u.to_json() for u in self.movies]}


def collect_assets(steps: Sequence[TraceStep], command: Callable[[TraceStep], tuple[int, Command]]) -> Assets:
    """Pictures (with the layer tracker's ``_hh_vv`` keys), voices, SEs, motions, movies,
    ``_STCG`` names and messages (page / slot from ``_PGST`` / ``_WNTY``) of the executed
    steps; ``command(step)`` = (script, statement)."""
    tracker = LayerTracker()
    out = Assets()
    page, j = 0, 0
    pgst: Optional[int] = None
    wnty = 0
    for step in steps:
        k, c = command(step)
        if c.name_offset < 0:
            continue
        if c.key in ("STCH", "SCH2"):
            key = picture_key(c, tracker.src_name(c))
            if key and not key.lstrip("-").isdigit():
                out.pictures.append(AssetUse(k, c.index, c.offset, c.key,
                                             {"layer": c.value(0), "key": key, "x": c.value(2), "y": c.value(3),
                                              "a9": c.value(9) if len(c.args) > 9 else None,
                                              "a10": c.value(10) if len(c.args) > 10 else None}))
        tracker.update(c)
        if c.key in ("VPLY", "VPL2"):
            vname, vidx = taudio.parse_voice_ref(",".join(a.raw for a in c.args))
            out.voices.append(AssetUse(k, c.index, c.offset, c.key, {"name": vname, "index": vidx}))
        elif c.key == "SEPL" and len(c.args) > 2:
            out.ses.append(AssetUse(k, c.index, c.offset, c.key,
                                    {"channel": c.value(0), "loop": c.value(1), "name": c.args[2].value,
                                     "tags": {str(a.tag): a.value for a in c.args if a.tag is not None}}))
        elif c.key == "STMA":
            out.motions.append(AssetUse(k, c.index, c.offset, c.key, {"layer": c.value(0), "motion": c.value(1)}))
        elif c.key == "TOMP":
            out.movies.append(AssetUse(k, c.index, c.offset, c.key, {"movie": c.value(0)}))
        elif c.key == "STCG" and c.args:
            out.seen.append(AssetUse(k, c.index, c.offset, c.key, {"name": c.args[0].value,
                                                                   "key": tcatalog.script_image_key(c.args[0].value)}))
        elif c.key == "PGST":
            pgst = c.value(0)
        elif c.key == "WNTY":
            wnty = c.int_arg(0)
        elif c.name == "ZM":
            assert step.vars is not None
            out.messages.append(Message(k, c.index, c.offset, c.block, c.sub, c.args[0].raw if c.args else "",
                                        c.text_ids, dict(step.vars), page, j, pgst, wnty))
            j += 1
            if wnty == 1:                     # _WNTY(1): this message is the last of its page
                page, j = page + 1, 0
    return out


@dataclass
class ScriptAnalysis:
    index: int
    name: str
    scene_id: int
    stop: Optional[str]
    raw: bytes
    section_raw: bytes
    commands: list[Command]
    labels: list[Label]
    trace: Trace
    messages: list[Message]
    pictures: list[AssetUse]
    voices: list[AssetUse]
    ses: list[AssetUse]
    motions: list[AssetUse]
    movies: list[AssetUse]
    segments_match: bool
    seen: list[AssetUse] = field(default_factory=list)


def _segments_match(raw: bytes, cmds: Sequence[Command]) -> bool:
    """The VM's own segmentation (vm_vars.segments) gives the same statement boundaries."""
    seg_bounds: list[tuple[int, int]] = []
    pos = 0
    for sg in vm_vars.segments(raw):
        seg_bounds.append((pos, pos + len(sg)))
        pos += len(sg)
    return seg_bounds == [(c.offset, c.end) for c in cmds]


def analyse_script(ctx: SliceContext, r: ScriptRange) -> ScriptAnalysis:
    scr = ctx.allscr
    k = r.index
    raw = scr.raw(k)
    section_raw = bytes(scr.arc.view(k + SCRIPT_SECTION_BASE))
    cmds = scr.commands(k)
    labels = scr.labels(k)
    tr = trace_script(cmds, labels, r.stop)
    segments_match = _segments_match(raw, cmds)
    a = collect_assets(tr.steps, lambda step: (k, cmds[step.index]))
    return ScriptAnalysis(k, scr.names[k], SCENE_IDS[k], r.stop, raw, section_raw, cmds, labels, tr,
                          a.messages, a.pictures, a.voices, a.ses, a.motions, a.movies, segments_match, a.seen)


# ---------------------------------------------------------------------------
# part: eboot
# ---------------------------------------------------------------------------

def _probe(part: PartWriter, name: str, expected: Any, actual: Any) -> bool:
    return part.check(f"probe.{name}", expected, actual, "RUNTIME_REQUIREMENTS 3.3 item 2")


def _ffprobe(path: Path) -> Optional[dict[str, Any]]:
    exe = shutil.which("ffprobe")
    if exe is None:
        return None
    p = subprocess.run([exe, "-v", "error", "-show_entries",
                        "format=duration,size,format_name:stream=index,codec_type,codec_name,profile,"
                        "width,height,r_frame_rate,nb_frames,sample_rate,channels,duration",
                        "-of", "json", str(path)], capture_output=True, text=True, check=False)
    if p.returncode != 0:
        raise RuntimeError(f"ffprobe failed on {path.name}: {p.stderr.strip()}")
    out: dict[str, Any] = json.loads(p.stdout)
    return out


def export_eboot(part: PartWriter, ctx: SliceContext, spec: SliceSpec) -> None:
    from eboot.selfelf import Eboot as SelfEboot
    from eboot.uitext import read_table as read_ui_text
    from tsuki.misc_adv_system_state import CONFIG_FIELDS, read_static_config
    from tsuki.misc_compositing_transitions_shaders import read_engine_tables
    from tsuki.misc_layer_state_and_motions import MotionTable

    eb_path = ctx.eboot_path
    inp = part.add_input(eb_path, full_hash=True)
    # -- build identity, probes, data counts (RR 3.3) ---------------------------------
    data = eb_path.read_bytes()                  # 12.8 MB
    raw_head = data[:0x200]
    sha1_full = sha1_bytes(data)
    part.check("eboot.size", EXPECTED_EBOOT[0], inp["size"], "RUNTIME_REQUIREMENTS 3.3 item 1")
    part.check("eboot.sha256", EXPECTED_EBOOT[1], inp["sha256"], "RUNTIME_REQUIREMENTS 3.3 item 1")
    probes: dict[str, Any] = {}
    se = SelfEboot.open(eb_path)
    try:
        probes["self_magic"] = raw_head[:4].hex()
        _probe(part, "self_magic", "4f153d1d", probes["self_magic"])
        segs = [{"index": s.index, "flags": s.flags, "offset": s.offset, "comp_size": s.comp_size,
                 "dec_size": s.dec_size, "plain": not (s.deflated or s.encrypted)} for s in se.self_segments]
        probes["self_segments"] = segs
        _probe(part, "self_segment_count", 10, len(segs))
        _probe(part, "self_segments_plain", True, all(s["plain"] for s in segs))
        offs = {s["offset"] for s in segs}
        _probe(part, "segment_offsets", [True, True, True], [o in offs for o in (0x2280, 0x32C210, 0x365FA0)])
        hunex = se.read_va(0x2DA510, 22)
        probes["hunex_string"] = hunex.decode("latin-1")
        _probe(part, "hunex_string", "Hunex Consumer Library", probes["hunex_string"])
        ui = read_ui_text(se)
        probes["ui_text_ja_0"] = ui["ja"][0].text
        _probe(part, "ui_text_ja_0", "NULL", probes["ui_text_ja_0"])
        part.count("ui_text_entries", {k: len(v) for k, v in ui.items()})
    finally:
        se.close()
    cat_start, cat_used, cat_empty = find_catalog(data)
    probes["catalog"] = {"file_offset": cat_start, "used": cat_used, "empty": cat_empty}
    _probe(part, "catalog_file_offset", 0x3D78A0, cat_start)
    _probe(part, "catalog_used_empty", [14744, 31], [cat_used, cat_empty])
    cat = ctx.catalog
    _probe(part, "catalog_records_0_1", ["BLACK.MZP", "BLUE.MZP"], [cat[0].name0.upper(), cat[1].name0.upper()])
    with CatEboot(eb_path) as ce:
        names = ce.image_names()
        terminator = ce.read(0x7F6800 + 32 * len(names), 1)
        movies = ce.movie_table()
        movie_term = ce.i32(0xDCA590)
        prologue = ce.prologue_ids()
        suffixes = ce.lang_suffixes()
    _probe(part, "picture_names", [14745, "BLACK", "", "ff"], [len(names), names[0], names[-1], terminator.hex()])
    _probe(part, "movie_table", [134, 0, "/app0/image/movie/titlebg1.mp4", -1],
           [len(movies), movies[0].movie_id, movies[0].path, movie_term])
    scenes = read_scene_ids(eb_path)
    _probe(part, "scene_ids_start", [110, 240, 260, 270], list(scenes[:4]))
    part.check("scene_ids_equal_constants", list(SCENE_IDS), list(scenes), "tsuki.script_tables.SCENE_IDS")
    ft = EbootFontTables.open(eb_path)
    _probe(part, "font_tables_verify", [], ft.verify())

    # data-file counts (RR 3.3 item 3, FORMATS 2)
    counts: dict[str, Any] = {}
    per_arc: dict[str, int] = {}
    for a in TRIPLETS:
        hed = ctx.img / f"{a}.hed"
        part.add_input(hed)
        nam = ctx.img / f"{a}.nam"
        if nam.exists():
            part.add_input(nam)
        part.add_input(ctx.img / f"{a}.mrg")
        per_arc[a] = len(parse_hed(hed.read_bytes()).entries)
    counts["triplet_entries"] = sum(per_arc.values())
    counts["triplet_entries_per_archive"] = per_arc
    counts["allscr_sections"] = len(ctx.allscr.arc)
    counts["text_lines"] = ctx.text.count
    counts["text_languages"] = len(ctx.text.tables)
    counts["voices"] = len(ctx.archive("voice"))
    counts["movies"] = len(movies)
    movie_dir = ctx.img / "movie"
    counts["movie_files_present"] = sum(1 for m in movies if (movie_dir / Path(m.path).name).is_file())
    for k, exp_n in EXPECTED_COUNTS.items():
        part.check(f"count.{k}", exp_n, counts[k], "FORMATS 2 / RUNTIME_REQUIREMENTS 3.3 item 3")
    part.check("count.movie_files_present", 134, counts["movie_files_present"], "FORMATS 2")
    for k, got in counts.items():
        part.count(k, got)
    build = {"eboot": {"path": "eboot.bin", "size": inp["size"], "sha256": inp["sha256"], "sha1": sha1_full},
             "expected": {"size": EXPECTED_EBOOT[0], "sha256": EXPECTED_EBOOT[1]},
             "probes": probes, "counts": counts}
    part.write_json("build.json", build, _producer("export_eboot", {"table": "build"}, (
        "eboot.selfelf.Eboot", "eboot.uitext.read_table", "tsuki.image_catalog.find_catalog",
        "tsuki.catalog_eboot.Eboot", "tsuki.script_tables.read_scene_ids",
        "tsuki.font_eboot.EbootFontTables.verify", "tsuki.mrg.parse_hed", "tsuki.text.ScriptText")))

    # -- catalog records and picture names of the slice pictures ----------------------
    an = ctx.analyses(spec)
    keys = _picture_keys(an, spec)
    recs = []
    for key in keys:
        res = ctx.resolver.resolve(key)
        if not res.found:
            raise ManifestError(f"picture {key} not in the picture-name list")
        e = cat[res.index]
        recs.append({"key": key, "name_index": res.index, "name": names[res.index],
                     "record": _catalog_json(e), "file": res.file, "overlay": res.overlay,
                     "file_id": _image_id(res.file_id), "overlay_id": _image_id(res.overlay_id),
                     "reduced_id": _image_id(res.reduced_id)})
    for key, idx in EXPECTED_CATALOG.items():
        if key in keys:
            part.check(f"catalog.{key}", idx, next(r["name_index"] for r in recs if r["key"] == key), RR)
    seen = _seen_records(part, ctx, an, keys, names)
    part.write_json("catalog.json", {
        "table": {"va": 0x671900, "segment": "data", "record_size": 0x48, "used": cat_used, "empty": cat_empty},
        "names_table": {"va": 0x7F6800, "segment": "data", "record_size": 32, "entries": len(names)},
        "resolution": "key = upper-case picture id (+ '_hh_vv' from STCH a9/a10) -> index k in the "
                      "picture-name list -> catalog record k -> name0 / name1 found by the NAM search "
                      "allpac4, allpac3, allpac1_2, allpac1, allpac0 (catalog.find_image)",
        "records": recs,
        "seen_records_note": "seen_records: every _STCG name the traced statements execute (picture-seen bits, "
                             "not displayed; script.md STCG row, gallery.md 2.2). picture_id = 0x82CE0 lookup in "
                             "the picture-name list; stcg_id = -1 when picture_id >= 0xA8D (the _STCG clamp, "
                             "FORMATS 2.2); seen_index = the bit 0x95CA0 sets (-1 = none); displayed = the key is "
                             "also in records",
        "seen_records": seen}, _producer("export_eboot", {"table": "catalog", "keys": keys,
                                                           "seen_keys": [r["key"] for r in seen]}, (
            "tsuki.catalog.ScriptImageResolver.resolve", "tsuki.image_catalog.load_catalog",
            "tsuki.catalog_eboot.Eboot.image_names", "tsuki.misc_gallery_extras_flowchart.Unlocks.seen_index")))
    part.count("catalog_records_exported", len(recs))
    part.count("catalog_seen_records_exported", len(seen))

    # -- scenes, movies ------------------------------------------------------------
    part.write_json("scenes.json", {"va": 0x216130, "segment": "text", "type": "i16", "count": len(scenes),
                                    "scene_ids": list(scenes)},
                    _producer("export_eboot", {"table": "scenes"}, ("tsuki.script_tables.read_scene_ids",)))
    mids = sorted({u.detail["movie"] for a in an for u in a.movies}) if spec.movie_ids is None else list(spec.movie_ids)
    remap = {str(m): [tcatalog.movie_index_for_language(m, li) for li in range(4)] for m in mids}
    if "4" in remap:
        part.check("prologue_remap", list(EXPECTED_PROLOGUE), remap["4"], "RUNTIME_REQUIREMENTS 7.2 step 3")
        part.check("prologue_table", list(EXPECTED_PROLOGUE), prologue, "FORMATS 5.4 (text 0x200F70)")
    files: list[dict[str, Any]] = []
    for idx in sorted({i for v in remap.values() for i in v}):
        m = movies[idx]
        p = movie_dir / Path(m.path).name
        rec = part.add_input(p, full_hash=spec.hash_movies)
        files.append({"index": idx, "path": m.path, "file": p.name, "size": rec["size"], "sha256": rec["sha256"],
                      "ffprobe": _ffprobe(p)})
    part.write_json("movies.json", {
        "table": {"va": 0xDC8C70, "segment": "data", "record_size": 0x30, "terminator_va": 0xDCA590},
        "records": [{"id": m.movie_id, "va": m.va, "path": m.path, "raw": list(m.raw_fields)} for m in movies],
        "prologue_table": {"va": 0x200F70, "segment": "text", "ids": prologue},
        "language_suffixes": suffixes,
        "remap": {"note": "movie_index_for_language(id, lang) for lang 0 ja, 1 en, 2 zc, 3 zt (VA 0x5E280)",
                  "ids": remap},
        "files": files}, _producer("export_eboot", {"table": "movies", "movie_ids": mids, "hash": spec.hash_movies},
                                   ("tsuki.catalog_eboot.Eboot.movie_table", "tsuki.catalog.movie_index_for_language",
                                    "ffprobe")))

    # -- motions -----------------------------------------------------------------
    mt = MotionTable(eb_path)
    want = sorted({u.detail["motion"] for a in an for u in a.motions}) if spec.motions is None else list(spec.motions)
    if spec.motions is None and spec.scripts == DEFAULT_SPEC.scripts:
        part.check("motions", list(EXPECTED_MOTIONS), want, RR)
    # loop points (VA 0xC1C70, a compiled switch) exist only as the emulation result of the
    # layer-model research; recorded when that local file is present
    loops: Optional[dict[int, int]] = None
    loops_file = Path(__file__).resolve().parents[2] / "samples" / "misc_layer_state_and_motions" / "motion_loops.json"
    if loops_file.exists():
        from tsuki.misc_layer_state_and_motions import load_motion_loops
        loops = load_motion_loops(loops_file)
        part.add_input(loops_file)
    mrecs = []
    for mid in want:
        mo = mt.get(mid)
        if mo is None:
            raise ManifestError(f"motion {mid} does not exist")
        mrecs.append({"id": mid, "duration_ms": mo.duration, "first_key": mo.start, "end_key": mo.end,
                      "field_mask": mt.flags(mid), "loop": loops.get(mid, 0) if loops is not None else None,
                      "keys": [{"index": i, "time": mt.times[i], "fields": list(mt.vals[i])}
                               for i in range(mo.start, mo.end)]})
    part.write_json("motions.json", {
        "tables": {"duration_va": 0x274538, "end_va": 0x2A3938, "keys_va": 0x873FF0, "key_size": 72,
                   "field_mask_va": 0x245130, "count": len(mt.motions), "keyframes": len(mt.times)},
        "fields": "time = u64 ms; fields 1..27 = i16 at +8..+0x14 (1..7), i64 at +0x18 (8), i16 at +0x20..+0x44 "
                  "(9..27); -32767 = not animated (tsuki.script_tables.Keyframe)",
        "loop_source": loops_file.relative_to(loops_file.parents[2]).as_posix() if loops is not None else None,
        "motions": mrecs}, _producer("export_eboot", {"table": "motions", "ids": want}, (
            "tsuki.misc_layer_state_and_motions.MotionTable", "tsuki.misc_layer_state_and_motions.load_motion_loops")))
    part.count("motions", len(mt.motions))
    part.count("motion_keyframes", len(mt.times))

    # -- font tables (all of them: any line of the slice languages can need any glyph) --
    lookups: list[dict[str, Any]] = []
    for s in LOOKUP_SPECS:
        lt = ft.lookups[s.name]
        lookups.append({"name": s.name, "va": s.va, "count": lt.count, "entries": [[k, lt.index[k]] for k in lt.keys]})
    records: list[dict[str, Any]] = []
    for rs in RECORD_SPECS:
        rt = ft.records[rs.name]
        records.append({"name": rs.name, "va": rs.va, "count": rt.count,
                        "entries": [list(dataclasses.astuple(rt.get(i))) for i in range(rt.count)]})
    n_lookup = sum(x["count"] for x in lookups)
    part.check("font_lookups", 17423, n_lookup, "ROADMAP M1 acceptance 5")
    part.write_json("font_tables.json", {
        "lookup_record": "{u64 key (UTF-8 bytes big-endian), s16 glyph index, u16 0, u32 0}; entries = [key, index]",
        "rect_record": "{u16 tex slot, u16 x, u16 y, s16 w, u16 h}; entries = [tex, x, y, w, h]",
        "lookups": lookups, "records": records, "wide_base": list(ft.wide_base), "tags": list(ft.tags)},
        _producer("export_eboot", {"table": "font_tables"}, ("tsuki.font_eboot.EbootFontTables",)))
    part.count("font_lookup_entries", n_lookup)

    # -- text-box layout and window tables ------------------------------------------
    with CatEboot(eb_path) as ce:
        part.write_json("textbox_tables.json", _textbox_tables(part, ce),
                        _producer("export_eboot", {"table": "textbox"}, ("tsuki.catalog_eboot.Eboot.read",
                                                                         "tsuki.textbox constants")))
    # -- config defaults -------------------------------------------------------------
    cfg = read_static_config(eb_path)
    fields = []
    for f in CONFIG_FIELDS:
        val: Optional[int] = int.from_bytes(cfg[f.off:f.off + f.size], "little") if f.size in (1, 2, 4, 8) else None
        fields.append({"offset": f.off, "size": f.size, "name": f.name, "value": val, "documented": f.static,
                       "recommended": f.recommended, "confidence": f.conf})
        if val is not None:
            part.check(f"config.0x{f.off:02x}", f.static, val, "tsuki.misc_adv_system_state.CONFIG_FIELDS")
    part.write_json("config.json", {"va": 0x66C578, "segment": "data", "size": len(cfg), "raw": _hex(cfg),
                                    "fields": fields},
                    _producer("export_eboot", {"table": "config"}, ("tsuki.misc_adv_system_state.read_static_config",
                                                                     "tsuki.misc_adv_system_state.CONFIG_FIELDS")))
    # -- compositing tables (STBR modes 11, 13, 17, 19, 24 of the slice) ----------------
    et = read_engine_tables(eb_path)
    part.write_json("compositing.json", {
        "sources": {"blend_states": 0x2D8030, "effect_mode": 0x2D5978, "effect_strength": 0x2D59AC,
                    "effect_blend_state": 0x2D55E0, "stbr_to_effect": 0x2D5860, "flag_effects": 0xD9FB9,
                    "mask_names": 0x869B30, "sepia_clamp": 0x2DAA50, "capture15_types": 0x209FF0,
                    "transition_draw": 0x209C50},
        "tables": json.loads(json.dumps(dataclasses.asdict(et), default=str))},
        _producer("export_eboot", {"table": "compositing"},
                  ("tsuki.misc_compositing_transitions_shaders.read_engine_tables",)))


# followups.md (M0-prep): the _STCG names of 00_01 that have no record among the displayed pictures
EXPECTED_STCG_UNDISPLAYED = tuple(f"IMG{n:04d}" for n in (700, 758, 784, 1592, 1621, 1626, 1686, 2285, 2343,
                                                          2459, 2460, 2461, *range(2599, 2607), 2634, 2635, 2636))


def _resolved_json(ctx: SliceContext, key: str, names: Sequence[str]) -> dict[str, Any]:
    res = ctx.resolver.resolve(key)
    if not res.found:
        return {"name_index": None, "name": None, "record": None, "file": None, "overlay": None,
                "file_id": None, "overlay_id": None, "reduced_id": None}
    return {"name_index": res.index, "name": names[res.index], "record": _catalog_json(ctx.catalog[res.index]),
            "file": res.file, "overlay": res.overlay, "file_id": _image_id(res.file_id),
            "overlay_id": _image_id(res.overlay_id), "reduced_id": _image_id(res.reduced_id)}


def _seen_records(part: PartWriter, ctx: SliceContext, an: Sequence["ScriptAnalysis"], displayed: Sequence[str],
                  names: Sequence[str]) -> list[dict[str, Any]]:
    """Catalog records and picture-seen bits of every ``_STCG`` name the traced statements run."""
    from tsuki.misc_gallery_extras_flowchart import SEEN_DIRECT_LIMIT, UnlockState, Unlocks
    from tsuki.misc_gallery_extras_flowchart import load as load_gallery
    unl = Unlocks(load_gallery(ctx.eboot_path), UnlockState())
    order: list[str] = []
    uses: dict[str, list[dict[str, Any]]] = {}
    for a in an:
        for u in a.seen:
            key = u.detail["key"]
            if key not in uses:
                order.append(key)
                uses[key] = []
            uses[key].append({"script": u.script, "statement": u.statement, "offset": u.offset,
                              "name": u.detail["name"]})
    out: list[dict[str, Any]] = []
    for key in order:
        pid = unl.lookup_picture(key)
        sid = -1 if pid >= SEEN_DIRECT_LIMIT else pid
        rec = {"key": key, "displayed": key in displayed, **_resolved_json(ctx, key, names),
               "picture_id": pid, "stcg_id": sid, "seen_index": unl.seen_index(sid) if sid >= 0 else -1,
               "uses": uses[key]}
        out.append(rec)
    if spec_is_default(an):
        part.check("catalog.stcg_names", 39, len(out), "RUNTIME_REQUIREMENTS 7.2 step 3 (39 x _STCG in 00_01)")
        part.check("catalog.stcg_undisplayed", list(EXPECTED_STCG_UNDISPLAYED),
                   sorted(r["key"] for r in out if not r["displayed"]), "followups.md (oracle slice coverage)")
    part.check("catalog.stcg_all_resolve", [], [r["key"] for r in out if r["name_index"] is None],
               "script.md STCG row (0x82CE0 lookup)")
    part.check("catalog.stcg_lookup_equals_resolver", [r["name_index"] for r in out],
               [r["picture_id"] for r in out], "tsuki.misc_gallery_extras_flowchart.Unlocks.lookup_picture")
    return out


def spec_is_default(an: Sequence["ScriptAnalysis"]) -> bool:
    return [(a.index, a.stop) for a in an] == [(r.index, r.stop) for r in DEFAULT_SPEC.scripts]


def _image_id(packed: Optional[int]) -> Optional[dict[str, Any]]:
    if packed is None:
        return None
    a, i = tcatalog.unpack_image_id(packed)
    return {"packed": packed, "archive": a, "index": i}


def _catalog_json(e: CatalogEntry) -> dict[str, Any]:
    d = dataclasses.asdict(e)
    d["source_rect"] = {lang: list(e.source_rect(lang)) for lang in ("ja", "en", "zc", "zt")}
    d["band_stride"] = e.band_stride
    return d


def _textbox_tables(part: PartWriter, ce: CatEboot) -> dict[str, Any]:
    """Raw bytes of the documented text-box tables (FORMATS 5.4) with their decoded values (the
    eboot bytes are the authority).  Each table is compared with the constant tsuki.textbox
    uses; a difference is recorded as a finding (``matches_tsuki_textbox`` false)."""
    out: dict[str, Any] = {}

    def table(name: str, va: int, fmt: str, constant: Any, decode: Callable[[tuple[Any, ...]], Any]) -> None:
        n = struct.calcsize("<" + fmt)
        raw = ce.read(va, n)
        vals = decode(struct.unpack("<" + fmt, raw))
        lib, ebt = _norm(constant), _norm(vals)
        out[name] = {"va": va, "segment": "text", "format": fmt, "raw": _hex(raw), "values": vals,
                     "matches_tsuki_textbox": lib == ebt}
        if lib != ebt:
            diff = [[i, a, b] for i, (a, b) in enumerate(zip(lib, ebt)) if a != b]
            part.finding(f"textbox.{name}", f"tsuki.textbox constant differs from the eboot float32 table at "
                         f"text VA 0x{va:X} in {len(diff)} entries ([index, tsuki.textbox, eboot]); the export "
                         "uses the eboot values", diff)
        part.check(f"textbox.{name}.length", len(lib), len(ebt), "tsuki.textbox constants vs eboot bytes")

    def pairs(v: tuple[Any, ...]) -> list[list[Any]]:
        return [list(v[i:i + 2]) for i in range(0, len(v), 2)]

    table("colours", 0x2D34C0, "6H", list(tb.COLOR_TABLE), list)
    table("style_flags", 0x2D34E0, "4Q", [tb.STYLE_FLAGS[k] for k in (2, 3, 4, 5)], list)
    table("scales", 0x2D3730, "10f", list(tb.SCALE_TABLE), list)
    table("font_metrics", 0x2D3758, "4i", list(tb.FONT_METRIC_TABLE), list)
    table("mirror_x", 0x2D3260, "2f", list(tb.MIRROR_X), list)
    table("window_table", 0x2006C0, "iff" * 56, [list(r) for r in tb.WINDOW_TABLE],
          lambda v: [list(v[i:i + 3]) for i in range(0, len(v), 3)])
    table("text_offsets", 0x200960, "ff" * 56, [list(r) for r in tb._TOFF], pairs)
    table("window_sizes", 0x200BB0, "23f23f", [list(r) for r in tb._WSIZE],
          lambda v: [[v[i], v[23 + i]] for i in range(23)])
    table("x_offsets", 0x2005B0, "27f", list(tb._XOFF), list)
    table("y_offsets", 0x200620, "37f", list(tb._YOFF), list)
    raw = ce.read(0x2D32C0, 0x2D3768 - 0x2D32C0)
    out["layout_region"] = {"va": 0x2D32C0, "end_va": 0x2D3768, "segment": "text", "raw": _hex(raw),
                            "note": "whole region 0x2D32C0..0x2D3768 ('@' jump table, colours, style "
                                    "flags, icons, scales, '@f' widths)"}
    return out


def _norm(v: Any) -> Any:
    """Compare float tables at float32 precision (the eboot stores float32)."""
    if isinstance(v, float):
        return float(np.float32(v))
    if isinstance(v, (list, tuple)):
        return [_norm(x) for x in v]
    return v


# ---------------------------------------------------------------------------
# part: scripts
# ---------------------------------------------------------------------------

def export_scripts(part: PartWriter, ctx: SliceContext, spec: SliceSpec) -> None:
    scr = ctx.allscr
    part.add_input(scr.path, full_hash=True)
    part.write_json("allscr_names.json", {
        "section": 0, "format": "NAM32", "names": scr.names, "scene_ids": list(SCENE_IDS),
        "script_section": "script k = allscr section k + 3 (MZX0, then XOR 0xFF)",
        "sections": len(scr.arc)}, _producer("export_scripts", {"what": "names"}, ("tsuki.script.AllScr",)))
    for a in ctx.analyses(spec):
        k = a.index
        part.add_entry("allscr", k + SCRIPT_SECTION_BASE, a.name, scr.arc.sections[k + SCRIPT_SECTION_BASE].offset,
                       a.section_raw)
        part.check(f"script{k}.segments_equal_vm_vars", True, a.segments_match, "tsuki.vm_vars.segments")
        binrec = part.write_bytes(f"script_{k:04d}.bin", a.raw, "bytes",
                                  _producer("export_scripts", {"script": k}, ("tsuki.script.AllScr.raw",)),
                                  {"script": k, "name": a.name})
        last = a.trace.steps[-1]
        doc: dict[str, Any] = {
            "script": k, "name": a.name, "scene_id": a.scene_id, "section": k + SCRIPT_SECTION_BASE,
            "decoded": {"file": binrec.path.split("/", 1)[1], "size": len(a.raw), "sha1": sha1_bytes(a.raw)},
            "section_raw": {"size": len(a.section_raw), "sha1": sha1_bytes(a.section_raw)},
            "labels": [{"name": lab.name, "block": lab.block, "offset": lab.offset} for lab in a.labels],
            "statement_count": len(a.commands),
            "segments_equal_vm_vars": a.segments_match,
            "statements": [statement_json(c) for c in a.commands],
            "trace": {
                "initial_state": "all variables 0 (fresh VarState); scan mode off",
                "stop": a.stop, "end": a.trace.end, "last_statement": last.index, "last_offset": last.offset,
                "executed": len(a.trace.steps),
                "skipped": _skipped_ranges([s.index for s in a.trace.steps]),
                "steps": [{"index": s.index, "offset": s.offset, "name": s.name,
                           **({"writes": [list(w) for w in s.writes]} if s.writes else {}),
                           **({"goto": s.goto} if s.goto else {}),
                           **({"vars": s.vars} if s.vars else {})} for s in a.trace.steps],
            },
            "messages": [m.to_json() for m in a.messages],
            "assets": {"pictures": [u.to_json() for u in a.pictures], "voices": [u.to_json() for u in a.voices],
                       "ses": [u.to_json() for u in a.ses], "motions": [u.to_json() for u in a.motions],
                       "movies": [u.to_json() for u in a.movies]},
            "commands_used": sorted({a.commands[s.index].name for s in a.trace.steps if a.commands[s.index].name}),
        }
        part.write_json(f"script_{k:04d}.json", doc, _producer(
            "export_scripts", {"script": k, "stop": a.stop},
            ("tsuki.script.parse_script", "tsuki.script.parse_labels", "tsuki.script.Command.int_arg",
             "tsuki.script.Command.present", "tsuki.script.obj_split", "tsuki.script.obj_expr",
             "tsuki.script.obj_strict_int", "tsuki.script.obj_partial_int", "tsuki.script.Command.value",
             "tsuki.vm_vars.execute", "tsuki.vm_vars.segments", "oracle.export_slice.trace_script")),
            {"script": k, "statements": len(a.commands), "executed": len(a.trace.steps)})
        part.count(f"script{k}.statements", len(a.commands))
        part.count(f"script{k}.executed", len(a.trace.steps))
        part.count(f"script{k}.commands_used", len(doc["commands_used"]))
        part.count(f"script{k}.messages", len(a.messages))
    # cross-checks against RR 7.2 / 7.4
    if spec.scripts == DEFAULT_SPEC.scripts:
        an = {a.index: a for a in ctx.analyses(spec)}
        part.check("script3.stop_offset", 0x13A5, an[3].trace.steps[-1].offset, "dump of 00_02 (_PGST(2) at 0x13a5)")
        part.check("script2.end", ["RTMN", 0x7D4], [an[2].trace.end, an[2].trace.steps[-1].offset],
                   "script.md RTMN row: 00_01 ends with _RDED(485);_RTMN();_END_() and RTMN returns to the title")
        part.check("script3.messages_text_ids", list(range(8)), [m.text_ids[0] for m in an[3].messages],
                   "RUNTIME_REQUIREMENTS 7.2 step 4")
        part.check("script3.pages", [0, 0, 0, 1, 1, 1, 1, 1], [m.page for m in an[3].messages],
                   "RUNTIME_REQUIREMENTS 7.2 step 4 (_WNTY(1) ends a page)")
        part.check("script3.pages_follow_pgst", [10000] * 3 + [1] * 5, [m.pgst for m in an[3].messages],
                   "RUNTIME_REQUIREMENTS 7.2 step 4")
        # RR 7.4: "00_01 uses 25 statement names, 00_02 up to _PGST(2) uses 30" (markers by kind)
        stop3 = an[3].trace.steps[-1].index
        part.check("names_00_01", 25, len({c.name for c in an[2].commands if c.name}), "RUNTIME_REQUIREMENTS 7.4")
        part.check("names_00_02_to_pgst2", 30, len({c.name for c in an[3].commands[:stop3 + 1] if c.name}),
                   "RUNTIME_REQUIREMENTS 7.4")
        # vm_variables.md 4.3 / 7: _IF__ jumps when its condition is false; F026 is 0 at a scene
        # start, so both scripts skip the block _STCC(-1) .. _WKST(F028,eq,0) (statements 40..68)
        part.check("if_block_skipped", [[[40, 68]], [[40, 68]]],
                   [_skipped_ranges([s.index for s in an[k].trace.steps]) for k in (2, 3)],
                   "vm_variables.md 4.3 (F026 == 0 after every scene start)")


def _skipped_ranges(executed: Sequence[int]) -> list[list[int]]:
    """Statement index ranges [first, last] passed over by forward jumps of the trace."""
    out: list[list[int]] = []
    for a, b in zip(executed, executed[1:]):
        if b > a + 1:
            out.append([a + 1, b - 1])
    return out


# ---------------------------------------------------------------------------
# part: text
# ---------------------------------------------------------------------------

def _messages(ctx: SliceContext, spec: SliceSpec) -> list[Message]:
    return [m for a in ctx.analyses(spec) for m in a.messages]


def export_text(part: PartWriter, ctx: SliceContext, spec: SliceSpec) -> None:
    st = ctx.text
    part.add_input(st.path, full_hash=True)
    langs: dict[str, Any] = {}
    for lang in spec.langs:
        t = st.table(lang)
        rows = []
        for i in spec.text_ids:
            raw = t.raw(i)
            eng = t.engine_line(i)
            rows.append({"id": i, "offset": t.offsets[i], "next_offset": t.offsets[i + 1],
                         "raw_hex": _hex(raw), "engine_line_hex": _hex(eng), "text": t.line(i),
                         "raw_sha1": sha1_bytes(raw)})
        langs[lang] = {"section_offsets": 2 * LANG_INDEX[lang], "section_blob": 2 * LANG_INDEX[lang] + 1,
                       "count": t.count, "lines": rows}
        part.count(f"{lang}.lines", t.count)
    msgs = []
    for m in _messages(ctx, spec):
        exp = {}
        for lang in spec.langs:
            b = st.expand_bytes(m.arg, lang)
            exp[lang] = {"hex": _hex(b), "text": b.decode("utf-8", "replace"), "sha1": sha1_bytes(b)}
        msgs.append({"script": m.script, "statement": m.statement, "offset": m.offset, "arg": m.arg,
                     "text_ids": m.text_ids, "expanded": exp})
    part.write_json("lines.json", {
        "file": "image/script_text.mrg", "ids": list(spec.text_ids),
        "engine_line": "VA 0x75F80: len = off[id+1] - off[id]; len < 3 -> empty; else min(len, 0x301) - 2 bytes",
        "expansion": "VA 0x76040 ($NNNNNN references, output cap 0x2FC bytes)",
        "languages": langs, "messages": msgs},
        _producer("export_text", {"ids": list(spec.text_ids), "langs": list(spec.langs)},
                  ("tsuki.text.ScriptText", "tsuki.text.TextTable.raw", "tsuki.text.TextTable.engine_line",
                   "tsuki.text.ScriptText.expand_bytes")))
    part.count("messages", len(msgs))


# ---------------------------------------------------------------------------
# part: layouts
# ---------------------------------------------------------------------------

def _rec_json(r: tb.GlyphRec) -> dict[str, Any]:
    return {"index": r.index, "flags": r.flags, "color": r.color, "ext": r.ext, "line": r.line, "tex": r.tex,
            "x": r.x, "y": r.y, "x2": r.x2, "y2": r.y2, "ctr": r.ctr, "dx": f32_json(r.dx),
            "dy": f32_json(r.dy), "sx": f32_json(r.sx), "sy": f32_json(r.sy)}


def _block_json(b: tb.TextBlock) -> dict[str, Any]:
    n = b.nlines
    return {"n": b.n, "width_arg": b.width_arg, "height": b.height, "flags": b.flags, "spacing": b.spacing,
            "leading": b.leading, "box_width": b.box_width, "sx": f32_json(b.sx), "sy": f32_json(b.sy),
            "nlines": n, "last_glyph_line": b.last_glyph_line, "line_glyphs": list(b.line_glyphs[:max(n, 1)]),
            "line_width": [f32_json(x) for x in b.line_width[:max(n, 1)]],
            "line_height": [f32_json(x) for x in b.line_height[:max(n, 1)]],
            "end_ctr": b.end_ctr, "y_field": b.y_field, "empty": b.empty,
            "classes": _hex(bytes(b.classes)), "wordlen": list(b.wordlen[:b.n])}


def _quad_json(q: tb.Quad) -> dict[str, Any]:
    return {"kind": q.kind, "tex": q.tex, "u0": q.u0, "v0": q.v0, "u1": q.u1, "v1": q.v1,
            "x": f32_json(q.x), "y": f32_json(q.y), "sx": f32_json(q.sx), "sy": f32_json(q.sy),
            "rgb": list(q.rgb), "alpha": q.alpha, "record": q.rec.index if q.rec is not None else None}


def export_layouts(part: PartWriter, ctx: SliceContext, spec: SliceSpec) -> None:
    st = ctx.text
    msgs = _messages(ctx, spec)
    for lang in spec.langs:
        li = LANG_INDEX[lang]
        font = ctx.font(lang)
        pages: dict[tuple[int, int], list[Message]] = {}
        for m in msgs:
            pages.setdefault((m.script, m.page), []).append(m)
        out_msgs: list[dict[str, Any]] = []
        out_pages: list[dict[str, Any]] = []
        for (script, page), ms in sorted(pages.items()):
            blocks: list[tb.TextBlock] = []
            params: list[tb.BoxParams] = []
            for m in ms:
                v = m.vars
                if v["C835"] or v["C836"]:
                    raise SliceFlowError(f"message at 0x{m.offset:x}: C835/C836 scale {v['C835']}/{v['C836']} "
                                         "is not modelled by the slice export")
                window = v["F1109"] if li == 1 and v["F1109"] > 0 else v["F1105"]
                p = tb.main_box_params(li, window=window, f31=v["F31"], f32=v["F32"])
                text = st.expand_bytes(m.arg, li)
                blocks.append(tb.layout_message(text, li, p, font=font, block=4 if m.j == 0 else 79 + m.j))
                params.append(p)
            p0 = params[0]
            origins = tb.page_positions(blocks, li, blank_before=[m.blank_before for m in ms], origin=(p0.x, p0.y))
            out_pages.append({"script": script, "page": page, "pgst": ms[0].pgst,
                              "messages": [m.text_ids for m in ms],
                              "blank_before": [m.blank_before for m in ms],
                              "origins": [[f32_json(x), f32_json(y)] for x, y in origins]})
            for m, b, p, (ox, oy) in zip(ms, blocks, params, origins):
                quads = tb.block_quads(b, ox, oy, draw_flags=DRAW_FLAGS, lang=li)
                lines = tb.break_lines(b)
                out_msgs.append({
                    "script": script, "statement": m.statement, "offset": m.offset, "text_ids": m.text_ids,
                    "page": page, "j": m.j, "layout_block": 4 if m.j == 0 else 79 + m.j,
                    "params": {"width": p.width, "height": p.height, "spacing": p.spacing, "leading": p.leading,
                               "sx": f32_json(np.float32(p.sx)), "sy": f32_json(np.float32(p.sy)), "color": p.color, "flags": p.flags,
                               "x": p.x, "y": p.y, "window": p.window},
                    "origin": [f32_json(ox), f32_json(oy)], "blank_before": m.blank_before,
                    "block": _block_json(b), "records": [_rec_json(r) for r in b.records],
                    "lines": [[r.index for r in ln] for ln in lines],
                    "quads": {"draw_flags": DRAW_FLAGS, "head": tb.REVEAL_ALL, "tail": tb.REVEAL_ALL,
                              "list": [_quad_json(q) for q in quads]}})
        doc = {"lang": lang, "lang_index": li,
               "space": "text space 640 x 360 units; the engine maps 1 unit to 3 px on the 1920 x 1080 frame "
                        "(textbox.md 2; the prologue movie shows 3.115 px vertically, RR 9 item 1)",
               "pages": out_pages, "messages": out_msgs}
        part.write_json(f"layout_{lang}.json", doc, _producer(
            "export_layouts", {"lang": lang, "draw_flags": DRAW_FLAGS},
            ("tsuki.textbox.main_box_params", "tsuki.textbox.layout_message", "tsuki.textbox.page_positions",
             "tsuki.textbox.break_lines", "tsuki.textbox.block_quads", "tsuki.font.Font",
             "tsuki.text.ScriptText.expand_bytes")))
        part.count(f"{lang}.messages", len(out_msgs))
        part.count(f"{lang}.nlines", [m["block"]["nlines"] for m in out_msgs])
        if lang == "en" and spec.scripts == DEFAULT_SPEC.scripts:
            # RR 7.5 check 5: messages 3 and 5 break into two lines in English
            nl = {m["text_ids"][0]: m["block"]["nlines"] for m in out_msgs}
            part.check("en.messages_3_5_two_lines", [2, 2], [nl.get(3), nl.get(5)], "RUNTIME_REQUIREMENTS 7.5 check 5")
    for f in ctx._fonts.values():
        part.add_input(f.pages.path, full_hash=True)


# ---------------------------------------------------------------------------
# part: pictures
# ---------------------------------------------------------------------------

def _picture_keys(an: Sequence[ScriptAnalysis], spec: SliceSpec) -> list[str]:
    keys: list[str] = []
    for a in an:
        for u in a.pictures:
            if u.detail["key"] not in keys:
                keys.append(u.detail["key"])
    if spec.pictures is not None:
        missing = [k for k in spec.pictures if k not in keys]
        if missing:
            raise ManifestError(f"pictures {missing} are not displayed by the traced statements")
        keys = [k for k in keys if k in spec.pictures]
    return keys


@dataclass
class PictureTiles:
    header: timage.ImageHeader
    tiles: list[dict[str, Any]]
    data: list[bytes]                  # RGBA of the non-empty tiles, in tile order
    assembled: np.ndarray


def picture_tiles(entry_bytes: bytes) -> PictureTiles:
    """Decode an image MZP into its stored tiles the way the engine uploads them (VA 0xD8B80 /
    0xD9330: flag-0 tiles get no texture) and the displayed picture (``tsuki.image.assemble``)."""
    mzp = MzpArchive(entry_bytes)
    hdr = timage.parse_header(mzp.read(0))
    if mzp.count != 1 + hdr.ntiles:
        raise timage.ImageError(f"{mzp.count} sections for {hdr.ntiles} tiles")
    W, H, b = hdr.out_width, hdr.out_height, hdr.border
    tiles: list[dict[str, Any]] = []
    data: list[bytes] = []
    decoded: dict[int, np.ndarray] = {}
    off = 0
    for t in range(hdr.ntiles):
        sec = mzp.read(1 + t)
        x0, y0 = hdr.tile_origin(t)
        cw, ch = max(0, min(hdr.inner_w, W - x0)), max(0, min(hdr.inner_h, H - y0))
        rec: dict[str, Any] = {"index": t, "col": t % hdr.tiles_x, "row": t // hdr.tiles_x,
                               "flag": hdr.tile_flags[t], "section_size": len(sec), "section_sha1": sha1_bytes(sec),
                               "copy": {"src": [b, b], "dst": [x0, y0], "size": [cw, ch]}, "data": None}
        if hdr.tile_flags[t] != timage.TileFlag.EMPTY:
            raw = mzx.decompress(sec)
            px = np.ascontiguousarray(timage.decode_tile(hdr, raw))
            blob = px.tobytes()
            rec.update({"mzx_size": len(raw), "mzx_sha1": sha1_bytes(raw), "rgba_sha1": sha1_bytes(blob),
                        "data": {"offset": off, "length": len(blob), "width": hdr.tile_w, "height": hdr.tile_h}})
            data.append(blob)
            decoded[t] = px
            off += len(blob)
        tiles.append(rec)
    return PictureTiles(hdr, tiles, data, timage.assemble(hdr, decoded))


def _reassemble(pt: PictureTiles) -> np.ndarray:
    """Rebuild the displayed picture from the exported tile blobs and copy rectangles only
    (what a consumer of the files does)."""
    h = pt.header
    out = np.zeros((h.out_height, h.out_width, 4), np.uint8)
    blobs = iter(pt.data)
    for t in pt.tiles:
        if t["data"] is None:
            continue
        px = np.frombuffer(next(blobs), np.uint8).reshape(t["data"]["height"], t["data"]["width"], 4)
        (sx, sy), (dx, dy), (w, hh) = t["copy"]["src"], t["copy"]["dst"], t["copy"]["size"]
        out[dy:dy + hh, dx:dx + w] = px[sy:sy + hh, sx:sx + w]
    return out


def _header_json(h: timage.ImageHeader) -> dict[str, Any]:
    return {"width": h.width, "height": h.height, "tile_w": h.tile_w, "tile_h": h.tile_h, "tiles_x": h.tiles_x,
            "tiles_y": h.tiles_y, "bmp_type": h.bmp_type, "depth": h.depth, "attr": h.attr,
            "format": h.format.value, "border": h.border, "out_width": h.out_width, "out_height": h.out_height,
            "inner_w": h.inner_w, "inner_h": h.inner_h, "ntiles": h.ntiles, "tile_flags": list(h.tile_flags),
            "header_size": h.header_size, "palette": _hex(h.palette) if h.palette else None}


def _export_picture(part: PartWriter, ctx: SliceContext, archive: str, index: int, *,
                    uses: list[dict[str, Any]], catalog: Optional[CatalogEntry], role: str,
                    langs: Sequence[str], function: str = "export_pictures") -> dict[str, Any]:
    arc = ctx.archive(archive)
    e = arc.entries[index]
    part.add_input(arc.hed_path)
    if arc.nam is not None:
        part.add_input(arc.base.with_suffix(".nam"))
    part.add_input(arc.mrg_path)
    raw = arc.read(index)
    part.add_entry(archive, index, e.name, e.offset, raw)
    pt = picture_tiles(raw)
    asm_sha = sha1_bytes(pt.assembled.tobytes())
    ref_sha = sha1_bytes(timage.decode_image(MzpArchive(raw)).rgba.tobytes())
    part.check(f"{archive}_{index}.assembled_equals_tsuki_image", ref_sha, asm_sha, "tsuki.image.decode_image")
    part.check(f"{archive}_{index}.tiles_reassemble", asm_sha, sha1_bytes(_reassemble(pt).tobytes()),
               "oracle.export_slice._reassemble (tile records only)")
    stem = f"{archive}_{index:05d}"
    rgba_rec = part.write_chunks(f"{stem}.rgba", pt.data, "rgba8-tiles",
                                 _producer(function, {"archive": archive, "entry": index},
                                           ("tsuki.mrg.MrgArchive.read", "tsuki.mzp.MzpArchive",
                                            "tsuki.image.parse_header", "tsuki.mzx.decompress",
                                            "tsuki.image.decode_tile")),
                                 {"archive": archive, "entry": index, "name": e.name})
    hdr = {"archive": archive, "index": index, "name": e.name, "role": role,
           "entry": {"offset": e.offset, "size": e.size, "sha1": sha1_bytes(raw)},
           "header": _header_json(pt.header),
           "tiles_file": rgba_rec.path.split("/", 1)[1],
           "tiles": pt.tiles,
           "assembled": {"width": pt.header.out_width, "height": pt.header.out_height, "sha1": asm_sha,
                         "how": "zero RGBA out_width x out_height; for every tile with data copy "
                                "size from tile[src] to image[dst] (tsuki.image.assemble)"},
           "uses": uses}
    if catalog is not None:
        logical = {}
        for lang in langs:
            rect = catalog.source_rect(lang)
            logical[lang] = {"rect": list(rect), "sha1": sha1_bytes(crop_rect(pt.assembled, rect).tobytes())}
        hdr["catalog"] = {"index": catalog.index, "name0": catalog.name0, "name1": catalog.name1,
                          "anchor": [catalog.anchor_x, catalog.anchor_y], "size": [catalog.width, catalog.height],
                          "logical": logical}
    part.write_json(f"{stem}.json", hdr, _producer(function, {"archive": archive, "entry": index},
                                                   ("oracle.export_slice.picture_tiles",
                                                    "tsuki.image_catalog.CatalogEntry.source_rect")),
                    {"archive": archive, "entry": index, "name": e.name})
    return {"archive": archive, "index": index, "name": e.name, "bytes": rgba_rec.size, "sha1": asm_sha}


def export_pictures(part: PartWriter, ctx: SliceContext, spec: SliceSpec) -> None:
    an = ctx.analyses(spec)
    keys = _picture_keys(an, spec)
    cat = ctx.catalog
    done: list[dict[str, Any]] = []
    for key in keys:
        res = ctx.resolver.resolve(key)
        if not res.found or res.file_id is None:
            raise ManifestError(f"picture {key} does not resolve ({res})")
        uses = [u.to_json() for a in an for u in a.pictures if u.detail["key"] == key]
        entry = cat[res.index]
        archive, index = tcatalog.unpack_image_id(res.file_id)
        if key in EXPECTED_PICTURES:
            part.check(f"resolve.{key}", list(EXPECTED_PICTURES[key]), [archive, index], RR)
        done.append(_export_picture(part, ctx, archive, index, uses=uses, catalog=entry, role="base",
                                    langs=spec.langs) | {"key": key})
        if res.overlay_id is not None:
            oa, oi = tcatalog.unpack_image_id(res.overlay_id)
            done.append(_export_picture(part, ctx, oa, oi, uses=uses, catalog=None, role="overlay",
                                        langs=spec.langs) | {"key": key})
    for archive, index in spec.extra_pictures:
        done.append(_export_picture(part, ctx, archive, index, catalog=None, role="boot stand-in",
                                    uses=[{"note": "RUNTIME_REQUIREMENTS 7.2 step 2 (CAUTION_<lang>)"}],
                                    langs=spec.langs) | {"key": None})
    part.count("pictures", len(done))
    part.count("script_pictures", len(keys))
    part.count("tile_bytes", sum(d["bytes"] for d in done))
    if spec.pictures is None and spec.scripts == DEFAULT_SPEC.scripts:
        part.check("script_picture_keys", sorted(EXPECTED_PICTURES), sorted(keys), RR)


# ---------------------------------------------------------------------------
# part: audio
# ---------------------------------------------------------------------------

def audio_header(ctx: SliceContext, bank: str, index: int) -> tuple[dict[str, Any], bytes, bytes]:
    """(header fields, stored entry bytes, decodable stream) of one bank entry, without decoding."""
    ab = ctx.bank(bank)
    e = ab.arc.entries[index]
    stored = ab.arc.read_sectors(index) if bank in ("voice", "voice2") else ab.arc.read(index)
    stream = ab.stream(index)
    ent = ab.entry(index)
    w = ent.wave
    hdr: dict[str, Any] = {
        "bank": bank, "index": index, "name": ent.name, "codec": ent.codec, "channels": ent.channels,
        "rate": ent.sample_rate, "frames": ent.num_samples, "duration_s": ent.duration, "delay": ent.delay,
        "stream": {"size": len(stream), "sha1": sha1_bytes(stream),
                   "kind": "LV + MZX0 -> RIFF/WAVE ATRAC9" if bank in ("voice", "voice2") else "RIFF/WAVE ATRAC9"},
        "entry": {"offset": e.offset, "stored_size": len(stored), "sha1": sha1_bytes(stored)},
        "fact": list(w.fact) if w is not None else None,
        "smpl": [{"start": lp.start, "end": lp.end, "type": lp.type, "play_count": lp.play_count}
                 for lp in w.loops] if w is not None else [],
        "loop": ({"start": ent.loop.start, "end": ent.loop.end, "end_inclusive": True, "whole": ent.loop.whole}
                 if ent.loop is not None else None),
        "atrac9": {"config": _hex(w.config.raw), "block_align": w.block_align,
                   "samples_per_block": w.samples_per_block, "superframes": w.superframes} if w is not None else None,
    }
    if ent.lv is not None:
        hdr["lv"] = {"present": ent.lv.present, "magic": _hex(ent.lv.magic), "length": ent.lv.length,
                     "payload": _hex(ent.lv.payload)}
    return hdr, stored, stream


def _export_audio(part: PartWriter, ctx: SliceContext, bank: str, index: int, uses: list[dict[str, Any]]) -> dict[str, Any]:
    ab = ctx.bank(bank)
    part.add_input(ab.arc.hed_path)
    if ab.arc.nam is not None:
        part.add_input(ab.arc.base.with_suffix(".nam"))
    part.add_input(ab.arc.mrg_path)
    hdr, stored, stream = audio_header(ctx, bank, index)
    part.add_entry(bank, index, hdr["name"], hdr["entry"]["offset"], stored)
    pcm = taudio.decode_at9(stream, trim=True, float_output=True)
    x = np.ascontiguousarray(pcm.samples, dtype="<f4")
    if x.shape != (hdr["frames"], hdr["channels"]):
        raise ManifestError(f"{bank}#{index}: decoded {x.shape}, header says {hdr['frames']} x {hdr['channels']}")
    blob = x.tobytes()
    stem = f"{bank}_{index:05d}"
    rec = part.write_bytes(f"{stem}.f32", blob, "f32le",
                           _producer("export_audio", {"bank": bank, "entry": index, "trim": True, "float_output": True},
                                     ("tsuki.audio.AudioBank.stream", "tsuki.audio.decode_at9 (ffmpeg atrac9)")),
                           {"bank": bank, "entry": index, "frames": int(x.shape[0]), "channels": int(x.shape[1])})
    a = np.abs(x.astype(np.float64))
    hdr.update({"pcm_file": rec.path.split("/", 1)[1], "pcm": {
        "dtype": "f32le", "layout": "interleaved", "frames": int(x.shape[0]), "channels": int(x.shape[1]),
        "sha1": rec.sha1, "peak": float(a.max()) if a.size else 0.0,
        "rms": float(np.sqrt(np.mean(x.astype(np.float64) ** 2))) if x.size else 0.0,
        "range": "decoded[delay : delay + fact[0]] (encoder delay removed); loop points are in these frames"},
        "uses": uses})
    part.write_json(f"{stem}.json", hdr, _producer("export_audio", {"bank": bank, "entry": index},
                                                   ("tsuki.audio.AudioBank.entry", "tsuki.audio.parse_wave")))
    return hdr


def export_audio(part: PartWriter, ctx: SliceContext, spec: SliceSpec) -> None:
    from .manifest import ffmpeg_version
    part.count("decoder", ffmpeg_version())        # S1: BtbN 9.0.1 vs Gyan 9.0.2 parity matters
    an = ctx.analyses(spec)
    voices = sorted({u.detail["index"] for a in an for u in a.voices}) if spec.voices is None else list(spec.voices)
    if spec.voices is None and spec.scripts == DEFAULT_SPEC.scripts:
        part.check("voices", list(EXPECTED_VOICES), voices, RR)
    vb = ctx.bank("voice")
    for v in voices:
        uses = [u.to_json() for a in an for u in a.voices if u.detail["index"] == v]
        for u in uses:
            prefix = vb.names[v] or ""
            part.check(f"voice{v}.nam_prefix", True, str(u["name"]).startswith(prefix), "audio.md (voice.nam = prefix)")
        _export_audio(part, ctx, "voice", v, uses)
    se_names = sorted({u.detail["name"] for a in an for u in a.ses}) if spec.se_names is None else list(spec.se_names)
    sb = ctx.bank("vse")
    part.add_input(ctx.img / "se_nam.mrg", full_hash=True)
    for name in se_names:
        idx = sb.find(name)
        if name in EXPECTED_SE:
            part.check(f"se.{name}", EXPECTED_SE[name], idx, RR)
        uses = [u.to_json() for a in an for u in a.ses if u.detail["name"] == name]
        _export_audio(part, ctx, "vse", idx, uses)
    for n in spec.bgm:
        hdr = _export_audio(part, ctx, "bgm", taudio.bgm_index(n), [{"note": "ROADMAP M0 scope 5 / spike S1"}])
        if n in EXPECTED_BGM_LOOP:
            lp = hdr["loop"]
            part.check(f"bgm{n}.loop", list(EXPECTED_BGM_LOOP[n]), [lp["start"], lp["end"]] if lp else None,
                       "ROADMAP spike S1")
    part.count("voices", len(voices))
    part.count("ses", len(se_names))
    part.count("bgm", len(spec.bgm))


# ---------------------------------------------------------------------------
# part: fonts
# ---------------------------------------------------------------------------

def _font_chunks(fp: FontPages, pages: list[dict[str, Any]]) -> Iterator[bytes]:
    off = 0
    for s in range(len(fp)):
        p = fp.page(s)
        a = np.ascontiguousarray(p.alpha)
        blob = a.tobytes()
        rgba = np.ascontiguousarray(p.rgba()).tobytes()
        idx = np.ascontiguousarray(p.indices).tobytes()        # = the MZX0 output of the page
        pages.append({"section": s, "slot": p.slot, "width": p.width, "height": p.height,
                      "header": list(p.header), "flag": p.flag, "offset": off, "length": len(blob),
                      "alpha_sha1": sha1_bytes(blob), "rgba_sha1": sha1_bytes(rgba), "indices_sha1": sha1_bytes(idx),
                      "palette_rgba": _hex(np.ascontiguousarray(p.palette).tobytes()),
                      "section_size": fp.archive.sections[s].size,
                      "section_sha1": sha1_bytes(fp.archive.read(s))})
        off += len(blob)
        yield blob


def export_fonts(part: PartWriter, ctx: SliceContext, spec: SliceSpec) -> None:
    langs = spec.font_langs if spec.font_langs is not None else spec.langs
    for lang in langs:
        path = ctx.img / f"ft_{lang}.mrg"
        part.add_input(path, full_hash=True)
        pages: list[dict[str, Any]] = []
        with FontPages(path) as fp:
            rec = part.write_chunks(f"ft_{lang}.a8", _font_chunks(fp, pages), "a8-pages",
                                    _producer("export_fonts", {"lang": lang},
                                              ("tsuki.font.FontPages.page", "tsuki.font.FontPage.alpha")),
                                    {"lang": lang, "pages": len(pages)})
        part.write_json(f"ft_{lang}.json", {
            "file": f"image/ft_{lang}.mrg", "lang": lang, "pages_file": rec.path.split("/", 1)[1],
            "slot": "texture slot = 0x21 + section", "outline": "outline copy of every glyph at y + 512",
            "alpha": "palette alpha a -> 0xFF if a == 0x80 else (2 * a) & 0xFF (VA 0xD90B0), then indexed",
            "palette_rgba": "256 x RGBA8 in pixel-index order (CSM1 unswizzled), alpha as stored (PS2 0..0x80)",
            "pages": pages}, _producer("export_fonts", {"lang": lang}, ("tsuki.font.FontPages",
                                                                        "tsuki.font.decode_page_header")))
        part.count(f"{lang}.pages", len(pages))
        part.check(f"{lang}.pages", 65, len(pages), "fonts.md (65 sections)")


# ---------------------------------------------------------------------------
# part: retail (RUNTIME_REQUIREMENTS 7.6)
# ---------------------------------------------------------------------------

# RR 7.6: what 00_00 adds to the slice's statement names up to its first voiced line
EXPECTED_RETAIL_NEW_NAMES = ("ATSV", "CLO3", "MSAD", "SCH2", "SEFD", "SEVL", "SHAF", "SHS2", "SQK2", "SQS2", "SSS2")
EXPECTED_RETAIL_VOICE = 16309                     # _VPLY(D10_00_00_0000_0,03fb5) at 0x2341


@dataclass
class PathAnalysis:
    """The retail path: START_SCRIPT -> _JUMP(240,START) -> 00_00 up to the stop statement."""
    start: int
    stop: tuple[int, int]
    trace: Trace
    scripts: list[int]                         # in the order they were entered
    commands: dict[int, list[Command]]
    labels: dict[int, list[Label]]
    raw: dict[int, bytes]
    section_raw: dict[int, bytes]
    assets: Assets
    stop_index: int                            # statement index of the stop statement

    def command(self, step: TraceStep) -> Command:
        assert step.script is not None
        return self.commands[step.script][step.index]


def analyse_path(ctx: SliceContext, start: int, stop: tuple[int, int]) -> PathAnalysis:
    scr = ctx.allscr
    cmds: dict[int, list[Command]] = {}
    labels: dict[int, list[Label]] = {}

    def load(k: int) -> tuple[Sequence[Command], Sequence[Label]]:
        if k not in cmds:
            cmds[k] = scr.commands(k)
            labels[k] = scr.labels(k)
        return cmds[k], labels[k]

    tr = trace_path(load, start, stop)
    order: list[int] = []
    for st in tr.steps:
        assert st.script is not None
        if st.script not in order:
            order.append(st.script)

    def command(step: TraceStep) -> tuple[int, Command]:
        assert step.script is not None
        return step.script, cmds[step.script][step.index]

    a = collect_assets(tr.steps, command)
    raw = {k: scr.raw(k) for k in order}
    sec = {k: bytes(scr.arc.view(k + SCRIPT_SECTION_BASE)) for k in order}
    return PathAnalysis(start, stop, tr, order, cmds, labels, raw, sec, a, tr.steps[-1].index)


def _step_json(s: TraceStep) -> dict[str, Any]:
    return {**({"script": s.script} if s.script is not None else {}), "index": s.index, "offset": s.offset,
            "name": s.name, **({"writes": [list(w) for w in s.writes]} if s.writes else {}),
            **({"goto": s.goto} if s.goto else {}), **({"jump": s.jump} if s.jump else {}),
            **({"vars": s.vars} if s.vars else {})}


def _export_path_scripts(part: PartWriter, ctx: SliceContext, pa: PathAnalysis) -> None:
    scr = ctx.allscr
    stop_script = pa.stop[0]
    for k in pa.scripts:
        part.add_entry("allscr", k + SCRIPT_SECTION_BASE, scr.names[k], scr.arc.sections[k + SCRIPT_SECTION_BASE].offset,
                       pa.section_raw[k])
        cm = pa.commands[k]
        seg = _segments_match(pa.raw[k], cm)
        part.check(f"script{k}.segments_equal_vm_vars", True, seg, "tsuki.vm_vars.segments")
        binrec = part.write_bytes(f"script_{k:04d}.bin", pa.raw[k], "bytes",
                                  _producer("export_retail", {"script": k}, ("tsuki.script.AllScr.raw",)),
                                  {"script": k, "name": scr.names[k]})
        last = pa.stop_index if k == stop_script else len(cm) - 1
        doc = {"script": k, "name": scr.names[k], "scene_id": SCENE_IDS[k], "section": k + SCRIPT_SECTION_BASE,
               "decoded": {"file": binrec.path.split("/", 1)[1], "size": len(pa.raw[k]), "sha1": sha1_bytes(pa.raw[k])},
               "section_raw": {"size": len(pa.section_raw[k]), "sha1": sha1_bytes(pa.section_raw[k])},
               "labels": [{"name": lab.name, "block": lab.block, "offset": lab.offset} for lab in pa.labels[k]],
               "statement_count": len(cm), "statements_exported": [0, last],
               "segments_equal_vm_vars": seg,
               "statements": [statement_json(c) for c in cm[:last + 1]]}
        part.write_json(f"script_{k:04d}.json", doc, _producer(
            "export_retail", {"script": k, "last_statement": last},
            ("tsuki.script.parse_script", "tsuki.script.parse_labels", "tsuki.script.Command.int_arg",
             "tsuki.script.Command.present", "tsuki.script.obj_expr", "tsuki.script.Command.value")),
            {"script": k, "statements": len(cm), "exported": last + 1})
        part.count(f"script{k}.statements", len(cm))


def _picture_keys_of(uses: Sequence[AssetUse]) -> list[str]:
    keys: list[str] = []
    for u in uses:
        if u.detail["key"] not in keys:
            keys.append(u.detail["key"])
    return keys


def export_retail(part: PartWriter, ctx: SliceContext, spec: SliceSpec) -> None:
    """The retail path of RR 7.6: START_SCRIPT (scene 110, entered by New Game once bit 485 is
    set) through ``_JUMP(240,START)`` into 00_00 up to its first voiced line (offset 0x2341):
    both scripts with their statements, the executed path, its pictures, motions, voice and SEs."""
    from tsuki.misc_gallery_extras_flowchart import load as load_gallery
    scr = ctx.allscr
    pa = ctx.path(spec.retail_start, spec.retail_stop)
    part.add_input(scr.path, full_hash=True)
    _export_path_scripts(part, ctx, pa)
    stop_script, stop_off = pa.stop
    steps = pa.trace.steps
    gd = load_gallery(ctx.eboot_path)
    jumps = []
    for s0 in steps:
        if s0.jump is not None and s0.script is not None:
            frm, to = SCENE_IDS[s0.script], int(s0.jump["scene"])
            edge = next((e for e in gd.edges if e[0] == frm and e[1] == to), None)
            jumps.append({"script": s0.script, "statement": s0.index, "offset": s0.offset, "from_scene": frm,
                          "scene": to, "to_script": s0.jump["script"], "label": s0.jump["label"],
                          "edge_record": {"g": list(edge[2]), "f": list(edge[3])} if edge is not None else None})
    msgs = []
    for m in pa.assets.messages:
        exp = {}
        for lang in spec.langs:
            b = ctx.text.expand_bytes(m.arg, lang)
            exp[lang] = {"hex": _hex(b), "sha1": sha1_bytes(b)}
        msgs.append(m.to_json() | {"expanded": exp})
    names_used: dict[str, list[str]] = {}
    for k in pa.scripts:
        names_used[str(k)] = sorted({pa.commands[k][s0.index].name for s0 in steps
                                     if s0.script == k and pa.commands[k][s0.index].name})
    stmt_names = len({c.name for c in pa.commands[stop_script][:pa.stop_index + 1] if c.name})
    keys = _picture_keys_of(pa.assets.pictures)
    with CatEboot(ctx.eboot_path) as ce:
        names = ce.image_names()
    cat_recs = [{"key": k2, **_resolved_json(ctx, k2, names)} for k2 in keys]
    part.write_json("path.json", {
        "start": {"script": pa.start, "scene": SCENE_IDS[pa.start], "name": scr.names[pa.start]},
        "stop": {"script": stop_script, "offset": stop_off, "statement": pa.stop_index},
        "entry": "New Game with bit 485 set (00_01 was played): scene 110 = START_SCRIPT (script.md 6.4, VA 0x6946A)",
        "initial_state": "all variables 0 (fresh VarState); scan mode off",
        "not_modelled": "the JUMP handler's scene-transition edge code (VA 0xBA3C0 / 0xBA67F: F[110] = to, "
                        "edge_seen and F[600..] bits; gallery.md 2.1): the matching edge record is listed under "
                        "jumps, its writes are not applied",
        "end": pa.trace.end, "executed": len(steps), "scripts": pa.scripts,
        "skipped": {str(k): _skipped_ranges([s0.index for s0 in steps if s0.script == k]) for k in pa.scripts},
        "jumps": jumps, "steps": [_step_json(s0) for s0 in steps],
        "messages": msgs, "assets": pa.assets.to_json() | {"seen": [u.to_json() for u in pa.assets.seen]},
        "catalog": cat_recs, "commands_used": names_used, "statement_names_to_stop": stmt_names},
        _producer("export_retail", {"start": pa.start, "stop": [stop_script, stop_off]},
                  ("oracle.export_slice.trace_path", "tsuki.vm_vars.execute", "tsuki.text.ScriptText.expand_bytes",
                   "tsuki.catalog.ScriptImageResolver.resolve", "tsuki.misc_gallery_extras_flowchart.load")),
        {"executed": len(steps)})
    part.count("executed", len(steps))
    part.count("messages", len(msgs))
    part.count("statement_names_to_stop", stmt_names)
    if (spec.retail_start, spec.retail_stop) == (DEFAULT_SPEC.retail_start, DEFAULT_SPEC.retail_stop):
        rr = "RUNTIME_REQUIREMENTS 7.6"
        part.check("jump", [{"script": 0, "scene": 240, "to_script": 1, "label": "START"}],
                   [{k2: j[k2] for k2 in ("script", "scene", "to_script", "label")} for j in jumps],
                   rr + " / script.md 6.4")
        last = steps[-1]
        part.check("stop_statement", ["VPLY", 1, 0x2341], [pa.command(last).key, last.script, last.offset], rr)
        part.check("first_message_text_id", 107, msgs[0]["text_ids"][0] if msgs else None, rr)
        part.check("statement_names_00_00", 37, stmt_names, rr)
        slice_names = {c.name for a in ctx.analyses(DEFAULT_SPEC) for c in
                       a.commands[:a.trace.steps[-1].index + 1] if c.name}
        new_names = sorted({c.name for c in pa.commands[stop_script][:pa.stop_index + 1] if c.name} - slice_names)
        part.check("new_statement_names", list(EXPECTED_RETAIL_NEW_NAMES), new_names, rr)
        part.check("voice", [EXPECTED_RETAIL_VOICE], [u.detail["index"] for u in pa.assets.voices], rr)
        part.check("picture_variant", True, "IMG1696_11_11" in keys, rr)
        part.check("stbr_4_23", True, any(pa.command(s0).raw == "_STBR(4,23);" for s0 in steps), rr)
        part.check("motions", [1, 2, 3, 4, 5], [u.detail["motion"] for u in pa.assets.motions], rr)
        part.check("g10_set", [["G[10]", 1]], [list(w) for s0 in steps for w in s0.writes if w[0] == "G[10]"],
                   rr + " (00_00 sets G[10] = 1)")
        part.check("if_block_skipped", {"0": [], "1": [[18, 46]]},
                   {str(k): _skipped_ranges([s0.index for s0 in steps if s0.script == k]) for k in pa.scripts},
                   "vm_variables.md 4.3 (F026 == 0: _IF__(F026,ne,0,LV00000) jumps)")
    # pictures, motions, voices and SEs of the path
    done: list[dict[str, Any]] = []
    for k2 in keys:
        res = ctx.resolver.resolve(k2)
        if not res.found or res.file_id is None:
            raise ManifestError(f"retail picture {k2} does not resolve ({res})")
        uses = [u.to_json() for u in pa.assets.pictures if u.detail["key"] == k2]
        archive, index = tcatalog.unpack_image_id(res.file_id)
        if not any((d["archive"], d["index"]) == (archive, index) for d in done):
            done.append(_export_picture(part, ctx, archive, index, uses=uses, catalog=ctx.catalog[res.index],
                                        role="base", langs=spec.langs, function="export_retail") | {"key": k2})
        if res.overlay_id is not None:
            oa, oi = tcatalog.unpack_image_id(res.overlay_id)
            if not any((d["archive"], d["index"]) == (oa, oi) for d in done):
                done.append(_export_picture(part, ctx, oa, oi, uses=uses, catalog=None, role="overlay",
                                            langs=spec.langs, function="export_retail") | {"key": k2})
    part.count("pictures", len(done))
    part.count("picture_keys", keys)
    mids = sorted({int(u.detail["motion"]) for u in pa.assets.motions})
    part.write_json("motions.json", _motions_doc(part, ctx, mids),
                    _producer("export_retail", {"table": "motions", "ids": mids},
                              ("tsuki.misc_layer_state_and_motions.MotionTable",)))
    vb = ctx.bank("voice")
    voices = sorted({int(u.detail["index"]) for u in pa.assets.voices})
    for v in voices:
        uses = [u.to_json() for u in pa.assets.voices if u.detail["index"] == v]
        for u in uses:
            part.check(f"voice{v}.nam_prefix", True, str(u["name"]).startswith(vb.names[v] or ""),
                       "audio.md (voice.nam = prefix)")
        _export_audio(part, ctx, "voice", v, uses)
    sb = ctx.bank("vse")
    part.add_input(ctx.img / "se_nam.mrg", full_hash=True)
    se_names = sorted({str(u.detail["name"]) for u in pa.assets.ses})
    for name in se_names:
        _export_audio(part, ctx, "vse", sb.find(name), [u.to_json() for u in pa.assets.ses if u.detail["name"] == name])
    part.count("voices", len(voices))
    part.count("ses", len(se_names))


MOTION_LOOPS = Path(__file__).resolve().parents[2] / "samples" / "misc_layer_state_and_motions" / "motion_loops.json"


def _motion_loops(part: Optional[PartWriter]) -> Optional[dict[int, int]]:
    if not MOTION_LOOPS.exists():
        return None
    from tsuki.misc_layer_state_and_motions import load_motion_loops
    if part is not None:
        part.add_input(MOTION_LOOPS)
    return load_motion_loops(MOTION_LOOPS)


def _motions_doc(part: PartWriter, ctx: SliceContext, ids: Sequence[int]) -> dict[str, Any]:
    """Motion records in the format of eboot/motions.json."""
    from tsuki.misc_layer_state_and_motions import MotionTable
    mt = MotionTable(ctx.eboot_path)
    loops = _motion_loops(part)
    recs = []
    for mid in ids:
        mo = mt.get(mid)
        if mo is None:
            raise ManifestError(f"motion {mid} does not exist")
        recs.append({"id": mid, "duration_ms": mo.duration, "first_key": mo.start, "end_key": mo.end,
                     "field_mask": mt.flags(mid), "loop": loops.get(mid, 0) if loops is not None else None,
                     "keys": [{"index": i, "time": mt.times[i], "fields": list(mt.vals[i])}
                              for i in range(mo.start, mo.end)]})
    return {"tables": {"duration_va": 0x274538, "end_va": 0x2A3938, "keys_va": 0x873FF0, "key_size": 72,
                       "field_mask_va": 0x245130, "count": len(mt.motions), "keyframes": len(mt.times)},
            "fields": "as eboot/motions.json", "motions": recs}


# ---------------------------------------------------------------------------
# part: models (M2 acceptance 7 and 8, sampled motions)
# ---------------------------------------------------------------------------

SEPL_SLICE = "_SEPL(7,1,MOON_SE03028,,`001:8000,,`017:30);"
SOUND_FRAMES = 600
EXPECTED_SE_RAMP_FRAMES = 480                     # RR 7.5 check 7: 8,000 ms at 60 frames per s
EXPECTED_WTTM_4000 = 240                          # RR 7.5 check 8
EXPECTED_REVEAL_SPEED = 0.476                     # RR 7.5 check 8 (counters per frame, speed 35, rounded)


def _f32j(x: Any) -> dict[str, Any]:
    return f32_json(np.float32(x))


def _rec_fields(r: Any) -> dict[str, Any]:
    return {"state": r.state, "kind": r.kind, "w8": r.w8, "idx": r.idx, "vol": _f32j(r.vol), "vol_to": _f32j(r.vol_to),
            "vol_rate": _f32j(r.vol_rate), "pan": _f32j(r.pan), "pan_to": _f32j(r.pan_to),
            "pan_rate": _f32j(r.pan_rate), "char": r.char, "bytes": _hex(r.pack())}


def sound_trace(img: Path, stmt: str, frames: int) -> dict[str, Any]:
    """``SoundModel`` after ``stmt`` and ``frames`` frame updates: the channel record per frame
    and the sound-library calls (``is_playing`` polls left out)."""
    from tsuki.audio import load_se_names, se_lookup
    from tsuki.misc_audio_runtime_semantics import Config as SoundConfig
    from tsuki.misc_audio_runtime_semantics import SoundModel
    names = load_se_names(img)
    m = SoundModel(SoundConfig())
    m.se_lookup = lambda n, f: se_lookup(names, n, f)
    wait = m.run(stmt)
    ch = int(stmt[6:stmt.index(",")]) % 20

    def calls() -> list[list[Any]]:
        cs = [list(c) for c in m.calls if c[0] != "is_playing"]
        m.calls.clear()
        return cs

    start = {"frame": 0, "record": _rec_fields(m.se[ch]), "calls": calls()}
    out_frames: list[dict[str, Any]] = []
    reached: Optional[int] = None
    for n in range(1, frames + 1):
        m.update(1)
        r = m.se[ch]
        out_frames.append({"frame": n, "vol": _f32j(r.vol), "state": r.state, "calls": calls()})
        if reached is None and r.vol == r.vol_to:
            reached = n
    return {"statement": stmt, "channel": ch, "stream": 6 + ch, "wait": wait,
            "model": "tsuki.misc_audio_runtime_semantics.SoundModel(Config()) (boot config VA 0x3EAC0), fresh "
                     "state, every stream reported playing; one update(1) per frame (VA 0xAD80, 60 Hz, 1 vsync)",
            "after_statement": start, "frames": out_frames, "final_record": _rec_fields(m.se[ch]),
            "target_reached_frame": reached, "records_after": [_hex(b) for b in m.records()]}


def wttm_trace(ms: int, second: Optional[int], click: bool) -> dict[str, Any]:
    """Frames of ``_WTTM(ms [, second])`` in ``tsuki.wait_model`` with no input or with the
    decide button pressed on every frame."""
    from tsuki import wait_model as wm
    cfg, mode = wm.Config(), wm.AdvMode.normal()
    st = wm.WaitState()
    wm.wttm_apply(st, ms, second)
    left = [st.e08]
    resumed: Optional[int] = None
    for n in range(1, 10_000):
        inp = wm.FrameInput.press() if click else wm.FrameInput()
        wm.tick_reveal(st, inp, cfg, mode)
        eff = wm.adv_update(st, inp, cfg, mode)
        left.append(st.e08)
        if eff.script_step:
            resumed = n
            break
    return {"ms": ms, "second": second, "click_every_frame": click, "frames": wm.wait_frames(ms),
            "e18": 1 if (second is not None and second != 0) else 0, "script_resumes_at_frame": resumed,
            "e08_per_frame": left}


def _message_blocks(ctx: SliceContext, msgs: Sequence[Message], lang: str) -> list[tuple[Message, tb.TextBlock]]:
    """The text-box blocks of the slice messages (the computation of export_layouts)."""
    li = LANG_INDEX[lang]
    font = ctx.font(lang)
    out = []
    for m in msgs:
        v = m.vars
        window = v["F1109"] if li == 1 and v["F1109"] > 0 else v["F1105"]
        p = tb.main_box_params(li, window=window, f31=v["F31"], f32=v["F32"])
        out.append((m, tb.layout_message(ctx.text.expand_bytes(m.arg, li), li, p, font=font,
                                         block=4 if m.j == 0 else 79 + m.j)))
    return out


def reveal_trace(recs: Sequence[tuple[int, int]], read: bool, limit: int = 20_000) -> dict[str, Any]:
    """Reveal of one message block (records = (flags, ctr)) in ``tsuki.wait_model``."""
    from tsuki import wait_model as wm
    cfg, mode = wm.Config(), wm.AdvMode.normal()
    st = wm.WaitState()
    wm.zm_apply(st, wm.MODE_CLICK, voice_started=False, read=read)

    def done(s: Any, _block: int) -> int:
        return 1 if wm.reveal_done(recs, s.head, s.tail) else 0

    heads: list[int] = []
    tails: list[int] = []
    shown: Optional[int] = None
    for n in range(1, limit):
        wm.tick_reveal(st, wm.FrameInput(), cfg, mode, done)
        heads.append(f32_json(np.float32(st.head))["bits"])
        tails.append(f32_json(np.float32(st.tail))["bits"])
        if st.e00 & wm.W_SHOWN:
            shown = n
            break
    last = max((c for f, c in recs if f != 0), default=None)
    return {"read": read, "last_counter": last, "shown_at_frame": shown, "head_bits": heads, "tail_bits": tails}


def motion_samples(eboot: Path, motions: Sequence[tuple[int, int]], loops: Optional[dict[int, int]] = None
                   ) -> list[dict[str, Any]]:
    """Per-frame values of motions (id, divisor) from their start: the keyframe evaluation of
    ``tsuki.misc_layer_state_and_motions`` (VA 0xBDD70) with ``cur`` = 0."""
    from tsuki.misc_layer_state_and_motions import (FIELD_MASKS, FOUND_FIELDS, MotionTable, motion_found,
                                                    motion_keytime, motion_time_ms, motion_value)
    mt = MotionTable(eboot)
    out = []
    for mid, div in motions:
        mo = mt.get(mid)
        if mo is None:
            raise ManifestError(f"motion {mid} does not exist")
        flags = mt.flags(mid)
        lp = (loops or {}).get(mid, 0)
        n_end = (mo.duration * 3 + 49) // 50 + 1          # first frame with t >= duration, plus one
        rows: list[dict[str, Any]] = []
        for n in range(0, n_end + 1):
            t0 = motion_time_ms(n, div)
            t = motion_keytime(mt, mo, t0, lp)
            if t is None:
                rows.append({"frame": n, "t_ms": t0, "key_t_ms": None, "key": None, "values": {}})
                continue
            k = next((i for i in range(mo.start, mo.end) if float(mt.val(i, 0)) >= t), mo.end - 1)
            vals = {str(f): motion_value(mt, f, mo.start, mo.end - 1, k, t, 0.0) for f in range(3, 28)
                    if flags & FIELD_MASKS[f] and (f not in FOUND_FIELDS or motion_found(mt, mo, k, f))}
            rows.append({"frame": n, "t_ms": t0, "key_t_ms": t, "key": k, "values": vals})
        out.append({"motion": mid, "divisor": div, "loop": lp, "duration_ms": mo.duration, "field_mask": flags,
                    "samples": rows})
    return out


def export_models(part: PartWriter, ctx: SliceContext, spec: SliceSpec) -> None:
    """Golden traces of the reference models for M2 acceptance 7 and 8 and the layer checks:
    the SoundModel channel record of the slice SE loop per frame, every ``_WTTM`` of the traced
    paths, the text reveal of every slice message, and per-frame motion values."""
    an = ctx.analyses(spec)
    pa = ctx.path(spec.retail_start, spec.retail_stop) if spec.retail_stop is not None else None
    # -- SoundModel trace of the slice SE loop (RR 7.5 check 7) -------------------------
    stmt = next((ctx.allscr.commands(u.script)[u.statement].raw for a in an for u in a.ses
                 if ctx.allscr.commands(u.script)[u.statement].raw == SEPL_SLICE), None)
    if stmt is not None:
        part.add_input(ctx.img / "se_nam.mrg", full_hash=True)
        tr = sound_trace(ctx.img, stmt, SOUND_FRAMES)
        part.write_json("sound_sepl.json", tr, _producer("export_models", {"statement": stmt, "frames": SOUND_FRAMES},
                        ("tsuki.misc_audio_runtime_semantics.SoundModel.run",
                         "tsuki.misc_audio_runtime_semantics.SoundModel.update", "tsuki.audio.se_lookup")))
        rec = tr["after_statement"]["record"]
        f = np.float32
        exp_rate = float(f(f(f(f(30) * f(1000)) / f(60)) / f(8000)))
        rr = "RUNTIME_REQUIREMENTS 7.5 check 7 / sound_runtime.md 3.4, 6.2"
        play = next((c for c in tr["after_statement"]["calls"] if c[0] == "play"), None)
        part.check("sepl.play", ["vse", 598, 1], play[2:5] if play else None, rr)
        part.check("sepl.rate", exp_rate, rec["vol_rate"]["value"], rr)
        part.check("sepl.target", 30.0, tr["final_record"]["vol_to"]["value"], rr)
        part.check("sepl.frames_to_target", EXPECTED_SE_RAMP_FRAMES, tr["target_reached_frame"], rr)
        part.count("sound_frames", SOUND_FRAMES)
    # -- waits (RR 7.5 check 8) --------------------------------------------------------
    rows_w: list[tuple[Optional[int], TraceStep, Command]] = []
    for a in an:
        rows_w += [(a.index, s0, a.commands[s0.index]) for s0 in a.trace.steps]
    if pa is not None:
        rows_w += [(s0.script, s0, pa.command(s0)) for s0 in pa.trace.steps]
    waits: list[dict[str, Any]] = []
    for k, s0, c in rows_w:
        if c.name_offset >= 0 and c.key == "WTTM":
            ms = c.int_arg(0)
            second = c.int_arg(1) if c.present(1) else None
            waits.append({"script": k, "statement": s0.index, "offset": s0.offset, "raw": _ascii(c.raw),
                          "no_input": wttm_trace(ms, second, False), "decide_every_frame": wttm_trace(ms, second, True)})
    part.write_json("waits.json", {
        "model": "tsuki.wait_model: _WTTM via wttm_apply, then per frame tick_reveal (VA 0xA2630) and adv_update "
                 "(VA 0xA0B70) with the boot config, normal mode, 1 vsync; script_resumes_at_frame = the frame whose "
                 "adv_update runs Script_Step again",
        "waits": waits}, _producer("export_models", {"what": "waits"},
                                   ("tsuki.wait_model.wttm_apply", "tsuki.wait_model.tick_reveal",
                                    "tsuki.wait_model.adv_update")))
    w4000 = [w for w in waits if w["raw"].get("text") == "_WTTM(4000,1);"]
    if spec.scripts == DEFAULT_SPEC.scripts:
        part.check("wttm_4000_1.frames", [EXPECTED_WTTM_4000] * 2,
                   [w4000[0]["no_input"]["script_resumes_at_frame"], w4000[0]["decide_every_frame"]["script_resumes_at_frame"]]
                   if w4000 else None, "RUNTIME_REQUIREMENTS 7.5 check 8 (second argument 1: clicks do not cancel)")
    part.count("waits", len(waits))
    # -- reveal speed (RR 7.5 check 8) -------------------------------------------------
    from tsuki import wait_model as wm
    cfg = wm.Config()
    speeds = {f"{name}_vsync{v}": {"speed": _f32j(wm.reveal_speed(cfg, sel, v)), "width": _f32j(wm.reveal_width(cfg, sel, v))}
              for name, sel in (("unread", 0), ("read", 1)) for v in (1, 2, 3)}
    msgs = _messages(ctx, spec)
    reveals: dict[str, list[dict[str, Any]]] = {}
    for lang in spec.langs:
        rows = []
        for m, b in _message_blocks(ctx, msgs, lang):
            recs = [(r.flags, r.ctr) for r in b.records]
            rows.append({"script": m.script, "statement": m.statement, "text_ids": m.text_ids, "page": m.page,
                         "unread": reveal_trace(recs, False), "read": reveal_trace(recs, True)})
        reveals[lang] = rows
    part.write_json("reveal.json", {
        "config": {"text_speed_unread": cfg.text_speed_unread, "text_speed_read": cfg.text_speed_read},
        "speeds": speeds,
        "model": "per message: zm_apply (end mode click, no voice), then tick_reveal per frame with no input; "
                 "done = reveal_done over the layout records (flags, ctr); head_bits / tail_bits = float32 bit "
                 "patterns after each frame; shown_at_frame = the frame that sets wait bit 0x40",
        "messages": reveals}, _producer("export_models", {"what": "reveal", "langs": list(spec.langs)},
                                        ("tsuki.wait_model.reveal_speed", "tsuki.wait_model.reveal_width",
                                         "tsuki.wait_model.zm_apply", "tsuki.wait_model.tick_reveal",
                                         "tsuki.wait_model.reveal_done", "tsuki.textbox.layout_message")))
    part.check("reveal_speed_35", EXPECTED_REVEAL_SPEED, round(speeds["unread_vsync1"]["speed"]["value"], 3),
               "RUNTIME_REQUIREMENTS 7.5 check 8")
    part.check("reveal_all_shown", True, all(r[x]["shown_at_frame"] is not None for rows in reveals.values()
                                            for r in rows for x in ("unread", "read")), "tsuki.wait_model.reveal_done")
    part.count("reveal_messages", sum(len(v) for v in reveals.values()))
    # -- sampled motion values ---------------------------------------------------------
    uses = [u for a in an for u in a.motions] + (list(pa.assets.motions) if pa is not None else [])
    wanted: list[tuple[int, int]] = []
    for u in uses:
        key = (int(u.detail["motion"]), 0)          # no slice _STMA has a divisor argument
        if key not in wanted:
            wanted.append(key)
    loops = _motion_loops(part)
    samples = motion_samples(ctx.eboot_path, wanted, loops)
    for smp in samples:
        smp["uses"] = [u.to_json() for u in uses if int(u.detail["motion"]) == smp["motion"]]
    part.write_json("motion_samples.json", {
        "model": "per frame n since _STMA (divisor 0): t = motion_time_ms(n, 0) (VA 0xBE5A8), key time = "
                 "motion_keytime (loops from the layer-model research when present), k = first key with time >= "
                 "key time, value = motion_value(field, k, t, cur = 0.0) (VA 0xBDD70) for every field enabled by "
                 "the field mask (and found, for the found-scan fields); cur = 0 stands for the layer's own value, "
                 "returned where a field is not animated",
        "loop_source": MOTION_LOOPS.relative_to(MOTION_LOOPS.parents[2]).as_posix() if loops is not None else None,
        "motions": samples}, _producer("export_models", {"what": "motion samples", "motions": wanted},
                                       ("tsuki.misc_layer_state_and_motions.motion_time_ms",
                                        "tsuki.misc_layer_state_and_motions.motion_keytime",
                                        "tsuki.misc_layer_state_and_motions.motion_value")))
    by_id = {smp["motion"]: smp["samples"] for smp in samples}

    def at(mid: int, n: int, fld: int) -> Optional[int]:
        v = by_id[mid][n]["values"].get(str(fld))
        return None if v is None else round(v)

    if 22526 in by_id:
        part.check("motion22526.zoom", [1200, 1074, 912], [at(22526, 0, 9), at(22526, 1050, 9), at(22526, -1, 9)],
                   "RUNTIME_REQUIREMENTS 7.2 step 4 (zoom 1200 -> 912 per mille over 70 s, 1,074 at 17.5 s)")
    if 22525 in by_id:
        part.check("motion22525.alpha", [255, 0], [at(22525, 0, 5), at(22525, -1, 5)],
                   "RUNTIME_REQUIREMENTS 7.2 step 4 (alpha 255 -> 0 over 14 s)")
    part.count("motions_sampled", len(samples))
    part.count("motion_samples", sum(len(smp["samples"]) for smp in samples))


# ---------------------------------------------------------------------------
# part: s4 (spike S4's 154 picture SHA-1s as golden data)
# ---------------------------------------------------------------------------

S4_ORACLE = Path(__file__).resolve().parents[2] / "samples" / "spikes" / "s4" / "oracle.json"


def export_s4(part: PartWriter, ctx: SliceContext, spec: SliceSpec) -> None:
    """The pictures of spike S4 (largest pictures, every format, scene sets; its oracle file
    lists them) decoded with ``tsuki.image`` again: SHA-1 of the displayed RGBA and header facts,
    cross-checked against the SHA-1s the spike recorded."""
    src = Path(spec.s4_list) if spec.s4_list else S4_ORACLE
    if not src.is_file():
        raise ManifestError(f"{src} not found (spike S4 oracle: src/Spikes/S4.BigPicture/oracle/s4_oracle.py)")
    part.add_input(src, full_hash=True)
    doc = json.loads(src.read_text(encoding="utf-8"))
    rows = []
    for w in doc["pictures"]:
        archive, index = str(w["arc"]), int(w["index"])
        arc = ctx.archive(archive)
        part.add_input(arc.hed_path)
        if arc.nam is not None:
            part.add_input(arc.base.with_suffix(".nam"))
        part.add_input(arc.mrg_path)
        e = arc.entries[index]
        raw = arc.read(index)
        part.add_entry(archive, index, e.name, e.offset, raw)
        img = timage.decode_image(MzpArchive(raw))
        rgba = np.ascontiguousarray(img.rgba)
        h = img.header
        rows.append({"archive": archive, "index": index, "name": e.name, "roles": list(w.get("roles", [])),
                     "format": h.format.value, "stored": [h.width, h.height], "tile": [h.tile_w, h.tile_h],
                     "tiles": [h.tiles_x, h.tiles_y], "shown": [int(rgba.shape[1]), int(rgba.shape[0])],
                     "empty_tiles": sum(1 for fl in h.tile_flags if fl == timage.TileFlag.EMPTY),
                     "sha1": sha1_bytes(rgba.tobytes()), "spike_sha1": w["sha1"]})
        del img, rgba
    part.write_json("pictures.json", {
        "source": "spike S4 picture set (samples/spikes/s4/oracle.json, docs/status/spikes/S4.md)",
        "sha1": "SHA-1 of tsuki.image.decode_image(...).rgba: displayed RGBA8, row-major, straight alpha, "
                "flag-0 tiles transparent",
        "pictures": rows}, _producer("export_s4", {"source": src.name, "pictures": len(rows)},
                                     ("tsuki.mrg.MrgArchive.read", "tsuki.image.decode_image")))
    formats: dict[str, int] = {}
    for r in rows:
        formats[r["format"]] = formats.get(r["format"], 0) + 1
    part.count("pictures", len(rows))
    part.count("formats", dict(sorted(formats.items())))
    part.check("sha1_equal_spike", [], [f"{r['archive']}#{r['index']}" for r in rows if r["sha1"] != r["spike_sha1"]],
               "docs/status/spikes/S4.md (154 / 154 equal)")
    if not spec.s4_list:
        part.check("count", 154, len(rows), "docs/status/spikes/S4.md")


# ---------------------------------------------------------------------------
# driver
# ---------------------------------------------------------------------------

EXPORTERS: dict[str, tuple[Callable[[PartWriter, SliceContext, SliceSpec], None], str]] = {
    "eboot": (export_eboot, "build identity, RR 3.3 probes and counts, and the eboot-only tables of the slice"),
    "scripts": (export_scripts, "allscr names; scripts 2 and 3: decoded bytes, labels, statements with both "
                                "argument readers, executed sequence to the slice end"),
    "text": (export_text, "script_text lines 0..7 and the expanded _ZM messages, ja and en"),
    "layouts": (export_layouts, "text-box layouts of the slice messages: records, lines, origins, quads"),
    "pictures": (export_pictures, "slice pictures and CAUTION screens: RGBA tiles + JSON headers"),
    "audio": (export_audio, "voices 0x3D00..0x3D08, vse #598, bgm #33 as float PCM with loops"),
    "fonts": (export_fonts, "ft_ja / ft_en: 65 pages each as 8-bit coverage"),
    "retail": (export_retail, "RR 7.6 retail path: START_SCRIPT -> _JUMP(240,START) -> 00_00 up to 0x2341; "
                              "scripts, executed path, pictures, motions 1..5, voice #16309, SEs"),
    "models": (export_models, "SoundModel trace of the slice SE loop, _WTTM waits, text reveal, sampled motions"),
    "s4": (export_s4, "spike S4's 154 pictures: displayed-RGBA SHA-1s and header facts"),
}


def _record_analyses(part: PartWriter, ctx: SliceContext) -> None:
    """The allscr sections behind the script analyses and paths in ``ctx`` (they are cached in
    the context, so a part that reuses one opens no file the read tracking could see)."""
    if not ctx._analyses and not ctx._paths:
        return
    scr = ctx.allscr
    part.add_input(scr.path, full_hash=True)
    for a in ctx._analyses.values():
        idx = a.index + SCRIPT_SECTION_BASE
        if ("allscr", idx) not in part.entries:
            part.add_entry("allscr", idx, a.name, scr.arc.sections[idx].offset, a.section_raw)
    for pa in ctx._paths.values():
        for k in pa.scripts:
            idx = k + SCRIPT_SECTION_BASE
            if ("allscr", idx) not in part.entries:
                part.add_entry("allscr", idx, scr.names[k], scr.arc.sections[idx].offset, pa.section_raw[k])


def _export_part(area: AreaWriter, ctx: SliceContext, name: str, spec: SliceSpec,
                 log: Optional[Callable[[str], None]]) -> PartWriter:
    fn, desc = EXPORTERS[name]
    if log is not None:
        log(f"[slice] {name} ...")
    pw = area.part(name, desc)
    with pw.track_reads():
        fn(pw, ctx, spec)
        _record_analyses(pw, ctx)
    pw.finish()
    if log is not None:
        log(f"[slice] {name}: {len(pw.files)} files, {sum(f.size for f in pw.files):,} bytes, "
            f"{sum(1 for c in pw.checks if not c.ok)} failed checks of {len(pw.checks)}")
    return pw


_CHILD = "import sys; from oracle.export_slice import _child_main; raise SystemExit(_child_main(sys.argv[1:]))"


def _export_in_child(root: Path, game: Path, name: str, sel: Sequence[str], spec: SliceSpec,
                     log: Optional[Callable[[str], None]]) -> None:
    """Export one part in a fresh interpreter, so that its manifest's ``tool_used`` (and the
    read tracking) see that part only, not what earlier parts loaded or cached."""
    env = dict(os.environ)
    env["PYTHONPATH"] = os.pathsep.join(p for p in (str(TOOLS_ROOT), env.get("PYTHONPATH", "")) if p)
    env["PYTHONIOENCODING"] = "utf-8"
    cmd = [sys.executable, "-c", _CHILD, "--root", str(root), "--game", str(game), "--part", name,
           "--parts", ",".join(sel), "--spec", json.dumps(spec.to_json())] + (["--log"] if log is not None else [])
    out: list[str] = []
    with subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env, encoding="utf-8",
                          errors="replace") as proc:
        assert proc.stdout is not None
        for line in proc.stdout:
            line = line.rstrip("\r\n")
            out.append(line)
            if log is not None:
                log(line)
        rc = proc.wait()
    if rc != 0:
        tail = "\n".join(out[-40:])
        raise ManifestError(f"part {name!r} failed in its export process (exit status {rc}):\n{tail}")


def _child_main(argv: Sequence[str]) -> int:
    """Child side of :func:`_export_in_child`: export one part, leave the area manifest to the parent."""
    import argparse
    ap = argparse.ArgumentParser(prog="oracle.export_slice child")
    ap.add_argument("--root", required=True)
    ap.add_argument("--game", required=True)
    ap.add_argument("--part", required=True)
    ap.add_argument("--parts", required=True)
    ap.add_argument("--spec", required=True)
    ap.add_argument("--log", action="store_true")
    a = ap.parse_args(list(argv))
    spec = SliceSpec.from_json(json.loads(a.spec))
    sel = [p for p in a.parts.split(",") if p]
    gen = _producer("export", {"parts": sel, "spec": spec.to_json(), "game": Path(a.game).as_posix()})
    area = AreaWriter(Path(a.root), AREA, gen, description=DESCRIPTION, game_dir=Path(a.game))
    with SliceContext(Path(a.game)) as ctx:
        _export_part(area, ctx, a.part, spec, (lambda s: print(s, flush=True)) if a.log else None)
    return 0


def export(root: Path, game: Path, *, parts: Optional[Sequence[str]] = None, spec: SliceSpec = DEFAULT_SPEC,
           log: Optional[Callable[[str], None]] = None, isolate: bool = False) -> dict[str, Any]:
    """Export the slice area to ``root / 'slice'`` and return its manifest.

    ``isolate``: export every part in its own interpreter (``oracle.run`` does), so that each
    part manifest's ``tool_used`` and observed inputs belong to that part alone.  In one process
    (the default) a later part also lists the modules earlier parts loaded, and reads served from
    the shared :class:`SliceContext` caches are not observed again."""
    sel = list(PARTS) if parts is None else list(parts)
    bad = [p for p in sel if p not in EXPORTERS]
    if bad:
        raise ValueError(f"unknown parts {bad}; choose from {list(PARTS)}")
    gen = _producer("export", {"parts": sel, "spec": spec.to_json(), "game": Path(game).as_posix()})
    area = AreaWriter(Path(root), AREA, gen, description=DESCRIPTION, game_dir=Path(game))
    if isolate:
        SliceContext(Path(game)).close()                # fail early on a wrong game folder
        for name in sel:
            _export_in_child(Path(root), Path(game), name, sel, spec, log)
        return area.finish()
    with SliceContext(Path(game)) as ctx:
        for name in sel:
            _export_part(area, ctx, name, spec, log)
    return area.finish()


def run(root: Path, game: Path, *, parts: Optional[Sequence[str]] = None, quick: bool = False,
        log: Optional[Callable[[str], None]] = None) -> dict[str, Any]:
    """Entry point used by ``oracle.run``: the default slice, every part in its own process
    (``export(isolate=True)``); ``quick`` skips hashing the movie files (they are then recorded
    by size only)."""
    spec = dataclasses.replace(DEFAULT_SPEC, hash_movies=not quick)
    return export(root, game, parts=parts, spec=spec, log=log, isolate=True)
