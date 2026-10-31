"""Recover the script-VM command tables from the two interpreters in eboot.bin.

The allscr scripts are plain text, e.g. ``_WKST(F823,eq,0);_JUMP(240,START);``.
There is no table of command-name strings: each interpreter scans for '_' and ';'
and then dispatches on the 4 name characters c0 c1 c2 c3:

    p   = first '_' before ';' inside a 0x300-byte window      (p points after '_')
    c0  = p[0]; if not 'A' <= c0 <= 'Z': skip
    key = c1 << 16 | c2 << 8 | c3                              (24 bits, register r13)
    goto first_letter_table[c0 - 'A']        ; 26 x int32 relative offsets
    each case is compiled C++ `if`/`switch` code on `key`: compare trees
    (cmp r13d, imm; je/jne/jg/jle/...), masked compares (mov eax, r13d;
    and eax, ~2; cmp eax, imm -> two names), ranges (add r13, -K; cmp r13, N; jbe)
    and dense switches (jump tables).

Interpreters (both found automatically by the `add reg,-0x41; cmp reg,0x19` idiom):

    0x95E60  ScriptStep      letter table 0x20CAC0, skip label 0x95EB0
    0x9B760  Script_Replay   letter table 0x20D170, skip label 0x9FF30
             (a look-ahead scanner, see docs/formats/engine.md 5.4)

Method (`extract`): **concrete vectorised evaluation**.  For every first letter the
dispatch code is executed on the whole set of candidate names at once (numpy
arrays, one element per name c1c2c3 over the alphabet [A-Za-z0-9_], i.e. 63^3 =
250,047 names per letter).  Only the instructions the dispatcher uses are
modelled (mov/movzx/lea/shl/or/and/add/sub/cmp/test, setcc, jcc, jmp, jump tables);
the first instruction that does anything else ends the dispatch, and the address
reached last by a control transfer is the handler entry.  Each conditional jump
splits the set.  Names that reach the skip label (directly or through a
register-only trampoline) are not commands.  The handler -> name sets are then
compressed into patterns (`WKST`, `J***`, `ZY**`).

An earlier version walked the compare trees syntactically and lost the chain
behind the masked compare at 0x9F158 (7 replay commands); the evaluator has no
such blind spots: every name of the alphabet is decided.  A later version did not
model setcc and stopped at Script_Prefetch's `V` case (0x9BD0F: cmp/setne/cmp/setne/
test/jne), reporting a `V***` wildcard; the case accepts only VPLY and VPL2 (43
entries, confirmed by per-name unicorn emulation of all 2,588 V names tried).

CLI:  python -m eboot.scriptvm [--json out.json]
"""
from __future__ import annotations

import json
import re
import struct
import sys
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Set, Tuple

import numpy as np

from eboot.disasm import Index, Insn
from eboot.selfelf import Eboot

INTERP_VA = 0x95E60          # ScriptStep
LETTER_TABLE_VA = 0x20CAC0   # 26 x int32 offsets relative to the table
REPLAY_VA = 0x9B760          # Script_Replay (look-ahead scanner)
REPLAY_TABLE_VA = 0x20D170

ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_abcdefghijklmnopqrstuvwxyz"
_ACODES = np.frombuffer(ALPHABET.encode(), dtype=np.uint8).astype(np.int64)
_NA = len(ALPHABET)


@dataclass
class Command:
    name: str           # "WKST"; a pattern with '*' = any character, e.g. "J***", "ZY**"
    handler: int        # VA where the handler code starts (after jmp trampolines)
    via: str            # last dispatch decision: eq / ne-fallthrough / range / switch /
                        # masked / wildcard
    nkeys: int = 1      # number of alphabet names covered by the pattern
    exits: str = ""     # "continue" / "yield" / "both" / "exit" / "?" (static reachability)
    calls: List[int] = field(default_factory=list)   # direct callees inside the handler

    def matches(self, name: str) -> bool:
        return len(name) == 4 and all(p == "*" or p == c for p, c in zip(self.name, name))


