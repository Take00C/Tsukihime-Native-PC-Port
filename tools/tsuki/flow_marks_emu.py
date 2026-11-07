"""The flowchart marks of the story run under unicorn: what the PS4 engine writes into var_F / var_G at a
scene ``_JUMP`` and at a ``_SELD`` (docs/formats/gallery.md 5.3, package K44).

Functions run (CUSA47999 v01.01, text VA = file offset - 0x2280):

    0xB8870  node of a scene id (a switch over ids 240..2015; 1..9999 else -1)
    0xBA6A9  the body of 0xBA690 after its two lookups: 0xB9460(from, to) (the special pairs, a
             500-case switch) and, when it returns 0, ``to > 0`` and ``from`` is 1..530,
             0xBA3C0(from, to) (the edge table; F110 = to)
    0xB8620  the edge flags of choice id 1..82 (text 0x2214E0) and the cases 57, 67, 72, 73

The trophy check 0xB7CC0 that 0xB85A0 (mark a pseudo scene) and one case tail-call is replaced by a
``ret`` (it reads trophy state only; the port has no trophies) and counted.  The last choice's cursor
(word 0x17506A6, written by the choice code at 0xA6690 / 0xA7C05) is an input of the jump cases.

    python -m tsuki.flow_marks_emu        (needs eboot.bin, unicorn; a short summary)
"""
from __future__ import annotations

import random
import struct
import sys
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Tuple, Union

TEXT_OFF, TEXT_LEN = 0x2280, 0x329DC8
DATA_OFF, DATA_LEN, DATA_VA, DATA_MEMSZ = 0x365FA0, 0x7D66E0, 0x600000, 0x1A85790
F_VA = 0x164B1D0                 # var_F u16[1200], then var_Z u16[1400], then var_G u16[2000]
G_VA = 0x164C620
PICK_VA = 0x17506A6              # the cursor of the last decided choice
NODE_OF_ID = 0xB8870
JUMP_BODY = 0xBA6A9              # 0xBA690 after "ebx = from; eax = to"
CHOICE = 0xB8620
SPECIAL = 0xB9460
TROPHY = 0xB7CC0
F_WORDS, G_WORDS = 1200, 500
STK_LO, STK_HI = 0x7F000000, 0x7F100000
RET = 0x7EF00000


class FlowMarkEmu:
    """The three functions on one unicorn instance; every run starts from the F / G words it is given."""

    def __init__(self, eboot: Union[str, Path]) -> None:
        from unicorn import UC_ARCH_X86, UC_HOOK_CODE, UC_MODE_64, Uc
        d = Path(eboot).read_bytes()
        self.uc = uc = Uc(UC_ARCH_X86, UC_MODE_64)
        uc.mem_map(0, 0x340000)
        uc.mem_write(0, d[TEXT_OFF:TEXT_OFF + TEXT_LEN])
        uc.mem_map(DATA_VA, (DATA_MEMSZ + 0xFFFF) & ~0xFFFF)
        uc.mem_write(DATA_VA, d[DATA_OFF:DATA_OFF + DATA_LEN])
        uc.mem_map(STK_LO, STK_HI - STK_LO)
        uc.mem_map(RET, 0x1000)
        uc.mem_write(TROPHY, b"\xC3")
        self.trophy_checks = 0
        self.special_result: Optional[int] = None
        uc.hook_add(UC_HOOK_CODE, self._code, begin=TROPHY, end=TROPHY)
        uc.hook_add(UC_HOOK_CODE, self._after_special, begin=0xBA6B5, end=0xBA6B5)

    def _reg(self, name: str) -> int:
        from unicorn import x86_const
        return int(self.uc.reg_read(getattr(x86_const, "UC_X86_REG_" + name)))

    def _set(self, name: str, v: int) -> None:
        from unicorn import x86_const
        self.uc.reg_write(getattr(x86_const, "UC_X86_REG_" + name), v & (1 << 64) - 1)

    def _code(self, uc: object, a: int, size: int, _: object) -> None:
        self.trophy_checks += 1

    def _after_special(self, uc: object, a: int, size: int, _: object) -> None:
        self.special_result = self._reg("RAX") & 0xFF

    def _call(self, va: int, rsp_words: Sequence[int] = (), **regs: int) -> int:
        rsp = STK_HI - 0x1000
        words = list(rsp_words) + [RET]
        self.uc.mem_write(rsp, struct.pack(f"<{len(words)}Q", *words))
        self._set("RSP", rsp)
        self._set("RBP", 0)
        for k, v in regs.items():
            self._set(k, v)
        self.uc.emu_start(va, RET, count=200_000)
        if self._reg("RIP") != RET:
            raise RuntimeError(f"emulation of 0x{va:x} stopped at 0x{self._reg('RIP'):x}")
        return self._reg("RAX") & 0xFFFFFFFF

    def node_of_scene_id(self, scene_id: int) -> int:
        """0xB8870(scene_id) as a signed int."""
        v = self._call(NODE_OF_ID, RDI=scene_id & 0xFFFFFFFF)
        return v - (1 << 32) if v & 0x80000000 else v

    def _load(self, f: Sequence[int], g: Sequence[int], pick: int) -> None:
        self.uc.mem_write(F_VA, struct.pack(f"<{F_WORDS}H", *f))
        self.uc.mem_write(G_VA, struct.pack(f"<{G_WORDS}H", *g))
        self.uc.mem_write(PICK_VA, struct.pack("<H", pick & 0xFFFF))
        self.trophy_checks = 0
        self.special_result = None

    def _state(self) -> Tuple[List[int], List[int]]:
        f = list(struct.unpack(f"<{F_WORDS}H", bytes(self.uc.mem_read(F_VA, 2 * F_WORDS))))
        g = list(struct.unpack(f"<{G_WORDS}H", bytes(self.uc.mem_read(G_VA, 2 * G_WORDS))))
        return f, g

    def jump(self, from_node: int, to_node: int, f: Sequence[int], g: Sequence[int], pick: int = 0) -> Tuple[List[int], List[int]]:
        """0xBA690 with its lookups already done: the F and G words after the marks of ``from -> to``."""
        self._load(f, g, pick)
        # the frame 0xBA690 built: push rbp; push r14; push rbx (popped by its epilogue)
        self._call(JUMP_BODY, rsp_words=(0, 0, 0), RBX=from_node & 0xFFFFFFFF, RAX=to_node & 0xFFFFFFFF)
        return self._state()

    def choice(self, choice_id: int, f: Sequence[int], g: Sequence[int]) -> Tuple[List[int], List[int]]:
        """0xB8620(choice_id): the F and G words after the choice marks."""
        self._load(f, g, 0)
        self._call(CHOICE, RDI=choice_id & 0xFFFFFFFF)
        return self._state()


