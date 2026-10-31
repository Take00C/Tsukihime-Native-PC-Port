"""unicorn harness for the ADV system-state functions of eboot.bin (CUSA47999 v01.01).

Development aid for ``tsuki.misc_adv_system_state`` (docs/formats/adv_state.md); nothing
here is needed to read save data.  Needs ``unicorn`` and ``capstone``.

* The text, relro and data segments are mapped at their link addresses (file offsets as in
  ``tsuki.script_emu``); bss is zero-filled.
* unicorn 2.1 rejects 256-bit VEX forms and mis-executes some 128-bit ones (fonts.md 6.7),
  so every VEX SIMD instruction of the functions that are run is patched to ``ud2`` and
  executed by a small Python interpreter (:class:`_Vex`) on a shadow YMM register file.
  Only the forms used by the history / slot / config code are implemented; anything else
  raises ``NotImplementedError`` naming the instruction (no silent fallback).
* Imports reached through the PLT run as Python stubs (memcpy, memset, ...); any other
  import raises.  Game functions can be stubbed (``stub``) with a fixed or computed return
  value; every stubbed call is logged with its six integer arguments.
* Optional memory logs (``log_reads`` / ``log_writes``) record (pc, address, size, value).
"""
from __future__ import annotations

import random
import struct
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Dict, List, Optional, Sequence, Tuple, Union

TEXT_OFF, TEXT_LEN = 0x2280, 0x329DC8
DATA_OFF, DATA_LEN, DATA_VA, DATA_MEMSZ = 0x365FA0, 0x7D66E0, 0x600000, 0x1A85790
RELRO_OFF, RELRO_LEN, RELRO_VA = 0x32C210, 0x35ED0, 0x400000
STACK, STACK_SIZE = 0x30000000, 0x200000
HEAP, HEAP_SIZE = 0x32000000, 0x200000
SCRATCH, SCRATCH_SIZE = 0x40000000, 0x800000      # caller buffers (ring records, script, save image)
RET = 0x34000000
GUARD = 0x34001000
PLT_LO, PLT_HI = 0x173400, 0x174A00
STACK_GUARD_GOT = 0x4351D8

# PLT stub VA -> import name (eboot.disasm.import_names, CUSA47999 v01.01)
PLT_NAMES = {
    0x1734A0: "delete", 0x1739F0: "delete", 0x173660: "free",
    0x1734B0: "stack_chk_fail", 0x1734C0: "new", 0x1737E0: "new", 0x173680: "malloc",
    0x1734D0: "memmove", 0x173650: "memcpy", 0x1734F0: "memset", 0x173600: "strlen",
    0x173760: "memcmp", 0x173780: "strcpy", 0x173620: "strcmp", 0x1736C0: "ldexpf",
}
_BMI = {"andn", "bextr", "blsi", "blsmsk", "blsr", "bzhi", "mulx", "pdep", "pext", "rorx",
        "sarx", "shlx", "shrx"}

Number = Union[int, Callable[["AdvEmu"], Optional[int]]]


@dataclass
class StubCall:
    va: int
    args: Tuple[int, int, int, int, int, int]
    xmm0: float
    caller: int


@dataclass
class MemEvent:
    pc: int
    addr: int
    size: int
    value: int
    seq: int = 0


@dataclass
class EmuLog:
    reads: List[MemEvent] = field(default_factory=list)
    writes: List[MemEvent] = field(default_factory=list)
    copies: List[Tuple[int, int, int, int]] = field(default_factory=list)   # (caller, dst, src, n)
    calls: List[StubCall] = field(default_factory=list)


def _f32(b: bytes) -> float:
    return struct.unpack("<f", b[:4])[0]


def _p32(x: float) -> bytes:
    import numpy as np
    return np.float32(x).tobytes()


