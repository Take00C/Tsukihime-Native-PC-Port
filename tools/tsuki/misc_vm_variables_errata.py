"""Script-VM variables: emulation harness, corpus census and the engine re-check errata.

Companion of ``tsuki.vm_vars`` (the operand parser and read/write model a port needs).
This module holds everything that PROVES that model against the user's own eboot.bin and
scripts; docs/formats/vm_variables.md has the results (misc_vm_variables_errata.md points there).

* ``VarEmu``             unicorn harness running the engine's own handlers (below);
* ``compare_one`` / ``fuzz``      engine vs ``tsuki.vm_vars`` on one / N random statements;
* ``operand_table``      every operand form in every reader (emulated, cross-checked);
* ``first_byte_sweep`` / ``choice_setter_sweep``  every byte as the first (and for '_'
                         operands the second) byte of every operand position;
* ``choice_flag_cases`` / ``choice_store_check`` / ``choice_point_writes`` /
  ``snapshot_body_writes``  the choice commands under scan mode, F1118, the snapshot
                         gate (F929 / F1102 / block byte), both result stores, SELD's
                         flowchart call and the snapshot body;
* ``save_prompt_flow``   the _SELA save prompt in the 26 retail scripts (model walk);
* ``emulate_history`` / ``history_check``  the engine's history snapshot, slot-load and
                         rewind loops against vm_vars.history_roundtrip / history_after_rewind;
* ``run_eval_condition`` Script_EvalCondition (VA 0x79D30, the _EIF_ evaluator);
* ``corpus_census``      operand forms per command over the 549 scripts, F/G index
                         classification (history record / system data / transient);
* ``save_image_layout``  erratum (c), decoded from SaveImage_WriteBlock / _Commit;
* ``read_history_lists`` the three F index lists a history record / save slot keeps;
* ``prefetch_v_names``   erratum (b), Script_Prefetch's V case under emulation.

CLI (writes samples/misc_vm_variables_errata/ and prints a summary)::

    python -m tsuki.misc_vm_variables_errata census | table | sweep | history | eif | choice |
                                             fuzz [N] [SEED] | layout | vcase | all

``VarEmu`` loads the user's eboot.bin (CUSA47999 v01.01) into the unicorn CPU emulator
(``pip install unicorn capstone`` into the project venv) and executes ONE statement
through the engine's own code:

* ``run(stmt)``            ScriptStep (VA 0x95E60) from its entry to the loop continue
                           (VA 0x95EB0), the yield/return (VA 0x9B516) or the label
                           lookup (VA 0x794D0, captured as the jump target); every
                           command through its own handler (SELA / SELS / SELD included;
                           only UI, text layout and flowchart calls are stubbed, see run());
* ``run(stmt, prefetch=True)``  Script_Prefetch (VA 0x9B760) up to its skip label
                           (VA 0x9FF30) / exit (VA 0xA017F);
* ``run_choice_store(...)``    the choice UI's result stores (list VA 0xA65E3..0xA6647,
                           two-button VA 0xA7B99..0xA7BE2).

Every 16-bit read and write in the variable window (VA 0x1620000..0x1660000, which
contains every address an F/G/C/_ operand can form) is logged in program order, so the
log can be compared one to one with ``tsuki.vm_vars.VarMemory.log``.  libc imports are
Python stubs (memcpy/memmove/memset/strchr/strpbrk/strtol/strlen/new/delete), every
other import returns 0; AVX instructions (not supported by unicorn) are skipped, which
only affects the history-snapshot copies, never a variable access.
Tests: tests/test_vm_vars.py (model vs engine) and tests/test_misc_vm_variables_errata.py
(census, errata).
"""
from __future__ import annotations

import argparse
import collections
import functools
import json
import random
import re
import struct
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING, Callable, Counter, Dict, List, Optional, Set, Tuple, Union

if TYPE_CHECKING:
    from tsuki import vm_vars

TEXT_OFF, TEXT_LEN = 0x2280, 0x329DC8
RELRO_OFF, RELRO_LEN, RELRO_VA = 0x32C210, 0x35ED0, 0x400000
DATA_OFF, DATA_LEN, DATA_VA, DATA_MEMSZ = 0x365FA0, 0x7D66E0, 0x600000, 0x1A85790
STK_LO, STK_HI = 0x7F000000, 0x7F100000
BUF = 0x20000000                      # statement buffer
RET = 0x7EF00000                      # return trap
LOG_LO, LOG_HI = 0x1620000, 0x1660000
FILL_LO, FILL_HI = 0x164B000, 0x164D800   # var_F - 0x1D0 .. after var_G (+0x240): pattern-filled
VAR_LO, VAR_HI = 0x164B1D0, 0x164D5C0     # var_F .. end of var_G (8-bit writes are logged here)
ARGBUF = 0x16543C0
SAVE_AREAS = ((0x164D7D0, 0x164B1D0, 0x960), (0x164E130, 0x164BB30, 0xAF0), (0x164EC20, 0x164C620, 0xFA0))
CONTROL = frozenset({0x165420C, 0x165420E, 0x1654030, 0x1654034, 0x1654344, 0x1654348})
LABEL_LOOKUP = 0x794D0
PLT_LO, PLT_HI = 0x1734A0, 0x174980
# regions restored from the eboot image before every run (everything the handlers write)
RESTORE = ((LOG_LO, LOG_HI - LOG_LO), (0x1750600, 0x100), (0x174D900, 0x600), (0x1201000, 0x3000),
           (0x120A6C0, 0x40))

MAIN_ENTRY, MAIN_GO, MAIN_STOPS = 0x95E60, 0x95F4E, (0x95EB0, 0x9B516)
MAIN_CONTINUE, MAIN_YIELD = MAIN_STOPS        # loop continue / ScriptStep returns (yield)
PRE_ENTRY, PRE_GO, PRE_STOPS = 0x9B760, 0x9B915, (0x9FF30, 0xA017F, 0xA0193)
SELA_FUNC, SELS_FUNC = 0xB8400, 0xB82E0       # two-answer dialog helpers (modes 3 / 2)
CHOICE_UI = 0xA5DF0                   # choice UI set-up (stubbed: returns 0, hit recorded)
CHOICE_POINT = 0xB8620                # SELD argument 1 -> flowchart edge marks (gallery.md 5.3)
SCENE_JUMP_END = 0x7E620              # ends a running scene jump (only when word 0x120A6D2 != 0)
# SELD runs to its yield with the text layout / skip-stop / UI calls stubbed (return 0)
SELD_STUBS = (CHOICE_POINT, 0xC6A70, SCENE_JUMP_END, 0x56150, 0x53860, 0x76040, 0x41A20, 0xC6C20,
              CHOICE_UI)
SEL2_STUBS = (CHOICE_UI, SCENE_JUMP_END)      # SELA / SELS through ScriptStep's own handlers
SEL_STOP = CHOICE_UI                          # earlier name of CHOICE_UI (kept for old scratch scripts)
SNAPSHOT_BODY, SNAPSHOT_EXIT = 0x7AFDD, 0x7B4A3   # History_Snapshot: first insn after the gate / epilogue
DEFAULT_WATCH = frozenset({SNAPSHOT_BODY, CHOICE_UI, CHOICE_POINT})
SCAN_MODE, SKIP_COUNTER, SNAPSHOT_BLOCK, CHOICE_MODE = 0x174DE48, 0x165420E, 0x16541F4, 0x17506B0

PLT_NAMES = {
    0x1734A0: "delete", 0x1739F0: "delete", 0x173660: "delete",
    0x1734B0: "stack_chk_fail",
    0x1734C0: "new", 0x1737E0: "new", 0x173680: "new",
    0x1734D0: "memmove", 0x173650: "memmove",
    0x1734F0: "memset",
    0x1735C0: "strchr", 0x1735D0: "strpbrk", 0x1735E0: "strtol",
    0x173600: "strlen",
}



@dataclass
class EmuResult:
    log: List[Tuple[str, int, int]] = field(default_factory=list)   # ('r'|'w', va, value)
    labels: List[bytes] = field(default_factory=list)
    end: Optional[int] = None
    err: Optional[str] = None
    argbuf: bytes = b""
    choice_kind: int = 0
    choice_index: int = 0
    choice_id: int = 0
    plt: List[int] = field(default_factory=list)
    hits: List[int] = field(default_factory=list)                  # executed VAs of VarEmu.watch
    hit_rdi: List[Tuple[int, int]] = field(default_factory=list)   # (VA, RDI) at each watched VA
    choice_count: int = 0                                          # u16 0x1750646 (SELR lines)
    choice_values: List[int] = field(default_factory=list)         # u16 0x1750630[count]
    choice_locked: List[int] = field(default_factory=list)         # u8 0x175064C[count]
    choice_mode: int = 0                                           # u32 0x17506B0
    byte_writes: List[Tuple[int, int]] = field(default_factory=list)   # 8-bit writes into F / Z / G


