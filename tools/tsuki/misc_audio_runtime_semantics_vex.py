"""Python execution of the VEX (AVX) instructions the eboot's sound code uses.

Development aid for ``tsuki.misc_audio_runtime_semantics_emu`` (needs ``unicorn`` and
``capstone``).  unicorn 2.1.4 executes VEX 3-operand instructions as their legacy
2-operand forms (it ignores VEX.vvvv, so ``vmulss xmm0, xmm2, xmm0`` computes
``xmm0 * xmm0``) and rejects most 256-bit forms; this was established by the font and
text-box work (docs/formats/textbox.md 10.3, scratch/verify_fonts_reproduce/r2/vexfix.py)
and re-checked here (scratch/misc_audio_runtime_semantics/avxtest*.py).  The emulator
therefore routes EVERY instruction whose first byte is 0xC4/0xC5 to ``VexCpu.execute``,
which reads the operands through unicorn, computes the result with numpy float32 /
float64 arithmetic (IEEE round-to-nearest, like the hardware with the default MXCSR) and
writes it back with VEX.128 upper-lane zeroing.  An instruction that is not implemented
raises ``NotImplementedError`` (never a silent skip).

The approach (hook + re-implementation in Python) follows vexfix.py; the code is our own.
"""
from __future__ import annotations

import struct
from typing import Any, Callable, Dict, List, Optional, Tuple

import numpy as np
from capstone import CS_ARCH_X86, CS_MODE_64, Cs
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_OP_REG, X86_REG_INVALID, X86_REG_RIP
from unicorn import Uc
from unicorn import x86_const as X

M32 = 0xFFFFFFFF
M64 = 0xFFFFFFFFFFFFFFFF
M128 = (1 << 128) - 1
M256 = (1 << 256) - 1

_GPR64 = {
    "rax": "RAX", "rbx": "RBX", "rcx": "RCX", "rdx": "RDX", "rsi": "RSI", "rdi": "RDI",
    "rbp": "RBP", "rsp": "RSP", **{f"r{i}": f"R{i}" for i in range(8, 16)},
}
_GPR32 = {"eax": "rax", "ebx": "rbx", "ecx": "rcx", "edx": "rdx", "esi": "rsi", "edi": "rdi",
          "ebp": "rbp", "esp": "rsp", **{f"r{i}d": f"r{i}" for i in range(8, 16)}}
_GPR16 = {"ax": "rax", "bx": "rbx", "cx": "rcx", "dx": "rdx", "si": "rsi", "di": "rdi",
          "bp": "rbp", "sp": "rsp", **{f"r{i}w": f"r{i}" for i in range(8, 16)}}
_GPR8 = {"al": "rax", "bl": "rbx", "cl": "rcx", "dl": "rdx", "sil": "rsi", "dil": "rdi",
         "bpl": "rbp", "spl": "rsp", **{f"r{i}b": f"r{i}" for i in range(8, 16)}}

_CMP = {  # predicate -> (a, b) -> bool   (NaN handling as in the Intel SDM table)
    "eq": lambda a, b: bool(a == b), "lt": lambda a, b: bool(a < b), "le": lambda a, b: bool(a <= b),
    "unord": lambda a, b: bool(np.isnan(a) or np.isnan(b)),
    "neq": lambda a, b: not bool(a == b), "nlt": lambda a, b: not bool(a < b),
    "nle": lambda a, b: not bool(a <= b), "ord": lambda a, b: not (np.isnan(a) or np.isnan(b)),
    "nge": lambda a, b: not bool(a >= b), "ngt": lambda a, b: not bool(a > b),
    "ge": lambda a, b: bool(a >= b), "gt": lambda a, b: bool(a > b),
    "false": lambda a, b: False, "true": lambda a, b: True,
}


def f32(bits: int) -> np.float32:
    return np.frombuffer(struct.pack("<I", bits & M32), dtype=np.float32)[0]


def b32(x: Any) -> int:
    return int(struct.unpack("<I", np.float32(x).tobytes())[0])


def f64(bits: int) -> np.float64:
    return np.frombuffer(struct.pack("<Q", bits & M64), dtype=np.float64)[0]


def b64(x: Any) -> int:
    return int(struct.unpack("<Q", np.float64(x).tobytes())[0])


def _lanes(v: int, width: int, n: int) -> List[int]:
    m = (1 << width) - 1
    return [(v >> (width * i)) & m for i in range(n)]


def _join(lanes: List[int], width: int) -> int:
    m = (1 << width) - 1
    out = 0
    for i, x in enumerate(lanes):
        out |= (x & m) << (width * i)
    return out


