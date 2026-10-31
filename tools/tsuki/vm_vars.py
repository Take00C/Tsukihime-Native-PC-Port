"""Script-VM variables of the PS4 engine: operand forms and the exact read/write
semantics of every command that names a variable.

Reference: docs/formats/vm_variables.md (byte layouts, emulation evidence).  Every rule
below was derived from the eboot code (CUSA47999 v01.01, text VAs cited) and checked
against the engine's own code under unicorn (``tsuki.misc_vm_variables_errata.VarEmu``;
tests/test_vm_vars.py replays fuzzed statements through both and compares every
variable read and write, the label jumped to and the argument buffer).

Storage (all u16, little endian, in bss):

    var_F   VA 0x164B1D0   1200 words   F<n>, C<n> (= F[F823 + n]), _D<n> (= F[n])
    var_Z   VA 0x164BB30   1400 words   no script operand reaches it (choice code only)
    var_G   VA 0x164C620   2000 words   G<n>, _D600.._D700 in value position (= G[n-600])

The engine never range-checks an index: the address is ``base + 2*n`` with n a signed
16-bit value (C: the 64-bit sum of two signed 16-bit values), so ``F1200`` is Z[0] and
``F-1`` is the word before var_F.  :class:`VarMemory` is therefore a flat word memory
keyed by engine address, not three bounded arrays.

Operand syntax (first byte after an optional 5-byte backtick tag decides; the index is
the inline-reader value of the whole argument, see tsuki.script.inline_int):

    position                          C        F      G      _              other
    WKST / WKAD target                F[F823+n] F[n]  G[n]   F[n] / G[n-600]  ignored
    WKOR / WKOF target                ignored  F[n]   G[n]   F[n] / G[n-600]  ignored
    WKBO target                       ignored  F[n]   G[n]   ignored          ignored
    value (WKST, WKAD, WKOR, WKOF,    literal  F[n]   G[n]   F[n] / G[n-600]  literal n
           IF__ both sides)
    GOTS selector                     only ``_?ddd``: F[ddd] or G[ddd-600] (3 digit bytes)
    SELD / SELS target                only ``_Dddd`` stores the result in F[ddd]
    SELA                              reads / clears F1118; its dialog stores no result

(``_`` operands: n in 600..700 selects G[n-600], every other n selects F[n].)

Choice commands (vm_variables.md 4.5): ``_SELD`` (mode 1, list) and ``_SELS`` (mode 2,
two buttons) record a target and open the choice UI unless scan mode is set; ``_SELA``
(mode 3, "save your progress?") opens only when F1118 == 0 and always sets F1118 = 0.
All three yield.  The UI stores values[cursor] (mode 1) or the cursor itself (mode 2).

CLI:  python -m tsuki.vm_vars "_WKST(C847,eq,0);" ... [--f823 5]   (evaluates on zeroed memory)
"""
from __future__ import annotations

import argparse
import functools
import re
import sys
from dataclasses import dataclass, field
from typing import Callable, Dict, Iterator, List, Optional, Tuple

from tsuki.script import Buffer, inline_int, inline_present

__all__ = [
    "VAR_F", "VAR_Z", "VAR_G", "VAR_AFTER_G", "F_COUNT", "Z_COUNT", "G_COUNT", "MSG_INDEX_VAR",
    "ARGBUF_VA", "CHOICE_KIND_VA", "CHOICE_INDEX_VA", "CHOICE_ID_VA", "CHOICE_VALUES_VA",
    "VarMemory", "VarState", "Effect", "Operand", "parse_operand", "operand_address",
    "value_address", "target_address", "execute", "execute_text", "choice_store", "Role",
    "address_name", "HANDLED_MAIN", "HANDLED_PREFETCH", "arg_texts", "copy_arg", "arg_start",
    "HISTORY_U16", "HISTORY_BOOL", "HISTORY_S8", "history_storage", "history_roundtrip",
    "history_after_rewind", "segments", "snapshot_gate", "CHOICE_MODE_VA", "SAVE_PROMPT_VAR",
    "SNAPSHOT_GATE_VAR", "SNAPSHOT_MODE_VAR", "CHOICE_MODE_LIST", "CHOICE_MODE_TWO", "CHOICE_MODE_SAVE",
]

