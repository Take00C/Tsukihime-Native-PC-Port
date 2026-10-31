"""Run the PS4 interpreter's own argument readers / dispatch on the user's
eboot.bin under the unicorn CPU emulator, to verify tsuki.script against the
engine (development aid; needs ``pip install unicorn``; nothing here is needed
to read the data).

Emulated routines (CUSA47999 v01.01 text VAs):

* 0x76690 / 0x76780 / 0x76860  inline readers (i16 / i32 / present)
* 0xD690 + 0xDA50 / 0xE310      object parser + expression getter / strict int
* 0x96615 .. 0x96709            marker id decoding (block, sub -> 0x1654344/48)
* 0x95F09 ..                    name dispatch (handler entry or no-op)

libc imports reached by these routines are executed by Python stubs at their
PLT addresses (strtol, strpbrk, strchr, memset, memcpy, memmove, operator new /
delete, __stack_chk_fail).

CLI: python -m tsuki.script_emu expr "258-18" "242.25" ...
"""
from __future__ import annotations

import re
import struct
import sys
from pathlib import Path
from typing import Callable, Optional, Union

TEXT_OFF, TEXT_LEN = 0x2280, 0x329DC8
DATA_OFF, DATA_LEN, DATA_VA, DATA_MEMSZ = 0x365FA0, 0x7D66E0, 0x600000, 0x1A85790
RELRO_OFF, RELRO_LEN, RELRO_VA = 0x32C210, 0x35ED0, 0x400000

STACK, STACK_SIZE = 0x30000000, 0x100000
BUF = 0x31000000            # statement buffer
OBJ = 0x31100000            # object parser state (0x150 bytes)
OUT = 0x31100800            # out-parameters
HEAP, HEAP_SIZE = 0x32000000, 0x1000000
RET = 0x33000000            # return trap (hlt)

PLT = {
    0x1734A0: "delete", 0x1739F0: "delete", 0x173660: "delete",
    0x1734B0: "stack_chk_fail",
    0x1734C0: "new", 0x1737E0: "new", 0x173680: "new",
    0x1734D0: "memmove", 0x173650: "memmove",
    0x1734F0: "memset",
    0x1735C0: "strchr", 0x1735D0: "strpbrk", 0x1735E0: "strtol",
    0x173600: "strlen",
}


