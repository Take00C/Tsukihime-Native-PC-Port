"""uicalls - constant-propagating call decoder for the UI code of the PS4 eboot.bin.

The PS4 build has no layout tables for its screens: positions, texture slots, sub-rectangles,
canvases and scales are immediate operands and .rodata constants inside each screen's draw
function (docs/formats/archives_ui_movies.md 7.4, system_flow.md 2).  This module recovers
them statically.  It replaces the 113-line scratch decoder (scratch/misc_boot_title_choice_flow/
calls.py) and is the reference for the C# port (Tsuki.Data.UiLayoutExtractor, ROADMAP M7, on
the Iced decoder): the C# output must equal this module's output on every screen.

What it does
------------
* Walks the instructions of one function in address order and tracks, per register, per xmm
  register (low and high float) and per rbp-frame stack slot, a *symbolic value*: an int, an
  f32 float, or an expression over runtime memory (``Mem``), opaque results (``Sym``),
  loop-carried values (``Carried``), a pointer into the frame (``Frame``) or a join of
  alternatives (``Choice``).
* Forward branches carry their state to the target, where the states are joined: a register
  that holds 0.0 on one path and -187.5 on the other becomes ``Choice(0.0, -187.5)``.  A
  backward branch that does not close a loop (the compiler outlines blocks behind the function
  body) feeds the next pass; the sweep repeats until those states stop changing (at most 4).
* A loop is a strongly connected component of the instruction graph (compilers rotate loops, so
  the entry is often in the middle and the back edge is a fall-through).  Its header is the
  lowest entry node.  Every location the loop writes becomes ``Carried(header, loc)`` where a
  state enters from outside; the joined state on the edges back into the header gives the next
  iteration, so ``Carried`` evaluates for iteration k (``Env.iters``).  That recovers per-line
  positions like ``y0 + k * 26.02`` and widget ids like ``0xE4 + k``.  Nested loops are one SCC.
* Every call (and every ``jmp`` tail call) is recorded with a snapshot of the argument
  registers and of the engine's draw state (canvas, scale, colour, blend, rotation, clip),
  which the setters SetCanvas, SetScale, SetColor, SetBlend, SetRot and SetClip change.
* ``draw_sites`` turns the recorded calls to the draw primitives (TexDraw, TexDrawRect, Sprite,
  widget draw, text draw, FillRect) into :class:`DrawSite` rows with named arguments.
* ``Env`` evaluates an expression for given runtime values (``Env.mems`` maps a global's VA to
  its value, ``Env.syms`` an opaque result), so a test can ask "what does this site draw when
  the language is English, the cursor is on line 1 and the highlight is 0.5?".
* ``run_entry`` starts from a screen entry VA (the roadmap's 0x6758E points into the ``lea`` before a
  ``UiTableLoad`` call; see ``entry_instruction``):
  it decodes the enclosing function and its initialisers, collects the registered game
  processes (``ProcessRegister``) and decodes their functions.

Assumptions (the differential tests in tools/tests/test_uicalls.py check them on the real
title and choice screens against the hand-built draw lists of tsuki.misc_boot_title_choice_flow)
* A callee that is not in the recipe (``CALLEES``) does not change the draw state; it clobbers
  the caller-saved registers.  A frame pointer passed to such a callee is an out-parameter:
  the 16 bytes at that slot become ``Sym('out<callsite>.<reg>')``.  The result of an unknown
  call is ``Sym('ret<callsite>')`` / ``Sym('fret<callsite>')``; libm ``sinf`` is modelled.
* Loads below ``RO_LIMIT`` (code + .rodata) are constants and are folded; every other global is
  a ``Mem`` the caller binds (stores to globals are not tracked).  A table reached through a
  symbolic index (``Mem`` over an expression) is read from the image when it is evaluated;
  tables in .data are listed per screen (``Screen.tables``).
* ``WidgetDraw`` (0xCFC90) is a setter: it stores a widget's x, y and alpha in its record and draws
  nothing.  The widgets of a group are drawn by ``WidgetPass`` (0xD0BE0) with the canvas in force at
  that call (``widget_passes``); the draw state recorded at a ``WidgetDraw`` site is not the widget's
  (the choice screen calls the setter on its 640 x 344.7 text canvas and the pass on 640 x 448).  The
  pass overwrites scale, colour and rotation from the widget records, so those are unknown after it
  (``Callee.clobbers``).  A widget's group comes from its definition record (``widget_table``) or from
  the ``WidgetRegister`` call (0xCF9E0, ``group:rsi``).
* ``setcc r8`` with an unknown register gives ``Choice(0, 1)``, right for code that reads the
  byte alone (``mov al, bl``), which is the compiler's bool idiom.
* Jump tables (``jmp reg``) end a path; a case label is decoded with an empty state.  Indirect
  calls are unknown calls.  Padding (``nop``) after a ``jmp`` is not a path.

CLI (``PYTHONPATH=tools python -m eboot.uicalls``):

    calls  <start> [<end>]       every call with its decoded arguments
    draws  <start> [<end>]       the draw sites of a function
    screen title|choice|sysmenu  the draw sites of a recipe screen (sysmenu = the processes
                                 registered from the entry 0x6758E)
    entry  <va>                  run_entry: tables, processes and draw sites reached from an entry (the
                                 entry VA of the roadmap is the displacement of the `lea` before the
                                 UiTableLoad call, not an instruction boundary; the report says which)
    find   <lo> <hi> <slot>...   draw sites in a code range that draw one of the texture slots
"""
from __future__ import annotations

import bisect
import math
import re
import struct
import sys
from dataclasses import dataclass, field
from typing import Any, Dict, Iterable, Iterator, List, Optional, Sequence, Tuple

RO_LIMIT = 0x400000          # code and .rodata: loads below this are constants
M64 = (1 << 64) - 1
MAX_CHOICE = 8               # joins of more alternatives are dropped (value unknown)


# ---------------------------------------------------------------------------
# symbolic values
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class Sym:
    """An opaque runtime value: a call result or an out-parameter (bound in ``Env.syms``)."""
    name: str


@dataclass(frozen=True)
class Frame:
    """The address ``rbp + off`` (a stack slot of the function being decoded)."""
    off: int


@dataclass(frozen=True)
class Mem:
    """A load of ``size`` bytes (kind 'u' / 's' / 'f') from the address expression ``addr``."""
    addr: Any
    size: int
    kind: str


@dataclass(frozen=True)
class Op:
    op: str
    a: Any
    b: Any = None


@dataclass(frozen=True)
class Carried:
    """Value of location ``loc`` at the top of iteration k of the loop headed at ``header``."""
    header: int
    loc: str


@dataclass(frozen=True)
class Choice:
    """The value is one of ``vals`` (a join of paths)."""
    vals: tuple


def f32(x: float) -> float:
    try:
        return struct.unpack("<f", struct.pack("<f", x))[0]
    except OverflowError:
        return math.copysign(math.inf, x)


_INT_OPS = {
    "add": lambda x, y: (x + y) & M64, "sub": lambda x, y: (x - y) & M64, "mul": lambda x, y: (x * y) & M64,
    "and": lambda x, y: x & y, "or": lambda x, y: x | y, "xor": lambda x, y: x ^ y,
    "shl": lambda x, y: (x << (y & 63)) & M64, "shr": lambda x, y: (x & M64) >> (y & 63),
    "sar": lambda x, y: (_s64(x) >> (y & 63)) & M64,
}
_FLT_OPS = {
    "fadd": lambda x, y: x + y, "fsub": lambda x, y: x - y, "fmul": lambda x, y: x * y,
    "fmin": lambda x, y: x if x < y else y, "fmax": lambda x, y: x if x > y else y,
}


def _s64(v: int) -> int:
    v &= M64
    return v - (1 << 64) if v >> 63 else v


def _sx(v: int, bits: int) -> int:
    v &= (1 << bits) - 1
    return (v - (1 << bits) if v >> (bits - 1) else v) & M64


def _ddiv(x: float, y: Optional[float]) -> float:
    return x / y if y else math.nan


# x87-free double and libm helpers (the SSE `cvtss2sd` ... `mulsd` ... `cvtsd2ss` chain around sinf)
_DOUBLE_OPS = {
    "f2d": lambda x, y: x, "d2f": lambda x, y: f32(x), "sinf": lambda x, y: f32(math.sin(f32(x))),
    "dmul": lambda x, y: x * y, "dadd": lambda x, y: x + y, "dsub": lambda x, y: x - y, "ddiv": _ddiv,
}


def _fdiv(x: float, y: float) -> float:
    if y == 0.0:
        raise ZeroDivisionError
    return x / y


