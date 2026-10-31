"""Run the PS4 engine's own layer code (script handlers, FADS commit, motion and tween
update, layer draw) on the user's eboot.bin under unicorn, as ground truth for
tsuki.misc_layer_state_and_motions (development / verification aid only; nothing here is
needed by a port).

Requirements: ``unicorn`` (2.1), ``capstone``, ``keystone-engine`` and a Windows x64 host
(VEX instructions run natively, see tsuki.misc_layer_state_and_motions_vex).

Main entry points
-----------------
``LayerEmu(eboot)``        CPU + memory (text, relro, data, stack, heap), libc stubs at the PLT,
                           VEX instructions patched to ud2 and executed natively.
``LayerEmu.call(va, ...)`` call one function (System V ABI; float args in xmm0..).
``Stepper(emu)``           feed one script statement through the interpreter's own
                           ScriptStep (VA 0x95E60) and stop when the statement's handler
                           continues (0x95EB0) or yields (0x9B516).
``Stepper.snapshot()``     save / restore the whole data segment (fast replay from a state).

Addresses (CUSA47999 v01.01 text VAs; [E] = read in the disassembly):
    scene_state 0x16500C0 (0x1A08 bytes, per-layer script arrays), copy 0x1651AC8
    layer arrays A = 0xDDB5E8 (current, drawn), B = 0xDE2650 (next, written by the FADS
    commit 0x7DD10), C = 0xDE96B8 (previous, snapshot taken when B is transferred to A);
    40 records of 0x2C8 bytes each; code offsets are relative to array + 0x2C8 * layer.
"""
from __future__ import annotations

import math
import re
import struct
from pathlib import Path
from typing import Callable, Iterable, Optional, Union

__all__ = [
    "LayerEmu", "Stepper", "SCENE", "SCENE_LEN", "SCENE_OLD", "ARR_A", "ARR_B", "ARR_C",
    "LAYER_SIZE", "available", "Frames", "SpriteCall", "SPRITE_ENTRIES", "Rig", "REC_LEN",
]

TEXT_OFF, TEXT_LEN = 0x2280, 0x329DC8
DATA_OFF, DATA_LEN, DATA_VA, DATA_MEMSZ = 0x365FA0, 0x7D66E0, 0x600000, 0x1A85790
RELRO_OFF, RELRO_LEN, RELRO_VA = 0x32C210, 0x35ED0, 0x400000
STACK, STACK_SIZE = 0x30000000, 0x200000
HEAP, HEAP_SIZE = 0x32000000, 0x4000000
RET = 0x37000000
BUF = 0x37100000
TOUPPER = 0x37200000
PLT_LO, PLT_HI = 0x1734A0, 0x174980

SCENE, SCENE_LEN = 0x16500C0, 0x1A08
SCENE_OLD = 0x1651AC8
ARR_A, ARR_B, ARR_C = 0xDDB5E8, 0xDE2650, 0xDE96B8
LAYER_SIZE = 0x2C8
DATA_SNAP_LEN = 0x1A90000

M64 = (1 << 64) - 1
_BMI = {"andn", "bextr", "bzhi", "sarx", "shlx", "shrx", "pdep", "pext", "mulx", "rorx",
        "blsi", "blsr", "blsmsk"}


def available() -> bool:
    try:
        import unicorn  # noqa: F401
        import capstone  # noqa: F401
    except ImportError:
        return False
    from tsuki.misc_layer_state_and_motions_vex import available as vex_ok
    return vex_ok()


def _ff(x: float) -> int:
    return struct.unpack("<I", struct.pack("<f", x))[0]


def _f(u: int) -> float:
    return struct.unpack("<f", struct.pack("<I", u & 0xFFFFFFFF))[0]