class VarEmu:
    def __init__(self, eboot: Union[str, Path]) -> None:
        import capstone
        from unicorn import (UC_ARCH_X86, UC_HOOK_CODE, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE,
                             UC_MODE_64, Uc)
        d = Path(eboot).read_bytes()
        self.uc = uc = Uc(UC_ARCH_X86, UC_MODE_64)
        uc.mem_map(0, 0x440000)
        uc.mem_write(0, d[TEXT_OFF:TEXT_OFF + TEXT_LEN])
        uc.mem_write(RELRO_VA, d[RELRO_OFF:RELRO_OFF + RELRO_LEN])
        size = (DATA_MEMSZ + 0xFFFF) & ~0xFFFF
        uc.mem_map(DATA_VA, size)
        uc.mem_write(DATA_VA, d[DATA_OFF:DATA_OFF + DATA_LEN])
        self._data_size = size
        self.window0 = bytes(uc.mem_read(LOG_LO, LOG_HI - LOG_LO))
        self._clean_data: Optional[Dict[int, bytes]] = None
        uc.mem_map(STK_LO, STK_HI - STK_LO)
        uc.mem_map(BUF, 0x10000)
        uc.mem_map(RET, 0x1000)
        uc.mem_map(0x7E000000, 0x100000)          # heap for operator new
        self._heap = 0x7E000000
        self._md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self._ins: Dict[int, Tuple[int, str]] = {}
        self.r = EmuResult()
        self.go = False
        self.go_va = MAIN_GO
        self.stops: Tuple[int, ...] = MAIN_STOPS
        self.stubs: Dict[int, int] = {}
        self.redirects: Dict[int, int] = {}   # VA -> VA to continue at (skips a code range)
        self.watch: Set[int] = set(DEFAULT_WATCH)   # VAs whose execution is recorded in r.hits
        uc.hook_add(UC_HOOK_CODE, self._code)
        uc.hook_add(UC_HOOK_MEM_READ, self._rd, begin=LOG_LO, end=LOG_HI - 1)
        uc.hook_add(UC_HOOK_MEM_WRITE, self._wr, begin=LOG_LO, end=LOG_HI - 1)

    # -- registers / memory -------------------------------------------------
    def reg(self, name: str) -> int:
        from unicorn import x86_const
        return int(self.uc.reg_read(getattr(x86_const, "UC_X86_REG_" + name)))

    def set_reg(self, name: str, v: int) -> None:
        from unicorn import x86_const
        self.uc.reg_write(getattr(x86_const, "UC_X86_REG_" + name), v & (1 << 64) - 1)

    def u16(self, va: int) -> int:
        return int(struct.unpack("<H", bytes(self.uc.mem_read(va, 2)))[0])

    def cstr(self, va: int, n: int = 64) -> bytes:
        return bytes(self.uc.mem_read(va, n)).split(b"\0")[0]

    def window_word0(self, va: int) -> int:
        """Initial (eboot image) value of a word in the logged window."""
        o = va - LOG_LO
        return int(struct.unpack_from("<H", self.window0, o)[0]) if 0 <= o < len(self.window0) - 1 else 0

    def initial_word(self, va: int, fill: Optional[Callable[[int], int]], prefetch: bool = False,
                     init: Optional[Dict[int, int]] = None) -> int:
        """Value a run with ``fill``/``init`` starts with at ``va`` (fill inside
        FILL_LO..FILL_HI, the eboot image elsewhere; for Script_Prefetch also the copies of
        var_F/var_Z/var_G its prologue makes at 0x164D7D0/0x164E130/0x164EC20): the
        model's VarMemory.init."""
        if init and va in init:
            return init[va] & 0xFFFF
        if prefetch:
            for save, base, n in SAVE_AREAS:
                if save <= va < save + n:
                    return self.initial_word(base + (va - save), fill, False, init)
        if fill is not None and FILL_LO <= va < FILL_HI:
            return fill(va) & 0xFFFF
        return self.window_word0(va)

    # -- hooks --------------------------------------------------------------
    def _ret(self, val: int) -> None:
        rsp = self.reg("RSP")
        self.set_reg("RIP", struct.unpack("<Q", bytes(self.uc.mem_read(rsp, 8)))[0])
        self.set_reg("RSP", rsp + 8)
        self.set_reg("RAX", val)

    def _code(self, uc: object, a: int, size: int, _: object) -> None:
        if a in self.watch:
            self.r.hits.append(a)
            self.r.hit_rdi.append((a, self.reg("RDI")))
        if self.go and a in self.stops:
            self.r.end = a
            self.uc.emu_stop()
            return
        if PLT_LO <= a < PLT_HI:
            self.r.plt.append(a)
            self._ret(self._libc(PLT_NAMES.get(a)))
            return
        if self.go and a == LABEL_LOOKUP:
            self.r.labels.append(self.cstr(self.reg("RDI")))
            self.r.end = a
            self.uc.emu_stop()
            return
        if a in self.stubs:
            self._ret(self.stubs[a])
            return
        if a in self.redirects:
            self.set_reg("RIP", self.redirects[a])
            return
        ins = self._ins.get(a)
        if ins is None:
            b = bytes(self.uc.mem_read(a, 16))
            _, sz, mn, _ops = next(self._md.disasm_lite(b, a))
            ins = self._ins[a] = (sz, mn)
        if ins[1].startswith("v"):
            self.set_reg("RIP", a + ins[0])
            return
        if a == self.go_va:
            self.go = True

    def _rd(self, uc: object, acc: int, a: int, size: int, v: int, _: object) -> None:
        if self.go and size == 2 and a not in CONTROL:
            self.r.log.append(("r", a, self.u16(a)))

    def _wr(self, uc: object, acc: int, a: int, size: int, v: int, _: object) -> None:
        if self.go and size == 2 and a not in CONTROL:
            self.r.log.append(("w", a, v & 0xFFFF))
        elif self.go and size == 1 and VAR_LO <= a < VAR_HI:
            self.r.byte_writes.append((a, v & 0xFF))

    # -- libc stubs ---------------------------------------------------------
    def _libc(self, name: Optional[str]) -> int:
        uc = self.uc
        rdi, rsi, rdx = self.reg("RDI"), self.reg("RSI"), self.reg("RDX")
        if name == "memmove":
            if rdx:
                uc.mem_write(rdi, bytes(uc.mem_read(rsi, rdx)))
            return rdi
        if name == "memset":
            if rdx:
                uc.mem_write(rdi, bytes([rsi & 0xFF]) * rdx)
            return rdi
        if name == "strlen":
            return len(self._cstr(rdi))
        if name == "strchr":
            k = 0
            while True:
                b = uc.mem_read(rdi + k, 1)[0]
                if b == rsi & 0xFF:
                    return rdi + k
                if b == 0:
                    return 0
                k += 1
        if name == "strpbrk":
            acc = self._cstr(rsi)
            k = 0
            while True:
                b = uc.mem_read(rdi + k, 1)[0]
                if b == 0:
                    return 0
                if b in acc:
                    return rdi + k
                k += 1
        if name == "strtol":
            txt = self._cstr(rdi, 4096)
            m = re.match(rb"[ \t\n\v\f\r]*([+-]?)([0-9]+)", txt)
            if m is None:
                v, e = 0, rdi
            else:
                v = int(m.group(2))
                v = -min(v, 1 << 63) if m.group(1) == b"-" else min(v, (1 << 63) - 1)
                e = rdi + m.end()
            if rsi:
                uc.mem_write(rsi, struct.pack("<Q", e))
            return v & (1 << 64) - 1
        if name == "new":
            a = (self._heap + 15) & ~15
            self._heap = a + max(rdi, 16)
            return a
        if name == "stack_chk_fail":
            raise RuntimeError("__stack_chk_fail")
        return 0

    def _cstr(self, a: int, limit: int = 0x10000) -> bytes:
        out = bytearray()
        while len(out) < limit:
            c = self.uc.mem_read(a + len(out), 1)[0]
            if c == 0:
                break
            out.append(c)
        return bytes(out)

    # -- set-up -------------------------------------------------------------
    def _prepare(self, stmt: bytes, fill: Optional[Callable[[int], int]], init: Optional[Dict[int, int]],
                 argbuf: Optional[bytes], globals_: Optional[Dict[int, bytes]]) -> None:
        uc = self.uc
        if self._clean_data is None:
            self._clean_data = {a: bytes(uc.mem_read(a, n)) for a, n in RESTORE}
        else:
            for a, b in self._clean_data.items():
                uc.mem_write(a, b)
        if fill is not None:
            words = struct.pack(f"<{(FILL_HI - FILL_LO) // 2}H",
                                *[fill(a) & 0xFFFF for a in range(FILL_LO, FILL_HI, 2)])
            uc.mem_write(FILL_LO, words)
        for a, v in (init or {}).items():
            uc.mem_write(a, struct.pack("<H", v & 0xFFFF))
        if argbuf is not None:
            uc.mem_write(ARGBUF, argbuf)
        for a, b in (globals_ or {}).items():
            uc.mem_write(a, b)
        uc.mem_write(BUF, bytes(0x1000))
        uc.mem_write(BUF, stmt)
        self.r = EmuResult()
        self.go = False
        self._heap = 0x7E000000
        self.set_reg("RSP", STK_HI - 0x8000)
        self.set_reg("RBP", STK_HI - 0x7000)
        uc.mem_write(STK_HI - 0x8000, struct.pack("<Q", RET))

    def _finish(self) -> EmuResult:
        r = self.r
        r.argbuf = bytes(self.uc.mem_read(ARGBUF, 0x40))
        r.choice_kind = self.uc.mem_read(0x1750648, 1)[0]
        r.choice_index = self.u16(0x175064A)
        r.choice_id = self.u16(0x1750698)
        r.choice_count = n = self.u16(0x1750646)
        if 0 <= n <= 10:
            r.choice_values = list(struct.unpack(f"<{n}H", bytes(self.uc.mem_read(0x1750630, 2 * n))))
            r.choice_locked = list(bytes(self.uc.mem_read(0x175064C, n)))
        r.choice_mode = int(struct.unpack("<I", bytes(self.uc.mem_read(CHOICE_MODE, 4)))[0])
        return r

    def _start(self, entry: int, count: int) -> None:
        from unicorn import UcError
        try:
            self.uc.emu_start(entry, RET, count=count)
        except UcError as e:
            self.r.err = f"{e} at {self.reg('RIP'):#x}"

    # -- runs ---------------------------------------------------------------
    def run(self, stmt: bytes, fill: Optional[Callable[[int], int]] = None,
            init: Optional[Dict[int, int]] = None, argbuf: Optional[bytes] = None,
            prefetch: bool = False, globals_: Optional[Dict[int, bytes]] = None,
            count: int = 400_000, snapshot_body: bool = False, choice_point: bool = False) -> EmuResult:
        """Execute the first statement of ``stmt`` (bytes; append e.g. ``_DMMY();``).
        ``fill(va)`` initialises the words FILL_LO..FILL_HI, ``init`` sets single words,
        ``globals_`` raw bytes (e.g. the scan-mode byte).  Every command runs through the
        interpreter's own handler up to the loop continue / yield / label lookup.

        Stubbed (return 0, the call recorded in ``hits`` / ``hit_rdi``): the choice UI set-up
        VA 0xA5DF0 and the scene-jump end VA 0x7E620 for SELA / SELS / SELD, and for SELD
        also its text layout and skip-stop calls and, unless ``choice_point``, the flowchart
        edge marking VA 0xB8620 (gallery state).  Unless ``snapshot_body``, History_Snapshot
        returns right after its gate (VA 0x7AFDD -> epilogue 0x7B4A3; the body only reads
        variables), so ``SNAPSHOT_BODY in hits`` says whether the gate passed."""
        self._prepare(stmt, fill, init, argbuf, globals_)
        name = stmt[stmt.index(b"_") + 1:stmt.index(b"_") + 5]
        self.redirects = {} if snapshot_body else {SNAPSHOT_BODY: SNAPSHOT_EXIT}
        if prefetch:
            self.go_va, self.stops, self.stubs = PRE_GO, PRE_STOPS, {}
            self.set_reg("RSI", 0x18)
        else:
            self.go_va, self.stops = MAIN_GO, MAIN_STOPS
            self.stubs = {}
            if name == b"SELD":
                self.stubs = {a: 0 for a in SELD_STUBS if not (choice_point and a == CHOICE_POINT)}
            elif name in (b"SELA", b"SELS"):
                self.stubs = {a: 0 for a in SEL2_STUBS}
        self.set_reg("RDI", BUF)
        self._start(PRE_ENTRY if prefetch else MAIN_ENTRY, count)
        return self._finish()

    def run_choice_store(self, kind: int, index: int, cursor: int, values: List[int],
                         fill: Optional[Callable[[int], int]] = None, two_button: bool = False) -> EmuResult:
        """The choice UI's result store with the kind byte 0x1750648, index 0x175064A,
        cursor word 0x17506A2 and value table 0x1750630 set: the list input's store (mode 1,
        VA 0xA65E3..0xA6647) or, with ``two_button``, the two-button input's (mode 2,
        VA 0xA7B99..0xA7BE2; it returns at 0xA7BC0 when the cursor is not 0 / 1)."""
        g = {0x1750648: bytes([kind]), 0x175064A: struct.pack("<H", index & 0xFFFF),
             0x17506A2: struct.pack("<H", cursor & 0xFFFF)}
        if values:
            g[0x1750630] = struct.pack(f"<{len(values)}H", *[v & 0xFFFF for v in values])
        self._prepare(b"", fill, None, None, g)
        self.go = True
        self.redirects = {}
        if two_button:
            self.go_va, self.stops, self.stubs = -1, (0xA7BE2, 0xA7BC0, RET), {}
            self._start(0xA7B99, 10_000)
        else:
            self.go_va, self.stops, self.stubs = -1, (0xA6647, RET), {}
            self._start(0xA65E3, 10_000)
        return self._finish()