@dataclass
class VmTable:
    loop_va: int
    skip_va: int
    fail_targets: Set[int]
    letter_cases: Dict[str, int]
    commands: List[Command] = field(default_factory=list)
    yield_va: Optional[int] = None      # where the per-call budget check leaves the loop
    budget: Optional[int] = None        # loop limit of the counter at the skip label
    counter_reg: Optional[str] = None   # r11d (ScriptStep) / r15d (Script_Replay)
    stops: List[Tuple[int, str]] = field(default_factory=list)   # (handler, insn) diagnostics

    def by_name(self) -> Dict[str, Command]:
        return {c.name: c for c in self.commands}

    def lookup(self, name: str) -> Optional[Command]:
        """Command handling the 4-character script name (exact names before patterns)."""
        for c in self.commands:
            if c.name == name:
                return c
        for c in self.commands:
            if "*" in c.name and c.matches(name):
                return c
        return None


def find_interpreters(ix: Index) -> List[Tuple[int, int]]:
    """(function VA, letter-table VA) for every function containing the dispatch
    idiom `add reg, -0x41; cmp reg, 0x19; ...; lea rX, [rip + table]`."""
    out = []
    for f, lst in ix.insns.items():
        for k in range(len(lst) - 2):
            a, b = lst[k], lst[k + 1]
            if a.mnem == "add" and a.ops.endswith(", -0x41") and b.mnem == "cmp" \
                    and b.ops.endswith(", 0x19"):
                for j in range(k + 2, min(k + 12, len(lst))):
                    m = re.match(r"^(\w+), \[rip \+ (0x[0-9a-f]+)\]$", lst[j].ops)
                    if lst[j].mnem == "lea" and m:
                        out.append((f, lst[j].addr + lst[j].size + int(m.group(2), 16)))
                        break
                break
    return sorted(out)


def letter_cases(eb: Eboot, table_va: int = LETTER_TABLE_VA) -> Dict[str, int]:
    out = {}
    for i in range(26):
        off = struct.unpack("<i", eb.read_va(table_va + 4 * i, 4))[0]
        out[chr(0x41 + i)] = table_va + off
    return out


# ---------------------------------------------------------------------------
# x86 register model
# ---------------------------------------------------------------------------

def _build_regs() -> Dict[str, Tuple[str, int, int]]:
    """name -> (64-bit base register, width in bits, bit shift)"""
    r: Dict[str, Tuple[str, int, int]] = {}
    for x in "abcd":
        b = f"r{x}x"
        r.update({b: (b, 64, 0), f"e{x}x": (b, 32, 0), f"{x}x": (b, 16, 0),
                  f"{x}l": (b, 8, 0), f"{x}h": (b, 8, 8)})
    for x in ("si", "di", "bp", "sp"):
        b = "r" + x
        r.update({b: (b, 64, 0), "e" + x: (b, 32, 0), x: (b, 16, 0), x + "l": (b, 8, 0)})
    for n in range(8, 16):
        b = f"r{n}"
        r.update({b: (b, 64, 0), b + "d": (b, 32, 0), b + "w": (b, 16, 0), b + "b": (b, 8, 0)})
    return r


_REGS = _build_regs()
_M64 = (1 << 64) - 1
_IMM = re.compile(r"^-?(0x[0-9a-f]+|\d+)$")
_MEM = re.compile(r"^(?:(?:byte|word|dword|qword) ptr )?\[(\w+)(?: \+ (\w+)\*(\d))?(?: ([+-]) (0x[0-9a-f]+|\d+))?\]$")
_FLAGLESS = {"mov", "movabs", "movzx", "movsx", "movsxd", "lea", "nop", "vmovups", "vmovaps",
             "vmovdqu", "vmovdqa"}
_JCC = {"je", "jne", "jg", "jge", "jl", "jle", "ja", "jae", "jb", "jbe", "js", "jns"}
# setcc -> the jcc with the same condition (setcc writes 0/1 to an 8-bit register and
# leaves the flags alone); e.g. Script_Prefetch 0x9BD0F: cmp r13,'PLY'; setne al;
# cmp r13,'PL2'; setne cl; test al,cl; jne skip  (only VPLY and VPL2 reach the handler)
_SETCC = {"sete": "je", "setne": "jne", "setg": "jg", "setge": "jge", "setl": "jl",
          "setle": "jle", "seta": "ja", "setae": "jae", "setb": "jb", "setbe": "jbe",
          "sets": "js", "setns": "jns"}


def _mask(bits: int) -> int:
    return (1 << bits) - 1


class _Unsupported(Exception):
    """The instruction is not part of the dispatch (or not modelled): stop here."""


