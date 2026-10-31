"""allscr.mrg scenario scripts: container access, tokenizer/IR, label tables,
cross-references and readable dumps.

Format summary (details and evidence: docs/formats/script.md)
-------------------------------------------------------------
allscr.mrg is a standalone mrgd00 container (tools/tsuki/mzp.py):

    section 0      NAM32 table, name k <-> script k            (549 names)
    section 1      nested mrgd00, child k = label table of script k
    section 2      nested image MZP (128x96, unused by the scripts)
    section k + 3  script k: MZX0 stream, decoded plain then XOR 0xFF
                   (tsuki.mzx.decompress(..., invert=True))

A decoded script is plain ASCII (UTF-8 only in TEST_SCRIPT) with no line
breaks: a sequence of statements ``_NAME(arg,arg,...);``.  The engine
(main interpreter at eboot text VA 0x95E60) tokenizes like this, and so does
:func:`parse_script`:

* a statement is everything up to and including the next ``;`` (the engine
  scans at most 0x300 bytes);
* the command name is the 4 bytes after the first ``_`` of the statement; the
  first of them must be ``A``..``Z`` (otherwise the statement is skipped);
* arguments start 6 bytes after that ``_`` (the separator byte, normally ``(``, is
  never checked: ``_STTR,0,26)`` works like ``_STTR(0,26)``);
* an argument ends at ``,``; the argument list ends at the first ``)`` or
  ``;``.  Bytes between ``)`` and ``;`` are ignored (``trailer``);
* an argument may start with a 5-byte tag ```NNN:`` (backtick, 3 decimal
  digits, colon): a keyword parameter (e.g. ```017:`` = volume) that the
  handlers find by tag rather than by position;
* there are TWO argument readers, chosen per command (``OBJ_COMMANDS``):
  - the inline readers VA 0x76690 (i16) / 0x76780 (i32) (:func:`inline_int`):
    optional tag skipped, then up to 16 bytes, sum of decimal terms separated by
    ``+``/``-`` (``258-18`` = 240), every other byte ignored (``242.25`` =
    24225), an empty argument is 0, a missing one -1;
  - the object parser VA 0xD690 (:func:`obj_split`, at most 20 arguments,
    ``"..."`` quoting) whose integer getter VA 0xDA50 (:func:`obj_expr`)
    evaluates a C-like expression (``+ - * / % << >> < <= > >= == != & ^ | && ||
    ! ~``, ``{}`` for grouping, right-associative chains, stops at the first
    unknown byte: ``242.25`` = 242); handlers test presence first (VA 0xD8C0)
    and use their own default for missing/empty arguments.

Names starting with ``Z`` are statement markers with an id:
``_Z<k><bbb><ss>(arg)``, k = kind letter (M = message, Z = label, Y = local
label, D = message variant), bbb = 3 hex digits block id, ss = 2 hex digits
sub id (lower-case hex; :func:`marker_hex` reproduces the engine's decoding of
other bytes).  Every name starting with ``J`` is JUMP.

Label tables (section 1 children) are 32-byte ASCII records
``NAME(21, space padded) BBB(3 hex) ',' OOOOO(5 hex) CR LF``: label name,
block id of its ``_ZZ``/``_ZY`` marker and the byte offset of that marker in
the decoded script.
"""
from __future__ import annotations

import argparse
import mmap
import re
import sys
from dataclasses import dataclass
from functools import cached_property
from pathlib import Path
from typing import Iterable, Optional, Protocol, Sequence, Union

from . import image_dir
from . import mzx
from .mzp import MzpArchive
from .nam import load_nam, parse_nam32
from .script_tables import MOVIE_FILES, OBJ_COMMANDS, SCENE_IDS, engine_handler, scene_index

__all__ = [
    "MAX_STATEMENT", "SCRIPT_SECTION_BASE", "ScriptError", "Arg", "Command", "Label",
    "parse_script", "parse_labels", "engine_int", "inline_int", "inline_present", "obj_split", "obj_expr",
    "obj_strict_int", "obj_partial_int", "marker_hex", "ExprUndefined", "OBJ_MAX_ARGS", "text_refs",
    "AllScr", "TextSource", "open_text", "AssetResolver", "AssetRef", "command_assets", "Edge", "script_edges", "scene_graph",
    "render_script", "LANGS",
]

MAX_STATEMENT = 0x300          # engine scan limit for one statement (VA 0x95ECA)
SCRIPT_SECTION_BASE = 3        # script k = allscr section k + 3 (VA 0x7917A)
LABEL_RECORD = 32
LANGS = ("ja", "en", "zc", "zt", "ko")   # script_text.mrg pair index -> language

_TERM = b",);"
_TEXT_REF = re.compile(r"\$(\d{6})")
_VAR = re.compile(r"^(F|G|C|_D)(\d+)$")

Buffer = Union[bytes, bytearray, memoryview]


class ScriptError(ValueError):
    pass


# ---------------------------------------------------------------------------
# arguments, reader 1: the inline integer readers (VA 0x76690 / 0x76780 / 0x76860)
# ---------------------------------------------------------------------------

def _wrap(v: int, bits: int) -> int:
    v &= (1 << bits) - 1
    return v - (1 << bits) if v >> (bits - 1) else v


def _inline_read(buf: bytes, p: int) -> int:
    """Value loop of VA 0x76690/0x76780 starting at buf[p] (unbounded result)."""
    n = len(buf)
    if p < n and buf[p] == 0x60:            # '`': skip a 5-byte keyword tag blindly
        p += 5
    total, num, sign = 0, 0, 1
    for i in range(16):
        c = buf[p + i] if p + i < n else 0
        if c in (0x29, 0x2C, 0x3B):         # ')' ',' ';'
            break
        if c == 0x2B:                       # '+'
            total += sign * num; num = 0; sign = 1
        elif c == 0x2D:                     # '-'
            total += sign * num; num = 0; sign = -1
        elif 0x30 <= c <= 0x39:
            num = num * 10 + (c - 0x30)
        # every other byte (including NUL, '.', 'x', letters) is ignored
    return total + sign * num


def _inline_skip(buf: bytes, p: int, index: int) -> Optional[int]:
    """Skip loop of VA 0x76690/0x76780/0x76860: advance past ``index`` commas.
    None when a ')' or ';' comes first (the engine then returns -1 / false)."""
    n = len(buf)
    for _ in range(max(0, index)):
        for k in range(500):
            c = buf[p + k] if p + k < n else 0
            if c in (0x29, 0x3B):
                return None
            if c == 0x2C:
                p += k + 1
                break
        else:
            p += 500
    return p