VAR_F = 0x164B1D0
VAR_Z = 0x164BB30
VAR_G = 0x164C620
VAR_AFTER_G = 0x164D5C0
F_COUNT, Z_COUNT, G_COUNT = 1200, 1400, 2000
MSG_INDEX_VAR = 823                 # F823: message counter of the page; C<n> = F[F823 + n]
ARGBUF_VA = 0x16543C0               # scratch copy of the argument being decoded
CHOICE_KIND_VA = 0x1750648          # u8 'F' / 'G': array of the pending choice target
CHOICE_INDEX_VA = 0x175064A         # u16: its index
CHOICE_ID_VA = 0x1750698            # i16: SELD argument 1 (choice id), -1 when absent
CHOICE_VALUES_VA = 0x1750630        # u16[]: per-choice result values (SELR argument 0)
CHOICE_MODE_VA = 0x17506B0          # u32: 1 SELD list, 2 SELS two buttons, 3 SELA save prompt
CHOICE_MODE_LIST, CHOICE_MODE_TWO, CHOICE_MODE_SAVE = 1, 2, 3
SKIP_COUNTER_VA = 0x165420E         # if-block skip counter (IFF_/ELS_/EIF_), always 0 in retail
SCAN_MODE_VA = 0x174DE48            # save-restore scan mode byte
SNAPSHOT_BLOCK_VA = 0x16541F4       # u8 'message continues' byte; non-zero blocks History_Snapshot
SAVE_PROMPT_VAR = 1118              # F1118: 0 = _SELA opens its save prompt (cleared by _SELA)
SNAPSHOT_GATE_VAR = 929             # F929 != 0 blocks History_Snapshot
SNAPSHOT_MODE_VAR = 1102            # F1102 != 0 blocks History_Snapshot except kinds 0 and 11

ARGBUF_SIZE = 0x40
_TERM = (0x29, 0x2C, 0x3B)          # ')' ',' ';' (the copy loops' bit mask 0x800120000000000)

HANDLED_MAIN = ("WKST", "WKAD", "WKOR", "WKOF", "WKBO", "IF__", "GOTS", "SELD", "SELA", "SELS", "SELR")
HANDLED_PREFETCH = ("WKST", "WKAD", "WKOR", "WKOF", "IF__")

# F indices kept by a history record (= a save slot's state record), eboot rodata lists.
# Snapshot VA 0x7AF90, rewind restore VA 0x7C6D0, slot load VA 0xA3050 (which first
# zeroes var_F and var_Z).  Restore order is U16, then S8, then BOOL, so an index in two
# lists ends up with the later list's storage (F967: s8, F991: bool).
HISTORY_U16 = (*range(0, 9), 22, 991, 985, 954, 967, 966, 965, 964, *range(931, 940),
               *range(859, 870), 110, 1122,)                     # VA 0x2160E0, 39 x u16 (+0x34AA)
HISTORY_BOOL = (998, 997, 996, 994, 993, 991, 992, 988, 987, 986, 984, 822, 1103, 1104, 929,
                983, 975, 460, 500, *range(54, 75), 1118, 1123, 28,)  # VA 0x216580, 43 bits (+0x34FA)
HISTORY_S8 = (298, 1100, 989, 982, 967, 963, 962, 1101, 981, *range(799, 822), *range(823, 859),
              *range(870, 881), 23, *range(50, 54), *range(75, 82), *range(24, 28), 31, 32, 33,
              *range(1105, 1109), *range(1110, 1114), 1109, 501, 100, 101, 1098, *range(201, 273),
              199, 200, 995, *range(600, 688), 1120, 1121,)        # VA 0x2165F0, 276 x i8 (+0x3501)


def history_storage(index: int) -> Optional[str]:
    """How a history record / save slot keeps F[index]: 'u16' (exact), 's8' (low byte,
    restored sign-extended), 'bool' (restored as 0/1 = value != 0) or None (not kept:
    0 after loading a slot, unchanged by a rewind)."""
    if index in HISTORY_BOOL:
        return "bool"
    if index in HISTORY_S8:
        return "s8"
    if index in HISTORY_U16:
        return "u16"
    return None


def history_roundtrip(index: int, value: int) -> int:
    """F[index] after saving ``value`` and loading the slot again (u16 result)."""
    kind = history_storage(index)
    if kind == "u16":
        return value & 0xFFFF
    if kind == "s8":
        b = value & 0xFF
        return (b - 0x100 if b & 0x80 else b) & 0xFFFF
    if kind == "bool":
        return 1 if value & 0xFFFF else 0
    return 0


def history_after_rewind(index: int, saved: int, current: int) -> int:
    """F[index] after a rewind (History_Restore VA 0x7C6D0, loops 0x7CB99..0x7CC7F) to a
    record taken when F[index] was ``saved``; ``current`` is its value before the rewind.
    Unlike a slot load the rewind does not clear var_F, so an index the record does not
    keep stays at ``current``."""
    if history_storage(index) is None:
        return current & 0xFFFF
    return history_roundtrip(index, saved)


def _s16(v: int) -> int:
    v &= 0xFFFF
    return v - 0x10000 if v & 0x8000 else v


def address_name(va: int) -> str:
    """'F[12]' / 'Z[0]' / 'G[3]' / hex for an engine address."""
    for base, n, name in ((VAR_F, F_COUNT, "F"), (VAR_Z, Z_COUNT, "Z"), (VAR_G, G_COUNT, "G")):
        if base <= va < base + 2 * n and (va - base) % 2 == 0:
            return f"{name}[{(va - base) // 2}]"
    return f"{va:#x}"