@dataclass
class _Path:
    pc: int
    sel: np.ndarray                    # indices into the letter's key array
    regs: Dict[str, np.ndarray]        # 64-bit base register -> uint64 values (as int64 view)
    flags: Optional[Tuple[np.ndarray, np.ndarray, Optional[np.ndarray]]] = None  # signed, unsigned cmp sign, SF
    landing: int = 0
    via: str = "wildcard"
    masked: bool = False
    nameptr: Optional[str] = None      # register holding the name pointer (p)
    steps: int = 0

    def take(self, m: np.ndarray, pc: int) -> "_Path":
        return _Path(pc, self.sel[m], {k: v[m] for k, v in self.regs.items()}, None, pc,
                     self.via, self.masked, self.nameptr, self.steps)


def _u(v: np.ndarray) -> np.ndarray:
    return v.astype(np.uint64)


class _Evaluator:
    def __init__(self, eb: Eboot, insns: List[Insn], skip: int, front: Tuple[int, int],
                 script_reg: str):
        self.eb = eb
        self.script_reg = script_reg       # base register holding the script pointer
        self.front = front                 # [start, letter jump]: the shared front end
        self.case_jmp = front[1]
        self.keys = np.zeros(0, dtype=np.uint64)
        self.insns = insns
        self.at = {ins.addr: k for k, ins in enumerate(insns)}
        self.skip = skip
        self.fail_targets: Set[int] = {skip}
        self.stops: Dict[int, str] = {}
        self._noop_cache: Dict[int, bool] = {}

    # -- helpers -------------------------------------------------------------------
    def ins_at(self, a: int) -> Insn:
        k = self.at.get(a)
        if k is None:
            raise _Unsupported(f"no instruction at 0x{a:x}")
        return self.insns[k]

    def next_addr(self, ins: Insn) -> int:
        return ins.addr + ins.size

    def is_skip(self, a: int) -> bool:
        """`a` is the skip label or a side-effect-free trampoline to it: only register
        loads that do not touch the script pointer, e.g. `mov r11d, r14d; jmp skip`
        (main) or `vmovups ymm9, [..]; mov ebx, [rsp+0xc]; movabs r10, ..; jmp skip`
        (replay register restore)."""
        if a in self._noop_cache:
            return self._noop_cache[a]
        r = False
        cur = a
        for _ in range(6):
            if cur == self.skip:
                r = True
                break
            k = self.at.get(cur)
            if k is None:
                break
            ins = self.insns[k]
            if ins.mnem == "jmp" and ins.ops.startswith("0x"):
                cur = int(ins.ops, 16)
                continue
            ops = [o.strip() for o in ins.ops.split(",")]
            if ins.mnem in _FLAGLESS and len(ops) == 2 and \
                    ((ops[0] in _REGS and _REGS[ops[0]][0] != self.script_reg)
                     or re.match(r"^[xy]mm\d+$", ops[0])):
                cur = self.next_addr(ins)
                continue
            break
        self._noop_cache[a] = r
        if r:
            self.fail_targets.add(a)
        return r

    def read(self, p: _Path, op: str) -> np.ndarray:
        if op in _REGS:
            base, bits, sh = _REGS[op]
            if base not in p.regs:
                raise _Unsupported(f"untracked {op}")
            if bits == 64:
                return p.regs[base]
            return (p.regs[base] >> np.uint64(sh)) & np.uint64(_mask(bits))
        if _IMM.match(op):
            return np.full(len(p.sel), int(op, 0) & _M64, dtype=np.uint64)
        raise _Unsupported(f"operand {op}")

    def tracked(self, p: _Path, op: str) -> bool:
        return op in _REGS and _REGS[op][0] in p.regs

    def write(self, p: _Path, op: str, v: np.ndarray) -> None:
        base, bits, sh = _REGS[op]
        if bits == 64:
            p.regs[base] = v.astype(np.uint64)
        elif bits == 32:
            p.regs[base] = v.astype(np.uint64) & np.uint64(0xFFFFFFFF)
        elif base in p.regs:            # 8/16-bit write merges into the old value
            m = np.uint64(_mask(bits) << sh)
            p.regs[base] = (p.regs[base] & ~m) | ((v.astype(np.uint64) << np.uint64(sh)) & m)
        else:
            raise _Unsupported(f"partial write to untracked {op}")

    def drop(self, p: _Path, op: str) -> None:
        if op in _REGS:
            p.regs.pop(_REGS[op][0], None)
            if p.nameptr == _REGS[op][0]:
                p.nameptr = None

    @staticmethod
    def width(op: str) -> int:
        return _REGS[op][1] if op in _REGS else 64

    def set_cmp(self, p: _Path, a: np.ndarray, b: np.ndarray, bits: int) -> None:
        m = np.uint64(_mask(bits))
        ua, ub = a & m, b & m
        sbit = np.uint64(1 << (bits - 1))
        sa, sb = ua ^ sbit, ub ^ sbit       # order-preserving map of signed onto unsigned
        s = (sa > sb).astype(np.int64) - (sa < sb).astype(np.int64)
        u = (ua > ub).astype(np.int64) - (ua < ub).astype(np.int64)
        p.flags = (s, u, None)

    def set_logic(self, p: _Path, r: np.ndarray, bits: int) -> None:
        m = np.uint64(_mask(bits))
        ur = r & m
        neg = (ur >> np.uint64(bits - 1)) & np.uint64(1)
        s = np.where(ur == 0, 0, np.where(neg == 1, -1, 1)).astype(np.int64)
        u = (ur != 0).astype(np.int64)
        p.flags = (s, u, neg.astype(bool))

    @staticmethod
    def cond(mn: str, f: Tuple[np.ndarray, np.ndarray, Optional[np.ndarray]]) -> np.ndarray:
        s, u, sf = f
        if mn == "je":
            return u == 0
        if mn == "jne":
            return u != 0
        if mn == "jg":
            return s > 0
        if mn == "jge":
            return s >= 0
        if mn == "jl":
            return s < 0
        if mn == "jle":
            return s <= 0
        if mn == "ja":
            return u > 0
        if mn == "jae":
            return u >= 0
        if mn == "jb":
            return u < 0
        if mn == "jbe":
            return u <= 0
        if sf is None:
            raise _Unsupported(f"{mn} after cmp")
        return sf if mn == "js" else ~sf

    @staticmethod
    def reads_tracked(p: _Path, ins: Insn) -> bool:
        ops = ins.ops
        if ins.mnem in _FLAGLESS and "," in ops and ops.split(",")[0].strip() in _REGS:
            ops = ops.split(",", 1)[1]          # register destination: written, not read
        toks = re.findall(r"[a-z0-9]+", ops)
        return any(t in _REGS and _REGS[t][0] in p.regs for t in toks)

    def dispatch_ahead(self, p: _Path, a: int, depth: int = 5) -> bool:
        """Is `a` the start of neutral filler that leads to more dispatch code?"""
        cur = a
        for _ in range(depth):
            k = self.at.get(cur)
            if k is None:
                return False
            ins = self.insns[k]
            if self.reads_tracked(p, ins) and ins.mnem in ("cmp", "test", "and", "sub", "add",
                                                           "mov", "lea", "movzx"):
                return True
            if ins.mnem not in _FLAGLESS:
                return False
            cur = self.next_addr(ins)
        return False

    # -- one step ------------------------------------------------------------------
    def step(self, p: _Path) -> List[_Path]:
        """Execute the instruction at p.pc.  Returns successor paths ([] = finished,
        p.landing is the handler or the skip label); raises _Unsupported to stop."""
        ins = self.ins_at(p.pc)
        mn = ins.mnem
        ops = [o.strip() for o in ins.ops.split(",")] if ins.ops else []
        nxt = self.next_addr(ins)
        p.steps += 1
        if p.steps > 400:
            raise _Unsupported("step limit")

        if mn in _JCC:
            if p.flags is None or not ops[0].startswith("0x"):
                raise _Unsupported(f"{mn} without flags")
            c = self.cond(mn, p.flags)
            tgt = int(ops[0], 16)
            out = []
            for m, pc, taken in ((c, tgt, True), (~c, nxt, False)):
                if m.any():
                    q = p.take(m, pc)
                    if mn == "je":
                        q.via = "eq" if taken else "ne"
                    elif mn == "jne":
                        q.via = "ne" if taken else "ne-fallthrough"
                    else:
                        q.via = "range"
                    if p.masked:
                        q.via = "masked"
                    q.masked = False
                    out.append(q)
            return out
        if mn == "jmp":
            if ops[0].startswith("0x"):
                p.pc = p.landing = int(ops[0], 16)
                p.flags = None
                return [p]
            v = self.read(p, ops[0])
            out = []
            for t in np.unique(v):
                q = p.take(v == t, int(t))
                q.via = "wildcard" if ins.addr == self.case_jmp else "switch"
                q.masked = False
                out.append(q)
            return out

        if mn in _SETCC and len(ops) == 1 and ops[0] in _REGS and _REGS[ops[0]][1] == 8:
            if p.flags is None:
                raise _Unsupported(f"{mn} without flags")
            base = _REGS[ops[0]][0]
            if base not in p.regs:
                # only the byte register is consumed (test al, cl; jne): track the base
                # register from 0 so that the 8-bit write can be merged
                p.regs[base] = np.zeros(len(p.sel), dtype=np.uint64)
            self.write(p, ops[0], self.cond(_SETCC[mn], p.flags).astype(np.uint64))
            p.pc = nxt
            return [p]

        two = len(ops) == 2
        d = ops[0] if ops else ""
        s = ops[1] if two else ""
        if mn in ("nop",) or mn.startswith("nop"):
            p.pc = nxt
            return [p]
        if mn in ("mov", "movzx", "movsx") and two and d in _REGS:
            if s in _REGS:
                if self.tracked(p, s):
                    self.write(p, d, self.read(p, s))
                    if p.nameptr == _REGS[s][0] and _REGS[d][1] == 64:
                        p.nameptr = _REGS[d][0]
                    p.pc = nxt
                    return [p]
                if p.nameptr == _REGS[s][0] and _REGS[d][1] == 64:
                    self.drop(p, d)
                    p.nameptr = _REGS[d][0]
                    p.pc = nxt
                    return [p]
            mm = _MEM.match(s)
            if mn == "movzx" and mm and p.nameptr is not None and mm.group(1) in _REGS \
                    and _REGS[mm.group(1)][0] == p.nameptr and mm.group(2) is None:
                off = int(mm.group(5), 0) * (-1 if mm.group(4) == "-" else 1) if mm.group(5) else 0
                if 1 <= off <= 3 and s.startswith("byte"):
                    sh = 8 * (3 - off)
                    self.write(p, d, (self.keys[p.sel] >> np.uint64(sh)) & np.uint64(0xFF))
                    p.pc = nxt
                    return [p]
            if _IMM.match(s) and mn == "mov":
                self.write(p, d, self.read(p, s))
                p.pc = nxt
                return [p]
        if mn == "lea" and two and d in _REGS:
            mm = _MEM.match(s)
            if mm:
                b = mm.group(1)
                disp = int(mm.group(5), 0) * (-1 if mm.group(4) == "-" else 1) if mm.group(5) else 0
                if b == "rip" and mm.group(2) is None:
                    self.write(p, d, np.full(len(p.sel), (nxt + disp) & _M64, dtype=np.uint64))
                    p.pc = nxt
                    return [p]
                if self.tracked(p, b) and mm.group(2) is None:
                    self.write(p, d, (self.read(p, b) + np.uint64(disp & _M64)))
                    p.pc = nxt
                    return [p]
                if self.tracked(p, b) and mm.group(2) and self.tracked(p, mm.group(2)):
                    v = self.read(p, b) + self.read(p, mm.group(2)) * np.uint64(int(mm.group(3)))
                    self.write(p, d, v + np.uint64(disp & _M64))
                    p.pc = nxt
                    return [p]
        if mn == "movsxd" and two and d in _REGS:
            mm = _MEM.match(s)
            if mm and mm.group(2) and self.tracked(p, mm.group(1)) and self.tracked(p, mm.group(2)) \
                    and mm.group(3) == "4" and not mm.group(5):
                addr = self.read(p, mm.group(1)) + self.read(p, mm.group(2)) * np.uint64(4)
                vals = np.zeros(len(p.sel), dtype=np.uint64)
                for a in np.unique(addr):
                    vals[addr == a] = np.uint64(struct.unpack("<i", self.eb.read_va(int(a), 4))[0] & _M64)
                self.write(p, d, vals)
                p.via = "switch"
                p.pc = nxt
                return [p]
        if mn in ("shl", "shr", "sar") and two and self.tracked(p, d) and _IMM.match(s):
            v = self.read(p, d)
            k = np.uint64(int(s, 0))
            if mn == "shl":
                v = v << k
            elif mn == "shr":
                v = v >> k
            else:
                raise _Unsupported("sar")
            self.write(p, d, v)
            p.flags = None
            p.pc = nxt
            return [p]
        if mn in ("or", "and", "xor", "add", "sub") and two and d in _REGS:
            if mn == "xor" and d == s:
                self.write(p, d, np.zeros(len(p.sel), dtype=np.uint64))
                p.flags = None
                p.pc = nxt
                return [p]
            if self.tracked(p, d) and (self.tracked(p, s) or _IMM.match(s)):
                a, b = self.read(p, d), self.read(p, s)
                bits = self.width(d)
                if mn == "or":
                    r = a | b
                elif mn == "and":
                    r = a & b
                    p.masked = p.masked or (_IMM.match(s) is not None)
                elif mn == "xor":
                    r = a ^ b
                elif mn == "add":
                    r = a + b
                else:
                    r = a - b
                if mn == "sub":
                    self.set_cmp(p, a, b, bits)
                elif mn == "add":
                    p.flags = None     # CF/OF of add are not modelled; a jcc would stop
                else:
                    self.set_logic(p, r, bits)
                self.write(p, d, r)
                p.pc = nxt
                return [p]
        if mn == "cmp" and two and self.tracked(p, d) and (self.tracked(p, s) or _IMM.match(s)):
            self.set_cmp(p, self.read(p, d), self.read(p, s), self.width(d))
            p.pc = nxt
            return [p]
        if mn == "test" and two and self.tracked(p, d) and self.tracked(p, s):
            self.set_logic(p, self.read(p, d) & self.read(p, s), self.width(d))
            p.pc = nxt
            return [p]
        # neutral filler between compares (does not touch tracked registers)
        if mn in _FLAGLESS and not self.reads_tracked(p, ins):
            if p.flags is not None or self.front[0] <= p.pc < self.front[1] \
                    or self.dispatch_ahead(p, nxt):
                if d in _REGS:
                    self.drop(p, d)
                p.pc = nxt
                return [p]
        raise _Unsupported(f"{mn} {ins.ops}")

    # -- per letter ----------------------------------------------------------------
    def run_letter(self, start: int, init: Dict[str, np.ndarray], nameptr: str,
                   keys: np.ndarray) -> Dict[int, Tuple[np.ndarray, str]]:
        """Evaluate all `keys` from `start`.  Returns handler -> (key indices, via);
        the skip label collects every name that is not a command."""
        self.keys = keys
        work = [_Path(start, np.arange(len(keys)), dict(init), None, start, "wildcard",
                      False, nameptr)]
        res: Dict[int, List[np.ndarray]] = {}
        vias: Dict[int, str] = {}
        while work:
            p = work.pop()
            if self.is_skip(p.pc):
                res.setdefault(self.skip, []).append(p.sel)
                continue
            try:
                succ = self.step(p)
            except _Unsupported as e:
                h = p.landing
                if self.is_skip(h):
                    h = self.skip
                else:
                    self.stops.setdefault(h, str(e))
                res.setdefault(h, []).append(p.sel)
                vias.setdefault(h, p.via)
                continue
            work.extend(succ)
        return {h: (np.sort(np.concatenate(v)), vias.get(h, "skip")) for h, v in res.items()}


