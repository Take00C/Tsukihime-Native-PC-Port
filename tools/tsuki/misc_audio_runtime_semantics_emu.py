"""Unicorn harness that runs the eboot's own sound code (CUSA47999 v01.01).

Development / verification aid for docs/formats/sound_runtime.md; nothing here is needed
to play the game's audio.  Needs ``unicorn`` and ``capstone`` in the project venv.

What it does
------------
* maps text + relro + data of the user's eboot.bin (RELATIVE relocations applied), a
  stack, a heap and a statement buffer;
* replaces the ``nexl_Sound`` object (pointer at data VA 0xDD9028, vtable relro
  0x432318) by a fake whose 64 methods are traps: every call is logged as
  ``SoundCall(method, stream, args, xmm0, xmm1)`` and answered by ``SoundEmu.answer``
  (``is_playing`` per stream, configurable);
* executes every VEX instruction in Python (``misc_audio_runtime_semantics_vex``);
* stubs libc imports (memcpy, strlen, strtol, ldexp, rand, ...) in Python, every other
  import returns 0 and is logged;
* logs every write into the sound globals (``WATCH`` ranges) in program order.

Entry points
------------
* ``run(stmt)``: ScriptStep (VA 0x95E60) on ONE statement, from its entry to the loop
  continue (VA 0x95EB0) or the yield/return (VA 0x9B516) -- same scheme as
  ``tsuki.misc_vm_variables_errata.VarEmu`` (the variables work), re-implemented here
  with sound-object traps and float support;
* ``call(va, rdi=..., xmm0=...)``: any function (per-frame update 0xAD80, voice update
  0x9220, BGM start 0x98C0, se_nam lookup 0xCC790, ...).

Memory persists between calls (so a statement followed by N frame updates behaves like the
game); ``reset()`` restores the pristine data image.
"""
from __future__ import annotations

import re
import struct
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Dict, List, Optional, Sequence, Tuple, Union

from .misc_audio_runtime_semantics_vex import VexCpu, b32, f32

TEXT_OFF, TEXT_LEN = 0x2280, 0x329DC8
RELRO_OFF, RELRO_LEN, RELRO_VA = 0x32C210, 0x35ED0, 0x400000
DATA_OFF, DATA_LEN, DATA_VA, DATA_MEMSZ = 0x365FA0, 0x7D66E0, 0x600000, 0x1A85790

STK_LO, STK_HI = 0x7F000000, 0x7F100000
HEAP_LO, HEAP_HI = 0x7E000000, 0x7E800000
BUF = 0x20000000                     # statement buffer (64 KiB)
RET = 0x7EF00000                     # return trap
SE_NAM = 0x7B000000                  # se_nam.mrg sections (boot())
HIST = 0x7C000000                    # history ring (0x3A10-byte records), see reset()
HIST_SIZE = 0x3A10 * 8
FAKE_OBJ = 0x7D000000                # fake nexl_Sound object
FAKE_VT = 0x7D000100                 # its vtable (64 entries)
TRAP = 0x7D001000                    # method i lives at TRAP + 8*i
SOUND_PTR = 0xDD9028

MAIN_ENTRY, MAIN_GO, MAIN_STOPS = 0x95E60, 0x95F4E, (0x95EB0, 0x9B516)
PLT_LO, PLT_HI = 0x1734A0, 0x174980
# nexl_Sound / waveout / ATRAC9 / HE-VAG library code: a direct call from game code into
# this range is trapped and logged like a vtable call (method = the entry VA)
LIB_LO, LIB_HI = 0x10D000, 0x11A000

# sound globals whose writes are logged (start, end)
WATCH: Tuple[Tuple[int, int], ...] = (
    (0x624300, 0x624400),            # BGM track id 0x62437C, BGM loop flags 0x624380
    (0xDD86A0, 0xDD9040),            # BGM / voice / SE channel records, slot, flags
    (0x1654200, 0x1654600),          # script-side audio state (ids, volumes, tags, name)
    (0x164D5C0, 0x164D800),          # music-heard bits
    (0x17DA268, 0x17DA2A0),          # sound volume block
    (0x174DE00, 0x174DE60),          # script flags (keep-BGM, skip)
)