# ---------------------------------------------------------------------------
# memory
# ---------------------------------------------------------------------------

class VarMemory:
    """Flat 16-bit word memory keyed by engine VA (only even addresses occur).
    ``init(va)`` supplies the value of a word never written (default 0).  Every
    access is appended to ``log`` as ('r'|'w', va, value)."""

    def __init__(self, init: Optional[Callable[[int], int]] = None) -> None:
        self.words: Dict[int, int] = {}
        self.init: Callable[[int], int] = init or (lambda va: 0)
        self.log: List[Tuple[str, int, int]] = []

    def peek(self, va: int) -> int:
        v = self.words.get(va)
        return self.init(va) & 0xFFFF if v is None else v

    def read(self, va: int) -> int:
        v = self.peek(va)
        self.log.append(("r", va, v))
        return v

    def write(self, va: int, value: int) -> None:
        value &= 0xFFFF
        self.words[va] = value
        self.log.append(("w", va, value))

    def poke(self, va: int, value: int) -> None:
        """Set a word without logging (test set-up)."""
        self.words[va] = value & 0xFFFF

    def reads(self) -> List[int]:
        return [a for k, a, _ in self.log if k == "r"]

    def writes(self) -> List[Tuple[int, int]]:
        return [(a, v) for k, a, v in self.log if k == "w"]


@dataclass
class VarState:
    """Engine state touched by the variable commands."""
    mem: VarMemory = field(default_factory=VarMemory)
    argbuf: bytearray = field(default_factory=lambda: bytearray(ARGBUF_SIZE))   # VA 0x16543C0
    skip_counter: int = 0              # VA 0x165420E
    scan_mode: bool = False            # VA 0x174DE48
    snapshot_block: int = 0            # VA 0x16541F4 (u8)
    choice_kind: int = 0               # VA 0x1750648 (0x46 'F' / 0x47 'G')
    choice_index: int = 0              # VA 0x175064A (u16)
    choice_id: int = -1                # VA 0x1750698 (i16)
    choice_count: int = 0              # VA 0x1750646: SELR lines registered for the next SELD
    choice_values: List[int] = field(default_factory=list)   # VA 0x1750630: SELR argument 0
    choice_locked: List[bool] = field(default_factory=list)  # VA 0x175064C: SELR argument 2 == 1
    choice_mode: int = 0               # VA 0x17506B0 (u32): CHOICE_MODE_LIST / _TWO / _SAVE


@dataclass
class Effect:
    handled: bool                       # the interpreter has a case for the name
    goto: Optional[bytes] = None        # label text handed to the label lookup VA 0x794D0
    yielded: bool = False               # ScriptStep returns to its caller (VA 0x9B516)
    snapshot: bool = False              # History_Snapshot passed its gate (body VA 0x7AFDD)
    choice_ui: bool = False             # the choice UI was set up (VA 0xA5DF0)
    choice_point: Optional[int] = None  # SELD: s16 argument 1 passed to VA 0xB8620 (flowchart marks)


# ---------------------------------------------------------------------------
# argument access (the inlined copies of the inline reader's loops)
# ---------------------------------------------------------------------------

def _byte(buf: bytes, i: int) -> int:
    return buf[i] if 0 <= i < len(buf) else 0


def arg_start(buf: bytes, p0: int, index: int) -> Optional[int]:
    """Start of argument ``index`` (``index`` commas skipped, at most 500 bytes each);
    None when ')' or ';' comes first.  Same loop as the inline reader VA 0x76690."""
    p = p0
    for _ in range(max(0, index)):
        for k in range(500):
            c = _byte(buf, p + k)
            if c in (0x29, 0x3B):
                return None
            if c == 0x2C:
                p += k + 1
                break
        else:
            p += 500
    return p


_END_RE = re.compile(rb"[);]")


def arg_texts(buf: Buffer, p0: int, limit: int = 32) -> List[bytes]:
    """The argument texts of a statement as the inline reader delimits them (split at
    ',' up to the first ')' or ';'; tags kept).  ``p0`` = first argument byte."""
    b = bytes(buf)
    m = _END_RE.search(b, p0)
    return b[p0:m.start() if m else len(b)].split(b",")[:limit]


def segments(script: bytes, window: int = 0x300) -> Iterator[bytes]:
    """The VM's statement segmentation (ScriptStep VA 0x95EC0, script.md 3.1): from the
    current position up to and including the next ';' inside ``window`` bytes, else
    ``window`` bytes.  Same result as the engine's find loop, computed from split(';')."""
    pieces = script.split(b";")
    last = len(pieces) - 1
    for i, piece in enumerate(pieces):
        off, n = 0, len(piece)
        while n - off >= window:                 # no ';' inside the window
            yield piece[off:off + window]
            off += window
        if i < last:
            yield piece[off:] + b";"
        elif off < n:
            yield piece[off:]