def inline_int(buf: Buffer, p0: int, index: int, bits: int = 16) -> int:
    """Engine integer reader on raw statement bytes: VA 0x76690 (``bits=16``) /
    VA 0x76780 (``bits=32``).  ``p0`` = offset of the first argument byte (the
    byte after the separator that follows the 4-letter name).

    Argument ``index`` is reached by skipping ``index`` commas (at most 500 bytes
    each); a ``)`` or ``;`` on the way gives -1.  Then a leading backtick skips 5
    bytes (keyword tag), and at most 16 bytes are read: digits accumulate a
    decimal number, ``+``/``-`` close the current term and set the sign of the
    next one, ``,`` ``)`` ``;`` end the value, every other byte is ignored.  So
    ``()`` index 0 is 0, ``(5)`` index 1 is -1, ``242.25`` is 24225, ``258-18``
    is 240, ``a5b6`` is 56."""
    b = bytes(buf)
    p = _inline_skip(b, p0, index)
    if p is None:
        return -1
    return _wrap(_inline_read(b, p), bits)


def inline_present(buf: Buffer, p0: int, index: int) -> bool:
    """VA 0x76860: argument ``index`` exists and its first byte (after an
    optional tag) is not ``,`` ``)`` ``;``."""
    b = bytes(buf)
    p = _inline_skip(b, p0, index)
    if p is None:
        return False
    if p < len(b) and b[p] == 0x60:
        p += 5
    c = b[p] if p < len(b) else 0
    return c not in (0x29, 0x2C, 0x3B)


def engine_int(raw: str, bits: int = 16) -> int:
    """Value of a single argument text under the inline reader (VA 0x76690 /
    0x76780), e.g. ``engine_int("258-18") == 240``.  Equivalent to
    :func:`inline_int` on ``raw + ")"`` with index 0."""
    return _wrap(_inline_read(raw.encode("utf-8", "surrogateescape") + b")", 0), bits)


# ---------------------------------------------------------------------------
# arguments, reader 2: the object argument parser (VA 0xD690) and its getters
# ---------------------------------------------------------------------------
# Used by most picture/layer commands (STCH, SCH2, FADS, STZ4, STGS, ...; the
# per-command list is OBJ_COMMANDS in script_tables.py).  It splits the argument
# list once into at most 20 (start, end) ranges; integer arguments are then
# evaluated by a small C-like expression evaluator (VA 0xDA50, tokenizer
# VA 0xDE30) - NOT by the inline reader above.

OBJ_MAX_ARGS = 20


def obj_split(buf: Buffer, sep: int) -> list[tuple[int, int]]:
    """VA 0xD690: split the argument list.  ``buf[sep]`` is the separator byte
    after the 4-letter name (normally ``(``).  Returns (start, end) offsets.

    * ``buf[sep+1]`` is ``)`` or ``;``: no arguments (``()`` has 0 arguments).
    * An argument starting with ``"`` runs to the next ``"`` (strchr; may contain
      ``,`` ``)`` ``;``); the byte after the closing quote is the separator.
    * Otherwise it runs to the next ``,`` ``)`` or ``;`` (strpbrk).
    * The list ends when the separator is ``)`` or ``;``, or after 20 arguments.
    """
    b = bytes(buf)
    n = len(b)

    def at(i: int) -> int:
        return b[i] if i < n else 0

    out: list[tuple[int, int]] = []
    if at(sep + 1) in (0x29, 0x3B):
        return out
    if b.find(b'"', sep + 1) < 0:                # fast path, same result: no quoted argument
        ends = [x for x in (b.find(b")", sep + 1), b.find(b";", sep + 1)) if x >= 0]
        if not ends:
            raise ScriptError("argument without , ) or ; (the engine's strpbrk would return NULL)")
        start = sep + 1
        for part in b[start:min(ends)].split(b","):
            out.append((start, start + len(part)))
            start += len(part) + 1
            if len(out) == OBJ_MAX_ARGS:
                break
        return out
    q = sep + 1
    while True:
        if at(q) == 0x22:
            start = q + 1
            e = b.find(b'"', start)
            if e < 0:
                raise ScriptError("unterminated quoted argument (the engine's strchr would run past the statement)")
        else:
            start = q
            cands = [x for x in (b.find(b",", start), b.find(b")", start), b.find(b";", start)) if x >= 0]
            if not cands:
                raise ScriptError("argument without , ) or ; (the engine's strpbrk would return NULL)")
            e = min(cands)
        out.append((start, e))
        skip = 1 if at(e) == 0x22 else 0
        if at(e + skip) in (0x29, 0x3B) or len(out) == OBJ_MAX_ARGS:
            return out
        q = e + skip + 1


class ExprUndefined(ScriptError):
    """The expression has no defined engine value (the PS4 code would crash or
    return stack garbage): division by zero, an operator without operands, or an
    error inside ``{...}``."""


# operator codes of the tokenizer (high nibble 2 = unary, 3 = binary; next
# nibble = precedence level, lower binds tighter)
_UPLUS, _UNEG, _UNOT, _UCOMPL = 0x22000001, 0x22000002, 0x22000003, 0x22000004
_MUL, _DIV, _MOD = 0x33000001, 0x33000002, 0x33000003
_ADD, _SUB = 0x34000001, 0x34000002
_SHL, _SHR = 0x35000001, 0x35000002
_LT, _LE, _GT, _GE = 0x36000001, 0x36000002, 0x36000003, 0x36000004
_EQ, _NE = 0x37000001, 0x37000002
_BAND, _BXOR, _BOR, _LAND, _LOR = 0x38000001, 0x39000001, 0x3A000001, 0x3B000001, 0x3C000001

_LONG_MAX = (1 << 63) - 1


def _strtol10(s: bytes, p: int) -> tuple[int, int]:
    """C strtol(s + p, &end, 10) for a string that starts with a digit (the only
    way VA 0xDE30 calls it): (value clamped to 64-bit long, end offset)."""
    q = p
    while q < len(s) and 0x30 <= s[q] <= 0x39:
        q += 1
    v = int(s[p:q])
    return min(v, _LONG_MAX), q