def _trunc_int(x: Any, bits: int) -> int:
    """cvtt*2si: truncate; NaN / out of range give the integer indefinite value."""
    lim = 1 << (bits - 1)
    if np.isnan(x) or np.isinf(x):
        return lim
    t = int(np.trunc(np.float64(x)))
    if t >= lim or t < -lim:
        return lim
    return t & ((1 << bits) - 1)


def _round_int(x: Any, bits: int) -> int:
    lim = 1 << (bits - 1)
    if np.isnan(x) or np.isinf(x):
        return lim
    t = int(np.rint(np.float64(x)))
    if t >= lim or t < -lim:
        return lim
    return t & ((1 << bits) - 1)


class VexCpu:
    """Executes one VEX instruction at a time on a unicorn x86-64 CPU."""

    def __init__(self, uc: Uc) -> None:
        self.uc = uc
        self.cs = Cs(CS_ARCH_X86, CS_MODE_64)
        self.cs.detail = True
        self._cache: Dict[int, object] = {}
        self.count = 0
        self.mnemonics: Dict[str, int] = {}
        # called as on_mem_write(va, size, value) for every memory store (unicorn's write
        # hooks do not see stores made through mem_write)
        self.on_mem_write: Optional[Callable[[int, int, int], None]] = None
        # original text bytes (VA 0 = offset 0) when the guest copy is patched (ud2)
        self.code: Optional[bytes] = None

    # -- operand access ------------------------------------------------------
    def _rname(self, reg: int) -> str:
        return str(self.cs.reg_name(reg))

    def _ea(self, ins: object, op: object) -> int:
        m = op.mem                                    # type: ignore[attr-defined]
        if m.segment != X86_REG_INVALID:
            raise NotImplementedError(f"segment override at {ins.address:#x}")  # type: ignore[attr-defined]
        a = m.disp
        if m.base == X86_REG_RIP:
            a += ins.address + ins.size               # type: ignore[attr-defined]
        elif m.base != X86_REG_INVALID:
            a += self._gpr_read(self._rname(m.base))
        if m.index != X86_REG_INVALID:
            a += self._gpr_read(self._rname(m.index)) * m.scale
        return a & M64

    def _gpr_read(self, name: str) -> int:
        if name in _GPR64:
            return int(self.uc.reg_read(getattr(X, "UC_X86_REG_" + _GPR64[name])))
        for tab, bits in ((_GPR32, 32), (_GPR16, 16), (_GPR8, 8)):
            if name in tab:
                return self._gpr_read(tab[name]) & ((1 << bits) - 1)
        raise NotImplementedError(f"register {name}")

    def _gpr_write(self, name: str, v: int) -> None:
        if name in _GPR64:
            self.uc.reg_write(getattr(X, "UC_X86_REG_" + _GPR64[name]), v & M64)
        elif name in _GPR32:                          # 32-bit writes zero-extend
            self._gpr_write(_GPR32[name], v & M32)
        else:
            raise NotImplementedError(f"write to {name}")

    def _vec_read(self, name: str) -> int:
        if name.startswith("ymm"):
            return int(self.uc.reg_read(getattr(X, "UC_X86_REG_YMM" + name[3:])))
        return int(self.uc.reg_read(getattr(X, "UC_X86_REG_XMM" + name[3:])))

    def _vec_write(self, name: str, v: int) -> None:
        # VEX.128 / VEX.256 both zero everything above the written width
        idx = name[3:]
        width = 256 if name.startswith("ymm") else 128
        self.uc.reg_write(getattr(X, "UC_X86_REG_YMM" + idx), v & ((1 << width) - 1))

    def read(self, ins: object, k: int, size: int = 0) -> int:
        op = ins.operands[k]                          # type: ignore[attr-defined]
        if op.type == X86_OP_REG:
            rn = self._rname(op.reg)
            return self._vec_read(rn) if rn.startswith(("xmm", "ymm")) else self._gpr_read(rn)
        if op.type == X86_OP_MEM:
            nb = int(size or op.size)
            return int.from_bytes(bytes(self.uc.mem_read(self._ea(ins, op), nb)), "little")
        if op.type == X86_OP_IMM:
            return int(op.imm) & M64
        raise NotImplementedError("operand type")

    def write(self, ins: object, k: int, v: int, size: int = 0) -> None:
        op = ins.operands[k]                          # type: ignore[attr-defined]
        if op.type == X86_OP_REG:
            rn = self._rname(op.reg)
            if rn.startswith(("xmm", "ymm")):
                self._vec_write(rn, v)
            else:
                self._gpr_write(rn, v)
            return
        if op.type == X86_OP_MEM:
            nb = int(size or op.size)
            ea = self._ea(ins, op)
            v &= (1 << (8 * nb)) - 1
            self.uc.mem_write(ea, v.to_bytes(nb, "little"))
            if self.on_mem_write is not None:
                self.on_mem_write(ea, nb, v)
            return
        raise NotImplementedError("write operand")

    def is_reg(self, ins: object, k: int) -> bool:
        return bool(ins.operands[k].type == X86_OP_REG)  # type: ignore[attr-defined]

    def width(self, ins: object, k: int) -> int:
        op = ins.operands[k]                          # type: ignore[attr-defined]
        if op.type == X86_OP_REG:
            return 256 if self._rname(op.reg).startswith("ymm") else 128
        return 8 * int(op.size)

    def _flags(self, zf: int, pf: int, cf: int) -> None:
        fl = int(self.uc.reg_read(X.UC_X86_REG_EFLAGS))
        fl &= ~((1 << 0) | (1 << 2) | (1 << 4) | (1 << 6) | (1 << 7) | (1 << 11))
        fl |= (cf << 0) | (pf << 2) | (zf << 6)
        self.uc.reg_write(X.UC_X86_REG_EFLAGS, fl)

    # -- execution -------------------------------------------------------------
    def decode(self, addr: int) -> object:
        ins = self._cache.get(addr)
        if ins is None:
            if self.code is not None and 0 <= addr < len(self.code):
                code = self.code[addr:addr + 16]
            else:
                code = bytes(self.uc.mem_read(addr, 16))
            ins = next(self.cs.disasm(code, addr, 1))
            self._cache[addr] = ins
        return ins

    def execute(self, addr: int) -> int:
        """Execute the VEX instruction at ``addr``; returns its size."""
        ins = self.decode(addr)
        mn = str(ins.mnemonic)                         # type: ignore[attr-defined]
        self.count += 1
        self.mnemonics[mn] = self.mnemonics.get(mn, 0) + 1
        h = _HANDLERS.get(mn)
        if h is None:
            if mn.startswith("vcmp"):
                h = _cmp_handler
            else:
                raise NotImplementedError(f"VEX {mn} {ins.op_str} at {addr:#x}")  # type: ignore[attr-defined]
        h(self, ins, mn)
        return int(ins.size)                           # type: ignore[attr-defined]