def copy_arg(buf: bytes, p0: int, index: int, state: VarState) -> int:
    """The handlers' argument copy into the buffer VA 0x16543C0 (e.g. VA 0x96566..0x965A7,
    0x97AA5..0x97AE1, 0x98786..0x987C0): a leading '`' skips 5 bytes, then bytes are
    copied up to the first ')' ',' ';' (NUL is copied like any byte) and a NUL is
    appended.  A missing argument only stores NUL at buffer[0].  Returns buffer[0]."""
    ab = state.argbuf
    p = arg_start(buf, p0, index)
    if p is None:
        ab[0] = 0
        return 0
    if _byte(buf, p) == 0x60:
        p += 5
    k = 0
    while True:
        c = _byte(buf, p + k) if p + k < len(buf) else 0x3B
        if c in _TERM:
            break
        if k < len(ab):
            ab[k] = c
        k += 1
    if k < len(ab):
        ab[k] = 0
    return ab[0]


# ---------------------------------------------------------------------------
# operand -> address
# ---------------------------------------------------------------------------

def _underscore(n: int) -> int:
    """'_' operands (VA 0x9880F: lea eax,[n-0x2BD]; cmp ax,-0x66; ja): n in 600..700
    (mod 2^16) selects G[n-600], everything else F[n] (n signed 16-bit)."""
    if ((n - 701) & 0xFFFF) > 0xFF9A:
        return VAR_G + 2 * ((n & 0xFFFF) - 600)
    return VAR_F + 2 * _s16(n)


def value_address(first: int, n: int) -> Optional[int]:
    """Address read for a value operand (WKST/WKAD/WKOR/WKOF value, IF__ both sides),
    or None for a literal.  Dispatch VA 0x987E3 / 0x98EEA / 0x98FCE / 0x99319 / 0x9A248."""
    if first == 0x46:                   # 'F'
        return VAR_F + 2 * _s16(n)
    if first == 0x47:                   # 'G'
        return VAR_G + 2 * _s16(n)
    if first == 0x5F:                   # '_'
        return _underscore(n)
    return None


def operand_value(first: int, n: int, mem: VarMemory) -> int:
    a = value_address(first, n)
    return mem.read(a) if a is not None else n & 0xFFFF


class Role:
    """Operand positions with different first-character dispatches."""
    WK_TARGET = "wk_target"          # WKST, WKAD: C F G _
    OR_TARGET = "or_target"          # WKOR, WKOF: F G _
    BO_TARGET = "bo_target"          # WKBO: F G
    VALUE = "value"                  # F G _, else literal
    GOTS = "gots"                    # _ + 3 digit bytes
    CHOICE = "choice"                # _D + 3 digit bytes (SELD/SELA/SELS)


def target_address(first: int, n: int, role: str, mem: VarMemory) -> Optional[int]:
    """Address written by a WK* target, or None when the command does nothing.
    WKST/WKAD jump tables 0x20D0B0 / 0x20CFF0 (index first-'C', 0..0x1C): C F G _ ;
    WKOR/WKOF compare chains (VA 0x99BAA / 0x99B35): F G _ ; WKBO (VA 0x97E6A): G F."""
    if first == 0x43 and role == Role.WK_TARGET:            # 'C': F[F823 + n]
        base = _s16(mem.read(VAR_F + 2 * MSG_INDEX_VAR))
        return VAR_F + 2 * (base + _s16(n))
    if first == 0x46:
        return VAR_F + 2 * _s16(n)
    if first == 0x47:
        return VAR_G + 2 * _s16(n)
    if first == 0x5F and role in (Role.WK_TARGET, Role.OR_TARGET):
        return _underscore(n)
    return None


def _three_digits(b: bytes) -> int:
    """GOTS / SELD / SELA / SELS index: bytes 2..4 as decimal digits without any check
    (VA 0x96109..0x9612D, 0x9B5AE..0x9B5CF): b2*100 + b3*10 + b4 - 5328."""
    return _byte(b, 2) * 100 + _byte(b, 3) * 10 + _byte(b, 4) - 0x14D0


# ---------------------------------------------------------------------------
# static operand parsing (for dumps, census and docs)
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Operand:
    """A statically decoded operand.  ``kind``: 'F' 'G' (fixed variable), 'C' (F[F823 + index]),
    'lit' (the value ``index``), 'none' (the command ignores the operand / does nothing)."""
    kind: str
    index: int
    text: str

    def address(self, f823: int = 0) -> Optional[int]:
        if self.kind == "F":
            return VAR_F + 2 * self.index
        if self.kind == "G":
            return VAR_G + 2 * self.index
        if self.kind == "C":
            return VAR_F + 2 * (_s16(f823) + self.index)
        return None

    def __str__(self) -> str:
        if self.kind in ("F", "G"):
            return f"{self.kind}[{self.index}]"
        if self.kind == "C":
            return f"F[F823+{self.index}]"
        return f"{self.kind}({self.index})"