class LayerEmu:
    """unicorn x86-64 machine with the eboot mapped at its link addresses."""

    def __init__(self, eboot: Union[str, Path]) -> None:
        import capstone
        from unicorn import (UC_ARCH_X86, UC_HOOK_CODE, UC_HOOK_MEM_WRITE, UC_MODE_64, Uc)
        from eboot.disasm import Index, import_names
        from eboot.selfelf import Eboot
        from tsuki.misc_layer_state_and_motions_vex import NativeVex
        d = Path(eboot).read_bytes()
        self.data = d
        eb = Eboot.open(Path(eboot))
        self.imports = import_names(eb)
        self.uc = uc = Uc(UC_ARCH_X86, UC_MODE_64)
        uc.mem_map(0, 0x440000)
        uc.mem_write(0, d[TEXT_OFF:TEXT_OFF + TEXT_LEN])
        uc.mem_write(RELRO_VA, d[RELRO_OFF:RELRO_OFF + RELRO_LEN])
        uc.mem_map(DATA_VA, (DATA_MEMSZ + 0xFFFF) & ~0xFFFF)
        uc.mem_write(DATA_VA, d[DATA_OFF:DATA_OFF + DATA_LEN])
        # every VEX SIMD instruction becomes an invalid opcode (empty write keeps bytes;
        # unicorn raises INSN_INVALID on the 256-bit / unsupported ones and we also force
        # the rest through the native path for correctness)
        self.native = NativeVex()
        ix = Index.build(eb)
        self.vex_orig: dict[int, bytes] = {}
        for f in ix.funcs:
            for ins in ix.insns.get(f, []):
                b0 = d[ins.addr + TEXT_OFF]
                if b0 in (0xC4, 0xC5) and ins.mnem not in _BMI:
                    raw = d[ins.addr + TEXT_OFF: ins.addr + TEXT_OFF + ins.size]
                    self.vex_orig[ins.addr] = raw
        for a in self.vex_orig:
            uc.mem_write(a, b"\x0f\x0b")          # ud2 (the rest of the bytes is never reached)
        uc.mem_map(STACK, STACK_SIZE)
        uc.mem_map(HEAP, HEAP_SIZE)
        uc.mem_map(RET, 0x1000)
        uc.mem_write(RET, b"\xf4")
        uc.mem_map(BUF, 0x100000)
        uc.mem_map(TOUPPER, 0x1000)
        uc.mem_write(TOUPPER, b"".join(struct.pack("<H", ord(chr(i).upper()) if i < 128 else i)
                                       for i in range(256)))
        uc.mem_write(BUF + 0xFF000, struct.pack("<Q", 0x1234567890ABCDEF))
        uc.mem_write(0x4351D8, struct.pack("<Q", BUF + 0xFF000))     # stack-guard pointer
        self._heap = HEAP
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.md.detail = True
        self.stubs: dict[int, Union[Callable[[], Optional[int]], int]] = {}
        self.unknown_imports: dict[str, int] = {}
        self.time_us = 0
        self.rand_state = 1
        self.writes: list[tuple[int, int, int]] = []
        self.track_writes = False
        self._stub_hooks: dict[int, int] = {}
        uc.hook_add(UC_HOOK_CODE, self._plt, begin=PLT_LO, end=PLT_HI)
        uc.hook_add(UC_HOOK_MEM_WRITE, self._wr)

    # ---- register / memory helpers --------------------------------------
    def r(self, name: str) -> int:
        from unicorn import x86_const as ux
        return int(self.uc.reg_read(getattr(ux, "UC_X86_REG_" + name.upper())))

    def w(self, name: str, v: int) -> None:
        from unicorn import x86_const as ux
        self.uc.reg_write(getattr(ux, "UC_X86_REG_" + name.upper()), v & M64)

    def rd(self, a: int, n: int) -> bytes:
        return bytes(self.uc.mem_read(a, n))

    def wr(self, a: int, b: bytes) -> None:
        self.uc.mem_write(a, b)
        if self.track_writes and b and not (STACK <= a < STACK + STACK_SIZE):
            self.writes.append((a, len(b), int.from_bytes(b[:8], "little")))

    def u8(self, a: int) -> int:
        return self.rd(a, 1)[0]

    def i16(self, a: int) -> int:
        return struct.unpack("<h", self.rd(a, 2))[0]

    def i32(self, a: int) -> int:
        return struct.unpack("<i", self.rd(a, 4))[0]

    def i64(self, a: int) -> int:
        return struct.unpack("<q", self.rd(a, 8))[0]

    def f32(self, a: int) -> float:
        return struct.unpack("<f", self.rd(a, 4))[0]

    def xmm_f(self, i: int) -> float:
        return _f(self.r(f"XMM{i}"))

    def set_xmm_f(self, i: int, v: float) -> None:
        from unicorn import x86_const as ux
        self.uc.reg_write(getattr(ux, f"UC_X86_REG_XMM{i}"), _ff(v))

    def cstr(self, a: int, limit: int = 4096) -> bytes:
        out = bytearray()
        while len(out) < limit:
            c = self.uc.mem_read(a + len(out), 1)[0]
            if c == 0:
                break
            out.append(c)
        return bytes(out)

    def _wr(self, uc: object, acc: int, addr: int, size: int, val: int, _: object) -> None:
        if self.track_writes and not (STACK <= addr < STACK + STACK_SIZE):
            self.writes.append((addr, size, val & ((1 << (8 * size)) - 1) if size <= 8 else val))

    # ---- stubs -------------------------------------------------------------
    def _ret(self, rax: int) -> None:
        self.w("RAX", rax)
        rsp = self.r("RSP")
        ret = struct.unpack("<Q", self.rd(rsp, 8))[0]
        self.w("RSP", rsp + 8)
        self.w("RIP", ret)

    def stub(self, va: int, fn: Union[Callable[[], Optional[int]], int] = 0) -> None:
        """Replace the function at ``va``: fn() returns RAX (None = fn set RIP itself)."""
        from unicorn import UC_HOOK_CODE
        self.stubs[va] = fn
        if va not in self._stub_hooks:
            self._stub_hooks[va] = self.uc.hook_add(UC_HOOK_CODE, self._stub_hit, begin=va, end=va)

    def unstub(self, va: int) -> None:
        self.stubs.pop(va, None)

    def _stub_hit(self, uc: object, addr: int, size: int, _: object) -> None:
        fn = self.stubs.get(addr)
        if fn is None:
            return
        v = fn() if callable(fn) else fn
        if v is not None:
            self._ret(v)

    def _plt(self, uc: object, addr: int, size: int, _: object) -> None:
        name = self.imports.get(addr, f"plt_{addr:x}")
        f = getattr(self, "_imp_" + name, None)
        if f is None:
            self.unknown_imports[name] = self.unknown_imports.get(name, 0) + 1
            self._ret(0)
            return
        v = f()
        if v is not None:
            self._ret(v)

    # libc ------------------------------------------------------------------
    def _alloc(self, n: int, al: int = 16) -> int:
        a = (self._heap + al - 1) & ~(al - 1)
        self._heap = a + max(n, 16)
        if self._heap > HEAP + HEAP_SIZE:
            raise MemoryError("emulator heap exhausted")
        return a

    def _imp__Znwm(self) -> int:
        return self._alloc(self.r("RDI"))
    _imp__Znam = _imp__Znwm

    def _imp_memalign(self) -> int:
        return self._alloc(self.r("RSI"), max(16, self.r("RDI")))

    def _imp_malloc(self) -> int:
        return self._alloc(self.r("RDI"))

    def _imp__ZdlPv(self) -> int:
        return 0
    _imp__ZdaPv = _imp__ZdlPv

    def _imp_free(self) -> int:
        return 0

    def _imp___stack_chk_fail(self) -> int:
        raise RuntimeError("__stack_chk_fail")

    def _imp_memcpy(self) -> int:
        dst, src, n = self.r("RDI"), self.r("RSI"), self.r("RDX")
        if n:
            self.wr(dst, self.rd(src, n))
        return dst
    _imp_memmove = _imp_memcpy

    def _imp_memset(self) -> int:
        dst, c, n = self.r("RDI"), self.r("RSI") & 0xFF, self.r("RDX")
        if n:
            self.wr(dst, bytes([c]) * n)
        return dst

    def _imp_memcmp(self) -> int:
        a, b, n = self.r("RDI"), self.r("RSI"), self.r("RDX")
        x, y = self.rd(a, n), self.rd(b, n)
        return 0 if x == y else (-1 if x < y else 1) & M64

    def _imp_strlen(self) -> int:
        return len(self.cstr(self.r("RDI")))

    def _imp_strcpy(self) -> int:
        dst, src = self.r("RDI"), self.r("RSI")
        self.wr(dst, self.cstr(src) + b"\0")
        return dst

    def _imp_strncpy(self) -> int:
        dst, src, n = self.r("RDI"), self.r("RSI"), self.r("RDX")
        s = self.cstr(src, n)[:n]
        self.wr(dst, s + b"\0" * (n - len(s)))
        return dst

    def _imp_strcat(self) -> int:
        dst, src = self.r("RDI"), self.r("RSI")
        a = self.cstr(dst)
        self.wr(dst + len(a), self.cstr(src) + b"\0")
        return dst

    def _imp_strcmp(self) -> int:
        a, b = self.cstr(self.r("RDI")), self.cstr(self.r("RSI"))
        return 0 if a == b else ((-1 if a < b else 1) & M64)

    def _imp_strncmp(self) -> int:
        n = self.r("RDX")
        a, b = self.cstr(self.r("RDI"), n)[:n], self.cstr(self.r("RSI"), n)[:n]
        return 0 if a == b else ((-1 if a < b else 1) & M64)

    def _imp_strchr(self) -> int:
        s, c = self.r("RDI"), self.r("RSI") & 0xFF
        k = 0
        while True:
            b = self.uc.mem_read(s + k, 1)[0]
            if b == c:
                return s + k
            if b == 0:
                return 0
            k += 1

    def _imp_strpbrk(self) -> int:
        s, acc = self.r("RDI"), self.cstr(self.r("RSI"))
        k = 0
        while True:
            b = self.uc.mem_read(s + k, 1)[0]
            if b == 0:
                return 0
            if b in acc:
                return s + k
            k += 1

    def _imp_strtol(self) -> int:
        s, endp, base = self.r("RDI"), self.r("RSI"), self.r("RDX") & 0xFFFFFFFF
        txt = self.cstr(s, 4096)
        if base == 0:
            m = re.match(rb"[ \t\n\v\f\r]*([+-]?)(0[xX][0-9a-fA-F]+|0[0-7]*|[1-9][0-9]*)", txt)
        else:
            m = re.match(rb"[ \t\n\v\f\r]*([+-]?)([0-9]+)", txt)
        if m is None:
            v, e = 0, s
        else:
            g = m.group(2)
            if g[:2] in (b"0x", b"0X"):
                v = int(g, 16)
            elif base == 0 and g.startswith(b"0") and len(g) > 1:
                v = int(g, 8)
            else:
                v = int(g)
            v = -min(v, 1 << 63) if m.group(1) == b"-" else min(v, (1 << 63) - 1)
            e = s + m.end()
        if endp:
            self.wr(endp, struct.pack("<Q", e))
        return v & M64

    def _imp__Getptoupper(self) -> int:
        return TOUPPER

    def _fmt(self, fmt: bytes, args: list[int]) -> bytes:
        out = bytearray()
        i = 0
        ai = 0
        while i < len(fmt):
            c = fmt[i]
            if c != 0x25:
                out.append(c)
                i += 1
                continue
            m = re.match(rb"%([-+ 0#]*)(\d*)(?:\.(\d+))?(hh|h|ll|l|z)?([diouxXsc%])", fmt[i:])
            if not m:
                raise RuntimeError(f"format {fmt!r}")
            flags, width, _prec, ln, conv = m.groups()
            i += m.end()
            if conv == b"%":
                out += b"%"
                continue
            a = args[ai]
            ai += 1
            if conv == b"s":
                s = self.cstr(a)
            elif conv == b"c":
                s = bytes([a & 0xFF])
            else:
                bits = 64 if ln in (b"l", b"ll", b"z") else 32
                v = a & ((1 << bits) - 1)
                if conv in b"di" and v >> (bits - 1):
                    v -= 1 << bits
                s = (format(v, "x") if conv == b"x" else format(v, "X") if conv == b"X" else str(v)).encode()
            wd = int(width or 0)
            if len(s) < wd:
                s = (b"0" if b"0" in flags and conv != b"s" else b" ") * (wd - len(s)) + s
            out += s
        return bytes(out)

    def _imp_sprintf(self) -> int:
        dst, fmt = self.r("RDI"), self.cstr(self.r("RSI"))
        s = self._fmt(fmt, [self.r(x) for x in ("RDX", "RCX", "R8", "R9")])
        self.wr(dst, s + b"\0")
        return len(s)

    def _imp_snprintf(self) -> int:
        dst, n, fmt = self.r("RDI"), self.r("RSI"), self.cstr(self.r("RDX"))
        s = self._fmt(fmt, [self.r(x) for x in ("RCX", "R8", "R9")])
        if n:
            self.wr(dst, s[:n - 1] + b"\0")
        return len(s)

    # libm (float args in xmm0 / xmm1)
    def _fret(self, v: float) -> None:
        self.set_xmm_f(0, v)
        self._ret(0)

    def _dret(self, v: float) -> None:
        from unicorn import x86_const as ux
        self.uc.reg_write(ux.UC_X86_REG_XMM0, struct.unpack("<Q", struct.pack("<d", v))[0])
        self._ret(0)

    def _xd(self, i: int) -> float:
        return struct.unpack("<d", struct.pack("<Q", self.r(f"XMM{i}") & M64))[0]

    def _imp_sinf(self) -> None:
        self._fret(math.sin(self.xmm_f(0)))

    def _imp_cosf(self) -> None:
        self._fret(math.cos(self.xmm_f(0)))

    def _imp_powf(self) -> None:
        self._fret(math.pow(self.xmm_f(0), self.xmm_f(1)))

    def _imp_fmodf(self) -> None:
        self._fret(math.fmod(self.xmm_f(0), self.xmm_f(1)))

    def _imp_atan2f(self) -> None:
        self._fret(math.atan2(self.xmm_f(0), self.xmm_f(1)))

    def _imp_sin(self) -> None:
        self._dret(math.sin(self._xd(0)))

    def _imp_cos(self) -> None:
        self._dret(math.cos(self._xd(0)))

    def _imp_pow(self) -> None:
        self._dret(math.pow(self._xd(0), self._xd(1)))

    def _imp_exp2(self) -> None:
        self._dret(2.0 ** self._xd(0))

    def _imp_exp2f(self) -> None:
        self._fret(2.0 ** self.xmm_f(0))

    def _imp_sqrt(self) -> None:
        self._dret(math.sqrt(self._xd(0)))

    def _imp_asin(self) -> None:
        self._dret(math.asin(self._xd(0)))

    def _imp_asinf(self) -> None:
        self._fret(math.asin(self.xmm_f(0)))

    def _imp_acosf(self) -> None:
        self._fret(math.acos(self.xmm_f(0)))

    def _imp_sqrtf(self) -> None:
        self._fret(math.sqrt(self.xmm_f(0)))

    def _imp_sincosf(self) -> None:
        x = self.xmm_f(0)
        self.wr(self.r("RDI"), struct.pack("<f", math.sin(x)))
        self.wr(self.r("RSI"), struct.pack("<f", math.cos(x)))
        self._ret(0)

    def _imp_sceKernelGetProcessTime(self) -> int:
        return self.time_us

    def _imp_rand(self) -> int:
        # deterministic LCG (the engine's shakes call rand(); values are recorded by the caller)
        self.rand_state = (self.rand_state * 1103515245 + 12345) & 0x7FFFFFFF
        return self.rand_state

    # ---- invalid-opcode trap: native VEX -------------------------------------
    def exec_invalid(self, rip: int) -> None:
        raw = self.vex_orig.get(rip)
        if raw is None:
            raise RuntimeError(f"invalid instruction at {rip:#x}")
        nxt = self.native.execute(self.uc, rip, raw)
        st = self.native.last_store
        if st is not None:
            self.wr(st[0], st[1])
        self.w("RIP", nxt)

    # ---- calls -----------------------------------------------------------------
    def run(self, pc: int, until: int = RET, limit: int = 200_000_000) -> None:
        from unicorn import UC_ERR_INSN_INVALID, UcError
        while True:
            try:
                self.uc.emu_start(pc, until, count=limit)
            except UcError as e:
                rip = self.r("RIP")
                if e.errno == UC_ERR_INSN_INVALID:
                    self.exec_invalid(rip)
                    pc = self.r("RIP")
                    continue
                raise RuntimeError(f"{e} at {rip:#x}") from None
            return

    def call(self, va: int, regs: Optional[dict[str, int]] = None,
             fregs: Optional[dict[int, float]] = None, stack_args: Iterable[int] = ()) -> int:
        sp = STACK + STACK_SIZE - 0x10000
        args = list(stack_args)
        for j, v in enumerate(args):
            self.uc.mem_write(sp + 8 + 8 * j, struct.pack("<Q", v & M64))
        self.uc.mem_write(sp, struct.pack("<Q", RET))
        self.w("RSP", sp)
        self.w("RBP", sp + 0x800)
        for reg, rv in (regs or {}).items():
            self.w(reg, rv)
        for i, fv in (fregs or {}).items():
            self.set_xmm_f(i, fv)
        self.run(va)
        return self.r("RAX")

    # ---- data snapshots ----------------------------------------------------------
    def snapshot(self) -> bytes:
        return self.rd(DATA_VA, DATA_SNAP_LEN)

    def restore(self, snap: bytes) -> None:
        self.uc.mem_write(DATA_VA, snap)

    def layer(self, arr: int, i: int, off: int, n: int) -> bytes:
        return self.rd(arr + LAYER_SIZE * i + off, n)