def _tokenize(s: bytes, out: list[int]) -> None:
    """VA 0xDE30 (shunting-yard, output in postfix order into ``out``).

    Operands are decimal numbers (strtol base 10: '0x10' is 0 followed by a stray
    'x') and ``{...}`` groups (the parentheses of this language; ``(`` ``)`` are
    argument delimiters).  Precedence levels as in C; operators of *equal*
    level are not popped, so chains are right-associative: ``10-3-2`` = 9.
    An unknown byte, a lone ``=``, or an operator as the last byte stops the
    tokenizer WITHOUT flushing its pending operators (what was output so far is
    evaluated)."""
    end = len(s)

    def at(i: int) -> int:
        return s[i] if i < end else 0

    class _Stop(Exception):
        pass

    def level(code: int) -> int:
        return (code >> 24) & 0xF

    def run(p: int, term: int) -> int:
        ops: list[int] = []

        def finish(p: int) -> int:
            while ops:
                out.append(ops.pop())
            return p

        c = at(p)
        if c == 0 or c == term or p >= end:
            return finish(p)
        after = False                      # False: expecting an operand; True: after an operand
        while True:
            c = at(p)
            if after and (c == 0 or c == term or p >= end):
                return finish(p)
            if 0x30 <= c <= 0x39:
                v, p = _strtol10(s, p)
                out.append(0x10000000)     # operand marker, value follows
                out.append(_wrap(v, 32))
                after = True
                continue
            if c == 0x7B:                  # '{'
                try:
                    r = run(p + 1, 0x7D)
                except _Stop:
                    raise ExprUndefined("error inside {...}: the engine dereferences address 1") from None
                p = r + 1
                after = True
                continue
            if end - p < 2:                # operator as the last byte: stop, no flush
                raise _Stop
            nxt = at(p + 1)
            if c == 0x21:                  # '!'
                code = _NE if nxt == 0x3D else _UNOT
                p += 1 if nxt == 0x3D else 0
            elif c == 0x25:
                code = _MOD
            elif c == 0x26:                # '&'
                code = _LAND if nxt == 0x26 else _BAND
                p += 1 if nxt == 0x26 else 0
            elif c == 0x2A:
                code = _MUL
            elif c == 0x2B:
                code = _ADD if after else _UPLUS
            elif c == 0x2D:
                code = _SUB if after else _UNEG
            elif c == 0x2F:
                code = _DIV
            elif c == 0x3C:                # '<'
                code = _LE if nxt == 0x3D else _SHL if nxt == 0x3C else _LT
                p += 1 if nxt in (0x3D, 0x3C) else 0
            elif c == 0x3D:                # '=' must be '=='
                if nxt != 0x3D:
                    raise _Stop
                code = _EQ
                p += 1
            elif c == 0x3E:                # '>'
                code = _GE if nxt == 0x3D else _SHR if nxt == 0x3E else _GT
                p += 1 if nxt in (0x3D, 0x3E) else 0
            elif c == 0x5E:
                code = _BXOR
            elif c == 0x7C:                # '|'
                code = _LOR if nxt == 0x7C else _BOR
                p += 1 if nxt == 0x7C else 0
            elif c == 0x7E:
                code = _UCOMPL
            else:
                raise _Stop                # any other byte (letters, '.', '`', space, ...)
            lv = level(code)
            while ops and lv > level(ops[-1]):
                out.append(ops.pop())
            ops.append(code)
            p += 1
            c = at(p)
            if c == 0 or c == term or p >= end:
                return finish(p)
            after = False

    try:
        run(0, 0)
    except _Stop:
        pass


def _apply(code: int, a: int, b: int) -> int:
    if code == _MUL:
        return _wrap(a * b, 32)
    if code in (_DIV, _MOD):
        if b == 0 or (a == -(1 << 31) and b == -1):
            raise ExprUndefined("division fault (idiv)")
        q = abs(a) // abs(b) * (1 if (a < 0) == (b < 0) else -1)     # truncate toward zero
        return q if code == _DIV else a - q * b
    if code == _ADD:
        return _wrap(a + b, 32)
    if code == _SUB:
        return _wrap(a - b, 32)
    if code == _SHL:
        return _wrap(a << (b & 31), 32)
    if code == _SHR:
        return a >> (b & 31)
    if code in (_LT, _LE, _GT, _GE, _EQ, _NE):
        return int({_LT: a < b, _LE: a <= b, _GT: a > b, _GE: a >= b, _EQ: a == b, _NE: a != b}[code])
    if code == _BAND:
        return a & b
    if code == _BXOR:
        return a ^ b
    if code == _BOR:
        return a | b
    if code == _LAND:
        return int(a != 0 and b != 0)
    if code == _LOR:
        return int((a | b) != 0)
    return a


def obj_expr(arg: Union[str, Buffer]) -> int:
    """Integer value of one object-parser argument (VA 0xDA50 on [start, end)).

    Examples: ``"258-18"`` = 240, ``"242.25"`` = 242 (stops at '.'), ``"2000.0"``
    = 2000, ``"0x000000"`` = 0, ``"img1728"`` = 0, ``"`017:100"`` = 0 (no tag
    support), ``"10-3-2"`` = 9 (right-associative), ``"{10-3}-2"`` = 5,
    ``"2*-3"`` = -6, ``"1<<4|1"`` = 17, ``""`` = 0.  32-bit signed arithmetic.
    Raises :class:`ExprUndefined` where the engine has no defined result."""
    s = arg.encode("utf-8", "surrogateescape") if isinstance(arg, str) else bytes(arg)
    toks: list[int] = []
    _tokenize(s, toks)
    if not toks:
        return 0
    st: list[int] = []
    i = 0
    while i < len(toks):
        t = toks[i]
        kind = t >> 28
        if t == 0x10000000:
            st.append(toks[i + 1])
            i += 2
            continue
        if kind == 2:
            if not st:
                raise ExprUndefined("unary operator without operand")
            x = st[-1]
            st[-1] = {_UNEG: _wrap(-x, 32), _UNOT: int(x == 0), _UCOMPL: ~x}.get(t, x)
        elif kind == 3:
            if len(st) < 2:
                raise ExprUndefined("binary operator without two operands")
            b = st.pop()
            st[-1] = _apply(t, st[-1], b)
        i += 1
    if not st:
        raise ExprUndefined("empty evaluation stack")
    return st[-1]