@functools.lru_cache(maxsize=1 << 16)
def parse_operand(text: str, role: str = Role.VALUE) -> Operand:
    """Decode one argument text the way the engine does in position ``role``.
    The text is the argument as written (an optional ```NNN:`` tag included).
    Pure function, memoised (operand texts repeat a lot in the corpus)."""
    raw = text.encode("latin-1", "replace")
    body = raw[5:] if raw[:1] == b"`" else raw
    first = body[0] if body else 0
    if role in (Role.GOTS, Role.CHOICE):
        if first != 0x5F:
            return Operand("none", 0, text)
        n = _three_digits(body + b"\0\0\0\0\0")
        if role == Role.CHOICE:
            return Operand("F", _s16(n), text) if _byte(body, 1) == 0x44 else Operand("none", n, text)
        return Operand(*_underscore_kind(n), text) if ((n - 701) & 0xFFFF) > 0xFF9A \
            else Operand("F", n, text)
    n = inline_int(raw + b")", 0, 0)
    allowed = {Role.VALUE: b"FG_", Role.WK_TARGET: b"CFG_", Role.OR_TARGET: b"FG_",
               Role.BO_TARGET: b"FG"}[role]
    if first == 0 or first not in allowed:
        return Operand("lit", n, text) if role == Role.VALUE else Operand("none", n, text)
    if first == 0x43:
        return Operand("C", _s16(n), text)
    if first == 0x5F:
        return Operand(*_underscore_kind(n), text)
    return Operand(chr(first), _s16(n), text)


def _underscore_kind(n: int) -> Tuple[str, int]:
    if ((n - 701) & 0xFFFF) > 0xFF9A:
        return "G", (n & 0xFFFF) - 600
    return "F", _s16(n)


def operand_address(text: str, role: str, f823: int = 0) -> Optional[int]:
    return parse_operand(text, role).address(f823)


# ---------------------------------------------------------------------------
# command execution
# ---------------------------------------------------------------------------

def _wk_value(buf: bytes, p0: int, index: int, state: VarState) -> int:
    first = copy_arg(buf, p0, index, state)
    n = inline_int(buf, p0, index)
    return operand_value(first, n, state.mem)


def _wkst(buf: bytes, p0: int, st: VarState, prefetch: bool) -> Effect:
    """ScriptStep 0x96566 / Script_Prefetch 0x9BFA2: target = value.  The operator slot
    is only tested for a leading 'e' (VA 0x98709): otherwise argument 1 itself is the
    value (``_WKST(F1,7)`` stores 7, ``_WKST(F1,ne,7)`` stores 0)."""
    first0 = copy_arg(buf, p0, 0, st)
    n0 = inline_int(buf, p0, 0)
    first1 = copy_arg(buf, p0, 1, st)
    if first1 == 0x65:                                    # 'e'
        value = _wk_value(buf, p0, 2, st)
    else:
        value = operand_value(first1, inline_int(buf, p0, 1), st.mem)
    a = target_address(first0, n0, Role.OR_TARGET if prefetch else Role.WK_TARGET, st.mem)
    if a is not None:
        st.mem.write(a, value)
    return Effect(True)


def _wk_rmw(buf: bytes, p0: int, st: VarState, role: str, op: Callable[[int, int], int]) -> Effect:
    first0 = copy_arg(buf, p0, 0, st)
    n0 = inline_int(buf, p0, 0)
    value = _wk_value(buf, p0, 1, st)
    a = target_address(first0, n0, role, st.mem)
    if a is not None:
        st.mem.write(a, op(st.mem.read(a), value))
    return Effect(True)


def _wkbo(buf: bytes, p0: int, st: VarState) -> Effect:
    """VA 0x97BB8: target |= 1 << (int(arg1) & 31), 16-bit store; the value is never a
    variable and argument 1 is not copied."""
    first0 = copy_arg(buf, p0, 0, st)
    n0 = inline_int(buf, p0, 0)
    bit = (1 << (inline_int(buf, p0, 1) & 31)) & 0xFFFF
    a = target_address(first0, n0, Role.BO_TARGET, st.mem)
    if a is not None:
        st.mem.write(a, st.mem.read(a) | bit)
    return Effect(True)


def _condition(op0: int, op1: int, lhs: int, rhs: int) -> bool:
    """IF__ operator (VA 0x9A474..0x9A64C): first byte e/n/g/l, second byte 'e' makes
    g/l inclusive; signed 16-bit compares; any other first byte is never true."""
    a, b = _s16(lhs), _s16(rhs)
    if op0 == 0x65:
        return a == b
    if op0 == 0x6E:
        return a != b
    if op0 == 0x67:
        return a >= b if op1 == 0x65 else a > b
    if op0 == 0x6C:
        return a <= b if op1 == 0x65 else a < b
    return False