class Stepper:
    """Run script statements through the engine's ScriptStep (VA 0x95E60)."""

    def __init__(self, emu: LayerEmu) -> None:
        from unicorn import UC_HOOK_CODE
        from tsuki import image_dir
        from tsuki.nam import load_nam
        self.E = E = emu
        E.stub(0x7AF90, 0)          # history snapshot (copies scene_state into the backlog)
        # picture-file lookup by name (archive-wide index), as the asset system would answer
        names: dict[str, dict[str, int]] = {}
        idir = image_dir()
        if idir is None:
            raise FileNotFoundError("game folder not found")
        for arc, base in (("allpac0", 0), ("allpac1", 0x10000), ("allpac1_2", 0x20000),
                          ("allpac3", 0x40000), ("allpac4", 0x50000)):
            for i, n in enumerate(load_nam(idir / (arc + ".nam")).names):
                names.setdefault(arc, {})[n.upper()] = base | i
        order = ["allpac4", "allpac3", "allpac1_2", "allpac1", "allpac0"]

        def c3270() -> int:
            nm = E.cstr(E.r("RDI")).decode("latin-1").upper()
            for a in order:
                if nm in names[a]:
                    return names[a][nm]
            return 0xFFFFFFFF
        E.stub(0xC3270, c3270)
        for va in (0xEB190, 0xEB540, 0xCC670):      # texture load / unload, UI sound
            E.stub(va, 0)
        # the commit (0x7E09C) looks pictures with id >= 0xA8D up in a red-black tree whose
        # header pointer 0x174DE38 is filled at boot: give it an empty tree (root = nil
        # node, nil flag byte +0x19 = 1) so sprite pictures can be committed
        if E.i64(0x174DE38) == 0:
            hdr = E._alloc(0x40)
            nil = E._alloc(0x40)
            E.wr(nil, bytes(0x40))
            E.wr(nil + 0x19, bytes([1]))
            E.wr(hdr, struct.pack("<QQQ", nil, nil, nil))
            E.wr(0x174DE38, struct.pack("<Q", hdr))
        self.state: dict[str, object] = {"go": False, "end": None}

        def h_disp(uc: object, a: int, s: int, _: object) -> None:
            self.state["go"] = True

        def h_end(uc: object, a: int, s: int, _: object) -> None:
            if self.state["go"]:
                self.state["end"] = "continue" if a == 0x95EB0 else "yield"
                E.uc.emu_stop()
        E.uc.hook_add(UC_HOOK_CODE, h_disp, begin=0x95F4E, end=0x95F4E)
        E.uc.hook_add(UC_HOOK_CODE, h_end, begin=0x95EB0, end=0x95EB0)
        E.uc.hook_add(UC_HOOK_CODE, h_end, begin=0x9B516, end=0x9B516)
        self.snap: Optional[bytes] = None

    def step(self, stmt: str) -> str:
        """Execute one statement such as ``_STCH(3,img0597,240,136,1000,255,2,,CC,,,);``.
        Returns 'continue' or 'yield'."""
        E = self.E
        b = stmt.encode("latin-1")
        E.wr(BUF, b"\0" * 0x1000)
        E.wr(BUF, b + b"_DMMY();")
        self.state.update(go=False, end=None)
        sp = STACK + STACK_SIZE - 0x10000
        E.wr(sp, struct.pack("<Q", RET))
        E.w("RSP", sp)
        E.w("RDI", BUF)
        E.run(0x95E60)
        if self.state["end"] is None:
            raise RuntimeError(f"no end for {stmt}: rip {E.r('RIP'):#x}")
        return str(self.state["end"])

    def save(self) -> None:
        self.snap = self.E.snapshot()

    def restore(self) -> None:
        if self.snap is None:
            raise RuntimeError("no snapshot")
        self.E.restore(self.snap)


