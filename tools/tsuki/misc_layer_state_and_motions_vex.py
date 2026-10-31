"""Execute single VEX (AVX/AVX2/F16C/FMA) guest instructions natively on the host CPU.

Development aid for tsuki.misc_layer_state_and_motions_emu (Windows x64 only).

unicorn 2.1 mis-executes some VEX forms silently (``vpmovsxwd xmm, m64`` returns 0) and
rejects every 256-bit one, so the emulator patches each VEX SIMD instruction of the eboot
to an invalid opcode and runs it here: the instruction is re-encoded with its memory
operand redirected to a host buffer, wrapped in a thunk that loads / stores all general
registers and YMM registers from a context block, and called through ctypes.

Needs ``keystone-engine`` (assembler for the thunks) and ``capstone``.
"""
from __future__ import annotations

import ctypes
import struct
import sys
from typing import Any, Optional

import capstone
from capstone import x86_const as cx

__all__ = ["NativeVex", "available"]

GPR64 = ["rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi",
         "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"]
_SUB: dict[str, str] = {r: r for r in GPR64}
for _a, _b in (("eax", "rax"), ("ecx", "rcx"), ("edx", "rdx"), ("ebx", "rbx"), ("esp", "rsp"),
               ("ebp", "rbp"), ("esi", "rsi"), ("edi", "rdi"), ("ax", "rax"), ("cx", "rcx"),
               ("dx", "rdx"), ("bx", "rbx"), ("si", "rsi"), ("di", "rdi"), ("bp", "rbp"),
               ("al", "rax"), ("cl", "rcx"), ("dl", "rdx"), ("bl", "rbx"), ("sil", "rsi"),
               ("dil", "rdi"), ("bpl", "rbp")):
    _SUB[_a] = _b
for _k in range(8, 16):
    for _suf in ("d", "w", "b"):
        _SUB[f"r{_k}{_suf}"] = f"r{_k}"

CTX_Y = 0x000          # 16 x 32 bytes of YMM state
CTX_G = 0x200          # 16 x 8 bytes of GPRs (GPR64 order)
CTX_FL = 0x280         # RFLAGS after the instruction
CTX_MEM = 0x300        # 128-byte memory-operand buffer
CODE0 = 0x1000

_BASES = [("rbx", 3), ("rsi", 6), ("rdi", 7), ("rdx", 2), ("rcx", 1)]


def available() -> bool:
    """True when native execution is possible (Windows x64 with keystone)."""
    if sys.platform != "win32" or struct.calcsize("P") != 8:
        return False
    try:
        import keystone  # noqa: F401
    except ImportError:
        return False
    return True