def make_op(op: str, a: Any, b: Any = None) -> Any:
    """Build ``op(a, b)``, folding constants.  ``None`` operands give ``None`` (unknown)."""
    if a is None or (b is None and op in _BINARY):
        return None
    if op in _INT_OPS and isinstance(a, int) and isinstance(b, int):
        return _INT_OPS[op](a, b)
    if op in _FLT_OPS and isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return f32(_FLT_OPS[op](float(a), float(b)))
    if op == "fdiv" and isinstance(a, (int, float)) and isinstance(b, (int, float)):
        try:
            return f32(_fdiv(float(a), float(b)))
        except ZeroDivisionError:
            return Op(op, a, b)
    if op in ("sx8", "sx16", "sx32") and isinstance(a, int):
        return _sx(a, int(op[2:]))
    if op in ("zx8", "zx16", "zx32") and isinstance(a, int):
        return a & ((1 << int(op[2:])) - 1)
    if op in ("cvt32", "cvt64") and isinstance(a, int):
        return f32(float(_s64(_sx(a, int(op[3:])))))
    if op in _DOUBLE_OPS and isinstance(a, (int, float)) and (b is None or isinstance(b, (int, float))):
        return _DOUBLE_OPS[op](float(a), None if b is None else float(b))
    if op == "cvtt" and isinstance(a, float):
        return int(a) & M64 if math.isfinite(a) else None
    if op == "fneg" and isinstance(a, (int, float)):
        return -float(a)
    if op == "add":                          # frame and zero identities
        if isinstance(a, Frame) and isinstance(b, int):
            return Frame(a.off + _s64(b))
        if isinstance(b, Frame) and isinstance(a, int):
            return Frame(b.off + _s64(a))
        if b == 0 and not isinstance(b, float):
            return a
        if a == 0 and not isinstance(a, float):
            return b
    if op == "sub":
        if isinstance(a, Frame) and isinstance(b, int):
            return Frame(a.off - _s64(b))
        if isinstance(b, int) and b == 0:
            return a
    if op == "mul" and isinstance(b, int) and b == 1:
        return a
    if op == "fmul" and isinstance(b, float) and b == 1.0:
        return a
    return Op(op, a, b)


_BINARY = set(_INT_OPS) | set(_FLT_OPS) | {"fdiv", "dmul", "dadd", "dsub", "ddiv"}


def join(vals: Sequence[Any]) -> Any:
    """Join the values of one location on several paths: equal -> that value, else a Choice."""
    flat: List[Any] = []
    for v in vals:
        if v is None:
            return None
        for w in (v.vals if isinstance(v, Choice) else (v,)):
            if w not in flat:
                flat.append(w)
    if len(flat) == 1:
        return flat[0]
    if len(flat) > MAX_CHOICE:
        return None
    return Choice(tuple(flat))


def fmt(v: Any) -> str:
    """Compact text of a value (used by the CLI and the dumps)."""
    if v is None:
        return "?"
    if isinstance(v, bool):
        return str(v)
    if isinstance(v, int):
        return hex(v) if v > 9 else str(v)
    if isinstance(v, float):
        return f"{v:.6g}"
    if isinstance(v, tuple):
        return "(" + ", ".join(fmt(x) for x in v) + ")"
    if isinstance(v, Sym):
        return v.name
    if isinstance(v, Frame):
        return f"&frame{v.off:+#x}"
    if isinstance(v, Carried):
        return f"L{v.header:x}.{v.loc}"
    if isinstance(v, Mem):
        t = {"f": "f", "d": "d", "s": "i", "u": "u"}[v.kind] + str(v.size * 8)
        return f"{t}[{fmt(v.addr)}]"
    if isinstance(v, Choice):
        return "{" + " | ".join(fmt(x) for x in v.vals) + "}"
    if isinstance(v, Op):
        sym = {"add": "+", "sub": "-", "mul": "*", "fadd": "+", "fsub": "-", "fmul": "*", "fdiv": "/",
               "and": "&", "or": "|", "xor": "^", "shl": "<<", "shr": ">>", "sar": ">>"}.get(v.op)
        if sym:
            return f"({fmt(v.a)} {sym} {fmt(v.b)})"
        if v.b is None:
            return f"{v.op}({fmt(v.a)})"
        return f"{v.op}({fmt(v.a)}, {fmt(v.b)})"
    return repr(v)


# ---------------------------------------------------------------------------
# recipe: the engine functions the UI code calls (the C# extractor ports this table)
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class Callee:
    name: str
    kind: str                       # canvas scale color blend rot clip | tex texrect fill widget text | other
    args: Tuple[Tuple[str, str], ...]   # (argument name, register)
    note: str = ""
    clobbers: Tuple[str, ...] = ()  # draw-state kinds the callee overwrites from its own data (state unknown after)


def _a(*pairs: str) -> Tuple[Tuple[str, str], ...]:
    return tuple((p.split(":")[0], p.split(":")[1]) for p in pairs)


_CALLEE_ROWS: List[Tuple[Any, ...]] = [
    (0xDCBE0, "SetCanvas", "canvas", _a("w:xmm0", "h:xmm1", "x0:xmm2", "y0:xmm3"), "one canvas unit = 1920/w x 1080/h pixels"),
    (0xD9DD0, "SetScale", "scale", _a("sx:xmm0", "sy:xmm1"), "texel -> canvas unit factor for draw flag 0x10"),
    (0xD9D20, "SetColor", "color", _a("a:xmm0", "r:xmm1", "g:xmm2", "b:xmm3")),
    (0xD9D90, "SetBlend", "blend", _a("mode:rdi")),
    (0xD9DF0, "SetRot", "rot", _a("angle:rdi"), "65536 = one turn"),
    (0xD1410, "SetClip", "clip", _a("x:xmm0", "y:xmm1", "w:xmm2", "h:xmm3")),
    (0xDD1C0, "FillRect", "fill", _a("x:xmm0", "y:xmm1", "w:xmm2", "h:xmm3", "z:xmm4")),
    (0xEB780, "TexDraw", "tex", _a("slot:rdi", "flags:rsi", "blend:rdx", "x:xmm0", "y:xmm1")),
    (0xEB890, "TexDrawRect", "texrect", _a("slot:rdi", "flags:rsi", "blend:rdx", "rect:rcx", "x:xmm0", "y:xmm1"),
     "rect = pointer to 4 x i16 (x0, y0, x1, y1)"),
    (0xEB9C0, "TexDrawRect2", "texrect", _a("slot:rdi", "flags:rsi", "blend:rdx", "rect:rcx", "x:xmm0", "y:xmm1")),
    (0xDD6D0, "Sprite", "sprite", _a("id:rdi", "flags:rsi", "x:xmm0", "y:xmm1", "z:xmm2"),
     "sprite-id level draw (the texture draws end in it)"),
    (0xCFC90, "WidgetDraw", "widget", _a("widget:rdi", "x:xmm0", "y:xmm1", "alpha:xmm2"),
     "a SETTER: stores x, y, alpha in the widget record (0x78 bytes, array at 0x178F070); nothing is drawn here"),
    (0xCFD30, "WidgetScale", "other", _a("widget:rdi", "sx:xmm0", "sy:xmm1"), "stores the scale in the widget record"),
    (0xCF900, "WidgetTable", "other", _a("table:rdi"),
     "registers every definition record of a table (0x30 bytes: id, group, flags at +0, +4, +8; id < 0 ends); see widget_table"),
    (0xCF9E0, "WidgetRegister", "other", _a("widget:rdi", "group:rsi", "rec0c:rdx", "hit:rcx", "x:xmm0", "y:xmm1", "alpha:xmm2"),
     "registers one widget: group -> record +8, rec0c -> +0xC, *hit -> +0x14 (hit box), x, y, alpha -> +0x1C, +0x20, +0x2C"),
    (0xD0BE0, "WidgetPass", "other", _a("group:rdi"),
     "draws every widget of the group (record +8) with the canvas in force at THIS call; it sets scale, colour and "
     "rotation from the widget records",
     ("scale", "color", "rot")),
    (0xC8F50, "TextDraw", "text", _a("block:rdi", "flags:rsi", "x:xmm0", "y:xmm1", "alpha:xmm2", "w:xmm3", "h:xmm4"),
     "draws a text block laid out by Text_Layout 0xC6C20"),
    (0xC6C20, "TextLayout", "other", _a("block:rdi", "line:rsi", "width:rdx", "limit:rcx")),
    (0xC2AF0, "UiTableLoad", "other", _a("table:rdi", "force:rsi"), "load a registration table (localised names)"),
    (0xEBCB0, "UiTableLoadPlain", "other", _a("table:rdi")),
    (0xEBCF0, "UiTableFree", "other", _a("table:rdi")),
    (0xECE30, "ProcessRegister", "other", _a("id:rdi", "x:rsi", "prio:rdx", "fn:rcx", "exit:r8"),
     "game process table at 0x2084560; r8 is the exit callback"),
    (0x3ED90, "LangIndex", "other", (), "returns the UI language index"),
]
CALLEES: Dict[int, Callee] = {row[0]: Callee(*row[1:]) for row in _CALLEE_ROWS}
DRAW_KINDS = ("tex", "texrect", "fill", "widget", "text", "sprite")
STATE_KINDS = ("canvas", "scale", "color", "blend", "rot", "clip")
MATH_FUNCS = {0x173560: "sinf"}      # libm functions the UI code calls: argument and result in xmm0
RETURNS = {0x3ED90: "lang"}      # callee VA -> name of the Sym its rax result becomes

ARG_REGS = ("rdi", "rsi", "rdx", "rcx", "r8", "r9")
XMM_ARGS = tuple(f"xmm{i}" for i in range(6))
CALLER_SAVED = ("rax", "rcx", "rdx", "rsi", "rdi", "r8", "r9", "r10", "r11")
GPR64 = ("rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi") + tuple(f"r{i}" for i in range(8, 16))


def _alias_table() -> Dict[str, Tuple[str, int]]:
    t: Dict[str, Tuple[str, int]] = {}
    for n in ("ax", "bx", "cx", "dx"):
        base = "r" + n
        t[base], t["e" + n], t[n] = (base, 64), (base, 32), (base, 16)
        t[n[0] + "l"] = (base, 8)
    for n in ("si", "di", "bp", "sp"):
        base = "r" + n
        t[base], t["e" + n], t[n], t[n + "l"] = (base, 64), (base, 32), (base, 16), (base, 8)
    for i in range(8, 16):
        base = f"r{i}"
        t[base], t[base + "d"], t[base + "w"], t[base + "b"] = (base, 64), (base, 32), (base, 16), (base, 8)
    return t