class EngineEmu:
    def __init__(self, eboot: Union[str, Path]) -> None:
        from unicorn import UC_ARCH_X86, UC_HOOK_CODE, UC_MODE_64, Uc
        d = Path(eboot).read_bytes()
        self.uc = uc = Uc(UC_ARCH_X86, UC_MODE_64)
        uc.mem_map(0, 0x440000)               # text + relro (GOT holds the stack guard pointer)
        uc.mem_write(0, d[TEXT_OFF:TEXT_OFF + TEXT_LEN])
        uc.mem_write(RELRO_VA, d[RELRO_OFF:RELRO_OFF + RELRO_LEN])
        size = (DATA_MEMSZ + 0xFFFF) & ~0xFFFF
        uc.mem_map(DATA_VA, size)
        uc.mem_write(DATA_VA, d[DATA_OFF:DATA_OFF + DATA_LEN])
        uc.mem_map(STACK, STACK_SIZE)
        uc.mem_map(BUF, 0x200000)
        uc.mem_map(HEAP, HEAP_SIZE)
        uc.mem_map(RET, 0x1000)
        uc.mem_write(RET, b"\xf4")
        self._heap = HEAP
        self._hooks: dict[str, Callable[[], int]] = {
            "delete": lambda: 0, "stack_chk_fail": self._fail, "new": self._new,
            "memmove": self._memmove, "memset": self._memset, "strchr": self._strchr,
            "strpbrk": self._strpbrk, "strtol": self._strtol, "strlen": self._strlen,
        }
        uc.hook_add(UC_HOOK_CODE, self._plt, begin=0x1734A0, end=0x174980)

    # -- libc stubs ---------------------------------------------------------
    def _reg(self, name: str) -> int:
        from unicorn import x86_const
        return int(self.uc.reg_read(getattr(x86_const, "UC_X86_REG_" + name)))

    def _set(self, name: str, v: int) -> None:
        from unicorn import x86_const
        self.uc.reg_write(getattr(x86_const, "UC_X86_REG_" + name), v & (1 << 64) - 1)

    def _cstr(self, a: int, limit: int = 0x10000) -> bytes:
        out = bytearray()
        while len(out) < limit:
            c = self.uc.mem_read(a + len(out), 1)[0]
            if c == 0:
                break
            out.append(c)
        return bytes(out)

    def _plt(self, uc: object, addr: int, size: int, _: object) -> None:
        name = PLT.get(addr)
        if name is None:
            raise RuntimeError(f"unhandled import stub at {addr:#x}")
        rax = self._hooks[name]()
        self._set("RAX", rax)
        rsp = self._reg("RSP")
        ret = struct.unpack("<Q", bytes(self.uc.mem_read(rsp, 8)))[0]
        self._set("RSP", rsp + 8)
        self._set("RIP", ret)

    def _fail(self) -> int:
        raise RuntimeError("__stack_chk_fail")

    def _new(self) -> int:
        n = self._reg("RDI")
        a = (self._heap + 15) & ~15
        self._heap = a + max(n, 16)
        return a

    def _memmove(self) -> int:
        dst, src, n = self._reg("RDI"), self._reg("RSI"), self._reg("RDX")
        if n:
            self.uc.mem_write(dst, bytes(self.uc.mem_read(src, n)))
        return dst

    def _memset(self) -> int:
        dst, c, n = self._reg("RDI"), self._reg("RSI") & 0xFF, self._reg("RDX")
        if n:
            self.uc.mem_write(dst, bytes([c]) * n)
        return dst

    def _strchr(self) -> int:
        s, c = self._reg("RDI"), self._reg("RSI") & 0xFF
        k = 0
        while True:
            b = self.uc.mem_read(s + k, 1)[0]
            if b == c:
                return s + k
            if b == 0:
                return 0
            k += 1

    def _strpbrk(self) -> int:
        s, acc = self._reg("RDI"), self._cstr(self._reg("RSI"))
        k = 0
        while True:
            b = self.uc.mem_read(s + k, 1)[0]
            if b == 0:
                return 0
            if b in acc:
                return s + k
            k += 1

    def _strlen(self) -> int:
        return len(self._cstr(self._reg("RDI")))

    def _strtol(self) -> int:
        s, endp, base = self._reg("RDI"), self._reg("RSI"), self._reg("RDX")
        assert base == 10
        txt = self._cstr(s, 4096)
        m = re.match(rb"[ \t\n\v\f\r]*([+-]?)([0-9]+)", txt)
        if m is None:
            v, e = 0, s
        else:
            v = int(m.group(2))
            v = -min(v, 1 << 63) if m.group(1) == b"-" else min(v, (1 << 63) - 1)
            e = s + m.end()
        if endp:
            self.uc.mem_write(endp, struct.pack("<Q", e))
        return v & (1 << 64) - 1

    # -- calling helpers ----------------------------------------------------
    def _call(self, va: int, **regs: int) -> int:
        sp = STACK + STACK_SIZE - 0x1000
        self.uc.mem_write(sp, struct.pack("<Q", RET))
        self._set("RSP", sp)
        self._set("RBP", sp + 0x800)
        for k, v in regs.items():
            self._set(k.upper(), v)
        self.uc.emu_start(va, RET, count=2_000_000)
        return self._reg("RAX")

    def _load(self, stmt: bytes) -> int:
        """Write a statement (followed by NULs) and return the address of the
        separator byte (name + 4)."""
        self.uc.mem_write(BUF, b"\0" * 0x1000)
        self.uc.mem_write(BUF, stmt)
        us = stmt.index(b"_")
        return BUF + us + 5

    # -- readers ------------------------------------------------------------
    def inline_int(self, stmt: bytes, index: int, bits: int = 16) -> int:
        sep = self._load(stmt)
        v = self._call(0x76690 if bits == 16 else 0x76780, rdi=sep, rsi=index) & ((1 << bits) - 1)
        return v - (1 << bits) if v >> (bits - 1) else v

    def inline_present(self, stmt: bytes, index: int) -> bool:
        sep = self._load(stmt)
        return bool(self._call(0x76860, rdi=sep, rsi=index) & 0xFFFF)

    def obj_split(self, stmt: bytes) -> list[tuple[int, int]]:
        sep = self._load(stmt)
        self.uc.mem_write(OBJ, b"\xaa" * 0x150)
        self._call(0xD690, rdi=OBJ, rsi=sep)
        n = struct.unpack("<Q", bytes(self.uc.mem_read(OBJ, 8)))[0]
        out = []
        for i in range(min(n, 20)):
            s, e = struct.unpack("<QQ", bytes(self.uc.mem_read(OBJ + 8 + 16 * i, 16)))
            out.append((s - BUF, e - BUF))
        return out

    def obj_expr(self, stmt: bytes, index: int) -> int:
        """VA 0xDA50 on argument ``index`` of the statement (after 0xD690)."""
        self.obj_split(stmt)
        self._heap = HEAP
        v = self._call(0xDA50, rdi=OBJ, rsi=index) & 0xFFFFFFFF
        return v - (1 << 32) if v >> 31 else v

    def obj_strict_int(self, stmt: bytes, index: int, default: int = -12345) -> int:
        self.obj_split(stmt)
        v = self._call(0xE310, rdi=OBJ, rsi=index, rdx=default & 0xFFFFFFFF) & 0xFFFFFFFF
        return v - (1 << 32) if v >> 31 else v

    def dispatch(self, name: str) -> Optional[int]:
        """Handler entry the main interpreter dispatches ``name`` to, or None when
        the name is ignored (reaches the loop continue VA 0x95EB0 / return
        VA 0x9B516 without a side effect).  The entry is the start of the basic
        block holding the first instruction with a memory operand (other than a
        frame spill or a switch-table load), a call or a push/pop."""
        from capstone import CS_ARCH_X86, CS_MODE_64, Cs
        from unicorn import UC_HOOK_CODE
        if not hasattr(self, "_md"):
            self._md = Cs(CS_ARCH_X86, CS_MODE_64)
        st: dict[str, Optional[int]] = {"block": 0x95F09, "result": None, "done": 0, "branch": 0}

        def is_work(mn: str, op: str) -> bool:
            if mn in ("call", "push", "pop", "ret"):
                return True
            if "[" in op and mn not in ("lea", "nop"):
                if mn == "movsxd" and re.search(r"\[\w+ \+ \w+\*4\]", op):
                    return False
                if mn == "mov" and re.search(r"\[(rbp|rsp)( [+-] 0x[0-9a-f]+)?\]", op):
                    return False
                return True
            return False

        def hook(uc: object, addr: int, size: int, _: object) -> None:
            if addr in (0x95EB0, 0x9B516):
                st["done"] = 1
                self.uc.emu_stop()
                return
            if st["branch"]:                       # first instruction after a branch = block start
                st["block"] = addr
            ins = next(self._md.disasm_lite(bytes(self.uc.mem_read(addr, size)), addr))
            st["branch"] = 1 if ins[2].startswith("j") else 0
            if 0x95F09 <= addr <= 0x95F4E:         # common prologue: reads the name, computes the key
                return
            if is_work(ins[2], ins[3]):
                st["result"] = st["block"]
                st["done"] = 1
                self.uc.emu_stop()

        h1 = self.uc.hook_add(UC_HOOK_CODE, hook, begin=0x95E60, end=0x9B760)
        try:
            stmt = b"_" + name.encode("latin-1") + b"(1,2,3);"
            sep = self._load(stmt)
            sp = STACK + STACK_SIZE - 0x1000
            self._set("RSP", sp)
            self._set("RBP", sp + 0x800)
            self._set("R14", sep - 4)
            self._set("RBX", BUF + len(stmt))
            self._set("R11", 0)
            self.uc.emu_start(0x95F09, 0x7FFFFFFF, count=400)
        finally:
            self.uc.hook_del(h1)
        if not st["done"]:
            raise RuntimeError(f"dispatch of {name!r} did not settle in 400 instructions")
        return st["result"]

    def marker_ids(self, stmt: bytes) -> tuple[int, int]:
        """Run the Z handler from VA 0x96615 up to 0x96709 (ids stored)."""
        sep = self._load(stmt)
        name = sep - 4
        sp = STACK + STACK_SIZE - 0x1000
        self._set("RSP", sp)
        self._set("RBP", sp + 0x800)
        self._set("R14", name)
        self._set("RCX", self.uc.mem_read(name + 2, 1)[0])
        self._set("RAX", self.uc.mem_read(name + 3, 1)[0])
        self.uc.mem_write(0x1654344, struct.pack("<ii", 0, 0))
        self.uc.emu_start(0x96615, 0x96709, count=200)
        b, s = struct.unpack("<ii", bytes(self.uc.mem_read(0x1654344, 8)))
        return b, s


def _main(argv: Optional[list[str]] = None) -> int:
    import argparse
    from . import game_dir
    ap = argparse.ArgumentParser(prog="python -m tsuki.script_emu")
    ap.add_argument("--eboot")
    ap.add_argument("mode", choices=["expr", "int", "split"])
    ap.add_argument("args", nargs="+")
    a = ap.parse_args(argv)
    gd = game_dir()
    eb = a.eboot or (str(gd / "eboot.bin") if gd else None)
    if eb is None:
        raise SystemExit("eboot.bin not found")
    emu = EngineEmu(eb)
    for x in a.args:
        stmt = b"_STCH(" + x.encode() + b");"
        if a.mode == "expr":
            print(repr(x), emu.obj_expr(stmt, 0))
        elif a.mode == "int":
            print(repr(x), emu.inline_int(stmt, 0))
        else:
            print(repr(x), emu.obj_split(stmt))
    return 0


if __name__ == "__main__":
    sys.exit(_main())