def _if(buf: bytes, p0: int, st: VarState) -> Effect:
    """ScriptStep 0x9708D / Script_Prefetch 0x9BAD5: ``_IF__(lhs,op,rhs,label)`` continues
    with the next statement when ``lhs op rhs`` holds and jumps to ``label`` when it does
    NOT hold (a structured ``if``: the label marks the end of the block)."""
    if st.skip_counter:
        return Effect(True)
    lhs = _wk_value(buf, p0, 0, st)
    rhs = _wk_value(buf, p0, 2, st)
    copy_arg(buf, p0, 1, st)
    if _condition(st.argbuf[0], st.argbuf[1], lhs, rhs):
        return Effect(True)
    if not inline_present(buf, p0, 3):
        return Effect(True)
    copy_arg(buf, p0, 3, st)
    return Effect(True, goto=bytes(st.argbuf).split(b"\0")[0])


def _gots(buf: bytes, p0: int, st: VarState) -> Effect:
    """VA 0x9609A: computed goto.  Selector ``_?ddd`` (any second byte; bytes 2..4 read
    from the argument buffer even past a short selector's NUL), value v of F[ddd] (or
    G[ddd-600]); the label is argument s16(v + 1), argument 0 itself when that is <= 0,
    the empty text when the argument does not exist."""
    if st.scan_mode:
        return Effect(True, yielded=True)
    if st.skip_counter:
        return Effect(True)
    if copy_arg(buf, p0, 0, st) != 0x5F:
        return Effect(True)
    n = _three_digits(bytes(st.argbuf))
    if ((n - 701) & 0xFFFF) > 0xFF9A:
        v = st.mem.read(VAR_G + 2 * ((n & 0xFFFF) - 600))
    else:
        v = st.mem.read(VAR_F + 2 * n)
    k = _s16(v + 1)
    # VA 0x76EA0: copy of argument k (k <= 0: argument 0), no NUL-only shortcut
    p = arg_start(buf, p0, k) if k > 0 else p0
    ab = st.argbuf
    if p is None:
        ab[0] = 0
    else:
        if _byte(buf, p) == 0x60:
            p += 5
        j = 0
        while True:
            c = _byte(buf, p + j) if p + j < len(buf) else 0x3B
            if c in _TERM:
                break
            if j < len(ab):
                ab[j] = c
            j += 1
        if j < len(ab):
            ab[j] = 0
    return Effect(True, goto=bytes(ab).split(b"\0")[0])


def _choice_target(b: bytes, st: VarState) -> None:
    """VA 0x9B589..0x9B5D6 (SELD), 0xB835C..0xB839A (SELS), 0xB847C..0xB84BA (SELA):
    only a target starting with '_' is stored: kind 'F' when byte 1 is 'D', else 'G'
    (never written back, see choice_store); index = 3 digit bytes.  Any other target
    leaves the previous kind/index in place."""
    if _byte(b, 0) != 0x5F:
        return
    st.choice_kind = 0x46 if _byte(b, 1) == 0x44 else 0x47
    st.choice_index = _three_digits(b) & 0xFFFF


def _seld(buf: bytes, p0: int, st: VarState) -> Effect:
    """ScriptStep's SELD (VA 0x9B1DC).  In scan mode it yields at once (VA 0x9B1E3) and
    changes nothing.  Otherwise: choice mode 1 (dword 0x17506B0, VA 0x9B1FF), the per-line
    loop VA 0x9B228..0x9B25F over the pending SELR lines, the target decode VA
    0x9B53F..0x9B5D6, argument 1 as the choice id when present (VA 0x9B5F3) else -1, the
    line layout and the UI set-up VA 0xA5DF0, then the yield (VA 0x9B74A -> 0x9B516).
    The result itself is stored later by the choice UI (:func:`choice_store`).

    When argument 1 (the choice point N) is present the handler also calls VA 0xB8620(N)
    (VA 0x9B60D), which for 1 <= N <= 82 ORs the flowchart edge bits of choice point N into
    G[400..443] (plus extra marks for N = 57, 67, 72, 73; gallery.md 5.3).  That gallery
    state is reported as ``Effect.choice_point`` and not executed here.  Not modelled
    either: a running scene jump (word 0x120A6D2) is ended via VA 0x7E620."""
    if st.scan_mode:
        return Effect(True, yielded=True)
    st.choice_mode = CHOICE_MODE_LIST
    copy_arg(buf, p0, 0, st)
    _choice_target(bytes(st.argbuf), st)
    point: Optional[int] = None
    if inline_present(buf, p0, 1):
        st.choice_id = inline_int(buf, p0, 1)
        point = _s16(st.choice_id)
    else:
        st.choice_id = -1
    return Effect(True, yielded=True, choice_ui=True, choice_point=point)