# ---------------------------------------------------------------------------
# frame pipeline: per-frame update (0x57980) and scene draw (0x56DE0) with the
# picture-draw entry points recorded instead of executed
# ---------------------------------------------------------------------------
SPRITE_ENTRIES = {0xEB780: "plain", 0xEB9C0: "rect", 0xEB890: "cells"}


class SpriteCall(dict):
    """One picture draw: slot (0xE8 + layer for scene layers), flags (bits 0..3 anchor,
    0x10 global scale, 0x20 rotate, 0x40 pivot zoom), x/y (engine units), the global draw
    state at that moment and, for 'rect'/'cells', the source rectangle."""

    @property
    def layer(self) -> int:
        return int(self["slot"]) - 0xE8


class Frames:
    """Engine pipeline driver: Stepper statements -> FADS commit -> per-frame update ->
    draw capture."""

    def __init__(self, emu: LayerEmu, stepper: Optional[Stepper] = None) -> None:
        self.E = emu
        self.S = stepper or Stepper(emu)
        emu.wr(0xDC7BE0, struct.pack("<H", 1))      # frames per update (0xD47C0 clamps to 3)
        emu.call(0x57590)                           # layer-array initialisation (scene start)
        self.calls: list[SpriteCall] = []
        for va, kind in SPRITE_ENTRIES.items():
            emu.stub(va, self._mk(kind))

    def _globals(self) -> dict[str, object]:
        E = self.E
        return {
            "scale": struct.unpack("<2f", E.rd(0xDC7BA4, 8)),
            "angle": E.i32(0x2009150),
            "origin9": struct.unpack("<2f", E.rd(0x2009154, 8)),
            "pivot": struct.unpack("<2f", E.rd(0x2009148, 8)),
            "pivot_zoom": struct.unpack("<2f", E.rd(0xDC7BAC, 8)),
            "screen": struct.unpack("<2f", E.rd(0x1DBD3EC, 8)),
            "colour": struct.unpack("<4f", E.rd(0x1DBD0D8, 16)),   # a, r, g, b
            "blend_node": E.i32(0x2009150 - 0x150 + 0x150),
        }

    def _mk(self, kind: str) -> Callable[[], int]:
        def f() -> int:
            E = self.E
            rec = SpriteCall(kind=kind, slot=E.r("RDI") & 0xFFFFFFFF, flags=E.r("RSI") & 0xFFFFFFFF,
                             arg2=E.r("RDX") & 0xFFFFFFFF, x=E.xmm_f(0), y=E.xmm_f(1),
                             ret=E.i64(E.r("RSP")))
            if kind in ("rect", "cells") and E.r("RCX"):
                rec["rect"] = struct.unpack("<4h", E.rd(E.r("RCX"), 8))
            rec.update(self._globals())
            self.calls.append(rec)
            return 0
        return f

    def step(self, stmt: str) -> str:
        return self.S.step(stmt)

    def update(self, frames: int = 1) -> None:
        self.E.call(0x57980, {"rdi": frames})

    def draw(self) -> list[SpriteCall]:
        self.calls = []
        self.E.call(0x56DE0)
        return list(self.calls)