def _front_end(insns: List[Insn], case_jmp: int) -> Tuple[int, int, str, str]:
    """Find (address after `ja skip` of the letter check, skip label, name-pointer register,
    script-pointer base register)."""
    at = {ins.addr: k for k, ins in enumerate(insns)}
    k = at[case_jmp]
    sp = next(i for i in insns if i.mnem == "lea" and i.ops.endswith(" + 0x300]"))
    script_reg = _REGS[sp.ops.split("[")[1].split()[0]][0]   # lea rax, [rbx + 0x300]
    for j in range(k, max(0, k - 40), -1):
        a, b, c = insns[j - 2], insns[j - 1], insns[j]
        if a.mnem == "add" and a.ops.endswith(", -0x41") and b.mnem == "cmp" \
                and b.ops.endswith(", 0x19") and c.mnem == "ja":
            ld = insns[j - 3]
            mm = re.match(r"^\w+, byte ptr \[(\w+)\]$", ld.ops)
            if ld.mnem != "movzx" or not mm:
                raise ValueError(f"letter load not found before 0x{a.addr:x}")
            return c.addr + c.size, int(c.ops, 16), mm.group(1), script_reg
    raise ValueError("dispatch front end not found")


def _patterns(names: List[str]) -> List[str]:
    """Compress a set of 3-character names into product patterns ('*' = whole alphabet)."""
    if not names:
        return []
    cols = [sorted({n[i] for n in names}) for i in range(3)]
    if len(names) == int(np.prod([len(c) for c in cols])):
        if all(len(c) in (1, _NA) for c in cols):
            return ["".join(c[0] if len(c) == 1 else "*" for c in cols)]
        if len(names) == 1:
            return names
    # split on the first column that is not a single character
    out: List[str] = []
    for ch in cols[0]:
        sub = [n[1:] for n in names if n[0] == ch]
        if len(sub) == _NA * _NA:
            out.append(ch + "**")
            continue
        cols2 = [sorted({n[i] for n in sub}) for i in range(2)]
        if len(sub) == len(cols2[0]) * len(cols2[1]) and all(len(c) in (1, _NA) for c in cols2):
            out.append(ch + "".join(c[0] if len(c) == 1 else "*" for c in cols2))
        else:
            out.extend(ch + n for n in sorted(sub))
    return out