def obj_strict_int(arg: Union[str, Buffer]) -> Optional[int]:
    """VA 0xE310 / 0xE3C0: strtol(arg, &end, 10) on a copy of the argument
    (1..15 bytes); the value when the whole argument was consumed, else None
    (leading white space and one sign are accepted by strtol, ``"5 "`` and
    ``"242.25"`` are not).  Result truncated to 32 bits."""
    s = arg.encode("utf-8", "surrogateescape") if isinstance(arg, str) else bytes(arg)
    if not 1 <= len(s) <= 15:
        return None
    m = re.fullmatch(rb"[ \t\n\v\f\r]*([+-]?)([0-9]+)", s)
    if m is None:
        return None
    v = int(m.group(2))
    v = -min(v, _LONG_MAX + 1) if m.group(1) == b"-" else min(v, _LONG_MAX)
    return _wrap(v, 32)


def obj_partial_int(arg: Union[str, Buffer]) -> Optional[int]:
    """The value VA 0xE3C0 stores even when it reports failure: strtol of the
    leading part (0 when there is no number; None when the argument is empty or
    longer than 15 bytes, in which case nothing is stored).  Used by TOMP."""
    s = arg.encode("utf-8", "surrogateescape") if isinstance(arg, str) else bytes(arg)
    if not 1 <= len(s) <= 15:
        return None
    m = re.match(rb"[ \t\n\v\f\r]*([+-]?)([0-9]+)", s)
    if m is None:
        return 0
    v = int(m.group(2))
    v = -min(v, _LONG_MAX + 1) if m.group(1) == b"-" else min(v, _LONG_MAX)
    return _wrap(v, 32)


def marker_hex(chars: bytes) -> int:
    """Marker id digits as the Z handler decodes them (VA 0x9665A..0x966E3):
    ``d = c - 0x30`` if ``c < 'a'`` else ``c - 0x57``, accumulated as
    ``v = v*16 + d`` and truncated to a signed 16-bit value.  Only lower-case
    hex is decoded correctly: ``"00A"`` gives 0x11 (17), not 10."""
    v = 0
    for c in chars:
        v = v * 16 + (c - 0x30 if c < 0x61 else c - 0x57)
    return _wrap(v, 16)


@dataclass(frozen=True)
class Arg:
    raw: str

    @property
    def tag(self) -> Optional[int]:
        """Keyword tag of ```NNN:value`` arguments, else None."""
        r = self.raw
        if len(r) >= 5 and r[0] == "`" and r[1:4].isdigit() and r[4] == ":":
            return int(r[1:4])
        return None

    @property
    def value(self) -> str:
        """The argument without its tag."""
        return self.raw[5:] if self.raw.startswith("`") else self.raw

    @property
    def empty(self) -> bool:
        """Engine 'present' test (VA 0x76860) is ``not empty``."""
        return self.value == ""

    def to_int(self, bits: int = 16) -> int:
        return engine_int(self.raw, bits)

    @property
    def var(self) -> Optional[tuple[str, int]]:
        """(namespace, number) for variable references F123 / G008 / C847 / _D000."""
        m = _VAR.match(self.value)
        return (m.group(1), int(m.group(2))) if m else None

    def __str__(self) -> str:
        return self.raw


def text_refs(s: str) -> list[int]:
    """script_text line ids (``$NNNNNN``, 6 decimal digits) in a text argument."""
    return [int(x) for x in _TEXT_REF.findall(s)]


# ---------------------------------------------------------------------------
# statements
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Command:
    index: int                 # statement number within the script
    offset: int                # byte offset of the statement (just after the previous ';')
    name_offset: int           # byte offset of the leading '_' (-1 if none)
    end: int                   # byte offset just after the terminating ';'
    key: str                   # the 4 bytes after '_' (what the engine dispatches on)
    name: str                  # display name: key, or 'ZM'/'ZZ'/'ZY'/'ZD' for markers
    args: tuple[Arg, ...]
    raw: str                   # the statement text including ';'
    trailer: str = ""          # bytes between ')' and ';' (ignored by the engine)
    block: Optional[int] = None    # Z markers: block id as the engine decodes it (marker_hex)
    sub: Optional[int] = None      # Z markers: sub id as the engine decodes it

    @property
    def is_marker(self) -> bool:
        return self.block is not None

    @property
    def handled(self) -> bool:
        """False for statements the PS4 interpreter skips (DMMY, NOP_, IFP4,
        SNX4, FANX, STQK, malformed)."""
        return self.name_offset >= 0 and engine_handler(self.key) is not None

    @cached_property
    def raw_bytes(self) -> bytes:
        return self.raw.encode("utf-8", "surrogateescape")

    @property
    def arg_start(self) -> int:
        """Offset in :attr:`raw_bytes` of the first argument byte for the inline
        readers: '_' + 4 name bytes + 1 separator byte."""
        return self.name_offset - self.offset + 6

    def arg(self, i: int) -> Optional[Arg]:
        return self.args[i] if i < len(self.args) else None

    def int_arg(self, i: int, bits: int = 16) -> int:
        """Inline-reader value (VA 0x76690 i16 / 0x76780 i32) of argument i:
        -1 when it does not exist (a ')' or ';' comes before i commas), 0 when it
        is empty (also for index 0 of ``()``).  Use :meth:`value` for the
        reader the command's handler actually uses."""
        if self.name_offset < 0:
            return -1
        return inline_int(self.raw_bytes, self.arg_start, i, bits)

    def present(self, i: int) -> bool:
        """Inline "argument present" test (VA 0x76860)."""
        return self.name_offset >= 0 and inline_present(self.raw_bytes, self.arg_start, i)

    @cached_property
    def obj_args(self) -> tuple[str, ...]:
        """Arguments as split by the object parser (VA 0xD690): ``()`` has none,
        quoted arguments lose their quotes, at most 20."""
        if self.name_offset < 0:
            return ()
        b = self.raw_bytes
        return tuple(b[s:e].decode("utf-8", "surrogateescape")
                     for s, e in obj_split(b, self.arg_start - 1))

    def obj_has(self, i: int) -> bool:
        """VA 0xD8C0: object argument i exists and is non-empty."""
        a = self.obj_args
        return i < len(a) and a[i] != ""

    def expr_arg(self, i: int) -> Optional[int]:
        """Object-parser value of argument i (VA 0xDA50), or None when the
        argument is missing or empty (``obj_has`` false: handlers then use their
        own default)."""
        a = self.obj_args
        if i >= len(a) or a[i] == "":
            return None
        return obj_expr(a[i])

    def value(self, i: int) -> Optional[int]:
        """Integer argument i read the way this command's PS4 handler reads it:
        object-parser commands (``OBJ_COMMANDS``) -> :meth:`expr_arg` (None =
        absent, handler default); TOMP -> strtol of the leading part; all others
        -> the inline reader :meth:`int_arg`."""
        if self.key in OBJ_COMMANDS:
            if self.key == "TOMP":
                a = self.obj_args
                return obj_partial_int(a[i]) if i < len(a) and a[i] else None
            return self.expr_arg(i)
        return self.int_arg(i)

    def tagged(self, tag: int) -> Optional[Arg]:
        """The first argument carrying keyword tag ``tag`` (```NNN:``)."""
        for a in self.args:
            if a.tag == tag:
                return a
        return None

    @property
    def text_ids(self) -> list[int]:
        if self.name in ("ZM", "ZD", "MSAD", "SELR"):
            return [i for a in self.args for i in text_refs(a.raw)]
        return []

    def __str__(self) -> str:
        return self.raw