ALIAS = _alias_table()
PTR_SIZES = {"byte": 1, "word": 2, "dword": 4, "qword": 8, "xmmword": 16, "ymmword": 32, "oword": 16, "tbyte": 10}
_MEM_RE = re.compile(r"^(?:(byte|word|dword|qword|xmmword|ymmword|oword|tbyte)\s+ptr\s+)?(?:\w+:)?\[(.*)\]$")
_TERM_RE = re.compile(r"([+-]?)\s*([A-Za-z0-9_]+)(?:\*(\d+))?")


@dataclass(frozen=True)
class MemOp:
    size: int
    terms: Tuple[Tuple[int, str, int], ...]     # (sign, register, scale)
    disp: int


def parse_mem(op: str) -> Optional[MemOp]:
    m = _MEM_RE.match(op.strip())
    if not m:
        return None
    size = PTR_SIZES.get(m.group(1) or "", 0)
    terms: List[Tuple[int, str, int]] = []
    disp = 0
    for sign, tok, scale in _TERM_RE.findall(m.group(2)):
        s = -1 if sign == "-" else 1
        if tok[0].isdigit():
            disp += s * int(tok, 0)
        else:
            terms.append((s, tok, int(scale) if scale else 1))
    return MemOp(size, tuple(terms), disp)


def _imm(op: str) -> Optional[int]:
    try:
        return int(op.strip(), 0) & M64
    except ValueError:
        return None


# ---------------------------------------------------------------------------
# machine state
# ---------------------------------------------------------------------------
class State:
    """Registers (``r``: canonical GPR names, ``xmmN`` low float, ``xmmNh`` high float, ``$name``
    draw state) and stack slots (``s``: rbp offset -> (value, size))."""

    __slots__ = ("r", "s", "rec")

    def __init__(self, r: Optional[dict] = None, s: Optional[dict] = None) -> None:
        self.r: Dict[str, Any] = dict(r or {})
        self.s: Dict[int, Tuple[Any, int]] = dict(s or {})
        self.rec: Optional[set] = None          # when set, every written location is added

    def copy(self) -> "State":
        return State(self.r, self.s)

    def set(self, loc: str, v: Any) -> None:
        if self.rec is not None:
            self.rec.add(loc)
        if v is None:
            self.r.pop(loc, None)
        else:
            self.r[loc] = v

    def store(self, off: int, v: Any, size: int) -> None:
        for o in [o for o, (_, sz) in self.s.items() if o < off + size and off < o + sz]:
            del self.s[o]
            if self.rec is not None:
                self.rec.add(f"s{o}")
        if self.rec is not None:
            self.rec.add(f"s{off}")
        if v is not None:
            self.s[off] = (v, size)

    def get_slot(self, off: int, size: int) -> Any:
        e = self.s.get(off)
        if e is not None and e[1] == size:
            return e[0]
        return None


def _loc_get(st: State, loc: str) -> Any:
    return st.s.get(int(loc[1:]), (None, 0))[0] if loc[0] == "s" and loc[1:].lstrip("-").isdigit() else st.r.get(loc)


def _loc_put(st: State, loc: str, v: Any) -> None:
    if loc[0] == "s" and loc[1:].lstrip("-").isdigit():
        off = int(loc[1:])
        if v is None:
            st.s.pop(off, None)
        else:
            st.s[off] = (v, st.s[off][1] if off in st.s else 4)
    elif v is None:
        st.r.pop(loc, None)
    else:
        st.r[loc] = v


def merge_states(states: Sequence[State]) -> State:
    if len(states) == 1:
        return states[0].copy()
    out = State()
    keys = set(states[0].r)
    for s in states[1:]:
        keys &= set(s.r)
    for k in keys:
        v = join([s.r[k] for s in states])
        if v is not None:
            out.r[k] = v
    skeys = set(states[0].s)
    for s in states[1:]:
        skeys &= set(s.s)
    for o in skeys:
        sizes = {s.s[o][1] for s in states}
        if len(sizes) == 1:
            v = join([s.s[o][0] for s in states])
            if v is not None:
                out.s[o] = (v, sizes.pop())
    return out


# ---------------------------------------------------------------------------
# trace records
# ---------------------------------------------------------------------------
@dataclass
class Call:
    va: int                              # call site
    kind: str                            # 'call' | 'jmp' (tail call)
    target: Optional[int]                # None for an indirect call
    func: int                            # start of the decoded function
    args: Dict[str, Any]                 # rdi..r9, xmm0..5 (low float), xmm0h/xmm1h (high float)
    draw: Dict[str, Any]                 # draw state before the call: canvas scale color blend rot clip
    slots: Dict[int, Tuple[Any, int]]    # stack slots (only kept when a frame pointer is an argument)

    @property
    def callee(self) -> Optional[Callee]:
        return CALLEES.get(self.target) if self.target is not None else None

    def named(self) -> Dict[str, Any]:
        c = self.callee
        return {n: self.args.get(r) for n, r in c.args} if c else {}


@dataclass
class LoopInfo:
    header: int
    end: int                                   # address of the last back-edge branch
    locs: Dict[str, Tuple[Any, Any]] = field(default_factory=dict)   # loc -> (entry value, back-edge value)


@dataclass
class Trace:
    func: int
    end: int
    calls: List[Call]
    loops: Dict[int, LoopInfo]


@dataclass
class DrawSite:
    """One call to a draw primitive.  ``draw`` is the draw state in force at the call, except for
    ``kind == "widget"``: ``WidgetDraw`` is a setter (position and alpha into the widget record) and
    the widget is drawn later by ``WidgetPass`` with the canvas of *that* call (:func:`widget_passes`),
    so a widget site's ``draw`` is not the canvas the widget appears in."""
    va: int
    func: int
    fn: str                      # callee name
    kind: str                    # tex texrect fill widget text
    args: Dict[str, Any]         # the callee's named arguments (values are symbolic)
    draw: Dict[str, Any]         # canvas / scale / color / blend / rot / clip tuples (symbolic)
    call: Call = field(repr=False, default=None)  # type: ignore[assignment]


# ---------------------------------------------------------------------------
# the decoder
# ---------------------------------------------------------------------------
_NOWRITE = {"cmp", "test", "push", "nop", "ret", "ud2", "hlt", "ucomiss", "comiss", "ucomisd", "comisd", "bt",
            "endbr64", "int3", "jmp", "call"}


def _is_reg(op: str) -> bool:
    return op in ALIAS


def _is_xmm(op: str) -> bool:
    return op.startswith("xmm") and op[3:].isdigit()