def all_keys() -> Tuple[np.ndarray, List[str]]:
    c = _ACODES
    k = (c[:, None, None] << 16) | (c[None, :, None] << 8) | c[None, None, :]
    names = [a + b + d for a in ALPHABET for b in ALPHABET for d in ALPHABET]
    return k.reshape(-1).astype(np.uint64), names


def extract(eb: Eboot, ix: Index, interp_va: int = INTERP_VA,
            table_va: int = LETTER_TABLE_VA) -> VmTable:
    insns: List[Insn] = ix.insns[interp_va]
    jmps = [i for i in insns if i.mnem == "jmp" and not i.ops.startswith("0x")]
    case_jmp = jmps[0].addr                           # `jmp rdx` of the letter table
    start, skip, nameptr, script_reg = _front_end(insns, case_jmp)
    cases = letter_cases(eb, table_va)
    ev = _Evaluator(eb, insns, skip, (start, case_jmp), script_reg)
    vm = VmTable(interp_va, skip, ev.fail_targets, cases)
    keys, names = all_keys()
    # registers at `start`: the letter index (c0 - 'A') is in the register compared with 0x19
    k0 = ev.at[start] - 2
    letter_reg = insns[k0].ops.split(",")[0].strip()
    for c0 in sorted(cases):
        init = {_REGS[letter_reg][0]: np.full(len(keys), ord(c0) - 0x41, dtype=np.uint64)}
        res = ev.run_letter(start, init, _REGS[nameptr][0], keys)
        for h, (sel, via) in sorted(res.items()):
            if h == skip:
                continue
            pats = _patterns([names[i] for i in sel])
            if len(sel) == len(keys):
                via = "wildcard"
            for pat in pats:
                n = int(np.prod([_NA if ch == "*" else 1 for ch in pat]))
                vm.commands.append(Command(c0 + pat, h, via, n))
    vm.commands.sort(key=lambda c: c.name)
    vm.stops = sorted(ev.stops.items())
    return vm