def _two_answer_dialog(buf: bytes, p0: int, st: VarState, mode: int) -> bool:
    """The SELS helper VA 0xB82E0 (mode 2) / SELA helper VA 0xB8400 (mode 3).  Scan mode
    (VA 0x7B4B0) returns without any change.  Otherwise: mode dword, target decode via
    the object parser (str(0) into a 16-byte stack buffer: at most 15 bytes; bytes after
    the NUL of a target shorter than 5 bytes are stack garbage in the engine, NUL here),
    count 2, values {0, 1} (dword 0x10000), id -1, byte 0x1653E00 |= 0x10, UI set-up
    VA 0xA5DF0.  Returns True when the UI was set up."""
    if st.scan_mode:
        return False
    from tsuki.script import obj_split
    st.choice_mode = mode
    spans = obj_split(buf, p0 - 1)
    if spans and spans[0][0] < spans[0][1]:
        s, e = spans[0]
        b = bytes(buf[s:min(e, s + 15)])
    else:
        b = b""
    _choice_target(b + b"\0" * 5, st)
    st.choice_id = -1
    st.choice_count = 2                  # VA 0xB83B2: count 2, values 0 and 1 (dword 0x10000)
    st.choice_values = [0, 1]
    return True


def _sels(buf: bytes, p0: int, st: VarState) -> Effect:
    """ScriptStep's SELS (VA 0x9B435): the two-button dialog helper, then yield."""
    return Effect(True, yielded=True, choice_ui=_two_answer_dialog(buf, p0, st, CHOICE_MODE_TWO))


def _sela(buf: bytes, p0: int, st: VarState) -> Effect:
    """ScriptStep's SELA (VA 0x9B1B6): the "save your progress?" prompt.  It reads F1118
    (VA 0xA7FA0: F[1118] == 0); only when it is 0 does it call the helper VA 0xB8400
    (which also does nothing in scan mode).  Then it always writes F1118 = 0 (VA 0xA7F90
    with edi = 0) and yields.  The scripts set F1118 = 1 in every scene-start block, and
    every slot save sets the F1118 bit of the stored record (adv_state.md 7.2), so the
    prompt opens only when that block is skipped (vm_variables.md 4.5)."""
    a = VAR_F + 2 * SAVE_PROMPT_VAR
    opened = False
    if st.mem.read(a) == 0:
        opened = _two_answer_dialog(buf, p0, st, CHOICE_MODE_SAVE)
    st.mem.write(a, 0)
    return Effect(True, yielded=True, choice_ui=opened)


def snapshot_gate(st: VarState, kind: int) -> bool:
    """History_Snapshot's entry test (VA 0x7AF9B..0x7AFD7), in the engine's order: scan
    mode -> no; F929 != 0 -> no; F1102 != 0 and kind not in (0, 11) -> no (F1102 is read
    only when F929 == 0); byte 0x16541F4 != 0 -> no.  True: the snapshot body runs
    (VA 0x7AFDD; it only reads variables)."""
    if st.scan_mode:
        return False
    if st.mem.read(VAR_F + 2 * SNAPSHOT_GATE_VAR) != 0:
        return False
    if st.mem.read(VAR_F + 2 * SNAPSHOT_MODE_VAR) != 0 and kind not in (0, 0xB):
        return False
    return not st.snapshot_block


def _selr(buf: bytes, p0: int, st: VarState) -> Effect:
    """SELR's variable side effects (VA 0x9936D..0x99525).  For the FIRST line of a
    choice group (count 0x1750646 == 0) the handler calls History_Snapshot(.., 0xD)
    (VA 0x993A0; its gate is :func:`snapshot_gate`, the record itself is adv_state.md
    work) and then, when F823 > 0, increments the per-message slot C847 = F[F823 + 847]
    (also in scan mode).  Every line stores its argument 0 as the choice value (u16 table
    0x1750630, VA 0x99500), its 'locked' flag (byte table 0x175064C = argument 2 == 1,
    VA 0x9951C) and counts itself (VA 0x99525).  SELR does not yield.  The line text
    ("$" + 5-digit text id, table 0x174FB70, stride 0xFA) is not modelled."""
    value = inline_int(buf, p0, 0)
    snap = False
    if st.choice_count == 0:
        snap = snapshot_gate(st, 0xD)
        k = _s16(st.mem.read(VAR_F + 2 * MSG_INDEX_VAR))
        if k > 0:
            a = VAR_F + 2 * (k + 847)
            st.mem.write(a, st.mem.read(a) + 1)
    del st.choice_values[st.choice_count:]
    del st.choice_locked[st.choice_count:]
    st.choice_values.append(value & 0xFFFF)
    st.choice_locked.append(inline_int(buf, p0, 2) == 1)
    st.choice_count += 1
    return Effect(True, snapshot=snap)