def pattern(va: int) -> int:
    """Default variable-window fill: a value that identifies its address."""
    return 0x2000 + ((va - LOG_LO) // 2) % 0xC000


# ---------------------------------------------------------------------------
# fuzzing: random statements over every operand form, compared with tsuki.vm_vars
# ---------------------------------------------------------------------------

_LETTERS = "FFFFGGGCC_ZXDfgcz1-` "
_INDEX = ["12", "0", "7", "823", "847", "1199", "1200", "1999", "2000", "0012", "070", "-3",
          "1+2", "12a", "a5", "", "32767", "32768", "65535", "70000", "600", "650", "700",
          "701", "599", "4000", "-600"]


def _rand_index(rng: random.Random) -> str:
    if rng.random() < 0.6:
        return rng.choice(_INDEX)
    return str(rng.randrange(0, 2600))


def rand_operand(rng: random.Random, value: bool = False) -> str:
    """A random operand text: variables of every letter, '_' forms, tags, literals."""
    r = rng.random()
    if r < 0.15:
        d = rng.choice(["000", "012", "600", "650", "700", "701", "599", "999", "0", "12", "1000"])
        return "_" + rng.choice("DDDXG_0") + d
    if r < 0.2:
        return "`001:" + rng.choice("FGC_") + _rand_index(rng)
    if value and r < 0.45:
        return str(rng.randrange(-40000, 70000)) if rng.random() < 0.3 else str(rng.randrange(0, 9))
    return rng.choice(_LETTERS) + _rand_index(rng)


def rand_statement(rng: random.Random) -> Tuple[str, bool]:
    """(statement text, prefetch?)"""
    cmd = rng.choice(["WKST"] * 5 + ["WKAD"] * 3 + ["WKOR", "WKOF", "WKBO", "IF__", "IF__",
                                                      "IF__", "GOTS", "GOTS", "SELD", "SELR", "SELR",
                                                      "SELA", "SELA", "SELS"])
    pre = cmd in ("WKST", "WKAD", "WKOR", "WKOF", "IF__") and rng.random() < 0.3
    if cmd == "WKST":
        op = rng.choice(["eq"] * 6 + ["ne", "e", "", "x", "eqq", "E"])
        args = [rand_operand(rng), op, rand_operand(rng, True)]
        if rng.random() < 0.08:
            args = args[:rng.randrange(1, 3)]
    elif cmd in ("WKAD", "WKOR", "WKOF", "WKBO"):
        args = [rand_operand(rng), rand_operand(rng, True)]
        if rng.random() < 0.08:
            args = args[:1]
    elif cmd == "IF__":
        op = rng.choice(["eq", "ne", "gt", "ge", "lt", "le", "e", "g", "l", "n", "gx", "lq",
                         "x", "", "E", "f"])
        args = [rand_operand(rng, True), op, rand_operand(rng, True),
                rng.choice(["LBL", "LV00000", "", "A"])]
        if rng.random() < 0.1:
            args = args[:rng.randrange(1, 4)]
    elif cmd == "GOTS":
        sel = rng.choice(["_D000", "_D012", "_X003", "_D600", "_D650", "_D1", "_D12", "F000",
                          "_D0123", "_Q999", "_D-12"])
        args = [sel] + [f"L{i}" for i in range(rng.randrange(0, 5))]
    elif cmd == "SELD":
        tgt = rng.choice(["_D000", "_D012", "_G005", "_X123", "F000", "_D1", "_D650", "", "_D999"])
        args = [tgt] + ([str(rng.randrange(-2, 90))] if rng.random() < 0.6 else [""])
    elif cmd in ("SELA", "SELS"):
        tgt = rng.choice(["_D000", "_D000", "_D012", "_G005", "_X123", "F000", "_D650", "", "_D0001"])
        args = [tgt, ""] if rng.random() < 0.8 else [tgt]
    else:
        args = [str(rng.randrange(0, 4)), "$000001"] + rng.choice([[], ["1"], ["0"], ["01"], ["`001:1"]])
    return f"_{cmd}({','.join(args)});", pre


@dataclass(frozen=True)
class Flags:
    """Engine flags a statement can run under (both sides of a comparison)."""
    scan: bool = False                  # scan mode byte 0x174DE48
    skip: int = 0                       # if-block skip counter 0x165420E
    block: int = 0                      # 'message continues' byte 0x16541F4 (snapshot gate)

    def globals_(self) -> Dict[int, bytes]:
        return {SCAN_MODE: bytes([1 if self.scan else 0]), SKIP_COUNTER: struct.pack("<H", self.skip & 0xFFFF),
                SNAPSHOT_BLOCK: bytes([self.block & 0xFF])}


NO_FLAGS = Flags()


def rand_case(rng: random.Random) -> Tuple[str, bool, int, bytes, Dict[int, int], Flags]:
    """One fuzz case: (statement, prefetch?, F823, argument buffer, word overrides, flags).
    The overrides cover the F0 / G3.. choice variables, F1118 (SELA prompt) and the
    History_Snapshot gate words F929 / F1102 (0 = gate open); the flags cover scan mode,
    the skip counter and the snapshot block byte."""
    from tsuki import vm_vars as vv
    text, pre = rand_statement(rng)
    f823 = rng.choice([0, 1, 5, 11, -3, 40])
    argbuf = bytes(rng.choice([0] + list(range(0x30, 0x7B))) for _ in range(0x40))
    small = {vv.VAR_F + 2 * rng.choice([0, 3, 12, 828]): rng.choice([0, 1, 2, -1]),
             vv.VAR_G + 2 * rng.choice([0, 3, 12, 50]): rng.choice([0, 1, 3])}
    if rng.random() < 0.6:
        small[vv.VAR_F + 2 * vv.SAVE_PROMPT_VAR] = rng.choice([0, 0, 1, 7])
    if rng.random() < 0.4:
        small[vv.VAR_F + 2 * vv.SNAPSHOT_GATE_VAR] = 0
    if rng.random() < 0.5:
        small[vv.VAR_F + 2 * vv.SNAPSHOT_MODE_VAR] = rng.choice([0, 0, 1])
    flags = Flags(scan=rng.random() < 0.15, skip=1 if rng.random() < 0.1 else 0,
                  block=1 if rng.random() < 0.1 else 0)
    return text, pre, f823, argbuf, small, flags


def _model_run(emu: VarEmu, stmt: bytes, fill: Optional[Callable[[int], int]], init: Dict[int, int],
               argbuf: bytes, prefetch: bool, flags: Flags = NO_FLAGS,
               choice0: Optional[Tuple[int, int]] = None) -> Tuple["vm_vars.Effect", "vm_vars.VarState"]:
    """tsuki.vm_vars on ``stmt`` with the memory, argument buffer, flags and choice
    globals a VarEmu run with the same arguments starts from (eboot image values: choice
    id 0, kind / index 0 unless ``choice0``, mode 0, no SELR lines)."""
    from tsuki import vm_vars as vv
    mem = vv.VarMemory(lambda va: emu.initial_word(va, fill, prefetch, init))
    st = vv.VarState(mem=mem, argbuf=bytearray(argbuf), choice_id=0, scan_mode=flags.scan,
                     skip_counter=flags.skip, snapshot_block=flags.block)
    if choice0 is not None:
        st.choice_kind, st.choice_index = choice0
    eff = vv.execute(stmt, stmt.index(b"_") + 1, st, prefetch)
    return eff, st


def compare_one(emu: VarEmu, text: str, prefetch: bool, f823: int, argbuf: bytes,
                small: Optional[Dict[int, int]] = None, flags: Flags = NO_FLAGS,
                choice0: Optional[Tuple[int, int]] = None) -> List[str]:
    """Run ``text`` through the engine (emulated) and tsuki.vm_vars with the same memory
    (pattern fill, F823 = f823, ``small`` overrides), argument buffer, flags (scan mode,
    skip counter, snapshot block byte) and, with ``choice0``, a preset choice kind / index.
    Returns the list of differences; empty means identical: every variable read and write
    in program order, the label, the argument buffer, the choice globals (kind, index, id,
    mode, line count and values; the locked flags for SELR), whether ScriptStep yielded,
    whether History_Snapshot passed its gate, whether the choice UI was set up and the
    argument of the flowchart call VA 0xB8620."""
    from tsuki import vm_vars as vv
    stmt = text.encode("latin-1") + b"_DMMY();"
    init = {vv.VAR_F + 2 * vv.MSG_INDEX_VAR: f823}
    init.update(small or {})
    g = flags.globals_()
    if choice0 is not None:
        g[CHOICE_KIND] = bytes([choice0[0]])
        g[CHOICE_INDEX] = struct.pack("<H", choice0[1] & 0xFFFF)
    r = emu.run(stmt, fill=pattern, init=init, argbuf=argbuf, prefetch=prefetch, globals_=g)
    eff, st = _model_run(emu, stmt, pattern, init, argbuf, prefetch, flags, choice0)
    out: List[str] = []
    if r.err:
        out.append(f"emulation error {r.err}")
    if r.end is None:
        out.append("emulation did not reach the next statement, a yield or a label lookup")
    if r.log != st.mem.log:
        out.append(f"log emu={[(k, vv.address_name(a), v) for k, a, v in r.log]} "
                   f"model={[(k, vv.address_name(a), v) for k, a, v in st.mem.log]}")
    elab = r.labels[0] if r.labels else None
    if elab != eff.goto:
        out.append(f"label emu={elab!r} model={eff.goto!r}")
    n = 32
    if r.argbuf[:n] != bytes(st.argbuf[:n]):
        out.append(f"argbuf emu={r.argbuf[:16]!r} model={bytes(st.argbuf[:16])!r}")
    got = (st.choice_kind, st.choice_index & 0xFFFF, st.choice_id & 0xFFFF, st.choice_mode,
           st.choice_count, st.choice_values)
    emu_c = (r.choice_kind, r.choice_index, r.choice_id, r.choice_mode, r.choice_count, r.choice_values)
    if emu_c != got:
        out.append(f"choice globals (kind, index, id, mode, count, values) emu={emu_c} model={got}")
    if text.startswith("_SELR") and r.choice_locked != [int(x) for x in st.choice_locked]:
        out.append(f"SELR locked emu={r.choice_locked} model={st.choice_locked}")
    if not prefetch and (r.end == MAIN_YIELD) != eff.yielded:
        out.append(f"yield emu end={r.end and hex(r.end)} model yielded={eff.yielded}")
    if (SNAPSHOT_BODY in r.hits) != eff.snapshot:
        out.append(f"history snapshot emu={SNAPSHOT_BODY in r.hits} model={eff.snapshot}")
    if (CHOICE_UI in r.hits) != eff.choice_ui:
        out.append(f"choice UI emu={CHOICE_UI in r.hits} model={eff.choice_ui}")
    cp = [vv._s16(rdi) for a, rdi in r.hit_rdi if a == CHOICE_POINT]
    if cp != ([eff.choice_point] if eff.choice_point is not None else []):
        out.append(f"choice point call emu={cp} model={eff.choice_point}")
    return out


def model_addresses_ok(emu: VarEmu, text: str, prefetch: bool, f823: int, argbuf: bytes,
                       small: Optional[Dict[int, int]] = None, flags: Flags = NO_FLAGS) -> bool:
    """False when the model touches an address the emulation cannot log faithfully
    (outside the logged window, an engine control word, the argument buffer or a
    region the handlers use for other state)."""
    from tsuki import vm_vars as vv
    stmt = text.encode("latin-1") + b"_DMMY();"
    init = {vv.VAR_F + 2 * vv.MSG_INDEX_VAR: f823}
    init.update(small or {})
    _, st = _model_run(emu, stmt, pattern, init, argbuf, prefetch, flags)
    for _k, a, _v in st.mem.log:
        if not (LOG_LO <= a < LOG_HI) or a in CONTROL or ARGBUF - 2 <= a < ARGBUF + 0x40:
            return False
    return True


@dataclass
class FuzzStats:
    run: int = 0
    skipped: int = 0                    # statements the address filter rejected
    with_write: int = 0
    with_read: int = 0
    jumps: int = 0
    per_command: Counter[str] = field(default_factory=collections.Counter)
    ends: Counter[str] = field(default_factory=collections.Counter)
    mismatches: List[Tuple[str, bool, int, List[str]]] = field(default_factory=list)
    seconds: float = 0.0
    flagged: Counter[str] = field(default_factory=collections.Counter)   # runs per flag set
    events: Counter[str] = field(default_factory=collections.Counter)    # yield / snapshot / UI / SELA prompt


def fuzz(emu: VarEmu, n: int = 1000, seed: int = 20261025) -> FuzzStats:
    """``n`` random cases (:func:`rand_case`) through the engine and tsuki.vm_vars
    (tests/test_vm_vars.py::test_fuzzed_statements_match_engine runs the default seed)."""
    rng = random.Random(seed)
    st = FuzzStats()
    t0 = time.time()
    while st.run < n:
        text, pre, f823, argbuf, small, flags = rand_case(rng)
        if not model_addresses_ok(emu, text, pre, f823, argbuf, small, flags):
            st.skipped += 1
            continue
        d = compare_one(emu, text, pre, f823, argbuf, small, flags)
        r = emu.r
        st.run += 1
        st.per_command[text[1:5] + (".pre" if pre else "")] += 1
        st.with_write += any(k == "w" for k, _a, _v in r.log)
        st.with_read += any(k == "r" for k, _a, _v in r.log)
        st.jumps += bool(r.labels)
        st.ends[hex(r.end or 0)] += 1
        for name, on in (("scan", flags.scan), ("skip", flags.skip), ("block", flags.block)):
            if on:
                st.flagged[name] += 1
        for name, on in (("yield", r.end == MAIN_YIELD and not pre), ("snapshot", SNAPSHOT_BODY in r.hits),
                         ("choice_ui", CHOICE_UI in r.hits),
                         ("choice_ui." + text[1:5], CHOICE_UI in r.hits),
                         ("choice_point", CHOICE_POINT in r.hits)):
            if on:
                st.events[name] += 1
        if d:
            st.mismatches.append((text, pre, f823, d))
    st.seconds = time.time() - t0
    return st


# ---------------------------------------------------------------------------
# emulation-backed operand table (every form in every reader)
# ---------------------------------------------------------------------------

CHOICE_KIND, CHOICE_INDEX = 0x1750648, 0x175064A
TABLE_F823 = 5
TABLE_FORMS = ["F12", "F0012", "F1123", "F1199", "F1200", "F2599", "F2600", "F-3", "F32767", "F32768",
               "F65535", "F70000", "F1+2", "F1a2", "F", "G12", "G070", "G1999", "G2000", "G-1",
               "C847", "C859", "C0", "C-5", "Z12", "Z1399", "_D000", "_D012", "_X012", "_000",
               "_D599", "_D600", "_D650", "_D700", "_D701", "_D1000", "D12", "X12", "f12", "g12",
               "c12", "z12", "12", "-12", "`001:F12", "`001:12", " F12", "(empty)"]
# reader -> (statement template, kind): w = target written, r = value read, g = GOTS
# selector, c = choice target, p* = the same in Script_Prefetch
TABLE_READERS: Dict[str, Tuple[str, str]] = {
    "WKST target": ("_WKST({x},eq,7);", "w"),
    "WKST value": ("_WKST(F5,eq,{x});", "r"),
    "WKAD target": ("_WKAD({x},7);", "w"),
    "WKAD value": ("_WKAD(F5,{x});", "r"),
    "WKOR target": ("_WKOR({x},6);", "w"),
    "WKOR value": ("_WKOR(F5,{x});", "r"),
    "WKOF target": ("_WKOF({x},6);", "w"),
    "WKBO target": ("_WKBO({x},6);", "w"),
    "WKBO value": ("_WKBO(F5,{x});", "r"),
    "IF__ lhs": ("_IF__({x},eq,0,LBL);", "r"),
    "IF__ rhs": ("_IF__(F5,eq,{x},LBL);", "r"),
    "GOTS selector": ("_GOTS({x},L0,L1);", "g"),
    "SELD target": ("_SELD({x},);", "c"),
    "SELA target": ("_SELA({x},);", "c"),
    "SELS target": ("_SELS({x},);", "c"),
    "prefetch WKST target": ("_WKST({x},eq,7);", "pw"),
    "prefetch WKAD target": ("_WKAD({x},7);", "pw"),
    "prefetch WKST value": ("_WKST(F5,eq,{x});", "pr"),
    "prefetch IF__ lhs": ("_IF__({x},eq,0,LBL);", "pr"),
}


def _cell(log: List[Tuple[str, int, int]], kind: str) -> str:
    from tsuki import vm_vars as vv
    if kind.endswith("w"):
        ws = [a for k, a, _v in log if k == "w"]
        return vv.address_name(ws[-1]) if ws else "-"
    rs = [a for k, a, _v in log if k == "r" and a != vv.VAR_F + 10]      # F5 is the fixed target
    if rs:
        return vv.address_name(rs[0])
    return "-" if kind == "g" else "lit"


def _choice_cell(kind: int, index: int) -> str:
    from tsuki import vm_vars as vv
    if kind == 0x46:
        return f"F[{vv._s16(index)}]"
    if kind == 0x47:
        return f"none (G{vv._s16(index)})"
    return "-"


def operand_table(emu: VarEmu) -> Tuple[Dict[str, Dict[str, str]], List[Tuple[str, str, str, str]]]:
    """(rows reader -> form -> engine cell, mismatches (reader, form, engine, model)).
    Cell: address written / read ('F[12]', 'Z[0]', hex outside the arrays), 'lit N' for a
    literal value, '-' for no effect, 'none (G..)' for a choice target the engine records
    with kind 'G' (never stored)."""
    from tsuki import vm_vars as vv
    rows: Dict[str, Dict[str, str]] = {}
    bad: List[Tuple[str, str, str, str]] = []
    f823_va = vv.VAR_F + 2 * vv.MSG_INDEX_VAR
    for rname, (tpl, kind) in TABLE_READERS.items():
        row: Dict[str, str] = {}
        for form in TABLE_FORMS:
            x = "" if form == "(empty)" else form
            stmt = tpl.format(x=x).encode("latin-1") + b"_DMMY();"
            init = {f823_va: TABLE_F823}
            pre = kind.startswith("p")
            if kind == "c":
                # through ScriptStep's own SELD / SELA / SELS handler; F1118 = 0 so that
                # _SELA reaches its dialog helper
                init[vv.VAR_F + 2 * vv.SAVE_PROMPT_VAR] = 0
                r = emu.run(stmt, fill=pattern, init=init,
                            globals_={CHOICE_KIND: b"\x00", CHOICE_INDEX: b"\x00\x00"})
                _eff, st = _model_run(emu, stmt, pattern, init, bytes(0x40), False, choice0=(0, 0))
                e = _choice_cell(r.choice_kind, r.choice_index)
                m = _choice_cell(st.choice_kind, st.choice_index)
            else:
                r = emu.run(stmt, fill=pattern, init=init, prefetch=pre)
                _eff, st = _model_run(emu, stmt, pattern, init, bytes(0x40), pre)
                e = _cell(r.log, kind)
                m = _cell(st.mem.log, kind)                       # type: ignore[attr-defined]
                lit = vv.parse_operand(x, vv.Role.VALUE).index
                e = f"lit {lit}" if e == "lit" else e
                m = f"lit {lit}" if m == "lit" else m
            if r.err:
                e = "ERR " + r.err
            if e != m:
                bad.append((rname, form, e, m))
            row[form] = e
        rows[rname] = row
    return rows, bad


# ---------------------------------------------------------------------------
# first-byte sweep: every possible first byte of every operand position
# ---------------------------------------------------------------------------

# reader -> (template with {b} = the swept byte, interpreters, F index -> value set before the
# run).  The operand is <byte> + a fixed tail, so the byte decides the namespace; for '_'
# operands byte 1 is swept as well.  F5 (the fixed target) is pattern-filled unless set.
_BOTH = (False, True)
_MAIN = (False,)
SWEEP_READERS: Dict[str, Tuple[str, Tuple[bool, ...], Dict[int, int]]] = {
    "WKST target": ("_WKST({b}12,eq,7);", _BOTH, {}),
    "WKST operator": ("_WKST(F5,{b}q,3);", _BOTH, {}),
    "WKST value": ("_WKST(F5,eq,{b}12);", _BOTH, {}),
    "WKAD target": ("_WKAD({b}12,7);", _BOTH, {}),
    "WKAD value": ("_WKAD(F5,{b}12);", _BOTH, {}),
    "WKOR target": ("_WKOR({b}12,6);", _BOTH, {}),
    "WKOR value": ("_WKOR(F5,{b}12);", _BOTH, {}),
    "WKOF target": ("_WKOF({b}12,6);", _BOTH, {}),
    "WKOF value": ("_WKOF(F5,{b}12);", _BOTH, {}),
    "WKBO target": ("_WKBO({b}12,6);", _MAIN, {}),
    "WKBO value": ("_WKBO(F5,{b}3);", _MAIN, {}),
    "IF__ lhs": ("_IF__({b}12,eq,12,LBL);", _BOTH, {}),
    "IF__ operator, second byte 'e', F5 = 12": ("_IF__(F5,{b}e,12,LBL);", _BOTH, {5: 12}),
    "IF__ operator, second byte 'x', F5 = 13": ("_IF__(F5,{b}x,12,LBL);", _BOTH, {5: 13}),
    "IF__ rhs": ("_IF__(F5,eq,{b}12,LBL);", _BOTH, {}),
    "_ operand byte 1 (WKST target)": ("_WKST(_{b}012,eq,7);", _BOTH, {}),
    "_ operand byte 1 (WKST value)": ("_WKST(F5,eq,_{b}012);", _BOTH, {}),
    "GOTS selector byte 0, F0 = 1": ("_GOTS({b}D000,L0,L1);", _MAIN, {0: 1}),
    "GOTS selector byte 1, F0 = 1": ("_GOTS(_{b}000,L0,L1);", _MAIN, {0: 1}),
    "SELD target byte 0": ("_SELD({b}D000,3);", _MAIN, {}),
    "SELD target byte 1": ("_SELD(_{b}000,3);", _MAIN, {}),
}
SWEEP_BYTES = [b for b in range(256) if b not in (0x29, 0x2C, 0x3B)]   # ')' ',' ';' end the operand
SWEEP_F823 = 5


def _sweep_class(r: EmuResult, reader: str, text: str) -> str:
    """Engine-side class of one sweep run: the variables touched (without the fixed F5 and
    F823), the written value for WKST's operator slot, the label (as 'argument k' when it is
    the text of argument k), the choice kind."""
    from tsuki import vm_vars as vv
    skip = {vv.VAR_F + 10, vv.VAR_F + 2 * vv.MSG_INDEX_VAR}
    rd = sorted({vv.address_name(a) for k, a, _v in r.log if k == "r" and a not in skip})
    wr = sorted({vv.address_name(a) for k, a, _v in r.log if k == "w"})
    parts = []
    if rd:
        parts.append("reads " + ",".join(rd))
    if wr:
        parts.append("writes " + ",".join(wr))
    if reader.startswith("WKST operator"):
        parts.append("value " + ",".join(str(v) for k, _a, v in r.log if k == "w"))
    if r.labels:
        b = text.encode("latin-1")
        args = vv.arg_texts(b, b.index(b"(") + 1)
        lab = r.labels[0]
        parts.append(f"goto argument {args.index(lab)}" if lab in args else f"goto {lab!r}")
    if reader.startswith("SELD"):
        parts.append("choice kind " + (repr(chr(r.choice_kind)) if 0x20 < r.choice_kind < 0x7F
                                       else f"{r.choice_kind:#x} (unchanged)"))
    return "; ".join(parts) or "no effect"


def _byte_text(b: int) -> str:
    return chr(b) if 0x21 <= b < 0x7F and b not in (0x7C, 0x60) else f"<{b:02X}>"


def first_byte_sweep(emu: VarEmu, prefetch_bytes: Optional[List[int]] = None
                     ) -> Tuple[Dict[str, Dict[str, List[int]]], List[Tuple[str, bool, int, List[str]]], int]:
    """Every byte 0x00..0xFF (except the terminators) as the first byte of every operand
    position, in ScriptStep and (where it has the command) Script_Prefetch; engine and
    tsuki.vm_vars compared with :func:`compare_one`.  ``prefetch_bytes`` limits the
    Script_Prefetch runs to those bytes (the tests use it to stay fast).  Returns (reader ->
    class -> bytes, mismatches (reader, prefetch, byte, differences), runs)."""
    groups: Dict[str, Dict[str, List[int]]] = {}
    bad: List[Tuple[str, bool, int, List[str]]] = []
    runs = 0
    argbuf = bytes(range(0x30, 0x70))
    from tsuki import vm_vars as vv
    for reader, (tpl, interps, fset) in SWEEP_READERS.items():
        small = {vv.VAR_F + 2 * i: v for i, v in fset.items()}
        for pre in interps:
            key = ("prefetch " if pre else "") + reader
            g: Dict[str, List[int]] = collections.defaultdict(list)
            for b in (prefetch_bytes if pre and prefetch_bytes is not None else SWEEP_BYTES):
                text = tpl.replace("{b}", chr(b))
                d = compare_one(emu, text, pre, SWEEP_F823, argbuf, small)
                runs += 1
                g[_sweep_class(emu.r, reader, text)].append(b)
                if d:
                    bad.append((key, pre, b, d))
            groups[key] = dict(g)
    return groups, bad, runs


def choice_setter_sweep(emu: VarEmu) -> Tuple[Dict[str, Dict[str, List[int]]],
                                              List[Tuple[str, int, int, str]], int]:
    """SELA / SELS target byte 0 and byte 1 over every byte, through ScriptStep's own
    handlers (VA 0x9B1B6 / 0x9B435 and their helpers VA 0xB8400 / 0xB82E0; F1118 = 0 so
    that SELA opens its dialog), compared with tsuki.vm_vars by :func:`compare_one`
    (variables, kind, index, id, mode, count, values, yield, UI set-up).  '"' is left out
    (it opens a quoted object argument that has no closing quote here)."""
    from tsuki import vm_vars as vv
    groups: Dict[str, Dict[str, List[int]]] = {}
    bad: List[Tuple[str, int, int, str]] = []
    runs = 0
    small = {vv.VAR_F + 2 * vv.SAVE_PROMPT_VAR: 0}
    for name in ("SELA", "SELS"):
        for pos, tpl in ((0, "{b}D012"), (1, "_{b}012")):
            g: Dict[str, List[int]] = collections.defaultdict(list)
            for b in SWEEP_BYTES:
                if b == 0x22:
                    continue
                text = f"_{name}({tpl.replace('{b}', chr(b))},);"
                d = compare_one(emu, text, False, SWEEP_F823, bytes(0x40), small, choice0=(0x11, 0x2222))
                r = emu.r
                runs += 1
                if d or r.end != MAIN_YIELD or CHOICE_UI not in r.hits:
                    bad.append((name, pos, b, f"end={r.end} ui={CHOICE_UI in r.hits} {d}"))
                cls = "kind/index unchanged" if r.choice_kind == 0x11 else \
                    f"kind {chr(r.choice_kind)} index {vv._s16(r.choice_index)}"
                g[cls].append(b)
            groups[f"{name} target byte {pos}"] = dict(g)
    return groups, bad, runs


def choice_flag_cases(emu: VarEmu) -> List[Tuple[str, Dict[str, object], List[str]]]:
    """The choice commands under every combination of the flags that steer them, through
    ScriptStep's own handlers, compared with tsuki.vm_vars (:func:`compare_one`):
    SELA / SELS / SELD x scan mode x F1118 in {0, 1}; SELR (first line) x scan mode x
    F929 in {0, 1} x F1102 in {0, 1} x snapshot block byte.  Returns (statement, case,
    differences) per run plus the engine's yield / UI / snapshot outcome in the case."""
    from tsuki import vm_vars as vv
    out: List[Tuple[str, Dict[str, object], List[str]]] = []
    for text in ("_SELA(_D000,);", "_SELS(_D000,);", "_SELD(_D005,7);", "_SELD(_D000,);"):
        for scan in (False, True):
            for f1118 in (0, 1):
                small = {vv.VAR_F + 2 * vv.SAVE_PROMPT_VAR: f1118}
                d = compare_one(emu, text, False, 3, bytes(0x40), small, Flags(scan=scan), choice0=(0x11, 0x2222))
                r = emu.r
                out.append((text, {"scan": scan, "F1118": f1118, "yield": r.end == MAIN_YIELD,
                                   "ui": CHOICE_UI in r.hits, "kind": r.choice_kind, "mode": r.choice_mode,
                                   "writes": [(vv.address_name(a), v) for k, a, v in r.log if k == "w"]}, d))
    for scan in (False, True):
        for f929 in (0, 1):
            for f1102 in (0, 1):
                for block in (0, 1):
                    small = {vv.VAR_F + 2 * vv.SNAPSHOT_GATE_VAR: f929, vv.VAR_F + 2 * vv.SNAPSHOT_MODE_VAR: f1102}
                    text = "_SELR(2,$042571,1);"
                    d = compare_one(emu, text, False, 3, bytes(0x40), small, Flags(scan=scan, block=block))
                    r = emu.r
                    out.append((text, {"scan": scan, "F929": f929, "F1102": f1102, "block": block,
                                       "snapshot": SNAPSHOT_BODY in r.hits,
                                       "reads": [vv.address_name(a) for k, a, _v in r.log if k == "r"]}, d))
    return out


def snapshot_body_writes(emu: VarEmu) -> Tuple[int, List[Tuple[str, int]], Optional[str]]:
    """First _SELR line with the History_Snapshot gate open (F929 = F1102 = 0, F823 = 3)
    and the snapshot body executed (AVX copies skipped, see the module doc): (number of
    variable reads, variable writes, error).  Shows that the snapshot only reads variables."""
    from tsuki import vm_vars as vv
    init = {vv.VAR_F + 2 * vv.MSG_INDEX_VAR: 3, vv.VAR_F + 2 * vv.SNAPSHOT_GATE_VAR: 0,
            vv.VAR_F + 2 * vv.SNAPSHOT_MODE_VAR: 0}
    r = emu.run(b"_SELR(2,$042571,1);_DMMY();", fill=pattern, init=init, snapshot_body=True)
    return (sum(1 for k, _a, _v in r.log if k == "r"),
            [(vv.address_name(a), v) for k, a, v in r.log if k == "w"], r.err)


def choice_point_writes(emu: VarEmu, points: Optional[List[int]] = None) -> Dict[int, List[str]]:
    """``_SELD(_D000,N)`` with the flowchart call VA 0xB8620 executed: the variable writes
    per N, 16-bit ('G[402]=0x200') and 8-bit ('G[142].lo|=..' style: 'G[142].lo=0x1'),
    i.e. the G[400..443] edge bits and the extra marks of N = 57, 67, 72, 73 (gallery.md
    5.3).  Variables are zero-filled (so every OR shows) except F24 = 1, which enables the
    F110 write of the extra marks."""
    from tsuki import vm_vars as vv
    out: Dict[int, List[str]] = {}
    for n in points if points is not None else list(range(-1, 86)):
        r = emu.run(f"_SELD(_D000,{n});_DMMY();".encode(), fill=lambda va: 0,
                    init={vv.VAR_F + 2 * 24: 1}, choice_point=True)
        if r.err:
            raise RuntimeError(f"SELD {n}: {r.err}")
        w = [f"{vv.address_name(a)}={v:#x}" for k, a, v in r.log if k == "w"]
        w += [f"{vv.address_name(a & ~1)}.{'hi' if a & 1 else 'lo'}={v:#x}" for a, v in r.byte_writes]
        out[n] = w
    return out


def choice_store_check(emu: VarEmu) -> List[Tuple[str, str, str]]:
    """Both result stores of the choice UI against vm_vars.choice_store: the list store
    (mode 1, values[cursor]) and the two-button store (mode 2, the cursor itself when it
    is 0 or 1).  Returns the mismatches (case, engine writes, model writes)."""
    from tsuki import vm_vars as vv
    bad: List[Tuple[str, str, str]] = []
    cases = [(0x46, 0, 1, [5, 9]), (0x47, 3, 0, [1, 2]), (0x5A, 4, 1, [3, 8]), (0x46, 0xFFFF, 0, [2]),
             (0x46, 7, 2, [4, 5, 6]), (0x46, 7, 0, [4, 5, 6]), (0x00, 7, 0, [4, 5])]
    for kind, idx, cur, vals in cases:
        for mode, two in ((vv.CHOICE_MODE_LIST, False), (vv.CHOICE_MODE_TWO, True)):
            for cursor in ((cur,) if not two else (0, 1, 2, 0xFFFF)):
                r = emu.run_choice_store(kind, idx, cursor, vals, fill=pattern, two_button=two)
                st = vv.VarState(choice_kind=kind, choice_index=idx, choice_mode=mode, choice_values=list(vals))
                st.mem = vv.VarMemory(lambda va: emu.initial_word(va, pattern))
                a = vv.choice_store(st, cursor)
                ew = [(x, v) for k, x, v in r.log if k == "w"]
                mw = st.mem.writes()
                if ew != mw or r.err or (a is None) != (not mw):
                    bad.append((f"mode {mode} kind {kind:#x} index {idx} cursor {cursor} values {vals}",
                                str(ew), str(mw)))
    return bad


# ---------------------------------------------------------------------------
# the _SELA save prompt in the retail scripts (model walk over the real scripts)
# ---------------------------------------------------------------------------

def _label_index(segs: List[bytes]) -> Dict[bytes, int]:
    """Label name -> index of its ``_ZZ`` / ``_ZY`` marker segment (script.md 2.3)."""
    out: Dict[bytes, int] = {}
    for i, s in enumerate(segs):
        m = re.match(rb"_Z[ZY][0-9a-f]{5}\(([^)]*)\);", s)
        if m and m.group(1) and m.group(1) not in out:
            out[m.group(1)] = i
    return out


def save_prompt_flow(scr: Optional[object] = None) -> List[Dict[str, object]]:
    """For every script with a ``_SELA``: run tsuki.vm_vars over the statements from the
    script start (``_IF__`` / ``_GOTS`` / ``_GOTO`` jumps followed to their labels, every
    other command skipped) until the ``_SELA``, once with F026 = 0 and once with F026 = 1;
    F1118 = 0 at the start (the chapter-end ``_WKST(F1118,eq,0)`` before ``_JUMP``).
    Returns per script: index, name, F1118 at the SELA and whether the prompt opens for
    each F026 value."""
    from tsuki import vm_vars as vv
    from tsuki.script import AllScr
    own = scr is None
    al = AllScr() if scr is None else scr
    rows: List[Dict[str, object]] = []
    try:
        for si in range(len(al)):                            # type: ignore[arg-type]
            s = al.raw(si)                                   # type: ignore[attr-defined]
            if b"_SELA(" not in s:
                continue
            segs = list(vv.segments(s))
            labels = _label_index(segs)
            row: Dict[str, object] = {"script": si, "name": al.names[si]}   # type: ignore[attr-defined]
            for f026 in (0, 1):
                st = vv.VarState()
                st.mem.poke(vv.VAR_F + 2 * 26, f026)
                i, steps, res = 0, 0, "not reached"
                while i < len(segs) and steps < 100_000:
                    steps += 1
                    seg = segs[i]
                    u = seg.find(b"_")
                    name = seg[u + 1:u + 5] if u >= 0 else b""
                    nxt = i + 1
                    if name in (b"JUMP", b"END_", b"RTM_"):
                        res = "left the script"
                        break
                    if name == b"GOTO":
                        lab = bytes(vv.arg_texts(seg, u + 6)[0])
                        nxt = labels.get(lab, len(segs))
                    elif name.decode("latin-1") in vv.HANDLED_MAIN:
                        if name == b"SELA":
                            f1118 = st.mem.peek(vv.VAR_F + 2 * vv.SAVE_PROMPT_VAR)
                            eff = vv.execute(seg, u + 1, st)
                            res = f"F1118={f1118} prompt={'opens' if eff.choice_ui else 'skipped'}"
                            break
                        eff = vv.execute(seg, u + 1, st)
                        if eff.goto is not None:
                            nxt = labels.get(eff.goto, len(segs))
                    i = nxt
                row[f"F026={f026}"] = res
            rows.append(row)
    finally:
        if own:
            al.close()                                       # type: ignore[attr-defined]
    return rows


def _write_sweep(groups: Dict[str, Dict[str, List[int]]]) -> None:
    SAMPLES.mkdir(parents=True, exist_ok=True)
    (SAMPLES / "first_byte_sweep.json").write_text(json.dumps(groups, indent=1), encoding="utf-8")
    lines = ["# First-byte sweep (engine under unicorn; F823 = 5, F5 pattern-filled)", "",
             "Generated by `python -m tsuki.misc_vm_variables_errata sweep`. Each row lists the operand",
             "bytes that give the same engine-side effect (printable bytes as characters, others as",
             "<hex>; '`' is <60>, '|' is <7C>). Every run was also compared with tsuki.vm_vars.", ""]
    for reader, g in groups.items():
        lines += [f"## {reader}", "", "| bytes | effect |", "|---|---|"]
        for cls, bs in sorted(g.items(), key=lambda kv: len(kv[1])):
            txt = "".join(_byte_text(b) for b in bs) if len(bs) <= 40 else f"{len(bs)} other bytes"
            lines.append(f"| `{txt}` | {cls} |")
        lines.append("")
    (SAMPLES / "first_byte_sweep.md").write_text("\n".join(lines), encoding="utf-8")


# ---------------------------------------------------------------------------
# history record: which F values survive a save / load and a rewind (emulated)
# ---------------------------------------------------------------------------

REC = BUF + 0x4000                     # fake history record (offsets up to +0x362D)
SNAPSHOT_LISTS = (0x7B191, 0x7B27C)    # History_Snapshot 0x7AF90: u16, s8, bool list loops
LOAD_LISTS = (0xA3293, 0xA334F)        # slot loader 0xA3050: the same three lists
REWIND_LISTS = (0x7CB99, 0x7CC7F)      # History_Restore 0x7C6D0 (rewind)


def _run_region(emu: VarEmu, start: int, end: int, regs: Dict[str, int]) -> None:
    emu.go = True
    emu.go_va, emu.stops, emu.stubs, emu.redirects = -1, (end,), {}, {}
    emu.r = EmuResult()
    for k, v in regs.items():
        emu.set_reg(k, v)
    emu.set_reg("RSP", STK_HI - 0x8000)
    emu._start(start, 200_000)
    if emu.r.err or emu.r.end != end:
        raise RuntimeError(f"region {start:#x}..{end:#x}: end {emu.r.end} err {emu.r.err}")


def emulate_history(emu: VarEmu, saved: List[int], current: Optional[List[int]] = None) -> List[int]:
    """Engine round trip of var_F (1200 words) through a history record.  ``saved`` is
    var_F when the record is taken (History_Snapshot's list loops, VA 0x7B191..0x7B27C).
    Without ``current`` the record is then loaded like a save slot: var_F and var_Z are
    zeroed (the memsets VA 0xA3163 / 0xA3176) and the loader's list loops run (VA
    0xA3293..0xA334F).  With ``current`` var_F is set to it and the rewind restore runs
    (History_Restore, VA 0x7CB99..0x7CC7F).  Returns var_F afterwards."""
    from tsuki import vm_vars as vv
    uc = emu.uc
    emu._prepare(b"", None, None, None, None)
    uc.mem_write(REC, bytes(0x3700))
    uc.mem_write(vv.VAR_F, struct.pack("<1200H", *[v & 0xFFFF for v in saved]))
    _run_region(emu, *SNAPSHOT_LISTS, {"R14": REC, "R15": 0})
    if current is None:
        uc.mem_write(vv.VAR_F, bytes(0x960))
        uc.mem_write(vv.VAR_Z, bytes(0xAF0))
        _run_region(emu, *LOAD_LISTS, {"RAX": REC, "RBX": vv.VAR_F, "R14": 0})
    else:
        uc.mem_write(vv.VAR_F, struct.pack("<1200H", *[v & 0xFFFF for v in current]))
        _run_region(emu, *REWIND_LISTS, {"R10": REC, "R12": 0, "RBX": REC})
    return list(struct.unpack("<1200H", bytes(uc.mem_read(vv.VAR_F, 0x960))))


def history_check(emu: VarEmu, trials: int = 12, seed: int = 1) -> Tuple[int, List[Tuple[str, int, int, int, int]]]:
    """Compare :func:`emulate_history` with tsuki.vm_vars.history_roundtrip /
    history_after_rewind on every F index for ``trials`` random var_F images per path
    (half of the values taken from the s8 / bool edge cases).  Returns (comparisons,
    mismatches (path, index, saved, engine, model))."""
    from tsuki import vm_vars as vv
    rng = random.Random(seed)
    edge = [0, 1, 2, 0x7F, 0x80, 0xFF, 0x100, 0x17F, 0x7FFF, 0x8000, 0xFFFF, 63]

    def img() -> List[int]:
        return [rng.choice(edge) if rng.random() < 0.5 else rng.randrange(0x10000) for _ in range(1200)]
    bad: List[Tuple[str, int, int, int, int]] = []
    n = 0
    for _ in range(trials):
        sv = img()
        got = emulate_history(emu, sv)
        for i in range(1200):
            n += 1
            want = vv.history_roundtrip(i, sv[i])
            if got[i] != want:
                bad.append(("load", i, sv[i], got[i], want))
        cur = img()
        got = emulate_history(emu, sv, cur)
        for i in range(1200):
            n += 1
            want = vv.history_after_rewind(i, sv[i], cur[i])
            if got[i] != want:
                bad.append(("rewind", i, sv[i], got[i], want))
    return n, bad


# ---------------------------------------------------------------------------
# Script_EvalCondition (VA 0x79D30): the _EIF_ expression evaluator
# ---------------------------------------------------------------------------

EVAL_CONDITION = 0x79D30
EVAL_CASES = [b"(F[12]=F[12])", b"(Z[12]=Z[12])", b"(G[12]=G[12])", b"(F[12]=1)", b"(F[12]&F[13])",
              b"(F[12]|Z[3])", b"(F[1]=G[1])", b"(F[12]!F[12])", b"(F12=F12)", b"(X[12]=1)", b"(f[12]=1)",
              b"(F[1200]=1)", b"(F[-3]=1)"]


def run_eval_condition(emu: VarEmu, text: bytes, fill: Optional[Callable[[int], int]] = pattern
                       ) -> Tuple[int, List[Tuple[str, int, int]], int, Optional[str]]:
    """Call VA 0x79D30(p = text, out) as `_EIF_` does (VA 0x96F10: p = the '(' after the
    name, out = the word 0x165420C).  Returns (u16 *out, 0xAAAA when not written; the
    variable log; the returned pointer as an offset into ``text``; error)."""
    out = BUF + 0x800
    emu._prepare(b"", fill, None, None, None)
    emu.uc.mem_write(BUF, text + b"\0")
    emu.uc.mem_write(out, b"\xAA\xAA")
    emu.go = True
    emu.go_va, emu.stops, emu.stubs, emu.redirects = -1, (RET,), {}, {}
    emu.set_reg("RDI", BUF)
    emu.set_reg("RSI", out)
    emu._start(EVAL_CONDITION, 100_000)
    r = emu._finish()
    return emu.u16(out), r.log, emu.reg("RAX") - BUF, r.err


# ---------------------------------------------------------------------------
# erratum (b): Script_Prefetch's V case
# ---------------------------------------------------------------------------

PREFETCH_V_HANDLER = 0x9BD2B          # VPLY / VPL2 handler in Script_Prefetch (calls 0x76C10)
PREFETCH_SKIP = 0x9FF30


def prefetch_v_names(emu: VarEmu, names: List[str]) -> Dict[str, str]:
    """Run Script_Prefetch on ``_<name>(0,0,0,0);`` for each name: 'handler' when the
    VPLY/VPL2 handler VA 0x9BD2B executes, 'skip' when the scan reaches the skip label
    0x9FF30 first, else the end address reached."""
    out: Dict[str, str] = {}
    old = emu.watch
    emu.watch = set(old) | {PREFETCH_V_HANDLER}
    try:
        for n in names:
            r = emu.run(f"_{n}(0,0,0,0);".encode("latin-1") + b"_DMMY();", prefetch=True, count=60_000)
            if PREFETCH_V_HANDLER in r.hits:
                out[n] = "handler"
            elif r.end == PREFETCH_SKIP:
                out[n] = "skip"
            else:
                out[n] = f"end {r.end and hex(r.end)} err {r.err}"
    finally:
        emu.watch = old
    return out


# ---------------------------------------------------------------------------
# erratum (c): save image layout from the code
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class SaveLayout:
    image_size: int          # memset at 0xEA5D9 (mov edx, imm)
    slot_base: int           # SaveImage_WriteBlock 0xEAD1D: lea r15, [rax + r15 + base]
    slot_stride: int         # 0xEAD16: imul rax, rax, stride
    slot_size: int           # SaveImage_Commit 0x6FEF7: mov edx, size
    slot_count: int          # 0x6FF0B: cmp r15, count

    @property
    def slots_end(self) -> int:
        """First byte after the last slot's stride (= start of the unused tail)."""
        return self.slot_base + self.slot_count * self.slot_stride

    @property
    def last_record_end(self) -> int:
        return self.slot_base + (self.slot_count - 1) * self.slot_stride + self.slot_size

    @property
    def unused(self) -> Tuple[int, int]:
        return self.slots_end, self.image_size - self.slots_end


def _insns(eb: object, start: int, end: int) -> List[Tuple[int, int, str, str]]:
    from eboot.disasm import disasm_range
    return [(i.addr, i.size, i.mnem, i.ops) for i in disasm_range(eb, start, end)]  # type: ignore[arg-type]


def _imm(ops: str) -> int:
    m = re.search(r"(0x[0-9a-f]+|\d+)\]?$", ops)
    if m is None:
        raise ValueError(f"no immediate in {ops!r}")
    return int(m.group(1), 0)


def save_image_layout(eboot: Union[str, Path]) -> SaveLayout:
    """Decode the save image layout from the instructions that define it."""
    from eboot.selfelf import Eboot
    eb = Eboot.open(Path(eboot))
    try:
        def find(start: int, end: int, mnem: str, pat: str) -> int:
            for _a, _s, mn, ops in _insns(eb, start, end):
                if mn == mnem and re.search(pat, ops):
                    return _imm(ops)
            raise ValueError(f"{mnem} {pat} not found in {start:#x}..{end:#x}")
        size = find(0xEA5D0, 0xEA5EC, "mov", r"^edx, ")
        stride = find(0xEAD0C, 0xEAD25, "imul", r"^rax, rax, ")
        base = find(0xEAD0C, 0xEAD25, "lea", r"^r15, \[rax \+ r15 \+ ")
        ssize = find(0x6FEF0, 0x6FF14, "mov", r"^edx, ")
        count = find(0x6FEF0, 0x6FF14, "cmp", r"^r15, ")
    finally:
        eb.close()
    return SaveLayout(size, base, stride, ssize, count)


def slot_load_clears(eboot: Union[str, Path]) -> List[Tuple[int, int]]:
    """(destination VA, byte count) of the memsets the slot loader (VA 0xA3050) does before
    restoring the history lists: var_F (0x960) and var_Z (0xAF0); var_G is not touched."""
    from eboot.selfelf import Eboot
    eb = Eboot.open(Path(eboot))
    try:
        ins = _insns(eb, 0xA311E, 0xA317B)
    finally:
        eb.close()
    out: List[Tuple[int, int]] = []
    dst: Optional[int] = None
    n: Optional[int] = None
    for a, size, mn, ops in ins:
        m = re.match(r"^(rbx|rdi), \[rip \+ (0x[0-9a-f]+)\]$", ops)
        if mn == "lea" and m:
            dst = a + size + int(m.group(2), 16)
        if mn == "mov" and ops.startswith("edx, "):
            n = _imm(ops)
        if mn == "call" and ops == "0x1734f0" and dst is not None and n is not None:   # memset
            out.append((dst, n))
            n = None
    return out


# ---------------------------------------------------------------------------
# history record index lists (eboot rodata)
# ---------------------------------------------------------------------------

def read_history_lists(eboot: Union[str, Path]) -> Tuple[List[int], List[int], List[int]]:
    """(u16 list VA 0x2160E0, 39 entries; bool list VA 0x216580, 43 entries; s8 list
    VA 0x2165F0, up to 300 entries ended by a negative value), as the snapshot (VA 0x7B199,
    0x7B1CF, 0x7B20B) and the slot loader (VA 0xA3293, 0xA32BC, 0xA32FB) walk them.  Text
    VA = file offset - 0x2280."""
    d = Path(eboot).read_bytes()

    def rd(va: int, n: int) -> List[int]:
        return list(struct.unpack(f"<{n}h", d[va + TEXT_OFF:va + TEXT_OFF + 2 * n]))
    u16 = rd(0x2160E0, 39)
    bl = rd(0x216580, 43)
    s8 = rd(0x2165F0, 300)
    end = next((k for k, v in enumerate(s8) if v < 0), len(s8))
    return u16, bl, s8[:end]


# ---------------------------------------------------------------------------
# corpus census (all 549 scripts)
# ---------------------------------------------------------------------------

@functools.lru_cache(maxsize=1 << 14)
def _shape(t: str) -> str:
    return re.sub(r"\d", "d", t) if t else "(empty)"


def corpus_census(scr: Optional[object] = None) -> Dict[str, object]:
    """Operand forms and decoded kinds per command position over all scripts, C-operand
    uses by the F823 value at the point of use (linear scan), F/G index classification and
    the literal values stored per F index.  ``scr``: an open tsuki.script.AllScr."""
    from tsuki import vm_vars as vv
    from tsuki.script import AllScr
    R = vv.Role
    roles: Dict[str, Dict[int, str]] = {
        "WKST": {0: R.WK_TARGET, 1: "op", 2: R.VALUE},
        "WKAD": {0: R.WK_TARGET, 1: R.VALUE},
        "WKOR": {0: R.OR_TARGET, 1: R.VALUE},
        "WKOF": {0: R.OR_TARGET, 1: R.VALUE},
        "WKBO": {0: R.BO_TARGET},
        "IF__": {0: R.VALUE, 1: "op", 2: R.VALUE, 3: "label"},
        "GOTS": {0: R.GOTS},
        "SELD": {0: R.CHOICE, 1: "id"},
        "SELA": {0: R.CHOICE},
        "SELS": {0: R.CHOICE},
        "SELR": {0: "choice-value"},
        "DMMY": {},
    }
    forms: Dict[str, Counter[str]] = collections.defaultdict(collections.Counter)
    kinds: Dict[str, Counter[str]] = collections.defaultdict(collections.Counter)
    ops: Dict[str, Counter[str]] = collections.defaultdict(collections.Counter)
    f_uses: Dict[int, Counter[str]] = collections.defaultdict(collections.Counter)
    g_uses: Dict[int, Counter[str]] = collections.defaultdict(collections.Counter)
    c_uses: Dict[int, Counter[int]] = collections.defaultdict(collections.Counter)
    c_by_cmd: Counter[str] = collections.Counter()
    f_values: Dict[int, Counter[int]] = collections.defaultdict(collections.Counter)
    f_incr: Dict[int, Counter[int]] = collections.defaultdict(collections.Counter)
    stmts: Counter[str] = collections.Counter()
    nseg = nseg_split = trailing = nscripts = 0
    max_f823 = 0
    # per distinct segment: (name, argument texts, has a C operand, F823 effect 0 none /
    # 1 set / 2 add, effect value), or None for statements without variable operands
    Info = Optional[Tuple[str, List[str], bool, int, int]]
    info_cache: Dict[bytes, Info] = {}
    occ: Counter[Tuple[bytes, int]] = collections.Counter()

    def _analyse(seg: bytes) -> Info:
        u = seg.find(b"_")
        if u < 0 or len(seg) < u + 5 or not (0x41 <= seg[u + 1] <= 0x5A):
            return None
        name = seg[u + 1:u + 5].decode("latin-1")
        if name not in roles:
            return None
        args = [a.decode("latin-1") for a in vv.arg_texts(seg, u + 6)]
        if seg[u + 6:u + 7] in (b")", b";"):
            args = []
        has_c = any(roles[name].get(k) == R.WK_TARGET and vv.parse_operand(t, R.WK_TARGET).kind == "C"
                    for k, t in enumerate(args))
        mode = val = 0
        if name in ("WKST", "WKAD") and len(args) >= (3 if name == "WKST" else 2):
            o = vv.parse_operand(args[0], R.WK_TARGET)
            v = vv.parse_operand(args[2 if name == "WKST" else 1], R.VALUE)
            if o.kind == "F" and o.index == vv.MSG_INDEX_VAR and v.kind == "lit":
                mode, val = (1 if name == "WKST" else 2), v.index
        return name, args, has_c, mode, val
    own = scr is None
    al = AllScr() if scr is None else scr
    try:
        for si in range(len(al)):                            # type: ignore[arg-type]
            s = al.raw(si)                                   # type: ignore[attr-defined]
            nscripts += 1
            # the count of engine.md 5.3 / script_cmd_counts.json: s.split(';') pieces
            # (0x300 chunks), which adds the empty piece after each script's last ';'
            for piece in s.split(b";"):
                nseg_split += max(1, -(-len(piece) // 0x300))
            trailing += s.endswith(b";")
            f823 = 0
            for seg in vv.segments(s):
                nseg += 1
                if seg in info_cache:
                    info = info_cache[seg]
                else:
                    info = info_cache[seg] = _analyse(seg)
                if info is None:
                    continue
                occ[(seg, f823 if info[2] else 0)] += 1           # F823 matters for C operands only
                if info[3] == 1:
                    f823 = info[4]
                elif info[3] == 2:
                    f823 += info[4]
                    max_f823 = max(max_f823, f823)
        # second pass: each distinct (statement, F823) once, weighted by its count
        for (seg, f823), cnt in occ.items():
            info = info_cache[seg]
            assert info is not None
            name, args = info[0], info[1]
            stmts[name] += cnt
            for k, t in enumerate(args):
                key = f"{name}.{k}"
                role = roles[name].get(k)
                if name == "DMMY":
                    forms[key][_shape(t)] += cnt
                    continue
                if role is None:
                    continue
                if role in ("op", "label", "id", "choice-value"):
                    ops[key][t if role in ("op", "choice-value") else _shape(t)] += cnt
                    continue
                forms[key][_shape(t)] += cnt
                o = vv.parse_operand(t, role)
                kinds[key][o.kind] += cnt
                if o.kind == "F":
                    f_uses[o.index][key] += cnt
                elif o.kind == "G":
                    g_uses[o.index][key] += cnt
                elif o.kind == "C":
                    c_uses[o.index][f823] += cnt
                    c_by_cmd[f"{name}.C{o.index}"] += cnt
                    f_uses[o.index + f823][key + "(C)"] += cnt
            if name in ("WKST", "WKAD") and len(args) >= (3 if name == "WKST" else 2):
                o = vv.parse_operand(args[0], R.WK_TARGET)
                v = vv.parse_operand(args[2 if name == "WKST" else 1], R.VALUE)
                if o.kind in ("F", "C") and v.kind == "lit":
                    idx = o.index + (f823 if o.kind == "C" else 0)
                    (f_values if name == "WKST" else f_incr)[idx][v.index] += cnt
    finally:
        if own:
            al.close()                                       # type: ignore[attr-defined]
    f_table: Dict[str, Dict[str, object]] = {}
    lossy: List[Tuple[int, int]] = []
    for i in sorted(f_uses):
        kind = vv.history_storage(i)
        for val in f_values[i]:
            if vv.history_roundtrip(i, val) != val & 0xFFFF:
                lossy.append((i, val))
        f_table[str(i)] = {"storage": kind or "transient", "uses": dict(f_uses[i]),
                           "literal_values": sorted(f_values[i]), "wkad_increments": sorted(f_incr[i])}
    g_table = {str(i): {"storage": "system-data" if 0 <= i < 500 else "transient", "uses": dict(g_uses[i])}
               for i in sorted(g_uses)}
    return {
        "scripts": nscripts, "segments": nseg, "segments_split_count": nseg_split,
        "scripts_ending_with_semicolon": trailing, "statements": dict(stmts),
        "forms": {k: dict(v.most_common()) for k, v in sorted(forms.items())},
        "kinds": {k: dict(v) for k, v in sorted(kinds.items())},
        "ops": {k: dict(v.most_common()) for k, v in sorted(ops.items())},
        "c_uses_by_f823": {str(b): {str(k): n for k, n in sorted(v.items())} for b, v in sorted(c_uses.items())},
        "c_uses_by_command": dict(sorted(c_by_cmd.items())),
        "max_f823_linear": max_f823,
        "f_indices": f_table, "g_indices": g_table,
        "f_storage_counts": dict(collections.Counter(str(v["storage"]) for v in f_table.values())),
        "literal_values_not_surviving_a_save": lossy,
    }


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

SAMPLES = Path(__file__).resolve().parents[2] / "samples" / "misc_vm_variables_errata"


def _eboot() -> Path:
    from eboot import eboot_path
    p = eboot_path()
    if p is None:
        raise SystemExit("eboot.bin not found (set TSUKI_GAME_DIR)")
    return p


def _write_table(rows: Dict[str, Dict[str, str]]) -> None:
    SAMPLES.mkdir(parents=True, exist_ok=True)
    (SAMPLES / "operand_table.json").write_text(json.dumps({"f823": TABLE_F823, "rows": rows}, indent=1),
                                                encoding="utf-8")
    cols = list(TABLE_READERS)
    lines = ["| form | " + " | ".join(cols) + " |", "|---|" + "---|" * len(cols)]
    for form in TABLE_FORMS:
        lines.append(f"| `{form}` | " + " | ".join(rows[c][form] for c in cols) + " |")
    (SAMPLES / "operand_table.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def _choice_report(emu: VarEmu) -> None:
    """CLI ``choice``: the choice commands under their flags, the snapshot body, the
    flowchart call, both result stores and the retail save-prompt flow; writes
    samples/misc_vm_variables_errata/choice_commands.json / .md."""
    cases = choice_flag_cases(emu)
    nb, sw, serr = snapshot_body_writes(emu)
    cp = choice_point_writes(emu)
    sbad = choice_store_check(emu)
    flow = save_prompt_flow()
    bad = [c for c in cases if c[2]]
    print(f"choice flag cases: {len(cases)} runs, {len(bad)} model mismatches")
    for text, case, d in cases:
        print(f"  {text:20s} {json.dumps(case)}{'  MISMATCH ' + str(d) if d else ''}")
    print(f"SELR with the snapshot body executed: {nb} variable reads, writes {sw}, error {serr}")
    marks = {n: w for n, w in cp.items() if w}
    print(f"SELD flowchart call: N with variable writes {sorted(marks)}")
    print(f"choice result stores: {len(sbad)} mismatches", *sbad[:5])
    opened = collections.Counter((str(r["F026=0"]), str(r["F026=1"])) for r in flow)
    print(f"save prompt flow over {len(flow)} scripts with _SELA: {dict(opened)}")
    SAMPLES.mkdir(parents=True, exist_ok=True)
    doc = {"flag_cases": [{"statement": t, **c, "mismatches": d} for t, c, d in cases],
           "snapshot_body": {"reads": nb, "writes": sw, "error": serr},
           "choice_point_writes": {str(n): w for n, w in cp.items()},
           "choice_store_mismatches": sbad, "save_prompt_flow": flow}
    (SAMPLES / "choice_commands.json").write_text(json.dumps(doc, indent=1), encoding="utf-8")
    lines = ["# Choice commands under emulation", "",
             "Generated by `python -m tsuki.misc_vm_variables_errata choice` (ScriptStep's own handlers;",
             "every run also compared with tsuki.vm_vars, 'diff' = number of differences).", "",
             "| statement | case | diff |", "|---|---|---|"]
    for t, c, d in cases:
        lines.append(f"| `{t}` | {json.dumps(c)} | {len(d)} |")
    lines += ["", f"SELR first line with the snapshot body executed: {nb} variable reads, writes {sw}.", "",
              "## `_SELD(_D000,N)`: variable words written by the flowchart call VA 0xB8620", "",
              "| N | writes |", "|---|---|"]
    for n, w in cp.items():
        if w:
            lines.append(f"| {n} | {', '.join(w)} |")
    lines += ["", f"N without writes: {[n for n, w in cp.items() if not w]}", "",
              "## `_SELA` save prompt in the retail scripts (model walk from the script start)", "",
              "| script | name | F026 = 0 | F026 = 1 |", "|---|---|---|---|"]
    for r in flow:
        lines.append(f"| {r['script']} | {r['name']} | {r['F026=0']} | {r['F026=1']} |")
    (SAMPLES / "choice_commands.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def _main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m tsuki.misc_vm_variables_errata",
                                 description=(__doc__ or "").split("\n")[0])
    ap.add_argument("what", choices=["census", "table", "fuzz", "layout", "vcase", "sweep", "history",
                                     "eif", "choice", "all"])
    ap.add_argument("n", nargs="?", type=int, default=1000, help="fuzz: statements")
    ap.add_argument("seed", nargs="?", type=int, default=20261025, help="fuzz: RNG seed")
    a = ap.parse_args(argv)
    todo = ["layout", "vcase", "table", "sweep", "history", "eif", "choice", "fuzz", "census"] \
        if a.what == "all" else [a.what]
    emu: Optional[VarEmu] = None
    for w in todo:
        if w in ("table", "fuzz", "vcase", "sweep", "history", "eif", "choice") and emu is None:
            emu = VarEmu(_eboot())
        if w == "choice":
            assert emu is not None
            _choice_report(emu)
        if w == "layout":
            lay = save_image_layout(_eboot())
            s, n = lay.unused
            print(f"save image {lay.image_size:#x}: slots {lay.slot_count} x {lay.slot_stride:#x} from "
                  f"{lay.slot_base:#x} (record {lay.slot_size:#x}); last record ends {lay.last_record_end:#x}; "
                  f"unused {s:#x}..{lay.image_size:#x} ({n:#x} bytes)")
            print("slot load clears", [(hex(x), hex(m)) for x, m in slot_load_clears(_eboot())])
        elif w == "vcase":
            assert emu is not None
            names = ["VPLY", "VPL2", "VCWT", "VCLR", "VAAA", "VPLZ", "VPL1", "VPLy", "V___", "VZZZ"]
            print("Script_Prefetch V case:", prefetch_v_names(emu, names))
        elif w == "table":
            assert emu is not None
            rows, bad = operand_table(emu)
            _write_table(rows)
            print(f"operand table: {len(TABLE_FORMS)} forms x {len(TABLE_READERS)} readers, "
                  f"{len(bad)} model mismatches -> {SAMPLES / 'operand_table.md'}")
            for b in bad[:10]:
                print("  MISMATCH", b)
        elif w == "sweep":
            assert emu is not None
            t0 = time.time()
            groups, sbad, runs = first_byte_sweep(emu)
            g2, sbad2, runs2 = choice_setter_sweep(emu)
            groups.update(g2)
            _write_sweep(groups)
            print(f"first-byte sweep: {runs} statement runs + {runs2} SELA/SELS runs, "
                  f"{len(sbad)} + {len(sbad2)} model mismatches, {time.time() - t0:.1f}s "
                  f"-> {SAMPLES / 'first_byte_sweep.md'}")
            for mm in [*sbad, *sbad2][:10]:
                print("  MISMATCH", mm)
        elif w == "history":
            assert emu is not None
            n, hb = history_check(emu, 12)
            print(f"history record: {n} (index, value) round trips (slot load + rewind), {len(hb)} mismatches")
            for x in hb[:10]:
                print("  MISMATCH", x)
        elif w == "eif":
            assert emu is not None
            from tsuki import vm_vars as vv
            for t in EVAL_CASES:
                out_v, elog, eend, eerr = run_eval_condition(emu, t)
                print(f"  {t.decode():16s} -> out {out_v:#06x} reads {[vv.address_name(x) for _k, x, _v in elog]} "
                      f"end +{eend} err {eerr}")
        elif w == "fuzz":
            assert emu is not None
            st = fuzz(emu, a.n, a.seed)
            print(json.dumps({"run": st.run, "skipped": st.skipped, "mismatches": len(st.mismatches),
                              "with_write": st.with_write, "with_read": st.with_read, "jumps": st.jumps,
                              "per_command": dict(sorted(st.per_command.items())), "ends": dict(st.ends),
                              "flagged": dict(st.flagged), "events": dict(sorted(st.events.items())),
                              "seconds": round(st.seconds, 1)}, indent=1))
            for m in st.mismatches[:10]:
                print("  MISMATCH", m)
        elif w == "census":
            res = corpus_census()
            SAMPLES.mkdir(parents=True, exist_ok=True)
            (SAMPLES / "operand_census.json").write_text(json.dumps(res, indent=1), encoding="utf-8")
            print(f"scripts {res['scripts']} segments {res['segments']} (split count "
                  f"{res['segments_split_count']}, scripts ending with ';' {res['scripts_ending_with_semicolon']})")
            print("statements", res["statements"])
            print("kinds", json.dumps(res["kinds"]))
            print("C uses", res["c_uses_by_command"], "max F823", res["max_f823_linear"])
            print("F indices", len(res["f_indices"]), res["f_storage_counts"],  # type: ignore[arg-type]
                  "G indices", len(res["g_indices"]),                          # type: ignore[arg-type]
                  "lossy", res["literal_values_not_surviving_a_save"])
    return 0


if __name__ == "__main__":
    sys.exit(_main())