# names of the nexl_Sound methods by vtable offset (from the method bodies, VA 0x10EAA0..)
METHOD_NAMES: Dict[int, str] = {
    0x28: "open_file", 0x30: "open_mem", 0x38: "close", 0x50: "play", 0x58: "is_paused",
    0x70: "stop", 0x78: "is_playing", 0x88: "set_flag6", 0xA8: "set_volume", 0xB8: "set_pan",
    0xC8: "position",
    0x10FC90: "set_loop", 0x10FFF0: "set_start_sample", 0x10F8A0: "set_fade_in",
    0x110020: "register_vag", 0x110280: "lib_110280",
}

LIBC: Dict[int, str] = {
    0x1734A0: "delete", 0x1739F0: "delete", 0x173660: "delete",
    0x1734B0: "stack_chk_fail",
    0x1734C0: "new", 0x1737E0: "new", 0x173680: "new",
    0x1734D0: "memmove", 0x173650: "memmove",
    0x1734F0: "memset",
    0x1735C0: "strchr", 0x1735D0: "strpbrk", 0x1735E0: "strtol",
    0x173600: "strlen", 0x173610: "strncmp", 0x173620: "strcmp", 0x173760: "memcmp",
    0x173780: "strcpy", 0x1737B0: "strncpy", 0x174340: "strrchr",
    0x1735A0: "ldexp", 0x173570: "rand", 0x173550: "powf", 0x1736F0: "pow",
    0x173590: "sprintf", 0x173640: "snprintf",
    0x1736B0: "log2f", 0x1736C0: "ldexpf", 0x173560: "sinf", 0x173690: "cosf",
}


@dataclass
class SoundCall:
    method: int
    stream: int
    rdx: int
    rcx: int
    r8: int
    r9: int
    xmm0: float
    xmm1: float
    caller: int

    @property
    def name(self) -> str:
        return METHOD_NAMES.get(self.method, f"m{self.method:#x}")

    def short(self) -> str:
        return (f"{self.name}(s={self.stream}, rdx={self.rdx:#x}, rcx={self.rcx}, r8={self.r8}, "
                f"x0={self.xmm0:.6g}, x1={self.xmm1:.6g})")


@dataclass
class RunResult:
    writes: List[Tuple[int, int, int]] = field(default_factory=list)   # (va, size, value)
    write_pcs: List[int] = field(default_factory=list)                 # PC of each write
    calls: List[SoundCall] = field(default_factory=list)
    stubs: List[Tuple[int, Tuple[int, ...]]] = field(default_factory=list)   # (va, (rdi, rsi, rdx))
    imports: List[Tuple[int, int, int, int, int]] = field(default_factory=list)  # (plt, rdi, rsi, rdx, caller)
    end: Optional[int] = None
    err: Optional[str] = None
    rax: int = 0
    xmm0: float = 0.0
    hits: List[int] = field(default_factory=list)
    steps: int = 0

    def final(self) -> Dict[int, Tuple[int, int]]:
        """Last value written per (va) -> (size, value)."""
        out: Dict[int, Tuple[int, int]] = {}
        for va, size, v in self.writes:
            out[va] = (size, v)
        return out


StubFn = Callable[["SoundEmu"], int]

# Archive accessors replaced by deterministic fakes, so a play call's offset argument
# names the entry: bgm.mrg byte offset of entry n (VA 0xC040, slot 0x1A) -> BGM_FAKE + n *
# 0x1000; vse.mrg offset of entry n (VA 0xC080, slot 0x1B) -> VSE_FAKE + n * 0x1000;
# vse.mrg file handle (VA 0xC0C0, slot 0x1C) -> VSE_HANDLE.
BGM_FAKE, VSE_FAKE, VSE_HANDLE = 0x40000000, 0x50000000, 0x5C5C5C5C


def _fake_offset(base: int) -> "StubFn":
    def f(emu: "SoundEmu") -> int:
        n = emu.reg("RDI") & 0xFFFF
        n = n - 0x10000 if n & 0x8000 else n
        return (base + n * 0x1000) & 0xFFFFFFFFFFFFFFFF
    return f