def find_budget(insns: List[Insn], skip: int) -> Tuple[Optional[int], Optional[int], Optional[str]]:
    """Loop-limit check at the skip label.

    ScriptStep:     inc r11d; cmp r11d, 0x3E8; je EPILOGUE        -> (EPILOGUE, 1000, r11d)
    Script_Replay:  cmp r15d, 0x3A; jg EXIT; inc r15d; ...        -> (EXIT, 59, r15d)
    The returned budget is the number of commands after which the loop is left
    (counted at the skip label): 1000 resp. 60 (r15d > 58 is tested before the increment)."""
    at = {ins.addr: k for k, ins in enumerate(insns)}
    k = at.get(skip)
    if k is None:
        return None, None, None
    for j in range(k, min(k + 4, len(insns) - 1)):
        m = re.match(r"^(\w+), (0x[0-9a-f]+|\d+)$", insns[j].ops) if insns[j].mnem == "cmp" else None
        if m and insns[j + 1].ops.startswith("0x"):
            reg, imm = m.group(1), int(m.group(2), 0)
            tgt = int(insns[j + 1].ops, 16)
            if insns[j + 1].mnem == "je":            # counter incremented before the test
                return tgt, imm, reg
            if insns[j + 1].mnem == "jg":            # tested before the increment
                return tgt, imm + 2, reg
    return None, None, None