# ---------------------------------------------------------------------------
# instruction semantics
# ---------------------------------------------------------------------------
Handler = Callable[[VexCpu, object, str], None]


def _nops(ins: object) -> int:
    return len(ins.operands)                          # type: ignore[attr-defined]


def _mov_full(c: VexCpu, ins: object, mn: str) -> None:            # vmovups/aps/dqu/dqa
    c.write(ins, 0, c.read(ins, 1))


def _movss(c: VexCpu, ins: object, mn: str) -> None:
    w = 32 if mn == "vmovss" else 64
    m = (1 << w) - 1
    if _nops(ins) == 3:
        c.write(ins, 0, (c.read(ins, 1) & ~m & M128) | (c.read(ins, 2) & m))
    elif c.is_reg(ins, 0):                                          # load: zero upper
        c.write(ins, 0, c.read(ins, 1, w // 8) & m)
    else:                                                           # store
        c.write(ins, 0, c.read(ins, 1) & m, w // 8)


def _movd(c: VexCpu, ins: object, mn: str) -> None:
    w = 32 if mn == "vmovd" else 64
    m = (1 << w) - 1
    dst_vec = c.is_reg(ins, 0) and c._rname(ins.operands[0].reg).startswith("xmm")  # type: ignore[attr-defined]
    if dst_vec:
        c.write(ins, 0, c.read(ins, 1, w // 8) & m)
    else:
        c.write(ins, 0, c.read(ins, 1) & m, w // 8)


def _movlps(c: VexCpu, ins: object, mn: str) -> None:
    if _nops(ins) == 2:                                             # store low qword
        c.write(ins, 0, c.read(ins, 1) & M64, 8)
    else:
        c.write(ins, 0, (c.read(ins, 1) & (M64 << 64)) | c.read(ins, 2, 8))


def _movhps(c: VexCpu, ins: object, mn: str) -> None:
    if _nops(ins) == 2:
        c.write(ins, 0, (c.read(ins, 1) >> 64) & M64, 8)
    else:
        c.write(ins, 0, (c.read(ins, 1) & M64) | (c.read(ins, 2, 8) << 64))


def _movdup(c: VexCpu, ins: object, mn: str) -> None:
    w = c.width(ins, 0)
    s = _lanes(c.read(ins, 1, w // 8), 32, w // 32)
    if mn == "vmovsldup":
        out = [s[i & ~1] for i in range(len(s))]
    else:
        out = [s[i | 1] for i in range(len(s))]
    c.write(ins, 0, _join(out, 32))


def _scalar_f(op: Callable[[np.floating, np.floating], np.floating], w: int) -> Handler:
    def h(c: VexCpu, ins: object, mn: str) -> None:
        m = (1 << w) - 1
        a, bb = c.read(ins, 1), c.read(ins, 2, w // 8)
        with np.errstate(all="ignore"):
            if w == 32:
                r = b32(op(f32(a & m), f32(bb & m)))
            else:
                r = b64(op(f64(a & m), f64(bb & m)))
        c.write(ins, 0, (a & ~m & M128) | r)
    return h


def _minf(a: np.floating, b: np.floating) -> np.floating:
    return a if a < b else b                 # NaN / equal -> second source (SDM MINSS)


def _maxf(a: np.floating, b: np.floating) -> np.floating:
    return a if a > b else b


def _packed_f(op: Callable[[np.floating, np.floating], np.floating], w: int) -> Handler:
    def h(c: VexCpu, ins: object, mn: str) -> None:
        width = c.width(ins, 0)
        a = _lanes(c.read(ins, 1), w, width // w)
        bb = _lanes(c.read(ins, 2, width // 8), w, width // w)
        conv, back = (f32, b32) if w == 32 else (f64, b64)
        with np.errstate(all="ignore"):
            out = [back(op(conv(x), conv(y))) for x, y in zip(a, bb)]
        c.write(ins, 0, _join(out, w))
    return h


def _logic(op: Callable[[int, int], int]) -> Handler:
    def h(c: VexCpu, ins: object, mn: str) -> None:
        width = c.width(ins, 0)
        m = (1 << width) - 1
        c.write(ins, 0, op(c.read(ins, 1), c.read(ins, 2, width // 8)) & m)
    return h


def _ucomi(c: VexCpu, ins: object, mn: str) -> None:
    w = 32 if mn.endswith("ss") else 64
    conv = f32 if w == 32 else f64
    a = conv(c.read(ins, 0) & ((1 << w) - 1))
    b = conv(c.read(ins, 1, w // 8) & ((1 << w) - 1))
    if np.isnan(a) or np.isnan(b):
        c._flags(1, 1, 1)
    elif a < b:
        c._flags(0, 0, 1)
    elif a == b:
        c._flags(1, 0, 0)
    else:
        c._flags(0, 0, 0)


def _cmp_handler(c: VexCpu, ins: object, mn: str) -> None:
    suf = mn[-2:]
    pred = mn[4:-2]
    if pred not in _CMP:
        raise NotImplementedError(mn)
    fn = _CMP[pred]
    w = 32 if suf in ("ss", "ps") else 64
    conv = f32 if w == 32 else f64
    ones = (1 << w) - 1
    if suf in ("ss", "sd"):
        a = c.read(ins, 1)
        bb = c.read(ins, 2, w // 8)
        r = ones if fn(conv(a & ones), conv(bb & ones)) else 0
        c.write(ins, 0, (a & ~ones & M128) | r)
    else:
        width = c.width(ins, 0)
        a_l = _lanes(c.read(ins, 1), w, width // w)
        b_l = _lanes(c.read(ins, 2, width // 8), w, width // w)
        c.write(ins, 0, _join([ones if fn(conv(x), conv(y)) else 0 for x, y in zip(a_l, b_l)], w))


def _blendv(c: VexCpu, ins: object, mn: str) -> None:
    w = 32 if mn == "vblendvps" else 64
    width = c.width(ins, 0)
    a = _lanes(c.read(ins, 1), w, width // w)
    bb = _lanes(c.read(ins, 2, width // 8), w, width // w)
    msk = _lanes(c.read(ins, 3), w, width // w)
    c.write(ins, 0, _join([y if (mm >> (w - 1)) & 1 else x for x, y, mm in zip(a, bb, msk)], w))


def _cvtsi2s(c: VexCpu, ins: object, mn: str) -> None:
    op2 = ins.operands[2]                                           # type: ignore[attr-defined]
    size = int(op2.size)
    v = c.read(ins, 2, size) & ((1 << (8 * size)) - 1)
    if v >> (8 * size - 1):
        v -= 1 << (8 * size)
    a = c.read(ins, 1)
    if mn == "vcvtsi2ss":
        # int -> float32 with one rounding (exact via float64 for |v| < 2**53)
        if abs(v) >= (1 << 53):
            raise NotImplementedError("vcvtsi2ss of a >53-bit integer")
        c.write(ins, 0, (a & ~M32 & M128) | b32(np.float32(np.float64(v))))
    else:
        if abs(v) >= (1 << 53):
            raise NotImplementedError("vcvtsi2sd of a >53-bit integer")
        c.write(ins, 0, (a & ~M64 & M128) | b64(np.float64(v)))


def _cvtt2si(c: VexCpu, ins: object, mn: str) -> None:
    bits = 8 * int(ins.operands[0].size)                            # type: ignore[attr-defined]
    src_w = 32 if "ss2si" in mn else 64
    x = (f32 if src_w == 32 else f64)(c.read(ins, 1, src_w // 8) & ((1 << src_w) - 1))
    r = _trunc_int(x, bits) if "cvtt" in mn else _round_int(x, bits)
    c.write(ins, 0, r)


def _cvtss2sd(c: VexCpu, ins: object, mn: str) -> None:
    a = c.read(ins, 1)
    if mn == "vcvtss2sd":
        c.write(ins, 0, (a & ~M64 & M128) | b64(np.float64(f32(c.read(ins, 2, 4)))))
    else:
        with np.errstate(all="ignore"):
            c.write(ins, 0, (a & ~M32 & M128) | b32(np.float32(f64(c.read(ins, 2, 8)))))


def _cvtdq2ps(c: VexCpu, ins: object, mn: str) -> None:
    width = c.width(ins, 0)
    s = _lanes(c.read(ins, 1, width // 8), 32, width // 32)
    out = []
    for x in s:
        x = x - (1 << 32) if x >> 31 else x
        out.append(b32(np.float32(np.float64(x))))
    c.write(ins, 0, _join(out, 32))


def _cvttps2dq(c: VexCpu, ins: object, mn: str) -> None:
    width = c.width(ins, 0)
    s = _lanes(c.read(ins, 1, width // 8), 32, width // 32)
    c.write(ins, 0, _join([_trunc_int(f32(x), 32) for x in s], 32))


def _int_packed(w: int, op: Callable[[int, int], int]) -> Handler:
    def h(c: VexCpu, ins: object, mn: str) -> None:
        width = c.width(ins, 0)
        a = _lanes(c.read(ins, 1), w, width // w)
        bb = _lanes(c.read(ins, 2, width // 8), w, width // w)
        c.write(ins, 0, _join([op(x, y) for x, y in zip(a, bb)], w))
    return h


def _pmuludq(c: VexCpu, ins: object, mn: str) -> None:
    width = c.width(ins, 0)
    a = _lanes(c.read(ins, 1), 64, width // 64)
    bb = _lanes(c.read(ins, 2, width // 8), 64, width // 64)
    c.write(ins, 0, _join([(x & M32) * (y & M32) for x, y in zip(a, bb)], 64))


def _shift(w: int, left: bool) -> Handler:
    def h(c: VexCpu, ins: object, mn: str) -> None:
        width = c.width(ins, 0)
        a = _lanes(c.read(ins, 1), w, width // w)
        op2 = ins.operands[2]                                       # type: ignore[attr-defined]
        n = int(op2.imm) if op2.type == X86_OP_IMM else c.read(ins, 2, 16) & M64
        m = (1 << w) - 1
        out = [0 if n >= w else ((x << n) & m if left else x >> n) for x in a]
        c.write(ins, 0, _join(out, w))
    return h


def _pshufd(c: VexCpu, ins: object, mn: str) -> None:
    width = c.width(ins, 0)
    s = _lanes(c.read(ins, 1, width // 8), 32, width // 32)
    imm = c.read(ins, 2)
    out = []
    for lane in range(width // 128):
        base = 4 * lane
        out += [s[base + ((imm >> (2 * i)) & 3)] for i in range(4)]
    c.write(ins, 0, _join(out, 32))


def _pextr(c: VexCpu, ins: object, mn: str) -> None:
    w = {"vpextrb": 8, "vpextrw": 16, "vpextrd": 32, "vpextrq": 64, "vextractps": 32}[mn]
    v = c.read(ins, 1)
    k = c.read(ins, 2) & (128 // w - 1)
    r = (v >> (w * k)) & ((1 << w) - 1)
    if c.is_reg(ins, 0):
        c.write(ins, 0, r)                                          # zero-extends into the GPR
    else:
        c.write(ins, 0, r, w // 8)


def _pinsr(c: VexCpu, ins: object, mn: str) -> None:
    w = {"vpinsrb": 8, "vpinsrw": 16, "vpinsrd": 32, "vpinsrq": 64}[mn]
    a = c.read(ins, 1)
    v = c.read(ins, 2, w // 8) & ((1 << w) - 1)
    k = c.read(ins, 3) & (128 // w - 1)
    m = ((1 << w) - 1) << (w * k)
    c.write(ins, 0, (a & ~m & M128) | (v << (w * k)))


def _extractf128(c: VexCpu, ins: object, mn: str) -> None:
    v = c.read(ins, 1)
    k = c.read(ins, 2) & 1
    r = (v >> (128 * k)) & M128
    if c.is_reg(ins, 0):
        c.write(ins, 0, r)
    else:
        c.write(ins, 0, r, 16)


def _insertf128(c: VexCpu, ins: object, mn: str) -> None:
    a = c.read(ins, 1)
    v = c.read(ins, 2, 16) & M128
    k = c.read(ins, 3) & 1
    m = M128 << (128 * k)
    c.write(ins, 0, (a & ~m & M256) | (v << (128 * k)))


def _packssdw(c: VexCpu, ins: object, mn: str) -> None:
    def sat(x: int) -> int:
        x = x - (1 << 32) if x >> 31 else x
        return max(-32768, min(32767, x)) & 0xFFFF
    a = _lanes(c.read(ins, 1), 32, 4)
    bb = _lanes(c.read(ins, 2, 16), 32, 4)
    c.write(ins, 0, _join([sat(x) for x in a + bb], 16))


def _broadcastss(c: VexCpu, ins: object, mn: str) -> None:
    width = c.width(ins, 0)
    x = c.read(ins, 1, 4) & M32
    c.write(ins, 0, _join([x] * (width // 32), 32))


def _unpcklps(c: VexCpu, ins: object, mn: str) -> None:
    a = _lanes(c.read(ins, 1), 32, 4)
    bb = _lanes(c.read(ins, 2, 16), 32, 4)
    c.write(ins, 0, _join([a[0], bb[0], a[1], bb[1]], 32))


def _insertps(c: VexCpu, ins: object, mn: str) -> None:
    a = _lanes(c.read(ins, 1), 32, 4)
    imm = c.read(ins, 3) & 0xFF
    if c.is_reg(ins, 2):
        src = _lanes(c.read(ins, 2), 32, 4)[(imm >> 6) & 3]
    else:
        src = c.read(ins, 2, 4) & M32
    a[(imm >> 4) & 3] = src
    for i in range(4):
        if imm & (1 << i):
            a[i] = 0
    c.write(ins, 0, _join(a, 32))


def _sqrtss(c: VexCpu, ins: object, mn: str) -> None:
    a = c.read(ins, 1)
    with np.errstate(all="ignore"):
        r = b32(np.sqrt(f32(c.read(ins, 2, 4) & M32)))
    c.write(ins, 0, (a & ~M32 & M128) | r)


def _gpr_bits(c: VexCpu, ins: object) -> int:
    return 8 * int(ins.operands[0].size)                            # type: ignore[attr-defined]


def _szf(c: VexCpu, r: int, bits: int, cf: int = 0) -> None:
    fl = int(c.uc.reg_read(X.UC_X86_REG_EFLAGS))
    fl &= ~((1 << 0) | (1 << 6) | (1 << 7) | (1 << 11))
    fl |= (cf << 0) | ((r == 0) << 6) | (((r >> (bits - 1)) & 1) << 7)
    c.uc.reg_write(X.UC_X86_REG_EFLAGS, fl)


def _andn(c: VexCpu, ins: object, mn: str) -> None:                # BMI1: dst = ~src1 & src2
    bits = _gpr_bits(c, ins)
    m = (1 << bits) - 1
    r = (~c.read(ins, 1) & c.read(ins, 2, bits // 8)) & m
    c.write(ins, 0, r)
    _szf(c, r, bits)


def _shiftx(c: VexCpu, ins: object, mn: str) -> None:              # BMI2 shlx/shrx/sarx: no flags
    bits = _gpr_bits(c, ins)
    m = (1 << bits) - 1
    v = c.read(ins, 1, bits // 8) & m
    n = c.read(ins, 2) & (bits - 1)
    if mn == "shlx":
        r = (v << n) & m
    elif mn == "shrx":
        r = v >> n
    else:
        sv = v - (1 << bits) if v >> (bits - 1) else v
        r = (sv >> n) & m
    c.write(ins, 0, r)


def _rorx(c: VexCpu, ins: object, mn: str) -> None:
    bits = _gpr_bits(c, ins)
    m = (1 << bits) - 1
    v = c.read(ins, 1, bits // 8) & m
    n = c.read(ins, 2) & (bits - 1)
    c.write(ins, 0, ((v >> n) | (v << (bits - n))) & m)


def _bzhi(c: VexCpu, ins: object, mn: str) -> None:
    bits = _gpr_bits(c, ins)
    m = (1 << bits) - 1
    v = c.read(ins, 1, bits // 8) & m
    n = c.read(ins, 2) & 0xFF
    r = v & ((1 << n) - 1) if n < bits else v
    c.write(ins, 0, r)
    _szf(c, r, bits, int(n > bits - 1))


def _blsx(c: VexCpu, ins: object, mn: str) -> None:
    bits = _gpr_bits(c, ins)
    m = (1 << bits) - 1
    v = c.read(ins, 1, bits // 8) & m
    if mn == "blsr":
        r, cf = v & (v - 1) & m, int(v == 0)
    elif mn == "blsi":
        r, cf = v & (-v) & m, int(v != 0)
    else:                                                           # blsmsk
        r, cf = (v ^ (v - 1)) & m, int(v == 0)
    c.write(ins, 0, r)
    _szf(c, r, bits, cf)


def _bextr(c: VexCpu, ins: object, mn: str) -> None:
    bits = _gpr_bits(c, ins)
    v = c.read(ins, 1, bits // 8) & ((1 << bits) - 1)
    ctl = c.read(ins, 2)
    start, ln = ctl & 0xFF, (ctl >> 8) & 0xFF
    r = (v >> start) & ((1 << ln) - 1) if start < bits else 0
    c.write(ins, 0, r)
    _szf(c, r, bits)


_HANDLERS: Dict[str, Handler] = {
    "andn": _andn, "shlx": _shiftx, "shrx": _shiftx, "sarx": _shiftx, "rorx": _rorx,
    "bzhi": _bzhi, "blsr": _blsx, "blsi": _blsx, "blsmsk": _blsx, "bextr": _bextr,
    "vmovups": _mov_full, "vmovaps": _mov_full, "vmovdqu": _mov_full, "vmovdqa": _mov_full,
    "vmovupd": _mov_full, "vmovapd": _mov_full,
    "vmovss": _movss, "vmovsd": _movss, "vmovd": _movd, "vmovq": _movd,
    "vmovlps": _movlps, "vmovlpd": _movlps, "vmovhps": _movhps, "vmovhpd": _movhps,
    "vmovsldup": _movdup, "vmovshdup": _movdup,
    "vaddss": _scalar_f(lambda a, b: a + b, 32), "vsubss": _scalar_f(lambda a, b: a - b, 32),
    "vmulss": _scalar_f(lambda a, b: a * b, 32), "vdivss": _scalar_f(lambda a, b: a / b, 32),
    "vminss": _scalar_f(_minf, 32), "vmaxss": _scalar_f(_maxf, 32),
    "vaddsd": _scalar_f(lambda a, b: a + b, 64), "vsubsd": _scalar_f(lambda a, b: a - b, 64),
    "vmulsd": _scalar_f(lambda a, b: a * b, 64), "vdivsd": _scalar_f(lambda a, b: a / b, 64),
    "vminsd": _scalar_f(_minf, 64), "vmaxsd": _scalar_f(_maxf, 64),
    "vaddps": _packed_f(lambda a, b: a + b, 32), "vsubps": _packed_f(lambda a, b: a - b, 32),
    "vmulps": _packed_f(lambda a, b: a * b, 32), "vdivps": _packed_f(lambda a, b: a / b, 32),
    "vminps": _packed_f(_minf, 32), "vmaxps": _packed_f(_maxf, 32),
    "vxorps": _logic(lambda a, b: a ^ b), "vxorpd": _logic(lambda a, b: a ^ b),
    "vpxor": _logic(lambda a, b: a ^ b),
    "vandps": _logic(lambda a, b: a & b), "vandpd": _logic(lambda a, b: a & b),
    "vpand": _logic(lambda a, b: a & b),
    "vandnps": _logic(lambda a, b: ~a & b), "vandnpd": _logic(lambda a, b: ~a & b),
    "vpandn": _logic(lambda a, b: ~a & b),
    "vorps": _logic(lambda a, b: a | b), "vorpd": _logic(lambda a, b: a | b), "vpor": _logic(lambda a, b: a | b),
    "vucomiss": _ucomi, "vcomiss": _ucomi, "vucomisd": _ucomi, "vcomisd": _ucomi,
    "vblendvps": _blendv, "vblendvpd": _blendv,
    "vcvtsi2ss": _cvtsi2s, "vcvtsi2sd": _cvtsi2s,
    "vcvttss2si": _cvtt2si, "vcvttsd2si": _cvtt2si, "vcvtss2si": _cvtt2si, "vcvtsd2si": _cvtt2si,
    "vcvtss2sd": _cvtss2sd, "vcvtsd2ss": _cvtss2sd,
    "vcvtdq2ps": _cvtdq2ps, "vcvttps2dq": _cvttps2dq,
    "vpaddq": _int_packed(64, lambda a, b: a + b), "vpaddd": _int_packed(32, lambda a, b: a + b),
    "vpsubq": _int_packed(64, lambda a, b: a - b), "vpsubd": _int_packed(32, lambda a, b: a - b),
    "vpaddw": _int_packed(16, lambda a, b: a + b),
    "vpcmpeqd": _int_packed(32, lambda a, b: M32 if a == b else 0),
    "vpcmpeqb": _int_packed(8, lambda a, b: 0xFF if a == b else 0),
    "vpmuludq": _pmuludq,
    "vpsrlq": _shift(64, False), "vpsllq": _shift(64, True), "vpsrld": _shift(32, False),
    "vpslld": _shift(32, True),
    "vpshufd": _pshufd,
    "vpextrb": _pextr, "vpextrw": _pextr, "vpextrd": _pextr, "vpextrq": _pextr, "vextractps": _pextr,
    "vpinsrb": _pinsr, "vpinsrw": _pinsr, "vpinsrd": _pinsr, "vpinsrq": _pinsr,
    "vextractf128": _extractf128, "vinsertf128": _insertf128,
    "vpackssdw": _packssdw, "vbroadcastss": _broadcastss, "vunpcklps": _unpcklps,
    "vinsertps": _insertps, "vsqrtss": _sqrtss,
}


def self_test() -> Tuple[int, int]:
    """Execute a few hand-assembled VEX instructions and compare with known results.
    Returns (passed, total)."""
    from unicorn import UC_ARCH_X86, UC_MODE_64
    uc = Uc(UC_ARCH_X86, UC_MODE_64)
    uc.mem_map(0x1000, 0x1000)
    cpu = VexCpu(uc)
    cases: List[Tuple[str, Dict[str, int], str, int]] = [
        # vmulss xmm0, xmm2, xmm0   (c5 ea 59 c0): xmm0 = xmm2 * xmm0
        ("c5ea59c0", {"XMM0": b32(3.0), "XMM2": b32(2.5)}, "XMM0", b32(7.5)),
        # vsubss xmm3, xmm0, xmm1   (c5 fa 5c d9): xmm3 = xmm0 - xmm1
        ("c5fa5cd9", {"XMM0": b32(1.0), "XMM1": b32(0.25)}, "XMM3", b32(0.75)),
        # vcvtsi2ss xmm2, xmm2, edx (c5 ea 2a d2)
        ("c5ea2ad2", {"RDX": 40, "XMM2": 0}, "XMM2", b32(40.0)),
        # vcvttss2si r12d, xmm0     (c4 41 7a 2c e0)? use c5 7a 2c e0 = vcvttss2si r12d, xmm0
        ("c57a2ce0", {"XMM0": b32(39.9)}, "R12", 39),
        # vmaxss xmm3, xmm4, xmm3   (c5 da 5f db)
        ("c5da5fdb", {"XMM4": b32(-2.0), "XMM3": b32(2.0)}, "XMM3", b32(2.0)),
        # andn eax, ecx, eax (c4 e2 70 f2 c0): eax = ~ecx & eax  (the clamp at VA 0xAED3)
        ("c4e270f2c0", {"RAX": 0xFFFFFFF6, "RCX": 0xFFFFFFFF}, "RAX", 0),
        ("c4e270f2c0", {"RAX": 57, "RCX": 0}, "RAX", 57),
    ]
    ok = 0
    for hx, regs, out, want in cases:
        code = bytes.fromhex(hx)
        uc.mem_write(0x1000, code + b"\x90" * 8)
        for r, v in regs.items():
            uc.reg_write(getattr(X, "UC_X86_REG_" + r), v)
        cpu._cache.clear()
        cpu.execute(0x1000)
        got = int(uc.reg_read(getattr(X, "UC_X86_REG_" + out)))
        if out.startswith("XMM"):
            got &= M32
        ok += int(got == want)
    return ok, len(cases)


if __name__ == "__main__":
    print("VEX self test: %d / %d" % self_test())