class NativeVex:
    def __init__(self) -> None:
        from keystone import KS_ARCH_X86, KS_MODE_64, Ks
        k32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
        k32.VirtualAlloc.restype = ctypes.c_void_p
        k32.VirtualAlloc.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint32, ctypes.c_uint32]
        self.size = 0x400000
        page = k32.VirtualAlloc(None, self.size, 0x3000, 0x40)
        if not page:
            raise OSError("VirtualAlloc failed")
        self.page = int(page)
        self.ctx = self.page
        self.code_ptr = self.page + CODE0
        self.ks = Ks(KS_ARCH_X86, KS_MODE_64)
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.md.detail = True
        self.cache: dict[bytes, tuple[Any, ...]] = {}
        self.count = 0
        self.last_store: Optional[tuple[int, bytes]] = None
        c = self.ctx
        pro = ["push rbx", "push rbp", "push rdi", "push rsi", "push r12", "push r13", "push r14",
               "push r15", "sub rsp, 0xa8"]
        pro += [f"movdqu xmmword ptr [rsp + {16 * k:#x}], xmm{6 + k}" for k in range(10)]
        pro += [f"movabs rax, {c:#x}"]
        pro += [f"vmovdqu ymm{k}, ymmword ptr [rax + {CTX_Y + 32 * k:#x}]" for k in range(16)]
        pro += [f"mov {r}, qword ptr [rax + {CTX_G + 8 * i:#x}]" for i, r in enumerate(GPR64)
                if r not in ("rax", "rsp")]
        pro += [f"mov rax, qword ptr [rax + {CTX_G:#x}]"]
        epi = ["pushfq", "push rax", f"movabs rax, {c:#x}", f"pop qword ptr [rax + {CTX_G:#x}]",
               f"pop qword ptr [rax + {CTX_FL:#x}]"]
        epi += [f"mov qword ptr [rax + {CTX_G + 8 * i:#x}], {r}" for i, r in enumerate(GPR64)
                if r not in ("rax", "rsp")]
        epi += [f"vmovdqu ymmword ptr [rax + {CTX_Y + 32 * k:#x}], ymm{k}" for k in range(16)]
        epi += [f"movdqu xmm{6 + k}, xmmword ptr [rsp + {16 * k:#x}]" for k in range(10)]
        epi += ["add rsp, 0xa8", "pop r15", "pop r14", "pop r13", "pop r12", "pop rsi", "pop rdi",
                "pop rbp", "pop rbx", "vzeroupper", "ret"]
        self.pro = bytes(self.ks.asm("; ".join(pro))[0])
        self.epi = bytes(self.ks.asm("; ".join(epi))[0])

    def _compile(self, raw: bytes, addr: int) -> tuple[Any, ...]:
        ins = next(self.md.disasm(raw, addr))
        raw = bytes(ins.bytes)
        mem = [op for op in ins.operands if op.type == cx.X86_OP_MEM]
        if len(mem) > 1:
            raise RuntimeError(f"two memory operands: {ins.mnemonic} {ins.op_str}")
        rd, wr = ins.regs_access()
        names_r = {self.md.reg_name(r) for r in rd}
        names_w = {self.md.reg_name(r) for r in wr}
        opregs = {self.md.reg_name(op.reg) for op in ins.operands if op.type == cx.X86_OP_REG}
        base: Optional[tuple[str, int]] = None
        new = raw
        if mem:
            e = ins.encoding
            mo = e.modrm_offset
            modrm = raw[mo]
            used = {_SUB[n] for n in opregs if n in _SUB}
            for nm, num in _BASES:
                if nm not in used:
                    base = (nm, num)
                    break
            if base is None:
                raise RuntimeError("no free base register")
            imm = raw[e.imm_offset:e.imm_offset + e.imm_size] if e.imm_size else b""
            pre = bytearray(raw[:mo])
            if pre[0] == 0xC4:
                pre[1] |= 0x60            # VEX.~X = VEX.~B = 1 (no extended index/base)
            elif pre[0] != 0xC5:
                raise RuntimeError(f"not VEX: {raw.hex()}")
            new = bytes(pre) + bytes([(modrm & 0x38) | base[1]]) + imm
            chk = next(self.md.disasm(new, 0))
            if chk.mnemonic != ins.mnemonic:
                raise RuntimeError(f"re-encode mismatch {ins.mnemonic} -> {chk.mnemonic}")
        setb = bytes(self.ks.asm(f"movabs {base[0]}, {self.ctx + CTX_MEM:#x}")[0]) if base else b""
        code = self.pro + setb + new + self.epi
        p = self.code_ptr
        ctypes.memmove(p, code, len(code))
        self.code_ptr = (p + len(code) + 15) & ~15
        fn = ctypes.CFUNCTYPE(None)(p)
        vec_r = sorted({n for n in names_r | opregs if n.startswith(("xmm", "ymm"))})
        vec_w = sorted({n for n in names_w | opregs if n.startswith(("xmm", "ymm"))})
        g_r = sorted({_SUB[n] for n in names_r | opregs if n in _SUB} - {"rsp", "rip"})
        g_w = {_SUB[n] for n in names_w if n in _SUB}
        op0 = ins.operands[0] if ins.operands else None
        if op0 is not None and op0.type == cx.X86_OP_REG and self.md.reg_name(op0.reg) in _SUB:
            g_w.add(_SUB[self.md.reg_name(op0.reg)])
        g_w_l = sorted(g_w - {"rsp", "rip"})
        memop = None
        if mem:
            m = mem[0].mem
            memop = (self.md.reg_name(m.base) if m.base else None,
                     self.md.reg_name(m.index) if m.index else None, m.scale, m.disp, mem[0].size,
                     ins.operands[0].type == cx.X86_OP_MEM)
        wfl = ins.mnemonic in ("vucomiss", "vucomisd", "vcomiss", "vcomisd", "vptest", "vtestps", "vtestpd")
        return (fn, ins.size, vec_r, vec_w, g_r, g_w_l, memop, wfl, f"{ins.mnemonic} {ins.op_str}")

    def execute(self, uc: Any, addr: int, raw: bytes) -> int:
        """Run the instruction ``raw`` (originally at ``addr``) on unicorn state ``uc``;
        returns the next RIP.  A memory store is left in ``last_store`` (address, bytes)."""
        from unicorn import x86_const as ux
        ent = self.cache.get(raw)
        if ent is None:
            ent = self._compile(raw, addr)
            self.cache[raw] = ent
        fn, size, vec_r, vec_w, g_r, g_w, memop, wflags, _text = ent
        self.count += 1
        c = self.ctx
        for n in vec_r:
            k = int(n[3:])
            v = int(uc.reg_read(getattr(ux, f"UC_X86_REG_YMM{k}")))
            ctypes.memmove(c + CTX_Y + 32 * k, v.to_bytes(32, "little"), 32)
        for n in g_r:
            v = int(uc.reg_read(getattr(ux, "UC_X86_REG_" + n.upper())))
            ctypes.memmove(c + CTX_G + 8 * GPR64.index(n), struct.pack("<Q", v), 8)
        ea = 0
        if memop is not None:
            b, ix, sc, disp, sz, _w = memop
            ea = disp
            if b == "rip":
                ea += addr + size
            elif b:
                ea += int(uc.reg_read(getattr(ux, "UC_X86_REG_" + b.upper())))
            if ix:
                ea += int(uc.reg_read(getattr(ux, "UC_X86_REG_" + ix.upper()))) * sc
            ea &= (1 << 64) - 1
            n = max(sz, 1)
            ctypes.memmove(c + CTX_MEM, bytes(uc.mem_read(ea, n)), n)
        fn()
        for n in vec_w:
            k = int(n[3:])
            v = int.from_bytes(ctypes.string_at(c + CTX_Y + 32 * k, 32), "little")
            uc.reg_write(getattr(ux, f"UC_X86_REG_YMM{k}"), v)
        for n in g_w:
            v = struct.unpack("<Q", ctypes.string_at(c + CTX_G + 8 * GPR64.index(n), 8))[0]
            uc.reg_write(getattr(ux, "UC_X86_REG_" + n.upper()), v)
        if wflags:
            fl = struct.unpack("<Q", ctypes.string_at(c + CTX_FL, 8))[0]
            old = int(uc.reg_read(ux.UC_X86_REG_EFLAGS))
            mask = 0x8D5           # CF PF AF ZF SF OF
            uc.reg_write(ux.UC_X86_REG_EFLAGS, (old & ~mask) | (fl & mask))
        if memop is not None and memop[5]:
            self.last_store = (ea, ctypes.string_at(c + CTX_MEM, memop[4]))
        else:
            self.last_store = None
        return addr + size