def choice_store(st: VarState, cursor: int) -> Optional[int]:
    """The choice UI's result store when the player confirms line ``cursor`` (u16
    0x17506A2), by choice mode (dispatch VA 0xA6180):

    * mode 1 (SELD) and any mode other than 2 / 3, list input VA 0xA6180, store VA
      0xA65E3..0xA662E: v = values[s16 cursor] (a cursor outside the registered lines reads
      stale table memory in the engine; the model uses 0 there);
    * mode 2 (SELS), two-button input VA 0xA7AA0 (via 0xA6940), store VA 0xA7B99..0xA7BDE:
      only when the u16 cursor is 0 or 1, and v = the cursor itself;
    * mode 3 (SELA), input VA 0xA7FD0: no store at all (cursor 0 closes the dialog,
      cursor 1 continues with phases 4..7, vm_variables.md 4.5).

    The value goes to var_F[s16 index] for kind 'F', var_Z[s16 index] for kind 'Z' (no
    setter produces 'Z'); any other kind ('G') writes nothing.  Returns the address
    written or None."""
    if st.choice_mode == CHOICE_MODE_SAVE:
        return None
    if st.choice_mode == CHOICE_MODE_TWO:
        if cursor & 0xFFFF > 1:
            return None
        value = cursor & 0xFFFF
    else:
        i = _s16(cursor)
        value = st.choice_values[i] if 0 <= i < len(st.choice_values) else 0
    if st.choice_kind == 0x46:
        a = VAR_F + 2 * _s16(st.choice_index)
    elif st.choice_kind == 0x5A:
        a = VAR_Z + 2 * _s16(st.choice_index)
    else:
        return None
    st.mem.write(a, value)
    return a


def execute(buf: Buffer, name_pos: int, state: VarState, prefetch: bool = False) -> Effect:
    """Run the variable semantics of the statement whose 4-byte name starts at
    ``buf[name_pos]`` (the byte after '_').  ``prefetch`` selects Script_Prefetch's
    handlers (VA 0x9B760: no C targets, no WKBO/GOTS/SEL*).  Commands without variable
    semantics return ``Effect(False)``."""
    b = bytes(buf)
    name = b[name_pos:name_pos + 4].decode("latin-1")
    p0 = name_pos + 5
    if prefetch and name not in HANDLED_PREFETCH:
        return Effect(False)
    if name == "WKST":
        return _wkst(b, p0, state, prefetch)
    if name == "WKAD":
        return _wk_rmw(b, p0, state, Role.OR_TARGET if prefetch else Role.WK_TARGET,
                       lambda a, v: a + v)
    if name == "WKOR":
        return _wk_rmw(b, p0, state, Role.OR_TARGET, lambda a, v: a | v)
    if name == "WKOF":
        return _wk_rmw(b, p0, state, Role.OR_TARGET, lambda a, v: a & ~v)
    if name == "WKBO":
        return _wkbo(b, p0, state)
    if name == "IF__":
        return _if(b, p0, state)
    if name == "GOTS":
        return _gots(b, p0, state)
    if name == "SELD":
        return _seld(b, p0, state)
    if name == "SELA":
        return _sela(b, p0, state)
    if name == "SELS":
        return _sels(b, p0, state)
    if name == "SELR":
        return _selr(b, p0, state)
    return Effect(False)


def execute_text(stmt: str, state: Optional[VarState] = None, prefetch: bool = False) -> Tuple[Effect, VarState]:
    """Convenience: run one statement text such as ``"_WKST(C847,eq,0);"``."""
    st = state or VarState()
    b = stmt.encode("latin-1")
    return execute(b, b.index(b"_") + 1, st, prefetch), st


def _main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m tsuki.vm_vars", description=__doc__.split("\n")[0])
    ap.add_argument("stmt", nargs="+")
    ap.add_argument("--f823", type=int, default=0, help="value of F823 (C operands)")
    ap.add_argument("--prefetch", action="store_true", help="Script_Prefetch semantics")
    ap.add_argument("--scan", action="store_true", help="scan mode (byte 0x174DE48) set")
    ap.add_argument("--set", action="append", default=[], metavar="Fn=v",
                    help="initial value of a variable, e.g. --set F1118=1 (repeatable)")
    a = ap.parse_args(argv)
    st = VarState(scan_mode=a.scan)
    st.mem.poke(VAR_F + 2 * MSG_INDEX_VAR, a.f823)
    for kv in a.set:
        k, v = kv.split("=")
        st.mem.poke((VAR_F if k[0] == "F" else VAR_G) + 2 * int(k[1:]), int(v, 0))
    for s in a.stmt:
        st.mem.log.clear()
        eff, _ = execute_text(s, st, a.prefetch)
        rd = [address_name(x) for x in st.mem.reads()]
        wr = [(address_name(x), v) for x, v in st.mem.writes()]
        print(f"{s}  handled={eff.handled} reads={rd} writes={wr} goto={eff.goto!r} "
              f"yield={eff.yielded} choice_ui={eff.choice_ui} snapshot={eff.snapshot}")
    return 0


if __name__ == "__main__":
    sys.exit(_main())