class _Vex:
    """Python execution of the VEX SIMD forms used by the ADV state code."""

    def __init__(self, emu: "AdvEmu") -> None:
        import capstone
        self.emu = emu
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.md.detail = True
        self.y = [bytearray(32) for _ in range(16)]

    # operand helpers -----------------------------------------------------
    def _ymm(self, name: str) -> Tuple[int, int]:
        """(register index, width in bytes) for xmmN / ymmN."""
        if name.startswith("xmm"):
            return int(name[3:]), 16
        if name.startswith("ymm"):
            return int(name[3:]), 32
        raise NotImplementedError(name)

    def _addr(self, ins, op) -> int:
        from capstone import x86_const as cx
        m = op.mem
        a = m.disp
        if m.base != 0:
            bn = ins.reg_name(m.base)
            a += ins.address + ins.size if bn == "rip" else self.emu.r(bn)
        if m.index != 0:
            a += self.emu.r(ins.reg_name(m.index)) * m.scale
        del cx
        return a & 0xFFFFFFFFFFFFFFFF

    def _get(self, ins, op, width: int) -> bytes:
        from capstone import x86_const as cx
        if op.type == cx.X86_OP_REG:
            name = ins.reg_name(op.reg)
            if name.startswith(("xmm", "ymm")):
                i, _w = self._ymm(name)
                return bytes(self.y[i][:width])
            v = self.emu.r(name)
            return (v & ((1 << (8 * width)) - 1)).to_bytes(width, "little")
        if op.type == cx.X86_OP_MEM:
            return self.emu.rd_logged(self._addr(ins, op), width, ins.address)
        if op.type == cx.X86_OP_IMM:
            return (op.imm & ((1 << (8 * width)) - 1)).to_bytes(width, "little")
        raise NotImplementedError(ins.mnemonic)

    def _set_y(self, name: str, data: bytes, vex: bool = True) -> None:
        i, w = self._ymm(name)
        buf = bytearray(32)
        buf[:len(data)] = data
        if w == 16 and not vex:
            buf[16:] = self.y[i][16:]
        self.y[i] = buf

    def _put(self, ins, op, data: bytes) -> None:
        from capstone import x86_const as cx
        if op.type == cx.X86_OP_REG:
            name = ins.reg_name(op.reg)
            if name.startswith(("xmm", "ymm")):
                self._set_y(name, data)
                return
            self.emu.set_gpr(name, int.from_bytes(data, "little"))
            return
        if op.type == cx.X86_OP_MEM:
            self.emu.wr_logged(self._addr(ins, op), data, ins.address)
            return
        raise NotImplementedError(ins.mnemonic)

    def _width(self, ins, op) -> int:
        from capstone import x86_const as cx
        if op.type == cx.X86_OP_REG:
            name = ins.reg_name(op.reg)
            if name.startswith("ymm"):
                return 32
            if name.startswith("xmm"):
                return 16
        return op.size

    # execution -------------------------------------------------------------
    def execute(self, addr: int, raw: bytes) -> int:
        import numpy as np
        from capstone import x86_const as cx
        ins = next(self.md.disasm(raw, addr))
        mn = ins.mnemonic
        ops = ins.operands
        nxt = addr + ins.size
        if mn == "vzeroupper":
            for r in self.y:
                r[16:] = bytes(16)
            return nxt
        if mn in ("vmovups", "vmovaps", "vmovdqu", "vmovdqa", "vmovupd", "vmovapd"):
            w = max(self._width(ins, ops[0]), self._width(ins, ops[1]))
            self._put(ins, ops[0], self._get(ins, ops[1], w))
            return nxt
        if mn in ("vmovss", "vmovsd", "vmovd", "vmovq"):
            n = 4 if mn in ("vmovss", "vmovd") else 8
            if len(ops) == 3:                       # vmovss xmm, xmm, xmm (merge)
                hi = self._get(ins, ops[1], 16)
                lo = self._get(ins, ops[2], n)
                self._put(ins, ops[0], lo + hi[n:])
                return nxt
            src = self._get(ins, ops[1], n)
            if ops[0].type == cx.X86_OP_REG and ins.reg_name(ops[0].reg).startswith("xmm"):
                self._put(ins, ops[0], src + bytes(16 - n))
            else:
                self._put(ins, ops[0], src)
            return nxt
        if mn in ("vxorps", "vxorpd", "vpxor", "vandps", "vandpd", "vpand", "vorps", "vorpd", "vpor",
                  "vandnps", "vpandn"):
            w = self._width(ins, ops[0])
            a = int.from_bytes(self._get(ins, ops[1], w), "little")
            b = int.from_bytes(self._get(ins, ops[2], w), "little")
            if mn in ("vxorps", "vxorpd", "vpxor"):
                r = a ^ b
            elif mn in ("vandps", "vandpd", "vpand"):
                r = a & b
            elif mn in ("vandnps", "vpandn"):
                r = (~a) & b
            else:
                r = a | b
            self._put(ins, ops[0], (r & ((1 << (8 * w)) - 1)).to_bytes(w, "little"))
            return nxt
        if mn == "vcmptrueps" or mn == "vcmptrue_usps":
            w = self._width(ins, ops[0])
            self._put(ins, ops[0], b"\xff" * w)
            return nxt
        if mn in ("vpcmpeqd", "vpcmpeqb", "vpcmpeqw", "vpcmpeqq"):
            w = self._width(ins, ops[0])
            k = {"vpcmpeqb": 1, "vpcmpeqw": 2, "vpcmpeqd": 4, "vpcmpeqq": 8}[mn]
            a = self._get(ins, ops[1], w)
            b = self._get(ins, ops[2], w)
            out = b"".join(b"\xff" * k if a[j:j + k] == b[j:j + k] else bytes(k) for j in range(0, w, k))
            self._put(ins, ops[0], out)
            return nxt
        if mn in ("vpextrw", "vpextrb", "vpextrd", "vpextrq", "vextractps"):
            k = {"vpextrw": 2, "vpextrb": 1, "vpextrd": 4, "vpextrq": 8, "vextractps": 4}[mn]
            src = self._get(ins, ops[1], 16)
            sel = ops[2].imm & (16 // k - 1)
            val = src[sel * k:(sel + 1) * k]
            if ops[0].type == cx.X86_OP_REG:
                self.emu.set_gpr(ins.reg_name(ops[0].reg), int.from_bytes(val, "little"), zero_ext=True)
            else:
                self._put(ins, ops[0], val)
            return nxt
        if mn in ("vpinsrw", "vpinsrb", "vpinsrd", "vpinsrq"):
            k = {"vpinsrw": 2, "vpinsrb": 1, "vpinsrd": 4, "vpinsrq": 8}[mn]
            base = bytearray(self._get(ins, ops[1], 16))
            v = self._get(ins, ops[2], k)
            sel = ops[3].imm & (16 // k - 1)
            base[sel * k:(sel + 1) * k] = v
            self._put(ins, ops[0], bytes(base))
            return nxt
        if mn in ("vcvtsi2ss", "vcvtsi2sd"):
            hi = self._get(ins, ops[1], 16)
            w = ops[2].size
            v = int.from_bytes(self._get(ins, ops[2], w), "little", signed=True)
            if mn == "vcvtsi2ss":
                self._put(ins, ops[0], np.float32(v).tobytes() + hi[4:])
            else:
                self._put(ins, ops[0], np.float64(v).tobytes() + hi[8:])
            return nxt
        if mn in ("vcvttss2si", "vcvtss2si"):
            f = np.frombuffer(self._get(ins, ops[1], 4), np.float32)[0]
            w = ops[0].size
            if not np.isfinite(f) or abs(float(f)) >= 2.0 ** (8 * w - 1):
                v = 1 << (8 * w - 1)
            else:
                v = int(np.trunc(f)) if mn == "vcvttss2si" else int(np.rint(f))
            self.emu.set_gpr(ins.reg_name(ops[0].reg), v & ((1 << (8 * w)) - 1), zero_ext=True)
            return nxt
        if mn in ("vaddss", "vsubss", "vmulss", "vdivss", "vminss", "vmaxss", "vsqrtss"):
            hi = self._get(ins, ops[1], 16)
            a = np.frombuffer(hi[:4], np.float32)[0]
            b = np.frombuffer(self._get(ins, ops[2], 4), np.float32)[0]
            with np.errstate(all="ignore"):
                r = {"vaddss": lambda: a + b, "vsubss": lambda: a - b, "vmulss": lambda: a * b,
                     "vdivss": lambda: a / b if b != 0 else np.float32(np.inf),
                     "vminss": lambda: a if a < b else b, "vmaxss": lambda: a if a > b else b,
                     "vsqrtss": lambda: np.sqrt(b)}[mn]()
            self._put(ins, ops[0], np.float32(r).tobytes() + hi[4:])
            return nxt
        if mn in ("vucomiss", "vcomiss"):
            a = np.frombuffer(self._get(ins, ops[0], 4), np.float32)[0]
            b = np.frombuffer(self._get(ins, ops[1], 4), np.float32)[0]
            fl = self.emu.r("rflags") & ~(0x1 | 0x4 | 0x40 | 0x800 | 0x10 | 0x80)
            if np.isnan(a) or np.isnan(b):
                fl |= 0x45
            elif a < b:
                fl |= 0x1
            elif a == b:
                fl |= 0x40
            self.emu.w("rflags", fl)
            return nxt
        if mn in ("vpmovzxbd", "vpmovzxwd", "vpmovzxbw"):
            src_k, dst_k = {"vpmovzxbd": (1, 4), "vpmovzxwd": (2, 4), "vpmovzxbw": (1, 2)}[mn]
            w = self._width(ins, ops[0])
            n = w // dst_k
            src = self._get(ins, ops[1], n * src_k)
            out = b"".join(int.from_bytes(src[j * src_k:(j + 1) * src_k], "little").to_bytes(dst_k, "little")
                           for j in range(n))
            self._put(ins, ops[0], out)
            return nxt
        if mn == "vpshufd":
            w = self._width(ins, ops[0])
            src = self._get(ins, ops[1], w)
            imm = ops[2].imm
            out = bytearray()
            for lane in range(w // 16):
                L = src[lane * 16:(lane + 1) * 16]
                for j in range(4):
                    s = (imm >> (2 * j)) & 3
                    out += L[s * 4:(s + 1) * 4]
            self._put(ins, ops[0], bytes(out))
            return nxt
        if mn == "vbroadcastss":
            w = self._width(ins, ops[0])
            v = self._get(ins, ops[1], 4)
            self._put(ins, ops[0], v * (w // 4))
            return nxt
        if mn in ("vinsertf128", "vinserti128"):
            a = bytearray(self._get(ins, ops[1], 32))
            b = self._get(ins, ops[2], 16)
            sel = ops[3].imm & 1
            a[sel * 16:(sel + 1) * 16] = b
            self._put(ins, ops[0], bytes(a))
            return nxt
        if mn in ("vextractf128", "vextracti128"):
            a = self._get(ins, ops[1], 32)
            sel = ops[2].imm & 1
            self._put(ins, ops[0], a[sel * 16:(sel + 1) * 16])
            return nxt
        raise NotImplementedError(f"VEX {mn} {ins.op_str} at {addr:#x}")


class AdvEmu:
    """unicorn x86-64 machine with the eboot mapped at its link addresses."""

    GPR = ("rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp", "r8", "r9", "r10", "r11", "r12",
           "r13", "r14", "r15", "rip", "rflags")
    _SUB = {"eax": ("rax", 4), "ebx": ("rbx", 4), "ecx": ("rcx", 4), "edx": ("rdx", 4),
            "esi": ("rsi", 4), "edi": ("rdi", 4), "ebp": ("rbp", 4), "esp": ("rsp", 4),
            "ax": ("rax", 2), "bx": ("rbx", 2), "cx": ("rcx", 2), "dx": ("rdx", 2), "si": ("rsi", 2),
            "di": ("rdi", 2), "al": ("rax", 1), "bl": ("rbx", 1), "cl": ("rcx", 1), "dl": ("rdx", 1),
            "sil": ("rsi", 1), "dil": ("rdi", 1)}
    for _k in range(8, 16):
        _SUB[f"r{_k}d"] = (f"r{_k}", 4)
        _SUB[f"r{_k}w"] = (f"r{_k}", 2)
        _SUB[f"r{_k}b"] = (f"r{_k}", 1)

    def __init__(self, eboot: Union[str, Path]) -> None:
        from unicorn import UC_ARCH_X86, UC_HOOK_CODE, UC_MODE_64, Uc
        from unicorn import x86_const as ux
        self._ux = ux
        d = Path(eboot).read_bytes()
        self.image = d
        self.uc = uc = Uc(UC_ARCH_X86, UC_MODE_64)
        uc.mem_map(0, 0x440000)
        uc.mem_write(0, d[TEXT_OFF:TEXT_OFF + TEXT_LEN])
        uc.mem_write(RELRO_VA, d[RELRO_OFF:RELRO_OFF + RELRO_LEN])
        uc.mem_map(DATA_VA, (DATA_MEMSZ + 0xFFFF) & ~0xFFFF)
        uc.mem_write(DATA_VA, d[DATA_OFF:DATA_OFF + DATA_LEN])
        uc.mem_map(STACK, STACK_SIZE)
        uc.mem_map(HEAP, HEAP_SIZE)
        uc.mem_map(SCRATCH, SCRATCH_SIZE)
        uc.mem_map(RET, 0x2000)
        uc.mem_write(RET, b"\xf4")
        uc.mem_write(GUARD, struct.pack("<Q", 0x5A5A5A5A12345678))
        uc.mem_write(STACK_GUARD_GOT, struct.pack("<Q", GUARD))
        self._heap = HEAP
        self.vex = _Vex(self)
        self.patched: Dict[int, bytes] = {}
        self.stubs: Dict[int, Number] = {}
        self.log: Optional[EmuLog] = None
        self._log_r: Sequence[Tuple[int, int]] = ()
        self._log_w: Sequence[Tuple[int, int]] = ()
        self._hooks: List[int] = []
        self._seq = 0
        uc.hook_add(UC_HOOK_CODE, self._plt, begin=PLT_LO, end=PLT_HI)
        self._stub_hook = None
        self._code_hook = None

    # -- registers / memory ---------------------------------------------------
    def r(self, name: str) -> int:
        name = name.lower()
        if name in self._SUB:
            full, w = self._SUB[name]
            return self.r(full) & ((1 << (8 * w)) - 1)
        return int(self.uc.reg_read(getattr(self._ux, "UC_X86_REG_" + name.upper())))

    def w(self, name: str, v: int) -> None:
        self.uc.reg_write(getattr(self._ux, "UC_X86_REG_" + name.upper()), v & 0xFFFFFFFFFFFFFFFF)

    def set_gpr(self, name: str, v: int, zero_ext: bool = False) -> None:
        name = name.lower()
        if name in self._SUB:
            full, w = self._SUB[name]
            if w == 4 or zero_ext:
                self.w(full, v & ((1 << (8 * w)) - 1) if not zero_ext else v)
            else:
                old = self.r(full)
                m = (1 << (8 * w)) - 1
                self.w(full, (old & ~m) | (v & m))
            return
        self.w(name, v)

    def rd(self, a: int, n: int) -> bytes:
        return bytes(self.uc.mem_read(a, n))

    def wr(self, a: int, b: bytes) -> None:
        self.uc.mem_write(a, bytes(b))

    def wr_logged(self, a: int, b: bytes, pc: int) -> None:
        """Memory write from Python-executed instructions (VEX, PLT stubs), logged like a
        guest write."""
        if self.log is not None:
            for lo, hi in self._log_w:
                if lo <= a < hi or lo < a + len(b) <= hi:
                    self.log.writes.append(MemEvent(pc, a, len(b), int.from_bytes(b, "little"), self._next()))
                    break
        self.wr(a, b)

    def rd_logged(self, a: int, n: int, pc: int) -> bytes:
        b = self.rd(a, n)
        if self.log is not None:
            for lo, hi in self._log_r:
                if lo <= a < hi:
                    self.log.reads.append(MemEvent(pc, a, n, int.from_bytes(b, "little"), self._next()))
                    break
        return b

    def u8(self, a: int) -> int:
        return self.rd(a, 1)[0]

    def u16(self, a: int) -> int:
        return struct.unpack("<H", self.rd(a, 2))[0]

    def i16(self, a: int) -> int:
        return struct.unpack("<h", self.rd(a, 2))[0]

    def u32(self, a: int) -> int:
        return struct.unpack("<I", self.rd(a, 4))[0]

    def i32(self, a: int) -> int:
        return struct.unpack("<i", self.rd(a, 4))[0]

    def u64(self, a: int) -> int:
        return struct.unpack("<Q", self.rd(a, 8))[0]

    # -- VEX patching -------------------------------------------------------
    def patch_vex(self, start: int, end: int) -> int:
        """Replace every VEX SIMD instruction in [start, end) by ud2 (executed by _Vex)."""
        import capstone
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        code = self.image[start + TEXT_OFF:end + TEXT_OFF]
        n = 0
        for a, size, mn, _ops in md.disasm_lite(code, start):
            b0 = code[a - start]
            if b0 in (0xC4, 0xC5) and mn not in _BMI and a not in self.patched:
                self.patched[a] = code[a - start:a - start + size]
                self.uc.mem_write(a, b"\x0f\x0b")
                n += 1
        return n

    def patch_functions(self, funcs: Sequence[int]) -> None:
        from eboot.disasm import eh_function_starts
        from eboot.selfelf import Eboot
        if not hasattr(self, "_starts"):
            import bisect
            from tsuki import game_dir
            g = game_dir()
            eb = Eboot.open(g / "eboot.bin") if g else None
            if eb is None:
                raise FileNotFoundError("eboot.bin")
            self._starts = eh_function_starts(eb)
            self._bisect = bisect
        for f in funcs:
            i = self._bisect.bisect_right(self._starts, f) - 1
            st = self._starts[i]
            en = self._starts[i + 1] if i + 1 < len(self._starts) else st + 0x1000
            self.patch_vex(st, en)

    # -- stubs ----------------------------------------------------------------
    def stub(self, va: int, value: Number = 0) -> None:
        from unicorn import UC_HOOK_CODE
        self.stubs[va] = value
        self.uc.hook_add(UC_HOOK_CODE, self._stub_hit, begin=va, end=va)

    def _return(self, rax: Optional[int]) -> None:
        if rax is not None:
            self.w("rax", rax)
        rsp = self.r("rsp")
        ret = self.u64(rsp)
        self.w("rsp", rsp + 8)
        self.w("rip", ret)

    def _stub_hit(self, uc: object, addr: int, size: int, _: object) -> None:
        if addr not in self.stubs:
            return
        rsp = self.r("rsp")
        caller = self.u64(rsp)
        if self.log is not None:
            args = tuple(self.r(x) for x in ("rdi", "rsi", "rdx", "rcx", "r8", "r9"))
            self.log.calls.append(StubCall(addr, args, _f32(bytes(self.vex.y[0][:4])), caller))  # type: ignore[arg-type]
        v = self.stubs[addr]
        self._return(v(self) if callable(v) else v)

    # -- imports --------------------------------------------------------------
    def _plt(self, uc: object, addr: int, size: int, _: object) -> None:
        name = PLT_NAMES.get(addr)
        if name is None:
            raise RuntimeError(f"unhandled import stub at {addr:#x}")
        rdi, rsi, rdx = self.r("rdi"), self.r("rsi"), self.r("rdx")
        caller = self.u64(self.r("rsp"))
        rax = 0
        if name in ("memcpy", "memmove"):
            if rdx:
                data = self.rd_logged(rsi, rdx, caller)
                self.wr_logged(rdi, data, caller)
            if self.log is not None:
                self.log.copies.append((caller, rdi, rsi, rdx))
            rax = rdi
        elif name == "memset":
            if rdx:
                self.wr_logged(rdi, bytes([rsi & 0xFF]) * rdx, caller)
            if self.log is not None:
                self.log.copies.append((caller, rdi, -1 - (rsi & 0xFF), rdx))
            rax = rdi
        elif name in ("new", "malloc"):
            a = (self._heap + 15) & ~15
            self._heap = a + max(rdi, 16)
            rax = a
        elif name in ("delete", "free"):
            rax = 0
        elif name == "ldexpf":
            _ldexpf(self)
            self._return(None)
            return
        elif name == "stack_chk_fail":
            raise RuntimeError("__stack_chk_fail")
        elif name == "strlen":
            n = 0
            while self.u8(rdi + n):
                n += 1
            rax = n
        elif name == "memcmp":
            a, b = self.rd(rdi, rdx), self.rd(rsi, rdx)
            rax = 0 if a == b else (1 if a > b else 0xFFFFFFFF)
        elif name == "strcpy":
            n = 0
            while self.u8(rsi + n):
                n += 1
            self.wr(rdi, self.rd(rsi, n + 1))
            rax = rdi
        elif name == "strcmp":
            i = 0
            while True:
                a, b = self.u8(rdi + i), self.u8(rsi + i)
                if a != b or a == 0:
                    rax = (a - b) & 0xFFFFFFFF
                    break
                i += 1
        self._return(rax)

    # -- logging ----------------------------------------------------------------
    def start_log(self, reads: Sequence[Tuple[int, int]] = (), writes: Sequence[Tuple[int, int]] = ()) -> EmuLog:
        from unicorn import UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE
        self.log = EmuLog()
        self._log_r, self._log_w = tuple(reads), tuple(writes)
        for lo, hi in self._log_r:
            self._hooks.append(self.uc.hook_add(UC_HOOK_MEM_READ, self._on_read, begin=lo, end=hi - 1))
        for lo, hi in self._log_w:
            self._hooks.append(self.uc.hook_add(UC_HOOK_MEM_WRITE, self._on_write, begin=lo, end=hi - 1))
        return self.log

    def stop_log(self) -> EmuLog:
        for h in self._hooks:
            self.uc.hook_del(h)
        self._hooks = []
        log, self.log = self.log, None
        assert log is not None
        return log

    def _on_read(self, uc: object, acc: int, a: int, size: int, v: int, _: object) -> None:
        if self.log is not None:
            val = int.from_bytes(self.rd(a, size), "little")
            self.log.reads.append(MemEvent(self.r("rip"), a, size, val, self._next()))

    def _on_write(self, uc: object, acc: int, a: int, size: int, v: int, _: object) -> None:
        if self.log is not None:
            self.log.writes.append(MemEvent(self.r("rip"), a, size, v & ((1 << (8 * size)) - 1), self._next()))

    def _next(self) -> int:
        self._seq += 1
        return self._seq

    # -- calls ----------------------------------------------------------------------
    def call(self, va: int, *args: int, xmm0: Optional[float] = None, count: int = 5_000_000) -> int:
        from unicorn import UcError
        from unicorn.unicorn_const import UC_ERR_INSN_INVALID
        sp = STACK + STACK_SIZE - 0x2000
        self.wr(sp, struct.pack("<Q", RET))
        self.w("rsp", sp)
        self.w("rbp", sp + 0x1000)
        for name, v in zip(("rdi", "rsi", "rdx", "rcx", "r8", "r9"), args):
            self.w(name, v)
        if xmm0 is not None:
            self.vex.y[0] = bytearray(_p32(xmm0) + bytes(28))
        pc = va
        budget = count
        while True:
            try:
                self.uc.emu_start(pc, RET, count=budget)
            except UcError as e:
                rip = self.r("rip")
                if e.errno == UC_ERR_INSN_INVALID and rip in self.patched:
                    pc = self.vex.execute(rip, self.patched[rip])
                    continue
                raise RuntimeError(f"{e} at rip {rip:#x}") from None
            break
        if self.r("rip") != RET:
            raise RuntimeError(f"stopped at {self.r('rip'):#x} (instruction budget)")
        return self.r("rax")


# ---------------------------------------------------------------------------
# checks of the tsuki.misc_adv_system_state maps
# ---------------------------------------------------------------------------
PUSH_VA = 0x7AF90
RESTORE_VA = 0x7C6D0
REC = 0x3A10
_RING_K = 5
_SCRIPT_BUF = SCRATCH + 0x100000
SCRIPT_PTR = 0x16543B0          # current statement pointer (written at 0x7C7DE)


def _eboot_path(eboot: Union[str, Path, None]) -> Path:
    if eboot is not None:
        return Path(eboot)
    from tsuki import game_dir
    g = game_dir()
    if g is None:
        raise FileNotFoundError("game folder not found")
    return g / "eboot.bin"


def _ldexpf(emu: "AdvEmu") -> None:
    import numpy as np
    x = np.frombuffer(bytes(emu.vex.y[0][:4]), np.float32)[0]
    e = emu.r("edi")
    e = e - (1 << 32) if e >> 31 else e
    emu.vex.y[0] = bytearray(np.float32(np.ldexp(x, e)).tobytes() + bytes(28))


def _random_world(emu: "AdvEmu", seed: int) -> random.Random:
    """Random bss and the data windows the ADV state code reads; push gates open."""
    rng = random.Random(seed)
    lo, hi = 0xDD6000, (DATA_VA + DATA_MEMSZ) & ~0xFFF
    emu.wr(lo, rng.randbytes(hi - lo))
    emu.wr(0x66C578, rng.randbytes(0x140))
    emu.wr(0x86AA00, rng.randbytes(0x100))
    emu.wr(0x174DE48, b"\0")                       # not in scan mode
    emu.wr(0x164B912, b"\0\0")                     # F929 = 0 (push gate)
    emu.wr(0x164BA6C, b"\0\0")                     # F1102 = 0 (push gate)
    emu.wr(0x16541F4, b"\0")                       # push gate
    emu.wr(0x120A6E0, struct.pack("<Q", SCRATCH))  # history_ptr
    emu.wr(0x174D640, struct.pack("<Q", _SCRIPT_BUF))
    emu.wr(0x120A6EA, struct.pack("<h", rng.randrange(0x7FFF)))   # serial counter (0..0x7FFF in play)
    emu.wr(0x164B1C0, struct.pack("<h", 0))        # scene-stack top
    return rng


_SCRIPT_MAX = 0x100000          # room for a decoded script at _SCRIPT_BUF (below the image area)


def run_push(eboot: Union[str, Path, None] = None, seed: int = 1, mode: int = 0,
             script: Optional[bytes] = None, pointers: Optional[Tuple[int, int]] = None,
             script_no: Optional[int] = None, f_set: Optional[Dict[int, int]] = None,
             blocking: Optional[int] = None,
             ) -> Tuple["AdvEmu", bytes, bytes, Dict[str, int]]:
    """Run History_Push on random RAM; returns (emu, record before, record after, args).
    Optional: a decoded script copied to the script buffer, the two pointer arguments as
    offsets in it, the current script number (0x86AA18), F values set before the push, and
    the effect-blocking bits returned by the stubbed 0x5C680."""
    emu = AdvEmu(_eboot_path(eboot))
    emu.patch_functions([PUSH_VA])
    rng = _random_world(emu, seed)
    emu.wr(0x120A6EE, struct.pack("<h", _RING_K))
    emu.wr(0x120A6F2, struct.pack("<h", 300))
    emu.wr(SCRATCH, rng.randbytes(REC * (_RING_K + 2)))
    for k in range(_RING_K + 2):                    # ring clear invariant: record +0 = its index
        emu.wr(SCRATCH + k * REC, struct.pack("<i", k))
    bits_a, bits_b, w = rng.getrandbits(14), rng.getrandbits(14), rng.getrandbits(1)
    if blocking is not None:
        bits_b = blocking
    emu.stub(0x5C660, lambda e: (bits_a >> e.r("rdi")) & 1)
    emu.stub(0x5C680, lambda e: (bits_b >> e.r("rdi")) & 1)
    emu.stub(0x53620, w)
    a, b = rng.randrange(0x100000), rng.randrange(0x100000)
    if script is not None:
        if len(script) > _SCRIPT_MAX:
            raise ValueError(f"script of 0x{len(script):x} bytes does not fit the buffer")
        emu.wr(_SCRIPT_BUF, script)
    if pointers is not None:
        a, b = pointers
    if script_no is not None:
        emu.wr(0x86AA18, struct.pack("<H", script_no))
    for idx, v in (f_set or {}).items():
        emu.wr(0x164B1D0 + 2 * idx, struct.pack("<H", v & 0xFFFF))
    before = emu.rd(SCRATCH + _RING_K * REC, REC)
    emu.call(PUSH_VA, _SCRIPT_BUF + a, _SCRIPT_BUF + b, mode)
    after = emu.rd(SCRATCH + _RING_K * REC, REC)
    return emu, before, after, {"a": a, "b": b, "bits_a": bits_a, "bits_b": bits_b, "w": w, "mode": mode}


def verify_push(eboot: Union[str, Path, None] = None, seeds: Sequence[int] = (1, 2, 3)) -> List[str]:
    """Compare History_Push's output with HISTORY_FIELDS; returns mismatch descriptions."""
    from tsuki import misc_adv_system_state as m
    lists = m.read_history_lists(eboot)
    bad: List[str] = []
    for seed in seeds:
        mode = seed % 14
        emu, before, rec, args = run_push(eboot, seed, mode)
        for f in m.HISTORY_FIELDS:
            got = rec[f.off:f.off + f.size]
            if f.push_src is not None:
                want = emu.rd(f.push_src, f.size)
                if got != want:
                    bad.append(f"seed {seed}: {f.name} != RAM 0x{f.push_src:x}")
            elif f.name.startswith("unused") or f.name in ("ring_index", "var_3640", "var_165420a",
                                                           "se_arr_e_stale"):
                if got != before[f.off:f.off + f.size]:
                    bad.append(f"seed {seed}: {f.name} was written")
        serial = struct.unpack_from("<i", rec, 4)[0]
        if serial < 0 or emu.i16(0x120A6EA) != (serial + 1 if serial + 1 < 0x8000 else 0):
            bad.append(f"seed {seed}: serial {serial}")
        off_a, off_b = struct.unpack_from("<QQ", rec, 8)
        if (off_a, off_b) != (args["a"], args["b"]) or struct.unpack_from("<H", rec, 0x24)[0] != mode:
            bad.append(f"seed {seed}: offsets / mode")
        if struct.unpack_from("<iHH", rec, 0x3630) != (-1, 0xFFFF, args["bits_a"]):
            bad.append(f"seed {seed}: voice / effect bits")
        if struct.unpack_from("<HH", rec, 0x3638) != (args["bits_b"], args["w"]):
            bad.append(f"seed {seed}: blocking bits / WNTY bit")
        for i in range(20):
            if rec[0x3642 + i] != emu.u8(0x1654270 + 2 * i) or rec[0x3657 + i] != emu.u8(0x16542A0 + 2 * i):
                bad.append(f"seed {seed}: se byte arrays [{i}]")
            if emu.u8(0x1654330 + i) != before[0x36BC + i]:
                bad.append(f"seed {seed}: 0x1654330[{i}] not overwritten from the stale record")
            for a, n in ((0, 3), (4, 2), (8, 0xE)):
                o = 0x36D0 + 0x18 * i + a
                if rec[o:o + n] != emu.rd(0x16543F0 + 0x18 * i + a, n):
                    bad.append(f"seed {seed}: channel {i} +{a}")
        if rec[0x38FE] != 0:
            bad.append(f"seed {seed}: text not cleared")
        f995 = emu.u8(m.VAR_F + 2 * 995)                 # 0x7B283 / 0x7B2A2: scene +0x19A3 = F995 & 0xFF
        if emu.u8(0x1651A63) != f995 or rec[0x58 + 0x19A3] != f995:
            bad.append(f"seed {seed}: scene_state +0x19A3 != low byte of F995")
        u16, bl, s8 = lists
        for k, idx in enumerate(u16):
            if rec[0x34AA + 2 * k:0x34AC + 2 * k] != emu.rd(m.VAR_F + 2 * idx, 2):
                bad.append(f"seed {seed}: u16 list {k}")
        for k, idx in enumerate(s8):
            if 0 <= idx <= 0x4B0 and rec[0x3501 + k] != emu.u8(m.VAR_F + 2 * idx):
                bad.append(f"seed {seed}: s8 list {k}")
        for k, idx in enumerate(bl):
            if 0 <= idx <= 0x4B0:
                bit = (rec[0x34FA + (k >> 3)] >> (k & 7)) & 1
                if bit != (1 if emu.u16(m.VAR_F + 2 * idx) else 0):
                    bad.append(f"seed {seed}: bool list {k}")
        nxt = struct.unpack("<i", emu.rd(SCRATCH + (_RING_K + 1) * REC + 4, 4))[0]
        if nxt != -1 or emu.i16(0x120A6EC) != _RING_K or emu.i16(0x120A6EE) != _RING_K + 1:
            bad.append(f"seed {seed}: ring indices")
    return bad


RESUME_OFF = 0x40                # record +0x10 used by run_restore (kind 0 re-display resumes here)


def run_restore(eboot: Union[str, Path, None] = None, seed: int = 11, mode: int = 1,
                from_history: int = 1, snapshot: bool = False,
                ) -> Tuple["AdvEmu", bytes]:
    """History_Restore(3, from_history) of a random record with every non-import callee
    stubbed (returns 0).  The record's cmd_offset is 0 (script_ptr -> the `_ZM00a01(TEXT@e);`
    statement at the buffer start) and its resume_offset RESUME_OFF.  With ``snapshot`` the
    record's effect_blocking_bits are 0 and F823 = 0, so the restore ends with the
    Staging_SetSnapshot tail call (0x73980, stubbed and logged)."""
    import capstone
    path = _eboot_path(eboot)
    emu = AdvEmu(path)
    emu.patch_functions([RESTORE_VA])
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    code = emu.image[RESTORE_VA + TEXT_OFF:0x7DD10 + TEXT_OFF]
    for _a, _s, mn, ops in md.disasm_lite(code, RESTORE_VA):
        if mn in ("call", "jmp") and ops.startswith("0x"):
            t = int(ops, 16)
            if not RESTORE_VA <= t < 0x7DD10 and t not in PLT_NAMES and t not in emu.stubs:
                emu.stub(t, 0)
    rng = _random_world(emu, seed)
    rec = bytearray(rng.randbytes(REC))
    struct.pack_into("<iQQH", rec, 4, 7, 0, RESUME_OFF, 5)
    struct.pack_into("<H", rec, 0x24, mode)
    struct.pack_into("<H", rec, 0x3638, 0 if snapshot else 1)   # blocking bits: 0 allows the snapshot
    if snapshot:
        lists = _lists(eboot)
        k = lists[2].index(823)
        rec[0x3501 + k] = 0                          # F823 = 0 after the s8 list (first message)
    emu.wr(0x86AA18, struct.pack("<H", 5))           # same script: no reload
    # cmd_offset points at the byte before the statement's '_' (Script_Step passes name - 2)
    emu.wr(_SCRIPT_BUF, RESTORE_STMT + bytes(64))
    emu.wr(SCRATCH + 3 * REC, bytes(rec))
    emu.pre = {a: emu.rd(a, n) for a, n in _PRE}      # type: ignore[attr-defined]
    emu.log = EmuLog()                               # log stubbed calls (no memory hooks)
    emu.call(RESTORE_VA, 3, from_history)
    return emu, bytes(rec)


RESTORE_STMT = b";_ZM00a01(TEXT@e);"
_PRE = ((0x174D9A8, 1), (0x86AA24, 4), (0x1653E00, 8), (0x1653E70, 8), (0x16541F4, 1))


_LISTS_CACHE: Dict[str, Tuple[List[int], List[int], List[int]]] = {}


def _lists(eboot: Union[str, Path, None]) -> Tuple[List[int], List[int], List[int]]:
    from tsuki import misc_adv_system_state as m
    key = str(eboot)
    if key not in _LISTS_CACHE:
        _LISTS_CACHE[key] = m.read_history_lists(eboot)
    return _LISTS_CACHE[key]


def kind0_scene_state(rec: bytes, f931: int) -> bytes:
    """scene_state after a kind-0 restore: the record's +0x58 block with +0x1984 = F931 when
    F931 >= 0 (0x7D716..0x7D736) and +0x1982 = +0x1984 (0x7D860, again at 0x7DBBE)."""
    ss = bytearray(rec[0x58:0x58 + 0x1A08])
    if f931 >= 0:
        struct.pack_into("<h", ss, 0x1984, f931)
    ss[0x1982:0x1984] = ss[0x1984:0x1986]
    return bytes(ss)


def verify_restore(eboot: Union[str, Path, None] = None, seeds: Sequence[int] = (11, 12),
                   modes: Sequence[int] = (1, 2, 3, 6, 8, 9), from_history: Sequence[int] = (1,),
                   ) -> List[str]:
    """Compare History_Restore's RAM effects with RESTORE_COPIES / the F lists, and for kind 0
    with the kind-0 tail of adv_state.md 3.1 (both values of from_history)."""
    from tsuki import misc_adv_system_state as m
    lists = _lists(eboot)
    bad: List[str] = []
    for seed in seeds:
        for mode in modes:
            for fh in from_history:
                tag = f"seed {seed} kind {mode} from_history {fh}"
                emu, rec = run_restore(eboot, seed, mode, from_history=fh)
                fv = m.history_f_values(rec, lists=lists)
                for off, n, va, how in m.RESTORE_COPIES:
                    raw = rec[off:off + n]
                    if how == "copy":
                        want = raw
                    elif how == "sx8_16":
                        want = (struct.unpack("<b", raw)[0] & 0xFFFF).to_bytes(2, "little")
                    else:
                        want = (struct.unpack("<h", raw)[0] & 0xFFFFFFFF).to_bytes(4, "little")
                    if mode == 0 and fh == 1 and va in (0x1654344, 0x1654348):
                        continue                                  # re-display: checked below
                    if emu.rd(va, len(want)) != want:
                        bad.append(f"{tag}: rec+0x{off:x} -> 0x{va:x}")
                if mode == 0:
                    f931 = struct.unpack("<h", (fv.get(931, 0) & 0xFFFF).to_bytes(2, "little"))[0]
                    want_ss = kind0_scene_state(rec, f931)
                else:
                    want_ss = rec[0x58:0x58 + 0x1A08]
                if emu.rd(m.SCENE_STATE, 0x1A08) != want_ss:
                    bad.append(f"{tag}: scene_state")
                if mode in (1, 9) and emu.rd(m.SCENE_COMMITTED, 0x1A08) != rec[0x1A60:0x3468]:
                    bad.append(f"{tag}: committed copy")
                win2 = rec[0x38E2:0x38FE]
                if mode != 0:
                    if emu.u8(0x164FD40) != 0:
                        bad.append(f"{tag}: 0x164FD40 not cleared")
                elif fh == 0:
                    if emu.rd(0x164FD40, 0x1C) != win2:
                        bad.append(f"{tag}: 0x164FD40 != rec+0x38E2")
                else:
                    if emu.u8(0x164FD40) != 0 or emu.rd(0x164FD60, 0x1C) != win2:
                        bad.append(f"{tag}: rec+0x38E2 not moved to 0x164FD60")
                for idx, val in fv.items():
                    if idx == 967:      # 0x7D513: kept only when it equals scene word 0x1651A66 + 1, else 0
                        sw = struct.unpack_from("<h", emu.rd(0x1651A66, 2))[0]
                        val = val if (val & 0xFFFF) == ((sw + 1) & 0xFFFF) or not (0 < struct.unpack("<h", val.to_bytes(2, "little"))[0]) else 0
                    if emu.u16(m.VAR_F + 2 * idx) != val:
                        bad.append(f"{tag}: F{idx}")
                if emu.i16(0x120A6EC) != 3 or emu.i16(0x120A6EE) != 4:
                    bad.append(f"{tag}: ring indices")
                base = _SCRIPT_BUF
                want_ptr = base + (RESUME_OFF if (mode == 0 and fh == 1) else 0)
                if emu.u64(SCRIPT_PTR) != want_ptr:
                    bad.append(f"{tag}: script_ptr 0x{emu.u64(SCRIPT_PTR) - base:x}")
                pre = emu.pre                                     # type: ignore[attr-defined]
                if mode == 0:
                    called = [c.args[0] for c in emu.log.calls if c.va == 0x75CD0]   # type: ignore[union-attr]
                    if (pre[0x86AA24] != bytes(4)) != (called == [1]) or emu.rd(0x86AA24, 4) != bytes(4):
                        bad.append(f"{tag}: 0x86AA24 / 0x75CD0(1)")
                    if emu.u8(0x174DE00) != 0:
                        bad.append(f"{tag}: 0x174DE00")
                    text = RESTORE_STMT[10:RESTORE_STMT.index(b"@")]
                    if fh == 0:
                        if emu.u16(0x1653AE0) != 0 or emu.rd(0x174D9A8, 1) != pre[0x174D9A8]:
                            bad.append(f"{tag}: re-display state touched")
                    else:
                        tb, tl = struct.unpack_from("<hh", rec, 0x1A)
                        e70 = struct.unpack("<Q", pre[0x1653E70])[0] | 1
                        e00 = struct.unpack("<Q", pre[0x1653E00])[0] | 0x140 | e70      # '@e' ends the text
                        f967 = struct.unpack("<h", (fv.get(967, 0) & 0xFFFF).to_bytes(2, "little"))[0]
                        sw = struct.unpack_from("<h", emu.rd(0x1651A66, 2))[0]
                        if f967 > 0 and f967 != sw + 1:
                            e00 |= 0x200                          # F967 reset, 0x7D53F (common part)
                        checks = [
                            (emu.u16(0x1653AE0) == len(text), "0x1653AE0 = text length"),
                            (emu.rd(0x16537E0, len(text) + 1) == text + b"\0", "0x16537E0 = statement text"),
                            (emu.rd(0x1653AF0, len(text)) == text, "0x1653AF0 copy"),
                            (emu.u8(0x174D9A8) == 1, "0x174D9A8 = 1"),
                            (emu.u16(0x1654208) == 0, "0x1654208 = 0"),
                            (emu.u32(0x16541D8) == 1, "0x16541D8 = 1"),
                            (emu.i32(0x16541FC) == -1 and emu.i32(0x1654200) == -1, "0x16541FC / 0x1654200 = -1"),
                            (emu.u64(0x1653E70) == e70, "0x1653E70 |= 1"),
                            (emu.u64(0x1653E00) == e00, "0x1653E00 |= 0x140 | 0x1653E70"),
                            (emu.u8(0x16541F4) == 0, "0x16541F4 = 0"),
                            (struct.unpack("<iiii", emu.rd(0x1654344, 16)) == (tb, tl, tb, tl),
                             "0x1654344 / 0x165434C = block, 0x1654348 / 0x1654350 = line"),
                        ]
                        bad += [f"{tag}: {what}" for ok, what in checks if not ok]
    return bad


def config_recommended(eboot: Union[str, Path, None] = None) -> bytes:
    """Config block after 'Apply recommended settings' (0x3EAC0) on the boot block."""
    emu = AdvEmu(_eboot_path(eboot))
    emu.patch_functions([0x3EAC0])
    emu.stub(0x3E4A0, 0)
    emu.call(0x3EAC0)
    return emu.rd(0x66C578, 0x140)


def config_from_menu(eboot: Union[str, Path, None] = None) -> List[Tuple[int, int, int, int]]:
    """(page, item, config offset, width) written by Config_FromMenu 0x3E6E0, found by making
    the item getter 0x3D440 return a value unique to each call and locating it in the block."""
    emu = AdvEmu(_eboot_path(eboot))
    emu.patch_functions([0x3E6E0])
    calls: List[Tuple[int, int]] = []

    def getter(e: "AdvEmu") -> int:
        calls.append((e.r("edi") & 0xFF, e.r("esi") & 0xFFFF))
        return 0x2000 + len(calls)               # never 1, so mute toggles read as "off"

    emu.stub(0x3D440, getter)
    emu.wr(0x66C578, bytes(0x140))
    emu.call(0x3E6E0)
    blk = emu.rd(0x66C578, 0x140)
    out: List[Tuple[int, int, int, int]] = []
    for k, (page, item) in enumerate(calls, start=1):
        code = 0x2000 + k
        hit = [o for o in range(0, 0x140 - 1, 2) if struct.unpack_from("<H", blk, o)[0] == code]
        if hit:
            out.append((page, item, hit[0], 2))
            continue
        hit = [o for o in range(0x140) if blk[o] == code & 0xFF
               and not any(o in (q, q + 1) for _p, _i, q, w in out if w == 2)]
        if hit:
            out.append((page, item, hit[0], 1))
    return out


@dataclass
class SlotImage:
    image: bytes                 # 4 MiB save image written by SaveImage_Commit
    record: bytes                # the pushed history record (state A source)
    record_b: Optional[bytes]    # the scene-stack top record (state B source) or None
    slot: int
    script_no: int
    stmt_offset: int             # offset of the `_ZM` statement's '_' in the decoded script
    readmax: bytes               # RAM readmax copied into the system block


def _first_zm(raw: bytes) -> Tuple[int, int, int, int]:
    """(offset of '_', end after ';', block, line) of the first `_ZM` statement of a script."""
    import re
    from tsuki.script import marker_hex
    mt = re.search(rb"(?:^|;)[^_;]*(_ZM(.....)[^;]*;)", raw, re.S)
    if mt is None:
        raise ValueError("no _ZM statement")
    hexid = mt.group(2)
    return mt.start(1), mt.end(1), marker_hex(hexid[:3]), marker_hex(hexid[3:])


def engine_slot_image(eboot: Union[str, Path, None] = None, seed: int = 5, slot: int = 7,
                      date: Tuple[int, int, int, int, int, int] = (2026, 10, 25, 12, 34, 56),
                      script_no: Optional[int] = None, with_state_b: bool = False, quick: bool = False,
                      ) -> SlotImage:
    """A save image made by the game's own code.  History_Push (kind 0) of the first `_ZM`
    statement of script ``script_no`` (pointers as Script_Step passes them: name - 2 and the
    statement end; F1118 = 0 and F823 = 1 before the push) -> Staging_SetSnapshot(record, n,
    recB) with n = 1 and recB = a scene-stack top record when ``with_state_b`` -> Slot_Store(slot)
    0x737D0, or with ``quick`` QuickSave_Store 0x73A70 (slot 139; its commit 0xA9450 is
    stubbed) -> SaveImage_Commit(1, or 0 for the quick save).  RAM readmax is zero except
    readmax[block of that message] = its line."""
    from tsuki.script import AllScr
    with AllScr() as scr:
        if script_no is None:                       # first script with a message that fits the buffer
            script_no = next(i for i in range(len(scr))
                             if len(scr.raw(i)) <= _SCRIPT_MAX and b"_ZM" in scr.raw(i))
        raw = scr.raw(script_no)
    us, end, block, line = _first_zm(raw)
    emu, _before, rec, _args = run_push(eboot, seed, 0, script=raw, pointers=(us - 1, end),
                                        script_no=script_no, f_set={1118: 0, 823: 1}, blocking=0)
    emu.patch_functions([0x73980, 0x737D0, 0x73A70, 0x76500, 0x6FEC0, 0xEACF0, 0x419D0, 0xC2F60])
    img = SCRATCH + 0x200000
    emu.wr(img, bytes(0x400000))
    emu.wr(0x1765788, struct.pack("<Q", img))
    emu.wr(0xE24600, bytes(140 * 0x7108))           # all slots empty
    emu.wr(0x12031E0, bytes(0x7108))
    rmax = bytearray(0xFB4)
    if 0 <= block < len(rmax):
        rmax[block] = line & 0xFF
    emu.wr(0x17DA278, bytes(rmax))

    def rtc(e: "AdvEmu") -> int:
        y, mo, d, h, mi, sec = date
        e.wr(e.r("rdi"), struct.pack("<HHBBBBBB", 0, y, mo, d, h, mi, sec, 0))
        return 0
    emu.stub(0xEAD50, rtc)
    rec_b: Optional[bytes] = None
    stack_rec = 0
    if with_state_b:
        rng = random.Random(seed + 1000)
        rb = bytearray(rng.randbytes(REC))
        first = raw.find(b";_")                       # a statement start (the ';' before its '_')
        struct.pack_into("<iiQQH", rb, 0, 2, 41, first, first, script_no)
        struct.pack_into("<H", rb, 0x24, 6)
        rec_b = bytes(rb)
        stack_rec = 0x120A700                         # scene_stack[0], top = 0
        emu.wr(stack_rec, rec_b)
        emu.wr(0x164B1C0, struct.pack("<h", 0))
    emu.call(0x73980, SCRATCH + _RING_K * REC, 1 if with_state_b else 0, stack_rec)
    if quick:
        emu.stub(0xEA970, 1)
        emu.stub(0xEABE0, 1)
        emu.stub(0xA9450, 0)
        emu.call(0x73A70)
        emu.call(0x6FEC0, 0)
        slot = 139
    else:
        emu.call(0x737D0, slot)
        emu.call(0x6FEC0, 1)
    return SlotImage(emu.rd(img, 0x400000), rec, rec_b, slot, script_no, us, bytes(rmax))


RING_VA = 0x50000000             # 808-record ring for the load emulation (0xB74280 bytes)


def run_slot_load(eboot: Union[str, Path, None], image: bytes, slot: int, seed: int = 9,
                  ) -> "AdvEmu":
    """Load ``slot`` of a save image with the game's own code: slots[slot] = the image's slot
    block, dword 0xE20CC8 = 2, then 0x73890(slot) (copy to the staging, jmp Slot_Load 0xA2D10
    -> 0x7E790 -> History_Restore).  Every other callee of these functions is stubbed (returns
    0, logged); ScanLoad 0xA3050 too, so a call to it shows in the log.  RAM is random except
    the state the loader is given; the decoded script of state A is in the script buffer."""
    import capstone
    from eboot import savedata
    from tsuki.script import AllScr
    from tsuki import misc_adv_system_state as m
    emu = AdvEmu(_eboot_path(eboot))
    emu.uc.mem_map(RING_VA, 0xB80000)
    run = (0x73890, 0xA2D10, 0x7E790, RESTORE_VA, 0x73BD0, 0x73BE0, 0x73C20)
    emu.patch_functions(list(run))
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    import bisect
    starts = emu._starts                                   # set by patch_functions
    ends = {f: starts[bisect.bisect_right(starts, f)] for f in (0x73890, 0xA2D10, 0x7E790, RESTORE_VA)}
    for f, e_ in ends.items():
        for _a, _s, mn, ops in md.disasm_lite(emu.image[f + TEXT_OFF:e_ + TEXT_OFF], f):
            if mn in ("call", "jmp") and ops.startswith("0x"):
                t = int(ops, 16)
                if t in run or f <= t < e_ or t in PLT_NAMES or t in emu.stubs:
                    continue
                emu.stub(t, 0)
    _random_world(emu, seed)
    off = savedata.SLOT_BASE + slot * savedata.SLOT_STRIDE
    blk = image[off:off + savedata.SLOT_SIZE]
    a, _b = m.slot_states(blk)
    script_no = struct.unpack_from("<H", a, 0x18)[0]
    with AllScr() as scr:
        raw = scr.raw(script_no)
    emu.wr(_SCRIPT_BUF, raw[:_SCRIPT_MAX])
    emu.wr(0x86AA18, struct.pack("<H", script_no))    # same script: History_Restore does not reload
    emu.wr(0x120A6E0, struct.pack("<Q", RING_VA))
    emu.wr(0xE24600 + slot * 0x7108, blk)
    emu.wr(0xE20CC8, struct.pack("<I", 2))
    emu.pre = {0x17DB230: emu.rd(0x17DB230, 2)}      # type: ignore[attr-defined]
    emu.log = EmuLog()
    emu.call(0x73890, slot)
    return emu


def verify_slot_load(eboot: Union[str, Path, None], image: bytes, slot: int, seed: int = 9) -> List[str]:
    """What Slot_Load leaves in RAM for an image slot, against adv_state.md 7.3: no scan load,
    F / Z cleared except the state-A lists, scene_stack[n] = state A, scene_stack[n-1] = state B
    (text byte cleared), ring[0] = state A, top = n - 1, script_ptr = base + state A cmd_offset
    (the message command runs again), kind-0 scene_state rule, cur = next = 0, F1118 restored,
    skip latch cleared, 0x174DE04 = 0 / 0x174DE08 = 1, Staging_SetSnapshot(ring[0], n, ..)."""
    from eboot import savedata
    from tsuki import misc_adv_system_state as m
    emu = run_slot_load(eboot, image, slot, seed)
    off = savedata.SLOT_BASE + slot * savedata.SLOT_STRIDE
    blk = image[off:off + savedata.SLOT_SIZE]
    a, b = m.slot_states(blk)
    n = struct.unpack_from("<i", blk, 4)[0]
    bad: List[str] = []
    calls = emu.log.calls                                   # type: ignore[union-attr]
    if any(c.va == 0xA3050 for c in calls):
        bad.append("ScanLoad 0xA3050 was called")
    lists = _lists(eboot)
    fv = m.history_f_values(a, lists=lists)
    f967_base = struct.unpack_from("<h", a, 0x58 + 0x19A6)[0]
    for idx in range(1200):
        want = fv.get(idx, 0)
        if idx == 967:
            want = (f967_base + 1) & 0xFFFF            # 0x7E790 re-syncs F967 (adv_state.md 2.1)
        got = emu.u16(m.VAR_F + 2 * idx)
        if got != want and not (idx == 967 and got in (0, want)):
            bad.append(f"F{idx} = {got} != {want}")
    if any(emu.rd(0x164BB30, 0xAF0)):
        bad.append("Z not cleared")
    if emu.rd(0x120A700 + n * REC, REC) != a:
        bad.append(f"scene_stack[{n}] != state A")
    if n >= 1 and b is not None:
        sb = emu.rd(0x120A700 + (n - 1) * REC, 0x36A8)
        if sb != b:
            bad.append("scene_stack[n-1] != state B (0x36A8 bytes)")
        if emu.u8(0x120A700 + (n - 1) * REC + 0x38FE) != 0:
            bad.append("scene_stack[n-1] text byte +0x38FE not cleared")
    ring0 = emu.rd(RING_VA, REC)
    if ring0[4:] != a[4:]:
        bad.append("ring[0] != state A")
    if emu.i16(0x164B1C0) != n - 1:
        bad.append(f"scene-stack top {emu.i16(0x164B1C0)} != {n - 1}")
    cmd = struct.unpack_from("<Q", a, 8)[0]
    if emu.u64(SCRIPT_PTR) != _SCRIPT_BUF + cmd:
        bad.append(f"script_ptr = base + 0x{emu.u64(SCRIPT_PTR) - _SCRIPT_BUF:x}, state A cmd_offset 0x{cmd:x}")
    f931 = struct.unpack("<h", (fv.get(931, 0) & 0xFFFF).to_bytes(2, "little"))[0]
    if struct.unpack_from("<H", a, 0x24)[0] == 0 and emu.rd(m.SCENE_STATE, 0x1A08) != kind0_scene_state(a, f931):
        bad.append("scene_state != state A +0x58 (kind-0 rule)")
    if (emu.i16(0x120A6EC), emu.i16(0x120A6EE)) != (0, 0):
        bad.append("cur / next not 0")
    if emu.u8(0x17DB230) & 2:
        bad.append("skip latch not cleared")
    if (emu.i32(0x174DE04), emu.i32(0x174DE08)) != (0, 1):
        bad.append("0x174DE04 / 0x174DE08")
    snap = [c for c in calls if c.va == 0x73980]
    kind = struct.unpack_from("<H", a, 0x24)[0]
    blocking = struct.unpack_from("<H", a, 0x3638)[0]
    f823 = struct.unpack("<h", (fv.get(823, 0) & 0xFFFF).to_bytes(2, "little"))[0]
    want_snap = blocking == 0 and kind not in (11, 12) and f823 <= 1
    # the restore's tail call runs before 0x7E790 sets top = n - 1, while top is still the -1
    # Slot_Load wrote: Staging_SetSnapshot(ring[0], 0, 0), i.e. no state B until the re-run
    # message pushes again
    if bool(snap) != want_snap or (snap and snap[0].args[:3] != (RING_VA, 0, 0)):
        bad.append(f"Staging_SetSnapshot calls {[(hex(c.args[0]), c.args[1]) for c in snap]}")
    return bad