DEFAULT_STUBS: Dict[int, Union[int, StubFn]] = {
    0xC040: _fake_offset(BGM_FAKE), 0xC080: _fake_offset(VSE_FAKE), 0xC0C0: VSE_HANDLE,
}


def fake_entry(offset: int) -> Tuple[str, int]:
    """Inverse of the fake accessors: ('bgm'|'vse', entry index)."""
    for name, base in (("bgm", BGM_FAKE), ("vse", VSE_FAKE)):
        k = offset - base
        if -0x8000 * 0x1000 <= k < 0x8000 * 0x1000 and k % 0x1000 == 0:
            return name, k // 0x1000
    return "?", offset


class SoundEmu:
    def __init__(self, eboot: Union[str, Path]) -> None:
        import capstone
        from unicorn import (UC_ARCH_X86, UC_HOOK_CODE, UC_HOOK_INSN_INVALID, UC_HOOK_MEM_WRITE,
                             UC_MODE_64, Uc)
        d = Path(eboot).read_bytes()
        self.uc = uc = Uc(UC_ARCH_X86, UC_MODE_64)
        uc.mem_map(0, 0x440000)
        uc.mem_write(0, d[TEXT_OFF:TEXT_OFF + TEXT_LEN])
        uc.mem_write(RELRO_VA, d[RELRO_OFF:RELRO_OFF + RELRO_LEN])
        size = (DATA_MEMSZ + 0xFFFF) & ~0xFFFF
        uc.mem_map(DATA_VA, size)
        uc.mem_write(DATA_VA, d[DATA_OFF:DATA_OFF + DATA_LEN])
        self._apply_relocs(Path(eboot))
        self._data_size = size
        uc.mem_map(STK_LO, STK_HI - STK_LO)
        uc.mem_map(HEAP_LO, HEAP_HI - HEAP_LO)
        uc.mem_map(BUF, 0x10000)
        uc.mem_map(RET, 0x1000)
        uc.mem_write(RET, b"\xf4")
        uc.mem_map(FAKE_OBJ, 0x10000)
        uc.mem_write(FAKE_OBJ, struct.pack("<Q", FAKE_VT))
        uc.mem_write(FAKE_VT, struct.pack("<64Q", *[TRAP + 8 * i for i in range(64)]))
        uc.mem_write(TRAP, b"\xc3" * 0x200)
        uc.mem_write(SOUND_PTR, struct.pack("<Q", FAKE_OBJ))
        # History_Push (VA 0x7AF90) writes a 0x3A10-byte record at ring[0x120A6E0] +
        # 0x3A10 * index[0x120A6EE]; give it a real buffer so it does not scribble on text.
        uc.mem_map(HIST, (HIST_SIZE + 0xFFFF) & ~0xFFFF)
        uc.mem_write(0x120A6E0, struct.pack("<Q", HIST))
        uc.mem_write(0x120A6EE, struct.pack("<H", 0))
        self._pristine = bytes(uc.mem_read(DATA_VA, size))
        self.booted = False
        self._se_mapped = False
        self._md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.vex = VexCpu(uc)
        self.vex.on_mem_write = self._host_write
        self._isvex: Dict[int, bool] = {}
        self._heap = HEAP_LO
        self.r = RunResult()
        self.go = True
        self.go_va = -1
        self.stops: Tuple[int, ...] = ()
        self.stubs: Dict[int, Union[int, StubFn]] = dict(DEFAULT_STUBS)
        self.watch: set = set()
        self.playing: Dict[int, bool] = {}       # answer of is_playing per stream
        self.default_playing = True
        self.rand_values: List[int] = []
        self.position: Dict[int, int] = {}
        self._text = d[TEXT_OFF:TEXT_OFF + TEXT_LEN]
        self.vex.code = self._text
        self._vex_addrs = vex_addresses(Path(eboot))
        for va in self._vex_addrs:                # executed in Python via INSN_INVALID
            uc.mem_write(va, b"")
        self._pointed: set = set()
        uc.hook_add(UC_HOOK_CODE, self._h_trap, begin=TRAP, end=TRAP + 0x1FF)
        uc.hook_add(UC_HOOK_CODE, self._h_plt, begin=PLT_LO, end=PLT_HI - 1)
        uc.hook_add(UC_HOOK_CODE, self._h_lib, begin=LIB_LO, end=LIB_HI - 1)
        uc.hook_add(UC_HOOK_INSN_INVALID, self._h_invalid)
        self._hook_points(list(DEFAULT_STUBS) + list(MAIN_STOPS) + [MAIN_GO, RET])
        for lo, hi in WATCH:
            uc.hook_add(UC_HOOK_MEM_WRITE, self._wr, begin=lo, end=hi - 1)

    # -- set-up ----------------------------------------------------------------
    def _apply_relocs(self, eboot: Path) -> None:
        from eboot.selfelf import R_X86_64_RELATIVE, Eboot
        eb = Eboot.open(eboot)
        try:
            for r in eb.rela():
                if r.type == R_X86_64_RELATIVE and (RELRO_VA <= r.offset < RELRO_VA + RELRO_LEN
                                                    or DATA_VA <= r.offset < DATA_VA + DATA_LEN):
                    self.uc.mem_write(r.offset, struct.pack("<Q", r.addend & 0xFFFFFFFFFFFFFFFF))
        finally:
            eb.close()

    def reset(self) -> None:
        """Restore the data image as it was after ``boot()`` (or after loading)."""
        self.uc.mem_write(DATA_VA, self._pristine)
        self.playing = {}
        self.default_playing = True
        self.position = {}

    def boot(self, se_sections: Optional[Sequence[bytes]] = None) -> None:
        """Run the engine's own boot-time set-up that the sound code depends on:
        config defaults (VA 0x3EAC0) and Sound_ApplyConfig(full) (VA 0x3E9B0), which fill
        the volume block 0x17DA268; optionally load the four se_nam.mrg sections (raw
        NAM32 bytes, in file order) and point 0x178DB98/A0/A8/B0 at them like VA 0xCC730.
        The resulting image becomes the state ``reset()`` restores."""
        for va, rdi in ((0x3EAC0, 0), (0x3E9B0, 1)):
            r = self.call(va, rdi=rdi)
            if r.err:
                raise RuntimeError(f"boot step {va:#x} failed: {r.err}")
        if se_sections is not None:
            base = SE_NAM
            if not self._se_mapped:
                self.uc.mem_map(SE_NAM, 0x100000)
                self._se_mapped = True
            for k, sec in enumerate(se_sections):
                self.wr(base, sec + bytes(64))
                self.put_u64(0x178DB98 + 8 * k, base)
                base = (base + len(sec) + 64 + 0xFFF) & ~0xFFF
        self._pristine = bytes(self.uc.mem_read(DATA_VA, self._data_size))
        self.booted = True

    # -- registers / memory ----------------------------------------------------
    def reg(self, name: str) -> int:
        from unicorn import x86_const
        return int(self.uc.reg_read(getattr(x86_const, "UC_X86_REG_" + name)))

    def set_reg(self, name: str, v: int) -> None:
        from unicorn import x86_const
        self.uc.reg_write(getattr(x86_const, "UC_X86_REG_" + name), v & (1 << 64) - 1)

    def xmm(self, k: int) -> float:
        return float(f32(self.reg(f"XMM{k}") & 0xFFFFFFFF))

    def set_xmm(self, k: int, v: float) -> None:
        from unicorn import x86_const
        self.uc.reg_write(getattr(x86_const, f"UC_X86_REG_YMM{k}"), b32(v))

    def rd(self, va: int, n: int) -> bytes:
        return bytes(self.uc.mem_read(va, n))

    def wr(self, va: int, b: bytes) -> None:
        self.uc.mem_write(va, b)

    def u8(self, va: int) -> int:
        return self.rd(va, 1)[0]

    def i8(self, va: int) -> int:
        return int(struct.unpack("<b", self.rd(va, 1))[0])

    def u16(self, va: int) -> int:
        return int(struct.unpack("<H", self.rd(va, 2))[0])

    def i16(self, va: int) -> int:
        return int(struct.unpack("<h", self.rd(va, 2))[0])

    def u32(self, va: int) -> int:
        return int(struct.unpack("<I", self.rd(va, 4))[0])

    def i32(self, va: int) -> int:
        return int(struct.unpack("<i", self.rd(va, 4))[0])

    def u64(self, va: int) -> int:
        return int(struct.unpack("<Q", self.rd(va, 8))[0])

    def f32(self, va: int) -> float:
        return float(struct.unpack("<f", self.rd(va, 4))[0])

    def put_u8(self, va: int, v: int) -> None:
        self.wr(va, bytes([v & 0xFF]))

    def put_u16(self, va: int, v: int) -> None:
        self.wr(va, struct.pack("<H", v & 0xFFFF))

    def put_u32(self, va: int, v: int) -> None:
        self.wr(va, struct.pack("<I", v & 0xFFFFFFFF))

    def put_u64(self, va: int, v: int) -> None:
        self.wr(va, struct.pack("<Q", v & 0xFFFFFFFFFFFFFFFF))

    def put_f32(self, va: int, v: float) -> None:
        self.wr(va, struct.pack("<f", v))

    def cstr(self, va: int, limit: int = 0x1000) -> bytes:
        out = bytearray()
        while len(out) < limit:
            c = self.uc.mem_read(va + len(out), 1)[0]
            if c == 0:
                break
            out.append(c)
        return bytes(out)

    def alloc(self, n: int, align: int = 16) -> int:
        a = (self._heap + align - 1) & ~(align - 1)
        self._heap = a + max(n, 16)
        if self._heap > HEAP_HI:
            raise MemoryError("emulator heap exhausted")
        return a

    # -- hooks -------------------------------------------------------------------
    def _ret(self, val: int) -> None:
        rsp = self.reg("RSP")
        self.set_reg("RIP", struct.unpack("<Q", self.rd(rsp, 8))[0])
        self.set_reg("RSP", rsp + 8)
        self.set_reg("RAX", val)

    def _point(self, uc: object, a: int, size: int, _: object) -> None:
        """Hook on single addresses: stops, the 'go' address, stubs, watched addresses."""
        if self.watch and a in self.watch:
            self.r.hits.append(a)
        if self.go and a in self.stops:
            self.r.end = a
            self.uc.emu_stop()
            return
        st = self.stubs.get(a)
        if st is not None:
            self.r.stubs.append((a, (self.reg("RDI"), self.reg("RSI"), self.reg("RDX"))))
            self._ret(st(self) if callable(st) else st)
            return
        if a == self.go_va:
            self.go = True

    def _hook_points(self, addrs: "Sequence[int]") -> None:
        from unicorn import UC_HOOK_CODE
        for a in addrs:
            if a >= 0 and a not in self._pointed:
                self.uc.hook_add(UC_HOOK_CODE, self._point, begin=a, end=a)
                self._pointed.add(a)

    def _h_trap(self, uc: object, a: int, size: int, _: object) -> None:
        self._method((a - TRAP) // 8 * 8)

    def _h_plt(self, uc: object, a: int, size: int, _: object) -> None:
        rsp = self.reg("RSP")
        self.r.imports.append((a, self.reg("RDI"), self.reg("RSI"), self.reg("RDX"),
                               struct.unpack("<Q", self.rd(rsp, 8))[0]))
        self._ret(self._libc(LIBC.get(a)))

    def _h_lib(self, uc: object, a: int, size: int, _: object) -> None:
        self._method(a)                           # direct call into the nexl sound library

    def _h_invalid(self, uc: object, _: object) -> bool:
        a = self.reg("RIP")
        if a not in self._vex_addrs:
            return False                          # a real invalid instruction: stop
        n = self.vex.execute(a)
        self.set_reg("RIP", a + n)
        return True

    def _wr(self, uc: object, acc: int, a: int, size: int, v: int, _: object) -> None:
        if self.go:
            self.r.writes.append((a, size, v & ((1 << (8 * size)) - 1)))
            self.r.write_pcs.append(self.reg("RIP"))

    def _host_write(self, a: int, size: int, v: int) -> None:
        """A store done in Python (VEX instruction): log it like a guest store, split into
        8-byte pieces when it is wider."""
        if not self.go:
            return
        for lo, hi in WATCH:
            if a < hi and a + size > lo:
                for k in range(0, size, 8):
                    n = min(8, size - k)
                    self.r.writes.append((a + k, n, (v >> (8 * k)) & ((1 << (8 * n)) - 1)))
                    self.r.write_pcs.append(self.reg("RIP"))
                return

    def _method(self, off: int) -> None:
        rsp = self.reg("RSP")
        caller = struct.unpack("<Q", self.rd(rsp, 8))[0]
        c = SoundCall(off, self.reg("RSI") & 0xFFFFFFFF, self.reg("RDX"), self.reg("RCX"),
                      self.reg("R8"), self.reg("R9"), self.xmm(0), self.xmm(1), caller - 6)
        if c.stream >= 0x80000000:
            c.stream -= 1 << 32
        self.r.calls.append(c)
        self._ret(self.answer(c))

    def answer(self, c: SoundCall) -> int:
        """Return value of a fake nexl_Sound method."""
        if c.method == 0x78:                                  # is_playing
            return int(self.playing.get(c.stream, self.default_playing))
        if c.method == 0x38:                                  # close: succeeded
            return 1
        if c.method == 0x50:                                  # play
            self.playing[c.stream] = True
            return 1
        if c.method == 0xC8:
            return self.position.get(c.stream, 0)
        return 0

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
            return len(self.cstr(rdi, 0x10000))
        if name == "strcpy":
            s = self.cstr(rsi, 0x10000)
            uc.mem_write(rdi, s + b"\0")
            return rdi
        if name == "strncpy":
            s = self.cstr(rsi, rdx)[:rdx]
            uc.mem_write(rdi, s + b"\0" * (rdx - len(s)))
            return rdi
        if name in ("strcmp", "strncmp", "memcmp"):
            if name == "memcmp":
                a, b = self.rd(rdi, rdx), self.rd(rsi, rdx)
            else:
                lim = rdx if name == "strncmp" else 0x10000
                a, b = self.cstr(rdi, lim)[:lim], self.cstr(rsi, lim)[:lim]
            return 0 if a == b else (1 if a > b else (1 << 64) - 1)
        if name in ("strchr", "strrchr"):
            s = self.cstr(rdi, 0x10000)
            ch = rsi & 0xFF
            if ch == 0:
                return rdi + len(s)
            k = s.find(bytes([ch])) if name == "strchr" else s.rfind(bytes([ch]))
            return 0 if k < 0 else rdi + k
        if name == "strpbrk":
            text, acc = self.cstr(rdi, 0x10000), self.cstr(rsi)
            for k, ch2 in enumerate(text):
                if ch2 in acc:
                    return rdi + k
            return 0
        if name == "strtol":
            txt = self.cstr(rdi, 4096)
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
            return self.alloc(rdi)
        if name == "delete":
            return 0
        if name == "ldexp":
            import math
            from unicorn import x86_const
            x = struct.unpack("<d", struct.pack("<Q", self.reg("XMM0") & 0xFFFFFFFFFFFFFFFF))[0]
            e = rdi & 0xFFFFFFFF
            e = e - (1 << 32) if e >> 31 else e
            r = math.ldexp(x, e)
            self.uc.reg_write(x86_const.UC_X86_REG_YMM0, struct.unpack("<Q", struct.pack("<d", r))[0])
            return 0
        if name in ("log2f", "ldexpf", "sinf", "cosf", "powf"):
            import math
            from unicorn import x86_const
            x = float(f32(self.reg("XMM0") & 0xFFFFFFFF))
            if name == "log2f":
                r = math.log2(x) if x > 0 else (-math.inf if x == 0 else math.nan)
            elif name == "ldexpf":
                e = rdi & 0xFFFFFFFF
                r = math.ldexp(x, e - (1 << 32) if e >> 31 else e)
            elif name == "sinf":
                r = math.sin(x)
            elif name == "cosf":
                r = math.cos(x)
            else:
                r = math.pow(x, float(f32(self.reg("XMM1") & 0xFFFFFFFF)))
            self.uc.reg_write(x86_const.UC_X86_REG_YMM0, b32(r))
            return 0
        if name == "rand":
            return self.rand_values.pop(0) if self.rand_values else 0
        if name == "stack_chk_fail":
            raise RuntimeError("__stack_chk_fail")
        return 0

    # -- running -----------------------------------------------------------------
    def _start(self, entry: int, count: int) -> None:
        from unicorn import UcError
        self._hook_points(list(self.stops) + list(self.stubs) + list(self.watch) + [self.go_va])
        try:
            self.uc.emu_start(entry, RET, count=count)
        except UcError as e:
            self.r.err = f"{e} at {self.reg('RIP'):#x}"
        except (NotImplementedError, RuntimeError) as e:
            self.r.err = f"{type(e).__name__}: {e} at {self.reg('RIP'):#x}"
        self.r.rax = self.reg("RAX")
        self.r.xmm0 = self.xmm(0)

    def _frame(self) -> None:
        self.r = RunResult()
        self._heap = HEAP_LO
        self.set_reg("RSP", STK_HI - 0x8000)
        self.set_reg("RBP", STK_HI - 0x7000)
        self.wr(STK_HI - 0x8000, struct.pack("<Q", RET))

    def run(self, stmt: Union[str, bytes], count: int = 2_000_000,
            stops: Sequence[int] = ()) -> RunResult:
        """ScriptStep on the first statement of ``stmt`` (e.g. ``"_MPLY(5,,`017:80);"``)."""
        b = stmt.encode("utf-8") if isinstance(stmt, str) else stmt
        self._frame()
        self.wr(BUF, bytes(0x2000))
        self.wr(BUF, b + b"\0")
        # keep History_Push inside the fake ring (its index advances on every push)
        self.wr(0x120A6EC, bytes(4))
        self.go = False
        self.go_va = MAIN_GO
        self.stops = tuple(MAIN_STOPS) + tuple(stops)
        self.stubs = dict(DEFAULT_STUBS)
        self.set_reg("RDI", BUF)
        self._start(MAIN_ENTRY, count)
        return self.r

    def call(self, va: int, count: int = 2_000_000, stops: Sequence[int] = (),
             xmm: Optional[Dict[int, float]] = None, **regs: int) -> RunResult:
        """Call the function at ``va`` with integer registers ``regs`` (rdi=..)."""
        self._frame()
        self.go = True
        self.go_va = -1
        self.stops = (RET,) + tuple(stops)
        self.stubs = dict(DEFAULT_STUBS)
        for k, v in regs.items():
            self.set_reg(k.upper(), v)
        for xk, fv in (xmm or {}).items():
            self.set_xmm(xk, fv)
        self._start(va, count)
        return self.r


_VEX_CACHE: Dict[str, Tuple[int, ...]] = {}


def vex_addresses(eboot: Path) -> Tuple[int, ...]:
    """VAs of every VEX-encoded instruction (first byte 0xC4/0xC5) of every function of
    the text segment (function list from .eh_frame_hdr, eboot.disasm.Index)."""
    key = str(Path(eboot).resolve())
    hit = _VEX_CACHE.get(key)
    if hit is not None:
        return hit
    from eboot.disasm import Index
    from eboot.selfelf import Eboot
    eb = Eboot.open(Path(eboot))
    try:
        ix = Index.build(eb)
        text = eb.read_va(0, TEXT_LEN)
        out = sorted({i.addr for f in ix.funcs for i in ix.insns.get(f, [])
                      if 0 <= i.addr < TEXT_LEN and text[i.addr] in (0xC4, 0xC5)})
    finally:
        eb.close()
    _VEX_CACHE[key] = tuple(out)
    return _VEX_CACHE[key]


# ---------------------------------------------------------------------------
# state snapshots
# ---------------------------------------------------------------------------
BGM_REC, VOICE_REC, SE_REC, REC_SIZE = 0xDD86A0, 0xDD8B20, 0xDD8710, 0x34


def stream_record(stream: int) -> int:
    """Address of the channel record that drives sound stream ``stream``:
    streams 0-1 = BGM slots, 2-5 = voice-type streams, 6-25 = script SE channels 0-19."""
    if 0 <= stream < 2:
        return BGM_REC + REC_SIZE * stream
    if 2 <= stream < 6:
        return VOICE_REC + REC_SIZE * (stream - 2)
    if 6 <= stream < 26:
        return SE_REC + REC_SIZE * (stream - 6)
    raise ValueError(stream)


def decode_record(b: bytes) -> Dict[str, object]:
    st, kind, a, idx = struct.unpack_from("<IIHH", b, 0)
    f = struct.unpack_from("<9f", b, 0xC)
    return {"state": st, "kind": kind, "w8": a, "wA": idx,
            "vol": f[0], "vol_to": f[1], "vol_rate": f[2],
            "pan": f[3], "pan_to": f[4], "pan_rate": f[5],
            "p3": f[6], "p3_to": f[7], "p3_rate": f[8], "char": b[0x30]}


def snapshot(emu: SoundEmu) -> Dict[str, object]:
    """Decoded sound state (records of streams 0..25 plus the globals the handlers use)."""
    out: Dict[str, object] = {}
    for s in range(26):
        out[f"rec{s}"] = decode_record(emu.rd(stream_record(s), REC_SIZE))
    out["bgm_slot"] = emu.u32(0xDD8F28)
    out["bgm_track"] = emu.i32(0x62437C)
    out["bgm_loop"] = (emu.i32(0x624380), emu.i32(0x624384))
    out["voice_stream"] = emu.u32(0xDD8F08)
    out["voice_state"] = emu.u16(0xDD8E50)
    out["vol_block"] = emu.rd(0x17DA268, 0x10).hex()
    out["scr_track"] = emu.i16(0x16545D0)
    out["scr_bgm_vol"] = emu.i16(0x1654238)
    for name, va, n, fmt in (("scr_se_id", 0x1654210, 20, "h"), ("scr_se_name", 0x1654240, 20, "h"),
                             ("scr_se_vol", 0x1654270, 20, "h"), ("scr_se_loop", 0x16542A0, 20, "h"),
                             ("scr_se_pan", 0x16542D0, 20, "h"), ("scr_se_char", 0x1654330, 20, "b")):
        out[name] = list(struct.unpack(f"<{n}{fmt}", emu.rd(va, n * struct.calcsize(fmt))))
    for ch in range(20):
        q = emu.rd(DEFER_QUEUE + 0x18 * ch, 0x18)
        out[f"defer{ch}"] = q.hex()
    return out


DEFER_QUEUE = 0x16543F0          # 20 x 0x18-byte deferred SE commands (`068 delay)


def run_voice_start(emu: SoundEmu, pending: int, char: int, r15: int = 0) -> Tuple[Optional[int], int, RunResult]:
    """Execute the voice-start block of the _ZM handler (VA 0x9281E..0x92902) with
    pending voice ``pending`` (0x16541FC) and character ``char`` (0x164FCF6); the voice
    play request 0xC3A80 is stubbed.  Returns (entry passed to 0xC3A80 or None, the voice
    volume byte +0xD of 0x17DA268 afterwards, run result)."""
    emu.put_u32(0x16541FC, pending)
    emu.put_u16(0x164FCF6, char)
    emu.stubs[0xC3A80] = 0
    emu._frame()
    emu.go = True
    emu.go_va = -1
    emu.stops = (0x92902, RET)
    emu.set_reg("R13", 0x66C578)
    emu.set_reg("R12", 0x17DA268)
    emu.set_reg("R15", r15)
    emu.set_reg("R14", 0)
    emu._start(0x9281E, 100_000)
    r = emu.r
    played = [args[0] for a, args in r.stubs if a == 0xC3A80]
    del emu.stubs[0xC3A80]
    return (played[0] if played else None), emu.u8(0x17DA275), r


def run_duck_step(emu: SoundEmu, voice_state: int) -> RunResult:
    """Execute the BGM-ducking block of the ADV frame (VA 0x694B9..0x69564) once with the
    voice state word 0xDD8E50 = ``voice_state`` (0 = no voice)."""
    emu.put_u16(0xDD8E50, voice_state)
    emu._frame()
    emu.go = True
    emu.go_va = -1
    emu.stops = (0x69564, RET)
    emu._start(0x694B9, 100_000)
    return emu.r