def _split_args(data: bytes, start: int, stop: int) -> tuple[list[str], int]:
    """Split ``data[start:stop]`` at ',' until ')' or ';'.  Returns (args, close)
    where close is the index of the terminating ')' / ';' (or stop)."""
    args: list[str] = []
    cur = start
    i = start
    while i < stop:
        c = data[i]
        if c == 0x2C:                      # ','
            args.append(data[cur:i].decode("utf-8", "surrogateescape"))
            cur = i + 1
        elif c in (0x29, 0x3B):            # ')' ';'
            break
        i += 1
    tail = data[cur:i].decode("utf-8", "surrogateescape")
    if args or tail:
        args.append(tail)
    return args, i


def parse_script(data: Buffer) -> list[Command]:
    """Tokenize a decoded script into statements, engine-faithfully."""
    buf = bytes(data)
    n = len(buf)
    out: list[Command] = []
    pos = 0
    idx = 0
    while pos < n:
        limit = min(n, pos + MAX_STATEMENT)
        semi = buf.find(b";", pos, limit)
        end = (semi + 1) if semi >= 0 else limit
        if semi < 0 and limit < n:
            raise ScriptError(f"statement at 0x{pos:x} longer than 0x{MAX_STATEMENT:x} bytes")
        us = buf.find(b"_", pos, end)
        raw = buf[pos:end].decode("utf-8", "surrogateescape")
        if us < 0 or us + 5 > end:
            out.append(Command(idx, pos, -1, end, "", "", (), raw))
        else:
            key = buf[us + 1:us + 5].decode("latin-1")
            block = sub = None
            name = key
            astart = us + 5 + 1                       # skip name and one separator byte
            if key[:1] == "Z" and len(key) == 4:
                hexid = buf[us + 3:us + 8]
                if len(hexid) == 5:                   # engine decoding, any bytes (marker_hex)
                    block, sub = marker_hex(hexid[:3]), marker_hex(hexid[3:])
                name = "Z" + key[1]
                astart = us + 8 + 1
            elif key[:1] == "J":
                name = key                            # every J... is JUMP for the engine
            astart = min(astart, end)
            args, close = _split_args(buf, astart, end)
            trailer = ""
            if close < end and buf[close] == 0x29:  # ')'
                trailer = buf[close + 1:end - 1 if semi >= 0 else end].decode("utf-8", "surrogateescape")
            if not ("A" <= key[:1] <= "Z"):
                out.append(Command(idx, pos, -1, end, key, key, (), raw))
            else:
                out.append(Command(idx, pos, us, end, key, name, tuple(Arg(a) for a in args),
                                   raw, trailer, block, sub))
        idx += 1
        pos = end
    return out


# ---------------------------------------------------------------------------
# label tables
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Label:
    name: str
    block: int        # block id of the _ZZ/_ZY marker that defines it
    offset: int       # byte offset of that marker in the decoded script


def parse_labels(data: Buffer) -> list[Label]:
    """Parse a label table (allscr section 1 child)."""
    b = bytes(data)
    if len(b) % LABEL_RECORD:
        raise ScriptError(f"label table size {len(b)} is not a multiple of 32")
    out: list[Label] = []
    for i in range(0, len(b), LABEL_RECORD):
        r = b[i:i + LABEL_RECORD]
        if r[24:25] != b"," or r[30:32] != b"\r\n":
            raise ScriptError(f"bad label record {i // 32}: {r!r}")
        out.append(Label(r[:21].decode("ascii").rstrip(" "), int(r[21:24], 16), int(r[25:30], 16)))
    return out


# ---------------------------------------------------------------------------
# archive access
# ---------------------------------------------------------------------------

def _open_mm(path: Path) -> tuple[object, mmap.mmap]:
    f = open(path, "rb")
    return f, mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)


class AllScr:
    """Read-only access to image/allscr.mrg."""

    def __init__(self, path: Union[str, Path, None] = None) -> None:
        if path is None:
            d = image_dir()
            if d is None:
                raise FileNotFoundError("game folder not found (set TSUKI_GAME_DIR)")
            path = d / "allscr.mrg"
        self.path = Path(path)
        self._f, self._mm = _open_mm(self.path)
        self.arc = MzpArchive(self._mm)
        self.names: list[str] = parse_nam32(self.arc.read(0)).names
        self._labels_arc = self.arc.child(1)
        if len(self.names) + SCRIPT_SECTION_BASE != len(self.arc) or len(self._labels_arc) != len(self.names):
            raise ScriptError("allscr.mrg layout: names / label tables / scripts do not line up")
        self._cache: dict[int, bytes] = {}

    def close(self) -> None:
        self._labels_arc = None  # type: ignore[assignment]
        self.arc = None  # type: ignore[assignment]
        self._mm.close()
        self._f.close()  # type: ignore[attr-defined]

    def __enter__(self) -> "AllScr":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    def __len__(self) -> int:
        return len(self.names)

    def index(self, key: Union[int, str]) -> int:
        """Script index from an index, a script name (``00_00``) or ``#<scene id>``."""
        if isinstance(key, int):
            return key
        if key.startswith("#"):
            i = scene_index(int(key[1:]))
            if i is None:
                raise KeyError(key)
            return i
        if key.isdigit() and int(key) < len(self.names):
            return int(key)
        return self.names.index(key)

    @staticmethod
    def scene_id(i: int) -> int:
        return SCENE_IDS[i]

    def raw(self, i: int) -> bytes:
        """Decoded script bytes (MZX decode, then XOR 0xFF)."""
        if i not in self._cache:
            self._cache[i] = mzx.decompress(self.arc.view(i + SCRIPT_SECTION_BASE), invert=True)
        return self._cache[i]

    def commands(self, i: int) -> list[Command]:
        return parse_script(self.raw(i))

    def labels(self, i: int) -> list[Label]:
        return parse_labels(self._labels_arc.view(i))

    def label_map(self, i: int) -> dict[str, Label]:
        return {lab.name: lab for lab in self.labels(i)}