class Decoder:
    def __init__(self, image: Any) -> None:
        self.image = image

    # -- instruction access -------------------------------------------------
    def instructions(self, start: int, end: int) -> List[Any]:
        from eboot.disasm import disasm_range
        return list(disasm_range(self.image, start, end))

    # -- operands -----------------------------------------------------------
    def _addr(self, mo: MemOp, st: State, ins: Any) -> Any:
        total: Any = mo.disp & M64
        for sign, reg, scale in mo.terms:
            if reg == "rip":
                v: Any = (ins.addr + ins.size) & M64
            else:
                al = ALIAS.get(reg)
                if al is None:
                    return None
                v = st.r.get(al[0])
                if v is None:
                    return None
            if scale != 1:
                v = make_op("mul", v, scale)
            total = make_op("add" if sign > 0 else "sub", total, v)
            if total is None:
                return None
        return total

    def _read_image(self, addr: int, size: int, kind: str) -> Any:
        try:
            b = self.image.read_va(addr, size)
        except (KeyError, ValueError):
            return None
        if kind == "f" and size == 4:
            return struct.unpack("<f", b)[0]
        if kind == "d" and size == 8:
            return struct.unpack("<d", b)[0]
        return int.from_bytes(b, "little", signed=(kind == "s")) & M64

    def _load(self, mo: MemOp, st: State, ins: Any, size: int, kind: str) -> Any:
        a = self._addr(mo, st, ins)
        if a is None:
            return None
        if isinstance(a, Frame):
            v = st.get_slot(a.off, size)
            if v is None:
                return None
            if kind == "f" and isinstance(v, int):
                return struct.unpack("<f", struct.pack("<I", v & 0xFFFFFFFF))[0]
            return v
        if isinstance(a, int):
            if a < RO_LIMIT:
                return self._read_image(a, size, kind)
            return Mem(a, size, kind)
        return Mem(a, size, kind)

    def _reg_get(self, st: State, name: str) -> Any:
        canon, bits = ALIAS[name]
        v = st.r.get(canon)
        if v is None or bits == 64:
            return v
        if isinstance(v, int):
            return v & ((1 << bits) - 1)
        return v if bits == 32 else make_op(f"zx{bits}", v)

    def _reg_set(self, st: State, name: str, v: Any) -> None:
        canon, bits = ALIAS[name]
        if bits == 64:
            st.set(canon, v)
        elif bits == 32:
            st.set(canon, (v & 0xFFFFFFFF) if isinstance(v, int) else v)
        elif bits == 8 and isinstance(st.r.get(canon), int) and v is not None:
            st.set(canon, make_op("or", st.r[canon] & ~0xFF, make_op("zx8", v)))
        else:
            st.set(canon, None)

    def _src(self, op: str, st: State, ins: Any, size_hint: int = 0, kind: str = "u") -> Tuple[Any, int]:
        """(value, size in bytes) of a source operand."""
        if _is_reg(op):
            return self._reg_get(st, op), ALIAS[op][1] // 8
        mo = parse_mem(op)
        if mo is not None:
            sz = mo.size or size_hint
            return self._load(mo, st, ins, sz, kind), sz
        v = _imm(op)
        return v, size_hint

    def _xget(self, st: State, op: str, hi: bool = False) -> Any:
        return st.r.get(op + ("h" if hi else ""))

    def _xset(self, st: State, op: str, lo: Any, hi: Any = None) -> None:
        st.set(op, lo)
        st.set(op + "h", hi)

    def _xsrc(self, op: str, st: State, ins: Any) -> Any:
        """Low float of an xmm register or a 4-byte memory operand."""
        if _is_xmm(op):
            return st.r.get(op)
        mo = parse_mem(op)
        return self._load(mo, st, ins, 4, "f") if mo else None

    # -- one instruction ------------------------------------------------------
    def step(self, ins: Any, st: State) -> None:
        """Interpret a non-control-flow instruction."""
        m = ins.mnem
        parts = [p.strip() for p in ins.ops.split(",")] if ins.ops else []
        if m in _NOWRITE or (m[0] == "v" and m[1:] in _NOWRITE):
            return
        d = parts[0] if parts else ""
        if m in ("mov", "movabs"):
            self._mov(parts, st, ins)
        elif m in ("movsx", "movzx", "movsxd"):
            if _is_reg(d):
                v, sz = self._src(parts[1], st, ins)
                bits = max(sz, 1) * 8
                self._reg_set(st, d, make_op(("sx" if m != "movzx" else "zx") + str(bits), v) if v is not None else None)
        elif m == "lea":
            mo = parse_mem(parts[1])
            if _is_reg(d):
                self._reg_set(st, d, self._addr(mo, st, ins) if mo else None)
        elif m == "cdqe":
            st.set("rax", make_op("sx32", self._reg_get(st, "eax")))
        elif m == "cwde":
            self._reg_set(st, "eax", make_op("sx16", self._reg_get(st, "ax")))
        elif m in ("cdq", "cqo"):
            st.set("rdx", None)
        elif m in ("mul", "div", "idiv") or (m == "imul" and len(parts) == 1):
            st.set("rax", None)
            st.set("rdx", None)
        elif m == "pop":
            if _is_reg(d):
                self._reg_set(st, d, None)
        elif m in ("add", "sub", "and", "or", "xor", "shl", "shr", "sar", "imul", "sal"):
            self._alu(m, parts, st, ins)
        elif m in ("inc", "dec", "neg", "not"):
            if _is_reg(d):
                v = self._reg_get(st, d)
                nv = {"inc": make_op("add", v, 1), "dec": make_op("sub", v, 1),
                      "neg": make_op("sub", 0, v), "not": make_op("xor", v, M64)}[m] if v is not None else None
                self._reg_set(st, d, nv)
        elif m.startswith("cmov"):
            if _is_reg(d):
                old = self._reg_get(st, d)
                src, _ = self._src(parts[1], st, ins)
                self._reg_set(st, d, join([old, src]) if old is not None and src is not None else None)
        elif m.startswith("set"):
            if _is_reg(d):
                canon, bits = ALIAS[d]
                # setcc writes 0 or 1 into the low byte.  A known register keeps its upper bytes;
                # an unknown one becomes Choice(0, 1), which is only right for code that reads the
                # byte alone (`mov al, bl`, the idiom the compiler emits for a bool).
                old = st.r.get(canon)
                if bits != 8:
                    st.set(canon, None)
                elif isinstance(old, int):
                    st.set(canon, Choice((old & ~0xFF, (old & ~0xFF) | 1)))
                else:
                    st.set(canon, Choice((0, 1)))
        elif m == "xchg":
            for p in parts:
                if _is_reg(p):
                    st.set(ALIAS[p][0], None)
        elif m.lstrip("v") in _SSE or m[0] == "v":
            self._sse(m, parts, st, ins)
        elif _is_reg(d):
            st.set(ALIAS[d][0], None)

    def _mov(self, parts: List[str], st: State, ins: Any) -> None:
        dst, src = parts
        if dst == "rbp" and src == "rsp":                   # frame pointer: stack slots are rbp-relative
            st.set("rbp", Frame(0))
            return
        if _is_reg(dst):
            v, _ = self._src(src, st, ins, ALIAS[dst][1] // 8)
            self._reg_set(st, dst, v)
            return
        mo = parse_mem(dst)
        if mo is None:
            return
        size = mo.size or (ALIAS[src][1] // 8 if _is_reg(src) else 4)
        v, _ = self._src(src, st, ins, size)
        a = self._addr(mo, st, ins)
        if isinstance(a, Frame):
            st.store(a.off, v, size)

    def _alu(self, m: str, parts: List[str], st: State, ins: Any) -> None:
        d = parts[0]
        if not _is_reg(d):
            return
        if m == "xor" and len(parts) == 2 and parts[1] == d:
            self._reg_set(st, d, 0)
            return
        if m == "imul" and len(parts) == 3:
            a, _ = self._src(parts[1], st, ins)
            b, _ = self._src(parts[2], st, ins)
            self._reg_set(st, d, make_op("mul", a, b))
            return
        a = self._reg_get(st, d)
        b, _ = self._src(parts[1], st, ins, ALIAS[d][1] // 8) if len(parts) > 1 else (None, 0)
        op = {"imul": "mul", "sal": "shl"}.get(m, m)
        self._reg_set(st, d, make_op(op, a, b) if a is not None and b is not None else None)

    # -- SSE scalar float subset ---------------------------------------------------
    def _sse(self, m: str, parts: List[str], st: State, ins: Any) -> None:
        vex = m.startswith("v") and m not in ("vzeroupper",)
        base = m[1:] if vex else m
        d = parts[0] if parts else ""
        if base in ("movss",):
            if _is_xmm(d):
                if len(parts) == 3 and _is_xmm(parts[2]):          # vmovss d, a, b
                    self._xset(st, d, st.r.get(parts[2]), self._xget(st, parts[1], True))
                elif _is_xmm(parts[1]):
                    st.set(d, st.r.get(parts[1]))
                else:
                    self._xset(st, d, self._xsrc(parts[1], st, ins), 0.0)
            else:
                self._fstore(d, st.r.get(parts[1]), 4, st, ins)
        elif base in ("movaps", "movups", "movdqa", "movdqu", "movapd"):
            if _is_xmm(d) and _is_xmm(parts[1]):
                self._xset(st, d, st.r.get(parts[1]), self._xget(st, parts[1], True))
            elif _is_xmm(d):
                mo = parse_mem(parts[1])
                a = self._addr(mo, st, ins) if mo else None
                if isinstance(a, Frame):
                    self._xset(st, d, self._fslot(st, a.off), self._fslot(st, a.off + 4))
                elif a is not None:
                    assert mo is not None
                    self._xset(st, d, self._load(MemOp(4, mo.terms, mo.disp), st, ins, 4, "f"),
                               self._load(MemOp(4, mo.terms, mo.disp + 4), st, ins, 4, "f"))
                else:
                    self._xset(st, d, None, None)
            else:
                mo = parse_mem(d)
                a = self._addr(mo, st, ins) if mo else None
                if isinstance(a, Frame) and _is_xmm(parts[1]):
                    st.store(a.off, st.r.get(parts[1]), 4)
                    st.store(a.off + 4, self._xget(st, parts[1], True), 4)
                    st.store(a.off + 8, None, 8)
        elif base == "movsd" and _is_xmm(d):
            if _is_xmm(parts[1]):
                self._xset(st, d, None, None)
            else:
                mo = parse_mem(parts[1])
                a = self._addr(mo, st, ins) if mo else None
                if isinstance(a, Frame):
                    self._xset(st, d, self._fslot(st, a.off), self._fslot(st, a.off + 4))
                elif a is not None:
                    assert mo is not None
                    self._xset(st, d, self._load(MemOp(4, mo.terms, mo.disp), st, ins, 4, "f"),
                               self._load(MemOp(4, mo.terms, mo.disp + 4), st, ins, 4, "f"))
                else:
                    self._xset(st, d, None, None)
        elif base == "movsd":
            mo = parse_mem(d)
            a = self._addr(mo, st, ins) if mo else None
            if isinstance(a, Frame) and _is_xmm(parts[1]):
                st.store(a.off, st.r.get(parts[1]), 4)
                st.store(a.off + 4, self._xget(st, parts[1], True), 4)
        elif base == "movshdup":
            hi = self._xget(st, parts[1], True)
            self._xset(st, d, hi, hi)
        elif base == "xorps" or base == "pxor":
            srcs = parts[1:]
            if len(srcs) == 2 and srcs[0] == srcs[1]:
                self._xset(st, d, 0.0, 0.0)
                return
            a = st.r.get(srcs[0]) if len(srcs) == 2 else st.r.get(d)
            other = srcs[-1]
            mo = parse_mem(other)
            if mo is not None and a is not None:                   # sign-mask flip: xorps x, [0x80000000 ...]
                av = self._load(mo, st, ins, 4, "u")
                if av == 0x80000000:
                    self._xset(st, d, make_op("fneg", a), self._xget(st, srcs[0], True) if len(srcs) == 2 else self._xget(st, d, True))
                    return
            self._xset(st, d, None, None)
        elif base in _SSE_ARITH:
            op = _SSE_ARITH[base]
            if len(parts) == 3:
                a, b = st.r.get(parts[1]), self._xsrc(parts[2], st, ins)
                hi = self._xget(st, parts[1], True)
            else:
                a, b = st.r.get(d), self._xsrc(parts[1], st, ins)
                hi = self._xget(st, d, True)
            self._xset(st, d, make_op(op, a, b) if a is not None and b is not None else None, hi)
        elif base in ("cvtss2sd", "cvtsd2ss"):
            src = parts[-1]
            if base == "cvtss2sd":
                a = self._xsrc(src, st, ins)
            else:
                a = st.r.get(src) if _is_xmm(src) else None
            self._xset(st, d, make_op("f2d" if base == "cvtss2sd" else "d2f", a) if a is not None else None, 0.0)
        elif base in ("mulsd", "addsd", "subsd", "divsd"):
            a = st.r.get(parts[1] if len(parts) == 3 else d)
            src = parts[-1]
            if _is_xmm(src):
                b = st.r.get(src)
            else:
                mo = parse_mem(src)
                b = self._load(MemOp(8, mo.terms, mo.disp), st, ins, 8, "d") if mo else None
            op = {"mulsd": "dmul", "addsd": "dadd", "subsd": "dsub", "divsd": "ddiv"}[base]
            self._xset(st, d, make_op(op, a, b) if a is not None and b is not None else None, 0.0)
        elif base == "cvtsi2ss":
            src = parts[-1]
            v, sz = self._src(src, st, ins, 4)
            hi = self._xget(st, parts[1], True) if len(parts) == 3 else self._xget(st, d, True)
            self._xset(st, d, make_op("cvt64" if sz == 8 else "cvt32", v) if v is not None else None, hi)
        elif base in ("cvttss2si", "cvtss2si"):
            if _is_reg(d):
                a = self._xsrc(parts[1], st, ins)
                self._reg_set(st, d, make_op("cvtt", a) if a is not None else None)
        elif base in ("ucomiss", "comiss"):
            return
        elif base == "blendvps":                                     # select(mask, b, a): either operand
            if len(parts) == 4 and _is_xmm(parts[1]):
                a_lo, a_hi = st.r.get(parts[1]), self._xget(st, parts[1], True)
                if _is_xmm(parts[2]):
                    b_lo, b_hi = st.r.get(parts[2]), self._xget(st, parts[2], True)
                else:
                    mo = parse_mem(parts[2])
                    b_lo = self._load(mo, st, ins, 4, "f") if mo else None
                    b_hi = self._load(MemOp(4, mo.terms, mo.disp + 4), st, ins, 4, "f") if mo else None
                self._xset(st, d, join([a_lo, b_lo]) if a_lo is not None and b_lo is not None else None,
                           join([a_hi, b_hi]) if a_hi is not None and b_hi is not None else None)
            else:
                self._xset(st, d, None, None)
        elif _is_xmm(d):
            self._xset(st, d, None, None)
        elif parse_mem(d) is not None:
            return
        elif _is_reg(d):
            st.set(ALIAS[d][0], None)

    def _fslot(self, st: State, off: int) -> Any:
        v = st.get_slot(off, 4)
        if isinstance(v, int):
            return struct.unpack("<f", struct.pack("<I", v & 0xFFFFFFFF))[0]
        return v

    def _fstore(self, dst: str, v: Any, size: int, st: State, ins: Any) -> None:
        mo = parse_mem(dst)
        a = self._addr(mo, st, ins) if mo else None
        if isinstance(a, Frame):
            st.store(a.off, v, size)

    # -- calls ----------------------------------------------------------------------
    def _snapshot(self, st: State) -> Dict[str, Any]:
        out = {r: st.r.get(r) for r in ARG_REGS}
        for x in XMM_ARGS:
            out[x] = st.r.get(x)
        out["xmm0h"] = st.r.get("xmm0h")
        out["xmm1h"] = st.r.get("xmm1h")
        return out

    def _draw_state(self, st: State) -> Dict[str, Any]:
        return {k: st.r.get("$" + k) for k in STATE_KINDS}

    def do_call(self, ins: Any, st: State, func: int, kind: str, target: Optional[int]) -> Call:
        args = self._snapshot(st)
        has_frame = any(isinstance(v, Frame) for v in args.values())
        rec = Call(ins.addr, kind, target, func, args, self._draw_state(st),
                   dict(st.s) if has_frame else {})
        self._apply_call(rec, st)
        return rec

    def _apply_call(self, rec: Call, st: State) -> None:
        c = rec.callee
        if c is not None and c.kind in STATE_KINDS:
            st.set("$" + c.kind, tuple(rec.args.get(r) for _, r in c.args))
        if c is not None:
            for k in c.clobbers:
                st.set("$" + k, None)
        if c is None:
            for reg, v in list(rec.args.items()):
                if isinstance(v, Frame):                    # out-parameter
                    st.store(v.off, Sym(f"out{rec.va:x}.{reg}"), 4)
                    st.store(v.off + 4, None, 12)
        for r in CALLER_SAVED:
            st.set(r, None)
        for i in range(16):
            st.set(f"xmm{i}", None)
            st.set(f"xmm{i}h", None)
        if rec.target in MATH_FUNCS:
            st.set("xmm0", make_op(MATH_FUNCS[rec.target], rec.args.get("xmm0")))
        if rec.target in RETURNS:
            st.set("rax", Sym(RETURNS[rec.target]))
        elif rec.target is not None and rec.target not in MATH_FUNCS and (c is None or c.kind == "other"):
            st.set("rax", Sym(f"ret{rec.va:x}"))
            st.set("xmm0", Sym(f"fret{rec.va:x}"))

    # -- the function sweep ---------------------------------------------------------
    def decode(self, start: int, end: Optional[int] = None) -> Trace:
        """Decode one function.

        The sweep goes through the instructions in address order.  A forward edge carries its
        state to the target; a backward edge that does not close a loop is carried to the next
        pass (the sweep repeats until those states stop changing, at most ``MAX_PASSES``).  A
        loop is a strongly connected component of the instruction graph (a compiler lays loops
        out rotated, with the entry in the middle, or outlines blocks behind the function body,
        so backward branches alone do not find them): its *header* is the lowest entry node,
        a state entering the loop from outside is joined into ``LoopInfo`` and the locations
        the loop writes become ``Carried(header, loc)`` for the body."""
        if end is None:
            end = function_end(self.image, start)
        ins_list = self.instructions(start, end)
        addrs = [i.addr for i in ins_list]
        index = {a: n for n, a in enumerate(addrs)}
        succ = self._graph(ins_list, index, start, end)
        sccs = _sccs(succ)
        loops: Dict[int, LoopInfo] = {}
        scc_of: Dict[int, int] = {}                 # node -> header of its loop
        members: Dict[int, set] = {}
        written: Dict[int, set] = {}
        reach = {0}
        todo = [0]
        while todo:                                  # padding after a jmp is not a predecessor
            for o in succ[todo.pop()]:
                if o not in reach:
                    reach.add(o)
                    todo.append(o)
        preds: Dict[int, List[int]] = {}
        for n, outs in enumerate(succ):
            if n in reach:
                for o in outs:
                    preds.setdefault(o, []).append(n)
        for comp in sccs:
            cs = set(comp)
            if len(cs) == 1 and comp[0] not in succ[comp[0]]:
                continue
            entries = [n for n in cs if n == 0 or any(p not in cs for p in preds.get(n, []))]
            h = addrs[min(entries or cs)]
            members[h] = {addrs[n] for n in cs}
            loops[h] = LoopInfo(h, max(members[h]))
            for a in members[h]:
                scc_of[a] = h
            scratch = State({"rbp": Frame(0)})
            scratch.rec = set()
            for n in sorted(cs):
                self._scan(ins_list[n], scratch)
            scratch.rec.discard("rbp")
            scratch.rec.discard("rsp")
            written[h] = scratch.rec
        extra: Dict[int, List[Tuple[int, State]]] = {}
        for _pass in range(MAX_PASSES):
            calls, entry, back, nxt = self._sweep(ins_list, start, end, loops, scc_of, members, written, extra)
            if _same_extra(nxt, extra):
                break
            extra = nxt
        for h, li in loops.items():
            ent = merge_states(entry[h]) if entry[h] else State()
            bk = merge_states(back[h]) if back[h] else State()
            for loc in written[h]:
                li.locs[loc] = (_loc_get(ent, loc), _loc_get(bk, loc))
        return Trace(start, end, calls, loops)

    def _graph(self, ins_list: List[Any], index: Dict[int, int], start: int, end: int) -> List[List[int]]:
        succ: List[List[int]] = []
        for n, ins in enumerate(ins_list):
            outs: List[int] = []
            m = ins.mnem
            t = _branch_target(ins)
            falls = m not in ("jmp", "ret", "ud2", "hlt") and not (m == "call" and t in NORETURN)
            if falls and n + 1 < len(ins_list):
                outs.append(n + 1)
            if t is not None and m != "call" and start <= t < end and t in index:
                outs.append(index[t])
            succ.append(outs)
        return succ

    def _sweep(self, ins_list: List[Any], start: int, end: int, loops: Dict[int, LoopInfo],
               scc_of: Dict[int, int], members: Dict[int, set], written: Dict[int, set],
               extra: Dict[int, List[Tuple[int, State]]]) -> Tuple[List[Call], dict, dict, dict]:
        entry: Dict[int, List[State]] = {h: [] for h in loops}
        back: Dict[int, List[State]] = {h: [] for h in loops}
        pending: Dict[int, List[Tuple[int, State]]] = {a: list(v) for a, v in extra.items()}
        nxt: Dict[int, List[Tuple[int, State]]] = {}
        calls: List[Call] = []
        cur: Optional[State] = State({**{r: Sym(f"arg_{r}") for r in ARG_REGS},
                                      **{f"xmm{i}": Sym(f"arg_xmm{i}") for i in range(6)}})   # the function's arguments
        prev_addr = start - 1

        def classify(src: int, a: int, s: State) -> Optional[State]:
            """Route one incoming state: returns the state to merge, or None if it only
            feeds a loop's back-edge record."""
            h = scc_of.get(a)
            if h is None:
                return s
            if src not in members[h]:
                entry[h].append(s.copy())
                self._blur(s, h, written[h])
                return s
            if a == h:
                back[h].append(s.copy())
                return None
            return s

        for ins in ins_list:
            a = ins.addr
            incoming: List[Tuple[int, State]] = []
            if cur is not None:
                incoming.append((prev_addr, cur))
            incoming.extend(pending.pop(a, []))
            merged = [s for s in (classify(src, a, st.copy()) for src, st in incoming) if s is not None]
            if merged:
                cur = merge_states(merged)
            else:
                h = scc_of.get(a)
                if h is not None and entry[h]:
                    cur = merge_states(entry[h])
                    self._blur(cur, h, written[h])
                elif ins.mnem in ("nop", "int3"):           # alignment padding after a jmp / ret
                    cur = None
                    prev_addr = a
                    continue
                else:                                       # a jump-table case label or dead code
                    cur = State({"rbp": Frame(0)})
            prev_addr = a
            m = ins.mnem
            t = _branch_target(ins)
            if m == "call":
                calls.append(self.do_call(ins, cur, start, "call", t))
                if t in NORETURN:
                    cur = None
            elif m == "jmp":
                if t is not None and start <= t < end:
                    self._edge(a, t, cur, pending, back, nxt, scc_of, members)
                elif t is not None:                         # tail call
                    calls.append(self.do_call(ins, cur, start, "jmp", t))
                cur = None                                  # (a jump-table jmp ends the path)
            elif m.startswith("j") and t is not None:
                if start <= t < end:
                    self._edge(a, t, cur, pending, back, nxt, scc_of, members)
            elif m in ("ret", "ud2", "hlt"):
                cur = None
            else:
                self.step(ins, cur)
        return calls, entry, back, nxt

    def _edge(self, src: int, tgt: int, st: State, pending: dict, back: dict, nxt: dict,
              scc_of: Dict[int, int], members: Dict[int, set]) -> None:
        if tgt > src:
            pending.setdefault(tgt, []).append((src, st.copy()))
            return
        h = scc_of.get(tgt)
        if h is not None and tgt == h and src in members[h]:
            back[h].append(st.copy())                    # loop back edge: feeds the Carried recurrence
        else:
            nxt.setdefault(tgt, []).append((src, st.copy()))

    def _scan(self, ins: Any, st: State) -> None:
        """Run one instruction on a scratch state to record the locations it writes."""
        m = ins.mnem
        if m == "call":
            t = _branch_target(ins)
            c = CALLEES.get(t) if t is not None else None
            if c is None:
                for reg in ARG_REGS:                         # out-parameters
                    v = st.r.get(reg)
                    if isinstance(v, Frame):
                        st.store(v.off, None, 16)
            for r in CALLER_SAVED:
                st.set(r, None)
            for i in range(16):
                st.set(f"xmm{i}", None)
                st.set(f"xmm{i}h", None)
            if c is not None and c.kind in STATE_KINDS:
                st.set("$" + c.kind, None)
            if c is not None:
                for k in c.clobbers:
                    st.set("$" + k, None)
            return
        if m == "jmp" or m.startswith("j") or m in ("ret", "ud2"):
            return
        self.step(ins, st)

    def _blur(self, st: State, header: int, locs: Iterable[str]) -> None:
        for loc in locs:
            if _loc_get(st, loc) is not None:
                _loc_put(st, loc, Carried(header, loc))


MAX_PASSES = 4
NORETURN = {0x1734B0}            # __stack_chk_fail


def _same_extra(a: dict, b: dict) -> bool:
    if set(a) != set(b):
        return False
    for k in a:
        if len(a[k]) != len(b[k]):
            return False
        for (s1, x), (s2, y) in zip(a[k], b[k]):
            if s1 != s2 or x.r != y.r or x.s != y.s:
                return False
    return True


def _sccs(succ: List[List[int]]) -> List[List[int]]:
    """Strongly connected components (iterative Tarjan); nodes are indices."""
    n = len(succ)
    idx = [-1] * n
    low = [0] * n
    on = [False] * n
    stack: List[int] = []
    out: List[List[int]] = []
    counter = 0
    for root in range(n):
        if idx[root] != -1:
            continue
        work = [(root, 0)]
        while work:
            v, i = work.pop()
            if i == 0:
                idx[v] = low[v] = counter
                counter += 1
                stack.append(v)
                on[v] = True
            recurse = False
            while i < len(succ[v]):
                w = succ[v][i]
                i += 1
                if idx[w] == -1:
                    work.append((v, i))
                    work.append((w, 0))
                    recurse = True
                    break
                if on[w]:
                    low[v] = min(low[v], idx[w])
            if recurse:
                continue
            if low[v] == idx[v]:
                comp: List[int] = []
                while True:
                    w = stack.pop()
                    on[w] = False
                    comp.append(w)
                    if w == v:
                        break
                out.append(comp)
            if work:
                p = work[-1][0]
                low[p] = min(low[p], low[v])
    return out


def _branch_target(ins: Any) -> Optional[int]:
    m = ins.mnem
    if (m in ("call", "jmp") or m.startswith("j")) and ins.ops.startswith("0x"):
        try:
            return int(ins.ops, 16)
        except ValueError:
            return None
    return None


_SSE_ARITH = {"addss": "fadd", "subss": "fsub", "mulss": "fmul", "divss": "fdiv", "minss": "fmin", "maxss": "fmax"}
_SSE = {"movss", "movaps", "movups", "movdqa", "movdqu", "movapd", "movsd", "movshdup", "xorps", "pxor", "cvtsi2ss",
        "cvttss2si", "cvtss2si", "ucomiss", "comiss", "cvtss2sd", "cvtsd2ss", "mulsd", "addsd", "subsd", "divsd",
        "addps", "subps", "mulps", "blendps", "movlps", "movhps", "movq", "movd", "sqrtss", "andps", "orps",
        "shufps", "unpcklps", "cvtps2pd", "cvtdq2ps", "blendvps", "cmpltss", "cmpless", "cmpeqss"} | set(_SSE_ARITH)


def function_end(image: Any, start: int) -> int:
    """End of the function at ``start`` from the .eh_frame function table (needs an Eboot)."""
    from eboot.disasm import eh_function_starts
    cache = getattr(image, "_uicalls_funcs", None)
    if cache is None:
        cache = eh_function_starts(image)
        try:
            image._uicalls_funcs = cache
        except AttributeError:
            pass
    i = bisect.bisect_right(cache, start)
    return cache[i] if i < len(cache) else start + 0x1000


def function_of(image: Any, va: int) -> int:
    from eboot.disasm import eh_function_starts
    cache = getattr(image, "_uicalls_funcs", None)
    if cache is None:
        cache = eh_function_starts(image)
        try:
            image._uicalls_funcs = cache
        except AttributeError:
            pass
    return cache[bisect.bisect_right(cache, va) - 1]


# ---------------------------------------------------------------------------
# evaluation
# ---------------------------------------------------------------------------
class Unbound(Exception):
    """A symbol or runtime memory value the Env does not know."""


@dataclass
class Env:
    """Runtime values for :func:`ev_all`.

    ``mems``: VA of a global -> its value; ``syms``: Sym name -> value; ``iters``: loop header ->
    iteration index; ``loops``: the :class:`LoopInfo` of the trace (for ``Carried``);
    ``static``: extra (lo, hi) ranges that may be read from the image (tables in .data)."""
    image: Any = None
    mems: Dict[int, Any] = field(default_factory=dict)
    syms: Dict[str, Any] = field(default_factory=dict)
    iters: Dict[int, int] = field(default_factory=dict)
    loops: Dict[int, LoopInfo] = field(default_factory=dict)
    static: Sequence[Tuple[int, int]] = ()

    def with_iter(self, header: int, k: int) -> "Env":
        it = dict(self.iters)
        it[header] = k
        return Env(self.image, self.mems, self.syms, it, self.loops, self.static)


_CAP = 64


def _uniq(xs: Iterable[Any]) -> List[Any]:
    out: List[Any] = []
    for x in xs:
        if x not in out:
            out.append(x)
    return out


def ev_all(v: Any, env: Env) -> List[Any]:
    """All values ``v`` can take under ``env`` (a Choice gives several).  Raises Unbound."""
    if v is None:
        raise Unbound("unknown value")
    if isinstance(v, (int, float)):
        return [v]
    if isinstance(v, tuple):
        outs: List[Tuple[Any, ...]] = [()]
        for x in v:
            outs = [o + (e,) for o in outs for e in ev_all(x, env)]
            if len(outs) > _CAP:
                raise Unbound("too many alternatives")
        return _uniq(outs)
    if isinstance(v, Choice):
        res: List[Any] = []
        for w in v.vals:
            res.extend(ev_all(w, env))
        return _uniq(res)
    if isinstance(v, Sym):
        if v.name in env.syms:
            return [env.syms[v.name]]
        raise Unbound(v.name)
    if isinstance(v, Frame):
        raise Unbound("frame address")
    if isinstance(v, Carried):
        li = env.loops.get(v.header)
        if li is None or v.loc not in li.locs:
            raise Unbound(fmt(v))
        k = env.iters.get(v.header, 0)
        ent, bk = li.locs[v.loc]
        if k == 0:
            return ev_all(ent, env)
        return ev_all(bk, env.with_iter(v.header, k - 1))
    if isinstance(v, Mem):
        res = []
        for a in ev_all(v.addr, env):
            if not isinstance(a, int):
                raise Unbound("non-integer address")
            if a in env.mems:
                res.append(env.mems[a])
            elif a < RO_LIMIT or any(lo <= a < hi for lo, hi in env.static):
                if env.image is None:
                    raise Unbound(f"no image for {a:#x}")
                b = env.image.read_va(a, v.size)
                if v.kind == "f" and v.size == 4:
                    res.append(struct.unpack("<f", b)[0])
                elif v.kind == "d" and v.size == 8:
                    res.append(struct.unpack("<d", b)[0])
                else:
                    res.append(int.from_bytes(b, "little", signed=(v.kind == "s")) & M64)
            else:
                raise Unbound(f"mem {a:#x}")
        return _uniq(res)
    if isinstance(v, Op):
        As = ev_all(v.a, env)
        Bs = ev_all(v.b, env) if v.b is not None else [None]
        res = []
        for x in As:
            for y in Bs:
                r = make_op(v.op, x, y)
                if isinstance(r, Op):
                    raise Unbound(f"cannot fold {v.op}")
                if r is None:
                    raise Unbound(f"unknown {v.op}")
                res.append(r)
        return _uniq(res)
    raise Unbound(repr(v))


def ev(v: Any, env: Env) -> Any:
    vals = ev_all(v, env)
    if len(vals) != 1:
        raise Unbound(f"{len(vals)} alternatives")
    return vals[0]


def try_all(v: Any, env: Env) -> Optional[List[Any]]:
    try:
        return ev_all(v, env)
    except (Unbound, KeyError, ZeroDivisionError, struct.error):
        return None


def read_rect(call: Call, env: Env, reg: str = "rcx") -> Optional[List[Tuple[int, int, int, int]]]:
    """The 4 x i16 rectangle a TexDrawRect call points at, for each value the pointer can take."""
    p = call.args.get(reg)
    if p is None:
        return None
    out: List[Tuple[int, int, int, int]] = []
    for cand in (p.vals if isinstance(p, Choice) else (p,)):
        if isinstance(cand, Frame):
            raw = bytearray(8)
            seen = [False] * 8
            for off, (val, size) in call.slots.items():
                if isinstance(val, int) and cand.off <= off < cand.off + 8 and off + size <= cand.off + 8:
                    for i in range(size):
                        raw[off - cand.off + i] = (val >> (8 * i)) & 0xFF
                        seen[off - cand.off + i] = True
            if not all(seen):
                return None
            out.append(struct.unpack("<4h", bytes(raw)))     # type: ignore[arg-type]
        else:
            addrs = try_all(cand, env)
            if addrs is None or env.image is None:
                return None
            for a in addrs:
                out.append(struct.unpack("<4h", env.image.read_va(a, 8)))   # type: ignore[arg-type]
    return _uniq(out)


# ---------------------------------------------------------------------------
# draw sites
# ---------------------------------------------------------------------------
def draw_sites(trace: Trace) -> List[DrawSite]:
    out: List[DrawSite] = []
    for c in trace.calls:
        cal = c.callee
        if cal is not None and cal.kind in DRAW_KINDS:
            out.append(DrawSite(c.va, trace.func, cal.name, cal.kind, c.named(), c.draw, c))
    return out


def widget_passes(traces: Iterable[Trace], group: Optional[int] = None) -> List[Call]:
    """The ``WidgetPass`` calls (0xD0BE0) of some traces, optionally only those of one widget group
    (a constant argument).  ``WidgetDraw`` only stores a widget's position and alpha; the pass draws
    the group's widgets with the canvas and scale in force at *its* call, so ``call.draw["canvas"]``
    of a pass is the canvas of the widgets, not the canvas at the ``WidgetDraw`` site."""
    out: List[Call] = []
    for tr in traces:
        for c in tr.calls:
            if c.target == 0xD0BE0 and (group is None or c.args.get("rdi") == group):
                out.append(c)
    return out


def widget_table(image: Any, table: int) -> List[Tuple[int, int, int]]:
    """The definition records ``WidgetTable`` (0xCF900) registers: ``(widget id, group, flags)``.

    A record is 0x30 bytes; the id is the first dword (a negative id ends the table), the group
    the second (it becomes widget record +8, which the ``WidgetPass`` calls (0xD0BE0) match), the third a flags
    word (record +0xC).  The frame list (texture slot, flags, rectangle) is a pointer at +0x20 and
    is not read here (open item: widget frames).  Tables: title 0x873780 (group 10), menu 0x7A6C00
    (group 1), mode panel 0x7A76D0 (group 14), prompt 0x8731F0 (widgets 0xD5, 0xD6; group 13)."""
    out: List[Tuple[int, int, int]] = []
    for k in range(64):
        wid, grp, flags = struct.unpack("<3i", image.read_va(table + 0x30 * k, 12))
        if wid < 0:
            break
        out.append((wid, grp, flags))
    return out


def site_row(s: DrawSite) -> str:
    a = " ".join(f"{k}={fmt(v)}" for k, v in s.args.items())
    if s.kind == "widget":                  # a setter: the draw state here is not the widget's (see widget_passes)
        return f"{s.va:#08x} {s.fn:<12} {a}   [setter: drawn later by WidgetPass]"
    d = " ".join(f"{k}={fmt(v)}" for k, v in s.draw.items() if v is not None)
    return f"{s.va:#08x} {s.fn:<12} {a}   [{d}]"


# ---------------------------------------------------------------------------
# screens (recipe) and the eboot
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class Screen:
    """A screen's recipe: the draw function(s) and the read-only .data tables they index."""
    name: str
    funcs: Tuple[int, ...]
    note: str = ""
    tables: Tuple[Tuple[int, int], ...] = ()      # (lo, hi) of tables in .data that are only read


SCREENS: Dict[str, Screen] = {
    "title": Screen("title", (0xB1F30,), "title draw 0xB1F30, called by the title draw process 0xB02B0 (system_flow 4.5)",
                    ((0x873910, 0x873940),)),     # TITLE_PARTS logo rectangles, 24 bytes per language
    "choice": Screen("choice", (0xA6E10,), "_SELR / _SELD choice screen draw 0xA6E10 (system_flow 5)"),
    "sysmenu": Screen("sysmenu", (0x6BF10, 0x6C640, 0x6CB80, 0x6E810),
                      "processes 0x10 (backdrop 0x6BF10, petals 0x6C640), 0x11 (menu body 0x6CB80) and 0x12 (mode "
                      "panel 0x6E810) registered from the entry 0x6758E (run_entry)"),
}


def screen_env(image: Any, name: str, trace: Trace, mems: Optional[Dict[int, Any]] = None,
               syms: Optional[Dict[str, Any]] = None, iters: Optional[Dict[int, int]] = None) -> Env:
    """An :class:`Env` for a screen's trace with the screen's static tables."""
    return Env(image, dict(mems or {}), dict(syms or {}), dict(iters or {}), trace.loops, SCREENS[name].tables)


def scan_functions(image: Any, lo: int, hi: int) -> Iterator[Trace]:
    """Decode every function (from the .eh_frame table) that starts in [lo, hi)."""
    from eboot.disasm import eh_function_starts
    starts = eh_function_starts(image)
    for i, f in enumerate(starts):
        if lo <= f < hi:
            end = starts[i + 1] if i + 1 < len(starts) else f + 0x1000
            yield decode_function(image, f, end)


def find_draws(image: Any, lo: int, hi: int, slots: Iterable[int]) -> List[DrawSite]:
    """Draw sites in [lo, hi) whose texture slot can be one of ``slots`` (constants and Choices)."""
    want = set(slots)
    out: List[DrawSite] = []
    for tr in scan_functions(image, lo, hi):
        for s in draw_sites(tr):
            a = s.args.get("slot")
            if a is None:
                continue
            vals = a.vals if isinstance(a, Choice) else (a,)
            if any(isinstance(v, int) and v in want for v in vals):
                out.append(s)
    return out
SYSMENU_ENTRY = 0x6758E        # inside the `lea rdi, [0x7A67F0]` at 0x6758B; UiTableLoad call 0x67597 in Game_StartProcesses 0x67530
SYSMENU_PROCESSES = (0x10, 0x11, 0x12)         # backdrop 0x6BF10, menu body 0x6CB80, mode panel 0x6E810
SYSMENU_TABLES = (0x7A67F0, 0x7A6B20, 0x7A7680)   # menu 8..17 -> slots 328..337, menu 0..7 -> 320..327, modepnl -> 382..383


def open_eboot() -> Any:
    from eboot import eboot_path
    from eboot.selfelf import Eboot
    p = eboot_path()
    if p is None:
        raise FileNotFoundError("eboot.bin not found (set TSUKI_GAME_DIR)")
    return Eboot.open(p)


def decode_function(image: Any, start: int, end: Optional[int] = None) -> Trace:
    return Decoder(image).decode(start, end)


def decode_screen(image: Any, name: str) -> List[Trace]:
    return [decode_function(image, f) for f in SCREENS[name].funcs]


GAME_CODE_END = 0xB5000          # game code comes first; the engine library (UI tables 0xC2AF0, text 0xC8F50, ...) follows


@dataclass(frozen=True)
class Process:
    """One ``ProcessRegister(id, 0, prio, fn, exit)`` call (the game's process table, 0x2084560)."""
    id: int
    prio: int
    fn: int                  # per-frame function (update and draw)
    exit: int                # exit callback, 0 = none
    site: int                # call site
    registered_in: int       # function that makes the call


@dataclass
class EntryRun:
    """Result of running the decoder on a screen entry (see :func:`run_entry`)."""
    entry: int
    func: int                                      # function that contains the entry
    tables: List[Tuple[int, int, int]]             # (call site, registration table VA, force) in decode order
    processes: List[Process]
    traces: Dict[int, Trace]                       # function start -> trace (entry function, initialisers, process functions)
    insn: Optional[Tuple[int, int, str]] = None    # (address, size, text) of the instruction that CONTAINS the entry
    load: Optional[Tuple[int, int, int]] = None    # the first UiTableLoad of the entry function at or after that instruction

    def draws(self, func: int) -> List[DrawSite]:
        return draw_sites(self.traces[func])


def _ints(v: Any) -> Optional[int]:
    return v if isinstance(v, int) else None


def entry_instruction(image: Any, va: int) -> Optional[Tuple[int, int, str]]:
    """The instruction that contains ``va`` as ``(address, size, text)``.

    The roadmap's entry VAs are not instruction boundaries: 0x6758E is the 32-bit displacement of the
    7-byte ``lea rdi, [0x7A67F0]`` at 0x6758B (the call is at 0x67597); 0x6CA83 and 0x6E683 are the
    displacements of the ``lea rdi, [table]`` that starts the 16-byte helpers 0x6CA80 and 0x6E680
    (``lea; xor esi, esi; jmp UiTableLoad``).  A decoder cannot start at such a VA; it starts at the
    function (:func:`function_of`) and the entry only says which table load is meant."""
    f = function_of(image, va)
    for ins in Decoder(image).instructions(f, function_end(image, f)):
        if ins.addr <= va < ins.addr + ins.size:
            return ins.addr, ins.size, f"{ins.mnem} {ins.ops}"
    return None


def run_entry(image: Any, entry: int = SYSMENU_ENTRY, depth: int = 1) -> EntryRun:
    """Run the decoder on a screen entry.

    The PS4 build keeps every screen's layout in its draw function, reached through a game
    process (``ProcessRegister``).  The entry VAs of the roadmap (0x6758E, 0x6CA83, 0x6E683) point
    INTO the ``lea rdi, [parts table]`` that precedes the ``UiTableLoad`` call (they are the lea's
    32-bit displacement, not an instruction boundary and not a call; see :func:`entry_instruction`).
    The function that contains the entry also registers the process.  This does, in order:

    1. decode the function that contains ``entry`` (all of it: ``tables`` and ``processes`` list the
       loads and registrations before the entry too; only the calls AFTER the entry are followed);
    2. decode every game-code function it calls directly (the initialisers, e.g. 0x6CA90 and
       0x6E690), ``depth`` levels deep, and collect their ``UiTableLoad`` and ``ProcessRegister``
       calls;
    3. decode each registered process function and exit callback, plus the game-code functions
       they call that contain draw calls (e.g. the petal effect 0x6C640).
    """
    traces: Dict[int, Trace] = {}
    tables: List[Tuple[int, int, int]] = []
    processes: List[Process] = []

    def visit(f: int, level: int) -> None:
        if f in traces:
            return
        tr = decode_function(image, f)
        traces[f] = tr
        for c in tr.calls:
            if c.target == 0xC2AF0 and _ints(c.args.get("rdi")) is not None:
                tables.append((c.va, c.args["rdi"], _ints(c.args.get("rsi")) or 0))
            elif c.target == 0xECE30:
                pid, prio, fn, ex = (_ints(c.args.get(r)) for r in ("rdi", "rdx", "rcx", "r8"))
                if pid is not None and prio is not None and fn is not None:
                    processes.append(Process(pid, prio, fn, ex or 0, c.va, f))
        if level < depth:
            for c in tr.calls:
                t = c.target
                if t is not None and c.callee is None and 0 < t < GAME_CODE_END and c.va >= (entry if f == func else 0):
                    visit(t, level + 1)

    func = function_of(image, entry)
    visit(func, 0)
    for p in list(processes):
        for f in (p.fn, p.exit):
            if f and f not in traces:
                traces[f] = decode_function(image, f)
                for c in traces[f].calls:
                    t = c.target
                    if t is not None and c.callee is None and 0 < t < GAME_CODE_END and t not in traces:
                        sub = decode_function(image, t)
                        if draw_sites(sub):
                            traces[t] = sub
    insn = entry_instruction(image, entry)
    anchor = insn[0] if insn else entry
    load = next((t for t in tables if anchor <= t[0] < traces[func].end), None)
    return EntryRun(entry, func, tables, processes, traces, insn, load)


def const_args(site: DrawSite, env: Optional[Env] = None) -> Dict[str, Optional[List[Any]]]:
    """Each named argument of a draw site evaluated with no runtime values: a list of its possible
    values, or None where it depends on runtime state (a global, an argument, a loop index)."""
    e = env or Env()
    return {k: try_all(v, e) for k, v in site.args.items()}


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def _fmt_call(c: Call) -> str:
    cal = c.callee
    nm = cal.name if cal else (f"{c.target:#x}" if c.target is not None else "indirect")
    if cal:
        a = " ".join(f"{k}={fmt(v)}" for k, v in c.named().items())
    else:
        a = " ".join(f"{k}={fmt(v)}" for k, v in c.args.items() if v is not None and k in ARG_REGS + XMM_ARGS[:3])
    return f"{c.va:#08x} {c.kind:<4} {nm:<14} {a}"


def format_entry_run(run: EntryRun, process_ids: Optional[Iterable[int]] = None) -> List[str]:
    """Text report of :func:`run_entry`: tables, processes and the draw sites of their functions
    (and of the game-code functions they call that draw)."""
    ids = set(process_ids) if process_ids is not None else None
    lines = [f"# entry {run.entry:#x} in function {run.func:#x}"]
    if run.insn is not None:
        a, n, txt = run.insn
        lines.append(f"# {run.entry:#x} is inside the {n}-byte instruction `{txt}` at {a:#x}"
                     + (f"; its UiTableLoad call is at {run.load[0]:#x}" if run.load else ""))
    for site, tbl, force in run.tables:
        if ids is None or tbl in SYSMENU_TABLES:
            lines.append(f"table  {site:#08x} UiTableLoad {tbl:#x} force={force}")
    shown: set = set()

    def show(f: int, indent: str) -> None:
        tr = run.traces[f]
        lines.append(f"{indent}function {f:#x}..{tr.end:#x}: {len(tr.calls)} calls, {len(tr.loops)} loops")
        lines.extend(indent + "  " + site_row(sd) for sd in draw_sites(tr))
        for c in tr.calls:
            t = c.target
            if t in run.traces and t not in shown and c.callee is None and draw_sites(run.traces[t]):
                shown.add(t)
                show(t, indent + "  ")

    for p in run.processes:
        if ids is not None and p.id not in ids:
            continue
        lines.append(f"process {p.id:#x} prio {p.prio:#x} fn {p.fn:#x} exit {p.exit:#x} "
                     f"(registered at {p.site:#x} in {p.registered_in:#x})")
        for f in (p.fn, p.exit):
            if f and f in run.traces and f not in shown:
                shown.add(f)
                show(f, "  ")
    return lines


def _main(argv: List[str]) -> int:
    getattr(sys.stdout, "reconfigure", lambda **_: None)(encoding="utf-8", errors="replace")
    if not argv:
        print(__doc__)
        return 1
    img = open_eboot()
    cmd = argv[0]
    if cmd in ("calls", "draws"):
        start = int(argv[1], 0)
        end = int(argv[2], 0) if len(argv) > 2 else None
        tr = decode_function(img, start, end)
        if cmd == "calls":
            for c in tr.calls:
                print(_fmt_call(c))
        else:
            for s in draw_sites(tr):
                print(site_row(s))
        return 0
    if cmd == "screen":
        name = argv[1]
        if name == "sysmenu":
            print(*format_entry_run(run_entry(img), SYSMENU_PROCESSES), sep=chr(10))
            return 0
        for tr in decode_screen(img, name):
            print(f"# function {tr.func:#x}..{tr.end:#x}: {len(tr.calls)} calls, {len(tr.loops)} loops")
            for s in draw_sites(tr):
                print(site_row(s))
        return 0
    if cmd == "entry":
        run = run_entry(img, int(argv[1], 0))
        print(*format_entry_run(run), sep=chr(10))
        return 0
    if cmd == "find":
        lo, hi = int(argv[1], 0), int(argv[2], 0)
        slots = [int(x, 0) for x in argv[3:]]
        for s in find_draws(img, lo, hi, slots):
            print(site_row(s))
        return 0
    print(__doc__)
    return 1


if __name__ == "__main__":
    raise SystemExit(_main(sys.argv[1:]))