def classify_exits(vm: VmTable, insns: List[Insn], limit: int = 600) -> None:
    """Static reachability from each handler: does it fall back into the dispatch
    loop (continue with the next command in the same call) or reach the epilogue
    (yield: the call returns, for ScriptStep the frame ends)?  For Script_Replay the
    loop exit is reported as "exit".  Calls are not followed; indirect jumps stop
    the walk ("?")."""
    at = {ins.addr: k for k, ins in enumerate(insns)}
    loop_heads = set(vm.fail_targets)
    yname = "exit" if vm.loop_va == REPLAY_VA else "yield"
    for c in vm.commands:
        seen: Set[int] = set()
        work = [c.handler]
        res: Set[str] = set()
        calls: List[int] = []
        steps = 0
        while work and steps < limit:
            a = work.pop()
            if a in seen:
                continue
            if a in loop_heads:
                res.add("continue")
                continue
            if vm.yield_va is not None and a == vm.yield_va:
                res.add(yname)
                continue
            k = at.get(a)
            if k is None:
                res.add("?")
                continue
            seen.add(a)
            while k < len(insns) and steps < limit:
                ins = insns[k]
                steps += 1
                if ins.addr != a and ins.addr in seen:
                    break
                seen.add(ins.addr)
                if ins.addr in loop_heads:
                    res.add("continue")
                    break
                if vm.yield_va is not None and ins.addr == vm.yield_va:
                    res.add(yname)
                    break
                if ins.mnem == "call" and ins.ops.startswith("0x"):
                    if len(calls) < 8:
                        calls.append(int(ins.ops, 16))
                elif ins.mnem == "jmp":
                    if ins.ops.startswith("0x"):
                        work.append(int(ins.ops, 16))
                    else:
                        res.add("?")
                    break
                elif ins.mnem.startswith("j"):
                    if ins.ops.startswith("0x"):
                        work.append(int(ins.ops, 16))
                elif ins.mnem == "ret":
                    res.add("?")
                    break
                k += 1
        if steps >= limit:
            res.add("?")
        if {"continue", yname} <= res:
            c.exits = "both"
        elif res:
            c.exits = sorted(res - {"?"})[0] if res - {"?"} else "?"
        c.calls = calls