def special_constants() -> Dict[int, List[int]]:
    """For each ``from`` node of 0xB9460, the ``to`` constants its case compares (read from the code), so a sweep
    reaches every branch."""
    import capstone
    from eboot import eboot_path
    d = Path(eboot_path()).read_bytes()
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    base = 0x220C24
    targets: Dict[int, List[int]] = {}
    for i in range(500):
        off = struct.unpack_from("<i", d, base - 0 + TEXT_OFF + 4 * i)[0]
        targets.setdefault(base + off, []).append(i + 0x18)
    code = d[SPECIAL + TEXT_OFF:0xBA3C0 + TEXT_OFF]
    ins = list(md.disasm(code, SPECIAL))
    # the shared default ("to == 486": the prologue) and the empty cases need no constants of their own
    for plain in (0xB94F3, 0xBA3AB):
        targets.pop(plain, None)
    out: Dict[int, List[int]] = {}
    consts = set()
    for k in ins:
        if k.mnemonic == "cmp" and k.op_str.startswith("esi, "):
            consts.add(int(k.op_str.split(", ")[1], 0))
    for k in ins:
        if k.mnemonic == "add" and k.op_str.startswith("esi, "):
            v = int(k.op_str.split(", ")[1], 0)
            v = v - (1 << 32) if v & 0x80000000 else v
            consts.update({-v, -v + 1, -v + 2})
    for t, froms in targets.items():
        for fr in froms:
            out[fr] = sorted(consts)
    for fr in (905, 1196):
        out[fr] = sorted(consts)
    return out


# ---------------------------------------------------------------------------------------------
# the F words a case reads: random states set these (F24, F50, F54 and the per-save node / edge bytes)
# ---------------------------------------------------------------------------------------------
STATE_WORDS = [24, 50, 54, 110] + list(range(201, 273)) + list(range(600, 688))


def random_f(rng: random.Random, density: float) -> List[int]:
    f = [0] * F_WORDS
    for w in STATE_WORDS:
        if w in (24, 50, 54):
            f[w] = rng.choice([0, 0, 1, 2]) if rng.random() < density else 0
        elif w == 110:
            f[w] = rng.randrange(0, 600)
        else:
            f[w] = rng.randrange(0, 256) if rng.random() < density else 0
    return f


def summary() -> int:
    from eboot import eboot_path
    p = eboot_path()
    if p is None:
        print("eboot.bin not found", file=sys.stderr)
        return 1
    emu = FlowMarkEmu(p)
    ids = [emu.node_of_scene_id(i) for i in range(0, 2100)]
    print("scene ids with a node:", sum(1 for v in ids if v >= 0))
    z = [0] * F_WORDS
    f, g = emu.jump(51, 68, z, [0] * G_WORDS)
    print("51 -> 68: F110 =", f[110], "special", emu.special_result)
    f, g = emu.jump(68, 67, z, [0] * G_WORDS)
    print("68 -> 67: F110 =", f[110], "special", emu.special_result)
    return 0


if __name__ == "__main__":
    sys.exit(summary())