class TextSource(Protocol):
    """Anything that expands ``$NNNNNN`` references, e.g. tsuki.text.ScriptText
    (the text module owns script_text.mrg; this module only needs ``expand``)."""

    def expand(self, arg: str, lang: Union[str, int]) -> str: ...


def open_text() -> TextSource:
    """The project's script_text.mrg reader (tsuki.text.ScriptText)."""
    from .text import ScriptText
    return ScriptText()


# ---------------------------------------------------------------------------
# cross references
# ---------------------------------------------------------------------------

IMAGE_ARCHIVES = ("allpac0", "allpac1", "allpac1_2", "allpac4")   # disjoint full-size sets
IMAGE_SMALL = "allpac2"                                           # reduced copies


@dataclass(frozen=True)
class AssetRef:
    kind: str                 # image / voice / se / bgm / movie / scene
    key: str                  # what the script wrote
    archive: Optional[str]    # e.g. "allpac1", "voice", "vse", "bgm", "movie"
    index: Optional[int]      # entry index in that archive (None: unresolved)
    note: str = ""
    layers: tuple[tuple[str, int], ...] = ()   # images: full-size layers (base, then face)
    catalog: Optional[int] = None             # images: engine catalog record = allpac2 index
    missing: tuple[str, ...] = ()             # images: catalog layer files found in no archive


class AssetResolver:
    """Maps script arguments to archive entries (lazy-loads the NAM tables)."""

    def __init__(self, img_dir: Union[str, Path, None] = None, eboot: Union[str, Path, None] = None) -> None:
        d = Path(img_dir) if img_dir is not None else image_dir()
        if d is None:
            raise FileNotFoundError("game folder not found (set TSUKI_GAME_DIR)")
        self.dir = d
        eb = Path(eboot) if eboot is not None else d.parent / "eboot.bin"
        self.eboot: Optional[Path] = eb if eb.exists() else None
        self._catalog: Optional[dict[str, tuple[int, tuple[str, ...]]]] = None
        self._images: Optional[dict[str, tuple[str, int]]] = None
        self._small: Optional[dict[str, int]] = None
        self._voice: Optional[list[str]] = None
        self._se: Optional[dict[str, int]] = None

    def _load_images(self) -> None:
        self._images = {}
        for a in IMAGE_ARCHIVES:
            for i, n in enumerate(load_nam(self.dir / f"{a}.nam").names):
                self._images.setdefault(n.upper(), (a, i))
        self._small = {n.upper(): i for i, n in enumerate(load_nam(self.dir / f"{IMAGE_SMALL}.nam").names)}

    def _load_catalog(self) -> None:
        # the image catalog compiled into eboot.bin (owned by tsuki.image_catalog):
        # record i <-> allpac2 entry i, layers name0 (+ name1 face) in the full-size archives
        from .image_catalog import load_catalog
        assert self.eboot is not None
        self._catalog = {e.thumb_name: (e.index, tuple(e.layers)) for e in load_catalog(self.eboot)}

    def image(self, name: str) -> AssetRef:
        """Image id as written in STCH/SCH2/SPFC/SPRD/STCG (``img2345``,
        ``aki_a_30_01_n01``, ``WHITE``).

        With eboot.bin available the id is looked up in the engine's image
        catalog (``<NAME>.MZP`` == allpac2 name of the record) and resolved to
        its layers: base (name0) and, for character sprites, the face layer
        (name1), each found in allpac0/1/1_2/4.  Without eboot.bin the id is
        looked up directly as ``<NAME>.MZP`` in allpac0/1/1_2/4 (for sprites
        that is the face layer only)."""
        if self._images is None:
            self._load_images()
        assert self._images is not None and self._small is not None
        key = name.upper() + ".MZP"
        if self.eboot is not None:
            if self._catalog is None:
                self._load_catalog()
            assert self._catalog is not None
            rec = self._catalog.get(key)
            if rec is not None:
                layers = tuple(self._images[n] for n in rec[1] if n in self._images)
                missing = tuple(n for n in rec[1] if n not in self._images)
                note = f"catalog #{rec[0]}" + (" base+face" if len(rec[1]) > 1 else "")
                if missing:
                    # the engine's file search (VA 0xC3270) returns -1 and the texture load
                    # (VA 0xEB190) returns at once for a negative file index: that layer is
                    # simply not loaded; the other layer still is
                    note += " (missing: " + ", ".join(missing) + ")"
                first = layers[0] if layers else (None, None)
                return AssetRef("image", name, first[0], first[1], note, layers, rec[0], missing)
        hit = self._images.get(key)
        small = self._small.get(key)
        note = f"small copy allpac2#{small}" if small is not None else ""
        if hit is None:
            return AssetRef("image", name, None, None, note)
        return AssetRef("image", name, hit[0], hit[1], note, (hit,))

    def voice(self, cmd: Command) -> AssetRef:
        """VPLY/VPL2(name, hex index): voice.hed entry = int(arg1, 16)."""
        if self._voice is None:
            self._voice = load_nam(self.dir / "voice.nam").names
        name = cmd.args[0].value if cmd.args else ""
        try:
            idx = int(cmd.args[1].value, 16)
        except (IndexError, ValueError):
            return AssetRef("voice", name, "voice", None, "no index")
        ok = idx < len(self._voice) and name.startswith(self._voice[idx])
        return AssetRef("voice", name, "voice", idx, "" if ok else "NAM8 prefix mismatch")

    def se(self, name: str) -> AssetRef:
        """SEPL name -> vse.hed entry via se_nam.mrg section 2."""
        if self._se is None:
            f, mm = _open_mm(self.dir / "se_nam.mrg")
            try:
                arc = MzpArchive(mm)
                self._se = {n.upper(): i for i, n in enumerate(parse_nam32(arc.read(2)).names)}
                del arc
            finally:
                mm.close()
                f.close()  # type: ignore[attr-defined]
        i = self._se.get(name.upper())
        return AssetRef("se", name, "vse", i)

    @staticmethod
    def bgm(n: int) -> AssetRef:
        """MPLY(n): bgm.hed entry n (engine VA 0x98C0 / 0x9B40 with the
        alternative-BGM option off; with config byte +0x1E == 1 and 1 <= n <= 131
        the engine would use n + 131, which does not exist on this disc)."""
        return AssetRef("bgm", str(n), "bgm", n if 0 <= n < 132 else None)

    @staticmethod
    def movie(n: int) -> AssetRef:
        f = MOVIE_FILES[n] if 0 <= n < len(MOVIE_FILES) else None
        return AssetRef("movie", str(n), "movie", n if f else None, f or "")

    @staticmethod
    def scene(scene_id: int) -> AssetRef:
        i = scene_index(scene_id)
        return AssetRef("scene", str(scene_id), "allscr", i)