def analyse(eb: Eboot, ix: Index, interp_va: int = INTERP_VA,
            table_va: int = LETTER_TABLE_VA) -> VmTable:
    """extract() + budget/epilogue detection + exit classification."""
    vm = extract(eb, ix, interp_va, table_va)
    insns = ix.insns[interp_va]
    vm.yield_va, vm.budget, vm.counter_reg = find_budget(insns, vm.skip_va)
    classify_exits(vm, insns)
    return vm


def _main(argv: List[str]) -> int:
    import argparse

    from eboot import eboot_path

    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--json")
    ap.add_argument("--stops", action="store_true", help="print where each dispatch path stopped")
    a = ap.parse_args(argv)
    p = eboot_path()
    if p is None:
        print("game not found", file=sys.stderr)
        return 2
    eb = Eboot.open(p)
    ix = Index.build(eb)
    dump = []
    for fva, tva in find_interpreters(ix):
        vm = analyse(eb, ix, fva, tva)
        print(f"interpreter 0x{vm.loop_va:x} (letter table 0x{tva:x}); skip 0x{vm.skip_va:x}, "
              f"no-op trampolines {sorted(hex(x) for x in vm.fail_targets if x != vm.skip_va)}; "
              f"loop exit {hex(vm.yield_va) if vm.yield_va else None}, budget {vm.budget} "
              f"({vm.counter_reg})")
        for c in vm.commands:
            print(f"  _{c.name:<5} handler 0x{c.handler:06x}  {c.via:<15} exits={c.exits:<8} "
                  f"calls={[hex(x) for x in c.calls[:4]]}")
        nfixed = sum("*" not in c.name for c in vm.commands)
        print(f"  {len(vm.commands)} entries ({nfixed} fixed names, "
              f"{len(vm.commands) - nfixed} patterns)")
        if a.stops:
            for h, why in vm.stops:
                print(f"    stop 0x{h:x}: {why}")
        dump.append({"interpreter": vm.loop_va, "letter_table": tva, "skip": vm.skip_va,
                     "loop_exit": vm.yield_va, "budget": vm.budget,
                     "counter_reg": vm.counter_reg,
                     "commands": [{"name": c.name, "handler": c.handler, "via": c.via,
                                   "names_covered": c.nkeys, "exits": c.exits,
                                   "calls": c.calls}
                                  for c in vm.commands]})
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(dump, f, indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