# ---------------------------------------------------------------------------
# Rig: one emulator with a prepared base state (layer 0 = BLACK, committed, one
# frame run) that every probe restores before running its statements.
# ---------------------------------------------------------------------------
REC_LEN = 0x2D8          # bytes dumped per layer record (offsets up to +0x2D1 are used)


class Rig:
    """Probe driver used by the verification scripts and tests.

    ``Rig(eboot)`` builds the emulator, runs ``_STCC(-1)``, ``_STCH(0,BLACK,...)``
    (the first STCH builds the engine's picture-name map, about 16 s) and ``_FADS(1,0,0)``,
    runs one frame and snapshots the data segment.  ``run(stmts, frames)`` restores that
    snapshot, executes the statements through ScriptStep, runs ``frames`` per-frame updates
    and the scene draw, and returns the sprite calls of scene layers.
    """

    BASE = ("_STCC(-1);", "_STCH(0,BLACK,240,136,1000,255,2,,CC,,,);", "_FADS(1,0,0);")

    def __init__(self, eboot: Union[str, Path], base: Iterable[str] = BASE) -> None:
        self.E = LayerEmu(eboot)
        self.F = Frames(self.E)
        for st in base:
            self.F.step(st)
        self.F.update(1)
        self.base = self.E.snapshot()

    def reset(self) -> None:
        self.E.restore(self.base)

    def run(self, stmts: Iterable[str], frames: int = 1, draw: bool = True,
            reset: bool = True, commit: bool = True) -> list[SpriteCall]:
        """``commit`` appends ``_FADS(1,0,0);`` (immediate commit) to the statements."""
        if reset:
            self.reset()
        for st in list(stmts) + (["_FADS(1,0,0);"] if commit else []):
            self.F.step(st)
        for _ in range(frames):
            self.F.update(1)
        return [c for c in self.F.draw() if c["slot"] >= 0xE8] if draw else []

    def scene(self) -> bytes:
        """The script-side scene_state block (0x1A08 bytes at VA 0x16500C0)."""
        return self.E.rd(SCENE, SCENE_LEN)

    def record(self, arr: int, layer: int) -> bytes:
        """``REC_LEN`` bytes of the render record of ``layer`` in array A, B or C."""
        return self.E.rd(arr + LAYER_SIZE * layer, REC_LEN)