IMAGE_ARG = {"STCH": 1, "SCH2": 1, "SPFC": 1, "SPRD": 1, "STCG": 0}
PICTURE_COMMANDS = ("STCH", "SCH2")      # shared backend VA 0x81760
NUM_LAYERS = 40


def _variant_digits(name: str) -> Optional[tuple[int, int]]:
    """(hh, vv) parsed from a layer's current picture name ``..._hh_vv`` the way
    VA 0x81862..0x81935 does: needs len >= 7, '_' at len-6 and len-3, and either
    the prefix ``IMG`` or len >= 15; the four bytes are not validated."""
    n = len(name)
    if n < 7 or name[n - 6] != "_" or name[n - 3] != "_":
        return None
    if not (name.startswith("IMG") or n >= 15):
        return None
    d = [ord(ch) - 0x30 for ch in name[n - 5:n - 3] + name[n - 2:]]
    return 10 * d[0] + d[1], 10 * d[2] + d[3]


def picture_key(cmd: Command, src_name: Optional[str] = None) -> Optional[str]:
    """Name that STCH / SCH2 (backend VA 0x81760) looks up in the picture name
    map (VA 0x78410; key = upper-case, at most 32 bytes, cut at the first '.').

    The picture is argument 1.  Arguments 9 and 10 (h, v) select a pre-made
    variant: when ``h | v != 0`` the engine appends ``"_%02d_%02d"`` (digits of
    h % 100 and v % 100; a value <= 0 gives "00"), e.g. ``img1696`` with 11,11
    -> ``IMG1696_11_11``.  When argument 18 names a source layer (0..39) whose
    current picture name ends in ``_hh_vv`` (see :func:`_variant_digits`), h and
    v default to those digits; explicit arguments 9/10 override them.
    ``src_name`` is that layer's current key (the caller tracks layer state,
    e.g. with :class:`LayerTracker`)."""
    a = cmd.obj_args
    if len(a) < 2 or not a[1]:
        return None
    name = a[1][:255]
    h = v = 0
    src = cmd.expr_arg(18)
    if src is not None and 0 <= src < NUM_LAYERS and src_name:
        dv = _variant_digits(src_name)
        if dv is not None:
            h, v = dv
    eh, ev = cmd.expr_arg(9), cmd.expr_arg(10)
    h = h if eh is None else eh
    v = v if ev is None else ev
    if h | v:
        name += "_%02d_%02d" % (h % 100 if h > 0 else 0, v % 100 if v > 0 else 0)
    return _ascii_upper(name.split(".", 1)[0][:32])


def _ascii_upper(s: str) -> str:
    """toupper() of the C locale (VA 0x78486 uses the libc table): a-z only."""
    return s.translate(_UPPER)


_UPPER = {c: c - 32 for c in range(0x61, 0x7B)}


class LayerTracker:
    """Current picture key per layer, following STCH / SCH2 / STCC in statement
    order (no control flow: an approximation used for cross-references)."""

    def __init__(self) -> None:
        self.names: dict[int, str] = {}

    def src_name(self, cmd: Command) -> Optional[str]:
        src = cmd.expr_arg(18) if cmd.key in PICTURE_COMMANDS else None
        return self.names.get(src) if src is not None else None

    def update(self, cmd: Command) -> None:
        if cmd.key in PICTURE_COMMANDS:
            layer = cmd.expr_arg(0)
            key = picture_key(cmd, self.src_name(cmd))
            if layer is not None and 0 <= layer < NUM_LAYERS and key:
                self.names[layer] = key
        elif cmd.key == "STCC":
            layer = cmd.expr_arg(0)
            if layer == -1:
                self.names.clear()
            elif layer is not None:
                self.names.pop(layer, None)


def command_assets(cmd: Command, res: AssetResolver, layers: Optional[LayerTracker] = None) -> list[AssetRef]:
    """Asset references made by one command.  For STCH/SCH2 the picture name
    includes the variant suffix (:func:`picture_key`); pass a
    :class:`LayerTracker` (updated by the caller after each command) to resolve
    suffixes inherited from a source layer."""
    n = cmd.name
    if n in PICTURE_COMMANDS:
        key = picture_key(cmd, layers.src_name(cmd) if layers is not None else None)
        if key and not key.lstrip("-").isdigit():
            return [res.image(key)]
        return []
    if n in IMAGE_ARG:
        a = cmd.arg(IMAGE_ARG[n])
        if a is not None and a.value and not a.value.lstrip("-").isdigit():
            return [res.image(a.value)]
        return []
    if n in ("VPLY", "VPL2"):
        return [res.voice(cmd)]
    if n == "SEPL" and len(cmd.args) > 2:
        return [res.se(cmd.args[2].value)]
    if n == "MPLY" and cmd.args:
        return [res.bgm(cmd.int_arg(0))]
    if n == "TOMP" and cmd.args:
        m = cmd.value(0)
        return [res.movie(m if m is not None else 0)]
    if n.startswith("J") and cmd.args:
        return [res.scene(cmd.int_arg(0))]
    return []


# ---------------------------------------------------------------------------
# control flow / scene graph
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Edge:
    src: int                  # script index
    offset: int               # statement offset in src
    kind: str                 # jump / goto / if / select / platform / cond / end
    dst: Optional[int]        # script index (None for unresolved / end)
    label: Optional[str]      # target label (None = start of script)
    condition: str = ""


LABEL_JUMPS = {"GOTO": 0, "IF__": 3, "IFNX": 1, "IFP4": 1, "IFSC": 1, "IFUR": 2}


def script_edges(i: int, cmds: Sequence[Command]) -> list[Edge]:
    out: list[Edge] = []
    for c in cmds:
        n = c.name
        if not n:
            continue
        if n.startswith("J"):
            sid = c.int_arg(0)
            lab = c.args[1].value if len(c.args) > 1 and c.args[1].value else None
            out.append(Edge(i, c.offset, "jump", scene_index(sid), lab, f"scene {sid}"))
        elif n in LABEL_JUMPS and len(c.args) > LABEL_JUMPS[n]:
            lab = c.args[LABEL_JUMPS[n]].value
            kind = {"GOTO": "goto", "IF__": "if", "IFNX": "platform", "IFP4": "platform"}.get(n, "cond")
            cond = ",".join(a.raw for a in c.args[:LABEL_JUMPS[n]])
            out.append(Edge(i, c.offset, kind, i, lab, f"{n}({cond})"))
        elif n == "GOTS":
            for k, a in enumerate(c.args[1:]):
                out.append(Edge(i, c.offset, "select", i, a.value, f"{c.args[0].value}=={k}"))
        elif n in ("END_", "RTM_", "RTMN"):
            out.append(Edge(i, c.offset, "end", None, None, n))
    return out


def scene_graph(scr: AllScr) -> dict[int, list[Edge]]:
    """Inter-script edges (JUMP) and intra-script label edges for every script."""
    return {i: script_edges(i, scr.commands(i)) for i in range(len(scr))}


# ---------------------------------------------------------------------------
# readable dump
# ---------------------------------------------------------------------------

def render_script(scr: AllScr, i: int, text: Optional[TextSource] = None, lang: int = 1,
                  res: Optional[AssetResolver] = None, skip_names: Iterable[str] = ()) -> str:
    """Human-readable listing: one statement per line with its byte offset,
    labels, resolved text lines and asset references."""
    skip = set(skip_names)
    labels = {lab.offset: lab for lab in scr.labels(i)}
    lines = [f"# script {i} {scr.names[i]}  scene id {SCENE_IDS[i]}  ({len(scr.raw(i))} bytes)"]
    run_name, run_count = "", 0

    def flush() -> None:
        nonlocal run_name, run_count
        if run_count:
            lines.append(f"          ... {run_count} x {run_name} skipped")
        run_name, run_count = "", 0

    tracker = LayerTracker()
    for c in scr.commands(i):
        assets = command_assets(c, res, tracker) if res is not None else []
        tracker.update(c)
        if c.name in skip:
            if c.name != run_name:
                flush()
                run_name = c.name
            run_count += 1
            continue
        flush()
        if c.offset in labels or c.name_offset in labels:
            lab = labels.get(c.offset) or labels[c.name_offset]
            lines.append(f"{lab.name}:")
        extra: list[str] = []
        if not c.handled:
            extra.append("[no-op on PS4]")
        if text is not None and c.text_ids:
            for a in c.args:
                if text_refs(a.raw):
                    shown = text.expand(a.raw, lang).replace("\r", "\\r").replace("\n", "\\n")
                    extra.append('"' + shown + '"')
        if res is not None:
            for r in assets:
                where = f"{r.archive}#{r.index}" if r.index is not None else "UNRESOLVED"
                if len(r.layers) > 1:
                    where = " + ".join(f"{a}#{i}" for a, i in r.layers)
                extra.append(f"-> {where}" + (f" ({r.note})" if r.note else ""))
        lines.append(f"  {c.offset:06x}  {c.raw}" + ("    ; " + "  ".join(extra) if extra else ""))
    flush()
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m tsuki.script", description=__doc__.split("\n")[0])
    ap.add_argument("--allscr", help="path to allscr.mrg (default: game folder)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("list", help="list scripts with scene ids and sizes")
    d = sub.add_parser("dump", help="readable dump of one script")
    d.add_argument("script", help="index, name (00_00) or #sceneid")
    d.add_argument("--lang", default="en", choices=LANGS)
    d.add_argument("--raw", action="store_true", help="print the decoded bytes only")
    d.add_argument("--no-assets", action="store_true")
    d.add_argument("--hide", default="WKST", help="comma-separated command names to fold (default WKST)")
    s = sub.add_parser("stats", help="command frequency over all scripts")
    s.add_argument("--json", action="store_true")
    g = sub.add_parser("graph", help="inter-script JUMP edges")
    g.add_argument("--dot", action="store_true")
    a = ap.parse_args(argv)
    # dumps contain Japanese / Chinese text and accented Latin: always write UTF-8
    # (a Windows console or redirect would otherwise use the ANSI code page, e.g. cp932)
    reconf = getattr(sys.stdout, "reconfigure", None)
    if reconf is not None:
        reconf(encoding="utf-8")
    scr = AllScr(a.allscr)
    try:
        if a.cmd == "list":
            for i, n in enumerate(scr.names):
                print(f"{i:3d}  #{SCENE_IDS[i]:<5d} {n:32s} {len(scr.raw(i)):8d}")
        elif a.cmd == "dump":
            i = scr.index(a.script)
            if a.raw:
                sys.stdout.buffer.write(scr.raw(i) + b"\n")
                return 0
            txt = open_text()
            res = None if a.no_assets else AssetResolver(scr.path.parent)
            hide = [h for h in a.hide.split(",") if h]
            sys.stdout.write(render_script(scr, i, txt, LANGS.index(a.lang), res, hide))
        elif a.cmd == "stats":
            import collections
            import json
            cnt: collections.Counter[str] = collections.Counter()
            for i in range(len(scr)):
                cnt.update(c.name for c in scr.commands(i) if c.name)
            if a.json:
                print(json.dumps(dict(cnt.most_common()), indent=1))
            else:
                for k, v in cnt.most_common():
                    h = engine_handler(k if len(k) == 4 else k + "00")
                    print(f"{v:8d}  {k:5s} {'0x%x %s' % h if h else 'no-op'}")
        elif a.cmd == "graph":
            edges = [e for i in range(len(scr)) for e in script_edges(i, scr.commands(i)) if e.kind == "jump"]
            if a.dot:
                print("digraph scenes {")
                for e in edges:
                    if e.dst is not None:
                        print(f'  "{scr.names[e.src]}" -> "{scr.names[e.dst]}"' + (f' [label="{e.label}"]' if e.label else "") + ";")
                print("}")
            else:
                for e in edges:
                    dst = scr.names[e.dst] if e.dst is not None else "?"
                    print(f"{scr.names[e.src]:28s} @{e.offset:06x} -> {dst} {e.label or ''} ({e.condition})")
    finally:
        scr.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
